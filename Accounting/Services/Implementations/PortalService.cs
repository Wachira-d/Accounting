using System.Security.Cryptography;
using System.Text;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs.Portal;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class PortalService : IPortalService
{
    private readonly AccountingDbContext _db;
    private readonly IPdfGenerationService? _pdfService;
    private readonly IImageProcessingService? _images;
    private readonly string _portalSigningKey;

    public PortalService(AccountingDbContext db, IConfiguration configuration, IPdfGenerationService? pdfService = null, IImageProcessingService? images = null)
    {
        _db = db;
        _pdfService = pdfService;
        _images = images;
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
        // ไม่ Include Contact ใน SQL (required nav + !IsDeleted → INNER JOIN
        // ตัดแถวที่ contact ถูกลบ) — materialize แล้ว reattach + map ใน memory
        var accesses = await _db.Set<PortalAccess>()
            .Where(p => p.CompanyId == companyId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        var contactIds = accesses.Select(p => p.ContactId).Distinct().ToList();
        var contactMap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);
        foreach (var p in accesses)
            if (contactMap.TryGetValue(p.ContactId, out var c)) p.Contact = c;

        return accesses.Select(p => new PortalAccessResponse(
                p.Id, p.ContactId, p.Contact?.Name ?? string.Empty, p.Email, p.DisplayName,
                p.IsActive, p.LastLoginAt,
                p.CanViewInvoices, p.CanViewStatements, p.CanDownloadPdf, p.CanMakePayment))
            .ToList();
    }

    public async Task<PortalAccessResponse> UpdateAccessAsync(Guid companyId, Guid accessId, UpdatePortalAccessRequest request)
    {
        var access = await _db.Set<PortalAccess>()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == accessId)
            ?? throw new InvalidOperationException("Portal access not found.");

        // ไม่ Include Contact (INNER JOIN ตัดแถวที่ contact ถูกลบ) — reattach เอง
        access.Contact = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == access.ContactId);

        if (request.CanViewInvoices.HasValue) access.CanViewInvoices = request.CanViewInvoices.Value;
        if (request.CanViewStatements.HasValue) access.CanViewStatements = request.CanViewStatements.Value;
        if (request.CanDownloadPdf.HasValue) access.CanDownloadPdf = request.CanDownloadPdf.Value;
        if (request.CanMakePayment.HasValue) access.CanMakePayment = request.CanMakePayment.Value;
        if (request.IsActive.HasValue) access.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();

        return MapToAccessResponse(access, access.Contact?.Name ?? access.DisplayName ?? access.Email);
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
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.CompanyId == request.CompanyId
                                   && p.Email == request.Email
                                   && p.IsActive
                                   && !p.IsDeleted)
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
            access.Contact?.Name ?? access.DisplayName ?? access.Email,
            company.Id,
            company.Name);
    }

    public async Task<PortalLoginResponse> RefreshTokenAsync(string refreshToken)
    {
        // Decode the refresh token to extract claims
        var (portalAccessId, contactId, companyId) = DecodeJwtToken(refreshToken);

        var access = await _db.Set<PortalAccess>()
            .FirstOrDefaultAsync(p => p.Id == portalAccessId && p.IsActive)
            ?? throw new InvalidOperationException("Portal access not found or inactive.");

        // ไม่ Include Contact (INNER JOIN ตัดแถวที่ contact ถูกลบ) — reattach เอง
        access.Contact = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == access.ContactId);

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
            access.Contact?.Name ?? access.DisplayName ?? access.Email,
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
            .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.ContactId == contactId && d.Id == documentId)
            ?? throw new InvalidOperationException("Document not found.");
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate แยก
        await _db.HydrateContactAsync(companyId, doc);

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

    private string GenerateJwtToken(Guid portalAccessId, Guid contactId, Guid companyId, TimeSpan expiry)
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

    public Guid? ExtractContactIdFromToken(string token)
    {
        try
        {
            var (_, contactId, _) = DecodeJwtToken(token);
            return contactId;
        }
        catch
        {
            return null;
        }
    }

    private (Guid portalAccessId, Guid contactId, Guid companyId) DecodeJwtToken(string token)
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
        return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        try
        {
            // Support legacy SHA256 hashes during migration period
            if (!storedHash.StartsWith("$2"))
            {
                return VerifyLegacySha256(password, storedHash);
            }
            return BCrypt.Net.BCrypt.Verify(password, storedHash);
        }
        catch
        {
            return false;
        }
    }

    private static bool VerifyLegacySha256(string password, string storedHash)
    {
        try
        {
            var storedBytes = Convert.FromBase64String(storedHash);
            if (storedBytes.Length < 17) return false;
            var salt = new byte[16];
            Buffer.BlockCopy(storedBytes, 0, salt, 0, 16);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(Convert.ToBase64String(salt) + password));
            var storedHashBytes = new byte[storedBytes.Length - 16];
            Buffer.BlockCopy(storedBytes, 16, storedHashBytes, 0, storedHashBytes.Length);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hash, storedHashBytes);
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Portal password verification failed: {ex.Message}"); return false; }
    }

    private static PortalAccessResponse MapToAccessResponse(PortalAccess p, string contactName) => new(
        p.Id, p.ContactId, contactName, p.Email, p.DisplayName,
        p.IsActive, p.LastLoginAt,
        p.CanViewInvoices, p.CanViewStatements, p.CanDownloadPdf, p.CanMakePayment);

    // ====================================================================
    // Customer-self-service payments — portal customers paying invoices
    // ====================================================================

    /// <summary>Return the first active payment gateway across any of
    /// this company's sites. The customer portal is per-company, not
    /// per-site, so we surface whichever gateway the owner has
    /// configured (typical SME has one PromptPay + bank-transfer
    /// gateway shared across all their sites). When no gateway is
    /// configured, HasPaymentMethod is false and the portal hides
    /// the slip-upload UI.</summary>
    public async Task<StorefrontPaymentOptions> GetCompanyPaymentOptionsAsync(Guid companyId)
    {
        var gw = await _db.Set<SitePaymentGateway>().AsNoTracking()
            .Where(g => g.CompanyId == companyId && g.IsActive && !g.IsDeleted)
            .OrderBy(g => g.SortOrder)
            .FirstOrDefaultAsync();
        if (gw == null) return new StorefrontPaymentOptions { HasPaymentMethod = false };
        var has = !string.IsNullOrEmpty(gw.PromptPayId) || !string.IsNullOrEmpty(gw.BankAccountNumber);
        return new StorefrontPaymentOptions
        {
            PromptPayId = gw.PromptPayId,
            PromptPayQrUrl = gw.PromptPayQrUrl,
            BankName = gw.BankName,
            BankAccountNumber = gw.BankAccountNumber,
            BankAccountName = gw.BankAccountName,
            HasPaymentMethod = has
        };
    }

    /// <summary>Portal customer uploads a slip against a specific
    /// invoice/tax-invoice. Creates a Payment row tied to the document
    /// in status=Pending, with the slip stored as a FileAttachment.
    /// Owner confirms via /pages/documents (existing Payment workflow).
    /// Strict ownership check: document.ContactId must match the
    /// portal user's contact id — prevents one customer from uploading
    /// to another customer's invoice.</summary>
    public async Task<PortalSlipUploadResponse> UploadDocumentSlipAsync(
        Guid companyId, Guid contactId, Guid documentId, IFormFile file, decimal? amount)
    {
        if (file == null || file.Length == 0)
            throw new ArgumentException("กรุณาเลือกไฟล์สลิป");
        if (!file.ContentType.StartsWith("image/") && file.ContentType != "application/pdf")
            throw new ArgumentException("รองรับเฉพาะรูปภาพหรือ PDF");

        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && d.ContactId == contactId && !d.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var balance = doc.TotalAmount - doc.PaidAmount;
        var pay = amount ?? balance;
        if (pay <= 0) throw new InvalidOperationException("ยอดที่จะชำระต้องมากกว่า 0");
        if (pay > doc.TotalAmount * 1.05m) throw new InvalidOperationException("ยอดสลิปมากกว่ายอดในเอกสาร — ตรวจสอบอีกครั้ง");

        // Save file under wwwroot/uploads/portal-slips/{yyyy-MM}/...
        // Run images through ImageProcessingService (Slip profile — compressed
        // but still readable). PDFs and unknown types pass through untouched.
        var relDir = $"uploads/portal-slips/{DateTime.UtcNow:yyyy-MM}";
        var absDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "wwwroot", relDir);
        System.IO.Directory.CreateDirectory(absDir);
        string storedName; string absPath; string relUrl;
        if (_images != null && _images.IsProcessableImage(file.ContentType))
        {
            await using var s = file.OpenReadStream();
            var processed = await _images.ProcessAndSaveAsync(s, file.ContentType, file.FileName, absDir, "/" + relDir, ImageProfile.Slip);
            absPath = processed.AbsolutePath;
            storedName = System.IO.Path.GetFileName(absPath);
            relUrl = processed.RelativeUrl;
        }
        else
        {
            var ext = System.IO.Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
            storedName = $"{Guid.NewGuid():N}{ext}";
            absPath = System.IO.Path.Combine(absDir, storedName);
            await using (var fs = System.IO.File.Create(absPath)) await file.CopyToAsync(fs);
            relUrl = "/" + relDir + "/" + storedName;
        }

        _db.Set<FileAttachment>().Add(new FileAttachment
        {
            CompanyId = companyId,
            FileName = storedName,
            OriginalFileName = file.FileName ?? storedName,
            ContentType = file.ContentType ?? "application/octet-stream",
            StoragePath = absPath,
            EntityType = "Document",
            EntityId = doc.Id,
            UploadedByUserId = Guid.Empty,
            CreatedBy = "portal-customer"
        });

        // Payment entity has no Status field — owner confirms by
        // posting a JE / marking the Document. We leave Notes with
        // a clear "[PENDING-VERIFY]" prefix so the existing payments
        // list shows it as awaiting owner confirmation.
        var payment = new Payment
        {
            CompanyId = companyId,
            PaymentNumber = await NextPaymentNumberAsync(companyId),
            PaymentDate = DateTime.UtcNow.Date,
            PaymentMethod = PaymentMethod.BankTransfer,
            DocumentId = doc.Id,
            Amount = pay,
            Reference = $"slip-{file.FileName}",
            Notes = $"[PENDING-VERIFY] แนบสลิปจาก Portal ลูกค้า · ไฟล์: {relUrl}",
            CreatedBy = "portal-customer"
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        return new PortalSlipUploadResponse(payment.Id, doc.Id, relUrl, pay, payment.CreatedAt);
    }

    private async Task<string> NextPaymentNumberAsync(Guid companyId)
    {
        var prefix = $"PAY-{DateTime.UtcNow:yyyyMM}-";
        var last = await _db.Payments
            .Where(p => p.CompanyId == companyId && p.PaymentNumber.StartsWith(prefix))
            .Select(p => p.PaymentNumber)
            .OrderByDescending(n => n)
            .FirstOrDefaultAsync();
        var seq = 1;
        if (last != null && int.TryParse(last.AsSpan(prefix.Length), out var n)) seq = n + 1;
        return $"{prefix}{seq:D4}";
    }
}
