using System.ComponentModel.DataAnnotations;

namespace Accounting.Models.DTOs.Integration;

// ===== Integration Configuration =====

/// <remarks>สิทธิ์ของคีย์ (รอบ 193 · G2-01): <c>CanRead/CanWrite/CanDelete</c> = ค่าที่เก็บ (สิ่งที่เจ้าของตั้ง) ·
/// <c>Effective*</c> = สิ่งที่บังคับใช้จริงตอนนี้ (คีย์รุ่นเก่าในช่วงผ่อนผันได้สิทธิ์เต็ม) — เซิร์ฟเวอร์คิดจาก
/// <c>Helpers/IntegrationKeyPolicy</c> หน้าเว็บแสดงอย่างเดียว</remarks>
public record IntegrationResponse(
    Guid Id, string SystemName, string SystemType, string? SystemVersion, string? BaseUrl,
    string ApiKeyPrefix, bool IsActive, DateTime? LastSyncAt, int TotalSyncCount, int ErrorCount,
    int RateLimitPerMinute, string? WebhookUrl, bool WebhookEnabled, DateTime CreatedAt,
    bool CanRead = true, bool CanWrite = false, bool CanDelete = false,
    bool IsLegacyKey = false, DateTime? LegacyDeprecatesAt = null, bool LegacyPrivilegeActive = false,
    bool EffectiveCanRead = true, bool EffectiveCanWrite = false, bool EffectiveCanDelete = false,
    // จำนวนวันที่เหลือก่อนคีย์รุ่นเก่าหมดช่วงผ่อนผัน (server คำนวณ · null = ไม่ใช่คีย์รุ่นเก่า) — ฝ่ายค้านรอบ 193 P5
    int? LegacyDaysRemaining = null);

/// <remarks>สิทธิ์ที่ไม่ได้ส่งมา = อ่านอย่างเดียว (<c>IntegrationKeyPolicy.ScopesForNewKey</c>) — ห้ามตีความเป็น "ให้ทั้งหมด"</remarks>
public record CreateIntegrationRequest(
    string SystemName, string SystemType, string? SystemVersion, string? BaseUrl,
    int RateLimitPerMinute = 60,
    string? WebhookUrl = null, bool WebhookEnabled = false,
    bool? CanRead = null, bool? CanWrite = null, bool? CanDelete = null);

public record IntegrationCreatedResponse(
    Guid Id, string SystemName, string ApiKey, string ApiKeyPrefix, string? SecretKey, DateTime CreatedAt);

/// <remarks>ส่งสิทธิ์มาอย่างน้อยหนึ่งช่อง = เจ้าของเลือกสิทธิ์เองแล้ว ⇒ คีย์รุ่นเก่าย้ายเข้านโยบายใหม่ทันที ·
/// ไม่ส่งเลย (null ทั้งสาม) = ไม่แตะสิทธิ์ (เช่น แก้ชื่อ/เปิดปิด ต้องไม่ทำให้คีย์รุ่นเก่าเสียสิทธิ์เงียบ ๆ)</remarks>
public record UpdateIntegrationRequest(
    string? SystemName, string? SystemType, string? SystemVersion, string? BaseUrl,
    bool? IsActive, int? RateLimitPerMinute,
    string? WebhookUrl, bool? WebhookEnabled,
    bool? CanRead = null, bool? CanWrite = null, bool? CanDelete = null);

// ===== Account Mapping =====

public record AccountMappingResponse(
    Guid Id, Guid IntegrationId,
    string ExternalCategory, string? ExternalCode, string? ExternalDescription,
    Guid? DebitAccountId, string? DebitAccountCode, string? DebitAccountName,
    Guid? CreditAccountId, string? CreditAccountCode, string? CreditAccountName,
    string? JournalDescription, bool IsActive, bool AutoCreateJournal);

public record CreateAccountMappingRequest(
    string ExternalCategory, string? ExternalCode, string? ExternalDescription,
    Guid? DebitAccountId, Guid? CreditAccountId,
    string? JournalDescription, bool AutoCreateJournal = true);

public record UpdateAccountMappingRequest(
    string? ExternalCategory, string? ExternalCode, string? ExternalDescription,
    Guid? DebitAccountId, Guid? CreditAccountId,
    string? JournalDescription, bool? IsActive, bool? AutoCreateJournal);

/// <summary>Mapping templates สำหรับตั้งค่าเร็ว — แยกตามประเภทธุรกิจ</summary>
public record MappingTemplateResponse(
    string IndustryType, string IndustryName,
    List<MappingTemplateItem> Mappings);

public record MappingTemplateItem(
    string ExternalCategory, string Description,
    string? SuggestedDebitCode, string? SuggestedCreditCode);

// ===== Sync Log =====

public record SyncLogResponse(
    Guid Id, string EventType, string? ExternalId, string? ExternalRef,
    string Status, string? ErrorMessage,
    Guid? CreatedDocumentId, Guid? CreatedContactId, Guid? CreatedJournalEntryId,
    int ProcessingTimeMs, DateTime CreatedAt);

// ===== Inbound Data (received from ANY external system) =====

public record InboundCustomerRequest(
    string? ExternalId,
    string? Name, string? NameEn,
    string? TaxId, string? Phone, string? Email,
    string? Address, string? SubDistrict, string? District, string? Province, string? PostalCode,
    string? ContactType,       // "Individual", "JuristicPerson", "GovernmentAgency"
    string? BranchCode,
    bool? IsCustomer,          // default true
    bool? IsSupplier,          // default false
    string? Notes,
    string? Moo = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? StreetName = null);

public record InboundInvoiceRequest(
    string? ExternalId, string? ExternalRef,
    string? CustomerExternalId, string? CustomerName, string? CustomerTaxId,
    DateTime DocumentDate, DateTime? DueDate,
    string? DocumentType,      // "Invoice", "TaxInvoice", "Receipt", "Quotation" — default TaxInvoice
    string? Description,
    List<InboundInvoiceLineRequest> Lines,
    string? PaymentMethod,
    decimal? VatRate,          // อัตราทั้งใบเมื่อบรรทัดไม่ระบุ — ความหมาย 0/-1 เหมือน InboundInvoiceLineRequest.VatRate
    string? Currency,          // default "THB"
    string? Notes,
    bool IncludeVat = true,
    /// <summary>Optional attachments embedded in the same request as base64.
    /// Useful for POS systems that submit JSON-only and want to bundle the
    /// receipt image with the invoice. Each attachment is decoded and saved
    /// after the document is created — its FileAttachment row will be linked
    /// to the new document automatically.</summary>
    List<InboundAttachment>? Attachments = null,
    // ── Preparer identity from the source system (same as InboundExpenseRequest) ──
    // Name + signature image ("data:image/...;base64,..." or raw base64) of the
    // real preparer, stamped into the "ผู้จัดทำ" slot of the created document.
    // Both null → falls back to the company Owner as before.
    string? PreparerName = null,
    string? PreparerSignatureBase64 = null,
    /// <summary>Resync update — เมื่อ true และพบเอกสารเดิม (ExternalRef ซ้ำ):
    /// แทนที่จะข้าม (idempotent skip) ระบบจะ "แก้เอกสาร + ปรับ JE" ให้ตรงข้อมูล
    /// ใหม่แบบถูกหลักบัญชี: กลับ JE เดิม (reversal คู่) → อัปเดตบรรทัด/ยอด →
    /// post JE ใหม่ — เลขเอกสารคงเดิม. เงื่อนไข: ยังไม่มีการชำระ, ไม่มี CN/DN
    /// อ้างถึง, งวด VAT ของเดือนภาษีเดิมยังไม่ยื่น/ล็อก — ไม่ผ่านเงื่อนไขจะได้
    /// error ชัดเจน (ให้ void แล้วส่งใหม่ หรือออก CN แทน).</summary>
    bool ResyncUpdate = false,
    /// <summary>ผู้ซื้อไม่ประสงค์รับใบกำกับภาษี (ขายปลีกหน้าร้าน) — เมื่อ true
    /// ระบบผูกเอกสารกับผู้ติดต่อกลาง "ลูกค้าเงินสด (ไม่ประสงค์รับใบกำกับภาษี)"
    /// โดยไม่ต้องส่ง customerName/customerTaxId. ไม่ส่ง flag แต่เว้นข้อมูล
    /// ลูกค้าว่างทั้งหมด = พฤติกรรมเดียวกัน (นัยเดียวกัน).</summary>
    bool BuyerDeclinedTaxInvoice = false,
    /// <summary>เลขจอง/booking ของระบบต้นทาง (PMS โรงแรม / POS / CRM) — ผูก
    /// เอกสารหลายใบเข้ากับ booking เดียวกัน (มัดจำ → ใบกำกับสุดท้าย → ใบเสร็จ)
    /// เพื่อ auto-suggest หักมัดจำ + กระทบยอด. เก็บลง `Document.BookingNumber`
    /// (JSON key = `bookingNumber`, string). null = ไม่ผูก. ป้อน RES-{reservationId}.</summary>
    string? BookingNumber = null,
    /// <summary>ขายเงินสด (B2B cash sale) — เมื่อ true ระบบจะ "รับชำระเต็มยอด
    /// ในคำขอเดียว" ทันทีหลังสร้างใบกำกับ **โดยไม่ออกใบเสร็จแยก** → ได้เอกสาร
    /// ใบเดียว หัวพิมพ์ "ใบกำกับภาษี/ใบเสร็จรับเงิน" (ServedAsReceipt) + export
    /// e-Tax เป็น TAX_INVOICE ตามปกติ. GL: ใบกำกับลง Dr ลูกหนี้/Cr รายได้+VAT
    /// แล้วการชำระลง Dr เงินสด(PaymentAccountId)/Cr ลูกหนี้ = สุทธิ Dr เงินสด/
    /// Cr รายได้+VAT (ขายสด). แก้ปัญหา 3 ใบ (TIV + REC 2 ใบ) เหลือใบเดียว.
    /// ใช้กับ DocumentType=TaxInvoice เท่านั้น. ถ้ามีหักมัดจำ ชำระเฉพาะยอดคงเหลือ.</summary>
    bool IsCashSale = false,
    /// <summary>บัญชีสินทรัพย์ที่รับเงิน(เงินสด/ธนาคาร) สำหรับ IsCashSale — cash
    /// side ของ JE ชำระจะลงบัญชีนี้. null = ใช้บัญชีเงินสด default ของบริษัท.</summary>
    Guid? PaymentAccountId = null,
    /// <summary>วันที่รับเงินจริง สำหรับ IsCashSale. null = ใช้ DocumentDate.</summary>
    DateTime? PaymentDate = null,
    /// <summary>ยอดเงินมัดจำ (รวม VAT) ที่หักบนใบนี้ (checkout) — แสดงบรรทัด
    /// "หักเงินมัดจำ" + ยอดชำระสุทธิ. → Document.DepositAppliedAmount.</summary>
    decimal DepositAppliedAmount = 0m,
    /// <summary>เลขใบมัดจำที่นำมาหัก → Document.DepositAppliedRef (ใช้กลับบัญชี
    /// deferred 217xx/21913 ของใบมัดจำเมื่อ DepositAppliedDrivesJournal=true).</summary>
    string? DepositAppliedRef = null,
    /// <summary>VAT ของมัดจำถูก defer (21913) ไว้ตอนรับมัดจำหรือไม่ →
    /// Document.DepositOutputVatDeferred (กำหนดว่าจะกลับ 21913 หรือ 21911).</summary>
    bool DepositOutputVatDeferred = false,
    /// <summary>true = ให้ DepositAppliedAmount ขับ JE self-contained (Dr เงินสด
    /// สุทธิ + กลับ 217xx/21913 ของใบมัดจำ) ในใบเดียว ไม่ต้องมี JV แยก →
    /// Document.DepositAppliedDrivesJournal (โหมด drives ที่ verified แล้ว).</summary>
    bool DepositAppliedDrivesJournal = false);

/// <summary>
/// Base64-encoded file attachment for external integrations. Server enforces:
///   - Maximum 25MB per file (post-decode)
///   - Magic-byte validation against ContentType (rejects mislabeled files)
///   - Allowed types: PDF, JPEG, PNG, BMP, TIFF, HEIF, WebP, Excel, Word
/// External systems can also use the multipart endpoint when sending many
/// large files — base64 has 33% overhead so isn't ideal beyond ~5MB.
/// </summary>
public record InboundAttachment(
    string FileName,           // Original filename to preserve in audit trail
    string ContentType,        // MIME type — verified against magic bytes
    string Base64Content);     // Standard RFC 4648 base64 (with or without padding)

public record InboundInvoiceLineRequest(
    string? ItemCode, string ItemName, decimal Quantity, decimal UnitPrice,
    decimal? DiscountAmount, string? AccountCode, string? Category,
    string? Unit,
    // อัตรา VAT ของบรรทัด (สัญญา API — INTEGRATION_RESYNC.md §11): null = อัตราตั้งต้นบริษัท · 7 = ปกติ ·
    // 0 = ขายอัตราศูนย์ §80/1 (ส่งออก — ถือเป็นใบกำกับภาษีอัตรา 0) · -1 = ยกเว้น §81 (ไม่ใช่ใบกำกับ) —
    // ห้ามส่ง 0 แทน "ยกเว้น" (ฝ่ายค้านรอบสาม B6)
    decimal? VatRate,
    // Withholding-tax rate (%) for this line. Used on purchase-side docs
    // (Expense) so integration sync can auto-issue the WHT certificate.
    decimal? WithholdingTaxRate = null,
    // Explicit VAT amount (บาท) for this line. When the external system has
    // ALREADY computed VAT — typically because the line mixes ภาษี 7% goods
    // with ของยกเว้น (exempt) where a single VatRate can't express the true
    // VAT — NextAcc honors THIS value instead of recomputing net × rate.
    // Null = recompute as before (backward compatible). Prevents the
    // "ยอดจ่าย ≠ ยอด NextAcc" mismatch that creates a phantom ค้างชำระ.
    decimal? VatAmount = null);

public record InboundPaymentRequest(
    string? ExternalId, string? ExternalRef,
    string? InvoiceExternalRef, Guid? DocumentId,
    string? CustomerExternalId, string? CustomerName,
    DateTime PaymentDate, decimal Amount,
    string? PaymentMethod,      // "Cash", "BankTransfer", "CreditCard", "PromptPay", "Cheque", "EWallet"
    string? BankAccountName, string? ReferenceNo, string? SlipUrl,
    string? Notes);

public record InboundCreditNoteRequest(
    string? ExternalId, string? ExternalRef,
    string? OriginalInvoiceRef, Guid? OriginalDocumentId,
    string? CustomerExternalId, string? CustomerName,
    DateTime DocumentDate,
    string Reason,
    List<InboundInvoiceLineRequest> Lines,
    string? Notes,
    List<InboundAttachment>? Attachments = null,
    /// <summary>เลขจอง booking (JSON `bookingNumber`, string) → Document.BookingNumber
    /// — ผูก CN เข้า booking เดียวกับมัดจำ/ใบกำกับ (เหมือน invoice). null = ไม่ผูก.</summary>
    string? BookingNumber = null);

public record InboundDebitNoteRequest(
    string? ExternalId, string? ExternalRef,
    string? OriginalInvoiceRef, Guid? OriginalDocumentId,
    string? CustomerExternalId, string? CustomerName,
    DateTime DocumentDate,
    string Reason,
    List<InboundInvoiceLineRequest> Lines,
    string? Notes,
    List<InboundAttachment>? Attachments = null,
    /// <summary>เลขจอง booking (JSON `bookingNumber`, string) → Document.BookingNumber
    /// — ผูก DN เข้า booking เดียวกับมัดจำ/ใบกำกับ (เหมือน invoice). null = ไม่ผูก.</summary>
    string? BookingNumber = null);

/// <summary>ค่าใช้จ่ายจากระบบภายนอก</summary>
public record InboundExpenseRequest(
    string? ExternalId, string? ExternalRef,
    string? SupplierExternalId, string? SupplierName, string? SupplierTaxId,
    DateTime DocumentDate, DateTime? DueDate,
    List<InboundInvoiceLineRequest> Lines,
    decimal? VatRate,
    string? Notes,
    bool IncludeVat = true,
    List<InboundAttachment>? Attachments = null,
    // ── Preparer identity from the source system ──
    // The partner names the real person who prepared the voucher and may
    // ship their signature image inline (a "data:image/png;base64,..." URI
    // or raw base64). When present, this person + signature is stamped into
    // the "ผู้จัดทำ" slot of the document — even though they are not a
    // NextAcc User. Both null → falls back to the company Owner as before.
    string? PreparerName = null,
    string? PreparerSignatureBase64 = null,
    /// <summary>Resync update — เหมือน InboundInvoiceRequest.ResyncUpdate.</summary>
    bool ResyncUpdate = false,
    /// <summary>true (default, backward-compat) = สร้างเป็น Approved + ลง JE +
    /// ออก 50 ทวิ ทันที (พฤติกรรมเดิม). false = สร้างเป็น "ฉบับร่าง (Draft)" —
    /// ยังไม่ลง GL และยังไม่ออก 50 ทวิ; จะลง JE + ออก 50 ทวิ เมื่ออนุมัติ
    /// (ApproveDocumentAsync) ภายหลัง. ผู้เรียกเดิมที่ไม่ส่ง field นี้ = true
    /// เหมือนเดิม ไม่กระทบระบบที่เชื่อมต่ออยู่.</summary>
    bool AutoApprove = true,
    /// <summary>Contact.Id ของผู้จำหน่ายในระบบ NextAcc โดยตรง (ถ้าต้นทางเคยรู้).
    /// ใช้ match ผู้จำหน่ายแบบแม่นที่สุด — ก่อน SupplierExternalId/TaxId/ชื่อ
    /// — กัน contact ซ้ำจาก integration.</summary>
    Guid? SupplierContactId = null);

/// <summary>ใบสำคัญจ่าย (การจ่ายเงินจริง) จากระบบภายนอก — สำหรับ voucher
/// ที่จ่ายเงินไปแล้วในระบบต้นทาง: สร้างเอกสาร PV เดียวจบ (Dr ค่าใช้จ่าย /
/// Cr เงินสด-ธนาคาร) ไม่ต้อง map เป็น expense + payment สองยก
/// ซึ่งสร้างหนี้หลอกที่ถูกตัดทันที</summary>
public record InboundPaymentVoucherRequest(
    string? ExternalId, string? ExternalRef,
    string? SupplierExternalId, string? SupplierName, string? SupplierTaxId,
    DateTime DocumentDate,
    DateTime? PaymentDate,
    List<InboundInvoiceLineRequest> Lines,
    decimal? VatRate,
    string? Notes,
    bool IncludeVat = true,
    List<InboundAttachment>? Attachments = null,
    string? PreparerName = null,
    string? PreparerSignatureBase64 = null,
    /// <summary>true (default, backward-compat) = สร้าง PV เป็น Approved + ลง JE
    /// + ออก 50 ทวิ ทันที (พฤติกรรมเดิม). false = สร้างเป็นฉบับร่าง (Draft) —
    /// ยังไม่ลง GL/ยังไม่ออก 50 ทวิ จนกว่าจะอนุมัติภายหลัง. ไม่ส่ง = true.</summary>
    bool AutoApprove = true,
    /// <summary>Contact.Id ของผู้จำหน่ายในระบบ NextAcc โดยตรง — match แม่นสุด
    /// ก่อน SupplierExternalId/TaxId/ชื่อ กัน contact ซ้ำจาก integration.</summary>
    Guid? SupplierContactId = null);

/// <summary>ใบรับรองแทนใบเสร็จจากระบบภายนอก</summary>
public record InboundCertificateInLieuRequest(
    string? ExternalId, string? ExternalRef,
    string? SupplierExternalId, string? SupplierName, string? SupplierTaxId,
    DateTime DocumentDate,
    DateTime? PaymentDate,
    string CertificateReason,
    string CertifierName,
    string? CertifierPosition,
    string? WitnessName,
    string? WitnessPosition,
    List<InboundInvoiceLineRequest> Lines,
    decimal? VatRate,
    string? Notes,
    bool IncludeVat = true,
    List<InboundAttachment>? Attachments = null);

/// <summary>สินค้า/บริการจากระบบภายนอก</summary>
public record InboundProductRequest(
    string? ExternalId,
    string Code, string Name, string? NameEn,
    string? ProductType,       // "Product", "Service", "NonStock"
    decimal? Price, decimal? CostPrice,
    string? Unit,
    string? Category,
    string? AccountCode,       // Revenue account code
    bool? IsActive,
    string? Notes);

/// <summary>บันทึกบัญชีตรงจากระบบภายนอก</summary>
public record InboundJournalRequest(
    string? ExternalId, string? ExternalRef,
    DateTime EntryDate,
    string? JournalType,       // "General", "Sales", "Purchase", "CashReceipts", "CashPayments"
    string? Description,
    List<InboundJournalLineRequest> Lines,
    bool AutoBalanceVat = false);

public record InboundJournalLineRequest(
    string AccountCode, decimal DebitAmount, decimal CreditAmount,
    string? Description);

/// <summary>กลับรายการ Journal จากระบบภายนอก</summary>
public record InboundReverseJournalRequest(
    string? ExternalId, string? ExternalRef,
    Guid OriginalJournalEntryId,
    DateTime? ReversalDate,
    string? Description);

/// <summary>
/// ยกเลิกเอกสาร (Receipt / Invoice / TaxInvoice / PaymentVoucher ฯลฯ) จากระบบ
/// ภายนอก — รองรับกรณีเช่น TaketTime / booking ที่ออกใบเสร็จมัดจำมาแล้ว ต่อมา
/// ต้องการยกเลิกใบเดิมเพื่อออกใบรายรับเต็มเมื่อลูกค้าเข้าพักจริง. ระบบจะค้น
/// เอกสารด้วย ExternalRef (preferred) หรือ ExternalId แล้วเรียก VoidDocumentAsync
/// ที่ cascade ครบ: reverse Posted JE, void Payments ที่ผูก, ตัด bank match.
/// </summary>
public record InboundVoidDocumentRequest(
    string? ExternalId,
    string? ExternalRef,
    Guid? DocumentId,        // ทางเลือก: ส่ง internal Id โดยตรงก็ได้
    string? Reason);

/// <summary>Batch import — ส่งข้อมูลหลายรายการพร้อมกัน (สูงสุด 500 รายการต่อประเภท)</summary>
public record InboundBatchRequest(
    [property: MaxLength(500)] List<InboundCustomerRequest>? Customers,
    [property: MaxLength(500)] List<InboundInvoiceRequest>? Invoices,
    [property: MaxLength(500)] List<InboundPaymentRequest>? Payments,
    [property: MaxLength(500)] List<InboundExpenseRequest>? Expenses,
    [property: MaxLength(500)] List<InboundProductRequest>? Products,
    [property: MaxLength(500)] List<InboundJournalRequest>? Journals,
    [property: MaxLength(500)] List<InboundCertificateInLieuRequest>? CertificatesInLieu = null,
    [property: MaxLength(500)] List<InboundPaymentVoucherRequest>? PaymentVouchers = null);

public record InboundBatchResponse(
    int TotalProcessed, int SuccessCount, int ErrorCount,
    List<BatchResultItem> Results);

public record BatchResultItem(
    string Type, string? ExternalRef, bool Success, string? Message,
    Guid? CreatedId, string? CreatedNumber);

public record InboundDailySummaryRequest(
    DateTime SummaryDate,
    string? Description,
    List<DailySummaryLineRequest> Lines);

public record DailySummaryLineRequest(
    string Category,           // ตั้งค่าได้ตาม mapping ของแต่ละบริษัท
    string? Description,
    decimal Amount,
    string? PaymentMethod);

// ===== Response =====

public record InboundSyncResponse(
    bool Success, string? Message,
    Guid? DocumentId, Guid? ContactId, Guid? JournalEntryId, Guid? PaymentId,
    string? DocumentNumber,
    /// <summary>IDs of FileAttachment rows created from Attachments[] in the request.
    /// Empty when no attachments were sent or all failed validation. Order matches
    /// the request's Attachments list — failed entries are reported via Warnings.</summary>
    List<Guid>? AttachmentIds = null,
    List<string>? Warnings = null);

// ===== Outbound Data (external systems read FROM Next Acc) =====

public record OutboundDocumentResponse(
    Guid Id, string DocumentNumber, string DocumentType, string Status,
    DateTime DocumentDate, DateTime? DueDate,
    string? ContactName, string? ContactTaxId,
    decimal SubTotal, decimal VatAmount, decimal TotalAmount, decimal PaidAmount, decimal BalanceDue,
    string? Reference, string? Notes,
    List<OutboundDocumentLineResponse> Lines,
    DateTime CreatedAt,
    // ไฟล์แนบ (รวมไฟล์ต้นฉบับ OCR) — partner ดึงไปแสดง/ดาวน์โหลด.
    // เดิม response ไม่มี field นี้ → "บนระบบมีไฟล์ แต่ api ดึงไปไม่มี".
    List<OutboundAttachment>? Attachments = null);

/// <summary>ไฟล์แนบของเอกสารใน outbound response — partner ใช้ DownloadUrl
/// (relative path) ดาวน์โหลดผ่าน API เดิม (แนบ X-Api-Key/Bearer).</summary>
public record OutboundAttachment(
    Guid Id,
    string FileName,
    string OriginalFileName,
    string ContentType,
    long FileSize,
    string DownloadUrl,
    DateTime CreatedAt);

public record OutboundDocumentLineResponse(
    string? ProductCode, string Description, decimal Quantity,
    string? Unit, decimal UnitPrice, decimal DiscountAmount, decimal Amount,
    decimal VatRate, decimal VatAmount);

public record OutboundContactResponse(
    Guid Id, string Name, string? TaxId, string? BranchCode,
    string ContactType, bool IsCustomer, bool IsSupplier,
    string? Address, string? Phone, string? Email,
    DateTime CreatedAt,
    string? BranchName,
    string? BuildingNumber,
    string? BuildingName,
    string? StreetName,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode,
    string? CountryCode,
    string? ContactPerson,
    bool IsActive,
    string? Moo = null);

public record OutboundPaymentResponse(
    Guid Id, string PaymentNumber, Guid DocumentId, string? DocumentNumber,
    DateTime PaymentDate, decimal Amount, string PaymentMethod,
    string? Reference, string? Notes, DateTime CreatedAt);

public record OutboundAccountBalanceResponse(
    string AccountCode, string AccountName, string AccountType,
    decimal DebitBalance, decimal CreditBalance, decimal NetBalance);

public record OutboundQueryParams(
    DateTime? FromDate, DateTime? ToDate,
    string? Status, string? Type,
    int Page = 1, int PageSize = 50);

public record OutboundPagedResponse<T>(
    List<T> Items, int TotalCount, int Page, int PageSize, int TotalPages);

// ===== Integration Dashboard =====

public record IntegrationDashboardResponse(
    int TotalIntegrations, int ActiveIntegrations,
    int TodaySyncCount, int TodayErrorCount,
    List<IntegrationSummary> Integrations,
    List<RecentSyncItem> RecentSyncs);

public record IntegrationSummary(
    Guid Id, string SystemName, string SystemType, bool IsActive,
    DateTime? LastSyncAt, int TotalSyncCount, int ErrorCount);

public record RecentSyncItem(
    Guid Id, string SystemName, string EventType, string Status,
    string? ExternalRef, DateTime CreatedAt);

// ===== Revenue Reports (generic — ใช้ได้ทุกธุรกิจ) =====

public record RevenueByCategoryItem(
    string AccountCode, string AccountName, decimal Amount, decimal Percentage);

public record RevenueBySourceItem(
    string Source, int InvoiceCount, decimal TotalAmount, decimal Percentage);

public record DepositSummaryResponse(
    decimal TotalDepositsReceived, decimal TotalDepositsApplied, decimal OutstandingDeposits,
    List<DepositDetailItem> Details);

public record DepositDetailItem(
    Guid DocumentId, string DocumentNumber, string? CustomerName,
    decimal DepositAmount, DateTime DocumentDate, string Status);

public record DailyRevenueItem(
    DateTime Date, decimal TotalRevenue, int InvoiceCount,
    List<DailyRevenueCategoryItem> Categories);

public record DailyRevenueCategoryItem(
    string AccountCodePrefix, string CategoryName, decimal Amount);
