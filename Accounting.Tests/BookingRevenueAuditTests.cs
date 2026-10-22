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

    // ───────────── ฝ่ายค้านรอบ 205 (คอมมิตตามหลัง) ─────────────

    [Fact]
    public void R1_แพ็กเกจกลุ่ม_หักโบนัสตามยอดรวมกลุ่ม_ไม่ใช่ตัวนับบริษัทเดียว()
    {
        var now = DateTime.UtcNow;
        // กลุ่มเพดาน 100 ใช้ครบ 100 · บริษัทนี้ใช้ 10 (เพดานบริษัท 50) · โบนัส 20 ⇒ ใบที่ 101 ของกลุ่มต้องกินโบนัส
        var oldConsumes = DocumentQuotaPolicy.ConsumesBonus(50, 11, 20, null, now);   // ฐานเดิม: ตัวนับบริษัท
        Assert.False(oldConsumes);
        var (used, plan) = DocumentQuotaPolicy.Basis(pooled: true, companyUsed: 10, companyLimit: 50, poolUsed: 100, poolLimit: 100);
        Assert.Equal((100, 100), (used, plan));
        Assert.True(DocumentQuotaPolicy.ConsumesBonus(plan, used + 1, 20, null, now));
        // บริษัทเดี่ยว: ตัวนับ/เพดานของตัวเอง (พฤติกรรมเดิม)
        Assert.Equal((10, 50), DocumentQuotaPolicy.Basis(false, 10, 50, 100, 100));
    }

    [Fact]
    public void R1_แพ็กเกจกลุ่ม_ตรวจโควตาด้วยยอดรวม_โบนัสไม่หายสองเท่า()
    {
        var now = DateTime.UtcNow;
        // กลุ่มใช้ 130 (เพดาน 100) · โบนัสคงเหลือ 70 (ใช้ไป 30) ⇒ เพดานจริง 200 ยังอยู่ในโควตา
        Assert.Equal(200, DocumentQuotaPolicy.EffectiveLimit(100, 70, null, now, used: 130));
        // สูตรเดิมของเส้นกลุ่ม (ไม่ส่ง used) = 170 ⇒ เหลือใช้ 40 แทน 70
        Assert.Equal(170, DocumentQuotaPolicy.EffectiveLimit(100, 70, null, now));
    }

    [Theory]
    [InlineData(150, 100, 0, 150)]   // โบนัสหมดตอนใบที่ 150 ⇒ เพดานจริง 150 (เดิมแสดง 150/100)
    [InlineData(160, 100, 10, 150)]  // ต่อมาอีก 10 ใบเป็นส่วนเกิน ⇒ ยัง 150
    [InlineData(40, 100, 0, 100)]    // ยังไม่ถึงแพ็กเกจ ⇒ เพดานเดิม
    [InlineData(120, 100, 20, 100)]  // ไม่มีโบนัสเลย ส่วนเกิน 20 ⇒ 100
    [InlineData(500, 0, 0, 0)]       // ไม่จำกัด คงไม่จำกัด
    public void R4_เพดานที่แสดง_คือเพดานที่ใช้จริง(int used, int limit, int overage, int expected)
        => Assert.Equal(expected, DocumentQuotaPolicy.DisplayLimit(used, limit, overage));

    [Fact]
    public void R4_ข้อความเต็มตอนโบนัสหมด_ไม่แสดง150ส่วน100()
    {
        var m = DocumentQuotaPolicy.CrossedMessage(2, 150, 100);
        Assert.Contains("150/150", m);
        Assert.DoesNotContain("150/100", m);
    }

    [Fact]
    public void R2_สลิปรอตรวจ_ถูกปิดเมื่อเงินออนไลน์เข้า_แถวอื่นไม่ถูกแตะ()
    {
        Assert.True(GatewayPaymentRecord.IsPendingSlipRow(SitePaymentStatus.Pending, "slip-a.jpg"));
        Assert.False(GatewayPaymentRecord.IsPendingSlipRow(SitePaymentStatus.Completed, "slip-a.jpg"));
        Assert.False(GatewayPaymentRecord.IsPendingSlipRow(SitePaymentStatus.Pending, GatewayPaymentRecord.Reference(Guid.NewGuid())));
        Assert.False(GatewayPaymentRecord.IsPendingSlipRow(SitePaymentStatus.Pending, null));
    }

    [Fact]
    public void R2_สลิปยืนยันแล้ว_บวกจ่ายออนไลน์ก้อนเดียวกัน_เกินยอด_ถูกปฏิเสธ()
    {
        // เดิม: PaidAmount = Σ แถวที่ยืนยัน = 2,000 ของออเดอร์ 1,000 เงียบ ๆ
        Assert.True(GatewayPaymentRecord.OverpaysOrder(completedOtherRows: 1000m, incoming: 1000m, orderTotal: 1000m));
        Assert.Contains("ชำระซ้ำ", GatewayPaymentRecord.OverpayMessage("WEB-2610-0001", 1000m, 1000m, 1000m));
    }

    [Fact]
    public void R2_ชำระบางส่วนสองก้อนครบพอดี_ไม่ถูกปฏิเสธ()
    {
        Assert.False(GatewayPaymentRecord.OverpaysOrder(500m, 500m, 1000m));
        Assert.False(GatewayPaymentRecord.OverpaysOrder(0m, 1000m, 1000m));
    }

    [Fact]
    public void R5_ล้มหลังสร้างใบ_หมายเหตุบอกเลขใบและห้ามออกซ้ำ()
    {
        var n = CmsBookingErpPolicy.SyncFailedNote("x", "REC-20261022-0001");
        Assert.Contains("REC-20261022-0001", n);
        Assert.Contains("ห้ามออกใบใหม่ซ้ำ", n);
        Assert.DoesNotContain("ห้ามออกใบใหม่ซ้ำ", CmsBookingErpPolicy.SyncFailedNote("x"));
        Assert.Equal(ErpBookingDocumentType.AdvanceReceipt, CmsBookingErpPolicy.ErpTypeOf(DocumentType.Receipt));
        Assert.Equal(ErpBookingDocumentType.TaxInvoice, CmsBookingErpPolicy.ErpTypeOf(DocumentType.TaxInvoice));
        Assert.Equal(ErpBookingDocumentType.DraftServiceOrder, CmsBookingErpPolicy.ErpTypeOf(DocumentType.Quotation));
    }

    [Fact]
    public void R6_PrePaymentมัดจำศูนย์_ไม่สร้างใบร่าง_ชนิดอื่นสร้างตามเดิม()
    {
        Assert.False(CmsBookingErpPolicy.NeedsErpDocument(BookingType.PrePayment, 0m));
        Assert.True(CmsBookingErpPolicy.NeedsErpDocument(BookingType.PrePayment, 1m));
        Assert.True(CmsBookingErpPolicy.NeedsErpDocument(BookingType.Appointment, 0m));
        Assert.True(CmsBookingErpPolicy.NeedsErpDocument(BookingType.Guaranteed, 0m));
    }
}
