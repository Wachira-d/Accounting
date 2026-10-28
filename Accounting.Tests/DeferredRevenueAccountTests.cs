using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// R-40 (รอบ 205 W6): ผังรายได้รับล่วงหน้าของตารางรับรู้รายได้ตามสัญญา — ต้องเป็นกลุ่ม 217 เท่านั้น · ไม่มี fallback 218 "ค่าใช้จ่ายบุคลากรค้างจ่าย" ·
/// ไม่รู้ = null (ผู้เรียกล้มดัง) ไม่ใช่เลือกผังที่ความหมายผิด
/// </summary>
public class DeferredRevenueAccountTests
{
    private static DeferredRevenueAccount.Candidate L(string code) => new(Guid.NewGuid(), code, AccountType.Liability);

    [Fact]
    public void มี21713_เลือก21713เสมอ_แม้มีผัง217อื่น()
    {
        var exact = L("21713");
        var picked = DeferredRevenueAccount.Pick(new[] { L("21711"), L("21712"), exact, L("21811") });
        Assert.Equal(exact.Id, picked);
    }

    [Fact]
    public void ไม่มี21713_แต่กลุ่ม217มีผังเดียว_ใช้ผังนั้น()
    {
        var only = L("21712");
        Assert.Equal(only.Id, DeferredRevenueAccount.Pick(new[] { only, L("21811"), L("21714") }));   // 21714 ดอกเบี้ยค้างจ่ายไม่นับ
    }

    [Fact]
    public void กลุ่ม217หลายผังและไม่มี21713_หรือไม่มีเลย_คืนnull_ไม่เดา()
    {
        Assert.Null(DeferredRevenueAccount.Pick(new[] { L("21711"), L("21712") }));
        Assert.Null(DeferredRevenueAccount.Pick(new[] { L("21811"), L("21812") }));   // ทิศเดิมที่เคยผิด: StartsWith("218") ได้เงินเดือนค้างจ่าย
        Assert.Null(DeferredRevenueAccount.Pick(Array.Empty<DeferredRevenueAccount.Candidate>()));
        Assert.Equal(2, DeferredRevenueAccount.GroupCandidateCount(new[] { L("21711"), L("21712"), L("21714") }));
    }

    [Fact]
    public void ผังหัวกลุ่ม217_และผังที่ไม่ใช่หนี้สิน_ไม่ถูกเลือก()
    {
        Assert.Null(DeferredRevenueAccount.Pick(new[] { L("217") }));
        Assert.Null(DeferredRevenueAccount.Pick(new[] { new DeferredRevenueAccount.Candidate(Guid.NewGuid(), "21713", AccountType.Revenue) }));
    }

    [Fact]
    public void รายงานตรวจ_JEที่ลง218ถือว่าผิดกลุ่ม_217ไม่ใช่()
    {
        Assert.True(DeferredRevenueAccount.IsMisposted("21811"));
        Assert.False(DeferredRevenueAccount.IsMisposted("21713"));
        Assert.Contains("21713", DeferredRevenueAccount.MissingMessage(0));
        Assert.Contains("หลายรหัส", DeferredRevenueAccount.MissingMessage(2));
    }
}
