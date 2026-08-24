using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// หัวเอกสารฝั่งขายเมื่อขายมี VAT แต่ผู้ซื้อไม่รับใบกำกับ/ข้อมูล §86/4 ไม่ครบ
/// — ต้องเป็น "ใบกำกับภาษีอย่างย่อ" (§86/6) ไม่ใช่ตัดคำใบกำกับทิ้งเหลือใบเสร็จ
/// เปล่าแบบเดิม (ผู้จด VAT ต้องออกใบกำกับบางรูปแบบทุกการขาย §86)
///
/// <para>เรียก <see cref="PdfGenerationService.ComputeDocumentTitle"/> ตัวจริง —
/// ตัวเดียวที่ทั้ง HTML renderer และ QuestPDF ใช้ จึงคุมทั้งสองทางพร้อมกัน</para>
/// </summary>
public class AbbreviatedTaxInvoiceTitleTests
{
    private static Contact CompleteIndividual() => new()
    {
        Name = "คุณสมชาย ใจดี",
        ContactType = ContactType.Individual,
        Address = "1 ถ.สุขุมวิท กรุงเทพฯ 10110",
    };

    private static Contact WalkIn() => new()
    {
        Name = "ลูกค้าเงินสด",
        IsWalkInCustomer = true,
    };

    private static Document Doc(DocumentType type, decimal vat = 7m,
        Contact? contact = null, bool declined = false)
        => new()
        {
            DocumentType = type,
            DocumentNumber = "T-0001",
            VatAmount = vat,
            TotalAmount = 107m,
            Contact = contact ?? CompleteIndividual(),
            BuyerDeclinedTaxInvoice = declined,
        };

    private static string Title(Document d, string lang = "th")
        => PdfGenerationService.ComputeDocumentTitle(d, new DocumentTemplate(), null, lang);

    // ── เคสหลักตามคำขอผู้ใช้ ─────────────────────────────────────────

    [Fact]
    public void Receipt_with_vat_and_incomplete_buyer_becomes_abbreviated()
    {
        // ข้อมูลผู้ซื้อไม่ครบ (walk-in) + ขายมี VAT → ใบเสร็จ/ใบกำกับอย่างย่อ
        var t = Title(Doc(DocumentType.Receipt, contact: WalkIn()));
        Assert.Equal("ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ", t);
    }

    [Fact]
    public void Receipt_with_vat_and_declined_buyer_becomes_abbreviated()
    {
        var t = Title(Doc(DocumentType.Receipt, declined: true));
        Assert.Equal("ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ", t);
    }

    [Fact]
    public void Receipt_with_vat_and_complete_buyer_keeps_the_full_combined_header()
    {
        // ครบ §86/4 → แบบเดิม: ใบกำกับภาษี/ใบเสร็จรับเงิน (เต็มรูป)
        var t = Title(Doc(DocumentType.Receipt));
        Assert.Equal("ใบกำกับภาษี/ใบเสร็จรับเงิน", t);
    }

    [Fact]
    public void Receipt_without_vat_stays_a_plain_receipt()
    {
        // ไม่มี VAT = ไม่มีใบกำกับให้พูดถึง — หัวเดิมทุกกรณี
        Assert.Equal("ใบเสร็จรับเงิน", Title(Doc(DocumentType.Receipt, vat: 0m, contact: WalkIn())));
        Assert.Equal("ใบเสร็จรับเงิน", Title(Doc(DocumentType.Receipt, vat: 0m)));
    }

    [Fact]
    public void Tax_invoice_credit_sale_with_declined_buyer_becomes_abbreviated_alone()
    {
        // ขายเชื่อ (ยังไม่รับเงิน) — ห้ามมีคำ "ใบเสร็จรับเงิน" (ม.105) จึงเป็น
        // อย่างย่อเดี่ยว ไม่ใช่หัวคู่
        var t = Title(Doc(DocumentType.TaxInvoice, declined: true));
        Assert.Equal("ใบกำกับภาษีอย่างย่อ", t);
    }

    [Fact]
    public void Cash_tax_invoice_with_declined_buyer_gets_the_paired_header()
    {
        var d = Doc(DocumentType.TaxInvoice, declined: true);
        d.IssuedAsCashReceipt = true;
        Assert.Equal("ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ", Title(d));
    }

    // ── ข้อยกเว้นที่ต้องคงเดิม ───────────────────────────────────────

    [Fact]
    public void Settlement_receipt_of_a_tax_invoice_stays_a_plain_receipt()
    {
        // VAT รายงานที่ใบกำกับต้นทางแล้ว — ใบเสร็จตามหลังห้ามมีคำใบกำกับซ้ำ
        // (กันกระดาษที่เคลมได้ 2 ใบจากการขายเดียว)
        var d = Doc(DocumentType.Receipt, contact: WalkIn());
        d.SettlesTaxInvoiceSource = true;
        Assert.Equal("ใบเสร็จรับเงิน", Title(d));
    }

    [Fact]
    public void Deferred_vat_deposit_is_not_an_abbreviated_tax_invoice()
    {
        // มัดจำ VAT พักรอ — ยังไม่ใช่ใบกำกับใด ๆ ทั้งสิ้น
        var d = Doc(DocumentType.Receipt, contact: WalkIn());
        d.IsDeposit = true;
        d.DepositOutputVatDeferred = true;
        Assert.False(PdfGenerationService.IsAbbreviatedTaxInvoiceDoc(d));
    }

    [Fact]
    public void Custom_template_title_still_wins()
    {
        // ผู้ใช้ตั้งหัวเองในเทมเพลต — resolver ห้ามทับ (พฤติกรรมเดิม)
        var tpl = new DocumentTemplate { CustomTitle = "บิลเงินสดร้านเรา" };
        var t = PdfGenerationService.ComputeDocumentTitle(
            Doc(DocumentType.Receipt, contact: WalkIn()), tpl, null, "th");
        Assert.Equal("บิลเงินสดร้านเรา", t);
    }

    // ── โหมดอังกฤษ + ตัวตัดสินกลาง ──────────────────────────────────

    [Fact]
    public void English_mode_keeps_the_thai_legal_wording()
    {
        // §86/4 บังคับคำไทยบนหัวเอกสารภาษี — โหมด en พิมพ์สองภาษา
        var t = Title(Doc(DocumentType.Receipt, contact: WalkIn()), "en");
        Assert.Contains("Abbreviated Tax Invoice", t);
        Assert.Contains("ใบกำกับภาษีอย่างย่อ", t);
    }

    [Fact]
    public void The_shared_resolver_agrees_with_the_title()
    {
        // IsAbbreviatedTaxInvoiceDoc คือตัวที่ renderer ใช้พิมพ์ข้อความ §86/6(6)
        // ("ยอดรวมทั้งสิ้นได้รวมภาษีมูลค่าเพิ่มแล้ว") — ต้องชี้ใบเดียวกับหัวเสมอ
        var abbreviated = Doc(DocumentType.Receipt, contact: WalkIn());
        var full = Doc(DocumentType.Receipt);
        Assert.True(PdfGenerationService.IsAbbreviatedTaxInvoiceDoc(abbreviated));
        Assert.False(PdfGenerationService.IsAbbreviatedTaxInvoiceDoc(full));
        Assert.Contains("อย่างย่อ", Title(abbreviated));
        Assert.DoesNotContain("อย่างย่อ", Title(full));
    }
}
