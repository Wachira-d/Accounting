using System.Text;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 218 (คำตัดสินข้อ 177) — นโยบายชำระล่วงหน้าของการจอง: ลำดับ บริการ ?? เว็บ ?? แบบเดิม · ยอด (AwayFromZero · min/max · clamp) ·
/// ชนิดบริการที่เก็บเงินไม่ได้ ⇒ ปิดพร้อมเหตุผล · กำหนดเวลา · ด่านบันทึก · ช่องทาง · QR พร้อมเพย์ CRC16</summary>
public class BookingPaymentPolicyTests
{
    private static BookingPaymentRuleInput Rule(BookingPaymentMode? m = null, decimal? fx = null, decimal? pct = null, decimal? min = null,
        decimal? max = null, int? deadline = null) => new(m, fx, pct, min, max, deadline);

    // ─────────────── ลำดับ (ทั้งสองทิศ) ───────────────

    [Fact]
    public void บริการตั้งเอง_ชนะค่าเว็บ()
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment,
            Rule(BookingPaymentMode.DepositRequired, pct: 30, deadline: 30), Rule(BookingPaymentMode.FullRequired));
        Assert.Equal(BookingPaymentMode.FullRequired, e.Mode);
        Assert.Equal(BookingPaymentModeSource.Service, e.Source);
        Assert.Equal(30, e.DeadlineMinutes);   // ช่องที่บริการไม่ตั้ง ถอยไปค่าเว็บทีละช่อง
    }

    [Fact]
    public void บริการไม่ตั้ง_ใช้ค่าเว็บทั้งโหมดและสูตรยอด()
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment,
            Rule(BookingPaymentMode.DepositRequired, pct: 30, min: 100, max: 1000, deadline: 60), Rule());
        Assert.Equal(BookingPaymentMode.DepositRequired, e.Mode);
        Assert.Equal(BookingPaymentModeSource.Site, e.Source);
        Assert.Equal(30m, e.Percent);
        Assert.Equal(100m, e.MinAmount);
        Assert.Equal(1000m, e.MaxAmount);
    }

    [Fact]
    public void บริการตั้งยอดเอง_ชนะสูตรยอดของเว็บทั้งคู่_แม้โหมดมาจากเว็บ()
    {
        // บริการกรอกมัดจำบาท ⇒ ไม่เอา % ของเว็บมาปน (สูตรยอดเป็นหน่วยเดียว)
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, pct: 30), Rule(fx: 200));
        Assert.Equal(200m, e.FixedAmount);
        Assert.Null(e.Percent);
        Assert.Equal(200m, BookingPaymentPolicy.AmountDue(e, 1000m, false));
    }

    [Fact]
    public void บริการปิดเอง_ชนะเว็บที่บังคับ()
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.FullRequired), Rule(BookingPaymentMode.Off));
        Assert.False(e.Collects);
        Assert.Equal(0m, BookingPaymentPolicy.AmountDue(e, 1000m, true));
    }

    [Fact]
    public void ไม่มีชั้นใดตั้ง_แบบเดิม_PrePaymentเก็บตามช่องบริการ_ไม่มีกำหนดเวลา()
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(), Rule(pct: 50));
        Assert.True(e.IsLegacy);
        Assert.Equal(BookingPaymentMode.DepositRequired, e.Mode);
        Assert.Equal(0, e.DeadlineMinutes);
        Assert.False(BookingPaymentPolicy.OnSeat(e, 500m, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), false).ForcePending);   // แบบเดิม: AutoConfirm ตามเดิม
        Assert.Null(BookingPaymentPolicy.HoldUntil(e, 500m, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), false));
    }

    [Theory]
    [InlineData(1000, null, 25, 250)]
    [InlineData(999.99, null, 33.33, 333.30)]   // 333.296667 ⇒ 333.30
    [InlineData(1000, 300, 10, 300)]            // บาทชนะ % (สูตรเดิม)
    [InlineData(100, 500, null, 100)]           // ไม่เกินยอดรวม
    [InlineData(1000, null, null, 0)]
    public void แบบเดิม_ยอดเท่าสูตรเดิมทุกตัวอักษร(decimal total, double? fx, double? pct, decimal expected)
    {
        decimal? f = fx is double a ? (decimal)a : null, p = pct is double b ? (decimal)b : null;
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(), Rule(fx: f, pct: p));
        Assert.Equal(expected, BookingPaymentPolicy.AmountDue(e, total, false));
        Assert.Equal(BookingSlotEngine.Deposit(total, f, p), BookingPaymentPolicy.BookingDeposit(BookingType.PrePayment, e, total, f, p, false));
    }

    [Fact]
    public void แบบเดิม_ชนิดอื่น_ยอดมัดจำบนการจองคงสูตรเดิม_แต่ไม่เก็บเงิน()
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.Appointment, Rule(), Rule(fx: 200));
        Assert.False(e.Collects);
        Assert.Equal(200m, BookingPaymentPolicy.BookingDeposit(BookingType.Appointment, e, 1000m, 200m, null, false));
    }

    // ─────────────── ชนิดบริการ (D1) ───────────────

    [Theory]
    [InlineData(BookingType.Lead)]
    [InlineData(BookingType.Appointment)]
    [InlineData(BookingType.Guaranteed)]
    public void ชนิดที่ไม่ใช่PrePayment_ตั้งเก็บเงิน_ได้ปิดพร้อมเหตุผล_ไม่เงียบ(BookingType type)
    {
        var e = BookingPaymentPolicy.Resolve(type, Rule(BookingPaymentMode.DepositRequired, pct: 30), Rule());
        Assert.Equal(BookingPaymentMode.Off, e.Mode);
        Assert.Contains("ชำระมัดจำล่วงหน้า", e.Problem);
        Assert.Contains("§78/1", e.Problem);
        Assert.Equal(0m, BookingPaymentPolicy.BookingDeposit(type, e, 1000m, null, null, true));
        Assert.Contains("ไม่เก็บเงินล่วงหน้า", BookingPaymentPolicy.Describe(e));
        Assert.NotNull(BookingPaymentPolicy.ServiceSaveProblem(type, BookingPaymentMode.FullRequired));
    }

    [Fact]
    public void บันทึกบริการ_ทิศตรงข้าม_PrePaymentหรือปิดหรือตามเว็บ_ผ่าน()
    {
        Assert.Null(BookingPaymentPolicy.ServiceSaveProblem(BookingType.PrePayment, BookingPaymentMode.DepositRequired));
        Assert.Null(BookingPaymentPolicy.ServiceSaveProblem(BookingType.Lead, BookingPaymentMode.Off));
        Assert.Null(BookingPaymentPolicy.ServiceSaveProblem(BookingType.Lead, null));
    }

    // ─────────────── ยอด ───────────────

    [Theory]
    [InlineData(1000, 15, 100, 500, 150)]
    [InlineData(300, 15, 100, 500, 100)]     // 45 < ขั้นต่ำ ⇒ 100
    [InlineData(80, 15, 100, 500, 80)]       // ขั้นต่ำเกินยอดรวม ⇒ ไม่เกินยอดรวม
    [InlineData(10000, 15, 100, 500, 500)]   // เกินสูงสุด ⇒ 500
    [InlineData(333.35, 10, null, null, 33.34)] // 33.335 ⇒ 33.34 (AwayFromZero — banker's ได้ 33.34 เหมือนกัน · ดูเคสถัดไป)
    [InlineData(100.25, 10, null, null, 10.03)] // 10.025 ⇒ 10.03 (banker's จะได้ 10.02)
    public void มัดจำเปอร์เซ็นต์_ขั้นต่ำสูงสุด_ปัดAwayFromZero(decimal total, decimal pct, double? min, double? max, decimal expected)
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment,
            Rule(BookingPaymentMode.DepositRequired, pct: pct, min: min is double a ? (decimal)a : null, max: max is double b ? (decimal)b : null), Rule());
        Assert.Equal(expected, BookingPaymentPolicy.AmountDue(e, total, false));
    }

    [Fact]
    public void เต็มยอด_เท่ายอดรวม_และมัดจำไม่บังคับขึ้นกับการเลือกของลูกค้า()
    {
        var full = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.FullRequired), Rule());
        Assert.Equal(1234.57m, BookingPaymentPolicy.AmountDue(full, 1234.567m, false));
        var opt = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositOptional, fx: 200), Rule());
        Assert.Equal(0m, BookingPaymentPolicy.AmountDue(opt, 1000m, false));
        Assert.Equal(200m, BookingPaymentPolicy.AmountDue(opt, 1000m, true));
        Assert.False(BookingPaymentPolicy.OnSeat(opt, 200m, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), false).ForcePending);   // ไม่บังคับ ⇒ ไม่บังคับรอยืนยัน/ไม่ยกเลิกอัตโนมัติ
    }

    [Fact]
    public void มัดจำไม่ได้ตั้งยอด_มีเหตุผล_และขั้นต่ำไม่สร้างยอดจากศูนย์()
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, min: 100), Rule());
        Assert.NotNull(e.Problem);
        Assert.Equal(0m, BookingPaymentPolicy.AmountDue(e, 1000m, true));
    }

    // ─────────────── กำหนดเวลา/ถือรอบ ───────────────

    [Fact]
    public void ถือรอบ_ไม่เกินเวลานัด_และพนักงานจองแทนไม่ถือ()
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, fx: 100, deadline: 120), Rule());
        var now = new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddMinutes(120), BookingPaymentPolicy.HoldUntil(e, 100m, now, now.AddDays(1), false));
        Assert.Equal(now.AddMinutes(30), BookingPaymentPolicy.HoldUntil(e, 100m, now, now.AddMinutes(30), false));
        Assert.Null(BookingPaymentPolicy.HoldUntil(e, 100m, now, now.AddDays(1), createdByStaff: true));
        Assert.Null(BookingPaymentPolicy.HoldUntil(e, 0m, now, now.AddDays(1), false));   // ยอด 0 (จองด้วยบัตรคอร์ส) ไม่ถือ
        var noDeadline = e with { DeadlineMinutes = 0 };
        Assert.Null(BookingPaymentPolicy.HoldUntil(noDeadline, 100m, now, now.AddDays(1), false));
        Assert.True(BookingPaymentPolicy.OnSeat(noDeadline, 100m, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), false).ForcePending);   // ไม่ตั้งเวลา ⇒ ยังรอยืนยันจนจ่าย แต่ไม่ยกเลิกเอง
    }

    [Fact]
    public void พ้นเวลา_ยกเลิกได้เฉพาะรอยืนยัน_ไม่มีเงิน_ไม่มีสลิปรอตรวจ()
    {
        var now = new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
        var past = now.AddMinutes(-1);
        Assert.True(BookingPaymentPolicy.HoldLapsed(BookingStatus.Pending, past, 0m, false, now));
        Assert.False(BookingPaymentPolicy.HoldLapsed(BookingStatus.Pending, now.AddMinutes(1), 0m, false, now));   // ยังไม่ถึงเวลา
        Assert.False(BookingPaymentPolicy.HoldLapsed(BookingStatus.Pending, past, 50m, false, now));              // รับเงินบางส่วนแล้ว ⇒ คนตัดสิน
        Assert.False(BookingPaymentPolicy.HoldLapsed(BookingStatus.Pending, past, 0m, true, now));                // สลิปรอตรวจ
        Assert.False(BookingPaymentPolicy.HoldLapsed(BookingStatus.Confirmed, past, 0m, false, now));             // ร้านยืนยันเองแล้ว
        Assert.False(BookingPaymentPolicy.HoldLapsed(BookingStatus.Pending, null, 0m, false, now));               // ไม่ถือ
    }

    // ─────────────── ด่านบันทึกค่าตั้งเว็บ ───────────────

    [Fact]
    public void โหมดเก็บเงิน_ไม่มีช่องทาง_บันทึกไม่ได้_มีทางไปต่อ()
    {
        var p = BookingPaymentPolicy.SiteSaveProblem(Rule(BookingPaymentMode.FullRequired), gatewayReady: false, transferReady: false);
        Assert.NotNull(p);
        Assert.Contains("ช่องทางรับเงิน", p);
        Assert.Null(BookingPaymentPolicy.SiteSaveProblem(Rule(BookingPaymentMode.FullRequired), false, true));
        Assert.Null(BookingPaymentPolicy.SiteSaveProblem(Rule(BookingPaymentMode.FullRequired), true, false));
        Assert.Null(BookingPaymentPolicy.SiteSaveProblem(Rule(BookingPaymentMode.Off), false, false));   // ปิด = ไม่ต้องมีช่องทาง
        Assert.Null(BookingPaymentPolicy.SiteSaveProblem(Rule(), false, false));                         // ไม่ตั้ง = แบบเดิม
        Assert.NotNull(BookingPaymentPolicy.SiteSaveProblem(Rule(BookingPaymentMode.DepositRequired), false, true));   // มัดจำไม่มียอดตั้งต้น
        Assert.Null(BookingPaymentPolicy.SiteSaveProblem(Rule(BookingPaymentMode.DepositRequired, pct: 30), false, true));
    }

    [Fact]
    public void ตรวจค่าตั้ง_ติดลบ_เกินร้อย_ขั้นต่ำมากกว่าสูงสุด_กำหนดเวลาเกิน()
    {
        Assert.NotEmpty(BookingPaymentPolicy.ValidateRule(Rule(fx: -1)));
        Assert.NotEmpty(BookingPaymentPolicy.ValidateRule(Rule(fx: 10.005m)));
        Assert.NotEmpty(BookingPaymentPolicy.ValidateRule(Rule(pct: 101)));
        Assert.NotEmpty(BookingPaymentPolicy.ValidateRule(Rule(min: 500, max: 100)));
        Assert.NotEmpty(BookingPaymentPolicy.ValidateRule(Rule(deadline: BookingPaymentPolicy.MaxDeadlineMinutes + 1)));
        Assert.Empty(BookingPaymentPolicy.ValidateRule(Rule(BookingPaymentMode.DepositRequired, 100, 30, 50, 500, 60)));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData("Inherit", null, true)]
    [InlineData("DepositRequired", BookingPaymentMode.DepositRequired, true)]
    [InlineData("fullrequired", BookingPaymentMode.FullRequired, true)]
    [InlineData("1", null, false)]       // ตัวเลขไม่รับ (enum ออกเป็นชื่อเสมอ)
    [InlineData("Bogus", null, false)]
    public void ParseMode_รับเฉพาะชื่อ(string? raw, BookingPaymentMode? expected, bool ok)
    {
        var (m, err) = BookingPaymentPolicy.ParseMode(raw);
        Assert.Equal(expected, m);
        Assert.Equal(ok, err == null);
    }

    // ─────────────── ป้าย/ข้อความ ───────────────

    [Fact]
    public void ป้ายสถานะ_รอชำระ_รอตรวจสลิป_ชำระแล้ว()
    {
        var hold = new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal("รอชำระ ฿300.00 ภายใน 30/10/2026 10:00 น.",
            BookingPaymentPolicy.StatusLabel(new BookingPaymentFacts(BookingStatus.Pending, 300m, 0m, false, hold, null)));
        Assert.Equal("รอตรวจสลิป", BookingPaymentPolicy.StatusLabel(new BookingPaymentFacts(BookingStatus.Pending, 300m, 0m, true, hold, null)));
        Assert.Equal("ชำระแล้ว", BookingPaymentPolicy.StatusLabel(new BookingPaymentFacts(BookingStatus.Confirmed, 300m, 300m, false, null, null)));
        Assert.Null(BookingPaymentPolicy.StatusLabel(new BookingPaymentFacts(BookingStatus.Confirmed, 0m, 0m, false, null, null)));
        Assert.Null(BookingPaymentPolicy.StatusLabel(new BookingPaymentFacts(BookingStatus.Cancelled, 300m, 0m, false, null, null)));
        Assert.Contains("สลิปไม่ผ่าน: ยอดไม่ตรง",
            BookingPaymentPolicy.StatusLabel(new BookingPaymentFacts(BookingStatus.Pending, 300m, 0m, false, null, "ยอดไม่ตรง")));
    }

    [Fact]
    public void ข้อความลูกค้า_บอกยอด_บังคับ_กำหนดเวลา()
    {
        var e = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, fx: 300, deadline: 30), Rule());
        var s = BookingPaymentPolicy.CustomerSummary(e, 300m, hasDeadlineHold: true);
        Assert.Contains("฿300.00", s);
        Assert.Contains("30 นาที", s);
        Assert.Contains("ยกเลิกการจองอัตโนมัติ", s);
        Assert.Null(BookingPaymentPolicy.CustomerSummary(e, 0m, true));
        Assert.Contains("ลิงก์จัดการการจอง", BookingPaymentPolicy.NotifyLine(300m, null, true, false));
        Assert.Null(BookingPaymentPolicy.NotifyLine(0m, null, true, true));
    }

    // ─────────────── ช่องทาง ───────────────

    [Theory]
    [InlineData("123-4-56789-0", true)]
    [InlineData("1234567890123", true)]
    [InlineData("12345", false)]          // สั้นเกิน
    [InlineData("12345678901234567", false)]
    [InlineData("12A4567890", false)]
    public void เลขบัญชี_10ถึง15หลัก(string acct, bool ok)
        => Assert.Equal(ok, BookingPaymentChannels.ValidateBankAccount("กสิกรไทย", acct, "ร้านทดสอบ", null).Count == 0);

    [Fact]
    public void เลขบัญชีต้องมีชื่อธนาคารและชื่อบัญชี_ไม่กรอกเลขบัญชีเลยผ่าน()
    {
        Assert.NotEmpty(BookingPaymentChannels.ValidateBankAccount(null, "1234567890", "ร้าน", null));
        Assert.NotEmpty(BookingPaymentChannels.ValidateBankAccount("กสิกรไทย", "1234567890", " ", null));
        Assert.Empty(BookingPaymentChannels.ValidateBankAccount(null, null, null, null));
        Assert.Equal("1234567890", BookingPaymentChannels.NormalizeAccountNumber("123-456 7890"));
    }

    [Fact]
    public void ช่องทางโอนพร้อม_ต้องเปิดและมีอย่างน้อยหนึ่งอย่าง()
    {
        var t = new BookingTransferInfo("กสิกรไทย", "1234567890", "ร้าน", null, null, null);
        Assert.True(BookingPaymentChannels.TransferReady(true, t, false));
        Assert.False(BookingPaymentChannels.TransferReady(false, t, false));   // ร้านปิดช่องโอน
        Assert.False(BookingPaymentChannels.TransferReady(true, new BookingTransferInfo(null, null, null, null, "0812345678", null), false));
        Assert.True(BookingPaymentChannels.TransferReady(true, new BookingTransferInfo(null, null, null, null, "0812345678", null), true));
        Assert.True(BookingPaymentChannels.TransferReady(true, new BookingTransferInfo(null, null, null, null, null, "/uploads/cms/a/b/qr.png"), false));
        Assert.False(BookingPaymentChannels.TransferReady(true, null, true));
        Assert.False(BookingPaymentChannels.GatewayReady(true, false));
    }

    [Theory]
    [InlineData("https://cdn.example.com/qr.png", true)]
    [InlineData("/uploads/cms/c/s/qr.png", true)]
    [InlineData("/api/companies/x/cms/sites/y/storefront/media/z", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    [InlineData("http://insecure/qr.png", false)]
    [InlineData("/uploads/cms/../../etc/passwd", false)]
    public void รูปQR_รับเฉพาะURLภายในหรือhttps(string url, bool ok)
        => Assert.Equal(ok, BookingPaymentChannels.QrImageUrlAcceptable(url));

    // ─────────────── QR พร้อมเพย์ (EMVCo · ตัวเดียวของระบบ — PromptPayService) ───────────────

    private static ushort Crc16Ccitt(string s)
    {
        ushort crc = 0xFFFF;
        foreach (var b in Encoding.ASCII.GetBytes(s))
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    [Fact]
    public void ตัวคำนวณCRCของเทสต์ตรงค่าอ้างอิงมาตรฐาน()
        => Assert.Equal(0x29B1, Crc16Ccitt("123456789"));   // CRC-16/CCITT-FALSE check value

    [Theory]
    [InlineData("0812345678", 300, "01130066812345678", "5406300.00")]
    [InlineData("0812345678", 1234.5, "01130066812345678", "54071234.50")]
    public void QRพร้อมเพย์_มียอด_CRCถูก_และเบอร์แปลงเป็น0066(string id, decimal amount, string idTag, string amountTag)
    {
        var payload = new PromptPayService().GenerateQrPayload(id, amount);
        Assert.StartsWith("000201010212", payload);   // dynamic (มียอด)
        Assert.Contains(idTag, payload);
        Assert.Contains(amountTag, payload);
        Assert.Contains("5303764", payload);           // THB
        Assert.Equal("6304", payload[^8..^4]);
        Assert.Equal(Crc16Ccitt(payload[..^4]).ToString("X4"), payload[^4..]);
    }

    [Fact]
    public void QRพร้อมเพย์_ไม่มียอด_เป็นแบบคงที่_ไม่มีแท็กยอด()
    {
        var payload = new PromptPayService().GenerateQrPayload("0812345678", 0m);
        Assert.StartsWith("000201010211", payload);
        Assert.DoesNotContain("5406", payload);
        Assert.Equal(Crc16Ccitt(payload[..^4]).ToString("X4"), payload[^4..]);
    }

    // ─────────────── สลิป (BookingSlipRule) ───────────────

    [Fact]
    public void แนบสลิป_ด่านครบ_และทิศตรงข้ามผ่าน()
    {
        Assert.Null(BookingSlipRule.UploadProblem(BookingStatus.Pending, 150m, true, false, 1000));
        Assert.NotNull(BookingSlipRule.UploadProblem(BookingStatus.Pending, 150m, true, false, 0));                         // ไม่มีไฟล์
        Assert.NotNull(BookingSlipRule.UploadProblem(BookingStatus.Pending, 150m, true, false, BookingSlipRule.MaxBytes + 1)); // ใหญ่เกิน
        Assert.Contains("ยกเลิก", BookingSlipRule.UploadProblem(BookingStatus.Cancelled, 150m, true, false, 1000));
        Assert.Contains("ชำระครบ", BookingSlipRule.UploadProblem(BookingStatus.Confirmed, 0m, true, false, 1000));
        Assert.Contains("ไม่รับชำระด้วยการโอน", BookingSlipRule.UploadProblem(BookingStatus.Pending, 150m, false, false, 1000));
        Assert.Contains("รอร้านตรวจ", BookingSlipRule.UploadProblem(BookingStatus.Pending, 150m, true, true, 1000));
    }

    [Fact]
    public void ปฏิเสธสลิป_ต้องมีเหตุผล_ตัดความยาว()
    {
        Assert.NotNull(BookingSlipRule.NormalizeRejectReason("  ").Error);
        Assert.NotNull(BookingSlipRule.NormalizeRejectReason(null).Error);
        Assert.Equal("ยอดไม่ตรง", BookingSlipRule.NormalizeRejectReason(" ยอดไม่ตรง ").Reason);
        Assert.Equal(BookingSlipRule.MaxReasonLength, BookingSlipRule.NormalizeRejectReason(new string('ก', 900)).Reason!.Length);
    }

    [Fact]
    public void เงินที่บันทึกปิดสถานะรอตรวจ_เฉพาะเงินของสลิปนี้หรือรับครบ()
    {
        var intent = Guid.NewGuid();
        Assert.True(BookingSlipRule.SettlesPendingSlip(intent, GatewayPaymentRecord.Reference(intent), 150m, 0m));
        Assert.False(BookingSlipRule.SettlesPendingSlip(intent, GatewayPaymentRecord.Reference(Guid.NewGuid()), 150m, 50m));   // เงินก้อนอื่น บางส่วน
        Assert.True(BookingSlipRule.SettlesPendingSlip(intent, "เงินสด", 150m, 150m));                                       // รับครบแล้ว
        Assert.False(BookingSlipRule.SettlesPendingSlip(null, null, 150m, 0m));
    }

    [Fact]
    public void ตรวจสลิปได้เฉพาะเมื่อมีสลิปรอตรวจ()
    {
        Assert.Null(BookingSlipRule.DecideProblem(Guid.NewGuid(), DateTime.UtcNow));
        Assert.NotNull(BookingSlipRule.DecideProblem(null, DateTime.UtcNow));
        Assert.NotNull(BookingSlipRule.DecideProblem(Guid.NewGuid(), null));
    }

    [Fact]
    public void ปฏิเสธแล้วต่อเวลา24ชม_ไม่เกินเวลานัด_ใบที่ไม่ถือไม่ต่อ()
    {
        var now = new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddHours(24), BookingSlipRule.HoldAfterReject(now.AddMinutes(10), now, now.AddDays(3)));
        Assert.Equal(now.AddHours(5), BookingSlipRule.HoldAfterReject(now.AddMinutes(10), now, now.AddHours(5)));
        Assert.Null(BookingSlipRule.HoldAfterReject(null, now, now.AddDays(3)));
    }

    [Fact]
    public void สลิปรอตรวจ_ปิดช่องจ่ายออนไลน์พร้อมเหตุผล_ไม่มีสลิป_จ่ายได้()
    {
        Assert.Equal(CmsBookingOnlinePayment.SlipPendingNote,
            CmsBookingOnlinePayment.BlockedNote(true, BookingType.PrePayment, BookingStatus.Pending, 150m, slipPending: true));
        Assert.Null(CmsBookingOnlinePayment.BlockedNote(true, BookingType.PrePayment, BookingStatus.Pending, 150m, slipPending: false));
    }

    // ─────────────── ฝ่ายค้านรอบ 218 ───────────────

    [Fact]
    public void S5_ได้ที่_ตัวตัดสินเดียว_บังคับ_ถือรอบ_ทิศตรงข้ามพนักงานและแบบเดิม()
    {
        var now = new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
        var forced = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.DepositRequired, fx: 100, deadline: 60), Rule());
        var d = BookingPaymentPolicy.OnSeat(forced, 100m, now, now.AddDays(1), createdByStaff: false);
        Assert.True(d.ForcePending);
        Assert.Equal(now.AddMinutes(60), d.PaymentHoldUntil);
        Assert.False(BookingPaymentPolicy.OnSeat(forced, 100m, now, now.AddDays(1), createdByStaff: true).ForcePending);
        var legacy = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(), Rule(fx: 100));
        Assert.Equal(new BookingSeatPaymentDecision(false, null), BookingPaymentPolicy.OnSeat(legacy, 100m, now, now.AddDays(1), false));
    }

    [Fact]
    public void S6_รายการชำระออนไลน์ยังไม่หมดอายุ_ไม่ยกเลิก_หมดแล้วพ้นระยะผ่อน_ยกเลิกได้()
    {
        var now = new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
        Assert.True(BookingPaymentPolicy.GatewayPaymentInProgress(true, now.AddMinutes(5), now.AddMinutes(-10), now));
        Assert.True(BookingPaymentPolicy.GatewayPaymentInProgress(true, now.AddMinutes(-5), now.AddMinutes(-20), now));    // ระยะผ่อน 10 นาที
        Assert.False(BookingPaymentPolicy.GatewayPaymentInProgress(true, now.AddMinutes(-15), now.AddMinutes(-30), now));
        Assert.False(BookingPaymentPolicy.GatewayPaymentInProgress(false, now.AddMinutes(30), now, now));                 // ปิดแล้ว
    }

    [Fact]
    public void B2_แนบ31ตุลา_ยืนยัน2พฤศจิกา_วันรับเงินคือ31ตุลา()
    {
        var uploaded = new DateTime(2026, 10, 31, 9, 30, 0, DateTimeKind.Utc);   // 16:30 น. 31 ต.ค. (ไทย)
        var created = new DateTime(2026, 10, 30, 2, 0, 0, DateTimeKind.Utc);
        var verifyNow = new DateTime(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc);
        var (paid, err) = BookingSlipRule.ResolvePaidAt(null, uploaded, created, verifyNow);
        Assert.Null(err);
        Assert.Equal(new DateOnly(2026, 10, 31), DateOnly.FromDateTime(paid.AddHours(7)));   // เดือนภาษี ต.ค. ไม่ใช่ พ.ย.
        var (staffPick, err2) = BookingSlipRule.ResolvePaidAt(new DateOnly(2026, 10, 30), uploaded, created, verifyNow);
        Assert.Null(err2);
        Assert.Equal(new DateOnly(2026, 10, 30), DateOnly.FromDateTime(staffPick.AddHours(7)));
        Assert.NotNull(BookingSlipRule.ResolvePaidAt(new DateOnly(2026, 11, 5), uploaded, created, verifyNow).Error);   // อนาคต
        Assert.NotNull(BookingSlipRule.ResolvePaidAt(new DateOnly(2026, 10, 29), uploaded, created, verifyNow).Error);  // ก่อนวันที่จอง
    }

    [Fact]
    public void S1_ยืนยันด้วยมือ_ได้เฉพาะสลิปที่ยังรอตรวจของรายการนี้()
    {
        var id = Guid.NewGuid();
        Assert.Null(BookingSlipRule.ManualConfirmProblem(id, id, DateTime.UtcNow, BookingStatus.Pending));
        Assert.NotNull(BookingSlipRule.ManualConfirmProblem(id, Guid.NewGuid(), DateTime.UtcNow, BookingStatus.Pending));   // ถูกแทน
        Assert.NotNull(BookingSlipRule.ManualConfirmProblem(id, id, null, BookingStatus.Confirmed));                        // ตรวจแล้ว
        Assert.NotNull(BookingSlipRule.ManualConfirmProblem(id, id, DateTime.UtcNow, BookingStatus.Cancelled));             // ยกเลิก
    }

    [Fact]
    public void S2_ตรวจสลิปของการจองที่ยกเลิกหรือไม่มา_ปฏิเสธพร้อมทางไปต่อ()
    {
        Assert.Contains("คืนเงิน", BookingSlipRule.DecideProblem(Guid.NewGuid(), DateTime.UtcNow, BookingStatus.Cancelled));
        Assert.NotNull(BookingSlipRule.DecideProblem(Guid.NewGuid(), DateTime.UtcNow, BookingStatus.NoShow));
        Assert.Null(BookingSlipRule.DecideProblem(Guid.NewGuid(), DateTime.UtcNow, BookingStatus.Confirmed));
    }

    [Fact]
    public void S3_บันทึกผู้ยืนยัน_ตัดความยาว_ว่างเป็นnull()
    {
        Assert.Null(BookingSlipRule.NormalizeVerifyNote("  "));
        Assert.Equal(BookingSlipRule.MaxVerifyNoteLength, BookingSlipRule.NormalizeVerifyNote(new string('ก', 999))!.Length);
    }

    [Fact]
    public void B1_ช่องที่ถูกล้างต้องยืนยัน()
    {
        var before = new BookingTransferInfo("กสิกรไทย", "1234567890", "ร้าน", null, "0812345678", null);
        Assert.Equal(new[] { "พร้อมเพย์" }, BookingPaymentChannels.ClearedFields(before, "กสิกรไทย", "1234567890", "ร้าน", null, null, null));
        Assert.Empty(BookingPaymentChannels.ClearedFields(before, "กสิกรไทย", "1234567890", "ร้าน", "สยาม", "0812345678", null));   // เพิ่มค่า = ไม่ใช่การล้าง
        Assert.Empty(BookingPaymentChannels.ClearedFields(null, null, null, null, null, null, null));
    }

    [Fact]
    public void บันทึกบริการเก็บเงินเอง_ต้องมีช่องทางและยอด_ทิศตรงข้ามตามเว็บไม่ตรวจ()
    {
        var own = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(), Rule(BookingPaymentMode.DepositRequired));
        Assert.NotNull(BookingPaymentPolicy.ServiceChannelProblem(own, false, false));
        Assert.Contains("ยอดมัดจำ", BookingPaymentPolicy.ServiceChannelProblem(own, false, true));
        var ownWithAmount = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(), Rule(BookingPaymentMode.DepositRequired, fx: 100));
        Assert.Null(BookingPaymentPolicy.ServiceChannelProblem(ownWithAmount, false, true));
        var inherit = BookingPaymentPolicy.Resolve(BookingType.PrePayment, Rule(BookingPaymentMode.FullRequired), Rule());
        Assert.Null(BookingPaymentPolicy.ServiceChannelProblem(inherit, false, false));   // ค่าเว็บถูกตรวจตอนบันทึกเว็บ
    }

    [Fact]
    public void PDPA_สลิปที่ปฏิเสธของการจองไม่มีเงิน_ลบหลัง90วัน_สลิปที่มีเงินหรือรอตรวจไม่ลบ()
    {
        var now = new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
        var cut = BookingSlipRetention.Cutoff(now);
        Assert.True(BookingSlipRetention.ShouldPurge(now.AddDays(-91), isCurrentSlip: false, bookingHasMoney: false, cut));
        Assert.False(BookingSlipRetention.ShouldPurge(now.AddDays(-91), false, bookingHasMoney: true, cut));   // หลักฐานบัญชี
        Assert.False(BookingSlipRetention.ShouldPurge(now.AddDays(-91), isCurrentSlip: true, false, cut));    // รอตรวจ
        Assert.False(BookingSlipRetention.ShouldPurge(now.AddDays(-30), false, false, cut));                   // ยังไม่ครบอายุ
    }

    [Fact]
    public void สลิปค้างเลยเวลานัด_ป้ายตรวจด่วน()
        => Assert.Contains("ตรวจด่วน", BookingPaymentPolicy.StatusLabel(new BookingPaymentFacts(BookingStatus.Pending, 300m, 0m, true, null, null, PastStart: true)));
}
