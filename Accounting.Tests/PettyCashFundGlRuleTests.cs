using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>กติกาผังกองทุนเงินสดย่อย (PETTY_CASH_AUDIT 2026-11-02) — pure</summary>
public class PettyCashFundGlRuleTests
{
    [Fact]
    public void ผังว่าง_ผ่าน_ผังมีกองทุนอื่น_ปฏิเสธพร้อมชื่อกองทุนและทางไปต่อ_ติ๊กยอมผังร่วม_ผ่าน()
    {
        Assert.Null(PettyCashFundGlRule.SharedAccountProblem("11112 เงินสดย่อย", Array.Empty<string>(), allowShared: false));
        var msg = PettyCashFundGlRule.SharedAccountProblem("11112 เงินสดย่อย", new[] { "สำนักงานใหญ่" }, allowShared: false);
        Assert.NotNull(msg);
        Assert.Contains("“สำนักงานใหญ่”", msg);
        Assert.Contains("11112-01", msg);                       // ทางไปต่อ: ผังย่อยแยก
        Assert.Contains("ใช้ผังร่วมกับกองทุนอื่น", msg);         // ทางไปต่อ: ชื่อช่องติ๊กบนหน้าจอ
        Assert.Null(PettyCashFundGlRule.SharedAccountProblem("11112 เงินสดย่อย", new[] { "สำนักงานใหญ่" }, allowShared: true));
    }

    [Theory]
    [InlineData(300, "Replenishment", 300)]
    [InlineData(-120.5, "Disbursement", 120.5)]
    public void ยอดสุทธิที่ลงผังกองทุน_เป็นชนิดแถวทะเบียน(decimal net, string type, decimal amount)
    {
        var mv = PettyCashFundGlRule.ExternalMovement(net);
        Assert.NotNull(mv);
        Assert.Equal(type, mv!.Value.Type);
        Assert.Equal(amount, mv.Value.Amount);
    }

    [Fact]
    public void ยอดสุทธิศูนย์_ไม่มีผลต่อกองทุน() => Assert.Null(PettyCashFundGlRule.ExternalMovement(0m));

    [Fact]
    public void สถานะ_รายการค้างชนะ_ยอดตรงคือตรงกัน_ยอดต่างไม่มีค้างคือต่าง()
    {
        Assert.Equal(PettyCashGlReconStatus.Unattributed, PettyCashFundGlRule.Classify(700m, 700m, 1));
        Assert.Equal(PettyCashGlReconStatus.Matched, PettyCashFundGlRule.Classify(700m, 700m, 0));
        Assert.Equal(PettyCashGlReconStatus.Difference, PettyCashFundGlRule.Classify(700m, 1000m, 0));
        Assert.All(Enum.GetValues<PettyCashGlReconStatus>(), s => Assert.False(string.IsNullOrWhiteSpace(PettyCashFundGlRule.StatusLabel(s))));
    }

    [Fact]
    public void หลักฐาน_รับรูปและPDFจากไบต์จริง_ปฏิเสธไฟล์ว่าง_ใหญ่เกิน_HTMLปลอมนามสกุล()
    {
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%âãÏÓ\n1 0 obj<<>>endobj\n");
        var (k, e) = PettyCashEvidence.Check(pdf, "slip.jpg");      // นามสกุลโกหก — ไบต์ตัดสิน
        Assert.Null(e);
        Assert.Equal("application/pdf", k!.Value.ContentType);
        Assert.Equal("slip.pdf", PettyCashEvidence.DisplayName("slip.jpg", k.Value.Extension));
        Assert.NotNull(PettyCashEvidence.Check(Array.Empty<byte>(), "a.png").Error);
        Assert.NotNull(PettyCashEvidence.Check(new byte[PettyCashEvidence.MaxBytes + 1], "a.png").Error);
        Assert.NotNull(PettyCashEvidence.Check(System.Text.Encoding.UTF8.GetBytes("<html><script>x</script></html>"), "slip.png").Error);
        Assert.NotNull(PettyCashEvidence.Check(System.Text.Encoding.UTF8.GetBytes("a,b,c\n1,2,3\n"), "slip.csv").Error);   // csv ผ่านที่เก็บกลาง แต่ไม่ใช่หลักฐานเงินสดย่อย
        Assert.Equal("หลักฐานเงินสดย่อย.png", PettyCashEvidence.DisplayName(null, ".png"));
    }

    // ══ ส่วน D (2026-11-02 รอบ 2): ปิดกองทุน · รายการค้างจากใบที่ถูกลบ/ยกเลิก ══

    [Theory]
    [InlineData("Replenishment", 500, 500)]
    [InlineData("Disbursement", 120, -120)]
    [InlineData("Return", 380, -380)]
    [InlineData("GlOnly", 40, 0)]
    public void ผลต่อยอดกองทุน_ตัวเดียวของระบบ(string type, decimal amount, decimal effect)
        => Assert.Equal(effect, PettyCashFundGlRule.FundEffect(type, amount));

    [Fact]
    public void ผลต่อยอดกองทุน_ชนิดไม่รู้จัก_ไม่เดา()
    {
        Assert.Null(PettyCashFundGlRule.FundEffect("Adjustment", 10m));
        Assert.Null(PettyCashFundGlRule.FundEffect(null, 10m));
    }

    [Fact]
    public void สถานะ_รายการค้างจากใบที่หายชนะทุกอย่าง_ทิศตรงข้ามไม่มีค้างยังเป็นสถานะเดิม()
    {
        Assert.Equal(PettyCashGlReconStatus.OrphanedFundEntries, PettyCashFundGlRule.Classify(100m, 300m, 2, orphanedEntries: 1));
        Assert.Equal(PettyCashGlReconStatus.Difference, PettyCashFundGlRule.Classify(100m, 300m, 0, orphanedEntries: 0));
        Assert.Equal(PettyCashGlReconStatus.Matched, PettyCashFundGlRule.Classify(300m, 300m, 0));
        Assert.Contains("ย้อนผลต่อกองทุน", PettyCashFundGlRule.StatusLabel(PettyCashGlReconStatus.OrphanedFundEntries));
    }

    [Fact]
    public void ปิดกองทุน_ไม่มีค้างปิดได้_มีค้างบอกจำนวนและทางไปต่อ_ติ๊กรับทราบปิดได้()
    {
        Assert.Null(PettyCashFundGlRule.CloseBlocker(0, 0, 0, 0m, acknowledged: false));
        var msg = PettyCashFundGlRule.CloseBlocker(2, 1, 0, 50m, acknowledged: false);
        Assert.NotNull(msg);
        Assert.Contains("2 ใบ", msg);
        Assert.Contains("1 รายการ", msg);
        Assert.Contains("50.00", msg);
        Assert.Contains("รับทราบรายการค้าง", msg);     // ชื่อช่องติ๊กบนหน้าจอ = ทางไปต่อ
        Assert.Null(PettyCashFundGlRule.CloseBlocker(2, 1, 0, 50m, acknowledged: true));   // แถวค้างของกองทุนอื่นบนผังร่วม = รับทราบได้
        Assert.NotNull(PettyCashFundGlRule.CloseBlocker(0, 0, 0, -0.01m, acknowledged: false));   // ผลต่างสตางค์เดียวก็นับ
    }

    [Fact]
    public void ปิดกองทุน_แถวค้างของกองทุนนี้เอง_รับทราบแทนไม่ได้_ต้องย้อนผลก่อน()
    {
        var msg = PettyCashFundGlRule.CloseBlocker(0, 1, 1, 200m, acknowledged: true);
        Assert.NotNull(msg);
        Assert.Contains("ย้อนผลต่อกองทุน", msg);
        Assert.Contains("รับทราบแทนไม่ได้", msg);
        Assert.NotNull(PettyCashFundGlRule.CloseBlocker(0, 1, 1, 200m, acknowledged: false));
    }

    [Fact]
    public void สาเหตุรายการค้าง_แยกลบ_ยกเลิก_ร่าง_ไม่พบ()
    {
        Assert.Contains("ถูกลบ", PettyCashFundGlRule.OrphanReason(true, true, "Posted"));
        Assert.Contains("ยกเลิก", PettyCashFundGlRule.OrphanReason(true, false, "Voided"));
        Assert.Contains("แบบร่าง", PettyCashFundGlRule.OrphanReason(true, false, "Draft"));
        Assert.Contains("ไม่พบ", PettyCashFundGlRule.OrphanReason(false, false, null));
    }

    [Fact]
    public void คืนเงินปิดกองทุน_เดบิตบัญชีรับคืน_เครดิตผังกองทุน_ไม่เลือกบัญชีหรือผังเดียวกันปฏิเสธ()
    {
        var fund = Guid.NewGuid(); var bank = Guid.NewGuid();
        var p = PettyCashJePlan.ForReturn(fund, bank, 380m);
        Assert.True(p.Ok);
        Assert.Equal(bank, p.DebitAccountId);
        Assert.Equal(fund, p.CreditAccountId);
        var none = PettyCashJePlan.ForReturn(fund, null, 380m);
        Assert.False(none.Ok);
        Assert.Contains("คืนเงินเข้าบัญชี", none.Error);
        Assert.False(PettyCashJePlan.ForReturn(fund, fund, 380m).Ok);
        Assert.False(PettyCashJePlan.ForReturn(fund, bank, 0m).Ok);
        Assert.Equal(PettyCashJePlan.RuleNoFundAccount, PettyCashJePlan.ForReturn(null, bank, 1m).RuleCode);
        // คืนเข้าผังของกองทุนเงินสดย่อยอื่น = ปฏิเสธ (กองทุนปลายทางไม่รู้ยอด) · ทิศตรงข้าม: ธนาคาร (ไม่ใช่ผังกองทุน) ผ่าน
        var toFund = PettyCashJePlan.ForReturn(fund, bank, 380m, targetIsPettyFundAccount: true);
        Assert.False(toFund.Ok);
        Assert.Contains("เติมเงิน", toFund.Error);
        Assert.True(PettyCashJePlan.ForReturn(fund, bank, 380m, targetIsPettyFundAccount: false).Ok);
    }
}
