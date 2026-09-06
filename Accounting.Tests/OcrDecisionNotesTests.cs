using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำตัดสินเกี่ยวกับ "ตัวกระดาษ" อยู่ใน <c>ProcessingNotes</c> เป็นสตริงล้วน (ไม่มีคอลัมน์
/// เก็บแยก) — เส้น "ไฟล์ซ้ำ" เคยเขียนทับทั้งก้อน ⇒ อัปไฟล์เดิมซ้ำแล้วใบกำกับอย่างย่อ
/// กลับมาเคลมภาษีซื้อได้ (ผลตรวจ OCR 2026-09-06 · T5-N1)
/// </summary>
public class OcrDecisionNotesTests
{
    private const string Notes = """
    [Tier] Azure DI สำเร็จ (2 หน้า)
    [VAT-CLAIM] ใบกำกับภาษีอย่างย่อ §82/5(2) — เคลมภาษีซื้อไม่ได้
    [Field Confidence]
      DocumentNumber: 95%
    [TAX-INV-PENDING] กระดาษเป็น Invoice ไม่ใช่ใบกำกับภาษี — VAT พักไว้ (11640)
    [Reasoning] vendor canon → contact abc
    [Σ-GAP] Σ บรรทัด 1,000.00 น้อยกว่ายอดก่อน VAT 1,050.00
    """;

    [Fact]
    public void คัดเฉพาะบรรทัดที่เป็นคำตัดสิน_ไม่เอา_diagnostic()
    {
        var kept = OcrScanSnapshot.DecisionNotes(Notes);
        Assert.Contains("[VAT-CLAIM]", kept);
        Assert.Contains("[TAX-INV-PENDING]", kept);
        Assert.Contains("[Σ-GAP]", kept);
        Assert.DoesNotContain("[Tier]", kept);
        Assert.DoesNotContain("Field Confidence", kept);
        Assert.DoesNotContain("[Reasoning]", kept);
        Assert.Equal(3, kept.Split('\n').Length);
    }

    [Fact]
    public void ไม่มีคำตัดสิน_คืนสตริงว่าง()
    {
        Assert.Equal(string.Empty, OcrScanSnapshot.DecisionNotes("[Tier] ok\n[Reasoning] x"));
        Assert.Equal(string.Empty, OcrScanSnapshot.DecisionNotes(null));
        Assert.Equal(string.Empty, OcrScanSnapshot.DecisionNotes("   "));
    }

    [Fact]
    public void ProcessingNotes_ยังอยู่ใน_deny_list_ของตัวคัดลอก()
    {
        // ตั้งใจ: หมายเหตุของ "การอัปโหลดครั้งนั้น" (tier/engine) ห้ามคัดลอก —
        // เฉพาะบรรทัดคำตัดสินเท่านั้นที่ถูกยกไปโดย DecisionNotes
        Assert.Contains(nameof(Accounting.Models.Entities.OcrScanResult.ProcessingNotes),
            OcrScanSnapshot.RowIdentityFields);
    }
}
