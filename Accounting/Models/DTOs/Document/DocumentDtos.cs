using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Document;

public record CreateDocumentRequest(
    DocumentType DocumentType,
    DateTime DocumentDate,
    DateTime? DueDate,
    Guid ContactId,
    string? Reference,
    string? Notes,
    List<DocumentLineRequest> Lines,
    Guid? ProjectId = null,
    // Cost center / สาขา / แผนก — ไหลลง JE.DimensionId ตอน approve
    Guid? DimensionId = null,
    // เอกสารต้นทาง (convert chain) — ต้องรู้ตั้งแต่ create เพราะ PaymentType
    // inference / cash-settle / PV auto-approve แยกพฤติกรรมด้วย field นี้
    Guid? RelatedDocumentId = null,
    // หักเงินมัดจำบนใบรับเงินสุดท้าย (display-only — ไม่กระทบ JE): ยอดมัดจำ
    // ที่หัก (รวม VAT) + เลขใบมัดจำอ้างอิง → renderer แสดง "หักเงินมัดจำ" +
    // "ยอดชำระสุทธิ". line ยังเป็นการขายเต็มจำนวน (ห้าม line ติดลบ)
    decimal? DepositAppliedAmount = null,
    string? DepositAppliedRef = null,
    bool? DepositAppliedDrivesJournal = null,
    bool? BuyerDeclinedTaxInvoice = null,
    // ส่วนลดท้ายบิล (จากยอดรวม) — กรอกอย่างใดอย่างหนึ่ง: % หรือ ยอดบาท (ex-VAT).
    // ระบบเฉลี่ย pro-rata ลงบรรทัดให้ VAT ถูกต้อง
    decimal? BillDiscountPercent = null,
    decimal? BillDiscountAmount = null,
    Guid? BankAccountId = null,
    Guid? PaymentAccountId = null,
    Guid? ExpenseCategoryId = null,
    string? CustomAppendix = null,
    string? CustomFooterNotes = null,
    string? CustomTermsAndConditions = null,
    Guid? RevenueContractId = null,
    Guid? PerformanceObligationId = null,
    // ===== ใบรับรองแทนใบเสร็จ =====
    string? CertificateReason = null,
    string? CertifierName = null,
    string? CertifierPosition = null,
    string? WitnessName = null,
    string? WitnessPosition = null,
    DateTime? PaymentDate = null,
    // ===== สกุลเงิน + อัตราแลกเปลี่ยน =====
    // Currency defaults to THB; ExchangeRate to 1. For non-THB docs the
    // service auto-fetches the BoT mid-rate at DocumentDate if ExchangeRate
    // is omitted; callers can override with a contracted rate.
    string Currency = "THB",
    decimal? ExchangeRate = null,
    // ===== Sensitivity classification (optional) =====
    // Internal callers (e.g. PayrollService) pass Sensitivity to gate the
    // resulting document behind the matching role. External clients leave it
    // None (the default) and the document is publicly visible within the company.
    SensitivityKind Sensitivity = SensitivityKind.None,
    // CreditNote reason — required when DocumentType=CreditNote. Determines
    // whether stock restocks (Return only) vs pure financial adjustment.
    CreditNoteReason? CreditNoteReason = null,
    // ===== Supplier-side tax invoice metadata (PurchaseInvoice / supplier-issued docs) =====
    // SupplierInvoiceNumber = partner's own running number (distinct from
    // our DocumentNumber) — needed for VAT-audit reconciliation against
    // the supplier statement. SupplierTaxInvoiceDate = the date on the
    // partner's tax invoice; controls the VAT claim period (Revenue
    // Code §82/4) when we book a bill late. CreditDays / PaymentTerms
    // capture the agreed payment window for DSO/DPO + DueDate auto-fill.
    string? SupplierInvoiceNumber = null,
    DateTime? SupplierTaxInvoiceDate = null,
    // ใบสำคัญจ่าย ติ๊ก "ใช้งานใบกำกับภาษี" → flag + snapshot สาขาผู้ขาย
    // (อ้างอิงใบกำกับซื้อ ขอเครดิตภาษีซื้อ — RD §86/4 + §86/14).
    bool HasTaxInvoiceReference = false,
    string? SupplierBranchCode = null,
    int? CreditDays = null,
    string? PaymentTerms = null,
    // Settlement basis (Payment Voucher: เครดิต vs จ่ายทันที). Cash → straight
    // to Cash/Bank, no payable/due/aging. Credit → AP + due date + aging.
    // Null → service infers per type (standalone PV defaults to Cash).
    PaymentType? PaymentType = null,
    // Unit prices entered VAT-inclusive (ราคารวมภาษี). True → back 7% VAT out.
    bool PricesIncludeVat = false,
    // ภ.พ.36 / ภ.ง.ด.54 — flag เมื่อซื้อบริการจากต่างประเทศ (Google Ads /
    // AWS / Facebook ฯลฯ). ผู้รับบริการในไทยต้อง self-assess VAT 7% และ
    // หัก WHT ตาม DTA. Default false. Apply เฉพาะ PI/Expense/PV.
    bool IsForeignService = false,
    // เงินมัดจำ/รับล่วงหน้า — Receipt/ReceiptVoucher ที่รับเงินก่อนส่งมอบ.
    // True → Cr "ขายรอรับรู้" (217xx) แทนรายได้.
    // DepositDeferredAccountCode = ผังพักรายได้ (null → 21712).
    // DepositOutputVatDeferred: false = tax point เกิดแล้ว → Cr ภาษีขาย 21911
    //   เข้า ภ.พ.30 ทันที (§78 รับชำระราคา); true = ยังไม่เกิด tax point
    //   (เงินประกัน/ยังไม่ให้บริการ) → Cr ภาษีขายรอเรียกเก็บ 21913 ยังไม่เข้า
    //   ภ.พ.30 จนกว่าจะรับรู้ (RealizeDeposit).
    bool IsDeposit = false,
    string? DepositDeferredAccountCode = null,
    bool DepositOutputVatDeferred = false,
    // Tax Point §78/§78/1 inputs (optional) — ถ้าระบุ ระบบใช้คำนวณจุดความรับผิด
    // VAT (MIN กับ payment/issue). ไม่ระบุ → fallback DocumentDate/PaymentDate.
    DateTime? DeliveryDate = null,
    DateTime? OwnershipTransferDate = null,
    DateTime? ServiceUsedDate = null,
    // เลขจอง (PMS/POS/CRM external key) — ผูกเอกสารหลายใบเข้า booking เดียว
    string? BookingNumber = null,
    // ผัง VAT ปลายทาง override — กรณีไม่เคลม VAT ลงเป็นต้นทุน/ค่าใช้จ่าย
    // (§82/5) เว้นว่าง = default ตาม completeness §86/4 (11610 / 11640)
    string? InputVatAccountCodeOverride = null,
    // ใบแจ้งหนี้/ใบกำกับภาษี (combined) — frontend ติ๊ก checkbox ที่หน้าใบแจ้งหนี้
    // แล้ว force DocumentType=TaxInvoice + flag นี้=true. เอกสารทำงานเป็นใบกำกับ
    // ภาษีเต็มรูป (VAT/ภ.พ.30/§86/4/e-Tax) แต่หัวกระดาษพิมพ์ "ใบแจ้งหนี้/ใบกำกับภาษี".
    bool CombinedInvoiceTaxInvoice = false,
    // ชื่อ + ลายเซ็นผู้จัดทำจากระบบต้นทาง (คนทำรายการจริง เช่น พนักงานหน้าร้าน) —
    // stamp ช่อง "ผู้จัดทำ/ผู้รับเงิน" (slot 0) แทน CreatedBy user (= service account).
    // ให้ priority เหนือ CreatedBy; ช่อง "ผู้มีอำนาจลงนาม" (slot 1) คงเป็นกรรมการ.
    // เหมือน integration PV/invoice. null = fallback CreatedBy user เหมือนเดิม.
    string? PreparerName = null,
    string? PreparerSignatureBase64 = null);

public record DocumentLineRequest(
    string Description,
    decimal Quantity,
    string? Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal VatRate,
    decimal WithholdingTaxRate,
    Guid? AccountId,
    // ผังบัญชีในรูป AccountCode (string) — ทางเลือกแทน AccountId. ใช้กับ AI
    // suggestion ที่คืน code ตรง ๆ + integration ที่ส่ง code มาจากต่างประเทศ.
    // Service จะ resolve code → AccountId ตอน save (ในผังของบริษัท).
    string? AccountCode = null,
    // FeedbackId จาก AI suggestion endpoint — frontend ส่งกลับมาเมื่อบรรทัด
    // นี้ได้ผังจาก AI (หรือ user แก้จาก AI). Service เปรียบ AccountCode ที่
    // user เลือกกับ AiPrimaryAnswer แล้วเรียก RecordUserChoiceAsync — ปิด
    // ลูปการสอน local distillation model (กฎเหล็ก #1).
    Guid? GlAccountAiFeedbackId = null,
    // Optional per-line project override (null → inherits Document.ProjectId)
    Guid? ProjectId = null,
    // Optional product linkage — set when the user picked a product via
    // the line-item typeahead. Stored on DocumentLine.ProductCode so reports
    // can group revenue/cost by product without re-parsing descriptions.
    string? ProductCode = null,
    // Traceability link for flexible/partial conversion — set by the
    // conversion engine, and round-tripped by the edit form so editing a
    // converted document never loses its link to the source line.
    Guid? SourceLineId = null,
    // ภาษีซื้อต้องห้าม (Non-claimable Input VAT) per ประมวลรัษฎากร §82/5.
    // Default true (เคลมได้). UI ติ๊กออก / AI suggest false สำหรับค่ารับรอง
    // / น้ำมันรถยนต์นั่ง / ใบกำกับฯ ไม่สมบูรณ์.
    bool IsVatClaimable = true,
    string? VatNonClaimableReason = null,
    // Landed cost — บรรทัดต้นทุนแฝง (ค่าขนส่ง/อากร/ประกัน) บน PI/GRN
    // ถูกเกลี่ยเข้าต้นทุนต่อหน่วยของบรรทัดสินค้า + JE เข้า 115 สินค้าคงเหลือ
    bool IsLandedCost = false,
    // ส่วนลดต่อบรรทัดเป็น "ยอดเงิน" (มาตรฐานสากล: ERP รองรับ discount ทั้ง %
    // และ amount). เมื่อระบุ > 0 ระบบใช้ค่านี้ตรง ๆ แทนการคิดจาก DiscountPercent
    // (เคสใบกำกับระบุส่วนลดเป็นบาท เช่น "ส่วนลด 600.28"). null/0 = ใช้ %.
    decimal? DiscountAmount = null,
    // VAT amount (บาท) ที่ระบุมาตรง ๆ — ใช้เมื่อบรรทัดปนของเสียภาษี 7% กับ
    // ของยกเว้น (exempt) ในก้อนเดียว ที่ VatRate เดียวแสดงไม่ได้. เมื่อระบุ
    // ระบบจะ honor ค่านี้แทนการคิด net × VatRate (กัน external/integration
    // ส่งยอดจ่ายจริงมาแล้ว NextAcc คิด VAT ใหม่จนเกิดส่วนต่าง ค้างชำระผี).
    // null = คิดตามปกติ (backward compatible).
    decimal? VatAmountOverride = null);

public record UpdateDocumentRequest(
    DateTime? DocumentDate,
    DateTime? DueDate,
    Guid? ContactId,
    string? Reference,
    string? Notes,
    List<DocumentLineRequest>? Lines,
    Guid? ProjectId = null,
    Guid? DimensionId = null,
    Guid? BankAccountId = null,
    Guid? PaymentAccountId = null,
    Guid? ExpenseCategoryId = null,
    string? CustomAppendix = null,
    string? CustomFooterNotes = null,
    string? CustomTermsAndConditions = null,
    Guid? RevenueContractId = null,
    Guid? PerformanceObligationId = null,
    string? CertificateReason = null,
    string? CertifierName = null,
    string? CertifierPosition = null,
    string? WitnessName = null,
    string? WitnessPosition = null,
    DateTime? PaymentDate = null,
    string? SupplierInvoiceNumber = null,
    DateTime? SupplierTaxInvoiceDate = null,
    // PV: ใช้งานใบกำกับภาษี (nullable → omit ไม่แตะค่าเดิม).
    bool? HasTaxInvoiceReference = null,
    string? SupplierBranchCode = null,
    string? InputVatAccountCodeOverride = null,
    int? CreditDays = null,
    string? PaymentTerms = null,
    PaymentType? PaymentType = null,
    // Nullable on update so omitting it preserves the stored value.
    bool? PricesIncludeVat = null,
    // Tax Point §78 inputs (optional, แก้ได้ตอน Draft)
    DateTime? DeliveryDate = null,
    DateTime? OwnershipTransferDate = null,
    DateTime? ServiceUsedDate = null,
    string? BookingNumber = null,
    // ===== Fields ที่เดิมแก้ไม่ได้ตอน update (เคยมีเฉพาะ Create) =====
    // ทั้งหมด nullable → omit = คงค่าเดิม. แก้ได้เฉพาะตอน Draft (service guard).
    // CreditNoteReason: เหตุผลใบลดหนี้ (§86/10). IsForeignService: ภ.พ.36/ภ.ง.ด.54.
    // IsDeposit + DepositDeferredAccountCode + DepositOutputVatDeferred: เงินมัดจำ.
    CreditNoteReason? CreditNoteReason = null,
    bool? IsForeignService = null,
    bool? IsDeposit = null,
    string? DepositDeferredAccountCode = null,
    bool? DepositOutputVatDeferred = null,
    // ใบแจ้งหนี้/ใบกำกับภาษี (combined) — แก้ได้ตอน Draft เท่านั้น.
    bool? CombinedInvoiceTaxInvoice = null,
    decimal? DepositAppliedAmount = null,
    string? DepositAppliedRef = null,
    bool? DepositAppliedDrivesJournal = null,
    bool? BuyerDeclinedTaxInvoice = null,
    // ส่วนลดท้ายบิล (จากยอดรวม) — null = คงค่าเดิม
    decimal? BillDiscountPercent = null,
    decimal? BillDiscountAmount = null);

/// <summary>เติม/แก้ใบกำกับภาษีซื้อหลังอนุมัติ — trigger reclassify 11640→11610
/// เมื่อข้อมูลครบ §86/4. ทุก field nullable: omit = คงค่าเดิม. ส่งเฉพาะที่แก้.
/// InputVatAccountCodeOverride: ตั้ง "" (empty) เพื่อล้าง override กลับ default;
/// null = ไม่แตะ; ค่าอื่น = pin ผัง VAT ปลายทางใหม่.</summary>
public record CompleteSupplierTaxInvoiceRequest(
    string? SupplierInvoiceNumber = null,
    DateTime? SupplierTaxInvoiceDate = null,
    string? SupplierBranchCode = null,
    string? InputVatAccountCodeOverride = null);

/// <summary>รับรู้รายได้จากเงินมัดจำ (ตัด "ขายรอรับรู้" 217xx → รายได้) เมื่อ
/// ส่งมอบสินค้า/บริการจริง. Amount = ฐานไม่รวม VAT ที่จะรับรู้ (รองรับบางส่วน);
/// RevenueAccountCode = ผังรายได้ปลายทาง (null → default 41000/42000);
/// FinalInvoiceId = ใบแจ้งหนี้/ใบกำกับสุดท้ายที่หักมัดจำนี้ (optional ใช้ link).</summary>
public record RealizeDepositRequest(
    decimal Amount,
    DateTime? RealizeDate = null,
    string? RevenueAccountCode = null,
    Guid? FinalInvoiceId = null);

/// <summary>คืนเงินมัดจำ (ยกเลิกการจอง) — gen reversal JE: Dr ขายรอรับรู้ +
/// Dr ภาษีขาย (ใบลดหนี้) / Cr เงินสด. Amount = ยอดรวม VAT ที่จะคืน.</summary>
public record RefundDepositRequest(
    decimal Amount,
    DateTime? RefundDate = null,
    string? Reason = null);

/// <summary>นำมัดจำไปหักกับใบแจ้งหนี้/ใบกำกับสุดท้าย (offset). ระบบรับรู้
/// รายได้จากมัดจำ (Dr ขายรอรับรู้/Cr รายได้) + ลด BalanceDue ของใบสุดท้าย
/// ตามยอดมัดจำที่จ่ายมาแล้ว (treat เป็น prepayment).</summary>
public record ApplyDepositRequest(
    Guid DepositDocumentId,
    decimal Amount,
    DateTime? ApplyDate = null);

/// <summary>สรุปมัดจำคงค้างต่อ contact (สำหรับหน้า contact + dropdown ตอน
/// ออกใบแจ้งหนี้). TotalOutstanding = มัดจำที่ยังไม่รับรู้/ไม่คืน.</summary>
public record ContactDepositSummary(
    Guid ContactId,
    decimal TotalOutstanding,
    int Count,
    IReadOnlyList<DepositSummary> Deposits);

/// <summary>สรุปเงินมัดจำคงค้างสำหรับหน้าจัดการมัดจำ (ขึ้นงบดุลเป็นหนี้สิน
/// ไม่ใช่เจ้าหนี้การค้า). OutstandingAmount = BaseAmount − RealizedAmount.</summary>
/// <summary>ขอ AI แนะนำผังบัญชีให้ทุกบรรทัดของใบสำคัญจ่ายที่กำลังสร้างจาก
/// ใบกำกับภาษีซื้อต้นทาง. SourceInvoiceId = ใบ PI/TaxInvoice ต้นทาง (ถ้ามี).
/// Lines = บรรทัด PV draft ที่ user กรอกแล้ว (description + amount + ผังปัจจุบัน
/// ถ้ามี). ส่งเป็น tempId ฝั่ง client เพื่อ map ผลกลับ.</summary>
public record SuggestPvAccountingRequest(
    Guid? SourceInvoiceId,
    string? VendorName,
    string? VendorTaxId,
    string? VendorIndustry,
    string Currency,
    IReadOnlyList<SuggestPvAccountingLine> Lines);

public record SuggestPvAccountingLine(
    string TempId,
    string Description,
    decimal Amount,
    string? CurrentAccountCode);

public record SuggestPvAccountingResponse(
    IReadOnlyList<SuggestPvAccountingLineResult> Lines,
    IReadOnlyList<string> CrossLineObservations,
    bool UsedAi);

/// <summary>ผลแนะนำต่อบรรทัด — AccountCode = ผังบัญชีที่แนะนำ; Confidence 0..1;
/// FeedbackId เก็บไว้ใส่บน DocumentLine ตอน save → ใช้บันทึก user choice ภายหลัง.</summary>
public record SuggestPvAccountingLineResult(
    string TempId,
    string? AccountCode,
    decimal? Confidence,
    IReadOnlyList<string> Alternatives,
    string? Reasoning,
    bool UsedAi,
    Guid? FeedbackId);

public record DepositSummary(
    Guid Id,
    string DocumentNumber,
    DateTime DocumentDate,
    string ContactName,
    string? ContactTaxId,
    decimal BaseAmount,
    decimal VatAmount,
    decimal TotalAmount,
    decimal RealizedAmount,
    decimal OutstandingAmount,
    DateTime? RealizedAt,
    int AgeDays,
    string Status,
    string? DeferredAccountCode,
    // หมายเลขอ้างอิง (เลขจอง/booking) — ใช้กลับรายการ/กระทบยอด
    string? Reference,
    // ภาษีขาย: false = ถึงกำหนดแล้ว (21911/ภ.พ.30); true = รอเรียกเก็บ (21913)
    bool OutputVatDeferred,
    // วันที่ภาษีขาย deferred ถูกรับรู้เข้า ภ.พ.30 (null = ยังไม่รับรู้)
    DateTime? OutputVatRecognizedAt,
    // เลขจอง (BookingNumber) — ผูกกับใบปลายทางที่ booking เดียวกัน;
    // UI ใช้ highlight + auto-suggest มัดจำเมื่อ user กรอก booking ตรงกัน
    string? BookingNumber = null);

/// <summary>ตัววินิจฉัยหน้าเงินมัดจำ — บอกว่าระบบ "เห็น" อะไรบ้าง เพื่อหา
/// สาเหตุเมื่อ dashboard โชว์ 0 (ไม่มีบัญชีมัดจำในผัง / ไม่มี JE เครดิต /
/// เอกสารไม่ติดธง). แสดงในหน้าเมื่อ list ว่าง.</summary>
public record DepositDiagnostics(
    int MatchedAccountCount,
    List<DepositAccountInfo> MatchedAccounts,
    int NativeDepositDocCount,      // Documents.IsDeposit = true (non-draft/void)
    int CreditLineCount,            // JE lines Cr บัญชีมัดจำ (Posted, ไม่ reverse)
    decimal TotalCreditNet,         // ΣCr − ΣDr รวมทุกบัญชีมัดจำ
    int DocLinkedCreditCount,       // credit lines ที่มี SourceDocumentId
    int DocLessCreditCount,         // credit lines ที่ไม่มี SourceDocumentId
    string Hint);                   // คำแนะนำภาษาไทยว่าติดตรงไหน

public record DepositAccountInfo(string AccountCode, string AccountName, decimal NetCredit);

/// <summary>สรุปเอกสารที่ภาษีซื้อค้างอยู่ที่ 11640 "ยังไม่ถึงกำหนด" รอใบกำกับ
/// ครบ §86/4. MonthsLeft = เดือนเหลือก่อนหมดสิทธิเคลม (§82/3 6 เดือนนับจาก
/// เดือนใบกำกับ); IsExpired = เกิน 6 เดือนแล้ว (เคลมไม่ได้ ต้องลงเป็นต้นทุน).</summary>
public record UndueInputVatSummary(
    Guid Id,
    string DocumentNumber,
    DateTime DocumentDate,
    string SupplierName,
    string? SupplierTaxId,
    decimal VatAmount,
    int AgeDays,
    int MonthsLeft,
    bool IsExpired,
    IReadOnlyList<string> MissingFields);

/// <summary>เดือน/ปีที่มีเอกสารจริง (สำหรับ dropdown กรองตามงวด) + จำนวนเอกสาร.
/// Year เก็บ ค.ศ. (UI แปลงเป็น พ.ศ. เอง).</summary>
public record DocumentPeriod(int Year, int Month, int Count);

public record DocumentResponse(
    Guid Id,
    string DocumentNumber,
    DocumentType DocumentType,
    DocumentStatus Status,
    DateTime DocumentDate,
    DateTime? DueDate,
    ContactBrief Contact,
    decimal SubTotal,
    decimal DiscountAmount,
    decimal BillDiscountPercent,
    decimal BillDiscountAmount,
    decimal VatAmount,
    decimal WithholdingTaxAmount,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal BalanceDue,
    string? Reference,
    string? Notes,
    List<DocumentLineResponse> Lines,
    DateTime CreatedAt,
    // e-Tax info — set when this document has been processed via the e-Tax pipeline.
    // The UI uses these to surface the "Download PDF/A-3 (with embedded XML)" action,
    // which is required for e-Tax by Email compliance — printing strips the XML payload.
    Guid? EtaxInvoiceId = null,
    EtaxStatus? EtaxStatus = null,
    Guid? ProjectId = null,
    string? ProjectCode = null,
    string? ProjectName = null,
    Guid? DimensionId = null,
    Guid? BankAccountId = null,
    string? BankAccountName = null,
    Guid? PaymentAccountId = null,
    string? PaymentAccountName = null,
    Guid? ExpenseCategoryId = null,
    string? ExpenseCategoryName = null,
    string? CustomAppendix = null,
    string? CustomFooterNotes = null,
    string? CustomTermsAndConditions = null,
    Guid? RevenueContractId = null,
    Guid? PerformanceObligationId = null,
    Guid? RelatedDocumentId = null,
    string? CertificateReason = null,
    string? CertifierName = null,
    string? CertifierPosition = null,
    string? WitnessName = null,
    string? WitnessPosition = null,
    DateTime? PaymentDate = null,
    // ERP upgrade — OCR RD compliance + aging cache
    decimal? OcrConfidenceScore = null,
    RdComplianceStatus RdComplianceStatus = RdComplianceStatus.Pending,
    string? RdComplianceIssuesJson = null,
    bool OcrTenantMismatchFlag = false,
    int? AgingDays = null,
    // Days a document has sat in a non-terminal status (Draft/WaitingApproval/
    // Approved/Sent/Partially-paid) beyond the stale threshold — null when not
    // stale. Surfaces "forgotten" documents (e.g. a PO left 3 months).
    int? StaleDays = null,
    // Multi-currency — Currency is doc's denomination; ExchangeRate is THB per
    // 1 unit of Currency captured at Create. Both default to ("THB", 1).
    string Currency = "THB",
    decimal ExchangeRate = 1m,
    // Sensitivity — None for the regular sales/purchase stream. Payroll vouchers
    // and other restricted records stamp this. When the requesting user lacks
    // the matching permission the API returns a stub with IsRedacted=true and
    // amounts/contact/notes blanked out so integration targets know the record
    // exists but is hidden — they should not 404 or pretend it isn't there.
    SensitivityKind Sensitivity = SensitivityKind.None,
    bool IsRedacted = false,
    string? RedactedReason = null,
    // CreditNote reason — set when DocumentType=CreditNote so the UI can
    // display "ลดราคา" / "คืนสินค้า" etc. Drives whether ApplyStockMovements
    // restocks on approval (only Return does).
    CreditNoteReason? CreditNoteReason = null,
    // Supplier-side tax invoice metadata for PurchaseInvoice rows.
    string? SupplierInvoiceNumber = null,
    DateTime? SupplierTaxInvoiceDate = null,
    // PV flag + supplier branch snapshot (RD §86/4 + §86/14).
    bool HasTaxInvoiceReference = false,
    string? SupplierBranchCode = null,
    int? CreditDays = null,
    string? PaymentTerms = null,
    // Settlement basis — Cash (จ่ายทันที) vs Credit (เครดิต). Drives whether
    // the UI shows a due date / outstanding balance for a Payment Voucher.
    PaymentType? PaymentType = null,
    bool PricesIncludeVat = false,
    // ภ.พ.36 / ภ.ง.ด.54 — ซื้อบริการจากต่างประเทศ. Echo กลับมาเพื่อ form hydration.
    bool IsForeignService = false,
    // ===== Conversion lineage =====
    // Source-side view (this doc was converted from another): RelatedDocumentId
    // already carries the upstream id; the populated brief lets the UI render
    // "แปลงมาจาก QT-0042" without a second round-trip.
    DocumentBrief? RelatedDocument = null,
    // Target-side view (other docs created from this one): list of children
    // spawned via ConvertCoreAsync — populated server-side from the
    // (CompanyId, RelatedDocumentId) index so the source doc can render
    // "ใบที่ออกต่อจากเอกสารนี้" without N+1.
    List<DocumentBrief>? ConvertedToDocuments = null,
    // 0..100 — share of source quantity consumed by child docs across
    // all axes (Delivery + Billing). Null when this doc has no source
    // lines. Used to badge "✓ Fully converted" / "◐ 60% converted".
    decimal? ConversionCompletionPercent = null,
    // Convenience aggregate of the above — "None" / "Partial" / "Full" — so
    // the UI can pick a badge color without computing thresholds itself.
    string? ConversionStatus = null,
    // ===== Project-cost booking summary =====
    // Populated by GetDocumentAsync from the (DocumentId, DocumentLineId)
    // links on ProjectCostEntry — lets the UI show "🏗️ ลงโครงการแล้ว
    // 3 รายการ / ฿15,400" and the per-project breakdown without an extra
    // round-trip.
    bool HasProjectCostEntries = false,
    int ProjectCostEntryCount = 0,
    decimal ProjectCostBookedAmount = 0,
    List<ProjectCostBrief>? BookedProjects = null,
    // ===== Lifecycle =====
    // Unified "what's the state of this doc's purpose?" view, derived
    // from Status + ConversionStatus + BalanceDue. Lets the UI show a
    // single clear badge per doc instead of asking the user to mentally
    // combine 3 signals. Values:
    //   • "Open"            — still has work to do
    //   • "PartiallyDone"   — converted or settled in part
    //   • "Done"            — purpose fulfilled (paid / fully converted / approved one-shot)
    //   • "Cancelled"       — voided / rejected
    string? LifecycleStatus = null,
    // Short Thai phrase explaining the lifecycle state in context, e.g.
    // "✓ จ่ายแล้ว", "✓ แปลงเป็น PI-001", "◐ แปลงไป 60%", "× ยกเลิก".
    // Picked up directly by the badge tooltip + list column.
    string? LifecycleReason = null,
    // ===== Undue Input VAT (§82/3) =====
    // True เมื่อตอน approve ใบกำกับยังไม่ครบ §86/4 → VAT ลง 11640 "ภาษีซื้อ
    // ยังไม่ถึงกำหนด" แทน 11610. UI โชว์ป้าย "⏳ ภาษีซื้อรอใบกำกับครบ" + ปุ่ม
    // "เติมข้อมูลใบกำกับ" (เรียก CompleteSupplierTaxInvoiceAsync).
    bool InputVatPostedAsUndue = false,
    // เมื่อ != null = ระบบ reclassify 11640→11610 แล้ว (ใบกำกับครบ) ณ วันนี้ —
    // ภ.พ.30 ใช้เดือนนี้เป็น tax point. null + InputVatPostedAsUndue=true =
    // ยังค้าง 11640 รอเติมข้อมูล.
    DateTime? InputVatBecameClaimableAt = null,
    string? InputVatAccountCodeOverride = null,
    // ===== เงินมัดจำ/รับล่วงหน้า =====
    bool IsDeposit = false,
    decimal DepositRealizedAmount = 0m,
    DateTime? DepositRealizedAt = null,
    string? DepositDeferredAccountCode = null,
    bool DepositOutputVatDeferred = false,
    DateTime? DepositOutputVatRecognizedAt = null,
    // ===== Tax Point §78 + Retention §87/3 + §65 ตรี =====
    DateTime? TaxPointDate = null,
    DateTime? RetentionUntil = null,
    // ยอดรายจ่ายต้องห้ามที่ต้องบวกกลับ ภ.ง.ด.50 + รายละเอียด rule (JSON)
    decimal NonDeductibleAmount = 0m,
    string? NonDeductibleRuleJson = null,
    string? LateReason = null,
    // Tax Point §78 input fields (echoed back สำหรับฟอร์ม hydration)
    DateTime? DeliveryDate = null,
    DateTime? OwnershipTransferDate = null,
    DateTime? ServiceUsedDate = null,
    // Deposit lifecycle status
    decimal DepositRefundedAmount = 0m,
    DateTime? DepositRefundedAt = null,
    string? DepositRefundReason = null,
    Guid? DepositAppliedToDocumentId = null,
    string? BookingNumber = null,
    // ใบแจ้งหนี้/ใบกำกับภาษี (combined) — echo กลับมาเพื่อ hydrate checkbox
    // ตอนแก้ไข + ให้ UI ติดป้าย/หัวกระดาษถูก. type ยังเป็น TaxInvoice.
    bool CombinedInvoiceTaxInvoice = false,
    // ยอดมัดจำที่นำมาหักบนใบรับเงินนี้ (display) — ยอดรวม (TotalAmount) ยังเป็น
    // ยอดขายเต็ม, รับสุทธิ = TotalAmount − DepositAppliedAmount. ให้ list โชว์
    // "รับสุทธิ" กันงงเมื่อมีหักมัดจำ
    decimal DepositAppliedAmount = 0m);

public record ProjectCostBrief(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    int EntryCount,
    decimal Amount);

public record DocumentBrief(
    Guid Id,
    string DocumentNumber,
    DocumentType DocumentType,
    DocumentStatus Status,
    DateTime DocumentDate,
    decimal TotalAmount);

public record DocumentLineResponse(
    Guid Id,
    int LineOrder,
    string Description,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal DiscountAmount,
    decimal Amount,
    decimal VatRate,
    decimal VatAmount,
    decimal WithholdingTaxRate,
    decimal WithholdingTaxAmount,
    Guid? AccountId = null,
    Guid? ProjectId = null,
    string? ProductCode = null,
    Guid? SourceLineId = null,
    string? ProjectCode = null,
    string? ProjectName = null,
    // Set when this line has been auto-spawned into a ProjectCostEntry on
    // approval (via SyncProjectCostEntriesAsync). UI flags the line
    // "🏗️ ลงโครงการแล้ว" so the user knows the cost has been booked.
    Guid? ProjectCostEntryId = null,
    bool HasProjectCostEntry = false,
    // ภาษีซื้อต้องห้าม flag + เหตุผล — UI แสดง checkbox + tooltip
    bool IsVatClaimable = true,
    string? VatNonClaimableReason = null,
    bool IsLandedCost = false,
    // AccountCode (string) คู่กับ AccountId — ให้ frontend ใช้ matched code
    // ใน per-line picker โดยไม่ต้อง round-trip ลง /chart-of-accounts ทุกครั้ง
    string? AccountCode = null,
    // FeedbackId ของ AI suggestion ที่เคยให้ผังบรรทัดนี้ — frontend ต้อง
    // round-trip กลับมาตอน update เพื่อให้ backend ปิดลูปการสอน local model
    Guid? GlAccountAiFeedbackId = null,
    // ชื่อผังบัญชี (denormalized) — ใช้ใน reclassify modal แสดงผังเดิม +
    // detail table ระบุชื่อบัญชีคู่กับ code
    string? AccountName = null);

// ===== Flexible / partial document conversion =====

/// <summary>Convert only a chosen subset of a source document's lines, each
/// at a chosen quantity — e.g. split one PO into several delivery notes /
/// invoices. <see cref="Lines"/> with quantity 0 are ignored.</summary>
public record PartialConvertRequest(
    List<PartialConvertLineRequest> Lines,
    DateTime? DocumentDate = null,
    DateTime? DueDate = null);

public record PartialConvertLineRequest(Guid SourceLineId, decimal Quantity);

/// <summary>Per-line fulfilment snapshot of a source document — how much of
/// each line has already been carried forward into delivery notes vs.
/// billing documents, and how much remains.</summary>
public record DocumentFulfillmentResponse(
    Guid DocumentId,
    string DocumentNumber,
    DocumentType DocumentType,
    bool SupportsDelivery,
    bool SupportsBilling,
    List<DocumentLineFulfillmentResponse> Lines);

public record DocumentLineFulfillmentResponse(
    Guid LineId,
    int LineOrder,
    string Description,
    string Unit,
    decimal OrderedQuantity,
    decimal DeliveredQuantity,
    decimal DeliveryRemaining,
    decimal BilledQuantity,
    decimal BillingRemaining,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal VatRate,
    decimal WithholdingTaxRate,
    Guid? AccountId,
    Guid? ProjectId,
    string? ProductCode);

public record ContactBrief(Guid Id, string Name, string? TaxId);

public record ApproveDocumentRequest(string? Notes, bool AcknowledgeWarnings = false);

/// <summary>เปลี่ยนผังบัญชีของ DocumentLine.AccountId หลัง approved
/// — ระบบจะ post reclassify-JE (Dr ผังใหม่/Cr ผังเก่า) อัตโนมัติ.</summary>
public record ReclassifyLineRequest(Guid LineId, Guid NewAccountId, string? Reason);

/// <summary>เปลี่ยน "แหล่งเงิน" (บัญชี Cr) ของเอกสารจ่าย/รับสดหลัง approved —
/// ระบุแหล่งเงินใหม่ทางใดทางหนึ่ง: NewBankAccountId (บัญชีธนาคาร) หรือ
/// NewPaymentAccountId (ChartOfAccount เงินสด/อื่น ๆ). ระบบ post correcting-JE
/// (Dr ผังเก่า/Cr ผังใหม่) อัตโนมัติ.</summary>
public record ReclassifyPaymentSourceRequest(
    Guid? NewBankAccountId, Guid? NewPaymentAccountId, string? Reason);

/// <summary>Returned on the first approve attempt when pre-approval checks
/// produced soft warnings (legal/correct but unusual). Operator reviews the
/// list and retries with AcknowledgeWarnings=true to proceed. Hard errors
/// (data corruption / illegal state) still throw inline — they're never
/// surfaced as warnings.</summary>
public record ApprovalWarningsResponse(
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ApprovalWarningAiHintDto>? AiHints = null);

/// <summary>
/// AI-generated hint for one approval warning. Paired with Warnings[i]
/// by index. Surfaced in the UI alongside the warning text — operator
/// sees Primary recommendation + actions + risks without needing to
/// think through the warning from scratch. NULL when AI is disabled
/// or unreachable.
/// </summary>
public record ApprovalWarningAiHintDto(
    string Primary,
    decimal Confidence,
    string? Reasoning,
    IReadOnlyList<string> SuggestedActions,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> ComplianceFlags,
    Guid? FeedbackId,
    bool UsedAi);

// ===== Contact =====
public record CreateContactRequest(
    [property: Required, StringLength(200)] string Name,
    [property: RegularExpression(@"^\d{13}$", ErrorMessage = "เลขประจำตัวผู้เสียภาษีต้องเป็นตัวเลข 13 หลัก")] string? TaxId,
    [property: RegularExpression(@"^\d{5}$", ErrorMessage = "รหัสสาขาต้องเป็นตัวเลข 5 หลัก")] string? BranchCode,
    ContactType? ContactType,
    bool IsCustomer,
    bool IsSupplier,
    string? Address,
    string? Phone,
    string? Email,
    string? ContactPerson,
    // Structured address (optional — recommended for e-Tax compliance)
    string? BranchName = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? Moo = null,
    string? StreetName = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? CountryCode = null,
    // ตั้งค่าการบันทึกบัญชี — per-contact GL overrides. Null leaves the
    // system default in effect (FindAccountAsync "113" / "212" prefix).
    Guid? DefaultArAccountId = null,
    Guid? DefaultApAccountId = null,
    Guid? DefaultIrGrAccountId = null,
    decimal? CreditLimit = null,
    // ลูกค้ารายนี้ออกใบกำกับภาษีเสมอ → pre-select TaxInvoice ตอนสร้างเอกสาร
    bool DefaultIssueTaxInvoice = false,
    // เครดิตเทอมต่อลูกค้า (วันเครดิต + ป้ายกำกับ)
    int? PaymentDueDays = null,
    string? PaymentTerms = null);

public record UpdateContactRequest(
    [property: StringLength(200)] string? Name,
    [property: RegularExpression(@"^\d{13}$", ErrorMessage = "เลขประจำตัวผู้เสียภาษีต้องเป็นตัวเลข 13 หลัก")] string? TaxId,
    [property: RegularExpression(@"^\d{5}$", ErrorMessage = "รหัสสาขาต้องเป็นตัวเลข 5 หลัก")] string? BranchCode,
    ContactType? ContactType,
    bool? IsCustomer,
    bool? IsSupplier,
    string? Address,
    string? Phone,
    string? Email,
    string? ContactPerson,
    bool? IsActive,
    string? BranchName = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? Moo = null,
    string? StreetName = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? CountryCode = null,
    Guid? DefaultArAccountId = null,
    Guid? DefaultApAccountId = null,
    Guid? DefaultIrGrAccountId = null,
    decimal? CreditLimit = null,
    bool? DefaultIssueTaxInvoice = null,
    int? PaymentDueDays = null,
    string? PaymentTerms = null);

/// <summary>
/// Result of attempting to delete a contact. May be a hard delete or
/// a soft deactivation if the contact has linked accounting records.
/// </summary>
public record ContactDeleteResult(
    bool Deleted,            // true = removed; false = deactivated only
    bool Deactivated,        // true if the contact was set to inactive
    int LinkedDocumentsCount,
    int LinkedWhtCount,
    string Message);

public record ContactResponse(
    Guid Id,
    string Name,
    string? TaxId,
    string? BranchCode,
    ContactType ContactType,
    bool IsCustomer,
    bool IsSupplier,
    string? Address,
    string? Phone,
    string? Email,
    string? ContactPerson,
    bool IsActive,
    string? BranchName = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? Moo = null,
    string? StreetName = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? CountryCode = "TH",
    int LoyaltyPoints = 0,
    DateTime? LastVisitAt = null,
    int TotalVisitCount = 0,
    // Per-contact GL overrides — null means "use system default
    // (113/212 prefix)". UI shows the account labels too for display.
    Guid? DefaultArAccountId = null,
    string? DefaultArAccountCode = null,
    string? DefaultArAccountName = null,
    Guid? DefaultApAccountId = null,
    string? DefaultApAccountCode = null,
    string? DefaultApAccountName = null,
    Guid? DefaultIrGrAccountId = null,
    string? DefaultIrGrAccountCode = null,
    string? DefaultIrGrAccountName = null,
    decimal? CreditLimit = null,
    bool DefaultIssueTaxInvoice = false,
    int? PaymentDueDays = null,
    string? PaymentTerms = null);

/// <summary>Request body for the smart-parse endpoint — paste address text, get structured fields.</summary>
public record ParseAddressRequest(string Address);

public record ParsedAddressResponse(
    string? BuildingNumber,
    string? BuildingName,
    string? StreetName,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode,
    string? Moo = null);

/// <summary>ค่าเริ่มต้นอัตโนมัติ ระบบวิเคราะห์จากข้อมูลผู้ติดต่อ</summary>
public record ContactSmartDefaults(
    ContactType ContactType,
    string ContactTypeLabel,
    TaxType SuggestedTaxFormType,
    string SuggestedTaxFormLabel,
    DocumentType? SuggestedDocumentType,
    string? SuggestedDocumentTypeLabel,
    decimal DefaultWhtRate,
    string DefaultIncomeTypeCode,
    string DefaultIncomeTypeLabel);

// ===== Payment =====
public record CreatePaymentRequest(
    Guid DocumentId,
    DateTime PaymentDate,
    decimal Amount,
    PaymentMethod PaymentMethod,
    string? Reference,
    string? BankAccount,
    string? Notes,
    /// <summary>Optional — overrides the source document's BankAccountId for
    /// THIS payment only. Use when the cheque actually cleared through a
    /// different bank than the invoice originally targeted; the GL hit and
    /// bank-balance update follow this override, not doc.BankAccountId.</summary>
    Guid? OverrideBankAccountId = null,
    /// <summary>Optional — fund this payment from a specific GL account that
    /// isn't a bank (เงินทดรองกรรมการ / เงินสดย่อย / clearing). When set, the
    /// auto-posted JE's cash side hits this account. Overrides both the bank's
    /// linked GL and the default cash account.</summary>
    Guid? OverridePaymentAccountId = null,
    /// <summary>Optional — WHT withheld on THIS installment. Null = the
    /// service computes a proportional default: Amount / Document.TotalAmount
    /// × Document.WithholdingTaxAmount. Use the override when the customer's
    /// WHT certificate shows a different amount than the proportional split
    /// (e.g. they withhold the full amount on the first installment).
    /// Cumulative WHT across all payments must not exceed the source's
    /// WithholdingTaxAmount.</summary>
    decimal? WithholdingTaxAmount = null,
    /// <summary>Optional — overrides the source document's ProjectId
    /// for THIS payment. Used when one document is split across
    /// project payments (advance booked to Project A; final to
    /// Project B). The auto-posted JE picks this up first; falls
    /// back to Document.ProjectId.</summary>
    Guid? ProjectId = null,
    /// <summary>Multi-document allocation — when set with 1+ rows,
    /// the legacy DocumentId field is ignored and the payment is
    /// split across these target documents. SUM(AllocatedAmount) must
    /// be ≤ Amount; the remainder lands as UnappliedCredit on the
    /// response. WHT is allocated proportionally when individual
    /// rows omit WithholdingTaxAmount. Each AllocatedAmount must be
    /// ≤ the target document's current BalanceDue.</summary>
    List<PaymentAllocationRequest>? Allocations = null,
    /// <summary>Optional — base64 signature image (data-url or bare) to print
    /// in the "ผู้จ่ายเงิน" slot of the PV PDF. Overrides the CreatedBy user's
    /// stored signature for THIS payment. Used by integrations whose service
    /// account has no signature on file. Null = use User.SignatureImageBase64
    /// of CreatedBy.</summary>
    string? PayerSignatureBase64 = null,
    /// <summary>Optional — display name printed under the payer signature
    /// image. Defaults to CreatedBy user's FullName when null.</summary>
    string? PayerSignatureName = null,
    /// <summary>อัตราแลกเปลี่ยน ณ วันชำระจริง (เฉพาะเอกสารสกุลต่างประเทศ) —
    /// ต่างจาก rate เอกสาร → ระบบ post กำไร/ขาดทุนจากอัตราแลกเปลี่ยน
    /// realized อัตโนมัติ (42600/54950). Null = ใช้ rate เอกสารตามเดิม.</summary>
    decimal? ExchangeRate = null,
    /// <summary>ค่าธรรมเนียมที่ถูกหักจากยอดโอน (marketplace/gateway/ธนาคาร)
    /// — Amount คือเงินสุทธิที่เข้าบัญชี; เอกสารถูกล้างที่ Amount+FeeAmount.
    /// ใช้ได้เฉพาะเอกสารฝั่งขาย (Invoice/TaxInvoice/DebitNote).</summary>
    decimal? FeeAmount = null,
    /// <summary>ผังค่าธรรมเนียม — null = ระบบหา 53xxx/ชื่อ "ค่าธรรมเนียม".</summary>
    Guid? FeeAccountId = null,
    /// <summary>ออก "ใบเสร็จรับเงิน" (Document) เป็นหลักฐานคู่กับการชำระนี้ —
    /// default true (ฝั่งขาย Invoice/TaxInvoice/DebitNote). ใบเสร็จนี้ผูกกับ
    /// Payment, ลงวันที่ชำระ, ไม่ลง JE ซ้ำ (Payment ลง Dr เงินสด/Cr ลูกหนี้ แล้ว)
    /// และไม่คิด VAT ซ้ำ (VAT อยู่ที่ใบกำกับ). null = true.</summary>
    bool? IssueReceiptDocument = null);

public record PaymentAllocationRequest(
    Guid DocumentId,
    decimal AllocatedAmount,
    decimal? WithholdingTaxAmount = null,
    string? Note = null);

public record PaymentAllocationResponse(
    Guid Id,
    Guid PaymentId,
    Guid DocumentId,
    string DocumentNumber,
    DocumentType DocumentType,
    decimal AllocatedAmount,
    decimal WithholdingTaxAmount,
    string? Note);

public record PaymentResponse(
    Guid Id,
    string PaymentNumber,
    Guid DocumentId,
    DateTime PaymentDate,
    decimal Amount,
    PaymentMethod PaymentMethod,
    string? Reference,
    string? BankAccount,
    Guid? BankAccountId,
    string? Notes,
    DateTime CreatedAt,
    /// <summary>Multi-document allocation rows. Empty for legacy
    /// single-doc payments (and the legacy DocumentId then carries
    /// the settled doc id).</summary>
    List<PaymentAllocationResponse>? Allocations = null,
    /// <summary>Amount − SUM(Allocations.AllocatedAmount). Positive
    /// when the customer overpaid (carry-forward credit); zero
    /// otherwise. Doesn't itself create a credit-note; the operator
    /// can later attach the unapplied amount to a new invoice via
    /// /payments/{id}/allocations.</summary>
    decimal UnappliedCredit = 0,
    /// <summary>True when this payment carries a per-request payer signature
    /// override (Payment.PayerSignatureBase64). The blob itself isn't echoed
    /// in the response — only its presence — to keep payloads compact and
    /// avoid leaking signature images to clients that don't render them.</summary>
    bool HasPayerSignature = false,
    string? PayerSignatureName = null);


public record WriteOffBadDebtRequest(string? Reason);
public record BatchConvertRequest(List<Guid> DocumentIds);
