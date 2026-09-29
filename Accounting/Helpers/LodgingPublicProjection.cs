using Accounting.Models.DTOs.Lodging;

namespace Accounting.Helpers;

/// <summary>ประเภทห้องที่ส่งให้หน้าจองสาธารณะ (ไม่ต้องล็อกอิน) — ตัดข้อมูลภายในออก (รอบ 200 ทีม R · F-05)
///
/// ═══ ที่มา ═══
/// <c>GET …/lodging/info</c> ใช้ <see cref="LodgingRoomTypeDto"/> ตัวเดียวกับหน้าตั้งค่า ⇒ ส่ง <c>Units</c> ทั้งชุดของทุกห้อง
/// (เลขห้อง · ชั้น · หมายเหตุภายใน "แอร์เสีย ลูกค้าร้องเรียน" · สถานะแม่บ้าน · ปิดซ่อม) และประเภทห้องที่ปิดขายแล้ว ให้ใครก็ได้
/// ขณะที่บริการเสริม/แผนราคา/นโยบายถูกกรองแล้ว ⇒ หน้าจองใช้แค่ข้อมูลประเภทห้อง (ค้นห้องว่างผ่าน endpoint ค้นหาอยู่แล้ว)</summary>
public static class LodgingPublicProjection
{
    /// <summary>เฉพาะประเภทห้องที่เปิดขาย · ไม่มีรายการห้อง (<c>Units</c> ว่าง) · ไม่มีสินค้าภายในที่ผูกไว้ ·
    /// <c>UnitCount</c> คงไว้ (จำนวนห้องไม่ใช่ข้อมูลภายในรายห้อง)</summary>
    public static List<LodgingRoomTypeDto> RoomTypes(IEnumerable<LodgingRoomTypeDto> all)
        => all.Where(r => r.IsActive).Select(r => new LodgingRoomTypeDto
        {
            Id = r.Id, PropertyId = r.PropertyId, Name = r.Name, NameEn = r.NameEn, Code = r.Code, Slug = r.Slug,
            Description = r.Description, Images = r.Images, Amenities = r.Amenities, BedType = r.BedType,
            SizeSqm = r.SizeSqm, ViewType = r.ViewType, StandardOccupancy = r.StandardOccupancy,
            MaxAdults = r.MaxAdults, MaxChildren = r.MaxChildren, MaxOccupancy = r.MaxOccupancy,
            AllowExtraBed = r.AllowExtraBed, MaxExtraBeds = r.MaxExtraBeds, PricingMode = r.PricingMode,
            BaseRate = r.BaseRate, ExtraGuestPrice = r.ExtraGuestPrice, ExtraBedPrice = r.ExtraBedPrice,
            MinNights = r.MinNights, IncludesBreakfast = r.IncludesBreakfast, IsActive = r.IsActive,
            SortOrder = r.SortOrder, UnitCount = r.UnitCount,
            ProductId = null,
            Units = new List<LodgingUnitDto>(),
        }).ToList();
}
