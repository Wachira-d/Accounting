namespace Accounting.Helpers;

/// <summary>ช่องของแบบ ภ.พ.30 ทางการ — <b>หมายเลขช่องมีที่นี่ที่เดียว</b> (รอบ 269 ฝ่ายค้าน M3 · ชุดสอง M3)
///
/// <para>ที่มา: ระบบพิมพ์เลขช่องเองหลายที่และไม่ตรงกัน — แถวสรุปบนจอเขียน "ช่อง 7" (0%) / "ช่อง 8" (ยกเว้น) · PDF ของระบบใช้ 3/4 ·
/// ตัวจำแนกเขียน "ช่อง 5" = ยอดขายที่ต้องเสียภาษี · ผู้ใช้ที่กรอกแบบตามเลขบนจอจะเอายอด 0% ไปลงช่องภาษีซื้อของแบบจริง ·
/// ชุดสอง: ช่อง 8/9 เดิมพิมพ์ยอด<b>หลัง</b>หักภาษีชำระเกินยกมา ⇒ ผู้กรอกเว็บสรรพากรหักเครดิตยกมาซ้ำ (ชำระขาดเท่าเครดิต)</para>
/// <para>ลำดับตามแบบ ภ.พ.30: 1 ยอดขาย · 2 ยอดขาย 0% · 3 ยอดขายยกเว้น · 4 ยอดขายที่ต้องเสียภาษี (1 − 2 − 3) · 5 ภาษีขาย · 6 ยอดซื้อที่มีสิทธิหักภาษีซื้อ ·
/// 7 ภาษีซื้อ · 8 ภาษีที่ต้องชำระเดือนนี้ (5 &gt; 7) · 9 ภาษีที่ชำระเกินเดือนนี้ (5 &lt; 7) · 10 ภาษีที่ชำระเกินยกมา · 11 ภาษีสุทธิที่ต้องชำระ · 12 ภาษีสุทธิที่ชำระเกิน —
/// ทุกจอ/PDF/Excel/e-Filing/ข้อความต้องเรียก <see cref="Pp30FormBoxes"/> ห้ามพิมพ์เลขหรือคิดยอดช่องเอง</para></summary>
public enum Pp30FormBox
{
    TotalSales = 1,
    ZeroRatedSales = 2,
    ExemptSales = 3,
    TaxableSales = 4,
    OutputVat = 5,
    Purchases = 6,
    InputVat = 7,
    TaxPayable = 8,
    TaxOverpaid = 9,
    OverpaidBroughtForward = 10,
    NetTaxPayable = 11,
    NetTaxOverpaid = 12,
}

public static class Pp30FormBoxes
{
    /// <summary>รหัสแถวเครดิตภาษีชำระเกินยกมาบนรายงาน (TaxAmount เก็บติดลบ)</summary>
    public const string CreditBroughtForwardCode = "VAT_CREDIT_CF";

    /// <summary>หมายเลขช่องบนแบบ (ข้อความ)</summary>
    public static string No(Pp30FormBox box) => ((int)box).ToString();

    /// <summary>"ช่อง N"</summary>
    public static string Ref(Pp30FormBox box) => $"ช่อง {No(box)}";

    /// <summary>ชื่อช่องตามแบบ</summary>
    public static string Name(Pp30FormBox box) => box switch
    {
        Pp30FormBox.TotalSales => "ยอดขายในเดือนนี้",
        Pp30FormBox.ZeroRatedSales => "ยอดขายที่เสียภาษีในอัตราร้อยละ 0 (§80/1)",
        Pp30FormBox.ExemptSales => "ยอดขายที่ได้รับยกเว้น (§81)",
        Pp30FormBox.TaxableSales => "ยอดขายที่ต้องเสียภาษี",
        Pp30FormBox.OutputVat => "ภาษีขายเดือนนี้",
        Pp30FormBox.Purchases => "ยอดซื้อที่มีสิทธินำภาษีซื้อมาหักในการคำนวณภาษีเดือนนี้",
        Pp30FormBox.InputVat => "ภาษีซื้อเดือนนี้",
        Pp30FormBox.TaxPayable => "ภาษีที่ต้องชำระเดือนนี้",
        Pp30FormBox.TaxOverpaid => "ภาษีที่ชำระเกินเดือนนี้",
        Pp30FormBox.OverpaidBroughtForward => "ภาษีที่ชำระเกินยกมา",
        Pp30FormBox.NetTaxPayable => "ภาษีสุทธิที่ต้องชำระ",
        Pp30FormBox.NetTaxOverpaid => "ภาษีสุทธิที่ชำระเกิน",
        _ => box.ToString(),
    };

    /// <summary>"ช่อง N ชื่อช่อง"</summary>
    public static string Label(Pp30FormBox box) => $"{Ref(box)} {Name(box)}";

    /// <summary>ยอดทุกช่องของแบบจากบรรทัดรายงานที่บันทึกไว้ — <b>สูตรเดียว</b>ของจอ · PDF · Excel · e-Filing · audit (กฎ #4 A สอง renderer ห้าม drift) ·
    /// แถวที่ติ๊กออกไม่นับ · ช่องยอดขายจัดด้วย <see cref="VatReportLineKind"/> · ภาษีขาย/ซื้อ = ยอดของรายงาน ·
    /// ช่อง 8/9 = ภาษีขาย − ภาษีซื้อ <b>ก่อน</b>หักเครดิตยกมา · ช่อง 10 = แถว <see cref="CreditBroughtForwardCode"/> · ช่อง 11/12 = 8 − 9 − 10</summary>
    public static IReadOnlyList<(Pp30FormBox Box, decimal Amount)> Figures(
        IEnumerable<(string? IncomeTypeCode, decimal IncomeAmount, decimal TaxAmount, bool IsExcluded)> lines, decimal outputVat, decimal inputVat)
    {
        var active = lines.Where(l => !l.IsExcluded).ToList();
        decimal SalesBox(Pp30SalesBox b) => active.Where(l => VatReportLineKind.SalesBoxOf(l.IncomeTypeCode) == b).Sum(l => l.IncomeAmount);
        var zero = SalesBox(Pp30SalesBox.ZeroRated);
        var exempt = SalesBox(Pp30SalesBox.Exempt);
        var taxable = VatReportLineKind.StandardBase(SalesBox(Pp30SalesBox.Standard7), zero);
        var purchases = active.Where(l => VatReportLineKind.SideOf(l.IncomeTypeCode) == VatReportSide.Input).Sum(l => l.IncomeAmount);
        var month = outputVat - inputVat;
        var cf = active.Where(l => l.IncomeTypeCode == CreditBroughtForwardCode).Sum(l => Math.Abs(l.TaxAmount));
        var net = month - cf;
        return new[]
        {
            (Pp30FormBox.TotalSales, taxable + zero + exempt),
            (Pp30FormBox.ZeroRatedSales, zero),
            (Pp30FormBox.ExemptSales, exempt),
            (Pp30FormBox.TaxableSales, taxable),
            (Pp30FormBox.OutputVat, outputVat),
            (Pp30FormBox.Purchases, purchases),
            (Pp30FormBox.InputVat, inputVat),
            (Pp30FormBox.TaxPayable, month > 0m ? month : 0m),
            (Pp30FormBox.TaxOverpaid, month < 0m ? -month : 0m),
            (Pp30FormBox.OverpaidBroughtForward, cf),
            (Pp30FormBox.NetTaxPayable, net > 0m ? net : 0m),
            (Pp30FormBox.NetTaxOverpaid, net < 0m ? -net : 0m),
        };
    }
}
