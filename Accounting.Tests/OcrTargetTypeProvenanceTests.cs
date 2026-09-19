using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ชนิดเอกสารที่จะสร้าง: ป้าย "AI ถูก" + สมุดที่มา** (ผลตรวจ 2026-09-18 · D3-4)
///
/// <para>ช่อง <c>TargetDocumentType</c> ถูกเขียนทับ 7 ชั้นในไปป์ไลน์เดียวแบบ "ใครมาหลังชนะ"
/// โดยไม่มีชั้นไหนบันทึกที่มา ⇒ (ก) หน้ารีวิวตอบไม่ได้ว่าค่ามาจากไหน (ข) เส้น 1-click
/// บันทึก <c>acceptedAi</c> โดยเทียบกับ<b>ค่าสุดท้าย</b>แทนคำตอบของ AI ⇒ label ปลอม</para>
/// </summary>
public class OcrTargetTypeProvenanceTests
{
    // ── ป้าย "AI ถูก" ─────────────────────────────────────────────────────

    [Fact]
    public void ชั้นหลังทับคำตอบ_AI_แล้วผู้ใช้กดสร้าง_ต้องไม่นับว่า_AI_ถูก()
    {
        // AI ตอบ PurchaseInvoice → ประวัติผู้ขายทับเป็น PaymentVoucher → ผู้ใช้กดสร้าง
        Assert.False(OcrAiLabelScope.AcceptedAi("PurchaseInvoice", "PaymentVoucher"));
    }

    [Fact]
    public void ไม่มีคำตอบของ_AI_ห้ามนับว่า_AI_ถูกไม่ว่ากรณีใด()
    {
        // AI ไม่ได้ตอบ / คำตอบไม่ผ่านด่านกันมั่ว ⇒ เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล
        // ห้ามตกเป็น "ผ่าน"
        Assert.False(OcrAiLabelScope.AcceptedAi(null, "Expense"));
        Assert.False(OcrAiLabelScope.AcceptedAi("", "Expense"));
        Assert.False(OcrAiLabelScope.AcceptedAi("   ", "Expense"));
        Assert.False(OcrAiLabelScope.AcceptedAi(null, null));
    }

    [Fact]
    public void ผู้ใช้สร้างตามคำตอบ_AI_ต้องนับว่า_AI_ถูก()
    {
        Assert.True(OcrAiLabelScope.AcceptedAi("PurchaseInvoice", "PurchaseInvoice"));
        // ชื่อ enum เดียวกันคนละตัวพิมพ์/มีช่องว่างติดมา = คำตอบเดียวกัน
        Assert.True(OcrAiLabelScope.AcceptedAi("purchaseinvoice", "PurchaseInvoice"));
        Assert.True(OcrAiLabelScope.AcceptedAi(" Buyer ", "Buyer"));
    }

    [Fact]
    public void ไม่มีค่าที่ผู้ใช้เลือก_ก็ห้ามนับว่า_AI_ถูก()
    {
        Assert.False(OcrAiLabelScope.AcceptedAi("Expense", null));
        Assert.False(OcrAiLabelScope.AcceptedAi("Expense", ""));
    }

    // ── สมุดที่มาของชนิดเอกสาร ────────────────────────────────────────────

    [Fact]
    public void ช่องชนิดเอกสารต้องมีชื่อกลาง_และ_canonical_ไม่เปลี่ยนชื่อมัน()
    {
        Assert.Equal("TargetDocumentType", OcrFieldKeys.TargetDocumentType);
        Assert.Equal(OcrFieldKeys.TargetDocumentType,
            OcrFieldKeys.Canonical(OcrFieldKeys.TargetDocumentType));
    }

    [Fact]
    public void สมุดต้องเก็บผู้เสนอทุกชั้น_เพื่อให้ตอบได้ว่ามาจากไหน()
    {
        // ใบจริงทรงที่พบบ่อย: ตัวอนุมานบทบาทตั้ง Expense (กติกา) → AI เสนอ
        // PurchaseInvoice → ประวัติผู้ขายทับเป็น PaymentVoucher
        var candidates = new[]
        {
            new OcrFieldCandidate(OcrFieldKeys.TargetDocumentType, "Expense",
                OcrFieldSource.Rule, 0.60m, "ค่าเริ่มต้นตามบทบาท"),
            new OcrFieldCandidate(OcrFieldKeys.TargetDocumentType, "PurchaseInvoice",
                OcrFieldSource.Ai, 0.80m, "AI จำแนกชนิดเอกสาร"),
            new OcrFieldCandidate(OcrFieldKeys.TargetDocumentType, "PaymentVoucher",
                OcrFieldSource.VendorHistory, 0.92m, "ประวัติ 12 เอกสารของผู้ขาย"),
        };

        var decision = OcrFieldArbiter.Decide(OcrFieldKeys.TargetDocumentType, candidates);

        Assert.NotNull(decision);
        // ผู้เสนอทุกรายต้องอยู่ในสมุด (ผู้ชนะ + ตัวเลือกที่แพ้) — ไม่ใช่เหลือแค่ค่าสุดท้าย
        Assert.Equal(2, decision!.Alternatives.Count);
        Assert.Contains(decision.Alternatives, a => a.Value == "Expense");
        Assert.Contains(decision.Alternatives, a => a.Value == "PurchaseInvoice");
        Assert.False(string.IsNullOrWhiteSpace(OcrFieldArbiter.SourceLabel(decision.Source)));
    }

    [Fact]
    public void ป้ายกำกับบนกระดาษต้องชนะประวัติผู้ขายและ_AI_ในสมุด()
    {
        // (สมุดยังไม่ใช่ตัวตัดสินของไปป์ไลน์ — แต่ลำดับความน่าเชื่อต้องถูกไว้ก่อน
        //  เพราะการย้ายตัวตัดสินคือขั้นถัดไปตามแผน §4 D1)
        var candidates = new[]
        {
            new OcrFieldCandidate(OcrFieldKeys.TargetDocumentType, "PaymentVoucher",
                OcrFieldSource.VendorHistory, 0.95m, "ประวัติผู้ขาย"),
            new OcrFieldCandidate(OcrFieldKeys.TargetDocumentType, "Deposit",
                OcrFieldSource.PaperLabel, 0.70m, "กระดาษเขียนว่ามัดจำพร้อมยอดเงิน"),
        };
        var decision = OcrFieldArbiter.Decide(OcrFieldKeys.TargetDocumentType, candidates);
        Assert.Equal("Deposit", decision!.Value);
        Assert.Equal(OcrFieldSource.PaperLabel, decision.Source);
    }
}
