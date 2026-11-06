namespace Accounting.Services.Interfaces;

/// <summary>ผล push ที่บอกเหตุ (รอบ 212 C) — <see cref="Error"/> ไม่มี LINE userId เต็ม (ปิดบังแล้ว) · ใช้ประทับบนแถว/ตอบผู้กด ·
/// รอบ 232: <see cref="RuleCode"/> = กติกาของ <c>Helpers/NotificationAudiencePolicy</c> ที่ตัดสินเส้นทาง (null = ไม่ได้ถูกนโยบายกั้น) ·
/// รอบ 254 WP-11: <see cref="HttpStatus"/> = สถานะ HTTP ที่ LINE ตอบ (null = ไม่ได้ยิง/เครือข่ายล้ม) · <see cref="TransportError"/> = เครือข่าย/timeout (ลองใหม่ได้) ·
/// <see cref="LineRequestId"/> = <c>x-line-request-id</c> ของคำขอที่ LINE รับ (หลักฐานการส่ง) — ผู้จำแนกผลตัวเดียว <c>Helpers/LinePushOutcome</c></summary>
public sealed record LinePushResult(bool Ok, string? Error, string? RuleCode = null, int? HttpStatus = null, string? LineRequestId = null, bool TransportError = false)
{
    public static LinePushResult Success { get; } = new(true, null);
    public static LinePushResult Fail(string error) => new(false, error);
    public static LinePushResult Denied(string ruleCode, string reason) => new(false, reason, ruleCode);
}

/// <summary>ช่องส่ง LINE ของระบบ — <b>ทุกเมธอดประกาศผู้รับ + OA ที่ใช้ชัดเจน</b> และเดินผ่าน <c>Helpers/NotificationAudiencePolicy.LineRoute</c> ตัวเดียว (รอบ 232 · คำตัดสิน 232-Notify)
/// <list type="bullet">
/// <item>เรื่องของ<b>ผู้เช่า</b> (TenantBusiness): <see cref="TryPushCompanyGroupAsync"/> · <see cref="TryPushToUserAsync"/> · <see cref="TryPushFlexToUserAsync"/> ใช้ <b>OA ของบริษัทเท่านั้น</b> — ไม่มี OA ⇒ ล้มพร้อมเหตุ
/// <b>ไม่ถอยไป OA กลาง</b> (เดิม <c>ResolveConfigAsync</c> ถอยไป token+GroupId ของ OA กลาง ⇒ "เอกสารอนุมัติแล้ว" ของลูกค้าเด้งเข้าห้องแอดมินแพลตฟอร์ม)</item>
/// <item>1:1 ถึงสมาชิก/พนักงาน/ลูกค้าของบริษัทที่ผูกกับ<b>บอทกลาง</b>เอง: <see cref="TryPushViaNextAccAsync"/> · <see cref="TryPushFlexViaNextAccAsync"/> (userId แยกตาม provider — ช่องส่วนตัวของเขา)</item>
/// <item>เรื่องของ<b>แพลตฟอร์ม</b> (PlatformOps): <see cref="TryPushPlatformOpsAsync"/> เข้ากลุ่ม LINE กลางของผู้ดูแลระบบเท่านั้น</item>
/// </list></summary>
public interface ILineNotifyService
{
    /// <summary>รอบ 232: ข้อความถึง<b>ผู้ดูแลแพลตฟอร์ม</b> (PlatformOps — ฟอร์มติดต่อ/แชทหน้าเว็บ NextAcc · ระบบล้ม) เข้ากลุ่ม LINE กลาง (<c>SiteSettings.LineMessagingGroupId</c>) ·
    /// ปิดได้ที่หน้าแอดมิน (LINE OA กลาง → "ส่งเรื่องของระบบเข้ากลุ่มกลาง") · <b>ห้าม</b>ใช้กับเหตุการณ์ของผู้เช่า</summary>
    Task<LinePushResult> TryPushPlatformOpsAsync(string message);

    /// <summary>รอบ 232: ข้อความธุรกิจของบริษัท (TenantBusiness) เข้า<b>กลุ่ม LINE ของบริษัทนั้น</b> (<c>CompanySettings.LineDefaultGroupId</c>) ผ่าน OA ของบริษัท ·
    /// ไม่มี OA/กลุ่ม ⇒ <c>Fail</c> พร้อมเหตุ + <c>RuleCode</c> (ไม่ถอยไป OA กลาง) · ผู้เรียกหลัก = <c>NotificationEngine</c> ช่อง "กลุ่ม LINE"</summary>
    Task<LinePushResult> TryPushCompanyGroupAsync(Guid companyId, string message);

    /// <summary>1:1 ถึง userId ที่ผูกกับ <b>OA ของบริษัท</b> (ลูกค้าที่ผูกผ่าน webhook ต่อบริษัท · LINE id ที่ HR กรอกเอง) — OA ของบริษัทเท่านั้น ·
    /// รอบ 205 W3: ผู้เรียกที่ต้อง "ประทับหลังส่งถึงจริง" ใช้ผล · รอบ 232: คืน <see cref="LinePushResult"/> (เดิม bool) + ไม่ถอยไป OA กลาง</summary>
    Task<LinePushResult> TryPushToUserAsync(Guid companyId, string lineUserId, string message);

    /// <summary>Flex 1:1 ผ่าน <b>OA ของบริษัท</b> (ส่งเอกสารให้ลูกค้า · สลิปเงินเดือนถึง LINE id ที่ HR กรอก) — คู่ของ <see cref="TryPushToUserAsync"/> ·
    /// รอบ 232: คืนผล (เดิม <c>PushFlexToUserAsync</c> กลืนผลล้ม ⇒ ผู้เรียกรายงาน "ส่งแล้ว" ทั้งที่ LINE ปฏิเสธ) + ไม่ถอยไป OA กลาง</summary>
    Task<LinePushResult> TryPushFlexToUserAsync(Guid companyId, string lineUserId, string altText, object flexContents);

    /// <summary>รอบ 212 C: push ถึง<b>พนักงาน/ผู้ใช้ ERP/ลูกค้า</b>ของบริษัทที่ผูกผ่าน OA กลางของ NextAcc แบบระบุชัด (userId ของ LINE แยกตาม provider) ·
    /// ไม่เลือก OA ตามค่าตั้งบริษัท · ≤ 5 ก้อน × ≤ 5,000 ตัว ในคำขอเดียว · คืนเหตุเมื่อล้ม · รอบ 232: นโยบาย = TenantBusiness/PlatformAccount → TenantMember ผ่าน NextAccOa</summary>
    /// <param name="audience">ผู้รับของเหตุการณ์ตามตาราง <c>NotificationAudiencePolicy</c> — ตั้งต้น TenantBusiness · ใบแจ้งหนี้/เตือนค่าบริการ (<c>PlatformBillingNotifier</c>) = PlatformAccount</param>
    Task<LinePushResult> TryPushViaNextAccAsync(string lineUserId, IReadOnlyList<string> messages,
        Accounting.Helpers.NotificationAudience audience = Accounting.Helpers.NotificationAudience.TenantBusiness);

    /// <summary>รอบ 212 D: Flex ถึง<b>พนักงาน</b>ผ่าน OA กลางแบบระบุชัด (คู่ของ <see cref="TryPushViaNextAccAsync"/>) — สลิปเงินเดือนของพนักงานที่ผูก
    /// "สลิป 123456" กับ OA กลาง · คืนเหตุเมื่อล้ม (ไม่กลืน) · ไม่ตั้ง token กลาง ⇒ ล้มพร้อมเหตุ (ไม่ถอยไป OA ร้าน)</summary>
    Task<LinePushResult> TryPushFlexViaNextAccAsync(string lineUserId, string altText, object flexContents);

    /// <summary>รอบ 254 WP-11 L2 — การ์ด Flex ถึง<b>ปลายทางของผู้ติดต่อ</b> (ผู้ใช้ LINE หรือกลุ่มที่ผูกด้วยรหัส) ผ่าน OA ที่ปลายทางผูกไว้ <b>แบบระบุชัด</b>:
    /// <paramref name="oaScope"/> = <c>company</c> ⇒ OA ของบริษัทเท่านั้น (ไม่มี/ปิด ⇒ ปฏิเสธ — ไม่ถอยไป OA กลาง เพราะ userId ผูกกับ OA) ·
    /// <c>nextacc</c> ⇒ ปฏิเสธ (คำตัดสิน N8 — เปิดพร้อมโควตา LINE แยกช่อง) · ปลายทางเป็นกลุ่มกลางของแอดมิน (ทุกแหล่ง) ⇒ <b>ปฏิเสธเสมอ</b> (คำตอบเจ้าของ Q1(b)) ·
    /// ทุกครั้งส่ง <c>X-Line-Retry-Key</c> = <paramref name="retryKey"/> (ลองใหม่ด้วย key เดิม ⇒ LINE ไม่ส่งซ้ำ)</summary>
    Task<LinePushResult> TryPushContactAsync(Guid companyId, string oaScope, Accounting.Helpers.LineTargetKind target, string to,
        string altText, object flexContents, Guid retryKey);

    /// <summary>ปุ่ม "ทดสอบ" ของหน้าตั้งค่าบริษัท — ใช้ <b>token ของบริษัทเท่านั้น</b> (รอบ 232: เดิมทดสอบด้วย token ของ OA กลางเมื่อบริษัทยังไม่ตั้ง ⇒ "สำเร็จ" ทั้งที่ OA ของร้านยังไม่มี) ·
    /// Returns (success, message) suitable for echoing to the admin UI.</summary>
    Task<(bool ok, string message)> TestConfigAsync(Guid companyId, string toLineId);
}
