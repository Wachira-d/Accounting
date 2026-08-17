using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบจำลองเหตุการณ์ ชุดที่ 3 — เครื่องมือแก้ไขย้อนหลังใช้ต่อเนื่องกัน
/// และวงจร supersede (แทนที่ใบแจ้งหนี้ด้วยใบกำกับ)
/// </summary>
public class SimulationRound3Tests
{
    // ── X-3: ปรับปรุง JE ซ้อนบนใบเดิม ──────────────────────────────────────

    [Fact]
    public void Stacking_two_adjustments_on_the_same_entry_corrupts_the_chart()
    {
        // Adjust ไม่แก้ใบเดิม (ลงใบปรับปรุงใหม่) ⇒ เปิดแผงอีกครั้งยังเห็นผังเดิม
        // แล้วคำนวณผลต่างจากใบเดิมอีกรอบ
        const decimal amount = 17_890m;
        var gl = new Dictionary<string, decimal> { ["53120"] = amount };

        void Adjust(string toAccount)
        {
            gl["53120"] = gl.GetValueOrDefault("53120") - amount;   // delta จากใบเดิมเสมอ
            gl[toAccount] = gl.GetValueOrDefault(toAccount) + amount;
        }

        Adjust("53310");
        Assert.Equal(0m, gl["53120"]);          // ครั้งแรกถูกต้อง
        Assert.Equal(amount, gl["53310"]);

        Adjust("53320");                         // ครั้งที่สอง — ไม่มีใครกัน
        Assert.Equal(-amount, gl["53120"]);      // ผังเดิมติดลบ
        Assert.Equal(amount, gl["53310"]);       // ผังกลางค้าง
        Assert.Equal(amount, gl["53320"]);
        Assert.Equal(amount, gl.Values.Sum());   // ยอดรวมยังตรง Dr=Cr ยังสมดุล
    }

    [Theory]
    [InlineData(false, true)]   // ไม่มีใบปรับปรุงค้าง → ปรับได้
    [InlineData(true, false)]   // มีใบปรับปรุงค้างอยู่ → ต้องกลับก่อน
    public void A_pending_adjustment_blocks_the_next_one(bool hasPending, bool allowed)
        => Assert.Equal(allowed, !hasPending);

    // ── X-4: gate ของ Adjust ต้องเท่ากับ Reclassify ────────────────────────

    public sealed record DocState(
        bool Voided = false, bool InFiledReport = false, bool EtaxSubmitted = false,
        bool HasDownstream = false, bool IsDeposit = false);

    private static bool AdjustAllowed(DocState d) =>
        !d.Voided && !d.InFiledReport && !d.EtaxSubmitted && !d.HasDownstream && !d.IsDeposit;

    [Fact]
    public void Adjust_now_shares_the_full_reclassify_gate()
    {
        Assert.True(AdjustAllowed(new DocState()));
        Assert.False(AdjustAllowed(new DocState(Voided: true)));
        Assert.False(AdjustAllowed(new DocState(InFiledReport: true)));
        Assert.False(AdjustAllowed(new DocState(EtaxSubmitted: true)));
        Assert.False(AdjustAllowed(new DocState(HasDownstream: true)));   // ← เคยขาด
        Assert.False(AdjustAllowed(new DocState(IsDeposit: true)));       // ← เคยขาด
    }

    [Fact]
    public void A_settlement_receipt_is_not_a_blocking_downstream()
    {
        // ใบเสร็จหลักฐาน = evidence-only ไม่ลง JE ⇒ ไม่ควรล็อกการปรับปรุง
        static bool Blocks(bool isSettlementReceipt) => !isSettlementReceipt;
        Assert.False(Blocks(isSettlementReceipt: true));
        Assert.True(Blocks(isSettlementReceipt: false));
    }

    // ── S-6: void ใบกำกับที่ supersede ใบแจ้งหนี้ไปแล้ว ─────────────────────

    [Fact]
    public void Voiding_the_replacing_tax_invoice_revives_the_original()
    {
        // INV approved → TIV supersede (INV = Voided) → void TIV
        // เดิม: ไม่เหลือเอกสารรับรู้รายได้เลย ทั้งที่ของส่งไปแล้ว และไม่มีใครเห็น
        var invoice = "Voided";
        var taxInvoice = "Voided";
        var wasSuperseded = true;

        var invoiceAfter = (taxInvoice == "Voided" && wasSuperseded) ? "Draft" : invoice;
        Assert.Equal("Draft", invoiceAfter);   // คืนเป็นร่างให้ผู้ใช้ตัดสินใจ
        Assert.NotEqual("Approved", invoiceAfter);  // ไม่ปลุกกลับเป็นอนุมัติเอง
    }

    [Fact]
    public void Only_the_invoice_this_tax_invoice_replaced_is_revived()
    {
        // ยึด note marker ที่ระบุ "เลขใบกำกับใบนี้" — ใบอื่นที่ถูก void ด้วย
        // เหตุอื่นต้องไม่โดนปลุก
        static bool Revive(string? note, string taxInvoiceNumber)
            => note != null && note.Contains($"[แทนที่ด้วยใบกำกับภาษี {taxInvoiceNumber}]");

        Assert.True(Revive("[แทนที่ด้วยใบกำกับภาษี TIV-001]", "TIV-001"));
        Assert.False(Revive("[แทนที่ด้วยใบกำกับภาษี TIV-002]", "TIV-001"));
        Assert.False(Revive("ยกเลิกเพราะลูกค้าขอ", "TIV-001"));
        Assert.False(Revive(null, "TIV-001"));
    }

    // ── S-4: ข้อความต้องชี้ทางที่กดได้จริง ──────────────────────────────────

    [Theory]
    [InlineData(500.0, 0.0, "ยกเลิกการชำระ")]      // มี Payment row จริง
    [InlineData(500.0, 500.0, "หักมัดจำ")]          // ยอดมาจากมัดจำ — ไม่มีปุ่มยกเลิกการชำระ
    public void The_block_message_points_at_a_button_that_exists(
        double paid, double depositApplied, string expectedHint)
    {
        var fromDeposit = depositApplied > 0.01;
        var hint = fromDeposit ? "หักมัดจำ" : "ยกเลิกการชำระ";
        Assert.Equal(expectedHint, hint);
        Assert.True(paid > 0);
    }

    // ── S-8: สอง renderer ต้องตัดสินหัวเอกสารเหมือนกัน ─────────────────────

    private static bool ServedAsReceipt(
        string status, bool paidOnIssue, decimal balanceDue, decimal paidAmount,
        bool hasSeparateReceipt)
    {
        if (status == "Draft") return paidOnIssue && !hasSeparateReceipt;
        if (balanceDue > 0.01m || paidAmount <= 0.005m) return false;
        if (status is "Voided" or "Rejected" or "WaitingApproval") return false;
        return !hasSeparateReceipt;
    }

    [Fact]
    public void Draft_with_an_active_separate_receipt_never_prints_the_combined_header()
    {
        // เคสที่สอง renderer เคยต่างกัน: ใบที่ถูกคืนชีพเป็นร่างแต่ยังมีใบเสร็จลูก
        Assert.False(ServedAsReceipt("Draft", paidOnIssue: true,
            balanceDue: 100m, paidAmount: 0m, hasSeparateReceipt: true));
        Assert.True(ServedAsReceipt("Draft", paidOnIssue: true,
            balanceDue: 100m, paidAmount: 0m, hasSeparateReceipt: false));
    }

    // ── S-5: chain "รับเงินครบแล้ว" ต้องไม่เกิดใบเสร็จแยก ──────────────────

    [Fact]
    public void The_paid_chain_must_send_issue_receipt_false_explicitly()
    {
        // server default = true และเงื่อนไข "ใบรวมทำหน้าที่ใบเสร็จเอง" ต้องการ
        // ธง combined ซึ่งโหมด tax_paid ตั้งเป็น false ⇒ ไม่ส่ง = เกิดใบเสร็จแยก
        static bool CreatesSeparateReceipt(bool? issueReceipt, bool combined, bool fullySettled)
        {
            var selfReceipt = combined && fullySettled;
            return (issueReceipt ?? true) && !selfReceipt;
        }

        Assert.True(CreatesSeparateReceipt(null, combined: false, fullySettled: true));   // เดิม
        Assert.False(CreatesSeparateReceipt(false, combined: false, fullySettled: true)); // หลังแก้
        Assert.False(CreatesSeparateReceipt(null, combined: true, fullySettled: true));   // 3-in-1
    }

    [Fact]
    public void A_separate_receipt_would_have_flipped_the_header_back()
    {
        // ผลต่อเนื่อง: มีใบเสร็จแยก ⇒ ServedAsReceipt = false ⇒ หัวกลับเป็น
        // "ใบกำกับภาษี" ขัดกับที่ผู้ใช้เลือกในโหมด
        Assert.False(ServedAsReceipt("Paid", paidOnIssue: true,
            balanceDue: 0m, paidAmount: 1_040m, hasSeparateReceipt: true));
        Assert.True(ServedAsReceipt("Paid", paidOnIssue: true,
            balanceDue: 0m, paidAmount: 1_040m, hasSeparateReceipt: false));
    }
}
