using Accounting.Helpers;
using Xunit;
using Kind = Accounting.Helpers.SlipAccountMatch.Kind;
using C = Accounting.Tests.SlipCorpusRound264;

namespace Accounting.Tests;

/// <summary>
/// รอบ 264 แพ็ก P2 (คำตัดสิน 264-4-4) — <see cref="SlipAccountMatch"/> กับคู่กรณีจาก<b>สลิปจริง</b> · ตรงเดียว / หลาย / ไม่ใช่ของเรา / ธนาคารไม่ตรง /
/// เลขท้ายซ้ำกันข้ามธนาคาร / ข้อมูลบัญชีของเราไม่ครบ / บัตร-บิล-พร้อมเพย์ไม่จับคู่
/// </summary>
public class SlipAccountMatchTests
{
    private static readonly Guid Kb = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Kb2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Ktb = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Scb = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Odd = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    private static OcrSlipText.Reading Read(C.Slip s) => OcrSlipText.Parse(s.Text, 2026, s.Qr);
    private static SlipAccountMatch.AccountKey Acc(Guid id, string? bank, string? no) => new(id, bank, no);

    [Fact]
    public void ผู้รับบนสลิปกรุงไทย_ไป_กสิกร_ตรงบัญชีกสิกรเดียว()
    {
        var payee = Read(C.KtbToKbank).Payee;   // กสิกรไทย xxx-x-xx168-1
        var r = SlipAccountMatch.Match(payee, new[]
        {
            Acc(Kb, "ธนาคารกสิกรไทย (KBANK)", "123-4-56168-1"),
            Acc(Ktb, "ธนาคารกรุงไทย", "987-6-54321-0"),
        });
        Assert.Equal(Kind.Unique, r.Kind);
        Assert.Equal(Kb, r.AccountId);
        Assert.Equal("กสิกรไทย ••1681", r.PartyLabel);
        Assert.False(r.UsedAi);
    }

    [Fact]
    public void เลขท้ายตรงแต่ธนาคารไม่ตรง_ไม่ใช่ของเรา()
    {
        var payee = Read(C.KtbToKbank).Payee;   // กสิกร ••1681
        var r = SlipAccountMatch.Match(payee, new[] { Acc(Ktb, "กรุงไทย", "111-1-11168-1"), Acc(Scb, "SCB", "222-222168-1") });
        Assert.Equal(Kind.NotOurs, r.Kind);
        Assert.Null(r.AccountId);
    }

    [Fact]
    public void เลขท้ายเหมือนกันสองบัญชีธนาคารเดียวกัน_ไม่เดา()
    {
        var payee = Read(C.KbizTransferMemo).Payee;   // กสิกร xxx-x-x8844-x (หลักสุดท้ายถูกปิด)
        var r = SlipAccountMatch.Match(payee, new[] { Acc(Kb, "กสิกรไทย", "123-4-58844-1"), Acc(Kb2, "Kasikornbank", "999-9-98844-7") });
        Assert.Equal(Kind.Many, r.Kind);
        Assert.Equal(2, r.Candidates.Count);
        Assert.Null(r.AccountId);
    }

    [Fact]
    public void หลักที่ปิดไม่ถูกเติม_หลักที่เห็นต้องตรงตำแหน่ง()
    {
        var payer = Read(C.KbizTransferMemo).Payer;   // xxx-x-x0621-x ⇒ 0621 ที่ตำแหน่ง 2..5 จากขวา
        Assert.Equal(Kind.Unique, SlipAccountMatch.Match(payer, new[] { Acc(Kb, "KBANK", "000-0-00621-9") }).Kind);
        // 0621 อยู่ท้ายสุด (เลื่อนตำแหน่ง) ⇒ ไม่ตรง
        Assert.Equal(Kind.NotOurs, SlipAccountMatch.Match(payer, new[] { Acc(Kb, "KBANK", "000-0-00006-21") }).Kind);
    }

    [Fact]
    public void ฝั่งผู้จ่าย_SCB_ธนาคารจาก_QR_ตรงบัญชี_SCB()
    {
        var payer = Read(C.ScbBillStore).Payer;   // 014 (QR) xxx-xxx189-5
        var r = SlipAccountMatch.Match(payer, new[] { Acc(Scb, "ไทยพาณิชย์", "123-456189-5"), Acc(Kb, "กสิกรไทย", "123-4-56189-5") });
        Assert.Equal(Kind.Unique, r.Kind);
        Assert.Equal(Scb, r.AccountId);
    }

    [Fact]
    public void บัญชีของเรามีธนาคารนอกตาราง_เลขตรง_ไม่ยืนยัน_Unique()
    {
        var payee = Read(C.KtbToKbank).Payee;
        var r = SlipAccountMatch.Match(payee, new[] { Acc(Kb, "กสิกรไทย", "123-4-56168-1"), Acc(Odd, "ธนาคารนอกตาราง", "555-5-55168-1") });
        Assert.Equal(Kind.Many, r.Kind);
    }

    [Fact]
    public void ไม่ตรงแต่มีบัญชีที่ข้อมูลไม่ครบ_ไม่กล่าวหาว่าไม่ใช่ของเรา()
    {
        var payee = Read(C.KtbToKbank).Payee;
        var r = SlipAccountMatch.Match(payee, new[] { Acc(Kb, "กสิกรไทย", "123-4-56000-0"), Acc(Odd, "กสิกรไทย", null) });
        Assert.Equal(Kind.Unreadable, r.Kind);
        Assert.Equal(Kind.NotOurs, SlipAccountMatch.Match(payee, new[] { Acc(Kb, "กสิกรไทย", "123-4-56000-0") }).Kind);
    }

    [Theory]
    [InlineData(nameof(C.KbizBillSso))]          // Biller
    [InlineData(nameof(C.ScbBillStore))]         // Biller
    [InlineData(nameof(C.ScbPromptPayMobile))]   // พร้อมเพย์ไปเบอร์ xxx-xxx-0035
    public void ผู้รับที่ไม่ใช่บัญชีธนาคาร_ไม่จับคู่เด็ดขาด(string slip)
    {
        var payee = Read(Slip(slip)).Payee;
        // แม้บัญชีของเราลงท้ายด้วยเลขเดียวกัน (0035) ก็ไม่จับคู่
        var r = SlipAccountMatch.Match(payee, new[] { Acc(Scb, "SCB", "123-456-0035"), Acc(Kb, "KBANK", "000-0-00003-5") });
        Assert.Equal(Kind.NotAnAccount, r.Kind);
        Assert.Null(r.AccountId);
    }

    [Theory]
    [InlineData(nameof(C.KplusSameBank))]   // ไม่มีป้าย + QR ตรงทั้งสองบล็อก
    [InlineData(nameof(C.TtbCardEnglish))]
    [InlineData(nameof(C.KplusCardBill))]
    public void ไม่รู้บทบาท_อ่านไม่ได้(string slip)
    {
        var r = Read(Slip(slip));
        Assert.Equal(Kind.Unreadable, SlipAccountMatch.Match(r.Payee, new[] { Acc(Kb, "KBANK", "123-4-58177-1") }).Kind);
        Assert.Equal(Kind.Unreadable, SlipAccountMatch.Match(r.Payer, new[] { Acc(Kb, "KBANK", "123-4-52577-1") }).Kind);
    }

    [Fact]
    public void เลขปิดบางที่_OCR_อ่านเพี้ยน_อ่านไม่ได้_และเห็นไม่ถึงเกณฑ์_อ่านไม่ได้()
    {
        var payer = Read(C.ScbBillRefs).Payer;   // "2%%%<-%%%189-5" ⇒ ไม่เชื่อ mask
        Assert.Equal(Kind.Unreadable, SlipAccountMatch.Match(payer, new[] { Acc(Scb, "SCB", "123-456189-5") }).Kind);
        var two = new OcrSlipText.SlipParty(OcrSlipText.PartyKind.BankAccount, "004", false, "xxx-x-xxxx9-1", null, null);
        Assert.Equal(2, two.VisibleDigits.Count);
        Assert.Equal(Kind.Unreadable, SlipAccountMatch.Match(two, new[] { Acc(Kb, "KBANK", "123-4-56789-1") }).Kind);
        Assert.Equal(3, SlipAccountMatch.MinVisibleDigits);
    }

    [Fact]
    public void ฝ่ายค้าน_L1_เลขท้ายต่างจากบัญชีธนาคารเดียวกันหนึ่งหลัก_ไม่กล่าวหา_สองหลักหรือธนาคารอื่น_ยังไม่ใช่ของเรา()
    {
        var payee = Read(C.KtbToKbank).Payee;   // กสิกร ••1681
        // หนึ่งหลักต่าง (OCR 8↔3) ⇒ อ่านไม่ได้ (ไม่ใช่ NotOurs · ไม่ใช่ Unique)
        var one = SlipAccountMatch.Match(payee, new[] { Acc(Kb, "กสิกรไทย", "123-4-56163-1") });
        Assert.Equal(Kind.Unreadable, one.Kind);
        Assert.Null(one.AccountId);
        // สองหลักต่าง ⇒ ไม่ใช่ของเรา
        Assert.Equal(Kind.NotOurs, SlipAccountMatch.Match(payee, new[] { Acc(Kb, "กสิกรไทย", "123-4-56133-1") }).Kind);
        // หนึ่งหลักต่างแต่คนละธนาคาร ⇒ ไม่ใช่ของเรา
        Assert.Equal(Kind.NotOurs, SlipAccountMatch.Match(payee, new[] { Acc(Ktb, "กรุงไทย", "123-4-56163-1") }).Kind);
    }

    private static C.Slip Slip(string name) => (C.Slip)typeof(C).GetField(name)!.GetValue(null)!;
}
