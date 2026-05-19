using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

/// <summary>Data migration wizard for ERP onboarding — see service impl.</summary>
public interface IMigrationWizardService
{
    Task<MigrationSession> CreateSessionAsync(Guid companyId, string name, MigrationType type, Guid? targetPeriodId, string userId);
    Task UploadLegacyAccountsAsync(Guid companyId, Guid sessionId, List<(string Code, string? Name, decimal Debit, decimal Credit)> rows, string userId);
    Task ApplyMappingsAsync(Guid companyId, Guid sessionId, Dictionary<string, Guid> codeToAccountId, string userId);
    Task<List<string>> ValidateSessionAsync(Guid companyId, Guid sessionId);
    Task<MigrationSession> CommitAsync(Guid companyId, Guid sessionId, string userId);
    Task<List<MigrationSession>> ListSessionsAsync(Guid companyId);
    Task<MigrationSession> GetSessionAsync(Guid companyId, Guid sessionId);
    Task<List<AccountMapping>> GetMappingsAsync(Guid companyId, Guid sessionId);
}
