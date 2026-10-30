using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 214 (คำตัดสินข้อ 173) — เอกสารทางกฎหมายที่แอดมิน/เจ้าของร้านแก้ได้:
/// ① ตัวแปลง markdown ชุดย่อย (<see cref="LegalMarkdown"/>) ต้องไม่มีทางให้ข้อความผู้ใช้กลายเป็นแท็ก/สคริปต์/ลิงก์ javascript:
/// ② ตัวตัดสินเวอร์ชัน (<see cref="LegalDocumentPolicy"/>) — ฉบับปัจจุบัน/ประกาศล่วงหน้า · เผยแพร่แล้วแก้ไม่ได้ · เลขฉบับเดินหน้าเท่านั้น
/// ③ ตัวตัดสินลิงก์ของร้าน (<see cref="CmsLegalPages"/>) — ลิงก์ของร้าน &gt; ข้อความที่ร้านเขียน &gt; แม่แบบ
/// ④ แม่แบบของร้านแทนตัวตนร้านจริง · ฉบับตั้งต้นของแพลตฟอร์มไม่ลดหัวข้อจาก privacy.html/terms.html เดิม
/// </summary>
public class LegalDocumentTests
{
    // ═══════════════ ① LegalMarkdown ═══════════════

    [Fact]
    public void Render_BasicBlocks()
    {
        var html = LegalMarkdown.Render("## หัวข้อ\n\nย่อหน้า **หนา** และ *เอียง* `code`\n\n- ก\n- ข\n\n1. หนึ่ง\n2. สอง\n\n> หมายเหตุ");
        Assert.Contains("<h2>หัวข้อ</h2>", html);
        Assert.Contains("<p>ย่อหน้า <b>หนา</b> และ <i>เอียง</i> <code>code</code></p>", html);
        Assert.Contains("<ul><li>ก</li><li>ข</li></ul>", html);
        Assert.Contains("<ol><li>หนึ่ง</li><li>สอง</li></ol>", html);
        Assert.Contains("<div class=\"legal-note\">หมายเหตุ</div>", html);
    }

    [Fact]
    public void Render_Table_WithHeaderAndPaddedRows()
    {
        var html = LegalMarkdown.Render("| ก | ข |\n|---|---|\n| 1 | 2 |\n| 3 |");
        Assert.Contains("<thead><tr><th>ก</th><th>ข</th></tr></thead>", html);
        Assert.Contains("<tr><td>1</td><td>2</td></tr>", html);
        Assert.Contains("<tr><td>3</td><td></td></tr>", html);   // แถวสั้นเติมช่องว่างให้ครบหัวตาราง
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("\"><svg onload=alert(1)>")]
    [InlineData("## <b onclick=alert(1)>x</b>")]
    [InlineData("| <script>x</script> | a |\n|---|---|\n| <iframe> | b |")]
    public void Render_RawHtml_IsEncoded_NeverATag(string md)
    {
        var html = LegalMarkdown.Render(md);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;", html);
    }

    [Theory]
    [InlineData("[คลิก](javascript:alert(1))")]
    [InlineData("[คลิก](JAVASCRIPT:alert(1))")]
    [InlineData("[คลิก](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[คลิก](vbscript:msgbox)")]
    [InlineData("[คลิก](//evil.example/x)")]
    [InlineData("[คลิก](&#106;avascript:alert(1))")]
    [InlineData("[คลิก](java\tscript:alert(1))")]
    public void Render_DangerousLinks_AreNotAnchors(string md)
    {
        var html = LegalMarkdown.Render(md);
        Assert.DoesNotContain("<a ", html);
        Assert.Contains("คลิก", html);
    }

    [Theory]
    [InlineData("[เว็บ](https://example.com/a?b=1&c=2)", "href=\"https://example.com/a?b=1&amp;c=2\"")]
    [InlineData("[อีเมล](mailto:dpo@example.com)", "href=\"mailto:dpo@example.com\"")]
    [InlineData("[โทร](tel:021234567)", "href=\"tel:021234567\"")]
    [InlineData("[ข้อกำหนด](/terms.html)", "href=\"/terms.html\"")]
    [InlineData("[นโยบาย](privacy)", "href=\"privacy\"")]
    public void Render_AllowedLinks(string md, string expectedHref)
    {
        var html = LegalMarkdown.Render(md);
        Assert.Contains(expectedHref, html);
    }

    [Fact]
    public void Render_ExternalLink_OpensNewTabWithNoopener()
    {
        var html = LegalMarkdown.Render("[x](https://example.com)");
        Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", html);
        Assert.DoesNotContain("target=", LegalMarkdown.Render("[x](/privacy.html)"));
    }

    [Fact]
    public void Tokens_AreEncoded_AndCannotInjectMarkupOrMarkdown()
    {
        var tokens = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["company.name"] = "<script>alert('x')</script> **ไม่หนา** [x](javascript:alert(1))",
            ["controller.email"] = "a\"b@example.com",
        };
        var html = LegalMarkdown.Render("ร้าน {{company.name}} · [{{controller.email}}](mailto:{{controller.email}})", tokens);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<b>ไม่หนา</b>", html);         // ค่าโทเคนไม่ถูกตีความเป็น markdown
        Assert.DoesNotContain("href=\"javascript", html);
        Assert.Contains("href=\"mailto:a&quot;b@example.com\"", html);   // อัญประกาศในค่าไม่หลุด attribute
    }

    [Fact]
    public void Tokens_Unknown_StayVerbatim_KnownNull_BecomesEmpty()
    {
        var tokens = new Dictionary<string, string?> { ["company.name"] = null };
        var html = LegalMarkdown.Render("{{company.name}}|{{unknown.token}}", tokens);
        Assert.Contains("|{{unknown.token}}", html);
        Assert.DoesNotContain("{{company.name}}", html);
    }

    [Fact]
    public void StashMarker_FromUserText_CannotForgeLinks()
    {
        var html = LegalMarkdown.Render("a\u00010\u0001b [x](https://ok.example)");
        Assert.DoesNotContain("\u0001", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<a "));
    }

    [Fact]
    public void Warnings_FlagBadLinks_Html_UnknownTokens_Length()
    {
        var w = LegalMarkdown.Warnings("[x](javascript:1) <b>x</b> {{nope.x}}", new[] { "company.name" });
        Assert.Contains(w, x => x.Contains("javascript:1"));
        Assert.Contains(w, x => x.Contains("HTML"));
        Assert.Contains(w, x => x.Contains("nope.x"));
        Assert.Contains(LegalMarkdown.Warnings(new string('ก', LegalMarkdown.MaxLength + 1)), x => x.Contains("ยาวเกิน"));
        Assert.Empty(LegalMarkdown.Warnings("## ปกติ\n[x](https://a.example) {{company.name}}", new[] { "company.name" }));
    }

    [Fact]
    public void Render_EmptyOrNull_IsEmpty()
    {
        Assert.Equal("", LegalMarkdown.Render(null));
        Assert.Equal("", LegalMarkdown.Render("   \n  "));
    }

    // ═══════════════ ② LegalDocumentPolicy ═══════════════

    private static LegalDocumentPolicy.VersionRow Pub(string v, int y, int m, int d, int pubDay = 1)
        => new(v, new DateOnly(y, m, d), LegalDocumentPolicy.StatusPublished, new DateTime(2026, 9, pubDay));

    private static readonly DateOnly Today = new(2026, 10, 30);

    [Fact]
    public void PickCurrent_NoRows_IsNull_UseBuiltIn()
        => Assert.Null(LegalDocumentPolicy.PickCurrent(Array.Empty<LegalDocumentPolicy.VersionRow>(), Today));

    [Fact]
    public void PickCurrent_LatestEffectiveNotInFuture_DraftsIgnored()
    {
        var rows = new[]
        {
            Pub("1.1", 2026, 9, 1),
            Pub("1.2", 2026, 10, 30),                  // มีผลวันนี้ = ปัจจุบัน
            Pub("2.0", 2026, 12, 1),                   // ประกาศล่วงหน้า
            new LegalDocumentPolicy.VersionRow("9.9", new DateOnly(2026, 10, 1), LegalDocumentPolicy.StatusDraft, null),
        };
        Assert.Equal("1.2", LegalDocumentPolicy.PickCurrent(rows, Today)!.Version);
        Assert.Equal("2.0", LegalDocumentPolicy.PickUpcoming(rows, Today)!.Version);
        Assert.Null(LegalDocumentPolicy.PickUpcoming(rows.Take(2), Today));
    }

    [Fact]
    public void PickCurrent_SameEffectiveDate_LaterPublishWins()
    {
        var rows = new[] { Pub("1.1", 2026, 10, 1, pubDay: 1), Pub("1.2", 2026, 10, 1, pubDay: 5) };
        Assert.Equal("1.2", LegalDocumentPolicy.PickCurrent(rows, Today)!.Version);
    }

    [Theory]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("2", "2.0", 0)]
    [InlineData("1.0.1", "1.0", 1)]
    [InlineData("1.0", "1.1", -1)]
    public void CompareVersions_Numeric(string a, string b, int sign)
        => Assert.Equal(sign, Math.Sign(LegalDocumentPolicy.CompareVersions(a, b)));

    [Fact]
    public void ValidatePublish_Rules()
    {
        var published = new List<LegalDocumentPolicy.VersionRow> { Pub("1.1", 2026, 10, 1) };
        // ผ่าน
        Assert.Null(LegalDocumentPolicy.ValidatePublish("1.2", Today, "## x", published, Today));
        Assert.Null(LegalDocumentPolicy.ValidatePublish("2.0", Today.AddDays(30), "## x", published, Today));   // ประกาศล่วงหน้า
        // เลขฉบับไม่เดินหน้า / ซ้ำ / ซ้ำกับฉบับตั้งต้น
        Assert.NotNull(LegalDocumentPolicy.ValidatePublish("1.1", Today, "## x", published, Today));
        Assert.NotNull(LegalDocumentPolicy.ValidatePublish("1.0", Today, "## x", new List<LegalDocumentPolicy.VersionRow>(), Today));
        Assert.NotNull(LegalDocumentPolicy.ValidatePublish("0.9", Today, "## x", new List<LegalDocumentPolicy.VersionRow>(), Today));
        // วันมีผลย้อนหลัง
        Assert.NotNull(LegalDocumentPolicy.ValidatePublish("1.2", Today.AddDays(-1), "## x", published, Today));
        // เนื้อความว่าง / เลขฉบับรูปแบบผิด
        Assert.NotNull(LegalDocumentPolicy.ValidatePublish("1.2", Today, "  ", published, Today));
        Assert.NotNull(LegalDocumentPolicy.ValidatePublish("v1.2", Today, "## x", published, Today));
        Assert.NotNull(LegalDocumentPolicy.ValidatePublish(null, Today, "## x", published, Today));
    }

    [Fact]
    public void ValidatePublish_EffectiveBeforeLatestAnnounced_Rejected()
    {
        // มีฉบับ 2.0 ประกาศล่วงหน้า (มีผล 1 ธ.ค.) ⇒ ฉบับ 2.1 ที่มีผลก่อนหน้านั้นไม่ได้ (ลำดับวันมีผลต้องเดินหน้าตามเลข)
        var published = new List<LegalDocumentPolicy.VersionRow> { Pub("2.0", 2026, 12, 1) };
        Assert.NotNull(LegalDocumentPolicy.ValidatePublish("2.1", Today, "## x", published, Today));
        Assert.Null(LegalDocumentPolicy.ValidatePublish("2.1", new DateOnly(2026, 12, 1), "## x", published, Today));
    }

    [Fact]
    public void PublishedRows_AreNotEditable_DraftsAre()
    {
        Assert.False(LegalDocumentPolicy.CanEdit(LegalDocumentPolicy.StatusPublished));
        Assert.True(LegalDocumentPolicy.CanEdit(LegalDocumentPolicy.StatusDraft));
    }

    [Fact]
    public void ConsentVersion_SameVersions_StaysBackwardCompatible()
    {
        Assert.Equal("1.0", LegalDocumentPolicy.ConsentVersion("1.0", "1.0"));   // แถวยินยอมก่อนรอบ 214 = "1.0"
        Assert.Equal("privacy 1.1 · terms 1.0", LegalDocumentPolicy.ConsentVersion("1.1", "1.0"));
        Assert.True("privacy 9999.9999.9999 · terms 9999.9999.9999".Length <= 50);   // PdpaConsentRecords.PolicyVersion varchar(50)
    }

    [Fact]
    public void NeedsAcknowledge_OnlyWhenNoRecordOrDifferentVersion()
    {
        Assert.True(LegalDocumentPolicy.NeedsAcknowledge(null, "1.0"));
        Assert.True(LegalDocumentPolicy.NeedsAcknowledge("1.0", "privacy 1.1 · terms 1.0"));
        Assert.False(LegalDocumentPolicy.NeedsAcknowledge("1.0", "1.0"));      // ผู้ใช้เดิม + ยังไม่มีฉบับใหม่ = ไม่รบกวน
    }

    [Fact]
    public void TodayBangkok_UsesThaiCalendarDay()
    {
        Assert.Equal(new DateOnly(2026, 10, 31), LegalDocumentPolicy.TodayBangkok(new DateTime(2026, 10, 30, 17, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateOnly(2026, 10, 30), LegalDocumentPolicy.TodayBangkok(new DateTime(2026, 10, 30, 16, 59, 0, DateTimeKind.Utc)));
    }

    // ═══════════════ ③ CmsLegalPages ═══════════════

    [Fact]
    public void Resolve_Precedence_External_Then_Custom_Then_Default()
    {
        Assert.Equal(CmsLegalPages.ModeExternal, CmsLegalPages.Resolve("privacy", "https://shop.example/privacy", "## เขียนเอง").Mode);
        Assert.Equal(CmsLegalPages.ModeCustom, CmsLegalPages.Resolve("privacy", null, "## เขียนเอง").Mode);
        Assert.Equal(CmsLegalPages.ModeCustom, CmsLegalPages.Resolve("privacy", "  ", "## เขียนเอง").Mode);
        Assert.Equal(CmsLegalPages.ModeDefault, CmsLegalPages.Resolve("terms", null, null).Mode);
        Assert.Equal(CmsLegalPages.ModeDefault, CmsLegalPages.Resolve("terms", "", "  ").Mode);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example")]
    [InlineData("https://x.example/\"onmouseover=alert(1)")]
    public void Resolve_UnsafeExternal_TreatedAsNotSet(string url)
    {
        var l = CmsLegalPages.Resolve("privacy", url, null);
        Assert.Equal(CmsLegalPages.ModeDefault, l.Mode);
        Assert.Null(l.ExternalUrl);
        Assert.NotNull(CmsLegalPages.ValidateExternalUrl(url));
    }

    [Fact]
    public void Resolve_SitePath_IsShopsOwnPage()
    {
        var l = CmsLegalPages.Resolve("privacy", "/privacy", null);   // หน้า CMS ที่ร้านทำไว้เอง (ค่าเดิมก่อนรอบ 214)
        Assert.Equal(CmsLegalPages.ModeExternal, l.Mode);
        Assert.Equal("/privacy", l.ExternalUrl);
        Assert.Null(CmsLegalPages.ValidateExternalUrl("/privacy"));
        Assert.Null(CmsLegalPages.ValidateExternalUrl(""));
    }

    [Fact]
    public void Slugs_LiveUnderLegal_NoClashWithShopPages()
    {
        Assert.Equal("legal/privacy", CmsLegalPages.Slug("privacy"));
        Assert.Equal("legal/terms", CmsLegalPages.Slug("terms"));
        Assert.Equal("legal/privacy", CmsLegalPages.Resolve("privacy", null, null).Slug);
    }

    [Fact]
    public void EffectiveMarkdown_CustomOrTemplate()
    {
        Assert.Equal("## ของร้าน", CmsLegalPages.EffectiveMarkdown("privacy", "## ของร้าน"));
        Assert.Equal(LegalDocumentDefaults.ShopTemplate("privacy"), CmsLegalPages.EffectiveMarkdown("privacy", null));
        Assert.Equal(LegalDocumentDefaults.ShopTemplate("terms"), CmsLegalPages.EffectiveMarkdown("terms", " "));
    }

    // ═══════════════ ④ แม่แบบ / ฉบับตั้งต้น ═══════════════

    [Theory]
    [InlineData("privacy")]
    [InlineData("terms")]
    public void ShopTemplate_ContainsShopIdentity_AndProcessorStatement(string kind)
    {
        var tokens = CmsContentTokens.BuildValues("ร้านกาแฟดอยช้าง จำกัด", null, "02-111-2222", "hello@doi.example",
            "99 ถ.สุขุมวิท กรุงเทพฯ 10110", "0105551234567", null, siteName: "Doi Cafe");
        var html = LegalMarkdown.Render(LegalDocumentDefaults.ShopTemplate(kind), CmsLegalPages.BuildShopTokens(tokens, isJuristic: true));
        Assert.Contains("ร้านกาแฟดอยช้าง จำกัด", html);
        Assert.Contains("99 ถ.สุขุมวิท กรุงเทพฯ 10110", html);
        Assert.Contains("0105551234567", html);
        Assert.Contains("href=\"mailto:hello@doi.example\"", html);
        Assert.Contains("02-111-2222", html);
        Assert.Contains("Doi Cafe", html);
        Assert.DoesNotContain("{{", html);   // ทุกโทเคนในแม่แบบเป็นโทเคนที่รู้จัก
        Assert.Empty(LegalMarkdown.Warnings(LegalDocumentDefaults.ShopTemplate(kind), CmsContentTokens.Supported.Select(t => t.Token)));
    }

    [Fact]
    public void ShopPrivacyTemplate_StatesControllerAndProcessorRoles_AndLineAndPayment()
    {
        var md = LegalDocumentDefaults.ShopTemplate("privacy");
        Assert.Contains("ผู้ควบคุมข้อมูลส่วนบุคคล", md);
        Assert.Contains("ผู้ประมวลผลข้อมูล", md);
        Assert.Contains("LINE", md);
        Assert.Contains("ชำระเงิน", md);
        Assert.Contains("การจอง", md);
        Assert.Contains("สมาชิก", md);
    }

    [Theory]
    [InlineData("privacy", 12)]
    [InlineData("terms", 12)]
    public void PlatformBuiltIn_KeepsAllSectionsOfOldPages(string kind, int sections)
    {
        var md = LegalDocumentDefaults.PlatformBuiltIn(kind);
        for (var i = 1; i <= sections; i++) Assert.Contains($"## {i}. ", md);
        var html = LegalMarkdown.Render(md, LegalDocumentDefaults.BuildPlatformTokens("บริษัท เน็กซ์แอค จำกัด", "0105500000001", "กรุงเทพฯ", "02-000-0000", "dpo@nextacc.example", "NextAcc"));
        Assert.Contains("บริษัท เน็กซ์แอค จำกัด", html);
        Assert.Contains("href=\"mailto:dpo@nextacc.example\"", html);
        Assert.DoesNotContain("{{", html);
        Assert.Empty(LegalMarkdown.Warnings(md, LegalDocumentDefaults.PlatformTokens.Select(t => t.Token)));
    }

    [Fact]
    public void PlatformTokens_Missing_ShowExplicitNotSet_NotBlank()
    {
        var t = LegalDocumentDefaults.BuildPlatformTokens(null, null, null, null, null, null);
        Assert.Contains("ยังไม่ได้ตั้งค่า", t["controller.name"]);
        Assert.Contains("ยังไม่ได้ตั้งค่า", t["controller.email"]);
    }

    [Fact]
    public void BuiltInVersion_MatchesLegacyConsentVersion()
    {
        Assert.Equal(PdpaPolicy.CurrentVersion, LegalDocumentDefaults.BuiltInVersion);
        Assert.Equal("1.0", LegalDocumentDefaults.BuiltInVersion);
        Assert.Contains(PdpaPolicy.SignupPurpose, PdpaPolicy.TermsAcceptancePurposes);
        Assert.Contains(PdpaPolicy.PolicyUpdatePurpose, PdpaPolicy.TermsAcceptancePurposes);
    }

    // ═══════════════ ฝ่ายค้านรอบ 214 ═══════════════

    public static IEnumerable<object[]> Pathological() => new[]
    {
        new object[] { "italic", string.Concat(Enumerable.Repeat("*a ", 33_000)) },
        new object[] { "bold", string.Concat(Enumerable.Repeat("**a ", 25_000)) },
        new object[] { "link-open", new string('[', 100_000) },
        new object[] { "link-half", string.Concat(Enumerable.Repeat("[x](", 25_000)) },
        new object[] { "tags", string.Concat(Enumerable.Repeat("<a", 50_000)) },
        new object[] { "tokens", string.Concat(Enumerable.Repeat("{{ ", 33_000)) },
        new object[] { "heading", "## " + new string(' ', 50_000) + "x" + new string('#', 40_000) },
        new object[] { "paragraphs", string.Join("\n\n", Enumerable.Repeat(string.Concat(Enumerable.Repeat("*a ", 1_300)), 25)) },
        new object[] { "table", "| a |\n|---|\n" + string.Concat(Enumerable.Repeat("| *a [b ", 12_000)) },
    };

    /// <summary>S1 (ReDoS): ข้อความ 100k ที่ออกแบบให้ regex ย้อนรอย ต้องแปลง + ตรวจคำเตือนเสร็จภายใน ~1 วินาที (เดิม quadratic — หน้าร้านสาธารณะค้าง)</summary>
    [Theory]
    [MemberData(nameof(Pathological))]
    public void Render_PathologicalInput_IsFast(string name, string md)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var html = LegalMarkdown.Render(md, new Dictionary<string, string?> { ["company.name"] = "x" });
        LegalMarkdown.Warnings(md, new[] { "company.name" });
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1500, $"{name}: {sw.ElapsedMilliseconds} ms");
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void Render_OverlongParagraph_IsPlainEncoded_ShortOneStillFormatted()
    {
        var longPara = "**หนา** <i>" + new string('x', LegalMarkdown.MaxInlineLength);
        var html = LegalMarkdown.Render(longPara);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;i&gt;", html);
        Assert.Contains("<b>หนา</b>", LegalMarkdown.Render("**หนา** สั้น"));   // ทิศตรงข้าม: ย่อหน้าปกติยังแปลง
    }

    [Fact]
    public void Heading_TrailingHashes_Stripped()
        => Assert.Contains("<h2>หัวข้อ</h2>", LegalMarkdown.Render("## หัวข้อ ##"));

    /// <summary>S4: อีเมลยังไม่ได้ตั้ง ⇒ ไม่มีลิงก์ "mailto:" เสีย — แสดงข้อความแทน · ทิศตรงข้าม: อีเมลจริงยังเป็นลิงก์</summary>
    [Fact]
    public void MissingEmail_NoBrokenMailto_RealEmailStillLinked()
    {
        var md = "ติดต่อ [{{company.email}}](mailto:{{company.email}})";
        var missing = LegalMarkdown.Render(md, CmsLegalPages.BuildShopTokens(new Dictionary<string, string?>(), isJuristic: true));
        Assert.DoesNotContain("mailto:", missing);
        Assert.Contains(CmsLegalPages.NotSpecified, missing);
        var real = LegalMarkdown.Render(md, CmsLegalPages.BuildShopTokens(new Dictionary<string, string?> { ["company.email"] = "a@b.example" }, true));
        Assert.Contains("href=\"mailto:a@b.example\"", real);
    }

    /// <summary>S3: บุคคลธรรมดา (ไม่มีเลขนิติบุคคล) — เลขผู้เสียภาษี = เลขบัตรประชาชน ห้ามเผยแพร่อัตโนมัติ · นิติบุคคลแสดงได้</summary>
    [Fact]
    public void ShopTokens_TaxId_OnlyForJuristic_AndMissingValuesExplicit()
    {
        var baseTokens = CmsContentTokens.BuildValues("ร้านป้าแดง", null, null, null, null, "3100500123456", null);
        var person = CmsLegalPages.BuildShopTokens(baseTokens, isJuristic: false);
        Assert.Equal(CmsLegalPages.TaxIdWithheld, person["company.taxId"]);
        var html = LegalMarkdown.Render(LegalDocumentDefaults.ShopTemplate("privacy"), person);
        Assert.DoesNotContain("3100500123456", html);
        Assert.Contains(CmsLegalPages.NotSpecified, html);              // ที่อยู่/เบอร์/อีเมลที่ยังไม่ตั้ง
        Assert.Equal("ร้านป้าแดง", person["site.name"]);                  // ชื่อเว็บว่าง ⇒ ชื่อร้าน
        var company = CmsLegalPages.BuildShopTokens(baseTokens, isJuristic: true);
        Assert.Equal("3100500123456", company["company.taxId"]);
    }

    [Fact]
    public void ShopTokens_BookingPageScope_RevealsOnlyNameAndPhone()
    {
        var t = CmsLegalPages.BuildShopTokens(
            CmsContentTokens.BuildValues("ร้านข้าวมันไก่", null, "081-000-0000", "owner@x.example", "บ้านเลขที่ 1", "0105500000001", null),
            isJuristic: true, bookingPageScope: true);
        Assert.Equal("ร้านข้าวมันไก่", t["company.name"]);
        Assert.Equal("081-000-0000", t["company.phone"]);
        Assert.Equal(CmsLegalPages.AskTheShop, t["company.address"]);
        Assert.Equal(CmsLegalPages.AskTheShop, t["company.email"]);
        Assert.Equal(CmsLegalPages.AskTheShop, t["company.taxId"]);
    }

    [Fact]
    public void IdentityGaps_And_StoredLinkWarning()
    {
        Assert.Equal(new[] { "ที่อยู่", "อีเมลติดต่อ" }, CmsLegalPages.IdentityGaps("ร้าน", " ", "02", null));
        Assert.Empty(CmsLegalPages.IdentityGaps("ร้าน", "ที่อยู่", "02", "a@b.c"));
        Assert.Contains("javascript:alert(1)", CmsLegalPages.StoredLinkWarning("นโยบาย", "javascript:alert(1)"));
        Assert.Null(CmsLegalPages.StoredLinkWarning("นโยบาย", "https://shop.example/p"));
        Assert.Null(CmsLegalPages.StoredLinkWarning("นโยบาย", "/privacy"));
        Assert.Null(CmsLegalPages.StoredLinkWarning("นโยบาย", null));
    }

    /// <summary>หมายเหตุฝ่ายค้าน: ร่าง ≤ 100k ตัวอักษรไม่ถูกคัดลอกทั้งก้อนลง audit ทุกครั้งที่บันทึก — เก็บความยาว + hash · ช่องอื่นไม่ถูกแตะ</summary>
    [Fact]
    public void AuditRedaction_Markdown_IsDigested_OthersUntouched()
    {
        var body = new string('ก', 50_000);
        var v = AuditRedaction.Value("BodyMarkdown", body) as string;
        Assert.StartsWith("[markdown 50,000 ตัวอักษร · sha256 ", v);
        Assert.NotEqual(v, AuditRedaction.Value("PrivacyPolicyMarkdown", body + "x"));
        Assert.Equal("", AuditRedaction.Value("TermsOfServiceMarkdown", ""));
        Assert.Null(AuditRedaction.Value("TermsOfServiceMarkdown", null));
        Assert.Equal("1.1", AuditRedaction.Value("Version", "1.1"));
    }
}
