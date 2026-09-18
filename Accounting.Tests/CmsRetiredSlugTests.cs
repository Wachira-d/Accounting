using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>slug ของหน้า CMS ที่ถูกปลดออก — unique index (SiteId, Slug) ไม่ได้กรอง IsDeleted
/// ⇒ soft-delete แล้วสร้างหน้า slug เดิมใหม่จะชน ต้องย้าย slug เดิมออกไปเสมอ</summary>
public class CmsRetiredSlugTests
{
    private static readonly DateTime T = new(2026, 9, 11, 10, 30, 15, 123, DateTimeKind.Utc);

    [Fact]
    public void ย้าย_slug_แล้วต้องไม่เท่าเดิม_และตรวจจับได้ว่าถูกปลด()
    {
        var r = CmsRetiredSlug.For("booking", T);
        Assert.NotEqual("booking", r);
        Assert.StartsWith("booking" + CmsRetiredSlug.Marker, r);
        Assert.True(CmsRetiredSlug.IsRetired(r));
        Assert.False(CmsRetiredSlug.IsRetired("booking"));
        Assert.False(CmsRetiredSlug.IsRetired(null));
    }

    [Fact]
    public void ปลดสองครั้งคนละเวลา_ต้องได้_slug_ไม่ซ้ำกัน()
    {
        var a = CmsRetiredSlug.For("home", T);
        var b = CmsRetiredSlug.For("home", T.AddMilliseconds(1));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ปลดซ้ำ_ต้องไม่ต่อป้ายซ้อนกัน_และยังคงชื่อเดิมไว้()
    {
        // เดิมถ้าปลดหน้าที่ถูกปลดแล้วอีกรอบ จะได้ `home--retired-A--retired-B`
        // ⇒ ยาวขึ้นทุกครั้งจนชื่อเดิมถูกตัดหัวทิ้ง ร่องรอยว่า "เคยชื่ออะไร" หายไป
        var once = CmsRetiredSlug.For("home", T);
        var twice = CmsRetiredSlug.For(once, T.AddSeconds(5));

        Assert.Equal(1, CountMarkers(twice));
        Assert.StartsWith("home" + CmsRetiredSlug.Marker, twice);
        Assert.NotEqual(once, twice);
        Assert.True(CmsRetiredSlug.IsRetired(twice));
    }

    private static int CountMarkers(string s)
    {
        var n = 0;
        var i = s.IndexOf(CmsRetiredSlug.Marker, StringComparison.Ordinal);
        while (i >= 0)
        {
            n++;
            i = s.IndexOf(CmsRetiredSlug.Marker, i + CmsRetiredSlug.Marker.Length, StringComparison.Ordinal);
        }
        return n;
    }

    [Fact]
    public void slug_ยาวสุด_256_ต้องไม่เกินคอลัมน์หลังต่อท้าย()
    {
        var longSlug = new string('a', CmsRetiredSlug.MaxLength);
        var r = CmsRetiredSlug.For(longSlug, T);
        Assert.Equal(CmsRetiredSlug.MaxLength, r.Length);
        Assert.True(CmsRetiredSlug.IsRetired(r));
    }

    [Fact]
    public void ตราเวลาเป็น_ค_ศ_InvariantCulture_เสมอ()
    {
        // culture th-TH ทำให้ yyyy เป็น พ.ศ. ถ้าไม่ระบุ InvariantCulture — ล็อกไว้
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            var r = CmsRetiredSlug.For("about", T);
            Assert.Contains("20260911103015123", r);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public void ความยาวปริยายต้องยังเป็นของ_SitePage_เหมือนเดิม()
        => Assert.Equal(CmsFieldLengths.PageSlug, CmsRetiredSlug.MaxLength);

    [Theory]
    [InlineData(CmsFieldLengths.SiteSubdomain)]
    [InlineData(CmsFieldLengths.SiteSlug)]
    [InlineData(CmsFieldLengths.Domain)]
    public void ปลดคีย์ของคอลัมน์ที่สั้นกว่า_ต้องไม่ยาวเกินและยังตรวจจับได้(int maxLength)
    {
        // Site.Subdomain ยาวได้แค่ 63 — ใช้ความยาวปริยาย 256 จะได้สตริงยาวเกินคอลัมน์
        // แล้ว insert ล้มตอน runtime โดยคอมไพเลอร์ไม่จับ
        var got = CmsRetiredSlug.For(new string('a', 400), T, maxLength);
        Assert.True(got.Length <= maxLength, $"ยาวเกิน: {got.Length} > {maxLength}");
        Assert.True(CmsRetiredSlug.IsRetired(got));
    }

}
