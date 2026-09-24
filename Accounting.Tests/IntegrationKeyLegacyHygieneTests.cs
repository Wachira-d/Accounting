using System;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบ 193 ข้อ P2/P5 ของนโยบายคีย์รุ่นเก่า — ร่องรอยต้องไม่ถี่จนเป็น log spam แต่ต้องไม่หาย ·
/// วันเลิกใช้ต้องออกเป็น UTC (มี Z) และจำนวนวันที่เหลือต้องคำนวณที่เซิร์ฟเวอร์
/// </summary>
public class IntegrationKeyLegacyHygieneTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 3, 0, 0, DateTimeKind.Utc);

    // ════════ P2: บันทึกครั้งเดียวต่อช่วง ════════

    [Fact]
    public void ยังไม่เคยบันทึก_ต้องบันทึก()
        => Assert.True(IntegrationKeyPolicy.ShouldRecordLegacyEmailMatch(null, Now));

    [Fact]
    public void บันทึกไปเมื่อครู่_ไม่บันทึกซ้ำ_กันlogspamทุกrequest()
        => Assert.False(IntegrationKeyPolicy.ShouldRecordLegacyEmailMatch(Now.AddMinutes(-5), Now));

    [Fact]
    public void ครบช่วงแล้ว_บันทึกอีกครั้ง_ร่องรอยไม่หาย()
        => Assert.True(IntegrationKeyPolicy.ShouldRecordLegacyEmailMatch(
            Now - IntegrationKeyPolicy.LegacyEmailMatchAuditWindow, Now));

    [Fact]
    public void นาฬิกาถอยหลัง_บันทึก_ไม่เงียบ()
        => Assert.True(IntegrationKeyPolicy.ShouldRecordLegacyEmailMatch(Now.AddMinutes(10), Now));

    // ════════ P5: วันเลิกใช้เป็น UTC + วันที่เหลือ ════════

    [Fact]
    public void ค่าจากคอลัมน์timestamp_ถูกประทับเป็นUtc_ไม่เลื่อน7ชม()
    {
        var raw = new DateTime(2026, 12, 23, 3, 0, 0, DateTimeKind.Unspecified);   // แบบที่ Npgsql legacy คืนมา
        var utc = IntegrationKeyPolicy.AsUtc(raw)!.Value;
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(raw.Ticks, utc.Ticks);                                           // ค่าเดิม ไม่แปลงโซนซ้ำ
        Assert.EndsWith("Z", System.Text.Json.JsonSerializer.Serialize(utc).Trim('"'));
        Assert.Null(IntegrationKeyPolicy.AsUtc(null));
    }

    [Fact]
    public void วันที่เหลือ_ปัดขึ้น_และหมดแล้วเป็น0()
    {
        Assert.Equal(90, IntegrationKeyPolicy.LegacyDaysRemaining(true, Now.AddDays(IntegrationKeyPolicy.LegacyGraceDays), Now));
        Assert.Equal(1, IntegrationKeyPolicy.LegacyDaysRemaining(true, Now.AddHours(2), Now));
        Assert.Equal(0, IntegrationKeyPolicy.LegacyDaysRemaining(true, Now, Now));
        Assert.Equal(0, IntegrationKeyPolicy.LegacyDaysRemaining(true, Now.AddDays(-3), Now));
    }

    [Fact]
    public void ไม่ใช่คีย์รุ่นเก่าหรือไม่รู้วัน_เป็นnull_ไม่ใช่0()
    {
        Assert.Null(IntegrationKeyPolicy.LegacyDaysRemaining(false, Now.AddDays(10), Now));
        Assert.Null(IntegrationKeyPolicy.LegacyDaysRemaining(true, null, Now));
    }
}
