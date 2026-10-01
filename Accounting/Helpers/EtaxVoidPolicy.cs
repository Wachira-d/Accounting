using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ยกเลิกแถว e-Tax ในระบบนี้ได้ไหม — ตัวตัดสินตัวเดียวของ <c>EtaxInvoiceService.VoidAsync</c>** (รอบ 200 ทีม V1H · คำตัดสินข้อ 51)
/// <para>ระบบนี้<b>ไม่ได้ส่งคำยกเลิกถึงกรมสรรพากร</b> — การยกเลิกที่นี่คือการบันทึกในฐานเราเท่านั้น (R1 ใน DECISION_AUDIT: ห้ามประทับสถานะปลายทางเอง) ⇒</para>
/// <list type="bullet">
/// <item>ยังไม่ถึงกรมสรรพากร (สร้าง/เซ็น/ผิดพลาด/ถูกปฏิเสธ) ⇒ ยกเลิกได้เหมือนเดิม (ไม่มีอะไรอยู่ที่กรมสรรพากร)</item>
/// <item><b>ส่งแล้ว (Submitted)</b> ⇒ เอกสารอยู่ที่กรมสรรพากร/ผู้ให้บริการแล้ว — ยกเลิกในระบบนี้ได้เมื่อ<b>แนบไฟล์หลักฐานการยกเลิกจากฝั่งกรมสรรพากร/ผู้ให้บริการ</b>
/// (ผ่านด่านไฟล์แนบตัวเดียว <c>IAttachmentAccessGate</c> ที่ controller · service ตรวจว่าไฟล์เป็นของเอกสารของแถวนี้) + เหตุผล · เดิมยกเลิกในฐานเราเงียบ ๆ
/// แล้วด่านยกเลิกเอกสาร/การชำระ (<see cref="DocumentVoidPreconditions.EffectiveEtaxAsync"/> ไม่นับแถว Voided) ปล่อยผ่านทั้งที่กรมสรรพากรอาจตอบรับภายหลัง</item>
/// <item>ตอบรับแล้ว (Accepted) ⇒ ปฏิเสธ (ต้องยกเลิก/ออกใบลดหนี้ที่กรมสรรพากร) · ยกเลิกแล้ว ⇒ ปฏิเสธ</item>
/// </list>
/// <para>ป้ายสถานะบนหน้าจอ = "ยกเลิกในระบบนี้" ไม่ใช่ "กรมสรรพากรยกเลิกแล้ว" (<see cref="VoidedStatusNote"/>) · G6: pure</para>
/// </summary>
public static class EtaxVoidPolicy
{
    /// <summary>ป้ายสถานะ Voided ของแถว e-Tax ที่หน้าจอแสดง — บอกสิ่งที่ระบบรู้จริง (ไม่ใช่คำยืนยันจากกรมสรรพากร)</summary>
    public const string VoidedLabel = "ยกเลิกในระบบนี้";

    /// <summary>ตัดสินคำขอยกเลิกแถว e-Tax</summary>
    /// <param name="status">สถานะปัจจุบันของแถว</param>
    /// <param name="reason">เหตุผลที่ผู้ใช้ระบุ (บังคับเมื่อ Submitted)</param>
    /// <param name="evidenceFileAttached">ไฟล์หลักฐานที่ผ่านด่านไฟล์แนบแล้ว และเป็นไฟล์ของเอกสารของแถวนี้จริง</param>
    public static EtaxVoidVerdict Decide(EtaxStatus status, string? reason, bool evidenceFileAttached)
    {
        const string Tail = " · ระบบยังไม่ได้แตะอะไร";
        switch (status)
        {
            case EtaxStatus.Voided:
                return EtaxVoidVerdict.Refused("e-Tax นี้ถูกยกเลิกในระบบนี้ไปแล้ว" + Tail);
            case EtaxStatus.Accepted:
                return EtaxVoidVerdict.Refused(
                    "ยกเลิก e-Tax ที่ถึงกรมสรรพากรแล้ว (ตอบรับแล้ว หรือ e-Tax by Email ที่ประทับเวลาแล้ว) จากระบบนี้ไม่ได้ — ต้องยกเลิกหรือออกใบลดหนี้ที่ระบบ e-Tax "
                    + "ของกรมสรรพากร (หรือผู้ให้บริการ e-Tax) แล้วบันทึกผลที่เอกสารต้นทาง" + Tail);
            case EtaxStatus.Submitted:
                if (string.IsNullOrWhiteSpace(reason))
                    return EtaxVoidVerdict.Refused(
                        "e-Tax นี้ส่งไปกรมสรรพากรแล้ว (Submitted) — กรุณาระบุเหตุผลการยกเลิก (เก็บไว้ให้ผู้สอบบัญชี)" + Tail);
                if (!evidenceFileAttached)
                    return EtaxVoidVerdict.Refused(
                        "e-Tax นี้ส่งไปกรมสรรพากรแล้ว (Submitted) — ระบบนี้ไม่ได้ส่งคำยกเลิกถึงกรมสรรพากรเอง · ดำเนินการยกเลิกที่ระบบ e-Tax ของกรมสรรพากร"
                        + "หรือผู้ให้บริการ e-Tax ก่อน แล้วแนบไฟล์หลักฐานการยกเลิก (ภาพ/ไฟล์ตอบกลับ) เข้าเอกสารนี้ — ไฟล์ต้องแนบหลังวันที่ส่ง e-Tax "
                        + "(ไฟล์ที่แนบไว้ก่อนส่งไม่ใช่หลักฐานการยกเลิก) (คำตัดสินข้อ 51)" + Tail);
                return new EtaxVoidVerdict(true, null,
                    "ยกเลิกในระบบนี้ตามหลักฐานการยกเลิกจากกรมสรรพากร/ผู้ให้บริการที่แนบ (ผู้ใช้ยืนยัน — ระบบตรวจกับกรมสรรพากรเองไม่ได้)");
            default:
                return new EtaxVoidVerdict(true, null,
                    "ยกเลิกในระบบนี้ก่อนส่งถึงกรมสรรพากร (ไม่มีอะไรต้องยกเลิกที่กรมสรรพากร)");
        }
    }

    /// <summary>
    /// **สถานะที่ใช้ตัดสินการยกเลิกแถว e-Tax นี้** (รอบ 200 ทีม V1I · ฝ่ายค้าน V1H-O2) — สถานะของแถวเอง + บันทึก e-Tax by Email ที่ประทับเวลาของ
    /// กรมสรรพากรของเอกสารนี้ (เกณฑ์เดียวกับ <see cref="DocumentVoidPreconditions.EffectiveEtaxAsync"/>): ส่งอีเมลประทับเวลาแล้ว = ถึงกรมสรรพากร (ถือเท่าตอบรับ ⇒
    /// ยกเลิกจากระบบนี้ไม่ได้) แม้แถวยังเป็น “ลงนามแล้ว” · เดิมดูแค่สถานะแถว ⇒ แถว Signed ที่ส่งอีเมลแล้วยกเลิกได้โดยไม่มีหลักฐาน และ audit เขียนว่า
    /// “ก่อนส่งถึงกรมสรรพากร” (เท็จ) · ใช้สถานะของ<b>แถว</b> ไม่ใช่สถานะแรงสุดของทั้งเอกสาร (แถว Error ที่ค้างคู่แถวที่ตอบรับแล้วยังยกเลิกได้) ·
    /// แถวที่ยกเลิกแล้วคงเป็น Voided · G6: pure
    /// </summary>
    public static EtaxStatus StatusForVoid(EtaxStatus rowStatus, bool documentSentByEmailWithRdTimestamp)
        => rowStatus == EtaxStatus.Voided ? EtaxStatus.Voided
            : DocumentVoidPreconditions.EffectiveEtax(rowStatus, documentSentByEmailWithRdTimestamp) ?? rowStatus;

    /// <summary>
    /// **ไฟล์หลักฐานการยกเลิกต้องแนบหลังเวลานี้** (รอบ 200 ทีม V1I · ฝ่ายค้าน V1H-O5) — หลักฐานการยกเลิกเกิดได้หลังส่ง e-Tax เท่านั้น ⇒ ไฟล์ของเอกสาร
    /// ที่แนบก่อนส่ง (เช่น PDF ใบเสร็จต้นฉบับ) ไม่นับ · แถวไม่มีเวลาส่ง (ข้อมูลเก่า) = เวลาสร้างแถว e-Tax · G6: pure
    /// </summary>
    public static DateTime EvidenceNotBefore(DateTime? submittedAt, DateTime rowCreatedAt) => submittedAt ?? rowCreatedAt;

    /// <summary>หมายเหตุของแถวที่ถูกยกเลิก (หน้าจอแสดงคู่ป้าย <see cref="VoidedLabel"/>) — null = แถวไม่ได้ถูกยกเลิก</summary>
    public static string? VoidedStatusNote(EtaxStatus status, string? voidReason)
        => status != EtaxStatus.Voided ? null
            : VoidedLabel + " — ไม่ใช่คำยืนยันจากกรมสรรพากร" + (string.IsNullOrWhiteSpace(voidReason) ? "" : " · " + voidReason!.Trim());
}

/// <param name="Allowed">ยกเลิกได้</param>
/// <param name="Reason">ข้อความไทยพร้อมทางไปต่อเมื่อยกเลิกไม่ได้</param>
/// <param name="EvidenceLabel">ป้ายหลักฐานที่ลง audit/เหตุผลการยกเลิก (บอกสิ่งที่ระบบรู้จริง)</param>
public sealed record EtaxVoidVerdict(bool Allowed, string? Reason, string? EvidenceLabel)
{
    internal static EtaxVoidVerdict Refused(string reason) => new(false, reason, null);
}
