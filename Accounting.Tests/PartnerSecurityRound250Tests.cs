using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 250 WP-02b (AUDIT_249_COVERAGE M-1/M-2 · AUTOPILOT_BILLING_DESIGN §6.1 P-1/P-2/P-6 · G-T7) — ตัวตัดสิน pure
/// <para>P-2 <see cref="PartnerCompanyMatch"/>: เลขภาษี + สาขา · กำกวม ⇒ ไม่เดา · ทิศตรงข้าม: คู่ที่ไม่เคยกรอกสาขา (บริษัทเดียวของเลขนี้) ยังจับได้</para>
/// <para>P-6 <see cref="PartnerRoutingFailedNote"/>: ป้ายบนเอกสารเขียน/ถอด/ไม่สะสม · ใบที่ไม่มีป้ายไม่ถูกแตะ</para>
/// <para>M-2 <see cref="PortalSlipPending.CountsAsReceived"/>: แถวสลิปรุ่นเก่าที่ยังไม่ตรวจไม่นับ · แถวรับชำระปกติ (รวมที่ผูก intent) นับตามเดิม</para>
/// </summary>
public class PartnerSecurityRound250Tests
{
    private static readonly Guid Hq = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Br1 = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Br2 = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    // ───────── P-2 ─────────

    [Fact]
    public void สองบริษัทเลขเดียวกันต่างสาขา_ระบุสาขา_ได้บริษัทของสาขานั้น_ไม่ขึ้นกับลำดับ()
    {
        var a = new[] { new PartnerCompanyCandidate(Hq, "00000"), new PartnerCompanyCandidate(Br1, "00001") };
        var b = a.Reverse().ToArray();
        foreach (var list in new[] { a, b })
        {
            Assert.Equal(new PartnerCompanyPick(PartnerMatchKind.Matched, Br1), PartnerCompanyMatch.Pick(list, "00001"));
            Assert.Equal(new PartnerCompanyPick(PartnerMatchKind.Matched, Br1), PartnerCompanyMatch.Pick(list, "1"));
            Assert.Equal(new PartnerCompanyPick(PartnerMatchKind.Matched, Hq), PartnerCompanyMatch.Pick(list, "00000"));
        }
    }

    [Fact]
    public void ระบุสาขาที่ไม่มีบริษัท_ไม่ถอยไปสำนักงานใหญ่()
    {
        var list = new[] { new PartnerCompanyCandidate(Hq, "00000") };
        Assert.Equal(PartnerMatchKind.None, PartnerCompanyMatch.Pick(list, "00003").Kind);
    }

    [Fact]
    public void ไม่ระบุสาขา_หลายบริษัท_จับสำนักงานใหญ่หนึ่งเดียว_ไม่มีสำนักงานใหญ่_กำกวม()
    {
        var withHq = new[] { new PartnerCompanyCandidate(Br1, "00001"), new PartnerCompanyCandidate(Hq, null) };
        Assert.Equal(new PartnerCompanyPick(PartnerMatchKind.Matched, Hq), PartnerCompanyMatch.Pick(withHq, null));
        var noHq = new[] { new PartnerCompanyCandidate(Br1, "00001"), new PartnerCompanyCandidate(Br2, "00002") };
        Assert.Equal(new PartnerCompanyPick(PartnerMatchKind.Ambiguous, null), PartnerCompanyMatch.Pick(noHq, "  "));
    }

    [Fact]
    public void สองบริษัทเลขเดียวกันสาขาเดียวกัน_กำกวม_ไม่สุ่ม()
    {
        var dup = new[] { new PartnerCompanyCandidate(Hq, "00000"), new PartnerCompanyCandidate(Br1, null) };
        Assert.Equal(PartnerMatchKind.Ambiguous, PartnerCompanyMatch.Pick(dup, "00000").Kind);
        Assert.Equal(PartnerMatchKind.Ambiguous, PartnerCompanyMatch.Pick(dup, null).Kind);
    }

    [Fact]
    public void ทิศตรงข้าม_คู่เดิมที่ไม่เคยกรอกสาขา_บริษัทเดียวของเลข_ยังจับได้_แม้บริษัทเป็นสาขา()
    {
        // คู่ Accepted เดิมที่ผู้ติดต่อไม่ระบุสาขา — พฤติกรรมเดิมต้องไม่หาย (ไม่กำกวม = มีบริษัทเดียว)
        var only = new[] { new PartnerCompanyCandidate(Br1, "00001") };
        Assert.Equal(new PartnerCompanyPick(PartnerMatchKind.Matched, Br1), PartnerCompanyMatch.Pick(only, null));
        Assert.Equal(PartnerMatchKind.None, PartnerCompanyMatch.Pick(Array.Empty<PartnerCompanyCandidate>(), null).Kind);
        Assert.Equal(PartnerMatchKind.None, PartnerCompanyMatch.Pick(null, "00000").Kind);
    }

    [Theory]
    [InlineData("0105556000011", "0105556000011")]
    [InlineData("0-1055-56000-01-1", "0105556000011")]
    [InlineData("-", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("12345", null)]
    public void เลขภาษี_13หลักเท่านั้น_ขีดไม่ใช่เลข(string? raw, string? expected)
        => Assert.Equal(expected, PartnerCompanyMatch.NormalizeTaxId(raw));

    [Fact]
    public void คำตอบเชิญ_ไม่บอกผล_ข้อความคงที่()
    {
        // P-1: ข้อความเดียวทุกกรณี — ไม่มี "ไม่พบ" / ไม่มีเลขภาษีที่ผู้เชิญกรอกสะท้อนกลับ
        Assert.DoesNotContain("ไม่พบ", PartnerCompanyMatch.InviteAckMessage);
        Assert.Contains("ถ้าผู้รับใช้ NextAcc", PartnerCompanyMatch.InviteAckMessage);
    }

    // ───────── P-6 ─────────

    private const string AckNote = "รับทราบคำเตือน §86/4 แล้ว";

    [Fact]
    public void ป้ายส่งคู่ค้าล้ม_ต่อท้าย_ไม่ทับหมายเหตุเดิม_ล้มซ้ำไม่สะสม()
    {
        var once = PartnerRoutingFailedNote.Append(AckNote, "สาเหตุแรก\nบรรทัดสอง");
        Assert.StartsWith(AckNote, once);
        Assert.True(PartnerRoutingFailedNote.Has(once));
        Assert.DoesNotContain("\nบรรทัดสอง", once);   // ย่อหน้าเดียว — Clear ถอดได้ครบ
        var twice = PartnerRoutingFailedNote.Append(once, "สาเหตุที่สอง");
        Assert.Equal(1, (twice.Length - twice.Replace(PartnerRoutingFailedNote.Marker, "").Length) / PartnerRoutingFailedNote.Marker.Length);
        Assert.Contains("สาเหตุที่สอง", twice);
        Assert.DoesNotContain("สาเหตุแรก", twice);
    }

    [Fact]
    public void ส่งสำเร็จ_ล้างป้าย_คงหมายเหตุอื่น_ใบที่ไม่มีป้ายไม่ถูกแตะ()
    {
        var stamped = PartnerRoutingFailedNote.Append(AckNote, "x");
        Assert.Equal(AckNote, PartnerRoutingFailedNote.Clear(stamped));
        Assert.Null(PartnerRoutingFailedNote.Clear(PartnerRoutingFailedNote.Append(null, "x")));
        Assert.Equal(AckNote, PartnerRoutingFailedNote.Clear(AckNote));       // ทิศตรงข้าม: ไม่มีป้าย = ค่าเดิมทุกตัวอักษร
        Assert.False(PartnerRoutingFailedNote.Has(AckNote));
        // ป้าย e-Tax ล้มอยู่ในหมายเหตุเดียวกันได้ — ล้างป้ายคู่ค้าไม่แตะป้าย e-Tax
        var both = PartnerRoutingFailedNote.Append(EtaxAutoFailedNote.Append(AckNote, "RD ล่ม"), "คู่ค้า");
        var cleared = PartnerRoutingFailedNote.Clear(both);
        Assert.True(EtaxAutoFailedNote.Has(cleared));
        Assert.False(PartnerRoutingFailedNote.Has(cleared));
    }

    // ───────── M-2 ─────────

    private static Payment Pay(string? notes, Guid? intent = null, string? createdBy = PortalSlipPending.LegacyCreatedBy)
        => new() { Notes = notes, PaymentIntentId = intent, PaymentNumber = "PAY-1", Amount = 100m, CreatedBy = createdBy };

    [Fact]
    public void สลิปพอร์ทัลรุ่นเก่าที่ยังไม่ตรวจ_ไม่นับเป็นเงินรับ_แถวปกตินับตามเดิม()
    {
        var counts = PortalSlipPending.CountsAsReceived.Compile();
        var legacy = Pay("[PENDING-VERIFY] แนบสลิปจาก Portal ลูกค้า · ไฟล์: /uploads/x.jpg");
        Assert.False(counts(legacy));

        // ทิศตรงข้าม: แถวรับชำระจริงทุกแบบต้องนับเหมือนเดิม
        Assert.True(counts(Pay(null)));
        Assert.True(counts(Pay("รับชำระเงินสด")));
        Assert.True(counts(Pay("หมายเหตุ [PENDING-VERIFY] อยู่กลางข้อความ")));             // ป้ายต้องขึ้นต้นเท่านั้น
        Assert.True(counts(Pay("[PENDING-VERIFY] …", Guid.NewGuid())));                    // ผูก intent = ผ่านการยืนยันแล้ว
        // (ฝ่ายค้าน L1) ป้ายเดียวกันแต่ผู้สร้างไม่ใช่พอร์ทัล (พิมพ์เอง/นำเข้า CSV) = แถวรับชำระจริง ⇒ นับ
        Assert.True(counts(Pay("[PENDING-VERIFY] พิมพ์เองโดยพนักงาน", createdBy: "staff@x")));
    }

    [Fact]
    public void คำอธิบายรายการรอยืนยัน_มีเลขเอกสารและไฟล์สลิป()
    {
        var d = PortalSlipPending.IntentDescription("INV-0001", "/uploads/portal-slips/2026-11/a.jpg");
        Assert.Contains("INV-0001", d);
        Assert.Contains("/uploads/portal-slips/2026-11/a.jpg", d);
        Assert.Contains("รอตรวจ", d);
    }

    // ───────── ฝ่ายค้านรอบ 250 ─────────

    [Fact]
    public void L1_หมายเหตุขึ้นต้นป้ายแต่ผู้สร้างไม่ใช่พอร์ทัล_ยังนับเป็นเงินรับ()
    {
        var counts = PortalSlipPending.CountsAsReceived.Compile();
        var typedByUser = new Payment { Notes = "[PENDING-VERIFY] ผู้ใช้พิมพ์เอง", CreatedBy = "user-1", PaymentNumber = "P", Amount = 1m };
        Assert.True(counts(typedByUser));
        Assert.True(PortalSlipPending.WasCountedAsReceived(typedByUser));
        var legacy = new Payment { Notes = "[PENDING-VERIFY] x", CreatedBy = PortalSlipPending.LegacyCreatedBy, PaymentNumber = "P", Amount = 1m };
        Assert.False(PortalSlipPending.WasCountedAsReceived(legacy));
        Assert.True(PortalSlipPending.UnverifiedLegacy.Compile()(legacy));
        Assert.False(PortalSlipPending.UnverifiedLegacy.Compile()(typedByUser));
    }

    [Theory]
    [InlineData(DocumentStatus.Approved, 1070, 500, true)]
    [InlineData(DocumentStatus.PartiallyPaid, 570, 570, true)]      // งวด 2 พอดียอดค้าง
    [InlineData(DocumentStatus.PartiallyPaid, 570, 600, false)]     // เกินยอดค้าง
    [InlineData(DocumentStatus.Paid, 0, 100, false)]                // ชำระครบแล้ว
    [InlineData(DocumentStatus.Draft, 1070, 100, false)]
    [InlineData(DocumentStatus.Voided, 1070, 100, false)]
    [InlineData(DocumentStatus.Approved, 1070, 0, false)]
    public void H2_รับสลิปตัดสินที่ยอดค้าง(DocumentStatus status, double balance, double amount, bool ok)
        => Assert.Equal(ok, DocumentSlipIntents.UploadProblem(status, (decimal)balance, (decimal)amount) == null);

    [Fact]
    public void H1_ยอดสลิปเกินยอดค้าง_เผื่อปัดเศษหนึ่งสตางค์()
    {
        Assert.True(DocumentSlipIntents.ExceedsBalance(100.02m, 100m));
        Assert.False(DocumentSlipIntents.ExceedsBalance(100.01m, 100m));
        Assert.False(DocumentSlipIntents.ExceedsBalance(50m, 100m));
    }

    [Fact]
    public void M4_วันรับเงิน_ตั้งต้นวันแนบสลิปเวลาไทย_ระบุได้_อนาคตไม่ได้()
    {
        var now = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
        var uploaded = new DateTime(2026, 11, 2, 18, 30, 0, DateTimeKind.Utc);   // 3 พ.ย. 01:30 เวลาไทย
        Assert.Equal((DateTime?)new DateTime(2026, 11, 3), DocumentSlipIntents.ReceivedDate(null, uploaded, now).PaidAtUtc);
        Assert.Equal((DateTime?)new DateTime(2026, 11, 1), DocumentSlipIntents.ReceivedDate(new DateTime(2026, 11, 1), uploaded, now).PaidAtUtc);
        var future = DocumentSlipIntents.ReceivedDate(new DateTime(2026, 11, 8), uploaded, now);
        Assert.Null(future.PaidAtUtc);
        Assert.NotNull(future.Problem);
    }

    [Theory]
    [InlineData("[PENDING-VERIFY] แนบสลิปจาก Portal ลูกค้า · ไฟล์: /uploads/portal-slips/2026-10/a.jpg", "/uploads/portal-slips/2026-10/a.jpg")]
    [InlineData("[PENDING-VERIFY] แนบสลิปจาก Portal ลูกค้า", null)]
    [InlineData(null, null)]
    public void M6_ไฟล์สลิปจากหมายเหตุเดิม(string? notes, string? expected)
        => Assert.Equal(expected, DocumentSlipIntents.SlipUrlFromLegacyNotes(notes));

    [Fact]
    public void M5_ข้อความสาขาไม่ตรง_บอกเลขและสาขา()
    {
        var m = PartnerCompanyMatch.BranchMismatchRouteMessage("0105556000011", null);
        Assert.Contains("0105556000011", m);
        Assert.Contains("00000", m);
    }
}
