using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 232 (คำตัดสิน 232-Checkout-1) — เอกสารกฎหมาย 2 ภาษา: ตัวตัดสิน <see cref="LegalDocumentLanguage"/> ตัวเดียว (แพลตฟอร์ม · CMS · หน้าจองโต๊ะ)
/// ① normalize ภาษา ② ขอ en มี en ⇒ en · ขอ en ไม่มี ⇒ ไทย + ประกาศ (ไม่แต่ง) · ขอ th ⇒ ไทย (+ บอกว่ามี en ให้สลับ) ③ ชื่อเอกสารตามภาษาที่แสดงจริง
/// </summary>
public class LegalDocumentLanguageRound232Tests
{
    [Theory]
    [InlineData(null, "th")]
    [InlineData("", "th")]
    [InlineData("th", "th")]
    [InlineData("EN", "en")]
    [InlineData(" en ", "en")]
    [InlineData("fr", "th")]   // ไม่รู้จัก = ไทย ไม่โยน (query string สาธารณะ)
    public void Normalize(string? requested, string expected) => Assert.Equal(expected, LegalDocumentLanguage.Normalize(requested));

    [Fact]
    public void ขออังกฤษ_มีฉบับอังกฤษ_ได้อังกฤษ()
    {
        var c = LegalDocumentLanguage.Pick("## ไทย", "## English", "en");
        Assert.Equal("## English", c.Markdown);
        Assert.Equal("en", c.Language);
        Assert.True(c.EnglishAvailable);
        Assert.Null(c.Notice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ขออังกฤษ_ไม่มีฉบับอังกฤษ_ได้ไทยพร้อมประกาศ_ไม่แต่ง(string? en)
    {
        var c = LegalDocumentLanguage.Pick("## ไทย", en, "en");
        Assert.Equal("## ไทย", c.Markdown);
        Assert.Equal("th", c.Language);
        Assert.False(c.EnglishAvailable);
        Assert.Equal(LegalDocumentLanguage.EnglishUnavailableNotice, c.Notice);
    }

    [Fact]
    public void ขอไทย_มีอังกฤษ_ได้ไทยและบอกว่ามีอังกฤษให้สลับ()
    {
        var c = LegalDocumentLanguage.Pick("## ไทย", "## English", null);
        Assert.Equal("## ไทย", c.Markdown);
        Assert.Equal("th", c.Language);
        Assert.True(c.EnglishAvailable);
        Assert.Null(c.Notice);
    }

    [Fact]
    public void ขอไทย_ไม่มีอังกฤษ_ไม่มีประกาศ()
    {
        var c = LegalDocumentLanguage.Pick("## ไทย", null, "th");
        Assert.False(c.EnglishAvailable);
        Assert.Null(c.Notice);
    }

    [Fact]
    public void ชื่อเอกสารตามภาษาที่แสดงจริง()
    {
        Assert.Equal("Terms of Service", LegalDocumentLanguage.TitleFor(LegalDocumentDefaults.KindTerms, "en"));
        Assert.Equal(LegalDocumentDefaults.Title(LegalDocumentDefaults.KindTerms), LegalDocumentLanguage.TitleFor(LegalDocumentDefaults.KindTerms, "th"));
        Assert.Equal("Cancellation & Refund Policy", LegalDocumentLanguage.TitleEn(LegalDocumentDefaults.KindRefund));
        Assert.Equal("Privacy Policy", LegalDocumentLanguage.TitleEn(LegalDocumentDefaults.KindPrivacy));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegalDocumentLanguage.TitleEn("cookies"));
    }

    [Fact]
    public void ทุกชนิดใน_Kinds_มีชื่ออังกฤษ()
    {
        foreach (var k in LegalDocumentDefaults.Kinds) Assert.False(string.IsNullOrWhiteSpace(LegalDocumentLanguage.TitleEn(k)));
    }

    // ═══════════ ฝ่ายค้าน N3: เติม/แก้อังกฤษของฉบับที่เผยแพร่แล้ว — เลขฉบับคงเดิม ═══════════
    [Fact]
    public void ฉบับที่เผยแพร่แล้ว_เติมหรือล้างอังกฤษได้()
    {
        Assert.Null(LegalDocumentLanguage.PublishedEnglishRefusal(true, LegalDocumentPolicy.StatusPublished, "## Terms", LegalMarkdown.MaxLength));
        Assert.Null(LegalDocumentLanguage.PublishedEnglishRefusal(true, LegalDocumentPolicy.StatusPublished, "", LegalMarkdown.MaxLength));
        Assert.Null(LegalDocumentLanguage.PublishedEnglishRefusal(true, LegalDocumentPolicy.StatusPublished, null, LegalMarkdown.MaxLength));
    }

    [Fact]
    public void ทิศตรงข้าม_ร่าง_ฉบับตั้งต้นไม่มีแถว_ยาวเกิน_ถูกปฏิเสธพร้อมทางไปต่อ()
    {
        Assert.Contains("บันทึกร่าง", LegalDocumentLanguage.PublishedEnglishRefusal(true, LegalDocumentPolicy.StatusDraft, "x", LegalMarkdown.MaxLength));
        Assert.Contains("ออกฉบับใหม่ผ่านร่าง", LegalDocumentLanguage.PublishedEnglishRefusal(false, null, "x", LegalMarkdown.MaxLength));
        Assert.NotNull(LegalDocumentLanguage.PublishedEnglishRefusal(true, LegalDocumentPolicy.StatusPublished, new string('a', 11), 10));
    }
}
