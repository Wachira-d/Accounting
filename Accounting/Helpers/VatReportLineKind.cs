namespace Accounting.Helpers;

/// <summary>ฝั่งของบรรทัดในรายงานภาษี — <b>Summary = แถวยอดรวมของแบบ ภ.พ.30
/// ไม่ใช่รายการเอกสาร</b> จึงห้ามโผล่ในตาราง §87 และห้ามเข้ายอดรวมของตาราง</summary>
public enum VatReportSide
{
    Output = 0,   // ภาษีขาย — รายการต่อใบ
    Input = 1,    // ภาษีซื้อ — รายการต่อใบ
    Summary = 2,  // ยอดรวมของแบบ (ยกเว้น/0%/เครดิตยกมา) — ไม่มีวันที่/เลขที่ใบ
}

/// <summary>ช่องของแบบ ภ.พ.30 ที่ยอด "ขาย" บรรทัดนี้เข้า</summary>
public enum Pp30SalesBox
{
    None = 0,       // ไม่ใช่ยอดขาย (ฝั่งซื้อ / เครดิตยกมา / ยอดซื้อยกเว้น)
    Standard7 = 1,  // ช่อง 5 — ยอดขายที่ต้องเสียภาษี
    ZeroRated = 2,  // ช่อง 7 — ส่งออก §80/1
    Exempt = 3,     // ช่อง 8 — ยกเว้น §81
}

/// <summary>
/// **ตัวจำแนกบรรทัดรายงานภาษีตัวเดียวของระบบ** — ตอบว่าบรรทัดหนึ่งเป็น
/// ภาษีขาย / ภาษีซื้อ / แถวยอดรวมของแบบ และเข้าช่องไหนของ ภ.พ.30
///
/// <para>ที่มา (ผู้ใช้รายงาน 2026-09-10): รายงานภาษีขายมีแถวแรกเป็น
/// <c>"ยอดซื้อที่ได้รับยกเว้นภาษี (§81)"</c> ลงวันที่ <c>01/01/0544</c> ยอด 7,151
/// ปนอยู่กับใบกำกับขาย และถูกบวกเข้ายอดรวมของรายงานด้วย. ต้นเหตุ: กติกา
/// "บรรทัดนี้ฝั่งไหน" ถูกเขียนไว้ <b>5 ที่</b> และไม่ตรงกัน —
/// <list type="bullet">
/// <item><c>tax.html</c> (จอ) · <c>TaxService.Export.cs</c> (Excel/CSV) ·
/// <c>NormalizeReportLineOrder</c> ใช้ <b>allow-list 3 ทาง</b> (ถูก)</item>
/// <item><c>PdfGenerationService.TaxReport.cs</c> ใช้ <b>deny-list</b>
/// (<c>"ไม่ใช่ INPUT/JE_INPUT = ขาย"</c>) ⇒ แถวยอดรวม <c>EXEMPT</c> (ฝั่ง<b>ซื้อ</b>)
/// และ <c>VAT_CREDIT_CF</c> (เครดิตภาษีซื้อยกมา) ตกเข้ารายงานภาษี<b>ขาย</b>
/// พร้อมวันที่ <c>default(DateTime)</c> ที่ render เป็น พ.ศ. 0544</item>
/// </list>
/// และรหัสยอดรวมถูกแยกเป็น <c>EXEMPT_SALES</c>/<c>ZERO_RATED_SALES</c> ทีหลัง
/// แต่ <c>ComposePp30</c> ยังอ่าน <c>"EXEMPT"</c> ว่าเป็น "ยอดขายยกเว้น" ⇒
/// <b>ช่อง 7 กับ 8 สลับกัน</b> (ยอดซื้อยกเว้นไปอยู่ช่อง 8 · ยอดขายยกเว้นไปอยู่ช่อง 7)</para>
///
/// <para>กติกา: ทุกจุดที่ต้องรู้ฝั่ง/ช่อง ต้องเรียกตัวนี้ — ห้ามเทียบ
/// <c>IncomeTypeCode</c> เองอีก (กฎเหล็ก #4 A "รายการที่คัดลอกมาด้วยมือ = drift
/// แน่นอน แค่รอเวลา"). ฝั่งหน้าเว็บอ่าน <c>TaxReportLineResponse.Side</c>
/// ที่เซิร์ฟเวอร์คำนวณมาให้แล้ว</para>
/// </summary>
public static class VatReportLineKind
{
    // ── รหัสที่ระบบใช้จริง (ห้ามพิมพ์สตริงซ้ำที่อื่น) ──
    public const string Input = "INPUT";
    public const string JeInput = "JE_INPUT";
    public const string Output = "OUTPUT";
    public const string JeOutput = "JE_OUTPUT";
    /// <summary>ยอด<b>ซื้อ</b>ยกเว้น §81 — รหัสเดิมที่คงไว้ให้รายงานเก่าอ่านออก
    /// (⚠️ รายงานที่สร้าง<b>ก่อน</b>การแยกช่อง 7/8 รหัสนี้รวมยอด<b>ขาย</b>ยกเว้น
    /// ไว้ด้วย — ต้องกดสร้างรายงานใหม่ถึงจะแยกช่องถูก)</summary>
    public const string ExemptPurchases = "EXEMPT";
    public const string ExemptSales = "EXEMPT_SALES";
    public const string ZeroRatedSales = "ZERO_RATED_SALES";
    public const string VatCreditCarryForward = "VAT_CREDIT_CF";
    public const string Summary = "SUMMARY";
    public const string TaxCredit = "TAX_CREDIT";

    /// <summary>บรรทัดนี้อยู่ฝั่งไหน — <c>null</c>/ว่าง = ภาษีขาย (บรรทัดรุ่นเก่า
    /// ที่ยังไม่มีรหัส) ซึ่งเป็นพฤติกรรมเดิมของทุกตัวที่ยุบมารวมกันที่นี่</summary>
    public static VatReportSide SideOf(string? incomeTypeCode)
    {
        var c = (incomeTypeCode ?? "").Trim().ToUpperInvariant();
        return c switch
        {
            Input or JeInput => VatReportSide.Input,
            ExemptPurchases or ExemptSales or ZeroRatedSales
                or VatCreditCarryForward or Summary or TaxCredit => VatReportSide.Summary,
            _ => VatReportSide.Output,
        };
    }

    /// <summary>บรรทัดนี้เป็น "รายการเอกสาร" ที่ลงในตารางรายงานภาษีขาย/ซื้อ
    /// ตาม §87 ได้ไหม — แถวยอดรวมไม่มีวันที่/เลขที่ใบกำกับ/คู่ค้า จึงลงตาราง
    /// ไม่ได้ (และห้ามเข้ายอดรวมท้ายตาราง)</summary>
    public static bool IsDocumentRow(string? incomeTypeCode)
        => SideOf(incomeTypeCode) != VatReportSide.Summary;

    /// <summary>บรรทัดนี้อยู่ในตารางของรายงานฝั่งที่ขอไหม
    /// (<paramref name="wantSales"/> = รายงานภาษีขาย)</summary>
    public static bool BelongsToDetailReport(string? incomeTypeCode, bool wantSales)
        => SideOf(incomeTypeCode) == (wantSales ? VatReportSide.Output : VatReportSide.Input);

    /// <summary>แถวยอดรวมนี้ควรแสดงเป็น "หมายเหตุท้ายรายงาน" ของฝั่งไหน —
    /// ยอดขาย 0%/ยกเว้น ช่วยกระทบยอดฝั่งขาย · ยอดซื้อยกเว้นช่วยฝั่งซื้อ ·
    /// เครดิตยกมาเป็นเรื่องของแบบ ภ.พ.30 เท่านั้น (ไม่โชว์ในสองรายงานนั้น).
    /// <b>ห้ามซ่อนเงียบ</b> — ตัวเลขที่ตัดออกจากตารางต้องยังหาเจอที่ใดที่หนึ่ง</summary>
    public static bool IsNoteFor(string? incomeTypeCode, bool salesReport)
    {
        var c = (incomeTypeCode ?? "").Trim().ToUpperInvariant();
        if (SideOf(c) != VatReportSide.Summary) return false;
        return salesReport
            ? c is ExemptSales or ZeroRatedSales
            : c == ExemptPurchases;
    }

    /// <summary>ช่องของ ภ.พ.30 ที่ยอดขายบรรทัดนี้เข้า
    ///
    /// <para>⚠️ ยอด<b>ขาย</b>ที่เป็น 0%/ยกเว้น มาจาก<b>แถวยอดรวม</b>เท่านั้น
    /// (<c>Pp30SalesClassifier.Split</c> แยกให้รายบรรทัดตอน generate) — แถวรายการ
    /// เอกสารเก็บ <c>IncomeAmount = SubTotal − ยอดยกเว้น</c> ซึ่ง<b>ยังรวมฐาน 0%</b>
    /// ไว้ ⇒ ถ้านับแถวเอกสารเข้าช่อง 7 ด้วยจะได้ยอดซ้ำสองเท่า. ตัวเลขช่อง 5
    /// จึงต้องคิดแบบลบ (ดู <see cref="StandardBase"/>) ไม่ใช่คัดแถวตามอัตรา</para></summary>
    public static Pp30SalesBox SalesBoxOf(string? incomeTypeCode)
    {
        var c = (incomeTypeCode ?? "").Trim().ToUpperInvariant();
        return c switch
        {
            ZeroRatedSales => Pp30SalesBox.ZeroRated,
            ExemptSales => Pp30SalesBox.Exempt,
            _ => SideOf(c) == VatReportSide.Output ? Pp30SalesBox.Standard7 : Pp30SalesBox.None,
        };
    }

    /// <summary>ช่อง 5 (ยอดขายที่ต้องเสียภาษี) = Σ แถวรายการฝั่งขาย − ยอดขาย 0%
    ///
    /// <para>แถวรายการเก็บ "ฐานที่ไม่ยกเว้น" (= ฐาน 7% + ฐาน 0%) ⇒ ลบยอด 0% ออก
    /// ได้ช่อง 5 พอดี และรวมสามช่องกลับมาได้ยอดขายทั้งหมดเป๊ะ — ไม่ต้องคัด
    /// แถวตาม <c>TaxRate</c> ซึ่งใช้ไม่ได้กับใบที่มีทั้ง 7% และ 0% ในใบเดียว
    /// (<c>TaxRate</c> ของแถวคืออัตรา<b>สูงสุด</b>ในใบ)</para></summary>
    public static decimal StandardBase(decimal documentRowsBase, decimal zeroRatedBase)
        => documentRowsBase - zeroRatedBase;
}
