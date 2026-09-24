using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Audit;

namespace Accounting.Services.Interfaces;

public interface IAuditTrailService
{
    Task<PagedResponse<AuditLogResponse>> GetLogsAsync(Guid companyId, AuditLogQueryRequest request);
    Task<AuditSummaryResponse> GetSummaryAsync(Guid companyId, DateTime fromDate, DateTime toDate);
    Task<List<AuditLogResponse>> GetEntityHistoryAsync(Guid companyId, string entityType, string entityId);
    Task<List<AuditLogResponse>> GetUserActivityAsync(Guid companyId, Guid userId, int limit = 100);

    /// <summary>Verify tamper-evident hash chain ของ AuditLog ทั้งบริษัท.
    /// คืน list ของแถวที่ RowHash/PrevHash ไม่ match (= ถูกแก้ไข/แทรก/ลบ
    /// หลัง insert) — list ว่าง = chain ปลอดภัย. ใช้โดย background job
    /// ตรวจรายสัปดาห์ + เรียกตอน RD/DBD audit เพื่อพิสูจน์ตามมาตรฐาน
    /// พ.ร.บ.บัญชี ม.11 ทวิ (เก็บข้อมูลอิเล็กทรอนิกส์).</summary>
    Task<AuditChainVerifyResult> VerifyHashChainAsync(Guid companyId);
}

/// <summary>ผลตรวจ hash chain — <c>IsValid</c> = ไม่มีหลักฐานการแก้/ลบ (fork จากคำขอพร้อมกันไม่นับ — รายงานแยกใน
/// <c>ForkCount</c>) · <c>FirstBroken*</c> = แถวแรก (ตาม Id) ที่ถูกแก้หรือขาดตอน · รายการเต็มอยู่ใน
/// <c>TamperedLogIds</c>/<c>DanglingLogIds</c> · <c>AlertMessage</c> = ข้อความถึงลูกค้าตามสาเหตุที่ตรวจพบ (ฝ่ายค้านรอบ 193 รอบสอง W2-C1)</summary>
public record AuditChainVerifyResult(
    int TotalRows,
    int FirstBrokenRow,
    string? FirstBrokenLogId,
    DateTime? FirstBrokenAt,
    bool IsValid,
    int TamperedCount = 0,
    int DanglingCount = 0,
    int ForkCount = 0,
    IReadOnlyList<string>? TamperedLogIds = null,
    IReadOnlyList<string>? DanglingLogIds = null,
    string? AlertMessage = null);
