using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 274 — บั๊ก production: ไฟล์ ภ.ง.ด.1/1ก ช่อง "เลขประจำตัวผู้เสียภาษี" ของพนักงานยาว 5/7/8/10/14 หลัก (บางแถว 13 หลักที่ไม่ใช่เลขจริง) และ
/// 50 ทวิ ภ.ง.ด.1ก พิมพ์กล่อง 13 หลักได้ 7 ตัว — หลังผู้ใช้ตั้ง <c>ENCRYPTION_KEY</c>
///
/// <para><b>สาเหตุ:</b> (1) <c>EncryptedColumnConverter</c> ถอดด้วยคีย์ปัจจุบันตัวเดียว ⇒ แถวที่เข้ารหัสด้วย dev key (ก่อนตั้งคีย์) ถอดไม่ออก ⇒ คืน ciphertext
/// <c>enc1:…</c> ทั้งก้อน · (2) <c>EmployeeTaxIdentity.Resolve</c> "เหลือเฉพาะตัวเลข" ⇒ ตัวเลขที่ปนใน base64 กลายเป็นเลขผู้เสียภาษี ·
/// (3) ไฟล์ยื่น "คงค่าที่ผิดรูปไว้" + 50 ทวิ เติมกล่องเท่าที่มีตัวเลข · เทสต์ชุดนี้ reproduce ก่อน แล้วล็อกการแก้ทั้งสามชั้น + การหมุนคีย์สองทิศ</para>
/// </summary>
public class PiiKeyRotationRound274Tests
{
    // ค่าทดสอบเท่านั้น
    private const string OldKey = "r274-old-key-0123456789";
    private const string NewKey = "r274-new-key-9876543210";
    private const string ValidId = "1101700001237";      // ผ่าน mod-11
    private const string BadChecksum = "1101700001238";  // 13 หลัก หลักสุดท้ายผิด
    private static readonly Regex Thirteen = new("^[0-9]{13}$");

    // ───────── reproduce ─────────

    [Fact]
    public void reproduce_คีย์ใหม่ถอดแถวคีย์เดิมไม่ออก_และตัวตัดเลขเดิมได้ตัวเลขสุ่มจาก_ciphertext()
    {
        var stored = EncryptionHelper.Encrypt(ValidId, EncryptionKeyResolver.DevKey);   // บันทึกก่อนตั้งคีย์ (dev key)
        // ก่อนแก้: converter มีแต่คีย์ใหม่ ⇒ ถอดไม่ออก ⇒ ค่าที่ EF คืน = ciphertext ทั้งก้อน
        var read = EncryptedColumnConverter.Inspect(stored, NewKey, Array.Empty<string>());
        Assert.Equal(PiiCipherState.Undecryptable, read.State);
        Assert.Equal(stored, read.Plain);
        // ตัวตัดเลขเดิม (Normalize) ⇒ ไม่ใช่เลขจริง (คือรูปของบั๊กที่ผู้ใช้เห็น: จำนวนหลักสุ่ม)
        Assert.NotEqual(ValidId, ThaiTaxId.Normalize(read.Plain));
        // หลังแก้: resolver ไม่คืนตัวเลขจาก ciphertext อีก
        var id = EmployeeTaxIdentity.Inspect(null, read.Plain);
        Assert.Equal(TaxIdProblem.Undecryptable, id.Problem);
        Assert.Null(id.Value);
        Assert.Null(EmployeeTaxIdentity.Resolve(null, read.Plain));
        Assert.Contains(EmployeeTaxIdentity.UndecryptableText, id.Reason);
        Assert.DoesNotContain(stored, id.Reason!);   // ไม่เผย ciphertext
    }

    [Fact]
    public void reproduce_ตัวอย่างจำนวนหลักที่ตัดจาก_ciphertext_ไม่ใช่13หลักเสมอ()
    {
        // ยืนยันว่ารูปความยาวสุ่มที่ผู้ใช้เห็น (5–14 หลัก) มาจากกลไกนี้ — 50 ตัวอย่างต้องมีความยาวมากกว่าหนึ่งแบบ และเกือบทั้งหมดไม่ใช่เลขที่ถูกต้อง
        var lengths = Enumerable.Range(0, 50)
            .Select(_ => ThaiTaxId.Normalize(EncryptionHelper.Encrypt(ValidId, EncryptionKeyResolver.DevKey)).Length)
            .Distinct().Count();
        Assert.True(lengths > 1);
    }

    // ───────── หมุนคีย์สองทิศ ─────────

    [Fact]
    public void หมุนคีย์_เดิมไปใหม่_ถอดด้วยคีย์เดิมได้_แล้วเข้ารหัสใหม่ด้วยคีย์ปัจจุบัน()
    {
        var stored = EncryptionHelper.Encrypt(ValidId, OldKey);
        var r = EncryptedColumnConverter.Inspect(stored, NewKey, new[] { OldKey });
        Assert.Equal(PiiCipherState.Previous, r.State);
        Assert.Equal(ValidId, r.Plain);
        Assert.Equal(0, r.KeyIndex);

        var rekeyed = EncryptedColumnConverter.ProtectWithCurrentKey(r.Plain!, NewKey);
        var after = EncryptedColumnConverter.Inspect(rekeyed, NewKey, new[] { OldKey });
        Assert.Equal(PiiCipherState.Current, after.State);
        Assert.Equal(ValidId, after.Plain);
        // ย้ายแล้วถอดคีย์เดิมออกได้โดยข้อมูลไม่หาย
        Assert.Equal(ValidId, EncryptedColumnConverter.Inspect(rekeyed, NewKey, Array.Empty<string>()).Plain);
    }

    [Fact]
    public void หมุนคีย์_ย้อนกลับ_ใหม่ไปเดิม_ทิศตรงข้ามก็ถอดได้()
    {
        // ผู้ดูแลถอยคีย์กลับ (rollback) — แถวที่เข้ารหัสด้วยคีย์ใหม่ระหว่างนั้นต้องยังอ่านได้เมื่อคีย์ใหม่ไปอยู่ในรายการคีย์เดิม
        var writtenWithNew = EncryptionHelper.Encrypt(ValidId, NewKey);
        var writtenWithOld = EncryptionHelper.Encrypt(BadChecksum, OldKey);
        var a = EncryptedColumnConverter.Inspect(writtenWithNew, OldKey, new[] { NewKey });
        Assert.Equal(PiiCipherState.Previous, a.State);
        Assert.Equal(ValidId, a.Plain);
        var b = EncryptedColumnConverter.Inspect(writtenWithOld, OldKey, new[] { NewKey });
        Assert.Equal(PiiCipherState.Current, b.State);
        Assert.Equal(BadChecksum, b.Plain);
    }

    [Fact]
    public void ไม่มีคีย์ไหนถอดได้_คืนค่าดิบเป็น_Undecryptable_ไม่ใช่_plaintext()
    {
        var stored = EncryptionHelper.Encrypt(ValidId, "lost-key");
        var r = EncryptedColumnConverter.Inspect(stored, NewKey, new[] { OldKey, EncryptionKeyResolver.DevKey });
        Assert.Equal(PiiCipherState.Undecryptable, r.State);
        // ทิศตรงข้าม: plaintext เก่า/ค่าว่าง ไม่ถูกแตะ
        Assert.Equal(PiiCipherState.Plaintext, EncryptedColumnConverter.Inspect(ValidId, NewKey, new[] { OldKey }).State);
        Assert.Equal(PiiCipherState.Empty, EncryptedColumnConverter.Inspect(null, NewKey, new[] { OldKey }).State);
    }

    [Fact]
    public void PiiPreviousKeys_ลำดับ_ENCRYPTION_KEY_PREVIOUS_ก่อน_แล้ว_config_และ_dev_key_ไม่มีคีย์ปัจจุบัน()
    {
        var keys = EncryptionKeyResolver.PiiPreviousKeys(NewKey, OldKey, envKey: NewKey, configKey: "cfg-key");
        Assert.Equal(new[] { OldKey, "cfg-key", EncryptionKeyResolver.DevKey }, keys);
        Assert.DoesNotContain(NewKey, keys);
        // ไม่ตั้งคีย์เดิม = ยังลอง config/dev key (กรณีผู้ใช้: ข้อมูลบันทึกก่อนตั้ง ENCRYPTION_KEY)
        Assert.Equal(new[] { EncryptionKeyResolver.DevKey }, EncryptionKeyResolver.PiiPreviousKeys(NewKey, null, NewKey, null));
        // ช่องว่างล้วน = ไม่มี
        Assert.Equal(new[] { EncryptionKeyResolver.DevKey }, EncryptionKeyResolver.PiiPreviousKeys(NewKey, "   ", NewKey, ""));
    }

    [Theory]
    [InlineData("changeme")]
    [InlineData(" CHANGE_ME ")]
    [InlineData("your-encryption-key")]
    public void production_คีย์_placeholder_บูตไม่ขึ้น_เครื่องนักพัฒนาใช้ตามเดิม(string key)
    {
        Assert.Throws<InvalidOperationException>(() => EncryptionKeyResolver.RequireUsable(key, isProduction: true, "ทดสอบ"));
        Assert.Equal(key, EncryptionKeyResolver.RequireUsable(key, isProduction: false, "ทดสอบ"));
    }

    [Fact]
    public void production_คีย์ว่างหรือ_dev_key_บูตไม่ขึ้น_คีย์จริงผ่าน()
    {
        Assert.Throws<InvalidOperationException>(() => EncryptionKeyResolver.RequireUsable("", true, "ทดสอบ"));
        Assert.Throws<InvalidOperationException>(() => EncryptionKeyResolver.RequireUsable(EncryptionKeyResolver.DevKey, true, "ทดสอบ"));
        Assert.Equal(NewKey, EncryptionKeyResolver.RequireUsable(NewKey, true, "ทดสอบ"));
    }

    // ───────── resolver ─────────

    [Theory]
    [InlineData(null, ValidId, TaxIdProblem.None)]
    [InlineData(null, "1-1017-00001-23-7", TaxIdProblem.None)]
    [InlineData(null, BadChecksum, TaxIdProblem.Invalid)]
    [InlineData(null, "1101700", TaxIdProblem.Invalid)]            // 7 หลัก (รูปที่ผู้ใช้เห็นบน 50 ทวิ)
    [InlineData(null, "11017000012371", TaxIdProblem.Invalid)]     // 14 หลัก
    [InlineData(null, null, TaxIdProblem.Missing)]
    [InlineData("-", ValidId, TaxIdProblem.None)]                  // ช่อง TaxId ไม่มีตัวเลข = ว่าง ⇒ ใช้เลขบัตร (เหมือนเดิม)
    [InlineData(BadChecksum, ValidId, TaxIdProblem.Invalid)]       // TaxId ที่กรอกเองผิด ⇒ ต้องแก้ ไม่แทนเงียบด้วยเลขบัตร
    public void Inspect_คืนค่าเฉพาะเลข13หลักที่ผ่าน_mod11(string? taxId, string? citizenId, TaxIdProblem expected)
    {
        var r = EmployeeTaxIdentity.Inspect(taxId, citizenId);
        Assert.Equal(expected, r.Problem);
        if (expected == TaxIdProblem.None) Assert.Matches(Thirteen, r.Value!);
        else Assert.Null(r.Value);
    }

    [Fact]
    public void Display_ใช้ได้แสดงเต็มเฉพาะ_PiiView_ใช้ไม่ได้แสดงป้ายเหตุไม่เผยตัวเลข()
    {
        Assert.Equal(ValidId, EmployeeTaxIdentity.Display(EmployeeTaxIdentity.InspectValue(ValidId), includePii: true));
        Assert.Equal("1-XXXX-XXXXX-XX-7", EmployeeTaxIdentity.Display(EmployeeTaxIdentity.InspectValue(ValidId), includePii: false));
        var cipher = EncryptionHelper.Encrypt(ValidId, "lost-key");
        Assert.StartsWith("⛔", EmployeeTaxIdentity.Display(EmployeeTaxIdentity.InspectValue(cipher), true));
        var bad = EmployeeTaxIdentity.Display(EmployeeTaxIdentity.InspectValue("1101700"), true)!;
        Assert.DoesNotContain("1101700", bad);
    }

    // ───────── ไฟล์ ภ.ง.ด.1 / 1ก ─────────

    private static readonly DateTime PayDay = new(2026, 9, 30);

    private static PayrollDetail Detail(string code, string? citizenId, decimal tax = 500m)
    {
        var run = new PayrollRun { Year = 2026, Month = 9, PayDate = PayDay, Status = PayrollRunFilingScope.Paid };
        var emp = new Employee { EmployeeCode = code, TitleTh = "นาย", FirstNameTh = "ทดสอบ" + code, LastNameTh = "สกุล",
            CitizenId = citizenId, Address = "1 ถนนสุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพมหานคร 10110" };
        return new PayrollDetail { Employee = emp, EmployeeId = emp.Id, PayrollRun = run, GrossIncome = 30000m, TaxableGross = 30000m, WithholdingTax = tax };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ไฟล์ภงด1และ1ก_กันแถวที่ถอดรหัสไม่ได้และเลขผิด_แถวที่เหลือ13หลักทุกแถว(bool annual)
    {
        var cipher = EncryptionHelper.Encrypt(ValidId, "lost-key");   // ค่าที่ EF คืนเมื่อถอดไม่ออก
        var details = new[]
        {
            Detail("E001", ValidId),
            Detail("E002", cipher, 700m),
            Detail("E003", BadChecksum, 300m),
            Detail("E004", "3100500123458"),
        };
        var data = Pnd1FilingData.Build(details, Array.Empty<WithholdingTaxCert>(), annual);

        Assert.Equal(new[] { "E001", "E004" }, data.Rows.Select(r => r.SourceRef).ToArray());
        Assert.Equal(new[] { "E002", "E003" }, data.BlockedRows.Select(b => b.SourceRef).ToArray());
        Assert.Equal(TaxIdProblem.Undecryptable, data.BlockedRows[0].Problem);
        Assert.Equal(TaxIdProblem.Invalid, data.BlockedRows[1].Problem);
        Assert.Contains(data.Notes, n => n.Contains("E002") && n.Contains(EmployeeTaxIdentity.UndecryptableText));
        Assert.Contains(data.Notes, n => n.Contains("E003") && n.Contains(EmployeeTaxIdentity.InvalidText));
        Assert.DoesNotContain(data.Notes, n => n.Contains(cipher) || n.Contains(BadChecksum));   // เลข/ciphertext ไม่หลุดลงข้อความ

        // ทุกแถวของไฟล์: ช่อง 5 = 13 หลักพอดี
        var filer = new Pnd1FileFormat.Filer("0105561012346", "00000", 2026, annual ? null : 9);
        foreach (var r in data.Rows)
            Assert.Matches(Thirteen, Pnd1FileFormat.Fields(r, filer)[4]);
        // ยอดในไฟล์ไม่รวมแถวที่ถูกกัน
        Assert.Equal(1000m, data.TotalTax);
    }

    [Fact]
    public void ไฟล์ยื่น_ชั้นสุดท้าย_แถวเลขผิดรูปที่ข้ามตัวโหลดมาต้องล้มดัง_ไม่เขียนเลขผิด()
    {
        var row = new Pnd1FileFormat.Row(Category: Pnd1FileFormat.GeneralSalary, PayeeTaxId: "1101700", Title: "นาย", FirstName: "ก", LastName: "ข",
            PayDate: PayDay, IncomeAmount: 100m, TaxAmount: 1m, Condition: 1, Address: "1 กรุงเทพมหานคร 10110", SourceRef: "E9", TaxRate: null);
        var filer = new Pnd1FileFormat.Filer("0105561012346", "00000", 2026, 9);
        var ex = Assert.Throws<BusinessRuleException>(() => Pnd1FileFormat.Fields(row, filer));
        Assert.Equal("RD-PND1-PAYEE-TAXID", ex.RuleCode);
        // ทิศตรงข้าม: ไม่มีเลข (ว่าง) ⇒ เติม 0 13 หลักตามสเปกเหมือนเดิม · เลขถูกผ่าน
        Assert.Equal(new string('0', 13), Pnd1FileFormat.Fields(row with { PayeeTaxId = null }, filer)[4]);
        Assert.Equal(ValidId, Pnd1FileFormat.Fields(row with { PayeeTaxId = ValidId }, filer)[4]);
    }

    // ───────── 50 ทวิ ─────────

    [Fact]
    public void กล่อง13หลักของ50ทวิ_เลขใช้ไม่ได้เป็นกล่องว่าง_ไม่เติมเท่าที่มีตัวเลข()
    {
        Assert.Equal(ValidId, WhtCertFormText.TaxIdBoxDigits("1-1017-00001-23-7"));
        Assert.Equal(new string(' ', 13), WhtCertFormText.TaxIdBoxDigits("1101700"));                                  // รูปที่ผู้ใช้เห็น: 7 ตัวแล้วว่าง
        Assert.Equal(new string(' ', 13), WhtCertFormText.TaxIdBoxDigits(EncryptionHelper.Encrypt(ValidId, "lost-key")));
        Assert.Equal(new string(' ', 13), WhtCertFormText.TaxIdBoxDigits(null));
        // ทิศตรงข้าม: ผู้จ่าย (บริษัทเอง) คงพฤติกรรมเดิม — ไม่ถูกแตะด้วยด่านผู้ถูกหัก
        Assert.Equal("0105556000005", WhtCertFormText.TaxIdBoxDigits("0105556000005", payee: false));
        Assert.Null(WhtCertFormText.PayeeTaxIdProblem("PND1-202609-E001", ValidId));
        Assert.Null(WhtCertFormText.PayeeTaxIdProblem("X", null));                         // ว่าง = ด่านอื่นตัดสิน (คงเดิม)
        Assert.Contains("สร้างเอกสารใหม่", WhtCertFormText.PayeeTaxIdProblem("PND1-202609-E001", "1101700"));
    }

    [Fact]
    public void ร่าง_html_50ทวิ_ผู้รับเลข7หลัก_ไม่พิมพ์ตัวเลขลงกล่อง()
    {
        var cert = new WithholdingTaxCert
        {
            CertificateNumber = "PAYROLL-2026-E001", TaxFormType = Accounting.Models.Enums.TaxType.WithholdingTax1, TaxYear = 2026, TaxMonth = 12,
            PayeeContact = new Contact { Name = "นายทดสอบ", TaxId = "9876543" },
        };
        cert.Lines.Add(new WithholdingTaxCertLine { LineOrder = 1, IncomeTypeCode = "1", IncomeDescription = "เงินเดือน", PaymentDate = PayDay, IncomeAmount = 100m, TaxAmount = 1m });
        var html = Accounting.Services.Implementations.PdfGenerationService.BuildWithholdingTaxCertHtml(cert,
            new Company { Name = "บริษัททดสอบ", TaxId = "0105561012346" }, null, null);
        Assert.DoesNotContain("<span class='tb'>9</span><span class='tb'>8</span>", html);
        Assert.Contains("<span class='tb'>0</span><span class='ts'>-</span><span class='tb'>1</span>", html);   // ผู้จ่าย (เลขถูก) ยังพิมพ์ครบ
    }

    [Fact]
    public void รายงานแถวเสีย_Classify_แยกถอดไม่ได้_ซ้อน_รอย้ายคีย์_เลขผิด()
    {
        Assert.Equal(EmployeeIdentityHealth.Issue.Undecryptable,
            EmployeeIdentityHealth.Classify(EncryptionHelper.Encrypt(ValidId, "lost-key"), NewKey, new[] { OldKey })!.Value.Issue);
        Assert.Equal(EmployeeIdentityHealth.Issue.PendingRekey,
            EmployeeIdentityHealth.Classify(EncryptionHelper.Encrypt(ValidId, OldKey), NewKey, new[] { OldKey })!.Value.Issue);
        var nested = EncryptionHelper.Encrypt(EncryptionHelper.Encrypt(ValidId, "lost-key"), NewKey);
        Assert.Equal(EmployeeIdentityHealth.Issue.NestedCipher, EmployeeIdentityHealth.Classify(nested, NewKey, new[] { OldKey })!.Value.Issue);
        Assert.Equal(EmployeeIdentityHealth.Issue.Invalid,
            EmployeeIdentityHealth.Classify(EncryptionHelper.Encrypt(BadChecksum, NewKey), NewKey, new[] { OldKey })!.Value.Issue);
        // ทิศตรงข้าม: ค่าถูก/ว่าง/plaintext ถูก ⇒ ไม่อยู่ในรายงาน
        Assert.Null(EmployeeIdentityHealth.Classify(EncryptionHelper.Encrypt(ValidId, NewKey), NewKey, new[] { OldKey }));
        Assert.Null(EmployeeIdentityHealth.Classify(null, NewKey, new[] { OldKey }));
        Assert.Null(EmployeeIdentityHealth.Classify(ValidId, NewKey, new[] { OldKey }));
    }

    // ───────── ทางออกอื่นที่อ่านคอลัมน์ PII ชุดเดียวกัน (ไฟล์ สปส. · ไฟล์โอนเงินเดือน) ─────────

    [Fact]
    public void ไฟล์สปส_เลขประกันสังคมถอดรหัสไม่ได้_ไม่ลงไฟล์ทั้งก้อน_ถอยไปเลขบัตรที่ถูก()
    {
        var cipher = EncryptionHelper.Encrypt(ValidId, "lost-key");
        Assert.Equal((ValidId, true), SsoInsuredNumber.Resolve(cipher, ValidId));
        Assert.Equal(("", false), SsoInsuredNumber.Resolve(cipher, cipher));
        Assert.Equal(("", false), SsoInsuredNumber.Resolve(null, cipher));
        Assert.Equal("", SsoInsuredNumber.CitizenIdForFile(cipher));
        Assert.Equal(ValidId, SsoInsuredNumber.CitizenIdForFile(ValidId));
        // ทิศตรงข้าม: เลขประกันสังคมที่กรอกไว้ (ถอดได้) ใช้ตามเดิม
        Assert.Equal(("1234567890", false), SsoInsuredNumber.Resolve("1234567890", ValidId));
    }

    [Fact]
    public void ไฟล์โอนเงินเดือน_เลขบัญชีถอดรหัสไม่ได้_ไม่พร้อมโอน_ไม่ตัดตัวเลขจาก_ciphertext()
    {
        var cipher = EncryptionHelper.Encrypt("1234567890", "lost-key");
        var r = PayrollBankTransfer.Evaluate("Paid", 10000m, 0m, false, cipher, paymentSourceIsBank: true);
        Assert.False(r.Ready);
        Assert.Equal(PayrollBankTransfer.UndecryptableStatus, r.Status);
        Assert.Equal("", r.AccountDigits);
        // ทิศตรงข้าม: เลขบัญชีปกติพร้อมโอนเหมือนเดิม
        Assert.True(PayrollBankTransfer.Evaluate("Paid", 10000m, 0m, false, "123-4-56789-0", true).Ready);
    }

    // ───────── ผลตรวจฝ่ายค้านรอบ 274 ─────────

    [Fact]
    public void ฝ่ายค้าน_HIGH1_ข้อความปฏิเสธไฟล์_มีรายชื่อ_เลขปิดบัง_ยอดภาษีที่ขาด_ไม่มีciphertext()
    {
        var cipher = EncryptionHelper.Encrypt(ValidId, "lost-key");
        var emp = new Employee { EmployeeCode = "E009", FirstNameTh = "เลข", LastNameTh = "หาย", CitizenId = cipher };
        var d = new PayrollDetail { Employee = emp, GrossIncome = 30000m, TaxableGross = 30000m, WithholdingTax = 375m,
            PayrollRun = new PayrollRun { Year = 2026, Month = 9, Status = PayrollRunFilingScope.Paid, PayDate = new DateTime(2026, 9, 30) } };
        var data = Pnd1FilingData.Build(new[] { d }, Array.Empty<WithholdingTaxCert>(), annual: false);
        var b = Assert.Single(data.BlockedRows);
        var msg = Pnd1FilingData.BlockedMessage("ภ.ง.ด.1", "เดือน 9/2026", data.BlockedRows);
        Assert.Contains("E009", msg);
        Assert.Contains("375.00", msg);
        Assert.Contains(EmployeeTaxIdentity.UndecryptableText, msg);
        Assert.DoesNotContain(cipher, msg);
        Assert.DoesNotContain(ValidId, msg);
        // ข้อความไปถึงแบนเนอร์ของรอบเงินเดือน (ทางแนบอัตโนมัติ)
        Assert.Contains("E009", PayrollFilingAttachmentNotice.Pnd1Missing(new BusinessRuleException(msg, Pnd1FilingData.BlockedRuleCode, 422)));
    }

    [Fact]
    public void ฝ่ายค้าน_MED1_ciphertextรุ่นก่อน_ไม่มีคำนำหน้า_ถอดไม่ได้_เป็นUndecryptable_ไม่ใช่Plaintext()
    {
        var legacy = EncryptionHelper.Encrypt(ValidId, "lost-key")[EncryptionHelper.CipherPrefix.Length..];
        Assert.Equal(PiiCipherState.Undecryptable, EncryptedColumnConverter.Inspect(legacy, NewKey, new[] { OldKey }).State);
        Assert.True(EncryptedColumnConverter.IsUnreadable(legacy));
        Assert.Equal(TaxIdProblem.Undecryptable, EmployeeTaxIdentity.InspectValue(legacy).Problem);
        Assert.False(PayrollBankTransfer.Evaluate("Paid", 1000m, 0m, false, legacy, true).Ready);
        Assert.Equal(("", false), SsoInsuredNumber.Resolve(legacy, legacy));
        // ถอดได้ด้วยคีย์เดิม = Previous (ย้ายได้)
        var legacyOld = EncryptionHelper.Encrypt(ValidId, OldKey)[EncryptionHelper.CipherPrefix.Length..];
        var r = EncryptedColumnConverter.Inspect(legacyOld, NewKey, new[] { OldKey });
        Assert.Equal((PiiCipherState.Previous, ValidId), (r.State, r.Plain));
        // ทิศตรงข้าม: ค่าธรรมดา (เลขสั้น · JSON) ไม่ใช่ ciphertext
        Assert.Equal(PiiCipherState.Plaintext, EncryptedColumnConverter.Inspect(ValidId, NewKey, new[] { OldKey }).State);
        Assert.Equal(PiiCipherState.Plaintext, EncryptedColumnConverter.Inspect("{\"a\":\"0123456789012345678901234567890123456789\"}", NewKey, Array.Empty<string>()).State);
        Assert.False(EncryptedColumnConverter.IsUnreadable("123-4-56789-0"));
    }

    [Fact]
    public void ฝ่ายค้าน_MED2_ทางไปต่อของแถวรอย้ายคีย์_ตามผลรันจริง()
    {
        Assert.Contains("ยังไม่เคยรัน", EmployeeIdentityHealth.RekeyAction(null));
        Assert.Contains("สำเร็จ", EmployeeIdentityHealth.RekeyAction(new(new DateTime(2026, 11, 19, 1, 0, 0), true, "ok")));
        var failed = EmployeeIdentityHealth.RekeyAction(new(new DateTime(2026, 11, 19, 1, 0, 0), false, "ล้ม: timeout"));
        Assert.Contains("⛔", failed);
        Assert.Contains("timeout", failed);
        Assert.DoesNotContain("ไม่ต้องทำอะไร", failed);
        // คอลัมน์ที่ไม่ใช่เลขผู้เสียภาษี: ไม่ตรวจ 13 หลัก
        Assert.Null(EmployeeIdentityHealth.Classify(EncryptionHelper.Encrypt("1234567890", NewKey), NewKey, new[] { OldKey }, taxIdColumn: false));
        Assert.Equal(EmployeeIdentityHealth.Issue.PendingRekey,
            EmployeeIdentityHealth.Classify(EncryptionHelper.Encrypt("1234567890", OldKey), NewKey, new[] { OldKey }, taxIdColumn: false)!.Value.Issue);
    }

    [Fact]
    public void ฝ่ายค้าน_LOW3_ส่งออก_ไม่ส่งciphertextดิบ()
    {
        var cipher = EncryptionHelper.Encrypt(ValidId, "lost-key");
        Assert.Equal(EncryptedColumnConverter.UnreadableExportText, EncryptedColumnConverter.ForExport(cipher));
        Assert.Equal(ValidId, EncryptedColumnConverter.ForExport(ValidId));
        Assert.Null(EncryptedColumnConverter.ForExport(null));
    }

    [Fact]
    public void ฝ่ายค้าน_LOW5_ไฟล์สปส_เลขบัตรใช้ไม่ได้_ปฏิเสธพร้อมรายชื่อ_ทิศตรงข้ามเลขถูกหรือว่างผ่าน()
    {
        var cipher = EncryptionHelper.Encrypt(ValidId, "lost-key");
        var ex = Assert.Throws<BusinessRuleException>(() => SsoInsuredNumber.ThrowIfUnusable("สปส.1-10",
            new[] { ("E001", (string?)"ถูก", (string?)ValidId, (string?)null), ("E002", "หาย", cipher, null), ("E003", "ผิด", BadChecksum, null) }));
        Assert.Equal(SsoInsuredNumber.UnusableRuleCode, ex.RuleCode);
        Assert.Contains("E002", ex.Message);
        Assert.Contains("E003", ex.Message);
        Assert.DoesNotContain("E001", ex.Message);
        Assert.DoesNotContain(cipher, ex.Message);
        SsoInsuredNumber.ThrowIfUnusable("สปส.1-10", new[] { ("E001", (string?)"ถูก", (string?)ValidId, (string?)null), ("E004", "ว่าง", (string?)null, (string?)null) });
    }
}
