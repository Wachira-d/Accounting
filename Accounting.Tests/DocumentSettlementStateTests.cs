using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **สถานะ/ยอดค้าง/จ่ายเกิน — ตัวตัดสินตัวเดียว** (ผลตรวจ D4-3 รอบ 181)
///
/// <para>สองครึ่งตามกฎเหล็ก #4 H: ครึ่งที่พิสูจน์ว่าเคสที่เคยพังกลับมาถูก
/// (จ่ายเกินหายเงียบ · ยอดค้างติดลบ · เศษ 1 สตางค์ถูกประทับว่าชำระครบ)
/// และครึ่งที่พิสูจน์ว่าเส้นทางปกติ (ชำระครบ/ชำระบางส่วน) <b>ไม่ถูกแตะ</b></para>
/// </summary>
public class DocumentSettlementStateTests
{
    // ══════════ ครึ่งแรก: เคสที่เคยพัง ══════════

    [Fact]
    public void จ่ายเกินต้องถูกรายงาน_ไม่ใช่กลืนเงียบ()
    {
        var r = DocumentSettlementState.Apply(1_000m, 1_200m, DocumentStatus.Approved);

        Assert.True(r.Overpaid);
        Assert.Equal(200m, r.OverpaidAmount);
        Assert.Equal(DocumentStatus.Paid, r.Status);
        Assert.Equal(0m, r.BalanceDue);       // ยอดค้างห้ามติดลบ (ลูกหนี้ติดลบในรายงาน)
    }

    [Fact]
    public void ยอดค้างหนึ่งสตางค์ยังไม่ใช่ชำระครบ()
    {
        // เกณฑ์ 0.01 ที่เคยใช้ใน IntegrationService ประทับ "ชำระครบ" ให้ใบนี้
        // ทั้งที่ยอดค้างยังโชว์ 0.01 = สองความจริงบนใบเดียว
        var r = DocumentSettlementState.Apply(1_000m, 999.99m, DocumentStatus.Approved);

        Assert.Equal(DocumentStatus.PartiallyPaid, r.Status);
        Assert.Equal(0.01m, r.BalanceDue);
        Assert.False(r.Overpaid);
    }

    [Fact]
    public void เศษต่ำกว่าครึ่งสตางค์ถือเป็นศูนย์()
    {
        var r = DocumentSettlementState.Apply(1_000m, 999.997m, DocumentStatus.Sent);

        Assert.Equal(DocumentStatus.Paid, r.Status);
        Assert.Equal(0m, r.BalanceDue);
    }

    [Fact]
    public void ด่านจ่ายเกินต้องฟ้องก่อนบันทึก()
    {
        Assert.True(DocumentSettlementState.WouldOverpay(balanceDue: 500m, incomingAmount: 500.02m));
        Assert.False(DocumentSettlementState.WouldOverpay(balanceDue: 500m, incomingAmount: 500m));
        // การปัดเศษฝั่งผู้จ่ายไม่เกิน 1 สตางค์ ยังรับได้ (แล้ว clamp ยอดค้างเป็น 0)
        Assert.False(DocumentSettlementState.WouldOverpay(balanceDue: 500m, incomingAmount: 500.01m));
    }

    [Fact]
    public void เอกสารที่ยกเลิกหรือยังเป็นร่าง_ห้ามถูกปลุกด้วยการรับเงิน()
    {
        foreach (var s in new[]
        {
            DocumentStatus.Draft, DocumentStatus.WaitingApproval,
            DocumentStatus.Voided, DocumentStatus.Rejected,
        })
        {
            var r = DocumentSettlementState.Apply(1_000m, 1_000m, s);
            Assert.Equal(s, r.Status);
        }
    }

    // ══════════ ครึ่งหลัง: เส้นทางปกติห้ามเปลี่ยน ══════════

    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Sent)]
    [InlineData(DocumentStatus.Overdue)]
    [InlineData(DocumentStatus.PartiallyPaid)]
    public void ชำระครบจากทุกสถานะที่ยังค้างเงิน_ต้องกลายเป็นชำระแล้ว(DocumentStatus from)
    {
        var r = DocumentSettlementState.Apply(5_000m, 5_000m, from);

        Assert.Equal(DocumentStatus.Paid, r.Status);
        Assert.Equal(0m, r.BalanceDue);
        Assert.False(r.Overpaid);
    }

    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Sent)]
    [InlineData(DocumentStatus.Overdue)]
    public void ชำระบางส่วน_ยอดค้างตรงและสถานะเป็นชำระบางส่วน(DocumentStatus from)
    {
        var r = DocumentSettlementState.Apply(5_000m, 2_000m, from);

        Assert.Equal(DocumentStatus.PartiallyPaid, r.Status);
        Assert.Equal(3_000m, r.BalanceDue);
    }

    [Fact]
    public void ยกเลิกการชำระจนไม่เหลือเงิน_กลับไปเป็นอนุมัติแล้ว()
    {
        var r = DocumentSettlementState.Apply(5_000m, 0m, DocumentStatus.PartiallyPaid);

        Assert.Equal(DocumentStatus.Approved, r.Status);
        Assert.Equal(5_000m, r.BalanceDue);
    }

    [Fact]
    public void ใบยอดศูนย์ที่ยังไม่มีเงินเข้า_ไม่ถูกประทับว่าชำระแล้ว()
    {
        // ใบยอด 0 (เช่นใบที่ลดหนี้จนหมด) ต้องไม่กลายเป็น "ชำระแล้ว" เพียงเพราะ
        // 0 − 0 = 0 — สถานะปลายทางต้องมาจากเงินที่เข้าจริง
        var r = DocumentSettlementState.Apply(0m, 0m, DocumentStatus.Approved);

        Assert.Equal(DocumentStatus.Approved, r.Status);
        Assert.Equal(0m, r.BalanceDue);
    }
}
