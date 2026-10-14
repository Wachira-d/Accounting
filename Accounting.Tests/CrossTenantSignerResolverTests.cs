using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 2026-10-14 ทีม F4 (ID#10) — ผู้ลงนาม/ลายเซ็นของ workflow ข้ามบริษัท (pure) · เส้นจริงบนฐาน: Db/CrossTenantSignerDbTests</summary>
public class CrossTenantSignerResolverTests
{
    private static readonly Guid Acting = Guid.NewGuid(), Default = Guid.NewGuid(), Picked = Guid.NewGuid(), Foreign = Guid.NewGuid();

    [Fact]
    public void ระบุผู้ลงนาม_ตัวเองหรือผู้อนุมัติเริ่มต้น_ได้_สมาชิกคนอื่น_ปฏิเสธเพราะไม่ใช่การยินยอมของเจ้าของลายเซ็น()
    {
        var members = new HashSet<Guid> { Picked, Default };
        var self = CrossTenantSignerResolver.Resolve(Acting, Default, Acting, members);
        Assert.Equal(Acting, self.SignerUserId);
        Assert.Null(self.Problem);
        var def = CrossTenantSignerResolver.Resolve(Default, Default, Acting, members);
        Assert.Equal(Default, def.SignerUserId);
        Assert.Null(def.Problem);
        var colleague = CrossTenantSignerResolver.Resolve(Picked, Default, Acting, members);
        Assert.NotNull(colleague.Problem);
        Assert.Contains("ชื่อคุณเอง", colleague.Problem);
    }

    [Fact]
    public void Guidว่าง_เท่ากับไม่ระบุ_ตัวเดียวกับที่controllerใช้()
    {
        Assert.False(CrossTenantSignerResolver.IsSignerRequested(Guid.Empty));
        Assert.False(CrossTenantSignerResolver.IsSignerRequested(null));
        Assert.True(CrossTenantSignerResolver.IsSignerRequested(Picked));
        var r = CrossTenantSignerResolver.Resolve(Guid.Empty, Default, Acting, new HashSet<Guid>());
        Assert.Equal(CrossTenantSignerResolver.DepartedDefaultApproverNotice, r.Notice);
    }

    [Fact]
    public void ระบุผู้ลงนามของบริษัทอื่น_ปฏิเสธพร้อมทางไปต่อ()
    {
        var r = CrossTenantSignerResolver.Resolve(Foreign, Default, Acting, new HashSet<Guid> { Default });
        Assert.NotNull(r.Problem);
        Assert.Contains("สมาชิก", r.Problem);
        Assert.DoesNotContain(Foreign.ToString(), r.Problem);
    }

    [Fact]
    public void ไม่ระบุ_ผู้อนุมัติเริ่มต้นยังอยู่_ใช้ผู้อนุมัติเริ่มต้น_ออกแล้ว_ลงนามในชื่อผู้กดพร้อมคำเตือน()
    {
        var stay = CrossTenantSignerResolver.Resolve(null, Default, Acting, new HashSet<Guid> { Default });
        Assert.Equal(Default, stay.SignerUserId);
        Assert.Null(stay.Notice);

        var left = CrossTenantSignerResolver.Resolve(null, Default, Acting, new HashSet<Guid>());
        Assert.Equal(Acting, left.SignerUserId);                       // ไม่ลงนามในชื่อคนที่ออกไปแล้ว
        Assert.Null(left.Problem);                                     // ไม่บล็อกคนที่กดอนุมัติอยู่ตรงหน้า
        Assert.Equal(CrossTenantSignerResolver.DepartedDefaultApproverNotice, left.Notice);

        var none = CrossTenantSignerResolver.Resolve(null, null, Acting, new HashSet<Guid>());
        Assert.Equal(Acting, none.SignerUserId);
        Assert.Null(none.Notice);
    }

    [Fact]
    public void ตั้งค่า_ผู้อนุมัติต้องเป็นสมาชิก_ลายเซ็นต้องเป็นของผู้อนุมัติ()
    {
        var sig = Guid.NewGuid();
        Assert.NotNull(CrossTenantSignerResolver.ConfigProblem(Foreign, false, null, false, null, Foreign));
        Assert.Null(CrossTenantSignerResolver.ConfigProblem(Default, true, null, false, null, Default));
        Assert.NotNull(CrossTenantSignerResolver.ConfigProblem(null, true, sig, false, null, Default));        // ไม่พบลายเซ็น
        Assert.NotNull(CrossTenantSignerResolver.ConfigProblem(null, true, sig, true, Foreign, Default));      // ลายเซ็นของคนอื่น
        Assert.NotNull(CrossTenantSignerResolver.ConfigProblem(null, false, sig, true, Default, null));         // ยังไม่มีผู้อนุมัติ
        Assert.Null(CrossTenantSignerResolver.ConfigProblem(null, true, sig, true, Default, Default));
        Assert.Null(CrossTenantSignerResolver.ConfigProblem(Guid.Empty, false, Guid.Empty, false, null, null)); // ล้างค่า
    }
}
