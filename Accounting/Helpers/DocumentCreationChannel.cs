using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ช่องทางที่เอกสารถูกสร้าง — ใช้เฉพาะ "รายงานตรวจสอบการใช้งาน" ฝั่งแอดมินแพลตฟอร์ม (อ่านอย่างเดียว · ไม่ตัดสินเงิน/ภาษี)</summary>
public enum DocumentCreationChannel
{
    /// <summary>ผู้ใช้ในระบบสร้างจากหน้าจอ (CreatedBy = รหัสผู้ใช้ และไม่มีร่องรอยช่องทางอื่น)</summary>
    Manual,
    Ocr,
    Import,
    Api,
    Pos,
    Lodging,
    /// <summary>หน้าร้าน/เว็บไซต์ CMS (สั่งซื้อ · จอง · ฟอร์ม · lead)</summary>
    Storefront,
    Recurring,
    /// <summary>ซิงก์จากระบบเชื่อมต่อ/marketplace</summary>
    Integration,
    /// <summary>ลงรอบโอนของช่องทางขาย (settlement)</summary>
    Settlement,
    ExpenseClaim,
    PlatformBilling,
    /// <summary>งานอัตโนมัติของระบบ (CreatedBy = "System") — รวมงานประจำที่รันตามรอบซึ่งไม่ใช่ใบล่าสุดของรายการประจำ</summary>
    SystemJob,
    /// <summary>ไม่มีร่องรอยพอจะบอกช่องทาง — ห้ามเดา (LINE ไม่ประทับป้าย ⇒ ตกที่นี่)</summary>
    Unknown,
}

/// <summary>หลักฐานของเอกสาร 1 กลุ่ม — ทุกช่องมาจากคอลัมน์/ตารางที่มีจริง (ไม่มีการเดาจากชื่อ/ข้อความ)</summary>
/// <param name="OriginModule"><c>Document.OriginModule</c> ("Import" · "Lodging" · "Pos" · "Ocr" · null)</param>
/// <param name="CreatedByMarker">ชนิดของ <c>Document.CreatedBy</c> ที่ SQL จัดกลุ่มแล้ว — ค่าใน <see cref="DocumentCreationChannelRule"/>.Marker*</param>
/// <param name="HasOcrLink">มี <c>OcrScanResult.CreatedDocumentId</c> ชี้มาที่ใบนี้</param>
/// <param name="HasPosLink">มี <c>PosOrder.DocumentId</c> ชี้มาที่ใบนี้</param>
/// <param name="HasStorefrontLink">มีรายการหน้าร้าน (<c>SiteOrder</c>/<c>SiteBooking</c>/<c>SiteFormSubmission</c>/<c>CmsLead</c>) ชี้ <c>ErpDocumentId</c> มาที่ใบนี้</param>
/// <param name="HasApiMeter">มี <c>UsageEvent</c> ที่ผูก ApiClient อ้างใบนี้ (มิเตอร์ของ API สาธารณะ)</param>
/// <param name="IsRecurringLast">เป็น <c>RecurringTransaction.LastDocumentId</c> (เก็บแค่ใบล่าสุดของแต่ละรายการ)</param>
public readonly record struct DocumentOriginProbe(
    string? OriginModule,
    string? CreatedByMarker,
    bool HasOcrLink = false,
    bool HasPosLink = false,
    bool HasStorefrontLink = false,
    bool HasApiMeter = false,
    bool IsRecurringLast = false);

/// <summary>ตัวตัดสินเดียวว่า "เอกสารนี้มาจากช่องทางไหน" สำหรับรายงานการใช้งานแอดมิน
///
/// <para>ลำดับความเชื่อถือ: (1) <c>OriginModule</c> ที่โมดูลประทับเอง → (2) ตารางที่ผูก FK กลับมาที่ใบ (OCR/POS/หน้าร้าน/มิเตอร์ API)
/// → (3) ป้าย <c>CreatedBy</c> ของเส้นระบบ ("api:v1" · "storefront-*" · "OCR-*" · "system:settlement:*" …) → (4) ใบล่าสุดของรายการประจำ
/// → (5) "System" = งานอัตโนมัติ → (6) รหัสผู้ใช้ = สร้างจากหน้าจอ → อื่น ๆ = <see cref="DocumentCreationChannel.Unknown"/>.</para>
/// <para>ข้อจำกัดที่บอกผู้อ่านตรง ๆ (ไม่แต่งตัวเลข): LINE บันทึก CreatedBy เป็นอีเมลผู้ใช้โดยไม่มีป้ายช่องทาง ⇒ "ไม่ทราบ" ·
/// รายการประจำเก็บเฉพาะใบล่าสุด ⇒ ใบงวดก่อน ๆ อยู่ใน "งานอัตโนมัติ"/"หน้าจอ" (ถ้ากดรันเอง).</para></summary>
public static class DocumentCreationChannelRule
{
    // ── ชนิดของ CreatedBy (ผลของ CASE ใน SQL — ตัวเดียวกับที่ controller ใช้จัดกลุ่ม) ──
    public const string MarkerNone = "none";
    public const string MarkerUser = "user";
    public const string MarkerApi = "api";
    public const string MarkerStorefront = "storefront";
    public const string MarkerOcr = "ocr";
    public const string MarkerSettlement = "settlement";
    public const string MarkerExpenseClaim = "expense-claim";
    public const string MarkerPlatformBilling = "platform-billing";
    public const string MarkerIntegration = "integration";
    public const string MarkerSystem = "system";
    public const string MarkerEmail = "email";
    public const string MarkerOther = "other";

    // ── ป้าย CreatedBy ที่เส้นระบบประทับจริง (ดูจุดเรียก CreateDocumentAsync/new Document ของแต่ละเส้น) ──
    /// <summary><c>DocumentsV1Controller</c> — "api:v1"</summary>
    public const string ApiActorPrefix = "api:";
    /// <summary><c>CmsBookingService</c>/<c>CmsCommerceService</c> — "storefront-booking"/"storefront-customer"</summary>
    public const string StorefrontActorPrefix = "storefront-";
    /// <summary><c>OcrService</c> — "OCR-DBD-AutoCreate"/"OCR-FallbackAutoCreate"</summary>
    public const string OcrActorPrefix = "OCR-";
    /// <summary><c>SettlementPostingKeys.CreatorPrefix</c> — "system:settlement:{batch}:"</summary>
    public const string SettlementActorPrefix = "system:settlement:";
    /// <summary><c>ExpenseClaimService</c> — "system:expense-claim" / "system:expense-claim:no-receipt"</summary>
    public const string ExpenseClaimActorPrefix = "system:expense-claim";
    /// <summary><c>PlatformBillingDocumentIssuer.Actor</c></summary>
    public const string PlatformBillingActor = "platform-billing";
    /// <summary><c>ECommerceService</c></summary>
    public const string EcommerceActor = "system-ecommerce";
    /// <summary><c>IntegrationService</c> — "integration-sync"/"integration-resync"</summary>
    public const string IntegrationActorPrefix = "integration-";
    /// <summary><c>RecurringTransactionService</c> ตอนรันตามรอบ</summary>
    public const string SystemActor = "System";
    /// <summary>รูป GUID 36 ตัว (LIKE) — CreatedBy ของผู้ใช้จากหน้าจอคือ userId.ToString()</summary>
    public const string GuidLikePattern = "________-____-____-____-____________";

    public static DocumentCreationChannel Classify(DocumentOriginProbe p)
    {
        // เทียบแบบไม่สนตัวพิมพ์ — ตรงกับ DocumentService.IsLodgingOrigin
        switch (p.OriginModule?.Trim().ToLowerInvariant())
        {
            case "import": return DocumentCreationChannel.Import;
            case "lodging": return DocumentCreationChannel.Lodging;
            case "pos": return DocumentCreationChannel.Pos;
            case "ocr": return DocumentCreationChannel.Ocr;
            // รอบ 205 ทีม W1 (R-04): เส้นอัตโนมัติหน้าเว็บประทับต้นทางแล้ว (เดิมแยกจาก marker ของ CreatedBy เท่านั้น)
            case "cmsbooking": return DocumentCreationChannel.Storefront;
            case "site-order": return DocumentCreationChannel.Storefront;
        }
        if (p.HasPosLink) return DocumentCreationChannel.Pos;
        if (p.HasOcrLink) return DocumentCreationChannel.Ocr;
        if (p.HasStorefrontLink) return DocumentCreationChannel.Storefront;
        if (p.HasApiMeter) return DocumentCreationChannel.Api;
        switch (p.CreatedByMarker)
        {
            case MarkerApi: return DocumentCreationChannel.Api;
            case MarkerStorefront: return DocumentCreationChannel.Storefront;
            case MarkerOcr: return DocumentCreationChannel.Ocr;
            case MarkerSettlement: return DocumentCreationChannel.Settlement;
            case MarkerExpenseClaim: return DocumentCreationChannel.ExpenseClaim;
            case MarkerPlatformBilling: return DocumentCreationChannel.PlatformBilling;
            case MarkerIntegration: return DocumentCreationChannel.Integration;
        }
        if (p.IsRecurringLast) return DocumentCreationChannel.Recurring;
        return p.CreatedByMarker switch
        {
            MarkerSystem => DocumentCreationChannel.SystemJob,
            MarkerUser => DocumentCreationChannel.Manual,
            _ => DocumentCreationChannel.Unknown,
        };
    }

    /// <summary>ชื่อช่องทาง (ไทย) — หน้าเว็บแสดงค่านี้ ห้ามมีตารางที่สอง</summary>
    public static string Label(DocumentCreationChannel c) => c switch
    {
        DocumentCreationChannel.Manual => "สร้างจากหน้าจอ",
        DocumentCreationChannel.Ocr => "OCR สแกน",
        DocumentCreationChannel.Import => "นำเข้าไฟล์ (CSV/Excel)",
        DocumentCreationChannel.Api => "API",
        DocumentCreationChannel.Pos => "POS",
        DocumentCreationChannel.Lodging => "ที่พัก/การจองห้อง",
        DocumentCreationChannel.Storefront => "หน้าร้าน/เว็บไซต์ (CMS)",
        DocumentCreationChannel.Recurring => "รายการประจำ (recurring)",
        DocumentCreationChannel.Integration => "เชื่อมต่อระบบ/marketplace",
        DocumentCreationChannel.Settlement => "รอบโอนช่องทางขาย",
        DocumentCreationChannel.ExpenseClaim => "เบิกค่าใช้จ่าย/เงินทดรอง",
        DocumentCreationChannel.PlatformBilling => "บิลค่าบริการแพลตฟอร์ม",
        DocumentCreationChannel.SystemJob => "งานอัตโนมัติของระบบ",
        _ => "ไม่ทราบ",
    };

    /// <summary>หมายเหตุความแม่นของช่องทาง — null = หลักฐานตรง</summary>
    public static string? Caveat(DocumentCreationChannel c) => c switch
    {
        DocumentCreationChannel.Recurring => "นับได้เฉพาะใบล่าสุดของแต่ละรายการประจำ — ใบงวดก่อนหน้าอยู่ใน \"งานอัตโนมัติของระบบ\" หรือ \"สร้างจากหน้าจอ\" (กดรันเอง)",
        DocumentCreationChannel.SystemJob => "CreatedBy = System — ส่วนใหญ่คือรายการประจำที่รันตามรอบ",
        DocumentCreationChannel.Unknown => "ไม่มีร่องรอยช่องทาง (เช่น LINE ที่บันทึกเป็นอีเมลผู้ใช้ หรือเส้นที่ไม่ประทับป้าย) — ไม่เดา",
        _ => null,
    };

    /// <summary>กลุ่มสถานะสำหรับรายงาน: Draft · Pending (รออนุมัติ) · Approved (มีเลขจริง เดินต่อแล้ว) · Voided · Rejected</summary>
    public static string StatusBucket(DocumentStatus s) => s switch
    {
        DocumentStatus.Draft => "Draft",
        DocumentStatus.WaitingApproval => "Pending",
        DocumentStatus.Voided => "Voided",
        DocumentStatus.Rejected => "Rejected",
        _ => "Approved",
    };

    public static string StatusBucketLabel(string bucket) => bucket switch
    {
        "Draft" => "ร่าง",
        "Pending" => "รออนุมัติ",
        "Approved" => "อนุมัติแล้ว",
        "Voided" => "ยกเลิก",
        "Rejected" => "ถูกปฏิเสธ",
        _ => bucket,
    };

    /// <summary>ใบนี้นับเข้าโควตาเอกสาร/เดือนไหม — ผ่าน <see cref="DocumentQuotaPolicy.Classify"/> ตัวเดียวกับตอนสร้างเอกสาร ·
    /// ประมาณการย้อนหลัง: ใบกำกับที่ออก "แทน" ใบเสร็จ (ไม่นับซ้ำตอนสร้าง) ไม่มีคอลัมน์บอก ⇒ อาจนับเกินเล็กน้อย</summary>
    public static bool CountsTowardDocumentQuota(DocumentType type, bool isDeposit, string? originModule)
        => DocumentQuotaPolicy.Classify(type, isDeposit, fromLodging: string.Equals(originModule?.Trim(), "Lodging", StringComparison.OrdinalIgnoreCase)) != QuotaEnforcement.NotCounted;
}
