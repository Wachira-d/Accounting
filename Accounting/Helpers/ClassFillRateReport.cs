namespace Accounting.Helpers;

/// <summary>สถานะการเข้าเรียนของการจองหนึ่งรายการในรายงานคลาส (แปลงจาก <c>BookingStatus</c> / <c>CreditBookingStatus</c> ที่ผู้เรียก)</summary>
public enum ClassAttendance { Upcoming = 0, Attended = 1, NoShow = 2, Cancelled = 3 }

/// <summary>การจองหนึ่งรายการของรายงานคลาส — คีย์รอบ = บริการ + วัน + เวลาเริ่ม (ตรงกับที่ engine นับ "รอบเดียวกัน" · คำตัดสินข้อ 168 ฌ) ·
/// <paramref name="Seats"/> = ที่นั่งที่กิน (บริการนับคน = จำนวนคน · นับคิว = 1) · <paramref name="Capacity"/> = ความจุของรอบ (≤ 0 = ไม่รู้)</summary>
public sealed record ClassBookingInput(
    string Source, Guid? ServiceId, string ServiceName, DateTime Date, string StartTime, int Capacity, int Seats,
    ClassAttendance Status, string? ContactKey, IReadOnlyList<(Guid Id, string Name)> Instructors);

/// <summary>รอบเรียนที่ตั้งไว้จริง (รอบของโมดูลคอร์ส / <c>SiteClassSession</c> เมื่อมี) — ให้รอบที่ไม่มีคนจองเลยปรากฏเป็น 0% · ทับความจุ/ผู้สอนของการจองได้</summary>
public sealed record ClassSessionInput(
    string Source, Guid? ServiceId, string ServiceName, DateTime Date, string StartTime, int Capacity, bool Cancelled,
    IReadOnlyList<(Guid Id, string Name)> Instructors);

public sealed record ClassFillRow(string Source, string ServiceName, DateTime Date, string StartTime, int Capacity, int Booked, int Attended,
    int NoShow, int Cancelled, decimal? FillRatePercent, decimal? NoShowRatePercent, string Instructors, bool SessionCancelled);

public sealed record ClassFillGroup(string Key, string Name, int Classes, int Capacity, int Booked, int Attended, int NoShow, int Cancelled,
    decimal? FillRatePercent, decimal? NoShowRatePercent);

public sealed record ClassFillTotals(int Classes, int Capacity, int Booked, int Attended, int NoShow, int Cancelled,
    decimal? FillRatePercent, decimal? NoShowRatePercent);

/// <summary>กิจกรรมของสมาชิกหนึ่งครั้ง (มาเรียน/ใช้สิทธิ์) — retention นับจากวันที่ของแถว</summary>
public sealed record MemberActivity(string ContactKey, string? Name, DateTime Date);

public sealed record AtRiskMember(string ContactKey, string? Name, DateTime LastActivity, int DaysSince);

public sealed record RetentionSummary(int ActiveThisPeriod, int ActiveLastPeriod, int Retained, int Churned, int New,
    decimal? RetentionRatePercent, IReadOnlyList<AtRiskMember> AtRisk);

public sealed record ExpiringPassRow(Guid PassId, string PassNumber, string? ContactName, string PackageName, DateTime ExpiresOn, int DaysLeft,
    string RemainingLabel, decimal DeferredBalance, bool Notified);

/// <summary>ผลรายงานคลาสทั้งหน้า (endpoint <c>GET course-credits/reports/classes</c>)</summary>
public sealed record ClassReportResponse(DateTime From, DateTime To, IReadOnlyList<ClassFillRow> Classes, IReadOnlyList<ClassFillGroup> ByService,
    IReadOnlyList<ClassFillGroup> ByInstructor, ClassFillTotals Totals, RetentionSummary Retention, IReadOnlyList<ExpiringPassRow> Expiring,
    IReadOnlyList<string> Notes);

/// <summary>
/// รอบ 209 · P5 (R-209-C7) — <b>ตัวประกอบเดียว</b>ของรายงาน fill-rate / no-show / retention ของคลาส — pure (เทสต์ได้โดยไม่ต้องมีฐานข้อมูล)
/// <para><b>นิยาม:</b> fill-rate = ที่นั่งที่ถูกจอง (มา + ไม่มา + ยังไม่ถึง · ไม่รวมยกเลิก) ÷ ความจุ × 100 · no-show rate = ไม่มา ÷ (มา + ไม่มา) × 100 ·
/// ความจุไม่รู้ (≤ 0) ⇒ fill-rate = null (ไม่แต่งตัวเลข · F2 #3) · ตัวหารเป็น 0 ⇒ null · ปัด 2 ตำแหน่ง AwayFromZero</para>
/// <para><b>คีย์รอบ</b> = แหล่ง + บริการ + วัน + เวลาเริ่ม — ทำงานได้ทั้งมีและไม่มีตารางรอบจริง: ไม่มี ⇒ รอบที่ไม่มีคนจองเลยจะไม่ปรากฏ (หมายเหตุบนรายงาน) ·
/// มี (<see cref="ClassSessionInput"/>) ⇒ รอบว่างนับเป็น 0% และความจุ/ผู้สอนของรอบชนะค่าจากการจอง</para>
/// <para><b>Retention:</b> สมาชิกที่มีกิจกรรมช่วงนี้ vs ช่วงก่อนยาวเท่ากัน · คงอยู่ = มีทั้งสองช่วง · หาย = มีช่วงก่อนแต่ไม่มีช่วงนี้ · ใหม่ = มีช่วงนี้เท่านั้น ·
/// เสี่ยงหาย = กิจกรรมล่าสุดเกิน N วัน (นับถึงวันสิ้นช่วง) ในกลุ่มที่เคยมาช่วงก่อนหรือช่วงนี้</para>
/// </summary>
public static class ClassFillRateReport
{
    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static decimal? Rate(int numerator, int denominator) => denominator <= 0 ? null : R2(numerator * 100m / denominator);

    private static string SlotKey(string source, Guid? serviceId, string serviceName, DateTime date, string start)
        => $"{source}|{serviceId?.ToString() ?? serviceName}|{date:yyyy-MM-dd}|{start}";

    private sealed class Slot
    {
        public string Source = "", ServiceName = "", StartTime = "";
        public Guid? ServiceId;
        public DateTime Date;
        public int Capacity, Booked, Attended, NoShow, Cancelled;
        public bool SessionCancelled, HasSession;
        public Dictionary<Guid, string> Instructors = new();
    }

    public static (IReadOnlyList<ClassFillRow> Classes, IReadOnlyList<ClassFillGroup> ByService, IReadOnlyList<ClassFillGroup> ByInstructor, ClassFillTotals Totals)
        Build(IReadOnlyList<ClassBookingInput> bookings, IReadOnlyList<ClassSessionInput>? sessions = null)
    {
        var slots = new Dictionary<string, Slot>();
        Slot Get(string source, Guid? serviceId, string serviceName, DateTime date, string start)
        {
            var k = SlotKey(source, serviceId, serviceName, date.Date, start);
            if (!slots.TryGetValue(k, out var s))
                slots[k] = s = new Slot { Source = source, ServiceId = serviceId, ServiceName = serviceName, Date = date.Date, StartTime = start };
            return s;
        }
        foreach (var x in sessions ?? Array.Empty<ClassSessionInput>())
        {
            var s = Get(x.Source, x.ServiceId, x.ServiceName, x.Date, x.StartTime);
            s.HasSession = true;
            s.Capacity = x.Capacity;
            s.SessionCancelled = x.Cancelled;
            if (x.Instructors.Count > 0) { s.Instructors.Clear(); foreach (var i in x.Instructors) s.Instructors[i.Id] = i.Name; }
        }
        foreach (var b in bookings)
        {
            var s = Get(b.Source, b.ServiceId, b.ServiceName, b.Date, b.StartTime);
            if (!s.HasSession && b.Capacity > s.Capacity) s.Capacity = b.Capacity;
            var seats = Math.Max(1, b.Seats);
            switch (b.Status)
            {
                case ClassAttendance.Cancelled: s.Cancelled += seats; break;
                case ClassAttendance.Attended: s.Attended += seats; s.Booked += seats; break;
                case ClassAttendance.NoShow: s.NoShow += seats; s.Booked += seats; break;
                default: s.Booked += seats; break;
            }
            if (!s.HasSession) foreach (var i in b.Instructors) s.Instructors.TryAdd(i.Id, i.Name);
        }
        // รอบที่ร้านยกเลิกทั้งรอบไม่นับเป็น "คลาส" ใน fill-rate (ไม่มีการสอนเกิดขึ้น) — ยังแสดงในรายการให้เห็น
        var rows = slots.Values
            .OrderBy(s => s.Date).ThenBy(s => s.StartTime, StringComparer.Ordinal).ThenBy(s => s.ServiceName, StringComparer.Ordinal)
            .Select(s => new ClassFillRow(s.Source, s.ServiceName, s.Date, s.StartTime, s.Capacity, s.Booked, s.Attended, s.NoShow, s.Cancelled,
                s.SessionCancelled || s.Capacity <= 0 ? null : Rate(s.Booked, s.Capacity), Rate(s.NoShow, s.Attended + s.NoShow),
                string.Join(", ", s.Instructors.Values.OrderBy(n => n, StringComparer.Ordinal)), s.SessionCancelled))
            .ToList();
        var live = slots.Values.Where(s => !s.SessionCancelled).ToList();

        ClassFillGroup Group(string key, string name, IReadOnlyCollection<Slot> g)
        {
            var known = g.Where(s => s.Capacity > 0).ToList();
            return new ClassFillGroup(key, name, g.Count, known.Sum(s => s.Capacity), g.Sum(s => s.Booked), g.Sum(s => s.Attended), g.Sum(s => s.NoShow),
                g.Sum(s => s.Cancelled), Rate(known.Sum(s => s.Booked), known.Sum(s => s.Capacity)),
                Rate(g.Sum(s => s.NoShow), g.Sum(s => s.Attended) + g.Sum(s => s.NoShow)));
        }

        var byService = live.GroupBy(s => s.ServiceId?.ToString() ?? s.ServiceName)
            .Select(g => Group(g.Key, g.First().ServiceName, g.ToList()))
            .OrderByDescending(x => x.Classes).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        var byInstructor = live.SelectMany(s => s.Instructors.Count == 0
                ? new[] { (Key: "-", Name: "ยังไม่ระบุผู้สอน", Slot: s) }
                : s.Instructors.Select(i => (Key: i.Key.ToString(), Name: i.Value, Slot: s)).ToArray())
            .GroupBy(x => x.Key)
            .Select(g => Group(g.Key, g.First().Name, g.Select(x => x.Slot).ToList()))
            .OrderBy(x => x.Key == "-" ? 1 : 0).ThenByDescending(x => x.Classes).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        var knownAll = live.Where(s => s.Capacity > 0).ToList();
        var totals = new ClassFillTotals(live.Count, knownAll.Sum(s => s.Capacity), live.Sum(s => s.Booked), live.Sum(s => s.Attended),
            live.Sum(s => s.NoShow), live.Sum(s => s.Cancelled), Rate(knownAll.Sum(s => s.Booked), knownAll.Sum(s => s.Capacity)),
            Rate(live.Sum(s => s.NoShow), live.Sum(s => s.Attended) + live.Sum(s => s.NoShow)));
        return (rows, byService, byInstructor, totals);
    }

    /// <summary>Retention ระหว่างช่วงนี้ [<paramref name="from"/>, <paramref name="to"/>] กับช่วงก่อนยาวเท่ากัน (วันปฏิทิน รวมหัวท้าย) ·
    /// เสี่ยงหาย = กิจกรรมล่าสุด (ถึง <paramref name="to"/>) เก่ากว่า <paramref name="atRiskDays"/> วัน · เรียงจากหายนานสุด</summary>
    public static RetentionSummary Retention(IEnumerable<MemberActivity> activities, DateTime from, DateTime to, int atRiskDays)
    {
        var f = from.Date; var t = to.Date;
        var len = Math.Max(1, (t - f).Days + 1);
        var pf = f.AddDays(-len); var pt = f.AddDays(-1);
        var list = activities.Where(a => !string.IsNullOrEmpty(a.ContactKey) && a.Date.Date <= t).ToList();
        var thisSet = list.Where(a => a.Date.Date >= f && a.Date.Date <= t).Select(a => a.ContactKey).ToHashSet();
        var lastSet = list.Where(a => a.Date.Date >= pf && a.Date.Date <= pt).Select(a => a.ContactKey).ToHashSet();
        var retained = thisSet.Count(k => lastSet.Contains(k));
        var churned = lastSet.Count(k => !thisSet.Contains(k));
        var fresh = thisSet.Count(k => !lastSet.Contains(k));
        var cohort = thisSet.Union(lastSet).ToHashSet();
        var atRisk = list.Where(a => cohort.Contains(a.ContactKey))
            .GroupBy(a => a.ContactKey)
            .Select(g => { var last = g.OrderByDescending(a => a.Date).First(); return new AtRiskMember(g.Key, last.Name, last.Date.Date, (t - last.Date.Date).Days); })
            .Where(m => atRiskDays > 0 && m.DaysSince > atRiskDays)
            .OrderByDescending(m => m.DaysSince).ThenBy(m => m.Name ?? "", StringComparer.Ordinal)
            .ToList();
        return new RetentionSummary(thisSet.Count, lastSet.Count, retained, churned, fresh, Rate(retained, lastSet.Count), atRisk);
    }

    /// <summary>จำนวนวันที่เหลือถึงวันหมดอายุ (วันหมดอายุยังใช้ได้ ⇒ วันนี้ = 0 · เลยแล้ว = ติดลบ)</summary>
    public static int DaysLeft(DateTime expiresOn, DateTime today) => (expiresOn.Date - today.Date).Days;
}
