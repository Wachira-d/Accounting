using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้าน W1c P2-6 (ทีม W1d) — ตัวกรอง "เลขผู้เสียภาษีเหลือแต่ตัวเลข" ทุกจุดเรียก <see cref="ThaiTaxId.Normalize"/> ตัวเดียว ⇒ เลขไทย ๐–๙ กับเลขอารบิก
/// คือเลขเดียวกันทุกทางเข้า (เดิมสำเนา inline ราว 40 จุดคงเลขไทยไว้ ขณะที่ตัวตั้งแปลงเป็น ASCII ⇒ ตัวเทียบที่เอาค่าสองรูปมาชนกันไม่มีวันเท่ากัน)
/// <para>กันสำเนาใหม่: <c>tools/taxid_digits_inline_check.py</c> (ระดับบรรทัด · --self-test)</para>
/// </summary>
public class ThaiTaxIdSingleSourceTests
{
    private const string Ascii = "0105561012346";
    private const string Thai = "๐๑๐๕๕๖๑๐๑๒๓๔๖";

    [Fact]
    public void ตัวตรวจเลขบัตร_ศูนย์นำเข้า_เลขไทยที่ถูกผ่าน()
    {
        // เดิม: clean = เลขไทย ⇒ HasValidChecksum คิดจากรหัสอักขระไทย ⇒ "check digit ไม่ถูกต้อง" ทั้งที่เลขถูก
        Assert.True(ThaiTaxIdValidator.Check(Thai).IsValid);
        Assert.Equal(Ascii, ThaiTaxIdValidator.Normalize(Thai));
        // ทิศตรงข้าม: เลขผิดยังผิด
        Assert.False(ThaiTaxIdValidator.Check("๐๑๐๕๕๖๑๐๑๒๓๔๗").IsValid);
    }

    [Fact]
    public void ตัวตรวจchecksumของOCR_เลขไทยที่ถูกผ่าน_ตัวเลขมีขีดยังไม่ผ่านเหมือนเดิม()
    {
        Assert.True(FieldPatternLibrary.ValidateThaiTaxId(Thai));
        Assert.True(FieldPatternLibrary.ValidateThaiTaxId(Ascii));
        Assert.False(FieldPatternLibrary.ValidateThaiTaxId("0105561012347"));
        Assert.False(FieldPatternLibrary.ValidateThaiTaxId("0-1055-61012-34-6"));   // ต้องเป็นตัวเลข 13 ตัวล้วน (เหมือนเดิม)
    }

    [Fact]
    public void เลขเราบนกระดาษพิมพ์เลขไทย_จับได้ว่าเป็นเลขเรา()
    {
        // เดิม: ours = Normalize (ASCII) แต่เลขบนกระดาษผ่านสำเนา inline (เลขไทย) ⇒ ไม่มีวันเท่ากัน
        var (exact, near) = OcrPartyResolver.OurTaxIdOnPaper($"ผู้ซื้อ เลขประจำตัวผู้เสียภาษี {Thai}", Ascii);
        Assert.True(exact);
        Assert.Null(near);
        // ทิศตรงข้าม: เลขคนอื่นไม่ใช่เลขเรา
        Assert.False(OcrPartyResolver.OurTaxIdOnPaper("เลขประจำตัวผู้เสียภาษี 0994000165501", Ascii).Exact);
    }

    [Fact]
    public void ตัวตัดสินที่ใช้เลขผู้เสียภาษี_เลขไทยได้ผลเดียวกับเลขอารบิก()
    {
        Assert.Equal(WhtPayeeKind.Detect(Ascii, ContactType.Unknown, "x"), WhtPayeeKind.Detect(Thai, ContactType.Unknown, "x"));
        Assert.Equal(OcrUtilityKind.Electricity, OcrUtilityVendor.KindOf("๐๙๙๔๐๐๐๑๖๕๕๐๑", null));
        Assert.Equal(WhtCertFormText.PartyName(null, "บจก. ก", Ascii, "00000", null), WhtCertFormText.PartyName(null, "บจก. ก", Thai, "00000", null));
        Assert.True(EncryptedIdMatch.Same(Thai, Ascii));
        Assert.Equal(Ascii, EncryptedIdMatch.Normalize(Thai));
        Assert.Equal(PiiMask.CitizenId(Ascii), PiiMask.CitizenId(Thai));
        Assert.Equal("0-XXXX-XXXXX-XX-6", PiiMask.CitizenId(Thai));
        Assert.Equal(PiiMask.TaxId(Ascii), PiiMask.TaxId(Thai));
        Assert.Equal(VendorLearningKey.For(Ascii, null), VendorLearningKey.For(Thai, null));
    }
}
