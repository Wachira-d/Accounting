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
        IPlatformDunningService? dunning = null, IOverageService? overage = null)
    {
        _overage = overage;
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

    /// <summary>รอบ 231 (คำตัดสินข้อ 231-2): ด่านส่วนเกินพื้นที่ (GB-เดือน) — null ในเทสต์ที่สร้างเอง = บล็อกตามเดิม</summary>
    private readonly IOverageService? _overage;

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
            softDeleted.Kind = SubscriptionKindRule.Derive(permFree, softDeleted.Status, neverPaid: true, isFreeTrialPlan: request.TargetPlan == SubscriptionPlan.FreeTrial);   // รอบ 231 (231-10)
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
            // รอบ 231 (231-10): ชนิดจากตัวตัดสินเดียว — ฟรีถาวร = Free · อื่น ๆ = Trial
            Kind = SubscriptionKindRule.Derive(isPermanentFree, isPermanentFree ? SubscriptionStatus.Active : SubscriptionStatus.Trial, neverPaid: true, isFreeTrialPlan: request.TargetPlan == SubscriptionPlan.FreeTrial),
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
                tc.TrialMessage,
                tc.DeleteDataAfterGracePeriod, tc.DataRetentionDays, tc.PurgeNoticeDays, tc.PurgeNoticeSentAt),
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
                new ChangeSubscriptionRequest(request.Plan, request.BillingCycle, CouponCode: request.PromoCode), performedBy);

        var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == request.Plan && p.IsActive)
            ?? throw new KeyNotFoundException($"ไม่พบ plan template สำหรับ {request.Plan}");

        // ส่วนลดเมื่อแปลงจากทดลองใช้ (โค้ดของบริษัทนี้ · ยังไม่หมดอายุ) — คิดใน PlatformPlanPricing ตัวเดียว
        decimal? discount = null;
        if (!string.IsNullOrEmpty(request.PromoCode) && sub.TrialConfig != null
            && sub.TrialConfig.ConversionPromoCode == request.PromoCode
            && sub.TrialConfig.DiscountPercentOnConversion.HasValue
            && (sub.TrialConfig.DiscountValidUntil == null || sub.TrialConfig.DiscountValidUntil > DateTime.UtcNow))
            discount = sub.TrialConfig.DiscountPercentOnConversion.Value;

        // รอบ 230 (คำตัดสินข้อ 230-Q2): โค้ดที่ไม่ใช่โปรโมชันแปลงทดลองใช้ของบริษัทนี้ ⇒ คูปองแพลตฟอร์ม (ใช้ไม่ได้ = 400 พร้อมเหตุ — เดิมโค้ดผิดถูกเพิกเฉยเงียบ ๆ)
        var coupon = discount == null && !string.IsNullOrWhiteSpace(request.PromoCode) ? request.PromoCode : null;
        var payment = await RequestPlanPaymentAsync(sub, template, request.BillingCycle, discount, performedBy,
            "PlanPurchaseRequested", "ขอเปลี่ยนเป็นแพ็กเกจจ่ายเงิน", couponCode: coupon);
        return new PlanPurchaseResponse(await GetSubscriptionAsync(companyId), payment,
            $"สร้างรายการชำระ {payment.PaymentNumber} ยอด {payment.Amount:N2} บาทแล้ว — แพ็กเกจ {template.Name} จะเปิดใช้เมื่อชำระเงินและผู้ดูแลระบบยืนยัน "
            + "(ระหว่างนี้ใช้งานตามสิทธิ์เดิมได้ตามปกติ)");
    }

    /// <summary>รอบ 219 เฟส 0: สร้าง (หรือใช้ซ้ำ) รายการชำระค่าแพ็กเกจที่<b>ยอดมาจากเซิร์ฟเวอร์</b> (<see cref="PlatformPlanPricing.AmountDue"/>) —
    /// ทางเดียวของผู้เช่าในการขอแพ็กเกจจ่ายเงิน · ใช้ซ้ำรายการรอชำระที่แพ็กเกจ/รอบ/ยอดเดียวกัน (กดซ้ำไม่เกิดหลายรายการ)
    /// <para>รอบ 230: <paramref name="proration"/> (ใช้ได้) ⇒ ยอด = ส่วนต่างตามวันที่เหลือ · <c>RequestedPeriodMonths = 0</c> (อนุมัติแล้วไม่ขยับวันหมดอายุ) ·
    /// <paramref name="couponCode"/> ⇒ จองคูปองใต้ล็อกในธุรกรรมเดียวกับการสร้างรายการ (<see cref="PlatformCouponStore.ReserveAsync"/>) ·
    /// ทุกรายการที่เส้นนี้สร้าง ประทับ <c>QuotedAmount</c> (ยอดของเซิร์ฟเวอร์ — ช่องทางออนไลน์อนุมัติอัตโนมัติเมื่อเงินเข้าเท่ายอดนี้)</para></summary>
    private async Task<SubscriptionPaymentResponse> RequestPlanPaymentAsync(Subscription sub, PlanTemplate template,
        BillingCycle cycle, decimal? discountPercent, string performedBy, string historyAction, string note,
        PlatformProration.Result? proration = null, string? couponCode = null, PlatformProration.CycleChangeResult? cycleChange = null)
    {
        var prorated = proration?.Applies == true;
        var cycleChanged = cycleChange?.Applies == true;
        var months = prorated ? 0 : PlatformPlanPricing.MonthsOf(cycle);
        if (!prorated && months <= 0) throw new BusinessRuleException("รอบบิลไม่ถูกต้อง");
        // รอบ 231 (231-8): ราคาต่อรอบผ่านตัวตัดสินราคาพิเศษตัวเดียว (ราคาพิเศษ → ส่วนลดโปรโมชัน % → คูปอง) — ส่วนต่าง/เปลี่ยนรอบคิดราคาพิเศษทั้งสองฝั่งมาแล้ว
        var effective = await PlatformPriceOverrideStore.ResolveForSubscriptionAsync(_db, sub, template, cycle, DateTime.UtcNow, forRenewal: PlatformPlanPricing.IsRenewalPurchase(sub.Plan, template.Plan));   // ฝ่ายค้าน S4: ซื้อแพ็กเกจเดิมซ้ำ = ต่ออายุ
        var listAmount = prorated ? proration!.Amount
            : cycleChanged ? cycleChange!.Amount
            : PlatformPlanPricing.AmountDue(effective.Price, discountPercent);
        if (listAmount <= 0m)
            throw new BusinessRuleException(cycleChanged
                ? "มูลค่าคงเหลือของรอบเดิมครอบคลุมค่ารอบใหม่ทั้งหมด — ติดต่อผู้ดูแลระบบ NextAcc ให้บันทึกเปลี่ยนรอบแบบยกเว้นค่าบริการ (ยอด 0)"
                : "แพ็กเกจนี้ไม่มีค่าบริการตามตารางราคา — ติดต่อผู้ดูแลระบบ NextAcc เพื่อเปลี่ยนแพ็กเกจ");
        var code = PlatformCouponRule.Normalize(couponCode);

        // กดซ้ำ = รายการเดิม (แพ็กเกจ/รอบ/ชนิด/คูปอง/ยอดก่อนคูปองเดียวกัน · ยังไม่จ่าย)
        var existing = await _db.SubscriptionPayments
            .FirstOrDefaultAsync(p => p.SubscriptionId == sub.Id && p.Status == SubscriptionPaymentStatus.Pending
                && p.RequestedPlan == template.Plan && p.RequestedBillingCycle == cycle && p.IsProration == prorated && p.IsCycleChange == cycleChanged
                && p.CouponCode == code && (p.ListAmount ?? p.Amount) == listAmount && p.PlatformInvoiceDocumentId == null);
        if (existing != null) return MapPaymentToResponse(existing);

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = null;
        if (code != null && _db.Database.IsRelational() && _db.Database.CurrentTransaction == null)
            tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;
            var payment = new SubscriptionPayment
            {
                SubscriptionId = sub.Id,
                PaymentNumber = await NextPaymentNumberAsync(sub),
                PaymentDate = now,
                PaymentMethod = PaymentMethod.BankTransfer,
                RequestedPlan = template.Plan,
                RequestedBillingCycle = cycle,
                RequestedPeriodMonths = months,
                CustomerNotes = note,
                Status = SubscriptionPaymentStatus.Pending,
                CreatedBy = performedBy
            };
            var amount = listAmount;
            // รายการรอชำระเดิมที่ถือคูปองเดียวกันไว้ / ส่วนต่างอัปเกรดใบเก่า (ไม่มีสลิป · ไม่เคยเริ่มชำระออนไลน์) = คำขอที่ถูกแทน ⇒ ยกเลิก + ปลดการจอง
            // (ไม่ให้กินสิทธิ์ต่อบริษัทของคำขอใหม่ · ฝ่ายค้าน S6: ส่วนต่างใบเก่าคิดวันที่เหลือไม่ตรงแล้ว)
            if (code != null || prorated || cycleChanged) await SupersedePendingPaymentsAsync(sub, code, prorated || cycleChanged, performedBy);
            if (code != null)
            {
                var (coupon, eval) = await PlatformCouponStore.ReserveAsync(_db, sub.CompanyId, code, PlatformCouponScope.Subscription,
                    template.Plan.ToString(), cycle.ToString(), null, listAmount, PlatformCouponRule.SourceSubscriptionPayment, payment.Id, performedBy, now);
                amount = eval.AmountAfter;
                payment.CouponId = coupon.Id; payment.CouponCode = coupon.Code; payment.CouponDiscountAmount = eval.Discount;
                payment.ListAmount = listAmount;
            }
            payment.Amount = amount;
            // รอบ 231 (231-8): ตามรอยราคาพิเศษ — ราคาตั้งเก็บไว้ให้ประวัติ/แอดมินเห็นส่วนต่าง (ใบกำกับแสดงราคาที่ตกลงเป็นราคาขาย ไม่ใช่ราคาตั้ง−ส่วนลด)
            payment.PriceOverrideId = effective.OverrideId;
            payment.StandardAmount = effective.Applied && !prorated && !cycleChanged ? PlatformPlanPricing.AmountDue(effective.ListPrice, discountPercent) : null;
            // ฝ่ายค้าน B1 (คำตัดสินผู้ประสานงาน): ยอดที่เซิร์ฟเวอร์เสนอประทับเฉพาะรายการส่วนต่าง/มีคูปอง/ราคาพิเศษ/เปลี่ยนรอบ (และแอดมินแก้ยอด) — รายการราคาเต็มคงกติกาเดิม
            // (ช่องทางออนไลน์/handler เทียบราคาแพ็กเกจปัจจุบัน ⇒ ราคาเปลี่ยนหลังสร้างรายการ = ปฏิเสธ) · ราคาพิเศษต้องประทับ ไม่งั้นทุกใบถูกพักให้แอดมิน (§C.2)
            payment.QuotedAmount = prorated || cycleChanged || code != null || effective.Applied ? amount : null;
            if (prorated)
            {
                payment.IsProration = true;
                payment.ProrationFromPlan = sub.Plan.ToString();
                payment.ProrationDays = proration!.DaysRemaining;
                payment.ProrationCycleDays = proration.CycleDays;
                payment.ProrationCycleEndUtc = proration.CycleEndUtc;
            }
            if (cycleChanged)
            {
                // รอบ 231 (231-9): เปลี่ยนรอบบิลกลางรอบ — D/C และวันหมดอายุของรอบที่คิดเครดิต (ความหมายเดียวกับส่วนต่าง: รอบที่ถูกคิด) · อนุมัติ ⇒ เริ่มรอบใหม่วันที่ชำระ
                payment.IsCycleChange = true;
                payment.CycleChangeCredit = cycleChange!.Credit;
                payment.ProrationFromPlan = sub.Plan.ToString();
                payment.ProrationDays = cycleChange.DaysRemaining;
                payment.ProrationCycleDays = cycleChange.CycleDays;
                payment.ProrationCycleEndUtc = cycleChange.CycleEndUtc;
            }
            _db.SubscriptionPayments.Add(payment);
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = historyAction,
                FromPlan = sub.Plan,
                ToPlan = template.Plan,
                Notes = $"{note}: {payment.PaymentNumber} {template.Plan} ({cycle}) {amount:N2} THB"
                        + (prorated ? $" · ส่วนต่าง {proration!.DaysRemaining}/{proration.CycleDays} วัน (วันหมดอายุเดิม)" : "")
                        + (cycleChanged ? $" · เปลี่ยนรอบบิล หักมูลค่าคงเหลือ {cycleChange!.Credit:N2} ({cycleChange.DaysRemaining}/{cycleChange.CycleDays} วัน) รอบใหม่เริ่มวันที่ชำระ" : "")
                        + (effective.Applied ? $" · ราคาพิเศษ (ราคาตั้ง {effective.ListPrice:N2}/รอบ → {effective.Price:N2})" : "")
                        + (payment.CouponCode != null ? $" · คูปอง {payment.CouponCode} ลด {payment.CouponDiscountAmount:N2}" : "")
                        + " — รอชำระ (ยังไม่เปิดแพ็กเกจ)",
                PerformedBy = performedBy
            });
            await _db.SaveChangesAsync();
            if (tx != null) await tx.CommitAsync();
            return MapPaymentToResponse(payment);
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }

    /// <summary>รอบ 230: รายการรอชำระของ subscription นี้ที่จองคูปองเดียวกันไว้ หรือเป็นส่วนต่างอัปเกรดใบเก่า (เมื่อ <paramref name="prorated"/>) และยังไม่มีสลิป/ไม่เคยเริ่มชำระออนไลน์
    /// ⇒ ยกเลิก + ปลดการจองคูปอง (คำขอใหม่แทน) · มีสลิป/intent แล้ว = ไม่แตะ (เงินอาจกำลังเข้า)</summary>
    private async Task SupersedePendingPaymentsAsync(Subscription sub, string? code, bool prorated, string performedBy)
    {
        var olds = await _db.SubscriptionPayments
            .Where(p => p.SubscriptionId == sub.Id && p.Status == SubscriptionPaymentStatus.Pending && p.SlipFileName == null && p.PlatformInvoiceDocumentId == null
                && ((code != null && p.CouponCode == code) || (prorated && (p.IsProration || p.IsCycleChange))))
            .ToListAsync();
        var n = 0;
        foreach (var old in olds)
        {
            var hasIntent = await _db.PaymentIntents.AsNoTracking()
                .AnyAsync(i => i.SourceKind == PaymentSourceKind.SubscriptionPayment && i.SourceId == old.Id);
            if (hasIntent) continue;
            await CancelQuotedPaymentAsync(sub.Id, sub.CompanyId, old, "ถูกแทนด้วยคำขอใหม่", "PaymentSupersededByNewRequest", performedBy);
            n++;
        }
        // บันทึกก่อนจอง — ตัวนับเพดาน (CountAsync) อ่านจากฐาน ไม่ใช่จาก change tracker
        if (n > 0) await _db.SaveChangesAsync();
    }

    /// <summary>รอบ 230: ยกเลิกรายการรอชำระที่เซิร์ฟเวอร์เสนอยอด (ส่วนต่าง/คูปอง) + ปลดการจองคูปอง + ประวัติ — ตัวเดียวของ "ถูกแทน" และ "ค้างเกินกำหนด" (ผู้เรียก SaveChanges)</summary>
    private async Task CancelQuotedPaymentAsync(Guid subscriptionId, Guid companyId, SubscriptionPayment old, string reason, string action, string performedBy)
    {
        old.Status = SubscriptionPaymentStatus.Cancelled;
        old.RejectionReason = reason;
        if (old.CouponId != null)
            await PlatformCouponStore.ReleaseAsync(_db, companyId, PlatformCouponRule.SourceSubscriptionPayment, old.Id, reason, performedBy);
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = subscriptionId, Action = action,
            Notes = $"ยกเลิกรายการรอชำระ {old.PaymentNumber}" + (old.CouponCode != null ? $" (คูปอง {old.CouponCode})" : "") + (old.IsProration ? " (ส่วนต่างอัปเกรด)" : "") + $" — {reason}",
            PerformedBy = performedBy
        });
    }

    /// <summary>รอบ 230 (ฝ่ายค้าน S3): รายการค่าแพ็กเกจที่มีคูปอง/ส่วนต่างอัปเกรด ค้างไม่จ่ายเกิน <see cref="PlatformCouponRule.ReservationDays"/> วัน
    /// (ไม่มีสลิป · ไม่มีการชำระออนไลน์ค้าง/สำเร็จ) ⇒ ยกเลิก + ปลดคูปอง (เหมือนโควตาเสริมค้าง 14 วัน) — งาน <c>AccountPlanExpiryReminderJob</c> ทุกรอบ · idempotent</summary>
    public async Task<int> ExpireStaleQuotedPaymentsAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-PlatformCouponRule.ReservationDays);
        var rows = await _db.SubscriptionPayments.Include(p => p.Subscription)
            .Where(p => p.Status == SubscriptionPaymentStatus.Pending && p.SlipFileName == null && p.PlatformInvoiceDocumentId == null
                && (p.CouponId != null || p.IsProration) && p.CreatedAt < cutoff)
            .Take(500).ToListAsync();
        var n = 0;
        foreach (var p in rows)
        {
            if (await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.SourceKind == PaymentSourceKind.SubscriptionPayment && i.SourceId == p.Id
                    && (i.Status == PaymentIntentStatus.Created || i.Status == PaymentIntentStatus.Pending || i.Status == PaymentIntentStatus.Succeeded)))
                continue;
            await CancelQuotedPaymentAsync(p.SubscriptionId, p.Subscription.CompanyId, p, $"ค้างไม่ชำระเกิน {PlatformCouponRule.ReservationDays} วัน",
                "QuotedPaymentExpired", "system:platform-billing");
            n++;
        }
        if (n > 0) await _db.SaveChangesAsync();
        return n;
    }

    /// <summary>เลขรายการชำระค่าแพ็กเกจ (SP-{บริษัท 8 ตัว}-{ลำดับ}) — ตัวเดียวของ Submit/คำขอแพ็กเกจ/บันทึกโดยแอดมิน</summary>
    private async Task<string> NextPaymentNumberAsync(Subscription sub)
    {
        var count = await _db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id);
        return $"SP-{sub.CompanyId.ToString()[..8].ToUpper()}-{count + 1:D4}";
    }

    /// <summary>รอบ 219 เฟส 0: คัดลอกสิทธิ์/โควตาของแพ็กเกจลง subscription — ตัวเดียวของการอนุมัติชำระ และดาวน์เกรดที่ถึงวันมีผล</summary>
    /// <returns>true = แถวนี้เคย "ไม่มีรอบ" (ฟรีถาวร/ค่าแทน) แล้วแม่แบบใหม่ไม่ฟรีถาวร ⇒ ปิดรอบผ่าน <see cref="SubscriptionExpiry.CloseOpenEnded"/> แล้ว (ผู้เรียกบันทึกประวัติ/แจ้ง)</returns>
    /// <param name="paidPeriodEndUtc">รอบ 233 ฝ่ายค้าน B1: วันหมดอายุของรอบที่จ่าย (<see cref="PlanPurchasePreview.PaidPeriod"/>) — ปิดสถานะ "ไม่มีรอบ" ด้วยค่านี้
    /// (เดิม CloseOpenEnded(UtcNow) ทับ EndDate ที่ตั้งจากวันรับเงิน ⇒ รายการชำระ/ใบเสร็จบอกวันหนึ่ง แต่ subscription อีกวัน) · null = ผู้เรียกที่ไม่ใช่การชำระ (resync/ตามกำหนด)</param>
    private static bool ApplyTemplate(Subscription sub, PlanTemplate template, BillingCycle cycle, DateTime? paidPeriodEndUtc = null)
    {
        var nowUtc = DateTime.UtcNow;
        var wasOpenEnded = SubscriptionExpiry.IsOpenEnded(sub.IsPermanentFree, sub.EndDate, nowUtc);
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
        // รอบ 231 (231-10): เปิดแพ็กเกจจากแม่แบบ = จ่ายแล้ว (Paid) หรือ Free Edition — ตัวตัดสินเดียว SubscriptionKindRule
        sub.Kind = SubscriptionKindRule.AfterTemplateApplied(template.IsPermanentFree);
        // ฝ่ายค้านรอบ 219 S2: แพ็กเกจฟรีถาวร = Active ไม่หมดอายุ (เหมือน ResyncSubscriptionsFromTemplateAsync) — ดาวน์เกรดไปแพ็กเกจฟรีห้ามตกเป็น PastDue
        if (template.IsPermanentFree)
        {
            sub.Status = SubscriptionStatus.Active;
            sub.EndDate = SubscriptionExpiry.OpenEnded(nowUtc);   // รอบ 232: ค่าแทน "ไม่หมดอายุ" ตัวเดียว
            sub.NextBillingDate = null;
            sub.ExpiryNeedsReview = false;
            return false;
        }
        // ฝ่ายค้าน 232-hub B1(ก): ธงฟรีถาวรถูกถอด (หรือค่าแทนค้างอยู่) ⇒ ตั้งรอบใหม่ผ่านตัวตั้งเดียว — ห้ามปล่อยค่าแทน 2126 ไว้บนแผนจ่ายเงิน
        if (wasOpenEnded)
        {
            if (paidPeriodEndUtc is DateTime pe) { sub.EndDate = pe; sub.NextBillingDate = pe; }
            else (sub.EndDate, sub.NextBillingDate) = SubscriptionExpiry.CloseOpenEnded(nowUtc, cycle);
            sub.Status = SubscriptionStatus.Active;
            sub.ExpiryNeedsReview = false;
            return true;
        }
        return false;
    }

    /// <summary>ฝ่ายค้าน 232-hub B1: <b>ผู้เขียนเดียว</b>ของการปิดสถานะ "ไม่มีรอบ" บนแผนจ่ายเงิน — ตั้ง EndDate/NextBillingDate ผ่าน <see cref="SubscriptionExpiry.CloseOpenEnded"/> ·
    /// ปลด <c>ExpiryNeedsReview</c> · ประวัติ + audit (hash chain) + แจ้งเจ้าของบริษัท "แพ็กเกจเปลี่ยนเป็นแบบมีรอบ หมดอายุ &lt;วันที่&gt;" · ผู้เรียก: resync แม่แบบ (ฟรีถาวร→จ่ายเงิน) · แอดมินกด "กำหนดรอบ" · แพ็กเกจตามกำหนดถึงวันมีผล</summary>
    private async Task CloseOpenEndedCoreAsync(Subscription sub, string performedBy, string reason, DateTime nowUtc, bool alreadyClosed)
    {
        var before = sub.EndDate;
        if (!alreadyClosed)
        {
            (sub.EndDate, sub.NextBillingDate) = SubscriptionExpiry.CloseOpenEnded(nowUtc, sub.BillingCycle);
            sub.Status = SubscriptionStatus.Active;
        }
        sub.ExpiryNeedsReview = false;
        sub.UpdatedAt = nowUtc;
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id, Action = "OpenEndedClosed",
            Notes = $"แพ็กเกจเปลี่ยนจาก \"ไม่มีรอบเรียกเก็บ\" เป็นแบบมีรอบ ({sub.BillingCycle}) หมดอายุ {sub.EndDate:yyyy-MM-dd} · {reason}",
            PerformedBy = performedBy,
        });
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = sub.CompanyId, Action = AuditAction.Update, EntityType = nameof(Subscription), EntityId = sub.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { endDate = before, isPermanentFree = true, expiryNeedsReview = true }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "OpenEndedClosed", ruleCode = "SUB-EXPIRY-CLOSE-OPEN-ENDED", endDate = sub.EndDate, nextBillingDate = sub.NextBillingDate, cycle = sub.BillingCycle.ToString(), reason, by = performedBy }),
            Timestamp = nowUtc,
        });
        await SendSubscriptionNotificationToOwner(sub, NotificationType.SubscriptionExpiring,
            "แพ็กเกจเปลี่ยนเป็นแบบมีรอบเรียกเก็บ",
            $"แพ็กเกจ {sub.Plan} ของบริษัทเปลี่ยนเป็นแบบมีรอบ ({sub.BillingCycle}) — ใช้ได้ถึง {sub.EndDate:dd/MM/yyyy} จากนั้นต้องต่ออายุ ({reason})");
    }

    /// <summary>แอดมินกด "กำหนดรอบ" บนแถวที่ <c>ExpiryNeedsReview</c> (หน้าลูกค้า) — ผ่านผู้เขียนเดียว <see cref="CloseOpenEndedCoreAsync"/></summary>
    public async Task<SubscriptionResponse> CloseOpenEndedAsync(Guid companyId, string performedBy, string? reason = null)
    {
        var now = DateTime.UtcNow;
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");
        if (sub.IsPermanentFree)
            throw new BusinessRuleException("แพ็กเกจนี้เป็นฟรีถาวร (ธง IsPermanentFree) — ไม่มีรอบให้กำหนด ให้เปลี่ยนแม่แบบแพ็กเกจก่อน", "SUB-EXPIRY-PERMANENT-FREE");
        if (!sub.ExpiryNeedsReview && !SubscriptionExpiry.IsOpenEnded(sub.EndDate, now))
            throw new BusinessRuleException($"แพ็กเกจนี้มีรอบอยู่แล้ว (หมดอายุ {sub.EndDate:dd/MM/yyyy}) — ใช้ \"ปรับวันที่\" แทน", "SUB-EXPIRY-ALREADY-CLOSED");
        await CloseOpenEndedCoreAsync(sub, performedBy, reason ?? "แอดมินกำหนดรอบ", now, alreadyClosed: false);
        await _db.SaveChangesAsync();
        return await GetSubscriptionAsync(companyId);
    }

    /// <summary>จำนวนแถวที่รอแอดมินตรวจ (งานพื้นหลังข้าม · แสดงใน JobRun)</summary>
    public Task<int> CountExpiryNeedsReviewAsync()
        => _db.Subscriptions.CountAsync(s => s.ExpiryNeedsReview);

    /// <summary>รอบ 231 (231-8): <see cref="ApplyTemplate"/> + ราคาต่อรอบบน subscription = <b>ราคาที่บริษัทนี้จ่ายจริง</b> (ราคาพิเศษผ่าน <see cref="PlatformPriceOverrideStore"/>) —
    /// ใบต่ออายุ/ส่วนต่างฝั่งเดิมใช้ <c>PricePerCycle</c> เป็น fallback เมื่อไม่มีแม่แบบ จึงต้องเป็นราคาพิเศษ ไม่ใช่ราคาตั้ง · ผู้เรียก: อนุมัติชำระ (ทุกชนิด) · ดาวน์เกรดถึงวันมีผล</summary>
    private async Task ApplyTemplateWithPriceAsync(Subscription sub, PlanTemplate template, BillingCycle cycle, DateTime nowUtc, string performedBy = "System",
        DateTime? paidPeriodEndUtc = null)
    {
        // ฝ่ายค้าน 232-hub B1(ก): ฟรีถาวร/ค่าแทน → แผนจ่ายเงิน = ปิดรอบ + ประวัติ/audit/แจ้งเจ้าของ (ทางอนุมัติชำระจะตั้ง EndDate จากวันชำระทับอีกที — ประวัติ/แจ้งยังบอกการเปลี่ยนชนิด)
        if (ApplyTemplate(sub, template, cycle, paidPeriodEndUtc))
            await CloseOpenEndedCoreAsync(sub, performedBy, $"เปลี่ยนเป็นแม่แบบ {template.Name}", nowUtc, alreadyClosed: true);
        sub.PricePerCycle = (await PlatformPriceOverrideStore.ResolveForSubscriptionAsync(_db, sub, template, cycle, nowUtc, forRenewal: true)).Price;
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

        // รอบ 231 (231-8): "ราคาพิเศษสำหรับคุณ" ของแพ็กเกจ+รอบปัจจุบัน (ตัวตัดสินเดียว PlatformPriceOverrideStore) — หน้าเว็บแสดงอย่างเดียว · ฟรีถาวรไม่มีราคา
        string? specialPriceNote = null;
        if (!sub.IsPermanentFree)
        {
            var priceTpl = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Plan == plan && t.IsActive);
            if (priceTpl != null)
                specialPriceNote = PlatformPlanPricing.SpecialPriceNote(
                    await PlatformPriceOverrideStore.ResolveForSubscriptionAsync(_db, sub, priceTpl, sub.BillingCycle, DateTime.UtcNow, forRenewal: true), sub.BillingCycle);
        }

        var current = await GetUsageCurrentAsync(sub);   // รอบ 231: ใช้ร่วมกับหลอดเอกสาร (DocumentsBar) — เรียกครั้งเดียว
        return new SubscriptionResponse(
            sub.Id, sub.CompanyId, plan, status, sub.BillingCycle,
            sub.PricePerCycle, sub.StartDate, endDate, sub.NextBillingDate,
            features,
            FeatureFlagsHelper.ToNameList(features),
            new UsageLimits(maxUsers, maxCompanies, maxDocs, maxJournals, maxStorage),
            current,
            sub.IsPermanentFree,
            addOnCodes,
            planName,
            IsOpenEnded: SubscriptionExpiry.IsOpenEnded(sub.IsPermanentFree, endDate, DateTime.UtcNow),
            ScheduledPlan: sub.ScheduledPlan,
            ScheduledBillingCycle: sub.ScheduledBillingCycle,
            ScheduledPlanEffectiveAt: sub.ScheduledPlanEffectiveAt,
            CancelAtPeriodEnd: sub.CancelAtPeriodEnd,
            CancelConfirmText: SubscriptionCancelPolicy.ConfirmText(sub.Status, sub.EndDate, DateTime.UtcNow, sub.IsPermanentFree),
            Kind: sub.Kind == SubscriptionKind.Unknown ? null : sub.Kind.ToString(),
            SpecialPriceNote: specialPriceNote,
            // รอบ 231 (P0 จ · "ไม่จำกัด" ที่เดียว): หลอดเอกสาร + ข้อความเพดานคิดที่เซิร์ฟเวอร์ — pages/subscription.html เลิกพิมพ์เกณฑ์ ≥ 99999 เอง
            DocumentsBar: UsageBarRule.Documents(current.DocumentsThisMonth,
                Accounting.Helpers.DocumentQuotaPolicy.EffectiveLimit(maxDocs, sub.DocumentBonusQuota, sub.DocumentBonusExpiresAt, DateTime.UtcNow, used: current.DocumentsThisMonth)),
            LimitTexts: UsageBarRule.LimitTexts(maxUsers, maxCompanies, maxDocs, maxJournals, acct?.MaxOcrPagesPerMonth ?? sub.MaxOcrPagesPerMonth, maxStorage, sub.MaxSharedMessagesPerMonth),
            Unlimited: UsageBarRule.UnlimitedFlags(maxUsers, maxCompanies, maxDocs, maxJournals, acct?.MaxOcrPagesPerMonth ?? sub.MaxOcrPagesPerMonth, maxStorage, sub.MaxSharedMessagesPerMonth));
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
        // รอบ 231 (231-10): ผู้อ่านของ TrialConfig.BlockAccessOnExpiry / AllowDataExportAfterExpiry — เฉพาะทดลองใช้ที่หมดอายุแล้ว (ไม่เสีย query กับบริษัทอื่น) ·
        // Free Edition/จ่ายเงิน ไม่มีนโยบายนี้ (กันแถวเก่า Kind=Unknown ด้วย LastPaymentDate)
        bool blockReads = false, allowExport = true;
        if (sub.Status == SubscriptionStatus.Expired && !sub.IsPermanentFree
            && (sub.Kind == SubscriptionKind.Trial || (sub.Kind == SubscriptionKind.Unknown && sub.LastPaymentDate == null)))
        {
            var tc = await _db.TrialConfigs.AsNoTracking().Where(t => t.SubscriptionId == sub.Id)
                .Select(t => new { t.BlockAccessOnExpiry, t.AllowDataExportAfterExpiry }).FirstOrDefaultAsync();
            if (tc != null) { blockReads = tc.BlockAccessOnExpiry; allowExport = tc.AllowDataExportAfterExpiry; }
        }
        return new SubscriptionGateState(ov.Plan, ov.Status, ov.Features, blockReads, allowExport);
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
        // รอบ 231 (คำตัดสินข้อ 231-9 · §B.8): เปลี่ยนรอบบิลให้ยาวขึ้นกลางรอบ (แพ็กเกจเดิม หรือพร้อมอัปเกรด) ⇒ จ่ายรอบใหม่เต็ม หักมูลค่าคงเหลือรอบเดิม ·
        // รอบใหม่เริ่มวันที่ชำระ · สิทธิ์สลับเมื่อเงินเข้า · สั้นลง/ดาวน์เกรด = เส้น Scheduled* เดิม (มีผลวันครบรอบ ไม่คืนเงิน)
        if (cycle != sub.BillingCycle && kind != PlatformPlanPricing.ChangeKind.Downgrade
            && PlatformPlanPricing.MonthsOf(cycle) > PlatformPlanPricing.MonthsOf(sub.BillingCycle))
        {
            var cc = await CycleChangeForAsync(sub, current, template, cycle, DateTime.UtcNow);
            if (cc.Applies)
            {
                var ccPay = await RequestPlanPaymentAsync(sub, template, cycle, null, performedBy, "CycleChangeRequested",
                    $"ขอเปลี่ยนรอบบิล {sub.BillingCycle} → {cycle}" + (template.Plan != sub.Plan ? $" พร้อมเปลี่ยนเป็น {template.Plan}" : ""),
                    couponCode: request.CouponCode, cycleChange: cc);
                return new PlanPurchaseResponse(await GetSubscriptionAsync(companyId), ccPay,
                    $"สร้างรายการชำระ {ccPay.PaymentNumber} ยอด {ccPay.Amount:N2} บาทแล้ว ({cc.Note}) — "
                    + (template.Plan != sub.Plan ? $"เปลี่ยนเป็น {template.Name} ราย{PlatformPlanPricing.CycleLabel(cycle)}" : $"รอบบิลราย{PlatformPlanPricing.CycleLabel(cycle)}")
                    + " มีผลทันทีเมื่อชำระเงิน · รอบใหม่เริ่มวันที่ชำระ (วันหมดอายุเดิมไม่ใช้แล้ว)");
            }
        }
        if (kind == PlatformPlanPricing.ChangeKind.Upgrade)
        {
            // รอบ 230 (คำตัดสินข้อ 230-Q1 · §7.1): อัปเกรดกลางรอบ = จ่ายส่วนต่างของวันที่เหลือ · วันหมดอายุคงเดิม (แทน "เริ่มรอบใหม่ — ส่วนที่เหลือสละ")
            var pr = await ProrationForAsync(sub, current, template, DateTime.UtcNow);
            if (pr.Applies)
            {
                var prorated = await RequestPlanPaymentAsync(sub, template, sub.BillingCycle, null, performedBy,
                    "PlanUpgradeProrated", $"ขออัปเกรดจาก {sub.Plan} (ส่วนต่างตามวันที่เหลือ)", pr, request.CouponCode);
                return new PlanPurchaseResponse(await GetSubscriptionAsync(companyId), prorated,
                    $"สร้างรายการชำระส่วนต่าง {prorated.PaymentNumber} ยอด {prorated.Amount:N2} บาทแล้ว ({pr.Note}) — อัปเกรดเป็น {template.Name} มีผลทันทีเมื่อชำระเงิน "
                    + $"· ใช้ได้ถึงวันเดิม {sub.EndDate.AddHours(7):dd/MM/yyyy} · รอบถัดไปคิดราคา {template.Name}"
                    + (cycle != sub.BillingCycle ? " · รอบบิลสั้นลงคงเดิม — เปลี่ยนรอบบิลสั้นลงได้หลังอัปเกรด (มีผลวันครบรอบ)" : ""));
            }
            var payment = await RequestPlanPaymentAsync(sub, template, cycle, null, performedBy,
                "PlanUpgradeRequested", $"ขออัปเกรดจาก {sub.Plan}", couponCode: request.CouponCode);
            return new PlanPurchaseResponse(await GetSubscriptionAsync(companyId), payment,
                $"สร้างรายการชำระ {payment.PaymentNumber} ยอด {payment.Amount:N2} บาทแล้ว — อัปเกรดเป็น {template.Name} มีผลทันทีเมื่อชำระเงินและผู้ดูแลระบบยืนยัน "
                + $"(เริ่มรอบใหม่จากวันที่ชำระ · {pr.NotApplicableReason})");
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

    /// <summary>รอบ 231 (ฝ่ายค้าน S2): ฐาน "ราคาเดิมต่อรอบ" ของส่วนต่างอัปเกรด/เครดิตเปลี่ยนรอบ = <b>ยอดที่จ่ายจริง</b>ของรอบนั้น
    /// (<see cref="PlatformPriceOverrideStore.LastApprovedCycleAmountAsync"/> — หลังคูปอง/แก้ยอด) · ไม่เคยจ่าย (แอดมินเปิดให้) ⇒ ราคาพิเศษ/แม่แบบของแพ็กเกจเดิม ·
    /// ไม่มีแม่แบบ ⇒ <c>PricePerCycle</c> ที่บันทึก — สูตร pure <see cref="PlatformProration.OldPriceBasis"/></summary>
    private async Task<decimal> OldCyclePriceBasisAsync(Subscription sub, PlanTemplate? current, BillingCycle cycle, DateTime nowUtc)
    {
        var lastPaid = await PlatformPriceOverrideStore.LastApprovedCycleAmountAsync(_db, sub.Id, sub.Plan, cycle);
        var resolved = current != null
            ? (await PlatformPriceOverrideStore.ResolveForSubscriptionAsync(_db, sub, current, cycle, nowUtc, forRenewal: true)).Price
            : sub.PricePerCycle;
        return PlatformProration.OldPriceBasis(lastPaid, resolved);
    }

    /// <summary>รอบ 230: ส่วนต่างอัปเกรดของ subscription นี้ (ตัวเดียวของ "ขออัปเกรด" และ "ดูราคา") — ราคาเดิม/ใหม่ต่อรอบบิล<b>ปัจจุบัน</b> ตามตารางแพ็กเกจ ·
    /// ไม่มีแม่แบบของแพ็กเกจเดิม ⇒ ราคาต่อรอบที่บันทึกบน subscription</summary>
    /// <para>รอบ 231 (231-8): ราคา<b>ทั้งสองฝั่ง</b>ผ่านตัวตัดสินราคาพิเศษ — ฝั่งเดิม = ราคาที่บริษัทจ่ายจริง (override ของแพ็กเกจเดิมถ้ามี · ไม่งั้นแม่แบบ ·
    /// ไม่มีแม่แบบ = <c>PricePerCycle</c> ที่บันทึก) · ฝั่งใหม่ = ราคาพิเศษของแพ็กเกจใหม่ — ใช้ราคาตั้งฝั่งเดิมจะคิดส่วนต่างมาก/น้อยเกิน (§C.2)</para>
    private async Task<PlatformProration.Result> ProrationForAsync(Subscription sub, PlanTemplate? current, PlanTemplate target, DateTime nowUtc)
    {
        var oldPrice = await OldCyclePriceBasisAsync(sub, current, sub.BillingCycle, nowUtc);
        var newPrice = (await PlatformPriceOverrideStore.ResolveForSubscriptionAsync(_db, sub, target, sub.BillingCycle, nowUtc,
            forRenewal: PlatformPlanPricing.IsRenewalPurchase(sub.Plan, target.Plan))).Price;
        return PlatformProration.Upgrade(oldPrice, newPrice,
            PlatformPlanPricing.MonthsOf(sub.BillingCycle), sub.EndDate, nowUtc,
            planActive: sub.Status == SubscriptionStatus.Active, isTrial: sub.Status == SubscriptionStatus.Trial, isPermanentFree: sub.IsPermanentFree);
    }

    /// <summary>รอบ 231 (231-9): เปลี่ยนรอบบิลกลางรอบ (ตัวเดียวของ "ขอเปลี่ยน" และ "ดูราคา") — ราคาเดิมต่อรอบเดิม / ราคาใหม่ต่อรอบใหม่ ผ่านราคาพิเศษทั้งสองฝั่ง</summary>
    private async Task<PlatformProration.CycleChangeResult> CycleChangeForAsync(Subscription sub, PlanTemplate? current, PlanTemplate target,
        BillingCycle newCycle, DateTime nowUtc)
    {
        var oldPrice = await OldCyclePriceBasisAsync(sub, current, sub.BillingCycle, nowUtc);
        var newPrice = (await PlatformPriceOverrideStore.ResolveForSubscriptionAsync(_db, sub, target, newCycle, nowUtc,
            forRenewal: PlatformPlanPricing.IsRenewalPurchase(sub.Plan, target.Plan))).Price;
        return PlatformProration.CycleChange(oldPrice, PlatformPlanPricing.MonthsOf(sub.BillingCycle), newPrice, PlatformPlanPricing.MonthsOf(newCycle),
            sub.EndDate, nowUtc, planActive: sub.Status == SubscriptionStatus.Active, isTrial: sub.Status == SubscriptionStatus.Trial, isPermanentFree: sub.IsPermanentFree);
    }

    /// <summary>รอบ 230 (D-8 · คำตัดสินข้อ 230-Q1/230-Q2): ดูราคาก่อนยืนยัน — ใช้ตัวตัดสินเดียวกับ <see cref="ConvertTrialAsync"/>/<see cref="ChangeSubscriptionAsync"/>
    /// (จำแนกทิศ · ส่วนต่าง · ราคาเต็ม · คูปอง) · ไม่สร้าง/ไม่จองอะไร</summary>
    public async Task<PlanChangeQuote> QuotePlanChangeAsync(Guid companyId, SubscriptionPlan plan, BillingCycle? cycleRequested, string? couponCode)
    {
        var q = await QuotePlanChangeCoreAsync(companyId, plan, cycleRequested, couponCode);
        return q with { Preview = await PlanPreviewForQuoteAsync(companyId, plan, q, DateTime.UtcNow) };
    }

    /// <summary>รอบ 233 (คำตัดสิน 233-*): แปลงใบเสนอ (ตัวเลขจากตัวคิดราคาเดิม) เป็น "สิ่งที่จะได้" ผ่าน <see cref="PlanPurchasePreview"/> ตัวเดียว —
    /// วันที่มาจาก <see cref="PlanPurchasePreview.PaidPeriod"/> ตัวเดียวกับตอนอนุมัติชำระ (ReviewPaymentLockedAsync) · อ่านอย่างเดียว</summary>
    private async Task<PlanPurchasePreview.Result> PlanPreviewForQuoteAsync(Guid companyId, SubscriptionPlan plan, PlanChangeQuote q, DateTime nowUtc)
    {
        var sub = await _db.Subscriptions.AsNoTracking().FirstAsync(s => s.CompanyId == companyId);
        var currentTpl = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Plan == sub.Plan && p.IsActive);
        var targetTpl = await _db.PlanTemplates.AsNoTracking().FirstAsync(p => p.Plan == plan && p.IsActive);
        var cycle = Enum.TryParse<BillingCycle>(q.Cycle, out var qc) ? qc : sub.BillingCycle;
        var cur = PlanPurchasePreview.CurrentOf(sub, currentTpl);
        var tgt = PlanPurchasePreview.TargetOf(targetTpl, cycle);
        if (q.Kind == "Same") return PlanPurchasePreview.ForSame(cur, tgt, nowUtc);
        if (q.AmountDue <= 0m && q.ListAmount <= 0m && q.EffectiveAt is DateTime eff)
            return PlanPurchasePreview.ForScheduled(cur, tgt, eff, q.Blocked, nowUtc);
        // ฝ่ายค้าน N3: ธง VAT ตัวตัดสินเดียวกับตัวออกใบ (จด VAT + เลขผู้เสียภาษีถูกต้อง)
        var vs = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt)
            .Select(s => new { s.PlatformIsVatRegistered, s.PlatformSellerTaxId }).FirstOrDefaultAsync();
        var vat = vs != null && PlanPurchasePreview.IssuesTaxInvoice(vs.PlatformIsVatRegistered, vs.PlatformSellerTaxId);
        return PlanPurchasePreview.ForPurchase(cur, tgt, new PlanPurchasePreview.Money(q.ListAmount, q.Discount, q.AmountDue,
            Prorated: q.Prorated, CycleChange: q.Kind == "CycleChange" && q.CycleChangeCredit != null, OldPriceBasis: q.OldPriceBasis,
            DaysRemaining: q.DaysRemaining, CycleDays: q.CycleDays, CycleChangeCredit: q.CycleChangeCredit,
            CouponLabel: q.CouponLabel, CouponProblem: q.CouponProblem, SpecialPriceNote: q.SpecialPriceNote, NewCyclePrice: q.NewCyclePrice), nowUtc, vat);
    }

    private async Task<PlanChangeQuote> QuotePlanChangeCoreAsync(Guid companyId, SubscriptionPlan plan, BillingCycle? cycleRequested, string? couponCode)
    {
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");
        var template = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Plan == plan && p.IsActive)
            ?? throw new KeyNotFoundException($"ไม่พบ plan template สำหรับ {plan}");
        var current = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Plan == sub.Plan && p.IsActive);
        var cycle = cycleRequested ?? sub.BillingCycle;
        var now = DateTime.UtcNow;

        // รอบ 231 (231-8): ราคาพิเศษของแพ็กเกจเป้าหมายรอบที่ขอ — ตัวตัดสินเดียวกับตอนยืนยัน (RequestPlanPaymentAsync) · StandardAmount = ราคาตั้ง
        var effective = await PlatformPriceOverrideStore.ResolveForSubscriptionAsync(_db, sub, template, cycle, now, forRenewal: PlatformPlanPricing.IsRenewalPurchase(sub.Plan, template.Plan));   // ฝ่ายค้าน S4
        var specialNote = PlatformPlanPricing.SpecialPriceNote(effective, cycle);
        // ฝ่ายค้าน S10: ส่วนต่างอัปเกรดคิดบน "รอบปัจจุบัน" ไม่ใช่รอบที่ขอ ⇒ ราคาตั้ง/ป้ายราคาพิเศษของบรรทัดนั้นต้องมาจากรอบที่คิดจริง
        var effectiveCurrentCycle = cycle == sub.BillingCycle ? effective
            : await PlatformPriceOverrideStore.ResolveForSubscriptionAsync(_db, sub, template, sub.BillingCycle, now, forRenewal: PlatformPlanPricing.IsRenewalPurchase(sub.Plan, template.Plan));
        var specialNoteCurrentCycle = PlatformPlanPricing.SpecialPriceNote(effectiveCurrentCycle, sub.BillingCycle);

        async Task<PlanChangeQuote> Pay(string kind, bool prorated, BillingCycle c, decimal list, int? d, int? cd, DateTime? end, string msg,
            decimal? standard = null, decimal? credit = null, DateTime? newEnd = null, string? special = null, decimal? oldBasis = null, decimal? newCyclePrice = null)
        {
            var ev = await PlatformCouponStore.PreviewAsync(_db, companyId, couponCode, PlatformCouponScope.Subscription,
                plan.ToString(), c.ToString(), null, list, now);
            var ok = ev is { Ok: true };
            return new PlanChangeQuote(kind, prorated, plan.ToString(), c.ToString(), list, ok ? ev!.Discount : 0m, ok ? ev!.AmountAfter : list,
                d, cd, end, null, msg, ok ? ev!.Label : null, ev is { Ok: false } ? ev.Problem : null,
                StandardAmount: standard, SpecialPriceNote: standard is decimal s && s > list ? (special ?? specialNote) : null,
                CycleChangeCredit: credit, NewPeriodEndUtc: newEnd, OldPriceBasis: oldBasis, NewCyclePrice: newCyclePrice);
        }

        // ทดลองใช้/หมดอายุ/ฟรีถาวร ⇒ ซื้อเต็มรอบ เริ่มรอบใหม่จากวันที่ชำระ (เส้น ConvertTrialAsync)
        if (sub.Status != SubscriptionStatus.Active || sub.IsPermanentFree)
        {
            var full = PlatformPlanPricing.AmountDue(effective.Price, null);
            var standardFull = PlatformPlanPricing.AmountDue(effective.ListPrice, null);
            // ฝ่ายค้าน N6: โค้ดโปรโมชันแปลงจากทดลองใช้ของบริษัทนี้ (TrialConfig) — เงื่อนไขเดียวกับ ConvertTrialAsync · ใช้ก่อนคูปองแพลตฟอร์ม
            var trial = await _db.TrialConfigs.AsNoTracking().FirstOrDefaultAsync(t => t.SubscriptionId == sub.Id);
            if (!string.IsNullOrEmpty(couponCode) && trial != null && trial.ConversionPromoCode == couponCode
                && trial.DiscountPercentOnConversion.HasValue && (trial.DiscountValidUntil == null || trial.DiscountValidUntil > now))
            {
                var promo = PlatformPlanPricing.AmountDue(effective.Price, trial.DiscountPercentOnConversion.Value);
                return new PlanChangeQuote("New", false, plan.ToString(), cycle.ToString(), full, PlatformCouponRule.R2(full - promo), promo, null, null, null, null,
                    $"ชำระ {template.Name} {PlatformPlanPricing.MonthsOf(cycle)} เดือน — เริ่มรอบใหม่จากวันที่ชำระ (โปรโมชันแปลงจากทดลองใช้ ลด {trial.DiscountPercentOnConversion:0.##}% · ผู้ดูแลระบบตรวจยอดก่อนเปิด)",
                    $"โปรโมชันแปลงจากทดลองใช้ {trial.DiscountPercentOnConversion:0.##}%",
                    StandardAmount: standardFull, SpecialPriceNote: effective.Applied ? specialNote : null);
            }
            return await Pay("New", false, cycle, full, null, null, null,
                $"ชำระ {template.Name} {PlatformPlanPricing.MonthsOf(cycle)} เดือน — เริ่มรอบใหม่จากวันที่ชำระ", standard: standardFull);
        }

        var kind = PlatformPlanPricing.Classify(current?.MonthlyPrice ?? 0m, template.MonthlyPrice);
        // รอบ 231 (231-9): เปลี่ยนรอบบิลยาวขึ้นกลางรอบ — ตัวตัดสินเดียวกับ ChangeSubscriptionAsync
        if (cycle != sub.BillingCycle && kind != PlatformPlanPricing.ChangeKind.Downgrade
            && PlatformPlanPricing.MonthsOf(cycle) > PlatformPlanPricing.MonthsOf(sub.BillingCycle))
        {
            var cc = await CycleChangeForAsync(sub, current, template, cycle, now);
            if (cc.Applies)
                return await Pay("CycleChange", false, cycle, cc.Amount, cc.DaysRemaining, cc.CycleDays, sub.EndDate,
                    $"เปลี่ยนเป็นราย{PlatformPlanPricing.CycleLabel(cycle)}: {cc.Note} — มีผลทันทีเมื่อชำระ · รอบใหม่ {cc.NewCycleMonths} เดือน นับจากวันที่ชำระ (วันหมดอายุเดิมไม่ใช้แล้ว)",
                    standard: effective.Applied ? PlatformPlanPricing.AmountDue(effective.ListPrice, null) : null, credit: cc.Credit, newEnd: cc.NewEndUtc,
                    newCyclePrice: effective.Price);   // ฝ่ายค้าน N2: ราคาเดียวกับที่ CycleChangeForAsync ส่งเข้าตัวคิด (target · รอบใหม่ · ราคาพิเศษ)
        }
        if (kind == PlatformPlanPricing.ChangeKind.Upgrade)
        {
            var pr = await ProrationForAsync(sub, current, template, now);
            if (pr.Applies)
                return await Pay("Upgrade", true, sub.BillingCycle, pr.Amount, pr.DaysRemaining, pr.CycleDays, sub.EndDate,
                    $"จ่ายส่วนต่างเฉพาะวันที่เหลือ ({pr.Note}) — เปลี่ยนเป็น {template.Name} ทันทีเมื่อชำระ · ใช้ได้ถึงวันเดิม · รอบถัดไปคิดราคา {template.Name}"
                    + (cycle != sub.BillingCycle ? " · รอบบิลสั้นลงคงเดิม (เปลี่ยนรอบบิลสั้นลงได้หลังอัปเกรด มีผลวันครบรอบ)" : ""),
                    standard: effectiveCurrentCycle.Applied ? PlatformPlanPricing.AmountDue(effectiveCurrentCycle.ListPrice, null) : null, special: specialNoteCurrentCycle,   // S10
                    oldBasis: pr.OldPricePerCycle);   // รอบ 233: แตกบรรทัดเครดิตแพ็กเดิมใน preview
            var daysLeft = UsageBarRule.DaysLeft(sub.EndDate, now);
            return await Pay("Upgrade", false, cycle, PlatformPlanPricing.AmountDue(effective.Price, null), null, null, null,
                $"ชำระ {template.Name} เต็มรอบ — เริ่มรอบใหม่จากวันที่ชำระ ({pr.NotApplicableReason})"
                + (daysLeft > 0 ? $" · วันที่เหลือของแพ็กเกจเดิม {daysLeft} วันไม่ถูกนำมาหัก" : ""),
                standard: PlatformPlanPricing.AmountDue(effective.ListPrice, null));
        }
        if (template.Plan == sub.Plan && cycle == sub.BillingCycle)
            return new PlanChangeQuote("Same", false, plan.ToString(), cycle.ToString(), 0m, 0m, 0m, null, null, sub.EndDate, null,
                "แพ็กเกจและรอบบิลนี้คือแพ็กเกจปัจจุบันอยู่แล้ว");
        var blocked = PlatformPlanPricing.ScheduleBlockedByIssuedRenewal(sub.RenewalInvoiceForEndDate, sub.EndDate);
        var effectiveAt = PlatformPlanPricing.ScheduledChangeEffectiveAt(sub.IsPermanentFree, sub.EndDate, now);
        return new PlanChangeQuote(kind == PlatformPlanPricing.ChangeKind.Downgrade ? "Downgrade" : "CycleChange", false, plan.ToString(), cycle.ToString(),
            0m, 0m, 0m, null, null, sub.EndDate, effectiveAt,
            blocked ?? $"ไม่ต้องชำระตอนนี้ — เปลี่ยนเป็น {template.Name} มีผล {effectiveAt.AddHours(7):dd/MM/yyyy} (ใช้แพ็กเกจเดิมได้จนถึงวันนั้น · ไม่คืนเงินส่วนต่าง)",
            Blocked: blocked);
    }

    /// <summary>รอบ 230 (คำตัดสินเจ้าของข้อ 178 ① — แอดมินยืดหยุ่นได้เสมอ): แก้ยอดของรายการชำระค่าแพ็กเกจที่<b>ยังไม่จ่าย</b> (เช่น ส่วนลดพิเศษ · ส่วนต่างที่ตกลงกัน) —
    /// ยอดใหม่ = ยอดที่ช่องทางออนไลน์อนุมัติอัตโนมัติ (<c>QuotedAmount</c>) · เหตุผลบังคับ · audit chain <c>PLATFORM-ADMIN-PRICE-OVERRIDE</c> ·
    /// ห้ามเมื่อมีการชำระออนไลน์ค้าง (ยอดใน QR/บัตรเดิมจะไม่ตรง) · ใบแจ้งหนี้ที่ผูกไว้ ⇒ ใช้ช่อง "เงินที่รับจริง" ตอนอนุมัติแทน</summary>
    public async Task<SubscriptionPaymentResponse> OverridePaymentAmountAsync(Guid paymentId, decimal amount, string reason, string performedBy)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("ระบุเหตุผลที่แก้ยอด");
        var newAmount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (newAmount <= 0m)
            throw new BusinessRuleException("ยอดต้องมากกว่า 0 — ไม่เก็บเงินเลย ให้ใช้ \"บันทึกรับเงินเอง\" แบบยกเว้นค่าบริการแทน");
        var subscriptionId = await _db.SubscriptionPayments.AsNoTracking().Where(p => p.Id == paymentId)
            .Select(p => (Guid?)p.SubscriptionId).FirstOrDefaultAsync() ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");
        await using var lockTx = await SubscriptionPaymentLock.AcquireAsync(_db, subscriptionId);
        var payment = await _db.SubscriptionPayments.Include(p => p.Subscription).FirstAsync(p => p.Id == paymentId);
        if (_db.Database.IsRelational()) await _db.Entry(payment).ReloadAsync();
        if (payment.Status is not (SubscriptionPaymentStatus.Pending or SubscriptionPaymentStatus.UnderReview))
            throw new BusinessRuleException("แก้ยอดได้เฉพาะรายการที่ยังไม่อนุมัติ/ไม่ปฏิเสธ", "PLATFORM-ADMIN-PRICE-OVERRIDE", 409);
        if (payment.PlatformInvoiceDocumentId != null)
            throw new BusinessRuleException("รายการนี้ชำระใบแจ้งหนี้ — ยอดตามใบ (กรอก \"เงินที่รับจริง\" ตอนอนุมัติ หรือออกใบลดหนี้)", "PLATFORM-ADMIN-PRICE-OVERRIDE", 409);
        var liveIntent = await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.SourceKind == PaymentSourceKind.SubscriptionPayment && i.SourceId == payment.Id
            && (i.Status == PaymentIntentStatus.Created || i.Status == PaymentIntentStatus.Pending || i.Status == PaymentIntentStatus.Succeeded));
        if (liveIntent)
            throw new BusinessRuleException("มีการชำระออนไลน์ของรายการนี้ค้าง/สำเร็จแล้ว — แก้ยอดไม่ได้ (รอหมดอายุ หรืออนุมัติตามเงินที่รับจริง)", "PLATFORM-ADMIN-PRICE-OVERRIDE", 409);
        var before = payment.Amount;
        var beforeDiscount = payment.CouponDiscountAmount;
        payment.Amount = newAmount;
        payment.QuotedAmount = newAmount;
        // ฝ่ายค้าน S5: รายการมีคูปอง ⇒ ส่วนลดบนใบ = ราคาก่อนคูปอง − ยอดใหม่ (ไม่ติดลบ) — ไม่งั้นใบกำกับบวกส่วนลดเดิมกลับเป็น "ราคาเต็ม" ที่ไม่จริง ·
        // ยอดใหม่ ≥ ราคาก่อนคูปอง ⇒ ปลดคูปอง (ไม่นับการใช้) · แก้แถวการใช้ในธุรกรรมเดียวกัน
        if (payment.CouponId != null && payment.ListAmount is decimal listBeforeCoupon)
        {
            var d = await PlatformCouponStore.AdjustForOverrideAsync(_db, payment.Subscription.CompanyId, PlatformCouponRule.SourceSubscriptionPayment, payment.Id,
                listBeforeCoupon, newAmount, performedBy);
            payment.CouponDiscountAmount = d;
            if (d <= 0m) { payment.CouponId = null; payment.ListAmount = null; }
        }
        payment.AmountOverrideReason = reason.Trim().Length > 500 ? reason.Trim()[..500] : reason.Trim();
        payment.UpdatedAt = DateTime.UtcNow; payment.UpdatedBy = performedBy;
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = payment.SubscriptionId, Action = "PaymentAmountOverridden",
            Notes = $"แอดมินแก้ยอด {payment.PaymentNumber} {before:N2} → {newAmount:N2} บาท · {payment.AmountOverrideReason}", PerformedBy = performedBy
        });
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = payment.Subscription.CompanyId, Action = AuditAction.Update, EntityType = nameof(SubscriptionPayment), EntityId = payment.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { amount = before, couponDiscount = beforeDiscount }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "PlatformAdminPriceOverride", ruleCode = "PLATFORM-ADMIN-PRICE-OVERRIDE", amount = newAmount, couponDiscount = payment.CouponDiscountAmount,
                slipAttached = payment.SlipFileName != null, reason = payment.AmountOverrideReason, by = performedBy,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        if (lockTx != null) await lockTx.CommitAsync();
        return MapPaymentToResponse(payment);
    }

    public async Task<string> CancelSubscriptionAsync(Guid companyId, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");

        // รอบ 219 ส่วน B (คำตัดสินข้อ 179): แพ็กเกจที่ชำระแล้วยังไม่สิ้นรอบ ⇒ ใช้ได้ถึงสิ้นรอบ (นโยบายคืนเงินฉบับตั้งต้นสัญญาไว้เช่นนี้)
        var now = DateTime.UtcNow;
        var outcome = SubscriptionCancelPolicy.Decide(sub.Status, sub.EndDate, now, sub.IsPermanentFree);
        if (outcome == SubscriptionCancelPolicy.Outcome.AtPeriodEnd)
        {
            sub.CancelAtPeriodEnd = true;
            sub.CancelRequestedAt = now;
            // ฝ่ายค้าน S5: ยกเลิกแล้ว ⇒ การเปลี่ยนแพ็กเกจที่รอมีผลไม่มีความหมาย · ใบแจ้งหนี้ต่ออายุที่ออกแล้ว<b>คงตัวชี้ไว้</b> — เฟส 2 ใช้ตัวชี้นี้
            // ยกเว้นใบจากการทวงหนี้ (PlatformDunningService) + ห้ามจ่าย (PlatformInvoiceRules.RenewalNotPayableReason) + "ใช้ต่อ" แล้วจ่ายใบเดิมได้ (ล้าง = ใบซ้ำ/ทวงผิด)
            sub.ScheduledPlan = null; sub.ScheduledBillingCycle = null; sub.ScheduledPlanEffectiveAt = null;
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = "CancelScheduled",
                FromStatus = sub.Status,
                ToStatus = sub.Status,
                Notes = $"ยกเลิกมีผลสิ้นรอบ {sub.EndDate:yyyy-MM-dd} (ใช้งานได้ถึงวันดังกล่าว · ไม่ต่ออายุ)",
                PerformedBy = performedBy
            });
            await _db.SaveChangesAsync();
            return SubscriptionCancelPolicy.Message(outcome, sub.EndDate);
        }

        // เก็บสถานะเดิม "ก่อน" mutate — เดิมอ่านหลังตั้ง Cancelled ทำให้ history
        // ได้ From=Cancelled→Cancelled ทุกครั้ง audit สืบสถานะก่อนยกเลิกไม่ได้
        var cancelFromStatus = sub.Status;
        sub.Status = SubscriptionStatus.Cancelled;
        sub.CancelledAt = now;
        sub.CancelAtPeriodEnd = false;

        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "Cancelled",
            FromStatus = cancelFromStatus,
            ToStatus = SubscriptionStatus.Cancelled,
            PerformedBy = performedBy
        });

        await _db.SaveChangesAsync();
        return SubscriptionCancelPolicy.Message(outcome, sub.EndDate);
    }

    public async Task<string> ResumeSubscriptionAsync(Guid companyId, string performedBy)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");
        if (SubscriptionCancelPolicy.ResumeRefusal(sub.CancelAtPeriodEnd, sub.Status, sub.EndDate, DateTime.UtcNow) is string refusal)
            throw new BusinessRuleException(refusal, "SUB-CANCEL-RESUME");
        sub.CancelAtPeriodEnd = false;
        sub.CancelRequestedAt = null;
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "CancelRevoked",
            FromStatus = sub.Status,
            ToStatus = sub.Status,
            Notes = "กลับมาใช้ต่อก่อนสิ้นรอบ (ยกเลิกการยกเลิก)",
            PerformedBy = performedBy
        });
        await _db.SaveChangesAsync();
        return "กลับมาใช้แพ็กเกจต่อแล้ว — ต่ออายุตามรอบบิลเดิม";
    }

    // ==================== Usage Tracking ====================

    /// <summary>รอบ 229 ฝ่ายค้าน S3: เรื่องค้างบนรายการชำระ — ต่ออายุแพ็กเกจแล้วแต่โควตาเสริมในใบไม่ได้รอบใหม่</summary>
    public const string QuotaRenewalAttentionRule = "PLATFORM-QUOTA-RENEWAL-FAILED";

    private async Task ReleaseStaleQuotaRenewalLinesAsync(SubscriptionPayment payment, Guid staleInvoiceId, string actor)
    {
        try
        {
            var cid = payment.Subscription.CompanyId;
            var rows = await _db.CompanyQuotaAddOns.Where(p => p.CompanyId == cid && p.RenewalInvoiceDocumentId == staleInvoiceId).ToListAsync();
            if (rows.Count == 0) return;
            foreach (var r in rows) { r.RenewalInvoiceDocumentId = null; r.RenewUnitPrice = null; r.UpdatedAt = DateTime.UtcNow; r.UpdatedBy = actor; }
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = cid, Action = AuditAction.Update, EntityType = nameof(CompanyQuotaAddOn), EntityId = staleInvoiceId.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "QuotaRenewalLinesReleased", invoiceId = staleInvoiceId, rows = rows.Select(r => r.Id), payment = payment.PaymentNumber, by = actor }),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
            var msg = $"ใบแจ้งหนี้ต่ออายุเดิมถูกแทนที่ด้วยรายการ {payment.PaymentNumber} — โควตาเสริม {rows.Count} รายการในใบเดิมยังไม่ได้ต่อ "
                + "(ใช้ได้ถึงวันหมดอายุเดิมที่จ่ายไว้ · ข้อมูลไม่หาย) · ต่อได้โดยซื้อแบบ \"ถึงวันหมดอายุแพ็กเกจ\" ที่หน้า \"แพ็กเกจและการชำระเงิน\"";
            var ownerId = await _db.CompanyUsers.Where(cu => cu.CompanyId == cid && cu.Role == UserRole.Owner).Select(cu => cu.UserId).FirstOrDefaultAsync();
            if (ownerId != Guid.Empty)
                await NotifyUserAsync(ownerId, cid, Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded, NotificationType.SubscriptionPaymentApproved,
                    "โควตาเสริมยังไม่ได้ต่ออายุ", msg, "/pages/billing.html", "SubscriptionPayment", payment.Id);
            await PlatformPaymentAttention.NotifyAdminsAsync(_db, _notificationService, $"โควตาเสริมหลุดจากการต่ออายุ ({payment.PaymentNumber})", msg, payment.Id);
        }
        catch (Exception ex) { _logger?.LogError(ex, "ปลดบรรทัดโควตาเสริมของใบต่ออายุที่ถูกแทนที่ไม่สำเร็จ (ใบ {Invoice})", staleInvoiceId); }
    }

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
        // รอบ 231 (§D.1): snapshot ค่าปิดเดือน (เอกสาร/OCR) **ก่อน** ล้างตัวนับ — INSERT … ON CONFLICT บนคอนเนกชันเดียวกัน (ไม่ถึงวันรีเซ็ต = ไม่เขียน)
        await UsageSnapshotService.WriteRolloverAsync(_db, sub, DateTime.UtcNow);
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
        if (status.UsedBytes + additionalBytes <= status.MaxBytes) return true;
        // รอบ 231 (§B.4 · คำตัดสินข้อ 231-2): เกินเพดาน + ผู้เช่าเปิด "ใช้ต่อได้ คิดค่าส่วนเกิน" พื้นที่ (มีราคา · ไม่ถึงเพดานเงิน · ไม่ถูกล็อก) ⇒ อัปโหลดได้ ·
        // คิดเป็น GB-เดือนสิ้นเดือนจากตัวอย่างรายวัน (UsageSnapshotJob) ไม่ใช่ต่อไฟล์ — ด่านนี้แค่ "ยอมให้เกิน"
        if (_overage == null) return false;
        var overGb = (int)Math.Min(int.MaxValue, (status.UsedBytes + additionalBytes - status.MaxBytes + Models.Constants.QuotaResources.BytesPerGb - 1) / Models.Constants.QuotaResources.BytesPerGb);
        return (await _overage.DecideAsync(companyId, Models.Constants.OverageResources.StorageGb, Math.Max(1, overGb))).Allowed;
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
        // รอบ 229 (QUOTA_ADDON §B.4): override รายบริษัทของแอดมิน (GB · -1 = ไม่จำกัด) ชนะช่องเดิม · + พื้นที่ที่บริษัทนี้ซื้อเพิ่มและยังมีผล
        // (ตัวอ่านเดียวกับด่าน/หน้าสรุป — QuotaStore) · ไม่มีแถว = เพดานเดิมทุกบาท (ไม่กระทบผู้เช่าเดิม)
        maxBytes = await Accounting.Helpers.QuotaStorageLimit.ApplyAsync(_db, companyId, maxBytes);
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
                // รอบ 231 (คำตัดสินข้อ 231-5): เกณฑ์เตือนตามที่แอดมินตั้ง (PlatformBillingSettings.QuotaWarnPercents · QuotaWarnPolicy) —
                // ค่าที่เก็บ = % ที่เพิ่งข้าม (80/90/100) แจ้งครั้งเดียวต่อเกณฑ์ต่อเดือน (เดิมระดับ 1/2 ตายตัว 80/100)
                var warnPercents = (await PlatformBillingSettingsService.LoadPolicyAsync(_db)).WarnPercents;
                crossedLevel = Accounting.Helpers.DocumentQuotaPolicy.CrossedWarnLevel(usedBefore, limitBefore,
                    usedAfter, limitAfter, warnPercents);
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
            // รอบ 231 (P0 ข · คำตัดสินข้อ 231-5): level = % เกณฑ์ที่ข้าม (80/90/100) · ลิงก์ไปแท็บ "ใช้ไปเท่าไร" ของ hub (เดิม /pages/addons.html) ·
            // ฝ่ายค้าน S7: ผู้รับ = เจ้าของ + ผู้มีสิทธิ์ BillingManage (ตัวเดียว OverageService.BillingRecipientsAsync — เส้นเดียวกับทรัพยากรอื่น)
            foreach (var uid in await OverageService.BillingRecipientsAsync(_db, companyId, default))
                await NotifyUserAsync(uid, companyId,
                    Models.Constants.NotificationEvents.DocumentQuotaLevel, NotificationType.System,
                    Accounting.Helpers.QuotaWarnPolicy.Title("เอกสาร", level),
                    Accounting.Helpers.DocumentQuotaPolicy.CrossedMessage(level >= 100 ? 2 : 1, used, limit),
                    Models.Constants.QuotaResources.UsageUrl, "Subscription", null);
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
                        sub.EndDate = SubscriptionExpiry.OpenEnded(DateTime.UtcNow);   // รอบ 232: ค่าแทน "ไม่หมดอายุ" ตัวเดียว
                        sub.ExpiryNeedsReview = false;
                        sub.EnabledFeatures = template.EnabledFeatures;
                    }
                    else
                    {
                        // ฝ่ายค้าน 232-hub B1(ก): แม่แบบเลิกฟรีถาวร ⇒ ห้ามทิ้งค่าแทน 2126 ไว้ (เคยหลุดถึงจอเป็น "2669") — ตั้งรอบใหม่ + ประวัติ/audit/แจ้งเจ้าของ
                        await CloseOpenEndedCoreAsync(sub, "System", $"แม่แบบ {template.Name} เลิกฟรีถาวร", DateTime.UtcNow, alreadyClosed: false);
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
        if (request.PurgeNoticeDays.HasValue) tc.PurgeNoticeDays = Math.Max(0, request.PurgeNoticeDays.Value);   // รอบ 231 (ฝ่ายค้าน B2)
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

        var ocrCredits = await _db.OcrCreditPurchases
            .Where(p => p.CompanyId == companyId && p.Status == "Approved"
                && p.PagesRemaining > 0
                && (p.ExpiresAt == null || p.ExpiresAt > DateTime.UtcNow))
            .SumAsync(p => p.PagesRemaining);

        // รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT P0 ก): เพดานที่มีผล = ตัวตัดสินเดียวกับด่าน/hub — เดิมอ่านช่องดิบ ⇒ ไม่รวม override แอดมิน/ที่นั่ง-GB ที่ซื้อเพิ่ม/โบนัสเอกสาร
        // ผู้ใช้: QuotaLimitPolicy.Effective (override · allowance · ช่องเดิม · ที่ซื้อ) — ตัวเดียวกับ QuotaGateService.LimitAsync (QuotaStore ใช้ได้โดยไม่เกิดวง DI)
        var limitNow = DateTime.UtcNow;
        var usersLimit = Accounting.Helpers.QuotaLimitPolicy.Effective(
            await QuotaStore.OverrideAsync(_db, companyId, Models.Constants.QuotaResources.Users),
            await QuotaStore.AllowanceAsync(_db, await QuotaStore.PlanTemplateIdAsync(_db, companyId), Models.Constants.QuotaResources.Users),
            status == SubscriptionStatus.Trial, maxUsers,
            await QuotaStore.PurchasesAsync(_db, companyId, Models.Constants.QuotaResources.Users), limitNow);
        maxUsers = usersLimit.IsUnlimited ? -1 : usersLimit.Effective!.Value;
        // พื้นที่: GetStorageStatusAsync (เพดานเดิม + override + ที่ซื้อ · pool ของ License) — ตัวตัดสินเดิมของด่านสื่อ CMS
        if (await GetStorageStatusAsync(companyId) is { } storageStatus) { maxStorage = storageStatus.MaxBytes; storageUsed = storageStatus.UsedBytes; }
        // เอกสาร: เพดานที่ด่านใช้จริง (โบนัส top-up/แอดมิน/ภารกิจ) + เพดานที่แสดงเมื่อเกิน (DisplayLimit — ไม่โชว์ "150/100")
        var docOverageThisMonth = (await _db.UsageEvents.AsNoTracking()
            .Where(u => u.CompanyId == companyId && !u.IsDeleted && !u.IsSandbox
                && (u.FeatureCode == Models.Constants.AddOnCodes.DocumentsOverage || u.FeatureCode == Models.Constants.OverageResources.FeatureCode(Models.Constants.OverageResources.Documents)))
            .Select(u => new { u.OccurredAt, u.Quantity }).ToListAsync())
            .Where(u => AddOnBilling.PeriodOf(u.OccurredAt) == AddOnBilling.PeriodOf(limitNow)).Sum(u => u.Quantity);
        var docEffective = Accounting.Helpers.DocumentQuotaPolicy.EffectiveLimit(maxDocs, sub.DocumentBonusQuota, sub.DocumentBonusExpiresAt, limitNow, used: docsUsed);
        var docBonusAlive = sub.DocumentBonusQuota > 0 && (sub.DocumentBonusExpiresAt == null || sub.DocumentBonusExpiresAt > limitNow);
        maxDocs = Accounting.Helpers.DocumentQuotaPolicy.DisplayLimit(docsUsed, docEffective, docOverageThisMonth);
        var warnPercents = (await PlatformBillingSettingsService.LoadPolicyAsync(_db)).WarnPercents;

        // รอบ 230 (D-4 · F2 ข้อ 5): หลอดทุกตัวคิดที่เซิร์ฟเวอร์ตัวเดียว (UsageBarRule) — เกณฑ์ "ไม่จำกัด"/สีเตือน/% ไม่อยู่ในหน้าเว็บอีก · คำเตือนข้างล่างอ่านระดับจากหลอดเดียวกัน
        // รอบ 231: เกณฑ์เตือนจากค่าตั้งของแอดมิน (QuotaWarnPolicy) ทุกหลอด
        var bars = new List<UsageBarRule.Bar>
        {
            UsageBarRule.Documents(docsUsed, maxDocs, warnPercents, docBonusAlive ? $"รวมโบนัสคงเหลือ {sub.DocumentBonusQuota:N0}" : null),
            UsageBarRule.Ocr(ocrUsed, maxOcr, sub.OcrBonusPages, ocrCredits, warnPercents),
            UsageBarRule.Users(companyUsers.Count, maxUsers, warnPercents),
            UsageBarRule.Storage(storageUsed, maxStorage, warnPercents),
        };
        var storageBar = bars[3]; var docBar = bars[0]; var userBar = bars[2];

        // Build alerts
        var alerts = new List<UsageAlert>();
        if (storageBar.Level == 2)
            alerts.Add(new UsageAlert("danger", "storage", $"พื้นที่เก็บข้อมูลใช้ไป {storageBar.Percent:F0}% แล้ว กรุณาเคลียร์ไฟล์หรืออัปเกรดแพ็กเกจ"));
        else if (storageBar.Level == 1)
            alerts.Add(new UsageAlert("warning", "storage", $"พื้นที่เก็บข้อมูลใช้ไป {storageBar.Percent:F0}% แล้ว"));

        if (docBar.Level == 2)
            alerts.Add(new UsageAlert("danger", "documents", $"เอกสารเดือนนี้ใช้ไป {docsUsed}/{maxDocs} รายการ"));
        else if (docBar.Level == 1)
            alerts.Add(new UsageAlert("warning", "documents", $"เอกสารเดือนนี้ใช้ไป {docBar.Percent:F0}%"));

        if (!userBar.Unlimited && companyUsers.Count >= maxUsers)
            alerts.Add(new UsageAlert("danger", "users", "จำนวนผู้ใช้เต็มแล้ว อัปเกรดแพ็กเกจเพื่อเพิ่มผู้ใช้"));
        else if (!userBar.Unlimited && companyUsers.Count >= maxUsers - 1)
            alerts.Add(new UsageAlert("warning", "users", $"เหลือโควต้าผู้ใช้อีก {maxUsers - companyUsers.Count} คน"));

        // รอบ 232: ไม่หมดอายุ (ฟรีถาวร/ค่าแทน 2126) = ไม่มีคำเตือนหมดอายุ · วันที่เหลือผ่าน SubscriptionExpiry ตัวเดียว (null = ไม่หมดอายุ ไม่ใช่ 36,500 วัน)
        int? daysLeftDisplay = SubscriptionExpiry.IsOpenEnded(sub.IsPermanentFree, endDate, DateTime.UtcNow) ? null : UsageBarRule.DaysLeft(endDate, DateTime.UtcNow);
        if (daysLeftDisplay is int daysLeft)
        {
            if (daysLeft <= 0)
                alerts.Add(new UsageAlert("danger", "subscription", "Subscription หมดอายุแล้ว กรุณาต่ออายุ"));
            else if (daysLeft <= 7)
                alerts.Add(new UsageAlert("warning", "subscription", $"Subscription จะหมดอายุใน {daysLeft} วัน"));
        }

        var ocrBar = bars[1];
        if (ocrBar.Level == 2)
            alerts.Add(new UsageAlert("danger", "ocr", $"โควต้า OCR ใช้ไป {ocrUsed}/{ocrBar.LimitText} หน้า"));
        else if (ocrBar.Level == 1)
            alerts.Add(new UsageAlert("warning", "ocr", $"โควต้า OCR ใช้ไป {ocrBar.Percent:F0}%"));
        var planName = await _db.PlanTemplates.AsNoTracking().Where(t => t.Plan == plan && t.IsActive).Select(t => t.Name).FirstOrDefaultAsync();

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
            Alerts: alerts,
            Bars: bars,
            StatusLabel: PlatformBillingCenterService.StatusLabel(status),
            DaysLeft: daysLeftDisplay,
            PlanName: planName ?? plan.ToString());
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
            // รอบ 231 (231-10 · ฝ่ายค้าน B1): หมดทดลอง = ยังเป็นชนิด Trial (ไม่ใช่ Paid) — ผู้อ่าน: ด่านอ่าน (BlockAccessOnExpiry) · งานปิดข้อมูล (TrialDataRetentionJob)
            sub.Kind = SubscriptionKindRule.AfterTrialExpired(sub.IsPermanentFree);
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

        // รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §A.8 "หมดทดลอง ไม่แจ้งเจ้าของจากจุดนี้"): แจ้งเจ้าของหลัง commit — ล้มเหลวไม่ย้อนสถานะ (log อย่างเดียว · ไม่ block งาน)
        foreach (var sub in expiredTrials)
        {
            try
            {
                var ownerId = await _db.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner)
                    .Select(cu => cu.UserId).FirstOrDefaultAsync();
                if (ownerId == Guid.Empty) continue;
                var blocks = sub.TrialConfig?.BlockAccessOnExpiry == true;
                await NotifyUserAsync(ownerId, sub.CompanyId,
                    Models.Constants.NotificationEvents.SubscriptionExpired, NotificationType.SubscriptionExpired,
                    "ช่วงทดลองใช้สิ้นสุดแล้ว",
                    (blocks ? "ระบบปิดการเข้าถึงข้อมูลจนกว่าจะเลือกแพ็กเกจ" : "ยังเปิดดูข้อมูลได้ แต่สร้าง/แก้ไขไม่ได้หลังพ้นช่วงผ่อนผัน")
                    + " — เลือกแพ็กเกจที่หน้า \"แพ็กเกจและการชำระเงิน\"",
                    "/pages/billing.html#tab=plan", "Subscription", sub.Id);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "แจ้งเจ้าของว่าทดลองใช้หมดอายุไม่สำเร็จ (บริษัท {CompanyId}) — สถานะเปลี่ยนแล้ว", sub.CompanyId);
            }
        }
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
        // รอบ 231 (231-8): ยอดที่ต้องตรง = ราคาพิเศษของบริษัทนี้ (ถ้ามี) ไม่ใช่ราคาแม่แบบ — ตัวตัดสินเดียวกับ handler/เริ่มชำระออนไลน์
        var effectiveSubmit = await PlatformPriceOverrideStore.ResolveForSubscriptionAsync(_db, sub, template, request.RequestedBillingCycle, DateTime.UtcNow, forRenewal: PlatformPlanPricing.IsRenewalPurchase(sub.Plan, template.Plan));   // ฝ่ายค้าน S4
        var due = PlatformPlanPricing.AmountDue(effectiveSubmit.Price, null);
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
    /// <item>ติ๊กยอมรับนโยบายบังคับ — ผู้เรียกตรวจก่อนด้วย part B <c>PurchasePolicyAcceptance</c> (ช่อง platform-invoice) · ไม่ติ๊ก/ฉบับไม่ตรง ⇒ ไม่เกิดรายการ</item>
    /// <item>ใบต่ออายุของรอบนี้ ⇒ อนุมัติแล้วต่ออายุตามเส้นเดิม (เดือน = รอบบิลที่ใบคิด) · ใบอื่น ⇒ <c>RequestedPeriodMonths = 0</c> (ตัดหนี้อย่างเดียว)</item>
    /// </list>
    /// </summary>
    public async Task<SubscriptionPaymentResponse> SubmitInvoicePaymentAsync(Guid companyId, Guid invoiceDocumentId,
        SubmitInvoicePaymentRequest request, string performedBy)
    {
        // ด่านติ๊กยอมรับนโยบาย (part B · PurchasePolicyAcceptance ช่อง platform-invoice) อยู่ที่ผู้เรียก <b>ก่อน</b>เมธอดนี้ (ต้องรู้ผู้ใช้/IP · ลิงก์ชำระไม่มีบัญชี) —
        // ล็อกด้วย required_call_site_check ทั้งศูนย์บิลและลิงก์ชำระ · แถวหลักฐานที่ผู้เรียกเพิ่มไว้ถูกบันทึกพร้อม SaveChanges ของเมธอดนี้
        var (sub, inv) = await PayableInvoiceAsync(companyId, invoiceDocumentId);
        // ฝ่ายค้าน S4 + รอบสุดท้าย 4b: ภาษีหัก ณ ที่จ่ายของผู้เช่านิติบุคคล — ฐาน = ยอดก่อน VAT ของใบตามสัดส่วนยอดค้าง · เงินโอน + ภาษีหัก = ยอดค้างของใบ
        var balance = decimal.Round(inv.BalanceDue, 2, MidpointRounding.AwayFromZero);
        var wht = request.WithholdingTaxAmount is decimal w && w > 0m ? decimal.Round(w, 2, MidpointRounding.AwayFromZero) : 0m;
        if (PlatformInvoiceRules.WithholdingProblem(wht, balance, inv.TotalAmount, inv.SubTotal, wht > 0m && await PayerIsJuristicAsync(companyId)) is string whtProblem)
            throw new BusinessRuleException(whtProblem, "PLATFORM-WHT-INVALID", 400);
        var cash = balance - wht;

        var isRenewal = inv.Id == sub.RenewalInvoiceDocumentId;
        var plan = isRenewal ? (sub.ScheduledPlan ?? sub.Plan) : sub.Plan;
        var cycle = isRenewal && sub.ScheduledPlan != null ? (sub.ScheduledBillingCycle ?? sub.BillingCycle) : sub.BillingCycle;
        var months = isRenewal ? PlatformPlanPricing.MonthsOf(cycle) : 0;

        var pending = await _db.SubscriptionPayments
            .Where(p => p.SubscriptionId == sub.Id && p.PlatformInvoiceDocumentId == inv.Id && p.AttentionReason == null
                && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview))
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        if (pending != null)
        {
            // (สลิปแนบแล้ว/พัก/อนุมัติรอตัดชำระ ⇒ PayableInvoiceAsync ปฏิเสธไปแล้ว) — เหลือรายการที่ยังไม่มีหลักฐานเงิน
            var changes = pending.Amount != cash || pending.WithholdingTaxAmount != wht || pending.RequestedPlan != plan
                || pending.RequestedBillingCycle != cycle || pending.RequestedPeriodMonths != months;
            // ฝ่ายค้านรอบสุดท้าย 2: มีรายการชำระออนไลน์เริ่มแล้วด้วยยอดเดิม ⇒ ห้ามเปลี่ยนยอดใต้ intent (เงินเข้าแล้ว handler เทียบยอดไม่ตรง ⇒ ค้าง)
            if (changes && await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.SourceKind == PaymentSourceKind.SubscriptionPayment
                    && i.SourceId == pending.Id && (i.Status == PaymentIntentStatus.Created || i.Status == PaymentIntentStatus.Pending
                        || i.Status == PaymentIntentStatus.Succeeded)))
                throw new BusinessRuleException(
                    $"รายการ {pending.PaymentNumber} มีการชำระออนไลน์ที่เริ่มไว้ด้วยยอดเดิม — ทำรายการนั้นให้เสร็จ (หรือรอหมดอายุ) ก่อนเปลี่ยนยอด/ภาษีหัก",
                    "PLATFORM-INVOICE-PENDING", 409);
            // ฝ่ายค้าน S3: ใช้รายการเดิมซ้ำ ⇒ ยอด/แพ็กเกจ/รอบ ตามใบ "ตอนนี้"
            pending.Amount = cash;
            pending.WithholdingTaxAmount = wht;
            pending.RequestedPlan = plan; pending.RequestedBillingCycle = cycle; pending.RequestedPeriodMonths = months;
            // ฝ่ายค้านรอบสุดท้าย 2: เติมรายละเอียดการโอนเฉพาะช่องที่ยังว่าง — คำขอภายหลัง (เช่น ผู้จ่ายผ่านลิงก์ที่ไม่มีบัญชี) ไม่เขียนทับของที่ผู้เช่าแจ้งไว้
            var n0 = DateTime.UtcNow;
            if (pending.FromBankName == null && pending.TransferReference == null && request.PaymentDate != null)
                pending.PaymentDate = PlatformPlanPricing.ClampPaymentDate(request.PaymentDate.Value, n0, n0);
            pending.FromBankName ??= Trunc(request.FromBankName, 100);
            pending.TransferReference ??= Trunc(request.TransferReference, 100);
            await _db.SaveChangesAsync();
            return MapPaymentToResponse(pending);
        }

        var now = DateTime.UtcNow;
        var payment = new SubscriptionPayment
        {
            SubscriptionId = sub.Id,
            PaymentNumber = await NextPaymentNumberAsync(sub),
            Amount = cash,
            WithholdingTaxAmount = wht,
            PaymentDate = PlatformPlanPricing.ClampPaymentDate(request.PaymentDate ?? now, now, now),
            PaymentMethod = PaymentMethod.BankTransfer,
            FromBankName = Trunc(request.FromBankName, 100),
            TransferReference = Trunc(request.TransferReference, 100),
            RequestedPlan = plan,
            RequestedBillingCycle = cycle,
            RequestedPeriodMonths = months,
            CustomerNotes = Trunc(request.CustomerNotes, 1000),
            PlatformInvoiceDocumentId = inv.Id,
            Status = SubscriptionPaymentStatus.Pending,
            CreatedBy = performedBy,
        };
        _db.SubscriptionPayments.Add(payment);
        var history = new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            Action = "InvoicePaymentSubmitted",
            Notes = $"แจ้งชำระใบแจ้งหนี้ {inv.DocumentNumber}: {payment.PaymentNumber} {payment.Amount:N2} THB"
                    + (wht > 0m ? $" (หักภาษี ณ ที่จ่าย {wht:N2})" : ""),
            PerformedBy = performedBy
        };
        _db.SubscriptionHistories.Add(history);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
            && pg.ConstraintName == "UX_SubscriptionPayments_OpenPerInvoice")
        {
            // ฝ่ายค้าน S3: สองแท็บ/สองคนกดพร้อมกัน — อีกคำขอสร้างรายการเปิดของใบนี้ก่อน ⇒ คืนรายการนั้น (ไม่เกิดรายการซ้อน · ไม่ 500)
            // แถวหลักฐานการยอมรับนโยบายที่ผู้เรียกเพิ่มไว้ยังอยู่ใน context — บันทึกรอบถัดไป
            _db.Entry(payment).State = EntityState.Detached;
            _db.Entry(history).State = EntityState.Detached;
            var winner = await _db.SubscriptionPayments
                .Where(p => p.PlatformInvoiceDocumentId == inv.Id && p.SubscriptionId == sub.Id && p.AttentionReason == null
                    && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview))
                .FirstAsync();
            if (winner.SlipFileName != null)
                throw new BusinessRuleException(
                    $"ใบแจ้งหนี้ {inv.DocumentNumber} มีรายการแจ้งชำระ {winner.PaymentNumber} รอผู้ดูแลระบบตรวจอยู่แล้ว", "PLATFORM-INVOICE-PENDING", 409);
            await _db.SaveChangesAsync();
            return MapPaymentToResponse(winner);
        }
        return MapPaymentToResponse(payment);
    }

    /// <summary>ฝ่ายค้านรอบสุดท้าย 5: ตรวจล่วงหน้าว่าใบนี้แจ้งชำระได้ไหม (ด่านเดียวกับ <see cref="SubmitInvoicePaymentAsync"/>) — ผู้เรียกตรวจก่อนเก็บไฟล์สลิป
    /// (ไม่ทิ้งไฟล์กำพร้าเมื่อจะถูกปฏิเสธอยู่แล้ว) · ไม่ผ่าน ⇒ โยน BusinessRuleException/KeyNotFoundException แบบเดียวกัน</summary>
    public async Task EnsureInvoicePayableAsync(Guid companyId, Guid invoiceDocumentId) => await PayableInvoiceAsync(companyId, invoiceDocumentId);

    public async Task<SubscriptionPaymentResponse> AttachWhtCertificateAsync(Guid companyId, Guid paymentId, string storedFileName, string performedBy)
    {
        var payment = await _db.SubscriptionPayments.Include(p => p.Subscription)
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.Subscription.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");
        if (payment.WithholdingTaxAmount <= 0m)
            throw new BusinessRuleException("รายการนี้ไม่ได้หักภาษี ณ ที่จ่าย — ไม่ต้องส่งหนังสือรับรอง", "PLATFORM-WHT-CERT-NOT-NEEDED", 409);
        payment.WhtCertificateFileName = storedFileName;
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = payment.SubscriptionId, Action = "WhtCertificateSubmitted",
            Notes = $"ส่งหนังสือรับรอง 50 ทวิ ของรายการ {payment.PaymentNumber} ({payment.WithholdingTaxAmount:N2} บาท)", PerformedBy = performedBy
        });
        if (payment.AttentionRule == PlatformPaymentAttention.RuleWhtCertPending)
            PlatformPaymentAttention.Flag(_db, payment, companyId, "ผู้เช่าส่ง 50 ทวิ แล้ว — ตรวจเอกสารแล้วกด \"จัดการแล้ว\"",
                PlatformPaymentAttention.RuleWhtCertPending, performedBy);
        await _db.SaveChangesAsync();
        return MapPaymentToResponse(payment);
    }

    /// <summary>ด่านร่วมของการแจ้งชำระใบ (ใบของบริษัทนี้ · ยังค้าง · ไม่ใช่ใบต่ออายุของรอบที่ยกเลิก/ถูกแทนที่ · ไม่มีรายการที่จ่ายแล้วรอฝั่งเรา)</summary>
    private async Task<(Subscription Sub, PlatformInvoiceRow Inv)> PayableInvoiceAsync(Guid companyId, Guid invoiceDocumentId)
    {
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db)
            ?? throw new BusinessRuleException(
                "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ — ใบแจ้งหนี้ค่าบริการยังชำระผ่านหน้านี้ไม่ได้ กรุณาชำระที่หน้าแพ็กเกจ", "PLATFORM-TENANT-NOT-SET", 409);
        var inv = await PlatformInvoiceQueries.ForBuyer(_db, platformId, companyId)
            .Where(d => d.Id == invoiceDocumentId).AsRows(companyId).FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("ไม่พบใบแจ้งหนี้นี้ของบริษัท");
        if (!PlatformInvoiceRules.IsOpen(inv.Status, inv.BalanceDue))
            throw new BusinessRuleException($"ใบแจ้งหนี้ {inv.DocumentNumber} ไม่มียอดค้างชำระแล้ว", "PLATFORM-INVOICE-NOT-OPEN", 409);
        if (inv.Id == sub.RenewalInvoiceDocumentId && PlatformInvoiceRules.RenewalNotPayableReason(sub.CancelAtPeriodEnd) is string cancelled)
            throw new BusinessRuleException(cancelled, "PLATFORM-RENEWAL-CANCELLED", 409);
        // ฝ่ายค้าน N6: ใบต่ออายุที่ถูกแทนที่แล้ว (ตัวชี้ใบต่ออายุไม่ใช่ใบนี้) ⇒ ไม่รับชำระเอง (เดิมกลายเป็น "ตัดหนี้อย่างเดียว" เงียบ ๆ = จ่ายซ้ำรอบที่จ่ายแล้ว) — ไปที่แอดมิน
        if (PlatformInvoiceRules.SupersededRenewalReason(inv.Reference, inv.Id, sub.RenewalInvoiceDocumentId) is string superseded)
            throw new BusinessRuleException(superseded, "PLATFORM-RENEWAL-SUPERSEDED", 409);
        // ฝ่ายค้านรอบสุดท้าย 3: มีรายการที่จ่ายแล้วรอฝั่งเรา (สลิปรอตรวจ · พักให้แอดมิน · อนุมัติแล้วรอตัดชำระ) ⇒ ห้ามแจ้ง/จ่ายซ้ำ — ตัวตัดสินเดียว PlatformInvoiceClaims
        var claim = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.SubscriptionId == sub.Id && p.PlatformInvoiceDocumentId == inv.Id)
            .Where(PlatformInvoiceClaims.AwaitingPlatform)
            .Select(p => p.PaymentNumber).FirstOrDefaultAsync();
        if (claim != null)
            throw new BusinessRuleException(
                $"ใบแจ้งหนี้ {inv.DocumentNumber} มีรายการ {claim} ที่ชำระ/แจ้งชำระแล้ว รอผู้ดูแลระบบดำเนินการ — ไม่ต้องชำระซ้ำ", "PLATFORM-INVOICE-PENDING", 409);
        return (sub, inv);
    }

    /// <summary>ฝ่ายค้าน S6: สถานะหลังอนุมัติการชำระแพ็กเกจ — ข้อเท็จจริง (ระงับโดย dunning? · เหตุระงับล่าสุดในประวัติ) แล้วให้ตัวตัดสินเดียวตัดสิน</summary>
    private async Task<SubscriptionStatus> StatusAfterPaymentAsync(Subscription sub)
    {
        if (sub.Status != SubscriptionStatus.Suspended) return PlatformDunningPolicy.StatusAfterPayment(sub.Status, false, _dunning != null, null);
        var byDunning = await _db.PlatformDunningStates.AsNoTracking()
            .AnyAsync(s => s.CompanyId == sub.CompanyId && s.SuspendedByDunningAt != null);
        var lastSuspendAction = await _db.SubscriptionHistories.AsNoTracking()
            // ฝ่ายค้านรอบสุดท้าย: แอดมินระงับซ้ำทับการระงับเพราะหมดอายุ (Suspended→Suspended) ต้องนับเป็นคำสั่งล่าสุด — ข้ามเฉพาะแถวที่การอนุมัติเขียนเอง
            .Where(h => h.SubscriptionId == sub.Id && h.ToStatus == SubscriptionStatus.Suspended && h.Action != "PaymentApprovedSuspensionKept"
                && h.Action != "PaymentApproved")
            .OrderByDescending(h => h.CreatedAt).Select(h => h.Action).FirstOrDefaultAsync();
        return PlatformDunningPolicy.StatusAfterPayment(sub.Status, byDunning, _dunning != null, lastSuspendAction);
    }

    /// <summary>ฝ่ายค้าน S4: บริษัทผู้จ่ายเป็นนิติบุคคล/หน่วยงานรัฐ (หักภาษี ณ ที่จ่ายได้) — จากเลขผู้เสียภาษี (ตัวตัดสินเดียว ContactTypeResolver.FromTaxId)</summary>
    private async Task<bool> PayerIsJuristicAsync(Guid companyId)
    {
        var taxId = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.TaxId).FirstOrDefaultAsync();
        return ContactTypeResolver.FromTaxId(taxId) is ContactType.JuristicPerson or ContactType.GovernmentAgency;
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

    /// <summary>รอบ 219 ส่วน B ฝ่ายค้าน S2: แอดมินอนุมัติสลิป กับ เงินออนไลน์เข้า บนรายการเดียวกันต้องไม่ผ่านพร้อมกัน (ต่ออายุสองรอบ/จ่ายซ้อน) —
    /// ล็อก session ต่อรายการรอบงานทั้งก้อน (ไม่เปิดธุรกรรม — งานข้างในออกเอกสารด้วยธุรกรรมของตัวเอง) · อ่านสถานะใหม่หลังได้ล็อก</summary>
    public Task<SubscriptionPaymentResponse> ReviewPaymentAsync(
        Guid paymentId, ReviewSubscriptionPaymentRequest request, string performedBy,
        bool systemConfirmed = false, DateTime? gatewayPaidAtUtc = null)
    {
        return SessionAdvisoryLock.RunAsync(_db, AdvisoryLockKey.For(SessionAdvisoryLock.PlatformSubscriptionPayment, paymentId.ToString("N")),
            () => ReviewPaymentLockedAsync(paymentId, request, performedBy, systemConfirmed, gatewayPaidAtUtc));
    }

    private async Task<SubscriptionPaymentResponse> ReviewPaymentLockedAsync(
        Guid paymentId, ReviewSubscriptionPaymentRequest request, string performedBy,
        bool systemConfirmed, DateTime? gatewayPaidAtUtc)
    {
        // ฝ่ายค้านรอบ 219 เฟส 2 (S3): นอกจากล็อกต่อรายการของ part B (ข้างบน) — ล็อกต่อ subscription ในธุรกรรม ⇒ สองรายการของใบเดียว/ต่ออายุซ้อน
        // จากรายการคนละตัวไม่แทรกกัน · ขั้นหลัง commit (ตัดชำระ/ใบเสร็จ/ทวงหนี้) ทำนอกธุรกรรม (idempotent)
        var subscriptionId = await _db.SubscriptionPayments.AsNoTracking().Where(p => p.Id == paymentId)
            .Select(p => (Guid?)p.SubscriptionId).FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");
        await using var lockTx = await SubscriptionPaymentLock.AcquireAsync(_db, subscriptionId);
        var payment = await _db.SubscriptionPayments
            .Include(p => p.Subscription)
            .FirstOrDefaultAsync(p => p.Id == paymentId)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");
        if (_db.Database.IsRelational())
        {
            // สถานะล่าสุดหลังได้ล็อก (แถวอาจถูกติดตามไว้ก่อนในคำขอเดียวกัน)
            await _db.Entry(payment).ReloadAsync();
            await _db.Entry(payment.Subscription).ReloadAsync();
        }

        if (payment.Status != SubscriptionPaymentStatus.Pending && payment.Status != SubscriptionPaymentStatus.UnderReview)
            throw new InvalidOperationException("ไม่สามารถตรวจสอบรายการนี้ได้ เนื่องจากสถานะไม่ใช่รอตรวจสอบ");
        // ฝ่ายค้านรอบสุดท้าย (หมายเหตุ): รายการที่พักให้แอดมินแล้ว — ระบบ (webhook/poll ซ้ำ) ไม่อนุมัติเอง และไม่เขียนประวัติ/audit/กระดิ่งซ้ำ · คนตัดสินเท่านั้น
        if (systemConfirmed && payment.AttentionRule == PlatformPaymentAttention.RuleHeld)
        {
            _logger?.LogInformation("รายการ {Payment} พักให้แอดมินอยู่แล้ว — ข้ามการยืนยันอัตโนมัติซ้ำ", payment.PaymentNumber);
            return MapPaymentToResponse(payment);
        }

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

        // ฝ่ายค้าน B2/N6: รายการที่ผูกใบแจ้งหนี้ — ใบต้องยังค้าง และ (ถ้าขอต่ออายุ) ต้องยังเป็นใบต่ออายุปัจจุบัน · ตัวตัดสินเดียว PlatformInvoiceRules.ReviewLinked
        var forceInvoiceOnly = false;
        string? linkedNote = null;
        if (request.Approve && payment.PlatformInvoiceDocumentId is Guid linkedId)
        {
            var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db);
            var inv = platformId == null ? null : await PlatformInvoiceQueries.ByIdAsync(_db, platformId.Value, linkedId);
            // ฝ่ายค้านรอบสุดท้าย 3: มีรายการอื่นของใบเดียวกันที่อนุมัติแล้วแต่ตัดชำระยังไม่สำเร็จ ⇒ ใบ "จ่ายแล้ว รอฝั่งเรา" = ถือว่าปิด (กันต่ออายุซ้อนจากใบเดียว)
            var paidElsewhere = await _db.SubscriptionPayments.AsNoTracking()
                .Where(p => p.Id != payment.Id && p.PlatformInvoiceDocumentId == linkedId)
                .Where(PlatformInvoiceClaims.PaidAwaitingPlatform).AnyAsync();
            var open = inv != null && inv.BuyerCompanyId == payment.Subscription.CompanyId && PlatformInvoiceRules.IsOpen(inv.Status, inv.BalanceDue)
                && !paidElsewhere;
            var superseded = payment.RequestedPeriodMonths > 0 && linkedId != payment.Subscription.RenewalInvoiceDocumentId;
            var (action, message) = PlatformInvoiceRules.ReviewLinked(systemConfirmed, open, superseded, inv?.DocumentNumber);
            switch (action)
            {
                case PlatformInvoiceRules.LinkedReviewAction.Refuse:
                    throw new BusinessRuleException(message!, "PLATFORM-INVOICE-NOT-OPEN", 409);
                case PlatformInvoiceRules.LinkedReviewAction.Hold:
                    // พักให้แอดมิน: ไม่อนุมัติ ไม่ต่ออายุ — เรื่องค้างบนรายการ + audit + กระดิ่ง (ล้มดัง) · รายการพักอยู่นอก unique "ใบละหนึ่งรายการเปิด"
                    payment.Status = SubscriptionPaymentStatus.UnderReview;
                    payment.ReviewNotes = request.ReviewNotes;
                    PlatformPaymentAttention.Flag(_db, payment, payment.Subscription.CompanyId, message!, PlatformPaymentAttention.RuleHeld, performedBy);
                    _db.SubscriptionHistories.Add(new SubscriptionHistory
                    {
                        SubscriptionId = payment.SubscriptionId, Action = "PaymentHeldForAdmin",
                        Notes = $"Payment {payment.PaymentNumber}: {message}", PerformedBy = performedBy
                    });
                    await _db.SaveChangesAsync();
                    if (lockTx != null) await lockTx.CommitAsync();
                    try { await PlatformPaymentAttention.NotifyAdminsAsync(_db, _notificationService, $"พักรายการชำระ {payment.PaymentNumber} ให้ตรวจ", message!, payment.Id); }
                    catch (Exception ex) { _logger?.LogError(ex, "ส่งกระดิ่งแอดมิน (พักรายการ {Payment}) ไม่สำเร็จ — เรื่องค้างอยู่ในหน้าแอดมินแล้ว", payment.Id); }
                    _logger?.LogError("พักรายการชำระค่าบริการ {Payment} ให้แอดมิน: {Message}", payment.PaymentNumber, message);
                    return MapPaymentToResponse(payment);
                case PlatformInvoiceRules.LinkedReviewAction.InvoiceOnly:
                    forceInvoiceOnly = true; linkedNote = message;
                    break;
            }
            // ฝ่ายค้าน S4 + รอบสุดท้าย 4c: แอดมินกรอกเงินที่รับจริง (ตามสลิป) + ภาษีหักจาก 50 ทวิ ตอนอนุมัติ — ไม่ลดยอดเงินเงียบ ๆ ·
            // ตัวตรวจภาษีเดียวกับฝั่งผู้เช่า · เงินที่รับ + ภาษีหัก ≤ ยอดค้าง (น้อยกว่า = จ่ายบางส่วน ตามที่แอดมินยืดหยุ่นได้ — คำตัดสินเจ้าของ ①)
            if ((request.WithholdingTaxAmount != null || request.AmountReceived != null) && inv != null)
            {
                var cash = request.AmountReceived ?? payment.Amount;
                var adminWht = request.WithholdingTaxAmount ?? payment.WithholdingTaxAmount;
                if (PlatformInvoiceRules.AdminLinkedAmountsProblem(cash, adminWht, inv.BalanceDue) is string amountProblem)
                    throw new BusinessRuleException(amountProblem, "PLATFORM-WHT-INVALID", 400);
                if (adminWht > 0m && PlatformInvoiceRules.WithholdingProblem(adminWht, inv.BalanceDue, inv.TotalAmount, inv.SubTotal,
                        await PayerIsJuristicAsync(payment.Subscription.CompanyId)) is string whtProblem)
                    throw new BusinessRuleException(whtProblem, "PLATFORM-WHT-INVALID", 400);
                payment.Amount = decimal.Round(cash, 2, MidpointRounding.AwayFromZero);
                payment.WithholdingTaxAmount = decimal.Round(adminWht, 2, MidpointRounding.AwayFromZero);
            }
        }
        else if (request.Approve && request.WithholdingTaxAmount is decimal plainWht && plainWht != payment.WithholdingTaxAmount)
        {
            // ฝ่ายค้านรอบสุดท้าย 4c: รายการไม่ผูกใบ — ตัวตรวจเดียวกัน (นิติบุคคล · อัตรา ม.40(2) ของฐานก่อน VAT ของยอดรวมก่อนหัก)
            var gross = payment.Amount + plainWht;
            var vatRegistered = await _db.SiteSettings.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => x.PlatformIsVatRegistered).FirstOrDefaultAsync();
            var pre = vatRegistered ? decimal.Round(gross * 100m / 107m, 2, MidpointRounding.AwayFromZero) : gross;
            if (PlatformInvoiceRules.WithholdingProblem(plainWht, gross, gross, pre, plainWht > 0m && await PayerIsJuristicAsync(payment.Subscription.CompanyId)) is string whtProblem)
                throw new BusinessRuleException(whtProblem, "PLATFORM-WHT-INVALID", 400);
            payment.WithholdingTaxAmount = plainWht;
        }

        // รอบ 230 (ฝ่ายค้าน S3): คูปองที่การจองหมดอายุระหว่างค้างชำระ ⇒ นับเพดานใหม่ใต้ล็อกของคูปอง · เกินแล้ว + ระบบยืนยัน ⇒ พักให้แอดมิน (แอดมินอนุมัติ = ตัดสินแล้ว)
        if (request.Approve && systemConfirmed && payment.CouponId != null
            && await PlatformCouponStore.RedeemProblemAsync(_db, payment.Subscription.CompanyId, PlatformCouponRule.SourceSubscriptionPayment, payment.Id) is string couponOver)
            return await HoldForAdminAsync(payment, request, performedBy, lockTx, $"เงินเข้า {payment.PaymentNumber} แล้วแต่ {couponOver}");

        payment.ReviewedByUserId = reviewerUserId;
        payment.ReviewedAt = DateTime.UtcNow;
        payment.ReviewNotes = linkedNote == null ? request.ReviewNotes
            : string.IsNullOrWhiteSpace(request.ReviewNotes) ? linkedNote : $"{request.ReviewNotes} · {linkedNote}";
        // อนุมัติแล้ว = เรื่องที่พักไว้จบที่คนตัดสิน (ปุ่มอนุมัติ/ปฏิเสธของแอดมิน) — ตัดชำระล้มหลังจากนี้จะประทับเรื่องใหม่เอง
        if (!systemConfirmed && payment.AttentionRule == PlatformPaymentAttention.RuleHeld)
            PlatformPaymentAttention.Clear(_db, payment, payment.Subscription.CompanyId, performedBy, request.Approve ? "แอดมินอนุมัติ" : "แอดมินปฏิเสธ");

        // รอบ 219 เฟส 2 (คำตัดสินข้อ 180): การชำระใบแจ้งหนี้ค่าใช้งาน (ผูกใบ · ไม่มีเดือนต่ออายุ) = ตัดหนี้อย่างเดียว ห้ามแตะวันหมดอายุ/แพ็กเกจ
        var invoiceOnly = forceInvoiceOnly || PlatformInvoiceRules.IsInvoiceOnlyPayment(payment.PlatformInvoiceDocumentId, payment.RequestedPeriodMonths);
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
        else if (request.Approve && payment.IsCycleChange)
        {
            // รอบ 231 (คำตัดสินข้อ 231-9 · §B.8): เปลี่ยนรอบบิลกลางรอบ — รอบใหม่ "เริ่มใหม่" วันที่ชำระ · วันหมดอายุใหม่ = วันชำระ + เดือนของรอบใหม่ · สิทธิ์สลับเมื่อเงินเข้า
            var sub = payment.Subscription;
            var now = DateTime.UtcNow;
            var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == payment.RequestedPlan && p.IsActive);
            // ล้าสมัย (ต่ออายุ/แอดมินแก้วันหมดอายุ/แพ็กเกจเปลี่ยนหลังคิดเครดิต) ⇒ พักให้แอดมิน (เครดิตที่หักอาจไม่ตรงแล้ว) · แอดมินอนุมัติ = ตัดสินแล้ว
            var ccStale = template == null ? $"ไม่พบแม่แบบแพ็กเกจ {payment.RequestedPlan} ที่เปิดใช้"
                : PlatformProration.StaleReason(payment.ProrationCycleEndUtc ?? sub.EndDate, sub.EndDate, payment.ProrationFromPlan ?? sub.Plan.ToString(), sub.Plan.ToString(), now);
            var ccRenewalClaim = sub.RenewalInvoiceDocumentId is Guid oldRenewalCc
                ? await RenewalInvoiceClaims.ClaimProblemAsync(_db, sub.Id, oldRenewalCc, payment.Id) : null;
            if ((ccStale != null || ccRenewalClaim != null) && systemConfirmed)
                return await HoldForAdminAsync(payment, request, performedBy, lockTx,
                    $"เงินค่าเปลี่ยนรอบบิล {payment.PaymentNumber} เข้าแล้วแต่ต้องให้คนตัดสิน: {ccStale ?? ccRenewalClaim} — ตรวจแล้วอนุมัติ (เริ่มรอบใหม่) หรือคืนเงิน");
            // ฝ่ายค้าน S3: ใบต่ออายุเดิมถูกจ่าย/แจ้งชำระแล้ว ⇒ ไม่ทับ EndDate เอง — แอดมินต้องจัดการใบนั้นก่อน (ยกเลิก/จับคู่) แล้วค่อยอนุมัติ
            if (ccRenewalClaim != null)
                throw new BusinessRuleException(
                    $"อนุมัติเปลี่ยนรอบบิล {payment.PaymentNumber} ไม่ได้: {ccRenewalClaim} — จัดการใบต่ออายุเดิมก่อน (ยกเลิก/จับคู่การชำระ) ระบบไม่ทับวันหมดอายุเอง",
                    PlatformProration.RenewalClaimRuleCode);
            payment.Status = SubscriptionPaymentStatus.Approved;
            var fromPlan = sub.Plan;
            var fromCycle = sub.BillingCycle;
            payment.PaymentDate = PlatformPlanPricing.ApprovedPaymentDate(systemConfirmed, gatewayPaidAtUtc,
                payment.Kind is SubscriptionPaymentKind.ManualByAdmin or SubscriptionPaymentKind.Waived,
                payment.PaymentDate, payment.CreatedAt, now);
            if (sub.RenewalInvoiceNumber != null || sub.RenewalInvoiceDocumentId != null)
            {
                staleRenewalId = sub.RenewalInvoiceDocumentId;
                _db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = sub.Id, Action = "RenewalInvoiceSuperseded", FromPlan = fromPlan, ToPlan = payment.RequestedPlan,
                    Notes = $"ใบแจ้งหนี้ต่ออายุ {sub.RenewalInvoiceNumber} (รอบเดิม {fromCycle}) ถูกแทนที่ด้วยการเปลี่ยนรอบบิล {payment.PaymentNumber} — "
                          + "ยังไม่มีการชำระ = ระบบยกเลิกใบเดิม · รอบใหม่ออกใบต่ออายุของตัวเอง",
                    PerformedBy = performedBy
                });
                RenewalInvoiceSupersede.Apply(sub);
            }
            var ccStatusAfter = await StatusAfterPaymentAsync(sub);
            var ccMonths = payment.RequestedPeriodMonths > 0 ? payment.RequestedPeriodMonths : PlatformPlanPricing.MonthsOf(payment.RequestedBillingCycle);
            // รอบ 233: ตัวคำนวณวันที่ตัวเดียวกับ preview (PlanPurchasePreview.PaidPeriod) — วันที่ที่หน้าชำระบอก = วันที่ที่ประทับ ·
            // ฝ่ายค้าน B2: วันรับเงินย้อนหลังจนรอบจบก่อนอนุมัติ ⇒ นับจากเวลาอนุมัติ · B1: ApplyTemplate ใช้วันเดียวกัน (ไม่ปิดรอบด้วย UtcNow ทับ)
            var ccPeriod = PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Same, false, false, false,
                isProration: false, isCycleChange: true, sub.EndDate, payment.PaymentDate, ccMonths, approvedAtUtc: now);
            if (template != null) await ApplyTemplateWithPriceAsync(sub, template, payment.RequestedBillingCycle, now, paidPeriodEndUtc: ccPeriod.EndUtc);
            else { sub.Plan = payment.RequestedPlan; sub.BillingCycle = payment.RequestedBillingCycle; }
            sub.EndDate = ccPeriod.EndUtc;
            if (ccPeriod.StartedAtApproval)
                _db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = sub.Id, Action = "PaidPeriodStartedAtApproval",
                    Notes = $"Payment {payment.PaymentNumber}: {PlanPurchasePreview.BackdatedNote} (วันรับเงิน {payment.PaymentDate:yyyy-MM-dd})", PerformedBy = performedBy
                });
            sub.NextBillingDate = sub.EndDate;
            sub.LastPaymentDate = payment.PaymentDate;
            sub.PaymentReference = payment.PaymentNumber;
            sub.ScheduledPlan = null; sub.ScheduledBillingCycle = null; sub.ScheduledPlanEffectiveAt = null;
            sub.CancelAtPeriodEnd = false; sub.CancelRequestedAt = null;
            sub.Status = ccStatusAfter;
            payment.SubscriptionExtendedTo = sub.EndDate;
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id, Action = "CycleChangeApplied", FromPlan = fromPlan, ToPlan = payment.RequestedPlan, ToStatus = sub.Status,
                Notes = $"Payment {payment.PaymentNumber} approved — เปลี่ยนรอบบิล {fromCycle} → {payment.RequestedBillingCycle}"
                        + (fromPlan != payment.RequestedPlan ? $" พร้อมเปลี่ยน {fromPlan} → {payment.RequestedPlan}" : "")
                        + $" · หักมูลค่าคงเหลือ {payment.CycleChangeCredit ?? 0m:N2} ({payment.ProrationDays}/{payment.ProrationCycleDays} วัน) · รอบใหม่ถึง {sub.EndDate:yyyy-MM-dd}"
                        + (ccStale != null ? $" · แอดมินอนุมัติแม้ล้าสมัย: {ccStale}" : ""),
                PerformedBy = performedBy
            });
            var ccOwner = await _db.CompanyUsers.Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId).FirstOrDefaultAsync();
            if (ccOwner != Guid.Empty)
                await NotifyUserAsync(ccOwner, sub.CompanyId,
                    Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded, NotificationType.SubscriptionPaymentApproved,
                    "เปลี่ยนรอบบิลแล้ว",
                    $"ได้รับชำระ {payment.PaymentNumber} ยอด {payment.Amount:N2} บาท — รอบบิลราย{PlatformPlanPricing.CycleLabel(payment.RequestedBillingCycle)} เริ่มแล้ว ใช้ได้ถึง {sub.EndDate.AddHours(7):dd/MM/yyyy}",
                    "/pages/billing.html", "SubscriptionPayment", payment.Id);
        }
        else if (request.Approve && payment.IsProration)
        {
            // รอบ 230 (คำตัดสินข้อ 230-Q1): ส่วนต่างอัปเกรดกลางรอบ — เปลี่ยนแพ็กเกจทันที <b>วันหมดอายุคงเดิม</b> (ไม่ต่ออายุ · ไม่เริ่มรอบใหม่)
            var sub = payment.Subscription;
            var now = DateTime.UtcNow;
            var template = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Plan == payment.RequestedPlan && p.IsActive);
            // ล้าสมัย (ต่ออายุ/แอดมินแก้วันหมดอายุ/แพ็กเกจเปลี่ยนหลังคิดส่วนต่าง) ⇒ ระบบไม่ตัดสินเอง: พักให้แอดมิน (ล้มดัง) · แอดมินกดอนุมัติ = ตัดสินแล้ว (เปลี่ยนแพ็กเกจตามรายการ)
            var prorationStale = template == null ? $"ไม่พบแม่แบบแพ็กเกจ {payment.RequestedPlan} ที่เปิดใช้"
                : PlatformProration.StaleReason(payment.ProrationCycleEndUtc ?? sub.EndDate, sub.EndDate, payment.ProrationFromPlan ?? sub.Plan.ToString(), sub.Plan.ToString(), now);
            // ฝ่ายค้าน S1: ใบต่ออายุของแพ็กเกจเดิมที่มีการชำระ/แจ้งชำระแล้ว ⇒ ห้ามแทนที่อัตโนมัติ (ตัวตัดสินเดียวกับการเปลี่ยนรายการต่ออายุ — RenewalInvoiceClaims)
            var renewalClaim = sub.RenewalInvoiceDocumentId is Guid oldRenewal
                ? await RenewalInvoiceClaims.ClaimProblemAsync(_db, sub.Id, oldRenewal, payment.Id) : null;
            if ((prorationStale != null || renewalClaim != null) && systemConfirmed)
                return await HoldForAdminAsync(payment, request, performedBy, lockTx,
                    $"เงินส่วนต่างอัปเกรด {payment.PaymentNumber} เข้าแล้วแต่ต้องให้คนตัดสิน: {prorationStale ?? renewalClaim} — ตรวจแล้วอนุมัติ (เปลี่ยนแพ็กเกจ) หรือคืนเงิน");
            payment.Status = SubscriptionPaymentStatus.Approved;
            var keepEnd = sub.EndDate;
            var keepNext = sub.NextBillingDate;
            var keepStatus = sub.Status;
            var fromPlan = sub.Plan;
            // ใบแจ้งหนี้ต่ออายุรอบหน้าที่ออกตามแพ็กเกจเดิม ⇒ ล้าสมัย (ราคาแพ็กเกจใหม่) — ล้างตัวชี้ (ตัวเดียว RenewalInvoiceSupersede) ·
            // หลัง commit: ยังไม่มีการชำระ ⇒ ยกเลิกใบผ่านเส้นยกเลิกเอกสาร (HandleStaleRenewalInvoiceAsync) แล้วงานต่ออายุออกใบใหม่ตามแพ็กเกจใหม่
            if (renewalClaim != null)
                // แอดมินอนุมัติเอง ทั้งที่ใบต่ออายุเดิมถูกจ่าย/แจ้งชำระแล้ว ⇒ คงใบเดิม (ไม่แทนที่ · ไม่ยกเลิก) — ผู้ดูแลระบบออกใบลดหนี้/ใบใหม่ด้วยมือ (บอกในประวัติ)
                _db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = sub.Id, Action = "RenewalInvoiceKeptAfterUpgrade", FromPlan = fromPlan, ToPlan = payment.RequestedPlan,
                    Notes = $"อัปเกรดกลางรอบ {payment.PaymentNumber} แต่ไม่แทนที่ใบต่ออายุเดิม: {renewalClaim}", PerformedBy = performedBy
                });
            else if (sub.RenewalInvoiceNumber != null || sub.RenewalInvoiceDocumentId != null)
            {
                staleRenewalId = sub.RenewalInvoiceDocumentId;
                _db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = sub.Id, Action = "RenewalInvoiceSuperseded", FromPlan = fromPlan, ToPlan = payment.RequestedPlan,
                    Notes = $"ใบแจ้งหนี้ต่ออายุ {sub.RenewalInvoiceNumber} (แพ็กเกจ {fromPlan}) ถูกแทนที่ด้วยการอัปเกรดกลางรอบ {payment.PaymentNumber} — "
                          + "ยังไม่มีการชำระ = ระบบยกเลิกใบเดิมและออกใบใหม่ตามแพ็กเกจใหม่ · มีการชำระแล้ว = ผู้ดูแลระบบออกใบลดหนี้",
                    PerformedBy = performedBy
                });
                RenewalInvoiceSupersede.Apply(sub);
            }
            if (template != null) await ApplyTemplateWithPriceAsync(sub, template, sub.BillingCycle, now);
            else sub.Plan = payment.RequestedPlan;
            // รอบ 233: ส่วนต่างอัปเกรด = วันหมดอายุคงเดิม — ผ่านตัวคำนวณวันที่ตัวเดียวกับ preview (PlanPurchasePreview.PaidPeriod isProration)
            sub.EndDate = PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Upgrade, false, false, false,
                isProration: true, isCycleChange: false, keepEnd, now, 0).EndUtc;
            sub.NextBillingDate = keepNext; sub.Status = keepStatus;
            // อัปเกรดแทนการดาวน์เกรด/เปลี่ยนรอบที่รอวันครบรอบ (คำขอล่าสุดชนะ) · ฝ่ายค้าน N5: บอกในประวัติว่าล้างอะไรไป (ไม่เงียบ)
            if (sub.ScheduledPlan != null || sub.CancelAtPeriodEnd)
                _db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = sub.Id, Action = "ScheduledChangeClearedByUpgrade", FromPlan = fromPlan, ToPlan = payment.RequestedPlan,
                    Notes = $"อัปเกรดกลางรอบ {payment.PaymentNumber} ล้าง"
                            + (sub.ScheduledPlan != null ? $" การเปลี่ยนเป็น {sub.ScheduledPlan} ({sub.ScheduledBillingCycle}) ที่รอมีผล {sub.ScheduledPlanEffectiveAt:yyyy-MM-dd}" : "")
                            + (sub.CancelAtPeriodEnd ? " · การยกเลิกเมื่อสิ้นรอบ (จ่ายอัปเกรด = ใช้ต่อ)" : ""),
                    PerformedBy = performedBy
                });
            sub.ScheduledPlan = null; sub.ScheduledBillingCycle = null; sub.ScheduledPlanEffectiveAt = null;
            sub.CancelAtPeriodEnd = false; sub.CancelRequestedAt = null;
            payment.PaymentDate = PlatformPlanPricing.ApprovedPaymentDate(systemConfirmed, gatewayPaidAtUtc,
                payment.Kind is SubscriptionPaymentKind.ManualByAdmin or SubscriptionPaymentKind.Waived,
                payment.PaymentDate, payment.CreatedAt, now);
            sub.LastPaymentDate = payment.PaymentDate;
            sub.PaymentReference = payment.PaymentNumber;
            payment.SubscriptionExtendedTo = sub.EndDate;
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id, Action = "PlanUpgradedProrated", FromPlan = fromPlan, ToPlan = payment.RequestedPlan, ToStatus = sub.Status,
                Notes = $"Payment {payment.PaymentNumber} approved — อัปเกรด {fromPlan} → {payment.RequestedPlan} (ส่วนต่าง {payment.ProrationDays}/{payment.ProrationCycleDays} วัน) · "
                        + $"วันหมดอายุคงเดิม {sub.EndDate:yyyy-MM-dd}" + (prorationStale != null ? $" · แอดมินอนุมัติแม้ล้าสมัย: {prorationStale}" : ""),
                PerformedBy = performedBy
            });
            var ownerUserId = await _db.CompanyUsers.Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId).FirstOrDefaultAsync();
            if (ownerUserId != Guid.Empty)
                await NotifyUserAsync(ownerUserId, sub.CompanyId,
                    Models.Constants.NotificationEvents.SubscriptionPaymentSucceeded, NotificationType.SubscriptionPaymentApproved,
                    "อัปเกรดแพ็กเกจแล้ว",
                    $"ได้รับชำระส่วนต่าง {payment.PaymentNumber} ยอด {payment.Amount:N2} บาท — ใช้แพ็กเกจ {payment.RequestedPlan} ได้ทันที ถึง {sub.EndDate.AddHours(7):dd/MM/yyyy}",
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
            // รอบ 233 (คำตัดสิน 233-1/233-7): วันเริ่ม/วันหมดอายุใหม่จาก<b>ตัวคำนวณวันที่ตัวเดียว</b>กับ preview (PlanPurchasePreview.PaidPeriod) ·
            // วันชำระ = วันรับเงินที่อนุมัติ (ApprovedPaymentDate — ตัวเดียวกับวันที่บนใบเสร็จ) แทน "เวลาที่กดอนุมัติ" · ทดลองใช้ ⇒ เริ่มวันชำระ (ไม่ต่อท้ายวันทดลอง)
            var wasTrial = sub.Status == SubscriptionStatus.Trial;
            var paidAt = PlatformPlanPricing.ApprovedPaymentDate(systemConfirmed, gatewayPaidAtUtc,
                payment.Kind is SubscriptionPaymentKind.ManualByAdmin or SubscriptionPaymentKind.Waived,
                payment.PaymentDate, payment.CreatedAt, now);
            var period = PlanPurchasePreview.PaidPeriod(changeKind, sub.Plan == payment.RequestedPlan, sub.IsPermanentFree, wasTrial,
                isProration: false, isCycleChange: false, sub.EndDate, paidAt, payment.RequestedPeriodMonths, approvedAtUtc: now);
            var (baseDate, newEndDate) = period;
            // ฝ่ายค้าน B2: วันรับเงินย้อนหลังจนรอบที่คิดจบก่อนอนุมัติ ⇒ ลูกค้าได้รอบที่ใช้ได้จริง (นับจากวันอนุมัติ) + บอกในประวัติ (ไม่เงียบ)
            if (period.StartedAtApproval)
                _db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = sub.Id, Action = "PaidPeriodStartedAtApproval",
                    Notes = $"Payment {payment.PaymentNumber}: {PlanPurchasePreview.BackdatedNote} (วันรับเงิน {paidAt:yyyy-MM-dd})", PerformedBy = performedBy
                });
            staleRenewalId = PlatformInvoiceRules.StaleRenewalInvoice(sub.RenewalInvoiceDocumentId, sub.RenewalInvoiceForEndDate, sub.EndDate,
                payment.PlatformInvoiceDocumentId);
            if (staleRenewalId != null)
            {
                // ใบเดิมถูกจัดการหลังบันทึก (ยกเลิกผ่านเส้นยกเลิกเอกสาร / ต้องออกใบลดหนี้) — ล้างตัวชี้ให้รอบใหม่ออกใบต่ออายุของตัวเอง
                sub.RenewalInvoiceNumber = null; sub.RenewalInvoiceIssuedAt = null; sub.RenewalInvoiceForEndDate = null; sub.RenewalInvoiceDocumentId = null;
            }
            // จ่ายแพ็กเกจ = ใช้ต่อ ⇒ ยกเลิกเมื่อสิ้นรอบที่ตั้งไว้ไม่มีผลแล้ว (คำตัดสินเจ้าของข้อ 178 ⑥)
            sub.CancelAtPeriodEnd = false; sub.CancelRequestedAt = null;
            // ฝ่ายค้าน S6: การจ่ายไม่ปลดการระงับของผู้ดูแลระบบ — ระงับเพราะค้างชำระ (dunning) ⇒ คงไว้แล้วให้ dunning คืนสถานะหลังตัดชำระ (ทางคืนอัตโนมัติทางเดียว) ·
            // ระงับเพราะหมดอายุเกินกำหนด ⇒ จ่ายแล้วเปิด (เดิม) · ระงับด้วยเหตุอื่น/ไม่รู้ ⇒ คงไว้ · ตัวตัดสินเดียว PlatformDunningPolicy.StatusAfterPayment
            var statusAfterPayment = await StatusAfterPaymentAsync(sub);
            sub.EndDate = newEndDate;
            sub.NextBillingDate = newEndDate;
            // รอบ 233 (E-2): แถวที่รอแอดมินตรวจวันหมดอายุค่าแทน (ExpiryNeedsReview) — จ่ายแล้วได้รอบจริงจาก PaidPeriod (เริ่มวันชำระ) ⇒ ปลดธง
            // (เดิมต่อจากปี 2126 แล้ว ApplyTemplate ปิดรอบทับ — ใบเสร็จ/แจ้งเตือนบอก "ต่ออายุถึง 2670")
            if (sub.ExpiryNeedsReview && !SubscriptionExpiry.IsOpenEnded(newEndDate, now)) sub.ExpiryNeedsReview = false;
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
                    RenewalInvoiceSupersede.Apply(sub);   // ตัวล้างตัวชี้ใบต่ออายุตัวเดียว (เดิมเขียน 4 บรรทัดเอง)
                }
                // ธง "ฟรีถาวร" ต้องตามแพ็กเกจใหม่เสมอ (ApplyTemplate ตั้งให้) — เดิมเส้นนี้เคยลืมธงนี้ ⇒ จ่ายแล้วยังโชว์ "ฟรีถาวร" และงานตัดหมดอายุไม่ตัด
                await ApplyTemplateWithPriceAsync(sub, template, payment.RequestedBillingCycle, now, paidPeriodEndUtc: newEndDate);   // ฝ่ายค้าน B1
                sub.ScheduledPlan = null; sub.ScheduledBillingCycle = null; sub.ScheduledPlanEffectiveAt = null;
            }
            else
            {
                sub.Plan = payment.RequestedPlan;
                sub.BillingCycle = payment.RequestedBillingCycle;
            }
            // ตั้งหลัง ApplyTemplate (แพ็กเกจฟรีถาวรตั้ง Active เอง) — การระงับที่ต้องคงไว้ต้องชนะ
            if (sub.Status != statusAfterPayment && statusAfterPayment == SubscriptionStatus.Suspended)
                _db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = sub.Id, Action = "PaymentApprovedSuspensionKept", FromStatus = SubscriptionStatus.Suspended, ToStatus = SubscriptionStatus.Suspended,
                    Notes = $"Payment {payment.PaymentNumber}: ต่ออายุแล้วแต่คงสถานะระงับ (ระงับโดยผู้ดูแลระบบ/ค้างชำระ — ปลดโดยผู้ดูแลระบบหรือระบบทวงหนี้เมื่อไม่มีหนี้เกินกำหนด)",
                    PerformedBy = performedBy
                });
            sub.Status = statusAfterPayment;
            if (wasTrial)
            {
                var trial = await _db.TrialConfigs.FirstOrDefaultAsync(t => t.SubscriptionId == sub.Id);
                if (trial != null) trial.TrialStatus = TrialStatus.Converted;
            }
            payment.SubscriptionExtendedTo = newEndDate;
            // ฝ่ายค้านรอบ 219 (หมายเหตุ) + รอบ 2 (ข้อ 5): วันที่บนใบเสร็จ/ใบกำกับ = PaymentDate — ระบบยืนยัน ⇒ เวลาเงินเข้าจริง ·
            // แอดมินกรอก ⇒ คงวันที่กรอก (ตรวจที่ RecordManualPaymentAsync แล้ว) · ลูกค้าแจ้ง ⇒ บีบ (ClampPaymentDate) — ตัวตัดสินเดียว (คิดไว้ก่อนเป็น paidAt)
            payment.PaymentDate = paidAt;
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
                    // รอบ 233 ฝ่ายค้าน S5 (K-3): แพลนบัญชี (ตัวคุมสิทธิ์ของบริษัทที่ผูก — GetEffectivePlanAsync) ใช้ตัวคำนวณวันที่ + วันรับเงินเดียวกับ subscription
                    // (เดิมนับจากเวลาอนุมัติเอง ⇒ แพลนบัญชีกับ subscription คนละวัน)
                    var acctNewEnd = PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Same, true, false, acct.Status == SubscriptionStatus.Trial,
                        isProration: false, isCycleChange: false, acct.EndDate, paidAt, payment.RequestedPeriodMonths, approvedAtUtc: now).EndUtc;
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
                ToStatus = sub.Status,   // ฝ่ายค้านรอบสุดท้าย: สถานะจริงหลังอนุมัติ (การระงับของแอดมินคงอยู่ได้ — S6)
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

        // ฝ่ายค้านรอบสุดท้าย 4a: เงินเข้าออนไลน์โดยผู้เช่าหักภาษี ณ ที่จ่าย ⇒ อนุมัติตามปกติ (เงินเข้าแล้ว) + เรื่องค้าง "รอหนังสือรับรอง 50 ทวิ"
        // จนกว่าผู้เช่าส่งหลักฐาน (ศูนย์บิล/ลิงก์ชำระ) และแอดมินตรวจ — ไม่พักการอนุมัติ (ลูกค้าจ่ายแล้วต้องได้ใช้)
        if (payment.Status == SubscriptionPaymentStatus.Approved
            && PlatformPaymentAttention.WhtCertificateOutstanding(systemConfirmed, payment.WithholdingTaxAmount, payment.WhtCertificateFileName))
            PlatformPaymentAttention.Flag(_db, payment, payment.Subscription.CompanyId,
                $"ผู้เช่าหักภาษี ณ ที่จ่าย {payment.WithholdingTaxAmount:N2} บาท — รอหนังสือรับรอง 50 ทวิ (ผู้เช่าส่งที่ศูนย์บิล/ลิงก์ชำระ)",
                PlatformPaymentAttention.RuleWhtCertPending, performedBy);

        // รอบ 230 (คำตัดสินข้อ 230-Q2): คูปองของรายการนี้ — เงินเข้า (อนุมัติ) ⇒ นับการใช้ · ปฏิเสธ ⇒ ปลดการจอง (ในธุรกรรมที่ล็อกเดียวกัน)
        if (payment.CouponId != null && payment.Status == SubscriptionPaymentStatus.Approved)
            await PlatformCouponStore.RedeemAsync(_db, payment.Subscription.CompanyId, PlatformCouponRule.SourceSubscriptionPayment, payment.Id, performedBy);
        else if (payment.CouponId != null && payment.Status == SubscriptionPaymentStatus.Rejected)
            await PlatformCouponStore.ReleaseAsync(_db, payment.Subscription.CompanyId, PlatformCouponRule.SourceSubscriptionPayment, payment.Id,
                $"รายการถูกปฏิเสธ: {request.RejectionReason}", performedBy);

        // รอบ 229 (QUOTA_ADDON §B.6 ขั้น 5 · ฝ่ายค้าน S3): จ่ายใบต่ออายุ (ต่อแพ็กเกจแล้วในหน่วยความจำ) ⇒ โควตาเสริมทุกบรรทัดในใบได้รอบใหม่
        // ถึงวันหมดอายุใหม่ — <b>บันทึกพร้อมการอนุมัติในธุรกรรมที่ล็อกเดียวกัน</b> (save:false) ⇒ ต่อแพ็กเกจแต่ไม่ต่อโควตาเสริมเป็นไปไม่ได้ ·
        // idempotent (unique RenewedFromId + แถวที่เพิ่มค้างใน context) · ล้ม (อ่านไม่ได้) ⇒ ไม่ทำให้การรับเงินพัง แต่ประทับเรื่องค้างบนรายการชำระ + audit (ล้มดัง)
        if (payment.Status == SubscriptionPaymentStatus.Approved && !invoiceOnly && payment.PlatformInvoiceDocumentId is Guid renewalInvoiceId)
        {
            try
            {
                await Accounting.Services.Payments.QuotaRenewalLines.ActivateForInvoiceAsync(_db, payment.Subscription.CompanyId, renewalInvoiceId,
                    payment.Subscription.EndDate, performedBy, save: false);
            }
            catch (Exception ex)
            {
                foreach (var added in _db.ChangeTracker.Entries<CompanyQuotaAddOn>().Where(x => x.State == EntityState.Added).ToList())
                    added.State = EntityState.Detached;
                _logger?.LogError(ex, "ต่ออายุโควตาเสริมพร้อมแพ็กเกจไม่สำเร็จ (payment {Payment} · ใบ {Invoice})", payment.Id, renewalInvoiceId);
                PlatformPaymentAttention.Flag(_db, payment, payment.Subscription.CompanyId,
                    $"ต่ออายุแพ็กเกจแล้วแต่สร้างรอบใหม่ของโควตาเสริมในใบ {payment.PlatformInvoiceDocumentId} ไม่สำเร็จ — ผู้ดูแลระบบต้องเปิดรอบใหม่ให้ด้วยมือ",
                    QuotaRenewalAttentionRule, performedBy);
            }
        }

        await _db.SaveChangesAsync();
        if (lockTx != null) await lockTx.CommitAsync();   // ปล่อยล็อก — ขั้นถัดไป idempotent (อ้างอิงต่อรายการ) และเรียกบริการที่มีธุรกรรมของตัวเอง

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
            await _billing.HandleStaleRenewalInvoiceAsync(payment.SubscriptionId, stale, $"รายการ {payment.PaymentNumber} เริ่ม/ต่อรอบใหม่");
        // ฝ่ายค้าน S3: ใบต่ออายุที่ถูกแทนที่มีบรรทัดโควตาเสริม ⇒ บรรทัดเหล่านั้นไม่ได้ต่อ (ใบถูกยกเลิก/ต้องลดหนี้) — ปลดล็อกให้เลือกต่อในใบใหม่ได้ + แจ้งลูกค้าและผู้ดูแล (ไม่เงียบ)
        if (payment.Status == SubscriptionPaymentStatus.Approved && staleRenewalId is Guid staleInv)
            await ReleaseStaleQuotaRenewalLinesAsync(payment, staleInv, performedBy);

        // รอบ 219 เฟส 2 ขั้น 2 (G-8): ชำระแล้ว ⇒ ประเมินค้างชำระของบริษัทนี้ใหม่ทันที (ไม่รอรอบงาน) — ระบบระงับไว้เพราะค้างชำระและไม่มีใบเกินผ่อนผันแล้ว ⇒ คืนสถานะ
        if (payment.Status == SubscriptionPaymentStatus.Approved && _dunning != null)
        {
            try { await _dunning.EvaluateCompanyAsync(payment.Subscription.CompanyId, sendReminders: false); }
            catch (Exception ex) { _logger?.LogError(ex, "ประเมินค้างชำระหลังอนุมัติ {Payment} ไม่สำเร็จ — งานทวงหนี้จะประเมินรอบถัดไป", payment.Id); }
        }

        return MapPaymentToResponse(payment);
    }

    /// <summary>รอบ 230: พักรายการที่เงินเข้าแล้วให้แอดมินตัดสิน (สถานะ UnderReview + เรื่องค้าง <c>PLATFORM-PAYMENT-HELD</c> + ประวัติ + กระดิ่ง · ล้มดัง) —
    /// ตัวเดียวของเหตุรอบ 230 (ส่วนต่างล้าสมัย · ใบต่ออายุเดิมถูกจ่ายแล้ว · คูปองเกินเพดาน) · commit ธุรกรรมที่ล็อกไว้</summary>
    private async Task<SubscriptionPaymentResponse> HoldForAdminAsync(SubscriptionPayment payment, ReviewSubscriptionPaymentRequest request, string performedBy,
        IDbContextTransaction? lockTx, string why)
    {
        payment.Status = SubscriptionPaymentStatus.UnderReview;
        payment.ReviewNotes = request.ReviewNotes;
        PlatformPaymentAttention.Flag(_db, payment, payment.Subscription.CompanyId, why, PlatformPaymentAttention.RuleHeld, performedBy);
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = payment.SubscriptionId, Action = "PaymentHeldForAdmin", Notes = $"Payment {payment.PaymentNumber}: {why}", PerformedBy = performedBy
        });
        await _db.SaveChangesAsync();
        if (lockTx != null) await lockTx.CommitAsync();
        try { await PlatformPaymentAttention.NotifyAdminsAsync(_db, _notificationService, $"พักรายการชำระ {payment.PaymentNumber} ให้ตรวจ", why, payment.Id); }
        catch (Exception ex) { _logger?.LogError(ex, "ส่งกระดิ่งแอดมิน (พัก {Payment}) ไม่สำเร็จ — เรื่องค้างอยู่ในหน้าแอดมินแล้ว", payment.Id); }
        _logger?.LogError("พักรายการชำระ {Payment} ให้แอดมิน: {Why}", payment.PaymentNumber, why);
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
        WarnExpiryNeedsReviewOncePerDay(activeSubscriptions.Count(s => s.ExpiryNeedsReview), "ProcessSubscriptionNotificationsAsync");

        foreach (var sub in activeSubscriptions)
        {
            if (sub.ExpiryNeedsReview) continue;   // รอบ 232 (B1 ข): แถวรอแอดมินกำหนดรอบ — ไม่เตือน/ไม่ออกใบต่ออายุ/ไม่เตือนตัดบัญชีจากค่าแทน 2126
            var daysUntilExpiry = (int)(sub.EndDate - now).TotalDays;
            var deactivationDate = sub.EndDate.AddDays(sub.DeactivationDaysAfterExpiry);
            var daysUntilDeactivation = (int)(deactivationDate - now).TotalDays;

            // แจ้งเตือนก่อนหมดอายุ
            // รอบ 219 ส่วน B ฝ่ายค้าน S5: ขอยกเลิกสิ้นรอบไว้ ⇒ ไม่เตือนให้ต่ออายุ/ไม่ออกใบต่ออายุ
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

    // รอบ 232 (B1 ข): เตือนแถวรอตรวจครั้งเดียวต่อวันต่อ process (งานรัน 6 ชม. · จำนวนจริงอยู่ใน JobRun ของ AccountPlanExpiryReminderJob)
    private static DateTime _expiryReviewWarnedOn = DateTime.MinValue;
    private void WarnExpiryNeedsReviewOncePerDay(int count, string where)
    {
        if (count <= 0 || _expiryReviewWarnedOn == DateTime.UtcNow.Date) return;
        _expiryReviewWarnedOn = DateTime.UtcNow.Date;
        _logger?.LogWarning("{Where}: ข้าม {Count} subscription ที่ ExpiryNeedsReview=true (แผนจ่ายเงินไม่มีรอบเรียกเก็บ — แอดมินต้องกด \"กำหนดรอบ\" ในหน้าลูกค้า)", where, count);
    }

    public async Task ProcessExpiredSubscriptionsAsync()
    {
        var now = DateTime.UtcNow;
        var autoSuspend = (await PlatformBillingSettingsService.LoadPolicyAsync(_db)).AutoSuspendEnabled;

        // รอบ 219 เฟส 0 (G-0): ดาวน์เกรด/เปลี่ยนรอบที่ถึงวันมีผล — ใช้สิทธิ์ของแพ็กเกจใหม่ (ApplyTemplate ตัวเดียวกับการอนุมัติชำระ)
        var dueChanges = await _db.Subscriptions
            .Where(s => s.ScheduledPlan != null && s.ScheduledPlanEffectiveAt != null && s.ScheduledPlanEffectiveAt <= now
                && !s.CancelAtPeriodEnd && s.Status != SubscriptionStatus.Cancelled)
            .ToListAsync();
        foreach (var sub in dueChanges)
        {
            var tpl = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Plan == sub.ScheduledPlan && p.IsActive);
            var fromPlan = sub.Plan;
            if (tpl != null) await ApplyTemplateWithPriceAsync(sub, tpl, sub.ScheduledBillingCycle ?? sub.BillingCycle, now, "System");
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

        // รอบ 219 ส่วน B (คำตัดสินข้อ 179): ขอยกเลิกไว้และพ้นสิ้นรอบ ⇒ Cancelled (ไม่ใช่ PastDue — ไม่ทวงเงินรอบที่ผู้ใช้ยกเลิกแล้ว)
        var dueCancels = await _db.Subscriptions
            .Where(s => s.CancelAtPeriodEnd && s.Status == SubscriptionStatus.Active && s.EndDate <= now)
            .ToListAsync();
        // ฝ่ายค้าน S8: ใบต่ออายุของรอบที่ยกเลิก (ออกไปก่อนผู้เช่ากดยกเลิก) ⇒ เมื่อการยกเลิกมีผล ยกเลิกใบผ่านเส้นยกเลิกเอกสาร (เส้นเดียวกับ S9 —
        // มีการชำระ/รายการค้าง ⇒ ไม่ยกเลิก ตั้งงานแอดมิน) + ล้างตัวชี้ (ไม่ทวง · ไม่ค้างเป็นหนี้เท็จ) · ตัวตัดสิน PlatformInvoiceRules.RenewalVoidOnCancel
        var renewalsToVoid = new List<(Guid SubId, Guid InvoiceId)>();
        foreach (var due in dueCancels.Where(x => SubscriptionCancelPolicy.DueForCancellation(x.CancelAtPeriodEnd, x.Status, x.EndDate, now)))
        {
            // ฝ่ายค้านรอบสุดท้าย 7: ล็อกเดียวกับการอนุมัติการชำระ (ต่อ subscription) แล้วอ่านใหม่ใต้ล็อก — ผู้เช่าจ่าย/กดใช้ต่อพร้อมกับงานนี้
            // ⇒ การชำระที่อนุมัติก่อนชนะ (ล้างการยกเลิกแล้ว) ไม่ถูกตั้ง Cancelled ทับ
            await using var lockTx = await SubscriptionPaymentLock.AcquireAsync(_db, due.Id);
            if (_db.Database.IsRelational()) await _db.Entry(due).ReloadAsync();
            var sub = due;
            if (!SubscriptionCancelPolicy.DueForCancellation(sub.CancelAtPeriodEnd, sub.Status, sub.EndDate, now)) continue;
            if (PlatformInvoiceRules.RenewalVoidOnCancel(sub.RenewalInvoiceDocumentId, sub.RenewalInvoiceForEndDate, sub.EndDate) is Guid rid)
            {
                renewalsToVoid.Add((sub.Id, rid));
                sub.RenewalInvoiceNumber = null; sub.RenewalInvoiceIssuedAt = null; sub.RenewalInvoiceForEndDate = null; sub.RenewalInvoiceDocumentId = null;
            }
            sub.Status = SubscriptionStatus.Cancelled;
            sub.CancelledAt = now;
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                Action = "CancelledAtPeriodEnd",
                FromStatus = SubscriptionStatus.Active,
                ToStatus = SubscriptionStatus.Cancelled,
                Notes = $"สิ้นรอบ {sub.EndDate:yyyy-MM-dd} ตามที่ขอยกเลิกไว้",
                PerformedBy = "System"
            });
            await _db.SaveChangesAsync();
            if (lockTx != null) await lockTx.CommitAsync();
        }

        // Mark active subscriptions as expired (ยกเว้นที่ขอยกเลิกไว้ — ข้างบนจัดการแล้ว)
        var newlyExpired = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active && s.EndDate < now && !s.CancelAtPeriodEnd)
            .ToListAsync();

        foreach (var sub in newlyExpired)
        {
            // ตัวที่ track อยู่อาจถูกเปลี่ยนในรอบนี้แล้ว (แพ็กเกจฟรีถาวร = ไม่หมดอายุ) — ตัดสินจากค่าปัจจุบัน ไม่ใช่ค่าที่ DB ใช้กรอง
            if (sub.Status != SubscriptionStatus.Active || sub.EndDate >= now || sub.ExpiryNeedsReview || sub.IsPermanentFree) continue;   // รอบ 232 (B1 ข): แถวรอตรวจข้าม
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
                    Action = PlatformDunningPolicy.ExpirySuspendAction,
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

        // S8: หลังบันทึกสถานะ — เส้นยกเลิกเอกสารของ tenant ผู้ให้บริการมีธุรกรรมของตัวเอง · ผลทุกทางลงประวัติ + audit (HandleStaleRenewalInvoiceAsync)
        if (_billing != null)
            foreach (var (subId, invoiceId) in renewalsToVoid)
                await _billing.HandleStaleRenewalInvoiceAsync(subId, invoiceId, "ยกเลิกเมื่อสิ้นรอบ");
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
            p.PlatformInvoiceDocumentId,
            p.WithholdingTaxAmount,
            p.AttentionReason,
            p.WhtCertificateFileName != null,
            p.IsProration,
            p.IsProration && p.ProrationDays is int pd && p.ProrationCycleDays is int pc
                ? $"ส่วนต่างอัปเกรดจาก {p.ProrationFromPlan} คิด {pd}/{pc} วัน (วันหมดอายุเดิม)" : null,
            p.ListAmount,
            p.CouponCode,
            p.CouponDiscountAmount,
            p.QuotedAmount,
            p.AmountOverrideReason,
            // รอบ 231 (ฝ่ายค้าน S1): เปลี่ยนรอบบิล · ราคาพิเศษ — echo กลับให้หน้าตรวจสลิป/hub
            p.IsCycleChange,
            p.CycleChangeCredit,
            p.IsCycleChange && p.ProrationDays is int cd && p.ProrationCycleDays is int cc
                ? $"เปลี่ยนรอบบิลเป็นราย{PlatformPlanPricing.CycleLabel(p.RequestedBillingCycle)} — หักมูลค่าคงเหลือรอบเดิม {p.CycleChangeCredit ?? 0m:N2} บาท ({cd}/{cc} วัน) · อนุมัติแล้วรอบใหม่ {p.RequestedPeriodMonths} เดือนเริ่มวันที่ชำระ"
                : null,
            p.StandardAmount,
            p.PriceOverrideId);
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
            Accounting.Helpers.NotifyQuotaPolicy.SuggestedTrialLimit,
            // รอบ 231 (P0 จ): ข้อความ/ธง "ไม่จำกัด" ของแพ็กเกจคิดที่เซิร์ฟเวอร์ (UsageBarRule) — js/plan-display.js เลิกถือสำเนาเกณฑ์ 999/99/99999
            UsageBarRule.LimitTexts(t.MaxUsers, t.MaxCompanies, t.MaxDocumentsPerMonth, t.MaxJournalEntriesPerMonth, t.MaxOcrPagesPerMonth, t.MaxStorageBytes, t.MaxSharedMessagesPerMonth),
            UsageBarRule.UnlimitedFlags(t.MaxUsers, t.MaxCompanies, t.MaxDocumentsPerMonth, t.MaxJournalEntriesPerMonth, t.MaxOcrPagesPerMonth, t.MaxStorageBytes, t.MaxSharedMessagesPerMonth));
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

