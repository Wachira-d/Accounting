using Accounting.Helpers;

namespace Accounting.Models.DTOs.Document;

/// <summary>รอบ 266 แพ็ก C — ข้อเท็จจริงของคู่ใบซ้ำ (อ่านอย่างเดียว · <c>DocumentService.LoadDuplicateSaleFactsAsync</c> ตัวเดียวของพรีวิวและการยืนยัน)</summary>
/// <param name="CashBankAccountId">บัญชีธนาคารของใบขายสด (เงินเข้าที่ไหน — การรับชำระใบแจ้งหนี้ใช้บัญชีเดิม)</param>
/// <param name="CashPaymentAccountId">ผังเงินของใบขายสด (เงินสด/e-Wallet) — มีค่าก่อนธนาคาร</param>
/// <param name="AllocationDate">วันที่จัดสรรเงินรอจัดสรรให้ใบแจ้งหนี้ (ฝ่ายค้าน H3 · 266-1-41) — เงินลงวันที่ใบขายสด</param>
/// <param name="CreditPaymentId">เงินรอจัดสรรที่ตัวช่วยลงไว้จากใบขายสดนี้แล้ว (ทำขั้นที่เหลือต่อ) · null = ยังไม่ลง</param>
public record DuplicateSaleSnapshot(
    DuplicateSaleDocFacts Cash, DuplicateSaleDocFacts Invoice, DuplicateSaleContext Context,
    Guid? CashBankAccountId, Guid? CashPaymentAccountId, DateTime AllocationDate, Guid? CreditPaymentId = null, decimal CreditRemaining = 0m);

/// <summary>ผลของขั้นลงเงินของใบขายสดที่ยกเลิกเป็นเงินรอจัดสรร — <paramref name="Created"/> = false เมื่อลงไว้แล้ว (ทำซ้ำได้)</summary>
public record DuplicateSaleCreditResult(Guid PaymentId, string PaymentNumber, decimal Unapplied, bool Created);

/// <summary>คู่ใบที่อาจซ้ำ (ผู้ติดต่อเดียวกัน · ยอดเท่ากัน) — เปิดจากใบขายสดหรือใบแจ้งหนี้</summary>
public record DuplicateSaleCandidate(Guid CashSaleId, string CashSaleNumber, DateTime CashSaleDate, Guid InvoiceId, string InvoiceNumber,
    DateTime InvoiceDate, decimal TotalAmount, string Label);

public record DuplicateSaleCandidatesResponse(Guid DocumentId, string DocumentNumber, string? Side, string? Notice, List<DuplicateSaleCandidate> Items);

/// <summary>ตัวเลือกหนึ่งของพรีวิว — <paramref name="Keep"/>/<paramref name="Action"/> ออกเป็น "ชื่อ" (F2 ข้อ 5)</summary>
public record DuplicateSaleOption(string Keep, bool Allowed, string Action, string RuleCode, string? Message, string? WayForward,
    List<ReceiptLinkEffectRow> Effects, string? PreviewToken);

public record DuplicateSalePreviewResponse(Guid CashSaleId, string CashSaleNumber, Guid InvoiceId, string InvoiceNumber, decimal TotalAmount,
    List<DuplicateSaleOption> Options, string LegalReference);

/// <param name="Keep">ชื่อ <c>DuplicateSaleKeep</c> ("CashSale" · "Invoice")</param>
/// <param name="PreviewToken">กุญแจของตัวเลือกนี้จากพรีวิว (ข้อเท็จจริงเปลี่ยน = 409 DS-STALE)</param>
/// <param name="Reason">เหตุผล (บังคับ — เก็บใน audit ให้ผู้สอบบัญชี)</param>
public record DuplicateSaleRequest(Guid CashSaleId, Guid InvoiceId, string Keep, string? PreviewToken, string? Reason);

public record DuplicateSaleResult(string Keep, string Action, Guid? VoidedDocumentId, string? VoidedDocumentNumber,
    Guid? CreditNoteId, string? CreditNoteNumber, Guid? PaymentId, string? PaymentNumber, IReadOnlyList<string> Notices);
