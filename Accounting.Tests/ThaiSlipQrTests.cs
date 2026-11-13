using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 264 แพ็ก P2 (คำตัดสิน 264-4-1/2) — QR "ตรวจสอบสลิป" (<see cref="ThaiSlipQr"/>) + ตารางธนาคาร (<see cref="ThaiBankCodes"/>) + ตัวถอดรูป (<see cref="SlipQrImageDecoder"/>) ·
/// payload จาก<b>สลิปจริง</b> 6 ใบ (<see cref="SlipCorpusRound264"/> · เลขอ้างอิงถูกแทน + CRC ใหม่) · ทิศตรงข้าม: CRC ผิด · QR พร้อมเพย์ชำระเงิน · รหัส API อื่น ⇒ null
/// </summary>
public class ThaiSlipQrTests
{
    [Theory]
    [InlineData(nameof(SlipCorpusRound264.KtbToKbank), "006", "A31790g468abc5efe")]
    [InlineData(nameof(SlipCorpusRound264.KtbToBbl), "006", "Aba79024680135e25")]
    [InlineData(nameof(SlipCorpusRound264.ScbPromptPayMobile), "014", "202790246jQb35TfUhiPQWcRw")]
    [InlineData(nameof(SlipCorpusRound264.ScbBillStore), "014", "202790246jQb3RTYU2OP8WcLg")]
    [InlineData(nameof(SlipCorpusRound264.ScbBillRefs), "014", "202790246j0WcRe9gIiPa0E0Z")]
    [InlineData(nameof(SlipCorpusRound264.KplusSameBank), "004", "016790246801ERT90209")]
    public void Payload_จากสลิปจริง_ได้ธนาคารผู้โอนและเลขอ้างอิง(string slip, string bank, string reference)
    {
        var p = ThaiSlipQr.Parse(Slip(slip).Qr);
        Assert.NotNull(p);
        Assert.Equal(bank, p!.SendingBankCode);
        Assert.Equal(reference, p.TransactionRef);   // ตัวพิมพ์เล็กคงตามจริง (กรุงไทยมีตัวเล็ก)
        Assert.NotNull(p.SendingBank);
    }

    [Fact]
    public void สลิปที่ไม่มี_QR_บนกระดาษจริง_ระบุไว้ในชุด()
    {
        // K+ จ่ายบัตร · ttb · KBIZ ×2 — ไม่มี QR (ตรวจจากรูปจริง) ⇒ ผู้เรียกได้ null
        Assert.Null(SlipCorpusRound264.KplusCardBill.Qr);
        Assert.Null(SlipCorpusRound264.TtbCardEnglish.Qr);
        Assert.Null(SlipCorpusRound264.KbizTransferMemo.Qr);
        Assert.Null(SlipCorpusRound264.KbizBillSso.Qr);
        Assert.Equal(6, SlipCorpusRound264.All.Count(x => x.Qr != null));
    }

    [Fact]
    public void CRC_ผิด_หรือถูกตัดทอน_null()
    {
        var good = SlipCorpusRound264.KtbToBbl.Qr!;
        Assert.NotNull(ThaiSlipQr.Parse(good));
        Assert.Null(ThaiSlipQr.Parse(good[..^1] + (good[^1] == '0' ? '1' : '0')));
        // แก้เลขอ้างอิงหนึ่งตัวโดยไม่คำนวณ CRC ใหม่ ⇒ null (ไม่เดา)
        Assert.Null(ThaiSlipQr.Parse(good.Replace("Aba79", "Aba78")));
        Assert.Null(ThaiSlipQr.Parse(good[..20]));
        Assert.Null(ThaiSlipQr.Parse(null));
        Assert.Null(ThaiSlipQr.Parse("   "));
        Assert.Null(ThaiSlipQr.Parse("ข้อความไทย9104ABCD"));
    }

    [Fact]
    public void QR_พร้อมเพย์สำหรับชำระเงิน_ไม่ใช่_QR_สลิป()
    {
        // EMVCo "000201…6304" (CRC ถูก) — QR ที่ร้านแสดงให้ลูกค้าสแกนจ่าย ไม่ใช่หลักฐานการโอน
        const string promptPayPay = "00020101021129370016A000000677010111011300668123456785802TH530376463045D82";
        Assert.Equal("5D82", ThaiSlipQr.Crc16(promptPayPay[..^4]));
        Assert.Null(ThaiSlipQr.Parse(promptPayPay));
    }

    [Fact]
    public void รหัส_API_อื่น_รหัสธนาคารไม่ใช่ตัวเลข_ประเทศอื่น_null_แม้_CRC_ถูก()
    {
        Assert.Null(ThaiSlipQr.Parse("0038000600000201030060217Aba79024680135e255102TH9104850C"));
        Assert.Null(ThaiSlipQr.Parse("0038000600000101030X60217Aba79024680135e255102TH91041A07"));
        Assert.Null(ThaiSlipQr.Parse("0038000600000101030060217Aba79024680135e255102US910443F7"));
    }

    [Fact]
    public void Crc16_ตรงกับ_CRC_บนสลิปจริงทุกใบ()
    {
        foreach (var s in SlipCorpusRound264.All.Where(x => x.Qr != null))
            Assert.Equal(s.Qr![^4..], ThaiSlipQr.Crc16(s.Qr[..^4]));
    }

    // ═══ ตัวถอดรูป: QR ที่สร้างจาก payload จริงด้วย QRCoder (ตัวสร้างเดิมของระบบ) ⇒ ถอดกลับได้ข้อความเดิม ═══

    [Fact]
    public void ถอดรูป_QR_กลับได้_payload_เดิม_และรูปที่ไม่มี_QR_ได้_null()
    {
        var payload = SlipCorpusRound264.KplusSameBank.Qr!;
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        var text = SlipQrImageDecoder.TryDecode(png);
        Assert.Equal(payload, text);
        Assert.Equal("004", ThaiSlipQr.Parse(text)!.SendingBankCode);

        Assert.Null(SlipQrImageDecoder.TryDecode(Array.Empty<byte>()));
        Assert.Null(SlipQrImageDecoder.TryDecode(new byte[] { 1, 2, 3, 4, 5 }));   // ไม่ใช่รูป ⇒ null ไม่โยน
    }

    // ═══ ThaiBankCodes ═══

    [Theory]
    [InlineData("กรุงไทย", "006")]
    [InlineData("กสิกรไทย", "004")]
    [InlineData("ธ.กสิกรไทย", "004")]
    [InlineData("Kasikornbank", "004")]
    [InlineData("กรุงเทพ", "002")]
    [InlineData("ttb", "011")]
    [InlineData("ธนาคารไทยพาณิชย์", "014")]
    public void บรรทัดชื่อธนาคารบนกระดาษจริง(string line, string code) => Assert.Equal(code, ThaiBankCodes.FromBankLine(line));

    [Theory]
    [InlineData("ttb credit card")]          // ปลายทางเป็นบัตร ไม่ใช่บรรทัดธนาคาร
    [InlineData("บจก. กรุงไทยการไฟฟ้า")]     // ชื่อบริษัทที่มีชื่อธนาคารปน
    [InlineData("2 Nov 26 11:44           KBIZ")]
    [InlineData("")]
    public void บรรทัดที่ไม่ใช่ชื่อธนาคารล้วน_null(string line) => Assert.Null(ThaiBankCodes.FromBankLine(line));

    [Fact]
    public void FromText_ช่องธนาคารของบัญชี_และไม่เดาเมื่อมีสองธนาคาร()
    {
        Assert.Equal("004", ThaiBankCodes.FromText("ธนาคารกสิกรไทย (KBANK)"));
        Assert.Equal("006", ThaiBankCodes.FromText("ธนาคารกรุงไทย (KTB)"));
        Assert.Equal("011", ThaiBankCodes.FromText("ธนาคารทหารไทยธนชาต"));
        Assert.Equal("025", ThaiBankCodes.FromText("กรุงศรีอยุธยา"));
        Assert.Null(ThaiBankCodes.FromText("กรุงไทย หรือ กสิกรไทย"));
        Assert.Null(ThaiBankCodes.FromText("Rattbon"));        // "ttb" กลางคำอังกฤษ ไม่นับ
        Assert.Null(ThaiBankCodes.FromText("ธนาคารนอกตาราง"));
        Assert.True(ThaiBankCodes.SameByTable("ธนาคารกสิกรไทย (KBANK)", "Kasikornbank"));
        Assert.False(ThaiBankCodes.SameByTable("KTB", "ไทยพาณิชย์"));
        Assert.Null(ThaiBankCodes.SameByTable("ธนาคารนอกตาราง", "KTB"));
    }

    private static SlipCorpusRound264.Slip Slip(string name)
        => (SlipCorpusRound264.Slip)typeof(SlipCorpusRound264).GetField(name)!.GetValue(null)!;
}
