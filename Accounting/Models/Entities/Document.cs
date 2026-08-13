using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// เอกสารทางธุรกิจ (ใบเสนอราคา, ใบแจ้งหนี้, ใบเสร็จ, ใบกำกับภาษี)
/// </summary>
public class Document : TenantEntity
{
    public string DocumentNumber { get; set; } = null!;    // running number
    public DocumentType DocumentType { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;
    public DateTime DocumentDate { get; set; }
    public DateTime? DueDate { get; set; }

    // Counterparty-side tax-invoice metadata. Required for PurchaseInvoice
    // (and any other doc where the counterparty issues their own tax
    // invoice we then book). SupplierInvoiceNumber is the partner's own
    // running number — distinct from our internal DocumentNumber and
    // needed for VAT-audit reconciliation against the supplier's
    // statement. SupplierTaxInvoiceDate is the date on the partner's
    // tax invoice; it controls which VAT period the input VAT is
    // claimed in (per Revenue Code §82/4 it may differ from our
    // DocumentDate when we book the bill late).
    public string? SupplierInvoiceNumber { get; set; }
    public DateTime? SupplierTaxInvoiceDate { get; set; }

    /// <summary>True เมื่อผู้ใช้ติ๊ก "ใช้งานใบกำกับภาษี" บนใบสำคัญจ่าย —
    /// บอกว่า PV ใบนี้อ้างใบกำกับภาษีซื้อเพื่อขอเครดิตภาษีซื้อ (ภพ.30).
    /// แยกออกจาก PV ที่จ่ายเฉย ๆ ไม่มี VAT (ค่าใช้จ่ายที่กิจการรับเอง).
    /// เมื่อ true → SupplierInvoiceNumber + SupplierTaxInvoiceDate + Contact.TaxId
    /// + SupplierBranchCode + SubTotal + VatAmount ต้องครบ (RD §86/4, §86/14)
    /// และข้อมูลใบนี้จะไหลเข้ารายงานภาษีซื้อ.</summary>
    public bool HasTaxInvoiceReference { get; set; }

    /// <summary>สาขาผู้ขาย ณ ตอนออกใบกำกับภาษี (snapshot) — Contact.BranchCode
    /// อาจถูกแก้ภายหลัง แต่รายงานภาษีซื้อย้อนหลังต้องคงสาขาเดิมตามใบจริง.
    /// "00000" = สำนักงานใหญ่; "00001"+ = สาขา. Null → fallback ใช้
    /// Contact.BranchCode ตอน export (back-compat กับเอกสารเก่า).</summary>
    public string? SupplierBranchCode { get; set; }

    /// <summary>True เมื่อ approve เอกสารแล้ว ใบกำกับภาษียัง §86/4 ไม่ครบ →
    /// VAT ถูก post เข้า 11640 "ภาษีซื้อยังไม่ถึงกำหนด" แทน 11610 "ภาษีซื้อ ภ.พ.30"
    /// (ป.รัษฎากร §82/3 — เครดิตได้เมื่อใบกำกับครบ). พอ user มาแก้ให้ครบ
    /// ระบบจะออก adjusting JE: Dr 11610 / Cr 11640, set
    /// InputVatBecameClaimableAt = now → InputVatPostedAsUndue ยังคง true เป็น
    /// historical marker, แต่ ภ.พ.30 จะ include ในเดือนของ BecameClaimableAt
    /// (ไม่ใช่ DocumentDate) เพื่อ match วันที่ JE ที่ลงจริง.</summary>
    public bool InputVatPostedAsUndue { get; set; }

    /// <summary>วันที่ระบบ generate adjusting JE ย้าย VAT 11640 → 11610.
    /// Null = ยังไม่ครบ หรือไม่เคย suspend. ใช้เป็น tax-point สำหรับ ภ.พ.30
    /// เมื่อ InputVatPostedAsUndue = true (เพื่อให้ตรงกับ JE จริง).</summary>
    public DateTime? InputVatBecameClaimableAt { get; set; }

    /// <summary>ฝั่งขาย (mirror ของ InputVatBecameClaimableAt): ใบแจ้งหนี้งานบริการ
    /// ล้วน VAT พักที่ 21913 "ภาษีขายรอเรียกเก็บ" (§78/1 tax point เกิดเมื่อรับชำระ/
    /// ออกใบกำกับ — ใบแจ้งหนี้ไม่ใช่ใบกำกับภาษี). เมื่อรับเงิน (Payment/ใบเสร็จ
    /// settlement) ระบบออก adjusting JE: Dr 21913 / Cr 21911 + stamp วันที่นี้ →
    /// ภ.พ.30 include ใบนี้ในเดือนของ OutputVatDueAt (ไม่ใช่ DocumentDate).
    /// Null + GL มี 21913 ค้าง = ยังไม่ถึง tax point → ไม่เข้า ภ.พ.30.
    /// Null + ไม่มี 21913 (ใบเก่า/ใบมีสินค้า ลง 21911 ตรง) = พฤติกรรมเดิม.</summary>
    public DateTime? OutputVatDueAt { get; set; }

    /// <summary>§82/3: ภาษีซื้อที่ค้าง 11640 พ้น 6 เดือนโดยใบกำกับไม่ครบ → เคลม
    /// ไม่ได้แล้ว ถูก reclassify เป็นค่าใช้จ่าย (Dr ค่าใช้จ่าย / Cr 11640) เมื่อ
    /// timestamp นี้ถูกตั้ง. คู่กับ ReclassifyExpiredUndueInputVatAsync.</summary>
    public DateTime? InputVatExpiredAt { get; set; }

    /// <summary>True = เอกสารนี้ออกเป็น "ใบแจ้งหนี้/ใบกำกับภาษี" ใบเดียว
    /// (combined). DocumentType ยังเป็น TaxInvoice จึงทำงานเป็นใบกำกับภาษี
    /// เต็มรูป (ลง VAT 21911 → ภ.พ.30, บังคับ §86/4, ออก e-Tax ได้) แต่หัว
    /// กระดาษ PDF พิมพ์ "ใบแจ้งหนี้/ใบกำกับภาษี" แทน "ใบกำกับภาษี" เพื่อให้ใช้
    /// เป็นทั้งใบแจ้งหนี้ (เรียกเก็บเงิน + เครดิตเทอม) และใบกำกับภาษีในใบเดียว.</summary>
    public bool CombinedInvoiceTaxInvoice { get; set; }

    /// <summary>ผู้ซื้อ "ไม่ประสงค์รับใบกำกับภาษี" (per-document, ผู้ใช้ติ๊กเอง) —
    /// ต่างจาก Contact.IsWalkInCustomer ที่เป็นผู้ติดต่อกลาง: อันนี้ใช้กับลูกค้า
    /// มีชื่อที่ข้อมูล §86/4 ไม่ครบและไม่ต้องการใบกำกับ. ผล: (1) ยกเว้น hard-block
    /// §86/4 ตอน approve (2) หัวเอกสารไม่ upgrade เป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน"
    /// — คงเป็น "ใบเสร็จรับเงิน" (ไม่ใช่ใบกำกับเต็มรูป ผู้ซื้อเคลมภาษีซื้อไม่ได้).
    /// **VAT ขายยังลงรายงานภาษีขาย/ภ.พ.30 ครบตามปกติ** (นำส่งภาษีได้ ไม่ขึ้นกับ
    /// หัวเอกสาร).</summary>
    public bool BuyerDeclinedTaxInvoice { get; set; }

    /// <summary>Transient (ไม่เก็บ DB) — ใบกำกับภาษีที่ "รับเงินตอนออกใบ" (cash
    /// sale, ชำระครบ ณ วันออก และไม่มีใบเสร็จแยกอ้างถึง) ทำหน้าที่เป็นทั้ง
    /// ใบกำกับภาษีและใบเสร็จรับเงินในใบเดียว → หัวกระดาษพิมพ์
    /// "ใบกำกับภาษี/ใบเสร็จรับเงิน". คำนวณตอน render (GenerateDocumentPdf/Html)
    /// ไม่ persist เพราะสถานะชำระเปลี่ยนได้.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool ServedAsReceipt { get; set; }

    /// <summary>ภาษาของเอกสารใบนี้เมื่อพิมพ์/ส่งออก ("th"/"en") — null = ใช้ค่า
    /// ตั้งต้นของบริษัท (<c>CompanySettings.DocumentLanguage</c>). ตรึงไว้กับใบ
    /// เพื่อให้ลูกค้าต่างชาติได้ไฟล์ภาษาเดิมทุกครั้งที่พิมพ์ซ้ำ</summary>
    public string? DocumentLanguage { get; set; }

    /// <summary>Transient (ไม่เก็บ DB) — ใบเสร็จ/ใบสำคัญรับที่ "อ้างใบกำกับภาษี"
    /// (settlement ของ TaxInvoice ที่รายงาน VAT ไปแล้ว) → หัวต้องเป็น
    /// "ใบเสร็จรับเงิน" เปล่า ห้ามมีคำว่า "ใบกำกับภาษี" ซ้ำ — ไม่งั้นลูกค้าถือ
    /// กระดาษที่มีคำว่าใบกำกับ 2 ใบจากการขายครั้งเดียว = เสี่ยงเคลมภาษีซื้อซ้ำ.
    /// (settlement ของ "ใบแจ้งหนี้" ตรงข้าม: ใบเสร็จนี่แหละคือใบกำกับที่กฎหมาย
    /// บังคับออก ณ วันรับเงิน §78/1 → พิมพ์ ใบกำกับภาษี/ใบเสร็จรับเงิน ถูกแล้ว)
    /// คำนวณตอน render ใน ResolveServedAsReceiptAsync.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool SettlesTaxInvoiceSource { get; set; }

    /// <summary>Transient (ไม่เก็บ DB) — ข้อมูลใบต้นฉบับสำหรับกล่อง §86/9-10 บน
    /// ใบลดหนี้/ใบเพิ่มหนี้ (เลขที่+วันที่+มูลค่าใบเดิม → มูลค่าที่ถูกต้อง+ผลต่าง)
    /// โหลดตอน render ใน ResolveServedAsReceiptAsync ทั้ง HTML และ QuestPDF.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? AdjustmentOriginalNumber { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public DateTime? AdjustmentOriginalDate { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public decimal? AdjustmentOriginalSubTotal { get; set; }

    /// <summary>ขายเงินสด B2B (integration isCashSale) — ใบกำกับภาษีที่รับชำระครบ
    /// พร้อมออก ทำหน้าที่เป็น "ใบเสร็จรับเงิน/ใบกำกับภาษี" ในตัว. persist (ต่างจาก
    /// ServedAsReceipt ที่คำนวณตอน render) เพราะ e-Tax generator ต้องรู้ตอน export
    /// → ออก e-Tax **T03 "ใบเสร็จรับเงิน/ใบกำกับภาษี"** (schema TaxInvoice, RD รับ)
    /// แทน 388. + หัว PDF พิมพ์ "ใบเสร็จรับเงิน/ใบกำกับภาษี". books ยังผ่านเส้น
    /// TaxInvoice ปกติ (deposit+settle ที่ verified) — flag นี้แค่ classification/หัว.</summary>
    public bool IssuedAsCashReceipt { get; set; }

    /// <summary>Capability token สำหรับลิงก์ "ลูกค้ากดยอมรับใบเสนอราคาออนไลน์"
    /// — random hex 64 ตัว สร้างเมื่อผู้ใช้ขอลิงก์ (POST accept-link). ผู้ถือ
    /// ลิงก์ดู/ยอมรับใบเสนอราคาได้โดยไม่ต้อง login (read-only + accept เท่านั้น).</summary>
    public string? QuotationAcceptToken { get; set; }
    /// <summary>วันหมดอายุของลิงก์ยอมรับ (default +30 วันจากที่สร้าง).</summary>
    public DateTime? QuotationAcceptTokenExpiresAt { get; set; }
    /// <summary>เวลาที่ลูกค้ากดยอมรับ (null = ยังไม่ยอมรับ).</summary>
    public DateTime? QuotationAcceptedAt { get; set; }
    /// <summary>ชื่อผู้กดยอมรับ (ลูกค้าพิมพ์เอง — บันทึกเป็นหลักฐาน).</summary>
    public string? QuotationAcceptedBy { get; set; }

    /// <summary>ครั้งที่แก้ไข (Rev.) — 0 = ฉบับแรก. เพิ่มทีละ 1 ทุกครั้งที่แก้
    /// **เอกสาร operational** ที่ "อนุมัติ/ส่งแล้ว" โดยคงเลขที่เดิม (ธรรมเนียม
    /// การค้า: QT-xxx Rev.2 = ใบเดิมที่ต่อรองแล้ว ไม่ใช่ใบใหม่). สภาพก่อนแก้
    /// ถูก snapshot ลง DocumentRevisions ทุกครั้ง เปิดดูย้อนหลังได้.
    ///
    /// ใช้ได้เฉพาะชนิดที่ **ไม่มี JE / ไม่ขยับสต๊อก / ไม่เข้ารายงานภาษี** —
    /// Quotation, PurchaseOrder, PurchaseRequisition, BillingNote, DeliveryNote
    /// (ดู DocumentService.RevisableTypes). เอกสารภาษี §86/4 และเอกสารที่ลง
    /// บัญชีแล้วห้ามแก้ย้อนหลัง ต้องยกเลิก/ออกใบลดหนี้ตามเดิม.</summary>
    public int RevisionNumber { get; set; }

    /// <summary>Capability token ลิงก์ "ลูกค้าเซ็นรับสินค้าออนไลน์" (Proof of
    /// Delivery) — ใช้กับใบส่งของ (DeliveryNote): ลูกค้าเปิดลิงก์บนมือถือ
    /// วาดลายเซ็น + พิมพ์ชื่อ → ประทับลงช่อง "ผู้รับของ" บน PDF อัตโนมัติ.</summary>
    public string? DeliverySignToken { get; set; }
    public DateTime? DeliverySignTokenExpiresAt { get; set; }
    public DateTime? DeliverySignedAt { get; set; }
    public string? DeliverySignedBy { get; set; }
    /// <summary>ภาพลายเซ็นผู้รับของ (data-url PNG จาก canvas) — render ลง
    /// slot "ผู้รับของ" ของ PDF ใบส่งของ.</summary>
    public string? DeliverySignatureBase64 { get; set; }

    /// <summary>User override ผังบัญชีปลายทางของ VAT ส่วนนี้. Null = default
    /// (11610/11640 ตาม completeness); ค่าอื่น เช่น "51000" (ต้นทุนขาย) =
    /// treat as cost ตาม §82/5(1) — block claim VAT ใน ภ.พ.30, ลง expense
    /// เต็มจำนวน. AccountCode (ไม่ใช่ Id) เพื่อ portable ระหว่าง tenants.</summary>
    public string? InputVatAccountCodeOverride { get; set; }

    /// <summary>True = ใบเสร็จ/ใบสำคัญรับนี้เป็น "เงินมัดจำ/รับล่วงหน้า"
    /// (deposit/advance) ไม่ใช่การขายที่รับรู้รายได้ทันที. ผลทางบัญชี:
    /// Dr เงินสด/ธนาคาร, Cr "ขายรอรับรู้/รับล่วงหน้า" (217xx — หนี้สิน) แทน
    /// บัญชีรายได้, Cr ภาษีขาย (21911). VAT ถึงกำหนดทันที (tax point = วันรับเงิน
    /// §78/§78/1) จึงเข้ารายงานภาษีขาย/ภ.พ.30 เดือนที่รับ แต่รายได้ยังรอรับรู้
    /// จนกว่าจะส่งมอบจริง (เรียก RealizeDepositAsync ตัด 217xx → รายได้).</summary>
    public bool IsDeposit { get; set; }

    /// <summary>ยอด (ฐานไม่รวม VAT) ของเงินมัดจำที่ถูกรับรู้เป็นรายได้แล้ว —
    /// รองรับการรับรู้บางส่วน (partial). คงค้าง = SubTotal − DepositRealizedAmount.
    /// 0 = ยังไม่รับรู้เลย (มัดจำคงค้างเต็มจำนวน).</summary>
    public decimal DepositRealizedAmount { get; set; }

    /// <summary>วันที่รับรู้รายได้ครบเต็มจำนวน (มัดจำปิด). Null = ยังคงค้าง
    /// (บางส่วนหรือทั้งหมด). ใช้คัดกรอง "มัดจำคงค้าง" ในหน้าจัดการ + งบดุล.</summary>
    public DateTime? DepositRealizedAt { get; set; }

    /// <summary>ผังบัญชี "ขายรอรับรู้/รับล่วงหน้า" ที่ใช้พักรายได้มัดจำใบนี้
    /// (snapshot ตอนรับเงิน). Null → default 21712 (ค่าสินค้ารับล่วงหน้า) /
    /// 21713 (ค่าบริการรับล่วงหน้า) ตอน post. ใช้ตอน RealizeDeposit ตัดกลับ
    /// บัญชีเดิม.</summary>
    public string? DepositDeferredAccountCode { get; set; }

    /// <summary>เคสภาษีขายของเงินมัดจำ — รองรับ 2 กรณีตามจังหวะ tax point:
    /// <para>• <c>false</c> (Immediate): tax point เกิดแล้วเมื่อรับเงิน
    /// (§78 ขายสินค้า / §78/1 บริการ — รับชำระราคา = จุดรับผิด) → Cr ภาษีขาย
    /// 21911 เข้า ภ.พ.30 เดือนที่รับทันที.</para>
    /// <para>• <c>true</c> (Deferred): ยังไม่เกิด tax point (เช่น เงินประกัน/
    /// มัดจำที่ยังไม่ถือเป็นการรับชำระราคา หรือบัญชีพิจารณาว่ายังไม่ให้บริการ)
    /// → Cr "ภาษีขายรอเรียกเก็บ" 21913 (Deferred Output VAT) — ยังไม่เข้า
    /// ภ.พ.30 จนกว่าจะเกิด tax point แล้ว reclassify 21913 → 21911.</para></summary>
    public bool DepositOutputVatDeferred { get; set; }

    /// <summary>ยอดเงินมัดจำ (รวม VAT) ที่นำมา "หัก" บนเอกสารรับเงินฉบับสุดท้าย
    /// เพื่อแสดงบรรทัด "หักเงินมัดจำ" + "ยอดชำระสุทธิ" บนใบ — DISPLAY ONLY:
    /// ไม่กระทบ JE (การรับรู้มัดจำ/กลับ 21913 ทำผ่าน RealizeDeposit/adjustment
    /// แยกอยู่แล้ว) และไม่ใช้ DocumentLine ติดลบ (validator ปฏิเสธ UnitPrice&lt;0).
    /// ระบบภายนอกส่งค่านี้มาตอนสร้างใบ = ยอดมัดจำที่หัก; renderer หักจาก
    /// TotalAmount → ยอดชำระสุทธิ. 0 = ไม่มีการหักมัดจำ.</summary>
    public decimal DepositAppliedAmount { get; set; }
    /// <summary>เลขใบมัดจำ/อ้างอิงที่นำมาหัก (แสดงในวงเล็บบนบรรทัด "หักเงินมัดจำ").
    /// เมื่อ DepositAppliedDrivesJournal=true ใช้ค่านี้ค้นใบมัดจำ (DocumentNumber)
    /// เพื่อกลับบัญชี deferred ของใบนั้น.</summary>
    public string? DepositAppliedRef { get; set; }
    /// <summary>true = ให้ DepositAppliedAmount "ขับ JE" ของใบรับเงินนี้: Dr เงินสด
    /// สุทธิ (Total−Applied) + กลับ 217xx/21913 ของใบมัดจำที่อ้าง (DepositAppliedRef)
    /// → JE self-contained ในใบเดียว (ไม่ต้องมี JV แยก). false (default) = display
    /// only (การรับรู้มัดจำทำผ่าน RealizeDeposit/JV ภายนอกเหมือนเดิม). opt-in
    /// เพื่อกัน double-reverse ช่วง transition — ระบบภายนอกเปิดเมื่อเลิกส่ง JV แยก.</summary>
    public bool DepositAppliedDrivesJournal { get; set; }

    /// <summary>true = ใบเสร็จรับเงินที่ออกเป็น "หลักฐาน" คู่กับ Payment (ตอน
    /// บันทึกชำระเงินใบกำกับ/ใบแจ้งหนี้เครดิต) — Payment ลง JE (Dr เงินสด/Cr ลูกหนี้)
    /// + ตัด AR ให้แล้ว ใบนี้จึง **ไม่ลง JE ซ้ำ ไม่ตัดหนี้ซ้ำ ไม่คิด VAT ซ้ำ**
    /// (VAT อยู่ที่ใบกำกับต้นทาง). ใช้เพื่อพิมพ์ใบเสร็จลงวันที่รับเงินจริง.</summary>
    public bool IsSettlementReceipt { get; set; }
    /// <summary>Payment ต้นทางที่ออกใบเสร็จหลักฐานนี้ (คู่กับ IsSettlementReceipt).</summary>
    public Guid? SettlementPaymentId { get; set; }

    /// <summary>ยอดมัดจำ (รวม VAT) ที่คืนให้ลูกค้าแล้ว (กรณียกเลิกการจอง).
    /// RefundDepositAsync gen reversal JE + ออกใบลดหนี้กลับ output VAT.
    /// 0 = ยังไม่คืน.</summary>
    public decimal DepositRefundedAmount { get; set; }
    /// <summary>วันที่คืนมัดจำ (null = ยังไม่คืน).</summary>
    public DateTime? DepositRefundedAt { get; set; }
    /// <summary>เลขเอกสารอ้างอิงตอนคืน/เหตุผล (เช่น "ยกเลิกงานแต่ง 15/8").</summary>
    public string? DepositRefundReason { get; set; }
    /// <summary>เมื่อมัดจำถูกนำไปหักกับใบแจ้งหนี้/ใบกำกับสุดท้าย —
    /// FK ไปเอกสารนั้น (offset). Null = ยังไม่ถูกนำไปหัก.</summary>
    public Guid? DepositAppliedToDocumentId { get; set; }

    /// <summary>เลขจอง/รหัส booking ที่ระบบภายนอก (PMS โรงแรม / POS ร้าน /
    /// CRM งานแต่ง) อ้างถึง. ใช้ผูกเอกสารหลายใบเข้ากับ booking เดียวกัน:
    /// มัดจำ → ใบแจ้งหนี้สุดท้าย → ใบเสร็จ. ต่างจาก Reference (free-text) ที่
    /// BookingNumber เป็น key indexed สำหรับ query รวมเอกสารทั้ง booking.
    /// Null = เอกสารไม่ผูก booking.</summary>
    public string? BookingNumber { get; set; }

    /// <summary>วันที่ภาษีขายมัดจำ (เคส Deferred) ถูกย้าย 21913 → 21911 (tax point
    /// เกิดจริง เช่น ส่งมอบ/ออกใบกำกับ). ใช้เป็น tax point ของ ภ.พ.30 สำหรับ
    /// มัดจำ deferred. Null = ยังไม่เกิด (ยังไม่เข้า ภ.พ.30) หรือเป็นเคส Immediate
    /// (ซึ่งเข้า ภ.พ.30 ตั้งแต่ DocumentDate อยู่แล้ว).</summary>
    public DateTime? DepositOutputVatRecognizedAt { get; set; }

    /// <summary>Credit term in days from the document date — used to
    /// auto-fill DueDate when not explicit, and to roll DSO / DPO
    /// reports. Defaulted from Contact.PaymentTermDays on create when
    /// the caller doesn't override.</summary>
    public int? CreditDays { get; set; }

    /// <summary>Free-text payment terms label (e.g. "Net 30", "2/10
    /// Net 30", "EOM+15") — for human readability on printed
    /// documents. Independent of CreditDays which drives auto math.</summary>
    public string? PaymentTerms { get; set; }

    /// <summary>Settlement basis — Cash (จ่าย/รับทันที) vs Credit (เครดิต).
    /// Primarily for Payment Voucher (ใบสำคัญจ่าย): Cash posts straight to
    /// Cash/Bank with no payable + no due date + no aging; Credit posts to
    /// Accounts Payable, carries a due date, and ages until settled. Null =
    /// not specified → the service picks a per-type default (standalone
    /// Payment Voucher defaults to Cash).</summary>
    public PaymentType? PaymentType { get; set; }

    /// <summary>True when the unit prices on the lines were entered VAT-
    /// INCLUSIVE (ราคารวมภาษี) — common in Thai retail. When set, the line
    /// calculator backs the 7% VAT out of the entered price so SubTotal /
    /// VatAmount post the correct ex-VAT base + tax. False = prices are
    /// ex-VAT (the historical default, VAT added on top).</summary>
    public bool PricesIncludeVat { get; set; }

    // Contact (Customer/Supplier)
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;

    // Reference
    public string? Reference { get; set; }

    /// <summary>เลข "อ้างอิง" ที่ **แสดง** บนเอกสาร/PDF — ให้ความสำคัญเลขจอง
    /// (BookingNumber = RES-id ที่มีความหมายกับคน) เหนือ Reference. ฝั่ง integration
    /// เก็บ externalRef (dedup key ภายใน เช่น REC260718006) ลง Reference ซึ่งไม่ควร
    /// โชว์ให้ลูกค้า/บัญชี → ใช้ property นี้ render แทน. company doc ที่ไม่มี
    /// BookingNumber → คืน Reference ตามเดิม. **display เท่านั้น — dedup/idempotency
    /// ยังใช้ Reference field ไม่กระทบ**.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? DisplayReference =>
        !string.IsNullOrWhiteSpace(BookingNumber) ? BookingNumber : Reference;

    public Guid? RelatedDocumentId { get; set; }  // e.g. Quotation → Invoice

    /// <summary>ใบลดหนี้/ใบเพิ่มหนี้: บังคับฝั่งด้วยมือ — <c>true</c> = ฝั่งซื้อ
    /// (ลดภาษีซื้อ 116x), <c>false</c> = ฝั่งขาย (ลดภาษีขาย 2191x),
    /// <c>null</c> = ให้ระบบตัดสินเอง (ใบต้นทาง → GL → บทบาทคู่ค้า)
    ///
    /// <para>มีไว้เพราะ CN/DN ที่ผู้ขายอ้างเลขใบกำกับนอกระบบ อาจถูกจัดฝั่งผิด
    /// ตั้งแต่ตอนอนุมัติ แล้ว JE ลงผิดฝั่งถาวร — รายงาน ภ.พ.30 ยึด GL เป็นความจริง
    /// จึงตามไปผิดด้วย และ "สร้างรายงานใหม่" ไม่ช่วย. ค่านี้ให้ผู้ใช้ย้ายฝั่งได้
    /// โดย <b>ระบบกลับ JE เดิมแล้วลงใหม่ให้ถูกฝั่ง</b> — ไม่ใช่แค่ย้ายตัวเลขใน
    /// รายงาน (ถ้าย้ายแต่รายงาน งบกับแบบยื่นจะไม่ตรงกัน ตรวจสอบย้อนหลังไม่ได้)</para></summary>
    public bool? CnDnPurchaseSideOverride { get; set; }

    /// <summary>Required when DocumentType = CreditNote — distinguishes the
    /// legal/accounting reason per ประมวลรัษฎากร §82/10. Determines whether
    /// the CN restocks goods (Return only) or is a pure financial adjustment
    /// (Discount / Writeoff / OtherAdjustment).</summary>
    public CreditNoteReason? CreditNoteReason { get; set; }

    // Project tagging — header default; lines can override per-line.
    // Used to attribute revenue/cost on auto-posted journal entries to a Project,
    // enabling per-project P&L (see ProjectAccountingService.GetGlSummaryAsync).
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>Cost center / สาขา / แผนก (AccountingDimension) — ต่างจาก
    /// Project: มิติเป็นหน่วยงาน "ถาวร" ตามโครงสร้างองค์กร (วัดต้นทุนต่อสาขา/
    /// แผนกต่อเนื่อง) ส่วน Project เป็น "งานชั่วคราว" มีจบ (วัดกำไรต่องาน).
    /// ไหลลง JournalEntry.DimensionId ตอน auto-post → รายงาน P&L ต่อมิติ.</summary>
    public Guid? DimensionId { get; set; }

    // Bank account link — which bank account money flows in/out of.
    // Used for reconciliation and auto-posting to correct GL bank account.
    public Guid? BankAccountId { get; set; }
    public BankAccount? BankAccount { get; set; }

    // Payment account — direct GL account for non-bank money flow
    // (e.g. เงินสด 111, เงินทดรองกรรมการ 115/219, e-Wallet 11190)
    // Takes precedence over BankAccountId when set.
    public Guid? PaymentAccountId { get; set; }
    public ChartOfAccount? PaymentAccount { get; set; }

    // Expense category (header-level default when all lines share the same category)
    public Guid? ExpenseCategoryId { get; set; }
    public ChartOfAccount? ExpenseCategory { get; set; }

    // Amounts
    public string Currency { get; set; } = "THB";
    /// <summary>FX rate at the time of document creation (1 unit of Currency = X THB).
    /// 1.0 when Currency = THB. Captured on Create so JE posting uses the same
    /// rate that was shown to the user on the document.</summary>
    public decimal ExchangeRate { get; set; } = 1m;
    public decimal SubTotal { get; set; }
    /// <summary>ผลรวมส่วนลด "รายบรรทัด" (Σ DocumentLine.DiscountAmount).</summary>
    public decimal DiscountAmount { get; set; }
    /// <summary>ส่วนลด "ท้ายบิล" (จากยอดรวม) — % ที่ผู้ใช้กรอก (0 = ใช้โหมดยอดบาท).</summary>
    public decimal BillDiscountPercent { get; set; }
    /// <summary>ส่วนลดท้ายบิลที่หักจริง (ex-VAT, เฉลี่ย pro-rata ลงบรรทัดแล้ว).
    /// SubTotal เป็นยอด "หลังหักท้ายบิล" → ยอดก่อนหัก = SubTotal + BillDiscountAmount.</summary>
    public decimal BillDiscountAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal WithholdingTaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal BalanceDue { get; set; }

    // Notes
    public string? Notes { get; set; }
    public string? InternalNotes { get; set; }

    /// <summary>True when the operator has explicitly DISMISSED this document
    /// from the "waiting to issue WHT cert" list. The source doc still
    /// exists and is unchanged; we just don't pester the user about it any
    /// more. Used when WHT was deducted but no certificate is needed (e.g.,
    /// internal accruals, intra-company reclassifications).</summary>
    public bool WhtCertSkipped { get; set; }

    /// <summary>Access-control classification — None for the regular sales
    /// stream, Payroll/ExecutivePay/HrPersonal for restricted records.
    /// Owner picks which roles can see each kind in CompanySensitivitySettings.</summary>
    public SensitivityKind Sensitivity { get; set; } = SensitivityKind.None;

    // Per-document overrides for the company's global appendix/footer templates.
    // When null, falls back to CompanySettings.{Type}Notes / {Type}Footer.
    public string? CustomAppendix { get; set; }
    public string? CustomFooterNotes { get; set; }
    public string? CustomTermsAndConditions { get; set; }

    // Optional link to a Revenue Contract — set when this document is invoicing
    // against a recognized contract milestone. Used for ASC 606 / TFRS 15 tracking.
    public Guid? RevenueContractId { get; set; }
    public Guid? PerformanceObligationId { get; set; }

    // ===== ใบรับรองแทนใบเสร็จ (CertificateInLieu) =====
    public string? CertificateReason { get; set; }       // เหตุผลที่ไม่ได้รับใบเสร็จ
    public string? CertifierName { get; set; }            // ชื่อผู้รับรอง
    public string? CertifierPosition { get; set; }        // ตำแหน่งผู้รับรอง
    public string? WitnessName { get; set; }              // ชื่อพยาน
    public string? WitnessPosition { get; set; }          // ตำแหน่งพยาน
    public DateTime? PaymentDate { get; set; }            // วันที่จ่ายเงินจริง

    /// <summary>ซื้อบริการจาก supplier ต่างประเทศที่ไม่ได้จด VAT ในไทย
    /// (ตามมาตรา 83/6 ผู้รับบริการต้อง self-assess VAT 7% ผ่าน ภ.พ.36 ภายใน
    /// วันที่ 7 ของเดือนถัดไป). Default false. ตั้ง true สำหรับ PI/Expense
    /// ที่เป็น cross-border services (Google Ads / AWS / Software license
    /// จาก US, etc.).</summary>
    public bool IsForeignService { get; set; }

    /// <summary>Link ไปยัง EarlyPaymentDiscountTerm ("2/10 net 30") ที่ผูก
    /// กับเอกสารฝั่งขาย. Receipt ตรวจ window → auto-apply discount. Null =
    /// ไม่มีเงื่อนไขส่วนลดเงินสด.</summary>
    public Guid? EarlyPaymentDiscountTermId { get; set; }

    // ===== External preparer signature override =====
    // When a document is created by an integrating system (e.g. TakeTime
    // syncing a payment voucher), the real preparer is a user of THAT system,
    // not a NextAcc User — so the normal CreatedBy(GUID)→User.Signature lookup
    // finds nothing. The partner can ship the preparer's name + signature image
    // inline; we store them here and ResolveSignersAsync stamps them into the
    // "ผู้จัดทำ" slot directly, before any User/Owner fallback.

    /// <summary>Display name of the external preparer ("ผู้จัดทำ"). Set only
    /// when the document originates from an integration that supplied it.</summary>
    public string? PreparerName { get; set; }

    /// <summary>External preparer's signature image — a "data:image/...;base64,"
    /// URI or raw base64. Rendered in the preparer slot when present.</summary>
    public string? PreparerSignatureBase64 { get; set; }

    // ===== Tax Point (จุดความรับผิดในการเสีย VAT) §78 / §78/1 / §78/2 =====
    /// <summary>วันส่งมอบสินค้า (input ของ tax point §78 ขายสินค้า). Null =
    /// ยังไม่ส่งมอบ/ไม่ระบุ.</summary>
    public DateTime? DeliveryDate { get; set; }
    /// <summary>วันโอนกรรมสิทธิ์ (input ของ tax point §78). Null = ไม่ระบุ.</summary>
    public DateTime? OwnershipTransferDate { get; set; }
    /// <summary>วันที่ใช้บริการ/บริการเสร็จ (input ของ tax point §78/1 บริการ).</summary>
    public DateTime? ServiceUsedDate { get; set; }
    /// <summary>จุดความรับผิดในการเสีย VAT ที่ระบบคำนวณ (TaxPointResolver):
    /// <para>• ขายสินค้า §78 = MIN(DeliveryDate, OwnershipTransferDate, PaymentDate, IssueDate)</para>
    /// <para>• บริการ §78/1 = MIN(PaymentDate, IssueDate, ServiceUsedDate)</para>
    /// VAT period ของ ภ.พ.30 ใช้เดือนของ TaxPointDate (ไม่ใช่ DocumentDate).
    /// Null = ยังไม่คำนวณ (เอกสารเก่า) → fallback DocumentDate ตอน export.</summary>
    public DateTime? TaxPointDate { get; set; }

    // ===== Retention (อายุการเก็บเอกสาร) §87/3 + พ.ร.บ.บัญชี ม.10 =====
    /// <summary>วันที่เก็บเอกสารถึง (เก็บอย่างน้อย 5 ปีจากวันสิ้นรอบ/วันยื่น
    /// per §87/3 + ม.10). ห้ามลบจริงก่อนวันนี้ (legal hold). คำนวณตอน approve.</summary>
    public DateTime? RetentionUntil { get; set; }

    // ===== §65 ตรี — รายจ่ายต้องห้าม (Non-Deductible add-back) =====
    /// <summary>ยอดรายจ่ายต้องห้ามที่ต้อง "บวกกลับ" ใน ภ.ง.ด.50 (§65 ตรี).
    /// คำนวณโดย Section65TerValidator ตอน approve เอกสารฝั่งซื้อ/ค่าใช้จ่าย.
    /// 0 = หักภาษีได้เต็ม.</summary>
    public decimal NonDeductibleAmount { get; set; }
    /// <summary>RuleCode + มาตราที่ทำให้รายจ่ายส่วนนี้ต้องห้าม (เช่น
    /// "RD-65ter(6) ค่าปรับ"). JSON array ถ้าหลายข้อ. ลง audit + ภ.ง.ด.50 note.</summary>
    public string? NonDeductibleRuleJson { get; set; }
    /// <summary>เหตุผลกรณีใบกำกับมาช้า 1-6 เดือน (§82/3) — required เมื่อ
    /// (FilingMonth − InvoiceMonth) อยู่ใน 1..6. Null = ไม่ช้า.</summary>
    public string? LateReason { get; set; }

    // ===== OCR Self-Learning =====
    // Watermark set by VendorIntelligenceService.TrainFromDocumentAsync after this
    // document's data has been counted into the per-vendor intelligence cache.
    // Prevents double-counting on re-approval (Draft → Approved → Rejected → Draft → Approved).
    public DateTime? OcrIntelTrainedAt { get; set; }

    // ===== OCR RD Compliance (Task 5 ERP Upgrade) =====

    /// <summary>Average confidence (0..1) across all fields extracted by
    /// Azure Document Intelligence on first OCR pass. Below 0.5 should
    /// surface a "manual review" badge to the operator.</summary>
    public decimal? OcrConfidenceScore { get; set; }

    /// <summary>Result of the post-OCR RD compliance check
    /// (Tax Invoice keyword present, Buyer/Seller Tax IDs valid, branch
    /// code populated, VAT breakdown balances). Drives the UI badge.</summary>
    public Enums.RdComplianceStatus RdComplianceStatus { get; set; } = Enums.RdComplianceStatus.Pending;

    /// <summary>JSON-serialised list of individual issues so the badge can
    /// expand to "3 problems: missing branch code, ..." without a join.</summary>
    public string? RdComplianceIssuesJson { get; set; }

    /// <summary>True when OCR pulled a Buyer Tax ID that doesn't match this
    /// tenant's CompanyTaxId — usually means an invoice for a different
    /// legal entity was uploaded into the wrong company. Hard warning.</summary>
    public bool OcrTenantMismatchFlag { get; set; }

    /// <summary>Aging engine watermark — calendar days since the document
    /// became Pending (Approved-but-unpaid). Refreshed by AgingBackgroundService;
    /// cached here so list views don't recompute on every fetch.</summary>
    public int? AgingDays { get; set; }
    public DateTime? AgingLastEvaluatedAt { get; set; }

    /// <summary>วันที่ส่งหนังสือทวงหนี้ครั้งล่าสุด — กัน OverdueDunningJob
    /// ส่งซ้ำในรอบเดียวกัน. NULL = ไม่เคยส่ง. job ส่ง 3 ระดับตาม aging:
    /// 30d (Reminder) → 60d (First Notice) → 90d (Final Notice).</summary>
    public DateTime? LastDunningSentAt { get; set; }

    /// <summary>ระดับ dunning ล่าสุด (1/2/3). เมื่อ AgingDays ข้ามขั้นถัดไป
    /// และ ≥ 7 วัน นับจาก LastDunningSentAt → ส่งใหม่</summary>
    public int? LastDunningLevel { get; set; }

    /// <summary>True = an opening-balance subledger document imported during
    /// migration (open AR/AP carried over from a previous system). It is
    /// created already-Approved and is deliberately NEVER auto-posted to the
    /// GL — the control-account total is carried by the GL opening balance
    /// (TrialBalance migration), so posting it would double-count.</summary>
    public bool IsOpeningBalance { get; set; } = false;

    // Navigation
    public ICollection<DocumentLine> Lines { get; set; } = new List<DocumentLine>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    /// <summary>Adjusting JE lines ที่ user เพิ่มเองนอกเหนือจาก DocumentLine
    /// ปกติ (เช่น ค่าธรรมเนียมโอน, สำรอง, ปันส่วน). AutoPostToJournalAsync
    /// รวม lines เหล่านี้เข้า JE หลังจาก gen lines มาตรฐานครบ — ก่อน balance
    /// check. user รับผิดชอบให้ยอด Dr/Cr ของ adjusting lines balance กันเอง
    /// (Dr รวม = Cr รวม) — auto-gen lines ที่เหลือ + adjusting lines ทั้งหมด
    /// จะ balance อัตโนมัติ.</summary>
    public ICollection<DocumentAdjustingJournalLine> AdjustingJournalLines { get; set; }
        = new List<DocumentAdjustingJournalLine>();
}

/// <summary>
/// Adjusting Journal Entry Line — รายการ Dr/Cr "เพิ่มเติม" บนเอกสาร 1 ใบ
/// นอกเหนือจาก DocumentLine ปกติ. ใช้รองรับเคส:
///   • PV 1 ใบ มีค่าธรรมเนียมโอนเงิน 50 บาท ที่ไม่ใช่ค่าใช้จ่ายหลัก
///   • ตั้งสำรอง / accrual ในเอกสารเดียว
///   • ปันส่วน cost ไปหลาย project ที่ไม่ได้ map 1:1 กับ DocumentLine
///   • Reverse partial — Cr ค่าใช้จ่ายที่ผันแปร
///
/// **ข้อบังคับ:** ผู้ใช้ต้องทำให้ "ผลรวม Dr ของ adjusting = ผลรวม Cr ของ
/// adjusting" — เพราะ auto-gen JE ที่เหลือ balance อยู่แล้ว (Dr=Cr) →
/// adjusting ต้องเป็น net-zero ไม่งั้นรวมแล้วเอกสารเสีย balance.
/// AutoPostToJournalAsync ตรวจ Dr=Cr รวมทั้ง JE ตอน post → throw ถ้าเสีย.
/// </summary>
public class DocumentAdjustingJournalLine : BaseEntity
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public int LineOrder { get; set; }
    public Guid AccountId { get; set; }
    public ChartOfAccount? Account { get; set; }
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public string? Description { get; set; }
    public Guid? ProjectId { get; set; }
    public string? Reason { get; set; }   // เหตุผลในการเพิ่ม line (audit)
}

/// <summary>
/// รายการย่อยในเอกสาร
/// </summary>
public class DocumentLine : BaseEntity
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;

    public int LineOrder { get; set; }
    public string? ProductCode { get; set; }
    public string Description { get; set; } = null!;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "ชิ้น";
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal Amount { get; set; }
    public decimal VatRate { get; set; } = 7;
    public decimal VatAmount { get; set; }
    public decimal WithholdingTaxRate { get; set; }
    public decimal WithholdingTaxAmount { get; set; }
    public string? IncomeTypeCode { get; set; }  // รหัสประเภทเงินได้ สำหรับภาษีหัก ณ ที่จ่าย

    // Account mapping for auto-posting
    public Guid? AccountId { get; set; }
    public ChartOfAccount? Account { get; set; }

    /// <summary>FeedbackId ของ AiSuggestionFeedback ที่ให้ผังบัญชีเส้นนี้ —
    /// set เมื่อ AccountId นี้มาจาก AI (OCR / PV bulk suggest / integration
    /// GL fallback). ใช้ปิดลูปการสอน local model ตามกฎเหล็ก #1: ตอนผู้ใช้
    /// ยืนยัน/แก้ AccountId, DocumentService เรียก RecordUserChoiceAsync
    /// (acceptedAi = chosenCode == AiPrimaryAnswer). Null = user เลือก
    /// ผังเองตั้งแต่ต้น ไม่ต้องบันทึก feedback.</summary>
    public Guid? GlAccountAiFeedbackId { get; set; }

    // Per-line project override — falls back to Document.ProjectId if null.
    // Allows splitting a single document across multiple projects (e.g. one
    // mixed invoice billing two projects).
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>
    /// When this line was created by converting another document, points at
    /// the source <see cref="DocumentLine"/> it was derived from. Null on
    /// lines entered directly by the user.
    ///
    /// This is the backbone of FLEXIBLE / PARTIAL document composition: one
    /// source line (e.g. a PO line for 100 units) may be carried forward
    /// into several child lines across several documents (DeliveryNote ×2,
    /// Invoice ×3...). The quantity still available to convert is
    ///   <c>source.Quantity − Σ(child line Quantity)</c>
    /// computed per fulfilment axis (delivery vs. billing) — see
    /// DocumentService.ComputeConsumptionAsync.
    /// </summary>
    public Guid? SourceLineId { get; set; }

    /// <summary>
    /// Input VAT claimability per ประมวลรัษฎากร §82/5.
    /// <para>true (default) = VAT บนบรรทัดนี้ไปเข้าบัญชี "ภาษีซื้อ 116"
    /// ตอน post JE และจะปรากฏใน ภพ.30 ฝั่ง Input VAT.</para>
    /// <para>false = VAT ต้องห้าม (§82/5(1)(3)(4)(6)(7) — เช่น ค่ารับรอง /
    /// น้ำมันรถยนต์นั่ง / ใบกำกับฯ ไม่สมบูรณ์). JE จะรวม VAT เข้ากับ
    /// ค่าใช้จ่ายเลย (Dr expense = ราคา + VAT) ไม่เข้า ภาษีซื้อ. ภพ.30
    /// จะไม่นับเป็น Input VAT.</para>
    /// </summary>
    public bool IsVatClaimable { get; set; } = true;

    /// <summary>Landed cost — บรรทัดต้นทุนแฝงของการซื้อ/นำเข้า (ค่าขนส่ง/
    /// อากร/ประกันภัย/เคลียร์ของ) บน PI/GRN: มูลค่าถูก "เกลี่ย" เข้าต้นทุน
    /// ต่อหน่วยของบรรทัดสินค้า TrackStock ในใบเดียวกัน (ถ่วงตามมูลค่า line)
    /// → WAC/movement UnitCost รวมต้นทุนแฝง และ JE default เข้า 115
    /// สินค้าคงเหลือ (ไม่ใช่ค่าใช้จ่าย) ตาม TFRS NPAEs บทที่ 8 (cost of
    /// purchase = ราคาซื้อ + ต้นทุนจัดหาจนถึงสภาพพร้อมขาย).</summary>
    public bool IsLandedCost { get; set; }

    /// <summary>เหตุผลที่ VAT บรรทัดนี้เคลมไม่ได้ — ใช้แสดงในรายงานสรรพากร
    /// + audit trail. ค่าที่ใช้บ่อย: "§82/5(3) ค่ารับรอง" / "§82/5(6)
    /// รถยนต์นั่ง" / "§82/5(1) ใบกำกับฯ ไม่สมบูรณ์" / free text. Null เมื่อ
    /// IsVatClaimable = true.</summary>
    public string? VatNonClaimableReason { get; set; }
}

/// <summary>
/// Contact (ลูกค้า / ผู้ขาย) — ที่อยู่เก็บแบบ structured ตามมาตรฐาน ETDA Schematron
/// (ต้องมี BuildingNumber + ตำบล/อำเภอ/จังหวัด/รหัสไปรษณีย์ สำหรับ e-Tax XML).
/// คงฟิลด์ Address ไว้เพื่อ backward compat — ใหม่ใช้ structured fields เป็นหลัก.
/// </summary>
public class Contact : TenantEntity
{
    public string Name { get; set; } = null!;
    public string? TaxId { get; set; }
    public string? BranchCode { get; set; }
    public string? BranchName { get; set; }
    public ContactType ContactType { get; set; } = ContactType.Individual;
    public bool IsCustomer { get; set; }
    public bool IsSupplier { get; set; }

    // === Address fields (structured per ETDA TradePartyType) ===
    /// <summary>Free-text address — kept for backward compat + display.
    /// New code should prefer the structured fields below.</summary>
    public string? Address { get; set; }
    /// <summary>บ้านเลขที่ — required by ETDA Schematron for CountryID=TH</summary>
    public string? BuildingNumber { get; set; }
    /// <summary>ชื่ออาคาร (optional)</summary>
    public string? BuildingName { get; set; }
    /// <summary>หมู่ที่ (village number) — common in rural / provincial Thai
    /// addresses, sits between BuildingNumber and StreetName.</summary>
    public string? Moo { get; set; }
    /// <summary>ถนน/ซอย</summary>
    public string? StreetName { get; set; }
    public string? SubDistrict { get; set; }   // ตำบล/แขวง
    public string? District { get; set; }      // อำเภอ/เขต
    public string? Province { get; set; }      // จังหวัด
    public string? PostalCode { get; set; }    // 5 digits
    public string CountryCode { get; set; } = "TH";

    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? ContactPerson { get; set; }
    /// <summary>รหัสผู้ติดต่อในระบบต้นทาง (integration เช่น TakeTime) — เก็บไว้
    /// match ผู้จำหน่าย/ลูกค้าเดิมเวลา sync รอบถัดไป กัน contact ซ้ำ. คู่กับ
    /// ExternalSystem บอกว่ามาจากระบบไหน (กันชนกันข้าม integration).</summary>
    public string? ExternalId { get; set; }
    public string? ExternalSystem { get; set; }
    /// <summary>LINE userId ของลูกค้า (ผูกผ่าน LINE OA / bind flow) — ใช้ส่ง
    /// เอกสาร (ใบแจ้งหนี้/ใบเสร็จ) ผ่าน LINE flex message. null = ลูกค้ายัง
    /// ไม่ผูก LINE → ระบบ fallback ไป email/print. PDPA: anonymize ตอน erase.</summary>
    public string? LineUserId { get; set; }
    /// <summary>ลูกค้าเงินสดหน้าร้านที่ "ไม่ประสงค์รับใบกำกับภาษี" — ใช้เป็น
    /// ผู้ซื้อกลางของใบกำกับขายปลีกจาก POS/API. ประกาศอธิบดีฯ ฉบับ 199 บังคับ
    /// เลขผู้เสียภาษีผู้ซื้อเฉพาะเมื่อผู้ซื้อเป็นผู้ประกอบการ VAT — contact นี้
    /// จึงได้รับยกเว้น hard-block §86/4 (เลข 13 หลัก/สาขา) ตอนอนุมัติ.</summary>
    public bool IsWalkInCustomer { get; set; }
    public bool IsActive { get; set; } = true;

    // ───── Per-contact GL account overrides ─────
    // Default at the system level is the first level-4+ account starting
    // with "113" (AR) / "212" (AP) / "212305" (IR/GR clearing) — see
    // DocumentService.FindAccountAsync. When a contact has a specific
    // override here, DocumentService uses THAT account on every doc
    // created against this contact. Lets shops with multiple ลูกหนี้
    // (เครดิตการค้า / ลูกหนี้พนักงาน / ลูกหนี้กรรมการ) book each contact
    // straight to the right ledger without manual JE adjustment.
    public Guid? DefaultArAccountId { get; set; }
    public ChartOfAccount? DefaultArAccount { get; set; }

    public Guid? DefaultApAccountId { get; set; }
    public ChartOfAccount? DefaultApAccount { get; set; }

    /// <summary>IR/GR clearing account — used by the goods-received-not-
    /// invoiced and invoice-received-not-goods accruals. Default 212305
    /// "ค่าใช้จ่ายค้างจ่ายอื่น" in the Thai SME template.</summary>
    public Guid? DefaultIrGrAccountId { get; set; }
    public ChartOfAccount? DefaultIrGrAccount { get; set; }

    /// <summary>ลูกค้ารายนี้ต้องออกเป็น "ใบกำกับภาษี" เสมอ (ลูกค้าจด VAT ที่ต้อง
    /// เคลมภาษีซื้อ) — เมื่อเลือก contact นี้ตอนสร้างเอกสารขาย ระบบ pre-select
    /// ชนิด TaxInvoice แทน Invoice + เตือนถ้าข้อมูล §86/4 (TaxId/สาขา/ที่อยู่)
    /// ไม่ครบ. Default false = เลือกชนิดเอกสารเองตามปกติ.</summary>
    public bool DefaultIssueTaxInvoice { get; set; } = false;

    /// <summary>เครดิตเทอมของลูกค้ารายนี้ — จำนวนวันเครดิต (เช่น 30 = Net 30).
    /// null = ใช้ค่าเริ่มต้นบริษัท (CompanySettings.DefaultPaymentDueDays). เมื่อ
    /// เลือก contact นี้ตอนสร้างเอกสารขาย ระบบเติม "วันครบกำหนด" = วันที่เอกสาร +
    /// PaymentDueDays และ label เครดิตเทอมให้อัตโนมัติ.</summary>
    public int? PaymentDueDays { get; set; }
    /// <summary>ป้ายเครดิตเทอม (เช่น "Net 30", "เงินสด", "60 วัน") — free-text
    /// override; ถ้าว่างระบบสร้างจาก PaymentDueDays ("Net {n}").</summary>
    public string? PaymentTerms { get; set; }

    /// <summary>Loyalty points balance — earned per POS sale, redeemable next visit.
    /// Default earn rate = 1 point per ฿100, set on the company config later.</summary>
    public int LoyaltyPoints { get; set; } = 0;
    public DateTime? LastVisitAt { get; set; }
    public int TotalVisitCount { get; set; } = 0;

    /// <summary>Credit limit (วงเงินเครดิต) สำหรับลูกค้า — null = ไม่จำกัด
    /// (พฤติกรรมเดิม). ระบบใช้ดู AR ค้างต่อลูกค้าเทียบเทียบขีดจำกัด เพื่อ
    /// เตือนตอนสร้าง Invoice ใหม่ที่จะทำให้ยอดค้างเกินวงเงิน. AI suggest
    /// endpoint (/ai/credit-limit/suggest) คำนวณ P75 จากลูกค้าปัจจุบัน
    /// เป็นค่าเริ่มต้น.</summary>
    public decimal? CreditLimit { get; set; }

    public ICollection<Document> Documents { get; set; } = new List<Document>();
}
