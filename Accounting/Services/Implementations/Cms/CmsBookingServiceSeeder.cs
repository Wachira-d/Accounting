using Accounting.Data;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Cms;

/// <summary>seed "บริการที่จองได้" ตัวอย่างให้เว็บที่มีหน้า /booking แบบ slot (BookingCalendar) —
/// สปา · คลินิก · ร้านอาหาร (จองโต๊ะ) · อสังหา (นัดดูทรัพย์) · เว็บชนิด Booking ทั่วไป.
/// ที่มา (รอบ 158, ทีมตรวจเทมเพลต D-08): หน้าจองของทุกเทมเพลตขึ้น "ยังไม่มีบริการให้จองในขณะนี้"
/// จนกว่าเจ้าของจะไปตั้งเองใน cms-edit ขณะที่เว็บที่พักได้ห้อง/ราคาครบตั้งแต่สร้าง = สองมาตรฐาน.
/// idempotent ต่อเว็บ: มีบริการอยู่แล้วแม้ตัวเดียว → ไม่แตะ (ของที่เจ้าของตั้งไว้ต้องชนะเสมอ)
/// <para>ทีม B (engine รอบจอง): ตัวอย่างทุกตัวมา <b>พร้อมเวลาทำการ + ความละเอียดรอบ + buffer + ความจุ</b> — เดิม seed แค่บริการ
/// ไม่มีแถวเวลา ⇒ หน้าเวลาว่างตอบว่างทุกวัน (ลูกค้าจองไม่ได้เลยทั้งที่การ์ดบริการขึ้น) · ตาราง <see cref="Presets"/> เป็นตัวตั้งเดียวของ
/// "แม่แบบตามประเภทธุรกิจ" ทั้งตอน seed และปุ่ม "เพิ่มจากแม่แบบ" ในหน้า CMS (หน้าเว็บไม่มีสำเนาค่า)</para></summary>
public static class CmsBookingServiceSeeder
{
    /// <summary>ช่วงเวลาทำการรายสัปดาห์ — <paramref name="Days"/> = เลขวัน (0=อาทิตย์ … 6=เสาร์) เช่น "1-5" · "0,6" · "*" ·
    /// <paramref name="Break"/> = ช่วงพัก (ปิด) ของวันเดียวกัน</summary>
    public sealed record Hours(string Days, string Start, string End, bool Break = false);

    public sealed record Sample(string Name, string NameEn, int Minutes, decimal Price, string Category, string Description, BookingType Type, int Capacity)
    {
        public BookingSlotMode Mode { get; init; } = BookingSlotMode.Interval;
        public int? Interval { get; init; }
        public int BufferBefore { get; init; }
        public int BufferAfter { get; init; }
        public BookingCapacityUnit Unit { get; init; } = BookingCapacityUnit.Bookings;
        public int MaxUnits { get; init; } = 1;
        public string? HalfDaySplit { get; init; }
        public string? CapacityGroup { get; init; }
        public int MinAdvanceHours { get; init; } = 1;
        public int MaxAdvanceDays { get; init; } = 30;
        public decimal? DepositPercent { get; init; }
        public int? FreeCancellationHours { get; init; }
        public decimal? CancellationFeePercent { get; init; }
        public Hours[] OpeningHours { get; init; } = Daily1020;
        /// <summary>รอบเต็มรับคิวสำรอง</summary>
        public bool Waitlist { get; init; }
        /// <summary>การจัดสรรผู้ให้บริการ — ยังไม่มีผู้ให้บริการผูก = นับความจุของบริการ (เริ่มใช้ได้ทันที)</summary>
        /// <summary>ฝ่ายค้านรอบ 215 เฟส 2 S2: null = ตามค่าเว็บ (ค่าเริ่มต้นของตัวอย่าง) · ตั้งเฉพาะตัวอย่างที่ต้องการโหมดเฉพาะจริง</summary>
        public BookingResourceMode? ResourceMode { get; init; }
    }

    public sealed record Preset(string Key, string Label, string Description, IReadOnlyList<Sample> Samples);

    private static readonly Hours[] Daily1020 = { new("*", "10:00", "20:00") };

    // ─────────────── แม่แบบตามประเภทธุรกิจ (ตัวตั้งเดียว) ───────────────

    private static readonly Sample ThaiMassage = new("นวดไทย 60 นาที", "Thai massage 60", 60, 450, "นวด", "นวดแผนไทยทั้งตัว คลายกล้ามเนื้อ", BookingType.Appointment, 2)
    { Interval = 30, BufferAfter = 15, CapacityGroup = "หมอนวด", FreeCancellationHours = 6, ResourceMode = BookingResourceMode.CustomerChoice, OpeningHours = new[] { new Hours("*", "10:00", "22:00") } };
    private static readonly Sample AromaMassage = new("นวดน้ำมันอโรมา 90 นาที", "Aroma oil massage 90", 90, 900, "นวด", "นวดน้ำมันหอมระเหย ผ่อนคลาย", BookingType.Appointment, 2)
    { Interval = 30, BufferAfter = 15, CapacityGroup = "หมอนวด", FreeCancellationHours = 6, OpeningHours = new[] { new Hours("*", "10:00", "22:00") } };
    private static readonly Sample GelNail = new("ทำเล็บเจล", "Gel manicure", 75, 690, "เล็บ", "ทาเจล + ตกแต่งเล็บมือ", BookingType.Appointment, 1)
    { Interval = 15, BufferAfter = 10, FreeCancellationHours = 3, ResourceMode = BookingResourceMode.Auto, OpeningHours = new[] { new Hours("0,2-6", "10:00", "20:00") } };
    private static readonly Sample HairCut = new("ตัดผม + สระไดร์", "Haircut and blow dry", 45, 350, "ผม", "ตัดผมพร้อมสระไดร์", BookingType.Appointment, 2)
    { Interval = 15, BufferAfter = 10, OpeningHours = new[] { new Hours("0,2-6", "10:00", "20:00") } };

    private static readonly Hours[] ClinicHours = { new("1-6", "09:00", "18:00"), new("1-6", "12:00", "13:00", Break: true) };
    private static readonly Sample CheckUp = new("ตรวจสุขภาพทั่วไป", "General check-up", 30, 500, "ตรวจ", "พบแพทย์ทั่วไป · ตรวจร่างกายเบื้องต้น", BookingType.Appointment, 1)
    { MinAdvanceHours = 2, OpeningHours = ClinicHours };
    private static readonly Sample Specialist = new("ปรึกษาแพทย์เฉพาะทาง", "Specialist consultation", 30, 1200, "ปรึกษา", "นัดพบแพทย์เฉพาะทาง (กรุณาระบุอาการ)", BookingType.Appointment, 1)
    { BufferAfter = 10, MinAdvanceHours = 24, MaxAdvanceDays = 60, OpeningHours = new[] { new Hours("1-5", "13:00", "17:00") } };
    private static readonly Sample FluVaccine = new("ฉีดวัคซีนไข้หวัดใหญ่", "Flu vaccine", 15, 890, "วัคซีน", "ฉีดวัคซีน + สังเกตอาการ 15 นาที", BookingType.Appointment, 2)
    { MinAdvanceHours = 2, OpeningHours = ClinicHours };

    private static readonly Hours[] RestaurantHours = { new("*", "11:00", "14:00"), new("*", "17:00", "22:00") };
    private static readonly Sample TableSmall = new("จองโต๊ะ 2–4 ที่นั่ง", "Table for 2-4", 90, 0, "โต๊ะ", "จองโต๊ะล่วงหน้า ไม่มีค่าใช้จ่าย", BookingType.Lead, 4)
    { Interval = 30, OpeningHours = RestaurantHours };
    private static readonly Sample TableGroup = new("จองโต๊ะกลุ่ม 5–10 ที่นั่ง", "Table for 5-10", 120, 0, "โต๊ะ", "โต๊ะกลุ่ม/ห้องส่วนตัว — ร้านโทรยืนยันภายใน 30 นาที", BookingType.Lead, 10)
    { Interval = 30, Unit = BookingCapacityUnit.Guests, MinAdvanceHours = 3, OpeningHours = RestaurantHours };
    private static readonly Sample CafeTable = new("จองโต๊ะ / มุมทำงาน", "Table booking", 120, 0, "โต๊ะ", "จองที่นั่งล่วงหน้า", BookingType.Lead, 4)
    { Interval = 60, OpeningHours = new[] { new Hours("*", "08:00", "18:00") } };

    private static readonly Sample PropertyViewing = new("นัดดูทรัพย์", "Property viewing", 60, 0, "นัดหมาย", "นัดชมบ้าน/คอนโดกับเจ้าหน้าที่ (ระบุรหัสทรัพย์ในหมายเหตุ)", BookingType.Lead, 1)
    { Interval = 60, BufferAfter = 30, MinAdvanceHours = 12, OpeningHours = new[] { new Hours("*", "09:00", "17:00") } };

    private static readonly Hours[] OfficeHours = { new("1-5", "09:00", "17:00"), new("1-5", "12:00", "13:00", Break: true) };
    private static readonly Sample FreeConsult = new("ปรึกษาเบื้องต้น 30 นาที (ฟรี)", "Free consultation 30", 30, 0, "ปรึกษา", "คุยโจทย์เบื้องต้นก่อนเสนอราคา", BookingType.Lead, 1)
    { BufferAfter = 15, CapacityGroup = "ที่ปรึกษา", MinAdvanceHours = 24, MaxAdvanceDays = 60, OpeningHours = OfficeHours };
    private static readonly Sample PlanningSession = new("ประชุมวางแผนงาน 60 นาที", "Planning session 60", 60, 1500, "ปรึกษา", "เวิร์กชอปวางแผนกับทีม", BookingType.Appointment, 1)
    { Interval = 30, BufferAfter = 15, CapacityGroup = "ที่ปรึกษา", MinAdvanceHours = 24, MaxAdvanceDays = 60, FreeCancellationHours = 24, CancellationFeePercent = 50, OpeningHours = OfficeHours };

    private static readonly Sample TrialClass = new("ทดลองเรียนฟรี", "Free trial class", 60, 0, "ทดลองเรียน", "นัดทดลองเรียน 1 ครั้ง", BookingType.Lead, 6)
    { Interval = 60, Unit = BookingCapacityUnit.Guests, OpeningHours = new[] { new Hours("1-5", "16:00", "19:00"), new Hours("0,6", "09:00", "17:00") } };

    private static readonly Sample YogaClass = new("คลาสโยคะ 60 นาที", "Yoga class 60", 60, 300, "คลาส", "คลาสกลุ่ม — จองที่นั่งต่อคน", BookingType.Appointment, 15)
    { Mode = BookingSlotMode.Windows, Unit = BookingCapacityUnit.Guests, FreeCancellationHours = 12, Waitlist = true,
      OpeningHours = new[] { new Hours("1-5", "07:00", "08:00"), new Hours("1-5", "18:00", "19:00"), new Hours("6", "09:00", "10:00") } };
    private static readonly Sample PersonalTraining = new("เทรนเนอร์ส่วนตัว 60 นาที", "Personal training 60", 60, 800, "เทรนเนอร์", "เทรนตัวต่อตัวกับเทรนเนอร์", BookingType.Appointment, 2)
    { Interval = 60, BufferAfter = 0, CapacityGroup = "เทรนเนอร์", FreeCancellationHours = 12, ResourceMode = BookingResourceMode.CustomerChoice, OpeningHours = new[] { new Hours("*", "06:00", "21:00") } };

    private static readonly Sample BadmintonCourt = new("สนามแบดมินตัน (ต่อชั่วโมง)", "Badminton court hourly", 60, 200, "สนาม", "ราคาต่อสนามต่อชั่วโมง · จองติดกันได้สูงสุด 3 ชั่วโมง", BookingType.Appointment, 4)
    { Interval = 60, MaxUnits = 3, MinAdvanceHours = 0, MaxAdvanceDays = 14, OpeningHours = new[] { new Hours("*", "08:00", "23:00") } };
    private static readonly Sample FootballCourt = new("สนามฟุตบอล 7 คน (ต่อชั่วโมง)", "Football 7-a-side hourly", 60, 1200, "สนาม", "หญ้าเทียม · จองติดกันได้สูงสุด 3 ชั่วโมง", BookingType.Appointment, 1)
    { Interval = 60, MaxUnits = 3, BufferAfter = 0, MinAdvanceHours = 2, MaxAdvanceDays = 30, DepositPercent = 50, OpeningHours = new[] { new Hours("*", "09:00", "24:00") } };

    private static readonly Hours[] CoworkHours = { new("1-5", "08:00", "18:00") };
    private static readonly Sample MeetingHourly = new("ห้องประชุม A (ทุก 30 นาที)", "Meeting room A 30min", 30, 250, "ห้องประชุม", "ราคาต่อ 30 นาที · จองติดกันได้สูงสุด 8 ช่วง (4 ชั่วโมง)", BookingType.Appointment, 1)
    { Interval = 30, MaxUnits = 8, BufferAfter = 0, CapacityGroup = "ห้องประชุม A", MinAdvanceHours = 1, OpeningHours = CoworkHours };
    private static readonly Sample MeetingHalfDay = new("ห้องประชุม A (ครึ่งวัน)", "Meeting room A half day", 240, 1500, "ห้องประชุม", "ช่วงเช้า 08:00–13:00 หรือช่วงบ่าย 13:00–18:00", BookingType.Appointment, 1)
    { Mode = BookingSlotMode.HalfDay, HalfDaySplit = "13:00", CapacityGroup = "ห้องประชุม A", MinAdvanceHours = 12, OpeningHours = CoworkHours };
    private static readonly Sample MeetingFullDay = new("ห้องประชุม A (เต็มวัน)", "Meeting room A full day", 600, 2500, "ห้องประชุม", "ทั้งวัน 08:00–18:00", BookingType.Appointment, 1)
    { Mode = BookingSlotMode.FullDay, CapacityGroup = "ห้องประชุม A", MinAdvanceHours = 12, OpeningHours = CoworkHours };
    private static readonly Sample HotDesk = new("Hot desk รายวัน", "Hot desk day pass", 600, 300, "โต๊ะทำงาน", "โต๊ะทำงานร่วม 1 ที่ ทั้งวัน", BookingType.Appointment, 20)
    { Mode = BookingSlotMode.FullDay, MinAdvanceHours = 0, OpeningHours = CoworkHours };

    private static readonly Sample CarRental = new("รถเก๋งเช่า (รายวัน)", "Sedan rental daily", 720, 1200, "รถเช่า", "รับรถ 08:00 คืน 20:00 · มัดจำ 30%", BookingType.PrePayment, 3)
    { Mode = BookingSlotMode.FullDay, MinAdvanceHours = 24, MaxAdvanceDays = 90, DepositPercent = 30, FreeCancellationHours = 48, CancellationFeePercent = 30,
      OpeningHours = new[] { new Hours("*", "08:00", "20:00") } };
    private static readonly Sample CameraHalfDay = new("เช่ากล้อง + เลนส์ (ครึ่งวัน)", "Camera rental half day", 240, 600, "อุปกรณ์", "ช่วงเช้า 09:00–13:00 หรือช่วงบ่าย 13:00–18:00", BookingType.Appointment, 2)
    { Mode = BookingSlotMode.HalfDay, HalfDaySplit = "13:00", CapacityGroup = "กล้อง", MinAdvanceHours = 12, OpeningHours = new[] { new Hours("*", "09:00", "18:00") } };
    private static readonly Sample CameraFullDay = new("เช่ากล้อง + เลนส์ (รายวัน)", "Camera rental full day", 540, 1000, "อุปกรณ์", "ทั้งวัน 09:00–18:00", BookingType.Appointment, 2)
    { Mode = BookingSlotMode.FullDay, CapacityGroup = "กล้อง", MinAdvanceHours = 12, OpeningHours = new[] { new Hours("*", "09:00", "18:00") } };

    /// <summary>แม่แบบทั้งหมด (ลำดับ = ลำดับในปุ่ม "เพิ่มจากแม่แบบ")</summary>
    public static IReadOnlyList<Preset> Presets { get; } = new[]
    {
        new Preset("spa", "สปา / นวด", "รอบทุก 30 นาที · เผื่อเก็บห้อง 15 นาที · หมอนวด 2 คนใช้ร่วมกันทุกคอร์ส", new[] { ThaiMassage, AromaMassage }),
        new Preset("beauty", "ทำเล็บ / ร้านเสริมสวย", "รอบทุก 15 นาที · เผื่อเก็บ 10 นาที · ปิดวันจันทร์", new[] { GelNail, HairCut }),
        new Preset("clinic", "คลินิก", "จันทร์–เสาร์ · พักเที่ยง 12:00–13:00 · จองล่วงหน้าอย่างน้อย 2 ชั่วโมง", new[] { CheckUp, Specialist, FluVaccine }),
        new Preset("professional", "บริการมืออาชีพ / ที่ปรึกษา", "วันทำการ 09:00–17:00 · เผื่อเวลาสรุปงาน 15 นาที · จองล่วงหน้า 1 วัน", new[] { FreeConsult, PlanningSession }),
        new Preset("fitness", "ฟิตเนส / คลาสออกกำลังกาย", "คลาสกลุ่มรอบตายตัวนับที่นั่งต่อคน + เทรนเนอร์ส่วนตัวรายชั่วโมง", new[] { YogaClass, PersonalTraining }),
        new Preset("court", "สนามกีฬา (รายชั่วโมง)", "รอบละ 1 ชั่วโมง · จองติดกันได้หลายชั่วโมง · ความจุ = จำนวนสนาม", new[] { BadmintonCourt, FootballCourt }),
        new Preset("meeting", "ห้องประชุม / Coworking", "ห้องเดียวกันขายได้ทั้งราย 30 นาที · ครึ่งวัน · เต็มวัน (ใช้ทรัพยากรร่วม ไม่จองซ้อนกัน)", new[] { MeetingHourly, MeetingHalfDay, MeetingFullDay, HotDesk }),
        new Preset("rental", "รถเช่า / อุปกรณ์ให้เช่า", "รายวัน / ครึ่งวัน (เช้า-บ่าย) · ความจุ = จำนวนคัน/ชุด · มัดจำ", new[] { CarRental, CameraHalfDay, CameraFullDay }),
        new Preset("restaurant", "ร้านอาหาร (จองโต๊ะ)", "รอบทุก 30 นาที · มื้อกลางวัน/เย็น · โต๊ะกลุ่มนับจำนวนคน", new[] { TableSmall, TableGroup }),
        new Preset("cafe", "คาเฟ่", "จองที่นั่งรายชั่วโมง", new[] { CafeTable }),
        new Preset("education", "โรงเรียน / สถาบันสอน", "ทดลองเรียน นับที่นั่งต่อคน", new[] { TrialClass }),
        new Preset("property", "อสังหา (นัดดูทรัพย์)", "รอบละ 1 ชั่วโมง · เผื่อเดินทาง 30 นาที", new[] { PropertyViewing }),
    };

    private static IReadOnlyList<Sample> Of(params string[] keys)
        => keys.SelectMany(k => Presets.First(p => p.Key == k).Samples).ToList();

    /// <summary>ตัวอย่างต่ออุตสาหกรรม — ว่าง = อุตสาหกรรมนี้ไม่ใช้จองคิวแบบ slot (ที่พักใช้ Lodging แทน)</summary>
    public static IReadOnlyList<Sample> SamplesFor(IndustryType industry) => industry switch
    {
        IndustryType.Beauty => new[] { ThaiMassage, AromaMassage, GelNail },
        IndustryType.Healthcare => Of("clinic"),
        IndustryType.Restaurant => Of("restaurant"),
        IndustryType.Cafe => Of("cafe"),
        IndustryType.RealEstate => Of("property"),
        IndustryType.Service or IndustryType.Freelance => Of("professional"),
        IndustryType.Education => Of("education"),
        IndustryType.Transportation => new[] { CarRental },
        _ => Array.Empty<Sample>(),
    };

    // ─────────────── แปลงเป็นคำขอ/ตารางเวลา ───────────────

    /// <summary>"1-5" · "0,6" · "*" → วันในสัปดาห์</summary>
    public static IReadOnlyList<DayOfWeek> ParseDays(string spec)
    {
        if (spec.Trim() == "*") return Enum.GetValues<DayOfWeek>();
        var days = new SortedSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ab = part.Split('-', StringSplitOptions.TrimEntries);
            var a = int.Parse(ab[0], System.Globalization.CultureInfo.InvariantCulture);
            var b = ab.Length > 1 ? int.Parse(ab[1], System.Globalization.CultureInfo.InvariantCulture) : a;
            for (var d = a; d <= b; d++) if (d is >= 0 and <= 6) days.Add(d);
        }
        return days.Select(d => (DayOfWeek)d).ToList();
    }

    public static BookingScheduleDto ScheduleOf(Sample s) => new()
    {
        Weekly = s.OpeningHours
            .SelectMany(h => ParseDays(h.Days).Select(d => new BookingWeeklyHoursDto { DayOfWeek = d, Start = h.Start, End = h.End, IsBlocked = h.Break }))
            .OrderBy(w => w.DayOfWeek).ThenBy(w => w.IsBlocked).ThenBy(w => w.Start, StringComparer.Ordinal).ToList(),
    };

    /// <summary>ตัวอย่าง → คำขอสร้างบริการ (ฟอร์ม "เพิ่มจากแม่แบบ" · seed ใช้ทางเดียวกัน)</summary>
    public static CreateBookingServiceRequest ToRequest(Sample s) => new()
    {
        Name = s.Name, NameEn = s.NameEn, Description = s.Description, Slug = Slugify(s.NameEn),
        DurationMinutes = s.Minutes, BufferMinutes = s.BufferAfter, BufferBeforeMinutes = s.BufferBefore,
        MaxCapacity = s.Capacity, CapacityUnit = s.Unit, MaxUnitsPerBooking = s.MaxUnits,
        MaxAdvanceBookingDays = s.MaxAdvanceDays, MinAdvanceBookingHours = s.MinAdvanceHours,
        Price = s.Price, DepositPercent = s.DepositPercent, BookingType = s.Type, Category = s.Category,
        AutoConfirm = false, FreeCancellationHours = s.FreeCancellationHours, CancellationFeePercent = s.CancellationFeePercent,
        SlotMode = s.Mode, SlotIntervalMinutes = s.Interval, HalfDaySplitTime = s.HalfDaySplit, CapacityGroup = s.CapacityGroup,
        Schedule = ScheduleOf(s),
        WaitlistEnabled = s.Waitlist, ResourceMode = s.ResourceMode, EnforceSlotCapacity = true,
    };

    public static IReadOnlyList<BookingServicePresetResponse> PresetResponses()
        => Presets.Select(p => new BookingServicePresetResponse
        {
            Key = p.Key, Label = p.Label, Description = p.Description,
            Services = p.Samples.Select(ToRequest).ToList(),
        }).ToList();

    /// <summary>คืนจำนวนบริการที่เพิ่ม (0 = มีอยู่แล้ว/ไม่มีตัวอย่างสำหรับประเภทนี้) — ผู้เรียก SaveChanges เอง</summary>
    public static async Task<int> SeedForSiteAsync(AccountingDbContext db, Guid companyId, Site site, IndustryType industry, string userId)
    {
        var samples = SamplesFor(industry);
        if (samples.Count == 0) return 0;
        if (await db.SiteBookingServices.AnyAsync(b => b.CompanyId == companyId && b.SiteId == site.Id)) return 0;
        var sort = 0;
        foreach (var s in samples)
        {
            var req = ToRequest(s);
            var svc = new SiteBookingService
            {
                CompanyId = companyId, SiteId = site.Id,
                Currency = site.DefaultCurrency, IsActive = true, SortOrder = sort++, CreatedBy = userId,
            };
            CmsBookingService.ApplySettings(svc, req);
            foreach (var row in CmsBookingService.BuildSlotRows(companyId, svc, req.Schedule!, userId))
                svc.Slots.Add(row);
            db.SiteBookingServices.Add(svc);
        }
        return samples.Count;
    }

    public static string Slugify(string nameEn)
    {
        var chars = nameEn.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
