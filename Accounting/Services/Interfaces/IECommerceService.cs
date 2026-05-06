namespace Accounting.Services.Interfaces;

public interface IECommerceService
{
    Task<ECommerceConnectionResponse> ConnectAsync(Guid companyId, ConnectECommerceRequest request);
    Task<List<ECommerceConnectionResponse>> GetConnectionsAsync(Guid companyId);
    Task<ECommerceSyncResult> SyncOrdersAsync(Guid companyId, Guid connectionId, DateTime? since = null);
    Task<ECommerceSyncResult> SyncAllAsync(Guid companyId);
    Task DisconnectAsync(Guid companyId, Guid connectionId);
    Task<ECommerceConnectionResponse> TestConnectionAsync(Guid companyId, Guid connectionId);
}

public record ConnectECommerceRequest(
    string Platform,
    string ShopName,
    string? ApiKey,
    string? ApiSecret,
    string? AccessToken,
    string? RefreshToken,
    string? ShopId,
    bool AutoSync = true,
    bool AutoCreateInvoice = true);

public record ECommerceConnectionResponse(
    Guid Id,
    string Platform,
    string ShopName,
    string Status,
    DateTime? LastSyncAt,
    int TotalOrdersSynced,
    int TotalRevenue,
    bool AutoSync,
    bool AutoCreateInvoice);

public record ECommerceSyncResult(
    Guid ConnectionId,
    string Platform,
    string ShopName,
    int OrdersFetched,
    int NewOrders,
    int InvoicesCreated,
    decimal TotalAmount,
    string Status,
    string? ErrorMessage);
