using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **หน้าตั้งค่า/API ตั้งค่าห้ามรับค่าแล้วเงียบ** (กฎเหล็ก #4 A · ผลตรวจ S-13 / S-20 รอบ 193)
///
/// <para>S-13: ช่องข้อความ "" = ล้าง (เดิมหน้าเว็บส่ง <c>value || null</c> และ server อ่าน null = ไม่แก้ ⇒ ลบชื่อผู้ส่งอีเมล/
/// เงื่อนไขชำระเงินแล้วกดบันทึก ค่าเดิมยังอยู่) · S-20: <c>NumberSeries.Suffix/Format/CurrentNumber/ResetPeriod</c> ที่ตัวออกเลข
/// ไม่ใช้ ต้องถูกปฏิเสธด้วยข้อความไทยเมื่อ "เปลี่ยน" แทนการตอบสำเร็จ</para>
///
/// <para>แต่ละชุดมีสองครึ่ง: ครึ่งที่เคยเงียบต้องมีผล/ถูกปฏิเสธ · ครึ่งที่ถูกอยู่แล้ว (ค่าจริง · ส่งค่าเดิมกลับ) ต้องผ่านเหมือนเดิม</para>
/// </summary>
public class SettingsSilentNoOpTests
{
    // ════════ S-13: "" = ล้าง ════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ข้อความว่าง_ล้างค่าได้(string input)
    {
        Assert.Null(SettingsService.TextOrNull(input));
        Assert.Null(SettingsService.MultilineOrNull(input));
    }

    [Fact]
    public void ข้อความจริง_ยังบันทึกตามที่พิมพ์()
    {
        Assert.Equal("บริษัท ตัวอย่าง จำกัด", SettingsService.TextOrNull("  บริษัท ตัวอย่าง จำกัด "));
        // หลายบรรทัด: ไม่ตัดย่อหน้า/ขึ้นบรรทัดที่ผู้ใช้ตั้งใจใส่
        Assert.Equal("  โอนเข้า\nกสิกร 123", SettingsService.MultilineOrNull("  โอนเข้า\nกสิกร 123"));
    }

    // ════════ S-20: ช่องที่ตัวออกเลขไม่ใช้ ════════

    [Fact]
    public void แก้เลขล่าสุดหรือรอบนับใหม่_ถูกปฏิเสธ_ไม่ใช่ตอบสำเร็จเงียบ()
    {
        var changed = NumberSeriesFieldPolicy.ChangedOnUpdate(
            currentSuffix: null, currentFormat: NumberSeriesFieldPolicy.DefaultFormat, currentNumber: 0, currentResetPeriod: 0,
            suffix: null, format: null, newCurrentNumber: 999, resetPeriod: 12);
        Assert.Equal(new[] { NumberSeriesFieldPolicy.CurrentNumber, NumberSeriesFieldPolicy.ResetPeriod }, changed.ToArray());
        var ex = Assert.Throws<BusinessRuleException>(() => NumberSeriesFieldPolicy.ThrowIfAny(changed));
        Assert.Equal(NumberSeriesFieldPolicy.RuleCode, ex.RuleCode);
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("ยังไม่รองรับ", ex.Message);
        Assert.Contains("ตัวย่อ", ex.Message);           // ทางไปต่อ
    }

    [Fact]
    public void สร้างใหม่พร้อมSuffixหรือเลขเริ่มต้น_ถูกปฏิเสธ()
    {
        var changed = NumberSeriesFieldPolicy.ChangedOnCreate(suffix: "-X", format: "{PREFIX}{SEQ:6}", startNumber: 1000, resetPeriod: 1);
        Assert.Equal(4, changed.Count);
    }

    [Fact]
    public void ส่งค่าเดิมกลับมาทั้งก้อน_ผ่าน_GETแล้วPUTไม่พัง()
    {
        var changed = NumberSeriesFieldPolicy.ChangedOnUpdate(
            currentSuffix: null, currentFormat: NumberSeriesFieldPolicy.DefaultFormat, currentNumber: 42, currentResetPeriod: 0,
            suffix: "", format: NumberSeriesFieldPolicy.DefaultFormat, newCurrentNumber: 42, resetPeriod: 0);
        Assert.Empty(changed);
        NumberSeriesFieldPolicy.ThrowIfAny(changed);   // ไม่โยน
    }

    [Fact]
    public void หน้าเว็บส่งแค่ตัวย่อ_ผ่านทั้งสร้างและแก้()
    {
        Assert.Empty(NumberSeriesFieldPolicy.ChangedOnCreate(null, null, NumberSeriesFieldPolicy.DefaultStartNumber,
            NumberSeriesFieldPolicy.DefaultResetPeriod));
        // หน้าเว็บรุ่นก่อนส่ง format ค่าเริ่มต้นมาด้วย — ต้องยังผ่าน
        Assert.Empty(NumberSeriesFieldPolicy.ChangedOnCreate(null, NumberSeriesFieldPolicy.DefaultFormat, 1, 0));
        Assert.Empty(NumberSeriesFieldPolicy.ChangedOnUpdate(null, NumberSeriesFieldPolicy.DefaultFormat, 7, 0,
            null, null, null, null));
    }
}
