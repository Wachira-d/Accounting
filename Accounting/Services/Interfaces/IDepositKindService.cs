using Accounting.Models.DTOs.Settings;

namespace Accounting.Services.Interfaces;

/// <summary>ประเภทเงินมัดจำต่อบริษัท (รอบ 194 · spec S1/S2/S8) — ตั้งค่าที่หน้า ตั้งค่า → ภาษี · ฟอร์มเอกสาร/integration อ่านรายการนี้</summary>
public interface IDepositKindService
{
    /// <summary>รายการทั้งหมดของบริษัท (รวมที่ปิดใช้) + ประเภทเริ่มต้น + ตัวเลือก/ตาราง ลักษณะ×โหมด · บริษัทที่ยังไม่มีแถว (เกิดก่อน migration)
    /// ถูก seed ให้ก่อน (<c>DepositKindSeed.EnsureSeededAsync</c>)</summary>
    Task<DepositKindListResponse> ListAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>สร้างประเภทใหม่ — ด่าน <c>DepositPolicyResolver.KindProblem</c> (ปฏิเสธ = BusinessRuleException ข้อความไทย) · คืนคำเตือนของคู่ลักษณะ×โหมด</summary>
    Task<DepositKindSaveResponse> CreateAsync(Guid companyId, SaveDepositKindRequest request, string userId, CancellationToken ct = default);

    /// <summary>แก้ประเภท (แทนค่าทั้งก้อน) — ใบที่ออกแล้วไม่เปลี่ยน (ตรึงสำเนาลงใบตอนออก)</summary>
    Task<DepositKindSaveResponse> UpdateAsync(Guid companyId, Guid id, SaveDepositKindRequest request, string userId, CancellationToken ct = default);

    /// <summary>ลบ (soft) — ประเภทที่มีใบ/ที่พักอ้างอยู่ห้ามลบ ⇒ ปิดใช้แทน</summary>
    Task<DepositKindDeleteResponse> DeleteAsync(Guid companyId, Guid id, string userId, CancellationToken ct = default);

    /// <summary>ตั้งเป็นประเภทเริ่มต้น (มีได้ตัวเดียวต่อบริษัท — unique index) · ประเภทที่ปิดใช้ตั้งไม่ได้</summary>
    Task<DepositKindSaveResponse> SetDefaultAsync(Guid companyId, Guid id, string userId, CancellationToken ct = default);
}
