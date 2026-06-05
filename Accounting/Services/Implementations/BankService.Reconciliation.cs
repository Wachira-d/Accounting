using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using MiniExcelLibs;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class BankService
{
    // ============================================================
    // M:N reconciliation + net-off (Receipt − PaymentVoucher = bank line)
    // ------------------------------------------------------------
    // Group-based reconciliation that supersedes the legacy 1:1 / M:1 flows
    // for complex cases. Each group bundles a set of bank transactions with
    // a set of match items (Payment / JournalEntry / Document); both sides
    // carry SIGNED allocated amounts so a customer refund (Receipt +1000)
    // and a vendor refund-out (PV -200) can both bind to a single bank
    // deposit of +800. Group "balances" when the signed sum on the bank
    // side matches the signed sum on the item side within the configured
    // tolerance (default ฿0.01). Existing 1:1 / M:1 endpoints keep working
    // — this is additive.
    // ============================================================

    public async Task<ReconciliationGroupResponse> CreateReconciliationGroupAsync(
        Guid companyId, CreateReconciliationGroupRequest request, string userId)
    {
        if (request.BankTransactions == null || request.BankTransactions.Count == 0)
            throw new ArgumentException("ต้องเลือก bank transaction อย่างน้อย 1 รายการ");
        if (request.MatchItems == null || request.MatchItems.Count == 0)
            throw new ArgumentException("ต้องเลือกรายการที่ใช้กระทบยอดอย่างน้อย 1 รายการ");

        var account = await _db.Set<BankAccount>()
            .FirstOrDefaultAsync(a => a.Id == request.BankAccountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        // Resolve and validate bank-side items
        var bankTxnIds = request.BankTransactions.Select(b => b.ItemId).Distinct().ToList();
        var bankTxns = await _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId
                && t.BankAccountId == request.BankAccountId
                && bankTxnIds.Contains(t.Id))
            .ToListAsync();
        if (bankTxns.Count != bankTxnIds.Count)
            throw new InvalidOperationException("มี bank transaction บางรายการไม่พบในบัญชีนี้");

        // Reject already-reconciled bank txns (either in another group or in the legacy 1:1 path)
        var alreadyReconciled = bankTxns
            .Where(t => t.ReconciliationGroupId.HasValue
                || t.MatchedPaymentId.HasValue
                || t.MatchedJournalEntryId.HasValue
                || !string.IsNullOrWhiteSpace(t.MatchedEntryIdsJson))
            .ToList();
        if (alreadyReconciled.Count > 0)
        {
            // Show date + amount + a snippet of the memo so the operator can
            // actually FIND the offending lines (a truncated GUID is useless).
            string Describe(BankTransaction t)
            {
                var sign = t.TransactionType == BankTransactionType.Deposit ? "+" : "-";
                var desc = (t.Description ?? t.Payee ?? "").Trim();
                if (desc.Length > 40) desc = desc[..40] + "…";
                return $"{t.TransactionDate:dd/MM/yyyy} {sign}{Math.Abs(t.Amount):N2}" +
                       (string.IsNullOrWhiteSpace(desc) ? "" : $" ({desc})");
            }
            throw new InvalidOperationException(
                "Bank transaction ต่อไปนี้ถูกกระทบยอดไปแล้ว — กรุณายกเลิกการจับคู่เดิมก่อน:\n• " +
                string.Join("\n• ", alreadyReconciled.Select(Describe)));
        }

        // Bank-side signed sum: deposits are positive, withdrawals negative,
        // but operator may override per-item. Default-fill amount from txn
        // when not provided (zero-allocated bank-side items don't make sense).
        decimal totalBank = 0m;
        var bankAllocations = new List<(BankTransaction Txn, decimal Amount)>();
        foreach (var bankReq in request.BankTransactions)
        {
            var txn = bankTxns.First(t => t.Id == bankReq.ItemId);
            var defaultAmount = txn.TransactionType == BankTransactionType.Deposit ? txn.Amount : -txn.Amount;
            var amount = bankReq.AllocatedAmount == 0 ? defaultAmount : bankReq.AllocatedAmount;
            bankAllocations.Add((txn, amount));
            totalBank += amount;
        }

        // Validate + accumulate match-side items. Polymorphic FK resolution.
        decimal totalMatched = 0m;
        foreach (var item in request.MatchItems)
        {
            if (!Enum.TryParse<ReconciliationItemType>(item.ItemType, ignoreCase: true, out var itemType)
                || itemType == ReconciliationItemType.BankTransaction)
                throw new ArgumentException($"ItemType '{item.ItemType}' ไม่ถูกต้อง — รองรับ Payment / JournalEntry / Document");
            if (!await DoesItemExistAsync(companyId, itemType, item.ItemId))
                throw new InvalidOperationException(
                    $"ไม่พบ {itemType} {item.ItemId.ToString("N")[..8]} ในบริษัทนี้");
            if (await IsItemAlreadyInGroupAsync(companyId, itemType, item.ItemId))
                throw new InvalidOperationException(
                    $"{itemType} {item.ItemId.ToString("N")[..8]} ถูกกระทบยอดในกลุ่มอื่นอยู่แล้ว");
            totalMatched += item.AllocatedAmount;
        }

        // Balance check (net-off check): bank side MUST equal item side within
        // tolerance. A group that doesn't balance leaves money unaccounted, so
        // we block it outright — the operator adds a fee / difference line (or
        // fixes the selection) until both sides match.
        var diff = Math.Abs(totalBank - totalMatched);
        var tolerance = request.Tolerance > 0 ? request.Tolerance : 0.01m;
        var balanced = diff <= tolerance;
        if (!balanced)
            throw new InvalidOperationException(
                $"ยอดสองฝั่งไม่ตรงกัน — Bank {totalBank:N2} vs รายการ {totalMatched:N2} ต่างกัน {diff:N2} บาท. " +
                "ยอดต้องเท่ากันเสมอ: เพิ่ม/แก้รายการให้ผลรวมเท่ากัน (เช่น เพิ่มรายการค่าธรรมเนียม/ส่วนต่าง) หรือปรับ Tolerance หากเป็นเศษปัดเศษเล็กน้อย.");

        var groupNumber = await NextReconciliationGroupNumberAsync(companyId);

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var group = new ReconciliationGroup
            {
                CompanyId = companyId,
                BankAccountId = request.BankAccountId,
                GroupNumber = groupNumber,
                ReconciledDate = request.ReconciledDate == default ? DateTime.UtcNow.Date : request.ReconciledDate.Date,
                TotalBankAmount = totalBank,
                TotalMatchedAmount = totalMatched,
                IsBalanced = balanced,
                Notes = request.Notes,
                CreatedBy = userId,
            };
            _db.ReconciliationGroups.Add(group);

            foreach (var (txn, amount) in bankAllocations)
            {
                _db.ReconciliationGroupItems.Add(new ReconciliationGroupItem
                {
                    GroupId = group.Id,
                    ItemType = ReconciliationItemType.BankTransaction,
                    ItemId = txn.Id,
                    AllocatedAmount = amount,
                });
                txn.ReconciliationGroupId = group.Id;
                txn.ReconciliationStatus = ReconciliationStatus.Matched;
                txn.ReconciledAt = DateTime.UtcNow;
                txn.ReconciledBy = userId;
                txn.UpdatedAt = DateTime.UtcNow;
            }

            foreach (var item in request.MatchItems)
            {
                var itemType = Enum.Parse<ReconciliationItemType>(item.ItemType, ignoreCase: true);
                _db.ReconciliationGroupItems.Add(new ReconciliationGroupItem
                {
                    GroupId = group.Id,
                    ItemType = itemType,
                    ItemId = item.ItemId,
                    AllocatedAmount = item.AllocatedAmount,
                    Notes = item.Notes,
                });
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            // Best-effort learning capture — failures don't roll back the confirmed match.
            await RecordReconciliationPatternsAsync(companyId, group.Id);

            return await BuildGroupResponseAsync(companyId, group.Id);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<ReconciliationGroupResponse> GetReconciliationGroupAsync(Guid companyId, Guid groupId)
        => await BuildGroupResponseAsync(companyId, groupId);

    public async Task<PagedResponse<ReconciliationGroupListItem>> GetReconciliationGroupsAsync(
        Guid companyId, Guid bankAccountId, PagedRequest request)
    {
        var query = _db.ReconciliationGroups
            .Where(g => g.CompanyId == companyId && g.BankAccountId == bankAccountId);

        var total = await query.CountAsync();
        var groups = await query
            .OrderByDescending(g => g.ReconciledDate)
            .ThenByDescending(g => g.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(g => new
            {
                g.Id,
                g.GroupNumber,
                g.ReconciledDate,
                g.TotalBankAmount,
                g.TotalMatchedAmount,
                g.IsBalanced,
                BankCount = g.Items.Count(i => i.ItemType == ReconciliationItemType.BankTransaction),
                ItemCount = g.Items.Count(i => i.ItemType != ReconciliationItemType.BankTransaction),
            })
            .ToListAsync();

        var items = groups.Select(g => new ReconciliationGroupListItem(
            g.Id, g.GroupNumber, g.ReconciledDate, g.BankCount, g.ItemCount,
            g.TotalBankAmount, g.TotalMatchedAmount, g.IsBalanced)).ToList();

        return new PagedResponse<ReconciliationGroupListItem>(items, total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    /// <summary>
    /// Find every ReconciliationGroup that contains the given (itemType, itemId)
    /// reference and unwind each one — release the bank-transaction members
    /// back to Unmatched, soft-delete the group + its items, and decrement the
    /// associated learning patterns so AI confidence isn't inflated by a
    /// reconciliation that was effectively undone via a cascade.
    /// </summary>
    public async Task UnwindGroupsContainingItemAsync(
        Guid companyId, ReconciliationItemType itemType, Guid itemId)
    {
        var groupIds = await _db.ReconciliationGroupItems
            .Where(i => i.ItemType == itemType && i.ItemId == itemId
                && i.Group.CompanyId == companyId
                && !i.IsDeleted)
            .Select(i => i.GroupId)
            .Distinct()
            .ToListAsync();
        foreach (var gid in groupIds)
            await UnreconcileGroupAsync(companyId, gid);
    }

    public async Task UnreconcileGroupAsync(Guid companyId, Guid groupId)
    {
        var group = await _db.ReconciliationGroups
            .Include(g => g.Items)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มการกระทบยอด");

        var bankTxnIds = group.Items
            .Where(i => i.ItemType == ReconciliationItemType.BankTransaction)
            .Select(i => i.ItemId).ToList();
        var bankTxns = await _db.Set<BankTransaction>()
            .Where(t => bankTxnIds.Contains(t.Id) && t.CompanyId == companyId)
            .ToListAsync();
        foreach (var t in bankTxns)
        {
            t.ReconciliationGroupId = null;
            t.ReconciliationStatus = ReconciliationStatus.Unmatched;
            t.ReconciledAt = null;
            t.ReconciledBy = null;
            t.UpdatedAt = DateTime.UtcNow;
        }

        // Soft-delete group + items (cascade query filter hides them from reads).
        foreach (var item in group.Items)
        {
            item.IsDeleted = true;
            item.UpdatedAt = DateTime.UtcNow;
        }
        group.IsDeleted = true;
        group.UpdatedAt = DateTime.UtcNow;

        // Decrement learning patterns associated with this group's
        // (bankTxn × item) pairs. Without this, repeated match→unmatch
        // cycles on the same pair inflate the "TimesConfirmed" counter
        // and pollute future AI suggestions with phantom confidence.
        await DecrementPatternsForGroupAsync(companyId, group);

        await _db.SaveChangesAsync();
    }

    /// <summary>Mirror of RecordReconciliationPatternsAsync that walks the
    /// same (bankTxn × item) cross-product and decrements TimesConfirmed.
    /// Patterns with TimesConfirmed ≤ 0 are soft-deleted so they don't
    /// linger as zero-confidence noise. Failures are swallowed —
    /// learning bookkeeping never blocks a reconcile/unreconcile.</summary>
    private async Task DecrementPatternsForGroupAsync(Guid companyId, ReconciliationGroup group)
    {
        try
        {
            var bankItems = group.Items.Where(i => i.ItemType == ReconciliationItemType.BankTransaction).ToList();
            var matchItems = group.Items.Where(i => i.ItemType != ReconciliationItemType.BankTransaction).ToList();
            if (bankItems.Count == 0 || matchItems.Count == 0) return;

            var bankIds = bankItems.Select(b => b.ItemId).ToList();
            var bankTxns = await _db.Set<BankTransaction>().AsNoTracking()
                .Where(t => bankIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Description, t.Reference, t.Payee })
                .ToListAsync();

            foreach (var b in bankItems)
            {
                var info = bankTxns.FirstOrDefault(x => x.Id == b.ItemId);
                if (info == null) continue;
                var sig = ComputeDescriptionSignature(info.Description, info.Reference, info.Payee);
                var bucket = ComputeAmountBucket(b.AllocatedAmount);

                foreach (var item in matchItems)
                {
                    var pattern = await _db.BankReconciliationPatterns
                        .FirstOrDefaultAsync(p => p.CompanyId == companyId
                            && p.BankAccountId == group.BankAccountId
                            && p.DescriptionSignature == sig
                            && p.AmountBucket == bucket
                            && p.TargetType == item.ItemType);
                    if (pattern == null) continue;
                    pattern.TimesConfirmed = Math.Max(0, pattern.TimesConfirmed - 1);
                    pattern.UpdatedAt = DateTime.UtcNow;
                    if (pattern.TimesConfirmed == 0)
                    {
                        pattern.IsDeleted = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DecrementPatternsForGroupAsync failed for group {GroupId}", group.Id);
        }
    }

    public async Task<byte[]> ExportReconciliationReportAsync(
        Guid companyId, Guid bankAccountId, DateTime? fromDate, DateTime? toDate)
    {
        var account = await _db.Set<BankAccount>()
            .FirstOrDefaultAsync(a => a.Id == bankAccountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        var from = fromDate?.Date ?? DateTime.UtcNow.Date.AddMonths(-3);
        var to = toDate?.Date.AddDays(1) ?? DateTime.UtcNow.Date.AddDays(1);

        var txns = await _db.Set<BankTransaction>()
            .AsNoTracking()
            .Where(t => t.CompanyId == companyId
                && t.BankAccountId == bankAccountId
                && t.TransactionDate >= from && t.TransactionDate < to)
            .OrderBy(t => t.TransactionDate)
            .ToListAsync();

        var groups = await _db.ReconciliationGroups
            .AsNoTracking()
            .Include(g => g.Items)
            .Where(g => g.CompanyId == companyId && g.BankAccountId == bankAccountId
                && g.ReconciledDate >= from && g.ReconciledDate < to)
            .OrderBy(g => g.ReconciledDate)
            .ToListAsync();

        // MiniExcel multi-sheet export: each sheet is a Dictionary<string,object>
        // entry where the value is a List of row dictionaries. MiniExcel writes
        // ordered columns based on the first row's key order; we use ordered
        // dictionaries to lock the column sequence per sheet.
        var groupNumberById = groups.ToDictionary(g => g.Id, g => g.GroupNumber);

        var sheet1 = txns.Select(t =>
        {
            var matchType = t.ReconciliationGroupId.HasValue ? "Group" :
                t.MatchedPaymentId.HasValue ? "Payment" :
                t.MatchedJournalEntryId.HasValue ? "JournalEntry" :
                !string.IsNullOrWhiteSpace(t.MatchedEntryIdsJson) ? "AI-Aggregate" : "";
            var groupNum = t.ReconciliationGroupId.HasValue && groupNumberById.TryGetValue(t.ReconciliationGroupId.Value, out var gn) ? gn : "";
            return new Dictionary<string, object?>
            {
                ["วันที่"] = t.TransactionDate.ToString("yyyy-MM-dd"),
                ["ประเภท"] = t.TransactionType.ToString(),
                ["จำนวนเงิน"] = t.Amount,
                ["ยอดคงเหลือ"] = t.BalanceAfter,
                ["รายละเอียด"] = t.Description ?? "",
                ["อ้างอิง"] = t.Reference ?? "",
                ["ผู้รับ/ผู้จ่าย"] = t.Payee ?? "",
                ["สถานะกระทบยอด"] = t.ReconciliationStatus.ToString(),
                ["Match Type"] = matchType,
                ["Matched #"] = t.MatchedPaymentId?.ToString() ?? t.MatchedJournalEntryId?.ToString() ?? "",
                ["เลขกลุ่ม"] = groupNum,
                ["วันที่กระทบยอด"] = t.ReconciledAt?.ToString("yyyy-MM-dd") ?? "",
            };
        }).ToList();

        var sheet2 = groups.Select(g => new Dictionary<string, object?>
        {
            ["เลขกลุ่ม"] = g.GroupNumber,
            ["วันที่"] = g.ReconciledDate.ToString("yyyy-MM-dd"),
            ["Bank Total"] = g.TotalBankAmount,
            ["Matched Total"] = g.TotalMatchedAmount,
            ["ผลต่าง"] = g.TotalBankAmount - g.TotalMatchedAmount,
            ["สมดุล"] = g.IsBalanced ? "✓" : "✗",
            ["บันทึกย่อ"] = g.Notes ?? "",
            ["ผู้ทำรายการ"] = g.CreatedBy ?? "",
        }).ToList();

        var sheet3 = groups.SelectMany(g =>
            g.Items
                .OrderBy(i => i.ItemType).ThenByDescending(i => Math.Abs(i.AllocatedAmount))
                .Select(it => new Dictionary<string, object?>
                {
                    ["เลขกลุ่ม"] = g.GroupNumber,
                    ["ประเภทรายการ"] = it.ItemType.ToString(),
                    ["Item ID"] = it.ItemId.ToString(),
                    ["จำนวนเงิน (signed)"] = it.AllocatedAmount,
                    ["หมายเหตุ"] = it.Notes ?? "",
                })
        ).ToList();

        var sheet4 = new List<Dictionary<string, object?>>
        {
            new() { ["รายการ"] = "บัญชี", ["ค่า"] = $"{account.AccountName} ({account.BankName} {account.AccountNumber})" },
            new() { ["รายการ"] = "ช่วงเวลา", ["ค่า"] = $"{from:yyyy-MM-dd} ถึง {to.AddDays(-1):yyyy-MM-dd}" },
            new() { ["รายการ"] = "ยอดธนาคารปัจจุบัน", ["ค่า"] = account.CurrentBalance.ToString("#,##0.00") },
            new() { ["รายการ"] = "รวมรายการในช่วงนี้", ["ค่า"] = txns.Count },
            new() { ["รายการ"] = "กระทบยอดแล้ว", ["ค่า"] = txns.Count(t => t.ReconciliationStatus == ReconciliationStatus.Matched) },
            new() { ["รายการ"] = "ยังไม่กระทบยอด", ["ค่า"] = txns.Count(t => t.ReconciliationStatus == ReconciliationStatus.Unmatched) },
            new() { ["รายการ"] = "จำนวนกลุ่มกระทบยอด", ["ค่า"] = groups.Count },
            new() { ["รายการ"] = "กลุ่มที่สมดุล", ["ค่า"] = groups.Count(g => g.IsBalanced) },
        };

        var sheets = new Dictionary<string, object>
        {
            ["Transactions"] = sheet1,
            ["Groups"] = sheet2,
            ["Group Items"] = sheet3,
            ["Summary"] = sheet4,
        };

        using var ms = new MemoryStream();
        ms.SaveAs(sheets);
        return ms.ToArray();
    }

    // ===== Helpers =====

    private async Task<string> NextReconciliationGroupNumberAsync(Guid companyId)
    {
        var now = DateTime.UtcNow;
        var prefix = $"RG-{now:yyyy-MM}-";
        var maxSerial = await _db.ReconciliationGroups
            .Where(g => g.CompanyId == companyId && g.GroupNumber.StartsWith(prefix))
            .Select(g => g.GroupNumber)
            .ToListAsync();
        var next = 1 + maxSerial
            .Select(n => int.TryParse(n.Split('-').LastOrDefault(), out var x) ? x : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"{prefix}{next:D3}";
    }

    private async Task<bool> DoesItemExistAsync(Guid companyId, ReconciliationItemType type, Guid id)
    {
        return type switch
        {
            ReconciliationItemType.Payment => await _db.Set<Payment>()
                .AnyAsync(p => p.Id == id && p.CompanyId == companyId),
            ReconciliationItemType.JournalEntry => await _db.JournalEntries
                .AnyAsync(j => j.Id == id && j.CompanyId == companyId),
            ReconciliationItemType.Document => await _db.Documents
                .AnyAsync(d => d.Id == id && d.CompanyId == companyId),
            _ => false,
        };
    }

    private async Task<bool> IsItemAlreadyInGroupAsync(Guid companyId, ReconciliationItemType type, Guid id)
    {
        return await _db.ReconciliationGroupItems
            .AnyAsync(i => i.ItemType == type && i.ItemId == id
                && i.Group.CompanyId == companyId);
    }

    /// <summary>
    /// Build the pool of items that haven't been reconciled yet anywhere in the
    /// system — across both legacy 1:1 / M:1 fields and the new
    /// ReconciliationGroup. Filtered to the bank account's linked GL account
    /// when possible so the M:N workbench only surfaces relevant payments / JEs.
    /// </summary>
    public async Task<UnmatchedItemsResponse> GetUnmatchedItemsAsync(
        Guid companyId, Guid bankAccountId, string? search, DateTime? fromDate, DateTime? toDate)
    {
        var account = await _db.Set<BankAccount>().AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == bankAccountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        var from = fromDate?.Date ?? DateTime.UtcNow.Date.AddMonths(-6);
        var to = toDate?.Date.AddDays(1) ?? DateTime.UtcNow.Date.AddDays(1);
        var q = (search ?? "").Trim().ToLowerInvariant();

        // ----- Exclusion sets: already matched legacy-style OR in a group -----
        var legacyMatched = await _db.Set<BankTransaction>().AsNoTracking()
            .Where(t => t.CompanyId == companyId
                && (t.MatchedPaymentId != null || t.MatchedJournalEntryId != null
                    || t.MatchedEntryIdsJson != null || t.MatchGroupId != null))
            .Select(t => new { t.MatchedPaymentId, t.MatchedJournalEntryId, t.MatchedEntryIdsJson })
            .ToListAsync();
        var usedPaymentIds = new HashSet<Guid>();
        var usedJeIds = new HashSet<Guid>();
        foreach (var t in legacyMatched)
        {
            if (t.MatchedPaymentId.HasValue) usedPaymentIds.Add(t.MatchedPaymentId.Value);
            if (t.MatchedJournalEntryId.HasValue) usedJeIds.Add(t.MatchedJournalEntryId.Value);
            if (string.IsNullOrWhiteSpace(t.MatchedEntryIdsJson)) continue;
            try
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(t.MatchedEntryIdsJson!);
                if (ids != null) foreach (var id in ids) { usedPaymentIds.Add(id); usedJeIds.Add(id); }
            }
            catch { /* malformed; ignore */ }
        }

        // Items already inside any ReconciliationGroup for the company.
        var inGroupItems = await _db.ReconciliationGroupItems.AsNoTracking()
            .Where(i => i.Group.CompanyId == companyId
                && i.ItemType != ReconciliationItemType.BankTransaction)
            .Select(i => new { i.ItemType, i.ItemId })
            .ToListAsync();
        foreach (var i in inGroupItems)
        {
            if (i.ItemType == ReconciliationItemType.Payment) usedPaymentIds.Add(i.ItemId);
            else if (i.ItemType == ReconciliationItemType.JournalEntry) usedJeIds.Add(i.ItemId);
        }
        var usedDocIds = inGroupItems
            .Where(i => i.ItemType == ReconciliationItemType.Document)
            .Select(i => i.ItemId).ToHashSet();

        // ----- Payments -----
        var paymentExclude = usedPaymentIds.ToList();
        var paymentsQuery = _db.Set<Payment>().AsNoTracking()
            .Include(p => p.Document).ThenInclude(d => d.Contact)
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                && p.PaymentDate >= from && p.PaymentDate < to
                && p.Document.Status != DocumentStatus.Voided
                && !paymentExclude.Contains(p.Id));
        if (!string.IsNullOrWhiteSpace(q))
            paymentsQuery = paymentsQuery.Where(p =>
                p.PaymentNumber.ToLower().Contains(q)
                || (p.Notes != null && p.Notes.ToLower().Contains(q))
                || (p.Document.Contact != null && p.Document.Contact.Name.ToLower().Contains(q)));
        var payments = await paymentsQuery
            .OrderByDescending(p => p.PaymentDate).Take(200)
            .Select(p => new UnmatchedItem(
                "Payment", p.Id, p.PaymentNumber, p.PaymentDate,
                p.Notes ?? p.Document.DocumentNumber, p.Amount,
                p.Document.Contact != null ? p.Document.Contact.Name : null))
            .ToListAsync();

        // ----- Journal Entries (Posted, not auto-doc-twin) -----
        var jeExclude = usedJeIds.ToList();
        var jesQuery = _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= from && j.EntryDate < to
                && !jeExclude.Contains(j.Id));
        if (!string.IsNullOrWhiteSpace(q))
            jesQuery = jesQuery.Where(j =>
                j.EntryNumber.ToLower().Contains(q)
                || (j.Description != null && j.Description.ToLower().Contains(q))
                || (j.Reference != null && j.Reference.ToLower().Contains(q)));
        var jes = await jesQuery
            .OrderByDescending(j => j.EntryDate).Take(200)
            .Select(j => new UnmatchedItem(
                "JournalEntry", j.Id, j.EntryNumber, j.EntryDate,
                j.Description, j.TotalDebit, null))
            .ToListAsync();

        // ----- Documents (Approved receipts / payment vouchers without payment record yet) -----
        var docExclude = usedDocIds.ToList();
        var docsQuery = _db.Documents.AsNoTracking()
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.Status == DocumentStatus.Approved
                && d.DocumentDate >= from && d.DocumentDate < to
                && (d.DocumentType == DocumentType.Receipt
                    || d.DocumentType == DocumentType.PaymentVoucher
                    || d.DocumentType == DocumentType.ReceiptVoucher)
                && !docExclude.Contains(d.Id));
        if (!string.IsNullOrWhiteSpace(q))
            docsQuery = docsQuery.Where(d =>
                d.DocumentNumber.ToLower().Contains(q)
                || (d.Contact != null && d.Contact.Name.ToLower().Contains(q)));
        var docs = await docsQuery
            .OrderByDescending(d => d.DocumentDate).Take(200)
            .Select(d => new UnmatchedItem(
                "Document", d.Id, d.DocumentNumber, d.DocumentDate,
                d.DocumentType + " · " + (d.Contact != null ? d.Contact.Name : ""),
                d.TotalAmount,
                d.Contact != null ? d.Contact.Name : null))
            .ToListAsync();

        return new UnmatchedItemsResponse(payments, jes, docs);
    }

    private async Task<ReconciliationGroupResponse> BuildGroupResponseAsync(Guid companyId, Guid groupId)
    {
        var group = await _db.ReconciliationGroups
            .Include(g => g.Items)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกลุ่มการกระทบยอด");

        var bankTxnIds = group.Items.Where(i => i.ItemType == ReconciliationItemType.BankTransaction).Select(i => i.ItemId).ToList();
        var paymentIds = group.Items.Where(i => i.ItemType == ReconciliationItemType.Payment).Select(i => i.ItemId).ToList();
        var jeIds = group.Items.Where(i => i.ItemType == ReconciliationItemType.JournalEntry).Select(i => i.ItemId).ToList();
        var docIds = group.Items.Where(i => i.ItemType == ReconciliationItemType.Document).Select(i => i.ItemId).ToList();

        var bankMap = (await _db.Set<BankTransaction>()
            .Where(t => bankTxnIds.Contains(t.Id))
            .Select(t => new { t.Id, t.TransactionDate, t.Description, t.Reference })
            .ToListAsync()).ToDictionary(t => t.Id);
        var paymentMap = (await _db.Set<Payment>()
            .Where(p => paymentIds.Contains(p.Id))
            .Select(p => new { p.Id, p.PaymentDate, p.PaymentNumber, p.Notes })
            .ToListAsync()).ToDictionary(p => p.Id);
        var jeMap = (await _db.JournalEntries
            .Where(j => jeIds.Contains(j.Id))
            .Select(j => new { j.Id, j.EntryDate, j.EntryNumber, j.Description })
            .ToListAsync()).ToDictionary(j => j.Id);
        var docMap = (await _db.Documents
            .Where(d => docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentDate, d.DocumentNumber, d.Notes })
            .ToListAsync()).ToDictionary(d => d.Id);

        var responseItems = group.Items.Select(i =>
        {
            string? num = null, desc = null;
            DateTime? date = null;
            switch (i.ItemType)
            {
                case ReconciliationItemType.BankTransaction:
                    if (bankMap.TryGetValue(i.ItemId, out var b))
                    { num = b.Reference; desc = b.Description; date = b.TransactionDate; }
                    break;
                case ReconciliationItemType.Payment:
                    if (paymentMap.TryGetValue(i.ItemId, out var p))
                    { num = p.PaymentNumber; desc = p.Notes; date = p.PaymentDate; }
                    break;
                case ReconciliationItemType.JournalEntry:
                    if (jeMap.TryGetValue(i.ItemId, out var j))
                    { num = j.EntryNumber; desc = j.Description; date = j.EntryDate; }
                    break;
                case ReconciliationItemType.Document:
                    if (docMap.TryGetValue(i.ItemId, out var d))
                    { num = d.DocumentNumber; desc = d.Notes; date = d.DocumentDate; }
                    break;
            }
            return new ReconciliationGroupItemResponse(
                i.Id, i.ItemType.ToString(), i.ItemId, num, desc, date, i.AllocatedAmount, i.Notes);
        }).ToList();

        return new ReconciliationGroupResponse(
            group.Id, group.BankAccountId, group.GroupNumber, group.ReconciledDate,
            group.TotalBankAmount, group.TotalMatchedAmount,
            group.TotalBankAmount - group.TotalMatchedAmount,
            group.IsBalanced, group.Notes, group.CreatedBy, group.CreatedAt, responseItems);
    }
}
