using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 200 ทีม R · A07 + G2-09 — กฎการอนุมัติ: ผู้อนุมัติต้องเป็นสมาชิกบริษัท · ฟอร์มที่ถูกต้องบันทึกได้</summary>
public class ApprovalRuleValidationTests
{
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Cfo = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Outsider = Guid.Parse("00000000-0000-0000-0000-0000000000f9");
    private static readonly IReadOnlySet<Guid> Members = new HashSet<Guid> { Owner, Cfo };

    [Fact]
    public void กฎที่ถูกต้อง_สองขั้นสมาชิกจริง_บันทึกได้()
        => Assert.Null(ApprovalRuleValidation.Problem("จ่ายเกิน 100,000", 100_000m, null,
            new List<(int, Guid)> { (1, Cfo), (2, Owner) }, Members));

    [Fact]
    public void ผู้อนุมัติไม่ใช่สมาชิกบริษัท_ปฏิเสธ()
        => Assert.Contains("ไม่ใช่สมาชิก", ApprovalRuleValidation.Problem("x", null, null,
            new List<(int, Guid)> { (1, Outsider) }, Members));

    [Fact]
    public void ไม่มีขั้น_หรือขั้นว่าง_ปฏิเสธ()
    {
        Assert.NotNull(ApprovalRuleValidation.Problem("x", null, null, new List<(int, Guid)>(), Members));
        Assert.NotNull(ApprovalRuleValidation.Problem("x", null, null, new List<(int, Guid)> { (1, Guid.Empty) }, Members));
    }

    [Fact]
    public void ช่วงเงินกลับด้าน_หรือชื่อว่าง_ปฏิเสธ()
    {
        Assert.NotNull(ApprovalRuleValidation.Problem("x", 500m, 100m, new List<(int, Guid)> { (1, Owner) }, Members));
        Assert.NotNull(ApprovalRuleValidation.Problem(" ", null, null, new List<(int, Guid)> { (1, Owner) }, Members));
    }

    [Fact]
    public void ลำดับขั้นซ้ำ_ปฏิเสธ()
        => Assert.NotNull(ApprovalRuleValidation.Problem("x", null, null,
            new List<(int, Guid)> { (1, Owner), (1, Cfo) }, Members));
}
