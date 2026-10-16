using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// 2026-10-16 — ผู้ใช้รายงาน: "ocr เอกสารเข้ามาสร้างใบสำคัญจ่าย ยอดส่วนไม่มี vat สร้างผิด … ยอดที่ถูกควรเป็น 1220 และยอดรวมทั้งหมดควรเป็น 10980"
/// (ใบเสร็จรับเงิน/ใบกำกับภาษี Makro · บมจ.ซีพี แอ็กซ์ตร้า สาขา 03060 · หน้า 2 จาก 2 · เลขที่ 405901117163 — ภาพถ่าย)
///
/// <para><b>ตัวเลขที่ผู้ใช้เห็น</b> (ภาพหน้าจอ): "(ส่วนมีภาษี)" 9,121.43 VAT 638.50 · "(ส่วนไม่มีภาษี)" 2,086.57 · ยอดรวมก่อน VAT 11,208.00 ·
/// ยอดสุทธิ 11,846.50 · <b>ตัวเลขบนกระดาษ</b>: ตารางรหัส ภ.พ. 1 ยกเว้น 1,220.00 · 2 ฐาน 9,121.50 VAT 638.50 · รวม 10,980.00 ·
/// "ราคาสินค้ารวมภาษีมูลค่าเพิ่ม/ TOTAL 11,208.00" (รวม VAT ก่อนหักส่วนลด 228) · AMOUNT/NET AMOUNT/ตัวอักษร/แถวชำระ = 10,980.00</para>
///
/// <para><b>ราก</b> (ไล่ในโค้ด): หัวสแกนถือ ฐาน 11,208 (ป้าย "รวมภาษี…TOTAL") + VAT 638.50 = ยอด 11,846.50 (ไม่มีบนกระดาษ) ·
/// <see cref="OcrTotalAnchor.Find"/> นับ 11,208 เป็น "ฐานที่พิมพ์" ⇒ 11,846.50 ได้ชั้น VAT ⇒ <c>Conflict</c> กับ 10,980 ⇒ ไม่แตะค่า ⇒ เส้นบรรทัด
/// ตามตารางรหัส ภ.พ. ไม่ทำงาน (ต้องการยอดรวม = แถว "รวม" ของตาราง) ⇒ บรรทัดสรุปเดียว 11,208 · 7% · VAT 638.50 + คำแนะนำ
/// "VAT บนกระดาษบอกว่ายอดที่มี VAT ≈ 9,121.43 ⇒ ยกเว้น ≈ 2,086.57" (VAT/0.07 บนฐานผิด) = ตัวเลขสองบรรทัดในภาพหน้าจอพอดี</para>
///
/// <para>สองครึ่งตาม CLAUDE.md §H: ครึ่งแรก = ใบนี้กลับมาถูก (ข้อความถอดจากภาพ + ข้อความ Tesseract จริง) · ครึ่งหลัง = ใบที่ถูกอยู่แล้ว
/// (Makro 3/3 · เลขที่พิมพ์บนกระดาษจริง · ฐานที่อยู่ก่อนป้ายรวม VAT · มัดจำจริง) ไม่ถูกแตะ · ชุดกระดาษทั้ง Corpus = ไม่มีใบไหนเปลี่ยน
/// (<see cref="OcrReplayGoldenTests"/> · <see cref="OcrReplayLineGoldenTests"/>)</para>
/// </summary>
public class OcrMakroPage2VatAddedTwiceTests
{
    /// <summary><b>ข้อความ Tesseract 5.3.4 จริง</b> (tha+eng · LSTM) ของภาพถ่ายใบนี้ — ครึ่งบน = <c>--psm 3</c> ทั้งหน้า · ครึ่งล่าง = ครอปพื้นที่ตาราง/ท้ายใบ
    /// (150,800)–(1450,1800) ขยาย ×2 <c>--psm 6</c> (บนภาพเต็ม <c>--psm 3</c> หยุดอ่านกลางตารางรายการ — เส้น <c>EmbeddedTesseractOcrService</c>
    /// ของเรพ (PSM Auto · ไม่ครอป) จึงไม่ได้ตัวเลขท้ายใบเลยสักตัวบนภาพนี้) · ยกมาทุกตัวอักษร: "59 2 … 9,760.00 ," (จุลภาคจากรอยพับ) ·
    /// คำอธิบายรหัส "1«สินค้าได้รับการยกเว้น…" / "28สินคําที่ต้องเสีย…" (เครื่องหมาย = หาย) · NET AMOUNT อ่านเป็น 40,980.00 ·
    /// แถว ": = : ie หักเงินมัดจํา/ DEPOSIT" (ยอด 0.00 หาย)</summary>
    public const string MakroPage2of2Tesseract =
        "Lh\n" +
        "บริษัท ซีพี แอ๊กซ์ตร้า จํากัด (มหาชน)\n" +
        "สํานักงานใหญ่ โทร. 0-2067-8999\n" +
        "เลขป' ะจําตัวผู้เสียภาษีอากร 0 10 7 567 00041 4\n" +
        "ใบเสร็จรับเงิน/ใบกํากับภาษี\n" +
        "RECEIPT / TAX INVOICE\n" +
        "สาขาที่ออกใบกํากับภาษี/ Branch\n" +
        "ที่อยู่/ Address\n" +
        "“หจก.แอม แฮปปี้เนส\n" +
        "รหัสสมาชิกลูกค้า/๐น5!๐ท6 No.          00596437340722\n" +
        "ที่อยู่/ Address                              202/24 ม.5 ซอยบ้านห้วยกุ่ม4 ตําบลบาง\n" +
        "พระ อําเภอศรีราชา จังหวัดชลบุรี 20110\n" +
        "เชื่อลูกค้า/0นร!อททฮะ Name\n" +
        "เลขประจําตัวผู้เสียภาษี/ Tax ID         0203562005871 สาขา 00000\n" +
        "เลขที่ใบกํากับภาษี/ Tax Invoice No.\n" +
        "วชิร ดิลกสัมพันธ์\n" +
        "202/24 ม.5, Tambon Bang Phra sua  วันที่ใบกํากับภาษี/ Tax Invoice Date\n" +
        "สถานที่ส่งสินค้า/ Shipping address\n" +
        "ที่อยู่/ Address\n" +
        "บางพระ อําเภอศรีราชา จังหวัดชลบุรี 20110\n" +
        "วชิร ดิลกสัมพันธ์\n" +
        "taketime.bangphra@gmail.com\n" +
        "+66942514696\n" +
        "วันที่สั่งซื้อ/ Order Date\n" +
        "เลขที่สั่งซื้อ/ Order No.\n" +
        "วิธีการชําระเงิน/ Payment type\n" +
        "ต้นฉบับลูกค้า\n" +
        "For Customer\n" +
        "หน้าที่ 2จาก 2\n" +
        "03060\n" +
        "บมจ.ซีพี แอ๊กซ์ตร้า สาขาเอฟซี แหลม\n" +
        "ฉบัง\n" +
        "154/26-29 หมู่ที่ 4 ต.บึง\n" +
        "อ.ศรีราชา ชลบุรี\n" +
        "20230\n" +
        "405901117163\n" +
        "43/10/2026\n" +
        "43/10/2026\n" +
        "8543953633B\n" +
        "Credit Card/Debit Card\n" +
        "จํานวน/น้าหนัก                           ราคาต่อหน่วย | รหัส              จํานวนเงินรวม\n" +
        "\\NTITY                             UNIT PRICE | VAT CODE        TOTAL\n" +
        "ซื้อสินค้าฮีโร่ ถุงขยะ ถุงหูหิ้ว ถุงชิป ครบ 359 ลด 30 บาท(*1.0)\n" +
        "an 60บเมื่อซื้อสินค้ายูนิลีเวอร์ ดูแลเส้นผม ผิวหน้า ผิวกาย ที่ร่วมครบ 599 บ(“1.0)\n" +
        "3         ซื้อสินค้าฮีโร่ ถุงขยะ ถุงหูหิ้ว ถุงชิป ครบ 359 ลด 30 บาท(*1.0)                                      30.00\n" +
        "4         ลด 60บเมื่อซื้อสินค้ายูนิลีเวอร์ ดูแลเส้นผม ผิวหน้า ผิวกาย ที่ร่วมครบ 599 บ(*1.0)                    60.00\n" +
        ".00\n" +
        "5            SLAB1(*5.0)                                                                                                                      10\n" +
        "รวมส่วนลด                                                                                                                       228.00\n" +
        "จํานวนชิ้น |รหัส ภ.พ.                                          ราคาสินค้า| ภาษีมูลค่าเพิ่ม                  รวม\n" +
        "2                         1                                                                   1,220.00                          0.00             1,220.00\n" +
        "59                 2                                                  9,121.50                638.50         9,760.00                                                                                         ,\n" +
        "รวม                                                                         10,341.50                       638.50           10,980.00\n" +
        "ง่านวนเงินที่ต้องชําระ                                                    หนึ่งหมื่นเก้าร้อยแปดสิบบาทถ้วน                                              าคาสินค้ารวมภาษีมูลค่าเพิ่ม/ 1๐1 /แ.                 11,208.00\n" +
        "หมายเหตุ/ Remark         เงื่อนไข/ Condition:                                                                               หักส่วนสด/ DISCOUNT                                   228.00\n" +
        "รหัสภพ                                            1.โปรดเก็บเอกสารไว้เป็นหลักฐานในการคิดต่อกับทางบริษัท\n" +
        "1«สินค้าได้รับการยกเว้นภาษิมูลคําเพิ่ม       2.บริษัทจะรับคืนสินค้าภายใน 7 วัน ยกเว้นของสตรับคินภายในวันที่รับสินค้า และสินค้าที่รับคืนต้องอยู่ใน      จํานวนเงินรวมสุทธิ/ AMOUNT                         10,980.00\n" +
        "28สินคําที่ต้องเสียภาษีมูลคําเพิ่ม             สภาพเดิม                                                                                                            «        ~\n" +
        ":                      =       :                         ie                                  หักเงินมัดจํา/ DEPOSIT\n" +
        "4ะสินค้ามูลค่างแพาะคิดภาษิมูลคําเพิ่มจาก |3.บริษัทขอสงวนสิทธิในกรณีทีเป็นบัตรเครดิดใบเสร็จจะสมบูรณ์เมื่อได้เรียกเกิบเงินจากธนาคารเรียบร้อยแล้ว\n" +
        "Legs! Amount                        4.ใบเสร็จรับเงินถูกจัดทําชิ้นโดยระบบอัดโนมัติของแพลดตพ่อร์ม (“เอกสารอิตโนมัติ“) และให้ถือเป็นหลักฐาน   ราคาสินค้าที่ต้องซําระ / NET AMOUNT         40,980.00\n" +
        "การชําระเงินและรายละเอียดธุรกรรมที่มีความถูกต้องครบถ้วนทุกประการ ซึ่งมิผลผูกพันคู่สัญญา โดยไม่\n" +
        "จําเป็นต้องมีการลงลายมือซื่อ ทั้งนี้ ใบเสริจรับเงินจะมิผลสมบูรณ์ต่อเมื่อคูสัญญาได้ชําระเงินครบถ้วนตาม\n" +
        "จํานวนที่ระบุในเอกสารตังกล่าว\n" +
        "ว                                       Credit Card/Debit\n" +
        "ประเภทการชําระเงิน / Payment type                                                                           Voucher\n" +
        "Card\n" +
        "Fa Ana     ท๓ [ooo | อ๑ 11 0 ศัฑ์ 1 aa";

    private static string Norm(string s) => ThaiTextNormalizer.Normalize(s);

    // ═════════ ทำซ้ำตัวเลขที่ผู้ใช้เห็น (ก่อนแก้) — ตัวเลข 9,121.43 / 2,086.57 มาจากไหน ═════════

    [Fact]
    public void ทำซ้ำ_บรรทัดสรุปเดิม11208_คำแนะนำVATหาร0_07ให้9121_43กับ2086_57_ตรงภาพหน้าจอ()
    {
        // ก่อนแก้: หัวสแกน 11,208 / 638.50 / 11,846.50 ⇒ ไม่มีตารางกลุ่ม (ยอดรวม ≠ แถว "รวม" 10,980) ⇒ บรรทัดสรุปเดียว 11,208 · 7% · VAT 638.50
        // ด่าน Σ บอกทางแก้ด้วย VAT ÷ 0.07 บน "ฐาน" ที่จริงคือยอดรวม VAT ก่อนส่วนลด ⇒ ตัวเลขสองบรรทัดในใบสำคัญจ่ายของผู้ใช้พอดี
        var check = OcrAmountIntegrity.Check(new[] { new OcrPlannedLine(11208.00m, 7m, 638.50m) }, 638.50m, 11846.50m);
        var advice = Assert.Single(check.Problems, p => p.Kind == OcrAmountIntegrityKind.VatRateMismatch).Message;
        Assert.Contains("9,121.43", advice);
        Assert.Contains("2,086.57", advice);
        // ตัวตัดสินเดียวกันบนฐานที่ยึดถูก (10,341.50) ⇒ ส่วนยกเว้นโดยประมาณ 1,220.07 ≈ 1,220.00 ของตารางบนกระดาษ — และเส้นตารางกลุ่มทำให้ไม่ต้องประมาณเลย
        var fixedBase = OcrAmountIntegrity.Check(new[] { new OcrPlannedLine(10341.50m, 7m, 638.50m) }, 638.50m, 10980.00m);
        Assert.Contains("1,220.07", Assert.Single(fixedBase.Problems, p => p.Kind == OcrAmountIntegrityKind.VatRateMismatch).Message);
    }

    // ═════════ ครึ่งที่ 1: ใบที่พัง ต้องกลับมาถูก ═════════

    [Fact]
    public void ยอดที่บวกVATซ้ำ11846_50_ไม่มีบนกระดาษ_ยึด10980_ฐาน10341_50_ไม่ใช่ขัดกัน()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.MakroPage2of2, 11846.50m);

        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);              // เดิม Conflict (ไม่แตะค่า ⇒ ใบสำคัญจ่าย 11,846.50)
        Assert.Equal(10980.00m, r.Total);
        Assert.Equal(OcrTotalRole.VatAddedTwice, r.EngineRole);       // 11,208 (รวม VAT ก่อนส่วนลด) + VAT 638.50 อีกรอบ
        Assert.Equal(10341.50m, r.PrintedBase);                      // แถว "รวม" ของตารางรหัส ภ.พ.
        Assert.Equal(638.50m, r.PrintedVat);
        // 11,846.50 ต้องไม่มีหลักฐานชั้น VAT อีกแล้ว (เดิม "ฐาน 11,208.00 + VAT 638.50 = 11,846.50 (พิมพ์ทั้งคู่)")
        var engine = r.Candidates.Single(c => c.Amount == 11846.50m);
        Assert.False(engine.HasVatEvidence);
        Assert.Empty(engine.Evidence);
        Assert.Contains("บวก VAT ซ้ำ", r.Reason);

        var plan = OcrTotalAnchor.Plan(r, 11208.00m, 638.50m);
        Assert.Equal(10980.00m, plan.Total);
        Assert.Equal(10341.50m, plan.SubTotal);                       // ฐานที่พิมพ์ (PaperLabel) — ไม่ใช่ 11,208 ที่รวม VAT แล้ว
        Assert.Equal(OcrFieldSource.PaperLabel, plan.SubSource);
        Assert.Equal(638.50m, plan.Vat);
    }

    [Fact]
    public void engineหยิบป้ายTOTAL11208เป็นยอดรวม_ยังยึด10980_เป็นยอดก่อนส่วนลด()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.MakroPage2of2, 11208.00m);
        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(10980.00m, r.Total);
        Assert.Equal(OcrTotalRole.PreDiscountTotal, r.EngineRole);
    }

    [Fact]
    public void ตารางรหัสภพ_เป็นตัวแยกยกเว้น1220_กับมีVAT9121_50_บรรทัดสรุปต่อกลุ่ม()
    {
        var table = OcrLineVatMarks.ReadGroups(OcrPaperSamples.MakroPage2of2);
        Assert.True(table.AllKnown);
        Assert.Equal(new[] { (OcrVatGroupKind.Exempt, 1220.00m, 0.00m), (OcrVatGroupKind.Standard7, 9121.50m, 638.50m) },
            table.Groups.Select(g => (g.Kind, g.Net, g.Vat)).ToArray());
        Assert.Equal(10980.00m, table.Gross);

        // หลังยึดยอด: ยอด 10,980 · VAT 638.50 · ฐาน 10,341.50 ⇒ บรรทัดตามกลุ่ม (เส้นเดียวกับ BuildScanLinesAsync)
        var d = OcrTotalDecomposer.Decompose(OcrPaperSamples.MakroPage2of2, 10341.50m, 638.50m, 10980.00m, 228.00m);
        Assert.Equal(OcrDiscountPlacement.InclVat, d.Placement);
        var lines = OcrTotalDecomposer.SummaryGroupLines(d, 10980.00m, 638.50m, 10341.50m);
        Assert.Equal(2, lines.Count);
        Assert.Equal(10980.00m, lines.Sum(g => g.Net + g.Vat));
    }

    [Fact]
    public void ใบสำคัญจ่ายที่สร้างไปแล้ว11846_50_ด่านเกินกระดาษต้องฟ้อง_10980ไม่ฟ้อง()
    {
        var text = Norm(OcrPaperSamples.MakroPage2of2);
        // ตัวเลขในภาพหน้าจอ: 9,121.43 + 638.50 + 2,086.57 = 11,846.50 (สแกนเก่าถือยอด 11,846.50)
        var over = OcrTotalAnchor.DocumentOverstatement(text, 11846.50m, 9121.43m + 638.50m + 2086.57m);
        Assert.NotNull(over);
        Assert.Equal(10980.00m, over!.PaperTotal);
        Assert.StartsWith(OcrTotalAnchor.OverstatementLead, over.Message);
        Assert.Contains("866.50", over.Message);
        // ทิศตรงข้าม: เอกสารตรงกระดาษ / ต่ำกว่ากระดาษ ⇒ ไม่ฟ้อง (ต่ำกว่า = เรื่องของตัวกระทบยอด ไม่ใช่ด่านนี้)
        Assert.Null(OcrTotalAnchor.DocumentOverstatement(text, 11846.50m, 10980.00m));
        Assert.Null(OcrTotalAnchor.DocumentOverstatement(text, 10980.00m, 10980.01m));
        Assert.Null(OcrTotalAnchor.DocumentOverstatement(text, 10980.00m, 9760.00m));
        // กระดาษไม่ยืนยันยอดใด (ข้อความว่าง) ⇒ ไม่รู้ = ไม่ฟ้อง
        Assert.Null(OcrTotalAnchor.DocumentOverstatement("", 11846.50m, 11846.50m));
    }

    [Fact]
    public void คำเตือนตอนอนุมัติ_ข้อเกินกระดาษ_ท่อนตอนนี้ใช้ยอดกระดาษ_และหายเมื่อผู้ใช้แก้บรรทัดแล้ว()
    {
        var text = Norm(OcrPaperSamples.MakroPage2of2);
        var over = OcrTotalAnchor.DocumentOverstatement(text, 11846.50m, 11846.50m);
        var warnings = OcrApprovalGapWarning.Build(null, 11846.50m, 11846.50m, overstatement: over, overstatementCheckedLive: true);
        var w = Assert.Single(warnings);
        Assert.StartsWith(OcrApprovalGapWarning.Prefix, w);
        Assert.True(OcrApprovalGapWarning.IsAmountGapWarning(w));       // ชุด "ยอดไม่ตรงกระดาษ" — รับทราบตอนอนุมัติ
        Assert.Contains("กระดาษ 10,980.00 (ต่าง +866.50)", w);          // ไม่ใช่ "กระดาษ 11,846.50 (ตรงกันแล้ว)"

        // หมายเหตุสแกน (ตัวสร้างบรรทัดเขียนไว้ตอนสร้าง) + ผลสดซ้ำกัน ⇒ ข้อเดียว
        var notes = "[Σ] x\n" + OcrLineBuildNotes.GapTag + " " + over!.Message;
        Assert.Single(OcrApprovalGapWarning.Build(notes, 11846.50m, 11846.50m, overstatement: over, overstatementCheckedLive: true));
        // ผู้ใช้แก้บรรทัดให้ตรงกระดาษแล้ว (ผลสด = null) ⇒ ข้อความเก่าในหมายเหตุไม่ค้างเป็นคำเตือนที่โกหก
        Assert.Empty(OcrApprovalGapWarning.Build(notes, 11846.50m, 10980.00m, overstatement: null, overstatementCheckedLive: true));
        // ผู้เรียกที่ไม่ได้ตรวจสด (พฤติกรรมเดิม) ⇒ อ่านจากหมายเหตุเหมือนทุก [Σ-GAP]
        Assert.Single(OcrApprovalGapWarning.Build(notes, 11846.50m, 10980.00m));
    }

    [Fact]
    public void ข้อความTesseractจริง_แถวตารางที่มีจุลภาคท้ายแถว_ยังอ่านตารางได้()
    {
        var table = OcrLineVatMarks.ReadGroups(Norm(MakroPage2of2Tesseract));
        Assert.True(table.Found);                                       // เดิม: "59 2 … 9,760.00 ," ไม่ผ่าน ⇒ ทั้งตาราง None
        Assert.Equal(10980.00m, table.Gross);
        Assert.Equal(new[] { 1220.00m, 9121.50m }, table.Groups.Select(g => g.Net).ToArray());
        // คำอธิบายรหัสอ่านไม่ออก ("1«…") ⇒ รหัส 1 VAT 0 ไม่รู้ว่ายกเว้นหรือ 0% — ไม่เดา (ไม่สร้างบรรทัดต่อกลุ่ม · ยอดยกเว้นยังกันตัวพิสูจน์ "ทั้งใบ 7%")
        Assert.False(table.AllKnown);
        Assert.Equal(1220.00m, OcrLineVatPlanner.PaperExemptAmount(OcrLineVatMarks.Read(MakroPage2of2Tesseract), table));
    }

    [Fact]
    public void ข้อความTesseractจริง_แถวหักเงินมัดจำที่มีเศษนำหน้า_ไม่ใช่ใบมัดจำ()
    {
        // เดิม: NegationPrefix ต้องขึ้นต้นบรรทัด ⇒ ": = : ie หักเงินมัดจำ/ DEPOSIT" ไม่ถูกนับเป็นการหัก ⇒ ยอด "4" ของบรรทัดหมายเหตุถัดไป = มัดจำ
        Assert.False(OcrDepositMarker.Decide(Norm(MakroPage2of2Tesseract), null).IsDeposit);
        Assert.False(OcrDepositMarker.Decide(OcrPaperSamples.MakroPage2of2, null).IsDeposit);
        Assert.False(OcrDepositMarker.Decide("ยอดหลังหักเงินมัดจำ 9,000.00", null).IsDeposit);
    }

    // ═════════ ครึ่งที่ 2: ใบที่ถูกอยู่แล้ว ห้ามถูกแตะ ═════════

    [Fact]
    public void มัดจำจริง_ยังติดธง_ทั้งบนกระดาษและรายการ()
    {
        Assert.True(OcrDepositMarker.Decide("ใบรับเงินมัดจำ\nรับเงินมัดจำ 5,000.00", null).IsDeposit);
        Assert.True(OcrDepositMarker.Decide("ใบเสร็จรับเงิน", new (string?, decimal?)[] { ("ค่ามัดจำห้องพัก", 1500m) }).IsDeposit);
        Assert.True(OcrDepositMarker.Decide("DEPOSIT RECEIPT\nDeposit 2,000.00", null).IsDeposit);
    }

    [Fact]
    public void Makro3จาก3_คำตัดสินเดิมไม่เปลี่ยน()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.MakroPage3of3, 24110.00m);
        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(23812.25m, r.Total);
        Assert.Equal(OcrTotalRole.PreDiscountTotal, r.EngineRole);
        Assert.Null(OcrTotalAnchor.DocumentOverstatement(OcrPaperSamples.MakroPage3of3, 24110.00m, 23812.25m));
    }

    private const string PlainInvoice =
        "ใบกำกับภาษี\n" +
        "มูลค่าสินค้า 1,000.00\n" +
        "ภาษีมูลค่าเพิ่ม 7% 70.00\n" +
        "รวมทั้งสิ้น 1,070.00\n" +
        "(หนึ่งพันเจ็ดสิบบาทถ้วน)";

    [Fact]
    public void ยอดรวมบวกVATซ้ำที่ไม่มีบนกระดาษ_ยึดยอดจริง_แต่เลขที่พิมพ์บนกระดาษจริงไม่ถูกตีความใหม่()
    {
        // 1,070 + 70 = 1,140 ไม่มีบนกระดาษ ⇒ บทบาท "VAT ซ้ำ" ⇒ ยึด 1,070
        var r = OcrTotalAnchor.Find(PlainInvoice, 1140.00m);
        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(1070.00m, r.Total);
        Assert.Equal(OcrTotalRole.VatAddedTwice, r.EngineRole);

        // ทิศตรงข้าม: 1,140 พิมพ์อยู่บนกระดาษ (ความหมายอื่น) ⇒ อธิบายไม่ได้ ⇒ ไม่เขียนทับ (กติกาเดิม)
        var printed = OcrTotalAnchor.Find(PlainInvoice + "\nวงเงินเครดิตคงเหลือ 1,140.00", 1140.00m);
        Assert.NotEqual(OcrTotalVerdict.Proven, printed.Verdict);
        Assert.NotEqual(OcrTotalRole.VatAddedTwice, printed.EngineRole);

        // ใบที่ถูกอยู่แล้ว: engine ได้ 1,070 ⇒ Confirmed ไม่แตะ
        Assert.Equal(OcrTotalVerdict.Confirmed, OcrTotalAnchor.Find(PlainInvoice, 1070.00m).Verdict);
    }

    [Fact]
    public void ป้ายรวมVAT_ตัวเลขหลังป้ายเท่านั้น_ไม่รวมคำว่าไม่รวม()
    {
        Assert.True(OcrPaperAmounts.IsPrintedAsVatInclusive(OcrPaperSamples.MakroPage2of2, 11208.00m));
        Assert.False(OcrPaperAmounts.IsPrintedAsVatInclusive(OcrPaperSamples.MakroPage2of2, 10341.50m));
        Assert.True(OcrPaperAmounts.IsVatInclusiveOnLine("ยอดที่ต้องเสียภาษี (รวม VAT) 428.00", 428.00m));
        Assert.True(OcrPaperAmounts.IsVatInclusiveOnLine("Total (VAT Included) 1,070.00", 1070.00m));
        // ไม่รวม VAT = ฐาน · ตัวเลขก่อนป้าย = ฐาน (แถวเดียวพิมพ์ทั้งฐานและภาษี)
        Assert.False(OcrPaperAmounts.IsVatInclusiveOnLine("ราคาไม่รวมภาษีมูลค่าเพิ่ม 1,000.00", 1000.00m));
        Assert.False(OcrPaperAmounts.IsVatInclusiveOnLine("มูลค่าสินค้า 1,000.00 รวมภาษี 70.00", 1000.00m));
    }

    [Fact]
    public void ฐานที่พิมพ์บนแถวอื่น_ยังเป็นฐานของชั้นVATได้_แม้เลขเดียวกันอยู่หลังป้ายรวมVAT()
    {
        // เลข 1,000 อยู่ทั้งแถวฐานและแถว "รวม VAT" (คนละความหมาย) — แถวฐานยังใช้ได้ ⇒ 1,070 ยังพิสูจน์ด้วยชั้น VAT
        var text = "มูลค่าก่อนภาษี 1,000.00\nภาษีมูลค่าเพิ่ม 70.00\nรวมทั้งสิ้น 1,070.00\nบัตรกำนัล (รวม VAT) 1,000.00 หมดเขต";
        var r = OcrTotalAnchor.Find(text, 1070.00m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Contains(r.Candidates.Single(c => c.Amount == 1070.00m).Evidence, e => e.Kind == OcrTotalEvidenceKind.VatClosure);
    }

    [Fact]
    public void แถวตารางที่มีตัวเลขต่อท้าย_ยังไม่ใช่แถวตารางสรุป()
    {
        // ผ่อนเฉพาะเครื่องหมายวรรคตอนท้ายแถว — ตัวเลขท้ายแถว (คอลัมน์ที่หก) = ไม่ใช่รูปตาราง ⇒ ทั้งตาราง None เหมือนเดิม
        const string withDigit =
            "2 1 1,220.00 0.00 1,220.00\n" +
            "59 2 9,121.50 638.50 9,760.00 12\n" +
            "รวม 10,341.50 638.50 10,980.00\n" +
            "รหัส ภ.พ. 1=สินค้าได้รับการยกเว้นภาษีมูลค่าเพิ่ม 2=สินค้าที่ต้องเสียภาษีมูลค่าเพิ่ม";
        Assert.False(OcrLineVatMarks.ReadGroups(withDigit).Found);
        Assert.True(OcrLineVatMarks.ReadGroups(withDigit.Replace("9,760.00 12", "9,760.00 ,")).AllKnown);
    }
}
