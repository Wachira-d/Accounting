using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 267 แพ็ก P5 — <b>จำข้อมูลผู้ลงนามด้วย LINE Login</b> (คำตัดสินเจ้าของ 267-1-13 · 267-1-73…267-1-76 · ดีไซน์ §14 · DOCUMENT_FLOW §2.12n-P5) ·
/// ผู้เขียนเดียวของ <see cref="CustomerSignerProfile"/> + ความยินยอม Purpose <see cref="CustomerSignerProfileRule.ConsentPurpose"/> ·
/// กติกา/ข้อความทั้งหมดอยู่ที่ <see cref="CustomerSignerProfileRule"/> (pure) — ที่นี่โหลด/เขียนเท่านั้น
///
/// <para>ลำดับ: (1) หน้าเอกสารขอ URL หน้าอนุญาตของ LINE (state ผูก token ของใบ) (2) LINE คืน code ⇒ <see cref="CompleteLoginAsync"/> ได้ "ตั๋วผู้ลงนาม" (HMAC · 30 นาที ·
/// ไม่เก็บในฐาน) + ค่าที่เติมฟอร์ม ถ้าเคยยินยอมไว้ (3) ลงนามสำเร็จ + ติ๊ก "จำไว้กับ LINE" ⇒ <see cref="RememberAsync"/> (ความยินยอม + โปรไฟล์ — ไม่ติ๊ก = ไม่มีอะไรถูกเก็บ)
/// (4) ถอน ⇒ <see cref="WithdrawAsync"/> ลบโปรไฟล์ทันที · ไม่ได้ใช้ 24 เดือน ⇒ <see cref="PurgeAsync"/> (งานกลางคืน ใต้ JobLock)</para>
///
/// <para>Kill-switch: ปิด LINE Login ของระบบ ⇒ ไม่มีปุ่ม · ลงนามได้ครบเหมือนเดิม (ฟอร์มกรอกมือ + จำบนเครื่อง 267-1-41) · ไม่มี AI ในเส้นนี้</para>
/// </summary>
public class CustomerSignerProfileService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CustomerSignerProfileService> _logger;
    private readonly ILineSignerLoginVerifier? _line;
    private readonly IFileStorage? _storage;
    private readonly IConfiguration? _config;

    public CustomerSignerProfileService(AccountingDbContext db, ILogger<CustomerSignerProfileService> logger,
        ILineSignerLoginVerifier? line = null, IFileStorage? storage = null, IConfiguration? config = null)
    {
        _db = db;
        _logger = logger;
        _line = line;
        _storage = storage;
        _config = config;
    }

    /// <summary>ที่อยู่ของระบบ (ตัวเดียวกับลิงก์หน้าเอกสาร) — Callback URL ของ LINE ต้องเป็นค่านี้ · ไม่ได้ตั้ง = ไม่มี LINE Login บนหน้า</summary>
    private Task<string?> OriginAsync(CancellationToken ct) => SystemBaseUrl.ResolveAsync(_db, _config, ct);

    public sealed record LoginResult(string? Ticket, CustomerSignerProfileRule.Prefill? Prefill, string? Error);
    /// <summary>URL หน้าอนุญาตของ LINE + nonce ที่ผู้เรียก (controller) ต้องตั้งเป็น cookie ของเบราว์เซอร์นี้ (ฝ่ายค้าน 267-P5 LOW-1)</summary>
    public sealed record LoginStart(string Url, string BrowserNonce);
    public sealed record RememberResult(Guid? ProfileId, string? Error);

    private sealed record LinkFacts(Guid DocumentId, Guid ContactId, string TokenHash, string SellerName);

    /// <summary>token ของหน้าเอกสารยังใช้ได้กับบริษัทนี้ไหม (ด่านเดียวกับหน้า: hash · ไม่เพิกถอน · ไม่หมดอายุ · ออกเลขแล้ว)</summary>
    private async Task<LinkFacts?> LinkAsync(Guid companyId, string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || !DocumentPayLinkPolicy.LooksValid(token)) return null;
        var hash = DocumentPayLinkPolicy.TokenHash(token);
        var d = await _db.Documents.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.PayLinkTokenHash == hash && !x.IsDeleted)
            .Select(x => new { x.Id, x.ContactId, x.Status, x.PayLinkTokenHash, x.PayLinkExpiresAt, x.PayLinkRevokedAt })
            .FirstOrDefaultAsync(ct);
        if (d == null || !DocumentPayLinkPolicy.TokenMatches(token, d.PayLinkTokenHash)
            || !DocumentPayLinkPolicy.LinkUsable(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, DateTime.UtcNow, null)
            || !DocumentStatusRules.IsIssued(d.Status)) return null;
        var seller = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        return new LinkFacts(d.Id, d.ContactId, hash, seller);
    }

    /// <summary>LINE Login ของระบบเปิดใช้ไหม (หน้าแสดงปุ่มเฉพาะเมื่อ true)</summary>
    public async Task<bool> AvailableAsync(CancellationToken ct = default)
        => _line != null && await _line.SignerLineChannelIdAsync() != null && !string.IsNullOrWhiteSpace(await OriginAsync(ct));

    /// <summary>(1) URL หน้าอนุญาตของ LINE — null = ปิด LINE Login / ลิงก์ใช้ไม่ได้</summary>
    public async Task<LoginStart?> LoginUrlAsync(Guid companyId, string token, CancellationToken ct = default)
    {
        var origin = await OriginAsync(ct);
        if (_line == null || string.IsNullOrWhiteSpace(origin)) return null;
        var channel = await _line.SignerLineChannelIdAsync();
        if (channel == null) return null;
        var link = await LinkAsync(companyId, token, ct);
        if (link == null) return null;
        var nonce = CustomerSignerProfileRule.NewNonce();
        return new LoginStart(CustomerSignerProfileRule.AuthorizeUrl(channel, CustomerSignerProfileRule.RedirectUri(origin),
            CustomerSignerProfileRule.NewState(companyId, link.TokenHash, DateTime.UtcNow, nonce)), nonce);
    }

    /// <summary>(2) LINE คืน code ⇒ ตั๋วผู้ลงนาม + ค่าที่เติม (ถ้าเคยยินยอมไว้ — ไม่มีช่องรับรอง) · ข้อความล้มเดียวทุกเหตุ</summary>
    public async Task<LoginResult> CompleteLoginAsync(Guid companyId, string token, string? code, string? state, string? browserNonce, CancellationToken ct = default)
    {
        const string fail = "เข้าด้วย LINE ไม่สำเร็จ — กรุณาลองใหม่ หรือกรอกข้อมูลเอง";
        var origin = await OriginAsync(ct);
        if (_line == null || string.IsNullOrWhiteSpace(origin)) return new(null, null, fail);
        var link = await LinkAsync(companyId, token, ct);
        var now = DateTime.UtcNow;
        if (link == null || !CustomerSignerProfileRule.StateValid(state, companyId, link.TokenHash, now, browserNonce) || string.IsNullOrWhiteSpace(code))
            return new(null, null, fail);
        var channel = await _line.SignerLineChannelIdAsync();
        var sub = channel == null ? null : await _line.ResolveSignerLineUserAsync(code, CustomerSignerProfileRule.RedirectUri(origin), ct);
        if (channel == null || sub == null) return new(null, null, fail);
        var subject = CustomerSignerProfileRule.SubjectKey(companyId, channel, sub);
        // ฝ่ายค้าน 267-P5 LOW-2 (migration แบบเจ้าของยืนยันเอง): แถวรุ่นแรกใช้กุญแจที่ไม่ผูกบริษัท — LINE เพิ่งยืนยันว่าคนนี้คือเจ้าของกุญแจเดิม ⇒ ย้ายแถวของบริษัทนี้มากุญแจใหม่
        // (คนเดียวกันเท่านั้น — ไม่มีทางย้ายแถวของคนอื่น) · แถวที่เจ้าของไม่กลับมาอีกถูกลบเมื่อครบ 24 เดือนตามเดิม
        var legacy = CustomerSignerProfileRule.LegacySubjectKey(channel, sub);
        if (!await _db.CustomerSignerProfiles.AnyAsync(x => x.CompanyId == companyId && x.SubjectKey == subject, ct))
            await _db.CustomerSignerProfiles.Where(x => x.CompanyId == companyId && x.SubjectKey == legacy)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.SubjectKey, subject), ct);
        var ticket = CustomerSignerProfileRule.IssueTicket(companyId, link.TokenHash, subject, now);
        var profile = await ActiveProfileAsync(companyId, subject, ct);
        if (profile != null)
        {
            profile.LastUsedAt = now;
            profile.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }
        return new(ticket, profile == null ? null : new CustomerSignerProfileRule.Prefill(profile.FirstName, profile.LastName, profile.Title, profile.Email), null);
    }

    /// <summary>โปรไฟล์ที่ความยินยอมยังมีผล (ถอนแล้ว/หาไม่พบ = ไม่ใช้ — ไม่เติม)</summary>
    private async Task<CustomerSignerProfile?> ActiveProfileAsync(Guid companyId, string subjectKey, CancellationToken ct)
    {
        var p = await _db.CustomerSignerProfiles.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SubjectKey == subjectKey, ct);
        if (p == null) return null;
        var consentActive = await _db.PdpaConsentRecords.AsNoTracking()
            .AnyAsync(c => c.Id == p.ConsentRecordId && c.CompanyId == companyId && c.WithdrawnAt == null, ct);
        return consentActive ? p : null;
    }

    /// <summary>โปรไฟล์ที่เติมฟอร์มของคำขอนี้ (ตั๋วถูกต้อง + ยินยอมอยู่ + ลงนามด้วยชื่อในโปรไฟล์ — ฝ่ายค้าน 267-P5 MED-1) —
    /// ผู้เรียก: <c>CustomerDocumentResponseService.RespondAsync</c> (อ้างที่มาในแถวหลักฐาน · คนอื่นพิมพ์ชื่อตัวเองบนตั๋วเดิม = ไม่อ้าง)</summary>
    public async Task<Guid?> ActiveProfileIdAsync(Guid companyId, string token, string ticket, CustomerSignRule.SignerInput? signer, CancellationToken ct = default)
    {
        var link = await LinkAsync(companyId, token, ct);
        var subject = link == null ? null : CustomerSignerProfileRule.ReadTicket(ticket, companyId, link.TokenHash, DateTime.UtcNow);
        var p = subject == null ? null : await ActiveProfileAsync(companyId, subject, ct);
        return p != null && CustomerSignerProfileRule.SignedAsProfile(p.FirstName, p.LastName, signer?.FirstName, signer?.LastName) ? p.Id : null;
    }

    /// <summary>
    /// (3) ผู้ลงนามติ๊ก "จำไว้กับ LINE" และลงนามสำเร็จแล้ว ⇒ ความยินยอม (ม.19 · หลักฐาน canonical) + โปรไฟล์ (เข้ารหัส) + RoPA ครั้งแรก + audit chain (ไม่มี PII) ·
    /// ตั๋วหมดอายุ/ผิดใบ = ไม่เก็บอะไร + ข้อความ · เคยยินยอมไว้ ⇒ ใช้ความยินยอมเดิม แก้ค่าเป็นค่าล่าสุดที่ผู้ลงนามพิมพ์
    /// </summary>
    public async Task<RememberResult> RememberAsync(Guid companyId, string token, string ticket, CustomerSignRule.SignerInput? signer,
        CustomerDocumentResponseService.Caller caller, string? shownConsentVersion, CancellationToken ct = default)
    {
        var link = await LinkAsync(companyId, token, ct);
        var now = DateTime.UtcNow;
        var subject = link == null ? null : CustomerSignerProfileRule.ReadTicket(ticket, companyId, link.TokenHash, now);
        if (link == null || subject == null) return new(null, "จำข้อมูลกับ LINE ไม่สำเร็จ (การเข้าด้วย LINE หมดอายุ) — เข้าด้วย LINE ใหม่ครั้งหน้า");
        // ฝ่ายค้าน 267-P5 MED-2: ความยินยอมผูกกับข้อความที่หน้าแสดงจริง (รุ่น + ภาษาที่หน้าส่งกลับ) — รุ่นไม่ตรง = ไม่จำ (หน้าค้างจากรุ่นก่อน)
        if (CustomerSignerProfileRule.LanguageOfShownVersion(shownConsentVersion) is not bool en)
            return new(null, "จำข้อมูลกับ LINE ไม่สำเร็จ (ข้อความยินยอมบนหน้าเป็นรุ่นเก่า) — โหลดหน้าใหม่แล้วติ๊กจำอีกครั้งครั้งหน้า");
        var first = QuotationAcceptance.CleanText(signer?.FirstName, CustomerSignRule.NamePartMax);
        var last = QuotationAcceptance.CleanText(signer?.LastName, CustomerSignRule.NamePartMax);
        if (first == null || last == null) return new(null, "จำข้อมูลกับ LINE ไม่สำเร็จ (ไม่มีชื่อ-นามสกุล)");
        var title = QuotationAcceptance.CleanText(signer?.Title, CustomerSignRule.TitleMax);
        var email = CustomerSignRule.CleanEmail(signer?.Email, false).Email;

        var profile = await _db.CustomerSignerProfiles.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SubjectKey == subject, ct);
        var consentActive = profile != null && await _db.PdpaConsentRecords.AsNoTracking()
            .AnyAsync(c => c.Id == profile.ConsentRecordId && c.CompanyId == companyId && c.WithdrawnAt == null, ct);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        Guid consentId;
        if (consentActive) consentId = profile!.ConsentRecordId;
        else
        {
            var text = CustomerSignerProfileRule.ConsentText(en, link.SellerName);
            var version = CustomerSignerProfileRule.StoredVersion(en);
            var consent = new PdpaConsentRecord
            {
                CompanyId = companyId, SubjectContactId = link.ContactId, SubjectContact = email,
                Purpose = CustomerSignerProfileRule.ConsentPurpose, PolicyVersion = version, GrantedAt = CustomerSignerProfileRule.StoredInstant(now),
                EvidenceHash = CustomerSignerProfileRule.ConsentEvidenceHash(companyId, subject, text, version, now), EvidenceSource = "system",
                Channel = CustomerSignerProfileRule.Channel, IpAddress = caller.Ip, UserAgent = QuotationAcceptance.CleanText(caller.UserAgent, 500),
                CreatedBy = "ผู้ลงนามผ่านหน้าเอกสาร (LINE Login)",
            };
            _db.PdpaConsentRecords.Add(consent);
            consentId = consent.Id;
        }
        if (profile == null)
        {
            profile = new CustomerSignerProfile { CompanyId = companyId, SubjectKey = subject, CreatedBy = "ผู้ลงนามผ่านหน้าเอกสาร (LINE Login)" };
            _db.CustomerSignerProfiles.Add(profile);
        }
        profile.ContactId = link.ContactId;
        profile.FirstName = first;
        profile.LastName = last;
        profile.Title = title;
        profile.Email = email;
        profile.ConsentRecordId = consentId;
        profile.LastUsedAt = now;
        profile.UpdatedAt = now;
        await EnsureRopaAsync(companyId, ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = "ผู้ลงนามผ่านหน้าเอกสาร (LINE Login)", Action = AuditAction.Update,
            EntityType = nameof(CustomerSignerProfile), EntityId = profile.Id.ToString(),
            // ไม่มีชื่อ/อีเมลใน audit (append-only — ลบตามคำขอไม่ได้ · 267-1-51)
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "CustomerSignerProfileSaved", documentId = link.DocumentId, consentRecordId = consentId, newConsent = !consentActive,
                policyVersion = CustomerSignerProfileRule.StoredVersion(en), ruleCode = CustomerSignerProfileRule.RuleCode,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(profile.Id, null);
    }

    /// <summary>(4) ถอนความยินยอม (ตั๋วของลิงก์นี้) ⇒ ลบโปรไฟล์ทันที + ประทับถอนบนแถวความยินยอม · แถวหลักฐานการลงนามไม่ถูกแตะ (S35) · ไม่มีโปรไฟล์ = สำเร็จ (ไม่มีอะไรให้ลบ)</summary>
    public async Task<string?> WithdrawAsync(Guid companyId, string token, string? ticket, CancellationToken ct = default)
    {
        var link = await LinkAsync(companyId, token, ct);
        var subject = link == null ? null : CustomerSignerProfileRule.ReadTicket(ticket, companyId, link.TokenHash, DateTime.UtcNow);
        if (subject == null) return "กรุณาเข้าด้วย LINE ก่อน แล้วกดลบข้อมูลที่จำไว้อีกครั้ง";
        await RemoveAsync(companyId, x => x.SubjectKey == subject, "ผู้ลงนามถอนความยินยอมบนหน้าเอกสาร", "ผู้ลงนามผ่านหน้าเอกสาร (LINE Login)", ct);
        return null;
    }

    /// <summary>
    /// ฝ่ายค้าน 267-P5 MED-4: แถวความยินยอมของการจำข้อมูลผู้ลงนามที่ถูกถอน/ลบตามคำขอ/ลบอัตโนมัติ ⇒ ล้างอีเมล · IP · UA (ประทับ <c>PseudonymisedAt</c>) ·
    /// คง EvidenceHash + เวลาให้/ถอน เป็นหลักฐาน (ภาระพิสูจน์ ม.19) · เฉพาะ Purpose ของเส้นนี้ · ทำซ้ำได้
    /// </summary>
    internal static Task<int> AnonymizeConsentsAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> consentIds, DateTime nowUtc, CancellationToken ct)
    {
        return consentIds.Count == 0 ? Task.FromResult(0)
            : db.PdpaConsentRecords.IgnoreQueryFilters()
                .Where(c => c.CompanyId == companyId && consentIds.Contains(c.Id) && c.Purpose == CustomerSignerProfileRule.ConsentPurpose && c.PseudonymisedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.SubjectContact, (string?)null).SetProperty(c => c.IpAddress, (string?)null)
                    .SetProperty(c => c.UserAgent, (string?)null).SetProperty(c => c.PseudonymisedAt, (DateTime?)nowUtc)
                    .SetProperty(c => c.WithdrawnReason, c => (c.WithdrawnReason ?? "") + CustomerSignerProfileRule.ConsentAnonymizedReasonSuffix), ct);
    }

    /// <summary>ลบโปรไฟล์ตามเงื่อนไข (ลบจริง ไม่ใช่ soft delete — ข้อมูลตามความยินยอมไม่มีภาระเก็บ) + ถอนความยินยอม + audit · คืนจำนวนที่ลบ</summary>
    private async Task<int> RemoveAsync(Guid companyId, System.Linq.Expressions.Expression<Func<CustomerSignerProfile, bool>> which, string reason, string actor, CancellationToken ct)
    {
        var rows = await _db.CustomerSignerProfiles.IgnoreQueryFilters().Where(x => x.CompanyId == companyId).Where(which)
            .Select(x => new { x.Id, x.ConsentRecordId }).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        var now = DateTime.UtcNow;
        var ids = rows.Select(r => r.Id).ToList();
        var consentIds = rows.Select(r => r.ConsentRecordId).ToList();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.PdpaConsentRecords.Where(c => c.CompanyId == companyId && consentIds.Contains(c.Id) && c.WithdrawnAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.WithdrawnAt, (DateTime?)now).SetProperty(c => c.WithdrawnReason, reason), ct);
        await AnonymizeConsentsAsync(_db, companyId, consentIds, now, ct);
        var deleted = await _db.CustomerSignerProfiles.IgnoreQueryFilters().Where(x => x.CompanyId == companyId && ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = actor, Action = AuditAction.Delete, EntityType = nameof(CustomerSignerProfile), EntityId = string.Join(",", ids.Take(20)),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "CustomerSignerProfileDeleted", count = deleted, reason, ruleCode = CustomerSignerProfileRule.RuleCode }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return deleted;
    }

    /// <summary>
    /// งานกลางคืน (ผู้เรียกเดียว <c>PdpaRetentionPurgeJob</c> ใต้ <c>JobLock</c> — ไม่เพิ่ม BackgroundService · F4 ข้อ 6): ลบโปรไฟล์ที่ไม่ได้ใช้เกิน 24 เดือน
    /// หรือความยินยอมถูกถอน/หาไม่พบ (ตัวตัดสิน <see cref="CustomerSignerProfileRule.ShouldPurge"/>) · ทุกบริษัท · คืนจำนวนที่ลบ
    /// </summary>
    public static async Task<int> PurgeAsync(AccountingDbContext db, DateTime nowUtc, CancellationToken ct = default)
    {
        var cutoff = CustomerSignerProfileRule.PurgeCutoff(nowUtc);
        var candidates = await db.CustomerSignerProfiles.IgnoreQueryFilters().AsNoTracking()
            .Select(p => new
            {
                p.Id, p.CompanyId, p.ConsentRecordId, p.LastUsedAt,
                ConsentActive = db.PdpaConsentRecords.IgnoreQueryFilters().Any(c => c.Id == p.ConsentRecordId && c.CompanyId == p.CompanyId && c.WithdrawnAt == null),
            })
            .Where(p => p.LastUsedAt < cutoff || !p.ConsentActive)
            .Take(5000).ToListAsync(ct);
        var due = candidates.Where(p => CustomerSignerProfileRule.ShouldPurge(p.LastUsedAt, p.ConsentActive, nowUtc)).ToList();
        var total = 0;
        foreach (var company in due.GroupBy(p => p.CompanyId))
        {
            var ids = company.Select(p => p.Id).ToList();
            var consentIds = company.Select(p => p.ConsentRecordId).ToList();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // ฝ่ายค้าน 267-P5 LOW-6: ตรวจเงื่อนไขซ้ำใน WHERE ของการลบ — ผู้ลงนามที่เพิ่งใช้ระหว่างงานรัน (LastUsedAt ใหม่ + ยินยอมอยู่) ไม่ถูกลบ ·
            // ถอน/ล้างเฉพาะความยินยอมที่ไม่มีโปรไฟล์อ้างแล้ว (= แถวที่ลบจริง)
            var n = await db.CustomerSignerProfiles.IgnoreQueryFilters()
                .Where(x => x.CompanyId == company.Key && ids.Contains(x.Id)
                    && (x.LastUsedAt < cutoff || !db.PdpaConsentRecords.IgnoreQueryFilters().Any(c => c.Id == x.ConsentRecordId && c.CompanyId == x.CompanyId && c.WithdrawnAt == null)))
                .ExecuteDeleteAsync(ct);
            var orphaned = await db.PdpaConsentRecords.IgnoreQueryFilters()
                .Where(c => c.CompanyId == company.Key && consentIds.Contains(c.Id)
                    && !db.CustomerSignerProfiles.IgnoreQueryFilters().Any(p => p.CompanyId == c.CompanyId && p.ConsentRecordId == c.Id))
                .Select(c => c.Id).ToListAsync(ct);
            await db.PdpaConsentRecords.IgnoreQueryFilters().Where(c => c.CompanyId == company.Key && orphaned.Contains(c.Id) && c.WithdrawnAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.WithdrawnAt, (DateTime?)nowUtc)
                    .SetProperty(c => c.WithdrawnReason, $"ไม่ได้ใช้ข้อมูลที่จำไว้เกิน {CustomerSignerProfileRule.RetentionMonths} เดือน — ลบอัตโนมัติ"), ct);
            await AnonymizeConsentsAsync(db, company.Key, orphaned, nowUtc, ct);
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = company.Key, UserEmail = "ระบบ (ลบข้อมูลผู้ลงนามที่หมดอายุ)", Action = AuditAction.Delete,
                EntityType = nameof(CustomerSignerProfile), EntityId = string.Join(",", ids.Take(20)),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "CustomerSignerProfilePurged", count = n, ruleCode = CustomerSignerProfileRule.RuleCode }),
                Timestamp = nowUtc,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            total += n;
        }
        return total;
    }

    // ═══════════════ DSR (PDPA ม.30 · ม.31 · ม.33) — ผู้ลงนามเป็นบุคคลที่สาม (267-1-40 · 267-1-75 · ฝ่ายค้าน 267-P5 MED-3/MED-4/LOW-4/LOW-5) ═══════════════
    // ตัวระบุเจ้าของข้อมูล: อีเมล หรือ ชื่อ+นามสกุล+เลขเอกสารที่ผู้นั้นลงนาม (ชื่ออย่างเดียวไม่พอ — คนชื่อซ้ำ) · ชื่อ+เลขเอกสารพบหลายคน ⇒ ให้ DPO เลือกด้วยอีเมล (ไม่รวมเอง)

    /// <summary>คำขอ DSR ของผู้ลงนาม — ต้องมี <see cref="Email"/> หรือ (<see cref="FirstName"/> + <see cref="LastName"/> + <see cref="DocumentNumber"/>)</summary>
    public sealed record SignerQuery(string? Email, string? FirstName, string? LastName, string? DocumentNumber);

    public sealed record SignerSubjectReport(string? Email, string? FirstName, string? LastName, string? DocumentNumber,
        IReadOnlyList<Dictionary<string, object?>> Profiles, IReadOnlyList<Dictionary<string, object?>> Consents,
        IReadOnlyList<Dictionary<string, object?>> Evidence, int TotalRecords);

    public sealed record SignerErasureResult(int ProfilesDeleted, int ConsentsAnonymized, int EvidenceErased, int EvidenceHeld, IReadOnlyList<string> Notes);

    /// <summary>ชื่อ+เลขเอกสารพบผู้ลงนามหลายคน (อีเมลต่างกัน) — DPO ต้องเลือกด้วยอีเมล · ไม่รวมข้อมูลของหลายคนเอง</summary>
    public sealed class SignerSubjectAmbiguousException : Exception
    {
        public IReadOnlyList<string> Candidates { get; }
        public SignerSubjectAmbiguousException(IReadOnlyList<string> candidates)
            : base($"พบผู้ลงนามชื่อนี้บนเอกสารนี้ {candidates.Count} คน (อีเมลต่างกัน) — ระบุอีเมลของเจ้าของข้อมูลเพื่อเลือก (ระบบไม่รวมข้อมูลของหลายคนให้เอง)")
            => Candidates = candidates;
    }

    /// <summary>เพดานแถวหลักฐานต่อคำขอ — เกิน = ล้มดัง (ไม่ส่ง/ไม่ลบไม่ครบแบบเงียบ · LOW-5)</summary>
    public const int DsrEvidenceCap = 5000;

    private static string Norm(string? s) => (s ?? "").Trim().ToLowerInvariant();

    /// <summary>อีเมลแบบปิดบังสำหรับให้ DPO เลือก ("s***@buyer.co.th")</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "***" : email[..1] + "***" + email[at..];
    }

    /// <summary>LOW-4: ตัวระบุเจ้าของข้อมูลใน PiiAccessLog — Guid ที่ได้จาก hash ของตัวระบุ (ไม่เก็บอีเมล/ชื่อดิบใน log · ค้นย้อนได้ด้วยตัวระบุเดิม) + ชื่อช่อง</summary>
    public static (Guid SubjectId, string FieldName) LogSubject(Guid companyId, SignerQuery q)
    {
        var e = CustomerSignRule.CleanEmail(q.Email, false).Email;
        var key = e != null ? "email|" + e : $"name|{Norm(q.FirstName)}|{Norm(q.LastName)}|doc|{Norm(q.DocumentNumber)}";
        var h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"signer-dsr|{companyId:N}|{key}"));
        return (new Guid(h.AsSpan(0, 16)), (e != null ? "email-sha256:" : "name-doc-sha256:") + Convert.ToHexString(h, 16, 8).ToLowerInvariant());
    }

    private sealed record Resolved(string? Email, string? First, string? Last, string? DocumentNumber, IReadOnlyList<Guid> NameOnlyRowIds);

    /// <summary>ตัวระบุ ⇒ เจ้าของข้อมูลคนเดียว · อีเมล = ตัวระบุ · ชื่อ+เลขเอกสาร ⇒ หาแถวบนเอกสารนั้น: อีเมลเดียว = ใช้อีเมลนั้น · หลายอีเมล = ให้ DPO เลือก ·
    /// ไม่มีอีเมล = เฉพาะแถวบนเอกสารนั้น (ไม่ขยายไปแถว/โปรไฟล์อื่นที่ชื่อตรง)</summary>
    private async Task<Resolved> ResolveAsync(Guid companyId, SignerQuery q, CancellationToken ct)
    {
        var e = CustomerSignRule.CleanEmail(q.Email, false).Email;
        var f = QuotationAcceptance.CleanText(q.FirstName, CustomerSignRule.NamePartMax);
        var l = QuotationAcceptance.CleanText(q.LastName, CustomerSignRule.NamePartMax);
        var docNo = QuotationAcceptance.CleanText(q.DocumentNumber, 100);
        if (e != null) return new Resolved(e, f, l, docNo, Array.Empty<Guid>());
        if (f == null || l == null || docNo == null)
            throw new InvalidOperationException("ระบุอีเมลของผู้ลงนาม หรือ ชื่อ + นามสกุล + เลขเอกสารที่ผู้นั้นลงนาม (ชื่ออย่างเดียวไม่พอ — อาจมีคนชื่อซ้ำ)");
        var docIds = await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && d.DocumentNumber == docNo && !d.IsDeleted)
            .Select(d => d.Id).ToListAsync(ct);
        if (docIds.Count == 0) throw new InvalidOperationException($"ไม่พบเอกสารเลข {docNo} ของบริษัทนี้");
        var rows = (await _db.CustomerDocumentActions.AsNoTracking()
                .Where(a => a.CompanyId == companyId && docIds.Contains(a.DocumentId) && a.ErasedAt == null)
                .Select(a => new { a.Id, a.SignerFirstName, a.SignerLastName, a.SignerEmail }).ToListAsync(ct))
            .Where(a => Norm(a.SignerFirstName) == Norm(f) && Norm(a.SignerLastName) == Norm(l)).ToList();
        if (rows.Count == 0) throw new InvalidOperationException($"ไม่พบผู้ลงนามชื่อนี้บนเอกสารเลข {docNo}");
        var emails = rows.Select(r => Norm(r.SignerEmail)).Where(x => x.Length > 0).Distinct().ToList();
        if (emails.Count > 1) throw new SignerSubjectAmbiguousException(emails.Select(MaskEmail).ToList());
        return emails.Count == 1
            ? new Resolved(emails[0], f, l, docNo, Array.Empty<Guid>())
            : new Resolved(null, f, l, docNo, rows.Select(r => r.Id).ToList());
    }

    /// <summary>โปรไฟล์ของบริษัทนี้ของเจ้าของข้อมูล (อีเมลเท่านั้น — ชื่ออย่างเดียวไม่ใช่ตัวระบุ) · ชื่อ/อีเมลเข้ารหัสในฐาน ⇒ ถอดแล้วกรองในหน่วยความจำ</summary>
    private async Task<List<CustomerSignerProfile>> MatchingProfilesAsync(Guid companyId, Resolved r, CancellationToken ct)
        => r.Email == null ? new List<CustomerSignerProfile>()
            : (await _db.CustomerSignerProfiles.AsNoTracking().Where(p => p.CompanyId == companyId).ToListAsync(ct))
                .Where(p => Norm(p.Email) == r.Email).ToList();

    private async Task<List<CustomerDocumentAction>> MatchingEvidenceAsync(Guid companyId, Resolved r, bool track, CancellationToken ct)
    {
        var q = _db.CustomerDocumentActions.Where(a => a.CompanyId == companyId && a.ErasedAt == null);
        if (!track) q = q.AsNoTracking();
        var ids = r.NameOnlyRowIds;
        var email = r.Email;
        var rows = await (email != null
                ? q.Where(a => a.SignerEmail != null && a.SignerEmail.ToLower() == email)
                : q.Where(a => ids.Contains(a.Id)))
            .OrderBy(a => a.CreatedAt).Take(DsrEvidenceCap + 1).ToListAsync(ct);
        if (rows.Count > DsrEvidenceCap)
            throw new InvalidOperationException($"หลักฐานการลงนามของเจ้าของข้อมูลนี้เกิน {DsrEvidenceCap} แถว — ระบบไม่ส่ง/ไม่ลบแบบไม่ครบ · ติดต่อผู้ดูแลระบบเพื่อดำเนินการเป็นชุด");
        return rows;
    }

    /// <summary>แถวความยินยอมของเส้นนี้ของเจ้าของข้อมูล — ของโปรไฟล์ที่พบ + ที่บันทึกอีเมลนี้ไว้ (รวมที่ถอนแล้วแต่ยังไม่ถูกล้าง)</summary>
    private async Task<List<PdpaConsentRecord>> MatchingConsentsAsync(Guid companyId, Resolved r, IReadOnlyCollection<Guid> profileConsentIds, CancellationToken ct)
    {
        var email = r.Email;
        if (email == null && profileConsentIds.Count == 0) return new List<PdpaConsentRecord>();
        return await _db.PdpaConsentRecords.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.Purpose == CustomerSignerProfileRule.ConsentPurpose
                && (profileConsentIds.Contains(c.Id) || (email != null && c.SubjectContact != null && c.SubjectContact.ToLower() == email)))
            .OrderBy(c => c.GrantedAt).ToListAsync(ct);
    }

    /// <summary>ม.30/31 — โปรไฟล์ที่จำไว้ + แถวความยินยอม (อีเมล · IP · UA ที่ยังไม่ถูกล้าง) + แถวหลักฐานการลงนาม (ไม่ฝังไฟล์) · ผู้เรียกตรวจสิทธิ์ DPO + บันทึก PiiAccessLog</summary>
    public async Task<SignerSubjectReport> AccessAsync(Guid companyId, SignerQuery query, CancellationToken ct = default)
    {
        var r = await ResolveAsync(companyId, query, ct);
        var profiles = await MatchingProfilesAsync(companyId, r, ct);
        var consents = await MatchingConsentsAsync(companyId, r, profiles.Select(p => p.ConsentRecordId).ToList(), ct);
        var byId = consents.ToDictionary(c => c.Id);
        var evidence = await MatchingEvidenceAsync(companyId, r, false, ct);
        var docIds = evidence.Select(a => a.DocumentId).Distinct().ToList();
        var numbers = await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType }).ToDictionaryAsync(d => d.Id, ct);
        return new SignerSubjectReport(r.Email, r.First, r.Last, r.DocumentNumber,
            profiles.Select(p => new Dictionary<string, object?>
            {
                ["id"] = p.Id, ["firstName"] = p.FirstName, ["lastName"] = p.LastName, ["title"] = p.Title, ["email"] = p.Email,
                ["contactId"] = p.ContactId, ["lastUsedAt"] = p.LastUsedAt, ["createdAt"] = p.CreatedAt,
                ["consentPurpose"] = CustomerSignerProfileRule.ConsentPurpose,
                ["consentGrantedAt"] = byId.TryGetValue(p.ConsentRecordId, out var c) ? c.GrantedAt : null,
                ["consentVersion"] = byId.TryGetValue(p.ConsentRecordId, out var c2) ? c2.PolicyVersion : null,
                ["linkedTo"] = "บัญชี LINE (เก็บเป็น HMAC — ระบบไม่มี LINE userId)",
            }).ToList(),
            consents.Select(x => new Dictionary<string, object?>
            {
                ["id"] = x.Id, ["purpose"] = x.Purpose, ["version"] = x.PolicyVersion, ["grantedAt"] = x.GrantedAt, ["withdrawnAt"] = x.WithdrawnAt,
                ["withdrawnReason"] = x.WithdrawnReason, ["email"] = x.SubjectContact, ["ip"] = x.IpAddress, ["userAgent"] = x.UserAgent,
                ["channel"] = x.Channel, ["evidenceHash"] = x.EvidenceHash, ["anonymizedAt"] = x.PseudonymisedAt,
            }).ToList(),
            evidence.Select(a => new Dictionary<string, object?>
            {
                ["id"] = a.Id, ["documentNumber"] = numbers.TryGetValue(a.DocumentId, out var n) ? n.DocumentNumber : null,
                ["documentType"] = numbers.TryGetValue(a.DocumentId, out var n2) ? n2.DocumentType.ToString() : null,
                ["revisionNumber"] = a.RevisionNumber, ["action"] = a.Action, ["channel"] = a.Channel, ["at"] = a.CreatedAt,
                ["signerFirstName"] = a.SignerFirstName, ["signerLastName"] = a.SignerLastName, ["signerTitle"] = a.SignerTitle,
                ["signerOrgName"] = a.SignerOrgName, ["signerEmail"] = a.SignerEmail, ["message"] = a.Message, ["promisedDate"] = a.PromisedDate,
                ["ip"] = a.Ip, ["userAgentSha256"] = a.UserAgentHash, ["contentHash"] = a.ContentHash,
                ["hasSignature"] = a.SignatureSha256 != null, ["hasSignedCopy"] = a.SignedPdfSha256 != null,
            }).ToList(),
            profiles.Count + consents.Count + evidence.Count);
    }

    /// <summary>
    /// ม.33 — ลบโปรไฟล์ที่จำไว้ทั้งหมดของผู้ลงนามรายนี้ (ความยินยอม — ลบได้เสมอ) + ถอน/ล้างอีเมล·IP·UA บนแถวความยินยอม (คง hash + เวลา) ·
    /// แถวหลักฐานการลงนาม: เอกสารยังอยู่ในอายุเก็บ = คงไว้ (legal hold · S33) · พ้นแล้ว = ตัวล้างเดียว <see cref="QuotationAcceptance.EraseEvidencePii"/> + ลบไฟล์
    /// </summary>
    public async Task<SignerErasureResult> EraseAsync(Guid companyId, SignerQuery query, string actor, CancellationToken ct = default)
    {
        var r = await ResolveAsync(companyId, query, ct);
        var profiles = await MatchingProfilesAsync(companyId, r, ct);
        var profileIds = profiles.Select(p => p.Id).ToList();
        var consentIds = (await MatchingConsentsAsync(companyId, r, profiles.Select(p => p.ConsentRecordId).ToList(), ct)).Select(c => c.Id).ToList();
        var evidence = await MatchingEvidenceAsync(companyId, r, true, ct);   // ตรวจเพดานก่อนลบอะไร (ล้มก่อน = ไม่ลบครึ่งทาง)
        var now = DateTime.UtcNow;
        // ความยินยอมก่อน (นับแถวที่ล้าง) แล้วจึงลบโปรไฟล์ (RemoveAsync เจอแถวที่ถอน/ล้างแล้ว = ไม่ทำซ้ำ)
        await _db.PdpaConsentRecords.Where(c => c.CompanyId == companyId && consentIds.Contains(c.Id) && c.WithdrawnAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.WithdrawnAt, (DateTime?)now).SetProperty(c => c.WithdrawnReason, "เจ้าของข้อมูลขอลบ (DSR ม.33)"), ct);
        var anonymized = await AnonymizeConsentsAsync(_db, companyId, consentIds, now, ct);
        var profilesDeleted = profileIds.Count == 0 ? 0
            : await RemoveAsync(companyId, x => profileIds.Contains(x.Id), "เจ้าของข้อมูลขอลบ (DSR ม.33)", actor, ct);
        var docIds = evidence.Select(a => a.DocumentId).Distinct().ToList();
        var docs = await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentDate, d.RetentionUntil }).ToDictionaryAsync(d => d.Id, ct);
        var files = new List<string>();
        int erased = 0, held = 0;
        foreach (var row in evidence)
        {
            if (!docs.TryGetValue(row.DocumentId, out var d) || QuotationAcceptance.UnderLegalHold(d.DocumentDate, d.RetentionUntil, now)) { held++; continue; }
            files.AddRange(QuotationAcceptance.EraseEvidencePii(row, now));
            row.UpdatedAt = now;
            row.UpdatedBy = actor;
            erased++;
        }
        if (erased > 0 || anonymized > 0)
        {
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserEmail = actor, Action = AuditAction.Update, EntityType = nameof(CustomerDocumentAction), EntityId = "dsr-signer",
                NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "CustomerSignerEvidenceErased", erased, held, consentsAnonymized = anonymized, ruleCode = "PDPA-33" }),
                Timestamp = now,
            });
            await _db.SaveChangesAsync(ct);
            if (_storage != null)
                foreach (var file in files.Distinct())
                {
                    try { await _storage.DeleteAsync(file, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "ลบไฟล์ของแถวหลักฐานที่ลบข้อมูลแล้วไม่สำเร็จ (company {CompanyId}) — ต้องลบด้วยมือ", companyId);
                    }
                }
        }
        var notes = new List<string>
        {
            $"ลบข้อมูลที่จำไว้กับ LINE {profilesDeleted} รายการ (ถอนความยินยอมแล้ว)",
            $"ล้างอีเมล/IP/UA บนแถวความยินยอม {anonymized} แถว (คง hash + เวลาเป็นหลักฐานการให้และการถอน)",
            $"ล้างข้อมูลผู้ลงนามในหลักฐานการลงนามที่พ้นอายุเก็บ {erased} แถว",
        };
        if (held > 0)
            notes.Add($"คงไว้ {held} แถว — เอกสารยังอยู่ในอายุเก็บตาม พ.ร.บ.การบัญชี ม.14 / ป.รัษฎากร §87/3 (legal hold) · ลบได้เมื่อพ้นอายุเก็บ");
        if (r.Email == null)
            notes.Add("ค้นด้วยชื่อ + เลขเอกสาร (แถวบนเอกสารนั้นไม่มีอีเมล) — ดำเนินการเฉพาะแถวบนเอกสารนั้น · มีอีเมลของเจ้าของข้อมูล ให้ค้นด้วยอีเมลเพื่อครอบคลุมเอกสารอื่น");
        return new SignerErasureResult(profilesDeleted, anonymized, erased, held, notes);
    }

    /// <summary>RoPA (ม.39) แถว "จำข้อมูลผู้ลงนาม" ครั้งแรกของบริษัท (ไม่ทับที่บริษัทแก้ไว้) — ในธุรกรรมเดียวกับโปรไฟล์แรก</summary>
    private async Task EnsureRopaAsync(Guid companyId, CancellationToken ct)
    {
        if (await _db.PdpaProcessingActivities.AsNoTracking().AnyAsync(r => r.CompanyId == companyId && r.Purpose == CustomerSignerProfileRule.RopaPurpose, ct)) return;
        _db.PdpaProcessingActivities.Add(new PdpaProcessingActivity
        {
            CompanyId = companyId, Purpose = CustomerSignerProfileRule.RopaPurpose, LegalBasis = CustomerSignerProfileRule.RopaLegalBasis,
            DataCategories = CustomerSignerProfileRule.RopaDataCategories, RetentionPeriod = CustomerSignerProfileRule.RopaRetention,
            Recipients = CustomerSignerProfileRule.RopaRecipients, TransfersOutsideThailand = false, Notes = CustomerSignerProfileRule.RopaNote,
            CreatedBy = "system", LastReviewedBy = "system",
        });
    }
}
