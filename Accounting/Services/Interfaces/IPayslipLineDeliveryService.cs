namespace Accounting.Services.Interfaces;

/// <summary>ส่งสลิปเงินเดือนให้พนักงานรายบุคคลผ่าน LINE — flex card (ซ่อนยอด)
/// + ปุ่มดาวน์โหลดผ่าน secure token link (ไม่ต้องล็อกอิน, หมดอายุ, log การเข้าถึง
/// ตาม PDPA ม.37). พนักงานผูก LINE ผ่าน LINE OA บริษัทด้วยรหัส 6 หลัก (self-service).</summary>
public interface IPayslipLineDeliveryService
{
    /// <summary>สร้างรหัสผูก LINE 6 หลักให้พนักงานคนหนึ่ง (ยกเลิกรหัสเก่าที่ยังไม่ใช้).
    /// คืน (code, addFriendUrl?) — addFriendUrl มีเมื่อบริษัทตั้ง LineOaBasicId.</summary>
    Task<(string Code, string? AddFriendUrl)> IssueBindCodeAsync(Guid companyId, Guid employeeId, string? actorEmail);

    /// <summary>สถานะผูก LINE ของพนักงาน — bound + lineUserId (mask).</summary>
    Task<(bool Bound, string? MaskedLineUserId)> GetLineStatusAsync(Guid companyId, Guid employeeId);

    /// <summary>เรียกจาก LINE bot เมื่อพนักงานส่ง "สลิป {รหัส}" — จับคู่รหัสผูก
    /// แล้วเขียน Employee.LineUserId. คืนข้อความตอบกลับ หรือ null ถ้าไม่ match.</summary>
    Task<string?> TryBindFromLineAsync(string lineUserId, string code);

    /// <summary>ส่งสลิปงวดหนึ่งให้พนักงานคนเดียวทาง LINE.</summary>
    Task<PayslipLineSendResult> SendPayslipAsync(Guid companyId, Guid payrollRunId, Guid employeeId,
        string? actorEmail, CancellationToken ct = default);

    /// <summary>ส่งสลิปทั้งงวดให้พนักงานทุกคนที่ผูก LINE แล้ว — คืนสรุปจำนวน.</summary>
    Task<PayslipLineBulkResult> SendPayslipForRunAsync(Guid companyId, Guid payrollRunId,
        string? actorEmail, CancellationToken ct = default);

    /// <summary>Resolve public download token → PDF สลิป (validate + increment +
    /// log PdpaPiiAccessLog). คืน null ถ้า token ไม่ถูกต้อง/หมดอายุ/ถูกเพิกถอน.</summary>
    Task<(byte[] Pdf, string FileName)?> ResolvePublicPayslipAsync(string token,
        string? ipAddress, string? userAgent, CancellationToken ct = default);
}

/// <summary>ผลการส่งสลิปทาง LINE รายคน.</summary>
public enum PayslipLineSendResult
{
    Sent,                // ส่งสำเร็จ
    NotBound,            // พนักงานยังไม่ผูก LINE
    NoPayrollDetail,     // ไม่พบรายละเอียดเงินเดือนในงวดนี้
    LineNotConfigured,   // บริษัทยังไม่เปิด/ตั้งค่า LINE Messaging API
    LineFailed,          // LINE API ตอบไม่สำเร็จ
}

/// <summary>สรุปการส่งสลิปทั้งงวด.</summary>
public record PayslipLineBulkResult(int Total, int Sent, int NotBound, int Failed);
