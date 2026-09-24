using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบสอง (รอบ 193) — สองเรื่องของ "ใบนี้ควรมี e-Tax ไหม"
///
/// <para><b>R2-C6</b>: hook e-Tax อัตโนมัติเคยข้าม<b>เงียบ</b>ทุกใบที่ <c>TaxService.NotFullTaxInvoice</c> = true ⇒ ผู้ซื้อ
/// <b>นิติบุคคล</b>ที่ข้อมูล §86/4 ไม่ครบ (เส้นเว็บบล็อกอนุมัติ แต่ API/POS ออกได้) ถูกข้ามโดยไม่มีป้าย ·
/// ตอนนี้ข้ามเงียบเฉพาะ "โดยเจตนา" (<c>NotFullTaxInvoiceByDesign</c>) ส่วนที่เหลือไปล้มดังใน <c>GenerateAsync</c></para>
///
/// <para><b>R2-C7</b>: ใบขาย 0% (§80/1) ของผู้จด VAT = ใบกำกับภาษีอัตรา 0 (กระดาษพิมพ์ "ใบกำกับภาษี") แต่ Integration
/// ตรึงธงด้วยพื้น "VAT &gt; 0" ⇒ <c>IsTaxInvoiceByLaw=false</c> ⇒ e-Tax ข้าม/ปฏิเสธ · ยกเว้น §81 (-1) ต้องยังไม่ใช่</para>
///
/// ทุกหัวข้อมีสองครึ่ง: ใบที่พังกลับมาถูก + ใบที่ถูกอยู่แล้วไม่ถูกแตะ (กฎ #4 H)
/// </summary>
public class EtaxSkipIntentAndZeroRatedTests
{
    // ═══ R2-C6 — ข้ามเงียบเฉพาะโดยเจตนา ═══

    private static Contact Juristic(string? address = "1 ถนนสุขุมวิท", string? taxId = "0105556000001") => new()
    {
        Name = "บริษัท ผู้ซื้อ จำกัด",
        Address = address,
        TaxId = taxId,
        ContactType = ContactType.JuristicPerson,
    };

    private static Contact Individual(string? address = null) => new()
    {
        Name = "สมชาย ใจดี",
        Address = address,
        TaxId = null,
        ContactType = ContactType.Individual,
    };

    [Fact]
    public void นิติบุคคลข้อมูลไม่ครบ_ไม่ใช่เจตนา_ต้องไม่ข้ามเงียบ()
    {
        var c = Juristic(address: null);
        // ยังไม่ใช่ใบกำกับเต็มรูป (ตัวออก e-Tax จะปฏิเสธพร้อมบอกช่องที่ขาด) …
        Assert.True(TaxService.NotFullTaxInvoice(107m, false, c));
        // … แต่ไม่ใช่ "โดยเจตนา" ⇒ Judge ต้องไม่คืนเหตุข้าม ⇒ hook เรียกตัวออกแล้วประทับป้ายล้ม
        Assert.False(TaxService.NotFullTaxInvoiceByDesign(107m, false, c));
        Assert.Equal(EtaxAutoSkip.None, EtaxAutoIssueScope.Judge(DocumentType.TaxInvoice, DocumentStatus.Approved,
            false, false, null, null, isTaxInvoiceByLaw: true,
            notFullTaxInvoice: TaxService.NotFullTaxInvoiceByDesign(107m, false, c)));
    }

    [Fact]
    public void นิติบุคคลเลขภาษีขาด_ไม่ใช่เจตนา()
        => Assert.False(TaxService.NotFullTaxInvoiceByDesign(107m, false,
            new Contact { Name = "บริษัท ก จำกัด", Address = "กทม.", ContactType = ContactType.JuristicPerson }));

    [Fact]
    public void ไม่มีผู้ติดต่อเลย_ไม่ใช่เจตนา_ต้องดัง()
        => Assert.False(TaxService.NotFullTaxInvoiceByDesign(107m, false, null));

    [Fact]
    public void ผู้ซื้อไม่ประสงค์รับ_ข้ามโดยเจตนา()
        => Assert.True(TaxService.NotFullTaxInvoiceByDesign(107m, true, Juristic(address: null)));

    [Fact]
    public void ลูกค้าเงินสด_walk_in_ข้ามโดยเจตนา()
    {
        var c = Individual();
        c.IsWalkInCustomer = true;
        Assert.True(TaxService.NotFullTaxInvoiceByDesign(107m, false, c));
    }

    [Fact]
    public void บุคคลธรรมดาข้อมูลไม่ครบ_ข้ามโดยเจตนา_หัวเป็นใบย่อ()
        => Assert.True(TaxService.NotFullTaxInvoiceByDesign(107m, false, Individual(address: null)));

    [Fact]
    public void ผู้ซื้อครบ_เป็นใบกำกับเต็มรูป_ไม่ข้าม()
    {
        Assert.False(TaxService.NotFullTaxInvoice(107m, false, Juristic()));
        Assert.False(TaxService.NotFullTaxInvoiceByDesign(107m, false, Juristic()));
    }

    [Fact]
    public void ไม่มี_VAT_ไม่ใช่เรื่องของเกณฑ์นี้()
        => Assert.False(TaxService.NotFullTaxInvoiceByDesign(0m, true, null));

    // ═══ R2-C7 — 0% §80/1 เป็นใบกำกับ · ยกเว้น §81 ไม่ใช่ ═══

    private static Document Sale(params decimal[] rates)
    {
        var d = new Document
        {
            DocumentType = DocumentType.TaxInvoice,
            VatAmount = 0m,
            Contact = Juristic(),
        };
        foreach (var r in rates) d.Lines.Add(new DocumentLine { VatRate = r, Amount = 1000m });
        return d;
    }

    [Fact]
    public void ขายส่งออก_0_เปอร์เซ็นต์_ของผู้จด_VAT_เป็นใบกำกับตามกฎหมาย()
    {
        var d = Sale(0m);
        Assert.True(TaxInvoiceSeriesPolicy.IsZeroRatedFullTaxInvoice(d));
        Assert.True(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, null, companyVatRegistered: true));
    }

    [Fact]
    public void ผสม_0_กับยกเว้น_ยังเป็นใบกำกับ_เพราะส่วน_0_ต้องมีใบกำกับ()
        => Assert.True(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(Sale(0m, ThaiVatTypeRule.ExemptRate), null, true));

    [Fact]
    public void ยกเว้น_81_ล้วน_ไม่ใช่ใบกำกับ()
    {
        var d = Sale(ThaiVatTypeRule.ExemptRate, ThaiVatTypeRule.ExemptRate);
        Assert.False(TaxInvoiceSeriesPolicy.IsZeroRatedFullTaxInvoice(d));
        Assert.False(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, null, companyVatRegistered: true));
    }

    [Fact]
    public void ผู้ไม่จด_VAT_ออกใบกำกับไม่ได้แม้ทุกบรรทัดเป็น_0()
        => Assert.False(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(Sale(0m), null, companyVatRegistered: false));

    [Fact]
    public void ผู้เรียกที่ไม่ส่งสถานะจด_พฤติกรรมเดิม_POS()
        => Assert.False(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(Sale(0m), null));

    [Fact]
    public void ผู้ซื้อ_walk_in_หรือไม่ประสงค์รับ_หัวเป็นใบเสร็จ_ไม่ใช่ใบกำกับ()
    {
        var walkIn = Sale(0m);
        walkIn.Contact = Individual();
        walkIn.Contact.IsWalkInCustomer = true;
        Assert.False(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(walkIn, null, true));

        var declined = Sale(0m);
        declined.BuyerDeclinedTaxInvoice = true;
        Assert.False(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(declined, null, true));
    }

    [Fact]
    public void บรรทัด_0_ที่ถูกลบไม่นับ()
    {
        var d = Sale(ThaiVatTypeRule.ExemptRate);
        d.Lines.Add(new DocumentLine { VatRate = 0m, Amount = 500m, IsDeleted = true });
        Assert.False(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, null, true));
    }

    [Fact]
    public void ไม่มีบรรทัด_ไม่นับเป็น_0_เปอร์เซ็นต์()
        => Assert.False(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(Sale(), null, true));

    [Fact]
    public void ใบที่มี_VAT_ยังเป็นใบกำกับตามพื้นเดิม_ไม่ขึ้นกับสถานะจดที่ส่ง()
    {
        var d = Sale(7m);
        d.VatAmount = 70m;
        Assert.True(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, null));
        Assert.True(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, null, companyVatRegistered: true));
    }

    [Fact]
    public void ชนิดอื่น_0_เปอร์เซ็นต์_ไม่ถูกดึงเข้าพื้น()
    {
        var d = Sale(0m);
        d.DocumentType = DocumentType.Receipt;
        Assert.False(TaxInvoiceSeriesPolicy.IsZeroRatedFullTaxInvoice(d));
        Assert.False(TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole(d, null, true));
    }
}
