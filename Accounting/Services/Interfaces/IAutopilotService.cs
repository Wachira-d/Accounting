using Accounting.Helpers;
using Accounting.Models.DTOs.Autopilot;

namespace Accounting.Services.Interfaces;

/// <summary>รอบ 252 WP-12A — หน้า Autopilot (read model จากแถวจริง) + ค่าตั้ง + สรุปประจำวัน · ผู้ทำ: <c>AutopilotService</c></summary>
public interface IAutopilotService
{
    /// <summary>ภาพรวม N วันล่าสุด: แถวเอกสาร (ขั้นที่อยู่) · เรื่องที่ระบบทำต่อไม่ได้ · ใบเสนอราคา · คิวเงินเข้า · ค่าตั้ง</summary>
    Task<AutopilotOverviewResponse> OverviewAsync(Guid companyId, int days, CancellationToken ct = default);
    /// <summary>เรื่องที่ระบบทำต่อไม่ได้ (union จากแถวสถานะจริง)</summary>
    Task<IReadOnlyList<AutopilotIssueDto>> IssuesAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default);
    Task<AutopilotSettingsDto> SettingsAsync(Guid companyId, CancellationToken ct = default);
    Task<AutopilotSettingsDto> UpdateSettingsAsync(Guid companyId, UpdateAutopilotSettingsRequest request, Guid userId, CancellationToken ct = default);
    /// <summary>ตั้งโหมดแปลงของใบเสนอราคาหนึ่งใบ (null/"" = ตามค่าบริษัท) — ล้างผลที่หยุด/ล้มไว้ (ให้ตัดสินใหม่)</summary>
    Task<QuotationAutopilotRowDto> SetQuotationModeAsync(Guid companyId, Guid quotationId, string? mode, Guid userId, CancellationToken ct = default);
    /// <summary>ลองแปลงใหม่ (ล้างผล Held/Failed) — งานรอบถัดไปตัดสินอีกครั้ง</summary>
    Task RetryQuotationAsync(Guid companyId, Guid quotationId, Guid userId, CancellationToken ct = default);
    /// <summary>ผู้ติดต่อรายนี้: ไม่ปิดหนี้อัตโนมัติจากเงินโอน (ยังเสนอให้คนยืนยันได้) — audit</summary>
    Task SetContactBankAutoSettleAsync(Guid companyId, Guid contactId, bool disabled, Guid userId, CancellationToken ct = default);
    /// <summary>ตัวเลขของสรุปประจำวันในช่วง [from, to)</summary>
    Task<AutopilotNotice.DigestFacts> DigestFactsAsync(Guid companyId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}
