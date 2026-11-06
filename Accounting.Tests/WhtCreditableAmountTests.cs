using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND §7.1) — ตัวคำนวณเครดิตเดียวของทุกผู้อ่าน (ภ.ง.ด.50 · เครื่องคำนวณ · ทะเบียน · รายงานปลายปี):
/// ใบรวมครอบหลายใบแจ้งหนี้ / ผ่อน 3 งวดได้ใบงวดแรก ⇒ เครดิตตามใบจริง ไม่ใช่ทั้งแถว · ทิศตรงข้าม: แถวรุ่นเก่าที่ Received (ReceivedAmount = 0) ยังเต็มแถว
/// </summary>
public class WhtCreditableAmountTests
{
    [Fact]
    public void ผ่อน3งวด_ได้ใบงวดแรก_เครดิตเฉพาะใบเดียว()
    {
        // ถูกหักรวม 300 (3 งวด × 100) · ได้ใบงวดแรก 100
        Assert.Equal(100m, WhtCreditableAmount.Of(WhtCreditStatus.Pending, 300m, 100m));
        Assert.Equal(200m, WhtCreditableAmount.Missing(WhtCreditStatus.Pending, 300m, 100m));
    }

    [Fact]
    public void ทิศตรงข้าม_แถวเก่าReceived_ไม่มียอดได้รับ_ยังเครดิตเต็มแถว_และPendingไม่มีใบ_เป็นศูนย์()
    {
        Assert.Equal(300m, WhtCreditableAmount.Of(WhtCreditStatus.Received, 300m, 0m));
        Assert.Equal(300m, WhtCreditableAmount.Of(WhtCreditStatus.Claimed, 300m, 0m));
        Assert.Equal(0m, WhtCreditableAmount.Of(WhtCreditStatus.Pending, 300m, 0m));
        Assert.Equal(300m, WhtCreditableAmount.Missing(WhtCreditStatus.Pending, 300m, 0m));
        Assert.Equal(0m, WhtCreditableAmount.Missing(WhtCreditStatus.Received, 300m, 0m));
    }

    [Fact]
    public void ตัดสูญรุ่นเก่า_เป็นศูนย์_และยอดได้รับเกินยอดถูกหัก_ไม่เกินแถว()
    {
        Assert.Equal(0m, WhtCreditableAmount.Of(WhtCreditStatus.Expired, 300m, 0m));
        // GL ลดลงหลังได้ใบ (ยกเลิกการรับชำระบางส่วน) — เครดิตไม่เกินยอดที่ถูกหักจริง
        Assert.Equal(200m, WhtCreditableAmount.Of(WhtCreditStatus.Pending, 200m, 300m));
        Assert.Equal(0m, WhtCreditableAmount.Missing(WhtCreditStatus.Pending, 200m, 300m));
        Assert.Equal(0m, WhtCreditableAmount.Of(WhtCreditStatus.Pending, 200m, -5m));
    }

    [Fact]
    public void M1_ตัดสูญเฉพาะส่วนที่ขาด_ส่วนที่มีใบยังเครดิตได้_ทิศตรงข้ามแถวไม่มีใบตัดเต็ม()
    {
        // ได้ใบ 100 จาก 300 ⇒ ตัดสูญได้ 200 · หลังตัดสูญ (Expired) ยังเครดิต 100
        Assert.Equal(200m, WhtCreditWriteOff.AmountOf(WhtCreditStatus.Pending, 300m, 100m));
        Assert.Equal(100m, WhtCreditableAmount.Of(WhtCreditStatus.Expired, 300m, 100m));
        // ทิศตรงข้าม: ไม่มีใบเลย ⇒ ตัดเต็ม · Received รุ่นเก่า ⇒ เต็มแถว (พฤติกรรมเดิม) · ได้ใบครบผ่านกล่องรับ ⇒ ไม่มีอะไรให้ตัด
        Assert.Equal(300m, WhtCreditWriteOff.AmountOf(WhtCreditStatus.Pending, 300m, 0m));
        Assert.Equal(300m, WhtCreditWriteOff.AmountOf(WhtCreditStatus.Received, 300m, 0m));
        Assert.Equal(0m, WhtCreditWriteOff.AmountOf(WhtCreditStatus.Received, 300m, 300m));
        Assert.False(WhtCreditWriteOff.IsYearEndCandidate(WhtCreditStatus.Pending, 0m, new DateTime(2025, 12, 31), new DateTime(2026, 11, 6)));
        Assert.True(WhtCreditWriteOff.IsYearEndCandidate(WhtCreditStatus.Pending, 200m, new DateTime(2025, 12, 31), new DateTime(2026, 11, 6)));
    }

    [Fact]
    public void L2_ใบต่ำกว่ายอดถูกหักไม่เกิน1บาท_ปิดแถว_เครดิตไม่เกินใบ_ทิศตรงข้ามเกิน1บาทยังรอ()
    {
        Assert.Equal(WhtCreditStatus.Received, WhtCreditableAmount.StatusForReceived(300.50m, 300m));
        Assert.Equal(300m, WhtCreditableAmount.Of(WhtCreditStatus.Received, 300.50m, 300m));   // ไม่ใช่ 300.50
        Assert.Equal(0.50m, WhtCreditWriteOff.AmountOf(WhtCreditStatus.Received, 300.50m, 300m)); // เศษตัดสูญได้
        Assert.Equal(WhtCreditStatus.Pending, WhtCreditableAmount.StatusForReceived(300m, 298m));
        Assert.Equal(WhtCreditStatus.Received, WhtCreditableAmount.StatusForReceived(300m, 300m));
    }

    [Fact]
    public void M3_GLลดแต่ไม่ต่ำกว่ายอดที่ได้ใบ_ลดยอด_ทิศตรงข้ามGLเท่าเดิมไม่แตะ_GLต่ำกว่าใบคงยอด()
    {
        var shrink = WhtCreditableAmount.ResyncWithGl(WhtCreditStatus.Pending, 300m, 100m, 100m);
        Assert.Equal((WhtCreditableAmount.GlResyncKind.Shrink, 100m, WhtCreditStatus.Received), shrink);
        var shrinkPartial = WhtCreditableAmount.ResyncWithGl(WhtCreditStatus.Pending, 300m, 100m, 200m);
        Assert.Equal((WhtCreditableAmount.GlResyncKind.Shrink, 200m, WhtCreditStatus.Pending), shrinkPartial);
        var grow = WhtCreditableAmount.ResyncWithGl(WhtCreditStatus.Received, 100m, 100m, 200m);
        Assert.Equal((WhtCreditableAmount.GlResyncKind.Grow, 200m, WhtCreditStatus.Pending), grow);
        Assert.Equal(WhtCreditableAmount.GlResyncKind.None, WhtCreditableAmount.ResyncWithGl(WhtCreditStatus.Received, 300m, 300m, 300m).Kind);
        var over = WhtCreditableAmount.ResyncWithGl(WhtCreditStatus.Received, 300m, 300m, 100m);
        Assert.Equal((WhtCreditableAmount.GlResyncKind.OverGl, 300m, WhtCreditStatus.Received), over);
    }

    [Fact]
    public void M2_ลบเอกสาร_ไม่ลบแถวที่ได้ใบบางส่วน_ทิศตรงข้ามแถวรอใบล้วนยังลบตามเดิม()
    {
        var step = Assert.Single(DocumentPurgeOrder.DocumentSteps, x => x.Table == "WhtCreditsReceived");
        Assert.Contains("\"Status\" = 0", step.Sql);
        Assert.Contains("\"ReceivedAmount\" = 0", step.Sql);
        Assert.Equal(DocumentPurgeOrder.StepAction.Delete, step.Action);
    }

    [Fact]
    public void ปัดเศษ_AwayFromZero()
        => Assert.Equal(100.01m, WhtCreditableAmount.Of(WhtCreditStatus.Pending, 300m, 100.005m));
}
