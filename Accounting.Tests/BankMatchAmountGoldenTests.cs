using System;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เคสกระทบยอด "ผลรวมรายการ ↔ บรรทัดธนาคาร" จาก `DECISION_AUDIT_2026-09-18.md`
/// §3 D4-5 / D4-4. ทุกหมวดมีสองครึ่ง (ใบที่ควรผ่าน + ใบที่ควรโดนปฏิเสธ)
/// </summary>
public class BankMatchAmountGoldenTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ══════════════════════════════════════════════════════════════════
    //  (ค) Makro 7,490 gross / 7,280 net WHT → ผ่าน "ทางเดียว"
    // ══════════════════════════════════════════════════════════════════
    // เดิม `ValidateMatchAmountAsync` นับขนานสองชุด (net จากบรรทัดบัญชีธนาคาร
    // + gross จาก TotalDebit) แล้ว "ผ่านถ้าชุดใดชุดหนึ่งตรง" ⇒ บรรทัดธนาคาร
    // จะเป็น 7,490 หรือ 7,280 ก็ผ่านทั้งคู่ = สองโอกาสผ่านต่อใบ

    [Fact]
    public void Makro_บรรทัดธนาคารเป็นยอดสุทธิหลังหักWht_ต้องผ่าน()
    {
        var pv = new BankMatchAmountReconciler.Item(
            A, "ใบสำคัญจ่าย PV-0001", BankFlowDirection.Outflow,
            RecordedAmount: 7490m, BankLineAmount: null, WithheldAmount: 210m);

        var r = BankMatchAmountReconciler.Reconcile(7280m, BankFlowDirection.Outflow, new[] { pv });

        Assert.True(r.Ok, r.Message);
        Assert.Equal(7280m, r.ExpectedNet);
    }

    [Fact]
    public void Makro_บรรทัดธนาคารเป็นยอดgross_ต้องถูกปฏิเสธ_ไม่ใช่สองโอกาสผ่าน()
    {
        var pv = new BankMatchAmountReconciler.Item(
            A, "ใบสำคัญจ่าย PV-0001", BankFlowDirection.Outflow,
            RecordedAmount: 7490m, BankLineAmount: null, WithheldAmount: 210m);

        var r = BankMatchAmountReconciler.Reconcile(7490m, BankFlowDirection.Outflow, new[] { pv });

        Assert.False(r.Ok);
        Assert.Equal("BANK-AMT-MISMATCH", r.RuleCode);
        Assert.Equal(210m, r.Difference);
    }

    [Fact]
    public void ใบที่ไม่มีWht_ยอดตรงเป๊ะ_ต้องผ่านเหมือนเดิม()
    {
        // ครึ่ง "ใบที่ถูกอยู่แล้วห้ามถูกแตะ"
        var pv = new BankMatchAmountReconciler.Item(
            A, "ใบสำคัญจ่าย PV-0002", BankFlowDirection.Outflow, RecordedAmount: 5000m);

        var r = BankMatchAmountReconciler.Reconcile(5000m, BankFlowDirection.Outflow, new[] { pv });

        Assert.True(r.Ok, r.Message);
    }

    [Fact]
    public void ค่าธรรมเนียมที่ถูกหักจากยอดโอน_ต้องกระทบยอดได้()
    {
        // Shopee/gateway หัก 35 บาทจากยอดโอน 2,000 → เงินเข้าจริง 1,965
        var rv = new BankMatchAmountReconciler.Item(
            A, "ใบเสร็จ RV-0100", BankFlowDirection.Inflow,
            RecordedAmount: 2000m, BankLineAmount: null, WithheldAmount: 0m, FeeAmount: 35m);

        var r = BankMatchAmountReconciler.Reconcile(1965m, BankFlowDirection.Inflow, new[] { rv });

        Assert.True(r.Ok, r.Message);
    }

    // ══════════════════════════════════════════════════════════════════
    //  (ง) Receipt 2,500 − PaymentVoucher 500 = 2,000 net
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ใบเสร็จหักใบสำคัญจ่าย_ได้ยอดสุทธิเข้าบัญชี_ต้องผ่าน()
    {
        var rv = new BankMatchAmountReconciler.Item(
            A, "ใบเสร็จ RV-0001", BankFlowDirection.Inflow, RecordedAmount: 2500m);
        var pv = new BankMatchAmountReconciler.Item(
            B, "ใบสำคัญจ่าย PV-0009", BankFlowDirection.Outflow, RecordedAmount: 500m);

        var r = BankMatchAmountReconciler.Reconcile(2000m, BankFlowDirection.Inflow, new[] { rv, pv });

        Assert.True(r.Ok, r.Message);
        Assert.Equal(2000m, r.ExpectedNet);
    }

    [Fact]
    public void ใบรับสองใบเอามาลบกันไม่ได้_ต้องถูกปฏิเสธ()
    {
        // ทิศตรงข้าม: 2,500 + 500 = 3,000 ≠ 2,000 → ปฏิเสธ
        var rv1 = new BankMatchAmountReconciler.Item(
            A, "ใบเสร็จ RV-0001", BankFlowDirection.Inflow, RecordedAmount: 2500m);
        var rv2 = new BankMatchAmountReconciler.Item(
            B, "ใบเสร็จ RV-0002", BankFlowDirection.Inflow, RecordedAmount: 500m);

        var r = BankMatchAmountReconciler.Reconcile(2000m, BankFlowDirection.Inflow, new[] { rv1, rv2 });

        Assert.False(r.Ok);
        Assert.Equal(3000m, r.ExpectedNet);
    }

    // ══════════════════════════════════════════════════════════════════
    //  D4-5(1) ชนิดที่ไม่รู้ทิศ → ปฏิเสธพร้อมบอกชนิด (เดิม "บวกเงียบ ๆ")
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ชนิดที่ไม่รู้ทิศ_ต้องปฏิเสธและบอกว่าใบไหน()
    {
        var unknown = new BankMatchAmountReconciler.Item(
            A, "ใบเสนอราคา QT-0007", BankFlowDirection.Unknown, RecordedAmount: 2000m);

        var r = BankMatchAmountReconciler.Reconcile(2000m, BankFlowDirection.Inflow, new[] { unknown });

        Assert.False(r.Ok);
        Assert.Equal("BANK-AMT-ITEM-DIR-UNKNOWN", r.RuleCode);
        Assert.Contains("QT-0007", r.Message);
    }

    [Fact]
    public void ไม่รู้ทิศของบรรทัดธนาคารเอง_ต้องปฏิเสธ()
    {
        var rv = new BankMatchAmountReconciler.Item(
            A, "ใบเสร็จ RV-0001", BankFlowDirection.Inflow, RecordedAmount: 2000m);

        var r = BankMatchAmountReconciler.Reconcile(2000m, BankFlowDirection.Unknown, new[] { rv });

        Assert.False(r.Ok);
        Assert.Equal("BANK-AMT-BANK-DIR-UNKNOWN", r.RuleCode);
    }

    [Fact]
    public void JEที่รู้ยอดบนบรรทัดบัญชีธนาคาร_ใช้ยอดนั้น_ไม่ใช่TotalDebit()
    {
        // JE รวม 7,490 (Dr เจ้าหนี้) แต่บรรทัดบัญชีธนาคารเป็น 7,280
        var je = new BankMatchAmountReconciler.Item(
            A, "JE-0031", BankFlowDirection.Outflow,
            RecordedAmount: 7490m, BankLineAmount: -7280m);

        var ok = BankMatchAmountReconciler.Reconcile(7280m, BankFlowDirection.Outflow, new[] { je });
        var bad = BankMatchAmountReconciler.Reconcile(7490m, BankFlowDirection.Outflow, new[] { je });

        Assert.True(ok.Ok, ok.Message);
        Assert.False(bad.Ok);
    }

    // ══════════════════════════════════════════════════════════════════
    //  (จ) Tolerance 100 → ปฏิเสธ  (D4-4)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Toleranceหนึ่งร้อยบาท_ต้องถูกปฏิเสธแม้มีเหตุผล()
    {
        var withoutReason = BankReconciliationTolerance.Resolve(100m, null);
        var withReason = BankReconciliationTolerance.Resolve(100m, "ค่าธรรมเนียมโอนต่างธนาคาร");

        Assert.False(withoutReason.Ok);
        Assert.False(withReason.Ok);
        Assert.Equal("BANK-TOL-OVER-CAP", withReason.RuleCode);
    }

    [Fact]
    public void Toleranceหนึ่งล้าน_ต้องถูกปฏิเสธ()
    {
        var r = BankReconciliationTolerance.Resolve(1_000_000m, "เศษปัดเศษ");
        Assert.False(r.Ok);
        Assert.Equal(BankReconciliationTolerance.Default, r.Tolerance);
    }

    [Fact]
    public void Toleranceเศษสตางค์_ยังผ่านเหมือนเดิม()
    {
        // ครึ่งตรงข้าม — เพดานต้องไม่ทำให้เคสปกติพัง
        var notSet = BankReconciliationTolerance.Resolve(0m, null);
        var oneSatang = BankReconciliationTolerance.Resolve(0.01m, null);
        var oneBaht = BankReconciliationTolerance.Resolve(1.00m, null);

        Assert.True(notSet.Ok);
        Assert.Equal(BankReconciliationTolerance.Default, notSet.Tolerance);
        Assert.True(oneSatang.Ok);
        Assert.True(oneBaht.Ok);
        Assert.Equal(1.00m, oneBaht.Tolerance);
    }

    [Fact]
    public void Toleranceเกินหนึ่งบาทแต่ไม่เกินเพดาน_ต้องมีเหตุผลกำกับ()
    {
        var noReason = BankReconciliationTolerance.Resolve(15m, "   ");
        var withReason = BankReconciliationTolerance.Resolve(15m, "ค่าธรรมเนียมโอนต่างธนาคาร 15 บาท");

        Assert.False(noReason.Ok);
        Assert.Equal("BANK-TOL-NEEDS-REASON", noReason.RuleCode);
        Assert.True(withReason.Ok);
        Assert.Equal(15m, withReason.Tolerance);
    }
}
