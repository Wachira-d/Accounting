using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เจตนา "รับเงินครบแล้ว ณ วันออก" (PaidOnIssue) กับหัวเอกสาร
///
/// ที่มา (ผู้ใช้): แปลงใบแจ้งหนี้ → เลือก "ใบกำกับภาษี/ใบเสร็จรับเงิน — รับเงิน
/// ครบแล้ว" แต่ใบร่างพิมพ์หัว "ใบกำกับภาษี" เฉย ๆ และเปิดแก้ใหม่ตัวเลือกหาย —
/// เพราะโหมด tax_paid อยู่แค่ในฟอร์ม+chain ฝั่ง client ไม่เคยบันทึกลงเอกสาร
///
/// กติกาหลังแก้ (mirror ของ ComputeServedAsReceipt / ResolveServedAsReceiptAsync):
///   Draft  → ใช้ "เจตนา" (ใบร่างเลข DRAFT ไม่ใช่เอกสารตามกฎหมาย —
///            หลัก Draft PDF = Approved PDF ตัวอย่างต้องตรงกับใบจริงที่จะออก)
///   หลังอนุมัติ → ใช้ "การชำระจริง" เท่านั้น (อนุมัติแล้วแต่ยังไม่บันทึกชำระ
///            ห้ามพิมพ์ "ใบเสร็จรับเงิน" — จะกลายเป็นหลักฐานรับเงินเท็จ)
/// </summary>
public class PaidOnIssueHeaderTests
{
    /// <summary>mirror ของ DocumentService.ComputeServedAsReceipt หลังแก้
    ///
    /// <para>ด่าน "รับเงินวันเดียวกับวันที่บนใบ" (ม.105 — เพิ่ม 2026-09-10) ถูก
    /// ล็อกไว้ที่ <c>CombinedReceiptSameDayTests</c> ไฟล์นี้จึงสมมติว่ารับเงิน
    /// วันเดียวกันเสมอ เพื่อให้เทสต์ชุดนี้ยังตอบคำถามเดิม (เจตนา PaidOnIssue)</para></summary>
    private static bool Served(
        string docType, string status, bool paidOnIssue,
        decimal balanceDue, decimal paidAmount, bool hasSeparateReceipt,
        bool settledSameDay = true)
    {
        if (docType != "TaxInvoice") return false;
        if (status == "Draft") return paidOnIssue && !hasSeparateReceipt;
        if (balanceDue > 0.01m || paidAmount <= 0.005m) return false;
        if (status is "Voided" or "Rejected" or "WaitingApproval") return false;
        if (!settledSameDay) return false;
        return !hasSeparateReceipt;
    }

    [Fact]
    public void Draft_with_paid_intent_prints_the_combined_header()
    {
        // เคสจริง: ใบแปลง 301,444.42 ยังเป็นร่าง — เลือกโหมดรับเงินครบแล้ว
        Assert.True(Served("TaxInvoice", "Draft", paidOnIssue: true,
            balanceDue: 301444.42m, paidAmount: 0m, hasSeparateReceipt: false));
    }

    [Fact]
    public void Draft_without_intent_stays_plain_tax_invoice()
    {
        Assert.False(Served("TaxInvoice", "Draft", false, 301444.42m, 0m, false));
    }

    [Fact]
    public void Approved_but_unpaid_never_prints_receipt_even_with_intent()
    {
        // อนุมัติแล้วแต่ chain บันทึกชำระล้ม — หัวห้ามอ้างว่ารับเงินแล้ว
        Assert.False(Served("TaxInvoice", "Approved", paidOnIssue: true,
            balanceDue: 301444.42m, paidAmount: 0m, hasSeparateReceipt: false));
    }

    [Fact]
    public void Fully_paid_prints_combined_regardless_of_intent_flag()
    {
        Assert.True(Served("TaxInvoice", "Paid", false, 0m, 301444.42m, false));
        Assert.True(Served("TaxInvoice", "Paid", true, 0m, 301444.42m, false));
    }

    [Fact]
    public void Payment_on_another_day_never_prints_the_combined_header()
    {
        // ผู้ใช้รายงาน 2026-09-10: ใบ 5 ส.ค. รับเงินคนละวัน — ใบเสร็จตัวจริงคือ REC
        // ที่ลงวันที่รับเงิน ไม่ใช่หัวบนใบเดิม (รายละเอียดใน CombinedReceiptSameDayTests)
        Assert.False(Served("TaxInvoice", "Paid", false, 0m, 111800m, false, settledSameDay: false));
    }

    [Fact]
    public void Separate_receipt_wins_over_intent()
    {
        // มีใบเสร็จแยกอ้างใบนี้แล้ว — หัวรวมจะซ้ำหน้าที่กับใบเสร็จ
        Assert.False(Served("TaxInvoice", "Draft", true, 100m, 0m, hasSeparateReceipt: true));
        Assert.False(Served("TaxInvoice", "Paid", false, 0m, 100m, hasSeparateReceipt: true));
    }

    [Theory]
    [InlineData("Invoice")]
    [InlineData("Receipt")]
    public void Other_document_types_are_untouched(string type)
        => Assert.False(Served(type, "Draft", true, 0m, 0m, false));

    /// <summary>server เก็บเจตนาเฉพาะชนิดที่มีความหมาย — mirror ของ guard
    /// ใน Create/UpdateDocumentAsync</summary>
    [Theory]
    [InlineData("TaxInvoice", true, true)]
    [InlineData("Invoice", true, true)]      // 3-in-1 (invoice_tax_paid)
    [InlineData("Receipt", true, false)]     // ใบเสร็จรับเงินอยู่แล้ว — ไม่มีความหมาย
    [InlineData("Expense", true, false)]
    [InlineData("TaxInvoice", false, false)]
    public void Intent_is_persisted_only_for_sales_invoice_types(
        string docType, bool requested, bool expectedStored)
    {
        var stored = requested && docType is "TaxInvoice" or "Invoice";
        Assert.Equal(expectedStored, stored);
    }
}
