using System.Diagnostics;
using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs.Integration;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class IntegrationService : IIntegrationService
{
    private readonly AccountingDbContext _db;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<IntegrationService> _logger;

    public IntegrationService(AccountingDbContext db, ISettingsService settingsService, ILogger<IntegrationService> logger)
    {
        _db = db;
        _settingsService = settingsService;
        _logger = logger;
    }

    // ===== Integration Config =====

    public async Task<List<IntegrationResponse>> GetIntegrationsAsync(Guid companyId)
    {
        return await _db.Set<ExternalIntegration>()
            .Where(i => i.CompanyId == companyId && !i.IsDeleted)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new IntegrationResponse(
                i.Id, i.SystemName, i.SystemType, i.SystemVersion, i.BaseUrl,
                i.ApiKeyPrefix, i.IsActive, i.LastSyncAt, i.TotalSyncCount, i.ErrorCount,
                i.RateLimitPerMinute, i.WebhookUrl, i.WebhookEnabled, i.CreatedAt))
            .ToListAsync();
    }

    public async Task<IntegrationCreatedResponse> CreateIntegrationAsync(Guid companyId, CreateIntegrationRequest request)
    {
        // Generate API key
        var rawKey = $"int_{Convert.ToBase64String(Guid.NewGuid().ToByteArray())}{Convert.ToBase64String(Guid.NewGuid().ToByteArray())}"
            .Replace("=", "").Replace("+", "").Replace("/", "");
        var keyPrefix = rawKey[..8];
        var keyHash = BCrypt.Net.BCrypt.HashPassword(rawKey);

        // Generate HMAC secret
        var secretKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        var integration = new ExternalIntegration
        {
            CompanyId = companyId,
            SystemName = request.SystemName,
            SystemType = request.SystemType,
            SystemVersion = request.SystemVersion,
            BaseUrl = request.BaseUrl,
            ApiKey = rawKey, // Store temporarily — will be shown only once
            ApiKeyHash = keyHash,
            ApiKeyPrefix = keyPrefix,
            SecretKey = secretKey,
            RateLimitPerMinute = request.RateLimitPerMinute,
            WebhookUrl = request.WebhookUrl,
            WebhookEnabled = request.WebhookEnabled
        };

        _db.Set<ExternalIntegration>().Add(integration);
        await _db.SaveChangesAsync();

        // Clear raw key from entity (only the hash is stored permanently)
        integration.ApiKey = "";
        await _db.SaveChangesAsync();

        return new IntegrationCreatedResponse(integration.Id, integration.SystemName, rawKey, keyPrefix, secretKey, integration.CreatedAt);
    }

    public async Task<IntegrationResponse> UpdateIntegrationAsync(Guid companyId, Guid integrationId, UpdateIntegrationRequest request)
    {
        var integration = await _db.Set<ExternalIntegration>()
            .FirstOrDefaultAsync(i => i.Id == integrationId && i.CompanyId == companyId && !i.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบ Integration");

        if (request.SystemName != null) integration.SystemName = request.SystemName;
        if (request.SystemType != null) integration.SystemType = request.SystemType;
        if (request.SystemVersion != null) integration.SystemVersion = request.SystemVersion;
        if (request.BaseUrl != null) integration.BaseUrl = request.BaseUrl;
        if (request.IsActive.HasValue) integration.IsActive = request.IsActive.Value;
        if (request.RateLimitPerMinute.HasValue) integration.RateLimitPerMinute = request.RateLimitPerMinute.Value;
        if (request.WebhookUrl != null) integration.WebhookUrl = request.WebhookUrl;
        if (request.WebhookEnabled.HasValue) integration.WebhookEnabled = request.WebhookEnabled.Value;

        await _db.SaveChangesAsync();

        return new IntegrationResponse(
            integration.Id, integration.SystemName, integration.SystemType, integration.SystemVersion,
            integration.BaseUrl, integration.ApiKeyPrefix, integration.IsActive, integration.LastSyncAt,
            integration.TotalSyncCount, integration.ErrorCount, integration.RateLimitPerMinute,
            integration.WebhookUrl, integration.WebhookEnabled, integration.CreatedAt);
    }

    public async Task DeleteIntegrationAsync(Guid companyId, Guid integrationId)
    {
        var integration = await _db.Set<ExternalIntegration>()
            .FirstOrDefaultAsync(i => i.Id == integrationId && i.CompanyId == companyId && !i.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบ Integration");

        integration.IsDeleted = true;
        integration.IsActive = false;
        await _db.SaveChangesAsync();
    }

    public async Task<IntegrationResponse> RegenerateApiKeyAsync(Guid companyId, Guid integrationId)
    {
        var integration = await _db.Set<ExternalIntegration>()
            .FirstOrDefaultAsync(i => i.Id == integrationId && i.CompanyId == companyId && !i.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบ Integration");

        var rawKey = $"int_{Convert.ToBase64String(Guid.NewGuid().ToByteArray())}{Convert.ToBase64String(Guid.NewGuid().ToByteArray())}"
            .Replace("=", "").Replace("+", "").Replace("/", "");
        integration.ApiKeyHash = BCrypt.Net.BCrypt.HashPassword(rawKey);
        integration.ApiKeyPrefix = rawKey[..8];
        integration.ConsecutiveErrors = 0;
        await _db.SaveChangesAsync();

        return new IntegrationResponse(
            integration.Id, integration.SystemName, integration.SystemType, integration.SystemVersion,
            integration.BaseUrl, integration.ApiKeyPrefix, integration.IsActive, integration.LastSyncAt,
            integration.TotalSyncCount, integration.ErrorCount, integration.RateLimitPerMinute,
            integration.WebhookUrl, integration.WebhookEnabled, integration.CreatedAt);
    }

    // ===== Account Mapping =====

    public async Task<List<AccountMappingResponse>> GetMappingsAsync(Guid companyId, Guid integrationId)
    {
        return await _db.Set<IntegrationAccountMapping>()
            .Include(m => m.DebitAccount)
            .Include(m => m.CreditAccount)
            .Where(m => m.IntegrationId == integrationId && m.CompanyId == companyId && !m.IsDeleted)
            .OrderBy(m => m.ExternalCategory)
            .Select(m => new AccountMappingResponse(
                m.Id, m.IntegrationId,
                m.ExternalCategory, m.ExternalCode, m.ExternalDescription,
                m.DebitAccountId, m.DebitAccount != null ? m.DebitAccount.AccountCode : null, m.DebitAccount != null ? m.DebitAccount.AccountName : null,
                m.CreditAccountId, m.CreditAccount != null ? m.CreditAccount.AccountCode : null, m.CreditAccount != null ? m.CreditAccount.AccountName : null,
                m.JournalDescription, m.IsActive, m.AutoCreateJournal))
            .ToListAsync();
    }

    public async Task<AccountMappingResponse> CreateMappingAsync(Guid companyId, Guid integrationId, CreateAccountMappingRequest request)
    {
        var mapping = new IntegrationAccountMapping
        {
            CompanyId = companyId,
            IntegrationId = integrationId,
            ExternalCategory = request.ExternalCategory,
            ExternalCode = request.ExternalCode,
            ExternalDescription = request.ExternalDescription,
            DebitAccountId = request.DebitAccountId,
            CreditAccountId = request.CreditAccountId,
            JournalDescription = request.JournalDescription,
            AutoCreateJournal = request.AutoCreateJournal
        };

        _db.Set<IntegrationAccountMapping>().Add(mapping);
        await _db.SaveChangesAsync();

        return (await GetMappingsAsync(companyId, integrationId)).First(m => m.Id == mapping.Id);
    }

    public async Task<AccountMappingResponse> UpdateMappingAsync(Guid companyId, Guid integrationId, Guid mappingId, UpdateAccountMappingRequest request)
    {
        var mapping = await _db.Set<IntegrationAccountMapping>()
            .FirstOrDefaultAsync(m => m.Id == mappingId && m.IntegrationId == integrationId && m.CompanyId == companyId && !m.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบ Mapping");

        if (request.ExternalCategory != null) mapping.ExternalCategory = request.ExternalCategory;
        if (request.ExternalCode != null) mapping.ExternalCode = request.ExternalCode;
        if (request.ExternalDescription != null) mapping.ExternalDescription = request.ExternalDescription;
        if (request.DebitAccountId.HasValue) mapping.DebitAccountId = request.DebitAccountId;
        if (request.CreditAccountId.HasValue) mapping.CreditAccountId = request.CreditAccountId;
        if (request.JournalDescription != null) mapping.JournalDescription = request.JournalDescription;
        if (request.IsActive.HasValue) mapping.IsActive = request.IsActive.Value;
        if (request.AutoCreateJournal.HasValue) mapping.AutoCreateJournal = request.AutoCreateJournal.Value;

        await _db.SaveChangesAsync();

        return (await GetMappingsAsync(companyId, integrationId)).First(m => m.Id == mapping.Id);
    }

    public async Task DeleteMappingAsync(Guid companyId, Guid integrationId, Guid mappingId)
    {
        var mapping = await _db.Set<IntegrationAccountMapping>()
            .FirstOrDefaultAsync(m => m.Id == mappingId && m.IntegrationId == integrationId && m.CompanyId == companyId && !m.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบ Mapping");

        mapping.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ===== Sync Logs =====

    public async Task<List<SyncLogResponse>> GetSyncLogsAsync(Guid companyId, Guid? integrationId, int page = 1, int pageSize = 50)
    {
        var query = _db.Set<IntegrationSyncLog>()
            .Where(l => l.CompanyId == companyId && !l.IsDeleted);

        if (integrationId.HasValue)
            query = query.Where(l => l.IntegrationId == integrationId.Value);

        return await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new SyncLogResponse(
                l.Id, l.EventType, l.ExternalId, l.ExternalRef,
                l.Status, l.ErrorMessage,
                l.CreatedDocumentId, l.CreatedContactId, l.CreatedJournalEntryId,
                l.ProcessingTimeMs, l.CreatedAt))
            .ToListAsync();
    }

    // ===== Dashboard =====

    public async Task<IntegrationDashboardResponse> GetDashboardAsync(Guid companyId)
    {
        var integrations = await _db.Set<ExternalIntegration>()
            .Where(i => i.CompanyId == companyId && !i.IsDeleted)
            .ToListAsync();

        var today = DateTime.UtcNow.Date;
        var todayLogs = await _db.Set<IntegrationSyncLog>()
            .Where(l => l.CompanyId == companyId && l.CreatedAt >= today)
            .GroupBy(l => l.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        var recentSyncs = await _db.Set<IntegrationSyncLog>()
            .Include(l => l.Integration)
            .Where(l => l.CompanyId == companyId)
            .OrderByDescending(l => l.CreatedAt)
            .Take(20)
            .Select(l => new RecentSyncItem(
                l.Id, l.Integration.SystemName, l.EventType, l.Status,
                l.ExternalRef, l.CreatedAt))
            .ToListAsync();

        return new IntegrationDashboardResponse(
            integrations.Count,
            integrations.Count(i => i.IsActive),
            todayLogs.Sum(l => l.Count),
            todayLogs.Where(l => l.Status == "Failed").Sum(l => l.Count),
            integrations.Select(i => new IntegrationSummary(
                i.Id, i.SystemName, i.SystemType, i.IsActive,
                i.LastSyncAt, i.TotalSyncCount, i.ErrorCount)).ToList(),
            recentSyncs);
    }

    // ===== API Key Validation =====

    public async Task<(Guid CompanyId, Guid IntegrationId)?> ValidateApiKeyAsync(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length < 8)
            return null;

        var prefix = apiKey[..8];
        var candidates = await _db.Set<ExternalIntegration>()
            .Where(i => i.ApiKeyPrefix == prefix && i.IsActive && !i.IsDeleted)
            .Select(i => new { i.Id, i.CompanyId, i.ApiKeyHash })
            .ToListAsync();

        foreach (var candidate in candidates)
        {
            if (BCrypt.Net.BCrypt.Verify(apiKey, candidate.ApiKeyHash))
                return (candidate.CompanyId, candidate.Id);
        }

        return null;
    }

    // ===== Phase 2: Inbound Data Processing =====

    public async Task<InboundSyncResponse> ProcessCustomerAsync(Guid companyId, Guid integrationId, InboundCustomerRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "customer.sync", request.ExternalId, request.Name);

        try
        {
            // Find existing contact by TaxId or Name
            Contact? contact = null;
            if (!string.IsNullOrEmpty(request.TaxId))
                contact = await _db.Set<Contact>().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.TaxId == request.TaxId && !c.IsDeleted);
            if (contact == null)
                contact = await _db.Set<Contact>().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Name == request.Name && !c.IsDeleted);

            if (contact == null)
            {
                contact = new Contact
                {
                    CompanyId = companyId,
                    Name = request.Name,
                    TaxId = request.TaxId,
                    Phone = request.Phone,
                    Email = request.Email,
                    Address = request.Address,
                    BranchCode = request.BranchCode,
                    ContactType = ParseContactType(request.ContactType),
                    IsCustomer = request.IsCustomer ?? true,
                    IsSupplier = request.IsSupplier ?? false,
                    IsActive = true
                };
                _db.Set<Contact>().Add(contact);
            }
            else
            {
                // Update existing
                if (request.Phone != null) contact.Phone = request.Phone;
                if (request.Email != null) contact.Email = request.Email;
                if (request.Address != null) contact.Address = request.Address;
                if (request.TaxId != null) contact.TaxId = request.TaxId;
            }

            await _db.SaveChangesAsync();

            log.Status = "Success";
            log.CreatedContactId = contact.Id;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Customer synced", null, contact.Id, null, null, null);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    public async Task<InboundSyncResponse> ProcessInvoiceAsync(Guid companyId, Guid integrationId, InboundInvoiceRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "invoice.created", request.ExternalId, request.ExternalRef);

        try
        {
            // Resolve or create contact
            var contact = await ResolveContactAsync(companyId, request.CustomerExternalId, request.CustomerName, request.CustomerTaxId);

            // Get next document number
            var docNumber = await _settingsService.GetNextNumberAsync(companyId, DocumentType.TaxInvoice);

            // Calculate totals
            var vatRate = request.VatRate ?? 7m;
            decimal subTotal = 0, totalVat = 0, totalDiscount = 0;
            var lines = new List<DocumentLine>();

            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                var lineAmount = line.Quantity * line.UnitPrice;
                var lineDiscount = line.DiscountAmount ?? 0;
                var lineNet = lineAmount - lineDiscount;
                var lineVatRate = line.VatRate ?? vatRate;
                var lineVat = request.IncludeVat
                    ? lineNet - (lineNet / (1 + lineVatRate / 100))
                    : lineNet * lineVatRate / 100;

                subTotal += lineNet;
                totalVat += lineVat;
                totalDiscount += lineDiscount;

                // Find account by code or category mapping
                Guid? accountId = null;
                if (!string.IsNullOrEmpty(line.AccountCode))
                {
                    var account = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == line.AccountCode);
                    accountId = account?.Id;
                }

                lines.Add(new DocumentLine
                {
                    LineOrder = i + 1,
                    ProductCode = line.ItemCode,
                    Description = line.ItemName,
                    Quantity = line.Quantity,
                    Unit = line.Unit ?? "หน่วย",
                    UnitPrice = line.UnitPrice,
                    DiscountAmount = lineDiscount,
                    Amount = lineNet,
                    VatRate = lineVatRate,
                    VatAmount = lineVat,
                    AccountId = accountId
                });
            }

            var totalAmount = request.IncludeVat ? subTotal : subTotal + totalVat;

            var document = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = DocumentType.TaxInvoice,
                Status = DocumentStatus.Approved,
                DocumentDate = request.DocumentDate,
                DueDate = request.DueDate ?? request.DocumentDate.AddDays(30),
                ContactId = contact.Id,
                Reference = request.ExternalRef,
                SubTotal = subTotal,
                DiscountAmount = totalDiscount,
                VatAmount = totalVat,
                TotalAmount = totalAmount,
                BalanceDue = totalAmount,
                Notes = request.Notes,
                Lines = lines
            };

            _db.Documents.Add(document);
            await _db.SaveChangesAsync();

            // Auto-create journal entry from category mappings
            var journalEntryId = await CreateJournalFromMappingsAsync(companyId, integrationId, document, "invoice");

            log.Status = "Success";
            log.CreatedDocumentId = document.Id;
            log.CreatedContactId = contact.Id;
            log.CreatedJournalEntryId = journalEntryId;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Invoice created", document.Id, contact.Id, journalEntryId, null, docNumber);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    public async Task<InboundSyncResponse> ProcessPaymentAsync(Guid companyId, Guid integrationId, InboundPaymentRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "payment.received", request.ExternalId, request.ExternalRef);

        try
        {
            // Find document
            Document? document = null;
            if (request.DocumentId.HasValue)
                document = await _db.Documents.FirstOrDefaultAsync(d => d.Id == request.DocumentId.Value && d.CompanyId == companyId);
            else if (!string.IsNullOrEmpty(request.InvoiceExternalRef))
                document = await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Reference == request.InvoiceExternalRef && !d.IsDeleted);

            if (document == null)
                throw new KeyNotFoundException($"ไม่พบเอกสารอ้างอิง: {request.InvoiceExternalRef ?? request.DocumentId?.ToString()}");

            // Create payment
            var paymentCount = await _db.Set<Payment>().CountAsync(p => p.CompanyId == companyId);
            var payment = new Payment
            {
                CompanyId = companyId,
                PaymentNumber = $"PAY-{DateTime.UtcNow:yyyyMM}-{(paymentCount + 1):D4}",
                DocumentId = document.Id,
                PaymentDate = request.PaymentDate,
                Amount = request.Amount,
                PaymentMethod = ParsePaymentMethod(request.PaymentMethod),
                BankAccount = request.BankAccountName,
                Reference = request.ReferenceNo ?? request.ExternalRef,
                Notes = request.Notes
            };

            _db.Set<Payment>().Add(payment);

            // Update document
            document.PaidAmount += request.Amount;
            document.BalanceDue = document.TotalAmount - document.PaidAmount;
            if (document.BalanceDue <= 0)
            {
                document.BalanceDue = 0;
                document.Status = DocumentStatus.Paid;
            }
            else
            {
                document.Status = DocumentStatus.PartiallyPaid;
            }

            await _db.SaveChangesAsync();

            // Create journal entry for payment
            var journalEntryId = await CreatePaymentJournalAsync(companyId, integrationId, payment, document, request);

            log.Status = "Success";
            log.CreatedPaymentId = payment.Id;
            log.CreatedDocumentId = document.Id;
            log.CreatedJournalEntryId = journalEntryId;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Payment recorded", document.Id, null, journalEntryId, payment.Id, payment.PaymentNumber);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    public async Task<InboundSyncResponse> ProcessCreditNoteAsync(Guid companyId, Guid integrationId, InboundCreditNoteRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "creditnote.created", request.ExternalId, request.ExternalRef);

        try
        {
            var contact = await ResolveContactAsync(companyId, request.CustomerExternalId, request.CustomerName, null);
            var docNumber = await _settingsService.GetNextNumberAsync(companyId, DocumentType.CreditNote);

            // Find original document
            Guid? relatedDocId = request.OriginalDocumentId;
            if (relatedDocId == null && !string.IsNullOrEmpty(request.OriginalInvoiceRef))
            {
                var original = await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Reference == request.OriginalInvoiceRef);
                relatedDocId = original?.Id;
            }

            var lines = BuildDocumentLines(request.Lines);
            var subTotal = lines.Sum(l => l.Amount);
            var totalVat = lines.Sum(l => l.VatAmount);

            var document = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = DocumentType.CreditNote,
                Status = DocumentStatus.Approved,
                DocumentDate = request.DocumentDate,
                ContactId = contact.Id,
                RelatedDocumentId = relatedDocId,
                Reference = request.ExternalRef,
                SubTotal = subTotal,
                VatAmount = totalVat,
                TotalAmount = subTotal + totalVat,
                BalanceDue = 0,
                Notes = $"{request.Reason}\n{request.Notes}".Trim(),
                Lines = lines
            };

            _db.Documents.Add(document);
            await _db.SaveChangesAsync();

            log.Status = "Success";
            log.CreatedDocumentId = document.Id;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Credit Note created", document.Id, contact.Id, null, null, docNumber);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    public async Task<InboundSyncResponse> ProcessDebitNoteAsync(Guid companyId, Guid integrationId, InboundDebitNoteRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "debitnote.created", request.ExternalId, request.ExternalRef);

        try
        {
            var contact = await ResolveContactAsync(companyId, request.CustomerExternalId, request.CustomerName, null);
            var docNumber = await _settingsService.GetNextNumberAsync(companyId, DocumentType.DebitNote);

            Guid? relatedDocId = request.OriginalDocumentId;
            if (relatedDocId == null && !string.IsNullOrEmpty(request.OriginalInvoiceRef))
            {
                var original = await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Reference == request.OriginalInvoiceRef);
                relatedDocId = original?.Id;
            }

            var lines = BuildDocumentLines(request.Lines);
            var subTotal = lines.Sum(l => l.Amount);
            var totalVat = lines.Sum(l => l.VatAmount);

            var document = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = DocumentType.DebitNote,
                Status = DocumentStatus.Approved,
                DocumentDate = request.DocumentDate,
                ContactId = contact.Id,
                RelatedDocumentId = relatedDocId,
                Reference = request.ExternalRef,
                SubTotal = subTotal,
                VatAmount = totalVat,
                TotalAmount = subTotal + totalVat,
                BalanceDue = subTotal + totalVat,
                Notes = $"{request.Reason}\n{request.Notes}".Trim(),
                Lines = lines
            };

            _db.Documents.Add(document);
            await _db.SaveChangesAsync();

            log.Status = "Success";
            log.CreatedDocumentId = document.Id;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Debit Note created", document.Id, contact.Id, null, null, docNumber);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    // ===== Phase 3: Daily Summary / Auto Journal =====

    public async Task<InboundSyncResponse> ProcessDailySummaryAsync(Guid companyId, Guid integrationId, InboundDailySummaryRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "daily.summary", null, request.SummaryDate.ToString("yyyy-MM-dd"));

        try
        {
            // Load mappings for this integration
            var mappings = await _db.Set<IntegrationAccountMapping>()
                .Where(m => m.IntegrationId == integrationId && m.CompanyId == companyId && m.IsActive && !m.IsDeleted)
                .ToListAsync();

            var journalLines = new List<JournalEntryLine>();
            int lineOrder = 1;

            foreach (var line in request.Lines)
            {
                // Find mapping by category
                var mapping = mappings.FirstOrDefault(m =>
                    m.ExternalCategory.Equals(line.Category, StringComparison.OrdinalIgnoreCase))
                    ?? mappings.FirstOrDefault(m => m.ExternalCategory == "DEFAULT");

                if (mapping == null)
                {
                    _logger.LogWarning("No mapping found for category '{Category}' in integration {IntegrationId}", line.Category, integrationId);
                    continue;
                }

                if (mapping.DebitAccountId.HasValue)
                {
                    journalLines.Add(new JournalEntryLine
                    {
                        AccountId = mapping.DebitAccountId.Value,
                        DebitAmount = line.Amount,
                        CreditAmount = 0,
                        Description = line.Description ?? mapping.JournalDescription ?? line.Category,
                        LineOrder = lineOrder++
                    });
                }

                if (mapping.CreditAccountId.HasValue)
                {
                    journalLines.Add(new JournalEntryLine
                    {
                        AccountId = mapping.CreditAccountId.Value,
                        DebitAmount = 0,
                        CreditAmount = line.Amount,
                        Description = line.Description ?? mapping.JournalDescription ?? line.Category,
                        LineOrder = lineOrder++
                    });
                }
            }

            if (!journalLines.Any())
                throw new InvalidOperationException("ไม่มี mapping ที่ตรงกับ category ที่ส่งมา กรุณาตั้งค่า account mapping ก่อน");

            // Create journal entry
            var jeCount = await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId);
            var journalEntry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = $"JV-INT-{DateTime.UtcNow:yyyyMM}-{(jeCount + 1):D4}",
                EntryDate = request.SummaryDate,
                JournalType = JournalType.General,
                Description = request.Description ?? $"สรุปรายวัน {request.SummaryDate:dd/MM/yyyy}",
                Reference = $"DAILY-{request.SummaryDate:yyyyMMdd}",
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                TotalDebit = journalLines.Sum(l => l.DebitAmount),
                TotalCredit = journalLines.Sum(l => l.CreditAmount),
                Lines = journalLines
            };

            _db.JournalEntries.Add(journalEntry);
            await _db.SaveChangesAsync();

            log.Status = "Success";
            log.CreatedJournalEntryId = journalEntry.Id;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Daily summary journal created", null, null, journalEntry.Id, null, journalEntry.EntryNumber);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    // ===== Helpers =====

    private async Task<Contact> ResolveContactAsync(Guid companyId, string? externalId, string? name, string? taxId)
    {
        Contact? contact = null;

        if (!string.IsNullOrEmpty(taxId))
            contact = await _db.Set<Contact>().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.TaxId == taxId && !c.IsDeleted);

        if (contact == null && !string.IsNullOrEmpty(name))
            contact = await _db.Set<Contact>().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Name == name && !c.IsDeleted);

        if (contact == null)
        {
            contact = new Contact
            {
                CompanyId = companyId,
                Name = name ?? "ลูกค้าทั่วไป",
                TaxId = taxId,
                IsCustomer = true,
                IsActive = true
            };
            _db.Set<Contact>().Add(contact);
            await _db.SaveChangesAsync();
        }

        return contact;
    }

    private static List<DocumentLine> BuildDocumentLines(List<InboundInvoiceLineRequest> lines, decimal defaultVatRate = 7)
    {
        return lines.Select((line, i) =>
        {
            var lineAmount = line.Quantity * line.UnitPrice;
            var lineDiscount = line.DiscountAmount ?? 0;
            var lineNet = lineAmount - lineDiscount;
            var lineVatRate = line.VatRate ?? defaultVatRate;
            var lineVat = lineNet * lineVatRate / 100;

            return new DocumentLine
            {
                LineOrder = i + 1,
                ProductCode = line.ItemCode,
                Description = line.ItemName,
                Quantity = line.Quantity,
                Unit = line.Unit ?? "หน่วย",
                UnitPrice = line.UnitPrice,
                DiscountAmount = lineDiscount,
                Amount = lineNet,
                VatRate = lineVatRate,
                VatAmount = lineVat
            };
        }).ToList();
    }

    private async Task<Guid?> CreateJournalFromMappingsAsync(Guid companyId, Guid integrationId, Document document, string type)
    {
        var mappings = await _db.Set<IntegrationAccountMapping>()
            .Where(m => m.IntegrationId == integrationId && m.CompanyId == companyId && m.IsActive && m.AutoCreateJournal && !m.IsDeleted)
            .ToListAsync();

        if (!mappings.Any()) return null;

        var journalLines = new List<JournalEntryLine>();
        int lineOrder = 1;

        // For each document line, try to find a matching mapping
        foreach (var docLine in document.Lines)
        {
            var mapping = mappings.FirstOrDefault(m =>
                !string.IsNullOrEmpty(docLine.ProductCode) && m.ExternalCode == docLine.ProductCode)
                ?? mappings.FirstOrDefault(m => m.ExternalCategory == "DEFAULT");

            if (mapping == null) continue;

            if (mapping.DebitAccountId.HasValue)
            {
                journalLines.Add(new JournalEntryLine
                {
                    AccountId = mapping.DebitAccountId.Value,
                    DebitAmount = docLine.Amount,
                    Description = docLine.Description,
                    LineOrder = lineOrder++
                });
            }

            if (mapping.CreditAccountId.HasValue)
            {
                journalLines.Add(new JournalEntryLine
                {
                    AccountId = mapping.CreditAccountId.Value,
                    CreditAmount = docLine.Amount,
                    Description = docLine.Description,
                    LineOrder = lineOrder++
                });
            }
        }

        if (!journalLines.Any()) return null;

        var jeCount = await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId);
        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = $"JV-INT-{DateTime.UtcNow:yyyyMM}-{(jeCount + 1):D4}",
            EntryDate = document.DocumentDate,
            JournalType = type == "payment" ? JournalType.CashReceipts : JournalType.Sales,
            Description = $"Auto: {document.DocumentNumber}",
            Reference = document.DocumentNumber,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            SourceDocumentId = document.Id,
            TotalDebit = journalLines.Sum(l => l.DebitAmount),
            TotalCredit = journalLines.Sum(l => l.CreditAmount),
            Lines = journalLines
        };

        _db.JournalEntries.Add(je);
        await _db.SaveChangesAsync();
        return je.Id;
    }

    private async Task<Guid?> CreatePaymentJournalAsync(Guid companyId, Guid integrationId, Payment payment, Document document, InboundPaymentRequest request)
    {
        // Find cash/bank account for debit
        var paymentMethod = request.PaymentMethod?.ToLower();
        var cashAccountCode = paymentMethod switch
        {
            "cash" => "111",         // เงินสด
            "banktransfer" or "promptpay" => "112", // เงินฝากธนาคาร
            "creditcard" => "112",
            _ => "111"
        };

        var debitAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith(cashAccountCode) && a.IsActive);
        var creditAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("113") && a.IsActive); // ลูกหนี้การค้า

        if (debitAccount == null || creditAccount == null) return null;

        var jeCount = await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId);
        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = $"JV-PAY-{DateTime.UtcNow:yyyyMM}-{(jeCount + 1):D4}",
            EntryDate = payment.PaymentDate,
            JournalType = JournalType.CashReceipts,
            Description = $"รับชำระ {document.DocumentNumber}",
            Reference = payment.PaymentNumber,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            SourceDocumentId = document.Id,
            TotalDebit = payment.Amount,
            TotalCredit = payment.Amount,
            Lines = new List<JournalEntryLine>
            {
                new() { AccountId = debitAccount.Id, DebitAmount = payment.Amount, Description = $"รับชำระ ({request.PaymentMethod})", LineOrder = 1 },
                new() { AccountId = creditAccount.Id, CreditAmount = payment.Amount, Description = $"ตัดลูกหนี้ {document.DocumentNumber}", LineOrder = 2 }
            }
        };

        _db.JournalEntries.Add(je);
        await _db.SaveChangesAsync();
        return je.Id;
    }

    private IntegrationSyncLog CreateSyncLog(Guid companyId, Guid integrationId, string eventType, string? externalId, string? externalRef)
    {
        return new IntegrationSyncLog
        {
            CompanyId = companyId,
            IntegrationId = integrationId,
            EventType = eventType,
            ExternalId = externalId,
            ExternalRef = externalRef,
            Status = "Pending"
        };
    }

    private async Task SaveSyncLog(IntegrationSyncLog log, Guid integrationId)
    {
        _db.Set<IntegrationSyncLog>().Add(log);

        var integration = await _db.Set<ExternalIntegration>().FindAsync(integrationId);
        if (integration != null)
        {
            integration.LastSyncAt = DateTime.UtcNow;
            integration.TotalSyncCount++;
            if (log.Status == "Failed")
            {
                integration.ErrorCount++;
                integration.ConsecutiveErrors++;
            }
            else
            {
                integration.ConsecutiveErrors = 0;
            }
        }

        await _db.SaveChangesAsync();
    }

    private async Task<InboundSyncResponse> HandleSyncError(IntegrationSyncLog log, Guid integrationId, Exception ex, Stopwatch sw)
    {
        _logger.LogError(ex, "Integration sync error: {EventType} - {Message}", log.EventType, ex.Message);
        log.Status = "Failed";
        log.ErrorMessage = ex.Message;
        log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
        await SaveSyncLog(log, integrationId);
        return new InboundSyncResponse(false, ex.Message, null, null, null, null, null);
    }

    // ===== Phase 4: Revenue Reports =====

    public async Task<List<RevenueByCategoryItem>> GetRevenueByCategoryAsync(Guid companyId, DateTime? from, DateTime? to)
    {
        var fromDate = from ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var toDate = to ?? DateTime.UtcNow;

        var data = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fromDate
                && l.JournalEntry.EntryDate <= toDate
                && l.Account.AccountType == AccountType.Revenue)
            .GroupBy(l => new { l.Account.AccountCode, l.Account.AccountName })
            .Select(g => new { g.Key.AccountCode, g.Key.AccountName, Amount = g.Sum(l => l.CreditAmount - l.DebitAmount) })
            .OrderByDescending(x => x.Amount)
            .ToListAsync();

        var total = data.Sum(d => d.Amount);
        return data.Select(d => new RevenueByCategoryItem(
            d.AccountCode, d.AccountName, d.Amount,
            total > 0 ? Math.Round(d.Amount / total * 100, 2) : 0)).ToList();
    }

    public async Task<List<RevenueBySourceItem>> GetRevenueBySourceAsync(Guid companyId, DateTime? from, DateTime? to)
    {
        var fromDate = from ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var toDate = to ?? DateTime.UtcNow;

        // Group invoices by integration source (Reference prefix) or direct
        var data = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.Status != DocumentStatus.Voided
                && d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .GroupBy(d => d.Reference != null && d.Reference.Length > 0
                ? (d.Reference.Contains("-") ? d.Reference.Substring(0, d.Reference.IndexOf("-")) : "Direct")
                : "Direct")
            .Select(g => new { Source = g.Key, Count = g.Count(), Total = g.Sum(d => d.TotalAmount) })
            .OrderByDescending(x => x.Total)
            .ToListAsync();

        var grandTotal = data.Sum(d => d.Total);
        return data.Select(d => new RevenueBySourceItem(
            d.Source, d.Count, d.Total,
            grandTotal > 0 ? Math.Round(d.Total / grandTotal * 100, 2) : 0)).ToList();
    }

    public async Task<DepositSummaryResponse> GetDepositSummaryAsync(Guid companyId, DateTime? from, DateTime? to)
    {
        var fromDate = from ?? new DateTime(DateTime.UtcNow.Year, 1, 1);
        var toDate = to ?? DateTime.UtcNow;

        // Look for deposit-related journal entries (account code 215xx = เงินมัดจำรับล่วงหน้า)
        var depositReceived = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fromDate
                && l.JournalEntry.EntryDate <= toDate
                && l.Account.AccountCode.StartsWith("215"))
            .SumAsync(l => l.CreditAmount);

        var depositApplied = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fromDate
                && l.JournalEntry.EntryDate <= toDate
                && l.Account.AccountCode.StartsWith("215"))
            .SumAsync(l => l.DebitAmount);

        // Get recent deposit documents
        var details = await _db.Documents
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= fromDate && d.DocumentDate <= toDate
                && d.Notes != null && d.Notes.Contains("มัดจำ"))
            .OrderByDescending(d => d.DocumentDate)
            .Take(50)
            .Select(d => new DepositDetailItem(
                d.Id, d.DocumentNumber, d.Contact != null ? d.Contact.Name : null,
                d.TotalAmount, d.DocumentDate, d.Status.ToString()))
            .ToListAsync();

        return new DepositSummaryResponse(depositReceived, depositApplied, depositReceived - depositApplied, details);
    }

    public async Task<List<DailyRevenueItem>> GetDailyRevenueAsync(Guid companyId, DateTime? from, DateTime? to)
    {
        var fromDate = from ?? DateTime.UtcNow.Date.AddDays(-30);
        var toDate = to ?? DateTime.UtcNow.Date;

        // Get daily invoice totals
        var invoiceData = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.Status != DocumentStatus.Voided
                && d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .GroupBy(d => d.DocumentDate.Date)
            .Select(g => new { Date = g.Key, Count = g.Count(), Total = g.Sum(d => d.TotalAmount) })
            .ToListAsync();

        // Get daily revenue by account code prefix (generic — works for any industry)
        var journalData = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fromDate
                && l.JournalEntry.EntryDate <= toDate
                && l.Account.AccountType == AccountType.Revenue)
            .GroupBy(l => new { Date = l.JournalEntry.EntryDate.Date, CodePrefix = l.Account.AccountCode.Substring(0, 3), l.Account.AccountName })
            .Select(g => new { g.Key.Date, g.Key.CodePrefix, g.Key.AccountName, Amount = g.Sum(l => l.CreditAmount - l.DebitAmount) })
            .ToListAsync();

        var result = new List<DailyRevenueItem>();
        for (var d = fromDate; d <= toDate; d = d.AddDays(1))
        {
            var inv = invoiceData.FirstOrDefault(x => x.Date == d);
            var dayCategories = journalData
                .Where(x => x.Date == d)
                .GroupBy(x => x.CodePrefix)
                .Select(g => new DailyRevenueCategoryItem(g.Key, g.First().AccountName, g.Sum(x => x.Amount)))
                .ToList();

            var totalRevenue = dayCategories.Sum(c => c.Amount);
            result.Add(new DailyRevenueItem(d, inv?.Total ?? totalRevenue, inv?.Count ?? 0, dayCategories));
        }

        return result;
    }

    // ===== New Generic Inbound Processing =====

    public async Task<InboundSyncResponse> ProcessExpenseAsync(Guid companyId, Guid integrationId, InboundExpenseRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "expense.created", request.ExternalId, request.ExternalRef);

        try
        {
            // Resolve supplier contact
            Contact? supplier = null;
            if (!string.IsNullOrEmpty(request.SupplierTaxId))
                supplier = await _db.Set<Contact>().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.TaxId == request.SupplierTaxId && !c.IsDeleted);
            if (supplier == null && !string.IsNullOrEmpty(request.SupplierName))
                supplier = await _db.Set<Contact>().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Name == request.SupplierName && !c.IsDeleted);

            if (supplier == null)
            {
                supplier = new Contact
                {
                    CompanyId = companyId,
                    Name = request.SupplierName ?? "ผู้จำหน่ายทั่วไป",
                    TaxId = request.SupplierTaxId,
                    IsCustomer = false,
                    IsActive = true
                };
                _db.Set<Contact>().Add(supplier);
                await _db.SaveChangesAsync();
            }

            var docNumber = await _settingsService.GetNextNumberAsync(companyId, DocumentType.Expense);
            var vatRate = request.VatRate ?? 7m;
            var lines = BuildDocumentLines(request.Lines, vatRate);
            var subTotal = lines.Sum(l => l.Amount);
            var totalVat = lines.Sum(l => l.VatAmount);
            var totalAmount = request.IncludeVat ? subTotal : subTotal + totalVat;

            var document = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = DocumentType.Expense,
                Status = DocumentStatus.Approved,
                DocumentDate = request.DocumentDate,
                DueDate = request.DueDate ?? request.DocumentDate.AddDays(30),
                ContactId = supplier.Id,
                Reference = request.ExternalRef,
                SubTotal = subTotal,
                VatAmount = totalVat,
                TotalAmount = totalAmount,
                BalanceDue = totalAmount,
                Notes = request.Notes,
                Lines = lines
            };

            _db.Documents.Add(document);
            await _db.SaveChangesAsync();

            var journalEntryId = await CreateJournalFromMappingsAsync(companyId, integrationId, document, "expense");

            log.Status = "Success";
            log.CreatedDocumentId = document.Id;
            log.CreatedContactId = supplier.Id;
            log.CreatedJournalEntryId = journalEntryId;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Expense created", document.Id, supplier.Id, journalEntryId, null, docNumber);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    public async Task<InboundSyncResponse> ProcessProductAsync(Guid companyId, Guid integrationId, InboundProductRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "product.sync", request.ExternalId, request.Code);

        try
        {
            // Find existing product by code
            var product = await _db.Set<Product>()
                .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Code == request.Code && !p.IsDeleted);

            if (product == null)
            {
                product = new Product
                {
                    CompanyId = companyId,
                    Code = request.Code,
                    Name = request.Name,
                    Unit = request.Unit ?? "หน่วย",
                    SellingPrice = request.Price ?? 0,
                    CostPrice = request.CostPrice ?? 0,
                    IsActive = request.IsActive ?? true
                };
                _db.Set<Product>().Add(product);
            }
            else
            {
                product.Name = request.Name;
                if (request.Unit != null) product.Unit = request.Unit;
                if (request.Price.HasValue) product.SellingPrice = request.Price.Value;
                if (request.CostPrice.HasValue) product.CostPrice = request.CostPrice.Value;
                if (request.IsActive.HasValue) product.IsActive = request.IsActive.Value;
            }

            await _db.SaveChangesAsync();

            log.Status = "Success";
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Product synced", null, null, null, null, product.Code);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    public async Task<InboundSyncResponse> ProcessJournalAsync(Guid companyId, Guid integrationId, InboundJournalRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "journal.created", request.ExternalId, request.ExternalRef);

        try
        {
            var journalLines = new List<JournalEntryLine>();
            int lineOrder = 1;

            foreach (var line in request.Lines)
            {
                var account = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == line.AccountCode && a.IsActive);

                if (account == null)
                    throw new KeyNotFoundException($"ไม่พบผังบัญชี: {line.AccountCode}");

                journalLines.Add(new JournalEntryLine
                {
                    AccountId = account.Id,
                    DebitAmount = line.DebitAmount,
                    CreditAmount = line.CreditAmount,
                    Description = line.Description ?? request.Description,
                    LineOrder = lineOrder++
                });
            }

            if (journalLines.Sum(l => l.DebitAmount) != journalLines.Sum(l => l.CreditAmount))
                throw new InvalidOperationException("ยอดเดบิตไม่เท่ากับเครดิต");

            var journalType = request.JournalType?.ToLower() switch
            {
                "sales" => JournalType.Sales,
                "purchase" => JournalType.Purchase,
                "cashreceipts" or "cash_receipts" => JournalType.CashReceipts,
                "cashpayments" or "cash_payments" => JournalType.CashPayments,
                _ => JournalType.General
            };

            var jeCount = await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId);
            var je = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = $"JV-INT-{DateTime.UtcNow:yyyyMM}-{(jeCount + 1):D4}",
                EntryDate = request.EntryDate,
                JournalType = journalType,
                Description = request.Description ?? "บันทึกจากระบบภายนอก",
                Reference = request.ExternalRef,
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                TotalDebit = journalLines.Sum(l => l.DebitAmount),
                TotalCredit = journalLines.Sum(l => l.CreditAmount),
                Lines = journalLines
            };

            _db.JournalEntries.Add(je);
            await _db.SaveChangesAsync();

            log.Status = "Success";
            log.CreatedJournalEntryId = je.Id;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Journal entry created", null, null, je.Id, null, je.EntryNumber);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    public async Task<InboundBatchResponse> ProcessBatchAsync(Guid companyId, Guid integrationId, InboundBatchRequest request)
    {
        var results = new List<BatchResultItem>();
        int success = 0, errors = 0;

        // Process customers
        if (request.Customers != null)
        {
            foreach (var c in request.Customers)
            {
                var r = await ProcessCustomerAsync(companyId, integrationId, c);
                results.Add(new BatchResultItem("Customer", c.Name, r.Success, r.Message, r.ContactId, null));
                if (r.Success) success++; else errors++;
            }
        }

        // Process invoices
        if (request.Invoices != null)
        {
            foreach (var inv in request.Invoices)
            {
                var r = await ProcessInvoiceAsync(companyId, integrationId, inv);
                results.Add(new BatchResultItem("Invoice", inv.ExternalRef, r.Success, r.Message, r.DocumentId, r.DocumentNumber));
                if (r.Success) success++; else errors++;
            }
        }

        // Process payments
        if (request.Payments != null)
        {
            foreach (var p in request.Payments)
            {
                var r = await ProcessPaymentAsync(companyId, integrationId, p);
                results.Add(new BatchResultItem("Payment", p.ExternalRef, r.Success, r.Message, r.PaymentId, r.DocumentNumber));
                if (r.Success) success++; else errors++;
            }
        }

        // Process expenses
        if (request.Expenses != null)
        {
            foreach (var e in request.Expenses)
            {
                var r = await ProcessExpenseAsync(companyId, integrationId, e);
                results.Add(new BatchResultItem("Expense", e.ExternalRef, r.Success, r.Message, r.DocumentId, r.DocumentNumber));
                if (r.Success) success++; else errors++;
            }
        }

        // Process products
        if (request.Products != null)
        {
            foreach (var p in request.Products)
            {
                var r = await ProcessProductAsync(companyId, integrationId, p);
                results.Add(new BatchResultItem("Product", p.Code, r.Success, r.Message, null, r.DocumentNumber));
                if (r.Success) success++; else errors++;
            }
        }

        // Process journals
        if (request.Journals != null)
        {
            foreach (var j in request.Journals)
            {
                var r = await ProcessJournalAsync(companyId, integrationId, j);
                results.Add(new BatchResultItem("Journal", j.ExternalRef, r.Success, r.Message, r.JournalEntryId, r.DocumentNumber));
                if (r.Success) success++; else errors++;
            }
        }

        return new InboundBatchResponse(success + errors, success, errors, results);
    }

    // ===== Outbound Data (external systems read FROM Next Acc) =====

    public async Task<OutboundPagedResponse<OutboundDocumentResponse>> GetDocumentsForExternalAsync(Guid companyId, OutboundQueryParams query)
    {
        var q = _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted);

        if (query.FromDate.HasValue) q = q.Where(d => d.DocumentDate >= query.FromDate.Value);
        if (query.ToDate.HasValue) q = q.Where(d => d.DocumentDate <= query.ToDate.Value);
        if (!string.IsNullOrEmpty(query.Status) && Enum.TryParse<DocumentStatus>(query.Status, true, out var status))
            q = q.Where(d => d.Status == status);
        if (!string.IsNullOrEmpty(query.Type) && Enum.TryParse<DocumentType>(query.Type, true, out var docType))
            q = q.Where(d => d.DocumentType == docType);

        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(d => d.DocumentDate)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(d => new OutboundDocumentResponse(
                d.Id, d.DocumentNumber, d.DocumentType.ToString(), d.Status.ToString(),
                d.DocumentDate, d.DueDate,
                d.Contact != null ? d.Contact.Name : null, d.Contact != null ? d.Contact.TaxId : null,
                d.SubTotal, d.VatAmount, d.TotalAmount, d.PaidAmount, d.BalanceDue,
                d.Reference, d.Notes,
                d.Lines.Select(l => new OutboundDocumentLineResponse(
                    l.ProductCode, l.Description, l.Quantity, l.Unit, l.UnitPrice,
                    l.DiscountAmount, l.Amount, l.VatRate, l.VatAmount)).ToList(),
                d.CreatedAt))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)total / query.PageSize);
        return new OutboundPagedResponse<OutboundDocumentResponse>(items, total, query.Page, query.PageSize, totalPages);
    }

    public async Task<OutboundPagedResponse<OutboundContactResponse>> GetContactsForExternalAsync(Guid companyId, OutboundQueryParams query)
    {
        var q = _db.Set<Contact>()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted);

        if (!string.IsNullOrEmpty(query.Type))
        {
            if (query.Type.Equals("customer", StringComparison.OrdinalIgnoreCase))
                q = q.Where(c => c.IsCustomer);
            else if (query.Type.Equals("supplier", StringComparison.OrdinalIgnoreCase))
                q = q.Where(c => !c.IsCustomer);
        }

        var total = await q.CountAsync();
        var items = await q
            .OrderBy(c => c.Name)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(c => new OutboundContactResponse(
                c.Id, c.Name, c.TaxId, c.BranchCode,
                c.ContactType.ToString(), c.IsCustomer, c.IsSupplier,
                c.Address, c.Phone, c.Email, c.CreatedAt))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)total / query.PageSize);
        return new OutboundPagedResponse<OutboundContactResponse>(items, total, query.Page, query.PageSize, totalPages);
    }

    public async Task<OutboundPagedResponse<OutboundPaymentResponse>> GetPaymentsForExternalAsync(Guid companyId, OutboundQueryParams query)
    {
        var q = _db.Set<Payment>()
            .Include(p => p.Document)
            .Where(p => p.CompanyId == companyId && !p.IsDeleted);

        if (query.FromDate.HasValue) q = q.Where(p => p.PaymentDate >= query.FromDate.Value);
        if (query.ToDate.HasValue) q = q.Where(p => p.PaymentDate <= query.ToDate.Value);

        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(p => p.PaymentDate)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(p => new OutboundPaymentResponse(
                p.Id, p.PaymentNumber, p.DocumentId, p.Document != null ? p.Document.DocumentNumber : null,
                p.PaymentDate, p.Amount, p.PaymentMethod.ToString(),
                p.Reference, p.Notes, p.CreatedAt))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)total / query.PageSize);
        return new OutboundPagedResponse<OutboundPaymentResponse>(items, total, query.Page, query.PageSize, totalPages);
    }

    public async Task<List<OutboundAccountBalanceResponse>> GetAccountBalancesForExternalAsync(Guid companyId)
    {
        return await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted)
            .Select(a => new OutboundAccountBalanceResponse(
                a.AccountCode, a.AccountName, a.AccountType.ToString(),
                _db.JournalEntryLines
                    .Where(l => l.AccountId == a.Id && l.JournalEntry.Status == JournalEntryStatus.Posted)
                    .Sum(l => l.DebitAmount),
                _db.JournalEntryLines
                    .Where(l => l.AccountId == a.Id && l.JournalEntry.Status == JournalEntryStatus.Posted)
                    .Sum(l => l.CreditAmount),
                _db.JournalEntryLines
                    .Where(l => l.AccountId == a.Id && l.JournalEntry.Status == JournalEntryStatus.Posted)
                    .Sum(l => l.DebitAmount - l.CreditAmount)))
            .OrderBy(a => a.AccountCode)
            .ToListAsync();
    }

    // ===== Mapping Templates =====

    public List<MappingTemplateResponse> GetMappingTemplates()
    {
        return new List<MappingTemplateResponse>
        {
            new("hotel", "โรงแรม / ที่พัก", new List<MappingTemplateItem>
            {
                new("ROOM_REVENUE", "รายได้ค่าห้องพัก", "113", "411"),
                new("F&B_REVENUE", "รายได้อาหาร/เครื่องดื่ม", "113", "412"),
                new("SPA_REVENUE", "รายได้สปา", "113", "413"),
                new("LAUNDRY_REVENUE", "รายได้ซักรีด", "113", "414"),
                new("DEPOSIT_RECEIVED", "มัดจำรับ", "111", "215"),
                new("MINIBAR_REVENUE", "รายได้มินิบาร์", "113", "412"),
                new("TRANSPORT_REVENUE", "รายได้รถรับส่ง", "113", "419")
            }),
            new("restaurant", "ร้านอาหาร / คาเฟ่", new List<MappingTemplateItem>
            {
                new("FOOD_REVENUE", "รายได้อาหาร", "111", "411"),
                new("BEVERAGE_REVENUE", "รายได้เครื่องดื่ม", "111", "412"),
                new("DELIVERY_REVENUE", "รายได้เดลิเวอรี่", "113", "413"),
                new("SERVICE_CHARGE", "ค่าบริการ", "111", "419"),
                new("TIPS", "ทิปส์", "111", "419")
            }),
            new("retail", "ร้านค้าปลีก", new List<MappingTemplateItem>
            {
                new("PRODUCT_SALES", "รายได้ขายสินค้า", "113", "411"),
                new("SHIPPING_INCOME", "รายได้ค่าส่ง", "111", "419"),
                new("REFUND", "คืนเงิน", "411", "113"),
                new("DISCOUNT", "ส่วนลด", "411", "113")
            }),
            new("ecommerce", "E-Commerce / ออนไลน์", new List<MappingTemplateItem>
            {
                new("PRODUCT_SALES", "รายได้ขายสินค้า", "113", "411"),
                new("SHIPPING_INCOME", "รายได้ค่าจัดส่ง", "111", "419"),
                new("PLATFORM_FEE", "ค่าธรรมเนียมแพลตฟอร์ม", "519", "112"),
                new("REFUND", "คืนเงิน", "411", "113"),
                new("COD_RECEIVED", "รับเงิน COD", "111", "113")
            }),
            new("service", "ธุรกิจบริการ", new List<MappingTemplateItem>
            {
                new("SERVICE_REVENUE", "รายได้ค่าบริการ", "113", "411"),
                new("CONSULTING_FEE", "รายได้ที่ปรึกษา", "113", "411"),
                new("RETAINER_FEE", "ค่ารักษาสิทธิ์", "113", "411"),
                new("DEPOSIT_RECEIVED", "มัดจำรับ", "111", "215"),
                new("EXPENSE_REIMBURSEMENT", "เบิกคืนค่าใช้จ่าย", "113", "419")
            }),
            new("clinic", "คลินิก / โรงพยาบาล", new List<MappingTemplateItem>
            {
                new("TREATMENT_REVENUE", "รายได้ค่ารักษา", "113", "411"),
                new("MEDICINE_REVENUE", "รายได้ค่ายา", "113", "412"),
                new("LAB_REVENUE", "รายได้ค่าแล็บ", "113", "413"),
                new("MEDICAL_SUPPLY", "เวชภัณฑ์", "113", "414"),
                new("DEPOSIT_RECEIVED", "มัดจำรับ", "111", "215")
            }),
            new("general", "ทั่วไป", new List<MappingTemplateItem>
            {
                new("REVENUE", "รายได้", "113", "411"),
                new("EXPENSE", "ค่าใช้จ่าย", "511", "112"),
                new("PAYMENT_RECEIVED", "รับชำระเงิน", "111", "113"),
                new("PAYMENT_MADE", "จ่ายชำระเงิน", "211", "112"),
                new("DEPOSIT_RECEIVED", "มัดจำรับ", "111", "215")
            })
        };
    }

    private static ContactType ParseContactType(string? type) => type?.ToLower() switch
    {
        "juristicperson" or "juristic" or "company" => ContactType.JuristicPerson,
        "government" => ContactType.GovernmentAgency,
        _ => ContactType.Individual
    };

    private static PaymentMethod ParsePaymentMethod(string? method) => method?.ToLower() switch
    {
        "cash" => PaymentMethod.Cash,
        "banktransfer" or "transfer" => PaymentMethod.BankTransfer,
        "creditcard" or "credit_card" => PaymentMethod.CreditCard,
        "cheque" or "check" => PaymentMethod.Cheque,
        "promptpay" or "prompt_pay" => PaymentMethod.PromptPay,
        "ewallet" or "e_wallet" => PaymentMethod.EWallet,
        _ => PaymentMethod.Other
    };
}
