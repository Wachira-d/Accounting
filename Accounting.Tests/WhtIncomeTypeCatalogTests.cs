using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ตารางประเภทเงินได้ครบทุกแบบที่ SME เจอ + ทุกแถวมีที่ไปบนเอกสาร/แบบยื่น** (รอบ 203)
///
/// ═══ ที่มา (คำถามเจ้าของ 2026-10-12) ═══
/// "หัก ณ ที่จ่ายค่าเบี้ยประกันภัย 1% ระบบจะลงถูกช่องในใบหัก ณ ที่จ่ายใช่ไหม" — ตารางกลางไม่มีประเภทนี้เลย ⇒ ผู้ใช้ต้องเลือก
/// "ค่าขนส่ง 1%" (ได้แถว 5 ถูกโดยบังเอิญ แต่ชื่อ/รหัสผิด) หรือ "ค่าบริการ" (อัตรา 3% ผิด) · ตัวอ่านค่าที่เก็บไว้ 3 ตัว
/// (ป้ายบนหน้า 50 ทวิ · รหัส Col7 ไฟล์ ภ.ง.ด. · ตัวตรวจอัตราทะเบียนภาษีถูกหัก) เป็นสำเนามือคนละชุด
///
/// <para>เทสต์ชุดนี้ล็อก (1) โครงทุกแถว (2) ประเภทใหม่ (3) <b>ทิศตรงข้าม</b>: รหัสเดิม/ข้อมูลเก่าให้คำตอบเดิมทุกตัว</para>
/// </summary>
public class WhtIncomeTypeCatalogTests
{
    private static readonly string[] CertificateRows = { "1", "2", "3", "4a", "4b", "5", "6" };

    public static TheoryData<string> ทุกรหัส()
    {
        var d = new TheoryData<string>();
        foreach (var t in ThaiWhtRateTable.All) d.Add(t.Code);
        return d;
    }

    [Theory]
    [MemberData(nameof(ทุกรหัส))]
    public void ทุกประเภทมีแถว50ทวิ_แบบยื่น_รหัสไฟล์_และอัตราที่ด่านอนุมัติรู้จัก(string code)
    {
        var t = ThaiWhtRateTable.Find(code)!;
        Assert.False(string.IsNullOrWhiteSpace(t.Name));
        Assert.False(string.IsNullOrWhiteSpace(t.TaxSection));
        Assert.NotEmpty(t.ApplicableForms);
        Assert.Contains(ThaiWhtRateTable.CertificateRow(code), CertificateRows);
        Assert.False(string.IsNullOrWhiteSpace(t.PndFileCode));
        Assert.Equal(t.PndFileCode, PndIncomeTypeCode.ForFile(code));
        Assert.False(string.IsNullOrWhiteSpace(t.LegalReference));
        Assert.True(code.Length <= 10, "WithholdingTaxCertLine.IncomeTypeCode HasMaxLength(10)");
        // อัตราของทุกแถวต้องอยู่ในชุดที่ด่านอนุมัติเอกสารถือว่า "ตามกฎหมาย" — ไม่งั้นใบที่ถูกถูกเตือน
        foreach (var r in new[] { t.IndividualRate, t.JuristicRate })
            if (r is decimal rate) Assert.Contains(rate, ThaiWhtRateTable.StatutoryRates);
        // แถวที่ไม่มีอัตราทั้งสองข้างได้เฉพาะเงินเดือน (อัตราก้าวหน้า)
        if (t.IndividualRate is null && t.JuristicRate is null) Assert.Equal("1", code);
    }

    [Fact]
    public void รหัสไม่ซ้ำ_และค้นด้วยเลขมาตราได้แถวรวมเดิม()
    {
        Assert.Equal(ThaiWhtRateTable.All.Count, ThaiWhtRateTable.All.Select(t => t.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // ข้อมูลเก่าที่เก็บเป็นเลขมาตรา ต้องได้แถวรวม (ไม่ใช่ประเภทย่อยที่เพิ่มทีหลัง)
        Assert.Equal("8", ThaiWhtRateTable.Find("40(8)")!.Code);
        Assert.Equal("2", ThaiWhtRateTable.Find("40(2)")!.Code);
    }

    // ─────────── ค่าเบี้ยประกันวินาศภัย 1% (คำถามเจ้าของ) ───────────

    [Fact]
    public void ค่าเบี้ยประกันวินาศภัย_1เปอร์เซ็นต์_แถว5_ระบุชื่อ_ภงด53()
    {
        var t = ThaiWhtRateTable.Find("8ins")!;
        Assert.Equal(1m, ThaiWhtRateTable.RateFor("8ins", payeeIsJuristic: true));
        Assert.Equal("5", ThaiWhtRateTable.CertificateRow("8ins"));
        Assert.Equal("ค่าเบี้ยประกันวินาศภัย", t.CertificateRowText);
        Assert.Contains("ภ.ง.ด.53", t.ApplicableForms);
        Assert.Equal("ค่าเบี้ยประกันวินาศภัย",
            ThaiWhtRateTable.CertificateRowSpecification("5", new (string?, string?)[] { ("8ins", "เบี้ยประกันรถ กข 1234") }));
    }

    [Fact]
    public void ค่าเบี้ยประกัน_ผู้รับบุคคลธรรมดาไม่มีอัตรา_ห้ามยืมอัตรานิติบุคคล()
    {
        Assert.Null(ThaiWhtRateTable.RateFor("8ins", payeeIsJuristic: false));
        Assert.Equal(0m, TaxService.GetWhtRate("8ins", 10_000m, 0m, payeeIsJuristic: false));
    }

    [Theory]
    [InlineData("8pr", 3.0, 3.0, "5")]    // รางวัล/ส่วนลดจากการส่งเสริมการขาย
    [InlineData("8cp", 5.0, 5.0, "5")]    // รางวัลประกวด/แข่งขัน/ชิงโชค
    [InlineData("8ent", 5.0, null, "5")]  // นักแสดงสาธารณะ (ผู้มีถิ่นที่อยู่) — บุคคลธรรมดาเท่านั้น
    [InlineData("gov1", null, 1.0, "6")]  // หน่วยงานรัฐหัก 1% (ม.69 ทวิ) → แถว "อื่น ๆ (ระบุ)"
    public void ประเภทใหม่_อัตรา_และแถว(string code, double? individual, double? juristic, string row)
    {
        Assert.Equal(individual is null ? null : (decimal?)(decimal)individual.Value, ThaiWhtRateTable.RateFor(code, payeeIsJuristic: false));
        Assert.Equal(juristic is null ? null : (decimal?)(decimal)juristic.Value, ThaiWhtRateTable.RateFor(code, payeeIsJuristic: true));
        Assert.Equal(row, ThaiWhtRateTable.CertificateRow(code));
        Assert.NotNull(ThaiWhtRateTable.CertificateRowSpecification(row, new (string?, string?)[] { (code, null) }));
    }

    // ─────────── ทิศตรงข้าม: ของเดิมต้องไม่ถูกแตะ ───────────

    [Fact]
    public void ค่าขนส่งยังเป็น8tr_แถว5_ระบุค่าขนส่ง()
    {
        Assert.Equal(1m, ThaiWhtRateTable.RateFor("8tr", payeeIsJuristic: true));
        Assert.Equal("5", ThaiWhtRateTable.CertificateRow("8tr"));
        Assert.Equal("ค่าขนส่ง", ThaiWhtRateTable.CertificateRowSpecification("5", new (string?, string?)[] { ("8tr", "ค่าส่งของ") }));
    }

    [Fact]
    public void รหัสรวม8_ไม่พิมพ์ระบุ_เพราะเป็นค่าตั้งต้นของใบที่ไม่ได้ระบุประเภท()
        // AutoGenerateFromDocumentAsync ใส่ "8" ให้บรรทัดที่ไม่มีประเภท ⇒ ถ้าพิมพ์ "ค่าจ้างทำของ" ลงใบค่าเบี้ยประกัน = ข้อความที่แต่งขึ้น
        => Assert.Null(ThaiWhtRateTable.CertificateRowSpecification("5", new (string?, string?)[] { ("8", "เบี้ยประกันภัย") }));

    [Fact]
    public void รหัสไม่รู้จัก_ตกแถว6_และระบุด้วยคำอธิบายบรรทัด_ไม่ใช่ว่าง()
    {
        Assert.Equal("6", ThaiWhtRateTable.CertificateRow("zzz"));
        Assert.Equal("ค่าตอบแทนกรรมการ", ThaiWhtRateTable.CertificateRowSpecification("6",
            new (string?, string?)[] { ("zzz", "ค่าตอบแทนกรรมการ") }));
    }

    [Fact]
    public void ระบุ_รวมหลายประเภทในแถวเดียว_ไม่ซ้ำ_และไม่ดึงบรรทัดของแถวอื่น()
    {
        var lines = new (string?, string?)[] { ("8tr", "a"), ("8ad", "b"), ("8tr", "c"), ("1", "เงินเดือน"), ("8", "ค่าบริการ") };
        Assert.Equal("ค่าขนส่ง, ค่าโฆษณา", ThaiWhtRateTable.CertificateRowSpecification("5", lines));
        Assert.Null(ThaiWhtRateTable.CertificateRowSpecification("1", lines));   // แถว 1–4 ชื่อแถวเจาะจงอยู่แล้ว
        Assert.Null(ThaiWhtRateTable.CertificateRowSpecification("6", lines));   // ไม่มีบรรทัดของแถว 6
    }

    [Fact]
    public void ระบุ_ยาวเกินช่องถูกตัดพร้อมจุดไข่ปลา()
    {
        var longDesc = new string('ก', 300);
        var spec = ThaiWhtRateTable.CertificateRowSpecification("6", new (string?, string?)[] { ("zzz", longDesc) })!;
        Assert.Equal(ThaiWhtRateTable.CertificateSpecificationMaxLength, spec.Length);
        Assert.EndsWith("…", spec);
    }

    [Theory]
    // รูปที่ switch เดิมของ PndIncomeTypeCode รับ — คำตอบต้องเหมือนเดิมทุกตัว
    [InlineData("1", "1")] [InlineData("40(1)", "1")]
    [InlineData("2", "2")] [InlineData("40(2)", "2")]
    [InlineData("3", "3")] [InlineData("40(3)", "3")]
    [InlineData("4a", "4A")] [InlineData("40(4)a", "4A")] [InlineData("40(4)(ก)", "4A")] [InlineData("40(4)(a)", "4A")]
    [InlineData("4b", "4B")] [InlineData("40(4)b", "4B")] [InlineData("40(4)(ข)", "4B")] [InlineData("40(4)(b)", "4B")]
    [InlineData("5", "5")] [InlineData("40(5)", "5")]
    [InlineData("6", "6")] [InlineData("7", "6")] [InlineData("40(7)", "6")]
    [InlineData("8", "6")] [InlineData("40(8)", "6")] [InlineData("8ad", "6")] [InlineData("8tr", "6")]
    [InlineData(null, "6")] [InlineData("", "6")] [InlineData("zzz", "6")]
    public void รหัสไฟล์ภงด_ของข้อมูลเดิมไม่เปลี่ยน(string? stored, string expected)
        => Assert.Equal(expected, PndIncomeTypeCode.ForFile(stored));

    [Theory]
    // ค่าที่ตัวอ่าน 50 ทวิ ที่ได้รับ (WhtCertIncomeType.Infer) เขียนลงทะเบียนภาษีถูกหัก
    [InlineData("40(8) ค่าโฆษณา", "8ad")]
    [InlineData("40(8) ค่าขนส่ง", "8tr")]
    [InlineData("40(8) ค่าบริการ", "8")]
    [InlineData("40(4)(ก) ดอกเบี้ย", "4a")]
    [InlineData("40(4)(ข) เงินปันผล", "4b")]
    [InlineData("40(5) ค่าเช่า", "5")]
    [InlineData("8ins", "8ins")]
    [InlineData("ม.69 ทวิ", "gov1")]
    public void Resolve_ค่าที่เก็บไว้ทุกรูป(string stored, string expectedCode)
        => Assert.Equal(expectedCode, ThaiWhtRateTable.Resolve(stored)?.Code);

    [Theory]
    [InlineData("40(4)")]       // ดอกเบี้ยหรือปันผล — ชี้ขาดไม่ได้ ห้ามเดา
    [InlineData("ค่าบริการ")]
    [InlineData(null)]
    public void Resolve_ชี้ขาดไม่ได้คืนnull(string? stored) => Assert.Null(ThaiWhtRateTable.Resolve(stored));

    // ─────────── ตัวตัดสินอัตราไม่ตรงประเภท ───────────

    private static readonly DateTime Pay2026 = new(2026, 10, 1);

    [Theory]
    [InlineData("8ins", 1.0, true)]
    [InlineData("8tr", 1.0, true)]
    [InlineData("8ad", 2.0, false)]
    [InlineData("5", 5.0, null)]
    [InlineData("4a", 15.0, false)]
    [InlineData("4a", 1.0, null)]      // ไม่รู้ชนิดผู้รับ ⇒ อัตราของชนิดใดชนิดหนึ่งผ่าน (G3)
    [InlineData("4a", 15.0, null)]
    [InlineData("1", 5.0, false)]      // เงินเดือนไม่มีอัตราคงที่ ⇒ ไม่ตรวจ
    [InlineData("zzz", 7.0, true)]     // ไม่รู้จักประเภท ⇒ ไม่ตรวจ (ด่าน StatutoryRates เดิมดูแลอยู่)
    [InlineData("8", 0.0, true)]       // ไม่ได้หัก
    public void อัตราตรงประเภท_ไม่เตือน(string code, double rate, bool? juristic)
        => Assert.Null(ThaiWhtRateTable.CheckRateForType(code, (decimal)rate, juristic, Pay2026));

    [Theory]
    [InlineData("8ins", 3.0, true, "1%")]   // เบี้ยประกันหัก 3% = หักเกิน
    [InlineData("8", 1.0, true, "3%")]      // ค่าบริการหัก 1% = หักขาด (ม.54 ผู้จ่ายรับผิด)
    [InlineData("4a", 15.0, true, "1%")]    // ดอกเบี้ยนิติบุคคล
    [InlineData("4a", 1.0, false, "15%")]   // ดอกเบี้ยบุคคลธรรมดา
    [InlineData("8cp", 3.0, null, "5%")]
    public void อัตราไม่ตรงประเภท_เตือนพร้อมอัตราที่ควรใช้และตัวบท(string code, double rate, bool? juristic, string expectedRateText)
    {
        var w = ThaiWhtRateTable.CheckRateForType(code, (decimal)rate, juristic, Pay2026);
        Assert.NotNull(w);
        Assert.Contains(expectedRateText, w);
        Assert.Contains(ThaiWhtRateTable.Find(code)!.LegalReference, w);
    }

    [Fact]
    public void อัตราลดชั่วคราวในช่วงประกาศ_ไม่เตือน_นอกช่วงเตือน()
    {
        Assert.Null(ThaiWhtRateTable.CheckRateForType("8", 1.5m, true, new DateTime(2020, 6, 1)));
        Assert.NotNull(ThaiWhtRateTable.CheckRateForType("8", 1.5m, true, Pay2026));
    }

    // ─────────── ทะเบียนภาษีถูกหัก: ตัวตรวจอัตราอ่านตารางกลาง ───────────

    [Theory]
    [InlineData("8ad", 2.0)]              // เดิม: รหัสจาก dropdown ไม่ถูกตรวจเลย — ตอนนี้ตรวจและผ่าน
    [InlineData("40(8) ค่าโฆษณา", 2.0)]   // ค่าจากตัวอ่าน 50 ทวิ — เหมือนเดิม
    [InlineData("8ins", 1.0)]
    [InlineData("40(4)", 10.0)]           // 40(4) เฉย ๆ ชี้ขาดไม่ได้ ⇒ ไม่เตือน (เดิมเดาเป็นดอกเบี้ย 1% แล้วเตือนปันผล 10%)
    public void ทะเบียนภาษีถูกหัก_อัตราถูก_ไม่เตือน(string stored, double rate)
        => Assert.Null(WhtCreditService.CheckRate(stored, (decimal)rate, Pay2026));

    [Theory]
    [InlineData("5", 3.0)]                // เดิมไม่ถูกตรวจ (รหัสจาก dropdown) — ค่าเช่าต้อง 5%
    [InlineData("40(8) ค่าบริการ", 5.0)]
    public void ทะเบียนภาษีถูกหัก_อัตราผิด_เตือน(string stored, double rate)
        => Assert.NotNull(WhtCreditService.CheckRate(stored, (decimal)rate, Pay2026));
}
