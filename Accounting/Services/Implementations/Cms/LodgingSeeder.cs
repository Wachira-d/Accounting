using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Cms;

/// <summary>seed ที่พักเริ่มต้นเมื่อสร้างเว็บไซต์ประเภท "โรงแรม/ที่พัก" (IndustryType.Hotel)
/// เป้าหมาย: สร้าง portal แล้ว **จองได้ทันที** — มีประเภทห้อง+หมายเลขห้อง+แผนราคา+ฤดูกาล+
/// นโยบายยกเลิก+บริการเสริม+สินค้า ERP สำหรับลงรายได้ ครบโดยไม่ต้องตั้งค่าก่อน
/// (ผู้ใช้ปรับทีหลังในหน้าตั้งค่าที่พักได้ทุกค่า). ตัวเลข default ตรงกับหน้าเว็บ
/// ที่ CmsSiteTemplateSeeder.HotelPlan พิมพ์ไว้ (Standard 1,500 / Deluxe 2,500 / Suite 4,500)
/// เพื่อไม่ให้ราคาบนหน้าแรกกับราคาที่จองได้จริงเล่าคนละเรื่อง
///
/// idempotent: ถ้าเว็บไซต์นี้มีที่พักผูกอยู่แล้วจะไม่สร้างซ้ำ</summary>
public static class LodgingSeeder
{
    public static async Task<LodgingProperty?> SeedForSiteAsync(AccountingDbContext db, Guid companyId, Site site, string userId)
    {
        if (await db.LodgingProperties.AnyAsync(p => p.CompanyId == companyId && p.SiteId == site.Id)) return null;

        var company = await db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.Address, c.Phone, c.Email }).FirstOrDefaultAsync();

        var code = DeriveCode(site.Subdomain);
        var existingCodes = await db.LodgingProperties.Where(p => p.CompanyId == companyId).Select(p => p.Code).ToListAsync();
        var n = 1; var baseCode = code;
        while (existingCodes.Contains(code)) code = $"{baseCode}{++n}";

        var prop = new LodgingProperty
        {
            CompanyId = companyId, SiteId = site.Id, BranchId = site.BranchId,
            Name = site.Name, NameEn = site.NameEn, Code = code, PropertyType = LodgingPropertyType.Hotel,
            Description = "ห้องพักสะอาด · บรรยากาศดี · จองตรงรับส่วนลด",
            Address = company?.Address, Phone = company?.Phone, Email = company?.Email,
            AmenitiesJson = "[\"wifi\",\"parking\",\"breakfast\",\"pool\",\"aircon\"]",
            CheckInTime = new TimeOnly(14, 0), CheckOutTime = new TimeOnly(12, 0),
            MinNights = 1, MaxNights = 30, MaxAdvanceDays = 365,
            AutoConfirmOnDeposit = true, PaymentHoldMinutes = 60 * 24,
            DepositPercent = 50, PricesIncludeVat = true, ServiceChargePercent = 0,
            WeekendMultiplier = 1.00m, WeekendDaysMask = 32 | 64, ExtraGuestPrice = 500,
            NoShowChargePercent = 100,
            ConfirmationMessage = "ขอบคุณที่จองกับเราโดยตรง — แสดงเลขที่จองนี้ที่แผนกต้อนรับในวันเข้าพัก",
            HouseRules = "เช็คอิน 14:00 · เช็คเอาต์ 12:00\nกรุณาแสดงบัตรประชาชน/พาสปอร์ตตอนเช็คอิน\nงดสูบบุหรี่ในห้องพัก\nงดส่งเสียงดังหลัง 22:00",
            NotifyOwnerOnBooking = true, NotifyEmails = company?.Email,
            HousekeepingMinutesPerRoom = 30, AutoCreateHousekeepingTaskOnCheckout = true,
            IsActive = true, CreatedBy = userId,
        };
        db.LodgingProperties.Add(prop);

        // นโยบายยกเลิก — ขั้นบันไดมาตรฐาน + non-refundable
        var flexible = new LodgingCancellationPolicy
        {
            CompanyId = companyId, Property = prop, Name = "ยืดหยุ่น (ฟรีก่อน 7 วัน)",
            Description = "ยกเลิกฟรีก่อนเข้าพัก 7 วัน · 3–6 วัน คิด 50% · น้อยกว่า 3 วัน คิด 100%",
            RulesJson = "[{\"daysBefore\":7,\"penaltyPercent\":0},{\"daysBefore\":3,\"penaltyPercent\":50},{\"daysBefore\":0,\"penaltyPercent\":100}]",
            IsDefault = true, CreatedBy = userId,
        };
        var strict = new LodgingCancellationPolicy
        {
            CompanyId = companyId, Property = prop, Name = "ไม่คืนเงิน (Non-refundable)",
            Description = "ราคาพิเศษ — ยกเลิก/เลื่อนไม่ได้ ไม่คืนเงินทุกกรณี", RulesJson = "[]", NonRefundable = true, CreatedBy = userId,
        };
        db.LodgingCancellationPolicies.AddRange(flexible, strict);
        prop.DefaultCancellationPolicy = flexible;

        // สินค้า/บริการ ERP สำหรับลงรายได้ (ProductType.Service · ราคารวม VAT)
        var products = new Dictionary<string, Product>();
        Product P(string codeSuffix, string name, string nameEn, decimal price, string unit)
        {
            var p = new Product
            {
                CompanyId = companyId, Code = $"{code}-{codeSuffix}", Name = name, NameEn = nameEn, ProductType = ProductType.Service,
                Category = "ที่พัก", Unit = unit, SellingPrice = price, VatRate = 7, IsVatIncluded = true, TrackStock = false, IsActive = true, CreatedBy = userId,
            };
            products[codeSuffix] = p;
            return p;
        }
        // seed ซ้ำ (ที่พักเดิมถูกลบแล้วสร้างเว็บใหม่ด้วย subdomain เดิม) → ใช้สินค้าเดิม ไม่สร้างรหัสซ้ำ
        var existingProducts = await db.Products.Where(x => x.CompanyId == companyId && x.Code.StartsWith(code + "-")).ToDictionaryAsync(x => x.Code, x => x);
        void AddProduct(Product p)
        {
            if (existingProducts.TryGetValue(p.Code, out var existing)) { products[p.Code[(code.Length + 1)..]] = existing; return; }
            db.Products.Add(p);
        }
        AddProduct(P("STD", "ค่าห้องพัก Standard", "Standard Room", 1500, "คืน"));
        AddProduct(P("DLX", "ค่าห้องพัก Deluxe", "Deluxe Room", 2500, "คืน"));
        AddProduct(P("SUI", "ค่าห้องพัก Suite", "Suite", 4500, "คืน"));
        AddProduct(P("BRK", "อาหารเช้า", "Breakfast", 350, "ท่าน"));
        AddProduct(P("XBD", "เตียงเสริม", "Extra bed", 800, "คืน"));
        AddProduct(P("TRF", "รถรับส่งสนามบิน", "Airport transfer", 800, "เที่ยว"));
        AddProduct(P("LCO", "Late check-out", "Late check-out", 500, "ครั้ง"));

        // ประเภทห้อง + หมายเลขห้อง (ตัวเลขตรงกับหน้าเว็บที่ seed ไว้)
        LodgingRoomType RT(string name, string nameEn, string codeSuffix, decimal rate, int stdOcc, int maxAdults, int maxChildren, int maxOcc, string bed, decimal sqm, bool extraBed, int sort, string desc)
            => new()
            {
                CompanyId = companyId, Property = prop, Name = name, NameEn = nameEn, Code = codeSuffix, Slug = nameEn.ToLowerInvariant().Replace(' ', '-'),
                Description = desc, BedType = bed, SizeSqm = sqm, StandardOccupancy = stdOcc, MaxAdults = maxAdults, MaxChildren = maxChildren, MaxOccupancy = maxOcc,
                AllowExtraBed = extraBed, MaxExtraBeds = extraBed ? 1 : 0, ExtraBedPrice = extraBed ? 800 : null,
                PricingMode = LodgingPricingMode.PerUnit, BaseRate = rate, Product = products[codeSuffix],
                AmenitiesJson = "[\"wifi\",\"aircon\",\"tv\",\"minibar\",\"hot-shower\"]", IsActive = true, SortOrder = sort, CreatedBy = userId,
            };
        var std = RT("Standard Room", "Standard Room", "STD", 1500, 2, 2, 1, 3, "เตียง 6 ฟุต", 25, false, 1, "25 ตรม. · เตียง 6 ฟุต · ห้องน้ำในตัว · WiFi · TV · มินิบาร์");
        var dlx = RT("Deluxe Room", "Deluxe Room", "DLX", 2500, 2, 2, 2, 4, "King size", 32, true, 2, "32 ตรม. · King size · วิวเมือง · เครื่องชงกาแฟ · อ่างอาบน้ำ");
        var sui = RT("Suite", "Suite", "SUI", 4500, 2, 3, 2, 5, "King size + โซฟาเบด", 65, true, 3, "65 ตรม. · ห้องนั่งเล่นแยก · มินิบาร์ฟรี · Late checkout 16:00");
        db.LodgingRoomTypes.AddRange(std, dlx, sui);
        void Units(LodgingRoomType rt, string floor, params string[] numbers)
        {
            var i = 0;
            foreach (var num in numbers)
                db.LodgingUnits.Add(new LodgingUnit { CompanyId = companyId, RoomType = rt, Number = num, Floor = floor, SortOrder = i++, CreatedBy = userId });
        }
        Units(std, "1", "101", "102", "103", "104", "105");
        Units(dlx, "2", "201", "202", "203", "204");
        Units(sui, "3", "301", "302");

        // แผนราคา (TakeTime Voucher/Affiliate → rate plan มาตรฐาน)
        db.LodgingRatePlans.AddRange(
            new LodgingRatePlan { CompanyId = companyId, Property = prop, Name = "ราคามาตรฐาน", NameEn = "Standard rate", Code = "STD", AdjustMode = LodgingRateAdjustMode.Base, IsRefundable = true, CancellationPolicy = flexible, IsDefault = true, SortOrder = 1, CreatedBy = userId },
            new LodgingRatePlan { CompanyId = companyId, Property = prop, Name = "ไม่คืนเงิน ลด 10%", NameEn = "Non-refundable -10%", Code = "NRF", AdjustMode = LodgingRateAdjustMode.Multiplier, AdjustValue = 0.90m, IsRefundable = false, CancellationPolicy = strict, DepositPercent = 100, SortOrder = 2, CreatedBy = userId },
            new LodgingRatePlan { CompanyId = companyId, Property = prop, Name = "รวมอาหารเช้า 2 ท่าน", NameEn = "Breakfast included", Code = "BB", AdjustMode = LodgingRateAdjustMode.Delta, AdjustValue = 700, IncludesBreakfast = true, IsRefundable = true, CancellationPolicy = flexible, SortOrder = 3, CreatedBy = userId },
            new LodgingRatePlan { CompanyId = companyId, Property = prop, Name = "พักยาว 7 คืนขึ้นไป ลด 15%", NameEn = "Long stay -15%", Code = "LONG", AdjustMode = LodgingRateAdjustMode.Multiplier, AdjustValue = 0.85m, MinNights = 7, IsRefundable = true, CancellationPolicy = flexible, SortOrder = 4, CreatedBy = userId });

        // ฤดูกาล (TakeTime PricingSeasons seed: สงกรานต์ ×1.50 · ปีใหม่ ×1.60 · high ×1.25 · low ×0.85) — recurring ทุกปี
        var y = DateTime.UtcNow.Year;
        db.LodgingSeasons.AddRange(
            new LodgingSeason { CompanyId = companyId, Property = prop, Name = "High season", SeasonType = LodgingSeasonType.High, StartDate = new DateTime(y, 11, 1, 0, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(y, 2, 28, 0, 0, 0, DateTimeKind.Utc), Multiplier = 1.25m, IsRecurringYearly = true, CreatedBy = userId },
            new LodgingSeason { CompanyId = companyId, Property = prop, Name = "Low season", SeasonType = LodgingSeasonType.Low, StartDate = new DateTime(y, 5, 1, 0, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(y, 10, 31, 0, 0, 0, DateTimeKind.Utc), Multiplier = 0.85m, IsRecurringYearly = true, CreatedBy = userId },
            new LodgingSeason { CompanyId = companyId, Property = prop, Name = "สงกรานต์", SeasonType = LodgingSeasonType.Holiday, StartDate = new DateTime(y, 4, 12, 0, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(y, 4, 16, 0, 0, 0, DateTimeKind.Utc), Multiplier = 1.50m, IsRecurringYearly = true, MinNights = 2, CreatedBy = userId },
            new LodgingSeason { CompanyId = companyId, Property = prop, Name = "ปีใหม่", SeasonType = LodgingSeasonType.Peak, StartDate = new DateTime(y, 12, 28, 0, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(y, 1, 2, 0, 0, 0, DateTimeKind.Utc), Multiplier = 1.60m, IsRecurringYearly = true, MinNights = 2, CreatedBy = userId });

        // บริการเสริม
        db.LodgingExtras.AddRange(
            new LodgingExtra { CompanyId = companyId, Property = prop, Name = "อาหารเช้า", NameEn = "Breakfast", Category = LodgingExtraCategory.Breakfast, PriceMode = LodgingExtraPriceMode.PerPersonPerNight, Price = 350, Product = products["BRK"], SortOrder = 1, CreatedBy = userId },
            new LodgingExtra { CompanyId = companyId, Property = prop, Name = "เตียงเสริม", NameEn = "Extra bed", Category = LodgingExtraCategory.ExtraBed, PriceMode = LodgingExtraPriceMode.PerNight, Price = 800, MaxQuantity = 1, Product = products["XBD"], SortOrder = 2, CreatedBy = userId },
            new LodgingExtra { CompanyId = companyId, Property = prop, Name = "รถรับส่งสนามบิน (เที่ยวเดียว)", NameEn = "Airport transfer", Category = LodgingExtraCategory.Transfer, PriceMode = LodgingExtraPriceMode.PerStay, Price = 800, MaxQuantity = 2, Product = products["TRF"], SortOrder = 3, CreatedBy = userId },
            new LodgingExtra { CompanyId = companyId, Property = prop, Name = "Late check-out ถึง 16:00", NameEn = "Late check-out", Category = LodgingExtraCategory.Other, PriceMode = LodgingExtraPriceMode.PerStay, Price = 500, MaxQuantity = 1, Product = products["LCO"], SortOrder = 4, CreatedBy = userId });

        return prop;
    }

    private static string DeriveCode(string subdomain)
    {
        var letters = new string((subdomain ?? "").Where(char.IsLetterOrDigit).Take(4).ToArray()).ToUpperInvariant();
        return string.IsNullOrEmpty(letters) ? "STAY" : letters;
    }
}
