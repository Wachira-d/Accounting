using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// 2026-10-14 ทีม F3 (PAYMENT_CHANNEL_AUDIT PC#11 · ID#13/#17/#19) — กติกา "ที่อยู่ของเงิน" ตัวเดียว (<see cref="MoneyAccountRule"/>) ·
/// "ผังลงรายการได้" (<see cref="PostingAccountRule"/>) · ข้อความชน foreign key (<see cref="ForeignKeyViolation"/>) — ทั้งสองทิศ
/// </summary>
public class MoneyAccountRuleTests
{
    // ── MoneyAccountRule ──

    [Theory]
    [InlineData("11111", true)]   // เงินสด
    [InlineData("11113", true)]   // กระเป๋าเงิน
    [InlineData("11330", true)]   // เงินทดรองกรรมการ (1133)
    [InlineData("21230", true)]   // เจ้าหนี้กรรมการ (2123)
    [InlineData("11340", false)]  // ลูกหนี้ผู้ให้บริการรับชำระ — ไม่ใช่ช่องจ่าย (1134)
    [InlineData("51110", false)]  // ค่าใช้จ่าย
    [InlineData("1010", false)]   // ผังของบริษัทเอง — ไม่เข้าผังมาตรฐาน
    public void ผังมาตรฐาน_รหัสขึ้นต้นเดิม_พฤติกรรมเดิม(string code, bool expected)
        => Assert.Equal(expected, MoneyAccountRule.IsMoneyAccount(code, linkedToActiveBank: false, MoneyAccountRule.StandardPrefixes));

    [Fact]
    public void ผังที่ผูกบัญชีธนาคาร_เป็นที่อยู่ของเงินเสมอ_ไม่ขึ้นกับรหัส()
    {
        Assert.True(MoneyAccountRule.IsMoneyAccount("1020-01", linkedToActiveBank: true, MoneyAccountRule.StandardPrefixes));
        Assert.Equal(MoneyChannelGroup.Bank, MoneyAccountRule.GroupOf("1020-01", true, MoneyAccountRule.StandardPrefixes));
    }

    [Fact]
    public void ผังมาตรฐาน_1112ที่ยังไม่ผูกธนาคาร_อยู่กลุ่มธนาคาร_อื่นๆอยู่กลุ่มอื่น()
    {
        Assert.Equal(MoneyChannelGroup.Bank, MoneyAccountRule.GroupOf("11121", false, MoneyAccountRule.StandardPrefixes));
        Assert.Equal(MoneyChannelGroup.Other, MoneyAccountRule.GroupOf("11111", false, MoneyAccountRule.StandardPrefixes));
        Assert.Equal(MoneyChannelGroup.Other, MoneyAccountRule.GroupOf("11131", false, MoneyAccountRule.StandardPrefixes));
        Assert.Null(MoneyAccountRule.GroupOf("41100", false, MoneyAccountRule.StandardPrefixes));
    }

    [Fact]
    public void ผังของบริษัทเอง_ตั้งรหัสแล้ว_แทนผังมาตรฐาน()
    {
        var custom = MoneyAccountRule.ParsePrefixes("101, 102");
        Assert.False(MoneyAccountRule.IsStandard(custom));
        Assert.True(MoneyAccountRule.IsMoneyAccount("1010", false, custom));
        Assert.True(MoneyAccountRule.IsMoneyAccount("1021", false, custom));
        // ทิศตรงข้าม: ตั้งรหัสเองแล้ว รหัสมาตรฐานที่บริษัทใช้เป็นอย่างอื่น (เช่น 2123) ไม่ถูกนับเป็นช่องจ่าย และไม่มีกลุ่ม 1112 อัตโนมัติ
        Assert.False(MoneyAccountRule.IsMoneyAccount("21230", false, custom));
        Assert.Equal(MoneyChannelGroup.Other, MoneyAccountRule.GroupOf("1021", false, custom));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ,  ")]
    public void ค่าตั้งว่าง_ใช้ผังมาตรฐาน(string? configured)
    {
        Assert.True(MoneyAccountRule.IsStandard(MoneyAccountRule.ParsePrefixes(configured)));
    }

    [Fact]
    public void ปรับรูปค่าตั้ง_ว่างคือล้าง_ตัวอักษรแปลกถูกปฏิเสธ()
    {
        Assert.Equal(("101, 102", (string?)null), MoneyAccountRule.NormalizeSetting(" 101 ;102,101 "));
        Assert.Equal(((string?)null, (string?)null), MoneyAccountRule.NormalizeSetting("   "));
        var (_, err) = MoneyAccountRule.NormalizeSetting("101, 10'2");
        Assert.NotNull(err);
        Assert.Contains("101, 102", err);   // บอกตัวอย่างรูปแบบที่ถูก
    }

    [Fact]
    public void บัญชีธนาคาร_แยก_ไม่พบ_ปิดใช้_ยังไม่ผูก_ผังใช้ไม่ได้()
    {
        var gl = Guid.NewGuid();
        Assert.Equal(BankLinkProblem.NotFound, MoneyAccountRule.CheckBank(false, false, null, PostingAccountProblem.None));
        Assert.Equal(BankLinkProblem.Inactive, MoneyAccountRule.CheckBank(true, false, gl, PostingAccountProblem.None));
        Assert.Equal(BankLinkProblem.NotLinked, MoneyAccountRule.CheckBank(true, true, null, PostingAccountProblem.None));
        Assert.Equal(BankLinkProblem.LinkedAccountUnusable, MoneyAccountRule.CheckBank(true, true, gl, PostingAccountProblem.Inactive));
        Assert.Equal(BankLinkProblem.None, MoneyAccountRule.CheckBank(true, true, gl, PostingAccountProblem.None));
        // ข้อความ: "ไม่พบ" ต้องไม่บอกว่า "ยังไม่ผูก" (ID#19) · ทุกข้อความบอกที่แก้
        var notFound = MoneyAccountRule.BankProblemMessage(BankLinkProblem.NotFound, null);
        Assert.DoesNotContain("ยังไม่ได้ผูก", notFound);
        Assert.Contains("เลือกบัญชีธนาคารใหม่", notFound);
        Assert.Contains("หน้า \"บัญชีธนาคาร\"", MoneyAccountRule.BankProblemMessage(BankLinkProblem.NotLinked, "บัญชีเงินเดือน"));
        Assert.Contains("ปิดใช้งาน", MoneyAccountRule.BankProblemMessage(BankLinkProblem.Inactive, "บัญชีเงินเดือน"));
    }

    // ── PostingAccountRule ──

    private static PostingAccountFacts Acc(bool active = true, bool group = false)
        => new(Guid.NewGuid(), "11122", "เงินฝากออมทรัพย์", active, group);

    [Fact]
    public void ผังลงรายการได้_ทั้งสองทิศ()
    {
        Assert.Equal(PostingAccountProblem.None, PostingAccountRule.Check(Acc()));
        Assert.Null(PostingAccountRule.Problem(Acc(), "ช่องทดสอบ"));
        Assert.Equal(PostingAccountProblem.NotFound, PostingAccountRule.Check(null));
        Assert.Equal(PostingAccountProblem.Inactive, PostingAccountRule.Check(Acc(active: false)));
        Assert.Equal(PostingAccountProblem.Group, PostingAccountRule.Check(Acc(group: true)));
    }

    [Fact]
    public void ข้อความผังใช้ไม่ได้_บอกช่องและที่แก้_ไม่มีGUID()
    {
        var a = Acc(active: false);
        var msg = PostingAccountRule.Problem(a, "บัญชีพักเงิน")!;
        Assert.StartsWith("บัญชีพักเงิน:", msg);
        Assert.Contains("11122 เงินฝากออมทรัพย์", msg);
        Assert.Contains("หน้า \"ผังบัญชี\"", msg);
        Assert.DoesNotContain(a.Id.ToString(), msg);
        Assert.Contains("บัญชีย่อย", PostingAccountRule.Problem(Acc(group: true), "x")!);
        Assert.Contains("เลือกบัญชีใหม่", PostingAccountRule.Problem(null, "x")!);
    }

    // ── ForeignKeyViolation (ID#17) ──

    [Fact]
    public void ชนFK_ตอนเพิ่ม_บอกให้เลือกค่าใหม่_ไม่ใช่ปิดการใช้งาน()
    {
        var kind = ForeignKeyViolation.Classify(
            "insert or update on table \"JournalEntryLines\" violates foreign key constraint \"FK_JournalEntryLines_ChartOfAccounts_AccountId\"", "POST");
        Assert.Equal(ForeignKeyViolationKind.MissingReference, kind);
        var msg = ForeignKeyViolation.Message(kind, "FK_JournalEntryLines_ChartOfAccounts_AccountId", "ABC12345");
        Assert.Contains("ช่องผังบัญชี", msg);
        Assert.Contains("เลือกค่าในช่องนั้นจากรายการอีกครั้ง", msg);
        Assert.DoesNotContain("ปิดการใช้งาน", msg);
        Assert.Contains("ABC12345", msg);
    }

    [Fact]
    public void ชนFK_ตอนลบ_ยังบอกว่าถูกอ้างอิง_ทิศเดิมไม่เปลี่ยน()
    {
        Assert.Equal(ForeignKeyViolationKind.StillReferenced, ForeignKeyViolation.Classify(
            "update or delete on table \"Contacts\" violates foreign key constraint \"FK_Documents_Contacts_ContactId\" on table \"Documents\"", "POST"));
        // ข้อความเซิร์ฟเวอร์เป็นภาษาอื่น ⇒ ดู HTTP method
        Assert.Equal(ForeignKeyViolationKind.StillReferenced, ForeignKeyViolation.Classify("ข้อความภาษาอื่น", "DELETE"));
        Assert.Equal(ForeignKeyViolationKind.Unknown, ForeignKeyViolation.Classify("ข้อความภาษาอื่น", "PUT"));
        var msg = ForeignKeyViolation.Message(ForeignKeyViolationKind.StillReferenced, "FK_Documents_Contacts_ContactId", "R1");
        Assert.Contains("ยังถูกอ้างอิง", msg);
        Assert.Contains("ปิดการใช้งานแทน", msg);
        // ไม่รู้ทิศ = ข้อความกลาง ไม่วินิจฉัยเกิน
        var neutral = ForeignKeyViolation.Message(ForeignKeyViolationKind.Unknown, null, "R2");
        Assert.DoesNotContain("ปิดการใช้งานแทน", neutral);
        Assert.Contains("R2", neutral);
    }

    [Theory]
    [InlineData("FK_Documents_BankAccounts_BankAccountId", "บัญชีธนาคาร")]
    [InlineData("FK_JournalEntryLines_ChartOfAccounts_AccountId", "ผังบัญชี")]
    [InlineData("FK_X_UnknownTable_Col", null)]
    [InlineData("PK_Something", null)]
    [InlineData(null, null)]
    public void ชื่อตารางปลายทางจากชื่อconstraint(string? constraint, string? expected)
        => Assert.Equal(expected, ForeignKeyViolation.TargetLabel(constraint));
}
