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
        decimal refunded = 0m, CreditPassStatus status = CreditPassStatus.Active, decimal contract = 4205.61m,
        decimal total = 10m, CreditUnit unit = CreditUnit.Session)
        => new("CP-202610-0001", status, used, reserved, contract, recognized, refunded, total, unit);

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
        var over = CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 2600m, true);
        Assert.Equal(A.Block, over.Action);
        Assert.Contains("2,523.37", over.Message);   // เกินส่วนที่เหลือ — บอกยอดสูงสุดที่ออกได้
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

    // ===== R-38 คืนเงินบางส่วน (รอบ 205 W6) — รอบ 208: ราคาต่อเครดิตคงเหลือ = ส่วนที่เหลือ 2,523.37 ÷ เครดิตที่ยังไม่ใช้ 6 = 420.5616… =====

    [Fact]
    public void ใบลดหนี้บางส่วน_ลดเครดิตเท่ามูลค่าที่คืน_บัตรยังใช้ต่อ()
    {
        var p = Pass(used: 4m, recognized: 1682.24m);              // เหลือ 2,523.37 · ยังไม่ใช้ 6 เครดิต
        var d = CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 841.12m, true);
        Assert.Equal(A.ReduceCredits, d.Action);
        Assert.Equal(2m, d.CreditsToRemove);                     // 2 × 420.5616… = 841.12
        var d2 = CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 2102.81m, true);
        Assert.Equal(A.ReduceCredits, d2.Action);
        Assert.Equal(5m, d2.CreditsToRemove);                    // 5 × 420.5616… = 2,102.81 · เหลือ 5 > ใช้แล้ว 4
        var plan = CreditPassDocumentRule.RefundPreview(p, 2102.81m);
        Assert.Equal(5m, plan.CreditsRemaining);
        Assert.Equal(2102.81m, plan.Net);
        Assert.False(plan.ClosesPass);
        // เศษสตางค์ ±0.01 ยังลงตัว (ยอดพิมพ์ปัดต่างกันหนึ่งสตางค์)
        Assert.Equal(A.ReduceCredits, CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 841.11m, true).Action);
    }

    [Fact]
    public void ใบลดหนี้บางส่วน_ยอดไม่ลงตัวกับราคาต่อเครดิต_บล็อก_ไม่คืนเกินมูลค่าเครดิตที่ตัด()
    {
        // รอบ 208 ฝ่ายค้าน W6 ข้อ 3: เดิม 1,000 ⇒ floor = 2 เครดิต (841.12) แต่คืนเงิน 1,000 ⇒ 21713 ของเครดิตที่ยังอยู่บนบัตรหายไป 158.88
        var p = Pass(used: 4m, recognized: 1682.24m);
        var d = CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 1000m, true);
        Assert.Equal(A.Block, d.Action);
        Assert.Contains("ไม่ลงตัว", d.Message);
        Assert.Contains("841.12", d.Message);                    // 2 เครดิต
        Assert.Contains("1,261.69", d.Message);                  // 3 เครดิต — ทางไปต่อ
        Assert.Equal(A.Block, CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 2500m, true).Action);
    }

    [Fact]
    public void ราคาต่อเครดิตคงเหลือ_คิดจากส่วนที่ยังไม่รับรู้_ไม่ใช่สัญญาหารเครดิตทั้งหมด()
    {
        // สัญญา 1,000 / 3 ครั้ง · ใช้ 1 รับรู้ 333.33 ⇒ เหลือ 666.67 ÷ 2 = 333.335 ⇒ 1 ครั้ง = 333.34 (เดิม 1,000 ÷ 3 = 333.33 ⇒ เศษ 0.01 ค้าง 21713 ตลอด)
        var p = Pass(contract: 1000m, total: 3m, used: 1m, recognized: 333.33m);
        Assert.Equal(333.34m, CreditPassDocumentRule.RefundPreview(p, 0m, credits: 1m).Net);
        Assert.Equal(333.34m, p.PricePerCredit);
        Assert.Equal(666.67m, CreditPassDocumentRule.RefundPreview(p, 0m, credits: 2m).Net);   // ทั้งหมดที่เหลือ = ส่วนที่ยังไม่รับรู้พอดี
    }

    [Fact]
    public void คืนตามจำนวนเครดิต_ส่งเครดิตตรง_ไม่ปัดหาย_บัตรชั่วโมง()
    {
        // รอบ 208 ฝ่ายค้าน W6 ข้อ 4: บัตร 3 ชั่วโมง 1,000 ⇒ 0.25 ชม. = 83.33 · เดิมแปลง 83.33 ÷ 333.33 ÷ 0.25 = 0.99996 ⇒ floor 0 ⇒ "ไม่มีเครดิตให้ลด"
        var hour = Pass(contract: 1000m, total: 3m, unit: CreditUnit.Hour);
        var plan = CreditPassDocumentRule.RefundPreview(hour, 0m, credits: 0.25m);
        Assert.Null(plan.Problem);
        Assert.Equal(0.25m, plan.CreditsToRemove);
        Assert.Equal(83.33m, plan.Net);
        // ใบร่างนั้นอนุมัติแล้ว ด่านอนุมัติ (จากยอดบาท) ได้เครดิตเท่ากัน
        var d = CreditPassDocumentRule.OnCreditNote(hour, "TIV-1", plan.Net, true);
        Assert.Equal(A.ReduceCredits, d.Action);
        Assert.Equal(0.25m, d.CreditsToRemove);
        // เครดิตที่ยังไม่ใช้ทั้งหมด ⇒ ยอดเท่าส่วนที่เหลือ ⇒ ปิดบัตร · ไม่ใช่พหุคูณของหน่วยนับ ⇒ บอกเหตุ
        var all = CreditPassDocumentRule.RefundPreview(hour, 0m, credits: 3m);
        Assert.True(all.ClosesPass);
        Assert.Equal(1000m, all.Net);
        Assert.Contains("พหุคูณ", CreditPassDocumentRule.RefundPreview(hour, 0m, credits: 0.1m).Problem);
        // หน่วยครั้ง: 2 ครั้ง ⇒ 841.12 (ตัวเดียวกับที่คิดจากยอดบาท)
        var p = Pass(used: 4m, recognized: 1682.24m);
        var byCredits = CreditPassDocumentRule.RefundPreview(p, 0m, credits: 2m);
        Assert.Equal(841.12m, byCredits.Net);
        Assert.Equal(2m, byCredits.CreditsToRemove);
    }

    [Fact]
    public void ยกเลิกใบลดหนี้คืนบางส่วน_บัตรใช้งานได้_คืนเครดิต_บัตรปิดแล้วบล็อก()
    {
        // รอบ 208 ฝ่ายค้าน W6 ข้อ 2: เดิมยกเลิกใบลดหนี้บางส่วนผ่านเงียบ — JE กลับ Cr 21713 แต่บัตรยังถูกลดเครดิต/ยอดคืนค้าง
        var after = Pass(used: 4m, recognized: 1682.24m, refunded: 841.12m, total: 8m);
        var ok = CreditPassDocumentRule.OnVoidPartialCreditNote(after, "CN-1", 2m, 841.12m);
        Assert.Equal(A.RestoreCredits, ok.Action);
        Assert.Equal(2m, ok.CreditsToRemove);
        // ใช้หมดแล้ว (เพราะเครดิตถูกลด) ⇒ คืนได้ (บัตรเปิดกลับ)
        Assert.Equal(A.RestoreCredits, CreditPassDocumentRule.OnVoidPartialCreditNote(
            Pass(used: 8m, recognized: 3364.49m, refunded: 841.12m, total: 8m, status: CreditPassStatus.Exhausted), "CN-1", 2m, 841.12m).Action);
        // บัตรปิด/หมดอายุ/ไม่พบ/ยอดคืนไม่พอ ⇒ บล็อกพร้อมทางไปต่อ
        var cancelled = CreditPassDocumentRule.OnVoidPartialCreditNote(after with { Status = CreditPassStatus.Cancelled }, "CN-1", 2m, 841.12m);
        Assert.Equal(A.Block, cancelled.Action);
        Assert.Contains("ใบขายคอร์สใหม่", cancelled.Message);
        Assert.Equal(A.Block, CreditPassDocumentRule.OnVoidPartialCreditNote(after with { Status = CreditPassStatus.Expired }, "CN-1", 2m, 841.12m).Action);
        Assert.Equal(A.Block, CreditPassDocumentRule.OnVoidPartialCreditNote(null, "CN-1", 2m, 841.12m).Action);
        Assert.Equal(A.Block, CreditPassDocumentRule.OnVoidPartialCreditNote(after with { RefundedAmount = 100m }, "CN-1", 2m, 841.12m).Action);
    }

    [Fact]
    public void ปุ่มคืนเงินออกใบลดหนี้เฉพาะผู้จดVATที่ออกใบกำกับ()
    {
        Assert.Contains("ไม่ได้จดทะเบียน", CreditPassDocumentRule.RefundDocumentProblem(false, 0m));
        Assert.Contains("ยกเว้น", CreditPassDocumentRule.RefundDocumentProblem(true, -1m));
        // ทิศตรงข้าม: ผู้จด VAT ใบกำกับ 7% / 0% ออกได้ตามเดิม
        Assert.Null(CreditPassDocumentRule.RefundDocumentProblem(true, 7m));
        Assert.Null(CreditPassDocumentRule.RefundDocumentProblem(true, 0m));
    }

    [Fact]
    public void ใบลดหนี้เต็มส่วนที่เหลือ_ยังปิดบัตรเหมือนเดิม_ไม่กลายเป็นลดเครดิต()
    {
        var p = Pass(used: 4m, recognized: 1682.24m);
        Assert.Equal(A.CancelPass, CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 2523.37m, true).Action);
        Assert.Equal(A.CancelPass, CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 2523.36m, true).Action);   // ±0.01
        var plan = CreditPassDocumentRule.RefundPreview(p, 2523.37m);
        Assert.True(plan.ClosesPass);
        Assert.Equal(6m, plan.CreditsToRemove);
        Assert.Equal(0m, plan.CreditsRemaining);
        // บัตรยังไม่เคยใช้: คืนทั้งหมด = ปิดบัตร (ทิศเดิม)
        Assert.Equal(A.CancelPass, CreditPassDocumentRule.OnCreditNote(Pass(), "TIV-1", 4205.61m, true).Action);
    }

    [Fact]
    public void ใบลดหนี้บางส่วน_เกินส่วนที่เหลือ_หรือน้อยกว่าราคา1เครดิต_บล็อกพร้อมยอดที่ออกได้()
    {
        var p = Pass(used: 4m, recognized: 1682.24m);
        var over = CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 2523.50m, true);
        Assert.Equal(A.Block, over.Action);
        Assert.Contains("เกิน", over.Message);
        var tiny = CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 100m, true);
        Assert.Equal(A.Block, tiny.Action);
        Assert.Contains("420.56", tiny.Message);   // ขั้นต่ำ = ราคา 1 เครดิต
        Assert.Equal(A.Block, CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 0m, true).Action);
    }

    [Fact]
    public void ใบลดหนี้บางส่วน_ทำให้เครดิตคงเหลือไม่เกินใช้แล้วบวกจองไว้_บล็อก_บอกยอดสูงสุด()
    {
        var p = Pass(used: 4m, reserved: 2m, recognized: 1682.24m);   // ใช้ 4 จอง 2 ⇒ ลดได้สูงสุด 10−4−2−1 = 3 เครดิต
        var d = CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 2102.81m, true);   // 5 เครดิต ⇒ เหลือ 5 ≤ 6
        Assert.Equal(A.Block, d.Action);
        Assert.Contains("จองไว้ 2", d.Message);
        Assert.Equal(3m, p.MaxRemovableCredits);
        Assert.Equal(1261.69m, CreditPassDocumentRule.MaxPartialAmount(p));   // 3 × 420.5616… = 1,261.685 ⇒ AwayFromZero
        Assert.Contains("1,261.69", d.Message);
        // ลด 3 เครดิต (1,261.69) ⇒ ผ่าน · เหลือ 7 > 6
        Assert.Equal(A.ReduceCredits, CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 1261.69m, true).Action);
        // คืนทั้งส่วนที่เหลือทั้งที่มีจองค้าง ⇒ บล็อก (ทิศเดิม) + บอกทางคืนบางส่วน
        var full = CreditPassDocumentRule.OnCreditNote(p, "TIV-1", 2523.37m, true);
        Assert.Equal(A.Block, full.Action);
        Assert.Contains("1,261.69", full.Message);
    }

    [Fact]
    public void บัตรชั่วโมง_ลดเป็นขั้น0_25ชั่วโมง_และบัตรเก่าไม่มีCreditsTotal_ไม่คืนบางส่วน()
    {
        var hour = Pass(contract: 3000m, total: 10m, unit: CreditUnit.Hour);   // 300 บาท/ชั่วโมง
        var d = CreditPassDocumentRule.OnCreditNote(hour, "TIV-1", 975m, true);
        Assert.Equal(A.ReduceCredits, d.Action);
        Assert.Equal(3.25m, d.CreditsToRemove);                       // 3.25 ชม. × 300 = 975
        Assert.Equal(A.Block, CreditPassDocumentRule.OnCreditNote(hour, "TIV-1", 1000m, true).Action);   // รอบ 208: ไม่ลงตัว (3.333 ชม.)
        var legacy = CreditPassDocumentRule.OnCreditNote(Pass(total: 0m), "TIV-1", 1000m, true);
        Assert.Equal(A.Block, legacy.Action);
        Assert.Contains("ไม่รองรับ", legacy.Message);
    }

    [Fact]
    public void คืนบางส่วนแล้ว_ราคาต่อเครดิตคิดจากสัญญาที่เหลือ_และยอดคงเหลือหักยอดคืน()
    {
        // คืน 2 เครดิต 1,000 ⇒ สัญญาที่มีผล 3,205.61 ÷ 8 = 400.70 · เหลือ 4,205.61 − 0 − 1,000 = 3,205.61
        var after = Pass(refunded: 1000m, total: 8m);
        Assert.Equal(400.70m, after.PricePerCredit);
        Assert.Equal(3205.61m, after.Deferred);
        Assert.Equal(400.70m, CourseCreditMath.PricePerCredit(CourseCreditMath.EffectiveContract(4205.61m, 1000m), 8m));
    }
}
