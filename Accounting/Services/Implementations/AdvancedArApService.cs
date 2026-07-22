using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.AdvancedArAp;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class AdvancedArApService : IAdvancedArApService
{
    private readonly AccountingDbContext _db;

    public AdvancedArApService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Credit Management =====

    public async Task<CreditSettingResponse> SetCreditLimitAsync(Guid companyId, Guid contactId, SetCreditLimitRequest request)
    {
        var contact = await _db.Contacts
            .FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");

        var setting = await _db.Set<ContactCreditSetting>()
            .FirstOrDefaultAsync(s => s.ContactId == contactId && s.CompanyId == companyId);

        if (setting == null)
        {
            setting = new ContactCreditSetting
            {
                CompanyId = companyId,
                ContactId = contactId,
                CreditLimit = request.CreditLimit,
                CreditTermDays = request.CreditTermDays,
                CreditRating = request.CreditRating,
                AvailableCredit = request.CreditLimit,
                LastReviewDate = DateTime.UtcNow
            };
            _db.Set<ContactCreditSetting>().Add(setting);
        }
        else
        {
            setting.CreditLimit = request.CreditLimit;
            setting.CreditTermDays = request.CreditTermDays;
            if (request.CreditRating != null) setting.CreditRating = request.CreditRating;
            setting.AvailableCredit = request.CreditLimit - setting.CurrentBalance;
            setting.LastReviewDate = DateTime.UtcNow;
            setting.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return MapToCreditResponse(setting, contact.Name);
    }

    public async Task<CreditSettingResponse> GetCreditSettingAsync(Guid companyId, Guid contactId)
    {
        var setting = await _db.Set<ContactCreditSetting>()
            .FirstOrDefaultAsync(s => s.ContactId == contactId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบการตั้งค่าเครดิต");

        // ไม่ Include Contact (required nav + !IsDeleted filter → INNER JOIN
        // ตัดแถวที่ contact ถูกลบ) — reattach เอง (รวมที่ถูก soft-delete)
        setting.Contact = (await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == setting.ContactId))!;

        // Recalculate outstanding from all AR docs (handles CN net).
        var outstandingBalance = await ComputeContactOutstandingArAsync(companyId, contactId);
        setting.CurrentBalance = outstandingBalance;
        setting.AvailableCredit = setting.CreditLimit - outstandingBalance;
        await _db.SaveChangesAsync();

        return MapToCreditResponse(setting, setting.Contact?.Name ?? string.Empty);
    }

    /// <summary>Computes net AR for a contact — sum of open (Invoice +
    /// TaxInvoice + BillingNote + DebitNote) BalanceDue minus CreditNote
    /// BalanceDue. States that don't represent debt (Draft/Waiting/Rejected/
    /// Voided/Paid) are excluded. Used by credit setting + credit check.</summary>
    private async Task<decimal> ComputeContactOutstandingArAsync(Guid companyId, Guid contactId)
    {
        var positiveTypes = new[] {
            DocumentType.Invoice, DocumentType.TaxInvoice,
            DocumentType.BillingNote, DocumentType.DebitNote };
        var openStates = new[] {
            DocumentStatus.Approved, DocumentStatus.Sent,
            DocumentStatus.PartiallyPaid, DocumentStatus.Overdue };
        var positive = await _db.Documents
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId
                && !d.IsDeleted
                && positiveTypes.Contains(d.DocumentType)
                && openStates.Contains(d.Status)
                && d.BalanceDue > 0)
            .SumAsync(d => d.BalanceDue);
        var negative = await _db.Documents
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId
                && !d.IsDeleted
                && d.DocumentType == DocumentType.CreditNote
                && openStates.Contains(d.Status)
                && d.BalanceDue > 0)
            .SumAsync(d => d.BalanceDue);
        return Math.Max(0, positive - negative);
    }

    public async Task<List<CreditSettingResponse>> GetAllCreditSettingsAsync(Guid companyId)
    {
        var settings = await _db.Set<ContactCreditSetting>().AsNoTracking()
            // ไม่ Include/OrderBy Contact ใน SQL (required nav + !IsDeleted →
            // INNER JOIN ตัดแถวที่ contact ถูกลบ) — reattach + sort ใน memory
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .ToListAsync();
        if (settings.Count == 0) return new();

        var settingContactIds = settings.Select(s => s.ContactId).Distinct().ToList();
        var contactMap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && settingContactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);
        foreach (var s in settings)
            if (contactMap.TryGetValue(s.ContactId, out var c)) s.Contact = c;
        settings = settings.OrderBy(s => s.Contact?.Name).ToList();

        // Compute outstanding balances for all contacts in ONE batched query
        // (instead of one query per setting) — replaces the previously stale
        // CurrentBalance / AvailableCredit returned from raw entity rows.
        var contactIds = settings.Select(s => s.ContactId).ToList();
        var positiveTypes = new[] {
            DocumentType.Invoice, DocumentType.TaxInvoice,
            DocumentType.BillingNote, DocumentType.DebitNote };
        var openStates = new[] {
            DocumentStatus.Approved, DocumentStatus.Sent,
            DocumentStatus.PartiallyPaid, DocumentStatus.Overdue };
        var openDocs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && contactIds.Contains(d.ContactId)
                && openStates.Contains(d.Status)
                && d.BalanceDue > 0
                && (positiveTypes.Contains(d.DocumentType)
                    || d.DocumentType == DocumentType.CreditNote))
            .Select(d => new { d.ContactId, d.DocumentType, d.BalanceDue })
            .ToListAsync();
        var outstandingByContact = openDocs
            .GroupBy(d => d.ContactId)
            .ToDictionary(g => g.Key, g => Math.Max(0,
                g.Where(x => x.DocumentType != DocumentType.CreditNote).Sum(x => x.BalanceDue)
                - g.Where(x => x.DocumentType == DocumentType.CreditNote).Sum(x => x.BalanceDue)));

        foreach (var s in settings)
        {
            var bal = outstandingByContact.GetValueOrDefault(s.ContactId, 0m);
            s.CurrentBalance = bal;
            s.AvailableCredit = s.CreditLimit - bal;
        }
        return settings.Select(s => MapToCreditResponse(s, s.Contact?.Name ?? string.Empty)).ToList();
    }

    public async Task<CreditCheckResponse> CheckCreditAsync(Guid companyId, Guid contactId, decimal amount)
    {
        var setting = await _db.Set<ContactCreditSetting>()
            .FirstOrDefaultAsync(s => s.ContactId == contactId && s.CompanyId == companyId);

        if (setting == null)
        {
            // No credit setting means no restriction
            return new CreditCheckResponse(true, amount, decimal.MaxValue, 0, 0, null);
        }

        if (setting.IsOnHold)
        {
            return new CreditCheckResponse(false, amount, setting.AvailableCredit,
                setting.CurrentBalance, setting.CreditLimit,
                $"ผู้ติดต่อถูกระงับ: {setting.HoldReason}");
        }

        var outstandingBalance = await ComputeContactOutstandingArAsync(companyId, contactId);

        var available = setting.CreditLimit - outstandingBalance;
        var isApproved = amount <= available;

        string? reason = null;
        if (!isApproved)
            reason = $"วงเงินเครดิตไม่เพียงพอ (คงเหลือ {available:N2} บาท)";

        return new CreditCheckResponse(isApproved, amount, available,
            outstandingBalance, setting.CreditLimit, reason);
    }

    public async Task HoldContactAsync(Guid companyId, Guid contactId, string reason)
    {
        var setting = await _db.Set<ContactCreditSetting>()
            .FirstOrDefaultAsync(s => s.ContactId == contactId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบการตั้งค่าเครดิต");

        setting.IsOnHold = true;
        setting.HoldReason = reason;
        setting.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task ReleaseHoldAsync(Guid companyId, Guid contactId)
    {
        var setting = await _db.Set<ContactCreditSetting>()
            .FirstOrDefaultAsync(s => s.ContactId == contactId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบการตั้งค่าเครดิต");

        setting.IsOnHold = false;
        setting.HoldReason = null;
        setting.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ===== Dunning =====

    public async Task<DunningLetterResponse> GenerateDunningLetterAsync(Guid companyId, Guid contactId, int level)
    {
        var contact = await _db.Contacts
            .FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");

        // Get overdue invoices
        var today = DateTime.UtcNow.Date;
        var overdueInvoices = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && d.ContactId == contactId
                && d.DocumentType == DocumentType.Invoice
                && d.Status != DocumentStatus.Voided
                && d.BalanceDue > 0
                && d.DueDate.HasValue
                && d.DueDate.Value < today)
            .OrderBy(d => d.DueDate)
            .ToListAsync();

        if (!overdueInvoices.Any())
            throw new InvalidOperationException("ไม่มีใบแจ้งหนี้ค้างชำระที่เกินกำหนด");

        var dunYm = DateTime.UtcNow.ToString("yyyyMM");
        var dunPrefix = $"DUN-{dunYm}-";
        var maxDun = await _db.Set<DunningLetter>()
            .IgnoreQueryFilters()
            .Where(d => d.CompanyId == companyId && d.LetterNumber.StartsWith(dunPrefix))
            .Select(d => d.LetterNumber)
            .MaxAsync() as string;
        var dunSeq = 1;
        if (maxDun != null)
        {
            var lastPart = maxDun.Substring(dunPrefix.Length);
            if (int.TryParse(lastPart, out var parsed)) dunSeq = parsed + 1;
        }
        var letterNumber = $"{dunPrefix}{dunSeq:D4}";

        var totalOverdue = overdueInvoices.Sum(d => d.BalanceDue);
        var oldestDays = (int)(today - overdueInvoices.Min(d => d.DueDate!.Value)).TotalDays;

        var letter = new DunningLetter
        {
            CompanyId = companyId,
            LetterNumber = letterNumber,
            ContactId = contactId,
            DunningLevel = level,
            LetterDate = today,
            TotalOverdueAmount = totalOverdue,
            OldestOverdueDays = oldestDays,
            Status = "Draft"
        };

        _db.Set<DunningLetter>().Add(letter);

        foreach (var inv in overdueInvoices)
        {
            var overdueDays = (int)(today - inv.DueDate!.Value).TotalDays;
            _db.Set<DunningLetterLine>().Add(new DunningLetterLine
            {
                CompanyId = companyId,
                DunningLetterId = letter.Id,
                DocumentId = inv.Id,
                DocumentNumber = inv.DocumentNumber,
                DocumentDate = inv.DocumentDate,
                DueDate = inv.DueDate!.Value,
                Amount = inv.TotalAmount,
                PaidAmount = inv.PaidAmount,
                OverdueAmount = inv.BalanceDue,
                OverdueDays = overdueDays
            });
        }

        await _db.SaveChangesAsync();

        return new DunningLetterResponse(
            letter.Id, letter.LetterNumber, contactId, contact.Name,
            level, letter.LetterDate, totalOverdue, oldestDays,
            letter.Status, letter.SentAt, overdueInvoices.Count);
    }

    public async Task<DunningLetterResponse> SendDunningLetterAsync(Guid companyId, Guid letterId, string channel)
    {
        var letter = await _db.Set<DunningLetter>()
            .Include(l => l.Lines)
            .FirstOrDefaultAsync(l => l.Id == letterId && l.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบจดหมายทวงหนี้");

        // ไม่ Include Contact (INNER JOIN ตัดแถวที่ contact ถูกลบ) — reattach เอง
        letter.Contact = (await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == letter.ContactId))!;

        letter.Status = "Sent";
        letter.SentAt = DateTime.UtcNow;
        letter.SentVia = channel;
        letter.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return new DunningLetterResponse(
            letter.Id, letter.LetterNumber, letter.ContactId, letter.Contact?.Name ?? string.Empty,
            letter.DunningLevel, letter.LetterDate, letter.TotalOverdueAmount,
            letter.OldestOverdueDays, letter.Status, letter.SentAt, letter.Lines.Count);
    }

    public async Task<PagedResponse<DunningLetterResponse>> GetDunningLettersAsync(Guid companyId, PagedRequest request)
    {
        // ไม่ Include Contact (INNER JOIN ตัดแถวที่ contact ถูกลบ). ค้นด้วยชื่อ
        // contact ทำผ่าน pre-resolve ContactId (IgnoreQueryFilters) แทน join.
        var query = _db.Set<DunningLetter>().AsNoTracking()
            .Include(l => l.Lines)
            .Where(l => l.CompanyId == companyId && !l.IsDeleted);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var matchContactIds = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                .Where(c => c.CompanyId == companyId && c.Name.Contains(request.Search))
                .Select(c => c.Id).ToListAsync();
            query = query.Where(l => l.LetterNumber.Contains(request.Search)
                || matchContactIds.Contains(l.ContactId));
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(l => l.LetterDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        // reattach Contact (รวมที่ถูก soft-delete) กัน row หาย
        var itemContactIds = items.Select(l => l.ContactId).Distinct().ToList();
        var itemContactMap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && itemContactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);
        foreach (var l in items)
            if (itemContactMap.TryGetValue(l.ContactId, out var c)) l.Contact = c;

        var responses = items.Select(l => new DunningLetterResponse(
            l.Id, l.LetterNumber, l.ContactId, l.Contact?.Name ?? string.Empty,
            l.DunningLevel, l.LetterDate, l.TotalOverdueAmount,
            l.OldestOverdueDays, l.Status, l.SentAt, l.Lines.Count)).ToList();

        return new PagedResponse<DunningLetterResponse>(
            responses, total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    // ===== Payment Reminders =====

    public async Task<int> SendPaymentRemindersAsync(Guid companyId, int daysBefore = 3)
    {
        var today = DateTime.UtcNow.Date;
        var reminderDate = today.AddDays(daysBefore);

        // Find invoices due within the reminder window
        var upcomingInvoices = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && d.DocumentType == DocumentType.Invoice
                && d.Status != DocumentStatus.Voided
                && d.BalanceDue > 0
                && d.DueDate.HasValue
                && d.DueDate.Value <= reminderDate
                && d.DueDate.Value >= today)
            .ToListAsync();

        var sentCount = 0;
        foreach (var inv in upcomingInvoices)
        {
            // Check if reminder already sent for this invoice recently
            var recentReminder = await _db.Set<PaymentReminder>()
                .AnyAsync(r => r.DocumentId == inv.Id
                    && r.CompanyId == companyId
                    && r.ReminderDate >= today.AddDays(-7));

            if (recentReminder) continue;

            var priorCount = await _db.Set<PaymentReminder>()
                .CountAsync(r => r.DocumentId == inv.Id && r.CompanyId == companyId);

            var reminder = new PaymentReminder
            {
                CompanyId = companyId,
                DocumentId = inv.Id,
                ContactId = inv.ContactId,
                ReminderLevel = priorCount + 1,
                ReminderDate = today,
                Status = "Sent",
                SentAt = DateTime.UtcNow,
                Channel = "Email"
            };

            _db.Set<PaymentReminder>().Add(reminder);
            sentCount++;
        }

        await _db.SaveChangesAsync();
        return sentCount;
    }

    public async Task<List<PaymentReminderResponse>> GetRemindersAsync(Guid companyId, Guid? documentId = null)
    {
        var query = _db.Set<PaymentReminder>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted);

        if (documentId.HasValue)
            query = query.Where(r => r.DocumentId == documentId.Value);

        var reminders = await query
            .OrderByDescending(r => r.ReminderDate)
            .Take(100)
            .ToListAsync();

        // Fetch related documents and contacts in batch
        var docIds = reminders.Select(r => r.DocumentId).Distinct().ToList();
        var contactIds = reminders.Select(r => r.ContactId).Distinct().ToList();

        var documents = await _db.Documents.AsNoTracking()
            .Where(d => docIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.DocumentNumber);

        var contacts = await _db.Contacts.AsNoTracking()
            .Where(c => contactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        return reminders.Select(r => new PaymentReminderResponse(
            r.Id, r.DocumentId,
            documents.GetValueOrDefault(r.DocumentId, ""),
            r.ContactId,
            contacts.GetValueOrDefault(r.ContactId, ""),
            r.ReminderLevel, r.ReminderDate, r.Status, r.SentAt)).ToList();
    }

    // ===== Statements =====

    public async Task<StatementResponse> GenerateStatementAsync(Guid companyId, Guid contactId, DateTime fromDate, DateTime toDate)
    {
        var contact = await _db.Contacts
            .FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");

        // Get opening balance (outstanding balance before fromDate)
        var openingBalance = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && d.ContactId == contactId
                && d.DocumentType == DocumentType.Invoice
                && d.Status != DocumentStatus.Voided
                && d.DocumentDate < fromDate)
            .SumAsync(d => d.BalanceDue);

        // Get all documents in the period
        var invoices = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId
                && d.ContactId == contactId
                && d.DocumentType == DocumentType.Invoice
                && d.Status != DocumentStatus.Voided
                && d.DocumentDate >= fromDate
                && d.DocumentDate <= toDate)
            .OrderBy(d => d.DocumentDate)
            .ToListAsync();

        // Get all payments in the period
        var payments = await _db.Payments.AsNoTracking()
            .Include(p => p.Document)
            .Where(p => p.CompanyId == companyId
                && p.Document!.ContactId == contactId
                && p.PaymentDate >= fromDate
                && p.PaymentDate <= toDate)
            .OrderBy(p => p.PaymentDate)
            .ToListAsync();

        var lines = new List<StatementLine>();
        var runningBalance = openingBalance;

        // Merge invoices and payments chronologically
        var allEntries = invoices.Select(i => new { Date = i.DocumentDate, IsInvoice = true, Doc = (Document?)i, Pay = (Payment?)null })
            .Concat(payments.Select(p => new { Date = p.PaymentDate, IsInvoice = false, Doc = (Document?)null, Pay = (Payment?)p }))
            .OrderBy(e => e.Date)
            .ToList();

        foreach (var entry in allEntries)
        {
            if (entry.IsInvoice && entry.Doc != null)
            {
                runningBalance += entry.Doc.TotalAmount;
                lines.Add(new StatementLine(
                    entry.Doc.DocumentDate,
                    entry.Doc.DocumentNumber,
                    $"ใบแจ้งหนี้",
                    entry.Doc.TotalAmount, 0, runningBalance));
            }
            else if (entry.Pay != null)
            {
                runningBalance -= entry.Pay.Amount;
                lines.Add(new StatementLine(
                    entry.Pay.PaymentDate,
                    entry.Pay.PaymentNumber ?? "",
                    $"รับชำระเงิน",
                    0, entry.Pay.Amount, runningBalance));
            }
        }

        var totalInvoiced = invoices.Sum(i => i.TotalAmount);
        var totalPaid = payments.Sum(p => p.Amount);

        return new StatementResponse(
            contactId, contact.Name, fromDate, toDate,
            openingBalance, totalInvoiced, totalPaid,
            runningBalance, lines);
    }

    // ===== Mapping Helpers =====

    private static CreditSettingResponse MapToCreditResponse(ContactCreditSetting s, string contactName) =>
        new(s.ContactId, contactName, s.CreditLimit, s.CreditTermDays,
            s.CurrentBalance, s.AvailableCredit, s.CreditRating,
            s.IsOnHold, s.HoldReason);
}
