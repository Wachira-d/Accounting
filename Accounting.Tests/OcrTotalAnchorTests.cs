using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 192 (Total-first) — <see cref="OcrTotalAnchor"/>: "ยอดไหนคือยอดรวมทั้งสิ้นของเอกสาร"
///
/// <para><b>ครึ่งที่ 1 (พัง → ถูก)</b>: ใบ M Makro ป้าย "TOTAL 24,110.00" เป็นยอดก่อนหักส่วนลด ⇒ ยึด 23,812.25 ·
/// ใบ U Shopee ถ้า engine หยิบ "ยอดชำระ 438" ⇒ ยึด 536 (ยอดที่ VAT ถูกคิด) · ยอดสองค่าที่ต่างมีหลักฐาน ⇒ ไม่เลือกเอง</para>
/// <para><b>ครึ่งที่ 2 (ห้ามแตะ)</b>: Wine Pro · ร้านวัสดุ · ซูเปอร์ · ค้าส่งผสม · Makro 951/49/1,000 · ลักกี้เวย์ ·
/// IKEA ราคารวม VAT · หัก ณ ที่จ่ายที่พิมพ์ · มัดจำ · ส่งออก 0% · แถวเงินทอน — ต้อง<b>ไม่มีคำตัดสินที่แตะค่า</b>
/// (Proven/Conflict/Unsure) และยอดที่ยืนยันต้องเท่าค่าเดิม</para>
/// </summary>
public class OcrTotalAnchorTests
{
    private static void AssertNoAction(OcrTotalAnchorResult r)
    {
        Assert.NotEqual(OcrTotalVerdict.Proven, r.Verdict);
        Assert.NotEqual(OcrTotalVerdict.Conflict, r.Verdict);
        Assert.NotEqual(OcrTotalVerdict.Unsure, r.Verdict);
        Assert.Null(OcrTotalAnchor.Note(r));
    }

    // ── ครึ่งที่ 1: ใบที่พัง ต้องได้ยอดที่ถูก ─────────────────────────────────

    [Fact]
    public void Makro_ป้ายTOTALเป็นยอดก่อนลด_ยอดรวมคือ23812_25_Proven()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.MakroPage3of3, 24110.00m);

        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(23812.25m, r.Total);
        Assert.Equal(OcrTotalRole.PreDiscountTotal, r.EngineRole);   // 24,110 − 297.75 = 23,812.25
        // คู่ฐาน/VAT ที่พิมพ์ซึ่งปิดยอด (แถว "รวม" ของตารางรหัส ภ.พ.) — ใช้แทน VAT ที่เส้น Tesseract แต่ง
        Assert.Equal(22663.97m, r.PrintedBase);
        Assert.Equal(1148.28m, r.PrintedVat);
        var winner = r.Candidates.Single(c => c.Amount == 23812.25m);
        Assert.True(winner.Strong);
        Assert.Equal(4, winner.IndependentClasses);   // VAT · ตัวอักษร · ป้าย · แถวชำระ
        Assert.StartsWith(OcrTotalAnchor.AnchoredTag, OcrTotalAnchor.Note(r));
        Assert.Contains("297.75", r.Reason);
    }

    [Fact]
    public void Makro_engineอ่าน23812_25อยู่แล้ว_Confirmed_ไม่แตะ()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.MakroPage3of3, 23812.25m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Equal(23812.25m, r.Total);
        Assert.Null(OcrTotalAnchor.Note(r));
    }

    [Fact]
    public void Makro_engineไม่ได้ยอดรวม_เติม23812_25จากหลักฐานบนกระดาษ()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.MakroPage3of3, null);
        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(23812.25m, r.Total);
    }

    [Fact]
    public void Uptoyou_engineหยิบยอดชำระ438_ยึด536ที่VATถูกคิด_ค่าเดิมคือยอดชำระ()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.UptoyouShopee, 438.00m);

        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(536.00m, r.Total);
        Assert.Equal(OcrTotalRole.AmountSettled, r.EngineRole);   // 536 − ส่วนลดพิเศษ 98 = 438
        Assert.Equal(500.93m, r.PrintedBase);
        Assert.Equal(35.07m, r.PrintedVat);
        // 438 มีป้าย + แถวชำระ แต่ไม่มีชั้น VAT (438 × 7/107 = 28.65 ≠ 35.07) ⇒ ไม่มีวันเป็นยอดรวมทั้งสิ้น
        Assert.False(r.Candidates.Single(c => c.Amount == 438.00m).Strong);
    }

    [Fact]
    public void Uptoyou_engineอ่าน536_Confirmed()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.UptoyouShopee, 536.00m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Equal(536.00m, r.Total);
    }

    [Fact]
    public void Scommerce_ยอด5024ยืนยันสามทาง_Confirmed()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.ScommerceLazada, 5024.00m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Equal(4695.33m, r.PrintedBase);
        Assert.Equal(328.67m, r.PrintedVat);
    }

    [Fact]
    public void สองยอดต่างมีหลักฐาน_อธิบายกันไม่ได้_Conflict_ไม่แตะค่า_แท็กห้ามอนุมัติ()
    {
        // ตัวอักษรพูด 1,250 แต่ฐาน+VAT+ป้ายพูด 1,520 และส่วนต่าง 270 ไม่มีบนกระดาษ
        const string paper =
            "ใบกำกับภาษี\nมูลค่าสินค้า 1,420.56\nภาษีมูลค่าเพิ่ม 7% 99.44\nรวมทั้งสิ้น 1,520.00\n(หนึ่งพันสองร้อยห้าสิบบาทถ้วน)";
        var r = OcrTotalAnchor.Find(paper, 1250.00m);

        Assert.Equal(OcrTotalVerdict.Conflict, r.Verdict);
        var note = OcrTotalAnchor.Note(r);
        Assert.StartsWith(OcrTotalAnchor.ConflictTag, note);
        Assert.Contains("1,520.00", note);
        Assert.Contains("1,250.00", note);
        // แท็กนี้ต้องหยุดการอนุมัติอัตโนมัติจริง (ตัวตัดสินตัวเดียวของทุกช่องทาง)
        Assert.False(OcrPostingReadiness.Evaluate(note, hasUsableDate: true).CanAutoApprove);
    }

    // ── ครึ่งที่ 2: ใบที่ถูกอยู่แล้ว ต้องไม่ถูกแตะ ────────────────────────────

    [Theory]
    [InlineData("winepro", 3593.00)]
    [InlineData("hardware", 1418.02)]
    [InlineData("supermarket", 735.30)]
    public void ใบรอบ190ที่ถูกอยู่แล้ว_Confirmedด้วยยอดเดิม(string paper, double engineTotal)
    {
        var text = paper switch
        {
            "winepro" => OcrPaperSamples.WinePro,
            "hardware" => OcrPaperSamples.HardwareBillDiscount,
            _ => OcrPaperSamples.SupermarketMemberDiscount,
        };
        var r = OcrTotalAnchor.Find(text, (decimal)engineTotal);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Equal((decimal)engineTotal, r.Total);
        Assert.Null(OcrTotalAnchor.Note(r));
    }

    [Fact]
    public void ค้าส่งผสม_VATไม่ใช่7ส่วน107ของยอดรวม_ไม่มีคำตัดสินที่แตะค่า()
        => AssertNoAction(OcrTotalAnchor.Find(OcrPaperSamples.WholesaleMixedVat, 792m));

    [Theory]
    // ลักกี้เวย์: หลังสลับป้ายแล้ว (1,070) และก่อนสลับ (1,000) — ชั้นนี้ไม่สลับเอง
    [InlineData("บริษัท ลักกี้เวย์ จำกัด\nรวมเป็นเงิน 1,070.00\nภาษีมูลค่าเพิ่ม 70.00\nจำนวนเงินรวมทั้งสิ้น 1,000.00", 1070.00)]
    [InlineData("บริษัท ลักกี้เวย์ จำกัด\nรวมเป็นเงิน 1,070.00\nภาษีมูลค่าเพิ่ม 70.00\nจำนวนเงินรวมทั้งสิ้น 1,000.00", 1000.00)]
    // แถวฟอร์มมัดจำยอด 0 · ใบมัดจำจริง · หัก ณ ที่จ่ายพิมพ์ (ยอดชำระสุทธิ 10,400 ต้องไม่ชนะ) · ไม่พิมพ์ส่วนหัก · ส่งออก 0%
    [InlineData("ใบเสร็จรับเงิน\nค่าสินค้า 1,000.00\nหักเงินมัดจำ 0.00\nรวมสุทธิ 1,000.00", 1000.00)]
    [InlineData("ใบรับเงินมัดจำ\nเงินมัดจำค่าก่อสร้าง 50,000.00\nรวม 50,000.00", 50000.00)]
    [InlineData("ใบแจ้งหนี้ค่าบริการ\nค่าบริการ 10,000.00\nภาษีมูลค่าเพิ่ม 7% 700.00\nรวม 10,700.00\nหัก ณ ที่จ่าย 3% 300.00\nยอดชำระสุทธิ 10,400.00", 10700.00)]
    [InlineData("ใบแจ้งหนี้ค่าบริการ\nค่าที่ปรึกษา 20,000.00\nภาษีมูลค่าเพิ่ม 7% 1,400.00\nรวม 21,400.00", 21400.00)]
    [InlineData("INVOICE (EXPORT)\nGoods 100,000.00\nVAT 0% 0.00\nTotal 100,000.00", 100000.00)]
    public void ใบในชุดreplayเดิม_ไม่มีคำตัดสินที่แตะค่า(string paper, double engineTotal)
        => AssertNoAction(OcrTotalAnchor.Find(paper, (decimal)engineTotal));

    [Fact]
    public void Makro951_49_1000_ยอดเดิมถูก_Confirmed()
    {
        var r = OcrTotalAnchor.Find(
            "บริษัท สยามแม็คโคร จำกัด (มหาชน)\nรวมเงิน 951.00\nภาษีมูลค่าเพิ่ม 7% 49.00\nรวมทั้งสิ้น 1,000.00", 1000m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Equal(1000m, r.Total);
    }

    [Fact]
    public void IKEA_ราคารวมVAT_Confirmed()
    {
        var r = OcrTotalAnchor.Find("IKEA\nรวมทั้งสิ้น 1,396.00\nภาษีมูลค่าเพิ่ม 7% 91.33\nมูลค่าสินค้า 1,304.67", 1396m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
    }

    [Fact]
    public void แถวเงินสดรับ_เงินทอน_ไม่ใช่หลักฐานของยอดรวม()
    {
        // บทเรียน Makro 951/49/1,000 (T2-09): 1,000 = เงินสดรับ · 49 = เงินทอน — ต้องไม่เป็นผู้สมัครเลย
        var r = OcrTotalAnchor.Find("ร้านค้า\nรวมทั้งสิ้น 951.00\nภาษีมูลค่าเพิ่ม 62.21\nเงินสด 1,000.00\nเงินทอน 49.00", 951m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.DoesNotContain(r.Candidates, c => c.Amount == 1000m);
        Assert.DoesNotContain(r.Candidates, c => c.Amount == 49m);
    }

    [Fact]
    public void ป้ายเดียวไม่มีชั้นVAT_ไม่เขียนทับ()
    {
        // ใบไม่มี VAT: ป้ายพูด 900 · engine อ่าน 1,000 — ไม่มีชั้น VAT ⇒ ไม่มีวัน "พิสูจน์" (พฤติกรรมเดิม)
        AssertNoAction(OcrTotalAnchor.Find("ใบเสร็จ\nค่าสินค้า 1,000.00\nยอดสุทธิ 900.00", 1000m));
    }

    [Fact]
    public void ไม่มีข้อความ_NotChecked()
        => Assert.Equal(OcrTotalVerdict.NotChecked, OcrTotalAnchor.Find("  ", 100m).Verdict);

    // ── รอบฝ่ายค้าน (C1 · C2 · C4 · C5) ──────────────────────────────────────

    private const string RoundingReceipt =
        "ร้านค้าปลีก\nมูลค่าสินค้า 1,153.50\nภาษีมูลค่าเพิ่ม 7% 80.75\nรวมทั้งสิ้น 1,234.25\nปัดเศษ -0.25\n"
        + "ยอดชำระ 1,234.00\nเงินสด 1,300.00\nเงินทอน 66.00";

    [Fact]
    public void C1_ใบปัดเศษ_ยอดรวม1234_25_ยอดชำระ1234ไม่ใช่คู่แข่ง_ไม่มีConflict()
    {
        // เดิม: 1,234.00 × 7/107 = 80.73 ≈ 80.75 (±0.02) ⇒ "พิสูจน์ได้" ทั้งคู่ ⇒ [TOTAL-CONFLICT] บล็อกใบค้าปลีกทั่วไป
        var r = OcrTotalAnchor.Find(RoundingReceipt, 1234.25m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Equal(1234.25m, r.Total);
        Assert.False(r.Candidates.Single(c => c.Amount == 1234.00m).Strong);   // อัตราส่วนแพ้ฐาน+VAT ที่พิมพ์ตรงเป๊ะ
        Assert.Null(OcrTotalAnchor.Note(r));
    }

    [Fact]
    public void C1_engineหยิบยอดชำระหลังปัดเศษ_ยึด1234_25_ค่าเดิมคือยอดปัดเศษที่พิมพ์แถวไว้()
    {
        var r = OcrTotalAnchor.Find(RoundingReceipt, 1234.00m);
        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(1234.25m, r.Total);
        Assert.Equal(OcrTotalRole.RoundedPayable, r.EngineRole);
        // ไม่มี [PAY≠TOTAL]: ส่วนต่าง < 1 บาทที่กระดาษพิมพ์แถว "ปัดเศษ" เป็นเรื่องปกติของเงินสด (ไม่ใช่ส่วนลด/คูปอง)
        Assert.True(OcrPostingReadiness.Evaluate(OcrTotalAnchor.Note(r), true).CanAutoApprove);
    }

    [Fact]
    public void C1_ทิศตรงข้าม_ส่วนต่างเศษสตางค์ที่ไม่มีแถวปัดเศษ_engineหยิบยอดที่ไม่ใช่ยอดVAT_ยังดัง()
    {
        const string paper = "ร้านค้าปลีก\nมูลค่าสินค้า 1,153.50\nภาษีมูลค่าเพิ่ม 7% 80.75\nรวมทั้งสิ้น 1,234.25\nยอดชำระ 1,234.00";
        Assert.Equal(OcrTotalVerdict.Confirmed, OcrTotalAnchor.Find(paper, 1234.25m).Verdict);
        // ส่วนต่าง 0.25 ไม่มีอะไรบนกระดาษอธิบาย ⇒ ไม่เขียนทับ · คนตัดสิน
        Assert.Equal(OcrTotalVerdict.Conflict, OcrTotalAnchor.Find(paper, 1234.00m).Verdict);
    }

    [Theory]
    [InlineData("INVOICE\nAmount (USD) 1,000.00\nVAT 7% (USD) 70.00\nTotal (USD) 1,070.00\nExchange rate 34.00\n"
        + "Amount (THB) 34,000.00\nVAT (THB) 2,380.00\nTotal (THB) 36,380.00", 1070.00)]
    [InlineData("INVOICE\nAmount 1,000.00\nVAT 7% 70.00\nTotal 1,070.00\nAmount (THB) 34,000.00\nVAT (THB) 2,380.00\nTotal (THB) 36,380.00", 1070.00)]
    [InlineData("INVOICE\nAmount 1,000.00\nVAT 7% 70.00\nTotal 1,070.00\nAmount (THB) 34,000.00\nVAT (THB) 2,380.00\nTotal (THB) 36,380.00", 36380.00)]
    public void C2_ใบสองสกุลเงิน_ไม่รู้_ไม่ใช่ขัดกัน(string paper, double engineTotal)
    {
        var r = OcrTotalAnchor.Find(paper, (decimal)engineTotal);
        Assert.Equal(OcrTotalVerdict.Unknown, r.Verdict);
        Assert.Null(OcrTotalAnchor.Note(r));
    }

    [Theory]
    [InlineData("CREDIT NOTE\nOriginal invoice no. INV-001\nOriginal invoice amount 10,000.00\nVAT on original invoice 700.00\n"
        + "Original invoice total 10,700.00\nCorrect amount 8,000.00\nDifference 2,000.00\nVAT 7% 140.00\nGrand Total 2,140.00")]
    [InlineData("ใบลดหนี้\nมูลค่าตามใบกำกับภาษีเดิม 10,000.00\nภาษีมูลค่าเพิ่มตามใบกำกับภาษีเดิม 700.00\nมูลค่าที่ถูกต้อง 8,000.00\n"
        + "ผลต่าง 2,000.00\nภาษีมูลค่าเพิ่ม 7% 140.00\nรวมทั้งสิ้น 2,140.00")]
    public void C2_ใบลดหนี้_ยอดของใบเดิมไม่ใช่คู่แข่ง_ยอดใบนี้Confirmed(string paper)
    {
        var r = OcrTotalAnchor.Find(paper, 2140m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Equal(2140m, r.Total);
        Assert.DoesNotContain(r.Candidates, c => c.Amount == 10700m && c.Strong);
    }

    [Fact]
    public void C4_ฐานที่คำนวณจากยอดลบVAT_ไม่ถูกประทับว่าพิมพ์บนกระดาษ()
    {
        // มีแต่ VAT 70.00 กับยอด 1,070.00 พิมพ์ (ไม่มีแถวฐาน) · engine หยิบ 1,000 (ไม่ได้พิมพ์ = ยอดก่อน VAT)
        const string paper = "ใบกำกับภาษี\nภาษีมูลค่าเพิ่ม 7% 70.00\nรวมทั้งสิ้น 1,070.00\n(หนึ่งพันเจ็ดสิบบาทถ้วน)";
        var r = OcrTotalAnchor.Find(paper, 1000m);
        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(1070m, r.Total);
        Assert.Equal(OcrTotalRole.NetBeforeVat, r.EngineRole);
        Assert.Null(r.PrintedBase);        // 1,000 ไม่ได้พิมพ์บนกระดาษ
        Assert.Equal(70m, r.PrintedVat);

        var plan = OcrTotalAnchor.Plan(r, currentSub: null, currentVat: null);
        Assert.Equal(1070m, plan.Total);
        Assert.Equal(1000m, plan.SubTotal);
        Assert.Equal(OcrFieldSource.Rule, plan.SubSource);                 // ไม่ใช่ PaperLabel
        Assert.True(plan.SubConfidence < 0.85);                            // ไฮไลต์เหลือง
        Assert.Equal(70m, plan.Vat);
    }

    [Fact]
    public void Plan_Makroเส้นข้อความ_ฐานVATจากแถวรวมที่พิมพ์_PaperLabel()
    {
        // เส้น Tesseract: AmountTriple เติม 16,403.97/1,148.28 (กลุ่มย่อย) ⇒ ไม่ปิดยอดใหม่ ⇒ ใช้คู่ที่พิมพ์บนแถว "รวม"
        var r = OcrTotalAnchor.Find(OcrPaperSamples.MakroPage3of3, 24110m);
        var plan = OcrTotalAnchor.Plan(r, 16403.97m, 1148.28m);
        Assert.Equal(23812.25m, plan.Total);
        Assert.Equal(22663.97m, plan.SubTotal);
        Assert.Equal(OcrFieldSource.PaperLabel, plan.SubSource);
        Assert.Equal(1148.28m, plan.Vat);
        Assert.StartsWith(OcrTotalAnchor.AnchoredTag, plan.Note);
    }

    [Fact]
    public void Plan_ทิศตรงข้าม_Azureฐานปิดยอดอยู่แล้ว_ไม่แตะฐาน_Confirmedไม่เขียนอะไร()
    {
        var proven = OcrTotalAnchor.Plan(OcrTotalAnchor.Find(OcrPaperSamples.MakroPage3of3, 24110m), 22663.97m, 1148.28m);
        Assert.Equal(23812.25m, proven.Total);
        Assert.Null(proven.SubTotal);
        var confirmed = OcrTotalAnchor.Plan(OcrTotalAnchor.Find(OcrPaperSamples.WinePro, 3593m), 3357.94m, 235.06m);
        Assert.Null(confirmed.Total);
        Assert.Null(confirmed.SubTotal);
        Assert.Null(confirmed.TotalConfidenceCap);
        Assert.Null(confirmed.Note);
        var conflict = OcrTotalAnchor.Plan(OcrTotalAnchor.Find(
            "ใบกำกับภาษี\nมูลค่าสินค้า 1,420.56\nภาษีมูลค่าเพิ่ม 7% 99.44\nรวมทั้งสิ้น 1,520.00\n(หนึ่งพันสองร้อยห้าสิบบาทถ้วน)", 1250m), null, null);
        Assert.Null(conflict.Total);
        Assert.Equal(OcrTotalAnchor.DisputedConfidenceCap, conflict.TotalConfidenceCap);
    }

    [Fact]
    public void C5_แถวรวมของตารางแถวเดียว_นับเป็นหลักฐานชั้นเดียว()
    {
        // "รวมทั้งสิ้น ฐาน VAT รวม" แถวเดียว เข้าทั้งป้ายยอดรวมและแถวรวมของตารางกลุ่ม — เดิมนับเป็นสองชั้น ⇒ "พิสูจน์" จากแถวเดียว
        const string paper = "ใบกำกับภาษี\n   17  1  6,260.00  0.00  6,260.00\n  134  2  16,403.97  1,148.28  17,552.25\n"
            + "รวมทั้งสิ้น 22,663.97 1,148.28 23,812.25\n1=สินค้ายกเว้นภาษีมูลค่าเพิ่ม · 2=สินค้าที่ต้องเสียภาษีมูลค่าเพิ่ม";
        var r = OcrTotalAnchor.Find(paper, 24000m);
        var c = r.Candidates.Single(x => x.Amount == 23812.25m);
        Assert.Equal(1, c.IndependentClasses);
        Assert.False(c.Strong);
        Assert.NotEqual(OcrTotalVerdict.Proven, r.Verdict);
    }

    // ── แท็กใหม่ทั้งสองอยู่ในรายการห้ามอนุมัติเอง (และไม่ใช่ substring ของกันและกัน) ──

    [Fact]
    public void แท็กใหม่_TOTAL_CONFLICT_และ_PAY_NOT_TOTAL_หยุดการอนุมัติอัตโนมัติ()
    {
        Assert.Contains(OcrPostingReadiness.BlockingTags, t => t.Tag == OcrTotalAnchor.ConflictTag);
        Assert.Contains(OcrPostingReadiness.BlockingTags, t => t.Tag == OcrTotalDecomposer.PayNotTotalTag);
        Assert.False(OcrPostingReadiness.Evaluate("[PAY≠TOTAL] ยอดตามใบกำกับ 536.00", true).CanAutoApprove);
    }

    [Fact]
    public void ข้อสังเกต_TOTAL_และ_TOTAL_UNSURE_และ_PAGES_PARTIAL_ไม่บล็อก()
    {
        // ทิศตรงข้าม: ยึดยอดสำเร็จ / ไม่แน่ใจ / หน้าไม่ครบ = ข้อสังเกต ไม่ใช่ตัวหยุด (คำเตือนที่ฟ้องใบถูก = ปิดด่าน)
        Assert.True(OcrPostingReadiness.Evaluate("[TOTAL] ยอดรวมทั้งสิ้น 23,812.25", true).CanAutoApprove);
        Assert.True(OcrPostingReadiness.Evaluate("[TOTAL-UNSURE] ยอด", true).CanAutoApprove);
        Assert.True(OcrPostingReadiness.Evaluate("[PAGES-PARTIAL] หน้า", true).CanAutoApprove);
    }
}
