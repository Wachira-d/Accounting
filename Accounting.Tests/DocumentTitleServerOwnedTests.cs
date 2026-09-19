using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// หัวเอกสาร — <c>PdfGenerationService.ComputeDocumentTitle</c> คือเจ้าของกฎตัวเดียว
///
/// ═══ ที่มา ═══
/// <c>Layout.docHeaderLabel</c> ใน layout.js เคยเป็น**สำเนามือ**ของกฎนี้ที่รู้จัก
/// แค่ 3 ธง (buyerDeclined / issuedAsCashReceipt / combined) ⇒ จอกับกระดาษไม่ตรงกัน
/// อย่างน้อย 5 เคส. แก้ด้วยการให้เซิร์ฟเวอร์ส่ง <c>DocumentTitle</c> มาใน
/// <c>DocumentResponse</c> แล้ว JS **แสดงอย่างเดียว** (กลไกเดียวกับ MENU_SECTIONS
/// และ complianceIssues — drift เป็นศูนย์โดยโครงสร้าง)
///
/// เทสต์ชุดนี้ล็อก "ความจริงฝั่งกระดาษ" ของทั้ง 5 เคสนั้นไว้ — ถ้าข้อไหนแดง
/// แปลว่าหัวที่ส่งไปให้หน้าเว็บเปลี่ยนความหมาย ต้องไล่ดู renderer ทั้งสองตัวด้วย
/// (ฝั่ง JS มี simulation คู่กันที่รัน docHeaderLabel จริงบน layout.js)
/// </summary>
public class DocumentTitleServerOwnedTests
{
    private static DocumentTemplate Tpl(DocumentType type, string? custom = null) => new()
    {
        Id = Guid.NewGuid(),
        DocumentType = type,
        IsDefault = true,
        IsActive = true,
        CustomTitle = custom,
    };

    private static Contact FullBuyer() => new()
    {
        Name = "บริษัท ผู้ซื้อ จำกัด",
        TaxId = "0105556091234",       // 13 หลัก ขึ้นต้น 0 = นิติบุคคล
        BranchCode = "00000",
        Address = "1 ถนนสุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพฯ 10110",
        ContactType = ContactType.JuristicPerson,
    };

    private static string Title(Document doc, DocumentTemplate tpl, CompanySettings? st = null,
        string lang = "th")
        => PdfGenerationService.ComputeDocumentTitle(doc, tpl, st, lang, companyMayIssueAbbreviated: true);

    [Fact]
    public void เคส1_ชื่อหัวที่ผู้ใช้ตั้งเองใน_CompanySettings_ต้องชนะหัวมาตรฐาน()
    {
        var doc = new Document { DocumentType = DocumentType.TaxInvoice, Contact = FullBuyer() };
        var st = new CompanySettings
        {
            DocumentTitleOverridesJson = """{"TaxInvoice":"ใบกำกับภาษี (ต้นฉบับ)"}""",
        };
        Assert.Equal("ใบกำกับภาษี (ต้นฉบับ)", Title(doc, Tpl(DocumentType.TaxInvoice), st));
        // กฎเดิมฝั่ง JS ไม่รู้จัก settings เลย → ได้ "ใบกำกับภาษี" (คนละใบกับกระดาษ)
        Assert.NotEqual("ใบกำกับภาษี", Title(doc, Tpl(DocumentType.TaxInvoice), st));
    }

    [Fact]
    public void เคส2_template_CustomTitle_ต้องชนะหัวมาตรฐาน()
    {
        var doc = new Document { DocumentType = DocumentType.Invoice };
        Assert.Equal("INVOICE / ใบวางบิล",
            Title(doc, Tpl(DocumentType.Invoice, "INVOICE / ใบวางบิล")));
    }

    [Fact]
    public void เคส3_ผู้ซื้อ_86_4_ไม่ครบ_ต้องเป็นใบกำกับภาษีอย่างย่อ()
    {
        // walk-in / ข้อมูลผู้ซื้อไม่ครบ + ขายมี VAT → §86/6 อย่างย่อ
        var doc = new Document
        {
            DocumentType = DocumentType.TaxInvoice,
            VatAmount = 70m,
            Contact = new Contact { Name = "ลูกค้าทั่วไป", IsWalkInCustomer = true },
        };
        Assert.Equal("ใบกำกับภาษีอย่างย่อ", Title(doc, Tpl(DocumentType.TaxInvoice)));
    }

    [Fact]
    public void เคส4_ใบเสร็จที่มี_VAT_ต้องเป็นใบกำกับภาษีสลาชใบเสร็จรับเงิน()
    {
        // ใบเสร็จรับเงินที่มี VAT และไม่ได้ settle ใบกำกับ → ใบเสร็จนี่แหละคือ
        // ใบกำกับ ณ วันรับเงิน (§78/1) — กฎเดิมฝั่ง JS คืน "ใบเสร็จรับเงิน" เปล่า
        var doc = new Document
        {
            DocumentType = DocumentType.Receipt,
            VatAmount = 70m,
            Contact = FullBuyer(),
        };
        Assert.Equal("ใบกำกับภาษี/ใบเสร็จรับเงิน", Title(doc, Tpl(DocumentType.Receipt)));
    }

    [Fact]
    public void เคส4ข_ใบเสร็จที่_settle_ใบกำกับ_ต้องเป็นใบเสร็จเปล่า()
    {
        // ห้ามมีคำว่า "ใบกำกับภาษี" บนกระดาษ 2 ใบจากการขายครั้งเดียว
        var doc = new Document
        {
            DocumentType = DocumentType.Receipt,
            VatAmount = 70m,
            Contact = FullBuyer(),
            SettlesTaxInvoiceSource = true,
        };
        Assert.Equal("ใบเสร็จรับเงิน", Title(doc, Tpl(DocumentType.Receipt)));
    }

    [Fact]
    public void เคส5_ใบมัดจำ_ต้องต่อท้ายวงเล็บเงินมัดจำ()
    {
        var doc = new Document
        {
            DocumentType = DocumentType.TaxInvoice,
            Contact = FullBuyer(),
            IsDeposit = true,
        };
        Assert.Equal("ใบกำกับภาษี (เงินมัดจำ)", Title(doc, Tpl(DocumentType.TaxInvoice)));
    }

    [Fact]
    public void สามธงที่_JS_เคยรู้จัก_ต้องได้ผลเท่าเดิมทุกตัว()
    {
        var tpl = Tpl(DocumentType.TaxInvoice);
        Assert.Equal("ใบกำกับภาษี", Title(
            new Document { DocumentType = DocumentType.TaxInvoice, Contact = FullBuyer() }, tpl));
        // ⚠️ กฎเดิมฝั่ง JS คืน "ใบเสร็จรับเงิน" เปล่า — ของจริงคือ §86/6 อย่างย่อ
        // (ผู้ขายจด VAT ต้องออกใบกำกับ*บางรูปแบบ*ทุกการขาย) นี่คือเคสที่ 6
        Assert.Equal("ใบกำกับภาษีอย่างย่อ", Title(
            new Document
            {
                DocumentType = DocumentType.TaxInvoice, VatAmount = 70m,
                BuyerDeclinedTaxInvoice = true, Contact = FullBuyer(),
            }, tpl));
        Assert.Equal("ใบเสร็จรับเงิน/ใบกำกับภาษี", Title(
            new Document
            {
                DocumentType = DocumentType.TaxInvoice, IssuedAsCashReceipt = true,
                Contact = FullBuyer(),
            }, tpl));
        Assert.Equal("ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน", Title(
            new Document
            {
                DocumentType = DocumentType.TaxInvoice, CombinedInvoiceTaxInvoice = true,
                ServedAsReceipt = true, Contact = FullBuyer(),
            }, tpl));
        Assert.Equal("ใบกำกับภาษี/ใบเสร็จรับเงิน", Title(
            new Document
            {
                DocumentType = DocumentType.TaxInvoice, ServedAsReceipt = true,
                Contact = FullBuyer(),
            }, tpl));
    }
}
