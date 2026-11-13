using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 267 แพ็ก P4 (คำตัดสิน 267-1-8 · 267-1-44) — ค่าตั้ง "ออก/ส่งใบเสร็จ-ใบกำกับอัตโนมัติหลังรับชำระ" <b>ต่อเอกสาร</b> ตอนสร้าง/แก้ไขใบ ·
/// ตัวตัดสินเดียว <see cref="PostPaymentAutomationPolicy.ApplyDocumentLayer"/> (ปุ่มบนหน้าเอกสารของใบที่ออกแล้วใช้ตัวเดียวกันที่
/// <c>PostPaymentAutomationService.SetDocumentLayerAsync</c>) · ผู้อ่านเชิงธุรกิจ = <c>PostPaymentAutomationService.SettingsForAsync</c> ·
/// <b>ไม่สืบทอด</b>ไปใบลูก/โคลน/ใบแทน: ทางสร้างใบลูกทุกทางไม่ส่งสองช่องนี้ ⇒ ใบลูก = ตามค่าตั้ง (เจตนาของใบต้นทางไม่ไหลตาม)
/// </summary>
public partial class DocumentService
{
    /// <summary>ค่าใหม่ของสองช่อง — รหัสไม่รู้จัก/ชนิดที่ตั้งไม่ได้ ⇒ โยน (ข้อความไทย · ไม่เก็บค่าที่ไม่มีผู้อ่าน)</summary>
    private static (bool? EmailReceipt, bool? IncludePartial) PostPayDocumentLayerOrThrow(DocumentType type, bool? currentEmailReceipt,
        bool? currentIncludePartial, string? receiptMode, string? partialMode)
    {
        var r = PostPaymentAutomationPolicy.ApplyDocumentLayer(type, currentEmailReceipt, currentIncludePartial, receiptMode, partialMode);
        if (r.Problem != null) throw new BusinessRuleException(r.Problem, PostPaymentAutomationPolicy.RuleCode, 400);
        return (r.EmailReceipt, r.IncludePartial);
    }

    /// <summary>แก้ไขใบ: null = ไม่แตะ · "" / "Inherit" = ล้างกลับไปตามค่าตั้ง · "On" / "Off" = ตั้งที่ใบ</summary>
    private static void ApplyPostPayDocumentLayer(Document doc, string? receiptMode, string? partialMode)
    {
        if (receiptMode == null && partialMode == null) return;
        (doc.PostPayEmailReceipt, doc.PostPayIncludePartial) =
            PostPayDocumentLayerOrThrow(doc.DocumentType, doc.PostPayEmailReceipt, doc.PostPayIncludePartial, receiptMode, partialMode);
    }
}
