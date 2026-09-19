using System;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// D4-6 (เช็คเด้งไม่ถอย Payment/JE) + D4-7 (เงินสดย่อยไม่มี JE)
/// — `DECISION_AUDIT_2026-09-18.md` §3. สองครึ่งทุกหมวด
/// </summary>
public class ChequeBounceAndPettyCashTests
{
    private static readonly Guid Expense = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Fund = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Bank = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ══════════════════════════════════════════════════════════════════
    //  D4-6 เช็คเด้ง
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void เช็ครับเด้งที่ผูกกับการรับชำระ_ต้องกลับรายการชำระ()
    {
        var p = ChequeBouncePlan.Decide(isInbound: true, wasCleared: false, hasLinkedPayment: true);
        Assert.True(p.ReversePayment);
        Assert.False(p.RestoreBankBalanceDirectly);   // กันคืนยอดสองรอบ
        Assert.Equal("CHEQUE-BOUNCE-REVERSE-PAYMENT", p.RuleCode);
    }

    [Fact]
    public void เช็คจ่ายเด้งที่ผูกกับการจ่าย_ต้องกลับรายการจ่าย()
    {
        var p = ChequeBouncePlan.Decide(isInbound: false, wasCleared: false, hasLinkedPayment: true);
        Assert.True(p.ReversePayment);
        Assert.Contains("เช็คจ่าย", p.Reason);
    }

    [Fact]
    public void เช็คที่ยังไม่ขึ้นเงินและไม่มีการชำระผูก_ไม่มีอะไรให้ถอย()
    {
        // ครึ่ง "ของที่ถูกอยู่แล้วต้องไม่ถูกแตะ" — ห้ามไปยุ่งกับยอดธนาคาร
        var p = ChequeBouncePlan.Decide(isInbound: true, wasCleared: false, hasLinkedPayment: false);
        Assert.False(p.ReversePayment);
        Assert.False(p.RestoreBankBalanceDirectly);
        Assert.Equal("CHEQUE-BOUNCE-NO-POSTING", p.RuleCode);
    }

    [Fact]
    public void เช็คที่ขึ้นเงินแล้วถูกคืน_ถ้ามีการชำระให้กลับรายการชำระอย่างเดียว()
    {
        // ยอดธนาคารถูกขยับโดยการชำระ ⇒ การกลับรายการชำระเป็นคนคืนยอด
        var p = ChequeBouncePlan.Decide(isInbound: true, wasCleared: true, hasLinkedPayment: true);
        Assert.True(p.ReversePayment);
        Assert.False(p.RestoreBankBalanceDirectly);
        Assert.Equal("CHEQUE-BOUNCE-AFTER-CLEAR", p.RuleCode);
    }

    [Fact]
    public void เช็คที่ขึ้นเงินเองโดยไม่มีการชำระ_ถูกคืน_ต้องคืนยอดเอง()
    {
        // สมมาตรกับ `MarkClearedAsync`: ใครขยับยอด คนนั้นคืน
        var p = ChequeBouncePlan.Decide(isInbound: false, wasCleared: true, hasLinkedPayment: false);
        Assert.False(p.ReversePayment);
        Assert.True(p.RestoreBankBalanceDirectly);
    }

    // ══════════════════════════════════════════════════════════════════
    //  D4-7 เงินสดย่อย
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void จ่ายเงินสดย่อยครบผัง_ลงบัญชีได้และไม่ติดธงเมื่อมีใบเสร็จ()
    {
        var p = PettyCashJePlan.ForDisbursement(Expense, Fund, "RC-2026-0012", 350m);
        Assert.True(p.Ok);
        Assert.Equal(Expense, p.DebitAccountId);
        Assert.Equal(Fund, p.CreditAccountId);
        Assert.False(p.NonDeductible);
        Assert.Null(p.Error);
    }

    [Fact]
    public void จ่ายเงินสดย่อยไม่มีใบเสร็จ_ยังบันทึกได้แต่ติดธง65ตรี9()
    {
        var p = PettyCashJePlan.ForDisbursement(Expense, Fund, "   ", 350m);
        Assert.True(p.Ok);                       // เงินออกจริงแล้ว — ต้องบันทึกได้
        Assert.True(p.NonDeductible);            // แต่บวกกลับตอนคำนวณภาษี
        Assert.Equal(PettyCashJePlan.RuleNoReceipt, p.RuleCode);
    }

    [Fact]
    public void ไม่ได้เลือกผังค่าใช้จ่าย_ต้องล้มดังไม่ใช่บันทึกเงียบโดยไม่มีJE()
    {
        var p = PettyCashJePlan.ForDisbursement(null, Fund, "RC-1", 100m);
        Assert.False(p.Ok);
        Assert.Equal(PettyCashJePlan.RuleNoExpenseAccount, p.RuleCode);
        Assert.Contains("เลือกบัญชีค่าใช้จ่าย", p.Error);   // บอกทางไปต่อ
    }

    [Fact]
    public void กองทุนยังไม่ผูกผัง_ต้องล้มดังพร้อมบอกว่าไปตั้งค่าที่ไหน()
    {
        var p = PettyCashJePlan.ForDisbursement(Expense, null, "RC-1", 100m);
        Assert.False(p.Ok);
        Assert.Equal(PettyCashJePlan.RuleNoFundAccount, p.RuleCode);
        Assert.Contains("ตั้งค่าเงินสดย่อย", p.Error);
    }

    [Fact]
    public void Guidว่างถือว่าไม่ได้เลือก_ไม่ใช่ผังที่มีอยู่จริง()
    {
        var p = PettyCashJePlan.ForDisbursement(Guid.Empty, Fund, "RC-1", 100m);
        Assert.False(p.Ok);
    }

    [Fact]
    public void เติมเงินสดย่อยครบข้อมูล_ได้JEสองขาถูกทิศ()
    {
        var p = PettyCashJePlan.ForReplenishment(Fund, Bank, 5000m);
        Assert.True(p.Ok);
        Assert.Equal(Fund, p.DebitAccountId);    // เงินสดย่อยเพิ่ม
        Assert.Equal(Bank, p.CreditAccountId);   // ธนาคารลด
    }

    [Fact]
    public void เติมเงินโดยไม่รู้บัญชีต้นทาง_ต้องล้มไม่ใช่ข้ามJE()
    {
        var p = PettyCashJePlan.ForReplenishment(Fund, null, 5000m);
        Assert.False(p.Ok);
        Assert.Equal(PettyCashJePlan.RuleNoSourceAccount, p.RuleCode);
        Assert.Contains("บัญชีธนาคาร", p.Error);
    }

    [Fact]
    public void ต้นทางเป็นบัญชีเดียวกับกองทุน_ต้องปฏิเสธ()
    {
        var p = PettyCashJePlan.ForReplenishment(Fund, Fund, 5000m);
        Assert.False(p.Ok);
    }

    [Fact]
    public void ยอดไม่เป็นบวก_ปฏิเสธทั้งสองทาง()
    {
        Assert.False(PettyCashJePlan.ForDisbursement(Expense, Fund, "RC", 0m).Ok);
        Assert.False(PettyCashJePlan.ForReplenishment(Fund, Bank, -1m).Ok);
    }
}
