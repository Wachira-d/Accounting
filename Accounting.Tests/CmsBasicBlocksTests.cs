using System.Text.Json;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// บล็อกพื้นฐานของเว็บ CMS (Hero · Image · FAQ · Testimonials) — ตัวอ่าน+เรนเดอร์ตัวเดียว <see cref="CmsBasicBlocks"/> (ทีม W4 · R-46)
///
/// <para>ที่มา: เซิร์ฟเวอร์เดิมอ่าน <c>title/subtitle/backgroundImage</c> · <c>question/answer</c> · <c>items[].name/text</c> ส่วนตัวแก้ไข/เทมเพลต/storefront
/// ใช้ <c>headline/subheadline/backgroundUrl</c> · <c>q/a</c> · <c>testimonials[].quote/author</c> ⇒ สองตัวเรนเดอร์ drift · ตอนนี้ storefront ใช้ HTML
/// จากตัวนี้อย่างเดียว ⇒ golden ต้องล็อก (ก) โครง HTML ที่ storefront เคยวาด (คลาส blk-*) เท่าเดิม (ข) คีย์เดิมของเซิร์ฟเวอร์ยังอ่านได้
/// (ค) ข้อความผู้ใช้ไม่หลุดเป็น HTML/สคริปต์ (กฎเหล็ก #4 C)</para>
/// </summary>
public class CmsBasicBlocksTests
{
    private static JsonElement J(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    // ══════════ Hero ══════════

    [Fact]
    public void Hero_golden_คีย์ตัวแก้ไข_HTMLเท่าที่storefrontเคยวาด()
    {
        var html = CmsBasicBlocks.RenderHero(CmsBasicBlocks.ParseHero(J(
            """{"headline":"ยินดีต้อนรับ","subheadline":"บริการดี","backgroundUrl":"/uploads/cms/bg.jpg","ctaText":"ติดต่อ","ctaUrl":"/contact"}""")), "");
        Assert.Equal(
            "<div class=\"blk-hero with-bg\" style=\"background-image:url('/uploads/cms/bg.jpg')\"><h1>ยินดีต้อนรับ</h1><p>บริการดี</p><a href=\"/contact\" class=\"btn\">ติดต่อ</a></div>",
            html);
    }

    [Fact]
    public void Hero_คีย์เดิมของเซิร์ฟเวอร์_title_subtitle_backgroundImage_ยังอ่านได้()
    {
        var m = CmsBasicBlocks.ParseHero(J("""{"title":"T","subtitle":"S","backgroundImage":"https://x.test/a.jpg"}"""));
        Assert.Equal("T", m.Headline);
        Assert.Equal("S", m.Subheadline);
        Assert.Equal("https://x.test/a.jpg", m.BackgroundUrl);
    }

    [Fact]
    public void Hero_ไม่มีพื้นหลัง_ไม่มีปุ่ม_ไม่ใส่style_ไม่ใส่a()
    {
        var html = CmsBasicBlocks.RenderHero(CmsBasicBlocks.ParseHero(J("""{"headline":"H"}""")), "");
        Assert.Equal("<div class=\"blk-hero\"><h1>H</h1></div>", html);
    }

    [Fact]
    public void Hero_escape_หัวข้อที่มีสคริปต์_และพื้นหลังjavascript_ถูกตัด()
    {
        var html = CmsBasicBlocks.RenderHero(CmsBasicBlocks.ParseHero(J(
            """{"headline":"<script>alert(1)</script>","backgroundUrl":"javascript:alert(1)","ctaText":"x","ctaUrl":"javascript:alert(2)"}""")), "");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("with-bg", html);
        Assert.Contains("href=\"#\"", html);
    }

    [Fact]
    public void Hero_คลาสของผู้แก้ไข_ต่อท้ายผ่านCmsBlockCss()
    {
        var html = CmsBasicBlocks.RenderHero(CmsBasicBlocks.ParseHero(J("""{"headline":"H"}""")), CmsBlockCss.Suffix("my-hero \" onmouseover=\"x"));
        Assert.StartsWith("<div class=\"blk-hero my-hero\">", html);
    }

    // ══════════ ลิงก์ปุ่ม ══════════

    [Theory]
    [InlineData("/contact", "/contact")]
    [InlineData("contact", "/contact")]
    [InlineData("#pricing", "#pricing")]
    [InlineData("mailto:a@b.c", "mailto:a@b.c")]
    [InlineData("tel:021234567", "tel:021234567")]
    [InlineData("https://line.me/ti/p/x", "https://line.me/ti/p/x")]
    [InlineData("javascript:alert(1)", "#")]
    [InlineData("data:text/html,x", "#")]
    [InlineData("//evil.test/x", "#")]
    [InlineData("", "#")]
    [InlineData(null, "#")]
    public void SafeLink_ยอมเฉพาะลิงก์ที่ปลอดภัย(string? input, string expected) =>
        Assert.Equal(expected, CmsBasicBlocks.SafeLink(input));

    // ══════════ Image ══════════

    [Fact]
    public void Image_golden_url_alt_caption()
    {
        var html = CmsBasicBlocks.RenderImage(CmsBasicBlocks.ParseImage(J("""{"url":"/uploads/cms/a.jpg","alt":"A","caption":"คำบรรยาย"}""")), "");
        Assert.Equal("<figure class=\"blk-image\"><img src=\"/uploads/cms/a.jpg\" alt=\"A\" loading=\"lazy\"><figcaption>คำบรรยาย</figcaption></figure>", html);
    }

    [Fact]
    public void Image_คีย์src_เดิม_ยังอ่านได้_และaltว่างใช้caption()
    {
        var m = CmsBasicBlocks.ParseImage(J("""{"src":"https://x.test/b.jpg","caption":"B"}"""));
        Assert.Equal("https://x.test/b.jpg", m.Url);
        Assert.Equal("B", m.Alt);
    }

    [Fact]
    public void Image_URLไม่ปลอดภัย_ไม่วาดimg()
    {
        var html = CmsBasicBlocks.RenderImage(CmsBasicBlocks.ParseImage(J("""{"url":"javascript:alert(1)"}""")), "");
        Assert.DoesNotContain("<img", html);
        Assert.Contains("blk-image--empty", html);
    }

    // ══════════ FAQ ══════════

    [Fact]
    public void Faq_golden_คีย์q_a_พร้อมหัวข้อ()
    {
        var html = CmsBasicBlocks.RenderFaq(CmsBasicBlocks.ParseFaq(J(
            """{"headline":"คำถามที่พบบ่อย","items":[{"q":"เปิดกี่โมง","a":"9 โมง"},{"q":"มีที่จอดรถไหม","a":"มี"}]}""")), "");
        Assert.Equal(
            "<div class=\"blk-faq\"><h2>คำถามที่พบบ่อย</h2>" +
            "<details class=\"faq-item\"><summary>เปิดกี่โมง</summary><div class=\"a\">9 โมง</div></details>" +
            "<details class=\"faq-item\"><summary>มีที่จอดรถไหม</summary><div class=\"a\">มี</div></details></div>",
            html);
    }

    [Fact]
    public void Faq_คีย์เดิม_question_answer_ยังอ่านได้_และข้ามรายการไม่มีคำถาม()
    {
        var m = CmsBasicBlocks.ParseFaq(J("""{"items":[{"question":"Q1","answer":"A1"},{"answer":"ไม่มีคำถาม"}]}"""));
        Assert.Single(m.Items);
        Assert.Equal("Q1", m.Items[0].Question);
        Assert.Equal("A1", m.Items[0].Answer);
    }

    [Fact]
    public void Faq_escape_คำตอบที่มีHTML()
    {
        var html = CmsBasicBlocks.RenderFaq(CmsBasicBlocks.ParseFaq(J("""{"items":[{"q":"x","a":"<img src=x onerror=alert(1)>"}]}""")), "");
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
    }

    // ══════════ Testimonials ══════════

    [Fact]
    public void Testimonials_golden_testimonials_quote_author_role()
    {
        var html = CmsBasicBlocks.RenderTestimonials(CmsBasicBlocks.ParseTestimonials(J(
            """{"headline":"ลูกค้าพูดถึงเรา","testimonials":[{"quote":"ดีมาก","author":"คุณสมชาย","role":"ลูกค้าประจำ"}]}""")), "");
        Assert.Equal(
            "<div class=\"blk-testi\"><h2>ลูกค้าพูดถึงเรา</h2><div class=\"testi-grid\">" +
            "<div class=\"testi-card\"><div class=\"quote\">\"ดีมาก\"</div><div class=\"author\">คุณสมชาย <span style=\"color:var(--text-secondary)\">— ลูกค้าประจำ</span></div></div>" +
            "</div></div>",
            html);
    }

    [Fact]
    public void Testimonials_คีย์เดิม_items_text_name_avatar_ยังอ่านได้()
    {
        var m = CmsBasicBlocks.ParseTestimonials(J("""{"items":[{"text":"T","name":"N","avatar":"/uploads/cms/av.jpg"}]}"""));
        Assert.Single(m.Items);
        Assert.Equal("T", m.Items[0].Quote);
        Assert.Equal("N", m.Items[0].Author);
        Assert.Equal("/uploads/cms/av.jpg", m.Items[0].AvatarUrl);
        Assert.Contains("class=\"testi-avatar\"", CmsBasicBlocks.RenderTestimonials(m, ""));
    }

    [Fact]
    public void Testimonials_ว่าง_แสดงข้อความยังไม่มีรีวิว_เหมือนstorefrontเดิม()
    {
        var html = CmsBasicBlocks.RenderTestimonials(CmsBasicBlocks.ParseTestimonials(J("""{}""")), "");
        Assert.Equal("<div class=\"blk-testi\"><div class=\"testi-grid\"><div style=\"color:var(--text-secondary)\">ยังไม่มีรีวิว</div></div></div>", html);
    }

    [Fact]
    public void Testimonials_escape_ชื่อที่มีอัญประกาศ()
    {
        var html = CmsBasicBlocks.RenderTestimonials(CmsBasicBlocks.ParseTestimonials(J("""{"testimonials":[{"quote":"x","author":"a\"><script>"}]}""")), "");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("a&quot;&gt;&lt;script&gt;", html);
    }

    // ══════════ คอนฟิกเสีย — หน้าสาธารณะต้องไม่พัง ══════════

    [Fact]
    public void คอนฟิกไม่ใช่object_ทุกตัวอ่านเป็นโมเดลว่าง_ไม่throw()
    {
        var arr = J("[1,2,3]");
        Assert.Equal("", CmsBasicBlocks.ParseHero(arr).Headline);
        Assert.Equal("", CmsBasicBlocks.ParseImage(arr).Url);
        Assert.Empty(CmsBasicBlocks.ParseFaq(arr).Items);
        Assert.Empty(CmsBasicBlocks.ParseTestimonials(arr).Items);
    }
}
