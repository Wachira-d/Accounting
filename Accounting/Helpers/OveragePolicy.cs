using Accounting.Models.Constants;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ราคาส่วนเกิน 1 แถว (ภาพของ <c>OverageRate</c> ที่ pure helper รับ)</summary>
public sealed record OverageRateRow(Guid Id, string ResourceKey, Guid? PlanTemplateId, int UnitSize, decimal UnitPrice,
    DateTime EffectiveFromUtc, DateTime? EffectiveToUtc, bool IsActive);

/// <summary>นโยบายส่วนเกินของบริษัท (ภาพของ <c>CompanyOveragePolicy</c> · ไม่มีแถว = <see cref="None"/>)</summary>
public sealed record OveragePolicyView(OverageMode Mode, decimal? MonthlyCapAmount, string? ResourcesCsv, DateTime? LockedByUnpaidAt,
    string? CapNotifiedPeriod = null, string? StartedNotifiedPeriod = null)
{
    public static readonly OveragePolicyView None = new(OverageMode.HardLock, null, null, null);
    public bool IsLocked => LockedByUnpaidAt != null;
}

/// <summary>ผลตัดสิน "เกินแล้วใช้ต่อได้ไหม" ของ 1 คำขอ — <see cref="Allowed"/> true = ใช้ต่อ + คิดส่วนเกิน <see cref="Units"/> หน่วย ราคา <see cref="UnitPrice"/> ·
/// false ⇒ <see cref="RuleCode"/> บอกเหตุ (ไม่ได้เปิด · ไม่มีราคา · ถึงเพดานเงิน · ถูกล็อกเพราะค้างชำระ) + <see cref="Reason"/> ไทย</summary>
public sealed record OverageDecision(bool Allowed, string ResourceKey, int Units, decimal UnitPrice, decimal Amount, string? RuleCode, string? Reason)
{
    public static OverageDecision Deny(string resource, string rule, string? reason) => new(false, resource, 0, 0m, 0m, rule, reason);
}

/// <summary>
/// รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §B.3/§B.4 · คำตัดสินข้อ 231-1..4) — <b>ตัวตัดสินส่วนเกินตัวเดียว (pure)</b>
///
/// <code>
/// Decide(resource, adding, policy, rate, spentThisMonth, nowUtc):
///   policy.Mode != AllowOverage                       ⇒ ไม่ได้เปิด        (OVERAGE-OFF)
///   policy.LockedByUnpaidAt != null                   ⇒ ถูกล็อกเพราะค้าง (OVERAGE-LOCKED)
///   resource ∉ policy.Resources (ถ้าระบุ)              ⇒ ไม่ได้เปิดทรัพยากรนี้ (OVERAGE-OFF)
///   ไม่มีราคา (rate null / ราคา ≤ 0)                   ⇒ เปิดไม่ได้          (OVERAGE-NO-RATE)  ⇐ ไม่ใช่คิด 0
///   spent + ค่าของคำขอนี้ > cap (ถ้าตั้ง)               ⇒ ถึงเพดานเงิน      (OVERAGE-CAP)
///   อื่น ⇒ Allowed · Units = ceil(adding / UnitSize) · Amount = round(Units × UnitPrice, 2, AwayFromZero)
/// </code>
/// ทุกทางเข้า (DocumentService · OcrQuotaService · NotifyQuotaService · CanFitStorageAsync · งานพื้นที่รายวัน) ถาม <c>IOverageService.DecideAsync</c> ซึ่งเรียกตัวนี้ — ห้ามประกอบเอง
/// </summary>
public static class OveragePolicy
{
    /// <summary>ฉบับเงื่อนไข — ฝ่ายค้านรอบ 231 S3: เพิ่มประโยคตัดบัตรอัตโนมัติ ⇒ bump (ผู้เช่าที่ยอมรับฉบับเก่าต้องยอมรับใหม่ก่อนเปิดอีกครั้ง)</summary>
    public const string PolicyVersion = "2026-11.2";

    /// <summary>ฝ่ายค้านรอบ 231 S1: ทรัพยากรที่คิดต่อเหตุการณ์ (เอกสาร/OCR/ข้อความ) บังคับ UnitSize = 1 — ด่าน Decide ส่ง adding=1 ต่อเหตุการณ์ ขนาดหน่วย &gt; 1 จะคิด 1 หน่วยเต็มต่อฉบับ (ผิดจากที่ผู้เช่าเข้าใจ) ·
    /// พื้นที่ (สรุปสิ้นเดือน) ใช้ UnitSize ได้ · คืนข้อความไทยเมื่อผิด · null = ผ่าน</summary>
    public static string? ValidateRate(string resourceKey, int unitSize)
    {
        var res = OverageResources.Find(resourceKey);
        if (res == null) return "ทรัพยากรต้องเลือกจากรายการของระบบ";
        if (unitSize < 1 || unitSize > 1_000_000) return "ขนาดหน่วยต้องเป็น 1 ขึ้นไป";
        if (res.Realtime && unitSize != 1) return $"{res.Label}คิดต่อ{res.Unit} ณ เวลาที่เกิด — ขนาดหน่วยต้องเป็น 1 (ตั้งราคาต่อ{res.Unit}โดยตรง)";
        return null;
    }

    /// <summary>ทรัพยากรที่นโยบายเปิด — ว่าง = ทุกทรัพยากรที่มีราคา</summary>
    public static IReadOnlySet<string> Resources(string? csv)
        => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(OverageResources.IsKnown).ToHashSet(StringComparer.Ordinal);

    public static bool Covers(OveragePolicyView policy, string resourceKey)
    {
        var set = Resources(policy.ResourcesCsv);
        return set.Count == 0 || set.Contains(resourceKey);
    }

    public static OverageDecision Decide(string resourceKey, int adding, OveragePolicyView policy, OverageRateRow? rate, decimal spentThisMonth, DateTime nowUtc)
    {
        var res = OverageResources.Find(resourceKey);
        if (res == null) return OverageDecision.Deny(resourceKey, OverageResources.RuleNotEnabled, "ทรัพยากรนี้ไม่คิดส่วนเกิน — ซื้อโควตาเพิ่มหรืออัปเกรดแพ็กเกจ");
        if (adding <= 0) adding = 1;
        if (policy.Mode != OverageMode.AllowOverage)
            return OverageDecision.Deny(resourceKey, OverageResources.RuleNotEnabled,
                $"หยุดส่วนที่เกิน (ค่าตั้งต้น) — เปิด \"ใช้ต่อได้ คิดค่าส่วนเกิน\" ได้ที่หน้า \"แพ็กเกจและการชำระเงิน\" ({(rate != null && rate.UnitPrice > 0m ? PriceText(res, rate) : "ยังไม่มีราคาส่วนเกินของรายการนี้")})");
        if (policy.LockedByUnpaidAt is DateTime lockedAt)
            return OverageDecision.Deny(resourceKey, OverageResources.RuleLocked,
                $"ปิดสิทธิ์ส่วนเกินชั่วคราวตั้งแต่ {PlatformInvoiceRules.ThaiDate(lockedAt):dd/MM/yyyy} — ใบแจ้งหนี้ค่าส่วนเกินค้างชำระ · ชำระที่หน้า \"แพ็กเกจและการชำระเงิน\" แล้วระบบจะเปิดคืนอัตโนมัติ");
        if (!Covers(policy, resourceKey))
            return OverageDecision.Deny(resourceKey, OverageResources.RuleNotEnabled, $"ไม่ได้เปิดส่วนเกินของ{res.Label} — เปลี่ยนได้ที่หน้า \"แพ็กเกจและการชำระเงิน\"");
        if (rate == null || rate.UnitPrice <= 0m || !RateLive(rate, nowUtc))
            return OverageDecision.Deny(resourceKey, OverageResources.RuleNoRate, $"ยังไม่มีราคาส่วนเกินของ{res.Label} — ระบบไม่คิดเงินโดยไม่มีราคา · ซื้อโควตาเพิ่มหรือติดต่อผู้ดูแลระบบ NextAcc");
        var unitSize = Math.Max(1, rate.UnitSize);
        var units = (adding + unitSize - 1) / unitSize;
        var amount = Math.Round(units * rate.UnitPrice, 2, MidpointRounding.AwayFromZero);
        if (policy.MonthlyCapAmount is decimal cap && spentThisMonth + amount > cap)
            return OverageDecision.Deny(resourceKey, OverageResources.RuleCap,
                $"ถึงเพดานค่าส่วนเกินต่อเดือนที่ตั้งไว้ ({cap:N2} บาท · ใช้ไป {spentThisMonth:N2}) — ระบบกลับไปหยุดส่วนที่เกินจนขึ้นเดือนใหม่ · เพิ่มเพดานได้ที่หน้า \"แพ็กเกจและการชำระเงิน\"");
        return new OverageDecision(true, resourceKey, units, rate.UnitPrice, amount, null, null);
    }

    public static bool RateLive(OverageRateRow r, DateTime nowUtc)
        => r.IsActive && r.EffectiveFromUtc <= nowUtc && (r.EffectiveToUtc == null || nowUtc < r.EffectiveToUtc.Value);

    /// <summary>"เอกสารละ ฿5.00" / "10 GB-เดือนละ ฿200.00"</summary>
    public static string PriceText(OverageResources.Resource res, OverageRateRow rate)
    {
        var unit = rate.UnitSize > 1 ? $"{rate.UnitSize:N0} {res.Unit}" : res.Unit;
        return $"{unit}ละ ฿{rate.UnitPrice:N2}";
    }

    /// <summary>ส่วนเกิน GB-เดือนของพื้นที่จากตัวอย่างรายวัน (§B.3): <c>ceil(sum(max(usedGb − limitGb, 0)) / วันในเดือน)</c> · ไม่มีตัวอย่าง/ไม่จำกัด = 0 ·
    /// คิดเป็น GB ทศนิยมต่อวันก่อนเฉลี่ย (2.3 GB × 30 วัน ⇒ 3 GB-เดือน) · ฝ่ายค้านรอบ 231 S5: หารด้วย<b>จำนวนวันของงวด</b> ไม่ใช่จำนวนตัวอย่าง
    /// (เปิดส่วนเกิน 6 วันจาก 30 แล้วเกิน 10 GB ทุกวัน = 2 GB-เดือน ไม่ใช่ 10)</summary>
    public static int StorageOverageGbMonth(IReadOnlyList<(long UsedBytes, long? LimitBytes)> dailySamples, int daysInMonth)
    {
        if (dailySamples.Count == 0 || daysInMonth <= 0) return 0;
        decimal sum = 0m;
        foreach (var (used, limit) in dailySamples)
        {
            if (limit is not long max || max <= 0 || max >= QuotaResources.UnlimitedBytes) continue;
            var over = used - max;
            if (over > 0) sum += (decimal)over / QuotaResources.BytesPerGb;
        }
        if (sum <= 0m) return 0;
        var avg = sum / daysInMonth;
        return (int)Math.Ceiling(avg);
    }

    // ───────── ขั้น 1 ของการค้างชำระ (คำตัดสินข้อ 231-4) ─────────

    public enum UnpaidLockAction { None = 0, Lock = 1, Unlock = 2 }

    /// <summary>ใบ USAGE- <b>เกินวันครบกำหนด</b> (ครบกำหนด = วันออก + OverageGraceDays อยู่แล้ว — ฝ่ายค้านรอบ 231 N1: ไม่นับผ่อนผันซ้ำเป็น 2×) ⇒ <see cref="UnpaidLockAction.Lock"/> (ถ้ายังไม่ล็อก) ·
    /// ไม่มีใบค้างเกินกำหนด + ล็อกอยู่ ⇒ <see cref="UnpaidLockAction.Unlock"/> · ใบที่ <c>Excluded</c> (สลิปรอตรวจ ฯลฯ) ไม่นับ · ของที่ใช้ไปแล้วไม่ถูกถอน (ล็อกมีผลกับคำขอถัดไปเท่านั้น)</summary>
    public static UnpaidLockAction UnpaidLock(IReadOnlyList<DunningInvoice> openUsageInvoices, DateTime nowUtc, bool currentlyLocked)
    {
        var beyond = openUsageInvoices.Any(i => !i.Excluded && i.Balance > PlatformInvoiceRules.Epsilon
            && (PlatformInvoiceRules.DaysPastDue(i.DueDateUtc, nowUtc) ?? int.MinValue) > 0);
        if (beyond) return currentlyLocked ? UnpaidLockAction.None : UnpaidLockAction.Lock;
        return currentlyLocked ? UnpaidLockAction.Unlock : UnpaidLockAction.None;
    }

    /// <summary>ใบไหนเข้าขั้น 2 (ระงับทั้งบริษัท): <see cref="OverageUnpaidAction.LockOverageOnly"/> ⇒ ใบ USAGE- ไม่นับ (คงใบต่ออายุ/อื่น) · <see cref="OverageUnpaidAction.LockThenDunning"/> ⇒ ทุกใบ</summary>
    public static IReadOnlyList<DunningInvoice> ForSuspension(IReadOnlyList<DunningInvoice> all, IReadOnlyList<string?> references, OverageUnpaidAction action)
    {
        if (action != OverageUnpaidAction.LockOverageOnly) return all;
        var list = new List<DunningInvoice>(all.Count);
        for (var i = 0; i < all.Count; i++)
            if (PlatformInvoiceRules.KindOf(i < references.Count ? references[i] : null) != PlatformInvoiceKind.Usage) list.Add(all[i]);
        return list;
    }

    /// <summary>ตรวจค่าที่ผู้เช่าส่ง (null = ผ่าน) — เพดาน ≥ 0 · ทรัพยากรต้องอยู่ใน closed list · เปิดต้องยอมรับฉบับปัจจุบัน</summary>
    public static string? ValidateTenantUpdate(OverageMode mode, decimal? cap, IEnumerable<string>? resources, bool accepted, string? acceptedVersion)
    {
        if (cap is decimal c && (c < 0m || c > 10_000_000m)) return "เพดานค่าส่วนเกินต่อเดือนต้องเป็น 0 ขึ้นไป (ว่าง = ไม่จำกัด)";
        if (resources != null) foreach (var r in resources) if (!OverageResources.IsKnown(r)) return $"ทรัพยากร \"{r}\" ไม่คิดส่วนเกิน";
        if (mode == OverageMode.AllowOverage && (!accepted || acceptedVersion != PolicyVersion))
            return $"ต้องยอมรับเงื่อนไขค่าส่วนเกินฉบับ {PolicyVersion} ก่อนเปิดใช้";
        return null;
    }
}

/// <summary>รอบ 231 (§B.2): เลือกราคาส่วนเกินที่มีผล — แถวของแพ็กเกจชนะแถวทั่วไป · ในกลุ่มเดียวกันเอาแถวที่เริ่มมีผลล่าสุด · เฉพาะที่ active และ now ∈ [from, to)</summary>
public static class OverageRateResolver
{
    public static OverageRateRow? Pick(IEnumerable<OverageRateRow> rates, string resourceKey, Guid? planTemplateId, DateTime nowUtc)
        => rates.Where(r => r.ResourceKey == resourceKey && OveragePolicy.RateLive(r, nowUtc)
                            && (r.PlanTemplateId == null || r.PlanTemplateId == planTemplateId))
            .OrderByDescending(r => r.PlanTemplateId.HasValue)
            .ThenByDescending(r => r.EffectiveFromUtc)
            .FirstOrDefault();
}
