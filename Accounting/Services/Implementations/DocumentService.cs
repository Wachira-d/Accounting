using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class DocumentService : IDocumentService
{
    private readonly AccountingDbContext _db;
    private readonly IAccountingService _accountingService;
    private readonly ISubscriptionService _subscriptionService;

    public DocumentService(AccountingDbContext db, IAccountingService accountingService, ISubscriptionService subscriptionService)
    {
        _db = db;
        _accountingService = accountingService;
        _subscriptionService = subscriptionService;
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
            _ => "DOC"
        };

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var count = await _db.Documents.CountAsync(d => d.CompanyId == companyId && d.DocumentType == request.DocumentType);
            var docNumber = $"{prefix}-{DateTime.UtcNow:yyyyMM}-{(count + 1):D4}";

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
                CreatedBy = createdBy
            };

            _db.Documents.Add(doc);

            decimal subTotal = 0, totalDiscount = 0, totalVat = 0, totalWht = 0;
            var order = 1;

            foreach (var line in request.Lines)
            {
                var lineAmount = line.Quantity * line.UnitPrice;
                var discountAmt = lineAmount * line.DiscountPercent / 100;
                var afterDiscount = lineAmount - discountAmt;
                var vatAmt = afterDiscount * line.VatRate / 100;
                var whtAmt = afterDiscount * line.WithholdingTaxRate / 100;

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
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        return MapDocumentToResponse(doc);
    }

    public async Task<PagedResponse<DocumentResponse>> GetDocumentsAsync(Guid companyId, DocumentType? type, PagedRequest request)
    {
        var query = _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines)
            .Where(d => d.CompanyId == companyId);

        if (type.HasValue)
            query = query.Where(d => d.DocumentType == type.Value);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(d => d.DocumentNumber.Contains(request.Search) || d.Contact.Name.Contains(request.Search));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(d => d.DocumentDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<DocumentResponse>(
            items.Select(MapDocumentToResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
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

        if (request.Lines != null)
        {
            _db.DocumentLines.RemoveRange(doc.Lines);

            decimal subTotal = 0, totalDiscount = 0, totalVat = 0, totalWht = 0;
            var order = 1;

            foreach (var line in request.Lines)
            {
                var lineAmount = line.Quantity * line.UnitPrice;
                var discountAmt = lineAmount * line.DiscountPercent / 100;
                var afterDiscount = lineAmount - discountAmt;
                var vatAmt = afterDiscount * line.VatRate / 100;
                var whtAmt = afterDiscount * line.WithholdingTaxRate / 100;

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

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            doc.Status = DocumentStatus.Approved;
            doc.UpdatedBy = approvedBy;

            // Auto-post to journal if Invoice or TaxInvoice with account mappings
            if ((doc.DocumentType == DocumentType.Invoice || doc.DocumentType == DocumentType.TaxInvoice)
                && doc.Lines.Any(l => l.AccountId.HasValue))
            {
                await AutoPostToJournalAsync(companyId, doc, approvedBy);
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            return await GetDocumentAsync(companyId, documentId);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task VoidDocumentAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        doc.Status = DocumentStatus.Voided;
        await _db.SaveChangesAsync();
    }

    public async Task<DocumentResponse> ConvertDocumentAsync(Guid companyId, Guid documentId, DocumentType targetType, string createdBy)
    {
        var source = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var lines = source.Lines.Select(l => new DocumentLineRequest(
            l.Description, l.Quantity, l.Unit, l.UnitPrice,
            l.DiscountPercent, l.VatRate, l.WithholdingTaxRate, l.AccountId)).ToList();

        var newDoc = await CreateDocumentAsync(companyId, new CreateDocumentRequest(
            targetType, DateTime.UtcNow, source.DueDate, source.ContactId,
            source.DocumentNumber, source.Notes, lines), createdBy);

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
            var count = await _db.Payments.CountAsync(p => p.CompanyId == companyId);
            var payment = new Payment
            {
                CompanyId = companyId,
                PaymentNumber = $"PAY-{DateTime.UtcNow:yyyyMM}-{(count + 1):D4}",
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

    private async Task AutoPostToJournalAsync(Guid companyId, Document doc, string createdBy)
    {
        var lines = new List<Models.DTOs.Accounting.JournalLineRequest>();

        // Debit: AR (1200)
        var arAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode == "1200");

        if (arAccount != null)
        {
            lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                arAccount.Id, doc.TotalAmount, 0, $"ลูกหนี้การค้า - {doc.DocumentNumber}"));
        }

        // Credit: Revenue accounts from lines
        foreach (var docLine in doc.Lines.Where(l => l.AccountId.HasValue))
        {
            lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                docLine.AccountId!.Value, 0, docLine.Amount, docLine.Description));
        }

        // Credit: VAT payable (2200)
        if (doc.VatAmount > 0)
        {
            var vatAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == "2200");
            if (vatAccount != null)
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    vatAccount.Id, 0, doc.VatAmount, "ภาษีขาย"));
        }

        // Debit: WHT (reduce AR)
        if (doc.WithholdingTaxAmount > 0)
        {
            var whtAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == "2300");
            if (whtAccount != null)
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    whtAccount.Id, 0, doc.WithholdingTaxAmount, "ภาษีหัก ณ ที่จ่าย"));
        }

        if (lines.Count >= 2)
        {
            var entry = await _accountingService.CreateJournalEntryAsync(companyId,
                new Models.DTOs.Accounting.CreateJournalEntryRequest(
                    doc.DocumentDate, $"Auto-post จาก {doc.DocumentNumber}",
                    doc.DocumentNumber, lines), createdBy);

            // Auto-post
            await _accountingService.PostJournalEntryAsync(companyId, entry.Id);

            // Link
            var je = await _db.JournalEntries.FindAsync(entry.Id);
            if (je != null)
            {
                je.IsAutoGenerated = true;
                je.SourceDocumentId = doc.Id;
                await _db.SaveChangesAsync();
            }
        }
    }

    private static DocumentResponse MapDocumentToResponse(Document d) => new(
        d.Id, d.DocumentNumber, d.DocumentType, d.Status,
        d.DocumentDate, d.DueDate,
        new ContactBrief(d.Contact.Id, d.Contact.Name, d.Contact.TaxId),
        d.SubTotal, d.DiscountAmount, d.VatAmount, d.WithholdingTaxAmount,
        d.TotalAmount, d.PaidAmount, d.BalanceDue, d.Notes,
        d.Lines.OrderBy(l => l.LineOrder).Select(l => new DocumentLineResponse(
            l.Id, l.LineOrder, l.Description, l.Quantity, l.Unit,
            l.UnitPrice, l.DiscountPercent, l.DiscountAmount, l.Amount,
            l.VatRate, l.VatAmount, l.WithholdingTaxRate, l.WithholdingTaxAmount)).ToList(),
        d.CreatedAt);

    private static ContactResponse MapContactToResponse(Contact c) => new(
        c.Id, c.Name, c.TaxId, c.BranchCode, c.IsCustomer, c.IsSupplier,
        c.Address, c.Phone, c.Email, c.ContactPerson, c.IsActive);
}
