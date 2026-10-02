namespace Accounting.Helpers;

/// <summary>สถานะ "ลูกค้าจองผ่านเว็บได้จริงไหม" ของที่พัก — ออกเป็น <b>ชื่อ</b> (JsonStringEnumConverter) ห้าม UI เทียบตัวเลข</summary>
public enum LodgingPublicBookingStatus
{
    /// <summary>ผูกเว็บ + เปิดใช้ + เปิดจองออนไลน์ + มีห้องให้ขาย ⇒ /booking จองได้</summary>
    Live = 1,
    /// <summary>ยังไม่ผูกเว็บไซต์ ⇒ /booking ไม่รู้จักที่พักนี้ (ตกไปเป็นการ์ดนัดหมาย)</summary>
    NotLinked = 2,
    /// <summary>ผูกเว็บไว้แต่เว็บนั้นไม่อยู่แล้ว (ถูกลบ) ⇒ เท่ากับไม่ผูก</summary>
    SiteMissing = 3,
    /// <summary>ปิดใช้งานที่พัก ⇒ <c>ResolvePropertyIdForSiteAsync</c> ไม่คืนที่พักนี้</summary>
    Inactive = 4,
    /// <summary>ปิด "เปิดให้จองผ่านเว็บ" ⇒ แขกเห็นข้อมูลแต่จองเองไม่ได้</summary>
    OnlineOff = 5,
    /// <summary>ยังไม่มีห้อง (หมายเลขห้อง) ที่เปิดขาย ⇒ ค้นห้องว่างได้ 0 เสมอ</summary>
    NoRooms = 6,
}

/// <summary>ผลตัดสิน + ข้อความไทย + ทางแก้ (หน้าเว็บแสดงอย่างเดียว · F2 ข้อ 5)</summary>
public sealed record LodgingPublicReadinessResult(LodgingPublicBookingStatus Status, string Message, string? FixHint)
{
    public bool IsLive => Status == LodgingPublicBookingStatus.Live;
}

/// <summary>
/// ตัวตัดสินตัวเดียวว่า "ที่พักนี้เปิดให้แขกจองผ่านเว็บได้จริงไหม" (รอบ 202 ทีม LS · W-01)
///
/// <para><b>ที่มา</b>: ผู้ใช้ตั้งค่าที่พักครบ แต่ "เว็บไซต์ที่ผูก = ไม่ผูก" ⇒ หน้า /booking ตกไปเป็นการ์ดนัดหมาย ฿0
/// โดยหน้าตั้งค่าไม่มีป้ายเตือนอะไรเลย ⇒ ผู้ใช้คิดว่าเว็บ "ไม่อ้างอิงข้อมูลห้อง" · เงื่อนไขที่ storefront ใช้จริงอยู่ใน
/// <c>LodgingService.ResolvePropertyIdForSiteAsync</c> (SiteId ตรง + IsActive) และ engine (OnlineBookingEnabled · ห้องที่มี)
/// — ตัวนี้ถอดเงื่อนไขชุดเดียวกันออกมาให้หน้าตั้งค่าบอกเหตุผล + ทางแก้ แทนการเงียบ</para>
///
/// <para>ลำดับ: ไม่ผูก → เว็บหาย → ปิดใช้ → ปิดจองออนไลน์ → ไม่มีห้อง → พร้อม (ข้อแรกที่ไม่ผ่านคือสิ่งที่ต้องแก้ก่อน) ·
/// "ไม่รู้" ไม่ตกเป็น "พร้อม": ห้องว่าง 0 ⇒ ไม่พร้อม (DECISION_DOCTRINE §1)</para>
/// </summary>
public static class LodgingPublicReadiness
{
    /// <param name="bindableSiteCount">จำนวนเว็บประเภทที่พักที่ผูกให้ที่พักนี้ได้ทันที (<see cref="BindCandidates"/>) — มี ⇒ ทางแก้ชี้ปุ่ม "ผูกที่พักนี้กับเว็บ"</param>
    public static LodgingPublicReadinessResult Evaluate(
        Guid? siteId, bool siteExists, string? siteName, bool isActive, bool onlineBookingEnabled, int sellableUnitCount,
        int bindableSiteCount = 0)
    {
        var bindHint = bindableSiteCount > 0
            ? "กดปุ่ม «ผูกที่พักนี้กับเว็บ» บนป้ายนี้ (หรือเลือก «เว็บไซต์ที่ผูก» ในส่วนข้อมูลที่พัก แล้วกดบันทึก)"
            : null;
        if (siteId is null)
            return new(LodgingPublicBookingStatus.NotLinked,
                "ยังไม่เปิดจองออนไลน์ — ที่พักนี้ยังไม่ผูกกับเว็บไซต์ หน้า /booking ของเว็บจึงไม่แสดงห้องพักของที่นี่",
                bindHint ?? "เลือก «เว็บไซต์ที่ผูก» ในส่วนข้อมูลที่พัก แล้วกดบันทึก");
        if (!siteExists)
            return new(LodgingPublicBookingStatus.SiteMissing,
                "ยังไม่เปิดจองออนไลน์ — เว็บไซต์ที่ผูกไว้ไม่อยู่ในระบบแล้ว (อาจถูกลบ)",
                bindHint ?? "เลือก «เว็บไซต์ที่ผูก» ใหม่ แล้วกดบันทึก");
        var where = string.IsNullOrWhiteSpace(siteName) ? "เว็บที่ผูก" : $"เว็บ “{siteName}”";
        if (!isActive)
            return new(LodgingPublicBookingStatus.Inactive,
                $"ยังไม่เปิดจองออนไลน์ — ที่พักปิดใช้งานอยู่ {where} จึงไม่แสดงที่พักนี้",
                "ติ๊ก «เปิดใช้งานที่พัก» แล้วกดบันทึก");
        if (!onlineBookingEnabled)
            return new(LodgingPublicBookingStatus.OnlineOff,
                $"ยังไม่เปิดจองออนไลน์ — ปิด «เปิดให้จองผ่านเว็บ» ไว้ แขกบน{where}เห็นข้อมูลแต่จองเองไม่ได้ (พนักงานรับจองได้ตามปกติ)",
                "ติ๊ก «เปิดให้จองผ่านเว็บ» แล้วกดบันทึก");
        if (sellableUnitCount <= 0)
            return new(LodgingPublicBookingStatus.NoRooms,
                $"ยังจองไม่ได้ — ยังไม่มีห้อง (หมายเลขห้อง) ที่เปิดขาย แขกบน{where}จะค้นแล้วไม่พบห้องว่าง",
                "เพิ่มประเภทห้องและหมายเลขห้องที่แท็บ «ประเภทห้อง & ห้อง»");
        return new(LodgingPublicBookingStatus.Live, $"เปิดจองออนไลน์อยู่ — แขกจองเองได้ที่ /booking ของ{where}", null);
    }

    /// <summary>ผูกด่วนจากป้ายสถานะได้เฉพาะเมื่อที่พักยังไม่มีเว็บที่ใช้งานได้ (ไม่ผูก/เว็บหาย) — ที่พักที่ผูกเว็บอยู่แล้วเปลี่ยนเว็บที่ช่องเดิม</summary>
    public static bool AllowsQuickBind(LodgingPublicBookingStatus status)
        => status is LodgingPublicBookingStatus.NotLinked or LodgingPublicBookingStatus.SiteMissing;

    /// <summary>
    /// เว็บที่เสนอให้ "ผูกที่พักนี้กับเว็บ" ได้ทันที (รอบ 202 ทีม LS · ต่อจาก LW คำตัดสินข้อ 118) — เซิร์ฟเวอร์ตัดสิน หน้าแสดงอย่างเดียว
    /// <para>เฉพาะ<b>เว็บประเภทที่พัก</b> (<c>Site.IndustryType == Hotel</c> ตัวเดียวกับ <c>StorefrontSiteInfo.IsLodgingSite</c>) ที่
    /// <b>ไม่ได้ผูกกับที่พักอื่น</b> (เว็บหนึ่งผูกได้ที่พักเดียว — ผูกซ้ำ <c>EnsureSiteNotBoundElsewhereAsync</c> ปฏิเสธอยู่แล้ว ห้ามเสนอ) ·
    /// เว็บประเภทอื่นยังเลือกได้จากช่อง «เว็บไซต์ที่ผูก» ตามเดิม แค่ไม่ถูกเสนอเป็นปุ่ม</para>
    /// </summary>
    public static List<LodgingSiteBindOption> BindCandidates(Guid propertyId, IEnumerable<LodgingSiteBindSource> sites)
        => sites.Where(s => s.IsLodgingSite && (s.BoundPropertyId is null || s.BoundPropertyId == propertyId))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new LodgingSiteBindOption(s.SiteId, s.Name)).ToList();

    /// <summary>ด่านเฉพาะของปุ่มผูกด่วน (ไม่ซ้ำด่านเดิม: tenant/ผูกซ้ำ อยู่ที่ <c>EnsurePropertyRefsBelongAsync</c>/<c>EnsureSiteNotBoundElsewhereAsync</c>)
    /// · null = ผ่าน</summary>
    public static string? QuickBindRefusal(LodgingPublicBookingStatus currentStatus, bool targetIsLodgingSite)
    {
        if (!AllowsQuickBind(currentStatus))
            return "ที่พักนี้ผูกกับเว็บอยู่แล้ว — ถ้าต้องการเปลี่ยนเว็บ ให้เลือกที่ช่อง «เว็บไซต์ที่ผูก» แล้วกดบันทึก";
        if (!targetIsLodgingSite)
            return "เว็บนี้ไม่ใช่เว็บประเภทที่พัก — ปุ่มผูกด่วนใช้กับเว็บที่พักเท่านั้น (เว็บประเภทอื่นเลือกได้ที่ช่อง «เว็บไซต์ที่ผูก»)";
        return null;
    }
}

/// <summary>ข้อเท็จจริงของเว็บ 1 เว็บสำหรับคัดผู้สมัครผูกด่วน</summary>
public sealed record LodgingSiteBindSource(Guid SiteId, string Name, bool IsLodgingSite, Guid? BoundPropertyId);

/// <summary>เว็บที่ผูกด่วนได้ (หน้าเว็บสร้างปุ่ม/ตัวเลือกจากลิสต์นี้)</summary>
public sealed record LodgingSiteBindOption(Guid SiteId, string SiteName);
