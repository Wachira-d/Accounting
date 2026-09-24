using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ผู้ใช้รอบ 191: "ทำไมเอกสารกลับมาเป็นใบเสร็จรับเงินอย่างเดียวอีกแล้ว ทั้งที่ควรเป็นใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ"
/// — ด่าน ภ.พ.06 (รอบ 183) ลดหัวเงียบ ๆ · เทสต์ล็อกว่า (1) ใบที่ถูกลดจริงได้ข้อความบอกเหตุ + ทางไปต่อ
/// (2) ใบที่ไม่ได้ถูกลด (มีสิทธิ์ · ผู้ซื้อครบ · ไม่มี VAT · ใบเสร็จของใบกำกับ) ไม่มีข้อความ — คำเตือนที่ฟ้องใบถูกทุกใบ = ปิดด่าน
/// ใบตัวอย่างตรงกับกระดาษที่ผู้ใช้ส่งมา: ใบเสร็จค่าห้องพักแขกบุคคลธรรมดา (ชื่อ + โทร ไม่มีที่อยู่) VAT 487.38
/// </summary>
public class AbbreviatedDowngradeNoticeTests
{
    private static Contact Guest() => new() { Name = "วราภัสร์ แซ่หลี่", ContactType = ContactType.Individual };

    private static Document GuestReceipt(decimal vat = 487.38m) => new()
    {
        DocumentType = DocumentType.Receipt,
        DocumentNumber = "REC-20260919-0006",
        VatAmount = vat,
        TotalAmount = 7450m,
        Contact = Guest(),
    };

    [Fact]
    public void ใบเสร็จค่าห้องพักของแขก_บริษัทยังไม่กรอก_ภพ06_ต้องบอกเหตุและทางไปต่อ()
    {
        var notice = PdfGenerationService.AbbreviatedDowngradeNotice(
            GuestReceipt(), AbbreviatedInvoiceBlockReason.NoPhoR06Approval);
        Assert.NotNull(notice);
        Assert.Contains("ภ.พ.06", notice);
        Assert.Contains("ใบกำกับภาษีอย่างย่อ", notice);
        Assert.Contains("เต็มรูป", notice);
        // หัวกระดาษของใบเดียวกันต้องตรงกับที่ข้อความบอก — ตัวตัดสินรูปใบตัวเดียวกัน
        Assert.Equal("ใบเสร็จรับเงิน", PdfGenerationService.ComputeDocumentTitle(
            GuestReceipt(), new DocumentTemplate(), null, "th", companyMayIssueAbbreviated: false));
    }

    [Fact]
    public void ทิศตรงข้าม_บริษัทมีสิทธิ์_ไม่มีข้อความ_และหัวกลับเป็นอย่างย่อ()
    {
        Assert.Null(PdfGenerationService.AbbreviatedDowngradeNotice(
            GuestReceipt(), AbbreviatedInvoiceBlockReason.None));
        Assert.Equal("ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ", PdfGenerationService.ComputeDocumentTitle(
            GuestReceipt(), new DocumentTemplate(), null, "th", companyMayIssueAbbreviated: true));
    }

    [Fact]
    public void ทิศตรงข้าม_ผู้ซื้อข้อมูลครบ_เป็นใบกำกับเต็มรูปอยู่แล้ว_ไม่มีข้อความ()
    {
        var d = GuestReceipt();
        d.Contact!.Address = "202/24 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110";
        Assert.Null(PdfGenerationService.AbbreviatedDowngradeNotice(d, AbbreviatedInvoiceBlockReason.NoPhoR06Approval));
    }

    [Fact]
    public void ทิศตรงข้าม_ใบไม่มี_VAT_เช่นใบมัดจำ_ไม่มีข้อความ()
        => Assert.Null(PdfGenerationService.AbbreviatedDowngradeNotice(
            GuestReceipt(vat: 0m), AbbreviatedInvoiceBlockReason.NoPhoR06Approval));

    [Fact]
    public void ทิศตรงข้าม_ใบเสร็จของใบกำกับที่ออกแล้ว_ไม่มีข้อความ()
    {
        var d = GuestReceipt();
        d.SettlesTaxInvoiceSource = true;   // VAT รายงานที่ใบกำกับต้นทางแล้ว — ใบนี้เป็นใบเสร็จเปล่าโดยถูกต้อง
        Assert.Null(PdfGenerationService.AbbreviatedDowngradeNotice(d, AbbreviatedInvoiceBlockReason.NoPhoR06Approval));
    }
}
