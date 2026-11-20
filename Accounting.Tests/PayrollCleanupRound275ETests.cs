using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Import;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 275 แพ็ก E (เก็บกวาดหลังรอบ 274) — pure:
/// <list type="bullet">
/// <item>T4 H-3 + คำตัดสิน 4.2: ตัวตัดสินเดียว <see cref="EmployeeTaxIdentity.ForFiling(string?, string?, DateTime?)"/> — รับทราบมีผลเฉพาะ "ไม่มีเลขเลย" ·
///   เลขผิด/ถอดรหัสไม่ได้ไม่ได้รับสิทธิ์ · ไฟล์ ภ.ง.ด.1 ตัดสินแบบเดียวกับ 50 ทวิ · สปส. ตามเลข ปกส. · ข้อความประกอบพาสปอร์ต</item>
/// <item>T4 H-2: เลขที่ใบเงินเดือน → รหัสพนักงาน · "เลขขยะ" · ทางไปต่อของงวดที่ยื่นแล้ว</item>
/// <item>T4 M-3 (กรองแบบ/ประเทศ) · M-6 (แถวนำเข้าถอดไม่ได้ = ข้อความไทย) · L-7 (ชื่ออังกฤษ)</item>
/// </list>
/// </summary>
public class PayrollCleanupRound275ETests
{
    private const string Valid = "1101700001237";
    private const string BadChecksum = "1101700001238";
    private static readonly DateTime Ack = new(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);

    // ───────── H-3 · ตัวตัดสินเดียว ─────────

    [Fact]
    public void ForFiling_ไม่มีเลข_ยังไม่รับทราบ_บล็อก_รับทราบแล้ว_ออกใบได้()
    {
        var before = EmployeeTaxIdentity.ForFiling(null, null, null);
        Assert.Equal(EmployeeTaxIdentity.FilingKind.NeedsAcknowledgement, before.Kind);
        Assert.False(before.CanIssueCert);
        Assert.True(before.BlocksWhenTaxed);
        Assert.Contains("รับทราบไม่มีเลข 13 หลัก", before.Reason);

        var after = EmployeeTaxIdentity.ForFiling(null, "", Ack);
        Assert.Equal(EmployeeTaxIdentity.FilingKind.WaivedNoId, after.Kind);
        Assert.True(after.CanIssueCert);
        Assert.False(after.BlocksWhenTaxed);
        Assert.Null(after.Value);
        Assert.Null(after.Reason);
    }

    [Fact]
    public void ForFiling_เลขผิดหรือถอดรหัสไม่ได้_รับทราบแล้วก็ยังบล็อก_ต้องแก้เลข()
    {
        var bad = EmployeeTaxIdentity.ForFiling(null, BadChecksum, Ack);
        Assert.Equal(EmployeeTaxIdentity.FilingKind.Unusable, bad.Kind);
        Assert.False(bad.CanIssueCert);
        var cipher = EncryptionHelper.Encrypt(Valid, "r275e-lost-key");
        var lost = EmployeeTaxIdentity.ForFiling(cipher, null, Ack);
        Assert.Equal(EmployeeTaxIdentity.FilingKind.Unusable, lost.Kind);
        Assert.Equal(TaxIdProblem.Undecryptable, lost.Inspected.Problem);
        // ทิศตรงข้าม: มีเลขถูก — การรับทราบเดิมไม่มีผล (ใช้เลขจริง)
        var ok = EmployeeTaxIdentity.ForFiling(null, Valid, Ack);
        Assert.Equal(EmployeeTaxIdentity.FilingKind.Ok, ok.Kind);
        Assert.Equal(Valid, ok.Value);
    }

    [Fact]
    public void WaiverRefusal_รับทราบได้เฉพาะคนที่ไม่มีเลข()
    {
        Assert.Null(EmployeeTaxIdentity.WaiverRefusal(null, null));
        Assert.Null(EmployeeTaxIdentity.WaiverRefusal(" - ", ""));
        Assert.Contains("checksum", EmployeeTaxIdentity.WaiverRefusal(null, BadChecksum));
        Assert.Contains("มีเลขประจำตัว 13 หลักที่ถูกต้องแล้ว", EmployeeTaxIdentity.WaiverRefusal(null, Valid));
        Assert.StartsWith("รับทราบไม่ได้", EmployeeTaxIdentity.WaiverRefusal(EncryptionHelper.Encrypt(Valid, "x-key"), null));
    }

    [Fact]
    public void WaiverCertNote_แสดงพาสปอร์ต_ถอดรหัสไม่ได้ไม่เผยตัวเลข_ไม่รับทราบไม่แสดง()
    {
        Assert.Equal("ผู้มีเงินได้ไม่มีเลขประจำตัวผู้เสียภาษี 13 หลัก · เลขหนังสือเดินทาง AB1234567",
            EmployeeTaxIdentity.WaiverCertNote(true, " AB1234567 "));
        var cipher = EncryptionHelper.Encrypt("AB1234567", "lost");
        var n = EmployeeTaxIdentity.WaiverCertNote(true, cipher)!;
        Assert.DoesNotContain("AB", n);
        Assert.DoesNotContain(cipher, n);
        Assert.Equal("ผู้มีเงินได้ไม่มีเลขประจำตัวผู้เสียภาษี 13 หลัก", EmployeeTaxIdentity.WaiverCertNote(true, null));
        Assert.Null(EmployeeTaxIdentity.WaiverCertNote(false, "AB1234567"));
    }

    // ───────── H-3 · ไฟล์ ภ.ง.ด.1 ใช้ตัวตัดสินเดียวกับ 50 ทวิ ─────────

    private static PayrollDetail Detail(string code, string? citizen, decimal tax, DateTime? ack = null, string? first = "ชื่อ")
    {
        var emp = new Employee { EmployeeCode = code, TitleTh = "นาย", FirstNameTh = first ?? "", LastNameTh = "สกุล" + code, FirstNameEn = "John", LastNameEn = "Smith",
            CitizenId = citizen, TaxIdWaiverAcknowledgedAt = ack, Address = "1 ถ.สุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพมหานคร 10110" };
        var run = new PayrollRun { Year = 2026, Month = 9, PayDate = new DateTime(2026, 9, 30), Status = "Paid" };
        return new PayrollDetail { Employee = emp, EmployeeId = Guid.NewGuid(), PayrollRun = run, GrossIncome = 50000m, TaxableGross = 50000m, WithholdingTax = tax };
    }

    [Fact]
    public void Pnd1_ไม่มีเลข_มีภาษี_ยังไม่รับทราบ_กันแถว_รับทราบแล้วลงเลขศูนย์()
    {
        var blocked = Pnd1FilingData.Build(new[] { Detail("F001", null, 2291.67m), Detail("E002", Valid, 375m) }, Array.Empty<WithholdingTaxCert>(), annual: false);
        var b = Assert.Single(blocked.BlockedRows);
        Assert.Equal("F001", b.SourceRef);
        Assert.Equal(TaxIdProblem.Missing, b.Problem);
        Assert.Contains("รับทราบไม่มีเลข 13 หลัก", b.Reason);
        Assert.Contains("ยังไม่มี", Pnd1FilingData.BlockedMessage("ภ.ง.ด.1", "09/2569", blocked.BlockedRows));
        Assert.Equal(Valid, Assert.Single(blocked.Rows).PayeeTaxId);   // ทิศตรงข้าม: คนที่มีเลขถูกไม่ถูกแตะ

        var waived = Pnd1FilingData.Build(new[] { Detail("F001", null, 2291.67m, Ack) }, Array.Empty<WithholdingTaxCert>(), annual: true);
        Assert.Empty(waived.BlockedRows);
        Assert.Null(Assert.Single(waived.Rows).PayeeTaxId);
        // ฝ่ายค้าน MED-2: ป้ายเตือนบอกชัดว่ายังไม่ยืนยันกับสเปก RD ว่าช่อง 5 รับเลข 0 ได้
        Assert.Contains(waived.Notes, n => n.StartsWith("⚠️ 1 รายการเป็นผู้ไม่มีเลขประจำตัว 13 หลักที่ผู้ทำบัญชีรับทราบแล้ว") && n.Contains("ยังไม่ได้ยืนยันกับสเปกกรมสรรพากร"));
        Assert.DoesNotContain(waived.Notes, n => n.Contains("รายการไม่มีเลขประจำตัวผู้เสียภาษี (ไฟล์ลงเลข 0"));
    }

    [Fact]
    public void Pnd1_ไม่มีเลข_ภาษีศูนย์_ไม่ต้องรับทราบ_คงพฤติกรรมเดิม()
    {
        var r = Pnd1FilingData.Build(new[] { Detail("E009", null, 0m) }, Array.Empty<WithholdingTaxCert>(), annual: false);
        Assert.Empty(r.BlockedRows);
        Assert.Null(Assert.Single(r.Rows).PayeeTaxId);
        Assert.Contains(r.Notes, n => n.Contains("ไม่มีเลขประจำตัวผู้เสียภาษี"));
    }

    [Fact]
    public void Pnd1_เลขผิด_รับทราบแล้ว_ยังกันแถว()
    {
        var r = Pnd1FilingData.Build(new[] { Detail("E003", BadChecksum, 500m, Ack) }, Array.Empty<WithholdingTaxCert>(), annual: false);
        Assert.Equal(TaxIdProblem.Invalid, Assert.Single(r.BlockedRows).Problem);
        Assert.Empty(r.Rows);
    }

    private static WithholdingTaxCert ManualCert(string no, string? taxId, string country, DateTime? waiver = null, decimal tax = 1500m)
    {
        var c = new WithholdingTaxCert
        {
            CertificateNumber = no, TaxFormType = TaxType.WithholdingTax1, TaxYear = 2026, TaxMonth = 9, Status = WithholdingTaxCertStatus.Issued,
            TotalIncomeAmount = 10000m, TotalTaxAmount = tax,
            PayeeContact = new Contact { Name = "Foreign Agent", TaxId = taxId, CountryCode = country, ContactType = ContactType.Individual, PostalCode = "10500",
                TaxIdWaiverAcknowledgedAt = waiver },
        };
        c.Lines.Add(new WithholdingTaxCertLine { LineOrder = 1, IncomeTypeCode = "2", PaymentDate = new DateTime(2026, 9, 15), IncomeAmount = 10000m, TaxRate = 15m, TaxAmount = tax });
        return c;
    }

    /// <summary>ฝ่ายค้าน MED-2: ไม่มีทางลัดผ่านประเทศของผู้ติดต่อ — เลขต่างประเทศในช่องเลข = เลขผิด (กัน) · ไม่มีเลข + ภาษี ⇒ ต้องรับทราบรายคน ·
    /// รับทราบแล้ว ⇒ เลข 0 + ป้ายเตือน · ทิศตรงข้าม: ผู้รับเลขถูกไม่ถูกแตะ</summary>
    [Fact]
    public void Pnd1_ผู้รับใบมือ_ไม่มีทางลัดผ่านประเทศ_ต้องรับทราบรายคน_ทิศตรงข้ามเลขถูกผ่าน()
    {
        var foreignNo = Pnd1FilingData.Build(Array.Empty<PayrollDetail>(), new[] { ManualCert("WHT-1", "US-998877", "US", waiver: Ack) }, annual: false);
        Assert.Equal(TaxIdProblem.Invalid, Assert.Single(foreignNo.BlockedRows).Problem);   // รับทราบไม่มีผลกับเลขผิด

        var noAck = Pnd1FilingData.Build(Array.Empty<PayrollDetail>(), new[] { ManualCert("WHT-2", null, "US") }, annual: false);
        var b = Assert.Single(noAck.BlockedRows);
        Assert.Equal(TaxIdProblem.Missing, b.Problem);
        Assert.Contains("รับทราบไม่มีเลข 13 หลัก", b.Reason);

        var acked = Pnd1FilingData.Build(Array.Empty<PayrollDetail>(), new[] { ManualCert("WHT-3", null, "US", waiver: Ack) }, annual: false);
        Assert.Empty(acked.BlockedRows);
        var row = Assert.Single(acked.Rows);
        Assert.Equal("", ThaiTaxId.Normalize(row.PayeeTaxId));
        Assert.Equal(Pnd1FileFormat.Section40_2NonResident, row.Category);
        Assert.Contains(acked.Notes, n => n.Contains("ยังไม่ได้ยืนยันกับสเปกกรมสรรพากร"));

        var ok = Pnd1FilingData.Build(Array.Empty<PayrollDetail>(), new[] { ManualCert("WHT-4", Valid, "TH") }, annual: false);
        Assert.Empty(ok.BlockedRows);
        Assert.Equal(Valid, Assert.Single(ok.Rows).PayeeTaxId);
        // ภาษี 0 ไม่ต้องรับทราบ (เส้นเดิม)
        Assert.Empty(Pnd1FilingData.Build(Array.Empty<PayrollDetail>(), new[] { ManualCert("WHT-5", null, "TH", tax: 0m) }, annual: false).BlockedRows);
    }

    // ───────── H-3 / F3 · ไฟล์ สปส. ตามเลข ปกส. ─────────

    private const string ValidSsn = "3100500123458";

    [Fact]
    public void Sso_เลขบัตรผิดแต่มีเลขประกันสังคมที่ถูกต้อง_ไม่กันไฟล์_ช่องเลขบัตรว่าง_ทิศตรงข้ามไม่มีเลขปกสยังกัน()
    {
        SsoInsuredNumber.ThrowIfUnusable("สปส.1-10", new[] { ("F001", (string?)"ต่างชาติ", (string?)"AB1234567", (string?)ValidSsn) });
        Assert.Equal("", SsoInsuredNumber.CitizenIdForFile("AB1234567"));
        Assert.Equal(Valid, SsoInsuredNumber.CitizenIdForFile(Valid));
        var ex = Assert.Throws<BusinessRuleException>(() => SsoInsuredNumber.ThrowIfUnusable("สปส.1-10",
            new[] { ("F002", (string?)"ไม่มีปกส", (string?)"AB1234567", (string?)null) }));
        Assert.Contains("F002", ex.Message);
        // ถอดรหัสไม่ได้ยังกันทั้งไฟล์แม้มีเลข ปกส. (ข้อมูลหาย ต้องให้คนเห็น)
        var cipher = EncryptionHelper.Encrypt(Valid, "lost-key-275e");
        Assert.Throws<BusinessRuleException>(() => SsoInsuredNumber.ThrowIfUnusable("สปส.1-10",
            new[] { ("F003", (string?)"หาย", (string?)cipher, (string?)ValidSsn) }));
    }

    /// <summary>ฝ่ายค้าน MED-1: เลข ปกส. ที่ปล่อยผ่านเลขบัตรผิดต้องเป็น 13 หลัก + mod-11 — เท่ากับเลขบัตรที่ผิด/checksum ผิด/ไม่ครบ 13 หลัก ⇒ กัน</summary>
    [Theory]
    [InlineData("1101700001238")]   // เท่ากับเลขบัตรที่ checksum ผิด
    [InlineData("1234567890123")]   // 13 หลัก checksum ผิด
    [InlineData("1234567890")]      // ไม่ครบ 13 หลัก
    [InlineData("SSO-ABC")]
    public void Sso_เลขปกสที่ไม่ผ่านรูปแบบ_ไม่ปล่อยเลขบัตรผิดผ่าน(string ssn)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => SsoInsuredNumber.ThrowIfUnusable("สปส.1-10",
            new[] { ("E010", (string?)"เลขผิด", (string?)BadChecksum, (string?)ssn) }));
        Assert.Contains("E010", ex.Message);
        Assert.False(SsoInsuredNumber.HasUsableSsn(ssn));
        Assert.True(SsoInsuredNumber.HasUsableSsn(ValidSsn));
    }

    // ───────── H-2 · เครื่องมือซ่อม + ทางไปต่อ ─────────

    [Theory]
    [InlineData("PND1-202609-E001", new[] { "E001" })]
    [InlineData("PND1-202609-E001-2", new[] { "E001-2", "E001" })]
    [InlineData("PND1-202609-HR-07", new[] { "HR-07", "HR" })]
    [InlineData("WHT-202609-0001", new string[0])]
    [InlineData("PND1-2026X9-E001", new string[0])]
    [InlineData(null, new string[0])]
    public void EmployeeCodeCandidates_จากเลขที่ใบเงินเดือน(string? no, string[] expected)
        => Assert.Equal(expected, PayrollJunkPayee.EmployeeCodeCandidates(no));

    [Fact]
    public void IsJunkTaxId_เลขขยะเท่านั้น_ว่างหรือถูกไม่ใช่()
    {
        Assert.True(PayrollJunkPayee.IsJunkTaxId("1234567"));
        Assert.True(PayrollJunkPayee.IsJunkTaxId(BadChecksum));
        Assert.False(PayrollJunkPayee.IsJunkTaxId(Valid));
        Assert.False(PayrollJunkPayee.IsJunkTaxId(null));
        Assert.False(PayrollJunkPayee.IsJunkTaxId(" - "));
    }

    [Fact]
    public void PayeeAction_งวดที่ยื่นแล้ว_ไม่สั่งสร้างเอกสารใหม่เป็นทางออก_ชี้เครื่องมือซ่อมและยื่นเพิ่มเติม()
    {
        var filed = EmployeeIdentityHealth.PayeeAction(fromPayroll: true, periodFiled: true);
        Assert.Contains("ถูกปฏิเสธ", filed);
        Assert.Contains("ผู้ติดต่อเลขผิดจากเงินเดือน", filed);
        Assert.Contains("ยื่น ภ.ง.ด.1 เพิ่มเติม", filed);
        var open = EmployeeIdentityHealth.PayeeAction(fromPayroll: true, periodFiled: false);
        Assert.Contains("สร้างเอกสารใหม่", open);
        Assert.DoesNotContain("ยื่น ภ.ง.ด.1 เพิ่มเติม", open);
        Assert.Contains("ยื่นแบบเพิ่มเติม", EmployeeIdentityHealth.PayeeAction(fromPayroll: false, periodFiled: true));
    }

    // ───────── M-3 · กรองตามแบบ/ประเทศ ─────────

    [Theory]
    [InlineData(TaxType.WithholdingTax1, "US", true)]
    [InlineData(TaxType.WithholdingTax3, "TH", true)]
    [InlineData(TaxType.WithholdingTax53, "", true)]
    [InlineData(TaxType.WithholdingTax53, "SG", false)]
    [InlineData(TaxType.WithholdingTax54, "TH", false)]
    [InlineData(TaxType.WithholdingTax2, "TH", false)]
    public void PayeeNeedsThaiId_ตามแบบและประเทศ(TaxType form, string country, bool expected)
        => Assert.Equal(expected, EmployeeIdentityHealth.PayeeNeedsThaiId(form, country));

    // ───────── M-6 · แถวนำเข้าถอดไม่ได้ ─────────

    [Fact]
    public void ImportJson_ค่าที่ถอดไม่ได้_ล้มด้วยข้อความไทย_ไม่ใช่JsonExceptionดิบ_ทิศตรงข้ามJSONปกติอ่านได้()
    {
        var cipher = EncryptionHelper.Encrypt("{\"name\":\"x\"}", "lost-import-key");
        var ex = Assert.Throws<BusinessRuleException>(() => ImportJson.Deserialize<Dictionary<string, string?>>(cipher));
        Assert.Equal(ImportJson.UnreadableRuleCode, ex.RuleCode);
        Assert.DoesNotContain(cipher, ex.Message);
        Assert.Equal("x", ImportJson.Deserialize<Dictionary<string, string?>>("{\"name\":\"x\"}")!["name"]);
        Assert.Null(ImportJson.Deserialize<Dictionary<string, string?>>(null));
    }

    // ───────── ฝ่ายค้าน LOW (ก)(ง)(ข)(ฉ) ─────────

    [Fact]
    public void IsPayrollCreated_กุญแจสร้างโดยเงินเดือน_ผู้ขายจริงหรือHRผูกเองไม่ใช่()
    {
        Assert.True(PayrollJunkPayee.IsPayrollCreated("system:payroll-pnd1", false, false, false, false));
        Assert.True(PayrollJunkPayee.IsPayrollCreated(null, false, true, false, false));   // ข้อมูลเก่าก่อนมีเครื่องหมาย
        Assert.False(PayrollJunkPayee.IsPayrollCreated(null, false, true, true, false));   // มีเอกสาร/ใบอื่นอ้าง = ผู้ขายจริง
        Assert.False(PayrollJunkPayee.IsPayrollCreated(null, false, true, false, true));   // HR ผูกกับพนักงานเอง
        Assert.False(PayrollJunkPayee.IsPayrollCreated(null, true, true, false, false));   // ลูกค้า
        Assert.False(PayrollJunkPayee.IsPayrollCreated("hr@company.test", false, true, false, false));   // ผู้ใช้สร้างเอง
    }

    [Fact]
    public void ShouldClear_ได้เลขถูกแล้วการรับทราบหมดผล_เลขผิดหรือว่างไม่ล้าง()
    {
        Assert.True(EmployeeTaxIdWaiver.ShouldClear(Ack, null, Valid));
        Assert.False(EmployeeTaxIdWaiver.ShouldClear(Ack, null, null));
        Assert.False(EmployeeTaxIdWaiver.ShouldClear(Ack, null, BadChecksum));
        Assert.False(EmployeeTaxIdWaiver.ShouldClear(null, null, Valid));
    }

    [Fact]
    public void WhtCertPayeeNote_อ่านจากสำเนาณวันออก_ปิดบังพาสปอร์ตเว้นแต่ขอเต็ม_ไม่มีธงไม่แสดง()
    {
        var contact = new Contact { Name = "John Smith", TaxId = null };
        var cert = new WithholdingTaxCert { TaxFormType = TaxType.WithholdingTax1, PayeeContact = contact,
            PayeeSnapshotJson = WhtPayeeSnapshot.Serialize(contact, "GB7654321", employeeWaived: true) };
        Assert.Contains("GB7654321", WhtCertPayeeNote.Of(cert, includePii: true));
        var masked = WhtCertPayeeNote.Of(cert, includePii: false)!;
        Assert.DoesNotContain("7654", masked);
        Assert.Contains("ไม่มีเลขประจำตัวผู้เสียภาษี 13 หลัก", masked);
        // ค่าสดเปลี่ยนภายหลังไม่กระทบ (อ่านจากสำเนา)
        contact.Name = "เปลี่ยนชื่อ";
        Assert.Contains("GB7654321", WhtCertPayeeNote.Of(cert, includePii: true));
        // ไม่มีธงรับทราบในสำเนา ⇒ ไม่แสดง · แบบอื่นไม่แสดง
        var plain = new WithholdingTaxCert { TaxFormType = TaxType.WithholdingTax1, PayeeContact = contact, PayeeSnapshotJson = WhtPayeeSnapshot.Serialize(contact) };
        Assert.Null(WhtCertPayeeNote.Of(plain, includePii: true));
        cert.TaxFormType = TaxType.WithholdingTax3;
        Assert.Null(WhtCertPayeeNote.Of(cert, includePii: true));
    }

    // ───────── L-7 · ชื่ออังกฤษ ─────────

    [Fact]
    public void PayeeName_ไม่มีชื่อไทยใช้ชื่ออังกฤษ_มีชื่อไทยคงเดิม()
    {
        Assert.Equal("John Smith", PayrollPnd1Certs.PayeeName(new Employee { EmployeeCode = "F1", FirstNameTh = "", FirstNameEn = "John", LastNameEn = "Smith" }));
        Assert.Equal("นาย สมชาย ใจดี", PayrollPnd1Certs.PayeeName(new Employee { EmployeeCode = "E1", TitleTh = "นาย", FirstNameTh = "สมชาย", LastNameTh = "ใจดี", FirstNameEn = "Somchai" }));
        Assert.Equal("F9", PayrollPnd1Certs.PayeeName(new Employee { EmployeeCode = "F9" }));
    }
}
