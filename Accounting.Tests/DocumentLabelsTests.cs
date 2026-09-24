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
    public void Branch_other_renders_full_five_digit_code()
    {
        // รอบ 193 คำตัดสินเจ้าของข้อ 21: "สาขาที่ 00008" (เดิมตัดศูนย์นำ "สาขาที่ 3")
        Assert.Equal("สาขาที่ 00003", DocumentLabels.For("th").Branch("00003"));
        Assert.Equal("Branch 00012", DocumentLabels.For("en").Branch("00012"));
        Assert.Equal("สาขาที่ 00008", DocumentLabels.For("th").Branch("8"));
    }

    [Fact]
    public void Branch_labels_match_the_central_resolver_used_by_both_renderers()
    {
        // สอง renderer (HTML + QuestPDF) พิมพ์ผ่าน TaxBranchCode.LabelWithName — DocumentLabels ต้องได้คำเดียวกัน
        foreach (var code in new[] { "00000", "00001", "00008", "00123", "0" })
        {
            Assert.Equal(Accounting.Helpers.TaxBranchCode.Label(code), DocumentLabels.For("th").Branch(code));
            Assert.Equal(Accounting.Helpers.TaxBranchCode.Label(code, isEnglish: true), DocumentLabels.For("en").Branch(code));
        }
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

    // ───────────────────────────────────────────────────────────────
    //  เอกสารภาษาอังกฤษต้องไม่มีคำไทยหลุด (audit "ใช้ได้ 100% ไหม")
    // ───────────────────────────────────────────────────────────────

    /// <summary>ไทยกับอังกฤษต้องมี key ชุดเดียวกัน — ตกฝั่งไหน เอกสารภาษานั้น
    /// จะพิมพ์ "ชื่อ key" ดิบ ๆ ออกไปให้ลูกค้าเห็น (indexer คืน key เมื่อไม่พบ)</summary>
    [Fact]
    public void Thai_and_english_have_identical_key_sets()
    {
        var th = DocumentLabels.For("th").Keys.OrderBy(k => k).ToList();
        var en = DocumentLabels.For("en").Keys.OrderBy(k => k).ToList();
        Assert.Equal(th, en);
    }

    /// <summary>ทุก label ฝั่งอังกฤษต้องไม่มีอักษรไทยปน — ยกเว้นหัวเอกสารตาม
    /// §86/4 ที่ต้องคงคำไทยไว้ (พิมพ์สองภาษา ทดสอบแยกที่ LegalTitle)</summary>
    [Fact]
    public void English_labels_contain_no_thai_characters()
    {
        var en = DocumentLabels.For("en");
        foreach (var key in en.Keys)
        {
            var v = en[key];
            Assert.DoesNotContain(v, c => c >= '\u0E00' && c <= '\u0E7F');
        }
    }

    /// <summary>ไม่มี key ไหนที่ indexer คืนชื่อ key กลับมา (= ค่าหาย)</summary>
    [Theory]
    [InlineData("th")]
    [InlineData("en")]
    public void Every_key_resolves_to_real_text(string lang)
    {
        var L = DocumentLabels.For(lang);
        foreach (var key in L.Keys)
            Assert.NotEqual(key, L[key]);
    }

    /// <summary>"เครดิต N วัน" เรียงคำถูกตามภาษา (ไทยนำหน้า / อังกฤษต่อท้าย)</summary>
    [Fact]
    public void Credit_days_text_is_localised()
    {
        Assert.Equal("เครดิต 30 วัน", DocumentLabels.For("th").CreditDaysText(30));
        Assert.Equal("30 days credit", DocumentLabels.For("en").CreditDaysText(30));
    }

    /// <summary>ป้ายวันที่ตัวที่สองแปลตามชนิดเอกสารด้วย (ใบเสนอราคา/PO/PR)</summary>
    [Fact]
    public void DueDateFor_is_localised_per_document_type()
    {
        var en = DocumentLabels.For("en");
        Assert.Equal("Valid until", en.DueDateFor(Accounting.Models.Enums.DocumentType.Quotation));
        Assert.Equal("Delivery date", en.DueDateFor(Accounting.Models.Enums.DocumentType.PurchaseOrder));
        Assert.Equal("Due date", en.DueDateFor(Accounting.Models.Enums.DocumentType.Invoice));
    }

    /// <summary>หัวเอกสารโหมดอังกฤษต้องยัง "มีคำไทย" อยู่ — §86/4 บังคับ
    /// (ตัดไทยทิ้ง = ใบกำกับไม่สมบูรณ์ ผู้ซื้อเคลมภาษีซื้อไม่ได้ §82/5(1))</summary>
    [Fact]
    public void Legal_title_keeps_thai_in_english_mode()
    {
        var t = DocumentLabels.For("en").LegalTitle("ใบกำกับภาษี", "Tax Invoice");
        Assert.Contains("ใบกำกับภาษี", t);
        Assert.Contains("Tax Invoice", t);
    }

    // ───────────────────────────────────────────────────────────────
    //  คำที่ตรวจแล้วว่า "ผิด/กำกวม" — ห้ามกลับมาอีก
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// เทสต์ก่อนหน้าตรวจได้แค่ "ครบและไม่ปนภาษาไทย" — ตรวจ **ความถูกต้องของศัพท์**
    /// ไม่ได้เลย. คำต้องห้ามด้านล่างมาจากการตรวจทั้งพจนานุกรมทีละคำ แต่ละตัวมี
    /// เหตุผลกำกับว่าทำไมผิด เพื่อกันคนถัดมา (รวม AI) เผลอ "แปลตรงตัว" กลับเข้ามา
    ///
    /// <para><b>วิธีเพิ่ม</b>: เจอคำที่คู่ค้าต่างชาติทักว่าอ่านแล้วสะดุด/ตีความผิด
    /// → แก้พจนานุกรม แล้วเพิ่มคำเดิมลงตารางนี้พร้อมเหตุผล</para>
    /// </summary>
    public static IEnumerable<object[]> BannedEnglishTerms => new[]
    {
        // "bill discount" ในภาษาการเงิน = การขายลดตั๋วเงิน (discounting a bill
        // of exchange) — คนละเรื่องกับ "ส่วนลดท้ายบิล" ⇒ ใช้ "invoice discount"
        new object[] { "bill discount", "ศัพท์การเงินหมายถึงการขายลดตั๋วเงิน ไม่ใช่ส่วนลดท้ายบิล" },
        // "under-calculated" ไม่ใช่คำอังกฤษจริง ⇒ "undercharged" / "miscalculated"
        new object[] { "under-calculated", "ไม่ใช่คำอังกฤษจริง เจ้าของภาษาอ่านแล้วสะดุด" },
        new object[] { "undercalculated", "ไม่ใช่คำอังกฤษจริง" },
        // แปลตรงตัวจาก "เอกสารในระบบ" — ห้วนผิดธรรมเนียมจดหมายธุรกิจ ⇒ "Our ref."
        new object[] { "our document", "ห้วนผิดธรรมเนียมจดหมายธุรกิจ ใช้ \"Our ref.\"" },
    };

    [Theory]
    [MemberData(nameof(BannedEnglishTerms))]
    public void English_labels_avoid_terms_proven_wrong(string banned, string why)
    {
        var en = DocumentLabels.For("en");
        foreach (var key in en.Keys)
        {
            Assert.False(
                en[key].Contains(banned, StringComparison.OrdinalIgnoreCase),
                $"ป้าย \"{key}\" = \"{en[key]}\" มีคำต้องห้าม \"{banned}\" — {why}");
        }
    }

    /// <summary>เหตุผลใบลด/เพิ่มหนี้เป็น "รายการบังคับบนกระดาษ" ตาม §86/9-10 —
    /// ฝั่งอังกฤษต้องสื่อสาระเท่าฝั่งไทย ไม่ใช่ย่อจนเหลือคำกลาง ๆ ที่สรรพากร
    /// ตรวจย้อนไม่ได้ว่าลดหนี้เพราะอะไร (เดิม "Adjustment" โดด ๆ ทั้งที่ไทยระบุ
    /// "ค่าสินค้าน้อยกว่าที่ตกลง")</summary>
    [Theory]
    [InlineData("cn_reason_adjustment")]
    [InlineData("cn_reason_writeoff")]
    [InlineData("dn_reason_adjustment")]
    public void Credit_debit_note_reasons_are_specific_not_generic(string key)
    {
        var v = DocumentLabels.For("en")[key];
        Assert.False(string.Equals(v, "Adjustment", StringComparison.OrdinalIgnoreCase),
            $"ป้าย {key} กว้างเกินไป — §86/9-10 ต้องระบุสาเหตุที่ตรวจย้อนได้");
        Assert.False(string.Equals(v, "Write-off", StringComparison.OrdinalIgnoreCase),
            $"ป้าย {key} กว้างเกินไป — CN ลดได้ไม่เกินยอดใบเดิม ต้องบอกว่าบางส่วน");
        // ยาวพอจะมีคำขยาย (คำเดียวโดด ๆ = ตกสาระ)
        Assert.Contains(" ", v.Trim());
    }
}
