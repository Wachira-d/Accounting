using System.Globalization;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Import.Targets;

/// <summary>
/// **ปลายทาง "รายการเดินบัญชีธนาคาร (statement)" ของศูนย์นำเข้าข้อมูล** (รอบ 205 ทีม C · IMPORT_ETL_REVIEW §2.4/§2.9)
///
/// <para>ลงผ่านตัวเขียนตัวเดียว <see cref="IBankService.ImportParsedStatementRowsAsync"/> (ธุรกรรมของตัวเขียนเอง ⇒
/// <see cref="ImportCommitStyle.OwnTransactionPerGroup"/> · กลุ่ม = บัญชีธนาคาร) — กันซ้ำ · ยอดคงเหลือ = ยอดตามธนาคาร · จับคู่อัตโนมัติหลังบันทึก
/// เหมือนหน้า "ธนาคาร" ทุกข้อ · ไฟล์ต้นทางเก็บโดย pipeline (ไฟล์แนบ "ImportBatch") จึงไม่เก็บซ้ำที่นี่</para>
///
/// <para>บัญชีธนาคาร: ตัวเลือกของ batch <c>bankAccountId</c> (<see cref="ImportTargetContext.BatchOptions"/>) → คอลัมน์ "บัญชีธนาคาร"
/// (เลขที่บัญชี/ชื่อบัญชี) → บริษัทมีบัญชีเดียว = ระบบเติมให้ · ไม่พบหลังทุกทาง = บล็อกพร้อมทางไปต่อ (§2.5 "ไม่มีตัวตน") ·
/// บัญชีของบริษัทอื่น = บล็อก (§2.5 "อ้างข้ามบริษัท") · นอกนั้นคำเตือน (คำตัดสินข้อ 150)</para>
///
/// <para>ยอดเงิน: ช่อง "ถอน/เดบิต" + "ฝาก/เครดิต" (ตัวแปลงกลาง) หรือ "จำนวนเงิน" ช่องเดียว (<see cref="BankCsvParser.SignedAmount"/>:
/// วงเล็บ/ลบท้าย/CR-DR) + "ชนิดรายการ" (ฝาก/ถอน/ค่าธรรมเนียม/ดอกเบี้ย) เมื่อยอดไม่มีเครื่องหมาย</para>
/// <para>ย้อน: ลบรายการที่ยังไม่จับคู่ (<see cref="IBankService.DeleteTransactionsAsync"/>) · จับคู่/กระทบยอดแล้ว = บล็อก + ทางไปต่อ "ยกเลิกการจับคู่ก่อน"</para>
/// </summary>
public class BankStatementImportTarget : IImportTarget
{
    /// <summary>คีย์ตัวเลือกของ batch ที่บอกบัญชีธนาคารปลายทาง (Guid) — หน้า/ตัวสร้าง batch ใส่ไว้ใน OptionsJson</summary>
    public const string BankAccountOptionKey = "bankAccountId";
    public const string EntityTypeKey = "banktransactions";
    private const string CommittedEntityType = "BankTransaction";

    private readonly AccountingDbContext _db;
    private readonly IBankService _bank;

    public BankStatementImportTarget(AccountingDbContext db, IBankService bank)
    {
        _db = db;
        _bank = bank;
    }

    public string EntityType => EntityTypeKey;
    public string Label => "รายการเดินบัญชีธนาคาร (statement)";
    public ImportCommitStyle CommitStyle => ImportCommitStyle.OwnTransactionPerGroup;
    public bool SupportsUpdate => false;

    public IReadOnlyList<ImportFieldDto> Schema { get; } = new List<ImportFieldDto>
    {
        new("date", "วันที่", ImportFieldType.Date, true, "วันที่รายการ (รองรับ พ.ศ./ค.ศ. · วัน/เดือน/ปี)", Example: "05/08/2569"),
        new("description", "รายการ/รายละเอียด", ImportFieldType.Text, false, MaxLength: 500, Example: "รับโอนเงิน PromptPay"),
        new("debit", "ถอน/เงินออก (เดบิต)", ImportFieldType.Amount, false, "ยอดเงินออกจากบัญชี", Example: "1,250.00"),
        new("credit", "ฝาก/เงินเข้า (เครดิต)", ImportFieldType.Amount, false, "ยอดเงินเข้าบัญชี", Example: "3,000.00"),
        new("amount", "จำนวนเงิน (มีเครื่องหมาย)", ImportFieldType.Text, false,
            "ช่องเดียวแทนถอน/ฝาก — ติดลบ/วงเล็บ/ลบท้าย/DR = เงินออก · CR = เงินเข้า", Example: "(1,250.00) หรือ 1,250.00 DR"),
        new("type", "ชนิดรายการ", ImportFieldType.Text, false, "ฝาก/ถอน/ค่าธรรมเนียม/ดอกเบี้ย — ใช้เมื่อจำนวนเงินไม่มีเครื่องหมาย", Example: "ฝาก"),
        new("balance", "ยอดคงเหลือ", ImportFieldType.Amount, false, "ไม่บังคับ — มี = อัปเดตยอดตามธนาคาร + ตรวจความต่อเนื่อง", Example: "10,500.00"),
        new("reference", "เลขอ้างอิง", ImportFieldType.Reference, false, Example: "TRF20260805001"),
        new("channel", "ช่องทาง", ImportFieldType.Text, false, MaxLength: 200, Example: "K PLUS"),
        new("payee", "ผู้โอน/ผู้รับ", ImportFieldType.Text, false, MaxLength: 300),
        new("externalId", "รหัสรายการของธนาคาร", ImportFieldType.Reference, false, "ไม่ซ้ำต่อรายการ (FITID) — กันนำเข้าซ้ำแบบตรงตัว"),
        new("bankAccount", "บัญชีธนาคาร (เลขที่บัญชี)", ImportFieldType.Text, false,
            "ใช้เมื่อไฟล์มีหลายบัญชีหรือไม่ได้เลือกบัญชีที่ชุดนำเข้า", MaxLength: 100),
    };

    // ═══════════════════════ ตรวจ ═══════════════════════

    public async Task<IReadOnlyList<ImportRowValidation>> ValidateAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> rows,
        CancellationToken ct)
    {
        var accounts = await LoadAccountsAsync(ctx.CompanyId, ct);
        var optionAccount = ResolveOptionAccount(ctx, accounts, out var optionIssue);

        var parsed = rows.Select(r => (Row: r, Line: ParseLine(r))).ToList();
        var resolved = parsed.Select(p => (p.Row, p.Line, Account: optionAccount ?? ResolveRowAccount(p.Row, accounts))).ToList();

        // ของเดิมในช่วงวันของไฟล์ (ต่อบัญชี) — ตัดสินซ้ำด้วยกติกาเดียวกับตัวเขียน (รหัสต้นทาง · วัน+ยอด+ทิศแบบใช้ครั้งเดียว)
        var existing = new List<BankTransaction>();
        var dated = resolved.Where(x => x.Account != null && x.Line.Date != null).ToList();
        if (dated.Count > 0)
        {
            var accIds = dated.Select(x => x.Account!.Id).Distinct().ToList();
            var from = dated.Min(x => x.Line.Date!.Value).Date;
            var to = dated.Max(x => x.Line.Date!.Value).Date.AddDays(1);
            var extIds = dated.Where(x => x.Line.ExternalId != null).Select(x => x.Line.ExternalId!).Distinct().ToList();
            existing = await _db.BankTransactions.AsNoTracking()
                .Where(t => t.CompanyId == ctx.CompanyId && accIds.Contains(t.BankAccountId)
                    && ((t.TransactionDate >= from && t.TransactionDate < to) || (t.Reference != null && extIds.Contains(t.Reference))))
                .ToListAsync(ct);
        }
        var usedExisting = new HashSet<Guid>();
        var seenExternal = new HashSet<string>(StringComparer.Ordinal);
        var anyBalance = parsed.Any(p => p.Line.Balance != null);
        var firstRowId = rows.OrderBy(r => r.RowNo).Select(r => r.RowId).FirstOrDefault();
        decimal? prevBalance = null, prevAmount = null;
        var notedAccounts = new HashSet<Guid>();

        var result = new List<ImportRowValidation>(rows.Count);
        foreach (var (row, line, account) in resolved)
        {
            var issues = new List<ImportIssueDto>(line.Issues);
            ImportRowAction? action = null;
            Guid? matched = null;
            string? matchKind = null;
            decimal? matchScore = null;
            IReadOnlyDictionary<string, string?>? filled = null;

            if (optionIssue != null) issues.Add(optionIssue);
            else if (account == null)
                issues.Add(new ImportIssueDto("bankAccount", "bank-account-required", ImportIssueSeverity.Error,
                    !accounts.Any(a => a.IsActive)
                        ? "บริษัทยังไม่มีบัญชีธนาคาร — สร้างบัญชีที่หน้า \"ธนาคาร\" ก่อน แล้ววิเคราะห์ชุดนำเข้านี้อีกครั้ง"
                        : "ไม่รู้ว่ารายการนี้เป็นของบัญชีธนาคารใด — เลือกบัญชีที่ตัวเลือกของชุดนำเข้า หรือใส่เลขที่บัญชีในคอลัมน์ \"บัญชีธนาคาร\"",
                    Source: ImportIssueSource.Target));
            else if (optionAccount == null && string.IsNullOrWhiteSpace(Value(row, "bankAccount")))
            {
                // บริษัทมีบัญชีเดียว — ระบบเติมให้ (ผู้ใช้เห็นเซลล์ฟ้า "ระบบเติม" และแก้ได้)
                filled = new Dictionary<string, string?> { ["bankAccount"] = account.AccountNumber };
                issues.Add(new ImportIssueDto("bankAccount", "bank-account-defaulted", ImportIssueSeverity.Info,
                    $"ระบบเลือกบัญชี \"{account.AccountName}\" ให้ (บริษัทมีบัญชีธนาคารเดียว)", Source: ImportIssueSource.Target));
            }
            // ข้อสังเกตของบัญชี (ปิดใช้ · ไม่ผูกผัง) — ครั้งเดียวต่อบัญชี ที่แถวแรกของบัญชีนั้น (คำเตือนรับทราบตามรหัสอยู่แล้ว ไม่ต้องซ้ำทุกแถว)
            if (account != null && notedAccounts.Add(account.Id)) issues.AddRange(AccountNotes(account));

            if (line.Amount == 0m && !line.Issues.Any(i => i.Field is "amount" or "debit" or "credit"))
            {
                issues.Add(new ImportIssueDto(null, "amount-missing", ImportIssueSeverity.Warning,
                    "แถวนี้ไม่มีจำนวนเงิน (ไม่ใช่รายการ — อาจเป็นแถวหัวข้อ/ยอดยกมา) — ระบบตั้งเป็น \"ข้าม\" · ใส่จำนวนเงินถ้าเป็นรายการจริง",
                    Source: ImportIssueSource.Target));
                action = ImportRowAction.Skip;
            }
            if (row.Action == ImportRowAction.Update || row.Action == ImportRowAction.Merge)
                issues.Add(new ImportIssueDto(null, "bank-update-unsupported", ImportIssueSeverity.Warning,
                    "รายการธนาคารแก้ทับของเดิมจากไฟล์ไม่ได้ — ระบบจะข้ามแถวนี้ (แก้รายการเดิมที่หน้า \"ธนาคาร\")", Source: ImportIssueSource.Target));

            // ความต่อเนื่องของยอดตามลำดับในไฟล์ (เตือนรายแถว — แถวที่ยอดกระโดด = ไฟล์ขาดรายการก่อนหน้า) · รับทั้งไฟล์เก่า→ใหม่
            // (ยอดนี้ = ยอดก่อน + รายการนี้) และใหม่→เก่า (ยอดก่อน = ยอดนี้ + รายการก่อน)
            if (line.Balance is { } bal && line.Amount != 0m)
            {
                if (prevBalance is { } pb && prevAmount is { } pa
                    && Math.Abs(pb + line.Amount - bal) > 0.01m && Math.Abs(bal + pa - pb) > 0.01m)
                    issues.Add(new ImportIssueDto("balance", "balance-discontinuity", ImportIssueSeverity.Warning,
                        "ยอดคงเหลือไม่ต่อเนื่องจากแถวก่อน — ไฟล์อาจขาดรายการ (ถูกตัดหน้า/กรองตอนส่งออก) ยอดบัญชีอาจไม่ตรงธนาคาร",
                        Source: ImportIssueSource.Target));
                prevBalance = bal;
                prevAmount = line.Amount;
            }
            if (!anyBalance && row.RowId == firstRowId)
                issues.Add(new ImportIssueDto("balance", "no-balance-column", ImportIssueSeverity.Info,
                    "ไฟล์ไม่มียอดคงเหลือ — นำเข้าได้ แต่ยอดตามธนาคารบนการ์ดบัญชีจะไม่ถูกอัปเดต และตรวจความต่อเนื่องไม่ได้",
                    Source: ImportIssueSource.Target));

            // ซ้ำของเดิม — ระบบตั้ง "ข้าม" ให้ (ผู้ใช้เปลี่ยนเป็นสร้างได้ถ้ามั่นใจว่าไม่ซ้ำ)
            if (account != null && line.Date is { } date && line.Amount != 0m)
            {
                var isDeposit = line.Amount > 0;
                var amount = Math.Abs(line.Amount);
                var type = isDeposit ? BankTransactionType.Deposit : BankTransactionType.Withdrawal;
                var storedRef = line.ExternalId ?? line.Reference;
                BankTransaction? dup = null;
                var kind = "";
                if (line.ExternalId != null)
                {
                    dup = existing.FirstOrDefault(t => t.BankAccountId == account.Id && t.Reference == line.ExternalId);
                    if (dup == null && !seenExternal.Add(account.Id + "|" + line.ExternalId))
                    {
                        issues.Add(new ImportIssueDto("externalId", "duplicate-in-file", ImportIssueSeverity.Warning,
                            $"รหัสรายการ {line.ExternalId} ซ้ำกับแถวก่อนหน้าในไฟล์ — ระบบตั้งเป็น \"ข้าม\"", Source: ImportIssueSource.Target));
                        action = ImportRowAction.Skip;
                    }
                    kind = "ExternalId";
                }
                dup ??= storedRef == null ? null : existing.FirstOrDefault(t => t.BankAccountId == account.Id && !usedExisting.Contains(t.Id)
                    && t.Reference == storedRef && t.TransactionDate.Date == date.Date && t.Amount == amount && t.TransactionType == type);
                if (dup != null && kind.Length == 0) kind = "Reference";
                var sameContent = dup != null;
                if (dup == null)
                {
                    dup = existing.FirstOrDefault(t => t.BankAccountId == account.Id && !usedExisting.Contains(t.Id)
                        && t.TransactionDate.Date == date.Date && t.Amount == amount && t.TransactionType == type);
                    sameContent = dup != null && string.Equals((dup.Description ?? "").Trim(), (line.Description ?? "").Trim(),
                        StringComparison.OrdinalIgnoreCase);
                    kind = "DateAmount";
                }
                if (dup != null)
                {
                    usedExisting.Add(dup.Id);
                    matched = dup.Id;
                    matchKind = kind;
                    matchScore = sameContent ? 1m : 0.8m;
                    if (sameContent)
                    {
                        issues.Add(new ImportIssueDto(null, "duplicate-existing", ImportIssueSeverity.Warning,
                            $"มีรายการนี้ในบัญชีแล้ว ({dup.TransactionDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} · {dup.Amount.ToString("N2", CultureInfo.InvariantCulture)}) — ระบบตั้งเป็น \"ข้าม\"",
                            Source: ImportIssueSource.Target));
                        action = ImportRowAction.Skip;
                    }
                    else
                        issues.Add(new ImportIssueDto(null, "possible-duplicate", ImportIssueSeverity.Warning,
                            $"วันที่/ยอดตรงกับรายการเดิม \"{dup.Description}\" แต่คำอธิบายต่าง — ถ้าเป็นรายการเดียวกันให้ตั้งเป็น \"ข้าม\" ไม่งั้นระบบสร้างใหม่",
                            Source: ImportIssueSource.Target));
                }
            }

            result.Add(new ImportRowValidation(row.RowId, issues, action, matched, matchKind, matchScore,
                DisplayKey: DisplayKey(line), GroupKey: account != null ? "bank:" + account.Id.ToString("N") : null, FilledValues: filled));
        }
        return result;
    }

    // ═══════════════════════ ลง ═══════════════════════

    public async Task<IReadOnlyList<ImportRowCommitResult>> CommitGroupAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> group,
        CancellationToken ct)
    {
        var results = new Dictionary<Guid, ImportRowCommitResult>();
        var accounts = await LoadAccountsAsync(ctx.CompanyId, ct);
        var optionAccount = ResolveOptionAccount(ctx, accounts, out var optionIssue);

        var byAccount = new Dictionary<Guid, List<(ImportTargetRow Row, BankStatementLineInput Line)>>();
        foreach (var row in group.OrderBy(r => r.RowNo))
        {
            if (row.Action != ImportRowAction.Create)
            {
                results[row.RowId] = new(row.RowId, true, Skipped: true);
                continue;
            }
            var line = ParseLine(row);
            var account = optionIssue == null ? optionAccount ?? ResolveRowAccount(row, accounts) : null;
            if (account == null)
            {
                results[row.RowId] = new(row.RowId, false, Error: optionIssue?.Message
                    ?? "ไม่รู้ว่ารายการนี้เป็นของบัญชีธนาคารใด — เลือกบัญชีที่ตัวเลือกของชุดนำเข้า หรือใส่เลขที่บัญชีในคอลัมน์ \"บัญชีธนาคาร\"");
                continue;
            }
            if (line.Date is not { } date)
            {
                results[row.RowId] = new(row.RowId, false, Error: "ไม่มีวันที่ — แก้เซลล์วันที่แล้วลงใหม่");
                continue;
            }
            if (line.Amount == 0m)
            {
                results[row.RowId] = new(row.RowId, true, Skipped: true);
                continue;
            }
            if (!byAccount.TryGetValue(account.Id, out var list)) byAccount[account.Id] = list = new();
            list.Add((row, new BankStatementLineInput(date, line.Description,
                Withdrawal: line.Amount < 0 ? -line.Amount : 0m, Deposit: line.Amount > 0 ? line.Amount : 0m,
                Balance: line.Balance, Reference: line.Reference, Channel: line.Channel, ExternalId: line.ExternalId,
                Payee: line.Payee, RowNumber: row.RowNo)));
        }

        foreach (var (accountId, items) in byAccount)
        {
            try
            {
                // ผู้ใช้เห็นคำเตือน "possible-duplicate" แล้วยังให้สร้าง ⇒ ลงเป็นรายการใหม่ (InsertAsNew) · ซ้ำตรงตัว = ตัวเขียนข้ามเสมอ (idempotent)
                var written = await _bank.ImportParsedStatementRowsAsync(ctx.CompanyId, accountId, items.Select(i => i.Line).ToList(),
                    new BankStatementWriteOptions(OnConflict: BankStatementConflictPolicy.InsertAsNew, UploadedByUserId: ctx.Actor.UserId), ct);
                foreach (var lr in written.Lines)
                {
                    var rowId = items[lr.Index].Row.RowId;
                    results[rowId] = lr.Outcome switch
                    {
                        BankStatementLineOutcome.Created or BankStatementLineOutcome.Updated
                            => new(rowId, true, CommittedEntityType, lr.BankTransactionId),
                        BankStatementLineOutcome.Duplicate or BankStatementLineOutcome.Conflict
                            => new(rowId, true, Skipped: true),
                        _ => new(rowId, false, Error: lr.Message ?? "ลงรายการไม่สำเร็จ"),
                    };
                }
            }
            catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException
                                           or BusinessRuleException or DbUpdateException)
            {
                // ทั้งกลุ่มของบัญชีนี้ลงไม่สำเร็จ (ตัวเขียนย้อนทั้งชุดแล้ว) — ทุกแถวได้ข้อความ ไม่หายเงียบ · บัญชีอื่นลงต่อ
                var msg = ex is DbUpdateException ? "บันทึกรายการธนาคารไม่สำเร็จ — ลองลงใหม่อีกครั้ง" : ex.Message;
                foreach (var (row, _) in items) results[row.RowId] = new(row.RowId, false, Error: msg);
            }
        }

        return group.Select(r => results.TryGetValue(r.RowId, out var x) ? x : new ImportRowCommitResult(r.RowId, false, Error: "ไม่ได้ลง"))
            .ToList();
    }

    // ═══════════════════════ ย้อน ═══════════════════════

    public async Task<IReadOnlyList<ImportUndoCheck>> CanUndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows,
        CancellationToken ct)
    {
        var ids = rows.Select(r => r.EntityId).Distinct().ToList();
        var txns = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.CompanyId == ctx.CompanyId && ids.Contains(t.Id))
            .Select(t => new { t.Id, t.ReconciliationStatus, t.ReconciliationGroupId })
            .ToDictionaryAsync(t => t.Id, ct);
        return rows.Select(r =>
        {
            if (!txns.TryGetValue(r.EntityId, out var t))
                return new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, "รายการธนาคารนี้ถูกลบไปแล้ว", null);
            if (t.ReconciliationStatus == ReconciliationStatus.Matched || t.ReconciliationGroupId != null)
                return new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, "รายการนี้จับคู่/กระทบยอดกับเอกสารแล้ว",
                    "ยกเลิกการจับคู่ที่หน้า \"ธนาคาร → รายการเคลื่อนไหว\" ก่อน แล้วย้อนการนำเข้าอีกครั้ง");
            return new ImportUndoCheck(r.RowId, true, ImportUndoKind.SoftDelete);
        }).ToList();
    }

    public async Task<IReadOnlyList<ImportRowUndoResult>> UndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows,
        string reason, CancellationToken ct)
    {
        var checks = await CanUndoAsync(ctx, rows, ct);
        var allowed = rows.Zip(checks).Where(x => x.Second.CanUndo).Select(x => x.First).ToList();
        if (allowed.Count > 0)
            await _bank.DeleteTransactionsAsync(ctx.CompanyId,
                new DeleteTransactionsRequest(TransactionIds: allowed.Select(r => r.EntityId).ToList(), DeleteReconciled: false));

        var ids = rows.Select(r => r.EntityId).ToList();
        var stillThere = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.CompanyId == ctx.CompanyId && ids.Contains(t.Id))
            .Select(t => t.Id).ToListAsync(ct);
        var remaining = stillThere.ToHashSet();
        return rows.Zip(checks).Select(x => x.Second.CanUndo && !remaining.Contains(x.First.EntityId)
            ? new ImportRowUndoResult(x.First.RowId, true)
            : new ImportRowUndoResult(x.First.RowId, false,
                x.Second.CanUndo ? "ลบรายการไม่สำเร็จ (อาจถูกจับคู่ระหว่างทาง) — ตรวจที่หน้า \"ธนาคาร\"" : x.Second.BlockReason))
            .ToList();
    }

    // ═══════════════════════ ภายใน ═══════════════════════

    /// <summary>หนึ่งแถวที่ถอดเป็นค่าของรายการแล้ว — <see cref="Amount"/> มีเครื่องหมาย (บวก = เงินเข้า) · issue ของการถอด (ยอดอ่านไม่ได้ ฯลฯ)</summary>
    private sealed record ParsedLine(DateTime? Date, decimal Amount, decimal? Balance, string? Description, string? Reference,
        string? Channel, string? ExternalId, string? Payee, IReadOnlyList<ImportIssueDto> Issues);

    private static string? Value(ImportTargetRow row, string field)
        => row.Values.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static decimal? Canonical(ImportTargetRow row, string field)
        => Value(row, field) is { } s && decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var d) ? d : null;

    private static readonly string[] DepositWords = { "ฝาก", "เข้า", "รับ", "ดอกเบี้ย", "deposit", "credit", "cr", "in", "interest" };
    private static readonly string[] WithdrawalWords = { "ถอน", "ออก", "จ่าย", "โอนออก", "ค่าธรรมเนียม", "withdrawal", "withdraw", "debit", "dr", "out", "fee", "payment" };

    private static ParsedLine ParseLine(ImportTargetRow row)
    {
        var issues = new List<ImportIssueDto>();
        DateTime? date = Value(row, "date") is { } ds
            && DateTime.TryParseExact(ds, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        var credit = Canonical(row, "credit") ?? 0m;
        var debit = Math.Abs(Canonical(row, "debit") ?? 0m);
        var signed = credit - debit;
        if (signed == 0m && Value(row, "amount") is { } rawAmount)
        {
            var a = BankCsvParser.SignedAmount(rawAmount);
            if (!a.Ok)
                issues.Add(new ImportIssueDto("amount", a.IssueCode ?? "amount-invalid", ImportIssueSeverity.Warning,
                    (a.Message ?? $"อ่านจำนวนเงิน \"{rawAmount}\" ไม่ได้") + " — แก้เซลล์ แล้วระบบจะลงแถวนี้", Source: ImportIssueSource.Normalizer));
            else if (a.Value is { } v)
            {
                signed = v;
                var type = Value(row, "type")?.ToLowerInvariant();
                var hasSign = rawAmount.Contains('-') || rawAmount.Contains('(') || rawAmount.Contains('−')
                    || rawAmount.Contains("CR", StringComparison.OrdinalIgnoreCase) || rawAmount.Contains("DR", StringComparison.OrdinalIgnoreCase);
                if (type != null && !hasSign && signed > 0 && WithdrawalWords.Any(w => TypeIs(type, w)) && !DepositWords.Any(w => TypeIs(type, w)))
                    signed = -signed;
                else if (type != null && hasSign && signed > 0 && WithdrawalWords.Any(w => TypeIs(type, w)) && !DepositWords.Any(w => TypeIs(type, w)))
                    issues.Add(new ImportIssueDto("type", "type-sign-conflict", ImportIssueSeverity.Warning,
                        $"ชนิดรายการ \"{Value(row, "type")}\" ขัดกับเครื่องหมายของจำนวนเงิน — ระบบยึดเครื่องหมาย (เงินเข้า)", Source: ImportIssueSource.Target));
            }
        }
        else if (credit != 0m && debit != 0m)
            issues.Add(new ImportIssueDto(null, "both-debit-credit", ImportIssueSeverity.Warning,
                "แถวมีทั้งเงินเข้าและเงินออก — ระบบลงเป็นยอดสุทธิ ตรวจกับ statement", Source: ImportIssueSource.Target));

        var desc = Value(row, "description");
        return new ParsedLine(date, Math.Round(signed, 2, MidpointRounding.AwayFromZero), Canonical(row, "balance"),
            string.IsNullOrWhiteSpace(desc) ? "(ไม่มีรายละเอียด)" : desc, Value(row, "reference"), Value(row, "channel"),
            Value(row, "externalId"), Value(row, "payee"), issues);
    }

    /// <summary>คำชนิดรายการ: ไทย = มีคำนั้นอยู่ในข้อความ · อังกฤษ = เป็นคำเต็ม (กัน "in" ใน "interest"/"invoice")</summary>
    private static bool TypeIs(string type, string word)
        => word[0] > 127
            ? type.Contains(word, StringComparison.Ordinal)
            : type.Split(new[] { ' ', '/', '-', '_', '.', ',' }, StringSplitOptions.RemoveEmptyEntries).Any(t => t == word);

    private static string? DisplayKey(ParsedLine l)
    {
        if (l.Date == null) return l.Description;
        var desc = l.Description ?? "";
        if (desc.Length > 40) desc = desc[..40] + "…";
        return $"{l.Date.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} · {(l.Amount >= 0 ? "+" : "−")}{Math.Abs(l.Amount).ToString("N2", CultureInfo.InvariantCulture)} · {desc}";
    }

    private sealed record AccountRef(Guid Id, string AccountName, string AccountNumber, bool IsActive, bool HasLinkedGl);

    /// <summary>บัญชีธนาคารทั้งหมดของบริษัทนี้ (รวมที่ปิดใช้ — ตัวเลือกของ batch ชี้บัญชีปิดใช้ได้ = คำเตือน · การเดาจากคอลัมน์ใช้เฉพาะที่เปิดใช้)</summary>
    private async Task<List<AccountRef>> LoadAccountsAsync(Guid companyId, CancellationToken ct)
        => await _db.BankAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId)
            .Select(a => new AccountRef(a.Id, a.AccountName, a.AccountNumber, a.IsActive, a.LinkedAccountId != null))
            .ToListAsync(ct);

    /// <summary>
    /// บัญชีจากตัวเลือกของ batch <c>bankAccountId</c> (หน้า import-hub ส่ง <c>BankAccounts.Id</c> ที่ผู้ใช้เลือก) — ไม่ระบุ = null แล้วใช้ทางสำรอง
    /// (คอลัมน์ "บัญชีธนาคาร" → บริษัทมีบัญชีเดียว) · ไม่ใช่ Guid / ไม่ใช่บัญชีของบริษัทนี้ = บล็อกพร้อมทางไปต่อ (§2.5 ไม่มีตัวตน/อ้างข้ามบริษัท)
    /// </summary>
    private static AccountRef? ResolveOptionAccount(ImportTargetContext ctx, List<AccountRef> accounts, out ImportIssueDto? issue)
    {
        issue = null;
        if (ctx.BatchOptions == null || !ctx.BatchOptions.TryGetValue(BankAccountOptionKey, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;
        if (Guid.TryParse(raw, out var id) && accounts.FirstOrDefault(a => a.Id == id) is { } acc) return acc;
        issue = new ImportIssueDto("bankAccount", "bank-account-not-found", ImportIssueSeverity.Error,
            "ไม่พบบัญชีธนาคารที่เลือกไว้ในบริษัทนี้ (อาจถูกลบ หรือเป็นของบริษัทอื่น) — เลือกบัญชีใหม่ที่ตัวเลือก \"บัญชีธนาคาร\" ของชุดนำเข้า แล้ววิเคราะห์อีกครั้ง",
            Source: ImportIssueSource.Target);
        return null;
    }

    /// <summary>ข้อสังเกตของบัญชีปลายทาง (ไม่บล็อก · คำตัดสินข้อ 150): ปิดใช้อยู่ · ยังไม่ผูกผังบัญชี (จับคู่กับสมุดรายวันได้ไม่ครบ)</summary>
    private static IEnumerable<ImportIssueDto> AccountNotes(AccountRef account)
    {
        if (!account.IsActive)
            yield return new ImportIssueDto("bankAccount", "bank-account-inactive", ImportIssueSeverity.Warning,
                $"บัญชี \"{account.AccountName}\" ปิดใช้อยู่ — ลงรายการได้ แต่ตรวจว่าเลือกบัญชีถูก หรือเปิดใช้บัญชีที่หน้า \"ธนาคาร\" ก่อน",
                Source: ImportIssueSource.Target);
        if (!account.HasLinkedGl)
            yield return new ImportIssueDto("bankAccount", "bank-account-no-gl", ImportIssueSeverity.Warning,
                $"บัญชี \"{account.AccountName}\" ยังไม่ผูกผังบัญชี — นำเข้าได้ แต่จับคู่กับสมุดรายวันอัตโนมัติไม่ได้ครบ · ผูกผังที่หน้า \"ธนาคาร → แก้ไขบัญชี\"",
                Source: ImportIssueSource.Target);
    }

    /// <summary>บัญชีจากคอลัมน์ "บัญชีธนาคาร" (เลขที่บัญชีตรงตัวเลข/ลงท้ายเหมือนกัน ≥ 4 หลัก · หรือชื่อบัญชีตรง) → บริษัทมีบัญชีเดียว = บัญชีนั้น</summary>
    private static AccountRef? ResolveRowAccount(ImportTargetRow row, List<AccountRef> accounts)
    {
        accounts = accounts.Where(a => a.IsActive).ToList();
        var v = Value(row, "bankAccount");
        if (v == null) return accounts.Count == 1 ? accounts[0] : null;
        static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());
        var digits = Digits(v);
        if (digits.Length >= 4)
        {
            var hits = accounts.Where(a =>
            {
                var mine = Digits(a.AccountNumber);
                return mine.Length >= 4 && (mine == digits || mine.EndsWith(digits, StringComparison.Ordinal) || digits.EndsWith(mine, StringComparison.Ordinal));
            }).ToList();
            if (hits.Count == 1) return hits[0];
        }
        var byName = accounts.Where(a => string.Equals(a.AccountName.Trim(), v, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }
}
