using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §3.2 ช่อง ① · §6.5 · §9 เฟส 244b) — <b>ลิงก์อัปโหลดหนังสือรับรอง 50 ทวิ แบบไม่ต้องล็อกอิน</b>
///
/// <para>ลำดับฝั่งรับไฟล์ (<see cref="PublicUploadAsync"/>): ค้น token ด้วย hash แล้วยืนยันซ้ำแบบเวลาคงที่ → ลิงก์ใช้ได้ (ไม่หมดอายุ/ไม่ถูกเพิกถอน) →
/// ตรวจไบต์ (<see cref="WhtCertUploadLinkPolicy.Inspect"/> — PDF/JPG/PNG · ≤ 10 MB · PDF ไม่มีสคริปต์/ไฟล์ฝัง) → ไฟล์เดิมเคยส่ง ⇒ ตอบ "ได้รับแล้ว" (ไม่เก็บซ้ำ ·
/// ไม่สแกนซ้ำ) → เก็บไฟล์ผ่านที่เก็บกลาง (ถัง "ภาษีถูกหัก" ก่อนบันทึก) → OCR เดิม → <b>ประตูเดียว</b> <see cref="IWhtCertIntakeService.SubmitFromScanAsync"/>
/// (Channel = UploadLink · ด่าน/ตรวจของจริง/นักเรียน AI เดียวกับทุกช่องทาง) · สแกนล้ม ⇒ เข้าประตูเดียวแบบว่าง (ผู้ขายตรวจจากไฟล์ — ไม่ทิ้งไฟล์ลูกค้าเงียบ)</para>
///
/// <para>ผลที่คืนให้ลูกค้า = ข้อความคงที่ตามสถานะ (ไม่สะท้อนเหตุผลจับคู่/ชื่อลูกค้ารายอื่น) · ทุก query ผูก CompanyId · ออก/เพิกถอนลิงก์ลง audit chain</para>
/// </summary>
public class WhtCertUploadLinkService : IWhtCertUploadLinkService
{
    private readonly AccountingDbContext _db;
    private readonly IConfiguration? _config;
    private readonly IWhtCertIntakeService _intake;
    private readonly IOcrService _ocr;
    private readonly IFileAttachmentService _files;
    private readonly ILogger<WhtCertUploadLinkService> _logger;

    public WhtCertUploadLinkService(AccountingDbContext db, IWhtCertIntakeService intake, IOcrService ocr, IFileAttachmentService files,
        ILogger<WhtCertUploadLinkService> logger, IConfiguration? config = null)
    {
        _db = db; _intake = intake; _ocr = ocr; _files = files; _logger = logger; _config = config;
    }

    /// <summary>สถานะแถวเครดิตที่ "ยังต้องการใบจากลูกค้า" บนหน้าอัปโหลด/อีเมลทวง (Submitted แสดงว่า "ส่งแล้ว รอตรวจ" แต่ไม่ทวง)</summary>
    internal static bool AwaitsCustomer(WhtCertIntakeStatus.State s)
        => s is WhtCertIntakeStatus.State.AwaitingCert or WhtCertIntakeStatus.State.Rejected
            or WhtCertIntakeStatus.State.PartiallyReceived or WhtCertIntakeStatus.State.EWhtAwaitingEvidence;

    // ═══════════ ออกลิงก์ (ตัวเดียว — ปุ่มบนหน้า · อีเมลใบเสร็จ · อีเมลทวง) ═══════════

    /// <summary>ออกลิงก์ใหม่หนึ่งแถว + audit chain แล้ว<b>บันทึกทันที</b> (URL ที่ส่งออกไปต้องมีแถวรองรับเสมอ) · คืน URL (null = ไม่มีที่อยู่เว็บของระบบ)</summary>
    internal static async Task<WhtCertUploadLinkIssued> IssueCoreAsync(AccountingDbContext db, IConfiguration? config, Guid companyId, Guid contactId,
        Guid? documentId, string source, Guid? userId, string actor, CancellationToken ct)
    {
        var token = WhtCertUploadLinkPolicy.NewToken();
        var row = AddLinkRow(db, companyId, contactId, documentId, WhtCertUploadLinkPolicy.TokenHash(token), source, userId, actor, null);
        await db.SaveChangesAsync(ct);
        var origin = await SystemBaseUrl.ResolveAsync(db, config, ct);
        return origin == null
            ? new WhtCertUploadLinkIssued(row.Id, null, row.ExpiresAt, "ยังไม่ได้ตั้งที่อยู่เว็บของระบบ — สร้าง URL ลิงก์อัปโหลดไม่ได้ (ผู้ดูแลระบบตั้งได้ที่ค่าตั้งระบบ)")
            : new WhtCertUploadLinkIssued(row.Id, WhtCertUploadLinkPolicy.Url(origin, companyId, token), row.ExpiresAt, null);
    }

    /// <summary>แถวลิงก์ + audit chain (ไม่บันทึก — ผู้เรียกบันทึกพร้อมงานของตัวเอง) · ผู้สร้างแถวลิงก์ตัวเดียว (ปุ่ม · อีเมลใบเสร็จ · อีเมลทวง)</summary>
    internal static WhtCertUploadLink AddLinkRow(AccountingDbContext db, Guid companyId, Guid contactId, Guid? documentId, string tokenHash,
        string source, Guid? userId, string actor, Guid? emailQueueId, Guid? lineQueueId = null)
    {
        var now = DateTime.UtcNow;
        var row = new WhtCertUploadLink
        {
            CompanyId = companyId, ContactId = contactId, DocumentId = documentId, TokenHash = tokenHash,
            ExpiresAt = WhtCertUploadLinkPolicy.ExpiresAt(now), Source = source, CreatedByUserId = userId, CreatedBy = actor,
            EmailQueueId = emailQueueId, LineQueueId = lineQueueId,
        };
        db.WhtCertUploadLinks.Add(row);
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, UserEmail = actor, Action = AuditAction.Create,
            EntityType = nameof(WhtCertUploadLink), EntityId = row.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "wht-upload-link-issued", contactId, documentId, source, row.ExpiresAt,
                emailQueueId, lineQueueId, ruleCode = WhtCertUploadLinkPolicy.RuleCode }),
            Timestamp = now,
        });
        return row;
    }

    /// <summary>
    /// ฝ่ายค้าน WP-10 M3: URL ลิงก์อัปโหลดของอีเมลทวงหนึ่งฉบับ ณ ตอนส่ง — <b>อ่านอย่างเดียว</b> (ไม่เขียนฐานกลางทางส่ง): token คำนวณซ้ำจากกุญแจ + id คิว
    /// (<see cref="WhtCertUploadLinkPolicy.TokenForQueue"/>) แล้วยืนยันว่ามีแถวลิงก์ของคิวนี้ (สร้างพร้อมแถวคิว) และ hash ตรง · ลองส่งใหม่ = URL เดิม ·
    /// คืน (null, เหตุ) เมื่อไม่มีแถว/ไม่มีที่อยู่เว็บ ⇒ ผู้ส่งคิวไม่ส่ง (ไม่ส่งลิงก์เสีย)
    /// </summary>
    internal static async Task<(string? Url, string? Problem)> ReminderUrlAsync(AccountingDbContext db, IConfiguration? config, Guid companyId,
        Guid emailQueueId, CancellationToken ct)
    {
        var token = WhtCertUploadLinkPolicy.TokenForQueue(WhtCertUploadLinkPolicy.DeriveQueueKey(config?["Jwt:Secret"]), companyId, emailQueueId);
        // ฝ่ายค้าน 259 L6: ลิงก์ที่ถูกเพิกถอน/หมดอายุ ⇒ ไม่ส่ง (เดิมส่งปุ่มที่ใช้ไม่ได้)
        var now = DateTime.UtcNow;
        var stored = await db.WhtCertUploadLinks.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.EmailQueueId == emailQueueId && l.RevokedAt == null && l.ExpiresAt > now)
            .Select(l => l.TokenHash).FirstOrDefaultAsync(ct);
        if (stored == null || !WhtCertUploadLinkPolicy.TokenMatches(token, stored))
            return (null, WhtCertUploadLinkPolicy.ReminderLinkUnusable);
        var origin = await SystemBaseUrl.ResolveAsync(db, config, ct);
        return origin == null
            ? (null, "ยังไม่ได้ตั้งที่อยู่เว็บของระบบ — สร้าง URL ลิงก์อัปโหลดไม่ได้ (ผู้ดูแลระบบตั้งได้ที่ค่าตั้งระบบ)")
            : (WhtCertUploadLinkPolicy.Url(origin, companyId, token), null);
    }

    /// <summary>รอบ 259 (258-3-6): URL ลิงก์อัปโหลดของการ์ด LINE ทวงหนึ่งใบ ณ ตอนส่ง — อ่านอย่างเดียว (คู่ของ <see cref="ReminderUrlAsync"/>) ·
    /// ไม่มีแถว/ความลับเปลี่ยน/ไม่มีที่อยู่เว็บ ⇒ (null, เหตุ) ⇒ ผู้ส่งคิวไม่ส่ง (ไม่ส่งลิงก์เสีย)</summary>
    internal static async Task<(string? Url, string? Problem)> LineReminderUrlAsync(AccountingDbContext db, IConfiguration? config, Guid companyId,
        Guid lineQueueId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config?["Jwt:Secret"]))
            return (null, "ไม่ได้ตั้งความลับของเซิร์ฟเวอร์ — สร้างลิงก์อัปโหลดของการ์ด LINE ไม่ได้");
        var token = WhtCertUploadLinkPolicy.TokenForLineQueue(WhtCertUploadLinkPolicy.DeriveQueueKey(config["Jwt:Secret"]), companyId, lineQueueId);
        var now = DateTime.UtcNow;
        var stored = await db.WhtCertUploadLinks.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.LineQueueId == lineQueueId && l.RevokedAt == null && l.ExpiresAt > now)
            .Select(l => l.TokenHash).FirstOrDefaultAsync(ct);
        if (stored == null || !WhtCertUploadLinkPolicy.TokenMatches(token, stored))
            return (null, WhtCertUploadLinkPolicy.ReminderLinkUnusable);
        var origin = await SystemBaseUrl.ResolveAsync(db, config, ct);
        return origin == null
            ? (null, "ยังไม่ได้ตั้งที่อยู่เว็บของระบบ — สร้าง URL ลิงก์อัปโหลดไม่ได้ (ผู้ดูแลระบบตั้งได้ที่ค่าตั้งระบบ)")
            : (WhtCertUploadLinkPolicy.Url(origin, companyId, token), null);
    }

    public async Task<WhtCertUploadLinkIssued> IssueAsync(Guid companyId, Guid? contactId, Guid? documentId, Guid? userId, string actor,
        CancellationToken ct = default)
    {
        Guid cid;
        if (documentId is Guid docId)
        {
            var docContact = await _db.Documents.AsNoTracking().Where(d => d.Id == docId && d.CompanyId == companyId && !d.IsDeleted)
                .Select(d => (Guid?)d.ContactId).FirstOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("ไม่พบเอกสารในบริษัทนี้");
            if (contactId is Guid given && given != docContact)
                throw new BusinessRuleException("ผู้ติดต่อที่ระบุไม่ใช่ผู้ติดต่อของเอกสารนี้ — ออกลิงก์จากหน้าเอกสาร หรือเลือกผู้ติดต่อให้ตรง", WhtCertUploadLinkPolicy.RuleCode, 400);
            cid = docContact;
        }
        else cid = contactId ?? throw new BusinessRuleException("ระบุผู้ติดต่อหรือเอกสารที่จะออกลิงก์อัปโหลด", WhtCertUploadLinkPolicy.RuleCode, 400);
        if (!await _db.Contacts.AsNoTracking().AnyAsync(c => c.Id == cid && c.CompanyId == companyId && !c.IsDeleted, ct))
            throw new KeyNotFoundException("ไม่พบผู้ติดต่อในบริษัทนี้");
        return await IssueCoreAsync(_db, _config, companyId, cid, documentId, "manual", userId, actor, ct);
    }

    public async Task<List<WhtCertUploadLinkDto>> ListAsync(Guid companyId, Guid contactId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.WhtCertUploadLinks.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.ContactId == contactId)
            .OrderByDescending(l => l.CreatedAt).Take(50).ToListAsync(ct);
        return rows.Select(l => new WhtCertUploadLinkDto(l.Id, l.ContactId, l.DocumentId, l.Source, l.CreatedAt, l.ExpiresAt, l.RevokedAt,
            l.LastUsedAt, l.UploadCount, WhtCertUploadLinkPolicy.Usable(l.ExpiresAt, l.RevokedAt, now))).ToList();
    }

    public async Task RevokeAsync(Guid companyId, Guid linkId, string actor, CancellationToken ct = default)
    {
        var row = await _db.WhtCertUploadLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบลิงก์อัปโหลด");
        if (row.RevokedAt != null) return;   // กดซ้ำ = ผลเดิม
        row.RevokedAt = DateTime.UtcNow;
        row.UpdatedAt = row.RevokedAt;
        row.UpdatedBy = actor;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = actor, Action = AuditAction.Update,
            EntityType = nameof(WhtCertUploadLink), EntityId = row.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "wht-upload-link-revoked", row.ContactId, ruleCode = WhtCertUploadLinkPolicy.RuleCode }),
            Timestamp = row.RevokedAt.Value,
        });
        await _db.SaveChangesAsync(ct);
    }

    // ═══════════ ฝั่งลูกค้า (ไม่ล็อกอิน) ═══════════

    /// <summary>ค้นด้วย hash แล้วยืนยันซ้ำแบบเวลาคงที่ · รูปผิด/ไม่พบ ⇒ null (ข้อความเดียวกับ token ผิด)</summary>
    private async Task<WhtCertUploadLink?> FindAsync(Guid companyId, string token, bool track, CancellationToken ct)
    {
        if (!WhtCertUploadLinkPolicy.LooksValid(token)) return null;
        var hash = WhtCertUploadLinkPolicy.TokenHash(token);
        var q = _db.WhtCertUploadLinks.Where(l => l.CompanyId == companyId && l.TokenHash == hash);
        var row = await (track ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct);
        return row != null && WhtCertUploadLinkPolicy.TokenMatches(token, row.TokenHash) ? row : null;
    }

    /// <summary>รายการรอใบของลูกค้าที่ถือลิงก์ (ตัวโหลด/ตัวตัดสินเดียวกับป้าย 50 ทวิ) — เฉพาะที่ผูกเอกสาร</summary>
    private Task<List<WhtUploadItemDto>> ItemsAsync(Guid companyId, WhtCertUploadLink link, CancellationToken ct)
        => ItemsForContactAsync(_db, companyId, link.ContactId, link.DocumentId, ct);

    /// <summary>รายการรอใบของผู้ติดต่อหนึ่งราย — ตัวโหลดเดียวของหน้าอัปโหลด 50 ทวิ (ลิงก์อัปโหลด) และการ์ด 50 ทวิ บนหน้าเอกสาร
    /// (รอบ 267 P4 · <c>DocumentPayLinkService.AfterPayAsync</c>) · <paramref name="preselectDocumentId"/> = เอกสารที่ติ๊กไว้ให้ (null = ทุกใบ)</summary>
    internal static async Task<List<WhtUploadItemDto>> ItemsForContactAsync(AccountingDbContext db, Guid companyId, Guid contactId,
        Guid? preselectDocumentId, CancellationToken ct)
    {
        var rows = await WhtCertTracking.LoadAsync(db, companyId, DateTime.UtcNow, contactId: contactId,
            statuses: WhtCertTracking.FollowUpStatuses, ct: ct);
        return rows
            .Where(r => r.DocumentId != null && WhtCertIntakeStatus.NeedsFollowUp(r.Verdict.State))
            .GroupBy(r => r.DocumentId!.Value)
            .Select(g => new WhtUploadItemDto(g.Key, g.First().DocumentNumber, g.Min(x => x.WithheldSince),
                Math.Round(g.Sum(x => WhtCreditableAmount.Missing(x.Status, x.WhtAmount, x.ReceivedAmount)), 2, MidpointRounding.AwayFromZero),
                g.Any(x => AwaitsCustomer(x.Verdict.State)) ? "รอหนังสือรับรอง" : "ส่งแล้ว — รอผู้ขายตรวจ",
                !g.Any(x => AwaitsCustomer(x.Verdict.State)),
                preselectDocumentId == null || preselectDocumentId == g.Key))
            .OrderBy(i => i.WithheldSince ?? DateTime.MaxValue).ToList();
    }

    public async Task<WhtUploadPublicView?> PublicViewAsync(Guid companyId, string token, CancellationToken ct = default)
    {
        var link = await FindAsync(companyId, token, track: false, ct);
        if (link == null) return null;
        if (!WhtCertUploadLinkPolicy.Usable(link.ExpiresAt, link.RevokedAt, DateTime.UtcNow))
            return new WhtUploadPublicView(false, WhtCertUploadLinkPolicy.UnusableMessage, null, null, null, null, null, Array.Empty<WhtUploadItemDto>(), false);
        var co = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.TaxId, c.BranchCode, c.Address }).FirstOrDefaultAsync(ct);
        var contactName = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == link.ContactId && c.CompanyId == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var items = await ItemsAsync(companyId, link, ct);
        return new WhtUploadPublicView(true, null, co?.Name, co?.TaxId, TaxBranchCode.Label(co?.BranchCode), co?.Address, contactName, items,
            AllReceived: items.Count == 0);
    }

    public async Task<WhtUploadPublicResult?> PublicUploadAsync(Guid companyId, string token, byte[] content, IReadOnlyList<Guid>? documentIds,
        string? senderName, CancellationToken ct = default)
    {
        var link = await FindAsync(companyId, token, track: false, ct);
        if (link == null) return null;
        if (!WhtCertUploadLinkPolicy.Usable(link.ExpiresAt, link.RevokedAt, DateTime.UtcNow))
            throw new BusinessRuleException(WhtCertUploadLinkPolicy.UnusableMessage, WhtCertUploadLinkPolicy.RuleCode, 410);

        var r = await UploadCoreAsync(companyId, link.ContactId, link.DocumentId, content, documentIds, senderName, ct);
        await _db.WhtCertUploadLinks.Where(l => l.Id == link.Id && l.CompanyId == companyId)
            .ExecuteUpdateAsync(x => x.SetProperty(l => l.LastUsedAt, (DateTime?)DateTime.UtcNow)
                .SetProperty(l => l.UploadCount, l => l.UploadCount + 1), ct);
        return r;
    }

    /// <summary>
    /// รอบ 267 P4 (267-1-48): อัปโหลด 50 ทวิ <b>จากหน้าเอกสารของลูกค้า</b> — สิทธิ์พิสูจน์แล้วโดยผู้เรียก (token ของใบ ·
    /// <c>DocumentPayLinkService.WhtCertTargetAsync</c>) · เดินแกนเดียวกับลิงก์อัปโหลดเดิม (<see cref="UploadCoreAsync"/> — ตรวจไบต์ · กันซ้ำ ·
    /// ที่เก็บกลาง · OCR · ประตูเดียว <c>WhtCertIntakeService</c> ช่องทาง UploadLink) · ไม่มีระบบอัปโหลดที่สอง · ใบที่ไม่ได้รอหนังสือรับรอง = ปฏิเสธ
    /// </summary>
    public async Task<WhtUploadPublicResult> UploadForDocumentAsync(Guid companyId, Guid contactId, Guid documentId, byte[] content, string? senderName,
        CancellationToken ct = default)
    {
        var open = (await ItemsForContactAsync(_db, companyId, contactId, documentId, ct)).FirstOrDefault(i => i.DocumentId == documentId);
        if (open == null || open.Submitted)
            throw new BusinessRuleException(open == null
                ? "เอกสารนี้ไม่ได้รอหนังสือรับรองการหักภาษี ณ ที่จ่าย — ถ้ามีข้อสงสัย กรุณาติดต่อผู้ขาย"
                : "ได้รับหนังสือรับรองของเอกสารนี้แล้ว — รอผู้ขายตรวจ (ไม่ต้องส่งซ้ำ)", WhtCertUploadLinkPolicy.RuleCode, 409);
        return await UploadCoreAsync(companyId, contactId, documentId, content, new[] { documentId }, senderName, ct);
    }

    /// <summary>แกนรับไฟล์ 50 ทวิ จากลูกค้า (ไม่ล็อกอิน) — ตัวเดียวของลิงก์อัปโหลดและหน้าเอกสาร · <paramref name="scopeDocumentId"/> = เอกสารที่ลิงก์ผูก
    /// (null = ทุกใบของผู้ติดต่อ) · <paramref name="documentIds"/> กรองให้อยู่ในชุดรอใบของผู้ติดต่อนี้เท่านั้น</summary>
    private async Task<WhtUploadPublicResult> UploadCoreAsync(Guid companyId, Guid contactId, Guid? scopeDocumentId, byte[] content,
        IReadOnlyList<Guid>? documentIds, string? senderName, CancellationToken ct)
    {
        // ไบต์ตัดสิน (MagicBytes) — ก่อนเก็บ/สแกนอะไรทั้งนั้น
        var verdict = WhtCertUploadLinkPolicy.Inspect(content);
        if (verdict.Kind is not { } kind)
            throw new BusinessRuleException(verdict.Problem ?? WhtCertUploadLinkPolicy.RejectTypeMessage, WhtCertUploadLinkPolicy.RuleCode, 400);

        // ไฟล์เดิมเคยเข้ากล่องรับแล้ว ⇒ ไม่เก็บซ้ำ ไม่สแกนซ้ำ (ตัวกันซ้ำเดียวกับประตูเดียว)
        var sha = WhtCertUploadLinkPolicy.Sha256Hex(content);
        if (await _intake.FindDuplicateShaAsync(companyId, sha, ct) is { } dup)
            return new WhtUploadPublicResult(true, true, dup.Status, "ได้รับไฟล์นี้แล้ว — ไม่ต้องส่งซ้ำ ขอบคุณค่ะ");

        var name = WhtCertUploadLinkPolicy.SanitizeSenderName(senderName);
        var allowed = (await ItemsForContactAsync(_db, companyId, contactId, scopeDocumentId, ct)).Select(i => i.DocumentId).ToList();
        var hints = WhtCertUploadLinkPolicy.FilterHints(documentIds, allowed);

        // ที่เก็บกลาง (ห้ามแตะดิสก์ตรง) · ถัง "ภาษีถูกหัก" ก่อนบันทึก — ประตูเดียวผูกไฟล์กับแถวเครดิตตอนยืนยัน (AdoptBucketFileAsync)
        var saved = await _files.UploadAnonymousBytesAsync(companyId, "WhtCredit", Guid.Empty, $"50ทวิ-จากลูกค้า{kind.Extension}", kind.ContentType, content);

        WhtCertSubmitResult r;
        Guid? scanId = null;
        try { scanId = (await _ocr.ScanAsync(companyId, saved.Id)).Id; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // สแกนล้ม (ผู้ให้บริการ OCR ล่ม/ไฟล์อ่านไม่ได้) — ไม่ทิ้งไฟล์ลูกค้าเงียบ: เข้าประตูเดียวแบบไม่มีค่าที่อ่านได้ ⇒ รอผู้ขายตรวจจากไฟล์
            _logger.LogWarning(ex, "สแกนใบ 50 ทวิ จากลิงก์อัปโหลดไม่สำเร็จ ไฟล์ {File} บริษัท {Company} — เข้ากล่องรับแบบรอตรวจ", saved.Id, companyId);
        }
        try
        {
            if (scanId is Guid sid)
                r = await _intake.SubmitFromScanAsync(companyId, sid, WhtCertIntakeChannel.UploadLink, hints,
                    WhtCertUploadLinkPolicy.SubmittedBy(name), null, ct, fileSha256: sha, expectedPayerContactId: contactId);
            else
                // รอบ 257 ชุด 2 (B-L3): ตัวเดียวกับอัปโหลดเอง (ประตูเดียว · ไม่มีค่าที่อ่านได้ · รอคนตรวจ)
                r = await _intake.SubmitUnreadableAsync(companyId, WhtCertIntakeChannel.UploadLink, saved.Id, sha, hints,
                    WhtCertUploadLinkPolicy.SubmittedBy(name), null, ct, expectedPayerContactId: contactId);
        }
        catch (BusinessRuleException ex) when (ex.StatusCode == 409)
        {
            // ใบเดียวกัน (เลขที่ + ผู้จ่าย) อยู่ในทะเบียนแล้ว — ลูกค้าส่งซ้ำ ⇒ ตอบ "ได้รับแล้ว" (ไม่สะท้อนยอด/เหตุภายใน) · ไฟล์สำเนาไม่เก็บไว้
            try { await _files.DeleteAsync(companyId, saved.Id); }
            catch (Exception delEx) when (delEx is not OperationCanceledException)
            {
                _logger.LogError(delEx, "ลบไฟล์สำเนา 50 ทวิ {File} ที่ลูกค้าส่งซ้ำไม่ได้", saved.Id);
            }
            return new WhtUploadPublicResult(true, true, "Duplicate", PublicMessage(new WhtCertSubmitResult(Guid.Empty, "Duplicate", false, true, ""), null));
        }
        // รอบ 257 ชุด 2 (B-M2): ประตูเดียวคืน "ใบเดิม" (ไฟล์/ใบเดียวกันของผู้จ่ายรายนี้) ⇒ ไฟล์ที่เพิ่งเก็บไม่มีแถวใดอ้าง — ลบ (เดิมค้างเป็นไฟล์กำพร้าในถัง)
        if (r.Duplicate)
            await DeleteOrphanAsync(companyId, saved.Id);

        var ourName = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        return new WhtUploadPublicResult(true, r.Duplicate, r.Status, PublicMessage(r, ourName));
    }

    private async Task DeleteOrphanAsync(Guid companyId, Guid fileId)
    {
        try { await _files.DeleteAsync(companyId, fileId); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ลบไฟล์สำเนา 50 ทวิ {File} ที่ลูกค้าส่งซ้ำไม่ได้", fileId);
        }
    }

    /// <summary>ข้อความถึงลูกค้าตามสถานะ (คงที่ — ไม่สะท้อนเหตุผลภายใน/ชื่อรายอื่น) · ตีกลับ = ใบไม่ได้ออกในนามเรา (เหตุเดียวที่ประตูเดียวตีกลับทันที)</summary>
    internal static string PublicMessage(WhtCertSubmitResult r, string? ourName)
    {
        if (r.Duplicate) return "ได้รับไฟล์/หนังสือรับรองนี้แล้ว — ไม่ต้องส่งซ้ำ ขอบคุณค่ะ";
        return r.Status switch
        {
            nameof(WhtCertIntakeRecordStatus.Confirmed) => "ได้รับหนังสือรับรองแล้ว — ตรงกับรายการ ขอบคุณค่ะ",
            nameof(WhtCertIntakeRecordStatus.Rejected) =>
                $"ใบนี้ไม่ได้ออกในนาม {ourName ?? "ผู้ขาย"} — โปรดตรวจชื่อและเลขผู้เสียภาษีของผู้ถูกหักบนหนังสือรับรอง แล้วส่งใบที่ถูกต้องอีกครั้ง",
            _ => "ได้รับแล้ว — ผู้ขายจะตรวจและยืนยัน (ไม่ต้องส่งซ้ำ)",
        };
    }
}
