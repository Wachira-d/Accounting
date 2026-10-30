using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// ฝ่ายค้านรอบ 219 B2 — <b>เข้ารหัสความลับใหม่ด้วยคีย์ปัจจุบัน</b> หลังเปลี่ยนแหล่งคีย์ (<see cref="EncryptionKeyResolver"/>)
///
/// <para>ค่าที่เข้ารหัสด้วยคีย์เดิม (config เดิม/dev key) ยังถอดได้ผ่านคีย์เดิมของ <see cref="SecretProtector"/> (ไม่มีอะไรพังทันที) ·
/// งานนี้ย้ายค่าเหล่านั้นไปคีย์ปัจจุบัน<b>ครั้งเดียวต่อการบูต</b> (ล็อก <see cref="JobLock"/> — หลาย instance ไม่ทำซ้ำ) แล้วบันทึกจำนวน +
/// audit chain ต่อแถว (ชื่อช่องเท่านั้น ไม่มีค่า) · ค่าที่ถอดไม่ออกทุกคีย์ <b>ไม่ถูกแตะ</b> (คงอยู่ · <c>IsUsable=false</c> ⇒ ผู้ใช้กรอกใหม่)</para>
///
/// <para><b>ทะเบียนช่องความลับ (ตัวเดียว):</b> ทุกช่องที่เขียนผ่าน <c>ISecretProtector.Protect</c> — เพิ่มช่องใหม่ต้องเพิ่มที่นี่
/// (ล็อกด้วย required_call_site_check)</para>
/// </summary>
public static class SecretReprotectPass
{
    public sealed record Result(int Scanned, int Reprotected, IReadOnlyDictionary<string, int> PerColumn);

    /// <param name="onlyCompanyId">จำกัดเฉพาะแถวของบริษัทนี้ (ซ่อมรายบริษัท/เทสต์บนฐานร่วม) · null = ทุกแถว รวมค่าตั้งแพลตฟอร์ม</param>
    public static async Task<Result> RunAsync(AccountingDbContext db, ISecretProtector protector, CancellationToken ct = default,
        Guid? onlyCompanyId = null)
    {
        var per = new Dictionary<string, int>();
        var scanned = 0;

        async Task Pass<T>(IQueryable<T> q, string table, Func<T, Guid?> company, Func<T, string> id,
            params (string Name, Func<T, string?> Get, Action<T, string> Set)[] cols) where T : class
        {
            var rows = (await q.ToListAsync(ct)).Where(r => onlyCompanyId == null || company(r) == onlyCompanyId);
            foreach (var row in rows)
            {
                var changed = new List<string>();
                foreach (var c in cols)
                {
                    var v = c.Get(row);
                    if (string.IsNullOrEmpty(v)) continue;
                    scanned++;
                    if (protector.Reprotect(v) is string nv)
                    {
                        c.Set(row, nv);
                        changed.Add(c.Name);
                        per[$"{table}.{c.Name}"] = per.GetValueOrDefault($"{table}.{c.Name}") + 1;
                    }
                }
                if (changed.Count > 0)
                    db.AddChainedAuditLog(new AuditLog
                    {
                        CompanyId = company(row),
                        Action = AuditAction.Update,
                        EntityType = table,
                        EntityId = id(row),
                        NewValues = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            action = "SecretReprotected", fields = changed, ruleCode = "SEC-KEY-REPROTECT",
                        }),
                        Timestamp = DateTime.UtcNow,
                    });
            }
            await db.SaveChangesAsync(ct);
        }

        await Pass<CompanySettings>(db.CompanySettings.IgnoreQueryFilters(), "CompanySettings", r => r.CompanyId, r => r.Id.ToString(),
            ("EmailSmtpPassword", r => r.EmailSmtpPassword, (r, v) => r.EmailSmtpPassword = v),
            ("EmailMsClientSecret", r => r.EmailMsClientSecret, (r, v) => r.EmailMsClientSecret = v),
            ("EmailGmailClientSecret", r => r.EmailGmailClientSecret, (r, v) => r.EmailGmailClientSecret = v),
            ("EmailGmailRefreshToken", r => r.EmailGmailRefreshToken, (r, v) => r.EmailGmailRefreshToken = v),
            ("LineChannelAccessToken", r => r.LineChannelAccessToken, (r, v) => r.LineChannelAccessToken = v),
            ("LineChannelSecret", r => r.LineChannelSecret, (r, v) => r.LineChannelSecret = v),
            ("EtaxCertificatePassword", r => r.EtaxCertificatePassword, (r, v) => r.EtaxCertificatePassword = v),
            ("EtaxRdApiSecret", r => r.EtaxRdApiSecret, (r, v) => r.EtaxRdApiSecret = v));
        await Pass<SiteSettings>(db.SiteSettings.IgnoreQueryFilters(), "SiteSettings", _ => null, r => r.Id.ToString(),
            ("SystemSmtpPassword", r => r.SystemSmtpPassword, (r, v) => r.SystemSmtpPassword = v),
            ("SystemMsClientSecret", r => r.SystemMsClientSecret, (r, v) => r.SystemMsClientSecret = v),
            ("SystemGmailClientSecret", r => r.SystemGmailClientSecret, (r, v) => r.SystemGmailClientSecret = v),
            ("SystemGmailRefreshToken", r => r.SystemGmailRefreshToken, (r, v) => r.SystemGmailRefreshToken = v),
            ("LineMessagingChannelAccessToken", r => r.LineMessagingChannelAccessToken, (r, v) => r.LineMessagingChannelAccessToken = v),
            ("LineMessagingChannelSecret", r => r.LineMessagingChannelSecret, (r, v) => r.LineMessagingChannelSecret = v),
            ("GoogleClientSecret", r => r.GoogleClientSecret, (r, v) => r.GoogleClientSecret = v),
            ("FacebookAppSecret", r => r.FacebookAppSecret, (r, v) => r.FacebookAppSecret = v),
            ("LineLoginChannelSecret", r => r.LineLoginChannelSecret, (r, v) => r.LineLoginChannelSecret = v));
        await Pass<PaymentProviderConfig>(db.PaymentProviderConfigs.IgnoreQueryFilters(), "PaymentProviderConfigs", r => r.CompanyId, r => r.Id.ToString(),
            ("TestSecretKeyProtected", r => r.TestSecretKeyProtected, (r, v) => r.TestSecretKeyProtected = v),
            ("LiveSecretKeyProtected", r => r.LiveSecretKeyProtected, (r, v) => r.LiveSecretKeyProtected = v));
        await Pass<BankConnection>(db.BankConnections.IgnoreQueryFilters(), "BankConnections", r => r.CompanyId, r => r.Id.ToString(),
            ("EncryptedCredentials", r => r.EncryptedCredentials, (r, v) => r.EncryptedCredentials = v),
            ("AccessToken", r => r.AccessToken, (r, v) => r.AccessToken = v));
        await Pass<WebhookRegistration>(db.WebhookRegistrations.IgnoreQueryFilters(), "WebhookRegistrations", r => r.CompanyId, r => r.Id.ToString(),
            ("Secret", r => r.Secret, (r, v) => r.Secret = v));
        await Pass<ExternalIntegration>(db.ExternalIntegrations.IgnoreQueryFilters(), "ExternalIntegrations", r => r.CompanyId, r => r.Id.ToString(),
            ("SecretKey", r => r.SecretKey, (r, v) => r.SecretKey = v));

        return new Result(scanned, per.Values.Sum(), per);
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
