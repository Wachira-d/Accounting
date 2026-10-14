using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>อีเมลตั้งเวลาของเอกสาร — ตรวจซ้ำ "ตอนส่ง" ไม่ใช่ตอนเข้าคิว · ตัวตัดสินเดียว (pure) ของ <c>EmailScheduleService.ProcessPendingQueueAsync</c>
/// · รอบ 2026-10-14 ทีม F4 (ID#12)
///
/// <para>═══ ที่มา ═══ คิวอีเมลของเอกสาร (ส่งหลังอนุมัติ + OffsetDays · เตือนใกล้ครบกำหนด/เกินกำหนด) ถูกส่งตามที่เข้าคิวไว้โดยไม่ดูเอกสารอีกเลย ⇒
/// ใบที่ถูกยกเลิก (Void) หรือชำระครบแล้วยังถูกส่ง/ทวงถึงลูกค้า พร้อมยอดค้างชำระ ณ ตอนเข้าคิว · และไฟล์ PDF แนบสร้างไม่สำเร็จก็ยังส่งแล้วประทับ "Sent"
/// (ลูกค้าได้อีเมลเปล่า ระบบบอกว่าส่งสำเร็จ)</para></summary>
public static class ScheduledEmailSendCheck
{
    /// <summary>สถานะคิวที่ระบบข้ามโดยตั้งใจ (มีเหตุผลใน ErrorMessage) — ต่างจาก Failed (ส่งไม่สำเร็จ) และ Cancelled (ผู้ใช้ยกเลิก)</summary>
    public const string SkippedStatus = "Skipped";

    /// <summary>ทริกเกอร์ที่เป็นการทวง/เตือนชำระ — ใบที่ชำระครบแล้วต้องไม่ถูกทวง</summary>
    private static bool IsPaymentReminder(string? trigger) => trigger is "DocumentDueSoon" or "DocumentOverdue";

    /// <summary>ทริกเกอร์จากคีย์กันซ้ำ <c>doc:{id}:{trigger}:{suffix}</c> — ใช้เมื่อคิวไม่มีกฎจริง (กฎเสมือนของรายการประจำ)</summary>
    public static string? TriggerFromIdempotencyKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var parts = key.Split(':');
        return parts.Length >= 3 && parts[0] == "doc" ? parts[2] : null;
    }

    /// <summary>null = ส่งได้ · ข้อความ = ข้าม (บันทึกลงคิวให้เห็นบนหน้า "ตารางส่งอีเมล")</summary>
    public static string? SkipReason(string? trigger, bool documentFound, DocumentStatus status, decimal balanceDue)
    {
        if (!documentFound) return "ไม่ได้ส่ง: ไม่พบเอกสารแล้ว (อาจถูกลบหลังเข้าคิว)";
        if (status == DocumentStatus.Voided)
            return "ไม่ได้ส่ง: เอกสารถูกยกเลิก (Void) หลังเข้าคิว — ลูกค้าจึงไม่ได้รับอีเมลฉบับนี้";
        if (!DocumentStatusRules.IsIssued(status))
            return "ไม่ได้ส่ง: เอกสารกลับเป็นร่าง/รออนุมัติ/ถูกปฏิเสธหลังเข้าคิว — อนุมัติเอกสารแล้วส่งอีเมลจากหน้าเอกสาร";
        if (IsPaymentReminder(trigger) && (balanceDue <= 0m || status == DocumentStatus.Paid))
            return "ไม่ได้ส่งคำเตือนชำระ: เอกสารชำระครบแล้ว";
        return null;
    }

    /// <summary>หนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) ตอนส่ง — null = ส่งได้</summary>
    public static string? WhtCertSkipReason(bool found, bool voided)
        => !found ? "ไม่ได้ส่ง: ไม่พบหนังสือรับรองหัก ณ ที่จ่ายแล้ว (อาจถูกลบหลังเข้าคิว)"
         : voided ? "ไม่ได้ส่ง: หนังสือรับรองหัก ณ ที่จ่ายถูกยกเลิกหลังเข้าคิว — ถ้าออกใบใหม่แล้ว ส่งใบใหม่จากหน้าหนังสือรับรอง"
         : null;

    /// <summary>สร้างไฟล์แนบไม่สำเร็จ ⇒ ไม่ส่งอีเมลเปล่า · ข้อความบอกเหตุ + ระบบจะทำอะไรต่อ + ผู้ใช้ทำอะไรได้</summary>
    public static string AttachmentFailure(string? reason, bool willRetry)
        => $"สร้างไฟล์ PDF แนบไม่สำเร็จ ({(string.IsNullOrWhiteSpace(reason) ? "ไม่ทราบสาเหตุ" : reason!.Trim())}) — ยังไม่ได้ส่งอีเมล · "
           + (willRetry
               ? "ระบบจะลองใหม่อัตโนมัติใน 15 นาที"
               : "ลองครบจำนวนครั้งแล้ว: เปิดเอกสารแล้วกดพิมพ์/PDF เพื่อดูสาเหตุ แก้แล้วกด “ลองใหม่” ที่คิวนี้");

    /// <summary>ป้ายสถานะคิวภาษาไทย (server computes · page displays)</summary>
    public static string StatusLabel(string? status) => status switch
    {
        "Pending" => "รอส่ง",
        "Sending" => "กำลังส่ง",
        "Sent" => "ส่งแล้ว",
        "Failed" => "ส่งไม่สำเร็จ",
        "Cancelled" => "ยกเลิกแล้ว",
        SkippedStatus => "ข้าม (ไม่ส่ง)",
        _ => status ?? "",
    };
}
