using Accounting.Models.DTOs.Notification;

namespace Accounting.Services.Interfaces;

/// <summary>สรุปประจำวันทาง LINE ถึงพนักงาน (รอบ 212 C) — ตั้งค่า · ส่งทดสอบ · รอบส่งของงานเบื้องหลัง</summary>
public interface IStaffDailySummaryService
{
    Task<StaffDailySummaryOverviewDto> GetOverviewAsync(Guid companyId, CancellationToken ct = default);
    Task<StaffDailySummaryConfigDto> UpsertAsync(Guid companyId, Guid? configId, UpsertStaffDailySummaryRequest request, string actor, CancellationToken ct = default);
    Task DeleteAsync(Guid companyId, Guid configId, string actor, CancellationToken ct = default);

    /// <summary>ส่งสรุปของชุดนี้ถึง "ฉัน" ตอนนี้ (กรองตามสิทธิ์ของฉัน · นับโควตา 1 · ไม่แตะ watermark/แถวส่งประจำวัน)</summary>
    Task<StaffDailySummaryTestResult> SendTestAsync(Guid companyId, Guid configId, Guid userId, CancellationToken ct = default);

    /// <summary>หนึ่งรอบของงาน — ชุดที่ถึงกำหนด (ผู้เรียกถือ JobLock) · คืนจำนวนข้อความที่ส่งถึง</summary>
    Task<int> RunDueAsync(DateTime nowUtc, CancellationToken ct, Guid? onlyCompanyId = null);
}
