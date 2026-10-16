using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านของคอมมิต 6e56d33d (Makro หน้า 2/2) — สี่ข้อ ทุกข้อล็อกสองทิศ (ใบที่พังกลับมาถูก · ใบที่ถูกอยู่แล้วไม่ถูกแตะ)
/// <list type="bullet">
/// <item>P1-1 ป้าย "รวม VAT" กว้างเกิน: ไม่รู้จักคำปฏิเสธที่ Tesseract ทำวรรณยุกต์หล่น/อังกฤษ · นับทุกเลขหลังป้ายเป็นยอดรวม VAT</item>
/// <item>P2-2 "หัก…มัดจำ ที่ใดก็ได้ = การหัก" พลิกใบมัดจำจริงเป็นไม่ใช่มัดจำ — ถอนกฎ · fallback บรรทัดถัดไปรับเฉพาะบรรทัดยอดเงินล้วน</item>
/// <item>P2-3 ข้อความ "ยอดเอกสารเกินกระดาษ" โทษ VAT บวกซ้ำทุกครั้ง — เป็นกลาง เว้นแต่ตัวเลขพิสูจน์บทบาทนั้น</item>
/// <item>P2-4 หมายเหตุ [Σ-GAP] เกินกระดาษตอนสร้าง หยุดการอนุมัติเอง/LINE ตลอดไปแม้แก้บรรทัดแล้ว — ตรวจสดแทน (ตรวจไม่ได้ = หยุดต่อ)</item>
/// </list>
/// </summary>
public class OcrReview6e56d33dTests
{
    // ใบผสม: ยกเว้น 500 + มี VAT 1,000 · VAT 70 · รวม 1,570 — ฐานก่อน VAT ที่พิมพ์ = 1,500 · engine หยิบ 1,500 เป็นยอดรวม
    private static string MixedPaper(string baseRow) =>
        "บริษัท ตัวอย่างค้าส่ง จำกัด (สำนักงานใหญ่)\n" +
        "เลขประจำตัวผู้เสียภาษี 0105536092641\n" +
        "ใบกำกับภาษี/ใบเสร็จรับเงิน\n" +
        "ไข่ไก่ (ยกเว้นภาษี)                     500.00\n" +
        "น้ำยาล้างจาน                          1,000.00\n" +
        baseRow + "\n" +
        "ภาษีมูลค่าเพิ่ม 7%                        70.00\n" +
        "จำนวนเงินรวมทั้งสิ้น                  1,570.00";

    private static void AssertProven1570(string paper)
    {
        var r = OcrTotalAnchor.Find(paper, 1500.00m);
        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(1570.00m, r.Total);
        Assert.Equal(1500.00m, r.PrintedBase);
        Assert.Equal(70.00m, r.PrintedVat);
    }

    [Fact]
    public void P1_1_ทิศเดิม_ป้ายไม่รวมภาษีสะกดถูก_ยังยึด1570ฐาน1500()
        => AssertProven1570(MixedPaper("รวมเงินไม่รวมภาษี                     1,500.00"));

    [Fact]
    public void P1_1_Tesseractทำไม้เอกหล่น_ไมรวมภาษี_ยังเป็นฐาน_ยึด1570()
    {
        const string row = "รวมเงินไมรวมภาษี                      1,500.00";
        Assert.False(OcrPaperAmounts.IsVatInclusiveOnLine(row, 1500.00m));
        AssertProven1570(MixedPaper(row));
    }

    [Fact]
    public void P1_1_ป้ายอังกฤษ_not_incl_VAT_ยังเป็นฐาน_ยึด1570()
    {
        const string row = "Amount not incl. VAT                   1,500.00";
        Assert.False(OcrPaperAmounts.IsVatInclusiveOnLine(row, 1500.00m));
        AssertProven1570(MixedPaper(row));
    }

    [Fact]
    public void P1_1_สองคอลัมน์รวมบรรทัดเดียว_เฉพาะเลขแรกหลังป้ายรวมVATคือยอดรวมVAT()
    {
        const string row = "Total (VAT incl.) 1,570.00   Total (VAT excl.) 1,500.00";
        Assert.True(OcrPaperAmounts.IsVatInclusiveOnLine(row, 1570.00m));
        Assert.False(OcrPaperAmounts.IsVatInclusiveOnLine(row, 1500.00m));   // ช่วงของป้าย excl. ไม่ใช่ของป้าย incl.
        AssertProven1570(MixedPaper(row));
    }

    [Fact]
    public void P1_1_ทิศตรงข้าม_ป้ายรวมVATจริงยังกันฐานปลอม_Makro2จาก2ยังยึด10980()
    {
        Assert.True(OcrPaperAmounts.IsVatInclusiveOnLine("ราคาสินค้ารวมภาษีมูลค่าเพิ่ม/ TOTAL 11,208.00", 11208.00m));
        Assert.True(OcrPaperAmounts.IsVatInclusiveOnLine("Total (VAT Included) 1,070.00", 1070.00m));
        // เลขที่สองหลังป้ายไม่ใช่ยอดรวม VAT อีกแล้ว (เดิมทุกเลขหลังป้าย)
        Assert.False(OcrPaperAmounts.IsVatInclusiveOnLine("รวมภาษี 1,070.00 ส่วนลด 50.00", 50.00m));
        var r = OcrTotalAnchor.Find(OcrPaperSamples.MakroPage2of2, 11846.50m);
        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(10980.00m, r.Total);
        Assert.Equal(10341.50m, r.PrintedBase);
    }

    // ── P2-2 มัดจำ ──

    [Theory]
    [InlineData("RECEIPT\nAdvance payment received (less advance previously paid) 5,000.00")]
    [InlineData("ใบเสร็จรับเงิน\nรับเงินมัดจำ 30% หักมัดจำครั้งก่อน 10,000.00")]
    public void P2_2_ใบมัดจำจริงที่อธิบายการหักงวดก่อนบนบรรทัดเดียวกัน_ยังเป็นมัดจำ(string paper)
        => Assert.True(OcrDepositMarker.Decide(paper, null).IsDeposit);

    [Fact]
    public void P2_2_รายการมัดจำงวดที่2ที่วงเล็บหักงวดแรก_ยังเป็นมัดจำ()
        => Assert.True(OcrDepositMarker.Decide("ใบแจ้งหนี้",
            new (string?, decimal?)[] { ("ค่ามัดจำงวดที่ 2 (หักมัดจำงวดที่ 1 แล้ว)", 5000.00m) }).IsDeposit);

    [Fact]
    public void P2_2_ทิศตรงข้าม_Makroแถวหักเงินมัดจำยอดศูนย์_และบรรทัดถัดไปไม่ใช่ยอดเงินล้วน_ไม่ใช่มัดจำ()
    {
        Assert.False(OcrDepositMarker.Decide(OcrPaperSamples.MakroPage2of2, null).IsDeposit);
        Assert.False(OcrDepositMarker.Decide(
            ThaiTextNormalizer.Normalize(OcrMakroPage2VatAddedTwiceTests.MakroPage2of2Tesseract), null).IsDeposit);
        // ป้ายแยกเซลล์: ป้ายบรรทัดหนึ่ง ยอดล้วนบรรทัดถัดไป — ยังอ่านยอดได้ (ใบมัดจำจริง)
        Assert.True(OcrDepositMarker.Decide("ใบรับเงิน\nเงินมัดจำ\n50,000.00", null).IsDeposit);
        // บรรทัดถัดไปเป็นข้อความที่มีเลขปน (หมายเหตุ "4.ใบเสร็จ…") ⇒ ไม่ใช่ยอดมัดจำ
        Assert.False(OcrDepositMarker.Decide("ใบเสร็จรับเงิน\nหมายเหตุ เงินมัดจำ\n4.ใบเสร็จรับเงินถูกจัดทำโดยระบบ", null).IsDeposit);
        // "ยอดหลังหักเงินมัดจำ" ยังเป็นการหัก (รูปที่ไม่มีความหมายอื่น)
        Assert.False(OcrDepositMarker.Decide("ยอดหลังหักเงินมัดจำ 9,000.00", null).IsDeposit);
    }

    // ── P2-3 ข้อความเกินกระดาษ ──

    private const string PlainInvoice =
        "ใบกำกับภาษี\nค่าสินค้า 1,000.00\nภาษีมูลค่าเพิ่ม 7% 70.00\nจำนวนเงินรวมทั้งสิ้น 1,070.00\n(หนึ่งพันเจ็ดสิบบาทถ้วน)";

    [Fact]
    public void P2_3_ผู้ใช้เพิ่มบรรทัดค่าขนส่งเอง_ข้อความเป็นกลาง_ไม่โทษVATบวกซ้ำ()
    {
        var o = OcrTotalAnchor.DocumentOverstatement(PlainInvoice, 1070.00m, 1120.00m);
        Assert.NotNull(o);
        Assert.StartsWith(OcrTotalAnchor.OverstatementLead, o!.Message);
        Assert.Contains("ยอดเอกสาร 1,120.00 สูงกว่ายอดชำระบนกระดาษ 1,070.00", o.Message);
        Assert.Contains("ต่าง 50.00", o.Message);
        Assert.Contains("ตรวจว่ามีบรรทัดเกินหรือ VAT ซ้ำ", o.Message);
        Assert.DoesNotContain("ถูกบวก VAT ซ้ำ", o.Message);
    }

    [Fact]
    public void P2_3_ทิศตรงข้าม_Makroยอดที่บวกVATซ้ำจริง_ข้อความบอกบทบาทนั้น()
    {
        var o = OcrTotalAnchor.DocumentOverstatement(
            ThaiTextNormalizer.Normalize(OcrPaperSamples.MakroPage2of2), 11846.50m, 11846.50m);
        Assert.NotNull(o);
        Assert.Contains("ถูกบวก VAT ซ้ำ", o!.Message);
        Assert.Null(OcrTotalAnchor.DocumentOverstatement(PlainInvoice, 1070.00m, 1070.00m));   // tolerance เดิม 0.02
        Assert.NotNull(OcrTotalAnchor.DocumentOverstatement(PlainInvoice, 1070.00m, 1070.03m));
    }

    // ── P2-4 ความพร้อมลงบัญชีเอง ──

    private static readonly string StaleNotes =
        "[Σ] อะไรสักอย่าง\n" + OcrLineBuildNotes.GapTag + " " + OcrTotalAnchor.OverstatementLead + ": เอกสาร 11,846.50 · กระดาษ 10,980.00";

    [Fact]
    public void P2_4_แก้บรรทัดให้ตรงกระดาษแล้ว_ตรวจสดไม่เกิน_หมายเหตุเก่าไม่หยุดการอนุมัติเอง()
        => Assert.True(OcrPostingReadiness.Evaluate(StaleNotes, hasUsableDate: true, overstatedNow: false).CanAutoApprove);

    [Fact]
    public void P2_4_ทิศตรงข้าม_ยังเกิน_หรือตรวจสดไม่ได้_หรือมีSigmaGapข้ออื่น_ยังหยุด()
    {
        Assert.False(OcrPostingReadiness.Evaluate(StaleNotes, true, overstatedNow: true).CanAutoApprove);
        Assert.False(OcrPostingReadiness.Evaluate(StaleNotes, true, overstatedNow: null).CanAutoApprove);
        Assert.False(OcrPostingReadiness.Evaluate(StaleNotes, true).CanAutoApprove);
        var other = StaleNotes + "\n" + OcrLineBuildNotes.GapTag + " ยอดรวมจากรายการ 100.00 + VAT 7.00 ≠ ยอดรวมบนกระดาษ 200.00";
        Assert.False(OcrPostingReadiness.Evaluate(other, true, overstatedNow: false).CanAutoApprove);
    }
}
