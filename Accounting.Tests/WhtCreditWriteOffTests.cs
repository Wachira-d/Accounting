using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 243a — ตัวตัดสินตัดสูญเครดิตภาษีถูกหัก 11910 (<see cref="WhtCreditWriteOff"/>) · ทุกด่านมีทิศตรงข้าม (เคสที่ถูกต้องผ่าน)
/// </summary>
public class WhtCreditWriteOffTests
{
    private static readonly DateTime Today = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
    private const string Reason = "ทวงแล้ว 3 ครั้ง ลูกค้าไม่ออกใบ";

    private static WhtCreditWriteOff.Decision D(WhtCreditStatus st = WhtCreditStatus.Pending, bool hasJe = false, decimal amount = 1_000m,
        string? reason = Reason, DateTime? date = null, string? closed = null, decimal gl = 5_000m, bool checkReason = true)
        => WhtCreditWriteOff.Decide(st, hasJe, amount, reason, date ?? Today, Today, closed, gl, checkReason);

    [Fact]
    public void รอใบ_และได้ใบแล้วยังไม่ใช้_ตัดสูญได้()
    {
        Assert.True(D().Allowed);
        Assert.True(D(WhtCreditStatus.Received).Allowed);              // ได้ใบแต่พ้นกำหนดขอคืน 3 ปี — ตัดได้
        Assert.True(D(gl: 1_000m).Allowed);                             // ยอด 11910 เท่ายอดพอดี
        Assert.True(D(date: Today.AddDays(-40)).Allowed);               // ย้อนหลังในงวดที่เปิด
    }

    [Fact]
    public void ตัดสูญแล้วพร้อมJE_กดซ้ำ_คืนผลเดิม_ไม่ใช่ข้อผิดพลาด()
    {
        var d = D(WhtCreditStatus.Expired, hasJe: true);
        Assert.Equal(WhtCreditWriteOff.Outcome.AlreadyWrittenOff, d.Outcome);
        Assert.False(d.Allowed);
        Assert.Null(d.Problem);
    }

    [Theory]
    [InlineData(WhtCreditStatus.Claimed, false)]      // ใช้เครดิตในแบบยื่นแล้ว
    [InlineData(WhtCreditStatus.Expired, false)]      // ตัดออกไว้ก่อนแล้วโดยไม่มี JE (ก่อนรอบนี้/คืนเงินอ้างใบลดหนี้) — ไม่ลงซ้ำ
    public void สถานะที่ตัดสูญไม่ได้(WhtCreditStatus st, bool hasJe)
    {
        var d = D(st, hasJe);
        Assert.Equal(WhtCreditWriteOff.Outcome.Blocked, d.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(d.Problem));
    }

    [Fact]
    public void ด่านยอด_เหตุผล_วันที่_งวดปิด_ยอด11910()
    {
        Assert.Equal(WhtCreditWriteOff.Outcome.Blocked, D(amount: 0m).Outcome);
        Assert.Equal(WhtCreditWriteOff.Outcome.Blocked, D(reason: "สั้นไป").Outcome);
        Assert.Equal(WhtCreditWriteOff.Outcome.Blocked, D(reason: "     ทวงแล้ว     ").Outcome);   // นับหลังตัดช่องว่าง (7 ตัว)
        Assert.Equal(WhtCreditWriteOff.Outcome.Blocked, D(reason: null).Outcome);
        Assert.True(D(reason: new string('ก', WhtCreditWriteOff.MinReasonLength)).Allowed);     // ทิศตรงข้าม: ครบ 10 พอดีผ่าน
        Assert.Equal(WhtCreditWriteOff.Outcome.Blocked, D(date: Today.AddDays(1)).Outcome);      // วันในอนาคต
        var closed = D(closed: "งวด 2026/10 ปิดแล้ว");
        Assert.Equal(WhtCreditWriteOff.Outcome.Blocked, closed.Outcome);
        Assert.Equal("งวด 2026/10 ปิดแล้ว", closed.Problem);
        Assert.Equal(WhtCreditWriteOff.Outcome.Blocked, D(gl: 999.98m).Outcome);                  // 11910 ไม่พอ = ไม่เคยลง/ถูกล้างแล้ว
        Assert.True(D(gl: 999.995m).Allowed);                                                     // ภายในเศษสตางค์
        // หน้ายืนยัน (ยังไม่พิมพ์เหตุผล) ไม่ฟ้องเรื่องเหตุผล แต่ยังฟ้องด่านอื่น
        Assert.True(D(reason: null, checkReason: false).Allowed);
        Assert.Equal(WhtCreditWriteOff.Outcome.Blocked, D(reason: null, closed: "ปิด", checkReason: false).Outcome);
    }

    [Fact]
    public void ได้ใบภายหลัง_ต้องกลับรายการเฉพาะแถวที่ตัดสูญพร้อมJEที่ยังมีผล()
    {
        Assert.True(WhtCreditWriteOff.NeedsReversal(WhtCreditStatus.Expired, Guid.NewGuid(), null));
        Assert.False(WhtCreditWriteOff.NeedsReversal(WhtCreditStatus.Expired, null, null));                 // ตัดออกแบบไม่มี JE — ไม่มีอะไรให้กลับ
        Assert.False(WhtCreditWriteOff.NeedsReversal(WhtCreditStatus.Expired, Guid.NewGuid(), Guid.NewGuid())); // กลับ JE แล้ว (ต้นทางหาย) — คง Expired
        Assert.False(WhtCreditWriteOff.NeedsReversal(WhtCreditStatus.Received, Guid.NewGuid(), Guid.NewGuid())); // ได้ใบภายหลังแล้ว
        Assert.False(WhtCreditWriteOff.NeedsReversal(WhtCreditStatus.Pending, null, null));
    }

    [Fact]
    public void JEตัดสูญยังมีผล_เฉพาะPostedที่ยังไม่ถูกกลับ_และไม่ถูกลบ()
    {
        Assert.True(WhtCreditWriteOff.JournalStillLive(JournalEntryStatus.Posted, null, false));
        Assert.False(WhtCreditWriteOff.JournalStillLive(JournalEntryStatus.Reversed, Guid.NewGuid(), false));
        Assert.False(WhtCreditWriteOff.JournalStillLive(JournalEntryStatus.Posted, Guid.NewGuid(), false));
        Assert.False(WhtCreditWriteOff.JournalStillLive(JournalEntryStatus.Posted, null, true));
    }

    [Fact]
    public void คืนเงินแพลตฟอร์มหลังตัดสูญ_ใช้เส้นกลับJEเดียว_ทิศตรงข้ามคงเดิม()
    {
        Assert.Equal(PlatformRefundCreditNote.RegistryAction.UndoWriteOff,
            PlatformRefundCreditNote.RegistryActionFor(WhtCreditStatus.Expired, 30m, 0m, writtenOffWithJournal: true));
        Assert.Equal(PlatformRefundCreditNote.RegistryAction.None,
            PlatformRefundCreditNote.RegistryActionFor(WhtCreditStatus.Expired, 30m, 0m));                  // Expired ไม่มี JE — คงเดิม
        Assert.Equal(PlatformRefundCreditNote.RegistryAction.Expire,
            PlatformRefundCreditNote.RegistryActionFor(WhtCreditStatus.Pending, 30m, 0m));                   // Pending — คงเดิม
        Assert.Equal(PlatformRefundCreditNote.RegistryAction.None,
            PlatformRefundCreditNote.RegistryActionFor(WhtCreditStatus.Expired, 0m, 0m, writtenOffWithJournal: true));   // ไม่มีส่วนที่คืน
    }

    [Fact]
    public void ด่านJEตัดสูญจากสมุดรายวัน_ข้อความบอกทางไปต่อ()
    {
        var m = WhtWriteOffJournalLock.BlockMessage("กลับรายการ", new[] { "JV-202611-0003" });
        Assert.Contains("JV-202611-0003", m);
        Assert.Contains("ได้รับใบแล้ว", m);
        Assert.Contains("ยังไม่มีอะไรถูกบันทึก", m);
    }

    [Fact]
    public void รายการเสนอสิ้นรอบ_เฉพาะรอใบ_และรอบบัญชีสิ้นสุดแล้ว()
    {
        var end2025 = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(WhtCreditWriteOff.IsYearEndCandidate(WhtCreditStatus.Pending, end2025, Today));
        Assert.False(WhtCreditWriteOff.IsYearEndCandidate(WhtCreditStatus.Pending, new DateTime(2026, 12, 31), Today));   // รอบยังไม่จบ
        Assert.False(WhtCreditWriteOff.IsYearEndCandidate(WhtCreditStatus.Pending, Today, Today));                       // วันสิ้นรอบเอง
        Assert.False(WhtCreditWriteOff.IsYearEndCandidate(WhtCreditStatus.Received, end2025, Today));
        Assert.False(WhtCreditWriteOff.IsYearEndCandidate(WhtCreditStatus.Expired, end2025, Today));
    }

    [Theory]
    [InlineData("58010", AccountType.Expense, "580", true)]    // ผังมาตรฐานใต้กลุ่ม 580
    [InlineData("58010", AccountType.Expense, null, true)]     // 58000–58099 ไม่ต้องรู้แม่ (เดิม StartsWith("5800") หลุด)
    [InlineData("53010", AccountType.Expense, "530", false)]   // ผังค่าใช้จ่ายทั่วไป — ภ.ง.ด.50 จะนับเป็นรายจ่ายหักได้ (ผิด)
    [InlineData("54820", AccountType.Expense, "548", false)]   // ค่าปรับ (บวกกลับคนละกลไก)
    [InlineData("58010", AccountType.Asset, "580", false)]
    public void ผังตัดสูญต้องเป็นกลุ่มภาษีเงินได้_บวกกลับเต็มจำนวน(string code, AccountType type, string? parent, bool nonDeductible)
    {
        Assert.Equal(nonDeductible, WhtCreditWriteOff.IsNonDeductibleAccount(code, type, parent));
        Assert.Equal(nonDeductible ? 1_234.57m : 0m, WhtCreditWriteOff.NonDeductibleAmount(1_234.565m, code, type, parent));
        // ตัวกรองเดียวกับ ภ.ง.ด.50/51 (ExceptCitExpense) ตัดสินตรงกัน
        var line = new JournalEntryLine { Account = new ChartOfAccount { AccountCode = code, AccountType = type, AccountName = "x",
            ParentAccount = parent == null ? null : new ChartOfAccount { AccountCode = parent, AccountName = "แม่" } } };
        Assert.Equal(!nonDeductible, new[] { line }.AsQueryable().ExceptCitExpense().Any());
    }

    [Fact]
    public void ผังมาตรฐาน58010อยู่ในแม่แบบ_ใต้580_และเป็นผังเฉพาะกิจ()
    {
        var t = ChartOfAccountTemplates.GetCommonAccounts().Single(a => a.Code == WhtCreditWriteOff.ExpenseAccountCode);
        Assert.Equal(AccountType.Expense, t.Type);
        Assert.Equal("580", SettlementChartSeed.ParentGroupCode(t.Code));
        Assert.Contains(WhtCreditWriteOff.ExpenseAccountCode, SpecialPurposeAccount.Codes);   // ไม่เป็นผังค่าใช้จ่ายตั้งต้น
        Assert.Contains(ChartOfAccountTemplates.GetCommonAccounts(), a => a.Code == "580");
    }

    [Fact]
    public void อ้างอิงJEตัดสูญ_เขียนและอ่านด้วยตัวเดียว()
    {
        var id = Guid.NewGuid();
        Assert.True(WhtCreditWriteOff.IsWriteOffReference(WhtCreditWriteOff.Reference(id)));
        Assert.False(WhtCreditWriteOff.IsWriteOffReference(WhtYearEndReference.For(2025)));   // JE ปิดปีคนละชุด
        Assert.False(WhtCreditWriteOff.IsWriteOffReference(null));
        Assert.True(WhtCreditWriteOff.Reference(id).Length <= 50);
    }
}
