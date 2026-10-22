using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 ทีม E — ความถูกต้องเชิงบัญชี + โควตาของธุรกรรมจาก CMS (ที่พัก · จองนัดหมาย · คำสั่งซื้อหน้าเว็บ) · matrix เต็มใน
/// <c>docs/BOOKING_REVENUE_AUDIT.md</c> · ทุกกลุ่ม reproduce ตัวเลขของบั๊กเดิมก่อน แล้วล็อกพฤติกรรมใหม่ + เคสที่ถูกอยู่แล้วไม่ถูกแตะ
/// </summary>
public class BookingRevenueAuditTests
{
    // ───────────── L-1 ที่พัก: ชำระยอดคงเหลือออนไลน์ถูกตีเป็น "ซ้ำ" ─────────────

    [Fact]
    public void L1_ยอดคงเหลือเท่ามัดจำ_กติกาเดิมตีเป็นซ้ำ_กติกาใหม่ไม่ซ้ำ()
    {
        // การจอง 3,000 · มัดจำ 1,500 รับแล้ว (อ้างอิง chrg_A) · แขกจ่ายยอดคงเหลือ 1,500 ออนไลน์ (chrg_B)
        decimal depositPaid = 1500m, secondIntentAmount = 1500m;
        // กติกาเดิมของ LodgingReservationPaymentHandler (สถานะ Confirmed && DepositPaid >= ยอด − 0.005) ⇒ ข้าม = เงินหาย
        var oldGuardSkipped = depositPaid >= secondIntentAmount - 0.005m;
        Assert.True(oldGuardSkipped);
        Assert.False(GatewayPaymentRecord.AlreadyRecordedOnReservation("chrg_A", depositPaid, "chrg_B"));
    }

    [Fact]
    public void L1_ยอดคงเหลือน้อยกว่ามัดจำ_ไม่ซ้ำ()
        => Assert.False(GatewayPaymentRecord.AlreadyRecordedOnReservation("chrg_A", 2000m, "chrg_B"));

    [Fact]
    public void L1_เลขอ้างอิงเดิมที่บันทึกแล้ว_ยังเป็นซ้ำ()
        => Assert.True(GatewayPaymentRecord.AlreadyRecordedOnReservation("chrg_A", 1500m, " chrg_A "));

    [Fact]
    public void L1_ยังไม่เคยรับเงิน_ไม่ซ้ำแม้เลขตรง()
        => Assert.False(GatewayPaymentRecord.AlreadyRecordedOnReservation("chrg_A", 0m, "chrg_A"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void L1_ไม่มีเลขอ้างอิงเข้ามา_ไม่ตีเป็นซ้ำ(string? incoming)
        => Assert.False(GatewayPaymentRecord.AlreadyRecordedOnReservation(null, 1500m, incoming));

    [Fact]
    public void L1_ผู้ให้บริการไม่ส่งเลข_ใช้เลขของรายการชำระแทน_กันซ้ำได้()
    {
        var id = Guid.NewGuid();
        var r = GatewayPaymentRecord.ReservationReference(id, null);
        Assert.Equal(GatewayPaymentRecord.Reference(id), r);
        Assert.True(GatewayPaymentRecord.AlreadyRecordedOnReservation(r, 100m, GatewayPaymentRecord.ReservationReference(id, "  ")));
        Assert.Equal("chrg_x", GatewayPaymentRecord.ReservationReference(id, " chrg_x "));
    }

    [Fact]
    public void Reference_รูปแบบเดิมของเส้นเอกสาร_ไม่เปลี่ยน()
    {
        // แถว Payment เดิมที่ DocumentPaymentHandler บันทึกไว้ใช้รูปนี้ — เปลี่ยน = ด่านกันซ้ำมองไม่เห็นของเก่า
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal("PAY-INTENT-0f8fad5bd9cb469fa16570867728950e", GatewayPaymentRecord.Reference(id));
        Assert.Equal(GatewayPaymentRecord.Reference(id),
            Accounting.Services.Payments.Handlers.DocumentPaymentHandler.PaymentReference(id));
    }

    [Theory]
    [InlineData(PaymentMethodKind.PromptPay, PaymentMethod.PromptPay)]
    [InlineData(PaymentMethodKind.Card, PaymentMethod.CreditCard)]
    [InlineData(PaymentMethodKind.MobileBanking, PaymentMethod.BankTransfer)]
    [InlineData(PaymentMethodKind.InternetBanking, PaymentMethod.BankTransfer)]
    [InlineData(PaymentMethodKind.TrueMoney, PaymentMethod.EWallet)]
    [InlineData(PaymentMethodKind.ManualSlip, PaymentMethod.Other)]
    public void S1_วิธีชำระของแถวรับเงินคำสั่งซื้อ(PaymentMethodKind kind, PaymentMethod expected)
        => Assert.Equal(expected, GatewayPaymentRecord.MethodOf(kind));

    [Fact]
    public void ข้อความหาต้นทางไม่พบ_บอกยอดและทางไปต่อ()
    {
        var msg = GatewayPaymentRecord.SourceMissingMessage("คำสั่งซื้อ", Guid.NewGuid(), 1234.5m);
        Assert.Contains("1,234.50", msg);
        Assert.Contains("คืนเงิน", msg);
    }

    // ───────────── B-1 จองนัดหมาย PrePayment: ใบเสร็จมัดจำของเงินที่ยังไม่ได้รับ ─────────────

    [Fact]
    public void B1_PrePayment_ยังไม่รับเงิน_ไม่อนุมัติใบเสร็จ()
        => Assert.False(CmsBookingErpPolicy.AutoApprove(BookingType.PrePayment, depositAmount: 500m, paidAmount: 0m));

    [Fact]
    public void B1_PrePayment_รับเงินครบแล้ว_อนุมัติได้()
        => Assert.True(CmsBookingErpPolicy.AutoApprove(BookingType.PrePayment, depositAmount: 500m, paidAmount: 500m));

    [Fact]
    public void B1_PrePayment_รับไม่ครบ_ไม่อนุมัติ()
        => Assert.False(CmsBookingErpPolicy.AutoApprove(BookingType.PrePayment, depositAmount: 500m, paidAmount: 499m));

    [Theory]
    [InlineData(BookingType.Appointment, true)]
    [InlineData(BookingType.Guaranteed, true)]
    [InlineData(BookingType.Lead, false)]
    public void B1_ชนิดอื่น_คงพฤติกรรมเดิม(BookingType type, bool expected)
        => Assert.Equal(expected, CmsBookingErpPolicy.AutoApprove(type, 0m, 0m));

    [Fact]
    public void B1_หมายเหตุรอรับเงิน_บอกยอดและวิธีอนุมัติ()
    {
        var n = CmsBookingErpPolicy.AwaitingPaymentNote(500m);
        Assert.Contains("500.00", n);
        Assert.Contains("อนุมัติ", n);
        Assert.Contains("ใบร่าง", n);
    }

    // ───────────── Q-1 โควตาเอกสาร: โบนัสถูกนับสองครั้ง ─────────────

    /// <summary>จำลองเส้น CreateDocumentAsync: ตรวจ "ยังอยู่ในโควตา" ก่อน แล้ว IncrementUsageAsync (นับ + หักโบนัส) — คืนจำนวนใบที่อยู่ในโควตา</summary>
    private static int DocsWithinQuota(int plan, int bonus, Func<int, int, int> limitOf)
    {
        int used = 0, within = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (used < limitOf(used, bonus)) within++; else break;
            used++;
            if (DocumentQuotaPolicy.ConsumesBonus(plan, used, bonus, null, DateTime.UtcNow)) bonus--;
        }
        return within;
    }

    [Fact]
    public void Q1_ซื้อโบนัส100_สูตรเดิมได้ใช้แค่50_สูตรใหม่ได้ครบ100()
    {
        var now = DateTime.UtcNow;
        // สูตรเดิม: plan + โบนัสคงเหลือ (ไม่รู้จักใบที่ใช้ไป)
        var oldWithin = DocsWithinQuota(100, 100, (used, bonus) => bonus > 0 ? 100 + bonus : 100);
        Assert.Equal(150, oldWithin);
        var newWithin = DocsWithinQuota(100, 100, (used, bonus) => DocumentQuotaPolicy.EffectiveLimit(100, bonus, null, now, used));
        Assert.Equal(200, newWithin);
    }

    [Fact]
    public void Q1_ใช้ไม่เกินแพ็กเกจ_สูตรเดิมทุกตัวเลข()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(120, DocumentQuotaPolicy.EffectiveLimit(100, 20, null, now, used: 0));
        Assert.Equal(120, DocumentQuotaPolicy.EffectiveLimit(100, 20, null, now, used: 100));
        Assert.Equal(100, DocumentQuotaPolicy.EffectiveLimit(100, 20, now.AddDays(-1), now, used: 130));
        Assert.Equal(0, DocumentQuotaPolicy.EffectiveLimit(0, 20, null, now, used: 50));   // ไม่จำกัด คงไม่จำกัด
    }

    [Fact]
    public void Q1_ใช้เกินแพ็กเกจ_เพดานรวมโบนัสที่ใช้ไปแล้ว()
        => Assert.Equal(200, DocumentQuotaPolicy.EffectiveLimit(100, 70, null, DateTime.UtcNow, used: 130));

    [Fact]
    public void Q1_หักโบนัสเฉพาะใบที่เกินแพ็กเกจและโบนัสยังไม่หมดอายุ()
    {
        var now = DateTime.UtcNow;
        Assert.False(DocumentQuotaPolicy.ConsumesBonus(100, 100, 10, null, now));
        Assert.True(DocumentQuotaPolicy.ConsumesBonus(100, 101, 10, null, now));
        Assert.False(DocumentQuotaPolicy.ConsumesBonus(100, 101, 10, now.AddMinutes(-1), now));
        Assert.False(DocumentQuotaPolicy.ConsumesBonus(0, 101, 10, null, now));
        Assert.False(DocumentQuotaPolicy.ConsumesBonus(100, 101, 0, null, now));
    }

    // ───────────── Q-3 แจ้งเจ้าของเมื่อข้ามระดับ ─────────────

    [Theory]
    [InlineData(79, 80, 1)]   // ข้าม 80%
    [InlineData(99, 100, 2)]  // เต็ม
    [InlineData(80, 81, 0)]   // อยู่ระดับเดิม — ไม่แจ้งซ้ำ
    [InlineData(100, 101, 0)] // เต็มแล้วออกต่อ (ใบที่กฎหมายบังคับ) — ไม่แจ้งทุกใบ
    [InlineData(10, 11, 0)]
    public void Q3_แจ้งเฉพาะตอนข้ามระดับใหม่(int before, int after, int expected)
        => Assert.Equal(expected, DocumentQuotaPolicy.CrossedWarnLevel(before, 100, after, 100));

    [Fact]
    public void Q3_ไม่จำกัดโควตา_ไม่แจ้ง()
        => Assert.Equal(0, DocumentQuotaPolicy.CrossedWarnLevel(999, 0, 1000, 0));

    [Fact]
    public void Q3_ข้อความเต็ม_บอกว่าใบที่กฎหมายบังคับยังออกได้()
    {
        var m = DocumentQuotaPolicy.CrossedMessage(2, 100, 100);
        Assert.Contains("ยังออกได้", m);
        Assert.Contains("100/100", m);
    }
}
