using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;
using S = Accounting.Helpers.WhtCertIntakeStatus.State;

namespace Accounting.Tests;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §4.1 · เฟส 243) — ตัวตัดสินเดียวของสถานะ 50 ทวิ: ตาราง 10 สถานะ + ทิศตรงข้าม
/// (ใบไม่มี WHT ⇒ ไม่มีชิป · ยังไม่รับชำระ ⇒ NotYetPaid ไม่ใช่ค้าง — กันคำเตือนฟ้องใบถูก · หลักการ #8)
/// </summary>
public class WhtCertIntakeStatusTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);

    private static WhtCertIntakeStatus.Facts F(WhtCreditStatus? row, decimal wht = 300m, decimal received = 0m, bool pending = false,
        bool rejected = false, WhtEvidenceKind kind = WhtEvidenceKind.PaperCertificate, int? daysAgo = 12, decimal expected = 0m)
        => new(row == null ? 0m : wht, expected, received, row, pending, rejected, kind, daysAgo is int d ? Now.AddDays(-d) : null);

    public static IEnumerable<object[]> Table => new[]
    {
        new object[] { F(null), S.NotApplicable, "none", false },
        new object[] { F(null, expected: 300m), S.NotYetPaid, "muted", false },
        new object[] { F(WhtCreditStatus.Pending), S.AwaitingCert, "warning", false },
        new object[] { F(WhtCreditStatus.Pending, pending: true), S.Submitted, "info", false },
        new object[] { F(WhtCreditStatus.Pending, received: 100m), S.PartiallyReceived, "warning", false },
        new object[] { F(WhtCreditStatus.Received), S.Received, "success", false },
        new object[] { F(WhtCreditStatus.Pending, kind: WhtEvidenceKind.EWithholding), S.EWhtAwaitingEvidence, "muted", false },
        new object[] { F(WhtCreditStatus.Pending, rejected: true), S.Rejected, "warning", false },
        new object[] { F(WhtCreditStatus.Claimed), S.Claimed, "success", false },
        new object[] { F(WhtCreditStatus.Expired), S.WrittenOff, "muted", false },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void ตาราง_10_สถานะ(WhtCertIntakeStatus.Facts f, S expected, string tone, bool overdue)
    {
        var v = WhtCertIntakeStatus.Of(f, Now);
        Assert.Equal(expected, v.State);
        Assert.Equal(tone, v.Tone);
        Assert.Equal(overdue, v.IsOverdue);
    }

    [Fact]
    public void ครบทุกค่าของ_enum_อยู่ในตาราง()
        => Assert.Equal(Enum.GetValues<S>().OrderBy(x => x), Table.Select(r => (S)r[1]).Distinct().OrderBy(x => x));

    [Fact]
    public void ถูกหัก300ไม่มีใบ_วันที่31_เกินกำหนด_วันที่30_ยังไม่เกิน()
    {
        var d31 = WhtCertIntakeStatus.Of(F(WhtCreditStatus.Pending, daysAgo: 31), Now);
        Assert.Equal(S.AwaitingCert, d31.State);
        Assert.True(d31.IsOverdue);
        Assert.Equal("danger", d31.Tone);
        Assert.Equal(31, d31.DaysWaiting);
        // ทิศตรงข้าม: วันที่ 30 พอดี (เกณฑ์ "เกิน 30 วัน") ยังไม่แดง
        var d30 = WhtCertIntakeStatus.Of(F(WhtCreditStatus.Pending, daysAgo: 30), Now);
        Assert.False(d30.IsOverdue);
        Assert.Equal("warning", d30.Tone);
    }

    [Fact]
    public void ทิศตรงข้าม_ใบไม่มีWHT_ไม่มีชิป_ใบยังไม่ชำระ_ไม่ใช่การค้าง()
    {
        var none = WhtCertIntakeStatus.Of(F(null), Now);
        Assert.False(WhtCertIntakeStatus.ShowsChip(none.State));
        Assert.False(WhtCertIntakeStatus.NeedsFollowUp(none.State));
        // แถวทะเบียนมีแล้ว (เกณฑ์สิทธิ์ตั้ง 11910 ตอนออกใบ) แต่ยังไม่รับเงิน ⇒ NotYetPaid · ไม่นับวันค้าง · ไม่เกินกำหนดแม้ผ่านไป 90 วัน
        var unpaid = WhtCertIntakeStatus.Of(F(WhtCreditStatus.Pending, daysAgo: null), Now);
        Assert.Equal(S.NotYetPaid, unpaid.State);
        Assert.False(unpaid.IsOverdue);
        Assert.Null(unpaid.DaysWaiting);
        Assert.False(WhtCertIntakeStatus.ShowsChip(unpaid.State));
        Assert.False(WhtCertIntakeStatus.NeedsFollowUp(unpaid.State));
    }

    [Fact]
    public void มีใบรอยืนยัน_ชนะ_ได้รับบางส่วน_และไม่เกินกำหนด()
    {
        var v = WhtCertIntakeStatus.Of(F(WhtCreditStatus.Pending, received: 100m, pending: true, daysAgo: 60), Now);
        Assert.Equal(S.Submitted, v.State);
        Assert.False(v.IsOverdue);   // ลูกค้าส่งแล้ว — งานค้างอยู่ฝั่งเรา ไม่ใช่การค้างของลูกค้า
        Assert.True(WhtCertIntakeStatus.NeedsFollowUp(v.State));
    }

    [Fact]
    public void ตัวกรอง_ตัวเดียวกับชิป_ค่าที่ไม่รู้จักถูกปฏิเสธ()
    {
        Assert.True(WhtCertIntakeStatus.TryParseFilter(null, out var none));
        Assert.Null(none);
        Assert.True(WhtCertIntakeStatus.TryParseFilter("Overdue", out var od));
        Assert.Equal("overdue", od);
        Assert.False(WhtCertIntakeStatus.TryParseFilter("pending-ish", out _));
        var overdue = WhtCertIntakeStatus.Of(F(WhtCreditStatus.Pending, daysAgo: 45), Now);
        Assert.True(WhtCertIntakeStatus.MatchesFilter(overdue, "overdue"));
        Assert.True(WhtCertIntakeStatus.MatchesFilter(overdue, "awaiting"));
        Assert.False(WhtCertIntakeStatus.MatchesFilter(overdue, "received"));
        var received = WhtCertIntakeStatus.Of(F(WhtCreditStatus.Received), Now);
        Assert.True(WhtCertIntakeStatus.MatchesFilter(received, "received"));
        Assert.False(WhtCertIntakeStatus.MatchesFilter(received, "awaiting"));
    }
}
