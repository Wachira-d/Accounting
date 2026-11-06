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
    public void ตัดสูญ_เป็นศูนย์_และยอดได้รับเกินยอดถูกหัก_ไม่เกินแถว()
    {
        Assert.Equal(0m, WhtCreditableAmount.Of(WhtCreditStatus.Expired, 300m, 300m));
        // GL ลดลงหลังได้ใบ (ยกเลิกการรับชำระบางส่วน) — เครดิตไม่เกินยอดที่ถูกหักจริง
        Assert.Equal(200m, WhtCreditableAmount.Of(WhtCreditStatus.Pending, 200m, 300m));
        Assert.Equal(0m, WhtCreditableAmount.Missing(WhtCreditStatus.Pending, 200m, 300m));
        Assert.Equal(0m, WhtCreditableAmount.Of(WhtCreditStatus.Pending, 200m, -5m));
    }

    [Fact]
    public void ปัดเศษ_AwayFromZero()
        => Assert.Equal(100.01m, WhtCreditableAmount.Of(WhtCreditStatus.Pending, 300m, 100.005m));
}
