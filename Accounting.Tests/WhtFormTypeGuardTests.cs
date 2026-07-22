using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตรวจ guard ประเภทแบบ ภ.ง.ด. หัก ณ ที่จ่าย — ประเภทแบบขึ้นกับ "ผู้ถูกหักภาษี":
/// นิติบุคคล → ภ.ง.ด.53, บุคคลธรรมดา → ภ.ง.ด.3. เคสจริง: ระบบภายนอก (มังกร) ออกใบ
/// ให้บริษัทแต่ส่ง TaxFormType=ภ.ง.ด.3 มา → ระบบต้องตรวจจับว่า payee เป็นนิติบุคคล
/// (จากเลขภาษี 13 หลักขึ้นต้น 0 / ContactType / ชื่อ "บริษัท") แล้วแก้เป็น ภ.ง.ด.53.
/// </summary>
public class WhtFormTypeGuardTests
{
    private static Contact C(string name, string? taxId = null, ContactType type = ContactType.Individual)
        => new() { Name = name, TaxId = taxId, ContactType = type };

    // ── DetectJuristic ─────────────────────────────────────────────────────

    [Fact]
    public void TaxId_starting_zero_is_juristic()
        => Assert.True(WithholdingTaxCertService.DetectJuristic(C("นาย ก", "0105556012345")));

    [Fact]
    public void TaxId_starting_one_is_individual_even_if_type_juristic()
        // เลขบัตร ปชช. ขึ้นต้น 1 = บุคคลธรรมดา — authoritative กว่า ContactType ที่ตั้งผิด
        => Assert.False(WithholdingTaxCertService.DetectJuristic(C("บริษัท ปลอม", "1103700123456", ContactType.JuristicPerson)));

    [Fact]
    public void ContactType_juristic_without_taxid_is_juristic()
        => Assert.True(WithholdingTaxCertService.DetectJuristic(C("ไม่มีเลข", null, ContactType.JuristicPerson)));

    [Fact]
    public void Name_with_borisat_keyword_is_juristic_even_when_type_default_individual()
        // เคสตรงกับบั๊ก: contact สร้างจาก integration ไม่ตั้ง type (default Individual)
        // แต่ชื่อขึ้นต้น "บริษัท" → ต้องจับเป็นนิติบุคคล
        => Assert.True(WithholdingTaxCertService.DetectJuristic(C("บริษัท ทดสอบ จำกัด")));

    [Fact]
    public void Name_with_english_coltd_is_juristic()
        => Assert.True(WithholdingTaxCertService.DetectJuristic(C("Test Co., Ltd.")));

    [Fact]
    public void Plain_individual_name_no_signal_is_individual()
        => Assert.False(WithholdingTaxCertService.DetectJuristic(C("นายสมชาย ใจดี")));

    // ── ResolveWhtFormType (การแก้จริง) ────────────────────────────────────

    [Fact]
    public void Company_requested_pnd3_gets_corrected_to_pnd53()
    {
        // ⭐ เคสที่ผู้ใช้แจ้ง: บริษัท (เลขภาษีขึ้นต้น 0) แต่ถูกออกเป็น ภ.ง.ด.3
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(
            C("บริษัท ลูกค้า จำกัด", "0105556012345"), TaxType.WithholdingTax3);
        Assert.Equal(TaxType.WithholdingTax53, form);
        Assert.True(corrected);
    }

    [Fact]
    public void Company_by_name_only_requested_pnd3_corrected()
    {
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(
            C("บริษัท ไม่มีเลขภาษี จำกัด"), TaxType.WithholdingTax3);
        Assert.Equal(TaxType.WithholdingTax53, form);
        Assert.True(corrected);
    }

    [Fact]
    public void Individual_requested_pnd53_gets_corrected_to_pnd3()
    {
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(
            C("นายสมชาย", "1103700123456"), TaxType.WithholdingTax53);
        Assert.Equal(TaxType.WithholdingTax3, form);
        Assert.True(corrected);
    }

    [Fact]
    public void Company_already_pnd53_not_flagged_corrected()
    {
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(
            C("บริษัท ถูกแล้ว จำกัด", "0105556012345"), TaxType.WithholdingTax53);
        Assert.Equal(TaxType.WithholdingTax53, form);
        Assert.False(corrected);
    }

    [Fact]
    public void No_requested_form_company_resolves_pnd53()
    {
        var (form, _, _) = WithholdingTaxCertService.ResolveWhtFormType(
            C("บริษัท ก จำกัด", "0105556012345"), null);
        Assert.Equal(TaxType.WithholdingTax53, form);
    }

    [Fact]
    public void Pnd1_employment_never_touched_regardless_of_payee()
    {
        // ภ.ง.ด.1 (เงินเดือน) ขึ้นกับประเภทเงินได้ ไม่ใช่ผู้ถูกหัก — ห้ามแก้เป็น 53
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(
            C("บริษัท ก จำกัด", "0105556012345"), TaxType.WithholdingTax1);
        Assert.Equal(TaxType.WithholdingTax1, form);
        Assert.False(corrected);
    }

    [Fact]
    public void Ambiguous_individual_requested_pnd3_stays_pnd3()
    {
        // ไม่มีสัญญาณนิติบุคคล + type Individual → คง ภ.ง.ด.3 ไม่ flag corrected
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(
            C("นายสมชาย ใจดี"), TaxType.WithholdingTax3);
        Assert.Equal(TaxType.WithholdingTax3, form);
        Assert.False(corrected);
    }
}
