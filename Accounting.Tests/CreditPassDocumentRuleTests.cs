using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;
using A = Accounting.Helpers.CreditPassDocumentRule.DocAction;

namespace Accounting.Tests;

/// <summary>
/// ด่านเอกสารขายที่ผูกบัตรคอร์ส (ทีม L · ฝ่ายค้านรอบ 1 ข้อ 1–2 · <c>Helpers/CreditPassDocumentRule</c>) — ล็อกทั้งสองทิศ:
/// ใบที่ใช้เครดิตแล้วยกเลิก/ลดหนี้ผิดยอดไม่ได้ · ใบที่ยังไม่ใช้ยกเลิกได้และปิดบัตร · ใบไม่ผูกบัตรไม่ถูกแตะ
/// </summary>
public class CreditPassDocumentRuleTests
{
    private static CreditPassDocumentRule.PassFacts Pass(decimal used = 0m, decimal reserved = 0m, decimal recognized = 0m,
        decimal refunded = 0m, CreditPassStatus status = CreditPassStatus.Active, decimal contract = 4205.61m)
        => new("CP-202610-0001", status, used, reserved, contract, recognized, refunded);

    [Fact]
    public void ยกเลิกใบขาย_บัตรยังไม่ใช้_ยกเลิกได้และปิดบัตร()
        => Assert.Equal(A.CancelPass, CreditPassDocumentRule.OnVoidSale(Pass(), "TIV-1").Action);

    [Fact]
    public void ยกเลิกใบขาย_ใช้แล้วหรือจองค้าง_บล็อกพร้อมยอดที่ลดหนี้ได้()
    {
        var used = CreditPassDocumentRule.OnVoidSale(Pass(used: 4m, recognized: 1682.24m), "TIV-1");
        Assert.Equal(A.Block, used.Action);
        Assert.Contains("2,523.37", used.Message);   // ทางไปต่อ: ใบลดหนี้ส่วนที่ยังไม่ใช้
        Assert.Equal(A.Block, CreditPassDocumentRule.OnVoidSale(Pass(reserved: 1m), "TIV-1").Action);
        var exhausted = CreditPassDocumentRule.OnVoidSale(Pass(used: 10m, recognized: 4205.61m, status: CreditPassStatus.Exhausted), "TIV-1");
        Assert.Equal(A.Block, exhausted.Action);
        Assert.Contains("ครบแล้ว", exhausted.Message);
    }

    [Fact]
    public void ใบที่ไม่ผูกบัตร_หรือบัตรปิดแล้ว_ไม่ถูกแตะ()
    {
        Assert.Equal(A.None, CreditPassDocumentRule.OnVoidSale(null, "TIV-1").Action);
        Assert.Equal(A.None, CreditPassDocumentRule.OnVoidSale(Pass(used: 3m, status: CreditPassStatus.Cancelled), "TIV-1").Action);
        Assert.Equal(A.None, CreditPassDocumentRule.OnCreditNote(null, "TIV-1", 100m, true).Action);
        Assert.Equal(A.None, CreditPassDocumentRule.OnVoidCreditNote(null, "CN-1").Action);
    }

    [Fact]
    public void ใบลดหนี้_เท่าส่วนที่ยังไม่ใช้_ผ่านและปิดบัตร()
    {
        var p = Pass(used: 4m, recognized: 1682.24m);
        Assert.Equal(A.CancelPass, CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 2523.37m, true).Action);
        Assert.Equal(A.CancelPass, CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 2523.38m, true).Action);   // ปัดสตางค์ ±0.01
    }

    [Fact]
    public void ใบลดหนี้_ผิดยอด_ผิดผัง_จองค้าง_หรือรับรู้ครบ_บล็อก()
    {
        var p = Pass(used: 4m, recognized: 1682.24m);
        var partial = CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 1000m, true);
        Assert.Equal(A.Block, partial.Action);
        Assert.Contains("2,523.37", partial.Message);
        Assert.Equal(A.Block, CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 2523.37m, false).Action);
        Assert.Equal(A.Block, CreditPassDocumentRule.OnCreditNote(Pass(used: 4m, reserved: 1m, recognized: 1682.24m), "TIV-1", 2523.37m, true).Action);
        Assert.Equal(A.Block, CreditPassDocumentRule.OnCreditNote(Pass(used: 10m, recognized: 4205.61m), "TIV-1", 0m, true).Action);
        // คืนไปแล้วบางส่วนนับในยอดคงเหลือ
        Assert.Equal(2000m, Pass(recognized: 1000m, refunded: 1205.61m).Deferred);
    }

    [Fact]
    public void ยกเลิกใบลดหนี้ที่ปิดบัตร_บล็อก()
        => Assert.Equal(A.Block, CreditPassDocumentRule.OnVoidCreditNote("CP-202610-0001", "CN-1").Action);

    [Fact]
    public void เปิดบัตรจากเอกสาร_ยึดยอดJE_และสายเอกสาร()
    {
        Assert.Null(CreditPassDocumentRule.ActivateProblem("TIV-1", 4205.61m, null));
        // ใบเสร็จที่ตัดลูกหนี้ใบแจ้งหนี้: JE ไม่ Cr 21713 ⇒ 0 ⇒ ไม่เปิด (เดิมเปิดได้จากยอดบรรทัด = สองบัตรจากเงินก้อนเดียว)
        Assert.Contains("ไม่ได้ลงเครดิต", CreditPassDocumentRule.ActivateProblem("RE-1", 0m, null));
        Assert.Contains("ไม่ได้ลงเครดิต", CreditPassDocumentRule.ActivateProblem("RE-1", -10m, null));
        Assert.Contains("สายเอกสารเดียวกัน", CreditPassDocumentRule.ActivateProblem("RE-1", 4205.61m, "CP-202610-0001"));
    }
}
