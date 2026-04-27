using Accounting.Data;
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

    public EmailSenderFactory(AccountingDbContext db, IConfiguration config,
        IHttpClientFactory httpFactory, ILogger<EmailSenderFactory> logger)
    {
        _db = db;
        _config = config;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IEmailSender> GetSenderAsync(Guid companyId, CancellationToken ct = default)
    {
        var settings = await _db.Set<CompanySettings>()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        return settings == null ? GetGlobalFallbackSender() : GetSenderForSettings(settings);
    }

    public IEmailSender GetSenderForSettings(CompanySettings s)
    {
        return s.EmailProvider switch
        {
            EmailProvider.MicrosoftGraph when AllPresent(s.EmailMsTenantId, s.EmailMsClientId,
                s.EmailMsClientSecret, s.EmailMsSenderUpn) =>
                new MicrosoftGraphEmailSender(s.EmailMsTenantId!, s.EmailMsClientId!,
                    s.EmailMsClientSecret!, s.EmailMsSenderUpn!, _httpFactory, _logger),

            EmailProvider.GmailApi when AllPresent(s.EmailGmailClientId, s.EmailGmailClientSecret,
                s.EmailGmailRefreshToken, s.EmailFromAddress) =>
                new GmailApiEmailSender(s.EmailGmailClientId!, s.EmailGmailClientSecret!,
                    s.EmailGmailRefreshToken!, s.EmailFromAddress!, _httpFactory, _logger),

            EmailProvider.Smtp when !string.IsNullOrWhiteSpace(s.EmailSmtpHost) =>
                new SmtpEmailSender(s.EmailSmtpHost!, s.EmailSmtpPort,
                    s.EmailSmtpUsername, s.EmailSmtpPassword, s.EmailSmtpUseSsl, _logger),

            _ => GetGlobalFallbackSender()
        };
    }

    public IEmailSender GetGlobalFallbackSender()
    {
        var host = _config["Email:SmtpHost"] ?? "";
        var port = int.TryParse(_config["Email:SmtpPort"], out var p) ? p : 587;
        var user = _config["Email:Username"];
        var pwd = _config["Email:Password"];
        var ssl = !bool.TryParse(_config["Email:UseSsl"], out var b) || b;
        return new SmtpEmailSender(host, port, user, pwd, ssl, _logger);
    }

    private static bool AllPresent(params string?[] values) =>
        values.All(v => !string.IsNullOrWhiteSpace(v));
}
