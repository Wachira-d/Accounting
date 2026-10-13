using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 204 — แบบ ภ.ง.ด.2 (คำตัดสินเจ้าของ DECISIONS ข้อ 148 · ป.รัษฎากร ม.50(2) · ม.59)
///
/// ล็อกทั้งสองทิศ (กฎเหล็ก #4 F2 ข้อ 8):
/// · <b>ใบที่ต้องย้าย</b> — ดอกเบี้ย/เงินปันผล ม.40(4) จ่ายบุคคลธรรมดาในประเทศ ⇒ ภ.ง.ด.2 · ผัง 21915 · ไฟล์ PND.2 · นำส่ง WhtPnd2
/// · <b>ใบที่ห้ามแตะ</b> — ค่าบริการของบุคคล (ภ.ง.ด.3) · ดอกเบี้ยของนิติบุคคล (ภ.ง.ด.53 1%) · ดอกเบี้ยจ่ายต่างประเทศ (ภ.ง.ด.54) ·
///   ใบที่ไม่รู้ประเภท/WHT ระดับหัว (ภ.ง.ด.3 เดิม) · ใบปน (ภ.ง.ด.3 เดิม + คำเตือน)
/// </summary>
public class Pnd2FormTests
{
    // เลขบัตรประชาชนที่ผ่าน checksum (ใช้ในเทสต์อื่นของเรพ) — บุคคลธรรมดาที่พิสูจน์ได้
    private const string PersonId = "1103700123456";
    private const string CompanyId13 = "0105556012345";

    private static Contact Person() => new() { Name = "นายสมชาย ใจดี", TaxId = PersonId, ContactType = ContactType.Individual };
    private static Contact Firm() => new() { Name = "บริษัท ทดสอบ จำกัด", TaxId = CompanyId13, ContactType = ContactType.JuristicPerson };

    private static TaxType Form(Contact c, params string?[] codes)
        => WhtPayeeKind.ResolveForm(false, c.CountryCode, c.TaxId, c.ContactType, c.Name, codes);

    // ══════════ ตารางกลาง: ม.40(4) ══════════

    [Theory]
    [InlineData("4a")]
    [InlineData("4b")]
    [InlineData("4A")]
    [InlineData("40(4)(ก)")]
    [InlineData("40(4)(ข)")]
    [InlineData("40(4)(a)")]
    [InlineData("40(4)")]                 // ไม่ระบุวงเล็บ — แบบชี้ขาดได้แล้ว แม้อัตราจะชี้ขาดไม่ได้
    [InlineData("40(4)(ก) ดอกเบี้ย")]      // รูปที่ตัวอ่าน 50 ทวิ ที่ได้รับเขียนไว้
    public void ม40_4_ทุกรูปที่เก็บไว้_เป็นเงินได้_ภงด2(string code)
        => Assert.True(ThaiWhtRateTable.IsSection40_4(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("8")]
    [InlineData("2")]
    [InlineData("3")]          // ม.40(3) ค่าสิทธิ — คำตัดสินข้อ 148 ไม่ครอบ (คำถามค้าง) ⇒ ห้ามย้ายเงียบ ๆ
    [InlineData("40(8)")]
    [InlineData("8ins")]
    [InlineData("4")]          // ตัวเลขล้วนไม่ระบุมาตรา — ไม่รู้ ≠ ดอกเบี้ย
    public void เงินได้อื่น_หรือไม่รู้ประเภท_ไม่ใช่_ม40_4(string? code)
        => Assert.False(ThaiWhtRateTable.IsSection40_4(code));

    [Fact]
    public void จำแนกส่วนผสม_ไม่มีบรรทัด_ทั้งใบ_ไม่มี_ปน()
    {
        Assert.Equal(ThaiWhtRateTable.Section40_4Mix.Unknown, ThaiWhtRateTable.ClassifySection40_4(null));
        Assert.Equal(ThaiWhtRateTable.Section40_4Mix.Unknown, ThaiWhtRateTable.ClassifySection40_4(Array.Empty<string?>()));
        Assert.Equal(ThaiWhtRateTable.Section40_4Mix.AllSection40_4, ThaiWhtRateTable.ClassifySection40_4(new[] { "4a", "4b" }));
        Assert.Equal(ThaiWhtRateTable.Section40_4Mix.NoSection40_4, ThaiWhtRateTable.ClassifySection40_4(new[] { "8", "5" }));
        Assert.Equal(ThaiWhtRateTable.Section40_4Mix.Mixed, ThaiWhtRateTable.ClassifySection40_4(new[] { "4a", "8" }));
        // บรรทัดหักแต่ไม่ระบุประเภท = "อื่น" (ไม่ใช่ดอกเบี้ย) ⇒ ปน
        Assert.Equal(ThaiWhtRateTable.Section40_4Mix.Mixed, ThaiWhtRateTable.ClassifySection40_4(new[] { "4a", null }));
    }

    [Theory]
    [InlineData(15.0, "4a")]
    [InlineData(15.02, "4a")]
    [InlineData(10.0, "4b")]
    [InlineData(1.0, "40(4)")]     // อัตรานิติบุคคล/อัตราแปลก — บอกแบบได้ ไม่เดาวงเล็บ
    [InlineData(3.0, "40(4)")]
    public void รหัสจากอัตรา_ใช้กับแถว_JV_ที่ไม่มีเอกสาร(double rate, string expected)
        => Assert.Equal(expected, ThaiWhtRateTable.Section40_4CodeFromRate((decimal)rate));

    // ══════════ ตัวตัดสินแบบ ══════════

    [Fact]
    public void ดอกเบี้ย_ปันผล_จ่ายบุคคลธรรมดา_ภงด2()
    {
        Assert.Equal(TaxType.WithholdingTax2, Form(Person(), "4a"));
        Assert.Equal(TaxType.WithholdingTax2, Form(Person(), "4b", "40(4)(ข)"));
    }

    [Fact]
    public void ทิศตรงข้าม_ใบที่ถูกอยู่แล้ว_ไม่ถูกแตะ()
    {
        Assert.Equal(TaxType.WithholdingTax3, Form(Person(), "8"));        // ค่าบริการบุคคล
        Assert.Equal(TaxType.WithholdingTax3, Form(Person()));             // ไม่มีบรรทัดหัก (WHT ระดับหัว) = ไม่รู้ ⇒ เดิม
        Assert.Equal(TaxType.WithholdingTax3,                              // ผู้เรียกที่ไม่ส่งรหัส = พฤติกรรมเดิมทุกตัวอักษร
            WhtPayeeKind.ResolveForm(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี"));
        Assert.Equal(TaxType.WithholdingTax53, Form(Firm(), "4a"));        // ดอกเบี้ยจ่ายนิติบุคคล = ภ.ง.ด.53 (1%)
        Assert.Equal(TaxType.WithholdingTax54,                             // ม.70 ชนะเสมอ
            WhtPayeeKind.ResolveForm(true, null, PersonId, ContactType.Individual, "Mr. Smith", new[] { "4b" }));
        Assert.Equal(TaxType.WithholdingTax54,
            WhtPayeeKind.ResolveForm(false, "SG", null, ContactType.Individual, "Mr. Tan", new[] { "4a" }));
    }

    [Fact]
    public void ใบปน_ดอกเบี้ยกับค่าบริการ_คงภงด3_และต้องเตือน()
    {
        Assert.Equal(TaxType.WithholdingTax3, Form(Person(), "4a", "8"));
        var w = WhtPayeeKind.Pnd2MixWarning(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี", new[] { "4a", "8" });
        Assert.NotNull(w);
        Assert.Contains("ภ.ง.ด.2", w);
        Assert.Contains("แยก", w);
    }

    [Fact]
    public void คำเตือนใบปน_เงียบกับใบที่ไม่ต้องแยก()
    {
        Assert.Null(WhtPayeeKind.Pnd2MixWarning(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี", new[] { "4a" }));
        Assert.Null(WhtPayeeKind.Pnd2MixWarning(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี", new[] { "8", "5" }));
        Assert.Null(WhtPayeeKind.Pnd2MixWarning(false, null, CompanyId13, ContactType.JuristicPerson, "บริษัท ก จำกัด", new[] { "4a", "8" }));
        Assert.Null(WhtPayeeKind.Pnd2MixWarning(true, null, null, ContactType.Individual, "Foreign", new[] { "4a", "8" }));
    }

    // ══════════ 50 ทวิ (ResolveWhtFormType) ══════════

    [Fact]
    public void ใบ50ทวิ_บุคคล_ดอกเบี้ยทั้งใบ_ขอภงด3_ถูกแก้เป็นภงด2()
    {
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(Person(), TaxType.WithholdingTax3,
            withheldIncomeTypeCodes: new[] { "4a" });
        Assert.Equal(TaxType.WithholdingTax2, form);
        Assert.True(corrected);
    }

    [Fact]
    public void ใบ50ทวิ_ขอภงด2_ให้บริษัท_ถูกแก้เป็นภงด53()
    {
        // เดิม ภ.ง.ด.2 ไม่อยู่ในแกน ⇒ ผ่านเฉย ๆ (บริษัทลง ภ.ง.ด.2 = ยื่นผิดแบบ)
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(Firm(), TaxType.WithholdingTax2,
            withheldIncomeTypeCodes: new[] { "4a" });
        Assert.Equal(TaxType.WithholdingTax53, form);
        Assert.True(corrected);
    }

    [Fact]
    public void ใบ50ทวิ_บุคคล_ผู้ใช้เลือกภงด2เอง_เคารพ_ไม่ฟ้องว่าแก้()
    {
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(Person(), TaxType.WithholdingTax2,
            withheldIncomeTypeCodes: new[] { "3" });
        Assert.Equal(TaxType.WithholdingTax2, form);
        Assert.False(corrected);
    }

    [Fact]
    public void ฝ่ายค้าน204_ข้อ2_เลือกภงด2ให้ค่าบริการของบุคคล_ถูกแก้เป็นภงด3()
    {
        // เดิมเคารพทุกกรณี ⇒ ค่าบริการ 40(8) เข้าไฟล์ ภ.ง.ด.2 + 21915 เงียบ ๆ · ใบที่แก้ประเภทภายหลังติด ภ.ง.ด.2 ค้าง
        var (form, corrected, _) = WithholdingTaxCertService.ResolveWhtFormType(Person(), TaxType.WithholdingTax2,
            withheldIncomeTypeCodes: new[] { "8" });
        Assert.Equal(TaxType.WithholdingTax3, form);
        Assert.True(corrected);
        // ปนดอกเบี้ย + ค่าบริการ ⇒ ไม่เคารพเช่นกัน
        Assert.Equal(TaxType.WithholdingTax3, WithholdingTaxCertService.ResolveWhtFormType(Person(), TaxType.WithholdingTax2,
            withheldIncomeTypeCodes: new[] { "4a", "8" }).formType);
        // ทิศตรงข้าม: ไม่ระบุรหัสเลย ⇒ ยังเคารพการเลือก (ใบคีย์มือปันผลที่ไม่กรอกประเภท)
        Assert.Equal(TaxType.WithholdingTax2, WithholdingTaxCertService.ResolveWhtFormType(Person(), TaxType.WithholdingTax2,
            withheldIncomeTypeCodes: new string?[] { null, "" }).formType);
        Assert.True(WhtPayeeKind.Pnd2ChoiceAllowed(null));
        Assert.True(WhtPayeeKind.Pnd2ChoiceAllowed(new[] { "4b", "40(4)(ก)", "3" }));
        Assert.False(WhtPayeeKind.Pnd2ChoiceAllowed(new[] { "8ins" }));
    }

    [Theory]
    [InlineData("TIP-PAYOUT-2026-10", true)]
    [InlineData("JV-2026-10-0001", false)]
    [InlineData(null, false)]
    public void ฝ่ายค้าน204_ข้อ1_JEจ่ายทิป_ระบุได้ด้วยตัวตัดสินเดียว(string? reference, bool expected)
        => Assert.Equal(expected, TipShareAllocation.IsTipJournalReference(reference));

    [Fact]
    public void ใบ50ทวิ_ทิศตรงข้าม_ค่าบริการบุคคล_ยังภงด3_ภงด1และ54ผ่านตามที่ขอ()
    {
        Assert.Equal(TaxType.WithholdingTax3,
            WithholdingTaxCertService.ResolveWhtFormType(Person(), null, withheldIncomeTypeCodes: new[] { "8" }).formType);
        Assert.Equal(TaxType.WithholdingTax3,
            WithholdingTaxCertService.ResolveWhtFormType(Person(), null).formType);
        Assert.Equal(TaxType.WithholdingTax1,
            WithholdingTaxCertService.ResolveWhtFormType(Person(), TaxType.WithholdingTax1, withheldIncomeTypeCodes: new[] { "4a" }).formType);
        Assert.Equal(TaxType.WithholdingTax53,
            WithholdingTaxCertService.ResolveWhtFormType(Firm(), null, withheldIncomeTypeCodes: new[] { "4b" }).formType);
    }

    // ══════════ ผังค้างจ่าย ══════════

    [Fact]
    public void ผังค้างจ่าย_ภงด2_21915_ตกไป21916เมื่อผังไม่ครบ()
    {
        var chain = WhtPayableAccount.CodeChain(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี", new[] { "4b" });
        Assert.Equal(new[] { "21915", "21916", "21917" }, chain);
        Assert.Equal("ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 2",
            WhtPayableAccount.PreferredLabel(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี", new[] { "4b" }));
        Assert.Equal("21915", WhtPayableAccount.CodeFor(TaxType.WithholdingTax2));
        Assert.Contains("21915", WhtPayableAccount.DocumentPayableCodes);
    }

    [Fact]
    public void ผังค้างจ่าย_ทิศตรงข้าม_ไม่ถูกแตะ()
    {
        Assert.Equal("21916", WhtPayableAccount.CodeChain(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี", new[] { "8" })[0]);
        Assert.Equal("21916", WhtPayableAccount.CodeChain(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี")[0]);
        Assert.Equal("21917", WhtPayableAccount.CodeChain(false, null, CompanyId13, ContactType.JuristicPerson, "บริษัท ก จำกัด", new[] { "4a" })[0]);
        Assert.Equal("21918", WhtPayableAccount.CodeChain(true, null, null, ContactType.Individual, "Foreign", new[] { "4a" })[0]);
        Assert.Null(WhtPayableAccount.CodeFor(TaxType.WithholdingTax1));   // 21914 มาจากรอบเงินเดือน ไม่ใช่เอกสารซื้อ
    }

    [Fact]
    public void ทิป_ลงผัง_ภงด3_ตามแบบของ50ทวิทิป_ไม่ใช่21915()
    {
        Assert.Equal("21916", TipShareAllocation.PayableAccountCode);
        Assert.Equal(TaxType.WithholdingTax3, WhtPayeeKind.IndividualForm(new[] { TipShareAllocation.IncomeTypeCode }));
    }

    [Fact]
    public void migration_ใส่ผัง21915ให้บริษัทเดิม_ONCONFLICT_ไม่ย้ายยอด_และอยู่ในชุดคำสั่ง()
    {
        var sql = WhtPayableAccount.MigrationSeedSql();
        Assert.Contains("'21915','219'", sql);
        Assert.Contains("ภ.ง.ด. 2", sql);
        Assert.EndsWith("ON CONFLICT DO NOTHING;", sql);
        Assert.DoesNotContain("UPDATE", sql);
        Assert.Contains(sql, DatabaseMigrationHelper.GetAlterStatements());
        // ตัวสร้าง SQL ตัวเดียว — ผังของ Settlement ยังได้ข้อความเดิม (ล็อกไม่ให้ refactor เปลี่ยนคำสั่งที่รันทุกบูต)
        Assert.Equal(SettlementChartSeed.SeedSqlFor(SettlementChartSeed.SeededCodes), SettlementChartSeed.MigrationSeedSql());
        Assert.Contains("'11350','113'", SettlementChartSeed.MigrationSeedSql());
    }

    // ══════════ กำหนดยื่น · ไฟล์ · นำส่ง ══════════

    [Fact]
    public void กำหนดยื่น_ภงด2_เท่ากับ_ภงด3()
    {
        Assert.Equal("WhtPnd2", TaxFilingDeadline.KeyOf(TaxType.WithholdingTax2));
        for (var m = 1; m <= 12; m++)
        {
            Assert.Equal(TaxFilingDeadline.For("WhtPnd3", 2026, m), TaxFilingDeadline.For("WhtPnd2", 2026, m));
            Assert.Equal(TaxFilingDeadline.WarnByFor(TaxType.WithholdingTax3, 2026, m),
                TaxFilingDeadline.WarnByFor(TaxType.WithholdingTax2, 2026, m));
        }
    }

    [Fact]
    public void รหัสแบบ_PND2_และป้าย()
    {
        Assert.Equal(TaxType.WithholdingTax2, WhtUnissuedCertGate.TaxTypeForPndForm("PND.2"));
        Assert.Equal(TaxType.WithholdingTax2, WhtUnissuedCertGate.TaxTypeForPndForm("pnd.2"));
        Assert.Equal("ภ.ง.ด.2", WhtUnissuedCertGate.FormLabel(TaxType.WithholdingTax2));
        Assert.Equal(TaxType.WithholdingTax2, WhtCertVoidGuard.RemittanceForm("WhtPnd2"));
    }

    [Fact]
    public void แถว_JV_21915_ติ๊กออก_แต่ไม่บล็อกการยื่น()
    {
        // marker คนละตัวกับ "ยังไม่ออก 50 ทวิ" โดยตั้งใจ — JV ไม่มีลิงก์ถึงใบรับรอง ถ้าบล็อก = ยื่นไม่ได้ตลอดกาล
        var rows = new[] { ((string?)$"{WhtUnissuedCertGate.JournalOnlyPnd2Marker} — JV-0001", true, 1500m) };
        Assert.False(WhtUnissuedCertGate.Evaluate(rows).Any);
        // ทิศตรงข้าม: แถว "ยังไม่ออก 50 ทวิ" ยังบล็อกเหมือนเดิม
        var unissued = new[] { ((string?)$"{WhtUnissuedCertGate.UnissuedCertMarker} — ดอกเบี้ยเงินกู้กรรมการ", true, 1500m) };
        Assert.True(WhtUnissuedCertGate.Evaluate(unissued).Any);
    }

    [Fact]
    public void หน้านำส่ง_ภงด2_ยื่นเฉพาะเดือนที่จ่าย()
    {
        var (always, legal) = StatutoryRemittanceService.FilingRule("WhtPnd2");
        Assert.False(always);
        Assert.Contains("ม.50(2)", legal);
        Assert.Equal(StatutoryRemittanceService.DueDates("WhtPnd3", 2026, 10), StatutoryRemittanceService.DueDates("WhtPnd2", 2026, 10));
    }

    [Fact]
    public void ไฟล์ยื่น_ภงด2_ใช้layoutเดียวกับ3_53_รหัส4A_4B()
    {
        Assert.Equal("4A", PndIncomeTypeCode.ForFile("4a"));
        Assert.Equal("4B", PndIncomeTypeCode.ForFile("40(4)(ข)"));
        var row = PndTextFileFormat.DetailRow(1, new PndTextFileFormat.Row(PersonId, "00000", "นายสมชาย ใจดี", false,
            new DateTime(2026, 10, 5), PndIncomeTypeCode.ForFile("4b"), 10_000m, 10m, 1_000m, PayeeTitle: "นาย"));
        Assert.Equal("1|1103700123456|00000|สมชาย|ใจดี|05102026|4B|10000.00|10.00|1000.00|1|นาย", row);
    }

    // ══════════ ชุดรหัสของบรรทัดที่หักจริง ══════════

    [Fact]
    public void ชุดรหัส_นับเฉพาะบรรทัดที่หักภาษี_ไม่นับบรรทัดลบ()
    {
        var lines = new[]
        {
            new DocumentLine { Description = "ดอกเบี้ยเงินกู้กรรมการ", IncomeTypeCode = "4a", WithholdingTaxAmount = 150m },
            new DocumentLine { Description = "ค่าธรรมเนียมโอน", IncomeTypeCode = null, WithholdingTaxAmount = 0m },   // ไม่หัก ⇒ ไม่นับ
            new DocumentLine { Description = "ลบแล้ว", IncomeTypeCode = "8", WithholdingTaxAmount = 30m, IsDeleted = true },
        };
        Assert.Equal(new string?[] { "4a" }, WhtIncomeCodes.Withheld(lines));
        Assert.Equal(TaxType.WithholdingTax2, WhtPayeeKind.IndividualForm(WhtIncomeCodes.Withheld(lines)));

        var certLines = new[]
        {
            new WithholdingTaxCertLine { IncomeTypeCode = "4b", TaxAmount = 100m },
            new WithholdingTaxCertLine { IncomeTypeCode = "8", TaxAmount = 0m },
        };
        Assert.Equal(new string?[] { "4b" }, WhtIncomeCodes.Withheld(certLines));
    }

    // ══════════ B) ประกัน พ.ร.บ. รถยนต์ = ค่าเบี้ยประกันวินาศภัย (ข้อ 148 (3)) ══════════

    private static WhtLineFact Line(string desc) => new(desc, null, null, 5_000m);

    [Theory]
    [InlineData("ประกัน พ.ร.บ. รถยนต์ ทะเบียน 1กข 1234")]
    [InlineData("ค่า พ.ร.บ. รถจักรยานยนต์")]
    [InlineData("พรบ รถยนต์ ปี 2569")]
    [InlineData("เบี้ยประกันภัยภาคบังคับ")]
    [InlineData("ประกันภัยตาม พ.ร.บ.คุ้มครองผู้ประสบภัยจากรถ")]
    [InlineData("Compulsory motor insurance")]
    public void ประกันพรบรถยนต์_ต้องสงสัยว่าต้องหัก(string desc)
    {
        var r = WhtServiceHints.Scan(new[] { Line(desc) });
        Assert.True(r.Suspicious, desc);
    }

    [Theory]
    [InlineData("เบี้ยประกันชีวิตกรรมการ")]
    [InlineData("เงินสมทบประกันสังคมตาม พ.ร.บ.ประกันสังคม")]
    [InlineData("ประกันสุขภาพกลุ่มพนักงาน")]
    [InlineData("หนังสือ พ.ร.บ.การบัญชี 2543")]
    public void ทิศตรงข้าม_ประกันชีวิต_ประกันสังคม_พรบอื่น_ไม่สงสัย(string desc)
    {
        var r = WhtServiceHints.Scan(new[] { Line(desc) });
        Assert.False(r.Suspicious, $"{desc} → {r.MatchedKeyword}");
    }

    [Fact]
    public void ประกันพรบ_ประเภทเงินได้_8ins_1เปอร์เซ็นต์_ภงด53()
    {
        var t = ThaiWhtRateTable.Find("8ins")!;
        Assert.Equal(1m, t.JuristicRate);
        Assert.Equal(new[] { "ภ.ง.ด.53" }, t.ApplicableForms);
        Assert.Equal("5", ThaiWhtRateTable.CertificateRow("8ins"));
    }
}
