using System.Globalization;
using System.Text;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Cms;

namespace Accounting.Helpers;

/// <summary>ซ่อมข้อมูลเว็บที่พักที่ seed ไว้ก่อนรอบ 202 (ทีม LW · คำตัดสินข้อ 117 · W-02/W-03) — สองคำสั่ง รันทุกบูตได้ (idempotent)
/// ครอบ <c>pg_advisory_xact_lock</c> คีย์คงที่จาก <see cref="AdvisoryLockKey"/> (สองเครื่องบูตพร้อมกัน = ทำทีละเครื่อง)
///
/// <para><b>① บล็อกราคาห้อง seed → บล็อกข้อมูลสด</b> (<see cref="RoomBlocksSql"/>): เดิม <c>HotelPlan</c> พิมพ์รายการห้อง+ราคาเป็น
/// RichText + PricingTable ตายตัว ⇒ เจ้าของแก้ราคาในระบบที่พักแล้วหน้าแรกยังโชว์ ฿1,500 เดิม · เว็บไม่ผูกที่พักก็โชว์ราคาที่จองไม่ได้.
/// แทน<b>เฉพาะ</b>บล็อกที่ <c>BlockType</c> + <c>ConfigJson</c> ตรง snapshot ของ seed <b>ทุกไบต์</b> — snapshot สร้างจากฟังก์ชัน
/// <c>CmsSiteTemplateSeeder.Legacy*</c> ตัวเดียวกับที่เคย seed (ไม่ใช่สำเนาข้อความ) · บล็อกที่เจ้าของแก้แม้ตัวอักษรเดียว / ซ่อนไว้ /
/// มีคำแปล = ไม่แตะ (ทิศปลอดภัย: ราคาเดิมที่เจ้าของตั้งใจคงไว้ต้องอยู่) · snapshot ครอบสองรุ่น: ก่อนรอบ 158 (ก่อน d2ad229b) และรอบ 158–201.
/// ต่อหน้า: บล็อกแรก (ตาม SortOrder) ที่ตรง ⇒ เปลี่ยนเป็น <see cref="CmsBlockType.LodgingRooms"/> · ตัวที่เหลือที่ตรง ⇒ ซ่อน (<c>IsVisible=false</c>
/// — ไม่ลบ เจ้าของเปิดกลับได้ในตัวแก้เว็บ) · หน้าที่มีบล็อก LodgingRooms อยู่แล้ว = ไม่แตะ (รอบที่สองเป็นต้นไป 0 แถว).
/// Hero หน้า rooms ที่มี "3 ประเภท · 11 ห้อง" จาก seed ⇒ Hero รุ่นไม่มีตัวเลข (ตรงทุกไบต์เท่านั้น)</para>
///
/// <para><b>② บริการนัดหมาย ฿0 ที่ GET สาธารณะเคยสร้างเอง</b> (<see cref="AutoSeedServiceCleanupSql"/>): <c>CmsBookingService.GetServicesAsync</c>
/// เดิมสร้าง "นัดหมาย / จอง" (<c>CreatedBy='auto-seed'</c>) ทุกครั้งที่หน้าเว็บโหลดแล้วยังไม่มีบริการ ⇒ เว็บที่พักที่ไม่ผูกที่พักได้การ์ด
/// นัดหมาย ฿0 แทนระบบจองห้อง + เมนู "การจองคิว" ที่ไม่มีวันมีข้อมูล. ลบแบบ soft เฉพาะแถวที่: สร้างโดย auto-seed · <b>ไม่มีการจองสักแถว</b> ·
/// <b>ไม่เคยถูกแก้</b> (<c>UpdatedBy IS NULL</c>) · อยู่บนเว็บประเภทที่พัก (<c>IndustryType = Hotel</c>) — เว็บประเภทอื่นอาจใช้บริการนี้จริง ไม่แตะ</para>
///
/// <para>ค่าข้อความทุกตัวในคำสั่งส่งเป็น base64 (<c>convert_from(decode(…,'base64'),'UTF8')</c>) — ไม่มีวงเล็บปีกกา/อัญประกาศใน SQL
/// (ExecuteSqlRaw ตีความ <c>{…}</c> ได้ · JSON ของบล็อกมีทั้งคู่) และเทียบแบบ <c>=</c> ตรงตัว ห้าม LIKE/trim/jsonb</para></summary>
public static class LodgingSiteSeedMigration
{
    /// <summary>กติกาแทนบล็อก: (ชนิดเดิม, ConfigJson เดิมตรงทุกไบต์) → (ชนิดใหม่, ConfigJson ใหม่)</summary>
    internal readonly record struct Rule(CmsBlockType OldType, string OldConfigJson, CmsBlockType NewType, string NewConfigJson);

    /// <summary>บล็อกรายการห้อง/ราคา seed — แทนด้วย LodgingRooms ตัวเดียวต่อหน้า (ตัวแรก) ที่เหลือซ่อน</summary>
    internal static IReadOnlyList<Rule> RoomListRules()
    {
        return new[]
        {
            new Rule(CmsBlockType.RichText, CmsSiteTemplateSeeder.LegacyHotelRoomsRichTextConfig(), CmsBlockType.LodgingRooms, CmsSiteTemplateSeeder.HotelLiveRoomsConfig()),
            new Rule(CmsBlockType.PricingTable, CmsSiteTemplateSeeder.LegacyHotelPricingTableConfig(), CmsBlockType.LodgingRooms, CmsSiteTemplateSeeder.HotelLiveRoomsConfig()),
            // รุ่นก่อนรอบ 158 (ก่อน d2ad229b · ฝ่ายค้าน P2-3) — 4 ประเภท "Junior Suite ฿3,800" + รูป placehold.co ของห้องที่ไม่มีจริง
            new Rule(CmsBlockType.RichText, CmsSiteTemplateSeeder.LegacyHotelRoomsRichTextV1Config(), CmsBlockType.LodgingRooms, CmsSiteTemplateSeeder.HotelLiveRoomsConfig()),
            new Rule(CmsBlockType.PricingTable, CmsSiteTemplateSeeder.LegacyHotelPricingTableV1Config(), CmsBlockType.LodgingRooms, CmsSiteTemplateSeeder.HotelLiveRoomsConfig()),
            new Rule(CmsBlockType.RichText, CmsSiteTemplateSeeder.LegacyHotelRoomsPageRichTextV1Config(), CmsBlockType.LodgingRooms, CmsSiteTemplateSeeder.HotelLiveRoomsConfig()),
            new Rule(CmsBlockType.Gallery, CmsSiteTemplateSeeder.LegacyHotelRoomsGalleryV1Config(), CmsBlockType.LodgingRooms, CmsSiteTemplateSeeder.HotelLiveRoomsConfig()),
        };
    }

    /// <summary>Hero หน้า rooms ที่มีจำนวนประเภท/ห้องจาก seed (ทั้งสองรุ่น) — แทนตัวต่อตัวด้วย Hero ไม่มีตัวเลข</summary>
    internal static IReadOnlyList<Rule> RoomsHeroRules()
    {
        return new[]
        {
            new Rule(CmsBlockType.Hero, CmsSiteTemplateSeeder.LegacyHotelRoomsHeroConfig(), CmsBlockType.Hero, CmsSiteTemplateSeeder.HotelRoomsHeroConfig()),
            new Rule(CmsBlockType.Hero, CmsSiteTemplateSeeder.LegacyHotelRoomsHeroV1Config(), CmsBlockType.Hero, CmsSiteTemplateSeeder.HotelRoomsHeroConfig()),
        };
    }

    /// <summary>ป้ายใน UpdatedBy/CreatedBy ของแถวที่ migration นี้แตะ — ให้ตามรอยได้ว่าใครเปลี่ยน</summary>
    internal const string Actor = "migration:r202-lw";

    internal static string LockKey =>
        AdvisoryLockKey.For("db-migration", "lodging-site-seed-r202").ToString(CultureInfo.InvariantCulture);

    /// <summary>ข้อความ → นิพจน์ SQL ที่ได้ข้อความเดิมทุกไบต์ (base64 ไม่มี ' { } \ ให้หนี)</summary>
    internal static string Text(string s) =>
        "convert_from(decode('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "','base64'),'UTF8')";

    private static string I(CmsBlockType t) => ((int)t).ToString(CultureInfo.InvariantCulture);

    /// <summary>① แทนบล็อกห้อง/ราคา seed ด้วยบล็อกข้อมูลสด · Hero ตัวเลข seed ⇒ Hero ไม่มีตัวเลข</summary>
    public static string RoomBlocksSql()
    {
        var hotel = ((int)IndustryType.Hotel).ToString(CultureInfo.InvariantCulture);
        var live = I(CmsBlockType.LodgingRooms);
        var match = string.Join(" OR ", RoomListRules().Select(r =>
            $"(b.\"BlockType\" = {I(r.OldType)} AND b.\"ConfigJson\" = {Text(r.OldConfigJson)})"));
        var heroMatch = string.Join(" OR ", RoomsHeroRules().Select(h =>
            $"(b.\"BlockType\" = {I(h.OldType)} AND b.\"ConfigJson\" = {Text(h.OldConfigJson)})"));
        var sb = new StringBuilder();
        sb.Append("DO $mig$ BEGIN ");
        sb.Append("PERFORM pg_advisory_xact_lock(").Append(LockKey).Append("); ");
        // ① รายการห้อง: ตัวแรกต่อหน้า → LodgingRooms · ตัวถัดไป → ซ่อน
        sb.Append("WITH m AS (SELECT b.\"Id\", ROW_NUMBER() OVER (PARTITION BY b.\"PageId\" ORDER BY b.\"SortOrder\", b.\"Id\") AS rn ");
        sb.Append("FROM \"PageBlocks\" b ");
        sb.Append("JOIN \"SitePages\" p ON p.\"Id\" = b.\"PageId\" AND p.\"CompanyId\" = b.\"CompanyId\" ");
        sb.Append("JOIN \"Sites\" s ON s.\"Id\" = p.\"SiteId\" AND s.\"CompanyId\" = p.\"CompanyId\" ");
        sb.Append("WHERE s.\"IndustryType\" = ").Append(hotel).Append(" AND b.\"IsVisible\" = true AND b.\"IsDeleted\" = false ");
        sb.Append("AND (").Append(match).Append(") ");
        sb.Append("AND NOT EXISTS (SELECT 1 FROM \"PageBlockTranslations\" t WHERE t.\"PageBlockId\" = b.\"Id\") ");
        sb.Append("AND NOT EXISTS (SELECT 1 FROM \"PageBlocks\" e WHERE e.\"PageId\" = b.\"PageId\" AND e.\"CompanyId\" = b.\"CompanyId\" AND e.\"BlockType\" = ").Append(live).Append(")) ");
        sb.Append("UPDATE \"PageBlocks\" x SET ");
        sb.Append("\"BlockType\" = CASE WHEN m.rn = 1 THEN ").Append(live).Append(" ELSE x.\"BlockType\" END, ");
        sb.Append("\"ConfigJson\" = CASE WHEN m.rn = 1 THEN ").Append(Text(CmsSiteTemplateSeeder.HotelLiveRoomsConfig())).Append(" ELSE x.\"ConfigJson\" END, ");
        sb.Append("\"IsVisible\" = CASE WHEN m.rn = 1 THEN x.\"IsVisible\" ELSE false END, ");
        sb.Append("\"UpdatedAt\" = now(), \"UpdatedBy\" = ").Append(Text(Actor)).Append(' ');
        sb.Append("FROM m WHERE x.\"Id\" = m.\"Id\"; ");
        // Hero หน้า rooms ที่มีจำนวนประเภท/ห้องจาก seed
        sb.Append("UPDATE \"PageBlocks\" b SET \"ConfigJson\" = ").Append(Text(CmsSiteTemplateSeeder.HotelRoomsHeroConfig())).Append(", ");
        sb.Append("\"UpdatedAt\" = now(), \"UpdatedBy\" = ").Append(Text(Actor)).Append(' ');
        sb.Append("FROM \"SitePages\" p, \"Sites\" s ");
        sb.Append("WHERE p.\"Id\" = b.\"PageId\" AND p.\"CompanyId\" = b.\"CompanyId\" AND s.\"Id\" = p.\"SiteId\" AND s.\"CompanyId\" = p.\"CompanyId\" ");
        sb.Append("AND s.\"IndustryType\" = ").Append(hotel).Append(" AND b.\"IsDeleted\" = false ");
        sb.Append("AND (").Append(heroMatch).Append(") ");
        sb.Append("AND NOT EXISTS (SELECT 1 FROM \"PageBlockTranslations\" t WHERE t.\"PageBlockId\" = b.\"Id\"); ");
        sb.Append("END $mig$;");
        return sb.ToString();
    }

    /// <summary>② soft-delete บริการนัดหมาย ฿0 ที่ GET สาธารณะเคยสร้างเองบนเว็บที่พัก (ไม่มีการจอง · ไม่เคยถูกแก้)</summary>
    public static string AutoSeedServiceCleanupSql()
    {
        var hotel = ((int)IndustryType.Hotel).ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("DO $mig$ BEGIN ");
        sb.Append("PERFORM pg_advisory_xact_lock(").Append(LockKey).Append("); ");
        sb.Append("UPDATE \"SiteBookingServices\" v SET \"IsDeleted\" = true, \"IsActive\" = false, ");
        sb.Append("\"UpdatedAt\" = now(), \"UpdatedBy\" = ").Append(Text(Actor)).Append(' ');
        sb.Append("WHERE v.\"CreatedBy\" = ").Append(Text(CmsBookingAutoSeedMarker)).Append(" AND v.\"IsDeleted\" = false AND v.\"UpdatedBy\" IS NULL ");
        sb.Append("AND EXISTS (SELECT 1 FROM \"Sites\" s WHERE s.\"Id\" = v.\"SiteId\" AND s.\"CompanyId\" = v.\"CompanyId\" AND s.\"IndustryType\" = ").Append(hotel).Append(") ");
        sb.Append("AND NOT EXISTS (SELECT 1 FROM \"SiteBookings\" k WHERE k.\"BookingServiceId\" = v.\"Id\" AND k.\"CompanyId\" = v.\"CompanyId\") ");
        // ฝ่ายค้าน P2-5(ก): เจ้าของเปิดช่วงเวลา/ใส่คำแปลให้บริการนี้แล้ว = ใช้งานจริง (UpdatedBy ไม่เปลี่ยนเมื่อแก้ตารางลูก) ⇒ ไม่แตะ
        sb.Append("AND NOT EXISTS (SELECT 1 FROM \"SiteBookingSlots\" l WHERE l.\"BookingServiceId\" = v.\"Id\" AND l.\"CompanyId\" = v.\"CompanyId\") ");
        sb.Append("AND NOT EXISTS (SELECT 1 FROM \"SiteBookingServiceTranslations\" t WHERE t.\"BookingServiceId\" = v.\"Id\" AND t.\"CompanyId\" = v.\"CompanyId\"); ");
        sb.Append("END $mig$;");
        return sb.ToString();
    }

    /// <summary>ค่า CreatedBy ที่ตัวสร้างบริการอัตโนมัติ (ถอดออกแล้วในรอบ 202) เคยประทับ</summary>
    internal const string CmsBookingAutoSeedMarker = "auto-seed";
}
