using Accounting.Models.Entities;

namespace Accounting.Services.Interfaces;

/// <summary>
/// **ผลข้างเคียงหลัง "ออกเอกสาร" — จุดเดียวที่ทุกทางเข้าต้องเรียก** (รอบ 193 · ผลตรวจ S-02)
///
/// <para>ค่าตั้งที่มีผล "ตอนออกเอกสาร" เคยอยู่ใน <c>ApproveDocumentAsync</c> เท่านั้น แต่มีทางเข้าที่ประทับ
/// <c>Approved</c> เอง (POS ใบกำกับเต็มรูป · Integration TIV/CN/DN) ⇒ บริษัทที่เปิด e-Tax อัตโนมัติ
/// ได้ e-Tax เฉพาะใบจากเว็บ ใบจาก POS/Integration ไม่เคยถูกออก e-Tax และไม่มีอะไรเตือน</para>
///
/// <para>ตอนนี้มีขั้นเดียว: e-Tax อัตโนมัติ (<c>EtaxEnabled</c> + <c>EtaxAutoSign</c>) · ขอบเขตรายใบตัดสินด้วย
/// <see cref="Accounting.Helpers.EtaxAutoIssueScope"/> · ล้ม = <b>ไม่ทำให้ใบขายล้ม</b> แต่ประทับ
/// <c>[ETAX-AUTO-FAILED]</c> ลงหมายเหตุภายในของเอกสาร (ที่ผู้ใช้เปิดดู) + log · <b>ไม่</b>รวมวงเงินอนุมัติ/SoD/Budget
/// (คำถามเจ้าของ Q2) · <c>tools/approved_status_writer_check.py</c> ฟ้องจุดประทับ Approved นอก DocumentService
/// ที่ไม่เรียกตัวนี้</para>
/// </summary>
public interface IIssuedDocumentHooks
{
    /// <summary>รันผลข้างเคียงหลังออกเอกสาร — เรียก<b>หลัง</b> commit ธุรกรรมของใบนั้นแล้ว (ล้มต้องไม่ย้อนการออกใบ)
    /// · ใช้ entity ตัวที่ผู้เรียกถืออยู่ (context เดียวกัน) · idempotent (มี e-Tax ที่ไม่ใช่ Error แล้ว = ข้าม)</summary>
    Task RunAsync(Guid companyId, Document doc, CancellationToken ct = default);

    /// <summary>เหมือน <see cref="RunAsync(Guid, Document, CancellationToken)"/> แต่รับเลขเอกสาร (ผู้เรียกไม่ได้ถือ entity) ·
    /// ไม่พบเอกสารของบริษัทนี้ = ไม่ทำอะไร</summary>
    Task RunAsync(Guid companyId, Guid documentId, CancellationToken ct = default);
}
