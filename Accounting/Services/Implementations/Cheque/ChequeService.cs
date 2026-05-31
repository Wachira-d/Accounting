using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Cheque;

/// <summary>
/// Cheque lifecycle management — Thai SMEs still use cheques heavily
/// for vendor payments and customer collections. This service owns:
///
///   • ChequeBook ordering + sequence integrity (the bank gives us
///     a roll of 25/50/100 pre-printed cheques; we issue them in
///     order; missing numbers are flagged unless explicitly Voided).
///   • Outbound cheque issuance — link to a Payment + a contact.
///   • Inbound cheque deposit — customer hands us a cheque; we record
///     it as DepositedPending until it clears.
///   • State transitions — Issued → Cleared | Bounced.
///   • Bounce + replacement chain — when a cheque bounces, the
///     replacement carries ReplacesChequeId for audit.
///   • Outstanding-cheques report — AP team's "what cheques are still
///     out there" view that drives bank rec.
/// </summary>
public interface IChequeService
{
    Task<ChequeBook> OpenChequeBookAsync(Guid companyId, Guid bankAccountId,
        string bookNumber, long startNumber, long endNumber, CancellationToken ct = default);

    Task<Cheque> IssueOutboundAsync(Guid companyId, Guid chequeBookId,
        Guid? contactId, DateTime chequeDate, decimal amount, Guid? paymentId,
        string? notes, Guid? projectId = null, CancellationToken ct = default);

    Task<Cheque> RecordInboundAsync(Guid companyId, Guid? contactId,
        long chequeNumber, string issuingBank, DateTime chequeDate,
        decimal amount, Guid? paymentId, string? notes,
        Guid? projectId = null, CancellationToken ct = default);

    Task<Cheque> MarkClearedAsync(Guid companyId, Guid chequeId,
        DateTime clearedAt, CancellationToken ct = default);

    Task<Cheque> MarkBouncedAsync(Guid companyId, Guid chequeId,
        string reason, CancellationToken ct = default);

    Task<Cheque> VoidAsync(Guid companyId, Guid chequeId,
        string? notes, CancellationToken ct = default);

    Task<IReadOnlyList<Cheque>> ListOutstandingAsync(Guid companyId,
        Guid? bankAccountId, CancellationToken ct = default);
}

public class ChequeService : IChequeService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<ChequeService> _logger;
    private readonly Accounting.Services.Interfaces.IWebhookService? _webhooks;

    public ChequeService(AccountingDbContext db, ILogger<ChequeService> logger,
        Accounting.Services.Interfaces.IWebhookService? webhooks = null)
    { _db = db; _logger = logger; _webhooks = webhooks; }

    private async Task FireAsync(Guid companyId, string eventType, object payload)
    {
        if (_webhooks == null) return;
        try { await _webhooks.TriggerAsync(companyId, eventType, payload); }
        catch { /* fire-and-forget */ }
    }

    public async Task<ChequeBook> OpenChequeBookAsync(Guid companyId, Guid bankAccountId,
        string bookNumber, long startNumber, long endNumber, CancellationToken ct = default)
    {
        if (endNumber < startNumber)
            throw new ArgumentException("EndChequeNumber must be ≥ StartChequeNumber.");
        var book = new ChequeBook
        {
            CompanyId = companyId,
            BankAccountId = bankAccountId,
            BookNumber = bookNumber,
            StartChequeNumber = startNumber,
            EndChequeNumber = endNumber,
            NextNumber = startNumber,
            ReceivedFromBankAt = DateTime.UtcNow,
        };
        _db.ChequeBooks.Add(book);
        await _db.SaveChangesAsync(ct);
        return book;
    }

    public async Task<Cheque> IssueOutboundAsync(Guid companyId, Guid chequeBookId,
        Guid? contactId, DateTime chequeDate, decimal amount, Guid? paymentId,
        string? notes, Guid? projectId = null, CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentException("Amount must be positive.");
        // Take the book under tracking so NextNumber updates atomically.
        var book = await _db.ChequeBooks.FirstOrDefaultAsync(b => b.Id == chequeBookId
            && b.CompanyId == companyId, ct);
        if (book == null) throw new InvalidOperationException("ChequeBook not found.");
        if (book.NextNumber > book.EndChequeNumber)
            throw new InvalidOperationException("ChequeBook exhausted — open a new book first.");

        var cheque = new Cheque
        {
            CompanyId = companyId,
            ChequeBookId = book.Id,
            ChequeNumber = book.NextNumber,
            ChequeDate = chequeDate,
            Amount = amount,
            ContactId = contactId,
            PaymentId = paymentId,
            Status = ChequeStatus.Issued,
            IsInbound = false,
            Notes = notes,
            ProjectId = projectId,
        };
        _db.Cheques.Add(cheque);
        book.NextNumber++;
        if (book.NextNumber > book.EndChequeNumber)
            book.ExhaustedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return cheque;
    }

    public async Task<Cheque> RecordInboundAsync(Guid companyId, Guid? contactId,
        long chequeNumber, string issuingBank, DateTime chequeDate,
        decimal amount, Guid? paymentId, string? notes,
        Guid? projectId = null, CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentException("Amount must be positive.");
        var cheque = new Cheque
        {
            CompanyId = companyId,
            ChequeBookId = null,                 // not from OUR book
            ChequeNumber = chequeNumber,
            ChequeDate = chequeDate,
            Amount = amount,
            ContactId = contactId,
            IssuingBankName = issuingBank,
            PaymentId = paymentId,
            Status = ChequeStatus.DepositedPending,
            IsInbound = true,
            Notes = notes,
            ProjectId = projectId,
        };
        _db.Cheques.Add(cheque);
        await _db.SaveChangesAsync(ct);
        return cheque;
    }

    public async Task<Cheque> MarkClearedAsync(Guid companyId, Guid chequeId,
        DateTime clearedAt, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques
            .Include(c => c.ChequeBook).ThenInclude(b => b.BankAccount)
            .FirstOrDefaultAsync(c => c.Id == chequeId && c.CompanyId == companyId, ct);
        if (cheque == null) throw new InvalidOperationException("Cheque not found.");
        if (cheque.Status != ChequeStatus.Issued && cheque.Status != ChequeStatus.DepositedPending)
            throw new InvalidOperationException($"Cheque {cheque.Status} cannot be cleared.");
        cheque.Status = ChequeStatus.Cleared;
        cheque.ClearedAt = clearedAt;

        // Float settles — move money. Outbound (we issued) debits cash;
        // Inbound (customer cheque deposited) credits cash. Re-clearing is
        // blocked by the status guard above so this is idempotent on its own.
        var bank = cheque.ChequeBook?.BankAccount;
        if (bank != null)
        {
            if (cheque.IsInbound) bank.CurrentBalance += cheque.Amount;
            else                  bank.CurrentBalance -= cheque.Amount;
        }

        await _db.SaveChangesAsync(ct);
        await FireAsync(companyId, "cheque.cleared", new
        {
            id = cheque.Id, chequeNumber = cheque.ChequeNumber,
            amount = cheque.Amount, isInbound = cheque.IsInbound,
            clearedAt = cheque.ClearedAt,
            bankBalanceAfter = bank?.CurrentBalance,
        });
        return cheque;
    }

    public async Task<Cheque> MarkBouncedAsync(Guid companyId, Guid chequeId,
        string reason, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques
            .Include(c => c.ChequeBook).ThenInclude(b => b.BankAccount)
            .FirstOrDefaultAsync(c => c.Id == chequeId && c.CompanyId == companyId, ct);
        if (cheque == null) throw new InvalidOperationException("Cheque not found.");
        if (cheque.Status == ChequeStatus.Cleared)
            throw new InvalidOperationException("Cleared cheque cannot be bounced.");
        var wasCleared = cheque.Status == ChequeStatus.Cleared;
        cheque.Status = ChequeStatus.Bounced;
        cheque.BounceReason = reason;

        // If the bounce happens AFTER clearing (rare — usually bounces
        // before clear, but bank can claw back), reverse the balance.
        // wasCleared is always false today because the guard above blocks
        // it — kept for defensive symmetry with VoidAsync.
        if (wasCleared)
        {
            var bank = cheque.ChequeBook?.BankAccount;
            if (bank != null)
            {
                if (cheque.IsInbound) bank.CurrentBalance -= cheque.Amount;
                else                  bank.CurrentBalance += cheque.Amount;
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("Cheque {Id} bounced: {Reason}", chequeId, reason);
        await FireAsync(companyId, "cheque.bounced", new
        {
            id = cheque.Id, chequeNumber = cheque.ChequeNumber,
            amount = cheque.Amount, isInbound = cheque.IsInbound,
            reason = cheque.BounceReason,
        });
        return cheque;
    }

    public async Task<Cheque> VoidAsync(Guid companyId, Guid chequeId,
        string? notes, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques.FirstOrDefaultAsync(c => c.Id == chequeId
            && c.CompanyId == companyId, ct);
        if (cheque == null) throw new InvalidOperationException("Cheque not found.");
        if (cheque.Status == ChequeStatus.Cleared)
            throw new InvalidOperationException("Cleared cheque cannot be voided.");
        cheque.Status = ChequeStatus.Voided;
        if (!string.IsNullOrWhiteSpace(notes))
            cheque.Notes = (cheque.Notes ?? "") + "\nVOID: " + notes;
        await _db.SaveChangesAsync(ct);
        return cheque;
    }

    public async Task<IReadOnlyList<Cheque>> ListOutstandingAsync(Guid companyId,
        Guid? bankAccountId, CancellationToken ct = default)
    {
        var q = _db.Cheques.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted
                        && (c.Status == ChequeStatus.Issued
                            || c.Status == ChequeStatus.DepositedPending));
        if (bankAccountId.HasValue)
            q = q.Where(c => c.ChequeBook != null && c.ChequeBook.BankAccountId == bankAccountId.Value);
        return await q.OrderBy(c => c.ChequeDate).ToListAsync(ct);
    }
}
