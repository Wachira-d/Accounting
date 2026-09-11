using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Cms;

/// <summary>seed ที่พักเริ่มต้นเมื่อสร้างเว็บไซต์ประเภท "โรงแรม/ที่พัก" (IndustryType.Hotel)
/// เป้าหมาย: สร้าง portal แล้ว **จองได้ทันที** — มีประเภทห้อง+หมายเลขห้อง+แผนราคา+ฤดูกาล+
/// นโยบายยกเลิก+บริการเสริม+สินค้า ERP สำหรับลงรายได้ ครบโดยไม่ต้องตั้งค่าก่อน
/// (ผู้ใช้ปรับทีหลังในหน้าตั้งค่าที่พักได้ทุกค่า). ตัวเลขทุกตัวมาจาก <see cref="LodgingSeedDefaults"/>
/// ซึ่ง CmsSiteTemplateSeeder.HotelPlan ใช้พิมพ์หน้าเว็บด้วย — ห้ามพิมพ์ตัวเลขซ้ำที่นี่
/// (รอบ 158 พบว่าสองที่เคยถือคนละชุด: หน้าเว็บบอก 4 ประเภท/15:00/ยกเลิกฟรี 3 วัน แต่ที่จองได้จริงคือ 3/14:00/7 วัน)
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
            CheckInTime = new TimeOnly(LodgingSeedDefaults.CheckInHour, 0), CheckOutTime = new TimeOnly(LodgingSeedDefaults.CheckOutHour, 0),
            MinNights = 1, MaxNights = 30, MaxAdvanceDays = 365,
            AutoConfirmOnDeposit = true, PaymentHoldMinutes = 60 * 24,
            DepositPercent = LodgingSeedDefaults.DepositPercent, PricesIncludeVat = true, ServiceChargePercent = 0,
            WeekendMultiplier = 1.00m, WeekendDaysMask = 32 | 64, ExtraGuestPrice = LodgingSeedDefaults.ExtraGuestPrice,
            NoShowChargePercent = 100,
            ConfirmationMessage = "ขอบคุณที่จองกับเราโดยตรง — แสดงเลขที่จองนี้ที่แผนกต้อนรับในวันเข้าพัก",
            HouseRules = $"เช็คอิน {LodgingSeedDefaults.CheckIn} · เช็คเอาต์ {LodgingSeedDefaults.CheckOut}\nกรุณาแสดงบัตรประชาชน/พาสปอร์ตตอนเช็คอิน\nงดสูบบุหรี่ในห้องพัก\nงดส่งเสียงดังหลัง 22:00",
            NotifyOwnerOnBooking = true, NotifyEmails = company?.Email,
            HousekeepingMinutesPerRoom = 30, AutoCreateHousekeepingTaskOnCheckout = true,
            IsActive = true, CreatedBy = userId,
        };
        db.LodgingProperties.Add(prop);

        // นโยบายยกเลิก — ขั้นบันไดมาตรฐาน + non-refundable
        var flexible = new LodgingCancellationPolicy
        {
            CompanyId = companyId, Property = prop, Name = $"ยืดหยุ่น (ฟรีก่อน {LodgingSeedDefaults.FreeCancelDaysBefore} วัน)",
            Description = LodgingSeedDefaults.CancellationSummary,
            RulesJson = LodgingSeedDefaults.CancellationRulesJson,
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
        // สินค้า ERP ต่อประเภทห้อง — รหัส/ราคาชุดเดียวกับ RoomTypes (products[code] ถูกผูกเข้า RT() ข้างล่าง)
        foreach (var d in LodgingSeedDefaults.RoomTypes)
            AddProduct(P(d.Code, "ค่าห้องพัก " + d.Name.Replace(" Room", ""), d.Name, d.Rate, "คืน"));
        AddProduct(P("BRK", "อาหารเช้า", "Breakfast", LodgingSeedDefaults.BreakfastPerPersonPerNight, "ท่าน"));
        AddProduct(P("XBD", "เตียงเสริม", "Extra bed", LodgingSeedDefaults.ExtraBedPerNight, "คืน"));
        AddProduct(P("TRF", "รถรับส่งสนามบิน", "Airport transfer", LodgingSeedDefaults.AirportTransferPerStay, "เที่ยว"));
        AddProduct(P("LCO", "Late check-out", "Late check-out", LodgingSeedDefaults.LateCheckoutPerStay, "ครั้ง"));

        // ประเภทห้อง + หมายเลขห้อง (ตัวเลขตรงกับหน้าเว็บที่ seed ไว้)
        LodgingRoomType RT(string name, string nameEn, string codeSuffix, decimal rate, int stdOcc, int maxAdults, int maxChildren, int maxOcc, string bed, decimal sqm, bool extraBed, int sort, string desc)
            => new()
            {
                CompanyId = companyId, Property = prop, Name = name, NameEn = nameEn, Code = codeSuffix, Slug = nameEn.ToLowerInvariant().Replace(' ', '-'),
                Description = desc, BedType = bed, SizeSqm = sqm, StandardOccupancy = stdOcc, MaxAdults = maxAdults, MaxChildren = maxChildren, MaxOccupancy = maxOcc,
                AllowExtraBed = extraBed, MaxExtraBeds = extraBed ? 1 : 0, ExtraBedPrice = extraBed ? LodgingSeedDefaults.ExtraBedPerNight : null,
                PricingMode = LodgingPricingMode.PerUnit, BaseRate = rate, Product = products[codeSuffix],
                AmenitiesJson = "[\"wifi\",\"aircon\",\"tv\",\"minibar\",\"hot-shower\"]", IsActive = true, SortOrder = sort, CreatedBy = userId,
            };
        // ประเภทห้อง + หมายเลขห้อง — ทุกค่าจาก LodgingSeedDefaults.RoomTypes (ชุดเดียวกับที่ HotelPlan พิมพ์บนหน้าเว็บ)
        var roomTypes = new List<LodgingRoomType>();
        var rtSort = 1;
        foreach (var d in LodgingSeedDefaults.RoomTypes)
        {
            var rt = RT(d.Name, d.Name, d.Code, d.Rate, d.StandardOccupancy, d.MaxAdults, d.MaxChildren, d.MaxOccupancy, d.Bed, d.SizeSqm, d.ExtraBed, rtSort++, d.Description);
            roomTypes.Add(rt);
            Units(rt, d.Floor, d.Units);
        }
        db.LodgingRoomTypes.AddRange(roomTypes);
        void Units(LodgingRoomType rt, string floor, params string[] numbers)
        {
            var i = 0;
            foreach (var num in numbers)
                db.LodgingUnits.Add(new LodgingUnit { CompanyId = companyId, RoomType = rt, Number = num, Floor = floor, SortOrder = i++, CreatedBy = userId });
        }

        // แผนราคา (TakeTime Voucher/Affiliate → rate plan มาตรฐาน)
        db.LodgingRatePlans.AddRange(
            new LodgingRatePlan { CompanyId = companyId, Property = prop, Name = "ราคามาตรฐาน", NameEn = "Standard rate", Code = "STD", AdjustMode = LodgingRateAdjustMode.Base, IsRefundable = true, CancellationPolicy = flexible, IsDefault = true, SortOrder = 1, CreatedBy = userId },
            new LodgingRatePlan { CompanyId = companyId, Property = prop, Name = $"ไม่คืนเงิน ลด {LodgingSeedDefaults.NonRefundableDiscountPercent}%", NameEn = $"Non-refundable -{LodgingSeedDefaults.NonRefundableDiscountPercent}%", Code = "NRF", AdjustMode = LodgingRateAdjustMode.Multiplier, AdjustValue = 1m - LodgingSeedDefaults.NonRefundableDiscountPercent / 100m, IsRefundable = false, CancellationPolicy = strict, DepositPercent = 100, SortOrder = 2, CreatedBy = userId },
            new LodgingRatePlan { CompanyId = companyId, Property = prop, Name = "รวมอาหารเช้า 2 ท่าน", NameEn = "Breakfast included", Code = "BB", AdjustMode = LodgingRateAdjustMode.Delta, AdjustValue = LodgingSeedDefaults.BreakfastPlanDelta, IncludesBreakfast = true, IsRefundable = true, CancellationPolicy = flexible, SortOrder = 3, CreatedBy = userId },
            new LodgingRatePlan { CompanyId = companyId, Property = prop, Name = $"พักยาว {LodgingSeedDefaults.LongStayMinNights} คืนขึ้นไป ลด {LodgingSeedDefaults.LongStayDiscountPercent}%", NameEn = $"Long stay -{LodgingSeedDefaults.LongStayDiscountPercent}%", Code = "LONG", AdjustMode = LodgingRateAdjustMode.Multiplier, AdjustValue = 1m - LodgingSeedDefaults.LongStayDiscountPercent / 100m, MinNights = LodgingSeedDefaults.LongStayMinNights, IsRefundable = true, CancellationPolicy = flexible, SortOrder = 4, CreatedBy = userId });

        // ฤดูกาล (TakeTime PricingSeasons seed: สงกรานต์ ×1.50 · ปีใหม่ ×1.60 · high ×1.25 · low ×0.85) — recurring ทุกปี
        var y = DateTime.UtcNow.Year;
        db.LodgingSeasons.AddRange(
            new LodgingSeason { CompanyId = companyId, Property = prop, Name = "High season", SeasonType = LodgingSeasonType.High, StartDate = new DateTime(y, 11, 1, 0, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(y, 2, 28, 0, 0, 0, DateTimeKind.Utc), Multiplier = 1.25m, IsRecurringYearly = true, CreatedBy = userId },
            new LodgingSeason { CompanyId = companyId, Property = prop, Name = "Low season", SeasonType = LodgingSeasonType.Low, StartDate = new DateTime(y, 5, 1, 0, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(y, 10, 31, 0, 0, 0, DateTimeKind.Utc), Multiplier = 0.85m, IsRecurringYearly = true, CreatedBy = userId },
            new LodgingSeason { CompanyId = companyId, Property = prop, Name = "สงกรานต์", SeasonType = LodgingSeasonType.Holiday, StartDate = new DateTime(y, 4, 12, 0, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(y, 4, 16, 0, 0, 0, DateTimeKind.Utc), Multiplier = 1.50m, IsRecurringYearly = true, MinNights = 2, CreatedBy = userId },
            new LodgingSeason { CompanyId = companyId, Property = prop, Name = "ปีใหม่", SeasonType = LodgingSeasonType.Peak, StartDate = new DateTime(y, 12, 28, 0, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(y, 1, 2, 0, 0, 0, DateTimeKind.Utc), Multiplier = 1.60m, IsRecurringYearly = true, MinNights = 2, CreatedBy = userId });

        // บริการเสริม
        db.LodgingExtras.AddRange(
            new LodgingExtra { CompanyId = companyId, Property = prop, Name = "อาหารเช้า", NameEn = "Breakfast", Category = LodgingExtraCategory.Breakfast, PriceMode = LodgingExtraPriceMode.PerPersonPerNight, Price = LodgingSeedDefaults.BreakfastPerPersonPerNight, Product = products["BRK"], SortOrder = 1, CreatedBy = userId },
            new LodgingExtra { CompanyId = companyId, Property = prop, Name = "เตียงเสริม", NameEn = "Extra bed", Category = LodgingExtraCategory.ExtraBed, PriceMode = LodgingExtraPriceMode.PerNight, Price = LodgingSeedDefaults.ExtraBedPerNight, MaxQuantity = 1, Product = products["XBD"], SortOrder = 2, CreatedBy = userId },
            new LodgingExtra { CompanyId = companyId, Property = prop, Name = "รถรับส่งสนามบิน (เที่ยวเดียว)", NameEn = "Airport transfer", Category = LodgingExtraCategory.Transfer, PriceMode = LodgingExtraPriceMode.PerStay, Price = LodgingSeedDefaults.AirportTransferPerStay, MaxQuantity = 2, Product = products["TRF"], SortOrder = 3, CreatedBy = userId },
            new LodgingExtra { CompanyId = companyId, Property = prop, Name = $"Late check-out ถึง {LodgingSeedDefaults.LateCheckoutUntil}", NameEn = "Late check-out", Category = LodgingExtraCategory.Other, PriceMode = LodgingExtraPriceMode.PerStay, Price = LodgingSeedDefaults.LateCheckoutPerStay, MaxQuantity = 1, Product = products["LCO"], SortOrder = 4, CreatedBy = userId });

        return prop;
    }

    private static string DeriveCode(string subdomain)
    {
        var letters = new string((subdomain ?? "").Where(char.IsLetterOrDigit).Take(4).ToArray()).ToUpperInvariant();
        return string.IsNullOrEmpty(letters) ? "STAY" : letters;
    }
}
