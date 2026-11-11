using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 257 ชุด 2 (erp-review/2026-11-10/team-B.md · คำตัดสิน 257-2-1…257-2-4) — ตัวตัดสิน pure ของ 50 ทวิ ขาเข้า · ทุกด่านมีทิศตรงข้าม
/// (B-H1 รับใบหลังตัดสูญ · B-H2 สถานะหลังถอนการยืนยัน · B-M1 ประทับ Claimed · B-M2 ผู้จ่ายของใบเลขเดียวกัน · B-M3 e-WHT · B-L2 ทวงที่ข้าม · B-L4 ส่งผ่านคู่ค้า)
/// </summary>
public class WhtCertRound257Tests
{
    private static readonly Guid Je = Guid.NewGuid(), Rev = Guid.NewGuid();

    // ═══════════ B-H1 ═══════════

    [Fact]
    public void H1_ได้ใบบางส่วนแล้วตัดสูญส่วนที่เหลือ_รับใบถัดไปได้()
    {
        Assert.True(WhtCreditWriteOff.ReopensForLateCertificate(WhtCreditStatus.Expired, Je, null, 1_000m, 400m));
    }

    [Theory]
    [InlineData(WhtCreditStatus.Expired, true, false, 1_000, 0)]      // ตัดสูญทั้งแถว (ไม่เคยได้ใบ) ⇒ ทาง "ได้รับใบแล้ว" เดิม
    [InlineData(WhtCreditStatus.Expired, true, true, 1_000, 400)]     // JE ตัดสูญถูกกลับแล้ว
    [InlineData(WhtCreditStatus.Expired, false, false, 1_000, 400)]   // ตัดออกโดยไม่มี JE (คืนเงิน/ข้อมูลเก่า)
    [InlineData(WhtCreditStatus.Expired, true, false, 1_000, 1_000)]  // ได้ใบครบแล้ว ไม่มีอะไรให้ลง
    [InlineData(WhtCreditStatus.Pending, false, false, 1_000, 400)]   // แถวรอใบปกติ = ทางเดิม
    [InlineData(WhtCreditStatus.Claimed, false, false, 1_000, 400)]
    public void H1_ทิศตรงข้าม_แถวอื่นไม่ใช่เส้นกลับรายการตัดสูญ(WhtCreditStatus st, bool hasJe, bool reversed, double wht, double received)
        => Assert.False(WhtCreditWriteOff.ReopensForLateCertificate(st, hasJe ? Je : null, reversed ? Rev : null, (decimal)wht, (decimal)received));

    [Fact]
    public void H1_ปุ่มได้รับใบแล้ว_ตรงกับด่านของ_MarkReceived()
    {
        Assert.True(WhtCreditWriteOff.CanMarkReceived(WhtCreditStatus.Expired, Je, null, 0m));       // ตัดสูญทั้งแถว ⇒ ใช้ได้ (กลับ JE ให้)
        Assert.True(WhtCreditWriteOff.CanMarkReceived(WhtCreditStatus.Pending, null, null, 0m));
        Assert.False(WhtCreditWriteOff.CanMarkReceived(WhtCreditStatus.Expired, Je, null, 400m));    // ได้ใบบางส่วนแล้ว ⇒ ปุ่มตาย (เดิมแสดงแล้วถูกปฏิเสธ)
        Assert.False(WhtCreditWriteOff.CanMarkReceived(WhtCreditStatus.Pending, null, null, 400m));
        Assert.False(WhtCreditWriteOff.CanMarkReceived(WhtCreditStatus.Claimed, null, null, 0m));
        Assert.False(WhtCreditWriteOff.CanMarkReceived(WhtCreditStatus.Expired, null, null, 0m));   // ตัดออกโดยไม่มี JE
    }

    [Fact]
    public void H1_ข้อความหลังยืนยัน_เสนอตัดสูญส่วนที่เหลือ_ทิศตรงข้ามครบแล้วไม่เสนอ()
    {
        Assert.Null(WhtCertIntakeService.WrittenOffFollowUp(Array.Empty<(string, string, string, decimal)>()));
        var partial = WhtCertIntakeService.WrittenOffFollowUp(new[] { ("ลูกค้า ก", "JV-1", "JV-2", 100m) })!;
        Assert.Contains("JV-1", partial);
        Assert.Contains("100.00", partial);
        Assert.Contains("ตัดสูญ", partial);
        var full = WhtCertIntakeService.WrittenOffFollowUp(new[] { ("ลูกค้า ก", "JV-1", "JV-2", 0m) })!;
        Assert.Contains("ได้ใบครบแล้ว", full);
        Assert.DoesNotContain("ยังขาดใบ", full);
    }

    // ═══════════ B-H2 ═══════════

    [Theory]
    [InlineData(1_000, 0, WhtCreditStatus.Pending)]
    [InlineData(0.50, 0, WhtCreditStatus.Pending)]          // ถูกหัก ≤ ฿1 และไม่เหลือใบ ⇒ รอใบ (StatusForReceived จะตอบ Received)
    [InlineData(1_000, 400, WhtCreditStatus.Pending)]       // ยังเหลือใบอื่น ⇒ ได้บางส่วน
    [InlineData(1_000, 999.50, WhtCreditStatus.Received)]   // เหลือใบที่ครบภายในเกณฑ์ ±฿1
    public void H2_สถานะหลังถอนการยืนยัน(double wht, double remaining, WhtCreditStatus expected)
        => Assert.Equal(expected, WhtCreditableAmount.StatusAfterUnconfirm((decimal)wht, (decimal)remaining));

    [Theory]
    [InlineData(600, 1_000, 400, 600)]    // ใบนี้ 600 · อีกใบ 400 ⇒ ถอน 600
    [InlineData(600, 900, 400, 500)]      // ยอดแถวถูกลดไปแล้ว (เช่น GL ลด) ⇒ ถอนไม่เกินส่วนที่เหลือของใบนี้ — ใบอื่นไม่ถูกลด (ฝ่ายค้าน L4)
    [InlineData(600, 300, 400, 0)]        // ไม่เหลือส่วนของใบนี้ ⇒ 0 (ไม่ติดลบ)
    [InlineData(1_000, 1_000, 0, 1_000)]
    public void H2_L4_ถอนเฉพาะยอดของใบนี้(double alloc, double received, double others, double expected)
        => Assert.Equal((decimal)expected, WhtCreditableAmount.UnconfirmTake((decimal)alloc, (decimal)received, (decimal)others));

    [Fact]
    public void H1_ล็อกตามปีภาษีที่ยื่นแล้ว_ทิศตรงข้ามปีที่ยังไม่ยื่น()
    {
        var filed = new[] { 2025 };
        Assert.Equal(2025, WhtCreditFiledYearGuard.LockedYear(new DateTime(2025, 6, 1), 1, filed));
        Assert.Null(WhtCreditFiledYearGuard.LockedYear(new DateTime(2026, 6, 1), 1, filed));     // ปีที่ยังไม่ยื่น ⇒ ทำได้
        Assert.Null(WhtCreditFiledYearGuard.LockedYear(null, 1, filed));                         // ยังไม่มีใบ ⇒ ไม่มีเครดิตในแบบใด
        Assert.Equal(2025, WhtCreditFiledYearGuard.LockedYear(new DateTime(2026, 3, 1), 7, filed));   // รอบ ก.ค.–มิ.ย. ⇒ มี.ค. 2026 อยู่ปี 2025
        var msg = WhtCreditFiledYearGuard.Message(2025, "ตัดสูญ");
        Assert.Contains("2568", msg);
        Assert.Contains("ปลดล็อก", msg);
        Assert.Contains("ปีถัดไป", msg);
    }

    [Fact]
    public void M1_M2_ยอดเครดิตในรายงานไม่ตรงทะเบียน_ต้องสร้างใหม่_ทิศตรงข้ามตรงหรือต่างไม่เกินหนึ่งสตางค์ยื่นได้()
    {
        Assert.True(WhtCreditClaimStamp.ReportOutOfDate(1_000m, 1_400m));
        Assert.True(WhtCreditClaimStamp.ReportOutOfDate(0m, 300m));     // สร้างรายงานตอนยังไม่มีใบ แล้วได้ใบภายหลัง
        Assert.False(WhtCreditClaimStamp.ReportOutOfDate(1_000m, 1_000m));
        Assert.False(WhtCreditClaimStamp.ReportOutOfDate(1_000m, 1_000.01m));
    }

    [Fact]
    public void H2_ทิศตรงข้าม_StatusForReceived_เดิมไม่เปลี่ยน()
        => Assert.Equal(WhtCreditStatus.Received, WhtCreditableAmount.StatusForReceived(0.50m, 0m));

    // ═══════════ B-M1 ═══════════

    [Fact]
    public void M1_ถอนการประทับ_กลับเป็นได้รับใบแล้ว_ประทับใหม่ได้()
        => Assert.Equal(WhtCreditStatus.Received, WhtCreditClaimStamp.ReleasedStatus);   // เส้นจริงล็อกที่ Db M1_ประทับ_Claimed_…

    // ═══════════ B-M2 ═══════════

    private static readonly Guid C1 = Guid.NewGuid(), C2 = Guid.NewGuid();
    private const string T1 = "0107556000116", T2 = "0105556123453";

    [Fact]
    public void M2_เลขผู้จ่ายอ่านได้_ตัดสินด้วยเลข()
    {
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Same, WhtCertIntakeVerdict.SameCertPayer(T1, null, T1, null));
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Different, WhtCertIntakeVerdict.SameCertPayer(T1, null, T2, null));
    }

    [Fact]
    public void M2_L5_ฝั่งหนึ่งมีเลขผู้จ่าย_อีกฝั่งไม่มี_ไม่รู้_ด่านอาจซ้ำ()
    {
        // ฝ่ายค้าน L5: เดิม "มีเลข vs ไม่มีเลข" = ต่างผู้จ่าย ⇒ ใบเดิมที่อ่านเลขไม่ได้กับใบใหม่ที่อ่านได้ลงซ้ำ
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Unknown, WhtCertIntakeVerdict.SameCertPayer(T1, null, null, null));
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Unknown, WhtCertIntakeVerdict.SameCertPayer(T1, C1, null, C1));
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Unknown, WhtCertIntakeVerdict.SameCertPayer(null, null, T1, null));
        // ทิศตรงข้าม: มีเลขทั้งสองฝั่ง ⇒ ตัดสินด้วยเลขตามเดิม
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Same, WhtCertIntakeVerdict.SameCertPayer(T1, null, T1, C2));
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Different, WhtCertIntakeVerdict.SameCertPayer(T1, C1, T2, C1));
    }

    [Fact]
    public void M2_เลขผู้จ่ายอ่านไม่ได้_ใช้ผู้ติดต่อ_ไม่รู้ทั้งคู่ไม่ใช่ซ้ำ()
    {
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Same, WhtCertIntakeVerdict.SameCertPayer(null, C1, null, C1));
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Different, WhtCertIntakeVerdict.SameCertPayer("", C1, null, C2));
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Unknown, WhtCertIntakeVerdict.SameCertPayer(null, C1, T1, null));
        // เดิม: ไม่มีเลขผู้จ่าย ⇒ เลขที่เดียวกันถือว่าซ้ำ ⇒ ใบ "001" ของลูกค้าอื่นถูกกลืน · ตอนนี้ = ไม่รู้ (สร้างใบ + ด่าน "อาจซ้ำ")
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Unknown, WhtCertIntakeVerdict.SameCertPayer(null, null, T1, C1));
        Assert.Equal(WhtCertIntakeVerdict.CertPayerMatch.Unknown, WhtCertIntakeVerdict.SameCertPayer("12", null, null, null));
    }

    [Fact]
    public void M2_ด่านอาจซ้ำ_บอกเลขที่และทางไปต่อ()
    {
        var g = WhtCertIntakeService.PossibleDuplicateGate("001");
        Assert.Contains("001", g);
        Assert.Contains("ตีกลับ", g);
        Assert.Contains("กรอกเลขผู้จ่าย", g);
    }

    // ═══════════ B-M3 ═══════════

    [Fact]
    public void M3_แจ้ง_eWHT_ได้เฉพาะแถวรอใบที่ยังไม่ได้ใบ_และป้ายไปถึง()
    {
        Assert.True(WhtCertIntakeStatus.CanMarkEWhtPending(WhtCreditStatus.Pending, 0m));
        Assert.False(WhtCertIntakeStatus.CanMarkEWhtPending(WhtCreditStatus.Pending, 100m));   // ได้บางส่วน ⇒ ป้าย "ได้บางส่วน" ชนะ
        Assert.False(WhtCertIntakeStatus.CanMarkEWhtPending(WhtCreditStatus.Received, 0m));
        Assert.False(WhtCertIntakeStatus.CanMarkEWhtPending(WhtCreditStatus.Expired, 0m));
        Assert.False(WhtCertIntakeStatus.CanMarkEWhtPending(WhtCreditStatus.Claimed, 0m));
        var now = new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc);
        var v = WhtCertIntakeStatus.Of(new WhtCertIntakeStatus.Facts(300m, 0m, 0m, WhtCreditStatus.Pending, false, false, WhtEvidenceKind.EWithholding,
            now.AddDays(-5)), now);
        Assert.Equal(WhtCertIntakeStatus.State.EWhtAwaitingEvidence, v.State);
        Assert.True(WhtCertIntakeStatus.MatchesFilter(v, "ewht"));
    }

    // ═══════════ B-L2 ═══════════

    [Theory]
    [InlineData("Skipped", true, true)]
    [InlineData("Skipped", false, false)]   // ยังไม่มีอีเมล ⇒ ไม่บันทึกซ้ำทุกวัน
    [InlineData("Queued", true, false)]     // เข้าคิวแล้ว ⇒ ไม่ส่งซ้ำ
    [InlineData(null, true, false)]
    public void L2_ขั้นที่ข้าม_ลองใหม่เมื่อส่งได้(string? outcome, bool deliverable, bool expected)
        => Assert.Equal(expected, WhtCertReminderCadence.RetrySkippedStep(outcome, deliverable));

    // ═══════════ B-L4 ═══════════

    [Fact]
    public void L4_กล่องรับตีกลับ_ไม่ใช่ส่งแล้ว_ทิศตรงข้ามรับไว้_คือส่งแล้ว()
    {
        Assert.True(PartnerExchangeRules.IntakeRejected("Rejected"));
        Assert.False(PartnerExchangeRules.IntakeRejected("AwaitingConfirm"));
        Assert.False(PartnerExchangeRules.IntakeRejected("Confirmed"));
        Assert.Contains("ผู้ถูกหัก", PartnerExchangeRules.IntakeRejectedReason("ผู้ถูกหักบนใบไม่ใช่บริษัทเรา"));
        Assert.StartsWith("ส่งผ่านคู่ค้าไม่สำเร็จ", PartnerExchangeRules.WhtDeliveryLabel(null, "เหตุ"));
        Assert.Equal("ส่งเข้ากล่องรับของคู่ค้าแล้ว", PartnerExchangeRules.WhtDeliveryLabel(DateTime.UtcNow, null));
        Assert.Null(PartnerExchangeRules.WhtDeliveryLabel(null, null));
    }
}
