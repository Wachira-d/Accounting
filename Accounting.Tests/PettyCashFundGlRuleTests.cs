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
}
