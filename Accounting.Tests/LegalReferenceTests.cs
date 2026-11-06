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
            Assert.Contains("พ.ร.บ.การบัญชี 2543 ม.10", r);
            Assert.DoesNotContain("ม.11", r);
        }
        var msg = OpeningRoll.FiscalYearStartMonthLockedMessage(1, 4, new[] { 2024 });
        Assert.Contains("ม.10", msg);
        Assert.DoesNotContain("อธิบดี", msg);   // ฝ่ายค้าน B: ผู้อนุญาตเปลี่ยนรอบไม่ใช่อธิบดี (สารวัตรใหญ่บัญชี/สารวัตรบัญชี)
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
        var th = LegalDocumentDefaults.PlatformBuiltIn(kind);
        var en = LegalDocumentDefaults.PlatformBuiltInEn(kind);
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
        var th = LegalDocumentDefaults.PlatformBuiltIn(LegalDocumentDefaults.KindTerms);
        var en = LegalDocumentDefaults.PlatformBuiltInEn(LegalDocumentDefaults.KindTerms);
        Assert.Contains("ม.19", th);
        Assert.Contains("ม.7(6)", th);
        Assert.DoesNotContain("พ.ศ. 2543 ม.7 ", th);
        Assert.Contains("Section 19", en);
        Assert.Contains("การส่งอีเมลในนามท่าน", th);                       // Y6: ข้อเดิม (รอบ 240) ยังอยู่
        Assert.Contains("การแลกเปลี่ยนเอกสารกับคู่ค้าที่ใช้บริการนี้", th);   // N3: ร่างเดียวกัน
        Assert.Contains("**ร่าง**", th);
        Assert.Contains("Exchanging documents with counterparties", en);
        Assert.Contains("การส่งเอกสารและการแจ้งเตือนผ่าน LINE ในนามท่าน", th);   // LINE_OA_TERMS_CHECK C9 — ร่างเดียวกัน เผยแพร่ครั้งเดียว
        Assert.Contains("via LINE on your behalf", en);
    }

    [Fact]
    public void ร้านค้า_แม่แบบนโยบาย_อ้างม14()
    {
        Assert.Contains("พ.ร.บ.การบัญชี ม.14", LegalDocumentDefaults.ShopTemplate(LegalDocumentDefaults.KindPrivacy));
        Assert.Contains("Section 14 of the Accounting Act", LegalDocumentDefaults.ShopTemplateEn(LegalDocumentDefaults.KindPrivacy));
    }
}
