namespace Accounting.Services.Interfaces;

public interface ILineNotifyService
{
    Task SendMessageAsync(string message);
    Task NotifyDocumentApprovedAsync(Guid companyId, string documentNumber, string contactName, decimal amount);
    Task NotifyPaymentReceivedAsync(Guid companyId, string documentNumber, decimal amount);
    Task NotifyOverdueInvoiceAsync(Guid companyId, string documentNumber, string contactName, decimal amount, int daysOverdue);
    Task NotifyBankSyncCompleteAsync(Guid companyId, string bankName, int newTransactions);
    Task NotifyECommerceSyncAsync(Guid companyId, string platform, int newOrders, decimal totalAmount);
    Task NotifyPayrollCompletedAsync(Guid companyId, string runName, int employeeCount, decimal totalNet);
    Task NotifyLowBalanceAsync(Guid companyId, string accountName, decimal balance, decimal threshold);
}
