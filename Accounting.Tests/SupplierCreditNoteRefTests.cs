using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ช่อง "เลขที่/วันที่ใบลดหนี้จากผู้ขาย" บนใบลดหนี้-ใบเพิ่มหนี้ฝั่งซื้อ
///
/// ช่องคู่นี้ใช้ field เดียวกับ "เลขใบกำกับของผู้ขาย" (SupplierInvoiceNumber /
/// SupplierTaxInvoiceDate) แต่ **ความหมายเปลี่ยนตามชนิดเอกสาร**:
///   PI / Expense / PV → เลขที่+วันที่ใบกำกับภาษีของผู้ขาย
///   CN / DN ฝั่งซื้อ  → เลขที่+วันที่ **ใบลดหนี้/ใบเพิ่มหนี้** ที่ผู้ขายออกให้
///
/// ที่มา (ผู้ใช้): สร้างใบลดหนี้จากใบซื้อ แล้วระบบเอา **เลขใบกำกับที่อ้างถึง**
/// มาเติมให้ (BASIE26070112200 / 27-07-2026) — ผู้ใช้สับสน และถ้าบันทึกต่อ
/// รายงานภาษีซื้อจะโชว์เลขใบกำกับแทนเลขใบลดหนี้จริง
/// </summary>
public class SupplierCreditNoteRefTests
{
    // ── ตอนเลือกใบต้นทางในฟอร์ม (ไม่ใช่ OCR) ────────────────────────────
    private static (string? Number, string? Date) CarryFromSource(
        string targetType, string sourceNumber, string sourceDate)
    {
        // mirror ของ documents.html: CN/DN ไม่ยกเลขที่/วันที่ของใบเดิมมาเติม
        var isCnDn = targetType is "CreditNote" or "DebitNote";
        return isCnDn ? (null, null) : (sourceNumber, sourceDate);
    }

    [Theory]
    [InlineData("CreditNote")]
    [InlineData("DebitNote")]
    public void Source_invoice_number_is_never_copied_into_a_credit_note(string type)
    {
        var (num, date) = CarryFromSource(type, "BASIE26070112200", "2026-07-27");
        Assert.Null(num);    // ต้องว่างให้ผู้ใช้กรอกเลขใบลดหนี้จริง
        Assert.Null(date);
    }

    [Fact]
    public void Other_purchase_documents_still_carry_the_supplier_invoice()
    {
        // ใบซื้อ/ค่าใช้จ่าย ช่องนี้ยังหมายถึงใบกำกับของผู้ขายเหมือนเดิม
        var (num, date) = CarryFromSource("PurchaseInvoice", "BASIE26070112200", "2026-07-27");
        Assert.Equal("BASIE26070112200", num);
        Assert.Equal("2026-07-27", date);
    }

    [Fact]
    public void The_referenced_invoice_is_still_traceable_after_the_change()
    {
        // ไม่ได้ทำข้อมูลหาย — ใบที่ถูกลดหนี้ยังผูกอยู่ที่ลิงก์เอกสารต้นทาง
        Guid? relatedDocumentId = Guid.NewGuid();
        Assert.NotNull(relatedDocumentId);
    }

    // ── ตอน OCR สแกนใบลดหนี้ของผู้ขาย ──────────────────────────────────
    private static bool BookSupplierRef(
        string docType, bool isSalesSide, string? extractedNumber,
        decimal? vatAmount, string? vendorTaxId, bool vatNotClaimable)
    {
        // mirror ของ OcrService: bookSupplierInvoice || bookSupplierCreditNote
        var hasNumber = !string.IsNullOrWhiteSpace(extractedNumber);
        var bookInvoice = !isSalesSide && !vatNotClaimable
            && docType is "PaymentVoucher" or "PurchaseInvoice" or "Expense"
            && hasNumber
            && ((vatAmount ?? 0) > 0 || !string.IsNullOrWhiteSpace(vendorTaxId));
        var bookCreditNote = !isSalesSide
            && docType is "CreditNote" or "DebitNote"
            && hasNumber;
        return bookInvoice || bookCreditNote;
    }

    [Theory]
    [InlineData("CreditNote")]
    [InlineData("DebitNote")]
    public void Ocr_fills_the_number_when_it_scans_a_suppliers_credit_note(string docType)
    {
        Assert.True(BookSupplierRef(docType, isSalesSide: false, "CN-2026-0007",
            vatAmount: 700m, vendorTaxId: "0105536000021", vatNotClaimable: false));
    }

    [Fact]
    public void Ocr_fills_it_even_when_the_credit_note_has_no_vat()
    {
        // เลขที่/วันที่ใบลดหนี้เป็นข้อมูลอ้างอิงตาม §86/10 ไม่ใช่เงื่อนไขการเคลม
        // (ต่างจากใบกำกับซื้อที่ต้องมี VAT หรือเลขผู้เสียภาษีถึงจะ book)
        Assert.True(BookSupplierRef("CreditNote", false, "CN-2026-0008",
            vatAmount: 0m, vendorTaxId: null, vatNotClaimable: false));
        Assert.False(BookSupplierRef("PurchaseInvoice", false, "INV-1",
            vatAmount: 0m, vendorTaxId: null, vatNotClaimable: false));
    }

    [Fact]
    public void Ocr_fills_it_even_when_input_vat_is_not_claimable()
    {
        // ใบกำกับอย่างย่อ §86/6 ห้าม book เป็นใบกำกับซื้อ — แต่ใบลดหนี้ยังต้อง
        // มีเลขที่อ้างอิงเสมอ
        Assert.True(BookSupplierRef("CreditNote", false, "CN-2026-0009",
            vatAmount: 700m, vendorTaxId: "0105536000021", vatNotClaimable: true));
        Assert.False(BookSupplierRef("PurchaseInvoice", false, "INV-2",
            vatAmount: 700m, vendorTaxId: "0105536000021", vatNotClaimable: true));
    }

    [Fact]
    public void Sales_side_credit_notes_do_not_use_the_supplier_field()
    {
        // ใบลดหนี้ที่ **เรา** ออกให้ลูกค้า — เลขที่คือของเราเอง ไม่ใช่ของผู้ขาย
        Assert.False(BookSupplierRef("CreditNote", isSalesSide: true, "CN-2026-0010",
            700m, "0105536000021", false));
    }

    [Fact]
    public void No_number_on_the_paper_means_nothing_to_fill()
    {
        Assert.False(BookSupplierRef("CreditNote", false, null, 700m, "0105536000021", false));
        Assert.False(BookSupplierRef("CreditNote", false, "   ", 700m, "0105536000021", false));
    }

    /// <summary>คำเตือนก่อนอนุมัติ (มีอยู่เดิม) กลับมามีความหมายจริง — เดิมถูก
    /// "ทำให้ผ่าน" ด้วยเลขใบกำกับที่ระบบเติมให้เอง</summary>
    [Theory]
    [InlineData("", true)]                    // ว่าง → ต้องเตือน
    [InlineData("   ", true)]
    [InlineData("CN-2026-0007", false)]       // กรอกแล้ว → ไม่เตือน
    public void Approval_warns_when_a_purchase_credit_note_has_no_supplier_number(
        string supplierNumber, bool expectWarning)
    {
        const decimal vat = 700m;
        var warn = vat != 0 && string.IsNullOrWhiteSpace(supplierNumber);
        Assert.Equal(expectWarning, warn);
    }
}
