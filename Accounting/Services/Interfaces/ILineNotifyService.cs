namespace Accounting.Services.Interfaces;

public interface ILineNotifyService
{
    Task SendMessageAsync(string message);

    /// <summary>Push a personalised text message to a specific LINE user
    /// (the "U..." userId from LINE Login / Messaging API webhook).
    /// Used by the centralised NotificationEngine; no-op when LINE is
    /// not configured or the userId is empty.</summary>
    Task PushToUserAsync(string lineUserId, string message);

    /// <summary>Company-aware push — resolves Channel Access Token from
    /// CompanySettings.LineChannelAccessToken (falls back to global
    /// appsettings if the company didn't configure their own).</summary>
    Task PushToUserAsync(Guid? companyId, string lineUserId, string message);

    /// <summary>Push a LINE Flex Message bubble to a specific user.
    /// <paramref name="flexContents"/> is the "contents" payload LINE
    /// expects (the bubble shape with header / body / footer / actions
    /// — caller builds it). <paramref name="altText"/> is the fallback
    /// shown in chat lists / push previews.</summary>
    Task PushFlexToUserAsync(string lineUserId, string altText, object flexContents);
    Task PushFlexToUserAsync(Guid? companyId, string lineUserId, string altText, object flexContents);

    /// <summary>Send a test message using the per-company config to verify
    /// the token + destination ID work. Returns (success, message) suitable
    /// for echoing to the admin UI.</summary>
    Task<(bool ok, string message)> TestConfigAsync(Guid companyId, string toLineId);

    Task NotifyDocumentApprovedAsync(Guid companyId, string documentNumber, string contactName, decimal amount);
    Task NotifyPaymentReceivedAsync(Guid companyId, string documentNumber, decimal amount);
    Task NotifyOverdueInvoiceAsync(Guid companyId, string documentNumber, string contactName, decimal amount, int daysOverdue);
    Task NotifyBankSyncCompleteAsync(Guid companyId, string bankName, int newTransactions);
    Task NotifyECommerceSyncAsync(Guid companyId, string platform, int newOrders, decimal totalAmount);
    Task NotifyPayrollCompletedAsync(Guid companyId, string runName, int employeeCount, decimal totalNet);
    Task NotifyLowBalanceAsync(Guid companyId, string accountName, decimal balance, decimal threshold);
}
