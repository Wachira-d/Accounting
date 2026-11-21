using System.Security.Claims;
using System.Text.Json;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Storage;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 285 บน PostgreSQL จริง — เจ้าของ (280-10): "ใครก็ตั้ง TaxId ของบริษัทเป็นเลขของคนอื่นได้" ⇒ ยืนยันด้วยหลักฐาน + ผู้ดูแลแพลตฟอร์มอนุมัติ ·
/// ยืนยันแล้วเห็นคำขอเชื่อมคู่ค้าที่ส่งก่อนสมัคร · ยังไม่ยืนยัน = เดิม (ทิศตรงข้าม) · เปลี่ยนเลข = หลุดสถานะ · ชื่อขัดทะเบียน = ยื่นไม่ได้ · audit
/// </summary>
[Trait("Category", "Db")]
[Collection("PartnerLinkRequestSweep")]
public class CompanyTaxIdVerificationRound285DbTests
{
    private readonly ITestOutputHelper _out;
    public CompanyTaxIdVerificationRound285DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม";
    private static readonly byte[] Pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n% r285 evidence\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF");
    private static readonly Random Rng = new();

    private static string RandJuristicTax()
    {
        string twelve;
        lock (Rng) twelve = "0285" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('0' + Rng.Next(10))));
        return CompanyTaxIdVerificationRound285Tests.ValidTax(twelve);
    }

    /// <summary>ที่เก็บไฟล์กลางจริง (ดิสก์ในโฟลเดอร์ชั่วคราว) + กระดิ่งจำลองที่บันทึกผู้รับ</summary>
    internal sealed class FakeFiles
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "r285-" + Guid.NewGuid().ToString("N"));
        public List<(Guid UserId, Guid? CompanyId, string Title, string Message)> Bells { get; } = new();
        public IFileStorage Storage() => new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(Root, "uploads"), Path.Combine(Root, "wwwroot"), Root, Root));
        public INotificationService Notifier() => F4Proxy<INotificationService>.Create((m, a) =>
        {
            if (m != nameof(INotificationService.SendAsync)) return null;
            lock (Bells) Bells.Add(((Guid)a[0]!, (Guid?)a[1], (string)a[3]!, (string)a[4]!));
            return Task.CompletedTask;
        });
        public string[] StoredFiles() => Directory.Exists(Root) ? Directory.GetFiles(Root, "*", SearchOption.AllDirectories) : Array.Empty<string>();
    }

    private static IDbdLookupService Registry(Func<string, string?> nameFor) => F4Proxy<IDbdLookupService>.Create((m, a) =>
        m == nameof(IDbdLookupService.GetByJuristicIdAsync)
            ? Task.FromResult(nameFor((string)a[0]!) is { } n ? new DbdCompanyResult((string)a[0]!, n, null, null, null, null, null, null, null) : null)
            : null);

    internal static CompanyTaxIdVerificationService Svc(AccountingDbContext db, FakeFiles f, IDbdLookupService? reg = null)
        => new(db, f.Storage(), reg, f.Notifier());

    internal static CompanyTaxIdVerificationService.EvidenceViewer Viewer(Guid userId) => new(userId, "127.0.0.1", "test", "เทสต์รอบ 285");

    internal static async Task<(Company Co, Guid Owner)> CompanyAsync(AccountingDbContext db, string name, string? taxId = null)
    {
        var c = new Company { Name = name + " " + Guid.NewGuid().ToString("N")[..6] + " จำกัด", TaxId = taxId ?? RandJuristicTax(), BranchCode = "00000" };
        db.Companies.Add(c);
        var u = new User { Email = $"r285-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของ 285" };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = c.Id, Role = UserRole.Owner });
        await db.SaveChangesAsync();
        return (c, u.Id);
    }

    internal static async Task<Guid> AdminAsync(AccountingDbContext db)
    {
        var u = new User { Email = $"r285-admin-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ดูแล 285", IsSystemAdmin = true };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }

    private static async Task<bool> PairAsync(Guid requester, Guid target)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await c.TradingPartnerships.AnyAsync(p => p.InvitedByCompanyId == requester && (p.CompanyAId == target || p.CompanyBId == target));
    }

    private static CrossTenantWorkflowService Xt(AccountingDbContext db) => new(db, NullLogger<CrossTenantWorkflowService>.Instance);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public async Task ตั้งเลขทีหลัง_ยังไม่ยืนยันไม่เห็นคำขอเก่า_ยืนยันเอกสารแล้วเห็น_การ์ดบอกผู้ขอยืนยันหรือยัง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await CompanyAsync(db, "บริษัท ผู้ขอ285");
        var tax = RandJuristicTax();
        await Xt(db).InvitePartnerAsync(a.Co.Id, tax, null, a.Owner, null);   // ส่งก่อนปลายทางสมัคร
        var b = await CompanyAsync(db, "บริษัท ปลายทาง285", taxId: tax);

        // ทิศเดิม (280): ยังไม่ยืนยัน ⇒ คำขอที่ส่งก่อนตั้งเลขไม่ถูกเปิด
        await Xt(db).MaterializeIncomingLinkRequestsAsync(b.Co.Id, DateTime.UtcNow);
        Assert.False(await PairAsync(a.Co.Id, b.Co.Id));

        var files = new FakeFiles();
        var reg = Registry(_ => b.Co.Name);
        var res = await Svc(db, files, reg).SubmitAsync(b.Co.Id, b.Owner, CompanyTaxIdVerification.EvidencePp20, Pdf, "pp20.pdf", DateTime.UtcNow);
        Assert.Equal(CompanyTaxIdVerification.RegistryMatch, res.RegistryResult);
        // ข้อเพิ่ม: ไฟล์อยู่ในที่เก็บไฟล์กลาง (ไม่ใช่ FileAttachments) · มีระยะเก็บ
        var stored = Assert.Single(files.StoredFiles());
        Assert.Contains(CompanyTaxIdVerification.EvidenceFolder, stored);
        Assert.False(await db.FileAttachments.AnyAsync(f => f.CompanyId == b.Co.Id));
        Assert.NotNull((await db.CompanyTaxIdVerificationRequests.AsNoTracking().SingleAsync(x => x.Id == res.RequestId)).EvidenceRetainUntil);
        // ยื่นแล้วยังไม่อนุมัติ = ยังไม่เปิด
        await Xt(db).MaterializeIncomingLinkRequestsAsync(b.Co.Id, DateTime.UtcNow);
        Assert.False(await PairAsync(a.Co.Id, b.Co.Id));
        // ยื่นซ้ำระหว่างรอ = 409
        var dup = await Assert.ThrowsAsync<CompanyTaxIdVerificationService.VerificationException>(() =>
            Svc(db, files, reg).SubmitAsync(b.Co.Id, b.Owner, CompanyTaxIdVerification.EvidencePp20, Pdf, "pp20.pdf", DateTime.UtcNow));
        Assert.Equal(409, dup.StatusCode);

        var admin = await AdminAsync(db);
        var ev = await Svc(db, files).ReadEvidenceAsync(res.RequestId, null, Viewer(admin));
        Assert.Equal(Pdf, ev!.Value.Bytes);
        // เจ้าของบริษัทเปิดของตัวเองได้ · ผู้ดูแลแพลตฟอร์มเปิดผ่านเส้นบริษัทไม่ได้ (ไม่ใช่เจ้าของ) · บริษัทอื่นไม่เห็นแถว · ทุกครั้งที่เปิดบันทึก PiiAccessLog
        Assert.Equal(Pdf, (await Svc(db, files).ReadEvidenceAsync(res.RequestId, b.Co.Id, Viewer(b.Owner)))!.Value.Bytes);
        Assert.Equal(403, (await Assert.ThrowsAsync<CompanyTaxIdVerificationService.VerificationException>(() =>
            Svc(db, files).ReadEvidenceAsync(res.RequestId, b.Co.Id, Viewer(admin)))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<CompanyTaxIdVerificationService.VerificationException>(() =>
            Svc(db, files).ReadEvidenceAsync(res.RequestId, null, Viewer(b.Owner)))).StatusCode);   // เจ้าของไม่ใช่ผู้ดูแลแพลตฟอร์ม
        Assert.Null(await Svc(db, files).ReadEvidenceAsync(res.RequestId, a.Co.Id, Viewer(a.Owner)));
        Assert.Equal(2, await db.PdpaPiiAccessLogs.CountAsync(l => l.SubjectId == res.RequestId && l.SubjectType == CompanyTaxIdVerification.PiiSubjectType));
        using (var adb = DbTestDatabase.TryCreateContext()!)
            await Svc(adb, files).ApproveAsync(res.RequestId, admin, "ตรวจ ภ.พ.20 แล้ว", DateTime.UtcNow);

        using (var u = DbTestDatabase.TryCreateContext()!)
        {
            var co = await u.Companies.AsNoTracking().SingleAsync(x => x.Id == b.Co.Id);
            Assert.True(CompanyTaxIdVerification.IsVerified(co));
            Assert.Equal(res.RequestId, co.TaxIdVerificationRequestId);
            Assert.True(await u.AuditLogs.AnyAsync(l => l.CompanyId == b.Co.Id && l.EntityType == nameof(CompanyTaxIdVerificationRequest)
                && l.NewValues!.Contains(CompanyTaxIdVerification.RuleCode) && l.NewValues.Contains(CompanyTaxIdVerification.StatusApproved)));
            await Xt(u).MaterializeIncomingLinkRequestsAsync(b.Co.Id, DateTime.UtcNow);
        }
        Assert.True(await PairAsync(a.Co.Id, b.Co.Id));   // ยืนยันแล้ว ⇒ เห็นคำขอที่ส่งก่อนสมัคร
        // ข้อเพิ่ม: บริษัทได้รับแจ้งผลอนุมัติ (ผู้ยื่น = เจ้าของ)
        Assert.Contains(files.Bells, x => x.UserId == b.Owner && x.CompanyId == b.Co.Id && x.Title.Contains("ยืนยันเลขผู้เสียภาษี"));

        // การ์ดฝั่งผู้รับ: ผู้ขอ (a) ยังไม่ยืนยัน ⇒ ป้ายบอกตามจริง
        using var check = DbTestDatabase.TryCreateContext()!;
        var row = await check.TradingPartnerships.AsNoTracking().SingleAsync(p => p.InvitedByCompanyId == a.Co.Id);
        var card = JsonSerializer.Serialize(await Xt(check).GetIncomingLinkRequestAsync(b.Co.Id, row.Id, DateTime.UtcNow), Json);
        Assert.Contains("\"taxIdVerified\":false", card);
        Assert.Contains(CompanyTaxIdVerification.PartnerTaxIdLabel(false), card);
        var status = JsonSerializer.Serialize(await Svc(check, files).GetStatusAsync(b.Co.Id), Json);
        Assert.Contains("\"verified\":true", status);
        Assert.Contains("\"canSubmit\":false", status);
    }

    [Fact]
    public async Task เปลี่ยนเลขหลังยืนยัน_หลุดสถานะ_คำขอที่รอของเลขเดิมอนุมัติไม่ได้_ถอนแล้วล้าง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var b = await CompanyAsync(db, "บริษัท เปลี่ยนเลข285");
        var files = new FakeFiles();
        var admin = await AdminAsync(db);
        var r1 = await Svc(db, files).SubmitAsync(b.Co.Id, b.Owner, CompanyTaxIdVerification.EvidenceCompanyCertificate, Pdf, "cert.pdf", DateTime.UtcNow);
        Assert.Equal(CompanyTaxIdVerification.RegistryUnknown, r1.RegistryResult);   // ไม่มีทะเบียนให้ถาม = ไม่ทราบ (ผู้ดูแลตัดสินจากเอกสาร)
        using (var adb = DbTestDatabase.TryCreateContext()!)
            await Svc(adb, files).ApproveAsync(r1.RequestId, admin, null, DateTime.UtcNow);

        // เปลี่ยนเลขผ่าน SaveChanges (ทุกทางเข้า) ⇒ หลุดสถานะ
        using (var u = DbTestDatabase.TryCreateContext()!)
        {
            var co = await u.Companies.SingleAsync(x => x.Id == b.Co.Id);
            Assert.True(CompanyTaxIdVerification.IsVerified(co));
            co.TaxId = RandJuristicTax();
            await u.SaveChangesAsync();
        }
        using (var u = DbTestDatabase.TryCreateContext()!)
        {
            var co = await u.Companies.AsNoTracking().SingleAsync(x => x.Id == b.Co.Id);
            Assert.Null(co.TaxIdVerifiedAt);
            Assert.Null(co.TaxIdVerifiedFor);
        }

        // ยื่นของเลขใหม่ แล้วเปลี่ยนเลขอีกก่อนอนุมัติ ⇒ อนุมัติไม่ได้ (409)
        // context ใหม่ต่อ "คำขอ" (เหมือน production) — context เดิมยังถือเลขเก่าไว้
        CompanyTaxIdVerificationService.SubmitResult r2;
        using (var sdb = DbTestDatabase.TryCreateContext()!)
            r2 = await Svc(sdb, files).SubmitAsync(b.Co.Id, b.Owner, CompanyTaxIdVerification.EvidencePp20, Pdf, "pp20.pdf", DateTime.UtcNow);
        using (var u = DbTestDatabase.TryCreateContext()!)
        {
            var co = await u.Companies.SingleAsync(x => x.Id == b.Co.Id);
            Assert.Equal(co.TaxId, (await u.CompanyTaxIdVerificationRequests.AsNoTracking().SingleAsync(x => x.Id == r2.RequestId)).TaxId);
            co.TaxId = RandJuristicTax();
            await u.SaveChangesAsync();
        }
        using (var adb = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<CompanyTaxIdVerificationService.VerificationException>(() =>
                Svc(adb, files).ApproveAsync(r2.RequestId, admin, null, DateTime.UtcNow));
            Assert.Equal(409, ex.StatusCode);
        }

        // ยื่นของเลขปัจจุบัน → อนุมัติ → ถอน (ต้องมีเหตุ) ⇒ ล้างสถานะ
        CompanyTaxIdVerificationService.SubmitResult r3;
        using (var sdb = DbTestDatabase.TryCreateContext()!)
            r3 = await Svc(sdb, files).SubmitAsync(b.Co.Id, b.Owner, CompanyTaxIdVerification.EvidencePp20, Pdf, "pp20.pdf", DateTime.UtcNow);
        using (var adb = DbTestDatabase.TryCreateContext()!)
            await Svc(adb, files).ApproveAsync(r3.RequestId, admin, null, DateTime.UtcNow);
        using (var adb = DbTestDatabase.TryCreateContext()!)
        {
            await Assert.ThrowsAsync<CompanyTaxIdVerificationService.VerificationException>(() => Svc(adb, files).RevokeAsync(r3.RequestId, admin, " ", DateTime.UtcNow));
            await Svc(adb, files).RevokeAsync(r3.RequestId, admin, "พบภายหลังว่าเอกสารไม่ใช่ของบริษัทนี้", DateTime.UtcNow);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(CompanyTaxIdVerification.IsVerified(await check.Companies.AsNoTracking().SingleAsync(x => x.Id == b.Co.Id)));
        Assert.Equal(CompanyTaxIdVerification.StatusRevoked, (await check.CompanyTaxIdVerificationRequests.AsNoTracking().SingleAsync(x => x.Id == r3.RequestId)).Status);
    }

    [Fact]
    public async Task ฝ่ายค้านB2_ชื่อขัดทะเบียน_ยื่นได้ติดธง_อนุมัติต้องมีเหตุ_ไฟล์ไม่ใช่PDFรูปถูกปัด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var b = await CompanyAsync(db, "บริษัท ชื่อการค้า285");
        var files = new FakeFiles();
        var admin = await AdminAsync(db);
        // ไฟล์ HTML ปลอมเป็น .pdf ⇒ ปัด ไม่มีแถว/ไฟล์ค้าง
        await Assert.ThrowsAsync<CompanyTaxIdVerificationService.VerificationException>(() =>
            Svc(db, files).SubmitAsync(b.Co.Id, b.Owner, CompanyTaxIdVerification.EvidencePp20, System.Text.Encoding.UTF8.GetBytes("<html><script>x</script></html>"), "pp20.pdf", DateTime.UtcNow));
        Assert.Empty(files.StoredFiles());
        Assert.False(await db.CompanyTaxIdVerificationRequests.AnyAsync(r => r.CompanyId == b.Co.Id));
        // ชื่อไม่ตรงทะเบียน ⇒ ยื่นได้ ติดธง
        var r = await Svc(db, files, Registry(_ => "บริษัท นิติบุคคลจริง285 จำกัด")).SubmitAsync(b.Co.Id, b.Owner, CompanyTaxIdVerification.EvidencePp20, Pdf, "pp20.pdf", DateTime.UtcNow);
        Assert.Equal(CompanyTaxIdVerification.RegistryMismatch, r.RegistryResult);
        var list = JsonSerializer.Serialize(await Svc(db, files).ListForAdminAsync("Pending"), Json);
        Assert.Contains("ชื่อไม่ตรงทะเบียน: บริษัท นิติบุคคลจริง285 จำกัด", list);
        using (var adb = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<CompanyTaxIdVerificationService.VerificationException>(() => Svc(adb, files).ApproveAsync(r.RequestId, admin, null, DateTime.UtcNow));
            Assert.Contains("ระบุเหตุผล", ex.Message);
        }
        using (var adb = DbTestDatabase.TryCreateContext()!)
            await Svc(adb, files).ApproveAsync(r.RequestId, admin, "ตรวจ ภ.พ.20 แล้ว ชื่อในระบบเป็นชื่อการค้า", DateTime.UtcNow);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.True(CompanyTaxIdVerification.IsVerified(await check.Companies.AsNoTracking().SingleAsync(x => x.Id == b.Co.Id)));
        Assert.True(await check.AuditLogs.AnyAsync(l => l.EntityId == r.RequestId.ToString() && l.NewValues!.Contains("ชื่อการค้า")));
        // บริษัทที่เลขยังเป็น "-" (สมัคร/SSO) ยื่นไม่ได้ + หน้าบอกเหตุ
        var dash = await CompanyAsync(db, "บริษัท ยังไม่ตั้งเลข285", taxId: "-");
        var st = JsonSerializer.Serialize(await Svc(db, files).GetStatusAsync(dash.Co.Id), Json);
        Assert.Contains("\"canSubmit\":false", st);
        Assert.Contains("13 หลัก", st);
    }

    [Fact]
    public async Task ฝ่ายค้านB1_เลขเดียวยืนยันได้บริษัทเดียวต่อสาขา_ต้องถอนของเดิมก่อน_สาขาอื่นได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var tax = RandJuristicTax();
        var x = await CompanyAsync(db, "บริษัท เจ้าของเดิม285", taxId: tax);
        var y = await CompanyAsync(db, "บริษัท ผู้มาใหม่285", taxId: tax);
        var files = new FakeFiles();
        var admin = await AdminAsync(db);
        var rx = await Svc(db, files).SubmitAsync(x.Co.Id, x.Owner, CompanyTaxIdVerification.EvidencePp20, Pdf, "x.pdf", DateTime.UtcNow);
        using (var adb = DbTestDatabase.TryCreateContext()!)
            await Svc(adb, files).ApproveAsync(rx.RequestId, admin, null, DateTime.UtcNow);
        var ry = await Svc(db, files).SubmitAsync(y.Co.Id, y.Owner, CompanyTaxIdVerification.EvidencePp20, Pdf, "y.pdf", DateTime.UtcNow);
        // ธงเด่นบนคิว + อนุมัติไม่ได้
        var list = JsonSerializer.Serialize(await Svc(db, files).ListForAdminAsync("Pending"), Json);
        Assert.Contains(CompanyTaxIdVerification.ConflictLabel(x.Co.Name), list);
        Assert.Contains(rx.RequestId.ToString(), list);   // ConflictRequestId ให้ถอนได้
        using (var adb = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<CompanyTaxIdVerificationService.VerificationException>(() => Svc(adb, files).ApproveAsync(ry.RequestId, admin, null, DateTime.UtcNow));
            Assert.Equal(409, ex.StatusCode);
            Assert.Contains(x.Co.Name, ex.Message);
        }
        // สาขาอื่นของเลขเดียวกัน ยืนยันได้ (คนละช่อง)
        var z = await CompanyAsync(db, "บริษัท สาขาหนึ่ง285", taxId: tax);
        using (var u = DbTestDatabase.TryCreateContext()!)
        {
            var co = await u.Companies.SingleAsync(c => c.Id == z.Co.Id);
            co.BranchCode = "00001";
            await u.SaveChangesAsync();
        }
        CompanyTaxIdVerificationService.SubmitResult rz;
        using (var sdb = DbTestDatabase.TryCreateContext()!)
            rz = await Svc(sdb, files).SubmitAsync(z.Co.Id, z.Owner, CompanyTaxIdVerification.EvidencePp20, Pdf, "z.pdf", DateTime.UtcNow);
        using (var adb = DbTestDatabase.TryCreateContext()!)
            await Svc(adb, files).ApproveAsync(rz.RequestId, admin, null, DateTime.UtcNow);
        // ถอนของเดิม (มีเหตุ · บริษัทเดิมได้รับแจ้งพร้อมเหตุ) แล้วอนุมัติผู้มาใหม่ได้
        using (var adb = DbTestDatabase.TryCreateContext()!)
            await Svc(adb, files).RevokeAsync(rx.RequestId, admin, "เอกสารของบริษัท Y ตรงทะเบียนกว่า", DateTime.UtcNow);
        Assert.Contains(files.Bells, b => b.UserId == x.Owner && b.Message.Contains("เอกสารของบริษัท Y ตรงทะเบียนกว่า"));
        using (var adb = DbTestDatabase.TryCreateContext()!)
            await Svc(adb, files).ApproveAsync(ry.RequestId, admin, null, DateTime.UtcNow);
        using var check = DbTestDatabase.TryCreateContext()!;
        var verified = await check.Companies.AsNoTracking().Where(c => c.TaxId == tax).ToListAsync();
        Assert.Equal(new[] { y.Co.Id, z.Co.Id }.OrderBy(g => g), verified.Where(CompanyTaxIdVerification.IsVerified).Select(c => c.Id).OrderBy(g => g));
    }

    [Fact]
    public async Task คอนโทรลเลอร์ผู้ดูแล_รายการบอกเหตุอนุมัติไม่ได้_ปฏิเสธต้องมีเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var b = await CompanyAsync(db, "บริษัท คิวผู้ดูแล285");
        var files = new FakeFiles();
        var admin = await AdminAsync(db);
        var r = await Svc(db, files).SubmitAsync(b.Co.Id, b.Owner, CompanyTaxIdVerification.EvidencePp20, Pdf, "pp20.pdf", DateTime.UtcNow);
        var ctl = new AdminCompanyTaxIdVerificationController(Svc(db, files))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, admin.ToString()), new Claim(ClaimTypes.Role, "SystemAdmin") }, "test")),
                },
            },
        };
        var list = JsonSerializer.Serialize(((ObjectResult)(await ctl.List("Pending", default)).Result!).Value, Json);
        Assert.Contains(r.RequestId.ToString(), list);
        await Assert.ThrowsAsync<CompanyTaxIdVerificationService.VerificationException>(() => ctl.Reject(r.RequestId, new(null), default));
        await ctl.Reject(r.RequestId, new("ไฟล์อ่านไม่ออก"), default);
        using var check = DbTestDatabase.TryCreateContext()!;
        var row = await check.CompanyTaxIdVerificationRequests.AsNoTracking().SingleAsync(x => x.Id == r.RequestId);
        Assert.Equal(CompanyTaxIdVerification.StatusRejected, row.Status);
        Assert.Equal("ไฟล์อ่านไม่ออก", row.ReviewNote);
        Assert.False(CompanyTaxIdVerification.IsVerified(await check.Companies.AsNoTracking().SingleAsync(x => x.Id == b.Co.Id)));
        // ถูกปฏิเสธแล้วยื่นใหม่ได้
        var st = JsonSerializer.Serialize(await Svc(check, files).GetStatusAsync(b.Co.Id), Json);
        Assert.Contains("\"canSubmit\":true", st);
    }
}
