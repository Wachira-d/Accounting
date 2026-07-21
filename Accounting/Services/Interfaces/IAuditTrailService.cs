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

public record AuditChainVerifyResult(
    int TotalRows,
    int FirstBrokenRow,
    string? FirstBrokenLogId,
    DateTime? FirstBrokenAt,
    bool IsValid);
