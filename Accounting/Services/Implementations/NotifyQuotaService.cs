using System.Data.Common;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services.Implementations;

/// <summary>
/// โควตาข้อความผ่านช่องกลาง + กันส่งซ้ำต่อข้อความ — รอบ 212 แพ็ก A (คำตัดสินข้อ 171 · design §3.2) ·
/// กติกาทั้งหมดอยู่ที่ <see cref="NotifyQuotaPolicy"/> · ที่นี่ทำแค่ "นับให้ถูกเมื่อหลาย instance ชนกัน":
/// <list type="bullet">
/// <item>ตัวนับ: <c>UPDATE "NotifyUsageCounters" SET "Used" = "Used" + 1 WHERE … AND "Used" &lt; @limit RETURNING</c> (ADO.NET ตรงแบบ R-05 · ไม่อ่าน-แก้-เขียน)</item>
/// <item>กันซ้ำ: แถว <c>CustomerNotifyLogs</c> unique (CompanyId, MessageKey) + lease 2 นาที (คำขอที่ล้มกลางทาง ⇒ คำขอถัดไปรับช่วงได้)</item>
/// <item>ทุกคำสั่งนับ/claim ใช้ DbContext ของ scope ใหม่ (มี <see cref="IServiceScopeFactory"/>) — ไม่ผูกธุรกรรมของผู้เรียก</item>
/// <item>ฐานล้มระหว่างนับ ⇒ log Error แล้ว<b>ส่งต่อ</b> (ข้อความถึงลูกค้าสำคัญกว่าตัวนับ · ไม่บล็อกงานธุรกิจ)</item>
/// </list>
/// </summary>
public class NotifyQuotaService : INotifyQuotaService
{
    public const string KindPlan = "plan";
    public const string KindBonus = "bonus";
    public const string KindEssential = "essential";
    public const string KindFull = "full";
    public const string KindUntracked = "untracked";
    /// <summary>อายุ claim — คำขอที่ค้าง/ล้มกลางทางเกินนี้ ⇒ คำขอถัดไปรับช่วงได้ ·
    /// ฝ่ายค้านรอบ 212 (a): ต้องยาวกว่าผลรวม timeout ของทุกขั้น (LINE push + SMTP ร้าน + SMTP ระบบ) — ไม่งั้นคำขอที่ยังส่งอยู่ถูกรับช่วงแล้วส่งซ้ำ</summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);

    private readonly AccountingDbContext _db;
    private readonly ILogger<NotifyQuotaService> _logger;
    private readonly IServiceScopeFactory? _scopes;
    private readonly IUsageMeteringService? _metering;
    private readonly INotificationEngine? _engine;
    private readonly INotificationService? _bell;

    public NotifyQuotaService(AccountingDbContext db, ILogger<NotifyQuotaService> logger, IServiceScopeFactory? scopes = null,
        IUsageMeteringService? metering = null, INotificationEngine? engine = null, INotificationService? bell = null)
    {
        _db = db;
        _logger = logger;
        _scopes = scopes;
        _metering = metering;
        _engine = engine;
        _bell = bell;
    }

    // ───────────────────────── ฐานแยก + ADO ─────────────────────────

    private async Task<T> IsolatedAsync<T>(Func<AccountingDbContext, Task<T>> work)
    {
        if (_scopes == null) return await work(_db);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        return await work(db);
    }

    private static NpgsqlParameter P(string name, object? value, NpgsqlDbType type)
        => new(name, type) { Value = value ?? DBNull.Value };

    private static async Task<DbCommand> CommandAsync(AccountingDbContext db, string sql, CancellationToken ct, params NpgsqlParameter[] ps)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var p in ps) cmd.Parameters.Add(p);
        return cmd;
    }

    private static async Task<int?> ScalarIntAsync(AccountingDbContext db, string sql, CancellationToken ct, params NpgsqlParameter[] ps)
    {
        await using var cmd = await CommandAsync(db, sql, ct, ps);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r == null || r is DBNull ? null : Convert.ToInt32(r);
    }

    private static async Task<int> ExecAsync(AccountingDbContext db, string sql, CancellationToken ct, params NpgsqlParameter[] ps)
    {
        await using var cmd = await CommandAsync(db, sql, ct, ps);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ───────────────────────── กันส่งซ้ำ ─────────────────────────

    public async Task<NotifyClaim> ClaimAsync(Guid companyId, string? messageKey, string eventKey, string? targetKind, Guid? targetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageKey)) return NotifyClaim.Untracked;
        var key = messageKey.Length > 200 ? messageKey[..200] : messageKey;
        try
        {
            return await IsolatedAsync(async db =>
            {
                const string insert = """
                    INSERT INTO "CustomerNotifyLogs" ("Id","CompanyId","MessageKey","EventKey","TargetKind","TargetId","Metered","Status","Attempts","CreatedAt","IsDeleted")
                    VALUES (@id, @c, @k, @e, @tk, @ti, false, 'Pending', 0, now(), false)
                    ON CONFLICT ("CompanyId","MessageKey") DO NOTHING;
                    """;
                await ExecAsync(db, insert, ct,
                    P("id", Guid.NewGuid(), NpgsqlDbType.Uuid), P("c", companyId, NpgsqlDbType.Uuid), P("k", key, NpgsqlDbType.Varchar),
                    P("e", eventKey, NpgsqlDbType.Varchar), P("tk", targetKind, NpgsqlDbType.Varchar), P("ti", targetId, NpgsqlDbType.Uuid));

                // claim แบบมีเงื่อนไข: ยังไม่ส่ง + lease ว่าง/หมดอายุ — นับแถว 0 = ส่งแล้ว หรือคำขออื่นกำลังส่ง
                const string claim = """
                    UPDATE "CustomerNotifyLogs"
                    SET "Status" = 'Claimed', "ClaimedUntil" = now() + make_interval(secs => @lease), "Attempts" = "Attempts" + 1, "UpdatedAt" = now()
                    WHERE "CompanyId" = @c AND "MessageKey" = @k AND "Status" <> 'Sent'
                      AND ("ClaimedUntil" IS NULL OR "ClaimedUntil" < now())
                    RETURNING "Id";
                    """;
                await using (var cmd = await CommandAsync(db, claim, ct,
                    P("lease", ClaimLease.TotalSeconds, NpgsqlDbType.Double), P("c", companyId, NpgsqlDbType.Uuid), P("k", key, NpgsqlDbType.Varchar)))
                {
                    var r = await cmd.ExecuteScalarAsync(ct);
                    if (r is Guid id) return new NotifyClaim(true, false, id, null);
                }
                var prev = await db.CustomerNotifyLogs.AsNoTracking()
                    .Where(l => l.CompanyId == companyId && l.MessageKey == key)
                    .Select(l => new { l.Status, l.Channel }).FirstOrDefaultAsync(ct);
                return prev?.Status == NotifyLogStatus.Sent
                    ? new NotifyClaim(false, true, null, prev.Channel)
                    : new NotifyClaim(false, false, null, null);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "claim คีย์ข้อความไม่สำเร็จ (company {CompanyId} event {EventKey}) — ส่งต่อโดยไม่กันซ้ำ", companyId, eventKey);
            return NotifyClaim.Untracked;
        }
    }

    public async Task CompleteAsync(Guid companyId, Guid? logId, string status, string? channel, string? source, bool metered, string? note, CancellationToken ct = default)
    {
        if (logId is not Guid id) return;
        // ฝ่ายค้านรอบ 212 S3: ปิดบังอีเมล/เบอร์/LINE userId ในหมายเหตุก่อนเก็บ (ตัวเดียว CustomerNotifyLogRule.Redact)
        var trimmed = CustomerNotifyLogRule.Redact(note);
        try
        {
            await IsolatedAsync(async db =>
            {
                const string sql = """
                    UPDATE "CustomerNotifyLogs"
                    SET "Status" = @s, "Channel" = @ch, "Source" = @src, "Metered" = @m, "Note" = @n, "ClaimedUntil" = NULL,
                        "SentAt" = CASE WHEN @s = 'Sent' THEN now() ELSE "SentAt" END, "UpdatedAt" = now()
                    WHERE "Id" = @id AND "CompanyId" = @c;
                    """;
                return await ExecAsync(db, sql, ct,
                    P("s", status, NpgsqlDbType.Varchar), P("ch", channel, NpgsqlDbType.Varchar), P("src", source, NpgsqlDbType.Varchar),
                    P("m", metered, NpgsqlDbType.Boolean), P("n", trimmed, NpgsqlDbType.Varchar),
                    P("id", id, NpgsqlDbType.Uuid), P("c", companyId, NpgsqlDbType.Uuid));
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // แถวค้าง Claimed ⇒ lease หมดแล้วคำขอถัดไปรับช่วงได้ (ไม่ค้างถาวร)
            _logger.LogError(ex, "ปิดแถวบันทึกการส่ง {LogId} ไม่สำเร็จ (company {CompanyId})", id, companyId);
        }
    }

    // ───────────────────────── โควตา ─────────────────────────

    /// <summary>เพดานของบริษัท + โบนัส (อ่านอย่างเดียว) — ผู้ตัดสินเพดานตัวเดียว: <see cref="NotifyQuotaPolicy.PlanLimit"/></summary>
    private static async Task<(int PlanLimit, int BonusRemaining, DateTime? BonusExpiresAt)> LoadLimitAsync(AccountingDbContext db, Guid companyId, CancellationToken ct)
    {
        var sub = await db.Subscriptions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new { s.Plan, s.Status, s.MaxSharedMessagesPerMonth, s.NotifyBonusQuota, s.NotifyBonusExpiresAt, s.AccountSubscriptionId })
            .FirstOrDefaultAsync(ct);
        if (sub == null) return (NotifyQuotaPolicy.Unlimited, 0, null);

        // แม่แบบแพ็กเกจที่มีผล: กลุ่มบัญชี ⇒ แม่แบบของกลุ่ม (นับต่อบริษัท ไม่ pool) · ไม่งั้น ⇒ แม่แบบตามระดับแพ็กเกจของบริษัท
        int? tplLimit = null, tplTrial = null;
        var trial = sub.Status == SubscriptionStatus.Trial;
        var fromAccount = false;
        if (sub.AccountSubscriptionId is Guid aid)
        {
            var acct = await db.AccountSubscriptions.AsNoTracking()
                .Where(a => a.Id == aid && !a.IsDeleted)
                .Select(a => new { a.Status, a.PlanTemplate.MaxSharedMessagesPerMonth, a.PlanTemplate.TrialMaxSharedMessagesPerMonth })
                .FirstOrDefaultAsync(ct);
            if (acct != null)
            {
                fromAccount = true;
                trial = acct.Status == SubscriptionStatus.Trial;
                tplLimit = acct.MaxSharedMessagesPerMonth;
                tplTrial = acct.TrialMaxSharedMessagesPerMonth;
            }
        }
        if (!fromAccount)
        {
            var tpl = await db.PlanTemplates.AsNoTracking()
                .Where(p => p.Plan == sub.Plan && p.IsActive && !p.IsDeleted)
                .OrderBy(p => p.CreatedAt)
                .Select(p => new { p.MaxSharedMessagesPerMonth, p.TrialMaxSharedMessagesPerMonth })
                .FirstOrDefaultAsync(ct);
            tplLimit = tpl?.MaxSharedMessagesPerMonth;
            tplTrial = tpl?.TrialMaxSharedMessagesPerMonth;
        }
        return (NotifyQuotaPolicy.PlanLimit(sub.MaxSharedMessagesPerMonth, tplLimit, tplTrial, trial), sub.NotifyBonusQuota, sub.NotifyBonusExpiresAt);
    }

    private static Task<int> EnsureCounterAsync(AccountingDbContext db, Guid companyId, string period, CancellationToken ct)
        => ExecAsync(db, """
            INSERT INTO "NotifyUsageCounters" ("CompanyId","Period","Used","BonusUsed") VALUES (@c, @p, 0, 0)
            ON CONFLICT ("CompanyId","Period") DO NOTHING;
            """, ct, P("c", companyId, NpgsqlDbType.Uuid), P("p", period, NpgsqlDbType.Varchar));

    public async Task<NotifyReservation> TryReserveAsync(Guid companyId, bool essential, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var period = NotifyQuotaPolicy.PeriodOf(now);
        try
        {
            return await IsolatedAsync(async db =>
            {
                var (planLimit, bonusRemaining, bonusExp) = await LoadLimitAsync(db, companyId, ct);
                await EnsureCounterAsync(db, companyId, period, ct);
                var c = P("c", companyId, NpgsqlDbType.Uuid);
                var p = P("p", period, NpgsqlDbType.Varchar);

                // 1) เพดานแพ็กเกจ (ไม่จำกัด = ไม่มีเงื่อนไข) — atomic: สองคำขอที่ Used = limit-1 ผ่านได้คำขอเดียว
                int? after;
                if (planLimit < 0)
                    after = await ScalarIntAsync(db, """
                        UPDATE "NotifyUsageCounters" SET "Used" = "Used" + 1 WHERE "CompanyId" = @c AND "Period" = @p
                        RETURNING "Used" + "BonusUsed";
                        """, ct, c, p);
                else
                    after = await ScalarIntAsync(db, """
                        UPDATE "NotifyUsageCounters" SET "Used" = "Used" + 1 WHERE "CompanyId" = @c AND "Period" = @p AND "Used" < @limit
                        RETURNING "Used" + "BonusUsed";
                        """, ct, P("c", companyId, NpgsqlDbType.Uuid), P("p", period, NpgsqlDbType.Varchar), P("limit", planLimit, NpgsqlDbType.Integer));
                if (after is int planAfter)
                    return Reserved(KindPlan, period, planLimit, planAfter, bonusRemaining, bonusExp, now);

                // 2) เกินเพดานแพ็กเกจ ⇒ โบนัสที่ซื้อ (มีเหลือ + ยังไม่หมดอายุ) — atomic เช่นกัน
                var bonusAfter = await ScalarIntAsync(db, """
                    UPDATE "Subscriptions" SET "NotifyBonusQuota" = "NotifyBonusQuota" - 1
                    WHERE "CompanyId" = @c AND "IsDeleted" = false AND "NotifyBonusQuota" > 0
                      AND ("NotifyBonusExpiresAt" IS NULL OR "NotifyBonusExpiresAt" > now())
                    RETURNING "NotifyBonusQuota";
                    """, ct, P("c", companyId, NpgsqlDbType.Uuid));
                if (bonusAfter is int left)
                {
                    var used = await ScalarIntAsync(db, """
                        UPDATE "NotifyUsageCounters" SET "BonusUsed" = "BonusUsed" + 1 WHERE "CompanyId" = @c AND "Period" = @p
                        RETURNING "Used" + "BonusUsed";
                        """, ct, P("c", companyId, NpgsqlDbType.Uuid), P("p", period, NpgsqlDbType.Varchar)) ?? 0;
                    return Reserved(KindBonus, period, planLimit, used, left, bonusExp, now);
                }

                // 3) ข้อความจำเป็น ⇒ นับเกินเพดาน (ไม่ระงับ)
                if (essential)
                {
                    var used = await ScalarIntAsync(db, """
                        UPDATE "NotifyUsageCounters" SET "Used" = "Used" + 1 WHERE "CompanyId" = @c AND "Period" = @p
                        RETURNING "Used" + "BonusUsed";
                        """, ct, P("c", companyId, NpgsqlDbType.Uuid), P("p", period, NpgsqlDbType.Varchar)) ?? 0;
                    return new NotifyReservation(true, KindEssential, period, planLimit, 0, used);
                }

                // 4) เต็ม — แจ้งเจ้าของระดับ 100% (ครั้งเดียวต่องวด · AlertIfCrossedAsync ประทับกันซ้ำ)
                return new NotifyReservation(false, KindFull, period, planLimit, 2, planLimit);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "นับโควตาข้อความช่องกลางไม่สำเร็จ (company {CompanyId}) — ส่งต่อโดยไม่นับ", companyId);
            return new NotifyReservation(true, KindUntracked, period, NotifyQuotaPolicy.Unlimited, 0, 0);
        }
    }

    private static NotifyReservation Reserved(string kind, string period, int planLimit, int usedAfter, int bonusRemaining, DateTime? bonusExp, DateTime now)
    {
        if (planLimit < 0) return new NotifyReservation(true, kind, period, NotifyQuotaPolicy.Unlimited, 0, usedAfter);
        // เพดานรวม = แพ็กเกจ + โบนัสที่ใช้แล้ว + โบนัสที่เหลือ (= ใช้ได้ทั้งหมดงวดนี้) · ระดับเตือนเทียบกับเพดานรวม
        var bonusUsed = Math.Max(0, usedAfter - planLimit);
        var limit = NotifyQuotaPolicy.EffectiveLimit(planLimit, bonusUsed, bonusRemaining, bonusExp, now);
        return new NotifyReservation(true, kind, period, limit, NotifyQuotaPolicy.CrossedLevel(usedAfter - 1, usedAfter, limit), usedAfter);
    }

    public async Task ReleaseAsync(Guid companyId, NotifyReservation reservation, CancellationToken ct = default)
    {
        if (!reservation.Ok || reservation.Kind is KindUntracked or KindFull) return;
        try
        {
            await IsolatedAsync(async db =>
            {
                if (reservation.Kind == KindBonus)
                {
                    await ExecAsync(db, """UPDATE "Subscriptions" SET "NotifyBonusQuota" = "NotifyBonusQuota" + 1 WHERE "CompanyId" = @c AND "IsDeleted" = false;""",
                        ct, P("c", companyId, NpgsqlDbType.Uuid));
                    return await ExecAsync(db, """
                        UPDATE "NotifyUsageCounters" SET "BonusUsed" = GREATEST("BonusUsed" - 1, 0) WHERE "CompanyId" = @c AND "Period" = @p;
                        """, ct, P("c", companyId, NpgsqlDbType.Uuid), P("p", reservation.Period, NpgsqlDbType.Varchar));
                }
                return await ExecAsync(db, """
                    UPDATE "NotifyUsageCounters" SET "Used" = GREATEST("Used" - 1, 0) WHERE "CompanyId" = @c AND "Period" = @p;
                    """, ct, P("c", companyId, NpgsqlDbType.Uuid), P("p", reservation.Period, NpgsqlDbType.Varchar));
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "คืนหน่วยโควตาข้อความไม่สำเร็จ (company {CompanyId} period {Period})", companyId, reservation.Period);
        }
    }

    public async Task AlertIfCrossedAsync(Guid companyId, NotifyReservation reservation, CancellationToken ct = default)
    {
        if (reservation.CrossedLevel <= 0 || reservation.Limit < 0) return;
        try
        {
            var column = reservation.CrossedLevel >= 2 ? "\"Warned100At\"" : "\"Warned80At\"";
            var stamped = await IsolatedAsync(async db =>
            {
                await EnsureCounterAsync(db, companyId, reservation.Period, ct);
                return await ExecAsync(db,
                    $"UPDATE \"NotifyUsageCounters\" SET {column} = now() WHERE \"CompanyId\" = @c AND \"Period\" = @p AND {column} IS NULL;",
                    ct, P("c", companyId, NpgsqlDbType.Uuid), P("p", reservation.Period, NpgsqlDbType.Varchar));
            });
            if (stamped != 1) return;   // งวดนี้แจ้งระดับนี้ไปแล้ว (หรือคำขออื่นแจ้งพร้อมกัน)
            var owner = await IsolatedAsync(db => db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner)
                .Select(cu => cu.UserId).FirstOrDefaultAsync(ct));
            if (owner == Guid.Empty) return;
            var title = NotifyQuotaPolicy.CrossedTitle(reservation.CrossedLevel);
            var message = NotifyQuotaPolicy.CrossedMessage(reservation.CrossedLevel, reservation.UsedAfter, reservation.Limit);
            var eventKey = reservation.CrossedLevel >= 2 ? NotificationEvents.NotifyQuota100 : NotificationEvents.NotifyQuota80;
            if (_engine != null)
                await _engine.DispatchAsync(companyId, eventKey, new NotificationContext
                {
                    Title = title, Message = message, ActionUrl = "/pages/addons.html", EntityType = "Subscription",
                    BellType = NotificationType.System, RecipientUserId = owner,
                });
            else if (_bell != null)
                await _bell.SendAsync(owner, companyId, NotificationType.System, title, message, "/pages/addons.html", "Subscription", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งเจ้าของเรื่องโควตาข้อความ (ระดับ {Level}) ไม่สำเร็จ — บริษัท {CompanyId}", reservation.CrossedLevel, companyId);
        }
    }

    // ───────────────────────── สถานะ ─────────────────────────

    public async Task<NotifyQuotaStatus> GetStatusAsync(Guid companyId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var period = NotifyQuotaPolicy.PeriodOf(now);
        var (planLimit, bonusRemaining, bonusExp) = await LoadLimitAsync(_db, companyId, ct);
        var counter = await _db.NotifyUsageCounters.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.Period == period)
            .Select(x => new { x.Used, x.BonusUsed }).FirstOrDefaultAsync(ct);
        var used = (counter?.Used ?? 0) + (counter?.BonusUsed ?? 0);
        var bonusUsed = counter?.BonusUsed ?? 0;
        var bonusAlive = NotifyQuotaPolicy.BonusAlive(bonusRemaining, bonusExp, now);
        var limit = NotifyQuotaPolicy.EffectiveLimit(planLimit, bonusUsed, bonusRemaining, bonusExp, now);
        decimal? price = null;
        if (_metering != null)
        {
            try { price = (await _metering.ResolveEffectivePlanAsync(companyId, AddOnCodes.NotifyTopUp, ct))?.UnitPrice; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "อ่านราคาแพ็กข้อความไม่สำเร็จ (company {CompanyId})", companyId);
            }
        }
        return new NotifyQuotaStatus(
            Period: period,
            Unlimited: limit < 0,
            Used: used,
            PlanLimit: planLimit,
            BonusUsed: bonusUsed,
            BonusRemaining: bonusAlive ? bonusRemaining : 0,
            BonusExpiresAt: bonusAlive ? bonusExp : null,
            EffectiveLimit: limit,
            Remaining: limit < 0 ? int.MaxValue : Math.Max(0, limit - used),
            WarnLevel: NotifyQuotaPolicy.WarnLevel(used, limit),
            Message: limit < 0 ? null : NotifyQuotaPolicy.StatusMessage(used, limit),
            MeteredNote: NotifyQuotaPolicy.MeteredChannelsNote,
            PackSize: AddOnCodes.MessagesPerNotifyTopUpPack,
            PackPrice: price,
            PackValidDays: AddOnCodes.TopUpValidDays);
    }

    // ───────────────────────── ซื้อแพ็กเพิ่ม ─────────────────────────

    public async Task<NotifyQuotaStatus> PurchaseTopUpAsync(Guid companyId, int packs, string actor, CancellationToken ct = default, string? idempotencyKey = null)
    {
        if (packs <= 0) throw new BusinessRuleException("จำนวนแพ็กต้องมากกว่า 0", "NOTIFY-TOPUP");
        if (packs > 50) throw new BusinessRuleException("ซื้อได้ครั้งละไม่เกิน 50 แพ็ก", "NOTIFY-TOPUP");
        if (_metering == null) throw new BusinessRuleException("ระบบคิดค่าบริการไม่พร้อม — ลองใหม่ภายหลัง", "NOTIFY-TOPUP");

        // ล็อกต่อบริษัท (ตัวเรียกเดียวกับ QuotaService.PurchaseTopUpAsync) **ก่อน**อ่านอะไรทั้งนั้น — ลำดับการซื้อในงวดเป็นส่วนของคีย์กันซ้ำ ·
        // pg_advisory_xact_lock ต้องอยู่ในธุรกรรม ไม่งั้นหลุดทันที
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.QuotaGrant, "notify-topup") }, ct);

        var subId = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (subId is not Guid sid) throw new BusinessRuleException("บริษัทนี้ยังไม่มีแพ็กเกจ", "NOTIFY-TOPUP");

        // ฝ่ายค้านรอบ 212 B2: ลำดับนับจากคำนำหน้าคีย์ของงวดไทย (ไม่ใช่ต้นเดือน UTC)
        var nowUtc = DateTime.UtcNow;
        var period = AddOnBilling.PeriodOf(nowUtc);
        var prefix = AddOnBilling.TopUpKeyPrefix(AddOnCodes.NotifyTopUp, period);
        var seq = await _db.UsageEvents.AsNoTracking()
            .CountAsync(u => u.CompanyId == companyId && u.FeatureCode == AddOnCodes.NotifyTopUp
                          && u.IdempotencyKey != null && u.IdempotencyKey.StartsWith(prefix) && !u.IsDeleted, ct) + 1;

        // คิดเงินก่อนให้ของ — บันทึกมิเตอร์ไม่สำเร็จ = ไม่แจกข้อความฟรี · ใบแจ้งหนี้ไหลเส้นเดิม (UsageInvoicingJob → PlatformBillingDocumentIssuer)
        var res = await _metering.RecordAsync(new UsageRecordRequest(
            CompanyId: companyId,
            FeatureCode: AddOnCodes.NotifyTopUp,
            Quantity: packs,
            IdempotencyKey: AddOnBilling.TopUpRequestKey(AddOnCodes.NotifyTopUp, period, idempotencyKey) ?? AddOnBilling.TopUpKey(AddOnCodes.NotifyTopUp, period, seq),
            RefEntityType: "Subscription",
            RefEntityId: sid), ct);
        if (!res.Recorded)
        {
            // ฝ่ายค้าน S2: คำขอเดิม (Idempotency-Key เดียวกัน) ส่งซ้ำ ⇒ ผลเดิม (ให้ของไปแล้วตอนคำขอแรก · ไม่คิดเงิน/ไม่ให้ซ้ำ)
            if (res.Duplicate && !string.IsNullOrWhiteSpace(idempotencyKey))
            {
                await tx.RollbackAsync(ct);
                return await GetStatusAsync(companyId, ct);
            }
            throw new BusinessRuleException(
                res.Duplicate
                    ? "เพิ่งซื้อแพ็กข้อความไปเมื่อครู่ — กรุณารอสักครู่แล้วลองใหม่ (ระบบกันการกดซ้ำ)"
                    : $"ซื้อแพ็กข้อความไม่สำเร็จ: {res.Reason ?? "ไม่ทราบสาเหตุ"}",
                "NOTIFY-TOPUP");
        }

        var granted = packs * AddOnCodes.MessagesPerNotifyTopUpPack;
        var expires = nowUtc.AddDays(AddOnCodes.TopUpValidDays);
        // ฝ่ายค้านรอบ 212 B1: ให้โบนัสด้วย UPDATE เดียวใต้ล็อก (ไม่เขียนค่าสัมบูรณ์จาก entity ที่อ่านไว้ — เดิมสองการซื้อพร้อมกันได้ 500 จ่าย 2 แพ็ก ·
        // และการหักโบนัสของ TryReserveAsync ที่เกิดระหว่างนั้นถูกเขียนทับ) · โบนัสเดิมหมดอายุแล้ว ⇒ เริ่มนับใหม่ · อายุยาวที่สุดชนะ
        var updated = await _db.Database.ExecuteSqlRawAsync("""
            UPDATE "Subscriptions"
            SET "NotifyBonusQuota" = CASE WHEN "NotifyBonusQuota" > 0 AND ("NotifyBonusExpiresAt" IS NULL OR "NotifyBonusExpiresAt" > now())
                                          THEN "NotifyBonusQuota" ELSE 0 END + {0},
                "NotifyBonusExpiresAt" = GREATEST("NotifyBonusExpiresAt", {1})
            WHERE "Id" = {2} AND "CompanyId" = {3};
            """, new object[] { granted, expires, sid, companyId }, ct);
        if (updated != 1) throw new BusinessRuleException("ให้แพ็กข้อความไม่สำเร็จ — ไม่พบแพ็กเกจของบริษัท", "NOTIFY-TOPUP");

        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            Action = AuditAction.Create,
            EntityType = "Subscription",
            EntityId = sid.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "NotifyTopUp", packs, granted,
                charged = res.ChargedAmount, usageEventId = res.UsageEventId,
                expiresAt = expires, by = actor,
            }),
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _logger.LogInformation("บริษัท {Cid} ซื้อแพ็กข้อความ {Packs} แพ็ก ({Msgs} ข้อความ) เป็นเงิน {Amt}", companyId, packs, granted, res.ChargedAmount);
        return await GetStatusAsync(companyId, ct);
    }
}
