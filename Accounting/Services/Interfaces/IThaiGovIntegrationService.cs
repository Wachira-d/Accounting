namespace Accounting.Services.Interfaces;

/// <summary>
/// รวมบริการภาครัฐไทยที่เชื่อมต่อได้ — ครอบคลุมกรมศุลกากร, สรรพสามิต, ETDA, BOT
/// </summary>
public interface IThaiGovIntegrationService
{
    // === กรมศุลกากร (Thai Customs) ===
    Task<List<HsCodeResult>> SearchHsCodeAsync(string query, int limit = 20);
    Task<HsCodeResult?> GetHsCodeAsync(string hsCode);
    Task<CustomsDutyInfo?> GetImportDutyRateAsync(string hsCode);

    // === ETDA: e-Timestamp & e-Signature verification ===
    Task<TimestampResponse> RequestTimestampAsync(byte[] documentHash, string hashAlgorithm = "SHA256");
    Task<bool> VerifyTimestampAsync(byte[] timestampToken);

    // === สำนักงานประกันสังคม (SSO) ===
    Task<SsoContributionRate> GetCurrentSsoRateAsync();

    // === กรมสรรพากร (RD) — extended services ===
    Task<RdVatRateInfo> GetCurrentVatRateAsync();
    Task<List<RdWhtRateInfo>> GetWhtRatesAsync();
    Task<RdBranchInfo?> LookupBranchAsync(string taxId, string branchCode);

    // === Thai National Single Window (NSW) ===
    Task<bool> CheckNswConnectionAsync();
}

// === Response DTOs ===

public record HsCodeResult(
    string HsCode,
    string DescriptionTh,
    string DescriptionEn,
    string? Unit,
    decimal? ImportDutyRate,
    decimal? VatRate,
    decimal? ExciseRate,
    string? Category);

public record CustomsDutyInfo(
    string HsCode,
    decimal GeneralRate,
    decimal? WtoRate,
    decimal? FtaAseanRate,
    decimal? FtaJtepaRate,
    decimal? FtaChinaRate,
    string? Notes);

public record TimestampResponse(
    bool Success,
    string? TimestampToken,
    DateTime? Timestamp,
    string? Authority,
    string? ErrorMessage);

public record SsoContributionRate(
    decimal EmployeeRate,
    decimal EmployerRate,
    decimal MaxSalaryBase,
    decimal MinSalaryBase,
    int Year,
    string? Notes);

public record RdVatRateInfo(
    decimal StandardRate,
    decimal ReducedRate,
    DateTime EffectiveFrom,
    string? Notes);

public record RdWhtRateInfo(
    string IncomeType,
    string Description,
    decimal PersonRate,
    decimal CompanyRate,
    string? Notes);

public record RdBranchInfo(
    string TaxId,
    string BranchCode,
    string? BranchName,
    string? Address,
    string? Status);
