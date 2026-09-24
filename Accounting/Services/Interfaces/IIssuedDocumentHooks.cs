using Accounting.Models.Entities;

namespace Accounting.Services.Interfaces;

/// <summary>ผลของผลข้างเคียงหลังออกเอกสาร — ให้ผู้เรียกบอกต่อในคำตอบของตัวเอง (F2 ข้อ 7 "ล้มดัง 3 ที่")</summary>
/// <param name="EtaxAttempted">ลองออก e-Tax อัตโนมัติในรอบนี้ (บริษัทเปิด e-Tax + ใบอยู่ในขอบเขต)</param>
/// <param name="EtaxFailureReason">ข้อความสาเหตุเมื่อออกไม่สำเร็จ · <c>null</c> = ไม่ได้ล้ม</param>
public sealed record IssuedDocumentHookResult(bool EtaxAttempted, string? EtaxFailureReason)
{
    public static readonly IssuedDocumentHookResult Nothing = new(false, null);
    public bool EtaxFailed => EtaxFailureReason != null;
}

/// <summary>
/// **ผลข้างเคียงหลัง "ออกเอกสาร" — จุดเดียวที่ทุกทางเข้าต้องเรียก** (รอบ 193 · ผลตรวจ S-02)
///
/// <para>ค่าตั้งที่มีผล "ตอนออกเอกสาร" เคยอยู่ใน <c>ApproveDocumentAsync</c> เท่านั้น แต่มีทางเข้าที่ประทับ
/// <c>Approved</c>/<c>Paid</c> เอง (POS ใบกำกับเต็มรูป · Integration TIV/CN/DN · ใบเสร็จ settlement §78/1) ⇒ บริษัทที่เปิด
/// e-Tax อัตโนมัติได้ e-Tax เฉพาะใบจากเว็บ</para>
///
/// <para>ตอนนี้มีขั้นเดียว: e-Tax อัตโนมัติ (<c>EtaxEnabled</c> + <c>EtaxAutoSign</c>) · ขอบเขตรายใบตัดสินด้วย
/// <see cref="Accounting.Helpers.EtaxAutoIssueScope"/> · ล้ม = <b>ไม่ทำให้ใบขายล้ม</b> แต่ประทับป้าย
/// (<see cref="Accounting.Helpers.EtaxAutoFailedNote"/>) ลงหมายเหตุภายใน + หน้าเอกสารแสดง + คืนผลให้ผู้เรียก ·
/// <b>ไม่</b>รวมวงเงินอนุมัติ/SoD/Budget (คำถามเจ้าของ Q2) · <c>tools/approved_status_writer_check.py</c> ฟ้องจุดออกเอกสาร
/// นอก ApproveDocumentAsync ที่ไม่เรียกตัวนี้</para>
/// </summary>
public interface IIssuedDocumentHooks
{
    /// <summary>รันผลข้างเคียงหลังออกเอกสาร — เรียก<b>หลัง</b> commit ธุรกรรมของใบนั้นแล้ว (ล้มต้องไม่ย้อนการออกใบ)
    /// · ใช้ entity ตัวที่ผู้เรียกถืออยู่ (context เดียวกัน) · idempotent (มี e-Tax ที่ไม่ใช่ Error = ข้าม) · ไม่ throw</summary>
    Task<IssuedDocumentHookResult> RunAsync(Guid companyId, Document doc, CancellationToken ct = default);

    /// <summary>เหมือน <see cref="RunAsync(Guid, Document, CancellationToken)"/> แต่รับเลขเอกสาร · ไม่พบเอกสารของบริษัทนี้ = ไม่ทำอะไร</summary>
    Task<IssuedDocumentHookResult> RunAsync(Guid companyId, Guid documentId, CancellationToken ct = default);
}
