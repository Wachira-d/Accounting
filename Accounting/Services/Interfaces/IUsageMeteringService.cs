using Accounting.Models.Entities;

namespace Accounting.Services.Interfaces;

/// <summary>คำขอบันทึกการใช้งาน 1 ครั้ง</summary>
/// <param name="CompanyId">บริษัทที่ใช้งานจริง (tenant)</param>
/// <param name="FeatureCode">รหัสฟีเจอร์ใน <see cref="ApiFeature"/></param>
/// <param name="Quantity">จำนวนหน่วย (เอกสาร/บรรทัด) — ต้อง &gt; 0</param>
/// <param name="IdempotencyKey">กันเก็บเงินซ้ำตอน client retry — แนะนำให้ส่งเสมอ
/// สำหรับงานที่มาจาก API</param>
public record UsageRecordRequest(
    Guid CompanyId,
    string FeatureCode,
    int Quantity = 1,
    string? IdempotencyKey = null,
    Guid? BranchId = null,
    Guid? ApiClientId = null,
    string? RefEntityType = null,
    Guid? RefEntityId = null);

/// <summary>ผลการบันทึก — บอกด้วยว่าคิดเงินไปเท่าไรและทำไม</summary>
/// <param name="Recorded">false = ไม่ได้บันทึกใหม่ (ซ้ำ/ฟีเจอร์ปิด/ไม่มีราคา)</param>
/// <param name="Duplicate">true = เจอ IdempotencyKey เดิม คืนแถวเดิม ไม่เก็บเงินซ้ำ</param>
public record UsageRecordResult(
    bool Recorded,
    Guid? UsageEventId,
    decimal ChargedAmount,
    bool CoveredByFreeQuota,
    bool Duplicate,
    string? Reason = null);

/// <summary>
/// บันทึกและคิดเงินการใช้งานรายหน่วย (ACCOUNT_STRUCTURE.md §6)
///
/// **หลักที่ห้ามละเมิด**
///   • append-only — ไม่มี method แก้/ลบ UsageEvent (คิดผิดให้ออกใบลดหนี้)
///   • snapshot ราคาลงแถวตอนเกิด — ขึ้นราคาแล้วบิลเก่าต้องไม่ขยับ
///   • ล้มเหลวต้องไม่ทำให้งานหลักพัง — บันทึกไม่ได้ = log แล้วปล่อยผ่าน
///     (เสียรายได้ 1 รายการ ยอมรับได้ มากกว่าทำให้ลูกค้าสร้างเอกสารไม่ได้)
/// </summary>
public interface IUsageMeteringService
{
    /// <summary>บันทึก 1 การใช้งาน + คิดราคา + ตัดเครดิต (ถ้า Prepaid).
    /// ไม่ throw — ทุกความล้มเหลวคืน Recorded=false พร้อมเหตุผล</summary>
    Task<UsageRecordResult> RecordAsync(UsageRecordRequest request, CancellationToken ct = default);

    /// <summary>ฟีเจอร์นี้เปิดใช้อยู่ไหมสำหรับบริษัทนี้ — endpoint เรียกก่อนทำงาน
    /// (ปิดอยู่ = 403 และต้องไม่เกิด UsageEvent)</summary>
    Task<bool> IsFeatureEnabledAsync(Guid companyId, string featureCode, CancellationToken ct = default);

    /// <summary>เปิด/ปิดฟีเจอร์ (ลูกค้ากดเองใน portal) — บันทึกว่าใครกดตอนไหน
    /// และเห็นราคาเท่าไร</summary>
    Task SetFeatureEnabledAsync(Guid companyId, string featureCode, bool enabled,
        string actor, CancellationToken ct = default);

    /// <summary>สรุปการใช้งานของบริษัทในเดือนที่ระบุ (สำหรับหน้า usage)</summary>
    Task<List<UsageSummaryRow>> GetMonthlyUsageAsync(Guid companyId, int year, int month,
        CancellationToken ct = default);

    /// <summary>สรุประดับกลุ่ม — ทุกบริษัทใต้ BillingAccount รวมกัน</summary>
    Task<List<UsageSummaryRow>> GetAccountMonthlyUsageAsync(Guid billingAccountId, int year, int month,
        CancellationToken ct = default);
}

public record UsageSummaryRow(
    Guid CompanyId,
    string CompanyName,
    string FeatureCode,
    string FeatureName,
    string UnitLabel,
    int TotalQuantity,
    int FreeQuantity,
    decimal TotalCharged);
