using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.DTOs.Portal;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 252 WP-13 J (audit 249 D §10/§9): ป้ายไทยของรอบบิล/สถานะทดลองใช้/ช่องทางชำระมาจากเซิร์ฟเวอร์ตารางเดียว —
/// หน้า subscription.html/portal.html เลิกมีตาราง enum→ไทยเอง · ล็อกทั้ง "ทุกค่าของ enum มีป้ายไทย" และ
/// "ค่าที่ไม่รู้จักคืนค่าเดิม (ไม่แต่งคำ)" + ป้ายถูก serialize ออกไปกับ DTO จริง (หน้าเว็บอ่านชื่อ camelCase)
/// </summary>
public class SubscriptionLabelsRound252Tests
{
    [Fact]
    public void Every_billing_cycle_and_trial_status_has_a_thai_label()
    {
        foreach (var c in Enum.GetValues<BillingCycle>())
            Assert.NotEqual(c.ToString(), SubscriptionLabels.Cycle(c));
        foreach (var t in Enum.GetValues<TrialStatus>())
            Assert.NotEqual(t.ToString(), SubscriptionLabels.Trial(t));
        Assert.Equal("รายปี", SubscriptionLabels.Cycle(BillingCycle.Annual));
        Assert.Equal("กำลังทดลอง", SubscriptionLabels.Trial(TrialStatus.Active));
    }

    [Fact]
    public void Unknown_values_fall_back_to_raw_name_not_invented_text()
    {
        Assert.Equal("99", SubscriptionLabels.Cycle((BillingCycle)99));
        var p = new PortalPaymentResponse(Guid.NewGuid(), DateTime.UtcNow, 100m, "SomethingNew", null, "RV-1");
        Assert.Equal("SomethingNew", p.PaymentMethodLabel);
    }

    [Fact]
    public void Portal_payment_label_is_serialized_with_the_dto()
    {
        var p = new PortalPaymentResponse(Guid.NewGuid(), DateTime.UtcNow, 100m, nameof(PaymentMethod.BankTransfer), null, "RV-1");
        Assert.Equal("โอนเงิน", p.PaymentMethodLabel);
        var json = JsonSerializer.Serialize(p, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"paymentMethodLabel\":\"", json);
        Assert.Contains("\"paymentMethod\":\"BankTransfer\"", json);
    }
}
