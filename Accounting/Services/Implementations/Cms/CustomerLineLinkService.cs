using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Accounting.Services.Implementations.Cms;

/// <summary>ดู <see cref="ICustomerLineLinkService"/> — รอบ 211 · คำตัดสินข้อ 170 · <c>erp-review/2026-10-30/LINE_CUSTOMER_NOTIFY_DESIGN.md</c></summary>
public class CustomerLineLinkService : ICustomerLineLinkService
{
    private readonly AccountingDbContext _db;
    private readonly IConfiguration _config;
    private readonly ISecretProtector _secrets;
    private readonly IHttpClientFactory _http;
    private readonly IChatRateLimiter _limiter;
    private readonly ILogger<CustomerLineLinkService> _logger;
    /// <summary>รอบ 212 D: ค่าตั้ง OA กลาง — หน้าแอดมินก่อน · config Line:* fallback (ผู้อ่านตัวเดียว)</summary>
    private readonly INextAccLineConfig _nextAcc;

    /// <summary>ความพยายามไถ่รหัสต่อ LINE user (DB-backed · หลาย instance) — เท่ากับเพดานผูกบัญชี ERP</summary>
    private const int RedeemPerMinute = 3;
    private const int RedeemPerDay = 10;

    public CustomerLineLinkService(AccountingDbContext db, IConfiguration config, ISecretProtector secrets,
        IHttpClientFactory http, IChatRateLimiter limiter, ILogger<CustomerLineLinkService> logger, INextAccLineConfig nextAcc)
    {
        _db = db; _config = config; _secrets = secrets; _http = http; _limiter = limiter; _logger = logger; _nextAcc = nextAcc;
    }

    // ───────────── ช่องทาง ─────────────

    public async Task<CustomerLineChannel> ResolveChannelAsync(Guid companyId, CancellationToken ct = default)
    {
        var s = await _db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new { x.LineEnabled, x.LineChannelAccessToken, x.LineChannelSecret, x.LineOaBasicId, x.LineOaDestination })
            .FirstOrDefaultAsync(ct);
        var nx = await _nextAcc.GetAsync(ct);
        return CustomerNotifyChannel.ResolveLine(
            s?.LineEnabled == true, s != null && _secrets.IsUsable(s.LineChannelAccessToken), s != null && _secrets.IsUsable(s.LineChannelSecret),
            s?.LineOaBasicId, s?.LineOaDestination,
            nx.HasToken, nx.HasSecret, nx.BasicId, nx.BotUserId);
    }

    public async Task<CustomerLineChannelInfo> GetChannelInfoAsync(Guid companyId, CancellationToken ct = default)
    {
        var ch = await ResolveChannelAsync(companyId, ct);
        var groups = await _db.CustomerLineLinks.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.UnlinkedAt == null && l.BlockedAt == null)
            .GroupBy(l => new { l.OaScope, l.OaDestination })
            .Select(g => new { g.Key.OaScope, g.Key.OaDestination, Count = g.Count() })
            .ToListAsync(ct);
        int active = 0, stale = 0;
        foreach (var g in groups)
        {
            if (CustomerNotifyChannel.LinkUsable(g.OaScope, g.OaDestination, ch.Scope, ch.CurrentDestination)) active += g.Count;
            else stale += g.Count;
        }
        var baseUrl = (_config["App:BaseUrl"] ?? "").TrimEnd('/');
        var webhook = string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl}/api/line/webhook/c/{companyId}";
        return new CustomerLineChannelInfo(ch.Scope, ch.BasicId, ch.CanLink, ch.Problem, webhook, active, stale);
    }

    // ───────────── เป้าหมาย ─────────────

    public async Task<CustomerLineRecordRef?> ResolveRecordByTokenAsync(Guid companyId, Guid siteId, string kind, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        return kind switch
        {
            CustomerLineLinkRule.KindBooking => await _db.SiteBookings.AsNoTracking()
                .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.PublicToken == token)
                .Select(b => new CustomerLineRecordRef(b.Id, b.CustomerId)).FirstOrDefaultAsync(ct),
            CustomerLineLinkRule.KindOrder => await _db.SiteOrders.AsNoTracking()
                .Where(o => o.CompanyId == companyId && o.SiteId == siteId && o.PublicToken == token)
                .Select(o => new CustomerLineRecordRef(o.Id, o.CustomerId)).FirstOrDefaultAsync(ct),
            _ => null,
        };
    }

    private sealed record TargetFacts(string SiteName, string? Email, string? Phone);

    /// <summary>ข้อเท็จจริงของเป้าหมาย (ชื่อเว็บ + อีเมล/เบอร์สำหรับหลักฐานยินยอม) · ไม่พบในบริษัท+เว็บนี้ ⇒ null</summary>
    private async Task<TargetFacts?> LoadTargetAsync(Guid companyId, Guid siteId, string kind, Guid targetId, CancellationToken ct)
    {
        var site = await _db.Sites.AsNoTracking().Where(s => s.Id == siteId && s.CompanyId == companyId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        if (site == null) return null;
        switch (kind)
        {
            case CustomerLineLinkRule.KindMember:
                var c = await _db.SiteCustomers.AsNoTracking()
                    .Where(x => x.Id == targetId && x.CompanyId == companyId && x.SiteId == siteId && x.IsActive)
                    .Select(x => new { x.Email, x.Phone }).FirstOrDefaultAsync(ct);
                return c == null ? null : new TargetFacts(site, c.Email, c.Phone);
            case CustomerLineLinkRule.KindBooking:
                var b = await _db.SiteBookings.AsNoTracking()
                    .Where(x => x.Id == targetId && x.CompanyId == companyId && x.SiteId == siteId)
                    .Select(x => new { Email = x.GuestEmail ?? (x.Customer != null ? x.Customer.Email : null), x.GuestPhone }).FirstOrDefaultAsync(ct);
                return b == null ? null : new TargetFacts(site, b.Email, b.GuestPhone);
            case CustomerLineLinkRule.KindOrder:
                var o = await _db.SiteOrders.AsNoTracking()
                    .Where(x => x.Id == targetId && x.CompanyId == companyId && x.SiteId == siteId)
                    .Select(x => new { Email = x.ShippingEmail ?? (x.Customer != null ? x.Customer.Email : null), x.ShippingPhone }).FirstOrDefaultAsync(ct);
                return o == null ? null : new TargetFacts(site, o.Email, o.ShippingPhone);
            default:
                return null;
        }
    }

    public async Task<CustomerLineStatusResponse> GetStatusAsync(Guid companyId, Guid siteId, string kind, Guid targetId, Guid? memberId, CancellationToken ct = default)
    {
        var ch = await ResolveChannelAsync(companyId, ct);
        var siteName = await _db.Sites.AsNoTracking().Where(s => s.Id == siteId && s.CompanyId == companyId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        var links = await _db.CustomerLineLinks.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.SiteId == siteId && l.UnlinkedAt == null
                && ((l.TargetKind == kind && l.TargetId == targetId)
                    || (memberId != null && l.TargetKind == CustomerLineLinkRule.KindMember && l.TargetId == memberId)))
            .OrderByDescending(l => l.LinkedAt).ToListAsync(ct);
        // ระดับระเบียนชนะระดับสมาชิก (ตัวเดียวกับที่ใช้ตอนส่ง)
        var link = links.FirstOrDefault(l => l.TargetKind == kind && l.TargetId == targetId) ?? links.FirstOrDefault();
        var usable = link != null && CustomerNotifyChannel.LinkUsable(link.OaScope, link.OaDestination, ch.Scope, ch.CurrentDestination);
        return new CustomerLineStatusResponse(
            Available: ch.CanLink,
            UnavailableReason: ch.CanLink ? null : ch.Problem,
            Linked: usable && link!.BlockedAt == null,
            Blocked: usable && link!.BlockedAt != null,
            NeedsRelink: link != null && !usable,
            MaskedLineUserId: link == null ? null : CustomerLineLinkRule.Mask(link.LineUserId),
            LinkedAt: link?.LinkedAt,
            OaBasicId: ch.BasicId,
            AddFriendUrl: CustomerLineLinkRule.AddFriendUrl(ch.BasicId),
            ConsentText: CustomerLineLinkRule.ConsentText(siteName),
            LinkedVia: link?.TargetKind);
    }

    public async Task<CustomerLineStartResponse> StartAsync(Guid companyId, Guid siteId, string kind, Guid targetId, CancellationToken ct = default)
    {
        if (!CustomerLineLinkRule.IsKnownKind(kind)) throw new BusinessRuleException("ชนิดการเชื่อมต่อไม่ถูกต้อง", CustomerLineLinkRule.RuleCode);
        var ch = await ResolveChannelAsync(companyId, ct);
        if (!ch.CanLink) throw new BusinessRuleException(ch.Problem ?? "ร้านยังไม่เปิดรับแจ้งเตือนทาง LINE", CustomerLineLinkRule.RuleCode);
        if (await LoadTargetAsync(companyId, siteId, kind, targetId, ct) == null) throw new KeyNotFoundException("ไม่พบรายการ");

        var now = DateTime.UtcNow;
        var code = CustomerLineLinkRule.NewCode();
        var expires = now.Add(CustomerLineLinkRule.CodeLifetime);
        // รอบ 211 ฝ่ายค้าน ข้อ 7: มีรหัสค้างได้ทีละรหัสต่อเป้าหมาย — ล็อกต่อเป้าหมายในธุรกรรมเดียวกับ "เผารหัสเดิม + ออกรหัสใหม่"
        // (สองคำขอพร้อมกันจาก instance ต่างกันจะเข้าคิว ไม่ได้รหัสใช้ได้สองตัว)
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.CustomerLineCode, $"{kind}:{targetId:N}") }, ct);
            await CustomerLineLinkClosure.BurnPendingCodesAsync(_db, companyId, kind, targetId, now, ct);
            _db.CustomerLineLinkCodes.Add(new CustomerLineLinkCode
            {
                CompanyId = companyId, SiteId = siteId, TargetKind = kind, TargetId = targetId,
                CodeHash = CustomerLineLinkRule.Hash(code), OaScope = ch.Scope, ExpiresAt = expires, CreatedBy = "storefront",
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var sendUrl = CustomerLineLinkRule.SendCodeUrl(ch.BasicId, code);
        return new CustomerLineStartResponse(code, CustomerLineLinkRule.MessageText(code), CustomerLineLinkRule.AddFriendUrl(ch.BasicId),
            sendUrl, sendUrl == null ? null : QrDataUri(sendUrl), ch.BasicId, expires);
    }

    private static string QrDataUri(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    public async Task<int> UnlinkAsync(Guid companyId, Guid siteId, string kind, Guid targetId, string reason, CancellationToken ct = default)
    {
        var rows = await _db.CustomerLineLinks
            .Where(l => l.CompanyId == companyId && l.SiteId == siteId && l.TargetKind == kind && l.TargetId == targetId && l.UnlinkedAt == null)
            .ToListAsync(ct);
        // เส้นปิดเดียว (ถอนยินยอม · ล้าง userId · audit · เผารหัสค้าง) — ไม่มีลิงก์ก็ยังเผารหัสค้าง
        if (rows.Count == 0)
        {
            await CustomerLineLinkClosure.BurnPendingCodesAsync(_db, companyId, kind, targetId, DateTime.UtcNow, ct);
            return 0;
        }
        await CustomerLineLinkClosure.CloseAsync(_db, companyId, rows, reason, DateTime.UtcNow, ct);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    // ───────────── webhook ─────────────

    public async Task<string?> TryRedeemAsync(string scope, Guid? companyId, string? destination, string lineUserId, string text, CancellationToken ct = default)
    {
        if (!CustomerLineLinkRule.IsLinkAttempt(text) || string.IsNullOrWhiteSpace(lineUserId)) return null;
        if (scope == CustomerNotifyChannel.ScopeCompany && companyId == null) return null;

        var key = "custline:" + lineUserId;
        if (!await _limiter.TryConsumeAsync(key, RedeemPerMinute, RedeemPerDay, ct, failClosed: true))
        {
            await _limiter.RecordStrikeAsync(key, ct);
            _logger.LogWarning("ไถ่รหัสผูก LINE ลูกค้าเกินเพดาน — LINE {Masked}", CustomerLineLinkRule.Mask(lineUserId));
            return CustomerLineLinkRule.Replies.Throttled;
        }
        var code = CustomerLineLinkRule.TryParse(text);
        if (code == null) return CustomerLineLinkRule.Replies.BadFormat;

        // รอบ 211 ฝ่ายค้าน ข้อ 4: OA กลาง — ปลายทาง webhook ต้องตรงกับ Line:BotUserId เมื่อตั้งไว้ (ตั้งผิด = ล้มดัง ไม่เก็บลิงก์ที่จะใช้ไม่ได้)
        if (scope == CustomerNotifyChannel.ScopeNextAcc && !CustomerNotifyChannel.AcceptDestination((await _nextAcc.GetAsync(ct)).BotUserId, destination))
        {
            _logger.LogError("Bot User ID ของ OA กลาง (หน้าแอดมิน/Line:BotUserId) ไม่ตรงกับ destination ของ webhook — ไม่ผูก LINE ลูกค้า (ตรวจค่าตั้ง)");
            return CustomerLineLinkRule.Replies.WrongChannel;
        }

        var hash = CustomerLineLinkRule.Hash(code);
        // OA กลาง: OA เดียวทุกบริษัท ⇒ ค้นด้วย hash (unique) + scope · OA ของบริษัท: ต้องเป็นรหัสของบริษัทเจ้าของ URL เท่านั้น (กฎ M)
        var q = _db.CustomerLineLinkCodes.Where(c => c.CodeHash == hash && c.OaScope == scope);
        if (scope == CustomerNotifyChannel.ScopeCompany) q = q.Where(c => c.CompanyId == companyId!.Value);
        var row = await q.FirstOrDefaultAsync(ct);
        var now = DateTime.UtcNow;
        if (row == null || !CustomerLineLinkRule.IsRedeemable(row.ExpiresAt, row.UsedAt, now)) return CustomerLineLinkRule.Replies.Invalid;

        // ร้านเปลี่ยน OA ระหว่างออกรหัสกับส่งรหัส ⇒ userId นี้ใช้กับ OA ที่ส่งจริงไม่ได้
        var ch = await ResolveChannelAsync(row.CompanyId, ct);
        if (ch.Scope != scope) return CustomerLineLinkRule.Replies.WrongChannel;

        var facts = await LoadTargetAsync(row.CompanyId, row.SiteId, row.TargetKind, row.TargetId, ct);
        if (facts == null) return CustomerLineLinkRule.Replies.Invalid;

        // รอบ 211 ฝ่ายค้าน ข้อ 7: ใช้รหัส + บันทึกลิงก์ในธุรกรรมเดียว — บันทึกล้ม ⇒ rollback รหัสไม่ถูกเผา (ลูกค้าส่งซ้ำได้)
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // ใช้ครั้งเดียว — update แบบมีเงื่อนไข (สอง instance ไถ่พร้อมกัน ได้แถวเดียว)
        var claimed = await _db.CustomerLineLinkCodes.Where(c => c.Id == row.Id && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);
        if (claimed != 1) return CustomerLineLinkRule.Replies.Invalid;

        var prev = await _db.CustomerLineLinks
            .Where(l => l.CompanyId == row.CompanyId && l.TargetKind == row.TargetKind && l.TargetId == row.TargetId && l.UnlinkedAt == null)
            .ToListAsync(ct);
        if (prev.Count > 0) await CustomerLineLinkClosure.CloseAsync(_db, row.CompanyId, prev, "relinked", now, ct);

        // ความยินยอม (PDPA ม.19): ลูกค้ากดปุ่มที่แสดงข้อความยินยอม + ส่งรหัสเอง = การกระทำยืนยันชัดแจ้ง ·
        // ไม่มีอีเมล/เบอร์ ⇒ ตัวระบุ hash ของ LINE userId (ฝ่ายค้าน ข้อ 8 — หลักฐานต้องมีเสมอ)
        var shown = CustomerLineLinkRule.ConsentText(facts.SiteName);
        var reference = $"line:{row.TargetKind}:{row.TargetId:N}";
        var consent = CmsDataConsent.Stage(_db, row.CompanyId, CmsDataConsent.LineNotifyPurpose, shown, facts.Email, facts.Phone, reference, now)
            ?? CmsDataConsent.StageForSubject(_db, row.CompanyId, CmsDataConsent.LineNotifyPurpose, shown,
                CustomerLineLinkRule.ConsentSubject(lineUserId), reference, now);
        var link = new CustomerLineLink
        {
            CompanyId = row.CompanyId, SiteId = row.SiteId, TargetKind = row.TargetKind, TargetId = row.TargetId,
            LineUserId = lineUserId.Trim(), OaScope = scope, OaDestination = string.IsNullOrWhiteSpace(destination) ? null : destination.Trim(),
            LinkedAt = now, ConsentRecordId = consent.Id, CreatedBy = "line-webhook",
        };
        _db.CustomerLineLinks.Add(link);
        row.UsedAt = now;
        row.LinkId = link.Id;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = row.CompanyId, Action = AuditAction.Create, EntityType = nameof(CustomerLineLink), EntityId = link.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                linked = true, row.TargetKind, row.TargetId, scope,
                lineUser = CustomerLineLinkRule.Mask(lineUserId), consent = true, rule = CustomerLineLinkRule.RuleCode,
            }),
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return CustomerLineLinkRule.Replies.Linked(facts.SiteName, row.TargetKind);
    }

    public async Task<int> SetBlockedAsync(string scope, Guid? companyId, string lineUserId, bool blocked, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lineUserId)) return 0;
        if (scope == CustomerNotifyChannel.ScopeCompany && companyId == null) return 0;
        var q = _db.CustomerLineLinks.Where(l => l.LineUserId == lineUserId && l.OaScope == scope && l.UnlinkedAt == null);
        if (scope == CustomerNotifyChannel.ScopeCompany) q = q.Where(l => l.CompanyId == companyId!.Value);
        var now = DateTime.UtcNow;
        return blocked
            ? await q.Where(l => l.BlockedAt == null).ExecuteUpdateAsync(s => s.SetProperty(l => l.BlockedAt, now).SetProperty(l => l.UpdatedAt, now), ct)
            : await q.Where(l => l.BlockedAt != null).ExecuteUpdateAsync(s => s.SetProperty(l => l.BlockedAt, (DateTime?)null).SetProperty(l => l.UpdatedAt, now), ct);
    }

    public async Task LearnDestinationAsync(Guid companyId, string? destination, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destination) || destination.Length > 64) return;
        var d = destination.Trim();
        await _db.CompanySettings.Where(s => s.CompanyId == companyId && (s.LineOaDestination == null || s.LineOaDestination != d))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LineOaDestination, d), ct);
    }

    public async Task<bool> ReplyAsync(string scope, Guid? companyId, string replyToken, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(replyToken) || string.IsNullOrWhiteSpace(text)) return false;
        string? token;
        if (scope == CustomerNotifyChannel.ScopeCompany && companyId is Guid cid)
        {
            var enc = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == cid).Select(s => s.LineChannelAccessToken).FirstOrDefaultAsync(ct);
            token = _secrets.Unprotect(enc);
        }
        else token = (await _nextAcc.GetAsync(ct)).AccessToken;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var content = new StringContent(JsonSerializer.Serialize(new { replyToken, messages = new[] { new { type = "text", text } } }),
                Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("https://api.line.me/v2/bot/message/reply", content, ct);
            if (resp.IsSuccessStatusCode) return true;
            _logger.LogWarning("LINE reply ({Scope}) ตอบ {Status}", scope, (int)resp.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LINE reply ({Scope}) ล้ม", scope);
            return false;
        }
    }

    // ───────────── ฝั่งส่งแจ้งเตือน ─────────────

    public async Task<string?> ResolveLineUserIdAsync(Guid companyId, CustomerLineTarget target, CancellationToken ct = default)
    {
        var memberIds = new List<Guid>();
        if (target.SiteCustomerId is Guid sc) memberIds.Add(sc);
        if (target.ContactId is Guid contactId)
            memberIds.AddRange(await _db.SiteCustomers.AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.ContactId == contactId && c.IsActive).Select(c => c.Id).ToListAsync(ct));
        var recordKind = target.RecordKind;
        var recordId = target.RecordId;
        if (memberIds.Count == 0 && (recordKind == null || recordId == null)) return null;

        var links = await _db.CustomerLineLinks.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.UnlinkedAt == null && l.BlockedAt == null
                && ((recordKind != null && recordId != null && l.TargetKind == recordKind && l.TargetId == recordId)
                    || (l.TargetKind == CustomerLineLinkRule.KindMember && memberIds.Contains(l.TargetId))))
            .OrderByDescending(l => l.LinkedAt)
            .Select(l => new { l.TargetKind, l.LineUserId, l.OaScope, l.OaDestination })
            .ToListAsync(ct);
        if (links.Count == 0) return null;
        var ch = await ResolveChannelAsync(companyId, ct);
        var usable = links.Where(l => CustomerNotifyChannel.LinkUsable(l.OaScope, l.OaDestination, ch.Scope, ch.CurrentDestination)).ToList();
        var pick = usable.FirstOrDefault(l => l.TargetKind != CustomerLineLinkRule.KindMember) ?? usable.FirstOrDefault();
        return pick?.LineUserId;
    }
}
