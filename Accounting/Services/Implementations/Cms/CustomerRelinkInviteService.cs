using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Line;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Cms;

/// <summary>
/// รอบ 212 แพ็ก B — เชิญลูกค้าผูก LINE ใหม่เมื่อร้านเปลี่ยน OA (ดู <see cref="ICustomerRelinkInviteService"/> · ตัวตัดสิน <see cref="CustomerRelinkInvitePlan"/>)
/// <list type="bullet">
/// <item>บันทึกการสลับ: PUT ค่าตั้ง LINE (<c>LineConfigController.Update</c>) + webhook เห็น bot ใหม่ (<see cref="NoteWebhookDestinationAsync"/>) — ไม่ส่งอะไรในคำขอ HTTP</item>
/// <item>ส่ง: <c>CustomerNotifyOutboxJob</c> (JobLock) → <see cref="ProcessPendingAsync"/> → <see cref="IBookingNotifier.SendAsync"/> ทีละคน ·
///   กันส่งซ้ำด้วยคีย์ <see cref="CustomerNotifyKeys.Relink"/> ใน <c>CustomerNotifyLogs</c> (ข้ามเครื่อง/ข้ามรอบ) · นับโควตาเมื่อออกช่องกลาง · ร้านปิดประเภท
///   <see cref="CustomerNotifyEvents.LineRelink"/> ได้ที่ตารางค่าตั้ง</item>
/// <item>ความคืบหน้า (Total/Invited/Failed/Skipped/LastError) นับใหม่จากบันทึกการส่งทุกรอบ ⇒ ตัวเลขบนหน้าตั้งค่าตรงกับของจริงเสมอ · ครบแล้วประทับ <c>ProcessedAt</c></item>
/// </list>
/// </summary>
public class CustomerRelinkInviteService : ICustomerRelinkInviteService
{
    private readonly AccountingDbContext _db;
    private readonly ICustomerLineLinkService _links;
    private readonly IBookingNotifier? _notifier;
    private readonly ICmsShopNotifier? _shop;
    private readonly INextAccLineConfig _nextAcc;   // รอบ 212 D: ค่าตั้ง OA กลางผ่าน resolver เดียว (หน้าแอดมินก่อน · config fallback)
    private readonly IHttpClientFactory? _http;
    private readonly ILogger<CustomerRelinkInviteService> _logger;

    /// <summary>bot ของ OA กลางที่เครื่องนี้ตรวจแล้ว — <b>ตัวลดงานต่อเครื่องเท่านั้น</b> (ไม่ใช่สถานะที่ต้องตรงกันข้ามเครื่อง: เครื่องอื่นตรวจซ้ำได้ ·
    /// ความถูกต้องมาจากคีย์กันซ้ำในฐาน) · กัน query ข้ามบริษัททุกข้อความที่ผู้ใช้ ERP ส่งถึง OA กลาง</summary>
    private static string? _nextAccDestinationChecked;

    public CustomerRelinkInviteService(AccountingDbContext db, ICustomerLineLinkService links, INextAccLineConfig nextAcc,
        ILogger<CustomerRelinkInviteService> logger, IBookingNotifier? notifier = null, ICmsShopNotifier? shop = null, IHttpClientFactory? http = null)
    {
        _db = db; _links = links; _nextAcc = nextAcc; _logger = logger; _notifier = notifier; _shop = shop; _http = http;
    }

    // ───────────── บันทึกการสลับ ─────────────

    public async Task<Guid?> RecordSwitchAsync(Guid companyId, string? fromScope, string? fromDestination, string toScope, string? toDestination,
        string source, string? actor, CancellationToken ct = default)
    {
        if (!CustomerRelinkInvitePlan.IsSwitch(fromScope, fromDestination, toScope, toDestination)) return null;
        var key = CustomerRelinkInvitePlan.DedupeKey(fromScope!, fromDestination, toScope, toDestination);
        // webhook: ลิงก์เก่าค้างอยู่ตลอด (ลูกค้าที่ไม่ผูกใหม่) ⇒ การสลับคู่เดียวกันบันทึกครั้งเดียวตลอดไป (ไม่เชิญซ้ำทุกครั้งที่ webhook มา)
        if (source == CustomerRelinkInvitePlan.SourceWebhook
            && await _db.LineOaSwitchEvents.AsNoTracking().AnyAsync(e => e.CompanyId == companyId && e.DedupeKey == key, ct))
            return null;

        var row = new LineOaSwitchEvent
        {
            CompanyId = companyId,
            FromScope = fromScope,
            FromDestination = Trunc(fromDestination, 64),
            ToScope = toScope,
            ToDestination = Trunc(toDestination, 64),
            DetectedAt = DateTime.UtcNow,
            Source = source,
            DedupeKey = key,
            CreatedBy = actor ?? "system:line-relink",
        };
        _db.LineOaSwitchEvents.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // คำขอซ้อน/อีกเครื่องบันทึกการสลับเดียวกันที่ยังค้างอยู่แล้ว ⇒ ใช้แถวนั้น (unique บางส่วน UX_LineOaSwitchEvents_Pending_Key)
            _db.Entry(row).State = EntityState.Detached;
            return null;
        }
        _logger.LogInformation("บันทึกการเปลี่ยน LINE OA ของบริษัท {CompanyId}: {Key} ({Source}) — จะเชิญลูกค้าผูกใหม่", companyId, key, source);
        return row.Id;
    }

    public async Task<int> NoteWebhookDestinationAsync(string scope, Guid? companyId, string? destination, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destination) || destination.Length > 64) return 0;
        var d = destination.Trim();
        try
        {
            if (scope == CustomerNotifyChannel.ScopeCompany)
            {
                if (companyId is not Guid cid) return 0;
                var ch = await _links.ResolveChannelAsync(cid, ct);
                if (ch.Scope != CustomerNotifyChannel.ScopeCompany) return 0;   // webhook ของ OA ที่บริษัทไม่ได้ใช้ส่ง — ไม่ใช่การสลับ
                var olds = await _db.CustomerLineLinks.AsNoTracking()
                    .Where(l => l.CompanyId == cid && l.UnlinkedAt == null && l.OaScope == CustomerNotifyChannel.ScopeCompany
                        && l.OaDestination != null && l.OaDestination != d)
                    .Select(l => l.OaDestination!).Distinct().ToListAsync(ct);
                var n = 0;
                foreach (var old in olds)
                    if (await RecordSwitchAsync(cid, CustomerNotifyChannel.ScopeCompany, old, CustomerNotifyChannel.ScopeCompany, d,
                            CustomerRelinkInvitePlan.SourceWebhook, null, ct) != null) n++;
                return n;
            }

            // OA กลาง: bot ที่ไม่ตรงกับที่ตั้งไว้ = ค่าตั้งผิด ไม่ใช่การสลับ (ตัวตัดสินเดียวกับการผูก)
            if (!CustomerNotifyChannel.AcceptDestination((await _nextAcc.GetAsync(ct)).BotUserId, d)) return 0;
            if (string.Equals(Volatile.Read(ref _nextAccDestinationChecked), d, StringComparison.Ordinal)) return 0;
            // OA เดียวทุกบริษัท ⇒ ค้นข้ามบริษัทแล้วแตกเป็นแถวต่อบริษัท (แต่ละแถวมี CompanyId ของตัวเอง)
            var stale = await _db.CustomerLineLinks.AsNoTracking()
                .Where(l => l.UnlinkedAt == null && l.OaScope == CustomerNotifyChannel.ScopeNextAcc && l.OaDestination != null && l.OaDestination != d)
                .Select(l => new { l.CompanyId, Dest = l.OaDestination! })
                .Distinct().Take(5000).ToListAsync(ct);
            var recorded = 0;
            foreach (var g in stale)
            {
                var ch = await _links.ResolveChannelAsync(g.CompanyId, ct);
                if (ch.Scope != CustomerNotifyChannel.ScopeNextAcc) continue;   // บริษัทใช้ OA ของตัวเอง — การสลับของบริษัทบันทึกจากค่าตั้งแล้ว
                if (await RecordSwitchAsync(g.CompanyId, CustomerNotifyChannel.ScopeNextAcc, g.Dest, CustomerNotifyChannel.ScopeNextAcc, d,
                        CustomerRelinkInvitePlan.SourceWebhook, null, ct) != null) recorded++;
            }
            Volatile.Write(ref _nextAccDestinationChecked, d);
            return recorded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ตรวจการเปลี่ยน LINE OA จาก webhook ({Scope}) ไม่สำเร็จ", scope);
            return 0;
        }
    }

    public async Task<string?> FetchBotUserIdAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || _http == null) return null;
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Trim());
            using var resp = await client.GetAsync("https://api.line.me/v2/bot/info", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("ถาม LINE bot info ไม่สำเร็จ ({Status}) — รอ webhook ตัดสินการเปลี่ยน OA", (int)resp.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var id = doc.RootElement.TryGetProperty("userId", out var u) ? u.GetString() : null;
            return string.IsNullOrWhiteSpace(id) || id.Length > 64 ? null : id.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ถาม LINE bot info ล้ม — รอ webhook ตัดสินการเปลี่ยน OA");
            return null;
        }
    }

    // ───────────── ทยอยส่ง ─────────────

    public async Task<RelinkRunResult> ProcessPendingAsync(DateTime nowUtc, CancellationToken ct = default, Guid? onlyCompanyId = null)
    {
        if (_notifier == null) return new RelinkRunResult(0, 0, 0, 0);
        var pending = await _db.LineOaSwitchEvents
            .Where(e => e.ProcessedAt == null && (onlyCompanyId == null || e.CompanyId == onlyCompanyId))
            .OrderBy(e => e.DetectedAt).ThenBy(e => e.Id)
            .Take(200)
            .ToListAsync(ct);
        int events = 0, sentAll = 0, failedAll = 0, completed = 0, budgetAll = CustomerRelinkInvitePlan.MaxPerRun;
        var today = DateOnly.FromDateTime(BookingSlotEngine.BangkokNow(nowUtc));
        var nextAccCanPush = (await _nextAcc.GetAsync(ct)).HasToken;

        foreach (var company in pending.GroupBy(e => e.CompanyId))
        {
            if (budgetAll <= 0) break;
            var budget = Math.Min(CustomerRelinkInvitePlan.BatchPerCompanyPerRun, budgetAll);
            var current = await _links.ResolveChannelAsync(company.Key, ct);
            var sites = new Dictionary<Guid, (string? Name, string? BaseUrl)>();
            foreach (var evt in company)
            {
                events++;
                var r = await ProcessEventAsync(evt, current, nextAccCanPush, today, budget, sites, nowUtc, ct);
                budget -= r.Attempted;
                budgetAll -= r.Attempted;
                sentAll += r.Sent;
                failedAll += r.Failed;
                if (evt.ProcessedAt != null) completed++;
                if (budget <= 0) break;   // ส่วนที่เหลือรอบหน้า (ทยอยส่งต่อบริษัท)
            }
        }
        return new RelinkRunResult(events, sentAll, failedAll, completed);
    }

    private sealed record EventRun(int Attempted, int Sent, int Failed);

    private async Task<EventRun> ProcessEventAsync(LineOaSwitchEvent evt, CustomerLineChannel current, bool nextAccCanPush, DateOnly today, int budget,
        Dictionary<Guid, (string? Name, string? BaseUrl)> sites, DateTime nowUtc, CancellationToken ct)
    {
        var companyId = evt.CompanyId;
        var facts = await LoadFactsAsync(companyId, evt.FromScope ?? "", today, ct);
        var plan = CustomerRelinkInvitePlan.Plan(new RelinkSwitch(evt.Id, evt.FromScope ?? "", evt.FromDestination),
            current.Scope, current.CurrentDestination, facts, nextAccCanPush);
        var keys = plan.Invite.Select(c => c.MessageKey).ToList();
        var logs = await LoadLogsAsync(companyId, keys, ct);

        int attempted = 0, sent = 0, failed = 0;
        string? lastError = null;
        foreach (var c in plan.Invite)
        {
            if (attempted >= budget) break;
            if (logs.TryGetValue(c.MessageKey, out var log) && CustomerRelinkInvitePlan.IsTerminal(log.Status, log.Attempts)) continue;
            attempted++;
            var site = await SiteAsync(companyId, c.Link.SiteId, sites, ct);
            var text = CustomerRelinkInvitePlan.Text(site.Name, CmsStorefrontLink.RelinkLine(site.BaseUrl, c.Link.TargetKind, c.Link.PublicToken));
            var viaLine = c.Channel == CustomerNotifyChannel.ChannelLine;
            var res = await _notifier!.SendAsync(new CustomerNotifyRequest(companyId, c.Link.SiteId, CustomerNotifyEvents.LineRelink, c.MessageKey,
                new BookingContact(c.Email, viaLine ? c.Link.LineUserId : null), CustomerRelinkInvitePlan.Subject(site.Name), text)
            {
                TargetKind = c.Link.TargetKind,
                TargetId = c.Link.TargetId,
                LineViaNextAcc = viaLine,
            }, ct);
            if (res.Sent) sent++;
            else
            {
                if (!res.IsSuppressed) failed++;
                lastError = res.Error;
            }
            await Task.Delay(CustomerRelinkInvitePlan.Throttle, ct);
        }

        // ความคืบหน้านับใหม่จากบันทึกการส่ง (ตัวจริง) — ไม่บวกสะสมเอง
        var after = await LoadLogsAsync(companyId, keys, ct);
        var invited = after.Values.Count(l => l.Status == NotifyLogStatus.Sent);
        var suppressed = after.Values.Count(l => l.Status == NotifyLogStatus.Suppressed);
        var failedNow = after.Values.Count(l => l.Status == NotifyLogStatus.Failed);
        var done = plan.Invite.All(c => after.TryGetValue(c.MessageKey, out var l) && CustomerRelinkInvitePlan.IsTerminal(l.Status, l.Attempts));
        evt.Total = plan.Invite.Count;
        evt.Invited = invited;
        evt.Failed = failedNow;
        evt.Skipped = plan.Skipped + suppressed;
        evt.LastRunAt = nowUtc;
        if (lastError != null) evt.LastError = Trunc(lastError, 500);
        else if (attempted > 0 && failedNow == 0) evt.LastError = null;
        if (done) evt.ProcessedAt = nowUtc;
        evt.UpdatedAt = nowUtc;
        evt.UpdatedBy = "system:customer-notify-outbox";
        await _db.SaveChangesAsync(ct);
        if (lastError != null)
            _logger.LogWarning("เชิญผูก LINE ใหม่ (บริษัท {CompanyId} การสลับ {SwitchId}) มีรายการส่งไม่สำเร็จ: {Error}", companyId, evt.Id, lastError);
        return new EventRun(attempted, sent, failed);
    }

    private sealed record LogRow(string Status, int Attempts);

    private async Task<Dictionary<string, LogRow>> LoadLogsAsync(Guid companyId, List<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0) return new Dictionary<string, LogRow>(StringComparer.Ordinal);
        var rows = await _db.CustomerNotifyLogs.AsNoTracking()
            .Where(l => l.CompanyId == companyId && keys.Contains(l.MessageKey))
            .Select(l => new { l.MessageKey, l.Status, l.Attempts })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.MessageKey, r => new LogRow(r.Status, r.Attempts), StringComparer.Ordinal);
    }

    private async Task<(string? Name, string? BaseUrl)> SiteAsync(Guid companyId, Guid siteId, Dictionary<Guid, (string? Name, string? BaseUrl)> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(siteId, out var hit)) return hit;
        var name = await _db.Sites.AsNoTracking().Where(s => s.Id == siteId && s.CompanyId == companyId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        var baseUrl = _shop == null ? null : await _shop.StorefrontBaseUrlAsync(companyId, siteId, ct);
        var v = (name, baseUrl);
        cache[siteId] = v;
        return v;
    }

    /// <summary>ลิงก์ที่ยังเปิดบน OA ชนิดเดิม + ความยินยอม + สถานะเป้าหมาย + อีเมล/โทเคน — ทุก query กรอง <c>CompanyId</c></summary>
    private async Task<List<RelinkLinkFact>> LoadFactsAsync(Guid companyId, string fromScope, DateOnly today, CancellationToken ct)
    {
        var links = await _db.CustomerLineLinks.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.UnlinkedAt == null && l.OaScope == fromScope)
            .Select(l => new { l.Id, l.SiteId, l.TargetKind, l.TargetId, l.LineUserId, l.OaScope, l.OaDestination, l.LinkedAt, l.BlockedAt, l.ConsentRecordId })
            .ToListAsync(ct);
        if (links.Count == 0) return new List<RelinkLinkFact>();

        var consentIds = links.Where(l => l.ConsentRecordId != null).Select(l => l.ConsentRecordId!.Value).Distinct().ToList();
        // ยังยินยอมอยู่ = พบแถว + ยังไม่ถอน · แถวหาย (ลบตาม DSR) ⇒ ถือว่าถอน (ด้านปลอดภัย)
        var consentOk = consentIds.Count == 0 ? new HashSet<Guid>() : (await _db.PdpaConsentRecords.AsNoTracking()
            .Where(c => c.CompanyId == companyId && consentIds.Contains(c.Id) && c.WithdrawnAt == null)
            .Select(c => c.Id).ToListAsync(ct)).ToHashSet();

        List<Guid> Ids(string kind) => links.Where(l => l.TargetKind == kind).Select(l => l.TargetId).Distinct().ToList();
        var memberIds = Ids(CustomerLineLinkRule.KindMember);
        var bookingIds = Ids(CustomerLineLinkRule.KindBooking);
        var orderIds = Ids(CustomerLineLinkRule.KindOrder);
        var memberRows = await _db.SiteCustomers.AsNoTracking()
            .Where(x => x.CompanyId == companyId && memberIds.Contains(x.Id))
            .Select(x => new { x.Id, x.IsActive, x.Email })
            .ToListAsync(ct);
        var bookingRows = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.CompanyId == companyId && bookingIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Status, x.BookingDate, Email = x.GuestEmail ?? (x.Customer != null ? x.Customer.Email : null), x.PublicToken })
            .ToListAsync(ct);
        var orderRows = await _db.SiteOrders.AsNoTracking()
            .Where(x => x.CompanyId == companyId && orderIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Status, Email = x.ShippingEmail ?? (x.Customer != null ? x.Customer.Email : null), x.PublicToken })
            .ToListAsync(ct);

        var targets = new Dictionary<(string, Guid), TargetRow>();
        foreach (var m in memberRows)
            targets[(CustomerLineLinkRule.KindMember, m.Id)] = new TargetRow(m.Id,
                CustomerRelinkInvitePlan.IsRelevant(CustomerLineLinkRule.KindMember, m.IsActive, null, null, null, today), m.Email, null);
        foreach (var b in bookingRows)
            targets[(CustomerLineLinkRule.KindBooking, b.Id)] = new TargetRow(b.Id,
                CustomerRelinkInvitePlan.IsRelevant(CustomerLineLinkRule.KindBooking, null, b.Status, b.BookingDate, null, today), b.Email, b.PublicToken);
        foreach (var o in orderRows)
            targets[(CustomerLineLinkRule.KindOrder, o.Id)] = new TargetRow(o.Id,
                CustomerRelinkInvitePlan.IsRelevant(CustomerLineLinkRule.KindOrder, null, null, null, o.Status, today), o.Email, o.PublicToken);

        return links.Select(l =>
        {
            targets.TryGetValue((l.TargetKind, l.TargetId), out var t);
            return new RelinkLinkFact(l.Id, l.SiteId, l.TargetKind, l.TargetId, l.LineUserId, l.OaScope, l.OaDestination, l.LinkedAt, l.BlockedAt,
                ConsentWithdrawn: l.ConsentRecordId is Guid cid && !consentOk.Contains(cid),
                TargetRelevant: t?.Relevant == true, Email: t?.Email, PublicToken: t?.PublicToken);
        }).ToList();
    }

    private sealed record TargetRow(Guid Id, bool Relevant, string? Email, string? PublicToken);

    // ───────────── หน้าตั้งค่า ─────────────

    public async Task<CustomerRelinkProgress?> GetProgressAsync(Guid companyId, CancellationToken ct = default)
    {
        var pendingCount = await _db.LineOaSwitchEvents.AsNoTracking().CountAsync(e => e.CompanyId == companyId && e.ProcessedAt == null, ct);
        var e = await _db.LineOaSwitchEvents.AsNoTracking()
            .Where(x => x.CompanyId == companyId)
            .OrderBy(x => x.ProcessedAt == null ? 0 : 1).ThenByDescending(x => x.DetectedAt)
            .FirstOrDefaultAsync(ct);
        if (e == null) return null;
        var done = e.ProcessedAt != null;
        return new CustomerRelinkProgress(e.Id, e.FromScope, e.ToScope, e.DetectedAt, e.ProcessedAt, e.Total, e.Invited, e.Failed, e.Skipped, done,
            CustomerRelinkInvitePlan.ProgressLabel(e.LastRunAt != null, e.Total, e.Invited, e.Failed, e.Skipped, done), e.LastError, pendingCount);
    }

    private static string? Trunc(string? s, int max) => s == null ? null : s.Length > max ? s[..max] : s;

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().Name == "PostgresException"
           && (ex.InnerException.Message.Contains("23505") || ex.InnerException.Message.Contains("duplicate key value"));
}
