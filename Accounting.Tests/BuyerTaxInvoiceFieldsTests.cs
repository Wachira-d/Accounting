using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// §86/4(3) — "ใบกำกับภาษีเต็มรูปต้องมีข้อมูลผู้ซื้ออะไรบ้าง"
///
/// กฎหมายบังคับแค่ **ชื่อ + ที่อยู่** ของผู้ซื้อ ส่วนเลขประจำตัวผู้เสียภาษี
/// และเครื่องหมายสาขาบังคับ **เฉพาะผู้ซื้อที่เป็นผู้ประกอบการจดทะเบียน**
/// (ประกาศอธิบดีฯ VAT ฉบับที่ 194 และ 199 พ.ศ. 2556)
///
/// ทำไมต้องมีเทสต์: เดิมระบบบังคับเลขภาษี 13 หลักจากผู้ซื้อ *ทุกราย* → ขายให้
/// บุคคลธรรมดาที่ให้ชื่อ+ที่อยู่ครบก็ถูกตัดสินว่า "ไม่ครบ" แล้ว downgrade หัว
/// เอกสารเป็น "ใบเสร็จรับเงิน" ทั้งที่ออกใบกำกับเต็มรูปได้ ลูกค้าที่ขอใบกำกับ
/// จึงไม่ได้ใบ (ขัด §86) และเกิดสภาพ "ใบเสร็จโผล่ในรายงานภาษีขาย"
///
/// เกณฑ์นี้ถูกใช้ 2 ที่ (gate ตอนอนุมัติ + การตัดสินหัวเอกสารใน PDF) —
/// เทสต์ล็อกไว้ที่ตัวกลาง เพื่อไม่ให้สองที่ drift กันอีก
/// </summary>
public class BuyerTaxInvoiceFieldsTests
{
    private static Contact Individual(string? taxId = null, string? address = "1 ถ.สุขุมวิท กทม.")
        => new() { Name = "คุณอุมาวรรณ จตุวงษ์วิวัฒน์", ContactType = ContactType.Individual,
                   TaxId = taxId, Address = address };

    private static Contact Juristic(string? taxId = "0105561040684", string? branch = "00000",
        string? address = "99 อาคารเอ กทม.")
        => new() { Name = "บริษัท พาวเวอร์วอลล์ (ประเทศไทย) จำกัด",
                   ContactType = ContactType.JuristicPerson, TaxId = taxId,
                   BranchCode = branch, Address = address };

    // ── บุคคลธรรมดา: ชื่อ + ที่อยู่ พอแล้ว ──────────────────────────

    [Fact]
    public void Individual_with_name_and_address_can_receive_a_full_tax_invoice()
    {
        Assert.Empty(TaxInvoiceCompletenessChecker.MissingBuyerFields(Individual()));
    }

    [Fact]
    public void Individual_does_not_need_a_tax_id()
    {
        // เคสจริงจากรายงานภาษีขาย: ลูกค้าบุคคลธรรมดาไม่มีเลขผู้เสียภาษีในระบบ
        var missing = TaxInvoiceCompletenessChecker.MissingBuyerFields(Individual(taxId: null));
        Assert.DoesNotContain(missing, m => m.Contains("เลขผู้เสียภาษี"));
    }

    [Fact]
    public void Individual_does_not_need_a_branch_code()
    {
        // บุคคลธรรมดาไม่มี "สาขา" ตามแนวคิดของประกาศอธิบดีฯ 199
        var missing = TaxInvoiceCompletenessChecker.MissingBuyerFields(Individual());
        Assert.DoesNotContain(missing, m => m.Contains("สาขา"));
    }

    [Fact]
    public void Missing_address_still_blocks_even_for_an_individual()
    {
        var missing = TaxInvoiceCompletenessChecker.MissingBuyerFields(Individual(address: " "));
        Assert.Contains(missing, m => m.Contains("ที่อยู่"));
    }

    // ── นิติบุคคล: ต้องครบทั้งเลขภาษีและสาขา ────────────────────────

    [Fact]
    public void Juristic_buyer_with_everything_is_complete()
    {
        Assert.Empty(TaxInvoiceCompletenessChecker.MissingBuyerFields(Juristic()));
    }

    [Fact]
    public void Juristic_buyer_without_tax_id_is_incomplete()
    {
        var missing = TaxInvoiceCompletenessChecker.MissingBuyerFields(Juristic(taxId: null));
        Assert.Contains(missing, m => m.Contains("เลขผู้เสียภาษี"));
    }

    [Fact]
    public void Juristic_buyer_with_malformed_branch_code_is_incomplete()
    {
        // กติกาปัจจุบัน (TaxInvoiceCompletenessChecker.MissingBuyerFields · ดูคอมเมนต์ในโค้ด): สาขา **ว่าง** = "00000
        // สำนักงานใหญ่" ซึ่ง renderer พิมพ์ "(สำนักงานใหญ่)" ให้อยู่แล้ว ⇒ ไม่ขาด · ขาดเมื่อ**กรอกมาผิดรูป** (ไม่ใช่ 5 หลัก)
        // เดิมเทสต์นี้คาดว่าสาขาว่าง = ขาด ซึ่งค้างจากกติกาก่อนหน้าประวัติเรพ (≤350a394d) — โค้ดกับระบบทั้งชุด
        // (ContactTaxBranchKey · FormatBranch · SupplierBranchCode ?? "00000") ถือสาขาว่าง ≡ สนญ.
        Assert.Contains(TaxInvoiceCompletenessChecker.MissingBuyerFields(Juristic(branch: "12")), m => m.Contains("สาขา"));
        Assert.Contains(TaxInvoiceCompletenessChecker.MissingBuyerFields(Juristic(branch: "123456")), m => m.Contains("สาขา"));
        // ทิศตรงข้าม: สาขาว่าง/00000/00001 ไม่ขาด
        Assert.DoesNotContain(TaxInvoiceCompletenessChecker.MissingBuyerFields(Juristic(branch: null)), m => m.Contains("สาขา"));
        Assert.DoesNotContain(TaxInvoiceCompletenessChecker.MissingBuyerFields(Juristic(branch: "00001")), m => m.Contains("สาขา"));
    }

    /// <summary>ผู้ติดต่อที่ยังตั้งประเภทเป็น Individual แต่เลขภาษีขึ้นต้น 0
    /// = เลขทะเบียนนิติบุคคล → ต้องถือเป็นนิติบุคคล ไม่งั้นจะปล่อยใบกำกับที่
    /// ขาดรหัสสาขาออกไปให้ผู้ซื้อที่จด VAT (เคลมภาษีซื้อไม่ได้)</summary>
    [Fact]
    public void Tax_id_starting_with_zero_is_treated_as_juristic_even_if_type_says_otherwise()
    {
        var c = new Contact { Name = "ห้างหุ้นส่วน ก", ContactType = ContactType.Individual,
                              TaxId = "0105561040684", Address = "1 ถ.A", BranchCode = "12" };
        Assert.True(TaxInvoiceCompletenessChecker.IsJuristicBuyer(c));
        // ถูกถือเป็นนิติบุคคล ⇒ เดินด่านสาขา (สาขาผิดรูปถูกจับ — สาขาว่าง ≡ สนญ. ไม่ขาด ดูเทสต์ข้างบน)
        Assert.Contains(TaxInvoiceCompletenessChecker.MissingBuyerFields(c), m => m.Contains("สาขา"));
        // ทิศตรงข้าม: บุคคลธรรมดาจริง (เลขไม่ขึ้นต้น 0) สาขาผิดรูปไม่ถูกบังคับ
        var person = new Contact { Name = "นาย ก", ContactType = ContactType.Individual,
                                   TaxId = "1101700230708", Address = "1 ถ.A", BranchCode = "12" };
        Assert.False(TaxInvoiceCompletenessChecker.IsJuristicBuyer(person));
        Assert.DoesNotContain(TaxInvoiceCompletenessChecker.MissingBuyerFields(person), m => m.Contains("สาขา"));
    }

    [Fact]
    public void Null_buyer_is_incomplete()
    {
        Assert.NotEmpty(TaxInvoiceCompletenessChecker.MissingBuyerFields(null));
    }
}
