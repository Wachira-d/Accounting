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
        // รอบ 204: ส่งประเภทเงินได้ของบรรทัดที่มีภาษีด้วย — ดอกเบี้ย/ปันผลจ่ายบุคคลธรรมดา ⇒ ภ.ง.ด.2 (ข้อ 148)
        var (resolvedForm, corrected, reason) = ResolveWhtFormType(payee, request.TaxFormType,
            withheldIncomeTypeCodes: CertRequestIncomeCodes(request));
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

        if (request.DocumentId.HasValue)
        {
            var docOk = await _db.Documents.AsNoTracking().AnyAsync(d =>
                d.Id == request.DocumentId.Value && d.CompanyId == companyId && !d.IsDeleted);
            if (!docOk)
                throw new InvalidOperationException("ไม่พบเอกสารต้นทางที่อ้างถึง (หรืออยู่คนละบริษัท)");
            var dupDoc = await _db.WithholdingTaxCerts.AnyAsync(w =>
                w.CompanyId == companyId && w.DocumentId == request.DocumentId.Value
                && w.Status != WithholdingTaxCertStatus.Voided);
            if (dupDoc)
                throw new InvalidOperationException(
                    "เอกสารนี้มีหนังสือรับรองหัก ณ ที่จ่ายแล้ว — เปิดใบเดิมเพื่อแก้ไข หรือยกเลิกใบเดิมก่อนออกใหม่");
        }

        var cert = new WithholdingTaxCert
        {
            CompanyId = companyId,
            CertificateNumber = certNumber,
            PayeeContactId = request.PayeeContactId,
            TaxFormType = taxFormType.Value,
            TaxYear = request.TaxYear,
            TaxMonth = request.TaxMonth,
            CertificateType = request.CertificateType,
            // B1: ผูกเอกสารต้นทาง (ถ้าผู้ใช้ระบุ) — รายงาน ภ.ง.ด. ใช้ field นี้
            // ตัดสินว่าเอกสารนั้นออกใบแล้ว จึงไม่ขึ้นแถว "ยังไม่ออกหนังสือรับรอง"
            // ซ้ำกับแถว cert. ตรวจว่าเอกสารเป็นของ tenant นี้จริงก่อนผูก
            DocumentId = request.DocumentId,
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

        var created = await GetByIdAsync(companyId, cert.Id);
        return created with { Warnings = await IssueWarningsAsync(companyId, cert, payee) };
    }

    /// <summary>
    /// คำเตือนตอนออก/แก้ 50 ทวิ ด้วยมือ (ไม่บล็อก · รอบ 200 ทีม WF): (1) ใบ ภ.ง.ด.54 — อัตราแต่ละแถวเทียบตัวตัดสิน ม.70 ผ่านขอบเขตผู้รับ
    /// ตัวเดียวกับคำเตือนตอนอนุมัติเอกสาร (<see cref="ForeignWhtPayeeCheck"/> · ฝ่ายค้าน W-5) · (2) "ออกให้ตลอดไป" ที่ผูกเอกสารบริการต่างประเทศ —
    /// ภ.พ.36 ของเอกสารต้องคิดบนฐานรวมภาษีที่ออกแทน (<see cref="ForeignServiceVat.Pp36Shortfall"/> · คำตัดสินข้อ 40)
    /// </summary>
    private async Task<IReadOnlyList<string>> IssueWarningsAsync(Guid companyId, WithholdingTaxCert cert, Contact? payee)
    {
        var warnings = new List<string>();
        if (cert.TaxFormType == TaxType.WithholdingTax54)
            warnings.AddRange(ForeignWhtPayeeCheck.CertificateWarnings(
                ForeignWhtPayeeCheck.ScopeOf(payee?.TaxId, payee?.ContactType ?? ContactType.Unknown, payee?.Name),
                payee?.CountryCode,
                cert.Lines.OrderBy(l => l.LineOrder).Select(l => ((string?)l.IncomeTypeCode, l.TaxRate, l.PaymentDate)).ToList()));

        // รอบ 203: ใบในประเทศ (ภ.ง.ด.3/53) — อัตราแต่ละแถวเทียบประเภทเงินได้ + ชนิดผู้รับ ผ่านตัวตัดสินเดียวกับตอนอนุมัติเอกสาร
        // (เดิมออกใบ "ค่าเบี้ยประกัน 3%" หรือ "ค่าบริการ 1%" ได้เงียบ ๆ — หักขาด ผู้จ่ายรับผิด ม.54)
        if (cert.TaxFormType is TaxType.WithholdingTax3 or TaxType.WithholdingTax53)
        {
            var payeeJuristic = DetectJuristic(payee);
            foreach (var ln in cert.Lines.OrderBy(x => x.LineOrder))
                if (ThaiWhtRateTable.CheckRateForType(ln.IncomeTypeCode, ln.TaxRate, payeeJuristic, ln.PaymentDate) is string rateWarn)
                    warnings.Add($"แถว \"{ln.IncomeDescription}\": {rateWarn}");
        }

        if (cert.CertificateType == WithholdingTaxCertType.PayAlways && cert.DocumentId is Guid docId && cert.TotalTaxAmount > 0m)
        {
            var doc = await _db.Documents.AsNoTracking().Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.Id == docId && d.CompanyId == companyId && !d.IsDeleted);
            if (doc is { IsForeignService: true } && doc.VatAmount > 0m)
            {
                var serviceValue = DocumentVatFallback.TaxBase(doc.Lines, doc.SubTotal, doc.TotalAmount, doc.VatAmount);
                // ฝ่ายค้านรอบสอง R2M-3: บรรทัดที่คีย์ gross-up แล้ว (เงินได้บน 50 ทวิ = ยอดบรรทัด) ห้ามบวกภาษีออกแทนซ้ำ — ตัวตัดสินเดียวกับรายงาน ภ.พ.36
                var borneOutside = ForeignServiceVat.BorneTaxOutsideLines(serviceValue, doc.WithholdingTaxAmount,
                    cert.TotalIncomeAmount, cert.TotalTaxAmount);
                var gap = ForeignServiceVat.Pp36Shortfall(serviceValue, borneOutside, doc.VatAmount);
                if (gap > 0m)
                    warnings.Add($"ภ.พ.36 ของเอกสาร {doc.DocumentNumber} คิดจากฐาน {serviceValue:N2} แต่ภาษีเงินได้ที่ออกแทน {borneOutside:N2} "
                        + $"ยังไม่อยู่ในยอดบรรทัดและเป็นส่วนของมูลค่าบริการ (§79 · คำตัดสินข้อ 40) ⇒ ฐาน ภ.พ.36 = {ForeignServiceVat.Pp36Base(serviceValue, borneOutside):N2} "
                        + $"VAT ขาด {gap:N2} — แก้ VAT ของเอกสารก่อนนำส่ง หรือยื่น ภ.พ.36 เพิ่มเติมถ้ายื่นเดือนนั้นแล้ว");
            }
        }
        return warnings;
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
        var (updForm, updCorrected, updReason) = ResolveWhtFormType(updPayee, request.TaxFormType ?? cert.TaxFormType,
            withheldIncomeTypeCodes: CertRequestIncomeCodes(request));
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
            // แถวใหม่ใต้ parent ที่ติดตามอยู่ — ผ่าน EfNewChild ตัวเดียว (คอลเลกชันอย่างเดียว ⇒ Modified ⇒ UPDATE 0 แถว · Add สองทาง ⇒ แถวเบิ้ลในคอลเลกชัน ยอดเบิ้ล)
            _db.AddNewChild(cert.Lines, new WithholdingTaxCertLine
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
        var updated = await GetByIdAsync(companyId, cert.Id);
        return updated with { Warnings = await IssueWarningsAsync(companyId, cert, updPayee) };
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

        // รอบ 198 ฝ่ายค้าน C-2: ใบที่อยู่ในแบบ ภ.ง.ด. ที่ยื่นแล้ว (ผู้ถูกหักถือฉบับจริง · ยอดนำส่งแล้ว) ห้ามหายเงียบ — ตัวตัดสินตัวเดียว
        if (await WhtCertVoidGuard.CheckAsync(_db, companyId, new[] { certId }) is string filed)
            throw new BusinessRuleException(filed, "RD-50TWI-FILED", 409);

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
            _db.AddChainedAuditLog(new AuditLog
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

    // รอบ 200 ทีม WF: ป้ายแบบจากตัวตั้งเดียว (WhtUnissuedCertGate.FormLabel) — สำเนาเดิมไม่รู้จัก ภ.ง.ด.54 ⇒ ใบ 54 โชว์ "WithholdingTax54"
    private static string GetTaxFormName(TaxType type) => WhtUnissuedCertGate.FormLabel(type);

    // รอบ 203: ป้ายจากตารางกลางตัวเดียว (ThaiWhtRateTable) — เดิมเป็น switch ชุดที่สองที่ไม่รู้จัก 8ad/8tr/ประเภทใหม่
    // ⇒ ใบค่าโฆษณาโชว์ "ประเภทเงินได้ 8ad" · รหัสที่ไม่รู้จักยังคืนรูปเดิม (ไม่หายเงียบ)
    private static string GetIncomeTypeName(string code)
    {
        return ThaiWhtRateTable.Resolve(code) is { } t ? $"{t.Name} ({t.TaxSection})" : $"ประเภทเงินได้ {code}";
    }

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

        // ฝ่ายค้าน L1 (2026-10-12): ตรวจผู้ถูกหัก/เลขผู้เสียภาษี **ก่อน** ขั้นยกเลิกใบร่างระดับเอกสารข้างล่าง — เดิมตรวจหลัง ⇒ ด่านนี้ล้ม
        // (ออกใบไม่ได้) แต่ใบร่างเดิมถูกยกเลิกไปแล้ว (ทางกดเองไม่มี savepoint ย้อน) = เสียใบร่างโดยไม่ได้ใบใหม่
        // ── ผู้ถูกหักภาษี (คำขอเจ้าของ 2026-10-12) — ไม่ใช่ผู้รับเงินเสมอไป: ซื้อประกันผ่านโบรกเกอร์ ใบสำคัญจ่ายชื่อโบรกเกอร์
        // แต่ผู้มีเงินได้ = บริษัทประกัน ⇒ 50 ทวิ ชื่อ/เลขผู้เสียภาษี/สาขา/ที่อยู่/แบบ ภ.ง.ด. มาจาก "ผู้ถูกหัก" ตัวเดียว (WhtCertIssuePlan.PayeeOf)
        var payeeId = WhtCertIssuePlan.PayeeOf(doc.ContactId, doc.WhtPayeeContactId);
        var payee = payeeId == doc.ContactId
            ? doc.Contact
            : await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == payeeId && c.CompanyId == companyId)
              ?? throw new BusinessRuleException(
                  $"ไม่พบผู้ถูกหักภาษีที่เลือกไว้บนเอกสาร {doc.DocumentNumber} — เลือกผู้ถูกหักใหม่ แล้วออกใบอีกครั้ง",
                  WhtCertIssuePlan.PayeeRuleCode);
        var payeeForeign = Accounting.Helpers.WhtPayeeKind.IsForeignPayee(doc.IsForeignService, payee?.CountryCode);
        // 50 ทวิ/แบบ ภ.ง.ด. ต้องมีเลขประจำตัวผู้เสียภาษีของผู้ถูกหัก (ผู้รับต่างประเทศ ภ.ง.ด.54 ยกเว้น) — ออกใบที่ไม่มีเลข = ยื่นไม่ผ่าน
        // ⇒ ล้มดังพร้อมทางไปต่อ (ทางอัตโนมัติ: ป้ายบนเอกสาร + ลิสต์รอออกใบ · ทางกดเอง: ข้อความ 400)
        if (WhtCertIssuePlan.PayeeTaxIdProblem(payee?.Name, payee?.TaxId, payeeForeign) is string payeeProblem)
            throw new BusinessRuleException(payeeProblem, WhtCertIssuePlan.PayeeRuleCode);

        // Idempotency — ภ.ง.ด.3/53 เป็น cash basis: จ่ายเป็นงวดต้องออกใบ "งวดละใบ"
        // ตามยอดที่หักจริงของงวดนั้น จึงเช็คซ้ำที่ระดับ "งวดการจ่าย" เมื่อระบุ
        // sourcePaymentId มา (เดิมเช็คระดับเอกสาร → งวดที่ 2 เป็นต้นไปออกใบไม่ได้
        // และใบแรกก็ระบุยอดเต็มทั้งเอกสารทั้งที่จ่ายไปแค่บางส่วน)
        if (sourcePaymentId.HasValue)
        {
            // ซ้ำ = "งวดจ่ายเดียวกัน + เอกสารเดียวกัน" — การโอนก้อนเดียวปิดหลายใบ
            // ใช้ payment id ร่วมกันทุกใบ ถ้าเช็คแค่ payment id ใบที่ 2 เป็นต้นไป
            // จะออก 50 ทวิ ไม่ได้เลย (นำส่งขาดของผู้ขายรายอื่นในโอนเดียวกัน)
            var dupPayment = await _db.WithholdingTaxCerts
                .AnyAsync(w => w.CompanyId == companyId && w.SourcePaymentId == sourcePaymentId.Value
                    && w.DocumentId == documentId
                    && w.Status != WithholdingTaxCertStatus.Voided);
            if (dupPayment)
                throw new InvalidOperationException("งวดการจ่ายนี้มีหนังสือรับรองหัก ณ ที่จ่ายแล้ว");

            // B2: ถ้าเอกสารนี้ "ออกใบเต็มจำนวนไปแล้ว" (cert ที่ไม่ผูกงวดจ่าย —
            // ออกตอน approve) ห้ามออกใบรายงวดทับอีก ⇒ ยอดเดียวนำส่ง 2 ครั้ง
            // ทั้งบนรายงานและในไฟล์ยื่น. เดิมสองสาขาเช็คคนละ key จึงลอดกันได้
            // (ทิศกลับกันถูกบล็อกอยู่แล้วที่สาขา else)
            var fullDocCerts = await _db.WithholdingTaxCerts
                .Where(w => w.CompanyId == companyId && w.DocumentId == documentId
                    && w.SourcePaymentId == null
                    && w.Status != WithholdingTaxCertStatus.Voided)
                .ToListAsync();

            // ใบ "ฉบับร่าง" ไม่เคยส่งมอบผู้ขายและไม่เข้าแบบยื่น (ภ.ง.ด.3/53 นับ
            // เฉพาะ Issued/Filed) ⇒ ยกเลิกให้อัตโนมัติแล้วออกรายงวดต่อได้เลย
            // ที่มา (P-3): integration ตั้งหนี้ค่าใช้จ่ายสร้างใบระดับเอกสารไว้ล่วงหน้า
            // แล้วบล็อกใบรายงวดตอนจ่ายจริง — ผู้ขายไม่ได้ 50 ทวิ สักใบ
            var draftFull = fullDocCerts.Where(w => w.Status == WithholdingTaxCertStatus.Draft).ToList();
            if (draftFull.Count > 0 && draftFull.Count == fullDocCerts.Count)
            {
                foreach (var d in draftFull)
                    d.Status = WithholdingTaxCertStatus.Voided;
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "ยกเลิกหนังสือรับรองฉบับร่างระดับเอกสาร {Count} ใบ ของเอกสาร {DocId} — ออกรายงวดแทน",
                    draftFull.Count, documentId);
            }
            else if (fullDocCerts.Count > 0)
            {
                throw new InvalidOperationException(
                    "เอกสารนี้ออกหนังสือรับรองเต็มจำนวนไปแล้ว — ออกใบรายงวดเพิ่มจะทำให้นำส่งซ้ำ "
                    + "(ยกเลิกใบเต็มจำนวนก่อน ถ้าต้องการออกเป็นรายงวดแทน)");
            }
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
        // Individual (เคสบริษัทที่ contact สร้างจาก integration แล้วไม่ตั้ง type) · ตัดสินจาก "ผู้ถูกหัก" ไม่ใช่ผู้รับเงิน
        // รอบ 204 (ข้อ 148): ประเภทเงินได้ของบรรทัดที่หัก — ชุดเดียวกับ JE (WhtIncomeCodes) ⇒ ดอกเบี้ย/ปันผลบุคคลธรรมดา = ภ.ง.ด.2 ตรงผัง 21915
        var (taxFormType, _, _) = ResolveWhtFormType(payee, null, doc.IsForeignService,
            await _db.LoadWithheldAsync(companyId, doc));

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
            PayeeContactId = payeeId,
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
        // ออกใบได้แล้ว (ทางใดก็ตาม) ⇒ ป้าย "ออกอัตโนมัติไม่สำเร็จ" ของเอกสารหมดความหมาย — ล้างในคำสั่งบันทึกเดียวกับใบ
        doc.InternalNotes = WhtCertAutoFailedNote.Clear(doc.InternalNotes);
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

    /// <summary>
    /// **ทางเดียวของการออก 50 ทวิ "อัตโนมัติ" จากเอกสาร** (อนุมัติ · บันทึกจ่าย · จ่ายหลายใบ · ซิงค์ · สร้างทั้งเดือน) — คำขอเจ้าของ 2026-10-12
    ///
    /// <para>1) โหลดข้อเท็จจริง (ใบที่มีอยู่ · ติ๊กออกอัตโนมัติ · ผู้ถูกหัก) → 2) <see cref="WhtCertIssuePlan.Decide"/> ตัวเดียว →
    /// 3) ออกใบผ่าน <see cref="AutoGenerateFromDocumentAsync"/> · ล้ม = <b>ไม่กลืน</b>: ป้ายบนเอกสาร (<see cref="WhtCertAutoFailedNote"/>) +
    /// เหตุผลกลับในผลลัพธ์ (ผู้เรียกส่งต่อถึงหน้าจอ) + ใบโผล่ในลิสต์รอออกใบ · การอนุมัติ/การจ่ายเองยังสำเร็จ (ห้ามบล็อกการจ่ายเงิน)</para>
    ///
    /// <para>ในธุรกรรมของผู้เรียก: ครอบด้วย savepoint — ออกใบล้มระดับฐานข้อมูล (PostgreSQL ตั้ง transaction เป็น aborted) ต้องไม่ลาก
    /// การอนุมัติ/การจ่ายทั้งก้อนล้มตาม · ถอดแถวใบที่ค้างใน ChangeTracker ด้วย ไม่งั้น SaveChanges ครั้งถัดไปของผู้เรียกจะพยายามบันทึกซ้ำ</para>
    /// </summary>
    public async Task<WhtAutoIssueOutcome> TryAutoIssueForDocumentAsync(
        Guid companyId, Guid documentId, WhtCertIssueTrigger trigger, string createdBy,
        DateTime? paymentDate = null, Guid? sourcePaymentId = null, decimal? paymentWhtAmount = null,
        bool paidAtTrigger = true, bool issueAsIssued = true)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId);
        if (doc == null)
            return new WhtAutoIssueOutcome(WhtCertIssueAction.NotApplicable, null, null, null, "ไม่พบเอกสาร");

        var active = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.DocumentId == documentId
                && w.Status != WithholdingTaxCertStatus.Voided)
            .Select(w => new { w.SourcePaymentId, w.Status })
            .ToListAsync();
        var decision = WhtCertIssuePlan.Decide(new WhtCertIssueFacts(
            trigger, doc.DocumentType, doc.Status, paymentWhtAmount ?? doc.WithholdingTaxAmount,
            AutoIssueEnabled: doc.WhtCertAutoIssue,
            doc.ContactId, doc.WhtPayeeContactId,
            PaidAtTrigger: paidAtTrigger,
            HasSourcePayment: sourcePaymentId.HasValue,
            ActiveCertsForThisPayment: sourcePaymentId.HasValue ? active.Count(a => a.SourcePaymentId == sourcePaymentId) : 0,
            ActiveFullDocCertsNotDraft: active.Count(a => a.SourcePaymentId == null && a.Status != WithholdingTaxCertStatus.Draft),
            ActiveCertsAny: active.Count));
        if (decision.Action != WhtCertIssueAction.IssueNow)
            return new WhtAutoIssueOutcome(decision.Action, null, null, null, decision.Reason);

        var tx = _db.Database.CurrentTransaction is { SupportsSavepoints: true } current ? current : null;
        const string savepoint = "wht_cert_auto_issue";
        if (tx != null)
        {
            // บันทึกงานค้างของผู้เรียก (การอนุมัติ/การจ่าย) ก่อนตั้ง savepoint — ไม่งั้น SaveChanges ข้างในจะพางานของผู้เรียกเข้าไปหลัง
            // savepoint แล้วถูกย้อนทิ้งตอนออกใบล้ม ขณะที่ ChangeTracker เชื่อว่าบันทึกแล้ว (งานอนุมัติหายเงียบ)
            await _db.SaveChangesAsync();
            await tx.CreateSavepointAsync(savepoint);
        }
        try
        {
            var cert = await AutoGenerateFromDocumentAsync(companyId, documentId, issueAsIssued, createdBy,
                paymentDate, sourcePaymentId, paymentWhtAmount);
            return new WhtAutoIssueOutcome(WhtCertIssueAction.IssueNow, cert.Id, cert.CertificateNumber, null,
                $"ออกหนังสือรับรองหัก ณ ที่จ่าย {cert.CertificateNumber}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (tx != null) await tx.RollbackToSavepointAsync(savepoint);
            // ถอดแถว 50 ทวิ ทุกสถานะออกจาก ChangeTracker — Added (ยังไม่ลงฐาน) จะถูกบันทึกซ้ำตอนผู้เรียก SaveChanges ·
            // Unchanged/Modified หลัง savepoint ถูกย้อนในฐานแล้วแต่ tracker ยังจำค่าใหม่ (สถานะค้างผิดความจริง)
            foreach (var entry in _db.ChangeTracker.Entries()
                         .Where(e => e.Entity is WithholdingTaxCert or WithholdingTaxCertLine)
                         .ToList())
                entry.State = EntityState.Detached;

            // ล้มหลังบันทึกใบไปแล้ว (นอกธุรกรรม — เช่นตอนแนบไฟล์/อ่านกลับ) = ใบมีอยู่จริง ⇒ ไม่ใช่ความล้มเหลวของการออกใบ
            // (ก่อนเข้ามา ยังไม่มีใบของการจ่ายนี้ — ไม่งั้นตัวตัดสินตอบ AlreadyIssued ไปแล้ว)
            var nowCovered = await _db.WithholdingTaxCerts.AsNoTracking().AnyAsync(w =>
                w.CompanyId == companyId && w.DocumentId == documentId
                && w.Status != WithholdingTaxCertStatus.Voided
                && (sourcePaymentId == null || w.SourcePaymentId == sourcePaymentId));
            if (nowCovered)
                return new WhtAutoIssueOutcome(WhtCertIssueAction.AlreadyIssued, null, null, null,
                    "ออกหนังสือรับรองหัก ณ ที่จ่ายของการจ่ายนี้แล้ว");

            // ข้อความถึงผู้ใช้: กฎธุรกิจ = ข้อความไทยตรง ๆ · อย่างอื่น = ชนิดข้อผิดพลาด (ห้ามรั่วข้อความภายในของ framework)
            var reason = ex is BusinessRuleException or KeyNotFoundException
                    || (ex is InvalidOperationException && ex.GetType() == typeof(InvalidOperationException)
                        && ex.Message.Any(ch => ch >= '\u0E00' && ch <= '\u0E7F'))
                ? ex.Message
                : $"ระบบขัดข้อง ({ex.GetType().Name}) — กด \"สร้างใบ\" ที่ลิสต์รอออกใบเพื่อลองใหม่";
            _logger.LogWarning(ex, "ออก 50 ทวิ อัตโนมัติ ({Trigger}) ของเอกสาร {Doc} ไม่สำเร็จ — ติดป้ายบนเอกสาร + ลิสต์รอออกใบ",
                trigger, doc.DocumentNumber);
            doc.InternalNotes = WhtCertAutoFailedNote.Append(doc.InternalNotes, reason);
            await _db.SaveChangesAsync();
            return new WhtAutoIssueOutcome(WhtCertIssueAction.IssueNow, null, null, reason,
                $"ออก 50 ทวิ อัตโนมัติไม่สำเร็จ: {reason}");
        }
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
                // ชุดชนิด "เราเป็นผู้หัก" อยู่ที่ Helpers/WhtRemitScope ที่เดียว — เดิมพิมพ์เอง 3 ชนิด
                // ตก CertificateInLieu ที่ hook auto-gen และหน้านำส่งนับอยู่ ⇒ ใบรับรองแทนใบเสร็จที่ออก
                // 50 ทวิ ไม่สำเร็จจะไม่โผล่ในลิสต์ "รอออกใบ" เลย
                && Accounting.Helpers.WhtRemitScope.PayerSideTypes.Contains(d.DocumentType));

        if (year.HasValue)
            query = query.Where(d => d.DocumentDate.Year == year.Value);
        if (month.HasValue)
            query = query.Where(d => d.DocumentDate.Month == month.Value);

        var docs = await query.OrderByDescending(d => d.DocumentDate).ToListAsync();
        await _db.HydrateContactsAsync(companyId, docs);
        // ผู้ถูกหักที่ไม่ใช่ผู้ติดต่อของเอกสาร (ประกันผ่านโบรกเกอร์) — ลิสต์ต้องบอกว่า 50 ทวิ จะออกในนามใคร
        var payees = await _db.LoadWhtPayeeContactsAsync(companyId, docs);

        return docs.Select(d =>
        {
            var payee = ContactHydration.WhtPayeeOf(d, payees);
            var failed = WhtCertAutoFailedNote.ReasonOf(d.InternalNotes);
            var paid = d.Status == DocumentStatus.Paid || (d.BalanceDue <= 0.005m && d.PaidAmount > 0.005m);
            return new PendingWhtDocumentResponse(
                d.Id, d.DocumentNumber, d.DocumentType.ToString(), d.DocumentDate,
                d.ContactId, d.Contact?.Name ?? "", d.Contact?.TaxId,
                d.SubTotal, d.WithholdingTaxAmount,
                d.Lines.Where(l => l.WithholdingTaxAmount > 0).Select(l => new PendingWhtLineInfo(
                    l.Description, l.IncomeTypeCode, l.Amount,
                    l.WithholdingTaxRate, l.WithholdingTaxAmount)).ToList(),
                PayeeContactId: WhtCertIssuePlan.PayeeOf(d.ContactId, d.WhtPayeeContactId),
                PayeeName: payee?.Name,
                PayeeTaxId: payee?.TaxId,
                PayeeDiffersFromContact: d.WhtPayeeContactId.HasValue && d.WhtPayeeContactId != d.ContactId,
                AutoIssueEnabled: d.WhtCertAutoIssue,
                AutoIssueFailedReason: failed,
                PendingLabel: WhtCertIssuePlan.PendingLabel(d.WhtCertAutoIssue, failed, paid));
        }).ToList();
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
            // ทางเดียวกับอนุมัติ/บันทึกจ่าย (WhtCertIssuePlan) — ใบที่ตั้ง "ไม่สร้างอัตโนมัติ" ไม่ถูกกวาดออกทั้งเดือน
            // (ผู้ใช้ตั้งใจตรวจผู้ถูกหักเอง) ⇒ ข้ามพร้อมเหตุผล · ล้ม = ป้ายบนเอกสาร + เหตุผลในผลลัพธ์ ไม่หายเงียบ
            var outcome = await TryAutoIssueForDocumentAsync(companyId, doc.DocumentId, WhtCertIssueTrigger.Bulk,
                createdBy, issueAsIssued: request.AutoIssue);
            if (outcome.CertificateId is Guid certId)
            {
                generated.Add(await GetByIdAsync(companyId, certId));
                continue;
            }
            skipped++;
            skippedReasons.Add($"{doc.DocumentNumber}: {outcome.FailureReason ?? outcome.Message}");
        }

        return new BulkGenerateWhtResponse(
            pending.Count, generated.Count, skipped, skippedReasons, generated);
    }

    // ==================== Private Helpers ====================

    /// <summary>
    /// วิเคราะห์แบบ ภ.ง.ด. อัตโนมัติจาก ContactType ของผู้ติดต่อ
    /// นิติบุคคล → ภ.ง.ด.53, บุคคลธรรมดา → ภ.ง.ด.3
    /// </summary>
    /// <summary>รหัสเงินได้ของบรรทัดคำขอที่มีภาษี (ใบคีย์มือ/แก้ใบ) — เกณฑ์เดียวกับ <see cref="Accounting.Helpers.WhtIncomeCodes"/> (ภาษี &gt; 0)</summary>
    private static IReadOnlyList<string?> CertRequestIncomeCodes(CreateWithholdingTaxCertRequest request)
        => (request.Lines ?? new List<WithholdingTaxCertLineRequest>())
            .Where(l => l.TaxAmount > 0m).Select(l => (string?)l.IncomeTypeCode).ToList();

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
        => contact == null
            ? null
            : Accounting.Helpers.WhtPayeeKind.Detect(
                contact.TaxId, contact.ContactType, contact.Name);

    /// <summary>เลือกประเภทแบบ ภ.ง.ด. ที่ "ถูกต้อง" จาก payee — ตรวจ/แก้แม้ caller
    /// (เช่น มังกร) ส่ง TaxFormType มาแล้ว. แก้เฉพาะแกน ภ.ง.ด.3 ↔ 53 (ขึ้นกับผู้ถูก
    /// หัก); ภ.ง.ด.1 (เงินเดือน) / ภ.ง.ด.2 (ดอกเบี้ย/ปันผล) ขึ้นกับประเภทเงินได้ —
    /// ไม่แตะ. คืน (form ที่ถูก, corrected=แก้จากที่ขอมาไหม, reason).</summary>
    /// <param name="documentIsForeignService">ธง §83/6 ของ **เอกสาร** — ต้องส่งเข้ามา
    /// ด้วยเสมอเมื่อมีเอกสารต้นทาง: เดิมตัวนี้ดูแต่ <c>Contact.CountryCode</c>
    /// ⇒ เคส Booking.com ที่ติ๊ก §83/6 แต่คู่ค้าไม่ได้กรอกประเทศ (เคสที่คอมเมนต์ใน
    /// เรพเขียนเองว่าเกิดบ่อย) ออก 50 ทวิ เป็น **ภ.ง.ด.3/53** ⇒ รายงาน ภ.ง.ด.54
    /// หาใบนั้นไม่เจอ (cert คนละ TaxFormType) และ mine จากเอกสารก็ไม่ได้เพราะถูก
    /// <c>certCoveredDocIds</c> ตัดออก ⇒ <b>ใบหายจาก ภ.ง.ด.54 ทั้งใบ ไปโผล่ 53 แทน</b>
    /// ขณะที่ GL ตั้งหนี้ไว้ที่ 21918 ⇒ ยื่นผิดแบบ + 21918 ไม่มีวันถูกล้าง</param>
    /// <param name="withheldIncomeTypeCodes">รอบ 204 (คำตัดสินข้อ 148): รหัสเงินได้ของบรรทัดที่หัก — ผู้รับบุคคลธรรมดาที่ทุกบรรทัดเป็น ม.40(4)
    /// ⇒ ภ.ง.ด.2 (ตัวตัดสินเดียว <see cref="Accounting.Helpers.WhtPayeeKind.IndividualForm"/>) · null = ไม่รู้ประเภท (พฤติกรรมเดิม)</param>
    internal static (TaxType formType, bool corrected, string reason) ResolveWhtFormType(
        Contact? contact, TaxType? requested, bool documentIsForeignService = false,
        IEnumerable<string?>? withheldIncomeTypeCodes = null)
    {
        // แบบที่ไม่ใช่แกน 2/3/53 → ปล่อยตามที่ขอ (ภ.ง.ด.1 เงินเดือน · ภ.ง.ด.54 ที่ผู้เรียกระบุเอง)
        // รอบ 204: ภ.ง.ด.2 เข้าแกนแล้ว — เดิมปล่อยผ่านเฉย ๆ ⇒ ขอ ภ.ง.ด.2 ให้บริษัทก็ได้ (ผิดแบบ: นิติบุคคลอยู่ ภ.ง.ด.53)
        if (requested.HasValue
            && requested.Value != TaxType.WithholdingTax2
            && requested.Value != TaxType.WithholdingTax3
            && requested.Value != TaxType.WithholdingTax53)
            return (requested.Value, false, "");

        // ผู้รับต่างประเทศ (ม.70) → ภ.ง.ด.54 เท่านั้น — เดิม resolver คืนได้แค่
        // 3/53 ⇒ 50 ทวิ ของ payee ต่างประเทศถูกออกเป็น ภงด.3/53 → เข้ารายงาน+
        // ไฟล์ 3/53 ขณะที่ ภงด.54 (doc-mined) ก็นับเอกสารเดิม = นำส่งซ้ำสองแบบ
        var isForeignPayee = Accounting.Helpers.WhtPayeeKind.IsForeignPayee(
            documentIsForeignService, contact?.CountryCode);
        if (isForeignPayee)
            return (TaxType.WithholdingTax54, requested.HasValue && requested.Value != TaxType.WithholdingTax54,
                "payee ต่างประเทศ (ม.70 → ภ.ง.ด.54)");

        var juristic = DetectJuristic(contact);
        TaxType correct;
        string reason;
        if (juristic == true) { correct = TaxType.WithholdingTax53; reason = "payee เป็นนิติบุคคล"; }
        else if (juristic == false) { correct = TaxType.WithholdingTax3; reason = "payee เป็นบุคคลธรรมดา"; }
        else if (contact != null) { correct = DetermineTaxFormType(contact); reason = "จาก ContactType"; }
        else { correct = requested ?? TaxType.WithholdingTax3; reason = "ไม่พบ payee"; }

        // ── แกนประเภทเงินได้ของผู้รับบุคคลธรรมดา (รอบ 204 ข้อ 148): ม.40(4) ทั้งใบ ⇒ ภ.ง.ด.2 (ม.50(2) · ม.59) ──
        // ผู้ใช้ขอ ภ.ง.ด.2 เองกับบุคคลธรรมดา ⇒ เคารพ (แบบ ภ.ง.ด.2 ของกรมมีแถว ม.40(3) ด้วย — ตารางยังไม่ตัดสินเอง ข้อเสนอข้อ 149)
        if (correct == TaxType.WithholdingTax3)
        {
            if (requested == TaxType.WithholdingTax2)
                return (TaxType.WithholdingTax2, false, "ผู้ใช้เลือก ภ.ง.ด.2 · payee บุคคลธรรมดา");
            if (Accounting.Helpers.WhtPayeeKind.IndividualForm(withheldIncomeTypeCodes) == TaxType.WithholdingTax2)
            { correct = TaxType.WithholdingTax2; reason += " · เงินได้ ม.40(4) ทั้งใบ (ภ.ง.ด.2)"; }
        }

        var corrected = requested.HasValue && requested.Value != correct;
        return (correct, corrected, reason);
    }

    /// <summary>ที่อยู่บนหนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) — ใช้ตัวประกอบกลาง
    /// <see cref="ThaiAddressFormatter"/> ตัวเดียวกับ PDF/HTML ทุกเส้นทาง
    ///
    /// เดิมฟังก์ชันนี้เขียนเอง 2 สาขาและ<b>ผิดทั้งคู่</b>:
    ///   1. free-text ที่ "ดูครบ" (มีรหัสไปรษณีย์/ชื่อจังหวัด) → คืนดิบ ๆ ทันที
    ///      → ที่อยู่ที่ผู้ใช้พิมพ์แบบไม่มีคำนำหน้า ("44 หมู่ 9 หนองเหียง พนัสนิคม
    ///      ชลบุรี 20140") ถูกพิมพ์ลงเอกสารราชการทั้งอย่างนั้น
    ///   2. structured fallback → join ด้วยช่องว่างเปล่า ๆ ไม่มี ต./อ./จ. เลย
    ///      และไม่รู้จัก กทม. (ต้องเป็น แขวง/เขต)
    /// 50 ทวิ เป็นเอกสารยื่นสรรพากร — ที่อยู่ต้องอ่านออกว่าส่วนไหนตำบล/อำเภอ/
    /// จังหวัด ตามข้อความกำกับในแบบฟอร์มเอง</summary>
    private static string ComposeFullAddress(string? address, string? subDistrict, string? district, string? province, string? postalCode,
        string? moo = null, string? buildingNumber = null, string? streetName = null, string? buildingName = null)
        => ThaiAddressFormatter.Format(address, buildingNumber, buildingName, moo, streetName,
            subDistrict, district, province, postalCode);

    /// <summary>บรรทัด 50 ทวิ → คำตอบ — แถวบนแบบ (<c>CertificateRow</c>) จากตัวตัดสินเดียวกับ renderer ทั้งสอง (ฝ่ายค้าน M6 · หน้าเว็บแสดงอย่างเดียว)</summary>
    private static WithholdingTaxCertLineResponse MapLineToResponse(WithholdingTaxCertLine l)
    {
        return new WithholdingTaxCertLineResponse(
            l.Id, l.IncomeTypeCode, GetIncomeTypeName(l.IncomeTypeCode),
            l.IncomeDescription, l.PaymentDate, l.IncomeAmount, l.TaxRate, l.TaxAmount, l.Condition,
            Accounting.Helpers.WhtCertFormText.LineSpec(l),
            CertificateRow: Accounting.Helpers.ThaiWhtRateTable.CertificateRow(l.IncomeTypeCode));
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
        w.Lines.OrderBy(l => l.LineOrder).Select(MapLineToResponse).ToList(),
        w.IssuedDate, w.CreatedAt,
        w.DocumentId, w.Document?.DocumentNumber,
        w.SourcePayrollRunId,
        // แก้ไขได้ = Draft + สร้างเอง (ไม่ผูกเอกสาร/payroll)
        IsEditable: w.Status == WithholdingTaxCertStatus.Draft
                    && !w.DocumentId.HasValue && !w.SourcePayrollRunId.HasValue,
        CertificateRowSpecs: CertificateRowSpecs(w));

    /// <summary>ข้อความ "ระบุ" ของแถว 5/6 (รอบ 203) — ตัวตัดสินเดียว <see cref="ThaiWhtRateTable.CertificateRowSpecification"/></summary>
    private static IReadOnlyDictionary<string, string> CertificateRowSpecs(WithholdingTaxCert w)
    {
        var lines = w.Lines.OrderBy(l => l.LineOrder)
            .Select(l => ((string?)l.IncomeTypeCode, (string?)l.IncomeDescription)).ToList();
        var specs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in ThaiWhtRateTable.CertificateRowsWithSpecification)
            if (ThaiWhtRateTable.CertificateRowSpecification(row, lines) is string spec)
                specs[row] = spec;
        return specs;
    }
}
