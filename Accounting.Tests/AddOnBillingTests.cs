using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ค่าเหมารายเดือนของ add-on + คีย์กันเก็บเงินซ้ำ (LODGING_LICENSING_PLAN §6)
///
/// ที่มา: `PricingMethod.FlatMonthly` เคยคืน 0 เสมอใน `ComputeCharge` ⇒ ตั้งราคา
/// เหมาเดือนละ ฿100 ไว้แต่ไม่เคยเก็บได้สักบาท — เทสต์ชุดนี้ล็อกว่าเงื่อนไข
/// "ฟรี" มีเฉพาะ 3 กรณีที่ตั้งใจ (ทดลองใช้ · ของแถม · ยังไม่ตั้งราคา)
/// </summary>
public class AddOnBillingTests
{
    private static readonly DateTime Now = new(2026, 9, 3, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void งวดคิดตามปฏิทินไทย_ไม่ใช่_UTC()
    {
        // 2026-08-31 18:00Z = 1 ก.ย. 01:00 ตามเวลาไทย ⇒ ต้องเป็นงวดกันยายน
        Assert.Equal("2026-09", AddOnBilling.PeriodOf(new DateTime(2026, 8, 31, 18, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-08", AddOnBilling.PeriodOf(new DateTime(2026, 8, 31, 16, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void คีย์ค่าเหมาต้องคงที่ต่อฟีเจอร์ต่องวด_นี่คือด่านกันเก็บซ้ำ()
    {
        Assert.Equal("flat:lodging.guest-portal:2026-09",
            AddOnBilling.FlatKey("lodging.guest-portal", "2026-09"));
        Assert.NotEqual(AddOnBilling.FlatKey("lodging.guest-portal", "2026-09"),
                        AddOnBilling.FlatKey("lodging.guest-portal", "2026-10"));
    }

    [Fact]
    public void คีย์ทอปอัปต้องต่างกันทุกครั้ง_เพราะซื้อซ้ำในงวดเดียวกันได้()
    {
        Assert.NotEqual(AddOnBilling.TopUpKey("documents.topup", "2026-09", 1),
                        AddOnBilling.TopUpKey("documents.topup", "2026-09", 2));
    }

    [Fact]
    public void ราคาปกติ_คิดเต็มจำนวน()
    {
        var (amount, reason) = AddOnBilling.FlatAmount(
            standardPrice: 100m, snapshotPrice: null,
            trialUntilUtc: null, bundledOrGranted: false, nowUtc: Now);
        Assert.Equal(100m, amount);
        Assert.Equal("charged", reason);
    }

    [Fact]
    public void ราคาดีลเฉพาะราย_ชนะราคามาตรฐาน()
    {
        var (amount, _) = AddOnBilling.FlatAmount(100m, 60m, null, false, Now);
        Assert.Equal(60m, amount);
    }

    /// <summary>ช่วงทดลองใช้ = ฿0 **แต่ยังต้องมีแถว** เพื่อให้ลูกค้าเห็นในบิลว่า
    /// "ทดลองใช้ ฿0" — เงียบไปเลยจะกลายเป็นเซอร์ไพรส์ตอนเดือนถัดไปโดนคิดเงิน</summary>
    [Fact]
    public void ยังอยู่ในช่วงทดลองใช้_ศูนย์บาท_และบอกเหตุผลว่าทดลอง()
    {
        var (amount, reason) = AddOnBilling.FlatAmount(100m, null, Now.AddDays(5), false, Now);
        Assert.Equal(0m, amount);
        Assert.Equal("trial", reason);
    }

    [Fact]
    public void ทดลองใช้หมดอายุแล้ว_กลับมาคิดเงินเต็ม()
    {
        var (amount, reason) = AddOnBilling.FlatAmount(100m, null, Now.AddDays(-1), false, Now);
        Assert.Equal(100m, amount);
        Assert.Equal("charged", reason);
    }

    /// <summary>ของแถม/มากับแพ็กเกจก็ ฿0 เหมือนช่วงทดลอง แต่ **คนละเหตุผล** —
    /// แยกไว้ให้รายงานตอบได้ว่าทำไมยอดเป็นศูนย์</summary>
    [Fact]
    public void ของแถมหรือมากับแพ็กเกจ_ศูนย์บาท_เหตุผลต่างจากทดลองใช้()
    {
        var (amount, reason) = AddOnBilling.FlatAmount(100m, null, null, true, Now);
        Assert.Equal(0m, amount);
        Assert.Equal("granted", reason);
    }

    [Fact]
    public void ยังไม่ตั้งราคา_ไม่คิดเงินและบอกว่ายังไม่มีราคา()
    {
        var (amount, reason) = AddOnBilling.FlatAmount(0m, null, null, false, Now);
        Assert.Equal(0m, amount);
        Assert.Equal("no-price", reason);
    }
}
