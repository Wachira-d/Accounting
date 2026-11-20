using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Background;
using Accounting.Services.Implementations.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 275 แพ็ก A (T4 H-1 · M-4) บน PostgreSQL จริง:
/// หมุนคีย์ env→env แล้วตั้ง ENCRYPTION_KEY_PREVIOUS ⇒ ความลับที่เข้ารหัสด้วยคีย์เดิม<b>ถูกย้ายจริง</b>โดยงานย้ายคีย์ (เดิม WrongKey ทั้งชุด) ·
/// โหมดตรวจอย่างเดียวนับสถานะโดยไม่เขียน · ลายนิ้วมือคีย์: บันทึกครั้งแรก → หมุนตามขั้นตอน → เครื่องคีย์เก่า/คีย์แปลกถูกห้ามย้าย · ยืนยันคีย์ = audit
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]   // แตะแถว SiteSettings ร่วม — ไม่ขนานกับคลาสที่แก้ค่าตั้งระบบ
public class KeysSecretsRound275ADbTests
{
    private readonly ITestOutputHelper _out;
    public KeysSecretsRound275ADbTests(ITestOutputHelper output) => _out = output;

    private const string K1 = "r275a-old-key-K1";
    private const string K2 = "r275a-new-key-K2";

    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
        => n => vars.FirstOrDefault(v => v.Name == n).Value;

    [Fact]
    public async Task หมุนคีย์_env_ไป_env_แล้วตั้ง_PREVIOUS_งานย้ายคีย์ย้ายความลับไปคีย์ใหม่_และตรวจอย่างเดียวไม่เขียน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบหมุนคีย์ 275A", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var oldCipher = new SecretProtector(K1).Protect("whsec_rotated");
        var lostCipher = new SecretProtector("r275a-lost-key").Protect("whsec_lost");
        var a = new WebhookRegistration { CompanyId = company.Id, Name = "a", Url = "https://example.com/a", Secret = oldCipher };
        var b = new WebhookRegistration { CompanyId = company.Id, Name = "b", Url = "https://example.com/b", Secret = lostCipher };
        db.WebhookRegistrations.AddRange(a, b);
        await db.SaveChangesAsync();

        // ทิศตรงข้าม (บั๊กเดิม): ห่วงโซ่แบบรอบ 219 (LegacyCandidates) ไม่มีคีย์ PREVIOUS ⇒ ย้ายไม่ได้ · ค่าเดิมใช้ไม่ได้
        var before = new SecretProtector(K2, null, EncryptionKeyResolver.LegacyCandidates(K2, K2, null));
        Assert.Equal(SecretState.WrongKey, before.Inspect(oldCipher));

        var chain = EncryptionKeyResolver.DecryptKeyChain(K2, Env(("ENCRYPTION_KEY", K2), (EncryptionKeyResolver.PreviousKeyEnv, K1)), null, null);
        var current = new SecretProtector(K2, null, chain);

        var dry = await SecretReprotectPass.RunAsync(db, current, default, company.Id, inspectOnly: true);
        Assert.Equal(0, dry.Reprotected);
        Assert.Equal(1, dry.LegacyKeyRemaining);
        Assert.Equal(1, dry.Unusable);
        Assert.Contains(dry.Findings!, f => f.RowId == b.Id && f.State == SecretState.WrongKey && f.Label == SecretReprotectPass.Label("WebhookRegistrations", "Secret"));
        using (var c0 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(oldCipher, (await c0.WebhookRegistrations.AsNoTracking().SingleAsync(w => w.Id == a.Id)).Secret);   // ตรวจอย่างเดียว = ไม่เขียน

        var r = await SecretReprotectPass.RunAsync(db, current, default, company.Id);
        Assert.Equal(1, r.Reprotected);
        Assert.Equal(0, r.LegacyKeyRemaining);
        Assert.Contains("✓ ไม่มีความลับบนคีย์เดิมเหลือ", SecretReprotectJob.Summary(r));

        using var check = DbTestDatabase.TryCreateContext()!;
        var a2 = await check.WebhookRegistrations.AsNoTracking().SingleAsync(w => w.Id == a.Id);
        Assert.Equal("whsec_rotated", new SecretProtector(K2).Unprotect(a2.Secret));   // ถอด PREVIOUS ออกได้แล้ว
        Assert.Equal(lostCipher, (await check.WebhookRegistrations.AsNoTracking().SingleAsync(w => w.Id == b.Id)).Secret);   // ไม่รู้คีย์ = ไม่แตะ
    }

    [Fact]
    public async Task ลายนิ้วมือคีย์_บันทึกครั้งแรก_หมุนตามขั้นตอน_เครื่องคีย์เก่าถูกห้ามย้าย_ยืนยันคีย์มี_audit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await db.SiteSettings.OrderBy(x => x.Id).FirstOrDefaultAsync();
        var created = s == null;
        if (s == null) { s = new SiteSettings(); db.SiteSettings.Add(s); }
        var orig = (s.EncryptionKeyFingerprint, s.EncryptionKeyFingerprintPrevious, s.EncryptionKeyFingerprintAt);
        s.EncryptionKeyFingerprint = null; s.EncryptionKeyFingerprintPrevious = null;
        await db.SaveChangesAsync();
        try
        {
            var k1 = new SecretProtector(K1);
            var first = await EncryptionKeyFingerprintService.CheckAsync(db, k1, persist: true);
            Assert.Equal(KeyFingerprintVerdict.FirstSeen, first.Verdict);
            Assert.Equal(KeyFingerprintVerdict.Match, (await EncryptionKeyFingerprintService.CheckAsync(db, k1, persist: true)).Verdict);

            // อีกเครื่องตั้งคีย์ใหม่ไม่มี PREVIOUS ⇒ Mismatch · ห้ามย้าย · ไม่เขียนทับลายนิ้วมือ
            var stranger = new SecretProtector("r275a-stranger");
            var mm = await EncryptionKeyFingerprintService.CheckAsync(db, stranger, persist: true);
            Assert.Equal(KeyFingerprintVerdict.Mismatch, mm.Verdict);
            Assert.False(mm.AllowsRekey);
            Assert.Equal(EncryptionKeyResolver.Fingerprint(K1), mm.Stored);

            // หมุนตามขั้นตอน (K2 + PREVIOUS=K1) ⇒ Rotated → บันทึก K2 + audit
            var k2 = new SecretProtector(K2, null, new[] { K1 });
            var rot = await EncryptionKeyFingerprintService.CheckAsync(db, k2, persist: true);
            Assert.Equal(KeyFingerprintVerdict.Rotated, rot.Verdict);
            Assert.True(rot.AllowsRekey);
            // เครื่องที่ยังใช้ K1 (หรือตั้ง PREVIOUS กลับด้าน) ⇒ RotatedBack · ห้ามย้ายแถวกลับ
            var back = await EncryptionKeyFingerprintService.CheckAsync(db, new SecretProtector(K1, null, new[] { K2 }), persist: true);
            Assert.Equal(KeyFingerprintVerdict.RotatedBack, back.Verdict);
            Assert.False(back.AllowsRekey);

            // ผู้ดูแลยืนยันคีย์ต้องมีเหตุผล · ยืนยันแล้ว = Match + audit chain
            await Assert.ThrowsAsync<BusinessRuleException>(() => EncryptionKeyFingerprintService.AcceptCurrentAsync(db, stranger, null, null, " "));
            var accepted = await EncryptionKeyFingerprintService.AcceptCurrentAsync(db, stranger, null, "admin@test", "ย้ายเครื่อง ตรวจคีย์ทุกเครื่องแล้ว");
            Assert.Equal(KeyFingerprintVerdict.Match, accepted.Verdict);
            using var check = DbTestDatabase.TryCreateContext()!;
            Assert.True(await check.AuditLogs.AsNoTracking().AnyAsync(l => l.EntityType == "SiteSettings" && l.EntityId == s.Id.ToString()
                && l.NewValues!.Contains("EncryptionKeyFingerprintAccepted")));
        }
        finally
        {
            using var restore = DbTestDatabase.TryCreateContext()!;
            var again = await restore.SiteSettings.FirstAsync(x => x.Id == s.Id);
            if (created) restore.SiteSettings.Remove(again);
            else (again.EncryptionKeyFingerprint, again.EncryptionKeyFingerprintPrevious, again.EncryptionKeyFingerprintAt) = orig;
            await restore.SaveChangesAsync();
        }
    }
}
