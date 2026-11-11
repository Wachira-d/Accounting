using Accounting.Models.DTOs.Cms;

namespace Accounting.Models.DTOs.Line;

public record LineConfigResponse(
    bool Enabled,
    bool HasAccessToken,
    bool HasChannelSecret,
    string? DefaultGroupId,
    bool Configured,
    DateTime? LastTestedAt,
    string? LastTestStatus,
    /// <summary>LINE OA basic id (@xxx) — ใช้สร้างลิงก์เพิ่มเพื่อนให้พนักงานผูก LINE รับสลิป.</summary>
    string? OaBasicId = null,
    /// <summary>รอบ 211: ช่องทางแจ้ง<b>ลูกค้าปลายทาง</b> (OA ไหน · ลูกค้าผูกได้ไหม · URL webhook ต่อบริษัท · จำนวนลิงก์) — เซิร์ฟเวอร์ตัดสิน</summary>
    CustomerLineChannelInfo? CustomerChannel = null,
    /// <summary>รอบ 212 แพ็ก B: ความคืบหน้าคำเชิญผูก LINE ใหม่หลังเปลี่ยน OA (เซิร์ฟเวอร์ประกอบป้าย "เชิญผูกใหม่: ส่งแล้ว N/M") · null = ไม่มีการเปลี่ยน OA ล่าสุด</summary>
    CustomerRelinkProgress? RelinkInvite = null,
    /// <summary>รอบ 212 ฝ่ายค้าน (B+D) ข้อ 2: webhook มาจากบอทอื่นที่ไม่ใช่บอทของ token (secret ของ OA อื่น) — ข้อความจากเซิร์ฟเวอร์ · null = ไม่มีปัญหา</summary>
    string? WebhookProblem = null,
    /// <summary>ฝ่ายค้านรอบ 231 S1: คำใบ้ (ไม่ใช่ข้อผิดพลาด) เมื่อ token/secret ที่เก็บไว้ "ดูเหมือน ciphertext รุ่นก่อนแต่ถอดไม่ออก" — ระบบใช้เป็นค่าดิบ · null = ไม่มี</summary>
    string? SecretHint = null,
    /// <summary>รอบ 232 (232-Notify): ป้ายสถานะ "แจ้งเตือนธุรกิจของบริษัททาง LINE" จากตัวตัดสินเดียว (<c>NotificationAudiencePolicy.CompanyGroupNotice</c>) —
    /// ไม่มี OA ของร้าน ⇒ บอกว่าไปกระดิ่ง/อีเมลแทน และ<b>ไม่</b>ส่งผ่าน OA กลาง · หน้าเว็บแสดงอย่างเดียว</summary>
    string? TenantLineNotice = null,
    /// <summary>รอบ 257 C-H1: บริษัทนี้คือบริษัทผู้ให้บริการ (<c>SiteSettings.PlatformCompanyId</c>) — หน้าเว็บซ่อนส่วน "ใช้ LINE OA ของร้านเอง"</summary>
    bool IsPlatformCompany = false,
    /// <summary>รอบ 257 C-H1: คำอธิบายของบริษัทผู้ให้บริการ (ใช้ OA กลางอยู่แล้ว · ห้ามเปลี่ยน webhook ของ OA กลาง) จาก <c>LineOwnOaGuard.PlatformNotice</c> · บริษัทอื่น = null</summary>
    string? PlatformOaNotice = null,
    /// <summary>รอบ 257 ฝ่ายค้าน M1: บริษัทผู้ให้บริการยังมี token/secret ค้าง — หน้าแสดงสถานะ + ปุ่มล้าง (ส่วน OA ของร้านถูกซ่อน) · null = ไม่มี</summary>
    string? PlatformLegacyOwnOaNotice = null,
    /// <summary>รอบ 259 (A-M5 · A-L3): แถวความพร้อมของ OA บริษัท (OA · webhook เข้าจริงไหม · Basic ID · ผลทดสอบ) จาก <c>LineOaHealth.Rows</c> — หน้าแสดงตรงตัว</summary>
    IReadOnlyList<Accounting.Helpers.LineOaHealth.Row>? Health = null,
    /// <summary>รอบ 259 (A-M6): ปลายทางทดสอบที่ระบบรู้จัก (กลุ่ม LINE ของทีมที่ตั้งไว้) — เลือกแทนการพิมพ์ U…/C… · ไม่มี LINE id ใน DTO (ส่งกลับเป็น key)</summary>
    IReadOnlyList<LineTestTarget>? TestTargets = null);

/// <summary>รอบ 259 (A-M6): ปลายทางทดสอบหนึ่งตัว — <paramref name="Key"/> = <c>LineOaHealth.TeamGroupTargetKey</c> (เซิร์ฟเวอร์อ่าน id เอง)</summary>
public record LineTestTarget(string Key, string Label);

/// <summary>รอบ 212 แพ็ก B: สถานะคำเชิญผูกใหม่ของการเปลี่ยน OA ล่าสุด — <c>Label</c> = ข้อความที่หน้าเว็บแสดงตรง ๆ (ไม่มีกติกาฝั่ง JS)</summary>
public record CustomerRelinkProgress(
    Guid SwitchId,
    string? FromScope,
    string? ToScope,
    DateTime DetectedAt,
    DateTime? ProcessedAt,
    int Total,
    int Invited,
    int Failed,
    int Skipped,
    bool Done,
    string Label,
    string? LastError,
    /// <summary>การเปลี่ยน OA ที่ยังทยอยส่งอยู่ (รวมแถวนี้)</summary>
    int PendingSwitches);

public record UpdateLineConfigRequest(
    bool Enabled,
    /// <summary>Channel Access Token (long-lived) จาก LINE Developers Console · รอบ 257 C-M1: null = ไม่แก้ค่าเดิม · "" = ล้าง · ค่า = ตั้งใหม่</summary>
    string? ChannelAccessToken,
    /// <summary>Channel Secret — ใช้ตรวจ webhook signature · null = ไม่แก้ · "" = ล้าง</summary>
    string? ChannelSecret,
    /// <summary>LINE Group/Room ID สำหรับส่งแจ้งเตือนกลุ่ม (optional) · null = ไม่แก้ · "" = ล้าง</summary>
    string? DefaultGroupId,
    /// <summary>LINE OA basic id (@xxx) — สำหรับปุ่ม/QR เพิ่มเพื่อนให้พนักงานผูก LINE รับสลิป · null = ไม่แก้ · "" = ล้าง</summary>
    string? OaBasicId = null);

/// <param name="TargetKey">รอบ 259 (A-M6): ปลายทางที่ระบบรู้จัก (<c>LineConfigResponse.TestTargets</c>) — มีค่า ⇒ ใช้แทน <paramref name="ToLineId"/></param>
public record TestLineConfigRequest(string? ToLineId, string? TargetKey = null);

public record LineTestResult(bool Success, string? Message);
