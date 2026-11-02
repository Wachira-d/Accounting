using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 223 (erp-review/2026-11-01/BOOKING_PAYMENT_AUDIT.md) — ขั้นการชำระของลูกค้าหลังกดจอง (BookingPaymentStep) ·
/// ที่มาของยอดมัดจำ (AmountSource/AmountBasis) · ช่องยอด 0 = ไม่ได้ตั้ง · คำเตือนช่องทางที่ติ๊กแต่ลูกค้าไม่เห็น</summary>
public class BookingPaymentStepTests
{
    private static BookingPaymentRuleInput Rule(BookingPaymentMode? m = null, decimal? fx = null, decimal? pct = null, decimal? min = null,
        decimal? max = null, int? deadline = null) => new(m, fx, pct, min, max, deadline);

    // ─────────────── ขั้นของลูกค้า: โหมดบังคับต้องไม่ขึ้น "จองสำเร็จ" จนรับเงิน ───────────────

    [Fact]
    public void บังคับจ่าย_รอยืนยัน_มียอดค้าง_ไม่ใช่จองสำเร็จ_ขั้นชำระมาก่อน()
    {
        var hold = new DateTime(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc);
        var r = BookingPaymentStep.Decide(BookingStatus.Pending, BookingPaymentMode.DepositRequired, 17.5m, false, hold);
        Assert.Equal(BookingPaymentStepKind.AwaitingPayment, r.Kind);
        Assert.True(r.PaymentFirst);
        Assert.Equal("รอชำระเงิน", r.CustomerStatusLabel);
        Assert.DoesNotContain("จองสำเร็จ", r.Headline);
        Assert.DoesNotContain("ยืนยันแล้ว", r.Headline);
        Assert.Contains("02/11/2026 10:00", r.Detail);   // UTC+7
        Assert.Contains("฿17.50", r.Detail);
    }

    [Fact]
    public void แนบสลิปแล้ว_รอยืนยัน_ป้ายรอร้านตรวจสลิป()
    {
        var r = BookingPaymentStep.Decide(BookingStatus.Pending, BookingPaymentMode.FullRequired, 35m, true, null);
        Assert.Equal(BookingPaymentStepKind.SlipReview, r.Kind);
        Assert.Equal("รอร้านตรวจสลิป", r.CustomerStatusLabel);
        Assert.Contains("รอร้านตรวจสลิป", r.Headline);
    }

    [Fact]
    public void รับเงินครบแล้ว_ยืนยัน_จองสำเร็จ_ไม่มีขั้นชำระ()
    {
        var r = BookingPaymentStep.Decide(BookingStatus.Confirmed, BookingPaymentMode.DepositRequired, 0m, false, null);
        Assert.Equal(BookingPaymentStepKind.None, r.Kind);
        Assert.Equal("จองสำเร็จ", r.Headline);
        Assert.False(r.PaymentFirst);
        Assert.Null(r.CustomerStatusLabel);   // ป้ายตามสถานะเดิม ("ยืนยันแล้ว")
    }

    [Fact]
    public void มัดจำไม่บังคับ_ลูกค้าเลือกจ่าย_ยืนยันแล้ว_ขั้นชำระขึ้นก่อนแต่บอกว่าข้ามได้()
    {
        var r = BookingPaymentStep.Decide(BookingStatus.Confirmed, BookingPaymentMode.DepositOptional, 10m, false, null);
        Assert.Equal(BookingPaymentStepKind.OptionalPayment, r.Kind);
        Assert.True(r.PaymentFirst);
        Assert.Contains("ไม่บังคับ", r.Headline);
        Assert.Contains("ไม่ถูกยกเลิก", r.Detail);
        Assert.Null(r.CustomerStatusLabel);
    }

    [Fact]
    public void ยืนยันแล้วแต่ยอดค้าง_โหมดบังคับหรือแบบเดิม_บอกยอดค้าง()
    {
        var r = BookingPaymentStep.Decide(BookingStatus.Confirmed, BookingPaymentMode.DepositRequired, 50m, false, null);
        Assert.Equal(BookingPaymentStepKind.OutstandingPayment, r.Kind);
        Assert.Contains("฿50.00", r.Detail);
    }

    [Theory]
    [InlineData(BookingStatus.Waitlisted, "ลงคิวสำรองแล้ว")]
    [InlineData(BookingStatus.Cancelled, "การจองนี้ถูกยกเลิกแล้ว")]
    [InlineData(BookingStatus.Pending, "ได้รับการจองแล้ว — รอร้านยืนยัน")]
    public void ไม่มียอดค้าง_หรือสถานะปิด_ไม่มีขั้นชำระ(BookingStatus status, string headline)
    {
        // ยอดค้างส่งมา > 0 แต่สถานะไม่รับเงิน (คิวสำรอง/ยกเลิก) ⇒ ไม่ชวนจ่าย · Pending + ยอด 0 = รอร้านยืนยันตามปกติ
        var due = status == BookingStatus.Pending ? 0m : 100m;
        var r = BookingPaymentStep.Decide(status, BookingPaymentMode.DepositRequired, due, false, null);
        Assert.Equal(BookingPaymentStepKind.None, r.Kind);
        Assert.Equal(headline, r.Headline);
        Assert.False(r.PaymentFirst);
    }

    [Fact]
    public void ข้อความก่อนกดจอง_และป้ายปุ่ม()
    {
        Assert.Null(BookingPaymentStep.PreviewNextStep(false, false, true, true));
        Assert.Contains("ขั้นชำระเงิน", BookingPaymentStep.PreviewNextStep(true, false, false, true));
        Assert.Contains("ไม่ติ๊กก็จองได้", BookingPaymentStep.PreviewNextStep(false, true, true, false));
        Assert.Contains("ติดต่อร้าน", BookingPaymentStep.PreviewNextStep(true, false, false, false));
        Assert.Equal("จอง แล้วไปขั้นชำระเงิน ฿35.00", BookingPaymentStep.SubmitLabel(true, 35m));
        Assert.Null(BookingPaymentStep.SubmitLabel(false, 35m));
        Assert.Null(BookingPaymentStep.SubmitLabel(true, 0m));
    }

    // ─────────────── ค่าไหนชนะ: ฿10 ของบริการ vs 50% ของเว็บ (ข้อร้องเรียนจริง) ───────────────

    [Fact]
    public void โหมดตามเว็บ_ยอดของบริการชนะ_และบอกที่มา()
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositOptional, pct: 50), Rule(fx: 10));
        Assert.Equal(BookingPaymentModeSource.Site, e.Source);
        Assert.Equal(BookingPaymentModeSource.Service, e.AmountSource);
        Assert.Equal(10m, BookingPaymentPolicy.AmountDue(e, 35m, true));
        Assert.Contains("ช่อง “มัดจำ (บาท/%)” ของบริการนี้", BookingPaymentPolicy.Describe(e));
        Assert.Equal("มัดจำคงที่ ฿10.00", BookingPaymentPolicy.AmountBasis(e, 35m, 10m));
    }

    [Fact]
    public void ทิศตรงข้าม_บริการไม่กรอกยอด_ใช้ยอดเว็บ_และไม่มีหมายเหตุที่มา()
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositOptional, pct: 50), Rule());
        Assert.Equal(BookingPaymentModeSource.Site, e.AmountSource);
        Assert.Equal(17.5m, BookingPaymentPolicy.AmountDue(e, 35m, true));
        Assert.DoesNotContain("ของบริการนี้", BookingPaymentPolicy.Describe(e));
        Assert.Equal("มัดจำ 50% ของค่าบริการ ฿35.00 = ฿17.50", BookingPaymentPolicy.AmountBasis(e, 35m, 17.5m));
    }

    [Fact]
    public void ช่องยอดของบริการกรอก0_ถือว่าไม่ได้ตั้ง_ไม่ทับยอดเว็บ()
    {
        // เดิม: บาท 0 ของบริการทับ 50% ของเว็บ ⇒ ลูกค้าไม่ถูกเก็บเงินเงียบ ๆ
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 50), Rule(fx: 0));
        Assert.Equal(17.5m, BookingPaymentPolicy.AmountDue(e, 35m, false));
        // บาท 0 + % 20 ของบริการ ⇒ ใช้ 20% (เดิมได้ 0 เพราะบาทมาก่อน)
        var e2 = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 50), Rule(fx: 0, pct: 20));
        Assert.Equal(200m, BookingPaymentPolicy.AmountDue(e2, 1000m, false));
        // เว็บตั้งยอด 0 ⇒ ด่านบันทึกเว็บฟ้อง (ไม่ใช่ผ่านแล้วไม่เก็บ)
        Assert.NotNull(BookingPaymentPolicy.SiteSaveProblem(Rule(BookingPaymentMode.DepositRequired, fx: 0), true, true));
        Assert.Null(BookingPaymentPolicy.SiteSaveProblem(Rule(BookingPaymentMode.DepositRequired, fx: 100), true, true));
    }

    [Fact]
    public void ที่มาของยอด_ปรับขั้นต่ำสูงสุด_และเต็มจำนวน()
    {
        var min = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 10, min: 100), Rule());
        Assert.Equal("มัดจำ 10% ของค่าบริการ ฿300.00 → ปรับตามขั้นต่ำ = ฿100.00", BookingPaymentPolicy.AmountBasis(min, 300m, 100m));
        var max = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 50, max: 200), Rule());
        Assert.Contains("ปรับตามสูงสุด", BookingPaymentPolicy.AmountBasis(max, 1000m, 200m));
        var full = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.FullRequired), Rule());
        Assert.Equal("ค่าบริการเต็มจำนวน ฿35.00", BookingPaymentPolicy.AmountBasis(full, 35m, 35m));
        var off = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.Off), Rule());
        Assert.Null(BookingPaymentPolicy.AmountBasis(off, 35m, 0m));
    }

    // ─────────────── ช่องทางที่ติ๊กแต่ลูกค้าไม่เห็น ───────────────

    [Fact]
    public void ติ๊กโอนแต่ไม่มีบัญชี_มีคำเตือน_ทิศตรงข้ามครบไม่มีคำเตือน()
    {
        var empty = new BookingTransferInfo(null, null, null, null, null, null);
        Assert.Contains(BookingPaymentChannels.Warnings(true, empty, false, false, false), w => w.Contains("แนบสลิปไม่ได้"));
        Assert.Contains(BookingPaymentChannels.Warnings(true, null, false, false, false), w => w.Contains("แนบสลิปไม่ได้"));
        var ok = new BookingTransferInfo("กสิกรไทย", "1234567890", "ร้าน", null, null, null);
        Assert.Empty(BookingPaymentChannels.Warnings(true, ok, false, false, false));
        Assert.Empty(BookingPaymentChannels.Warnings(false, null, false, false, false));   // ไม่ติ๊ก = ไม่เตือน
    }

    [Fact]
    public void พร้อมเพย์ผิดรูปแบบ_รูปQRไม่ปลอดภัย_ออนไลน์ไม่มีระบบบริษัท_มีคำเตือน()
    {
        var bad = new BookingTransferInfo(null, null, null, null, "123", null);
        Assert.Contains(BookingPaymentChannels.Warnings(true, bad, false, false, false), w => w.Contains("พร้อมเพย์"));
        var acct = new BookingTransferInfo("กสิกรไทย", "1234567890", "ร้าน", null, null, null);
        Assert.Contains(BookingPaymentChannels.Warnings(true, acct, false, false, false, "http://evil.example/qr.png"), w => w.Contains("รูป QR"));
        Assert.Contains(BookingPaymentChannels.Warnings(false, null, false, true, false), w => w.Contains("ชำระออนไลน์"));
        Assert.Empty(BookingPaymentChannels.Warnings(false, null, false, true, true));
    }
}
