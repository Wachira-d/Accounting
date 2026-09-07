using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวตัดสิน "อนุมัติเองได้ไหม" ต้องเห็นสัญญาณ **ชุดเดียวกับที่หน้าเว็บเห็น**
///
/// ═══ ที่มา (ผลตรวจทีม E · SYSTEM_AUDIT_2026-09-07.md E-03) ═══
/// doc-comment ของ <c>OcrPostingReadiness</c> เขียนเองว่าเป็น "ตัวตัดสินตัวเดียว
/// ของทุกช่องทาง" แต่มันดูแค่ 5 แท็กใน <c>ProcessingNotes</c> ขณะที่เว็บมีเกณฑ์
/// เพิ่มอีกชุดใน JS (<c>complianceIssues</c> ระดับ error + เกรดคุณภาพ D) ⇒ ใบที่
/// เว็บ <b>ไม่มีแม้ช่องให้ติ๊ก</b> ("ผู้ซื้อในเอกสารไม่ตรงกับบริษัท — อาจเป็น
/// เอกสารของบริษัทอื่น") บน LINE กลับขึ้นปุ่มเขียว "✅ อนุมัติเลย" ให้กดปุ่มเดียว
/// ลง JE + เข้ารายงานภาษีซื้อ
/// </summary>
public class OcrPostingReadinessComplianceTests
{
    [Fact]
    public void ใบที่มีข้อผิดพลาดระดับerror_ต้องอนุมัติเองไม่ได้()
    {
        var v = OcrPostingReadiness.Evaluate(
            processingNotes: null, hasUsableDate: true,
            issueSeverities: new[] { "warning", "error" }, qualityLetter: "A");
        Assert.False(v.CanAutoApprove);
        Assert.Contains("สรรพากร", v.Reason);
    }

    [Fact]
    public void ยังไม่ได้ประเมิน_ต้องไม่ถูกอ่านว่าไม่มีปัญหา()
    {
        // สัญญาเดียวกับหน้าเว็บ: null = "ยังไม่ได้ตรวจ" ไม่ใช่ "ผ่าน"
        var v = OcrPostingReadiness.Evaluate(null, true, issueSeverities: null, qualityLetter: "A");
        Assert.False(v.CanAutoApprove);
    }

    [Fact]
    public void เกรดคุณภาพD_ต้องอนุมัติเองไม่ได้()
    {
        var v = OcrPostingReadiness.Evaluate(null, true, Array.Empty<string>(), "D");
        Assert.False(v.CanAutoApprove);
        Assert.Contains("ถ่ายใหม่", v.Reason);
    }

    [Fact]
    public void ใบที่สะอาดครบทุกสัญญาณ_ยังอนุมัติเองได้เหมือนเดิม()
    {
        var v = OcrPostingReadiness.Evaluate(null, true, new[] { "warning" }, "B");
        Assert.True(v.CanAutoApprove);
        Assert.Null(v.Reason);
    }

    [Fact]
    public void แท็กที่บล็อกอยู่แล้ว_ยังบล็อกก่อนถึงด่านใหม่()
    {
        var v = OcrPostingReadiness.Evaluate("[Σ-GAP] ...", true, Array.Empty<string>(), "A");
        Assert.False(v.CanAutoApprove);
        Assert.Contains("ผลรวมรายการ", v.Reason);
    }

    [Fact]
    public void WhtCert_เป็นแท็กที่บล็อกและต้องมีคำอธิบาย()
    {
        // ปุ่มหายโดยไม่มีคำเตือนอธิบาย = silent no-op — เหตุผลต้องเอาไปโชว์ได้
        var v = OcrPostingReadiness.Evaluate("[WHT-CERT] ...", true, Array.Empty<string>(), "A");
        Assert.False(v.CanAutoApprove);
        Assert.False(string.IsNullOrWhiteSpace(v.Reason));
    }
}
