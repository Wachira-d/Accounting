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

    /// <summary>รอบ 259-3 (E-H2): ลิงก์ที่ออกแล้วทั้งหมดของบริษัท (ล่าสุดก่อน) — สถานะ/ป้าย/เพิกถอนได้ไหม จากตัวตัดสินเดียว <see cref="Accounting.Helpers.VendorPortalTokenRules"/></summary>
    Task<IReadOnlyList<VendorPortalTokenRow>> ListAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>รอบ 259-3 (E-H2): ส่งลิงก์ทางอีเมลถึง <c>RecipientEmail</c> ของลิงก์ (ผ่าน <see cref="Accounting.Services.Interfaces.IEmailService.TrySendAsync"/>) —
    /// คืนผลจริง (ไม่ได้ระบุอีเมล · ระบบยังไม่ตั้งอีเมล · ส่งล้ม · ส่งแล้ว) ไม่โยน</summary>
    Task<VendorPortalInviteResult> SendInviteAsync(Guid companyId, VendorPortalToken stored, string absoluteUrl, CancellationToken ct = default);

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

/// <summary>รอบ 259-3 (E-H2): แถวรายการลิงก์พอร์ทัลผู้ขาย — ไม่มี token ดิบ/แฮช</summary>
public sealed record VendorPortalTokenRow(Guid Id, Guid ContactId, string? ContactName, string Role, string RoleLabel,
    DateTime IssuedAt, DateTime ExpiresAt, DateTime? LastUsedAt, DateTime? RevokedAt, string? RevokedReason, string? RecipientEmail,
    string Status, string StatusLabel, bool CanRevoke);

/// <summary>รอบ 259-3 (E-H2): ผลส่งคำเชิญ — <paramref name="Sent"/> true เฉพาะเมื่อ SMTP รับไปจริง</summary>
public sealed record VendorPortalInviteResult(bool Requested, bool Sent, string Note);

public sealed record VendorInvoiceDraft(
    Guid DocumentId, string DocumentNumber, string Status);

public class VendorPortalService : IVendorPortalService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<VendorPortalService> _logger;
    private readonly Accounting.Services.Interfaces.IEmailService? _email;

    public VendorPortalService(AccountingDbContext db, ILogger<VendorPortalService> logger,
        Accounting.Services.Interfaces.IEmailService? email = null)
    { _db = db; _logger = logger; _email = email; }

    public async Task<(string RawToken, VendorPortalToken Stored)> IssueAsync(Guid companyId,
        Guid contactId, string role, int? validDays, string? recipientEmail,
        Guid? issuedByUserId, CancellationToken ct = default)
    {
        // Verify the contact exists in this company before issuing.
        var contactExists = await _db.Contacts.AnyAsync(
            c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted, ct);
        if (!contactExists) throw new InvalidOperationException("ไม่พบผู้ติดต่อ");
        // รอบ 259-3 (E-H2): ลูกค้ามีบ้านเดียว = พอร์ทัลลูกค้า — ออกลิงก์ใหม่ได้เฉพาะผู้ขาย (ลิงก์ลูกค้าเดิมยังใช้ได้จนหมดอายุ)
        if (Accounting.Helpers.VendorPortalTokenRules.IssueRefusal(role) is string refusal)
            throw new InvalidOperationException(refusal);

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
            Role = Accounting.Helpers.VendorPortalTokenRules.RoleVendor,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = expires,
            IssuedByUserId = issuedByUserId,
            RecipientEmail = string.IsNullOrWhiteSpace(recipientEmail) ? null : recipientEmail.Trim(),
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
        if (token == null) throw new InvalidOperationException("ไม่พบลิงก์นี้");
        // รอบ 259-3: เพิกถอนซ้ำ/ลิงก์หมดอายุ = บอกตรง ๆ (ไม่ประทับทับเวลาเดิม)
        var status = Accounting.Helpers.VendorPortalTokenRules.Status(token.RevokedAt, token.ExpiresAt, DateTime.UtcNow);
        if (!Accounting.Helpers.VendorPortalTokenRules.CanRevoke(status))
            throw new InvalidOperationException($"ลิงก์นี้{Accounting.Helpers.VendorPortalTokenRules.StatusLabel(status)} — ใช้งานไม่ได้อยู่แล้ว");
        token.RevokedAt = DateTime.UtcNow;
        token.RevokedReason = string.IsNullOrWhiteSpace(reason) ? "เพิกถอนจากหน้าพอร์ทัลผู้ขาย" : reason.Trim();
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<VendorPortalTokenRow>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        var rows = await _db.VendorPortalTokens.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted)
            .OrderByDescending(t => t.IssuedAt).Take(500)
            .Select(t => new { t.Id, t.ContactId, t.Role, t.IssuedAt, t.ExpiresAt, t.LastUsedAt, t.RevokedAt, t.RevokedReason, t.RecipientEmail })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.ContactId).Distinct().ToList();
        // ชื่อผู้ติดต่อแยก query (รวมที่ถูกลบ) — นำทางใน projection = INNER JOIN ใต้ query filter ⇒ ลิงก์ของผู้ติดต่อที่ถูกลบหายจากรายการ (เพิกถอนไม่ได้)
        var names = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var now = DateTime.UtcNow;
        return rows.Select(r =>
        {
            var st = Accounting.Helpers.VendorPortalTokenRules.Status(r.RevokedAt, r.ExpiresAt, now);
            return new VendorPortalTokenRow(r.Id, r.ContactId, names.GetValueOrDefault(r.ContactId), r.Role,
                Accounting.Helpers.VendorPortalTokenRules.RoleLabel(r.Role), r.IssuedAt, r.ExpiresAt, r.LastUsedAt, r.RevokedAt, r.RevokedReason,
                r.RecipientEmail, st, Accounting.Helpers.VendorPortalTokenRules.StatusLabel(st), Accounting.Helpers.VendorPortalTokenRules.CanRevoke(st));
        }).ToList();
    }

    public async Task<VendorPortalInviteResult> SendInviteAsync(Guid companyId, VendorPortalToken stored, string absoluteUrl, CancellationToken ct = default)
    {
        var to = stored.RecipientEmail;
        if (string.IsNullOrWhiteSpace(to))
            return new VendorPortalInviteResult(false, false, Accounting.Helpers.VendorPortalTokenRules.InviteNote(false, false, false));
        var configured = _email != null && await _email.IsSystemEmailConfiguredAsync();
        if (!configured)
            return new VendorPortalInviteResult(true, false, Accounting.Helpers.VendorPortalTokenRules.InviteNote(true, false, false));
        var companyName = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var contactName = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && c.Id == stored.ContactId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        // กฎ #4 C: ทุกค่าที่ผู้ใช้คุมได้ต่อเข้า HTML ผ่าน HtmlEncode
        var enc = System.Net.WebUtility.HtmlEncode;
        var body = $"<p>เรียน {enc(contactName)}</p>"
            + $"<p>{enc(companyName)} เปิดพอร์ทัลผู้ขายให้ท่านดูใบสั่งซื้อ · ใบแจ้งหนี้ · สถานะการจ่ายเงิน และส่งใบแจ้งหนี้ใหม่ได้โดยไม่ต้องมีบัญชีผู้ใช้</p>"
            + $"<p><a href=\"{enc(absoluteUrl)}\">เปิดพอร์ทัลผู้ขาย</a></p>"
            + $"<p>ลิงก์ใช้ได้ถึง {enc(stored.ExpiresAt.AddHours(7).ToString("d MMM yyyy", new System.Globalization.CultureInfo("th-TH")))} — กรุณาอย่าส่งต่อลิงก์นี้</p>";
        var sent = await _email!.TrySendAsync(to, $"พอร์ทัลผู้ขาย — {companyName}", body);
        return new VendorPortalInviteResult(true, sent, Accounting.Helpers.VendorPortalTokenRules.InviteNote(true, true, sent));
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
