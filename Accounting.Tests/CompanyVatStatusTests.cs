using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// S-01 (รอบ 193) — สถานะจด VAT เก็บสองธงที่ค่าเริ่มต้นตรงข้ามกัน:
/// <c>Company.IsVatRegistered</c> (false) กับ <c>CompanySettings.VatRegistered</c> (true)
///
/// <para>บั๊กจริง: สร้างบริษัทโดยไม่ติ๊ก "จด VAT" → หน้าตั้งค่าสร้างแถวใหม่แบบ lazy ด้วยค่า default "จด" →
/// กดบันทึก → ธงบริษัทพลิกเป็น true · และก่อนมีแถวค่าตั้ง ผู้อ่านฝั่งค่าตั้งทุกตัวเขียน <c>?? true</c> ⇒ ด่าน §90/2 ไม่ทำงาน</para>
///
/// <para>เทสต์สองครึ่ง: (1) ไม่จด VAT แล้วสร้างแถวค่าตั้ง/อ่านสถานะ ได้ "ไม่จด" (2) จด VAT แล้วได้ "จด" เหมือนเดิม
/// + ลำดับเมื่อมีทั้งสองธงยังเป็นแบบเดิม (ค่าตั้งก่อน — รอคำตัดสินเจ้าของ Q1)</para>
/// </summary>
public class CompanyVatStatusTests
{
    // ── ตัวสร้างแถวค่าตั้ง (ทุกทาง: สร้างบริษัท · สมัคร · SSO · lazy 6 ที่ เรียกตัวนี้ตัวเดียว) ──

    [Fact]
    public void บริษัทไม่จด_VAT_แถวค่าตั้งใหม่ต้องไม่ติ๊กจด()
    {
        var company = new Company { Name = "ร้านไม่จด", TaxId = "-", IsVatRegistered = false, VatRate = 7m };
        var s = CompanySettingsFactory.NewFor(company);
        Assert.False(s.VatRegistered);          // เดิม = true จากค่า default ของ entity
        Assert.Equal(company.Id, s.CompanyId);
    }

    [Fact]
    public void บริษัทที่สร้างด้วยค่าเริ่มต้นของ_Company_เช่นเส้นสมัครสมาชิก_ได้ไม่จด()
    {
        // AuthService/SSO สร้างบริษัทแค่ Name + TaxId "-" — ค่าเริ่มต้นของ Company = ไม่จด
        var company = new Company { Name = "สมัครใหม่", TaxId = "-" };
        Assert.False(CompanySettingsFactory.NewFor(company).VatRegistered);
    }

    [Fact]
    public void บริษัทจด_VAT_แถวค่าตั้งใหม่ยังติ๊กจดเหมือนเดิม()
    {
        var company = new Company { Name = "บจก. จด VAT", TaxId = "0105556000001", IsVatRegistered = true, VatRate = 7m };
        var s = CompanySettingsFactory.NewFor(company);
        Assert.True(s.VatRegistered);
        Assert.Equal(7m, s.DefaultVatRate);
    }

    [Fact]
    public void อัตราตั้งต้นของแถวใหม่มาจากบริษัท_ไม่ใช่เลข_7_ของ_entity()
    {
        var company = new Company { Name = "x", TaxId = "-", IsVatRegistered = true, VatRate = 10m };
        Assert.Equal(10m, CompanySettingsFactory.NewFor(company).DefaultVatRate);
    }

    [Fact]
    public void ค่าอื่นของแถวใหม่ยังเป็นค่า_default_ของ_entity()
    {
        // ตัวสร้างแตะแค่ธง/อัตรา VAT — ค่าอื่นต้องเหมือน new CompanySettings() เดิมทุกช่องที่ใช้บ่อย
        var fresh = new CompanySettings();
        var s = CompanySettingsFactory.NewFor(new Company { Name = "x", TaxId = "-" });
        Assert.Equal(fresh.EtaxEnabled, s.EtaxEnabled);
        Assert.Equal(fresh.IsVehicleDealer, s.IsVehicleDealer);
        Assert.Equal(fresh.WhtRecognitionBasis, s.WhtRecognitionBasis);
        Assert.Equal(fresh.EnforceFullTaxInvoiceFields, s.EnforceFullTaxInvoiceFields);
    }

    // ── ตัวอ่านสถานะ (ผู้อ่านฝั่งค่าตั้งทุกตัว — DocumentService 5 จุด · IntegrationService) ──

    [Fact]
    public void ไม่มีแถวค่าตั้ง_ใช้ธงบริษัท_ไม่ใช่ถือว่าจด()
    {
        Assert.False(CompanyVatStatus.IsRegistered(companyIsVatRegistered: false, settingsVatRegistered: null));
        Assert.True(CompanyVatStatus.IsRegistered(companyIsVatRegistered: true, settingsVatRegistered: null));
    }

    [Theory]
    [InlineData(false, true, true)]    // ขัดกัน — ลำดับเดิม: ค่าตั้งชนะ (รอ Q1 · ห้ามสลับเอง)
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void มีแถวค่าตั้ง_ลำดับเดิมไม่เปลี่ยน(bool companyFlag, bool settingsFlag, bool expected)
        => Assert.Equal(expected, CompanyVatStatus.IsRegistered(companyFlag, settingsFlag));

    [Fact]
    public void อัตราตั้งต้น_ค่าตั้งก่อน_ไม่มีแถวใช้ของบริษัท()
    {
        Assert.Equal(7m, CompanyVatStatus.DefaultRate(companyVatRate: 7m, settingsDefaultVatRate: null));
        Assert.Equal(0m, CompanyVatStatus.DefaultRate(companyVatRate: 7m, settingsDefaultVatRate: 0m));
    }

    [Theory]
    [InlineData(false, null, VatFlagAgreement.NoSettingsRow)]
    [InlineData(true, true, VatFlagAgreement.Agree)]
    [InlineData(false, false, VatFlagAgreement.Agree)]
    [InlineData(false, true, VatFlagAgreement.CompanyNoSettingsYes)]   // ทิศผิดกฎหมาย §90/2
    [InlineData(true, false, VatFlagAgreement.CompanyYesSettingsNo)]
    public void รายงานธงขัดกัน_จำแนกทิศถูก(bool companyFlag, bool? settingsFlag, VatFlagAgreement expected)
        => Assert.Equal(expected, CompanyVatStatus.Compare(companyFlag, settingsFlag));

    [Fact]
    public void แถวใหม่จากตัวสร้างไม่ขัดกับบริษัทเสมอ()
    {
        foreach (var registered in new[] { true, false })
        {
            var c = new Company { Name = "x", TaxId = "-", IsVatRegistered = registered };
            var s = CompanySettingsFactory.NewFor(c);
            Assert.Equal(VatFlagAgreement.Agree, CompanyVatStatus.Compare(c.IsVatRegistered, s.VatRegistered));
        }
    }

    // ── S-10: ใบเสนอราคาจาก lead / เอกสารจอง CMS คิด VAT ผ่าน OutputVatRate (เดิม 7 ตายตัว) ──

    [Fact]
    public void บริษัทไม่จด_VAT_อัตราภาษีขายเป็นศูนย์_แม้_VatRate_ของบริษัทเป็น_7()
        => Assert.Equal(0m, OutputVatRate.ForCompany(isVatRegistered: false, companyVatRate: 7m));

    [Fact]
    public void บริษัทจด_VAT_อัตราภาษีขายตาม_VatRate_ของบริษัท()
        => Assert.Equal(7m, OutputVatRate.ForCompany(isVatRegistered: true, companyVatRate: 7m));
}
