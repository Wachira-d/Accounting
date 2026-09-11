using System.Text.Json;
using System.Text.RegularExpressions;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Cms;
using Xunit;

namespace Accounting.Tests;

/// <summary>เทมเพลตเว็บทุกประเภทธุรกิจ — ล็อกผลตรวจของทีมรอบ 158 ให้เป็นด่านถาวร:
/// (1) ทุกลิงก์/CTA ที่แผน seed ต้องชี้ไป slug ที่แผนเดียวกัน seed (ไม่งั้นกดแล้ว 404 บนเว็บใหม่)
/// (2) เว็บชนิด Booking ต้องมีหน้าจอง (D-01: การ์ด "จองนัดหมาย" เคยได้เว็บที่ปรึกษาไม่มีที่จอง)
/// (3) ฟอร์มขอจอง/นัดหมาย ต้องมีช่องวัน·เวลา (D-03) ไม่ใช่ฟอร์มติดต่อเปล่า</summary>
public class CmsSiteTemplateSeederTests
{
    private static readonly IndustryType[] All = Enum.GetValues<IndustryType>();
    private static readonly Regex LinkRx = new(@"(?:ctaUrl|href)\\?""\s*:?\s*\\?""?/([a-z0-9-]+)", RegexOptions.IgnoreCase);

    public static IEnumerable<object[]> Industries() => All.Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Industries))]
    public void ทุกแผน_ลิงก์ภายในต้องชี้ไปหน้าที่แผนเดียวกัน_seed(IndustryType industry)
    {
        var pages = CmsSiteTemplateSeeder.BuildSeed(Guid.NewGuid(), Guid.NewGuid(), industry, "t");
        Assert.NotEmpty(pages);
        Assert.Contains(pages, p => p.Slug == "home");
        var slugs = pages.Select(p => p.Slug).ToHashSet();
        // slug พิเศษที่ storefront จัดการเองแม้ไม่มีหน้า CMS
        slugs.UnionWith(new[] { "cart", "checkout", "booking", "book", "rooms" });
        foreach (var block in pages.SelectMany(p => p.Blocks))
        {
            foreach (Match m in LinkRx.Matches(block.ConfigJson))
                Assert.True(slugs.Contains(m.Groups[1].Value), $"{industry}: ลิงก์ /{m.Groups[1].Value} ไม่มีหน้าในแผน");
        }
    }

    [Theory]
    [MemberData(nameof(Industries))]
    public void ทุกแผน_slug_ต้องไม่ซ้ำ_และ_ConfigJson_ต้องเป็น_JSON_ที่อ่านได้(IndustryType industry)
    {
        var pages = CmsSiteTemplateSeeder.BuildSeed(Guid.NewGuid(), Guid.NewGuid(), industry, "t", SiteType.Booking);
        Assert.Equal(pages.Count, pages.Select(p => p.Slug).Distinct().Count());
        foreach (var b in pages.SelectMany(p => p.Blocks))
            JsonDocument.Parse(b.ConfigJson).Dispose();
    }

    [Fact]
    public void เว็บชนิด_Booking_ต้องมีหน้าจองเสมอ_แม้แผนอุตสาหกรรมไม่มี()
    {
        var corporate = CmsSiteTemplateSeeder.BuildSeed(Guid.NewGuid(), Guid.NewGuid(), IndustryType.Service, "t", SiteType.Corporate);
        Assert.DoesNotContain(corporate.SelectMany(p => p.Blocks), b => b.BlockType == CmsBlockType.BookingCalendar);

        var booking = CmsSiteTemplateSeeder.BuildSeed(Guid.NewGuid(), Guid.NewGuid(), IndustryType.Service, "t", SiteType.Booking);
        Assert.Contains(booking, p => p.Slug == "booking");
        Assert.Contains(booking.SelectMany(p => p.Blocks), b => b.BlockType == CmsBlockType.BookingCalendar);
    }

    [Fact]
    public void แผนที่มีหน้าจองอยู่แล้ว_ต้องไม่ถูกเติมหน้าจองซ้ำ()
    {
        var spa = CmsSiteTemplateSeeder.BuildSeed(Guid.NewGuid(), Guid.NewGuid(), IndustryType.Beauty, "t", SiteType.Booking);
        Assert.Single(spa, p => p.Slug == "booking");
    }

    [Theory]
    [InlineData(IndustryType.Restaurant)]
    [InlineData(IndustryType.Beauty)]
    [InlineData(IndustryType.Healthcare)]
    [InlineData(IndustryType.Hotel)]
    public void ฟอร์มขอจอง_ต้องมีช่องวันที่เป็นช่องแยก_ไม่ใช่ข้อความอิสระ(IndustryType industry)
    {
        var pages = CmsSiteTemplateSeeder.BuildSeed(Guid.NewGuid(), Guid.NewGuid(), industry, "t");
        var bookingPage = pages.First(p => p.Blocks.Any(b => b.BlockType == CmsBlockType.BookingCalendar));
        var form = bookingPage.Blocks.First(b => b.BlockType == CmsBlockType.ContactForm);
        using var doc = JsonDocument.Parse(form.ConfigJson);
        var extra = doc.RootElement.GetProperty("extraFields").EnumerateArray().ToList();
        Assert.Contains(extra, f => f.GetProperty("type").GetString() == "date");
        Assert.True(doc.RootElement.GetProperty("phoneRequired").GetBoolean());
    }

    [Fact]
    public void บริการจองคิวตัวอย่าง_มีให้ทุกอุตสาหกรรมที่แผนมีหน้าจองแบบ_slot_และไม่มีให้ที่พัก()
    {
        foreach (var industry in All)
        {
            var pages = CmsSiteTemplateSeeder.BuildSeed(Guid.NewGuid(), Guid.NewGuid(), industry, "t");
            var hasSlotBooking = industry != IndustryType.Hotel && pages.SelectMany(p => p.Blocks).Any(b => b.BlockType == CmsBlockType.BookingCalendar);
            if (hasSlotBooking)
                Assert.True(CmsBookingServiceSeeder.SamplesFor(industry).Count > 0, $"{industry}: มีหน้าจองแต่ไม่มีบริการตัวอย่าง → หน้าจองว่างวันแรก");
        }
        Assert.Empty(CmsBookingServiceSeeder.SamplesFor(IndustryType.Hotel)); // ที่พักใช้ระบบจองห้อง ไม่ใช่ slot
        foreach (var industry in All)
        {
            var slugs = CmsBookingServiceSeeder.SamplesFor(industry).Select(s => CmsBookingServiceSeeder.Slugify(s.NameEn)).ToList();
            Assert.Equal(slugs.Count, slugs.Distinct().Count());
            Assert.All(slugs, sl => Assert.Matches("^[a-z0-9-]+$", sl));
        }
    }
}
