using Accounting.Models.DTOs.Import;

namespace Accounting.Services.Import;

/// <summary>
/// **กระทบยอดหลังย้ายระบบ** (ขั้นสุดท้ายของลำดับ §2.10) — รอบ 205 T0
///
/// <para>เจ้าของ implementation: <b>ทีม D</b> (<see cref="OpeningBalanceReconciliation"/>) · AR/AP = <c>SubLedgerReconciliationService.ReconcileAsync</c>
/// (ห้ามคำนวณซ้ำ) · มูลค่าสต็อก vs บัญชีสินค้า · ราคาทุน/ค่าเสื่อมสะสมของทะเบียนสินทรัพย์ vs งบทดลอง · อ่านอย่างเดียว ·
/// ไม่ลงตัว = บรรทัดพร้อม Hint ทางไปต่อ (ไม่บล็อก — ผู้ใช้ตัดสิน · คำตัดสินข้อ 150)</para>
/// </summary>
public interface IOpeningBalanceReconciliation
{
    Task<ImportReconciliationReportDto> ReconcileAsync(Guid companyId, DateTime asOf, CancellationToken ct = default);
}
