using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
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

        // ผู้ใช้ที่ยอมรับฉบับปัจจุบันแล้ว
        var user = new User { Email = $"legal-{Guid.NewGuid():N}@example.test", PasswordHash = "x", FullName = "ทดสอบนโยบาย" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var before = await svc.GetAcceptanceStatusAsync(user.Id);
        Assert.True(before.NeedsAcknowledge);   // ยังไม่มีหลักฐานเลย
        var accepted = await svc.RecordAcceptanceAsync(user.Id, before.CurrentConsentVersion, "203.0.113.9", "xunit");
        Assert.False(accepted.NeedsAcknowledge);

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
        var rec = await db.PdpaConsentRecords.AsNoTracking()
            .Where(c => c.SubjectUserId == user.Id && c.Purpose == PdpaPolicy.PolicyUpdatePurpose)
            .OrderByDescending(c => c.GrantedAt).FirstAsync();
        Assert.Equal(after.CurrentConsentVersion, rec.PolicyVersion);
        Assert.Equal("policy-update-banner", rec.Channel);
        Assert.Equal(64, rec.EvidenceHash!.Length);   // SHA-256 hex จาก canonical ตัวเดียวกับตอนสมัคร (round-trip เวลาผ่าน PG: ดูหมายเหตุความเสี่ยงรอบ 214)
    }
}
