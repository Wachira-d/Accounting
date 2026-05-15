using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using ClosedXML.Excel;
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
            throw new InvalidOperationException(
                "Bank transaction ต่อไปนี้ถูกกระทบยอดไปแล้ว — กรุณายกเลิกการจับคู่เดิมก่อน: " +
                string.Join(", ", alreadyReconciled.Select(t => t.Id.ToString("N")[..8])));

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

        // Balance check (net-off check): bank side = item side ± tolerance
        var diff = Math.Abs(totalBank - totalMatched);
        var tolerance = request.Tolerance > 0 ? request.Tolerance : 0.01m;
        var balanced = diff <= tolerance;
        if (!balanced && diff > 1000m)
            throw new InvalidOperationException(
                $"ยอดสองฝั่งห่างกันเกินไป — Bank {totalBank:N2} vs รายการ {totalMatched:N2} ต่างกัน {diff:N2} บาท. " +
                "หากจงใจให้ไม่ลงตัว ลองปรับ Tolerance หรือเพิ่มรายการค่าธรรมเนียม/ส่วนต่าง.");

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

        await _db.SaveChangesAsync();
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

        using var wb = new XLWorkbook();

        // Sheet 1: Transactions
        var s1 = wb.Worksheets.Add("Transactions");
        var hdr1 = new[] { "วันที่", "ประเภท", "จำนวนเงิน", "ยอดคงเหลือ", "รายละเอียด", "อ้างอิง", "ผู้รับ/ผู้จ่าย", "สถานะกระทบยอด", "Match Type", "Matched #", "เลขกลุ่ม", "วันที่กระทบยอด" };
        for (int i = 0; i < hdr1.Length; i++) { s1.Cell(1, i + 1).Value = hdr1[i]; s1.Cell(1, i + 1).Style.Font.Bold = true; }
        var groupNumberById = groups.ToDictionary(g => g.Id, g => g.GroupNumber);
        int r = 2;
        foreach (var t in txns)
        {
            var matchType = t.ReconciliationGroupId.HasValue ? "Group" :
                t.MatchedPaymentId.HasValue ? "Payment" :
                t.MatchedJournalEntryId.HasValue ? "JournalEntry" :
                !string.IsNullOrWhiteSpace(t.MatchedEntryIdsJson) ? "AI-Aggregate" : "";
            var groupNum = t.ReconciliationGroupId.HasValue && groupNumberById.TryGetValue(t.ReconciliationGroupId.Value, out var gn) ? gn : "";
            s1.Cell(r, 1).Value = t.TransactionDate;
            s1.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            s1.Cell(r, 2).Value = t.TransactionType.ToString();
            s1.Cell(r, 3).Value = t.Amount;
            s1.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00";
            s1.Cell(r, 4).Value = t.BalanceAfter;
            s1.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
            s1.Cell(r, 5).Value = t.Description ?? "";
            s1.Cell(r, 6).Value = t.Reference ?? "";
            s1.Cell(r, 7).Value = t.Payee ?? "";
            s1.Cell(r, 8).Value = t.ReconciliationStatus.ToString();
            s1.Cell(r, 9).Value = matchType;
            s1.Cell(r, 10).Value = t.MatchedPaymentId?.ToString() ?? t.MatchedJournalEntryId?.ToString() ?? "";
            s1.Cell(r, 11).Value = groupNum;
            s1.Cell(r, 12).Value = t.ReconciledAt?.ToString("yyyy-MM-dd") ?? "";
            r++;
        }
        s1.Columns().AdjustToContents();

        // Sheet 2: Reconciliation Groups (M:N detail)
        var s2 = wb.Worksheets.Add("Groups");
        var hdr2 = new[] { "เลขกลุ่ม", "วันที่", "Bank Total", "Matched Total", "ผลต่าง", "สมดุล", "บันทึกย่อ", "ผู้ทำรายการ" };
        for (int i = 0; i < hdr2.Length; i++) { s2.Cell(1, i + 1).Value = hdr2[i]; s2.Cell(1, i + 1).Style.Font.Bold = true; }
        r = 2;
        foreach (var g in groups)
        {
            s2.Cell(r, 1).Value = g.GroupNumber;
            s2.Cell(r, 2).Value = g.ReconciledDate;
            s2.Cell(r, 2).Style.DateFormat.Format = "yyyy-mm-dd";
            s2.Cell(r, 3).Value = g.TotalBankAmount; s2.Cell(r, 3).Style.NumberFormat.Format = "#,##0.00";
            s2.Cell(r, 4).Value = g.TotalMatchedAmount; s2.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
            s2.Cell(r, 5).Value = g.TotalBankAmount - g.TotalMatchedAmount; s2.Cell(r, 5).Style.NumberFormat.Format = "#,##0.00";
            s2.Cell(r, 6).Value = g.IsBalanced ? "✓" : "✗";
            s2.Cell(r, 7).Value = g.Notes ?? "";
            s2.Cell(r, 8).Value = g.CreatedBy ?? "";
            r++;
        }
        s2.Columns().AdjustToContents();

        // Sheet 3: Group Items (the line-level audit detail)
        var s3 = wb.Worksheets.Add("Group Items");
        var hdr3 = new[] { "เลขกลุ่ม", "ประเภทรายการ", "Item ID", "จำนวนเงิน (signed)", "หมายเหตุ" };
        for (int i = 0; i < hdr3.Length; i++) { s3.Cell(1, i + 1).Value = hdr3[i]; s3.Cell(1, i + 1).Style.Font.Bold = true; }
        r = 2;
        foreach (var g in groups)
        {
            foreach (var it in g.Items.OrderBy(i => i.ItemType).ThenByDescending(i => Math.Abs(i.AllocatedAmount)))
            {
                s3.Cell(r, 1).Value = g.GroupNumber;
                s3.Cell(r, 2).Value = it.ItemType.ToString();
                s3.Cell(r, 3).Value = it.ItemId.ToString();
                s3.Cell(r, 4).Value = it.AllocatedAmount; s3.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00;-#,##0.00";
                s3.Cell(r, 5).Value = it.Notes ?? "";
                r++;
            }
        }
        s3.Columns().AdjustToContents();

        // Sheet 4: Summary
        var s4 = wb.Worksheets.Add("Summary");
        s4.Cell(1, 1).Value = "บัญชี";
        s4.Cell(1, 2).Value = $"{account.AccountName} ({account.BankName} {account.AccountNumber})";
        s4.Cell(2, 1).Value = "ช่วงเวลา";
        s4.Cell(2, 2).Value = $"{from:yyyy-MM-dd} ถึง {to.AddDays(-1):yyyy-MM-dd}";
        s4.Cell(3, 1).Value = "ยอดธนาคารปัจจุบัน";
        s4.Cell(3, 2).Value = account.CurrentBalance; s4.Cell(3, 2).Style.NumberFormat.Format = "#,##0.00";
        s4.Cell(4, 1).Value = "รวมรายการในช่วงนี้";
        s4.Cell(4, 2).Value = txns.Count;
        s4.Cell(5, 1).Value = "กระทบยอดแล้ว";
        s4.Cell(5, 2).Value = txns.Count(t => t.ReconciliationStatus == ReconciliationStatus.Matched);
        s4.Cell(6, 1).Value = "ยังไม่กระทบยอด";
        s4.Cell(6, 2).Value = txns.Count(t => t.ReconciliationStatus == ReconciliationStatus.Unmatched);
        s4.Cell(7, 1).Value = "จำนวนกลุ่มกระทบยอด";
        s4.Cell(7, 2).Value = groups.Count;
        s4.Cell(8, 1).Value = "กลุ่มที่สมดุล";
        s4.Cell(8, 2).Value = groups.Count(g => g.IsBalanced);
        s4.Range("A1:A8").Style.Font.Bold = true;
        s4.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
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
