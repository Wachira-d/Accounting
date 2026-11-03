using Accounting.Models.Constants;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 229 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §B.4 · คำตัดสินข้อ 229-1/229-2) — <b>ตัวตัดสินเดียวของ "เพดานที่มีผล" และ "เพิ่มได้ไหม"</b> · pure (เทสต์ได้)
///
/// <code>
/// included  = override รายบริษัท (แอดมินชนะ)
///          ?? allowance ของแพ็กเกจ (ทดลองใช้ ⇒ TrialIncluded ?? Included)
///          ?? ช่องเดิมของแพ็กเกจ (MaxUsers/MaxStorageBytes/… — ตัวตั้งเดิม ไม่ทำสำเนา)
///          ?? ไม่จำกัด            ⇐ ยังไม่ตั้ง = ไม่จำกัด ⇒ deploy แล้วไม่มีผู้เช่าเดิมคนไหนถูกบล็อก
/// purchased = Σ Units × UnitSize ของการซื้อที่ Active และ now ∈ [PeriodStart, PeriodEnd)
/// effective = included ไม่จำกัด ? ไม่จำกัด : included + purchased
/// </code>
/// <para>นับ<b>ต่อบริษัท</b> (คำตัดสินข้อ 229-1 — ไม่รวมข้าม License) · ค่าลบ = ไม่จำกัด ·
/// <b>เกินเพดาน = บล็อกการเพิ่มเท่านั้น</b> ของเดิมใช้/แก้ได้ทั้งหมด ไม่ลบ ไม่ปิดเว็บ (คำตัดสินข้อ 229-2) — ด่านนี้ไม่มีคำสั่งลบอะไรเลยโดยโครงสร้าง</para>
/// </summary>
public static class QuotaLimitPolicy
{
    public const int Unlimited = -1;

    public sealed record Allowance(int Included, int? TrialIncluded);

    public sealed record Purchase(int Units, int UnitSize, QuotaAddOnStatus Status, DateTime PeriodStartUtc, DateTime? PeriodEndUtc);

    /// <param name="Included">null = ไม่จำกัด</param>
    /// <param name="Effective">null = ไม่จำกัด</param>
    /// <param name="Source">"override" · "plan" · "legacy" · "default" (ไม่ได้ตั้ง)</param>
    public sealed record QuotaLimit(int? Included, int Purchased, int? Effective, string Source)
    {
        public bool IsUnlimited => Effective == null;
    }

    /// <summary>การซื้อแถวนี้ให้สิทธิ์อยู่ ณ <paramref name="nowUtc"/> ไหม — ตัวเดียวของด่าน · หน้าสรุป · งานรายเดือน</summary>
    public static bool IsLive(QuotaAddOnStatus status, DateTime periodStartUtc, DateTime? periodEndUtc, DateTime nowUtc)
        => status == QuotaAddOnStatus.Active && periodStartUtc <= nowUtc && (periodEndUtc == null || nowUtc < periodEndUtc.Value);

    public static int PurchasedUnits(IEnumerable<Purchase> purchases, DateTime nowUtc)
    {
        long sum = 0;
        foreach (var p in purchases)
            if (IsLive(p.Status, p.PeriodStartUtc, p.PeriodEndUtc, nowUtc) && p.Units > 0 && p.UnitSize > 0)
                sum += (long)p.Units * p.UnitSize;
        return sum > int.MaxValue ? int.MaxValue : (int)sum;
    }

    /// <summary>เพดานที่มีผล — ดูลำดับที่หัวคลาส</summary>
    public static QuotaLimit Effective(int? overrideLimit, Allowance? allowance, bool inTrial, int? legacyLimit,
        IEnumerable<Purchase> purchases, DateTime nowUtc)
    {
        int? raw; string source;
        if (overrideLimit is int o) { raw = o; source = "override"; }
        else if (allowance != null) { raw = inTrial ? (allowance.TrialIncluded ?? allowance.Included) : allowance.Included; source = "plan"; }
        else if (legacyLimit is int l) { raw = l; source = "legacy"; }
        else { raw = null; source = "default"; }

        var purchased = PurchasedUnits(purchases, nowUtc);
        int? included = raw is int r && r >= 0 ? r : null;
        if (included == null) return new QuotaLimit(null, purchased, null, source);
        var eff = (long)included.Value + purchased;
        return new QuotaLimit(included, purchased, eff > int.MaxValue ? int.MaxValue : (int)eff, source);
    }

    /// <summary>ผลของด่าน "เพิ่มได้ไหม" — <see cref="BlockReason"/> = ข้อความไทยพร้อมทางไปต่อสองทาง (ลบ/ซื้อ) · <see cref="RuleCode"/> ให้หน้าเว็บเปิดหน้าต่างซื้อ</summary>
    public sealed record QuotaDecision(bool Allowed, string ResourceKey, int Used, int? Limit, int? Remaining, string? BlockReason,
        string? RuleCode, string? BuyUrl);

    /// <summary>เพิ่มอีก <paramref name="adding"/> หน่วยได้ไหม (ใช้ไป + เพิ่ม ≤ เพดาน) · <paramref name="buyPriceText"/> = ราคาที่จะบอกในข้อความ (เช่น "เว็บละ ฿199/เดือน") · null = ไม่บอก</summary>
    public static QuotaDecision Decide(string resourceKey, int used, int adding, QuotaLimit limit, bool canBuy, string? buyPriceText = null)
    {
        var res = QuotaResources.Find(resourceKey);
        if (adding < 0) adding = 0;
        if (limit.Effective is not int max)
            return new QuotaDecision(true, resourceKey, used, null, null, null, null, null);
        var remaining = Math.Max(0, max - used);
        if (used + adding <= max)
            return new QuotaDecision(true, resourceKey, used, max, remaining, null, null, null);

        var label = res?.Label ?? resourceKey;
        var unit = res?.Unit ?? "รายการ";
        var free = string.IsNullOrWhiteSpace(res?.FreeUpHint) ? "ลดการใช้งานลง" : res!.FreeUpHint;
        var buy = canBuy
            ? " หรือซื้อเพิ่ม" + (string.IsNullOrWhiteSpace(buyPriceText) ? "" : $" ({buyPriceText})") + " ที่หน้า \"แพ็กเกจและการชำระเงิน\""
            : " หรือติดต่อผู้ดูแลระบบเพื่ออัปเกรดแพ็กเกจ";
        var msg = $"เพิ่ม{label}ไม่ได้ — สิทธิ์ของคุณมี {max:N0} {unit} ใช้ไป {used:N0} {unit}"
                  + (adding > 1 ? $" (ต้องการเพิ่ม {adding:N0})" : "")
                  + $" · {free}{buy} · ของเดิมทั้งหมดยังใช้และแก้ไขได้ตามปกติ";
        return new QuotaDecision(false, resourceKey, used, max, remaining, msg, QuotaResources.FullRuleCode(resourceKey),
            canBuy ? QuotaResources.BuyUrl(resourceKey) : null);
    }

    /// <summary>รอบ 230: % ความยาวหลอด (0–100 · ปัด AwayFromZero) — เดิม <c>js/quota-ui.js</c> คิดเอง · ไม่จำกัด/ไม่รู้ = 0 · เพดาน 0 หรือเต็ม = 100</summary>
    public static int Percent(int? used, int? limit, int warnLevel)
    {
        if (limit is int max && max > 0 && used is int u)
            return (int)Math.Min(100m, Math.Max(0m, Math.Round(u * 100m / max, 0, MidpointRounding.AwayFromZero)));
        return warnLevel == 2 ? 100 : 0;
    }

    /// <summary>ระดับเตือนของหลอด (0 ปกติ · 1 ≥ 80% · 2 เต็ม/เกิน) — หน้าเว็บแสดงอย่างเดียว</summary>
    /// <remarks>รอบ 231: ตัวตัดสินเดียว <see cref="QuotaWarnPolicy.Severity"/> · <paramref name="percents"/> = เกณฑ์ของแอดมิน (null = ค่าตั้งต้น 80/90/100)</remarks>
    public static int WarnLevel(int used, int? limit, IReadOnlyList<int>? percents = null)
        => QuotaWarnPolicy.Severity(used, limit, percents);
}
