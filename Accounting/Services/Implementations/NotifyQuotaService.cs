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
    /// <summary>รอบ 231 (คำตัดสินข้อ 231-2): เต็มแต่ผู้เช่าเปิด "ใช้ต่อได้ คิดค่าส่วนเกิน" ⇒ ส่งทางช่องกลางต่อ + 1 UsageEvent overage.notify.messages ต่อข้อความ</summary>
    public const string KindOverage = "overage";
    /// <summary>รอบ 254 WP-11 L4: ใช้โบนัสแพ็กของช่อง (<c>notify.topup.line</c>/<c>.email</c>) — คืนหน่วยเข้าถังของช่องนั้น</summary>
    public const string KindChannelBonus = "bonus-channel";
    /// <summary>อายุ claim — คำขอที่ค้าง/ล้มกลางทางเกินนี้ ⇒ คำขอถัดไปรับช่วงได้ ·
    /// ฝ่ายค้านรอบ 212 (a): ต้องยาวกว่าผลรวม timeout ของทุกขั้น (LINE push + SMTP ร้าน + SMTP ระบบ) — ไม่งั้นคำขอที่ยังส่งอยู่ถูกรับช่วงแล้วส่งซ้ำ</summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);

    private readonly AccountingDbContext _db;
    private readonly ILogger<NotifyQuotaService> _logger;
    private readonly IServiceScopeFactory? _scopes;
    private readonly IUsageMeteringService? _metering;
    private readonly INotificationEngine? _engine;
    private readonly INotificationService? _bell;

    /// <summary>รอบ 231: ด่านส่วนเกินตัวเดียว (null ในเทสต์เดิม = ไม่มีส่วนเกิน — เส้นเดิมทุกประการ)</summary>
    private readonly IOverageService? _overage;

    public NotifyQuotaService(AccountingDbContext db, ILogger<NotifyQuotaService> logger, IServiceScopeFactory? scopes = null,
        IUsageMeteringService? metering = null, INotificationEngine? engine = null, INotificationService? bell = null, IOverageService? overage = null)
    {
        _overage = overage;
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
                    INSERT INTO "CustomerNotifyLogs" ("Id","CompanyId","MessageKey","EventKey","TargetKind","TargetId","Metered","Status","Attempts","ResendCount","CreatedAt","IsDeleted")
                    VALUES (@id, @c, @k, @e, @tk, @ti, false, 'Pending', 0, 0, now(), false)
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

    public async Task CompleteAsync(Guid companyId, Guid? logId, string status, string? channel, string? source, bool metered, string? note,
        string? suppressReason = null, CancellationToken ct = default)
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
                        "SuppressReason" = CASE WHEN @s = 'Suppressed' THEN @sr ELSE NULL END,
                        "SentAt" = CASE WHEN @s = 'Sent' THEN now() ELSE "SentAt" END, "UpdatedAt" = now()
                    WHERE "Id" = @id AND "CompanyId" = @c;
                    """;
                return await ExecAsync(db, sql, ct,
                    P("s", status, NpgsqlDbType.Varchar), P("ch", channel, NpgsqlDbType.Varchar), P("src", source, NpgsqlDbType.Varchar),
                    P("m", metered, NpgsqlDbType.Boolean), P("n", trimmed, NpgsqlDbType.Varchar), P("sr", suppressReason, NpgsqlDbType.Varchar),
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

    /// <summary>รอบ 254 WP-11 L4: เพดาน + โบนัสของทุกช่อง (อ่านอย่างเดียว) — ก้อนรวมผ่าน <see cref="NotifyQuotaPolicy.PlanLimit"/> · ช่องผ่าน <see cref="NotifyQuotaPolicy.ChannelPlanLimit"/>
    /// (null = ช่องนั้นไม่ได้ตั้งเพดานแยก)</summary>
    private sealed record QuotaLimits(int PlanLimit, int BonusRemaining, DateTime? BonusExpiresAt,
        int? LineLimit, int LineBonus, DateTime? LineBonusExpiresAt, int? EmailLimit, int EmailBonus, DateTime? EmailBonusExpiresAt)
    {
        public static readonly QuotaLimits None = new(NotifyQuotaPolicy.Unlimited, 0, null, null, 0, null, null, 0, null);

        public int? LimitFor(string channel) => channel switch
        {
            NotifyQuotaPolicy.ChannelLine => LineLimit,
            NotifyQuotaPolicy.ChannelEmail => EmailLimit,
            _ => PlanLimit,
        };

        public (int Bonus, DateTime? ExpiresAt) BonusFor(string channel) => channel switch
        {
            NotifyQuotaPolicy.ChannelLine => (LineBonus, LineBonusExpiresAt),
            NotifyQuotaPolicy.ChannelEmail => (EmailBonus, EmailBonusExpiresAt),
            _ => (BonusRemaining, BonusExpiresAt),
        };

        /// <summary>โบนัสที่ยังใช้ได้กับแถวตัวนับนี้ (ตามลำดับ <see cref="NotifyQuotaPolicy.BonusOrder"/>)</summary>
        public int AliveBonusFor(string counterChannel, DateTime now)
        {
            var sum = 0;
            foreach (var b in NotifyQuotaPolicy.BonusOrder(counterChannel))
            {
                var (q, e) = BonusFor(b);
                if (NotifyQuotaPolicy.BonusAlive(q, e, now)) sum += q;
            }
            return sum;
        }
    }

    /// <summary>คอลัมน์โบนัสของช่อง (ค่าคงที่ในโค้ด — ไม่มีค่าจากผู้ใช้ต่อเข้า SQL)</summary>
    private static (string Quota, string ExpiresAt) BonusColumns(string channel) => channel switch
    {
        NotifyQuotaPolicy.ChannelLine => ("NotifyBonusLineQuota", "NotifyBonusLineExpiresAt"),
        NotifyQuotaPolicy.ChannelEmail => ("NotifyBonusEmailQuota", "NotifyBonusEmailExpiresAt"),
        _ => ("NotifyBonusQuota", "NotifyBonusExpiresAt"),
    };

    /// <summary>เพดานของบริษัท + โบนัส (อ่านอย่างเดียว) — ผู้ตัดสินเพดานตัวเดียว: <see cref="NotifyQuotaPolicy.PlanLimit"/> / <see cref="NotifyQuotaPolicy.ChannelPlanLimit"/></summary>
    private static async Task<QuotaLimits> LoadLimitsAsync(AccountingDbContext db, Guid companyId, CancellationToken ct)
    {
        var sub = await db.Subscriptions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new
            {
                s.Plan, s.Status, s.MaxSharedMessagesPerMonth, s.NotifyBonusQuota, s.NotifyBonusExpiresAt, s.AccountSubscriptionId,
                s.MaxSharedLineMessagesPerMonth, s.MaxSharedEmailsPerMonth,
                s.NotifyBonusLineQuota, s.NotifyBonusLineExpiresAt, s.NotifyBonusEmailQuota, s.NotifyBonusEmailExpiresAt,
            })
            .FirstOrDefaultAsync(ct);
        if (sub == null) return QuotaLimits.None;

        // แม่แบบแพ็กเกจที่มีผล: กลุ่มบัญชี ⇒ แม่แบบของกลุ่ม (นับต่อบริษัท ไม่ pool) · ไม่งั้น ⇒ แม่แบบตามระดับแพ็กเกจของบริษัท
        int? tplLimit = null, tplTrial = null, tplLine = null, tplTrialLine = null, tplEmail = null, tplTrialEmail = null;
        var trial = sub.Status == SubscriptionStatus.Trial;
        var fromAccount = false;
        if (sub.AccountSubscriptionId is Guid aid)
        {
            var acct = await db.AccountSubscriptions.AsNoTracking()
                .Where(a => a.Id == aid && !a.IsDeleted)
                .Select(a => new
                {
                    a.Status, a.PlanTemplate.MaxSharedMessagesPerMonth, a.PlanTemplate.TrialMaxSharedMessagesPerMonth,
                    a.PlanTemplate.MaxSharedLineMessagesPerMonth, a.PlanTemplate.TrialMaxSharedLineMessagesPerMonth,
                    a.PlanTemplate.MaxSharedEmailsPerMonth, a.PlanTemplate.TrialMaxSharedEmailsPerMonth,
                })
                .FirstOrDefaultAsync(ct);
            if (acct != null)
            {
                fromAccount = true;
                trial = acct.Status == SubscriptionStatus.Trial;
                tplLimit = acct.MaxSharedMessagesPerMonth;
                tplTrial = acct.TrialMaxSharedMessagesPerMonth;
                tplLine = acct.MaxSharedLineMessagesPerMonth;
                tplTrialLine = acct.TrialMaxSharedLineMessagesPerMonth;
                tplEmail = acct.MaxSharedEmailsPerMonth;
                tplTrialEmail = acct.TrialMaxSharedEmailsPerMonth;
            }
        }
        if (!fromAccount)
        {
            var tpl = await db.PlanTemplates.AsNoTracking()
                .Where(p => p.Plan == sub.Plan && p.IsActive && !p.IsDeleted)
                .OrderBy(p => p.CreatedAt)
                .Select(p => new
                {
                    p.MaxSharedMessagesPerMonth, p.TrialMaxSharedMessagesPerMonth,
                    p.MaxSharedLineMessagesPerMonth, p.TrialMaxSharedLineMessagesPerMonth, p.MaxSharedEmailsPerMonth, p.TrialMaxSharedEmailsPerMonth,
                })
                .FirstOrDefaultAsync(ct);
            tplLimit = tpl?.MaxSharedMessagesPerMonth;
            tplTrial = tpl?.TrialMaxSharedMessagesPerMonth;
            tplLine = tpl?.MaxSharedLineMessagesPerMonth;
            tplTrialLine = tpl?.TrialMaxSharedLineMessagesPerMonth;
            tplEmail = tpl?.MaxSharedEmailsPerMonth;
            tplTrialEmail = tpl?.TrialMaxSharedEmailsPerMonth;
        }
        return new QuotaLimits(
            NotifyQuotaPolicy.PlanLimit(sub.MaxSharedMessagesPerMonth, tplLimit, tplTrial, trial), sub.NotifyBonusQuota, sub.NotifyBonusExpiresAt,
            NotifyQuotaPolicy.ChannelPlanLimit(sub.MaxSharedLineMessagesPerMonth, tplLine, tplTrialLine, trial), sub.NotifyBonusLineQuota, sub.NotifyBonusLineExpiresAt,
            NotifyQuotaPolicy.ChannelPlanLimit(sub.MaxSharedEmailsPerMonth, tplEmail, tplTrialEmail, trial), sub.NotifyBonusEmailQuota, sub.NotifyBonusEmailExpiresAt);
    }

    private static Task<int> EnsureCounterAsync(AccountingDbContext db, Guid companyId, string period, string channel, CancellationToken ct)
        => ExecAsync(db, """
            INSERT INTO "NotifyUsageCounters" ("CompanyId","Period","Channel","Used","BonusUsed") VALUES (@c, @p, @ch, 0, 0)
            ON CONFLICT ("CompanyId","Period","Channel") DO NOTHING;
            """, ct, P("c", companyId, NpgsqlDbType.Uuid), P("p", period, NpgsqlDbType.Varchar), P("ch", channel, NpgsqlDbType.Varchar));

    /// <summary>พารามิเตอร์กุญแจของแถวตัวนับ (สร้างใหม่ทุกคำสั่ง — NpgsqlParameter ใช้ซ้ำข้ามคำสั่งไม่ได้)</summary>
    private static NpgsqlParameter[] CounterKey(Guid companyId, string period, string channel, params NpgsqlParameter[] extra)
    {
        var list = new List<NpgsqlParameter>
        {
            P("c", companyId, NpgsqlDbType.Uuid), P("p", period, NpgsqlDbType.Varchar), P("ch", channel, NpgsqlDbType.Varchar),
        };
        list.AddRange(extra);
        return list.ToArray();
    }

    public async Task<NotifyReservation> TryReserveAsync(Guid companyId, bool essential, CancellationToken ct = default,
        string? channel = null, bool combinedFallback = true)
    {
        var now = DateTime.UtcNow;
        var period = NotifyQuotaPolicy.PeriodOf(now);
        var ch = NotifyQuotaPolicy.NormalizeChannel(channel);
        var counter = NotifyQuotaPolicy.ChannelAll;
        try
        {
            return await IsolatedAsync(async db =>
            {
                var limits = await LoadLimitsAsync(db, companyId, ct);
                var channelLimit = limits.LimitFor(ch);
                // รอบ 254 WP-11 L4: แถวตัวนับ = ช่องที่มีเพดานแยก · ไม่มี ⇒ ก้อนรวมเดิม (หรือแถวของช่องแบบไม่จำกัด เมื่อผู้เรียกห้ามกินก้อนรวม)
                counter = NotifyQuotaPolicy.CounterChannel(ch, ch == NotifyQuotaPolicy.ChannelAll ? limits.PlanLimit : channelLimit, combinedFallback);
                var planLimit = counter == NotifyQuotaPolicy.ChannelAll ? limits.PlanLimit : channelLimit ?? NotifyQuotaPolicy.Unlimited;
                var bonusAlive = limits.AliveBonusFor(counter, now);
                await EnsureCounterAsync(db, companyId, period, counter, ct);

                // 1) เพดานแพ็กเกจ (ไม่จำกัด = ไม่มีเงื่อนไข) — atomic: สองคำขอที่ Used = limit-1 ผ่านได้คำขอเดียว
                int? after;
                if (planLimit < 0)
                    after = await ScalarIntAsync(db, """
                        UPDATE "NotifyUsageCounters" SET "Used" = "Used" + 1 WHERE "CompanyId" = @c AND "Period" = @p AND "Channel" = @ch
                        RETURNING "Used" + "BonusUsed";
                        """, ct, CounterKey(companyId, period, counter));
                else
                    after = await ScalarIntAsync(db, """
                        UPDATE "NotifyUsageCounters" SET "Used" = "Used" + 1 WHERE "CompanyId" = @c AND "Period" = @p AND "Channel" = @ch AND "Used" < @limit
                        RETURNING "Used" + "BonusUsed";
                        """, ct, CounterKey(companyId, period, counter, P("limit", planLimit, NpgsqlDbType.Integer)));
                if (after is int planAfter)
                    return Reserved(KindPlan, period, planLimit, planAfter, bonusAlive, now) with { Channel = counter };

                // 2) เกินเพดานแพ็กเกจ ⇒ โบนัสที่ซื้อ (มีเหลือ + ยังไม่หมดอายุ) — แพ็กของช่องก่อน แล้วแพ็กรวมเดิม · atomic เช่นกัน
                foreach (var bonusChannel in NotifyQuotaPolicy.BonusOrder(counter))
                {
                    var (q, e) = BonusColumns(bonusChannel);
                    var bonusAfter = await ScalarIntAsync(db, $"""
                        UPDATE "Subscriptions" SET "{q}" = "{q}" - 1
                        WHERE "CompanyId" = @c AND "IsDeleted" = false AND "{q}" > 0
                          AND ("{e}" IS NULL OR "{e}" > now())
                        RETURNING "{q}";
                        """, ct, P("c", companyId, NpgsqlDbType.Uuid));
                    if (bonusAfter is not int) continue;
                    var used = await ScalarIntAsync(db, """
                        UPDATE "NotifyUsageCounters" SET "BonusUsed" = "BonusUsed" + 1 WHERE "CompanyId" = @c AND "Period" = @p AND "Channel" = @ch
                        RETURNING "Used" + "BonusUsed";
                        """, ct, CounterKey(companyId, period, counter)) ?? 0;
                    var kind = bonusChannel == NotifyQuotaPolicy.ChannelAll ? KindBonus : KindChannelBonus;
                    return Reserved(kind, period, planLimit, used, Math.Max(0, bonusAlive - 1), now) with { Channel = counter };
                }

                // 3) ข้อความจำเป็น ⇒ นับเกินเพดาน (ไม่ระงับ)
                if (essential)
                {
                    var used = await ScalarIntAsync(db, """
                        UPDATE "NotifyUsageCounters" SET "Used" = "Used" + 1 WHERE "CompanyId" = @c AND "Period" = @p AND "Channel" = @ch
                        RETURNING "Used" + "BonusUsed";
                        """, ct, CounterKey(companyId, period, counter)) ?? 0;
                    return new NotifyReservation(true, KindEssential, period, planLimit, 0, used) { Channel = counter };
                }

                // 4) รอบ 231 (§B.4 · คำตัดสินข้อ 231-2): เต็ม + ผู้เช่าเปิดส่วนเกิน (มีราคา · ไม่ถึงเพดาน · ไม่ถูกล็อก) ⇒ ส่งทางช่องกลางต่อ คิดต่อข้อความ
                var overage = await TryReserveOverageAsync(db, companyId, period, counter, ct);
                if (overage != null) return overage with { Limit = planLimit, CrossedLevel = 0 };

                // 5) เต็ม — แจ้งเจ้าของระดับ 100% (ครั้งเดียวต่องวดต่อช่อง · AlertIfCrossedAsync ประทับกันซ้ำ)
                return new NotifyReservation(false, KindFull, period, planLimit, 2, planLimit) { Channel = counter };
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "นับโควตาข้อความช่องกลางไม่สำเร็จ (company {CompanyId} ช่อง {Channel}) — ส่งต่อโดยไม่นับ", companyId, ch);
            return new NotifyReservation(true, KindUntracked, period, NotifyQuotaPolicy.Unlimited, 0, 0) { Channel = counter };
        }
    }

    /// <summary>รอบ 254 WP-11 L4 (คำตอบเจ้าของ Q4): อีเมลเอกสาร/ขอเซ็นผ่าน SMTP ของ NextAcc ตัดโควตาอีเมลรายเดือน (ต่อจากเพดานรายวัน <c>PlatformEmailQuota</c> — ต้องผ่านทั้งสองด่าน) ·
    /// แถวตัวนับของช่องอีเมลเสมอ (<c>combinedFallback: false</c> — ห้ามกินก้อนรวมของข้อความแจ้งลูกค้าเดิม) · ไม่ตั้งเพดาน ⇒ นับอย่างเดียว ไม่จำกัด (N9) ·
    /// เต็ม ⇒ แจ้งเจ้าของ 100% ครั้งเดียวต่องวด แล้วคืน <c>Ok=false</c> (ผู้เรียกล้มดังพร้อม <see cref="NotifyQuotaPolicy.EmailMonthlyFullReason"/>)</summary>
    public async Task<NotifyReservation> ReservePlatformEmailAsync(Guid companyId, CancellationToken ct = default)
    {
        var r = await TryReserveAsync(companyId, essential: false, ct, NotifyQuotaPolicy.ChannelEmail, combinedFallback: false);
        if (!r.Ok) await AlertIfCrossedAsync(companyId, r, ct);
        return r;
    }

    /// <summary>รอบ 231: จอง 1 หน่วยแบบส่วนเกิน — ถามด่าน (OverageService.DecideAsync) · อนุญาต ⇒ นับ Used ต่อ (atomic) + บันทึก UsageEvent overage.notify.messages
    /// (คีย์ = ตัวนับหลังเพิ่ม · unique ต่อบริษัท·งวด·ช่อง) · ไม่อนุญาต/ไม่มีบริการ ⇒ null (เส้นเดิม: ช่องของร้าน/ไม่ส่ง)</summary>
    private async Task<NotifyReservation?> TryReserveOverageAsync(AccountingDbContext db, Guid companyId, string period, string counter, CancellationToken ct)
    {
        if (_overage == null) return null;
        var decision = await _overage.DecideAsync(companyId, AddOnCodesOverageResource, 1, ct);
        if (!decision.Allowed) return null;
        var used = await ScalarIntAsync(db, """
            UPDATE "NotifyUsageCounters" SET "Used" = "Used" + 1 WHERE "CompanyId" = @c AND "Period" = @p AND "Channel" = @ch
            RETURNING "Used" + "BonusUsed";
            """, ct, CounterKey(companyId, period, counter)) ?? 0;
        await _overage.RecordAsync(companyId, AddOnCodesOverageResource, 1, OverageKey(period, used, counter), "NotifyUsageCounter", null,
            chargeable: true, unitPriceSnapshot: decision.UnitPrice, ct);
        return new NotifyReservation(true, KindOverage, period, NotifyQuotaPolicy.Unlimited, 0, used) { Channel = counter };
    }

    private const string AddOnCodesOverageResource = Accounting.Models.Constants.OverageResources.NotifyMessages;

    /// <summary>คีย์ UsageEvent ส่วนเกิน — ก้อนรวมคงรูปเดิม (แถวเก่ายังจับคู่คืนได้) · ช่องแยกใส่ชื่อช่อง (ตัวนับคนละแถว ⇒ เลขชนกันได้)</summary>
    private static string OverageKey(string period, int usedAfter, string counter)
        => counter == NotifyQuotaPolicy.ChannelAll ? $"notify-overage:{period}:{usedAfter}" : $"notify-overage:{period}:{counter}:{usedAfter}";

    private static NotifyReservation Reserved(string kind, string period, int planLimit, int usedAfter, int bonusRemaining, DateTime now)
    {
        if (planLimit < 0) return new NotifyReservation(true, kind, period, NotifyQuotaPolicy.Unlimited, 0, usedAfter);
        // เพดานรวม = แพ็กเกจ + โบนัสที่ใช้แล้ว + โบนัสที่เหลือ (= ใช้ได้ทั้งหมดงวดนี้) · ระดับเตือนเทียบกับเพดานรวม
        var bonusUsed = Math.Max(0, usedAfter - planLimit);
        var limit = NotifyQuotaPolicy.EffectiveLimit(planLimit, bonusUsed, bonusRemaining, null, now);
        return new NotifyReservation(true, kind, period, limit, NotifyQuotaPolicy.CrossedLevel(usedAfter - 1, usedAfter, limit), usedAfter);
    }

    public async Task ReleaseAsync(Guid companyId, NotifyReservation reservation, CancellationToken ct = default)
    {
        if (!reservation.Ok || reservation.Kind is KindUntracked or KindFull) return;
        var counter = NotifyQuotaPolicy.NormalizeChannel(reservation.Channel);
        try
        {
            await IsolatedAsync(async db =>
            {
                if (reservation.Kind == KindOverage)
                {
                    // รอบ 231: ส่งล้ม ⇒ คืนตัวนับ + ถอน UsageEvent ส่วนเกินของข้อความนี้ (ยังไม่ออกบิล) — ไม่คิดเงินข้อความที่ไม่ได้ส่ง
                    await ExecAsync(db, """UPDATE "UsageEvents" SET "IsDeleted" = true, "UpdatedAt" = now() WHERE "CompanyId" = @c AND "IdempotencyKey" = @k AND "BilledPeriod" IS NULL;""",
                        ct, P("c", companyId, NpgsqlDbType.Uuid), P("k", OverageKey(reservation.Period, reservation.UsedAfter, counter), NpgsqlDbType.Varchar));
                    return await ExecAsync(db, """
                        UPDATE "NotifyUsageCounters" SET "Used" = GREATEST("Used" - 1, 0) WHERE "CompanyId" = @c AND "Period" = @p AND "Channel" = @ch;
                        """, ct, CounterKey(companyId, reservation.Period, counter));
                }
                if (reservation.Kind is KindBonus or KindChannelBonus)
                {
                    // คืนโบนัสให้ถังที่หักไป: KindBonus = แพ็กรวมเดิม · KindChannelBonus = แพ็กของช่องของแถวตัวนับ
                    var (q, _) = BonusColumns(reservation.Kind == KindBonus ? NotifyQuotaPolicy.ChannelAll : counter);
                    await ExecAsync(db, $"""UPDATE "Subscriptions" SET "{q}" = "{q}" + 1 WHERE "CompanyId" = @c AND "IsDeleted" = false;""",
                        ct, P("c", companyId, NpgsqlDbType.Uuid));
                    return await ExecAsync(db, """
                        UPDATE "NotifyUsageCounters" SET "BonusUsed" = GREATEST("BonusUsed" - 1, 0) WHERE "CompanyId" = @c AND "Period" = @p AND "Channel" = @ch;
                        """, ct, CounterKey(companyId, reservation.Period, counter));
                }
                return await ExecAsync(db, """
                    UPDATE "NotifyUsageCounters" SET "Used" = GREATEST("Used" - 1, 0) WHERE "CompanyId" = @c AND "Period" = @p AND "Channel" = @ch;
                    """, ct, CounterKey(companyId, reservation.Period, counter));
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "คืนหน่วยโควตาข้อความไม่สำเร็จ (company {CompanyId} period {Period} ช่อง {Channel})", companyId, reservation.Period, counter);
        }
    }

    public async Task AlertIfCrossedAsync(Guid companyId, NotifyReservation reservation, CancellationToken ct = default)
    {
        if (reservation.CrossedLevel <= 0 || reservation.Limit < 0) return;
        var counter = NotifyQuotaPolicy.NormalizeChannel(reservation.Channel);
        try
        {
            var column = reservation.CrossedLevel >= 2 ? "\"Warned100At\"" : "\"Warned80At\"";
            var stamped = await IsolatedAsync(async db =>
            {
                await EnsureCounterAsync(db, companyId, reservation.Period, counter, ct);
                return await ExecAsync(db,
                    $"UPDATE \"NotifyUsageCounters\" SET {column} = now() WHERE \"CompanyId\" = @c AND \"Period\" = @p AND \"Channel\" = @ch AND {column} IS NULL;",
                    ct, CounterKey(companyId, reservation.Period, counter));
            });
            if (stamped != 1) return;   // งวดนี้แจ้งระดับนี้ของช่องนี้ไปแล้ว (หรือคำขออื่นแจ้งพร้อมกัน)
            // ฝ่ายค้านรอบ 231 S7: ผู้รับ = เจ้าของ + ผู้มีสิทธิ์ BillingManage (ตัวเดียว OverageService.BillingRecipientsAsync)
            var recipients = await IsolatedAsync(db => OverageService.BillingRecipientsAsync(db, companyId, ct));
            if (recipients.Count == 0) return;
            // รอบ 254 WP-11 L4: แถวแยกช่องบอกชื่อช่องนำหน้า (ก้อนรวมคงข้อความเดิม)
            var prefix = counter == NotifyQuotaPolicy.ChannelAll ? "" : $"[{NotifyQuotaPolicy.ChannelLabel(counter)}] ";
            var title = prefix + NotifyQuotaPolicy.CrossedTitle(reservation.CrossedLevel);
            var message = prefix + NotifyQuotaPolicy.CrossedMessage(reservation.CrossedLevel, reservation.UsedAfter, reservation.Limit);
            var eventKey = reservation.CrossedLevel >= 2 ? NotificationEvents.NotifyQuota100 : NotificationEvents.NotifyQuota80;
            foreach (var uid in recipients)
            {
                if (_engine != null)
                    await _engine.DispatchAsync(companyId, eventKey, new NotificationContext
                    {
                        // รอบ 231 (P0 ข): ลิงก์ไปแท็บ "ใช้ไปเท่าไร" ของ hub (เดิม /pages/addons.html) — ที่เดียวกับทรัพยากรอื่น
                        Title = title, Message = message, ActionUrl = Accounting.Models.Constants.QuotaResources.UsageUrl, EntityType = "Subscription",
                        BellType = NotificationType.System, RecipientUserId = uid,
                    });
                else if (_bell != null)
                    await _bell.SendAsync(uid, companyId, NotificationType.System, title, message, Accounting.Models.Constants.QuotaResources.UsageUrl, "Subscription", null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งเจ้าของเรื่องโควตาข้อความ (ระดับ {Level} ช่อง {Channel}) ไม่สำเร็จ — บริษัท {CompanyId}", reservation.CrossedLevel, counter, companyId);
        }
    }

    // ───────────────────────── สถานะ ─────────────────────────

    public async Task<NotifyQuotaStatus> GetStatusAsync(Guid companyId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var period = NotifyQuotaPolicy.PeriodOf(now);
        var limits = await LoadLimitsAsync(_db, companyId, ct);
        var planLimit = limits.PlanLimit;
        var bonusRemaining = limits.BonusRemaining;
        var bonusExp = limits.BonusExpiresAt;
        var counters = await _db.NotifyUsageCounters.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.Period == period)
            .Select(x => new { x.Channel, x.Used, x.BonusUsed }).ToListAsync(ct);
        var counter = counters.FirstOrDefault(x => x.Channel == NotifyQuotaPolicy.ChannelAll);
        var used = (counter?.Used ?? 0) + (counter?.BonusUsed ?? 0);
        var bonusUsed = counter?.BonusUsed ?? 0;
        var bonusAlive = NotifyQuotaPolicy.BonusAlive(bonusRemaining, bonusExp, now);
        var limit = NotifyQuotaPolicy.EffectiveLimit(planLimit, bonusUsed, bonusRemaining, bonusExp, now);
        var pack = await LoadPackAsync(_db, NotifyQuotaPolicy.ChannelAll, ct);
        var price = await PackPriceAsync(companyId, NotifyQuotaPolicy.ChannelAll, ct);

        // รอบ 254 WP-11 L4: สถานะต่อช่อง — ช่องที่ยังไม่แยก (OwnLimit=false) แสดงยอดที่นับแถวของช่อง (ถ้ามี · เช่นอีเมลเอกสาร) โดยไม่มีเพดาน
        var channels = new List<NotifyChannelQuotaStatus>();
        foreach (var ch in new[] { NotifyQuotaPolicy.ChannelLine, NotifyQuotaPolicy.ChannelEmail })
        {
            var row = counters.FirstOrDefault(x => x.Channel == ch);
            var chUsed = (row?.Used ?? 0) + (row?.BonusUsed ?? 0);
            var chPlan = limits.LimitFor(ch);
            var (chBonus, chExp) = limits.BonusFor(ch);
            var chAlive = NotifyQuotaPolicy.BonusAlive(chBonus, chExp, now);
            var chLimit = chPlan is int cp ? NotifyQuotaPolicy.EffectiveLimit(cp, row?.BonusUsed ?? 0, chBonus, chExp, now) : NotifyQuotaPolicy.Unlimited;
            var chPack = await LoadPackAsync(_db, ch, ct);
            channels.Add(new NotifyChannelQuotaStatus(
                Channel: ch, Label: NotifyQuotaPolicy.ChannelLabel(ch), OwnLimit: chPlan != null, Used: chUsed,
                PlanLimit: chPlan ?? NotifyQuotaPolicy.Unlimited,
                BonusRemaining: chAlive ? chBonus : 0, BonusExpiresAt: chAlive ? chExp : null,
                EffectiveLimit: chLimit, Remaining: chLimit < 0 ? int.MaxValue : Math.Max(0, chLimit - chUsed),
                WarnLevel: NotifyQuotaPolicy.WarnLevel(chUsed, chLimit),
                Message: chLimit < 0 ? null : NotifyQuotaPolicy.StatusMessage(chUsed, chLimit),
                TopUpCode: NotifyQuotaPolicy.TopUpCodeFor(ch), PackSize: chPack.Size, PackPrice: await PackPriceAsync(companyId, ch, ct),
                PackValidDays: chPack.ValidDays));
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
            PackSize: pack.Size,
            PackPrice: price,
            PackValidDays: pack.ValidDays) { Channels = channels };
    }

    /// <summary>ราคาแพ็กของช่อง (แผนที่มีผลกับบริษัท) · อ่านไม่ได้ ⇒ null + log Error (หน้าเว็บแสดง "ไม่มีราคา")</summary>
    private async Task<decimal?> PackPriceAsync(Guid companyId, string channel, CancellationToken ct)
    {
        if (_metering == null) return null;
        try { return (await _metering.ResolveEffectivePlanAsync(companyId, NotifyQuotaPolicy.TopUpCodeFor(channel), ct))?.UnitPrice; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "อ่านราคาแพ็กข้อความ ({Channel}) ไม่สำเร็จ (company {CompanyId})", channel, companyId);
            return null;
        }
    }

    /// <summary>รอบ 213: ขนาด/อายุแพ็กที่แอดมินตั้ง (แถว <c>ApiFeatures</c> ของรหัสนั้น) → ตัวตัดสินเดียว <see cref="NotifyQuotaPolicy.TopUpPackFor"/> ·
    /// ไม่มีแถว/ไม่ได้ตั้ง ⇒ ค่าตั้งต้นของช่อง (รวม 500 · LINE 1,000 · อีเมล 5,000 · 60 วัน)</summary>
    private static async Task<NotifyTopUpPack> LoadPackAsync(AccountingDbContext db, string channel, CancellationToken ct)
    {
        var code = NotifyQuotaPolicy.TopUpCodeFor(channel);
        var row = await db.ApiFeatures.AsNoTracking()
            .Where(f => f.FeatureCode == code)
            .Select(f => new { f.PackSize, f.PackValidDays })
            .FirstOrDefaultAsync(ct);
        return NotifyQuotaPolicy.TopUpPackFor(channel, row?.PackSize, row?.PackValidDays);
    }

    // ───────────────────────── ซื้อแพ็กเพิ่ม ─────────────────────────

    public async Task<NotifyQuotaStatus> PurchaseTopUpAsync(Guid companyId, int packs, string actor, CancellationToken ct = default, string? idempotencyKey = null,
        int? expectedPackSize = null, decimal? expectedPackPrice = null, string? channel = null)
    {
        if (packs <= 0) throw new BusinessRuleException("จำนวนแพ็กต้องมากกว่า 0", "NOTIFY-TOPUP");
        if (packs > 50) throw new BusinessRuleException("ซื้อได้ครั้งละไม่เกิน 50 แพ็ก", "NOTIFY-TOPUP");
        if (_metering == null) throw new BusinessRuleException("ระบบคิดค่าบริการไม่พร้อม — ลองใหม่ภายหลัง", "NOTIFY-TOPUP");
        // รอบ 254 WP-11 L4: ช่องของแพ็ก ⇒ รหัสมิเตอร์ + ถังโบนัส (ตัวตัดสินเดียว NotifyQuotaPolicy.TopUpCodeFor / BonusColumns)
        var ch = NotifyQuotaPolicy.NormalizeChannel(channel);
        var code = NotifyQuotaPolicy.TopUpCodeFor(ch);

        // ล็อกต่อบริษัท (ตัวเรียกเดียวกับ QuotaService.PurchaseTopUpAsync) **ก่อน**อ่านอะไรทั้งนั้น — ลำดับการซื้อในงวดเป็นส่วนของคีย์กันซ้ำ ·
        // pg_advisory_xact_lock ต้องอยู่ในธุรกรรม ไม่งั้นหลุดทันที · ทุกช่องใช้ล็อกเดียวกัน (ถังโบนัสอยู่แถว Subscription เดียวกัน)
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.QuotaGrant, "notify-topup") }, ct);

        var subId = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (subId is not Guid sid) throw new BusinessRuleException("บริษัทนี้ยังไม่มีแพ็กเกจ", "NOTIFY-TOPUP");

        // ฝ่ายค้านรอบ 212 B2: ลำดับนับจากคำนำหน้าคีย์ของงวดไทย (ไม่ใช่ต้นเดือน UTC)
        var nowUtc = DateTime.UtcNow;
        var period = AddOnBilling.PeriodOf(nowUtc);
        var prefix = AddOnBilling.TopUpKeyPrefix(code, period);
        var seq = await _db.UsageEvents.AsNoTracking()
            .CountAsync(u => u.CompanyId == companyId && u.FeatureCode == code
                          && u.IdempotencyKey != null && u.IdempotencyKey.StartsWith(prefix) && !u.IsDeleted, ct) + 1;

        var usageKey = AddOnBilling.TopUpRequestKey(code, period, idempotencyKey) ?? AddOnBilling.TopUpKey(code, period, seq);

        // รอบ 213: ขนาด/อายุแพ็กอ่าน ณ ตอนซื้อ (ใต้ล็อก) — แอดมินเปลี่ยนค่าแล้วมีผลกับการซื้อครั้งถัดไปเท่านั้น
        var pack = await LoadPackAsync(_db, ch, ct);
        // ฝ่ายค้านรอบ 213 S4: ข้อเสนอที่ผู้ซื้อเห็นต้องตรงของจริง (ใต้ล็อก ก่อนคิดเงิน) · คำขอซ้ำที่สำเร็จไปแล้ว (คีย์เดิม) ไม่ถูกตรวจซ้ำ — คืนผลเดิม
        var alreadyRecorded = !string.IsNullOrWhiteSpace(idempotencyKey) && await _db.UsageEvents.AsNoTracking()
            .AnyAsync(u => u.CompanyId == companyId && u.IdempotencyKey == usageKey && !u.IsDeleted, ct);
        if (!alreadyRecorded && (expectedPackSize != null || expectedPackPrice != null))
        {
            var currentPrice = (await _metering.ResolveEffectivePlanAsync(companyId, code, ct))?.UnitPrice;
            if (NotifyQuotaPolicy.TopUpOfferChanged(expectedPackSize, expectedPackPrice, pack, currentPrice) is string changed)
            {
                await tx.RollbackAsync(ct);
                throw new NotifyTopUpOfferChangedException(changed);
            }
        }

        // คิดเงินก่อนให้ของ — บันทึกมิเตอร์ไม่สำเร็จ = ไม่แจกข้อความฟรี · ใบแจ้งหนี้ไหลเส้นเดิม (UsageInvoicingJob → PlatformBillingDocumentIssuer) ·
        // ฝ่ายค้านรอบ 213 S3: หน่วยของแพ็ก ณ ตอนซื้อถูกบันทึกลงแถว (ใบแจ้งหนี้ไม่อ่านป้ายปัจจุบันของฟีเจอร์ที่แอดมินแก้ทีหลัง)
        var res = await _metering.RecordAsync(new UsageRecordRequest(
            CompanyId: companyId,
            FeatureCode: code,
            Quantity: packs,
            IdempotencyKey: usageKey,
            RefEntityType: "Subscription",
            RefEntityId: sid,
            UnitLabel: NotifyQuotaPolicy.PackUnitLabelFor(ch, pack.Size)), ct);
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

        // อายุนับจากวันซื้อ · โบนัสคงเหลือทั้งก้อนใช้ได้ถึงวันหมดอายุที่ช้ากว่า (GREATEST ด้านล่าง — ไม่หดอายุของที่ซื้อไว้)
        var granted = packs * pack.Size;
        var expires = nowUtc.AddDays(pack.ValidDays);
        // ฝ่ายค้านรอบ 212 B1: ให้โบนัสด้วย UPDATE เดียวใต้ล็อก (ไม่เขียนค่าสัมบูรณ์จาก entity ที่อ่านไว้ — เดิมสองการซื้อพร้อมกันได้ 500 จ่าย 2 แพ็ก ·
        // และการหักโบนัสของ TryReserveAsync ที่เกิดระหว่างนั้นถูกเขียนทับ) · โบนัสเดิมหมดอายุแล้ว ⇒ เริ่มนับใหม่ · อายุยาวที่สุดชนะ
        var (bq, be) = BonusColumns(ch);
        var grantSql = $$"""
            UPDATE "Subscriptions"
            SET "{{bq}}" = CASE WHEN "{{bq}}" > 0 AND ("{{be}}" IS NULL OR "{{be}}" > now())
                                THEN "{{bq}}" ELSE 0 END + {0},
                "{{be}}" = GREATEST("{{be}}", {1})
            WHERE "Id" = {2} AND "CompanyId" = {3};
            """;
        var updated = await _db.Database.ExecuteSqlRawAsync(grantSql, new object[] { granted, expires, sid, companyId }, ct);
        if (updated != 1) throw new BusinessRuleException("ให้แพ็กข้อความไม่สำเร็จ — ไม่พบแพ็กเกจของบริษัท", "NOTIFY-TOPUP");

        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            Action = AuditAction.Create,
            EntityType = "Subscription",
            EntityId = sid.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "NotifyTopUp", channel = ch, featureCode = code, packs, granted, packSize = pack.Size, packValidDays = pack.ValidDays,
                charged = res.ChargedAmount, usageEventId = res.UsageEventId,
                expiresAt = expires, by = actor,
            }),
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _logger.LogInformation("บริษัท {Cid} ซื้อแพ็กข้อความ ({Channel}) {Packs} แพ็ก ({Msgs} ข้อความ) เป็นเงิน {Amt}", companyId, ch, packs, granted, res.ChargedAmount);
        return (await GetStatusAsync(companyId, ct)) with { Granted = granted, GrantedChannel = ch };
    }
}
