using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Pdpa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 214 (คำตัดสินข้อ 173) — เอกสารทางกฎหมายของแพลตฟอร์มบน PostgreSQL จริง ผ่าน <see cref="LegalDocumentService"/>:
/// ร่าง → เผยแพร่ = ฉบับปัจจุบัน · ฉบับเผยแพร่แล้วแก้ไม่ได้ (ร่างเลขเดิมถูกปฏิเสธ · แถวไม่เปลี่ยน) · วันมีผลย้อนหลังเผยแพร่ไม่ได้ (ยังเป็นร่าง) ·
/// ผู้ใช้ที่ยังไม่ยอมรับฉบับใหม่เห็นแถบ → กดรับทราบ = แถวยินยอมที่ hash ตรวจกลับได้ · หน้าค้างฉบับเก่ากดรับทราบไม่ได้
/// <para>ตารางเป็นระดับแพลตฟอร์ม (ใช้ร่วมทั้งฐานเทสต์) ⇒ เลขฉบับคำนวณจากฉบับล่าสุดที่มีอยู่ ไม่ฝังเลข</para>
/// </summary>
[Trait("Category", "Db")]
public class LegalDocumentDbTests
{
    private readonly ITestOutputHelper _out;
    public LegalDocumentDbTests(ITestOutputHelper output) => _out = output;

    private static LegalDocumentService Svc(Accounting.Data.AccountingDbContext db) => new(db, NullLogger<LegalDocumentService>.Instance);

    [Fact]
    public async Task Publish_is_append_only_and_drives_the_acceptance_banner()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var svc = Svc(db);
        const string kind = LegalDocumentDefaults.KindTerms;
        var today = LegalDocumentPolicy.TodayBangkok(DateTime.UtcNow);

        var newest = (await svc.GetHistoryAsync(kind)).Select(h => h.Version)
            .Aggregate((a, b) => LegalDocumentPolicy.CompareVersions(a, b) >= 0 ? a : b);
        var major = int.Parse(newest.Split('.')[0]) + 1;
        var v1 = $"{major}.0";
        var v2 = $"{major}.1";

        // ผู้ใช้ที่ยอมรับฉบับปัจจุบันแล้ว — ทั้งสองแบบ (CI รอบ 214: เดิมเขียน PdpaConsentRecords ด้วย CompanyId ศูนย์ ⇒ FK ล้ม):
        // (ก) ผู้ใช้ที่ยังไม่มีบริษัท (เพิ่งสมัคร/รับคำเชิญ/SystemAdmin) (ข) ผู้ใช้ที่เป็นสมาชิกบริษัท — การยอมรับเป็นระดับผู้ใช้ ไม่ผูกบริษัท
        var user = new User { Email = $"legal-{Guid.NewGuid():N}@example.test", PasswordHash = "x", FullName = "ทดสอบนโยบาย (ไม่มีบริษัท)" };
        var member = new User { Email = $"legal-m-{Guid.NewGuid():N}@example.test", PasswordHash = "x", FullName = "ทดสอบนโยบาย (มีบริษัท)" };
        db.Users.AddRange(user, member);
        var company = new Company { Name = "บริษัททดสอบนโยบาย", TaxId = "0105556000003" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = member.Id, CompanyId = company.Id, Role = Accounting.Models.Enums.UserRole.Owner, IsDefault = true });
        await db.SaveChangesAsync();
        var before = await svc.GetAcceptanceStatusAsync(user.Id);
        Assert.True(before.NeedsAcknowledge);   // ยังไม่มีหลักฐานเลย
        var accepted = await svc.RecordAcceptanceAsync(user.Id, before.CurrentConsentVersion, "203.0.113.9", "xunit");
        Assert.False(accepted.NeedsAcknowledge);
        Assert.True((await svc.GetAcceptanceStatusAsync(member.Id)).NeedsAcknowledge);
        Assert.False((await svc.RecordAcceptanceAsync(member.Id, before.CurrentConsentVersion, "203.0.113.10", "xunit")).NeedsAcknowledge);
        // ไม่มีแถวยินยอมระดับแพลตฟอร์มที่ CompanyId ศูนย์ถูกสร้างใหม่ (ห้ามกลับไปทางเดิม)
        Assert.False(await db.PdpaConsentRecords.AsNoTracking().AnyAsync(c => c.SubjectUserId == user.Id || c.SubjectUserId == member.Id));

        // ร่าง → เผยแพร่ (มีผลวันนี้)
        await svc.SaveDraftAsync(kind, new Accounting.Services.Interfaces.LegalDraftInput(v1, today, "## 1. ทดสอบ\nเนื้อความ {{controller.name}}", "ทดสอบรอบ 214"), "tester");
        var pub = await svc.PublishDraftAsync(kind, "tester");
        Assert.True(pub.IsCurrent);
        var current = await svc.GetCurrentAsync(kind);
        Assert.Equal(v1, current.Version);
        Assert.Equal("published", current.Source);
        Assert.Null(await svc.GetDraftAsync(kind));

        // ทิศตรงข้าม: ฉบับที่เผยแพร่แล้วแก้ไม่ได้ — ร่างเลขเดิมถูกปฏิเสธ · แถวเผยแพร่ไม่เปลี่ยน
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.SaveDraftAsync(kind, new Accounting.Services.Interfaces.LegalDraftInput(v1, today, "## แก้ทับ", null), "tester"));
        db.ChangeTracker.Clear();
        var row = await db.LegalDocumentVersions.AsNoTracking().SingleAsync(x => x.Kind == kind && x.Version == v1);
        Assert.Equal(LegalDocumentPolicy.StatusPublished, row.Status);
        Assert.Contains("เนื้อความ", row.BodyMarkdown);

        // วันมีผลย้อนหลัง ⇒ เผยแพร่ไม่ได้ ยังเป็นร่าง
        await svc.SaveDraftAsync(kind, new Accounting.Services.Interfaces.LegalDraftInput(v2, today.AddDays(-1), "## ย้อนหลัง", null), "tester");
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.PublishDraftAsync(kind, "tester"));
        Assert.NotNull(await svc.GetDraftAsync(kind));
        Assert.True(await svc.DiscardDraftAsync(kind, "tester"));
        Assert.Null(await svc.GetDraftAsync(kind));

        // ฉบับใหม่ ⇒ ผู้ใช้ที่ยอมรับฉบับเดิมเห็นแถบ · หน้าค้างฉบับเก่ากดรับทราบไม่ได้ · กดด้วยฉบับปัจจุบัน = หลักฐานที่ตรวจกลับได้
        var after = await svc.GetAcceptanceStatusAsync(user.Id);
        Assert.True(after.NeedsAcknowledge);
        Assert.Equal(v1, after.TermsVersion);
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.RecordAcceptanceAsync(user.Id, before.CurrentConsentVersion, "203.0.113.9", "xunit"));
        var done = await svc.RecordAcceptanceAsync(user.Id, after.CurrentConsentVersion, "203.0.113.9", "xunit");
        Assert.False(done.NeedsAcknowledge);
        db.ChangeTracker.Clear();
        var rec = await db.PlatformPolicyAcceptances.AsNoTracking()
            .Where(c => c.UserId == user.Id && c.Purpose == PdpaPolicy.PolicyUpdatePurpose)
            .OrderByDescending(c => c.AcceptedAt).FirstAsync();
        Assert.Equal(after.CurrentConsentVersion, rec.PolicyVersion);
        Assert.Equal("policy-update-banner", rec.Channel);
        // รอบ 214 ต่อ (ฎ): เขียน → อ่านกลับจาก PostgreSQL → ตรวจ hash ต้องผ่าน (v2 · เวลา normalize) — control ที่ไม่มี round-trip test = ไม่มี control
        Assert.StartsWith(PdpaConsentEvidence.V2Prefix, rec.EvidenceHash);
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Valid, PlatformPolicyAcceptanceRules.Check(rec));
        // ทิศตรงข้าม: แก้ช่องในฐานหลังบันทึก ⇒ ตรวจไม่ผ่าน
        await db.PlatformPolicyAcceptances.Where(x => x.Id == rec.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.IpAddress, "198.51.100.66"));
        db.ChangeTracker.Clear();
        var tampered = await db.PlatformPolicyAcceptances.AsNoTracking().SingleAsync(x => x.Id == rec.Id);
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Invalid, PlatformPolicyAcceptanceRules.Check(tampered));
    }

    /// <summary>รอบ 214 ต่อ (ฎ): ความยินยอมบนฟอร์มเว็บร้าน (PdpaConsentRecord · CmsDataConsent) — เขียน → อ่านกลับ → ตรวจผ่าน</summary>
    [Fact]
    public async Task Shop_form_consent_evidence_round_trips_through_postgres()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var company = new Company { Name = "ร้านทดสอบหลักฐานยินยอม", TaxId = "0105556000004" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var row = CmsDataConsent.StageForSubject(db, company.Id, CmsDataConsent.OrderPurpose, "ยินยอมให้ร้านใช้ข้อมูล",
            "guest@example.test", "WEB-214", DateTime.UtcNow.AddTicks(7));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var back = await db.PdpaConsentRecords.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.True(PdpaConsentEvidence.Verify(back.EvidenceHash, back.SubjectContact, back.Purpose, back.PolicyVersion,
            back.GrantedAt, back.Channel, null, null));
        Assert.False(PdpaConsentEvidence.Verify(back.EvidenceHash, "other@example.test", back.Purpose, back.PolicyVersion,
            back.GrantedAt, back.Channel, null, null));
    }

    /// <summary>ฝ่ายค้านรอบ 214 S2/S3: DSR ของบริษัทไม่ส่ง IP/UA (ประวัติข้ามบริษัท) แต่เจ้าของข้อมูลดูเองได้ครบ ·
    /// ลบตามคำขอทำไม่ให้ระบุตัว**รวมแถวที่ soft-delete** · แถวคงอยู่ (legal hold)</summary>
    [Fact]
    public async Task Dsr_hides_platform_ip_from_company_and_erasure_reaches_soft_deleted_rows()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var user = new User { Email = $"dsr-{Guid.NewGuid():N}@example.test", PasswordHash = "x", FullName = "ทดสอบ DSR นโยบาย" };
        var company = new Company { Name = "บริษัททดสอบ DSR นโยบาย", TaxId = "0105556000005" };
        db.Users.Add(user);
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = user.Id, CompanyId = company.Id, Role = Accounting.Models.Enums.UserRole.Owner, IsDefault = true });
        // ฝ่ายค้านรอบ 214 B2 (CI): ผู้ถูกลบต้องไม่ใช่เจ้าของคนสุดท้าย — เทสต์นี้ตรวจ DSR/legal hold ไม่ใช่ด่านเจ้าของ (ด่านอยู่ที่ Erasure_refuses_last_owner_…)
        var coOwner = new User { Email = $"dsr-co-{Guid.NewGuid():N}@example.test", PasswordHash = "x", FullName = "เจ้าของร่วม" };
        db.Users.Add(coOwner);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = coOwner.Id, CompanyId = company.Id, Role = Accounting.Models.Enums.UserRole.Owner });
        var live = PlatformPolicyAcceptanceRules.Create(user.Id, user.Email, PdpaPolicy.SignupPurpose, "1.0", "1.0", DateTime.UtcNow, "web-form", "203.0.113.21", "ua-live");
        var softDeleted = PlatformPolicyAcceptanceRules.Create(user.Id, user.Email, PdpaPolicy.PolicyUpdatePurpose, "1.0", "1.0", DateTime.UtcNow, "policy-update-banner", "203.0.113.22", "ua-old");
        softDeleted.IsDeleted = true;
        db.PlatformPolicyAcceptances.AddRange(live, softDeleted);
        await db.SaveChangesAsync();

        var pdpa = new PdpaService(db);
        var report = await pdpa.GenerateAccessReportAsync(company.Id, user.Id, null);
        var row = Assert.Single(report.PolicyAcceptances!);
        Assert.False(row.ContainsKey("ipAddress"));                     // S3: DPO ของบริษัทไม่เห็นประวัติ IP/UA ระดับแพลตฟอร์ม
        Assert.False(row.ContainsKey("userAgent"));
        Assert.Equal(PlatformPolicyAcceptanceRules.CheckLabel(PdpaConsentEvidence.EvidenceCheck.Valid), row["evidenceCheck"]);
        var mine = await Svc(db).GetMyAcceptancesAsync(user.Id);         // เจ้าของข้อมูลเองเห็นครบ
        Assert.Equal("203.0.113.21", Assert.Single(mine)["ipAddress"]);

        await pdpa.ApplyErasureAsync(company.Id, user.Id, null, "dpo-test");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var after = await db.PlatformPolicyAcceptances.IgnoreQueryFilters().AsNoTracking().Where(x => x.UserId == user.Id).ToListAsync();
        Assert.Equal(2, after.Count);                                     // legal hold: แถวคงอยู่
        Assert.All(after, x =>
        {
            Assert.NotNull(x.PseudonymisedAt);
            Assert.Null(x.IpAddress);
            Assert.Null(x.UserAgent);
            Assert.DoesNotContain("dsr-", x.Email);                       // S2: แถว soft-delete ก็ถูกทำไม่ให้ระบุตัว
            Assert.False(string.IsNullOrEmpty(x.EvidenceHash));
        });
    }

    /// <summary>รอบ 214 ต่อ (ฐ): ผู้ใช้ที่ไม่มีบริษัทยื่นคำขอลบเอง — กดซ้ำได้ฉบับเดิม · ผู้ใช้อื่นไม่เห็น/ไม่แตะคำขอนี้ · ดำเนินการลบ = แกนลบตัวเดียว
    /// (บัญชีไม่ระบุตัว · หลักฐานยอมรับนโยบาย legal hold) · ดำเนินการซ้ำ = 409 · ยื่นใหม่ภายใน 24 ชม. = 429 · ปฏิเสธต้องมีเหตุผล</summary>
    [Fact]
    public async Task Platform_erasure_request_is_idempotent_scoped_to_user_and_uses_the_single_erasure_core()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var testStart = DateTime.UtcNow.AddSeconds(-5);
        var a = new User { Email = $"erase-a-{Guid.NewGuid():N}@example.test", PasswordHash = "x", FullName = "ผู้ขอลบ (ไม่มีบริษัท)" };
        var b = new User { Email = $"erase-b-{Guid.NewGuid():N}@example.test", PasswordHash = "x", FullName = "ผู้ใช้อื่น" };
        db.Users.AddRange(a, b);
        db.PlatformPolicyAcceptances.Add(PlatformPolicyAcceptanceRules.Create(a.Id, a.Email, PdpaPolicy.SignupPurpose, "1.0", "1.0", DateTime.UtcNow, "web-form", "203.0.113.31", "ua"));
        await db.SaveChangesAsync();
        var originalEmail = a.Email;
        var pdpa = new PdpaService(db);

        var (r1, created1) = await pdpa.RequestPlatformErasureAsync(a.Id, "เลิกใช้บริการ", "203.0.113.31");
        var (r2, created2) = await pdpa.RequestPlatformErasureAsync(a.Id, "กดซ้ำ", "203.0.113.31");
        Assert.True(created1);
        Assert.False(created2);                                            // ค้างอยู่ ⇒ ฉบับเดิม
        Assert.Equal(r1.Id, r2.Id);
        Assert.Equal(r1.RequestedAt.AddDays(30), r1.DueAt);
        Assert.Null(await pdpa.GetMyPlatformErasureRequestAsync(b.Id));    // ผู้ใช้อื่นไม่เห็นคำขอของ A
        var (rb, _) = await pdpa.RequestPlatformErasureAsync(b.Id, null, null);
        Assert.NotEqual(r1.Id, rb.Id);
        await Assert.ThrowsAsync<BusinessRuleException>(() => pdpa.RejectPlatformErasureAsync(rb.Id, Guid.NewGuid(), " "));   // ปฏิเสธต้องมีเหตุผล

        var admin = Guid.NewGuid();
        var (done, notify) = await pdpa.CompletePlatformErasureAsync(r1.Id, admin, "ทดสอบ", "198.51.100.5");
        Assert.Equal(originalEmail, notify);                               // แจ้งผลไปอีเมลเดิม (เก็บก่อนลบ)
        Assert.Equal(PlatformErasureRequestPolicy.StatusCompleted, done.Status);
        db.ChangeTracker.Clear();
        var userAfter = await db.Users.AsNoTracking().SingleAsync(u => u.Id == a.Id);
        Assert.Equal("[ANONYMIZED]", userAfter.FullName);
        Assert.EndsWith("@deleted.local", userAfter.Email);
        var acc = await db.PlatformPolicyAcceptances.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.UserId == a.Id);
        Assert.NotNull(acc.PseudonymisedAt);                               // legal hold — แถวคงอยู่
        Assert.Null(acc.IpAddress);
        var reqAfter = await db.PlatformErasureRequests.AsNoTracking().SingleAsync(x => x.Id == r1.Id);
        Assert.EndsWith("@deleted.local", reqAfter.RequesterEmail);       // คำขอเองไม่เก็บอีเมลจริงหลังลบ

        // ฝ่ายค้านรอบ 214 B1: audit chain (append-only) ไม่มีค่า PII เดิมของผู้ใช้จากขั้นยื่น/ลบ — มีแค่ "ช่องไหนเปลี่ยน"
        // CI: ค่าใน audit เป็น JSON ที่ escape อักษรไทยเป็น \uXXXX ⇒ เทียบทั้งรูปดิบและรูปที่ escape แล้ว (เดิมเทียบรูปดิบ ⇒ PiiMask ไม่เจอ · ชื่อไทย "ไม่รั่ว" เพราะเทียบผิดรูป)
        static string J(string v) => System.Text.Json.JsonSerializer.Serialize(v).Trim('"');
        var recent = await db.AuditLogs.AsNoTracking().Where(x => x.Timestamp >= testStart).ToListAsync();
        var needles = new[] { originalEmail, "203.0.113.31", "ผู้ขอลบ (ไม่มีบริษัท)", J("ผู้ขอลบ (ไม่มีบริษัท)") };
        Assert.DoesNotContain(recent, x => needles.Any(n => (x.OldValues ?? "").Contains(n) || (x.NewValues ?? "").Contains(n)));
        Assert.Contains(recent, x => x.EntityType == "User" && x.EntityId == a.Id.ToString()
            && ((x.NewValues ?? "").Contains(AuditRedaction.PiiMask) || (x.NewValues ?? "").Contains(J(AuditRedaction.PiiMask))));   // ทิศตรงข้าม: ยังเห็นว่ามีการเปลี่ยน
        var again = await Assert.ThrowsAsync<BusinessRuleException>(() => pdpa.CompletePlatformErasureAsync(r1.Id, admin, null, null));
        Assert.Equal(409, again.StatusCode);
        var tooSoon = await Assert.ThrowsAsync<BusinessRuleException>(() => pdpa.RequestPlatformErasureAsync(a.Id, null, null));
        Assert.Equal(429, tooSoon.StatusCode);
        // ทิศตรงข้าม: ผู้ใช้อื่นไม่ถูกแตะ
        Assert.StartsWith("erase-b-", (await db.Users.AsNoTracking().SingleAsync(u => u.Id == b.Id)).Email);
    }

    /// <summary>ฝ่ายค้านรอบ 214 B2/S1/S2: ลบเจ้าของคนสุดท้าย = 409 พร้อมรายชื่อบริษัท (ทิศตรงข้าม: มีเจ้าของคนอื่นที่ยังทำงานได้ ⇒ ลบได้) ·
    /// แอดมินดำเนินการคำขอตัวเอง/ลบแอดมินแพลตฟอร์ม = 409 · บัญชี soft-delete ยังลบได้ (ไม่ปิดคำขอว่าเสร็จทั้งที่ไม่ได้ลบ)</summary>
    [Fact]
    public async Task Erasure_refuses_last_owner_self_and_platform_admin_and_handles_soft_deleted_users()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        User U(string tag) => new() { Email = $"{tag}-{Guid.NewGuid():N}@example.test", PasswordHash = "x", FullName = tag };
        var owner = U("owner"); var other = U("other"); var sysAdmin = U("sysadmin"); var ghost = U("ghost");
        sysAdmin.IsSystemAdmin = true;
        ghost.IsDeleted = true;
        db.Users.AddRange(owner, other, sysAdmin, ghost);
        var company = new Company { Name = "บริษัทเจ้าของคนเดียว", TaxId = "0105556000006" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = owner.Id, CompanyId = company.Id, Role = Accounting.Models.Enums.UserRole.Owner, IsDefault = true });
        await db.SaveChangesAsync();
        var pdpa = new PdpaService(db);
        var admin = Guid.NewGuid();

        var (rOwner, _) = await pdpa.RequestPlatformErasureAsync(owner.Id, null, null);
        var blocked = await Assert.ThrowsAsync<BusinessRuleException>(() => pdpa.CompletePlatformErasureAsync(rOwner.Id, admin, null, null));
        Assert.Equal(409, blocked.StatusCode);
        Assert.Contains("บริษัทเจ้าของคนเดียว", blocked.Message);
        Assert.Contains("บริษัทเจ้าของคนเดียว", string.Join("|", await pdpa.ErasureOwnershipBlockersAsync(owner.Id)));
        // ทิศตรงข้าม: เพิ่มเจ้าของอีกคนที่ยังทำงานได้ ⇒ ลบได้
        db.CompanyUsers.Add(new CompanyUser { UserId = other.Id, CompanyId = company.Id, Role = Accounting.Models.Enums.UserRole.Owner });
        await db.SaveChangesAsync();
        Assert.Empty(await pdpa.ErasureOwnershipBlockersAsync(owner.Id));
        var (done, notify) = await pdpa.CompletePlatformErasureAsync(rOwner.Id, admin, null, null);
        Assert.Equal(PlatformErasureRequestPolicy.StatusCompleted, done.Status);
        Assert.StartsWith("owner-", notify);

        // S2: ดำเนินการคำขอของตัวเอง · ลบแอดมินแพลตฟอร์ม
        var (rOther, _) = await pdpa.RequestPlatformErasureAsync(other.Id, null, null);
        Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() => pdpa.CompletePlatformErasureAsync(rOther.Id, other.Id, null, null))).StatusCode);
        var (rAdmin, _) = await pdpa.RequestPlatformErasureAsync(sysAdmin.Id, null, null);
        Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() => pdpa.CompletePlatformErasureAsync(rAdmin.Id, admin, null, null))).StatusCode);

        // S1: บัญชี soft-delete ยังถูกลบจริง (ไม่ใช่ปิดคำขอว่าเสร็จเงียบ ๆ)
        db.PlatformErasureRequests.Add(new PlatformErasureRequest { UserId = ghost.Id, RequesterEmail = ghost.Email, RequestedAt = DateTime.UtcNow,
            DueAt = DateTime.UtcNow.AddDays(30), Status = PlatformErasureRequestPolicy.StatusPending });
        await db.SaveChangesAsync();
        var rGhost = await db.PlatformErasureRequests.AsNoTracking().SingleAsync(x => x.UserId == ghost.Id);
        await pdpa.CompletePlatformErasureAsync(rGhost.Id, admin, null, null);
        db.ChangeTracker.Clear();
        Assert.Equal("[ANONYMIZED]", (await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == ghost.Id)).FullName);

        // ไม่พบบัญชี ⇒ 404 และคำขอยังค้าง (ไม่ปิดว่าเสร็จ)
        var orphan = new PlatformErasureRequest { UserId = Guid.NewGuid(), RequesterEmail = "x@example.test", RequestedAt = DateTime.UtcNow,
            DueAt = DateTime.UtcNow.AddDays(30), Status = PlatformErasureRequestPolicy.StatusPending };
        db.PlatformErasureRequests.Add(orphan);
        await db.SaveChangesAsync();
        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessRuleException>(() => pdpa.CompletePlatformErasureAsync(orphan.Id, admin, null, null))).StatusCode);
        db.ChangeTracker.Clear();
        Assert.Equal(PlatformErasureRequestPolicy.StatusPending, (await db.PlatformErasureRequests.AsNoTracking().SingleAsync(x => x.Id == orphan.Id)).Status);
    }

    /// <summary>ฝ่ายค้านรอบ 214 (ฑ-2) B1-a/S4-b: ลบผู้ติดต่อ + สแกน OCR ตามคำขอ ⇒ แถว audit ของการบันทึกนั้น<b>ไม่มีค่าเดิม</b> (ชื่อ/อีเมล/โทร/ที่อยู่/ข้อความสแกน) ·
    /// ลบผู้ใช้ ⇒ การผูกผู้ใช้ภายนอก (X-Acting-User) ถูกถอด · ทิศตรงข้าม: แก้ผู้ติดต่อตามปกติยังเก็บประวัติค่าใน audit · ขอบเขตการลบคืนค่าหลังบันทึก</summary>
    [Fact]
    public async Task Contact_and_ocr_erasure_leaves_no_original_pii_in_audit_but_ordinary_edits_keep_history()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var tag = Guid.NewGuid().ToString("N")[..10];
        var company = new Company { Name = "บริษัททดสอบลบผู้ติดต่อ", TaxId = "0105556000007" };
        var staff = new User { Email = $"staff-{tag}@example.test", PasswordHash = "x", FullName = "staff" };
        var owner2 = new User { Email = $"own2-{tag}@example.test", PasswordHash = "x", FullName = "owner2" };
        db.Companies.Add(company);
        db.Users.AddRange(staff, owner2);
        await db.SaveChangesAsync();
        db.CompanyUsers.AddRange(
            new CompanyUser { UserId = staff.Id, CompanyId = company.Id, Role = Accounting.Models.Enums.UserRole.Staff },
            new CompanyUser { UserId = owner2.Id, CompanyId = company.Id, Role = Accounting.Models.Enums.UserRole.Owner, IsDefault = true });
        var victim = new Contact { CompanyId = company.Id, Name = $"EraseName-{tag}", Email = $"erase-{tag}@example.test",
            Phone = "0899990001", Address = $"EraseRoad-{tag}", IsCustomer = true };
        var keeper = new Contact { CompanyId = company.Id, Name = $"Keeper-{tag}", Phone = "0811110000", IsCustomer = true };
        db.Contacts.AddRange(victim, keeper);
        var integration = new ExternalIntegration { CompanyId = company.Id, SystemName = "TakeTime", SystemType = "PMS" };
        db.Set<ExternalIntegration>().Add(integration);
        await db.SaveChangesAsync();
        db.Set<IntegrationUserMapping>().Add(new IntegrationUserMapping { CompanyId = company.Id, IntegrationId = integration.Id,
            UserId = staff.Id, ExternalUserKey = staff.Email, ExternalUserName = $"ExtName-{tag}" });
        var scan = new OcrScanResult { CompanyId = company.Id, OriginalFileName = $"ScanFile-{tag}.pdf", ScanStatus = "Completed",
            MatchedContactId = victim.Id, RawTextContent = $"RawText-{tag} EraseName-{tag}", ExtractedVendorName = $"EraseName-{tag}" };
        db.Set<OcrScanResult>().Add(scan);
        await db.SaveChangesAsync();
        // สแกนพ้นงวดเก็บ 5 ปี (ถึงลบช่อง PII ได้จริง) — ตั้งวันที่ตรงในฐาน
        await db.Set<OcrScanResult>().Where(x => x.Id == scan.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddYears(-6)));
        db.ChangeTracker.Clear();

        var pdpa = new PdpaService(db);
        Assert.True(await pdpa.ApplyErasureAsync(company.Id, null, victim.Id, "dpo-test") > 0);
        Assert.False(db.AuditErasureScope);                              // ขอบเขตคืนค่าหลังบันทึก
        await pdpa.ApplyErasureAsync(company.Id, staff.Id, null, "dpo-test");
        Assert.False(db.AuditErasureScope);

        // ทิศตรงข้าม: แก้ผู้ติดต่ออีกรายตามปกติ ⇒ audit ยังเก็บค่าใหม่
        var k = await db.Contacts.SingleAsync(x => x.Id == keeper.Id);
        k.Phone = "0822220000";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var audits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == victim.Id.ToString() || a.EntityId == scan.Id.ToString() || a.EntityId == staff.Id.ToString()
                        || a.EntityType == nameof(IntegrationUserMapping))
            .Where(a => a.Action == Accounting.Models.Enums.AuditAction.Update)
            .ToListAsync();
        Assert.Contains(audits, a => a.EntityType == nameof(Contact));
        Assert.Contains(audits, a => a.EntityType == nameof(OcrScanResult));
        foreach (var a in audits)
        {
            var text = (a.OldValues ?? "") + (a.NewValues ?? "");
            Assert.DoesNotContain(tag, text);                             // ชื่อ/อีเมล/ที่อยู่/ข้อความสแกน/ชื่อไฟล์/ชื่อภายนอก
            Assert.DoesNotContain("0899990001", text);
        }
        var keeperAudit = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == keeper.Id.ToString() && a.Action == Accounting.Models.Enums.AuditAction.Update).ToListAsync();
        Assert.Contains(keeperAudit, a => (a.NewValues ?? "").Contains("0822220000") && (a.OldValues ?? "").Contains("0811110000"));

        // S4-b: การผูกผู้ใช้ภายนอกถูกถอด + ไม่เหลือคีย์อีเมล
        var mapping = await db.Set<IntegrationUserMapping>().IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.IntegrationId == integration.Id);
        Assert.True(mapping.IsDeleted);
        Assert.Null(mapping.ExternalUserName);
        Assert.DoesNotContain(tag, mapping.ExternalUserKey);
    }
}
