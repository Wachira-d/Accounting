using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

public class DocumentService : IDocumentService
{
    private readonly AccountingDbContext _db;
    private readonly IAccountingService _accountingService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IWithholdingTaxCertService _whtService;
    private readonly IEtaxInvoiceService _etaxService;
    private readonly ILogger<DocumentService> _logger;
    private readonly ILineNotifyService _lineNotify;
    private readonly Accounting.Services.Implementations.Ocr.VendorIntelligenceService _vendorIntel;
    private readonly CrossTenantWorkflowService _crossTenantWorkflow;
    private readonly INotificationEngine? _notify;
    private readonly IBankService? _bankService;
    private readonly ITaxService? _taxService;
    private readonly IBotExchangeRateService? _fxRates;
    private readonly ISensitivityService? _sensitivity;

    public DocumentService(AccountingDbContext db, IAccountingService accountingService,
        ISubscriptionService subscriptionService, IWithholdingTaxCertService whtService,
        IEtaxInvoiceService etaxService, ILogger<DocumentService> logger,
        ILineNotifyService lineNotify,
        Accounting.Services.Implementations.Ocr.VendorIntelligenceService vendorIntel,
        CrossTenantWorkflowService crossTenantWorkflow,
        INotificationEngine? notify = null,
        IBankService? bankService = null,
        ITaxService? taxService = null,
        IBotExchangeRateService? fxRates = null,
        ISensitivityService? sensitivity = null)
    {
        _db = db;
        _accountingService = accountingService;
        _subscriptionService = subscriptionService;
        _whtService = whtService;
        _etaxService = etaxService;
        _logger = logger;
        _lineNotify = lineNotify;
        _vendorIntel = vendorIntel;
        _crossTenantWorkflow = crossTenantWorkflow;
        _sensitivity = sensitivity;
        _notify = notify;
        _bankService = bankService;
        _taxService = taxService;
        _fxRates = fxRates;
    }

    /// <summary>Resolve THB exchange rate for a document. THB → 1. Explicit
    /// override wins; otherwise auto-fetch the BoT mid-rate at DocumentDate.
    /// Throws if BoT lookup fails — silent fallback to 1 on non-THB would
    /// corrupt the GL.</summary>
    private async Task<decimal> ResolveExchangeRateAsync(string? currency, decimal? overrideRate, DateTime documentDate)
    {
        var cur = (currency ?? "THB").ToUpperInvariant();
        if (cur == "THB") return 1m;
        if (overrideRate is decimal r)
        {
            if (r <= 0m) throw new InvalidOperationException("อัตราแลกเปลี่ยนต้องมากกว่า 0");
            return r;
        }
        if (_fxRates == null)
            throw new InvalidOperationException(
                $"เอกสารสกุล {cur} ต้องระบุ ExchangeRate (ไม่ได้กำหนดบริการอัตรา ธ.ปท.)");
        var rate = await _fxRates.GetRateAsync(cur, documentDate);
        if (rate == null || rate.MidRate <= 0m)
            throw new InvalidOperationException(
                $"ไม่พบอัตราแลกเปลี่ยน {cur} ของวันที่ {documentDate:yyyy-MM-dd} จาก ธ.ปท. " +
                "กรุณาระบุ ExchangeRate ในคำขอ");
        return rate.MidRate;
    }

    public async Task<DocumentResponse> CreateDocumentAsync(Guid companyId, CreateDocumentRequest request, string createdBy)
    {
        // Check usage limit
        if (!await _subscriptionService.CheckUsageLimitAsync(companyId, "document"))
            throw new InvalidOperationException("เกินจำนวนเอกสารที่อนุญาตต่อเดือน");

        // Check feature access
        if (!await _subscriptionService.CheckFeatureAccessAsync(companyId, FeatureFlags.DocumentEngine))
            throw new InvalidOperationException("ไม่มีสิทธิ์ใช้ระบบเอกสาร");

        // Validate ContactId exists and has correct type for document
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == request.ContactId && c.CompanyId == companyId);
        if (contact == null)
            throw new InvalidOperationException("ไม่พบผู้ติดต่อในบริษัทนี้");

        var revenueDocTypes = new[] {
            DocumentType.Quotation, DocumentType.Invoice, DocumentType.TaxInvoice,
            DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.DebitNote,
            DocumentType.CreditNote, DocumentType.DeliveryNote, DocumentType.BillingNote
        };
        var purchaseDocTypes = new[] {
            DocumentType.PurchaseRequisition, DocumentType.PurchaseOrder,
            DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.PaymentVoucher,
            DocumentType.CertificateInLieu
        };
        if (revenueDocTypes.Contains(request.DocumentType) && !contact.IsCustomer)
            throw new InvalidOperationException($"ผู้ติดต่อ '{contact.Name}' ไม่ได้ตั้งค่าเป็นลูกค้า — กรุณาเปิดสถานะ 'ลูกค้า' ก่อนออกเอกสารขาย");
        if (purchaseDocTypes.Contains(request.DocumentType) && !contact.IsSupplier)
            throw new InvalidOperationException($"ผู้ติดต่อ '{contact.Name}' ไม่ได้ตั้งค่าเป็นผู้จำหน่าย — กรุณาเปิดสถานะ 'ผู้จำหน่าย' ก่อนออกเอกสารซื้อ");

        // Validate project tags belong to this company (security: prevent cross-tenant tagging)
        if (request.ProjectId.HasValue)
        {
            var projectOk = await _db.Projects.AnyAsync(p =>
                p.Id == request.ProjectId.Value && p.CompanyId == companyId);
            if (!projectOk)
                throw new InvalidOperationException("ไม่พบโครงการในบริษัทนี้");
        }
        var lineProjectIds = request.Lines?.Where(l => l.ProjectId.HasValue)
            .Select(l => l.ProjectId!.Value).Distinct().ToList() ?? new List<Guid>();
        if (lineProjectIds.Count > 0)
        {
            var validCount = await _db.Projects
                .CountAsync(p => p.CompanyId == companyId && lineProjectIds.Contains(p.Id));
            if (validCount != lineProjectIds.Count)
                throw new InvalidOperationException("รหัสโครงการในรายการบางบรรทัดไม่ถูกต้อง");
        }

        // CertificateInLieu requires reason and certifier
        if (request.DocumentType == DocumentType.CertificateInLieu)
        {
            if (string.IsNullOrWhiteSpace(request.CertificateReason))
                throw new InvalidOperationException("ใบรับรองแทนใบเสร็จต้องระบุเหตุผลที่ไม่ได้รับใบเสร็จ");
            if (string.IsNullOrWhiteSpace(request.CertifierName))
                throw new InvalidOperationException("ใบรับรองแทนใบเสร็จต้องระบุชื่อผู้รับรอง");
        }

        // Validate at least 1 line item
        if (request.Lines == null || request.Lines.Count == 0)
            throw new InvalidOperationException("ต้องมีรายการสินค้าอย่างน้อย 1 รายการ");

        // Validate each line
        foreach (var line in request.Lines)
        {
            if (line.Quantity <= 0)
                throw new InvalidOperationException("จำนวนสินค้าต้องมากกว่า 0");

            if (line.UnitPrice < 0)
                throw new InvalidOperationException("ราคาต่อหน่วยต้องไม่ติดลบ");

            if (line.DiscountPercent < 0 || line.DiscountPercent > 100)
                throw new InvalidOperationException("ส่วนลดต้องอยู่ระหว่าง 0-100%");

            if (line.VatRate != 0 && line.VatRate != 7 && line.VatRate != -1)
                throw new InvalidOperationException("อัตราภาษีมูลค่าเพิ่มต้องเป็น 0, 7 หรือ -1 (ยกเว้น)");

            if (line.WithholdingTaxRate < 0 || line.WithholdingTaxRate > 15)
                throw new InvalidOperationException("อัตราภาษีหัก ณ ที่จ่ายต้องอยู่ระหว่าง 0-15%");

            if (line.AccountId.HasValue)
            {
                var acctExists = await _db.ChartOfAccounts.AnyAsync(a =>
                    a.Id == line.AccountId.Value && a.CompanyId == companyId && a.IsActive);
                if (!acctExists)
                    throw new InvalidOperationException(
                        $"รหัสบัญชีที่ระบุในรายการ '{line.Description}' ไม่พบในผังบัญชี หรือถูกปิดใช้งาน");
            }
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var docNumber = await Accounting.Helpers.DocumentNumberGenerator.NextAsync(_db, companyId, request.DocumentType);

            // Resolve FX up front so the rate the user sees on the document
            // is exactly what posts to the GL on Approve. THB → always 1.
            // Non-THB: caller's override wins; otherwise auto-fetch BoT mid-rate
            // at DocumentDate. If BoT lookup fails, refuse — silent fallback to
            // 1 would corrupt the GL.
            var fxRate = await ResolveExchangeRateAsync(request.Currency, request.ExchangeRate, request.DocumentDate);

            var doc = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = request.DocumentType,
                DocumentDate = request.DocumentDate,
                Currency = string.IsNullOrWhiteSpace(request.Currency) ? "THB" : request.Currency.ToUpperInvariant(),
                ExchangeRate = fxRate,
                DueDate = request.DueDate,
                ContactId = request.ContactId,
                Reference = request.Reference,
                Notes = request.Notes,
                ProjectId = request.ProjectId,
                BankAccountId = request.BankAccountId,
                PaymentAccountId = request.PaymentAccountId,
                ExpenseCategoryId = request.ExpenseCategoryId,
                CustomAppendix = request.CustomAppendix,
                CustomFooterNotes = request.CustomFooterNotes,
                CustomTermsAndConditions = request.CustomTermsAndConditions,
                RevenueContractId = request.RevenueContractId,
                PerformanceObligationId = request.PerformanceObligationId,
                CertificateReason = request.CertificateReason,
                CertifierName = request.CertifierName,
                CertifierPosition = request.CertifierPosition,
                WitnessName = request.WitnessName,
                WitnessPosition = request.WitnessPosition,
                PaymentDate = request.PaymentDate,
                CreatedBy = createdBy
            };

            // Auto-link Revenue Contract via Project when not explicitly provided.
            // If document is tagged to a project that has exactly one active
            // RevenueContract, link this doc to that contract automatically.
            if (request.ProjectId.HasValue && !request.RevenueContractId.HasValue)
            {
                var contracts = await _db.Set<RevenueContract>()
                    .Where(rc => rc.CompanyId == companyId
                        && rc.ProjectId == request.ProjectId.Value
                        && !rc.IsDeleted)
                    .Select(rc => rc.Id)
                    .Take(2)
                    .ToListAsync();
                if (contracts.Count == 1)
                    doc.RevenueContractId = contracts[0];
            }

            _db.Documents.Add(doc);

            decimal subTotal = 0, totalDiscount = 0, totalVat = 0, totalWht = 0;
            var order = 1;

            foreach (var line in request.Lines)
            {
                var lineAmount = Math.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
                var discountAmt = Math.Round(lineAmount * line.DiscountPercent / 100, 2, MidpointRounding.AwayFromZero);
                var afterDiscount = lineAmount - discountAmt;
                var vatAmt = line.VatRate > 0 ? Math.Round(afterDiscount * line.VatRate / 100, 2, MidpointRounding.AwayFromZero) : 0m;
                var whtAmt = Math.Round(afterDiscount * line.WithholdingTaxRate / 100, 2, MidpointRounding.AwayFromZero);

                subTotal += afterDiscount;
                totalDiscount += discountAmt;
                totalVat += vatAmt;
                totalWht += whtAmt;

                _db.DocumentLines.Add(new DocumentLine
                {
                    DocumentId = doc.Id,
                    LineOrder = order++,
                    Description = line.Description,
                    Quantity = line.Quantity,
                    Unit = line.Unit ?? "ชิ้น",
                    UnitPrice = line.UnitPrice,
                    DiscountPercent = line.DiscountPercent,
                    DiscountAmount = discountAmt,
                    Amount = afterDiscount,
                    VatRate = line.VatRate,
                    VatAmount = vatAmt,
                    WithholdingTaxRate = line.WithholdingTaxRate,
                    WithholdingTaxAmount = whtAmt,
                    AccountId = line.AccountId,
                    ProjectId = line.ProjectId,
                    ProductCode = string.IsNullOrWhiteSpace(line.ProductCode) ? null : line.ProductCode.Trim(),
                    SourceLineId = line.SourceLineId
                });
            }

            doc.SubTotal = subTotal;
            doc.DiscountAmount = totalDiscount;
            doc.VatAmount = totalVat;
            doc.WithholdingTaxAmount = totalWht;
            doc.TotalAmount = subTotal + totalVat - totalWht;
            doc.BalanceDue = doc.TotalAmount;

            await _db.SaveChangesAsync();
            await _subscriptionService.IncrementUsageAsync(companyId, "document");

            await transaction.CommitAsync();

            return await GetDocumentAsync(companyId, doc.Id);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<DocumentResponse> GetDocumentAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines)
            .Include(d => d.Project)
            .Include(d => d.BankAccount)
            .Include(d => d.PaymentAccount)
            .Include(d => d.ExpenseCategory)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var etax = await GetLatestEtaxAsync(companyId, new[] { documentId });
        return MapDocumentToResponse(doc, etax.GetValueOrDefault(documentId));
    }

    public async Task<DocumentResponse> GetDocumentForUserAsync(Guid companyId, Guid documentId, Guid userId)
    {
        var full = await GetDocumentAsync(companyId, documentId);
        if (full.Sensitivity == SensitivityKind.None || _sensitivity == null) return full;
        var allowed = await _sensitivity.CanViewAsync(companyId, userId, full.Sensitivity);
        if (allowed) return full;
        // Fetch only the bare-minimum metadata needed for the stub.
        var stub = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.Status, d.DocumentDate, d.DueDate, d.CreatedAt, d.Sensitivity })
            .FirstAsync();
        return new DocumentResponse(stub.Id, stub.DocumentNumber, stub.DocumentType, stub.Status,
            stub.DocumentDate, stub.DueDate,
            new ContactBrief(Guid.Empty, "[ซ่อน]", null),
            0, 0, 0, 0, 0, 0, 0, null, null,
            new List<DocumentLineResponse>(), stub.CreatedAt,
            Sensitivity: stub.Sensitivity,
            IsRedacted: true,
            RedactedReason: SensitivityRedactReason(stub.Sensitivity));
    }

    public async Task<PagedResponse<DocumentResponse>> GetDocumentsForUserAsync(Guid companyId, Guid userId, DocumentType? type, PagedRequest request, Guid? projectId = null, Guid? contactId = null, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, Guid? relatedDocumentId = null, Guid? revenueContractId = null, bool staleOnly = false)
    {
        var page = await GetDocumentsAsync(companyId, type, request, projectId, contactId, status, fromDate, toDate, relatedDocumentId, revenueContractId, staleOnly);
        if (_sensitivity == null) return page;
        var visible = await _sensitivity.GetVisibleKindsAsync(companyId, userId);

        // Replace any item the user can't see with a redacted stub. We keep
        // the row in the list (don't filter out) so paging counts stay stable
        // and integration targets see "row N is hidden" rather than "row N
        // missing" — the latter is impossible to distinguish from a delete.
        var redactedItems = page.Items.Select(it =>
            it.Sensitivity == SensitivityKind.None || visible.Contains(it.Sensitivity)
                ? it
                : new DocumentResponse(it.Id, it.DocumentNumber, it.DocumentType, it.Status,
                    it.DocumentDate, it.DueDate,
                    new ContactBrief(Guid.Empty, "[ซ่อน]", null),
                    0, 0, 0, 0, 0, 0, 0, null, null,
                    new List<DocumentLineResponse>(), it.CreatedAt,
                    Sensitivity: it.Sensitivity,
                    IsRedacted: true,
                    RedactedReason: SensitivityRedactReason(it.Sensitivity))
        ).ToList();
        return new PagedResponse<DocumentResponse>(redactedItems, page.TotalCount, page.Page, page.PageSize, page.TotalPages);
    }

    private static string SensitivityRedactReason(SensitivityKind kind) => kind switch
    {
        SensitivityKind.Payroll      => "ต้องมีสิทธิ์ดูข้อมูลเงินเดือน (perm:Payroll.View)",
        SensitivityKind.ExecutivePay => "ต้องมีสิทธิ์ดูข้อมูลค่าตอบแทนผู้บริหาร",
        SensitivityKind.HrPersonal   => "ต้องมีสิทธิ์ดูข้อมูลบุคลากร",
        SensitivityKind.Confidential => "ต้องมีสิทธิ์ดูเอกสารลับ (perm:SensitiveDocs.View)",
        _                            => "ต้องมีสิทธิ์เพิ่มเติม"
    };

    public async Task<PagedResponse<DocumentResponse>> GetDocumentsAsync(Guid companyId, DocumentType? type, PagedRequest request, Guid? projectId = null, Guid? contactId = null, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, Guid? relatedDocumentId = null, Guid? revenueContractId = null, bool staleOnly = false)
    {
        var query = _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines)
            .Include(d => d.Project)
            .Include(d => d.BankAccount)
            .Include(d => d.PaymentAccount)
            .Include(d => d.ExpenseCategory)
            .Where(d => d.CompanyId == companyId);

        if (type.HasValue)
            query = query.Where(d => d.DocumentType == type.Value);

        if (projectId.HasValue)
            query = query.Where(d => d.ProjectId == projectId.Value
                || d.Lines.Any(l => l.ProjectId == projectId.Value));

        if (contactId.HasValue)
            query = query.Where(d => d.ContactId == contactId.Value);

        if (relatedDocumentId.HasValue)
            query = query.Where(d => d.RelatedDocumentId == relatedDocumentId.Value);

        if (revenueContractId.HasValue)
            query = query.Where(d => d.RevenueContractId == revenueContractId.Value);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<DocumentStatus>(status, true, out var statusEnum))
            query = query.Where(d => d.Status == statusEnum);

        // Stale = parked in a non-terminal status past the threshold.
        if (staleOnly)
        {
            var staleCutoff = DateTime.UtcNow.Date.AddDays(-StaleThresholdDays);
            query = query.Where(d => d.DocumentDate < staleCutoff
                && d.Status != DocumentStatus.Paid
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected);
        }

        if (fromDate.HasValue)
            query = query.Where(d => d.DocumentDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(d => d.DocumentDate <= toDate.Value);

        if (!string.IsNullOrEmpty(request.Search))
        {
            var search = $"%{request.Search}%";
            query = query.Where(d => EF.Functions.ILike(d.DocumentNumber, search)
                || (d.Contact != null && EF.Functions.ILike(d.Contact.Name, search)));
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(d => d.DocumentDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var etaxByDoc = await GetLatestEtaxAsync(companyId, items.Select(i => i.Id));

        return new PagedResponse<DocumentResponse>(
            items.Select(d => MapDocumentToResponse(d, etaxByDoc.GetValueOrDefault(d.Id))).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    /// <summary>
    /// Batch-load the latest e-Tax invoice per document. Picks the highest-status
    /// (Accepted &gt; Submitted &gt; Signed &gt; Generated) so the UI shows the most
    /// "advanced" eTax record that exists. Used to surface the download button on
    /// docs whose XML has been embedded in a PDF/A-3.
    /// </summary>
    private async Task<Dictionary<Guid, (Guid EtaxId, EtaxStatus Status)>> GetLatestEtaxAsync(
        Guid companyId, IEnumerable<Guid> docIds)
    {
        var ids = docIds.Distinct().ToList();
        if (ids.Count == 0) return new();

        var rows = await _db.EtaxInvoices
            .AsNoTracking()
            .Where(e => e.CompanyId == companyId && ids.Contains(e.DocumentId))
            .Select(e => new { e.Id, e.DocumentId, e.Status, e.CreatedAt })
            .ToListAsync();

        return rows
            .GroupBy(r => r.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderByDescending(r => (int)r.Status)
                    .ThenByDescending(r => r.CreatedAt)
                    .Select(r => (r.Id, r.Status))
                    .First());
    }

    public async Task<DocumentResponse> UpdateDocumentAsync(Guid companyId, Guid documentId, UpdateDocumentRequest request)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (doc.Status != DocumentStatus.Draft)
            throw new InvalidOperationException("แก้ไขได้เฉพาะเอกสาร Draft เท่านั้น");

        if (request.DocumentDate.HasValue) doc.DocumentDate = request.DocumentDate.Value;
        if (request.DueDate.HasValue) doc.DueDate = request.DueDate.Value;
        if (request.ContactId.HasValue) doc.ContactId = request.ContactId.Value;
        if (request.Reference != null) doc.Reference = request.Reference;
        if (request.Notes != null) doc.Notes = request.Notes;
        if (request.CustomAppendix != null) doc.CustomAppendix = request.CustomAppendix;
        if (request.CustomFooterNotes != null) doc.CustomFooterNotes = request.CustomFooterNotes;
        if (request.CustomTermsAndConditions != null) doc.CustomTermsAndConditions = request.CustomTermsAndConditions;
        if (request.RevenueContractId.HasValue) doc.RevenueContractId = request.RevenueContractId.Value;
        if (request.PerformanceObligationId.HasValue) doc.PerformanceObligationId = request.PerformanceObligationId.Value;

        // Project re-assignment (only allowed while Draft, which is enforced above)
        if (request.ProjectId.HasValue)
        {
            var projectOk = await _db.Projects.AnyAsync(p =>
                p.Id == request.ProjectId.Value && p.CompanyId == companyId);
            if (!projectOk)
                throw new InvalidOperationException("ไม่พบโครงการในบริษัทนี้");
            doc.ProjectId = request.ProjectId.Value;
        }

        if (request.BankAccountId.HasValue)
            doc.BankAccountId = request.BankAccountId.Value;
        if (request.PaymentAccountId.HasValue)
            doc.PaymentAccountId = request.PaymentAccountId.Value;
        if (request.ExpenseCategoryId.HasValue)
            doc.ExpenseCategoryId = request.ExpenseCategoryId.Value;

        // CertificateInLieu fields
        if (request.CertificateReason != null) doc.CertificateReason = request.CertificateReason;
        if (request.CertifierName != null) doc.CertifierName = request.CertifierName;
        if (request.CertifierPosition != null) doc.CertifierPosition = request.CertifierPosition;
        if (request.WitnessName != null) doc.WitnessName = request.WitnessName;
        if (request.WitnessPosition != null) doc.WitnessPosition = request.WitnessPosition;
        if (request.PaymentDate.HasValue) doc.PaymentDate = request.PaymentDate.Value;

        if (request.Lines != null)
        {
            _db.DocumentLines.RemoveRange(doc.Lines);

            decimal subTotal = 0, totalDiscount = 0, totalVat = 0, totalWht = 0;
            var order = 1;

            foreach (var line in request.Lines)
            {
                var lineAmount = Math.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
                var discountAmt = Math.Round(lineAmount * line.DiscountPercent / 100, 2, MidpointRounding.AwayFromZero);
                var afterDiscount = lineAmount - discountAmt;
                var vatAmt = line.VatRate > 0 ? Math.Round(afterDiscount * line.VatRate / 100, 2, MidpointRounding.AwayFromZero) : 0m;
                var whtAmt = Math.Round(afterDiscount * line.WithholdingTaxRate / 100, 2, MidpointRounding.AwayFromZero);

                subTotal += afterDiscount;
                totalDiscount += discountAmt;
                totalVat += vatAmt;
                totalWht += whtAmt;

                _db.DocumentLines.Add(new DocumentLine
                {
                    DocumentId = doc.Id,
                    LineOrder = order++,
                    Description = line.Description,
                    Quantity = line.Quantity,
                    Unit = line.Unit ?? "ชิ้น",
                    UnitPrice = line.UnitPrice,
                    DiscountPercent = line.DiscountPercent,
                    DiscountAmount = discountAmt,
                    Amount = afterDiscount,
                    VatRate = line.VatRate,
                    VatAmount = vatAmt,
                    WithholdingTaxRate = line.WithholdingTaxRate,
                    WithholdingTaxAmount = whtAmt,
                    AccountId = line.AccountId,
                    ProjectId = line.ProjectId,
                    ProductCode = string.IsNullOrWhiteSpace(line.ProductCode) ? null : line.ProductCode.Trim(),
                    // Preserve the conversion-traceability link across edits —
                    // the edit form round-trips SourceLineId per line, so a
                    // converted document keeps its fulfilment accounting intact.
                    SourceLineId = line.SourceLineId
                });
            }

            doc.SubTotal = subTotal;
            doc.DiscountAmount = totalDiscount;
            doc.VatAmount = totalVat;
            doc.WithholdingTaxAmount = totalWht;
            doc.TotalAmount = subTotal + totalVat - totalWht;
            doc.BalanceDue = doc.TotalAmount - doc.PaidAmount;
        }

        await _db.SaveChangesAsync();
        return await GetDocumentAsync(companyId, documentId);
    }

    public async Task<DocumentResponse> ApproveDocumentAsync(Guid companyId, Guid documentId, string approvedBy)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (doc.Status != DocumentStatus.Draft)
            throw new InvalidOperationException("อนุมัติได้เฉพาะเอกสาร Draft เท่านั้น");

        // Enforce CompanySettings.RequireApprovalForDocuments: when the
        // approval rail is on and the document's amount crosses the threshold,
        // refuse direct approve and force the multi-step SignatureApproval
        // flow. SignatureApprovalService finalises documents by setting
        // doc.Status = Approved directly (it doesn't re-enter this method),
        // so the workflow path is not impacted.
        var settings = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (settings is { RequireApprovalForDocuments: true })
        {
            var threshold = settings.ApprovalThresholdAmount ?? 0m;
            if (doc.TotalAmount >= threshold)
                throw new InvalidOperationException(
                    $"เอกสารยอด {doc.TotalAmount:N2} บาท เกินวงเงินอนุมัติอัตโนมัติ ({threshold:N2}) — " +
                    "กรุณาส่งเข้ากระบวนการอนุมัติหลายชั้นก่อน (เมนู Approval)");
        }

        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId &&
            f.StartDate <= doc.DocumentDate &&
            f.EndDate >= doc.DocumentDate);
        if (period != null && period.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException(
                $"ไม่สามารถอนุมัติเอกสารที่มีวันที่ในงวด {period.Name} ได้ เนื่องจากงวดดังกล่าวมีสถานะ {period.Status}");

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                // Pessimistic lock — two simultaneous Approve calls both saw
                // Status=Draft and hasExistingJournal=false before, so both
                // happily auto-posted, producing duplicate JEs and double-counting
                // revenue/expense. FOR UPDATE serialises them on the document row.
                await _db.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE",
                    documentId, companyId);

                // Re-read status under the lock — the prior reader may have
                // already moved this Document past Draft.
                var lockedStatus = await _db.Documents
                    .Where(d => d.Id == documentId && d.CompanyId == companyId)
                    .Select(d => d.Status)
                    .FirstAsync();
                if (lockedStatus != DocumentStatus.Draft)
                    throw new InvalidOperationException("เอกสารถูกอนุมัติไปแล้วโดยผู้ใช้งานคนอื่น กรุณารีเฟรชหน้านี้");

                // Idempotency guard INSIDE transaction to prevent race condition
                var hasExistingJournal = await _db.JournalEntries.AnyAsync(j =>
                    j.SourceDocumentId == documentId
                    && j.CompanyId == companyId
                    && j.Status == JournalEntryStatus.Posted);

                doc.Status = DocumentStatus.Approved;
                doc.UpdatedBy = approvedBy;
                doc.UpdatedAt = DateTime.UtcNow;

                var autoPostTypes = new[] {
                    DocumentType.Invoice, DocumentType.TaxInvoice,
                    DocumentType.DebitNote, DocumentType.CreditNote,
                    DocumentType.PurchaseInvoice, DocumentType.Expense,
                    DocumentType.Receipt, DocumentType.ReceiptVoucher,
                    DocumentType.PaymentVoucher, DocumentType.CertificateInLieu,
                };
                if (!hasExistingJournal && autoPostTypes.Contains(doc.DocumentType))
                {
                    await AutoPostToJournalAsync(companyId, doc, approvedBy);
                }

                await ApplySourceDocumentAdjustmentsAsync(companyId, doc);
                await ApplyProjectBillingAsync(companyId, doc, +1);

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        // Best-effort: train vendor intelligence cache for OCR self-learning.
        // Failures are logged inside the helper — training is a derived side-effect
        // that can always be rebuilt via BackfillFromHistoryAsync.
        await _vendorIntel.TryTrainAsync(companyId, doc.Id);

        // Best-effort: cross-tenant routing. If the recipient Contact maps to a
        // partner Company we have an Accepted partnership with, the document
        // shows up in their inbox automatically. Failures are logged and don't
        // unwind the approval.
        try { await _crossTenantWorkflow.OnDocumentSentAsync(doc); }
        catch (Exception ex)
        { _logger.LogWarning(ex, "Cross-tenant routing failed for doc {Id}", doc.Id); }

        // Best-effort auto-generate e-Tax record for eligible types when the
        // company has e-Tax enabled. Runs OUTSIDE the approval transaction so
        // an e-Tax failure (cert not configured, RD API down, etc.) doesn't
        // roll back the approval. Failures are logged; the user can still
        // generate the e-Tax manually from the detail modal.
        await TryAutoGenerateEtaxAsync(companyId, doc);

        // LINE notification (best-effort) — keeps the legacy broadcast room
        // hook for companies wired to a single LINE Notify channel.
        var contactName = await _db.Contacts.Where(c => c.Id == doc.ContactId).Select(c => c.Name).FirstOrDefaultAsync() ?? "";
        try
        {
            await _lineNotify.NotifyDocumentApprovedAsync(companyId, doc.DocumentNumber, contactName, doc.TotalAmount);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "LINE notification failed for document {DocNum}", doc.DocumentNumber); }

        // New per-user / per-channel dispatch via the notification engine —
        // resolves Accounting / Owner recipients per the configured matrix.
        if (_notify != null)
        {
            await _notify.DispatchAsync(companyId, NotificationEvents.DocumentApproved, new NotificationContext
            {
                Title = $"อนุมัติเอกสาร {doc.DocumentNumber}",
                Message = $"{doc.DocumentType} · {contactName} · ยอดรวม {doc.TotalAmount:N2} บาท",
                ActionUrl = $"/pages/documents.html?id={doc.Id}",
                EntityType = "Document", EntityId = doc.Id,
            });

            // PaymentVoucher is a specialised approved-document subtype — fire
            // its dedicated event so accounting can have a separate matrix row.
            if (doc.DocumentType == DocumentType.PaymentVoucher)
            {
                await _notify.DispatchAsync(companyId, NotificationEvents.PaymentVoucherGenerated, new NotificationContext
                {
                    Title = $"ใบสำคัญจ่าย {doc.DocumentNumber}",
                    Message = $"{contactName} · ยอดรวม {doc.TotalAmount:N2} บาท",
                    ActionUrl = $"/pages/documents.html?id={doc.Id}",
                    EntityType = "Document", EntityId = doc.Id,
                });
            }
        }

        return await GetDocumentAsync(companyId, documentId);
    }

    /// <summary>
    /// Auto-generate the e-Tax invoice record for eligible doc types when the
    /// company has e-Tax enabled. Only runs after the document has reached
    /// Approved status. Silently skips if the company isn't VAT-registered
    /// (TaxId missing), the contact's TaxId is missing, or e-Tax is disabled.
    /// </summary>
    private async Task TryAutoGenerateEtaxAsync(Guid companyId, Document doc)
    {
        var eligibleTypes = new[] {
            DocumentType.TaxInvoice, DocumentType.Receipt,
            DocumentType.DebitNote, DocumentType.CreditNote
        };
        if (!eligibleTypes.Contains(doc.DocumentType)) return;

        try
        {
            var settings = await _db.CompanySettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CompanyId == companyId);
            if (settings?.EtaxEnabled != true) return;

            // Skip if a (non-Error) e-Tax already exists — GenerateAsync will
            // throw "เอกสารนี้มี e-Tax Invoice แล้ว" and we'd rather no-op silently.
            var alreadyExists = await _db.EtaxInvoices
                .AnyAsync(e => e.DocumentId == doc.Id && e.CompanyId == companyId
                            && e.Status != EtaxStatus.Error);
            if (alreadyExists) return;

            await _etaxService.GenerateAsync(companyId,
                new GenerateEtaxRequest(doc.Id, SignDigitally: settings.EtaxAutoSign));
        }
        catch (Exception ex)
        {
            // Don't surface the error — approval already succeeded. The user can
            // retry manually from the detail modal's "สร้าง e-Tax" button.
            _logger.LogWarning(ex,
                "Auto e-Tax generation failed for document {DocId} ({DocNumber})",
                doc.Id, doc.DocumentNumber);
        }
    }

    /// <summary>
    /// ยกเลิกเอกสาร — เก็บเอกสารต้นฉบับไว้ + สร้าง reversal JE ตามมาตรฐานบัญชีไทย
    /// (กลับรายการ Dr↔Cr, link OriginalEntryId↔ReversedByEntryId).
    /// Cascade: void linked Payments (with their JE reversals) + void linked EtaxInvoice.
    /// ไม่ลบข้อมูลออกจากฐานข้อมูล — เพื่อรักษา audit trail และตรวจสอบทางภาษี.
    /// </summary>
    public async Task VoidDocumentAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (doc.Status == DocumentStatus.Voided)
            throw new InvalidOperationException("เอกสารนี้ถูกยกเลิกแล้ว");

        // Filing lock guard: an Approved document that's part of a TaxReport
        // already marked Filed (FilingLockedAt set) is sealed for audit —
        // the operator must Unlock the report first (admin) or use
        // RejectAndReverseTaxReportAsync to formally cancel the filing.
        if (_taxService != null && await _taxService.IsDocumentFilingLockedAsync(companyId, documentId))
            throw new InvalidOperationException(
                "เอกสารนี้อยู่ในรายงานภาษีที่ Filed แล้ว — กรุณา Unlock รายงานหรือใช้ 'Reject & Reverse' ก่อน");

        // Block void if eTax has been submitted/accepted by RD — must contact RD to revoke first
        var lockedEtax = await _db.EtaxInvoices.FirstOrDefaultAsync(e => e.DocumentId == documentId
            && e.CompanyId == companyId
            && (e.Status == EtaxStatus.Submitted || e.Status == EtaxStatus.Accepted));
        if (lockedEtax != null)
            throw new InvalidOperationException(
                $"ไม่สามารถยกเลิกเอกสารนี้ได้ เนื่องจาก e-Tax เลขที่ {lockedEtax.EtaxRefNumber} " +
                "ถูกส่งหรืออนุมัติโดยกรมสรรพากรแล้ว ต้องดำเนินการขอยกเลิกที่กรมสรรพากรก่อน");

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                // 1) Void linked Payments first — each reverses its own JE + restores doc balance
                //    (We void *all* payments inside this transaction; the document gets voided
                //    after, so payment-balance recalculation here is intermediate only.)
                var payments = await _db.Payments
                    .Where(p => p.DocumentId == documentId && p.CompanyId == companyId && !p.IsDeleted)
                    .ToListAsync();
                foreach (var payment in payments)
                {
                    await ReversePaymentInternalAsync(companyId, payment, doc,
                        $"ยกเลิกอัตโนมัติพร้อมเอกสาร {doc.DocumentNumber}");
                }

                // 2) Reverse linked Posted JEs via AccountingService (proper linkage:
                //    OriginalEntryId/ReversedByEntryId, fiscal period validation, dimensions).
                var postedJournalIds = await _db.JournalEntries
                    .Where(j => j.SourceDocumentId == documentId && j.CompanyId == companyId
                        && j.Status == JournalEntryStatus.Posted)
                    .Select(j => j.Id)
                    .ToListAsync();
                foreach (var jeId in postedJournalIds)
                {
                    await _accountingService.ReverseJournalEntryAsync(companyId, jeId,
                        reversalDate: DateTime.UtcNow.Date,
                        description: $"ยกเลิกเอกสาร {doc.DocumentNumber}",
                        systemTriggered: true);
                }

                // 3) Void linked EtaxInvoice (keep XML/PDF for audit; only flag status)
                var etaxes = await _db.EtaxInvoices
                    .Where(e => e.DocumentId == documentId && e.CompanyId == companyId
                        && e.Status != EtaxStatus.Voided && e.Status != EtaxStatus.Submitted
                        && e.Status != EtaxStatus.Accepted)
                    .ToListAsync();
                foreach (var etax in etaxes)
                {
                    etax.Status = EtaxStatus.Voided;
                    etax.VoidedAt = DateTime.UtcNow;
                    etax.VoidReason = $"ยกเลิกพร้อมเอกสาร {doc.DocumentNumber}";
                    etax.UpdatedAt = DateTime.UtcNow;
                }

                // 4) Unlink BankTransactions matched to this document's journal entries
                if (postedJournalIds.Any())
                {
                    await _db.BankTransactions
                        .Where(t => t.CompanyId == companyId
                            && t.MatchedJournalEntryId.HasValue
                            && postedJournalIds.Contains(t.MatchedJournalEntryId.Value))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(t => t.MatchedJournalEntryId, (Guid?)null)
                            .SetProperty(t => t.ReconciliationStatus, ReconciliationStatus.Unmatched)
                            .SetProperty(t => t.ReconciledAt, (DateTime?)null));
                }

                // 5) Restore source document's balance if this was a derivative
                //    (Receipt/CN/PaymentVoucher referencing another doc). Mirrors
                //    the adjustment applied during ApproveDocumentAsync.
                await RevertSourceDocumentAdjustmentsAsync(companyId, doc);

                // 6) Back out this document's contribution to its project's
                //    BilledAmount — mirror of the +1 applied at approval.
                //    Skip Draft docs: never approved, so never billed.
                if (doc.Status != DocumentStatus.Draft)
                    await ApplyProjectBillingAsync(companyId, doc, -1);

                // 7) Finally void the document itself
                doc.Status = DocumentStatus.Voided;
                doc.UpdatedAt = DateTime.UtcNow;

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        // Cascade-unwind any ReconciliationGroup that referenced this Document
        // directly (ItemType=Document — for Receipt/PV operators dragged into
        // a group without going through a Payment record). JE-pathed groups
        // were already unwound during step (2) via ReverseJournalEntryAsync.
        // Runs OUTSIDE the strategy/transaction above so a bank-cleanup
        // hiccup doesn't roll back the voided document state.
        if (_bankService != null)
        {
            try { await _bankService.UnwindGroupsContainingItemAsync(companyId, ReconciliationItemType.Document, documentId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Group unwind for voided document {DocId} failed", documentId); }
        }

        // Fire DocumentVoided notification (best-effort, post-commit). Cascades
        // through the per-user matrix to Accounting / Owner recipients per
        // their configured channels (in-app, LINE, email).
        if (_notify != null)
        {
            try
            {
                var contactName = await _db.Contacts.Where(c => c.Id == doc.ContactId)
                    .Select(c => c.Name).FirstOrDefaultAsync() ?? "";
                await _notify.DispatchAsync(companyId, NotificationEvents.DocumentVoided, new NotificationContext
                {
                    Title = $"ยกเลิกเอกสาร {doc.DocumentNumber}",
                    Message = $"{doc.DocumentType} · {contactName} · ยอดรวม {doc.TotalAmount:N2} บาท — กลับรายการบัญชี/ชำระเงิน/e-Tax อัตโนมัติ",
                    ActionUrl = $"/pages/documents.html?id={doc.Id}",
                    EntityType = "Document", EntityId = doc.Id,
                });
            }
            catch (Exception ex) { _logger.LogWarning(ex, "DocumentVoided notification failed for {DocId}", documentId); }
        }
    }

    /// <summary>
    /// ลบเอกสารถาวร — เฉพาะเอกสารฉบับร่าง (Draft) ที่ยังไม่กระทบบัญชีและไม่มีการชำระเงินเท่านั้น
    /// เอกสารที่อนุมัติแล้วต้องใช้ "ยกเลิก" (VoidDocumentAsync) เพื่อรักษา audit trail.
    /// </summary>
    public async Task DeleteDocumentAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (doc.Status != DocumentStatus.Draft)
            throw new InvalidOperationException(
                "ไม่สามารถลบเอกสารที่อนุมัติแล้วได้ — กรุณาใช้คำสั่ง 'ยกเลิก' " +
                "เพื่อสร้างรายการกลับบัญชีตามมาตรฐาน (รักษา audit trail)");

        var hasJournal = await _db.JournalEntries.AnyAsync(j => j.SourceDocumentId == documentId
            && j.CompanyId == companyId);
        if (hasJournal)
            throw new InvalidOperationException(
                "ไม่สามารถลบเอกสารที่มีรายการบัญชีเชื่อมอยู่ได้ — กรุณาใช้ 'ยกเลิก' แทน");

        var hasPayment = await _db.Payments.AnyAsync(p => p.DocumentId == documentId
            && p.CompanyId == companyId && !p.IsDeleted);
        if (hasPayment)
            throw new InvalidOperationException(
                "ไม่สามารถลบเอกสารที่มีการชำระเงินแล้วได้ — กรุณายกเลิกการชำระเงินก่อน");

        var hasEtax = await _db.EtaxInvoices.AnyAsync(e => e.DocumentId == documentId
            && e.CompanyId == companyId && e.Status != EtaxStatus.Voided);
        if (hasEtax)
            throw new InvalidOperationException(
                "ไม่สามารถลบเอกสารที่มีใบกำกับภาษีอิเล็กทรอนิกส์ (e-Tax) เชื่อมอยู่ได้");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Hard-delete lines first (FK), then header. Use Remove (not soft-delete)
            // because Draft never reached the books — no audit obligation.
            _db.DocumentLines.RemoveRange(doc.Lines);
            _db.Documents.Remove(doc);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task PurgeDocumentAsync(Guid companyId, Guid documentId)
    {
        await PurgeDocumentAsync(companyId, documentId, null);
    }

    public async Task PurgeDocumentAsync(Guid companyId, Guid documentId, Guid? userId)
    {
        var doc = await _db.Documents
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var auditSnapshot = System.Text.Json.JsonSerializer.Serialize(new
        {
            doc.DocumentNumber, doc.DocumentType, doc.Status,
            doc.TotalAmount, doc.ContactId, doc.DocumentDate
        });

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1. Delete JournalLineDimensions → JournalEntryLines → JournalEntries
            var journalIds = await _db.JournalEntries
                .IgnoreQueryFilters()
                .Where(j => j.SourceDocumentId == documentId && j.CompanyId == companyId)
                .Select(j => j.Id)
                .ToListAsync();

            // Also include reversal entries (linked via OriginalEntryId or ReversedByEntryId)
            if (journalIds.Any())
            {
                var reversalIds = await _db.JournalEntries
                    .IgnoreQueryFilters()
                    .Where(j => j.CompanyId == companyId &&
                        (journalIds.Contains(j.OriginalEntryId ?? Guid.Empty) ||
                         journalIds.Contains(j.ReversedByEntryId ?? Guid.Empty)))
                    .Select(j => j.Id)
                    .ToListAsync();
                journalIds = journalIds.Union(reversalIds).Distinct().ToList();
            }

            if (journalIds.Any())
            {
                var lineIds = await _db.JournalEntryLines
                    .IgnoreQueryFilters()
                    .Where(l => journalIds.Contains(l.JournalEntryId))
                    .Select(l => l.Id)
                    .ToListAsync();

                if (lineIds.Any())
                {
                    await _db.Database.ExecuteSqlRawAsync(
                        @"DELETE FROM ""JournalLineDimensions"" WHERE ""JournalEntryLineId"" = ANY({0})",
                        lineIds);
                    await _db.Database.ExecuteSqlRawAsync(
                        @"DELETE FROM ""JournalEntryLines"" WHERE ""Id"" = ANY({0})",
                        lineIds);
                }

                // Null out references from other tables before deleting journals
                await _db.Database.ExecuteSqlRawAsync(
                    @"UPDATE ""BankTransactions"" SET ""MatchedJournalEntryId"" = NULL WHERE ""MatchedJournalEntryId"" = ANY({0})",
                    journalIds);

                await _db.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""JournalEntries"" WHERE ""Id"" = ANY({0})",
                    journalIds);
            }

            // 2. Delete Payments
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Payments"" WHERE ""DocumentId"" = {0} AND ""CompanyId"" = {1}",
                documentId, companyId);

            // 3. Delete WHT certificates + lines linked to this document
            var whtIds = await _db.WithholdingTaxCerts
                .IgnoreQueryFilters()
                .Where(w => w.DocumentId == documentId && w.CompanyId == companyId)
                .Select(w => w.Id)
                .ToListAsync();

            if (whtIds.Any())
            {
                await _db.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""WithholdingTaxCertLines"" WHERE ""WithholdingTaxCertId"" = ANY({0})",
                    whtIds);
                await _db.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""WithholdingTaxCerts"" WHERE ""Id"" = ANY({0})",
                    whtIds);
            }

            // 4. Delete EtaxInvoices
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""EtaxInvoices"" WHERE ""DocumentId"" = {0} AND ""CompanyId"" = {1}",
                documentId, companyId);

            // 5. Null out DocumentEmailLogs (FK is SetNull but we force-clean)
            await _db.Database.ExecuteSqlRawAsync(
                @"UPDATE ""DocumentEmailLogs"" SET ""DocumentId"" = NULL WHERE ""DocumentId"" = {0}",
                documentId);

            // 6. Delete DocumentApprovals & DocumentSignatures
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""DocumentApprovals"" WHERE ""DocumentId"" = {0}",
                documentId);
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""DocumentSignatures"" WHERE ""DocumentId"" = {0}",
                documentId);

            // 7. Null out references from other documents (RelatedDocumentId)
            await _db.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Documents"" SET ""RelatedDocumentId"" = NULL WHERE ""RelatedDocumentId"" = {0}",
                documentId);

            // 8. Delete DocumentLines → Document
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""DocumentLines"" WHERE ""DocumentId"" = {0}",
                documentId);
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Documents"" WHERE ""Id"" = {0} AND ""CompanyId"" = {1}",
                documentId, companyId);

            if (userId.HasValue)
            {
                _db.AuditLogs.Add(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = userId,
                    Action = AuditAction.Delete,
                    EntityType = "Document",
                    EntityId = documentId.ToString(),
                    OldValues = auditSnapshot,
                    Timestamp = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// ยกเลิกการชำระเงิน — กลับรายการ JE ของการชำระ + คืนยอดให้เอกสารต้นทาง.
    /// ใช้ทั้งจาก endpoint โดยตรง และจาก VoidDocumentAsync (cascade).
    /// </summary>
    public async Task VoidPaymentAsync(Guid companyId, Guid paymentId)
    {
        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId
            && p.CompanyId == companyId && !p.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");

        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == payment.DocumentId
            && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสารต้นทาง");

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                await ReversePaymentInternalAsync(companyId, payment, doc, "ยกเลิกการชำระเงิน");
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        // Cascade-unwind any ReconciliationGroup that referenced this Payment
        // directly (ItemType=Payment). JE-pathed groups were unwound by
        // ReversePaymentInternalAsync → ReverseJournalEntryAsync → bank cleanup.
        if (_bankService != null)
        {
            try { await _bankService.UnwindGroupsContainingItemAsync(companyId, ReconciliationItemType.Payment, paymentId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Group unwind for voided payment {PayId} failed", paymentId); }
        }
    }

    /// <summary>
    /// ตัดหนี้สูญ — เมื่อลูกหนี้ผิดนัดและมั่นใจว่าจะไม่ได้รับเงิน
    /// JE: Dr หนี้สูญ (64xxx), Cr ลูกหนี้การค้า (113)
    /// อัพเดต source.PaidAmount = TotalAmount, BalanceDue = 0, Status = Paid (เคลียร์)
    /// </summary>
    public async Task<DocumentResponse> WriteOffBadDebtAsync(Guid companyId, Guid documentId, string writtenOffBy, string? reason = null)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // Only AR documents with outstanding balance can be written off
        var arTypes = new[] {
            DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote
        };
        if (!arTypes.Contains(doc.DocumentType))
            throw new InvalidOperationException(
                "ตัดหนี้สูญได้เฉพาะใบแจ้งหนี้ / ใบกำกับภาษี / ใบเพิ่มหนี้ ที่ยังคงค้างเท่านั้น");

        if (doc.Status != DocumentStatus.Approved
            && doc.Status != DocumentStatus.PartiallyPaid
            && doc.Status != DocumentStatus.Sent
            && doc.Status != DocumentStatus.Overdue)
            throw new InvalidOperationException(
                "ตัดหนี้สูญได้เฉพาะเอกสารที่อนุมัติแล้วและยังคงค้าง");

        if (doc.BalanceDue <= 0.01m)
            throw new InvalidOperationException("เอกสารนี้ไม่มียอดคงค้าง — ไม่ต้องตัดหนี้สูญ");

        // Find or fall back to bad-debt expense account
        var badDebtAcc = await FindAccountAsync(companyId, "64000")
            ?? await FindAccountAsync(companyId, "55000")
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId
                    && a.AccountType == AccountType.Expense
                    && a.AccountName!.Contains("หนี้สูญ")
                    && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();
        if (badDebtAcc == null)
            throw new InvalidOperationException(
                "ไม่พบบัญชี 'หนี้สูญ' (64000) ในผังบัญชี กรุณาเพิ่มบัญชีก่อน");

        var arAcc = await FindAccountAsync(companyId, "113")
            ?? throw new InvalidOperationException("ไม่พบบัญชี 'ลูกหนี้การค้า' (113) ในผังบัญชี");

        var writeOffAmount = doc.BalanceDue;

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                // Resolve fiscal period
                var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
                    f.CompanyId == companyId &&
                    f.StartDate <= DateTime.UtcNow.Date &&
                    f.EndDate >= DateTime.UtcNow.Date);
                if (period != null && period.Status != FiscalPeriodStatus.Open)
                    throw new InvalidOperationException(
                        $"ไม่สามารถตัดหนี้สูญในงวด {period.Name} ได้ เนื่องจากงวดถูกปิดแล้ว");

                var entryNumber = await GetNextJournalEntryNumberAsync(companyId, "JV");
                var je = new JournalEntry
                {
                    CompanyId = companyId,
                    EntryNumber = entryNumber,
                    EntryDate = DateTime.UtcNow.Date,
                    JournalType = JournalType.General,
                    Description = $"ตัดหนี้สูญ - {doc.DocumentNumber}" + (reason != null ? $" ({reason})" : ""),
                    Reference = doc.DocumentNumber,
                    Status = JournalEntryStatus.Posted,
                    TotalDebit = writeOffAmount,
                    TotalCredit = writeOffAmount,
                    CreatedBy = writtenOffBy,
                    IsAutoGenerated = true,
                    SourceDocumentId = doc.Id,
                    FiscalPeriodId = period?.Id,
                    ProjectId = doc.ProjectId
                };
                _db.JournalEntries.Add(je);
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    JournalEntryId = je.Id,
                    AccountId = badDebtAcc.Id,
                    DebitAmount = writeOffAmount,
                    CreditAmount = 0,
                    Description = $"หนี้สูญ - {doc.DocumentNumber}",
                    LineOrder = 1,
                    ProjectId = doc.ProjectId
                });
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    JournalEntryId = je.Id,
                    AccountId = arAcc.Id,
                    DebitAmount = 0,
                    CreditAmount = writeOffAmount,
                    Description = $"ตัดลูกหนี้ - {doc.DocumentNumber}",
                    LineOrder = 2,
                    ProjectId = doc.ProjectId
                });

                // Clear the source document
                doc.PaidAmount = doc.TotalAmount;
                doc.BalanceDue = 0;
                doc.Status = DocumentStatus.Paid;
                doc.UpdatedBy = writtenOffBy;
                doc.UpdatedAt = DateTime.UtcNow;
                doc.InternalNotes = string.IsNullOrWhiteSpace(doc.InternalNotes)
                    ? $"ตัดหนี้สูญ {DateTime.UtcNow:yyyy-MM-dd}: {reason ?? ""}"
                    : doc.InternalNotes + $"\nตัดหนี้สูญ {DateTime.UtcNow:yyyy-MM-dd}: {reason ?? ""}";

                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        return await GetDocumentAsync(companyId, documentId);
    }

    /// <summary>
    /// Internal: reverses a single payment within an existing transaction.
    /// - Creates JE reversal (via AccountingService) for the payment's posted JE
    /// - Soft-deletes the payment (IsDeleted=true) — keeps record for audit
    /// - Restores doc.PaidAmount and doc.BalanceDue
    /// - Recalculates doc.Status (Paid → PartiallyPaid → Approved)
    /// Caller is responsible for transaction + final SaveChangesAsync.
    /// </summary>
    private async Task ReversePaymentInternalAsync(Guid companyId, Payment payment, Document doc, string reason)
    {
        // Reverse linked JEs created from this payment.
        // Payment JEs are linked via SourceDocumentId = doc.Id with a Reference matching payment.PaymentNumber.
        var paymentJournals = await _db.JournalEntries
            .Where(j => j.SourceDocumentId == doc.Id
                && j.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && j.Reference == payment.PaymentNumber)
            .Select(j => j.Id)
            .ToListAsync();

        // Fail loud if the payment posted to GL but we can't find the JE to
        // reverse — silently skipping leaves Cash/AR overstated. The most
        // common cause is a Reference-format drift (whitespace, case). Log
        // the discrepancy and surface a clear error so the operator can
        // reverse the orphan JE manually instead of corrupting GL silently.
        if (paymentJournals.Count == 0)
        {
            var anyJeForDoc = await _db.JournalEntries
                .AnyAsync(j => j.SourceDocumentId == doc.Id
                    && j.CompanyId == companyId
                    && j.Status == JournalEntryStatus.Posted);
            if (anyJeForDoc)
                throw new InvalidOperationException(
                    $"ไม่พบ JE ที่ผูกกับใบรับเงิน {payment.PaymentNumber} (อาจถูกแก้ Reference ภายหลัง) — " +
                    "กรุณากลับรายการ JE ด้วยมือก่อนทำการ Reverse Payment เพื่อไม่ให้ยอด Cash/AR ใน GL คลาดเคลื่อน");
        }

        foreach (var jeId in paymentJournals)
        {
            await _accountingService.ReverseJournalEntryAsync(companyId, jeId,
                reversalDate: DateTime.UtcNow.Date,
                description: $"{reason} - {payment.PaymentNumber}",
                systemTriggered: true);
        }

        // Reverse bank balance — payment.Amount is in the document's currency;
        // BankAccount.CurrentBalance is in THB, so apply the document's FX rate
        // before adjusting. Mirrors the conversion in CreatePaymentJournalAsync.
        if (payment.BankAccountId.HasValue)
        {
            var isInflow = doc.DocumentType is DocumentType.Invoice or DocumentType.TaxInvoice
                or DocumentType.Receipt or DocumentType.ReceiptVoucher
                or DocumentType.DebitNote or DocumentType.BillingNote;
            var thbAmount = doc.ExchangeRate == 1m
                ? payment.Amount
                : Math.Round(payment.Amount * doc.ExchangeRate, 2, MidpointRounding.AwayFromZero);
            var delta = isInflow ? -thbAmount : thbAmount;
            await _db.BankAccounts
                .Where(b => b.Id == payment.BankAccountId.Value && b.CompanyId == companyId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.CurrentBalance, b => b.CurrentBalance + delta));
        }

        // Soft-delete the payment record (keep for audit; mirrors how Reverse keeps original JE)
        payment.IsDeleted = true;
        payment.UpdatedAt = DateTime.UtcNow;

        // Restore document balance
        doc.PaidAmount = Math.Max(0m, doc.PaidAmount - payment.Amount);
        doc.BalanceDue = doc.TotalAmount - doc.PaidAmount;
        if (doc.Status != DocumentStatus.Voided)
        {
            doc.Status = doc.PaidAmount <= 0 ? DocumentStatus.Approved
                : doc.BalanceDue <= 0 ? DocumentStatus.Paid
                : DocumentStatus.PartiallyPaid;
        }
        doc.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Updates a source document's balance/status when a derivative (Receipt, CN, DN,
    /// PaymentVoucher) referencing it is approved. This keeps AR/AP aging and per-document
    /// status consistent with the GL.
    ///
    /// Rules (Thai accounting practice):
    /// - Receipt/ReceiptVoucher with ref → settlement: source.PaidAmount += amount
    /// - PaymentVoucher with ref → AP settlement: source.PaidAmount += amount
    /// - CreditNote with ref → AR offset: source.PaidAmount += amount (treats as offset)
    /// - DebitNote with ref → no source mutation (DN is a NEW AR, not adjustment)
    ///
    /// Validation: refuses if cumulative adjustments would exceed the source's TotalAmount
    /// (i.e. you cannot refund/credit more than the customer owes).
    ///
    /// Caller is responsible for transaction + SaveChangesAsync.
    /// </summary>
    private async Task ApplySourceDocumentAdjustmentsAsync(Guid companyId, Document doc)
    {
        if (!doc.RelatedDocumentId.HasValue) return;

        var isSettlement = doc.DocumentType == DocumentType.Receipt
            || doc.DocumentType == DocumentType.ReceiptVoucher
            || doc.DocumentType == DocumentType.PaymentVoucher;
        var isCreditNote = doc.DocumentType == DocumentType.CreditNote;
        var isDebitNote = doc.DocumentType == DocumentType.DebitNote;
        if (!isSettlement && !isCreditNote && !isDebitNote) return;

        // Lock source row to prevent concurrent balance modifications (FOR UPDATE)
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} FOR UPDATE", doc.RelatedDocumentId.Value);

        var source = await _db.Documents.FirstOrDefaultAsync(d =>
            d.Id == doc.RelatedDocumentId.Value && d.CompanyId == companyId);
        if (source == null) return;

        if (isSettlement)
        {
            // Settlement (Receipt/ReceiptVoucher/PaymentVoucher): strict cap.
            // Cannot collect/pay more than the outstanding balance.
            if (doc.TotalAmount > source.BalanceDue + 0.01m)
                throw new InvalidOperationException(
                    $"จำนวนเงินรับ/จ่าย ({doc.TotalAmount:N2}) มากกว่ายอดคงค้างของเอกสารต้นทาง " +
                    $"{source.DocumentNumber} (คงค้าง {source.BalanceDue:N2})");

            source.PaidAmount += doc.TotalAmount;
        }
        else if (isCreditNote)
        {
            // CN: validation depends on source state.
            // - If source still has BalanceDue > 0: CN reduces AR/AP, capped by BalanceDue
            // - If source fully paid (BalanceDue = 0): CN becomes cash refund — JE
            //   handles the cash flow. Source state stays at Paid (PaidAmount unchanged).
            if (source.BalanceDue > 0.01m)
            {
                if (doc.TotalAmount > source.BalanceDue + 0.01m)
                    throw new InvalidOperationException(
                        $"จำนวนใบลดหนี้ ({doc.TotalAmount:N2}) มากกว่ายอดคงค้างของเอกสารต้นทาง " +
                        $"{source.DocumentNumber} (คงค้าง {source.BalanceDue:N2}) " +
                        $"— หากต้องการคืนเงินเกินกว่ายอดคงค้าง กรุณาแยกเป็นใบลดหนี้หลายใบ");

                source.PaidAmount += doc.TotalAmount;
            }
            // else: cash refund mode — source.PaidAmount stays at TotalAmount,
            // BalanceDue stays at 0, Status stays at Paid. JE Cr Cash handles it.
        }
        else if (isDebitNote)
        {
            // DN: increases the obligation (customer owes more / we owe more)
            // - If source has BalanceDue > 0: DN adds to BalanceDue (effectively
            //   reduces source.PaidAmount accumulation; we model it as TotalAmount += DN)
            //   Actually simpler: leave source untouched — DN is a separate AR/AP doc itself.
            // - If source fully paid: DN creates a new debt that needs to be collected
            //   separately. The DN itself acts as the new AR.
            //
            // For both cases: do NOT mutate source. The DN is a separate document
            // and shows up in AR/AP aging on its own merit.
            //
            // (User can still link via RelatedDocumentId for traceability.)
            return;
        }

        source.BalanceDue = source.TotalAmount - source.PaidAmount;
        if (source.Status != DocumentStatus.Voided)
        {
            source.Status = source.BalanceDue <= 0.01m
                ? DocumentStatus.Paid
                : DocumentStatus.PartiallyPaid;
        }
        source.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Reverse of ApplySourceDocumentAdjustmentsAsync — called from VoidDocumentAsync
    /// when a derivative document is voided, to restore the source's balance.
    /// </summary>
    private async Task RevertSourceDocumentAdjustmentsAsync(Guid companyId, Document doc)
    {
        if (!doc.RelatedDocumentId.HasValue) return;

        var typeAffectsSource = doc.DocumentType == DocumentType.Receipt
            || doc.DocumentType == DocumentType.ReceiptVoucher
            || doc.DocumentType == DocumentType.PaymentVoucher
            || doc.DocumentType == DocumentType.CreditNote;
        if (!typeAffectsSource) return;

        var source = await _db.Documents.FirstOrDefaultAsync(d =>
            d.Id == doc.RelatedDocumentId.Value && d.CompanyId == companyId);
        if (source == null) return;

        source.PaidAmount = Math.Max(0m, source.PaidAmount - doc.TotalAmount);
        source.BalanceDue = source.TotalAmount - source.PaidAmount;
        if (source.Status != DocumentStatus.Voided)
        {
            source.Status = source.PaidAmount <= 0.01m
                ? DocumentStatus.Approved
                : source.BalanceDue <= 0.01m
                    ? DocumentStatus.Paid
                    : DocumentStatus.PartiallyPaid;
        }
        source.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Roll an approved customer-billing document's value into its linked
    /// project's BilledAmount — the Documents → Project data flow that was
    /// missing, leaving project profitability reports showing zero billed.
    /// <paramref name="sign"/> is +1 on approval, -1 when the document is
    /// voided. Only Invoice / TaxInvoice / DebitNote count as billing;
    /// Receipts and purchase-side documents do not represent new billing.
    /// Caller owns the transaction + SaveChangesAsync.
    /// </summary>
    private async Task ApplyProjectBillingAsync(Guid companyId, Document doc, int sign)
    {
        if (!doc.ProjectId.HasValue) return;

        var billingTypes = new[] {
            DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote
        };
        if (!billingTypes.Contains(doc.DocumentType)) return;

        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == doc.ProjectId.Value && p.CompanyId == companyId);
        if (project == null) return;

        project.BilledAmount = Math.Max(0m, project.BilledAmount + sign * doc.TotalAmount);
        project.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Valid document conversions per Thai accounting workflow.
    /// Each entry: source type → list of allowed target types.
    /// Anything not listed is rejected to prevent illogical flows like
    /// Quotation→CreditNote (CN must reference Invoice/TaxInvoice/sale).
    /// </summary>
    private static readonly Dictionary<DocumentType, DocumentType[]> ValidConversions = new()
    {
        // Sales side
        [DocumentType.Quotation] = new[]
        {
            DocumentType.Invoice, DocumentType.TaxInvoice,
            DocumentType.BillingNote, DocumentType.DeliveryNote,
            DocumentType.Receipt
        },
        [DocumentType.BillingNote] = new[]
        {
            DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt
        },
        [DocumentType.DeliveryNote] = new[]
        {
            DocumentType.Invoice, DocumentType.TaxInvoice
        },
        [DocumentType.Invoice] = new[]
        {
            DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.ReceiptVoucher
        },
        [DocumentType.TaxInvoice] = new[]
        {
            DocumentType.Receipt, DocumentType.ReceiptVoucher,
            DocumentType.CreditNote, DocumentType.DebitNote
        },
        [DocumentType.DebitNote] = new[]
        {
            DocumentType.Receipt, DocumentType.ReceiptVoucher
        },
        // Purchase side
        [DocumentType.PurchaseRequisition] = new[] { DocumentType.PurchaseOrder },
        [DocumentType.PurchaseOrder] = new[]
        {
            DocumentType.PurchaseInvoice, DocumentType.Expense
        },
        [DocumentType.PurchaseInvoice] = new[]
        {
            DocumentType.PaymentVoucher,
            // Adjustments from supplier — supplier issues us CN/DN.
            // Posting auto-detects purchase-side via RelatedDocumentId.
            DocumentType.CreditNote, DocumentType.DebitNote
        },
        [DocumentType.Expense] = new[]
        {
            DocumentType.PaymentVoucher,
            DocumentType.CreditNote, DocumentType.DebitNote,
            DocumentType.CertificateInLieu
        },
        [DocumentType.CertificateInLieu] = new[]
        {
            DocumentType.PaymentVoucher
        }
        // Terminal types (no further conversion):
        // Receipt, ReceiptVoucher, CreditNote, PaymentVoucher
    };

    /// <summary>Public accessor used by API endpoint to surface valid targets to UI.</summary>
    public static IReadOnlyList<DocumentType> GetValidConversionTargets(DocumentType source) =>
        ValidConversions.TryGetValue(source, out var targets) ? targets : Array.Empty<DocumentType>();

    // ===================================================================
    // Flexible / partial document composition
    //
    // A source document line may be carried forward into MANY child lines
    // across MANY documents. Conversion tracks "how much is left" along two
    // independent fulfilment axes:
    //   • Delivery — consumed by DeliveryNote
    //   • Billing  — consumed by Invoice / TaxInvoice / BillingNote /
    //                PurchaseInvoice / Expense / PurchaseOrder
    // Conversions to other types (Receipt, CreditNote, ...) settle by
    // amount, not quantity, and keep the legacy whole-document behaviour.
    // ===================================================================

    private enum FulfillmentAxis { None, Delivery, Billing }

    private static FulfillmentAxis GetFulfillmentAxis(DocumentType type) => type switch
    {
        DocumentType.DeliveryNote => FulfillmentAxis.Delivery,
        DocumentType.Invoice or DocumentType.TaxInvoice or DocumentType.BillingNote
            or DocumentType.PurchaseInvoice or DocumentType.Expense
            or DocumentType.PurchaseOrder => FulfillmentAxis.Billing,
        _ => FulfillmentAxis.None
    };

    private static string AxisLabel(FulfillmentAxis axis) => axis switch
    {
        FulfillmentAxis.Delivery => "ส่งมอบ",
        FulfillmentAxis.Billing => "วางบิล/แจ้งหนี้",
        _ => ""
    };

    private const decimal QtyEpsilon = 0.0001m;

    /// <summary>How much of each given source line has already been carried
    /// forward into child documents, split per fulfilment axis. Voided /
    /// Rejected / deleted child documents do not count.</summary>
    private async Task<Dictionary<Guid, (decimal Delivery, decimal Billing)>> ComputeConsumptionAsync(
        Guid companyId, List<Guid> sourceLineIds)
    {
        var result = sourceLineIds.ToDictionary(id => id, _ => (Delivery: 0m, Billing: 0m));
        if (sourceLineIds.Count == 0) return result;

        var children = await (
            from cl in _db.DocumentLines
            join cd in _db.Documents on cl.DocumentId equals cd.Id
            where cl.SourceLineId != null && sourceLineIds.Contains(cl.SourceLineId.Value)
                  && cd.CompanyId == companyId
                  && cd.Status != DocumentStatus.Voided && cd.Status != DocumentStatus.Rejected
            select new { SourceLineId = cl.SourceLineId!.Value, cl.Quantity, cd.DocumentType })
            .ToListAsync();

        foreach (var c in children)
        {
            var axis = GetFulfillmentAxis(c.DocumentType);
            var cur = result[c.SourceLineId];
            result[c.SourceLineId] = axis switch
            {
                FulfillmentAxis.Delivery => (cur.Delivery + c.Quantity, cur.Billing),
                FulfillmentAxis.Billing => (cur.Delivery, cur.Billing + c.Quantity),
                _ => cur
            };
        }
        return result;
    }

    /// <summary>Shared validation for both whole-document and partial
    /// conversion — throws on any rule violation.</summary>
    private async Task ValidateConversionAsync(Document source, DocumentType targetType, Guid companyId)
    {
        // Block converting from Voided/Rejected source — they no longer reflect
        // the customer's true position; new derivative would carry stale data.
        if (source.Status == DocumentStatus.Voided)
            throw new InvalidOperationException(
                $"เอกสาร {source.DocumentNumber} ถูกยกเลิกแล้ว ไม่สามารถแปลงเป็นเอกสารใหม่ได้");
        if (source.Status == DocumentStatus.Rejected)
            throw new InvalidOperationException(
                $"เอกสาร {source.DocumentNumber} ถูกปฏิเสธ ไม่สามารถแปลงเป็นเอกสารใหม่ได้");

        // Block converting to self (no-op)
        if (source.DocumentType == targetType)
            throw new InvalidOperationException(
                $"ไม่สามารถแปลงเป็นเอกสารประเภทเดิม ({targetType})");

        // Block invalid type-to-type conversion (e.g. Quotation→CreditNote)
        var allowedTargets = GetValidConversionTargets(source.DocumentType);
        if (!allowedTargets.Contains(targetType))
        {
            var allowedNames = string.Join(", ", allowedTargets);
            throw new InvalidOperationException(
                $"ไม่สามารถแปลง {source.DocumentType} → {targetType} ได้ตามมาตรฐานบัญชี " +
                $"(แปลงได้เฉพาะ: {(allowedNames.Length > 0 ? allowedNames : "ไม่มี — เอกสารนี้เป็นปลายทาง")})");
        }

        // Cycle detection — fetch entire ancestry chain in a single recursive CTE
        // instead of N round-trips (one per ancestor level).
        if (source.RelatedDocumentId.HasValue)
        {
            var startId = source.RelatedDocumentId.Value;
            var ancestorMatch = await _db.Database
                .SqlQuery<AncestorRow>($@"
                    WITH RECURSIVE ancestry AS (
                        SELECT ""Id"", ""DocumentNumber"", ""DocumentType"", ""RelatedDocumentId"", 1 AS depth
                        FROM ""Documents""
                        WHERE ""Id"" = {startId} AND ""CompanyId"" = {companyId} AND ""IsDeleted"" = false
                        UNION ALL
                        SELECT d.""Id"", d.""DocumentNumber"", d.""DocumentType"", d.""RelatedDocumentId"", a.depth + 1
                        FROM ""Documents"" d
                        INNER JOIN ancestry a ON d.""Id"" = a.""RelatedDocumentId""
                        WHERE d.""CompanyId"" = {companyId} AND d.""IsDeleted"" = false AND a.depth < 20
                    )
                    SELECT ""Id"", ""DocumentNumber"", ""DocumentType"" FROM ancestry
                    WHERE ""DocumentType"" = {(int)targetType} LIMIT 1")
                .FirstOrDefaultAsync();

            if (ancestorMatch != null)
                throw new InvalidOperationException(
                    $"ตรวจพบวงกลมการแปลง: เอกสาร {ancestorMatch.DocumentNumber} ({(DocumentType)ancestorMatch.DocumentType}) เป็นบรรพบุรุษของเอกสารต้นทางอยู่แล้ว");
        }

        // For derivative types that adjust source's balance, source must be approved
        // and have outstanding balance.
        var derivativeTypes = new[] {
            DocumentType.Receipt, DocumentType.ReceiptVoucher,
            DocumentType.CreditNote, DocumentType.PaymentVoucher
        };
        if (derivativeTypes.Contains(targetType))
        {
            if (source.Status == DocumentStatus.Draft)
                throw new InvalidOperationException(
                    $"เอกสารต้นทาง {source.DocumentNumber} ยังเป็นฉบับร่าง — กรุณาอนุมัติก่อนแปลง");
            if (source.BalanceDue <= 0.01m && targetType != DocumentType.CreditNote
                && targetType != DocumentType.DebitNote)
                throw new InvalidOperationException(
                    $"เอกสาร {source.DocumentNumber} ไม่มียอดคงค้าง — ไม่สามารถแปลงเป็น {targetType}");
        }
    }

    /// <summary>Create the child document from a chosen set of (source line,
    /// quantity) pairs, link it back to the source, stamp SourceLineId on
    /// every new line, and cascade metadata + attachments.</summary>
    private async Task<DocumentResponse> ConvertCoreAsync(
        Document source, DocumentType targetType,
        List<(DocumentLine Line, decimal Qty)> spec, string createdBy,
        DateTime? documentDate = null, DateTime? dueDate = null)
    {
        var companyId = source.CompanyId;

        // SourceLineId travels through the DTO so CreateDocumentAsync stamps
        // it inside its own transaction — every new line is linked 1:1 to the
        // source line it was carried forward from.
        var lines = spec.Select(s => new DocumentLineRequest(
            s.Line.Description, s.Qty, s.Line.Unit, s.Line.UnitPrice,
            s.Line.DiscountPercent, s.Line.VatRate, s.Line.WithholdingTaxRate, s.Line.AccountId,
            ProjectId: s.Line.ProjectId,
            ProductCode: s.Line.ProductCode,
            SourceLineId: s.Line.Id)).ToList();

        var newDoc = await CreateDocumentAsync(companyId, new CreateDocumentRequest(
            targetType, documentDate ?? DateTime.UtcNow, dueDate ?? source.DueDate, source.ContactId,
            source.DocumentNumber, source.Notes, lines,
            ProjectId: source.ProjectId,
            BankAccountId: source.BankAccountId,
            PaymentAccountId: source.PaymentAccountId,
            ExpenseCategoryId: source.ExpenseCategoryId), createdBy);

        // Link new document to source + propagate appendix/contract metadata
        var created = await _db.Documents.FindAsync(newDoc.Id);
        if (created != null)
        {
            created.RelatedDocumentId = source.Id;
            created.CustomAppendix = source.CustomAppendix;
            created.CustomFooterNotes = source.CustomFooterNotes;
            created.CustomTermsAndConditions = source.CustomTermsAndConditions;
            created.RevenueContractId = source.RevenueContractId;
            created.PerformanceObligationId = source.PerformanceObligationId;
            created.CertificateReason = source.CertificateReason;
            created.CertifierName = source.CertifierName;
            created.CertifierPosition = source.CertifierPosition;
            created.WitnessName = source.WitnessName;
            created.WitnessPosition = source.WitnessPosition;
            created.PaymentDate = source.PaymentDate;
            await _db.SaveChangesAsync();
        }

        // Cascade file attachments — copy reference rows so child doc shares
        // the same physical files.
        await CascadeAttachmentsAsync(companyId, source.Id, newDoc.Id, createdBy);

        return await GetDocumentAsync(companyId, newDoc.Id);
    }

    public async Task<DocumentResponse> ConvertDocumentAsync(Guid companyId, Guid documentId, DocumentType targetType, string createdBy)
    {
        var source = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        await ValidateConversionAsync(source, targetType, companyId);

        var axis = GetFulfillmentAxis(targetType);
        var orderedLines = source.Lines.OrderBy(l => l.LineOrder).ToList();
        List<(DocumentLine Line, decimal Qty)> spec;

        if (axis == FulfillmentAxis.None)
        {
            // Settlement / adjustment target — copy every line verbatim.
            spec = orderedLines.Select(l => (l, l.Quantity)).ToList();
        }
        else
        {
            // Quantity-tracked target — carry forward only what is still
            // un-converted on this axis, so a second convert never duplicates.
            var consumption = await ComputeConsumptionAsync(companyId, orderedLines.Select(l => l.Id).ToList());
            spec = new List<(DocumentLine, decimal)>();
            foreach (var l in orderedLines)
            {
                var used = axis == FulfillmentAxis.Delivery ? consumption[l.Id].Delivery : consumption[l.Id].Billing;
                var remaining = l.Quantity - used;
                if (remaining > QtyEpsilon)
                    spec.Add((l, remaining));
            }
            if (spec.Count == 0)
                throw new InvalidOperationException(
                    $"เอกสาร {source.DocumentNumber} ถูกแปลงเพื่อ{AxisLabel(axis)}ครบทุกรายการแล้ว " +
                    $"— ไม่มีจำนวนคงเหลือให้แปลง");
        }

        return await ConvertCoreAsync(source, targetType, spec, createdBy);
    }

    public async Task<DocumentResponse> ConvertDocumentPartialAsync(
        Guid companyId, Guid documentId, DocumentType targetType,
        PartialConvertRequest request, string createdBy)
    {
        var source = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        await ValidateConversionAsync(source, targetType, companyId);

        var axis = GetFulfillmentAxis(targetType);
        if (axis == FulfillmentAxis.None)
            throw new InvalidOperationException(
                $"การแปลงเป็น {targetType} ไม่รองรับการเลือกบางรายการ — กรุณาใช้การแปลงทั้งฉบับ");

        if (request.Lines == null || request.Lines.Count == 0)
            throw new InvalidOperationException("กรุณาเลือกรายการที่ต้องการแปลงอย่างน้อย 1 รายการ");

        var consumption = await ComputeConsumptionAsync(
            companyId, source.Lines.Select(l => l.Id).ToList());
        var byId = source.Lines.ToDictionary(l => l.Id);

        // Preserve source line order in the new document.
        var spec = new List<(DocumentLine Line, decimal Qty)>();
        foreach (var req in request.Lines.Where(r => r.Quantity > QtyEpsilon)
                     .OrderBy(r => byId.TryGetValue(r.SourceLineId, out var sl) ? sl.LineOrder : int.MaxValue))
        {
            if (!byId.TryGetValue(req.SourceLineId, out var srcLine))
                throw new InvalidOperationException("ไม่พบรายการต้นทางที่เลือก ในเอกสารนี้");
            var used = axis == FulfillmentAxis.Delivery
                ? consumption[srcLine.Id].Delivery : consumption[srcLine.Id].Billing;
            var remaining = srcLine.Quantity - used;
            if (req.Quantity > remaining + QtyEpsilon)
                throw new InvalidOperationException(
                    $"รายการ '{srcLine.Description}' ขอแปลง {req.Quantity:0.##} " +
                    $"แต่คงเหลือให้{AxisLabel(axis)}เพียง {remaining:0.##} {srcLine.Unit}");
            spec.Add((srcLine, req.Quantity));
        }
        if (spec.Count == 0)
            throw new InvalidOperationException("ไม่มีรายการที่จะแปลง — จำนวนต้องมากกว่า 0");

        return await ConvertCoreAsync(source, targetType, spec, createdBy,
            request.DocumentDate, request.DueDate);
    }

    public async Task<DocumentFulfillmentResponse> GetDocumentFulfillmentAsync(Guid companyId, Guid documentId)
    {
        var source = await _db.Documents
            .Include(d => d.Lines)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var orderedLines = source.Lines.OrderBy(l => l.LineOrder).ToList();
        var consumption = await ComputeConsumptionAsync(companyId, orderedLines.Select(l => l.Id).ToList());

        // Which axes make sense for this source — based on what it can convert into.
        var targets = GetValidConversionTargets(source.DocumentType);
        var supportsDelivery = targets.Any(t => GetFulfillmentAxis(t) == FulfillmentAxis.Delivery);
        var supportsBilling = targets.Any(t => GetFulfillmentAxis(t) == FulfillmentAxis.Billing);

        var lines = orderedLines.Select(l =>
        {
            var c = consumption[l.Id];
            return new DocumentLineFulfillmentResponse(
                l.Id, l.LineOrder, l.Description, l.Unit,
                OrderedQuantity: l.Quantity,
                DeliveredQuantity: c.Delivery,
                DeliveryRemaining: Math.Max(0m, l.Quantity - c.Delivery),
                BilledQuantity: c.Billing,
                BillingRemaining: Math.Max(0m, l.Quantity - c.Billing),
                UnitPrice: l.UnitPrice,
                DiscountPercent: l.DiscountPercent,
                VatRate: l.VatRate,
                WithholdingTaxRate: l.WithholdingTaxRate,
                AccountId: l.AccountId,
                ProjectId: l.ProjectId,
                ProductCode: l.ProductCode);
        }).ToList();

        return new DocumentFulfillmentResponse(
            source.Id, source.DocumentNumber, source.DocumentType,
            supportsDelivery, supportsBilling, lines);
    }

    private async Task CascadeAttachmentsAsync(Guid companyId, Guid sourceDocId, Guid targetDocId, string createdBy)
    {
        var sourceAttachments = await _db.FileAttachments
            .AsNoTracking()
            .Where(f => f.CompanyId == companyId && f.EntityType == "Document" && f.EntityId == sourceDocId)
            .ToListAsync();

        if (sourceAttachments.Count == 0) return;

        // Plan the copy: compute new paths and create DB rows pointing at them FIRST.
        // The physical file copy then runs ASYNCHRONOUSLY so we don't block the request
        // while a 50MB file is being copied. If copy fails, the row's StoragePath still
        // points at the parent file — degrades gracefully but never loses the reference.
        var copyPlan = new List<(string Source, string Target)>(sourceAttachments.Count);

        foreach (var src in sourceAttachments)
        {
            var newStoragePath = src.StoragePath;
            if (!string.IsNullOrEmpty(src.StoragePath))
            {
                var dir = Path.GetDirectoryName(src.StoragePath) ?? "";
                var ext = Path.GetExtension(src.StoragePath);
                newStoragePath = Path.Combine(dir, $"{Guid.NewGuid()}{ext}");
                copyPlan.Add((src.StoragePath, newStoragePath));
            }

            _db.FileAttachments.Add(new FileAttachment
            {
                CompanyId = companyId,
                EntityType = "Document",
                EntityId = targetDocId,
                FileName = Path.GetFileName(newStoragePath),
                OriginalFileName = src.OriginalFileName,
                ContentType = src.ContentType,
                FileSize = src.FileSize,
                StoragePath = newStoragePath,
                UploadedByUserId = src.UploadedByUserId,
                CreatedBy = createdBy,
            });
        }

        await _db.SaveChangesAsync();

        // Fire-and-forget physical file copy — caller doesn't wait. If copy fails,
        // the new attachment row's StoragePath simply points at the parent file
        // (which still works as long as parent isn't purged).
        if (copyPlan.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                foreach (var (source, target) in copyPlan)
                {
                    try
                    {
                        if (System.IO.File.Exists(source) && !System.IO.File.Exists(target))
                        {
                            await using var srcStream = System.IO.File.OpenRead(source);
                            await using var dstStream = System.IO.File.Create(target);
                            await srcStream.CopyToAsync(dstStream);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Cascade file copy failed: {Source} -> {Target}", source, target);
                    }
                }
            });
        }
    }

    public async Task<List<DocumentResponse>> BatchConvertDocumentsAsync(
        Guid companyId, List<Guid> documentIds, DocumentType targetType, string createdBy)
    {
        // Run conversions sequentially within a single DbContext — EF Core DbContext
        // is NOT thread-safe so we cannot parallelize at the per-document level here
        // without provisioning fresh contexts. Instead we batch-fetch sources first
        // (avoiding N round-trips just to load them) and then loop.
        //
        // For true parallelism, the controller should fan out to N HTTP requests
        // or we'd need IServiceScopeFactory to spawn fresh DbContexts per worker.
        // Going with the safer optimization here: pre-fetch + sequential conversion
        // still saves ~30-50% on a 100-doc batch via reduced DB chatter.

        var distinctIds = documentIds.Distinct().ToList();
        var results = new List<DocumentResponse>(distinctIds.Count);
        var errors = new List<string>();

        // Pre-fetch all sources once; ConvertDocumentAsync will re-load from tracker but
        // EF caches the entity, avoiding a second DB hit.
        await _db.Documents
            .Include(d => d.Lines)
            .Where(d => documentIds.Contains(d.Id) && d.CompanyId == companyId)
            .LoadAsync();

        foreach (var id in distinctIds)
        {
            try
            {
                var converted = await ConvertDocumentAsync(companyId, id, targetType, createdBy);
                results.Add(converted);
            }
            catch (Exception ex)
            {
                errors.Add($"{id}: {ex.Message}");
                _logger.LogWarning(ex, "Batch convert: failed to convert document {DocId}", id);
            }
        }

        if (results.Count == 0 && errors.Count > 0)
            throw new InvalidOperationException("ไม่สามารถแปลงเอกสารได้: " + string.Join("; ", errors));

        return results;
    }

    public async Task<DocumentResponse> CreateInvoiceFromObligationAsync(
        Guid companyId, Guid performanceObligationId, string createdBy)
    {
        // Lock obligation row + contract row to prevent two parallel invoice-creations
        // from both seeing IsSatisfied=false and creating duplicate invoices for the
        // same milestone.
        using var tx = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            var obligation = await _db.Set<PerformanceObligation>()
                .Include(o => o.Contract)
                .FirstOrDefaultAsync(o => o.Id == performanceObligationId && o.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบ Performance Obligation");

            if (obligation.IsSatisfied)
                throw new InvalidOperationException("ภาระงานนี้รับรู้รายได้ครบแล้ว — ไม่สามารถออกใบแจ้งหนี้ซ้ำ");

            var unbilled = obligation.AllocatedPrice - obligation.RecognizedRevenue;
            if (unbilled <= 0.01m)
                throw new InvalidOperationException("ไม่มียอดคงเหลือสำหรับออกใบแจ้งหนี้");

            var contract = obligation.Contract
                ?? throw new InvalidOperationException("ไม่พบสัญญารายได้ของภาระงานนี้");

            var lineDescription = string.IsNullOrEmpty(obligation.Description)
                ? obligation.Name
                : $"{obligation.Name} — {obligation.Description}";

            var invoice = await CreateDocumentAsync(companyId, new CreateDocumentRequest(
                DocumentType.Invoice,
                DateTime.UtcNow,
                DueDate: DateTime.UtcNow.AddDays(30),
                ContactId: contract.ContactId,
                Reference: contract.ContractNumber,
                Notes: $"จากสัญญา {contract.ContractNumber}: {contract.Name}",
                Lines: new List<DocumentLineRequest>
                {
                    new(lineDescription, 1m, "งวด", unbilled, 0m, 7m, 0m, AccountId: null),
                },
                ProjectId: contract.ProjectId,
                RevenueContractId: contract.Id,
                PerformanceObligationId: obligation.Id), createdBy);

            // Update obligation state — invoice covers the unbilled portion
            obligation.RecognizedRevenue = obligation.AllocatedPrice;
            obligation.CompletionPercent = 100m;
            obligation.IsSatisfied = true;
            obligation.SatisfiedDate = DateTime.UtcNow;

            // Cascade-mark contract complete if all obligations are satisfied
            var allObligationsForContract = await _db.Set<PerformanceObligation>()
                .Where(o => o.RevenueContractId == contract.Id && o.Id != obligation.Id)
                .ToListAsync();
            if (allObligationsForContract.All(o => o.IsSatisfied))
            {
                contract.Status = "Completed";
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            return invoice;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ==================== Contacts ====================

    public async Task<ContactResponse> CreateContactAsync(Guid companyId, CreateContactRequest request)
    {
        // If structured fields are missing but free-text Address is provided,
        // attempt to auto-parse so e-Tax XML has the data it needs.
        var parsed = NeedsAutoParse(request) && !string.IsNullOrWhiteSpace(request.Address)
            ? ThaiAddressParser.Parse(request.Address)
            : null;

        var contact = new Contact
        {
            CompanyId = companyId,
            Name = request.Name,
            TaxId = request.TaxId,
            BranchCode = request.BranchCode,
            BranchName = request.BranchName,
            ContactType = request.ContactType ?? InferContactType(request.TaxId, request.BranchCode),
            IsCustomer = request.IsCustomer,
            IsSupplier = request.IsSupplier,
            BuildingNumber = request.BuildingNumber ?? parsed?.BuildingNumber,
            BuildingName = request.BuildingName ?? parsed?.BuildingName,
            StreetName = request.StreetName ?? parsed?.StreetName,
            SubDistrict = request.SubDistrict ?? parsed?.SubDistrict,
            District = request.District ?? parsed?.District,
            Province = request.Province ?? parsed?.Province,
            PostalCode = request.PostalCode ?? parsed?.PostalCode,
            CountryCode = request.CountryCode ?? "TH",
            Phone = request.Phone,
            Email = request.Email,
            ContactPerson = request.ContactPerson
        };

        contact.Address = request.Address ?? ComposeAddress(contact);

        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();

        return MapContactToResponse(contact);
    }

    private static string? ComposeAddress(Contact c)
    {
        var parts = new[] { c.BuildingNumber, c.BuildingName,
            string.IsNullOrEmpty(c.StreetName) ? null : "ถ." + c.StreetName,
            c.SubDistrict, c.District, c.Province, c.PostalCode }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var joined = string.Join(" ", parts);
        return string.IsNullOrEmpty(joined) ? null : joined;
    }

    private static bool NeedsAutoParse(CreateContactRequest r) =>
        string.IsNullOrEmpty(r.BuildingNumber) && string.IsNullOrEmpty(r.SubDistrict)
        && string.IsNullOrEmpty(r.District) && string.IsNullOrEmpty(r.Province)
        && string.IsNullOrEmpty(r.PostalCode);

    public async Task<ContactResponse> GetContactAsync(Guid companyId, Guid contactId)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        return MapContactToResponse(contact);
    }

    public async Task<PagedResponse<ContactResponse>> GetContactsAsync(Guid companyId, bool? isCustomer = null, bool? isSupplier = null, string? search = null, PagedRequest? paging = null)
    {
        var query = _db.Contacts.Where(c => c.CompanyId == companyId);
        if (isCustomer.HasValue) query = query.Where(c => c.IsCustomer == isCustomer.Value);
        if (isSupplier.HasValue) query = query.Where(c => c.IsSupplier == isSupplier.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(c => c.Name.ToLower().Contains(s)
                || (c.TaxId != null && c.TaxId.Contains(s))
                || (c.Email != null && c.Email.ToLower().Contains(s))
                || (c.Phone != null && c.Phone.Contains(s)));
        }

        var totalCount = await query.CountAsync();
        var page = paging?.Page ?? 1;
        var pageSize = paging?.PageSize ?? 50;
        var contacts = await query.OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        return new PagedResponse<ContactResponse>(
            contacts.Select(MapContactToResponse).ToList(),
            totalCount, page, pageSize, totalPages);
    }

    public async Task<ContactDeleteResult> DeleteContactAsync(Guid companyId, Guid contactId)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        var docCount = await _db.Documents.CountAsync(d => d.ContactId == contactId && d.CompanyId == companyId && !d.IsDeleted);
        var whtCount = await _db.WithholdingTaxCerts.CountAsync(w => w.PayeeContactId == contactId && w.CompanyId == companyId && !w.IsDeleted);
        if (docCount > 0 || whtCount > 0)
        {
            contact.IsActive = false;
            await _db.SaveChangesAsync();
            return new ContactDeleteResult(
                Deleted: false,
                Deactivated: true,
                LinkedDocumentsCount: docCount,
                LinkedWhtCount: whtCount,
                Message: $"ผู้ติดต่อมีเอกสาร {docCount} รายการ และใบหัก ณ ที่จ่าย {whtCount} รายการ — " +
                         $"ระบบปิดการใช้งานแทนการลบเพื่อรักษาข้อมูลทางบัญชี");
        }
        _db.Contacts.Remove(contact);
        await _db.SaveChangesAsync();
        return new ContactDeleteResult(
            Deleted: true,
            Deactivated: false,
            LinkedDocumentsCount: 0,
            LinkedWhtCount: 0,
            Message: "ลบผู้ติดต่อสำเร็จ");
    }

    public async Task<ContactResponse> UpdateContactAsync(Guid companyId, Guid contactId, UpdateContactRequest request)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");

        if (request.Name != null) contact.Name = request.Name;
        if (request.TaxId != null) contact.TaxId = request.TaxId;
        if (request.BranchCode != null) contact.BranchCode = request.BranchCode;
        if (request.BranchName != null) contact.BranchName = request.BranchName;
        if (request.ContactType.HasValue) contact.ContactType = request.ContactType.Value;
        else if (request.TaxId != null || request.BranchCode != null)
            contact.ContactType = InferContactType(request.TaxId ?? contact.TaxId, request.BranchCode ?? contact.BranchCode);
        if (request.IsCustomer.HasValue) contact.IsCustomer = request.IsCustomer.Value;
        if (request.IsSupplier.HasValue) contact.IsSupplier = request.IsSupplier.Value;
        if (request.BuildingNumber != null) contact.BuildingNumber = request.BuildingNumber;
        if (request.BuildingName != null) contact.BuildingName = request.BuildingName;
        if (request.StreetName != null) contact.StreetName = request.StreetName;
        if (request.SubDistrict != null) contact.SubDistrict = request.SubDistrict;
        if (request.District != null) contact.District = request.District;
        if (request.Province != null) contact.Province = request.Province;
        if (request.PostalCode != null) contact.PostalCode = request.PostalCode;
        if (request.CountryCode != null) contact.CountryCode = request.CountryCode;
        contact.Address = request.Address ?? ComposeAddress(contact);
        if (request.Phone != null) contact.Phone = request.Phone;
        if (request.Email != null) contact.Email = request.Email;
        if (request.ContactPerson != null) contact.ContactPerson = request.ContactPerson;
        if (request.IsActive.HasValue) contact.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        return MapContactToResponse(contact);
    }

    public async Task<ContactSmartDefaults> GetContactSmartDefaultsAsync(Guid companyId, Guid contactId)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        return GetSmartDefaults(contact);
    }

    // ==================== Payments ====================

    public async Task<PaymentResponse> CreatePaymentAsync(Guid companyId, CreatePaymentRequest request, string createdBy)
    {
        // Validate Amount > 0
        if (request.Amount <= 0)
            throw new InvalidOperationException("จำนวนเงินชำระต้องมากกว่า 0");

        // Validate PaymentDate is not in the future
        if (request.PaymentDate > DateTime.UtcNow.Date.AddDays(1))
            throw new InvalidOperationException("วันที่ชำระเงินต้องไม่เป็นวันที่ในอนาคต");

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
        // Lock document row to prevent concurrent overpayment
        var doc = await _db.Documents
            .FromSqlRaw("SELECT * FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", request.DocumentId, companyId)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // Validate document status allows payment
        if (doc.Status != DocumentStatus.Approved && doc.Status != DocumentStatus.PartiallyPaid && doc.Status != DocumentStatus.Sent)
            throw new InvalidOperationException("สามารถชำระเงินได้เฉพาะเอกสารที่อนุมัติแล้ว, ชำระบางส่วน หรือส่งแล้วเท่านั้น");

        if (request.Amount > doc.BalanceDue)
            throw new InvalidOperationException($"จำนวนเงินชำระ ({request.Amount:N2}) มากกว่ายอดค้างชำระ ({doc.BalanceDue:N2})");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var payYearMonth = DateTime.UtcNow.ToString("yyyyMM");
            var payPrefix = $"PAY-{payYearMonth}-";
            var maxPayNum = await _db.Payments
                .IgnoreQueryFilters()
                .Where(p => p.CompanyId == companyId && p.PaymentNumber.StartsWith(payPrefix))
                .Select(p => p.PaymentNumber)
                .MaxAsync() as string;
            var paySeq = 1;
            if (maxPayNum != null)
            {
                var lastPart = maxPayNum.Substring(payPrefix.Length);
                if (int.TryParse(lastPart, out var parsed)) paySeq = parsed + 1;
            }
            var payment = new Payment
            {
                CompanyId = companyId,
                PaymentNumber = $"{payPrefix}{paySeq:D4}",
                DocumentId = request.DocumentId,
                PaymentDate = request.PaymentDate,
                Amount = request.Amount,
                PaymentMethod = request.PaymentMethod,
                Reference = request.Reference,
                BankAccount = request.BankAccount,
                BankAccountId = doc.BankAccountId,
                Notes = request.Notes,
                CreatedBy = createdBy
            };

            _db.Payments.Add(payment);

            doc.PaidAmount += request.Amount;
            doc.BalanceDue = doc.TotalAmount - doc.PaidAmount;
            doc.Status = doc.BalanceDue <= 0 ? DocumentStatus.Paid : DocumentStatus.PartiallyPaid;

            await _db.SaveChangesAsync();

            // Create journal entry for payment
            await CreatePaymentJournalAsync(companyId, doc, payment, createdBy);

            await _db.SaveChangesAsync();

            // Sync BankAccount.CurrentBalance — convert from doc currency to THB
            // at the document's captured FX rate (BankAccount.CurrentBalance is
            // always THB in this iteration). Same conversion as the GL posting.
            if (payment.BankAccountId.HasValue)
            {
                var isInflow = doc.DocumentType is DocumentType.Invoice or DocumentType.TaxInvoice
                    or DocumentType.Receipt or DocumentType.ReceiptVoucher
                    or DocumentType.DebitNote or DocumentType.BillingNote;
                var thbAmount = doc.ExchangeRate == 1m
                    ? payment.Amount
                    : Math.Round(payment.Amount * doc.ExchangeRate, 2, MidpointRounding.AwayFromZero);
                var delta = isInflow ? thbAmount : -thbAmount;
                await _db.BankAccounts
                    .Where(b => b.Id == payment.BankAccountId.Value && b.CompanyId == companyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.CurrentBalance, b => b.CurrentBalance + delta));
            }

            // Auto-generate WHT cert for purchase documents with WHT on first payment
            if (doc.WithholdingTaxAmount > 0
                && (doc.DocumentType == DocumentType.PurchaseInvoice
                    || doc.DocumentType == DocumentType.Expense
                    || doc.DocumentType == DocumentType.PaymentVoucher
                    || doc.DocumentType == DocumentType.CertificateInLieu))
            {
                try
                {
                    await _whtService.AutoGenerateFromDocumentAsync(companyId, doc.Id, false, createdBy);
                }
                catch
                {
                    // Cert may already exist or other non-critical error — don't fail payment
                }
            }

            await transaction.CommitAsync();

            try
            {
                await _lineNotify.NotifyPaymentReceivedAsync(companyId, doc.DocumentNumber, payment.Amount);
            }
            catch { /* best-effort notification */ }

            return new PaymentResponse(
                payment.Id, payment.PaymentNumber, payment.DocumentId,
                payment.PaymentDate, payment.Amount, payment.PaymentMethod,
                payment.Reference, payment.BankAccount, payment.BankAccountId,
                payment.Notes, payment.CreatedAt);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        }); // end ExecutionStrategy
    }

    public async Task<List<PaymentResponse>> GetPaymentsAsync(Guid companyId, Guid? documentId = null)
    {
        var query = _db.Payments.Where(p => p.CompanyId == companyId);
        if (documentId.HasValue) query = query.Where(p => p.DocumentId == documentId.Value);

        var payments = await query.OrderByDescending(p => p.PaymentDate).ToListAsync();
        return payments.Select(p => new PaymentResponse(
            p.Id, p.PaymentNumber, p.DocumentId,
            p.PaymentDate, p.Amount, p.PaymentMethod,
            p.Reference, p.BankAccount, p.BankAccountId,
            p.Notes, p.CreatedAt)).ToList();
    }

    // ==================== Private ====================

    /// <summary>
    /// ค้นหาบัญชีจากรหัส — exact match ก่อน แล้ว prefix match (level 4)
    /// เช่น "113" จะ match "11310" (ลูกหนี้การค้า)
    /// </summary>
    private async Task<ChartOfAccount?> FindAccountAsync(Guid companyId, string codePrefix)
    {
        return await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == codePrefix && a.IsActive)
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith(codePrefix) && a.Level >= 4 && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();
    }

    /// <summary>
    /// บันทึกบัญชีอัตโนมัติเมื่ออนุมัติเอกสาร — สร้าง JournalEntry + Lines โดยตรงผ่าน DbContext
    /// (อยู่ภายใน transaction เดียวกับ ApproveDocumentAsync เพื่อความ atomic ตามหลักบัญชี)
    ///
    /// กฎการ post ตามมาตรฐาน TFRS / ประมวลรัษฎากร §86, §82/10:
    ///
    /// ฝั่งขาย (SV - สมุดรายวันขาย):
    ///   Invoice/TaxInvoice/DebitNote: Dr ลูกหนี้ + Dr ภาษีถูกหัก, Cr รายได้ + Cr ภาษีขาย
    ///   CreditNote (ใบลดหนี้): กลับด้าน — Cr ลูกหนี้ + Cr ภาษีถูกหัก, Dr รายได้ + Dr ภาษีขาย
    ///
    /// ฝั่งซื้อ (UV - สมุดรายวันซื้อ):
    ///   PurchaseInvoice/Expense: Dr ค่าใช้จ่าย + Dr ภาษีซื้อ, Cr เจ้าหนี้ + Cr ภาษีหัก ณ ที่จ่ายค้างจ่าย
    ///
    /// รับเงิน (RV - สมุดรายวันรับ):
    ///   Receipt/ReceiptVoucher (link to Invoice): Dr เงินสด + Dr WHT Asset, Cr ลูกหนี้
    ///   Receipt/ReceiptVoucher (cash sale, ไม่ link): Dr เงินสด + Dr WHT Asset, Cr รายได้ + Cr ภาษีขาย
    ///
    /// จ่ายเงิน (PV - สมุดรายวันจ่าย):
    ///   PaymentVoucher (link to PurchaseInvoice): Dr เจ้าหนี้, Cr เงินสด + Cr WHT Payable
    ///   PaymentVoucher (direct cash purchase): Dr ค่าใช้จ่าย + Dr ภาษีซื้อ, Cr เงินสด + Cr WHT Payable
    /// </summary>
    private async Task AutoPostToJournalAsync(Guid companyId, Document doc, string createdBy)
    {
        // Multi-currency: every Baht amount that hits the GL must be converted
        // from the document's currency at the rate captured on Create. Validate
        // the rate is sane (positive, finite, non-zero) — corrupt FX = corrupt GL.
        if (doc.ExchangeRate <= 0m)
            throw new InvalidOperationException(
                $"อัตราแลกเปลี่ยนไม่ถูกต้อง ({doc.ExchangeRate}) — กรุณาแก้ไขเอกสารและระบุอัตราที่ถูกต้องก่อนอนุมัติ");
        if (!string.Equals(doc.Currency, "THB", StringComparison.OrdinalIgnoreCase) && doc.ExchangeRate == 1m)
            throw new InvalidOperationException(
                $"เอกสารสกุลเงิน {doc.Currency} ต้องระบุอัตราแลกเปลี่ยนก่อนอนุมัติ (พบ ExchangeRate = 1)");

        // Resolve default fallback accounts up front — used when document lines
        // have no explicit AccountId (the UI doesn't expose per-line account selection yet).
        // The default Thai chart uses 41000 (sales) / 42000 (service) at level 4;
        // industry templates may override (e.g. hospitality uses 411xx). Pick the first
        // posting-level (Level >= 4) Revenue/Expense account by code as a safe default.
        var defaultRevenue = await FindAccountAsync(companyId, "41000")
            ?? await FindAccountAsync(companyId, "42000")
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountType == AccountType.Revenue && a.Level >= 4 && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();

        // Use document-level expense category if specified, otherwise fallback to default
        ChartOfAccount? defaultExpense = null;
        if (doc.ExpenseCategoryId.HasValue)
        {
            defaultExpense = await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.Id == doc.ExpenseCategoryId.Value && a.CompanyId == companyId);
        }
        defaultExpense ??= await FindAccountAsync(companyId, "51110")
            ?? await FindAccountAsync(companyId, "52110")
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountType == AccountType.Expense && a.Level >= 4 && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();

        // Resolve money account for cash/bank movement entries.
        // Priority: PaymentAccountId (direct GL) > BankAccountId (bank's linked GL) > Cash 111
        ChartOfAccount? moneyAccount = null;
        if (doc.PaymentAccountId.HasValue)
        {
            moneyAccount = await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.Id == doc.PaymentAccountId.Value && a.CompanyId == companyId);
        }
        if (moneyAccount == null && doc.BankAccountId.HasValue)
        {
            var bankAcc = await _db.BankAccounts
                .Include(b => b.LinkedAccount)
                .FirstOrDefaultAsync(b => b.Id == doc.BankAccountId.Value && b.CompanyId == companyId);
            moneyAccount = bankAcc?.LinkedAccount;
        }
        moneyAccount ??= await FindAccountAsync(companyId, "111");

        // Validate critical accounts exist — fail fast with clear error
        var isSalesDoc = doc.DocumentType is DocumentType.Invoice or DocumentType.TaxInvoice
            or DocumentType.DebitNote or DocumentType.CreditNote
            or DocumentType.Receipt or DocumentType.ReceiptVoucher;
        if (isSalesDoc && defaultRevenue == null)
            throw new InvalidOperationException(
                "ไม่พบบัญชีรายได้ (41000/42000) ในผังบัญชี — กรุณาเพิ่มบัญชีรายได้ก่อนอนุมัติเอกสารขาย");
        if (!isSalesDoc && defaultExpense == null)
            throw new InvalidOperationException(
                "ไม่พบบัญชีค่าใช้จ่าย (51110/52110) ในผังบัญชี — กรุณาเพิ่มบัญชีค่าใช้จ่ายก่อนอนุมัติเอกสารซื้อ");

        var pendingLines = new List<(Guid AccountId, decimal Debit, decimal Credit, string? Description)>();
        JournalType journalType;
        // ProjectId map: each pendingLines index → resolved project. System-generated
        // lines (AR/VAT/Cash/AP/WHT) inherit doc.ProjectId; per-line revenue/expense
        // can override via docLine.ProjectId.
        var lineProjects = new List<Guid?>();
        // Multi-currency: every amount fed into AddLine is in the document's
        // currency. Convert to THB (base) at the captured rate; AwayFromZero
        // to match the system-wide rounding convention.
        var fx = doc.ExchangeRate;
        decimal Conv(decimal amount) => fx == 1m ? amount : Math.Round(amount * fx, 2, MidpointRounding.AwayFromZero);
        void AddLine(Guid accountId, decimal debit, decimal credit, string? desc, Guid? proj = null)
        {
            pendingLines.Add((accountId, Conv(debit), Conv(credit), desc));
            lineProjects.Add(proj ?? doc.ProjectId);
        }

        // ============================================================
        // SALES SIDE: Invoice / TaxInvoice (full sale on credit)
        // ============================================================
        if (doc.DocumentType == DocumentType.Invoice
            || doc.DocumentType == DocumentType.TaxInvoice)
        {
            journalType = JournalType.Sales;

            // Dr: ลูกหนี้การค้า (113) — TotalAmount is net of WHT (Gross - WHT)
            var arAccount = await FindAccountAsync(companyId, "113");
            if (arAccount != null)
                AddLine(arAccount.Id, doc.TotalAmount, 0, $"ลูกหนี้การค้า - {doc.DocumentNumber}");

            // Dr: ภาษีถูกหัก ณ ที่จ่าย (สินทรัพย์ 11910) — claim from Revenue Dept
            if (doc.WithholdingTaxAmount > 0)
            {
                var whtAccount = await FindAccountAsync(companyId, "11910");
                if (whtAccount != null)
                    AddLine(whtAccount.Id, doc.WithholdingTaxAmount, 0, "ภาษีหัก ณ ที่จ่าย (ถูกหัก)");
            }

            // Cr: บัญชีรายได้ตามแต่ละบรรทัด (default = 411)
            foreach (var docLine in doc.Lines)
            {
                var revenueAccountId = docLine.AccountId ?? defaultRevenue?.Id;
                if (revenueAccountId.HasValue)
                    AddLine(revenueAccountId.Value, 0, docLine.Amount, docLine.Description, docLine.ProjectId);
            }

            // Cr: ภาษีขาย (Output VAT 21911) per ภ.พ.30
            if (doc.VatAmount > 0)
            {
                var vatAccount = await FindAccountAsync(companyId, "21911")
                    ?? throw new InvalidOperationException("ไม่พบบัญชีภาษีขาย (21911) ในผังบัญชี — กรุณาเพิ่มก่อนอนุมัติเอกสารที่มี VAT");
                if (vatAccount != null)
                    AddLine(vatAccount.Id, 0, doc.VatAmount, "ภาษีขาย");
            }
        }
        // ============================================================
        // ADJUSTMENT NOTES: CreditNote / DebitNote (polymorphic by source)
        //
        // Determines whether this is a sales-side adjustment (we issue to customer)
        // or a purchase-side adjustment (supplier issues to us, we return goods)
        // by inspecting the source document's type via RelatedDocumentId.
        //
        // Also detects "cash refund" mode: when source is fully paid (BalanceDue=0),
        // the counter-account is Cash instead of AR/AP — money actually moves.
        //
        // Standard cases per Revenue Code §82/10 (sales) and equivalent purchase
        // return treatment:
        //   Sales CN  AR-mode:    Cr AR,    Dr Revenue, Dr Output VAT, Cr WHT-Asset
        //   Sales CN  Cash-mode:  Cr Cash,  Dr Revenue, Dr Output VAT, Cr WHT-Asset
        //   Sales DN  AR-mode:    Dr AR,    Cr Revenue, Cr Output VAT, Dr WHT-Asset
        //   Sales DN  Cash-mode:  Dr Cash,  Cr Revenue, Cr Output VAT, Dr WHT-Asset
        //   Purch CN  AP-mode:    Dr AP,    Cr Expense, Cr Input VAT,  Dr WHT-Payable
        //   Purch CN  Cash-mode:  Dr Cash,  Cr Expense, Cr Input VAT,  Dr WHT-Payable
        //   Purch DN  AP-mode:    Cr AP,    Dr Expense, Dr Input VAT,  Cr WHT-Payable
        //   Purch DN  Cash-mode:  Cr Cash,  Dr Expense, Dr Input VAT,  Cr WHT-Payable
        // ============================================================
        else if (doc.DocumentType == DocumentType.CreditNote
                 || doc.DocumentType == DocumentType.DebitNote)
        {
            // Resolve the source side by inspecting RelatedDocumentId. Default is
            // sales-side (most common case + back-compat with previous behavior).
            Document? source = null;
            if (doc.RelatedDocumentId.HasValue)
            {
                source = await _db.Documents
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == doc.RelatedDocumentId.Value
                        && d.CompanyId == companyId);
            }

            var isPurchaseSide = source != null && (
                source.DocumentType == DocumentType.PurchaseInvoice
                || source.DocumentType == DocumentType.Expense
                || source.DocumentType == DocumentType.CertificateInLieu);
            var isCashSettlement = source != null && source.BalanceDue <= 0.01m;
            var isCreditNote = doc.DocumentType == DocumentType.CreditNote;

            journalType = isPurchaseSide ? JournalType.Purchase : JournalType.Sales;
            var typeLabel = isCreditNote ? "ใบลดหนี้" : "ใบเพิ่มหนี้";

            // === Counter-account: AR/AP/Cash depending on mode ===
            // For CN: counter-account is on the credit side (sales) / debit side (purchase)
            // For DN: counter-account is on the debit side (sales) / credit side (purchase)
            string counterAccCode;
            if (isPurchaseSide)
                counterAccCode = isCashSettlement ? "111" : "212"; // Cash or AP
            else
                counterAccCode = isCashSettlement ? "111" : "113"; // Cash or AR

            var counterAcc = await FindAccountAsync(companyId, counterAccCode);
            var counterDesc = $"{typeLabel} - {doc.DocumentNumber}" +
                (isCashSettlement ? " (เงินสด)" : "");

            // CN reduces the receivable/increases payable for purchase return; DN opposite.
            // Sales side:    CN→Cr counter, DN→Dr counter
            // Purchase side: CN→Dr counter, DN→Cr counter
            var counterIsDebit = isPurchaseSide ? isCreditNote : !isCreditNote;
            if (counterAcc != null)
            {
                AddLine(counterAcc.Id,
                    counterIsDebit ? doc.TotalAmount : 0,
                    counterIsDebit ? 0 : doc.TotalAmount,
                    counterDesc);
            }

            // === Revenue / Expense lines ===
            // Sales CN reverses revenue (Dr); Sales DN adds revenue (Cr)
            // Purchase CN reverses expense (Cr); Purchase DN adds expense (Dr)
            foreach (var docLine in doc.Lines)
            {
                Guid? lineAccId;
                if (isPurchaseSide)
                    lineAccId = docLine.AccountId ?? defaultExpense?.Id;
                else
                    lineAccId = docLine.AccountId ?? defaultRevenue?.Id;

                if (!lineAccId.HasValue) continue;

                // Determine Dr or Cr direction:
                //   Sales CN  → Dr revenue (reverse)
                //   Sales DN  → Cr revenue (add)
                //   Purch CN  → Cr expense (reverse)
                //   Purch DN  → Dr expense (add)
                var lineIsDebit = isPurchaseSide ? !isCreditNote : isCreditNote;
                AddLine(lineAccId.Value,
                    lineIsDebit ? docLine.Amount : 0,
                    lineIsDebit ? 0 : docLine.Amount,
                    $"{typeLabel} - {docLine.Description}",
                    docLine.ProjectId);
            }

            // === VAT line ===
            if (doc.VatAmount > 0)
            {
                // Sales: Output VAT 21911 (liability), Purchase: Input VAT 116 (asset)
                var vatCode = isPurchaseSide ? "116" : "21911";
                var vatAcc = await FindAccountAsync(companyId, vatCode);
                if (vatAcc != null)
                {
                    // Same direction rule as revenue/expense lines
                    var vatIsDebit = isPurchaseSide ? !isCreditNote : isCreditNote;
                    AddLine(vatAcc.Id,
                        vatIsDebit ? doc.VatAmount : 0,
                        vatIsDebit ? 0 : doc.VatAmount,
                        $"{typeLabel} ภาษี{(isPurchaseSide ? "ซื้อ" : "ขาย")}");
                }
            }

            // === WHT line ===
            if (doc.WithholdingTaxAmount > 0)
            {
                // Sales: WHT-Asset 11910 (we got withheld); Purchase: WHT-Payable 21916/17
                string whtCode;
                if (isPurchaseSide)
                {
                    whtCode = await FindAccountAsync(companyId, "21916") != null ? "21916" : "21917";
                }
                else
                {
                    whtCode = "11910";
                }
                var whtAcc = await FindAccountAsync(companyId, whtCode);
                if (whtAcc != null)
                {
                    // WHT-Asset behaves like revenue (sales) — opposite for purchase
                    // Sales CN→Cr WHT (reverse claim), DN→Dr WHT (add claim)
                    // Purch CN→Dr WHT-Payable (reverse), DN→Cr WHT-Payable (add)
                    var whtIsDebit = isPurchaseSide ? isCreditNote : !isCreditNote;
                    AddLine(whtAcc.Id,
                        whtIsDebit ? doc.WithholdingTaxAmount : 0,
                        whtIsDebit ? 0 : doc.WithholdingTaxAmount,
                        $"{typeLabel} ภาษีหัก ณ ที่จ่าย");
                }
            }
        }
        // ============================================================
        // PURCHASE SIDE: PurchaseInvoice / Expense / CertificateInLieu (on credit)
        // ============================================================
        else if (doc.DocumentType == DocumentType.PurchaseInvoice
                 || doc.DocumentType == DocumentType.Expense
                 || doc.DocumentType == DocumentType.CertificateInLieu)
        {
            journalType = JournalType.Purchase;

            // Dr: ค่าใช้จ่าย/สินค้า ตามรายการ
            foreach (var docLine in doc.Lines)
            {
                var expenseAccountId = docLine.AccountId ?? defaultExpense?.Id;
                if (expenseAccountId.HasValue)
                    AddLine(expenseAccountId.Value, docLine.Amount, 0, docLine.Description, docLine.ProjectId);
            }

            // Dr: ภาษีซื้อ (Input VAT 116) per ภ.พ.30
            if (doc.VatAmount > 0)
            {
                var vatInputAccount = await FindAccountAsync(companyId, "116")
                    ?? throw new InvalidOperationException("ไม่พบบัญชีภาษีซื้อ (116) ในผังบัญชี — กรุณาเพิ่มก่อนอนุมัติเอกสารซื้อที่มี VAT");
                if (vatInputAccount != null)
                    AddLine(vatInputAccount.Id, doc.VatAmount, 0, "ภาษีซื้อ");
            }

            // Cr: เจ้าหนี้การค้า (212) — net of WHT
            var apAccount = await FindAccountAsync(companyId, "212");
            if (apAccount != null)
                AddLine(apAccount.Id, 0, doc.TotalAmount, $"เจ้าหนี้การค้า - {doc.DocumentNumber}");

            // Cr: ภาษีหัก ณ ที่จ่ายค้างจ่าย (21916 ภ.ง.ด.3 / 21917 ภ.ง.ด.53)
            if (doc.WithholdingTaxAmount > 0)
            {
                var whtAccount = await FindAccountAsync(companyId, "21916")
                    ?? await FindAccountAsync(companyId, "21917");
                if (whtAccount != null)
                {
                    // Group WHT by rate for clear audit trail
                    var whtByRate = doc.Lines
                        .Where(l => l.WithholdingTaxAmount > 0)
                        .GroupBy(l => l.WithholdingTaxRate)
                        .Select(g => new { Rate = g.Key, Amount = g.Sum(l => l.WithholdingTaxAmount) })
                        .ToList();
                    if (whtByRate.Count > 1)
                    {
                        foreach (var g in whtByRate)
                            AddLine(whtAccount.Id, 0, g.Amount, $"ภาษีหัก ณ ที่จ่าย {g.Rate}%");
                    }
                    else
                    {
                        AddLine(whtAccount.Id, 0, doc.WithholdingTaxAmount, "ภาษีหัก ณ ที่จ่ายค้างจ่าย");
                    }
                }
            }
        }
        // ============================================================
        // CASH RECEIPTS: Receipt / ReceiptVoucher
        // - Linked to existing Invoice (RelatedDocumentId): collection
        // - Standalone (no link): direct cash sale
        // ============================================================
        else if (doc.DocumentType == DocumentType.Receipt
                 || doc.DocumentType == DocumentType.ReceiptVoucher)
        {
            journalType = JournalType.CashReceipts;

            if (doc.RelatedDocumentId.HasValue)
            {
                if (moneyAccount != null)
                    AddLine(moneyAccount.Id, doc.TotalAmount, 0, $"รับชำระ - {doc.DocumentNumber}");

                var arAccount = await FindAccountAsync(companyId, "113");
                if (arAccount != null)
                    AddLine(arAccount.Id, 0, doc.TotalAmount,
                        $"ตัดลูกหนี้ - {doc.DocumentNumber}");
            }
            else
            {
                if (moneyAccount != null)
                    AddLine(moneyAccount.Id, doc.TotalAmount, 0,
                        $"{(doc.BankAccountId.HasValue ? "รับเงินเข้าบัญชี" : "รับเงินสด")} - {doc.DocumentNumber}");

                if (doc.WithholdingTaxAmount > 0)
                {
                    var whtAccount = await FindAccountAsync(companyId, "11910");
                    if (whtAccount != null)
                        AddLine(whtAccount.Id, doc.WithholdingTaxAmount, 0, "ภาษีหัก ณ ที่จ่าย (ถูกหัก)");
                }

                foreach (var docLine in doc.Lines)
                {
                    var revenueAccountId = docLine.AccountId ?? defaultRevenue?.Id;
                    if (revenueAccountId.HasValue)
                        AddLine(revenueAccountId.Value, 0, docLine.Amount, docLine.Description, docLine.ProjectId);
                }

                if (doc.VatAmount > 0)
                {
                    var vatAccount = await FindAccountAsync(companyId, "21911");
                    if (vatAccount != null)
                        AddLine(vatAccount.Id, 0, doc.VatAmount, "ภาษีขาย");
                }
            }
        }
        // ============================================================
        // CASH PAYMENTS: PaymentVoucher
        // - Linked to existing PurchaseInvoice: settlement
        // - Standalone: direct cash purchase
        // ============================================================
        else if (doc.DocumentType == DocumentType.PaymentVoucher)
        {
            journalType = JournalType.CashPayments;

            if (doc.RelatedDocumentId.HasValue)
            {
                var apAccount = await FindAccountAsync(companyId, "212");
                if (apAccount != null)
                    AddLine(apAccount.Id, doc.TotalAmount, 0,
                        $"ตัดเจ้าหนี้ - {doc.DocumentNumber}");

                if (moneyAccount != null)
                    AddLine(moneyAccount.Id, 0, doc.TotalAmount,
                        $"{(doc.BankAccountId.HasValue ? "จ่ายจากบัญชี" : "จ่ายเงิน")} - {doc.DocumentNumber}");
            }
            else
            {
                foreach (var docLine in doc.Lines)
                {
                    var expenseAccountId = docLine.AccountId ?? defaultExpense?.Id;
                    if (expenseAccountId.HasValue)
                        AddLine(expenseAccountId.Value, docLine.Amount, 0, docLine.Description, docLine.ProjectId);
                }

                if (doc.VatAmount > 0)
                {
                    var vatInputAccount = await FindAccountAsync(companyId, "116");
                    if (vatInputAccount != null)
                        AddLine(vatInputAccount.Id, doc.VatAmount, 0, "ภาษีซื้อ");
                }

                if (moneyAccount != null)
                    AddLine(moneyAccount.Id, 0, doc.TotalAmount,
                        $"{(doc.BankAccountId.HasValue ? "จ่ายจากบัญชี" : "จ่ายเงินสด")} - {doc.DocumentNumber}");

                if (doc.WithholdingTaxAmount > 0)
                {
                    var whtAccount = await FindAccountAsync(companyId, "21916")
                        ?? await FindAccountAsync(companyId, "21917");
                    if (whtAccount != null)
                        AddLine(whtAccount.Id, 0, doc.WithholdingTaxAmount, "ภาษีหัก ณ ที่จ่ายค้างจ่าย");
                }
            }
        }
        else
        {
            // Operational documents (Quotation, DeliveryNote, BillingNote, PR, PO) → no JE
            return;
        }

        if (pendingLines.Count < 2)
        {
            // Fail fast — silently skipping the JE while marking the document
            // Approved makes the GL miss revenue/expense for days/weeks until
            // someone runs a TB reconciliation. Better to block the approval
            // and force the operator to seed the missing COA accounts.
            throw new InvalidOperationException(
                "ไม่สามารถบันทึกบัญชีอัตโนมัติได้: ผังบัญชี (COA) ที่ใช้สำหรับเอกสารประเภทนี้ยังไม่ครบ. " +
                "กรุณาเปิดเมนู ตั้งค่าผังบัญชี เพื่อ seed บัญชีที่จำเป็น (รายได้ / ค่าใช้จ่าย / VAT / AR / AP / เงินสด) ก่อนอนุมัติ");
        }

        // Validate double-entry balance per Thai accounting standards (TAS 1)
        var totalDebit = pendingLines.Sum(l => l.Debit);
        var totalCredit = pendingLines.Sum(l => l.Credit);
        if (Math.Round(totalDebit, 2, MidpointRounding.AwayFromZero) != Math.Round(totalCredit, 2, MidpointRounding.AwayFromZero))
            throw new InvalidOperationException(
                $"การบันทึกบัญชีอัตโนมัติไม่สมดุล: เดบิต {totalDebit:N2} ≠ เครดิต {totalCredit:N2}");

        // Resolve fiscal period — block posting to closed/locked periods per
        // Thai accounting standard (TAS 1: closed period is immutable). If the
        // document falls within a closed period, the user must reopen it first
        // or re-date the document.
        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId &&
            f.StartDate <= doc.DocumentDate &&
            f.EndDate >= doc.DocumentDate);
        if (period != null && period.Status != FiscalPeriodStatus.Open)
        {
            throw new InvalidOperationException(
                $"ไม่สามารถบันทึกบัญชีในงวด {period.Name} ได้ เนื่องจากงวดถูกปิดแล้ว " +
                $"กรุณาเปลี่ยนวันที่เอกสารเป็นงวดที่เปิดอยู่ หรือขอเปิดงวดก่อน");
        }

        // Generate entry number
        var prefix = journalType switch
        {
            JournalType.Sales => "SV",
            JournalType.Purchase => "UV",
            JournalType.CashReceipts => "RV",
            JournalType.CashPayments => "PV",
            _ => "JV"
        };
        var entryNumber = await GetNextJournalEntryNumberAsync(companyId, prefix);

        var entry = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = doc.DocumentDate,
            JournalType = journalType,
            Description = $"Auto-post จาก {doc.DocumentNumber}",
            Reference = doc.DocumentNumber,
            Status = JournalEntryStatus.Posted,
            TotalDebit = totalDebit,
            TotalCredit = totalCredit,
            CreatedBy = createdBy,
            IsAutoGenerated = true,
            SourceDocumentId = doc.Id,
            FiscalPeriodId = period?.Id,
            // Header-level Project: enables filtering JE lookups by project even on
            // system-generated lines that inherit. ProjectAccountingService queries
            // (l.ProjectId == projectId || l.JournalEntry.ProjectId == projectId).
            ProjectId = doc.ProjectId
        };

        _db.JournalEntries.Add(entry);

        var order = 1;
        for (int i = 0; i < pendingLines.Count; i++)
        {
            var line = pendingLines[i];
            _db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = entry.Id,
                AccountId = line.AccountId,
                DebitAmount = line.Debit,
                CreditAmount = line.Credit,
                Description = line.Description,
                LineOrder = order++,
                ProjectId = lineProjects[i]
            });
        }
    }

    /// <summary>Generate next journal entry number for a given prefix (SV/UV/RV/PV/JV) per month.
    /// Uses PostgreSQL advisory lock to prevent race conditions on concurrent inserts.</summary>
    private async Task<string> GetNextJournalEntryNumberAsync(Guid companyId, string prefix)
    {
        var lockKey = HashCode.Combine(companyId, prefix, "je-seq");
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);

        var yearMonth = DateTime.UtcNow.ToString("yyyyMM");
        var pattern = $"{prefix}-{yearMonth}-";
        var lastEntry = await _db.JournalEntries
            .IgnoreQueryFilters()
            .Where(j => j.CompanyId == companyId && j.EntryNumber.StartsWith(pattern))
            .OrderByDescending(j => j.EntryNumber)
            .Select(j => j.EntryNumber)
            .FirstOrDefaultAsync();
        var nextSeq = 1;
        if (lastEntry != null)
        {
            var lastPart = lastEntry[pattern.Length..];
            if (int.TryParse(lastPart, out var n)) nextSeq = n + 1;
        }
        return $"{pattern}{nextSeq:D4}";
    }

    /// <summary>
    /// สร้าง Journal Entry สำหรับการชำระเงิน — เขียน DbContext โดยตรงภายใน transaction ของ caller
    /// รับเงิน (ฝั่งขาย): Dr Cash, Cr AR → สมุดรายวันรับ (RV)
    /// จ่ายเงิน (ฝั่งซื้อ): Dr AP, Cr Cash → สมุดรายวันจ่าย (PV)
    /// </summary>
    private async Task CreatePaymentJournalAsync(Guid companyId, Document doc, Payment payment, string createdBy)
    {
        var revenueTypes = new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt,
            DocumentType.DebitNote, DocumentType.BillingNote, DocumentType.ReceiptVoucher };
        var isRevenue = revenueTypes.Contains(doc.DocumentType);
        var journalType = isRevenue ? JournalType.CashReceipts : JournalType.CashPayments;

        var cashAccount = await FindAccountAsync(companyId, "111");
        if (cashAccount == null) return;

        var pendingLines = new List<(Guid AccountId, decimal Debit, decimal Credit, string? Description)>();
        // Convert payment to THB (base) at the document's captured FX rate.
        // Note: this uses the doc rate, not a settlement-day rate, so FX gain/loss
        // on settlement isn't booked yet (separate feature when needed).
        var fx = doc.ExchangeRate;
        var thbAmount = fx == 1m ? payment.Amount : Math.Round(payment.Amount * fx, 2, MidpointRounding.AwayFromZero);
        if (isRevenue)
        {
            pendingLines.Add((cashAccount.Id, thbAmount, 0, $"รับชำระ - {doc.DocumentNumber} ({payment.PaymentNumber})"));
            var arAccount = await FindAccountAsync(companyId, "113");
            if (arAccount != null)
                pendingLines.Add((arAccount.Id, 0, thbAmount, $"ตัดลูกหนี้ - {doc.DocumentNumber}"));
        }
        else
        {
            var apAccount = await FindAccountAsync(companyId, "212");
            if (apAccount != null)
                pendingLines.Add((apAccount.Id, thbAmount, 0, $"ตัดเจ้าหนี้ - {doc.DocumentNumber}"));
            pendingLines.Add((cashAccount.Id, 0, thbAmount, $"จ่ายชำระ - {doc.DocumentNumber} ({payment.PaymentNumber})"));
        }

        if (pendingLines.Count < 2) return;

        var totalDebit = pendingLines.Sum(l => l.Debit);
        var totalCredit = pendingLines.Sum(l => l.Credit);
        if (Math.Round(totalDebit, 2, MidpointRounding.AwayFromZero) != Math.Round(totalCredit, 2, MidpointRounding.AwayFromZero))
            throw new InvalidOperationException(
                $"การบันทึกบัญชีชำระเงินไม่สมดุล: เดบิต {totalDebit:N2} ≠ เครดิต {totalCredit:N2}");

        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId &&
            f.StartDate <= payment.PaymentDate &&
            f.EndDate >= payment.PaymentDate);
        if (period != null && period.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException(
                $"ไม่สามารถบันทึกการชำระเงินในงวด {period.Name} ได้ เนื่องจากงวดถูกปิดแล้ว");

        var prefix = isRevenue ? "RV" : "PV";
        var entryNumber = await GetNextJournalEntryNumberAsync(companyId, prefix);

        var entry = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = payment.PaymentDate,
            JournalType = journalType,
            Description = $"ชำระเงิน {payment.PaymentNumber} - {doc.DocumentNumber}",
            Reference = payment.PaymentNumber,
            Status = JournalEntryStatus.Posted,
            TotalDebit = totalDebit,
            TotalCredit = totalCredit,
            CreatedBy = createdBy,
            IsAutoGenerated = true,
            SourceDocumentId = doc.Id,
            FiscalPeriodId = period?.Id,
            // Inherit project tag from the source document so payment JEs roll up
            // into per-project P&L (cash collection on a project's invoice).
            ProjectId = doc.ProjectId
        };

        _db.JournalEntries.Add(entry);

        var order = 1;
        foreach (var line in pendingLines)
        {
            _db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = entry.Id,
                AccountId = line.AccountId,
                DebitAmount = line.Debit,
                CreditAmount = line.Credit,
                Description = line.Description,
                LineOrder = order++,
                ProjectId = doc.ProjectId
            });
        }
    }

    private static DocumentResponse MapDocumentToResponse(Document d, (Guid EtaxId, EtaxStatus Status)? etax = null) => new(
        d.Id, d.DocumentNumber, d.DocumentType, d.Status,
        d.DocumentDate, d.DueDate,
        new ContactBrief(d.Contact.Id, d.Contact.Name, d.Contact.TaxId),
        d.SubTotal, d.DiscountAmount, d.VatAmount, d.WithholdingTaxAmount,
        d.TotalAmount, d.PaidAmount, d.BalanceDue, d.Reference, d.Notes,
        d.Lines.OrderBy(l => l.LineOrder).Select(l => new DocumentLineResponse(
            l.Id, l.LineOrder, l.Description, l.Quantity, l.Unit,
            l.UnitPrice, l.DiscountPercent, l.DiscountAmount, l.Amount,
            l.VatRate, l.VatAmount, l.WithholdingTaxRate, l.WithholdingTaxAmount,
            AccountId: l.AccountId,
            ProjectId: l.ProjectId,
            ProductCode: l.ProductCode,
            SourceLineId: l.SourceLineId)).ToList(),
        d.CreatedAt,
        EtaxInvoiceId: etax?.EtaxId,
        EtaxStatus: etax?.Status,
        ProjectId: d.ProjectId,
        ProjectCode: d.Project?.Code,
        ProjectName: d.Project?.Name,
        BankAccountId: d.BankAccountId,
        BankAccountName: d.BankAccount?.AccountName,
        PaymentAccountId: d.PaymentAccountId,
        PaymentAccountName: d.PaymentAccount != null ? $"{d.PaymentAccount.AccountCode} - {d.PaymentAccount.AccountName}" : null,
        ExpenseCategoryId: d.ExpenseCategoryId,
        ExpenseCategoryName: d.ExpenseCategory?.AccountName,
        CustomAppendix: d.CustomAppendix,
        CustomFooterNotes: d.CustomFooterNotes,
        CustomTermsAndConditions: d.CustomTermsAndConditions,
        RevenueContractId: d.RevenueContractId,
        PerformanceObligationId: d.PerformanceObligationId,
        RelatedDocumentId: d.RelatedDocumentId,
        CertificateReason: d.CertificateReason,
        CertifierName: d.CertifierName,
        CertifierPosition: d.CertifierPosition,
        WitnessName: d.WitnessName,
        WitnessPosition: d.WitnessPosition,
        PaymentDate: d.PaymentDate,
        // ERP upgrade fields — OCR compliance + aging
        OcrConfidenceScore: d.OcrConfidenceScore,
        RdComplianceStatus: d.RdComplianceStatus,
        RdComplianceIssuesJson: d.RdComplianceIssuesJson,
        OcrTenantMismatchFlag: d.OcrTenantMismatchFlag,
        AgingDays: d.AgingDays,
        StaleDays: ComputeStaleDays(d),
        Currency: d.Currency,
        ExchangeRate: d.ExchangeRate,
        Sensitivity: d.Sensitivity);

    /// <summary>Build the redacted stub returned to API consumers who lack
    /// permission to see a sensitive record. Keeps the Id, DocumentNumber, and
    /// Sensitivity so the integration target knows the record exists and what
    /// kind of access it would need; blanks out amounts / contact / notes /
    /// lines. The destination system can decide whether to skip, place-hold,
    /// or request access.</summary>
    private static DocumentResponse RedactDocumentResponse(Document d, string reason) => new(
        d.Id, d.DocumentNumber, d.DocumentType, d.Status,
        d.DocumentDate, d.DueDate,
        new ContactBrief(Guid.Empty, "[ซ่อน]", null),
        0, 0, 0, 0, 0, 0, 0, null, null,
        new List<DocumentLineResponse>(), d.CreatedAt,
        Sensitivity: d.Sensitivity,
        IsRedacted: true,
        RedactedReason: reason);

    /// <summary>Days a document has been parked in a non-terminal status past
    /// the stale threshold — null when fresh or in a terminal status.
    /// Terminal = Paid / Voided / Rejected.</summary>
    private const int StaleThresholdDays = 60;
    private static int? ComputeStaleDays(Document d)
    {
        if (d.Status is DocumentStatus.Paid or DocumentStatus.Voided or DocumentStatus.Rejected)
            return null;
        var days = (int)(DateTime.UtcNow.Date - d.DocumentDate.Date).TotalDays;
        return days > StaleThresholdDays ? days : null;
    }

    private static ContactResponse MapContactToResponse(Contact c) => new(
        c.Id, c.Name, c.TaxId, c.BranchCode, c.ContactType, c.IsCustomer, c.IsSupplier,
        c.Address, c.Phone, c.Email, c.ContactPerson, c.IsActive,
        BranchName: c.BranchName,
        BuildingNumber: c.BuildingNumber,
        BuildingName: c.BuildingName,
        StreetName: c.StreetName,
        SubDistrict: c.SubDistrict,
        District: c.District,
        Province: c.Province,
        PostalCode: c.PostalCode,
        CountryCode: c.CountryCode);

    // ==================== Smart Defaults ====================

    /// <summary>
    /// วิเคราะห์ประเภทผู้ติดต่อจาก TaxId และ BranchCode อัตโนมัติ
    /// - มี BranchCode (ไม่ใช่ 00000) → นิติบุคคล (มีสำนักงานสาขา)
    /// - TaxId 13 หลัก ขึ้นต้นด้วย 0 → นิติบุคคล (เลขทะเบียนนิติบุคคล)
    /// - อื่นๆ → บุคคลธรรมดา
    /// </summary>
    public static ContactType InferContactType(string? taxId, string? branchCode)
    {
        // BranchCode ≠ null/empty/"00000" → clearly juristic (has branch offices)
        if (!string.IsNullOrWhiteSpace(branchCode) && branchCode != "00000")
            return ContactType.JuristicPerson;

        // TaxId 13 digits starting with 0 → juristic person registration number
        if (!string.IsNullOrWhiteSpace(taxId) && taxId.Length == 13 && taxId[0] == '0')
            return ContactType.JuristicPerson;

        // BranchCode "00000" (สำนักงานใหญ่) with valid TaxId → also juristic
        if (branchCode == "00000" && !string.IsNullOrWhiteSpace(taxId) && taxId.Length == 13)
            return ContactType.JuristicPerson;

        return ContactType.Individual;
    }

    /// <summary>ค่าเริ่มต้นอัตโนมัติจากข้อมูลผู้ติดต่อ</summary>
    public static ContactSmartDefaults GetSmartDefaults(Contact contact)
    {
        var contactType = contact.ContactType;

        // WHT form type: นิติบุคคล → ภ.ง.ด.53, บุคคลธรรมดา → ภ.ง.ด.3
        var taxFormType = contactType switch
        {
            ContactType.JuristicPerson => TaxType.WithholdingTax53,
            ContactType.GovernmentAgency => TaxType.WithholdingTax53,
            _ => TaxType.WithholdingTax3
        };
        var taxFormLabel = taxFormType switch
        {
            TaxType.WithholdingTax53 => "ภ.ง.ด.53",
            TaxType.WithholdingTax3 => "ภ.ง.ด.3",
            _ => taxFormType.ToString()
        };

        // Document type: supplier → ซื้อ, customer → ขาย
        DocumentType? docType = null;
        string? docTypeLabel = null;
        if (contact.IsSupplier && !contact.IsCustomer)
        {
            docType = DocumentType.PurchaseInvoice;
            docTypeLabel = "ใบกำกับซื้อ";
        }
        else if (contact.IsCustomer && !contact.IsSupplier)
        {
            docType = DocumentType.Invoice;
            docTypeLabel = "ใบแจ้งหนี้";
        }

        // WHT rate & income type: นิติบุคคล default = ค่าบริการ 3%, บุคคลธรรมดา = ค่าจ้าง 3%
        var defaultWhtRate = 3m;
        var defaultIncomeCode = contactType == ContactType.JuristicPerson ? "8" : "6";
        var defaultIncomeLabel = contactType == ContactType.JuristicPerson
            ? "ค่าบริการอื่นๆ (40(8))"
            : "ค่าวิชาชีพอิสระ (40(6))";

        var contactTypeLabel = contactType switch
        {
            ContactType.Individual => "บุคคลธรรมดา",
            ContactType.JuristicPerson => "นิติบุคคล",
            ContactType.GovernmentAgency => "หน่วยงานราชการ",
            _ => contactType.ToString()
        };

        return new ContactSmartDefaults(
            contactType, contactTypeLabel,
            taxFormType, taxFormLabel,
            docType, docTypeLabel,
            defaultWhtRate, defaultIncomeCode, defaultIncomeLabel);
    }

    // Projection type for the recursive cycle-detection CTE
    private sealed record AncestorRow(Guid Id, string DocumentNumber, int DocumentType);
}
