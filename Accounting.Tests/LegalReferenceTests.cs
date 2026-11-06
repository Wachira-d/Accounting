using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 250 WP-06 (RETENTION_LAW_CHECK §2 + ฝ่ายค้าน GO ทั้ง A–D) — ล็อกเลขมาตรา พ.ร.บ.การบัญชี 2543 ที่ตัวตัดสินอ้างใน audit/ข้อความถึงผู้ใช้:
/// ระยะเก็บ = <b>ม.14</b> (ไม่ใช่ ม.10 · ไม่ใช่ ท.ป.4/2528 ซึ่งเป็นคำสั่งอัตราหัก ณ ที่จ่าย) · ปิดบัญชี/รอบบัญชี = <b>ม.10</b> (ไม่ใช่ ม.11 ซึ่งเป็นการจัดทำ/ยื่นงบ) ·
/// ผู้ทำบัญชี = <b>ม.19</b> (คุณสมบัติตาม ม.7(6)) · ทิศตรงข้าม: รหัสกฎที่ persist ใน audit แล้ว (<c>ACC-ACT-11-FISCAL-PERIOD</c>) ไม่ถูกเปลี่ยนชื่อ
/// และ audit row เก่าไม่ถูก migrate (กฎ M — append-only + hash chain) · การตรวจข้อความทั้งเรพอยู่ที่ <c>tools/legal_citation_check.py</c></summary>
public class LegalReferenceTests
{
    [Fact]
    public void ระยะเก็บ_อ้างม14_ไม่อ้างม10_หรือทป4()
    {
        Assert.Contains("พ.ร.บ.การบัญชี ม.14", TrialDataRetentionPolicy.LegalReference);
        Assert.DoesNotContain("ม.10", TrialDataRetentionPolicy.LegalReference);
        Assert.DoesNotContain("ท.ป.4/2528", TrialDataRetentionPolicy.LegalReference);
        var all = new TrialDataRetentionPolicy.LegalHoldFacts(true, true, true, true, true, true).Describe();
        Assert.Contains("ม.14", all);
        Assert.DoesNotContain("ม.10", all);
        Assert.DoesNotContain("ท.ป.4/2528", all);
        Assert.Contains("50 ทวิ", all);   // ทิศตรงข้าม: เหตุ legal hold ยังครบ (ไม่ได้ถอดเหตุออกพร้อมเลขมาตรา)
    }

    [Fact]
    public void ปิดบัญชีและรอบบัญชี_อ้างม10_ไม่อ้างม11()
    {
        foreach (var r in new[] { ClosingEntryLock.LegalReference, YearEndReopenRule.LegalReference })
        {
            Assert.Contains("พ.ร.บ.การบัญชี 2543 ม.10 (ปิดบัญชี", r);   // ม.10 = ปิดบัญชี/รอบบัญชี — ระบุเรื่องเสมอ (legal_citation_check)
            Assert.DoesNotContain("ม.11", r);
        }
        var msg = OpeningRoll.FiscalYearStartMonthLockedMessage(1, 4, new[] { 2024 });
        Assert.Contains("ม.10", msg);
        // ฝ่ายค้าน B: ผู้อนุญาตตาม พ.ร.บ.การบัญชี = สารวัตรใหญ่บัญชี/สารวัตรบัญชี (ไม่ใช่อธิบดี) · ฝ่ายค้าน WP-06 MED-3: แต่การอนุมัติของ
        // อธิบดีกรมสรรพากรตาม ป.รัษฎากร ม.65 ต้องยังอยู่ (ทิศตรงข้าม — แก้ข้อหนึ่งห้ามลบอีกข้อ)
        Assert.DoesNotContain("อนุมัติจากอธิบดี (พ.ร.บ.การบัญชี", msg);
        Assert.Contains("สารวัตร", msg);
        Assert.Contains("ม.65", msg);
        Assert.Contains("อธิบดีกรมสรรพากร", msg);
    }

    [Fact]
    public void รหัสกฎที่persistแล้ว_ไม่ถูกเปลี่ยนชื่อตามเลขมาตรา()
    {
        // ทิศตรงข้าม: แก้ข้อความอ้างมาตรา ≠ เปลี่ยนรหัสที่อยู่ใน audit เดิม (ผู้ค้นประวัติใช้รหัสนี้ · ห้าม migrate แถวเก่า)
        Assert.Equal("ACC-ACT-11-FISCAL-PERIOD", OpeningRoll.FiscalYearStartMonthRuleCode);
        Assert.Equal("TRIAL-RETENTION-PURGE", TrialDataRetentionPolicy.RuleCode);
        Assert.Equal("GL-YEAR-END-CLOSING-ENTRY", ClosingEntryLock.RuleCode);
    }

    [Theory]
    [InlineData(LegalDocumentDefaults.KindPrivacy)]
    [InlineData(LegalDocumentDefaults.KindTerms)]
    public void ร่างข้อกำหนดฉบับใหม่_เลขมาตราถูกทั้งไทยและอังกฤษ(string kind)
    {
        var th = LegalDocumentDefaults.PlatformNextDraft(kind);
        var en = LegalDocumentDefaults.PlatformNextDraftEn(kind);
        Assert.Contains("พ.ร.บ.การบัญชี ม.14", th);
        Assert.Contains("Section 14", en);
        Assert.DoesNotContain("พ.ร.บ.การบัญชี ม.10", th);
        Assert.DoesNotContain("Accounting Act, Section 10", en);
        Assert.DoesNotContain("Section 10 of the Accounting Act", en);
        Assert.DoesNotContain("รัฐมนตรี", th);   // ฝ่ายค้าน A: เงื่อนไข "ความเห็นชอบของรัฐมนตรี" ยังไม่ยืนยัน — ห้ามใส่
    }

    [Fact]
    public void ข้อกำหนด_ผู้ทำบัญชีอ้างม19_และมีข้อแลกเปลี่ยนข้ามผู้เช่า()
    {
        var th = LegalDocumentDefaults.PlatformNextDraft(LegalDocumentDefaults.KindTerms);
        var en = LegalDocumentDefaults.PlatformNextDraftEn(LegalDocumentDefaults.KindTerms);
        Assert.Contains("ม.19", th);
        Assert.Contains("ม.7(6)", th);
        Assert.DoesNotContain("พ.ศ. 2543 ม.7 ", th);
        Assert.Contains("Section 19", en);
        Assert.Contains("การส่งอีเมลในนามท่าน", th);                       // Y6: ข้อเดิม (รอบ 240) ยังอยู่
        Assert.Contains("การแลกเปลี่ยนเอกสารกับคู่ค้าที่ใช้บริการนี้", th);   // N3: ร่างเดียวกัน
        Assert.Contains("**ร่าง**", th);
        Assert.Contains("ระบบไม่ลงภาษีซื้อแทนผู้รับ", th);              // ฝ่ายค้าน HIGH-1: ไม่อ้างฟีเจอร์ที่ยังไม่มี
        Assert.Contains("ประทับเวลาโดย สพธอ.", th);
        Assert.Contains("does not claim input tax on the recipient's behalf", en);
        Assert.Contains("Exchanging documents with counterparties", en);
        Assert.Contains("การส่งเอกสารและการแจ้งเตือนผ่าน LINE ในนามท่าน", th);   // LINE_OA_TERMS_CHECK C9 — ร่างเดียวกัน เผยแพร่ครั้งเดียว
        Assert.Contains("via LINE on your behalf", en);
    }

    // ───── ฝ่ายค้าน WP-06 HIGH-1: ข้อความฉบับ 1.0 ในโค้ดถูกเสิร์ฟสด (ไม่มีฉบับเผยแพร่ ⇒ GetCurrent/GetPublishedVersion("1.0") = built-in) ⇒ แช่แข็ง ─────
    // แก้ข้อความตั้งต้นต้องเปลี่ยนเลขฉบับ (ผู้ที่ยอมรับ "1.0" ต้องชี้ข้อความเดิมเสมอ) — ข้อความใหม่ไปที่ PlatformNextDraft แล้วแอดมินเผยแพร่เป็นฉบับใหม่
    private static string Sha(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n")))).ToLowerInvariant();

    [Theory]
    [InlineData(LegalDocumentDefaults.KindPrivacy, false, "06230ab8c4d5abf87d8f216820d778d72812a20a5506a0819404e31d645feee1")]
    [InlineData(LegalDocumentDefaults.KindTerms, false, "aac3b4230cc9fc49161d0b4f1174fa99b6c3d8f6200fafdd5fd94bcf8991f41d")]
    [InlineData(LegalDocumentDefaults.KindPrivacy, true, "0f4bf1f692eaf0769771a0a7c5821387d01f3918a466d6a93f4e770dbc8b71f3")]
    [InlineData(LegalDocumentDefaults.KindTerms, true, "4aa89d1f117524a677de1254851da0f27162c83609cecd2c5c0bb6d9f1a72cb8")]
    public void ข้อความฉบับ10ในโค้ด_แช่แข็ง_hash(string kind, bool english, string sha)
    {
        Assert.Equal("1.0", LegalDocumentDefaults.BuiltInVersion);
        var text = english ? LegalDocumentDefaults.PlatformBuiltInEn(kind) : LegalDocumentDefaults.PlatformBuiltIn(kind);
        Assert.Equal(sha, Sha(text));   // ล้ม = มีคนแก้ข้อความ 1.0 — ย้อนกลับ แล้วแก้ที่ PlatformNextDraft แทน
    }

    [Fact]
    public void ร่างฉบับถัดไป_ไม่เสิร์ฟสด_และแบนเนอร์ตัดสินจากเซิร์ฟเวอร์()
    {
        foreach (var k in new[] { LegalDocumentDefaults.KindPrivacy, LegalDocumentDefaults.KindTerms })
        {
            Assert.NotEqual(LegalDocumentDefaults.PlatformBuiltIn(k), LegalDocumentDefaults.PlatformNextDraft(k));
            Assert.DoesNotContain("การแลกเปลี่ยนเอกสารกับคู่ค้าที่ใช้บริการนี้", LegalDocumentDefaults.PlatformBuiltIn(k));
            // ไม่มีฉบับเผยแพร่ (ปัจจุบัน = 1.0 ในโค้ด) ⇒ แบนเนอร์ขึ้น · เผยแพร่ร่างแล้ว (ปัจจุบัน/ประกาศล่วงหน้า = ร่าง) ⇒ ไม่ขึ้น
            Assert.True(LegalDocumentDefaults.NextDraftPending(k, LegalDocumentDefaults.PlatformBuiltIn(k), null));
            Assert.False(LegalDocumentDefaults.NextDraftPending(k, LegalDocumentDefaults.PlatformNextDraft(k).Replace("\n", "\r\n"), null));
            Assert.False(LegalDocumentDefaults.NextDraftPending(k, LegalDocumentDefaults.PlatformBuiltIn(k), LegalDocumentDefaults.PlatformNextDraft(k)));
        }
        // refund ยังไม่มีร่างใหม่ ⇒ ไม่มีแบนเนอร์เมื่อปัจจุบัน = ตั้งต้น
        Assert.False(LegalDocumentDefaults.NextDraftPending(LegalDocumentDefaults.KindRefund, LegalDocumentDefaults.PlatformBuiltIn(LegalDocumentDefaults.KindRefund), null));
        // เงื่อนไขก่อนเผยแพร่: ข้อกำหนดผูกกับ WP-12 + C5 (ข้อความบนแบนเนอร์มาจากที่นี่ที่เดียว) · นโยบายความเป็นส่วนตัวไม่มีเงื่อนไข
        var gate = LegalDocumentDefaults.NextDraftPublishGate(LegalDocumentDefaults.KindTerms);
        Assert.NotNull(gate);
        Assert.Contains("WP-12", gate);
        Assert.Contains("C5", gate);
        Assert.Null(LegalDocumentDefaults.NextDraftPublishGate(LegalDocumentDefaults.KindPrivacy));
    }

    [Theory]
    [InlineData(LegalDocumentDefaults.KindPrivacy)]
    [InlineData(LegalDocumentDefaults.KindTerms)]
    public void ร่างฉบับถัดไป_ไทยอังกฤษโครงตรงกัน(string kind)
    {
        var th = LegalDocumentDefaults.PlatformNextDraft(kind);
        var en = LegalDocumentDefaults.PlatformNextDraftEn(kind);
        static int Count(string s, string p) => System.Text.RegularExpressions.Regex.Matches(s, p, System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        Assert.Equal(Count(th, @"^#+ "), Count(en, @"^#+ "));
        Assert.Equal(Count(th, @"^\|"), Count(en, @"^\|"));
        Assert.Equal(Count(th, @"^- "), Count(en, @"^- "));
        Assert.EndsWith("*" + LegalDocumentDefaults.EnglishPrevailClause + "*", en.TrimEnd());
    }

    [Fact]
    public void ร้านค้า_แม่แบบนโยบาย_อ้างม14()
    {
        Assert.Contains("พ.ร.บ.การบัญชี ม.14", LegalDocumentDefaults.ShopTemplate(LegalDocumentDefaults.KindPrivacy));
        Assert.Contains("Section 14 of the Accounting Act", LegalDocumentDefaults.ShopTemplateEn(LegalDocumentDefaults.KindPrivacy));
    }
}
