using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ส่งออก Excel ให้นักบัญชีของลูกค้า (รูปแบบทั่วไป)** — รอบ 255 WP-12B ข้อ 8 · AUTOPILOT_BILLING_DESIGN §8.1 · pure (ผู้ประกอบแถวตัวเดียว · ผู้เขียนไฟล์ = MiniExcel ที่ <c>PortalService</c>)
/// <para>คอลัมน์ทั่วไปที่นักบัญชีนำไปลงรายงานภาษีซื้อ/เจ้าหนี้ได้เอง: วันที่ (ค.ศ. ISO — ไม่ใช่แบบยื่นทางการ) · เลขที่ · ชนิด · ผู้ขาย (ชื่อ · เลขภาษี · สาขา) ·
/// มูลค่าก่อนภาษี · VAT · ยอดรวม · ภาษีหัก ณ ที่จ่าย · สถานะ · รูปแบบนำเข้าเฉพาะโปรแกรม (PEAK/FlowAccount/Express) <b>ไม่เดา</b> — รอไฟล์ตัวอย่างจริง (Q9 · DECISIONS 255-WP12B-9)</para>
/// <para><b>รอบ 257 ชุด 3 (D-M4)</b>: ใบแจ้งหนี้/ใบกำกับ/ใบเสร็จของการขายเดียวกันอยู่คนละแถวพร้อม VAT เต็มทุกแถว ⇒ นักบัญชีอาจลงภาษีซื้อซ้ำ ⇒ เพิ่ม
/// (1) คอลัมน์ "อ้างอิงเอกสาร" (เลขที่ใบต้นทาง + อ้างอิงบนใบ) (2) คอลัมน์ "ใบกำกับภาษี (ใช้ลงภาษีซื้อ)" จากตัวตัดสิน <see cref="TaxInvoiceRole"/> ·
/// (3) สาขาผู้ขาย = สาขาที่ออก<b>ใบนั้น</b> (ผู้เรียกส่งผล <c>DocumentIssuerBranch.ResolveCode</c> ต่อแถว — เดิมใช้สาขาของบริษัททุกแถว)</para>
/// </summary>
public static class PortalAccountantExport
{
    /// <summary>ชนิดเอกสารที่ส่งออก (ฝั่งขายที่ลูกค้าได้รับ)</summary>
    public static readonly IReadOnlyList<DocumentType> Types = new[]
    {
        DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.DebitNote, DocumentType.CreditNote,
    };

    /// <param name="SellerBranch">สาขาที่ออกใบนี้ (ผล <c>DocumentIssuerBranch.ResolveCode</c>) · null = ใช้ค่าตั้งต้นที่ส่งให้ <see cref="Build"/></param>
    /// <param name="Reference">อ้างอิงเอกสาร (เลขที่ใบต้นทาง · อ้างอิงบนใบ) — ผู้เรียกประกอบด้วย <see cref="ReferenceText"/></param>
    /// <param name="IsTaxInvoiceByLaw"><c>Document.IsTaxInvoiceByLaw</c> ที่ตรึงตอนออก (null = ไม่ได้ตรึง/ใบเก่า)</param>
    public sealed record Row(DateTime Date, string Number, DocumentType Type, decimal SubTotal, decimal Vat, decimal Total, decimal Wht, bool Voided,
        string? SellerBranch = null, string? Reference = null, bool? IsTaxInvoiceByLaw = null);

    public static readonly IReadOnlyList<string> Columns = new[]
    {
        "วันที่", "เลขที่เอกสาร", "ชนิดเอกสาร", "อ้างอิงเอกสาร", "ใบกำกับภาษี (ใช้ลงภาษีซื้อ)", "ผู้ขาย", "เลขประจำตัวผู้เสียภาษีผู้ขาย", "สาขาผู้ขาย",
        "มูลค่าก่อนภาษี", "ภาษีมูลค่าเพิ่ม", "ยอดรวม", "ภาษีหัก ณ ที่จ่าย", "สถานะ",
    };

    public const string RoleTaxInvoice = "ใช่";
    public const string RoleNotTaxInvoice = "ไม่ใช่ — ห้ามลงภาษีซื้อจากแถวนี้";
    public const string RoleAdjustment = "ใบเพิ่มหนี้/ใบลดหนี้ (ปรับภาษีซื้อ)";
    public const string RoleUnknown = "ไม่ทราบ — ตรวจหัวกระดาษ";

    /// <summary>แถวนี้ใช้ลงภาษีซื้อได้ไหม (ตัวตัดสินเดียวของไฟล์) — ใบแจ้งหนี้ = ไม่ใช่ · ใบกำกับ/ใบเสร็จที่ตรึงบทบาทไว้ ⇒ ตามที่ตรึง ·
    /// ใบกำกับที่ไม่ได้ตรึงแต่มี VAT = ใช่ (ใบกำกับขายเชื่อที่มี VAT เป็นใบกำกับเสมอ) · ใบเสร็จที่ไม่ได้ตรึง = ไม่ทราบ (ห้ามเดา — F2 ข้อ 3) ·
    /// ใบเพิ่ม/ลดหนี้ = ปรับภาษีซื้อ (§86/9-10) · ไม่มี VAT = ไม่ใช่</summary>
    internal static string TaxInvoiceRole(DocumentType type, bool? isTaxInvoiceByLaw, decimal vat)
    {
        if (type is DocumentType.DebitNote or DocumentType.CreditNote) return vat != 0m ? RoleAdjustment : RoleNotTaxInvoice;
        if (vat <= 0m) return RoleNotTaxInvoice;
        // ฝ่ายค้าน LOW (รอบ 257 ชุด 3): "ใบนี้เป็นใบกำกับไหม" ตัวตัดสินเดียวกับเส้นคู่ค้า (PartnerExchangeRules.IsTaxInvoiceCarrier)
        if (PartnerExchangeRules.IsTaxInvoiceCarrier(type, isTaxInvoiceByLaw, vat)) return RoleTaxInvoice;
        return type == DocumentType.Receipt && isTaxInvoiceByLaw == null ? RoleUnknown : RoleNotTaxInvoice;
    }

    /// <summary>ข้อความ "อ้างอิงเอกสาร" — เลขที่ใบต้นทาง (ใบที่แปลงมา/ที่รับชำระ) ก่อน แล้วอ้างอิงบนใบ · ว่างทั้งคู่ = ""</summary>
    public static string ReferenceText(string? sourceNumber, string? reference)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(sourceNumber) && !sourceNumber.StartsWith("DRAFT-", StringComparison.Ordinal)) parts.Add("ต้นทาง " + sourceNumber.Trim());
        if (!string.IsNullOrWhiteSpace(reference) && reference.Trim() != sourceNumber?.Trim()) parts.Add(reference.Trim());
        return string.Join(" · ", parts);
    }

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string BranchLabel(string? code)
        => string.IsNullOrWhiteSpace(code) || code == TaxBranchCode.HeadOffice ? "สำนักงานใหญ่" : $"สาขาที่ {code}";

    /// <summary>แถวของไฟล์ (ลำดับคอลัมน์ตาม <see cref="Columns"/>) · สาขา 00000 = "สำนักงานใหญ่" · เอกสารที่ยกเลิกคงอยู่พร้อมสถานะ (นักบัญชีต้องเห็นว่ายกเลิก) ·
    /// <paramref name="sellerBranch"/> = ค่าตั้งต้นของแถวที่ไม่ได้ระบุสาขาเอง</summary>
    public static List<Dictionary<string, object?>> Build(IEnumerable<Row> rows, string sellerName, string? sellerTaxId, string? sellerBranch)
        => rows.Select(r => new Dictionary<string, object?>
        {
            [Columns[0]] = r.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            [Columns[1]] = r.Number,
            [Columns[2]] = DocumentTypeNames.Title(r.Type, null),
            [Columns[3]] = r.Reference ?? "",
            [Columns[4]] = TaxInvoiceRole(r.Type, r.IsTaxInvoiceByLaw, r.Vat),
            [Columns[5]] = sellerName,
            [Columns[6]] = sellerTaxId,
            [Columns[7]] = BranchLabel(r.SellerBranch ?? sellerBranch),
            [Columns[8]] = R(r.SubTotal),
            [Columns[9]] = R(r.Vat),
            [Columns[10]] = R(r.Total),
            [Columns[11]] = R(r.Wht),
            [Columns[12]] = r.Voided ? "ยกเลิก" : "ปกติ",
        }).ToList();
}
