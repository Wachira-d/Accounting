using Accounting.Helpers;
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
    private readonly IEmailScheduleService? _emailSchedule;

    public WithholdingTaxCertService(AccountingDbContext db, IPdfGenerationService pdf,
        IFileAttachmentService attachments, ILogger<WithholdingTaxCertService> logger,
        IEmailScheduleService? emailSchedule = null)
    {
        _db = db;
        _pdf = pdf;
        _attachments = attachments;
        _logger = logger;
        _emailSchedule = emailSchedule;
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
            // ปิด auto-attach โดย default — ผู้ใช้กด download/พิมพ์ใบ 50ทวิ เองจาก
            // หน้า WHT (ไฟล์สวยกว่า). เปิดได้ที่ CompanySettings.AutoAttachWhtCertPdf.
            var autoAttach = await _db.Set<CompanySettings>().AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted)
                .Select(c => (bool?)c.AutoAttachWhtCertPdf).FirstOrDefaultAsync() ?? false;
            if (!autoAttach) return;

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
        // เลือก/ตรวจ TaxFormType จาก payee เสมอ — ไม่เชื่อค่าที่ caller ส่งมาแบบตรง ๆ
        // (เคสจริง: มังกรออกใบให้บริษัทแต่ส่ง TaxFormType=ภ.ง.ด.3 มา → เดิมเชื่อทันที
        // = ใบผิด). ProcessReceipt/integration/UI ทุกทางต้องผ่าน create นี้ → แก้ที่
        // จุดเดียวคุมได้หมด. ภ.ง.ด.3↔53 derive จาก juristic status ของ payee จริง
        var payee = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == request.PayeeContactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        var (resolvedForm, corrected, reason) = ResolveWhtFormType(payee, request.TaxFormType);
        if (corrected)
            _logger.LogWarning(
                "WHT cert: แก้ประเภทแบบจาก {From} → {To} ({Reason}) payee={Payee} taxId={TaxId} — ค่าที่ส่งมาไม่ตรงกับสถานะผู้ถูกหัก",
                request.TaxFormType, resolvedForm, reason, payee.Name, payee.TaxId);
        var taxFormType = (TaxType?)resolvedForm;

        // เลขที่ 50ทวิ = WHT-{ปีค.ศ.}{เดือน 2 หลัก}-{running 4 หลัก} รันต่อ "เดือนภาษี"
        // (company × ปี × เดือน). เดือน = TaxMonth ที่ผู้ใช้ระบุ (เดือนของเงินได้ที่จ่าย)
        // ไม่ใช่เดือนที่ออกใบ → ใบภาษี ธ.ค. ที่ออก ม.ค. ยังลงเดือน 12. RD e-Filing ไม่
        // บังคับ format ขอแค่ unique ต่อ company × ปีภาษี — เพิ่มเดือนไม่กระทบ uniqueness
        // และให้เลขเดียวกับใบที่ auto-gen จากใบสำคัญจ่าย (WHT-YYYYMM-####).
        var mmSeg = request.TaxMonth is >= 1 and <= 12 ? request.TaxMonth : DateTime.UtcNow.Month;
        var whtPrefix = $"WHT-{request.TaxYear}{mmSeg:D2}-";
        // Numeric MAX on the parsed suffix avoids the lexicographic bug that
        // returned "9999" once a tenant crossed 10,000 certs in a year
        // (same fix DocumentNumberGenerator uses). Cap padding at 5 digits
        // — well above any realistic SME annual volume.
        var existingNumbers = await _db.WithholdingTaxCerts
            .IgnoreQueryFilters()
            .Where(w => w.CompanyId == companyId && w.CertificateNumber.StartsWith(whtPrefix))
            .Select(w => w.CertificateNumber)
            .ToListAsync();
        var whtSeq = existingNumbers
            .Select(n => int.TryParse(n.Substring(whtPrefix.Length), out var p) ? p : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
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

    public async Task<WithholdingTaxCertResponse> UpdateAsync(Guid companyId, Guid certId,
        CreateWithholdingTaxCertRequest request, string updatedBy)
    {
        var cert = await _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");

        // แก้ได้เฉพาะ Draft — Issued/Voided ห้ามแก้ (immutable หลังออก)
        if (cert.Status != WithholdingTaxCertStatus.Draft)
            throw new InvalidOperationException("แก้ไขได้เฉพาะหนังสือรับรองที่เป็นฉบับร่าง (Draft) เท่านั้น");

        // แก้ได้เฉพาะ cert ที่สร้างเอง — ไม่อ้างอิงใบสำคัญจ่าย/payroll.
        // cert ที่ผูกเอกสารต้นทาง ตัวเลขต้องตรงกับต้นทาง ห้ามแก้มือ
        // (ถ้าจะแก้ ต้องไปแก้ที่เอกสารต้นทางแล้ว regenerate).
        if (cert.DocumentId.HasValue)
            throw new InvalidOperationException("หนังสือรับรองนี้อ้างอิงใบสำคัญจ่าย — แก้ไม่ได้ กรุณาแก้ที่เอกสารต้นทางแล้วสร้างใหม่");
        if (cert.SourcePayrollRunId.HasValue)
            throw new InvalidOperationException("หนังสือรับรองนี้สร้างจากรอบเงินเดือน — แก้ไม่ได้ กรุณาแก้ที่ payroll แล้วสร้างใหม่");

        // อัปเดต header fields (CertificateNumber + TaxYear sequence ไม่แตะ)
        cert.PayeeContactId = request.PayeeContactId;
        // ตรวจ/แก้ประเภทแบบตาม payee เหมือน CreateAsync — แก้มือก็ต้องถูกกฎ
        // (ภ.ง.ด.3↔53 ขึ้นกับผู้ถูกหัก ไม่ใช่สิ่งที่ user เลือกได้ตามใจ)
        var updPayee = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == request.PayeeContactId && c.CompanyId == companyId);
        var (updForm, updCorrected, updReason) = ResolveWhtFormType(updPayee, request.TaxFormType ?? cert.TaxFormType);
        if (updCorrected)
            _logger.LogWarning("WHT cert update: แก้ประเภทแบบ → {To} ({Reason}) payee={Payee}", updForm, updReason, updPayee?.Name);
        cert.TaxFormType = updForm;
        cert.TaxYear = request.TaxYear;
        cert.TaxMonth = request.TaxMonth;
        cert.CertificateType = request.CertificateType;
        cert.UpdatedBy = updatedBy;
        cert.UpdatedAt = DateTime.UtcNow;

        // แทนที่ lines ทั้งหมด
        _db.WithholdingTaxCertLines.RemoveRange(cert.Lines);
        cert.Lines.Clear();
        var order = 1;
        foreach (var line in request.Lines)
        {
            cert.Lines.Add(new WithholdingTaxCertLine
            {
                WithholdingTaxCertId = cert.Id,
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

        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, cert.Id);
    }

    public async Task<WithholdingTaxCertResponse> GetByIdAsync(Guid companyId, Guid certId)
    {
        var cert = await _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            // ไม่ Include PayeeContact — hydrate แยก (กัน INNER JOIN ตัด 50 ทวิ ที่ payee ถูกลบ)
            .Include(w => w.Document)
            .FirstOrDefaultAsync(w => w.Id == certId && w.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหนังสือรับรองหัก ณ ที่จ่าย");
        await _db.HydratePayeeContactAsync(companyId, cert);

        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        return MapToResponse(cert, company);
    }

    public async Task<PagedResponse<WithholdingTaxCertResponse>> GetAllAsync(Guid companyId, TaxType? taxFormType, int? year, int? month, PagedRequest request)
    {
        var query = _db.WithholdingTaxCerts
            .Include(w => w.Lines)
            // ไม่ Include PayeeContact — hydrate แยก (กัน INNER JOIN ตัด 50 ทวิ ที่ payee ถูกลบ)
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
        await _db.HydratePayeeContactsAsync(companyId, items);

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

        // Auto-email hook: enqueue ส่งใบ 50ทวิให้ PayeeContact ถ้ามีกฎ + email
        if (_emailSchedule != null)
        {
            try { await _emailSchedule.OnWhtCertIssuedAsync(companyId, cert.Id); }
            catch (Exception ex) { _logger.LogWarning(ex, "WHT cert email enqueue failed Cert={Id}", cert.Id); }
        }

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
            .Include(w => w.Lines)   // ไม่ Include PayeeContact — hydrate แยก
            .Where(w => w.CompanyId == companyId && w.PayeeContactId == contactId);

        if (year.HasValue) query = query.Where(w => w.TaxYear == year.Value);

        var certs = await query.OrderByDescending(w => w.TaxYear).ThenByDescending(w => w.TaxMonth).ToListAsync();
        await _db.HydratePayeeContactsAsync(companyId, certs);
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
        Guid companyId, Guid documentId, bool autoIssue, string createdBy, DateTime? paymentDate = null,
        Guid? sourcePaymentId = null, decimal? paymentWhtAmount = null)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)   // ไม่ Include Contact — hydrate แยก (กัน INNER JOIN ทำ doc = null → 50 ทวิ ออกไม่ได้)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        await _db.HydrateContactAsync(companyId, doc);

        if (doc.WithholdingTaxAmount <= 0)
            throw new InvalidOperationException("เอกสารนี้ไม่มีภาษีหัก ณ ที่จ่าย");

        // Idempotency — ภ.ง.ด.3/53 เป็น cash basis: จ่ายเป็นงวดต้องออกใบ "งวดละใบ"
        // ตามยอดที่หักจริงของงวดนั้น จึงเช็คซ้ำที่ระดับ "งวดการจ่าย" เมื่อระบุ
        // sourcePaymentId มา (เดิมเช็คระดับเอกสาร → งวดที่ 2 เป็นต้นไปออกใบไม่ได้
        // และใบแรกก็ระบุยอดเต็มทั้งเอกสารทั้งที่จ่ายไปแค่บางส่วน)
        if (sourcePaymentId.HasValue)
        {
            var dupPayment = await _db.WithholdingTaxCerts
                .AnyAsync(w => w.CompanyId == companyId && w.SourcePaymentId == sourcePaymentId.Value
                    && w.Status != WithholdingTaxCertStatus.Voided);
            if (dupPayment)
                throw new InvalidOperationException("งวดการจ่ายนี้มีหนังสือรับรองหัก ณ ที่จ่ายแล้ว");
        }
        else
        {
            var existing = await _db.WithholdingTaxCerts
                .AnyAsync(w => w.CompanyId == companyId && w.DocumentId == documentId
                    && w.Status != WithholdingTaxCertStatus.Voided);
            if (existing)
                throw new InvalidOperationException("เอกสารนี้มีหนังสือรับรองหัก ณ ที่จ่ายแล้ว");
        }

        // สัดส่วนของงวดนี้เทียบยอดภาษีหักทั้งเอกสาร — ใช้เฉลี่ยยอดรายบรรทัด
        // (จ่าย 40% → ใบระบุภาษีหัก 40% ไม่ใช่ 100%)
        var whtRatio = 1m;
        if (paymentWhtAmount.HasValue && doc.WithholdingTaxAmount > 0
            && paymentWhtAmount.Value > 0 && paymentWhtAmount.Value < doc.WithholdingTaxAmount)
        {
            whtRatio = paymentWhtAmount.Value / doc.WithholdingTaxAmount;
        }

        // ประเภทแบบ ภ.ง.ด.53 นิติบุคคล / ภ.ง.ด.3 บุคคลธรรมดา — ตรวจจากหลายสัญญาณ
        // (เลขภาษี 13 หลัก + ContactType + ชื่อ) ไม่ใช่แค่ ContactType ที่ default เป็น
        // Individual (เคสบริษัทที่ contact สร้างจาก integration แล้วไม่ตั้ง type)
        var (taxFormType, _, _) = ResolveWhtFormType(doc.Contact, null);

        // เลขเดือน = เดือน "เงินได้ที่จ่าย" (tax month) ไม่ใช่เดือนที่ generate (UtcNow)
        // → ตรงกับ TaxMonth ที่ลงในใบ + สอดคล้องกับ CreateAsync (WHT-YYYYMM-####)
        var autoDate = paymentDate ?? doc.PaymentDate ?? doc.DocumentDate;
        var autoYm = autoDate.ToString("yyyyMM");
        var autoPrefix = $"WHT-{autoYm}-";
        // Numeric max (same fix as CreateAsync above).
        var autoExisting = await _db.WithholdingTaxCerts
            .IgnoreQueryFilters()
            .Where(w => w.CompanyId == companyId && w.CertificateNumber.StartsWith(autoPrefix))
            .Select(w => w.CertificateNumber)
            .ToListAsync();
        var autoSeq = autoExisting
            .Select(n => int.TryParse(n.Substring(autoPrefix.Length), out var p) ? p : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var certNumber = $"{autoPrefix}{autoSeq:D4}";

        var cert = new WithholdingTaxCert
        {
            CompanyId = companyId,
            CertificateNumber = certNumber,
            PayeeContactId = doc.ContactId,
            TaxFormType = taxFormType,
            // ภ.ง.ด.3/53 = cash basis: เดือนภาษีตาม "วันจ่ายจริง" (audit F13) —
            // เดิมใช้ DocumentDate → cert ตกงวดเดือนตั้งหนี้ทั้งที่ยังไม่จ่าย
            TaxYear = (paymentDate ?? doc.PaymentDate ?? doc.DocumentDate).Year,
            TaxMonth = (paymentDate ?? doc.PaymentDate ?? doc.DocumentDate).Month,
            CertificateType = WithholdingTaxCertType.Withhold,
            DocumentId = documentId,
            SourcePaymentId = sourcePaymentId,
            CreatedBy = createdBy
        };

        // Create lines from document lines that have WHT
        var order = 1;
        var whtLines = doc.Lines.Where(l => l.WithholdingTaxAmount > 0).ToList();
        // ยอดของงวด: เฉลี่ยตามสัดส่วน (whtRatio = 1 เมื่อจ่ายครั้งเดียว/เต็มจำนวน)
        decimal Prorate(decimal v) => whtRatio == 1m
            ? v : Math.Round(v * whtRatio, 2, MidpointRounding.AwayFromZero);

        if (whtLines.Count == 0)
        {
            // Fallback: use document-level WHT with default income type
            cert.Lines.Add(new WithholdingTaxCertLine
            {
                LineOrder = 1,
                IncomeTypeCode = "8", // ค่าบริการอื่นๆ default
                IncomeDescription = $"ตามเอกสาร {doc.DocumentNumber}"
                    + (whtRatio == 1m ? "" : $" (จ่ายงวดนี้ {whtRatio:P0})"),
                PaymentDate = paymentDate ?? doc.PaymentDate ?? doc.DocumentDate,
                IncomeAmount = Prorate(doc.SubTotal),
                TaxRate = doc.SubTotal > 0 ? doc.WithholdingTaxAmount * 100 / doc.SubTotal : 3m,
                TaxAmount = paymentWhtAmount ?? doc.WithholdingTaxAmount
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
                    PaymentDate = paymentDate ?? doc.PaymentDate ?? doc.DocumentDate,
                    IncomeAmount = Prorate(line.Amount),
                    TaxRate = line.WithholdingTaxRate,
                    TaxAmount = Prorate(line.WithholdingTaxAmount)
                });
            }
            // ปัดเศษรายบรรทัดอาจไม่รวมเท่ายอดงวดพอดี — ดูดผลต่างเข้าบรรทัดสุดท้าย
            // เพื่อให้ Σ บรรทัด = ภาษีที่นำส่งจริงของงวดนั้นเป๊ะ
            if (paymentWhtAmount.HasValue && cert.Lines.Count > 0)
            {
                var diff = paymentWhtAmount.Value - cert.Lines.Sum(l => l.TaxAmount);
                if (diff != 0m && Math.Abs(diff) <= 0.05m * cert.Lines.Count)
                    cert.Lines.Last().TaxAmount += diff;
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

        // Auto-email hook: enqueue ส่งใบ 50ทวิให้ PayeeContact ถ้ามีกฎ + email
        if (_emailSchedule != null)
        {
            try { await _emailSchedule.OnWhtCertIssuedAsync(companyId, cert.Id); }
            catch (Exception ex) { _logger.LogWarning(ex, "WHT cert email enqueue failed Cert={Id}", cert.Id); }
        }

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
            .Include(d => d.Lines)   // ไม่ Include Contact — hydrate แยก (กัน INNER JOIN ตัดใบที่ contact ถูกลบ)
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
        await _db.HydrateContactsAsync(companyId, docs);

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

    // ── ตรวจว่า payee เป็น "นิติบุคคล" หรือ "บุคคลธรรมดา" แบบหลายสัญญาณ ──
    // ประเภทแบบ ภ.ง.ด. ขึ้นกับ *ผู้ถูกหักภาษี*: นิติบุคคล → 53, บุคคลธรรมดา → 3.
    // เดิมดูแค่ ContactType (default = Individual) → ถ้า contact ถูกสร้างจาก
    // integration/มังกร โดยไม่ตั้ง type ให้ถูก หรือส่ง TaxFormType=ภ.ง.ด.3 มาตรง ๆ
    // ระบบเชื่อทันที → บริษัทได้ใบ ภ.ง.ด.3 ผิด. ตัวนี้ยืนยันจากหลักฐานที่หนักแน่น:
    //   1) เลขประจำตัวผู้เสียภาษี 13 หลัก — นิติบุคคลขึ้นต้น 0 / บุคคลธรรมดา 1-8
    //      (authoritative ที่สุด: เป็นเลขทะเบียนตามกฎหมาย)
    //   2) ContactType ระบุชัดเป็นนิติบุคคล/ราชการ
    //   3) ชื่อมีคำบ่งชี้นิติบุคคล (บริษัท/ห้างหุ้นส่วน/มหาชน/Co.,Ltd…)
    // คืน true=นิติบุคคล, false=บุคคลธรรมดา, null=ไม่มีสัญญาณชัด (ให้ caller fallback)
    internal static bool? DetectJuristic(Contact? contact)
    {
        if (contact == null) return null;

        var digits = new string((contact.TaxId ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 13)
        {
            if (digits[0] == '0') return true;                    // นิติบุคคล
            if (digits[0] >= '1' && digits[0] <= '8') return false; // บุคคลธรรมดา
        }

        if (contact.ContactType is ContactType.JuristicPerson or ContactType.GovernmentAgency)
            return true;

        var name = (contact.Name ?? "").Trim();
        if (name.Length > 0)
        {
            // Thai keywords (distinctive) + English legal suffixes
            string[] thaiKw = { "บริษัท", "บมจ", "หจก", "ห้างหุ้นส่วน", "มหาชน", "องค์การ", "สหกรณ์", "มูลนิธิ", "สมาคม" };
            if (thaiKw.Any(k => name.Contains(k))) return true;
            var lower = name.ToLowerInvariant();
            string[] engKw = { "co.,ltd", "co., ltd", "co.ltd", "company limited", "ltd.", "ltd ", " plc", "public company", "partnership", "corporation", "incorporated" };
            if (engKw.Any(k => lower.Contains(k))) return true;
        }

        // ไม่มีสัญญาณนิติบุคคล + ContactType ตั้งเป็น Individual ชัด → บุคคลธรรมดา;
        // ถ้า type ยังเป็น default โดยไม่มีหลักฐานอื่น คืน null ให้ caller ตัดสิน
        return contact.ContactType == ContactType.Individual ? false : (bool?)null;
    }

    /// <summary>เลือกประเภทแบบ ภ.ง.ด. ที่ "ถูกต้อง" จาก payee — ตรวจ/แก้แม้ caller
    /// (เช่น มังกร) ส่ง TaxFormType มาแล้ว. แก้เฉพาะแกน ภ.ง.ด.3 ↔ 53 (ขึ้นกับผู้ถูก
    /// หัก); ภ.ง.ด.1 (เงินเดือน) / ภ.ง.ด.2 (ดอกเบี้ย/ปันผล) ขึ้นกับประเภทเงินได้ —
    /// ไม่แตะ. คืน (form ที่ถูก, corrected=แก้จากที่ขอมาไหม, reason).</summary>
    internal static (TaxType formType, bool corrected, string reason) ResolveWhtFormType(Contact? contact, TaxType? requested)
    {
        // แบบที่ไม่ใช่แกน 3/53 → ปล่อยตามที่ขอ (income-type-driven)
        if (requested.HasValue
            && requested.Value != TaxType.WithholdingTax3
            && requested.Value != TaxType.WithholdingTax53)
            return (requested.Value, false, "");

        var juristic = DetectJuristic(contact);
        TaxType correct;
        string reason;
        if (juristic == true) { correct = TaxType.WithholdingTax53; reason = "payee เป็นนิติบุคคล"; }
        else if (juristic == false) { correct = TaxType.WithholdingTax3; reason = "payee เป็นบุคคลธรรมดา"; }
        else if (contact != null) { correct = DetermineTaxFormType(contact); reason = "จาก ContactType"; }
        else { correct = requested ?? TaxType.WithholdingTax3; reason = "ไม่พบ payee"; }

        var corrected = requested.HasValue && requested.Value != correct;
        return (correct, corrected, reason);
    }

    private static string ComposeFullAddress(string? address, string? subDistrict, string? district, string? province, string? postalCode,
        string? moo = null, string? buildingNumber = null, string? streetName = null, string? buildingName = null)
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
                buildingName,   // ชื่ออาคาร — เดิมตกหล่นจาก structured fallback
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
            company.Moo, company.BuildingNumber, company.StreetName, company.BuildingName),
        w.PayeeContactId, w.PayeeContact.Name, w.PayeeContact.TaxId,
        w.PayeeContact.BranchCode,
        ComposeFullAddress(w.PayeeContact.Address, w.PayeeContact.SubDistrict, w.PayeeContact.District, w.PayeeContact.Province, w.PayeeContact.PostalCode,
            w.PayeeContact.Moo, w.PayeeContact.BuildingNumber, w.PayeeContact.StreetName, w.PayeeContact.BuildingName),
        w.TaxFormType, GetTaxFormName(w.TaxFormType),
        w.TaxYear, w.TaxMonth, w.CertificateType, w.Status,
        w.TotalIncomeAmount, w.TotalTaxAmount,
        w.Lines.OrderBy(l => l.LineOrder).Select(l => new WithholdingTaxCertLineResponse(
            l.Id, l.IncomeTypeCode, GetIncomeTypeName(l.IncomeTypeCode),
            l.IncomeDescription, l.PaymentDate, l.IncomeAmount, l.TaxRate, l.TaxAmount, l.Condition)).ToList(),
        w.IssuedDate, w.CreatedAt,
        w.DocumentId, w.Document?.DocumentNumber,
        w.SourcePayrollRunId,
        // แก้ไขได้ = Draft + สร้างเอง (ไม่ผูกเอกสาร/payroll)
        IsEditable: w.Status == WithholdingTaxCertStatus.Draft
                    && !w.DocumentId.HasValue && !w.SourcePayrollRunId.HasValue);
}
