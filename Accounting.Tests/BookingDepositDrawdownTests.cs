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
        var s = BookingDepositDrawdown.EvaluateDrawdown(Draw(1000m), ledger);
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
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(Draw(1000m), ledger));
        // เศษสตางค์ไม่ถือว่าเกิน
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(Draw(1000.005m), ledger));
    }

    [Fact]
    public void PartialDeposit_AllowsUpToRemaining_RejectsBeyond()
    {
        var earlier = Guid.NewGuid();
        var ledger = new[] { Cr(Rv0070, "RV-1", 1000m), Dr(earlier, "JV-INT-1", 600m) };
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(Draw(400m), ledger));
        var s = Assert.Single(BookingDepositDrawdown.EvaluateDrawdown(Draw(500m), ledger));
        Assert.Equal(400m, s.Available);
    }

    [Fact]
    public void NoDepositDebit_OrNoTrace_IsNotTouched()
    {
        var reversed = new[] { Cr(Rv0070, "RV-1", 1000m), Dr(Rv0097, "RV-2", 1000m) };
        // JE ที่ไม่ตัดมัดจำ (เช่น Cr 21510 = รับมัดจำเพิ่ม) ⇒ ไม่แตะ
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(Draw(-1000m), reversed));
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(new Dictionary<Guid, decimal>(), reversed));
        // ไม่พบร่องรอยการจองเลย ⇒ ไม่ตัดสิน (มัดจำอาจบันทึกด้วยเลขอ้างอิงแบบอื่น)
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(Draw(1000m), Array.Empty<DepositLedgerLine>()));
    }

    // ── ทิศกลับ: ยกเลิก/กลับรายการมัดจำ ──
    [Fact]
    public void Removal_OfDrawnDeposit_IsBlocked_WithWayForward()
    {
        var ledger = new[] { Cr(Rv0070, "RV-202609-0070", 1000m), Dr(Jv0009, "JV-INT-202609-0009", 1000m) };
        var s = Assert.Single(BookingDepositDrawdown.EvaluateRemoval(ledger, new[] { Rv0070 }));
        Assert.Equal(1000m, s.Requested);
        var msg = BookingDepositDrawdown.RemovalMessage("149433", "ยกเลิกเอกสาร RV-202609-0070", new[] { s }, _ => "21510",
            new[] { "JV-INT-202609-0009" }, Array.Empty<string>());
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

    // ── หลังฝ่ายค้าน (2026-10-26) ──

    [Fact]
    public void ExtractKeys_FullBookingNumber_NotPrefix()
    {
        Assert.Equal(new[] { "101-2609-0001" }, BookingDepositDrawdown.ExtractKeys("RES-101-2609-0001").ToArray());
        Assert.False(BookingDepositDrawdown.Mentions("101", "RES-101-2609-0001"));
        Assert.Empty(BookingDepositDrawdown.ExtractKeys("ใบแจ้งหนี้ #1234"));      // "#" ที่ไม่ใช่การจองไม่นับ
    }

    [Fact]
    public void NightAudit_MultiBooking_SplitPerLine()   // ฝ่ายค้านข้อ 1
    {
        bool IsDep(Guid id) => id == Dep21510;
        var lines = new[]
        {
            new IncomingJournalLine(Dep21510, 500m, 0m, "รับรู้รายได้ RES-1001"),
            new IncomingJournalLine(Dep21510, 700m, 0m, "รับรู้รายได้ RES-1002"),
            new IncomingJournalLine(Guid.NewGuid(), 0m, 1200m, "รายได้ค่าห้อง"),
        };
        var per = BookingDepositDrawdown.AssignDrawdown(new string?[] { "NIGHT-AUDIT-20260922", "สรุปเช็คเอาต์ RES-1001 RES-1002" }, lines, IsDep);
        Assert.Equal(500m, per["1001"][Dep21510]);
        Assert.Equal(700m, per["1002"][Dep21510]);
        // การจอง 1001 มีมัดจำ 500 · 1002 มีมัดจำ 700 ⇒ ผ่านทั้งคู่ (เดิมเทียบ 1,200 ทั้งใบกับคงเหลือการจองเดียว ⇒ ปฏิเสธผิด)
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(per["1001"], new[] { Cr(Guid.NewGuid(), "RV-A", 500m) }));
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(per["1002"], new[] { Cr(Guid.NewGuid(), "RV-B", 700m) }));
    }

    [Fact]
    public void AmbiguousLine_IsNotDecided_SingleHeader_UsesHeader()
    {
        bool IsDep(Guid id) => id == Dep21510;
        var noKeyLine = new[] { new IncomingJournalLine(Dep21510, 1000m, 0m, "ตัดมัดจำ") };
        Assert.Empty(BookingDepositDrawdown.AssignDrawdown(new string?[] { "RES-1001 RES-1002" }, noKeyLine, IsDep));   // หัวอ้างสองการจอง ⇒ ไม่ตัดสิน
        var single = BookingDepositDrawdown.AssignDrawdown(new string?[] { "RES-149433-CHK", "รับรู้รายได้ Checkout - การจอง #149433" }, noKeyLine, IsDep);
        Assert.Equal(1000m, single["149433"][Dep21510]);
        Assert.Empty(BookingDepositDrawdown.AssignDrawdown(new string?[] { "RES-149433-CHK" },
            new[] { new IncomingJournalLine(Guid.NewGuid(), 1000m, 0m, null) }, IsDep));                                 // ไม่ใช่ผังมัดจำ ⇒ ไม่แตะ
    }

    [Fact]
    public void OnlyOwnDrawdowns_NoDepositCredit_IsNotDecided()   // ฝ่ายค้านข้อ 2
    {
        // มัดจำลงด้วยเลขอ้างอิงแบบอื่น — ระบบเห็นแค่งวดแรกที่ตัดไป 600 ⇒ งวดที่สองต้องไม่ถูกปฏิเสธ
        var onlyDraw = new[] { Dr(Guid.NewGuid(), "JV-INT-1", 600m) };
        Assert.False(BookingDepositDrawdown.HasDepositCredit(onlyDraw));
        Assert.Empty(BookingDepositDrawdown.EvaluateDrawdown(Draw(400m), onlyDraw));
        // เครดิตของตัวกลับรายการ (กลับ JE ที่ตัด) ไม่ใช่ "มีมัดจำ"
        var drawReversed = new[] { Dr(Jv0009, "JV-INT-9", 600m), new DepositLedgerLine(Guid.NewGuid(), "REV", Dep21510, 0m, 600m, default, IsReversal: true) };
        Assert.False(BookingDepositDrawdown.HasDepositCredit(drawReversed));
    }

    [Fact]
    public void Report_ChronologicalOrder_FlagsOnlySecondDraw()   // ฝ่ายค้านข้อ 6
    {
        var d1 = Guid.NewGuid(); var d2 = Guid.NewGuid();
        var ledger = new[]
        {
            new DepositLedgerLine(Rv0070, "RV-1", Dep21510, 0m, 1000m, new DateTime(2026, 9, 1)),
            new DepositLedgerLine(d1, "JV-INT-1", Dep21510, 600m, 0m, new DateTime(2026, 9, 5)),
            new DepositLedgerLine(d2, "JV-INT-2", Dep21510, 600m, 0m, new DateTime(2026, 9, 9)),
        };
        var o = Assert.Single(BookingDepositDrawdown.OverdrawnInOrder(ledger, new[] { d1, d2 }));
        Assert.Equal(d2, o.JournalEntryId);
        Assert.Equal(400m, o.AvailableBefore);
        Assert.Equal(600m, o.Drawn);
    }

    [Fact]
    public void Report_ReportedCase_FlagsJvInt_AndCleanBookingIsNotFlagged()
    {
        var ledger = new[]
        {
            new DepositLedgerLine(Rv0070, "RV-202609-0070", Dep21510, 0m, 1000m, new DateTime(2026, 9, 17)),
            new DepositLedgerLine(Rv0097, "RV-202609-0097", Dep21510, 1000m, 0m, new DateTime(2026, 9, 17), IsReversal: true),
            new DepositLedgerLine(Jv0009, "JV-INT-202609-0009", Dep21510, 1000m, 0m, new DateTime(2026, 9, 22)),
        };
        var o = Assert.Single(BookingDepositDrawdown.OverdrawnInOrder(ledger, new[] { Jv0009 }));
        Assert.Equal(0m, o.AvailableBefore);
        var clean = new[] { ledger[0], ledger[2] };   // มัดจำไม่ถูกกลับ ⇒ ตัดได้พอดี
        Assert.Empty(BookingDepositDrawdown.OverdrawnInOrder(clean, new[] { Jv0009 }));
    }

    [Fact]
    public void RemovalMessage_DocumentDrawing_SaysVoidDocument()   // ฝ่ายค้านข้อ 7
    {
        var msg = BookingDepositDrawdown.RemovalMessage("149433", "ยกเลิกเอกสาร RV-1", new[] { new DepositShortfall(Dep21510, 1000m, 0m) },
            _ => "21510", Array.Empty<string>(), new[] { "TIV-202609-0001" });
        Assert.Contains("ยกเลิกเอกสาร TIV-202609-0001", msg);
        Assert.DoesNotContain("กลับรายการ TIV", msg);
    }

    [Fact]
    public void ConfiguredDepositAccount_CountsEvenWithoutKeyword()   // ฝ่ายค้านข้อ 9
    {
        Assert.True(BookingDepositDrawdown.IsDepositLiabilityAccount(AccountType.Liability, "21799", "บัญชีพักลูกค้า", new HashSet<string> { "21799" }));
        Assert.False(BookingDepositDrawdown.IsDepositLiabilityAccount(AccountType.Liability, "21799", "บัญชีพักลูกค้า"));
        Assert.True(BookingDepositDrawdown.IsDepositLiabilityAccount(AccountType.Liability, "21540", "เงินรับจองห้องพัก"));
    }
}
