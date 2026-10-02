using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลของ "อนุมัติอัตโนมัติไม่สำเร็จ" ที่ทางเข้าหนึ่งบันทึกไว้แล้ว — คืนให้ผู้เรียกใช้ตอบผู้ใช้ด้วยข้อความเดียวกับที่ลงบนเอกสาร</summary>
/// <param name="StillDraft">สถานะจริงจากฐานข้อมูล (ไม่ใช่สถานะใน object ที่อาจค้าง) ยังเป็นร่าง/รออนุมัติ ⇒ ยังไม่ออกเลข ยังไม่ลงบัญชี ·
/// false = อนุมัติ/ลงบัญชีสำเร็จแล้ว แต่ขั้นหลัง commit (แจ้งเตือน · e-Tax · การเรียนรู้) ล้ม — ห้ามบอกผู้ใช้ว่า "ยังเป็นร่าง"</param>
/// <param name="DocumentNumber">เลขเอกสารจริงจากฐานข้อมูล</param>
/// <param name="Message">ข้อความไทยพร้อมทางไปต่อ (ตัวเดียวกับที่ต่อท้ายหมายเหตุภายในของเอกสาร)</param>
public sealed record AutoApproveFailureOutcome(bool StillDraft, string DocumentNumber, string Message);

/// <summary>
/// PP36_REVIEW P0-2 (2026-10-02) — ข้อความ "อนุมัติอัตโนมัติไม่สำเร็จ" ตัวเดียวของทุกทางเข้า (สร้างใบสำคัญจ่ายเงินสด · ลายเซ็นครบ/workflow ·
/// อนุมัติหลายใบ · สแกน · LINE · รายการประจำ · รอบโอน)
/// <para>═══ ที่มา ═══ PV-20260901-0001: อนุมัติอัตโนมัติล้ม (ด่าน JE §83/6) แล้วผู้เรียก <c>LogInformation("staying Draft")</c> — ประโยคนั้น<b>ไม่จริง</b>
/// (ค่าที่ค้างใน context ถูก SaveChanges ถัดไปบันทึกเป็น Paid + เลขเอกสาร) และไม่มีใครเห็นเหตุผล ⇒ หลักการ "ล้มดัง 3 ที่":
/// หมายเหตุบนเอกสาร (ผู้ใช้เปิดดูเห็น) · คำตอบของทางเข้า · log ระดับ Warning ขึ้นไป · และ "สาเหตุ" ที่บอกต้องตรวจจากสถานะจริงในฐานข้อมูล</para>
/// </summary>
public static class AutoApproveFailure
{
    /// <summary>สถานะนี้ = การอนุมัติไม่มีผล (ยังไม่ออกเลข · ไม่มี JE)</summary>
    public static bool IsStillDraft(DocumentStatus status)
        => status is DocumentStatus.Draft or DocumentStatus.WaitingApproval;

    /// <summary>ข้อความเดียวของทุกทางเข้า — <paramref name="channel"/> = ทางเข้าที่สั่งอนุมัติ (ภาษาไทย) · <paramref name="reason"/> = เหตุผลที่ผู้ใช้อ่านได้</summary>
    public static string Message(string channel, string documentNumber, bool stillDraft, string reason)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "ไม่ทราบสาเหตุ (ดูบันทึกระบบ)" : reason.Trim();
        return stillDraft
            ? $"⚠️ อนุมัติอัตโนมัติ ({channel}) ไม่สำเร็จ: {why} — เอกสาร {documentNumber} ยังเป็นร่าง ยังไม่ออกเลข ยังไม่ลงบัญชี · "
              + "แก้ตามเหตุผลแล้วกด “อนุมัติ” ที่หน้าเอกสาร"
            : $"⚠️ เอกสาร {documentNumber} อนุมัติและลงบัญชีแล้ว แต่ขั้นหลังอนุมัติ ({channel}) ล้ม: {why} — ไม่ต้องอนุมัติซ้ำ · "
              + "ตรวจการแจ้งเตือน/e-Tax ของใบนี้";
    }
}
