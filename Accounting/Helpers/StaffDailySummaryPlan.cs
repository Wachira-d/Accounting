using Accounting.Models.Constants;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>สรุปประจำวันทาง LINE ถึงพนักงาน (รอบ 212 C · คำตัดสินเจ้าของข้อ 5 · <c>erp-review/2026-10-30/NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN.md</c> §2.5 · §5) —
/// <b>ตัวตัดสินเดียว (OWNER)</b> ของ: แคตตาล็อกหัวข้อ + สิทธิ์ที่แต่ละหัวข้อต้องมี · เวลาถึงกำหนด (เวลาไทย) · การกรองข้อมูล<b>ต่อผู้รับ</b> ·
/// สถานะของแถวส่ง (อันไหนจบแล้ว/ลองใหม่ได้) — งาน (<c>StaffDailySummaryJob</c>) · บริการ · หน้าตั้งค่า อ่านจากที่นี่ที่เดียว (หน้าเว็บไม่มีสำเนาป้าย/สิทธิ์)
///
/// <para><b>กรองต่อผู้รับ</b> (§6 "ประกอบต่อผู้รับ ไม่ประกอบครั้งเดียวแล้วแจกทุกคน"): ข้อมูลของบริษัทถูกอ่านครั้งเดียวต่อรอบ แล้ว
/// <see cref="FilterForRecipient"/> ตัดให้เหลือเฉพาะ (1) หัวข้อที่ผู้รับมีสิทธิ์ (ไม่มีสิทธิ์ = <b>ไม่มีหัวข้อ</b> ไม่ใช่หัวข้อว่าง) และ
/// (2) แถวของเว็บที่ผู้รับเห็นตามกติกา <see cref="CmsSiteScope.AllowedSiteIds"/> — กติกาเดียวกับหน้าหลังบ้าน ⇒ LINE ไม่เปิดสิ่งที่จอไม่เปิด</para></summary>
public static class StaffDailySummaryPlan
{
    public const string RuleCode = "STAFF-SUMMARY-212";

    // ─────────────── หัวข้อ ───────────────

    public const string Bookings = "bookings";
    public const string Classes = "classes";
    public const string MadeToOrder = "mto";
    public const string ToShip = "ship";
    public const string Lodging = "lodging";
    public const string Credits = "credits";

    /// <summary>ขอบเขตเว็บของหัวข้อ — <see cref="None"/> = ข้อมูลระดับบริษัท (ไม่ผูกเว็บ)</summary>
    public enum SiteScopeKind { None, AnyRole, OrderRoles }

    /// <param name="AnyPermission">ต้องมีอย่างน้อยหนึ่งคีย์ (ว่าง = ไม่ต้องมีคีย์ระดับบริษัท — อาศัยสิทธิ์รายเว็บอย่างเดียว)</param>
    /// <param name="Page">หน้าหลังบ้านของหัวข้อ (ลิงก์ "ดูเพิ่มเติม" เมื่อข้อความถูกตัด)</param>
    public sealed record SectionDef(string Key, string Label, string Hint, IReadOnlyList<string> AnyPermission, SiteScopeKind Scope, string Page);

    /// <summary>ลำดับในรายการ = ลำดับในข้อความ</summary>
    public static readonly IReadOnlyList<SectionDef> Sections = new[]
    {
        new SectionDef(Bookings, "การจองวันนี้/พรุ่งนี้", "แยกตามผู้ให้บริการ · บริการ", Array.Empty<string>(), SiteScopeKind.AnyRole, "/pages/cms-bookings.html"),
        new SectionDef(Classes, "คลาสวันนี้/พรุ่งนี้ + ที่นั่ง", "ที่นั่งที่จองแล้ว/ทั้งหมด · คิวสำรอง", Array.Empty<string>(), SiteScopeKind.AnyRole, "/pages/cms-bookings.html"),
        new SectionDef(MadeToOrder, "คิวผลิตตามสั่ง", "วันพร้อมรับภายใน 7 วัน (รวมที่เลยกำหนด) · ยอดค้างชำระ", new[] { PermissionKeys.CmsOrderManage }, SiteScopeKind.OrderRoles, "/pages/cms-orders.html"),
        new SectionDef(ToShip, "ออเดอร์รอจัดส่ง", "ยืนยันแล้ว/กำลังเตรียม", new[] { PermissionKeys.CmsOrderManage }, SiteScopeKind.OrderRoles, "/pages/cms-orders.html"),
        new SectionDef(Lodging, "ที่พัก: เช็คอิน/เช็คเอาต์วันนี้", "เฉพาะบริษัทที่มีที่พัก", new[] { PermissionKeys.LodgingManage }, SiteScopeKind.None, "/pages/lodging.html"),
        new SectionDef(Credits, "บัตรคอร์สใกล้หมดอายุ (7 วัน)", "บัตรที่ยังใช้งาน · เครดิตคงเหลือ", new[] { PermissionKeys.PosManager, PermissionKeys.PosCashier, PermissionKeys.CmsOrderManage }, SiteScopeKind.None, "/pages/course-credits.html"),
    };

    public static SectionDef? Find(string? key) => Sections.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>หัวข้อเริ่มต้นของชุดใหม่ = ทุกหัวข้อ</summary>
    public static string DefaultSectionsCsv => string.Join(",", Sections.Select(s => s.Key));

    /// <summary>csv → คีย์ที่รู้จัก (ไม่ซ้ำ · เรียงตามแคตตาล็อก) — คีย์แปลกถูกทิ้ง (ไม่ throw: ค่าเก่าในฐานไม่ทำงานพัง)</summary>
    public static IReadOnlyList<string> ParseSections(string? csv)
    {
        var want = (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).ToHashSet();
        return Sections.Where(s => want.Contains(s.Key)).Select(s => s.Key).ToList();
    }

    public static string SectionsCsv(IEnumerable<string>? keys) => string.Join(",", ParseSections(string.Join(",", keys ?? Array.Empty<string>())));

    /// <summary>คีย์ที่ส่งมาแต่ไม่รู้จัก (หน้าจอบอกผู้ใช้ ไม่ทิ้งเงียบ)</summary>
    public static IReadOnlyList<string> UnknownSections(IEnumerable<string>? keys)
        => (keys ?? Array.Empty<string>()).Where(k => Find(k) == null).Distinct().ToList();

    // ─────────────── ผู้รับ ───────────────

    /// <summary>บทบาทบริษัทที่เลือกเป็นผู้รับได้ (ชื่อ enum + ป้าย) — ผู้ดูแลแพลตฟอร์มไม่อยู่ในรายการ</summary>
    public static readonly IReadOnlyList<(UserRole Role, string Label)> RecipientRoles = new[]
    {
        (UserRole.Owner, "เจ้าของบริษัท"), (UserRole.Accountant, "นักบัญชี"), (UserRole.Staff, "พนักงาน"),
        (UserRole.Auditor, "ผู้ตรวจสอบ"), (UserRole.Viewer, "ดูอย่างเดียว"), (UserRole.ExternalAccountant, "นักบัญชีภายนอก"),
    };

    public static IReadOnlyList<UserRole> ParseRoles(string? csv)
        => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Enum.TryParse<UserRole>(x, true, out var r) ? (UserRole?)r : null)
            .Where(r => r != null && RecipientRoles.Any(x => x.Role == r)).Select(r => r!.Value).Distinct().ToList();

    public static string RolesCsv(IEnumerable<UserRole>? roles) => string.Join(",", (roles ?? Array.Empty<UserRole>()).Distinct().Select(r => r.ToString()));

    public static IReadOnlyList<Guid> ParseUserIds(string? csv)
        => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();

    public static string UserIdsCsv(IEnumerable<Guid>? ids) => string.Join(",", (ids ?? Array.Empty<Guid>()).Where(g => g != Guid.Empty).Distinct());

    /// <summary>ผู้รับ = เลือกรายคน ∪ ตามบทบาท (สมาชิกของบริษัทเท่านั้น — ผู้เรียกส่งรายชื่อสมาชิกที่กรองบริษัทมาแล้ว)</summary>
    public static IReadOnlyList<Guid> ResolveRecipients(IEnumerable<(Guid UserId, UserRole Role)> members, IReadOnlyCollection<Guid> userIds, IReadOnlyCollection<UserRole> roles)
        => members.Where(m => userIds.Contains(m.UserId) || roles.Contains(m.Role)).Select(m => m.UserId).Distinct().ToList();

    // ─────────────── เวลา (Asia/Bangkok) ───────────────

    /// <summary>ส่งตามหลังได้ถึงกี่โมง (เครื่องล่ม/งานค้าง) — หลังจากนี้ไม่ส่งของวันนั้น (ไม่ backfill ข้ามวัน)</summary>
    public static readonly TimeOnly CatchUpUntil = new(23, 0);

    public static readonly TimeOnly DefaultSendAt = new(7, 30);

    /// <summary>ปัดลงเป็นช่วง 5 นาที (งานเดินรอบละ 5 นาที — เวลาละเอียดกว่านี้ไม่มีความหมาย)</summary>
    public static TimeOnly RoundSendAt(TimeOnly t) => new(t.Hour, t.Minute / 5 * 5);

    /// <summary>ถึงกำหนดส่งของ "วันนี้" หรือยัง — <paramref name="nowBangkok"/> = เวลาไทย (<see cref="BookingSlotEngine.BangkokNow"/>) ·
    /// ส่งแล้ววันนี้ (watermark) ⇒ ไม่ · ยังไม่ถึงเวลา ⇒ ไม่ · เลย <see cref="CatchUpUntil"/> ⇒ ไม่ (พรุ่งนี้ค่อยว่ากัน)</summary>
    public static bool Due(DateTime nowBangkok, TimeOnly sendAt, DateOnly? lastSentLocalDate)
    {
        var today = DateOnly.FromDateTime(nowBangkok);
        if (lastSentLocalDate is DateOnly last && last >= today) return false;
        var t = TimeOnly.FromDateTime(nowBangkok);
        return t >= RoundSendAt(sendAt) && t < CatchUpUntil;
    }

    // ─────────────── สถานะแถวส่ง (StaffDailySummaryDelivery) ───────────────

    public const string StatusSending = "Sending";
    public const string StatusSent = "Sent";
    public const string StatusFailed = "Failed";
    public const string StatusNoLine = "NoLine";
    public const string StatusNothingVisible = "NothingVisible";
    public const string StatusQuotaExceeded = "QuotaExceeded";

    /// <summary>ส่งล้มลองใหม่ได้กี่ครั้งต่อวัน (รอบละ 5 นาที)</summary>
    public const int MaxAttempts = 3;

    /// <summary>แถว "กำลังส่ง" ที่ค้างนานกว่านี้ (เครื่องล่มกลางคัน) รับช่วงได้</summary>
    public static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(15);

    /// <summary>แถวนี้จบสำหรับวันนี้แล้วหรือยัง (watermark ต่อบริษัทขยับได้เมื่อทุกผู้รับจบ) — ล้มแต่ยังไม่ครบโควตาลอง = ยังไม่จบ ·
    /// กำลังส่ง = ยังไม่จบ (อีกรอบ/อีกเครื่องรับช่วงเมื่อค้าง)</summary>
    public static bool IsTerminal(string? status, int attempts)
        => status switch
        {
            // ฝ่ายค้านรอบ 212 S4: โควตาเต็ม ≠ จบวัน — เติมแพ็กแล้วรอบถัดไปส่งต่อได้ถึง CatchUpUntil (Due ปิดเองหลัง 23:00 · ไม่ backfill ข้ามวัน)
            StatusSent or StatusNoLine or StatusNothingVisible => true,
            StatusQuotaExceeded => false,
            StatusFailed => attempts >= MaxAttempts,
            _ => false,
        };

    /// <summary>ป้ายภาษาไทยของสถานะ (หน้าประวัติ)</summary>
    public static string StatusLabel(string? status) => status switch
    {
        StatusSent => "ส่งแล้ว",
        StatusSending => "กำลังส่ง",
        StatusFailed => "ส่งไม่สำเร็จ",
        StatusNoLine => "ยังไม่ผูก LINE",
        StatusNothingVisible => "ไม่มีหัวข้อที่มีสิทธิ์ดู",
        StatusQuotaExceeded => "โควตาข้อความเต็ม",
        _ => status ?? "-",
    };

    /// <summary>คีย์กันซ้ำของโควตาข้อความช่องกลาง (1 หน่วย/ผู้รับ/วัน · §10)</summary>
    public static string MessageKey(Guid configId, DateOnly localDate, Guid userId) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"staffsum:{configId:N}:{localDate:yyyyMMdd}:{userId:N}");   // InvariantCulture: th-TH ให้ปี พ.ศ.

    // ─────────────── ข้อมูล + การกรองต่อผู้รับ ───────────────

    public sealed record BookingRow(Guid SiteId, DateOnly Date, TimeOnly Start, string Service, string Provider, string Customer);
    public sealed record ClassRow(Guid SiteId, DateOnly Date, TimeOnly Start, string Service, string? Instructor, int Taken, int Capacity, int Waitlisted);
    public sealed record OrderRow(Guid SiteId, string Number, string Customer, DateOnly? ReadyDate, decimal Outstanding, string Status);
    public sealed record LodgingRow(string Property, string Number, string Guest, bool CheckIn, bool CheckOut);
    public sealed record CreditRow(string PassNumber, string Customer, string Package, DateOnly ExpiresOn, decimal Remaining);

    /// <summary>ข้อมูลหนึ่งรอบ — หัวข้อที่เป็น <c>null</c> = ไม่อยู่ในข้อความ (ไม่ได้เลือก/ไม่มีสิทธิ์/ไม่มีโมดูล) · รายการว่าง = "ไม่มีรายการ"</summary>
    public sealed record SummaryData(
        IReadOnlyList<BookingRow>? Bookings,
        IReadOnlyList<ClassRow>? Classes,
        IReadOnlyList<OrderRow>? MadeToOrder,
        IReadOnlyList<OrderRow>? ToShip,
        IReadOnlyList<LodgingRow>? Lodging,
        IReadOnlyList<CreditRow>? Credits)
    {
        public bool HasAnySection => Bookings != null || Classes != null || MadeToOrder != null || ToShip != null || Lodging != null || Credits != null;
    }

    /// <summary>สิทธิ์ของผู้รับหนึ่งคน ณ เวลาส่ง</summary>
    /// <param name="GrantedKeys">คีย์สิทธิ์ที่ผู้รับผ่าน (เฉพาะคีย์ที่หัวข้อในแคตตาล็อกถาม — ผู้เรียกตรวจด้วย <c>IPermissionService.HasPermissionAsync</c>)</param>
    /// <param name="AnyRoleSiteIds">เว็บที่เห็นการจอง/คลาส (<see cref="CmsSiteScope.AnyRole"/>)</param>
    /// <param name="OrderSiteIds">เว็บที่เห็นออเดอร์ (<see cref="CmsSiteScope.OrderRoles"/>)</param>
    public sealed record RecipientScope(IReadOnlyCollection<string> GrantedKeys, IReadOnlyCollection<Guid> AnyRoleSiteIds, IReadOnlyCollection<Guid> OrderSiteIds);

    /// <summary>หัวข้อที่ผู้รับคนนี้ได้ = ที่ชุดเลือก ∩ มีคีย์สิทธิ์ (ถ้าหัวข้อถาม) ∩ มีเว็บที่เห็นอย่างน้อย 1 (ถ้าหัวข้อผูกเว็บ) ∩ บริษัทมีที่พัก (หัวข้อที่พัก)</summary>
    private static IReadOnlyList<string> AllowedSections(IReadOnlyCollection<string> selected, RecipientScope scope, bool lodgingActive)
    {
        var result = new List<string>();
        foreach (var s in Sections)
        {
            if (!selected.Contains(s.Key)) continue;
            if (s.AnyPermission.Count > 0 && !s.AnyPermission.Any(k => scope.GrantedKeys.Contains(k))) continue;
            if (s.Scope == SiteScopeKind.AnyRole && scope.AnyRoleSiteIds.Count == 0) continue;
            if (s.Scope == SiteScopeKind.OrderRoles && scope.OrderSiteIds.Count == 0) continue;
            if (s.Key == Lodging && !lodgingActive) continue;
            result.Add(s.Key);
        }
        return result;
    }

    /// <summary>ตัดข้อมูลของบริษัทให้เหลือเฉพาะที่ผู้รับเห็น — <b>ต้องเรียกก่อนประกอบข้อความทุกครั้ง</b>
    /// (<c>tools/required_call_site_check.py</c> ล็อกลำดับใน <c>StaffDailySummaryService</c>)</summary>
    public static SummaryData FilterForRecipient(SummaryData company, IReadOnlyCollection<string> selected, RecipientScope scope, bool lodgingActive)
    {
        var allowed = AllowedSections(selected, scope, lodgingActive).ToHashSet();
        var anyRole = scope.AnyRoleSiteIds.ToHashSet();
        var orderSites = scope.OrderSiteIds.ToHashSet();
        return new SummaryData(
            allowed.Contains(Bookings) ? (company.Bookings ?? Array.Empty<BookingRow>()).Where(r => anyRole.Contains(r.SiteId)).ToList() : null,
            allowed.Contains(Classes) ? (company.Classes ?? Array.Empty<ClassRow>()).Where(r => anyRole.Contains(r.SiteId)).ToList() : null,
            allowed.Contains(MadeToOrder) ? (company.MadeToOrder ?? Array.Empty<OrderRow>()).Where(r => orderSites.Contains(r.SiteId)).ToList() : null,
            allowed.Contains(ToShip) ? (company.ToShip ?? Array.Empty<OrderRow>()).Where(r => orderSites.Contains(r.SiteId)).ToList() : null,
            allowed.Contains(Lodging) ? (company.Lodging ?? Array.Empty<LodgingRow>()).ToList() : null,
            allowed.Contains(Credits) ? (company.Credits ?? Array.Empty<CreditRow>()).ToList() : null);
    }

    /// <summary>คีย์สิทธิ์ทั้งหมดที่แคตตาล็อกถาม (ผู้เรียกตรวจทีละคีย์ต่อผู้รับ)</summary>
    public static IReadOnlyList<string> PermissionKeysToCheck()
        => Sections.SelectMany(s => s.AnyPermission).Distinct().ToList();
}
