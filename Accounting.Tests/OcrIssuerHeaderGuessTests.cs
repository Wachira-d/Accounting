using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>เดาชื่อผู้ออกบิลจากกรอบบนของแบบฟอร์มพิมพ์สำเร็จ — ค่าเสนอความมั่นใจต่ำ (0.35)</summary>
public class OcrIssuerHeaderGuessTests
{
    private static readonly OcrOurIdentity Us = new("หจก. แอม แฮปปี้เนส", null, "0203562005871", "00000", null);

    private const string CashBill =
        "อ๊อฟ พิการ\nเล่มที่\nเลขที่\n177/18 ม.5 ต.บางพระ อ.ศรีราชา จ. ชลบุรี\nบิลเงินสด\nCASHSALE\n"
        + "บจก. แอมแฮปปี้เนส (สำนักงานใหญ่)\nนาม\n";

    [Fact]
    public void บิลเงินสดจริง_ต้องเสนอชื่อร้านจากกรอบบน()
        => Assert.Equal("อ๊อฟ พิการ", OcrIssuerHeaderGuess.FromTopBox(CashBill, Us));

    [Fact]
    public void บรรทัดที่อยู่และป้ายแบบฟอร์ม_ต้องไม่ถูกเสนอ()
        // ถ้าบรรทัดแรกเป็นที่อยู่/เล่มที่ ต้องข้าม ไม่ใช่หยิบมาเป็นชื่อ
        => Assert.Null(OcrIssuerHeaderGuess.FromTopBox(
            "เล่มที่\nเลขที่\n177/18 ม.5 ต.บางพระ\nบิลเงินสด\n", Us));

    [Fact]
    public void กระดาษที่หัวแบบฟอร์มอยู่บรรทัดแรก_ไม่มีกรอบบน_ต้องไม่เดา()
        => Assert.Null(OcrIssuerHeaderGuess.FromTopBox("ใบกำกับภาษี\nผู้ขาย บริษัท ก จำกัด\n", Us));

    [Fact]
    public void ชื่อเราเองในกรอบบน_ต้องไม่ถูกเสนอเป็นผู้ขาย()
        => Assert.Null(OcrIssuerHeaderGuess.FromTopBox("หจก. แอม แฮปปี้เนส\nใบเสร็จรับเงิน\n", Us));

    [Fact]
    public void ไม่ใช่แบบฟอร์มพิมพ์สำเร็จ_ต้องไม่เดา()
        => Assert.Null(OcrIssuerHeaderGuess.FromTopBox("รายการสินค้า\nน้ำดื่ม 20\nรวม 20\n", Us));

    [Fact]
    public void ความมั่นใจต้องต่ำกว่าเกณฑ์ไฮไลต์และเกณฑ์สร้างContact()
        => Assert.True(OcrIssuerHeaderGuess.Confidence < 0.5);
}
