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
    bool HasLeadSource);       // เว็บรับคำขอได้จริง: มีบล็อกฟอร์มติดต่อ (ContactForm) บนหน้าที่ยังไม่ลบ · ฟอร์มกำหนดเองที่ส่งเข้าระบบ · หรือมีแถว CmsLead แล้ว
                               // (รอบ 215 — เดิม HasAnySite ⇒ ทุกเว็บได้ปุ่ม "คำขอ/Lead" แม้ไม่มีทางรับ · CMS_UX_REDESIGN §1 ข้อ 4)

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

    /// <summary>เว็บนี้เป็นเว็บที่พักไหม (storefront ใช้ตัดสินว่าบล็อกจองเมื่อ <c>/lodging/info</c> ตอบ 404 ควรบอก "ยังไม่เปิดจองออนไลน์"
    /// แทนการ์ดนัดหมาย) — ประเภทธุรกิจที่พัก <b>หรือ</b> มีที่พัก (ไม่ลบ) ผูกเว็บนี้ แม้ประเภทธุรกิจยังเป็นค่าอื่น (เว็บเก่า/เปลี่ยนประเภททีหลัง ·
    /// รอบ 202 LW ฝ่ายค้าน P2-4 — เดิมดู Hotel อย่างเดียว ⇒ ที่พักที่ปิดชั่วคราวบนเว็บประเภทอื่นตกไปการ์ดนัดหมาย)</summary>
    public static bool IsLodgingSite(IndustryType industry, bool hasBoundLodgingProperty) =>
        industry == IndustryType.Hotel || hasBoundLodgingProperty;

    public static bool IsCommerceSiteType(SiteType t) =>
        t is SiteType.Ecommerce or SiteType.ServiceCatalog or SiteType.Hybrid;

    public static IReadOnlyList<string> Resolve(CmsModuleFacts f)
    {
        var list = new List<string>(4);
        if (f.HasCommerceSite || f.HasOrders) list.Add(Orders);
        if (f.HasBookingServices || f.HasBookings) list.Add(Bookings);
        if (f.HasLodgingProperty || f.HasHotelSite) list.Add(Lodging);
        if (f.HasLeadSource) list.Add(Leads);
        return list;
    }

    /// <summary>บล็อกหน้าเว็บที่ส่งคำขอเข้า <c>storefront/leads</c> (= ทางรับ lead) — ฟอร์มติดต่อ/RFQ/นัดดู (ContactForm) และ
    /// สมัครรับข่าว (Newsletter ⇒ lead ชนิด Subscription · storefront case 17 · seed ใน 4 เทมเพลต — ฝ่ายค้านรอบ 215 S3) ·
    /// ผู้ใช้: <c>CmsSiteService.LoadLeadSourceSiteIdsAsync</c> (query) — ห้ามเขียนชุดซ้ำ</summary>
    public static readonly CmsBlockType[] LeadBlockTypes = { CmsBlockType.ContactForm, CmsBlockType.Newsletter };

    public const string NavLabelBookings = "เว็บไซต์ & การจอง";
    public const string NavLabelShop = "เว็บไซต์ & ร้านออนไลน์";
    public const string NavLabelLodging = "เว็บไซต์ & ที่พัก";
    public const string NavLabelPlain = "เว็บไซต์";

    /// <summary>ชื่อหมวดเมนูซ้ายของงานเว็บ ตามโมดูลที่บริษัทใช้ (คำตัดสินข้อ 176 · รอบ 215) — ตัวตัดสินเดียว · layout.js แค่แสดง
    /// (ผ่าน <c>CompanySettingsResponse.CmsNavLabel</c>) · มีจองคิว หรือมีที่พักปนกับร้าน ⇒ "การจอง" · ร้านอย่างเดียว ⇒ "ร้านออนไลน์" ·
    /// ที่พักอย่างเดียว ⇒ "ที่พัก" · ไม่มีโมดูลงาน (มีแค่ lead/ยังไม่มีเว็บ) ⇒ "เว็บไซต์"</summary>
    public static string NavSectionLabel(IReadOnlyCollection<string>? modules)
    {
        var m = modules ?? Array.Empty<string>();
        bool orders = m.Contains(Orders), bookings = m.Contains(Bookings), lodging = m.Contains(Lodging);
        if (bookings || (lodging && orders)) return NavLabelBookings;
        if (lodging) return NavLabelLodging;
        if (orders) return NavLabelShop;
        return NavLabelPlain;
    }
}
