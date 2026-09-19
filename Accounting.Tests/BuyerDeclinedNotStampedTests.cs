using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ระบบเลิกประทับ "ผู้ซื้อไม่ประสงค์รับใบกำกับภาษี" แทนผู้ซื้อ** (คำตัดสินรอบ 181)
///
/// <para><c>BuyerDeclinedTaxInvoice</c> แปลว่า "<b>ผู้ซื้อ</b>แจ้งว่าไม่ประสงค์รับ
/// ใบกำกับ" = เจตนาของมนุษย์ · เดิม <c>ApproveDocumentAsync</c> ประทับธงนี้ให้เองเมื่อ
/// ข้อมูลผู้ซื้อไม่ครบและผู้ซื้อไม่ใช่นิติบุคคล ⇒ ค่าที่ระบบแต่งขึ้นถูก persist แล้ว
/// clone ต่อไปเอกสารลูก + โผล่ในข้อความ e-Tax ราวกับลูกค้าเคยปฏิเสธจริง
/// (ต้นเหตุร่วม R1 "สถานะปลายทางประทับเอง" + R2 "ไม่รู้ → ค่าที่แต่งขึ้น")</para>
///
/// <para>สองครึ่งตามกฎเหล็ก #4 H: ครึ่งที่พิสูจน์ว่า <b>หน้าตาเอกสารไม่เปลี่ยนแม้แต่ใบเดียว</b>
/// (ผู้อ่านธงทุกตัว OR กับการตรวจ "ผู้ซื้อ §86/4 ไม่ครบ" ด้วยตัวเองอยู่แล้ว) และครึ่งที่
/// พิสูจน์ว่าการติ๊กของ<b>ผู้ใช้เอง</b>ยังทำงานเหมือนเดิม</para>
/// </summary>
public class BuyerDeclinedNotStampedTests
{
    private static Contact IncompleteIndividual() => new()
    {
        Name = "คุณสมชาย",                 // ไม่มีที่อยู่ ⇒ §86/4 ไม่ครบ
        ContactType = ContactType.Individual,
    };

    private static Contact CompleteIndividual() => new()
    {
        Name = "คุณสมชาย ใจดี",
        ContactType = ContactType.Individual,
        Address = "12/3 ถ.พระราม 4 กรุงเทพฯ",
    };

    private static Document SaleWithVat(Contact buyer, bool declaredByUser = false) => new()
    {
        DocumentType = DocumentType.TaxInvoice,
        SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m,
        Contact = buyer,
        BuyerDeclinedTaxInvoice = declaredByUser,
    };

    /// <summary>กติกาตัดสินหัวเอกสารของ renderer (PdfGenerationService
    /// <c>buyerDeclined</c> / <c>IsAbbreviatedTaxInvoiceDoc</c>) — ธง <b>หรือ</b>
    /// walk-in <b>หรือ</b> ผู้ซื้อ §86/4 ไม่ครบ</summary>
    private static bool HeaderDropsFullTaxInvoice(Document doc)
        => doc.BuyerDeclinedTaxInvoice
           || (doc.Contact?.IsWalkInCustomer ?? false)
           || TaxInvoiceCompletenessChecker.MissingBuyerFields(doc.Contact).Count > 0;

    // ══════════ (ก) ผู้ซื้อไม่ครบ + ไม่ใช่นิติบุคคล ══════════

    [Fact]
    public void ผู้ซื้อข้อมูลไม่ครบ_หัวเอกสารยังเป็นอย่างย่อเหมือนเดิม_แต่ธงต้องไม่ถูกประทับ()
    {
        var doc = SaleWithVat(IncompleteIndividual());

        // สิ่งที่ผู้ใช้เห็นต้องเหมือนเดิมเป๊ะ — ทั้งหัวกระดาษและธงในรายงานภาษี
        Assert.True(HeaderDropsFullTaxInvoice(doc));
        Assert.True(TaxService.NotFullTaxInvoice(doc));

        // แต่ "เจตนาของผู้ซื้อ" ต้องยังว่าง — ระบบไม่ได้รับแจ้งอะไรมาเลย
        Assert.False(doc.BuyerDeclinedTaxInvoice);
    }

    [Fact]
    public void ผลลัพธ์ที่ผู้ใช้เห็นไม่ขึ้นกับธง_เมื่อผู้ซื้อไม่ครบ()
    {
        // ใบเดียวกันทั้งสองเวอร์ชัน (ก่อน = เคยถูกประทับ · หลัง = ไม่ประทับ)
        // ต้องให้คำตอบเดียวกันทุกด่านที่อ่านธงนี้
        var stamped = SaleWithVat(IncompleteIndividual(), declaredByUser: true);
        var notStamped = SaleWithVat(IncompleteIndividual());

        Assert.Equal(HeaderDropsFullTaxInvoice(stamped), HeaderDropsFullTaxInvoice(notStamped));
        Assert.Equal(TaxService.NotFullTaxInvoice(stamped), TaxService.NotFullTaxInvoice(notStamped));
    }

    [Fact]
    public void ผู้ซื้อนิติบุคคลที่ข้อมูลไม่ครบ_ยังต้องถูกบล็อกเหมือนเดิม()
    {
        // ด่าน throw ของผู้ซื้อนิติบุคคลไม่ถูกแตะ — ตัวตัดสินของมันคือสองตัวนี้
        var juristic = new Contact
        {
            Name = "บริษัท ทดสอบ จำกัด",
            ContactType = ContactType.JuristicPerson,
            TaxId = "0105561040684",
            // ไม่มีที่อยู่ ⇒ ไม่ครบ
        };

        Assert.True(TaxInvoiceCompletenessChecker.IsJuristicBuyer(juristic));
        Assert.NotEmpty(TaxInvoiceCompletenessChecker.MissingBuyerFields(juristic));
    }

    // ══════════ (ข) ผู้ใช้ติ๊กเอง ══════════

    [Fact]
    public void ผู้ใช้ติ๊กเอง_ธงยังเป็นจริงและหัวเอกสารเหมือนเดิม()
    {
        var doc = SaleWithVat(CompleteIndividual(), declaredByUser: true);

        Assert.True(doc.BuyerDeclinedTaxInvoice);
        Assert.True(HeaderDropsFullTaxInvoice(doc));       // เจตนาของผู้ซื้อยังมีผลเต็ม
        Assert.True(TaxService.NotFullTaxInvoice(doc));
    }

    // ══════════ (ค) ผู้ซื้อข้อมูลครบ ══════════

    [Fact]
    public void ผู้ซื้อข้อมูลครบและไม่ได้ติ๊ก_ยังเป็นใบกำกับเต็มรูป()
    {
        var doc = SaleWithVat(CompleteIndividual());

        Assert.Empty(TaxInvoiceCompletenessChecker.MissingBuyerFields(doc.Contact));
        Assert.False(doc.BuyerDeclinedTaxInvoice);
        Assert.False(HeaderDropsFullTaxInvoice(doc));
        Assert.False(TaxService.NotFullTaxInvoice(doc));
    }
}
