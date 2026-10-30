using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>สรุปประจำวันทาง LINE ถึงพนักงาน (รอบ 212 C · คำตัดสินข้อ 5 · <c>erp-review/2026-10-30/NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN.md</c> §5)
///
/// <para><b>หนึ่งรอบต่อชุด</b> (<see cref="RunConfigAsync"/>): อ่านข้อมูลของบริษัท<b>ครั้งเดียว</b> (อ่านอย่างเดียว · ทุก query <c>CompanyId == companyId</c> +
/// <c>siteIds.Contains</c>) → ต่อผู้รับ: claim แถว (ชุด, วัน, คน) ด้วย <c>INSERT … ON CONFLICT</c> (unique ⇒ ไม่ส่งซ้ำข้ามเครื่อง/รอบ) → ตรวจสิทธิ์
/// <b>ณ วันส่ง</b> (สมาชิกบริษัท · ผูก LINE · คีย์สิทธิ์ · เว็บที่เห็น) → <see cref="StaffDailySummaryPlan.FilterForRecipient"/> →
/// <see cref="StaffDailySummaryBuilder.Build"/> → จองโควตาช่องกลาง (<see cref="ISharedMessageMeter"/> · §10 นับ) → push ผ่าน <b>OA กลาง</b>
/// (<see cref="ILineNotifyService.TryPushViaNextAccAsync"/>) → ประทับผล/เหตุบนแถว · ทุกผู้รับจบ ⇒ ขยับ watermark <c>LastSentLocalDate</c></para>
///
/// <para>ล้มไม่เงียบ: เหตุอยู่บนแถวส่ง (<c>Error</c>) + สรุปบนชุด (<c>LastRunNote</c>) ที่หน้าตั้งค่าแสดง · ผู้รับที่ยังไม่ผูก LINE ได้แถว "ยังไม่ผูก LINE"</para></summary>
public class StaffDailySummaryService : IStaffDailySummaryService
{
    private readonly AccountingDbContext _db;
    private readonly IPermissionService _perms;
    private readonly ILineNotifyService _line;
    private readonly ISharedMessageMeter _meter;
    private readonly IConfiguration _config;
    private readonly ILogger<StaffDailySummaryService> _logger;

    public const string BindPage = "/pages/line-bind.html";
    public const string BindHowTo = "ผู้รับต้องผูก LINE กับบัญชี NextAcc ก่อน: เปิดหน้า \"ผูก LINE\" ขอรหัส 6 หลัก แล้วเพิ่มเพื่อน LINE OA ของ NextAcc และส่งข้อความ \"ผูก 123456\" (ใช้รหัสของตัวเอง) — ผู้ที่ยังไม่ผูกจะถูกข้าม";

    public StaffDailySummaryService(AccountingDbContext db, IPermissionService perms, ILineNotifyService line,
        ISharedMessageMeter meter, IConfiguration config, ILogger<StaffDailySummaryService> logger)
    {
        _db = db;
        _perms = perms;
        _line = line;
        _meter = meter;
        _config = config;
        _logger = logger;
    }

    // ═════════════════════ ตั้งค่า ═════════════════════

    public async Task<StaffDailySummaryOverviewDto> GetOverviewAsync(Guid companyId, CancellationToken ct = default)
    {
        var sites = await CompanySitesAsync(companyId, ct);
        var members = await MembersAsync(companyId, ct);
        var configs = await _db.StaffDailySummaryConfigs.AsNoTracking()
            .Where(c => c.CompanyId == companyId).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var since = DateOnly.FromDateTime(BookingSlotEngine.BangkokNow(DateTime.UtcNow)).AddDays(-6);
        var history = await _db.StaffDailySummaryDeliveries.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.LocalDate >= since)
            .OrderByDescending(d => d.LocalDate).ThenBy(d => d.UserId).Take(500).ToListAsync(ct);
        var names = await UserNamesAsync(history.Select(h => h.UserId), ct);

        return new StaffDailySummaryOverviewDto
        {
            Sections = StaffDailySummaryPlan.Sections.Select(s => new StaffDailySummarySectionDto { Key = s.Key, Label = s.Label, Hint = s.Hint }).ToList(),
            Roles = StaffDailySummaryPlan.RecipientRoles.Select(r => new StaffDailySummaryOptionDto { Key = r.Role.ToString(), Label = r.Label }).ToList(),
            Sites = sites.Select(s => new StaffDailySummaryOptionDto { Key = s.Id.ToString(), Label = s.Name }).ToList(),
            Members = members.Select(ToMemberDto).ToList(),
            Configs = configs.Select(c => ToDto(c, sites, members)).ToList(),
            History = history.Select(h => new StaffDailySummaryHistoryDto
            {
                ConfigId = h.ConfigId, Date = h.LocalDate, UserId = h.UserId, Name = names.GetValueOrDefault(h.UserId) ?? "-",
                Status = h.Status, StatusLabel = StaffDailySummaryPlan.StatusLabel(h.Status), Error = h.Error,
                Attempts = h.Attempts, Messages = h.Messages, SentAt = h.SentAt,
            }).ToList(),
            BindHowTo = BindHowTo,
            BindPage = BindPage,
            DefaultSendAt = StaffDailySummaryPlan.DefaultSendAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    public async Task<StaffDailySummaryConfigDto> UpsertAsync(Guid companyId, Guid? configId, UpsertStaffDailySummaryRequest request, string actor, CancellationToken ct = default)
    {
        var sites = await CompanySitesAsync(companyId, ct);
        if (request.SiteId is Guid sid && sites.All(s => s.Id != sid))
            throw new BusinessRuleException("ไม่พบเว็บไซต์นี้ในบริษัท", StaffDailySummaryPlan.RuleCode);
        TimeOnly sendAt = StaffDailySummaryPlan.DefaultSendAt;
        if (!string.IsNullOrWhiteSpace(request.SendAt) && !TimeOnly.TryParseExact(request.SendAt.Trim(), "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out sendAt))
            throw new BusinessRuleException("เวลาส่งต้องเป็นรูป HH:mm (เวลาไทย)", StaffDailySummaryPlan.RuleCode);
        var unknown = StaffDailySummaryPlan.UnknownSections(request.Sections);
        if (unknown.Count > 0)
            throw new BusinessRuleException($"ไม่รู้จักหัวข้อ: {string.Join(", ", unknown)}", StaffDailySummaryPlan.RuleCode);
        var sections = StaffDailySummaryPlan.SectionsCsv(request.Sections);
        if (sections.Length == 0)
            throw new BusinessRuleException("เลือกหัวข้ออย่างน้อย 1 หัวข้อ", StaffDailySummaryPlan.RuleCode);
        var roles = StaffDailySummaryPlan.ParseRoles(string.Join(",", request.RecipientRoles ?? new List<string>()));
        if ((request.RecipientRoles?.Count ?? 0) != roles.Count)
            throw new BusinessRuleException("บทบาทผู้รับไม่ถูกต้อง", StaffDailySummaryPlan.RuleCode);
        var members = await MembersAsync(companyId, ct);
        var userIds = (request.RecipientUserIds ?? new List<Guid>()).Distinct().ToList();
        if (userIds.Any(u => members.All(m => m.UserId != u)))
            throw new BusinessRuleException("ผู้รับบางคนไม่ได้เป็นสมาชิกของบริษัทนี้", StaffDailySummaryPlan.RuleCode);
        if (userIds.Count == 0 && roles.Count == 0)
            throw new BusinessRuleException("เลือกผู้รับอย่างน้อย 1 คนหรือ 1 บทบาท", StaffDailySummaryPlan.RuleCode);

        StaffDailySummaryConfig cfg;
        if (configId is Guid id)
        {
            cfg = await _db.StaffDailySummaryConfigs.FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId, ct)
                ?? throw new BusinessRuleException("ไม่พบชุดสรุปนี้", StaffDailySummaryPlan.RuleCode, 404);
            cfg.UpdatedAt = DateTime.UtcNow;
            cfg.UpdatedBy = actor;
        }
        else
        {
            cfg = new StaffDailySummaryConfig { CompanyId = companyId, CreatedBy = actor };
            _db.StaffDailySummaryConfigs.Add(cfg);
        }
        cfg.SiteId = request.SiteId;
        cfg.Enabled = request.Enabled;
        cfg.SendAtLocal = StaffDailySummaryPlan.RoundSendAt(sendAt);
        cfg.Sections = sections;
        cfg.RecipientUserIds = userIds.Count == 0 ? null : StaffDailySummaryPlan.UserIdsCsv(userIds);
        cfg.RecipientRoles = roles.Count == 0 ? null : StaffDailySummaryPlan.RolesCsv(roles);
        await _db.SaveChangesAsync(ct);
        return ToDto(cfg, sites, members);
    }

    public async Task DeleteAsync(Guid companyId, Guid configId, string actor, CancellationToken ct = default)
    {
        var cfg = await _db.StaffDailySummaryConfigs.FirstOrDefaultAsync(c => c.Id == configId && c.CompanyId == companyId, ct)
            ?? throw new BusinessRuleException("ไม่พบชุดสรุปนี้", StaffDailySummaryPlan.RuleCode, 404);
        cfg.IsDeleted = true;
        cfg.Enabled = false;
        cfg.UpdatedAt = DateTime.UtcNow;
        cfg.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<StaffDailySummaryTestResult> SendTestAsync(Guid companyId, Guid configId, Guid userId, CancellationToken ct = default)
    {
        var cfg = await _db.StaffDailySummaryConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == configId && c.CompanyId == companyId, ct)
            ?? throw new BusinessRuleException("ไม่พบชุดสรุปนี้", StaffDailySummaryPlan.RuleCode, 404);
        var me = (await MembersAsync(companyId, ct)).FirstOrDefault(m => m.UserId == userId);
        if (me == null) return new StaffDailySummaryTestResult { Sent = false, Message = "คุณไม่ได้เป็นสมาชิกของบริษัทนี้" };
        if (string.IsNullOrWhiteSpace(me.LineUserId))
            return new StaffDailySummaryTestResult { Sent = false, Message = "คุณยังไม่ผูก LINE — " + BindHowTo };

        var nowUtc = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(BookingSlotEngine.BangkokNow(nowUtc));
        var round = await LoadRoundAsync(cfg, today, ct);
        var messages = await BuildForRecipientAsync(cfg, round, me, today, ct);
        if (messages.Count == 0)
            return new StaffDailySummaryTestResult { Sent = false, Message = "ไม่มีหัวข้อที่คุณมีสิทธิ์ดูในชุดนี้ (ตรวจสิทธิ์รายเว็บ/คีย์สิทธิ์ของคุณ)" };

        var key = $"staffsum-test:{cfg.Id:N}:{userId:N}:{nowUtc.Ticks}";
        if (!await _meter.TryReserveAsync(companyId, key, ct))
            return new StaffDailySummaryTestResult { Sent = false, Message = StaffDailySummaryPlan.StatusLabel(StaffDailySummaryPlan.StatusQuotaExceeded) };
        var r = await _line.TryPushViaNextAccAsync(me.LineUserId!, messages);
        if (!r.Ok)
        {
            await _meter.ReleaseAsync(companyId, key, ct);
            return new StaffDailySummaryTestResult { Sent = false, Message = "ส่งไม่สำเร็จ: " + r.Error, Messages = messages.Count };
        }
        return new StaffDailySummaryTestResult { Sent = true, Message = $"ส่งสรุปถึง LINE ของคุณแล้ว ({messages.Count} ข้อความ)", Messages = messages.Count };
    }

    // ═════════════════════ รอบส่ง (งานเบื้องหลัง) ═════════════════════

    public async Task<int> RunDueAsync(DateTime nowUtc, CancellationToken ct, Guid? onlyCompanyId = null)
    {
        var nowBkk = BookingSlotEngine.BangkokNow(nowUtc);
        var configs = await _db.StaffDailySummaryConfigs
            .Where(c => c.Enabled && (onlyCompanyId == null || c.CompanyId == onlyCompanyId))
            .ToListAsync(ct);
        var sent = 0;
        foreach (var cfg in configs)
        {
            if (ct.IsCancellationRequested) break;
            // watermark ต่อบริษัท/ชุด — ส่งครบแล้ววันนี้ / ยังไม่ถึงเวลา / เลยเวลาตามหลัง ⇒ ข้าม
            if (!StaffDailySummaryPlan.Due(nowBkk, cfg.SendAtLocal, cfg.LastSentLocalDate)) continue;
            try { sent += await RunConfigAsync(cfg, nowUtc, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ชุดหนึ่งล้มไม่ล้มทั้งงาน — แต่ประทับบนชุด (หน้าตั้งค่าเห็น) ไม่ใช่แค่ log
                _logger.LogError(ex, "สรุปประจำวัน: ชุด {ConfigId} (บริษัท {CompanyId}) ล้ม", cfg.Id, cfg.CompanyId);
                cfg.LastRunAt = nowUtc;
                cfg.LastRunNote = LineMessageLimits.Truncate($"รอบ {DateOnly.FromDateTime(nowBkk):dd/MM/yyyy} ล้ม: {ex.GetType().Name} — ระบบจะลองใหม่ในรอบถัดไป", 1000);
                await _db.SaveChangesAsync(ct);
            }
        }
        return sent;
    }

    /// <summary>หนึ่งชุด หนึ่งวัน — claim ต่อผู้รับ · กรองสิทธิ์ต่อผู้รับ · push OA กลาง · ประทับ · ขยับ watermark เมื่อทุกผู้รับจบ</summary>
    private async Task<int> RunConfigAsync(StaffDailySummaryConfig cfg, DateTime nowUtc, CancellationToken ct)
    {
        var companyId = cfg.CompanyId;
        var today = DateOnly.FromDateTime(BookingSlotEngine.BangkokNow(nowUtc));
        var members = await MembersAsync(companyId, ct);
        var recipientIds = StaffDailySummaryPlan.ResolveRecipients(members.Select(m => (m.UserId, m.Role)),
            StaffDailySummaryPlan.ParseUserIds(cfg.RecipientUserIds), StaffDailySummaryPlan.ParseRoles(cfg.RecipientRoles));
        var recipients = members.Where(m => recipientIds.Contains(m.UserId)).ToList();

        RoundData? round = null;
        int sent = 0, noLine = 0, failed = 0, skipped = 0;
        string? lastError = null;
        foreach (var m in recipients)
        {
            if (ct.IsCancellationRequested) break;
            if (!await ClaimAsync(companyId, cfg.Id, today, m.UserId, ct)) continue;   // ส่งแล้ว/อีกเครื่องกำลังส่ง/ล้มครบโควตาลอง
            var row = await _db.StaffDailySummaryDeliveries
                .FirstAsync(d => d.CompanyId == companyId && d.ConfigId == cfg.Id && d.LocalDate == today && d.UserId == m.UserId, ct);

            if (string.IsNullOrWhiteSpace(m.LineUserId))
            {
                Stamp(row, StaffDailySummaryPlan.StatusNoLine, "ยังไม่ผูก LINE กับ NextAcc", 0, 0, null);
                noLine++;
                await _db.SaveChangesAsync(ct);
                continue;
            }

            round ??= await LoadRoundAsync(cfg, today, ct);
            var messages = await BuildForRecipientAsync(cfg, round, m, today, ct);
            if (messages.Count == 0)
            {
                Stamp(row, StaffDailySummaryPlan.StatusNothingVisible, "ผู้รับไม่มีสิทธิ์ดูหัวข้อใดในชุดนี้", 0, 0, null);
                skipped++;
                await _db.SaveChangesAsync(ct);
                continue;
            }

            var key = StaffDailySummaryPlan.MessageKey(cfg.Id, today, m.UserId);
            if (!await _meter.TryReserveAsync(companyId, key, ct))
            {
                Stamp(row, StaffDailySummaryPlan.StatusQuotaExceeded, "โควตาข้อความช่องกลางของเดือนนี้เต็ม — ซื้อแพ็กเพิ่มที่หน้าแพ็กเสริม", messages.Count, 0, null);
                failed++;
                lastError = row.Error;
                await _db.SaveChangesAsync(ct);
                continue;
            }

            var r = await _line.TryPushViaNextAccAsync(m.LineUserId!, messages);
            if (r.Ok)
            {
                Stamp(row, StaffDailySummaryPlan.StatusSent, null, messages.Count, 1, DateTime.UtcNow);
                sent++;
            }
            else
            {
                await _meter.ReleaseAsync(companyId, key, ct);
                Stamp(row, StaffDailySummaryPlan.StatusFailed, r.Error ?? "ส่งไม่สำเร็จ", messages.Count, 0, null);
                failed++;
                lastError = row.Error;
            }
            await _db.SaveChangesAsync(ct);
        }

        // watermark: ทุกผู้รับของวันนี้จบแล้ว (ส่งถึง/ข้ามด้วยเหตุ/ล้มครบโควตาลอง) ⇒ วันนี้ไม่ต้องกลับมาอีก
        var todays = await _db.StaffDailySummaryDeliveries.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ConfigId == cfg.Id && d.LocalDate == today)
            .Select(d => new { d.UserId, d.Status, d.Attempts }).ToListAsync(ct);
        var done = recipients.All(m => todays.Any(t => t.UserId == m.UserId && StaffDailySummaryPlan.IsTerminal(t.Status, t.Attempts)));
        if (done) cfg.LastSentLocalDate = today;
        cfg.LastRunAt = nowUtc;
        cfg.LastRunNote = LineMessageLimits.Truncate(
            recipients.Count == 0
                ? $"{today:dd/MM/yyyy}: ไม่มีผู้รับ (ตรวจผู้รับ/บทบาทในชุดนี้)"
                : $"{today:dd/MM/yyyy}: ส่งถึง {sent} · ยังไม่ผูก LINE {noLine} · ไม่มีหัวข้อที่มีสิทธิ์ {skipped} · ไม่สำเร็จ {failed}"
                  + (lastError != null ? $" (ล่าสุด: {lastError})" : "") + (done ? "" : " — จะลองใหม่ในรอบถัดไป"), 1000);
        await _db.SaveChangesAsync(ct);
        return sent;
    }

    private static void Stamp(StaffDailySummaryDelivery row, string status, string? error, int messages, int units, DateTime? sentAt)
    {
        row.Status = status;
        row.Error = error == null ? null : LineMessageLimits.Truncate(error, 1000);
        row.Messages = messages;
        row.Units = units;
        row.SentAt = sentAt;
        row.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>จองแถว (ชุด, วัน, คน) — คืน true เมื่อรอบนี้เป็นเจ้าของการส่ง: แถวใหม่ · ล้มแต่ยังลองได้ · "กำลังส่ง" ที่ค้างเกิน <see cref="StaffDailySummaryPlan.StaleClaim"/> ·
    /// unique index + <c>ON CONFLICT … WHERE</c> ในคำสั่งเดียว ⇒ สองเครื่อง/สองรอบไม่ได้แถวเดียวกัน</summary>
    private async Task<bool> ClaimAsync(Guid companyId, Guid configId, DateOnly localDate, Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var stale = now - StaffDailySummaryPlan.StaleClaim;
        var id = Guid.NewGuid();
        var sending = StaffDailySummaryPlan.StatusSending;
        var failedStatus = StaffDailySummaryPlan.StatusFailed;
        var max = StaffDailySummaryPlan.MaxAttempts;
        var n = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""StaffDailySummaryDeliveries"" (""Id"", ""CompanyId"", ""ConfigId"", ""LocalDate"", ""UserId"", ""Status"", ""Attempts"", ""ClaimedAt"", ""CreatedAt"", ""IsDeleted"")
            VALUES ({id}, {companyId}, {configId}, {localDate}, {userId}, {sending}, 1, {now}, {now}, false)
            ON CONFLICT (""ConfigId"", ""LocalDate"", ""UserId"") DO UPDATE
               SET ""Status"" = {sending}, ""Attempts"" = ""StaffDailySummaryDeliveries"".""Attempts"" + 1, ""ClaimedAt"" = {now}, ""Error"" = NULL
             WHERE ""StaffDailySummaryDeliveries"".""CompanyId"" = {companyId}
               AND ((""StaffDailySummaryDeliveries"".""Status"" = {failedStatus} AND ""StaffDailySummaryDeliveries"".""Attempts"" < {max})
                 OR (""StaffDailySummaryDeliveries"".""Status"" = {sending} AND ""StaffDailySummaryDeliveries"".""ClaimedAt"" < {stale}))", ct);
        return n == 1;
    }

    // ═════════════════════ ประกอบต่อผู้รับ ═════════════════════

    private sealed record Member(Guid UserId, UserRole Role, string Name, string? LineUserId);
    private sealed record SiteInfo(Guid Id, string Name);

    /// <summary>ข้อมูลของบริษัทหนึ่งรอบ (อ่านครั้งเดียว · ยังไม่กรองต่อผู้รับ)</summary>
    private sealed record RoundData(string CompanyName, string? ScopeName, IReadOnlyList<Guid> SiteIds, IReadOnlyList<string> Sections,
        bool LodgingActive, StaffDailySummaryPlan.SummaryData Rows);

    /// <summary>สิทธิ์ ณ วันส่ง → กรอง → ประกอบ · คืนรายการว่าง = ไม่มีหัวข้อที่ผู้รับเห็น</summary>
    private async Task<IReadOnlyList<string>> BuildForRecipientAsync(StaffDailySummaryConfig cfg, RoundData round, Member m, DateOnly today, CancellationToken ct)
    {
        var scope = await RecipientScopeAsync(cfg.CompanyId, m, round.SiteIds, ct);
        var visible = StaffDailySummaryPlan.FilterForRecipient(round.Rows, round.Sections, scope, round.LodgingActive);
        if (!visible.HasAnySection) return Array.Empty<string>();
        return StaffDailySummaryBuilder.Build(today, round.CompanyName, round.ScopeName, visible, _config["App:BaseUrl"]);
    }

    /// <summary>คีย์สิทธิ์ที่แคตตาล็อกถาม (ผ่าน <see cref="IPermissionService.HasPermissionAsync"/> — กติกาเดียวกับด่าน endpoint · เจ้าของผ่านทุกคีย์) +
    /// เว็บที่เห็นตาม <see cref="CmsSiteScope.AllowedSiteIds"/> (กติกาเดียวกับมุมมองทุกไซต์)</summary>
    private async Task<StaffDailySummaryPlan.RecipientScope> RecipientScopeAsync(Guid companyId, Member m, IReadOnlyList<Guid> siteIds, CancellationToken ct)
    {
        var granted = new List<string>();
        foreach (var key in StaffDailySummaryPlan.PermissionKeysToCheck())
            if (await _perms.HasPermissionAsync(companyId, m.UserId, key)) granted.Add(key);
        var access = await _db.SiteStaffAccesses.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.UserId == m.UserId)
            .Select(a => new { a.SiteId, a.Role, a.IsActive }).ToListAsync(ct);
        var rows = access.Select(a => new CmsSiteScope.AccessRow(a.SiteId, a.Role, a.IsActive)).ToList();
        var isOwner = m.Role == UserRole.Owner;
        return new StaffDailySummaryPlan.RecipientScope(granted,
            CmsSiteScope.AllowedSiteIds(isOwner, siteIds, rows, CmsSiteScope.AnyRole),
            CmsSiteScope.AllowedSiteIds(isOwner, siteIds, rows, CmsSiteScope.OrderRoles));
    }

    private async Task<RoundData> LoadRoundAsync(StaffDailySummaryConfig cfg, DateOnly today, CancellationToken ct)
    {
        var companyId = cfg.CompanyId;
        var sites = await CompanySitesAsync(companyId, ct);
        var scoped = cfg.SiteId is Guid sid ? sites.Where(s => s.Id == sid).ToList() : sites;
        var siteIds = scoped.Select(s => s.Id).ToList();
        var sections = StaffDailySummaryPlan.ParseSections(cfg.Sections);
        var companyName = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var lodgingActive = await _db.LodgingProperties.AsNoTracking().AnyAsync(p => p.CompanyId == companyId, ct);
        var data = await GatherAsync(companyId, siteIds, cfg.SiteId, sections, lodgingActive, today, ct);
        return new RoundData(companyName, cfg.SiteId != null ? scoped.FirstOrDefault()?.Name : null, siteIds, sections, lodgingActive, data);
    }

    /// <summary>อ่านข้อมูลของหัวข้อที่ชุดเลือก (อ่านอย่างเดียว) — ทุก query <c>CompanyId == companyId</c> · ข้อมูลผูกเว็บกรอง <c>siteIds.Contains</c> ·
    /// ลูกค้า: ชื่อเท่านั้น (ไม่มีเบอร์/อีเมล)</summary>
    private async Task<StaffDailySummaryPlan.SummaryData> GatherAsync(Guid companyId, List<Guid> siteIds, Guid? configSiteId,
        IReadOnlyList<string> sections, bool lodgingActive, DateOnly today, CancellationToken ct)
    {
        bool Want(string k) => sections.Contains(k);
        var tomorrow = today.AddDays(1);
        List<StaffDailySummaryPlan.BookingRow>? bookings = null;
        List<StaffDailySummaryPlan.ClassRow>? classes = null;
        List<StaffDailySummaryPlan.OrderRow>? mto = null, ship = null;
        List<StaffDailySummaryPlan.LodgingRow>? lodging = null;
        List<StaffDailySummaryPlan.CreditRow>? credits = null;

        if ((Want(StaffDailySummaryPlan.Bookings) || Want(StaffDailySummaryPlan.Classes)) && siteIds.Count > 0)
        {
            // วันที่จองเก็บเป็นวันตามปฏิทิน (เหมือน BookingReminderJob)
            var from = today.ToDateTime(TimeOnly.MinValue);
            var to = today.AddDays(2).ToDateTime(TimeOnly.MinValue);
            var raw = await _db.SiteBookings.AsNoTracking()
                .Where(b => b.CompanyId == companyId && siteIds.Contains(b.SiteId) && b.BookingDate >= from && b.BookingDate < to)
                .Select(b => new
                {
                    b.Id, b.SiteId, b.BookingDate, b.StartTime, b.BookingServiceId, b.Status, b.GuestCount,
                    ServiceName = b.BookingService.Name,
                    Customer = b.GuestName ?? (b.Customer != null ? b.Customer.FullName : null),
                    b.AssignedStaffName,
                    ResourceName = b.ResourceId == null ? null
                        : _db.SiteBookingResources.Where(r => r.Id == b.ResourceId && r.CompanyId == b.CompanyId).Select(r => r.Name).FirstOrDefault(),
                })
                .Take(2000).ToListAsync(ct);
            var ids = raw.Select(r => r.Id).ToList();
            var assigned = ids.Count == 0 ? new Dictionary<Guid, string>()
                : (await _db.SiteBookingAssignments.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && ids.Contains(a.BookingId))
                    .Select(a => new { a.BookingId, a.ResourceName }).ToListAsync(ct))
                  .GroupBy(a => a.BookingId).ToDictionary(g => g.Key, g => string.Join(" + ", g.Select(a => a.ResourceName).Distinct()));

            var sessions = await _db.SiteClassSessions.AsNoTracking()
                .Where(s => s.CompanyId == companyId && siteIds.Contains(s.SiteId) && s.SessionDate >= today && s.SessionDate <= tomorrow)
                .ToListAsync(ct);
            var factsBySvc = sessions.GroupBy(s => s.BookingServiceId).ToDictionary(g => g.Key, g => g.Select(s =>
                new ClassSessionFact(s.Id, s.SessionDate, s.StartTime, s.EndTime, s.Status == ClassSessionStatus.Cancelled, s.CapacityOverride,
                    s.InstructorResourceId, s.SubstituteResourceId)).ToList());

            var inClass = new Dictionary<Guid, List<(BookingStatus Status, int GuestCount)>>();
            bookings = new List<StaffDailySummaryPlan.BookingRow>();
            foreach (var b in raw)
            {
                var date = DateOnly.FromDateTime(b.BookingDate);
                var session = factsBySvc.TryGetValue(b.BookingServiceId, out var facts) ? ClassScheduleGenerator.SessionAt(facts, date, b.StartTime) : null;
                if (session != null)
                {
                    if (!inClass.TryGetValue(session.Id, out var list)) inClass[session.Id] = list = new();
                    list.Add((b.Status, b.GuestCount));
                    continue;
                }
                if (b.Status is BookingStatus.Cancelled or BookingStatus.NoShow or BookingStatus.Waitlisted) continue;
                var provider = assigned.GetValueOrDefault(b.Id) ?? b.AssignedStaffName ?? b.ResourceName;
                bookings.Add(new StaffDailySummaryPlan.BookingRow(b.SiteId, date, b.StartTime, b.ServiceName,
                    string.IsNullOrWhiteSpace(provider) ? "ไม่ระบุผู้ให้บริการ" : provider!, string.IsNullOrWhiteSpace(b.Customer) ? "ลูกค้า" : b.Customer!));
            }

            if (Want(StaffDailySummaryPlan.Classes))
            {
                var svcIds = sessions.Select(s => s.BookingServiceId).Distinct().ToList();
                var services = await _db.SiteBookingServices.AsNoTracking()
                    .Where(s => s.CompanyId == companyId && svcIds.Contains(s.Id))
                    .Select(s => new { s.Id, s.Name, s.MaxCapacity, s.CapacityUnit }).ToListAsync(ct);
                var teacherIds = sessions.Select(s => s.SubstituteResourceId ?? s.InstructorResourceId).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
                var teachers = teacherIds.Count == 0 ? new Dictionary<Guid, string>()
                    : await _db.SiteBookingResources.AsNoTracking().Where(r => r.CompanyId == companyId && teacherIds.Contains(r.Id))
                        .ToDictionaryAsync(r => r.Id, r => r.Name, ct);
                classes = new List<StaffDailySummaryPlan.ClassRow>();
                foreach (var s in sessions.Where(s => s.Status != ClassSessionStatus.Cancelled))
                {
                    var svc = services.FirstOrDefault(x => x.Id == s.BookingServiceId);
                    var (taken, wait) = ClassScheduleGenerator.Fill(inClass.GetValueOrDefault(s.Id) ?? new List<(BookingStatus Status, int GuestCount)>(),
                        svc?.CapacityUnit ?? BookingCapacityUnit.Bookings);
                    var teacherId = s.SubstituteResourceId ?? s.InstructorResourceId;
                    classes.Add(new StaffDailySummaryPlan.ClassRow(s.SiteId, s.SessionDate, s.StartTime, svc?.Name ?? "คลาส",
                        teacherId is Guid t ? teachers.GetValueOrDefault(t) : null, taken,
                        ClassScheduleGenerator.EffectiveCapacity(s.CapacityOverride, svc?.MaxCapacity ?? 1), wait));
                }
            }
            if (!Want(StaffDailySummaryPlan.Bookings)) bookings = null;
        }

        if ((Want(StaffDailySummaryPlan.MadeToOrder) || Want(StaffDailySummaryPlan.ToShip)) && siteIds.Count > 0)
        {
            var open = new[] { SiteOrderStatus.Pending, SiteOrderStatus.Confirmed, SiteOrderStatus.Processing, SiteOrderStatus.ReadyForPickup };
            var readyLimit = DateTime.SpecifyKind(today.AddDays(8).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var orders = await _db.SiteOrders.AsNoTracking()
                .Where(o => o.CompanyId == companyId && siteIds.Contains(o.SiteId) && open.Contains(o.Status)
                    && (((o.RequestedReadyDate ?? o.EarliestReadyDate) != null && (o.RequestedReadyDate ?? o.EarliestReadyDate) < readyLimit)
                        || o.Status == SiteOrderStatus.Confirmed || o.Status == SiteOrderStatus.Processing))
                .Select(o => new
                {
                    o.SiteId, o.OrderNumber, o.Status, o.FulfillmentMethod, o.TotalAmount, o.PaidAmount,
                    Ready = o.RequestedReadyDate ?? o.EarliestReadyDate,
                    Name = o.ShippingName ?? o.BillingName ?? (o.Customer != null ? o.Customer.FullName : null),
                })
                .OrderBy(o => o.OrderNumber).Take(1000).ToListAsync(ct);
            mto = new();
            ship = new();
            foreach (var o in orders)
            {
                var row = new StaffDailySummaryPlan.OrderRow(o.SiteId, o.OrderNumber, string.IsNullOrWhiteSpace(o.Name) ? "ลูกค้า" : o.Name!,
                    o.Ready is DateTime rd ? DateOnly.FromDateTime(rd) : null,
                    Math.Max(0m, o.TotalAmount - o.PaidAmount), SiteOrderPublic.StatusLabel(o.Status, o.FulfillmentMethod));
                if (o.Ready != null) { if (o.Ready < readyLimit) mto.Add(row); }
                else if (o.Status is SiteOrderStatus.Confirmed or SiteOrderStatus.Processing) ship.Add(row);
            }
            if (!Want(StaffDailySummaryPlan.MadeToOrder)) mto = null;
            if (!Want(StaffDailySummaryPlan.ToShip)) ship = null;
        }

        if (Want(StaffDailySummaryPlan.Lodging) && lodgingActive)
        {
            // วันเข้าพัก/ออก = วันตามปฏิทินไทยที่ 00:00 UTC (Lodging.cs)
            var d0 = DateTime.SpecifyKind(today.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var d1 = d0.AddDays(1);
            var res = await _db.LodgingReservations.AsNoTracking()
                .Where(r => r.CompanyId == companyId
                    && (configSiteId == null || r.Property.SiteId == null || r.Property.SiteId == configSiteId)
                    && ((r.CheckInDate >= d0 && r.CheckInDate < d1
                            && (r.Status == LodgingReservationStatus.Pending || r.Status == LodgingReservationStatus.Confirmed || r.Status == LodgingReservationStatus.CheckedIn))
                        || (r.CheckOutDate >= d0 && r.CheckOutDate < d1
                            && (r.Status == LodgingReservationStatus.Confirmed || r.Status == LodgingReservationStatus.CheckedIn))))
                .Select(r => new { r.ReservationNumber, r.GuestName, Property = r.Property.Name, r.CheckInDate, r.CheckOutDate })
                .Take(500).ToListAsync(ct);
            lodging = res.Select(r => new StaffDailySummaryPlan.LodgingRow(r.Property, r.ReservationNumber, r.GuestName,
                r.CheckInDate >= d0 && r.CheckInDate < d1, !(r.CheckInDate >= d0 && r.CheckInDate < d1))).ToList();
        }

        if (Want(StaffDailySummaryPlan.Credits))
        {
            var d0 = DateTime.SpecifyKind(today.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var d8 = d0.AddDays(8);
            var passes = await _db.CreditPasses.AsNoTracking()
                .Where(p => p.CompanyId == companyId && p.Status == CreditPassStatus.Active && p.ExpiresOn != null && p.ExpiresOn >= d0 && p.ExpiresOn < d8)
                .Select(p => new
                {
                    p.PassNumber, p.PackageName, p.ExpiresOn, p.CreditsTotal, p.CreditsUsed,
                    Name = _db.Contacts.Where(c => c.Id == p.ContactId && c.CompanyId == p.CompanyId).Select(c => c.Name).FirstOrDefault(),
                })
                .Take(500).ToListAsync(ct);
            credits = passes.Select(p => new StaffDailySummaryPlan.CreditRow(p.PassNumber, string.IsNullOrWhiteSpace(p.Name) ? "ลูกค้า" : p.Name!,
                p.PackageName, DateOnly.FromDateTime(p.ExpiresOn!.Value), Math.Max(0m, p.CreditsTotal - p.CreditsUsed))).ToList();
        }

        return new StaffDailySummaryPlan.SummaryData(bookings, classes, mto, ship, lodging, credits);
    }

    // ═════════════════════ ข้อมูลประกอบ ═════════════════════

    private async Task<List<SiteInfo>> CompanySitesAsync(Guid companyId, CancellationToken ct)
        => (await _db.Sites.AsNoTracking().Where(s => s.CompanyId == companyId).OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name }).ToListAsync(ct))
            .Select(s => new SiteInfo(s.Id, s.Name)).ToList();

    /// <summary>สมาชิกที่ยังใช้งานของบริษัท (ตรวจ ณ เวลาที่เรียก — ผู้รับที่ออกจากบริษัทแล้วไม่ได้รับ)</summary>
    private async Task<List<Member>> MembersAsync(Guid companyId, CancellationToken ct)
        => (await _db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == companyId && cu.User.Status == UserStatus.Active && cu.Role != UserRole.PlatformSupport)
                .Select(cu => new { cu.UserId, cu.Role, cu.User.FullName, cu.User.Email, cu.User.LineUserId })
                .ToListAsync(ct))
            .GroupBy(x => x.UserId).Select(g => g.First())
            .Select(x => new Member(x.UserId, x.Role, string.IsNullOrWhiteSpace(x.FullName) ? x.Email : x.FullName, x.LineUserId))
            .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();

    private async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new();
        return (await _db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).Select(u => new { u.Id, u.FullName, u.Email }).ToListAsync(ct))
            .ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName);
    }

    private static StaffDailySummaryMemberDto ToMemberDto(Member m) => new()
    {
        UserId = m.UserId, Name = m.Name, Role = m.Role.ToString(),
        RoleLabel = StaffDailySummaryPlan.RecipientRoles.FirstOrDefault(r => r.Role == m.Role).Label ?? m.Role.ToString(),
        LineBound = !string.IsNullOrWhiteSpace(m.LineUserId),
    };

    private static StaffDailySummaryConfigDto ToDto(StaffDailySummaryConfig c, List<SiteInfo> sites, List<Member> members)
    {
        var userIds = StaffDailySummaryPlan.ParseUserIds(c.RecipientUserIds);
        var roles = StaffDailySummaryPlan.ParseRoles(c.RecipientRoles);
        var resolved = StaffDailySummaryPlan.ResolveRecipients(members.Select(m => (m.UserId, m.Role)), userIds, roles);
        return new StaffDailySummaryConfigDto
        {
            Id = c.Id, SiteId = c.SiteId, SiteName = c.SiteId is Guid s ? sites.FirstOrDefault(x => x.Id == s)?.Name : null,
            Enabled = c.Enabled, SendAt = c.SendAtLocal.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            Sections = StaffDailySummaryPlan.ParseSections(c.Sections).ToList(),
            RecipientUserIds = userIds.ToList(), RecipientRoles = roles.Select(r => r.ToString()).ToList(),
            Recipients = members.Where(m => resolved.Contains(m.UserId)).Select(ToMemberDto).ToList(),
            LastSentLocalDate = c.LastSentLocalDate, LastRunNote = c.LastRunNote, LastRunAt = c.LastRunAt,
        };
    }
}
