namespace Accounting.Models.DTOs.Notification;

/// <summary>รอบ 245 L1 — ตัวเลือกเรื่องที่รับ (ชื่อ enum + ป้ายไทยจากเซิร์ฟเวอร์) + เหตุที่ปลายทาง LINE แต่ละชนิดติ๊กไม่ได้
/// (รอบ 267 P3: ลิงก์เซ็น = 1:1 ได้ · กลุ่มไม่ได้ — 267-1-7) · หน้าเว็บล็อกช่องตามชนิดของปลายทาง (ไม่มีสำเนากติกาในหน้า)</summary>
/// <param name="LineUserUnsupported">เรื่องนี้ส่งถึง LINE 1:1 ไม่ได้ ⇒ เหตุ · null = ได้</param>
/// <param name="LineGroupUnsupported">เรื่องนี้ส่งเข้ากลุ่ม LINE ไม่ได้ ⇒ เหตุ · null = ได้</param>
public record NotifyTopicOption(string Key, string Label, string? LineUserUnsupported = null, string? LineGroupUnsupported = null);

/// <summary>ปลายทางหนึ่งแถว — ทุกป้าย/สถานะ/สิทธิ์กดปุ่มคำนวณที่เซิร์ฟเวอร์ (หน้าแสดงอย่างเดียว) · <paramref name="Id"/> null = อีเมลหลักที่ยังไม่มีแถวค่าตั้ง (รับทุกเรื่อง) ·
/// ไม่มี LINE id ใน DTO (แม้แบบปิดบัง — เจ้าหน้าที่ไม่ต้องใช้)</summary>
public record ContactNotifyEndpointResponse(
    Guid? Id,
    string Kind,
    string KindLabel,
    string? Label,
    string? Email,
    string Display,
    bool IsPrimary,
    List<string> Topics,
    List<string> TopicLabels,
    bool ShowAmounts,
    bool ShowAmountsApplies,
    bool Usable,
    string StatusCode,
    string StatusLabel,
    string? StatusReason,
    string ConsentLabel,
    string Source,
    string SourceLabel,
    string? OaLabel,
    string? GroupName,
    int? MemberCount,
    DateTime? LinkedAt,
    DateTime? UnlinkedAt,
    DateTime? LastSentAt,
    string? LastFailure,
    bool CanEditTopics,
    bool CanEditLabel,
    bool CanUnlink);

/// <summary>แท็บ "ช่องทางแจ้งเตือน" ของผู้ติดต่อ — <paramref name="ComingSoon"/> = ช่องทางที่ยังไม่เปิดพร้อมเหตุ (หน้าแสดงเป็นปุ่มปิด)</summary>
public record ContactNotifyOverviewResponse(
    Guid ContactId,
    string ContactName,
    string? PrimaryEmail,
    List<ContactNotifyEndpointResponse> Endpoints,
    List<NotifyTopicOption> Topics,
    bool LineDeliveryOpen,
    List<ContactNotifyComingSoon> ComingSoon,
    string Note,
    bool CanEdit,
    string? ReadOnlyReason,
    // รอบ 251 (WP-07 · F-06): บริษัทใช้ e-Tax by Email ⇒ คำเตือนเรื่องผู้รับใบกำกับ 1 อีเมล (Helpers/EtaxByEmailFormat.ContactRecipientsNote) · null = ไม่ใช่โหมดนี้
    string? EtaxByEmailNote = null,
    // รอบ 254 WP-11 L2: ปุ่ม "ลิงก์/QR ผูก LINE" (ผ่าน OA ของบริษัท · ผู้รับยินยอมบนมือถือ — Q6) · null = ไม่แสดง
    ContactLineBindOffer? LineBind = null,
    // รอบ 256 (คำตัดสิน 256-2): กล่อง "เพิ่มกลุ่ม LINE" แยกจากลิงก์ผูก 1:1 (บริษัทผู้ให้บริการผูกกลุ่มผ่าน OA กลางได้ แต่ลิงก์ 1:1 ยังผ่าน OA บริษัทเท่านั้น) · null = ไม่แสดง
    ContactLineGroupBindOffer? LineGroupBind = null,
    // รอบ 256-2 (ฝ่ายค้าน L2): ผลข้างเคียงของคำสั่งล่าสุดที่ผู้ใช้ต้องทำต่อเอง (เช่น บอท OA กลางยังอยู่ในกลุ่ม) · null = ไม่มี
    string? ActionNote = null);

public record ContactNotifyComingSoon(string Key, string Label, string Reason);

/// <summary>รอบ 254 WP-11 L2 — <paramref name="Available"/> false ⇒ ปุ่มปิดพร้อม <paramref name="Reason"/></summary>
public record ContactLineBindOffer(bool Available, string? Reason, int OpenInvites = 0, bool ViaCentralOa = false);   // รอบ 262: บริษัทผู้ให้บริการผูกผ่าน OA กลาง

/// <summary>รอบ 256 (คำตัดสิน 256-2) — กล่องผูกกลุ่มด้วยรหัส #HeyNextAcc · <paramref name="Instruction"/> = ขั้นตอน (บอกว่าเชิญ OA ตัวไหน) ·
/// <paramref name="ViaCentralOa"/> = ผ่าน LINE OA กลาง (บริษัทผู้ให้บริการเท่านั้น — หน้าไม่ชี้ไปตั้ง OA ของบริษัท) · <paramref name="Available"/> false ⇒ ล็อกพร้อม <paramref name="Reason"/></summary>
public record ContactLineGroupBindOffer(bool Available, string? Reason, string Instruction, bool ViaCentralOa);

/// <summary>รอบ 254 WP-11 L2 — ลิงก์ให้ผู้รับเปิดบนมือถือ (หน้า <c>contact-line-join.html</c>) · token แสดงครั้งเดียว (เก็บเฉพาะ hash) · หมดอายุ <paramref name="ExpiresAt"/> ·
/// <paramref name="Url"/>/<paramref name="QrDataUri"/> null = ยังไม่ได้ตั้ง URL ของระบบ (หน้าเว็บประกอบจาก <paramref name="Path"/> + โดเมนที่เปิดอยู่ และบอกให้ตั้ง)</summary>
public record ContactLineInviteResponse(string Path, string? Url, string? QrDataUri, DateTime ExpiresAt, string Note);

/// <summary>หน้าเชิญ (public) — ข้อความยินยอม + ชื่อบริษัท · <paramref name="Problem"/> มีค่า = ลิงก์ใช้ไม่ได้ (หมดอายุ/ใช้แล้ว/บริษัทเปลี่ยน OA)</summary>
public record ContactLineJoinView(string? CompanyName, string? ContactName, string? ConsentText, string? ConsentVersion, string? Problem);

/// <summary>ผลกดยินยอม — รหัสผูก + ปุ่มเพิ่มเพื่อน/ส่งรหัส (เปิดแชต OA ของบริษัทพร้อมข้อความ) · ไม่มี basic id ⇒ แสดงรหัสให้พิมพ์เอง</summary>
public record ContactLineJoinConsentResponse(string MessageText, string? AddFriendUrl, string? SendCodeUrl, DateTime ExpiresAt);

/// <summary>ผู้รับของเรื่องหนึ่ง (ตัวตัดสินเดียว <c>ContactRecipientRule.Resolve</c>)</summary>
public record ContactRecipientsResponse(string Topic, string TopicLabel, List<string> Emails, List<ContactRecipientOptionResponse> Options);

public record ContactRecipientOptionResponse(Guid? EndpointId, string Kind, string KindLabel, string Display, bool Available, string? Reason, string? RuleCode, bool IsPrimary);

/// <summary>เพิ่มอีเมล — <paramref name="Topics"/> null = ค่าตั้งต้นของชนิด (ทุกเรื่อง)</summary>
public record AddContactNotifyEmailRequest(string? Email, string? Label, List<string>? Topics);

/// <summary>แก้ปลายทาง — null = ไม่แก้ช่องนั้น · <paramref name="Label"/> "" = ล้างป้าย · <paramref name="ShowAmounts"/> ใช้ได้กับกลุ่ม LINE เท่านั้น (ชนิดอื่น = ปฏิเสธ ไม่ใช่ไม่มีผลเงียบ)</summary>
public record UpdateContactNotifyEndpointRequest(string? Label, List<string>? Topics, bool? ShowAmounts);

/// <summary>เรื่องที่อีเมลหลัก (<c>Contact.Email</c>) รับ — ว่างได้ (= อีเมลหลักไม่รับเรื่องใด · ให้ปลายทางอื่นรับแทน)</summary>
public record UpdateContactNotifyPrimaryRequest(List<string>? Topics);

/// <summary>รอบ 254 WP-11 L3 — ตรวจรหัสผูกกลุ่ม: <paramref name="Problem"/> มีค่า = ผูกไม่ได้ (ข้อความถึงผู้ใช้) · ข้อความรับรองที่ต้องติ๊ก (เวอร์ชัน = hash)</summary>
public record ContactLineGroupPreview(string? GroupName, int? MemberCount, string ContactName, string? AttestText, string? AttestVersion, string? Problem);

/// <summary>รอบ 254 WP-11 L3 — ผูกกลุ่ม · <paramref name="ShowAmounts"/> null = แสดงยอด (Q5 ค่าตั้งต้น) · <paramref name="Topics"/> null = เอกสาร+ทวงชำระ</summary>
public record ConfirmContactLineGroupRequest(string? Code, List<string>? Topics, bool? ShowAmounts, bool AcceptResponsibility);

