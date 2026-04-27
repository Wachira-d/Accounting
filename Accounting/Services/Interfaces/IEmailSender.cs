using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

/// <summary>
/// Low-level email sender. Each provider (SMTP, Microsoft Graph, Gmail API)
/// has its own implementation. EmailSenderFactory builds the right one
/// for a given CompanySettings.
/// </summary>
public interface IEmailSender
{
    EmailProvider Provider { get; }
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct = default);
}

public class EmailMessage
{
    public string FromAddress { get; set; } = "";
    public string? FromName { get; set; }
    public string? ReplyTo { get; set; }
    public List<string> To { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    public List<string> Bcc { get; set; } = new();
    public string Subject { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public string? PlainTextBody { get; set; }
    public List<EmailAttachment> Attachments { get; set; } = new();
}

public class EmailAttachment
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

public class EmailSendResult
{
    public bool Success { get; set; }
    public string? MessageId { get; set; }
    public string? ErrorMessage { get; set; }

    public static EmailSendResult Ok(string? messageId = null) => new() { Success = true, MessageId = messageId };
    public static EmailSendResult Fail(string error) => new() { Success = false, ErrorMessage = error };
}

/// <summary>
/// Picks the right IEmailSender for a company based on its CompanySettings.
/// Falls back to global SMTP config if per-company settings are not configured.
/// </summary>
public interface IEmailSenderFactory
{
    Task<IEmailSender> GetSenderAsync(Guid companyId, CancellationToken ct = default);
    IEmailSender GetSenderForSettings(CompanySettings settings);
    IEmailSender GetGlobalFallbackSender();
}
