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
    string? WebhookProblem = null);

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
    /// <summary>Channel Access Token (long-lived) จาก LINE Developers
    /// Console. Empty/null = ไม่แก้ค่าเดิม. ถ้าต้องการล้างให้ส่ง "" + Enabled=false.</summary>
    string? ChannelAccessToken,
    /// <summary>Channel Secret — ใช้ตรวจ webhook signature.</summary>
    string? ChannelSecret,
    /// <summary>LINE Group/Room ID สำหรับส่งแจ้งเตือนกลุ่ม (optional).</summary>
    string? DefaultGroupId,
    /// <summary>LINE OA basic id (@xxx) — สำหรับปุ่ม/QR เพิ่มเพื่อนให้พนักงานผูก LINE รับสลิป.</summary>
    string? OaBasicId = null);

public record TestLineConfigRequest(string ToLineId);

public record LineTestResult(bool Success, string? Message);
