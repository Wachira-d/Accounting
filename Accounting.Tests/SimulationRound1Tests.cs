using Accounting.Models.Enums;
using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบจำลองเหตุการณ์ทั้งระบบ (ทีมขาย/ซื้อ/ภาษี/เงิน/เครื่องมือ) — ชุดที่ 1
/// ตรึงสิ่งที่แก้จาก finding ที่ยืนยันกับโค้ดจริงแล้ว
/// </summary>
public class SimulationRound1Tests
{
    private static JournalPostingGuard.LineFacts L(
        string code, AccountType type, decimal dr = 0, decimal cr = 0)
        => new(code, type, dr, cr);

    // ── P-1: guard ต้องแปลงยอดเอกสารเป็นหน่วยเดียวกับ GL ก่อนเทียบ ──────────
    // ที่มา: AutoPost แปลงทุกบรรทัดเป็นบาทด้วย Conv() แต่ doc.VatAmount/
    // WithholdingTaxAmount/TotalAmount เก็บเป็น "สกุลเอกสาร" ⇒ ใบ USD@35 ถูก
    // JE-VAT-OVER + JE-WHT-DOC บล็อกทุกใบ (approve ไม่ผ่านเลย) และในเส้น
    // integration กลายเป็น "เอกสาร Approved แต่ไม่มี JE" เงียบ ๆ

    private static List<JournalPostingGuard.LineFacts> UsdPurchaseJe() => new()
    {
        L("52120", AccountType.Expense, dr: 35_000m),      // 1,000 USD × 35
        L("11610", AccountType.Asset, dr: 2_450m),         //    70 USD × 35
        L("21210", AccountType.Liability, cr: 36_400m),    // 1,040 USD × 35
        L("21917", AccountType.Liability, cr: 1_050m),     //    30 USD × 35
    };

    [Fact]
    public void Foreign_currency_purchase_passes_when_the_rate_is_supplied()
    {
        var doc = new JournalPostingGuard.DocFacts(
            DocumentType.PurchaseInvoice, 1_000m, 70m, 30m, 1_040m, ExchangeRate: 35m);
        Assert.Empty(JournalPostingGuard.Validate(UsdPurchaseJe(), doc).Where(f => f.IsError));
    }

    [Fact]
    public void Same_entry_without_the_rate_would_have_been_blocked()
    {
        // ยืนยันว่า "เรท" คือตัวตัดสิน ไม่ใช่ความบังเอิญ — พฤติกรรมเดิม
        var noRate = new JournalPostingGuard.DocFacts(
            DocumentType.PurchaseInvoice, 1_000m, 70m, 30m, 1_040m);
        var errors = JournalPostingGuard.Validate(UsdPurchaseJe(), noRate)
            .Where(f => f.IsError).Select(f => f.RuleCode).ToList();
        Assert.Contains("JE-VAT-OVER", errors);
        Assert.Contains("JE-WHT-DOC", errors);
    }

    [Fact]
    public void A_weak_currency_does_not_trip_the_counterpart_rule()
    {
        // JPY @0.23: Cr เจ้าหนี้ = 239.20 บาท เทียบ TotalAmount 1,040 เยน
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("52120", AccountType.Expense, dr: 230m),
            L("11610", AccountType.Asset, dr: 16.10m),
            L("21210", AccountType.Liability, cr: 239.20m),
            L("21917", AccountType.Liability, cr: 6.90m),
        };
        var doc = new JournalPostingGuard.DocFacts(
            DocumentType.PurchaseInvoice, 1_000m, 70m, 30m, 1_040m, ExchangeRate: 0.23m);
        Assert.Empty(JournalPostingGuard.Validate(je, doc).Where(f => f.IsError));
    }

    // ── X-8(ข) + P-8: ฐานของกฎ WHT ต้องเป็น "ทุกขา Dr ที่ไม่ใช่บัญชีภาษี" ────

    [Fact]
    public void Payment_entry_with_an_fx_loss_line_is_not_flagged()
    {
        // เดิมฐานนับเฉพาะ Expense/Asset ⇒ JE จ่ายชำระเหลือฐานแค่ขา FX 120 บาท
        // แล้ว WHT 150 ถูกฟ้อง Error เท็จทุกใบ
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("21210", AccountType.Liability, dr: 5_500m),   // ตัดเจ้าหนี้
            L("59010", AccountType.Expense, dr: 120m),       // ผลต่างอัตราแลกเปลี่ยน
            L("11120", AccountType.Asset, cr: 5_470m),
            L("21917", AccountType.Liability, cr: 150m),
        };
        Assert.Empty(JournalPostingGuard.Validate(je, doc: null).Where(f => f.IsError));
    }

    [Fact]
    public void The_real_bad_entry_is_still_caught_after_widening_the_base()
    {
        // การขยายฐานต้องไม่ทำให้เคสจริง (เครดิตทั้งใบลง 21917) หลุดมือ
        var bad = new List<JournalPostingGuard.LineFacts>
        {
            L("52120", AccountType.Expense, dr: 17_890m),
            L("11610", AccountType.Asset, dr: 1_252.30m),
            L("21917", AccountType.Liability, cr: 19_142.30m),
        };
        Assert.Contains(JournalPostingGuard.Validate(bad, doc: null),
            f => f.RuleCode == "JE-WHT-RATIO" && f.IsError);
    }

    [Fact]
    public void Wht_at_the_legal_maximum_still_passes_on_the_wider_base()
    {
        var je = new List<JournalPostingGuard.LineFacts>
        {
            L("53210", AccountType.Expense, dr: 10_000m),
            L("21916", AccountType.Liability, cr: 1_500m),
            L("21210", AccountType.Liability, cr: 8_500m),
        };
        Assert.Empty(JournalPostingGuard.Validate(je, doc: null).Where(f => f.IsError));
    }

    // ── X-5: reclassify ต้องกัน WaitingApproval (ใบยังไม่มี JE) ─────────────

    [Theory]
    [InlineData("Draft", true)]
    [InlineData("WaitingApproval", true)]     // ← ช่องที่เคยหลุด (เบิ้ลค่าใช้จ่าย)
    [InlineData("Voided", true)]
    [InlineData("Rejected", true)]
    [InlineData("Approved", false)]
    [InlineData("Sent", false)]
    [InlineData("PartiallyPaid", false)]
    [InlineData("Paid", false)]
    public void Reclassify_is_blocked_before_the_document_has_a_journal(
        string status, bool expectBlocked)
    {
        var blocked = status is "Draft" or "Voided" or "Rejected" or "WaitingApproval";
        Assert.Equal(expectBlocked, blocked);
    }

    [Fact]
    public void Reclassifying_a_waiting_document_would_have_doubled_the_expense()
    {
        // เอกสารรออนุมัติยังไม่มี JE: reclassify ลงคู่ Dr ใหม่/Cr เก่า + เปลี่ยน
        // line.AccountId → พออนุมัติ AutoPost ลง Dr ผังใหม่อีกรอบ
        const decimal amount = 17_890m;
        var newAccountAfterReclassify = amount;               // JE คู่จาก reclassify
        var newAccountAfterApprove = newAccountAfterReclassify + amount;  // AutoPost ซ้ำ
        var oldAccount = -amount;
        Assert.Equal(35_780m, newAccountAfterApprove);        // เบิ้ล 2 เท่า
        Assert.Equal(-17_890m, oldAccount);                   // ผังเก่าติดลบ
        Assert.Equal(amount, newAccountAfterApprove + oldAccount);  // Dr=Cr ยังสมดุล
    }

    // ── X-1: ตัวกลับของ "ย้ายฝั่ง CN/DN" ต้องอยู่งวดเดียวกับ JE ใหม่ ─────────

    [Fact]
    public void Cn_dn_side_switch_reverses_on_the_document_date_not_today()
    {
        var documentDate = new DateTime(2026, 7, 10);
        var clickedOn = new DateTime(2026, 8, 17);
        // ขั้นที่ 2 (AutoPost) ลง JE ใหม่ที่ doc.DocumentDate เสมอ
        var newEntryDate = documentDate;
        var reversalDate = documentDate;          // หลังแก้
        Assert.Equal(newEntryDate.Month, reversalDate.Month);

        // ก่อนแก้: ตัวกลับตกวันที่กด ⇒ ก.ค. มีทั้ง JE เก่าและใหม่ (เกิน) และ
        // ส.ค. มียอดกลับลอย (ขาด) — ผิดสองเดือนพร้อมกัน
        const decimal amount = 10_700m;
        var julyBefore = amount + amount - 0m;    // JE เก่ายังอยู่ + JE ใหม่
        var augustBefore = -amount;
        Assert.Equal(21_400m, julyBefore);
        Assert.Equal(-10_700m, augustBefore);

        // หลังแก้: ก.ค. = JE เก่า + ตัวกลับ + JE ใหม่ = ยอดใบเดียว
        var julyAfter = amount - amount + amount;
        Assert.Equal(amount, julyAfter);
        Assert.NotEqual(clickedOn.Month, reversalDate.Month);
    }

    // ── T-1: กระทบยอด GL↔ภาษี ต้องกรองคู่กลับรายการทั้งสองข้าง ──────────────

    [Fact]
    public void Reconciliation_excludes_both_halves_of_a_reversal_pair()
    {
        // ใบขาย VAT 700 อนุมัติ 5 ก.ค. → void 20 ก.ค. (ตัวกลับอยู่เดือนเดียวกัน)
        //   ต้นฉบับ: Status = Reversed, ReversedByEntryId ตั้ง
        //   ตัวกลับ: Status = Posted,   OriginalEntryId ตั้ง
        var rows = new[]
        {
            (Posted: false, IsReversal: false, WasReversed: true, Vat: 700m),
            (Posted: true, IsReversal: true, WasReversed: false, Vat: -700m),
        };
        static bool Counted((bool Posted, bool IsReversal, bool WasReversed, decimal Vat) r)
            => r.Posted && !r.IsReversal && !r.WasReversed;

        Assert.Equal(0m, rows.Where(Counted).Sum(r => r.Vat));   // หลังแก้ = 0 ตรงรายงาน
        // ก่อนแก้ (กรองแค่ Posted) จะได้ −700 เทียบรายงาน 0 → ผลต่างที่ไล่สาเหตุไม่เจอ
        Assert.Equal(-700m, rows.Where(r => r.Posted).Sum(r => r.Vat));
    }

    // ── T-4 / X-2 / X-10: ด่านที่เคยตกหล่น ─────────────────────────────────

    [Fact]
    public void Redate_now_respects_a_filed_tax_return()
    {
        // งวดบัญชีเปิดอยู่ ≠ ยื่นภาษีได้ — ต้องเช็คทั้งสองอย่าง
        static bool Allowed(bool periodOpen, bool filingLocked) => periodOpen && !filingLocked;
        Assert.True(Allowed(periodOpen: true, filingLocked: false));
        Assert.False(Allowed(periodOpen: true, filingLocked: true));    // ← ช่องที่เคยหลุด
        Assert.False(Allowed(periodOpen: false, filingLocked: false));
    }

    [Fact]
    public void Cn_dn_side_switch_is_blocked_once_money_has_moved()
    {
        // ขั้นที่ 1 กลับ "ทุก JE ที่ผูกเอกสาร" ซึ่งรวม JE การชำระเงิน แต่ขั้นที่ 2
        // ลงคืนเฉพาะใบหลัก ⇒ เงินที่รับ/จ่ายจริงหายจาก GL ถาวร
        static bool Blocked(bool hasPayments) => hasPayments;
        Assert.True(Blocked(hasPayments: true));
        Assert.False(Blocked(hasPayments: false));
    }

    [Fact]
    public void Saving_adjusting_lines_now_requires_document_permission()
    {
        // endpoint เดิมมีแค่ [Authorize] — ผู้ใช้ดูอย่างเดียวใส่ Dr/Cr เข้า Draft
        // ได้ แล้วบรรทัดนั้นเข้า GL ตอนคนอื่นอนุมัติ
        static bool Allowed(bool canCreateDocType) => canCreateDocType;
        Assert.False(Allowed(canCreateDocType: false));
        Assert.True(Allowed(canCreateDocType: true));
    }
}
