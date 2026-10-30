using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// ระบบจัดการ Subscription และ Trial อย่างละเอียด
/// </summary>
public class SubscriptionService : ISubscriptionService
{
    private readonly AccountingDbContext _db;
    private readonly INotificationService _notificationService;
    private readonly INotificationEngine? _notify;
    private readonly ISaasBillingDocumentService? _billing;
    private readonly ILogger<SubscriptionService>? _logger;

    public SubscriptionService(AccountingDbContext db, INotificationService notificationService,
        INotificationEngine? notify = null, ISaasBillingDocumentService? billing = null,
        ISubscriptionGateShadowLog? shadowLog = null, ILogger<SubscriptionService>? logger = null,
        IPlatformDunningService? dunning = null)
    {
        _dunning = dunning;
        _db = db;
        _logger = logger;
        _notificationService = notificationService;
        _notify = notify;
        _billing = billing;
        _shadowLog = shadowLog;
    }

    /// <summary>รอบ 201 ทีม PL (C-3): ที่บันทึก "จะถูกปิด" ของด่านเจ้าของปิดฟีเจอร์ระดับ service (โหมดเงา) · null ในเทสต์ที่สร้างเอง</summary>
    private readonly ISubscriptionGateShadowLog? _shadowLog;

    /// <summary>รอบ 219 เฟส 2 ขั้น 2: ประเมินค้างชำระใหม่หลังอนุมัติการชำระ (คืนสถานะที่ระบบระงับเพราะค้างชำระทันที) · null ในเทสต์ที่สร้างเอง</summary>
    private readonly IPlatformDunningService? _dunning;

    /// <summary>duplicate audit #7 phase 2: route subscription notification ผ่าน
    /// NotificationEngine (production) → กลับไป NotificationService legacy
    /// ถ้าไม่ inject (test fixture). พอ phase 2 ครบจะลบ INotificationService dep ได้</summary>
    private async Task NotifyUserAsync(Guid recipientUserId, Guid? companyId,
        string eventKey, Models.Enums.NotificationType type, string title, string message,
        string? actionUrl = null, string? entityType = null, Guid? entityId = null)
    {
        if (_notify != null && companyId.HasValue)
        {
            await _notify.DispatchAsync(companyId.Value, eventKey, new NotificationContext
            {
                Title = title, Message = message, ActionUrl = actionUrl,
                EntityType = entityType, EntityId = entityId,
                BellType = type, RecipientUserId = recipientUserId,
            });
        }
        else
        {
            await _notificationService.SendAsync(recipientUserId, companyId, type,
                title, message, actionUrl, entityType, entityId);
        }
    }

    // ==================== Trial Management ====================

    public async Task<TrialStatusResponse> StartTrialAsync(StartTrialRequest request, string performedBy)
    {
        var company = await _db.Companies.FindAsync(request.CompanyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        var existing = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == request.CompanyId);
        if (existing != null)
            throw new InvalidOperationException("บริษัทนี้มี subscription อยู่แล้ว");

        // แถว soft-deleted ค้าง: global filter มองไม่เห็น แต่ unique index บน
        // CompanyId ยังครองคีย์อยู่ → insert ใหม่จะ DbUpdateException เสมอ
        // (แล้ว stub fallback ของ caller ก็ล้มซ้ำเพราะ tracker ค้าง entity เดิม)
        // = บริษัทติดสภาพไร้ subscription ถาวร. ทางแก้ที่ถูก: restore แถวเดิม
        // แล้ว resync จาก template เหมือนเริ่มใหม่
        var softDeleted = await _db.Subscriptions.IgnoreQueryFilters()
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == request.CompanyId && s.IsDeleted);
        if (softDeleted != null)
        {
            softDeleted.IsDeleted = false;
            softDeleted.Plan = request.TargetPlan;
            softDeleted.Status = SubscriptionStatus.Trial;   // จะถูก override ด้านล่างถ้าฟรีถาวร
            await ResyncZeroLimitsFromTemplateAsync(softDeleted);
            // เดินต่อด้วยแถวเดิมแทน insert — sync สถานะ/วันที่ตาม template
            var tpl = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == request.TargetPlan && p.IsActive);
            var permFree = tpl?.IsPermanentFree ?? false;
            var days = permFree ? 36500 : (request.TrialDays ?? tpl?.TrialDurationDays ?? 30);
            softDeleted.Status = permFree ? SubscriptionStatus.Active : SubscriptionStatus.Trial;
            softDeleted.IsPermanentFree = permFree;
            softDeleted.StartDate = DateTime.UtcNow;
            softDeleted.EndDate = DateTime.UtcNow.AddDays(days);
            // GetTrialStatusAsync ต้องมี TrialConfig — แถวเก่าอาจไม่มี/ถูกลบตาม
            if (softDeleted.TrialConfig == null)
            {
                _db.TrialConfigs.Add(new TrialConfig
                {
                    SubscriptionId = softDeleted.Id,
                    TrialStatus = TrialStatus.Active,
                    TrialStartDate = softDeleted.StartDate,
                    TrialEndDate = softDeleted.EndDate,
                    TrialDurationDays = days,
                    TrialMaxUsers = softDeleted.MaxUsers,
                    TrialMaxDocumentsPerMonth = softDeleted.MaxDocumentsPerMonth,
                    TrialMaxJournalEntriesPerMonth = softDeleted.MaxJournalEntriesPerMonth,
                    TrialMaxCompanies = 1,
                    CreatedBy = performedBy
                });
            }
            else
            {
                softDeleted.TrialConfig.TrialStatus = TrialStatus.Active;
                softDeleted.TrialConfig.TrialEndDate = softDeleted.EndDate;
            }
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = softDeleted.Id,
                Action = "RestoredFromSoftDelete",
                ToPlan = request.TargetPlan,
                ToStatus = softDeleted.Status,
                Notes = "Subscription เดิมถูกลบ (soft) — restore + resync จาก template แทน insert ใหม่",
                PerformedBy = performedBy
            });
            await _db.SaveChangesAsync();
            return await GetTrialStatusAsync(request.CompanyId);
        }

        // Get plan template for trial settings
        var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == request.TargetPlan && p.IsActive);

        var isPermanentFree = template?.IsPermanentFree ?? false;
        var trialDays = isPermanentFree
            ? 36500
            : (request.TrialDays ?? template?.TrialDurationDays ?? 30);
        var now = DateTime.UtcNow;

        // ===== เลือกแหล่ง limit ให้ถูกโหมด + floor > 0 =====
        // แพ็กเกจฟรีถาวร (IsPermanentFree) ไม่ใช่ trial — limit ตัวจริงอยู่คอลัมน์
        // หลักของ plan (Max*). เดิมดึง Trial* เสมอ → template ฟรีที่ admin ไม่ได้
        // กรอก Trial* ได้ 0/0 = สมัครปุ๊บสร้างเอกสาร/ลงบัญชีไม่ได้เลย.
        // Floor: plan ที่เปิดให้สมัครต้องใช้งานขั้นต่ำได้เสมอ — ค่า 0 บน counter
        // หลัก = ตั้งค่าพลาด ไม่ใช่เจตนา (ต่างจาก OCR ที่ 0 = ไม่ให้ใช้ ถูกต้อง)
        static int Pick(int primary, int secondary, int fallback)
            => primary > 0 ? primary : secondary > 0 ? secondary : fallback;
        var tMaxUsers = isPermanentFree
            ? Pick(template?.MaxUsers ?? 0, template?.TrialMaxUsers ?? 0, 2)
            : Pick(template?.TrialMaxUsers ?? 0, template?.MaxUsers ?? 0, 2);
        var tMaxDocs = isPermanentFree
            ? Pick(template?.MaxDocumentsPerMonth ?? 0, template?.TrialMaxDocumentsPerMonth ?? 0, 20)
            : Pick(template?.TrialMaxDocumentsPerMonth ?? 0, template?.MaxDocumentsPerMonth ?? 0, 20);
        var tMaxJournals = isPermanentFree
            ? Pick(template?.MaxJournalEntriesPerMonth ?? 0, template?.TrialMaxJournalEntriesPerMonth ?? 0, 50)
            : Pick(template?.TrialMaxJournalEntriesPerMonth ?? 0, template?.MaxJournalEntriesPerMonth ?? 0, 50);
        var tMaxStorage = (template?.MaxStorageBytes ?? 0) > 0
            ? template!.MaxStorageBytes : 50L * 1024 * 1024;

        var subscription = new Subscription
        {
            CompanyId = request.CompanyId,
            Plan = request.TargetPlan,
            Status = isPermanentFree ? SubscriptionStatus.Active : SubscriptionStatus.Trial,
            StartDate = now,
            EndDate = now.AddDays(trialDays),
            IsPermanentFree = isPermanentFree,
            EnabledFeatures = isPermanentFree
                ? (template?.EnabledFeatures ?? FeatureFlags.TrialFeatures)
                : (template?.TrialFeatures ?? FeatureFlags.TrialFeatures),
            MaxUsers = tMaxUsers,
            MaxCompanies = 1,
            MaxDocumentsPerMonth = tMaxDocs,
            MaxJournalEntriesPerMonth = tMaxJournals,
            MaxStorageBytes = tMaxStorage,
            // Pull OCR quotas from the template so a new subscription
            // honors the per-engine budget the admin configured for this
            // plan tier (free=0 Azure pages, paid tiers get more).
            MaxOcrPagesPerMonth = isPermanentFree
                ? (template?.MaxOcrPagesPerMonth ?? 0)
                : (template?.TrialMaxOcrPagesPerMonth ?? 10),
            AzureOcrPagesPerMonth = template?.AzureOcrPagesPerMonth,
            LocalOcrPagesPerMonth = template?.LocalOcrPagesPerMonth,
            FallbackToLocalWhenAzureExhausted = template?.FallbackToLocalWhenAzureExhausted ?? true,
            UsageResetDate = new DateTime(now.Year, now.Month, 1).AddMonths(1),
            CreatedBy = performedBy
        };

        _db.Subscriptions.Add(subscription);

        // Create trial config
        var trialConfig = new TrialConfig
        {
            SubscriptionId = subscription.Id,
            TrialStatus = TrialStatus.Active,
            TrialStartDate = now,
            TrialEndDate = now.AddDays(trialDays),
            TrialDurationDays = trialDays,
            AllowExtension = template != null ? template.TrialMaxExtensions > 0 : true,
            MaxExtensions = template?.TrialMaxExtensions ?? 2,
            ExtensionDays = template?.TrialExtensionDays ?? 15,
            TrialFeatures = template?.TrialFeatures ?? FeatureFlags.TrialFeatures,
            // ใช้ค่า effective เดียวกับ Subscription (ผ่าน Pick แล้ว) — ไม่งั้น
            // หน้าจอ trial status โชว์ 0/0 ทั้งที่ subscription จริงมี limit
            TrialMaxUsers = tMaxUsers,
            TrialMaxDocumentsPerMonth = tMaxDocs,
            TrialMaxJournalEntriesPerMonth = tMaxJournals,
            TrialMaxCompanies = 1,
            BlockAccessOnExpiry = template?.TrialBlockOnExpiry ?? false,
            GracePeriodDays = template?.TrialGracePeriodDays ?? 7,
            CreatedBy = performedBy
        };

        _db.TrialConfigs.Add(trialConfig);

        // Record history
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = subscription.Id,
            // ฟรีถาวรไม่ใช่ trial — audit ต้องตรง state จริง (เดิม hardcode
            // TrialStarted/Trial → รายงาน admin ตีความลูกค้าฟรีถาวรผิด)
            Action = isPermanentFree ? "FreePlanStarted" : "TrialStarted",
            ToPlan = request.TargetPlan,
            ToStatus = subscription.Status,
            Notes = isPermanentFree
                ? "แพ็กเกจฟรีถาวร (ไม่หมดอายุ)"
                : $"Trial started for {trialDays} days",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();

        return await GetTrialStatusAsync(request.CompanyId);
    }

    public async Task<TrialStatusResponse> GetTrialStatusAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        var tc = sub.TrialConfig
            ?? throw new InvalidOperationException("ไม่มีข้อมูล trial config");
        // พื้นที่จริง (Σ ไฟล์แนบ + สื่อ CMS) — Subscription.CurrentStorageUsed ไม่มีใครเขียน ⇒ เดิมโชว์ 0 ตลอด
        var trialStorageUsed = await StorageBytesForPoolAsync(await ResolvePoolCompanyIdsAsync(sub));

        var now = DateTime.UtcNow;
        var daysRemaining = Math.Max(0, (int)(tc.TrialEndDate - now).TotalDays);
        var daysUsed = (int)(now - tc.TrialStartDate).TotalDays;

        // Count current users
        var userCount = await _db.CompanyUsers.CountAsync(cu => cu.CompanyId == companyId);

        return new TrialStatusResponse(
            sub.Id,
            companyId,
            tc.TrialStatus,
            tc.TrialStartDate,
            tc.TrialEndDate,
            daysRemaining,
            daysUsed,
            tc.TrialDurationDays + (tc.ExtensionsUsed * tc.ExtensionDays),
            tc.AllowExtension && tc.ExtensionsUsed < tc.MaxExtensions,
            tc.ExtensionsUsed,
            tc.MaxExtensions,
            sub.EnabledFeatures,
            new TrialUsageInfo(
                sub.CurrentMonthDocuments,
                tc.TrialMaxDocumentsPerMonth,
                sub.CurrentMonthJournalEntries,
                tc.TrialMaxJournalEntriesPerMonth,
                userCount,
                tc.TrialMaxUsers,
                trialStorageUsed,
                sub.MaxStorageBytes),
            new TrialExpiryBehavior(
                tc.BlockAccessOnExpiry,
                tc.AllowDataExportAfterExpiry,
                tc.GracePeriodDays,
                tc.ShowTrialWatermark,
                tc.TrialMessage),
            tc.DiscountPercentOnConversion.HasValue
                ? new ConversionIncentive(tc.DiscountPercentOnConversion, tc.DiscountValidUntil, tc.ConversionPromoCode)
                : null,
            tc.ExtensionDays);
    }

    public async Task<TrialStatusResponse> ExtendTrialAsync(Guid companyId, ExtendTrialRequest request, string performedBy,
        bool allowCustomDays = false)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        var tc = sub.TrialConfig
            ?? throw new InvalidOperationException("ไม่มีข้อมูล trial config");

        // ฝ่ายค้านรอบ 219 (หมายเหตุ): ขยายได้เฉพาะระหว่างทดลองใช้ — เดิมขยายบน sub ที่จ่ายแล้ว/หมดอายุได้ (เลื่อน EndDate โดยไม่จ่าย)
        if (sub.Status != SubscriptionStatus.Trial)
            throw new BusinessRuleException("ขยายเวลาได้เฉพาะช่วงทดลองใช้ — แพ็กเกจนี้ไม่ได้อยู่ในช่วงทดลองใช้แล้ว");

        if (!tc.AllowExtension)
            throw new InvalidOperationException("Trial นี้ไม่อนุญาตให้ขยายเวลา");

        if (tc.ExtensionsUsed >= tc.MaxExtensions)
            throw new InvalidOperationException($"ใช้สิทธิ์ขยายเวลาครบแล้ว ({tc.MaxExtensions} ครั้ง)");

        // เพดานที่ server (ฝ่ายค้านรอบ 193 W-C3): เดิมผู้เรียกกำหนดจำนวนวันเองไม่มีเพดาน ⇒ ขยายครั้งเดียว 36,500 วันได้ ·
        // ลูกค้า = ไม่เกินค่าที่แพลตฟอร์มตั้งต่อครั้ง · ผู้ดูแลแพลตฟอร์ม (AdminController) กำหนดเองได้
        var additionalDays = Accounting.Helpers.TrialExtensionPolicy.ResolveDays(
            request.AdditionalDays, tc.ExtensionDays, allowCustomDays);
        tc.TrialEndDate = tc.TrialEndDate.AddDays(additionalDays);
        tc.ExtensionsUsed++;
        tc.TrialStatus = TrialStatus.Extended;

        sub.EndDate = tc.TrialEndDate;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "TrialExtended",
            Notes = $"Extended by {additionalDays} days (extension {tc.ExtensionsUsed}/{tc.MaxExtensions})",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
        return await GetTrialStatusAsync(companyId);
    }

    public async Task ExpireTrialAsync(Guid companyId, string performedBy)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        if (sub.TrialConfig != null)
            sub.TrialConfig.TrialStatus = TrialStatus.Expired;

        sub.Status = SubscriptionStatus.Expired;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "TrialExpired",
            FromStatus = SubscriptionStatus.Trial,
            ToStatus = SubscriptionStatus.Expired,
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
    }

    // ==================== Subscription Management ====================

    /// <summary>รอบ 219 เฟส 0 (G-0 · คำตัดสินข้อ 178): <b>จ่ายก่อนเปิด</b> — เดิมตั้ง Active + EndDate + ฟีเจอร์ทันทีโดยไม่มีขั้นชำระ
    /// (ปุ่ม "อัปเกรด" ให้แพ็กเกจจ่ายเงินฟรีหนึ่งรอบเต็ม) · ตอนนี้: ทดลองใช้/หมดอายุ ⇒ สร้างรายการชำระยอดจากเซิร์ฟเวอร์ (ทดลองใช้ต่อจนกว่าจะชำระ)
    /// · แพ็กเกจที่ใช้งานอยู่ ⇒ เดินเส้น <see cref="ChangeSubscriptionAsync"/> (อัปเกรด/ดาวน์เกรด) · แพ็กเกจเปิดเมื่อ <see cref="ReviewPaymentAsync"/> อนุมัติ</summary>
    public async Task<PlanPurchaseResponse> ConvertTrialAsync(Guid companyId, ConvertTrialRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        if (sub.Status == SubscriptionStatus.Active && !sub.IsPermanentFree)
            return await ChangeSubscriptionAsync(companyId,
                new ChangeSubscriptionRequest(request.Plan, request.BillingCycle), performedBy);

        var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == request.Plan && p.IsActive)
            ?? throw new KeyNotFoundException($"ไม่พบ plan template สำหรับ {request.Plan}");

        // ส่วนลดเมื่อแปลงจากทดลองใช้ (โค้ดของบริษัทนี้ · ยังไม่หมดอายุ) — คิดใน PlatformPlanPricing ตัวเดียว
        decimal? discount = null;
        if (!string.IsNullOrEmpty(request.PromoCode) && sub.TrialConfig != null
            && sub.TrialConfig.ConversionPromoCode == request.PromoCode
            && sub.TrialConfig.DiscountPercentOnConversion.HasValue
            && (sub.TrialConfig.DiscountValidUntil == null || sub.TrialConfig.DiscountValidUntil > DateTime.UtcNow))
            discount = sub.TrialConfig.DiscountPercentOnConversion.Value;

        var payment = await RequestPlanPaymentAsync(sub, template, request.BillingCycle, discount, performedBy,
            "PlanPurchaseRequested", "ขอเปลี่ยนเป็นแพ็กเกจจ่ายเงิน");
        return new PlanPurchaseResponse(await GetSubscriptionAsync(companyId), payment,
            $"สร้างรายการชำระ {payment.PaymentNumber} ยอด {payment.Amount:N2} บาทแล้ว — แพ็กเกจ {template.Name} จะเปิดใช้เมื่อชำระเงินและผู้ดูแลระบบยืนยัน "
            + "(ระหว่างนี้ใช้งานตามสิทธิ์เดิมได้ตามปกติ)");
    }

    /// <summary>รอบ 219 เฟส 0: สร้าง (หรือใช้ซ้ำ) รายการชำระค่าแพ็กเกจที่<b>ยอดมาจากเซิร์ฟเวอร์</b> (<see cref="PlatformPlanPricing.AmountDue"/>) —
    /// ทางเดียวของผู้เช่าในการขอแพ็กเกจจ่ายเงิน · ใช้ซ้ำรายการรอชำระที่แพ็กเกจ/รอบ/ยอดเดียวกัน (กดซ้ำไม่เกิดหลายรายการ)</summary>
    private async Task<SubscriptionPaymentResponse> RequestPlanPaymentAsync(Subscription sub, PlanTemplate template,
        BillingCycle cycle, decimal? discountPercent, string performedBy, string historyAction, string note)
    {
        var months = PlatformPlanPricing.MonthsOf(cycle);
        if (months <= 0) throw new BusinessRuleException("รอบบิลไม่ถูกต้อง");
        var amount = PlatformPlanPricing.AmountDue(template, cycle, discountPercent);
        if (amount <= 0m)
            throw new BusinessRuleException(
                "แพ็กเกจนี้ไม่มีค่าบริการตามตารางราคา — ติดต่อผู้ดูแลระบบ NextAcc เพื่อเปลี่ยนแพ็กเกจ");

        var existing = await _db.SubscriptionPayments
            .FirstOrDefaultAsync(p => p.SubscriptionId == sub.Id && p.Status == SubscriptionPaymentStatus.Pending
                && p.RequestedPlan == template.Plan && p.RequestedBillingCycle == cycle && p.Amount == amount);
        if (existing != null) return MapPaymentToResponse(existing);

        var payment = new SubscriptionPayment
        {
            SubscriptionId = sub.Id,
            PaymentNumber = await NextPaymentNumberAsync(sub),
            Amount = amount,
            PaymentDate = DateTime.UtcNow,
            PaymentMethod = PaymentMethod.BankTransfer,
            RequestedPlan = template.Plan,
            RequestedBillingCycle = cycle,
            RequestedPeriodMonths = months,
            CustomerNotes = note,
            Status = SubscriptionPaymentStatus.Pending,
            CreatedBy = performedBy
        };
        _db.SubscriptionPayments.Add(payment);
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = historyAction,
            FromPlan = sub.Plan,
            ToPlan = template.Plan,
            Notes = $"{note}: {payment.PaymentNumber} {template.Plan} ({cycle}) {amount:N2} THB — รอชำระ (ยังไม่เปิดแพ็กเกจ)",
            PerformedBy = performedBy
        });
        await _db.SaveChangesAsync();
        return MapPaymentToResponse(payment);
    }

    /// <summary>เลขรายการชำระค่าแพ็กเกจ (SP-{บริษัท 8 ตัว}-{ลำดับ}) — ตัวเดียวของ Submit/คำขอแพ็กเกจ/บันทึกโดยแอดมิน</summary>
    private async Task<string> NextPaymentNumberAsync(Subscription sub)
    {
        var count = await _db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id);
        return $"SP-{sub.CompanyId.ToString()[..8].ToUpper()}-{count + 1:D4}";
    }

    /// <summary>รอบ 219 เฟส 0: คัดลอกสิทธิ์/โควตาของแพ็กเกจลง subscription — ตัวเดียวของการอนุมัติชำระ และดาวน์เกรดที่ถึงวันมีผล</summary>
    private static void ApplyTemplate(Subscription sub, PlanTemplate template, BillingCycle cycle)
    {
        sub.Plan = template.Plan;
        sub.BillingCycle = cycle;
        sub.PricePerCycle = PlatformPlanPricing.CyclePrice(template, cycle);
        sub.EnabledFeatures = template.EnabledFeatures;
        sub.MaxUsers = template.MaxUsers;
        sub.MaxCompanies = template.MaxCompanies;
        sub.MaxDocumentsPerMonth = template.MaxDocumentsPerMonth;
        sub.MaxJournalEntriesPerMonth = template.MaxJournalEntriesPerMonth;
        sub.MaxStorageBytes = template.MaxStorageBytes;
        sub.MaxOcrPagesPerMonth = template.MaxOcrPagesPerMonth;
        sub.AzureOcrPagesPerMonth = template.AzureOcrPagesPerMonth;
        sub.LocalOcrPagesPerMonth = template.LocalOcrPagesPerMonth;
        sub.FallbackToLocalWhenAzureExhausted = template.FallbackToLocalWhenAzureExhausted;
        sub.IsPermanentFree = template.IsPermanentFree;
        // ฝ่ายค้านรอบ 219 S2: แพ็กเกจฟรีถาวร = Active ไม่หมดอายุ (เหมือน ResyncSubscriptionsFromTemplateAsync) — ดาวน์เกรดไปแพ็กเกจฟรีห้ามตกเป็น PastDue
        if (template.IsPermanentFree)
        {
            sub.Status = SubscriptionStatus.Active;
            sub.EndDate = DateTime.UtcNow.AddYears(100);
            sub.NextBillingDate = null;
        }
    }

    public async Task<SubscriptionResponse> GetSubscriptionAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);

        // Auto-create a default Free Edition (permanent free) if missing (e.g. legacy
        // company created before signup auto-subscription was added). Avoids 404 cascading
        // errors on settings/dashboard.
        if (sub == null)
        {
            try
            {
                await StartTrialAsync(
                    new Models.DTOs.Subscription.StartTrialRequest(companyId, SubscriptionPlan.FreeTrial),
                    "auto");
                sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
            }
            catch
            {
                // If auto-start fails (e.g. plan templates missing), throw the original 404
            }
            if (sub == null) throw new KeyNotFoundException("ไม่พบ subscription");
        }

        // Resolver overlay: when this company rides under a User License, the
        // License's features/limits/status win. Without this overlay the
        // SubscriptionMiddleware sees the stale per-company FreeTrial features
        // and blocks paid routes even after admin attached the company to an
        // Enterprise License. Mirrors the source-of-truth picked by
        // GetEffectivePlanAsync but stays inline so we don't double-query.
        // รอบ 198 ข้อ 5: แยกเป็น ResolveGateOverlayAsync ตัวเดียวที่ GetGateStateAsync (middleware) ใช้ร่วม — แพ็กเกจ/สถานะ/ฟีเจอร์
        // ที่หน้าเว็บเห็นกับที่ gate ตัดสินต้องมาจากสูตรเดียว
        var ov = await ResolveGateOverlayAsync(sub, companyId);
        var plan = ov.Plan;
        string? planName = ov.PlanName;
        var status = ov.Status;
        var features = ov.Features;
        var acct = ov.Account;
        var endDate = acct?.EndDate ?? sub.EndDate;
        var maxUsers = acct?.MaxUsersPerCompany ?? sub.MaxUsers;
        var maxCompanies = acct?.MaxCompanies ?? sub.MaxCompanies;
        var maxDocs = acct?.MaxDocumentsPerMonth ?? sub.MaxDocumentsPerMonth;
        var maxJournals = acct?.MaxJournalEntriesPerMonth ?? sub.MaxJournalEntriesPerMonth;
        var maxStorage = acct?.MaxStorageBytes ?? sub.MaxStorageBytes;

        // add-on ที่ซื้อเพิ่ม (string code) เดินทางมากับแพ็กเกจ เพื่อให้หน้าเว็บมี
        // ตัวตัดสินสิทธิ์ตัวเดียว (`Layout.hasFeature`) ไม่ต้องยิง endpoint ที่สอง
        // ฝ่ายค้านรอบ 219 S7: ส่วนเสริมที่ค้างชำระเกินระยะผ่อนผัน = ไม่อยู่ในรายการที่ใช้ได้ (ตัวตัดสินเดียวกับด่านสิทธิ์ — หน้าเว็บแสดงอย่างเดียว)
        var addOnNow = DateTime.UtcNow;
        var addOnCodes = (await _db.CompanyFeatures.AsNoTracking()
            .Where(f => f.CompanyId == companyId && f.IsEnabled && !f.IsDeleted)
            .Select(f => new { f.FeatureCode, f.PaymentStatus, f.PaymentAwaitingSince })
            .ToListAsync())
            .Where(f => AddOnPaymentPolicy.UsableWhilePending(f.PaymentStatus, f.PaymentAwaitingSince, addOnNow))
            .Select(f => f.FeatureCode)
            .ToList();

        // ชื่อแพ็กเกจที่แอดมินตั้ง (ไม่ใช่ชื่อ enum) — หน้าเว็บแสดงค่านี้ตรง ๆ
        planName ??= await _db.PlanTemplates.AsNoTracking()
            .Where(t => t.Plan == plan && t.IsActive)
            .Select(t => t.Name)
            .FirstOrDefaultAsync();

        return new SubscriptionResponse(
            sub.Id, sub.CompanyId, plan, status, sub.BillingCycle,
            sub.PricePerCycle, sub.StartDate, endDate, sub.NextBillingDate,
            features,
            FeatureFlagsHelper.ToNameList(features),
            new UsageLimits(maxUsers, maxCompanies, maxDocs, maxJournals, maxStorage),
            await GetUsageCurrentAsync(sub),
            sub.IsPermanentFree,
            addOnCodes,
            planName,
            ScheduledPlan: sub.ScheduledPlan,
            ScheduledBillingCycle: sub.ScheduledBillingCycle,
            ScheduledPlanEffectiveAt: sub.ScheduledPlanEffectiveAt,
            CancelAtPeriodEnd: sub.CancelAtPeriodEnd,
            CancelNote: SubscriptionCancelPolicy.Note(sub.CancelAtPeriodEnd, sub.EndDate));
    }

    /// <summary>ผลของ overlay แพ็กเกจ (บริษัท ↔ User License) + mask ที่เจ้าของบริษัทปิดเอง — ตัวเดียวของ
    /// <see cref="GetSubscriptionAsync"/> และ <see cref="GetGateStateAsync"/></summary>
    private sealed record GateOverlay(SubscriptionPlan Plan, string? PlanName, SubscriptionStatus Status,
        FeatureFlags Features, AccountSubscription? Account);

    private async Task<GateOverlay> ResolveGateOverlayAsync(Subscription sub, Guid companyId)
    {
        var plan = sub.Plan;
        string? planName = null;
        var status = sub.Status;
        var features = sub.EnabledFeatures;
        AccountSubscription? acct = null;

        if (sub.AccountSubscriptionId.HasValue)
        {
            acct = await _db.AccountSubscriptions
                .Include(a => a.PlanTemplate)
                .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
            if (acct != null) features = acct.EnabledFeatures;
        }
        // รอบ 205 ทีม I: ตัวตนแพ็กเกจ (ระดับ/ชื่อ/สถานะ) ผ่านตัวตัดสินตัวเดียวกับแดชบอร์ด/รายการบริษัท — Helpers/PlanIdentityResolver
        var identity = Accounting.Helpers.PlanIdentityResolver.Resolve(sub.Plan, sub.Status, sub.EndDate, null,
            acct != null
                ? new Accounting.Helpers.PlanIdentityResolver.LicenseSide(acct.PlanTemplate.Plan, acct.PlanTemplate.Name, acct.Status, acct.EndDate)
                : null);
        plan = identity.Plan;
        planName = identity.PlanName;
        status = identity.Status;

        // รอบ 200 ข้อ 14: ลูกค้าช่วงทดลอง/แพ็กเกจฟรีที่สำเนาฟีเจอร์ว่าง (คอลัมน์ DEFAULT 0 · แอดมินบันทึกรายการว่าง) ⇒ ใช้ฟีเจอร์ตามข้อมูล
        // แพ็กเกจ แทนการถูกบล็อกทุกเส้นทางเมื่อเปิดบังคับ · แพ็กเกจก็ว่าง = คงว่าง (รายงานแอดมินแสดง — ไม่แต่งชุดฟีเจอร์เอง) ·
        // S200-4: เมธอดเดียวกับ GetEffectivePlanAsync (ResolvePlanFeaturesAsync) — หน้าเว็บ/gate กับด่านสร้างเอกสารเห็นชุดเดียวกัน
        features = acct != null
            ? await ResolvePlanFeaturesAsync(features, plan, status, acct.PlanTemplate?.IsPermanentFree ?? false, acct.EndDate,
                subscriptionIdForTrialConfig: null, acct.PlanTemplate)
            : await ResolvePlanFeaturesAsync(features, plan, status, sub.IsPermanentFree, sub.EndDate, sub.Id, null);

        // Owner-level subtractive override — when the Owner has flipped
        // off features in CompanySettings.OwnerDisabledFeatures, mask
        // them out so the frontend sees only what the Owner has chosen
        // to expose. SystemAdmin's Subscription assignment is still
        // the upper bound; this is opt-out only.
        var ownerDisabled = await _db.Set<CompanySettings>()
            .Where(s => s.CompanyId == companyId)
            .Select(s => (FeatureFlags?)s.OwnerDisabledFeatures)
            .FirstOrDefaultAsync() ?? FeatureFlags.None;
        if (ownerDisabled != FeatureFlags.None)
            features = features & ~ownerDisabled;

        return new GateOverlay(plan, planName, status, features, acct);
    }

    /// <summary>
    /// <b>ฟีเจอร์ของแพ็กเกจที่มีผล (ก่อน mask ของเจ้าของบริษัท) — ตัวเดียวของทั้งระบบ</b> (รอบ 200 ฝ่ายค้าน S200-4)
    ///
    /// <para>เดิมมีสองตัวตัดสิน: <see cref="ResolveGateOverlayAsync"/> (หน้าเว็บ <c>hasFeature</c> + gate) เติมฟีเจอร์ทดลองจากแพ็กเกจเมื่อสำเนาว่าง แต่
    /// <see cref="GetEffectivePlanAsync"/> → <see cref="CheckFeatureAccessAsync"/> (ด่านสร้างเอกสาร · EntitlementService) อ่านสำเนาว่างตรง ๆ ⇒ หน้าเว็บบอก
    /// "มีฟีเจอร์" แต่กดสร้างเอกสารได้ "ไม่มีสิทธิ์ใช้ระบบเอกสาร". ตอนนี้ทั้งสองเรียกเมธอดนี้ · กติกาอยู่ที่
    /// <see cref="Accounting.Helpers.SubscriptionTrialReadiness.ResolveFeatures"/> (เฉพาะระหว่างทดลองที่ยังไม่หมดอายุ/ฟรีถาวร Active · เคารพ
    /// trial config รายบริษัท = None) · โหลดแพ็กเกจ/trial config เฉพาะเมื่อสำเนาว่างและอยู่ในเงื่อนไข (กรณีหายาก)</para>
    /// </summary>
    /// <param name="subscriptionIdForTrialConfig">แถว <c>Subscription</c> ของบริษัท (อ่าน <c>TrialConfig.TrialFeatures</c>) · <c>null</c> = สาย User License</param>
    /// <param name="knownTemplate">แพ็กเกจที่โหลดมาแล้ว (สาย User License) · <c>null</c> = หาแพ็กเกจที่เปิดใช้ตามระดับ</param>
    private async Task<FeatureFlags> ResolvePlanFeaturesAsync(FeatureFlags snapshot, SubscriptionPlan plan, SubscriptionStatus status,
        bool isPermanentFree, DateTime endDate, Guid? subscriptionIdForTrialConfig, PlanTemplate? knownTemplate)
    {
        var now = DateTime.UtcNow;
        if (!Accounting.Helpers.SubscriptionTrialReadiness.MayFillFromTemplate(snapshot, plan, status, isPermanentFree, endDate, now))
            return snapshot;
        var tpl = knownTemplate ?? await _db.PlanTemplates.AsNoTracking()
            .Where(t => t.Plan == plan && t.IsActive)
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefaultAsync();
        FeatureFlags? companyTrial = subscriptionIdForTrialConfig is Guid subId
            ? await _db.TrialConfigs.AsNoTracking()
                .Where(tc => tc.SubscriptionId == subId)
                .Select(tc => (FeatureFlags?)tc.TrialFeatures)
                .FirstOrDefaultAsync()
            : null;
        return Accounting.Helpers.SubscriptionTrialReadiness.ResolveFeatures(new Accounting.Helpers.TrialFeatureInputs(
            snapshot, plan, status, isPermanentFree, endDate, companyTrial, tpl?.TrialFeatures, tpl?.EnabledFeatures), now).Features;
    }

    /// <inheritdoc/>
    public async Task<SubscriptionGateState?> GetGateStateAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null) return null;
        var ov = await ResolveGateOverlayAsync(sub, companyId);
        return new SubscriptionGateState(ov.Plan, ov.Status, ov.Features);
    }

    /// <summary>
    /// **ตัวตอบ “ใช้ไปเท่าไร” ตัวเดียวของทั้งระบบ** — หน้าลูกค้า (`/api/subscription`) ·
    /// แดชบอร์ดการใช้งาน · พอร์ทัลแอดมิน (`/api/admin/customers/{id}`) ต้องเรียกตัวนี้
    /// ห้ามอ่านคอลัมน์ counter บน <see cref="Subscription"/> ตรง ๆ
    ///
    /// <para>ที่มา (2026-09-10): แอดมินเห็น Usage Limits เป็น 0 ทุกบริษัทตลอดกาล เพราะ
    /// endpoint แอดมินไม่เคยส่ง <c>current</c> (JS คัดลอกจากหน้าลูกค้าแล้วอ่านช่องที่
    /// เซิร์ฟเวอร์ตัวนี้ไม่ส่ง) และเมื่อไล่ต่อพบว่า counter สองตัวเป็น **write-never**:
    /// <c>CurrentStorageUsed</c> ไม่มีใครเขียนเลยทั้งเรพ (ที่เขียนคือ <c>CmsSite</c> คนละตาราง)
    /// และ <c>IncrementUsageAsync(…,"journal")</c> ไม่มี call site ⇒ ตัวเลขทั้งสองเป็น 0
    /// แม้ในหน้าลูกค้า และด่าน storage ผ่านเสมอ. ตัวนี้จึงคิดจาก**ของจริง**:
    /// <list type="bullet">
    /// <item>เอกสาร = counter รายเดือน (สูตรเดียวกับด่านบล็อก — ห้ามนับ COUNT(Documents)
    /// เพราะนโยบายนับเฉพาะบางชนิด ดู <c>DocumentQuotaPolicy</c>) · stale-aware · รวมทุกบริษัท
    /// ใต้ Account Plan เดียวกันเหมือนที่ enforcement เห็น</item>
    /// <item>สมุดรายวัน = COUNT(JournalEntries ที่ Posted ในเดือนนี้) ของ pool — ไม่ใช่ counter
    /// ที่ไม่มีใครเพิ่ม (เพดานสมุดรายวันยัง**ไม่ถูกบังคับ**ที่ไหน — จดไว้ใน backlog
    /// ไม่ใช่แอบเปิดด่านใหม่ในคอมมิตแสดงผล)</item>
    /// <item>พื้นที่ = Σ FileAttachments.FileSize ของ pool + สื่อ CMS ของ pool — สูตรเดียวกับ
    /// <see cref="CanFitStorageAsync"/></item>
    /// </list></para>
    /// </summary>
    public async Task<UsageCurrent> GetUsageCurrentAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
        if (sub == null) return new UsageCurrent(0, 0, 0);
        return await GetUsageCurrentAsync(sub);
    }

    private async Task<UsageCurrent> GetUsageCurrentAsync(Subscription sub)
    {
        var now = DateTime.UtcNow;
        var pool = await ResolvePoolCompanyIdsAsync(sub);

        int docs;
        if (sub.AccountSubscriptionId.HasValue)
        {
            var agg = await GetAggregateUsageAsync(sub.AccountSubscriptionId.Value);
            docs = agg?.Documents ?? (now >= sub.UsageResetDate ? 0 : sub.CurrentMonthDocuments);
        }
        else
        {
            // counter ที่ยังไม่ถูก reset ของเดือนก่อน ห้ามโชว์เป็นของเดือนนี้ (กติกา Stale
            // เดียวกับ GetAggregateUsageAsync)
            docs = now >= sub.UsageResetDate ? 0 : sub.CurrentMonthDocuments;
        }

        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var journals = await _db.JournalEntries.AsNoTracking()
            .CountAsync(j => pool.Contains(j.CompanyId) && !j.IsDeleted
                && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= monthStart);

        var storage = await StorageBytesForPoolAsync(pool);
        return new UsageCurrent(docs, journals, storage);
    }

    /// <summary>บริษัททั้งหมดที่ใช้โควตาก้อนเดียวกัน — ใต้ Account Plan = ทุกบริษัทที่ผูก
    /// License เดียวกัน · ไม่ใช่ = ตัวเอง</summary>
    private async Task<List<Guid>> ResolvePoolCompanyIdsAsync(Subscription sub)
    {
        if (!sub.AccountSubscriptionId.HasValue) return new List<Guid> { sub.CompanyId };
        var ids = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.AccountSubscriptionId == sub.AccountSubscriptionId.Value && !s.IsDeleted)
            .Select(s => s.CompanyId)
            .ToListAsync();
        return ids.Count == 0 ? new List<Guid> { sub.CompanyId } : ids;
    }

    /// <summary>พื้นที่ที่ใช้จริงของ pool = ไฟล์แนบระบบบัญชี (นับจากตาราง ไม่ใช่ counter
    /// ที่ไม่มีใครเขียน) + สื่อ CMS (counter ของ CmsSite ซึ่ง CmsContentService ดูแลอยู่แล้ว)</summary>
    private async Task<long> StorageBytesForPoolAsync(List<Guid> poolCompanyIds)
    {
        var docBytes = await _db.FileAttachments.AsNoTracking()
            .Where(f => poolCompanyIds.Contains(f.CompanyId))
            .SumAsync(f => (long?)f.FileSize) ?? 0L;
        var cmsBytes = await _db.Sites.AsNoTracking()
            .Where(s => poolCompanyIds.Contains(s.CompanyId) && !s.IsDeleted)
            .SumAsync(s => (long?)s.CurrentStorageUsed) ?? 0L;
        return docBytes + cmsBytes;
    }

    /// <summary>รอบ 219 เฟส 0 (G-0 · คำตัดสินข้อ 178): เดิมเปลี่ยนแพ็กเกจ/ราคา/สิทธิ์ทันทีโดยไม่คิดเงิน ⇒ อัปเกรดฟรีจนถึงรอบต่ออายุ ·
    /// ตอนนี้ <b>อัปเกรด</b> = รายการชำระยอดจากเซิร์ฟเวอร์ (เปิดเมื่อเงินเข้า) · <b>ดาวน์เกรด/เปลี่ยนรอบ</b> = ตั้งไว้มีผลวันครบรอบ
    /// (<c>Scheduled*</c> → <see cref="ProcessExpiredSubscriptionsAsync"/>) · แอดมินแพลตฟอร์มเปลี่ยนทันทีได้ที่ <c>AdminController.AdminChangePlan</c> (audit)</summary>
    public async Task<PlanPurchaseResponse> ChangeSubscriptionAsync(Guid companyId, ChangeSubscriptionRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        if (sub.Status != SubscriptionStatus.Active)
            throw new InvalidOperationException("สามารถเปลี่ยน plan ได้เฉพาะ subscription ที่ active เท่านั้น");

        var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == request.NewPlan && p.IsActive)
            ?? throw new KeyNotFoundException($"ไม่พบ plan template สำหรับ {request.NewPlan}");
        var current = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Plan == sub.Plan && p.IsActive);
        var cycle = request.NewBillingCycle ?? sub.BillingCycle;

        var kind = PlatformPlanPricing.Classify(current?.MonthlyPrice ?? 0m, template.MonthlyPrice);
        if (kind == PlatformPlanPricing.ChangeKind.Upgrade)
        {
            var payment = await RequestPlanPaymentAsync(sub, template, cycle, null, performedBy,
                "PlanUpgradeRequested", $"ขออัปเกรดจาก {sub.Plan}");
            return new PlanPurchaseResponse(await GetSubscriptionAsync(companyId), payment,
                $"สร้างรายการชำระ {payment.PaymentNumber} ยอด {payment.Amount:N2} บาทแล้ว — อัปเกรดเป็น {template.Name} มีผลทันทีเมื่อชำระเงินและผู้ดูแลระบบยืนยัน");
        }

        if (template.Plan == sub.Plan && cycle == sub.BillingCycle)
            return new PlanPurchaseResponse(await GetSubscriptionAsync(companyId), null, "แพ็กเกจและรอบบิลนี้คือแพ็กเกจปัจจุบันอยู่แล้ว");

        // ฝ่ายค้านรอบ 219 S9: ใบแจ้งหนี้ต่ออายุของรอบนี้ออกไปแล้ว (ราคาแพ็กเกจเดิม) ⇒ ไม่รับดาวน์เกรดในช่วงนั้น (ไม่แก้ใบที่ออกแล้วเงียบ ๆ) — บอกวันที่เลือกได้
        if (PlatformPlanPricing.ScheduleBlockedByIssuedRenewal(sub.RenewalInvoiceForEndDate, sub.EndDate) is string blockMsg)
            throw new BusinessRuleException(blockMsg);

        // ดาวน์เกรด / เปลี่ยนรอบบิล — ไม่ตัดสิทธิ์ที่จ่ายไปแล้ว: มีผลวันครบรอบปัจจุบัน · ฝ่ายค้านรอบ 219 รอบ 2 (S2): ฟรีถาวร ⇒ มีผลทันที (ไม่ใช่ +100 ปี)
        var effectiveAt = PlatformPlanPricing.ScheduledChangeEffectiveAt(sub.IsPermanentFree, sub.EndDate, DateTime.UtcNow);
        sub.ScheduledPlan = template.Plan;
        sub.ScheduledBillingCycle = cycle;
        sub.ScheduledPlanEffectiveAt = effectiveAt;
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "PlanChangeScheduled",
            FromPlan = sub.Plan,
            ToPlan = template.Plan,
            Notes = $"เปลี่ยนเป็น {template.Plan} ({cycle}) มีผล {effectiveAt:yyyy-MM-dd}",
            PerformedBy = performedBy
        });
        await _db.SaveChangesAsync();
        return new PlanPurchaseResponse(await GetSubscriptionAsync(companyId), null,
            $"บันทึกแล้ว — เปลี่ยนเป็น {template.Name} มีผล {effectiveAt.AddHours(7):dd/MM/yyyy} (ใช้สิทธิ์แพ็กเกจเดิมได้จนถึงวันนั้น)",
            effectiveAt);
    }

    public async Task<string> CancelSubscriptionAsync(Guid companyId, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        // รอบ 219 เฟส 2 ขั้น 3 (คำตัดสินเจ้าของข้อ 178 ⑥): มีรอบที่ชำระแล้วเหลือ ⇒ ใช้ได้ถึงสิ้นรอบ ไม่ prorate ไม่คืนเงิน · ตัวตัดสินเดียว SubscriptionCancelPolicy
        var now = DateTime.UtcNow;
        if (SubscriptionCancelPolicy.Decide(sub.Status, sub.IsPermanentFree, sub.EndDate, now) == SubscriptionCancelMode.AtPeriodEnd)
        {
            if (!sub.CancelAtPeriodEnd)
            {
                sub.CancelAtPeriodEnd = true;
                sub.CancelRequestedAt = now;
                sub.CancelRequestedBy = performedBy;
                _db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = sub.Id,
                    Action = "CancelScheduled",
                    FromStatus = sub.Status,
                    ToStatus = sub.Status,
                    Notes = $"ยกเลิกเมื่อสิ้นรอบ {sub.EndDate:yyyy-MM-dd} (ใช้งานได้ถึงวันนั้น · ไม่ออกใบต่ออายุ · ไม่ prorate)",
                    PerformedBy = performedBy
                });
                await _db.SaveChangesAsync();
            }
            return SubscriptionCancelPolicy.Note(true, sub.EndDate)!;
        }

        // เก็บสถานะเดิม "ก่อน" mutate — เดิมอ่านหลังตั้ง Cancelled ทำให้ history
        // ได้ From=Cancelled→Cancelled ทุกครั้ง audit สืบสถานะก่อนยกเลิกไม่ได้
        var cancelFromStatus = sub.Status;
        sub.Status = SubscriptionStatus.Cancelled;
        sub.CancelledAt = DateTime.UtcNow;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "Cancelled",
            FromStatus = cancelFromStatus,
            ToStatus = SubscriptionStatus.Cancelled,
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
        return "ยกเลิกแล้ว";
    }

    /// <summary>รอบ 219 เฟส 2 ขั้น 3: กลับมาใช้ต่อ (ยกเลิกการยกเลิก) ก่อนสิ้นรอบ — ใบต่ออายุจะออกตามปกติ</summary>
    public async Task<string> ReactivateSubscriptionAsync(Guid companyId, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");
        if (SubscriptionCancelPolicy.ReactivateRefusal(sub.CancelAtPeriodEnd, sub.Status, sub.EndDate, DateTime.UtcNow) is string why)
            throw new BusinessRuleException(why, "SUB-REACTIVATE-REFUSED", 409);
        sub.CancelAtPeriodEnd = false;
        sub.CancelRequestedAt = null;
        sub.CancelRequestedBy = null;
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "CancelReverted",
            FromStatus = sub.Status,
            ToStatus = sub.Status,
            Notes = "ใช้ต่อ — ยกเลิกการยกเลิกเมื่อสิ้นรอบ",
            PerformedBy = performedBy
        });
        await _db.SaveChangesAsync();
        return $"ใช้ต่อแล้ว — ต่ออายุตามปกติหลัง {PlatformInvoiceRules.ThaiDate(sub.EndDate):dd/MM/yyyy}";
    }

    // ==================== Usage Tracking ====================

    public async Task<bool> CheckFeatureAccessAsync(Guid companyId, FeatureFlags feature, bool recordShadow = true)
    {
        var eff = await GetEffectivePlanAsync(companyId);
        if (eff == null || !eff.IsActive) return false;
        if (!eff.EnabledFeatures.HasFlag(feature)) return false;
        // รอบ 201 ทีม PL (C-3 · ข้อ 76): เจ้าของบริษัทปิดฟีเจอร์ (CompanySettings.OwnerDisabledFeatures) — เดิมมีผลแค่ overlay หน้าเว็บ ⇒
        // ด่าน service เป็น silent no-op · ตอนนี้ผ่านโหมดเงาก่อน (บันทึก "จะถูกปิด") แล้วบังคับเมื่อแอดมินกด (SiteSettings.OwnerFeatureMaskEnforced)
        // ตัวตัดสินเดียว Helpers/OwnerFeatureMask · โหลดสวิตช์เฉพาะเมื่อเจ้าของปิดบิตของฟีเจอร์นี้ (กรณีหายาก — ไม่เพิ่ม query ทุกคำขอ)
        var ownerDisabled = await _db.Set<CompanySettings>().AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => (FeatureFlags?)s.OwnerDisabledFeatures)
            .FirstOrDefaultAsync() ?? FeatureFlags.None;
        if (!Accounting.Helpers.OwnerFeatureMask.IsOwnerDisabled(ownerDisabled, feature)) return true;
        var enforced = await _db.SiteSettings.AsNoTracking()
            .OrderBy(s => s.CreatedAt)
            .Select(s => (bool?)s.OwnerFeatureMaskEnforced)
            .FirstOrDefaultAsync() ?? false;
        var verdict = Accounting.Helpers.OwnerFeatureMask.Decide(eff.EnabledFeatures, ownerDisabled, feature, enforced);
        // PL-C2: เส้นอ่าน (GET ของหน้าเว็บ) ไม่ใช่ "การใช้งานที่จะถูกปิด" — บันทึกเฉพาะด่านที่กำลังจะทำงานจริง (สร้างเอกสาร · EntitlementService)
        if (verdict.ShadowHit && recordShadow && _shadowLog != null)
            await _shadowLog.RecordAsync(new SubscriptionGateShadowHit(
                companyId, Accounting.Helpers.SubscriptionGateReason.OwnerDisabledFeature, feature.ToString(), eff.Plan.ToString(),
                RouteKey: null, Method: "SERVICE", WouldBlock: true,
                Endpoint: "service:CheckFeatureAccess", SubscriptionStatus: eff.Status.ToString(), Enforced: false));
        return verdict.Allowed;
    }

    /// <summary>The resolved plan for a Company at quota-check time. Encapsulates
    /// the hybrid model: a Company's own Subscription (per-company plan) wins
    /// when AccountSubscriptionId is null; otherwise the owner's AccountSubscription
    /// (account-level plan) drives limits and features. Grace-period logic is
    /// applied here so callers don't reimplement it.</summary>
    public record EffectivePlan(
        string Source,                    // "Company" or "Account"
        bool IsActive,                    // false = past-expiry past-grace → readonly
        bool InGrace,                     // true = expired but within grace days
        SubscriptionStatus Status,
        DateTime EndDate,
        int MaxUsers,
        int MaxCompanies,
        int MaxDocumentsPerMonth,
        int MaxJournalEntriesPerMonth,
        long MaxStorageBytes,
        int MaxOcrPagesPerMonth,
        int? AzureOcrPagesPerMonth,
        int? LocalOcrPagesPerMonth,
        FeatureFlags EnabledFeatures,
        /// <summary>ระดับแพ็กเกจที่มีผลจริง — ใช้ตัดสิน "add-on นี้ขายเฉพาะ Pro ขึ้นไป"
        /// (`ApiFeature.MinPlanCsv`). ต้องมาจาก resolver ตัวเดียวกับ limits/features
        /// ไม่ใช่ query ซ้ำที่อื่น (บทเรียน "สอง resolver จะเถียงกันเองต่อหน้าผู้ใช้")</summary>
        SubscriptionPlan Plan = SubscriptionPlan.FreeTrial);

    public async Task<EffectivePlan?> GetEffectivePlanAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
        if (sub == null) return null;

        // Per-company plan wins when AccountSubscriptionId is null (legacy /
        // explicit "this company pays its own bill" mode).
        // S200-4: ฟีเจอร์ผ่าน ResolvePlanFeaturesAsync ตัวเดียวกับ overlay ของหน้าเว็บ/gate (ทดลองสำเนาว่าง ⇒ ตามข้อมูลแพ็กเกจ)
        if (!sub.AccountSubscriptionId.HasValue)
        {
            return await WithPlanFeaturesAsync(BuildFromCompanySub(sub), sub);
        }

        // Account-plan path. If the account row is missing / deleted, fall back
        // to the per-company row so we never lock the user out by accident.
        var acct = await _db.AccountSubscriptions
            .Include(a => a.PlanTemplate)   // ต้องมี ไม่งั้น EffectivePlan.Plan ตกเป็น FreeTrial เสมอ
            .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
        if (acct == null)
        {
            // Defensive: detach the dangling pointer so the next check is clean.
            sub.AccountSubscriptionId = null;
            await _db.SaveChangesAsync();
            return await WithPlanFeaturesAsync(BuildFromCompanySub(sub), sub);
        }

        var now = DateTime.UtcNow;
        var inGrace = acct.Status == SubscriptionStatus.Expired
                      && now <= acct.EndDate.AddDays(acct.GracePeriodDays);
        var active = (acct.Status == SubscriptionStatus.Trial
                      || acct.Status == SubscriptionStatus.Active
                      || acct.Status == SubscriptionStatus.PastDue)
                     && now <= acct.EndDate.AddDays(acct.GracePeriodDays);
        return new EffectivePlan(
            Source: "Account",
            IsActive: active,
            InGrace: inGrace,
            Status: acct.Status,
            EndDate: acct.EndDate,
            MaxUsers: acct.MaxUsersPerCompany,
            MaxCompanies: acct.MaxCompanies,
            MaxDocumentsPerMonth: acct.MaxDocumentsPerMonth,
            MaxJournalEntriesPerMonth: acct.MaxJournalEntriesPerMonth,
            MaxStorageBytes: acct.MaxStorageBytes,
            MaxOcrPagesPerMonth: acct.MaxOcrPagesPerMonth,
            AzureOcrPagesPerMonth: acct.AzureOcrPagesPerMonth,
            LocalOcrPagesPerMonth: acct.LocalOcrPagesPerMonth,
            EnabledFeatures: await ResolvePlanFeaturesAsync(acct.EnabledFeatures,
                acct.PlanTemplate != null ? acct.PlanTemplate.Plan : SubscriptionPlan.FreeTrial, acct.Status,
                acct.PlanTemplate?.IsPermanentFree ?? false, acct.EndDate, subscriptionIdForTrialConfig: null, acct.PlanTemplate),
            Plan: acct.PlanTemplate != null ? acct.PlanTemplate.Plan : SubscriptionPlan.FreeTrial);
    }

    /// <summary>S200-4: ฟีเจอร์ของสายบริษัทผ่านตัวเดียวกับ overlay (<see cref="ResolvePlanFeaturesAsync"/>)</summary>
    private async Task<EffectivePlan> WithPlanFeaturesAsync(EffectivePlan eff, Subscription sub) =>
        eff with
        {
            EnabledFeatures = await ResolvePlanFeaturesAsync(sub.EnabledFeatures, sub.Plan, sub.Status, sub.IsPermanentFree,
                sub.EndDate, sub.Id, null),
        };

    private static EffectivePlan BuildFromCompanySub(Subscription sub)
    {
        var now = DateTime.UtcNow;
        var graceDays = sub.TrialConfig?.GracePeriodDays ?? 7;
        var inGrace = sub.Status == SubscriptionStatus.Expired && now <= sub.EndDate.AddDays(graceDays);
        var active = sub.Status != SubscriptionStatus.Cancelled
                     && sub.Status != SubscriptionStatus.Suspended
                     && (sub.Status != SubscriptionStatus.Expired || inGrace);
        return new EffectivePlan(
            Source: "Company",
            IsActive: active,
            InGrace: inGrace,
            Status: sub.Status,
            EndDate: sub.EndDate,
            MaxUsers: sub.MaxUsers,
            MaxCompanies: sub.MaxCompanies,
            MaxDocumentsPerMonth: sub.MaxDocumentsPerMonth,
            MaxJournalEntriesPerMonth: sub.MaxJournalEntriesPerMonth,
            MaxStorageBytes: sub.MaxStorageBytes,
            MaxOcrPagesPerMonth: sub.MaxOcrPagesPerMonth,
            AzureOcrPagesPerMonth: sub.AzureOcrPagesPerMonth,
            LocalOcrPagesPerMonth: sub.LocalOcrPagesPerMonth,
            EnabledFeatures: sub.EnabledFeatures,
            Plan: sub.Plan);
    }

    public async Task<bool> CheckUsageLimitAsync(Guid companyId, string limitType)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null) return false;

        // CheckUsageLimitAsync เป็น method "อ่าน" ที่มี write แฝง 2 จุด (self-heal
        // + monthly reset ซึ่ง SaveChanges). DbContext เป็น scoped ตัวเดียวกับ
        // caller — ถ้า caller มี entity ค้างสถานะ Added/Modified อยู่ (เช่นเช็ค
        // สิทธิ์กลางการประกอบเอกสารก่อน save เอง) SaveChanges ที่นี่จะ flush ของ
        // caller ลง DB ก่อนเวลา → partial write ถ้า caller ตัดสินใจ abort ทีหลัง.
        // Guard: เขียนเฉพาะตอน tracker สะอาด (ไม่มี pending อื่น) — รอบที่ข้าม
        // จะถูกซ่อมในการเช็คครั้งถัดไปที่ tracker สะอาดแทน
        var trackerClean = !_db.ChangeTracker.Entries()
            .Any(e => e.State is Microsoft.EntityFrameworkCore.EntityState.Added
                or Microsoft.EntityFrameworkCore.EntityState.Modified
                or Microsoft.EntityFrameworkCore.EntityState.Deleted);

        // Self-heal: subscription ที่ถูกสร้างช่วงบั๊ก "แพ็กเกจฟรีถาวรดึง Trial*"
        // ค้าง limit = 0 → สร้างเอกสาร/ลงบัญชีไม่ได้เลยตั้งแต่สมัคร. sync จาก
        // template ทันทีที่มีการเช็คสิทธิ์ — ลูกค้าเก่าหายเองหลัง deploy ไม่ต้อง
        // รอ admin ไล่แก้มือทีละราย
        if (trackerClean
            && (sub.MaxDocumentsPerMonth <= 0 || sub.MaxJournalEntriesPerMonth <= 0
                || sub.MaxUsers <= 0 || sub.MaxStorageBytes <= 0)
            && (sub.Status == SubscriptionStatus.Trial || sub.Status == SubscriptionStatus.Active))
        {
            await ResyncZeroLimitsFromTemplateAsync(sub);
        }

        // Reset per-company monthly usage when the month rolls. Per-company
        // counters stay so the UI can show "Company X used 230 / 1,000" —
        // the AGGREGATE is what we enforce against when on an Account Plan.
        // ⚠️ ต้องล้าง **ตัวนับทุกตัว** ที่วันรีเซ็ตนี้คุม (รวม OCR) — เดิมจุดนี้ล้างแค่เอกสาร/JE
        // แล้วเลื่อนวันรีเซ็ต ⇒ ตัวนับ OCR ไม่เคยถูกล้าง สะสมข้ามเดือนจนผู้ใช้สแกนไม่ได้
        // (เจ้าของรายงาน 2026-09-24) — ดู Helpers/SubscriptionUsageRollover
        if (Accounting.Helpers.SubscriptionUsageRollover.RollIfDue(sub, DateTime.UtcNow))
        {
            // persist เฉพาะตอน tracker สะอาด (ดู trackerClean ด้านบน) — ค่าใน
            // memory ถูก reset แล้ว การเช็คด้านล่างใช้ค่าใหม่ถูกต้องเสมอ; แถวจะ
            // ถูก save จริงในรอบถัดไป/ตอน caller save เอง (sub เป็น tracked entity)
            if (trackerClean) await _db.SaveChangesAsync();
        }

        // Account Plan path — aggregate across every Company under the same
        // AccountSubscription. The accountant case ("1 Pro plan covers 10
        // client books") needs total docs across all 10 to stay under the
        // plan limit, not 10× the limit.
        if (sub.AccountSubscriptionId.HasValue)
        {
            var agg = await GetAggregateUsageAsync(sub.AccountSubscriptionId.Value);
            if (agg == null) return false;
            return limitType switch
            {
                // ฝ่ายค้านรอบ 205 ข้อ 1: ส่งยอดรวมกลุ่ม (ฐานเดียวกับการหักโบนัสใน IncrementUsageAsync — DocumentQuotaPolicy.Basis)
                "document" => agg.Documents < Accounting.Helpers.DocumentQuotaPolicy.EffectiveLimit(
                    agg.MaxDocuments, sub.DocumentBonusQuota, sub.DocumentBonusExpiresAt, DateTime.UtcNow, used: agg.Documents),
                "journal" => agg.JournalEntries < agg.MaxJournalEntries,
                "storage" => agg.StorageBytes < agg.MaxStorageBytes,
                _ => true
            };
        }

        // Per-company plan path — original behavior.
        // storage: คิดจากของจริง ไม่ใช่ Subscription.CurrentStorageUsed ที่ไม่มีใครเขียน (เดิมผ่านเสมอ — รอบ 193 ข้อ 30)
        if (limitType == "storage")
            return await StorageBytesForPoolAsync(new List<Guid> { sub.CompanyId }) < sub.MaxStorageBytes;
        return limitType switch
        {
            "document" => sub.CurrentMonthDocuments < Accounting.Helpers.DocumentQuotaPolicy.EffectiveLimit(
                sub.MaxDocumentsPerMonth, sub.DocumentBonusQuota, sub.DocumentBonusExpiresAt, DateTime.UtcNow,
                used: sub.CurrentMonthDocuments),
            "journal" => sub.CurrentMonthJournalEntries < sub.MaxJournalEntriesPerMonth,
            _ => true
        };
    }

    /// <summary>ซ่อม subscription ที่ counter หลักเป็น 0 (สร้างช่วงบั๊กแพ็กเกจ
    /// ฟรีถาวรดึงคอลัมน์ Trial*) — เติมจาก template ของ plan ปัจจุบันด้วยลำดับ
    /// เดียวกับ StartTrialAsync: ฟรีถาวร → Max* ก่อน, trial → Trial* ก่อน,
    /// สุดท้าย fallback ค่า default. แตะเฉพาะ field ที่เป็น 0 (ไม่ทับค่าที่ admin
    /// ตั้งเองไว้แล้ว). best-effort — ล้มเหลวเงียบ ไม่ block เส้นเช็คสิทธิ์.</summary>
    private async Task ResyncZeroLimitsFromTemplateAsync(Subscription sub)
    {
        try
        {
            var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == sub.Plan && p.IsActive);
            static int Pick(int primary, int secondary, int fallback)
                => primary > 0 ? primary : secondary > 0 ? secondary : fallback;
            var freeMode = sub.IsPermanentFree || (template?.IsPermanentFree ?? false);

            if (sub.MaxUsers <= 0)
                sub.MaxUsers = freeMode
                    ? Pick(template?.MaxUsers ?? 0, template?.TrialMaxUsers ?? 0, 2)
                    : Pick(template?.TrialMaxUsers ?? 0, template?.MaxUsers ?? 0, 2);
            if (sub.MaxDocumentsPerMonth <= 0)
                sub.MaxDocumentsPerMonth = freeMode
                    ? Pick(template?.MaxDocumentsPerMonth ?? 0, template?.TrialMaxDocumentsPerMonth ?? 0, 20)
                    : Pick(template?.TrialMaxDocumentsPerMonth ?? 0, template?.MaxDocumentsPerMonth ?? 0, 20);
            if (sub.MaxJournalEntriesPerMonth <= 0)
                sub.MaxJournalEntriesPerMonth = freeMode
                    ? Pick(template?.MaxJournalEntriesPerMonth ?? 0, template?.TrialMaxJournalEntriesPerMonth ?? 0, 50)
                    : Pick(template?.TrialMaxJournalEntriesPerMonth ?? 0, template?.MaxJournalEntriesPerMonth ?? 0, 50);
            if (sub.MaxStorageBytes <= 0)
                sub.MaxStorageBytes = (template?.MaxStorageBytes ?? 0) > 0
                    ? template!.MaxStorageBytes : 50L * 1024 * 1024;
            if (sub.MaxCompanies <= 0) sub.MaxCompanies = 1;

            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = "LimitsSelfHealed",
                Notes = $"Limit หลักเป็น 0 → resync จาก template {sub.Plan} " +
                        $"(users={sub.MaxUsers}, docs={sub.MaxDocumentsPerMonth}, journals={sub.MaxJournalEntriesPerMonth})",
                PerformedBy = "system"
            });
            await _db.SaveChangesAsync();
        }
        catch { /* best-effort — เส้นเช็คสิทธิ์ต้องไม่ล้มเพราะการซ่อม */ }
    }

    public async Task<bool> CanFitStorageAsync(Guid companyId, long additionalBytes)
    {
        if (additionalBytes < 0) return true;
        var status = await GetStorageStatusAsync(companyId);
        if (status == null) return false;
        return status.UsedBytes + additionalBytes <= status.MaxBytes;
    }

    /// <summary>พื้นที่ที่ใช้จริง + เพดานของ pool (บริษัทเดี่ยว หรือทุกบริษัทใต้ License เดียวกัน) —
    /// ตัวเดียวที่ <see cref="CanFitStorageAsync"/> (CMS บล็อก) และเส้นไฟล์แนบ (เตือน ไม่บล็อก — รอบ 193 ข้อ 30) ใช้
    /// · <c>null</c> = ไม่มี subscription (ไม่รู้เพดาน)</summary>
    public async Task<StorageStatus?> GetStorageStatusAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
        if (sub == null) return null;

        long maxBytes;
        List<Guid> poolCompanyIds;
        if (sub.AccountSubscriptionId.HasValue)
        {
            var acct = await _db.AccountSubscriptions.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
            if (acct == null)
            {
                // Dangling pointer — fall back to per-company so the user isn't
                // accidentally locked out by a deleted License.
                maxBytes = sub.MaxStorageBytes;
                poolCompanyIds = new List<Guid> { companyId };
            }
            else
            {
                maxBytes = acct.MaxStorageBytes;
                poolCompanyIds = await _db.Subscriptions.AsNoTracking()
                    .Where(s => s.AccountSubscriptionId == acct.Id && !s.IsDeleted)
                    .Select(s => s.CompanyId)
                    .ToListAsync();
            }
        }
        else
        {
            maxBytes = sub.MaxStorageBytes;
            poolCompanyIds = new List<Guid> { companyId };
        }

        // เดิมอ่าน Subscriptions.CurrentStorageUsed ซึ่ง**ไม่มีใครเขียนเลยทั้งเรพ** ⇒ 0 เสมอ
        // ⇒ ด่านนี้ผ่านทุกครั้ง (ด่านที่เขียนไว้แต่ไม่มีอินพุตจริง = ไม่มีด่าน). ใช้สูตรเดียว
        // กับที่หน้าจอโชว์ (StorageBytesForPoolAsync) — ไฟล์บัญชี + สื่อ CMS แชร์งบเดียวกัน
        var usedBytes = await StorageBytesForPoolAsync(poolCompanyIds);
        return new StorageStatus(usedBytes, maxBytes);
    }

    /// <summary>พื้นที่ที่ใช้จริง<b>รายบริษัท</b> (ไฟล์แนบ + สื่อ CMS) — แทนการอ่านคอลัมน์
    /// <c>Subscription.CurrentStorageUsed</c> ที่ไม่มีใครเขียน (หน้าแพ็กเกจกลุ่มบริษัท/พอร์ทัลแอดมินเคยโชว์ 0 ทุกบริษัท)</summary>
    public async Task<Dictionary<Guid, long>> GetStorageBytesByCompanyAsync(IReadOnlyCollection<Guid> companyIds)
    {
        var ids = companyIds.Distinct().ToList();
        var result = ids.ToDictionary(id => id, _ => 0L);
        if (ids.Count == 0) return result;
        var files = await _db.FileAttachments.AsNoTracking()
            .Where(f => ids.Contains(f.CompanyId))
            .GroupBy(f => f.CompanyId)
            .Select(g => new { CompanyId = g.Key, Bytes = g.Sum(f => f.FileSize) })
            .ToListAsync();
        foreach (var f in files) result[f.CompanyId] += f.Bytes;
        var cms = await _db.Sites.AsNoTracking()
            .Where(s => ids.Contains(s.CompanyId) && !s.IsDeleted)
            .GroupBy(s => s.CompanyId)
            .Select(g => new { CompanyId = g.Key, Bytes = g.Sum(s => s.CurrentStorageUsed) })
            .ToListAsync();
        foreach (var c in cms) result[c.CompanyId] += c.Bytes;
        return result;
    }

    public record AggregateUsage(
        int Documents, int MaxDocuments,
        int JournalEntries, int MaxJournalEntries,
        long StorageBytes, long MaxStorageBytes,
        int OcrPages, int MaxOcrPages,
        int CompaniesUsed, int MaxCompanies);

    /// <summary>Sum per-month counters across every Company under an
    /// AccountSubscription. Drives the aggregate quota check + the usage
    /// gauge on /account-subscription. Null when the account row is gone.</summary>
    public async Task<AggregateUsage?> GetAggregateUsageAsync(Guid accountSubscriptionId)
    {
        var acct = await _db.AccountSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountSubscriptionId && !a.IsDeleted);
        if (acct == null) return null;

        var aggNow = DateTime.UtcNow;
        var subs = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.AccountSubscriptionId == accountSubscriptionId && !s.IsDeleted)
            .Select(s => new {
                s.CurrentMonthDocuments,
                s.CurrentMonthJournalEntries,
                s.CompanyId,
                s.CurrentMonthOcrPages,
                s.UsageResetDate,
            })
            .ToListAsync();
        var poolStorage = await StorageBytesForPoolAsync(subs.Select(s => s.CompanyId).ToList());

        // counter รายเดือนของบริษัทพี่น้องที่ "ยังไม่ถูก reset" (เดือนใหม่แล้วแต่
        // ไม่มีใครเรียกเช็ค limit ของบริษัทนั้น) ห้ามนับรวม — ไม่งั้น usage ตกค้าง
        // เดือนก่อนกินโควตาเดือนนี้ → ผู้ใช้โดน block ทั้งที่โควตาว่าง
        bool Stale(DateTime resetAt) => aggNow >= resetAt;
        return new AggregateUsage(
            Documents: subs.Sum(s => Stale(s.UsageResetDate) ? 0 : s.CurrentMonthDocuments),
            MaxDocuments: acct.MaxDocumentsPerMonth,
            JournalEntries: subs.Sum(s => Stale(s.UsageResetDate) ? 0 : s.CurrentMonthJournalEntries),
            MaxJournalEntries: acct.MaxJournalEntriesPerMonth,
            // storage ไม่ใช่รายเดือน — นับเสมอ · คิดจากของจริง (Σ ไฟล์แนบ + สื่อ CMS ของ pool) ไม่ใช่
            // Subscription.CurrentStorageUsed ที่ไม่มีใครเขียน (เดิมหน้าแพ็กเกจกลุ่มโชว์ 0 ตลอด — รอบ 193 ข้อ 30)
            StorageBytes: poolStorage,
            MaxStorageBytes: acct.MaxStorageBytes,
            OcrPages: subs.Sum(s => Stale(s.UsageResetDate) ? 0 : s.CurrentMonthOcrPages),
            MaxOcrPages: acct.MaxOcrPagesPerMonth,
            CompaniesUsed: subs.Count,
            MaxCompanies: acct.MaxCompanies);
    }

    public async Task IncrementUsageAsync(Guid companyId, string usageType)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (sub == null) return;
        int crossedLevel = 0, crossedUsed = 0, crossedLimit = 0;

        switch (usageType)
        {
            case "document":
            {
                var nowUtc = DateTime.UtcNow;
                // ฝ่ายค้านรอบ 205 ข้อ 1: ฐานนับตัวเดียวกับ CheckUsageLimitAsync — แพ็กเกจกลุ่ม = ยอดรวมกลุ่ม/เพดานกลุ่ม (ยอดรวมอ่านจากฐาน ⇒ ก่อนใบนี้)
                var agg = sub.AccountSubscriptionId.HasValue ? await GetAggregateUsageAsync(sub.AccountSubscriptionId.Value) : null;
                var (usedBefore, planLimit) = Accounting.Helpers.DocumentQuotaPolicy.Basis(agg != null,
                    sub.CurrentMonthDocuments, sub.MaxDocumentsPerMonth, agg?.Documents ?? 0, agg?.MaxDocuments ?? 0);
                var limitBefore = Accounting.Helpers.DocumentQuotaPolicy.EffectiveLimit(planLimit,
                    sub.DocumentBonusQuota, sub.DocumentBonusExpiresAt, nowUtc, used: usedBefore);
                // รอบ 205 ทีม W1 (R-05 · BOOKING_REVENUE_AUDIT Q-6): ตัวนับเพิ่มแบบ atomic ในฐาน (UPDATE … SET n = n + 1 RETURNING) —
                // เดิมอ่าน-แก้-เขียนบน entity ⇒ สองใบที่สร้างพร้อมกันคนละคำขอเขียนทับกัน นับขาด 1 · อยู่ในธุรกรรมของผู้เรียก (ใบล้ม = rollback = ไม่นับ) ·
                // ค่าที่อ่านกลับถูกตั้งเป็น "ค่าเดิม" ของ entity ⇒ SaveChanges ด้านล่างไม่เขียนทับคอลัมน์นี้ซ้ำ (ไม่งั้นค่าของคำขอที่ชนกันหายอีกรอบ)
                var companyCounterAfter = await IncrementDocumentCounterAtomicAsync(sub);
                var usedAfter = agg != null ? usedBefore + 1 : companyCounterAfter;
                // โบนัสเอกสาร (top-up ที่ซื้อ · แอดมินให้ · ภารกิจแลกโควตา §12)
                // **ต้องถูกใช้ให้หมดไป** ไม่ใช่แค่ยกเพดานค้างไว้ — ถ้าไม่หัก ผู้ที่
                // ซื้อ +100 ใบครั้งเดียวจะได้ +100 ใบ **ทุกเดือน** จนโบนัสหมดอายุ
                // (counter รายเดือนถูกรีเซ็ต แต่โบนัสไม่ถูกแตะ) = แจกฟรีโดยไม่ตั้งใจ.
                // หักเฉพาะใบที่ **เกินโควตาแพ็กเกจล้วน ๆ** แล้ว — ใบที่ยังอยู่ในโควตา
                // ปกติไม่กินโบนัส (แบบเดียวกับ OcrBonusPages ใน OcrQuotaService) ·
                // รอบ 205 ทีม E: ตัวตัดสิน + สูตรเพดานคู่กันใน DocumentQuotaPolicy (เดิมหัก + เทียบ plan+คงเหลือ = โบนัสหายสองเท่า)
                if (Accounting.Helpers.DocumentQuotaPolicy.ConsumesBonus(planLimit, usedAfter,
                        sub.DocumentBonusQuota, sub.DocumentBonusExpiresAt, nowUtc))
                {
                    sub.DocumentBonusQuota--;
                }
                var limitAfter = Accounting.Helpers.DocumentQuotaPolicy.EffectiveLimit(planLimit,
                    sub.DocumentBonusQuota, sub.DocumentBonusExpiresAt, nowUtc, used: usedAfter);
                crossedLevel = Accounting.Helpers.DocumentQuotaPolicy.CrossedWarnLevel(usedBefore, limitBefore,
                    usedAfter, limitAfter);
                crossedUsed = usedAfter; crossedLimit = limitAfter;
                break;
            }
            case "journal":
                sub.CurrentMonthJournalEntries++;
                break;
        }

        await _db.SaveChangesAsync();

        // รอบ 205 ทีม E (BOOKING_REVENUE_AUDIT Q-3): ข้ามระดับ 80%/100% ⇒ แจ้งเจ้าของครั้งเดียวต่อระดับต่อเดือน — เดิมรู้ได้ทางเดียวคือเปิดหน้า
        // "ส่วนเสริมของฉัน" เอง ขณะที่ใบจากการจอง/ชำระออนไลน์ยังออกต่อ (กฎหมายบังคับ) แล้วไปโผล่เป็นค่าส่วนเกินในบิล ·
        // ฝ่ายค้านรอบ 205 ข้อ 3: อยู่ในธุรกรรมของผู้เรียก (CreateDocumentAsync) ⇒ เก็บไว้ก่อน แล้วผู้เรียกส่งหลัง commit
        // (FlushPendingQuotaAlertsAsync) — ใบถูกถอยกลับ = ไม่แจ้ง · ไม่มีธุรกรรม ⇒ ส่งทันที
        if (crossedLevel > 0)
        {
            _pendingQuotaAlert = (sub.CompanyId, crossedLevel, sub.CurrentMonthDocuments, crossedUsed, crossedLimit);
            if (_db.Database.CurrentTransaction == null) await FlushPendingQuotaAlertsAsync();
        }
    }

    /// <summary>R-05: <c>UPDATE "Subscriptions" SET "CurrentMonthDocuments" = n + 1 … RETURNING</c> บนแถวของบริษัทนี้ แล้วซิงก์ค่ากลับเข้า entity
    /// โดยไม่ทำให้คอลัมน์ถูกมองว่า "แก้" (OriginalValue = CurrentValue) · ต้องเรียกใน scope ที่ผู้เรียกจะ SaveChanges ต่อ (ธุรกรรมเดียวกับการสร้างใบ)
    /// · ไม่มีธุรกรรม = autocommit ของคำสั่งเดียวก็ยัง atomic</summary>
    private async Task<int> IncrementDocumentCounterAtomicAsync(Subscription sub)
    {
        // ADO.NET ตรง (แบบ ChatRateLimiter) — ไม่ผ่าน LINQ composer ที่อาจห่อ UPDATE…RETURNING เป็น subquery (PostgreSQL ไม่รับ) ·
        // บนคอนเนกชัน/ธุรกรรมปัจจุบันของ DbContext ⇒ rollback ของผู้เรียกถอยตัวนับด้วย
        const string sql = """
            UPDATE "Subscriptions" SET "CurrentMonthDocuments" = "CurrentMonthDocuments" + 1
            WHERE "Id" = @id
            RETURNING "CurrentMonthDocuments";
            """;
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        int after;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
            var pid = cmd.CreateParameter();
            pid.ParameterName = "id";
            pid.Value = sub.Id;
            cmd.Parameters.Add(pid);
            var result = await cmd.ExecuteScalarAsync();
            after = result is int i ? i : (result == null ? sub.CurrentMonthDocuments + 1 : Convert.ToInt32(result));
        }
        var prop = _db.Entry(sub).Property(x => x.CurrentMonthDocuments);
        prop.CurrentValue = after;
        prop.OriginalValue = after;
        prop.IsModified = false;
        return after;
    }

    /// <summary>แจ้งเตือนโควตาที่ค้างจาก <see cref="IncrementUsageAsync"/> — เรียกหลัง commit ธุรกรรมที่นับใบ · ส่งเฉพาะเมื่อตัวนับในฐานถึงค่าที่นับไว้จริง
    /// (ธุรกรรมถูกถอย ⇒ ตัวนับในฐานต่ำกว่า ⇒ ทิ้ง ไม่แจ้งว่า "เต็ม" ทั้งที่ใบไม่ถูกสร้าง)</summary>
    public async Task FlushPendingQuotaAlertsAsync()
    {
        if (_pendingQuotaAlert is not { } p) return;
        _pendingQuotaAlert = null;
        var persisted = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.CompanyId == p.CompanyId && !s.IsDeleted)
            .Select(s => (int?)s.CurrentMonthDocuments)
            .FirstOrDefaultAsync() ?? 0;
        if (persisted < p.CompanyCounter) return;
        await NotifyDocumentQuotaCrossedAsync(p.CompanyId, p.Level, p.Used, p.Limit);
    }

    /// <summary>ระดับที่ข้ามแล้วรอส่งหลัง commit (scoped ต่อคำขอ) · <c>CompanyCounter</c> = ตัวนับของบริษัทหลังนับใบนี้ (ใช้ตรวจว่า commit จริง)</summary>
    private (Guid CompanyId, int Level, int CompanyCounter, int Used, int Limit)? _pendingQuotaAlert;

    private async Task NotifyDocumentQuotaCrossedAsync(Guid companyId, int level, int used, int limit)
    {
        try
        {
            var ownerUserId = await _db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId)
                .FirstOrDefaultAsync();
            if (ownerUserId == Guid.Empty) return;
            await NotifyUserAsync(ownerUserId, companyId,
                Models.Constants.NotificationEvents.DocumentQuotaLevel, NotificationType.System,
                level >= 2 ? "โควตาเอกสารเดือนนี้เต็มแล้ว" : "โควตาเอกสารใกล้เต็ม",
                Accounting.Helpers.DocumentQuotaPolicy.CrossedMessage(level, used, limit),
                "/pages/addons.html", "Subscription", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "แจ้งเจ้าของเรื่องโควตาเอกสาร (ระดับ {Level}) ไม่สำเร็จ — บริษัท {Company}", level, companyId);
        }
    }


    // ==================== Admin: Plan Templates ====================

    public async Task<PlanTemplateResponse> CreatePlanTemplateAsync(CreatePlanTemplateRequest request)
    {
        var template = new PlanTemplate
        {
            Name = request.Name,
            Description = request.Description,
            Plan = request.Plan,
            MonthlyPrice = request.MonthlyPrice,
            QuarterlyPrice = request.QuarterlyPrice ?? request.MonthlyPrice * 3 * 0.95m,
            SemiAnnualPrice = request.SemiAnnualPrice ?? request.MonthlyPrice * 6 * 0.9m,
            AnnualPrice = request.AnnualPrice ?? request.MonthlyPrice * 12 * 0.85m,
            MaxUsers = request.MaxUsers,
            MaxCompanies = request.MaxCompanies,
            MaxDocumentsPerMonth = request.MaxDocumentsPerMonth,
            MaxJournalEntriesPerMonth = request.MaxJournalEntriesPerMonth,
            MaxStorageBytes = request.MaxStorageBytes,
            EnabledFeatures = request.EnabledFeatures,
            TrialDurationDays = request.TrialDurationDays,
            TrialMaxExtensions = request.TrialMaxExtensions,
            TrialExtensionDays = request.TrialExtensionDays,
            TrialFeatures = request.TrialFeatures,
            TrialMaxUsers = request.TrialMaxUsers,
            TrialMaxDocumentsPerMonth = request.TrialMaxDocumentsPerMonth,
            TrialMaxJournalEntriesPerMonth = request.TrialMaxJournalEntriesPerMonth,
            TrialBlockOnExpiry = request.TrialBlockOnExpiry,
            TrialGracePeriodDays = request.TrialGracePeriodDays,
            IsPermanentFree = request.IsPermanentFree,
            MaxSharedMessagesPerMonth = request.MaxSharedMessagesPerMonth,
            TrialMaxSharedMessagesPerMonth = request.TrialMaxSharedMessagesPerMonth,
        };

        _db.PlanTemplates.Add(template);
        await _db.SaveChangesAsync();

        return MapTemplateToResponse(template);
    }

    public async Task<List<PlanTemplateResponse>> GetPlanTemplatesAsync(bool includeInactive = false)
    {
        var query = _db.PlanTemplates.AsQueryable();
        if (!includeInactive)
            query = query.Where(p => p.IsActive);

        var templates = await query.OrderBy(p => p.Plan).ToListAsync();
        return templates.Select(MapTemplateToResponse).ToList();
    }

    public async Task<PlanTemplateResponse> UpdatePlanTemplateAsync(Guid templateId, UpdatePlanTemplateRequest request)
    {
        var template = await _db.PlanTemplates.FindAsync(templateId)
            ?? throw new KeyNotFoundException("ไม่พบ plan template");

        // Track which fields changed so we can propagate to existing subscriptions
        var oldEnabled = template.EnabledFeatures;
        var oldTrial = template.TrialFeatures;
        var oldMaxUsers = template.MaxUsers;
        var oldMaxCompanies = template.MaxCompanies;
        var oldMaxDocs = template.MaxDocumentsPerMonth;
        var oldMaxJournals = template.MaxJournalEntriesPerMonth;
        var oldMaxStorage = template.MaxStorageBytes;
        var oldTrialMaxUsers = template.TrialMaxUsers;
        var oldTrialMaxDocs = template.TrialMaxDocumentsPerMonth;
        var oldTrialMaxJournals = template.TrialMaxJournalEntriesPerMonth;
        var oldGrace = template.TrialGracePeriodDays;
        var oldBlock = template.TrialBlockOnExpiry;
        // OCR quota snapshot — needed so a template-side change propagates
        // to every existing subscription on this plan (so the user-facing
        // quota banner immediately reflects "5000 หน้า/เดือน" instead of
        // still showing the old 10 they got when they were on Trial).
        var oldMaxOcr = template.MaxOcrPagesPerMonth;
        var oldAzureOcr = template.AzureOcrPagesPerMonth;
        var oldLocalOcr = template.LocalOcrPagesPerMonth;
        var oldFallback = template.FallbackToLocalWhenAzureExhausted;
        var oldTrialMaxOcr = template.TrialMaxOcrPagesPerMonth;

        if (request.Name != null) template.Name = request.Name;
        if (request.Description != null) template.Description = request.Description;
        if (request.MonthlyPrice.HasValue) template.MonthlyPrice = request.MonthlyPrice.Value;
        if (request.QuarterlyPrice.HasValue) template.QuarterlyPrice = request.QuarterlyPrice.Value;
        if (request.SemiAnnualPrice.HasValue) template.SemiAnnualPrice = request.SemiAnnualPrice.Value;
        if (request.AnnualPrice.HasValue) template.AnnualPrice = request.AnnualPrice.Value;
        if (request.MaxUsers.HasValue) template.MaxUsers = request.MaxUsers.Value;
        if (request.MaxCompanies.HasValue) template.MaxCompanies = request.MaxCompanies.Value;
        if (request.MaxDocumentsPerMonth.HasValue) template.MaxDocumentsPerMonth = request.MaxDocumentsPerMonth.Value;
        if (request.MaxJournalEntriesPerMonth.HasValue) template.MaxJournalEntriesPerMonth = request.MaxJournalEntriesPerMonth.Value;
        if (request.MaxStorageBytes.HasValue) template.MaxStorageBytes = request.MaxStorageBytes.Value;

        // Resolve features: prefer NameList over enum value if provided
        if (request.EnabledFeatureNames != null)
            template.EnabledFeatures = FeatureFlagsHelper.FromNameList(request.EnabledFeatureNames);
        else if (request.EnabledFeatures.HasValue)
            template.EnabledFeatures = request.EnabledFeatures.Value;

        if (request.TrialFeatureNames != null)
            template.TrialFeatures = FeatureFlagsHelper.FromNameList(request.TrialFeatureNames);
        else if (request.TrialFeatures.HasValue)
            template.TrialFeatures = request.TrialFeatures.Value;

        if (request.IsActive.HasValue) template.IsActive = request.IsActive.Value;
        if (request.TrialDurationDays.HasValue) template.TrialDurationDays = request.TrialDurationDays.Value;
        if (request.TrialMaxExtensions.HasValue) template.TrialMaxExtensions = request.TrialMaxExtensions.Value;
        if (request.TrialExtensionDays.HasValue) template.TrialExtensionDays = request.TrialExtensionDays.Value;
        if (request.TrialMaxUsers.HasValue) template.TrialMaxUsers = request.TrialMaxUsers.Value;
        if (request.TrialMaxDocumentsPerMonth.HasValue) template.TrialMaxDocumentsPerMonth = request.TrialMaxDocumentsPerMonth.Value;
        if (request.TrialMaxJournalEntriesPerMonth.HasValue) template.TrialMaxJournalEntriesPerMonth = request.TrialMaxJournalEntriesPerMonth.Value;
        if (request.TrialBlockOnExpiry.HasValue) template.TrialBlockOnExpiry = request.TrialBlockOnExpiry.Value;
        if (request.TrialGracePeriodDays.HasValue) template.TrialGracePeriodDays = request.TrialGracePeriodDays.Value;
        if (request.IsPermanentFree.HasValue) template.IsPermanentFree = request.IsPermanentFree.Value;

        // OCR per-engine quotas. Null preserves the existing value so a UI
        // that only sends the field for plans that customize it doesn't
        // accidentally zero out other plans.
        // รอบ 212: เพดานข้อความช่องกลาง (ผู้อ่าน: NotifyQuotaService.LoadLimitAsync — อ่านจากแม่แบบตอนนับ ไม่คัดลอกลงบริษัท)
        // ค่าติดลบ = กลับเป็นไม่จำกัด (null) — หน้าแอดมินส่ง -1 เมื่อช่องว่าง
        if (request.MaxSharedMessagesPerMonth.HasValue) template.MaxSharedMessagesPerMonth = request.MaxSharedMessagesPerMonth.Value < 0 ? null : request.MaxSharedMessagesPerMonth.Value;
        if (request.TrialMaxSharedMessagesPerMonth.HasValue) template.TrialMaxSharedMessagesPerMonth = request.TrialMaxSharedMessagesPerMonth.Value < 0 ? null : request.TrialMaxSharedMessagesPerMonth.Value;
        if (request.MaxOcrPagesPerMonth.HasValue) template.MaxOcrPagesPerMonth = request.MaxOcrPagesPerMonth.Value;
        if (request.AzureOcrPagesPerMonth.HasValue) template.AzureOcrPagesPerMonth = request.AzureOcrPagesPerMonth.Value;
        if (request.LocalOcrPagesPerMonth.HasValue) template.LocalOcrPagesPerMonth = request.LocalOcrPagesPerMonth.Value;
        if (request.FallbackToLocalWhenAzureExhausted.HasValue) template.FallbackToLocalWhenAzureExhausted = request.FallbackToLocalWhenAzureExhausted.Value;
        if (request.TrialMaxOcrPagesPerMonth.HasValue) template.TrialMaxOcrPagesPerMonth = request.TrialMaxOcrPagesPerMonth.Value;

        await _db.SaveChangesAsync();

        // Propagate changes (features + limits + trial config) to all subscriptions
        // of this plan so users see updates without re-subscribing.
        var featuresChanged = template.EnabledFeatures != oldEnabled || template.TrialFeatures != oldTrial;
        var paidLimitsChanged = template.MaxUsers != oldMaxUsers
            || template.MaxCompanies != oldMaxCompanies
            || template.MaxDocumentsPerMonth != oldMaxDocs
            || template.MaxJournalEntriesPerMonth != oldMaxJournals
            || template.MaxStorageBytes != oldMaxStorage;
        var trialLimitsChanged = template.TrialMaxUsers != oldTrialMaxUsers
            || template.TrialMaxDocumentsPerMonth != oldTrialMaxDocs
            || template.TrialMaxJournalEntriesPerMonth != oldTrialMaxJournals
            || template.TrialGracePeriodDays != oldGrace
            || template.TrialBlockOnExpiry != oldBlock;
        // OCR-side change flag — split into "paid" and "trial" buckets so
        // we update only the relevant subscriptions per their status.
        var paidOcrChanged = template.MaxOcrPagesPerMonth != oldMaxOcr
            || template.AzureOcrPagesPerMonth != oldAzureOcr
            || template.LocalOcrPagesPerMonth != oldLocalOcr
            || template.FallbackToLocalWhenAzureExhausted != oldFallback;
        var trialOcrChanged = template.TrialMaxOcrPagesPerMonth != oldTrialMaxOcr;

        if (featuresChanged || paidLimitsChanged || trialLimitsChanged
            || paidOcrChanged || trialOcrChanged)
        {
            var subs = await _db.Subscriptions
                .Include(s => s.TrialConfig)
                .Where(s => s.Plan == template.Plan)
                .ToListAsync();
            foreach (var sub in subs)
            {
                if (sub.Status == SubscriptionStatus.Trial)
                {
                    if (featuresChanged) sub.EnabledFeatures = template.TrialFeatures;
                    if (trialLimitsChanged)
                    {
                        sub.MaxUsers = template.TrialMaxUsers;
                        sub.MaxDocumentsPerMonth = template.TrialMaxDocumentsPerMonth;
                        sub.MaxJournalEntriesPerMonth = template.TrialMaxJournalEntriesPerMonth;
                    }
                    // Trial subscriptions use the Trial OCR budget. Without
                    // this propagation an admin who edits the template sees
                    // their user still stuck on the old default of 10.
                    if (trialOcrChanged)
                    {
                        sub.MaxOcrPagesPerMonth = template.TrialMaxOcrPagesPerMonth;
                    }
                    if (sub.TrialConfig != null)
                    {
                        if (featuresChanged) sub.TrialConfig.TrialFeatures = template.TrialFeatures;
                        if (trialLimitsChanged)
                        {
                            sub.TrialConfig.TrialMaxUsers = template.TrialMaxUsers;
                            sub.TrialConfig.TrialMaxDocumentsPerMonth = template.TrialMaxDocumentsPerMonth;
                            sub.TrialConfig.TrialMaxJournalEntriesPerMonth = template.TrialMaxJournalEntriesPerMonth;
                            sub.TrialConfig.GracePeriodDays = template.TrialGracePeriodDays;
                            sub.TrialConfig.BlockAccessOnExpiry = template.TrialBlockOnExpiry;
                        }
                    }
                }
                else
                {
                    if (featuresChanged) sub.EnabledFeatures = template.EnabledFeatures;
                    if (paidLimitsChanged)
                    {
                        sub.MaxUsers = template.MaxUsers;
                        sub.MaxCompanies = template.MaxCompanies;
                        sub.MaxDocumentsPerMonth = template.MaxDocumentsPerMonth;
                        sub.MaxJournalEntriesPerMonth = template.MaxJournalEntriesPerMonth;
                        sub.MaxStorageBytes = template.MaxStorageBytes;
                    }
                    // Paid (Active/Past-due/etc.) subscriptions: bring the
                    // full OCR breakdown across. Total + per-engine + the
                    // fallback toggle — same set CreateSubscriptionAsync
                    // copies in for new subs.
                    if (paidOcrChanged)
                    {
                        sub.MaxOcrPagesPerMonth = template.MaxOcrPagesPerMonth;
                        sub.AzureOcrPagesPerMonth = template.AzureOcrPagesPerMonth;
                        sub.LocalOcrPagesPerMonth = template.LocalOcrPagesPerMonth;
                        sub.FallbackToLocalWhenAzureExhausted = template.FallbackToLocalWhenAzureExhausted;
                    }
                }

                // Propagate permanent-free flag — if template now is permanent free,
                // sync the subscription so existing customers benefit immediately
                if (sub.IsPermanentFree != template.IsPermanentFree)
                {
                    sub.IsPermanentFree = template.IsPermanentFree;
                    if (template.IsPermanentFree)
                    {
                        sub.Status = SubscriptionStatus.Active;
                        sub.EndDate = DateTime.UtcNow.AddYears(100);
                        sub.EnabledFeatures = template.EnabledFeatures;
                    }
                }
                sub.UpdatedAt = DateTime.UtcNow;
            }
            if (subs.Any()) await _db.SaveChangesAsync();
        }

        return MapTemplateToResponse(template);
    }

    /// <summary>
    /// Force-resync every subscription on the given plan against the
    /// template's current values — used when admin edited the template
    /// but a particular tenant's subscription was missed (e.g. status was
    /// hand-flipped in the DB and never went through UpgradeAsync). Same
    /// branch logic as UpdatePlanTemplateAsync: Trial subs take the Trial
    /// OCR budget; everyone else takes the per-engine paid breakdown.
    /// Returns the number of subscriptions touched.
    /// </summary>
    public async Task<int> ResyncSubscriptionsFromTemplateAsync(Guid templateId)
    {
        var template = await _db.PlanTemplates.FindAsync(templateId)
            ?? throw new KeyNotFoundException("ไม่พบ plan template");

        var subs = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .Where(s => s.Plan == template.Plan && !s.IsDeleted)
            .ToListAsync();

        foreach (var sub in subs)
        {
            if (sub.Status == SubscriptionStatus.Trial)
            {
                sub.EnabledFeatures = template.TrialFeatures;
                sub.MaxUsers = template.TrialMaxUsers;
                sub.MaxDocumentsPerMonth = template.TrialMaxDocumentsPerMonth;
                sub.MaxJournalEntriesPerMonth = template.TrialMaxJournalEntriesPerMonth;
                sub.MaxOcrPagesPerMonth = template.TrialMaxOcrPagesPerMonth;
                if (sub.TrialConfig != null)
                {
                    sub.TrialConfig.TrialFeatures = template.TrialFeatures;
                    sub.TrialConfig.TrialMaxUsers = template.TrialMaxUsers;
                    sub.TrialConfig.TrialMaxDocumentsPerMonth = template.TrialMaxDocumentsPerMonth;
                    sub.TrialConfig.TrialMaxJournalEntriesPerMonth = template.TrialMaxJournalEntriesPerMonth;
                    sub.TrialConfig.GracePeriodDays = template.TrialGracePeriodDays;
                    sub.TrialConfig.BlockAccessOnExpiry = template.TrialBlockOnExpiry;
                }
            }
            else
            {
                sub.EnabledFeatures = template.EnabledFeatures;
                sub.MaxUsers = template.MaxUsers;
                sub.MaxCompanies = template.MaxCompanies;
                sub.MaxDocumentsPerMonth = template.MaxDocumentsPerMonth;
                sub.MaxJournalEntriesPerMonth = template.MaxJournalEntriesPerMonth;
                sub.MaxStorageBytes = template.MaxStorageBytes;
                sub.MaxOcrPagesPerMonth = template.MaxOcrPagesPerMonth;
                sub.AzureOcrPagesPerMonth = template.AzureOcrPagesPerMonth;
                sub.LocalOcrPagesPerMonth = template.LocalOcrPagesPerMonth;
                sub.FallbackToLocalWhenAzureExhausted = template.FallbackToLocalWhenAzureExhausted;
            }
            sub.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return subs.Count;
    }

    // ==================== Admin: Direct Trial Config Update ====================

    public async Task<TrialStatusResponse> UpdateTrialConfigAsync(Guid companyId, UpdateTrialConfigRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        var tc = sub.TrialConfig
            ?? throw new InvalidOperationException("ไม่มี trial config");

        if (request.TrialDurationDays.HasValue)
        {
            tc.TrialDurationDays = request.TrialDurationDays.Value;
            tc.TrialEndDate = tc.TrialStartDate.AddDays(request.TrialDurationDays.Value + (tc.ExtensionsUsed * tc.ExtensionDays));
            sub.EndDate = tc.TrialEndDate;
        }
        if (request.AllowExtension.HasValue) tc.AllowExtension = request.AllowExtension.Value;
        if (request.MaxExtensions.HasValue) tc.MaxExtensions = request.MaxExtensions.Value;
        if (request.ExtensionDays.HasValue) tc.ExtensionDays = request.ExtensionDays.Value;
        if (request.TrialFeatures.HasValue)
        {
            tc.TrialFeatures = request.TrialFeatures.Value;
            sub.EnabledFeatures = request.TrialFeatures.Value;
        }
        if (request.TrialMaxUsers.HasValue)
        {
            tc.TrialMaxUsers = request.TrialMaxUsers.Value;
            sub.MaxUsers = request.TrialMaxUsers.Value;
        }
        if (request.TrialMaxDocumentsPerMonth.HasValue)
        {
            tc.TrialMaxDocumentsPerMonth = request.TrialMaxDocumentsPerMonth.Value;
            sub.MaxDocumentsPerMonth = request.TrialMaxDocumentsPerMonth.Value;
        }
        if (request.TrialMaxJournalEntriesPerMonth.HasValue)
        {
            tc.TrialMaxJournalEntriesPerMonth = request.TrialMaxJournalEntriesPerMonth.Value;
            sub.MaxJournalEntriesPerMonth = request.TrialMaxJournalEntriesPerMonth.Value;
        }
        if (request.TrialMaxCompanies.HasValue) tc.TrialMaxCompanies = request.TrialMaxCompanies.Value;
        if (request.ShowTrialWatermark.HasValue) tc.ShowTrialWatermark = request.ShowTrialWatermark.Value;
        if (request.TrialMessage != null) tc.TrialMessage = request.TrialMessage;
        if (request.NotifyBeforeExpiry.HasValue) tc.NotifyBeforeExpiry = request.NotifyBeforeExpiry.Value;
        if (request.NotifyDaysBeforeExpiry.HasValue) tc.NotifyDaysBeforeExpiry = request.NotifyDaysBeforeExpiry.Value;
        if (request.BlockAccessOnExpiry.HasValue) tc.BlockAccessOnExpiry = request.BlockAccessOnExpiry.Value;
        if (request.AllowDataExportAfterExpiry.HasValue) tc.AllowDataExportAfterExpiry = request.AllowDataExportAfterExpiry.Value;
        if (request.GracePeriodDays.HasValue) tc.GracePeriodDays = request.GracePeriodDays.Value;
        if (request.DeleteDataAfterGracePeriod.HasValue) tc.DeleteDataAfterGracePeriod = request.DeleteDataAfterGracePeriod.Value;
        if (request.DataRetentionDays.HasValue) tc.DataRetentionDays = request.DataRetentionDays.Value;
        if (request.DiscountPercentOnConversion.HasValue) tc.DiscountPercentOnConversion = request.DiscountPercentOnConversion.Value;
        if (request.DiscountValidUntil.HasValue) tc.DiscountValidUntil = request.DiscountValidUntil.Value;
        if (request.ConversionPromoCode != null) tc.ConversionPromoCode = request.ConversionPromoCode;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "TrialConfigUpdated",
            Notes = "Trial configuration updated by admin",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
        return await GetTrialStatusAsync(companyId);
    }

    // ==================== Usage Monitor ====================

    public async Task<UsageDetailResponse> GetUsageDetailAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription สำหรับบริษัทนี้");

        // Get company users
        var companyUsers = await _db.CompanyUsers
            .Where(cu => cu.CompanyId == companyId)
            .Join(_db.Users, cu => cu.UserId, u => u.Id, (cu, u) => new UsageUserInfo(
                u.Id, u.FullName, u.Email, cu.Role.ToString(), cu.JoinedAt, u.LastLoginAt))
            .ToListAsync();

        // Get file attachments for storage breakdown
        var files = await _db.FileAttachments
            .Where(f => f.CompanyId == companyId)
            .Select(f => new { f.Id, f.OriginalFileName, f.EntityType, f.FileSize, f.CreatedAt, f.UploadedByUserId })
            .ToListAsync();

        // Storage breakdown by entity type
        var breakdown = files
            .GroupBy(f => f.EntityType ?? "Other")
            .Select(g => new StorageCategoryInfo(
                g.Key,
                GetCategoryLabel(g.Key),
                g.Sum(f => f.FileSize),
                g.Count()))
            .OrderByDescending(c => c.Bytes)
            .ToList();

        // Top 10 largest files
        var uploaderIds = files.Where(f => f.UploadedByUserId is Guid g && g != Guid.Empty).Select(f => f.UploadedByUserId!.Value).Distinct().ToList();
        var userMap = await _db.Users
            .Where(u => uploaderIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var largest = files
            .OrderByDescending(f => f.FileSize)
            .Take(10)
            .Select(f => new StorageFileInfo(
                f.Id, f.OriginalFileName ?? "unknown", f.EntityType ?? "Other",
                f.FileSize, f.CreatedAt,
                f.UploadedByUserId is Guid up && userMap.TryGetValue(up, out var upName) ? upName : "-"))
            .ToList();

        // License overlay: when this company rides under a User License the
        // dashboard should show the License's limits (and aggregate Used)
        // not the stale per-company FreeTrial values. Otherwise the user
        // sees scary "1/1 users — upgrade now" warnings on a 100-seat
        // Enterprise account.
        var plan = sub.Plan;
        var status = sub.Status;
        var endDate = sub.EndDate;
        var maxUsers = sub.MaxUsers;
        var maxStorage = sub.MaxStorageBytes;
        var maxDocs = sub.MaxDocumentsPerMonth;
        var maxJournals = sub.MaxJournalEntriesPerMonth;
        var maxOcr = sub.MaxOcrPagesPerMonth;
        // ตัวเลข “ใช้ไป” ต้องมาจาก resolver ตัวเดียว (GetUsageCurrentAsync) — เดิมอ่าน
        // counter ที่ไม่มีใครเขียน (storage/journal) จึงเป็น 0 ตลอดกาล
        var current = await GetUsageCurrentAsync(sub);
        var storageUsed = current.StorageUsed;
        var docsUsed = current.DocumentsThisMonth;
        var journalsUsed = current.JournalEntriesThisMonth;
        var ocrUsed = sub.CurrentMonthOcrPages;

        if (sub.AccountSubscriptionId.HasValue)
        {
            var acct = await _db.AccountSubscriptions.AsNoTracking()
                .Include(a => a.PlanTemplate)
                .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
            if (acct != null)
            {
                plan = acct.PlanTemplate.Plan;
                status = acct.Status;
                endDate = acct.EndDate;
                maxUsers = acct.MaxUsersPerCompany;
                maxStorage = acct.MaxStorageBytes;
                maxDocs = acct.MaxDocumentsPerMonth;
                maxJournals = acct.MaxJournalEntriesPerMonth;
                maxOcr = acct.MaxOcrPagesPerMonth;
                // Used counters are aggregate across every attached company
                // so the dashboard's "X / Y" matches what enforcement sees.
                var agg = await _db.Subscriptions.AsNoTracking()
                    .Where(s => s.AccountSubscriptionId == acct.Id && !s.IsDeleted)
                    .Select(s => new { s.CurrentMonthOcrPages })
                    .ToListAsync();
                // docs/journals/storage รวม pool มาแล้วจาก GetUsageCurrentAsync — เหลือ OCR
                ocrUsed = agg.Sum(x => x.CurrentMonthOcrPages);
            }
        }

        // Build alerts
        var alerts = new List<UsageAlert>();
        var storagePct = maxStorage > 0 ? (double)storageUsed / maxStorage * 100 : 0;
        var docPct = maxDocs > 0 ? (double)docsUsed / maxDocs * 100 : 0;
        var userPct = maxUsers > 0 ? (double)companyUsers.Count / maxUsers * 100 : 0;

        if (storagePct >= 90)
            alerts.Add(new UsageAlert("danger", "storage", $"พื้นที่เก็บข้อมูลใช้ไป {storagePct:F0}% แล้ว กรุณาเคลียร์ไฟล์หรืออัปเกรดแพ็กเกจ"));
        else if (storagePct >= 70)
            alerts.Add(new UsageAlert("warning", "storage", $"พื้นที่เก็บข้อมูลใช้ไป {storagePct:F0}% แล้ว"));

        if (docPct >= 90)
            alerts.Add(new UsageAlert("danger", "documents", $"เอกสารเดือนนี้ใช้ไป {docsUsed}/{maxDocs} รายการ"));
        else if (docPct >= 70)
            alerts.Add(new UsageAlert("warning", "documents", $"เอกสารเดือนนี้ใช้ไป {docPct:F0}%"));

        if (userPct >= 100)
            alerts.Add(new UsageAlert("danger", "users", "จำนวนผู้ใช้เต็มแล้ว อัปเกรดแพ็กเกจเพื่อเพิ่มผู้ใช้"));
        else if (companyUsers.Count >= maxUsers - 1 && maxUsers < 999)
            alerts.Add(new UsageAlert("warning", "users", $"เหลือโควต้าผู้ใช้อีก {maxUsers - companyUsers.Count} คน"));

        var daysLeft = (endDate - DateTime.UtcNow).Days;
        if (daysLeft <= 0)
            alerts.Add(new UsageAlert("danger", "subscription", "Subscription หมดอายุแล้ว กรุณาต่ออายุ"));
        else if (daysLeft <= 7)
            alerts.Add(new UsageAlert("warning", "subscription", $"Subscription จะหมดอายุใน {daysLeft} วัน"));

        var ocrPct = maxOcr > 0 ? (double)ocrUsed / maxOcr * 100 : 0;
        if (ocrPct >= 90)
            alerts.Add(new UsageAlert("danger", "ocr", $"โควต้า OCR ใช้ไป {ocrUsed}/{maxOcr} หน้า"));
        else if (ocrPct >= 70)
            alerts.Add(new UsageAlert("warning", "ocr", $"โควต้า OCR ใช้ไป {ocrPct:F0}%"));

        var ocrCredits = await _db.OcrCreditPurchases
            .Where(p => p.CompanyId == companyId && p.Status == "Approved"
                && p.PagesRemaining > 0
                && (p.ExpiresAt == null || p.ExpiresAt > DateTime.UtcNow))
            .SumAsync(p => p.PagesRemaining);

        return new UsageDetailResponse(
            plan, status, endDate,
            companyUsers.Count, maxUsers, companyUsers,
            storageUsed, maxStorage, breakdown, largest,
            docsUsed, maxDocs,
            journalsUsed, maxJournals,
            sub.UsageResetDate,
            OcrPagesThisMonth: ocrUsed,
            MaxOcrPagesPerMonth: maxOcr,
            OcrBonusPages: sub.OcrBonusPages,
            OcrCreditPagesRemaining: ocrCredits,
            Alerts: alerts);
    }

    private static string GetCategoryLabel(string entityType)
    {
        return entityType switch
        {
            "Document" => "เอกสาร",
            "JournalEntry" => "รายการบัญชี",
            "Contact" => "ผู้ติดต่อ",
            "Product" => "สินค้า",
            "ExpenseClaim" => "เบิกค่าใช้จ่าย",
            "FixedAsset" => "สินทรัพย์ถาวร",
            "PayrollSlip" => "สลิปเงินเดือน",
            "Logo" => "โลโก้",
            _ => entityType
        };
    }

    // ==================== Background: Process Expired Trials ====================

    public async Task ProcessExpiredTrialsAsync()
    {
        var now = DateTime.UtcNow;
        var expiredTrials = await _db.Subscriptions
            .Include(s => s.TrialConfig)
            .Where(s => s.Status == SubscriptionStatus.Trial && s.EndDate < now && !s.IsPermanentFree)
            .ToListAsync();

        foreach (var sub in expiredTrials)
        {
            sub.Status = SubscriptionStatus.Expired;
            if (sub.TrialConfig != null)
                sub.TrialConfig.TrialStatus = TrialStatus.Expired;

            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = "TrialExpired",
                FromStatus = SubscriptionStatus.Trial,
                ToStatus = SubscriptionStatus.Expired,
                Notes = "Auto-expired by system",
                PerformedBy = "System"
            });
        }

        await _db.SaveChangesAsync();
    }

    // ==================== Subscription Notification Settings ====================

    public async Task<SubscriptionNotificationSettingsResponse> GetNotificationSettingsAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        return MapNotificationSettings(sub);
    }

    public async Task<SubscriptionNotificationSettingsResponse> UpdateNotificationSettingsAsync(
        Guid companyId, UpdateSubscriptionNotificationRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        if (request.NotifyBeforeExpiry.HasValue) sub.NotifyBeforeExpiry = request.NotifyBeforeExpiry.Value;
        if (request.NotifyDaysBeforeExpiry != null) sub.NotifyDaysBeforeExpiry = request.NotifyDaysBeforeExpiry;
        if (request.NotifyOnExpiry.HasValue) sub.NotifyOnExpiry = request.NotifyOnExpiry.Value;
        if (request.NotifyAfterExpiry.HasValue) sub.NotifyAfterExpiry = request.NotifyAfterExpiry.Value;
        if (request.NotifyDaysAfterExpiry != null) sub.NotifyDaysAfterExpiry = request.NotifyDaysAfterExpiry;
        if (request.DeactivationDaysAfterExpiry.HasValue) sub.DeactivationDaysAfterExpiry = request.DeactivationDaysAfterExpiry.Value;
        if (request.NotifyBeforeDeactivation.HasValue) sub.NotifyBeforeDeactivation = request.NotifyBeforeDeactivation.Value;
        if (request.NotifyDaysBeforeDeactivation != null) sub.NotifyDaysBeforeDeactivation = request.NotifyDaysBeforeDeactivation;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "NotificationSettingsUpdated",
            Notes = "Subscription notification settings updated",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
        return MapNotificationSettings(sub);
    }

    // ==================== Subscription Payment ====================

    public async Task<SubscriptionPaymentResponse> SubmitPaymentAsync(
        Guid companyId, SubmitSubscriptionPaymentRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        // รอบ 219 เฟส 0 (G-4 · คำตัดสินข้อ 178): ยอด/ระยะเวลามาจากตารางราคาของเซิร์ฟเวอร์ (PlatformPlanPricing) — เดิมเก็บ Amount/
        // RequestedPeriodMonths จาก body ตรง ๆ ⇒ ฿1 ขอ Enterprise 120 เดือนได้ · ยอดที่แจ้งมาไม่ตรง = 409 พร้อมยอดที่ถูก (ไม่เดา)
        var template = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Plan == request.RequestedPlan && p.IsActive)
            ?? throw new BusinessRuleException($"ไม่พบแพ็กเกจ {request.RequestedPlan} ที่เปิดขาย");
        var months = PlatformPlanPricing.MonthsOf(request.RequestedBillingCycle);
        if (months <= 0) throw new BusinessRuleException("รอบบิลไม่ถูกต้อง");
        if (request.RequestedPeriodMonths > 0 && request.RequestedPeriodMonths != months)
            throw new BusinessRuleException($"จำนวนเดือนต้องตรงกับรอบบิล ({months} เดือน)");
        var due = PlatformPlanPricing.AmountDue(template, request.RequestedBillingCycle);
        if (due <= 0m)
            throw new BusinessRuleException("แพ็กเกจนี้ไม่มีค่าบริการตามตารางราคา — ไม่ต้องแจ้งชำระ");
        if (!PlatformPlanPricing.ClaimedAmountAcceptable(request.Amount, due))
            throw new BusinessRuleException(
                $"ยอดที่แจ้ง ({request.Amount:N2} บาท) ไม่ตรงกับค่าบริการ {template.Name} รอบนี้ ({due:N2} บาท) — กรุณาแจ้งยอดตามราคาเต็ม "
                + "(ถ้าหักภาษี ณ ที่จ่าย ให้แนบหนังสือรับรองกับสลิป ผู้ดูแลระบบจะบันทึกให้)", "PLATFORM-PRICE-MISMATCH", 409);

        var payment = new SubscriptionPayment
        {
            SubscriptionId = sub.Id,
            PaymentNumber = await NextPaymentNumberAsync(sub),
            Amount = due,
            // ฝ่ายค้านรอบ 219 (หมายเหตุ): วันรับเงิน (= วันที่บนใบเสร็จ/ใบกำกับ) ถูกบีบที่เซิร์ฟเวอร์ — ไม่เป็นอนาคต ไม่ย้อนเกิน 31 วัน
            PaymentDate = PlatformPlanPricing.ClampPaymentDate(request.PaymentDate, DateTime.UtcNow, DateTime.UtcNow),
            PaymentMethod = request.PaymentMethod,
            FromBankName = request.FromBankName,
            FromAccountNumber = request.FromAccountNumber,
            ToBankName = request.ToBankName,
            ToAccountNumber = request.ToAccountNumber,
            TransferReference = request.TransferReference,
            RequestedPlan = request.RequestedPlan,
            RequestedBillingCycle = request.RequestedBillingCycle,
            RequestedPeriodMonths = months,
            CustomerNotes = request.CustomerNotes,
            Status = SubscriptionPaymentStatus.Pending,
            CreatedBy = performedBy
        };

        _db.SubscriptionPayments.Add(payment);

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "PaymentSubmitted",
            Notes = $"Payment {payment.PaymentNumber} submitted: {payment.Amount:N2} THB",
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();

        // Notify admins about pending payment
        var adminUsers = await _db.CompanyUsers
            .Where(cu => cu.CompanyId == companyId && (cu.Role == UserRole.Owner || cu.Role == UserRole.SystemAdmin))
            .Select(cu => cu.UserId)
            .ToListAsync();

        foreach (var adminId in adminUsers)
        {
            await NotifyUserAsync(adminId, companyId,
                Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded,
                NotificationType.SubscriptionPaymentPending,
                "มีการชำระเงิน Subscription รอตรวจสอบ",
                $"การชำระเงิน {payment.PaymentNumber} จำนวน {payment.Amount:N2} THB รอการตรวจสอบ",
                $"/admin/subscription-payments/{payment.Id}",
                "SubscriptionPayment", payment.Id);
        }

        return MapPaymentToResponse(payment);
    }

    /// <summary>
    /// รอบ 219 เฟส 2 (คำตัดสินข้อ 180) — แจ้งชำระ <b>ใบแจ้งหนี้ค่าบริการ</b> (ศูนย์บิล · ลิงก์ชำระ) ด้วยการโอน + สลิป
    /// <list type="bullet">
    /// <item>ใบต้องเป็นของบริษัทนี้ใน tenant ผู้ให้บริการ (<see cref="PlatformInvoiceQueries.ForBuyer"/>) — ของบริษัทอื่น/ไม่มี = 404 เหมือนกัน</item>
    /// <item>ยอด = ยอดค้างของใบ (เซิร์ฟเวอร์) · ใบชำระครบ/ยกเลิก = 409 · มีรายการรอตรวจของใบนี้แล้ว: ยังไม่แนบสลิป ⇒ คืนรายการเดิม · แนบแล้ว ⇒ 409</item>
    /// <item>ติ๊กยอมรับเงื่อนไขการซื้อบังคับ (<see cref="PlatformPurchaseAcceptanceWriter"/>) — ไม่ติ๊ก/ฉบับไม่ตรง ⇒ ไม่เกิดรายการ</item>
    /// <item>ใบต่ออายุของรอบนี้ ⇒ อนุมัติแล้วต่ออายุตามเส้นเดิม (เดือน = รอบบิลที่ใบคิด) · ใบอื่น ⇒ <c>RequestedPeriodMonths = 0</c> (ตัดหนี้อย่างเดียว)</item>
    /// </list>
    /// </summary>
    public async Task<SubscriptionPaymentResponse> SubmitInvoicePaymentAsync(Guid companyId, Guid invoiceDocumentId,
        SubmitInvoicePaymentRequest request, PlatformPurchaseAcceptanceInput acceptance, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        // ด่านเงื่อนไขการซื้อก่อนแตะอะไร (ไม่ติ๊ก = ไม่มีรายการเกิด)
        PlatformPurchaseAcceptanceWriter.EnsureAccepted(acceptance);

        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db)
            ?? throw new BusinessRuleException(
                "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ — ใบแจ้งหนี้ค่าบริการยังชำระผ่านหน้านี้ไม่ได้ กรุณาชำระที่หน้าแพ็กเกจ", "PLATFORM-TENANT-NOT-SET", 409);
        var inv = await PlatformInvoiceQueries.ForBuyer(_db, platformId, companyId)
            .FirstOrDefaultAsync(d => d.Id == invoiceDocumentId)
            ?? throw new KeyNotFoundException("ไม่พบใบแจ้งหนี้นี้ของบริษัท");
        if (!PlatformInvoiceRules.IsOpen(inv.Status, inv.BalanceDue))
            throw new BusinessRuleException($"ใบแจ้งหนี้ {inv.DocumentNumber} ไม่มียอดค้างชำระแล้ว", "PLATFORM-INVOICE-NOT-OPEN", 409);
        if (inv.Id == sub.RenewalInvoiceDocumentId && SubscriptionCancelPolicy.RenewalNotPayableReason(sub.CancelAtPeriodEnd) is string cancelled)
            throw new BusinessRuleException(cancelled, "PLATFORM-RENEWAL-CANCELLED", 409);
        var pending = await _db.SubscriptionPayments
            .Where(p => p.SubscriptionId == sub.Id && p.PlatformInvoiceDocumentId == inv.Id
                && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview))
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        if (pending != null)
        {
            if (pending.SlipFileName != null)
                throw new BusinessRuleException(
                    $"ใบแจ้งหนี้ {inv.DocumentNumber} มีรายการแจ้งชำระ {pending.PaymentNumber} รอผู้ดูแลระบบตรวจอยู่แล้ว", "PLATFORM-INVOICE-PENDING", 409);
            return MapPaymentToResponse(pending);
        }

        var isRenewal = inv.Id == sub.RenewalInvoiceDocumentId;
        var plan = isRenewal ? (sub.ScheduledPlan ?? sub.Plan) : sub.Plan;
        var cycle = isRenewal && sub.ScheduledPlan != null ? (sub.ScheduledBillingCycle ?? sub.BillingCycle) : sub.BillingCycle;
        var now = DateTime.UtcNow;
        var payment = new SubscriptionPayment
        {
            SubscriptionId = sub.Id,
            PaymentNumber = await NextPaymentNumberAsync(sub),
            Amount = decimal.Round(inv.BalanceDue, 2, MidpointRounding.AwayFromZero),
            PaymentDate = PlatformPlanPricing.ClampPaymentDate(request.PaymentDate ?? now, now, now),
            PaymentMethod = PaymentMethod.BankTransfer,
            FromBankName = Trunc(request.FromBankName, 100),
            TransferReference = Trunc(request.TransferReference, 100),
            RequestedPlan = plan,
            RequestedBillingCycle = cycle,
            RequestedPeriodMonths = isRenewal ? PlatformPlanPricing.MonthsOf(cycle) : 0,
            CustomerNotes = Trunc(request.CustomerNotes, 1000),
            PlatformInvoiceDocumentId = inv.Id,
            Status = SubscriptionPaymentStatus.Pending,
            CreatedBy = performedBy,
        };
        _db.SubscriptionPayments.Add(payment);
        var accepted = PlatformPurchaseAcceptanceWriter.Add(_db, companyId,
            acceptance.Subject.StartsWith("paylink:", StringComparison.Ordinal) ? PlatformPurchaseTerms.KindPayLinkPayment : PlatformPurchaseTerms.KindInvoicePayment,
            payment.Id, acceptance, now);
        payment.PolicyAcceptanceId = accepted.Id;
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "InvoicePaymentSubmitted",
            Notes = $"แจ้งชำระใบแจ้งหนี้ {inv.DocumentNumber}: {payment.PaymentNumber} {payment.Amount:N2} THB (ยอมรับเงื่อนไขฉบับ {PlatformPurchaseTerms.Version})",
            PerformedBy = performedBy
        });
        await _db.SaveChangesAsync();
        return MapPaymentToResponse(payment);
    }

    private static string? Trunc(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length > max ? t[..max] : t;
    }

    public async Task<SubscriptionPaymentResponse> UploadPaymentSlipAsync(
        Guid paymentId, string fileName, string originalFileName,
        string contentType, long fileSize, string storagePath, string performedBy)
    {
        var payment = await _db.SubscriptionPayments.FindAsync(paymentId)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");

        if (payment.Status != SubscriptionPaymentStatus.Pending)
            throw new InvalidOperationException("ไม่สามารถอัพโหลดสลิปได้ เนื่องจากสถานะไม่ใช่รอตรวจสอบ");

        // ฝ่ายค้านรอบ 219 (หมายเหตุ): รายการที่ระบบสร้างจากการเลือกแพ็กเกจ (ยังไม่โอน — ไม่มีรายละเอียดการโอน/สลิป) ⇒ วันรับเงิน = เวลาที่แนบสลิปครั้งแรก
        // (ไม่ใช่วันที่กดเลือกแพ็กเกจ) · รายการที่ลูกค้าแจ้งวันที่โอนเองคงวันเดิม (บีบแล้วตอนแจ้ง)
        if (payment.SlipFileName == null && payment.TransferReference == null && payment.FromBankName == null)
            payment.PaymentDate = DateTime.UtcNow;
        payment.SlipFileName = fileName;
        payment.SlipOriginalFileName = originalFileName;
        payment.SlipContentType = contentType;
        payment.SlipFileSize = fileSize;
        payment.SlipStoragePath = storagePath;

        await _db.SaveChangesAsync();
        return MapPaymentToResponse(payment);
    }

    public async Task<SubscriptionPaymentListResponse> GetPaymentsAsync(Guid companyId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        var payments = await _db.SubscriptionPayments
            .Include(p => p.ReviewedByUser)
            .Where(p => p.SubscriptionId == sub.Id)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        return new SubscriptionPaymentListResponse(
            payments.Select(MapPaymentToResponse).ToList(),
            payments.Count,
            payments.Count(p => p.Status == SubscriptionPaymentStatus.Pending));
    }

    public async Task<SubscriptionPaymentResponse> GetPaymentAsync(Guid paymentId)
    {
        var payment = await _db.SubscriptionPayments
            .Include(p => p.ReviewedByUser)
            .FirstOrDefaultAsync(p => p.Id == paymentId)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");

        return MapPaymentToResponse(payment);
    }

    public async Task<SubscriptionPaymentResponse> ReviewPaymentAsync(
        Guid paymentId, ReviewSubscriptionPaymentRequest request, string performedBy,
        bool systemConfirmed = false, DateTime? gatewayPaidAtUtc = null)
    {
        var payment = await _db.SubscriptionPayments
            .Include(p => p.Subscription)
            .FirstOrDefaultAsync(p => p.Id == paymentId)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");

        if (payment.Status != SubscriptionPaymentStatus.Pending && payment.Status != SubscriptionPaymentStatus.UnderReview)
            throw new InvalidOperationException("ไม่สามารถตรวจสอบรายการนี้ได้ เนื่องจากสถานะไม่ใช่รอตรวจสอบ");

        // เส้น "ระบบยืนยันเอง" (เงินเข้าจริงผ่านช่องทางชำระออนไลน์) ไม่มีผู้ตรวจสอบ
        // ที่เป็นคน ⇒ เก็บ null แล้วบอกเหตุผลใน ReviewNotes · ห้ามแต่ง user id ปลอม
        // ให้ช่องไม่ว่าง (ผู้สอบบัญชีต้องแยกออกว่า "ใครอนุมัติ" กับ "ระบบอนุมัติ")
        Guid? reviewerUserId = null;
        if (!systemConfirmed)
        {
            if (!Guid.TryParse(performedBy, out var parsed))
                throw new InvalidOperationException("ไม่สามารถระบุผู้ตรวจสอบได้");
            reviewerUserId = parsed;
        }

        payment.ReviewedByUserId = reviewerUserId;
        payment.ReviewedAt = DateTime.UtcNow;
        payment.ReviewNotes = request.ReviewNotes;

        // รอบ 219 เฟส 2 (คำตัดสินข้อ 180): การชำระใบแจ้งหนี้ค่าใช้งาน (ผูกใบ · ไม่มีเดือนต่ออายุ) = ตัดหนี้อย่างเดียว ห้ามแตะวันหมดอายุ/แพ็กเกจ
        var invoiceOnly = PlatformInvoiceRules.IsInvoiceOnlyPayment(payment.PlatformInvoiceDocumentId, payment.RequestedPeriodMonths);
        // รอบ 219 เฟส 2 (S9 · ผู้ประสานงาน): อนุมัติการชำระที่เริ่มรอบใหม่/ต่ออายุ "โดยไม่ได้จ่ายใบต่ออายุที่ออกไว้" ⇒ ใบต่ออายุของรอบเดิมค้างเป็นหนี้เท็จ
        Guid? staleRenewalId = null;
        if (request.Approve && invoiceOnly)
        {
            payment.Status = SubscriptionPaymentStatus.Approved;
            payment.PaymentDate = PlatformPlanPricing.ApprovedPaymentDate(systemConfirmed, gatewayPaidAtUtc,
                payment.Kind is SubscriptionPaymentKind.ManualByAdmin or SubscriptionPaymentKind.Waived,
                payment.PaymentDate, payment.CreatedAt, DateTime.UtcNow);
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = payment.SubscriptionId,
                Action = "InvoicePaymentApproved",
                Notes = $"Payment {payment.PaymentNumber} approved — ตัดชำระใบแจ้งหนี้ค่าบริการ (ไม่ต่ออายุแพ็กเกจ)",
                PerformedBy = performedBy
            });
            var ownerId = await _db.CompanyUsers
                .Where(cu => cu.CompanyId == payment.Subscription.CompanyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId).FirstOrDefaultAsync();
            if (ownerId != Guid.Empty)
                await NotifyUserAsync(ownerId, payment.Subscription.CompanyId,
                    Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded,
                    NotificationType.SubscriptionPaymentApproved,
                    "ได้รับชำระใบแจ้งหนี้แล้ว",
                    $"การชำระ {payment.PaymentNumber} ยอด {payment.Amount:N2} บาท ได้รับการยืนยันแล้ว",
                    "/pages/billing.html", "SubscriptionPayment", payment.Id);
        }
        else if (request.Approve)
        {
            payment.Status = SubscriptionPaymentStatus.Approved;

            // Extend/renew subscription
            var sub = payment.Subscription;
            var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == payment.RequestedPlan && p.IsActive);
            var now = DateTime.UtcNow;
            // รอบ 219 เฟส 0 (G-0): ดาวน์เกรดที่จ่ายระหว่างรอบที่ยังใช้งาน ⇒ ไม่ตัดสิทธิ์แพ็กเกจที่จ่ายไปแล้ว — ตั้งไว้มีผลวันครบรอบเดิม
            var currentTpl = sub.Plan == payment.RequestedPlan ? template
                : await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Plan == sub.Plan && p.IsActive);
            var changeKind = PlatformPlanPricing.Classify(currentTpl?.MonthlyPrice ?? 0m, template?.MonthlyPrice ?? 0m);
            var defer = sub.Plan != payment.RequestedPlan
                && PlatformPlanPricing.DeferToCycleEnd(changeKind, sub.Status, sub.EndDate, now);
            // ฝ่ายค้านรอบ 219 B1: ต่อจากวันหมดอายุเดิมเฉพาะ "ต่ออายุแพ็กเกจเดิม/ดาวน์เกรดที่รอวันครบรอบ" · อัปเกรดทันที และ sub ฟรีถาวร (EndDate +100 ปี)
            // ⇒ เริ่มรอบใหม่จากวันนี้ (เดิม: ฟรีถาวร → อัปเกรด ฿990 ได้ Pro 100 ปี) — ตัวตัดสินเดียว PlatformPlanPricing.PeriodStart
            var baseDate = PlatformPlanPricing.PeriodStart(changeKind, sub.Plan == payment.RequestedPlan, sub.IsPermanentFree, sub.EndDate, now);
            var newEndDate = baseDate.AddMonths(payment.RequestedPeriodMonths);
            var wasTrial = sub.Status == SubscriptionStatus.Trial;
            staleRenewalId = PlatformInvoiceRules.StaleRenewalInvoice(sub.RenewalInvoiceDocumentId, sub.RenewalInvoiceForEndDate, sub.EndDate,
                payment.PlatformInvoiceDocumentId);
            if (staleRenewalId != null)
            {
                // ใบเดิมถูกจัดการหลังบันทึก (ยกเลิกผ่านเส้นยกเลิกเอกสาร / ต้องออกใบลดหนี้) — ล้างตัวชี้ให้รอบใหม่ออกใบต่ออายุของตัวเอง
                sub.RenewalInvoiceNumber = null; sub.RenewalInvoiceIssuedAt = null; sub.RenewalInvoiceForEndDate = null; sub.RenewalInvoiceDocumentId = null;
            }
            // จ่ายแพ็กเกจ = ใช้ต่อ ⇒ ยกเลิกเมื่อสิ้นรอบที่ตั้งไว้ไม่มีผลแล้ว (คำตัดสินเจ้าของข้อ 178 ⑥)
            sub.CancelAtPeriodEnd = false; sub.CancelRequestedAt = null; sub.CancelRequestedBy = null;
            sub.Status = SubscriptionStatus.Active;
            sub.EndDate = newEndDate;
            sub.NextBillingDate = newEndDate;
            sub.LastPaymentDate = payment.PaymentDate;
            sub.PaymentReference = payment.PaymentNumber;
            if (defer && template != null)
            {
                sub.ScheduledPlan = payment.RequestedPlan;
                sub.ScheduledBillingCycle = payment.RequestedBillingCycle;
                sub.ScheduledPlanEffectiveAt = baseDate;
            }
            else if (template != null)
            {
                // ฝ่ายค้านรอบ 219 รอบ 2 (S9): เปลี่ยนแพ็กเกจทันที (อัปเกรด) หลังออกใบแจ้งหนี้ต่ออายุของแพ็กเกจเดิมแล้ว ⇒ ใบนั้นล้าสมัย
                // (จ่ายทีหลัง = ดาวน์เกรดที่รอวันครบรอบ) — ประทับ "ถูกแทนที่" ในประวัติ + ล้างตัวชี้ ⇒ รอบถัดไปออกใบใหม่ตามแพ็กเกจใหม่ ·
                // เอกสารใบเดิมใน tenant ผู้ให้บริการ: แอดมินยกเลิกผ่านเส้นยกเลิกเอกสารเดิม (บอกในประวัติ — ไม่ยกเลิกเอกสารภาษีเงียบ ๆ จากที่นี่)
                if (sub.Plan != payment.RequestedPlan && sub.RenewalInvoiceNumber != null)
                {
                    _db.SubscriptionHistories.Add(new SubscriptionHistory
                    {
                        SubscriptionId = sub.Id,
                        Action = "RenewalInvoiceSuperseded",
                        FromPlan = sub.Plan,
                        ToPlan = payment.RequestedPlan,
                        Notes = $"ใบแจ้งหนี้ต่ออายุ {sub.RenewalInvoiceNumber} (แพ็กเกจ {sub.Plan}) ถูกแทนที่ด้วยการเปลี่ยนแพ็กเกจ {payment.PaymentNumber} — "
                              + "ห้ามรับชำระใบเดิม · ผู้ดูแลระบบยกเลิกเอกสารใบเดิมในบริษัทผู้ให้บริการ",
                        PerformedBy = performedBy
                    });
                    sub.RenewalInvoiceNumber = null;
                    sub.RenewalInvoiceIssuedAt = null;
                    sub.RenewalInvoiceForEndDate = null;
                    sub.RenewalInvoiceDocumentId = null;
                }
                // ธง "ฟรีถาวร" ต้องตามแพ็กเกจใหม่เสมอ (ApplyTemplate ตั้งให้) — เดิมเส้นนี้เคยลืมธงนี้ ⇒ จ่ายแล้วยังโชว์ "ฟรีถาวร" และงานตัดหมดอายุไม่ตัด
                ApplyTemplate(sub, template, payment.RequestedBillingCycle);
                sub.ScheduledPlan = null; sub.ScheduledBillingCycle = null; sub.ScheduledPlanEffectiveAt = null;
            }
            else
            {
                sub.Plan = payment.RequestedPlan;
                sub.BillingCycle = payment.RequestedBillingCycle;
            }
            if (wasTrial)
            {
                var trial = await _db.TrialConfigs.FirstOrDefaultAsync(t => t.SubscriptionId == sub.Id);
                if (trial != null) trial.TrialStatus = TrialStatus.Converted;
            }
            payment.SubscriptionExtendedTo = newEndDate;
            // ฝ่ายค้านรอบ 219 (หมายเหตุ) + รอบ 2 (ข้อ 5): วันที่บนใบเสร็จ/ใบกำกับ = PaymentDate — ระบบยืนยัน ⇒ เวลาเงินเข้าจริง ·
            // แอดมินกรอก ⇒ คงวันที่กรอก (ตรวจที่ RecordManualPaymentAsync แล้ว) · ลูกค้าแจ้ง ⇒ บีบ (ClampPaymentDate) — ตัวตัดสินเดียว
            payment.PaymentDate = PlatformPlanPricing.ApprovedPaymentDate(systemConfirmed, gatewayPaidAtUtc,
                payment.Kind is SubscriptionPaymentKind.ManualByAdmin or SubscriptionPaymentKind.Waived,
                payment.PaymentDate, payment.CreatedAt, now);
            sub.LastPaymentDate = payment.PaymentDate;

            // Cascade renewal to the AccountSubscription if this Company is
            // covered by one. Without this the user's License (the layer above
            // company-scoped Subscription) would expire even though they
            // already paid. Sync EndDate + LastPaidAt + Status; quotas stay
            // whatever the admin / sales set on the AccountSubscription so a
            // negotiated enterprise limit isn't overwritten by a slip approval.
            if (sub.AccountSubscriptionId.HasValue)
            {
                var acct = await _db.AccountSubscriptions
                    .FirstOrDefaultAsync(a => a.Id == sub.AccountSubscriptionId.Value && !a.IsDeleted);
                // ฝ่ายค้านรอบ 219 S5: ต่ออายุแพลนระดับบัญชีได้เฉพาะเมื่อแพ็กเกจที่จ่าย = แพ็กเกจของแพลนบัญชี (ราคาของแพลนบัญชีอาจสูงกว่ามาก —
                // จ่าย Basic ของบริษัทเดียวแล้วต่ออายุ Enterprise ทั้งกลุ่มไม่ได้) · ไม่ตรง = ไม่ cascade + ประวัติบอกเหตุ
                var acctPlan = acct == null ? (SubscriptionPlan?)null
                    // ฝ่ายค้านรอบ 219 รอบ 2: แม่แบบของแพลนบัญชีอาจถูกลบ (soft delete) แล้ว — ยังต้องรู้ระดับเดิม ⇒ IgnoreQueryFilters (ตารางระดับแพลตฟอร์ม ไม่มี tenant)
                    : await _db.PlanTemplates.AsNoTracking().IgnoreQueryFilters().Where(t => t.Id == acct.PlanTemplateId).Select(t => (SubscriptionPlan?)t.Plan).FirstOrDefaultAsync();
                if (acct != null && !PlatformPlanPricing.MayCascadeToAccountPlan(payment.RequestedPlan, acctPlan))
                {
                    _db.SubscriptionHistories.Add(new SubscriptionHistory
                    {
                        SubscriptionId = sub.Id,
                        AccountSubscriptionId = acct.Id,
                        Action = "AccountPlanCascadeSkipped",
                        Notes = $"ไม่ต่ออายุแพลนบัญชี — รายการ {payment.PaymentNumber} จ่ายแพ็กเกจ {payment.RequestedPlan} ไม่ตรงแพลนบัญชี ({acctPlan?.ToString() ?? "ไม่ทราบ"})",
                        PerformedBy = performedBy
                    });
                    acct = null;
                }
                if (acct != null)
                {
                    var acctBase = acct.EndDate > now ? acct.EndDate : now;
                    var acctNewEnd = acctBase.AddMonths(payment.RequestedPeriodMonths);
                    if (acctNewEnd > acct.EndDate)
                    {
                        acct.EndDate = acctNewEnd;
                        acct.LastPaidAt = payment.PaymentDate;
                        // Bring the parent plan out of expiry / past-due so all
                        // companies under it light up immediately. Trial → Active
                        // is intentional: paying mid-trial converts to paid.
                        if (acct.Status == SubscriptionStatus.Expired
                            || acct.Status == SubscriptionStatus.PastDue
                            || acct.Status == SubscriptionStatus.Trial)
                            acct.Status = SubscriptionStatus.Active;
                        acct.UpdatedBy = performedBy;
                        acct.UpdatedAt = DateTime.UtcNow;
                        // Reset reminder mask now that EndDate moved forward —
                        // next expiry cycle starts fresh, otherwise we'd skip
                        // the 7-day warning the second time around.
                        acct.ExpiryRemindersSentMask = 0;
                        acct.LastExpiryReminderAt = null;

                        // History row scoped to the account-level extension —
                        // SubscriptionId still points at the company subscription
                        // that triggered the cascade so admin can trace cause.
                        _db.SubscriptionHistories.Add(new SubscriptionHistory
                        {
                            SubscriptionId = sub.Id,
                            AccountSubscriptionId = acct.Id,
                            Action = "AccountPlanExtendedViaSlip",
                            ToStatus = acct.Status,
                            Notes = $"Account Plan EndDate extended to {acctNewEnd:yyyy-MM-dd} via slip {payment.PaymentNumber} (company {sub.CompanyId})",
                            PerformedBy = performedBy
                        });
                    }
                }
            }

            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = "PaymentApproved",
                ToPlan = payment.RequestedPlan,
                ToStatus = SubscriptionStatus.Active,
                Notes = $"Payment {payment.PaymentNumber} approved. Subscription extended to {newEndDate:yyyy-MM-dd}",
                PerformedBy = performedBy
            });

            // Notify customer
            var ownerUserId = await _db.CompanyUsers
                .Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId)
                .FirstOrDefaultAsync();

            if (ownerUserId != Guid.Empty)
            {
                await NotifyUserAsync(ownerUserId, sub.CompanyId,
                    Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded,
                    NotificationType.SubscriptionPaymentApproved,
                    "การชำระเงินได้รับการอนุมัติ",
                    $"การชำระเงิน {payment.PaymentNumber} ได้รับการอนุมัติ Subscription ต่ออายุถึง {newEndDate:dd/MM/yyyy}",
                    $"/subscription",
                    "SubscriptionPayment", payment.Id);

                await NotifyUserAsync(ownerUserId, sub.CompanyId,
                    Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded,
                    NotificationType.SubscriptionRenewed,
                    "ต่ออายุ Subscription สำเร็จ",
                    $"Subscription plan {payment.RequestedPlan} ต่ออายุถึง {newEndDate:dd/MM/yyyy}",
                    $"/subscription",
                    "Subscription", sub.Id);
            }
        }
        else
        {
            payment.Status = SubscriptionPaymentStatus.Rejected;
            payment.RejectionReason = request.RejectionReason;

            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = payment.SubscriptionId,
                Action = "PaymentRejected",
                Notes = $"Payment {payment.PaymentNumber} rejected: {request.RejectionReason}",
                PerformedBy = performedBy
            });

            // Notify customer
            var sub = payment.Subscription;
            var ownerUserId = await _db.CompanyUsers
                .Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId)
                .FirstOrDefaultAsync();

            if (ownerUserId != Guid.Empty)
            {
                await NotifyUserAsync(ownerUserId, sub.CompanyId,
                    Models.Constants.NotificationEvents.SubscriptionPaymentFailed,
                    NotificationType.SubscriptionPaymentRejected,
                    "การชำระเงินถูกปฏิเสธ",
                    $"การชำระเงิน {payment.PaymentNumber} ถูกปฏิเสธ: {request.RejectionReason}",
                    $"/subscription/payments/{payment.Id}",
                    "SubscriptionPayment", payment.Id);
            }
        }

        await _db.SaveChangesAsync();

        // WP-B2: อนุมัติแล้ว → ออกใบเสร็จ/ใบกำกับค่าบริการ (best-effort, ไม่ block approval)
        // รอบ 219 เฟส 2 (G-10): รายการที่ผูกใบแจ้งหนี้ ⇒ ตัดชำระใบเดิมใน tenant ผู้ให้บริการ (ไม่ออกขายสดใบใหม่ = ไม่นับรายได้ซ้ำ)
        if (payment.Status == SubscriptionPaymentStatus.Approved && _billing != null)
        {
            if (payment.PlatformInvoiceDocumentId != null)
                await _billing.SettlePlatformInvoiceAsync(payment.Id);
            else
                await _billing.GenerateReceiptForApprovedPaymentAsync(payment.Id);
        }
        // รอบ 219 เฟส 2 (S9): ใบต่ออายุของรอบเดิมที่ไม่ได้ถูกจ่าย ⇒ ยกเลิกผ่านเส้นยกเลิกเอกสาร (ยังไม่มีการชำระ) / บันทึกงานออกใบลดหนี้ (มีการชำระแล้ว)
        if (payment.Status == SubscriptionPaymentStatus.Approved && staleRenewalId is Guid stale && _billing != null)
            await _billing.HandleStaleRenewalInvoiceAsync(payment.SubscriptionId, stale, payment.PaymentNumber);

        // รอบ 219 เฟส 2 ขั้น 2 (G-8): ชำระแล้ว ⇒ ประเมินค้างชำระของบริษัทนี้ใหม่ทันที (ไม่รอรอบงาน) — ระบบระงับไว้เพราะค้างชำระและไม่มีใบเกินผ่อนผันแล้ว ⇒ คืนสถานะ
        if (payment.Status == SubscriptionPaymentStatus.Approved && _dunning != null)
        {
            try { await _dunning.EvaluateCompanyAsync(payment.Subscription.CompanyId, sendReminders: false); }
            catch (Exception ex) { _logger?.LogError(ex, "ประเมินค้างชำระหลังอนุมัติ {Payment} ไม่สำเร็จ — งานทวงหนี้จะประเมินรอบถัดไป", payment.Id); }
        }

        return MapPaymentToResponse(payment);
    }

    public async Task<SubscriptionPaymentResponse> RecordManualPaymentAsync(
        Guid companyId, RecordManualPaymentRequest request, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        if (request.RequestedPeriodMonths <= 0)
            throw new InvalidOperationException("จำนวนเดือนที่ต่ออายุต้องมากกว่า 0");

        // Waived = ต่ออายุให้ฟรี → บังคับเหตุผล + ยอดต้องเป็น 0 (กัน admin แอบตั้งยอด)
        if (request.IsWaived)
        {
            if (request.WaiveReason == null)
                throw new InvalidOperationException("การยกเว้นค่าบริการต้องระบุเหตุผล (Goodwill/Compensation/Correction)");
        }
        else if (request.Amount <= 0)
        {
            throw new InvalidOperationException("ยอดรับเงินต้องมากกว่า 0 (หรือเลือกยกเว้นค่าบริการ)");
        }

        // ฝ่ายค้านรอบ 219 รอบ 2 (ข้อ 5): วันรับเงินที่แอดมินกรอก = วันที่บนใบเสร็จ/ใบกำกับ (tax point) — คงตามที่กรอก (เดิมถูกบีบเงียบ ๆ เมื่อย้อนเกิน 31 วัน)
        // แต่ไม่รับอนาคต และไม่รับวันที่อยู่ในงวดที่ปิดแล้วของบริษัทผู้ให้บริการ (ใบเสร็จลงวันนั้นในสมุดที่ปิดแล้วไม่ได้) — บอกเหตุผลชัด
        var nowUtc = DateTime.UtcNow;
        if (PlatformPlanPricing.AdminPaymentDateProblem(request.PaymentDate, nowUtc) is string dateProblem)
            throw new BusinessRuleException(dateProblem);
        var paidOn = request.PaymentDate == default ? nowUtc : request.PaymentDate;
        var platformCompanyId = await _db.SiteSettings.AsNoTracking().OrderBy(x => x.CreatedAt)
            .Select(x => x.PlatformCompanyId).FirstOrDefaultAsync();
        if (platformCompanyId is Guid pcid
            && await Journal.JournalEntryBuilder.ClosedPeriodReasonAsync(_db, pcid, ThaiDate.CalendarDateUtc(paidOn)) is string closedReason)
            throw new BusinessRuleException($"วันรับเงิน {paidOn.AddHours(7):dd/MM/yyyy} อยู่ในงวดบัญชีที่ปิดแล้วของบริษัทผู้ให้บริการ — {closedReason}");

        var paymentNumber = await NextPaymentNumberAsync(sub);

        var kindLabel = request.IsWaived ? "ยกเว้นค่าบริการ" : "บันทึกรับเงินโดย admin";
        var payment = new SubscriptionPayment
        {
            SubscriptionId = sub.Id,
            PaymentNumber = paymentNumber,
            Amount = request.IsWaived ? 0m : request.Amount,
            PaymentDate = paidOn,
            PaymentMethod = request.PaymentMethod,
            TransferReference = request.TransferReference,
            RequestedPlan = request.RequestedPlan,
            RequestedBillingCycle = request.RequestedBillingCycle,
            RequestedPeriodMonths = request.RequestedPeriodMonths,
            Kind = request.IsWaived ? SubscriptionPaymentKind.Waived : SubscriptionPaymentKind.ManualByAdmin,
            // ยกเว้นค่าบริการ = ไม่มีเงิน ไม่มีการหักภาษี
            WithholdingTaxAmount = request.IsWaived ? 0m : Math.Max(0m, request.WithholdingTaxAmount),
            WaiveReason = request.IsWaived ? request.WaiveReason : null,
            CustomerNotes = request.Notes,
            Status = SubscriptionPaymentStatus.Pending,
            CreatedBy = performedBy
        };
        _db.SubscriptionPayments.Add(payment);

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = request.IsWaived ? "PaymentWaivedRecorded" : "PaymentManualRecorded",
            Notes = $"{kindLabel} {paymentNumber}: {payment.Amount:N2} THB"
                  + (request.IsWaived ? $" (เหตุผล: {request.WaiveReason})" : ""),
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();

        // วิ่งเข้าเส้น approve เดิม → ต่ออายุ + cascade License + ประวัติ + (WP-B2) ใบเสร็จ
        return await ReviewPaymentAsync(payment.Id,
            new ReviewSubscriptionPaymentRequest(true,
                $"{kindLabel}" + (string.IsNullOrWhiteSpace(request.Notes) ? "" : $" — {request.Notes}"),
                null),
            performedBy);
    }

    public async Task<SubscriptionPaymentListResponse> GetAllPendingPaymentsAsync()
    {
        var payments = await _db.SubscriptionPayments
            .Include(p => p.Subscription)
            .Include(p => p.ReviewedByUser)
            .Where(p => p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();

        return new SubscriptionPaymentListResponse(
            payments.Select(MapPaymentToResponse).ToList(),
            payments.Count,
            payments.Count(p => p.Status == SubscriptionPaymentStatus.Pending));
    }

    // ==================== Background: Process Subscription Notifications ====================

    public async Task ProcessSubscriptionNotificationsAsync()
    {
        var now = DateTime.UtcNow;
        // รอบ 219 เฟส 2 (คำตัดสินข้อ 180): ออกใบแจ้งหนี้ต่ออายุกี่วันก่อนครบรอบ = ค่าตั้งของแอดมิน (เดิมค่าคงที่ 15) — ตัวโหลดเดียว
        var renewalLeadDays = (await PlatformBillingSettingsService.LoadPolicyAsync(_db)).RenewalInvoiceLeadDays;
        var activeSubscriptions = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.PastDue)
            .ToListAsync();

        foreach (var sub in activeSubscriptions)
        {
            var daysUntilExpiry = (int)(sub.EndDate - now).TotalDays;
            var deactivationDate = sub.EndDate.AddDays(sub.DeactivationDaysAfterExpiry);
            var daysUntilDeactivation = (int)(deactivationDate - now).TotalDays;

            // แจ้งเตือนก่อนหมดอายุ
            if (sub.NotifyBeforeExpiry && daysUntilExpiry > 0 && !sub.CancelAtPeriodEnd)
            {
                var notifyDays = ParseDaysList(sub.NotifyDaysBeforeExpiry);
                if (notifyDays.Contains(daysUntilExpiry))
                {
                    await SendSubscriptionNotificationToOwner(sub, NotificationType.SubscriptionExpiring,
                        $"Subscription จะหมดอายุในอีก {daysUntilExpiry} วัน",
                        $"Subscription plan {sub.Plan} จะหมดอายุในวันที่ {sub.EndDate:dd/MM/yyyy} (อีก {daysUntilExpiry} วัน) กรุณาต่ออายุก่อนหมดอายุ");
                }
            }

            // WP-B1: ออกใบแจ้งหนี้ต่ออายุล่วงหน้า (ค่าตั้ง RenewalInvoiceLeadDays · ตั้งต้น 15 วัน) —
            // idempotent ต่อ EndDate ของรอบ (service เช็คซ้ำอีกชั้น) จึงเรียกซ้ำได้ปลอดภัย
            if (_billing != null && daysUntilExpiry > 0 && daysUntilExpiry <= renewalLeadDays && !sub.CancelAtPeriodEnd
                && sub.Status == SubscriptionStatus.Active
                && sub.RenewalInvoiceForEndDate != sub.EndDate)
            {
                await _billing.GenerateRenewalInvoiceAsync(sub.Id);
            }

            // แจ้งเตือนก่อนตัดบัญชี
            if (sub.NotifyBeforeDeactivation && daysUntilExpiry <= 0 && daysUntilDeactivation > 0)
            {
                var notifyDays = ParseDaysList(sub.NotifyDaysBeforeDeactivation);
                if (notifyDays.Contains(daysUntilDeactivation))
                {
                    await SendSubscriptionNotificationToOwner(sub, NotificationType.SubscriptionDeactivation,
                        $"บัญชีจะถูกระงับในอีก {daysUntilDeactivation} วัน",
                        $"Subscription หมดอายุแล้ว บัญชีจะถูกระงับในวันที่ {deactivationDate:dd/MM/yyyy} (อีก {daysUntilDeactivation} วัน) กรุณาต่ออายุโดยด่วน");
                }
            }
        }

        // แจ้งเตือนวันหมดอายุ
        var todayStart = now.Date;
        var todayEnd = todayStart.AddDays(1);
        var expiringToday = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active && s.EndDate >= todayStart && s.EndDate < todayEnd)
            .ToListAsync();

        foreach (var sub in expiringToday)
        {
            if (sub.NotifyOnExpiry)
            {
                await SendSubscriptionNotificationToOwner(sub, NotificationType.SubscriptionExpired,
                    "Subscription หมดอายุวันนี้",
                    $"Subscription plan {sub.Plan} หมดอายุวันนี้ ({sub.EndDate:dd/MM/yyyy}) กรุณาต่ออายุเพื่อใช้งานต่อ");
            }
        }

        // แจ้งเตือนหลังหมดอายุ
        var expiredSubscriptions = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Expired || s.Status == SubscriptionStatus.PastDue)
            .ToListAsync();

        foreach (var sub in expiredSubscriptions)
        {
            if (!sub.NotifyAfterExpiry) continue;

            var daysSinceExpiry = (int)(now - sub.EndDate).TotalDays;
            var notifyDays = ParseDaysList(sub.NotifyDaysAfterExpiry);
            if (notifyDays.Contains(daysSinceExpiry))
            {
                var deactivationDate = sub.EndDate.AddDays(sub.DeactivationDaysAfterExpiry);
                var daysUntilDeactivation = (int)(deactivationDate - now).TotalDays;

                await SendSubscriptionNotificationToOwner(sub, NotificationType.SubscriptionExpired,
                    $"Subscription หมดอายุแล้ว {daysSinceExpiry} วัน",
                    $"Subscription plan {sub.Plan} หมดอายุไปแล้ว {daysSinceExpiry} วัน บัญชีจะถูกระงับในอีก {Math.Max(0, daysUntilDeactivation)} วัน");
            }
        }
    }

    public async Task ProcessExpiredSubscriptionsAsync()
    {
        var now = DateTime.UtcNow;
        var autoSuspend = (await PlatformBillingSettingsService.LoadPolicyAsync(_db)).AutoSuspendEnabled;

        // รอบ 219 เฟส 2 ขั้น 3 (คำตัดสินเจ้าของข้อ 178 ⑥): ยกเลิกเมื่อสิ้นรอบที่ถึงวัน ⇒ Cancelled (ไม่ PastDue · ไม่ทวง) — ทำก่อนการเปลี่ยนแพ็กเกจที่รอ
        var dueCancels = await _db.Subscriptions
            .Where(s => s.CancelAtPeriodEnd && s.Status == SubscriptionStatus.Active && s.EndDate < now)
            .ToListAsync();
        foreach (var sub in dueCancels)
        {
            sub.Status = SubscriptionStatus.Cancelled;
            sub.CancelledAt = now;
            sub.CancelAtPeriodEnd = false;
            sub.ScheduledPlan = null; sub.ScheduledBillingCycle = null; sub.ScheduledPlanEffectiveAt = null;
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = "CancelledAtPeriodEnd",
                FromStatus = SubscriptionStatus.Active,
                ToStatus = SubscriptionStatus.Cancelled,
                Notes = $"ยกเลิกเมื่อสิ้นรอบ {sub.EndDate:yyyy-MM-dd} ตามที่ผู้เช่าขอ ({sub.CancelRequestedAt:yyyy-MM-dd})",
                PerformedBy = "System"
            });
        }
        if (dueCancels.Count > 0) await _db.SaveChangesAsync();

        // รอบ 219 เฟส 0 (G-0): ดาวน์เกรด/เปลี่ยนรอบที่ถึงวันมีผล — ใช้สิทธิ์ของแพ็กเกจใหม่ (ApplyTemplate ตัวเดียวกับการอนุมัติชำระ)
        var dueChanges = await _db.Subscriptions
            .Where(s => s.ScheduledPlan != null && s.ScheduledPlanEffectiveAt != null && s.ScheduledPlanEffectiveAt <= now
                && !s.CancelAtPeriodEnd && s.Status != SubscriptionStatus.Cancelled)
            .ToListAsync();
        foreach (var sub in dueChanges)
        {
            var tpl = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Plan == sub.ScheduledPlan && p.IsActive);
            var fromPlan = sub.Plan;
            if (tpl != null) ApplyTemplate(sub, tpl, sub.ScheduledBillingCycle ?? sub.BillingCycle);
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = tpl != null ? "ScheduledPlanApplied" : "ScheduledPlanSkipped",
                FromPlan = fromPlan,
                ToPlan = sub.ScheduledPlan,
                Notes = tpl != null ? $"เปลี่ยนแพ็กเกจตามที่ตั้งไว้ (มีผล {sub.ScheduledPlanEffectiveAt:yyyy-MM-dd})"
                                    : $"ไม่พบแม่แบบแพ็กเกจ {sub.ScheduledPlan} ที่เปิดขาย — ไม่เปลี่ยน",
                PerformedBy = "System"
            });
            sub.ScheduledPlan = null; sub.ScheduledBillingCycle = null; sub.ScheduledPlanEffectiveAt = null;
        }
        // ฝ่ายค้านรอบ 219 รอบ 2 (CI Db Scheduled_downgrade_to_permanent_free_plan_stays_active): คำค้นถัดไปกรองด้วยค่าใน DB —
        // ยังไม่บันทึก ⇒ DB ยังเห็น EndDate เดิม (หมดแล้ว) แล้ว EF คืนตัวที่ track อยู่ (ApplyTemplate ตั้ง Active + EndDate +100 ปีแล้วในหน่วยความจำ)
        // ⇒ ลูปตั้ง PastDue ทับ · บันทึกการเปลี่ยนแพ็กเกจก่อน + ตรวจซ้ำจากค่าในหน่วยความจำในลูปถัดไป
        if (dueChanges.Count > 0) await _db.SaveChangesAsync();

        // Mark active subscriptions as expired
        var newlyExpired = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active && s.EndDate < now)
            .ToListAsync();

        foreach (var sub in newlyExpired)
        {
            // ตัวที่ track อยู่อาจถูกเปลี่ยนในรอบนี้แล้ว (แพ็กเกจฟรีถาวร = ไม่หมดอายุ) — ตัดสินจากค่าปัจจุบัน ไม่ใช่ค่าที่ DB ใช้กรอง
            if (sub.Status != SubscriptionStatus.Active || sub.EndDate >= now || sub.IsPermanentFree) continue;
            sub.Status = SubscriptionStatus.PastDue;

            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = "SubscriptionPastDue",
                FromStatus = SubscriptionStatus.Active,
                ToStatus = SubscriptionStatus.PastDue,
                Notes = $"Subscription expired on {sub.EndDate:yyyy-MM-dd}",
                PerformedBy = "System"
            });
        }

        // Deactivate subscriptions past deactivation period
        var pastDue = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.PastDue)
            .ToListAsync();

        foreach (var sub in pastDue)
        {
            var deactivationDate = sub.EndDate.AddDays(sub.DeactivationDaysAfterExpiry);
            // รอบ 219 เฟส 2 ขั้น 2 (คำตัดสินข้อ 180): สวิตช์ "ระงับอัตโนมัติ" ของแอดมินครอบเส้นหมดอายุด้วย (ปิด = คง PastDue ให้แอดมินตามเอง) · ค่าตั้งต้นเปิด = พฤติกรรมเดิม
            if (now >= deactivationDate && autoSuspend)
            {
                sub.Status = SubscriptionStatus.Suspended;

                _db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = sub.Id,
                    Action = "SubscriptionSuspended",
                    FromStatus = SubscriptionStatus.PastDue,
                    ToStatus = SubscriptionStatus.Suspended,
                    Notes = $"Suspended after {sub.DeactivationDaysAfterExpiry} days past expiry",
                    PerformedBy = "System"
                });

                await SendSubscriptionNotificationToOwner(sub, NotificationType.SubscriptionDeactivation,
                    "บัญชีถูกระงับ",
                    $"Subscription plan {sub.Plan} ถูกระงับเนื่องจากไม่ได้ต่ออายุภายในกำหนด กรุณาชำระเงินเพื่อเปิดใช้งานอีกครั้ง");
            }
        }

        await _db.SaveChangesAsync();
    }

    // ==================== Private Helpers ====================

    private async Task SendSubscriptionNotificationToOwner(Subscription sub, NotificationType type, string title, string message)
    {
        var ownerUserId = await _db.CompanyUsers
            .Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner)
            .Select(cu => cu.UserId)
            .FirstOrDefaultAsync();

        if (ownerUserId != Guid.Empty)
        {
            var evt = type switch
            {
                NotificationType.SubscriptionExpiring => Models.Constants.NotificationEvents.SubscriptionExpired,
                NotificationType.SubscriptionRenewed => Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded,
                NotificationType.SubscriptionPaymentApproved => Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded,
                NotificationType.SubscriptionPaymentRejected => Models.Constants.NotificationEvents.SubscriptionPaymentFailed,
                _ => Models.Constants.NotificationEvents.SubscriptionExpired,
            };
            await NotifyUserAsync(ownerUserId, sub.CompanyId, evt,
                type, title, message, "/subscription", "Subscription", sub.Id);
        }
    }

    private static List<int> ParseDaysList(string daysString)
    {
        if (string.IsNullOrWhiteSpace(daysString)) return new List<int>();
        return daysString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var d) ? d : -1)
            .Where(d => d > 0)
            .ToList();
    }

    private static SubscriptionNotificationSettingsResponse MapNotificationSettings(Subscription sub)
    {
        return new SubscriptionNotificationSettingsResponse(
            sub.NotifyBeforeExpiry,
            ParseDaysList(sub.NotifyDaysBeforeExpiry),
            sub.NotifyOnExpiry,
            sub.NotifyAfterExpiry,
            ParseDaysList(sub.NotifyDaysAfterExpiry),
            sub.DeactivationDaysAfterExpiry,
            sub.NotifyBeforeDeactivation,
            ParseDaysList(sub.NotifyDaysBeforeDeactivation));
    }

    private static SubscriptionPaymentResponse MapPaymentToResponse(SubscriptionPayment p)
    {
        return new SubscriptionPaymentResponse(
            p.Id,
            p.SubscriptionId,
            p.PaymentNumber,
            p.Amount,
            p.Currency,
            p.PaymentDate,
            p.PaymentMethod,
            p.FromBankName,
            p.FromAccountNumber,
            p.ToBankName,
            p.ToAccountNumber,
            p.TransferReference,
            p.SlipOriginalFileName,
            p.SlipStoragePath,
            p.RequestedPlan,
            p.RequestedBillingCycle,
            p.RequestedPeriodMonths,
            p.Status,
            p.ReviewedByUser?.FullName,
            p.ReviewedAt,
            p.ReviewNotes,
            p.RejectionReason,
            p.SubscriptionExtendedTo,
            p.CustomerNotes,
            p.CreatedAt,
            p.ReceiptNumber,
            p.ReceiptIsTaxInvoice,
            p.SlipOcrAmount,
            p.SlipOcrAmountMatches,
            p.SlipOcrReference,
            p.SlipOcrDate,
            p.PlatformInvoiceDocumentId);
    }

    private static PlanTemplateResponse MapTemplateToResponse(PlanTemplate t)
    {
        return new PlanTemplateResponse(
            t.Id, t.Name, t.Description, t.Plan, t.IsActive,
            t.MonthlyPrice, t.QuarterlyPrice, t.SemiAnnualPrice, t.AnnualPrice,
            t.MaxUsers, t.MaxCompanies, t.MaxDocumentsPerMonth, t.MaxJournalEntriesPerMonth,
            t.MaxStorageBytes, t.EnabledFeatures,
            FeatureFlagsHelper.ToNameList(t.EnabledFeatures),
            t.TrialDurationDays, t.TrialMaxExtensions, t.TrialExtensionDays, t.TrialFeatures,
            FeatureFlagsHelper.ToNameList(t.TrialFeatures),
            t.IsPermanentFree,
            t.MaxOcrPagesPerMonth,
            t.AzureOcrPagesPerMonth,
            t.LocalOcrPagesPerMonth,
            t.FallbackToLocalWhenAzureExhausted,
            t.TrialMaxOcrPagesPerMonth,
            t.TrialGracePeriodDays,
            t.TrialBlockOnExpiry,
            t.TrialMaxUsers,
            t.TrialMaxDocumentsPerMonth,
            t.TrialMaxJournalEntriesPerMonth,
            t.MaxSharedMessagesPerMonth,
            t.TrialMaxSharedMessagesPerMonth,
            Accounting.Helpers.NotifyQuotaPolicy.SuggestedLimit(t.Plan),
            Accounting.Helpers.NotifyQuotaPolicy.SuggestedTrialLimit);
    }
}

/// <summary>
/// ตัวแปลง bitmask ↔ รายชื่อ — **มอบต่อให้ <see cref="Accounting.Helpers.FeatureCatalog"/> ทั้งหมด**
/// (เดิมไฟล์นี้มีตารางป้ายไทย + หมวดของตัวเองซึ่งตกธง CMS 3 ตัว และซ้ำกับสำเนาใน JS 4 หน้า)
/// </summary>
public static class FeatureFlagsHelper
{
    public static List<string> ToNameList(FeatureFlags flags) => Accounting.Helpers.FeatureCatalog.NamesOf(flags);

    public static FeatureFlags FromNameList(IEnumerable<string>? names) => Accounting.Helpers.FeatureCatalog.FlagsOf(names);

    public static List<FeatureFlagInfo> AllFeatures()
        => Accounting.Helpers.FeatureCatalog.All
            .Select(e => new FeatureFlagInfo(e.Name, e.Category, e.LabelTh, e.LabelEn, e.DescriptionTh))
            .ToList();

    /// <summary>payload ของ `GET /api/subscription/feature-catalog`</summary>
    public static FeatureCatalogResponse Catalog()
        => new(AllFeatures(),
            Accounting.Helpers.FeatureCatalog.Categories.ToList(),
            Accounting.Helpers.FeatureCatalog.Presets.ToDictionary(kv => kv.Key, kv => kv.Value));
}

