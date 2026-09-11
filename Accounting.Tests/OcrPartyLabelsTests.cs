using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ป้ายฝั่งผู้ซื้อ/ผู้ขายบนกระดาษ — ตัวค้นตัวเดียวของระบบ
///
/// ═══ ที่มา ═══ เล่มบิล/ใบเสร็จพิมพ์สำเร็จของไทยใช้คำว่า “นาม / NAME” สำหรับ
/// <b>ลูกค้า</b> ส่วนร้านผู้ออกบิลอยู่ในกรอบบนที่ไม่มีป้ายอะไรเลย — รายการคำเดิม
/// (ซึ่งมีอยู่ <b>2 ชุดในคนละไฟล์และไม่ตรงกัน</b>) ไม่มีคำนี้ ⇒ กระดาษตระกูลนี้
/// ทั้งตระกูลไม่มีป้ายฝั่งผู้ซื้อเลยในสายตาระบบ
/// </summary>
public class OcrPartyLabelsTests
{
    [Fact]
    public void นามเดี่ยวๆบนแบบฟอร์ม_เป็นป้ายฝั่งผู้ซื้อ()
        => Assert.True(OcrPartyLabels.FindBuyer("บจก. ทดสอบ จำกัด\nนาม\nวันที่\n") >= 0);

    [Theory]
    [InlineData("ผู้มีอำนาจลงนาม")]      // “นาม” อยู่ท้ายคำอื่น
    [InlineData("ชื่อ-นามสกุล")]          // “นาม” อยู่หน้าคำอื่น
    [InlineData("นามบัตรของบริษัท")]
    public void นามที่เป็นส่วนของคำอื่น_ไม่ใช่ป้าย(string text)
        // ภาษาไทยเขียนติดกัน — Contains กับคำสั้นคือระเบิดเวลา
        => Assert.True(OcrPartyLabels.FindBuyer(text) < 0);

    [Fact]
    public void NAME_บรรทัดของตัวเอง_เป็นป้าย()
        => Assert.True(OcrPartyLabels.FindBuyer("ACME CO\nNAME\nDATE\n") >= 0);

    [Fact]
    public void NAME_ที่เป็นส่วนของหัวคอลัมน์อื่น_ไม่ใช่ป้าย()
        // “VENDOR NAME” คือหัวตารางฝั่งผู้ขาย — ถ้านับเป็นป้ายผู้ซื้อจะกลับทิศทั้งใบ
        => Assert.True(OcrPartyLabels.FindBuyer("VENDOR NAME\nACME CO\n") < 0);

    [Fact]
    public void ป้ายเดิมที่เคยใช้ได้_ต้องยังใช้ได้()
    {
        Assert.True(OcrPartyLabels.FindBuyer("ผู้ซื้อ: บริษัท ก") >= 0);
        Assert.True(OcrPartyLabels.FindBuyer("Bill To: ACME") >= 0);
        Assert.True(OcrPartyLabels.FindSeller("ผู้ขาย: บริษัท ข") >= 0);
    }

    [Fact]
    public void CUSTOMER_COPY_บนสลิปบัตร_ไม่ใช่ป้ายผู้ซื้อ()
        => Assert.True(OcrPartyLabels.FindBuyer("MERCHANT COPY\nCUSTOMER COPY\n") < 0);

    [Fact]
    public void กลบnoiseแล้วตำแหน่งป้ายจริงต้องไม่เลื่อน()
    {
        // กลบด้วยช่องว่างความยาวเท่าเดิม — ถ้าตัดทิ้ง index ของป้ายจริงจะเพี้ยน
        // แล้วการวัด “ป้ายอยู่ใกล้ชื่อไหม” พังทั้งหน้า
        const string t = "CUSTOMER COPY\nผู้ซื้อ\nบริษัท ก";
        Assert.Equal(t.IndexOf("ผู้ซื้อ", System.StringComparison.Ordinal),
            OcrPartyLabels.FindBuyer(t));
    }

    [Fact]
    public void ReceivedFrom_คือฝั่งผู้จ่าย_ไม่ใช่ผู้ขาย()
    {
        // เดิม "From" อยู่ฝั่งผู้ขาย ⇒ "Received From: <เรา>" ทำให้เรากลายเป็นผู้ขายของใบเสร็จที่ออกให้เรา
        const string t = "ใบเสร็จรับเงิน / RECEIPT\nได้รับเงินจาก / Received From: หจก. แอม แฮปปี้เนส\n";
        Assert.True(OcrPartyLabels.FindBuyer(t) >= 0);
        Assert.True(OcrPartyLabels.FindSeller(t) < 0);
    }

    [Fact]
    public void รหัสลูกค้าในกล่องผู้ออกใบ_ไม่ใช่ป้ายผู้ซื้อ()
        => Assert.True(OcrPartyLabels.FindBuyer("หจก. แอม แฮปปี้เนส\nTAX INVOICE\nCustomer Code: C-0012\nรหัสลูกค้า: 00123\n") < 0);

    [Fact]
    public void FindAll_คืนทุกตำแหน่ง_ไม่ใช่แค่ตัวแรก()
    {
        const string t = "ศูนย์บริการลูกค้า 02-000-0000\nบริษัท ผู้ขาย จำกัด\n...\nลูกค้า: หจก. แอม แฮปปี้เนส\n";
        var (buyers, _) = OcrPartyLabels.FindAll(t);
        Assert.Single(buyers);                                   // "ศูนย์บริการลูกค้า" ถูกกลบเป็น noise
        Assert.Equal(t.IndexOf("ลูกค้า:", System.StringComparison.Ordinal), buyers[0]);
    }

    [Fact]
    public void ผู้รับเงินท้ายบิล_ต้องไม่ถูกนับเป็นป้ายผู้ขาย()
        // ความหมายตรง แต่เป็นช่อง**ลายเซ็นท้ายบิล** — ถ้านับ จุดยึดฝั่งผู้ขายจะถูก
        // ลากไปท้ายหน้า แล้วชื่อผู้ขายจะถูกหยิบจากบรรทัดล่างสุดของกระดาษ
        => Assert.True(OcrPartyLabels.FindSeller("รวมเงิน 3500\nผู้รับเงิน/COLLECTOR\n") < 0);
}
