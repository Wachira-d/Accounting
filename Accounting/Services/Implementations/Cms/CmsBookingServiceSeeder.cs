using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Cms;

/// <summary>seed "บริการที่จองได้" ตัวอย่างให้เว็บที่มีหน้า /booking แบบ slot (BookingCalendar) —
/// สปา · คลินิก · ร้านอาหาร (จองโต๊ะ) · อสังหา (นัดดูทรัพย์) · เว็บชนิด Booking ทั่วไป.
/// ที่มา (รอบ 158, ทีมตรวจเทมเพลต D-08): หน้าจองของทุกเทมเพลตขึ้น "ยังไม่มีบริการให้จองในขณะนี้"
/// จนกว่าเจ้าของจะไปตั้งเองใน cms-edit ขณะที่เว็บที่พักได้ห้อง/ราคาครบตั้งแต่สร้าง = สองมาตรฐาน.
/// idempotent ต่อเว็บ: มีบริการอยู่แล้วแม้ตัวเดียว → ไม่แตะ (ของที่เจ้าของตั้งไว้ต้องชนะเสมอ)</summary>
public static class CmsBookingServiceSeeder
{
    public readonly record struct Sample(string Name, string NameEn, int Minutes, decimal Price, string Category, string Description, BookingType Type, int Capacity);

    /// <summary>ตัวอย่างต่ออุตสาหกรรม — ว่าง = อุตสาหกรรมนี้ไม่ใช้จองคิวแบบ slot (ที่พักใช้ Lodging แทน)</summary>
    public static IReadOnlyList<Sample> SamplesFor(IndustryType industry) => industry switch
    {
        IndustryType.Beauty => new Sample[]
        {
            new("นวดไทย 60 นาที", "Thai massage 60", 60, 450, "นวด", "นวดแผนไทยทั้งตัว คลายกล้ามเนื้อ", BookingType.Appointment, 2),
            new("นวดน้ำมันอโรมา 90 นาที", "Aroma oil massage 90", 90, 900, "นวด", "นวดน้ำมันหอมระเหย ผ่อนคลาย", BookingType.Appointment, 2),
            new("ทำเล็บเจล", "Gel manicure", 75, 690, "เล็บ", "ทาเจล + ตกแต่งเล็บมือ", BookingType.Appointment, 1),
        },
        IndustryType.Healthcare => new Sample[]
        {
            new("ตรวจสุขภาพทั่วไป", "General check-up", 30, 500, "ตรวจ", "พบแพทย์ทั่วไป · ตรวจร่างกายเบื้องต้น", BookingType.Appointment, 1),
            new("ปรึกษาแพทย์เฉพาะทาง", "Specialist consultation", 30, 1200, "ปรึกษา", "นัดพบแพทย์เฉพาะทาง (กรุณาระบุอาการ)", BookingType.Appointment, 1),
            new("ฉีดวัคซีนไข้หวัดใหญ่", "Flu vaccine", 15, 890, "วัคซีน", "ฉีดวัคซีน + สังเกตอาการ 15 นาที", BookingType.Appointment, 1),
        },
        IndustryType.Restaurant => new Sample[]
        {
            new("จองโต๊ะ 2–4 ที่นั่ง", "Table for 2-4", 90, 0, "โต๊ะ", "จองโต๊ะล่วงหน้า ไม่มีค่าใช้จ่าย", BookingType.Lead, 4),
            new("จองโต๊ะกลุ่ม 5–10 ที่นั่ง", "Table for 5-10", 120, 0, "โต๊ะ", "โต๊ะกลุ่ม/ห้องส่วนตัว — ร้านโทรยืนยันภายใน 30 นาที", BookingType.Lead, 10),
        },
        IndustryType.Cafe => new Sample[]
        {
            new("จองโต๊ะ / มุมทำงาน", "Table booking", 120, 0, "โต๊ะ", "จองที่นั่งล่วงหน้า", BookingType.Lead, 4),
        },
        IndustryType.RealEstate => new Sample[]
        {
            new("นัดดูทรัพย์", "Property viewing", 60, 0, "นัดหมาย", "นัดชมบ้าน/คอนโดกับเจ้าหน้าที่ (ระบุรหัสทรัพย์ในหมายเหตุ)", BookingType.Lead, 1),
        },
        IndustryType.Service or IndustryType.Freelance => new Sample[]
        {
            new("ปรึกษาเบื้องต้น 30 นาที (ฟรี)", "Free consultation 30", 30, 0, "ปรึกษา", "คุยโจทย์เบื้องต้นก่อนเสนอราคา", BookingType.Lead, 1),
            new("ประชุมวางแผนงาน 60 นาที", "Planning session 60", 60, 1500, "ปรึกษา", "เวิร์กชอปวางแผนกับทีม", BookingType.Appointment, 1),
        },
        IndustryType.Education => new Sample[]
        {
            new("ทดลองเรียนฟรี", "Free trial class", 60, 0, "ทดลองเรียน", "นัดทดลองเรียน 1 ครั้ง", BookingType.Lead, 6),
        },
        _ => Array.Empty<Sample>(),
    };

    /// <summary>คืนจำนวนบริการที่เพิ่ม (0 = มีอยู่แล้ว/ไม่มีตัวอย่างสำหรับประเภทนี้) — ผู้เรียก SaveChanges เอง</summary>
    public static async Task<int> SeedForSiteAsync(AccountingDbContext db, Guid companyId, Site site, IndustryType industry, string userId)
    {
        var samples = SamplesFor(industry);
        if (samples.Count == 0) return 0;
        if (await db.SiteBookingServices.AnyAsync(b => b.CompanyId == companyId && b.SiteId == site.Id)) return 0;
        var sort = 0;
        foreach (var s in samples)
        {
            db.SiteBookingServices.Add(new SiteBookingService
            {
                CompanyId = companyId, SiteId = site.Id,
                Name = s.Name, NameEn = s.NameEn, Description = s.Description,
                Slug = Slugify(s.NameEn),
                DurationMinutes = s.Minutes, MaxCapacity = s.Capacity,
                Price = s.Price, Currency = site.DefaultCurrency,
                BookingType = s.Type, Category = s.Category,
                IsActive = true, SortOrder = sort++, AutoConfirm = false,
                CreatedBy = userId,
            });
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
