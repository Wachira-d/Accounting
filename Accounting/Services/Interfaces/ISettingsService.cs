using Accounting.Models.DTOs.Settings;

namespace Accounting.Services.Interfaces;

public interface ISettingsService
{
    // Company Settings
    Task<CompanySettingsResponse> GetSettingsAsync(Guid companyId);
    Task<CompanySettingsResponse> UpdateSettingsAsync(Guid companyId, UpdateCompanySettingsRequest request);

    // Number Series
    Task<NumberSeriesResponse> CreateNumberSeriesAsync(Guid companyId, CreateNumberSeriesRequest request);
    Task<List<NumberSeriesResponse>> GetNumberSeriesAsync(Guid companyId);
    Task<NumberSeriesResponse> UpdateNumberSeriesAsync(Guid companyId, Guid seriesId, UpdateNumberSeriesRequest request);
    Task<string> GetNextNumberAsync(Guid companyId, Accounting.Models.Enums.DocumentType documentType);

    // API Key Management
    Task<ApiKeyCreatedResponse> CreateApiKeyAsync(Guid companyId, Guid userId, CreateApiKeyRequest request);
    Task<List<ApiKeyResponse>> GetApiKeysAsync(Guid companyId);
    Task RevokeApiKeyAsync(Guid companyId, Guid apiKeyId);
}
