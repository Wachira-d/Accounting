using Accounting.Data;
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

    public DocumentService(AccountingDbContext db, IAccountingService accountingService,
        ISubscriptionService subscriptionService, IWithholdingTaxCertService whtService,
        IEtaxInvoiceService etaxService, ILogger<DocumentService> logger)
    {
        _db = db;
        _accountingService = accountingService;
        _subscriptionService = subscriptionService;
        _whtService = whtService;
        _etaxService = etaxService;
        _logger = logger;
    }

    public async Task<DocumentResponse> CreateDocumentAsync(Guid companyId, CreateDocumentRequest request, string createdBy)
    {
        // Check usage limit
        if (!await _subscriptionService.CheckUsageLimitAsync(companyId, "document"))
            throw new InvalidOperationException("เกินจำนวนเอกสารที่อนุญาตต่อเดือน");

        // Check feature access
        if (!await _subscriptionService.CheckFeatureAccessAsync(companyId, FeatureFlags.DocumentEngine))
            throw new InvalidOperationException("ไม่มีสิทธิ์ใช้ระบบเอกสาร");

        // Validate ContactId exists in company contacts
        var contactExists = await _db.Contacts.AnyAsync(c => c.Id == request.ContactId && c.CompanyId == companyId);
        if (!contactExists)
            throw new InvalidOperationException("ไม่พบผู้ติดต่อในบริษัทนี้");

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
        }

        var prefix = request.DocumentType switch
        {
            DocumentType.Quotation => "QT",
            DocumentType.Invoice => "INV",
            DocumentType.Receipt => "REC",
            DocumentType.TaxInvoice => "TIV",
            DocumentType.DebitNote => "DN",
            DocumentType.CreditNote => "CN",
            DocumentType.DeliveryNote => "DLV",
            DocumentType.BillingNote => "BN",
            DocumentType.ReceiptVoucher => "RV",
            DocumentType.PurchaseRequisition => "PR",
            DocumentType.PurchaseOrder => "PO",
            DocumentType.PurchaseInvoice => "PI",
            DocumentType.Expense => "EXP",
            DocumentType.PaymentVoucher => "PV",
            _ => "DOC"
        };

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var yearMonth = DateTime.UtcNow.ToString("yyyyMM");
            var docPrefix = $"{prefix}-{yearMonth}-";
            var maxNumber = await _db.Documents
                .IgnoreQueryFilters()
                .Where(d => d.CompanyId == companyId && d.DocumentNumber.StartsWith(docPrefix))
                .Select(d => d.DocumentNumber)
                .MaxAsync() as string;
            var nextSeq = 1;
            if (maxNumber != null)
            {
                var lastPart = maxNumber.Substring(docPrefix.Length);
                if (int.TryParse(lastPart, out var parsed)) nextSeq = parsed + 1;
            }
            var docNumber = $"{docPrefix}{nextSeq:D4}";

            var doc = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = request.DocumentType,
                DocumentDate = request.DocumentDate,
                DueDate = request.DueDate,
                ContactId = request.ContactId,
                Reference = request.Reference,
                Notes = request.Notes,
                ProjectId = request.ProjectId,
                CreatedBy = createdBy
            };

            _db.Documents.Add(doc);

            decimal subTotal = 0, totalDiscount = 0, totalVat = 0, totalWht = 0;
            var order = 1;

            foreach (var line in request.Lines)
            {
                var lineAmount = Math.Round(line.Quantity * line.UnitPrice, 2);
                var discountAmt = Math.Round(lineAmount * line.DiscountPercent / 100, 2);
                var afterDiscount = lineAmount - discountAmt;
                var vatAmt = line.VatRate > 0 ? Math.Round(afterDiscount * line.VatRate / 100, 2) : 0m;
                var whtAmt = Math.Round(afterDiscount * line.WithholdingTaxRate / 100, 2);

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
                    ProjectId = line.ProjectId
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
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var etax = await GetLatestEtaxAsync(companyId, new[] { documentId });
        return MapDocumentToResponse(doc, etax.GetValueOrDefault(documentId));
    }

    public async Task<PagedResponse<DocumentResponse>> GetDocumentsAsync(Guid companyId, DocumentType? type, PagedRequest request, Guid? projectId = null)
    {
        var query = _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines)
            .Include(d => d.Project)
            .Where(d => d.CompanyId == companyId);

        if (type.HasValue)
            query = query.Where(d => d.DocumentType == type.Value);

        if (projectId.HasValue)
            query = query.Where(d => d.ProjectId == projectId.Value
                || d.Lines.Any(l => l.ProjectId == projectId.Value));

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

        // Project re-assignment (only allowed while Draft, which is enforced above)
        if (request.ProjectId.HasValue)
        {
            var projectOk = await _db.Projects.AnyAsync(p =>
                p.Id == request.ProjectId.Value && p.CompanyId == companyId);
            if (!projectOk)
                throw new InvalidOperationException("ไม่พบโครงการในบริษัทนี้");
            doc.ProjectId = request.ProjectId.Value;
        }

        if (request.Lines != null)
        {
            _db.DocumentLines.RemoveRange(doc.Lines);

            decimal subTotal = 0, totalDiscount = 0, totalVat = 0, totalWht = 0;
            var order = 1;

            foreach (var line in request.Lines)
            {
                var lineAmount = Math.Round(line.Quantity * line.UnitPrice, 2);
                var discountAmt = Math.Round(lineAmount * line.DiscountPercent / 100, 2);
                var afterDiscount = lineAmount - discountAmt;
                var vatAmt = line.VatRate > 0 ? Math.Round(afterDiscount * line.VatRate / 100, 2) : 0m;
                var whtAmt = Math.Round(afterDiscount * line.WithholdingTaxRate / 100, 2);

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
                    AccountId = line.AccountId
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

        // Idempotency guard — if a Posted JE already exists for this document,
        // don't create a duplicate. Per Thai accounting standard, voiding requires
        // an explicit reversal entry (handled in VoidDocumentAsync), not a re-post.
        var hasExistingJournal = await _db.JournalEntries.AnyAsync(j =>
            j.SourceDocumentId == documentId
            && j.CompanyId == companyId
            && j.Status == JournalEntryStatus.Posted);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                doc.Status = DocumentStatus.Approved;
                doc.UpdatedBy = approvedBy;
                doc.UpdatedAt = DateTime.UtcNow;

                // Auto-post to journal for document types that affect accounting.
                // Operational-only docs (Quotation, DeliveryNote, BillingNote, PR, PO)
                // are intentionally excluded — they don't create accounting entries.
                var autoPostTypes = new[] {
                    DocumentType.Invoice, DocumentType.TaxInvoice,          // ใบแจ้งหนี้/ใบกำกับภาษี → SV
                    DocumentType.DebitNote, DocumentType.CreditNote,        // ใบเพิ่มหนี้/ใบลดหนี้ → SV (adjustment)
                    DocumentType.PurchaseInvoice, DocumentType.Expense,     // ใบแจ้งหนี้ซื้อ/ค่าใช้จ่าย → UV
                    DocumentType.Receipt, DocumentType.ReceiptVoucher,      // ใบเสร็จ/ใบสำคัญรับ → RV
                    DocumentType.PaymentVoucher,                            // ใบสำคัญจ่าย → PV
                };
                if (!hasExistingJournal && autoPostTypes.Contains(doc.DocumentType))
                {
                    await AutoPostToJournalAsync(companyId, doc, approvedBy);
                }

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        // Best-effort auto-generate e-Tax record for eligible types when the
        // company has e-Tax enabled. Runs OUTSIDE the approval transaction so
        // an e-Tax failure (cert not configured, RD API down, etc.) doesn't
        // roll back the approval. Failures are logged; the user can still
        // generate the e-Tax manually from the detail modal.
        await TryAutoGenerateEtaxAsync(companyId, doc);

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

    public async Task VoidDocumentAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (doc.Status == DocumentStatus.Voided)
            throw new InvalidOperationException("เอกสารนี้ถูกยกเลิกแล้ว");
        if (doc.Status == DocumentStatus.Paid || doc.Status == DocumentStatus.PartiallyPaid)
            throw new InvalidOperationException("ไม่สามารถยกเลิกเอกสารที่มีการชำระเงินแล้ว กรุณายกเลิกการชำระเงินก่อน");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            doc.Status = DocumentStatus.Voided;
            doc.UpdatedAt = DateTime.UtcNow;

            // Create reversal journal entries for linked journals (Thai standard: reversal, not deletion)
            var linkedJournals = await _db.JournalEntries
                .Include(j => j.Lines)
                .Where(j => j.SourceDocumentId == documentId && j.CompanyId == companyId
                    && j.Status == JournalEntryStatus.Posted)
                .ToListAsync();
            foreach (var je in linkedJournals)
            {
                je.Status = JournalEntryStatus.Voided;
                je.UpdatedAt = DateTime.UtcNow;

                // Create reversal entry (swap Dr↔Cr)
                var reversalEntry = new JournalEntry
                {
                    CompanyId = companyId,
                    EntryNumber = $"{je.EntryNumber}-REV",
                    EntryDate = DateTime.UtcNow,
                    Description = $"กลับรายการ - {je.Description}",
                    Reference = je.Reference,
                    JournalType = je.JournalType,
                    TotalDebit = je.TotalCredit,
                    TotalCredit = je.TotalDebit,
                    Status = JournalEntryStatus.Posted,
                    IsAutoGenerated = true,
                    SourceDocumentId = documentId,
                    FiscalPeriodId = je.FiscalPeriodId
                };
                foreach (var line in je.Lines)
                {
                    reversalEntry.Lines.Add(new JournalEntryLine
                    {
                        AccountId = line.AccountId,
                        DebitAmount = line.CreditAmount,
                        CreditAmount = line.DebitAmount,
                        Description = $"กลับรายการ - {line.Description}",
                        LineOrder = line.LineOrder
                    });
                }
                _db.JournalEntries.Add(reversalEntry);
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<DocumentResponse> ConvertDocumentAsync(Guid companyId, Guid documentId, DocumentType targetType, string createdBy)
    {
        var source = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var lines = source.Lines.Select(l => new DocumentLineRequest(
            l.Description, l.Quantity, l.Unit, l.UnitPrice,
            l.DiscountPercent, l.VatRate, l.WithholdingTaxRate, l.AccountId,
            ProjectId: l.ProjectId)).ToList();

        var newDoc = await CreateDocumentAsync(companyId, new CreateDocumentRequest(
            targetType, DateTime.UtcNow, source.DueDate, source.ContactId,
            source.DocumentNumber, source.Notes, lines,
            ProjectId: source.ProjectId), createdBy);

        // Link
        var created = await _db.Documents.FindAsync(newDoc.Id);
        if (created != null)
        {
            created.RelatedDocumentId = documentId;
            await _db.SaveChangesAsync();
        }

        return newDoc;
    }

    // ==================== Contacts ====================

    public async Task<ContactResponse> CreateContactAsync(Guid companyId, CreateContactRequest request)
    {
        var contact = new Contact
        {
            CompanyId = companyId,
            Name = request.Name,
            TaxId = request.TaxId,
            BranchCode = request.BranchCode,
            ContactType = request.ContactType ?? InferContactType(request.TaxId, request.BranchCode),
            IsCustomer = request.IsCustomer,
            IsSupplier = request.IsSupplier,
            Address = request.Address,
            Phone = request.Phone,
            Email = request.Email,
            ContactPerson = request.ContactPerson
        };

        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();

        return MapContactToResponse(contact);
    }

    public async Task<List<ContactResponse>> GetContactsAsync(Guid companyId, bool? isCustomer = null, bool? isSupplier = null)
    {
        var query = _db.Contacts.Where(c => c.CompanyId == companyId);
        if (isCustomer.HasValue) query = query.Where(c => c.IsCustomer == isCustomer.Value);
        if (isSupplier.HasValue) query = query.Where(c => c.IsSupplier == isSupplier.Value);

        var contacts = await query.OrderBy(c => c.Name).ToListAsync();
        return contacts.Select(MapContactToResponse).ToList();
    }

    public async Task<ContactResponse> UpdateContactAsync(Guid companyId, Guid contactId, UpdateContactRequest request)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");

        if (request.Name != null) contact.Name = request.Name;
        if (request.TaxId != null) contact.TaxId = request.TaxId;
        if (request.BranchCode != null) contact.BranchCode = request.BranchCode;
        if (request.ContactType.HasValue) contact.ContactType = request.ContactType.Value;
        else if (request.TaxId != null || request.BranchCode != null)
            contact.ContactType = InferContactType(request.TaxId ?? contact.TaxId, request.BranchCode ?? contact.BranchCode);
        if (request.IsCustomer.HasValue) contact.IsCustomer = request.IsCustomer.Value;
        if (request.IsSupplier.HasValue) contact.IsSupplier = request.IsSupplier.Value;
        if (request.Address != null) contact.Address = request.Address;
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

        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == request.DocumentId && d.CompanyId == companyId)
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

            // Auto-generate WHT cert for purchase documents with WHT on first payment
            if (doc.WithholdingTaxAmount > 0
                && (doc.DocumentType == DocumentType.PurchaseInvoice
                    || doc.DocumentType == DocumentType.Expense
                    || doc.DocumentType == DocumentType.PaymentVoucher))
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

            return new PaymentResponse(
                payment.Id, payment.PaymentNumber, payment.DocumentId,
                payment.PaymentDate, payment.Amount, payment.PaymentMethod,
                payment.Reference, payment.BankAccount, payment.CreatedAt);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<List<PaymentResponse>> GetPaymentsAsync(Guid companyId, Guid? documentId = null)
    {
        var query = _db.Payments.Where(p => p.CompanyId == companyId);
        if (documentId.HasValue) query = query.Where(p => p.DocumentId == documentId.Value);

        var payments = await query.OrderByDescending(p => p.PaymentDate).ToListAsync();
        return payments.Select(p => new PaymentResponse(
            p.Id, p.PaymentNumber, p.DocumentId,
            p.PaymentDate, p.Amount, p.PaymentMethod,
            p.Reference, p.BankAccount, p.CreatedAt)).ToList();
    }

    // ==================== Private ====================

    /// <summary>
    /// ค้นหาบัญชีจากรหัส — exact match ก่อน แล้ว prefix match (level 4)
    /// เช่น "113" จะ match "11310" (ลูกหนี้การค้า)
    /// </summary>
    private async Task<ChartOfAccount?> FindAccountAsync(Guid companyId, string codePrefix)
    {
        // Try exact match first, then prefix match (first child account)
        return await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == codePrefix)
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith(codePrefix) && a.Level >= 4)
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
        var defaultExpense = await FindAccountAsync(companyId, "51110")
            ?? await FindAccountAsync(companyId, "52110")
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountType == AccountType.Expense && a.Level >= 4 && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();

        var pendingLines = new List<(Guid AccountId, decimal Debit, decimal Credit, string? Description)>();
        JournalType journalType;
        // ProjectId map: each pendingLines index → resolved project. System-generated
        // lines (AR/VAT/Cash/AP/WHT) inherit doc.ProjectId; per-line revenue/expense
        // can override via docLine.ProjectId.
        var lineProjects = new List<Guid?>();
        void AddLine(Guid accountId, decimal debit, decimal credit, string? desc, Guid? proj = null)
        {
            pendingLines.Add((accountId, debit, credit, desc));
            lineProjects.Add(proj ?? doc.ProjectId);
        }

        // ============================================================
        // SALES SIDE: Invoice / TaxInvoice / DebitNote (full sale entry)
        // ============================================================
        if (doc.DocumentType == DocumentType.Invoice
            || doc.DocumentType == DocumentType.TaxInvoice
            || doc.DocumentType == DocumentType.DebitNote)
        {
            journalType = JournalType.Sales;
            var typeLabel = doc.DocumentType == DocumentType.DebitNote ? "ใบเพิ่มหนี้" : "ลูกหนี้การค้า";

            // Dr: ลูกหนี้การค้า (113) — TotalAmount is net of WHT (Gross - WHT)
            var arAccount = await FindAccountAsync(companyId, "113");
            if (arAccount != null)
                AddLine(arAccount.Id, doc.TotalAmount, 0, $"{typeLabel} - {doc.DocumentNumber}");

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
                var vatAccount = await FindAccountAsync(companyId, "21911");
                if (vatAccount != null)
                    AddLine(vatAccount.Id, 0, doc.VatAmount, "ภาษีขาย");
            }
        }
        // ============================================================
        // SALES REVERSAL: CreditNote (ใบลดหนี้ §82/10)
        // Reverses signs of the original sale — reduces AR, revenue, VAT
        // ============================================================
        else if (doc.DocumentType == DocumentType.CreditNote)
        {
            journalType = JournalType.Sales;

            // Cr: ลูกหนี้การค้า (113) — reverse direction
            var arAccount = await FindAccountAsync(companyId, "113");
            if (arAccount != null)
                AddLine(arAccount.Id, 0, doc.TotalAmount, $"ใบลดหนี้ - {doc.DocumentNumber}");

            // Cr: ภาษีถูกหัก ณ ที่จ่าย — reverse if WHT was claimed on original sale
            if (doc.WithholdingTaxAmount > 0)
            {
                var whtAccount = await FindAccountAsync(companyId, "11910");
                if (whtAccount != null)
                    AddLine(whtAccount.Id, 0, doc.WithholdingTaxAmount, "กลับรายการ ภาษีถูกหัก ณ ที่จ่าย");
            }

            // Dr: รายได้ — reverse direction
            foreach (var docLine in doc.Lines)
            {
                var revenueAccountId = docLine.AccountId ?? defaultRevenue?.Id;
                if (revenueAccountId.HasValue)
                    AddLine(revenueAccountId.Value, docLine.Amount, 0, $"กลับรายการ - {docLine.Description}", docLine.ProjectId);
            }

            // Dr: ภาษีขาย — reverse direction (reduces VAT payable)
            if (doc.VatAmount > 0)
            {
                var vatAccount = await FindAccountAsync(companyId, "21911");
                if (vatAccount != null)
                    AddLine(vatAccount.Id, doc.VatAmount, 0, "กลับรายการ ภาษีขาย");
            }
        }
        // ============================================================
        // PURCHASE SIDE: PurchaseInvoice / Expense (on credit)
        // ============================================================
        else if (doc.DocumentType == DocumentType.PurchaseInvoice
                 || doc.DocumentType == DocumentType.Expense)
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
                var vatInputAccount = await FindAccountAsync(companyId, "116");
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
                    AddLine(whtAccount.Id, 0, doc.WithholdingTaxAmount, "ภาษีหัก ณ ที่จ่ายค้างจ่าย");
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
            var cashAccount = await FindAccountAsync(companyId, "111");

            if (doc.RelatedDocumentId.HasValue)
            {
                // Collection against an existing Invoice's AR.
                // The original Invoice already booked Dr WHT-Asset (Policy A: WHT
                // claimed at issuance). Don't book WHT again here — just clear AR.
                if (cashAccount != null)
                    AddLine(cashAccount.Id, doc.TotalAmount, 0, $"รับชำระ - {doc.DocumentNumber}");

                var arAccount = await FindAccountAsync(companyId, "113");
                if (arAccount != null)
                    AddLine(arAccount.Id, 0, doc.TotalAmount,
                        $"ตัดลูกหนี้ - {doc.DocumentNumber}");
            }
            else
            {
                // Direct cash sale (ใบเสร็จรับเงิน/ใบกำกับภาษี): no prior AR
                if (cashAccount != null)
                    AddLine(cashAccount.Id, doc.TotalAmount, 0, $"รับเงินสด - {doc.DocumentNumber}");

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
            var cashAccount = await FindAccountAsync(companyId, "111");

            if (doc.RelatedDocumentId.HasValue)
            {
                // Settlement of existing AP. The original PurchaseInvoice already
                // credited WHT-Payable; settling it is a separate event (when filing
                // ภ.ง.ด.3/53 with Revenue Dept). Just clear AP and pay cash here.
                var apAccount = await FindAccountAsync(companyId, "212");
                if (apAccount != null)
                    AddLine(apAccount.Id, doc.TotalAmount, 0,
                        $"ตัดเจ้าหนี้ - {doc.DocumentNumber}");

                if (cashAccount != null)
                    AddLine(cashAccount.Id, 0, doc.TotalAmount, $"จ่ายเงิน - {doc.DocumentNumber}");
            }
            else
            {
                // Direct cash purchase (no prior AP)
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

                if (cashAccount != null)
                    AddLine(cashAccount.Id, 0, doc.TotalAmount, $"จ่ายเงินสด - {doc.DocumentNumber}");

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
            return; // ผังบัญชียังไม่ได้ seed — ไม่บันทึก แทนที่จะ throw เพื่อไม่บล็อกการอนุมัติ

        // Validate double-entry balance per Thai accounting standards (TAS 1)
        var totalDebit = pendingLines.Sum(l => l.Debit);
        var totalCredit = pendingLines.Sum(l => l.Credit);
        if (Math.Round(totalDebit, 2) != Math.Round(totalCredit, 2))
            throw new InvalidOperationException(
                $"การบันทึกบัญชีอัตโนมัติไม่สมดุล: เดบิต {totalDebit:N2} ≠ เครดิต {totalCredit:N2}");

        // Resolve fiscal period
        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId &&
            f.StartDate <= doc.DocumentDate &&
            f.EndDate >= doc.DocumentDate &&
            f.Status == FiscalPeriodStatus.Open);

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

    /// <summary>Generate next journal entry number for a given prefix (SV/UV/RV/PV/JV) per month.</summary>
    private async Task<string> GetNextJournalEntryNumberAsync(Guid companyId, string prefix)
    {
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
        if (isRevenue)
        {
            pendingLines.Add((cashAccount.Id, payment.Amount, 0, $"รับชำระ - {doc.DocumentNumber} ({payment.PaymentNumber})"));
            var arAccount = await FindAccountAsync(companyId, "113");
            if (arAccount != null)
                pendingLines.Add((arAccount.Id, 0, payment.Amount, $"ตัดลูกหนี้ - {doc.DocumentNumber}"));
        }
        else
        {
            var apAccount = await FindAccountAsync(companyId, "212");
            if (apAccount != null)
                pendingLines.Add((apAccount.Id, payment.Amount, 0, $"ตัดเจ้าหนี้ - {doc.DocumentNumber}"));
            pendingLines.Add((cashAccount.Id, 0, payment.Amount, $"จ่ายชำระ - {doc.DocumentNumber} ({payment.PaymentNumber})"));
        }

        if (pendingLines.Count < 2) return;

        var totalDebit = pendingLines.Sum(l => l.Debit);
        var totalCredit = pendingLines.Sum(l => l.Credit);
        if (Math.Round(totalDebit, 2) != Math.Round(totalCredit, 2))
            throw new InvalidOperationException(
                $"การบันทึกบัญชีชำระเงินไม่สมดุล: เดบิต {totalDebit:N2} ≠ เครดิต {totalCredit:N2}");

        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId &&
            f.StartDate <= payment.PaymentDate &&
            f.EndDate >= payment.PaymentDate &&
            f.Status == FiscalPeriodStatus.Open);

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
        d.TotalAmount, d.PaidAmount, d.BalanceDue, d.Notes,
        d.Lines.OrderBy(l => l.LineOrder).Select(l => new DocumentLineResponse(
            l.Id, l.LineOrder, l.Description, l.Quantity, l.Unit,
            l.UnitPrice, l.DiscountPercent, l.DiscountAmount, l.Amount,
            l.VatRate, l.VatAmount, l.WithholdingTaxRate, l.WithholdingTaxAmount,
            ProjectId: l.ProjectId)).ToList(),
        d.CreatedAt,
        EtaxInvoiceId: etax?.EtaxId,
        EtaxStatus: etax?.Status,
        ProjectId: d.ProjectId,
        ProjectCode: d.Project?.Code,
        ProjectName: d.Project?.Name);

    private static ContactResponse MapContactToResponse(Contact c) => new(
        c.Id, c.Name, c.TaxId, c.BranchCode, c.ContactType, c.IsCustomer, c.IsSupplier,
        c.Address, c.Phone, c.Email, c.ContactPerson, c.IsActive);

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
}
