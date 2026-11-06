namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 241 — งานอัตโนมัติหลังรับชำระ <b>หนึ่งแถวต่อการชำระ</b> (unique <c>CompanyId + PaymentId</c> = กันซ้ำที่ฐาน: webhook ซ้ำ · กดบันทึกซ้ำ ·
/// เครื่องสองเครื่อง) · ผู้เขียนเดียว <c>PostPaymentAutomationService</c> (เข้าคิว <c>EnqueueAsync</c> · ประมวลผล <c>ProcessDueAsync</c> ใน
/// <c>EmailScheduleWorker</c>) · สถานะ/เหตุผลแสดงบนหน้าเอกสาร (ล้มดัง — ไม่ใช่แค่ log) · ตาราง/ดัชนีจาก <c>Helpers/DocumentAutoSendMigration</c>
/// </summary>
public class PostPaymentAutomationRun : TenantEntity
{
    public Guid PaymentId { get; set; }
    /// <summary>เอกสารต้นทางของการชำระ (ใบแจ้งหนี้/ใบกำกับ/ใบเพิ่มหนี้ หรือเอกสารฝั่งซื้อ)</summary>
    public Guid DocumentId { get; set; }
    /// <summary>ทางเข้าที่บันทึกการชำระ: gateway · manual</summary>
    public string Source { get; set; } = "";
    /// <summary>Pending · Done · Blocked · Failed · Skipped (<c>PostPaymentAutomationPolicy.Status*</c>)</summary>
    public string Status { get; set; } = "Pending";
    /// <summary>ใบต้นทางชำระครบ ณ ตอนเข้าคิว (ฝ่ายค้าน N) — ตัดสิน "งวดสุดท้าย/ชำระบางส่วน" จากเหตุการณ์ชำระนี้ ไม่ใช่สถานะตอนประมวลผล (งวดถัดไปอาจเข้ามาก่อน)</summary>
    public bool FullyPaid { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    /// <summary>ใบเสร็จที่ส่ง (ออกโดยเส้นเดิม) · หรือใบต้นทางเมื่อใบต้นทางเป็นใบเสร็จอยู่แล้ว</summary>
    public Guid? ReceiptDocumentId { get; set; }
    /// <summary>ขั้นที่ทำสำเร็จแล้ว (คั่นด้วย ,) — ลองใหม่ไม่ทำขั้นที่สำเร็จซ้ำ (ไม่ส่งอีเมลซ้ำ)</summary>
    public string? StepsDone { get; set; }
    /// <summary>เหตุผลที่หยุด/ล้ม หรือสรุปผล (ข้อความไทย — แสดงบนหน้าเอกสาร)</summary>
    public string? Message { get; set; }
    public DateTime? CompletedAt { get; set; }
}
