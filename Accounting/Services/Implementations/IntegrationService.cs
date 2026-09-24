using System.Diagnostics;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Integration;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class IntegrationService : IIntegrationService
{
    /// <summary>Cap on the partner-supplied preparer signature (data URI or
    /// raw base64). A typical PNG signature is &lt;30KB; we allow up to 200KB
    /// to be generous. Without this, a partner could ship multi-MB images on
    /// every doc and silently bloat the Documents table.</summary>
    private const int PreparerSignatureMaxBytes = 200_000;

    /// <summary>Trim + cap the partner-supplied preparer signature. Anything
    /// over the cap is rejected (throws a 400-equivalent to the caller).</summary>
    private static string? TrimPreparerSignature(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length > PreparerSignatureMaxBytes)
            throw new InvalidOperationException(
                $"PreparerSignatureBase64 ใหญ่เกินกำหนด ({trimmed.Length:N0} > {PreparerSignatureMaxBytes:N0} bytes) — กรุณาบีบอัดรูปลายเซ็นให้เล็กลง");
        return trimmed;
    }

    private readonly AccountingDbContext _db;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<IntegrationService> _logger;
    private readonly Accounting.Services.Implementations.Ocr.VendorIntelligenceService _vendorIntel;
    private readonly IDocumentService? _documentService;
    private readonly IWithholdingTaxCertService? _whtCertService;
    private readonly ISecretProtector _secrets;
    private readonly Accounting.Services.Interfaces.IDbdLookupService? _dbd;
    // AI fallback สำหรับเลือกผังบัญชี GL — เมื่อระบบภายนอกส่ง line.AccountCode
    // มาว่าง/หาไม่เจอใน chart, เรียก local distillation model ก่อน (student-first)
    // แล้ว AI teacher fallback ผ่าน orchestrator. ใช้ feature key GlAccountSuggestion
    // ตัวเดียวกับ OCR → cross-channel learning (feedback ฝั่ง OCR ช่วย integration).
    private readonly Accounting.Services.Ai.IOcrAiAugmenter? _glAi;

    public IntegrationService(AccountingDbContext db, ISettingsService settingsService, ILogger<IntegrationService> logger,
        Accounting.Services.Implementations.Ocr.VendorIntelligenceService vendorIntel,
        ISecretProtector secrets,
        IDocumentService? documentService = null,
        IWithholdingTaxCertService? whtCertService = null,
        Accounting.Services.Interfaces.IDbdLookupService? dbd = null,
        Accounting.Services.Ai.IOcrAiAugmenter? glAi = null)
    {
        _db = db;
        _settingsService = settingsService;
        _logger = logger;
        _vendorIntel = vendorIntel;
        _secrets = secrets;
        _documentService = documentService;
        _whtCertService = whtCertService;
        _dbd = dbd;
        _glAi = glAi;
    }

    // ===== Helper: Atomic Journal Entry Number =====

    /// <summary>
    /// เลข JE ของเส้น integration — **เดินผ่านเครื่องออกเลขตัวเดียวของระบบ**
    ///
    /// <para>เดิมเมธอดนี้ออกเลขเอง (<c>OrderByDescending(EntryNumber).First()+1</c>)
    /// ⇒ ไม่มีล็อก · เรียงแบบข้อความ (เลขทะลุ 9999 แล้ววนกลับไปทับ) · ไม่นับ JE
    /// ที่ยังค้างใน change tracker ⇒ ชน unique index แบบสุ่ม (ผลตรวจ F-08)</para>
    /// </summary>
    private Task<string> GetNextJournalNumberAsync(Guid companyId, string prefix = "JV-INT")
        => Journal.JournalEntryBuilder.NextJournalNumberAsync(_db, companyId, prefix, DateTime.UtcNow);

    /// <summary>เลขใบรับ-จ่ายเงินของเส้น integration — ล็อก + integer-max ผ่าน
    /// <c>Helpers.SequenceNumber</c> (เหตุผลเดียวกับเมธอดข้างบน)</summary>
    private async Task<string> GetNextPaymentNumberAsync(Guid companyId)
    {
        var pattern = $"PAY-{DateTime.UtcNow:yyyyMM}-";
        return await Accounting.Helpers.SequenceNumber.NextAsync(
            _db, companyId, Accounting.Helpers.AdvisoryLockKey.PaymentSequence, pattern,
            _db.Set<Payment>().IgnoreQueryFilters()
                .Where(x => x.CompanyId == companyId && x.PaymentNumber.StartsWith(pattern))
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => x.PaymentNumber),
            _db.Set<Payment>().Local.Select(x => x.PaymentNumber));
    }

    private static DateTime NormalizeDate(DateTime date)
    {
        // แปลง พ.ศ.↔ค.ศ. ก่อน
        if (date.Year < 1900)
            date = date.AddYears(543);
        else if (date.Year > 2400)
            date = new DateTime(date.Year - 543, date.Month, date.Day, date.Hour, date.Minute, date.Second, date.Kind);
        // แล้ว anchor เป็น "วันที่ไทย ณ 00:00 UTC" — กัน timestamptz shift
        return Accounting.Helpers.ThaiDate.CalendarDateUtc(date);
    }

    /// <summary>Batch-load chart of accounts by codes in one query instead of N+1.</summary>
    private async Task<Dictionary<string, ChartOfAccount>> BatchLoadAccountsByCodesAsync(Guid companyId, IEnumerable<string> accountCodes)
    {
        var codes = accountCodes.Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
        if (codes.Count == 0) return new Dictionary<string, ChartOfAccount>();

        return await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && codes.Contains(a.AccountCode) && a.IsActive)
            .ToDictionaryAsync(a => a.AccountCode, a => a);
    }

    // ===== Integration Config =====

    public async Task<List<IntegrationResponse>> GetIntegrationsAsync(Guid companyId)
    {
        var rows = await _db.Set<ExternalIntegration>().AsNoTracking()
            .Where(i => i.CompanyId == companyId && !i.IsDeleted)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
        var now = DateTime.UtcNow;
        return rows.Select(i => ToResponse(i, now)).ToList();
    }

    /// <summary>ตัวสร้าง <see cref="IntegrationResponse"/> ตัวเดียว (list + update) — สิทธิ์ที่บังคับใช้จริง
    /// คิดจาก <c>IntegrationKeyPolicy</c> ที่นี่ หน้าเว็บไม่ต้องรู้กติกาช่วงผ่อนผัน (รอบ 193 · G2-01)</summary>
    private static IntegrationResponse ToResponse(ExternalIntegration i, DateTime nowUtc)
    {
        var stored = new Accounting.Helpers.IntegrationKeyScopes(i.CanRead, i.CanWrite, i.CanDelete);
        var legacyActive = Accounting.Helpers.IntegrationKeyPolicy.IsLegacyPrivilegeActive(
            i.IsLegacyKey, i.LegacyDeprecatesAt, nowUtc);
        var effective = Accounting.Helpers.IntegrationKeyPolicy.EffectiveScopes(
            i.IsLegacyKey, i.LegacyDeprecatesAt, stored, nowUtc);
        return new IntegrationResponse(
            i.Id, i.SystemName, i.SystemType, i.SystemVersion, i.BaseUrl,
            i.ApiKeyPrefix, i.IsActive, i.LastSyncAt, i.TotalSyncCount, i.ErrorCount,
            i.RateLimitPerMinute, i.WebhookUrl, i.WebhookEnabled, i.CreatedAt,
            CanRead: i.CanRead, CanWrite: i.CanWrite, CanDelete: i.CanDelete,
            IsLegacyKey: i.IsLegacyKey, LegacyDeprecatesAt: i.LegacyDeprecatesAt,
            LegacyPrivilegeActive: legacyActive,
            EffectiveCanRead: effective.CanRead, EffectiveCanWrite: effective.CanWrite,
            EffectiveCanDelete: effective.CanDelete);
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

        // คีย์ที่ออกใหม่ = นโยบายใหม่เสมอ: ไม่ใช่รุ่นเก่า · สิทธิ์ที่ไม่ได้เลือก = อ่านอย่างเดียว (G2-01)
        var scopes = Accounting.Helpers.IntegrationKeyPolicy.ScopesForNewKey(
            request.CanRead, request.CanWrite, request.CanDelete);

        var integration = new ExternalIntegration
        {
            CompanyId = companyId,
            CanRead = scopes.CanRead,
            CanWrite = scopes.CanWrite,
            CanDelete = scopes.CanDelete,
            IsLegacyKey = false,
            LegacyDeprecatesAt = null,
            SystemName = request.SystemName,
            SystemType = request.SystemType,
            SystemVersion = request.SystemVersion,
            BaseUrl = request.BaseUrl,
            // The raw API key is never persisted — only the BCrypt hash + a
            // display prefix. Caller receives rawKey once in the response.
            ApiKey = "",
            ApiKeyHash = keyHash,
            ApiKeyPrefix = keyPrefix,
            // SecretKey is the HMAC signing secret for inbound integration
            // calls; the server has to be able to read it back, so encrypt at rest.
            SecretKey = _secrets.Protect(secretKey),
            RateLimitPerMinute = request.RateLimitPerMinute,
            WebhookUrl = request.WebhookUrl,
            WebhookEnabled = request.WebhookEnabled
        };

        _db.Set<ExternalIntegration>().Add(integration);
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

        // สิทธิ์ของคีย์ — ส่งมาอย่างน้อยหนึ่งช่อง = เจ้าของเลือกเองแล้ว ⇒ ย้ายเข้านโยบายใหม่ (G2-01)
        // ไม่ส่งเลย = ไม่แตะ (แก้ชื่อ/เปิดปิดต้องไม่ทำให้คีย์รุ่นเก่าเสียสิทธิ์เงียบ ๆ)
        var (scopes, scopesChanged) = Accounting.Helpers.IntegrationKeyPolicy.ApplyScopeUpdate(
            new Accounting.Helpers.IntegrationKeyScopes(integration.CanRead, integration.CanWrite, integration.CanDelete),
            request.CanRead, request.CanWrite, request.CanDelete);
        if (scopesChanged)
        {
            integration.CanRead = scopes.CanRead;
            integration.CanWrite = scopes.CanWrite;
            integration.CanDelete = scopes.CanDelete;
            integration.IsLegacyKey = false;
        }

        await _db.SaveChangesAsync();

        return ToResponse(integration, DateTime.UtcNow);
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

    public async Task<IntegrationCreatedResponse> RegenerateApiKeyAsync(Guid companyId, Guid integrationId)
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

        // Return the raw key so the caller can show it ONCE — it is hashed in
        // storage and cannot be retrieved again. SecretKey is unchanged by a
        // key regeneration, so it is not re-surfaced here.
        return new IntegrationCreatedResponse(
            integration.Id, integration.SystemName, rawKey, integration.ApiKeyPrefix,
            null, integration.CreatedAt);
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

    public async Task<(Guid CompanyId, Guid IntegrationId, Accounting.Helpers.IntegrationKeyScopes Scopes)?> ValidateApiKeyAsync(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length < 8)
            return null;

        var prefix = apiKey[..8];
        var candidates = await _db.Set<ExternalIntegration>()
            .Where(i => i.ApiKeyPrefix == prefix && i.IsActive && !i.IsDeleted)
            .Select(i => new { i.Id, i.CompanyId, i.ApiKeyHash,
                i.CanRead, i.CanWrite, i.CanDelete, i.IsLegacyKey, i.LegacyDeprecatesAt })
            .ToListAsync();

        foreach (var candidate in candidates)
        {
            if (BCrypt.Net.BCrypt.Verify(apiKey, candidate.ApiKeyHash))
            {
                // สิทธิ์ที่บังคับใช้จริง — ตัวตัดสินเดียวกับเส้น X-Api-Key ใน ApiKeyMiddleware (G2-01)
                var scopes = Accounting.Helpers.IntegrationKeyPolicy.EffectiveScopes(
                    candidate.IsLegacyKey, candidate.LegacyDeprecatesAt,
                    new Accounting.Helpers.IntegrationKeyScopes(candidate.CanRead, candidate.CanWrite, candidate.CanDelete),
                    DateTime.UtcNow);
                return (candidate.CompanyId, candidate.Id, scopes);
            }
        }

        return null;
    }


    // ═══════════════════════════════════════════════════════════════════
    // คุณภาพข้อมูลผู้ติดต่อที่มาจากระบบภายนอก
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>ผลการเทียบชื่อกับทะเบียนกรมพัฒนาธุรกิจการค้า</summary>
    private sealed record DbdVerification(
        bool Matched, string? OfficialName, string? OfficialNameEn, string? OfficialAddress,
        Accounting.Helpers.DbdTrustVerdict Verdict, string? Warning);

    /// <summary>เทียบเลขผู้เสียภาษีที่ระบบภายนอกส่งมากับทะเบียนราชการ
    ///
    /// <para><b>ที่มา (บั๊กจริง)</b>: ระบบภายนอกส่งชื่อ "ทบริษัท คาร์วิน ไทย…" (มีอักษรแปลก
    /// นำหน้า — ลายเซ็นของฟิลด์เหลื่อม/ตัดคำ) พร้อมเลขผู้เสียภาษีที่ถูกต้อง แต่เราเก็บชื่อ
    /// ตรง ๆ โดยไม่เคยเอาเลขไปตรวจกับทะเบียนเลย ⇒ ชื่อผิดไหลลงใบกำกับภาษี (§86/4
    /// บังคับชื่อผู้ซื้อถูกต้อง) · <c>IDbdLookupService</c> มีอยู่และถูก inject ไว้แล้วด้วยซ้ำ
    /// แต่ถูกเรียกเฉพาะตอน "ไม่มีชื่อมาเลย" — เงื่อนไขที่แคบเกินจนไม่เคยช่วยเคสนี้
    /// (defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้")</para>
    ///
    /// <para><b>ด่านสำคัญ</b>: ทะเบียนชนะได้ก็ต่อเมื่อ <b>กุญแจถูก</b> — เลขผิดหนึ่งหลัก
    /// จะได้ข้อมูล<b>บริษัทอื่น</b>ที่ถูกต้อง 100% ตามทะเบียน ⇒ ใช้
    /// <c>DbdIdentityGuard</c> ตัวเดียวกับที่เส้น OCR ใช้ (ห้ามเขียนกติกาซ้ำ)</para></summary>
    private async Task<DbdVerification> VerifyAgainstDbdAsync(string? taxId, string? incomingName)
    {
        var normalized = Accounting.Helpers.ThaiTaxId.Normalize(taxId);
        // ตรวจเฉพาะ**นิติบุคคล** (ขึ้นต้น 0) ที่ checksum ผ่าน — เลขบัตรประชาชนไม่มีในทะเบียนนี้
        if (_dbd == null || !Accounting.Helpers.ThaiTaxId.IsJuristic(normalized))
            return new(false, null, null, null, Accounting.Helpers.DbdTrustVerdict.NoIncomingName, null);

        Accounting.Services.Interfaces.DbdCompanyResult? dbd;
        try { dbd = await _dbd.GetByJuristicIdAsync(normalized); }
        catch (Exception ex)
        {
            // ทะเบียนล่ม = ใช้ข้อมูลที่ส่งมาตามเดิม (best-effort) ห้ามทำให้ sync ล้มทั้งก้อน
            _logger.LogWarning(ex, "ตรวจทะเบียน DBD ไม่สำเร็จสำหรับเลข {TaxId} — ใช้ข้อมูลที่ต้นทางส่งมา", normalized);
            return new(false, null, null, null, Accounting.Helpers.DbdTrustVerdict.NoIncomingName, null);
        }
        if (dbd == null || string.IsNullOrWhiteSpace(dbd.NameTh))
            return new(false, null, null, null, Accounting.Helpers.DbdTrustVerdict.NoIncomingName, null);

        var sim = Ocr.FuzzyMatcher.Similarity(incomingName ?? "", dbd.NameTh);
        var verdict = Accounting.Helpers.DbdIdentityGuard.Judge(dbd.NameTh, incomingName, sim);

        if (verdict == Accounting.Helpers.DbdTrustVerdict.KeyLooksWrong)
        {
            var msg = Accounting.Helpers.DbdIdentityGuard.KeyMismatchMessage(
                normalized, dbd.NameTh, incomingName ?? "");
            _logger.LogWarning("ผู้ติดต่อจากระบบภายนอก: {Message}", msg);
            return new(false, dbd.NameTh, dbd.NameEn, dbd.Address, verdict, msg);
        }

        return new(true, dbd.NameTh, dbd.NameEn, dbd.Address, verdict, null);
    }

    /// <summary>คัดค่าที่จะลงช่องที่อยู่แบบมีโครง — ค่าที่ไม่ผ่านจะถูก**ย้ายไปหมายเหตุ**
    /// ไม่ใช่ทิ้งเงียบ (กฎ "ห้าม silent no-op": ค่าที่ต้นทางส่งผิดช่องอาจมีข้อมูลจริงปนอยู่
    /// และเป็นหลักฐานว่าต้องไปแก้ที่ต้นทาง)</summary>
    private string? SanitizeStructuredAddressField(
        string? value, string fieldLabel, List<string> rejected)
    {
        var check = Accounting.Helpers.InboundAddressSanity.CheckStructured(value);
        if (check.Accepted) return value;
        rejected.Add($"{fieldLabel}: \"{check.Rejected}\" ({check.Reason})");
        _logger.LogWarning(
            "ผู้ติดต่อจากระบบภายนอก: ปฏิเสธค่าในช่อง \"{Field}\" — {Reason}", fieldLabel, check.Reason);
        return null;
    }

    /// <summary>สรุป "เกิดอะไรขึ้นตอนรับข้อมูลจากระบบภายนอก" ลงหมายเหตุของผู้ติดต่อ
    ///
    /// <para>ผู้ใช้เปิดหน้าผู้ติดต่อแล้วต้องเห็นได้เองว่าทำไมชื่อถูกแก้ / ทำไมช่องที่อยู่ว่าง —
    /// ถ้าเก็บไว้แต่ใน log ผู้ใช้จะเจอแค่ "ข้อมูลไม่เหมือนที่ส่ง" โดยไม่มีอะไรอธิบาย
    /// (กฎ "ต้องดังในที่ที่คนดู" — log ของเซิร์ฟเวอร์ไม่ใช่ช่องทางแจ้งผู้ใช้)</para></summary>
    private static string? BuildInboundDataNote(DbdVerification dbd, List<string> rejectedFields)
    {
        var lines = new List<string>();
        if (dbd.Verdict == Accounting.Helpers.DbdTrustVerdict.SameCompanyMisspelled && dbd.Matched)
            lines.Add($"ชื่อถูกแก้ตามทะเบียนกรมพัฒนาธุรกิจการค้า: {dbd.OfficialName}");
        if (dbd.Warning != null)
            lines.Add("⚠️ " + dbd.Warning);
        if (rejectedFields.Count > 0)
        {
            lines.Add("⚠️ ระบบต้นทางส่งค่าผิดช่อง จึงไม่นำมาใช้ (แก้ที่ต้นทางแล้ว sync ใหม่ได้):");
            lines.AddRange(rejectedFields.Select(r => "  • " + r));
        }
        if (lines.Count == 0) return null;
        var stamp = DateTime.UtcNow.AddHours(7)
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return $"[ตรวจข้อมูลขาเข้า {stamp} น.]\n" + string.Join("\n", lines);
    }

    /// <summary>ประเภทผู้ติดต่อ — อนุมานจากเลขผู้เสียภาษีเมื่อระบบภายนอกไม่ได้ส่งมา
    ///
    /// <para><b>ทำไมสำคัญ</b>: ค่านี้เป็นตัวตัดสิน <b>ภ.ง.ด.3 (บุคคล) vs ภ.ง.ด.53 (นิติบุคคล)</b>
    /// · ค่าเดิม default เป็น <c>Individual</c> เสมอเมื่อไม่ได้ส่งมา ⇒ นิติบุคคลถูกจัดเป็น
    /// บุคคลธรรมดาเงียบ ๆ แล้วยื่นผิดแบบ · เลข 13 หลักที่ขึ้นต้น <b>0</b> คือเลขทะเบียน
    /// นิติบุคคล ตอบได้จากข้อมูลที่มีอยู่แล้ว ไม่ต้องเดา</para></summary>
    /// <summary>ชนิดผู้ติดต่อจาก <c>Helpers/ContactTypeResolver</c> —
    /// <b>ตัวตัดสินตัวเดียวของระบบ</b> (สำเนาในไฟล์นี้ถูกถอดแล้ว)
    ///
    /// <para><b>ตัดสินไม่ได้ = <c>ContactType.Unknown</c> ไม่ใช่ <c>Individual</c></b>
    /// (DECISION_AUDIT §9.3 D-1 · DOCTRINE §1 G3). ของเดิมคืน <c>Individual</c>
    /// ⇒ คู่ค้าที่ระบบต้นทางไม่ส่งชนิดมาและไม่มีเลขภาษีที่ใช้ได้ กลายเป็น
    /// "บุคคลธรรมดาที่พิสูจน์แล้ว" ในสายตา <c>WhtPayeeKind.Detect</c> ⇒ 50 ทวิ
    /// ถูกออกเป็น ภ.ง.ด.3 โดยไม่มีใครเห็นว่าข้อมูลยังไม่ครบ</para></summary>
    private static ContactType ResolveContactType(string? sent, string? taxId, bool dbdMatched,
        string? name = null)
        => Accounting.Helpers.ContactTypeResolver.Resolve(sent, taxId, name, dbdMatched).Type;

    // ===== Phase 2: Inbound Data Processing =====

    public async Task<InboundSyncResponse> ProcessCustomerAsync(Guid companyId, Guid integrationId, InboundCustomerRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "customer.sync", request.ExternalId, request.Name);

        try
        {
            // Find existing contact by (TaxId + BranchCode) or Name — รอบ 193 ข้อ 20: คีย์เลขภาษี + สาขา
            // (Helpers/ContactTaxBranchKey ตัวเดียวกับทุกทางเข้า) · เดิม TaxId อย่างเดียว ⇒ payload สาขา 8 หยิบแถว
            // สำนักงานใหญ่ แล้วบล็อก update ด้านล่างเขียน BranchCode = 00008 ทับแถวนั้น
            Contact? contact = null;
            var taxKey = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(
                _db.Set<Contact>(), companyId, request.TaxId, request.BranchCode);
            if (taxKey.ContactId is Guid keyId)
                contact = await _db.Set<Contact>().FirstOrDefaultAsync(c => c.Id == keyId && c.CompanyId == companyId && !c.IsDeleted);
            // มีผู้ติดต่อเลขนี้แต่คนละสาขา ⇒ ห้ามถอยไปจับด้วยชื่อ (ชื่อเดียวกัน = แถวสาขาอื่นของเลขเดียวกัน) → สร้างแถวสาขานี้
            if (contact == null && !taxKey.TaxIdExists && !string.IsNullOrEmpty(request.Name))
                contact = await _db.Set<Contact>().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Name.ToLower() == request.Name.ToLower() && !c.IsDeleted);

            // ── ตรวจกับทะเบียนราชการก่อนเสมอ (ไม่ใช่เฉพาะตอนไม่มีชื่อมา) ──
            // ระบบภายนอกส่งชื่อผิดมาได้ (ฟิลด์เหลื่อม/ตัดคำ) ทั้งที่เลขผู้เสียภาษีถูก
            // ⇒ เอาเลขไปตรวจทุกครั้ง แล้วให้ชื่อทางการชนะ **เมื่อกุญแจน่าเชื่อถือ**
            var dbdCheck = await VerifyAgainstDbdAsync(request.TaxId, request.Name);
            var officialName = dbdCheck.Matched ? dbdCheck.OfficialName : null;

            // ค่าที่ต้นทางส่งผิดช่อง — เก็บไว้บอกผู้ใช้ ไม่ทิ้งเงียบ
            var rejectedFields = new List<string>();
            var moo = SanitizeStructuredAddressField(request.Moo, "หมู่ที่", rejectedFields);
            var subDistrict = SanitizeStructuredAddressField(request.SubDistrict, "ตำบล/แขวง", rejectedFields);
            var district = SanitizeStructuredAddressField(request.District, "อำเภอ/เขต", rejectedFields);
            var province = SanitizeStructuredAddressField(request.Province, "จังหวัด", rejectedFields);
            var streetName = SanitizeStructuredAddressField(request.StreetName, "ถนน/ซอย", rejectedFields);
            var buildingName = SanitizeStructuredAddressField(request.BuildingName, "ชื่ออาคาร", rejectedFields);
            var postalCheck = Accounting.Helpers.InboundAddressSanity.CheckPostalCode(request.PostalCode);
            var postalCode = postalCheck.Accepted ? request.PostalCode : null;
            if (!postalCheck.Accepted)
                rejectedFields.Add($"รหัสไปรษณีย์: \"{postalCheck.Rejected}\" ({postalCheck.Reason})");

            var syncNote = BuildInboundDataNote(dbdCheck, rejectedFields);

            if (contact == null)
            {
                // ชนิดผู้ติดต่อ + รหัสสาขา ต้องออกจากตัวตัดสินตัวเดียวกัน (§86/4(2))
                var newContactIdentity = Accounting.Helpers.ContactTypeResolver.ResolveWithBranch(
                    request.ContactType, request.TaxId, request.BranchCode,
                    officialName ?? request.Name, dbdCheck.Matched);
                contact = new Contact
                {
                    CompanyId = companyId,
                    // ชื่อเว้นว่างได้ (DTO เป็น `string?`) และทะเบียนอาจไม่คืนชื่อมา —
                    // คู่ค้าที่ไม่มีชื่อเลยค้นไม่เจอตลอดไป ⇒ ใช้เลขผู้เสียภาษีเป็นชื่อชั่วคราว
                    // ให้ผู้ใช้เห็นแล้วแก้ได้ แทนการปล่อย null ลงคอลัมน์ที่ไม่รับ null
                    Name = officialName
                        ?? (string.IsNullOrWhiteSpace(request.Name)
                            ? $"(ไม่ระบุชื่อ) {request.TaxId}".Trim()
                            : request.Name),
                    NameEn = dbdCheck.Matched ? dbdCheck.OfficialNameEn : null,
                    TaxId = request.TaxId,
                    Phone = request.Phone,
                    Email = request.Email,
                    Address = request.Address,
                    // ตัวตัดสิน ภ.ง.ด.3 vs 53 — ห้าม default เป็นบุคคลธรรมดาเงียบ ๆ
                    // ตัดสินไม่ได้ ⇒ ContactType.Unknown ที่เห็นได้บนหน้าผู้ติดต่อ
                    ContactType = newContactIdentity.Type,
                    BranchCode = newContactIdentity.BranchCode,
                    IsCustomer = request.IsCustomer ?? true,
                    IsSupplier = request.IsSupplier ?? false,
                    IsActive = true,
                    // Structured address fields were previously dropped on
                    // inbound sync — external CRMs that send them lost the
                    // data on the trip in. Map them through, including the
                    // newly-added Moo field.
                    BuildingNumber = request.BuildingNumber,
                    BuildingName = buildingName,
                    Moo = moo,
                    StreetName = streetName,
                    SubDistrict = subDistrict,
                    District = district,
                    Province = province,
                    PostalCode = postalCode,
                    InternalNotes = syncNote,
                };
                _db.Set<Contact>().Add(contact);
            }
            else
            {
                // ชื่อทางการชนะเมื่อทะเบียนยืนยัน — นี่คือจุดที่ซ่อมข้อมูลเก่าที่เพี้ยนไปแล้ว
                // (contact ที่สร้างก่อนมีด่านนี้จะถูกแก้ให้ถูกในการ sync ครั้งถัดไป)
                if (officialName != null && officialName != contact.Name)
                {
                    _logger.LogInformation(
                        "แก้ชื่อผู้ติดต่อตามทะเบียน: \"{Old}\" → \"{New}\" (เลข {TaxId})",
                        contact.Name, officialName, request.TaxId);
                    contact.Name = officialName;
                }
                if (dbdCheck.Matched && !string.IsNullOrWhiteSpace(dbdCheck.OfficialNameEn)
                    && string.IsNullOrWhiteSpace(contact.NameEn))
                    contact.NameEn = dbdCheck.OfficialNameEn;
                if (syncNote != null) contact.InternalNotes = syncNote;
                // Update existing — only overwrite when the request actually
                // carries a value, so partial syncs don't blank out fields
                // the receiving tenant has already enriched.
                if (request.Phone != null) contact.Phone = request.Phone;
                if (request.Email != null) contact.Email = request.Email;
                if (request.Address != null) contact.Address = request.Address;
                if (request.TaxId != null) contact.TaxId = request.TaxId;
                // §86/4: รหัสสาขา + ประเภทผู้ติดต่อ ต้องอัปเดตตอน resync ด้วย —
                // เดิม set เฉพาะตอน create → contact นิติบุคคลเก่า (สร้างก่อน
                // TakeTime ส่ง branchCode) ไม่มีวันได้รหัสสาขา → ใบกำกับเต็มรูป
                // approve 400 "ต้องมีรหัสสาขาผู้ซื้อ" ตลอดไป แม้ TakeTime ส่งครบ
                // ประเภทผู้ติดต่อ: อนุมานได้เมื่อไม่ได้ส่งมา — แต่ห้ามลดระดับค่าที่คน
                // เคยตั้งใจตั้งไว้ เพราะ sync ครั้งนี้ไม่ได้ส่งค่ามา. กติกาทั้งชุด
                // (ประกาศชนะ > รูปเลขที่ checksum ผ่าน > คงค่าเดิม > อนุมาน) อยู่ใน
                // Helpers/ContactTypeResolver.ApplyToExisting ตัวเดียว — เดิมด่าน
                // "ห้ามลดระดับ" ที่เขียนไว้ตรงนี้ครอบแค่ JuristicPerson ⇒ ราชการ
                // ถูกกดกลับเป็นบุคคลธรรมดาได้ทุกรอบ sync
                contact.ContactType = Accounting.Helpers.ContactTypeResolver.ApplyToExisting(
                    contact.ContactType, request.ContactType, request.TaxId ?? contact.TaxId,
                    contact.Name, dbdCheck.Matched).Type;
                // รหัสสาขาเป็นเรื่องของนิติบุคคล/ราชการ (§86/4(2) · ประกาศฯ 199) —
                // ตัวตัดสินตัวเดียวเป็นคนบอกว่าเก็บได้ไหม เพื่อไม่ให้ "00000" ที่ระบบ
                // ต้นทางใส่มาเป็น default ไปติดท้ายเลขบัตรประชาชนเป็น "(สำนักงานใหญ่)"
                // รอบ 193 ทีม C3: เขียนสาขาของ payload ได้เฉพาะเมื่อแถวที่จับได้ "ตรงสาขาแล้ว" (MayOverwriteBranch) —
                // payload รหัสผิดรูป ("8A"/"สาขา 8") ถูกตัวจับคู่ถือว่า "ไม่รู้" แล้วได้แถว สนญ. แต่ BranchCodeFor ดึงเลขออกมา
                // เป็น 00008 ⇒ ถ้าเขียน แถว สนญ. กลายเป็นสาขา 8 เงียบ ๆ (ช่องเดียวกับที่ข้อ 20 ปิด) · payload "" ก็ล้างสาขาทิ้งได้
                if (request.BranchCode != null && taxKey.MayOverwriteBranch)
                    contact.BranchCode = Accounting.Helpers.ContactTypeResolver.BranchCodeFor(
                        contact.ContactType, request.BranchCode);
                if (request.BuildingNumber != null) contact.BuildingNumber = request.BuildingNumber;
                // ใช้ค่าที่ผ่านด่านแล้ว — ค่าที่ถูกปฏิเสธจะไม่ทับของเดิมที่ผู้ใช้แก้ไว้ถูก
                if (buildingName != null) contact.BuildingName = buildingName;
                if (moo != null) contact.Moo = moo;
                if (streetName != null) contact.StreetName = streetName;
                if (subDistrict != null) contact.SubDistrict = subDistrict;
                if (district != null) contact.District = district;
                if (province != null) contact.Province = province;
                if (postalCode != null) contact.PostalCode = postalCode;
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
            // Idempotency: if a document with the same ExternalRef already exists,
            // return it instead of creating a duplicate. This makes re-syncs safe.
            if (!string.IsNullOrEmpty(request.ExternalRef))
            {
                var existing = await _db.Documents
                    .FirstOrDefaultAsync(d => d.CompanyId == companyId
                        && d.Reference == request.ExternalRef
                        && !d.IsDeleted
                        && d.DocumentType == DocumentType.TaxInvoice);
                if (existing != null && existing.Status != DocumentStatus.Voided)
                {
                    // ── Resync update: partner ส่งข้อมูลแก้ไขมาพร้อม flag ──
                    if (request.ResyncUpdate)
                        return await ResyncUpdateInvoiceAsync(companyId, integrationId, existing, request, log, sw);

                    // ยิงซ้ำ (idempotent): เอกสารมีอยู่แล้ว + JE โพสต์ครบตั้งแต่ create
                    // (ขายเงินสด = clean JE / ตั้งหนี้ = mapping JE). ถ้า create ล้มทั้ง
                    // สองชั้น (JE หาย) → ใช้ resyncUpdate=true เพื่อ rebuild. ที่นี่ skip.
                    log.Status = "Skipped";
                    log.CreatedDocumentId = existing.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", existing.Id, existing.ContactId, null, null, existing.DocumentNumber);
                }
            }
            else
            {
                // ไม่มี ExternalRef → fallback dedup ด้วย ExternalId จาก sync log เดิม
                var priorDoc = await TryFindDocumentByExternalIdAsync(companyId, integrationId, "invoice.created", request.ExternalId);
                if (priorDoc != null)
                {
                    log.Status = "Skipped";
                    log.CreatedDocumentId = priorDoc.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip by ExternalId)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", priorDoc.Id, priorDoc.ContactId, null, null, priorDoc.DocumentNumber);
                }
            }

            // ===== §90/2 — บริษัทไม่จด VAT ห้ามออกใบกำกับภาษี =====
            // endpoint นี้สร้าง TaxInvoice เสมอ — บริษัทที่ติ๊ก "ไม่จด VAT"
            // ต้องถูกปฏิเสธพร้อมทางแก้ ไม่ใช่ปล่อยใบกำกับหลุดออกไป (ความผิด
            // ทั้งค่าปรับและต้องนำส่ง VAT ที่เรียกเก็บ)
            var vatProfile = await GetCompanyVatProfileAsync(companyId);
            var vatRegistered = vatProfile.Registered;
            if (!vatRegistered)
            {
                log.Status = "Failed";
                log.ErrorMessage = "Company not VAT-registered (§90/2)";
                log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                await SaveSyncLog(log, integrationId);
                return new InboundSyncResponse(false,
                    "บริษัทยังไม่ได้จดทะเบียนภาษีมูลค่าเพิ่ม — ออกใบกำกับภาษีผ่าน API ไม่ได้ (§90/2). " +
                    "ถ้าจดทะเบียนแล้ว เปิด \"จดทะเบียนภาษีมูลค่าเพิ่ม\" ในหน้าตั้งค่าระบบบัญชีของ NextAcc",
                    null, null, null, null, null);
            }

            // Resolve or create contact
            // ผู้ซื้อไม่ประสงค์รับใบกำกับภาษี (ขายปลีก) — flag ชัดเจน หรือไม่ส่ง
            // ข้อมูลลูกค้าเลย → ผูกกับผู้ติดต่อกลาง "ลูกค้าเงินสด" (ยกเว้น §86/4
            // ฝั่งผู้ซื้อตามประกาศอธิบดีฯ ฉบับ 199 — บังคับเลขเฉพาะผู้ซื้อจด VAT)
            var anonymousBuyer = request.BuyerDeclinedTaxInvoice
                || (string.IsNullOrWhiteSpace(request.CustomerExternalId)
                    && string.IsNullOrWhiteSpace(request.CustomerName)
                    && string.IsNullOrWhiteSpace(request.CustomerTaxId));
            var contact = anonymousBuyer
                ? await GetOrCreateWalkInContactAsync(companyId)
                : await ResolveContactAsync(companyId, request.CustomerExternalId, request.CustomerName, request.CustomerTaxId);

            // ⚠️ เลขที่เอกสารถูกย้ายไปออก **หลังคำนวณยอด** — กติกา "หัวมีคำว่า
            // ใบกำกับภาษี → เลขชุด TIV เสมอ" ต้องรู้ VAT ก่อนถึงจะเลือกชุดได้
            // (ใบที่ VAT=0 หัวพิมพ์ "ใบเสร็จรับเงิน" จึงต้องไปชุด REC)
            // ย้ายได้ปลอดภัยเพราะตัวออกเลขนับจากเอกสารที่มีอยู่จริง — เลขที่ขอไว้
            // แล้วไม่ได้ใช้ (เส้นทาง fail ด้านล่าง) ไม่ทำให้เกิดช่องว่างอยู่แล้ว

            // Calculate totals
            // อัตราภาษีขาย — ตัวตัดสินเดียวอยู่ที่ Helpers/PartnerVatRate
            // (เดิมถอยไปหาเลข 7 ตายตัว ⇒ ไม่เคยอ่านอัตราที่บริษัทตั้งไว้เลย)
            // จุดนี้ผ่านด่าน §90/2 ข้างบนแล้วจึงไม่มีทาง Rejected — เช็คไว้กัน
            // ให้ด่านสองชั้นไม่หลุดทิศกันถ้าวันหนึ่งด่านแรกถูกย้าย/ผ่อน
            var vatDecision = Accounting.Helpers.PartnerVatRate.ForIssuedDocument(
                request.VatRate, vatRegistered, vatProfile.DefaultRate);
            if (vatDecision.Rejected)
            {
                log.Status = "Failed";
                log.ErrorMessage = "Output VAT blocked — company not VAT-registered (§90/2)";
                log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                await SaveSyncLog(log, integrationId);
                return new InboundSyncResponse(false, vatDecision.Error, null, null, null, null, null);
            }
            var vatRate = vatDecision.Rate;
            decimal subTotal = 0, totalVat = 0, totalDiscount = 0;
            var lines = new List<DocumentLine>();

            // Batch-load all referenced accounts in one query (avoid N+1)
            var accountCodesNeeded = request.Lines
                .Where(l => !string.IsNullOrEmpty(l.AccountCode))
                .Select(l => l.AccountCode!)
                .Distinct().ToList();
            var accountLookup = await BatchLoadAccountsByCodesAsync(companyId, accountCodesNeeded);
            var aiFallbackHits = 0;

            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                var lineAmount = line.Quantity * line.UnitPrice;
                var lineDiscount = line.DiscountAmount ?? 0;
                var lineNet = lineAmount - lineDiscount;
                var lineVatRate = line.VatRate ?? vatRate;
                // Honor explicit VatAmount when partner pre-computed it (mixed
                // 7%/exempt line). For IncludeVat=true the override is the VAT
                // baked into lineNet → strip it so subTotal stays ex-VAT.
                decimal lineVat;
                // ใช้ตัวคำนวณกลางตัวเดียวกับฝั่งซื้อ — เดิมสองสาขานี้ตีความ
                // IncludeVat คนละแบบ (สาขาแรกหัก VAT ออกจาก net สาขาที่สองไม่หัก)
                (lineNet, lineVat) = Accounting.Helpers.DocumentLineVatConvention.SplitLine(lineNet, lineVatRate, line.VatAmount, request.IncludeVat);

                subTotal += lineNet;
                totalVat += lineVat;
                totalDiscount += lineDiscount;

                // Resolve account from pre-loaded batch
                Guid? accountId = null;
                if (!string.IsNullOrEmpty(line.AccountCode) && accountLookup.TryGetValue(line.AccountCode, out var acct))
                    accountId = acct.Id;

                Guid? glFeedbackId = null;
                // AI fallback: AccountCode ไม่ระบุ หรือชี้บัญชีที่ไม่อยู่ในผัง
                // → ถาม student-first GL distillation model ผ่าน orchestrator.
                // High-confidence (≥0.70) + ผังที่แนะนำมีจริง → ใช้; ต่ำกว่านั้น
                // ปล่อย null ให้ผู้ใช้แก้ตอนจัดการเอกสาร (ไม่ block import).
                if (accountId == null && _glAi != null && lineNet > 0)
                {
                    try
                    {
                        var sugg = await _glAi.SuggestGlAccountAsync(
                            companyId: companyId,
                            scanResultId: Guid.Empty,   // ไม่ใช่ OCR — tag ผ่าน description
                            vendorName: request.CustomerName,
                            vendorTaxId: request.CustomerTaxId,
                            vendorIndustry: null,
                            lineDescription: line.ItemName ?? line.ItemCode ?? "",
                            amount: lineNet,
                            currency: "THB",
                            localBestAccountCode: line.AccountCode,
                            localConfidence: 0m);
                        if (!string.IsNullOrWhiteSpace(sugg.Answer)
                            && (sugg.Confidence ?? 0m) >= 0.70m
                            && accountLookup.TryGetValue(sugg.Answer, out var aiAcct))
                        {
                            accountId = aiAcct.Id;
                            glFeedbackId = sugg.FeedbackId;
                            aiFallbackHits++;
                        }
                        else if (!string.IsNullOrWhiteSpace(sugg.Answer)
                                 && (sugg.Confidence ?? 0m) >= 0.70m)
                        {
                            // AI แนะนำผังที่ยังไม่ได้ batch-load → load เพิ่มแล้วใช้
                            var resolved = await _db.ChartOfAccounts.AsNoTracking()
                                .FirstOrDefaultAsync(a => a.CompanyId == companyId
                                    && a.AccountCode == sugg.Answer && a.IsActive);
                            if (resolved != null)
                            {
                                accountLookup[sugg.Answer] = resolved;
                                accountId = resolved.Id;
                                glFeedbackId = sugg.FeedbackId;
                                aiFallbackHits++;
                            }
                        }
                    }
                    catch (Exception aiEx)
                    {
                        // AI ล้มเหลว → ปล่อย null ตามเดิม (ไม่ block import)
                        _logger.LogWarning(aiEx, "Integration GL AI fallback failed (line {Index})", i);
                    }
                }

                lines.Add(new DocumentLine
                {
                    LineOrder = i + 1,
                    ProductCode = line.ItemCode,
                    Description = line.ItemName ?? "",
                    Quantity = line.Quantity,
                    Unit = line.Unit ?? "หน่วย",
                    UnitPrice = line.UnitPrice,
                    DiscountAmount = lineDiscount,
                    Amount = lineNet,
                    VatRate = lineVatRate,
                    VatAmount = lineVat,
                    AccountId = accountId,
                    // เก็บ FeedbackId เพื่อปิดลูปการสอนเมื่อ user เปิดเอกสาร
                    // มาแก้ภายหลัง (DocumentService จะ record choice ตาม
                    // กฎเหล็ก #1)
                    GlAccountAiFeedbackId = glFeedbackId,
                });
            }

            // SubTotal เป็นฐานก่อน VAT เสมอแล้ว (SplitLineVat) ⇒ ยอดรวมต้องบวก VAT
            var totalAmount = subTotal + totalVat;

            // ── กันใบกำกับซ้อนกับ "ใบแจ้งหนี้" เดิมอ้างอิง (WO) เดียวกัน ──
            // เคสจริง: INV 97,500 ค้างอยู่ + integration mint TIV 75,000 แยกใบ
            // (บรรทัดหนึ่ง payload ส่งราคา 0 มา) → ไม่ผูกกัน = GL รายได้/VAT ซ้ำ
            // 2 ใบ + ภ.พ.30 ขึ้นซ้อน + ใบกำกับยอดขาด 22,500 เงียบ ๆ.
            // ทางแก้: ถ้ามีใบแจ้งหนี้ active อ้างอิงเดียวกัน →
            //   • ยอดตรง + ยังไม่ชำระ + ไม่มีมัดจำใน payload → "แปลงจากใบแจ้งหนี้
            //     จริง" (ConvertDocumentAsync + Approve) — บรรทัด/ราคา copy จาก
            //     ใบแจ้งหนี้ ไม่ใช้ payload + SupersedeSourceInvoiceAsync void
            //     ใบแจ้งหนี้อัตโนมัติ = เหลือใบกำกับใบเดียวใน GL/ภ.พ.30
            //   • ยอดไม่ตรง/ชำระแล้ว/มีมัดจำ → Failed พร้อมเหตุผล (ห้ามสร้างซ้อนเงียบ)
            if (!string.IsNullOrEmpty(request.ExternalRef) && _documentService != null)
            {
                var priorInvoice = await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == companyId
                        && d.Reference == request.ExternalRef && !d.IsDeleted
                        && d.DocumentType == DocumentType.Invoice
                        && d.Status != DocumentStatus.Voided
                        && d.Status != DocumentStatus.Rejected)
                    .OrderByDescending(d => d.CreatedAt)
                    .FirstOrDefaultAsync();
                if (priorInvoice != null)
                {
                    string? failReason = null;
                    if (priorInvoice.PaidAmount > 0.01m)
                        failReason = $"ใบแจ้งหนี้ {priorInvoice.DocumentNumber} (อ้างอิง {request.ExternalRef}) มีการชำระแล้ว {priorInvoice.PaidAmount:N2} บาท — ออกใบกำกับซ้อนไม่ได้ กรุณาจัดการใบเดิมก่อน";
                    else if (Math.Abs(priorInvoice.TotalAmount - totalAmount) > 0.01m)
                        failReason = $"ยอดใบกำกับที่ส่งมา ({totalAmount:N2}) ไม่ตรงกับใบแจ้งหนี้ {priorInvoice.DocumentNumber} ({priorInvoice.TotalAmount:N2}) อ้างอิงเดียวกัน ({request.ExternalRef}) — ตรวจราคาต่อบรรทัดใน payload (พบเคสส่งราคา 0 มา) แล้ว sync ใหม่ หรือยกเลิกใบแจ้งหนี้เดิมก่อน";
                    else if (request.DepositAppliedAmount > 0m)
                        failReason = $"มีใบแจ้งหนี้ {priorInvoice.DocumentNumber} อ้างอิงเดียวกันค้างอยู่ + payload มีหักมัดจำ — เคสนี้ต้องยกเลิกใบแจ้งหนี้เดิมก่อนแล้ว sync ใหม่ (กันมัดจำ/ยอดซ้อน)";

                    if (failReason != null)
                    {
                        log.Status = "Failed";
                        log.ErrorMessage = failReason;
                        log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                        await SaveSyncLog(log, integrationId);
                        return new InboundSyncResponse(false, failReason, priorInvoice.Id, null, null, null, priorInvoice.DocumentNumber);
                    }

                    // ยอดตรง → ออกใบกำกับด้วยการแปลงจากใบแจ้งหนี้จริง (เส้นทางเดียว
                    // กับผู้ใช้กดแปลงในระบบ: เลขรัน TIV + JE + supersede ครบ)
                    var convertActor = "integration:invoice-sync";
                    var converted = await _documentService.ConvertDocumentAsync(
                        companyId, priorInvoice.Id, DocumentType.TaxInvoice, convertActor);
                    var approvedTiv = await _documentService.ApproveDocumentAsync(
                        companyId, converted.Id, convertActor);
                    log.Status = "Success";
                    log.CreatedDocumentId = approvedTiv.Id;
                    log.ErrorMessage = $"ออกใบกำกับโดยแปลงจากใบแจ้งหนี้ {priorInvoice.DocumentNumber} (ใบแจ้งหนี้ถูกแทนที่อัตโนมัติ)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true,
                        $"Tax invoice issued by converting {priorInvoice.DocumentNumber} (superseded)",
                        approvedTiv.Id, priorInvoice.ContactId, null, null, approvedTiv.DocumentNumber);
                }
            }

            // ── เลขที่เอกสาร: เลือกชุดจากบทบาททางกฎหมาย ──
            // ใช้ Helpers/TaxInvoiceSeriesPolicy ตัวเดียวกับเส้นในระบบ (พื้นบังคับ
            // "TaxInvoice ที่มี VAT = ใบกำกับเสมอ" ครอบเคสของ endpoint นี้ครบ:
            // ขายสด/ผู้ซื้อไม่ครบ §86/4 หัวยังมีคำว่าใบกำกับทั้งคู่) — ห้ามเขียน
            // เงื่อนไข VAT>0 เองซ้ำที่นี่ จะกลายเป็นสำเนาที่ drift
            var roleProbe = new Document
            {
                DocumentType = DocumentType.TaxInvoice,
                VatAmount = totalVat,
            };
            var integrationCarriesTaxInvoice = Accounting.Helpers.TaxInvoiceSeriesPolicy
                .CarriesTaxInvoiceRole(roleProbe, resolvedTitle: null);
            var integrationSeriesType = DocumentType.TaxInvoice;
            if (Accounting.Helpers.TaxInvoiceSeriesPolicy.IsUnifiedSeriesEnabled(
                    await _db.CompanySettings.AsNoTracking()
                        .Where(s => s.CompanyId == companyId)
                        .Select(s => s.UnifyTaxInvoiceNumberSeries)
                        .FirstOrDefaultAsync()))
            {
                integrationSeriesType = Accounting.Helpers.TaxInvoiceSeriesPolicy
                    .SeriesTypeOverride(roleProbe, integrationCarriesTaxInvoice) ?? DocumentType.TaxInvoice;
            }
            var docNumber = await _settingsService.GetNextNumberAsync(
                companyId, integrationSeriesType, request.DocumentDate);

            var document = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                IsTaxInvoiceByLaw = integrationCarriesTaxInvoice,
                DocumentType = DocumentType.TaxInvoice,
                Status = DocumentStatus.Approved,
                DocumentDate = NormalizeDate(request.DocumentDate),
                DueDate = NormalizeDate(request.DueDate ?? request.DocumentDate.AddDays(30)),
                ContactId = contact.Id,
                Reference = request.ExternalRef,
                // เลขจอง booking (ผูกมัดจำ→ใบกำกับ→ใบเสร็จ เข้า booking เดียวกัน)
                BookingNumber = string.IsNullOrWhiteSpace(request.BookingNumber) ? null : request.BookingNumber.Trim(),
                SubTotal = subTotal,
                DiscountAmount = totalDiscount,
                VatAmount = totalVat,
                TotalAmount = totalAmount,
                BalanceDue = totalAmount,
                Notes = request.Notes,
                // Preparer identity from the source system → "ผู้จัดทำ" slot.
                PreparerName = string.IsNullOrWhiteSpace(request.PreparerName) ? null : request.PreparerName.Trim(),
                PreparerSignatureBase64 = TrimPreparerSignature(request.PreparerSignatureBase64),
                // ขายเงินสด B2B → ลง JE แบบเงินสด (ไม่มีลูกหนี้) + ออก e-Tax T03 หัวรวม.
                // IssuedAsCashReceipt คุมทั้ง branch AutoPost + e-Tax type + หัว PDF.
                IssuedAsCashReceipt = request.IsCashSale,
                // บัญชีเงินสด/ธนาคารที่รับเงิน — AutoPost อ่านเป็น moneyAccount (Dr)
                PaymentAccountId = request.PaymentAccountId,
                // มัดจำที่หักออกแล้ว (spec deposit/checkout). stamp ทุกกรณี:
                //  • drives (DrivesJournal=true): AutoPost/driveDeposit อ่านยอดนี้ไปกลับ
                //    บัญชี 217xx/21913 + Dr เงินสด "สุทธิ" (Total − ยอดนี้)
                //  • display-only (false): แค่ให้ renderer โชว์ "หักมัดจำ/รับสุทธิ" ไม่กระทบ GL
                DepositAppliedAmount = request.DepositAppliedAmount > 0m ? request.DepositAppliedAmount : 0m,
                DepositAppliedRef = string.IsNullOrWhiteSpace(request.DepositAppliedRef) ? null : request.DepositAppliedRef.Trim(),
                DepositOutputVatDeferred = request.DepositOutputVatDeferred,
                DepositAppliedDrivesJournal = request.DepositAppliedDrivesJournal,
                Lines = lines
            };

            _db.Documents.Add(document);
            await _db.SaveChangesAsync();
            await _vendorIntel.TryTrainAsync(companyId, document.Id);

            // ── JE posting ──
            // • ขายเงินสด (isCashSale): ลง JE เดียวแบบ "ขายเงินสด" ผ่าน AutoPost/
            //   driveDeposit → Dr เงินสด(paymentAccountId) + กลับมัดจำ 217xx/21913
            //   (ถ้า drives) / Cr รายได้ + Cr 21911 — **ไม่มีลูกหนี้การค้า** (spec TakeTime).
            //   ปิดยอด PaidAmount=Total, BalanceDue=0 → e-Tax T03. fail-soft: ถ้าลง
            //   ไม่สำเร็จ → degrade เป็นตั้งหนี้ (mapping JE) + คง BalanceDue → TakeTime
            //   capability-detection (balanceDue>0) จะ fallback settle เอง ไม่ settle ซ้ำ.
            // • ปกติ (ตั้งหนี้/เครดิต): mapping JE ตามเดิม (Dr ลูกหนี้/Cr รายได้+VAT).
            Guid? journalEntryId;
            string? jeSkipReason = null;
            string? cashSaleNote = null;
            if (request.IsCashSale && _documentService != null)
            {
                try
                {
                    journalEntryId = await _documentService.PostCashSaleJournalAsync(companyId, document.Id);
                    document.PaidAmount = document.TotalAmount;
                    document.BalanceDue = 0m;
                    await _db.SaveChangesAsync();
                    cashSaleNote = " (ใบเดียว: ใบเสร็จรับเงิน/ใบกำกับภาษี · e-Tax T03 · GL ขายเงินสด ไม่มีลูกหนี้)";
                }
                catch (Exception exCash)
                {
                    _logger.LogWarning(exCash,
                        "isCashSale AutoPost failed for {Doc} — degrade เป็นตั้งหนี้ (mapping JE) ให้ TakeTime/ผู้ใช้รับชำระ",
                        document.DocumentNumber);
                    document.IssuedAsCashReceipt = false;   // ไม่ใช่ขายเงินสดแล้ว (ลงตั้งหนี้)
                    await _db.SaveChangesAsync();
                    (journalEntryId, jeSkipReason) = await PostMappingJournalAsync(companyId, integrationId, document, "invoice", log);
                    cashSaleNote = " (⚠ ลง JE ขายเงินสดไม่สำเร็จ — ตั้งหนี้แทน, ให้รับชำระในระบบ)";
                }
            }
            else
            {
                (journalEntryId, jeSkipReason) = await PostMappingJournalAsync(companyId, integrationId, document, "invoice", log);
            }

            // ⚠️ ห้ามทับสถานะ PartialSuccess ที่ PostMappingJournalAsync ตั้งไว้ —
            // "สร้างเอกสารได้แต่ลงบัญชีไม่ได้" ไม่ใช่ Success
            if (log.Status != "PartialSuccess") log.Status = "Success";
            log.CreatedDocumentId = document.Id;
            log.CreatedContactId = contact.Id;
            log.CreatedJournalEntryId = journalEntryId;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            if (aiFallbackHits > 0)
                log.ResponseJson = JsonSerializer.Serialize(new
                {
                    documentId = document.Id,
                    aiGlFallbackUsed = true,
                    aiGlFallbackLines = aiFallbackHits,
                    totalLines = request.Lines.Count
                });
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true,
                "Invoice created" + cashSaleNote + JeSkipSuffix(jeSkipReason),
                document.Id, contact.Id, journalEntryId, null, docNumber);
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
                // อ้างอิง (WO) เดียวกันอาจมีหลายใบ: INV ที่ถูกแทนที่ (Voided) + TIV
                // ตัวจริง — ต้องไม่จับใบ Voided/Rejected และให้ใบกำกับ (TIV) มาก่อน
                // ใบแจ้งหนี้ (กันชำระเข้าใบที่ supersede ไปแล้ว → AR ที่ถูก reverse)
                document = await _db.Documents
                    .Where(d => d.CompanyId == companyId
                        && d.Reference == request.InvoiceExternalRef && !d.IsDeleted
                        && d.Status != DocumentStatus.Voided
                        && d.Status != DocumentStatus.Rejected)
                    .OrderByDescending(d => d.DocumentType == DocumentType.TaxInvoice)
                    .ThenByDescending(d => d.CreatedAt)
                    .FirstOrDefaultAsync();

            if (document == null)
                throw new KeyNotFoundException($"ไม่พบเอกสารอ้างอิง: {request.InvoiceExternalRef ?? request.DocumentId?.ToString()}");

            // ── กันยอดเบิ้ล (root cause ที่ผู้ใช้เจอ: มัดจำถูกนับซ้ำ) ──
            // (1) เอกสารขายเงินสด (isCashSale) — settle ในตัวใบแล้ว (Dr เงินสด + กลับ
            //     มัดจำ 21510/21913, BalanceDue=0). ห้ามรับชำระภายนอกซ้ำ มิฉะนั้นจะ
            //     Dr เงินสด/Cr ลูกหนี้(ที่ไม่มี) → เงินสดเกิน + AR ติดลบ + มัดจำนับซ้ำ.
            // (2) เอกสารปิดยอดแล้ว (Paid/BalanceDue≤0) — ห้ามชำระเพิ่ม (over-pay).
            //     TakeTime ต้อง "ดึงใบมัดจำเดิม" ผ่าน depositAppliedRef ตอนออกใบกำกับ
            //     ไม่ใช่ยิง payment แยกสำหรับส่วนมัดจำ.
            if (document.IssuedAsCashReceipt || document.Status == DocumentStatus.Paid
                || document.BalanceDue <= 0.005m)
            {
                var reason = document.IssuedAsCashReceipt
                    ? $"เอกสาร {document.DocumentNumber} เป็นขายเงินสด (settle ในตัวใบแล้ว) — ไม่รับชำระภายนอกซ้ำ (กันนับมัดจำ/เงินสดเบิ้ล)"
                    : $"เอกสาร {document.DocumentNumber} ชำระครบแล้ว (คงค้าง {document.BalanceDue:N2}) — ไม่รับชำระเพิ่ม";
                log.Status = "Skipped";
                log.CreatedDocumentId = document.Id;
                log.ErrorMessage = reason;
                log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                await SaveSyncLog(log, integrationId);
                return new InboundSyncResponse(true, reason, document.Id, null, null, null, document.DocumentNumber);
            }
            // over-payment cap: ยอดชำระต้องไม่เกินคงค้าง (กัน PaidAmount>Total, AR ติดลบ)
            // — เกณฑ์เดียวกับเว็บ/นำเข้าไฟล์ ผ่าน `DocumentSettlementState` (D4-3)
            if (Accounting.Helpers.DocumentSettlementState.WouldOverpay(
                    document.BalanceDue, request.Amount))
                throw new InvalidOperationException(
                    $"ยอดชำระ ({request.Amount:N2}) เกินยอดคงค้าง ({document.BalanceDue:N2}) ของ {document.DocumentNumber} — " +
                    "ถ้าหักมัดจำ ให้ส่ง depositAppliedRef ตอนออกใบกำกับ (drives) ไม่ใช่ยิง payment แยก");

            // idempotency: webhook ชำระเงินอาจถูกยิงซ้ำ (retry) → ถ้ามี Payment ของ
            // เอกสารนี้ด้วย Reference เดียวกันแล้ว คืนผลเดิม (ไม่สร้างซ้ำ) กันเอกสาร
            // ถูกชำระ 2 เท่า (PaidAmount เกิน, BalanceDue ติดลบ, Dr Cash/Cr AR ซ้ำ).
            var refKey = request.ReferenceNo ?? request.ExternalRef;
            if (!string.IsNullOrEmpty(refKey))
            {
                var dup = await _db.Set<Payment>().AsNoTracking().FirstOrDefaultAsync(p =>
                    p.CompanyId == companyId && p.DocumentId == document.Id
                    && p.Reference == refKey && !p.IsDeleted);
                if (dup != null)
                {
                    log.Status = "Duplicate";
                    log.CreatedPaymentId = dup.Id;
                    log.CreatedDocumentId = document.Id;
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Payment already recorded (idempotent)",
                        document.Id, null, null, dup.Id, dup.PaymentNumber);
                }
            }

            // Create payment
            var paymentNumber = await GetNextPaymentNumberAsync(companyId);
            var payment = new Payment
            {
                CompanyId = companyId,
                PaymentNumber = paymentNumber,
                DocumentId = document.Id,
                PaymentDate = NormalizeDate(request.PaymentDate),
                Amount = request.Amount,
                PaymentMethod = ParsePaymentMethod(request.PaymentMethod),
                BankAccount = request.BankAccountName,
                Reference = request.ReferenceNo ?? request.ExternalRef,
                Notes = request.Notes
            };

            _db.Set<Payment>().Add(payment);

            // Update document
            // ยอดค้าง/สถานะ: ตัวตัดสินตัวเดียวกับเว็บ (D4-3) — เดิมที่นี่ clamp
            // ยอดติดลบเป็น 0 เงียบ ๆ ⇒ การรับเงินเกินหายไปโดยไม่มีใครเห็น
            document.PaidAmount += request.Amount;
            var settle = Accounting.Helpers.DocumentSettlementState.Apply(
                document.TotalAmount, document.PaidAmount, document.Status);
            document.BalanceDue = settle.BalanceDue;
            document.Status = settle.Status;
            if (settle.Overpaid)
                _logger.LogWarning(
                    "รับชำระเกินยอดใบ {DocumentNumber} ({Company}) เกิน {Over:N2} บาท — ยอดค้างถูกปิดที่ 0",
                    document.DocumentNumber, companyId, settle.OverpaidAmount);
            if (settle.Status == DocumentStatus.Paid)
            {
                document.AgingDays = null;
                document.AgingLastEvaluatedAt = DateTime.UtcNow;
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
            // Idempotent re-sync: skip if same ExternalRef already created
            if (!string.IsNullOrEmpty(request.ExternalRef))
            {
                var existing = await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId
                    && d.Reference == request.ExternalRef && !d.IsDeleted
                    && d.DocumentType == DocumentType.CreditNote);
                if (existing != null && existing.Status != DocumentStatus.Voided)
                {
                    log.Status = "Skipped";
                    log.CreatedDocumentId = existing.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", existing.Id, existing.ContactId, null, null, existing.DocumentNumber);
                }
            }
            else
            {
                var priorDoc = await TryFindDocumentByExternalIdAsync(companyId, integrationId, "creditnote.created", request.ExternalId);
                if (priorDoc != null)
                {
                    log.Status = "Skipped";
                    log.CreatedDocumentId = priorDoc.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip by ExternalId)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", priorDoc.Id, priorDoc.ContactId, null, null, priorDoc.DocumentNumber);
                }
            }

            var contact = await ResolveContactAsync(companyId, request.CustomerExternalId, request.CustomerName, null);
            var docNumber = await _settingsService.GetNextNumberAsync(companyId, DocumentType.CreditNote, request.DocumentDate);

            // Find original document
            Guid? relatedDocId = request.OriginalDocumentId;
            if (relatedDocId == null && !string.IsNullOrEmpty(request.OriginalInvoiceRef))
            {
                var original = await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Reference == request.OriginalInvoiceRef);
                relatedDocId = original?.Id;
            }

            // §86/9-10 ใบเพิ่มหนี้/ใบลดหนี้ = เอกสารที่ **เราออก** ⇒ เป็นภาษีขาย:
            // อัตราต้องมาจากสถานะจดทะเบียนของบริษัท ไม่ใช่ค่า default `= 7`
            // ที่เคยฝังอยู่ในลายเซ็น BuildDocumentLinesAsync (DTO ของ CN/DN ไม่มี
            // ช่อง vatRate ระดับเอกสาร จึงส่ง null เสมอ — ดู Helpers/PartnerVatRate)
            var noteVatProfile = await GetCompanyVatProfileAsync(companyId);
            var noteVat = Accounting.Helpers.PartnerVatRate.ForIssuedDocument(
                null, noteVatProfile.Registered, noteVatProfile.DefaultRate);
            var lines = await BuildDocumentLinesAsync(companyId, request.Lines, noteVat.Rate,
                outputVatAllowed: noteVatProfile.Registered);
            var subTotal = lines.Sum(l => l.Amount);
            var totalVat = lines.Sum(l => l.VatAmount);

            var document = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = DocumentType.CreditNote,
                Status = DocumentStatus.Approved,
                DocumentDate = NormalizeDate(request.DocumentDate),
                ContactId = contact.Id,
                RelatedDocumentId = relatedDocId,
                Reference = request.ExternalRef,
                BookingNumber = string.IsNullOrWhiteSpace(request.BookingNumber) ? null : request.BookingNumber.Trim(),
                SubTotal = subTotal,
                VatAmount = totalVat,
                TotalAmount = subTotal + totalVat,
                BalanceDue = 0,
                Notes = $"{request.Reason}\n{request.Notes}".Trim(),
                Lines = lines
            };

            _db.Documents.Add(document);
            await _db.SaveChangesAsync();
            await _vendorIntel.TryTrainAsync(companyId, document.Id);

            // Create journal entry for credit note
            // ใบลดหนี้ (ฝั่งรายรับ): Dr รายได้ + Dr ภาษีขาย, Cr ลูกหนี้การค้า
            var journalEntryId = await CreateCreditNoteJournalAsync(companyId, document);

            // Update original document balance if linked.
            // CRITICAL: scope the lookup by companyId — FindAsync(id) would
            // happily return another tenant's invoice and let an integration
            // call authenticated as Company A silently mark Company B's
            // invoice as Paid.
            if (relatedDocId.HasValue)
            {
                var originalDoc = await _db.Documents
                    .FirstOrDefaultAsync(d => d.Id == relatedDocId.Value && d.CompanyId == companyId);
                if (originalDoc != null)
                {
                    // ⚠️ ตัวตั้งคือ PaidAmount · BalanceDue = TotalAmount − PaidAmount (กติกาเดียว
                    // กับ DocumentService สาย CN). เดิมลด BalanceDue ตรง ๆ โดยไม่แตะ PaidAmount
                    // ⇒ สองช่องแยกทาง: (ก) ArApAnalysis ที่อ่าน PaidAmount นับใบนี้ว่ายังไม่จ่าย
                    // ทั้งที่ Balance=0 (ข) รับชำระบางส่วนครั้งถัดไปคำนวณ Balance = Total − Paid
                    // ทับ ⇒ ยอด CN ที่หักไปแล้ว**เด้งกลับมาเป็นยอดค้าง** เงียบ ๆ
                    // (บทเรียน "ตัวเลขคู่ที่ต้องสอดคล้องกัน ต้องมีตัวตั้งตัวเดียว")
                    var apply = Math.Min(document.TotalAmount, Math.Max(0m, originalDoc.BalanceDue));
                    originalDoc.PaidAmount += apply;
                    // D4-3: เกณฑ์ปัดเศษเดิมที่นี่คือ 0.01 (ต่างจาก 0.005 ของเว็บ)
                    // ⇒ ใบที่เหลือ 1 สตางค์ถูกประทับ "ชำระครบ" ทั้งที่ยอดค้างยังโชว์
                    // — ตอนนี้ใช้ตัวตัดสินตัวเดียว (สถานะ Voided ถูกกันไว้ในตัวมันเอง)
                    var cnSettle = Accounting.Helpers.DocumentSettlementState.Apply(
                        originalDoc.TotalAmount, originalDoc.PaidAmount, originalDoc.Status);
                    originalDoc.BalanceDue = cnSettle.BalanceDue;
                    originalDoc.Status = cnSettle.Status;
                    await _db.SaveChangesAsync();
                }
            }

            log.Status = "Success";
            log.CreatedDocumentId = document.Id;
            log.CreatedJournalEntryId = journalEntryId;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Credit Note created", document.Id, contact.Id, journalEntryId, null, docNumber);
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
            // Idempotent re-sync: skip if same ExternalRef already created
            if (!string.IsNullOrEmpty(request.ExternalRef))
            {
                var existing = await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId
                    && d.Reference == request.ExternalRef && !d.IsDeleted
                    && d.DocumentType == DocumentType.DebitNote);
                if (existing != null && existing.Status != DocumentStatus.Voided)
                {
                    log.Status = "Skipped";
                    log.CreatedDocumentId = existing.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", existing.Id, existing.ContactId, null, null, existing.DocumentNumber);
                }
            }
            else
            {
                var priorDoc = await TryFindDocumentByExternalIdAsync(companyId, integrationId, "debitnote.created", request.ExternalId);
                if (priorDoc != null)
                {
                    log.Status = "Skipped";
                    log.CreatedDocumentId = priorDoc.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip by ExternalId)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", priorDoc.Id, priorDoc.ContactId, null, null, priorDoc.DocumentNumber);
                }
            }

            var contact = await ResolveContactAsync(companyId, request.CustomerExternalId, request.CustomerName, null);
            var docNumber = await _settingsService.GetNextNumberAsync(companyId, DocumentType.DebitNote, request.DocumentDate);

            Guid? relatedDocId = request.OriginalDocumentId;
            if (relatedDocId == null && !string.IsNullOrEmpty(request.OriginalInvoiceRef))
            {
                var original = await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.Reference == request.OriginalInvoiceRef);
                relatedDocId = original?.Id;
            }

            // §86/9-10 ใบเพิ่มหนี้/ใบลดหนี้ = เอกสารที่ **เราออก** ⇒ เป็นภาษีขาย:
            // อัตราต้องมาจากสถานะจดทะเบียนของบริษัท ไม่ใช่ค่า default `= 7`
            // ที่เคยฝังอยู่ในลายเซ็น BuildDocumentLinesAsync (DTO ของ CN/DN ไม่มี
            // ช่อง vatRate ระดับเอกสาร จึงส่ง null เสมอ — ดู Helpers/PartnerVatRate)
            var noteVatProfile = await GetCompanyVatProfileAsync(companyId);
            var noteVat = Accounting.Helpers.PartnerVatRate.ForIssuedDocument(
                null, noteVatProfile.Registered, noteVatProfile.DefaultRate);
            var lines = await BuildDocumentLinesAsync(companyId, request.Lines, noteVat.Rate,
                outputVatAllowed: noteVatProfile.Registered);
            var subTotal = lines.Sum(l => l.Amount);
            var totalVat = lines.Sum(l => l.VatAmount);

            var document = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = DocumentType.DebitNote,
                Status = DocumentStatus.Approved,
                DocumentDate = NormalizeDate(request.DocumentDate),
                ContactId = contact.Id,
                RelatedDocumentId = relatedDocId,
                Reference = request.ExternalRef,
                BookingNumber = string.IsNullOrWhiteSpace(request.BookingNumber) ? null : request.BookingNumber.Trim(),
                SubTotal = subTotal,
                VatAmount = totalVat,
                TotalAmount = subTotal + totalVat,
                BalanceDue = subTotal + totalVat,
                Notes = $"{request.Reason}\n{request.Notes}".Trim(),
                Lines = lines
            };

            _db.Documents.Add(document);
            await _db.SaveChangesAsync();
            await _vendorIntel.TryTrainAsync(companyId, document.Id);

            // Create journal entry for debit note
            // ใบเพิ่มหนี้ (ฝั่งรายรับ): Dr ลูกหนี้การค้า, Cr รายได้ + Cr ภาษีขาย
            var journalEntryId = await CreateDebitNoteJournalAsync(companyId, document);

            log.Status = "Success";
            log.CreatedDocumentId = document.Id;
            log.CreatedJournalEntryId = journalEntryId;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Debit Note created", document.Id, contact.Id, journalEntryId, null, docNumber);
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
        request = request with { SummaryDate = NormalizeDate(request.SummaryDate) };
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

            // Validate Dr == Cr
            var totalDr = journalLines.Sum(l => l.DebitAmount);
            var totalCr = journalLines.Sum(l => l.CreditAmount);
            if (totalDr != totalCr)
                throw new InvalidOperationException($"ยอดเดบิต ({totalDr:N2}) ไม่เท่ากับเครดิต ({totalCr:N2}) — ตรวจสอบ account mapping ว่าตั้งค่าครบทั้ง Dr และ Cr");

            // Find fiscal period
            var fiscalPeriod = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
                f.CompanyId == companyId
                && f.StartDate <= request.SummaryDate
                && f.EndDate >= request.SummaryDate
                && f.Status == FiscalPeriodStatus.Open);

            // Create journal entry
            var entryNumber = await GetNextJournalNumberAsync(companyId, "JV");
            var journalEntry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = request.SummaryDate,
                JournalType = JournalType.General,
                Description = request.Description ?? $"สรุปรายวัน {request.SummaryDate:dd/MM/yyyy}",
                Reference = $"DAILY-{request.SummaryDate:yyyyMMdd}",
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                FiscalPeriodId = fiscalPeriod?.Id,
                TotalDebit = totalDr,
                TotalCredit = totalCr,
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

    /// <summary>ผู้ติดต่อกลางสำหรับ "ลูกค้าเงินสด ไม่ประสงค์รับใบกำกับภาษี" —
    /// สร้างครั้งเดียวต่อบริษัท ใช้ซ้ำทุกใบ. Address "-" ให้ §86/4 มีค่าพิมพ์
    /// บนใบกำกับ (แนวปฏิบัติค้าปลีกที่สรรพากรยอมรับ — ผู้ซื้อเคลมภาษีซื้อไม่ได้
    /// อยู่แล้วซึ่งตรงตามที่ผู้ซื้อเลือกเอง).</summary>
    private async Task<Contact> GetOrCreateWalkInContactAsync(Guid companyId)
    {
        var walkIn = await _db.Set<Contact>().FirstOrDefaultAsync(
            c => c.CompanyId == companyId && c.IsWalkInCustomer && !c.IsDeleted);
        if (walkIn != null) return walkIn;

        walkIn = new Contact
        {
            CompanyId = companyId,
            Name = "ลูกค้าเงินสด (ไม่ประสงค์รับใบกำกับภาษี)",
            Address = "-",
            IsCustomer = true,
            IsActive = true,
            IsWalkInCustomer = true,
            ContactType = ContactType.Individual,
        };
        _db.Set<Contact>().Add(walkIn);
        await _db.SaveChangesAsync();
        return walkIn;
    }

    private async Task<Contact> ResolveContactAsync(Guid companyId, string? externalId, string? name, string? taxId)
    {
        Contact? contact = null;

        if (!string.IsNullOrEmpty(taxId))
        {
            // normalize เลขภาษี (ตัวเลขล้วน) — เทียบ == ตรง ๆ พลาดเมื่อ format ต่าง
            // (ขีด/เว้นวรรค) → สร้าง contact ซ้ำทุก sync
            // รอบ 193 ข้อ 20: คีย์เลขภาษี + สาขาตัวเดียวของทุกทางเข้า — payload ใบขายไม่มีช่องสาขาผู้ซื้อ
            // ⇒ "ไม่ระบุสาขา" = แถวสำนักงานใหญ่ก่อน (เดิม FirstOrDefault หยิบแถวไหนก็ได้ของเลขนั้น)
            var taxKey = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(
                _db.Set<Contact>(), companyId, taxId, branchCode: null);
            if (taxKey.ContactId is Guid keyId)
                contact = await _db.Set<Contact>().FirstOrDefaultAsync(c => c.Id == keyId && c.CompanyId == companyId);
        }

        if (contact == null && !string.IsNullOrEmpty(name))
            contact = await _db.Set<Contact>().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Name.ToLower() == name.ToLower() && !c.IsDeleted);

        if (contact == null)
        {
            // ── Auto-enrich from DBD/RD เมื่อ API ส่งมาแค่เลขผู้เสียภาษี ──
            // ระบบภายนอกบางตัวยิงมาแค่ taxId (name ว่าง/= taxId) — ก่อนบันทึก
            // ลองดึงชื่อจริง + ที่อยู่จากกรมพัฒนาธุรกิจการค้า/สรรพากร เพื่อให้
            // ผู้ติดต่อในระบบมีข้อมูลครบ (ชื่อ, ที่อยู่ ใช้ทำ e-Tax / เอกสาร).
            // Best-effort — ถ้า DBD ล่ม/ไม่พบ ใช้ค่าที่ส่งมาตามเดิม.
            string resolvedName = name ?? "";
            string? resolvedAddress = null, resolvedNameEn = null;
            var looksLikeJuristic = !string.IsNullOrWhiteSpace(taxId)
                && taxId.Length == 13 && taxId.All(char.IsDigit);
            // ⚠️ เดิมมีเงื่อนไข `nameMissing` คร่อมอยู่ ⇒ ตรวจทะเบียนเฉพาะตอนต้นทาง
            // **ไม่ส่งชื่อมาเลย** · เคสที่เจอจริงคือต้นทางส่งชื่อ **ผิด** มา (ฟิลด์เหลื่อม/
            // ตัดคำ) ซึ่งเงื่อนไขนั้นข้ามไปทุกครั้ง — ตอนนี้ตรวจเสมอเมื่อเลขเป็นนิติบุคคล
            // แล้วให้ `DbdIdentityGuard` เป็นคนตัดสินว่าทะเบียนชนะได้ไหม
            var dbdDoc = await VerifyAgainstDbdAsync(taxId, name);
            if (dbdDoc.Matched)
            {
                resolvedName = dbdDoc.OfficialName ?? resolvedName;
                resolvedNameEn = dbdDoc.OfficialNameEn;
                resolvedAddress = dbdDoc.OfficialAddress;
                _logger.LogInformation("DBD enrich: taxId {TaxId} → {Name}", taxId, resolvedName);
            }

            contact = new Contact
            {
                CompanyId = companyId,
                Name = !string.IsNullOrWhiteSpace(resolvedName) ? resolvedName
                     : (name ?? (looksLikeJuristic ? $"นิติบุคคล {taxId}" : "ลูกค้าทั่วไป")),
                TaxId = taxId,
                IsCustomer = true,
                IsActive = true,
                Address = resolvedAddress,
                // ชื่ออังกฤษจากทะเบียน — เดิมดึงมาแล้วแต่ไม่เคยถูกเก็บ (ตัวแปรลอย)
                // ⇒ โหมดเอกสารภาษาอังกฤษต้องถอดอักษรเอาเองทั้งที่มีชื่อทางการอยู่
                NameEn = resolvedNameEn,
                // ตัวตัดสิน ภ.ง.ด.3 vs 53 — ทะเบียนยืนยันแล้ว หรือเลขขึ้นต้น "0" = นิติบุคคล
                ContactType = ResolveContactType(null, taxId, dbdDoc.Matched, resolvedName ?? name),
            };
            _db.Set<Contact>().Add(contact);
            await _db.SaveChangesAsync();
        }

        return contact;
    }

    private static string NormalizeTaxId(string? taxId) =>
        string.IsNullOrEmpty(taxId) ? "" : new string(taxId.Where(char.IsDigit).ToArray());

    /// <summary>Resolve/สร้างผู้จำหน่ายจาก integration แบบ "กัน contact ซ้ำ" —
    /// match ตามลำดับความแม่น แล้วค่อยสร้างใหม่ (upsert):
    ///   1. SupplierContactId (Contact.Id ของ NextAcc ตรง ๆ) — แม่นสุด
    ///   2. ExternalId (รหัสผู้ติดต่อของระบบต้นทาง เช่น TakeTime)
    ///   3. TaxId แบบ normalize ตัวเลขล้วน (กัน "0-1055-..." vs "0105512...")
    ///   4. ชื่อ trim + case-insensitive
    /// พบแล้วเติม ExternalId/TaxId ให้ถ้ายังว่าง (enrich) → รอบถัดไป match แม่นขึ้น.
    /// เดิม match ด้วย TaxId exact-string + ชื่อ exact → integration ยิงเลขภาษี
    /// คนละรูปแบบ/ชื่อมีช่องว่าง = สร้าง contact ซ้ำ.</summary>
    private async Task<Contact> ResolveSupplierAsync(Guid companyId, Guid integrationId,
        Guid? supplierContactId, string? supplierExternalId, string? supplierName, string? supplierTaxId)
    {
        var taxDigits = NormalizeTaxId(supplierTaxId);
        var nameTrim = supplierName?.Trim();
        var extId = string.IsNullOrWhiteSpace(supplierExternalId) ? null : supplierExternalId!.Trim();

        Contact? supplier = null;

        // (1) Contact.Id ตรง ๆ
        if (supplierContactId is { } cid && cid != Guid.Empty)
            supplier = await _db.Set<Contact>().FirstOrDefaultAsync(c =>
                c.Id == cid && c.CompanyId == companyId && !c.IsDeleted);

        // (2) External id ของระบบต้นทาง
        if (supplier == null && extId != null)
            supplier = await _db.Set<Contact>().FirstOrDefaultAsync(c =>
                c.CompanyId == companyId && c.ExternalId == extId && !c.IsDeleted);

        // (3) เลขผู้เสียภาษี (+ สาขา) — ตัวจับคู่กลาง Helpers/ContactTaxBranchKey (รอบ 193 ข้อ 20) ·
        //     payload ไม่มีช่องสาขาผู้ขาย ⇒ แถวสำนักงานใหญ่ก่อน แทนแถวไหนก็ได้ของเลขนั้น
        if (supplier == null && taxDigits.Length > 0)
        {
            var taxKey = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(
                _db.Set<Contact>(), companyId, taxDigits, branchCode: null);
            if (taxKey.ContactId is Guid keyId)
                supplier = await _db.Set<Contact>().FirstOrDefaultAsync(c =>
                    c.Id == keyId && c.CompanyId == companyId && !c.IsDeleted);
        }

        // (4) ชื่อ trim + case-insensitive
        if (supplier == null && !string.IsNullOrWhiteSpace(nameTrim))
        {
            var lower = nameTrim.ToLower();
            supplier = await _db.Set<Contact>().FirstOrDefaultAsync(c =>
                c.CompanyId == companyId && c.Name.Trim().ToLower() == lower && !c.IsDeleted);
        }

        if (supplier == null)
        {
            var sysName = await _db.Set<ExternalIntegration>().AsNoTracking()
                .Where(i => i.Id == integrationId).Select(i => i.SystemName).FirstOrDefaultAsync();
            supplier = new Contact
            {
                CompanyId = companyId,
                Name = string.IsNullOrWhiteSpace(nameTrim) ? "ผู้จำหน่ายทั่วไป" : nameTrim,
                TaxId = taxDigits.Length > 0 ? taxDigits : null,
                ExternalId = extId,
                ExternalSystem = extId != null ? sysName : null,
                IsCustomer = false,
                IsSupplier = true,
                IsActive = true
            };
            _db.Set<Contact>().Add(supplier);
            await _db.SaveChangesAsync();
            return supplier;
        }

        // enrich contact เดิม — เติม external id / tax id ที่ยังว่าง เพื่อรอบถัดไป match แม่นขึ้น
        var dirty = false;
        if (string.IsNullOrWhiteSpace(supplier.ExternalId) && extId != null)
        {
            supplier.ExternalId = extId;
            if (string.IsNullOrWhiteSpace(supplier.ExternalSystem))
                supplier.ExternalSystem = await _db.Set<ExternalIntegration>().AsNoTracking()
                    .Where(i => i.Id == integrationId).Select(i => i.SystemName).FirstOrDefaultAsync();
            dirty = true;
        }
        if (string.IsNullOrWhiteSpace(supplier.TaxId) && taxDigits.Length > 0)
        {
            supplier.TaxId = taxDigits;
            dirty = true;
        }
        if (!supplier.IsSupplier) { supplier.IsSupplier = true; dirty = true; }
        if (dirty) await _db.SaveChangesAsync();

        return supplier;
    }

    /// <summary>
    /// Best-effort withholding-tax certificate auto-issue for an
    /// integration-synced purchase document. Returns a short note to append
    /// to the sync response message; never throws — the document and its
    /// journal entry are already committed by the time this runs.
    /// </summary>
    /// <param name="paid">เอกสารนี้ "จ่ายเงินแล้วจริง" ตอนซิงค์หรือไม่ —
    /// ใบสำคัญจ่าย (PV) = จ่ายแล้ว ⇒ ออกใบจริง (Issued) ตามวันจ่าย.
    /// ค่าใช้จ่ายตั้งหนี้ (Credit, PaidAmount = 0) = ยังไม่จ่าย ⇒ ออกเป็น
    /// **ฉบับร่าง** เท่านั้น. ที่มา (P-3): ท.ป.4/2528 เป็น cash basis —
    /// ออกใบจริงตอนตั้งหนี้ทำให้ ภ.ง.ด.3/53 ของเดือนนั้นนำส่งภาษีที่ยังไม่ได้
    /// หักจริง แล้วพอจ่ายจริงเดือนถัดไป guard "ออกใบเต็มจำนวนไปแล้ว" ยัง
    /// บล็อกใบรายงวดอีก ⇒ ผู้ขายไม่ได้ 50 ทวิ ที่ถูกต้องสักใบ.</param>
    private async Task<string> TryAutoGenerateWhtAsync(Guid companyId, Document document, bool paid)
    {
        if (document.WithholdingTaxAmount <= 0) return "";
        if (_whtCertService == null)
        {
            _logger.LogWarning("WHT auto-generate skipped for document {DocId}: WHT service unavailable", document.Id);
            return " (มีภาษีหัก ณ ที่จ่าย — กรุณาออกหนังสือรับรองในระบบ)";
        }
        try
        {
            var cert = await _whtCertService.AutoGenerateFromDocumentAsync(
                companyId, document.Id, autoIssue: paid, "integration-sync");
            _logger.LogInformation("WHT certificate {CertNo} auto-{Mode} for synced document {DocId}",
                cert.CertificateNumber, paid ? "issued" : "drafted", document.Id);
            return paid
                ? $" + ออกหนังสือรับรองหัก ณ ที่จ่าย {cert.CertificateNumber}"
                : $" + เตรียมหนังสือรับรองหัก ณ ที่จ่าย {cert.CertificateNumber} (ฉบับร่าง — ออกจริงเมื่อจ่ายเงิน)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WHT auto-generate failed for synced document {DocId}", document.Id);
            return " (ออกหนังสือรับรองหัก ณ ที่จ่ายอัตโนมัติไม่สำเร็จ — กรุณาออกในระบบ)";
        }
    }

    /// <summary>Resolve the correct VAT GL account with TYPE validation so
    /// VAT can never land on an unrelated account again. The old code used
    /// AccountCode.StartsWith("1140") for input VAT — which collides with
    /// "11400 เงินให้กู้ยืมระยะสั้น" (short-term loans) and silently posted
    /// ภาษีซื้อ there. Correct Thai-COA codes: input VAT = 11610 (ภาษีซื้อ
    /// ภ.พ.30) under 116; output VAT = 21911 (ภาษีขาย ภ.พ.30) under 2191.
    ///   isInput=true  → Asset account, prefer 11610 → 116 → name "ภาษีซื้อ"
    ///   isInput=false → Liability account, prefer 21911 → 2191 → "ภาษีขาย"
    /// Returns null when no account of the RIGHT TYPE exists — caller then
    /// refuses to post rather than guessing.</summary>
    private async Task<ChartOfAccount?> ResolveVatAccountAsync(Guid companyId, bool isInput)
    {
        var wantType = isInput ? AccountType.Asset : AccountType.Liability;
        // 1) Exact standard code.
        var exact = isInput ? "11610" : "21911";
        var acc = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.AccountCode == exact && a.IsActive && a.AccountType == wantType);
        if (acc != null) return acc;
        // 2) Code family (116x input / 2191x output) — strictly typed.
        var family = isInput ? "116" : "2191";
        acc = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive && a.AccountType == wantType
                && a.AccountCode.StartsWith(family)
                && a.AccountCode != "21919")              // 21919 = VAT payable pending, not output-VAT
            .OrderBy(a => a.AccountCode)
            .FirstOrDefaultAsync();
        if (acc != null) return acc;
        // 3) Name match, still type-guarded — handles custom charts.
        var namePart = isInput ? "ภาษีซื้อ" : "ภาษีขาย";
        acc = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive && a.AccountType == wantType
                && a.AccountName.Contains(namePart))
            .OrderBy(a => a.AccountCode)
            .FirstOrDefaultAsync();
        return acc;   // may be null → caller refuses to post
    }

    /// <summary>Sanity-check every JE line before it hits the ledger and
    /// auto-correct what's safely correctable. Catches the class of bug the
    /// partner sent ("VAT debited to a loan account"): when a line is clearly
    /// a VAT line (description ภาษีซื้อ/ภาษีขาย) but its resolved account is
    /// NOT a VAT account, reroute it to the proper VAT account. Returns false
    /// only for errors we can't fix (missing/inactive account, unfixable
    /// mismatch) so the caller refuses to post and leaves the doc for review.
    /// "ถ้าอันไหนผิดแน่นอน ต้องไม่ปล่อยผ่าน".</summary>
    private async Task<bool> ValidateAndAutofixJournalAsync(
        Guid companyId, List<JournalEntryLine> lines, Document document)
    {
        var acctIds = lines.Select(l => l.AccountId).Distinct().ToList();
        var accts = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && acctIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id);

        // ── JournalPostingGuard: ตรวจโครงสร้างก่อน (กฎชุดเดียวกับ AutoPost) ──
        // เคสจริง: JE จาก integration สมดุลเป๊ะแต่เครดิตทั้งใบลง 21917 (WHT)
        // ไม่มีขาเจ้าหนี้เลย — สมดุลผ่าน แต่โครงสร้างผิดแน่ ต้อง block ที่นี่
        var guardLines = lines
            .Where(l => accts.ContainsKey(l.AccountId))
            .Select(l => new JournalPostingGuard.LineFacts(
                accts[l.AccountId].AccountCode, accts[l.AccountId].AccountType,
                l.DebitAmount, l.CreditAmount))
            .ToList();
        // เส้น integration ลง JE ด้วยยอดเดียวกับเอกสารตรง ๆ (ไม่มี Conv) ⇒ rate 1
        // ระบุชัดไว้กันคนแก้ทีหลังเผลอใส่ doc.ExchangeRate แล้วเทียบผิดหน่วย
        var guardFindings = JournalPostingGuard.Validate(guardLines,
            new JournalPostingGuard.DocFacts(
                document.DocumentType, document.SubTotal, document.VatAmount,
                document.WithholdingTaxAmount, document.TotalAmount, document.IsDeposit,
                ExchangeRate: 1m));
        var guardError = JournalPostingGuard.ErrorSummary(guardFindings, document.DocumentNumber);
        if (guardError != null)
        {
            // ไม่โพสต์ — เอกสารยังซิงค์ได้ แต่ JE ที่โครงสร้างผิดห้ามเข้าแยกประเภท
            // (JournalAnomalyService จะรายงาน "เอกสารอนุมัติแล้วแต่ไม่มี JE" ให้เห็น)
            _logger.LogError("Integration JE ถูก block โดย posting guard: {Error}", guardError);
            return false;
        }
        foreach (var w in guardFindings.Where(f => !f.IsError))
            _logger.LogWarning("Integration JE guard เตือน {Doc}: [{Rule}] {Msg}",
                document.DocumentNumber, w.RuleCode, w.Message);

        foreach (var line in lines)
        {
            // 1) บัญชีต้องมีจริง + active
            if (!accts.TryGetValue(line.AccountId, out var acc) || !acc.IsActive)
            {
                _logger.LogWarning("JE line ใช้บัญชีที่ไม่พบ/ปิดใช้งาน (Acc {Acc}) — เอกสาร {Doc}, ไม่โพสต์",
                    line.AccountId, document.DocumentNumber);
                return false;
            }

            var desc = line.Description ?? "";
            var isVatInputLine = desc.Contains("ภาษีซื้อ");
            var isVatOutputLine = desc.Contains("ภาษีขาย");
            if (!isVatInputLine && !isVatOutputLine) continue;

            // 2) VAT line ต้องอยู่บัญชีภาษีที่ถูกต้อง (input=116x Asset,
            //    output=2191x Liability). ถ้าไม่ → reroute อัตโนมัติ.
            var wantInput = isVatInputLine;
            var codeOk = wantInput
                ? acc.AccountCode.StartsWith("116") && acc.AccountType == AccountType.Asset
                : acc.AccountCode.StartsWith("2191") && acc.AccountType == AccountType.Liability;
            if (codeOk) continue;

            var correct = await ResolveVatAccountAsync(companyId, wantInput);
            if (correct == null)
            {
                _logger.LogWarning("VAT line ({Kind}) ลงบัญชีผิด ({BadCode} {BadName}) และหาบัญชีภาษีที่ถูกต้อง" +
                    "ไม่ได้ — เอกสาร {Doc}, ไม่โพสต์เพื่อกันลงผิด",
                    wantInput ? "ซื้อ" : "ขาย", acc.AccountCode, acc.AccountName, document.DocumentNumber);
                return false;
            }
            _logger.LogWarning("แก้บัญชี VAT อัตโนมัติ: {Kind} เคยลง {BadCode} {BadName} → {GoodCode} {GoodName} " +
                "(เอกสาร {Doc})", wantInput ? "ซื้อ" : "ขาย", acc.AccountCode, acc.AccountName,
                correct.AccountCode, correct.AccountName, document.DocumentNumber);
            line.AccountId = correct.Id;   // reroute to the right VAT account
        }
        return true;
    }

    /// <summary>ผังบัญชีที่ partner ส่งมา "อยู่ผิดฝั่ง" หรือไม่ — ฝั่งรายจ่าย
    /// ห้ามเป็นบัญชีรายได้ และฝั่งรายรับห้ามเป็นบัญชีค่าใช้จ่าย.
    /// ที่มา (P-8): BuildDocumentLinesAsync รับ AccountCode อะไรก็ได้ที่ active
    /// ⇒ partner ยิงค่าใช้จ่ายมาพร้อม AccountCode "41000" ได้ → JE เป็น
    /// Dr 41000 (ล้างรายได้) แทน Dr ค่าใช้จ่าย. JournalPostingGuard จับไม่ได้
    /// เพราะ Dr=Cr ยังสมดุลและมีขาเจ้าหนี้ครบ ⇒ งบกำไรขาดทุนเพี้ยนทั้งสองบรรทัด
    /// โดยไม่มีใครเห็น. เช็คเฉพาะชนิดที่ "ผิดแน่นอน" (ไม่แตะ Asset/Liability
    /// เพราะใบมัดจำ/สินค้าคงเหลือใช้จริง).</summary>
    private static bool IsWrongSideAccount(AccountType type, bool expenseSide)
        => expenseSide ? type == AccountType.Revenue : type == AccountType.Expense;

    /// <summary>
    /// สถานะ VAT ของบริษัทนี้ — **จุดอ่านเดียวของทั้งไฟล์** (เดิมไม่มีเลย จึงเกิด
    /// `request.VatRate` ถอยไปหาเลข 7 ตายตัว 6 จุด + `defaultVatRate = 7` อีก 2 จุด
    /// โดยไม่มีใครรู้ว่า tenant จด VAT หรือไม่ — DECISION_AUDIT §3 D8-4)
    ///
    /// <para>อ่านจาก <c>CompanySettings</c> ชุดเดียวกับ <c>DocumentService</c> และ
    /// ด่าน §90/2 ที่มีอยู่แล้วในไฟล์นี้ (คู่ <c>Company.IsVatRegistered</c>/<c>VatRate</c>
    /// ถูก sync ให้ตรงกันเสมอที่ <c>CompanyService</c>/<c>SettingsService</c>)</para>
    ///
    /// <para>ยังไม่เคยตั้งค่า = ถือว่า "จด + 7%" — ตรงกับพฤติกรรมเดิมของบริษัทที่ยัง
    /// ไม่แตะหน้าตั้งค่า จึงไม่มีใครถูกบล็อกโดยไม่รู้ตัวจากการแก้รอบนี้</para>
    /// </summary>
    private async Task<(bool Registered, decimal DefaultRate)> GetCompanyVatProfileAsync(Guid companyId)
    {
        var row = await _db.CompanySettings.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => new { c.VatRegistered, c.DefaultVatRate })
            .FirstOrDefaultAsync();
        var rate = row?.DefaultVatRate ?? Accounting.Helpers.PartnerVatRate.StatutoryRate;
        return (row?.VatRegistered ?? true,
            rate > 0m ? rate : Accounting.Helpers.PartnerVatRate.StatutoryRate);
    }

    /// <param name="outputVatAllowed">false = บริษัทยังไม่จด VAT และเอกสารนี้ **เราเป็นผู้ออก**
    /// ⇒ บรรทัดที่คู่ค้าสั่งให้เก็บ VAT ต้องถูกปฏิเสธดัง ๆ ไม่ใช่เงียบ ๆ ตัดเป็น 0
    /// (ไม่งั้นด่านระดับเอกสารถูกข้ามด้วยอัตรารายบรรทัด — ราก R5)</param>
    /// <param name="inputVatClaimable">false = บริษัทไม่จด VAT ⇒ ภาษีซื้อบนใบของผู้ขาย
    /// เคลมไม่ได้ รวมเป็นต้นทุน (ยอดที่ต้องจ่ายผู้ขาย **ไม่เปลี่ยน**)</param>
    private async Task<List<DocumentLine>> BuildDocumentLinesAsync(
        Guid companyId, List<InboundInvoiceLineRequest> lines, decimal defaultVatRate,
        bool expenseSide = false, bool includeVat = false,
        bool outputVatAllowed = true, bool inputVatClaimable = true)
    {
        // Resolve any line-level AccountCode the partner sent → ChartOfAccount
        // id, so the document line carries its real GL account and the JE
        // posts there. Previously AccountCode was dropped (AccountId never set)
        // and the journal fell back to the first generic expense account.
        var codes = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.AccountCode))
            .Select(l => l.AccountCode!.Trim())
            .Distinct()
            .ToList();
        var codeToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (codes.Count > 0)
        {
            var rows = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive
                            && codes.Contains(a.AccountCode))
                .Select(a => new { a.AccountCode, a.AccountName, a.AccountType, a.Id })
                .ToListAsync();

            // ผิดฝั่ง = fail loud (ไม่ใช่เงียบ ๆ fallback) — sync log เก็บเหตุผล
            // ให้ partner แก้ mapping ฝั่งเขา ดีกว่าปล่อยตัวเลขผิดเข้าแยกประเภท
            var wrongSide = rows
                .Where(r => IsWrongSideAccount(r.AccountType, expenseSide))
                .Select(r => $"{r.AccountCode} {r.AccountName}")
                .ToList();
            if (wrongSide.Count > 0)
                throw new InvalidOperationException(
                    $"ผังบัญชีที่ส่งมาอยู่ผิดฝั่งเอกสาร ({(expenseSide ? "รายจ่ายห้ามใช้บัญชีรายได้" : "รายรับห้ามใช้บัญชีค่าใช้จ่าย")}): "
                    + string.Join(", ", wrongSide)
                    + " — กรุณาแก้ account mapping ฝั่งระบบต้นทาง");

            foreach (var r in rows) codeToId[r.AccountCode] = r.Id;

            // รหัสที่หาไม่เจอ/ปิดใช้งาน → บรรทัดจะตกไปบัญชีทั่วไป ต้องเห็นใน log
            var unresolved = codes.Where(c => !codeToId.ContainsKey(c)).ToList();
            if (unresolved.Count > 0)
                _logger.LogWarning("Integration ส่ง AccountCode ที่ไม่พบ/ปิดใช้งาน: {Codes} (company {Cid}) " +
                    "— บรรทัดเหล่านี้จะลงบัญชีทั่วไปแทน", string.Join(", ", unresolved), companyId);
        }

        return lines.Select((line, i) =>
        {
            var lineAmount = line.Quantity * line.UnitPrice;
            var lineDiscount = line.DiscountAmount ?? 0;
            var lineNet = lineAmount - lineDiscount;
            var lineVatRate = line.VatRate ?? defaultVatRate;
            // §90/2 — ผู้ไม่จดทะเบียนเรียกเก็บ VAT ไม่ได้ แม้คู่ค้าสั่งมารายบรรทัด
            if (!outputVatAllowed && (lineVatRate > 0m || line.VatAmount > 0m))
                throw new InvalidOperationException(
                    Accounting.Helpers.PartnerVatRate.BlockedMessage(lineVatRate));
            // ⚠️ เดิมคิด exclusive เสมอ ไม่รู้จัก IncludeVat ⇒ VAT ไม่เคยถูกบวก
            // เข้ายอดรวม แล้ว JE ถูกตีตกทั้งใบ (ดูหมายเหตุที่ SplitLineVat)
            (lineNet, var lineVat) = Accounting.Helpers.DocumentLineVatConvention.SplitLine(lineNet, lineVatRate, line.VatAmount, includeVat);
            var lineWhtRate = line.WithholdingTaxRate ?? 0;
            var lineWht = Math.Round(lineNet * lineWhtRate / 100, 2, MidpointRounding.AwayFromZero);

            Guid? accountId = null;
            if (!string.IsNullOrWhiteSpace(line.AccountCode)
                && codeToId.TryGetValue(line.AccountCode!.Trim(), out var aid))
                accountId = aid;

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
                VatAmount = lineVat,
                WithholdingTaxRate = lineWhtRate,
                WithholdingTaxAmount = lineWht,
                // ภาษีซื้อ: บริษัทไม่จด VAT = เคลมไม่ได้ทุกบรรทัด (รวมเป็นต้นทุน) ·
                // บรรทัดที่ไม่มี VAT ก็ไม่มีอะไรให้เคลม — กติกาเดียวกับเส้นคีย์มือ
                // ใน DocumentService (เดิมเส้น integration ไม่เคยตั้งค่านี้เลย
                // ⇒ default true ⇒ ภาษีซื้อของผู้ไม่จด VAT ถูกนับเป็นเคลมได้)
                IsVatClaimable = !expenseSide
                    || Accounting.Helpers.PartnerVatRate.InputVatClaimable(inputVatClaimable, lineVat),
                VatNonClaimableReason = expenseSide && !inputVatClaimable && lineVat > 0m
                    ? Accounting.Helpers.PartnerVatRate.NotVatRegisteredReason : null,
                // Honour the partner's AccountCode → real GL account on the line.
                AccountId = accountId
            };
        }).ToList();
    }

    /// <summary>สร้าง "บรรทัด JE" จาก mapping/มาตรฐาน (แยกออกมาให้ reuse ได้
    /// ทั้ง create ใหม่ และ in-place update). คืน null เมื่อสร้างไม่ได้.
    ///
    /// <para><paramref name="onSkip"/> = เหตุผลที่สร้างไม่ได้ ส่งกลับให้ผู้เรียก
    /// ไป **แสดงให้ผู้ใช้เห็น** — เดิมเหตุผลอยู่ใน LogWarning อย่างเดียว ซึ่ง
    /// ผู้ใช้และคู่ค้าไม่มีทางเห็น (ดู PostMappingJournalAsync)</para></summary>
    private async Task<(List<JournalEntryLine> Lines, JournalType Jt, string Prefix)?> BuildIntegrationJournalLinesAsync(
        Guid companyId, Guid integrationId, Document document, string type,
        Action<string>? onSkip = null)
    {
        // Load account mappings for this integration
        var mappings = await _db.Set<IntegrationAccountMapping>()
            .Where(m => m.IntegrationId == integrationId && m.CompanyId == companyId && m.IsActive && m.AutoCreateJournal && !m.IsDeleted)
            .ToListAsync();

        var journalLines = new List<JournalEntryLine>();
        int lineOrder = 1;

        // Determine journal type and entry prefix based on document type
        var isRevenue = type == "invoice"; // ฝั่งรายรับ
        var isExpense = type == "expense"; // ฝั่งค่าใช้จ่าย
        var journalType = type == "payment" ? JournalType.CashReceipts
            : isRevenue ? JournalType.Sales
            : isExpense ? JournalType.Purchase
            : JournalType.General;
        var prefix = journalType switch
        {
            JournalType.Sales => "SV",
            JournalType.Purchase => "UV",
            JournalType.CashReceipts => "RV",
            JournalType.CashPayments => "PV",
            _ => "JV-INT"
        };

        if (mappings.Any())
        {
            // ===== Mode 1: Use configured mappings =====
            // For each document line, find matching mapping
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

            // Mapped lines อยู่ที่ NET (ไม่รวม VAT) และ balanced net อยู่แล้ว →
            // เดิมเช็ค totalDr!=totalCr (ไม่มีวันจริง) → VAT ถูกตกทิ้งเสมอ →
            // ลูกหนี้/เจ้าหนี้ต่ำกว่า gross + ภาษีขาย/ซื้อ (21911/11610) ไม่ถูกลง
            // (ภ.พ.30 ขาด). แก้: gross up ฝั่ง "เงิน" (ลูกหนี้ฝั่งขาย/เจ้าหนี้ฝั่งซื้อ)
            // ด้วยยอด VAT + เพิ่มบรรทัด VAT ให้ JE = gross ของเอกสาร.
            if (journalLines.Any() && document.VatAmount > 0)
            {
                var vatAccount = await ResolveVatAccountAsync(companyId, isInput: !isRevenue);
                var moneyAccountId = isRevenue
                    ? journalLines.FirstOrDefault(l => l.DebitAmount > 0)?.AccountId   // ลูกหนี้
                    : journalLines.FirstOrDefault(l => l.CreditAmount > 0)?.AccountId; // เจ้าหนี้
                if (vatAccount != null && moneyAccountId.HasValue)
                {
                    if (isRevenue)
                    {
                        journalLines.Add(new JournalEntryLine { AccountId = moneyAccountId.Value,
                            DebitAmount = document.VatAmount, Description = "ปรับลูกหนี้รวมภาษี", LineOrder = lineOrder++ });
                        journalLines.Add(new JournalEntryLine { AccountId = vatAccount.Id,
                            CreditAmount = document.VatAmount, Description = "ภาษีขาย", LineOrder = lineOrder++ });
                    }
                    else
                    {
                        journalLines.Add(new JournalEntryLine { AccountId = vatAccount.Id,
                            DebitAmount = document.VatAmount, Description = "ภาษีซื้อ", LineOrder = lineOrder++ });
                        journalLines.Add(new JournalEntryLine { AccountId = moneyAccountId.Value,
                            CreditAmount = document.VatAmount, Description = "ปรับเจ้าหนี้รวมภาษี", LineOrder = lineOrder++ });
                    }
                }
            }
        }

        // ===== Mode 2: Auto-generate standard journal (no mapping needed) =====
        if (!journalLines.Any())
        {
            // Standard Thai accounting journal entry
            if (isRevenue)
            {
                // Revenue Invoice/Tax Invoice:
                // Dr: ลูกหนี้การค้า (113xx) = TotalAmount (รวม VAT)
                // Cr: รายได้ (4xxxx) = SubTotal (ก่อน VAT)
                // Cr: ภาษีขาย (2151x) = VatAmount
                var arAccount = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("113") && a.IsActive);
                var revenueAccount = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountType == AccountType.Revenue && a.IsActive);
                var vatAccount = document.VatAmount > 0
                    ? await ResolveVatAccountAsync(companyId, isInput: false)
                    : null;

                if (arAccount == null || revenueAccount == null)
                {
                    Skip(onSkip, "ไม่พบผังบัญชีลูกหนี้การค้า/รายได้ที่ใช้งานอยู่");
                    return null;
                }

                // Dr: ลูกหนี้การค้า = ยอดรวม (รวม VAT)
                journalLines.Add(new JournalEntryLine
                {
                    AccountId = arAccount.Id,
                    DebitAmount = document.TotalAmount,
                    Description = $"ลูกหนี้ - {document.DocumentNumber}",
                    LineOrder = lineOrder++
                });

                // Cr: รายได้ = ยอดก่อน VAT
                journalLines.Add(new JournalEntryLine
                {
                    AccountId = revenueAccount.Id,
                    CreditAmount = document.SubTotal,
                    Description = $"รายได้ - {document.DocumentNumber}",
                    LineOrder = lineOrder++
                });

                // Cr: ภาษีขาย = VAT
                if (vatAccount != null && document.VatAmount > 0)
                {
                    journalLines.Add(new JournalEntryLine
                    {
                        AccountId = vatAccount.Id,
                        CreditAmount = document.VatAmount,
                        Description = $"ภาษีขาย - {document.DocumentNumber}",
                        LineOrder = lineOrder++
                    });
                }
            }
            else if (isExpense)
            {
                // Expense (role separation — หลักบัญชีไทย):
                // Dr: ค่าใช้จ่าย (5xxxx) = SubTotal
                // Dr: ภาษีซื้อ (1140x) = VatAmount (ถ้ามี)
                // Cr (priority):
                //   1. contact.DefaultApAccountId — pinned per-supplier override
                //      (matches DocumentService.ResolvePayableAccountAsync so
                //      web + partner sync land on the SAME account; e.g. a
                //      director contact pinned to 21230 เจ้าหนี้กรรมการ posts
                //      consistently from both surfaces).
                //   2. 21220 เจ้าหนี้อื่น — the canonical non-trade AP for the
                //      Expense doc (NOT a supplier trade invoice).
                //   3. 212 family / legacy 211 — keeps custom charts posting.
                Contact? expenseContact = null;
                if (document.ContactId != Guid.Empty)
                    expenseContact = await _db.Contacts.AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == document.ContactId && c.CompanyId == companyId);
                ChartOfAccount? apAccount = null;
                if (expenseContact?.DefaultApAccountId is Guid pinnedApId)
                    apAccount = await _db.ChartOfAccounts
                        .FirstOrDefaultAsync(a => a.Id == pinnedApId && a.CompanyId == companyId && a.IsActive);
                apAccount ??= await _db.ChartOfAccounts
                        .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "21220" && a.IsActive)
                    ?? await _db.ChartOfAccounts
                        .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith("212") && a.IsActive)
                        .OrderBy(a => a.AccountCode)
                        .FirstOrDefaultAsync()
                    ?? await _db.ChartOfAccounts
                        .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("211") && a.IsActive);
                var expenseAccount = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountType == AccountType.Expense && a.IsActive);
                var vatAccount = document.VatAmount > 0
                    ? await ResolveVatAccountAsync(companyId, isInput: true)
                    : null;

                if (apAccount == null || expenseAccount == null)
                {
                    Skip(onSkip, "ไม่พบผังบัญชีเจ้าหนี้การค้า/ค่าใช้จ่ายที่ใช้งานอยู่");
                    return null;
                }
                if (document.VatAmount > 0 && vatAccount == null)
                {
                    Skip(onSkip, "เอกสารมี VAT แต่ไม่พบบัญชีภาษีซื้อในผังบัญชี");
                    return null;
                }

                // Dr: ค่าใช้จ่าย — ONE debit per document line using that line's
                // resolved GL account (from the partner's AccountCode) so each
                // category posts to its own account. Falls back to the generic
                // expense account only for lines that didn't carry/resolve a
                // code. (Previously this was a single generic-expense line for
                // the whole SubTotal — the limitation the partner reported.)
                var expenseLines = document.Lines
                    .Where(l => !l.IsDeleted)
                    .OrderBy(l => l.LineOrder)
                    .ToList();
                if (expenseLines.Count > 0)
                {
                    foreach (var dl in expenseLines)
                        journalLines.Add(new JournalEntryLine
                        {
                            AccountId = dl.AccountId ?? expenseAccount.Id,
                            DebitAmount = dl.Amount,
                            Description = string.IsNullOrWhiteSpace(dl.Description)
                                ? $"ค่าใช้จ่าย - {document.DocumentNumber}" : dl.Description,
                            LineOrder = lineOrder++
                        });
                }
                else
                {
                    journalLines.Add(new JournalEntryLine
                    {
                        AccountId = expenseAccount.Id,
                        DebitAmount = document.SubTotal,
                        Description = $"ค่าใช้จ่าย - {document.DocumentNumber}",
                        LineOrder = lineOrder++
                    });
                }

                // Dr: ภาษีซื้อ
                if (vatAccount != null && document.VatAmount > 0)
                {
                    journalLines.Add(new JournalEntryLine
                    {
                        AccountId = vatAccount.Id,
                        DebitAmount = document.VatAmount,
                        Description = $"ภาษีซื้อ - {document.DocumentNumber}",
                        LineOrder = lineOrder++
                    });
                }

                // Cr: เจ้าหนี้การค้า (= TotalAmount = SubTotal+VAT−WHT)
                journalLines.Add(new JournalEntryLine
                {
                    AccountId = apAccount.Id,
                    CreditAmount = document.TotalAmount,
                    Description = $"เจ้าหนี้ - {document.DocumentNumber}",
                    LineOrder = lineOrder++
                });

                // Cr: ภาษีหัก ณ ที่จ่ายค้างจ่าย — เดิมไม่มีบรรทัดนี้ → Dr (SubTotal+VAT)
                // > Cr เจ้าหนี้ (SubTotal+VAT−WHT) → JE ไม่ balance → return null →
                // ค่าใช้จ่ายที่มี WHT "ไม่ลง GL เลย" (ไม่มีทั้งค่าใช้จ่าย/ภาษีซื้อ/
                // เจ้าหนี้/WHT payable). 21917 นิติบุคคล / 21916 บุคคล.
                if (document.WithholdingTaxAmount > 0)
                {
                    // ⚠️ ลำดับรหัสต้องมาจาก `Helpers/WhtPayableAccount` ตัวเดียว —
                    // เดิมที่นี่ตัดสินจาก `ContactType` ดิบ (default = Individual และมี
                    // 12 ทางเข้าที่ไม่เคยตั้งค่า) **และไม่รู้จัก 21918 (ภ.ง.ด.54) เลย**
                    // ⇒ WHT ของการจ่ายต่างประเทศตกไปกอง ภ.ง.ด.3/53 ⇒ ตอนกดนำส่งจะ
                    // Dr บัญชีที่ไม่มียอด = ยอดค้างที่ล้างไม่ได้ (ผลตรวจรอบ 180)
                    var whtChain = Accounting.Helpers.WhtPayableAccount.CodeChain(
                        document.IsForeignService, expenseContact?.CountryCode, expenseContact?.TaxId,
                        expenseContact?.ContactType ?? Models.Enums.ContactType.Individual,
                        expenseContact?.Name);
                    ChartOfAccount? whtAcc = null;
                    foreach (var code in whtChain)
                    {
                        whtAcc = await _db.ChartOfAccounts.FirstOrDefaultAsync(
                            a => a.CompanyId == companyId && a.IsActive && a.AccountCode == code);
                        if (whtAcc != null) break;
                    }
                    if (whtAcc != null)
                        journalLines.Add(new JournalEntryLine
                        {
                            AccountId = whtAcc.Id,
                            CreditAmount = document.WithholdingTaxAmount,
                            Description = $"ภาษีหัก ณ ที่จ่ายค้างจ่าย - {document.DocumentNumber}",
                            LineOrder = lineOrder++
                        });
                    else
                        _logger.LogWarning("ไม่พบบัญชี WHT payable (21916/21917) company {Cid} — เอกสาร {Doc} มี WHT แต่ลง JE ไม่ได้",
                            companyId, document.DocumentNumber);
                }
            }
            else
            {
                Skip(onSkip, $"ไม่รู้จักชนิดเอกสาร \"{type}\" จึงสร้างรายการบัญชีอัตโนมัติไม่ได้");
                return null;
            }
        }

        if (!journalLines.Any())
        {
            Skip(onSkip, "สร้างบรรทัดบัญชีจาก mapping ไม่ได้เลยสักบรรทัด");
            return null;
        }

        // Validate Dr == Cr
        var totalDebit = journalLines.Sum(l => l.DebitAmount);
        var totalCredit = journalLines.Sum(l => l.CreditAmount);
        if (totalDebit != totalCredit)
        {
            Skip(onSkip, $"รายการบัญชีไม่สมดุล Dr {totalDebit:N2} ≠ Cr {totalCredit:N2}");
            return null;
        }

        return (journalLines, journalType, prefix);
    }

    /// <summary>ข้อความต่อท้ายคำตอบที่ส่งกลับคู่ค้า เมื่อเอกสารถูกสร้างแต่ยังไม่ได้
    /// ลงบัญชี — คู่ค้าต้องรู้ว่างานยังไม่จบ ไม่ใช่เห็นแค่คำว่า created</summary>
    private static string JeSkipSuffix(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "" : $" ⚠ ยังไม่ลงบัญชี: {reason}";

    /// <summary>บันทึกเหตุผลที่ "ไม่สร้างรายการบัญชี" ลง log ของเซิร์ฟเวอร์ **และ**
    /// ส่งต่อให้ผู้เรียก — จุดเดียวที่ทั้งสองอย่างเกิดพร้อมกัน ห้ามเขียนแยก</summary>
    private void Skip(Action<string>? onSkip, string reason)
    {
        _logger.LogWarning("ไม่สร้างรายการบัญชีจาก integration — {Reason}", reason);
        onSkip?.Invoke(reason);
    }

    /// <summary>
    /// ลงบัญชีจาก mapping แล้ว **ถ้าลงไม่ได้ ต้องดังพอให้คนเห็น**
    ///
    /// ═══ ที่มา (defect class ที่หนักที่สุดในเรพนี้) ═══
    /// เอกสารจาก integration ถูกสร้างเป็น <c>Status = Approved</c> เสมอ ⇒
    /// <c>TaxService</c> นับเข้า ภ.พ.30 ทันที. แต่การลงบัญชีมีทางออก null ถึง
    /// <b>7 ทาง</b> (ไม่พบผังลูกหนี้/รายได้ · ไม่พบผังเจ้าหนี้/ค่าใช้จ่าย · ไม่พบ
    /// บัญชีภาษีซื้อ · ชนิดเอกสารไม่รู้จัก · สร้างบรรทัดไม่ได้ · Dr≠Cr · ด่าน
    /// โครงสร้างไม่ผ่าน) และทุกทาง**เขียนแค่ LogWarning** แล้วเดินต่อ ⇒ คู่ค้าได้
    /// <c>success: true "Invoice created"</c>, log ขึ้น <c>Success</c>, เอกสาร
    /// อยู่ในระบบ แต่ <b>ไม่มีรายการบัญชีเลย</b> ⇒ **ภ.พ.30 ไม่ตรง GL ถาวร**
    /// โดยไม่มีใครรู้ (ร่องรอยเดียวคือ log ที่ไม่มีใครเปิดอ่าน)
    ///
    /// ที่นี่แปลงความเงียบเป็นเสียง 3 ทาง — ทำที่เดียวเพื่อไม่ให้ 6 จุดเรียกใช้
    /// drift กัน (กฎเหล็ก #4 E "ห้ามกลืน error ใน payment/stock/JE path"):
    /// <list type="number">
    /// <item><b>บนตัวเอกสาร</b> — ต่อเหตุผลเข้า <c>Notes</c> ด้วยหัวข้อ
    ///   <c>[ยังไม่ลงบัญชี]</c> ให้ผู้ใช้เห็นตอนเปิดใบ (ไม่ใช่แค่ใน log)</item>
    /// <item><b>บน sync log</b> — สถานะเป็น <c>PartialSuccess</c> ไม่ใช่
    ///   <c>Success</c> + ใส่เหตุผลใน <c>ErrorMessage</c></item>
    /// <item><b>ในคำตอบที่ส่งกลับคู่ค้า</b> — ผู้เรียกเอา <c>SkipReason</c>
    ///   ไปต่อท้ายข้อความ (ดูจุดเรียก)</item>
    /// </list>
    /// <para>ทำไมไม่ throw ทิ้งทั้งก้อน: เอกสารของคู่ค้าจะหายไปเลยและคู่ค้าส่วนใหญ่
    /// ไม่ retry — เก็บเอกสารไว้แล้วบอกให้ชัดว่ายังไม่ลงบัญชี ผู้ใช้แก้ผังบัญชี
    /// แล้วสั่งลงบัญชีใหม่จากหน้าเอกสารได้ ซึ่งกู้คืนได้จริง</para>
    /// </summary>
    private async Task<(Guid? JournalEntryId, string? SkipReason)> PostMappingJournalAsync(
        Guid companyId, Guid integrationId, Document document, string type, IntegrationSyncLog log)
    {
        string? skipReason = null;
        var jeId = await CreateJournalFromMappingsAsync(
            companyId, integrationId, document, type, r => skipReason ??= r);
        if (jeId != null) return (jeId, null);

        skipReason ??= "สร้างรายการบัญชีอัตโนมัติไม่สำเร็จ (ไม่ทราบสาเหตุ)";
        var note = $"[ยังไม่ลงบัญชี] {skipReason} — เอกสารนี้เข้ารายงานภาษีแล้วแต่ยังไม่มี"
                 + "รายการบัญชี กรุณาแก้ผังบัญชีแล้วสั่งลงบัญชีใหม่จากหน้าเอกสาร";
        document.Notes = string.IsNullOrWhiteSpace(document.Notes)
            ? note : document.Notes + "\n" + note;
        log.Status = "PartialSuccess";
        log.ErrorMessage = string.IsNullOrWhiteSpace(log.ErrorMessage)
            ? note : log.ErrorMessage + "\n" + note;
        await _db.SaveChangesAsync();
        return (null, skipReason);
    }

    private async Task<Guid?> CreateJournalFromMappingsAsync(Guid companyId, Guid integrationId, Document document, string type,
        Action<string>? onSkip = null)
    {
        var built = await BuildIntegrationJournalLinesAsync(companyId, integrationId, document, type, onSkip);
        if (built == null) return null;
        var (journalLines, journalType, prefix) = built.Value;

        // ด่านตรวจโครงสร้าง (JournalPostingGuard) — เส้นทางนี้เดิม**ไม่ผ่าน
        // การตรวจเลย** (ValidateAndAutofix ถูกเรียกเฉพาะ PV path) ⇒ mapping
        // ของ partner ที่ตั้งบัญชีผิดทำให้เครดิตทั้งใบลง 21917 ได้เงียบ ๆ
        if (!await ValidateAndAutofixJournalAsync(companyId, journalLines, document))
        {
            Skip(onSkip, "ด่านตรวจโครงสร้างรายการบัญชีไม่ผ่าน (ผังบัญชีที่ mapping ชี้ไปผิดประเภท)");
            return null;
        }

        var fiscalPeriod = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId
            && f.StartDate <= document.DocumentDate
            && f.EndDate >= document.DocumentDate
            && f.Status == FiscalPeriodStatus.Open);

        var entryNumber = await GetNextJournalNumberAsync(companyId, prefix);
        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = NormalizeDate(document.DocumentDate),
            JournalType = journalType,
            Description = $"Auto: {document.DocumentNumber}",
            Reference = document.DocumentNumber,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            SourceDocumentId = document.Id,
            FiscalPeriodId = fiscalPeriod?.Id,
            TotalDebit = journalLines.Sum(l => l.DebitAmount),
            TotalCredit = journalLines.Sum(l => l.CreditAmount),
            Lines = journalLines
        };

        _db.JournalEntries.Add(je);
        await _db.SaveChangesAsync();
        return je.Id;
    }

    /// <summary>งวดของวันที่นี้ยังเปิดอยู่ไหม — "ไม่มี period row" ถือว่าเปิด
    /// (convention เดียวกับจุดอื่นในระบบ).</summary>
    private async Task<bool> IsPeriodOpenAsync(Guid companyId, DateTime date)
    {
        var period = await _db.FiscalPeriods.AsNoTracking().FirstOrDefaultAsync(f =>
            f.CompanyId == companyId && f.StartDate <= date && f.EndDate >= date);
        return period == null || period.Status == FiscalPeriodStatus.Open;
    }

    /// <summary>In-place JE update (contract ของระบบต้นทางเช่น TakeTime):
    /// แก้ "JE ใบเดิม" — เลข JE คงเดิม, แทนที่บรรทัดทั้งชุดด้วยยอดใหม่,
    /// อัปเดต totals/วันที่. ใช้เฉพาะเมื่องวดยังเปิด. คืน JE id เมื่อสำเร็จ.</summary>
    private async Task<Guid?> UpdateJournalInPlaceAsync(
        Guid companyId, Guid integrationId, Document document, string type, JournalEntry original)
    {
        var built = await BuildIntegrationJournalLinesAsync(companyId, integrationId, document, type);
        if (built == null) return null;
        var (newLines, _, _) = built.Value;

        // ด่านตรวจโครงสร้างเดียวกับตอนสร้าง — resync ที่โครงสร้างผิดต้องไม่ทับ
        // JE เดิมที่ถูกอยู่แล้ว
        if (!await ValidateAndAutofixJournalAsync(companyId, newLines, document))
            return null;

        var oldLines = await _db.JournalEntryLines
            .Where(l => l.JournalEntryId == original.Id)
            .ToListAsync();
        _db.JournalEntryLines.RemoveRange(oldLines);
        foreach (var l in newLines)
        {
            l.JournalEntryId = original.Id;
            _db.JournalEntryLines.Add(l);
        }
        original.EntryDate = NormalizeDate(document.DocumentDate);
        // วันที่ใหม่อาจข้ามเดือน — งวดต้องตามไปด้วย (ทั้งสองงวดถูกยืนยันว่า
        // เปิดอยู่แล้วก่อนเข้าโหมด in-place)
        var newPeriod = await _db.FiscalPeriods.AsNoTracking().FirstOrDefaultAsync(f =>
            f.CompanyId == companyId
            && f.StartDate <= original.EntryDate && f.EndDate >= original.EntryDate
            && f.Status == FiscalPeriodStatus.Open);
        original.FiscalPeriodId = newPeriod?.Id;
        original.TotalDebit = newLines.Sum(l => l.DebitAmount);
        original.TotalCredit = newLines.Sum(l => l.CreditAmount);
        original.Description = $"Auto: {document.DocumentNumber} (แก้ไขจาก resync)";
        await _db.SaveChangesAsync();
        return original.Id;
    }

    private async Task<Guid?> CreatePaymentJournalAsync(Guid companyId, Guid integrationId, Payment payment, Document document, InboundPaymentRequest request)
    {
        // Find cash/bank account for the money side.
        var paymentMethod = request.PaymentMethod?.ToLower();
        var cashAccountCode = paymentMethod switch
        {
            "cash" => "111",         // เงินสด
            "banktransfer" or "promptpay" => "112", // เงินฝากธนาคาร
            "creditcard" => "112",
            _ => "111"
        };
        var cashAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith(cashAccountCode) && a.IsActive);
        if (cashAccount == null) return null;

        // DIRECTION FIX: branch by document type. The integration path used to
        // ALWAYS post Dr Cash / Cr AR(113) — correct for receiving customer
        // money, but WRONG for a Payment Voucher / expense settlement, which
        // must Dr AP (settle เจ้าหนี้) / Cr Cash. Mirror DocumentService's
        // revenue-vs-expense split so integration-created payments hit the
        // right side.
        var revenueTypes = new[] { DocumentType.Invoice, DocumentType.TaxInvoice,
            DocumentType.Receipt, DocumentType.DebitNote, DocumentType.BillingNote,
            DocumentType.ReceiptVoucher };
        var isRevenue = revenueTypes.Contains(document.DocumentType);

        // AR for revenue (113); AP for expense (211 → 212 fallback).
        ChartOfAccount? counterpart = null;
        if (isRevenue)
        {
            counterpart = await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("113") && a.IsActive);
        }
        else
        {
            // Clear the SAME payable account the document's journal credited:
            // contact-pinned override > Expense → 21220 > trade 212 family >
            // legacy 211. Keeps web + partner postings reconcilable on the
            // identical liability account.
            Contact? settleContact = null;
            if (document.ContactId != Guid.Empty)
                settleContact = await _db.Contacts.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == document.ContactId && c.CompanyId == companyId);
            if (settleContact?.DefaultApAccountId is Guid pinnedClearId)
                counterpart = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.Id == pinnedClearId && a.CompanyId == companyId && a.IsActive);
            if (counterpart == null && document.DocumentType == DocumentType.Expense)
                counterpart = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "21220" && a.IsActive);
            counterpart ??= await _db.ChartOfAccounts
                    .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith("212") && a.IsActive)
                    .OrderBy(a => a.AccountCode)
                    .FirstOrDefaultAsync()
                ?? await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("211") && a.IsActive);
        }
        if (counterpart == null) return null;

        // Find fiscal period
        var fiscalPeriod = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId
            && f.StartDate <= payment.PaymentDate
            && f.EndDate >= payment.PaymentDate
            && f.Status == FiscalPeriodStatus.Open);

        var prefix = isRevenue ? "RV" : "PV";
        var payJournalNumber = await GetNextJournalNumberAsync(companyId, prefix);
        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = payJournalNumber,
            EntryDate = NormalizeDate(payment.PaymentDate),
            JournalType = isRevenue ? JournalType.CashReceipts : JournalType.CashPayments,
            Description = (isRevenue ? "รับชำระ " : "จ่ายชำระ ") + document.DocumentNumber,
            Reference = payment.PaymentNumber,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            SourceDocumentId = document.Id,
            FiscalPeriodId = fiscalPeriod?.Id,
            TotalDebit = payment.Amount,
            TotalCredit = payment.Amount,
            Lines = isRevenue
                ? new List<JournalEntryLine>
                {
                    new() { AccountId = cashAccount.Id, DebitAmount = payment.Amount, Description = $"รับชำระ ({request.PaymentMethod})", LineOrder = 1 },
                    new() { AccountId = counterpart.Id, CreditAmount = payment.Amount, Description = $"ตัดลูกหนี้ {document.DocumentNumber}", LineOrder = 2 }
                }
                : new List<JournalEntryLine>
                {
                    new() { AccountId = counterpart.Id, DebitAmount = payment.Amount, Description = $"ตัดเจ้าหนี้ {document.DocumentNumber}", LineOrder = 1 },
                    new() { AccountId = cashAccount.Id, CreditAmount = payment.Amount, Description = $"จ่ายชำระ ({request.PaymentMethod})", LineOrder = 2 }
                }
        };

        _db.JournalEntries.Add(je);
        await _db.SaveChangesAsync();
        return je.Id;
    }

    /// <summary>
    /// ใบลดหนี้ (Credit Note) — กลับรายการของใบแจ้งหนี้เดิม
    /// Dr: รายได้ (4xxxx) = SubTotal
    /// Dr: ภาษีขาย (2151x) = VatAmount (ถ้ามี)
    /// Cr: ลูกหนี้การค้า (113xx) = TotalAmount
    ///
    /// หมายเหตุ (by design): endpoint นี้รองรับเฉพาะใบลดหนี้ "ฝั่งขาย" (คู่ค้าเป็น
    /// ลูกค้า) — InboundCreditNoteRequest มีแต่ field ลูกค้า ไม่มี supplier —
    /// ใบลดหนี้ฝั่งซื้อ (ผู้ขายลดหนี้ให้เรา = ลดเจ้าหนี้/ภาษีซื้อ) ต้อง sync ผ่าน
    /// expense/payment-voucher reversal ไม่ผ่านช่องทางนี้ จึงไม่มีการลงบัญชีผิดฝั่ง
    /// </summary>
    private async Task<Guid?> CreateCreditNoteJournalAsync(Guid companyId, Document document)
    {
        var arAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("113") && a.IsActive);
        var revenueAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountType == AccountType.Revenue && a.IsActive);
        var vatAccount = document.VatAmount > 0
            ? await ResolveVatAccountAsync(companyId, isInput: false)
            : null;

        if (arAccount == null || revenueAccount == null)
        {
            _logger.LogWarning("ไม่พบผังบัญชีลูกหนี้/รายได้ สำหรับ CreditNote company {CompanyId}", companyId);
            return null;
        }

        var journalLines = new List<JournalEntryLine>();
        int lineOrder = 1;

        // Dr: รายได้ (กลับรายการ)
        journalLines.Add(new JournalEntryLine
        {
            AccountId = revenueAccount.Id,
            DebitAmount = document.SubTotal,
            Description = $"ลดหนี้ - {document.DocumentNumber}",
            LineOrder = lineOrder++
        });

        // Dr: ภาษีขาย (กลับรายการ)
        if (vatAccount != null && document.VatAmount > 0)
        {
            journalLines.Add(new JournalEntryLine
            {
                AccountId = vatAccount.Id,
                DebitAmount = document.VatAmount,
                Description = $"ภาษีขาย (ลดหนี้) - {document.DocumentNumber}",
                LineOrder = lineOrder++
            });
        }

        // Cr: ลูกหนี้การค้า
        journalLines.Add(new JournalEntryLine
        {
            AccountId = arAccount.Id,
            CreditAmount = document.TotalAmount,
            Description = $"ลดลูกหนี้ - {document.DocumentNumber}",
            LineOrder = lineOrder++
        });

        var fiscalPeriod = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId && f.StartDate <= document.DocumentDate
            && f.EndDate >= document.DocumentDate && f.Status == FiscalPeriodStatus.Open);

        var entryNumber = await GetNextJournalNumberAsync(companyId, "SV");
        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = NormalizeDate(document.DocumentDate),
            JournalType = JournalType.Sales,
            Description = $"ใบลดหนี้: {document.DocumentNumber}",
            Reference = document.DocumentNumber,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            SourceDocumentId = document.Id,
            FiscalPeriodId = fiscalPeriod?.Id,
            TotalDebit = journalLines.Sum(l => l.DebitAmount),
            TotalCredit = journalLines.Sum(l => l.CreditAmount),
            Lines = journalLines
        };

        _db.JournalEntries.Add(je);
        await _db.SaveChangesAsync();
        return je.Id;
    }

    /// <summary>
    /// ใบเพิ่มหนี้ (Debit Note) — เพิ่มยอดหนี้ลูกค้า
    /// Dr: ลูกหนี้การค้า (113xx) = TotalAmount
    /// Cr: รายได้ (4xxxx) = SubTotal
    /// Cr: ภาษีขาย (2151x) = VatAmount (ถ้ามี)
    ///
    /// หมายเหตุ (by design): รองรับเฉพาะใบเพิ่มหนี้ "ฝั่งขาย" (คู่ค้าเป็นลูกค้า)
    /// เช่นเดียวกับใบลดหนี้ — ฝั่งซื้อ sync ผ่าน expense ไม่ผ่านช่องทางนี้
    /// </summary>
    private async Task<Guid?> CreateDebitNoteJournalAsync(Guid companyId, Document document)
    {
        var arAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("113") && a.IsActive);
        var revenueAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountType == AccountType.Revenue && a.IsActive);
        var vatAccount = document.VatAmount > 0
            ? await ResolveVatAccountAsync(companyId, isInput: false)
            : null;

        if (arAccount == null || revenueAccount == null)
        {
            _logger.LogWarning("ไม่พบผังบัญชีลูกหนี้/รายได้ สำหรับ DebitNote company {CompanyId}", companyId);
            return null;
        }

        var journalLines = new List<JournalEntryLine>();
        int lineOrder = 1;

        // Dr: ลูกหนี้การค้า
        journalLines.Add(new JournalEntryLine
        {
            AccountId = arAccount.Id,
            DebitAmount = document.TotalAmount,
            Description = $"เพิ่มหนี้ - {document.DocumentNumber}",
            LineOrder = lineOrder++
        });

        // Cr: รายได้
        journalLines.Add(new JournalEntryLine
        {
            AccountId = revenueAccount.Id,
            CreditAmount = document.SubTotal,
            Description = $"รายได้ (เพิ่มหนี้) - {document.DocumentNumber}",
            LineOrder = lineOrder++
        });

        // Cr: ภาษีขาย
        if (vatAccount != null && document.VatAmount > 0)
        {
            journalLines.Add(new JournalEntryLine
            {
                AccountId = vatAccount.Id,
                CreditAmount = document.VatAmount,
                Description = $"ภาษีขาย (เพิ่มหนี้) - {document.DocumentNumber}",
                LineOrder = lineOrder++
            });
        }

        var fiscalPeriod = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId && f.StartDate <= document.DocumentDate
            && f.EndDate >= document.DocumentDate && f.Status == FiscalPeriodStatus.Open);

        var entryNumber = await GetNextJournalNumberAsync(companyId, "SV");
        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = NormalizeDate(document.DocumentDate),
            JournalType = JournalType.Sales,
            Description = $"ใบเพิ่มหนี้: {document.DocumentNumber}",
            Reference = document.DocumentNumber,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            SourceDocumentId = document.Id,
            FiscalPeriodId = fiscalPeriod?.Id,
            TotalDebit = journalLines.Sum(l => l.DebitAmount),
            TotalCredit = journalLines.Sum(l => l.CreditAmount),
            Lines = journalLines
        };

        _db.JournalEntries.Add(je);
        await _db.SaveChangesAsync();
        return je.Id;
    }

    /// <summary>Resync update ใบแจ้งหนี้/ใบกำกับจากระบบภายนอก — เลขเอกสารคงเดิม
    /// แต่กลับ JE เดิม + สร้างบรรทัด/ยอดใหม่ + post JE ใหม่ (audit trail ครบ).</summary>
    private async Task<InboundSyncResponse> ResyncUpdateInvoiceAsync(
        Guid companyId, Guid integrationId, Document existing,
        InboundInvoiceRequest request, IntegrationSyncLog log, Stopwatch sw)
    {
        var guardError = await ResyncGuardAsync(companyId, existing, NormalizeDate(request.DocumentDate));
        if (guardError != null)
        {
            log.Status = "Failed";
            log.ErrorMessage = guardError;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);
            return new InboundSyncResponse(false, guardError, existing.Id, existing.ContactId, null, null, existing.DocumentNumber);
        }

        // อัตราภาษีขายของใบที่ resync — เส้นนี้ **ไม่ได้** ผ่านด่าน §90/2 ของ
        // ProcessInvoiceAsync (คู่ค้ายิงตรงเข้ามาแก้ใบเดิมได้) ⇒ ต้องตรวจเองที่นี่
        // มิฉะนั้นบริษัทที่เพิ่งยกเลิกจดทะเบียนจะยังถูก resync ใส่ VAT กลับเข้าไป
        var vatProfile = await GetCompanyVatProfileAsync(companyId);
        var vatDecision = Accounting.Helpers.PartnerVatRate.ForIssuedDocument(
            request.VatRate, vatProfile.Registered, vatProfile.DefaultRate);
        if (vatDecision.Rejected)
        {
            log.Status = "Failed";
            log.ErrorMessage = "Output VAT blocked — company not VAT-registered (§90/2)";
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);
            return new InboundSyncResponse(false, vatDecision.Error, existing.Id, existing.ContactId, null, null, existing.DocumentNumber);
        }

        // ลบบรรทัดเดิม → สร้างใหม่จากข้อมูล resync (helper เดียวกับตอน create)
        var oldLines = await _db.DocumentLines.Where(l => l.DocumentId == existing.Id).ToListAsync();
        _db.DocumentLines.RemoveRange(oldLines);
        var vatRate = vatDecision.Rate;
        var newLines = await BuildDocumentLinesAsync(companyId, request.Lines, vatRate,
            includeVat: request.IncludeVat, outputVatAllowed: vatProfile.Registered);
        foreach (var l in newLines) l.DocumentId = existing.Id;
        existing.Lines = newLines;   // ให้ JE builder เห็นบรรทัดใหม่ทันที (nav ไม่ได้ Include มา)

        var subTotal = newLines.Sum(l => l.Amount);
        var totalVat = newLines.Sum(l => l.VatAmount);
        existing.DocumentDate = NormalizeDate(request.DocumentDate);
        existing.DueDate = NormalizeDate(request.DueDate ?? request.DocumentDate.AddDays(30));
        existing.SubTotal = subTotal;
        existing.VatAmount = totalVat;
        // SubTotal เป็นฐานก่อน VAT เสมอแล้ว (SplitLineVat) ⇒ ยอดรวมต้องบวก VAT
        existing.TotalAmount = subTotal + totalVat;
        existing.BalanceDue = existing.TotalAmount;   // PaidAmount == 0 (guard ผ่านแล้ว)
        // Deposit fields (spec deposit/checkout) — resync = source of truth ทับค่าเดิม.
        // guard ด้านบนยืนยัน PaidAmount==0 (ขายเงินสดที่ปิดยอดถูกบล็อก resync แล้ว)
        // → stamp ยอดตรง ๆ ปลอดภัย (ไม่มี settle มาชนแล้ว)
        existing.DepositAppliedDrivesJournal = request.DepositAppliedDrivesJournal;
        existing.DepositOutputVatDeferred = request.DepositOutputVatDeferred;
        existing.DepositAppliedRef = string.IsNullOrWhiteSpace(request.DepositAppliedRef)
            ? null : request.DepositAppliedRef.Trim();
        existing.DepositAppliedAmount = request.DepositAppliedAmount > 0m ? request.DepositAppliedAmount : 0m;
        await _db.SaveChangesAsync();

        // ── เลือกวิธีปรับ JE (contract ระบบต้นทาง):
        //    งวดเปิด + มี JE เดิมใบเดียว → in-place (เลข JE คงเดิม)
        //    มิฉะนั้น → reversal + post ใหม่ (งวดปิดห้ามแก้ในงวด)
        var originals = await LoadResyncOriginalsAsync(companyId, existing.Id);
        Guid? journalEntryId = null;
        string? jeSkipReason = null;
        var inPlace = false;
        if (originals.Count == 1
            && await IsPeriodOpenAsync(companyId, originals[0].EntryDate)
            && await IsPeriodOpenAsync(companyId, existing.DocumentDate))
        {
            journalEntryId = await UpdateJournalInPlaceAsync(companyId, integrationId, existing, "invoice", originals[0]);
            inPlace = journalEntryId != null;
        }
        if (!inPlace)
        {
            await ResyncReverseOriginalsAsync(companyId, existing, originals);
            await _db.SaveChangesAsync();
            (journalEntryId, jeSkipReason) = await PostMappingJournalAsync(companyId, integrationId, existing, "invoice", log);
        }
        existing.Notes = (existing.Notes ?? "")
            + $"\n[Resync แก้ไขจากระบบภายนอก] {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — "
            + (inPlace ? "แก้ JE เดิม (in-place, เลขคงเดิม)" : "กลับ JE เดิม + post ใหม่ (reversal)");
        await _db.SaveChangesAsync();

        // หมายเหตุ: ขายเงินสดที่ปิดยอดแล้ว (PaidAmount=Total) ถูก ResyncGuardAsync
        // บล็อกตั้งแต่ต้น (แก้ไม่ได้หลังรับชำระ) จึงไม่ต้อง re-settle ที่นี่. เคส
        // degrade (ตั้งหนี้ PaidAmount=0) resync ได้ปกติเป็นใบตั้งหนี้ (mapping JE).

        if (log.Status != "PartialSuccess") log.Status = "Updated";
        log.CreatedDocumentId = existing.Id;
        log.CreatedJournalEntryId = journalEntryId;
        log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
        await SaveSyncLog(log, integrationId);
        _logger.LogInformation("Resync-updated invoice {DocNo} (company {Cid}) — {Mode}",
            existing.DocumentNumber, companyId, inPlace ? "in-place" : "reversal");
        return new InboundSyncResponse(true,
            (inPlace ? "Resync updated (in-place) — แก้ JE เดิม เลข JE คงเดิม"
                     : "Resync updated (reversal) — งวดเดิมปิด/มีหลาย JE จึงกลับรายการ + post ใหม่")
            + JeSkipSuffix(jeSkipReason),
            existing.Id, existing.ContactId, journalEntryId, null, existing.DocumentNumber);
    }

    /// <summary>Resync update ค่าใช้จ่ายจากระบบภายนอก — semantics เดียวกับ invoice.</summary>
    private async Task<InboundSyncResponse> ResyncUpdateExpenseAsync(
        Guid companyId, Guid integrationId, Document existing,
        InboundExpenseRequest request, IntegrationSyncLog log, Stopwatch sw)
    {
        var guardError = await ResyncGuardAsync(companyId, existing, NormalizeDate(request.DocumentDate));
        if (guardError != null)
        {
            log.Status = "Failed";
            log.ErrorMessage = guardError;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);
            return new InboundSyncResponse(false, guardError, existing.Id, existing.ContactId, null, null, existing.DocumentNumber);
        }

        var oldLines = await _db.DocumentLines.Where(l => l.DocumentId == existing.Id).ToListAsync();
        _db.DocumentLines.RemoveRange(oldLines);
        // ภาษี**ซื้อ**: VAT บนใบเป็นของผู้ขาย — สถานะจดทะเบียนของเราไม่ตัดอัตรานี้
        // (ตัดแล้วยอดที่ต้องจ่ายผู้ขายหายไปเงียบ ๆ) แต่ตัดสิน "เคลมได้ไหม":
        // ไม่จด VAT = เคลมไม่ได้ทุกบรรทัด รวมเป็นต้นทุน — กติกาเดียวกับเส้นคีย์มือ
        // ใน DocumentService · ตัวตัดสินเดียวอยู่ที่ Helpers/PartnerVatRate
        var vatProfile = await GetCompanyVatProfileAsync(companyId);
        var vatRate = Accounting.Helpers.PartnerVatRate.ForReceivedDocument(
            request.VatRate, vatProfile.DefaultRate);
        var newLines = await BuildDocumentLinesAsync(companyId, request.Lines, vatRate,
            expenseSide: true, includeVat: request.IncludeVat,
            inputVatClaimable: vatProfile.Registered);
        foreach (var l in newLines) l.DocumentId = existing.Id;
        existing.Lines = newLines;

        var subTotal = newLines.Sum(l => l.Amount);
        var totalVat = newLines.Sum(l => l.VatAmount);
        var totalWht = newLines.Sum(l => l.WithholdingTaxAmount);
        existing.DocumentDate = NormalizeDate(request.DocumentDate);
        existing.DueDate = NormalizeDate(request.DueDate ?? request.DocumentDate.AddDays(30));
        existing.SubTotal = subTotal;
        existing.VatAmount = totalVat;
        existing.WithholdingTaxAmount = totalWht;
        // SubTotal เป็นฐานก่อน VAT เสมอแล้ว (SplitLineVat) ⇒ ยอดรวมต้องบวก VAT
        existing.TotalAmount = subTotal + totalVat - totalWht;
        existing.BalanceDue = existing.TotalAmount;
        await _db.SaveChangesAsync();

        var originals = await LoadResyncOriginalsAsync(companyId, existing.Id);
        Guid? journalEntryId = null;
        string? jeSkipReason = null;
        var inPlace = false;
        if (originals.Count == 1
            && await IsPeriodOpenAsync(companyId, originals[0].EntryDate)
            && await IsPeriodOpenAsync(companyId, existing.DocumentDate))
        {
            journalEntryId = await UpdateJournalInPlaceAsync(companyId, integrationId, existing, "expense", originals[0]);
            inPlace = journalEntryId != null;
        }
        if (!inPlace)
        {
            await ResyncReverseOriginalsAsync(companyId, existing, originals);
            await _db.SaveChangesAsync();
            (journalEntryId, jeSkipReason) = await PostMappingJournalAsync(companyId, integrationId, existing, "expense", log);
        }
        existing.Notes = (existing.Notes ?? "")
            + $"\n[Resync แก้ไขจากระบบภายนอก] {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — "
            + (inPlace ? "แก้ JE เดิม (in-place, เลขคงเดิม)" : "กลับ JE เดิม + post ใหม่ (reversal)");
        await _db.SaveChangesAsync();

        if (log.Status != "PartialSuccess") log.Status = "Updated";
        log.CreatedDocumentId = existing.Id;
        log.CreatedJournalEntryId = journalEntryId;
        log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
        await SaveSyncLog(log, integrationId);
        _logger.LogInformation("Resync-updated expense {DocNo} (company {Cid}) — {Mode}",
            existing.DocumentNumber, companyId, inPlace ? "in-place" : "reversal");
        return new InboundSyncResponse(true,
            (inPlace ? "Resync updated (in-place) — แก้ JE เดิม เลข JE คงเดิม"
                     : "Resync updated (reversal) — งวดเดิมปิด/มีหลาย JE จึงกลับรายการ + post ใหม่")
            + JeSkipSuffix(jeSkipReason),
            existing.Id, existing.ContactId, journalEntryId, null, existing.DocumentNumber);
    }

    /// <summary>Resync guard — ตรวจว่าเอกสาร sync เดิมแก้ได้ไหม (ยังไม่แตะ JE).
    /// ตรวจทั้ง "เดือนภาษีเดิม" (หนังสือเดิมจะถูกแก้/กลับ) และ "เดือนของวันที่
    /// ใหม่" (JE ใหม่จะลงที่นั่น) — เดือนใดยื่น ภ.พ.30/ล็อกแล้ว = ปฏิเสธ.
    /// คืน error message เมื่อไม่ผ่าน (null = ผ่าน).</summary>
    private async Task<string?> ResyncGuardAsync(Guid companyId, Document doc, DateTime newDocDate)
    {
        // 1) มีการชำระแล้ว → ยอดใหม่จะชนกับ settlement ที่เกิดไปแล้ว
        if (doc.PaidAmount > 0.005m)
            return $"เอกสาร {doc.DocumentNumber} มีการรับ/จ่ายชำระแล้ว ({doc.PaidAmount:N2}) — " +
                   "resync แก้ไม่ได้ ให้ void แล้วส่งใหม่ หรือออกใบลดหนี้/เพิ่มหนี้ปรับยอดแทน";

        // 2) มี CN/DN อ้างถึง → การแก้ต้นทางทำ cap/ยอดอ้างอิงเพี้ยน
        var hasChildren = await _db.Documents.AsNoTracking().AnyAsync(d =>
            d.CompanyId == companyId && d.RelatedDocumentId == doc.Id && !d.IsDeleted
            && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected);
        if (hasChildren)
            return $"เอกสาร {doc.DocumentNumber} มีใบลดหนี้/ใบเพิ่มหนี้/เอกสารลูกอ้างถึง — resync แก้ไม่ได้";

        // 3) เดือนภาษีของเอกสารยื่น ภ.พ.30 ไปแล้ว/ล็อก → ห้ามแก้ย้อน (RD compliance)
        var taxDate = (doc.TaxPointDate ?? doc.DocumentDate);
        var vatFiled = await _db.TaxReports.AsNoTracking().AnyAsync(r =>
            r.CompanyId == companyId && !r.IsDeleted && r.TaxType == TaxType.VAT
            && r.Year == taxDate.Year && r.Month == taxDate.Month
            && (r.Status != TaxReportStatus.Draft || r.FilingLockedAt != null));
        if (vatFiled)
            return $"เดือนภาษี {taxDate:MM/yyyy} ของเอกสาร {doc.DocumentNumber} ยื่น ภ.พ.30 แล้ว — " +
                   "resync แก้ไม่ได้ ให้ปรับปรุงผ่านใบลดหนี้/เพิ่มหนี้ของเดือนปัจจุบัน";

        // 3b) เดือนของ "วันที่ใหม่" ก็ต้องยังไม่ยื่น — JE ที่แก้/post ใหม่จะลงเดือนนั้น
        if (newDocDate.Year != taxDate.Year || newDocDate.Month != taxDate.Month)
        {
            var newMonthFiled = await _db.TaxReports.AsNoTracking().AnyAsync(r =>
                r.CompanyId == companyId && !r.IsDeleted && r.TaxType == TaxType.VAT
                && r.Year == newDocDate.Year && r.Month == newDocDate.Month
                && (r.Status != TaxReportStatus.Draft || r.FilingLockedAt != null));
            if (newMonthFiled)
                return $"วันที่ใหม่ {newDocDate:dd/MM/yyyy} ตกในเดือนภาษีที่ยื่น ภ.พ.30 แล้ว — " +
                       "resync แก้ไม่ได้ ให้ใช้วันที่ในเดือนที่ยังไม่ยื่น หรือออกใบลดหนี้/เพิ่มหนี้แทน";
        }

        return null;
    }

    /// <summary>โหลด JE forward เดิมของเอกสาร (ยังไม่ถูกกลับ).</summary>
    private Task<List<JournalEntry>> LoadResyncOriginalsAsync(Guid companyId, Guid docId) =>
        _db.JournalEntries
            .Include(j => j.Lines)
            .Where(j => j.CompanyId == companyId && j.SourceDocumentId == docId
                && j.Status == JournalEntryStatus.Posted
                && j.OriginalEntryId == null && j.ReversedByEntryId == null)
            .ToListAsync();

    /// <summary>กลับ JE forward เดิมทั้งชุด (reversal คู่ Dr↔Cr) — ใช้เมื่องวด
    /// เดิมปิดแล้ว หรือ in-place ทำไม่ได้.</summary>
    private async Task ResyncReverseOriginalsAsync(Guid companyId, Document doc, List<JournalEntry> originals)
    {
        foreach (var original in originals)
        {
            var reversal = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = await GetNextJournalNumberAsync(companyId, "RV"),
                EntryDate = DateTime.UtcNow.Date,
                JournalType = original.JournalType,
                Description = $"กลับรายการ (resync แก้ไขจากระบบภายนอก) - {doc.DocumentNumber}",
                Reference = original.Reference,
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                SourceDocumentId = doc.Id,
                OriginalEntryId = original.Id,
                TotalDebit = original.TotalCredit,
                TotalCredit = original.TotalDebit,
                CreatedBy = "integration-resync",
            };
            _db.JournalEntries.Add(reversal);
            var order = 1;
            foreach (var line in original.Lines.OrderBy(l => l.LineOrder))
            {
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    JournalEntryId = reversal.Id,
                    AccountId = line.AccountId,
                    DebitAmount = line.CreditAmount,
                    CreditAmount = line.DebitAmount,
                    Description = $"กลับรายการ - {line.Description}",
                    LineOrder = order++,
                    ProjectId = line.ProjectId,
                    DimensionId = line.DimensionId,
                });
            }
            original.ReversedByEntryId = reversal.Id;
        }
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

    /// <summary>
    /// Idempotency fallback เมื่อ partner ไม่ส่ง ExternalRef มา — ค้น sync log
    /// เดิมที่ event เดียวกัน (integrationId + eventType + externalId) และสร้าง
    /// เอกสารสำเร็จไปแล้ว คืนเอกสารนั้นถ้ายังไม่ถูก void เพื่อกัน retry สร้างซ้ำ
    /// (ExternalId มี index อยู่แล้ว: IX_IntegrationSyncLogs_ExternalId)
    /// </summary>
    private async Task<Document?> TryFindDocumentByExternalIdAsync(
        Guid companyId, Guid integrationId, string eventType, string? externalId)
    {
        if (string.IsNullOrEmpty(externalId)) return null;

        var priorDocId = await _db.Set<IntegrationSyncLog>()
            .Where(l => l.CompanyId == companyId
                && l.IntegrationId == integrationId
                && l.EventType == eventType
                && l.ExternalId == externalId
                && l.CreatedDocumentId != null
                && (l.Status == "Success" || l.Status == "Skipped"))
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => l.CreatedDocumentId)
            .FirstOrDefaultAsync();
        if (priorDocId == null) return null;

        return await _db.Documents.FirstOrDefaultAsync(d => d.Id == priorDocId
            && d.CompanyId == companyId
            && !d.IsDeleted
            && d.Status != DocumentStatus.Voided);
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

        // Load filtered invoices then group in-memory (complex string logic won't translate to SQL)
        var rawDocs = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.Status != DocumentStatus.Voided
                && d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .Select(d => new { d.Reference, d.TotalAmount })
            .ToListAsync();

        var data = rawDocs
            .GroupBy(d => d.Reference != null && d.Reference.Length > 0
                ? (d.Reference.Contains("-") ? d.Reference[..d.Reference.IndexOf("-")] : "Direct")
                : "Direct")
            .Select(g => new { Source = g.Key, Count = g.Count(), Total = g.Sum(d => d.TotalAmount) })
            .OrderByDescending(x => x.Total)
            .ToList();

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
        // ห้าม project d.Contact.Name ตรง ๆ — query filter !IsDeleted ทำให้ชื่อหาย
        // เมื่อ contact ถูกลบ. select ContactId แล้ว resolve จาก dict (IgnoreQueryFilters).
        var detailRows = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= fromDate && d.DocumentDate <= toDate
                && d.Notes != null && d.Notes.Contains("มัดจำ"))
            .OrderByDescending(d => d.DocumentDate)
            .Take(50)
            .Select(d => new { d.Id, d.DocumentNumber, d.ContactId,
                d.TotalAmount, d.DocumentDate, d.Status })
            .ToListAsync();
        var detailCids = detailRows.Select(r => r.ContactId).Distinct().ToList();
        var detailCmap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && detailCids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);
        var details = detailRows.Select(r => new DepositDetailItem(
            r.Id, r.DocumentNumber, detailCmap.GetValueOrDefault(r.ContactId),
            r.TotalAmount, r.DocumentDate, r.Status.ToString())).ToList();

        return new DepositSummaryResponse(depositReceived, depositApplied, depositReceived - depositApplied, details);
    }

    public async Task<List<DailyRevenueItem>> GetDailyRevenueAsync(Guid companyId, DateTime? from, DateTime? to)
    {
        var fromDate = from ?? DateTime.UtcNow.Date.AddDays(-30);
        var toDate = to ?? DateTime.UtcNow.Date;

        // Get daily invoice totals — use Year/Month/Day to avoid .Date translation issues
        var invoiceData = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                && d.Status != DocumentStatus.Voided
                && d.DocumentDate >= fromDate && d.DocumentDate <= toDate)
            .GroupBy(d => new { d.DocumentDate.Year, d.DocumentDate.Month, d.DocumentDate.Day })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, Count = g.Count(), Total = g.Sum(d => d.TotalAmount) })
            .ToListAsync();

        // Get daily revenue by account code prefix — load then group in-memory (Substring in GroupBy key)
        var rawJournalData = await _db.JournalEntryLines
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= fromDate
                && l.JournalEntry.EntryDate <= toDate
                && l.Account.AccountType == AccountType.Revenue)
            .Select(l => new { EntryDate = l.JournalEntry.EntryDate, l.Account.AccountCode, l.Account.AccountName, l.CreditAmount, l.DebitAmount })
            .ToListAsync();

        var journalData = rawJournalData
            .GroupBy(l => new { Date = l.EntryDate.Date, CodePrefix = l.AccountCode.Length >= 3 ? l.AccountCode[..3] : l.AccountCode, l.AccountName })
            .Select(g => new { g.Key.Date, g.Key.CodePrefix, g.Key.AccountName, Amount = g.Sum(l => l.CreditAmount - l.DebitAmount) })
            .ToList();

        var result = new List<DailyRevenueItem>();
        for (var d = fromDate; d <= toDate; d = d.AddDays(1))
        {
            var inv = invoiceData.FirstOrDefault(x => x.Year == d.Year && x.Month == d.Month && x.Day == d.Day);
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
            // Idempotency: กัน retry สร้างเอกสารค่าใช้จ่ายซ้ำ — เช็ค ExternalRef
            // (→ Document.Reference) ก่อน ถ้าไม่มีก็ fallback ด้วย ExternalId จาก sync log
            if (!string.IsNullOrEmpty(request.ExternalRef))
            {
                var existing = await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId
                    && d.Reference == request.ExternalRef && !d.IsDeleted
                    && d.DocumentType == DocumentType.Expense);
                if (existing != null && existing.Status != DocumentStatus.Voided)
                {
                    if (request.ResyncUpdate)
                        return await ResyncUpdateExpenseAsync(companyId, integrationId, existing, request, log, sw);

                    log.Status = "Skipped";
                    log.CreatedDocumentId = existing.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", existing.Id, existing.ContactId, null, null, existing.DocumentNumber);
                }
            }
            else
            {
                var priorDoc = await TryFindDocumentByExternalIdAsync(companyId, integrationId, "expense.created", request.ExternalId);
                if (priorDoc != null)
                {
                    log.Status = "Skipped";
                    log.CreatedDocumentId = priorDoc.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip by ExternalId)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", priorDoc.Id, priorDoc.ContactId, null, null, priorDoc.DocumentNumber);
                }
            }

            // Resolve supplier contact (dedupe: contactId → externalId → taxId(digits) → name)
            var supplier = await ResolveSupplierAsync(companyId, integrationId,
                request.SupplierContactId, request.SupplierExternalId, request.SupplierName, request.SupplierTaxId);

            var docNumber = await _settingsService.GetNextNumberAsync(companyId, DocumentType.Expense, request.DocumentDate);
            // ภาษี**ซื้อ**: VAT บนใบเป็นของผู้ขาย — สถานะจดทะเบียนของเราไม่ตัดอัตรานี้
            // (ตัดแล้วยอดที่ต้องจ่ายผู้ขายหายไปเงียบ ๆ) แต่ตัดสิน "เคลมได้ไหม":
            // ไม่จด VAT = เคลมไม่ได้ทุกบรรทัด รวมเป็นต้นทุน — กติกาเดียวกับเส้นคีย์มือ
            // ใน DocumentService · ตัวตัดสินเดียวอยู่ที่ Helpers/PartnerVatRate
            var vatProfile = await GetCompanyVatProfileAsync(companyId);
            var vatRate = Accounting.Helpers.PartnerVatRate.ForReceivedDocument(
                request.VatRate, vatProfile.DefaultRate);
            var lines = await BuildDocumentLinesAsync(companyId, request.Lines, vatRate,
                expenseSide: true, includeVat: request.IncludeVat,
                inputVatClaimable: vatProfile.Registered);
            var subTotal = lines.Sum(l => l.Amount);
            var totalVat = lines.Sum(l => l.VatAmount);
            var totalWht = lines.Sum(l => l.WithholdingTaxAmount);
            // Net payable = gross − withholding tax (consistent with manual entry).
            // SubTotal เป็นฐานก่อน VAT เสมอแล้ว (SplitLineVat) ⇒ ยอดรวมต้องบวก VAT
            var totalAmount = subTotal + totalVat - totalWht;

            // AutoApprove=true (default, backward-compat) → Approved + JE + 50 ทวิ
            // ทันทีเหมือนเดิม. false → สร้าง Draft: ยังไม่ลง GL/ยังไม่ออก 50 ทวิ
            // จนกว่าจะอนุมัติผ่าน ApproveDocumentAsync (ซึ่ง post JE + ออก 50 ทวิ เอง)
            var autoApprove = request.AutoApprove;
            var document = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = DocumentType.Expense,
                Status = autoApprove ? DocumentStatus.Approved : DocumentStatus.Draft,
                DocumentDate = NormalizeDate(request.DocumentDate),
                DueDate = NormalizeDate(request.DueDate ?? request.DocumentDate.AddDays(30)),
                ContactId = supplier.Id,
                Reference = request.ExternalRef,
                SubTotal = subTotal,
                VatAmount = totalVat,
                WithholdingTaxAmount = totalWht,
                TotalAmount = totalAmount,
                BalanceDue = totalAmount,
                // Role separation: an Expense from the partner sync is the
                // request/accrual side (ตั้งหนี้) — its GL credits AP; cash
                // moves later via /payments or a PV. Mark Credit so the doc
                // ages correctly instead of sitting type-less.
                PaymentType = Models.Enums.PaymentType.Credit,
                Notes = request.Notes,
                // Preparer identity from the source system. Stamped into the
                // "ผู้จัดทำ" signature slot since the real preparer is a user of
                // the partner system, not a NextAcc User. Whitespace-only values
                // are treated as absent (fall back to Owner downstream).
                PreparerName = string.IsNullOrWhiteSpace(request.PreparerName) ? null : request.PreparerName.Trim(),
                PreparerSignatureBase64 = TrimPreparerSignature(request.PreparerSignatureBase64),
                Lines = lines
            };

            _db.Documents.Add(document);
            await _db.SaveChangesAsync();
            await _vendorIntel.TryTrainAsync(companyId, document.Id);

            // Draft (autoApprove=false) → ยังไม่ลง GL และยังไม่ออก 50 ทวิ. ทั้งสองจะ
            // เกิดตอนอนุมัติ (ApproveDocumentAsync post JE + ออก 50 ทวิ ตอนจ่าย/approve).
            Guid? journalEntryId = null;
            string? jeSkipReason = null;
            string whtNote = "";
            if (autoApprove)
            {
                (journalEntryId, jeSkipReason) = await PostMappingJournalAsync(companyId, integrationId, document, "expense", log);
                // Auto-issue the withholding-tax certificate so an int_ key sync is
                // self-sufficient (no separate manual WHT step). Best-effort — a
                // failure here must not fail the already-committed expense sync.
                whtNote = await TryAutoGenerateWhtAsync(companyId, document, paid: false);
            }

            if (log.Status != "PartialSuccess") log.Status = "Success";
            log.CreatedDocumentId = document.Id;
            log.CreatedContactId = supplier.Id;
            log.CreatedJournalEntryId = journalEntryId;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true,
                (autoApprove ? "Expense created" : "Expense created as Draft (pending approval — GL + 50 ทวิ on approve)")
                + whtNote + JeSkipSuffix(jeSkipReason),
                document.Id, supplier.Id, journalEntryId, null, docNumber);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    public async Task<InboundSyncResponse> ProcessPaymentVoucherAsync(Guid companyId, Guid integrationId, InboundPaymentVoucherRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "payment_voucher.created", request.ExternalId, request.ExternalRef);

        try
        {
            // Idempotency: กัน retry สร้างใบสำคัญจ่ายซ้ำ
            if (!string.IsNullOrEmpty(request.ExternalRef))
            {
                var existing = await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId
                    && d.Reference == request.ExternalRef && !d.IsDeleted
                    && d.DocumentType == DocumentType.PaymentVoucher);
                if (existing != null && existing.Status != DocumentStatus.Voided)
                {
                    log.Status = "Skipped";
                    log.CreatedDocumentId = existing.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", existing.Id, existing.ContactId, null, null, existing.DocumentNumber);
                }
            }
            else
            {
                var priorDoc = await TryFindDocumentByExternalIdAsync(companyId, integrationId, "payment_voucher.created", request.ExternalId);
                if (priorDoc != null)
                {
                    log.Status = "Skipped";
                    log.CreatedDocumentId = priorDoc.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip by ExternalId)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", priorDoc.Id, priorDoc.ContactId, null, null, priorDoc.DocumentNumber);
                }
            }

            // Resolve supplier contact (dedupe: contactId → externalId → taxId(digits) → name)
            var supplier = await ResolveSupplierAsync(companyId, integrationId,
                request.SupplierContactId, request.SupplierExternalId, request.SupplierName, request.SupplierTaxId);

            var docNumber = await _settingsService.GetNextNumberAsync(companyId, DocumentType.PaymentVoucher, request.DocumentDate);
            // ภาษี**ซื้อ**: VAT บนใบเป็นของผู้ขาย — สถานะจดทะเบียนของเราไม่ตัดอัตรานี้
            // (ตัดแล้วยอดที่ต้องจ่ายผู้ขายหายไปเงียบ ๆ) แต่ตัดสิน "เคลมได้ไหม":
            // ไม่จด VAT = เคลมไม่ได้ทุกบรรทัด รวมเป็นต้นทุน — กติกาเดียวกับเส้นคีย์มือ
            // ใน DocumentService · ตัวตัดสินเดียวอยู่ที่ Helpers/PartnerVatRate
            var vatProfile = await GetCompanyVatProfileAsync(companyId);
            var vatRate = Accounting.Helpers.PartnerVatRate.ForReceivedDocument(
                request.VatRate, vatProfile.DefaultRate);
            var lines = await BuildDocumentLinesAsync(companyId, request.Lines, vatRate,
                expenseSide: true, includeVat: request.IncludeVat,
                inputVatClaimable: vatProfile.Registered);
            var subTotal = lines.Sum(l => l.Amount);
            var totalVat = lines.Sum(l => l.VatAmount);
            var totalWht = lines.Sum(l => l.WithholdingTaxAmount);
            // Net cash out = gross − withholding (the supplier receives net).
            // SubTotal เป็นฐานก่อน VAT เสมอแล้ว (SplitLineVat) ⇒ ยอดรวมต้องบวก VAT
            var totalAmount = subTotal + totalVat - totalWht;
            var paymentDate = NormalizeDate(request.PaymentDate ?? request.DocumentDate);

            // Role separation (หลักบัญชีไทย): a Payment Voucher IS the
            // disbursement — created already-paid (Cash, no balance, no due
            // date). The two-step "expense + payment" mapping is no longer
            // needed for vouchers the partner has already paid.
            // AutoApprove=true (default) → Approved + JE + 50 ทวิ ทันที (เดิม).
            // false → Draft: ยังไม่ลง GL/ยังไม่ออก 50 ทวิ จนกว่าจะอนุมัติภายหลัง
            // (คงยอด PaidAmount/BalanceDue เป็น "จ่ายแล้ว" — เป็น draft ของใบจ่ายจริง;
            // ApproveDocumentAsync จะ post JE + ออก 50 ทวิ ตอนอนุมัติ)
            var autoApprove = request.AutoApprove;
            var document = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = DocumentType.PaymentVoucher,
                Status = autoApprove ? DocumentStatus.Approved : DocumentStatus.Draft,
                DocumentDate = NormalizeDate(request.DocumentDate),
                PaymentDate = paymentDate,
                ContactId = supplier.Id,
                Reference = request.ExternalRef,
                SubTotal = subTotal,
                VatAmount = totalVat,
                WithholdingTaxAmount = totalWht,
                TotalAmount = totalAmount,
                PaymentType = Models.Enums.PaymentType.Cash,
                PaidAmount = totalAmount,
                BalanceDue = 0,
                Notes = request.Notes,
                PreparerName = string.IsNullOrWhiteSpace(request.PreparerName) ? null : request.PreparerName.Trim(),
                PreparerSignatureBase64 = TrimPreparerSignature(request.PreparerSignatureBase64),
                Lines = lines
            };

            _db.Documents.Add(document);
            await _db.SaveChangesAsync();
            await _vendorIntel.TryTrainAsync(companyId, document.Id);

            // Draft (autoApprove=false) → ยังไม่ post JE และยังไม่ออก 50 ทวิ; เกิดตอนอนุมัติ
            Guid? journalEntryId = null;
            string whtNote = "";
            if (autoApprove)
            {
                journalEntryId = await CreatePaymentVoucherJournalAsync(companyId, document);
                // Auto-issue the WHT certificate — a paid voucher with withholding
                // is exactly when the 50 ทวิ must be handed to the supplier.
                whtNote = await TryAutoGenerateWhtAsync(companyId, document, paid: true);
            }

            log.Status = "Success";
            log.CreatedDocumentId = document.Id;
            log.CreatedContactId = supplier.Id;
            log.CreatedJournalEntryId = journalEntryId;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true,
                (autoApprove ? "Payment voucher created" : "Payment voucher created as Draft (pending approval — GL + 50 ทวิ on approve)") + whtNote,
                document.Id, supplier.Id, journalEntryId, null, docNumber);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    /// <summary>GL for a partner-synced Payment Voucher (already paid):
    ///   Dr ค่าใช้จ่ายตามบรรทัด (resolved AccountId, generic-expense fallback)
    ///   Dr ภาษีซื้อ (1140x)
    ///   Cr เงินสด (111 family — cash; partners pay from their own till)
    ///   Cr ภาษีหัก ณ ที่จ่ายค้างจ่าย (21917 นิติบุคคล / 21916 บุคคลธรรมดา)
    /// Balanced by construction: Dr(sub+vat) = Cr(net cash) + Cr(wht).</summary>
    private async Task<Guid?> CreatePaymentVoucherJournalAsync(Guid companyId, Document document)
    {
        var expenseAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountType == AccountType.Expense && a.IsActive);
        var cashAccount = await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.AccountCode.Length >= 5 && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync()
            ?? await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.IsActive);
        if (expenseAccount == null || cashAccount == null)
        {
            _logger.LogWarning("ไม่พบผังบัญชีค่าใช้จ่าย/เงินสด สำหรับ company {CompanyId} — ไม่สร้าง journal", companyId);
            return null;
        }

        var journalLines = new List<JournalEntryLine>();
        int lineOrder = 1;

        foreach (var dl in document.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder))
            journalLines.Add(new JournalEntryLine
            {
                AccountId = dl.AccountId ?? expenseAccount.Id,
                DebitAmount = dl.Amount,
                Description = string.IsNullOrWhiteSpace(dl.Description)
                    ? $"ค่าใช้จ่าย - {document.DocumentNumber}" : dl.Description,
                LineOrder = lineOrder++
            });

        if (document.VatAmount > 0)
        {
            // Type-validated input-VAT account (11610/116) — never the old
            // "1140" prefix that hit 11400 เงินให้กู้ยืม.
            var vatAccount = await ResolveVatAccountAsync(companyId, isInput: true);
            if (vatAccount == null)
            {
                _logger.LogWarning("ไม่พบบัญชีภาษีซื้อ (116/11610) ที่ถูกต้องสำหรับ company {CompanyId} — " +
                    "ไม่โพสต์ JE เพื่อกันลงบัญชีผิด (เอกสาร {DocNo})", companyId, document.DocumentNumber);
                return null;   // refuse to post rather than guess wrong account
            }
            journalLines.Add(new JournalEntryLine
            {
                AccountId = vatAccount.Id,
                DebitAmount = document.VatAmount,
                Description = $"ภาษีซื้อ - {document.DocumentNumber}",
                LineOrder = lineOrder++
            });
        }

        journalLines.Add(new JournalEntryLine
        {
            AccountId = cashAccount.Id,
            CreditAmount = document.TotalAmount,
            Description = $"จ่ายเงิน - {document.DocumentNumber}",
            LineOrder = lineOrder++
        });

        if (document.WithholdingTaxAmount > 0)
        {
            // ⚠️ เดิมที่นี่เดาเองจาก `TaxId.StartsWith("0")` ล้วน ๆ — เป็นกติกาชุดที่ 4
            // ของเรื่องเดียวกัน และเป็นชุดที่อ่อนที่สุด (ไม่ดู ContactType · ไม่ดูคำใน
            // ชื่อ · ไม่รู้จัก 21918) ⇒ ยุบมาที่ `Helpers/WhtPayableAccount` ตัวเดียว
            var supplier = await _db.Set<Contact>().AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == document.ContactId);
            var payChain = Accounting.Helpers.WhtPayableAccount.CodeChain(
                document.IsForeignService, supplier?.CountryCode, supplier?.TaxId,
                supplier?.ContactType ?? Models.Enums.ContactType.Individual, supplier?.Name);
            var whtCode = payChain[0];
            var whtAccount = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == whtCode && a.IsActive)
                ?? await _db.ChartOfAccounts
                    .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith("2191")
                        && a.AccountCode != "21911" && a.IsActive)
                    .OrderBy(a => a.AccountCode)
                    .FirstOrDefaultAsync();
            if (whtAccount != null)
                journalLines.Add(new JournalEntryLine
                {
                    AccountId = whtAccount.Id,
                    CreditAmount = document.WithholdingTaxAmount,
                    Description = $"ภาษีหัก ณ ที่จ่ายค้างจ่าย - {document.DocumentNumber}",
                    LineOrder = lineOrder++
                });
            else
                // No WHT-payable account — fold into cash so the entry still
                // balances (logged for the admin to fix the chart).
                _logger.LogWarning("ไม่พบบัญชีภาษีหัก ณ ที่จ่ายค้างจ่าย (2191x) สำหรับ company {CompanyId}", companyId);
        }

        // ── Posting sanity guard: กันลงบัญชีผิดแน่ ๆ ก่อน post เข้าแยกบัญชี ──
        // ตรวจทุกบรรทัด: VAT line ต้องอยู่บัญชีภาษี, บัญชีต้องมีจริง+active.
        // คืน false = พบ error ที่แก้ไม่ได้ → ไม่โพสต์ (เอกสารยังซิงค์ แต่รอ
        // ตรวจ). แก้อัตโนมัติได้ (เช่น VAT line บัญชีผิด) จะ reroute ให้.
        if (!await ValidateAndAutofixJournalAsync(companyId, journalLines, document))
            return null;

        var totalDebit = journalLines.Sum(l => l.DebitAmount);
        var totalCredit = journalLines.Sum(l => l.CreditAmount);
        if (totalDebit != totalCredit)
        {
            _logger.LogWarning("PV journal ไม่ balance (Dr {Dr} ≠ Cr {Cr}) สำหรับเอกสาร {Doc} — ไม่สร้าง journal",
                totalDebit, totalCredit, document.DocumentNumber);
            return null;
        }

        var journalNumber = await GetNextJournalNumberAsync(companyId, "PV");
        var fiscalPeriod = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId
            && f.StartDate <= document.DocumentDate
            && f.EndDate >= document.DocumentDate
            && f.Status == FiscalPeriodStatus.Open);

        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = journalNumber,
            EntryDate = document.DocumentDate,
            JournalType = JournalType.CashPayments,
            Description = $"ใบสำคัญจ่าย {document.DocumentNumber} (integration sync)",
            Reference = document.Reference,
            Status = JournalEntryStatus.Posted,
            TotalDebit = totalDebit,
            TotalCredit = totalCredit,
            SourceDocumentId = document.Id,
            FiscalPeriodId = fiscalPeriod?.Id,
            IsAutoGenerated = true,
            CreatedBy = "integration-sync"
        };
        foreach (var l in journalLines)
            je.Lines.Add(l);   // EF wires JournalEntryId via the nav on save
        _db.JournalEntries.Add(je);
        await _db.SaveChangesAsync();
        return je.Id;
    }

    public async Task<InboundSyncResponse> ProcessCertificateInLieuAsync(Guid companyId, Guid integrationId, InboundCertificateInLieuRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "certificate_in_lieu.created", request.ExternalId, request.ExternalRef);

        try
        {
            // Idempotency: กัน retry สร้างใบแทนหนังสือรับรองหักภาษีซ้ำ
            if (!string.IsNullOrEmpty(request.ExternalRef))
            {
                var existing = await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId
                    && d.Reference == request.ExternalRef && !d.IsDeleted
                    && d.DocumentType == DocumentType.CertificateInLieu);
                if (existing != null && existing.Status != DocumentStatus.Voided)
                {
                    log.Status = "Skipped";
                    log.CreatedDocumentId = existing.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", existing.Id, existing.ContactId, null, null, existing.DocumentNumber);
                }
            }
            else
            {
                var priorDoc = await TryFindDocumentByExternalIdAsync(companyId, integrationId, "certificate_in_lieu.created", request.ExternalId);
                if (priorDoc != null)
                {
                    log.Status = "Skipped";
                    log.CreatedDocumentId = priorDoc.Id;
                    log.ErrorMessage = "Document already exists (idempotent skip by ExternalId)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", priorDoc.Id, priorDoc.ContactId, null, null, priorDoc.DocumentNumber);
                }
            }

            // Resolve supplier contact (dedupe: externalId → taxId(digits) → name)
            var supplier = await ResolveSupplierAsync(companyId, integrationId,
                null, request.SupplierExternalId, request.SupplierName, request.SupplierTaxId);

            var docNumber = await _settingsService.GetNextNumberAsync(companyId, DocumentType.CertificateInLieu, request.DocumentDate);
            // ภาษี**ซื้อ**: VAT บนใบเป็นของผู้ขาย — สถานะจดทะเบียนของเราไม่ตัดอัตรานี้
            // (ตัดแล้วยอดที่ต้องจ่ายผู้ขายหายไปเงียบ ๆ) แต่ตัดสิน "เคลมได้ไหม":
            // ไม่จด VAT = เคลมไม่ได้ทุกบรรทัด รวมเป็นต้นทุน — กติกาเดียวกับเส้นคีย์มือ
            // ใน DocumentService · ตัวตัดสินเดียวอยู่ที่ Helpers/PartnerVatRate
            var vatProfile = await GetCompanyVatProfileAsync(companyId);
            var vatRate = Accounting.Helpers.PartnerVatRate.ForReceivedDocument(
                request.VatRate, vatProfile.DefaultRate);
            var lines = await BuildDocumentLinesAsync(companyId, request.Lines, vatRate,
                expenseSide: true, includeVat: request.IncludeVat,
                inputVatClaimable: vatProfile.Registered);
            var subTotal = lines.Sum(l => l.Amount);
            var totalVat = lines.Sum(l => l.VatAmount);
            // SubTotal เป็นฐานก่อน VAT เสมอแล้ว (SplitLineVat) ⇒ ยอดรวมต้องบวก VAT
            var totalAmount = subTotal + totalVat;

            var document = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = DocumentType.CertificateInLieu,
                Status = DocumentStatus.Approved,
                DocumentDate = NormalizeDate(request.DocumentDate),
                ContactId = supplier.Id,
                Reference = request.ExternalRef,
                SubTotal = subTotal,
                VatAmount = totalVat,
                TotalAmount = totalAmount,
                BalanceDue = totalAmount,
                Notes = request.Notes,
                Lines = lines,
                CertificateReason = request.CertificateReason,
                CertifierName = request.CertifierName,
                CertifierPosition = request.CertifierPosition,
                WitnessName = request.WitnessName,
                WitnessPosition = request.WitnessPosition,
                PaymentDate = request.PaymentDate.HasValue ? NormalizeDate(request.PaymentDate.Value) : null
            };

            _db.Documents.Add(document);
            await _db.SaveChangesAsync();
            await _vendorIntel.TryTrainAsync(companyId, document.Id);

            var (journalEntryId, jeSkipReason) = await PostMappingJournalAsync(companyId, integrationId, document, "expense", log);

            if (log.Status != "PartialSuccess") log.Status = "Success";
            log.CreatedDocumentId = document.Id;
            log.CreatedContactId = supplier.Id;
            log.CreatedJournalEntryId = journalEntryId;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true,
                "Certificate in lieu created" + JeSkipSuffix(jeSkipReason),
                document.Id, supplier.Id, journalEntryId, null, docNumber);
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
            // Idempotency: a journal with this ExternalRef was already synced —
            // return it instead of inserting a duplicate. Makes a retry after a
            // post-commit network timeout safe, matching the invoice/expense
            // dedup behaviour. Scoped to IsAutoGenerated so it never collides
            // with a manually-entered journal that happens to share a Reference.
            if (!string.IsNullOrEmpty(request.ExternalRef))
            {
                var existing = await _db.JournalEntries
                    .FirstOrDefaultAsync(j => j.CompanyId == companyId
                        && j.Reference == request.ExternalRef
                        && j.IsAutoGenerated);
                if (existing != null)
                {
                    log.Status = "Skipped";
                    log.CreatedJournalEntryId = existing.Id;
                    log.ErrorMessage = "Journal entry already exists (idempotent skip)";
                    log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                    await SaveSyncLog(log, integrationId);
                    return new InboundSyncResponse(true, "Already synced", null, null, existing.Id, null, existing.EntryNumber);
                }
            }

            // Batch-load all referenced accounts in one query (avoid N+1)
            var allCodes = request.Lines.Select(l => l.AccountCode).Distinct().ToList();
            var accountLookup = await BatchLoadAccountsByCodesAsync(companyId, allCodes);

            var journalLines = new List<JournalEntryLine>();
            int lineOrder = 1;

            foreach (var line in request.Lines)
            {
                if (!accountLookup.TryGetValue(line.AccountCode, out var account))
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

            var journalType = request.JournalType?.ToLower() switch
            {
                "sales" => JournalType.Sales,
                "purchase" => JournalType.Purchase,
                "cashreceipts" or "cash_receipts" => JournalType.CashReceipts,
                "cashpayments" or "cash_payments" => JournalType.CashPayments,
                _ => JournalType.General
            };

            var totalDr = journalLines.Sum(l => l.DebitAmount);
            var totalCr = journalLines.Sum(l => l.CreditAmount);

            if (totalDr != totalCr)
            {
                if (!request.AutoBalanceVat)
                    throw new InvalidOperationException($"ยอดเดบิต ({totalDr:N2}) ไม่เท่ากับยอดเครดิต ({totalCr:N2}) — ส่ง AutoBalanceVat=true เพื่อเพิ่มรายการภาษีอัตโนมัติ");

                var isExpenseSide = journalType is JournalType.CashPayments or JournalType.Purchase;
                var vatAccount = await ResolveVatAccountAsync(companyId, isInput: isExpenseSide);

                if (vatAccount == null)
                    throw new InvalidOperationException($"ยอดเดบิต ({totalDr:N2}) ไม่เท่ากับยอดเครดิต ({totalCr:N2}) และไม่พบบัญชีภาษี{(isExpenseSide ? "ซื้อ (116)" : "ขาย (2191)")} สำหรับปรับยอด");

                var diff = totalCr - totalDr;
                journalLines.Add(new JournalEntryLine
                {
                    AccountId = vatAccount.Id,
                    DebitAmount = diff > 0 ? diff : 0,
                    CreditAmount = diff < 0 ? Math.Abs(diff) : 0,
                    Description = isExpenseSide ? "ภาษีซื้อ" : "ภาษีขาย",
                    LineOrder = lineOrder++
                });

                totalDr = journalLines.Sum(l => l.DebitAmount);
                totalCr = journalLines.Sum(l => l.CreditAmount);
            }

            var entryNumber = await GetNextJournalNumberAsync(companyId);
            var je = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = NormalizeDate(request.EntryDate),
                JournalType = journalType,
                Description = request.Description ?? "บันทึกจากระบบภายนอก",
                Reference = request.ExternalRef,
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                TotalDebit = totalDr,
                TotalCredit = totalCr,
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

    public async Task<InboundSyncResponse> ProcessJournalReverseAsync(Guid companyId, Guid integrationId, InboundReverseJournalRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "journal.reversed", request.ExternalId, request.ExternalRef);

        try
        {
            var original = await _db.JournalEntries
                .Include(j => j.Lines)
                .FirstOrDefaultAsync(j => j.Id == request.OriginalJournalEntryId && j.CompanyId == companyId)
                ?? throw new KeyNotFoundException($"ไม่พบรายการต้นฉบับ: {request.OriginalJournalEntryId}");

            if (original.Status != JournalEntryStatus.Posted)
                throw new InvalidOperationException("สามารถกลับรายการได้เฉพาะรายการที่มีสถานะ Posted");

            if (original.ReversedByEntryId.HasValue)
                throw new InvalidOperationException("รายการนี้ถูกกลับรายการไปแล้ว");

            var effectiveDate = request.ReversalDate.HasValue
                ? NormalizeDate(request.ReversalDate.Value)
                : DateTime.UtcNow.Date;

            var entryNumber = await GetNextJournalNumberAsync(companyId);
            var reversal = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = effectiveDate,
                JournalType = original.JournalType,
                Description = request.Description ?? $"กลับรายการ {original.EntryNumber}: {original.Description}",
                Reference = request.ExternalRef ?? original.Reference,
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                OriginalEntryId = original.Id,
                TotalDebit = original.TotalCredit,
                TotalCredit = original.TotalDebit
            };

            // Load dimension allocations from original lines
            var originalLineIds = original.Lines.Select(l => l.Id).ToList();
            var originalDims = await _db.JournalLineDimensions
                .Where(d => originalLineIds.Contains(d.JournalEntryLineId))
                .ToListAsync();

            int order = 1;
            foreach (var line in original.Lines.OrderBy(l => l.LineOrder))
            {
                var newLine = new JournalEntryLine
                {
                    AccountId = line.AccountId,
                    DebitAmount = line.CreditAmount,
                    CreditAmount = line.DebitAmount,
                    Description = line.Description,
                    LineOrder = order++
                };
                reversal.Lines.Add(newLine);

                foreach (var dim in originalDims.Where(d => d.JournalEntryLineId == line.Id))
                {
                    _db.JournalLineDimensions.Add(new JournalLineDimension
                    {
                        CompanyId = companyId,
                        JournalEntryLineId = newLine.Id,
                        DimensionId = dim.DimensionId,
                        AllocatedAmount = dim.AllocatedAmount,
                        AllocatedPercent = dim.AllocatedPercent
                    });
                }
            }

            _db.JournalEntries.Add(reversal);

            original.Status = JournalEntryStatus.Reversed;
            original.ReversedByEntryId = reversal.Id;
            original.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            log.Status = "Success";
            log.CreatedJournalEntryId = reversal.Id;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);

            return new InboundSyncResponse(true, "Journal entry reversed", null, null, reversal.Id, null, reversal.EntryNumber);
        }
        catch (Exception ex)
        {
            return await HandleSyncError(log, integrationId, ex, sw);
        }
    }

    /// <summary>
    /// Void a document pushed earlier by the integration. Looks up by
    /// ExternalRef (matched against Document.Reference) → ExternalId →
    /// DocumentId in that order, then delegates to DocumentService.VoidDocumentAsync
    /// which cascades: reverses Posted JE (systemTriggered), voids linked
    /// Payments + their JEs, unmatches bank transactions, restores any source
    /// document's balance.
    ///
    /// Typical workflow: booking system pushes a deposit Receipt, then on
    /// check-in pushes a void of the same ExternalRef and a new full-revenue
    /// Receipt with RelatedDocumentNumber pointing back to the deposit.
    /// </summary>
    public async Task<InboundSyncResponse> VoidDocumentByExternalRefAsync(Guid companyId, Guid integrationId, InboundVoidDocumentRequest request)
    {
        var sw = Stopwatch.StartNew();
        var log = CreateSyncLog(companyId, integrationId, "document.voided", request.ExternalId, request.ExternalRef);

        try
        {
            if (_documentService == null)
                throw new InvalidOperationException("DocumentService not available");

            Document? doc = null;
            if (request.DocumentId.HasValue)
            {
                doc = await _db.Documents
                    .FirstOrDefaultAsync(d => d.Id == request.DocumentId.Value && d.CompanyId == companyId && !d.IsDeleted);
            }
            if (doc == null && !string.IsNullOrWhiteSpace(request.ExternalRef))
            {
                doc = await _db.Documents
                    .FirstOrDefaultAsync(d => d.CompanyId == companyId
                        && d.Reference == request.ExternalRef
                        && !d.IsDeleted);
            }
            if (doc == null && !string.IsNullOrWhiteSpace(request.ExternalId))
            {
                // ExternalId may have been stored in DocumentNumber or Notes for older integrations.
                doc = await _db.Documents
                    .FirstOrDefaultAsync(d => d.CompanyId == companyId
                        && (d.DocumentNumber == request.ExternalId
                            || (d.Notes != null && d.Notes.Contains(request.ExternalId)))
                        && !d.IsDeleted);
            }
            if (doc == null)
                throw new InvalidOperationException("ไม่พบเอกสารตามที่ระบุ — กรุณาตรวจสอบ ExternalRef / ExternalId / DocumentId");

            if (doc.Status == DocumentStatus.Voided)
            {
                // Idempotency: a re-sent void on an already-voided document is a no-op.
                log.Status = "Skipped";
                log.CreatedDocumentId = doc.Id;
                log.ErrorMessage = "Document already voided (idempotent skip)";
                log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
                await SaveSyncLog(log, integrationId);
                return new InboundSyncResponse(true, "Document already voided", doc.Id, doc.ContactId, null, null, doc.DocumentNumber);
            }

            await _documentService.VoidDocumentAsync(companyId, doc.Id);

            log.Status = "Success";
            log.CreatedDocumentId = doc.Id;
            log.ProcessingTimeMs = (int)sw.ElapsedMilliseconds;
            await SaveSyncLog(log, integrationId);
            return new InboundSyncResponse(true, "Document voided " + (request.Reason ?? ""), doc.Id, doc.ContactId, null, null, doc.DocumentNumber);
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

        // Process payment vouchers (already-paid disbursements)
        if (request.PaymentVouchers != null)
        {
            foreach (var pv in request.PaymentVouchers)
            {
                var r = await ProcessPaymentVoucherAsync(companyId, integrationId, pv);
                results.Add(new BatchResultItem("PaymentVoucher", pv.ExternalRef, r.Success, r.Message, r.DocumentId, r.DocumentNumber));
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

        if (request.CertificatesInLieu != null)
        {
            foreach (var c in request.CertificatesInLieu)
            {
                var r = await ProcessCertificateInLieuAsync(companyId, integrationId, c);
                results.Add(new BatchResultItem("CertificateInLieu", c.ExternalRef, r.Success, r.Message, r.DocumentId, r.DocumentNumber));
                if (r.Success) success++; else errors++;
            }
        }

        return new InboundBatchResponse(success + errors, success, errors, results);
    }

    // ===== Outbound Data (external systems read FROM Next Acc) =====

    public async Task<OutboundPagedResponse<OutboundDocumentResponse>> GetDocumentsForExternalAsync(Guid companyId, OutboundQueryParams query)
    {
        // Performance: read-only outbound → AsNoTracking (ตัด change-tracker
        // snapshot ต่อ entity), cap PageSize (กัน caller ขอ "ทั้งเดือน" ทีเดียว
        // แล้ว materialize เอกสาร+บรรทัดหลายพันแบบ tracked = แขวน >60 วิ), และ
        // Include(Lines) แบบ split-query (กัน JOIN 1-to-many ระเบิดแถวตอน page ใหญ่).
        // ใช้ index (CompanyId, DocumentType, DocumentDate) / (CompanyId, DocumentDate).
        var baseQ = _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted);

        if (query.FromDate.HasValue) baseQ = baseQ.Where(d => d.DocumentDate >= query.FromDate.Value);
        if (query.ToDate.HasValue) baseQ = baseQ.Where(d => d.DocumentDate <= query.ToDate.Value);
        if (!string.IsNullOrEmpty(query.Status) && Enum.TryParse<DocumentStatus>(query.Status, true, out var status))
            baseQ = baseQ.Where(d => d.Status == status);
        if (!string.IsNullOrEmpty(query.Type) && Enum.TryParse<DocumentType>(query.Type, true, out var docType))
            baseQ = baseQ.Where(d => d.DocumentType == docType);

        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);

        var total = await baseQ.CountAsync();
        var docs = await baseQ
            .OrderByDescending(d => d.DocumentDate)
            .ThenBy(d => d.Id)   // tiebreaker — pagination + split-query ต้อง order คงที่
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(d => d.Lines)
            .AsSplitQuery()
            .ToListAsync();
        await _db.HydrateContactsAsync(companyId, docs);  // กัน INNER JOIN ตัดใบที่ contact ถูกลบ

        // ── ไฟล์แนบ — รวม "ไฟล์ของเอกสาร" (EntityType=Document) + "ไฟล์ต้นฉบับ
        // OCR" (ค้างที่ EntityType=OcrScan จาก relink พลาด). batch query กัน
        // N+1: หา FileAttachment ที่ EntityId ∈ docIds + OcrScan.FileAttachmentId
        // ที่ CreatedDocumentId ∈ docIds. map กลับเข้าแต่ละเอกสาร. นี่คือเหตุที่
        // "บนระบบมีไฟล์ แต่ api ดึงไปไม่มี" — เดิม outbound response ไม่ join
        // attachment เลย.
        var docIds = docs.Select(d => d.Id).ToList();
        var directFiles = await _db.FileAttachments.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted
                && f.EntityType == "Document" && docIds.Contains(f.EntityId))
            .ToListAsync();
        // OCR-orphan: scan.CreatedDocumentId ∈ docIds + FileAttachmentId ที่ยัง
        // ไม่ relink (ไม่อยู่ใน directFiles)
        var directIds = directFiles.Select(f => f.Id).ToHashSet();
        var scanMap = await _db.Set<OcrScanResult>().AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.CreatedDocumentId != null
                && docIds.Contains(s.CreatedDocumentId!.Value) && s.FileAttachmentId != null)
            .Select(s => new { DocId = s.CreatedDocumentId!.Value, FileId = s.FileAttachmentId!.Value })
            .ToListAsync();
        var orphanScanFileIds = scanMap.Select(x => x.FileId).Where(id => !directIds.Contains(id)).Distinct().ToList();
        var orphanFiles = orphanScanFileIds.Count == 0
            ? new List<Models.Entities.FileAttachment>()
            : await _db.FileAttachments.AsNoTracking()
                .Where(f => f.CompanyId == companyId && !f.IsDeleted && orphanScanFileIds.Contains(f.Id))
                .ToListAsync();

        // index: docId → list of (file)
        var filesByDoc = new Dictionary<Guid, List<Models.Entities.FileAttachment>>();
        foreach (var f in directFiles)
        {
            if (!filesByDoc.TryGetValue(f.EntityId, out var l)) { l = new(); filesByDoc[f.EntityId] = l; }
            l.Add(f);
        }
        foreach (var s in scanMap)
        {
            var of = orphanFiles.FirstOrDefault(f => f.Id == s.FileId);
            if (of == null) continue;
            if (!filesByDoc.TryGetValue(s.DocId, out var l)) { l = new(); filesByDoc[s.DocId] = l; }
            if (l.All(x => x.Id != of.Id)) l.Add(of);
        }

        string AttUrl(Guid fileId) => $"/api/companies/{companyId}/attachments/{fileId}/download";

        var items = docs.Select(d => new OutboundDocumentResponse(
                d.Id, d.DocumentNumber, d.DocumentType.ToString(), d.Status.ToString(),
                d.DocumentDate, d.DueDate,
                d.Contact != null ? d.Contact.Name : null, d.Contact != null ? d.Contact.TaxId : null,
                d.SubTotal, d.VatAmount, d.TotalAmount, d.PaidAmount, d.BalanceDue,
                d.Reference, d.Notes,
                d.Lines.Select(l => new OutboundDocumentLineResponse(
                    l.ProductCode, l.Description, l.Quantity, l.Unit, l.UnitPrice,
                    l.DiscountAmount, l.Amount, l.VatRate, l.VatAmount)).ToList(),
                d.CreatedAt,
                filesByDoc.TryGetValue(d.Id, out var fl)
                    ? fl.OrderByDescending(f => f.CreatedAt)
                        .Select(f => new OutboundAttachment(f.Id, f.FileName, f.OriginalFileName,
                            f.ContentType, f.FileSize, AttUrl(f.Id), f.CreatedAt)).ToList()
                    : null))
            .ToList();

        var totalPages = (int)Math.Ceiling((double)total / pageSize);
        return new OutboundPagedResponse<OutboundDocumentResponse>(items, total, page, pageSize, totalPages);
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

        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        var total = await q.CountAsync();
        var items = await q.AsNoTracking()
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new OutboundContactResponse(
                c.Id, c.Name, c.TaxId, c.BranchCode,
                c.ContactType.ToString(), c.IsCustomer, c.IsSupplier,
                c.Address, c.Phone, c.Email, c.CreatedAt,
                c.BranchName, c.BuildingNumber, c.BuildingName, c.StreetName,
                c.SubDistrict, c.District, c.Province, c.PostalCode,
                c.CountryCode, c.ContactPerson, c.IsActive, c.Moo))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)total / pageSize);
        return new OutboundPagedResponse<OutboundContactResponse>(items, total, page, pageSize, totalPages);
    }

    public async Task<OutboundPagedResponse<OutboundPaymentResponse>> GetPaymentsForExternalAsync(Guid companyId, OutboundQueryParams query)
    {
        // ไม่ Include(Document) — projection ด้านล่างดึงเฉพาะ DocumentNumber ผ่าน
        // LEFT JOIN อัตโนมัติ (Include ถูก projection override อยู่แล้ว) + AsNoTracking
        var q = _db.Set<Payment>().AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted);

        if (query.FromDate.HasValue) q = q.Where(p => p.PaymentDate >= query.FromDate.Value);
        if (query.ToDate.HasValue) q = q.Where(p => p.PaymentDate <= query.ToDate.Value);

        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(p => p.PaymentDate).ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new OutboundPaymentResponse(
                p.Id, p.PaymentNumber, p.DocumentId, p.Document != null ? p.Document.DocumentNumber : null,
                p.PaymentDate, p.Amount, p.PaymentMethod.ToString(),
                p.Reference, p.Notes, p.CreatedAt))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)total / pageSize);
        return new OutboundPagedResponse<OutboundPaymentResponse>(items, total, page, pageSize, totalPages);
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
