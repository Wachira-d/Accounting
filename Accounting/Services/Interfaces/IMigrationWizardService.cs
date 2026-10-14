using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

/// <summary>Data migration wizard for ERP onboarding — see service impl.</summary>
public interface IMigrationWizardService
{
    Task<MigrationSession> CreateSessionAsync(Guid companyId, string name, MigrationType type, Guid? targetPeriodId, string userId);
    Task UploadLegacyAccountsAsync(Guid companyId, Guid sessionId, List<(string Code, string? Name, decimal Debit, decimal Credit)> rows, string userId);

    /// <summary>รอบ 205 ทีม D — ข้อความที่วาง (แท็บจาก Excel · | · CSV มีคำพูด · "1,234.50") อ่านที่เซิร์ฟเวอร์ (<see cref="TrialBalancePasteParser"/>)
    /// → อัปโหลด → แนะนำบัญชีให้ทันที · บรรทัดที่ข้ามคืนพร้อมเหตุผล</summary>
    Task<MigrationPasteResult> UploadLegacyTextAsync(Guid companyId, Guid sessionId, string? text, string userId, CancellationToken ct = default);

    /// <summary>ผู้ใช้ยืนยัน/แก้บัญชีปลายทาง (รหัสเดิม → บัญชี · <see cref="Guid.Empty"/> = ล้าง) · บัญชีต้องอยู่ในผังของบริษัท ·
    /// แถวที่มีคำแนะนำจาก AI ⇒ <c>RecordUserChoiceAsync</c> (กฎเหล็ก #1)</summary>
    Task ApplyMappingsAsync(Guid companyId, Guid sessionId, Dictionary<string, Guid> codeToAccountId, string userId);

    /// <summary>รอบ 205 ทีม D — แนะนำบัญชีให้แถวที่ยังไม่จับคู่ (<see cref="ImportAccountCodeResolver"/> → AI <c>ImportAccountMap</c>) ·
    /// ถึงเกณฑ์ 0.85 = ระบบเลือกให้ (แก้ได้) · ต่ำกว่า = แนะนำเท่านั้น · แถวที่ผู้ใช้เลือกแล้วไม่ถูกแตะ</summary>
    Task<IReadOnlyList<MigrationMappingSuggestion>> SuggestMappingsAsync(Guid companyId, Guid sessionId, string userId,
        bool allowAi = true, CancellationToken ct = default);

    /// <summary>ตรวจ — แยก <b>บล็อก</b> (Errors) กับ <b>คำเตือนที่รับทราบได้</b> (Warnings) ตามคำตัดสินข้อ 150 (<see cref="OpeningTrialBalanceRules"/>)</summary>
    Task<MigrationValidationResult> ValidateSessionAsync(Guid companyId, Guid sessionId);

    /// <summary>ทางไปต่อของงบทดลองไม่ลงตัว — ผู้ใช้เลือกบัญชีผลต่างยกมา ระบบเพิ่มบรรทัดส่วนต่างให้ (แทนที่บรรทัดผลต่างเดิม)</summary>
    Task<MigrationValidationResult> PostDifferenceToAccountAsync(Guid companyId, Guid sessionId, Guid accountId, string userId);

    /// <summary>ลงยอดยกมา — ตรวจใหม่ในตัว (ไม่เชื่อสถานะเดิม) · มี Error = ปฏิเสธพร้อมทางไปต่อ · คำเตือนต้องอยู่ใน
    /// <paramref name="acknowledgedWarningCodes"/> · เก็บยอดเดิมที่ถูกทับไว้ใน ImportSummaryJson เพื่อย้อน</summary>
    Task<MigrationSession> CommitAsync(Guid companyId, Guid sessionId, string userId, IReadOnlyCollection<string>? acknowledgedWarningCodes = null);

    /// <summary>ย้อน — ลบยอดยกมาที่ session สร้าง + <b>คืนยอดเดิม</b>ที่ session ทับ · แถวที่ถูกแก้หลัง commit ไม่แตะ (รายงานใน summary)</summary>
    Task<MigrationSession> RollbackAsync(Guid companyId, Guid sessionId, string userId);
    Task<List<MigrationSession>> ListSessionsAsync(Guid companyId);
    Task<MigrationSession> GetSessionAsync(Guid companyId, Guid sessionId);
    Task<List<AccountMapping>> GetMappingsAsync(Guid companyId, Guid sessionId);
}

/// <summary>ผลตรวจ — <see cref="IsReady"/> = ไม่มีบล็อก (คำเตือนรับทราบตอน commit)</summary>
public sealed record MigrationValidationResult(
    IReadOnlyList<OpeningTbIssue> Errors,
    IReadOnlyList<OpeningTbIssue> Warnings,
    decimal TotalDebit,
    decimal TotalCredit)
{
    public bool IsReady => Errors.Count == 0;
    public decimal Difference => Math.Round(TotalDebit - TotalCredit, 2, MidpointRounding.AwayFromZero);
}

/// <summary>คำแนะนำบัญชีของหนึ่งแถว — ป้ายแหล่ง/ความมั่นใจมาจากเซิร์ฟเวอร์ (หน้าเว็บแสดงอย่างเดียว)</summary>
/// <param name="AutoApplied">ระบบเลือกให้แล้ว (≥ 0.85) — ผู้ใช้เปลี่ยนได้</param>
/// <param name="UsedAi">true เฉพาะคำแนะนำที่ AI ตอบจริง ⇒ ป้าย "🤖 AI แนะนำ"</param>
public sealed record MigrationMappingSuggestion(
    Guid MappingId,
    string LegacyCode,
    string? LegacyName,
    Guid? SuggestedAccountId,
    string? SuggestedCode,
    string? SuggestedName,
    string Kind,
    string SourceLabel,
    decimal Score,
    bool AutoApplied,
    bool UsedAi,
    string Reason,
    IReadOnlyList<ImportMatchCandidateDto> Candidates);

public sealed record MigrationPasteResult(
    int Imported,
    IReadOnlyList<TrialBalancePasteSkip> Skipped,
    string Delimiter,
    IReadOnlyList<MigrationMappingSuggestion> Suggestions);
