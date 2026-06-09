using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class WithholdingTaxCertService : IWithholdingTaxCertService
{
    private readonly AccountingDbContext _db;
    private readonly IPdfGenerationService _pdf;
    private readonly IFileAttachmentService _attachments;
    private readonly ILogger<WithholdingTaxCertService> _logger;

    public WithholdingTaxCertService(AccountingDbContext db, IPdfGenerationService pdf,
        IFileAttachmentService attachments, ILogger<WithholdingTaxCertService> logger)
    {
        _db = db;
        _pdf = pdf;
        _attachments = attachments;
        _logger = logger;
    }

    /// <summary>Generate the issued WHT certificate's PDF and attach it to the
    /// linked source document (the ใบสำคัญจ่าย / expense it was withheld on) so
    /// the cert travels with that document automatically. No-op when the cert
    /// isn't linked to a document. Idempotent — won't re-attach the same cert.
    /// Fully fail-safe: a generation/storage hiccup never blocks issuing.</summary>
    private async Task AttachCertPdfToDocumentAsync(Guid companyId, Guid certId)
    {
        try
        {
            var cert = await _db.WithholdingTaxCerts.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId);
            if (cert?.DocumentId == null) return;   // not linked → nothing to attach to

            var fileName = $"WHT-{cert.CertificateNumber}.pdf";

            // Idempotent: skip if this cert's PDF is already on the document.
            var already = await _db.FileAttachments.AnyAsync(f =>
                f.CompanyId == companyId && !f.IsDeleted
                && f.EntityType == "Document" && f.EntityId == cert.DocumentId.Value
                && f.OriginalFileName == fileName);
            if (already) return;

            var pdf = await _pdf.GenerateWithholdingTaxCertPdfAsync(companyId, certId);
            var uploaderId = await ResolveUploaderUserIdAsync(companyId, cert.CreatedBy);
            if (uploaderId == Guid.Empty) return;   // no user to attribute → skip silently

            await _attachments.UploadBytesAsync(companyId, "Document", cert.DocumentId.Value,
                fileName, "application/pdf", pdf.PdfData, uploaderId);

            _logger.LogInformation("Auto-attached WHT cert {Cert} PDF to document {DocId}",
                cert.CertificateNumber, cert.DocumentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-attach WHT cert PDF failed for cert {CertId}", certId);
        }
    }

    /// <summary>Resolve a REAL user id to stamp on the auto-generated attachment.
    /// cert.CreatedBy may be a user GUID (web flow) or a literal like
    /// "integration-sync" (partner sync); fall back to the company Owner, then
    /// any member, so the FileAttachment→User FK never throws.</summary>
    private async Task<Guid> ResolveUploaderUserIdAsync(Guid companyId, string? createdBy)
    {
        if (Guid.TryParse(createdBy, out var uid)
            && await _db.Users.AsNoTracking().AnyAsync(u => u.Id == uid))
            return uid;

        var ownerId = await _db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner)
            .Select(cu => cu.UserId)
            .FirstOrDefaultAsync();
        if (ownerId != Guid.Empty) return ownerId;

        return await _db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId)
            .Select(cu => cu.UserId)
            .FirstOrDefaultAsync();
    }

    public async Task<WithholdingTaxCertResponse> CreateAsync(Guid companyId, CreateWithholdingTaxCertRequest request, string createdBy)
    {
        // Auto-determine TaxFormType from contact if not specified
        var taxFormType = request.TaxFormType;
        if (!taxFormType.HasValue)
        {
            var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == request.PayeeContactId && c.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
            taxFormType = DetermineTaxFormType(contact);
        }

        // Cert# anchored on the *tax year* (CE), not the issue month — so a 50ทวิ
        // issued in Jan 2026 for Dec 2025 still lands in the 2025 sequence and the
        // year segment matches what's printed on the form. RD's e-Filing doesn't
        // mandate a format, but it does require uniqueness within company × tax year.
        var whtPrefix = $"WHT-{request.TaxYear}-";
        var maxWht = await _db.WithholdingTaxCerts
            .IgnoreQueryFilters()
            .Where(w => w.CompanyId == companyId && w.CertificateNumber.StartsWith(whtPrefix))
            .Select(w => w.CertificateNumber)
            .MaxAsync() as string;
        var whtSeq = 1;
        if (maxWht != null)
        {
            var lastPart = maxWht.Substring(whtPrefix.Length);
            if (int.TryParse(lastPart, out var parsed)) whtSeq = parsed + 1;
        }
        var certNumber = $"{whtPrefix}{whtSeq:D4}";

        var cert = new WithholdingTaxCert
        {
            CompanyId = companyId,
            CertificateNumber = certNumber,
            PayeeContactId = request.PayeeContactId,
            TaxFormType = taxFormType.Value,
            TaxYear = request.TaxYear,
            TaxMonth = request.TaxMonth,
            CertificateType = request.CertificateType,
            CreatedBy = createdBy
        };

        var order = 1;
        foreach (var line in request.Lines)
        {
            cert.Lines.Add(new WithholdingTaxCertLine
            {
                LineOrder = order++,
                IncomeTypeCode = line.IncomeTypeCode,
                IncomeDescription = line.IncomeDescription,
                PaymentDate = line.PaymentDate,
                IncomeAmount = line.IncomeAmount,
                TaxRate = line.TaxRate,
                TaxAmount = line.TaxAmount,
                Condition = line.Condition
            });
        }

        cert.TotalIncomeAmount = cert.Lines.Sum(l => l.IncomeAmount);
        cert.TotalTaxAmount = cert.Lines.Sum(l => l.TaxAmount);

        _db.WithholdingTaxCerts.Add(cert);
        await _db.SaveChangesAsync();

        return await GetByIdAsync(companyId, cert.Id);
    }

    public async Task<WithholdingTaxCertResponse> GetByIdAsync(Guid companyId, Guid certId)
    {
        var cert = await _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            .Include(w => w.PayeeContact)
            .Include(w => w.Document)
            .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        return MapToResponse(cert, company);
    }

    public async Task<PagedResponse<WithholdingTaxCertResponse>> GetAllAsync(Guid companyId, TaxType? taxFormType, int? year, int? month, PagedRequest request)
    {
        var query = _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            .Include(w => w.PayeeContact)
            .Include(w => w.Document)
            .Where(w => w.CompanyId == companyId);

        if (taxFormType.HasValue) query = query.Where(w => w.TaxFormType == taxFormType.Value);
        if (year.HasValue) query = query.Where(w => w.TaxYear == year.Value);
        if (month.HasValue) query = query.Where(w => w.TaxMonth == month.Value);

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(w => w.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);

        return new PagedResponse<WithholdingTaxCertResponse>(
            items.Select(w => MapToResponse(w, company)).ToList(), total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<WithholdingTaxCertResponse> IssueAsync(Guid companyId, Guid certId)
    {
        var cert = await _db.WithholdingTaxCerts.FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");

        if (cert.Status != WithholdingTaxCertStatus.Draft)
            throw new InvalidOperationException("สามารถออกหนังสือรับรองได้เฉพาะที่เป็น Draft เท่านั้น");

        cert.Status = WithholdingTaxCertStatus.Issued;
        cert.IssuedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Save the issued cert PDF as an attachment on its source document.
        await AttachCertPdfToDocumentAsync(companyId, cert.Id);

        return await GetByIdAsync(companyId, cert.Id);
    }

    public async Task VoidAsync(Guid companyId, Guid certId)
    {
        var cert = await _db.WithholdingTaxCerts.FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");

        cert.Status = WithholdingTaxCertStatus.Voided;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid companyId, Guid certId)
    {
        await DeleteAsync(companyId, certId, null);
    }

    public async Task DeleteAsync(Guid companyId, Guid certId, Guid? userId)
    {
        var cert = await _db.WithholdingTaxCerts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");

        if (userId.HasValue)
        {
            _db.AuditLogs.Add(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                Action = AuditAction.Delete,
                EntityType = "WithholdingTaxCert",
                EntityId = certId.ToString(),
                OldValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    cert.CertificateNumber, cert.TaxFormType, cert.Status,
                    cert.TotalIncomeAmount, cert.TotalTaxAmount, cert.PayeeContactId
                }),
                Timestamp = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        await _db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""WithholdingTaxCertLines"" WHERE ""WithholdingTaxCertId"" = {0}", certId);
        await _db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""WithholdingTaxCerts"" WHERE ""Id"" = {0} AND ""CompanyId"" = {1}", certId, companyId);
        _db.ChangeTracker.Clear();
    }

    public async Task<List<WithholdingTaxCertResponse>> GetByContactAsync(Guid companyId, Guid contactId, int? year = null)
    {
        var query = _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            .Include(w => w.PayeeContact)
            .Where(w => w.CompanyId == companyId && w.PayeeContactId == contactId);

        if (year.HasValue) query = query.Where(w => w.TaxYear == year.Value);

        var certs = await query.OrderByDescending(w => w.TaxYear).ThenByDescending(w => w.TaxMonth).ToListAsync();
        var company = await _db.Companies.FirstAsync(c => c.Id == companyId);

        return certs.Select(w => MapToResponse(w, company)).ToList();
    }

    private static string GetTaxFormName(TaxType type) => type switch
    {
        TaxType.WithholdingTax1 => "ภ.ง.ด.1",
        TaxType.WithholdingTax3 => "ภ.ง.ด.3",
        TaxType.WithholdingTax53 => "ภ.ง.ด.53",
        _ => type.ToString()
    };

    private static string GetIncomeTypeName(string code) => code switch
    {
        "1" => "เงินเดือน ค่าจ้าง บำนาญ (40(1))",
        "2" => "ค่านายหน้า (40(2))",
        "3" => "ค่าแห่งลิขสิทธิ์ (40(3))",
        "4a" => "ดอกเบี้ย (40(4)(a))",
        "4b" => "เงินปันผล (40(4)(b))",
        "5" => "ค่าเช่าทรัพย์สิน (40(5))",
        "6" => "ค่าวิชาชีพอิสระ (40(6))",
        "7" => "ค่ารับเหมา (40(7))",
        "8" => "ค่าบริการอื่นๆ (40(8))",
        _ => $"ประเภทเงินได้ {code}"
    };

    // ==================== Auto-Generate from Document ====================

    public async Task<WithholdingTaxCertResponse> AutoGenerateFromDocumentAsync(
        Guid companyId, Guid documentId, bool autoIssue, string createdBy)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (doc.WithholdingTaxAmount <= 0)
            throw new InvalidOperationException("เอกสารนี้ไม่มีภาษีหัก ณ ที่จ่าย");

        // Check if cert already exists for this document
        var existing = await _db.WithholdingTaxCerts
            .AnyAsync(w => w.CompanyId == companyId && w.DocumentId == documentId
                && w.Status != WithholdingTaxCertStatus.Voided);
        if (existing)
            throw new InvalidOperationException("เอกสารนี้มีหนังสือรับรองหัก ณ ที่จ่ายแล้ว");

        // Determine tax form type: ภ.ง.ด.53 for juristic persons, ภ.ง.ด.3 for individuals
        var taxFormType = DetermineTaxFormType(doc.Contact);

        var autoYm = DateTime.UtcNow.ToString("yyyyMM");
        var autoPrefix = $"WHT-{autoYm}-";
        var maxAutoWht = await _db.WithholdingTaxCerts
            .IgnoreQueryFilters()
            .Where(w => w.CompanyId == companyId && w.CertificateNumber.StartsWith(autoPrefix))
            .Select(w => w.CertificateNumber)
            .MaxAsync() as string;
        var autoSeq = 1;
        if (maxAutoWht != null)
        {
            var lastPart = maxAutoWht.Substring(autoPrefix.Length);
            if (int.TryParse(lastPart, out var parsed)) autoSeq = parsed + 1;
        }
        var certNumber = $"{autoPrefix}{autoSeq:D4}";

        var cert = new WithholdingTaxCert
        {
            CompanyId = companyId,
            CertificateNumber = certNumber,
            PayeeContactId = doc.ContactId,
            TaxFormType = taxFormType,
            TaxYear = doc.DocumentDate.Year,
            TaxMonth = doc.DocumentDate.Month,
            CertificateType = WithholdingTaxCertType.Withhold,
            DocumentId = documentId,
            CreatedBy = createdBy
        };

        // Create lines from document lines that have WHT
        var order = 1;
        var whtLines = doc.Lines.Where(l => l.WithholdingTaxAmount > 0).ToList();
        if (whtLines.Count == 0)
        {
            // Fallback: use document-level WHT with default income type
            cert.Lines.Add(new WithholdingTaxCertLine
            {
                LineOrder = 1,
                IncomeTypeCode = "8", // ค่าบริการอื่นๆ default
                IncomeDescription = $"ตามเอกสาร {doc.DocumentNumber}",
                PaymentDate = doc.DocumentDate,
                IncomeAmount = doc.SubTotal,
                TaxRate = doc.SubTotal > 0 ? doc.WithholdingTaxAmount * 100 / doc.SubTotal : 3m,
                TaxAmount = doc.WithholdingTaxAmount
            });
        }
        else
        {
            foreach (var line in whtLines)
            {
                cert.Lines.Add(new WithholdingTaxCertLine
                {
                    LineOrder = order++,
                    IncomeTypeCode = line.IncomeTypeCode ?? "8",
                    IncomeDescription = line.Description,
                    PaymentDate = doc.DocumentDate,
                    IncomeAmount = line.Amount,
                    TaxRate = line.WithholdingTaxRate,
                    TaxAmount = line.WithholdingTaxAmount
                });
            }
        }

        cert.TotalIncomeAmount = cert.Lines.Sum(l => l.IncomeAmount);
        cert.TotalTaxAmount = cert.Lines.Sum(l => l.TaxAmount);

        if (autoIssue)
        {
            cert.Status = WithholdingTaxCertStatus.Issued;
            cert.IssuedDate = DateTime.UtcNow;
        }

        _db.WithholdingTaxCerts.Add(cert);
        await _db.SaveChangesAsync();

        // When the cert is issued straight away (integration sync / one-click),
        // save its PDF onto the source document automatically.
        if (autoIssue)
            await AttachCertPdfToDocumentAsync(companyId, cert.Id);

        return await GetByIdAsync(companyId, cert.Id);
    }

    public async Task<List<PendingWhtDocumentResponse>> GetPendingDocumentsAsync(
        Guid companyId, int? year = null, int? month = null)
    {
        // Find documents with WHT that don't have a cert yet (or cert was voided)
        var existingCertDocIds = await _db.WithholdingTaxCerts
            .Where(w => w.CompanyId == companyId && w.DocumentId != null
                && w.Status != WithholdingTaxCertStatus.Voided)
            .Select(w => w.DocumentId!.Value)
            .ToListAsync();

        // STATUS GUARD: voided / rejected / draft documents must NOT show up
        // in the "waiting to issue cert" list. A cancelled PV is a non-event
        // for tax purposes — no WHT cert should ever be created against it,
        // and the lingering row in the modal lets the user accidentally issue
        // a cert that ties back to a voided source doc. Also exclude soft-
        // deleted rows (was missing from this query entirely).
        var query = _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && !d.IsDeleted
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected
                && d.Status != DocumentStatus.Draft
                && !d.WhtCertSkipped
                && d.WithholdingTaxAmount > 0
                && !existingCertDocIds.Contains(d.Id)
                && (d.DocumentType == DocumentType.PurchaseInvoice
                    || d.DocumentType == DocumentType.Expense
                    || d.DocumentType == DocumentType.PaymentVoucher));

        if (year.HasValue)
            query = query.Where(d => d.DocumentDate.Year == year.Value);
        if (month.HasValue)
            query = query.Where(d => d.DocumentDate.Month == month.Value);

        var docs = await query.OrderByDescending(d => d.DocumentDate).ToListAsync();

        return docs.Select(d => new PendingWhtDocumentResponse(
            d.Id, d.DocumentNumber, d.DocumentType.ToString(), d.DocumentDate,
            d.ContactId, d.Contact.Name, d.Contact.TaxId,
            d.SubTotal, d.WithholdingTaxAmount,
            d.Lines.Where(l => l.WithholdingTaxAmount > 0).Select(l => new PendingWhtLineInfo(
                l.Description, l.IncomeTypeCode, l.Amount,
                l.WithholdingTaxRate, l.WithholdingTaxAmount)).ToList()
        )).ToList();
    }

    public async Task DismissPendingAsync(Guid companyId, Guid documentId, bool dismiss)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d =>
            d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        doc.WhtCertSkipped = dismiss;
        await _db.SaveChangesAsync();
    }

    public async Task<BulkGenerateWhtResponse> BulkGenerateAsync(
        Guid companyId, BulkGenerateWhtRequest request, string createdBy)
    {
        var pending = await GetPendingDocumentsAsync(companyId, request.Year, request.Month);
        var generated = new List<WithholdingTaxCertResponse>();
        var skippedReasons = new List<string>();
        var skipped = 0;

        foreach (var doc in pending)
        {
            try
            {
                var cert = await AutoGenerateFromDocumentAsync(
                    companyId, doc.DocumentId, request.AutoIssue, createdBy);
                generated.Add(cert);
            }
            catch (Exception ex)
            {
                skipped++;
                skippedReasons.Add($"{doc.DocumentNumber}: {ex.Message}");
            }
        }

        return new BulkGenerateWhtResponse(
            pending.Count, generated.Count, skipped, skippedReasons, generated);
    }

    // ==================== Private Helpers ====================

    /// <summary>
    /// วิเคราะห์แบบ ภ.ง.ด. อัตโนมัติจาก ContactType ของผู้ติดต่อ
    /// นิติบุคคล → ภ.ง.ด.53, บุคคลธรรมดา → ภ.ง.ด.3
    /// </summary>
    private static TaxType DetermineTaxFormType(Contact contact)
    {
        return contact.ContactType switch
        {
            ContactType.JuristicPerson => TaxType.WithholdingTax53,
            ContactType.GovernmentAgency => TaxType.WithholdingTax53,
            _ => TaxType.WithholdingTax3
        };
    }

    private static string ComposeFullAddress(string? address, string? subDistrict, string? district, string? province, string? postalCode,
        string? moo = null, string? buildingNumber = null, string? streetName = null)
    {
        // Many contacts have BOTH a free-form Address (already typed as a full
        // address by the user, e.g. "44/75 ม.3 ต.สุรศักดิ์ อ.ศรีราชา จ.ชลบุรี 20110")
        // AND structured fields (SubDistrict/District/Province/PostalCode).
        // Naively joining all of them produced duplicate text on the WHT cert.
        // Trust the free-form Address when it already looks complete (contains
        // the postal code, the province name, or a "จ." marker); otherwise
        // build the address from the structured fields.
        var addr = (address ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(addr))
        {
            bool looksComplete =
                (!string.IsNullOrEmpty(postalCode) && addr.Contains(postalCode!)) ||
                (!string.IsNullOrEmpty(province)   && addr.Contains(province!))   ||
                addr.Contains("จ.") || addr.Contains("จังหวัด");
            if (looksComplete) return addr;
        }
        // Structured fallback — include Moo + BuildingNumber + StreetName so a
        // payee whose free-form Address is empty but structured fields are set
        // doesn't end up with a half-printed cert.
        return string.Join(" ", new[]
            {
                addr,
                buildingNumber,
                string.IsNullOrEmpty(moo) ? null : $"หมู่ {moo}",
                string.IsNullOrEmpty(streetName) ? null : $"ถ.{streetName}",
                subDistrict, district, province, postalCode
            }
            .Where(s => !string.IsNullOrEmpty(s)));
    }

    private static WithholdingTaxCertResponse MapToResponse(WithholdingTaxCert w, Company company) => new(
        w.Id, w.CertificateNumber, w.CompanyId,
        company.Name, company.TaxId, company.BranchCode,
        ComposeFullAddress(company.Address, company.SubDistrict, company.District, company.Province, company.PostalCode,
            company.Moo, company.BuildingNumber, company.StreetName),
        w.PayeeContactId, w.PayeeContact.Name, w.PayeeContact.TaxId,
        w.PayeeContact.BranchCode,
        ComposeFullAddress(w.PayeeContact.Address, w.PayeeContact.SubDistrict, w.PayeeContact.District, w.PayeeContact.Province, w.PayeeContact.PostalCode,
            w.PayeeContact.Moo, w.PayeeContact.BuildingNumber, w.PayeeContact.StreetName),
        w.TaxFormType, GetTaxFormName(w.TaxFormType),
        w.TaxYear, w.TaxMonth, w.CertificateType, w.Status,
        w.TotalIncomeAmount, w.TotalTaxAmount,
        w.Lines.OrderBy(l => l.LineOrder).Select(l => new WithholdingTaxCertLineResponse(
            l.Id, l.IncomeTypeCode, GetIncomeTypeName(l.IncomeTypeCode),
            l.IncomeDescription, l.PaymentDate, l.IncomeAmount, l.TaxRate, l.TaxAmount, l.Condition)).ToList(),
        w.IssuedDate, w.CreatedAt,
        w.DocumentId, w.Document?.DocumentNumber);
}
