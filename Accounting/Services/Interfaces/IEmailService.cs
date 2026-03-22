namespace Accounting.Services.Interfaces;

public interface IEmailService
{
    Task SendAsync(string to, string subject, string htmlBody);
    Task SendPasswordResetAsync(string to, string fullName, string resetToken);
    Task SendDunningLetterAsync(string to, string contactName, string letterContent);
    Task SendPaymentReminderAsync(string to, string contactName, string documentNumber, decimal amount, DateTime dueDate);
    Task SendNotificationEmailAsync(string to, string fullName, string title, string message, string? actionUrl = null);
    Task SendPayslipAsync(string to, string employeeName, string payrollPeriod, byte[] pdfAttachment);
}
