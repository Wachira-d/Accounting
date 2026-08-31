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

        // Lock the bank lines we're about to claim BEFORE re-reading them, so
        // a concurrent CreateReconciliationGroup can't race past the
        // already-reconciled check below. Per-line advisory lock auto-releases
        // at transaction end (caller wraps this method in BeginTransaction).
        var bankTxnIds = request.BankTransactions.Select(b => b.ItemId).Distinct().ToList();
        foreach (var bid in bankTxnIds)
        {
            var lockKey = Accounting.Helpers.AdvisoryLockKey.For(
                companyId, Accounting.Helpers.AdvisoryLockKey.BankReconcile, bid.ToString("N"));
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);
        }
        var bankTxns = await _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId
                && t.BankAccountId == request.BankAccountId
                && bankTxnIds.Contains(t.Id))
            .ToListAsync();

        // Fiscal-period guard: every involved bank line must be in an open period.
        foreach (var t in bankTxns)
            await EnsureFiscalPeriodOpenAsync(companyId, t.TransactionDate, "create-reconciliation-group");
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
            // ห้าม override เกินยอดจริงของรายการเดินบัญชี — ไม่งั้น client กำหนด
            // ตัวเลขได้ทั้งสองฝั่งแล้ว balance check ด้านล่างกลายเป็นการเทียบค่าที่
            // client แต่งเองกับตัวเอง (ผ่านเสมอ) และยอดที่บันทึกลง DB เป็นค่าปลอม
            if (Math.Abs(amount) > Math.Abs(txn.Amount) + 0.01m)
                throw new InvalidOperationException(
                    $"ยอดจัดสรรฝั่งธนาคาร ({amount:N2}) เกินยอดรายการเดินบัญชีจริง "
                    + $"({txn.TransactionDate:dd/MM/yyyy} {txn.Amount:N2})");
            bankAllocations.Add((txn, amount));
            totalBank += amount;
        }

        // ผังบัญชีของธนาคารนี้ — ใช้หา "ขาที่วิ่งผ่านบัญชีนี้" ของ JE หลายขา
        // (เพดานการจัดสรรต้องเป็นขาธนาคาร ไม่ใช่ footing ทั้งใบ)
        var bankGlAccountId = account.LinkedAccountId;

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

            // ยอดจัดสรรต้องไม่เกิน "ยอดจริงของรายการนั้นใน DB" — จัดสรรบางส่วนได้
            // (ใบใหญ่ทยอยตัด) แต่ห้ามเกิน. เดิมเชื่อ AllocatedAmount จาก client
            // ทั้งสองฝั่ง → ยิง payload ให้สองฝั่งเท่ากันเองแล้วผ่าน balance check
            // ได้ทันที (เช็ค 5,000 จับคู่ใบเสร็จ 50 บาท) และยอดที่เก็บลง DB ปลอม
            var realAmount = await ResolveItemAmountAsync(companyId, itemType, item.ItemId, bankGlAccountId);
            if (realAmount > 0 && Math.Abs(item.AllocatedAmount) > realAmount + 0.01m)
                throw new InvalidOperationException(
                    $"ยอดจัดสรรของ {itemType} ({item.AllocatedAmount:N2}) เกินยอดจริงของรายการ ({realAmount:N2}) — "
                    + "จัดสรรบางส่วนได้ แต่ห้ามเกินยอดเอกสาร/รายการต้นทาง");

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

        var groupNumberById = groups.ToDictionary(g => g.Id, g => g.GroupNumber);

        // ── Resolve "จับคู่กับ" เป็นเลขเอกสาร/JE ที่คนอ่านออก (เดิมพิมพ์ GUID
        //    ซึ่งใช้ตรวจอะไรไม่ได้เลย) — batch lookup 3 ตาราง ─────────────────
        var payIds = txns.Where(t => t.MatchedPaymentId.HasValue).Select(t => t.MatchedPaymentId!.Value)
            .Concat(groups.SelectMany(g => g.Items.Where(i => i.ItemType == ReconciliationItemType.Payment).Select(i => i.ItemId)))
            .Distinct().ToList();
        var jeIds = txns.Where(t => t.MatchedJournalEntryId.HasValue).Select(t => t.MatchedJournalEntryId!.Value)
            .Concat(groups.SelectMany(g => g.Items.Where(i => i.ItemType == ReconciliationItemType.JournalEntry).Select(i => i.ItemId)))
            .Distinct().ToList();
        var docIds = groups.SelectMany(g => g.Items.Where(i => i.ItemType == ReconciliationItemType.Document).Select(i => i.ItemId))
            .Distinct().ToList();
        var payNo = new Dictionary<Guid, string>();
        if (payIds.Count > 0)
            payNo = await _db.Set<Payment>().AsNoTracking().IgnoreQueryFilters()
                .Where(p => payIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PaymentNumber);
        var jeNo = new Dictionary<Guid, string>();
        if (jeIds.Count > 0)
            jeNo = await _db.JournalEntries.AsNoTracking().IgnoreQueryFilters()
                .Where(j => jeIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.EntryNumber);
        var docNo = new Dictionary<Guid, string>();
        if (docIds.Count > 0)
            docNo = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => docIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.DocumentNumber);

        static string TypeTh(BankTransactionType t) => t switch
        {
            BankTransactionType.Deposit => "เงินเข้า",
            BankTransactionType.Withdrawal => "เงินออก",
            BankTransactionType.Transfer => "โอน",
            BankTransactionType.Fee => "ค่าธรรมเนียม",
            BankTransactionType.Interest => "ดอกเบี้ย",
            _ => t.ToString(),
        };
        static string StatusTh(ReconciliationStatus s) => s switch
        {
            ReconciliationStatus.Matched => "กระทบแล้ว",
            ReconciliationStatus.Unmatched => "ค้างกระทบ",
            ReconciliationStatus.Excluded => "ไม่นับ",
            ReconciliationStatus.Suggested => "AI เสนอ (รอยืนยัน)",
            _ => s.ToString(),
        };
        static bool IsInflow(BankTransaction t) =>
            t.TransactionType is BankTransactionType.Deposit or BankTransactionType.Interest;

        // ── ยอดตามบัญชี (GL) ของผังที่ผูกไว้ — ให้รายงานเป็น "งบกระทบยอด" จริง
        //    (ยอดธนาคาร vs ยอดตามบัญชี + ผลต่าง) ไม่ใช่แค่ list รายการ ─────────
        decimal? bookBalance = null;
        if (account.LinkedAccountId.HasValue)
        {
            bookBalance = await _db.JournalEntryLines.AsNoTracking()
                .Where(l => !l.IsDeleted && l.AccountId == account.LinkedAccountId.Value
                    && _db.JournalEntries.Any(j => j.Id == l.JournalEntryId
                        && j.CompanyId == companyId && !j.IsDeleted
                        && j.Status == JournalEntryStatus.Posted))
                .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
        }

        string MatchedLabel(BankTransaction t)
        {
            if (t.ReconciliationGroupId.HasValue)
                return groupNumberById.TryGetValue(t.ReconciliationGroupId.Value, out var gn) ? $"กลุ่ม {gn}" : "กลุ่ม";
            if (t.MatchedPaymentId.HasValue)
                return payNo.TryGetValue(t.MatchedPaymentId.Value, out var pn) ? pn : "ใบรับ/จ่ายเงิน";
            if (t.MatchedJournalEntryId.HasValue)
                return jeNo.TryGetValue(t.MatchedJournalEntryId.Value, out var jn) ? jn : "สมุดรายวัน";
            if (!string.IsNullOrWhiteSpace(t.MatchedEntryIdsJson)) return "หลายรายการ (AI)";
            return "";
        }

        // MiniExcel: sheet = List<Dictionary> — ลำดับคอลัมน์ตาม key ของแถวแรก,
        // ลำดับ sheet ตามลำดับใส่ dict → "สรุป" ต้องมาก่อนให้เปิดมาเจอภาพรวมทันที
        var matched = txns.Where(t => t.ReconciliationStatus == ReconciliationStatus.Matched).ToList();
        var pending = txns.Where(t => t.ReconciliationStatus is ReconciliationStatus.Unmatched or ReconciliationStatus.Suggested).ToList();
        var inflow = txns.Where(IsInflow).Sum(t => Math.Abs(t.Amount));
        var outflow = txns.Where(t => !IsInflow(t)).Sum(t => Math.Abs(t.Amount));
        var pendingIn = pending.Where(IsInflow).Sum(t => Math.Abs(t.Amount));
        var pendingOut = pending.Where(t => !IsInflow(t)).Sum(t => Math.Abs(t.Amount));
        var today = DateTime.UtcNow.Date;

        var summary = new List<Dictionary<string, object?>>
        {
            new() { ["รายการ"] = "บัญชี", ["ค่า"] = account.AccountName },
            new() { ["รายการ"] = "ธนาคาร / เลขบัญชี", ["ค่า"] = $"{account.BankName} {account.AccountNumber}" },
            new() { ["รายการ"] = "ช่วงเวลา", ["ค่า"] = $"{from:dd/MM/yyyy} – {to.AddDays(-1):dd/MM/yyyy}" },
            new() { ["รายการ"] = "จัดทำเมื่อ", ["ค่า"] = DateTime.UtcNow.AddHours(7).ToString("dd/MM/yyyy HH:mm") + " น." },
            new() { ["รายการ"] = "", ["ค่า"] = "" },
            new() { ["รายการ"] = "ยอดเงินตามธนาคาร (Statement)", ["ค่า"] = account.CurrentBalance },
            new() { ["รายการ"] = "ยอดเงินตามบัญชี (GL)", ["ค่า"] = (object?)bookBalance ?? "ยังไม่ผูกผังบัญชี" },
            new() { ["รายการ"] = "ผลต่าง ธนาคาร − บัญชี", ["ค่า"] = bookBalance.HasValue ? account.CurrentBalance - bookBalance.Value : (object?)"—" },
            new() { ["รายการ"] = "", ["ค่า"] = "" },
            new() { ["รายการ"] = $"รายการในช่วง ({txns.Count} รายการ)", ["ค่า"] = $"เงินเข้า {inflow:#,##0.00} · เงินออก {outflow:#,##0.00}" },
            new() { ["รายการ"] = $"กระทบยอดแล้ว ({matched.Count})", ["ค่า"] = matched.Sum(t => Math.Abs(t.Amount)) },
            new() { ["รายการ"] = $"ค้างกระทบ ({pending.Count})", ["ค่า"] = $"เงินเข้า {pendingIn:#,##0.00} · เงินออก {pendingOut:#,##0.00}" },
            new() { ["รายการ"] = "กลุ่มกระทบยอด", ["ค่า"] = $"{groups.Count} กลุ่ม (สมดุล {groups.Count(g => g.IsBalanced)} · ไม่สมดุล {groups.Count(g => !g.IsBalanced)})" },
        };

        var txSheet = txns.Select(t => new Dictionary<string, object?>
        {
            ["วันที่"] = t.TransactionDate.ToString("dd/MM/yyyy"),
            ["ประเภท"] = TypeTh(t.TransactionType),
            ["เงินเข้า"] = IsInflow(t) ? Math.Abs(t.Amount) : (object?)"",
            ["เงินออก"] = IsInflow(t) ? (object?)"" : Math.Abs(t.Amount),
            ["ยอดคงเหลือ"] = t.BalanceAfter,
            ["รายละเอียด"] = t.Description ?? "",
            ["อ้างอิง"] = t.Reference ?? "",
            ["ผู้รับ/ผู้จ่าย"] = t.Payee ?? "",
            ["สถานะ"] = StatusTh(t.ReconciliationStatus),
            ["จับคู่กับ"] = MatchedLabel(t),
            ["วันที่กระทบ"] = t.ReconciledAt?.ToString("dd/MM/yyyy") ?? "",
        }).ToList();

        // ค้างกระทบ เรียงเก่าสุดก่อน + อายุ — คือ list งานที่นักบัญชีต้องตามล้าง
        var pendingSheet = pending
            .OrderBy(t => t.TransactionDate)
            .Select(t => new Dictionary<string, object?>
            {
                ["วันที่"] = t.TransactionDate.ToString("dd/MM/yyyy"),
                ["อายุ (วัน)"] = Math.Max(0, (today - t.TransactionDate.Date).Days),
                ["ประเภท"] = TypeTh(t.TransactionType),
                ["เงินเข้า"] = IsInflow(t) ? Math.Abs(t.Amount) : (object?)"",
                ["เงินออก"] = IsInflow(t) ? (object?)"" : Math.Abs(t.Amount),
                ["รายละเอียด"] = t.Description ?? "",
                ["อ้างอิง"] = t.Reference ?? "",
                ["สถานะ"] = StatusTh(t.ReconciliationStatus),
            }).ToList();

        var groupSheet = groups.Select(g => new Dictionary<string, object?>
        {
            ["เลขกลุ่ม"] = g.GroupNumber,
            ["วันที่"] = g.ReconciledDate.ToString("dd/MM/yyyy"),
            ["ยอดฝั่งธนาคาร"] = g.TotalBankAmount,
            ["ยอดฝั่งเอกสาร"] = g.TotalMatchedAmount,
            ["ผลต่าง"] = g.TotalBankAmount - g.TotalMatchedAmount,
            ["สมดุล"] = g.IsBalanced ? "สมดุล" : "ไม่สมดุล",
            ["บันทึกย่อ"] = g.Notes ?? "",
        }).ToList();

        var itemSheet = groups.SelectMany(g =>
            g.Items
                .OrderBy(i => i.ItemType).ThenByDescending(i => Math.Abs(i.AllocatedAmount))
                .Select(it => new Dictionary<string, object?>
                {
                    ["เลขกลุ่ม"] = g.GroupNumber,
                    ["ประเภท"] = it.ItemType switch
                    {
                        ReconciliationItemType.Payment => "ใบรับ/จ่ายเงิน",
                        ReconciliationItemType.JournalEntry => "สมุดรายวัน",
                        ReconciliationItemType.Document => "เอกสาร",
                        _ => it.ItemType.ToString(),
                    },
                    ["เลขที่"] = it.ItemType switch
                    {
                        ReconciliationItemType.Payment => payNo.GetValueOrDefault(it.ItemId, it.ItemId.ToString("N")[..8]),
                        ReconciliationItemType.JournalEntry => jeNo.GetValueOrDefault(it.ItemId, it.ItemId.ToString("N")[..8]),
                        ReconciliationItemType.Document => docNo.GetValueOrDefault(it.ItemId, it.ItemId.ToString("N")[..8]),
                        _ => it.ItemId.ToString("N")[..8],
                    },
                    ["จำนวนเงิน (+รับ/−จ่าย)"] = it.AllocatedAmount,
                    ["หมายเหตุ"] = it.Notes ?? "",
                })
        ).ToList();

        // sheet ว่าง = ไฟล์ดูเหมือนพัง — ใส่แถวบอกสถานะแทน
        static List<Dictionary<string, object?>> OrEmpty(List<Dictionary<string, object?>> rows, string note)
            => rows.Count > 0 ? rows : new() { new() { ["หมายเหตุ"] = note } };

        var sheets = new Dictionary<string, object>
        {
            ["สรุป"] = summary,
            ["รายการเดินบัญชี"] = OrEmpty(txSheet, "ไม่มีรายการเดินบัญชีในช่วงเวลานี้ — นำเข้า Statement ก่อน หรือขยายช่วงเวลา"),
            ["ค้างกระทบยอด"] = OrEmpty(pendingSheet, "ไม่มีรายการค้างกระทบ 🎉"),
            ["กลุ่มกระทบยอด"] = OrEmpty(groupSheet, "ไม่มีกลุ่มกระทบยอดในช่วงเวลานี้"),
            ["รายการในกลุ่ม"] = OrEmpty(itemSheet, "ไม่มีรายการในกลุ่ม"),
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

    /// <summary>ยอดจริงของรายการฝั่งที่นำมากระทบ (absolute) — ใช้เป็นเพดานของ
    /// AllocatedAmount ที่ client ส่งมา. คืน 0 เมื่อหาไม่ได้ (ผู้เรียกจะข้ามการ
    /// ตรวจ ไม่ block งานที่ยังจับคู่ได้จริง).</summary>
    private async Task<decimal> ResolveItemAmountAsync(Guid companyId, ReconciliationItemType type, Guid id,
        Guid? bankGlAccountId = null)
    {
        switch (type)
        {
            case ReconciliationItemType.Payment:
                return await _db.Set<Payment>().AsNoTracking()
                    .Where(p => p.Id == id && p.CompanyId == companyId)
                    .Select(p => p.Amount).FirstOrDefaultAsync();

            case ReconciliationItemType.Document:
                return await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == id && d.CompanyId == companyId)
                    .Select(d => d.TotalAmount).FirstOrDefaultAsync();

            case ReconciliationItemType.JournalEntry:
                // "ขนาด" ของ JE ในบริบทกระทบยอด = ขาที่วิ่งผ่านบัญชีธนาคารนี้
                // ไม่ใช่ footing ทั้งใบ (JV เงินเดือน footing 77,678 แต่ออกจาก
                // ธนาคารจริง 70,110) — ใช้ footing เป็นเพดานคือปล่อยให้จัดสรร
                // เกินยอดที่แตะธนาคารจริงได้ 7,568 บาทโดยไม่มีอะไรค้าน
                // ต้องหาแบบเดียวกับ GetUnmatchedItemsAsync ทุกประการ (2 ชั้น:
                // ผังที่ผูกกับบัญชีนี้ → ผังเงินสด/ธนาคารใด ๆ 111x) ไม่งั้น
                // ตัวเลขที่โชว์กับเพดานที่ตรวจจะคนละตัว = ผู้ใช้เห็นยอดถูกแต่
                // กดยืนยันแล้วโดนปฏิเสธ
                var cashLegs = await _db.JournalEntryLines.AsNoTracking()
                    .Where(l => l.JournalEntryId == id
                        && (l.Account.AccountCode.StartsWith("111")
                            || (bankGlAccountId != null && l.AccountId == bankGlAccountId)))
                    .Select(l => new { l.AccountId, Net = l.DebitAmount - l.CreditAmount })
                    .ToListAsync();
                if (cashLegs.Count > 0)
                {
                    var exact = bankGlAccountId.HasValue
                        ? cashLegs.Where(l => l.AccountId == bankGlAccountId.Value).ToList()
                        : new();
                    var net = Math.Abs((exact.Count > 0 ? exact : cashLegs).Sum(l => l.Net));
                    if (net > 0.009m) return net;
                }
                // ไม่มีขาเงินสด/ธนาคารเลย → คงพฤติกรรมเดิมด้วย footing
                // (ไม่บล็อกการจับคู่ที่เคยทำได้ แต่ UI ขึ้นป้ายเตือนไว้แล้ว)
                return await _db.JournalEntries.AsNoTracking()
                    .Where(j => j.Id == id && j.CompanyId == companyId)
                    .Select(j => j.TotalDebit).FirstOrDefaultAsync();

            default:
                return 0m;
        }
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

        // ค้นด้วยชื่อ contact: pre-resolve ContactId (IgnoreQueryFilters — รวม
        // ที่ถูก soft-delete) แทนการ join Contact ที่ !IsDeleted ตรง ๆ เพราะ
        // Contact เป็น required nav + มี query filter !IsDeleted → join = INNER
        // JOIN ตัด payment/document ที่ contact ถูกลบทิ้งเงียบ ๆ.
        var searchContactIds = string.IsNullOrWhiteSpace(q)
            ? new List<Guid>()
            : await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                .Where(c => c.CompanyId == companyId && c.Name.ToLower().Contains(q))
                .Select(c => c.Id).ToListAsync();

        // ----- Payments -----
        var paymentExclude = usedPaymentIds.ToList();
        var paymentsQuery = _db.Set<Payment>().AsNoTracking()
            .Include(p => p.Document)
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                && p.PaymentDate >= from && p.PaymentDate < to
                && p.Document.Status != DocumentStatus.Voided
                && !paymentExclude.Contains(p.Id));
        if (!string.IsNullOrWhiteSpace(q))
            paymentsQuery = paymentsQuery.Where(p =>
                p.PaymentNumber.ToLower().Contains(q)
                || (p.Notes != null && p.Notes.ToLower().Contains(q))
                || searchContactIds.Contains(p.Document.ContactId));
        var paymentRows = await paymentsQuery
            .OrderByDescending(p => p.PaymentDate).Take(200)
            .Select(p => new
            {
                p.Id, p.PaymentNumber, p.PaymentDate, p.Notes,
                DocNumber = p.Document.DocumentNumber, p.Amount, p.Document.ContactId
            })
            .ToListAsync();
        // reattach ชื่อ contact (รวมที่ถูกลบ) — ไม่ให้ join ตัดแถว
        var paymentContactNames = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId
                && paymentRows.Select(r => r.ContactId).Distinct().Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);
        var payments = paymentRows.Select(r => new UnmatchedItem(
            "Payment", r.Id, r.PaymentNumber, r.PaymentDate,
            r.Notes ?? r.DocNumber, r.Amount,
            paymentContactNames.GetValueOrDefault(r.ContactId))).ToList();

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
        var jeRows = await jesQuery
            .OrderByDescending(j => j.EntryDate).Take(200)
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.Description, j.TotalDebit })
            .ToListAsync();

        // ── ยอดที่ต้องตรงกับสเตทเมนต์ = "ขาที่วิ่งผ่านบัญชีธนาคารนี้" ──
        // เดิมใช้ TotalDebit (footing ของ JE ทั้งใบ) ซึ่งผิดกับ JE หลายขา:
        //   JV เงินเดือน  Dr เงินเดือน 77,678
        //                   Cr ประกันสังคมค้างจ่าย  3,750
        //                   Cr ภ.ง.ด.1 ค้างจ่าย     3,818
        //                   Cr ธนาคาร             70,110   ← ตัวนี้เท่านั้นที่ออกจริง
        // การโชว์ 77,678 ทำให้ผู้ใช้เข้าใจว่า "JE ลงไม่ตรงกับที่จ่ายจริง"
        // ทั้งที่ JE ถูกต้อง — ส่วนต่างคือหนี้ค้างจ่ายที่ยังไม่ถึงกำหนดนำส่ง
        // หา 2 ชั้น เพราะ JE จำนวนมากไม่ได้ลงผังของบัญชีธนาคารนี้ตรง ๆ:
        //   ชั้น 1 — บรรทัดที่ลงผังที่ผูกกับบัญชีธนาคารนี้ (LinkedAccountId) = แม่นสุด
        //   ชั้น 2 — บรรทัดที่ลงผังเงินสด/ธนาคารใด ๆ (รหัสขึ้นต้น 111) เมื่อชั้น 1
        //     ไม่เจอ. เคสจริง: JE เงินเดือนที่ยิงมาจากระบบภายนอกมัก Cr ผัง
        //     "เงินฝากธนาคาร" กลาง ไม่ใช่ผังของบัญชีรายตัว — ถ้าไม่รองรับชั้นนี้
        //     ยอดจะตกกลับไปเป็น footing ทั้งใบซึ่งไม่มีวันตรงกับสเตทเมนต์
        // เก็บผังที่ใช้จริงไว้ด้วย เพื่อบอกผู้ใช้ว่า JE ลงบัญชีไหน (แก้ที่ต้นทาง
        // หรือผูกผังให้ถูกได้) แทนที่จะบอกแค่ "ไม่ตรง"
        var jeBankLeg = new Dictionary<Guid, decimal>();
        var jeLegAccount = new Dictionary<Guid, string>();
        var jeLegIsExact = new HashSet<Guid>();
        if (jeRows.Count > 0)
        {
            var jeIdList = jeRows.Select(j => j.Id).ToList();
            var cashLines = await _db.JournalEntryLines.AsNoTracking()
                .Where(l => jeIdList.Contains(l.JournalEntryId)
                    && (l.Account.AccountCode.StartsWith("111")
                        || (account.LinkedAccountId != null && l.AccountId == account.LinkedAccountId)))
                .Select(l => new
                {
                    l.JournalEntryId, l.AccountId, l.DebitAmount, l.CreditAmount,
                    l.Account.AccountCode, l.Account.AccountName,
                })
                .ToListAsync();

            foreach (var g in cashLines.GroupBy(l => l.JournalEntryId))
            {
                var exact = account.LinkedAccountId.HasValue
                    ? g.Where(l => l.AccountId == account.LinkedAccountId.Value).ToList()
                    : new();
                var use = exact.Count > 0 ? exact : g.ToList();
                var net = use.Sum(l => l.DebitAmount - l.CreditAmount);   // + เงินเข้า / − เงินออก
                if (Math.Abs(net) <= 0.009m) continue;
                jeBankLeg[g.Key] = net;
                var acc = use[0];
                jeLegAccount[g.Key] = $"{acc.AccountCode} {acc.AccountName}";
                if (exact.Count > 0) jeLegIsExact.Add(g.Key);
            }
        }

        var jes = jeRows.Select(j =>
        {
            var hasLeg = jeBankLeg.TryGetValue(j.Id, out var leg);
            var note = "";
            if (hasLeg)
            {
                // ส่วนต่างจาก footing = ขาอื่นในใบเดียวกัน (ค้างจ่าย/หักกลบ)
                // บอกที่มาไว้ในบรรทัดเลย ผู้ใช้จะได้ไม่ต้องเปิด JE ไปไล่เอง
                if (Math.Abs(Math.Abs(leg) - j.TotalDebit) > 0.009m)
                    note += $" · ยอด JE ทั้งใบ {j.TotalDebit:N2} (ส่วนต่างเป็นรายการค้างจ่าย/หักกลบในใบเดียวกัน)";
                // ลงผังเงินสด/ธนาคารตัวอื่น ไม่ใช่ผังของบัญชีนี้ — actionable
                if (!jeLegIsExact.Contains(j.Id) && jeLegAccount.TryGetValue(j.Id, out var accName))
                    note += $" · ขาเงินสด/ธนาคารในใบนี้ลงผัง {accName}";
            }
            return new UnmatchedItem(
                "JournalEntry", j.Id, j.EntryNumber, j.EntryDate,
                j.Description + note,
                hasLeg ? Math.Abs(leg) : j.TotalDebit,
                null,
                hasLeg ? leg : null);
        }).ToList();

        // ----- Documents (Approved receipts / payment vouchers without payment record yet) -----
        var docExclude = usedDocIds.ToList();
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — projection
        // ดึง ContactId แล้ว reattach ชื่อจาก IgnoreQueryFilters
        var docsQuery = _db.Documents.AsNoTracking()
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
                || searchContactIds.Contains(d.ContactId));
        var docRows = await docsQuery
            .OrderByDescending(d => d.DocumentDate).Take(200)
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentDate, d.DocumentType,
                d.TotalAmount, d.ContactId
            })
            .ToListAsync();
        var docContactNames = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId
                && docRows.Select(r => r.ContactId).Distinct().Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);
        var docs = docRows.Select(d =>
        {
            var name = docContactNames.GetValueOrDefault(d.ContactId);
            return new UnmatchedItem(
                "Document", d.Id, d.DocumentNumber, d.DocumentDate,
                d.DocumentType + " · " + (name ?? ""),
                d.TotalAmount, name);
        }).ToList();

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
