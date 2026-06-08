namespace Accounting.Services.Interfaces;

public interface IEmailService
{
    Task SendAsync(string to, string subject, string htmlBody);
    Task SendPasswordResetAsync(string to, string fullName, string resetToken);
    Task SendDunningLetterAsync(string to, string contactName, string letterContent);
    Task SendPaymentReminderAsync(string to, string contactName, string documentNumber, decimal amount, DateTime dueDate);
    Task SendNotificationEmailAsync(string to, string fullName, string title, string message, string? actionUrl = null);
    Task SendPayslipAsync(string to, string employeeName, string payrollPeriod, byte[] pdfAttachment);
    Task SendInvitationAsync(string to, string inviteeName, string inviterName,
        string companyName, string invitationToken, string role);

    /// <summary>True when a system SMTP host is configured (DB SiteSettings or
    /// appsettings). Lets callers tell "email actually sent" from "silently
    /// skipped because email isn't set up" so the UI can fall back to showing
    /// a copy-able invite link instead of falsely claiming the mail was sent.</summary>
    Task<bool> IsSystemEmailConfiguredAsync();
}
