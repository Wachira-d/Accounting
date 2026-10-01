using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม PL — renderer สองตัวได้สีแบรนด์เดียวกัน (A-PL6) · CSS กำหนดเองของธีมเก่าหลุดแท็ก style ไม่ได้ (A-PL7)
/// ทุกข้อมีทิศตรงข้าม: ใบที่ไม่ผูกแบรนด์/ธีมปกติต้องได้ผลเท่าเดิม
/// </summary>
public class PlatformRound201Tests
{
    // ── A-PL6 ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PL6_BrandColorWins_InHtmlCss_LikeQuestPdf()
    {
        var t = new DocumentTemplate { PrimaryColor = "#111111", AccentColor = "#222222" };
        var css = PdfGenerationService.BuildCss(t, "#AA0000");
        Assert.Contains("color: #AA0000", css);                        // body = สีหลักจากแบรนด์
        Assert.Contains(".company-name", css);
        Assert.DoesNotContain("#222222", css);                          // สีเน้นของเทมเพลตถูกแบรนด์ทับ
        Assert.Contains("#AA0000", PdfGenerationService.BuildLayoutCss("ModernLeft", t, "#AA0000"));
        // ตัวตัดสินเดียวที่ QuestPDF BuildBranding ใช้ — ผลเดียวกัน
        Assert.Equal("#AA0000", DocumentBrandColor.Accent("#AA0000", "#222222"));
        Assert.Equal("#AA0000", DocumentBrandColor.Primary("#AA0000", "#111111"));
    }

    [Fact]
    public void PL6_NoBrand_OrBadBrandColor_KeepsTemplateColors()
    {
        var t = new DocumentTemplate { PrimaryColor = "#111111", AccentColor = "#222222" };
        Assert.Equal(PdfGenerationService.BuildCss(t), PdfGenerationService.BuildCss(t, null));
        Assert.Equal(PdfGenerationService.BuildCss(t), PdfGenerationService.BuildCss(t, "red;}body{display:none"));
        Assert.Equal("#222222", DocumentBrandColor.Accent(null, "#222222"));
        Assert.Equal("#222222", DocumentBrandColor.Accent("  ", "#222222"));
        Assert.Equal("#111111", DocumentBrandColor.Primary("javascript:x", "#111111"));
        Assert.Null(DocumentBrandColor.Accent(null, null));             // ผู้เรียกใส่ค่าเริ่มต้นของตัวเองต่อ
    }

    // ── A-PL7 ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PL7_LegacyCustomCss_CannotCloseStyleTag()
    {
        var legacy = ".x{color:red}</style><script>alert(1)</script><style>";
        var safe = CssThemeValue.SafeCustomCss(legacy)!;
        Assert.DoesNotContain("<", safe);
        Assert.DoesNotContain("</style", safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\3c /style>", safe);                           // CSS escape — ไม่ใช่แท็ก HTML
    }

    [Fact]
    public void PL7_NormalCustomCss_IsUntouched()
    {
        const string css = ".hero { background: #fff; } .card > .title::after { content: '>'; }";
        Assert.Equal(css, CssThemeValue.SafeCustomCss(css));
        Assert.Null(CssThemeValue.SafeCustomCss("   "));
        Assert.Null(CssThemeValue.SafeCustomCss(null));
    }
}
