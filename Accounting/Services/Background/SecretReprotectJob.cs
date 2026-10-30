using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace Accounting.Services.Background;

/// <summary>
/// ฝ่ายค้านรอบ 219 B2 — <b>เข้ารหัสความลับใหม่ด้วยคีย์ปัจจุบัน</b> หลังเปลี่ยนแหล่งคีย์ (<see cref="EncryptionKeyResolver"/>)
///
/// <para>ค่าที่เข้ารหัสด้วยคีย์เดิม (config เดิม/dev key) ยังถอดได้ผ่านคีย์เดิมของ <see cref="SecretProtector"/> (ไม่มีอะไรพังทันที) ·
/// งานนี้ย้ายค่าเหล่านั้นไปคีย์ปัจจุบัน<b>ครั้งเดียวต่อการบูต</b> (ล็อก <see cref="JobLock"/> — หลาย instance ไม่ทำซ้ำ) แล้วบันทึกจำนวน +
/// audit chain ต่อแถว (ชื่อช่องเท่านั้น ไม่มีค่า) · ค่าที่ถอดไม่ออกทุกคีย์ <b>ไม่ถูกแตะ</b> (คงอยู่ · <c>IsUsable=false</c> ⇒ ผู้ใช้กรอกใหม่) ·
/// รอบ 2: อ่านทีละชุด + เขียนแบบมีเงื่อนไขค่าเดิม (ไม่ทับค่าที่ผู้ใช้เพิ่งหมุน)</para>
///
/// <para><b>ทะเบียนช่องความลับ (ตัวเดียว):</b> ทุกช่องที่เขียนผ่าน <c>ISecretProtector.Protect</c> — เพิ่มช่องใหม่ต้องเพิ่มที่นี่
/// (ล็อกด้วย <c>tools/secret_registry_check.py</c> — เทียบทุกจุดที่เขียน <c>.Protect(</c>/<c>EncryptionHelper.Encrypt(</c> กับทะเบียนนี้)</para>
/// </summary>
public static class SecretReprotectPass
{
    /// <summary>ฝ่ายค้านรอบ 219 รอบ 2: อ่านทีละชุด (keyset ตาม Id) — ไม่โหลดทั้งตารางเข้าหน่วยความจำ</summary>
    public const int BatchSize = 200;

    /// <param name="SkippedConcurrent">ค่าที่ถูกผู้ใช้เปลี่ยนระหว่างอ่าน→เขียน (เงื่อนไข "ค่าเดิม" ไม่ตรง) ⇒ ไม่เขียนทับ</param>
    public sealed record Result(int Scanned, int Reprotected, IReadOnlyDictionary<string, int> PerColumn, int SkippedConcurrent = 0);

    /// <summary>ช่องความลับหนึ่งช่อง — ชื่อคอลัมน์มาจากนิพจน์ (<c>r =&gt; r.Col</c>) ตัวเดียวกับที่ใช้อ่าน ⇒ ชื่อพิมพ์ผิดไม่ได้</summary>
    public sealed record Column<T>(string Name, Func<T, string?> Get);

    public static Column<T> Col<T>(Expression<Func<T, string?>> e)
        => new(((MemberExpression)e.Body).Member.Name, e.Compile());

    /// <param name="onlyCompanyId">จำกัดเฉพาะแถวของบริษัทนี้ (ซ่อมรายบริษัท/เทสต์บนฐานร่วม) · null = ทุกแถว รวมค่าตั้งแพลตฟอร์ม</param>
    /// <param name="beforeWrite">จุดสอดของเทสต์ — เรียกหลังอ่านค่า ก่อนเขียนแบบมีเงื่อนไข (จำลองผู้ใช้หมุนคีย์พร้อมกัน) · production = null</param>
    public static async Task<Result> RunAsync(AccountingDbContext db, ISecretProtector protector, CancellationToken ct = default,
        Guid? onlyCompanyId = null, Func<Guid, Task>? beforeWrite = null)
    {
        var per = new Dictionary<string, int>();
        var scanned = 0;
        var skipped = 0;

        IQueryable<T> Scope<T>(IQueryable<T> q) where T : TenantEntity
            => onlyCompanyId is Guid c ? q.Where(r => r.CompanyId == c) : q;

        // ฝ่ายค้านรอบ 219 รอบ 2 (ข้อ 1 · lost update): เดิมโหลดทุกแถวแล้ว SaveChanges ครั้งเดียวท้ายสุด ไม่มี concurrency token ⇒
        // ผู้ใช้หมุนคีย์ (บันทึกค่าใหม่) ระหว่างงานวิ่ง ⇒ งานเขียนค่าเก่าที่เข้ารหัสใหม่ทับ = ความลับใหม่หาย ·
        // ตอนนี้: อ่านแบบ AsNoTracking ทีละชุด · เขียนทีละค่าด้วย UPDATE ... WHERE Id = @id AND Col = @ciphertextเดิม ⇒ ค่าเปลี่ยนไปแล้ว = 0 แถว = ไม่แตะ
        async Task Pass<T>(IQueryable<T> src, string table, Func<T, Guid?> company, params Column<T>[] cols) where T : BaseEntity
        {
            Guid? last = null;
            while (true)
            {
                var page = src.AsNoTracking();
                if (last is Guid l) page = page.Where(r => r.Id.CompareTo(l) > 0);
                var batch = await page.OrderBy(r => r.Id).Take(BatchSize).ToListAsync(ct);
                if (batch.Count == 0) break;
                last = batch[^1].Id;

                var audits = 0;
                foreach (var row in batch)
                {
                    var changed = new List<string>();
                    foreach (var c in cols)
                    {
                        var v = c.Get(row);
                        if (string.IsNullOrEmpty(v)) continue;
                        scanned++;
                        if (protector.Reprotect(v) is not string nv) continue;
                        if (beforeWrite != null) await beforeWrite(row.Id);
                        var id = row.Id;
                        var name = c.Name;
                        var n = await src
                            .Where(r => r.Id == id && EF.Property<string?>(r, name) == v)
                            .ExecuteUpdateAsync(s => s.SetProperty(r => EF.Property<string?>(r, name), nv), ct);
                        if (n == 1)
                        {
                            changed.Add(name);
                            per[$"{table}.{name}"] = per.GetValueOrDefault($"{table}.{name}") + 1;
                        }
                        else skipped++;   // ค่าเปลี่ยนไปแล้ว (ผู้ใช้บันทึกใหม่/ลบ) ⇒ ของผู้ใช้ชนะ
                    }
                    if (changed.Count > 0)
                    {
                        audits++;
                        db.AddChainedAuditLog(new AuditLog
                        {
                            CompanyId = company(row),
                            Action = AuditAction.Update,
                            EntityType = table,
                            EntityId = row.Id.ToString(),
                            NewValues = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                action = "SecretReprotected", fields = changed, ruleCode = "SEC-KEY-REPROTECT",
                            }),
                            Timestamp = DateTime.UtcNow,
                        });
                    }
                }
                if (audits > 0) await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                if (batch.Count < BatchSize) break;
            }
        }

        await Pass(Scope(db.CompanySettings.IgnoreQueryFilters()), "CompanySettings", r => r.CompanyId,
            Col<CompanySettings>(r => r.EmailSmtpPassword),
            Col<CompanySettings>(r => r.EmailMsClientSecret),
            Col<CompanySettings>(r => r.EmailGmailClientSecret),
            Col<CompanySettings>(r => r.EmailGmailRefreshToken),
            Col<CompanySettings>(r => r.LineChannelAccessToken),
            Col<CompanySettings>(r => r.LineChannelSecret),
            Col<CompanySettings>(r => r.EtaxCertificatePassword),
            Col<CompanySettings>(r => r.EtaxRdApiSecret));
        if (onlyCompanyId == null)   // ค่าตั้งแพลตฟอร์ม ไม่มีบริษัท
            await Pass(db.SiteSettings.IgnoreQueryFilters(), "SiteSettings", _ => (Guid?)null,
                Col<SiteSettings>(r => r.SystemSmtpPassword),
                Col<SiteSettings>(r => r.SystemMsClientSecret),
                Col<SiteSettings>(r => r.SystemGmailClientSecret),
                Col<SiteSettings>(r => r.SystemGmailRefreshToken),
                Col<SiteSettings>(r => r.LineMessagingChannelAccessToken),
                Col<SiteSettings>(r => r.LineMessagingChannelSecret),
                Col<SiteSettings>(r => r.GoogleClientSecret),
                Col<SiteSettings>(r => r.FacebookAppSecret),
                Col<SiteSettings>(r => r.LineLoginChannelSecret));
        await Pass(Scope(db.PaymentProviderConfigs.IgnoreQueryFilters()), "PaymentProviderConfigs", r => r.CompanyId,
            Col<PaymentProviderConfig>(r => r.TestSecretKeyProtected),
            Col<PaymentProviderConfig>(r => r.LiveSecretKeyProtected));
        await Pass(Scope(db.BankConnections.IgnoreQueryFilters()), "BankConnections", r => r.CompanyId,
            Col<BankConnection>(r => r.EncryptedCredentials),
            Col<BankConnection>(r => r.AccessToken));
        await Pass(Scope(db.WebhookRegistrations.IgnoreQueryFilters()), "WebhookRegistrations", r => r.CompanyId,
            Col<WebhookRegistration>(r => r.Secret));
        await Pass(Scope(db.ExternalIntegrations.IgnoreQueryFilters()), "ExternalIntegrations", r => r.CompanyId,
            Col<ExternalIntegration>(r => r.SecretKey));
        // ฝ่ายค้านรอบ 219 รอบ 2: คีย์ gateway ของเว็บ (CmsCommerceService เขียนด้วย EncryptionHelper + คีย์เดียวกับ SecretProtector) +
        // webhook secret (CmsWebhookController ถอดด้วย SecretProtector) — เดิมไม่อยู่ในทะเบียน ⇒ ค้างบน dev key/config เดิม
        await Pass(Scope(db.SitePaymentGateways.IgnoreQueryFilters()), "SitePaymentGateways", r => r.CompanyId,
            Col<SitePaymentGateway>(r => r.ApiKeyEncrypted),
            Col<SitePaymentGateway>(r => r.SecretKeyEncrypted),
            Col<SitePaymentGateway>(r => r.WebhookSecret));

        return new Result(scanned, per.Values.Sum(), per, skipped);
    }
}

/// <summary>รันรอบเดียวหลังบูต (รอ migration 3 นาที) — ล็อก <see cref="JobLock"/> ทั้งระบบ · บันทึกผลลง <see cref="IJobRunRecorder"/> · ล้ม = log + บันทึกล้ม (ไม่ล้มแอป)</summary>
public class SecretReprotectJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SecretReprotectJob> _logger;

    public SecretReprotectJob(IServiceScopeFactory scopeFactory, ILogger<SecretReprotectJob> logger)
    { _scopeFactory = scopeFactory; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch { return; }
        var startedAt = DateTime.UtcNow;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
            await JobLock.RunExclusiveAsync(db, AdvisoryLockKey.BackgroundJob, nameof(SecretReprotectJob), async () =>
            {
                var r = await SecretReprotectPass.RunAsync(db, protector, stoppingToken);
                var summary = $"ตรวจความลับ {r.Scanned} ค่า · เข้ารหัสใหม่ด้วยคีย์ปัจจุบัน {r.Reprotected} ค่า"
                    + (r.PerColumn.Count == 0 ? "" : " (" + string.Join(", ", r.PerColumn.Select(kv => $"{kv.Key}={kv.Value}")) + ")");
                if (r.Reprotected > 0) _logger.LogWarning("SecretReprotect: {Summary}", summary);
                if (recorder != null) await recorder.RecordAsync("SecretReprotect", true, summary, r.Reprotected, startedAt);
            }, _logger, ct: stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SecretReprotectJob ล้ม — ความลับที่เข้ารหัสด้วยคีย์เดิมยังถอดได้ผ่านคีย์เดิม จะลองใหม่ในการบูตครั้งถัดไป");
        }
    }
}
