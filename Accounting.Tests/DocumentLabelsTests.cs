using Accounting.Services.Implementations.Pdf;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ภาษาเอกสารที่ออก (ไทย/อังกฤษ) — ครอบ FE/DOC ตาม TEST_PLAN.md
/// จุดที่ห้ามพลาด: ทุก key ต้องมีครบทั้งสองภาษา (ไม่งั้นเอกสารโผล่ key ดิบ)
/// และรหัสสาขาต้อง render ตามประกาศอธิบดีฯ ฉบับที่ 199
/// </summary>
public class DocumentLabelsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("th")]
    [InlineData("TH")]
    [InlineData("xx")]      // ค่าที่ไม่รู้จัก → ไทย (ค่าตั้งต้นปลอดภัย)
    public void Unknown_or_thai_language_gives_thai(string? lang)
        => Assert.False(DocumentLabels.For(lang).IsEnglish);

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData("En")]
    public void English_language_is_case_insensitive(string lang)
        => Assert.True(DocumentLabels.For(lang).IsEnglish);

    [Fact]
    public void Thai_labels_are_thai_and_english_labels_are_not()
    {
        var th = DocumentLabels.For("th");
        var en = DocumentLabels.For("en");

        Assert.Equal("รายการ", th.ColItem);
        Assert.Equal("ยอดรวมทั้งสิ้น", th.TotalGrand);
        Assert.Equal("Description", en.ColItem);
        Assert.Equal("Grand total", en.TotalGrand);
    }

    [Fact]
    public void Every_label_exists_in_both_languages()
    {
        // ป้ายที่มีในไทยแต่ไม่มีในอังกฤษจะคืน "key" ดิบออกไปพิมพ์บนเอกสารลูกค้า
        var th = DocumentLabels.For("th");
        var en = DocumentLabels.For("en");
        var props = typeof(DocumentLabels).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
            .ToList();

        Assert.NotEmpty(props);
        foreach (var p in props)
        {
            var thVal = (string?)p.GetValue(th);
            var enVal = (string?)p.GetValue(en);
            Assert.False(string.IsNullOrWhiteSpace(thVal), $"ป้าย {p.Name} ไม่มีค่าภาษาไทย");
            Assert.False(string.IsNullOrWhiteSpace(enVal), $"ป้าย {p.Name} ไม่มีค่าภาษาอังกฤษ");
        }
    }

    [Fact]
    public void Missing_key_returns_the_key_itself_so_gaps_are_visible()
        => Assert.Equal("no_such_label", DocumentLabels.For("th")["no_such_label"]);

    // ── รหัสสาขา (ประกาศอธิบดีฯ ฉบับที่ 199) ──────────────────────────

    [Fact]
    public void Branch_00000_renders_head_office()
    {
        Assert.Equal("สำนักงานใหญ่", DocumentLabels.For("th").Branch("00000"));
        Assert.Equal("Head Office", DocumentLabels.For("en").Branch("00000"));
    }

    [Fact]
    public void Branch_other_renders_branch_number_without_leading_zeros()
    {
        Assert.Equal("สาขาที่ 3", DocumentLabels.For("th").Branch("00003"));
        Assert.Equal("Branch 12", DocumentLabels.For("en").Branch("00012"));
    }

    [Fact]
    public void Branch_empty_renders_nothing()
    {
        // บุคคลธรรมดาไม่มีสาขา — ห้ามพิมพ์ "(สำนักงานใหญ่)" ต่อท้ายเลขผู้เสียภาษี
        Assert.Equal("", DocumentLabels.For("th").Branch(null));
        Assert.Equal("", DocumentLabels.For("th").Branch("   "));
    }

    // ── หัวเอกสารสองภาษา (§86/4) ─────────────────────────────────────

    [Fact]
    public void Legal_title_keeps_thai_alongside_english()
    {
        // §86/4 บังคับคำว่า "ใบกำกับภาษี" เป็นไทย — ตัดทิ้ง = ผู้ซื้อเคลมภาษีซื้อ
        // ไม่ได้ (§82/5(1)) ดังนั้นโหมดอังกฤษต้องพิมพ์คู่กัน
        var en = DocumentLabels.For("en").LegalTitle("ใบกำกับภาษี", "Tax Invoice");
        Assert.Contains("ใบกำกับภาษี", en);
        Assert.Contains("Tax Invoice", en);

        Assert.Equal("ใบกำกับภาษี", DocumentLabels.For("th").LegalTitle("ใบกำกับภาษี", "Tax Invoice"));
    }

    // ── รูปแบบวันที่ ─────────────────────────────────────────────────

    [Fact]
    public void Date_format_follows_language()
    {
        var d = new DateTime(2026, 1, 9);
        Assert.Equal("09/01/2026", DocumentLabels.For("th").Date(d));
        Assert.Equal("09 Jan 2026", DocumentLabels.For("en").Date(d));
    }
}
