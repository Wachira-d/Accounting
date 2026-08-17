using Accounting.Models.Enums;
using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่านตรวจ JE ก่อนบันทึก (JournalPostingGuard) — เทสต์จากเคสจริง
///
/// UV-202607-0037 (จาก EXP-20260706-0007 ผ่าน integration):
///   Dr 52120 ค่าวัสดุสิ้นเปลือง 17,890.00 (3 บรรทัด)
///   Dr 11610 ภาษีซื้อ            1,252.30
///   Cr 21917 ภาษีหัก ณ ที่จ่าย  19,142.30   ← เครดิตทั้งใบลง WHT!
/// Dr = Cr เป๊ะ → ผ่าน guard "สมดุล" เดิมทุกตัว แต่: ไม่มีขาเจ้าหนี้เลย
/// และ WHT = 107% ของฐาน (กฎหมายสูงสุด 15%)
/// </summary>
public class JournalPostingGuardTests
{
    private static JournalPostingGuard.LineFacts L(
        string code, AccountType type, decimal dr = 0, decimal cr = 0)
        => new(code, type, dr, cr);

    private static JournalPostingGuard.DocFacts ExpenseDoc(
        decimal sub, decimal vat, decimal wht) => new(
        DocumentType.Expense, sub, vat, wht, sub + vat - wht);

    // ── เคสจริงจากภาพผู้ใช้ ─────────────────────────────────────────────
    private static readonly List<JournalPostingGuard.LineFacts> BadJe = new()
    {
        L("52120", AccountType.Expense, dr: 11680.00m),
        L("52120", AccountType.Expense, dr: 1210.00m),
        L("52120", AccountType.Expense, dr: 5000.00m),
        L("11610", AccountType.Asset, dr: 1252.30m),
        L("21917", AccountType.Liability, cr: 19142.30m),   // ← ผิด: ทั้งใบลง WHT
    };

    [Fact]
    public void The_real_bad_je_is_caught_even_without_document_context()
    {
        // JE สมดุลเป๊ะ — แต่ WHT ratio จับได้โดยไม่ต้องรู้เอกสารเลย
        var findings = JournalPostingGuard.Validate(BadJe, doc: null);
        Assert.Contains(findings, f => f.RuleCode == "JE-WHT-RATIO" && f.IsError);
        Assert.DoesNotContain(findings, f => f.RuleCode == "JE-BAL");   // สมดุลจริง
    }

    [Fact]
    public void The_real_bad_je_is_caught_with_document_context()
    {
        // เอกสารจริง: ฐาน 17,890 + VAT 1,252.30, WHT 3% = 536.70
        var doc = ExpenseDoc(17890.00m, 1252.30m, 536.70m);
        var findings = JournalPostingGuard.Validate(BadJe, doc);
        var errors = findings.Where(f => f.IsError).Select(f => f.RuleCode).ToList();

        Assert.Contains("JE-WHT-RATIO", errors);       // 19,142.30 > 15% ของ 17,890
        Assert.Contains("JE-WHT-DOC", errors);         // ≠ 536.70 บนเอกสาร
        Assert.Contains("JE-NO-COUNTERPART", errors);  // ไม่มีขาเจ้าหนี้/เงินเลย
        Assert.NotNull(JournalPostingGuard.ErrorSummary(findings, "EXP-20260706-0007"));
    }

    [Fact]
    public void The_correct_version_of_the_same_je_passes_clean()
    {
        // JE ที่ถูก: Cr เจ้าหนี้ 18,605.60 + Cr WHT 536.70
        var good = new List<JournalPostingGuard.LineFacts>
        {
            L("52120", AccountType.Expense, dr: 17890.00m),
            L("11610", AccountType.Asset, dr: 1252.30m),
            L("21210", AccountType.Liability, cr: 18605.60m),
            L("21917", AccountType.Liability, cr: 536.70m),
        };
        var doc = ExpenseDoc(17890.00m, 1252.30m, 536.70m);
        var findings = JournalPostingGuard.Validate(good, doc);
        Assert.Empty(findings.Where(f => f.IsError));
        Assert.Null(JournalPostingGuard.ErrorSummary(findings, "X"));
    }

    [Fact]
    public void Cash_paid_pv_with_any_money_account_passes()
    {
        // จ่ายสด: Cr เงินสด = Total — แหล่งเงินเป็นบัญชีไหนก็ได้ที่ไม่ใช่ภาษี
        // (กฎไม่ fix รหัส เพื่อไม่ block เจ้าหนี้กรรมการ/เงินทดรอง)
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("52120", AccountType.Expense, dr: 1000m),
            L("11610", AccountType.Asset, dr: 70m),
            L("21230", AccountType.Liability, cr: 1040m),   // เจ้าหนี้กรรมการ
            L("21917", AccountType.Liability, cr: 30m),
        };
        var doc = new JournalPostingGuard.DocFacts(
            DocumentType.PaymentVoucher, 1000m, 70m, 30m, 1040m);
        Assert.Empty(JournalPostingGuard.Validate(je, doc).Where(f => f.IsError));
    }

    [Fact]
    public void Missing_wht_line_is_a_warning_not_a_block()
    {
        // เอกสารมี WHT แต่ JE ไม่ลง (เกณฑ์เงินสดรับรู้ตอนจ่าย) — เตือน ไม่ block
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("52120", AccountType.Expense, dr: 1000m),
            L("21210", AccountType.Liability, cr: 1000m),
        };
        var doc = new JournalPostingGuard.DocFacts(
            DocumentType.Expense, 1000m, 0m, 30m, 970m);
        var findings = JournalPostingGuard.Validate(je, doc);
        Assert.Contains(findings, f => f.RuleCode == "JE-WHT-MISSING" && !f.IsError);
        // COUNTERPART: Cr 1000 ≥ Total 970 → ผ่าน
        Assert.DoesNotContain(findings, f => f.RuleCode == "JE-NO-COUNTERPART");
    }

    [Fact]
    public void Unbalanced_je_is_flagged()
    {
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("52120", AccountType.Expense, dr: 1000m),
            L("21210", AccountType.Liability, cr: 900m),
        };
        Assert.Contains(JournalPostingGuard.Validate(je, null),
            f => f.RuleCode == "JE-BAL" && f.IsError);
    }

    [Fact]
    public void Vat_in_gl_may_be_less_but_never_more_than_the_document()
    {
        // น้อยกว่า = ไม่เคลม/พักรอใบกำกับ (ถูกกฎหมาย) — มากกว่า = ผิดแน่
        var doc = ExpenseDoc(1000m, 70m, 0m);
        var less = new List<JournalPostingGuard.LineFacts>
        {
            L("52120", AccountType.Expense, dr: 1070m),   // VAT รวมเป็นต้นทุน
            L("21210", AccountType.Liability, cr: 1070m),
        };
        Assert.Empty(JournalPostingGuard.Validate(less, doc).Where(f => f.IsError));

        var more = new List<JournalPostingGuard.LineFacts>
        {
            L("52120", AccountType.Expense, dr: 860m),
            L("11610", AccountType.Asset, dr: 210m),      // 3 เท่าของ VAT จริง
            L("21210", AccountType.Liability, cr: 1070m),
        };
        Assert.Contains(JournalPostingGuard.Validate(more, doc),
            f => f.RuleCode == "JE-VAT-OVER" && f.IsError);
    }

    [Fact]
    public void Legit_wht_at_the_maximum_15_percent_rate_passes()
    {
        // ค่าเช่า/ดอกเบี้ยบุคคลธรรมดาหักถึง 15% ได้จริง — ต้องไม่ false positive
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("53210", AccountType.Expense, dr: 10000m),
            L("21916", AccountType.Liability, cr: 1500m),
            L("21210", AccountType.Liability, cr: 8500m),
        };
        var doc = new JournalPostingGuard.DocFacts(
            DocumentType.Expense, 10000m, 0m, 1500m, 8500m);
        Assert.Empty(JournalPostingGuard.Validate(je, doc).Where(f => f.IsError));
    }

    [Fact]
    public void Deposit_documents_skip_document_context_rules()
    {
        // มัดจำมีทรงบัญชีของตัวเอง (Cr 215xx/217xx ไม่ใช่เจ้าหนี้) — ห้าม
        // false positive จากกฎ COUNTERPART
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("11111", AccountType.Asset, dr: 1070m),
            L("21712", AccountType.Liability, cr: 1000m),
            L("21913", AccountType.Liability, cr: 70m),
        };
        var doc = new JournalPostingGuard.DocFacts(
            DocumentType.Receipt, 1000m, 70m, 0m, 1070m, IsDeposit: true);
        Assert.Empty(JournalPostingGuard.Validate(je, doc).Where(f => f.IsError));
    }

    [Fact]
    public void Reversal_entries_are_validated_without_document_context()
    {
        // ตัวกลับของ JE ซื้อ: ขาสลับด้าน (Dr เจ้าหนี้ / Cr ค่าใช้จ่าย) — ถ้าเทียบ
        // กับเอกสารจะ false positive ทุกใบ ⇒ scanner ส่ง doc=null ให้
        var reversal = new List<JournalPostingGuard.LineFacts>
        {
            L("21210", AccountType.Liability, dr: 18605.60m),
            L("21917", AccountType.Liability, dr: 536.70m),
            L("52120", AccountType.Expense, cr: 17890.00m),
            L("11610", AccountType.Asset, cr: 1252.30m),
        };
        Assert.Empty(JournalPostingGuard.Validate(reversal, null).Where(f => f.IsError));
    }
}
