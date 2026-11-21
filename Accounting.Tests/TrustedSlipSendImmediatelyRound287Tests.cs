using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 287 (คำสั่งเจ้าของ "ถ้าเป็นผู้จ่ายที่ไว้ใจได้ อนุญาตให้ส่งใบกำกับภาษี หรือเอกสารอื่นๆอัตโนมัติได้เลย" · คำตัดสิน 287-1…) —
/// ตัวตัดสินการกักส่งเอกสาร (<see cref="TrustedSlipPayerRule.HoldDelivery"/>) · ข้อความเมื่อตรวจย้อนล้ม (<see cref="TrustedSlipPayerRule.FailedNextStep"/>) ·
/// ข้อเท็จจริง "ส่งแล้ว" จากขั้นของงานหลังรับชำระ (<see cref="PostPaymentAutomationPolicy.DeliveredToBuyer"/>) · pure · ไม่มี AI
/// </summary>
public class TrustedSlipSendImmediatelyRound287Tests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ค่าตั้งต้นของบริษัท_เปิดส่งทันที_ตามคำสั่งเจ้าของ()
        => Assert.True(new CompanySettings().SlipTrustedSendImmediately);

    [Fact]
    public void รอยืนยัน_ค่าตั้งเปิด_ไม่กัก_ค่าตั้งปิด_กัก()
    {
        Assert.False(TrustedSlipPayerRule.HoldDelivery(T0, null, null, sendImmediately: true));
        Assert.True(TrustedSlipPayerRule.HoldDelivery(T0, null, null, sendImmediately: false));   // ทิศตรงข้าม = พฤติกรรม 286 เดิม
    }

    [Fact]
    public void ตรวจย้อนล้มแล้ว_กักเสมอ_ไม่ว่าค่าตั้ง()
    {
        Assert.True(TrustedSlipPayerRule.HoldDelivery(T0, null, T0.AddDays(3), sendImmediately: true));
        Assert.True(TrustedSlipPayerRule.HoldDelivery(T0, null, T0.AddDays(3), sendImmediately: false));
    }

    [Fact]
    public void ไม่ใช่รายการล่วงหน้า_หรือยืนยันแล้ว_ไม่กักไม่ว่าค่าตั้ง()
    {
        foreach (var on in new[] { true, false })
        {
            Assert.False(TrustedSlipPayerRule.HoldDelivery(null, null, null, on));          // การรับเงินทั่วไป (ไม่แตะ)
            Assert.False(TrustedSlipPayerRule.HoldDelivery(T0, T0.AddDays(1), null, on));   // ธนาคารยืนยันแล้ว
        }
    }

    [Fact]
    public void ข้อความล้ม_ระบุว่าส่งไปแล้ว_และทางไปต่อ_ไม่ทำเอง()
    {
        var sent = TrustedSlipPayerRule.FailedNextStep(sentToBuyer: true, sentToPartner: false);
        Assert.Contains("ส่งใบเสร็จ/ใบกำกับถึงผู้ซื้อไปแล้ว", sent);
        Assert.Contains("ใบลดหนี้", sent);
        Assert.Contains("ยกเลิกการรับเงิน", sent);
        Assert.Contains("ระบบไม่ยกเลิกเอง", sent);
        Assert.DoesNotContain("คู่ค้า", sent);

        var partner = TrustedSlipPayerRule.FailedNextStep(sentToBuyer: false, sentToPartner: true);
        Assert.Contains("ส่งใบเสร็จ/ใบกำกับถึงผู้ซื้อไปแล้ว", partner);
        Assert.Contains("ระบบของผู้ซื้อคู่ค้าได้สถานะยกเลิก", partner);

        // ทิศตรงข้าม: ยังไม่ส่ง ⇒ ห้ามบอกว่าส่งไปแล้ว
        var held = TrustedSlipPayerRule.FailedNextStep(false, false);
        Assert.Contains("ยังไม่ถูกส่งถึงผู้ซื้อ", held);
        Assert.DoesNotContain("ไปแล้ว", held);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("IssueReceipt", false)]
    [InlineData("IssueReceipt,EmailReceipt", true)]
    [InlineData("LineReceipt", false)]   // ฝ่ายค้าน 287 LOW-2: เข้าคิวไม่ใช่ส่ง (LINE ตัดสินจาก LineQueue.SentAt)
    [InlineData("EmailSourceAsReceipt", true)]
    [InlineData("IssueReceipt,EmailReceipt:0f8fad5bd9cb469fa16570867728950e", true)]
    [InlineData("123,IssueReceipt", false)]   // ตัวเลขไม่ใช่ชื่อขั้น (Enum.TryParse จะรับ — ห้ามใช้)
    public void ส่งแล้วไหม_อ่านจากขั้นที่ทำแล้ว(string? steps, bool expected)
        => Assert.Equal(expected, PostPaymentAutomationPolicy.DeliveredToBuyer(steps));

    [Fact]
    public void ระยะตรวจซ้ำของงานที่กัก_รอยืนยันสั้น_ล้มแล้วยาว()
    {
        Assert.Equal(TimeSpan.FromHours(1), TrustedSlipPayerRule.HoldRecheck(failed: false));   // ฝ่ายค้าน 287 MED: เดิม 30 วันเสมอ
        Assert.Equal(TimeSpan.FromDays(30), TrustedSlipPayerRule.HoldRecheck(failed: true));
    }

    [Fact]
    public void migration_ปลุกงานที่กักด้วยป้ายกักตัวเดียว()
        => Assert.Contains(Round287Migration.Statements(), x => x.Contains("PostPaymentAutomationRuns") && x.Contains(TrustedSlipPayerRule.DeliveryHeldNote));

    [Fact]
    public void ประโยคความเสี่ยง_ภาษาคน_ตามคำสั่ง()
    {
        Assert.Contains("ถ้าสลิปปลอม ผู้ซื้อจะได้ใบกำกับไปก่อน", TrustedSlipPayerRule.SendImmediatelyRiskNotice);
        Assert.Contains("ใบลดหนี้/ยกเลิกและแจ้งผู้ซื้อ", TrustedSlipPayerRule.SendImmediatelyRiskNotice);
    }
}
