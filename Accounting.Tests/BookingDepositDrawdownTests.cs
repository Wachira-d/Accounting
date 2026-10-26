using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม S 2026-10-26 — ผู้ใช้รายงาน: การจอง #149433 ใบมัดจำ RV-202609-0070 (Cr 21510 1,000) ถูกกลับรายการแล้วด้วย RV-202609-0097
/// แต่ JV-INT-202609-0009 จาก API (RES-149433-CHK) ยังตัด 21510 1,000 + รับรู้รายได้/ภาษีขาย ⇒ ตัวตัดสินเดียว <see cref="BookingDepositDrawdown"/> ·
/// สองทิศ: มัดจำยังอยู่ ⇒ ผ่านเหมือนเดิม · ถูกกลับ ⇒ ปฏิเสธ · มัดจำบางส่วน · ไม่ได้อ้างการจอง/ไม่ตัดมัดจำ ⇒ ไม่แตะ · ทิศกลับ (ยกเลิกมัดจำที่ถูกตัดแล้ว)
/// </summary>
public class BookingDepositDrawdownTests
{
    private static readonly Guid Dep21510 = Guid.NewGuid();
    private static readonly Guid Rv0070 = Guid.NewGuid();
    private static readonly Guid Rv0097 = Guid.NewGuid();
    private static readonly Guid Jv0009 = Guid.NewGuid();

    private static DepositLedgerLine Cr(Guid je, string no, decimal amt) => new(je, no, Dep21510, 0m, amt);
    private static DepositLedgerLine Dr(Guid je, string no, decimal amt) => new(je, no, Dep21510, amt, 0m);
    private static IReadOnlyDictionary<Guid, decimal> Draw(decimal amt) => new Dictionary<Guid, decimal> { [Dep21510] = amt };

    // ── เลขการจอง ──
    [Fact]
    public void ExtractKeys_ReadsBookingFromReferenceAndDescription()
    {
        Assert.Equal(new[] { "149433" }, BookingDepositDrawdown.ExtractKeys("RES-149433-CHK").ToArray());
        Assert.Equal(new[] { "149433" }, BookingDepositDrawdown.ExtractKeys("รับรู้รายได้ Checkout - การจอง #149433 (คุณปราณี)").ToArray());
        Assert.Equal(new[] { "149433" }, BookingDepositDrawdown.ExtractKeys("รับมัดจำ - การจอง #149433", "RES-149433").ToArray());
    }

    [Fact]
    public void ExtractKeys_IgnoresTextWithoutBooking_AndMatchesExactly()
    {
        Assert.Empty(BookingDepositDrawdown.ExtractKeys("INV-2026-0001", "ขายเงินสด 1,000", null, ""));
        Assert.False(BookingDepositDrawdown.Mentions("149433", "RES-1494330"));
        Assert.False(BookingDepositDrawdown.Mentions("149433", "PRES-149433"));
        Assert.True(BookingDepositDrawdown.Mentions("149433", "res-149433-dep"));
    }

    // ── บัญชีมัดจำ: ดูความหมาย ไม่ใช่เลขนำหน้า ──
    [Theory]
    [InlineData(AccountType.Liability, "21510", "เงินมัดจำรับล่วงหน้าค่าห้องพัก", true)]
    [InlineData(AccountType.Liability, "21712", "ค่าสินค้ารับล่วงหน้า", true)]
    [InlineData(AccountType.Liability, "21610", "เงินมัดจำรับ", true)]
    [InlineData(AccountType.Liability, "21511", "ค่าไฟฟ้าค้างจ่าย", false)]      // ผังมาตรฐาน 215xx = ค้างจ่าย
    [InlineData(AccountType.Liability, "21714", "ดอกเบี้ยค้างจ่าย", false)]
    [InlineData(AccountType.Liability, "21815", "เงินประกันสังคมรอนำส่ง", false)]
    [InlineData(AccountType.Liability, "21530", "เงินประกันความเสียหาย", true)]
    [InlineData(AccountType.Liability, "21911", "ภาษีขาย ภ.พ. 30", false)]
    [InlineData(AccountType.Revenue, "41110", "รายได้ค่าห้องพัก", false)]
    [InlineData(AccountType.Asset, "11830", "เงินมัดจำจ่ายล่วงหน้า", false)]      // มัดจำจ่าย = สินทรัพย์ ไม่ใช่หนี้สิน
    public void IsDepositLiabilityAccount_ByMeaning(AccountType type, string code, string name, bool expected)
        => Assert.Equal(expected, BookingDepositDrawdown.IsDepositLiabilityAccount(type, code, name));

    // ── ฝั่งรับ JE ──
    [Fact]
    public void ReportedCase_DepositReversed_IsRejected()
    {
        // RV-0070 (สถานะ Reversed — ยังอยู่ในสมุด) Cr 1,000 + RV-0097 ตัวกลับ Dr 1,000 ⇒ คงเหลือ 0
        var ledger = new[] { Cr(Rv0070, "RV-202609-0070", 1000m), Dr(Rv0097, "RV-202609-0097", 1000m) };
        var s = BookingDepositDrawdown.EvaluateDrawdown(Draw(1000m), ledger, bookingTraceFound: true);
        var only = Assert.Single(s);
        Assert.Equal(1000m, only.Requested);
        Assert.Equal(0m, only.Available);

        var msg = BookingDepositDrawdown.DrawdownMessage("149433", s, _ => "21510 เงินมัดจำรับล่วงหน้าค่าห้องพัก", new[] { "RV-202609-0070" });
        Assert.Contains("#149433", msg);
        Assert.Contains("RV-202609-0070", msg);
        Assert.Contains(BookingDepositDrawdown.RuleCode, msg);
        Assert.Contains("ยังไม่มีอะไรถูกบันทึก", msg);
    }

    [Fact]
    public void DepositStillLive_PassesAsBefore()
    {
        var ledger = new[] { Cr(Rv0070, "RV-202609-0070", 1000m) };
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(Draw(1000m), ledger, true));
        // เศษสตางค์ไม่ถือว่าเกิน
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(Draw(1000.005m), ledger, true));
    }

    [Fact]
    public void PartialDeposit_AllowsUpToRemaining_RejectsBeyond()
    {
        var earlier = Guid.NewGuid();
        var ledger = new[] { Cr(Rv0070, "RV-1", 1000m), Dr(earlier, "JV-INT-1", 600m) };
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(Draw(400m), ledger, true));
        var s = Assert.Single(BookingDepositDrawdown.EvaluateDrawdown(Draw(500m), ledger, true));
        Assert.Equal(400m, s.Available);
    }

    [Fact]
    public void NoDepositDebit_OrNoTrace_IsNotTouched()
    {
        var reversed = new[] { Cr(Rv0070, "RV-1", 1000m), Dr(Rv0097, "RV-2", 1000m) };
        // JE ที่ไม่ตัดมัดจำ (เช่น Cr 21510 = รับมัดจำเพิ่ม) ⇒ ไม่แตะ
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(Draw(-1000m), reversed, true));
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(new Dictionary<Guid, decimal>(), reversed, true));
        // ไม่พบร่องรอยการจองเลย ⇒ ไม่ตัดสิน (มัดจำอาจบันทึกด้วยเลขอ้างอิงแบบอื่น)
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(Draw(1000m), Array.Empty<DepositLedgerLine>(), bookingTraceFound: false));
    }

    // ── ทิศกลับ: ยกเลิก/กลับรายการมัดจำ ──
    [Fact]
    public void Removal_OfDrawnDeposit_IsBlocked_WithWayForward()
    {
        var ledger = new[] { Cr(Rv0070, "RV-202609-0070", 1000m), Dr(Jv0009, "JV-INT-202609-0009", 1000m) };
        var s = Assert.Single(BookingDepositDrawdown.EvaluateRemoval(ledger, new[] { Rv0070 }));
        Assert.Equal(1000m, s.Requested);
        var msg = BookingDepositDrawdown.RemovalMessage("149433", "ยกเลิกเอกสาร RV-202609-0070", new[] { s }, _ => "21510", new[] { "JV-INT-202609-0009" });
        Assert.Contains("JV-INT-202609-0009", msg);
        Assert.Contains("กลับรายการ", msg);
        Assert.Contains(BookingDepositDrawdown.RemovalRuleCode, msg);
    }

    [Fact]
    public void Removal_OfDrawingJournal_OrUndrawnDeposit_IsAllowed()
    {
        var drawn = new[] { Cr(Rv0070, "RV-1", 1000m), Dr(Jv0009, "JV-INT-9", 1000m) };
        Assert.Empty(BookingDepositDrawdown.EvaluateRemoval(drawn, new[] { Jv0009 }));              // กลับ JE ที่ตัด ⇒ ยอดคืน
        Assert.Empty(BookingDepositDrawdown.EvaluateRemoval(new[] { Cr(Rv0070, "RV-1", 1000m) }, new[] { Rv0070 }));   // ยังไม่ถูกตัด
        // ข้อมูลที่พังอยู่แล้ว (มัดจำถูกกลับ + JV ยังตัด): กลับ JV ⇒ ยอดกลับเป็น 0 ⇒ ไม่บล็อก (ทางซ่อมของรายงานตรวจข้อมูล)
        var broken = new[] { Cr(Rv0070, "RV-1", 1000m), Dr(Rv0097, "RV-2", 1000m), Dr(Jv0009, "JV-INT-9", 1000m) };
        Assert.Empty(BookingDepositDrawdown.EvaluateRemoval(broken, new[] { Jv0009 }));
    }

    [Fact]
    public void Removal_OfOneOfTwoDeposits_AllowedWhileOtherCovers()
    {
        var second = Guid.NewGuid();
        var ledger = new[] { Cr(Rv0070, "RV-1", 1000m), Cr(second, "RV-2", 500m), Dr(Jv0009, "JV-INT-9", 1000m) };
        Assert.Empty(BookingDepositDrawdown.EvaluateRemoval(ledger, new[] { second }));            // เหลือ 1,000 พอกับที่ตัด
        Assert.Single(BookingDepositDrawdown.EvaluateRemoval(ledger, new[] { Rv0070 }));            // เหลือ 500 ไม่พอ 1,000
    }
}
