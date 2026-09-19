using System;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ความสดของรายงาน ภ.ง.ด.50/51 — <c>Helpers/CitReportFreshness</c> (คำตัดสิน Q12)
///
/// <para><b>ครึ่งที่ 1 (ของที่พังต้องกลับมาถูก)</b> — รายงานที่สร้างไว้แล้วข้อมูล
/// ต้นทางขยับ ต้องขึ้นข้อความ "คำนวณจากข้อมูล ณ &lt;วันเวลา&gt; · มี N รายการเปลี่ยน"
/// + เปิดปุ่ม "สร้างใหม่" · เดิมหน้าจอโชว์ยอดเก่าเงียบ ๆ</para>
///
/// <para><b>ครึ่งที่ 2 (ของที่ถูกอยู่แล้วห้ามถูกแตะ)</b> — รายงานที่ยอดยังตรง
/// ต้อง<b>ไม่</b>ขึ้นคำเตือนแม้จะมีเอกสารถูกแก้ (การแก้หมายเหตุก็ดัน
/// <c>UpdatedAt</c>) · และรายงานที่ <b>ยื่นแล้ว</b> ต้อง<b>ห้าม</b>สร้างใหม่
/// — ตัวเลขที่ยื่นไปแล้วเปลี่ยนเองไม่ได้ (ราก R1)</para>
/// </summary>
public class CitReportFreshnessTests
{
    private static readonly DateTime Generated = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    private static CitReportFreshness.Verdict Eval(
        decimal storedRev, decimal freshRev,
        decimal storedExp, decimal freshExp,
        int docs = 0, int jes = 0, bool filed = false)
        => CitReportFreshness.Evaluate(
            Generated, storedRev, freshRev, storedExp, freshExp, docs, jes, filed);

    // ══════════════════════════════════════════════════════════════════
    // ครึ่งที่ 1 — ยอดขยับแล้วต้องพูด
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void RevenueMovedAfterGeneration_IsStaleAndOffersRegenerate()
    {
        var v = Eval(1_000_000m, 1_250_000m, 700_000m, 700_000m, docs: 3, jes: 1);

        Assert.Equal(CitReportFreshness.Level.Stale, v.Status);
        Assert.True(v.NeedsAttention);
        Assert.True(v.CanRegenerate);
        Assert.Equal(250_000m, v.RevenueDelta);
        Assert.Equal(0m, v.ExpenseDelta);
        Assert.Contains("คำนวณจากข้อมูล ณ", v.Message!);
        Assert.Contains("4 รายการ", v.Message!);       // 3 เอกสาร + 1 สมุดรายวัน
        Assert.Contains("สร้างใหม่", v.Message!);
    }

    [Fact]
    public void ExpenseMovedAlone_IsAlsoStale()
    {
        var v = Eval(1_000_000m, 1_000_000m, 700_000m, 680_000m, jes: 2);
        Assert.Equal(CitReportFreshness.Level.Stale, v.Status);
        Assert.Equal(-20_000m, v.ExpenseDelta);
    }

    /// <summary>วันเวลาต้องแสดงเป็นเวลาไทย (+7) และปี พ.ศ. — 01/03/2026 10:00 UTC
    /// = 01/03/2569 17:00 น.</summary>
    [Fact]
    public void StampIsThaiLocalTime()
    {
        var v = Eval(100m, 200m, 0m, 0m);
        Assert.Contains("01/03/2569 17:00 น.", v.Message!);
    }

    /// <summary>ยอดขยับ "เล็กกว่าค่าคลาดเคลื่อน" ไม่ถือว่าไม่สด (กันเศษสตางค์
    /// จากการปัดของ SQL กลายเป็นคำเตือนทุกครั้งที่เปิดรายงาน)</summary>
    [Theory]
    [InlineData(0.005)]
    [InlineData(0.01)]
    public void SubToleranceDriftIsNotStale(double drift)
    {
        var v = Eval(1_000_000m, 1_000_000m + (decimal)drift, 0m, 0m);
        Assert.Equal(CitReportFreshness.Level.UpToDate, v.Status);
    }

    // ══════════════════════════════════════════════════════════════════
    // ครึ่งที่ 2 — ของที่ถูกอยู่แล้ว ห้ามถูกฟ้อง
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void NothingChanged_IsSilent()
    {
        var v = Eval(1_000_000m, 1_000_000m, 700_000m, 700_000m);
        Assert.Equal(CitReportFreshness.Level.UpToDate, v.Status);
        Assert.False(v.NeedsAttention);
        Assert.Null(v.Message);
    }

    /// <summary><b>หัวใจของเกณฑ์ Q12</b> — มีเอกสารถูกแก้ 12 ใบ แต่ยอดรวมยังเท่าเดิม
    /// ⇒ **ไม่ใช่คำเตือน** แค่บอกให้รู้ · ถ้าใช้ "วันที่" เป็นเกณฑ์ตามที่เผลอคิดกัน
    /// รายงานที่ถูกอยู่แล้วจะขึ้นแถบเตือนทุกเดือน = ปิดด่านโดยไม่ตั้งใจ (F2 ข้อ 8)</summary>
    [Fact]
    public void SourceTouchedButTotalsMatch_IsNotAWarning()
    {
        var v = Eval(1_000_000m, 1_000_000m, 700_000m, 700_000m, docs: 12);

        Assert.Equal(CitReportFreshness.Level.SourceTouched, v.Status);
        Assert.False(v.NeedsAttention);
        Assert.NotNull(v.Message);
        Assert.Contains("ยังตรงกับที่คำนวณไว้", v.Message!);
        Assert.DoesNotContain("สร้างใหม่", v.Message!);
    }

    /// <summary>รายงานที่ยื่น/บันทึกว่ายื่นแล้ว: ห้ามเสนอ "สร้างใหม่" —
    /// ต้องชี้ไปที่การยื่นเพิ่มเติม และบอกทางไปต่อ (ปลดล็อกก่อน)</summary>
    [Fact]
    public void FiledReport_NeverOffersRegenerate()
    {
        var v = Eval(1_000_000m, 1_400_000m, 0m, 0m, docs: 5, filed: true);

        Assert.Equal(CitReportFreshness.Level.StaleAfterFiling, v.Status);
        Assert.True(v.NeedsAttention);
        Assert.False(v.CanRegenerate);
        Assert.Contains("ยื่นเพิ่มเติม", v.Message!);
        Assert.Contains("ปลดล็อก", v.Message!);
    }

    /// <summary>รายงานที่ยื่นแล้วและยอดยังตรง ⇒ เงียบสนิท และ
    /// <c>CanRegenerate = false</c> (ปุ่มต้องไม่โผล่มาให้กดผิด)</summary>
    [Fact]
    public void FiledAndUpToDate_IsSilentAndLocked()
    {
        var v = Eval(1_000_000m, 1_000_000m, 0m, 0m, filed: true);
        Assert.Equal(CitReportFreshness.Level.UpToDate, v.Status);
        Assert.Null(v.Message);
        Assert.False(v.CanRegenerate);
    }

    /// <summary>ตัวตัดสินนี้ต้องไม่ throw ไม่ว่ารับค่าอะไร — รายงานต้องเปิดดูได้เสมอ</summary>
    [Fact]
    public void NeverThrows()
    {
        _ = CitReportFreshness.Evaluate(DateTime.MinValue, 0m, 0m, 0m, 0m, 0, 0, false);
        _ = CitReportFreshness.Evaluate(DateTime.MaxValue.AddHours(-24),
            decimal.MinValue / 4, decimal.MaxValue / 4, 0m, 0m, int.MaxValue, 0, true);
    }
}
