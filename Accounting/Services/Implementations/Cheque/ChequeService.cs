using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
// Alias the Cheque entity — the file's own namespace
// (Accounting.Services.Implementations.Cheque) shadows the unqualified
// name "Cheque" via outer-namespace resolution, so we use this alias
// in method signatures + var initialisers to disambiguate.
using ChequeEntity = Accounting.Models.Entities.Cheque;

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

    Task<ChequeEntity> IssueOutboundAsync(Guid companyId, Guid chequeBookId,
        Guid? contactId, DateTime chequeDate, decimal amount, Guid? paymentId,
        string? notes, Guid? projectId = null, CancellationToken ct = default);

    Task<ChequeEntity> RecordInboundAsync(Guid companyId, Guid? contactId,
        long chequeNumber, string issuingBank, DateTime chequeDate,
        decimal amount, Guid? paymentId, string? notes,
        Guid? projectId = null,
        /// <summary>Our bank account that the cheque is deposited into.
        /// When omitted, MarkCleared logs but doesn't update bank balance —
        /// caller can patch later. Strongly encouraged for proper bank rec.</summary>
        Guid? depositBankAccountId = null,
        CancellationToken ct = default);

    Task<ChequeEntity> MarkClearedAsync(Guid companyId, Guid chequeId,
        DateTime clearedAt, CancellationToken ct = default);

    Task<ChequeEntity> MarkBouncedAsync(Guid companyId, Guid chequeId,
        string reason, CancellationToken ct = default);

    Task<ChequeEntity> VoidAsync(Guid companyId, Guid chequeId,
        string? notes, CancellationToken ct = default);

    Task<IReadOnlyList<ChequeEntity>> ListOutstandingAsync(Guid companyId,
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

    public async Task<ChequeEntity> IssueOutboundAsync(Guid companyId, Guid chequeBookId,
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

        var cheque = new ChequeEntity
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

    public async Task<ChequeEntity> RecordInboundAsync(Guid companyId, Guid? contactId,
        long chequeNumber, string issuingBank, DateTime chequeDate,
        decimal amount, Guid? paymentId, string? notes,
        Guid? projectId = null,
        Guid? depositBankAccountId = null, CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentException("Amount must be positive.");
        var cheque = new ChequeEntity
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
            DepositBankAccountId = depositBankAccountId,
        };
        _db.Cheques.Add(cheque);
        await _db.SaveChangesAsync(ct);
        return cheque;
    }

    public async Task<ChequeEntity> MarkClearedAsync(Guid companyId, Guid chequeId,
        DateTime clearedAt, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques
            .Include(c => c.ChequeBook).ThenInclude(b => b!.BankAccount)
            .Include(c => c.DepositBankAccount)
            .FirstOrDefaultAsync(c => c.Id == chequeId && c.CompanyId == companyId, ct);
        if (cheque == null) throw new InvalidOperationException("Cheque not found.");
        if (cheque.Status != ChequeStatus.Issued && cheque.Status != ChequeStatus.DepositedPending)
            throw new InvalidOperationException($"Cheque {cheque.Status} cannot be cleared.");
        cheque.Status = ChequeStatus.Cleared;
        cheque.ClearedAt = clearedAt;

        // Float settles — move money. Outbound debits the cheque book's
        // BankAccount; inbound credits the DepositBankAccount the
        // operator specified at deposit time. Re-clearing is blocked by
        // the status guard above so this is idempotent on its own.
        // When the inbound deposit bank wasn't recorded, we log + skip
        // the balance side rather than silently mismatch.
        var bank = cheque.IsInbound
            ? cheque.DepositBankAccount
            : cheque.ChequeBook?.BankAccount;
        if (bank != null)
        {
            if (cheque.IsInbound) bank.CurrentBalance += cheque.Amount;
            else                  bank.CurrentBalance -= cheque.Amount;
        }
        else if (cheque.IsInbound)
        {
            _logger.LogWarning("Inbound cheque {Id} cleared with no DepositBankAccountId — bank balance NOT updated", chequeId);
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

    public async Task<ChequeEntity> MarkBouncedAsync(Guid companyId, Guid chequeId,
        string reason, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques
            .FirstOrDefaultAsync(c => c.Id == chequeId && c.CompanyId == companyId, ct);
        if (cheque == null) throw new InvalidOperationException("Cheque not found.");
        if (cheque.Status == ChequeStatus.Cleared)
            throw new InvalidOperationException("Cleared cheque cannot be bounced.");
        cheque.Status = ChequeStatus.Bounced;
        cheque.BounceReason = reason;

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

    public async Task<ChequeEntity> VoidAsync(Guid companyId, Guid chequeId,
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

    public async Task<IReadOnlyList<ChequeEntity>> ListOutstandingAsync(Guid companyId,
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
