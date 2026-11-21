using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 282 (คำตัดสิน 282-1..282-6) — pure: แหล่งเงินตัวเดียวของทั้งสองฝั่ง (<see cref="SlipMoneySource"/>) · ด่านบันทึกรับเงินจากสลิปเอง (<see cref="SlipAutoRecordRule"/>) ·
/// ตารางเตือนซ้ำ (ไม่ถี่ · มีเพดาน) · ทิศตรงข้าม: ทุกด่านที่ไม่ผ่าน ⇒ ไม่บันทึกเอง + เหตุ
/// </summary>
public class SlipAutoRecordRound282Tests
{
    private static readonly Guid AccK = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid AccS = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly DateTime Now = new(2026, 11, 21, 5, 0, 0, DateTimeKind.Utc);   // 12:00 ไทย
    private static readonly DateTime Today = new(2026, 11, 21);

    private static SlipAccountMatch.Result Unique(Guid id) => new(SlipAccountMatch.Kind.Unique, id, new[] { id }, "ตรง", "กสิกรไทย ••0621");
    private static SlipAccountMatch.Result Kind(SlipAccountMatch.Kind k) => new(k, null, Array.Empty<Guid>(), "เหตุ", "กสิกรไทย ••9999");

    [Fact]
    public void แหล่งเงิน_รายการเดินบัญชีชนะ_สลิปตรงบัญชีเดียวบันทึกเองได้_อื่นๆห้ามเดา()
    {
        var usable = new[] { AccK, AccS };
        Assert.Equal((AccS, SlipMoneySource.Evidence.BankLine, true), Pick(SlipMoneySource.Decide(AccS, Unique(AccK), usable)));
        Assert.Equal((AccK, SlipMoneySource.Evidence.SlipUnique, true), Pick(SlipMoneySource.Decide(null, Unique(AccK), usable)));
        // ทิศตรงข้าม: ไม่มีสลิป · ไม่ใช่ของเรา · หลายบัญชี · อ่านไม่ได้ · บัญชีที่ตรงใช้ไม่ได้ ⇒ ไม่รู้ (ไม่เลือกบัญชีให้)
        foreach (var v in new[]
                 {
                     SlipMoneySource.Decide(null, null, usable), SlipMoneySource.Decide(null, Kind(SlipAccountMatch.Kind.NotOurs), usable),
                     SlipMoneySource.Decide(null, Kind(SlipAccountMatch.Kind.Many), usable), SlipMoneySource.Decide(null, Kind(SlipAccountMatch.Kind.Unreadable), usable),
                     SlipMoneySource.Decide(null, Unique(AccK), new[] { AccS }), SlipMoneySource.Decide(Guid.NewGuid(), null, usable),
                 })
        {
            Assert.Null(v.AccountId);
            Assert.False(v.Auto);
            Assert.False(v.UsedAi);
            Assert.False(string.IsNullOrWhiteSpace(v.Reason));
        }
        Assert.Contains("ไม่ใช่บัญชีของบริษัท", SlipMoneySource.Decide(null, Kind(SlipAccountMatch.Kind.NotOurs), usable).Reason);
    }

    private static (Guid?, SlipMoneySource.Evidence, bool) Pick(SlipMoneySource.Verdict v) => (v.AccountId, v.Evidence, v.Auto);

    [Fact]
    public void ฝั่งผู้ซื้ออ่านนโยบายชั้นจากตัวตั้งเดียว()
    {
        Assert.Equal(SlipMoneySource.AllowsAuto(SlipMoneySource.Evidence.SlipUnique),
            PartnerPaySourceAccount.Row(PartnerPaySourceAccount.Origin.SlipUnique).AllowsAuto);
        Assert.Equal(SlipMoneySource.AllowsAuto(SlipMoneySource.Evidence.BankLine),
            PartnerPaySourceAccount.Row(PartnerPaySourceAccount.Origin.BankLine).AllowsAuto);
        Assert.False(SlipMoneySource.AllowsAuto(SlipMoneySource.Evidence.None));
    }

    private static SlipAutoRecordRule.Facts Good() => new(SettingOn: true, SenderVerified: true, SingleMatch: true, QrVerified: true, RefDuplicate: false,
        SlipDateTh: Today, DocumentDateTh: Today.AddDays(-3), Money: SlipMoneySource.Decide(null, Unique(AccK), new[] { AccK }),
        ReceivingBank: AccK, ReceivingDocChannelBank: null, ReceivingProblem: null, NowUtc: Now);

    [Fact]
    public void ผ่านทุกด่าน_บันทึกเอง_สืบทอดแหล่งเงินเอกสารที่เป็นบัญชีเดียวกันก็ผ่าน()
    {
        var v = SlipAutoRecordRule.Decide(Good());
        Assert.True(v.Auto);
        Assert.Equal(SlipAutoRecordRule.Codes.Ok, v.Code);
        Assert.False(v.UsedAi);
        Assert.True(SlipAutoRecordRule.Decide(Good() with { ReceivingBank = null, ReceivingDocChannelBank = AccK }).Auto);
    }

    [Fact]
    public void ทุกด่านที่ไม่ผ่าน_ไม่บันทึกเอง_พร้อมรหัสเหตุ()
    {
        var cases = new (SlipAutoRecordRule.Facts F, string Code)[]
        {
            (Good() with { SettingOn = false }, SlipAutoRecordRule.Codes.SettingOff),
            (Good() with { SenderVerified = false }, SlipAutoRecordRule.Codes.SenderUnverified),
            (Good() with { SingleMatch = false }, SlipAutoRecordRule.Codes.NotSingle),
            (Good() with { QrVerified = false }, SlipAutoRecordRule.Codes.NoQr),
            (Good() with { RefDuplicate = true }, SlipAutoRecordRule.Codes.DuplicateRef),
            (Good() with { SlipDateTh = null }, SlipAutoRecordRule.Codes.NoDate),
            (Good() with { SlipDateTh = Today.AddDays(1) }, SlipAutoRecordRule.Codes.DateOutOfRange),
            (Good() with { SlipDateTh = Today.AddDays(-(SlipAutoRecordRule.MaxSlipAgeDays + 1)) }, SlipAutoRecordRule.Codes.DateOutOfRange),
            (Good() with { SlipDateTh = Today.AddDays(-4) }, SlipAutoRecordRule.Codes.DateOutOfRange),   // ก่อนวันเอกสาร
            (Good() with { Money = SlipMoneySource.Decide(null, Kind(SlipAccountMatch.Kind.NotOurs), new[] { AccK }) }, SlipAutoRecordRule.Codes.MoneySource),
            (Good() with { Money = SlipMoneySource.Decide(null, null, new[] { AccK }) }, SlipAutoRecordRule.Codes.MoneySource),
            (Good() with { ReceivingProblem = "ต้องเลือกบัญชี" }, SlipAutoRecordRule.Codes.ReceivingConflict),
            (Good() with { ReceivingBank = AccS }, SlipAutoRecordRule.Codes.ReceivingConflict),
            (Good() with { ReceivingBank = null, ReceivingDocChannelBank = null }, SlipAutoRecordRule.Codes.ReceivingConflict),   // เอกสารลงเงินสด/ผังอื่น
        };
        foreach (var (f, code) in cases)
        {
            var v = SlipAutoRecordRule.Decide(f);
            Assert.False(v.Auto, code);
            Assert.Equal(code, v.Code);
            Assert.False(string.IsNullOrWhiteSpace(v.Reason));
        }
    }

    [Fact]
    public void เตือนซ้ำ_ไม่ถี่_มีเพดาน()
    {
        var first = Now.AddHours(-2);
        Assert.False(SlipMoneySource.ReminderDue(first, 0, null, Now));                       // ยังไม่ครบ 1 วัน
        Assert.True(SlipMoneySource.ReminderDue(Now.AddDays(-1), 0, null, Now));               // ครบ 1 วัน ⇒ ครั้งที่ 1
        Assert.False(SlipMoneySource.ReminderDue(Now.AddDays(-5), 1, Now.AddDays(-2), Now));   // ครั้งที่ 2 ต้องห่างครั้งก่อน 3 วัน
        Assert.True(SlipMoneySource.ReminderDue(Now.AddDays(-5), 1, Now.AddDays(-3), Now));
        Assert.False(SlipMoneySource.ReminderDue(Now.AddDays(-30), SlipMoneySource.MaxReminders, Now.AddDays(-20), Now));   // เพดาน
        Assert.Equal(2, SlipMoneySource.MaxReminders);
    }

    [Fact]
    public void ป้ายช่องทาง_และเหตุภายในไม่รั่วถึงผู้ซื้อ()
    {
        Assert.Equal("อีเมล", SlipAutoRecordRule.ChannelLabel(LineChatSlipRule.Sources.Email));
        Assert.Equal("กลุ่ม LINE ของ NextAcc", SlipAutoRecordRule.ChannelLabel(LineChatSlipRule.Sources.CentralGroup));
        Assert.DoesNotContain("บัญชี", LineChatSlipRule.Replies.AwaitingSeller.Replace("รายการเงินเข้า", ""));
        Assert.DoesNotContain("1,070.00", LineChatSlipRule.Replies.Recorded("INV-1", 1070m, showAmounts: false));
        Assert.Contains("1,070.00", LineChatSlipRule.Replies.Recorded("INV-1", 1070m, showAmounts: true));
    }
}
