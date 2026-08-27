using System.Net;
using System.Net.Mail;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations.Email;

/// <summary>
/// Generic SMTP sender — works with any SMTP provider including:
/// - Gmail SMTP (smtp.gmail.com:587, requires App Password)
/// - Office 365 SMTP (smtp.office365.com:587)
/// - Yahoo, Zoho, custom SMTP servers
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    public EmailProvider Provider => EmailProvider.Smtp;

    private readonly string _host;
    private readonly int _port;
    private readonly string? _username;
    private readonly string? _password;
    private readonly bool _useSsl;
    private readonly ILogger? _logger;

    public SmtpEmailSender(string host, int port, string? username, string? password, bool useSsl, ILogger? logger = null)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
        _useSsl = useSsl;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_host))
            return EmailSendResult.Fail("SMTP host not configured");
        if (message.To.Count == 0)
            return EmailSendResult.Fail("No recipients specified");

        try
        {
            using var client = new SmtpClient(_host)
            {
                Port = _port,
                EnableSsl = _useSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = string.IsNullOrEmpty(_username)
                    ? null
                    : new NetworkCredential(_username, _password)
            };

            using var msg = new MailMessage
            {
                From = new MailAddress(message.FromAddress, message.FromName ?? message.FromAddress),
                Subject = message.Subject,
                Body = message.HtmlBody,
                IsBodyHtml = true,
                BodyEncoding = System.Text.Encoding.UTF8,
                SubjectEncoding = System.Text.Encoding.UTF8
            };

            if (!string.IsNullOrWhiteSpace(message.ReplyTo))
                msg.ReplyToList.Add(new MailAddress(message.ReplyTo));

            foreach (var t in message.To) msg.To.Add(t);
            foreach (var c in message.Cc) msg.CC.Add(c);
            foreach (var b in message.Bcc) msg.Bcc.Add(b);

            foreach (var att in message.Attachments)
            {
                var stream = new MemoryStream(att.Content);
                msg.Attachments.Add(new Attachment(stream, att.FileName, att.ContentType));
            }

            await client.SendMailAsync(msg, ct);
            return EmailSendResult.Ok();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SMTP send failed to {Host}:{Port}", _host, _port);
            // มี username แต่ไม่มี password = ตั้งค่าไม่ครบ หรือรหัสที่เก็บไว้ถอดรหัส
            // ไม่ออก (คีย์เข้ารหัสถูกเปลี่ยน) — ปลายทางจะตอบเป็น "Authentication
            // Required" ซึ่งไม่ได้ชี้ต้นเหตุเลย ผู้ใช้ไล่เองไม่ถูกว่าพลาดตรงไหน
            if (!string.IsNullOrEmpty(_username) && string.IsNullOrEmpty(_password))
                return EmailSendResult.Fail(
                    "ระบบไม่มีรหัสผ่าน SMTP ที่ใช้งานได้ (ช่องรหัสผ่านว่าง หรือรหัสที่บันทึกไว้ "
                    + "ถอดรหัสไม่ออกเพราะคีย์เข้ารหัสของระบบถูกเปลี่ยน) — โปรดกรอกรหัสผ่าน/"
                    + $"App Password ใหม่แล้วบันทึกอีกครั้ง · ข้อความจากเซิร์ฟเวอร์: {ex.Message}");
            return EmailSendResult.Fail(ex.Message);
        }
    }
}
