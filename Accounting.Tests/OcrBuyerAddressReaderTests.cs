using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// <see cref="OcrBuyerAddressReader"/> — ที่อยู่ผู้ซื้อจากข้อความล้วน (เส้น python/Tesseract ที่ไม่มีโครงสร้าง)
///
/// <para>═══ ที่มา (รอบ 190 · เจ้าของข้อ 11 "local OCR จับที่อยู่ผู้ซื้อไม่ได้") ═══ ทั้งสองใบ Azure
/// อ่านที่อยู่ผู้ซื้อถูก (ช่อง <c>CustomerAddress</c> ของโมเดล Azure) แต่ฝั่งเรา<b>ไม่มีตัวอ่านที่อยู่ผู้ซื้อ</b>
/// และ <c>VendorAddressRegex</c> หยิบป้าย "ที่อยู่" ตัวแรกของหน้า — ซึ่งบนใบ Radisson คือ
/// “ที่อยู่ Address 202/24 …” ของ<b>ผู้ซื้อ</b> ⇒ ที่อยู่เราไปอยู่ช่องผู้ขาย ช่องผู้ซื้อว่าง</para>
///
/// <para>เทสต์สองครึ่ง: ใบจริงสองใบต้องได้ที่อยู่ผู้ซื้อ · ใบที่ไม่มีป้ายผู้ซื้อ / ป้าย "ที่อยู่" ของผู้ขาย /
/// ข้อความที่ไม่ใช่ที่อยู่ ต้อง<b>ไม่ถูกหยิบ</b> (ไม่มีหลักฐาน = ไม่เดา)</para>
/// </summary>
public class OcrBuyerAddressReaderTests
{
    private const string OurTaxId = "0203562005871";

    // ข้อความถอดจากกระดาษจริง (erp-review/2026-09-24/BRIEF.md)
    private const string WinePro =
        "Wine Pro Co.,Ltd. Branch 00012\n12/861 Moo 15 Bangkaew,\nBangplee, Samutprakarn 10540\n"
        + "Tel : 02-100-6401\nVAT Registration No.: 0105555175590\nPOS Terminal ID.: E051120003A1433\n"
        + "Receipt / Tax Invoice (Original)\nใบเสร็จรับเงิน/ใบกำกับภาษี (ต้นฉบับ)\nTax Inv No.:  BA2609-569\n"
        + "Slip: 0000000BN2000000413\nStaff: Kade\nDate: 18/09/26 6:35 PM\nCustomer Info. [CZBNG2600843]\n"
        + "หจก.แอม แฮปปี้เนส\nVAT Reg. No.: 0203562005871\nBranch: สำนักงานใหญ่\nAddress\n"
        + "202/24 หมู่ที่ 5 ซอยบ้านห้วยกุ่ม 4 ต.บาง\nพระ อ.ศรีราชา จ.ชลบุรี 20110\n"
        + "Description                                   Amount\nBELCOLLE Moscato d' Asti DOCG\n"
        + "  1 bottle x 524.00                        524.00 V\nTotal                                    3,593.00\n";

    private const string Radisson =
        "Radisson RESORT & SPA HUA HIN\nDestination Resorts Co.,Ltd  Head Office\n"
        + "200 Justmine International Tower Building, Unit2302A, 23Floor Moo 8 Chaeng Watthana Road,\n"
        + "Pak Kret, Nonthaburi   Branch Tax Invoice is Issued no. 8\n"
        + "854/2 Buriram Road, Cha-Am, Petchburi 76120 Thailand   Tel (66 32) 708 300 Fax (66 32) 708310\n"
        + "บริษัท เดสติเนชั่น รีสอร์ทส์ จำกัด  สำนักงานใหญ่ เลขที่ 200 อาคารจัสมินอินเตอร์เนชั่นแนลทาวเวอร์ ห้อง 2302A ชั้น 23\n"
        + "หมู่ 8 ถ.แจ้งวัฒนะ ต.ปากเกร็ด อ.ปากเกร็ด จ.นนทบุรี  สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8\n"
        + "854/2 ถนนบุรีรัมย์ ต.ชะอำ อ.ชะอำ จ.เพชรบุรี 76120\n"
        + "ใบเสร็จรับเงิน/ใบกำกับภาษี  OFFICIAL RECEIPT / TAX INVOICE          วันที่ DATE 12/09/26\n"
        + "เลขประจำตัวผู้เสียภาษี / TAX ID. 0105551136085\n"
        + "เล่มที่ BOOK NO. 066                                             เลขที่ SERIAL NO. 3267\n"
        + "ได้รับเงินจาก Receipt From  หจก. แอม แฮปปี้เนส (สำนักงานใหญ่)\n"
        + "ที่อยู่ Address  202/24 หมู่ที่5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110\n"
        + "เลขประจำตัวผู้เสียภาษีอากร TAX ID NUMBER  0203562005871   ☑ สำนักงานใหญ่ Head Office  ☐ สาขา Branch\n"
        + "รายการ Description                                              จำนวนเงิน Amount\n"
        + "ค่าอาหารและเครื่องดื่ม                                                   1,705.00\n";

    // ═════════ ครึ่งแรก: ใบจริงที่ local อ่านไม่ได้ → ต้องได้ ═════════

    [Theory]
    [InlineData(OurTaxId)]
    [InlineData(null)]
    public void WinePro_ที่อยู่ใต้ป้าย_Address_สองบรรทัด_ต่อคำที่เครื่องพิมพ์ตัดกลาง(string? buyerTaxId)
    {
        var r = OcrBuyerAddressReader.Read(WinePro, buyerTaxId);
        Assert.True(r.Found);
        Assert.Equal("202/24 หมู่ที่ 5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110", r.Address);
        Assert.True(r.Confidence < 0.85m, "อ่านจากข้อความล้วนไม่มีพิกัด ⇒ ต้องขึ้นไฮไลต์");
    }

    [Theory]
    [InlineData(OurTaxId)]
    [InlineData(null)]
    public void Radisson_ป้ายสองภาษา_ที่อยู่_Address_ในบรรทัดเดียว(string? buyerTaxId)
    {
        var r = OcrBuyerAddressReader.Read(Radisson, buyerTaxId);
        Assert.True(r.Found);
        Assert.Equal("202/24 หมู่ที่5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110", r.Address);
    }

    [Fact]
    public void ป้ายช่องอื่นต่อท้ายบรรทัดที่อยู่_ต้องตัดออก()
    {
        const string text = "ใบกำกับภาษี\nลูกค้า: บริษัท เอบีซี จำกัด\n"
            + "ที่อยู่: 99/1 ถ.สุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพฯ 10110 โทร 02-123-4567\n"
            + "รายการ  จำนวนเงิน\n";
        var r = OcrBuyerAddressReader.Read(text);
        Assert.Equal("99/1 ถ.สุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพฯ 10110", r.Address);
    }

    [Fact]
    public void Radisson_ที่อยู่ผู้ขายที่ตัวอ่านเดิมหยิบมา_คือบล็อกผู้ซื้อทั้งก้อน()
    {
        // ค่าจริงที่ VendorAddressRegex (ParseThaiDocument/EnrichFromRawText) คืนบนใบนี้ — จำลองด้วย python
        const string vendorFromOldRegex = "Address  202/24 หมู่ที่5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110";
        var buyer = OcrBuyerAddressReader.Read(Radisson, OurTaxId).Address;
        Assert.True(OcrBuyerAddressReader.IsPartOf(vendorFromOldRegex, buyer));
    }

    // ═════════ ครึ่งหลัง: ไม่มีหลักฐาน → ห้ามหยิบ ═════════

    [Fact]
    public void ไม่มีป้ายฝั่งผู้ซื้อ_ป้ายที่อยู่ในหัวร้าน_ต้องไม่ถูกหยิบเป็นที่อยู่ผู้ซื้อ()
    {
        const string text = "ร้านวัสดุดี\nที่อยู่ 12/3 ถ.สุขุมวิท ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110\n"
            + "ใบเสร็จรับเงิน\nปูน 2 ถุง 300.00\n";
        Assert.False(OcrBuyerAddressReader.Read(text).Found);
    }

    [Fact]
    public void ป้าย_ที่อยู่_ที่อยู่ใต้ป้ายฝั่งผู้ขาย_ไม่ใช่ของผู้ซื้อ()
    {
        const string text = "ลูกค้า: เงินสด\nผู้ขาย: บริษัท บีบี จำกัด\n"
            + "ที่อยู่ 1/2 ถ.พระราม 4 แขวงสีลม เขตบางรัก กรุงเทพฯ 10500\n";
        Assert.False(OcrBuyerAddressReader.Read(text).Found);
    }

    [Fact]
    public void ข้อความใต้ป้าย_Address_ที่ไม่ใช่ที่อยู่_ไม่หยิบ()
    {
        const string text = "Customer: John Smith\nE-mail address: john@example.com\nTotal 100.00\n";
        Assert.False(OcrBuyerAddressReader.Read(text).Found);
    }

    [Fact]
    public void ที่อยู่ผู้ขายจริงที่ยาวกว่าบล็อกผู้ซื้อ_ห้ามถือว่าเป็นส่วนหนึ่งของผู้ซื้อ()
    {
        var buyer = OcrBuyerAddressReader.Read(WinePro).Address;
        Assert.False(OcrBuyerAddressReader.IsPartOf(
            "12/861 Moo 15 Bangkaew, Bangplee, Samutprakarn 10540", buyer));
        // ร้านกับลูกค้าที่อยู่ติดกันในข้อความเดียว (glued) — ผู้ขายมีส่วนของร้านนำหน้า ⇒ ไม่ใช่ส่วนหนึ่ง
        Assert.False(OcrBuyerAddressReader.IsPartOf(
            "177/18 ม.5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี " + buyer, buyer));
    }

    [Fact]
    public void บรรทัดที่ขึ้นต้นด้วยคำย่อที่อยู่_ต้องคั่นด้วยช่องว่าง_ไม่ต่อคำ()
    {
        const string text = "Bill To: บริษัท ซีซี จำกัด\nAddress: 5/6 ซอยบ้านห้วยกุ่ม\nอ.ศรีราชา จ.ชลบุรี 20110\n";
        Assert.Equal("5/6 ซอยบ้านห้วยกุ่ม อ.ศรีราชา จ.ชลบุรี 20110", OcrBuyerAddressReader.Read(text).Address);
    }
}
