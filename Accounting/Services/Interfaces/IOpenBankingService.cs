using Accounting.Models.DTOs.OpenBanking;

namespace Accounting.Services.Interfaces;

public interface IOpenBankingService
{
    // Connections
    Task<BankConnectionResponse> CreateConnectionAsync(Guid companyId, CreateBankConnectionRequest request);
    Task<List<BankConnectionResponse>> GetConnectionsAsync(Guid companyId);
    Task<BankConnectionResponse> UpdateConnectionAsync(Guid companyId, Guid connectionId, UpdateBankConnectionRequest request);
    Task DeleteConnectionAsync(Guid companyId, Guid connectionId);

    // Sync
    Task<BankFeedImportResponse> SyncTransactionsAsync(Guid companyId, Guid connectionId, DateTime? fromDate = null, DateTime? toDate = null);
    Task<List<BankFeedImportResponse>> GetImportHistoryAsync(Guid companyId, Guid connectionId);
    Task ProcessAutoSyncAsync();

    // File import (OFX, QIF, CSV)
    Task<BankFeedImportResponse> ImportFileAsync(Guid companyId, Guid bankAccountId, string fileFormat, string base64Content);
}
