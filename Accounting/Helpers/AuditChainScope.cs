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

    // ── ฝ่ายค้านรอบสาม (รอบ 201 · ทีม PL) ──

    /// <summary>P1-1: ธุรกรรมระดับนี้ประทับ audit ตอน commit ได้ไหม — null = ได้ · Serializable/RepeatableRead/Snapshot ⇒ เหตุผล
    /// (PostgreSQL จับ snapshot ตั้งแต่คำสั่งแรก ⇒ อ่านปลาย chain หลังได้ล็อกก็ยังเห็นค่าเก่า ⇒ PrevHash ซ้ำกับธุรกรรมที่ commit ระหว่างนั้น = แตกกิ่ง) ·
    /// Unspecified = ค่าเริ่มต้นของฐาน (ReadCommitted)</summary>
    public static string? IsolationBlockReason(System.Data.IsolationLevel level)
    {
        if (level is System.Data.IsolationLevel.ReadCommitted or System.Data.IsolationLevel.ReadUncommitted or System.Data.IsolationLevel.Unspecified)
            return null;
        return $"ธุรกรรมระดับ {level} ประทับ audit hash chain ไม่ได้ (อ่านปลาย chain จาก snapshot เก่า ⇒ chain แตกกิ่ง) — "
               + "ใช้ ReadCommitted + SELECT … FOR UPDATE บนแถวที่ต้องกันแข่ง (ธุรกรรมนี้ถูกยกเลิกทั้งก้อน ไม่มีอะไรถูกบันทึก)";
    }

    /// <summary>P2-1: แถวต่อคำสั่ง INSERT — 500 × 13 คอลัมน์ = 6,500 พารามิเตอร์ (เพดาน PostgreSQL 65,535)</summary>
    public const int InsertBatchRows = 500;

    /// <summary>P2-1: SQL INSERT หลายแถว (placeholder <c>{n}</c> ของ EF เรียงตามแถวแล้วตามคอลัมน์) — ลำดับแถวใน VALUES = ลำดับที่ส่งเข้า</summary>
    public static string InsertSql(string table, IReadOnlyList<string> columns, int rowCount)
    {
        var cols = string.Join(", ", columns.Select(c => "\"" + c + "\""));
        var rows = Enumerable.Range(0, rowCount)
            .Select(r => "(" + string.Join(", ", Enumerable.Range(0, columns.Count).Select(c => "{" + (r * columns.Count + c) + "}")) + ")");
        return "INSERT INTO \"" + table + "\" (" + cols + ") VALUES " + string.Join(", ", rows);
    }
}
