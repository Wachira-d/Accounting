using System.Security.Cryptography;
using System.Text;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Portal;

/// <summary>
/// Vendor (and customer) portal — token-based access for counterparties
/// who don't have user accounts in the tenant. AP issues a magic-link
/// token tied to a Contact; the recipient hits /vendor-portal.html with
/// that token in the URL fragment, and gets a scoped view of:
///   • Their open POs awaiting fulfilment.
///   • Their submitted invoices + payment status (paid / scheduled).
///   • An upload form to submit a new invoice (creates a PurchaseInvoice
///     draft for the AP team to review).
///
/// Token format: 32-byte URL-safe random; stored as SHA-256 hash so a
/// DB leak can't replay. Expiry default 90 days; admin can shorten.
/// </summary>
public interface IVendorPortalService
{
    Task<(string RawToken, VendorPortalToken Stored)> IssueAsync(Guid companyId,
        Guid contactId, string role, int? validDays, string? recipientEmail,
        Guid? issuedByUserId, CancellationToken ct = default);

    Task<VendorPortalToken?> ResolveAsync(string rawToken, CancellationToken ct = default);

    Task RevokeAsync(Guid companyId, Guid tokenId, string reason, CancellationToken ct = default);

    Task<VendorScopedSummary> GetSummaryAsync(VendorPortalToken token,
        CancellationToken ct = default);

    Task<VendorInvoiceDraft> SubmitInvoiceAsync(VendorPortalToken token,
        string invoiceNumber, DateTime invoiceDate, decimal totalAmount,
        string? notes, string? attachmentUrl, CancellationToken ct = default);
}

public sealed record VendorScopedSummary(
    string ContactName,
    string Role,
    IReadOnlyList<VendorScopedDocument> OpenPos,
    IReadOnlyList<VendorScopedDocument> Invoices,
    IReadOnlyList<VendorScopedPayment> Payments);

public sealed record VendorScopedDocument(
    string Id, string Number, string Type, DateTime Date,
    decimal TotalAmount, decimal BalanceDue, string Status);

public sealed record VendorScopedPayment(
    string Id, DateTime Date, decimal Amount, string Method,
    string? Reference, string? RelatedDocumentNumber);

public sealed record VendorInvoiceDraft(
    Guid DocumentId, string DocumentNumber, string Status);

public class VendorPortalService : IVendorPortalService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<VendorPortalService> _logger;

    public VendorPortalService(AccountingDbContext db, ILogger<VendorPortalService> logger)
    { _db = db; _logger = logger; }

    public async Task<(string RawToken, VendorPortalToken Stored)> IssueAsync(Guid companyId,
        Guid contactId, string role, int? validDays, string? recipientEmail,
        Guid? issuedByUserId, CancellationToken ct = default)
    {
        // Verify the contact exists in this company before issuing.
        var contactExists = await _db.Contacts.AnyAsync(
            c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted, ct);
        if (!contactExists) throw new InvalidOperationException("Contact not found.");

        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Convert.ToBase64String(rawBytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = ComputeHash(rawToken);
        var expires = DateTime.UtcNow.AddDays(Math.Clamp(validDays ?? 90, 1, 365));
        var stored = new VendorPortalToken
        {
            CompanyId = companyId,
            TokenHash = hash,
            ContactId = contactId,
            Role = role ?? "Vendor",
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = expires,
            IssuedByUserId = issuedByUserId,
            RecipientEmail = recipientEmail,
        };
        _db.VendorPortalTokens.Add(stored);
        await _db.SaveChangesAsync(ct);
        return (rawToken, stored);
    }

    public async Task<VendorPortalToken?> ResolveAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = ComputeHash(rawToken);
        var token = await _db.VendorPortalTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && !t.IsDeleted, ct);
        if (token == null) return null;
        if (token.RevokedAt.HasValue) return null;
        if (token.ExpiresAt < DateTime.UtcNow) return null;
        // ไม่ Include Contact (required nav + !IsDeleted → INNER JOIN ตัด token ที่
        // contact ถูกลบ = ปฏิเสธ vendor ผิด ๆ) — reattach เอง ผ่าน token.CompanyId
        token.Contact = (await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.CompanyId == token.CompanyId && c.Id == token.ContactId, ct))!;
        // Touch LastUsedAt — soft write, ignore failure.
        try
        {
            token.LastUsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Touch LastUsedAt failed"); }
        return token;
    }

    public async Task RevokeAsync(Guid companyId, Guid tokenId, string reason, CancellationToken ct = default)
    {
        var token = await _db.VendorPortalTokens.FirstOrDefaultAsync(
            t => t.Id == tokenId && t.CompanyId == companyId, ct);
        if (token == null) throw new InvalidOperationException("Token not found.");
        token.RevokedAt = DateTime.UtcNow;
        token.RevokedReason = reason;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<VendorScopedSummary> GetSummaryAsync(VendorPortalToken token,
        CancellationToken ct = default)
    {
        var contactId = token.ContactId;
        var companyId = token.CompanyId;
        var contactName = token.Contact?.Name ?? "";

        // For Vendor role: show their POs + their submitted purchase
        // invoices + payment status. For Customer role: show invoices
        // we issued + receipts received.
        var isVendor = token.Role == "Vendor";

        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId
                        && !d.IsDeleted
                        && d.Status != DocumentStatus.Voided)
            .OrderByDescending(d => d.DocumentDate)
            .Take(100)
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate,
                d.TotalAmount, d.BalanceDue, d.Status,
            })
            .ToListAsync(ct);

        IEnumerable<VendorScopedDocument> openPos, invoices;
        if (isVendor)
        {
            openPos = docs.Where(d => d.DocumentType == DocumentType.PurchaseOrder
                                      && d.BalanceDue > 0)
                .Select(d => new VendorScopedDocument(
                    d.Id.ToString(), d.DocumentNumber, d.DocumentType.ToString(),
                    d.DocumentDate, d.TotalAmount, d.BalanceDue, d.Status.ToString()));
            invoices = docs.Where(d => d.DocumentType == DocumentType.PurchaseInvoice
                                       || d.DocumentType == DocumentType.Expense)
                .Select(d => new VendorScopedDocument(
                    d.Id.ToString(), d.DocumentNumber, d.DocumentType.ToString(),
                    d.DocumentDate, d.TotalAmount, d.BalanceDue, d.Status.ToString()));
        }
        else
        {
            openPos = Array.Empty<VendorScopedDocument>();
            invoices = docs.Where(d => d.DocumentType == DocumentType.Invoice
                                       || d.DocumentType == DocumentType.TaxInvoice)
                .Select(d => new VendorScopedDocument(
                    d.Id.ToString(), d.DocumentNumber, d.DocumentType.ToString(),
                    d.DocumentDate, d.TotalAmount, d.BalanceDue, d.Status.ToString()));
        }

        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                        && p.Document.ContactId == contactId)
            .OrderByDescending(p => p.PaymentDate)
            .Take(50)
            .Select(p => new VendorScopedPayment(
                p.Id.ToString(), p.PaymentDate, p.Amount,
                p.PaymentMethod.ToString(),
                p.Reference,
                p.Document.DocumentNumber))
            .ToListAsync(ct);

        return new VendorScopedSummary(
            ContactName: contactName,
            Role: token.Role,
            OpenPos: openPos.ToList(),
            Invoices: invoices.ToList(),
            Payments: payments);
    }

    public async Task<VendorInvoiceDraft> SubmitInvoiceAsync(VendorPortalToken token,
        string invoiceNumber, DateTime invoiceDate, decimal totalAmount,
        string? notes, string? attachmentUrl, CancellationToken ct = default)
    {
        if (token.Role != "Vendor")
            throw new InvalidOperationException("Customer-role tokens cannot submit invoices.");
        if (totalAmount <= 0) throw new ArgumentException("Total must be positive.");
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            throw new ArgumentException("Invoice number required.");

        var doc = new Models.Entities.Document
        {
            CompanyId = token.CompanyId,
            ContactId = token.ContactId,
            DocumentNumber = invoiceNumber,
            DocumentType = DocumentType.PurchaseInvoice,
            DocumentDate = invoiceDate,
            TotalAmount = totalAmount,
            BalanceDue = totalAmount,
            Status = DocumentStatus.Draft,
            Notes = $"[Vendor portal submission]" +
                (string.IsNullOrEmpty(notes) ? "" : "\n" + notes) +
                (string.IsNullOrEmpty(attachmentUrl) ? "" : $"\nAttachment: {attachmentUrl}"),
            CreatedBy = $"VendorPortal:{token.Id}",
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync(ct);
        return new VendorInvoiceDraft(doc.Id, doc.DocumentNumber, doc.Status.ToString());
    }

    private static string ComputeHash(string raw)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
