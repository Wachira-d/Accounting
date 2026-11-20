using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Background;
using Accounting.Services.Implementations;
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
[Collection(EncryptionGlobalStateCollection.Name)]   // แตะแถว SiteSettings + กุญแจ HMAC/ประตูเขียน (สถานะทั้ง process) — รันไม่ขนาน
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

    private sealed record SiteSnapshot(Guid Id, bool Created, string? Fp, string? FpPrev, DateTime? FpAt, string? Seed);

    private static async Task<SiteSnapshot> TakeSiteAsync(Accounting.Data.AccountingDbContext db)
    {
        var s = await db.SiteSettings.FirstOrDefaultAsync();
        var created = s == null;
        if (s == null) { s = new SiteSettings(); db.SiteSettings.Add(s); await db.SaveChangesAsync(); }
        var snap = new SiteSnapshot(s.Id, created, s.EncryptionKeyFingerprint, s.EncryptionKeyFingerprintPrevious, s.EncryptionKeyFingerprintAt, s.HmacSeedProtected);
        s.EncryptionKeyFingerprint = null; s.EncryptionKeyFingerprintPrevious = null; s.HmacSeedProtected = null;
        await db.SaveChangesAsync();
        await db.EncryptionKeyHeartbeats.ExecuteDeleteAsync();
        return snap;
    }

    private static async Task RestoreSiteAsync(SiteSnapshot snap)
    {
        using var restore = DbTestDatabase.TryCreateContext()!;
        var again = await restore.SiteSettings.FirstAsync(x => x.Id == snap.Id);
        if (snap.Created) restore.SiteSettings.Remove(again);
        else (again.EncryptionKeyFingerprint, again.EncryptionKeyFingerprintPrevious, again.EncryptionKeyFingerprintAt, again.HmacSeedProtected)
            = (snap.Fp, snap.FpPrev, snap.FpAt, snap.Seed);
        await restore.SaveChangesAsync();
        await restore.EncryptionKeyHeartbeats.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task ลายนิ้วมือคีย์_บันทึกครั้งแรก_หมุนรอทุกเครื่อง_เครื่องคีย์เก่าถูกห้ามย้าย_ยืนยันลายนิ้วมือที่ระบุ_มี_audit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var snap = await TakeSiteAsync(db);
        try
        {
            var k1 = new SecretProtector(K1);
            var first = await EncryptionKeyFingerprintService.CheckKeyAsync(db, k1, persist: true, instanceId: "A");
            Assert.Equal(KeyFingerprintVerdict.FirstSeen, first.Verdict);
            Assert.Equal(KeyFingerprintVerdict.Match, (await EncryptionKeyFingerprintService.CheckKeyAsync(db, k1, persist: true, instanceId: "A")).Verdict);

            // อีกเครื่องตั้งคีย์ใหม่ไม่มี PREVIOUS ⇒ Mismatch · ห้ามย้าย · ไม่เขียนทับลายนิ้วมือ
            var stranger = new SecretProtector("r275a-stranger");
            var mm = await EncryptionKeyFingerprintService.CheckKeyAsync(db, stranger, persist: true, instanceId: "S");
            Assert.Equal(KeyFingerprintVerdict.Mismatch, mm.Verdict);
            Assert.False(mm.AllowsRekey);
            Assert.Equal(EncryptionKeyResolver.Fingerprint(K1), mm.Stored);

            // MED-3: หมุนตามขั้นตอน (K2 + PREVIOUS=K1) แต่ยังมีเครื่อง A/S ส่ง heartbeat ด้วยคีย์อื่น ⇒ รอ · ไม่บันทึก · ไม่ย้าย
            var k2 = new SecretProtector(K2, null, new[] { K1 });
            var pending = await EncryptionKeyFingerprintService.CheckKeyAsync(db, k2, persist: true, instanceId: "C");
            Assert.Equal(KeyFingerprintVerdict.RotationPending, pending.Verdict);
            Assert.False(pending.AllowsRekey);
            Assert.Equal(EncryptionKeyResolver.Fingerprint(K1), pending.Stored);
            Assert.Equal(3, pending.Instances!.Count(i => i.Recent));
            // ทุกเครื่องที่ยังทำงานรายงานคีย์ใหม่แล้ว (A/S หยุด) ⇒ หมุนมีผล
            await db.EncryptionKeyHeartbeats.Where(h => h.InstanceId != "C").ExecuteDeleteAsync();
            var rot = await EncryptionKeyFingerprintService.CheckKeyAsync(db, k2, persist: true, instanceId: "C");
            Assert.Equal(KeyFingerprintVerdict.Rotated, rot.Verdict);
            Assert.True(rot.AllowsRekey);

            // เครื่องที่ยังใช้ K1 (หรือตั้ง PREVIOUS กลับด้าน) ⇒ RotatedBack · ห้ามย้ายแถวกลับ
            var back = await EncryptionKeyFingerprintService.CheckKeyAsync(db, new SecretProtector(K1, null, new[] { K2 }), persist: true, instanceId: "A");
            Assert.Equal(KeyFingerprintVerdict.RotatedBack, back.Verdict);
            Assert.False(back.AllowsRekey);

            // MED-1: ยืนยัน "ลายนิ้วมือที่ระบุ" — เครื่องที่รับคำขอใช้คีย์อื่น ⇒ 409 ไม่บันทึก · ไม่มีเหตุผล ⇒ ปฏิเสธ
            var conflict = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                EncryptionKeyFingerprintService.AcceptAsync(db, stranger, EncryptionKeyResolver.Fingerprint(K1), null, null, "ทดสอบ"));
            Assert.Equal(409, conflict.StatusCode);
            await Assert.ThrowsAsync<BusinessRuleException>(() =>
                EncryptionKeyFingerprintService.AcceptAsync(db, stranger, stranger.CurrentKeyFingerprint!, null, null, " "));
            var accepted = await EncryptionKeyFingerprintService.AcceptAsync(db, stranger, stranger.CurrentKeyFingerprint!, null, "admin@test",
                "ย้ายเครื่อง ตรวจคีย์ทุกเครื่องแล้ว");
            Assert.Equal(KeyFingerprintVerdict.Match, accepted.Verdict);
            using var check = DbTestDatabase.TryCreateContext()!;
            Assert.True(await check.AuditLogs.AsNoTracking().AnyAsync(l => l.EntityType == "SiteSettings" && l.EntityId == snap.Id.ToString()
                && l.NewValues!.Contains("EncryptionKeyFingerprintAccepted")));
            // ทิศตรงข้าม: ไม่มีปัญหา ⇒ ยืนยันซ้ำไม่ประทับอะไรเพิ่ม
            var n = await check.AuditLogs.CountAsync(l => l.EntityType == "SiteSettings" && l.EntityId == snap.Id.ToString());
            await EncryptionKeyFingerprintService.AcceptAsync(db, stranger, stranger.CurrentKeyFingerprint!, null, null, "ซ้ำ");
            Assert.Equal(n, await check.AuditLogs.CountAsync(l => l.EntityType == "SiteSettings" && l.EntityId == snap.Id.ToString()));
        }
        finally { await RestoreSiteAsync(snap); }
    }

    [Fact]
    public async Task MED4_หมุนคีย์แล้วส่งสลิปเดิม_แฮชเลขรายการธนาคารเทียบตรง_ถูกกันซ้ำ_เมล็ดถูกห่อใหม่แต่ค่าไม่เปลี่ยน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var snap = await TakeSiteAsync(db);
        try
        {
            var provider = new Company { Name = "สำนักงานบัญชีทดสอบ 275A", TaxId = "0105556000002" };
            db.Companies.Add(provider);
            await db.SaveChangesAsync();
            const string bank = "004", txnNo = "TXN-275A-0001";

            // บูตแรกด้วย K1 ⇒ เมล็ด = K1 · สลิปแรกบันทึกแฮช
            var bootK1 = new SecretProtector(K1);
            HmacSeedBootstrap.Configure(await HmacSeedBootstrap.EnsureAsync(db, bootK1, K1));
            var firstHash = TaxSlipRefHash.BankTxn(bank, txnNo)!;
            db.TaxSlipEvidences.Add(new TaxSlipEvidence
            {
                CompanyId = provider.Id, Source = TaxSlipIntakeService.SourceLine, ExternalMessageId = Guid.NewGuid().ToString("N"),
                SourceKey = new string('b', 64), FileSha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), StoragePath = "/tmp/x.png",
                ContentType = "image/png", BankCode = bank, BankTxnHash = firstHash, Amount = 100m, SlipDate = DateTime.UtcNow.Date,
                Status = TaxSlipIntakeService.Statuses.Received,
            });
            await db.SaveChangesAsync();

            // ทิศตรงข้าม (บั๊กเดิม): กุญแจ HMAC จาก ENCRYPTION_KEY ตรง ๆ ⇒ หมุนแล้วแฮชต่าง ⇒ กันซ้ำไม่ได้
            TaxSlipRefHash.ConfigureKey(K2);
            Assert.NotEqual(firstHash, TaxSlipRefHash.BankTxn(bank, txnNo));

            // หมุนคีย์ K1→K2 (PREVIOUS=K1) แล้วบูต ⇒ เมล็ดเดิม ⇒ สลิปเดิมถูกกันด้วยคำค้นเดียวกับ TaxSlipIntakeService
            var bootK2 = new SecretProtector(K2, null, new[] { K1 });
            HmacSeedBootstrap.Configure(await HmacSeedBootstrap.EnsureAsync(db, bootK2, K2));
            var again = TaxSlipRefHash.BankTxn(bank, txnNo);
            Assert.Equal(firstHash, again);
            Assert.True(await db.TaxSlipEvidences.AsNoTracking().AnyAsync(s => s.CompanyId == provider.Id && s.BankCode == bank && s.BankTxnHash == again));

            // งานย้ายคีย์ห่อเมล็ดใหม่ด้วย K2 (Reprotect ตัวเดียวกับ SecretReprotectPass — ช่องนี้อยู่ในทะเบียน) ⇒ ถอด PREVIOUS ออกแล้วบูตยังได้เมล็ด K1
            var site = await db.SiteSettings.FirstAsync(x => x.Id == snap.Id);
            Assert.Equal(SecretState.LegacyKey, bootK2.Inspect(site.HmacSeedProtected));
            site.HmacSeedProtected = bootK2.Reprotect(site.HmacSeedProtected);
            await db.SaveChangesAsync();
            using var c2 = DbTestDatabase.TryCreateContext()!;
            Assert.Equal(K1, await HmacSeedBootstrap.EnsureAsync(c2, new SecretProtector(K2), K2));
        }
        finally
        {
            HmacSeedBootstrap.Configure(EncryptionKeyResolver.DevKey);   // คืนสถานะ process (ค่าเริ่มต้นของเทสต์)
            await RestoreSiteAsync(snap);
        }
    }
}

/// <summary>รอบ 275 แพ็ก A: คลาสที่แตะสถานะทั้ง process (กุญแจ HMAC · ประตูเขียนค่าเข้ารหัส) หรือแถว SiteSettings ร่วม — รันไม่ขนาน</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EncryptionGlobalStateCollection
{
    public const string Name = "Encryption global state";
}
