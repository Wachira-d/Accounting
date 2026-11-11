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
    /// <summary>รอบ 252 WP-12A: แจ้งทีม "ลูกค้าแจ้งโอน" (E-06) · null ในเทสต์ที่สร้างตรง</summary>
    private readonly INotificationEngine? _notify;
    /// <summary>รอบ 255 WP-12B ข้อ 8: อีเมลลิงก์วิเศษ (SMTP ของระบบ) · null = ส่งไม่ได้ (ไม่สร้างแถว)</summary>
    private readonly IEmailService? _email;
    /// <summary>รอบ 255: URL ของระบบสำหรับลิงก์วิเศษ (SystemBaseUrl)</summary>
    private readonly IConfiguration _config;

    public PortalService(AccountingDbContext db, IConfiguration configuration, IPdfGenerationService? pdfService = null, IImageProcessingService? images = null,
        Accounting.Services.Storage.IFileStorage? storage = null, Accounting.Services.Payments.IPaymentIntentService? intents = null,
        IDocumentPayLinkService? payLinks = null, INotificationEngine? notify = null, IEmailService? email = null)
    {
        _email = email;
        _config = configuration;
        _payLinks = payLinks;
        _notify = notify;
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

    public async Task<PortalAccessResponse> CreateAccessAsync(Guid companyId, CreatePortalAccessRequest request, Guid actorUserId)
    {
        var contact = await _db.Contacts
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == request.ContactId)
            ?? throw new InvalidOperationException("Contact not found.");

        // รอบ 257 ฝ่ายค้าน L5: ไม่เปิดสิทธิ์ใดเลย ⇒ 400 (รวมหน้าเก่าที่แคชไว้ซึ่งส่งธงในรูปที่ไม่ bind)
        if (PortalAccessPermissions.CreateRefusal(request.CanViewInvoices, request.CanViewStatements, request.CanDownloadPdf, request.CanMakePayment) is string noFlags)
            throw new BusinessRuleException(noFlags, "PORTAL-FLAGS", 400);

        var existing = await _db.Set<PortalAccess>()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Email == request.Email);

        if (existing != null)
            throw new InvalidOperationException("A portal access with this email already exists.");

        var access = new PortalAccess
        {
            CompanyId = companyId,
            ContactId = request.ContactId,
            Email = request.Email,
            // รอบ 255 WP-12B ข้อ 8: ไม่ตั้งรหัสผ่าน = เข้าด้วยลิงก์วิเศษเท่านั้น (รหัสว่างล็อกอินด้วยรหัสไม่ได้ — VerifyPassword คืน false)
            PasswordHash = string.IsNullOrEmpty(request.Password) ? "" : HashPassword(request.Password!),
            DisplayName = request.DisplayName,
            CanViewInvoices = request.CanViewInvoices,
            CanViewStatements = request.CanViewStatements,
            CanDownloadPdf = request.CanDownloadPdf,
            CanMakePayment = request.CanMakePayment,
            IsActive = true
        };

        _db.Set<PortalAccess>().Add(access);
        AuditAccess(companyId, actorUserId, AuditAction.Create, access, null);   // รอบ 257 D-H1
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

    public async Task<PortalAccessResponse> UpdateAccessAsync(Guid companyId, Guid accessId, UpdatePortalAccessRequest request, Guid actorUserId)
    {
        var access = await _db.Set<PortalAccess>()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == accessId)
            ?? throw new InvalidOperationException("Portal access not found.");

        // ไม่ Include Contact (INNER JOIN ตัดแถวที่ contact ถูกลบ) — อ่านชื่อแยก · รอบ 257 ปิดท้าย: ไม่แขวนผู้ติดต่อที่ไม่ได้ติดตาม (AsNoTracking) บนแถวที่ติดตามอยู่
        // ก่อน SaveChanges — context ที่ติดตามผู้ติดต่อคนนี้อยู่แล้วจะชน "another instance with the same key is already being tracked"
        // (เทสต์ Db R257_DH1 ล้มทันทีที่คลาสถูกรันบน PostgreSQL จริงครั้งแรก)
        var contactName = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && c.Id == access.ContactId).Select(c => c.Name).FirstOrDefaultAsync();

        var before = AuditSnapshot(access);
        if (request.CanViewInvoices.HasValue) access.CanViewInvoices = request.CanViewInvoices.Value;
        if (request.CanViewStatements.HasValue) access.CanViewStatements = request.CanViewStatements.Value;
        if (request.CanDownloadPdf.HasValue) access.CanDownloadPdf = request.CanDownloadPdf.Value;
        if (request.CanMakePayment.HasValue) access.CanMakePayment = request.CanMakePayment.Value;
        if (request.IsActive.HasValue) access.IsActive = request.IsActive.Value;
        AuditAccess(companyId, actorUserId, AuditAction.Update, access, before);   // รอบ 257 D-H1

        await _db.SaveChangesAsync();

        return MapToAccessResponse(access, contactName ?? access.DisplayName ?? access.Email);
    }

    public async Task DeactivateAccessAsync(Guid companyId, Guid accessId, Guid actorUserId)
    {
        var access = await _db.Set<PortalAccess>()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Id == accessId)
            ?? throw new InvalidOperationException("Portal access not found.");

        var before = AuditSnapshot(access);
        access.IsActive = false;
        AuditAccess(companyId, actorUserId, AuditAction.Update, access, before);   // รอบ 257 D-H1
        await _db.SaveChangesAsync();
    }

    /// <summary>รอบ 257 D-H1: ภาพของสิทธิ์พอร์ทัลใน audit (ไม่มีรหัสผ่าน/แฮช)</summary>
    private static object AuditSnapshot(PortalAccess a) => new
    {
        a.ContactId, a.Email, a.IsActive, a.CanViewInvoices, a.CanViewStatements, a.CanDownloadPdf, a.CanMakePayment,
        MagicLinkOnly = string.IsNullOrEmpty(a.PasswordHash),
    };

    /// <summary>รอบ 257 D-H1: ให้/แก้/ปิดสิทธิ์พอร์ทัลลง audit chain (ผู้ทำ · ก่อน/หลัง) — บันทึกพร้อมแถวในคำสั่ง SaveChanges เดียวกัน</summary>
    private void AuditAccess(Guid companyId, Guid actorUserId, AuditAction action, PortalAccess after, object? before)
    {
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = actorUserId, Action = action, EntityType = "PortalAccess", EntityId = after.Id.ToString(),
            OldValues = before == null ? null : System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(AuditSnapshot(after)),
        });
    }

    public async Task<PortalAccessFlags?> GetAccessFlagsAsync(Guid companyId, Guid portalAccessId, CancellationToken ct = default)
    {
        return await _db.Set<PortalAccess>().AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.Id == portalAccessId && p.CompanyId == companyId && p.IsActive && !p.IsDeleted)
            .Select(p => new PortalAccessFlags(p.CanViewInvoices, p.CanViewStatements, p.CanDownloadPdf, p.CanMakePayment))
            .FirstOrDefaultAsync(ct);
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
        var (portalAccessId, contactId, companyId, tokenType) = DecodeJwtToken(refreshToken);
        // รอบ 255 ฝ่ายค้าน M9: /refresh รับเฉพาะโทเคน refresh · สิทธิ์ต้องเป็นของบริษัท/ผู้ติดต่อในโทเคน
        if (tokenType != PortalTokenPolicy.TypeRefresh)
            throw new InvalidOperationException("Invalid token type.");

        var access = await _db.Set<PortalAccess>()
            .FirstOrDefaultAsync(p => p.Id == portalAccessId && p.IsActive && p.CompanyId == companyId && p.ContactId == contactId)
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

    /// <summary>รอบ 257 D-H2: id ของใบใน <paramref name="query"/> ที่ "ลูกค้าเห็นได้" (<see cref="DocumentPayLinkPolicy.IsCustomerFacing"/> — ตัวตัดสินเดียว ·
    /// ต่อยอด IsSaleSide) · ข้อเท็จจริงฝั่ง (ใบต้นทาง · ผู้ติดต่อเป็นผู้ขายอย่างเดียว · ผู้ใช้สั่งย้ายฝั่ง) อ่านจากฐาน แล้วตัดสินในหน่วยความจำ ·
    /// ผู้ติดต่อที่เป็นทั้งลูกค้าและผู้ขายไม่เห็นใบซื้อ/PO/ค่าใช้จ่าย/ใบลดหนี้จากผู้ขาย (เดิมเห็น + ดาวน์โหลด PDF ได้)</summary>
    private async Task<List<Guid>> CustomerFacingIdsAsync(Guid companyId, IQueryable<Document> query, CancellationToken ct = default)
    {
        var facts = await query.AsNoTracking()
            .Select(d => new
            {
                d.Id, d.DocumentType, d.CnDnPurchaseSideOverride,
                RelatedType = _db.Documents.Where(r => r.Id == d.RelatedDocumentId && r.CompanyId == companyId).Select(r => (DocumentType?)r.DocumentType).FirstOrDefault(),
                ContactSupplierOnly = _db.Contacts.IgnoreQueryFilters().Where(c => c.Id == d.ContactId && c.CompanyId == companyId).Select(c => c.IsSupplier && !c.IsCustomer).FirstOrDefault(),
                ContactCustomerOnly = _db.Contacts.IgnoreQueryFilters().Where(c => c.Id == d.ContactId && c.CompanyId == companyId).Select(c => c.IsCustomer && !c.IsSupplier).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return facts.Where(f => DocumentPayLinkPolicy.IsCustomerFacing(f.DocumentType, f.RelatedType, f.CnDnPurchaseSideOverride, f.ContactSupplierOnly, f.ContactCustomerOnly))
            .Select(f => f.Id).ToList();   // List (ไม่ใช่ HashSet) — EF แปล List.Contains เป็น IN/ANY ได้แน่นอน
    }

    /// <summary>รอบ 257 ฝ่ายค้าน M2: เครื่องหมายของใบในใบแจ้งยอด — ตัวเดียวทั้งยอดยกมาและบรรทัดในงวด (<see cref="DocumentPayLinkPolicy.StatementSigned"/>:
    /// ใบที่ลูกค้าจ่ายเรา +1 · ใบลดหนี้ฝั่งขาย −1 · ใบเสนอราคา/ใบวางบิล/ใบเสร็จ/ฝั่งซื้อ 0)</summary>
    private async Task<Dictionary<Guid, decimal>> StatementSignsAsync(Guid companyId, IQueryable<Document> query, CancellationToken ct = default)
    {
        var rows = await query.AsNoTracking()
            .Select(d => new
            {
                d.Id, d.DocumentType, d.CnDnPurchaseSideOverride,
                RelatedType = _db.Documents.Where(r => r.Id == d.RelatedDocumentId && r.CompanyId == companyId).Select(r => (DocumentType?)r.DocumentType).FirstOrDefault(),
                ContactSupplierOnly = _db.Contacts.IgnoreQueryFilters().Where(c => c.Id == d.ContactId && c.CompanyId == companyId).Select(c => c.IsSupplier && !c.IsCustomer).FirstOrDefault(),
                ContactCustomerOnly = _db.Contacts.IgnoreQueryFilters().Where(c => c.Id == d.ContactId && c.CompanyId == companyId).Select(c => c.IsCustomer && !c.IsSupplier).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => DocumentPayLinkPolicy.StatementSigned(r.DocumentType, r.RelatedType, r.CnDnPurchaseSideOverride,
            r.ContactSupplierOnly, r.ContactCustomerOnly, 1m));
    }

    /// <summary>รอบ 257 D-H2: ใบนี้เป็นของผู้ติดต่อในโทเคน · ออกแล้ว · ลูกค้าเห็นได้ (ด่านเดียวของเส้นใบเดี่ยว: รายละเอียด · PDF · ตัวเลือกชำระ · แนบสลิป)</summary>
    private async Task<bool> IsPortalVisibleAsync(Guid companyId, Guid contactId, Guid documentId, CancellationToken ct = default)
        => (await CustomerFacingIdsAsync(companyId, PortalDocuments(companyId, contactId).Where(d => d.Id == documentId), ct)).Contains(documentId);

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
                // รอบ 250 (WP-02 ฝ่ายค้าน ข้อ 3): ฝั่งของใบเพิ่มหนี้ตัดสินด้วยตัวตัดสินเดียวกับ JE รับชำระ — ผู้ใช้สั่งย้ายฝั่ง › ใบต้นทาง › ผู้ติดต่อเป็นผู้ขายอย่างเดียว
                d.CnDnPurchaseSideOverride,
                ContactSupplierOnly = _db.Contacts.IgnoreQueryFilters().Where(c => c.Id == d.ContactId && c.CompanyId == companyId).Select(c => c.IsSupplier && !c.IsCustomer).FirstOrDefault(),
                ContactCustomerOnly = _db.Contacts.IgnoreQueryFilters().Where(c => c.Id == d.ContactId && c.CompanyId == companyId).Select(c => c.IsCustomer && !c.IsSupplier).FirstOrDefault(),
            })
            .ToListAsync();
        // รอบ 257 D-H2: เฉพาะใบที่ลูกค้าเห็นได้ (ตัวตัดสินเดียว — ข้อเท็จจริงฝั่งอยู่ในแถวที่ฉายแล้ว)
        rows = rows.Where(d => DocumentPayLinkPolicy.IsCustomerFacing(d.DocumentType, d.RelatedType, d.CnDnPurchaseSideOverride, d.ContactSupplierOnly, d.ContactCustomerOnly)).ToList();
        var pending = await SlipPendingByDocumentAsync(companyId, rows.Select(r => r.Id).ToList());   // รอบ 250 ฝ่ายค้าน L4 (WP-02b)
        // รอบ 250 WP-03 (ฝ่ายค้าน H1): ปุ่ม "ชำระ" ตามกติกาเดียวกับลิงก์ชำระ (DocumentPayLinkPolicy.IssueRefusal — ฝั่งขาย · THB · ยอดค้าง > 0 · ออกแล้ว)
        // · ใบกำกับที่อ้างใบแจ้งหนี้ตรวจตอนเปิดหน้าต่างชำระ (TransferForDocumentAsync) — หน้าเว็บไม่ตัดสินชนิดเอง
        return rows.Select(d => new PortalDocumentResponse(
                d.Id, d.DocumentNumber, d.DocumentType.ToString(),
                d.DocumentDate, d.DueDate, d.TotalAmount, d.PaidAmount,
                d.BalanceDue, d.Status.ToString(),
                SlipPendingAmount: pending.GetValueOrDefault(d.Id),
                CanPay: DocumentPayLinkPolicy.IssueRefusal(d.DocumentType, d.Status, d.BalanceDue,
                    DocumentPayLinkPolicy.IsSaleSide(d.DocumentType, d.RelatedType, d.CnDnPurchaseSideOverride, d.ContactSupplierOnly), d.Currency, null) == null))
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
        if (!await IsPortalVisibleAsync(companyId, contactId, documentId)) throw new InvalidOperationException("Document not found.");   // รอบ 257 D-H2
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
        if (!await IsPortalVisibleAsync(companyId, contactId, documentId)) throw new InvalidOperationException("Document not found.");   // รอบ 257 D-H2
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
        // รอบ 257 D-H2 + ฝ่ายค้าน M2: ใบแจ้งยอดนับเฉพาะใบที่ออกแล้ว · เครื่องหมายจากตัวตัดสินเดียว (StatementSignsAsync) ทั้งในงวดและยอดยกมา —
        // เดิมยอดยกมารวมยอดค้างของใบเสนอราคา/ใบวางบิล/ใบซื้อ ขณะที่บรรทัดในงวดนับเฉพาะใบแจ้งหนี้ ⇒ สองกติกาในใบเดียว
        var inPeriod = PortalDocuments(companyId, contactId).Where(d => d.DocumentDate >= fromDate && d.DocumentDate <= toDate);
        var inPeriodSigns = await StatementSignsAsync(companyId, inPeriod);
        var inPeriodIds = inPeriodSigns.Where(kv => kv.Value != 0m).Select(kv => kv.Key).ToList();
        var documents = await inPeriod
            .Where(d => inPeriodIds.Contains(d.Id))
            .OrderBy(d => d.DocumentDate)
            .ToListAsync();

        var beforeQuery = PortalDocuments(companyId, contactId).Where(d => d.DocumentDate < fromDate);
        var beforeSigns = await StatementSignsAsync(companyId, beforeQuery);
        var beforeIds = beforeSigns.Where(kv => kv.Value != 0m).Select(kv => kv.Key).ToList();
        var beforeBalances = await beforeQuery.AsNoTracking().Where(d => beforeIds.Contains(d.Id))
            .Select(d => new
            {
                d.Id, d.BalanceDue,
                HasSource = d.RelatedDocumentId != null && _db.Documents.Any(r => r.Id == d.RelatedDocumentId && r.CompanyId == companyId),
            }).ToListAsync();
        // รอบ 257 ปิดท้าย LOW: ใบลดหนี้ที่อ้างใบต้นทางลดยอดค้างของใบต้นทางไปแล้วตอนอนุมัติ ⇒ ไม่หักซ้ำในยอดยกมา (ตัวตัดสินเดียว StatementCreditNoteReduces)
        var openingBalance = beforeBalances
            .Where(b => beforeSigns[b.Id] > 0m || DocumentPayLinkPolicy.StatementCreditNoteReduces(b.HasSource, sourceListedInPeriod: false))
            .Sum(b => beforeSigns[b.Id] * b.BalanceDue);
        // ใบลดหนี้ในงวดที่ใบต้นทางอยู่ก่อนงวด ⇒ ยอดยกมา (BalanceDue ปัจจุบันของใบต้นทาง) รวมผลของใบลดหนี้นี้แล้ว — ขึ้นบรรทัดให้เห็น แต่ไม่ลดยอดซ้ำ
        var creditNoteSources = await inPeriod.AsNoTracking()
            .Where(d => inPeriodIds.Contains(d.Id) && d.RelatedDocumentId != null)
            .Select(d => new
            {
                d.Id,
                SourceDate = _db.Documents.Where(r => r.Id == d.RelatedDocumentId && r.CompanyId == companyId).Select(r => (DateTime?)r.DocumentDate).FirstOrDefault(),
            }).ToDictionaryAsync(x => x.Id, x => x.SourceDate);

        var lines = new List<PortalStatementLine>();
        var runningBalance = openingBalance;

        foreach (var doc in documents)
        {
            // ใบตั้งหนี้ (+) / ใบลดหนี้ฝั่งขาย (−) — ตัวตัดสินเดียวกับยอดยกมา
            var sign = inPeriodSigns.GetValueOrDefault(doc.Id);
            if (sign != 0m)
            {
                var sourceDate = creditNoteSources.GetValueOrDefault(doc.Id);
                var reduces = sign > 0m || DocumentPayLinkPolicy.StatementCreditNoteReduces(
                    hasSource: sourceDate != null, sourceListedInPeriod: sourceDate >= fromDate && sourceDate <= toDate);
                var amount = reduces ? sign * doc.TotalAmount : 0m;
                runningBalance += amount;
                lines.Add(new PortalStatementLine(
                    doc.DocumentDate, doc.DocumentNumber,
                    $"{(sign > 0 ? "Invoice" : "Credit note")} - {doc.DocumentNumber}" + (reduces ? "" : " (หักจากใบต้นทางแล้ว — รวมอยู่ในยอดยกมา)"),
                    amount, runningBalance));
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
        // รอบ 257 D-H2: การชำระของใบที่ลูกค้าเห็นได้เท่านั้น (ไม่ใช่เงินที่เราจ่ายผู้ติดต่อในฐานะผู้ขาย)
        var visible = await CustomerFacingIdsAsync(companyId, PortalDocuments(companyId, contactId));
        return await _db.Payments
            .Include(p => p.Document)
            .Where(PortalSlipPending.CountsAsReceived)   // รอบ 250 M-2: สลิปที่ยังไม่ตรวจไม่ใช่ "การชำระของฉัน"
            .Where(p => p.CompanyId == companyId && p.Document.ContactId == contactId && visible.Contains(p.DocumentId))
            .OrderByDescending(p => p.PaymentDate)
            .Select(p => new PortalPaymentResponse(
                p.Id, p.PaymentDate, p.Amount, p.PaymentMethod.ToString(),
                p.Reference, p.Document.DocumentNumber))
            .ToListAsync();
    }

    // ===== JWT Token Helpers =====

    private string GenerateJwtToken(Guid portalAccessId, Guid contactId, Guid companyId, TimeSpan expiry)
        => GenerateJwtToken(portalAccessId, contactId, companyId, expiry,
            expiry > PortalTokenPolicy.AccessLifetime ? PortalTokenPolicy.TypeRefresh : PortalTokenPolicy.TypeAccess);

    /// <summary>รอบ 255 ฝ่ายค้าน M9: ช่องที่ 5 = ชนิดโทเคน (access/refresh — <see cref="PortalTokenPolicy"/>)</summary>
    private string GenerateJwtToken(Guid portalAccessId, Guid contactId, Guid companyId, TimeSpan expiry, string tokenType)
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
        payload.Append('|');
        payload.Append(tokenType);

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
            var (_, contactId, _, type) = DecodeJwtToken(token);
            return type == PortalTokenPolicy.TypeAccess ? contactId : null;   // รอบ 255 M9: refresh ใช้แทน access ไม่ได้
        }
        catch
        {
            return null;
        }
    }

    /// <summary>รอบ 255 ฝ่ายค้าน H1/M9: ตัวตรวจของ scheme "Portal" — โทเคน access ที่ลายเซ็นถูก ยังไม่หมดอายุ · สิทธิ์พอร์ทัลยังเปิด (ตรวจทุกคำขอ) ·
    /// บริษัทของสิทธิ์ = บริษัทในโทเคน = บริษัทในเส้นทาง (ถ้ามี) · ไม่ผ่าน = null</summary>
    public async Task<PortalTokenPrincipal?> AuthenticateAccessAsync(string token, Guid? routeCompanyId, CancellationToken ct = default)
    {
        (Guid accessId, Guid contactId, Guid companyId, string type) t;
        try { t = DecodeJwtToken(token); }
        catch (InvalidOperationException) { return null; }
        var row = await _db.Set<PortalAccess>().AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.Id == t.accessId && !p.IsDeleted)
            .Select(p => new { p.IsActive, p.CompanyId, p.ContactId })
            .FirstOrDefaultAsync(ct);
        if (row == null || row.ContactId != t.contactId) return null;
        if (PortalTokenPolicy.AccessProblem(t.type, t.companyId, routeCompanyId, row.IsActive, row.CompanyId) != null) return null;
        return new PortalTokenPrincipal(t.accessId, t.contactId, t.companyId);
    }

    private (Guid portalAccessId, Guid contactId, Guid companyId, string tokenType) DecodeJwtToken(string token)
    {
        try
        {
            var tokenBytes = Convert.FromBase64String(token);
            // รอบ 255 WP-12B (พบจากเทสต์ลิงก์วิเศษ): เดิมหาตัวคั่นด้วย IndexOf('.') ตัวแรก — แต่วันหมดอายุรูป "O" มีจุดทศนิยมวินาที
            // ⇒ ตัดผิดตำแหน่ง ⇒ โทเคนพอร์ทัลทุกใบถอดไม่ได้ (ทุกเส้น [Authorize] ของพอร์ทัลตอบ 401) · ลายเซ็น HMAC-SHA256 ยาวคงที่ 32 ไบต์
            // ⇒ ตัวคั่นอยู่ตำแหน่ง len − 33 เสมอ (ตรวจว่าเป็น '.' จริง)
            const int SigLen = 32;
            var dotIndex = tokenBytes.Length - SigLen - 1;
            if (dotIndex <= 0 || tokenBytes[dotIndex] != (byte)'.')
                throw new InvalidOperationException("Invalid token format.");

            var payloadBytes = new byte[dotIndex];
            Buffer.BlockCopy(tokenBytes, 0, payloadBytes, 0, dotIndex);

            // รอบ 255 ฝ่ายค้าน M9: ตรวจลายเซ็นก่อนเชื่อเนื้อหา · รูปเก่า 4 ช่อง (ไม่มีชนิด) = ปฏิเสธ (DECISIONS 255-WP12B-13)
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_portalSigningKey));
            var expectedSignature = hmac.ComputeHash(payloadBytes);

            var actualSignature = new byte[tokenBytes.Length - dotIndex - 1];
            Buffer.BlockCopy(tokenBytes, dotIndex + 1, actualSignature, 0, actualSignature.Length);

            if (!CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature))
                throw new InvalidOperationException("Invalid token signature.");

            var payload = Encoding.UTF8.GetString(payloadBytes);
            var parts = payload.Split('|');

            if (parts.Length != 5 || (parts[4] != PortalTokenPolicy.TypeAccess && parts[4] != PortalTokenPolicy.TypeRefresh))
                throw new InvalidOperationException("Invalid token payload.");

            var expiry = DateTime.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            if (expiry < DateTime.UtcNow)
                throw new InvalidOperationException("Token has expired.");

            return (Guid.Parse(parts[0]), Guid.Parse(parts[1]), Guid.Parse(parts[2]), parts[4]);
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
        // รอบ 257 D-H2: + ลูกค้าเห็นได้ (ตัวตัดสินเดียว IsCustomerFacing — ใบซื้อของผู้ติดต่อที่เป็นผู้ขาย = ไม่พบ)
        if (!exists || !await IsPortalVisibleAsync(companyId, contactId, documentId, ct)) throw new KeyNotFoundException("ไม่พบเอกสาร");
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

    public async Task<PortalPayLinkResponse> GetDocumentPayLinkAsync(Guid companyId, Guid contactId, Guid documentId, CancellationToken ct = default)
    {
        // ความเป็นเจ้าของ + ลูกค้าเห็นได้ ผ่านด่านเดียวกับตัวเลือกชำระ (PortalDocuments + IsPortalVisibleAsync)
        var exists = await PortalDocuments(companyId, contactId).AsNoTracking().AnyAsync(d => d.Id == documentId, ct);
        if (!exists || !await IsPortalVisibleAsync(companyId, contactId, documentId, ct)) throw new KeyNotFoundException("ไม่พบเอกสาร");
        if (_payLinks == null) throw new InvalidOperationException("ระบบลิงก์ชำระยังไม่พร้อม — ติดต่อผู้ดูแลระบบ");
        var link = await _payLinks.EnsureLinkAsync(companyId, documentId, null, ct);
        if (link.Ok) return new PortalPayLinkResponse(link.Url, null, false);
        // ออกลิงก์ไม่ได้: ใบชำระไม่ได้ (เหตุเดียวกับหน้าชำระ) หรือบริษัทยังไม่มีช่องทางออนไลน์ ⇒ โอน/สลิปของพอร์ทัล (ยอดจากเซิร์ฟเวอร์)
        var transfer = await _payLinks.TransferForDocumentAsync(companyId, documentId, ct);
        var fallback = transfer is { Refusal: null, Transfer: not null };
        return new PortalPayLinkResponse(null, transfer?.Refusal ?? link.Refusal, fallback);
    }

    /// <summary>ลูกค้าแนบสลิปโอนของใบแจ้งหนี้/ใบกำกับ — <b>"แจ้งโอน" ไม่ใช่ "รับชำระแล้ว"</b> (รอบ 250 WP-02b · M-2/G-T7)
    /// <para>สร้าง <c>PaymentIntent(ManualSlip)</c> สถานะรอ ผ่าน <c>ManualSlipPaymentProvider</c> (ยอดซ้ำ + ยังเปิดอยู่ = ใบเดิม) + ไฟล์สลิปผูกเอกสาร ·
    /// <b>ไม่สร้างแถว Payment</b> (เดิมเพิ่ม Payment "[PENDING-VERIFY]" ทันที ⇒ ใบแจ้งยอด/จับคู่ธนาคารนับเป็นจ่ายแล้วก่อนมีใครตรวจ) ·
    /// พนักงานตรวจแล้วกดยืนยันที่หน้ารายการรับชำระ ⇒ <c>DocumentPaymentHandler</c> ⇒ <c>CreatePaymentAsync</c> (JE · ตัดหนี้ · ใบเสร็จ)</para>
    /// ตรวจความเป็นเจ้าของ: document.ContactId ต้องตรงผู้ติดต่อของโทเคนพอร์ทัล</summary>
    public async Task<PortalSlipUploadResponse> UploadDocumentSlipAsync(
        Guid companyId, Guid contactId, Guid documentId, IFormFile file, decimal? amount, string? bankRef = null)
    {
        if (file == null || file.Length == 0)
            throw new ArgumentException("กรุณาเลือกไฟล์สลิป");
        // รอบ 257 D-H2: แนบสลิปได้เฉพาะใบที่ลูกค้าเห็นได้ในพอร์ทัล (ใบซื้อของผู้ติดต่อที่เป็นผู้ขาย = ไม่พบ)
        if (!await IsPortalVisibleAsync(companyId, contactId, documentId)) throw new KeyNotFoundException("ไม่พบเอกสาร");
        if (!file.ContentType.StartsWith("image/") && file.ContentType != "application/pdf")
            throw new ArgumentException("รองรับเฉพาะรูปภาพหรือ PDF");
        // รอบ 259-3 (E-M1 · คำตัดสิน 259-3-2): พอร์ทัลไม่มีช่องให้ลูกค้าพิมพ์ยอดแล้ว — ไม่ส่งยอด ⇒ ยอดโอนหลังหัก ณ ที่จ่าย จากตัวคิดเดียวกับหน้าชำระ
        // (TransferForDocumentAsync · เดิม null = ยอดค้างเต็ม ⇒ ลูกค้าที่หักภาษีโอนขาดเท่า WHT แล้วสลิปไม่ตรงยอด) · ส่งยอดมาเอง (งวด) ยังผ่านด่านยอดค้างเดิม
        if (amount == null && _payLinks != null
            && await _payLinks.TransferForDocumentAsync(companyId, documentId) is { Refusal: null } transferView)
            amount = transferView.Net;

        byte[] slipBytes;
        await using (var hs = file.OpenReadStream())
        using (var ms = new System.IO.MemoryStream())
        {
            await hs.CopyToAsync(ms);
            slipBytes = ms.ToArray();
        }
        // รอบ 255 WP-12B ข้อ 1: ประตูเดียวของการแจ้งโอน (ด่านยอดค้าง · กันซ้ำ · เก็บไฟล์ · รายการรอยืนยัน · แจ้งทีม) — ใช้ร่วมกับหน้าชำระของเอกสาร ·
        // ด่านความเป็นเจ้าของ = ผู้ติดต่อในโทเคนพอร์ทัล (contactId) · สลิปไม่สร้างแถว Payment (ไม่ใช่รับชำระ)
        var slips = new SlipEvidenceService(_db, _intents, _storage, _images, _notify);
        var r = await slips.SubmitAsync(companyId, documentId, contactId, new SlipFile(slipBytes, file.ContentType, file.FileName ?? ""),
            amount, bankRef, SlipEvidenceService.ChannelPortal);
        return new PortalSlipUploadResponse(r.Intent.Id, documentId, r.FileUrl, r.Intent.Amount, DateTime.UtcNow, AwaitingReview: true);
    }

    // ====================================================================
    // รอบ 255 WP-12B ข้อ 8 — ลิงก์วิเศษ (ไม่ต้องจำรหัสผ่าน) + ส่งออก Excel ให้นักบัญชีของลูกค้า
    // ====================================================================

    /// <summary>ขอลิงก์เข้าสู่ระบบทางอีเมล — <b>ไม่บอกผล</b> (ผู้เรียกตอบ <see cref="PortalMagicLinkPolicy.AckMessage"/> เสมอ · กันไล่อีเมล) ·
    /// ส่งเฉพาะอีเมลที่ผู้ขายเปิดสิทธิ์พอร์ทัลไว้ (<c>PortalAccess</c> ใช้งานอยู่) · โทเคนเก็บเป็น hash · อายุ 15 นาที · ใช้ครั้งเดียว ·
    /// ไม่มี URL ของระบบ/SMTP ⇒ ไม่สร้างแถว + log (ลูกค้าได้คำตอบเดิม — ห้ามบอกว่าส่งแล้วกับผู้ดูแล: log ระดับ error) · คืน true เมื่อส่งจริง (เทสต์/ผู้ดูแล)</summary>
    public async Task<bool> RequestMagicLinkAsync(Guid companyId, string? email, CancellationToken ct = default)
    {
        var e = PortalMagicLinkPolicy.NormalizeEmail(email);
        if (e == null) return false;
        var access = await _db.Set<PortalAccess>().AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.CompanyId == companyId && p.IsActive && !p.IsDeleted && p.Email.ToLower() == e)
            .Select(p => new { p.Id, p.Email, p.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (access == null) return false;
        // รอบ 255 ฝ่ายค้าน M6: เข้าคิว EmailQueue (ผู้ส่งในนามบริษัท + โควตา/เพดานอีเมลของแพลตฟอร์มเดิม · ตรวจซ้ำตอนส่ง) — ไม่ส่งตรงจากคำขอนิรนาม ·
        // โทเคนออกตอนส่ง (PortalMagicLinkPolicy.LinkPlaceholder ⇒ IssueMagicLinkUrlAsync) — คิว/บันทึกอีเมลไม่เก็บ token
        var companyName = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        Func<string?, string?> enc = System.Net.WebUtility.HtmlEncode;
        var html = $"<p>เรียน {enc(access.DisplayName ?? access.Email)}</p>"
            + $"<p>กดลิงก์ด้านล่างเพื่อเข้าพอร์ทัลลูกค้าของ {enc(companyName)} (ใช้ได้ 15 นาที ครั้งเดียว — ไม่ต้องใช้รหัสผ่าน)</p>"
            + $"<p><a href=\"{PortalMagicLinkPolicy.LinkPlaceholder}\">เข้าสู่พอร์ทัลลูกค้า</a></p>"
            + "<p style=\"color:#64748b;font-size:12px\">ถ้าท่านไม่ได้ขอลิงก์นี้ ไม่ต้องทำอะไร — ลิงก์จะหมดอายุเอง</p>";
        var now = DateTime.UtcNow;
        _db.EmailQueues.Add(new EmailQueue
        {
            CompanyId = companyId, EntityType = PortalMagicLinkPolicy.QueueEntityType, EntityId = access.Id,
            ToEmail = access.Email, Subject = $"ลิงก์เข้าสู่พอร์ทัลลูกค้า — {companyName}", Body = html,
            AttachPdf = false, ScheduledFor = now, Status = "Pending",
            IdempotencyKey = $"portal-magic:{access.Id:N}:{Guid.NewGuid():N}", CreatedBy = "portal-magic-link",
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>รอบ 255 ฝ่ายค้าน M6: ตรวจแถวคิวลิงก์วิเศษตอนส่ง — สิทธิ์ยังเปิด · คำขอไม่ค้างนานเกิน · null = ส่ง</summary>
    internal static async Task<string?> MagicLinkSkipReasonAsync(AccountingDbContext db, EmailQueue item, CancellationToken ct)
    {
        var active = await db.Set<PortalAccess>().AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.Id == item.EntityId && p.CompanyId == item.CompanyId && !p.IsDeleted)
            .Select(p => (bool?)p.IsActive).FirstOrDefaultAsync(ct);
        return PortalMagicLinkPolicy.QueueSkipReason(active == true, item.CreatedAt, DateTime.UtcNow);
    }

    /// <summary>รอบ 255 ฝ่ายค้าน M6: ออกโทเคนลิงก์วิเศษตอนส่งอีเมลจากคิว (แถว PortalMagicLink ใหม่ · อายุนับจากตอนส่ง) · ไม่มี URL ของระบบ ⇒ (null, เหตุ) — ไม่ส่งลิงก์เสีย</summary>
    internal static async Task<(string? Url, string? Problem)> IssueMagicLinkUrlAsync(AccountingDbContext db, IConfiguration? config, Guid companyId, Guid portalAccessId, CancellationToken ct)
    {
        var baseUrl = await SystemBaseUrl.ResolveAsync(db, config, ct);
        var token = PortalMagicLinkPolicy.NewToken();
        var link = PortalMagicLinkPolicy.Link(baseUrl, companyId, token);
        if (link == null) return (null, "ยังไม่ได้ตั้ง URL ของระบบ — ส่งลิงก์เข้าพอร์ทัลไม่ได้");
        db.PortalMagicLinks.Add(new PortalMagicLink
        {
            CompanyId = companyId, PortalAccessId = portalAccessId, TokenHash = PortalMagicLinkPolicy.Hash(token),
            ExpiresAt = DateTime.UtcNow.Add(PortalMagicLinkPolicy.Ttl), CreatedBy = "portal-magic-link",
        });
        await db.SaveChangesAsync(ct);
        return (link, null);
    }

    /// <summary>แลกลิงก์วิเศษเป็นเซสชันพอร์ทัล — compare-and-set <c>UsedAt</c> (สองแท็บพร้อมกัน = ครั้งเดียว) · ข้อความเดียวทุกเหตุที่แลกไม่ได้ ·
    /// โทเคนพอร์ทัลแบบเดียวกับล็อกอินด้วยรหัสผ่าน (ลงนามด้วยกุญแจพอร์ทัลเดิม) · บันทึกกิจกรรม "MagicLogin"</summary>
    public async Task<PortalLoginResponse> RedeemMagicLinkAsync(Guid companyId, string? token, CancellationToken ct = default)
    {
        if (!PortalMagicLinkPolicy.LooksValid(token))
            throw new BusinessRuleException(PortalMagicLinkPolicy.InvalidMessage, PortalMagicLinkPolicy.RuleCode, 400);
        var hash = PortalMagicLinkPolicy.Hash(token!);
        var row = await _db.PortalMagicLinks.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.TokenHash == hash && !m.IsDeleted)
            .Select(m => new { m.Id, m.PortalAccessId, m.ExpiresAt, m.UsedAt })
            .FirstOrDefaultAsync(ct);
        var access = row == null ? null : await _db.Set<PortalAccess>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == row.PortalAccessId && p.CompanyId == companyId && !p.IsDeleted, ct);
        var now = DateTime.UtcNow;
        if (row == null || access == null || PortalMagicLinkPolicy.RedeemProblem(row.ExpiresAt, row.UsedAt, access.IsActive, now) is string)
            throw new BusinessRuleException(PortalMagicLinkPolicy.InvalidMessage, PortalMagicLinkPolicy.RuleCode, 400);
        var claimed = await _db.PortalMagicLinks
            .Where(m => m.Id == row.Id && m.CompanyId == companyId && m.UsedAt == null && m.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.UsedAt, (DateTime?)now), ct);
        if (claimed == 0)
            throw new BusinessRuleException(PortalMagicLinkPolicy.InvalidMessage, PortalMagicLinkPolicy.RuleCode, 400);

        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct)
            ?? throw new BusinessRuleException(PortalMagicLinkPolicy.InvalidMessage, PortalMagicLinkPolicy.RuleCode, 400);
        var contactName = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == access.ContactId && c.CompanyId == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        access.LastLoginAt = now;
        _db.Set<PortalActivity>().Add(new PortalActivity
        {
            CompanyId = companyId, PortalAccessId = access.Id, ActivityType = "MagicLogin", ActivityAt = now,
        });
        await _db.SaveChangesAsync(ct);
        var accessToken = GenerateJwtToken(access.Id, access.ContactId, companyId, TimeSpan.FromHours(2));
        var refreshToken = GenerateJwtToken(access.Id, access.ContactId, companyId, TimeSpan.FromDays(30));
        return new PortalLoginResponse(accessToken, refreshToken, access.ContactId,
            contactName ?? access.DisplayName ?? access.Email, company.Id, company.Name);
    }

    /// <summary>ส่งออก Excel "รายการเอกสารจากผู้ขาย" ให้นักบัญชีของลูกค้า (รูปแบบ<b>ทั่วไป</b> · MiniExcel — PEAK/FlowAccount/Express รอไฟล์ตัวอย่างจริง Q9) ·
    /// เฉพาะเอกสารที่ออกแล้วของผู้ติดต่อในโทเคน (ด่านเดียว <c>PortalDocuments</c>) · ช่วงวันที่ ≤ 366 วัน · ยอดปัด 2 ตำแหน่ง (AwayFromZero)</summary>
    public async Task<byte[]> ExportAccountantLedgerAsync(Guid companyId, Guid contactId, DateTime fromDate, DateTime toDate, CancellationToken ct = default)
    {
        if (toDate < fromDate) throw new ArgumentException("วันที่สิ้นสุดต้องไม่ก่อนวันเริ่ม");
        if ((toDate - fromDate).TotalDays > 366) throw new ArgumentException("ส่งออกได้ครั้งละไม่เกิน 1 ปี — แบ่งช่วงแล้วส่งออกใหม่");
        var seller = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.TaxId, c.BranchCode }).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");
        var to = toDate.Date.AddDays(1);
        var range = PortalDocuments(companyId, contactId)
            .Where(d => d.DocumentDate >= fromDate.Date && d.DocumentDate < to && PortalAccountantExport.Types.Contains(d.DocumentType));
        var visible = await CustomerFacingIdsAsync(companyId, range, ct);   // รอบ 257 D-H2
        var raw = await range.AsNoTracking()
            .Where(d => visible.Contains(d.Id))
            .OrderBy(d => d.DocumentDate).ThenBy(d => d.DocumentNumber)
            .Select(d => new
            {
                d.DocumentDate, d.DocumentNumber, d.DocumentType, d.SubTotal, d.VatAmount, d.TotalAmount, d.WithholdingTaxAmount,
                Voided = d.Status == DocumentStatus.Voided, d.IssuerBranchCode, d.BranchId, d.RelatedDocumentId, d.Reference, d.IsTaxInvoiceByLaw,
            })
            .ToListAsync(ct);
        // รอบ 257 ชุด 3 (D-M4): เลขที่ใบต้นทาง (บริษัทเดียวกันเท่านั้น) + สาขาที่ออกใบ (ตัวตัดสินเดียว DocumentIssuerBranch — snapshot ชนะทะเบียน ชนะค่าบริษัท)
        var relatedIds = raw.Where(r => r.RelatedDocumentId != null).Select(r => r.RelatedDocumentId!.Value).Distinct().ToList();
        var relatedNumbers = relatedIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && relatedIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.DocumentNumber, ct);
        var branchIds = raw.Where(r => r.BranchId != null).Select(r => r.BranchId!.Value).Distinct().ToList();
        var branchCodes = branchIds.Count == 0 ? new Dictionary<Guid, string?>()
            : await _db.Branches.AsNoTracking().IgnoreQueryFilters().Where(b => b.CompanyId == companyId && branchIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.TaxBranchCode, ct);
        var docs = raw.Select(d => new PortalAccountantExport.Row(d.DocumentDate, d.DocumentNumber, d.DocumentType, d.SubTotal, d.VatAmount, d.TotalAmount,
                d.WithholdingTaxAmount, d.Voided,
                SellerBranch: DocumentIssuerBranch.ResolveCode(d.IssuerBranchCode,
                    d.BranchId is Guid bid ? branchCodes.GetValueOrDefault(bid) : null, seller.BranchCode),
                Reference: PortalAccountantExport.ReferenceText(
                    d.RelatedDocumentId is Guid rid ? relatedNumbers.GetValueOrDefault(rid) : null, d.Reference),
                IsTaxInvoiceByLaw: d.IsTaxInvoiceByLaw))
            .ToList();
        var rows = PortalAccountantExport.Build(docs, seller.Name, seller.TaxId, seller.BranchCode);
        using var ms = new System.IO.MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(ms, rows, sheetName: "เอกสารจากผู้ขาย");
        return ms.ToArray();
    }
}
