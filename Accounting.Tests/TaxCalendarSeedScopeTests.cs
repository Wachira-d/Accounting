using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// กระดาน 2026-10-13 (A12): ปฏิทินภาษีสร้างแถว ภ.ง.ด.2/2ก · ภ.ง.ด.54 · ภ.พ.36 เฉพาะบริษัทที่มีกิจกรรม — เดิมสร้างให้ทุกบริษัท ⇒ เลยกำหนด/เตือนปลอม
/// · ล็อกทั้งสองทิศ: บริษัทที่มีกิจกรรมยังได้แถว · แบบอื่น (ภ.ง.ด.3/53 ฯลฯ) ไม่ถูกแตะ · แถวที่ผู้ใช้ทำเครื่องหมายไม่ถูกถอน
/// (จุดเรียกใน TaxCalendarService ล็อกด้วย tools/required_call_site_check.py)
/// </summary>
public class TaxCalendarSeedScopeTests
{
    private static readonly TaxCalendarSeedScope.SeedActivity All = new(true, true, true);
    private static readonly TaxCalendarSeedScope.SeedActivity None = TaxCalendarSeedScope.SeedActivity.None;

    [Theory]
    [InlineData("ภ.ง.ด.2")]
    [InlineData("ภ.ง.ด.2ก")]
    [InlineData("ภ.ง.ด.54")]
    [InlineData("ภ.พ.36")]
    public void GatedForms_NotSeeded_WithoutActivity(string code)
        => Assert.False(TaxCalendarSeedScope.ShouldSeed(code, None));

    [Theory]
    [InlineData("ภ.ง.ด.2")]
    [InlineData("ภ.ง.ด.2ก")]
    [InlineData("ภ.ง.ด.54")]
    [InlineData("ภ.พ.36")]
    public void GatedForms_Seeded_WithActivity(string code)   // ทิศตรงข้าม: บริษัทที่จ่ายจริงต้องยังได้เตือน
        => Assert.True(TaxCalendarSeedScope.ShouldSeed(code, All));

    [Fact]
    public void EachGate_ReadsItsOwnActivity()
    {
        var pnd2Only = new TaxCalendarSeedScope.SeedActivity(Pnd2: true, Pnd54: false, Pp36: false);
        Assert.True(TaxCalendarSeedScope.ShouldSeed("ภ.ง.ด.2", pnd2Only));
        Assert.True(TaxCalendarSeedScope.ShouldSeed("ภ.ง.ด.2ก", pnd2Only));
        Assert.False(TaxCalendarSeedScope.ShouldSeed("ภ.ง.ด.54", pnd2Only));
        Assert.False(TaxCalendarSeedScope.ShouldSeed("ภ.พ.36", pnd2Only));

        var foreignOnly = new TaxCalendarSeedScope.SeedActivity(Pnd2: false, Pnd54: true, Pp36: true);
        Assert.False(TaxCalendarSeedScope.ShouldSeed("ภ.ง.ด.2", foreignOnly));
        Assert.True(TaxCalendarSeedScope.ShouldSeed("ภ.ง.ด.54", foreignOnly));
        Assert.True(TaxCalendarSeedScope.ShouldSeed("ภ.พ.36", foreignOnly));
    }

    [Theory]
    [InlineData("ภ.พ.30")]
    [InlineData("ภ.ง.ด.1")]
    [InlineData("ภ.ง.ด.3")]
    [InlineData("ภ.ง.ด.53")]
    [InlineData("สปส.1-10")]
    [InlineData("ภ.ง.ด.50")]
    [InlineData("ภ.ง.ด.51")]
    [InlineData("ภ.ง.ด.1ก")]
    [InlineData("ภ.ง.ด.3ก")]
    [InlineData("ภ.ง.ด.53ก")]
    [InlineData("สบช.3")]
    public void OtherForms_AlwaysSeeded_EvenWithoutActivity(string code)   // ใบที่ถูกอยู่แล้วไม่ถูกแตะ (ภ.ง.ด.3/53 คงเดิม)
    {
        Assert.True(TaxCalendarSeedScope.ShouldSeed(code, None));
        Assert.True(TaxCalendarSeedScope.ShouldSeed(code, All));
    }

    [Fact]
    public void ActivityWindow_IsPreviousAndCurrentYear()
        => Assert.Equal((2025, 2026), TaxCalendarSeedScope.ActivityYears(2026));

    private static TaxCalendarEvent Row(string code, string status = "Pending") => new()
    {
        TaxFormCode = code, Year = 2026, Month = 3, Status = status, DueDate = new DateTime(2026, 4, 7),
    };

    [Theory]
    [InlineData("Pending")]
    [InlineData("Overdue")]    // GetOverdueAsync ประทับเองจาก Pending — แถวเลยกำหนดปลอมคืออาการที่ต้องซ่อม
    [InlineData("pending")]
    public void UntouchedSeedRow_WithoutActivity_IsRetracted(string status)
        => Assert.True(TaxCalendarSeedScope.ShouldRetract(Row("ภ.ง.ด.2", status), None));

    [Theory]
    [InlineData("Filed")]
    [InlineData("NotApplicable")]
    [InlineData("InProgress")]
    public void UserMarkedStatus_IsNeverRetracted(string status)
        => Assert.False(TaxCalendarSeedScope.ShouldRetract(Row("ภ.ง.ด.2", status), None));

    [Fact]
    public void RowWithUserEnteredData_IsNeverRetracted()
    {
        var filed = Row("ภ.ง.ด.2"); filed.FiledDate = new DateTime(2026, 4, 5);
        var reference = Row("ภ.ง.ด.2"); reference.FilingReference = "RD-123";
        var amount = Row("ภ.ง.ด.2"); amount.TaxAmount = 0m;          // 0 ที่ผู้ใช้กรอก = ข้อมูล ไม่ใช่ค่าว่าง
        var notes = Row("ภ.ง.ด.54"); notes.Notes = "ไม่มีจ่ายเดือนนี้";
        foreach (var r in new[] { filed, reference, amount, notes })
            Assert.False(TaxCalendarSeedScope.ShouldRetract(r, None));
    }

    [Fact]
    public void RowWithActivity_IsKept()
        => Assert.False(TaxCalendarSeedScope.ShouldRetract(Row("ภ.ง.ด.2"), All));

    [Theory]
    [InlineData("ภ.ง.ด.3")]
    [InlineData("ภ.ง.ด.53")]
    [InlineData("ภ.พ.30")]
    public void UngatedRow_IsNeverRetracted(string code)
        => Assert.False(TaxCalendarSeedScope.ShouldRetract(Row(code), None));

    [Fact]
    public void AlreadyDeletedRow_IsNotCountedAgain()
    {
        var r = Row("ภ.พ.36"); r.IsDeleted = true;
        Assert.False(TaxCalendarSeedScope.ShouldRetract(r, None));
    }

    [Fact]
    public void GatedCodes_MatchTheCalendarForms()
    {
        // ตัวตั้งรหัสเดียว — ลิสต์ในปฏิทินใช้ค่าคงที่ของ helper (เดิมพิมพ์ตรง)
        Assert.Equal("ภ.ง.ด.2", TaxCalendarSeedScope.Pnd2);
        Assert.Equal("ภ.ง.ด.2ก", TaxCalendarSeedScope.Pnd2a);
        Assert.Equal("ภ.ง.ด.54", TaxCalendarSeedScope.Pnd54);
        Assert.Equal("ภ.พ.36", TaxCalendarSeedScope.Pp36);
    }
}
