using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบ ETL (2026-10-14 · IMPORT_ETL_REVIEW §6) — ตัวช่วยแบบ pure: ปิดบังช่องอ่อนไหวของการนำเข้า (<see cref="ImportPiiFields"/>) ·
/// ยอดติดลบจากไฟล์แถวผิดนำเข้ากลับได้ (<see cref="ImportValueNormalizer"/> × <see cref="CsvFieldSafety"/>) · บัญชีที่ย้อนยอดยกมาไม่ได้เพราะแก้มือ
/// (<see cref="MigrationWizardService.RollbackSkippedAccountIds"/>) — ทุกกลุ่มล็อกสองทิศ (ค่าที่ต้องเปลี่ยน กับค่าที่ต้องไม่ถูกแตะ)
/// </summary>
public class ImportAdversarialEtlHelpersTests
{
    // ═══ PII (PDPA ม.26) ═══

    [Fact]
    public void ทะเบียนพนักงาน_เลขบัตร_เลขบัญชี_เงินเดือน_ถูกปิดบัง()
    {
        Assert.True(ImportPiiFields.HasPii("employees"));
        Assert.Equal("1-XXXX-XXXXX-XX-0", ImportPiiFields.Mask("employees", "CitizenId", "1101700203450"));
        Assert.Equal("1-XXXX-XXXXX-XX-0", ImportPiiFields.Mask("employees", "SocialSecurityNumber", "1-1017-00203-45-0"));
        Assert.Equal("XXXXX67890", ImportPiiFields.Mask("employees", "BankAccountNumber", "123-4-56789-0"));
        Assert.DoesNotContain("25", ImportPiiFields.Mask("employees", "BaseSalary", "25000.00"));
        // คอลัมน์ที่ไม่ได้จับคู่ของไฟล์พนักงาน (อาจเป็นเลขบัตร) ⇒ ปิดตามรูปค่า
        Assert.Equal("1-XXXX-XXXXX-XX-0", ImportPiiFields.Mask("employees", null, "1101700203450"));
    }

    [Fact]
    public void ช่องที่ไม่อ่อนไหว_และชนิดที่ไม่มี_PII_ไม่ถูกแตะ()
    {
        Assert.Equal("สมชาย", ImportPiiFields.Mask("employees", "FirstNameTh", "สมชาย"));
        Assert.Equal("ฝ่ายขาย", ImportPiiFields.Mask("employees", null, "ฝ่ายขาย"));
        Assert.False(ImportPiiFields.HasPii("contacts"));
        Assert.False(ImportPiiFields.IsPii("contacts", "TaxId"));
        Assert.Equal("0105556000001", ImportPiiFields.Mask("contacts", "TaxId", "0105556000001"));
        Assert.Null(ImportPiiFields.Mask("employees", "CitizenId", null));
    }

    [Fact]
    public void ตัวอย่างค่าที่ส่ง_AI_เหลือแค่รูป_แต่ข้อความธุรกิจคงเดิม()
    {
        // พนักงาน: ทุกคอลัมน์ที่ยังไม่รู้ชนิด ⇒ ตัวเลขเป็น 9 (ยังเห็นว่าเป็นยอด/วันที่/เลข 13 หลัก)
        Assert.Equal("99,999.99", ImportPiiFields.RedactForPrompt("employees", null, "25,000.00"));
        Assert.Equal("9999999999999", ImportPiiFields.RedactForPrompt("employees", null, "1101700203450"));
        Assert.Equal("99/99/9999", ImportPiiFields.RedactForPrompt("employees", null, "01/01/2569"));
        Assert.Equal("xxxxxxx@example.com", ImportPiiFields.RedactForPrompt("employees", "Email", "somchai@example.com"));
        Assert.Equal("สมชาย", ImportPiiFields.RedactForPrompt("employees", "FirstNameTh", "สมชาย"));   // ช่องที่ไม่อ่อนไหวของพนักงานคงเดิม
        // ชนิดอื่นคงเดิม — นักเรียนตรวจข้อมูลอ่านค่าจริง (checksum เลขผู้เสียภาษี) จาก prompt เดียวกัน
        Assert.Equal("1101700203450", ImportPiiFields.RedactForPrompt("contacts", null, "1101700203450"));
        Assert.Equal("0105556000001", ImportPiiFields.RedactForPrompt("contacts", "TaxId", "0105556000001"));
        Assert.Equal("บริษัท สยามทดสอบ จำกัด", ImportPiiFields.RedactForPrompt("contacts", null, "บริษัท สยามทดสอบ จำกัด"));
        Assert.Equal("1,234.50", ImportPiiFields.RedactForPrompt("journal-entries", "DebitAmount", "1,234.50"));
    }

    // ═══ ไฟล์แถวผิด → นำเข้ากลับ ═══

    [Fact]
    public void ยอดติดลบที่ไฟล์แถวผิดเติม_อัญประกาศ_นำเข้ากลับได้ค่าเดิม()
    {
        var escaped = CsvFieldSafety.Escape("-1500.00");
        Assert.Equal("'-1500.00", escaped);                                      // ความปลอดภัยของ CSV ยังอยู่ (ไม่แตะ CsvFieldSafety)
        var back = ImportValueNormalizer.Amount(escaped);
        Assert.True(back.Ok);
        Assert.Equal(-1500.00m, back.Value);
        var n = ImportValueNormalizer.Number("'-3");
        Assert.True(n.Ok);
        Assert.Equal(-3m, n.Value);
    }

    [Fact]
    public void อัญประกาศหน้าข้อความที่ไม่ใช่ตัวเลข_ยังอ่านไม่ได้_และตัวเลขปกติไม่ถูกแตะ()
    {
        Assert.False(ImportValueNormalizer.Amount("'=SUM(A1)").Ok);
        Assert.False(ImportValueNormalizer.Number("'abc").Ok);
        Assert.Equal(1234.50m, ImportValueNormalizer.Amount("1,234.50").Value);
        Assert.Equal(-1234.50m, ImportValueNormalizer.Amount("(1,234.50)").Value);
    }

    // ═══ ย้อนยอดยกมา: บัญชีที่ wizard ไม่แตะ ═══

    [Fact]
    public void บัญชีที่ย้อนไม่ได้เพราะแก้มือ_อ่านจากสรุปของชุด_รูปเสียหรือไม่มี_ได้ว่าง()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var json = "{\"originalCommit\":\"{}\",\"rollback\":{\"rowsSkipped\":2,\"skippedAccountIds\":[\"" + a + "\",\"" + b + "\"]}}";
        var ids = MigrationWizardService.RollbackSkippedAccountIds(json);
        Assert.Equal(2, ids.Count);
        Assert.Contains(a, ids);
        Assert.Contains(b, ids);
        Assert.Empty(MigrationWizardService.RollbackSkippedAccountIds("{\"rollback\":{\"rowsSkipped\":0}}"));
        Assert.Empty(MigrationWizardService.RollbackSkippedAccountIds(null));
        Assert.Empty(MigrationWizardService.RollbackSkippedAccountIds("ไม่ใช่ JSON"));
    }
}
