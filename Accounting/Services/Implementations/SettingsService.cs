using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs.Settings;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class SettingsService : ISettingsService
{
    private readonly AccountingDbContext _db;

    public SettingsService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<CompanySettingsResponse> GetSettingsAsync(Guid companyId)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);
        return MapToResponse(companyId, settings);
    }

    public async Task<CompanySettingsResponse> UpdateSettingsAsync(Guid companyId, UpdateCompanySettingsRequest request)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);

        if (request.PrimaryColor != null) settings.PrimaryColor = request.PrimaryColor;
        if (request.SecondaryColor != null) settings.SecondaryColor = request.SecondaryColor;
        if (request.DefaultPaymentTerms != null) settings.DefaultPaymentTerms = request.DefaultPaymentTerms;
        if (request.DefaultPaymentDueDays.HasValue) settings.DefaultPaymentDueDays = request.DefaultPaymentDueDays.Value;
        if (request.InvoiceNotes != null) settings.InvoiceNotes = request.InvoiceNotes;
        if (request.ReceiptNotes != null) settings.ReceiptNotes = request.ReceiptNotes;
        if (request.QuotationNotes != null) settings.QuotationNotes = request.QuotationNotes;
        if (request.InvoiceFooter != null) settings.InvoiceFooter = request.InvoiceFooter;
        if (request.ReceiptFooter != null) settings.ReceiptFooter = request.ReceiptFooter;
        if (request.DefaultVatRate.HasValue) settings.DefaultVatRate = request.DefaultVatRate.Value;
        if (request.VatRegistered.HasValue) settings.VatRegistered = request.VatRegistered.Value;
        if (request.EmailFromName != null) settings.EmailFromName = request.EmailFromName;
        if (request.EmailReplyTo != null) settings.EmailReplyTo = request.EmailReplyTo;
        if (request.InvoiceEmailSubject != null) settings.InvoiceEmailSubject = request.InvoiceEmailSubject;
        if (request.InvoiceEmailBody != null) settings.InvoiceEmailBody = request.InvoiceEmailBody;
        if (request.RequireApprovalForDocuments.HasValue) settings.RequireApprovalForDocuments = request.RequireApprovalForDocuments.Value;
        if (request.ApprovalThresholdAmount.HasValue) settings.ApprovalThresholdAmount = request.ApprovalThresholdAmount.Value;
        if (request.AllowFreelanceAccess.HasValue) settings.AllowFreelanceAccess = request.AllowFreelanceAccess.Value;
        if (request.MaxFreelanceUsers.HasValue) settings.MaxFreelanceUsers = request.MaxFreelanceUsers.Value;
        if (request.RequireTwoFactorForFreelance.HasValue) settings.RequireTwoFactorForFreelance = request.RequireTwoFactorForFreelance.Value;
        if (request.EnableApiAccess.HasValue) settings.EnableApiAccess = request.EnableApiAccess.Value;
        if (request.MaxApiKeys.HasValue) settings.MaxApiKeys = request.MaxApiKeys.Value;
        if (request.AutoCloseMonthEnd.HasValue) settings.AutoCloseMonthEnd = request.AutoCloseMonthEnd.Value;
        if (request.MonthEndClosingDay.HasValue) settings.MonthEndClosingDay = request.MonthEndClosingDay.Value;
        if (request.PreventPostToClosedPeriod.HasValue) settings.PreventPostToClosedPeriod = request.PreventPostToClosedPeriod.Value;

        // e-Tax settings
        if (request.EtaxEnabled.HasValue) settings.EtaxEnabled = request.EtaxEnabled.Value;
        if (request.EtaxCertificatePath != null) settings.EtaxCertificatePath = request.EtaxCertificatePath;
        if (request.EtaxCertificatePassword != null) settings.EtaxCertificatePassword = request.EtaxCertificatePassword;
        if (request.EtaxRdApiKey != null) settings.EtaxRdApiKey = request.EtaxRdApiKey;
        if (request.EtaxRdApiSecret != null) settings.EtaxRdApiSecret = request.EtaxRdApiSecret;
        if (request.EtaxTestMode.HasValue) settings.EtaxTestMode = request.EtaxTestMode.Value;
        if (request.EtaxAutoSign.HasValue) settings.EtaxAutoSign = request.EtaxAutoSign.Value;
        if (request.EtaxAutoSubmit.HasValue) settings.EtaxAutoSubmit = request.EtaxAutoSubmit.Value;
        if (request.EtaxServiceProvider != null) settings.EtaxServiceProvider = request.EtaxServiceProvider;

        // Landing Page – Accounting Services
        if (request.LandingContactPhone != null) settings.LandingContactPhone = request.LandingContactPhone;
        if (request.LandingContactLine != null) settings.LandingContactLine = request.LandingContactLine;
        if (request.LandingContactEmail != null) settings.LandingContactEmail = request.LandingContactEmail;
        if (request.LandingServicesJson != null) settings.LandingServicesJson = request.LandingServicesJson;

        await _db.SaveChangesAsync();
        return MapToResponse(companyId, settings);
    }

    // ===== Logo Management =====

    public async Task<CompanySettingsResponse> UploadLogoAsync(Guid companyId, Stream fileStream, string fileName, string contentType)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);

        // Validate content type
        var allowedTypes = new[] { "image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml" };
        if (!allowedTypes.Contains(contentType.ToLower()))
            throw new InvalidOperationException("รองรับเฉพาะไฟล์ PNG, JPEG, GIF, WebP, SVG เท่านั้น");

        // Create upload directory
        var uploadDir = Path.Combine("uploads", "logos", companyId.ToString());
        Directory.CreateDirectory(uploadDir);

        // Delete old logo if exists
        if (!string.IsNullOrEmpty(settings.LogoPath) && File.Exists(settings.LogoPath))
            File.Delete(settings.LogoPath);

        // Save new logo
        var ext = Path.GetExtension(fileName);
        var savedFileName = $"logo_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
        var filePath = Path.Combine(uploadDir, savedFileName);

        using (var fs = new FileStream(filePath, FileMode.Create))
            await fileStream.CopyToAsync(fs);

        settings.LogoPath = filePath;
        settings.LogoUrl = $"/uploads/logos/{companyId}/{savedFileName}";
        await _db.SaveChangesAsync();

        return MapToResponse(companyId, settings);
    }

    public async Task DeleteLogoAsync(Guid companyId)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);
        if (!string.IsNullOrEmpty(settings.LogoPath) && File.Exists(settings.LogoPath))
            File.Delete(settings.LogoPath);
        settings.LogoPath = null;
        settings.LogoUrl = null;
        await _db.SaveChangesAsync();
    }

    // ===== Number Series =====

    public async Task<NumberSeriesResponse> CreateNumberSeriesAsync(Guid companyId, CreateNumberSeriesRequest request)
    {
        var existing = await _db.Set<NumberSeries>()
            .AnyAsync(n => n.CompanyId == companyId && n.DocumentType == request.DocumentType && n.IsActive);
        if (existing)
            throw new InvalidOperationException("มี number series สำหรับประเภทเอกสารนี้อยู่แล้ว");

        var series = new NumberSeries
        {
            CompanyId = companyId,
            DocumentType = request.DocumentType,
            Prefix = request.Prefix,
            Suffix = request.Suffix,
            Format = request.Format,
            CurrentNumber = request.StartNumber - 1,
            ResetPeriod = request.ResetPeriod
        };

        _db.Set<NumberSeries>().Add(series);
        await _db.SaveChangesAsync();

        return MapSeriesToResponse(series);
    }

    public async Task<List<NumberSeriesResponse>> GetNumberSeriesAsync(Guid companyId)
    {
        var series = await _db.Set<NumberSeries>()
            .Where(n => n.CompanyId == companyId)
            .OrderBy(n => n.DocumentType)
            .ToListAsync();

        return series.Select(MapSeriesToResponse).ToList();
    }

    public async Task<NumberSeriesResponse> UpdateNumberSeriesAsync(Guid companyId, Guid seriesId, UpdateNumberSeriesRequest request)
    {
        var series = await _db.Set<NumberSeries>()
            .FirstOrDefaultAsync(n => n.Id == seriesId && n.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ number series");

        if (request.Prefix != null) series.Prefix = request.Prefix;
        if (request.Suffix != null) series.Suffix = request.Suffix;
        if (request.Format != null) series.Format = request.Format;
        if (request.CurrentNumber.HasValue) series.CurrentNumber = request.CurrentNumber.Value;
        if (request.ResetPeriod.HasValue) series.ResetPeriod = request.ResetPeriod.Value;
        if (request.IsActive.HasValue) series.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        return MapSeriesToResponse(series);
    }

    public async Task<string> GetNextNumberAsync(Guid companyId, DocumentType documentType)
    {
        var series = await _db.Set<NumberSeries>()
            .FirstOrDefaultAsync(n => n.CompanyId == companyId && n.DocumentType == documentType && n.IsActive);

        if (series == null)
        {
            // Fallback to default pattern
            var prefix = documentType switch
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
            var yearMonth = DateTime.UtcNow.ToString("yyyyMM");
            var pattern = $"{prefix}-{yearMonth}-";
            var lastDoc = await _db.Documents
                .Where(d => d.CompanyId == companyId && d.DocumentType == documentType && d.DocumentNumber.StartsWith(pattern))
                .OrderByDescending(d => d.DocumentNumber)
                .Select(d => d.DocumentNumber)
                .FirstOrDefaultAsync();
            int nextSeq = 1;
            if (lastDoc != null)
            {
                var lastPart = lastDoc[pattern.Length..];
                if (int.TryParse(lastPart, out var lastNum))
                    nextSeq = lastNum + 1;
            }
            return $"{pattern}{nextSeq:D4}";
        }

        // Check if reset is needed
        var now = DateTime.UtcNow;
        if (series.ResetPeriod > 0 && series.LastResetDate.HasValue)
        {
            var monthsSinceReset = (now.Year - series.LastResetDate.Value.Year) * 12 + (now.Month - series.LastResetDate.Value.Month);
            if (monthsSinceReset >= series.ResetPeriod)
            {
                series.CurrentNumber = 0;
                series.LastResetDate = now;
            }
        }

        series.CurrentNumber++;

        var result = series.Format
            .Replace("{PREFIX}", series.Prefix)
            .Replace("{SUFFIX}", series.Suffix ?? "")
            .Replace("{YYYY}", now.ToString("yyyy"))
            .Replace("{YY}", now.ToString("yy"))
            .Replace("{MM}", now.ToString("MM"))
            .Replace("{DD}", now.ToString("dd"));

        // Handle sequence with padding: {SEQ:4} → 0001
        var seqPattern = System.Text.RegularExpressions.Regex.Match(result, @"\{SEQ:(\d+)\}");
        if (seqPattern.Success)
        {
            var padding = int.Parse(seqPattern.Groups[1].Value);
            result = result.Replace(seqPattern.Value, series.CurrentNumber.ToString($"D{padding}"));
        }
        else
        {
            result = result.Replace("{SEQ}", series.CurrentNumber.ToString());
        }

        await _db.SaveChangesAsync();
        return result;
    }

    // ===== API Key Management =====

    public async Task<ApiKeyCreatedResponse> CreateApiKeyAsync(Guid companyId, Guid userId, CreateApiKeyRequest request)
    {
        var settings = await GetOrCreateSettingsAsync(companyId);
        if (!settings.EnableApiAccess)
            throw new InvalidOperationException("API access ยังไม่เปิดใช้งาน กรุณาเปิดในการตั้งค่า");

        var currentKeys = await _db.Set<ApiKey>()
            .CountAsync(k => k.CompanyId == companyId && k.Status == ApiKeyStatus.Active);
        if (currentKeys >= settings.MaxApiKeys)
            throw new InvalidOperationException($"จำนวน API key สูงสุด ({settings.MaxApiKeys}) เต็มแล้ว");

        // Generate raw key
        var rawKey = $"acc_{Convert.ToBase64String(Guid.NewGuid().ToByteArray())}{Convert.ToBase64String(Guid.NewGuid().ToByteArray())}".Replace("=", "").Replace("+", "").Replace("/", "");
        var keyPrefix = rawKey[..8];
        var keyHash = BCrypt.Net.BCrypt.HashPassword(rawKey);

        var apiKey = new ApiKey
        {
            CompanyId = companyId,
            CreatedByUserId = userId,
            Name = request.Name,
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            ExpiresAt = request.ExpiresAt,
            AllowedFeatures = request.AllowedFeatures,
            AllowedIpAddresses = request.AllowedIpAddresses,
            RateLimitPerMinute = request.RateLimitPerMinute,
            CanRead = request.CanRead,
            CanWrite = request.CanWrite,
            CanDelete = request.CanDelete
        };

        _db.Set<ApiKey>().Add(apiKey);
        await _db.SaveChangesAsync();

        // Return raw key only this one time
        return new ApiKeyCreatedResponse(apiKey.Id, apiKey.Name, rawKey, keyPrefix, apiKey.CreatedAt);
    }

    public async Task<List<ApiKeyResponse>> GetApiKeysAsync(Guid companyId)
    {
        var keys = await _db.Set<ApiKey>()
            .Where(k => k.CompanyId == companyId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();

        return keys.Select(k => new ApiKeyResponse(
            k.Id, k.Name, k.KeyPrefix, k.Status, k.ExpiresAt, k.LastUsedAt,
            k.AllowedFeatures, k.CanRead, k.CanWrite, k.CanDelete, k.CreatedAt)).ToList();
    }

    public async Task RevokeApiKeyAsync(Guid companyId, Guid apiKeyId)
    {
        var key = await _db.Set<ApiKey>()
            .FirstOrDefaultAsync(k => k.Id == apiKeyId && k.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ API key");

        key.Status = ApiKeyStatus.Revoked;
        await _db.SaveChangesAsync();
    }

    // ===== Helpers =====

    private async Task<CompanySettings> GetOrCreateSettingsAsync(Guid companyId)
    {
        var settings = await _db.Set<CompanySettings>().FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (settings == null)
        {
            settings = new CompanySettings { CompanyId = companyId };
            _db.Set<CompanySettings>().Add(settings);
            await _db.SaveChangesAsync();
        }
        return settings;
    }

    public async Task<LandingServicesResponse?> GetLandingServicesAsync()
    {
        // Get the first company's settings (for single-tenant landing page)
        var settings = await _db.Set<CompanySettings>()
            .Where(s => !s.IsDeleted && s.LandingServicesJson != null)
            .FirstOrDefaultAsync();

        if (settings == null)
            return null;

        var services = new List<LandingServiceItem>();
        if (!string.IsNullOrEmpty(settings.LandingServicesJson))
        {
            try
            {
                services = JsonSerializer.Deserialize<List<LandingServiceItem>>(
                    settings.LandingServicesJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            catch { /* invalid JSON */ }
        }

        return new LandingServicesResponse(
            settings.LandingContactPhone,
            settings.LandingContactLine,
            settings.LandingContactEmail,
            services);
    }

    private static CompanySettingsResponse MapToResponse(Guid companyId, CompanySettings s) => new(
        companyId, s.LogoUrl, s.PrimaryColor, s.SecondaryColor,
        s.DefaultPaymentTerms, s.DefaultPaymentDueDays,
        // Document notes/footer
        s.InvoiceNotes, s.ReceiptNotes, s.QuotationNotes, s.InvoiceFooter, s.ReceiptFooter,
        // Email
        s.EmailFromName, s.EmailReplyTo, s.InvoiceEmailSubject, s.InvoiceEmailBody,
        // Tax
        s.DefaultVatRate, s.VatRegistered,
        // Security
        s.RequireApprovalForDocuments, s.ApprovalThresholdAmount,
        s.AllowFreelanceAccess, s.MaxFreelanceUsers,
        s.EnableApiAccess, s.MaxApiKeys,
        // Closing
        s.AutoCloseMonthEnd, s.MonthEndClosingDay, s.PreventPostToClosedPeriod,
        // e-Tax
        s.EtaxEnabled, s.EtaxTestMode, s.EtaxAutoSign, s.EtaxAutoSubmit,
        s.EtaxServiceProvider,
        !string.IsNullOrEmpty(s.EtaxCertificatePath),
        !string.IsNullOrEmpty(s.EtaxRdApiKey),
        // Landing Page
        s.LandingContactPhone, s.LandingContactLine, s.LandingContactEmail,
        s.LandingServicesJson);

    private static NumberSeriesResponse MapSeriesToResponse(NumberSeries n) => new(
        n.Id, n.DocumentType, n.Prefix, n.Suffix, n.Format,
        n.CurrentNumber, n.ResetPeriod, n.IsActive);
}
