using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 232 — หน้าทดลองจ่ายเดิมล็อกพร้อมเพย์ ไม่มีให้เลือกวิธี · ตอนนี้ผู้ทดลองเลือกวิธีจากชุดที่เปิดจริง (เซิร์ฟเวอร์ตัดสิน)</summary>
public class PlatformPayMethodsPickRound232Tests
{
    private static readonly string[] Enabled = { "PromptPay", "Card" };

    [Fact]
    public void เลือกวิธีที่เปิด_ได้วิธีนั้น_ไม่สนตัวพิมพ์()
    {
        Assert.Equal(PaymentMethodKind.Card, PlatformPayMethods.Pick("card", Enabled));
        Assert.Equal(PaymentMethodKind.PromptPay, PlatformPayMethods.Pick("PromptPay", Enabled));
    }

    [Fact]
    public void ไม่ส่งวิธี_ได้พร้อมเพย์ถ้าเปิด_ไม่งั้นวิธีแรก()
    {
        Assert.Equal(PaymentMethodKind.PromptPay, PlatformPayMethods.Pick(null, Enabled));
        Assert.Equal(PaymentMethodKind.Card, PlatformPayMethods.Pick(null, new[] { "Card" }));
    }

    [Fact]
    public void ทิศตรงข้าม_วิธีที่ไม่เปิด_สลิป_ชื่อมั่ว_ชุดว่าง_ปฏิเสธ()
    {
        Assert.Null(PlatformPayMethods.Pick("TrueMoney", Enabled));
        Assert.Null(PlatformPayMethods.Pick("ManualSlip", new[] { "ManualSlip", "PromptPay" }));
        Assert.Null(PlatformPayMethods.Pick("Bitcoin", Enabled));
        Assert.Null(PlatformPayMethods.Pick(null, System.Array.Empty<string>()));
    }

    [Fact]
    public void ตัวเลือกบนหน้าเลือกวิธี_ป้ายไทยจากตัวเดียว()
    {
        var opts = PlatformPayMethods.Options(Enabled);
        Assert.Equal(2, opts.Count);
        Assert.Contains(PlatformPayMethods.Label(PaymentMethodKind.Card), System.Text.Json.JsonSerializer.Serialize(opts));
    }
}
