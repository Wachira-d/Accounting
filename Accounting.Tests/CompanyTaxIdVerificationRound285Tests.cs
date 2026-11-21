using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 285 — ตัวตัดสิน "บริษัทยืนยันเลขผู้เสียภาษีแล้ว" (<see cref="CompanyTaxIdVerification"/> · คำตัดสิน 285-1…285-6) ·
/// ล็อกทั้งทิศ "ยังไม่ยืนยัน = พฤติกรรมรอบ 280 เดิม" และ "ยืนยันแล้ว = เปิดคำขอย้อนหลังได้" · เทียบชื่อทะเบียนแบบตรงตัว (ไม่ fuzzy)
/// </summary>
public class CompanyTaxIdVerificationRound285Tests
{
    internal static string ValidTax(string twelve)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (twelve[i] - '0') * (13 - i);
        return twelve + ((11 - (sum % 11)) % 10);
    }

    private static readonly string Tax = ValidTax("010555600024");
    private static readonly DateTime T0 = new(2026, 11, 21, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ยืนยันแล้ว_ต้องมีเวลาและเลขที่ยืนยันตรงเลขปัจจุบัน()
    {
        Assert.True(CompanyTaxIdVerification.IsVerified(Tax, T0, Tax));
        Assert.True(CompanyTaxIdVerification.IsVerified(Tax[..1] + "-" + Tax[1..], T0, Tax));   // normalize
        Assert.False(CompanyTaxIdVerification.IsVerified(Tax, null, Tax));
        Assert.False(CompanyTaxIdVerification.IsVerified(Tax, T0, null));
        Assert.False(CompanyTaxIdVerification.IsVerified(ValidTax("010555600025"), T0, Tax));     // เลขเปลี่ยนนอกเส้นที่รู้จัก ⇒ ไม่ยืนยัน
        Assert.False(CompanyTaxIdVerification.IsVerified("-", T0, "-"));
    }

    [Fact]
    public void เปลี่ยนเลข_ล้างสามช่อง()
    {
        var c = new Company { Name = "x", TaxId = Tax, TaxIdVerifiedAt = T0, TaxIdVerifiedFor = Tax, TaxIdVerificationRequestId = Guid.NewGuid() };
        Assert.True(CompanyTaxIdVerification.IsVerified(c));
        CompanyTaxIdVerification.ClearOnTaxIdChange(c);
        Assert.False(CompanyTaxIdVerification.IsVerified(c));
        Assert.Null(c.TaxIdVerifiedAt);
        Assert.Null(c.TaxIdVerifiedFor);
        Assert.Null(c.TaxIdVerificationRequestId);
    }

    [Fact]
    public void จุดตัดคำขอ_ยืนยันแล้วเปิดทุกคำขอ_ยังไม่ยืนยันคงพฤติกรรม280()
    {
        Assert.Equal(DateTime.MinValue, CompanyTaxIdVerification.IncomingRequestCutoff(true, T0));
        Assert.Equal(DateTime.MinValue, CompanyTaxIdVerification.IncomingRequestCutoff(true, null));
        Assert.Equal(T0, CompanyTaxIdVerification.IncomingRequestCutoff(false, T0));   // ทิศตรงข้าม: เดิมไม่ถูกแตะ
        Assert.Null(CompanyTaxIdVerification.IncomingRequestCutoff(false, null));
    }

    [Theory]
    [InlineData("บริษัท ตัวอย่าง จำกัด", "ตัวอย่าง", CompanyTaxIdVerification.RegistryMatch)]
    [InlineData("ตัวอย่าง", "บริษัท ตัวอย่าง จำกัด (มหาชน)", CompanyTaxIdVerification.RegistryMatch)]
    [InlineData("บริษัท ตัวอย่าง จำกัด", "บริษัท ตัวอย่างดี จำกัด", CompanyTaxIdVerification.RegistryMismatch)]   // ไม่ fuzzy: ชื่อที่คล้ายไม่ผ่าน
    [InlineData("บริษัท ตัวอย่าง จำกัด", "บริษัท ตัวอย่าง เทรดดิ้ง จำกัด", CompanyTaxIdVerification.RegistryMismatch)]   // เป็นส่วนหนึ่งก็ไม่ผ่าน
    [InlineData("", "บริษัท ตัวอย่าง จำกัด", CompanyTaxIdVerification.RegistryMismatch)]
    [InlineData("บริษัท ตัวอย่าง จำกัด", null, CompanyTaxIdVerification.RegistryUnknown)]
    [InlineData("บริษัท ตัวอย่าง จำกัด", "  ", CompanyTaxIdVerification.RegistryUnknown)]
    public void เทียบชื่อทะเบียน_ตรงตัวหลังตัดคำนำหน้า(string mine, string? registry, string expected)
        => Assert.Equal(expected, CompanyTaxIdVerification.CompareRegistryName(mine, registry));

    [Fact]
    public void ยื่นได้_เฉพาะเลขถูกต้อง_ชนิดหลักฐานถูก_มีไฟล์_ชื่อไม่ขัดทะเบียน()
    {
        const string pp20 = CompanyTaxIdVerification.EvidencePp20;
        Assert.Null(CompanyTaxIdVerification.SubmitProblem(Tax, pp20, 100, false, false));
        Assert.Null(CompanyTaxIdVerification.SubmitProblem(Tax, CompanyTaxIdVerification.EvidenceCompanyCertificate, 100, false, false));
        Assert.Contains("13 หลัก", CompanyTaxIdVerification.SubmitProblem("0105556000240", pp20, 100, false, false) ?? "");   // checksum ผิด
        Assert.Contains("13 หลัก", CompanyTaxIdVerification.SubmitProblem("-", pp20, 100, false, false) ?? "");
        Assert.NotNull(CompanyTaxIdVerification.SubmitProblem(Tax, "Selfie", 100, false, false));
        Assert.NotNull(CompanyTaxIdVerification.SubmitProblem(Tax, pp20, 0, false, false));
        Assert.NotNull(CompanyTaxIdVerification.SubmitProblem(Tax, pp20, CompanyTaxIdVerification.MaxEvidenceBytes + 1, false, false));
        Assert.Contains("ยืนยันแล้ว", CompanyTaxIdVerification.SubmitProblem(Tax, pp20, 100, true, false) ?? "");
        Assert.Contains("รอ", CompanyTaxIdVerification.SubmitProblem(Tax, pp20, 100, false, true) ?? "");
        // ฝ่ายค้าน MED-B2: ชื่อไม่ตรงทะเบียนไม่บล็อกการยื่น (ติดธงให้ผู้ดูแลแทน)
        Assert.Contains("ชื่อไม่ตรงทะเบียน: บริษัท เจ้าของจริง จำกัด", CompanyTaxIdVerification.RegistryLabel(CompanyTaxIdVerification.RegistryMismatch, "บริษัท เจ้าของจริง จำกัด"));
    }

    [Fact]
    public void อนุมัติได้_เฉพาะรอตรวจ_เลขยังตรง_ชื่อไม่ขัด_มีไฟล์()
    {
        const string pending = CompanyTaxIdVerification.StatusPending;
        Assert.Null(CompanyTaxIdVerification.ApproveProblem(pending, Tax, Tax, CompanyTaxIdVerification.RegistryMatch, true, null, null));
        Assert.Null(CompanyTaxIdVerification.ApproveProblem(pending, Tax, Tax, CompanyTaxIdVerification.RegistryUnknown, true, null, null));
        Assert.Contains("เปลี่ยนเลข", CompanyTaxIdVerification.ApproveProblem(pending, Tax, ValidTax("010555600025"), CompanyTaxIdVerification.RegistryMatch, true) ?? "");
        Assert.NotNull(CompanyTaxIdVerification.ApproveProblem(CompanyTaxIdVerification.StatusApproved, Tax, Tax, CompanyTaxIdVerification.RegistryMatch, true, null, null));
        Assert.NotNull(CompanyTaxIdVerification.ApproveProblem(CompanyTaxIdVerification.StatusRejected, Tax, Tax, CompanyTaxIdVerification.RegistryMatch, true, null, null));
        Assert.NotNull(CompanyTaxIdVerification.ApproveProblem(pending, Tax, Tax, CompanyTaxIdVerification.RegistryMismatch, true, null, null));
        Assert.NotNull(CompanyTaxIdVerification.ApproveProblem(pending, Tax, Tax, CompanyTaxIdVerification.RegistryMatch, false, null, null));
    }

    [Fact]
    public void ฝ่ายค้านB1B2_เลขยืนยันแล้วโดยบริษัทอื่น_อนุมัติไม่ได้_ชื่อขัดทะเบียนอนุมัติได้เมื่อมีเหตุ()
    {
        const string pending = CompanyTaxIdVerification.StatusPending;
        Assert.Contains("ยืนยันแล้วโดยบริษัท เจ้าของเดิม", CompanyTaxIdVerification.ApproveProblem(pending, Tax, Tax, CompanyTaxIdVerification.RegistryMatch, true, "x", "เจ้าของเดิม") ?? "");
        Assert.Contains("ระบุเหตุผล", CompanyTaxIdVerification.ApproveProblem(pending, Tax, Tax, CompanyTaxIdVerification.RegistryMismatch, true, " ", null) ?? "");
        Assert.Null(CompanyTaxIdVerification.ApproveProblem(pending, Tax, Tax, CompanyTaxIdVerification.RegistryMismatch, true, "ตรวจแล้วเป็นชื่อการค้า", null));
        // สาขา: ว่าง = สำนักงานใหญ่ · คนละสาขา = คนละช่อง
        Assert.Equal(CompanyTaxIdVerification.SlotKey(Tax, null), CompanyTaxIdVerification.SlotKey(Tax, "00000"));
        Assert.NotEqual(CompanyTaxIdVerification.SlotKey(Tax, "00000"), CompanyTaxIdVerification.SlotKey(Tax, "00001"));
    }

    [Fact]
    public void ปฏิเสธถอน_ต้องมีเหตุ_และป้ายคู่ค้าบอกตามจริง()
    {
        Assert.NotNull(CompanyTaxIdVerification.ReasonProblem(null));
        Assert.NotNull(CompanyTaxIdVerification.ReasonProblem("  "));
        Assert.NotNull(CompanyTaxIdVerification.ReasonProblem(new string('ก', 1001)));
        Assert.Null(CompanyTaxIdVerification.ReasonProblem("เอกสารไม่ชัด"));
        Assert.Contains("ผ่านการยืนยัน", CompanyTaxIdVerification.PartnerTaxIdLabel(true));
        Assert.Contains("ยังไม่ได้ยืนยัน", CompanyTaxIdVerification.PartnerTaxIdLabel(false));
        Assert.Equal("ยังไม่ได้ยื่น", CompanyTaxIdVerification.StatusLabel(null));
    }
}
