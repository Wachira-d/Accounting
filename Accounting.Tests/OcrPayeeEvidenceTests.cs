using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>§65 ตรี(18) "พิสูจน์ผู้รับเงินได้ไหม" — ล็อก**สองทิศ**:
/// บิลที่พังต้องกลับมาถูก (ไม่ถูกดันเป็นใบรับรองแทนใบเสร็จ) **และ**
/// ใบที่ระบุผู้รับไม่ได้จริงต้องยังถูกดักอยู่ (ห้ามกลายเป็นการปิดด่านทิ้ง)</summary>
public class OcrPayeeEvidenceTests
{
    // ── ใบจริงที่ผู้ใช้รายงาน 2026-09-11 (บิลเงินสดร้านบริการ ชลบุรี) ──
    [Fact]
    public void บิลเงินสดมีชื่อและที่อยู่ครบ_ต้องถือว่าพิสูจน์ผู้รับเงินได้()
    {
        var r = OcrPayeeEvidence.Evaluate(
            vendorTaxId: null, vendorName: "อ๊อฟ บริการ",
            vendorAddress: "177/18 ม.5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี", vendorPhone: null);
        Assert.Equal(OcrPayeeProof.Identified, r.Level);
    }

    [Fact]
    public void มีชื่อและเบอร์โทรแต่ไม่มีที่อยู่_ก็พิสูจน์ได้()
        => Assert.Equal(OcrPayeeProof.Identified,
            OcrPayeeEvidence.Evaluate(null, "ร้านวัสดุพรชัย", null, "081-234-5678").Level);

    [Fact]
    public void เลขผู้เสียภาษีถูกต้อง_พิสูจน์ได้ทันทีแม้ไม่มีที่อยู่()
        => Assert.Equal(OcrPayeeProof.Identified,
            OcrPayeeEvidence.Evaluate("0105558123456", "บจก. ทดสอบ", null, null).Level);

    // ── ทิศที่ต้องยังดักอยู่ (ห้ามปิดด่านทิ้ง) ──
    [Fact]
    public void ไม่มีทั้งชื่อและที่อยู่_ต้องเป็นใบรับรองแทนใบเสร็จ()
    {
        var r = OcrPayeeEvidence.Evaluate(null, null, null, null);
        Assert.Equal(OcrPayeeProof.Unidentified, r.Level);
    }

    [Fact]
    public void ชื่อที่อ่านไม่ออกเป็นเศษตัวอักษร_ไม่นับว่ามีชื่อ()
        => Assert.Equal(OcrPayeeProof.Unidentified,
            OcrPayeeEvidence.Evaluate(null, "-", "  ", null).Level);

    [Fact]
    public void มีแต่ชื่อลอยๆ_ต้องเป็นก้ำกึ่งให้คนตัดสิน_ไม่ใช่เปลี่ยนชนิดเอกสารเงียบๆ()
        => Assert.Equal(OcrPayeeProof.Weak,
            OcrPayeeEvidence.Evaluate(null, "วินมอเตอร์ไซค์ปากซอย", null, null).Level);

    [Fact]
    public void ที่อยู่สั้นเกินไป_ยังไม่พอชี้ตัวผู้รับเงิน()
        => Assert.Equal(OcrPayeeProof.Weak,
            OcrPayeeEvidence.Evaluate(null, "ร้านป้าแดง", "ชลบุรี", null).Level);

    // ── บาร์โค้ดสินค้าที่ OCR หยิบมาเป็น "เลขผู้เสียภาษี" ต้องไม่ถูกนับเป็นหลักฐาน ──
    [Fact]
    public void บาร์โค้ดสินค้าEAN13_ต้องไม่ถูกนับเป็นเลขผู้เสียภาษี()
    {
        var r = OcrPayeeEvidence.Evaluate("8859991446166", "ร้านค้า", null, null);
        Assert.Equal(OcrPayeeProof.Weak, r.Level);
    }

    [Fact]
    public void เหตุผลต้องเป็นข้อความไทยที่เอาไปโชว์ผู้ใช้ได้()
    {
        foreach (var r in new[]
        {
            OcrPayeeEvidence.Evaluate(null, "ร้าน ก", "123/4 ถนนสุขุมวิท กรุงเทพ", null),
            OcrPayeeEvidence.Evaluate(null, "ร้าน ก", null, null),
            OcrPayeeEvidence.Evaluate(null, null, null, null),
        })
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
    }
}
