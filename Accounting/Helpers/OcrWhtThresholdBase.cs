namespace Accounting.Helpers;

/// <summary>ฐานเกณฑ์ 1,000 บาทมาจากช่องไหนของสแกน</summary>
public enum OcrWhtBaseSource
{
    /// <summary>ไม่รู้ยอด ⇒ ไม่ตัดสินแทน</summary>
    None = 0,
    /// <summary>ยอดรวมทั้งสิ้น − VAT ที่อ่านได้ (ยอดหลังส่วนลด ไม่รวม VAT)</summary>
    TotalLessVat = 1,
    /// <summary>ยอดก่อน VAT ที่กระดาษพิมพ์ (VAT อ่านไม่ได้)</summary>
    SubTotal = 2,
    /// <summary>ยอดรวมทั้งสิ้นของผู้ขายที่ไม่มีหลักฐานว่าเก็บ VAT (ไม่มี VAT ข้างใน)</summary>
    TotalNoVat = 3,
    /// <summary>ยอดรวมทั้งสิ้นของใบกำกับภาษีที่อ่าน VAT/ยอดก่อน VAT ไม่ได้ — <b>มี VAT ข้างในแต่ไม่รู้เท่าไร</b></summary>
    TotalVatInclusive = 4,
    /// <summary>ผลรวมบรรทัด (ไม่มียอดหัวใบ)</summary>
    LineSum = 5,
}

/// <summary>คำตอบของเกณฑ์ 1,000 บาทบนฐานที่ได้</summary>
public enum OcrWhtThresholdVerdict
{
    /// <summary>ไม่รู้ยอด — ผู้เรียกใช้พฤติกรรมเดิม (ไม่ตัดสินแทน)</summary>
    Unknown = 0,
    /// <summary>ถึงเกณฑ์ (≥ 1,000)</summary>
    Meets = 1,
    /// <summary>ต่ำกว่าเกณฑ์</summary>
    Below = 2,
    /// <summary>ใบกำกับภาษีที่อ่าน VAT ไม่ได้ — ยอดรวม VAT ถึงเกณฑ์ แต่ยอดก่อน VAT (ถ้า VAT 7%) ต่ำกว่า ⇒ <b>ตัดสินไม่ได้</b> (ห้ามเสนออัตราแบบมั่นใจ)</summary>
    Unsure = 3,
}

/// <summary>
/// **ฐานของเกณฑ์ 1,000 บาท (ท.ป.4/2528 ข้อ 12) + ยอดหัก ณ ที่จ่ายของสแกน — ตัวตั้งตัวเดียว** (ฝ่ายค้าน EC3 P2-5 · ทีม EC4 · 2026-10-16)
///
/// <para>═══ ที่มา ═══ สามจุดใช้ฐานคนละสูตร: ตัวจัดหมวด (<c>ExpenseCategoryResolver.ApplyTo</c>) = ยอดรวม − VAT · ตัวอ่านกระดาษ
/// (<c>OcrService.ParseThaiDocument</c>) = SubTotal ?? Total · ตอนสร้างเอกสาร (<c>OcrService.CreateDocumentFromScanAsync</c>) = ยอดรวม − VAT ⇒
/// ใบเดียวกันได้คำตอบ "ถึงเกณฑ์" ต่างกันตามทางเข้า · ใบค่าบริการ 950 ก่อน VAT ที่อ่าน VAT ไม่ได้ ⇒ ฐาน 1,016.50 ⇒ ได้ข้อเสนอ 3%</para>
///
/// <para>═══ สูตร (เลือกตามกฎหมาย) ═══ ฐานคือ<b>เงินได้ที่จ่ายจริง ไม่รวม VAT</b> (ท.ป.4/2528 ข้อ 12 — ภาษีมูลค่าเพิ่มไม่ใช่เงินได้ของผู้รับ) และ<b>หลังส่วนลด</b>
/// (ส่วนลดไม่ได้จ่าย):</para>
/// <list type="number">
/// <item>อ่าน VAT ได้ (รวม VAT = 0) + มียอดรวม ⇒ <b>ยอดรวม − VAT</b> — ยอดหลังส่วนลด ไม่รวม VAT · เป็นสูตรเดียวกับยอดหักตอนสร้างเอกสาร
/// (SubTotal ที่พิมพ์บนใบมีส่วนลดท้ายบิลมักเป็นยอด<b>ก่อน</b>ลด ⇒ ฐานเกิน = ส่วนลด × อัตรา — เหตุผลเดิมของจุดสร้างเอกสาร · จึงไม่เอา SubTotal มาก่อน)</item>
/// <item>อ่าน VAT ไม่ได้ + มี SubTotal ⇒ <b>SubTotal</b> — ตัวเลขไม่รวม VAT ที่กระดาษพิมพ์เอง (ยอดรวมอาจมี VAT ข้างใน = เกินทุกใบ 7%) ·
/// เว้นแต่ยอดรวม<b>ต่ำกว่า</b> SubTotal (VAT ไม่ทำให้ยอดลด ⇒ มีส่วนลดหลัง SubTotal) ⇒ ยอดรวม</item>
/// <item>อ่าน VAT ไม่ได้ + ไม่มี SubTotal + <b>ไม่มีหลักฐานว่าผู้ขายเก็บ VAT</b> ⇒ ยอดรวม (ใบเสร็จผู้ไม่จด VAT ไม่มี VAT ข้างใน)</item>
/// <item>อ่าน VAT ไม่ได้ + ไม่มี SubTotal + <b>ใบกำกับภาษี/ใบเพิ่ม-ลดหนี้</b> (ผู้ขายเก็บ VAT) ⇒ <b>ห้ามใช้ยอดรวมเป็นฐานของการตัดสิน</b>: ยอดรวมคือขอบบน ·
/// ยอดรวม × 100/107 คือขอบล่างเมื่อ VAT 7% · สองขอบอยู่คนละฝั่งของ 1,000 ⇒ <see cref="OcrWhtThresholdVerdict.Unsure"/> (บอกผู้ใช้ ไม่ตัดสินแทน — ไม่แต่ง VAT ขึ้นเอง)
/// · ยอดหัก ณ ที่จ่ายตอนสร้างเอกสารยังใช้ยอดรวม (พฤติกรรมเดิม — ไม่มีตัวเลขก่อน VAT จากกระดาษให้ใช้)</item>
/// <item>ไม่มียอดหัวใบ ⇒ ผลรวมบรรทัด · ไม่มีอะไรเลย ⇒ ไม่รู้</item>
/// </list>
/// <para>จุดเรียกทั้งสามถูกล็อกด้วย <c>tools/required_call_site_check.py</c> (ห้ามประกอบสูตรเอง)</para>
/// </summary>
public static class OcrWhtThresholdBase
{
    public readonly record struct Result(decimal? Amount, OcrWhtBaseSource Source, OcrWhtThresholdVerdict Verdict, decimal? ExVatLowerBound);

    /// <summary>ชนิดกระดาษที่ออกได้เฉพาะผู้จด VAT (ยอดรวมมี VAT ข้างใน)</summary>
    private static readonly HashSet<string> VatInvoiceTypes = new(StringComparer.Ordinal) { "TaxInvoice", "DebitNote", "CreditNote" };

    /// <summary>ชนิดกระดาษนี้ออกโดยผู้จด VAT เท่านั้นไหม (ใบกำกับภาษี · ใบเพิ่มหนี้ · ใบลดหนี้)</summary>
    private static bool IsVatInvoiceType(string? scannedDocumentType)
        => scannedDocumentType != null && VatInvoiceTypes.Contains(scannedDocumentType);

    /// <param name="subTotal">ยอดก่อน VAT ที่อ่านได้</param>
    /// <param name="vat">VAT ที่อ่านได้ — null = อ่านไม่ได้ (0 = อ่านได้ว่าไม่มี VAT)</param>
    /// <param name="total">ยอดรวมทั้งสิ้น</param>
    /// <param name="lineSum">ผลรวมยอดบรรทัด (ใช้เมื่อไม่มียอดหัวใบ)</param>
    /// <param name="scannedDocumentType">ชนิดกระดาษที่สแกน (<c>OcrExtractedData.DocumentType</c> / <c>OcrScanResult.DocumentType</c>)</param>
    public static Result FromScan(decimal? subTotal, decimal? vat, decimal? total, decimal? lineSum, string? scannedDocumentType)
    {
        if (total is > 0m && vat.HasValue)
            return Decide(Math.Max(0m, total.Value - vat.Value), OcrWhtBaseSource.TotalLessVat);
        if (subTotal is > 0m)
            // ยอดรวมต่ำกว่า SubTotal ⇒ มีส่วนลดหลัง SubTotal (VAT ไม่ทำให้ยอดลด) ⇒ เงินที่จ่ายจริงคือยอดรวม
            return total is > 0m && total.Value < subTotal.Value
                ? Decide(total.Value, OcrWhtBaseSource.TotalNoVat)
                : Decide(subTotal.Value, OcrWhtBaseSource.SubTotal);
        if (total is > 0m)
        {
            if (!IsVatInvoiceType(scannedDocumentType))
                return Decide(total.Value, OcrWhtBaseSource.TotalNoVat);
            var lower = Math.Round(total.Value * 100m / (100m + ThaiVatStandardRatePercent), 2, MidpointRounding.AwayFromZero);
            var verdict = !ThaiWhtRateTable.ShouldWithhold(total.Value) ? OcrWhtThresholdVerdict.Below
                : ThaiWhtRateTable.ShouldWithhold(lower) ? OcrWhtThresholdVerdict.Meets
                : OcrWhtThresholdVerdict.Unsure;
            return new Result(total.Value, OcrWhtBaseSource.TotalVatInclusive, verdict, lower);
        }
        if (lineSum is > 0m)
            return Decide(lineSum.Value, OcrWhtBaseSource.LineSum);
        return new Result(null, OcrWhtBaseSource.None, OcrWhtThresholdVerdict.Unknown, null);
    }

    /// <summary>อัตรา VAT มาตรฐาน (%) ที่ใช้คำนวณ<b>ขอบล่าง</b>ของยอดก่อน VAT เท่านั้น — ไม่ใช่ค่าที่เขียนลงเอกสาร</summary>
    private const decimal ThaiVatStandardRatePercent = 7m;

    private static Result Decide(decimal amount, OcrWhtBaseSource source)
        => new(amount, source, ThaiWhtRateTable.ShouldWithhold(amount) ? OcrWhtThresholdVerdict.Meets : OcrWhtThresholdVerdict.Below, null);

    /// <summary>ป้ายของที่มาฐาน (ภาษาไทย · ใส่ใน trace)</summary>
    public static string SourceLabel(OcrWhtBaseSource source) => source switch
    {
        OcrWhtBaseSource.TotalLessVat => "ยอดรวมหัก VAT",
        OcrWhtBaseSource.SubTotal => "ยอดก่อน VAT ที่พิมพ์ (อ่าน VAT ไม่ได้)",
        OcrWhtBaseSource.TotalNoVat => "ยอดรวมทั้งสิ้น (ไม่มีหลักฐานว่าผู้ขายเก็บ VAT)",
        OcrWhtBaseSource.TotalVatInclusive => "ยอดรวมทั้งสิ้นที่มี VAT ข้างใน (อ่าน VAT ไม่ได้)",
        OcrWhtBaseSource.LineSum => "ผลรวมบรรทัด",
        _ => "ไม่ทราบยอด",
    };

    // ─── งวดของสัญญาต่อเนื่อง ─────────────────────────────────────────────────────

    /// <summary>คำที่บอกว่าใบนี้เป็น<b>งวดหนึ่งของสัญญา/การจ่ายต่อเนื่อง</b> — CLAUDE.md #2 E: "ถ้ารวมทั้งสัญญา ≥ 1,000 ต้องหักทุกงวด"</summary>
    private static readonly string[] InstalmentPhrases =
    {
        "ประจำเดือน", "รายเดือน", "งวดที่", "ตามสัญญา", "ค่างวด", "monthly", "installment", "instalment",
    };

    /// <summary>คำว่า "งวด/ประจำเดือน/ตามสัญญา" บนข้อความ (คืนคำที่เจอ) · null = ไม่เจอ</summary>
    public static string? InstalmentWording(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lower = text.ToLowerInvariant();
        return InstalmentPhrases.FirstOrDefault(p => lower.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>ข้อความเตือนของงวดสัญญาที่ยอดงวดนี้ต่ำกว่าเกณฑ์ (ไม่เสนออัตรา — ไม่แต่งอัตราจากสัญญาที่ไม่เห็น)</summary>
    public static string InstalmentNote(string phrase, decimal amount)
        => $"ใบนี้มีคำว่า '{phrase}' = อาจเป็นงวดของสัญญาต่อเนื่อง — ถ้ายอดทั้งสัญญากับผู้รับรายนี้ ≥ "
            + $"{ThaiWhtRateTable.MinimumThresholdBaht:N0} บาท ต้องหัก ณ ที่จ่ายทุกงวด แม้งวดนี้ ({amount:N2} บาท) ต่ำกว่าเกณฑ์ "
            + "(ท.ป.4/2528 ข้อ 12) · ระบบไม่เห็นยอดทั้งสัญญาจึงไม่เติมอัตราให้ — ตรวจสัญญาแล้วตั้งอัตราเอง";
}
