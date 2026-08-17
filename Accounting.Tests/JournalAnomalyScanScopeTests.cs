using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ขอบเขตการเทียบ doc-facts ของ JournalAnomalyService — เทียบทั้งใบได้เฉพาะ
/// "JE หลัก" (primary posting ตอนอนุมัติ) เท่านั้น
///
/// ที่มา (self-review รอบจำลองเหตุการณ์): JE รับชำระงวดบางส่วน 500 ของใบ
/// 1,040 เคยโดนกฎ JE-NO-COUNTERPART (500 &lt; 1,040) ทั้งที่ถูกต้อง — JE ลูก
/// ทุกชนิดยอดเป็น "บางส่วน/ผลต่าง" โดยธรรมชาติ เทียบทั้งใบเมื่อไรก็ผิดเมื่อนั้น
///
/// ใช้ whitelist ตาม description จริงของ JE หลัก 3 แบบ (ไม่ใช่ blacklist
/// marker — JE ระบบชนิดใหม่ ๆ ที่ไม่มี marker จะหลุดมาโดน false positive):
///   AutoPost:            "Auto-post จาก {เลขเอกสาร}"
///   Integration mapping: "Auto: {เลขเอกสาร}"
///   Integration PV:      "ใบสำคัญจ่าย {เลขเอกสาร} (integration sync)"
/// </summary>
public class JournalAnomalyScanScopeTests
{
    /// <summary>mirror ของเงื่อนไข isPrimaryPosting ใน ScanAsync</summary>
    private static bool IsPrimary(string? description, bool isReversal = false)
    {
        if (isReversal) return false;
        var d = description ?? "";
        return d.StartsWith("Auto-post จาก", StringComparison.Ordinal)
            || d.StartsWith("Auto:", StringComparison.Ordinal)
            || d.Contains("(integration sync)", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Auto-post จาก EXP-20260706-0007")]
    [InlineData("Auto: INV-20260724-0003")]
    [InlineData("ใบสำคัญจ่าย PV-2026-001 (integration sync)")]
    public void Primary_postings_are_compared_against_the_document(string desc)
        => Assert.True(IsPrimary(desc));

    [Theory]
    [InlineData("ชำระเงิน INV-20260724-0003 งวดที่ 2")]                    // payment JE
    [InlineData("รับชำระ REC-2026-010")]                                   // receipt JE
    [InlineData("Reclassify ผังบัญชี — EXP-001 'ค่าโฆษณา' (53120 → 53310)")]
    [InlineData("ปรับปรุงผังบัญชีของ UV-202607-0037 (EXP-20260706-0007)")]  // adjust JE
    [InlineData("รับชำระ → tax point เกิด: ย้ายภาษีขายพัก 21913 → 21911")]  // undue reclass
    [InlineData("หักมัดจำ REC-001 เข้าใบ INV-002")]                        // deposit apply
    [InlineData("FX revaluation 2026-07")]                                 // FX reval
    [InlineData(null)]
    public void Child_and_system_entries_are_checked_structurally_only(string? desc)
        => Assert.False(IsPrimary(desc));

    [Fact]
    public void Reversals_are_never_primary_even_with_a_primary_looking_description()
    {
        // ตัวกลับของ JE หลัก สืบทอด description บางส่วนมาได้ — ต้องไม่ถูกเทียบ
        Assert.False(IsPrimary("Auto-post จาก EXP-001", isReversal: true));
    }

    [Fact]
    public void The_partial_payment_false_positive_is_gone()
    {
        // เคสที่เคยพัง: Expense 1,000 + VAT 70 − WHT 30 = Total 1,040
        // จ่ายงวดแรก 500 → JE: Dr เจ้าหนี้ 500 / Cr เงินสด 500
        var paymentJe = new List<JournalPostingGuard.LineFacts>
        {
            new("21210", Models.Enums.AccountType.Liability, 500m, 0m),
            new("11111", Models.Enums.AccountType.Asset, 0m, 500m),
        };
        // scanner ส่ง doc=null ให้ JE ลูก → ไม่มีกฎเทียบทั้งใบ → สะอาด
        Assert.Empty(JournalPostingGuard.Validate(paymentJe, doc: null)
            .Where(f => f.IsError));

        // แต่ถ้าพลาดส่ง doc-facts เข้าไป (พฤติกรรมเก่า) จะ false positive ทันที
        var docFacts = new JournalPostingGuard.DocFacts(
            Models.Enums.DocumentType.Expense, 1000m, 70m, 30m, 1040m);
        Assert.Contains(JournalPostingGuard.Validate(paymentJe, docFacts),
            f => f.RuleCode == "JE-NO-COUNTERPART");   // ยืนยันว่า scope คือตัวตัดสิน
    }

    [Fact]
    public void Wht_ratio_still_catches_the_real_bad_je_without_doc_facts()
    {
        // การลด scope ต้องไม่ทำให้เคสจริง (เครดิตทั้งใบลง 21917) หลุดมือ —
        // กฎ 15% จับได้จากโครงสร้างล้วน ๆ
        var bad = new List<JournalPostingGuard.LineFacts>
        {
            new("52120", Models.Enums.AccountType.Expense, 17890m, 0m),
            new("11610", Models.Enums.AccountType.Asset, 1252.30m, 0m),
            new("21917", Models.Enums.AccountType.Liability, 0m, 19142.30m),
        };
        Assert.Contains(JournalPostingGuard.Validate(bad, doc: null),
            f => f.RuleCode == "JE-WHT-RATIO" && f.IsError);
    }
}
