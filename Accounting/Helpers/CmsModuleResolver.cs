using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงที่ใช้ตัดสินว่า "โมดูล CMS ไหนถูกใช้อยู่จริง" — ระดับบริษัท (รวมทุกเว็บ)
/// หรือระดับเว็บเดียวก็ได้ (ส่ง fact ของเว็บนั้นเข้ามา)</summary>
public readonly record struct CmsModuleFacts(
    bool HasCommerceSite,      // มีเว็บชนิด Ecommerce/ServiceCatalog/Hybrid (เจตนาขายของ)
    bool HasOrders,            // มีแถว SiteOrder แล้ว
    bool HasBookingServices,   // มี SiteBookingService (จองคิว/นัดหมายแบบ slot)
    bool HasBookings,          // มีแถว SiteBooking แล้ว
    bool HasLodgingProperty,   // มี LodgingProperty (ผูกเว็บหรือไม่ก็ตาม)
    bool HasHotelSite,         // มีเว็บ IndustryType.Hotel (เจตนาเป็นที่พัก — property อาจถูกลบ/ยังไม่ seed)
    bool HasAnySite);          // มีเว็บอย่างน้อย 1 (ฟอร์มติดต่อ/lead มีทุกเทมเพลต)

/// <summary>ตัวตัดสิน "โมดูล CMS ที่ใช้อยู่" ตัวเดียวของทั้งระบบ — แถบเมนูหลัก (`cmsModule:` ใน
/// Layout.navItems ผ่าน `CompanySettingsResponse.CmsModules`) และแท็บใน cms-edit
/// (`SiteResponse.Modules`) ต้องเรียกตัวนี้ทั้งคู่ ห้ามเขียนเงื่อนไขซ้ำที่ปลายทาง
/// (defect class "สำเนามือฝั่ง JS ที่ตามหลังอยู่ไม่กี่ธง").
///
/// กติกาสำคัญ: <b>ห้ามตัดสิน "bookings" จาก SiteType.Booking</b> — เว็บที่พักก็เป็น
/// SiteType.Booking แต่ใช้ระบบจองห้อง (Lodging) ไม่ใช่จองคิวแบบ slot; ถ้าใช้ SiteType
/// โรงแรมจะเห็นเมนู "การจองจากเว็บ" ที่ไม่มีวันมีข้อมูล</summary>
public static class CmsModuleResolver
{
    public const string Orders = "orders";
    public const string Bookings = "bookings";
    public const string Lodging = "lodging";
    public const string Leads = "leads";

    public static bool IsCommerceSiteType(SiteType t) =>
        t is SiteType.Ecommerce or SiteType.ServiceCatalog or SiteType.Hybrid;

    public static IReadOnlyList<string> Resolve(CmsModuleFacts f)
    {
        var list = new List<string>(4);
        if (f.HasCommerceSite || f.HasOrders) list.Add(Orders);
        if (f.HasBookingServices || f.HasBookings) list.Add(Bookings);
        if (f.HasLodgingProperty || f.HasHotelSite) list.Add(Lodging);
        if (f.HasAnySite) list.Add(Leads);
        return list;
    }
}
