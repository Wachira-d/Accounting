namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสิน "งานตรวจ audit chain รอบนี้ตรวจช่วงไหน และขยับ watermark ได้ไหม" — <b>ตัวเดียว</b> ของ <c>AuditChainVerifyJob</c>
/// (รอบ 201 ทีม PL · A-PL3 · team-R H-2)
///
/// <para>═══ ที่มา ═══ เดิมงานรายสัปดาห์โหลด<b>ทุกแถวของทุกบริษัท</b>เข้าหน่วยความจำทุกรอบ — บริษัทใหญ่ (หลายล้านแถว) ทำให้รอบตรวจ
/// ยาวจนข้ามบริษัทที่เหลือ/ล้มทั้งรอบ ⇒ control "ตรวจทุกสัปดาห์" ไม่ได้ตรวจจริง</para>
///
/// <para>กติกา (ทิศที่มองเห็นและย้อนได้):</para>
/// <list type="bullet">
/// <item>ยังไม่มี checkpoint · ครบรอบตรวจเต็ม (<see cref="FullEvery"/>) · รอบก่อนพบปัญหา ⇒ <b>ตรวจเต็ม</b> (แถวเก่าที่ถูกแก้ทีหลังถูกจับได้
/// ภายในรอบตรวจเต็ม ไม่ใช่ไม่มีวัน)</item>
/// <item>นอกนั้น ⇒ ตรวจเฉพาะแถว Id &gt; watermark (หลัง A-PL1 การต่อ chain ของบริษัทหนึ่งถูก serialize ⇒ ลำดับ Id ของบริษัทเดียวกัน
/// = ลำดับ chain) · parent ที่อยู่ก่อน watermark ต้อง<b>มีอยู่จริงในฐาน</b>จึงนับเป็นจุดยึด (ผู้เรียกค้นแล้วส่งมา)</item>
/// <item>พบถูกแก้/ขาดตอน ⇒ <b>ไม่ขยับ watermark</b> + บันทึกจำนวนที่พบ (รอบหน้าตรวจเต็มและแจ้งซ้ำจนกว่าจะมีคนจัดการ — ล้มดัง ไม่เงียบ)</item>
/// <item>fork อย่างเดียว (คำขอพร้อมกันก่อนรอบ 201) ไม่ใช่หลักฐานการแก้ ⇒ ขยับได้</item>
/// </list>
/// </summary>
public static class AuditChainCheckpointPolicy
{
    /// <summary>ตรวจเต็มทุก 28 วัน (งานรันทุก 7 วัน ⇒ ทุกรอบที่ 4)</summary>
    public static readonly TimeSpan FullEvery = TimeSpan.FromDays(28);

    public enum Mode { Full, Incremental }

    /// <summary>แผนรอบนี้: <paramref name="AfterId"/> = ตรวจแถว Id มากกว่าค่านี้ (Full = 0)</summary>
    public sealed record Plan(Mode Mode, long AfterId, string Reason);

    /// <summary>สถานะ checkpoint ที่ต้องบันทึกหลังตรวจ</summary>
    public sealed record Next(long LastVerifiedId, DateTime? LastFullVerifiedAt, int FindingCount, bool Advanced);

    public static Plan Decide(long? checkpointId, DateTime? lastFullAt, int lastFindingCount, DateTime nowUtc)
    {
        if (checkpointId is null or <= 0) return new Plan(Mode.Full, 0, "ยังไม่เคยตรวจผ่าน — ตรวจเต็ม");
        if (lastFindingCount > 0) return new Plan(Mode.Full, 0, "รอบก่อนพบความไม่ตรงกัน — ตรวจเต็มและแจ้งซ้ำ");
        if (lastFullAt is null || nowUtc - lastFullAt.Value >= FullEvery)
            return new Plan(Mode.Full, 0, "ครบรอบตรวจเต็ม (แถวเก่าที่ถูกแก้หลังตรวจ)");
        return new Plan(Mode.Incremental, checkpointId.Value, "ตรวจต่อจาก watermark");
    }

    /// <param name="maxRowId">Id มากสุดของแถวที่ตรวจรอบนี้ (0 = ไม่มีแถวใหม่)</param>
    /// <param name="integrityFindings">จำนวนแถวถูกแก้ + ขาดตอน (fork ไม่นับ)</param>
    public static Next Advance(Plan plan, long? checkpointId, DateTime? lastFullAt, long maxRowId, int integrityFindings, DateTime nowUtc)
    {
        var current = checkpointId ?? 0;
        if (integrityFindings > 0)
            return new Next(current, lastFullAt, integrityFindings, Advanced: false);
        var fullAt = plan.Mode == Mode.Full ? nowUtc : lastFullAt;
        var id = Math.Max(current, maxRowId);
        return new Next(id, fullAt, 0, Advanced: id > current);
    }
}
