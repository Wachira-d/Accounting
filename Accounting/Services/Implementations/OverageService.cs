using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ส่วนเกินของ 1 ทรัพยากรในเดือน (หน้า hub/แอดมิน — เซิร์ฟเวอร์คิด หน้าเว็บแสดง)</summary>
public sealed record OverageMonthItem(string Resource, string Label, string Unit, int Units, decimal Amount, decimal? UnitPrice, bool HasRate,
    bool Enabled, string? PriceText,
    /// <summary>คำตัดสิน S8: หน่วยที่ออกเกินโควตา<b>โดยไม่คิดเงิน</b> (ใบที่กฎหมายบังคับในโหมดล็อก · ราคา 0) — แอดมิน/ผู้เช่าต้องเห็น</summary>
    int FreeUnits = 0);

/// <summary>สรุปส่วนเกินเดือนนี้ + นโยบาย (GET quota/overage/month)</summary>
public sealed record OverageMonthSummary(string Period, OverageMode Mode, string ModeLabel, decimal? MonthlyCapAmount, bool Locked, string? LockedNote,
    string? LockedInvoiceNumber, decimal TotalAmount, IReadOnlyList<OverageMonthItem> Items, string NextInvoiceNote, string TermsNote,
    string PolicyVersion, string? AcceptedPolicyVersion, DateTime? AcceptedAtUtc, IReadOnlyList<string> EnabledResources,
    /// <summary>คำตัดสิน S8: เอกสารที่ออกเกินโควตาโดยไม่คิดเงินเดือนนี้ (ความเสี่ยง: ผู้เช่าใช้เกินแพ็กเกจฟรี — ควรอัปเกรด)</summary>
    int FreeDocumentsThisMonth = 0);

/// <summary>คำขอตั้งนโยบายของผู้เช่า (PUT quota/overage-policy)</summary>
public sealed record OveragePolicyUpdate(OverageMode Mode, decimal? MonthlyCapAmount, List<string>? Resources, bool PolicyAccepted, string? PolicyVersionShown);

/// <summary>
/// รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §B · คำตัดสินข้อ 231-1..4) — <b>ด่านส่วนเกินตัวเดียวของทุกทางเข้า</b> (เอกสาร · OCR · ข้อความ · พื้นที่):
/// อ่านนโยบาย + ราคาที่มีผล + ยอดที่คิดไปแล้วเดือนนี้ ⇒ <see cref="OveragePolicy.Decide"/> (pure) · บันทึกมิเตอร์ผ่าน <see cref="IUsageMeteringService"/> เดิม
/// (idempotent · <c>overage.&lt;resource&gt;</c> · ราคา snapshot จากตาราง) — <b>ไม่สร้างเส้นเงินที่สอง</b> · ใบแจ้งหนี้ = <c>UsageInvoicingJob</c> · ทวง/ล็อก = <c>PlatformDunningService</c>
/// </summary>
public interface IOverageService
{
    Task<OveragePolicyView> GetPolicyViewAsync(Guid companyId, CancellationToken ct = default);
    /// <summary>เกินแล้วใช้ต่อได้ไหม (ไม่บันทึกอะไร) · <paramref name="adding"/> = จำนวนหน่วยทรัพยากรของคำขอนี้</summary>
    Task<OverageDecision> DecideAsync(Guid companyId, string resourceKey, int adding = 1, CancellationToken ct = default);
    /// <summary>บันทึกส่วนเกิน 1 เหตุการณ์ (ไม่โยน — งานหลักของผู้เช่าต้องไม่พังเพราะมิเตอร์) · <paramref name="chargeable"/> false = บันทึกเป็นข้อมูลราคา 0
    /// (ใบที่กฎหมายบังคับในโหมดล็อก — §B.4 "ออกได้ · ไม่คิดเงิน")</summary>
    Task<UsageRecordResult> RecordAsync(Guid companyId, string resourceKey, int quantity, string idempotencyKey, string? refEntityType, Guid? refEntityId,
        bool chargeable, decimal? unitPriceSnapshot, CancellationToken ct = default, DateTime? occurredAtUtc = null);
    /// <summary>ค่าส่วนเกินที่คิดไปแล้วในงวดนี้ (บาท · เฉพาะที่คิดเงิน)</summary>
    Task<decimal> SpentThisMonthAsync(Guid companyId, CancellationToken ct = default);
    /// <summary>ค่าส่วนเกินที่คิดไปแล้วของงวดที่ระบุ (ฝ่ายค้าน B2: เพดานเงินของงวดก่อนตรวจกับยอดของงวดนั้น)</summary>
    Task<decimal> SpentInPeriodAsync(Guid companyId, string period, CancellationToken ct = default);
    /// <summary>ราคาที่มีผลกับบริษัทนี้ ณ ตอนนี้ (แถวของแพ็กเกจชนะ) · null = ไม่มีราคา — ผู้เก็บตัวอย่างพื้นที่รายวันใช้ snapshot ราคาลงตัวอย่าง (B2)</summary>
    Task<OverageRateRow?> RateForAsync(Guid companyId, string resourceKey, CancellationToken ct = default);
    Task<OverageMonthSummary> MonthSummaryAsync(Guid companyId, string? period = null, CancellationToken ct = default);
    /// <summary>ผู้เช่าตั้งนโยบาย (Owner/BillingManage) — เปิดต้องยอมรับฉบับปัจจุบัน · ถูกล็อกเพราะค้างชำระ ⇒ เปิดเองไม่ได้ · audit</summary>
    Task<OverageMonthSummary> SetPolicyAsync(Guid companyId, OveragePolicyUpdate req, PlatformBillingActor actor, CancellationToken ct = default);
    /// <summary>แอดมินตั้งแทนผู้เช่า/ปลดล็อก — เหตุผลบังคับ · audit</summary>
    Task<OverageMonthSummary> AdminSetPolicyAsync(Guid companyId, OverageMode mode, decimal? cap, bool unlock, string reason, PlatformBillingActor actor, CancellationToken ct = default);
    /// <summary>ขั้น 1 ของค้างชำระ (คำตัดสินข้อ 231-4): ล็อก/ปลดตามใบ USAGE- ค้าง — คืน action ที่ทำ (None = ไม่เปลี่ยน) · ผู้เรียก (dunning) บันทึก/แจ้งต่อ</summary>
    Task<OveragePolicy.UnpaidLockAction> ApplyUnpaidLockAsync(Guid companyId, IReadOnlyList<DunningInvoice> openUsageInvoices, int graceDays, string? invoiceNumber, CancellationToken ct = default);
    Task<List<OverageRateRow>> RatesAsync(CancellationToken ct = default);
    /// <summary>แอดมินตั้งราคา — ปิดแถวเดิม (EffectiveTo = now) + แถวใหม่ (ไม่แก้ทับ · ประวัติคงอยู่) · ราคา 0/ว่าง = ปิดราคา (เปิดส่วนเกินทรัพยากรนั้นไม่ได้)</summary>
    Task UpsertRateAsync(string resourceKey, Guid? planTemplateId, decimal? unitPrice, int unitSize, string reason, PlatformBillingActor actor, CancellationToken ct = default);
}

public class OverageService : IOverageService
{
    public const string RulePolicy = "PLATFORM-OVERAGE-POLICY", RuleRate = "PLATFORM-OVERAGE-RATE",
        RuleLocked = "PLATFORM-OVERAGE-LOCKED", RuleUnlocked = "PLATFORM-OVERAGE-UNLOCKED";

    private readonly AccountingDbContext _db;
    private readonly IUsageMeteringService? _metering;
    private readonly INotificationEngine? _notify;
    private readonly ILogger<OverageService> _logger;

    public OverageService(AccountingDbContext db, ILogger<OverageService> logger, IUsageMeteringService? metering = null, INotificationEngine? notify = null)
    { _db = db; _logger = logger; _metering = metering; _notify = notify; }

    public static string ModeLabel(OverageMode m) => m == OverageMode.AllowOverage ? "ใช้ต่อได้ คิดค่าส่วนเกินตามจริง" : "หยุดส่วนที่เกิน";

    public async Task<OveragePolicyView> GetPolicyViewAsync(Guid companyId, CancellationToken ct = default)
    {
        var row = await _db.CompanyOveragePolicies.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyId == companyId, ct);
        return row == null ? OveragePolicyView.None
            : new OveragePolicyView(row.Mode, row.MonthlyCapAmount, row.ResourcesCsv, row.LockedByUnpaidAt, row.CapNotifiedPeriod, row.StartedNotifiedPeriod);
    }

    public Task<OverageRateRow?> RateForAsync(Guid companyId, string resourceKey, CancellationToken ct = default)
        => RateForAsync(companyId, resourceKey, DateTime.UtcNow, ct);

    /// <summary>บริษัทที่ถูกปิดสิทธิ์ส่วนเกินเพราะค้างชำระ (ฝ่ายค้าน B1: งานทวงหนี้ต้องประเมินบริษัทเหล่านี้แม้ไม่มีใบค้างแล้ว — ไม่งั้นไม่มีใครปลดล็อก)</summary>
    public static Task<List<Guid>> LockedOverageCompaniesAsync(AccountingDbContext db, CancellationToken ct)
        => db.CompanyOveragePolicies.AsNoTracking().Where(p => p.LockedByUnpaidAt != null).Select(p => p.CompanyId).ToListAsync(ct);

    /// <summary>ราคาที่มีผลกับบริษัทนี้ (แถวของแพ็กเกจชนะ) — ตัวเดียวของด่าน/หน้าสรุป/มิเตอร์</summary>
    private async Task<OverageRateRow?> RateForAsync(Guid companyId, string resourceKey, DateTime nowUtc, CancellationToken ct)
    {
        var rates = await _db.OverageRates.AsNoTracking().Where(r => r.ResourceKey == resourceKey && r.IsActive)
            .Select(r => new OverageRateRow(r.Id, r.ResourceKey, r.PlanTemplateId, r.UnitSize, r.UnitPrice, r.EffectiveFromUtc, r.EffectiveToUtc, r.IsActive))
            .ToListAsync(ct);
        if (rates.Count == 0) return null;
        var planId = await QuotaStore.PlanTemplateIdAsync(_db, companyId, ct);
        return OverageRateResolver.Pick(rates, resourceKey, planId, nowUtc);
    }

    public async Task<OverageDecision> DecideAsync(Guid companyId, string resourceKey, int adding = 1, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var policy = await GetPolicyViewAsync(companyId, ct);
        var rate = await RateForAsync(companyId, resourceKey, now, ct);
        var spent = policy.MonthlyCapAmount is null ? 0m : await SpentThisMonthAsync(companyId, ct);
        var decision = OveragePolicy.Decide(resourceKey, adding, policy, rate, spent, now);
        // คำตัดสินข้อ 231-5 (§B.6 `overage.capped`): ผู้เช่าเปิดส่วนเกินไว้แต่ระบบหยุดให้ (ถึงเพดานเงิน / ราคาถูกปิดทีหลัง) ⇒ แจ้งครั้งเดียวต่อเดือน —
        // ถูกล็อกเพราะค้างชำระแจ้งโดย dunning อยู่แล้ว (overage-locked) · ไม่โยน ไม่บล็อกคำขอหลัก
        var period = AddOnBilling.PeriodOf(now);
        if (!decision.Allowed && policy.Mode == OverageMode.AllowOverage
            && (decision.RuleCode == OverageResources.RuleCap || decision.RuleCode == OverageResources.RuleNoRate)
            && policy.CapNotifiedPeriod != period)   // ฝ่ายค้าน N6: เช็กในหน่วยความจำก่อน — ไม่ยิง UPDATE ทุกคำขอที่ถูกปฏิเสธ
            await NotifyCappedOnceAsync(companyId, period, decision, ct);
        return decision;
    }

    /// <summary>ผู้รับแจ้งเรื่องโควตา/ส่วนเกิน (§B.6): เจ้าของ + ผู้ที่บทบาทกำหนดเองให้สิทธิ์ <c>BillingManage</c> (ไม่ซ้ำ) — ตัวเดียวที่ทุกการแจ้งโควตา/ส่วนเกินใช้</summary>
    public static async Task<List<Guid>> BillingRecipientsAsync(AccountingDbContext db, Guid companyId, CancellationToken ct)
    {
        var owners = await db.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner).Select(cu => cu.UserId).ToListAsync(ct);
        var granted = await db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.CompanyRoleId != null
                && db.CompanyRolePermissions.Any(p => p.CompanyRoleId == cu.CompanyRoleId && p.MenuItemId == PermissionKeys.BillingManage && p.CanAccess))
            .Select(cu => cu.UserId).ToListAsync(ct);
        return owners.Concat(granted).Distinct().ToList();
    }

    private async Task DispatchToBillingRecipientsAsync(Guid companyId, string eventKey, string title, string message, CancellationToken ct)
    {
        if (_notify == null) return;
        foreach (var uid in await BillingRecipientsAsync(_db, companyId, ct))
            await _notify.DispatchAsync(companyId, eventKey, new NotificationContext
            {
                Title = title, Message = message, ActionUrl = QuotaResources.UsageUrl, EntityType = "Subscription",
                BellType = NotificationType.System, RecipientUserId = uid,
            });
    }

    private async Task NotifyOverageStartedAsync(Guid companyId, OverageResources.Resource res, decimal unitPrice, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            var settings = await PlatformBillingSettingsService.LoadPolicyAsync(_db, ct);
            await DispatchToBillingRecipientsAsync(companyId, NotificationEvents.OverageStarted,
                $"เริ่มคิดค่าส่วนเกินโควตา{res.Label}เดือนนี้",
                $"ใช้{res.Label}เกินโควตาแพ็กเกจแล้ว — คิดค่าส่วนเกิน {res.Unit}ละ ฿{unitPrice:N2} ตามนโยบาย \"ใช้ต่อได้ คิดค่าส่วนเกิน\" ที่ตั้งไว้ · "
                    + $"ออกใบแจ้งหนี้ต้นเดือนหน้า ชำระภายใน {settings.OverageGraceDays} วัน · ดูยอดสะสม/เปลี่ยนนโยบายได้ที่แท็บ \"ใช้ไปเท่าไร\"", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งเริ่มคิดส่วนเกิน {Resource} ของบริษัท {Company} ไม่สำเร็จ", res.Key, companyId);
        }
    }

    /// <summary>แจ้ง <c>overage.capped</c> ครั้งเดียวต่อ (บริษัท·เดือน) — ประทับ <c>CapNotifiedPeriod</c> ก่อนส่ง (UPDATE … WHERE ≠ งวด ⇒ หลายเครื่อง/หลายคำขอส่งครั้งเดียว)</summary>
    private async Task NotifyCappedOnceAsync(Guid companyId, string period, OverageDecision decision, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            if (!await StampPeriodOnceAsync(companyId, "CapNotifiedPeriod", period, ct)) return;
            var res = OverageResources.Find(decision.ResourceKey);
            var title = decision.RuleCode == OverageResources.RuleCap
                ? "หยุดส่วนเกินโควตาแล้ว — ถึงเพดานค่าส่วนเกินต่อเดือนที่ตั้งไว้"
                : $"หยุดส่วนเกินโควตา{res?.Label} — ยังไม่มีราคาส่วนเกินของรายการนี้";
            await DispatchToBillingRecipientsAsync(companyId, NotificationEvents.OverageCapped, title,
                (decision.Reason ?? "") + " · ใบกำกับภาษี/ใบเสร็จ/ใบลดหนี้ยังออกได้เสมอโดยไม่คิดเงิน", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้ง overage.capped ของบริษัท {Company} ไม่สำเร็จ", companyId);
        }
    }

    public Task<decimal> SpentThisMonthAsync(Guid companyId, CancellationToken ct = default)
        => SpentInPeriodAsync(companyId, AddOnBilling.PeriodOf(DateTime.UtcNow), ct);

    public async Task<decimal> SpentInPeriodAsync(Guid companyId, string period, CancellationToken ct = default)
    {
        var rows = await _db.UsageEvents.AsNoTracking()
            .Where(u => u.CompanyId == companyId && !u.IsDeleted && !u.IsSandbox && u.FeatureCode.StartsWith(OverageResources.MeterPrefix))
            .Where(u => u.BilledPeriod == null || u.BilledPeriod == period)
            .Select(u => new { u.OccurredAt, u.ChargedAmount }).ToListAsync(ct);
        return rows.Where(r => AddOnBilling.PeriodOf(r.OccurredAt) == period).Sum(r => r.ChargedAmount);
    }

    public async Task<UsageRecordResult> RecordAsync(Guid companyId, string resourceKey, int quantity, string idempotencyKey, string? refEntityType, Guid? refEntityId,
        bool chargeable, decimal? unitPriceSnapshot, CancellationToken ct = default, DateTime? occurredAtUtc = null)
    {
        if (_metering == null) return new UsageRecordResult(false, null, 0m, false, false, "ไม่มีบริการมิเตอร์");
        var res = OverageResources.Find(resourceKey);
        if (res == null) return new UsageRecordResult(false, null, 0m, false, false, "ทรัพยากรนี้ไม่คิดส่วนเกิน");
        try
        {
            decimal price = 0m;
            if (chargeable)
                price = unitPriceSnapshot ?? (await RateForAsync(companyId, resourceKey, DateTime.UtcNow, ct))?.UnitPrice ?? 0m;
            var result = await _metering.RecordAsync(new UsageRecordRequest(companyId, OverageResources.FeatureCode(resourceKey), Math.Max(1, quantity),
                IdempotencyKey: idempotencyKey, RefEntityType: refEntityType, RefEntityId: refEntityId,
                UnitLabel: chargeable ? res.Unit : $"{res.Unit} (ไม่คิดเงิน)", UnitPriceOverride: price, OccurredAtOverride: occurredAtUtc), ct);
            if (result.Recorded && !result.Duplicate)
            {
                var period = AddOnBilling.PeriodOf(occurredAtUtc ?? DateTime.UtcNow);
                // คำตัดสินข้อ 231-5: เริ่มคิดส่วนเกินครั้งแรกของเดือน ⇒ แจ้งผู้รับ — ฝ่ายค้าน N2: ประทับ StartedNotifiedPeriod (ไม่เดาจากยอดก่อนหน้า)
                if (chargeable && result.ChargedAmount > 0m && await StampPeriodOnceAsync(companyId, "StartedNotifiedPeriod", period, ct))
                    await NotifyOverageStartedAsync(companyId, res, price, ct);
                // คำตัดสิน S8: ใบที่กฎหมายบังคับออกเกินโควตาโดยไม่คิดเงิน ⇒ แจ้งผู้เช่าให้อัปเกรด ครั้งเดียวต่อเดือน (นับ/แสดงที่ MonthSummary.FreeDocumentsThisMonth)
                if (!chargeable && resourceKey == OverageResources.Documents && await StampPeriodOnceAsync(companyId, "FreeOverageNotifiedPeriod", period, ct))
                    await NotifyFreeOverageAsync(companyId, ct);
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "บันทึกส่วนเกิน {Resource} ของบริษัท {Company} ไม่สำเร็จ — งานหลักดำเนินต่อ", resourceKey, companyId);
            return new UsageRecordResult(false, null, 0m, false, false, "บันทึกส่วนเกินไม่สำเร็จ");
        }
    }

    public async Task<OverageMonthSummary> MonthSummaryAsync(Guid companyId, string? period = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        period ??= AddOnBilling.PeriodOf(now);
        var row = await _db.CompanyOveragePolicies.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyId == companyId, ct);
        var policy = row == null ? OveragePolicyView.None : new OveragePolicyView(row.Mode, row.MonthlyCapAmount, row.ResourcesCsv, row.LockedByUnpaidAt);
        var settings = await PlatformBillingSettingsService.LoadPolicyAsync(_db, ct);
        var events = await _db.UsageEvents.AsNoTracking()
            .Where(u => u.CompanyId == companyId && !u.IsDeleted && !u.IsSandbox
                && (u.FeatureCode.StartsWith(OverageResources.MeterPrefix) || u.FeatureCode == AddOnCodes.DocumentsOverage))
            .Select(u => new { u.FeatureCode, u.OccurredAt, u.Quantity, u.ChargedAmount }).ToListAsync(ct);
        var inPeriod = events.Where(e => AddOnBilling.PeriodOf(e.OccurredAt) == period).ToList();
        var enabled = OveragePolicy.Resources(policy.ResourcesCsv);
        var items = new List<OverageMonthItem>();
        foreach (var res in OverageResources.All)
        {
            var rate = await RateForAsync(companyId, res.Key, now, ct);
            var mine = inPeriod.Where(e => OverageResources.FromFeatureCode(e.FeatureCode)?.Key == res.Key).ToList();
            items.Add(new OverageMonthItem(res.Key, res.Label, res.Unit, mine.Where(e => e.ChargedAmount > 0m).Sum(e => e.Quantity), mine.Sum(e => e.ChargedAmount),
                rate?.UnitPrice, rate != null && rate.UnitPrice > 0m,
                policy.Mode == OverageMode.AllowOverage && !policy.IsLocked && (enabled.Count == 0 || enabled.Contains(res.Key)),
                rate == null || rate.UnitPrice <= 0m ? null : OveragePolicy.PriceText(res, rate),
                FreeUnits: mine.Where(e => e.ChargedAmount <= 0m).Sum(e => e.Quantity)));
        }
        // เลขใบที่ทำให้ถูกล็อกถูกประทับไว้ใน LockedReason ตอนล็อก (ApplyUnpaidLockAsync) — ไม่อ่านเอกสารของ tenant ผู้ให้บริการจากเส้นผู้เช่า (กฎ M)
        var lockedInvoice = row?.LockedByUnpaidAt != null ? row.LockedReason : null;
        var th = now.AddHours(7);
        var nextInvoice = new DateTime(th.Year, th.Month, 1).AddMonths(1);
        var total = items.Sum(i => i.Amount);
        return new OverageMonthSummary(period, policy.Mode, ModeLabel(policy.Mode), policy.MonthlyCapAmount, policy.IsLocked,
            policy.IsLocked ? $"ปิดสิทธิ์ส่วนเกินชั่วคราวตั้งแต่ {PlatformInvoiceRules.ThaiDate(policy.LockedByUnpaidAt!.Value):dd/MM/yyyy} — {row!.LockedReason ?? "ใบแจ้งหนี้ค่าส่วนเกินค้างชำระ"} · ชำระแล้วระบบเปิดคืนอัตโนมัติ" : null,
            lockedInvoice, total, items,
            total >= settings.OverageMinInvoiceAmount
                ? $"ประมาณการบิลค่าส่วนเกินวันที่ {nextInvoice:dd/MM/yyyy} ชำระภายใน {settings.OverageGraceDays} วัน"
                : $"ต่ำกว่าขั้นต่ำ {settings.OverageMinInvoiceAmount:N2} บาท — ยกยอดรวมกับเดือนถัดไป (ออกใบวันที่ {nextInvoice:dd/MM/yyyy} เมื่อถึงขั้นต่ำ)",
            $"ราคารวม VAT · ออกใบแจ้งหนี้ทุกต้นเดือน ชำระภายใน {settings.OverageGraceDays} วัน — เกินกำหนดชำระ ระบบจะปิดสิทธิ์ส่วนเกินชั่วคราวทันที (ของที่ใช้ไปแล้วไม่ถูกถอน)"
                + (settings.OverageUnpaidAction == OverageUnpaidAction.LockThenDunning ? $" และเข้าเกณฑ์ค้างชำระปกติ (ระงับเมื่อเกิน {settings.SuspendGraceDays} วัน)" : "")
                // ฝ่ายค้าน S3: ผู้เช่าต้องเห็นว่าใบส่วนเกินจะถูกตัดบัตรอัตโนมัติ (เมื่อแอดมินเปิด + ผู้เช่ามีบัตรที่บันทึกไว้และเปิดตัดอัตโนมัติ)
                + (settings.OverageAutoChargeSavedCard ? " · ถ้าเปิดตัดบัตรอัตโนมัติไว้ ระบบจะตัดบัตรที่บันทึกไว้เมื่อใบส่วนเกินถึงกำหนด" : ""),
            OveragePolicy.PolicyVersion, row?.AcceptedPolicyVersion, row?.AcceptedAtUtc, enabled.ToList(),
            FreeDocumentsThisMonth: items.FirstOrDefault(i => i.Resource == OverageResources.Documents)?.FreeUnits ?? 0);
    }

    public async Task<OverageMonthSummary> SetPolicyAsync(Guid companyId, OveragePolicyUpdate req, PlatformBillingActor actor, CancellationToken ct = default)
    {
        var err = OveragePolicy.ValidateTenantUpdate(req.Mode, req.MonthlyCapAmount, req.Resources, req.PolicyAccepted, req.PolicyVersionShown);
        if (err != null) throw new BusinessRuleException(err, "OVERAGE-POLICY-INVALID");
        var row = await _db.CompanyOveragePolicies.FirstOrDefaultAsync(p => p.CompanyId == companyId, ct);
        // ฝ่ายค้าน N3: ระหว่างถูกล็อก แก้เพดาน/ทรัพยากรได้ถ้าไม่เปลี่ยนโหมด — ห้ามเฉพาะ "เปิดใหม่" (HardLock → AllowOverage)
        if (row?.LockedByUnpaidAt != null && req.Mode == OverageMode.AllowOverage && row.Mode != OverageMode.AllowOverage)
            throw new BusinessRuleException("ปิดสิทธิ์ส่วนเกินชั่วคราวเพราะใบแจ้งหนี้ค่าส่วนเกินค้างชำระ — ชำระให้ครบแล้วระบบจะเปิดคืนอัตโนมัติ (เปิดเองไม่ได้ระหว่างนี้)", OverageResources.RuleLocked);
        if (req.Mode == OverageMode.AllowOverage)
        {
            // เปิดได้เฉพาะเมื่อมีราคาอย่างน้อย 1 ทรัพยากรที่เลือก — "ไม่มีราคา = เปิดไม่ได้" (§B.1 ข้อ 3)
            var chosen = req.Resources is { Count: > 0 } ? req.Resources.Distinct().ToList() : OverageResources.All.Select(r => r.Key).ToList();
            var any = false;
            foreach (var r in chosen) if ((await RateForAsync(companyId, r, DateTime.UtcNow, ct))?.UnitPrice > 0m) { any = true; break; }
            if (!any) throw new BusinessRuleException("ยังไม่มีราคาส่วนเกินของรายการที่เลือก — ระบบไม่คิดเงินโดยไม่มีราคา · ติดต่อผู้ดูแลระบบ NextAcc", OverageResources.RuleNoRate);
        }
        var before = row == null ? null : Snapshot(row);
        if (row == null) { row = new CompanyOveragePolicy { CompanyId = companyId, CreatedBy = actor.Email ?? actor.UserId?.ToString() }; _db.CompanyOveragePolicies.Add(row); }
        row.Mode = req.Mode;
        // ฝ่ายค้าน N2: เพิ่มเพดาน (หรือยกเลิกเพดาน) ⇒ ยอมให้แจ้ง capped ซ้ำได้ในเดือนนี้ถ้าถึงเพดานใหม่อีก
        if (CapRaised(row.MonthlyCapAmount, req.MonthlyCapAmount)) row.CapNotifiedPeriod = null;
        row.MonthlyCapAmount = req.MonthlyCapAmount;
        row.ResourcesCsv = req.Resources is { Count: > 0 } ? string.Join(",", req.Resources.Distinct()) : null;
        if (req.Mode == OverageMode.AllowOverage)
        {
            row.AcceptedPolicyVersion = req.PolicyVersionShown; row.AcceptedBy = actor.Email ?? actor.UserId?.ToString(); row.AcceptedAtUtc = DateTime.UtcNow;
        }
        row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor.Email ?? actor.UserId?.ToString();
        Audit(companyId, actor, RulePolicy, before, Snapshot(row), "ผู้เช่าตั้งนโยบายส่วนเกิน");
        await _db.SaveChangesAsync(ct);
        return await MonthSummaryAsync(companyId, null, ct);
    }

    public async Task<OverageMonthSummary> AdminSetPolicyAsync(Guid companyId, OverageMode mode, decimal? cap, bool unlock, string reason, PlatformBillingActor actor, CancellationToken ct = default)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length < 3) throw new BusinessRuleException("ต้องระบุเหตุผล (บันทึกใน audit)");
        if (cap is decimal c && c < 0m) throw new BusinessRuleException("เพดานต้องเป็น 0 ขึ้นไป หรือเว้นว่าง");
        if (!await _db.Companies.AnyAsync(x => x.Id == companyId, ct)) throw new KeyNotFoundException("ไม่พบบริษัท");
        var row = await _db.CompanyOveragePolicies.FirstOrDefaultAsync(p => p.CompanyId == companyId, ct);
        var before = row == null ? null : Snapshot(row);
        if (row == null) { row = new CompanyOveragePolicy { CompanyId = companyId, CreatedBy = "admin" }; _db.CompanyOveragePolicies.Add(row); }
        if (CapRaised(row.MonthlyCapAmount, cap)) row.CapNotifiedPeriod = null;
        row.Mode = mode; row.MonthlyCapAmount = cap;
        if (unlock) { row.LockedByUnpaidAt = null; row.LockedReason = null; }
        if (mode == OverageMode.AllowOverage && row.AcceptedPolicyVersion == null)
        { row.AcceptedPolicyVersion = OveragePolicy.PolicyVersion; row.AcceptedBy = "admin:" + (actor.Email ?? ""); row.AcceptedAtUtc = DateTime.UtcNow; }
        row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor.Email ?? "admin";
        Audit(companyId, actor, unlock ? RuleUnlocked : RulePolicy, before, Snapshot(row), $"ผู้ดูแลระบบ: {reason}");
        await _db.SaveChangesAsync(ct);
        return await MonthSummaryAsync(companyId, null, ct);
    }

    public async Task<OveragePolicy.UnpaidLockAction> ApplyUnpaidLockAsync(Guid companyId, IReadOnlyList<DunningInvoice> openUsageInvoices, int graceDays, string? invoiceNumber, CancellationToken ct = default)
    {
        var row = await _db.CompanyOveragePolicies.FirstOrDefaultAsync(p => p.CompanyId == companyId, ct);
        _ = graceDays;   // ฝ่ายค้าน N1: ล็อกเมื่อเกินวันครบกำหนด (ครบกำหนด = วันออก + grace อยู่แล้ว) — ไม่บวกผ่อนผันซ้ำ
        var action = OveragePolicy.UnpaidLock(openUsageInvoices, DateTime.UtcNow, row?.LockedByUnpaidAt != null);
        if (action == OveragePolicy.UnpaidLockAction.None) return action;
        if (row == null)
        {
            // ไม่มีแถว = HardLock อยู่แล้ว — ล็อกไว้เพื่อให้เปิดเองไม่ได้จนกว่าจะชำระ (คำตัดสินข้อ 231-4)
            row = new CompanyOveragePolicy { CompanyId = companyId, CreatedBy = "system:dunning" };
            _db.CompanyOveragePolicies.Add(row);
        }
        var before = Snapshot(row);
        if (action == OveragePolicy.UnpaidLockAction.Lock)
        {
            row.LockedByUnpaidAt = DateTime.UtcNow;
            row.LockedReason = $"ใบแจ้งหนี้ {invoiceNumber ?? "ค่าส่วนเกิน"} เกินกำหนดชำระ";
        }
        else { row.LockedByUnpaidAt = null; row.LockedReason = null; }
        row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = "system:dunning";
        Audit(companyId, null, action == OveragePolicy.UnpaidLockAction.Lock ? RuleLocked : RuleUnlocked, before, Snapshot(row),
            action == OveragePolicy.UnpaidLockAction.Lock ? row.LockedReason! : "ชำระใบค่าส่วนเกินครบแล้ว — ปลดล็อก");
        await _db.SaveChangesAsync(ct);
        return action;
    }

    public async Task<List<OverageRateRow>> RatesAsync(CancellationToken ct = default)
        => await _db.OverageRates.AsNoTracking().OrderBy(r => r.ResourceKey).ThenBy(r => r.PlanTemplateId).ThenByDescending(r => r.EffectiveFromUtc)
            .Select(r => new OverageRateRow(r.Id, r.ResourceKey, r.PlanTemplateId, r.UnitSize, r.UnitPrice, r.EffectiveFromUtc, r.EffectiveToUtc, r.IsActive))
            .ToListAsync(ct);

    public async Task UpsertRateAsync(string resourceKey, Guid? planTemplateId, decimal? unitPrice, int unitSize, string reason, PlatformBillingActor actor, CancellationToken ct = default)
    {
        if (!OverageResources.IsKnown(resourceKey)) throw new BusinessRuleException("ทรัพยากรต้องเลือกจากรายการของระบบ");
        if (OveragePolicy.ValidateRate(resourceKey, unitSize) is string sizeErr) throw new BusinessRuleException(sizeErr);   // ฝ่ายค้าน S1: realtime ⇒ UnitSize = 1
        reason = (reason ?? "").Trim();
        if (reason.Length < 3) throw new BusinessRuleException("ต้องระบุเหตุผล (บันทึกใน audit)");
        if (unitPrice is decimal p && (p < 0m || p > 1_000_000m)) throw new BusinessRuleException("ราคาต่อหน่วยต้องเป็น 0 ขึ้นไป (0/ว่าง = ปิดราคา)");
        if (unitSize < 1 || unitSize > 1_000_000) throw new BusinessRuleException("ขนาดหน่วยต้องเป็น 1 ขึ้นไป");
        if (planTemplateId is Guid tid && !await _db.PlanTemplates.AnyAsync(t => t.Id == tid, ct)) throw new KeyNotFoundException("ไม่พบแม่แบบแพ็กเกจ");
        var now = DateTime.UtcNow;
        var open = await _db.OverageRates.Where(r => r.ResourceKey == resourceKey && r.PlanTemplateId == planTemplateId && r.IsActive && r.EffectiveToUtc == null).ToListAsync(ct);
        var old = open.Select(r => new { r.UnitPrice, r.UnitSize, r.EffectiveFromUtc }).ToList();
        foreach (var r in open) { r.EffectiveToUtc = now; r.UpdatedAt = now; r.UpdatedBy = actor.Email; }
        if (unitPrice is decimal price && price > 0m)
            _db.OverageRates.Add(new OverageRate
            {
                ResourceKey = resourceKey, PlanTemplateId = planTemplateId, UnitSize = unitSize, UnitPrice = Math.Round(price, 2, MidpointRounding.AwayFromZero),
                EffectiveFromUtc = now, IsActive = true, SetBy = actor.Email, Reason = reason.Length > 500 ? reason[..500] : reason, CreatedBy = actor.Email,
            });
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = null, UserId = actor.UserId, UserEmail = actor.Email ?? "admin", Action = AuditAction.Update, EntityType = nameof(OverageRate),
            EntityId = $"{resourceKey}:{planTemplateId?.ToString() ?? "all"}",
            OldValues = JsonSerializer.Serialize(old), NewValues = JsonSerializer.Serialize(new { unitPrice, unitSize, reason, ruleCode = RuleRate }),
            IpAddress = actor.Ip, Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>เพดานใหม่สูงกว่าเดิม (หรือยกเลิกเพดาน) — ใช้รีเซ็ตตัวกันแจ้งซ้ำ capped (N2)</summary>
    private static bool CapRaised(decimal? oldCap, decimal? newCap) => oldCap is decimal o && (newCap is null || newCap > o);

    /// <summary>ประทับงวดลงคอลัมน์กันแจ้งซ้ำ (ครั้งเดียวต่อบริษัท·เดือน) — ไม่มีแถวนโยบาย ⇒ สร้างแถว HardLock ก่อน (พฤติกรรมเดิม) · true = คำขอนี้เป็นผู้ประทับ</summary>
    private async Task<bool> StampPeriodOnceAsync(Guid companyId, string column, string period, CancellationToken ct)
    {
        if (column is not ("StartedNotifiedPeriod" or "FreeOverageNotifiedPeriod" or "CapNotifiedPeriod")) throw new ArgumentOutOfRangeException(nameof(column));
        if (!await _db.CompanyOveragePolicies.AnyAsync(p => p.CompanyId == companyId, ct))
        {
            await _db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "CompanyOveragePolicies" ("Id","CompanyId","Mode","CreatedAt","CreatedBy","IsDeleted")
                VALUES (gen_random_uuid(), {0}, 1, now(), 'system:overage', false) ON CONFLICT DO NOTHING;
                """, new object[] { companyId }, ct);
        }
        var sql = $$"""UPDATE "CompanyOveragePolicies" SET "{{column}}" = {1}, "UpdatedAt" = now() WHERE "CompanyId" = {0} AND "IsDeleted" = false AND ("{{column}}" IS NULL OR "{{column}}" <> {1});""";
        return await _db.Database.ExecuteSqlRawAsync(sql, new object[] { companyId, period }, ct) == 1;
    }

    /// <summary>คำตัดสิน S8: ใบที่กฎหมายบังคับออกเกินโควตาโดยไม่คิดเงิน — แจ้งผู้รับให้อัปเกรด/ซื้อเพิ่ม (ไม่โยน)</summary>
    private async Task NotifyFreeOverageAsync(Guid companyId, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            await DispatchToBillingRecipientsAsync(companyId, NotificationEvents.DocumentQuotaLevel,
                "ออกเอกสารเกินโควตาแพ็กเกจแล้ว (ใบที่กฎหมายบังคับยังออกได้ ไม่คิดเงิน)",
                "โควตาเอกสารเดือนนี้เต็มแล้ว — ใบกำกับภาษี/ใบเสร็จ/ใบลดหนี้ยังออกได้โดยไม่คิดเงิน แต่ใบเสนอราคา/ใบสั่งซื้อจะถูกหยุด · "
                + "อัปเกรดแพ็กเกจ ซื้อโควตาเพิ่ม หรือเปิด \"ใช้ต่อได้ คิดค่าส่วนเกิน\" ได้ที่แท็บ \"ใช้ไปเท่าไร\"", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งออกเอกสารเกินโควตาไม่คิดเงินของบริษัท {Company} ไม่สำเร็จ", companyId);
        }
    }

    private static object Snapshot(CompanyOveragePolicy r) => new
    {
        mode = r.Mode.ToString(), r.MonthlyCapAmount, r.ResourcesCsv, r.AcceptedPolicyVersion, r.AcceptedAtUtc, r.LockedByUnpaidAt, r.LockedReason,
    };

    private void Audit(Guid companyId, PlatformBillingActor? actor, string rule, object? before, object after, string reason)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = actor?.UserId, UserEmail = actor?.Email ?? "system:overage", Action = AuditAction.Update,
            EntityType = nameof(CompanyOveragePolicy), EntityId = companyId.ToString(),
            OldValues = before == null ? null : JsonSerializer.Serialize(before),
            NewValues = JsonSerializer.Serialize(new { policy = after, reason, ruleCode = rule }),
            IpAddress = actor?.Ip, Timestamp = DateTime.UtcNow,
        });
}
