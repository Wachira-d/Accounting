using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// 2026-10-09 (ตรวจความครอบคลุมเส้นกระดาษ) — **บิลค่าบริการรายเดือนที่มี "ยอดค้างชำระจากรอบก่อน"** (โทรศัพท์ · เน็ต · ไฟฟ้า · ประปา)
///
/// <para>กระดาษพิมพ์ "รวมค่าใช้บริการรอบนี้ 1,070 · ยอดค้างชำระจากรอบบิลก่อน 500 · ยอดรวมที่ต้องชำระ 1,570" — engine หยิบ 1,570
/// (ป้าย "ที่ต้องชำระ"/AmountDue) เป็นยอดรวม ⇒ เอกสาร 1,570 / VAT 70 / ฐาน 1,500 ทั้งที่ใบกำกับนี้คือ 1,070 (ฐาน 1,000) และ 500 เป็นหนี้
/// ของใบก่อนที่ลงบัญชีไปแล้ว ⇒ ค่าใช้จ่าย+เจ้าหนี้เกินทุกเดือนที่มียอดค้าง · <see cref="OcrTotalAnchor"/> เดิมไม่เห็นผู้สมัคร 1,070 เพราะ
/// "รวมค่าใช้บริการรอบนี้" ไม่ใช่ป้ายยอดรวม และ 1,570 มีป้าย+ตัวอักษร (2 ชั้น ไม่มี VAT) ⇒ Unknown ⇒ คงค่าผิด</para>
///
/// <para>กลไก: <see cref="OcrPaperAmounts.PriorBalanceRows"/> (แถวค้างชำระที่มีเงิน · ต้องมีคำว่า "ของรอบก่อน" จริง) → ผู้สมัคร = ป้าย − ยอดค้าง
/// (ต้องพิมพ์บนกระดาษ) → ชั้นหลักฐานที่ 5 <see cref="OcrTotalEvidenceKind.PriorBalanceDecomposition"/> → ค่าที่ engine อ่านได้บทบาท
/// <see cref="OcrTotalRole.PriorBalanceIncluded"/> ⇒ Proven · สองครึ่ง: ใบที่พังกลับมาถูก · ใบที่ถูกอยู่แล้ว/ใบที่ "ค้างชำระ" คือยอดใบนี้ ไม่ถูกแตะ</para>
/// </summary>
public class OcrPriorBalanceTests
{
    // ── ครึ่งที่ 1: ใบที่พัง ต้องกลับมาถูก ─────────────────────────────────────────────────────────────

    [Fact]
    public void บิลเน็ตมีค้าง500_engineหยิบ1570_ต้องยึดใบกำกับรอบนี้1070_ฐานพิมพ์1000_VAT70()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.TelecomBillPriorBalance, 1570m);
        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(1070m, r.Total);
        Assert.Equal(1570m, r.EngineTotal);
        Assert.Equal(OcrTotalRole.PriorBalanceIncluded, r.EngineRole);   // 1,570 − ค้าง 500 = 1,070
        Assert.Equal(1000m, r.PrintedBase);
        Assert.Equal(70m, r.PrintedVat);
        Assert.Contains("ยอดค้างชำระจากรอบก่อน 500.00", r.Reason);

        var plan = OcrTotalAnchor.Plan(r, 1000m, 70m);
        Assert.Equal(1070m, plan.Total);
        Assert.Null(plan.SubTotal);   // ฐาน+VAT ที่ถืออยู่ปิด 1,070 แล้ว — ไม่แตะ
        Assert.StartsWith(OcrTotalAnchor.AnchoredTag, plan.Note);
    }

    [Fact]
    public void ผู้สมัคร1070_มีชั้นVATและชั้นแยกยอดค้าง_สองชั้นอิสระบนคนละบรรทัด()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.TelecomBillPriorBalance, 1570m);
        var winner = r.Candidates.Single(c => c.Amount == 1070m);
        Assert.True(winner.Strong);
        Assert.Equal(2, winner.IndependentClasses);
        Assert.Contains(winner.Evidence, ev => ev.Kind == OcrTotalEvidenceKind.VatClosure);
        Assert.Contains(winner.Evidence, ev => ev.Kind == OcrTotalEvidenceKind.PriorBalanceDecomposition);
        // 1,570 มีป้าย+ตัวอักษร แต่ไม่มี VAT ปิด ⇒ ไม่ "พิสูจน์" (ไม่ใช่การขัดกัน)
        var loser = r.Candidates.Single(c => c.Amount == 1570m);
        Assert.False(loser.Strong);
        Assert.False(loser.HasVatEvidence);
    }

    [Fact]
    public void บิลอังกฤษ_PreviousBalance_TotalAmountDue_เดิมเป็นConflict_ตอนนี้Proven()
    {
        // "Total current charges 1,070.00" เป็นป้าย total อยู่แล้ว (2 ชั้นกับ VAT) — เดิม 1,570 อธิบายไม่ได้ ⇒ [TOTAL-CONFLICT] บล็อกทุกเดือน
        const string text =
            "Monthly Service Invoice / Tax Invoice\n"
            + "Internet 500/500 Mbps                    1,000.00\n"
            + "VAT 7%                                      70.00\n"
            + "Total current charges                    1,070.00\n"
            + "Previous Balance                           500.00\n"
            + "Total Amount Due                         1,570.00\n";
        var r = OcrTotalAnchor.Find(text, 1570m);
        Assert.Equal(OcrTotalVerdict.Proven, r.Verdict);
        Assert.Equal(1070m, r.Total);
        Assert.Equal(OcrTotalRole.PriorBalanceIncluded, r.EngineRole);
    }

    [Theory]
    [InlineData("ยอดค้างชำระจากรอบบิลก่อน 500.00")]
    [InlineData("ยอดยกมา 500.00")]
    [InlineData("ค้างชำระของงวดที่แล้ว 500.00")]
    [InlineData("Previous Balance 500.00")]
    [InlineData("Balance B/F 500.00")]
    [InlineData("Balance brought forward 500.00")]
    public void แถวยอดค้างจากรอบก่อน_อ่านได้ทุกรูปที่บิลไทยพิมพ์(string row)
    {
        var rows = OcrPaperAmounts.PriorBalanceRows("ค่าบริการ 1,000.00\n" + row + "\nยอดรวมที่ต้องชำระ 1,570.00");
        Assert.Single(rows);
        Assert.Equal(500m, rows[0].Amount);
        Assert.Equal(1, rows[0].LineNo);
    }

    // ── ครึ่งที่ 2: ใบที่ถูกอยู่แล้ว / ใบที่คำว่า "ค้างชำระ" คือยอดของใบนี้ — ห้ามแตะ ────────────────────────

    [Fact]
    public void บิลรอบไม่มีค้าง_แถวฟอร์มค้างชำระศูนย์_Confirmed1070_ไม่มีแถวค้าง()
    {
        Assert.Empty(OcrPaperAmounts.PriorBalanceRows(OcrPaperSamples.TelecomBillNoArrears));   // แถวยอด 0 ไม่ใช่หลักฐาน (RG-02)
        var r = OcrTotalAnchor.Find(OcrPaperSamples.TelecomBillNoArrears, 1070m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Equal(1070m, r.Total);
        Assert.Null(OcrTotalAnchor.Note(r));
    }

    [Fact]
    public void บิลมีค้าง_แต่engineอ่าน1070ถูกอยู่แล้ว_Confirmed_ไม่เขียนทับ()
    {
        var r = OcrTotalAnchor.Find(OcrPaperSamples.TelecomBillPriorBalance, 1070m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Equal(1070m, r.Total);
        Assert.Equal(OcrTotalRole.TaxInvoiceTotal, r.EngineRole);
    }

    [Fact]
    public void ใบวางบิล_ยอดค้างชำระคือยอดของใบนี้_ไม่มีคำว่ารอบก่อน_ไม่นับเป็นยอดยกมา()
    {
        // "ยอดค้างชำระ 10,700" บนใบวางบิล/ใบแจ้งหนี้ = ยอดที่ต้องชำระของใบนี้ — ถ้านับเป็นยอดยกมาจะได้ผู้สมัคร 0/ติดลบ แล้วอธิบายผิดเรื่อง
        const string text =
            "ใบวางบิล/ใบแจ้งหนี้\n"
            + "ค่าบริการที่ปรึกษา                 10,000.00\n"
            + "ภาษีมูลค่าเพิ่ม 7%                    700.00\n"
            + "รวมเงินทั้งสิ้น                    10,700.00\n"
            + "ยอดค้างชำระ                        10,700.00\n";
        Assert.Empty(OcrPaperAmounts.PriorBalanceRows(text));
        var r = OcrTotalAnchor.Find(text, 10700m);
        Assert.Equal(OcrTotalVerdict.Confirmed, r.Verdict);
        Assert.Equal(10700m, r.Total);
    }

    [Fact]
    public void แถวค้างชำระที่ไม่พิมพ์ยอดใบกำกับรอบนี้ไว้_ไม่แต่งผู้สมัครขึ้นเอง_คงพฤติกรรมเดิม()
    {
        // ไม่มีบรรทัด "รวมค่าใช้บริการรอบนี้ 1,070" บนกระดาษ ⇒ 1,570 − 500 = 1,070 ไม่ได้พิมพ์ ⇒ ไม่เพิ่มผู้สมัคร (F2 ข้อ 3: ไม่แต่งตัวเลขที่ไม่มีบนใบ)
        const string text =
            "ใบแจ้งค่าใช้บริการ/ใบกำกับภาษี\n"
            + "ค่าบริการรายเดือน                  1,000.00\n"
            + "ภาษีมูลค่าเพิ่ม 7%                     70.00\n"
            + "ยอดค้างชำระจากรอบบิลก่อน              500.00\n"
            + "ยอดรวมที่ต้องชำระ                   1,570.00\n";
        var r = OcrTotalAnchor.Find(text, 1570m);
        Assert.DoesNotContain(r.Candidates, c => c.Amount == 1070m);
        Assert.NotEqual(OcrTotalVerdict.Proven, r.Verdict);
    }

    [Fact]
    public void กระดาษชุดเดิมในreplay_ไม่มีใบไหนมีแถวค้างชำระ_คำตอบเดิมไม่เปลี่ยน()
    {
        foreach (var p in OcrReplayHarness.Corpus.Where(x => !x.Name.StartsWith("telecom-", StringComparison.Ordinal)))
            Assert.Empty(OcrPaperAmounts.PriorBalanceRows(p.RawText));
    }
}
