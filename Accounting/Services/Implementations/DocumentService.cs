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
    private readonly IWithholdingTaxCertService _whtService;

    public DocumentService(AccountingDbContext db, IAccountingService accountingService, ISubscriptionService subscriptionService, IWithholdingTaxCertService whtService)
    {
        _db = db;
        _accountingService = accountingService;
        _subscriptionService = subscriptionService;
        _whtService = whtService;
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

            // Auto-post to journal for document types that affect accounting
            var autoPostTypes = new[] {
                DocumentType.Invoice, DocumentType.TaxInvoice,          // สมุดรายวันขาย (SV)
                DocumentType.PurchaseInvoice, DocumentType.Expense,     // สมุดรายวันซื้อ (UV)
                DocumentType.Receipt, DocumentType.ReceiptVoucher,      // สมุดรายวันรับ (RV)
                DocumentType.PaymentVoucher,                            // สมุดรายวันจ่าย (PV)
            };
            if (autoPostTypes.Contains(doc.DocumentType) && doc.Lines.Any(l => l.AccountId.HasValue))
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

    private async Task AutoPostToJournalAsync(Guid companyId, Document doc, string createdBy)
    {
        var lines = new List<Models.DTOs.Accounting.JournalLineRequest>();

        // Determine journal type based on document type
        var journalType = doc.DocumentType switch
        {
            DocumentType.Invoice or DocumentType.TaxInvoice => JournalType.Sales,
            DocumentType.PurchaseInvoice or DocumentType.Expense => JournalType.Purchase,
            DocumentType.Receipt or DocumentType.ReceiptVoucher => JournalType.CashReceipts,
            DocumentType.PaymentVoucher => JournalType.CashPayments,
            _ => JournalType.General
        };

        // ===== Sales documents (SV): Dr AR, Cr Revenue + VAT Output =====
        // ใบแจ้งหนี้/ใบกำกับภาษี → สมุดรายวันขาย
        if (journalType == JournalType.Sales)
        {
            // Dr: ลูกหนี้การค้า (112101)
            var arAccount = await FindAccountAsync(companyId, "113");
            if (arAccount != null)
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    arAccount.Id, doc.TotalAmount, 0, $"ลูกหนี้การค้า - {doc.DocumentNumber}"));

            // Cr: บัญชีรายได้ตามรายการ
            foreach (var docLine in doc.Lines.Where(l => l.AccountId.HasValue))
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    docLine.AccountId!.Value, 0, docLine.Amount, docLine.Description));

            // Cr: ภาษีขาย (212101)
            if (doc.VatAmount > 0)
            {
                var vatAccount = await FindAccountAsync(companyId, "219");
                if (vatAccount != null)
                    lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                        vatAccount.Id, 0, doc.VatAmount, "ภาษีขาย"));
            }

            // Dr: ภาษีถูกหัก ณ ที่จ่าย (สินทรัพย์) — ผู้ซื้อหักไว้จากยอดจ่าย
            if (doc.WithholdingTaxAmount > 0)
            {
                var whtAccount = await FindAccountAsync(companyId, "11910");
                if (whtAccount != null)
                    lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                        whtAccount.Id, doc.WithholdingTaxAmount, 0, "ภาษีหัก ณ ที่จ่าย (ถูกหัก)"));
            }
        }
        // ===== Purchase documents (UV): Dr Expense/Inventory + VAT Input, Cr AP =====
        // ใบแจ้งหนี้ซื้อ/บันทึกค่าใช้จ่าย → สมุดรายวันซื้อ
        else if (journalType == JournalType.Purchase)
        {
            // Dr: บัญชีค่าใช้จ่าย/สินค้า ตามรายการ
            foreach (var docLine in doc.Lines.Where(l => l.AccountId.HasValue))
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    docLine.AccountId!.Value, docLine.Amount, 0, docLine.Description));

            // Dr: ภาษีซื้อ (114101)
            if (doc.VatAmount > 0)
            {
                var vatInputAccount = await FindAccountAsync(companyId, "116");
                if (vatInputAccount != null)
                    lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                        vatInputAccount.Id, doc.VatAmount, 0, "ภาษีซื้อ"));
            }

            // Cr: เจ้าหนี้การค้า (211101)
            var apAccount = await FindAccountAsync(companyId, "212");
            if (apAccount != null)
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    apAccount.Id, 0, doc.TotalAmount, $"เจ้าหนี้การค้า - {doc.DocumentNumber}"));

            // Cr: ภาษีหัก ณ ที่จ่าย ค้างจ่าย (212201)
            if (doc.WithholdingTaxAmount > 0)
            {
                var whtAccount = await FindAccountAsync(companyId, "219");
                if (whtAccount != null)
                    lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                        whtAccount.Id, 0, doc.WithholdingTaxAmount, "ภาษีหัก ณ ที่จ่ายค้างจ่าย"));
            }
        }
        // ===== Cash Receipts (RV): Dr Cash/Bank, Cr AR =====
        // ใบเสร็จรับเงิน/ใบสำคัญรับ → สมุดรายวันรับ
        else if (journalType == JournalType.CashReceipts)
        {
            // Dr: เงินสด/ธนาคาร (111101)
            var cashAccount = await FindAccountAsync(companyId, "111");
            if (cashAccount != null)
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    cashAccount.Id, doc.TotalAmount, 0, $"รับเงิน - {doc.DocumentNumber}"));

            // Cr: ลูกหนี้การค้า (112101) — ลดยอดลูกหนี้
            var arAccount = await FindAccountAsync(companyId, "113");
            if (arAccount != null)
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    arAccount.Id, 0, doc.TotalAmount, $"ตัดลูกหนี้ - {doc.DocumentNumber}"));
        }
        // ===== Cash Payments (PV): Dr AP, Cr Cash/Bank =====
        // ใบสำคัญจ่าย → สมุดรายวันจ่าย
        else if (journalType == JournalType.CashPayments)
        {
            // Dr: เจ้าหนี้การค้า (211101) — ลดยอดเจ้าหนี้
            var apAccount = await FindAccountAsync(companyId, "212");
            if (apAccount != null)
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    apAccount.Id, doc.TotalAmount, 0, $"ตัดเจ้าหนี้ - {doc.DocumentNumber}"));

            // Cr: เงินสด/ธนาคาร (111101)
            var cashAccount = await FindAccountAsync(companyId, "111");
            if (cashAccount != null)
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    cashAccount.Id, 0, doc.TotalAmount, $"จ่ายเงิน - {doc.DocumentNumber}"));
        }

        if (lines.Count >= 2)
        {
            var entry = await _accountingService.CreateJournalEntryAsync(companyId,
                new Models.DTOs.Accounting.CreateJournalEntryRequest(
                    doc.DocumentDate, $"Auto-post จาก {doc.DocumentNumber}",
                    doc.DocumentNumber, lines, journalType), createdBy);

            await _accountingService.PostJournalEntryAsync(companyId, entry.Id);

            var je = await _db.JournalEntries.FindAsync(entry.Id);
            if (je != null)
            {
                je.IsAutoGenerated = true;
                je.SourceDocumentId = doc.Id;
                await _db.SaveChangesAsync();
            }
        }
    }

    /// <summary>
    /// สร้าง Journal Entry สำหรับการชำระเงิน
    /// รับเงิน (ฝั่งขาย): Dr Cash, Cr AR → สมุดรายวันรับ (RV)
    /// จ่ายเงิน (ฝั่งซื้อ): Dr AP, Cr Cash → สมุดรายวันจ่าย (PV)
    /// </summary>
    private async Task CreatePaymentJournalAsync(Guid companyId, Document doc, Payment payment, string createdBy)
    {
        var lines = new List<Models.DTOs.Accounting.JournalLineRequest>();
        var revenueTypes = new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt,
            DocumentType.DebitNote, DocumentType.BillingNote, DocumentType.ReceiptVoucher };
        var isRevenue = revenueTypes.Contains(doc.DocumentType);
        var journalType = isRevenue ? JournalType.CashReceipts : JournalType.CashPayments;

        var cashAccount = await FindAccountAsync(companyId, "111");
        if (cashAccount == null) return;

        if (isRevenue)
        {
            // รับเงินจากลูกค้า: Dr Cash, Cr AR
            lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                cashAccount.Id, payment.Amount, 0, $"รับชำระ - {doc.DocumentNumber} ({payment.PaymentNumber})"));
            var arAccount = await FindAccountAsync(companyId, "113");
            if (arAccount != null)
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    arAccount.Id, 0, payment.Amount, $"ตัดลูกหนี้ - {doc.DocumentNumber}"));
        }
        else
        {
            // จ่ายเงินให้ supplier: Dr AP, Cr Cash
            var apAccount = await FindAccountAsync(companyId, "212");
            if (apAccount != null)
                lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                    apAccount.Id, payment.Amount, 0, $"ตัดเจ้าหนี้ - {doc.DocumentNumber}"));
            lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                cashAccount.Id, 0, payment.Amount, $"จ่ายชำระ - {doc.DocumentNumber} ({payment.PaymentNumber})"));
        }

        if (lines.Count >= 2)
        {
            var entry = await _accountingService.CreateJournalEntryAsync(companyId,
                new Models.DTOs.Accounting.CreateJournalEntryRequest(
                    payment.PaymentDate, $"ชำระเงิน {payment.PaymentNumber} - {doc.DocumentNumber}",
                    payment.PaymentNumber, lines, journalType), createdBy);
            await _accountingService.PostJournalEntryAsync(companyId, entry.Id);
            var je = await _db.JournalEntries.FindAsync(entry.Id);
            if (je != null)
            {
                je.IsAutoGenerated = true;
                je.SourceDocumentId = doc.Id;
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
