using Accounting.Helpers;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 · P5 (R-209-C7) — รายงานคลาส: fill-rate ต่อคลาส/บริการ/ผู้สอน · no-show · retention สมาชิก · บัตรใกล้หมดอายุ ·
/// ตัวประกอบตัวเลขเดียว <see cref="ClassFillRateReport"/> (pure) — ที่นี่อ่านข้อมูลอย่างเดียว (ไม่เขียน) · ทุกคิวรีกรอง <c>CompanyId</c>
/// <para>แหล่ง: การจองหน้าเว็บ (<c>SiteBooking</c> ของบริการที่รับ &gt; 1 ที่ต่อรอบ · คีย์รอบ = บริการ + วัน + เวลาเริ่ม) และรอบเรียนของโมดูลคอร์ส
/// (<c>CreditClassSession</c> — รอบว่างนับ 0%) · ยังไม่มีตารางรอบจริงของเครื่องจองเว็บ (<c>SiteClassSession</c> ของแพ็ก P4) ⇒ รอบเว็บที่ไม่มีคนจองไม่ปรากฏ (บอกในหมายเหตุ) ·
/// เมื่อมีตารางนั้นแล้ว ส่งเป็น <see cref="ClassSessionInput"/> ได้ทันทีโดยไม่แก้ตัวประกอบ</para>
/// </summary>
public partial class CourseCreditService
{
    public const int ClassReportMaxDays = 366;
    /// <summary>กิจกรรมล่าสุดเก่ากว่ากี่วัน = เสี่ยงหาย</summary>
    public const int AtRiskDays = 30;
    private const string WebSource = "เว็บ";
    private const string CourseSource = "คอร์ส";

    public async Task<ClassReportResponse> GetClassReportAsync(Guid companyId, DateTime from, DateTime to, int expiringWithinDays, CancellationToken ct = default)
    {
        var f = from.Date; var t = to.Date;
        if (t < f) throw new BusinessRuleException("วันที่สิ้นสุดต้องไม่ก่อนวันที่เริ่ม", CourseCreditMath.RuleCode);
        if ((t - f).TotalDays >= ClassReportMaxDays) throw new BusinessRuleException($"ช่วงรายงานได้ไม่เกิน {ClassReportMaxDays} วัน", CourseCreditMath.RuleCode);
        expiringWithinDays = Math.Clamp(expiringWithinDays, 1, 180);
        var end = t.AddDays(1);
        var len = (t - f).Days + 1;
        var prevFrom = f.AddDays(-len);

        // ── การจองหน้าเว็บของคลาสกลุ่ม ──
        var web = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.BookingDate >= f && b.BookingDate < end && b.Status != BookingStatus.Waitlisted
                && b.BookingService.MaxCapacity > 1)
            .Select(b => new
            {
                b.Id, b.BookingServiceId, ServiceName = b.BookingService.Name, b.BookingDate, b.StartTime, b.Status, b.GuestCount, b.CustomerId,
                b.GuestEmail, b.GuestPhone, b.ResourceId, Capacity = b.BookingService.MaxCapacity, Unit = b.BookingService.CapacityUnit,
            })
            .ToListAsync(ct);
        var webIds = web.Select(b => b.Id).ToList();
        var assigns = await _db.SiteBookingAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && webIds.Contains(a.BookingId))
            .Select(a => new { a.BookingId, a.ResourceId, a.ResourceName }).ToListAsync(ct);
        var resIds = web.Where(b => b.ResourceId != null).Select(b => b.ResourceId!.Value).Distinct().ToList();
        var resNames = await _db.SiteBookingResources.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.CompanyId == companyId && resIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var custIds = web.Where(b => b.CustomerId != null).Select(b => b.CustomerId!.Value).Distinct().ToList();
        var custContacts = await _db.SiteCustomers.AsNoTracking()
            .Where(c => c.CompanyId == companyId && custIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ContactId, c.FullName }).ToDictionaryAsync(c => c.Id, c => (c.ContactId, c.FullName), ct);

        string? WebKey(Guid? customerId, string? email, string? phone)
        {
            if (customerId is Guid cid)
                return custContacts.TryGetValue(cid, out var cc) && cc.ContactId is Guid k ? "k:" + k : "c:" + cid;
            var g = (email ?? phone ?? "").Trim().ToLowerInvariant();
            return g.Length == 0 ? null : "g:" + g;
        }

        var inputs = web.Select(b =>
        {
            var mine = assigns.Where(a => a.BookingId == b.Id).Select(a => (a.ResourceId, a.ResourceName)).ToList();
            if (mine.Count == 0 && b.ResourceId is Guid rid) mine.Add((rid, resNames.GetValueOrDefault(rid) ?? "(ลบแล้ว)"));
            return new ClassBookingInput(WebSource, b.BookingServiceId, b.ServiceName, b.BookingDate, b.StartTime.ToString("HH:mm"), b.Capacity,
                b.Unit == BookingCapacityUnit.Guests ? Math.Max(1, b.GuestCount) : 1, WebAttendance(b.Status), WebKey(b.CustomerId, b.GuestEmail, b.GuestPhone),
                mine);
        }).ToList();

        // ── รอบเรียนของโมดูลคอร์ส (มีตารางรอบจริง) ──
        var fromUtc = DateTime.SpecifyKind(f.AddHours(-7), DateTimeKind.Utc);
        var endUtc = DateTime.SpecifyKind(end.AddHours(-7), DateTimeKind.Utc);
        var sessions = await _db.CreditClassSessions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.StartAt >= fromUtc && s.StartAt < endUtc && s.Capacity > 1)
            .ToListAsync(ct);
        var sessionIds = sessions.Select(s => s.Id).ToList();
        var trainers = await TrainerNamesAsync(companyId, sessions.Where(s => s.TrainerEmployeeId.HasValue).Select(s => s.TrainerEmployeeId!.Value), ct);
        var sessionInputs = sessions.Select(s => new ClassSessionInput(CourseSource, s.BookingServiceId, s.Title, ThaiDate.CalendarDateUtc(s.StartAt),
            s.StartAt.AddHours(7).ToString("HH:mm"), s.Capacity, s.Status == CreditSessionStatus.Cancelled,
            s.TrainerEmployeeId is Guid tid && trainers.TryGetValue(tid, out var tn) ? new List<(Guid, string)> { (tid, tn) } : new List<(Guid, string)>())).ToList();
        var sessionById = sessions.ToDictionary(s => s.Id);
        var courseBookings = await _db.CreditClassBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SessionId != null && sessionIds.Contains(b.SessionId.Value))
            .Select(b => new { SessionId = b.SessionId!.Value, b.Status, b.ContactId }).ToListAsync(ct);
        foreach (var b in courseBookings)
        {
            var s = sessionById[b.SessionId];
            inputs.Add(new ClassBookingInput(CourseSource, s.BookingServiceId, s.Title, ThaiDate.CalendarDateUtc(s.StartAt), s.StartAt.AddHours(7).ToString("HH:mm"),
                s.Capacity, 1, CourseAttendance(b.Status), "k:" + b.ContactId, new List<(Guid, string)>()));
        }
        var (classes, byService, byInstructor, totals) = ClassFillRateReport.Build(inputs, sessionInputs);

        // ── Retention: กิจกรรม = มาเรียน (เว็บ: เสร็จสิ้น · คอร์ส: มาเรียน) ช่วงก่อน + ช่วงนี้ ──
        var acts = new List<MemberActivity>();
        var webDone = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.BookingDate >= prevFrom && b.BookingDate < end && b.Status == BookingStatus.Completed)
            .Select(b => new { b.CustomerId, b.GuestEmail, b.GuestPhone, b.GuestName, b.BookingDate }).ToListAsync(ct);
        var moreCust = webDone.Where(b => b.CustomerId != null && !custContacts.ContainsKey(b.CustomerId.Value)).Select(b => b.CustomerId!.Value).Distinct().ToList();
        if (moreCust.Count > 0)
            foreach (var c in await _db.SiteCustomers.AsNoTracking().Where(c => c.CompanyId == companyId && moreCust.Contains(c.Id))
                         .Select(c => new { c.Id, c.ContactId, c.FullName }).ToListAsync(ct))
                custContacts[c.Id] = (c.ContactId, c.FullName);
        foreach (var b in webDone)
            if (WebKey(b.CustomerId, b.GuestEmail, b.GuestPhone) is string key)
                acts.Add(new MemberActivity(key, b.CustomerId is Guid cid && custContacts.TryGetValue(cid, out var cc) ? cc.FullName ?? b.GuestName : b.GuestName, b.BookingDate));
        var prevFromUtc = DateTime.SpecifyKind(prevFrom.AddHours(-7), DateTimeKind.Utc);
        var courseDone = await _db.CreditClassBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.Status == CreditBookingStatus.Attended && b.StartAt >= prevFromUtc && b.StartAt < endUtc)
            .Select(b => new { b.ContactId, b.StartAt }).ToListAsync(ct);
        var names = await ContactNamesAsync(companyId, courseDone.Select(b => b.ContactId), ct);
        foreach (var b in courseDone)
            acts.Add(new MemberActivity("k:" + b.ContactId, names.GetValueOrDefault(b.ContactId), ThaiDate.CalendarDateUtc(b.StartAt)));
        var retention = ClassFillRateReport.Retention(acts, f, t, AtRiskDays);

        // ── บัตรใกล้หมดอายุ ──
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var horizon = today.AddDays(expiringWithinDays);
        var expiring = await _db.CreditPasses.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.Status == CreditPassStatus.Active && p.ExpiresOn != null && p.ExpiresOn >= today && p.ExpiresOn <= horizon)
            .OrderBy(p => p.ExpiresOn).Take(500).ToListAsync(ct);
        var passNames = await ContactNamesAsync(companyId, expiring.Select(p => p.ContactId), ct);
        var expiringRows = expiring.Select(p => new ExpiringPassRow(p.Id, p.PassNumber, passNames.GetValueOrDefault(p.ContactId), p.PackageName, p.ExpiresOn!.Value,
            ClassFillRateReport.DaysLeft(p.ExpiresOn.Value, today),
            CourseCreditMath.IsUnlimited(p.Kind) ? "ไม่จำกัดครั้ง" : $"{CourseCreditMath.Available(p.CreditsTotal, p.CreditsUsed, p.CreditsReserved):0.##} {UnitTh(p.Unit)}",
            p.ContractAmount - p.RecognizedAmount - p.RefundedAmount - p.TransferredOutAmount, p.ExpiryNotifiedAt != null)).ToList();

        var notes = new List<string>
        {
            "นับเฉพาะบริการ/รอบที่รับได้มากกว่า 1 ที่ (คลาสกลุ่ม) · fill-rate = ที่นั่งที่จอง (มา + ไม่มา + ยังไม่ถึง) ÷ ความจุ",
            "การจองหน้าเว็บยังไม่มีตารางรอบจริง — รอบที่ไม่มีคนจองเลยจะไม่ปรากฏ (รอบของโมดูลคอร์สนับรอบว่างเป็น 0%)",
            $"เสี่ยงหาย = มาเรียนครั้งล่าสุดเกิน {AtRiskDays} วัน (นับถึงวันสิ้นช่วง)",
        };
        return new ClassReportResponse(f, t, classes, byService, byInstructor, totals, retention, expiringRows, notes);
    }

    private static ClassAttendance WebAttendance(BookingStatus s) => s switch
    {
        BookingStatus.Completed => ClassAttendance.Attended,
        BookingStatus.NoShow => ClassAttendance.NoShow,
        BookingStatus.Cancelled => ClassAttendance.Cancelled,
        _ => ClassAttendance.Upcoming,
    };

    private static ClassAttendance CourseAttendance(CreditBookingStatus s) => s switch
    {
        CreditBookingStatus.Attended => ClassAttendance.Attended,
        CreditBookingStatus.NoShow => ClassAttendance.NoShow,
        CreditBookingStatus.Cancelled or CreditBookingStatus.LateCancelled => ClassAttendance.Cancelled,
        _ => ClassAttendance.Upcoming,
    };
}
