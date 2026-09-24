using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่านเล็ก 3 ตัวจากผลฝ่ายค้านรอบ 193 (M2) — ทุกกลุ่มสองครึ่ง: เคสที่พังกลับมาถูก / เคสที่ถูกอยู่แล้วไม่ถูกแตะ
/// <list type="bullet">
/// <item><see cref="ServiceCommissionTypeReview"/> — แถวคอมมิชชันแพ็คเกจ POS ที่ฟอร์มเก่าบันทึกกลับด้าน</item>
/// <item><see cref="CommissionPlanRules.IsDeactivateOnly"/> — ปิดใช้งานแผนเก่าได้โดยไม่ต้องแต่งอัตรา</item>
/// <item><see cref="CurrencyRateSync"/> — sync ธปท. เขียนทับแถวอัตรา 0 ที่ค้าง</item>
/// </list>
/// </summary>
public class Round193M2RulesTests
{
    // ═══════════ ServiceCommissionTypeReview ═══════════

    [Fact]
    public void ค่า0จากฟอร์มเก่า_ไม่อยู่ในenum_ต้องตรวจ_และบอกว่าตอนขายคิดเป็นเปอร์เซ็นต์()
    {
        var c = ServiceCommissionTypeReview.Judge((CommissionType)0, confirmedAt: null);
        Assert.Equal(ServiceCommissionTypeClarity.UndefinedValue, c);
        Assert.True(ServiceCommissionTypeReview.NeedsReview(c));
        Assert.Contains("เปอร์เซ็นต์", ServiceCommissionTypeReview.Note(c));
        Assert.Contains(ServiceCommissionTypeReview.ReviewPrompt, ServiceCommissionTypeReview.Note(c));
    }

    [Fact]
    public void ค่า1ที่บันทึกก่อนแก้ฟอร์ม_กำกวม_ต้องตรวจ()
    {
        var c = ServiceCommissionTypeReview.Judge(CommissionType.Fixed, confirmedAt: null);
        Assert.Equal(ServiceCommissionTypeClarity.AmbiguousLegacy, c);
        Assert.True(ServiceCommissionTypeReview.NeedsReview(c));
    }

    [Fact]
    public void แถวที่ยืนยันแล้ว_หรือเป็นPercentage_ไม่ติดป้าย()
    {
        var at = new DateTime(2026, 9, 24, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(ServiceCommissionTypeClarity.Clear, ServiceCommissionTypeReview.Judge(CommissionType.Fixed, at));
        Assert.Equal(ServiceCommissionTypeClarity.Clear, ServiceCommissionTypeReview.Judge(CommissionType.Percentage, at));
        // ฟอร์มเก่าผลิต 2 ไม่ได้ ⇒ Percentage ที่ไม่มีเวลายืนยันก็มาจาก API ที่ส่งถูก
        Assert.Equal(ServiceCommissionTypeClarity.Clear, ServiceCommissionTypeReview.Judge(CommissionType.Percentage, null));
        Assert.Null(ServiceCommissionTypeReview.Note(ServiceCommissionTypeClarity.Clear));
    }

    [Fact]
    public void ค่านอกenumที่ส่งเข้ามาใหม่_ถูกปฏิเสธเป็นไทย_ค่าถูกผ่าน()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => ServiceCommissionTypeReview.EnsureDefined((CommissionType)0));
        Assert.Contains("คงที่", ex.Message);
        ServiceCommissionTypeReview.EnsureDefined(CommissionType.Fixed);
        ServiceCommissionTypeReview.EnsureDefined(CommissionType.Percentage);
    }

    // ═══════════ CommissionPlanRules.IsDeactivateOnly ═══════════

    private static readonly CommissionTierSpec[] NoTiers = Array.Empty<CommissionTierSpec>();

    [Fact]
    public void ส่งแค่ปิดใช้งาน_แผนเก่าอัตราว่าง_ผ่านโดยไม่ต้องแก้อัตรา()
    {
        // แผนเก่า: Percentage แต่ FlatRate null (ค้างจากฟอร์มก่อน A04) — Validate จะโยน "อัตรา (%) ต้องมากกว่า 0"
        Assert.Throws<BusinessRuleException>(() =>
            CommissionPlanRules.Validate("แผนเก่า", "Revenue", "Percentage", null, null));
        Assert.True(CommissionPlanRules.IsDeactivateOnly(false,
            null, null, null, null, null, null,
            "แผนเก่า", null, "Revenue", "Percentage", null, NoTiers));
    }

    [Fact]
    public void ฟอร์มส่งทุกช่องเท่าเดิม_แล้วติ๊กใช้งานออก_ถือว่าปิดอย่างเดียว()
    {
        Assert.True(CommissionPlanRules.IsDeactivateOnly(false,
            " แผนเก่า ", "", "Quantity", "percentage", null, NoTiers,
            "แผนเก่า", null, "Quantity", "Percentage", null, NoTiers));
    }

    [Fact]
    public void เปิดใช้งาน_หรือแก้ช่องอื่นพร้อมกัน_ต้องผ่านด่านเต็ม()
    {
        // เปิดใช้งาน = แผนจะถูกนำไปคำนวณ ⇒ ต้องผ่าน Validate เสมอ
        Assert.False(CommissionPlanRules.IsDeactivateOnly(true,
            null, null, null, null, null, null, "แผน", null, "Revenue", "Percentage", 5m, NoTiers));
        Assert.False(CommissionPlanRules.IsDeactivateOnly(null,
            null, null, null, null, null, null, "แผน", null, "Revenue", "Percentage", 5m, NoTiers));
        // ปิดพร้อมเปลี่ยนอัตรา / ชื่อ / ขั้น = แก้แผน ไม่ใช่แค่ปิด
        Assert.False(CommissionPlanRules.IsDeactivateOnly(false,
            null, null, null, null, 7m, null, "แผน", null, "Revenue", "Percentage", 5m, NoTiers));
        Assert.False(CommissionPlanRules.IsDeactivateOnly(false,
            "ชื่อใหม่", null, null, null, null, null, "แผน", null, "Revenue", "Percentage", 5m, NoTiers));
        Assert.False(CommissionPlanRules.IsDeactivateOnly(false,
            null, null, null, null, null, new[] { new CommissionTierSpec(0m, null, 3m) },
            "แผน", null, "Revenue", "Tiered", null, NoTiers));
    }

    // ═══════════ CurrencyRateSync ═══════════

    [Fact]
    public void แถวอัตรา0ค้าง_syncธปท_เขียนทับ()
    {
        // เดิม: มีแถวอยู่แล้ว → ข้าม ⇒ ตัวอ่านที่กรอง MidRate > 0 ใช้อัตราวันก่อนเงียบ ๆ
        Assert.Equal(CurrencyRateSyncAction.ReplaceUnusableRow, CurrencyRateSync.Decide(0m, 36.25m));
        Assert.Equal(CurrencyRateSyncAction.ReplaceUnusableRow, CurrencyRateSync.Decide(-1m, 36.25m));
    }

    [Fact]
    public void แถวที่ใช้ได้อยู่แล้ว_ไม่ถูกทับ_และไม่สร้างแถว0ใหม่()
    {
        Assert.Equal(CurrencyRateSyncAction.KeepExisting, CurrencyRateSync.Decide(35.10m, 36.25m));
        Assert.Equal(CurrencyRateSyncAction.Insert, CurrencyRateSync.Decide(null, 36.25m));
        Assert.Equal(CurrencyRateSyncAction.SkipInvalidIncoming, CurrencyRateSync.Decide(null, 0m));
        Assert.Equal(CurrencyRateSyncAction.SkipInvalidIncoming, CurrencyRateSync.Decide(0m, 0m));
    }
}
