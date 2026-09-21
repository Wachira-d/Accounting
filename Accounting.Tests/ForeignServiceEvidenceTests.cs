using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "ลืมติ๊กบริการต่างประเทศ (ภ.พ.36 §83/6)" — เคสจริงที่ผู้ใช้ส่งภาพมา 2026-09-21:
/// ใบสำคัญจ่ายค่าคอมมิชชั่น Booking.com B.V. สองใบของคู่ค้ารายเดียวกัน
/// ใบ ก.ค. ติ๊ก (มี Cr 21912) · ใบ ส.ค. ไม่ติ๊ก (ไม่มี 21912 + เครดิตผู้รับเงินรวม VAT)
///
/// เทสต์สองครึ่ง: ครึ่งแรกต้องจับใบที่ลืมติ๊กได้ · ครึ่งหลังต้อง **เงียบสนิท**
/// กับใบซื้อในประเทศปกติ (คำเตือนที่ฟ้องใบถูกทุกใบ = ปิดด่านโดยไม่ตั้งใจ)
/// </summary>
public class ForeignServiceEvidenceTests
{
    // ═══════════════ ครึ่งที่ 1 — ต้องจับได้ ═══════════════

    [Fact]
    public void คู่ค้าระบุประเทศไม่ใช่ไทย_มี_VAT_แต่ไม่ได้ติ๊ก_ต้องเตือน()
    {
        var v = ForeignServiceEvidence.Judge(
            isForeignService: false, vatAmount: 705.23m,
            contactCountryCode: "NL", contactTaxId: "NL805734958B01",
            inputVatParkedAsUndue: true);
        Assert.True(v.Suspect);
        Assert.Contains(ForeignServiceEvidence.RuleCode, v.Reason);
        Assert.Contains("NL", v.Reason);
        // ข้อความต้องบอก "ทางไปต่อ" ทั้งก่อนและหลังอนุมัติ (F2 ข้อ 8)
        Assert.Contains("ติ๊กช่อง", v.Reason);
        Assert.Contains("อนุมัติไปแล้ว", v.Reason);
        // และต้องบอกยอดที่จะเกินจริงเป็นตัวเลข ไม่ใช่คำลอย ๆ
        Assert.Contains("705.23", v.Reason);
    }

    [Fact]
    public void ไม่ระบุประเทศแต่เลขภาษีไม่ใช่รูปไทยและภาษีซื้อพักที่11640_ต้องเตือน()
    {
        var v = ForeignServiceEvidence.Judge(
            false, 715.52m, contactCountryCode: null,
            contactTaxId: "NL805734958B01", inputVatParkedAsUndue: true);
        Assert.True(v.Suspect);
    }

    [Theory]
    [InlineData("NL")]
    [InlineData("SG")]
    [InlineData("us")]   // ตัวพิมพ์เล็กต้องจับได้เหมือนกัน
    public void ประเทศต่างชาติทุกแบบต้องเตือน(string country)
    {
        Assert.True(ForeignServiceEvidence.Judge(false, 100m, country, null, false).Suspect);
    }

    // ═══════════ ครึ่งที่ 2 — ใบที่ถูกอยู่แล้วต้องเงียบ ═══════════

    [Fact]
    public void ติ๊กไว้แล้ว_ต้องไม่เตือน()
    {
        Assert.False(ForeignServiceEvidence.Judge(true, 705.23m, "NL", null, true).Suspect);
    }

    [Fact]
    public void ไม่มี_VAT_ต้องไม่เตือน()
    {
        // ใบที่ไม่มี VAT ไม่มีอะไรให้ประเมิน/นำส่ง — เตือนก็ไม่มีทางไปต่อ
        Assert.False(ForeignServiceEvidence.Judge(false, 0m, "NL", null, true).Suspect);
    }

    [Theory]
    [InlineData("TH")]
    [InlineData("th")]
    [InlineData("")]
    [InlineData(null)]
    public void คู่ค้าไทยหรือไม่ระบุประเทศ_ที่เลขภาษีเป็นรูปไทย_ต้องเงียบ(string? country)
    {
        var v = ForeignServiceEvidence.Judge(false, 700m, country, "0105558123456", true);
        Assert.False(v.Suspect);
        Assert.Equal("", v.Reason);
    }

    [Fact]
    public void ใบไทยที่เอกสารไม่ครบ_พักภาษีซื้อที่11640_ต้องไม่ถูกเตือนเพราะเหตุนี้อย่างเดียว()
    {
        // เคสปกติมากของ SME ไทย: ผู้ขายยังไม่ส่งใบกำกับตัวจริง ⇒ VAT พัก 11640
        // ถ้าเตือนด้วยหลักฐานนี้อย่างเดียว จะเตือนใบถูกแทบทุกใบ
        var v = ForeignServiceEvidence.Judge(false, 700m, "TH", contactTaxId: null,
            inputVatParkedAsUndue: true);
        Assert.False(v.Suspect);
    }

    [Fact]
    public void เลขภาษีไม่ใช่รูปไทยแต่ภาษีซื้อไม่ได้พัก_ยังไม่พอจะเตือน()
    {
        // หลักฐานชั้นรองต้องครบสองอย่าง — อย่างเดียวยังอ่อนเกินจะฟ้อง
        Assert.False(ForeignServiceEvidence.Judge(false, 700m, "TH", "ABC-123", false).Suspect);
    }

    [Theory]
    [InlineData("0105558123456", false)]      // เลขไทยถูกรูป → เงียบ
    [InlineData("0-1055-58123-45-6", false)]  // มีขีดคั่นก็ยังเป็นเลขไทย
    [InlineData("NL805734958B01", true)]      // เลข EU ของ Booking.com → เตือน
    [InlineData("12345", true)]               // สั้นเกิน = ไม่ใช่เลขไทย
    public void รูปเลขผู้เสียภาษีตัดสินผ่าน_ThaiTaxId_ตัวเดิมของเรพ(string taxId, bool expectSuspect)
    {
        // ไม่ได้เขียนกติกา "13 หลัก" ชุดที่สอง — ยืมตัวตั้งเดิม (F2 ข้อ 4)
        var v = ForeignServiceEvidence.Judge(false, 700m, "TH", taxId, inputVatParkedAsUndue: true);
        Assert.Equal(expectSuspect, v.Suspect);
    }
}
