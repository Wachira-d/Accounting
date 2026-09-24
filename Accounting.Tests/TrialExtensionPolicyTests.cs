using Accounting.Helpers;
using Accounting.Models.Constants;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบ 193 W-C3 — ขยาย trial ได้กี่วัน (server เป็นคนตัดสิน ไม่ใช่ตัวเลขใน body)
///
/// <para>═══ บั๊กจริง ═══ <c>ExtendTrialAsync</c> ใช้ <c>request.AdditionalDays</c> ตรง ๆ ⇒ ลูกค้าส่ง 36500 ครั้งเดียวได้ใช้ฟรีร้อยปี ·
/// ครึ่งแรกล็อกว่าลูกค้าถูกจำกัด · ครึ่งหลัง (ทิศตรงข้าม) ล็อกว่าค่าเริ่มต้นกับแอดมินแพลตฟอร์มยังทำงานเหมือนเดิม</para>
/// </summary>
public class TrialExtensionPolicyTests
{
    // ════════ ครึ่งแรก: ลูกค้าขอเกินค่าที่แพลตฟอร์มตั้ง = ปฏิเสธ (ไม่ตัดเงียบ) ════════

    [Fact]
    public void ลูกค้าขอ_36500_วัน_ถูกปฏิเสธพร้อมบอกเพดาน()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => TrialExtensionPolicy.ResolveDays(36_500, 7, allowCustomDays: false));
        Assert.Equal(TrialExtensionPolicy.RuleCode, ex.RuleCode);
        Assert.Contains("7 วัน", ex.Message);        // บอกว่าได้เท่าไร
        Assert.Contains("ผู้ดูแลระบบ", ex.Message);    // บอกทางไปต่อ
    }

    [Fact]
    public void ลูกค้าขอเกินหนึ่งวัน_ก็ถูกปฏิเสธ()
    {
        Assert.Throws<BusinessRuleException>(() => TrialExtensionPolicy.ResolveDays(8, 7, allowCustomDays: false));
    }

    // ════════ ครึ่งหลัง: ทางที่ถูกต้องยังใช้ได้ ════════

    [Theory]
    [InlineData(0, 7, 7)]    // ไม่ส่ง = ค่าที่แพลตฟอร์มตั้ง (หน้า billing เดิมไม่ส่ง)
    [InlineData(-3, 7, 7)]
    [InlineData(7, 7, 7)]    // ขอเท่าเพดาน = ได้
    [InlineData(3, 7, 3)]    // ขอน้อยกว่า = ได้ตามขอ
    public void ลูกค้าขอไม่เกินเพดาน_ได้ตามกติกา(int requested, int configured, int expected)
    {
        Assert.Equal(expected, TrialExtensionPolicy.ResolveDays(requested, configured, allowCustomDays: false));
    }

    [Theory]
    [InlineData(90, 7, 90)]  // แอดมินแพลตฟอร์มต่อเป็นกรณีพิเศษได้
    [InlineData(0, 7, 7)]
    public void แอดมินแพลตฟอร์ม_กำหนดจำนวนวันเองได้(int requested, int configured, int expected)
    {
        Assert.Equal(expected, TrialExtensionPolicy.ResolveDays(requested, configured, allowCustomDays: true));
    }
}

/// <summary>ฝ่ายค้านรอบ 193 W-C7 — ข้อความ 403 ของด่านสิทธิ์ต้องบอก "ขาดสิทธิ์อะไร · ขอจากใคร · ที่ไหน" (F2 ข้อ 8)</summary>
public class PermissionDeniedMessageTests
{
    [Fact]
    public void ข้อความบอกชื่อสิทธิ์ภาษาไทย_คนที่ขอได้_และหน้าที่ใช้เปิดสิทธิ์()
    {
        var msg = PermissionKeys.DeniedMessage(PermissionKeys.CompanySettingsEdit);
        Assert.Contains("ตั้งค่าบริษัท", msg);          // ชื่อไทยจาก Catalog ไม่ใช่ชื่อคีย์อย่างเดียว
        Assert.Contains("CompanySettings.Edit", msg);   // คีย์ไว้ให้ผู้ดูแลค้น
        Assert.Contains("เจ้าของบริษัท", msg);           // ขอจากใคร
        Assert.Contains("/pages/roles.html", msg);      // ที่ไหน
        Assert.DoesNotContain("perm:", msg);
    }

    [Fact]
    public void คีย์ที่ไม่อยู่ใน_Catalog_ยังได้ข้อความที่ใช้ได้()
    {
        var msg = PermissionKeys.DeniedMessage("perm:Unknown.Thing");
        Assert.Contains("Unknown.Thing", msg);
        Assert.Contains("/pages/roles.html", msg);
    }

    [Fact]
    public void ด่านคีย์_ไม่มีบริบทคำขอ_ไม่ปฏิเสธ()
    {
        // ทิศตรงข้ามของ RejectApiKey: งานเบื้องหลัง/เทสต์ที่ไม่มี HttpContext ต้องไม่ถูกตีเป็นคำขอจากคีย์
        Assert.Null(OwnerActionGuard.DenyResult(null, "ลบเอกสารถาวร"));
    }
}
