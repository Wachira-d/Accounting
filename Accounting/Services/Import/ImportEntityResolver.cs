using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Implementations.Import;
using Accounting.Services.Implementations.Ocr;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Import;

/// <summary>
/// จับคู่แถวนำเข้ากับข้อมูลเดิม (รอบ 205 ทีม B · สัญญาที่ <see cref="IImportEntityResolver"/> · IMPORT_ETL_REVIEW §2.4/§2.6)
/// <para>ไฟล์นี้ (<see cref="MatchAsync"/>) = <b>ทีม B</b> · <c>ImportEntityResolver.Accounts.cs</c> (<c>ResolveAccountsAsync</c>) = <b>ทีม D</b> —
/// partial class เพื่อให้สองทีมแก้คนละไฟล์ · constructor + dependency อยู่ที่ไฟล์นี้ (ทีม D ใช้ <see cref="_db"/> · <see cref="_ai"/> ร่วม)</para>
///
/// <para><b>ลำดับชั้น (ผู้ติดต่อ)</b>: เลขภาษี + สาขา (กุญแจ — ระบบเลือกให้เอง) → ชื่อนิติบุคคลเดียวกัน (<see cref="ThaiLegalNameKey"/>) → ชื่อคล้าย
/// (<see cref="CharNgramSimilarity.TopK"/> ≥ <see cref="SuggestThreshold"/>) → AI <c>ImportEntityMatch</c> (59) <b>เฉพาะ</b>ไม่มีเลขภาษี + สองอันดับแรกห่างกัน
/// &lt; <see cref="AmbiguityGap"/> · <b>ชื่ออย่างเดียวห้ามเลือกให้เอง</b> (หลักการ 3 · คำตัดสินข้อ 151 (5)) — ระบบแนะนำ ผู้ใช้กดยืนยัน ·
/// สินค้า: รหัส/บาร์โค้ด (กุญแจ) → <see cref="ProductMatcher"/> (แนะนำ)</para>
/// <para>กฎเหล็ก #1: AI ผ่าน <see cref="IImportAiAugmenter.ResolveEntityAsync"/> เท่านั้น (orchestrator · นักเรียนก่อน · ทีม A) · คำตอบต้องอยู่ในชุดผู้สมัคร ∪ {"NEW"} ·
/// เก็บ <see cref="ImportEntityMatchResult.AiFeedbackId"/> ให้ pipeline ปิดลูปตอนผู้ใช้ยืนยัน · ปิด AI / <see cref="ImportResolveOptions.AllowAi"/> = false ⇒
/// ทุกชั้นใต้ AI ยังให้ผลเท่าเดิม (kill-switch)</para>
/// </summary>
public partial class ImportEntityResolver : IImportEntityResolver
{
    /// <summary>คะแนนชื่อคล้ายที่ระบบ "แนะนำ" ให้ (ไม่เลือกเอง)</summary>
    public const decimal SuggestThreshold = 0.88m;
    /// <summary>สองอันดับแรกห่างกันน้อยกว่านี้ = กำกวม (ถามครูได้ถ้าไม่มีเลขภาษี)</summary>
    public const decimal AmbiguityGap = 0.05m;
    /// <summary>ผู้สมัครขั้นต่ำที่แสดงใน dropdown</summary>
    private const double CandidateFloor = 0.6;
    /// <summary>ผู้ติดต่อที่ดึงมาเทียบชื่อสูงสุด (ล่าสุดก่อน) — กันไฟล์ใหญ่ × ผู้ติดต่อหมื่นรายกินเวลาไม่จำกัด</summary>
    private const int NamePoolCap = 5000;
    /// <summary>คำตอบ AI ว่า "ไม่ใช่รายเดิม — สร้างใหม่"</summary>
    public const string NewEntityAnswer = "NEW";

    private readonly AccountingDbContext _db;
    private readonly IDuplicateDetector _duplicates;
    private readonly IImportAiAugmenter? _ai;
    private readonly ProductMatcher? _products;
    private readonly ILogger<ImportEntityResolver>? _logger;

    public ImportEntityResolver(AccountingDbContext db, IDuplicateDetector duplicates, IImportAiAugmenter? ai = null,
        ProductMatcher? products = null, ILogger<ImportEntityResolver>? logger = null)
    {
        _db = db;
        _duplicates = duplicates;
        _ai = ai;
        _products = products;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ImportEntityMatchResult>> MatchAsync(Guid companyId, string entityType, IReadOnlyList<ImportTargetRow> rows,
        ImportResolveOptions options, CancellationToken ct = default)
    {
        if (rows.Count == 0) return Array.Empty<ImportEntityMatchResult>();
        return ImportExportPermissionScope.NormalizeEntityType(entityType) switch
        {
            "contacts" => await MatchContactsAsync(companyId, rows, options, ct),
            "products" => await MatchProductsAsync(companyId, rows, ct),
            "chartofaccounts" => await MatchByCodeAsync(companyId, rows, "AccountCode",
                codes => _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && !a.IsDeleted && codes.Contains(a.AccountCode))
                    .Select(a => new CodeHit(a.Id, a.AccountCode, a.AccountCode + " " + a.AccountName)), ct),
            "projects" => await MatchByCodeAsync(companyId, rows, "Code",
                codes => _db.Projects.AsNoTracking()
                    .Where(p => p.CompanyId == companyId && !p.IsDeleted && codes.Contains(p.Code))
                    .Select(p => new CodeHit(p.Id, p.Code, p.Code + " " + p.Name)), ct),
            "employees" => await MatchByDetectorAsync(companyId, rows, "Employee", ct),
            _ => rows.Select(r => None(r.RowId)).ToList(),
        };
    }

    // ───────────────────────── ผู้ติดต่อ ─────────────────────────

    private sealed record PoolContact(Guid Id, string Name, string? TaxId, string Key);

    private async Task<IReadOnlyList<ImportEntityMatchResult>> MatchContactsAsync(Guid companyId, IReadOnlyList<ImportTargetRow> rows,
        ImportResolveOptions options, CancellationToken ct)
    {
        var keyed = await _duplicates.MatchBatchAsync(companyId, "Contact", rows.Select(r => r.Values).ToList(), ct);
        List<PoolContact>? pool = null;
        var results = new List<ImportEntityMatchResult>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var hit = keyed[i];
            var name = Value(row, "Name");
            var hasTax = ContactTaxBranchKey.HasTaxId(Value(row, "TaxId"));
            if (hit is { Kind: "TaxBranch" })
            {
                results.Add(new ImportEntityMatchResult(row.RowId, hit.ExistingId, "TaxBranch", 1m, AutoApply: true, UsedAi: false, null,
                    new[] { new ImportMatchCandidateDto(hit.ExistingId, hit.Label, 1m, "TaxBranch") }, Array.Empty<ImportIssueDto>()));
                continue;
            }
            // เลขนี้มีอยู่แล้วแต่คนละสาขา ⇒ ตัวจับคู่กลางบอก "รายใหม่ของสาขานั้น" — ไม่ถอยไปเทียบชื่อ
            if (hit == null && hasTax && await TaxIdExistsAsync(companyId, Value(row, "TaxId"), ct))
            {
                results.Add(None(row.RowId));
                continue;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                results.Add(None(row.RowId));
                continue;
            }

            pool ??= await LoadContactPoolAsync(companyId, ct);
            // SoftScope เดียวกับทุกทางเข้า: แถวในไฟล์มีเลข (ใหม่) ⇒ เทียบเฉพาะผู้ติดต่อที่ยังไม่มีเลข (แถวที่มีเลขอื่น = คนละนิติบุคคล)
            var scope = hasTax ? pool.Where(c => !ContactTaxBranchKey.HasTaxId(c.TaxId)).ToList() : pool;
            var candidates = new List<ImportMatchCandidateDto>();
            var rowKey = ThaiLegalNameKey.Key(name);
            foreach (var c in scope.Where(c => c.Key.Length > 0 && c.Key == rowKey && ThaiLegalNameKey.SameLegalName(name, c.Name)).Take(3))
                candidates.Add(new ImportMatchCandidateDto(c.Id, Label(c), 0.95m, "LegalName"));
            foreach (var (c, score) in CharNgramSimilarity.TopK(name, scope, c => c.Name, k: 5, minScore: CandidateFloor))
                if (candidates.All(x => x.EntityId != c.Id))
                    candidates.Add(new ImportMatchCandidateDto(c.Id, Label(c), Math.Round((decimal)score, 4, MidpointRounding.AwayFromZero), "Ngram"));
            candidates = candidates.OrderByDescending(c => c.Score).Take(5).ToList();

            if (candidates.Count == 0)
            {
                results.Add(None(row.RowId));
                continue;
            }
            var top = candidates[0];
            var suggested = top.Score >= SuggestThreshold ? top : null;
            var ambiguous = !hasTax && candidates.Count >= 2 && top.Score - candidates[1].Score < AmbiguityGap && candidates[1].Score >= CandidateFloorDecimal;
            if (ambiguous && options.AllowAi && _ai != null)
            {
                var ai = await AskTeacherAsync(companyId, row, name!, candidates, ct);
                if (ai != null)
                {
                    results.Add(ai);
                    continue;
                }
            }
            var issues = new List<ImportIssueDto>();
            if (suggested != null)
                issues.Add(new ImportIssueDto("Name", "match-name-only", ImportIssueSeverity.Warning,
                    $"ชื่อตรง/คล้ายผู้ติดต่อเดิม \"{suggested.Label}\" แต่ไม่มีเลขผู้เสียภาษียืนยัน — เลือก \"อัปเดตรายเดิม\" ถ้าเป็นรายเดียวกัน (ระบบไม่รวมให้เอง)",
                    Source: ImportIssueSource.Match));
            results.Add(new ImportEntityMatchResult(row.RowId, suggested?.EntityId, suggested?.Kind ?? "None", suggested?.Score ?? 0m,
                AutoApply: false, UsedAi: false, null, candidates, issues));
        }
        return results;
    }

    private static readonly decimal CandidateFloorDecimal = (decimal)CandidateFloor;

    private async Task<bool> TaxIdExistsAsync(Guid companyId, string? taxId, CancellationToken ct)
    {
        var key = await ContactTaxBranchKey.FindAsync(_db.Contacts.AsNoTracking().Where(c => !c.IsDeleted), companyId, taxId, null, ct);
        return key.TaxIdExists;
    }

    private async Task<List<PoolContact>> LoadContactPoolAsync(Guid companyId, CancellationToken ct)
        => (await _db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted)
                .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt)
                .Take(NamePoolCap)
                .Select(c => new { c.Id, c.Name, c.TaxId })
                .ToListAsync(ct))
            .Select(c => new PoolContact(c.Id, c.Name, c.TaxId, ThaiLegalNameKey.Key(c.Name)))
            .ToList();

    private static string Label(PoolContact c) => string.IsNullOrWhiteSpace(c.TaxId) ? c.Name : $"{c.Name} ({c.TaxId})";

    /// <summary>
    /// ถามชั้น AI <c>ImportEntityMatch</c> (59) ผ่าน <see cref="IImportAiAugmenter.ResolveEntityAsync"/> (ทีม A — orchestrator · นักเรียนก่อน ·
    /// ด่านคำตอบ ∈ ผู้สมัคร ∪ {"NEW"}) เฉพาะเคสกำกวมที่ไม่มีเลขภาษี · ยัง<b>ไม่เลือกให้เอง</b> (AutoApply = false — ชื่ออย่างเดียว) ·
    /// ไม่มีแถว feedback (provider ปิด/ล่ม — augmenter คืนคำตอบ local) หรือคำตอบมั่นใจ &lt; 0.70 ⇒ null (ผู้เรียกใช้ผลชั้น local ต่อเงียบ ๆ)
    /// </summary>
    private async Task<ImportEntityMatchResult?> AskTeacherAsync(Guid companyId, ImportTargetRow row, string name,
        IReadOnlyList<ImportMatchCandidateDto> candidates, CancellationToken ct)
    {
        var detail = string.Join(" · ", new[] { Value(row, "Address"), Value(row, "Email"), Value(row, "Phone") }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
        var pick = await _ai!.ResolveEntityAsync(companyId, null, "contacts", name, detail.Length == 0 ? null : detail,
            candidates.Select(c => new Accounting.Services.Ai.Prompts.ImportPrompts.EntityCandidate(c.EntityId.ToString(), c.Label, null, c.Score)).ToList(),
            candidates[0].EntityId.ToString(), candidates[0].Score, ct);
        if (pick.FeedbackId == null || string.IsNullOrWhiteSpace(pick.Answer)) return null;

        var used = pick.UsedAi;
        if (string.Equals(pick.Answer, NewEntityAnswer, StringComparison.OrdinalIgnoreCase))
            return new ImportEntityMatchResult(row.RowId, null, "Ai", pick.Confidence, AutoApply: false, UsedAi: used, pick.FeedbackId,
                candidates, new[] { new ImportIssueDto("Name", "match-ai-new", ImportIssueSeverity.Info,
                    (used ? "🤖 AI แนะนำ" : "⚙️ ระบบแนะนำ") + ": ชื่อคล้ายผู้ติดต่อเดิมหลายราย แต่น่าจะเป็นรายใหม่ — ตรวจแล้วเลือกรายเดิมได้ถ้าเป็นรายเดียวกัน",
                    Source: used ? ImportIssueSource.Ai : ImportIssueSource.Match) });
        // ด่านซ้ำฝั่งผู้เรียก: ต้องเป็น id ในชุดผู้สมัครเท่านั้น + มั่นใจพอ
        var chosen = Guid.TryParse(pick.Answer, out var id) ? candidates.FirstOrDefault(c => c.EntityId == id) : null;
        if (chosen == null || pick.Confidence < 0.70m) return null;
        return new ImportEntityMatchResult(row.RowId, chosen.EntityId, "Ai", pick.Confidence, AutoApply: false, UsedAi: used,
            pick.FeedbackId, candidates, new[] { new ImportIssueDto("Name", "match-name-only", ImportIssueSeverity.Warning,
                (used ? "🤖 AI แนะนำ" : "⚙️ ระบบแนะนำ") + $": น่าจะเป็นผู้ติดต่อเดิม \"{chosen.Label}\" (ไม่มีเลขผู้เสียภาษียืนยัน) — เลือก \"อัปเดตรายเดิม\" ถ้าใช่",
                Source: used ? ImportIssueSource.Ai : ImportIssueSource.Match) });
    }

    // ───────────────────────── สินค้า ─────────────────────────

    private async Task<IReadOnlyList<ImportEntityMatchResult>> MatchProductsAsync(Guid companyId, IReadOnlyList<ImportTargetRow> rows,
        CancellationToken ct)
    {
        var keyed = await _duplicates.MatchBatchAsync(companyId, "Product", rows.Select(r => r.Values).ToList(), ct);
        var results = new List<ImportEntityMatchResult>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (keyed[i] is { } hit)
            {
                results.Add(new ImportEntityMatchResult(row.RowId, hit.ExistingId, "Code", 1m, AutoApply: true, UsedAi: false, null,
                    new[] { new ImportMatchCandidateDto(hit.ExistingId, hit.Label, 1m, "Code") }, Array.Empty<ImportIssueDto>()));
                continue;
            }
            var name = Value(row, "Name");
            if (_products == null || string.IsNullOrWhiteSpace(name))
            {
                results.Add(None(row.RowId));
                continue;
            }
            var found = await _products.MatchAsync(companyId, name, null, maxAlternatives: 2);
            var candidates = found.Where(c => c.Confidence >= CandidateFloor)
                .Select(c => new ImportMatchCandidateDto(c.ProductId, $"{c.Code} {c.Name}",
                    Math.Round((decimal)c.Confidence, 4, MidpointRounding.AwayFromZero), "Ngram"))
                .ToList();
            var top = candidates.FirstOrDefault();
            var suggested = top != null && top.Score >= SuggestThreshold ? top : null;
            var issues = suggested == null ? Array.Empty<ImportIssueDto>() : new[]
            {
                new ImportIssueDto("Name", "match-name-only", ImportIssueSeverity.Warning,
                    $"ชื่อคล้ายสินค้าเดิม \"{suggested.Label}\" แต่รหัสไม่ตรง — เลือก \"อัปเดตรายเดิม\" ถ้าเป็นสินค้าเดียวกัน (ระบบไม่รวมให้เอง)",
                    Source: ImportIssueSource.Match),
            };
            results.Add(new ImportEntityMatchResult(row.RowId, suggested?.EntityId, suggested == null ? "None" : "Ngram", suggested?.Score ?? 0m,
                AutoApply: false, UsedAi: false, null, candidates, issues));
        }
        return results;
    }

    // ───────────────────────── รหัส / ตัวตรวจรายแถว ─────────────────────────

    private sealed record CodeHit(Guid Id, string Code, string Label);

    private static async Task<IReadOnlyList<ImportEntityMatchResult>> MatchByCodeAsync(Guid companyId, IReadOnlyList<ImportTargetRow> rows,
        string field, Func<List<string>, IQueryable<CodeHit>> query, CancellationToken ct)
    {
        var codes = rows.Select(r => Value(r, field)).Where(c => c != null).Select(c => c!).Distinct().ToList();
        var hits = codes.Count == 0 ? new List<CodeHit>() : await query(codes).ToListAsync(ct);
        return rows.Select(r =>
        {
            var code = Value(r, field);
            var h = code == null ? null : hits.FirstOrDefault(x => x.Code == code);
            return h == null ? None(r.RowId)
                : new ImportEntityMatchResult(r.RowId, h.Id, "Code", 1m, AutoApply: true, UsedAi: false, null,
                    new[] { new ImportMatchCandidateDto(h.Id, h.Label, 1m, "Code") }, Array.Empty<ImportIssueDto>());
        }).ToList();
    }

    private async Task<IReadOnlyList<ImportEntityMatchResult>> MatchByDetectorAsync(Guid companyId, IReadOnlyList<ImportTargetRow> rows,
        string detectorType, CancellationToken ct)
    {
        var keyed = await _duplicates.MatchBatchAsync(companyId, detectorType, rows.Select(r => r.Values).ToList(), ct);
        return rows.Select((r, i) => keyed[i] is { } h
            ? new ImportEntityMatchResult(r.RowId, h.ExistingId, "Code", (decimal)h.Score, AutoApply: h.Score >= 1.0, UsedAi: false, null,
                new[] { new ImportMatchCandidateDto(h.ExistingId, h.Label, (decimal)h.Score, "Code") }, Array.Empty<ImportIssueDto>())
            : None(r.RowId)).ToList();
    }

    private static ImportEntityMatchResult None(Guid rowId)
        => new(rowId, null, "None", 0m, AutoApply: false, UsedAi: false, null, Array.Empty<ImportMatchCandidateDto>(), Array.Empty<ImportIssueDto>());

    private static string? Value(ImportTargetRow row, string field)
        => row.Values.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}
