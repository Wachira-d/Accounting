namespace Accounting.Helpers;

/// <summary>URL ของไฟล์สื่อ CMS + สลิปออเดอร์ — <b>ตัวตัดสินตัวเดียว</b>ว่าไฟล์ "รูปเก่า" (เขียน disk ตรง) กับ "รูปใหม่" (ผ่าน
/// <c>IFileAttachmentService</c>) เปิดจากที่ไหน (OWNER file · R-43)
///
/// ที่มา: เดิม <c>CmsContentService.UploadMediaAsync</c> เขียนลง <c>uploads/cms/{companyId}/{siteId}</c> และ
/// <c>CmsCommerceService.RecordPaymentSlipAsync</c> เขียนลง <c>wwwroot/uploads/order-slips</c> ด้วย <c>Directory.GetCurrentDirectory()</c>
/// (กฎเหล็ก #4 D "ไฟล์ห้ามเขียน local disk ตรง") · ตอนนี้ทั้งคู่ไปที่เก็บไฟล์กลาง (<c>EntityType</c> = <see cref="SiteMediaEntityType"/> /
/// <see cref="OrderEntityType"/>) · <b>ไฟล์เดิมไม่ถูกย้าย</b> (F2 #9) — แถว <c>SiteMedia</c> ที่ <c>AttachmentId == null</c> ยังเปิดจาก URL เดิมได้
///
/// สื่อของเว็บ = สาธารณะ (อยู่บนหน้าเว็บอยู่แล้ว) ⇒ เสิร์ฟผ่าน endpoint storefront ที่ตรวจ "ไฟล์นี้เป็นสื่อของเว็บนี้ของบริษัทนี้" ·
/// สลิปออเดอร์ = มีชื่อผู้โอน/เลขบัญชี/ยอด (PII) ⇒ เปิดได้เฉพาะพนักงานผ่าน <c>attachments/{id}/download</c> ที่มีด่าน (<c>SiteOrder</c> →
/// <c>CmsOrderManage</c>) — เหมือนคำตัดสินสลิปที่พัก LDG-P2-06 · เดิมเป็น URL สาธารณะที่ใครได้ก็เปิดได้ตลอดกาล</summary>
public static class CmsMediaUrl
{
    public const string SiteMediaEntityType = "SiteMedia";
    public const string OrderEntityType = "SiteOrder";

    /// <summary>URL เดิมของสื่อที่เขียน disk ตรง (static allow-list <c>/uploads/cms</c>) — ใช้กับแถวที่ยังไม่มี <c>AttachmentId</c></summary>
    internal static string LegacyMediaUrl(Guid companyId, Guid siteId, string? fileName) =>
        $"/uploads/cms/{companyId}/{siteId}/{Uri.EscapeDataString(fileName ?? "")}";

    /// <summary>URL ของสื่อที่อยู่ในที่เก็บไฟล์กลาง — endpoint สาธารณะของ storefront (ตรวจ companyId + siteId + ชนิด)</summary>
    internal static string AttachmentMediaUrl(Guid companyId, Guid siteId, Guid attachmentId) =>
        $"/api/companies/{companyId}/cms/sites/{siteId}/storefront/media/{attachmentId}";

    /// <summary>URL สาธารณะของแถว <c>SiteMedia</c> — มี <paramref name="attachmentId"/> ⇒ ที่เก็บกลาง · ไม่มี ⇒ ตำแหน่งเดิม</summary>
    public static string PublicUrl(Guid companyId, Guid siteId, string? fileName, Guid? attachmentId) =>
        attachmentId is Guid id && id != Guid.Empty
            ? AttachmentMediaUrl(companyId, siteId, id)
            : LegacyMediaUrl(companyId, siteId, fileName);

    /// <summary>URL ของสลิปออเดอร์ (ต้องล็อกอิน + ด่าน CmsOrderManage) — หน้า cms-orders ดึงผ่าน <c>API.fetchFile</c> (แนบ JWT)</summary>
    public static string SlipDownloadUrl(Guid companyId, Guid attachmentId) =>
        $"/api/companies/{companyId}/attachments/{attachmentId}/download";

    /// <summary>สลิปเดิมที่เขียน disk ตรงเป็น URL สาธารณะ <c>/uploads/order-slips/…</c> — ยังเปิดได้จาก static (ไม่ย้ายไฟล์เงียบ ๆ) ·
    /// ตัวนี้บอกหน้าเว็บว่า URL นี้ต้องแนบ JWT ไหม</summary>
    public static bool NeedsAuth(string? slipUrl) =>
        !string.IsNullOrEmpty(slipUrl) && slipUrl.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
}
