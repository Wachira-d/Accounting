namespace Accounting.Models.DTOs.Cms;

/// <summary>รอบ 211: สถานะ "รับแจ้งเตือนทาง LINE" ของสมาชิก/การจอง/ออเดอร์ — เซิร์ฟเวอร์ตัดสินทุกช่อง หน้าเว็บแสดงอย่างเดียว (F2 #5)</summary>
public record CustomerLineStatusResponse(
    /// <summary>ร้านเปิดให้ผูกได้ (OA มี token + secret + basic id)</summary>
    bool Available,
    /// <summary>เหตุที่ยังผูกไม่ได้ (แสดงตรง ๆ) · null เมื่อ Available</summary>
    string? UnavailableReason,
    bool Linked,
    /// <summary>ผูกไว้แต่ลูกค้าบล็อก OA อยู่ (unfollow) — ส่งไม่ถึงจนกว่าจะเพิ่มเพื่อนกลับ</summary>
    bool Blocked,
    /// <summary>ผูกกับ OA อื่น (ร้านเปลี่ยน OA) — ต้องผูกใหม่</summary>
    bool NeedsRelink,
    string? MaskedLineUserId,
    DateTime? LinkedAt,
    /// <summary>"@xxxx" ของ OA ที่ใช้ตอนนี้</summary>
    string? OaBasicId,
    string? AddFriendUrl,
    /// <summary>ข้อความยินยอม (PDPA ม.19) ที่แสดงข้างปุ่ม</summary>
    string ConsentText,
    /// <summary>ผูกจากระดับไหน: member (บัญชีสมาชิก) / booking / order · null = ยังไม่ผูก</summary>
    string? LinkedVia);

/// <summary>รอบ 211: เริ่มผูก — รหัสใช้ครั้งเดียว + ลิงก์เพิ่มเพื่อน/ส่งรหัส + QR (PNG data URI ของลิงก์ส่งรหัส สำหรับเปิดบนคอมแล้วสแกนด้วยมือถือ)</summary>
public record CustomerLineStartResponse(
    string Code,
    string MessageText,
    string? AddFriendUrl,
    string? SendCodeUrl,
    string? SendCodeQrDataUri,
    string? OaBasicId,
    DateTime ExpiresAt);

/// <summary>รอบ 211: ช่องทางแจ้งลูกค้าของบริษัท (แสดงในหน้าตั้งค่า LINE)</summary>
public record CustomerLineChannelInfo(
    string Scope,
    string? OaBasicId,
    bool CanLink,
    string? Problem,
    /// <summary>URL webhook ที่ร้านต้องใส่ใน LINE Developers (เฉพาะเมื่อใช้ OA ของตัวเอง)</summary>
    string? CompanyWebhookUrl,
    int ActiveLinks,
    /// <summary>ลิงก์ที่ผูกกับ OA อื่น (ร้านเปลี่ยน OA) — ลูกค้าต้องผูกใหม่</summary>
    int StaleLinks);
