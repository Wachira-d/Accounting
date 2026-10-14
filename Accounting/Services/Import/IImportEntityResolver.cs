using Accounting.Models.DTOs.Import;

namespace Accounting.Services.Import;

/// <summary>
/// **จับคู่แถวนำเข้ากับข้อมูลเดิม** (รอบ 205 T0 · IMPORT_ETL_REVIEW §2.4/§2.6)
///
/// <para><see cref="MatchAsync"/> — เจ้าของ <b>ทีม B</b> (<c>ImportEntityResolver.cs</c> · ห่อ <c>DuplicateDetector.MatchBatchAsync</c>):
/// ผู้ติดต่อ = เลขภาษี+สาขา → <c>ThaiLegalNameKey</c> → <c>CharNgramSimilarity.TopK</c> (≥ 0.88 แนะนำ) → AI <c>ImportEntityMatch</c> (59)
/// เฉพาะ top-2 ห่าง &lt; 0.05 และไม่มีเลขภาษี · สินค้า = <c>ProductMatcher</c> · <b>ชื่อคล้ายอย่างเดียวห้าม AutoApply</b> (คำตัดสินข้อ 151 (5))</para>
/// <para><see cref="ResolveAccountsAsync"/> — เจ้าของ <b>ทีม D</b> (<c>ImportEntityResolver.Accounts.cs</c> · partial แยกไฟล์ไม่ทับทีม B):
/// <c>ImportAccountCodeResolver</c> (รหัสตรง → AccountMappings → n-gram ชื่อ → หมวดเลขหน้า) → AI <c>ImportAccountMap</c> (58) · ด่าน ∈ ผังบริษัท</para>
/// <para>AI ผ่าน <c>IAiOrchestrator.AskAsync</c> เท่านั้น · เก็บ <c>AiFeedbackId</c> ไว้ปิดลูปตอนผู้ใช้ยืนยัน · kill-switch: ทุกชั้นใต้ AI เป็น local</para>
/// </summary>
public interface IImportEntityResolver
{
    Task<IReadOnlyList<ImportEntityMatchResult>> MatchAsync(Guid companyId, string entityType, IReadOnlyList<ImportTargetRow> rows,
        ImportResolveOptions options, CancellationToken ct = default);

    Task<IReadOnlyList<ImportAccountMatchResult>> ResolveAccountsAsync(Guid companyId, IReadOnlyList<ImportAccountQuery> queries,
        ImportResolveOptions options, CancellationToken ct = default);
}

/// <param name="AllowAi">false = local ล้วน (ไฟล์ใหญ่/worker · ผู้ใช้ปิด) · ปิด provider ทั้งระบบแล้วต้องได้ผลเหมือนกันทุกชั้นใต้ AI</param>
public sealed record ImportResolveOptions(bool AllowAi = true);

/// <summary>ผลจับคู่ของแถว · <see cref="EntityId"/> null = สร้างใหม่</summary>
/// <param name="Kind">"TaxBranch" · "Code" · "LegalName" · "Ngram" · "Ai" · "None"</param>
/// <param name="AutoApply">ระบบตั้ง MatchedEntityId ให้เลย (มีเลขภาษี/รหัสตรง) · false = แนะนำ ให้ผู้ใช้เลือก</param>
/// <param name="AiAnswer">(ฝ่ายค้านรอบ ETL · เพิ่มท้าย) คำตอบของชั้นโมเดล (id/"NEW") เมื่อ <see cref="AiFeedbackId"/> มีค่าแต่ผลไม่ได้ใช้คำตอบนั้น
/// (มั่นใจต่ำ/นอกชุด) — ผู้ปิดลูปเทียบกับคำตอบนี้ ไม่ใช่กับ <see cref="EntityId"/></param>
public sealed record ImportEntityMatchResult(
    Guid RowId,
    Guid? EntityId,
    string Kind,
    decimal Score,
    bool AutoApply,
    bool UsedAi,
    Guid? AiFeedbackId,
    IReadOnlyList<ImportMatchCandidateDto> Candidates,
    IReadOnlyList<ImportIssueDto> Issues,
    string? AiAnswer = null);

/// <summary>บัญชีที่ต้องหา — <see cref="Key"/> = คีย์ของผู้เรียก (เช่น รหัส+ชื่อในไฟล์) ใช้จับคืน</summary>
public sealed record ImportAccountQuery(string Key, string? Code, string? Name, string? Hint = null);

/// <summary>ผลหาบัญชี · <see cref="AccountId"/> null = ไม่พบ (ผู้ใช้เลือก/สร้าง)</summary>
/// <param name="Kind">"Code" · "Mapping" · "Ngram" · "Category" · "Ai" · "None"</param>
public sealed record ImportAccountMatchResult(
    string Key,
    Guid? AccountId,
    string? AccountCode,
    string? AccountName,
    string Kind,
    decimal Score,
    bool UsedAi,
    Guid? AiFeedbackId,
    IReadOnlyList<ImportMatchCandidateDto> Candidates);
