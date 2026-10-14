using System;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม F1 · 2026-10-14 (PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 ข้อ 1/2 · ส่วนที่ 2 ข้อ 1/2/13/19) — ตัวตัดสินข้อความของ "รหัสที่ผู้ใช้เลือกมา"
/// ทั้งสองทิศ: ผังที่ลงรายการได้ต้องผ่าน (ไม่ใช่ด่านที่ฟ้องทุกใบ) · ผังที่ใช้ไม่ได้ต้องบอก "ช่องไหน · แก้ที่ไหน" เป็นไทย
/// และ "ไม่พบ" ≠ "ยังไม่ผูก" (เดิมรายงานเป็นข้อความเดียวกัน)
/// </summary>
public class PickedAccountGuardTests
{
    private static PickedAccountGuard.AccountFacts Acc(bool active = true, int level = 4, AccountType type = AccountType.Asset)
        => new("11112", "เงินสดย่อย", active, level, type);

    [Fact]
    public void ผังของบริษัทที่เปิดใช้ระดับรายการ_ผ่าน()
    {
        Assert.Null(PickedAccountGuard.AccountProblem(Acc(), "ผังบัญชีของกองทุน"));
        Assert.Null(PickedAccountGuard.AccountProblem(Acc(), "ผังบัญชีของกองทุน", new[] { AccountType.Asset }));
    }

    [Fact]
    public void ไม่พบผังในบริษัทนี้_บอกช่องและให้รีเฟรชเลือกใหม่()
    {
        var m = PickedAccountGuard.AccountProblem(null, "บัญชีค่าใช้จ่าย");
        Assert.NotNull(m);
        Assert.Contains("“บัญชีค่าใช้จ่าย”", m);
        Assert.Contains("ไม่พบผังบัญชี", m);
        Assert.DoesNotContain("ลบหรือเปลี่ยนไม่ได้", m);   // ไม่ใช่ข้อความ FK ที่วินิจฉัยผิดทางแบบเดิม
    }

    [Fact]
    public void ผังถูกปิดใช้งาน_บอกให้เปิดที่หน้าผังบัญชีหรือเลือกผังอื่น()
    {
        var m = PickedAccountGuard.AccountProblem(Acc(active: false), "บัญชีค่าใช้จ่าย");
        Assert.Contains("ปิดการใช้งาน", m);
        Assert.Contains("ผังบัญชี", m);
    }

    [Fact]
    public void บัญชีคุมระดับหมวด_ลงรายการไม่ได้()
    {
        var m = PickedAccountGuard.AccountProblem(Acc(level: 3), "ผังบัญชีเงินกู้");
        Assert.Contains("บัญชีคุม", m);
    }

    [Fact]
    public void ชนิดไม่ตรงช่อง_บอกชนิดที่ต้องเป็นเป็นภาษาไทย()
    {
        var m = PickedAccountGuard.AccountProblem(Acc(type: AccountType.Expense), "ผังบัญชีเงินกู้", new[] { AccountType.Liability });
        Assert.Contains("ค่าใช้จ่าย", m);
        Assert.Contains("หนี้สิน", m);
        Assert.Null(PickedAccountGuard.AccountProblem(Acc(type: AccountType.Liability), "ผังบัญชีเงินกู้", new[] { AccountType.Liability }));
    }

    [Fact]
    public void บัญชีธนาคาร_ไม่พบ_ปิดใช้_ยังไม่ผูก_เป็นคนละข้อความ()
    {
        var notFound = PickedAccountGuard.BankProblem(null, "เติมจากบัญชี");
        var inactive = PickedAccountGuard.BankProblem(new("กสิกร 123 (บัญชีหลัก)", false, Guid.NewGuid()), "เติมจากบัญชี");
        var unlinked = PickedAccountGuard.BankProblem(new("กสิกร 123 (บัญชีหลัก)", true, null), "เติมจากบัญชี");
        Assert.Contains("ไม่พบบัญชีธนาคาร", notFound);
        Assert.Contains("ปิดการใช้งาน", inactive);
        Assert.Contains("ยังไม่ได้ผูกผังบัญชี", unlinked);
        Assert.Contains("ผังบัญชีที่เชื่อมโยง", unlinked);   // ชี้ช่องจริงบนหน้า “บัญชีธนาคาร”
        Assert.NotEqual(notFound, unlinked);
    }

    [Fact]
    public void บัญชีธนาคารที่ผูกแล้ว_ผ่าน_และสมุดเช็คไม่บังคับการผูก()
    {
        Assert.Null(PickedAccountGuard.BankProblem(new("กสิกร 123 (บัญชีหลัก)", true, Guid.NewGuid()), "บัญชีธนาคาร"));
        Assert.Null(PickedAccountGuard.BankProblem(new("กสิกร 123 (บัญชีหลัก)", true, null), "บัญชีธนาคาร", requireLinked: false));
        Assert.NotNull(PickedAccountGuard.BankProblem(new("กสิกร 123 (บัญชีหลัก)", false, null), "บัญชีธนาคาร", requireLinked: false));
    }

    [Fact]
    public void ผังที่บัญชีธนาคารผูกไว้ใช้ไม่ได้_บอกให้แก้ที่หน้าบัญชีธนาคาร()
    {
        var bank = new PickedAccountGuard.BankFacts("กสิกร 123 (บัญชีหลัก)", true, Guid.NewGuid());
        Assert.Null(PickedAccountGuard.LinkedGlProblem(bank, Acc(), "เติมจากบัญชี"));
        var m = PickedAccountGuard.LinkedGlProblem(bank, Acc(active: false), "เติมจากบัญชี");
        Assert.Contains("บัญชีธนาคาร", m);
        Assert.Contains("ผังบัญชีที่เชื่อมโยง", m);
        Assert.NotNull(PickedAccountGuard.LinkedGlProblem(bank, null, "เติมจากบัญชี"));
        Assert.NotNull(PickedAccountGuard.LinkedGlProblem(bank, Acc(type: AccountType.Expense), "เติมจากบัญชี"));
    }

    [Fact]
    public void ส่งทั้งบัญชีธนาคารและผังเงินพร้อมกัน_ปฏิเสธ_ส่งอย่างเดียวหรือไม่ส่งผ่าน()
    {
        Assert.NotNull(PickedAccountGuard.ChannelProblem(Guid.NewGuid(), Guid.NewGuid(), "จ่ายจากบัญชี"));
        Assert.Null(PickedAccountGuard.ChannelProblem(Guid.NewGuid(), null, "จ่ายจากบัญชี"));
        Assert.Null(PickedAccountGuard.ChannelProblem(null, Guid.NewGuid(), "จ่ายจากบัญชี"));
        Assert.Null(PickedAccountGuard.ChannelProblem(Guid.NewGuid(), Guid.Empty, "จ่ายจากบัญชี"));   // Guid ว่าง = ไม่ได้เลือก
        Assert.False(PickedAccountGuard.IsSet(Guid.Empty));
        Assert.False(PickedAccountGuard.IsSet(null));
    }
}

/// <summary>ทีม F1 · 2026-10-14 (PAYMENT_CHANNEL_AUDIT ส่วนที่ 2 ข้อ 2) — ตัวตัดสินของสินเชื่อ</summary>
public class LoanPostingPlanTests
{
    private static readonly DateTime D = new(2026, 10, 1);

    [Fact]
    public void คำขอจากหน้าเว็บที่ครบ_ผ่าน()
        => Assert.Null(LoanPostingPlan.ValidateCreate("เงินกู้ซื้อเครื่องจักร", 500000m, 6.5m, 36, "Fixed", "Monthly", D, D.AddMonths(1)));

    [Theory]
    [InlineData("", 1000, 5, 12, "Fixed", "ชื่อสินเชื่อ")]
    [InlineData("ก", 0, 5, 12, "Fixed", "เงินต้น")]
    [InlineData("ก", 1000, -1, 12, "Fixed", "อัตราดอกเบี้ย")]
    [InlineData("ก", 1000, 5, 0, "Fixed", "ระยะเวลา")]
    [InlineData("ก", 1000, 5, 12, "Weird", "ประเภทดอกเบี้ย")]
    public void คำขอผิด_บอกช่องที่ผิด(string name, double principal, double rate, int term, string intType, string field)
    {
        var m = LoanPostingPlan.ValidateCreate(name, (decimal)principal, (decimal)rate, term, intType, "Monthly", D, null);
        Assert.NotNull(m);
        Assert.Contains(field, m);
    }

    [Fact]
    public void ความถี่รายไตรมาส_ปฏิเสธพร้อมทางไปต่อ_ไม่รับแล้วไม่มีผล()
    {
        var m = LoanPostingPlan.ValidateCreate("ก", 1000m, 5m, 12, "Fixed", "Quarterly", D, null);
        Assert.Contains("รายเดือน", m);
        Assert.Null(LoanPostingPlan.ValidateCreate("ก", 1000m, 5m, 12, "Fixed", null, D, null));   // ไม่ส่ง = รายเดือน
    }

    [Fact]
    public void วันที่ว่าง0001_หรืองวดแรกก่อนวันรับเงิน_ปฏิเสธ()
    {
        Assert.Contains("วันรับเงินกู้", LoanPostingPlan.ValidateCreate("ก", 1000m, 5m, 12, "Fixed", "Monthly", default, null));
        Assert.Contains("งวดแรก", LoanPostingPlan.ValidateCreate("ก", 1000m, 5m, 12, "Fixed", "Monthly", D, D.AddDays(-1)));
    }

    [Fact]
    public void ขอลงบัญชีรับเงินกู้_ต้องมีทั้งผังเงินกู้และบัญชีรับเงิน()
    {
        Assert.True(LoanPostingPlan.ForDisbursement(true, true, true).Post);
        var noLoan = LoanPostingPlan.ForDisbursement(true, false, true);
        Assert.False(noLoan.Post);
        Assert.Contains("ผังบัญชีเงินกู้", noLoan.Error);
        var noMoney = LoanPostingPlan.ForDisbursement(true, true, false);
        Assert.Contains("บัญชีรับ/จ่ายเงินกู้", noMoney.Error);
    }

    [Fact]
    public void เลือกไม่ลงบัญชี_ยอดยกมาแล้ว_ผ่านโดยไม่ลงJE()
    {
        var d = LoanPostingPlan.ForDisbursement(false, false, false);
        Assert.False(d.Post);
        Assert.Null(d.Error);
    }

    [Fact]
    public void ผู้เรียกAPIแบบเดิม_ไม่มีผังเลยไม่ลง_มีครึ่งเดียวปฏิเสธไม่ข้ามเงียบ()
    {
        Assert.Null(LoanPostingPlan.ForDisbursement(null, false, false).Error);
        Assert.False(LoanPostingPlan.ForDisbursement(null, false, false).Post);
        Assert.NotNull(LoanPostingPlan.ForDisbursement(null, true, false).Error);   // เดิม: ข้าม JE เงียบ ๆ
        Assert.NotNull(LoanPostingPlan.ForDisbursement(null, false, true).Error);
        Assert.True(LoanPostingPlan.ForDisbursement(null, true, true).Post);
    }

    [Theory]
    [InlineData("LineOfCredit", 60, "21100")]
    [InlineData("BankLoan", 12, "21300")]
    [InlineData("BankLoan", 36, "22100")]
    [InlineData("Mortgage", 0, "22100")]
    public void ผังเงินกู้ที่เสนอ_ตามชนิดและระยะ(string type, int term, string code)
        => Assert.Equal(code, LoanPostingPlan.SuggestLoanAccountCode(type, term));

    [Fact]
    public void ข้อความไม่มีบัญชีที่จ่าย_ชี้ช่องที่มีจริงบนหน้า()
    {
        Assert.Contains("จ่ายจากบัญชี", LoanPostingPlan.NoMoneyAccountMessage);
        Assert.Contains("แก้ไขสินเชื่อ / ผังบัญชี", LoanPostingPlan.NoLoanAccountMessage);
        Assert.DoesNotContain("ตั้งค่าในรายละเอียดสินเชื่อ", LoanPostingPlan.NoLoanAccountMessage);   // เดิม = ทางตัน (ไม่มีช่องให้ตั้ง)
    }
}
