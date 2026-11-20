using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Email;

public class EmailSenderFactory : IEmailSenderFactory
{
    private readonly AccountingDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<EmailSenderFactory> _logger;
    private readonly ISecretProtector _secrets;

    public EmailSenderFactory(AccountingDbContext db, IConfiguration config,
        IHttpClientFactory httpFactory, ILogger<EmailSenderFactory> logger,
        ISecretProtector secrets)
    {
        _db = db;
        _config = config;
        _httpFactory = httpFactory;
        _logger = logger;
        _secrets = secrets;
    }

    public async Task<IEmailSender> GetSenderAsync(Guid companyId, CancellationToken ct = default)
    {
        var settings = await _db.Set<CompanySettings>()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        return settings == null ? GetGlobalFallbackSender() : GetSenderForSettings(settings);
    }

    /// <summary>รอบ 240: บริษัทมีข้อมูลผู้ส่งของตัวเองครบไหม (ตัวตัดสินเดียวกับ <see cref="GetSenderForSettings"/> — ใช้ร่วมกับ
    /// <c>OnBehalfEmailSender</c> ว่าเส้นนี้จะส่ง "จากบริษัท" หรือ "ผ่าน NextAcc ในนามบริษัท")</summary>
    public static bool HasTenantSender(CompanySettings s) => s.EmailProvider switch
    {
        EmailProvider.MicrosoftGraph => AllPresent(s.EmailMsTenantId, s.EmailMsClientId, s.EmailMsClientSecret, s.EmailMsSenderUpn),
        EmailProvider.GmailApi => AllPresent(s.EmailGmailClientId, s.EmailGmailClientSecret, s.EmailGmailRefreshToken, s.EmailFromAddress),
        EmailProvider.Smtp => !string.IsNullOrWhiteSpace(s.EmailSmtpHost),
        _ => false,
    };

    public IEmailSender GetSenderForSettings(CompanySettings s)
    {
        if (!HasTenantSender(s)) return GetGlobalFallbackSender();
        // รอบ 275 แพ็ก A (T4 M-5 · S1): ความลับที่ตั้งไว้แต่ถอดไม่ได้ ⇒ ผู้ส่งที่ตอบ "ล้ม + เหตุจริง" ทุกครั้ง (แถวประวัติอีเมล/คำตอบผู้เรียกเห็นเหตุ) —
        // เดิมส่งรหัสว่างไปยืนยันตัวตนแล้วได้ "5.7.0 Authentication Required" ที่ชี้ต้นเหตุไม่ได้ · ไม่ถอยไปผู้ส่งกลางเงียบ ๆ (ผู้ใช้ตั้งใจส่งจากบริษัท)
        var problems = new List<string>();
        string? Use(string label, string? stored)
        {
            var o = SecretUse.Open(_secrets, label, stored);
            if (o.Unusable) problems.Add(o.Problem!);
            return o.Value;
        }
        IEmailSender sender = s.EmailProvider switch
        {
            EmailProvider.MicrosoftGraph =>
                new MicrosoftGraphEmailSender(s.EmailMsTenantId!, s.EmailMsClientId!,
                    Use("Client Secret ของ Microsoft 365", s.EmailMsClientSecret) ?? "", s.EmailMsSenderUpn!, _httpFactory, _logger),

            EmailProvider.GmailApi =>
                new GmailApiEmailSender(s.EmailGmailClientId!, Use("Client Secret ของ Gmail", s.EmailGmailClientSecret) ?? "",
                    Use("Refresh Token ของ Gmail", s.EmailGmailRefreshToken) ?? "", s.EmailFromAddress!, _httpFactory, _logger),

            EmailProvider.Smtp =>
                new SmtpEmailSender(s.EmailSmtpHost!, s.EmailSmtpPort,
                    s.EmailSmtpUsername, Use("รหัสผ่าน SMTP", s.EmailSmtpPassword), s.EmailSmtpUseSsl, _logger),

            _ => GetGlobalFallbackSender()
        };
        return problems.Count == 0 ? sender : new UnusableSecretEmailSender(s.EmailProvider, string.Join(" · ", problems), _logger);
    }

    /// <summary>รอบ 240: ที่อยู่ผู้ส่งของแพลตฟอร์ม (ผู้ดูแลตั้งในหน้า admin ก่อน แล้ว appsettings <c>Email:FromAddress</c>) · ว่าง = null
    /// (ผู้เรียกถอยไป <c>OnBehalfEmailSender.DefaultPlatformFrom</c>)</summary>
    public string? PlatformFromAddress()
    {
        try
        {
            var s = _db.SiteSettings.AsNoTracking().Select(x => x.SystemEmailFromAddress).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "อ่านที่อยู่ผู้ส่งของระบบจาก SiteSettings ไม่ได้ — ใช้ค่าจาก appsettings");
        }
        var c = _config["Email:FromAddress"];
        return string.IsNullOrWhiteSpace(c) ? null : c;
    }

    public IEmailSender GetGlobalFallbackSender()
    {
        // Try DB SiteSettings first (managed by admin via UI), fall back to appsettings.json
        try
        {
            var siteSettings = _db.SiteSettings.FirstOrDefault();
            if (siteSettings != null)
            {
                var sender = BuildSystemSender(siteSettings);
                if (sender != null) return sender;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read SystemEmail from SiteSettings; falling back to appsettings.json");
        }

        var host = _config["Email:SmtpHost"] ?? "";
        var port = int.TryParse(_config["Email:SmtpPort"], out var p) ? p : 587;
        var user = _config["Email:Username"];
        var pwd = _config["Email:Password"];
        var ssl = !bool.TryParse(_config["Email:UseSsl"], out var b) || b;
        return new SmtpEmailSender(host, port, user, pwd, ssl, _logger);
    }

    private IEmailSender? BuildSystemSender(SiteSettings s)
    {
        // รอบ 275 แพ็ก A (T4 M-5): แบบเดียวกับผู้ส่งของบริษัท — ความลับของอีเมลระบบถอดไม่ได้ ⇒ ล้มพร้อมเหตุ (ไม่ถอยไป appsettings เงียบ ๆ)
        var problems = new List<string>();
        string? Use(string label, string? stored)
        {
            var o = SecretUse.Open(_secrets, label, stored);
            if (o.Unusable) problems.Add(o.Problem!);
            return o.Value;
        }
        IEmailSender? sender = s.SystemEmailProvider switch
        {
            EmailProvider.MicrosoftGraph when AllPresent(s.SystemMsTenantId, s.SystemMsClientId,
                s.SystemMsClientSecret, s.SystemMsSenderUpn) =>
                new MicrosoftGraphEmailSender(s.SystemMsTenantId!, s.SystemMsClientId!,
                    Use("Client Secret ของ Microsoft 365 (อีเมลระบบ)", s.SystemMsClientSecret) ?? "", s.SystemMsSenderUpn!, _httpFactory, _logger),

            EmailProvider.GmailApi when AllPresent(s.SystemGmailClientId, s.SystemGmailClientSecret,
                s.SystemGmailRefreshToken, s.SystemEmailFromAddress) =>
                new GmailApiEmailSender(s.SystemGmailClientId!, Use("Client Secret ของ Gmail (อีเมลระบบ)", s.SystemGmailClientSecret) ?? "",
                    Use("Refresh Token ของ Gmail (อีเมลระบบ)", s.SystemGmailRefreshToken) ?? "", s.SystemEmailFromAddress!, _httpFactory, _logger),

            EmailProvider.Smtp when !string.IsNullOrWhiteSpace(s.SystemSmtpHost) =>
                new SmtpEmailSender(s.SystemSmtpHost!, s.SystemSmtpPort,
                    s.SystemSmtpUsername, Use("รหัสผ่าน SMTP ของระบบ", s.SystemSmtpPassword), s.SystemSmtpUseSsl, _logger),

            _ => null
        };
        return sender == null || problems.Count == 0 ? sender : new UnusableSecretEmailSender(s.SystemEmailProvider, string.Join(" · ", problems), _logger);
    }

    private static bool AllPresent(params string?[] values) =>
        values.All(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>รอบ 275 แพ็ก A (T4 M-5): ผู้ส่งเมื่อความลับของผู้ให้บริการอีเมลที่ตั้งไว้ถอดไม่ได้ — ไม่ติดต่อผู้ให้บริการ · ตอบล้มพร้อมเหตุจริงทุกครั้ง
/// (ผู้เรียกบันทึกลงประวัติการส่ง/ตอบผู้ใช้) + LogError ครั้งต่อการส่ง</summary>
public sealed class UnusableSecretEmailSender : IEmailSender
{
    private readonly string _problem;
    private readonly ILogger _logger;

    public UnusableSecretEmailSender(EmailProvider provider, string problem, ILogger logger)
    { Provider = provider; _problem = problem; _logger = logger; }

    public EmailProvider Provider { get; }

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        _logger.LogError("ไม่ส่งอีเมล ({Provider}): {Problem}", Provider, _problem);
        return Task.FromResult(EmailSendResult.Fail(_problem));
    }
}
