using Accounting.Helpers;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;

namespace Accounting.Services.Interfaces;

/// <summary>
/// รอบ 246 QR — <b>QR ชำระเงินของบัญชีธนาคาร + QR บนเอกสาร</b> (ตัวตัดสิน <see cref="PaymentQrPolicy"/> · DOCUMENT_FLOW §2.12i · คำตัดสิน 246-QR-*)
/// <para>ผู้เขียนเดียวของ <c>BankAccount.PromptPayId/PaymentQrSource/PaymentQrImagePath</c> · ผู้สร้างเดียวของ <see cref="PaymentQrBlock"/> ที่ตัวเรนเดอร์ทั้งสองวาด</para>
/// </summary>
public interface IPaymentQrService
{
    /// <summary>อัปโหลดรูป QR (ตรวจด้วย <see cref="PaymentQrImageValidator"/> → ถอดรูปแล้วเข้ารหัส PNG ใหม่ → ที่เก็บกลาง) · แทนรูปเดิม · audit</summary>
    Task<BankAccountResponse> UploadImageAsync(Guid companyId, Guid accountId, byte[] content, string actor, CancellationToken ct = default);

    /// <summary>ลบรูป QR ของบัญชี · audit · ไม่มีรูป = ข้อความ (ไม่ใช่สำเร็จเงียบ ๆ)</summary>
    Task<BankAccountResponse> RemoveImageAsync(Guid companyId, Guid accountId, string actor, CancellationToken ct = default);

    /// <summary>ตั้งเลขพร้อมเพย์/แหล่ง QR · audit</summary>
    Task<BankAccountResponse> UpdateSettingsAsync(Guid companyId, Guid accountId, UpdateBankPaymentQrRequest request, string actor, CancellationToken ct = default);

    /// <summary>ตัวอย่าง QR ที่จะพิมพ์ (หน้าแก้บัญชี) — <paramref name="promptPayId"/>/<paramref name="source"/> null = ค่าที่บันทึกไว้ (ตัวอย่างสดก่อนบันทึก)</summary>
    Task<BankPaymentQrPreviewResponse> PreviewAsync(Guid companyId, Guid accountId, string? promptPayId, string? source, decimal? amount, CancellationToken ct = default);

    /// <summary>QR ของใบนี้สำหรับตัวเรนเดอร์ทั้งสอง — null = ไม่วาดอะไร (ปิด · ชนิดไม่เข้า · ไม่มีบัญชีที่ตั้ง QR · ไม่มีลิงก์ที่ใช้ได้)</summary>
    Task<PaymentQrBlock?> ResolveForDocumentAsync(Document doc, DocumentTemplate template, CancellationToken ct = default);

    /// <summary>ฝ่ายค้าน S2(ก)/S4/N5 — เหตุที่ใบนี้ไม่พิมพ์ QR บัญชี + คำเตือน (ข้อความบัญชีไม่ตรง · WHT · ใบกำกับอ้างอยู่) สำหรับหน้าเอกสาร (ตัวตัดสินเดียวกับกระดาษ)</summary>
    Task<PaymentQrDiagnosis> DiagnoseDocumentAsync(Guid companyId, Guid documentId, CancellationToken ct = default);

    /// <summary>ฝ่ายค้าน S2(ก)/S4 — บัญชีที่ QR ของเทมเพลตจะใช้ + เหตุ/คำเตือน (หน้าเทมเพลต)</summary>
    Task<PaymentQrDiagnosis> DiagnoseTemplateAsync(Guid companyId, DocumentTemplate template, CancellationToken ct = default);

    /// <summary>ตัวอย่างบนหน้าเทมเพลต (ข้อมูลสมมุติ — QR บัญชีไม่ฝังยอด · ไม่มี QR หน้าชำระเพราะไม่มีใบจริง)</summary>
    Task<PaymentQrBlock?> SampleForTemplateAsync(Guid companyId, DocumentTemplate template, CancellationToken ct = default);
}
