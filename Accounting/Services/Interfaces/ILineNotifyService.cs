namespace Accounting.Services.Interfaces;

public interface ILineNotifyService
{
    Task SendMessageAsync(string message);

    /// <summary>Push a personalised text message to a specific LINE user
    /// (the "U..." userId from LINE Login / Messaging API webhook).
    /// Used by the centralised NotificationEngine; no-op when LINE is
    /// not configured or the userId is empty.</summary>
    Task PushToUserAsync(string lineUserId, string message);

    /// <summary>Push a LINE Flex Message bubble to a specific user.
    /// <paramref name="flexContents"/> is the "contents" payload LINE
    /// expects (the bubble shape with header / body / footer / actions
    /// — caller builds it). <paramref name="altText"/> is the fallback
    /// shown in chat lists / push previews.</summary>
    Task PushFlexToUserAsync(string lineUserId, string altText, object flexContents);

    Task NotifyDocumentApprovedAsync(Guid companyId, string documentNumber, string contactName, decimal amount);
    Task NotifyPaymentReceivedAsync(Guid companyId, string documentNumber, decimal amount);
    Task NotifyOverdueInvoiceAsync(Guid companyId, string documentNumber, string contactName, decimal amount, int daysOverdue);
    Task NotifyBankSyncCompleteAsync(Guid companyId, string bankName, int newTransactions);
    Task NotifyECommerceSyncAsync(Guid companyId, string platform, int newOrders, decimal totalAmount);
    Task NotifyPayrollCompletedAsync(Guid companyId, string runName, int employeeCount, decimal totalNet);
    Task NotifyLowBalanceAsync(Guid companyId, string accountName, decimal balance, decimal threshold);
}
