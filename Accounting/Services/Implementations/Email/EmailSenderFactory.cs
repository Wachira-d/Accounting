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
        return s.EmailProvider switch
        {
            EmailProvider.MicrosoftGraph =>
                new MicrosoftGraphEmailSender(s.EmailMsTenantId!, s.EmailMsClientId!,
                    _secrets.Unprotect(s.EmailMsClientSecret)!, s.EmailMsSenderUpn!, _httpFactory, _logger),

            EmailProvider.GmailApi =>
                new GmailApiEmailSender(s.EmailGmailClientId!, _secrets.Unprotect(s.EmailGmailClientSecret)!,
                    _secrets.Unprotect(s.EmailGmailRefreshToken)!, s.EmailFromAddress!, _httpFactory, _logger),

            EmailProvider.Smtp =>
                new SmtpEmailSender(s.EmailSmtpHost!, s.EmailSmtpPort,
                    s.EmailSmtpUsername, _secrets.Unprotect(s.EmailSmtpPassword), s.EmailSmtpUseSsl, _logger),

            _ => GetGlobalFallbackSender()
        };
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
        return s.SystemEmailProvider switch
        {
            EmailProvider.MicrosoftGraph when AllPresent(s.SystemMsTenantId, s.SystemMsClientId,
                s.SystemMsClientSecret, s.SystemMsSenderUpn) =>
                new MicrosoftGraphEmailSender(s.SystemMsTenantId!, s.SystemMsClientId!,
                    _secrets.Unprotect(s.SystemMsClientSecret)!, s.SystemMsSenderUpn!, _httpFactory, _logger),

            EmailProvider.GmailApi when AllPresent(s.SystemGmailClientId, s.SystemGmailClientSecret,
                s.SystemGmailRefreshToken, s.SystemEmailFromAddress) =>
                new GmailApiEmailSender(s.SystemGmailClientId!, _secrets.Unprotect(s.SystemGmailClientSecret)!,
                    _secrets.Unprotect(s.SystemGmailRefreshToken)!, s.SystemEmailFromAddress!, _httpFactory, _logger),

            EmailProvider.Smtp when !string.IsNullOrWhiteSpace(s.SystemSmtpHost) =>
                new SmtpEmailSender(s.SystemSmtpHost!, s.SystemSmtpPort,
                    s.SystemSmtpUsername, _secrets.Unprotect(s.SystemSmtpPassword), s.SystemSmtpUseSsl, _logger),

            _ => null
        };
    }

    private static bool AllPresent(params string?[] values) =>
        values.All(v => !string.IsNullOrWhiteSpace(v));
}
