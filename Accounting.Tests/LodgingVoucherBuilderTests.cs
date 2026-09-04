using Accounting.Helpers;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// หลักฐานการจองที่แขกโหลดเก็บ — <b>ไม่ใช่เอกสารภาษี</b>
///
/// <para>เทสต์ชุดนี้เป็น <b>ด่านถาวร</b> ไม่ใช่แค่ยืนยันว่าเรนเดอร์ได้: ถ้าวันหนึ่ง
/// มีคนใส่คำว่า "ใบกำกับภาษี" ลงหัวเอกสารนี้ ลูกค้าจะเอาไปเคลมภาษีซื้อแล้วผิด
/// §82/5(1) — เทสต์ต้องพังก่อนถึงมือผู้ใช้</para>
/// </summary>
public class LodgingVoucherBuilderTests
{
    private static LodgingReservationResponse Sample() => new()
    {
        Id = Guid.NewGuid(),
        PropertyName = "บ้านสวนริมเล",
        PropertyAddress = "99/1 ต.บางเทา อ.ถลาง จ.ภูเก็ต 83110",
        PropertyPhone = "076-123456",
        ReservationNumber = "RSV-20260904-0007",
        Status = LodgingReservationStatus.Confirmed,
        CheckInDate = new DateTime(2026, 12, 30, 0, 0, 0, DateTimeKind.Utc),
        CheckOutDate = new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        CheckInTime = "14:00",
        CheckOutTime = "12:00",
        Nights = 3,
        Adults = 2,
        Children = 1,
        GuestName = "สมชาย ใจดี",
        GuestPhone = "081-2345678",
        Currency = "THB",
        RoomSubtotal = 12000m,
        ServiceChargeAmount = 1200m,
        VatAmount = 864.49m,
        TotalAmount = 13200m,
        GrandTotal = 13550m,
        PaidAmount = 6600m,
        BalanceDue = 6950m,
        DepositRequired = 6600m,
        Rooms = new()
        {
            new LodgingReservationRoomDto
            {
                RoomTypeName = "Pool Villa", UnitNumber = "A2",
                Adults = 2, Children = 1, Subtotal = 12000m,
            },
        },
        Extras = new()
        {
            new LodgingReservationExtraDto { Name = "เตียงเสริม", Quantity = 1, Total = 350m },
        },
        CancellationPolicyName = "ยืดหยุ่น",
        CancellationRules = new()
        {
            new LodgingCancellationRuleDto(7, 0m),
            new LodgingCancellationRuleDto(3, 50m),
        },
    };

    [Fact]
    public void ต้องไม่มีคำที่ทำให้เข้าใจผิดว่าเป็นเอกสารภาษี()
    {
        var html = LodgingVoucherBuilder.BuildHtml(Sample());
        foreach (var word in LodgingVoucherBuilder.ForbiddenTitleWords)
            Assert.DoesNotContain(word, html, StringComparison.Ordinal);
    }

    [Fact]
    public void ต้องบอกชัดว่าใช้เป็นหลักฐานทางภาษีไม่ได้()
    {
        var html = LodgingVoucherBuilder.BuildHtml(Sample());
        Assert.Contains("ไม่ใช่เอกสารทางภาษี", html);
    }

    [Fact]
    public void ยอดบนกระดาษต้องตรงกับยอดในระบบ()
    {
        var r = Sample();
        var html = LodgingVoucherBuilder.BuildHtml(r);
        Assert.Contains("13,550.00", html);   // ยอดรวมทั้งสิ้น
        Assert.Contains("6,600.00", html);    // ชำระแล้ว
        Assert.Contains("6,950.00", html);    // คงเหลือ
        Assert.Contains("12,000.00", html);   // ค่าห้อง
        Assert.Contains("350.00", html);      // เตียงเสริม
    }

    [Fact]
    public void วันที่แสดงเป็น_พศ_และไม่ขึ้นกับ_culture_ของ_process()
    {
        var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            // ตั้ง th-TH ที่ปฏิทินเริ่มต้นเป็นพุทธ — ถ้าโค้ดพึ่ง culture จะบวก 543 ซ้ำ
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            var html = LodgingVoucherBuilder.BuildHtml(Sample());
            Assert.Contains("30/12/2569", html);   // เช็คอิน 30 ธ.ค. 2026
            Assert.Contains("02/01/2570", html);   // เช็คเอาต์ 2 ม.ค. 2027
            // ถ้าโค้ดพึ่ง culture จะได้ปีพุทธซ้อนพุทธ (2569 + 543 = 3112)
            Assert.DoesNotContain("30/12/3112", html);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
    }

    [Fact]
    public void ค่าที่แขกกรอกต้องถูกหนี_HTML()
    {
        var r = Sample();
        r.GuestName = "<script>alert(1)</script>";
        r.SpecialRequests = "ขอห้อง \"ติดสระ\" & เงียบ";
        var html = LodgingVoucherBuilder.BuildHtml(r);
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public void นโยบายยกเลิกที่ตรึงไว้ต้องอยู่บนเอกสาร()
    {
        var html = LodgingVoucherBuilder.BuildHtml(Sample());
        Assert.Contains("นโยบายการยกเลิก", html);
        Assert.Contains("ยืดหยุ่น", html);
        Assert.Contains("ยกเลิกก่อนเช็คอิน 7 วัน", html);
        Assert.Contains("50%", html);
    }

    [Fact]
    public void ชื่อไฟล์ต้องปลอดภัยแม้เลขที่จองมีอักขระแปลก()
    {
        var r = Sample();
        r.ReservationNumber = "RSV/2026\"08\\01";
        var name = LodgingVoucherBuilder.FileName(r);
        Assert.StartsWith("booking-", name);
        Assert.EndsWith(".pdf", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("\"", name);
        Assert.DoesNotContain("\\", name);
    }

    [Fact]
    public void รายการที่ยกเลิกแล้วต้องไม่ขึ้นบนเอกสาร()
    {
        var r = Sample();
        r.Charges = new()
        {
            new LodgingFolioChargeDto
            {
                Description = "มินิบาร์", Quantity = 2, Total = 240m,
                Status = LodgingChargeStatus.Pending,
            },
            new LodgingFolioChargeDto
            {
                Description = "รายการที่ยกเลิกแล้ว", Quantity = 1, Total = 999m,
                Status = LodgingChargeStatus.Cancelled,
            },
        };
        var html = LodgingVoucherBuilder.BuildHtml(r);
        Assert.Contains("มินิบาร์", html);
        Assert.DoesNotContain("รายการที่ยกเลิกแล้ว", html);
        Assert.DoesNotContain("999.00", html);
    }
}
