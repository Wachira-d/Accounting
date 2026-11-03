using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>1 แถวของหน้าสรุปโควตา — เซิร์ฟเวอร์คำนวณทุกช่อง หน้าเว็บแสดงอย่างเดียว (D-4: เดิม 3 หน้าใช้เกณฑ์ "ไม่จำกัด" 3 แบบ)</summary>
public sealed record QuotaSummaryItem(string Resource, string Label, string Unit, string Kind, int? Used, int? Limit, bool Unlimited,
    int? Included, int Purchased, int WarnLevel, string UsedText, string LimitText, bool PerSite, string? Note, bool CanBuy, string? BuyUrl,
    string Source)
{
    /// <summary>รอบ 230: ความยาวหลอด (%) คิดที่เซิร์ฟเวอร์ — <c>js/quota-ui.js</c> เดิมคิดเอง (F2 ข้อ 5)</summary>
    public int Percent => QuotaLimitPolicy.Percent(Used, Limit, WarnLevel);
}

/// <summary>
/// รอบ 229 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §B.1/§B.4) — <b>ด่านโควตาตัวเดียว</b> "เพิ่มได้ไหม" ของทุกทางเข้าที่สร้างของที่นับ
/// (สร้างเว็บ · เชิญผู้ใช้ · สร้างที่พัก) · สูตรอยู่ที่ <see cref="QuotaLimitPolicy"/> (pure) · ล็อกจุดเรียกด้วย <c>tools/required_call_site_check.py</c>
/// <para>Capacity ตรวจใต้ <c>pg_advisory_xact_lock</c> ต่อ (บริษัท, ทรัพยากร) — ผู้เรียกต้องเปิดธุรกรรมเอง แล้วเรียก <see cref="LockAsync"/> ก่อน
/// <see cref="CheckAsync"/> และ insert ในธุรกรรมเดียวกัน (สองคำขอพร้อมกันผ่านได้แค่หนึ่ง)</para>
/// </summary>
public interface IQuotaGate
{
    Task<QuotaLimitPolicy.QuotaLimit> LimitAsync(Guid companyId, string resourceKey, CancellationToken ct = default);
    Task<int> UsedAsync(Guid companyId, string resourceKey, CancellationToken ct = default);
    Task<QuotaLimitPolicy.QuotaDecision> CheckAsync(Guid companyId, string resourceKey, int adding = 1, CancellationToken ct = default);
    /// <summary>ล็อกธุรกรรมต่อ (บริษัท, ทรัพยากร) — ต้องอยู่ในธุรกรรมแล้ว (ไม่งั้นล็อกหลุดทันที = ไม่กันอะไร) ⇒ โยนเมื่อไม่มีธุรกรรม</summary>
    Task LockAsync(Guid companyId, string resourceKey, CancellationToken ct = default);
    Task<List<QuotaSummaryItem>> SummaryAsync(Guid companyId, CancellationToken ct = default);
}

/// <summary>อ่านค่าที่ใช้ร่วม (override · การซื้อที่มีผล · allowance) ด้วย DbContext ตรง — ให้เส้นที่อยู่ลึกใน DI (SubscriptionService · LodgingPropertyQuota)
/// เรียกได้โดยไม่เกิดวงกลม · ทุกคำสั่งกรอง <c>CompanyId</c> (กฎ M)</summary>
public static class QuotaStore
{
    public static async Task<int?> OverrideAsync(AccountingDbContext db, Guid companyId, string resourceKey, CancellationToken ct = default)
        => await db.CompanyQuotaOverrides.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.ResourceKey == resourceKey)
            .Select(o => (int?)o.Limit).FirstOrDefaultAsync(ct);

    public static async Task<List<QuotaLimitPolicy.Purchase>> PurchasesAsync(AccountingDbContext db, Guid companyId, string resourceKey,
        CancellationToken ct = default)
        => await db.CompanyQuotaAddOns.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.ResourceKey == resourceKey && p.Status == QuotaAddOnStatus.Active)
            .Select(p => new QuotaLimitPolicy.Purchase(p.Units, p.UnitSize, p.Status, p.PeriodStartUtc, p.PeriodEndUtc))
            .ToListAsync(ct);

    /// <summary>หน่วยที่ซื้อและยังมีผลอยู่ (หน่วยของทรัพยากร = Units × UnitSize)</summary>
    public static async Task<int> LiveUnitsAsync(AccountingDbContext db, Guid companyId, string resourceKey, DateTime nowUtc, CancellationToken ct = default)
        => QuotaLimitPolicy.PurchasedUnits(await PurchasesAsync(db, companyId, resourceKey, ct), nowUtc);

    /// <summary>แม่แบบแพ็กเกจที่มีผลกับบริษัทนี้ — ผูก License ⇒ แม่แบบของ License · ไม่ผูก ⇒ แม่แบบที่เปิดอยู่ของระดับแพ็กเกจเดียวกัน (ตัวเดียวกับ PlatformPlanPricing)</summary>
    public static async Task<Guid?> PlanTemplateIdAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var sub = await db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new { s.Plan, s.AccountSubscriptionId }).FirstOrDefaultAsync(ct);
        if (sub == null) return null;
        if (sub.AccountSubscriptionId is Guid aid)
        {
            var tid = await db.AccountSubscriptions.AsNoTracking().Where(a => a.Id == aid && !a.IsDeleted)
                .Select(a => (Guid?)a.PlanTemplateId).FirstOrDefaultAsync(ct);
            if (tid != null) return tid;
        }
        return await db.PlanTemplates.AsNoTracking().Where(t => t.Plan == sub.Plan && t.IsActive)
            .OrderBy(t => t.CreatedAt).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
    }

    public static async Task<QuotaLimitPolicy.Allowance?> AllowanceAsync(AccountingDbContext db, Guid? planTemplateId, string resourceKey,
        CancellationToken ct = default)
    {
        if (planTemplateId is not Guid tid) return null;
        return await db.PlanQuotaAllowances.AsNoTracking()
            .Where(a => a.PlanTemplateId == tid && a.ResourceKey == resourceKey)
            .Select(a => new QuotaLimitPolicy.Allowance(a.Included, a.TrialIncluded)).FirstOrDefaultAsync(ct);
    }

    /// <summary>ข้อเสนอที่เปิดขายของทรัพยากรนี้ (มีราคา) — null = ซื้อเพิ่มไม่ได้</summary>
    public static Task<QuotaAddOnOffer?> ActiveOfferAsync(AccountingDbContext db, string resourceKey, CancellationToken ct = default)
        => db.QuotaAddOnOffers.AsNoTracking()
            .Where(o => o.ResourceKey == resourceKey && o.IsActive && o.MonthlyUnitPrice > 0m)
            .FirstOrDefaultAsync(ct);

    /// <summary>ข้อความราคาสั้นในข้อความปฏิเสธ (เช่น "เว็บละ ฿199/เดือน")</summary>
    public static string? PriceText(QuotaAddOnOffer? o)
    {
        if (o == null) return null;
        var res = QuotaResources.Find(o.ResourceKey);
        var unit = o.UnitSize > 1 ? $"{o.UnitSize:N0} {res?.Unit}" : res?.Unit ?? "หน่วย";
        return $"{unit}ละ ฿{o.MonthlyUnitPrice:N2}/เดือน";
    }
}

public class QuotaGateService : IQuotaGate
{
    private readonly AccountingDbContext _db;
    /// <summary>resolve <see cref="ISubscriptionService"/> ตอนใช้ (ไม่ใช่ ctor) — กันวงกลม DI (CompanyService/CmsSiteService ↔ SubscriptionService)
    /// แบบเดียวกับ <c>PlatformBillingDocumentIssuer</c></summary>
    private readonly IServiceProvider _services;

    public QuotaGateService(AccountingDbContext db, IServiceProvider services) { _db = db; _services = services; }

    private ISubscriptionService Subscriptions => (ISubscriptionService)_services.GetService(typeof(ISubscriptionService))!;

    public async Task<QuotaLimitPolicy.QuotaLimit> LimitAsync(Guid companyId, string resourceKey, CancellationToken ct = default)
    {
        var res = QuotaResources.Find(resourceKey) ?? throw new ArgumentException($"ไม่รู้จักทรัพยากรโควตา {resourceKey}");
        var eff = await Subscriptions.GetEffectivePlanAsync(companyId);
        var inTrial = eff?.Status == SubscriptionStatus.Trial;
        int? legacy = res.Legacy switch
        {
            QuotaResources.LegacySource.PlanMaxUsers => eff?.MaxUsers,
            QuotaResources.LegacySource.PlanMaxStorage => eff == null ? (int?)null
                : eff.MaxStorageBytes <= 0 ? 0 : (int)Math.Min(int.MaxValue, eff.MaxStorageBytes / QuotaResources.BytesPerGb),
            QuotaResources.LegacySource.PlanOcrPages => eff?.MaxOcrPagesPerMonth,
            QuotaResources.LegacySource.LodgingFirstFree => (int?)1,
            _ => (int?)null,
        };
        var ov = await QuotaStore.OverrideAsync(_db, companyId, resourceKey, ct);
        var allowance = await QuotaStore.AllowanceAsync(_db, await QuotaStore.PlanTemplateIdAsync(_db, companyId, ct), resourceKey, ct);
        var purchases = await QuotaStore.PurchasesAsync(_db, companyId, resourceKey, ct);
        var limit = QuotaLimitPolicy.Effective(ov, allowance, inTrial, legacy, purchases, DateTime.UtcNow);
        // ฝ่ายค้าน S6: ส่วนเสริมสวิตช์ "ที่พักหลายแห่ง" = ไม่จำกัด (พฤติกรรมเดิม) — ทั้งด่านและหน้าสรุปเห็นเหมือนกัน · override แอดมินที่ตั้งตัวเลขยังชนะ
        if (resourceKey == QuotaResources.LodgingProperties && ov == null && !limit.IsUnlimited
            && _services.GetService(typeof(IEntitlementService)) is IEntitlementService ent
            && (await ent.CheckAsync(companyId, AddOnCodes.LodgingMultiProperty, ct)).Allowed)
            return new QuotaLimitPolicy.QuotaLimit(null, limit.Purchased, null, "addon");
        return limit;
    }

    public async Task<int> UsedAsync(Guid companyId, string resourceKey, CancellationToken ct = default) => resourceKey switch
    {
        // เว็บ: ทุกสถานะที่ยังไม่ลบ (ร่าง/ปิดปรับปรุง/ระงับ นับด้วย — §C: ไม่นับร่าง = สร้างร่างได้ไม่จำกัด)
        QuotaResources.CmsSites => await _db.Sites.CountAsync(s => s.CompanyId == companyId && !s.IsDeleted, ct),
        // ที่นั่ง: สมาชิก + คำเชิญค้าง (สูตรเดียวกับด่านเดิมของ CompanyService.AddUserAsync — คำเชิญที่ยังไม่ตอบกินที่นั่งแล้ว)
        QuotaResources.Users => await _db.CompanyUsers.CountAsync(cu => cu.CompanyId == companyId, ct)
            + await _db.CompanyInvitations.CountAsync(i => i.CompanyId == companyId && i.Status == InvitationStatus.Pending && !i.IsDeleted, ct),
        QuotaResources.LodgingProperties => await _db.LodgingProperties.CountAsync(p => p.CompanyId == companyId && !p.IsDeleted, ct),
        QuotaResources.StorageGb => (int)Math.Min(int.MaxValue,
            (((await Subscriptions.GetStorageStatusAsync(companyId))?.UsedBytes ?? 0L) + QuotaResources.BytesPerGb - 1) / QuotaResources.BytesPerGb),
        _ => 0,
    };

    public async Task<QuotaLimitPolicy.QuotaDecision> CheckAsync(Guid companyId, string resourceKey, int adding = 1, CancellationToken ct = default)
    {
        var limit = await LimitAsync(companyId, resourceKey, ct);
        if (limit.IsUnlimited) return QuotaLimitPolicy.Decide(resourceKey, 0, adding, limit, canBuy: false);
        var used = await UsedAsync(companyId, resourceKey, ct);
        var offer = await QuotaStore.ActiveOfferAsync(_db, resourceKey, ct);
        return QuotaLimitPolicy.Decide(resourceKey, used, adding, limit, canBuy: offer != null, QuotaStore.PriceText(offer));
    }

    public async Task LockAsync(Guid companyId, string resourceKey, CancellationToken ct = default)
    {
        if (_db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("ด่านโควตาต้องเรียกภายในธุรกรรม (pg_advisory_xact_lock หลุดทันทีถ้าไม่มีธุรกรรม)");
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.QuotaCapacity, resourceKey) }, ct);
    }

    public async Task<List<QuotaSummaryItem>> SummaryAsync(Guid companyId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var list = new List<QuotaSummaryItem>();
        var offers = await _db.QuotaAddOnOffers.AsNoTracking().Where(o => o.IsActive && o.MonthlyUnitPrice > 0m)
            .Select(o => o.ResourceKey).ToListAsync(ct);
        foreach (var res in QuotaResources.All)
        {
            var canBuy = offers.Contains(res.Key) && QuotaAddOnPricing.ModesFor(res.Key, QuotaAddOnMode.OneOffMonth | QuotaAddOnMode.RecurringMonthly | QuotaAddOnMode.Coterm) != QuotaAddOnMode.None;
            var buyUrl = canBuy ? QuotaResources.BuyUrl(res.Key) : null;
            var kind = res.Kind.ToString();
            switch (res.Key)
            {
                case QuotaResources.StorageGb:
                {
                    // พื้นที่: ตัวตัดสินเดิม GetStorageStatusAsync (รวมที่ซื้อ/override แล้ว · pool ของ License) — แสดงเป็นไบต์จริง
                    var st = await Subscriptions.GetStorageStatusAsync(companyId);
                    var purchased = await QuotaStore.LiveUnitsAsync(_db, companyId, res.Key, now, ct);
                    if (st == null) { list.Add(Unknown(res, canBuy, buyUrl)); break; }
                    var unlimited = st.MaxBytes >= QuotaResources.UnlimitedBytes;
                    var warn = unlimited ? 0 : st.UsedBytes >= st.MaxBytes ? 2 : st.UsedBytes * 100 >= st.MaxBytes * 80 ? 1 : 0;
                    list.Add(new QuotaSummaryItem(res.Key, res.Label, res.Unit, kind,
                        (int)Math.Min(int.MaxValue, st.UsedBytes / (1024 * 1024)), unlimited ? (int?)null : (int)Math.Min(int.MaxValue, st.MaxBytes / (1024 * 1024)),
                        unlimited, null, purchased, warn, Bytes(st.UsedBytes), unlimited ? "ไม่จำกัด" : Bytes(st.MaxBytes), false,
                        purchased > 0 ? $"รวมที่ซื้อเพิ่ม {purchased:N0} GB" : null, canBuy, buyUrl, "plan"));
                    break;
                }
                case QuotaResources.OcrPagesMonth:
                {
                    // OCR: ตัวนับเดิม (OcrQuotaService) — ที่ซื้อเพิ่มอยู่ในเครดิต OCR
                    var ocr = _services.GetService(typeof(IOcrQuotaService)) as IOcrQuotaService;
                    if (ocr == null) { list.Add(Unknown(res, canBuy, buyUrl)); break; }
                    var s = await ocr.GetQuotaStatusAsync(companyId);
                    var unlimited = s.MaxPagesPerMonth < 0;
                    var limit = unlimited ? (int?)null : s.MaxPagesPerMonth + s.BonusPages;
                    list.Add(new QuotaSummaryItem(res.Key, res.Label, res.Unit, kind, s.UsedThisMonth, limit, unlimited, s.MaxPagesPerMonth,
                        s.CreditPagesRemaining, QuotaLimitPolicy.WarnLevel(s.UsedThisMonth, limit is int l ? l + s.CreditPagesRemaining : (int?)null),
                        $"{s.UsedThisMonth:N0}", unlimited ? "ไม่จำกัด" : $"{limit:N0}", false,
                        s.CreditPagesRemaining > 0 ? $"+ เครดิตที่ซื้อคงเหลือ {s.CreditPagesRemaining:N0} หน้า" : null, canBuy, buyUrl, "plan"));
                    break;
                }
                default:
                {
                    var limit = await LimitAsync(companyId, res.Key, ct);
                    if (res.PerSite)
                    {
                        list.Add(new QuotaSummaryItem(res.Key, res.Label, res.Unit, kind, null, limit.Effective, limit.IsUnlimited, limit.Included,
                            limit.Purchased, 0, "—", limit.IsUnlimited ? "ไม่จำกัด" : $"{limit.Effective:N0} ต่อเว็บ", true,
                            "เพดานของแต่ละเว็บ (ตั้งรายเว็บได้โดยผู้ดูแลระบบ)", false, null, limit.Source));
                        break;
                    }
                    var used = await UsedAsync(companyId, res.Key, ct);
                    list.Add(new QuotaSummaryItem(res.Key, res.Label, res.Unit, kind, used, limit.Effective, limit.IsUnlimited, limit.Included,
                        limit.Purchased, QuotaLimitPolicy.WarnLevel(used, limit.Effective), $"{used:N0}",
                        limit.IsUnlimited ? "ไม่จำกัด" : $"{limit.Effective:N0}", false,
                        limit.Purchased > 0 ? $"รวมที่ซื้อเพิ่ม {limit.Purchased:N0} {res.Unit}" : null, canBuy, buyUrl, limit.Source));
                    break;
                }
            }
        }
        return list;
    }

    /// <summary>โหลดไม่ได้ ≠ ไม่จำกัด (D-4 · team.html เดิมตกเป็น 999)</summary>
    private static QuotaSummaryItem Unknown(QuotaResources.Resource res, bool canBuy, string? buyUrl)
        => new(res.Key, res.Label, res.Unit, res.Kind.ToString(), null, null, false, null, 0, 0, "—", "ดูไม่ได้", false,
            "ยังไม่มีแพ็กเกจ/โหลดข้อมูลไม่ได้", canBuy, buyUrl, "unknown");

    private static string Bytes(long b)
    {
        if (b >= QuotaResources.BytesPerGb) return $"{b / (double)QuotaResources.BytesPerGb:N2} GB";
        if (b >= 1024 * 1024) return $"{b / (1024.0 * 1024):N1} MB";
        return $"{b / 1024.0:N0} KB";
    }
}
