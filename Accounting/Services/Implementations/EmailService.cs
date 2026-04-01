using System.Net;
using System.Net.Mail;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;
    private readonly IErrorLogService _errorLogService;

    public EmailService(IConfiguration config, ILogger<EmailService> logger, IErrorLogService errorLogService)
    {
        _config = config;
        _logger = logger;
        _errorLogService = errorLogService;
    }

    public async Task SendAsync(string to, string subject, string htmlBody)
    {
        var smtpHost = _config["Email:SmtpHost"];
        if (string.IsNullOrEmpty(smtpHost))
        {
            _logger.LogWarning("Email not configured (Email:SmtpHost is empty). Skipping send to {To}: {Subject}", to, subject);
            return;
        }

        try
        {
            using var client = new SmtpClient(smtpHost)
            {
                Port = int.Parse(_config["Email:SmtpPort"] ?? "587"),
                Credentials = new NetworkCredential(
                    _config["Email:Username"],
                    _config["Email:Password"]),
                EnableSsl = bool.Parse(_config["Email:UseSsl"] ?? "true")
            };

            var fromEmail = _config["Email:FromAddress"] ?? "noreply@nextacc.com";
            var fromName = _config["Email:FromName"] ?? "Next Acc";

            var msg = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            msg.To.Add(to);

            await client.SendMailAsync(msg);
            _logger.LogInformation("Email sent to {To}: {Subject}", to, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}: {Subject}", to, subject);
            await _errorLogService.LogErrorAsync(ex, $"EmailService.Send/{to}");
        }
    }

    public async Task SendPasswordResetAsync(string to, string fullName, string resetToken)
    {
        var baseUrl = _config["App:BaseUrl"] ?? "https://app.nextacc.com";
        var resetLink = $"{baseUrl}/reset-password.html?token={Uri.EscapeDataString(resetToken)}";
        var html = $@"
            <div style='font-family:sans-serif;max-width:600px;margin:0 auto'>
                <h2 style='color:#4F46E5'>Next Acc - รีเซ็ตรหัสผ่าน</h2>
                <p>สวัสดี {fullName},</p>
                <p>เราได้รับคำขอรีเซ็ตรหัสผ่านของคุณ กรุณาคลิกปุ่มด้านล่างเพื่อตั้งรหัสผ่านใหม่:</p>
                <p style='text-align:center;margin:32px 0'>
                    <a href='{resetLink}' style='background:#4F46E5;color:#fff;padding:12px 32px;border-radius:8px;text-decoration:none;font-weight:600'>รีเซ็ตรหัสผ่าน</a>
                </p>
                <p style='color:#666;font-size:14px'>ลิงก์นี้จะหมดอายุภายใน 1 ชั่วโมง หากคุณไม่ได้ขอรีเซ็ตรหัสผ่าน กรุณาเพิกเฉยอีเมลนี้</p>
            </div>";
        await SendAsync(to, "รีเซ็ตรหัสผ่าน - Next Acc", html);
    }

    public async Task SendDunningLetterAsync(string to, string contactName, string letterContent)
    {
        var html = $@"
            <div style='font-family:sans-serif;max-width:600px;margin:0 auto'>
                <h2 style='color:#4F46E5'>Next Acc - แจ้งเตือนยอดค้างชำระ</h2>
                <p>เรียน {contactName},</p>
                <div style='background:#f9fafb;padding:16px;border-radius:8px;margin:16px 0'>{letterContent}</div>
                <p style='color:#666;font-size:14px'>กรุณาดำเนินการชำระเงินโดยเร็ว หากชำระแล้วกรุณาเพิกเฉยอีเมลนี้</p>
            </div>";
        await SendAsync(to, $"แจ้งเตือนยอดค้างชำระ - {contactName}", html);
    }

    public async Task SendPaymentReminderAsync(string to, string contactName, string documentNumber, decimal amount, DateTime dueDate)
    {
        var html = $@"
            <div style='font-family:sans-serif;max-width:600px;margin:0 auto'>
                <h2 style='color:#4F46E5'>Next Acc - เตือนกำหนดชำระ</h2>
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
        var smtpHost = _config["Email:SmtpHost"];
        if (string.IsNullOrEmpty(smtpHost))
        {
            _logger.LogWarning("Email not configured. Skipping payslip to {To}", to);
            return;
        }

        try
        {
            using var client = new SmtpClient(smtpHost)
            {
                Port = int.Parse(_config["Email:SmtpPort"] ?? "587"),
                Credentials = new NetworkCredential(_config["Email:Username"], _config["Email:Password"]),
                EnableSsl = bool.Parse(_config["Email:UseSsl"] ?? "true")
            };

            var fromEmail = _config["Email:FromAddress"] ?? "noreply@nextacc.com";
            var msg = new MailMessage
            {
                From = new MailAddress(fromEmail, _config["Email:FromName"] ?? "Next Acc"),
                Subject = $"สลิปเงินเดือน {payrollPeriod} - Next Acc",
                Body = $"<p>สวัสดี {employeeName},</p><p>แนบสลิปเงินเดือนสำหรับงวด {payrollPeriod}</p>",
                IsBodyHtml = true
            };
            msg.To.Add(to);
            msg.Attachments.Add(new Attachment(new MemoryStream(pdfAttachment), $"payslip-{payrollPeriod}.pdf", "application/pdf"));

            await client.SendMailAsync(msg);
            _logger.LogInformation("Payslip sent to {To} for {Period}", to, payrollPeriod);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send payslip to {To}", to);
            await _errorLogService.LogErrorAsync(ex, $"EmailService.SendPayslip/{to}");
        }
    }
}
