namespace Accounting.Helpers;

/// <summary>เงื่อนไขชำระของคำสั่งซื้อตามสั่ง — ค่าที่เก็บใน <c>SiteCommerceConfig.MtoPaymentTermMode</c> / <c>SiteOrder.PaymentTermMode</c></summary>
public static class PaymentTermMode
{
    public const string Full = "full";
    public const string DepositPercent = "deposit_percent";
    public const string DepositFixed = "deposit_fixed";
    public const string PayOnPickup = "pay_on_pickup";

    public static readonly IReadOnlyList<(string Value, string Label)> All = new[]
    {
        (Full, "ชำระเต็มจำนวนตอนสั่ง"),
        (DepositPercent, "มัดจำเป็นเปอร์เซ็นต์ ส่วนที่เหลือจ่ายตอนรับ"),
        (DepositFixed, "มัดจำยอดคงที่ ส่วนที่เหลือจ่ายตอนรับ"),
        (PayOnPickup, "ชำระเมื่อรับสินค้า"),
    };

    public static string Label(string? m) => All.FirstOrDefault(a => a.Value == m).Label ?? All[0].Label;

    /// <summary>ว่าง = เต็มจำนวน (พฤติกรรมเดิม) · ไม่รู้จัก = โยน</summary>
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return Full;
        foreach (var (v, _) in All) if (v == s) return v;
        throw new BusinessRuleException($"เงื่อนไขชำระ \"{raw}\" ไม่รู้จัก — เลือกได้: {string.Join(" · ", All.Select(a => a.Label))}", "MTO-PAY-TERM");
    }
}

/// <summary>ยอดที่ต้องจ่าย ณ ตอนนี้ของคำสั่งซื้อหนึ่งใบ</summary>
/// <param name="DepositRequired">ยอดมัดจำที่ต้องจ่ายก่อนร้านเริ่มทำ (โหมดเต็มจำนวน = ยอดทั้งใบ · ชำระเมื่อรับ = 0)</param>
/// <param name="DueNow">ยอดที่<b>ต้อง</b>จ่ายตอนนี้เพื่อยืนยันคำสั่งซื้อ (มัดจำที่ยังขาด)</param>
/// <param name="Balance">ยอดคงค้างทั้งใบ (รวมส่วนที่จ่ายตอนรับ)</param>
/// <param name="NextPayable">ยอดที่ช่องจ่ายออนไลน์/สลิปเปิดให้จ่ายรอบนี้ = มัดจำที่ยังขาด ถ้าไม่ขาดแล้ว = ยอดคงค้างที่เหลือ</param>
public sealed record PaymentDue(string Mode, decimal Total, decimal DepositRequired, decimal Paid,
    decimal DueNow, decimal Balance, decimal NextPayable);

/// <summary>
/// **ตัวคำนวณ "ยอดที่ต้องจ่ายตอนนี้" ของคำสั่งซื้อตามสั่ง — ตัวเดียว** (หน้าเช็กเอาต์ · สลิป · ช่องจ่ายออนไลน์ · หลังบ้าน)
///
/// <para>═══ ที่มา (ทีม K · 2026-10-22) ═══ เดิมทุกทางเข้าตั้งยอดจ่าย = <c>TotalAmount − PaidAmount</c> เอง
/// (<c>RecordPaymentSlipAsync</c> ใช้ <c>TotalAmount</c> ตรง ๆ · <c>PublicPaymentResolver</c> ลบเอง) ⇒ ร้านที่รับมัดจำทำไม่ได้</para>
///
/// <para><b>ขอบเขต</b>: ตัวนี้ตัดสินแค่ "ยอด" — การลงบัญชียังเป็นเส้นเดิมของคำสั่งซื้อออนไลน์ทุกบาท
/// (<c>ConfirmPaymentAsync</c>: เอกสารขาย + บันทึกรับชำระบางส่วนเท่าเงินที่ได้จริง) · ห้ามเขียน posting มัดจำใหม่ที่นี่ ·
/// มัดจำ % ปัด 2 ตำแหน่ง <c>AwayFromZero</c> · มัดจำคงที่เกินยอดใบ = ยอดใบ · ผลไม่ติดลบเสมอ</para>
/// </summary>
public static class MtoPaymentTerms
{
    private const MidpointRounding R = MidpointRounding.AwayFromZero;

    public static PaymentDue Compute(string? mode, decimal total, decimal? depositPercent, decimal? depositFixed, decimal paid)
    {
        var m = PaymentTermMode.Normalize(mode);
        var t = total < 0m ? 0m : total;
        var p = paid < 0m ? 0m : paid;

        decimal deposit = m switch
        {
            PaymentTermMode.DepositPercent => Math.Round(t * Clamp(depositPercent ?? 0m, 0m, 100m) / 100m, 2, R),
            PaymentTermMode.DepositFixed => Math.Min(t, Math.Max(0m, Math.Round(depositFixed ?? 0m, 2, R))),
            PaymentTermMode.PayOnPickup => 0m,
            _ => t,
        };

        return Tail(m, t, deposit, p);
    }

    /// <summary>ยอดของคำสั่งซื้อที่<b>ตรึงมัดจำไว้แล้ว</b>ตอนสั่ง (<c>SiteOrder.PaymentTermMode/DepositRequiredAmount</c>) —
    /// ทางเข้าหลังสั่งซื้อทุกตัว (สลิป · ช่องจ่ายออนไลน์ · หลังบ้าน) ใช้ตัวนี้ ห้ามคิดมัดจำใหม่จากค่าตั้งวันนี้ ·
    /// ออเดอร์เดิม (mode null) = เต็มจำนวน ⇒ ยอดเดิมทุกสตางค์</summary>
    public static PaymentDue FromSnapshot(string? mode, decimal total, decimal depositRequired, decimal paid)
    {
        var m = string.IsNullOrWhiteSpace(mode) ? PaymentTermMode.Full : PaymentTermMode.Normalize(mode);
        var t = total < 0m ? 0m : total;
        var deposit = m switch
        {
            PaymentTermMode.Full => t,
            PaymentTermMode.PayOnPickup => 0m,
            _ => Math.Min(t, Math.Max(0m, depositRequired)),
        };
        return Tail(m, t, deposit, paid < 0m ? 0m : paid);
    }

    private static PaymentDue Tail(string m, decimal t, decimal deposit, decimal p)
    {
        var balance = Math.Max(0m, t - p);
        var dueNow = Math.Max(0m, Math.Min(deposit - p, balance));
        var next = dueNow > 0m ? dueNow : balance;
        return new PaymentDue(m, t, deposit, p, dueNow, balance, next);
    }

    /// <summary>ตรวจค่าตั้งของร้านตอนบันทึก — มัดจำ % ต้อง 0 &lt; x &lt; 100 · มัดจำคงที่ต้อง &gt; 0</summary>
    public static void ValidateConfig(string mode, decimal? depositPercent, decimal? depositFixed)
    {
        if (mode == PaymentTermMode.DepositPercent && (depositPercent is not decimal pct || pct <= 0m || pct >= 100m))
            throw new BusinessRuleException("มัดจำเปอร์เซ็นต์ต้องมากกว่า 0 และน้อยกว่า 100 (100% = เลือก \"ชำระเต็มจำนวน\")", "MTO-PAY-TERM");
        if (mode == PaymentTermMode.DepositFixed && (depositFixed is not decimal amt || amt <= 0m))
            throw new BusinessRuleException("ยอดมัดจำคงที่ต้องมากกว่า 0", "MTO-PAY-TERM");
    }

    private static decimal Clamp(decimal v, decimal lo, decimal hi) => v < lo ? lo : v > hi ? hi : v;
}
