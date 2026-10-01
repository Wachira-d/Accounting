using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// บริษัทของแถว audit ก่อนประทับ hash — <b>ตัวตัดสินตัวเดียว</b> (รอบ 201 ทีม PL · ฝ่ายค้าน PL-X1/PL-X6 · คำตัดสินข้อ 104)
///
/// <para>═══ ที่มา ═══ <c>CaptureAuditEntries</c> ให้แถวของ entity ที่ไม่ผูกบริษัท (บรรทัดเอกสาร · บรรทัด JE · ผู้ใช้ · รายการ POS) เป็น
/// <c>Guid.Empty</c> และแถวที่เขียนตรงบางจุด (AuditMiddleware ไม่มีบริษัท) เป็น <c>null</c> ⇒ ล็อก/ปลาย chain ของ "บริษัทว่าง" ตัวเดียวทั้งแพลตฟอร์ม =
/// ทุก tenant ต่อคิวกัน และ <c>null</c> ไม่อยู่ในชุดที่งานตรวจอ่าน (ตัวตรวจกรอง <c>CompanyId != null</c>)</para>
///
/// <para>กติกา: ชุดแถวของการบันทึกครั้งหนึ่งมีบริษัทจริง (≠ ว่าง) <b>บริษัทเดียว</b> ⇒ แถวที่ไม่ผูกบริษัทได้บริษัทนั้น (เป็นลูกของรายการบริษัทนั้นจริง ·
/// ปรากฏในประวัติของบริษัทด้วย) · หลายบริษัทหรือไม่มีเลย ⇒ <c>Guid.Empty</c> (ระดับแพลตฟอร์มจริง — ไม่เดาว่าเป็นของบริษัทไหน) ·
/// <c>null</c> ไม่เหลือหลังผ่านตัวนี้ (ตัวตรวจอ่านได้ทุกแถว) · แถวที่มีบริษัทอยู่แล้วไม่ถูกแตะ</para>
/// </summary>
public static class AuditChainScope
{
    public static void Normalize(IReadOnlyList<AuditLog> rows)
    {
        var tenants = rows.Select(r => r.CompanyId)
            .Where(c => c.HasValue && c.Value != Guid.Empty)
            .Select(c => c!.Value)
            .Distinct()
            .Take(2)
            .ToList();
        var owner = tenants.Count == 1 ? tenants[0] : Guid.Empty;
        foreach (var r in rows)
            if (r.CompanyId is null || r.CompanyId == Guid.Empty)
                r.CompanyId = owner;
    }
}
