using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>รอบ 285 ข้อเพิ่ม — ยื่นหลักฐานแล้วผู้ดูแลแพลตฟอร์มได้กระดิ่ง (+ อีเมลตามค่าตั้งของผู้รับ) · ตั้งบริษัทผู้ให้บริการชั่วคราว
/// (แถว SiteSettings เดียวของระบบ ⇒ collection เดียวกับเทสต์แพลตฟอร์ม)</summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class CompanyTaxIdVerificationAdminNotifyRound285DbTests
{
    private readonly ITestOutputHelper _out;
    public CompanyTaxIdVerificationAdminNotifyRound285DbTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task ยื่นหลักฐาน_ผู้ดูแลแพลตฟอร์มได้กระดิ่งพร้อมลิงก์คิว_ผู้ใช้ทั่วไปไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var platform = await CompanyTaxIdVerificationRound285DbTests.CompanyAsync(db, "บริษัท ผู้ให้บริการ285");
        var admin = await CompanyTaxIdVerificationRound285DbTests.AdminAsync(db);
        db.CompanyUsers.Add(new CompanyUser { UserId = admin, CompanyId = platform.Co.Id, Role = UserRole.Staff });
        await db.SaveChangesAsync();
        var b = await CompanyTaxIdVerificationRound285DbTests.CompanyAsync(db, "บริษัท ผู้ยื่น285");
        var files = new CompanyTaxIdVerificationRound285DbTests.FakeFiles();
        CompanyTaxIdVerificationService.SubmitResult? r = null;
        await PlatformBillingPhase2DbTests.WithPlatformTenantAsync(db, platform.Co.Id, async () =>
        {
            using var sdb = DbTestDatabase.TryCreateContext()!;
            r = await CompanyTaxIdVerificationRound285DbTests.Svc(sdb, files).SubmitAsync(b.Co.Id, b.Owner, CompanyTaxIdVerification.EvidencePp20,
                System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%r285 notify\n%%EOF"), "pp20.pdf", DateTime.UtcNow);
        });
        Assert.NotNull(r);
        var bell = Assert.Single(files.Bells, x => x.UserId == admin);
        Assert.Contains(b.Co.Name, bell.Title);
        Assert.DoesNotContain(files.Bells, x => x.UserId == platform.Owner);   // เจ้าของบริษัทผู้ให้บริการที่ไม่ใช่ผู้ดูแลระบบ ไม่ได้
        Assert.DoesNotContain(files.Bells, x => x.UserId == b.Owner);
    }
}
