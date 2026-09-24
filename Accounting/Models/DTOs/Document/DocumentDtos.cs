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
    // ใบลดหนี้/เพิ่มหนี้: ฝั่งภาษีที่ผู้ใช้เลือกชัดเจนตั้งแต่สร้าง (true = ฝั่งซื้อ)
    // — ระบบจะได้ไม่ต้องเดาตอนอนุมัติ (เดาผิด = JE ลงผิดฝั่งถาวร ยอดผิดฝั่งใน ภ.พ.30)
    bool? CnDnPurchaseSideOverride = null,
    // หักเงินมัดจำบนใบรับเงินสุดท้าย (display-only — ไม่กระทบ JE): ยอดมัดจำ
    // ที่หัก (รวม VAT) + เลขใบมัดจำอ้างอิง → renderer แสดง "หักเงินมัดจำ" +
    // "ยอดชำระสุทธิ". line ยังเป็นการขายเต็มจำนวน (ห้าม line ติดลบ)
    decimal? DepositAppliedAmount = null,
    string? DepositAppliedRef = null,
    bool? DepositAppliedDrivesJournal = null,
    bool? BuyerDeclinedTaxInvoice = null,
    // ขายเงินสด: ออก "ใบกำกับภาษี/ใบเสร็จรับเงิน" ใบเดียว (ไม่ตั้งลูกหนี้ + ไม่ออก
    // ใบเสร็จหลักฐานแยก). TaxInvoice + IssuedAsCashReceipt → AutoPost ลงแบบเงินสด
    // (Dr เงินสด/Cr รายได้+VAT + กลับมัดจำถ้ามี), e-Tax T03, หัว "ใบเสร็จรับเงิน/
    // ใบกำกับภาษี", สถานะ Paid ทันที. ถ้ามีมัดจำต้องส่ง DepositAppliedDrivesJournal=true
    bool? IssuedAsCashReceipt = null,
    // เจตนา "รับเงินครบแล้ว ณ วันออก" (โหมด tax_paid ของใบแปลง) — เดินสายเครดิต
    // เต็มตามเดิม แค่บันทึกเจตนาให้หัวใบร่างพิมพ์รวม + ฟอร์ม echo กลับได้
    bool? PaidOnIssue = null,
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
    string? Currency = "THB",
    decimal? ExchangeRate = null,
    // ===== Sensitivity classification (optional) =====
    // Internal callers (e.g. PayrollService) pass Sensitivity to gate the
    // resulting document behind the matching role. External clients leave it
    // None (the default) and the document is publicly visible within the company.
    SensitivityKind Sensitivity = SensitivityKind.None,
    // CreditNote reason — required when DocumentType=CreditNote. Determines
    // whether stock restocks (Return only) vs pure financial adjustment.
    CreditNoteReason? CreditNoteReason = null,
    // DebitNote reason — required when DocumentType=DebitNote (§86/9 บังคับระบุสาเหตุ)
    DebitNoteReason? DebitNoteReason = null,
    // ===== Supplier-side tax invoice metadata (PurchaseInvoice / supplier-issued docs) =====
    // SupplierInvoiceNumber = partner's own running number (distinct from
    // our DocumentNumber) — needed for VAT-audit reconciliation against
    // the supplier statement. SupplierTaxInvoiceDate = the date on the
    // partner's tax invoice; controls the VAT claim period (Revenue
    // Code §82/4) when we book a bill late. CreditDays / PaymentTerms
    // capture the agreed payment window for DSO/DPO + DueDate auto-fill.
    string? SupplierInvoiceNumber = null,
    DateTime? SupplierTaxInvoiceDate = null,
    // งวด ภ.พ.30 ที่ตั้งใจเคลมภาษีซื้อ (push จากตัวเอกสาร) — "yyyy-MM",
    // "" = ล้างกลับเป็นปกติ (เคลมตามเดือนเอกสาร), null = ไม่แตะ.
    // กติกาใครชนะใคร: บรรทัดในรายงานจริงชนะเสมอ (มีบรรทัดแล้วต้องไปติ๊กออก
    // จากรายงานงวดนั้นก่อน) / flow ใบกำกับไม่ครบ 11640 ชนะเจตนา / ช่องนี้
    // เป็นแค่ "เจตนาเริ่มต้น" ให้ generation หยิบเข้างวดที่เลือก
    string? InputVatClaimPeriod = null,
    // ใบสำคัญจ่าย ติ๊ก "ใช้งานใบกำกับภาษี" → flag + snapshot สาขาผู้ขาย
    // (อ้างอิงใบกำกับซื้อ ขอเครดิตภาษีซื้อ — RD §86/4 + §86/14).
    bool HasTaxInvoiceReference = false,
    string? SupplierBranchCode = null,
    int? CreditDays = null,
    string? PaymentTerms = null,
    /// <summary>ภาษาของเอกสารใบนี้เมื่อพิมพ์/ส่งออก ("th"/"en") — null = ใช้ค่า
    /// ตั้งต้นของบริษัท (CompanySettings.DocumentLanguage)</summary>
    string? DocumentLanguage = null,
    // Settlement basis (Payment Voucher: เครดิต vs จ่ายทันที). Cash → straight
    // to Cash/Bank, no payable/due/aging. Credit → AP + due date + aging.
    // Null → service infers per type (standalone PV defaults to Cash).
    PaymentType? PaymentType = null,
    // Unit prices entered VAT-inclusive (ราคารวมภาษี). True → back 7% VAT out.
    bool PricesIncludeVat = false,
    // ชื่อทางการค้า/แบรนด์ที่ใช้ออกใบนี้ (null = ใช้ชื่อบริษัท). ผลต่อ "หน้าตา"
    // เท่านั้น — ไม่กระทบบัญชี/ภาษี/เลขที่. เอกสารที่กฎหมายบังคับชื่อผู้ประกอบการ
    // จดทะเบียน แบรนด์จะลงได้แค่โลโก้+บรรทัดรอง (Helpers.DocumentIssuerIdentity)
    Guid? BrandId = null,
    // สถานประกอบการ (สาขา) ที่ออกใบนี้ — **null = พฤติกรรมเดิมทุกประการ**
    // (ใช้ Company.BranchCode/ที่อยู่บริษัท) กิจการสาขาเดียวไม่ต้องส่งมาเลย
    // กระทบ "รหัสสาขาบนกระดาษ + ที่อยู่ + TXID ของ e-Tax" ตาม §86/4(2) + ป.86/2542
    Guid? BranchId = null,
    // รูปแบบ (เทมเพลต) ที่เลือกตอนออกใบ — null = ใช้ตั้งต้นของชนิดเอกสาร
    // (ตอนแก้ไข: Guid.Empty = กลับไปใช้ตั้งต้น เหมือนกติกาของ BrandId)
    Guid? DocumentTemplateId = null,
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
    string? PreparerSignatureBase64 = null,
    // รอบ 193 (เจ้าของข้อ 1/4): ยอดชำระจริงที่ต่างจากยอดเอกสาร (ใบกำกับ 536 · จ่าย 438) — null = จ่ายเต็มตามยอด
    // ใบสำคัญจ่าย: ส่วนต่างต้องอธิบายด้วย adjusting lines (Helpers/PaymentSettlementAdjustment) ก่อนอนุมัติ
    decimal? ActualPaidAmount = null,
    // รอบ 193 (เจ้าของข้อ 8): ผลต่างจากการปัดเศษ (|x| < 1) — SubTotal = Σ บรรทัด + ค่านี้ · null/0 = ไม่มี
    decimal? RoundingAdjustment = null);

// ⚠️ **ห้ามเพิ่ม `OriginModule` กลับเข้ามาใน request นี้**
// เดิมเคยอยู่ตรงนี้ แล้วถูกใช้ตัดสินว่าเอกสาร "นับโควตาไหม"
// (`DocumentQuotaPolicy.Classify(..., fromLodging)`) ⇒ ผู้ใช้ที่เรียก REST API
// ด้วย token ของตัวเองส่ง `"originModule":"Lodging"` มาทุกใบ ก็ไม่กินโควตาเลย
// ตลอดกาล และไม่เกิดค่าส่วนเกินด้วย — **ค่าที่ client คุมได้ ห้ามใช้ตัดสินเรื่องเงิน**
// ตอนนี้เป็น **พารามิเตอร์ของเมธอด** `IDocumentService.CreateDocumentAsync(..., originModule)`
// ซึ่ง model binding เอื้อมไม่ถึงโดยโครงสร้าง (ปลอดภัยกว่าการให้ controller ล้างเอง
// ซึ่งวันหนึ่งจะมี controller ตัวใหม่ที่ลืมล้าง — defect class "แก้ตัวเดียว เหลือที่เหลือ")

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
    // งวด ภ.พ.30 ที่ตั้งใจเคลมภาษีซื้อ (push จากตัวเอกสาร) — "yyyy-MM",
    // "" = ล้างกลับเป็นปกติ (เคลมตามเดือนเอกสาร), null = ไม่แตะ.
    // กติกาใครชนะใคร: บรรทัดในรายงานจริงชนะเสมอ (มีบรรทัดแล้วต้องไปติ๊กออก
    // จากรายงานงวดนั้นก่อน) / flow ใบกำกับไม่ครบ 11640 ชนะเจตนา / ช่องนี้
    // เป็นแค่ "เจตนาเริ่มต้น" ให้ generation หยิบเข้างวดที่เลือก
    string? InputVatClaimPeriod = null,
    // PV: ใช้งานใบกำกับภาษี (nullable → omit ไม่แตะค่าเดิม).
    bool? HasTaxInvoiceReference = null,
    string? SupplierBranchCode = null,
    string? InputVatAccountCodeOverride = null,
    int? CreditDays = null,
    string? PaymentTerms = null,
    /// <summary>ภาษาของเอกสารใบนี้เมื่อพิมพ์/ส่งออก ("th"/"en") — null = ใช้ค่า
    /// ตั้งต้นของบริษัท (CompanySettings.DocumentLanguage)</summary>
    string? DocumentLanguage = null,
    PaymentType? PaymentType = null,
    // Nullable on update so omitting it preserves the stored value.
    bool? PricesIncludeVat = null,
    // ชื่อทางการค้าที่ใช้ออกใบนี้ — ตอนแก้ไขต้องแยก "ไม่ส่งมา" ออกจาก "ล้างค่า"
    // ให้ได้ (Guid? ใช้ null เป็น "ไม่ส่งมา" ไปแล้ว) → ส่ง Guid.Empty = กลับไป
    // ใช้ชื่อบริษัท มิฉะนั้นผู้ใช้ปลดแบรนด์ออกไม่ได้เลย (defect class "silent no-op")
    Guid? BrandId = null,
    // สาขาผู้ออกใบ — กติกาเดียวกับ BrandId: null = ไม่ส่งมา (คงค่าเดิม),
    // Guid.Empty = ล้างกลับไปใช้ค่าบริษัท (ไม่งั้นผู้ใช้ถอดสาขาออกไม่ได้เลย)
    Guid? BranchId = null,
    // รูปแบบ (เทมเพลต) ที่เลือกตอนออกใบ — null = ใช้ตั้งต้นของชนิดเอกสาร
    // (ตอนแก้ไข: Guid.Empty = กลับไปใช้ตั้งต้น เหมือนกติกาของ BrandId)
    Guid? DocumentTemplateId = null,
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
    DebitNoteReason? DebitNoteReason = null,
    // ฝั่งภาษีของใบลดหนี้/เพิ่มหนี้ (true = ซื้อ) — แก้ได้ตอนยังเป็นร่าง
    // ถ้าไม่รับตรงนี้ ผู้ใช้เปลี่ยนฝั่งบนฟอร์มแล้วกดบันทึก ค่าจะถูกทิ้งเงียบ ๆ
    bool? CnDnPurchaseSideOverride = null,
    bool? IsForeignService = null,
    bool? IsDeposit = null,
    string? DepositDeferredAccountCode = null,
    bool? DepositOutputVatDeferred = null,
    // ใบแจ้งหนี้/ใบกำกับภาษี (combined) — แก้ได้ตอน Draft เท่านั้น.
    bool? CombinedInvoiceTaxInvoice = null,
    // ขายเงินสด ใบเดียว (ใบกำกับภาษี/ใบเสร็จรับเงิน) — เดิม UpdateRequest ไม่มี
    // ⇒ ติ๊กตอนแก้ไขแล้วไม่มีผล (silent no-op); null = ไม่แตะ
    bool? IssuedAsCashReceipt = null,
    // เจตนา "รับเงินครบแล้ว" — ใบแปลงเกิดเป็นร่างเสมอ ผู้ใช้เลือกโหมดตอนแก้ไข
    // จึงต้องรับที่ update ด้วย; null = ไม่แตะ
    bool? PaidOnIssue = null,
    decimal? DepositAppliedAmount = null,
    string? DepositAppliedRef = null,
    bool? DepositAppliedDrivesJournal = null,
    bool? BuyerDeclinedTaxInvoice = null,
    // ส่วนลดท้ายบิล (จากยอดรวม) — null = คงค่าเดิม
    decimal? BillDiscountPercent = null,
    decimal? BillDiscountAmount = null,
    // ผู้จัดทำจริงจากระบบต้นทาง — จุดเดียวที่ยัดได้เมื่อ NextAcc สร้างเอกสารเอง
    // (เช่น OCR ใบสำคัญจ่าย) แล้ว partner แตะแค่ PUT (staff ไม่ใช่ NextAcc user →
    // X-Acting-User ไม่พอ). → ช่อง "ผู้จัดทำ/ผู้รับเงิน" (slot 0) priority เหนือ
    // CreatedBy; ช่อง "ผู้มีอำนาจลงนาม" (slot 1 = กรรมการ) คงเดิม. null = ไม่แตะ.
    string? PreparerName = null,
    string? PreparerSignatureBase64 = null,
    // ===== Quotation revision =====
    // แก้ใบเสนอราคาที่ "ลูกค้ากดยอมรับออนไลน์แล้ว" = ข้อเสนอเปลี่ยน การยอมรับ
    // เดิมใช้ไม่ได้ — ต้องส่ง true ยืนยันว่ารับทราบ (หลักฐานการยอมรับเดิมถูก
    // เก็บลง snapshot ก่อน reset เสมอ ไม่หาย). ใบที่ยังไม่ถูกยอมรับ = ไม่ต้องส่ง
    bool? AcknowledgeRevisionResetsAcceptance = null,
    // เหตุผลการแก้ (บันทึกลงประวัติ revision — เช่น "ลูกค้าต่อราคา")
    string? RevisionReason = null,
    // รอบ 193: ยอดชำระจริง — null = ไม่แตะ · 0 = ล้าง (จ่ายเต็มตามยอด) · > 0 = ตั้งค่า
    decimal? ActualPaidAmount = null,
    // รอบ 193: ผลต่างจากการปัดเศษ — null = ไม่แตะ (ค่าเดิมยังอยู่ · SubTotal คิดใหม่ = Σ บรรทัด + ค่านี้) · 0 = ล้าง
    decimal? RoundingAdjustment = null);

/// <summary>เติม/แก้ใบกำกับภาษีซื้อหลังอนุมัติ — trigger reclassify 11640→11610
/// เมื่อข้อมูลครบ §86/4. ทุก field nullable: omit = คงค่าเดิม. ส่งเฉพาะที่แก้.
/// InputVatAccountCodeOverride: ตั้ง "" (empty) เพื่อล้าง override กลับ default;
/// null = ไม่แตะ; ค่าอื่น = pin ผัง VAT ปลายทางใหม่.</summary>
public record CompleteSupplierTaxInvoiceRequest(
    string? SupplierInvoiceNumber = null,
    DateTime? SupplierTaxInvoiceDate = null,
    string? SupplierBranchCode = null,
    string? InputVatAccountCodeOverride = null,
    // ติ๊กเคลมภาษีซื้อเข้า/ออกหลังอนุมัติ (ดุลพินิจผู้กรอก — ระบบสร้าง JE ปรับ
    // 11640/11610 ↔ ค่าใช้จ่ายให้): false = เลิกเคลม (VAT ลงค่าใช้จ่าย
    // "ภาษีซื้อขอคืนไม่ได้"), true = กลับมาเคลม (ต้องครบ §86/4 + ในกรอบ §82/3),
    // null = ไม่แตะสถานะเคลม (พฤติกรรมเดิม)
    bool? ClaimInputVat = null,
    // งวด ภ.พ.30 ที่จะเคลมใบนี้ ("yyyy-MM" · รับ พ.ศ. ด้วย) — ตั้งจากหน้าดูเอกสาร
    // ได้เลย ไม่ต้องไปติ๊กเข้า/ออกในรายงานภาษี. convention เดียวกับฟอร์มแก้ไข:
    // null = ไม่แตะ, "" = ล้างกลับเคลมตามเดือนเอกสารปกติ. กติกาใครชนะใคร
    // (รายงานชนะ · งวดยื่นแล้วห้ามย้าย · กรอบ 6 เดือน §82/3) อยู่ที่ตัวตรวจกลาง
    // ApplyInputVatClaimPeriodAsync ตัวเดียวกับเส้นทางฟอร์ม
    string? InputVatClaimPeriod = null);

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

/// <summary>"JV มัดจำที่ไม่มีเอกสาร" ที่เปิดให้นำมาตัดชำระได้ (integration post ตรง
/// ผ่าน /integration/journals หรือลงมือ). DeferredNet = ยอดคงเหลือบัญชี 215/217;
/// Gross = รวม VAT. EntryNumber ใช้ apply ผ่าน /apply-journal-deposit.</summary>
public record JournalDepositCandidate(
    string EntryNumber,
    DateTime EntryDate,
    string? Description,
    string? Reference,
    decimal DeferredNet,
    decimal Gross);

/// <summary>นำ JV มัดจำ (ไม่มีเอกสาร) มาตัดชำระใบแจ้งหนี้ — ระบุ EntryNumber ของ
/// สมุดรายวันมัดจำ (หักเต็ม JV, v1).</summary>
// ApplyDate: วันที่ลง JE ตัดชำระ (ควร = วันที่เอกสารปลายทาง เพื่อให้ VAT
// recognition ตกเดือนภาษีเดียวกับ tax point ของใบ §78) — null = วันนี้
public record ApplyJournalDepositRequest(string JournalEntryNumber, DateTime? ApplyDate = null);

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
    string? Currency,
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
    string? BookingNumber = null,
    // ยอดที่คืนเงินแล้ว (gross รวม VAT) — Outstanding หักส่วนนี้แล้ว (audit #7)
    decimal RefundedAmount = 0m);

/// <summary>ตัววินิจฉัยหน้าเงินมัดจำ — บอกว่าระบบ "เห็น" อะไรบ้าง เพื่อหา
/// สาเหตุเมื่อ dashboard โชว์ 0 (ไม่มีบัญชีมัดจำในผัง / ไม่มี JE เครดิต /
/// เอกสารไม่ติดธง). แสดงในหน้าเมื่อ list ว่าง.</summary>
/// <summary>Deposit Center — payload เดียวจบสำหรับหน้าเงินมัดจำใหม่: รายการ +
/// KPI + กระทบยอดกับ GL + แหล่งที่มา + เวลา generate. ออกแบบให้ "ตัวเลขโกหก
/// ไม่ได้": ทุก response stamp เวลา + build marker, และ TieOut ฟ้องทันทีเมื่อ
/// หน้ากับบัญชีแยกประเภทไม่ตรงกัน (แทนการโชว์ 0 เงียบ ๆ).</summary>
public record DepositCenterResponse(
    DateTime GeneratedAtUtc,
    string BuildMarker,
    DepositCenterKpis Kpis,
    List<DepositSummary> Rows,
    DepositCenterTieOut TieOut,
    DepositCenterSources Sources,
    string? Warning);

public record DepositCenterKpis(
    decimal OutstandingBase,     // มัดจำคงค้าง (ฐาน)
    decimal RealizedBase,        // รับรู้รายได้แล้ว (ฐาน)
    decimal RefundedGross,       // คืนเงินแล้ว (รวม VAT)
    decimal DeferredVatParked,   // VAT พักรอเรียกเก็บ (21913 ยังไม่ recognize)
    decimal VatReported,         // VAT ถึงกำหนด/รายงานแล้ว
    int OpenCount,               // ใบที่ยังไม่รับรู้ครบ
    int TotalCount);

public record DepositCenterTieOut(
    decimal GlNet,               // ยอดคงค้างสุทธิจากบัญชีแยกประเภท (ΣCr−ΣDr บัญชีมัดจำ)
    decimal PageNet,             // ยอดคงค้างที่หน้าแสดง (Σ outstanding ทุกแถว)
    decimal Diff,                // ผลต่าง (ควร ~0)
    bool Ok);

public record DepositCenterSources(
    int NativeDocs,              // เอกสารติดธง IsDeposit
    int GlDetectedDocs,          // ตรวจจับจาก GL (integration ไม่ติดธง)
    decimal DoclessNet,          // JE ล้วนไม่ผูกเอกสาร
    int DepositAccounts,         // จำนวนบัญชีมัดจำในผัง
    List<DepositAccountInfo> Accounts);

public record DepositDiagnostics(
    int MatchedAccountCount,
    List<DepositAccountInfo> MatchedAccounts,
    int NativeDepositDocCount,      // Documents.IsDeposit = true (non-draft/void)
    int CreditLineCount,            // JE lines Cr บัญชีมัดจำ (Posted, ไม่ reverse)
    decimal TotalCreditNet,         // ΣCr − ΣDr รวมทุกบัญชีมัดจำ
    int DocLinkedCreditCount,       // credit lines ที่มี SourceDocumentId
    int DocLessCreditCount,         // credit lines ที่ไม่มี SourceDocumentId
    string Hint,                    // คำแนะนำภาษาไทยว่าติดตรงไหน
    // self-probe: จำนวนแถวที่ GetDepositsAsync (endpoint จริงของหน้า list) คืน
    // ใน build เดียวกันนี้ — ฟันธง: probe > 0 แต่หน้าเห็น 0 = cache/SW เก่า 100%;
    // probe = 0 ทั้งที่ native > 0 = บั๊กใน list builder (ดู ListEndpointError)
    int ListEndpointCount = 0,
    string? ListEndpointError = null);

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
    IReadOnlyList<string> MissingFields,
    // ใบบริการต่างประเทศ (§83/6) — VAT พัก 11640 เหมือนกันแต่**คนละวงจร**:
    // ทางออกคือ นำส่ง ภ.พ.36 → รับรู้ (ใบเสร็จ RD) ไม่ใช่ "เติมใบกำกับผู้ขาย"
    // (ผู้ขาย ตปท. ไม่มีวันมีเลขภาษีไทย 13 หลัก) — UI แยก section + CTA ต่างกัน
    bool IsForeignService = false);

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
    DebitNoteReason? DebitNoteReason = null,
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
    // ชื่อทางการค้าที่ใช้ออกใบนี้ + ชื่อที่จะโชว์ (echo กลับเพื่อ hydrate ฟอร์ม
    // ตอนเปิดแก้ไข — เก็บแล้วไม่ echo = ค่าหายเงียบ ๆ)
    Guid? BrandId = null,
    string? BrandName = null,
    // สาขาผู้ออกใบ + ป้ายที่พิมพ์จริง (echo กลับเพื่อ hydrate ฟอร์มตอนเปิดแก้ไข —
    // เก็บแล้วไม่ echo = ค่าหายเงียบ ๆ). IssuerBranchCode = รหัสที่ตรึงตอนอนุมัติ
    Guid? BranchId = null,
    string? BranchName = null,
    string? IssuerBranchCode = null,
    Guid? DocumentTemplateId = null,
    // ภ.พ.36 / ภ.ง.ด.54 — ซื้อบริการจากต่างประเทศ. Echo กลับมาเพื่อ form hydration.
    bool IsForeignService = false,
    // ===== Conversion lineage =====
    // Source-side view (this doc was converted from another): RelatedDocumentId
    // already carries the upstream id; the populated brief lets the UI render
    // "แปลงมาจาก QT-0042" without a second round-trip.
    DocumentBrief? RelatedDocument = null,
    /// <summary>ใบลดหนี้/เพิ่มหนี้ที่ผู้ใช้สั่งย้ายฝั่งเอง — true=ซื้อ, false=ขาย,
    /// null=ระบบตัดสินเอง (UI ใช้แสดงสถานะปัจจุบันของปุ่ม "ย้ายฝั่ง")</summary>
    bool? CnDnPurchaseSideOverride = null,
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
    // job §82/3 ล้างภาษีซื้อที่พ้น 6 เดือนเป็นค่าใช้จ่ายแล้วเมื่อ — UI ใช้ซ่อน
    // ฟอร์มเติมใบกำกับ (เติมไปก็เคลมไม่ได้แล้ว) + โชว์เหตุผล
    DateTime? InputVatExpiredAt = null,
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
    decimal DepositAppliedAmount = 0m,
    // ใบเสร็จ "หลักฐานรับเงิน" ที่ระบบออกอัตโนมัติคู่การชำระ — ไม่มี JE/VAT ของ
    // ตัวเอง (บัญชีอยู่ที่ Payment + ใบกำกับต้นทาง) → list ติดป้ายให้ผู้ใช้รู้ว่า
    // ไม่ใช่ยอดขายซ้ำ
    bool IsSettlementReceipt = false,
    // ===== เหตุผลที่ภาษีซื้อยังค้าง 11640 (เคลม ภ.พ.30 ไม่ได้) =====
    // Populated เฉพาะตอน InputVatPostedAsUndue=true + BecameClaimableAt=null —
    // รายการภาษาไทยบอกตรง ๆ ว่า "ขาดอะไร" (จาก TaxInvoiceCompletenessChecker
    // + guard อื่นของ ReclassifyUndueInputVatAsync เช่น override/ไม่มีบรรทัดเคลม
    // VAT) เพื่อให้ UI โชว์เหตุผลจริงแทนข้อความ generic ที่ทำให้ผู้ใช้งง
    List<string>? UndueInputVatBlockers = null,
    // ===== สถานะเคลมภาษีซื้อ "จากรายงาน ภ.พ.30 จริง" (source of truth) =====
    // Populated เฉพาะ GetDocumentAsync (detail) — จาก TaxReportLines ที่อ้างใบนี้
    // (TaxType=VAT ฝั่งซื้อ !IsExcluded). null = ยังไม่อยู่ในรายงานงวดใด.
    // ต่างจาก flag บนเอกสาร (HasTaxInvoiceReference ฯลฯ) ซึ่งบอกแค่ "ควรเคลมได้"
    // — field ชุดนี้บอกว่า "เคลมเข้ารายงานแล้วจริง งวดไหน ยื่นหรือยัง" ให้ badge
    // บน UI ตรงกับ ภ.พ.30 เสมอ
    int? InputVatPp30Month = null,
    int? InputVatPp30Year = null,
    // TaxReportStatus ของรายงานงวดนั้น: "Draft" | "Filed" | "Submitted"
    string? InputVatPp30ReportStatus = null,
    // ===== Document revision (เอกสาร operational) =====
    // ครั้งที่แก้ไข (0 = ฉบับแรก) — UI โชว์ "Rev.N" + เปิดประวัติได้
    int RevisionNumber = 0,
    // แก้ไขแบบออก Rev ใหม่ได้ไหม (server ตัดสินจากชนิด+สถานะ+เอกสารปลายทาง) —
    // UI ใช้ตัดสินว่าจะโชว์ปุ่ม "แก้ไข (Rev ใหม่)" ไหม โดยไม่ต้องรู้กติกาเอง
    bool CanRevise = false,
    string? CannotReviseReason = null,
    // หลักฐานการยอมรับออนไลน์ (echo ให้ UI เตือนก่อนแก้ + โชว์สถานะ)
    DateTime? QuotationAcceptedAt = null,
    string? QuotationAcceptedBy = null,
    // หลักฐานเซ็นรับของ (POD) บนใบส่งของ — ผูกพันเท่าการยอมรับใบเสนอราคา
    // UI ใช้เตือนก่อนแก้ว่า Rev ใหม่จะทำให้ลายเซ็นเดิมใช้ไม่ได้
    DateTime? DeliverySignedAt = null,
    string? DeliverySignedBy = null,
    /// <summary>ภาษาที่ตรึงไว้กับใบนี้ ("th"/"en") — null = ใช้ค่าเริ่มต้นของ
    /// เทมเพลต/บริษัท. echo กลับมาเพื่อ hydrate ฟอร์มตอนแก้ไข ไม่งั้นเปิดแก้ใบ
    /// ภาษาอังกฤษแล้วกดบันทึก ภาษาจะถูกล้างกลับเป็นค่าบริษัทเงียบ ๆ</summary>
    string? DocumentLanguage = null,
    /// <summary>ขายเงินสด "ใบเดียวจบ" (Dr เงินสด/ไม่ตั้งลูกหนี้, e-Tax T03,
    /// หัว "ใบกำกับภาษี/ใบเสร็จรับเงิน"). echo กลับเพื่อ hydrate ฟอร์มตอนแก้ไข
    /// — defect class เดียวกับ DocumentLanguage: รับค่าใน Create/Update แล้ว
    /// ไม่คืนใน Response ⇒ เปิดแก้ใบที่เคยติ๊กไว้ กล่องกลับว่าง กดบันทึกซ้ำ
    /// ค่าหายเงียบ ๆ (CLAUDE.md กฎเหล็ก #4 A "เก็บแล้วต้อง echo กลับ")</summary>
    bool IssuedAsCashReceipt = false,
    // echo เจตนา "รับเงินครบแล้ว" กลับให้ฟอร์ม hydrate โหมด tax_paid ได้
    bool PaidOnIssue = false,
    /// <summary>ใบกำกับภาษีที่ "ทำหน้าที่ใบเสร็จในตัว" — รับเงินครบแล้วและไม่มี
    /// ใบเสร็จแยก ⇒ หัวพิมพ์เป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน" (หรือ 3-in-1 เมื่อ
    /// combined). read-only คำนวณตอน map — UI ใช้ตั้งป้ายประเภทเอกสารให้ตรงกับ
    /// หัวกระดาษจริง (Layout.docHeaderLabel) โดยไม่ต้องเปิดพิมพ์ก่อน.
    /// ⚠️ mirror: PdfGenerationService.ResolveServedAsReceiptAsync คือเจ้าของกฎ
    /// ตัวจริง (ใช้ตอน render) — แก้ที่นั่นต้องแก้ ComputeServedAsReceipt ด้วย</summary>
    bool ServedAsReceipt = false,

    // ═══ ช่องที่รับตอน Create/Update แต่เดิม "ไม่มีใน Response" ═══════════
    // ทั้ง 6 ช่องนี้ผู้ใช้กรอกได้ · เก็บลงฐานจริง · มีผลกับกระดาษ/ภาษี แต่
    // ไม่เคยถูก echo กลับ ⇒ เปิดแก้ใบเดิมแล้วฟอร์มอ่านได้ undefined → ส่งค่า
    // ที่ล้างแล้วกลับไปทับ = **ค่าหายเงียบทุกครั้งที่แก้อะไรก็ตามในใบนั้น**
    // (CLAUDE.md กฎเหล็ก #4 A "เก็บแล้วต้อง echo กลับ" — บล็อกเดียวกับ
    // IssuedAsCashReceipt/PaidOnIssue ข้างบนที่แก้ไปแล้ว แต่ตกค้าง 6 ช่อง)

    /// <summary>"ผู้ซื้อไม่ประสงค์ขอใบกำกับภาษี" — ธง §86/4 ที่ควบคุมทั้งหัว
    /// กระดาษ (PdfGenerationService) และหมายเหตุ e-Tax (EtaxInvoiceService)
    /// ⇒ ตกค้างมาแล้วทำให้ติ๊กแล้วเปิดแก้ใบ ธงเด้งกลับเป็น false ทุกครั้ง</summary>
    bool BuyerDeclinedTaxInvoice = false,
    /// <summary>งวดที่ผู้ใช้เลือกจะเคลมภาษีซื้อ (§82/3) — เดิมหน้าเว็บต้อง
    /// "เดา" ค่านี้กลับจาก InputVatBecameClaimableAt/TaxPointDate/DocumentDate
    /// ⇒ โชว์งวดที่ผู้ใช้ไม่เคยเลือกได้</summary>
    string? InputVatClaimPeriod = null,
    /// <summary>ชื่อผู้จัดทำเอกสาร + ลายเซ็น (พิมพ์ลงกระดาษ)</summary>
    string? PreparerName = null,
    string? PreparerSignatureBase64 = null,
    /// <summary>เลขที่ใบมัดจำที่นำมาหัก + ธง "ให้ JE เดินตามการหักมัดจำ"</summary>
    string? DepositAppliedRef = null,
    bool DepositAppliedDrivesJournal = false,

    /// <summary>หัวเรื่องที่จะพิมพ์บนกระดาษจริง — คำนวณโดย
    /// <c>PdfGenerationService.ComputeDocumentTitle</c> ซึ่งเป็น**เจ้าของกฎตัวจริง**
    ///
    /// <para>⚠️ ที่มา: <c>Layout.docHeaderLabel</c> ฝั่ง JS เป็นสำเนามือที่ล้าหลัง
    /// ⇒ จอกับกระดาษพูดคนละอย่างอย่างน้อย 5 เคส:
    /// <list type="bullet">
    /// <item>ติ๊ก "ผู้ซื้อไม่ประสงค์รับใบกำกับ" + มี VAT → จอ "ใบเสร็จรับเงิน" ·
    ///   กระดาษ "ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ"</item>
    /// <item><c>IssuedAsCashReceipt</c> → จอสลับลำดับกับกระดาษ (กระดาษเรียงตาม
    ///   e-Tax T03 pairing)</item>
    /// <item>Receipt/ReceiptVoucher ที่มี VAT → จอไม่เติม "ใบกำกับภาษี/"</item>
    /// <item>ลูกค้า walk-in หรือข้อมูล §86/4 ไม่ครบ → กระดาษ downgrade เป็น
    ///   "อย่างย่อ" แต่จอไม่รู้ (<c>IsWalkInCustomer</c> ไม่เคยอยู่ใน DTO ไหนเลย)</item>
    /// <item>บริษัทตั้ง <c>DocumentTitleOverridesJson</c> / <c>template.CustomTitle</c>
    ///   → จอไม่รู้จักเลย</item>
    /// </list>
    /// เคสที่เจ็บสุด: server ตั้ง <c>BuyerDeclinedTaxInvoice = true</c> ให้เองตอน
    /// approve เมื่อผู้ซื้อบุคคลธรรมดาข้อมูลไม่ครบ ⇒ ใบที่ผู้ใช้ตั้งใจออกเป็น
    /// ใบกำกับกลายเป็นใบย่อบนกระดาษ โดยจอไม่เคยบอก</para>
    ///
    /// <para>null = เส้นทางที่ยังไม่ได้คำนวณ (เช่นรายการหลายใบ) — หน้าเว็บ
    /// fallback ไป <c>Layout.docHeaderLabel</c> ตามเดิม</para></summary>
    string? DocumentTitle = null,

    /// <summary>หมายเหตุ**ภายใน** — ไม่พิมพ์ลงกระดาษ (ต่างจาก <c>Notes</c>)
    ///
    /// <para>ที่นี่คือที่เก็บ "คำเตือนที่ผู้ใช้กดรับทราบแล้วยืนยันอนุมัติ" —
    /// เดิมคำเตือนที่ถูก acknowledge หายไปเฉย ๆ ⇒ ใบที่อนุมัติทั้งที่รู้ว่าผิด
    /// §86 หน้าตาเหมือนใบที่ไม่เคยมีคำเตือน (ไม่มีอะไรตอบผู้สอบบัญชีได้)
    /// คู่กับ AuditLog <c>APPROVE-ACK-WARNINGS</c> ที่มี hash chain</para></summary>
    string? InternalNotes = null,
    /// <summary>โมดูลที่สร้างเอกสารนี้ (Lodging/Pos/…) — หน้าเว็บใช้ติดป้าย "มาจากระบบจอง"</summary>
    string? OriginModule = null,

    // ── ใบกำกับภาษีเต็มรูปที่ออก "แทน" ใบเสร็จ/ใบกำกับอย่างย่อ (§86/6 → §86/4) ──
    /// <summary>ใบนี้ถูกแทนที่ด้วยใบกำกับเต็มรูปใบไหน (null = ยังไม่เคยออกใบแทน)
    /// — ใบที่มีค่านี้จะ<b>ไม่อยู่ในรายงานภาษีขาย</b> (ใบแทนรายงานให้แล้ว)</summary>
    Guid? ReplacedByDocumentId = null,
    string? ReplacedByDocumentNumber = null,
    /// <summary>ใบนี้ออกมาแทนใบไหน (null = ไม่ใช่ใบแทน)</summary>
    Guid? ReplacesDocumentId = null,
    string? ReplacesDocumentNumber = null,
    string? ReplacementReason = null,
    DateTime? ReplacedAt = null,
    /// <summary>กดปุ่ม "ออกใบกำกับภาษีเต็มรูป" ได้ไหม — <b>เซิร์ฟเวอร์ตัดสิน</b>
    /// ด้วย <c>FullTaxInvoiceReplacement.Check</c> ตัวเดียวกับที่ endpoint ใช้
    ///
    /// <para>ห้ามให้หน้าเว็บเขียนกติกาเอง (§86/4 ครบไหม · บริษัทจด VAT ไหม ·
    /// ออกไปแล้วหรือยัง) — defect class "สำเนามือฝั่ง JS ที่ตามหลังอยู่ไม่กี่ธง".
    /// null = เส้นทางที่ยังไม่ได้คำนวณ (รายการหลายใบ) ≠ "ทำไม่ได้"</para></summary>
    bool? CanIssueFullTaxInvoice = null,
    /// <summary>เหตุผลที่กดไม่ได้ (ข้อความไทยพร้อมโชว์) — null เมื่อกดได้</summary>
    string? FullTaxInvoiceBlockedReason = null,
    /// <summary>หัวเอกสารถูกลดจาก "ใบกำกับภาษีอย่างย่อ" เป็น "ใบเสร็จรับเงิน" เพราะบริษัทยังไม่มีสิทธิ์ §86/6
    /// — ข้อความไทยพร้อมทางไปต่อ (<c>PdfGenerationService.AbbreviatedDowngradeNotice</c>) ·
    /// null = ไม่ได้ถูกลด หรือเส้นทางที่ยังไม่ได้คำนวณ (รายการหลายใบ)</summary>
    string? TaxInvoiceTitleNotice = null,
    /// <summary>ยอดชำระจริงที่ต่างจากยอดเอกสาร (รอบ 193 — echo ให้ฟอร์ม hydrate · null = จ่ายเต็มตามยอด)</summary>
    decimal? ActualPaidAmount = null,
    /// <summary>ผลต่างจากการปัดเศษ (SubTotal = Σ บรรทัด + ค่านี้) — echo ให้ฟอร์ม/หน้ารายละเอียดแสดง · 0 = ไม่มี</summary>
    decimal RoundingAdjustment = 0m);

/// <summary>1 รายการประวัติ revision ของใบเสนอราคา (list — ไม่รวม snapshot เต็ม)</summary>
/// <summary>1 ใบในสายการแปลงเอกสาร (ดู GetDocumentChainAsync)
/// เรียงจากต้นน้ำ → ปลายน้ำ; Depth 0 = รากของสาย</summary>
public record DocumentChainNode(
    Guid Id,
    string DocumentNumber,
    DocumentType DocumentType,
    DocumentStatus Status,
    DateTime DocumentDate,
    decimal TotalAmount,
    decimal PaidAmount,
    int Depth,               // ระยะจากราก (0 = ต้นทางสุด)
    Guid? ParentId,          // ใบก่อนหน้าในสาย (null = ราก)
    bool IsCurrent);         // ใบที่ผู้ใช้กำลังเปิดอยู่

public record DocumentRevisionListItem(
    int RevisionNumber,
    decimal TotalAmount,
    string? Reason,
    string? RevisedBy,       // ผู้แก้ (= ผู้สร้าง revision ถัดไป)
    DateTime RevisedAt,
    bool WasAccepted);       // revision นั้นเคยถูกลูกค้ายอมรับหรือไม่

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

public record ContactBrief(Guid Id, string Name, string? TaxId, string? BranchCode = null);

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

/// <summary>ย้ายฝั่งใบลดหนี้/ใบเพิ่มหนี้ — true = ฝั่งซื้อ (ลดภาษีซื้อ),
/// false = ฝั่งขาย (ลดภาษีขาย)</summary>
public record ReclassifyCnDnSideRequest(bool ToPurchaseSide, string? Reason);

/// <summary>ติ๊ก/ปลดติ๊ก "ซื้อบริการจากต่างประเทศ (ภ.พ.36 §83/6)" ของเอกสารที่
/// อนุมัติไปแล้ว — ระบบกลับ JE เดิมแล้วลงใหม่ ไม่ใช่ปะตัวเลขในรายงาน</summary>
public record ReclassifyForeignServiceRequest(bool ToForeignService, string? Reason);

/// <summary>รายการ "ตัวกลับ" 1 ใบที่เครื่องมือแก้วันที่กลับบัญชีจะย้าย —
/// ใช้โชว์ให้ผู้ใช้เห็นก่อนกดยืนยันว่า **ใบไหนบ้าง** จะถูกย้ายจากวันไหนไปวันไหน
/// (เอกสารหนึ่งใบมักมีตัวกลับหลายใบ: ใบซื้อ/ใบขาย + การรับ-จ่ายชำระ + มัดจำ)</summary>
public record VoidReversalRedateRow(
    Guid JournalEntryId,
    string EntryNumber,
    string JournalType,
    DateTime CurrentDate,
    DateTime SuggestedDate,
    string? OriginalEntryNumber,
    DateTime? OriginalEntryDate,
    decimal Amount,
    bool WillMove,
    string? BlockReason);

/// <summary>ผลตรวจก่อนย้ายวันที่รายการกลับบัญชี</summary>
public record VoidReversalRedatePreview(
    Guid DocumentId,
    string DocumentNumber,
    DateTime DocumentDate,
    IReadOnlyList<VoidReversalRedateRow> Rows);

/// <summary>เอกสารที่อาจเป็น "ใบเดียวกันที่บันทึกไปแล้ว" — ใช้เตือนก่อนสร้าง
/// จากสแกน (สแกนใบเดิมซ้ำ = ค่าใช้จ่าย/ภาษีซื้อเบิ้ล)</summary>
public record DuplicateDocumentCandidate(
    Guid Id,
    string DocumentNumber,
    string DocumentType,
    DateTime DocumentDate,
    decimal TotalAmount,
    string Status,
    string? SupplierInvoiceNumber,
    string? ContactName,
    /// <summary>"SupplierInvoiceNumber" = เลขใบกำกับผู้ขายตรงกัน (แน่นอนสุด) ·
    /// "SameContactAndAmount" = คู่ค้า+ยอด+ช่วงวันใกล้กัน (น่าสงสัย)</summary>
    string MatchReason,
    bool IsStrong);

public record DuplicateCheckResult(
    bool HasStrongMatch,
    IReadOnlyList<DuplicateDocumentCandidate> Candidates);

/// <summary>บรรทัด JE ของเอกสาร (อ่านจาก GL จริง) — ใช้ในแผง "ตรวจสอบ/แก้ไข
/// รายการบัญชี" บนหน้าเอกสาร. <c>IsControlAccount</c> = บัญชีคุมที่ยอดเคลื่อนไหว
/// ห้ามเปลี่ยน (ภาษีซื้อ-ขาย/ลูกหนี้-เจ้าหนี้/มัดจำ) เพราะรายงานภาษี/อายุหนี้อ่านอยู่</summary>
public record DocumentJournalLineDto(
    Guid AccountId,
    string AccountCode,
    string AccountName,
    decimal DebitAmount,
    decimal CreditAmount,
    string? Description,
    bool IsControlAccount);

/// <summary>ใบสำคัญ 1 ใบของเอกสาร + สิทธิ์ว่าปรับปรุงได้ไหม (พร้อมเหตุผลถ้าไม่ได้)</summary>
public record DocumentJournalEntryDto(
    Guid Id,
    string EntryNumber,
    DateTime EntryDate,
    string JournalType,
    string Status,
    decimal TotalDebit,
    decimal TotalCredit,
    bool IsReversalEntry,
    bool CanAdjust,
    string? BlockReason,
    IReadOnlyList<DocumentJournalLineDto> Lines);

public record AdjustJournalLineDto(
    Guid AccountId, decimal DebitAmount, decimal CreditAmount, string? Description);

/// <summary>"สถานะปลายทาง" ของใบสำคัญที่ต้องการ — ระบบคำนวณผลต่างแล้วลง
/// **ใบปรับปรุงใหม่** ให้ (ไม่แก้ใบเดิม เพื่อรักษา audit trail)</summary>
public record AdjustDocumentJournalRequest(
    DateTime? EntryDate, List<AdjustJournalLineDto> Lines, string? Reason);

/// <summary>กำหนดวันที่ให้ตัวกลับ "รายใบ" — ผู้ใช้แก้วันที่ในตารางได้ทีละบรรทัด
/// (บางเคสอยากให้ทุกใบไปวันเดียวกับใบแรก บางเคสอยากให้แต่ละใบตามต้นฉบับตัวเอง)</summary>
public record RedateEntryDate(Guid JournalEntryId, DateTime NewDate);

/// <summary>ย้ายวันที่รายการกลับบัญชีแบบระบุรายใบ — ว่าง/ไม่ส่ง = ใช้ค่าเริ่มต้น
/// (วันที่ของใบต้นฉบับที่ตัวเองกลับ)</summary>
public record RedateVoidReversalRequest(List<RedateEntryDate>? Entries);

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
    string? PaymentTerms = null,
    // ภาษาเอกสารเริ่มต้นของผู้ติดต่อ ("th"/"en", null = ตามค่าบริษัท) —
    // ประทับลงใบตอนสร้าง เปลี่ยนรายใบทับได้เสมอ
    string? DocumentLanguage = null,
    // ข้อมูลภาษาอังกฤษ — ใช้เมื่อออกเอกสารเป็นภาษาอังกฤษ. null = ชื่อใช้ไทย
    // ตามเดิม / ที่อยู่ให้ระบบถอดอักษรให้ (ThaiRomanizer)
    [property: StringLength(300)] string? NameEn = null,
    [property: StringLength(500)] string? AddressEn = null,
    // คำนำหน้าชื่อ (บุคคลธรรมดา) — แยกช่องเพื่อลงไฟล์ ภ.ง.ด.3 Col12 · "" = ไม่มีคำนำหน้า
    [property: StringLength(50)] string? TitleTh = null);

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
    string? PaymentTerms = null,
    // null = ไม่เปลี่ยน · "" = ล้างกลับเป็น "ตามค่าบริษัท" · th/en = ตั้งค่า
    string? DocumentLanguage = null,
    // null = ไม่เปลี่ยน · "" = ล้างค่า (กลับไปใช้ไทย/ถอดอักษรอัตโนมัติ)
    [property: StringLength(300)] string? NameEn = null,
    [property: StringLength(500)] string? AddressEn = null,
    // คำนำหน้าชื่อ (บุคคลธรรมดา) — แยกช่องเพื่อลงไฟล์ ภ.ง.ด.3 Col12 · "" = ไม่มีคำนำหน้า
    [property: StringLength(50)] string? TitleTh = null);

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
    string? PaymentTerms = null,
    // echo กลับเสมอ — ฟอร์มแก้ไข contact ต้อง hydrate ได้ ไม่งั้นเปิดแก้แล้ว
    // กดบันทึกค่าจะหาย (defect class เดียวกับ DocumentResponse.DocumentLanguage)
    string? DocumentLanguage = null,
    string? NameEn = null,
    string? AddressEn = null,
    // คำนำหน้าชื่อ (บุคคลธรรมดา) — แยกช่องเพื่อลงไฟล์ ภ.ง.ด.3 Col12 · "" = ไม่มีคำนำหน้า
    [property: StringLength(50)] string? TitleTh = null,
    // ป้ายสาขาตามประกาศอธิบดีฯ 199 ("สำนักงานใหญ่" / "สาขาที่ 8") — **เซิร์ฟเวอร์คำนวณ**
    // จาก Helpers/TaxBranchCode ตัวเดียว ให้ตัวเลือกผู้ติดต่อบนหน้าสร้างเอกสารแสดงว่า
    // "เลือกสาขาไหน" (เลขภาษีเดียวกันมีได้หลายแถว = หลายสาขา) · null = ไม่ใช่ผู้ประกอบการ
    // จดทะเบียน (บุคคลธรรมดาไม่มีสาขา) — ห้าม JS เดาป้ายเอง (รอบ 190 ทีม C ข้อ 6)
    string? BranchLabel = null);

/// <summary>
/// ผลตรวจ "ใบกำกับภาษีซื้อบนฟอร์มนี้ ครบ §86/4 พอจะเคลม ภ.พ.30 ไหม" — ตัวตรวจ**ตัวเดียวกับ
/// ตัวลงบัญชี** (<c>TaxInvoiceCompletenessChecker.Evaluate</c>) อ่านผู้ติดต่อจากฐานข้อมูล
/// ไม่ใช่จากช่องบนหน้าจอ (รอบ 190 ทีม C ข้อ 3: ช่องเลขผู้เสียภาษีขึ้นค่าแล้ว แต่กล่องแดง
/// ของตัวตรวจ JS ยังบอกว่า "ขาด" เพราะสองอย่างอ่านคนละแหล่ง/คนละเวลา)
/// </summary>
/// <param name="IsClaimable">true = ตอนอนุมัติ VAT จะลง 11610 (เคลมได้) · false = พัก 11640</param>
/// <param name="MissingFields">สิ่งที่ขาด (ถ้อยคำเดียวกับหน้า "ภาษีซื้อยังไม่ถึงกำหนด")</param>
/// <param name="MissingContactFields">ส่วนที่ต้องแก้ที่ข้อมูลผู้ติดต่อ (ไม่ใช่ช่องบนเอกสาร)</param>
/// <param name="ContactTaxId">เลขผู้เสียภาษีที่ระบบจะใช้เคลมจริง (ของผู้ติดต่อ) — หน้าจอแสดงค่านี้</param>
/// <param name="ContactBranchCode">รหัสสาขาที่บันทึกไว้กับผู้ติดต่อ</param>
/// <param name="EffectiveBranchCode">สาขาที่จะถูกบันทึกลงเอกสารถ้ากดบันทึกตอนนี้
/// (ลำดับเดียวกับ CreateDocumentAsync: ช่องบนฟอร์ม → ผู้ติดต่อ → 00000)</param>
/// <param name="EffectiveBranchLabel">ป้ายของ <paramref name="EffectiveBranchCode"/> ตามประกาศฯ 199</param>
/// <param name="BranchCodeError">รหัสสาขาที่กรอกผิดรูป (ไม่ใช่ตัวเลข 5 หลัก) — null = ถูกรูป/ไม่ได้กรอก</param>
public record SupplierTaxInvoiceCheckResponse(
    bool IsClaimable,
    List<string> MissingFields,
    List<string> MissingContactFields,
    string? ContactTaxId,
    string? ContactBranchCode,
    string EffectiveBranchCode,
    string EffectiveBranchLabel,
    string? BranchCodeError);

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
    bool? IssueReceiptDocument = null,
    /// <summary>รอบ 193 (คำตัดสินเจ้าของข้อ 1/3): บรรทัดปรับส่วนต่าง "ยอดหนี้ตามใบ ↔ เงินที่จ่ายจริง" — เฉพาะเอกสารตั้งหนี้
    /// ฝั่งซื้อ (ใบแจ้งหนี้ซื้อ/ค่าใช้จ่าย) · <c>Amount</c> = เงินที่จ่ายจริง · ยอดหนี้ที่ปิด = Amount − Σ บรรทัดปรับ
    /// (Shopee: จ่าย 438 · 51120 +37 ค่าส่ง · 51150 −135 คูปอง ⇒ ปิดหนี้ 536) · ตัวตรวจ Helpers/PaymentSettlementAdjustment ·
    /// null/ว่าง = พฤติกรรมเดิม</summary>
    List<PaymentSettlementAdjustmentRequest>? SettlementAdjustments = null);

/// <summary>บรรทัดปรับหนึ่งบรรทัดของการชำระ — <c>Amount</c> มีเครื่องหมาย: + = จ่ายเกินยอดหนี้ด้วยรายการนี้ (Dr ผังนี้) ·
/// − = ปิดหนี้โดยไม่ต้องจ่ายเงิน (Cr ผังนี้)</summary>
public record PaymentSettlementAdjustmentRequest(string AccountCode, decimal Amount, string? Reason = null);

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
    string? PayerSignatureName = null,
    /// <summary>"payment" = แถวจากตาราง Payments (บันทึกชำระ/modal);
    /// "document" = แถวสังเคราะห์จากเอกสาร settle เส้น "แปลงเอกสาร"
    /// (ใบเสร็จ/ใบสำคัญรับ-จ่าย/CIL ที่ไม่มี Payment row) — read-only ให้
    /// สองเส้นทางเห็นประวัติเหมือนกัน.</summary>
    string Source = "payment",
    /// <summary>ใบเสร็จรับเงิน (REC) ที่ออกคู่กับการรับชำระรายการนี้ — <c>null</c> =
    /// <b>ยังไม่มีกระดาษใบรับให้ลูกค้า</b> ซึ่งหน้าเว็บต้องแสดงปุ่ม "ออกใบเสร็จ"
    /// ไม่ใช่ปล่อยว่างเงียบ ๆ
    ///
    /// <para>ที่มา: การรับชำระที่บันทึกก่อนด่าน ม.105 (ใบเดิมยกหัวเป็นใบเสร็จเอง)
    /// ไม่เคยออก REC — พอหัวกลับเป็น "ใบกำกับภาษี" ตามกฎหมาย แถวเหล่านั้นจึงเหลือ
    /// JE รับเงินโดยไม่มีเอกสารคู่</para></summary>
    Guid? ReceiptDocumentId = null,
    string? ReceiptDocumentNumber = null,
    /// <summary>ใบต้นทาง<b>ทำหน้าที่ใบเสร็จของการรับเงินรายการนี้อยู่แล้ว</b>
    /// (รับครบในวันเดียวกับวันที่บนใบ ⇒ หัวกระดาษพิมพ์ "ใบกำกับภาษี/ใบเสร็จรับเงิน")
    /// — หน้าเว็บต้อง<b>ไม่</b>เสนอปุ่ม "ออกใบเสร็จ" แต่ต้องบอกเหตุผลด้วย ห้ามซ่อนเงียบ
    ///
    /// <para>เซิร์ฟเวอร์คำนวณด้วย <c>DocumentService.ComputeServedAsReceipt</c> ตัวเดียว
    /// กับที่ตัดสินหัวกระดาษ — ห้ามให้ JS เดาจากชนิด/ยอดเอง (สำเนามือ = drift)</para></summary>
    bool SourceServesAsReceipt = false,
    /// <summary>รอบ 193: ยอดหนี้ที่การชำระนี้ปิดด้วยบรรทัดปรับ (ไม่ใช่เงินสด) — ยอดที่ปิดทั้งหมด = Amount + ค่านี้ · 0 = ไม่มี</summary>
    decimal SettlementAdjustmentAmount = 0m,
    /// <summary>รอบ 193: บรรทัดปรับทั้งชุด (JSON — AccountCode/Amount/Reason) · null = ไม่มี</summary>
    string? SettlementAdjustmentsJson = null);


/// <summary>ผลของการ "ออกใบเสร็จรับเงินให้การรับชำระที่บันทึกไปแล้ว"
/// (<c>POST payments/{id}/receipt</c>)
///
/// <para>คืนแค่ตัวตนของใบปลายทาง ไม่ใช่ <c>DocumentResponse</c> เต็มใบ — ใบที่เพิ่ง
/// สร้างยังไม่ได้ load บรรทัดกลับมา การคืน DTO เต็มจะได้ใบที่ <c>Lines</c> ว่าง
/// ซึ่งหน้าเว็บอ่านแล้วเข้าใจผิดว่าใบไม่มีรายการ ("ห้ามคืนค่าที่ไม่ใช่ความจริง")</para>
///
/// <para><paramref name="AlreadyExisted"/> = มีใบเสร็จของการรับชำระนี้อยู่แล้ว
/// (ไม่ได้สร้างใหม่) — ข้อความที่โชว์ต้องต่างกัน ไม่งั้นผู้ใช้นึกว่าออกใบซ้ำ ·
/// <paramref name="IsDraft"/> = ผู้กดไม่มีสิทธิ์อนุมัติใบเสร็จ ใบจึงเป็นร่าง
/// (เลขจริงออกตอนผู้มีสิทธิ์อนุมัติ — gap-free §86/4)</para></summary>
public record IssuedReceiptResult(
    Guid ReceiptId,
    string ReceiptNumber,
    DateTime ReceiptDate,
    bool IsDraft,
    bool AlreadyExisted);

public record WriteOffBadDebtRequest(string? Reason);
public record BatchConvertRequest(List<Guid> DocumentIds);
