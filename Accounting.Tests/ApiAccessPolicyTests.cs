using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ปิดสวิตช์ "เปิดใช้งาน API Access" แล้วคีย์ <c>acc_</c> ที่ออกไว้ต้องหยุดใช้งานทันที** (ผลตรวจ S-04 · รอบ 193)
///
/// <para>═══ บั๊กที่ล็อกไว้ ═══ <c>CompanySettings.EnableApiAccess</c> ถูกตรวจแค่ตอนออกคีย์ · <c>ApiKeyMiddleware</c>
/// รับคีย์ทุกดอกที่ Active ⇒ เจ้าของสงสัยคีย์รั่ว กดปิด → คู่ค้ายังอ่าน/เขียนได้ต่อ</para>
///
/// <para>ครึ่งแรก = ปิดแล้วได้ 403 + ข้อความไทยที่บอกทางไปต่อ · ครึ่งหลัง (ทิศตรงข้าม) = เปิดอยู่ต้องผ่าน
/// (ไม่งั้นเทสต์ผ่านได้ทั้งตอนแก้ถูกและตอน "ปิดคีย์ทุกดอกทิ้ง")</para>
/// </summary>
public class ApiAccessPolicyTests
{
    // ════════ ครึ่งแรก: ปิด = 403 ════════

    [Fact]
    public void ปิดสวิตช์แล้ว_คีย์เดิม_ได้403_พร้อมข้อความไทย()
    {
        var d = ApiAccessPolicy.EvaluateAccountKey(false);
        Assert.False(d.Allowed);
        Assert.Equal(403, d.StatusCode);
        Assert.Equal(ApiAccessPolicy.KeyDisabledMessage, d.Message);
        Assert.Contains("เปิดใช้งาน API Access", d.Message);     // บอกทางไปต่อ ไม่ใช่แค่ "ห้าม"
        Assert.Equal(ApiAccessPolicy.RuleCode, d.RuleCode);
    }

    [Fact]
    public void ไม่มีแถวค่าตั้ง_ไม่รู้_ต้องไม่ตกเป็นผ่าน()
    {
        // entity default = false และคีย์ acc_ ออกได้เฉพาะเมื่อมีแถวที่เปิดไว้ ⇒ ไม่มีแถว = ผิดปกติ (DOCTRINE §1)
        var d = ApiAccessPolicy.EvaluateAccountKey(null);
        Assert.False(d.Allowed);
        Assert.Equal(403, d.StatusCode);
    }

    [Fact]
    public void ออกคีย์ใหม่ขณะปิด_ไม่ได้_ใช้สวิตช์ตัวเดียวกับตอนใช้งาน()
    {
        Assert.False(ApiAccessPolicy.CanIssueKey(false));
        Assert.False(ApiAccessPolicy.CanIssueKey(null));
        Assert.Contains("API Access", ApiAccessPolicy.IssueDisabledMessage);
    }

    // ════════ ครึ่งหลัง (ทิศตรงข้าม): เปิด = ผ่าน ════════

    [Fact]
    public void เปิดสวิตช์อยู่_คีย์ใช้งานได้ตามปกติ()
    {
        var d = ApiAccessPolicy.EvaluateAccountKey(true);
        Assert.True(d.Allowed);
        Assert.Equal(200, d.StatusCode);
        Assert.Null(d.Message);
        Assert.Null(d.RuleCode);
    }

    [Fact]
    public void เปิดสวิตช์อยู่_ออกคีย์ใหม่ได้()
    {
        Assert.True(ApiAccessPolicy.CanIssueKey(true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ออกคีย์ได้_ก็ต่อเมื่อ_ใช้คีย์ได้_สองด่านไม่มีวันขัดกัน(bool enabled)
    {
        Assert.Equal(ApiAccessPolicy.EvaluateAccountKey(enabled).Allowed, ApiAccessPolicy.CanIssueKey(enabled));
    }
}
