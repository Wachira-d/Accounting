using System.Globalization;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// JE นำส่ง ภ.พ.30 — **invariant คือ Dr = Cr เสมอ** ไม่ใช่ยอดของบรรทัดใดบรรทัดหนึ่ง
///
/// เทสต์ที่เช็คยอดรายบรรทัดจับคลาสบั๊กนี้ไม่ได้ เพราะทุกบรรทัด "ดูสมเหตุสมผล"
/// ตอนที่สูตรพัง (บทเรียนเดียวกับตารางค่าเสื่อม declining balance ใน CLAUDE.md)
/// จำลอง: tools/vat_remittance_je_sim.py — ก่อนแก้ไม่สมดุล 3/5 · หลังแก้ 0/5
/// </summary>
public class VatRemittanceJournalTests
{
    private static void AssertBalanced(VatRemittanceJournal.Plan p)
        => Assert.Equal(p.ClearOutputVat, p.ClearInputVat + p.PayFromBank);

    // ═══ invariant: ทุกชุดตัวเลขที่รับได้ ต้องสมดุล ═══

    // ⚠️ ส่งเป็นสตริงแล้ว parse — [InlineData(1234.56)] เป็น double ซึ่ง xUnit
    // แปลงเป็น decimal ให้ไม่ได้ (โยน error ตอนรัน ไม่ใช่ตอน compile)
    [Theory]
    [InlineData("7000", "3000", "4000", "0")]          // ไม่มีเครดิตยกมา (เคสปกติ — ต้องไม่เปลี่ยน)
    [InlineData("7000", "3000", "2800", "1200")]       // มีเครดิตยกมา 1,200 (เคยไม่สมดุล)
    [InlineData("7000", "3000", "500", "3500")]        // เครดิตยกมาใหญ่กว่าส่วนต่าง (เคยไม่สมดุล)
    [InlineData("5000", "0", "5000", "0")]             // ไม่มีภาษีซื้อเลย (ต้องไม่เปลี่ยน)
    [InlineData("1234.56", "987.65", "100.00", "146.91")] // เศษสตางค์ (เคยไม่สมดุล)
    public void ทุกงวดต้องสมดุล_และแยกเครดิตยกมาได้ถูก(
        string outputText, string inputText, string payText, string carryForwardText)
    {
        var output = decimal.Parse(outputText, CultureInfo.InvariantCulture);
        var input = decimal.Parse(inputText, CultureInfo.InvariantCulture);
        var pay = decimal.Parse(payText, CultureInfo.InvariantCulture);
        var expectedCarryForward = decimal.Parse(carryForwardText, CultureInfo.InvariantCulture);

        var plan = VatRemittanceJournal.Build(output, input, pay);
        AssertBalanced(plan);
        Assert.Equal(expectedCarryForward, plan.CarryForwardUsed);
        Assert.Equal(pay, plan.PayFromBank);          // เงินที่จ่ายต้องเท่ายอดสุทธิเป๊ะ
        Assert.Equal(input + expectedCarryForward, plan.ClearInputVat);
    }

    [Fact]
    public void เครดิตยกมาต้องถูกล้างออกจาก_11610_ไม่ใช่ทิ้งค้างไว้()
    {
        // งวดก่อนภาษีซื้อมากกว่าภาษีขาย ⇒ ไม่มี JE มาปิด 11610 ⇒ ยอดค้างมาถึงงวดนี้
        var plan = VatRemittanceJournal.Build(outputVat: 10_000m, inputVat: 2_000m, amountToPay: 6_500m);
        Assert.Equal(1_500m, plan.CarryForwardUsed);
        Assert.Equal(3_500m, plan.ClearInputVat);     // 2,000 ของงวด + 1,500 ยกมา
        AssertBalanced(plan);
    }

    // ═══ ทิศ "ต้องปฏิเสธ" — ห้ามเดา ห้ามเงียบ ═══

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public void งวดที่ไม่มียอดต้องชำระ_ต้องปฏิเสธพร้อมบอกว่ายังต้องยื่นแบบ(int pay)
    {
        var ex = Assert.Throws<BusinessRuleException>(
            () => VatRemittanceJournal.Build(3_000m, 5_000m, pay));
        Assert.Equal("VAT-REMIT-NO-AMOUNT", ex.RuleCode);
        Assert.Contains("ยื่นแบบ", ex.Message);       // ต้องมีทางไปต่อ ไม่ใช่ตันเฉย ๆ
    }

    [Fact]
    public void ยอดที่จะจ่ายมากกว่าขายหักซื้อ_คือรายงานขัดกันเอง_ต้องปฏิเสธ()
    {
        // pay > output − input เป็นไปไม่ได้ (เครดิตยกมาติดลบ) = ตัวเลขรายงานเพี้ยน
        var ex = Assert.Throws<BusinessRuleException>(
            () => VatRemittanceJournal.Build(7_000m, 3_000m, 5_000m));
        Assert.Equal("VAT-REMIT-MISMATCH", ex.RuleCode);
    }

    [Fact]
    public void ภาษีขายหรือภาษีซื้อติดลบ_ต้องปฏิเสธ()
    {
        Assert.Throws<BusinessRuleException>(() => VatRemittanceJournal.Build(-1m, 0m, 1m));
        Assert.Throws<BusinessRuleException>(() => VatRemittanceJournal.Build(1m, -1m, 1m));
    }

    // ═══ กวาดช่วงค่าจริง — invariant ต้องจริงทุกจุด ไม่ใช่เฉพาะเคสที่นึกออก ═══

    [Fact]
    public void กวาดทุกชุดตัวเลขที่รับได้_Dr_ต้องเท่ากับ_Cr_ทุกจุด()
    {
        for (var output = 100m; output <= 20_000m; output += 137.13m)
            for (var input = 0m; input < output; input += 311.07m)
                for (var pay = 0.01m; pay <= output - input; pay += 419.91m)
                    AssertBalanced(VatRemittanceJournal.Build(output, input, pay));
    }
}
