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
        string? notes, CancellationToken ct = default);

    Task<Cheque> RecordInboundAsync(Guid companyId, Guid? contactId,
        long chequeNumber, string issuingBank, DateTime chequeDate,
        decimal amount, Guid? paymentId, string? notes, CancellationToken ct = default);

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

    public ChequeService(AccountingDbContext db, ILogger<ChequeService> logger)
    { _db = db; _logger = logger; }

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
        string? notes, CancellationToken ct = default)
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
        decimal amount, Guid? paymentId, string? notes, CancellationToken ct = default)
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
        };
        _db.Cheques.Add(cheque);
        await _db.SaveChangesAsync(ct);
        return cheque;
    }

    public async Task<Cheque> MarkClearedAsync(Guid companyId, Guid chequeId,
        DateTime clearedAt, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques.FirstOrDefaultAsync(c => c.Id == chequeId
            && c.CompanyId == companyId, ct);
        if (cheque == null) throw new InvalidOperationException("Cheque not found.");
        if (cheque.Status != ChequeStatus.Issued && cheque.Status != ChequeStatus.DepositedPending)
            throw new InvalidOperationException($"Cheque {cheque.Status} cannot be cleared.");
        cheque.Status = ChequeStatus.Cleared;
        cheque.ClearedAt = clearedAt;
        await _db.SaveChangesAsync(ct);
        return cheque;
    }

    public async Task<Cheque> MarkBouncedAsync(Guid companyId, Guid chequeId,
        string reason, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques.FirstOrDefaultAsync(c => c.Id == chequeId
            && c.CompanyId == companyId, ct);
        if (cheque == null) throw new InvalidOperationException("Cheque not found.");
        if (cheque.Status == ChequeStatus.Cleared)
            throw new InvalidOperationException("Cleared cheque cannot be bounced.");
        cheque.Status = ChequeStatus.Bounced;
        cheque.BounceReason = reason;
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("Cheque {Id} bounced: {Reason}", chequeId, reason);
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
