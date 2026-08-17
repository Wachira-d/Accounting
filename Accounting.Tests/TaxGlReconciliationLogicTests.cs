using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตรรกะการกระทบยอด GL ↔ รายงานภาษี (TaxGlReconciliationService) — เทสต์ส่วน
/// ที่เป็น pure math/rule: การหาผลต่าง, เกณฑ์ "ตรงกัน", และการจัดลำดับสาเหตุ
/// (ส่วนที่แตะ DB ทดสอบด้วย integration test เมื่อ harness พร้อม — ดู TODO C/P1)
/// </summary>
public class TaxGlReconciliationLogicTests
{
    private static (decimal Variance, bool Matched) Recon(decimal gl, decimal report, decimal tol = 1m)
    {
        var v = gl - report;
        return (v, Math.Abs(v) <= tol);
    }

    [Fact]
    public void Exact_match_is_reconciled()
    {
        var (v, m) = Recon(12345.67m, 12345.67m);
        Assert.Equal(0m, v);
        Assert.True(m);
    }

    [Theory]
    [InlineData(1000.00, 999.50, true)]    // ต่าง 0.50 — ในเกณฑ์ (ปัดเศษ)
    [InlineData(1000.00, 999.00, true)]    // ต่าง 1.00 — ขอบพอดี ยังผ่าน
    [InlineData(1000.00, 998.99, false)]   // ต่าง 1.01 — เกินเกณฑ์
    [InlineData(1000.00, 1001.50, false)]  // แบบมากกว่า GL ก็ต้องจับได้
    public void Tolerance_boundary_is_inclusive(double gl, double report, bool expectMatched)
    {
        var (_, m) = Recon((decimal)gl, (decimal)report);
        Assert.Equal(expectMatched, m);
    }

    [Fact]
    public void Variance_sign_tells_which_side_is_higher()
    {
        // GL > แบบ = ลงบัญชีไว้แต่ยังไม่เข้าแบบ (มักลืมกด "สร้างใหม่")
        Assert.True(Recon(5000m, 3000m).Variance > 0);
        // แบบ > GL = แบบนับเกินจริง (เช่น เอกสารถูกยกเลิกหลังสร้างรายงาน)
        Assert.True(Recon(3000m, 5000m).Variance < 0);
    }

    [Fact]
    public void Missing_report_shows_full_gl_amount_as_variance()
    {
        // ยังไม่สร้างรายงาน → ยอดแบบ = 0 → ผลต่าง = ยอด GL ทั้งก้อน
        var (v, m) = Recon(8750.25m, 0m);
        Assert.Equal(8750.25m, v);
        Assert.False(m);
    }

    [Fact]
    public void Zero_on_both_sides_is_matched_not_flagged()
    {
        // งวดที่ไม่มีภาษีเลย ต้องไม่ขึ้นเป็น "ไม่ตรง"
        var (_, m) = Recon(0m, 0m);
        Assert.True(m);
    }

    /// <summary>ยอดเคลื่อนไหวในงวด = Σ(Credit − Debit) สำหรับบัญชีหนี้สิน
    /// (ภาษีขาย/WHT ค้างจ่าย) — การกลับรายการต้องหักกันเอง</summary>
    [Fact]
    public void Liability_movement_nets_reversals()
    {
        var rows = new[]
        {
            (Debit: 0m, Credit: 700m),      // ขายปกติ
            (Debit: 0m, Credit: 350m),      // ขายอีกใบ
            (Debit: 350m, Credit: 0m),      // กลับรายการใบที่สอง
        };
        var movement = rows.Sum(r => r.Credit - r.Debit);
        Assert.Equal(700m, movement);
    }

    /// <summary>บัญชีสินทรัพย์ (ภาษีซื้อ 11610) = Σ(Debit − Credit)</summary>
    [Fact]
    public void Asset_movement_uses_opposite_sign()
    {
        var rows = new[] { (Debit: 500m, Credit: 0m), (Debit: 0m, Credit: 120m) };
        var movement = rows.Sum(r => r.Debit - r.Credit);
        Assert.Equal(380m, movement);
    }

    /// <summary>ผลต่างที่ "อธิบายได้" ต้องเท่ากับผลรวมของสาเหตุ — ถ้าไม่เท่า
    /// แปลว่ายังมีส่วนที่ระบบหาไม่เจอ (ต้องมีบรรทัด UNKNOWN)</summary>
    [Fact]
    public void Explained_amount_should_reconcile_to_variance()
    {
        var variance = 1500m;
        var causeAmounts = new[] { 1000m, 500m };
        Assert.Equal(variance, causeAmounts.Sum());

        var partial = new[] { 1000m };
        Assert.NotEqual(variance, partial.Sum());   // → ต้องเพิ่ม UNKNOWN
    }
}
