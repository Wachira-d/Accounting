using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ขอบเขตสาขาของผู้ใช้ (POS_MULTI_BRANCH_ANALYSIS.md เฟส 6)
///
/// ═══ ที่มา ═══
/// แคชเชียร์ของสาขา B เปิดกะบนเครื่องของสาขา A ได้ ⇒ ยอดขายลงผิดสาขา · ตัดสต็อก
/// ผิดคลัง · เห็นยอดขายของสาขาที่ตัวเองไม่ได้ดูแล · <c>Employee.BranchId</c> มีอยู่แล้ว
/// แต่ <c>CompanyUser</c> (ตัวที่ตัดสินสิทธิ์จริง) ไม่มี
///
/// ข้อที่สำคัญที่สุดคือ <b>ค่าว่าง = ทุกสาขา</b> — ถ้าตีความกลับกัน ทุก tenant ที่
/// อัปเกรดมาจะล็อกตัวเองออกจากระบบทันทีในวินาทีที่ deploy
/// </summary>
public class BranchScopeTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    public void ยังไม่ตั้งค่า_แปลว่าทุกสาขา_ไม่ใช่ห้ามทุกอย่าง(string? csv)
    {
        Assert.False(BranchScope.IsRestricted(csv));
        Assert.True(BranchScope.CanAccess(csv, A));
        Assert.True(BranchScope.CanAccess(csv, null));   // เครื่องยังไม่ผูกสาขาก็ยังผ่าน
        Assert.Null(BranchScope.DenyReason(csv, A, "สีลม"));
    }

    [Fact]
    public void ถูกจำกัด_เข้าได้เฉพาะสาขาที่ระบุ()
    {
        var csv = BranchScope.ToCsv(new[] { A, B });
        Assert.True(BranchScope.CanAccess(csv, A));
        Assert.True(BranchScope.CanAccess(csv, B));
        Assert.False(BranchScope.CanAccess(csv, C));
    }

    [Fact]
    public void ถูกจำกัด_แต่เครื่องยังไม่ผูกสาขา_ต้องปฏิเสธ()
    {
        // "ไม่รู้ว่าเครื่องอยู่สาขาไหน" ⇒ ตอบไม่ได้ว่ามีสิทธิ์ไหม — ห้ามปล่อยผ่าน
        var csv = BranchScope.ToCsv(new[] { A });
        Assert.False(BranchScope.CanAccess(csv, null));
        var reason = BranchScope.DenyReason(csv, null, null);
        Assert.NotNull(reason);
        Assert.Contains("ยังไม่ได้ผูกสาขา", reason);
        Assert.Contains("ตั้งค่าเครื่อง", reason);      // ต้องบอกทางไปต่อ ไม่ใช่ตันเฉย ๆ
    }

    [Fact]
    public void ข้อความปฏิเสธบอกชื่อสาขาและทางแก้()
    {
        var reason = BranchScope.DenyReason(BranchScope.ToCsv(new[] { A }), C, "ตลาดนัด");
        Assert.NotNull(reason);
        Assert.Contains("ตลาดนัด", reason);
        Assert.Contains("จัดการผู้ใช้", reason);
    }

    [Fact]
    public void ค่าเสียหายใน_CSV_ถูกข้าม_ไม่ทำให้ทั้งชุดพัง()
    {
        var csv = $"{A},ไม่ใช่-guid,{B}";
        var parsed = BranchScope.Parse(csv);
        Assert.Equal(2, parsed.Count);
        Assert.Contains(A, parsed);
        Assert.Contains(B, parsed);
    }

    [Fact]
    public void ToCsv_ของว่าง_คืน_null_ไม่ใช่สตริงว่าง()
    {
        // null ในคอลัมน์แปลว่า "ทุกสาขา" — สตริงว่างก็ต้องได้ผลเดียวกัน แต่เก็บ null
        // ให้ชัดเจนกว่าเพื่อไม่ต้องเดาความหมายตอนอ่าน
        Assert.Null(BranchScope.ToCsv(null));
        Assert.Null(BranchScope.ToCsv(Array.Empty<Guid>()));
    }

    [Fact]
    public void ค่าซ้ำถูกยุบ()
        => Assert.Equal(A.ToString(), BranchScope.ToCsv(new[] { A, A }));
}
