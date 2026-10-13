using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 204 (คำตัดสินเจ้าของข้อ 148 ⇒ ข้อเสนอ 142/145) — ใบลดหนี้ลดฐานหลังออก 50 ทวิ (<see cref="WhtCertReissuePlan"/>) ·
/// เปลี่ยนผู้ถูกหักหลังอนุมัติ (<see cref="WhtPayeeChangePlan"/>) · ป้าย "50 ทวิ ต้องตรวจ" (<see cref="WhtCertReviewNote"/>) — ทั้งทิศทำให้/ไม่ทำให้
/// </summary>
public class WhtCertReissuePlanTests
{
    private static readonly DateTime Sep = new(2026, 9, 15);
    private static WhtCertAmountLine A(string code, decimal rate, decimal income, decimal tax, string? cond = null)
        => new(code, rate, income, tax, "ค่าบริการ", Sep, cond);

    // ── ข้อ 142: ใบลดหนี้ ──

    [Fact]
    public void ลดบางส่วน_ยกเลิกแล้วออกใหม่ยอดที่ลด_คงวันจ่ายและเงื่อนไขเดิม()
    {
        var d = WhtCertReissuePlan.ForCreditNote(new[] { A("8", 3m, 1000m, 30m, "1") }, new[] { A("8", 3m, 400m, 12m) },
            null, false, 1, "WHT-202609-0001");
        Assert.Equal(WhtCertReissueAction.Reissue, d.Action);
        var l = Assert.Single(d.NewLines);
        Assert.Equal(600m, l.IncomeAmount);
        Assert.Equal(18m, l.TaxAmount);
        Assert.Equal(Sep, l.PaymentDate);
        Assert.Equal("1", l.Condition);
    }

    [Fact]
    public void ลดเต็มยอด_ยกเลิกอย่างเดียว()
        => Assert.Equal(WhtCertReissueAction.VoidOnly, WhtCertReissuePlan.ForCreditNote(
            new[] { A("8", 3m, 1000m, 30m) }, new[] { A("8", 3m, 1000m, 30m) }, null, false, 1, "X").Action);

    [Fact]
    public void ใบลดหนี้ไม่ลดภาษีหัก_ไม่แตะใบเดิม()
        => Assert.Equal(WhtCertReissueAction.NoChange, WhtCertReissuePlan.ForCreditNote(
            new[] { A("8", 3m, 1000m, 30m) }, Array.Empty<WhtCertAmountLine>(), null, false, 1, "X").Action);

    [Fact]
    public void เดือนยื่นแล้ว_ไม่แก้เอง_ติดธงให้นักบัญชี()
    {
        var d = WhtCertReissuePlan.ForCreditNote(new[] { A("8", 3m, 1000m, 30m) }, new[] { A("8", 3m, 400m, 12m) },
            "ภ.ง.ด.53 เดือน 09/2569 ยื่นแล้ว", false, 1, "X");
        Assert.Equal(WhtCertReissueAction.ManualReview, d.Action);
        Assert.Contains("ยื่นแล้ว", d.Reason);
        Assert.Empty(d.NewLines);
    }

    [Theory]
    [InlineData(true, 1)]    // ใบรายงวด — ลดงวดไหนต้องให้คนตัดสิน
    [InlineData(false, 2)]   // หลายใบของผู้ถูกหักเดียวกัน
    public void ใบรายงวดหรือหลายใบ_ไม่เดา_ติดธง(bool installment, int count)
        => Assert.Equal(WhtCertReissueAction.ManualReview, WhtCertReissuePlan.ForCreditNote(
            new[] { A("8", 3m, 1000m, 30m) }, new[] { A("8", 3m, 400m, 12m) }, null, installment, count, "X").Action);

    [Fact]
    public void จับคู่ประเภทหรืออัตราไม่ได้_หรือลดเกิน_ไม่แต่ง_ติดธง()
    {
        Assert.Equal(WhtCertReissueAction.ManualReview, WhtCertReissuePlan.ForCreditNote(
            new[] { A("8", 3m, 1000m, 30m) }, new[] { A("8ad", 2m, 100m, 2m) }, null, false, 1, "X").Action);
        Assert.Equal(WhtCertReissueAction.ManualReview, WhtCertReissuePlan.ForCreditNote(
            new[] { A("8", 3m, 1000m, 30m) }, new[] { A("8", 3m, 1200m, 36m) }, null, false, 1, "X").Action);
    }

    [Fact]
    public void หลายแถวบนใบ_ลดเฉพาะแถวที่ตรง_แถวอื่นคงเดิม()
    {
        var d = WhtCertReissuePlan.ForCreditNote(new[] { A("8", 3m, 1000m, 30m), A("8ad", 2m, 500m, 10m) },
            new[] { A("8ad", 2m, 500m, 10m) }, null, false, 1, "X");
        Assert.Equal(WhtCertReissueAction.Reissue, d.Action);
        var kept = Assert.Single(d.NewLines);
        Assert.Equal("8", kept.IncomeTypeCode);
        Assert.Equal(30m, kept.TaxAmount);
    }

    // ── ข้อ 145: เปลี่ยนผู้ถูกหัก ──

    [Fact]
    public void เปลี่ยนผู้ถูกหัก_ด่านเรียงตามลำดับ_ผ่านเมื่อครบ()
    {
        Assert.NotNull(WhtPayeeChangePlan.BlockReason(false, DocumentStatus.Paid, 30m, "x", true, null));
        Assert.NotNull(WhtPayeeChangePlan.BlockReason(true, DocumentStatus.Paid, 0m, "x", true, null));
        Assert.Contains("ฟอร์มแก้ไข", WhtPayeeChangePlan.BlockReason(true, DocumentStatus.Draft, 30m, "x", true, null));
        Assert.NotNull(WhtPayeeChangePlan.BlockReason(true, DocumentStatus.Voided, 30m, "x", true, null));
        Assert.Contains("เหตุผล", WhtPayeeChangePlan.BlockReason(true, DocumentStatus.Paid, 30m, "  ", true, null));
        Assert.Contains("ไม่มีอะไรเปลี่ยน", WhtPayeeChangePlan.BlockReason(true, DocumentStatus.Paid, 30m, "x", false, null));
        Assert.Equal("ยื่นแล้ว", WhtPayeeChangePlan.BlockReason(true, DocumentStatus.Paid, 30m, "x", true, "ยื่นแล้ว"));
        Assert.Null(WhtPayeeChangePlan.BlockReason(true, DocumentStatus.Paid, 30m, "ผู้มีเงินได้คือบริษัทประกัน", true, null));
        Assert.Null(WhtPayeeChangePlan.BlockReason(true, DocumentStatus.Approved, 30m, "x", true, null));
    }

    [Fact]
    public void ย้ายผังเมื่อแบบเปลี่ยน_ผลต่างรวมศูนย์_แบบเดิมไม่ขยับ()
    {
        var moved = WhtPayeeChangePlan.PayableDeltas(
            new Dictionary<string, decimal> { ["21916"] = 30m },
            new Dictionary<string, decimal> { ["21917"] = 30m });
        Assert.Equal(new[] { ("21916", -30m), ("21917", 30m) }, moved);
        Assert.Equal(0m, moved.Sum(m => m.Delta));
        // ผู้ถูกหักเปลี่ยนแต่แบบเดิม (53 → 53) ⇒ ไม่มี JE
        Assert.Empty(WhtPayeeChangePlan.PayableDeltas(
            new Dictionary<string, decimal> { ["21917"] = 30m }, new Dictionary<string, decimal> { ["21917"] = 30m }));
        // แบ่งบางส่วน: 30 ที่ 21917 → 10 ที่ 21917 + 20 ที่ 21916
        var split = WhtPayeeChangePlan.PayableDeltas(
            new Dictionary<string, decimal> { ["21917"] = 30m },
            new Dictionary<string, decimal> { ["21917"] = 10m, ["21916"] = 20m });
        Assert.Equal(new[] { ("21916", 20m), ("21917", -20m) }, split);
    }

    // ── ป้าย "50 ทวิ ต้องตรวจ" ──

    [Fact]
    public void ป้ายต้องตรวจ_สะสมหลายเรื่อง_ซ้ำไม่เพิ่ม_ล้างแล้วหมายเหตุเดิมอยู่ครบ()
    {
        var n = WhtCertReviewNote.Append("หมายเหตุเดิม", "ใบลดหนี้ CN-1 ...");
        n = WhtCertReviewNote.Append(n, "ยกเลิกการชำระ PAY-1 ...");
        n = WhtCertReviewNote.Append(n, "ใบลดหนี้ CN-1 ...");
        Assert.Equal(new[] { "ใบลดหนี้ CN-1 ...", "ยกเลิกการชำระ PAY-1 ..." }, WhtCertReviewNote.Reasons(n));
        Assert.Contains("·", WhtCertReviewNote.ReasonOf(n));
        Assert.Equal("หมายเหตุเดิม", WhtCertReviewNote.Clear(n));
        Assert.Null(WhtCertReviewNote.ReasonOf("หมายเหตุเดิม"));
        Assert.Equal("หมายเหตุเดิม", WhtCertReviewNote.Clear("หมายเหตุเดิม"));
        // ป้ายออกอัตโนมัติไม่สำเร็จเป็นคนละป้าย — ไม่ถูกล้างด้วยการรับทราบ
        var both = WhtCertReviewNote.Append(WhtCertAutoFailedNote.Build("ไม่มีเลข"), "ต้องตรวจ");
        Assert.NotNull(WhtCertAutoFailedNote.ReasonOf(WhtCertReviewNote.Clear(both)));
    }
}
