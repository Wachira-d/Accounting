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
