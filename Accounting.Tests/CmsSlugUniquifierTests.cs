using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>Site.Slug สร้างจาก "ชื่อเว็บไซต์" อัตโนมัติ ผู้ใช้ไม่มีช่องให้แก้
/// ⇒ ชนแล้วต้องเติมเลขต่อท้ายให้ ไม่ใช่โยน error ให้ผู้ใช้ไปเดาเอง
/// (ต่างจาก Subdomain ที่ผู้ใช้พิมพ์เอง — ตัวนั้นต้องบอกให้เปลี่ยน ห้ามเปลี่ยนเงียบ ๆ)</summary>
public class CmsSlugUniquifierTests
{
    private const int Slug = CmsFieldLengths.SiteSlug;

    [Fact]
    public void ไม่ชนของเดิม_ต้องคืนค่าเดิมไม่แตะ()
        => Assert.Equal("b1", CmsSlugUniquifier.MakeUnique("b1", new[] { "a1", "c1" }, Slug));

    [Fact]
    public void ชนของเดิม_ต้องเติมเลขสองต่อท้าย()
        => Assert.Equal("b1-2", CmsSlugUniquifier.MakeUnique("b1", new[] { "b1" }, Slug));

    [Fact]
    public void ชนทั้งตัวเดิมและตัวที่สอง_ต้องข้ามไปตัวที่สาม()
        => Assert.Equal("b1-3", CmsSlugUniquifier.MakeUnique("b1", new[] { "b1", "b1-2" }, Slug));

    [Fact]
    public void เทียบชื่อต้องไม่สนตัวพิมพ์ใหญ่เล็ก()
        => Assert.Equal("b1-2", CmsSlugUniquifier.MakeUnique("b1", new[] { "B1" }, Slug));

    [Fact]
    public void รายการที่จองไว้ว่างหรือเป็นค่าว่าง_ต้องไม่พัง()
    {
        Assert.Equal("b1", CmsSlugUniquifier.MakeUnique("b1", null, Slug));
        Assert.Equal("b1", CmsSlugUniquifier.MakeUnique("b1", new string?[] { null, "" }, Slug));
    }

    [Fact]
    public void ชื่อที่ตัดแล้วเหลือว่าง_ต้องมีค่าให้_routing_จับได้()
        => Assert.Equal("site", CmsSlugUniquifier.MakeUnique("", null, Slug));

    [Fact]
    public void ผลลัพธ์ต้องไม่ยาวเกินคอลัมน์_แม้ชื่อยาวเกินและชนกัน()
    {
        // URL ย่อยสั้นที่สุดในระบบ (63) คือเคสที่ตัดแล้วเจ็บที่สุด
        var longName = new string('x', 200);
        var taken = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var got = CmsSlugUniquifier.MakeUnique(longName, taken, CmsFieldLengths.SiteSubdomain);
            Assert.True(got.Length <= CmsFieldLengths.SiteSubdomain, $"ยาวเกิน: {got.Length}");
            Assert.DoesNotContain(got, taken);
            taken.Add(got);
        }
    }

    [Fact]
    public void หัวที่ถูกตัดคาขีด_ต้องไม่ได้ขีดซ้อนกัน()
    {
        // "aaaa-" ถูกตัดพอดีที่ขีด แล้วต่อ "-2" จะได้ "aaaa--2" ถ้าไม่เล็มขีดท้ายทิ้ง
        var got = CmsSlugUniquifier.MakeUnique("aaaa-bbbb", new[] { "aaaa-bbbb" }, 7);
        Assert.DoesNotContain("--", got);
        Assert.True(got.Length <= 7);
    }

    [Fact]
    public void ทุกผลลัพธ์ต้องไม่ชนของที่จองไว้_ไล่ต่อเนื่องหลายรอบ()
    {
        // invariant ที่จับคลาสบั๊กนี้ได้จริง — ยอดรายตัว "ดูถูก" ได้ทั้งที่ชนกันเอง
        var taken = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            var got = CmsSlugUniquifier.MakeUnique("b1", taken, CmsFieldLengths.SiteSlug);
            Assert.DoesNotContain(got, taken);
            taken.Add(got);
        }
        Assert.Equal(50, taken.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
