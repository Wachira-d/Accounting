using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กติกาการเปลี่ยนสถานะการจ่ายเงิน (PAYMENT_GATEWAY_DESIGN.md)
///
/// ═══ ทำไมต้องล็อกด้วยเทสต์ ═══
/// สถานะถูกเปลี่ยนจาก <b>4 ทางที่ไม่เห็นกัน</b> — webhook · job กระทบยอด · คนกดยืนยัน ·
/// หน้าเว็บที่ poll · เคสที่ผิดคือเคสที่ <b>หายากที่สุด</b> (webhook มาช้ากว่า poll ·
/// provider ตัดสิน timeout ก่อนแล้วเงินเข้าทีหลัง · webhook ส่งซ้ำ) และทุกเคสเกี่ยวกับ
/// คำถามว่า "ลูกค้าจ่ายเงินแล้วหรือยัง" ซึ่งผิดไม่ได้
/// </summary>
public class PaymentIntentPolicyTests
{
    private static readonly Guid Src = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    [Fact]
    public void เส้นทางปกติ_รอจ่าย_แล้วสำเร็จ()
    {
        Assert.True(PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.Created, PaymentIntentStatus.Pending).Apply);
        Assert.True(PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.Pending, PaymentIntentStatus.Succeeded).Apply);
    }

    [Fact]
    public void ส่งสถานะเดิมซ้ำ_เป็น_no_op_ไม่ใช่_error()
    {
        // webhook ส่งซ้ำเป็นเรื่องปกติของทุกเจ้า — ถ้าตอบ error provider จะ retry ไม่รู้จบ
        var d = PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.Succeeded, PaymentIntentStatus.Succeeded);
        Assert.True(d.IsDuplicate);
        Assert.False(d.Apply);
        Assert.Null(d.Reason);
    }

    [Fact]
    public void เงินเข้าแล้ว_ถอยกลับไม่ได้()
    {
        foreach (var to in new[] { PaymentIntentStatus.Pending, PaymentIntentStatus.Failed,
                                   PaymentIntentStatus.Expired, PaymentIntentStatus.Created })
        {
            var d = PaymentIntentPolicy.Evaluate(PaymentIntentStatus.Succeeded, to);
            Assert.False(d.Apply);
            Assert.NotNull(d.Reason);
        }
    }

    [Fact]
    public void เงินเข้าแล้ว_ไปทางคืนเงินได้()
    {
        Assert.True(PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.Succeeded, PaymentIntentStatus.Refunded).Apply);
        Assert.True(PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.Succeeded, PaymentIntentStatus.PartiallyRefunded).Apply);
    }

    [Fact]
    public void หมดอายุหรือล้มเหลวแล้ว_แต่เงินเข้าทีหลัง_ต้องรับได้()
    {
        // เคสจริง: provider ตัดสิน timeout ที่ 10 นาที แต่ธนาคารยืนยันการโอนที่นาทีที่ 11
        // ถ้าห้ามไว้ = ลูกค้าจ่ายแล้วระบบไม่รับ ซึ่งเป็นเคสร้องเรียนที่แก้ยากที่สุด
        Assert.True(PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.Expired, PaymentIntentStatus.Succeeded).Apply);
        Assert.True(PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.Failed, PaymentIntentStatus.Succeeded).Apply);
    }

    [Fact]
    public void ล้มเหลวกับหมดอายุ_สลับกันเองไม่ได้()
    {
        // ไม่มีความหมาย และปิดบังสาเหตุจริงที่บันทึกไว้ตอนแรก
        Assert.False(PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.Failed, PaymentIntentStatus.Expired).Apply);
        Assert.False(PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.Expired, PaymentIntentStatus.Failed).Apply);
    }

    [Fact]
    public void คืนเงินครบแล้ว_เปลี่ยนอะไรไม่ได้อีก()
    {
        foreach (var to in Enum.GetValues<PaymentIntentStatus>())
        {
            if (to == PaymentIntentStatus.Refunded) continue;
            Assert.False(PaymentIntentPolicy.Evaluate(PaymentIntentStatus.Refunded, to).Apply);
        }
    }

    [Fact]
    public void คืนบางส่วนแล้ว_ไปได้แค่คืนเต็มจำนวน()
    {
        Assert.True(PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.PartiallyRefunded, PaymentIntentStatus.Refunded).Apply);
        Assert.False(PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.PartiallyRefunded, PaymentIntentStatus.Succeeded).Apply);
    }

    [Fact]
    public void รอจ่ายอยู่_ถอยกลับสถานะเริ่มต้นไม่ได้()
        => Assert.False(PaymentIntentPolicy.Evaluate(
            PaymentIntentStatus.Pending, PaymentIntentStatus.Created).Apply);

    [Theory]
    [InlineData(PaymentIntentStatus.Created, true)]
    [InlineData(PaymentIntentStatus.Pending, true)]
    [InlineData(PaymentIntentStatus.Succeeded, false)]
    [InlineData(PaymentIntentStatus.Failed, false)]
    [InlineData(PaymentIntentStatus.Expired, false)]
    public void IsOpen_บอกว่า_job_กระทบยอดต้องไล่ถามใบไหน(PaymentIntentStatus s, bool expected)
        => Assert.Equal(expected, PaymentIntentPolicy.IsOpen(s));

    [Theory]
    [InlineData(PaymentIntentStatus.Succeeded, true)]
    [InlineData(PaymentIntentStatus.Refunded, true)]
    [InlineData(PaymentIntentStatus.PartiallyRefunded, true)]
    [InlineData(PaymentIntentStatus.Pending, false)]
    [InlineData(PaymentIntentStatus.Failed, false)]
    public void IsSettledPositive_ใช้กันจ่ายซ้ำ(PaymentIntentStatus s, bool expected)
        => Assert.Equal(expected, PaymentIntentPolicy.IsSettledPositive(s));

    [Fact]
    public void คีย์กันซ้ำ_ยอดเดิม_ครั้งเดิม_ได้ค่าเดิม()
        => Assert.Equal(
            PaymentIntentPolicy.IdempotencyKey(PaymentSourceKind.SiteOrder, Src, 1250.5m, 1),
            PaymentIntentPolicy.IdempotencyKey(PaymentSourceKind.SiteOrder, Src, 1250.5m, 1));

    [Fact]
    public void คีย์กันซ้ำ_ลองใหม่หลัง_QR_หมดอายุ_ต้องได้คีย์ใหม่()
    {
        // ถ้าใช้คีย์เดิมจะติด unique index แล้วลูกค้ากดจ่ายซ้ำไม่ได้เลย
        // ซึ่งแย่กว่าปัญหาที่ตั้งใจกัน
        Assert.NotEqual(
            PaymentIntentPolicy.IdempotencyKey(PaymentSourceKind.SiteOrder, Src, 1250.5m, 1),
            PaymentIntentPolicy.IdempotencyKey(PaymentSourceKind.SiteOrder, Src, 1250.5m, 2));
    }

    [Fact]
    public void คีย์กันซ้ำ_ยอดเปลี่ยน_คือการจ่ายคนละครั้ง()
        => Assert.NotEqual(
            PaymentIntentPolicy.IdempotencyKey(PaymentSourceKind.SiteOrder, Src, 1250.5m, 1),
            PaymentIntentPolicy.IdempotencyKey(PaymentSourceKind.SiteOrder, Src, 1300m, 1));

    [Fact]
    public void คีย์กันซ้ำ_คนละชนิดต้นทาง_ไม่ชนกัน()
        => Assert.NotEqual(
            PaymentIntentPolicy.IdempotencyKey(PaymentSourceKind.SiteOrder, Src, 100m, 1),
            PaymentIntentPolicy.IdempotencyKey(PaymentSourceKind.Document, Src, 100m, 1));
}
