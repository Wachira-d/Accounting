using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// สัญญาณที่เซิร์ฟเวอร์พูดออกมาเองว่า "ยังไม่แน่ใจ" ต้องหยุดการอนุมัติอัตโนมัติเสมอ
/// และทุกช่องทาง (เว็บ · LINE · มือถือ) ต้องใช้ตัวตัดสินตัวเดียวกัน
/// (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T5-03 · T4-15/16)
/// </summary>
public class OcrPostingReadinessTests
{
    [Fact]
    public void ใบปกติ_อนุมัติอัตโนมัติได้()
    {
        var v = OcrPostingReadiness.Evaluate("[Tier] Azure DI สำเร็จ\n[Field Confidence]\n  Total: 98%", hasUsableDate: true);
        Assert.True(v.CanAutoApprove);
        Assert.Null(v.Reason);
    }

    [Theory]
    [InlineData("[Σ-GAP] Σ บรรทัด 1,000.00 น้อยกว่ายอดก่อน VAT 1,050.00")]
    [InlineData("[FX-UNKNOWN] เอกสารสกุล USD แต่ยังไม่มีอัตราแลกเปลี่ยน")]
    [InlineData("[TAX-INV-PENDING] กระดาษเป็น Invoice ไม่ใช่ใบกำกับภาษี")]
    [InlineData("[WHT-CERT] หนังสือรับรอง 50 ทวิ ที่เราถูกหัก")]
    [InlineData("[DATE-UNKNOWN] อ่านวันที่บนกระดาษไม่ได้")]
    public void สัญญาณไม่แน่ใจ_ต้องหยุดการอนุมัติอัตโนมัติ(string note)
    {
        var v = OcrPostingReadiness.Evaluate(note, hasUsableDate: true);
        Assert.False(v.CanAutoApprove);
        Assert.False(string.IsNullOrWhiteSpace(v.Reason));
    }

    [Fact]
    public void ไม่มีวันที่_หยุดแม้หมายเหตุว่าง_แถวเก่าที่ยังไม่มีแท็ก()
    {
        var v = OcrPostingReadiness.Evaluate(null, hasUsableDate: false);
        Assert.False(v.CanAutoApprove);
        Assert.Contains("วันที่", v.Reason);
    }

    [Fact]
    public void เหตุผลต้องเป็นข้อความที่เอาไปโชว์ได้_ไม่ใช่รหัสแท็ก()
    {
        var v = OcrPostingReadiness.Evaluate("[Σ-GAP] x", true);
        Assert.DoesNotContain("[", v.Reason);
    }
}
