using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Document;

/// <summary>รอบ 266 แพ็ก A — ผูกใบเสร็จ/เงินที่รับไว้แล้วกับใบแจ้งหนี้ (เจ้าเดียวกัน) · ตัวตัดสิน <c>Helpers/ReceiptLinkPolicy</c> ·
/// enum ออกเป็น "ชื่อ" เสมอ (F2 ข้อ 5)</summary>
/// <param name="Route">เส้นที่ใช้ (ชื่อ <c>ReceiptLinkRoute</c>) — แถวที่ไม่อนุญาตอาจมีเส้นของแพ็กถัดไป (ยังไม่เปิด)</param>
/// <param name="Kind">ชนิดฝั่งใบเสร็จ/เงิน (ชื่อ <c>ReceiptLinkKind</c>)</param>
/// <param name="ReceiptDocumentId">ใบเสร็จ/ใบมัดจำ (null = เงินรอจัดสรรที่ไม่มีใบเสร็จ)</param>
/// <param name="PaymentId">การชำระของเงินรอจัดสรร (null = ไม่เกี่ยว)</param>
/// <param name="Available">ยอดฝั่งเงินที่ใช้ได้ (ใบเสร็จร่าง = ยอดของใบ)</param>
/// <param name="Suggested">ยอดเสนอ (≤ ยอดค้าง) · ผู้ใช้แก้ได้เฉพาะ <paramref name="AmountEditable"/></param>
public record ReceiptLinkCandidate(
    Guid InvoiceId, string InvoiceNumber, Guid? ReceiptDocumentId, Guid? PaymentId, string Number, DateTime Date,
    string Kind, string? Route, decimal Available, decimal Suggested, bool AmountEditable,
    bool Allowed, string? RuleCode, string? Reason, string? WayForward, string Label);

/// <summary>ผู้สมัครของใบที่เปิดอยู่ — <paramref name="Side"/> = "invoice" (เปิดจากใบแจ้งหนี้) · "receipt" (เปิดจากใบเสร็จ) · null = ใบนี้ไม่ใช่ทั้งสอง</summary>
/// <param name="Notice">ข้อความระดับใบ (เช่น ใบเสร็จขายสด/ของการชำระใบอื่น — เหตุที่ผูกไม่ได้ + ทางไปต่อ) · null = ไม่มี</param>
/// <param name="ShowButton">มีแถวที่ผูกได้อย่างน้อย 1 แถวไหม (ฝ่ายค้าน M6) — false = หน้าเว็บแสดง <paramref name="Notice"/> + รายการที่ผูกไม่ได้พร้อมเหตุผล ·
/// การวาดปุ่มบนหน้าเอกสารใช้ <c>DocumentResponse.CanOpenReceiptLink</c> (ไม่เรียก endpoint นี้ตอนเปิดเอกสาร)</param>
/// <param name="CanUndoDraftSource">ใบเสร็จร่างนี้ผูกใบต้นทางอยู่ ⇒ แสดงปุ่มถอด</param>
public record ReceiptLinkCandidatesResponse(
    Guid DocumentId, string DocumentNumber, string? Side, string? Kind, string? Notice, string? WayForward,
    bool ShowButton, List<ReceiptLinkCandidate> Items,
    // ใบเสร็จร่างที่ผูกใบต้นทางอยู่ ⇒ ถอดได้ (เส้น SetDraftSource) — เซิร์ฟเวอร์ตัดสิน
    bool CanUndoDraftSource = false);

/// <summary>คำขอพรีวิว/ผูก — <paramref name="ReceiptDocumentId"/> หรือ <paramref name="PaymentId"/> (เงินรอจัดสรรที่ไม่มีใบเสร็จ) อย่างใดอย่างหนึ่ง</summary>
/// <param name="Amount">ยอด (เงินรอจัดสรร/มัดจำ) · null = ยอดเสนอ · ใบเสร็จร่างใช้ยอดของใบเสมอ</param>
/// <param name="PreviewToken">กุญแจจากพรีวิว (ต้องส่งตอนผูก — ข้อเท็จจริงเปลี่ยน = 409 RL-STALE)</param>
/// <param name="ApplyDate">วันที่จัดสรร/หักมัดจำ · null = วันนี้</param>
/// <param name="Reason">เหตุผล (บังคับเมื่อพรีวิวตอบ <c>RequiresReason</c> — ย้ายการรับชำระ: เก็บบนใบแทน + audit)</param>
public record ReceiptLinkRequest(Guid InvoiceId, Guid? ReceiptDocumentId, Guid? PaymentId, decimal? Amount = null,
    string? PreviewToken = null, DateTime? ApplyDate = null, string? Reason = null);

public record ReceiptLinkEffectRow(string Label, string Detail);

public record ReceiptLinkPreviewResponse(
    bool Allowed, string Kind, string? Route, string RuleCode, string? Message, string? WayForward, string LegalReference,
    decimal Amount, decimal InvoiceBalanceBefore, decimal InvoiceBalanceAfter, List<ReceiptLinkEffectRow> Effects, string? PreviewToken,
    // รอบ 266 แพ็ก B: เส้นนี้ต้องกรอกเหตุผลก่อนยืนยัน (เซิร์ฟเวอร์ตัดสิน — หน้าเว็บแค่แสดงช่อง)
    bool RequiresReason = false);

/// <param name="AllocationId">แถวจัดสรร (เส้นเงินรอจัดสรร) — ใช้ถอดการผูก</param>
public record ReceiptLinkResult(string Route, Guid InvoiceId, string InvoiceNumber, decimal InvoiceBalanceDue, string InvoiceStatus,
    Guid? ReceiptDocumentId, Guid? PaymentId, Guid? AllocationId, IReadOnlyList<string> Notices);

/// <summary>ถอดการผูก (แพ็ก A): ใบเสร็จร่าง ⇒ <paramref name="ReceiptDocumentId"/> · เงินรอจัดสรร ⇒ <paramref name="PaymentId"/> + <paramref name="AllocationId"/></summary>
/// <remarks>แพ็ก B: ถอดการย้ายการรับชำระที่<b>ไม่ได้ออกใบแทน</b> ⇒ <c>Route = MoveSettlement</c> + <paramref name="PaymentId"/> + <paramref name="AllocationId"/> (แถวที่มีผล) + <paramref name="Reason"/></remarks>
/// <param name="PreviewToken">ฝ่ายค้าน B-2: ถอดการย้ายการรับชำระ = ย้ายกลับ ⇒ ต้องพรีวิวก่อน (POST preview ด้วย invoiceId = ใบเดิม + paymentId) แล้วส่งกุญแจมา</param>
public record ReceiptLinkUndoRequest(string Route, Guid? ReceiptDocumentId = null, Guid? PaymentId = null, Guid? AllocationId = null, string? Reason = null,
    string? PreviewToken = null);

/// <summary>รอบ 266 แพ็ก B — ข้อเท็จจริงของการรับชำระที่จะย้าย (อ่านอย่างเดียว · <c>DocumentService.LoadSettlementMoveFactsAsync</c>)</summary>
/// <param name="Source">ข้อเท็จจริงฝั่ง "ใบเสร็จ/เงิน" ของตัวตัดสิน — ใบเสร็จที่ยังมีผลของการรับชำระ หรือการรับชำระเอง (ไม่มีใบเสร็จแยก)</param>
/// <param name="LiveReceiptId">ใบเสร็จที่ยังมีผลซึ่งอ้างใบเดิม (จะถูกยกเลิก + ออกใบแทน) · null = ไม่มี</param>
public record SettlementMoveSnapshot(Guid PaymentId, ReceiptLinkSourceFacts Source, ReceiptLinkMoveFacts Move, Guid? LiveReceiptId);

/// <summary>คำสั่งย้ายการรับชำระ — <paramref name="ExpectedFromDocumentId"/> = ใบที่พรีวิวเห็นว่าเงินตัดอยู่ (เปลี่ยนไปแล้ว = 409 · กดซ้ำ/สองคน)</summary>
/// <param name="ExpectedRoute">ฝ่ายค้าน B-2: เส้นที่พรีวิวเห็น (มี/ไม่มีใบเสร็จต้องยกเลิกและออกแทน) — ใต้ล็อกต่างไป = 409 พรีวิวใหม่ · null = ไม่ตรวจ (ผู้เรียกภายใน)</param>
public record SettlementMoveCommand(Guid PaymentId, Guid TargetDocumentId, Guid ExpectedFromDocumentId, string? Reason,
    Accounting.Helpers.ReceiptLinkRoute? ExpectedRoute = null);

public record SettlementMoveResult(
    Guid PaymentId, string PaymentNumber, Guid FromDocumentId, string FromDocumentNumber, decimal FromBalanceDue,
    Guid TargetDocumentId, string TargetDocumentNumber, decimal TargetBalanceDue, DocumentStatus TargetStatus, decimal Amount, Guid AllocationId,
    Guid? VoidedReceiptId, string? VoidedReceiptNumber, Guid? ReplacementReceiptId, string? ReplacementReceiptNumber, IReadOnlyList<string> Notices);
