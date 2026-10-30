using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>รอบ 214 (คำตัดสินข้อ 173) — ดู <see cref="ILegalDocumentService"/> · ตาราง <c>LegalDocumentVersions</c> เป็นระดับแพลตฟอร์ม
/// (ไม่มี CompanyId — เอกสารของ NextAcc เอง ไม่ใช่ของ tenant) · เขียนได้เฉพาะผ่าน controller ที่บังคับ SystemAdmin</summary>
public class LegalDocumentService : ILegalDocumentService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<LegalDocumentService> _logger;

    public LegalDocumentService(AccountingDbContext db, ILogger<LegalDocumentService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private static string RequireKind(string kind)
    {
        var k = (kind ?? "").Trim().ToLowerInvariant();
        if (!LegalDocumentDefaults.IsKind(k))
            throw new BusinessRuleException("ชนิดเอกสารต้องเป็น privacy หรือ terms", LegalDocumentPolicy.RuleCode);
        return k;
    }

    private static DateOnly Today => LegalDocumentPolicy.TodayBangkok(DateTime.UtcNow);

    private static LegalDocumentView BuiltIn(string kind) => new(
        kind, LegalDocumentDefaults.Title(kind), LegalDocumentDefaults.BuiltInVersion, LegalDocumentDefaults.BuiltInEffectiveDate,
        LegalDocumentDefaults.PlatformBuiltIn(kind), "builtin", null, null);

    private static LegalDocumentView ToView(LegalDocumentVersion r) => new(
        r.Kind, LegalDocumentDefaults.Title(r.Kind), r.Version, DateOnly.FromDateTime(r.EffectiveDate), r.BodyMarkdown,
        "published", r.PublishedAt, r.ChangeSummary);

    private Task<List<LegalDocumentVersion>> PublishedRowsAsync(string kind, CancellationToken ct)
        => _db.LegalDocumentVersions.AsNoTracking()
            .Where(x => x.Kind == kind && x.Status == LegalDocumentPolicy.StatusPublished)
            .ToListAsync(ct);

    private static LegalDocumentPolicy.VersionRow Row(LegalDocumentVersion r)
        => new(r.Version, DateOnly.FromDateTime(r.EffectiveDate), r.Status, r.PublishedAt);

    public async Task<LegalDocumentView> GetCurrentAsync(string kind, CancellationToken ct = default)
    {
        var k = RequireKind(kind);
        var rows = await PublishedRowsAsync(k, ct);
        var pick = LegalDocumentPolicy.PickCurrent(rows.Select(Row), Today);
        if (pick == null) return BuiltIn(k);
        return ToView(rows.First(r => r.Version == pick.Version));
    }

    public async Task<LegalDocumentView?> GetUpcomingAsync(string kind, CancellationToken ct = default)
    {
        var k = RequireKind(kind);
        var rows = await PublishedRowsAsync(k, ct);
        var pick = LegalDocumentPolicy.PickUpcoming(rows.Select(Row), Today);
        return pick == null ? null : ToView(rows.First(r => r.Version == pick.Version));
    }

    public async Task<LegalDocumentView?> GetPublishedVersionAsync(string kind, string version, CancellationToken ct = default)
    {
        var k = RequireKind(kind);
        var v = (version ?? "").Trim();
        var row = await _db.LegalDocumentVersions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Kind == k && x.Version == v && x.Status == LegalDocumentPolicy.StatusPublished, ct);
        if (row != null) return ToView(row);
        return v == LegalDocumentDefaults.BuiltInVersion ? BuiltIn(k) : null;
    }

    public async Task<LegalConsentVersion> GetConsentVersionAsync(CancellationToken ct = default)
    {
        var p = await GetCurrentAsync(LegalDocumentDefaults.KindPrivacy, ct);
        var t = await GetCurrentAsync(LegalDocumentDefaults.KindTerms, ct);
        return new LegalConsentVersion(p.Version, t.Version, LegalDocumentPolicy.ConsentVersion(p.Version, t.Version));
    }

    public async Task<string> RenderPlatformAsync(string markdown, CancellationToken ct = default)
    {
        var s = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        // ตัวตั้งเดียวกับ LegalController.GetPolicy: ชื่อนิติบุคคลผู้ให้บริการ → ชื่อเว็บ · อีเมลผู้ขาย → อีเมลติดต่อทั่วไป
        var tokens = LegalDocumentDefaults.BuildPlatformTokens(
            FirstNonEmpty(s?.PlatformSellerName, s?.SiteName), s?.PlatformSellerTaxId, s?.PlatformSellerAddress,
            FirstNonEmpty(s?.PlatformSellerPhone, s?.ContactPhone), FirstNonEmpty(s?.PlatformSellerEmail, s?.ContactEmail), s?.SiteName);
        return LegalMarkdown.Render(markdown, tokens);
    }

    internal static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public async Task<List<LegalHistoryItem>> GetHistoryAsync(string kind, CancellationToken ct = default)
    {
        var k = RequireKind(kind);
        var rows = await PublishedRowsAsync(k, ct);
        var today = Today;
        var cur = LegalDocumentPolicy.PickCurrent(rows.Select(Row), today);
        var up = LegalDocumentPolicy.PickUpcoming(rows.Select(Row), today);
        var list = rows
            .OrderByDescending(r => r.EffectiveDate)
            .ThenByDescending(r => r.Version, Comparer<string>.Create(LegalDocumentPolicy.CompareVersions))
            .Select(r => new LegalHistoryItem(r.Version, DateOnly.FromDateTime(r.EffectiveDate), r.PublishedAt, r.PublishedBy, r.ChangeSummary,
                IsCurrent: cur != null && cur.Version == r.Version, IsUpcoming: up != null && up.Version == r.Version, IsBuiltIn: false))
            .ToList();
        // ฉบับตั้งต้นอยู่ท้ายประวัติเสมอ (เป็นฉบับที่ผู้ใช้ก่อนรอบ 214 ยอมรับ) — ยังไม่ถูกแทน = ฉบับปัจจุบัน
        list.Add(new LegalHistoryItem(LegalDocumentDefaults.BuiltInVersion, LegalDocumentDefaults.BuiltInEffectiveDate, null, null,
            "ฉบับตั้งต้นของระบบ", IsCurrent: cur == null, IsUpcoming: false, IsBuiltIn: true));
        return list;
    }

    private Task<LegalDocumentVersion?> DraftRowAsync(string kind, CancellationToken ct)
        => _db.LegalDocumentVersions.FirstOrDefaultAsync(x => x.Kind == kind && x.Status == LegalDocumentPolicy.StatusDraft, ct);

    private static LegalDraftView DraftView(LegalDocumentVersion r) => new(
        r.Id, r.Version, DateOnly.FromDateTime(r.EffectiveDate), r.BodyMarkdown, r.ChangeSummary, r.CreatedAt, r.UpdatedAt, r.UpdatedBy ?? r.CreatedBy);

    public async Task<LegalDraftView?> GetDraftAsync(string kind, CancellationToken ct = default)
    {
        var k = RequireKind(kind);
        var r = await _db.LegalDocumentVersions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Kind == k && x.Status == LegalDocumentPolicy.StatusDraft, ct);
        return r == null ? null : DraftView(r);
    }

    public async Task<LegalDraftView> SaveDraftAsync(string kind, LegalDraftInput input, string actor, CancellationToken ct = default)
    {
        var k = RequireKind(kind);
        var body = input.BodyMarkdown ?? "";
        if (body.Length > LegalMarkdown.MaxLength)
            throw new BusinessRuleException($"เนื้อความยาวเกิน {LegalMarkdown.MaxLength:N0} ตัวอักษร", LegalDocumentPolicy.RuleCode);
        var version = (input.Version ?? "").Trim();
        // ร่างเก็บได้แม้ยังไม่ใส่เลข — แต่ถ้าใส่ ต้องเป็นรูปแบบที่เผยแพร่ได้ (บอกตั้งแต่ตอนบันทึก ไม่ใช่รอไปล้มตอนเผยแพร่)
        if (version.Length > 0 && !LegalDocumentPolicy.IsValidVersionLabel(version))
            throw new BusinessRuleException("เลขฉบับต้องเป็นตัวเลขคั่นด้วยจุด เช่น 1.1 หรือ 2.0", LegalDocumentPolicy.RuleCode);
        // เลขที่ชนฉบับเผยแพร่แล้ว — unique index จะล้มตอนบันทึก บอกเป็นภาษาคนก่อน
        if (version.Length > 0 && await _db.LegalDocumentVersions.AnyAsync(
                x => x.Kind == k && x.Version == version && x.Status == LegalDocumentPolicy.StatusPublished, ct))
            throw new BusinessRuleException($"ฉบับ {version} เผยแพร่ไปแล้ว — ฉบับที่เผยแพร่แล้วแก้ไม่ได้ ใช้เลขฉบับใหม่", LegalDocumentPolicy.RuleCode);

        var eff = input.EffectiveDate ?? Today;
        var summary = string.IsNullOrWhiteSpace(input.ChangeSummary) ? null : input.ChangeSummary.Trim();
        if (summary is { Length: > 500 }) summary = summary[..500];

        var row = await DraftRowAsync(k, ct);
        if (row == null)
        {
            row = new LegalDocumentVersion { Kind = k, Status = LegalDocumentPolicy.StatusDraft, CreatedBy = actor };
            _db.LegalDocumentVersions.Add(row);
        }
        else
        {
            // ด่านซ้ำชั้นใน: แถวที่ดึงมาต้องเป็นร่าง (DraftRowAsync กรองแล้ว — กันการเปลี่ยนในอนาคต)
            if (!LegalDocumentPolicy.CanEdit(row.Status))
                throw new BusinessRuleException("ฉบับที่เผยแพร่แล้วแก้ไม่ได้", LegalDocumentPolicy.RuleCode);
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = actor;
        }
        row.Version = version;
        row.EffectiveDate = eff.ToDateTime(TimeOnly.MinValue);
        row.BodyMarkdown = body;
        row.ChangeSummary = summary;
        await SaveGuardedAsync(ct);
        return DraftView(row);
    }

    public async Task<LegalHistoryItem> PublishDraftAsync(string kind, string actor, string? ipAddress = null, CancellationToken ct = default)
    {
        var k = RequireKind(kind);
        var row = await DraftRowAsync(k, ct)
            ?? throw new BusinessRuleException("ยังไม่มีร่าง — บันทึกร่างก่อนเผยแพร่", LegalDocumentPolicy.RuleCode);
        var published = (await PublishedRowsAsync(k, ct)).Select(Row).ToList();
        var err = LegalDocumentPolicy.ValidatePublish(row.Version, DateOnly.FromDateTime(row.EffectiveDate), row.BodyMarkdown, published, Today);
        if (err != null) throw new BusinessRuleException(err, LegalDocumentPolicy.RuleCode);

        row.Status = LegalDocumentPolicy.StatusPublished;
        row.PublishedAt = DateTime.UtcNow;
        row.PublishedBy = actor;
        row.UpdatedAt = row.PublishedAt;
        row.UpdatedBy = actor;
        // ร่องรอยเชิงกฎหมาย (นอกเหนือ audit อัตโนมัติของ BaseEntity) — ระบุเลขฉบับ/วันมีผล + ฐานกฎหมาย
        // ฝ่ายค้านรอบ 214 S5: ผู้เผยแพร่ระบุตัวได้ในแถว audit เอง (UserId · อีเมล · IP) ไม่ใช่แค่ข้อความใน NewValues
        Guid? actorId = Guid.TryParse(actor, out var aid) ? aid : null;
        var actorEmail = actorId is Guid a2
            ? await _db.Users.AsNoTracking().Where(u => u.Id == a2).Select(u => u.Email).FirstOrDefaultAsync(ct)
            : null;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = PdpaPolicy.PlatformScopeCompanyId,
            UserId = actorId,
            UserEmail = actorEmail,
            IpAddress = ipAddress is { Length: > 45 } ? ipAddress[..45] : ipAddress,
            Action = AuditAction.Update,
            EntityType = nameof(LegalDocumentVersion),
            EntityId = row.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "LegalDocumentPublished", kind = k, version = row.Version,
                effectiveDate = row.EffectiveDate.ToString("yyyy-MM-dd"), by = actor,
                ruleCode = LegalDocumentPolicy.RuleCode, legalReference = "PDPA ม.19 · ม.23",
            }),
        });
        await SaveGuardedAsync(ct);
        _logger.LogWarning("เผยแพร่{Title} ฉบับ {Version} (มีผล {Eff:yyyy-MM-dd}) โดย {Actor}",
            LegalDocumentDefaults.Title(k), row.Version, row.EffectiveDate, actor);
        var today = Today;
        var eff = DateOnly.FromDateTime(row.EffectiveDate);
        return new LegalHistoryItem(row.Version, eff, row.PublishedAt, actor, row.ChangeSummary,
            IsCurrent: eff <= today, IsUpcoming: eff > today, IsBuiltIn: false);
    }

    public async Task<bool> DiscardDraftAsync(string kind, string actor, CancellationToken ct = default)
    {
        var k = RequireKind(kind);
        var row = await DraftRowAsync(k, ct);
        if (row == null) return false;
        if (!LegalDocumentPolicy.CanEdit(row.Status))
            throw new BusinessRuleException("ฉบับที่เผยแพร่แล้วลบไม่ได้", LegalDocumentPolicy.RuleCode);
        // ร่างลบจริงได้ (ยังไม่เคยมีใครเห็น) — แต่ soft delete ⇒ audit อัตโนมัติยังเห็นร่องรอย · เลขฉบับถูกปล่อยเพราะ unique index กรอง IsDeleted
        row.IsDeleted = true;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = actor;
        await SaveGuardedAsync(ct);
        return true;
    }

    /// <summary>ฝ่ายค้านรอบ 214 (หมายเหตุ): สองแท็บกดพร้อมกัน — เผยแพร่ซ้ำ (Status เป็น concurrency token: UPDATE … WHERE Status='Draft') /
    /// ร่างแรกชนกัน (unique ร่าง 1 ฉบับต่อชนิด · เลขฉบับซ้ำ) ⇒ ข้อความไทย 409 ไม่ใช่ 500</summary>
    private async Task SaveGuardedAsync(CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new BusinessRuleException("ร่างนี้ถูกเผยแพร่/แก้จากอีกหน้าต่างไปแล้ว — โหลดหน้าใหม่", LegalDocumentPolicy.RuleCode, 409);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            throw new BusinessRuleException("มีร่าง/ฉบับเลขนี้อยู่แล้ว (อาจบันทึกพร้อมกันจากอีกหน้าต่าง) — โหลดหน้าใหม่", LegalDocumentPolicy.RuleCode, 409);
        }
    }

    public async Task<LegalAcceptanceStatus> GetAcceptanceStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var p = await GetCurrentAsync(LegalDocumentDefaults.KindPrivacy, ct);
        var t = await GetCurrentAsync(LegalDocumentDefaults.KindTerms, ct);
        var current = LegalDocumentPolicy.ConsentVersion(p.Version, t.Version);
        var purposes = PdpaPolicy.TermsAcceptancePurposes.ToList();
        // รอบ 214 หลัง CI: หลักฐานระดับผู้ใช้อยู่ที่ PlatformPolicyAcceptances (ไม่ผูกบริษัท) — แถวเดิมถูกคัดลอกมาแล้ว (LegalDocumentMigration)
        var last = await _db.PlatformPolicyAcceptances.AsNoTracking()
            .Where(c => c.UserId == userId && purposes.Contains(c.Purpose) && c.WithdrawnAt == null)
            .OrderByDescending(c => c.AcceptedAt)
            .Select(c => new { c.PolicyVersion, c.AcceptedAt })
            .FirstOrDefaultAsync(ct);
        return new LegalAcceptanceStatus(current, last?.PolicyVersion, last?.AcceptedAt,
            LegalDocumentPolicy.NeedsAcknowledge(last?.PolicyVersion, current),
            p.Version, t.Version, p.ChangeSummary, t.ChangeSummary);
    }

    public async Task<LegalAcceptanceStatus> RecordAcceptanceAsync(Guid userId, string? versionShown, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var email = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("ไม่พบผู้ใช้", LegalDocumentPolicy.RuleCode, 404);
        var current = (await GetConsentVersionAsync(ct)).ConsentVersion;
        // หน้าเว็บค้างฉบับเก่า (แท็บเปิดไว้ข้ามวันเผยแพร่) ⇒ ไม่บันทึกว่ายอมรับฉบับใหม่ที่ผู้ใช้ยังไม่เห็น — ให้โหลดใหม่
        if (!string.IsNullOrWhiteSpace(versionShown) && versionShown.Trim() != current)
            throw new BusinessRuleException("นโยบายมีฉบับใหม่กว่าที่หน้านี้แสดง — โหลดหน้าใหม่แล้วอ่านอีกครั้ง", LegalDocumentPolicy.RuleCode, 409);
        // รอบ 214 ต่อ (ฎ): ตัวสร้างแถวเดียวกับตอนสมัคร (เวลา normalize · ทุกช่องใน canonical เก็บในแถว)
        _db.PlatformPolicyAcceptances.Add(PlatformPolicyAcceptanceRules.Create(
            userId, email, PdpaPolicy.PolicyUpdatePurpose, current, versionShown ?? current, DateTime.UtcNow, "policy-update-banner", ip, userAgent));
        await _db.SaveChangesAsync(ct);
        return await GetAcceptanceStatusAsync(userId, ct);
    }
}
