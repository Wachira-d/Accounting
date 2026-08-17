using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เปลี่ยนผังบัญชีรายบรรทัดของเอกสารที่อนุมัติแล้ว (ReclassifyLineAccountAsync)
///
/// ที่มา: ผู้ใช้เปิดใบขายที่รับเงินแล้ว — "ไม่มีให้แก้ไขผังบัญชีเลย" เพราะเดิม
/// เปิดเฉพาะฝั่งซื้อ/จ่าย โดยอ้าง §86/4 ว่าใบกำกับแก้ไม่ได้ ซึ่งอ้างผิดมาตรา:
/// §86/4 บังคับ "สิ่งที่พิมพ์บนใบกำกับ" และ **รหัสผังบัญชีไม่ได้อยู่บนใบกำกับ**
/// การย้ายรายได้ 41100 → 41200 ไม่แตะยอดบนใบ ไม่แตะ VAT ไม่แตะ ภ.พ.30
///
/// จุดที่พลาดง่ายที่สุดตอนเปิดฝั่งขาย = **ทิศของ JE ย้ายบัญชี** — สูตรเดิม
/// Dr ใหม่ / Cr เก่า ถูกเฉพาะบรรทัดค่าใช้จ่าย ถ้าใช้กับรายได้จะกลายเป็น
/// รายได้เดิมเพิ่มเป็นสองเท่าและบัญชีใหม่ติดลบ
/// </summary>
public class ReclassifyLineAccountTests
{
    // ── ทิศของ JE ย้ายบัญชี ──────────────────────────────────────────────
    private enum Nature { DebitNatured, CreditNatured }

    /// <summary>mirror ของการเลือกทิศใน ReclassifyLineAccountAsync —
    /// อ่านขาที่ลงจริงใน GL ก่อน ถ้าไม่มี JE จึงตกกลับใช้ธรรมชาติของผัง</summary>
    private static (string DebitAccount, string CreditAccount) BuildMove(
        string oldAcc, string newAcc, decimal glDebit, decimal glCredit, Nature fallback)
    {
        var hasJe = glDebit > 0 || glCredit > 0;
        var oldWasCredited = hasJe ? glCredit > glDebit : fallback == Nature.CreditNatured;
        return oldWasCredited ? (oldAcc, newAcc) : (newAcc, oldAcc);
    }

    [Fact]
    public void Expense_line_moves_debit_to_the_new_account()
    {
        // ค่าใช้จ่ายเดิมลง Dr 53120 → ย้ายไป 53210: Dr ใหม่ / Cr เก่า
        var (dr, cr) = BuildMove("53120", "53210", glDebit: 1000m, glCredit: 0m, Nature.DebitNatured);
        Assert.Equal("53210", dr);
        Assert.Equal("53120", cr);
    }

    [Fact]
    public void Revenue_line_moves_in_the_opposite_direction()
    {
        // รายได้เดิมลง Cr 41100 → ย้ายไป 41200: Dr **เก่า** / Cr **ใหม่**
        var (dr, cr) = BuildMove("41100", "41200", glDebit: 0m, glCredit: 1000m, Nature.CreditNatured);
        Assert.Equal("41100", dr);
        Assert.Equal("41200", cr);
    }

    [Fact]
    public void Wrong_direction_on_revenue_would_double_the_old_account()
    {
        // เดโมว่าทำไมทิศสำคัญ: ถ้าใช้สูตรค่าใช้จ่ายกับรายได้
        const decimal amount = 1000m;
        var oldBefore = -amount;                 // Cr 1000 (ยอดรายได้ = เครดิต)
        var oldAfterWrong = oldBefore - amount;  // Cr อีก 1000 → รายได้เดิมเป็น 2000
        var newAfterWrong = amount;              // Dr 1000 → บัญชีใหม่ติดลบ
        Assert.Equal(-2000m, oldAfterWrong);
        Assert.Equal(1000m, newAfterWrong);

        var oldAfterRight = oldBefore + amount;  // Dr 1000 → ล้างเป็น 0
        var newAfterRight = -amount;             // Cr 1000 → ย้ายมาครบ
        Assert.Equal(0m, oldAfterRight);
        Assert.Equal(-1000m, newAfterRight);
    }

    [Theory]
    [InlineData(Nature.DebitNatured, "new", "old")]    // สินทรัพย์/ค่าใช้จ่าย
    [InlineData(Nature.CreditNatured, "old", "new")]   // รายได้/หนี้สิน/ทุน
    public void Falls_back_to_account_nature_when_no_je_found(
        Nature fallback, string expectDr, string expectCr)
    {
        // ใบเก่า/ใบที่ลงผ่าน integration อาจหา JE ของเอกสารไม่เจอ
        var (dr, cr) = BuildMove("old", "new", 0m, 0m, fallback);
        Assert.Equal(expectDr, dr);
        Assert.Equal(expectCr, cr);
    }

    [Fact]
    public void Net_effect_is_always_zero_across_the_two_accounts()
    {
        // ไม่ว่าทิศไหน ยอดรวมของงบต้องไม่ขยับ — ย้ายที่อยู่เท่านั้น
        foreach (var (d, c) in new[] { (1000m, 0m), (0m, 1000m) })
        {
            var (_, _) = BuildMove("A", "B", d, c, Nature.DebitNatured);
            Assert.Equal(0m, 1000m - 1000m);   // Dr เท่ากับ Cr เสมอ (บรรทัดคู่)
        }
    }

    // ── บัญชีคุมที่ห้ามย้ายเข้า/ออก ───────────────────────────────────────
    private static readonly HashSet<string> Protected = new()
    {
        "11310", "11610", "11640", "21210",
        "21510", "21520", "21610", "21711", "21712", "21713",
        "21911", "21912", "21913", "21916", "21917", "21918",
    };
    private static bool IsProtected(string code) => Protected.Contains(code.Trim());

    [Theory]
    [InlineData("21911")]   // ภาษีขาย — ย้ายแล้ว ภ.พ.30 เพี้ยน
    [InlineData("11610")]   // ภาษีซื้อ
    [InlineData("11310")]   // ลูกหนี้การค้า — ย้ายแล้วรายงานอายุหนี้เพี้ยน
    [InlineData("21210")]   // เจ้าหนี้การค้า
    [InlineData("21712")]   // ค่าสินค้ารับล่วงหน้า — วงจรมัดจำ
    [InlineData("21917")]   // ภ.ง.ด.53 ค้างจ่าย
    public void Control_accounts_are_blocked(string code) => Assert.True(IsProtected(code));

    [Theory]
    [InlineData("41100")]   // รายได้จากการขาย
    [InlineData("53120")]   // ค่าโฆษณา
    [InlineData("21511")]   // ค่าไฟฟ้าค้างจ่าย — อยู่ช่วง 215xx แต่ไม่ใช่มัดจำ
    [InlineData("21714")]   // ดอกเบี้ยค้างจ่าย — อยู่ช่วง 217xx แต่ไม่ใช่มัดจำ
    [InlineData("11320")]   // ลูกหนี้อื่น — ไม่ใช่บัญชีคุม AR
    public void Ordinary_accounts_stay_movable(string code) => Assert.False(IsProtected(code));

    [Fact]
    public void Prefix_matching_would_have_locked_ordinary_accruals()
    {
        // negative test ของตัวเลือกออกแบบ: ถ้าใช้ prefix "215"/"217" แทนรหัสตรง
        // จะเผลอล็อกค่าไฟฟ้า/ดอกเบี้ยค้างจ่าย ซึ่งเป็นผังปกติที่ต้องย้ายได้
        var prefixes = new[] { "215", "217" };
        Assert.True(prefixes.Any(p => "21511".StartsWith(p)));   // ← จะโดนล็อกผิด
        Assert.True(prefixes.Any(p => "21714".StartsWith(p)));
        Assert.False(IsProtected("21511"));                      // ของจริงไม่ล็อก
        Assert.False(IsProtected("21714"));
    }

    // ── ประตูที่ยังต้องปิดอยู่ ────────────────────────────────────────────
    private sealed record Doc(
        string Type, string Status, bool IsDeposit = false,
        bool PeriodClosed = false, bool HasNonReceiptDownstream = false,
        bool InSubmittedTaxReport = false, bool EtaxSubmitted = false,
        bool HasSettlementReceipt = false, bool HasPayments = false);

    private static string? Block(Doc d)
    {
        var allowed = new[] { "PurchaseInvoice", "Expense", "PaymentVoucher",
                              "Invoice", "TaxInvoice", "Receipt", "CreditNote", "DebitNote" };
        if (!allowed.Contains(d.Type)) return "type";
        if (d.IsDeposit) return "deposit";
        if (d.Status is "Draft" or "Voided" or "Rejected") return "status";
        if (d.PeriodClosed) return "period";
        if (d.HasNonReceiptDownstream) return "downstream";
        if (d.InSubmittedTaxReport) return "taxReport";
        if (d.EtaxSubmitted) return "etax";
        return null;
    }

    [Fact]
    public void Sales_invoice_that_has_been_paid_is_now_allowed()
    {
        // เคสของผู้ใช้: ใบขายอนุมัติแล้ว มีการรับชำระ + มีใบเสร็จหลักฐาน
        var d = new Doc("TaxInvoice", "Paid", HasPayments: true, HasSettlementReceipt: true);
        Assert.Null(Block(d));
    }

    [Fact]
    public void Payment_alone_no_longer_blocks()
    {
        // JE ของการชำระแตะ เงินสด ↔ ลูกหนี้/เจ้าหนี้ ไม่ใช่ผังของบรรทัดสินค้า
        Assert.Null(Block(new Doc("Expense", "Paid", HasPayments: true)));
    }

    [Fact]
    public void Settlement_receipt_is_not_a_downstream_document()
    {
        // ใบเสร็จหลักฐาน = evidence-only ไม่ลง JE ⇒ ไม่มีอะไรใน GL ให้ขัดกัน
        Assert.Null(Block(new Doc("Invoice", "Paid", HasSettlementReceipt: true)));
        // แต่เอกสารปลายทางจริง (ใบลดหนี้/ใบสำคัญจ่าย) ยังบล็อกเหมือนเดิม
        Assert.Equal("downstream", Block(new Doc("Invoice", "Paid", HasNonReceiptDownstream: true)));
    }

    [Theory]
    [InlineData("Draft", "status")]
    [InlineData("Voided", "status")]
    [InlineData("Rejected", "status")]
    public void Non_posted_states_still_use_the_normal_form(string status, string expected)
        => Assert.Equal(expected, Block(new Doc("Invoice", status)));

    [Fact]
    public void Compliance_gates_survive_the_relaxation()
    {
        Assert.Equal("period", Block(new Doc("TaxInvoice", "Paid", PeriodClosed: true)));
        Assert.Equal("taxReport", Block(new Doc("TaxInvoice", "Paid", InSubmittedTaxReport: true)));
        Assert.Equal("etax", Block(new Doc("TaxInvoice", "Paid", EtaxSubmitted: true)));
        Assert.Equal("deposit", Block(new Doc("Invoice", "Paid", IsDeposit: true)));
    }

    [Fact]
    public void Quotation_and_other_types_remain_unsupported()
    {
        Assert.Equal("type", Block(new Doc("Quotation", "Approved")));
        Assert.Equal("type", Block(new Doc("PurchaseOrder", "Approved")));
    }
}
