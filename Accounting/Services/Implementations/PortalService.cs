using System.Security.Cryptography;
using System.Text;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Portal;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class PortalService : IPortalService
{
    private readonly AccountingDbContext _db;
    private readonly IPdfGenerationService? _pdfService;
    private readonly string _portalSigningKey;

    public PortalService(AccountingDbContext db, IConfiguration configuration, IPdfGenerationService? pdfService = null)
    {
        _db = db;
        _pdfService = pdfService;
        _portalSigningKey = Environment.GetEnvironmentVariable("PORTAL_SIGNING_KEY")
            ?? configuration["Portal:SigningKey"]
            ?? throw new InvalidOperationException("Portal signing key is not configured. Set PORTAL_SIGNING_KEY env var or Portal:SigningKey in config.");
    }

    // ===== Portal Access Management =====

    public async Task<PortalAccessResponse> CreateAccessAsync(Guid companyId, CreatePortalAccessRequest request)
    {
        var contact = await _db.Contacts
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == request.ContactId)
            ?? throw new InvalidOperationException("Contact not found.");

        var existing = await _db.Set<PortalAccess>()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Email == request.Email);

        if (existing != null)
            throw new InvalidOperationException("A portal access with this email already exists.");

        var access = new PortalAccess
        {
            CompanyId = companyId,
            ContactId = request.ContactId,
            Email = request.Email,
            PasswordHash = HashPassword(request.Password),
            DisplayName = request.DisplayName,
            CanViewInvoices = request.CanViewInvoices,
            CanViewStatements = request.CanViewStatements,
            CanDownloadPdf = request.CanDownloadPdf,
            CanMakePayment = request.CanMakePayment,
            IsActive = true
        };

        _db.Set<PortalAccess>().Add(access);
        await _db.SaveChangesAsync();

        return MapToAccessResponse(access, contact.Name);
    }

    public async Task<List<PortalAccessResponse>> GetAccessesAsync(Guid companyId)
    {
        return await _db.Set<PortalAccess>()
            .Include(p => p.Contact)
            .Where(p => p.CompanyId == companyId)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new PortalAccessResponse(
                p.Id, p.ContactId, p.Contact.Name, p.Email, p.DisplayName,
                p.IsActive, p.LastLoginAt,
                p.CanViewInvoices, p.CanViewStatements, p.CanDownloadPdf, p.CanMakePayment))
            .ToListAsync();
    }

    public async Task<PortalAccessResponse> UpdateAccessAsync(Guid companyId, Guid accessId, UpdatePortalAccessRequest request)
    {
        var access = await _db.Set<PortalAccess>()
            .Include(p => p.Contact)
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == accessId)
            ?? throw new InvalidOperationException("Portal access not found.");

        if (request.CanViewInvoices.HasValue) access.CanViewInvoices = request.CanViewInvoices.Value;
        if (request.CanViewStatements.HasValue) access.CanViewStatements = request.CanViewStatements.Value;
        if (request.CanDownloadPdf.HasValue) access.CanDownloadPdf = request.CanDownloadPdf.Value;
        if (request.CanMakePayment.HasValue) access.CanMakePayment = request.CanMakePayment.Value;
        if (request.IsActive.HasValue) access.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();

        return MapToAccessResponse(access, access.Contact.Name);
    }

    public async Task DeactivateAccessAsync(Guid companyId, Guid accessId)
    {
        var access = await _db.Set<PortalAccess>()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == accessId)
            ?? throw new InvalidOperationException("Portal access not found.");

        access.IsActive = false;
        await _db.SaveChangesAsync();
    }

    // ===== Portal Auth =====

    public async Task<PortalLoginResponse> LoginAsync(PortalLoginRequest request)
    {
        var access = await _db.Set<PortalAccess>()
            .Include(p => p.Contact)
            .FirstOrDefaultAsync(p => p.CompanyId == request.CompanyId
                                   && p.Email == request.Email
                                   && p.IsActive)
            ?? throw new InvalidOperationException("Invalid email or password.");

        if (!VerifyPassword(request.Password, access.PasswordHash))
            throw new InvalidOperationException("Invalid email or password.");

        var company = await _db.Companies
            .FirstOrDefaultAsync(c => c.Id == request.CompanyId)
            ?? throw new InvalidOperationException("Company not found.");

        // Update last login
        access.LastLoginAt = DateTime.UtcNow;

        // Log portal activity
        _db.Set<PortalActivity>().Add(new PortalActivity
        {
            CompanyId = request.CompanyId,
            PortalAccessId = access.Id,
            ActivityType = "Login",
            ActivityAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        // Generate JWT tokens
        var accessToken = GenerateJwtToken(access.Id, access.ContactId, request.CompanyId, TimeSpan.FromHours(2));
        var refreshToken = GenerateJwtToken(access.Id, access.ContactId, request.CompanyId, TimeSpan.FromDays(30));

        return new PortalLoginResponse(
            accessToken,
            refreshToken,
            access.ContactId,
            access.Contact.Name,
            company.Id,
            company.Name);
    }

    public async Task<PortalLoginResponse> RefreshTokenAsync(string refreshToken)
    {
        // Decode the refresh token to extract claims
        var (portalAccessId, contactId, companyId) = DecodeJwtToken(refreshToken);

        var access = await _db.Set<PortalAccess>()
            .Include(p => p.Contact)
            .FirstOrDefaultAsync(p => p.Id == portalAccessId && p.IsActive)
            ?? throw new InvalidOperationException("Portal access not found or inactive.");

        var company = await _db.Companies
            .FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new InvalidOperationException("Company not found.");

        // Generate new tokens
        var newAccessToken = GenerateJwtToken(access.Id, access.ContactId, companyId, TimeSpan.FromHours(2));
        var newRefreshToken = GenerateJwtToken(access.Id, access.ContactId, companyId, TimeSpan.FromDays(30));

        return new PortalLoginResponse(
            newAccessToken,
            newRefreshToken,
            access.ContactId,
            access.Contact.Name,
            company.Id,
            company.Name);
    }

    // ===== Portal Data =====

    public async Task<List<PortalDocumentResponse>> GetMyDocumentsAsync(Guid companyId, Guid contactId, string? documentType = null)
    {
        var query = _db.Documents
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId);

        if (!string.IsNullOrWhiteSpace(documentType) && Enum.TryParse<DocumentType>(documentType, out var docType))
            query = query.Where(d => d.DocumentType == docType);

        return await query
            .OrderByDescending(d => d.DocumentDate)
            .Select(d => new PortalDocumentResponse(
                d.Id, d.DocumentNumber, d.DocumentType.ToString(),
                d.DocumentDate, d.DueDate, d.TotalAmount, d.PaidAmount,
                d.BalanceDue, d.Status.ToString()))
            .ToListAsync();
    }

    public async Task<PortalDocumentResponse> GetDocumentAsync(Guid companyId, Guid contactId, Guid documentId)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.ContactId == contactId && d.Id == documentId)
            ?? throw new InvalidOperationException("Document not found.");

        return new PortalDocumentResponse(
            doc.Id, doc.DocumentNumber, doc.DocumentType.ToString(),
            doc.DocumentDate, doc.DueDate, doc.TotalAmount, doc.PaidAmount,
            doc.BalanceDue, doc.Status.ToString());
    }

    public async Task<byte[]> DownloadDocumentPdfAsync(Guid companyId, Guid contactId, Guid documentId)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.ContactId == contactId && d.Id == documentId)
            ?? throw new InvalidOperationException("Document not found.");

        // Log the download activity
        var portalAccess = await _db.Set<PortalAccess>()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.ContactId == contactId && p.IsActive);

        if (portalAccess != null)
        {
            _db.Set<PortalActivity>().Add(new PortalActivity
            {
                CompanyId = companyId,
                PortalAccessId = portalAccess.Id,
                ActivityType = "DownloadPdf",
                EntityId = documentId,
                EntityType = "Document",
                ActivityAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        // Delegate to PdfGenerationService if available
        if (_pdfService != null)
        {
            var pdfResponse = await _pdfService.GenerateDocumentPdfAsync(companyId,
                new Models.DTOs.DocumentTemplate.GeneratePdfRequest(documentId, null, null, null, null));
            return pdfResponse.PdfData;
        }

        // Fallback: generate basic PDF
        var html = $"<html><body><h2>{doc.DocumentNumber}</h2><p>Date: {doc.DocumentDate:dd/MM/yyyy}</p><p>Total: {doc.TotalAmount:N2}</p></body></html>";
        return PdfGenerationService.ConvertHtmlToPdf(html, null);
    }

    public async Task<PortalStatementResponse> GetMyStatementAsync(Guid companyId, Guid contactId, DateTime fromDate, DateTime toDate)
    {
        var documents = await _db.Documents
            .Where(d => d.CompanyId == companyId
                     && d.ContactId == contactId
                     && d.DocumentDate >= fromDate
                     && d.DocumentDate <= toDate)
            .OrderBy(d => d.DocumentDate)
            .ToListAsync();

        // Calculate opening balance from documents before the period
        var openingBalance = await _db.Documents
            .Where(d => d.CompanyId == companyId
                     && d.ContactId == contactId
                     && d.DocumentDate < fromDate)
            .SumAsync(d => d.BalanceDue);

        var lines = new List<PortalStatementLine>();
        var runningBalance = openingBalance;

        foreach (var doc in documents)
        {
            // Add document charge
            if (doc.DocumentType == DocumentType.Invoice || doc.DocumentType == DocumentType.TaxInvoice)
            {
                runningBalance += doc.TotalAmount;
                lines.Add(new PortalStatementLine(
                    doc.DocumentDate, doc.DocumentNumber, $"Invoice - {doc.DocumentNumber}",
                    doc.TotalAmount, runningBalance));
            }

            // Add payments against this document
            var payments = await _db.Payments
                .Where(p => p.DocumentId == doc.Id && p.PaymentDate >= fromDate && p.PaymentDate <= toDate)
                .OrderBy(p => p.PaymentDate)
                .ToListAsync();

            foreach (var payment in payments)
            {
                runningBalance -= payment.Amount;
                lines.Add(new PortalStatementLine(
                    payment.PaymentDate, payment.PaymentNumber, $"Payment - {payment.Reference ?? payment.PaymentNumber}",
                    -payment.Amount, runningBalance));
            }
        }

        var totalCharged = lines.Where(l => l.Amount > 0).Sum(l => l.Amount);
        var totalPaid = Math.Abs(lines.Where(l => l.Amount < 0).Sum(l => l.Amount));

        return new PortalStatementResponse(
            fromDate, toDate, openingBalance, totalCharged, totalPaid, runningBalance, lines);
    }

    public async Task<List<PortalPaymentResponse>> GetMyPaymentsAsync(Guid companyId, Guid contactId)
    {
        return await _db.Payments
            .Include(p => p.Document)
            .Where(p => p.CompanyId == companyId && p.Document.ContactId == contactId)
            .OrderByDescending(p => p.PaymentDate)
            .Select(p => new PortalPaymentResponse(
                p.Id, p.PaymentDate, p.Amount, p.PaymentMethod.ToString(),
                p.Reference, p.Document.DocumentNumber))
            .ToListAsync();
    }

    // ===== JWT Token Helpers =====

    private static string GenerateJwtToken(Guid portalAccessId, Guid contactId, Guid companyId, TimeSpan expiry)
    {
        // Build a simple base64-encoded JWT-like token containing the claims and expiry
        // In production, use Microsoft.IdentityModel.Tokens with proper signing keys
        var payload = new StringBuilder();
        payload.Append(portalAccessId.ToString());
        payload.Append('|');
        payload.Append(contactId.ToString());
        payload.Append('|');
        payload.Append(companyId.ToString());
        payload.Append('|');
        payload.Append(DateTime.UtcNow.Add(expiry).ToString("O"));

        var payloadBytes = Encoding.UTF8.GetBytes(payload.ToString());

        // Sign with HMAC-SHA256 using a derived key (in production, use a configured secret)
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_portalSigningKey));
        var signature = hmac.ComputeHash(payloadBytes);

        var tokenBytes = new byte[payloadBytes.Length + 1 + signature.Length];
        Buffer.BlockCopy(payloadBytes, 0, tokenBytes, 0, payloadBytes.Length);
        tokenBytes[payloadBytes.Length] = (byte)'.';
        Buffer.BlockCopy(signature, 0, tokenBytes, payloadBytes.Length + 1, signature.Length);

        return Convert.ToBase64String(tokenBytes);
    }

    private static (Guid portalAccessId, Guid contactId, Guid companyId) DecodeJwtToken(string token)
    {
        try
        {
            var tokenBytes = Convert.FromBase64String(token);
            var dotIndex = Array.IndexOf(tokenBytes, (byte)'.');
            if (dotIndex < 0)
                throw new InvalidOperationException("Invalid token format.");

            var payloadBytes = new byte[dotIndex];
            Buffer.BlockCopy(tokenBytes, 0, payloadBytes, 0, dotIndex);

            var payload = Encoding.UTF8.GetString(payloadBytes);
            var parts = payload.Split('|');

            if (parts.Length != 4)
                throw new InvalidOperationException("Invalid token payload.");

            var expiry = DateTime.Parse(parts[3]);
            if (expiry < DateTime.UtcNow)
                throw new InvalidOperationException("Token has expired.");

            // Verify signature
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_portalSigningKey));
            var expectedSignature = hmac.ComputeHash(payloadBytes);

            var actualSignature = new byte[tokenBytes.Length - dotIndex - 1];
            Buffer.BlockCopy(tokenBytes, dotIndex + 1, actualSignature, 0, actualSignature.Length);

            if (!CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature))
                throw new InvalidOperationException("Invalid token signature.");

            return (Guid.Parse(parts[0]), Guid.Parse(parts[1]), Guid.Parse(parts[2]));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Invalid token format.");
        }
    }

    // ===== Password Helpers =====

    private static string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(Convert.ToBase64String(salt) + password));
        var result = new byte[salt.Length + hash.Length];
        Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
        Buffer.BlockCopy(hash, 0, result, salt.Length, hash.Length);
        return Convert.ToBase64String(result);
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        try
        {
            var storedBytes = Convert.FromBase64String(storedHash);
            if (storedBytes.Length < 17) return false;

            var salt = new byte[16];
            Buffer.BlockCopy(storedBytes, 0, salt, 0, 16);

            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(Convert.ToBase64String(salt) + password));

            var storedHashBytes = new byte[storedBytes.Length - 16];
            Buffer.BlockCopy(storedBytes, 16, storedHashBytes, 0, storedHashBytes.Length);

            return CryptographicOperations.FixedTimeEquals(hash, storedHashBytes);
        }
        catch
        {
            return false;
        }
    }

    private static PortalAccessResponse MapToAccessResponse(PortalAccess p, string contactName) => new(
        p.Id, p.ContactId, contactName, p.Email, p.DisplayName,
        p.IsActive, p.LastLoginAt,
        p.CanViewInvoices, p.CanViewStatements, p.CanDownloadPdf, p.CanMakePayment);
}
