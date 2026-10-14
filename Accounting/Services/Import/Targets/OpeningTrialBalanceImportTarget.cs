using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using static Accounting.Services.Import.Targets.ImportTargetValues;

namespace Accounting.Services.Import.Targets;

/// <summary>
/// **ปลายทาง "งบทดลองยกมา"** ของศูนย์นำเข้า (รอบ 205 ทีม D · IMPORT_ETL_REVIEW §2.4/§2.5/§2.9/§2.10)
///
/// <para>ลงยอดผ่าน <see cref="IMigrationWizardService"/> ตัวเดิมเท่านั้น (สร้างชุด → อัปโหลด → จับคู่ → ลงยอด) — ไม่เขียน OpeningBalances ตรง ·
/// ตรวจด้วย <see cref="OpeningTrialBalanceRules"/> ตัวเดียวกับหน้า wizard ⇒ รหัสคำเตือนชุดเดียวกัน รับทราบที่ศูนย์แล้วส่งต่อได้ตรงตัว ·
/// จับคู่บัญชีด้วย <see cref="ImportAccountMatcher"/> (รหัสตรง → ความจำ → ชื่อคล้าย → หมวด → AI 58) · ผู้ใช้เลือกเองได้ผ่าน MatchedEntityId</para>
/// <para><b>บล็อก</b>: เดบิต ≠ เครดิต — ทางไปต่อ: ผู้ใช้เลือก "บัญชีผลต่างยกมา" ในตัวเลือกของชุด (<see cref="OptionDifferenceAccountId"/>) แล้วระบบลงส่วนต่างให้ ·
/// ไม่มีบัญชีหลังทุกชั้น · ไม่มีงวด/งวดปิด · นอกนั้นคำเตือน · งบทดลองทั้งชุด = กลุ่มเดียว (ลงบางบรรทัดไม่ได้ — ยอดจะไม่ลงตัว)</para>
/// <para>ย้อน = <see cref="IMigrationWizardService.RollbackAsync"/> (ลบยอดที่สร้าง + คืนยอดเดิมที่ถูกทับ)</para>
/// </summary>
public class OpeningTrialBalanceImportTarget : IImportTarget, IImportGroupIntegrityTarget
{
    public const string Key = "opening-balances";
    /// <summary>ตัวเลือกของชุด: งวดบัญชีปลายทาง (Guid) — บังคับ</summary>
    public const string OptionPeriodId = "fiscalPeriodId";
    /// <summary>ตัวเลือกของชุด: บัญชีผลต่างยกมาที่ผู้ใช้เลือก (Guid) — ทางไปต่อของงบไม่ลงตัว</summary>
    public const string OptionDifferenceAccountId = "differenceAccountId";
    /// <summary>ชนิด entity ที่บันทึกใน ImportStagedRow.CommittedEntityType (ทุกแถวของชุดชี้ MigrationSession เดียว)</summary>
    public const string CommittedEntity = "MigrationSession";
    private const string Group = "opening-tb";

    private readonly AccountingDbContext _db;
    private readonly IMigrationWizardService _wizard;
    private readonly ImportAccountMatcher _accounts;

    public OpeningTrialBalanceImportTarget(AccountingDbContext db, IMigrationWizardService wizard, ImportAccountMatcher accounts)
    {
        _db = db;
        _wizard = wizard;
        _accounts = accounts;
    }

    public string EntityType => Key;
    public string Label => "งบทดลองยกมา";
    public ImportCommitStyle CommitStyle => ImportCommitStyle.OwnTransactionPerGroup;
    public bool SupportsUpdate => false;

    /// <summary>field ปลายทาง — ตัวเดียวที่ <see cref="ImportSchemas"/> (ทีม A · owner เดียวของแคตตาล็อก) · ทีม D ปรับรายการของชนิดนี้ที่นั่น</summary>
    public IReadOnlyList<ImportFieldDto> Schema => ImportSchemas.For(Key)!;

    /// <summary>เดบิต/เครดิตของแถว (ยอดบวกเสมอ) — คอลัมน์ยอดคงเหลือเดียวก็ได้ · เดบิตติดลบ = เครดิต</summary>
    public static (decimal Debit, decimal Credit) Amounts(ImportTargetRow row)
    {
        var dr = Amount(row, "Debit");
        var cr = Amount(row, "Credit");
        if (dr == null && cr == null && Amount(row, "Balance") is { } bal)
            return bal >= 0 ? (bal, 0m) : (0m, -bal);
        decimal d = dr ?? 0m, c = cr ?? 0m;
        if (d < 0) { c += -d; d = 0m; }
        if (c < 0) { d += -c; c = 0m; }
        return (d, c);
    }

    public async Task<IReadOnlyList<ImportRowValidation>> ValidateAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> rows, CancellationToken ct)
    {
        var (lines, picks) = await BuildLinesAsync(ctx, rows, ct);
        var period = await LoadPeriodAsync(ctx, ct);
        var verdict = await EvaluateAsync(ctx, lines, period, ct);

        var fileCodes = rows.Select(r => Text(r, "AccountCode") ?? "").ToHashSet(StringComparer.Ordinal);
        var result = new List<ImportRowValidation>(rows.Count);
        foreach (var row in rows)
        {
            var code = Text(row, "AccountCode") ?? "";
            var pick = picks[row.RowId];
            var issues = new List<ImportIssueDto>();
            foreach (var e in verdict.Errors.Concat(verdict.Warnings))
            {
                // ปัญหาที่ผูกบรรทัดซึ่งไม่อยู่ในไฟล์ (บรรทัดผลต่างที่ระบบเพิ่ม) = ของทั้งชุด ⇒ ติดทุกแถวให้ผู้ใช้รับทราบ
                if (e.Keys.Count > 0 && !e.Keys.Contains(code) && e.Keys.Any(fileCodes.Contains)) continue;
                var field = e.Code is OpeningTrialBalanceRules.Unmapped or OpeningTrialBalanceRules.AccountInvalid
                    or OpeningTrialBalanceRules.ClassMismatch or OpeningTrialBalanceRules.HeaderAccount ? "AccountCode" : null;
                var msg = e.WayForward == null ? e.Message : $"{e.Message} — {e.WayForward}";
                var suggested = e.Code == OpeningTrialBalanceRules.Unmapped ? pick.Match?.Candidates.FirstOrDefault()?.Label.Split(' ')[0] : null;
                issues.Add(e.IsError ? Error(field, e.Code, msg, suggested, "IMPORT-TB") : Warning(field, e.Code, msg, suggested, "IMPORT-TB"));
            }
            // ฝ่ายค้านรอบ ETL (P1-4): คู่ที่ผู้ใช้เลือก = "User" · คู่ที่ระบบเลือกไว้รอบก่อน = คงไว้ (MatchKind null ⇒ pipeline คงวิธี/คะแนน/ป้ายเดิม ·
            // ไม่ถาม AI ซ้ำทุกครั้งที่ตรวจ) · คู่ที่เพิ่งหา = วิธี/คะแนนของตัวหาบัญชี (AI มั่นใจ < 0.85 ⇒ แถบเหลือง) · feedback ของ ImportAccountMap
            // เก็บที่แถวพักแล้ว pipeline ปิดลูปตอนลงยอดด้วย "รหัสบัญชี" ที่ผู้ใช้ยืนยัน
            var m = pick.Match;
            result.Add(new ImportRowValidation(row.RowId, issues,
                MatchedEntityId: pick.AccountId,
                MatchKind: pick.ChosenByUser ? "User" : m?.Kind,
                MatchScore: pick.ChosenByUser ? 1m : m?.Score,
                DisplayKey: $"{code} {Text(row, "AccountName")}".Trim(),
                GroupKey: Group,
                MatchCandidates: m?.Candidates,
                MatchUsedAi: m?.UsedAi ?? false,
                MatchAiFeedbackId: m?.AiFeedbackId,
                MatchAiAnswer: m is { AiFeedbackId: not null } && m.Kind is "Ai" or "Learned" ? m.AccountCode : null,
                MatchAnswerSpace: ImportMatchAnswerSpace.AccountCode));
        }
        return result;
    }

    public async Task<IReadOnlyList<ImportRowCommitResult>> CommitGroupAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> group, CancellationToken ct)
    {
        var rows = group.Where(r => r.Action != ImportRowAction.Skip).ToList();
        var skipped = group.Where(r => r.Action == ImportRowAction.Skip)
            .Select(r => new ImportRowCommitResult(r.RowId, true, Skipped: true)).ToList();
        if (rows.Count == 0) return skipped;

        var periodId = OptionGuid(ctx, OptionPeriodId);
        if (periodId == null)
            return FailAll(rows, "ยังไม่ได้เลือกงวดบัญชีที่จะเก็บยอดยกมา — เลือกงวดในตัวเลือกของชุดแล้วนำเข้าอีกครั้ง").Concat(skipped).ToList();

        var (_, picks) = await BuildLinesAsync(ctx, rows, ct);
        var missing = rows.Where(r => picks[r.RowId].AccountId == null).Select(r => Text(r, "AccountCode")).ToList();
        if (missing.Count > 0)
            return FailAll(rows, $"ยังไม่ได้เลือกบัญชีปลายทางของ {string.Join(", ", missing.Take(5))} — เลือกบัญชีแล้วนำเข้าอีกครั้ง (งบทดลองต้องลงทั้งชุด)")
                .Concat(skipped).ToList();

        var by = CreatedBy(ctx);
        try
        {
            var session = await _wizard.CreateSessionAsync(ctx.CompanyId,
                $"นำเข้างบทดลอง (ศูนย์นำเข้า {ctx.BatchId.ToString("N")[..8]})", MigrationType.OpeningBalances, periodId, by);
            await _wizard.UploadLegacyAccountsAsync(ctx.CompanyId, session.Id,
                rows.Select(r => { var (d, c) = Amounts(r); return (Text(r, "AccountCode")!, Text(r, "AccountName"), d, c); }).ToList(), by);
            var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var r in rows) map[Text(r, "AccountCode")!.Trim()] = picks[r.RowId].AccountId!.Value;
            await _wizard.ApplyMappingsAsync(ctx.CompanyId, session.Id, map, by);
            if (OptionGuid(ctx, OptionDifferenceAccountId) is { } diffAcc)
                await _wizard.PostDifferenceToAccountAsync(ctx.CompanyId, session.Id, diffAcc, by);
            var committed = await _wizard.CommitAsync(ctx.CompanyId, session.Id, by, ctx.Options.AcknowledgedWarningCodes?.ToList());
            return rows.Select(r => new ImportRowCommitResult(r.RowId, true, CommittedEntity, committed.Id)).Concat(skipped).ToList();
        }
        catch (Exception ex) when (ex is BusinessRuleException or KeyNotFoundException)
        {
            // ล้มดังที่ผลรายแถว (ไม่กลืน) — ทั้งชุดล้มด้วยเหตุเดียว เพราะงบทดลองลงบางบรรทัดไม่ได้
            return FailAll(rows, ex.Message).Concat(skipped).ToList();
        }
    }

    public async Task<IReadOnlyList<ImportUndoCheck>> CanUndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.EntityId).Distinct().ToList();
        var sessions = await _db.MigrationSessions.AsNoTracking()
            .Where(s => s.CompanyId == ctx.CompanyId && ids.Contains(s.Id))
            .Select(s => new { s.Id, s.Status, s.TargetFiscalPeriodId })
            .ToListAsync(ct);
        var periodIds = sessions.Where(s => s.TargetFiscalPeriodId.HasValue).Select(s => s.TargetFiscalPeriodId!.Value).ToList();
        var periods = await _db.FiscalPeriods.AsNoTracking()
            .Where(p => p.CompanyId == ctx.CompanyId && periodIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        return rows.Select(r =>
        {
            var s = sessions.FirstOrDefault(x => x.Id == r.EntityId);
            if (s == null)
                return new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, "ไม่พบชุดยอดยกมาที่นำเข้า", null);
            if (s.Status != MigrationStatus.Committed)
                return new ImportUndoCheck(r.RowId, false, ImportUndoKind.None,
                    $"ชุดยอดยกมานี้{Accounting.Services.Implementations.MigrationWizardLabels.Status(s.Status)}แล้ว", null);
            if (s.TargetFiscalPeriodId is { } pid && periods.TryGetValue(pid, out var p)
                && p.Status is FiscalPeriodStatus.Closed or FiscalPeriodStatus.Locked)
                return new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, $"งวด {p.Name} ปิดแล้ว",
                    "เปิดงวดที่หน้า งวดบัญชี ก่อน แล้วย้อนอีกครั้ง");
            return new ImportUndoCheck(r.RowId, true, ImportUndoKind.Rollback);
        }).ToList();
    }

    public async Task<IReadOnlyList<ImportRowUndoResult>> UndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, string reason, CancellationToken ct)
    {
        var results = new List<ImportRowUndoResult>(rows.Count);
        foreach (var g in rows.GroupBy(r => r.EntityId))
        {
            try
            {
                var rolled = await _wizard.RollbackAsync(ctx.CompanyId, g.Key, CreatedBy(ctx));
                // ฝ่ายค้านรอบ ETL: wizard ไม่แตะยอดที่ถูกแก้ด้วยมือหลังลงยอด — แถวของบัญชีนั้นต้องไม่ถูกประทับ "ย้อนแล้ว" (บอกเหตุผล + ทางไปต่อรายแถว)
                var kept = Accounting.Services.Implementations.MigrationWizardService.RollbackSkippedAccountIds(rolled.ImportSummaryJson);
                var accountOf = kept.Count == 0 ? new Dictionary<Guid, Guid?>() : await RowAccountsAsync(ctx, g.Select(r => r.RowId).ToList(), ct);
                results.AddRange(g.Select(r => accountOf.GetValueOrDefault(r.RowId) is Guid acc && kept.Contains(acc)
                    ? new ImportRowUndoResult(r.RowId, false,
                        "ยอดยกมาของบัญชีนี้ถูกแก้ด้วยมือหลังนำเข้า ระบบจึงไม่ย้อนแถวนี้ (ไม่ทับงานของผู้ใช้) — ตรวจ/ลบยอดของบัญชีนี้ที่หน้ายอดยกมาเอง")
                    : new ImportRowUndoResult(r.RowId, true)));
            }
            catch (Exception ex) when (ex is BusinessRuleException or KeyNotFoundException)
            {
                results.AddRange(g.Select(r => new ImportRowUndoResult(r.RowId, false, ex.Message)));
            }
        }
        return results;
    }

    // ───────────────────────── ภายใน ─────────────────────────

    /// <summary>บัญชีที่แถวพักของชุดนี้เลือกไว้ (กรอง CompanyId + batch)</summary>
    private async Task<Dictionary<Guid, Guid?>> RowAccountsAsync(ImportTargetContext ctx, IReadOnlyList<Guid> rowIds, CancellationToken ct)
        => await _db.ImportStagedRows.AsNoTracking()
            .Where(r => r.CompanyId == ctx.CompanyId && r.SessionId == ctx.BatchId && rowIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.MatchedEntityId, ct);

    private sealed record Pick(Guid? AccountId, ImportAccountMatchResult? Match, bool ChosenByUser = false);

    /// <summary>บรรทัดสำหรับตัวตัดสิน + บัญชีที่เลือกต่อแถว (ผู้ใช้เลือก MatchedEntityId ชนะ · ไม่งั้นตัวหาบัญชีเลือกให้ถ้าถึงเกณฑ์)</summary>
    private async Task<(List<OpeningTbLine> Lines, Dictionary<Guid, Pick> Picks)> BuildLinesAsync(
        ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> rows, CancellationToken ct)
    {
        var chart = await _accounts.LoadChartAsync(ctx.CompanyId, ct);
        // คู่ที่ผู้ใช้เลือก/ระบบเลือกไว้รอบก่อนคงไว้ (บัญชียังอยู่) · ที่เหลือหาใหม่ — ถาม AI ตามที่ pipeline อนุญาต (ตอนลงยอด/worker/ตรวจทั้งชุดหลังแก้แถว = ไม่ถาม ·
        // ฝ่ายค้านรอบ ETL P1-4: เดิมถาม AI ทุกครั้งที่ตรวจและตอนลงยอด)
        var toResolve = rows.Where(r => r.MatchedEntityId is not Guid id || (!r.MatchChosenByUser && chart.Find(id) == null)).ToList();
        var resolved = await _accounts.ResolveAsync(ctx.CompanyId,
            toResolve.Select(r => new ImportAccountQuery(r.RowId.ToString("N"), Text(r, "AccountCode"), Text(r, "AccountName"))).ToList(),
            new ImportResolveOptions(AllowAi: AllowAi(ctx)), ct, chart);
        var byKey = resolved.ToDictionary(x => x.Key);

        var lines = new List<OpeningTbLine>(rows.Count + 1);
        var picks = new Dictionary<Guid, Pick>(rows.Count);
        foreach (var r in rows)
        {
            var (d, c) = Amounts(r);
            var code = Text(r, "AccountCode") ?? "";
            ImportChartAccount? acc;
            bool missing;
            if (!byKey.ContainsKey(r.RowId.ToString("N")) && r.MatchedEntityId is { } chosen)
            {
                acc = chart.Find(chosen);
                missing = acc == null;
                picks[r.RowId] = new Pick(acc?.AccountId, null, r.MatchChosenByUser);
            }
            else
            {
                var m = byKey[r.RowId.ToString("N")];
                acc = m.AccountId is { } id ? chart.Find(id) : null;
                missing = false;
                picks[r.RowId] = new Pick(acc?.AccountId, m);
            }
            if (r.Action != ImportRowAction.Skip)
                lines.Add(new OpeningTbLine(code, Text(r, "AccountName"), d, c, acc, missing));
        }

        // ทางไปต่อของงบไม่ลงตัว: ผู้ใช้เลือกบัญชีผลต่างแล้ว ⇒ ตรวจรวมบรรทัดผลต่าง (ตัวเลขเดียวกับที่ wizard จะเพิ่มตอนลงยอด)
        if (OptionGuid(ctx, OptionDifferenceAccountId) is { } diffId && chart.Find(diffId) is { } diffAcc)
        {
            var (dr, cr) = OpeningTrialBalanceRules.BalancingAmounts(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
            if (dr != 0m || cr != 0m)
                lines.Add(new OpeningTbLine(OpeningTrialBalanceRules.DifferenceLineKey, null, dr, cr, diffAcc));
        }
        return (lines, picks);
    }

    private async Task<Models.Entities.FiscalPeriod?> LoadPeriodAsync(ImportTargetContext ctx, CancellationToken ct)
        => OptionGuid(ctx, OptionPeriodId) is { } pid
            ? await _db.FiscalPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid && p.CompanyId == ctx.CompanyId, ct)
            : null;

    private async Task<OpeningTbVerdict> EvaluateAsync(ImportTargetContext ctx, List<OpeningTbLine> lines,
        Models.Entities.FiscalPeriod? period, CancellationToken ct)
    {
        var mapped = lines.Where(l => l.Account != null).Select(l => l.Account!.AccountId).Distinct().ToList();
        var existing = period == null ? new HashSet<Guid>()
            : (await _db.OpeningBalances.AsNoTracking()
                .Where(o => o.CompanyId == ctx.CompanyId && o.FiscalPeriodId == period.Id && mapped.Contains(o.AccountId))
                .Select(o => o.AccountId).ToListAsync(ct)).ToHashSet();
        return OpeningTrialBalanceRules.Evaluate(lines, new OpeningTbContext(period != null, period?.Name, period?.Status, existing));
    }
}
