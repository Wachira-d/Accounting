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
    // ── PP36_REVIEW P0-1 (2026-10-02): §83/6 บริการต่างประเทศ ─────────────────
    // เคสจริง PV-20260901-0001 Booking.com B.V. ค่าคอมมิชชั่น 5,908 · VAT ประเมินเอง 413.56 · TotalAmount 6,321.56
    // JE ที่ AutoPost สร้าง (ทรงถูกตามกฎหมาย): Dr ค่าใช้จ่าย 5,908 · Dr 11640 413.56 / Cr 21912 413.56 · Cr ธนาคาร 5,908
    // เดิมด่านเทียบ Cr ธนาคาร 5,908 กับ TotalAmount 6,321.56 ⇒ JE-NO-COUNTERPART ⇒ อนุมัติไม่ได้ทุกใบ

    private static List<JournalPostingGuard.LineFacts> ForeignJe(string payeeCode, AccountType payeeType) => new()
    {
        L("52190", AccountType.Expense, dr: 5908.00m),
        L("11640", AccountType.Asset, dr: 413.56m),
        L("21912", AccountType.Liability, cr: 413.56m),
        L(payeeCode, payeeType, cr: 5908.00m),
    };

    private static JournalPostingGuard.DocFacts ForeignDoc(DocumentType t, bool flagged) => new(
        t, 5908.00m, 413.56m, 0m, 6321.56m, IsForeignService: flagged);

    [Theory]
    [InlineData(DocumentType.PaymentVoucher, "11120")]   // จ่ายทันที — Cr ธนาคาร
    [InlineData(DocumentType.PurchaseInvoice, "21210")]  // ตั้งหนี้ — Cr เจ้าหนี้การค้า
    [InlineData(DocumentType.Expense, "21220")]          // ตั้งหนี้ค่าใช้จ่าย — Cr เจ้าหนี้อื่น
    public void Foreign_service_je_8306_passes_for_pv_pi_and_expense(DocumentType t, string payeeCode)
    {
        var payeeType = payeeCode.StartsWith("1") ? AccountType.Asset : AccountType.Liability;
        var findings = JournalPostingGuard.Validate(ForeignJe(payeeCode, payeeType), ForeignDoc(t, flagged: true));
        Assert.Empty(findings.Where(f => f.IsError));
        Assert.Null(JournalPostingGuard.ErrorSummary(findings, "PV-20260901-0001"));
    }

    [Fact]
    public void Foreign_service_expected_payee_comes_from_SplitCredit_not_inline_math()
    {
        // ยอดที่คาด = ForeignServiceVat.SplitCredit(...).PayeeCredit — เทสต์ล็อกว่าตัวเลขในข้อความเท่ากับตัวตัดสินกลาง
        var split = Accounting.Helpers.ForeignServiceVat.SplitCredit(true, 6321.56m, 413.56m);
        Assert.Equal(5908.00m, split.PayeeCredit);
        var short1 = new List<JournalPostingGuard.LineFacts>
        {
            L("52190", AccountType.Expense, dr: 5908.00m),
            L("11640", AccountType.Asset, dr: 413.56m),
            L("21912", AccountType.Liability, cr: 413.56m),
            L("11120", AccountType.Asset, cr: 4000.00m),
            L("21917", AccountType.Liability, cr: 1908.00m),   // เงินผู้รับไปกองบัญชีภาษี
        };
        var f = JournalPostingGuard.Validate(short1, ForeignDoc(DocumentType.PaymentVoucher, flagged: true));
        var e = Assert.Single(f, x => x.RuleCode == "JE-NO-COUNTERPART");
        Assert.Contains("§83/6", e.Message);   // ข้อความบอกว่ายอดที่คาดหักภาษีประเมินเองแล้ว (ไม่ผูกรูปแบบตัวเลขตาม culture)
    }

    [Fact]
    public void Unflagged_purchase_doc_with_cr_21912_is_blocked()
    {
        // ทิศตรงข้าม: ใบไม่ติ๊กบริการต่างประเทศแต่ JE มี Cr 21912 ⇒ ภ.พ.36 ไม่ถูกนับ/นำส่ง ⇒ ต้องล้มดัง
        var findings = JournalPostingGuard.Validate(
            ForeignJe("11120", AccountType.Asset), ForeignDoc(DocumentType.PaymentVoucher, flagged: false));
        var errors = findings.Where(x => x.IsError).Select(x => x.RuleCode).ToList();
        Assert.Contains("JE-PP36-UNFLAGGED", errors);
        Assert.Contains("JE-NO-COUNTERPART", errors);   // ใบไม่ติ๊ก ⇒ ยอดผู้รับที่คาด = TotalAmount เต็มเหมือนเดิม
    }

    [Fact]
    public void Unflagged_purchase_doc_with_full_counterpart_but_cr_21912_still_blocked()
    {
        // เจ้าหนี้ครบยอด แต่มีขา Cr 21912 แถม (ถ่วงด้วย Dr 11640) บนใบที่ไม่ติ๊ก ⇒ หนี้ ภ.พ.36 ที่ไม่มีใครนำส่ง
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("52190", AccountType.Expense, dr: 1000m),
            L("11610", AccountType.Asset, dr: 70m),
            L("21210", AccountType.Liability, cr: 1070m),
            L("21912", AccountType.Liability, cr: 70m),
            L("52190", AccountType.Expense, dr: 70m),
        };
        var doc = new JournalPostingGuard.DocFacts(DocumentType.PurchaseInvoice, 1000m, 70m, 0m, 1070m);
        Assert.Contains(JournalPostingGuard.Validate(je, doc), x => x.RuleCode == "JE-PP36-UNFLAGGED" && x.IsError);
    }

    [Fact]
    public void Flagged_foreign_doc_with_21912_above_self_assessed_vat_is_blocked()
    {
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("52190", AccountType.Expense, dr: 6321.56m),
            L("11640", AccountType.Asset, dr: 413.56m),
            L("21912", AccountType.Liability, cr: 827.12m),   // สองเท่าของ VAT ประเมินเอง
            L("11120", AccountType.Asset, cr: 5908.00m),
        };
        Assert.Contains(JournalPostingGuard.Validate(je, ForeignDoc(DocumentType.PaymentVoucher, flagged: true)),
            x => x.RuleCode == "JE-VAT-OVER" && x.IsError);
    }

    [Fact]
    public void Regular_purchase_doc_money_parked_in_tax_accounts_is_still_caught()
    {
        // ใบปกติ (ไม่ติ๊ก) ที่เงินไปกองบัญชีภาษี — ด่านเดิมยังทำงาน (เคส UV-202607-0037 ใช้ BadJe ข้างบนครอบอีกชั้น)
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("52190", AccountType.Expense, dr: 1000m),
            L("11610", AccountType.Asset, dr: 70m),
            L("21210", AccountType.Liability, cr: 1000m),
            L("21917", AccountType.Liability, cr: 70m),
        };
        var doc = new JournalPostingGuard.DocFacts(DocumentType.PurchaseInvoice, 1000m, 70m, 0m, 1070m);
        Assert.Contains(JournalPostingGuard.Validate(je, doc), x => x.RuleCode == "JE-NO-COUNTERPART" && x.IsError);
        // และธง §83/6 บนใบขายไม่ผ่อนกฎฝั่งซื้อ/ไม่ทำให้ภาษีขายถูกจำกัดเป็น 0
        var sale = new List<JournalPostingGuard.LineFacts>
        {
            L("11310", AccountType.Asset, dr: 1070m),
            L("41110", AccountType.Revenue, cr: 1000m),
            L("21911", AccountType.Liability, cr: 70m),
        };
        var saleDoc = new JournalPostingGuard.DocFacts(DocumentType.TaxInvoice, 1000m, 70m, 0m, 1070m, IsForeignService: true);
        Assert.Empty(JournalPostingGuard.Validate(sale, saleDoc).Where(x => x.IsError));
    }

    [Fact]
    public void Foreign_service_in_usd_converts_before_split()
    {
        // ใบ USD@35: ฐาน 100 VAT 7 Total 107 ⇒ GL ฐาน 3,500 · 21912 245 · ธนาคาร 3,500
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("52190", AccountType.Expense, dr: 3500m),
            L("11640", AccountType.Asset, dr: 245m),
            L("21912", AccountType.Liability, cr: 245m),
            L("11120", AccountType.Asset, cr: 3500m),
        };
        var doc = new JournalPostingGuard.DocFacts(DocumentType.PaymentVoucher, 100m, 7m, 0m, 107m,
            ExchangeRate: 35m, IsForeignService: true);
        Assert.Empty(JournalPostingGuard.Validate(je, doc).Where(x => x.IsError));
    }

    // PP36_REVIEW P2-7: ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทางเจ้าของ ภ.พ.36 — Cr 21912 อีกรอบ = นับซ้ำ
    [Fact]
    public void Settlement_pv_of_pp36_owner_with_21912_credit_is_blocked()
    {
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("21210", AccountType.Liability, dr: 1000m),
            L("21912", AccountType.Liability, cr: 70m),
            L("11120", AccountType.Asset, cr: 930m),
        };
        var doc = new JournalPostingGuard.DocFacts(DocumentType.PaymentVoucher, 1000m, 70m, 0m, 1070m,
            IsForeignService: true, SourceOwnsPp36: true);
        Assert.Contains(JournalPostingGuard.Validate(je, doc), f => f.RuleCode == "JE-PP36-DOUBLE" && f.IsError);
    }

    [Fact]
    public void Opposite_settlement_pv_of_pp36_owner_without_21912_passes_double_check()
    {
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("21210", AccountType.Liability, dr: 1000m),
            L("11120", AccountType.Asset, cr: 1000m),
        };
        var doc = new JournalPostingGuard.DocFacts(DocumentType.PaymentVoucher, 1000m, 70m, 0m, 1070m,
            IsForeignService: true, SourceOwnsPp36: true);
        Assert.DoesNotContain(JournalPostingGuard.Validate(je, doc), f => f.RuleCode == "JE-PP36-DOUBLE");
    }
}
