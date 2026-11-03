using System.Text.RegularExpressions;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 232 ต่อ (เจ้าของ: "แปลนโยบายเป็นภาษาอังกฤษให้เลย" · ต่อยอดระบบ 2 ภาษารอบ 232) — คำแปลอังกฤษของเนื้อความตั้งต้น
/// (<see cref="LegalDocumentDefaults.PlatformBuiltInEn"/> · <see cref="LegalDocumentDefaults.ShopTemplateEn"/>):
/// ① ทุกชนิดมีคำแปล ② โทเคน <c>{{…}}</c> ชุด/จำนวนเท่าฉบับไทย ③ ตัวเลขฉบับไทยอยู่ในฉบับอังกฤษครบ (อังกฤษเพิ่มได้เฉพาะปี ค.ศ. ในวงเล็บ)
/// ④ โครงหัวข้อ/ตาราง/รายการเท่ากัน ⑤ ประโยค "ไทยเป็นฉบับผูกพัน" ⑥ ทิศตรงข้าม: ชนิดไม่รู้จักโยนเหมือนฉบับไทย ·
/// ตัวตัดสินหน้าร้าน <see cref="CmsLegalPages.EffectiveMarkdownEn"/> + ค่าโทเคนภาษาอังกฤษ
/// </summary>
public class LegalDocumentDefaultsEnRound232Tests
{
    public static IEnumerable<object[]> Pairs()
    {
        foreach (var k in LegalDocumentDefaults.Kinds)
        {
            yield return new object[] { "platform", k };
            yield return new object[] { "shop", k };
        }
    }

    private static (string Th, string En) Get(string set, string kind) => set == "platform"
        ? (LegalDocumentDefaults.PlatformBuiltIn(kind), LegalDocumentDefaults.PlatformBuiltInEn(kind))
        : (LegalDocumentDefaults.ShopTemplate(kind), LegalDocumentDefaults.ShopTemplateEn(kind));

    private static List<string> Sorted(IEnumerable<string> xs) => xs.OrderBy(x => x, StringComparer.Ordinal).ToList();
    private static List<string> Tokens(string s) => Sorted(Regex.Matches(s, @"\{\{[^}]+\}\}").Select(m => m.Value));
    private static List<string> Numbers(string s) => Regex.Matches(s, @"\d+(?:[.,]\d+)*").Select(m => m.Value).ToList();
    private static int Count(string s, string pattern) => Regex.Matches(s, pattern, RegexOptions.Multiline).Count;

    /// <summary>ปี ค.ศ. ที่ฉบับอังกฤษเติมในวงเล็บหลังปี พ.ศ. (B.E. 2562 (2019)) — ตัวเลขเดียวที่อังกฤษมีเกินได้</summary>
    private static readonly HashSet<string> GregorianYears = new() { "2019", "2000", "1979", "1997" };

    [Theory, MemberData(nameof(Pairs))]
    public void ทุกชนิดมีคำแปล_ไม่ว่าง_และต่างจากไทย(string set, string kind)
    {
        var (th, en) = Get(set, kind);
        Assert.False(string.IsNullOrWhiteSpace(en));
        Assert.NotEqual(th, en);
        Assert.DoesNotMatch(@"[฀-๿]", en);   // ไม่มีอักษรไทยหลงในฉบับอังกฤษ
    }

    [Theory, MemberData(nameof(Pairs))]
    public void โทเคนชุดเดียวกัน_จำนวนเท่ากันทุกตำแหน่ง(string set, string kind)
    {
        var (th, en) = Get(set, kind);
        Assert.NotEmpty(Tokens(th));
        Assert.Equal(Tokens(th), Tokens(en));
    }

    [Theory, MemberData(nameof(Pairs))]
    public void ตัวเลขของฉบับไทยอยู่ในฉบับอังกฤษครบ_อังกฤษเกินได้เฉพาะปีคศ(string set, string kind)
    {
        var (th, en) = Get(set, kind);
        var remaining = Numbers(en);
        foreach (var n in Numbers(th))
            Assert.True(remaining.Remove(n), $"{set}/{kind}: ตัวเลข {n} ของฉบับไทยไม่อยู่ในฉบับอังกฤษ");
        Assert.All(remaining, n => Assert.True(GregorianYears.Contains(n), $"{set}/{kind}: ตัวเลข {n} มีเฉพาะในฉบับอังกฤษ (ไม่ใช่ปี ค.ศ. ที่เติมในวงเล็บ)"));
    }

    [Theory, MemberData(nameof(Pairs))]
    public void โครงเอกสารตรงกัน_หัวข้อ_เลขข้อ_ตาราง_รายการ(string set, string kind)
    {
        var (th, en) = Get(set, kind);
        Assert.Equal(Count(th, @"^#+ "), Count(en, @"^#+ "));
        Assert.Equal(Regex.Matches(th, @"^#+ (\d+)\.", RegexOptions.Multiline).Select(m => m.Groups[1].Value),
                     Regex.Matches(en, @"^#+ (\d+)\.", RegexOptions.Multiline).Select(m => m.Groups[1].Value));
        Assert.Equal(Count(th, @"^\|"), Count(en, @"^\|"));
        Assert.Equal(Count(th, @"^- "), Count(en, @"^- "));
        Assert.Equal(Count(th, @"^> ") + 1, Count(en, @"^> "));   // +1 = ประโยค "ไทยเป็นฉบับผูกพัน" ท้ายเอกสาร
    }

    [Theory, MemberData(nameof(Pairs))]
    public void ปิดท้ายด้วยประโยคไทยเป็นฉบับผูกพัน_และแปลงได้ไม่มีคำเตือน(string set, string kind)
    {
        var (_, en) = Get(set, kind);
        Assert.Contains(LegalDocumentDefaults.EnglishPrevailClause, en);
        Assert.Contains("Thai version prevails", en);
        Assert.EndsWith("*" + LegalDocumentDefaults.EnglishPrevailClause + "*", en.TrimEnd());
        var known = set == "platform" ? LegalDocumentDefaults.PlatformTokens.Select(t => t.Token) : CmsLegalPages.KnownTokens(kind);
        Assert.Empty(LegalMarkdown.Warnings(en, known));
    }

    [Fact]
    public void ทิศตรงข้าม_ชนิดไม่รู้จัก_โยนเหมือนฉบับไทย()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LegalDocumentDefaults.PlatformBuiltIn("cookies"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegalDocumentDefaults.PlatformBuiltInEn("cookies"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegalDocumentDefaults.ShopTemplateEn("cookies"));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegalDocumentDefaults.PlatformBuiltInEn("PRIVACY"));   // ไม่ normalize เงียบ ๆ
    }

    [Fact]
    public void ฉบับตั้งต้นแพลตฟอร์ม_แทนโทเคนอังกฤษ_ค่าที่ยังไม่ตั้งเป็นอังกฤษ()
    {
        var en = LegalDocumentDefaults.BuildPlatformTokens(null, null, null, null, null, null, english: true);
        var html = LegalMarkdown.Render(LegalDocumentDefaults.PlatformBuiltInEn("privacy"), en);
        Assert.DoesNotContain("{{", html);
        Assert.DoesNotMatch(@"[฀-๿]", html);
        Assert.Contains("not yet configured", html);
        // ทิศตรงข้าม: ค่าเริ่มต้น (ไทย) ไม่เปลี่ยน · ค่าที่ตั้งแล้วใช้ตามเดิมทั้งสองภาษา
        Assert.Equal("ผู้ให้บริการ (ยังไม่ได้ตั้งค่า)", LegalDocumentDefaults.BuildPlatformTokens(null, null, null, null, null, null)["controller.name"]);
        Assert.Equal("NextAcc Co., Ltd.", LegalDocumentDefaults.BuildPlatformTokens("NextAcc Co., Ltd.", null, null, null, null, null, english: true)["controller.name"]);
    }

    [Fact]
    public void หน้าร้าน_โหมดแม่แบบได้คำแปลแม่แบบ_ร้านเขียนเองได้เฉพาะอังกฤษของร้าน()
    {
        Assert.Equal(LegalDocumentDefaults.ShopTemplateEn("privacy"), CmsLegalPages.EffectiveMarkdownEn("privacy", null, null));
        Assert.Equal(LegalDocumentDefaults.ShopTemplateEn("refund"), CmsLegalPages.EffectiveMarkdownEn("refund", " ", "## stale"));
        Assert.Equal("## Ours", CmsLegalPages.EffectiveMarkdownEn("terms", "## ของร้าน", "## Ours"));
        // ทิศตรงข้าม: ร้านแก้ไทยเองแต่ไม่มีอังกฤษ ⇒ null (ไทย + ประกาศ) — ไม่เอาคำแปลแม่แบบไปคู่กับข้อความที่ไม่ตรงกัน
        Assert.Null(CmsLegalPages.EffectiveMarkdownEn("terms", "## ของร้าน", null));
        Assert.Null(CmsLegalPages.EffectiveMarkdownEn("terms", "## ของร้าน", "  "));
        var c = LegalDocumentLanguage.Pick(CmsLegalPages.EffectiveMarkdown("terms", "## ของร้าน"),
            CmsLegalPages.EffectiveMarkdownEn("terms", "## ของร้าน", null), "en");
        Assert.Equal(LegalDocumentLanguage.Thai, c.Language);
        Assert.Equal(LegalDocumentLanguage.EnglishUnavailableNotice, c.Notice);
    }

    [Fact]
    public void หน้าร้าน_แม่แบบคืนเงินอังกฤษ_ค่าโทเคนเป็นอังกฤษ_ตัวเลขจากค่าตั้ง()
    {
        var tokens = CmsLegalPages.BuildShopTokens(new Dictionary<string, string?> { ["company.name"] = "Baan Suan Bakery" }, isJuristic: false, english: true);
        foreach (var kv in CmsLegalPages.BuildRefundTokens(30m, 10, 5, english: true)) tokens[kv.Key] = kv.Value;
        var html = LegalMarkdown.Render(LegalDocumentDefaults.ShopTemplateEn("refund"), tokens);
        Assert.Contains("retains 30% of the deposit", html);
        Assert.Contains("10 days", html);
        Assert.Contains("5 days", html);
        Assert.DoesNotContain("{{", html);
        Assert.DoesNotMatch(@"[฀-๿]", html);
        Assert.Contains("refunds the deposit in full", CmsLegalPages.BuildRefundTokens(null, null, null, english: true)["shop.depositForfeit"]);
        Assert.Contains("booking page", CmsLegalPages.BookingPageRefundTokens(english: true)["shop.depositForfeit"]);
        Assert.Equal(CmsLegalPages.TaxIdWithheldEn, tokens["company.taxId"]);   // บุคคลธรรมดา: ปิดเลขเหมือนฉบับไทย
        // ทิศตรงข้าม: ค่าเริ่มต้นยังเป็นไทย (เลขฉบับ ShopPolicyVersion ใช้ค่าไทย — ไม่ขยับ)
        Assert.Contains("คืนมัดจำเต็มจำนวน", CmsLegalPages.BuildRefundTokens(null, null, null)["shop.depositForfeit"]);
        Assert.Equal(CmsLegalPages.NotSpecified, CmsLegalPages.BuildShopTokens(new Dictionary<string, string?>(), true)["company.email"]);
        Assert.Equal(CmsLegalPages.NotSpecifiedEn, CmsLegalPages.BuildShopTokens(new Dictionary<string, string?>(), true, english: true)["company.email"]);
    }
}
