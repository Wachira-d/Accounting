using System.Net;
using System.Net.Mail;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;
    private readonly IErrorLogService _errorLogService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecretProtector _secrets;

    public EmailService(IConfiguration config, ILogger<EmailService> logger,
        IErrorLogService errorLogService, IServiceScopeFactory scopeFactory,
        ISecretProtector secrets)
    {
        _config = config;
        _logger = logger;
        _errorLogService = errorLogService;
        _scopeFactory = scopeFactory;
        _secrets = secrets;
    }

    /// <summary>
    /// Resolve the active system email config: DB SiteSettings first (admin UI), fall back to appsettings.json
    /// </summary>
    private async Task<SystemEmailConfig> ResolveConfigAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var s = await db.SiteSettings.FirstOrDefaultAsync();
            if (s != null && !string.IsNullOrWhiteSpace(s.SystemSmtpHost))
            {
                return new SystemEmailConfig
                {
                    Provider = s.SystemEmailProvider,
                    SmtpHost = s.SystemSmtpHost,
                    SmtpPort = s.SystemSmtpPort,
                    SmtpUsername = s.SystemSmtpUsername,
                    SmtpPassword = _secrets.Unprotect(s.SystemSmtpPassword),
                    SmtpUseSsl = s.SystemSmtpUseSsl,
                    FromAddress = s.SystemEmailFromAddress ?? "noreply@nextacc.com",
                    FromName = s.SystemEmailFromName ?? s.SiteName ?? "Next Acc",
                    ReplyTo = s.SystemEmailReplyTo,
                    BaseUrl = s.AppBaseUrl ?? _config["App:BaseUrl"] ?? "https://app.nextacc.com",
                    Source = "Database"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read SystemEmail from SiteSettings; falling back to appsettings.json");
        }

        return new SystemEmailConfig
        {
            Provider = EmailProvider.Smtp,
            SmtpHost = _config["Email:SmtpHost"],
            SmtpPort = int.TryParse(_config["Email:SmtpPort"], out var p) ? p : 587,
            SmtpUsername = _config["Email:Username"],
            SmtpPassword = _config["Email:Password"],
            SmtpUseSsl = !bool.TryParse(_config["Email:UseSsl"], out var ssl) || ssl,
            FromAddress = _config["Email:FromAddress"] ?? "noreply@nextacc.com",
            FromName = _config["Email:FromName"] ?? "Next Acc",
            ReplyTo = _config["Email:ReplyTo"],
            BaseUrl = _config["App:BaseUrl"] ?? "https://app.nextacc.com",
            Source = "Configuration"
        };
    }

    public async Task<bool> IsSystemEmailConfiguredAsync()
    {
        var cfg = await ResolveConfigAsync();
        return !string.IsNullOrWhiteSpace(cfg.SmtpHost);
    }

    public async Task SendAsync(string to, string subject, string htmlBody)
    {
        var cfg = await ResolveConfigAsync();
        if (string.IsNullOrEmpty(cfg.SmtpHost))
        {
            _logger.LogWarning("System email not configured. Skipping send to {To}: {Subject}", to, subject);
            return;
        }
        await SendInternalAsync(cfg, to, subject, htmlBody, null, null);
    }

    private async Task SendInternalAsync(SystemEmailConfig cfg, string to, string subject,
        string htmlBody, byte[]? attachmentData, string? attachmentName)
    {
        try
        {
            using var client = new SmtpClient(cfg.SmtpHost!)
            {
                Port = cfg.SmtpPort,
                Credentials = string.IsNullOrEmpty(cfg.SmtpUsername)
                    ? CredentialCache.DefaultNetworkCredentials
                    : new NetworkCredential(cfg.SmtpUsername, cfg.SmtpPassword),
                EnableSsl = cfg.SmtpUseSsl
            };

            var msg = new MailMessage
            {
                From = new MailAddress(cfg.FromAddress!, cfg.FromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            msg.To.Add(to);
            if (!string.IsNullOrWhiteSpace(cfg.ReplyTo))
                msg.ReplyToList.Add(new MailAddress(cfg.ReplyTo));
            if (attachmentData != null && attachmentName != null)
                msg.Attachments.Add(new Attachment(new MemoryStream(attachmentData), attachmentName, "application/pdf"));

            await client.SendMailAsync(msg);
            _logger.LogInformation("System email sent ({Source}) to {To}: {Subject}", cfg.Source, to, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send system email to {To}: {Subject}", to, subject);
            await _errorLogService.LogErrorAsync(ex, $"EmailService.Send/{to}");
        }
    }

    public async Task SendPasswordResetAsync(string to, string fullName, string resetToken)
    {
        var cfg = await ResolveConfigAsync();
        var resetLink = $"{cfg.BaseUrl}/reset-password.html?token={Uri.EscapeDataString(resetToken)}";
        var html = $@"
            <div style='font-family:sans-serif;max-width:600px;margin:0 auto'>
                <h2 style='color:#4F46E5'>{cfg.FromName} - รีเซ็ตรหัสผ่าน</h2>
                <p>สวัสดี {fullName},</p>
                <p>เราได้รับคำขอรีเซ็ตรหัสผ่านของคุณ กรุณาคลิกปุ่มด้านล่างเพื่อตั้งรหัสผ่านใหม่:</p>
                <p style='text-align:center;margin:32px 0'>
                    <a href='{resetLink}' style='background:#4F46E5;color:#fff;padding:12px 32px;border-radius:8px;text-decoration:none;font-weight:600'>รีเซ็ตรหัสผ่าน</a>
                </p>
                <p style='color:#666;font-size:14px'>ลิงก์นี้จะหมดอายุภายใน 1 ชั่วโมง หากคุณไม่ได้ขอรีเซ็ตรหัสผ่าน กรุณาเพิกเฉยอีเมลนี้</p>
            </div>";
        await SendAsync(to, $"รีเซ็ตรหัสผ่าน - {cfg.FromName}", html);
    }

    public async Task SendDunningLetterAsync(string to, string contactName, string letterContent)
    {
        var cfg = await ResolveConfigAsync();
        var html = $@"
            <div style='font-family:sans-serif;max-width:600px;margin:0 auto'>
                <h2 style='color:#4F46E5'>{cfg.FromName} - แจ้งเตือนยอดค้างชำระ</h2>
                <p>เรียน {contactName},</p>
                <div style='background:#f9fafb;padding:16px;border-radius:8px;margin:16px 0'>{letterContent}</div>
                <p style='color:#666;font-size:14px'>กรุณาดำเนินการชำระเงินโดยเร็ว หากชำระแล้วกรุณาเพิกเฉยอีเมลนี้</p>
            </div>";
        await SendAsync(to, $"แจ้งเตือนยอดค้างชำระ - {contactName}", html);
    }

    public async Task SendPaymentReminderAsync(string to, string contactName, string documentNumber, decimal amount, DateTime dueDate)
    {
        var cfg = await ResolveConfigAsync();
        var html = $@"
            <div style='font-family:sans-serif;max-width:600px;margin:0 auto'>
                <h2 style='color:#4F46E5'>{cfg.FromName} - เตือนกำหนดชำระ</h2>
                <p>เรียน {contactName},</p>
                <p>ใบแจ้งหนี้เลขที่ <strong>{documentNumber}</strong> จำนวน <strong>{amount:N2} บาท</strong> จะครบกำหนดชำระในวันที่ <strong>{dueDate:dd/MM/yyyy}</strong></p>
                <p>กรุณาดำเนินการชำระเงินตามกำหนด ขอบคุณครับ/ค่ะ</p>
            </div>";
        await SendAsync(to, $"เตือนกำหนดชำระ - {documentNumber}", html);
    }

    public async Task SendNotificationEmailAsync(string to, string fullName, string title, string message, string? actionUrl = null)
    {
        var actionButton = actionUrl != null
            ? $"<p style='text-align:center;margin:24px 0'><a href='{actionUrl}' style='background:#4F46E5;color:#fff;padding:10px 24px;border-radius:8px;text-decoration:none'>ดูรายละเอียด</a></p>"
            : "";
        var html = $@"
            <div style='font-family:sans-serif;max-width:600px;margin:0 auto'>
                <h2 style='color:#4F46E5'>{title}</h2>
                <p>สวัสดี {fullName},</p>
                <p>{message}</p>
                {actionButton}
            </div>";
        await SendAsync(to, title, html);
    }

    public async Task SendPayslipAsync(string to, string employeeName, string payrollPeriod, byte[] pdfAttachment)
    {
        var cfg = await ResolveConfigAsync();
        if (string.IsNullOrEmpty(cfg.SmtpHost))
        {
            _logger.LogWarning("System email not configured. Skipping payslip to {To}", to);
            return;
        }

        var subject = $"สลิปเงินเดือน {payrollPeriod} - {cfg.FromName}";
        var body = $"<p>สวัสดี {employeeName},</p><p>แนบสลิปเงินเดือนสำหรับงวด {payrollPeriod}</p>";
        await SendInternalAsync(cfg, to, subject, body, pdfAttachment, $"payslip-{payrollPeriod}.pdf");
    }

    public async Task SendInvitationAsync(string to, string inviteeName, string inviterName,
        string companyName, string invitationToken, string role)
    {
        var cfg = await ResolveConfigAsync();
        var acceptLink = $"{cfg.BaseUrl}/accept-invitation.html?token={Uri.EscapeDataString(invitationToken)}";
        var html = $@"
            <div style='font-family:sans-serif;max-width:600px;margin:0 auto'>
                <h2 style='color:#4F46E5'>{cfg.FromName} - คำเชิญเข้าร่วมงาน</h2>
                <p>สวัสดี {inviteeName},</p>
                <p><strong>{inviterName}</strong> จากบริษัท <strong>{companyName}</strong> ได้เชิญคุณเข้าร่วมเป็น <strong>{role}</strong></p>
                <p>กดปุ่มด้านล่างเพื่อตอบรับคำเชิญและสร้างบัญชีของคุณ:</p>
                <p style='text-align:center;margin:32px 0'>
                    <a href='{acceptLink}' style='background:#4F46E5;color:#fff;padding:12px 32px;border-radius:8px;text-decoration:none;font-weight:600'>ตอบรับคำเชิญ</a>
                </p>
                <p style='color:#666;font-size:14px'>ลิงก์นี้จะหมดอายุภายใน 7 วัน</p>
                <hr style='border:none;border-top:1px solid #eee;margin:24px 0' />
                <p style='color:#999;font-size:12px'>หากปุ่มไม่ทำงาน คัดลอกลิงก์นี้ไปวางในเบราว์เซอร์: <br/>{acceptLink}</p>
            </div>";
        await SendAsync(to, $"คำเชิญเข้าร่วม {companyName} - {cfg.FromName}", html);
    }

    private class SystemEmailConfig
    {
        public EmailProvider Provider { get; set; }
        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 587;
        public string? SmtpUsername { get; set; }
        public string? SmtpPassword { get; set; }
        public bool SmtpUseSsl { get; set; } = true;
        public string? FromAddress { get; set; }
        public string? FromName { get; set; }
        public string? ReplyTo { get; set; }
        public string BaseUrl { get; set; } = "";
        public string Source { get; set; } = "";
    }
}
