using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 250 WP-03 (A H-01 · คำตัดสิน Y6) — แบนเนอร์ "ฉบับตั้งต้นมีข้อใหม่ที่ฉบับเผยแพร่ยังไม่มี" ที่ /admin/legal.html
/// ล็อกทั้งสองทิศ: ฉบับเผยแพร่ที่ขาดข้อ "การส่งอีเมลในนามท่าน" ต้องถูกฟ้อง · ฉบับที่มีครบ (แม้เลขข้อขยับ) ต้องไม่ถูกฟ้อง</summary>
public class LegalDocumentClauseDiffTests
{
    private const string Published = """
        ## 1. บัญชีผู้ใช้และความปลอดภัย
        - ท่านรับผิดชอบต่อกิจกรรมทั้งหมด
        ## 2. บทบาทของแต่ละฝ่ายต่อข้อมูลในระบบ
        - **ข้อมูลบัญชีผู้ใช้ของท่าน** (ชื่อ อีเมล) — เราเป็นผู้ควบคุม
        - **ข้อมูลที่ท่านนำเข้าระบบ** — ท่านเป็นผู้ควบคุม
        ## 3. ความถูกต้องของข้อมูลบัญชีและภาษี
        """;

    [Fact]
    public void ฉบับตั้งต้นจริง_มีข้อส่งอีเมลในนามท่าน_ฉบับเผยแพร่เก่าไม่มี_ต้องฟ้อง()
    {
        var builtIn = LegalDocumentDefaults.PlatformBuiltIn(LegalDocumentDefaults.KindTerms);
        var missing = LegalDocumentClauseDiff.MissingFromPublished(builtIn, Published);
        Assert.Contains("การส่งอีเมลในนามท่าน", missing);
        Assert.DoesNotContain("บทบาทของแต่ละฝ่ายต่อข้อมูลในระบบ", missing);   // ข้อที่มีอยู่แล้วไม่ถูกฟ้อง
        Assert.DoesNotContain("ข้อมูลบัญชีผู้ใช้ของท่าน", missing);
    }

    [Fact]
    public void ฉบับเผยแพร่มีครบแม้เลขข้อขยับ_ไม่ฟ้อง()
    {
        var builtIn = "## 2. บทบาท\n- **การส่งอีเมลในนามท่าน** — ข้อความ\n## 3. ภาษี\n";
        var published = "## 5) บทบาท\n- **การส่งอีเมลในนามท่าน** — เรียบเรียงใหม่\n## 7. ภาษี\n## 8. ข้อเพิ่มของแอดมิน\n";
        Assert.Empty(LegalDocumentClauseDiff.MissingFromPublished(builtIn, published));
    }

    [Fact]
    public void ยังไม่เคยเผยแพร่_ใช้ฉบับตั้งต้นอยู่_ไม่ฟ้อง()
    {
        var builtIn = LegalDocumentDefaults.PlatformBuiltIn(LegalDocumentDefaults.KindTerms);
        Assert.Empty(LegalDocumentClauseDiff.MissingFromPublished(builtIn, null));
        Assert.Empty(LegalDocumentClauseDiff.MissingFromPublished(builtIn, "  "));
    }

    [Fact]
    public void ฉบับเผยแพร่เท่าฉบับตั้งต้น_ไม่ฟ้อง_ทุกชนิด()
    {
        foreach (var kind in LegalDocumentDefaults.Kinds)
        {
            var b = LegalDocumentDefaults.PlatformBuiltIn(kind);
            Assert.Empty(LegalDocumentClauseDiff.MissingFromPublished(b, b));
            Assert.NotEmpty(LegalDocumentClauseDiff.Clauses(b));
        }
    }

    [Theory]
    [InlineData("2. บทบาทของแต่ละฝ่าย", "บทบาทของแต่ละฝ่าย")]
    [InlineData("**การส่งอีเมลในนามท่าน**", "การส่งอีเมลในนามท่าน")]
    [InlineData("10.1  ข้อย่อย   ยาว:", "ข้อย่อย ยาว")]
    [InlineData("[นโยบาย](/privacy.html) ของเรา", "นโยบาย ของเรา")]
    public void Normalize_ตัดเลขข้อและรูปแบบ(string raw, string expected)
        => Assert.Equal(expected, LegalDocumentClauseDiff.Normalize(raw));
}
