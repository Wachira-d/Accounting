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
    string? OaBasicId = null);

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
