using Accounting.Data;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ตัวเลือกค่าจัดส่งหนึ่งอัตรา (โซน × อัตรา ที่ใช้กับที่อยู่/น้ำหนักนี้ได้)</summary>
public sealed record DeliveryRateOption(Guid RateId, string Name, string ZoneName, decimal Fee, bool IsFree, int? EtaDaysMin, int? EtaDaysMax);

/// <summary>ผลคิดค่าจัดส่ง — <see cref="Chosen"/> null + มีตัวเลือก = ยังไม่เลือก · <see cref="Problem"/> = ข้อความถึงลูกค้า (null = คิดได้)</summary>
public sealed record DeliveryFeeResult(IReadOnlyList<DeliveryRateOption> Options, DeliveryRateOption? Chosen, decimal Fee, string? Problem, bool UsesZones);

/// <summary>
/// รอบ 226 — <b>ตัวคิดค่าจัดส่งของร้านตัวเดียว</b> ที่ทั้งคำสั่งซื้อ (<c>CmsCommerceService.ComputeCheckoutAsync</c>) และการจองที่เลือก "จัดส่ง"
/// (<c>CmsBookingService.QuoteFulfillmentAsync</c>) เรียก · ย้ายตรรกะเดิมของเช็กเอาต์ออกมาทั้งก้อน (ไม่เปลี่ยนผลของคำสั่งซื้อ — ทุกข้อความ/ลำดับเดิม)
///
/// <para><b>กติกา</b> (เดิมของร้าน): โซนที่ตรงที่อยู่ (<see cref="CmsShippingService.ZoneMatchesAddress"/> — จังหวัด/รหัสไปรษณีย์) × อัตราในโซน
/// (<see cref="ShippingFeeRules.Quote"/> — เหมา/ตามน้ำหนัก/ฟรี/ส่งฟรีเมื่อครบ) · เกณฑ์ส่งฟรีระดับร้านทับโซนเมื่อร้านเลือก
/// (<see cref="ShippingFeeRules.ApplyShopFreeThreshold"/>) · คูปองส่งฟรี ⇒ 0 · ร้านไม่ตั้งโซน ⇒ ค่าส่งเหมาจ่าย/ส่งฟรีเมื่อครบยอดระดับร้าน
/// (<see cref="ShippingFeeRules.SiteFlatFee"/>) · ตามระยะทาง <b>ไม่รองรับ</b> (ระยะจาก client เชื่อไม่ได้)</para>
/// <para>ค่าที่ลูกค้าส่งมามีแค่ "ที่อยู่ + อัตราที่เลือก" — <b>ยอดค่าส่งคิดที่เซิร์ฟเวอร์เสมอ</b> (ด่าน <c>tools/required_call_site_check.py</c>)</para>
/// </summary>
public static class ShopDeliveryFee
{
    public const string RuleCode = "SHIP-RATE";

    /// <summary>โซน/อัตราที่ใช้งานของเว็บ (กรองบริษัท + ลบแล้ว) — คิวรีตัวเดียวของทั้งสองผู้เรียก</summary>
    public static Task<List<SiteShippingZone>> LoadZonesAsync(AccountingDbContext db, Guid companyId, Guid siteId, CancellationToken ct = default)
    {
        return db.SiteShippingZones.AsNoTracking()
            .Include(z => z.Rates.Where(r => r.IsActive && !r.IsDeleted))
            .Where(z => z.SiteId == siteId && z.CompanyId == companyId && z.IsActive && !z.IsDeleted)
            // ฝ่ายค้านรอบ 228 S4: ลำดับตายตัว (ลำดับเท่ากัน ⇒ สร้างก่อน ⇒ Id) — โซน "ชนะโซนถัดไป" ต้องไม่ขึ้นกับลำดับที่ฐานคืนมา
            .OrderBy(z => z.SortOrder).ThenBy(z => z.CreatedAt).ThenBy(z => z.Id)
            .ToListAsync(ct);
    }

    /// <summary>รอบ 228 (BOOKING_PAYMENT_AUDIT §9 · คำขอเจ้าของ "ค่าส่งตามระยะทาง"): โซนที่ตรงที่อยู่ <b>ตามลำดับ</b> — โซนที่ตั้ง
    /// <see cref="SiteShippingZone.StopWhenMatched"/> ตรงแล้ว ⇒ ไม่ดูโซนถัดไป (โซน "ใกล้ร้าน" ที่ระบุรหัสไปรษณีย์ของอำเภอ/เขตรอบร้าน ชนะโซนทั้งจังหวัด ·
    /// ลูกค้าไม่ได้เห็นอัตราถูกกว่าของวงนอกให้เลือกแทน) · ไม่มีโซนใดตั้ง ⇒ ทุกโซนที่ตรง (เดิมทุกตัวอักษร) ·
    /// ตัวจับคู่เดียวของ <see cref="Compute"/> และหน้าร้าน (<c>CmsShippingService.CalculateShippingOptionsAsync</c>)</summary>
    /// <remarks>ระยะทาง (กม.) จริงไม่รองรับ: ต้องใช้แผนที่/geocoding ภายนอก (ค่าใช้จ่ายต่อครั้ง · ส่งที่อยู่ลูกค้าออกนอกระบบ) และระยะจากเครื่องลูกค้าเชื่อไม่ได้ ⇒
    /// ใช้รหัสไปรษณีย์ (ผูกกับอำเภอ/เขต · ตัวเลขตรงตัว ไม่ fuzzy) แทน</remarks>
    public static IEnumerable<SiteShippingZone> MatchingZones(IEnumerable<SiteShippingZone> zones, CalculateShippingRequest address)
    {
        return zones.Where(zone => CmsShippingService.ZoneMatchesAddress(zone, address));
    }

    /// <summary>ฝ่ายค้านรอบ 228 S5: หยุดดูโซนถัดไปหลังโซนนี้ไหม — โซนที่ติ๊ก "ชนะโซนถัดไป" หยุดเฉพาะเมื่อ<b>ให้อัตราที่ใช้ได้อย่างน้อยหนึ่ง</b>
    /// (อัตราของวงในจำกัดน้ำหนัก/ปิดหมด ⇒ ไม่ปล่อยลูกค้าวงในให้ "ไม่มีวิธีจัดส่ง" — ตกไปโซนถัดไป) · ผู้เรียกวนตาม <see cref="MatchingZones"/></summary>
    public static bool StopsAfter(SiteShippingZone zone, int applicableRates)
    {
        return zone.StopWhenMatched && applicableRates > 0;
    }

    /// <summary>ฝ่ายค้านรอบ 228 S4: คำเตือนตอนบันทึกโซน — โซน "ชนะโซนถัดไป" มีลำดับซ้ำกับโซนอื่น (ลำดับจริงตัดสินด้วยวันสร้าง — เจ้าของควรตั้งลำดับให้ชัด)</summary>
    public static string? StopZoneOrderWarning(bool stopWhenMatched, int sortOrder, IEnumerable<int> otherZoneSortOrders)
        => stopWhenMatched && otherZoneSortOrders.Contains(sortOrder)
            ? $"⚠️ โซนนี้ติ๊ก “ชนะโซนถัดไป” แต่ลำดับ {sortOrder} ซ้ำกับโซนอื่น — ระบบเรียงโซนที่สร้างก่อนขึ้นก่อน · ตั้งลำดับให้โซนวงในน้อยกว่าวงนอกเพื่อให้ผลแน่นอน"
            : null;

    /// <summary>รอบ 228 (CMS_SETTINGS_UX_AUDIT §1 ข้อ 3 · คำตัดสิน Q3): เปิดจัดส่ง (คำสั่งซื้อ หรือ การจอง) แต่ไม่มีโซนและไม่มีค่าส่งเหมาจ่าย ⇒ ทุกการจัดส่ง "ส่งฟรี" เงียบ ๆ ·
    /// <b>เตือนค้าง (ไม่บล็อก)</b> จนเจ้าของกด "ส่งฟรีจริง" · null = ไม่ต้องเตือน · ตัวเดียวของหน้าตั้งค่า/แม่แบบ/ค่าตั้งจัดส่งของการจอง</summary>
    public static string? FreeByOmissionWarning(bool deliveryOn, int zoneCount, decimal? flatRate, bool freeConfirmed)
    {
        if (!deliveryOn || zoneCount > 0 || flatRate is > 0m || freeConfirmed) return null;
        return "⚠️ เปิดจัดส่งแล้วแต่ยังไม่ได้ตั้งค่าส่ง (ไม่มีโซน/อัตรา และไม่มีค่าส่งเหมาจ่าย) — ตอนนี้ทุกการจัดส่ง “ส่งฟรี” · "
            + "ตั้งค่าส่งเหมาจ่ายหรือโซน/อัตรา หรือกด “ส่งฟรีจริง” ถ้าตั้งใจส่งฟรี";
    }

    /// <param name="zones">จาก <see cref="LoadZonesAsync"/> (เรียงแล้ว)</param>
    /// <param name="goodsAfterDiscount">ยอดสินค้า/ค่าบริการหลังส่วนลด — ฐานของ "ส่งฟรีเมื่อครบ"</param>
    /// <param name="chosenRateId">อัตราที่ลูกค้าเลือก · ไม่ตรงตัวเลือกใด ⇒ โยน (ไม่สลับเงียบ) · null + ตัวเลือกเดียว ⇒ เลือกให้</param>
    /// <param name="pickupAvailable">ร้าน/บริการเปิด "รับเอง" ไหม — ใช้ในข้อความทางไปต่อ</param>
    public static DeliveryFeeResult Compute(IReadOnlyList<SiteShippingZone> zones, CalculateShippingRequest address, decimal goodsAfterDiscount,
        decimal weightKg, decimal? shopFlatRate, decimal? shopFreeThreshold, bool freeOverridesZones, bool couponFreeShip, Guid? chosenRateId,
        bool pickupAvailable)
    {
        var options = new List<DeliveryRateOption>();
        foreach (var zone in MatchingZones(zones, address))
        {
            var produced = 0;
            foreach (var rate in zone.Rates.OrderBy(r => r.SortOrder))
            {
                var q = ShippingFeeRules.Quote(new ShippingRateFacts(rate.RateType, rate.BaseRate, rate.PerKgRate,
                    rate.FreeAboveAmount, rate.MinWeightKg, rate.MaxWeightKg), goodsAfterDiscount, weightKg);
                // B8 — เกณฑ์ส่งฟรีระดับร้านทับอัตราโซนเฉพาะเมื่อร้านเลือก (ค่าเริ่มต้น = เดิม)
                q = ShippingFeeRules.ApplyShopFreeThreshold(q, shopFreeThreshold, freeOverridesZones, goodsAfterDiscount);
                if (!q.Applicable) continue;
                // B7 — คูปองส่งฟรี ⇒ ทุกตัวเลือก 0 (ลูกค้าเห็นตรงกับยอดที่ถูกเก็บ)
                options.Add(new DeliveryRateOption(rate.Id, rate.Name, zone.Name, couponFreeShip ? 0m : q.Fee, couponFreeShip || q.IsFree,
                    rate.EstimatedDeliveryDaysMin, rate.EstimatedDeliveryDaysMax));
                produced++;
            }
            if (StopsAfter(zone, produced)) break;   // รอบ 228: โซน "ชนะโซนถัดไป" ที่ให้อัตราได้จริง
        }

        if (options.Count > 0)
        {
            DeliveryRateOption? chosen = null;
            if (chosenRateId is Guid rid)
                chosen = options.FirstOrDefault(o => o.RateId == rid)
                    ?? throw new BusinessRuleException("วิธีจัดส่งที่เลือกใช้กับที่อยู่/น้ำหนักนี้ไม่ได้ — เลือกใหม่", RuleCode);
            else if (options.Count == 1)
                chosen = options[0];
            return chosen != null
                ? new DeliveryFeeResult(options, chosen, chosen.Fee, null, true)
                : new DeliveryFeeResult(options, null, 0m, "กรุณาเลือกวิธีจัดส่ง", true);
        }
        if (zones.Count > 0)
        {
            // โซนจำกัดพื้นที่ + ยังไม่กรอกที่อยู่ ⇒ บอกให้กรอก (ไม่ใช่ "ไม่มีวิธีส่ง") · กรอกแล้วไม่ตรง ⇒ บอกทางไปต่อ
            var missingAddress = string.IsNullOrWhiteSpace(address.Province) || string.IsNullOrWhiteSpace(address.PostalCode);
            var problem = missingAddress && zones.Any(CmsShippingService.ZoneNeedsAddress)
                ? "กรุณากรอกจังหวัดและรหัสไปรษณีย์เพื่อคำนวณค่าจัดส่ง"
                : "ยังไม่มีวิธีจัดส่งสำหรับจังหวัด/รหัสไปรษณีย์นี้ — ตรวจการสะกด"
                    + (pickupAvailable ? " หรือเลือกมารับเองที่ร้าน" : " หรือติดต่อร้านเพื่อสั่งทางอื่น");
            return new DeliveryFeeResult(options, null, 0m, problem, true);
        }
        // ร้านไม่ได้ตั้งโซน ⇒ ค่าส่งเหมาจ่าย/ส่งฟรีเมื่อครบยอดระดับร้าน
        var flat = couponFreeShip ? 0m : ShippingFeeRules.SiteFlatFee(shopFlatRate, shopFreeThreshold, goodsAfterDiscount);
        return new DeliveryFeeResult(options, null, flat, null, false);
    }
}
