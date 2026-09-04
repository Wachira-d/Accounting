using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Payments;

/// <summary>
/// สิ่งที่ต้องเกิดขึ้น **หลังเงินเข้าจริง** สำหรับต้นทางแต่ละชนิด
///
/// ═══ ทำไมต้องมี ═══
/// ถ้า intent สำเร็จแล้ว<b>ไม่มีอะไรเกิดขึ้นต่อ</b> ระบบจะกลายเป็น defect class ที่
/// CLAUDE.md บอกว่าใหญ่ที่สุดในเรพนี้ทันที — "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้"
/// (ลูกค้าจ่ายเงินสำเร็จ · แถวในตารางถูกต้อง · แต่ออเดอร์ยังค้างชำระตลอดกาล)
///
/// ═══ ทำไมแยกเป็น handler ต่อชนิด ไม่ใช่ switch ก้อนเดียว ═══
/// ทางเข้าแต่ละทางมี orchestrator ของตัวเองที่ทำงานถูกอยู่แล้ว (ตัดสต็อก · ลง JE ·
/// ออก e-Tax · ส่งอีเมล) · หน้าที่ของชั้นนี้คือ<b>เรียกของที่มีอยู่</b> ไม่ใช่เขียนใหม่ ·
/// การแยกเป็น handler ทำให้เพิ่มทางเข้าใหม่ = เพิ่มไฟล์ ไม่ใช่แก้ service กลาง
/// </summary>
public interface IPaymentCompletionHandler
{
    PaymentSourceKind SourceKind { get; }

    /// <summary>เรียกเมื่อ intent เปลี่ยนเป็น <c>Succeeded</c> — **ต้อง idempotent**
    /// เพราะ webhook และ poll อาจยิงมาพร้อมกัน (orchestrator เดิมส่วนใหญ่ idempotent
    /// อยู่แล้ว แต่ห้ามสมมติ — ตรวจก่อนทำงานเสมอ)</summary>
    Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default);
}
