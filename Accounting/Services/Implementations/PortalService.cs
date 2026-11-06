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
    /// <summary>รอบ 250 WP-03 (ฝ่ายค้าน H1): กล่องโอน/QR ของใบจากตัวเดียวกับหน้าชำระ (<c>DocumentPayLinkService.TransferForDocumentAsync</c>)</summary>
    private readonly IDocumentPayLinkService? _payLinks;
    private readonly IPdfGenerationService? _pdfService;
    private readonly IImageProcessingService? _images;
    private readonly string _portalSigningKey;

    /// <summary>ที่เก็บไฟล์กลาง (ทีม FS 2026-11-02) — นอก DI ⇒ FileStorageDefaults.Local</summary>
    private readonly Accounting.Services.Storage.IFileStorage? _storage;

    /// <summary>รอบ 250 WP-02b (M-2): สลิปพอร์ทัล = <c>PaymentIntent(ManualSlip)</c> รอยืนยัน — ทางเดียวกับเส้นสลิป/gateway อื่น (null = สร้างนอก DI ⇒ แนบสลิปไม่ได้ ไม่ถอยไปสร้าง Payment)</summary>
    private readonly Accounting.Services.Payments.IPaymentIntentService? _intents;

    public PortalService(AccountingDbContext db, IConfiguration configuration, IPdfGenerationService? pdfService = null, IImageProcessingService? images = null,
        Accounting.Services.Storage.IFileStorage? storage = null, Accounting.Services.Payments.IPaymentIntentService? intents = null,
        IDocumentPayLinkService? payLinks = null)
    {
        _payLinks = payLinks;
        _storage = storage;
        _intents = intents;
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
        access.Contact = (await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == access.ContactId))!;

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
        access.Contact = (await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == access.ContactId))!;

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

    /// <summary>เอกสารที่ลูกค้าเห็นได้ใน portal = เฉพาะที่ "ออกแล้ว" — ใบร่าง/รออนุมัติ/
    /// ตีกลับ มีเลข <c>DRAFT-{guid}</c> และยอดเต็ม ⇒ เดิมโผล่เป็น "รอชำระ" พร้อมปุ่มจ่าย
    /// + ดาวน์โหลด PDF ได้ (ERP_REVIEW A-02). ด่านเดียวใช้ทั้ง 3 เมธอด — ห้ามคัดลอก</summary>
    private IQueryable<Document> PortalDocuments(Guid companyId, Guid contactId)
        => _db.Documents.Where(d => d.CompanyId == companyId && d.ContactId == contactId
            && !DocumentStatusRules.NotIssued.Contains(d.Status));

    public async Task<List<PortalDocumentResponse>> GetMyDocumentsAsync(Guid companyId, Guid contactId, string? documentType = null)
    {
        var query = PortalDocuments(companyId, contactId);

        if (!string.IsNullOrWhiteSpace(documentType) && Enum.TryParse<DocumentType>(documentType, out var docType))
            query = query.Where(d => d.DocumentType == docType);

        var rows = await query
            .OrderByDescending(d => d.DocumentDate)
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.DueDate, d.TotalAmount, d.PaidAmount, d.BalanceDue, d.Status, d.Currency,
                RelatedType = _db.Documents.Where(r => r.Id == d.RelatedDocumentId && r.CompanyId == companyId).Select(r => (DocumentType?)r.DocumentType).FirstOrDefault(),
            })
            .ToListAsync();
        var pending = await SlipPendingByDocumentAsync(companyId, rows.Select(r => r.Id).ToList());   // รอบ 250 ฝ่ายค้าน L4 (WP-02b)
        // รอบ 250 WP-03 (ฝ่ายค้าน H1): ปุ่ม "ชำระ" ตามกติกาเดียวกับลิงก์ชำระ (DocumentPayLinkPolicy.IssueRefusal — ฝั่งขาย · THB · ยอดค้าง > 0 · ออกแล้ว)
        // · ใบกำกับที่อ้างใบแจ้งหนี้ตรวจตอนเปิดหน้าต่างชำระ (TransferForDocumentAsync) — หน้าเว็บไม่ตัดสินชนิดเอง
        return rows.Select(d => new PortalDocumentResponse(
                d.Id, d.DocumentNumber, d.DocumentType.ToString(),
                d.DocumentDate, d.DueDate, d.TotalAmount, d.PaidAmount,
                d.BalanceDue, d.Status.ToString(),
                SlipPendingAmount: pending.GetValueOrDefault(d.Id),
                CanPay: DocumentPayLinkPolicy.IssueRefusal(d.DocumentType, d.Status, d.BalanceDue,
                    DocumentPayLinkPolicy.IsSaleSide(d.DocumentType, d.RelatedType), d.Currency, null) == null))
            .ToList();
    }

    /// <summary>รอบ 250 ฝ่ายค้าน L4: ยอดสลิปรอตรวจต่อเอกสาร (รายการ manual-slip ของเอกสารที่ยังเปิด) — ลูกค้าเห็นว่าแนบแล้ว แต่ยอดค้างไม่ลดจนผู้ขายยืนยัน</summary>
    private async Task<Dictionary<Guid, decimal>> SlipPendingByDocumentAsync(Guid companyId, List<Guid> documentIds)
    {
        if (documentIds.Count == 0) return new Dictionary<Guid, decimal>();
        return await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.Document && documentIds.Contains(i.SourceId)
                && i.MethodKind == PaymentMethodKind.ManualSlip
                && (i.Status == PaymentIntentStatus.Pending || i.Status == PaymentIntentStatus.Created))
            .GroupBy(i => i.SourceId)
            .Select(g => new { g.Key, Sum = g.Sum(i => i.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum);
    }

    public async Task<PortalDocumentResponse> GetDocumentAsync(Guid companyId, Guid contactId, Guid documentId)
    {
        var doc = await PortalDocuments(companyId, contactId)
            .FirstOrDefaultAsync(d => d.Id == documentId)
            ?? throw new InvalidOperationException("Document not found.");

        var pending = await SlipPendingByDocumentAsync(companyId, new List<Guid> { doc.Id });
        return new PortalDocumentResponse(
            doc.Id, doc.DocumentNumber, doc.DocumentType.ToString(),
            doc.DocumentDate, doc.DueDate, doc.TotalAmount, doc.PaidAmount,
            doc.BalanceDue, doc.Status.ToString(), pending.GetValueOrDefault(doc.Id));
    }

    public async Task<byte[]> DownloadDocumentPdfAsync(Guid companyId, Guid contactId, Guid documentId)
    {
        var doc = await PortalDocuments(companyId, contactId)
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId)
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

            // Add payments against this document — สลิปพอร์ทัลรุ่นเก่าที่ยังไม่ตรวจไม่ใช่เงินรับ (รอบ 250 M-2 · ตัวตั้งเดียว PortalSlipPending)
            var payments = await _db.Payments
                .Where(PortalSlipPending.CountsAsReceived)
                .Where(p => p.CompanyId == companyId && p.DocumentId == doc.Id && p.PaymentDate >= fromDate && p.PaymentDate <= toDate)
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
            .Where(PortalSlipPending.CountsAsReceived)   // รอบ 250 M-2: สลิปที่ยังไม่ตรวจไม่ใช่ "การชำระของฉัน"
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
        var gw = await Accounting.Helpers.SiteTransferAccount.Rows(_db.Set<SitePaymentGateway>().AsNoTracking(), companyId, null).FirstOrDefaultAsync();   // รอบ 228 S-1
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

    /// <summary>รอบ 250 WP-03 (A G-01 · D §9.1 · คำตัดสิน §2 #15) — ตัวเลือกชำระของใบนี้ + QR พร้อมเพย์ที่<b>เซิร์ฟเวอร์สร้าง</b>
    ///
    /// <para>เดิมพอร์ทัลประกอบ payload EMVCo ใน JS แล้วส่งเลขพร้อมเพย์+ยอดไป <c>api.qrserver.com</c> (บุคคลที่สาม — ข้อมูลการเงินของลูกค้าไหลออก ·
    /// สำเนา TLV builder ชุดที่สอง) · ตอนนี้ใช้ <c>PromptPayService</c> ตัวเดียวกับ pay-document (รอบ 246) · ยอดที่ฝัง = ยอดค้างของใบจากฐาน
    /// (ไม่รับยอดจากหน้าเว็บ) · ใบแจ้งหนี้ที่มีใบกำกับภาษีอ้างแล้ว = ไม่ฝังยอด (<c>PaymentQrPolicy.EmbedAmount</c>) · เลขพร้อมเพย์ใช้ไม่ได้ = ไม่มี QR</para></summary>
    public async Task<StorefrontPaymentOptions> GetDocumentPaymentOptionsAsync(Guid companyId, Guid contactId, Guid documentId, CancellationToken ct = default)
    {
        // ฝ่ายค้าน M2: ความเป็นเจ้าของ + "ออกแล้ว" ผ่านด่านเดียวของพอร์ทัล (PortalDocuments) — ไม่เขียน query เอง
        var exists = await PortalDocuments(companyId, contactId).AsNoTracking().AnyAsync(d => d.Id == documentId, ct);
        if (!exists) throw new KeyNotFoundException("ไม่พบเอกสาร");
        // ฝ่ายค้าน H1: กติกา/ยอด/บัญชีเดียวกับหน้าชำระ (pay-document) — ฝั่งขาย · THB · ยอดค้าง > 0 · ใบกำกับอ้างแล้ว ⇒ ปฏิเสธพร้อมเหตุ ·
        // QR ฝังยอดสุทธิหลัง WHT · บัญชีของใบก่อน (ตัวเลือกเดียวกับกระดาษ) แล้วจึงบัญชีรับโอนของเว็บ
        if (_payLinks == null) throw new InvalidOperationException("ระบบลิงก์ชำระยังไม่พร้อม — ติดต่อผู้ดูแลระบบ");
        var view = await _payLinks.TransferForDocumentAsync(companyId, documentId, ct)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var opts = new StorefrontPaymentOptions
        {
            PayRefusal = view.Refusal,
            AmountGross = view.Gross, AmountWht = view.Wht, AmountNet = view.Net,
            HasPaymentMethod = view.Refusal == null && view.Transfer != null,
        };
        if (view.Refusal == null && view.Transfer is { } t)
        {
            opts.BankName = t.BankName;
            opts.BankAccountNumber = t.AccountNumber;
            opts.BankAccountName = t.AccountName;
            opts.PromptPayId = t.PromptPayId;
            opts.PromptPayQrDataUri = t.PromptPayQrDataUri;
        }
        return opts;
    }

    /// <summary>ลูกค้าแนบสลิปโอนของใบแจ้งหนี้/ใบกำกับ — <b>"แจ้งโอน" ไม่ใช่ "รับชำระแล้ว"</b> (รอบ 250 WP-02b · M-2/G-T7)
    /// <para>สร้าง <c>PaymentIntent(ManualSlip)</c> สถานะรอ ผ่าน <c>ManualSlipPaymentProvider</c> (ยอดซ้ำ + ยังเปิดอยู่ = ใบเดิม) + ไฟล์สลิปผูกเอกสาร ·
    /// <b>ไม่สร้างแถว Payment</b> (เดิมเพิ่ม Payment "[PENDING-VERIFY]" ทันที ⇒ ใบแจ้งยอด/จับคู่ธนาคารนับเป็นจ่ายแล้วก่อนมีใครตรวจ) ·
    /// พนักงานตรวจแล้วกดยืนยันที่หน้ารายการรับชำระ ⇒ <c>DocumentPaymentHandler</c> ⇒ <c>CreatePaymentAsync</c> (JE · ตัดหนี้ · ใบเสร็จ)</para>
    /// ตรวจความเป็นเจ้าของ: document.ContactId ต้องตรงผู้ติดต่อของโทเคนพอร์ทัล</summary>
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
        if (_intents == null)
            throw new BusinessRuleException("ระบบรับสลิปยังไม่พร้อม — กรุณาติดต่อผู้ขาย", DocumentSlipIntents.RuleCode, 503);

        // รอบ 250 ฝ่ายค้าน H2/L4: ตัดสินที่ยอดค้างของเอกสาร (สลิปงวด 2 รับได้ · ใบชำระครบ/ยอดเกินยอดค้าง = ปฏิเสธ) — ตัวตัดสินเดียว DocumentSlipIntents
        var pay = amount ?? doc.BalanceDue;
        if (DocumentSlipIntents.UploadProblem(doc.Status, doc.BalanceDue, pay) is string uploadProblem)
            throw new BusinessRuleException(uploadProblem, DocumentSlipIntents.RuleCode, 400);

        // Save file under wwwroot/uploads/portal-slips/{yyyy-MM}/...
        // Run images through ImageProcessingService (Slip profile — compressed
        // but still readable). PDFs and unknown types pass through untouched.
        // ทีม FS 2026-11-02: เขียนผ่านที่เก็บกลางตัวเดียวกับ ImageProcessingService (Local/S3) — ห้ามแตะดิสก์ตรง
        var storage = _storage ?? _images?.Storage ?? Accounting.Services.Storage.FileStorageDefaults.Local;
        var relDir = $"uploads/portal-slips/{DateTime.UtcNow:yyyy-MM}";
        var absDir = System.IO.Path.Combine(storage.Locator.RelativeBase, "wwwroot", relDir);
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
            await using (var fs = file.OpenReadStream()) await storage.WriteAsync(absPath, fs, file.ContentType);
            relUrl = "/" + relDir + "/" + storedName;
        }

        // ความจริงเรื่องเงิน = PaymentIntent (manual-slip) รอยืนยัน — สลิปแต่ละใบเป็นรายการของตัวเอง (ฝ่ายค้าน H2) ·
        // ล้ม ⇒ ลบไฟล์ที่เพิ่งเขียน (ฝ่ายค้าน L4 — ไม่เก็บข้อมูลผู้โอนกำพร้า) แล้วโยนต่อ
        Accounting.Models.Entities.PaymentIntent intent;
        try
        {
            intent = await _intents.StartAsync(companyId, new Accounting.Services.Payments.StartPaymentRequest(
                PaymentSourceKind.Document, doc.Id, pay, PaymentMethodKind.ManualSlip,
                Description: PortalSlipPending.IntentDescription(doc.DocumentNumber, relUrl),
                ContactId: contactId),
                providerCode: Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { await storage.DeleteAsync(absPath); } catch (Exception delEx) when (delEx is not OperationCanceledException) { _ = delEx; }
            if (ex is BusinessRuleException) throw;
            // ข้อความไทยของ StartAsync (InvalidOperationException) ส่งต่อตรง · อื่น ๆ = ข้อความกลาง (ไม่รั่วข้อความภายใน)
            throw new BusinessRuleException(ex is InvalidOperationException ? ex.Message : "บันทึกสลิปไม่สำเร็จ — กรุณาแนบอีกครั้ง หรือติดต่อผู้ขาย",
                ex, DocumentSlipIntents.RuleCode, 409);
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
            UploadedByUserId = null,
            CreatedBy = "portal-customer"
        });

        await _db.SaveChangesAsync();

        return new PortalSlipUploadResponse(intent.Id, doc.Id, relUrl, intent.Amount, DateTime.UtcNow, AwaitingReview: true);
    }
}
