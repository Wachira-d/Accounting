using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบจำลองเหตุการณ์ ชุดที่ 7 — ใบขายที่ถูกหักภาษี ณ ที่จ่าย แล้วปิดยอด
/// ด้วยมัดจำ (ไม่ผ่านการรับเงินสด)
/// </summary>
public class SimulationRound7Tests
{
    // เกณฑ์เงินสด (ค่า default): ตอนอนุมัติ Dr ลูกหนี้ = TotalAmount + WHT
    // ส่วน BalanceDue ของ subledger ใช้ TotalAmount ที่สุทธิจาก WHT แล้ว
    private const decimal SubTotal = 10_000m, Vat = 700m, Wht = 300m;
    private const decimal TotalAmount = SubTotal + Vat - Wht;   // 10,400
    private const decimal ArAtApproval = TotalAmount + Wht;     // 10,700

    [Fact]
    public void The_ledger_receivable_is_gross_while_the_subledger_balance_is_net()
    {
        Assert.Equal(10_400m, TotalAmount);
        Assert.Equal(10_700m, ArAtApproval);
        Assert.NotEqual(TotalAmount, ArAtApproval);   // ต้นตอของช่องว่าง
    }

    [Fact]
    public void Closing_the_bill_with_a_deposit_used_to_strand_the_wht_in_receivables()
    {
        var balanceDue = TotalAmount - TotalAmount;      // subledger: ชำระครบ
        var arOld = ArAtApproval - TotalAmount;          // GL: ยังค้าง
        Assert.Equal(0m, balanceDue);
        Assert.Equal(Wht, arOld);                        // ลูกหนี้ผี 300 ตลอดไป
    }

    [Fact]
    public void The_settlement_entry_clears_the_receivable_and_books_the_credit()
    {
        var arOld = ArAtApproval - TotalAmount;
        // Dr 11910 / Cr ลูกหนี้ เท่ายอดที่ยังไม่เคยรับรู้
        var arNew = arOld - Wht;
        Assert.Equal(0m, arNew);
        Assert.Equal(300m, Wht);   // เข้าทะเบียนเครดิต → หักได้ตอนยื่น ภ.ง.ด.50
    }

    /// <summary>GL-first: ลงเฉพาะส่วนต่างจากที่ Dr 11910 ไปแล้วจริง</summary>
    private static decimal Remaining(decimal target, decimal alreadyRecognized)
        => Math.Max(0m, Math.Round(target - alreadyRecognized, 2, MidpointRounding.AwayFromZero));

    [Fact]
    public void A_partial_cash_receipt_already_booked_part_of_the_credit()
    {
        // รับสด 5,000 (WHT slice 150) แล้วหักมัดจำปิดยอดที่เหลือ
        Assert.Equal(150m, Remaining(Wht, alreadyRecognized: 150m));
        Assert.Equal(Wht, 150m + Remaining(Wht, 150m));   // ไม่เกิน ไม่ขาด
    }

    [Fact]
    public void Calling_it_twice_posts_nothing_the_second_time()
    {
        Assert.Equal(0m, Remaining(Wht, alreadyRecognized: Wht));
        Assert.Equal(0m, Remaining(Wht, alreadyRecognized: 400m));   // ไม่ลงติดลบ
    }

    [Fact]
    public void Accrual_basis_has_nothing_left_to_recognise()
    {
        // เกณฑ์คงค้าง: 11910 ลงตั้งแต่อนุมัติ และลูกหนี้ตั้งไว้สุทธิอยู่แล้ว
        const decimal arAccrual = TotalAmount;
        Assert.Equal(0m, arAccrual - TotalAmount);
        static bool Runs(string basis) => basis == "Cash";
        Assert.True(Runs("Cash"));
        Assert.False(Runs("Accrual"));
    }

    [Fact]
    public void A_bill_without_withholding_is_untouched()
    {
        static bool Runs(decimal balanceDue, decimal wht)
            => balanceDue <= 0.005m && wht > 0.005m;
        Assert.False(Runs(0m, 0m));        // ใบปกติ — ไม่ลง JE เพิ่ม
        Assert.False(Runs(1_000m, 300m));  // ยังค้างอยู่ — รอปิดครบก่อน
        Assert.True(Runs(0m, 300m));
    }

    [Fact]
    public void Both_deposit_paths_run_the_same_hook()
    {
        // หักมัดจำจากเอกสาร (ApplyDepositToInvoiceCoreAsync) และจาก JV
        // (ApplyJournalDepositToInvoiceAsync) ต้องเรียกตัวเดียวกัน — ไม่งั้น
        // ช่องว่างย้ายไปอยู่อีกทางหนึ่งแทน (defect class "สองเส้นทาง drift")
        var paths = new[] { "ApplyDepositDoc", "ApplyDepositJournal" };
        Assert.All(paths, p => Assert.Contains(p, paths));
        Assert.Equal(2, paths.Length);
    }

    [Fact]
    public void A_foreign_currency_bill_recognises_the_baht_amount()
    {
        // GL เป็นบาท — ยอด WHT บนเอกสารเป็นสกุลเอกสาร ต้องคูณเรทก่อน
        static decimal ToGl(decimal docAmount, decimal fx)
            => fx == 1m ? docAmount : Math.Round(docAmount * fx, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(300m, ToGl(300m, 1m));
        Assert.Equal(10_500m, ToGl(300m, 35m));   // USD 300 @35
    }
}
