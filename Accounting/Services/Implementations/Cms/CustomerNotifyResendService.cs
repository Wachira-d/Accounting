using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Cms;

/// <summary>
/// รอบ 213 (คำตัดสินข้อ 172 · เจ้าของ: "มีหน้าให้จัดการ ถ้าเติมโควต้าแล้ว สามารถส่งซ้ำทีหลังได้") — ข้อความถึงลูกค้าที่ค้างเพราะโควตาช่องกลางเต็ม:
/// <list type="number">
/// <item>รายการ: แถว <c>CustomerNotifyLogs</c> ของบริษัทนี้ที่ <c>Suppressed</c> + <see cref="NotifySuppressReason.QuotaFull"/> ย้อนหลัง <see cref="CustomerNotifyResend.ListWindow"/> ·
///   ทุกแถวผ่านตัวตัดสินเดียว <see cref="CustomerNotifyResend.Evaluate"/> (อายุ · ค่าตั้งปัจจุบัน · มีข้อความใหม่กว่าแล้ว · รายการยังอยู่/สถานะยังตรง)</item>
/// <item>ส่งซ้ำ: จองแถว (lease <see cref="CustomerNotifyResend.ResendLease"/> — กดซ้ำ/สองแท็บได้ครั้งเดียว) → ประกอบข้อความใหม่จากสถานะปัจจุบันผ่านเส้นส่งเดิม
///   (<see cref="ICmsBookingService.ResendCustomerNotifyAsync"/> / <see cref="ICmsCommerceService.ResendCustomerNotifyAsync"/> → <c>BookingNotifier</c> · คีย์เดิม ⇒ claim แถวเดิม ·
///   จองโควตาตามปกติ) → อ่านสถานะแถวกลับ (ความจริงจากตัวส่ง ไม่ใช่ค่าที่เดาเอง) · โควตายังเต็ม ⇒ หยุดทั้งชุด + บอกทางไปต่อ</item>
/// <item>ไม่เก็บเนื้อข้อความ (PDPA) · ทุก query กรอง <c>CompanyId</c> · ร่องรอยการกดลง audit chain</item>
/// </list>
/// </summary>
public class CustomerNotifyResendService : ICustomerNotifyResendService
{
    public const string OutcomeSent = "sent";
    public const string OutcomeFailed = "failed";
    public const string OutcomeQuotaFull = "quota-full";
    public const string OutcomeSkipped = "skipped";
    public const string OutcomeBusy = "busy";
    public const string TopUpUrl = "/pages/addons.html#notifyQuotaWrap";

    private readonly AccountingDbContext _db;
    private readonly ILogger<CustomerNotifyResendService> _logger;
    private readonly ICmsBookingService? _bookings;
    private readonly ICmsCommerceService? _commerce;
    private readonly INotifyQuotaService? _quota;

    public CustomerNotifyResendService(AccountingDbContext db, ILogger<CustomerNotifyResendService> logger,
        ICmsBookingService? bookings = null, ICmsCommerceService? commerce = null, INotifyQuotaService? quota = null)
    {
        _db = db;
        _logger = logger;
        _bookings = bookings;
        _commerce = commerce;
        _quota = quota;
    }

    private sealed record Row(Guid Id, string MessageKey, string EventKey, string? TargetKind, Guid? TargetId, string Status,
        string? SuppressReason, DateTime CreatedAt, int ResendCount, DateTime? ResendRequestedAt, DateTime? ClaimedUntil, string? Note);

    private sealed record Evaluated(Row Row, CustomerNotifyResendTarget Target, CustomerNotifyResendVerdict Verdict);

    /// <summary>สถานะปัจจุบันของการจอง/คำสั่งซื้อ (กรองบริษัท · แถวที่ลบแล้วไม่พบ = <see cref="CustomerNotifyResendTarget.Missing"/>) — แยกเป็นเมธอดให้เทสต์แทนค่าได้</summary>
    protected virtual async Task<IReadOnlyDictionary<Guid, CustomerNotifyResendTarget>> LoadTargetsAsync(Guid companyId,
        IReadOnlyCollection<Guid> bookingIds, IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        var map = new Dictionary<Guid, CustomerNotifyResendTarget>();
        if (bookingIds.Count > 0)
        {
            var ids = bookingIds.ToList();
            var bookings = await _db.SiteBookings.AsNoTracking()
                .Where(b => b.CompanyId == companyId && ids.Contains(b.Id))
                .Select(b => new { b.Id, b.SiteId, b.BookingNumber, b.Status, b.BookingDate, b.StartTime })
                .ToListAsync(ct);
            foreach (var b in bookings)
                map[b.Id] = new CustomerNotifyResendTarget(true, b.SiteId, CustomerNotifyResend.TargetLabel(CustomerLineLinkRule.KindBooking, b.BookingNumber),
                    BookingStatus: b.Status, BookingStartLocal: b.BookingDate.Date.Add(b.StartTime.ToTimeSpan()));
        }
        if (orderIds.Count > 0)
        {
            var ids = orderIds.ToList();
            var orders = await _db.SiteOrders.AsNoTracking()
                .Where(o => o.CompanyId == companyId && ids.Contains(o.Id))
                .Select(o => new { o.Id, o.SiteId, o.OrderNumber, o.Status })
                .ToListAsync(ct);
            foreach (var o in orders)
                map[o.Id] = new CustomerNotifyResendTarget(true, o.SiteId, CustomerNotifyResend.TargetLabel(CustomerLineLinkRule.KindOrder, o.OrderNumber),
                    OrderStatus: o.Status);
        }
        return map;
    }

    /// <summary>แถวที่ "ค้างส่ง" ตามนิยามเดียว (<see cref="CustomerNotifyResend.IsPending"/>) — แปลเป็น SQL: ระงับเพราะโควตา · ค้าง Claimed เกิน lease (ตัวส่งล้มกลางทาง — ฝ่ายค้าน S6) ·
    /// ส่งซ้ำแล้วล้ม (ฝ่ายค้าน S5 — ลองใหม่ได้)</summary>
    private IQueryable<CustomerNotifyLog> PendingQuery(Guid companyId, DateTime since, DateTime nowUtc)
    {
        return _db.CustomerNotifyLogs.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.CreatedAt >= since
                && ((l.Status == NotifyLogStatus.Suppressed && l.SuppressReason == NotifySuppressReason.QuotaFull)
                    || (l.Status == NotifyLogStatus.Claimed && l.SuppressReason == NotifySuppressReason.QuotaFull && l.ClaimedUntil < nowUtc)
                    || (l.Status == NotifyLogStatus.Failed && l.ResendCount > 0)));
    }

    /// <summary><paramref name="onlyIds"/> = แถวที่ผู้ใช้เลือก · ไม่งั้น: รายการ (<paramref name="forResend"/> = false) ย้อนหลัง <see cref="CustomerNotifyResend.ListWindow"/> ·
    /// ส่งซ้ำทั้งหมด (true) เฉพาะช่วงที่ส่งซ้ำได้ <see cref="CustomerNotifyResend.MaxAge"/> · <b>ใหม่ก่อน</b>แล้วค่อยตัด (ฝ่ายค้าน S1 — เดิมเก่าก่อน ⇒ แถวใหม่ที่ส่งได้ไม่เคยถูกตรวจ)</summary>
    private async Task<(List<Evaluated> Items, int Total)> EvaluateAsync(Guid companyId, IReadOnlyCollection<Guid>? onlyIds, bool forResend, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var nowLocal = BookingSlotEngine.BangkokNow(nowUtc);
        IQueryable<CustomerNotifyLog> q;
        if (onlyIds is { Count: > 0 })
        {
            var ids = onlyIds.Distinct().ToList();
            q = _db.CustomerNotifyLogs.AsNoTracking().Where(l => l.CompanyId == companyId && ids.Contains(l.Id));
        }
        else
            q = PendingQuery(companyId, nowUtc - (forResend ? CustomerNotifyResend.MaxAge : CustomerNotifyResend.ListWindow), nowUtc);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(l => l.CreatedAt).Take(CustomerNotifyResend.ListLimit)
            .Select(l => new Row(l.Id, l.MessageKey, l.EventKey, l.TargetKind, l.TargetId, l.Status, l.SuppressReason, l.CreatedAt,
                l.ResendCount, l.ResendRequestedAt, l.ClaimedUntil, l.Note))
            .ToListAsync(ct);
        if (rows.Count == 0) return (new List<Evaluated>(), total);

        var bookingIds = rows.Where(r => r.TargetKind == CustomerLineLinkRule.KindBooking && r.TargetId != null).Select(r => r.TargetId!.Value).Distinct().ToList();
        var orderIds = rows.Where(r => r.TargetKind == CustomerLineLinkRule.KindOrder && r.TargetId != null).Select(r => r.TargetId!.Value).Distinct().ToList();
        var targets = await LoadTargetsAsync(companyId, bookingIds, orderIds, ct);

        // ค่าตั้งปัจจุบัน (ตัวตัดสินเดียวกับตอนส่ง) — ปิดแล้ว ⇒ ไม่ส่งซ้ำ
        var rules = await _db.CustomerNotifyRules.AsNoTracking()
            .Where(r => r.CompanyId == companyId)
            .Select(r => new CustomerNotifyRuleRow(r.SiteId, r.EventKey, r.LineEnabled, r.EmailEnabled))
            .ToListAsync(ct);

        // ฝ่ายค้านรอบ 213 S2: ทุกแถวถึงรายการเดียวกัน (ไม่ว่าสถานะใด) — แถวที่ใหม่กว่าและแทนข้อความนี้ได้ (CustomerNotifyResend.Supersedes) ⇒ ส่งเฉพาะฉบับล่าสุด
        var targetIds = bookingIds.Concat(orderIds).ToList();
        var siblings = targetIds.Count == 0
            ? new List<(Guid Id, Guid? TargetId, string EventKey, DateTime CreatedAt)>()
            : (await _db.CustomerNotifyLogs.AsNoTracking()
                .Where(l => l.CompanyId == companyId && l.TargetId != null && targetIds.Contains(l.TargetId.Value))
                .Select(l => new { l.Id, l.TargetId, l.EventKey, l.CreatedAt })
                .ToListAsync(ct))
              .Select(x => (x.Id, x.TargetId, x.EventKey, x.CreatedAt)).ToList();

        var items = rows.Select(r =>
        {
            var target = r.TargetId is Guid tid && targets.TryGetValue(tid, out var t) ? t : CustomerNotifyResendTarget.Missing;
            var enabled = CustomerNotifyPolicy.Resolve(rules, target.SiteId, r.EventKey).AnyOn;
            var superseded = r.TargetId != null && siblings.Any(o => o.Id != r.Id && o.TargetId == r.TargetId && o.CreatedAt > r.CreatedAt
                && CustomerNotifyResend.Supersedes(o.EventKey, r.EventKey));
            var claimExpired = r.ClaimedUntil is DateTime cu && cu < nowUtc;
            var verdict = CustomerNotifyResend.Evaluate(
                new CustomerNotifyResendInput(r.EventKey, r.Status, r.SuppressReason, r.CreatedAt, enabled, superseded, target, claimExpired, r.ResendCount),
                nowUtc, nowLocal);
            return new Evaluated(r, target, verdict);
        }).ToList();
        return (items, total);
    }

    private static string EventLabel(string key)
        => CustomerNotifyEvents.Catalog.FirstOrDefault(e => e.Key == key)?.Label ?? key;

    public async Task<CustomerNotifyPendingList> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        var (all, total) = await EvaluateAsync(companyId, null, forResend: false, ct);
        var items = all.OrderByDescending(e => e.Row.CreatedAt).Select(e => new CustomerNotifyPendingItem(
            e.Row.Id, e.Row.EventKey, EventLabel(e.Row.EventKey), e.Target.Label ?? "รายการที่ถูกลบแล้ว", e.Row.CreatedAt,
            ReasonLabel(e.Row), e.Verdict.Resendable, e.Verdict.Code, e.Verdict.Reason,
            e.Row.ResendCount, e.Row.ResendRequestedAt)).ToList();
        var resendable = items.Count(i => i.Resendable);

        NotifyQuotaStatus? quota = null;
        if (_quota != null)
        {
            try { quota = await _quota.GetStatusAsync(companyId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "อ่านสถานะโควตาข้อความไม่สำเร็จ (company {CompanyId})", companyId);
            }
        }
        string? hint = null;
        if (resendable > 0 && quota is { Unlimited: false } && quota.Remaining < resendable)
            hint = quota.Remaining == 0
                ? $"โควตาข้อความเดือนนี้เหลือ 0 — ซื้อแพ็กข้อความเพิ่มก่อน แล้วค่อยกดส่งซ้ำ ({resendable} รายการรอส่ง)"
                : $"โควตาเหลือ {quota.Remaining:N0} ข้อความ แต่มีรายการรอส่ง {resendable} รายการ — ส่งได้ไม่ครบ ระบบจะหยุดเมื่อโควตาหมด";
        if (total > items.Count)
            hint = (hint == null ? "" : hint + " · ") + $"แสดง {items.Count} รายการล่าสุดจาก {total} รายการ";
        return new CustomerNotifyPendingList(items, total, resendable, quota, hint);
    }

    /// <summary>เหตุที่ค้าง — ส่งซ้ำแล้วล้ม (ฝ่ายค้าน S5) แสดงเหตุของช่องที่ล้ม (หมายเหตุถูกปิดบังข้อมูลติดต่อแล้วตอนเก็บ) · ค้างกลางทาง = บอกตรง ๆ</summary>
    private static string ReasonLabel(Row r) => r.Status switch
    {
        NotifyLogStatus.Failed => "ส่งซ้ำแล้วไม่สำเร็จ" + (string.IsNullOrWhiteSpace(r.Note) ? "" : ": " + r.Note),
        NotifyLogStatus.Claimed => "ส่งค้างกลางทาง (ระบบขัดข้อง) — ลองใหม่ได้",
        _ => NotifySuppressReason.Label(r.SuppressReason),
    };

    /// <summary>จองแถวสำหรับส่งซ้ำ (เงื่อนไขเดียว · หลาย instance/กดซ้ำได้ครั้งเดียว) — 0 แถว = คำขออื่นกำลังส่ง หรือส่งแล้ว</summary>
    private async Task<bool> TryLeaseAsync(Guid companyId, Guid logId, CancellationToken ct)
    {
        var n = await _db.Database.ExecuteSqlRawAsync("""
            UPDATE "CustomerNotifyLogs"
            SET "ResendRequestedAt" = now(), "ResendCount" = "ResendCount" + 1, "UpdatedAt" = now()
            WHERE "Id" = {0} AND "CompanyId" = {1}
              AND ("Status" IN ('Suppressed', 'Failed') OR ("Status" = 'Claimed' AND "ClaimedUntil" < now()))
              AND ("ResendRequestedAt" IS NULL OR "ResendRequestedAt" < now() - make_interval(secs => {2}))
            """, new object[] { logId, companyId, CustomerNotifyResend.ResendLease.TotalSeconds }, ct);
        return n == 1;
    }

    /// <summary>ส่งไม่ถึง ⇒ ปล่อย lease (กดใหม่ได้ทันทีหลังเติมโควตา) · ส่งถึง ⇒ คงเวลาไว้เป็นร่องรอย</summary>
    private Task<int> ReleaseLeaseAsync(Guid companyId, Guid logId, CancellationToken ct)
        => _db.Database.ExecuteSqlRawAsync("""
            UPDATE "CustomerNotifyLogs" SET "ResendRequestedAt" = NULL
            WHERE "Id" = {0} AND "CompanyId" = {1} AND "Status" <> 'Sent'
            """, new object[] { logId, companyId }, ct);

    private Task<string?> RebuildAsync(Guid companyId, Row row)
    {
        return CustomerNotifyResend.TargetKindOf(row.EventKey) switch
        {
            CustomerLineLinkRule.KindBooking when _bookings != null && row.TargetId is Guid bookingId
                => _bookings.ResendCustomerNotifyAsync(companyId, bookingId, row.EventKey, row.MessageKey),
            CustomerLineLinkRule.KindOrder when _commerce != null && row.TargetId is Guid orderId
                => _commerce.ResendCustomerNotifyAsync(companyId, orderId, row.EventKey),
            _ => Task.FromResult<string?>("ไม่มีเส้นส่งสำหรับประเภทนี้"),
        };
    }

    public async Task<CustomerNotifyResendResult> ResendAsync(Guid companyId, IReadOnlyCollection<Guid>? logIds, string actor, CancellationToken ct = default)
    {
        var explicitIds = logIds is { Count: > 0 };
        if (explicitIds && logIds!.Count > CustomerNotifyResend.MaxBatch)
            throw new BusinessRuleException($"ส่งซ้ำได้ครั้งละไม่เกิน {CustomerNotifyResend.MaxBatch} รายการ", "NOTIFY-RESEND");

        var (evaluated, _) = await EvaluateAsync(companyId, explicitIds ? logIds : null, forResend: true, ct);
        var results = new List<CustomerNotifyResendItemResult>();
        int sent = 0, failed = 0, skipped = 0;
        var stopped = false;

        // รหัสที่ขอแต่ไม่อยู่ในบริษัทนี้ = ไม่พบ (ไม่บอกว่ามีอยู่ในบริษัทอื่น)
        if (explicitIds)
            foreach (var missing in logIds!.Distinct().Where(id => evaluated.All(e => e.Row.Id != id)))
            {
                skipped++;
                results.Add(new(missing, OutcomeSkipped, "ไม่พบรายการ"));
            }
        foreach (var e in evaluated.Where(e => !e.Verdict.Resendable))
        {
            skipped++;
            results.Add(new(e.Row.Id, OutcomeSkipped, e.Verdict.Reason));
        }

        var candidates = evaluated.Where(e => e.Verdict.Resendable).OrderBy(e => e.Row.CreatedAt).Take(CustomerNotifyResend.MaxBatch).ToList();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!await TryLeaseAsync(companyId, c.Row.Id, ct))
            {
                skipped++;
                results.Add(new(c.Row.Id, OutcomeBusy, "กำลังส่งโดยคำขออื่น หรือส่งไปแล้ว"));
                continue;
            }
            string? detail;
            var crashed = false;
            try { detail = await RebuildAsync(companyId, c.Row); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ส่งซ้ำข้อความ {LogId} ({EventKey}) ล้ม (company {CompanyId})", c.Row.Id, c.Row.EventKey, companyId);
                detail = "เกิดข้อผิดพลาดภายในระหว่างส่ง (ดูบันทึกระบบ)";
                crashed = true;
            }

            // ความจริงจากตัวส่ง (แถวที่ BookingNotifier ปิดให้) — ไม่ใช่ค่าที่ประทับเอง (F2 #3)
            var after = await _db.CustomerNotifyLogs.AsNoTracking()
                .Where(l => l.Id == c.Row.Id && l.CompanyId == companyId)
                .Select(l => new { l.Status, l.SuppressReason, l.Note })
                .FirstOrDefaultAsync(ct);
            if (after?.Status == NotifyLogStatus.Sent)
            {
                sent++;
                results.Add(new(c.Row.Id, OutcomeSent, null));
                continue;
            }
            await ReleaseLeaseAsync(companyId, c.Row.Id, ct);
            // ฝ่ายค้านรอบ 213 S6 + (a): ยังค้าง Claimed — ล้มกลางทาง ⇒ ล้ม (แถวกลับเข้ารายการเองเมื่อ lease ของ claim หมด · PendingQuery) ·
            // ไม่ได้ล้ม ⇒ คำขออื่นถือ claim อยู่ = "กำลังส่ง" ไม่ใช่ความล้มเหลว
            if (after?.Status == NotifyLogStatus.Claimed)
            {
                if (crashed)
                {
                    failed++;
                    results.Add(new(c.Row.Id, OutcomeFailed, detail + $" — จะกลับมาให้ลองใหม่ภายใน {CustomerNotifyResend.ResendLease.TotalMinutes:0} นาที"));
                }
                else
                {
                    skipped++;
                    results.Add(new(c.Row.Id, OutcomeBusy, "กำลังส่งโดยคำขออื่น"));
                }
                continue;
            }
            if (after?.Status == NotifyLogStatus.Suppressed && after.SuppressReason == NotifySuppressReason.QuotaFull)
            {
                stopped = true;
                results.Add(new(c.Row.Id, OutcomeQuotaFull, NotifySuppressReason.Label(after.SuppressReason)));
                break;
            }
            failed++;
            results.Add(new(c.Row.Id, OutcomeFailed, after?.Note ?? detail ?? "ส่งไม่สำเร็จ"));
        }

        var stillPending = candidates.Count - sent - failed - results.Count(r => r.Outcome == OutcomeBusy && candidates.Any(c => c.Row.Id == r.LogId));
        var message = CustomerNotifyResend.ResultMessage(sent, failed, skipped, Math.Max(0, stillPending), stopped);

        if (candidates.Count > 0)
        {
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                Action = AuditAction.Update,
                EntityType = "CustomerNotifyLog",
                EntityId = "resend",
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "NotifyResend", requested = explicitIds ? logIds!.Count : candidates.Count, sent, failed, skipped,
                    stoppedForQuota = stopped, sentLogIds = results.Where(r => r.Outcome == OutcomeSent).Select(r => r.LogId), by = actor,
                }),
            });
            await _db.SaveChangesAsync(ct);
        }
        _logger.LogInformation("บริษัท {CompanyId} ส่งซ้ำข้อความค้าง: ส่ง {Sent} ล้ม {Failed} ข้าม {Skipped} หยุดเพราะโควตา {Stopped}", companyId, sent, failed, skipped, stopped);
        return new CustomerNotifyResendResult(explicitIds ? logIds!.Count : candidates.Count, sent, failed, skipped, Math.Max(0, stillPending), stopped, message,
            stopped ? TopUpUrl : null, results);
    }
}
