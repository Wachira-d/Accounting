using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ส่งออก Excel ให้นักบัญชีของลูกค้า (รูปแบบทั่วไป)** — รอบ 255 WP-12B ข้อ 8 · AUTOPILOT_BILLING_DESIGN §8.1 · pure (ผู้ประกอบแถวตัวเดียว · ผู้เขียนไฟล์ = MiniExcel ที่ <c>PortalService</c>)
/// <para>คอลัมน์ทั่วไปที่นักบัญชีนำไปลงรายงานภาษีซื้อ/เจ้าหนี้ได้เอง: วันที่ (ค.ศ. ISO — ไม่ใช่แบบยื่นทางการ) · เลขที่ · ชนิด · ผู้ขาย (ชื่อ · เลขภาษี · สาขา) ·
/// มูลค่าก่อนภาษี · VAT · ยอดรวม · ภาษีหัก ณ ที่จ่าย · สถานะ · รูปแบบนำเข้าเฉพาะโปรแกรม (PEAK/FlowAccount/Express) <b>ไม่เดา</b> — รอไฟล์ตัวอย่างจริง (Q9 · DECISIONS 255-WP12B-9)</para>
/// </summary>
public static class PortalAccountantExport
{
    /// <summary>ชนิดเอกสารที่ส่งออก (ฝั่งขายที่ลูกค้าได้รับ)</summary>
    public static readonly IReadOnlyList<DocumentType> Types = new[]
    {
        DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.DebitNote, DocumentType.CreditNote,
    };

    public sealed record Row(DateTime Date, string Number, DocumentType Type, decimal SubTotal, decimal Vat, decimal Total, decimal Wht, bool Voided);

    public static readonly IReadOnlyList<string> Columns = new[]
    {
        "วันที่", "เลขที่เอกสาร", "ชนิดเอกสาร", "ผู้ขาย", "เลขประจำตัวผู้เสียภาษีผู้ขาย", "สาขาผู้ขาย",
        "มูลค่าก่อนภาษี", "ภาษีมูลค่าเพิ่ม", "ยอดรวม", "ภาษีหัก ณ ที่จ่าย", "สถานะ",
    };

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>แถวของไฟล์ (ลำดับคอลัมน์ตาม <see cref="Columns"/>) · สาขา 00000 = "สำนักงานใหญ่" · เอกสารที่ยกเลิกคงอยู่พร้อมสถานะ (นักบัญชีต้องเห็นว่ายกเลิก)</summary>
    public static List<Dictionary<string, object?>> Build(IEnumerable<Row> rows, string sellerName, string? sellerTaxId, string? sellerBranch)
    {
        var branch = string.IsNullOrWhiteSpace(sellerBranch) || sellerBranch == TaxBranchCode.HeadOffice ? "สำนักงานใหญ่" : $"สาขาที่ {sellerBranch}";
        return rows.Select(r => new Dictionary<string, object?>
        {
            [Columns[0]] = r.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            [Columns[1]] = r.Number,
            [Columns[2]] = DocumentTypeNames.Title(r.Type, null),
            [Columns[3]] = sellerName,
            [Columns[4]] = sellerTaxId,
            [Columns[5]] = branch,
            [Columns[6]] = R(r.SubTotal),
            [Columns[7]] = R(r.Vat),
            [Columns[8]] = R(r.Total),
            [Columns[9]] = R(r.Wht),
            [Columns[10]] = r.Voided ? "ยกเลิก" : "ปกติ",
        }).ToList();
    }
}
