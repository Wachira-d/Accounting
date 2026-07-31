namespace Accounting.Services.Implementations.Pdf;

/// <summary>
/// พจนานุกรมข้อความบนเอกสารที่ออก (PDF/HTML) — ศูนย์กลางเดียวสำหรับไทย/อังกฤษ
/// เพื่อให้ทุก renderer ใช้ชุดคำเดียวกัน ไม่ต้องเขียน <c>isEn ? "..." : "..."</c>
/// กระจายทั่วไฟล์ (ซึ่งทำให้ตกหล่นและ drift)
///
/// <para><b>การเลือกภาษา</b> (เรียงลำดับความสำคัญ) — ดู
/// <c>PdfGenerationService.ResolveDocumentLanguage</c>:</para>
/// <list type="number">
/// <item>ภาษาที่ระบุมากับคำขอสร้างไฟล์ (<c>request.Language</c>) — ใช้ตอนกด
/// "ดาวน์โหลดฉบับภาษาอังกฤษ" เฉพาะครั้ง</item>
/// <item><c>Document.DocumentLanguage</c> — ตรึงภาษาไว้กับใบนั้น (ลูกค้าต่างชาติ)</item>
/// <item><c>DocumentTemplate.Language</c> — เทมเพลตเฉพาะงาน</item>
/// <item><c>CompanySettings.DocumentLanguage</c> — ค่าตั้งต้นของทั้งบริษัท</item>
/// <item>"th"</item>
/// </list>
///
/// <para><b>ข้อควรระวังด้านกฎหมาย</b>: หัวเอกสารตามประมวลรัษฎากร §86/4 ต้องมีคำว่า
/// "ใบกำกับภาษี" เป็นภาษาไทยบนเอกสารที่ใช้ยื่น/เคลมภาษีในประเทศ. เมื่อเลือกภาษา
/// อังกฤษ ระบบจึงพิมพ์หัวแบบ <b>สองภาษา</b> ("Tax Invoice / ใบกำกับภาษี") ไม่ใช่
/// ตัดไทยทิ้ง — ดู <see cref="LegalTitle"/>. ส่วน label อื่น ๆ (คอลัมน์ตาราง,
/// ยอดรวม, ที่อยู่) เปลี่ยนเป็นอังกฤษล้วนได้ ไม่มีข้อบังคับ.</para>
/// </summary>
public sealed class DocumentLabels
{
    public bool IsEnglish { get; }

    private readonly IReadOnlyDictionary<string, string> _map;

    private DocumentLabels(bool isEnglish, IReadOnlyDictionary<string, string> map)
    { IsEnglish = isEnglish; _map = map; }

    /// <summary>ข้อความตาม key — คืน key เองถ้าไม่พบ (ทำให้เห็นทันทีว่าตกหล่น)</summary>
    public string this[string key] => _map.TryGetValue(key, out var v) ? v : key;

    public static DocumentLabels For(string? lang)
    {
        var isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        return new DocumentLabels(isEn, isEn ? _en : _th);
    }

    /// <summary>หัวเอกสารที่ต้องคงคำไทยไว้ตามกฎหมาย — โหมดอังกฤษพิมพ์สองภาษา
    /// "English / ไทย" เพื่อให้ยังเป็นเอกสารตาม §86/4 ที่ใช้เคลมภาษีซื้อได้จริง
    /// (ตัดไทยทิ้ง = ใบกำกับไม่สมบูรณ์ ผู้ซื้อเคลมไม่ได้ §82/5(1))</summary>
    public string LegalTitle(string thaiTitle, string englishTitle)
        => IsEnglish ? $"{englishTitle} / {thaiTitle}" : thaiTitle;

    /// <summary>วันที่ตามรูปแบบของภาษา — ไทย dd/MM/yyyy, อังกฤษ dd MMM yyyy</summary>
    public string Date(DateTime d) => IsEnglish
        ? d.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
        : d.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>รหัสสาขาแบบอ่านออก — "00000" = สำนักงานใหญ่ (ประกาศอธิบดีฯ 199)</summary>
    public string Branch(string? code)
    {
        var c = (code ?? "").Trim();
        if (c.Length == 0) return "";
        return c == "00000"
            ? this["branch_head_office"]
            : string.Format(this["branch_number"], c.TrimStart('0').Length == 0 ? c : c.TrimStart('0'));
    }


    // ── Named accessors ─────────────────────────────────────────────
    // ใช้แทน indexer ในสตริง interpolated ($"...") — เลี่ยงเครื่องหมาย
    // คำพูดซ้อนใน interpolation hole ซึ่งอ่านยากและพังง่ายเวลาแก้
    public string AfterDiscountAlloc => this["after_discount_alloc"];
    public string BranchHeadOffice => this["branch_head_office"];
    public string BranchNumber => this["branch_number"];
    public string CertCertifier => this["cert_certifier"];
    public string CertInfo => this["cert_info"];
    public string CertPosition => this["cert_position"];
    public string CertReason => this["cert_reason"];
    public string CertWitness => this["cert_witness"];
    public string CnCorrectedValue => this["cn_corrected_value"];
    public string CnDecrease => this["cn_decrease"];
    public string CnDifference => this["cn_difference"];
    public string CnIncrease => this["cn_increase"];
    public string CnOriginalNumber => this["cn_original_number"];
    public string CnOriginalValue => this["cn_original_value"];
    public string CnRefOriginal => this["cn_ref_original"];
    public string ColAmount => this["col_amount"];
    public string ColAmountIncl => this["col_amount_incl"];
    public string ColDiscount => this["col_discount"];
    public string ColItem => this["col_item"];
    public string ColQty => this["col_qty"];
    public string ColUnit => this["col_unit"];
    public string ColUnitPrice => this["col_unit_price"];
    public string ColUnitPriceIncl => this["col_unit_price_incl"];
    public string CopyDuplicate => this["copy_duplicate"];
    public string CopyOriginal => this["copy_original"];
    public string DocDate => this["doc_date"];
    public string DocNumber => this["doc_number"];
    public string DueDate => this["due_date"];
    public string FromOcr => this["from_ocr"];
    public string GlPosting => this["gl_posting"];
    public string InclVatSuffix => this["incl_vat_suffix"];
    public string NoContent => this["no_content"];
    public string NotTaxInvoiceNote => this["not_tax_invoice_note"];
    public string Notes => this["notes"];
    public string PartyCustomer => this["party_customer"];
    public string PartyPayee => this["party_payee"];
    public string PartyVendor => this["party_vendor"];
    public string PartyVendorOrPayee => this["party_vendor_or_payee"];
    public string PaymentDate => this["payment_date"];
    public string PaymentInfo => this["payment_info"];
    public string Phone => this["phone"];
    public string Reference => this["reference"];
    public string StatusVoided => this["status_voided"];
    public string TaxId => this["tax_id"];
    public string TaxIdShort => this["tax_id_short"];
    public string Terms => this["terms"];
    public string TotalAfterDiscountBase => this["total_after_discount_base"];
    public string TotalBeforeBillDiscount => this["total_before_bill_discount"];
    public string TotalBillDiscount => this["total_bill_discount"];
    public string TotalDepositApplied => this["total_deposit_applied"];
    public string TotalDiscount => this["total_discount"];
    public string TotalGrand => this["total_grand"];
    public string TotalNet => this["total_net"];
    public string TotalNetPayable => this["total_net_payable"];
    public string TotalSubtotal => this["total_subtotal"];
    public string TotalVat => this["total_vat"];
    public string TotalWht => this["total_wht"];

    private static readonly Dictionary<string, string> _th = new()
    {
        // ── ป้ายสำเนา/สถานะ ──
        ["copy_original"] = "ต้นฉบับ",
        ["copy_duplicate"] = "สำเนา",
        ["status_voided"] = "ยกเลิก",

        // ── หัวข้อมูลเอกสาร ──
        ["doc_number"] = "เลขที่",
        ["doc_date"] = "วันที่",
        ["due_date"] = "ครบกำหนด",
        ["reference"] = "อ้างอิง",
        ["tax_id"] = "เลขประจำตัวผู้เสียภาษี",
        ["tax_id_short"] = "เลขผู้เสียภาษี",
        ["phone"] = "โทร",
        ["branch_head_office"] = "สำนักงานใหญ่",
        ["branch_number"] = "สาขาที่ {0}",

        // ── คู่สัญญา ──
        ["party_customer"] = "ลูกค้า",
        ["party_vendor"] = "ผู้ขาย",
        ["party_payee"] = "ผู้รับเงิน",
        ["party_vendor_or_payee"] = "ผู้ขาย/ผู้รับเงิน",

        // ── ตารางรายการ ──
        ["col_item"] = "รายการ",
        ["col_qty"] = "จำนวน",
        ["col_unit"] = "หน่วย",
        ["col_unit_price"] = "ราคา/หน่วย",
        ["col_unit_price_incl"] = "ราคา/หน่วย (รวม VAT)",
        ["col_discount"] = "ส่วนลด",
        ["col_amount"] = "จำนวนเงิน",
        ["col_amount_incl"] = "จำนวนเงิน (รวม VAT)",

        // ── ยอดรวม ──
        ["total_before_bill_discount"] = "ก่อนหักท้ายบิล",
        ["total_bill_discount"] = "ส่วนลดท้ายบิล",
        ["total_discount"] = "ส่วนลดรวม",
        ["total_subtotal"] = "ยอดรวมก่อน VAT",
        ["total_after_discount_base"] = "ยอดหลังหักส่วนลด (ฐานภาษี)",
        ["total_vat"] = "ภาษีมูลค่าเพิ่ม",
        ["total_wht"] = "ภาษีหัก ณ ที่จ่าย",
        ["total_deposit_applied"] = "หักเงินมัดจำ",
        ["total_grand"] = "ยอดรวมทั้งสิ้น",
        ["total_net"] = "ยอดรวมสุทธิ",
        ["total_net_payable"] = "ยอดชำระสุทธิ",
        ["incl_vat_suffix"] = "(รวม VAT)",
        ["after_discount_alloc"] = "หลังเฉลี่ยส่วนลด",

        // ── ใบเพิ่ม/ลดหนี้ (§86/9, §86/10) ──
        ["cn_ref_original"] = "อ้างอิงใบกำกับภาษีเดิม (มาตรา 86/{0})",
        ["cn_original_number"] = "เลขที่ {0}  ลงวันที่ {1}",
        ["cn_original_value"] = "มูลค่าตามใบเดิม",
        ["cn_corrected_value"] = "มูลค่าที่ถูกต้อง",
        ["cn_difference"] = "ผลต่าง",
        ["cn_increase"] = "เพิ่ม",
        ["cn_decrease"] = "ลด",

        // ── ส่วนท้าย / อื่น ๆ ──
        ["payment_info"] = "ข้อมูลชำระเงิน",
        ["terms"] = "เงื่อนไข",
        ["notes"] = "หมายเหตุ",
        ["gl_posting"] = "การบันทึกบัญชี",
        ["cert_info"] = "ข้อมูลการรับรอง",
        ["cert_reason"] = "เหตุผลที่ไม่ได้รับใบเสร็จ",
        ["cert_certifier"] = "ผู้รับรอง",
        ["cert_witness"] = "พยาน",
        ["cert_position"] = "ตำแหน่ง",
        ["payment_date"] = "วันที่จ่ายเงิน",
        ["from_ocr"] = "(จาก OCR)",
        ["no_content"] = "(ไม่มีเนื้อหา)",
        ["not_tax_invoice_note"] =
            "* เอกสารนี้ไม่ใช่ใบกำกับภาษี — ใบกำกับภาษีจะออกให้เมื่อมีการใช้บริการ/ชำระครบถ้วน",
    };

    private static readonly Dictionary<string, string> _en = new()
    {
        ["copy_original"] = "ORIGINAL",
        ["copy_duplicate"] = "COPY",
        ["status_voided"] = "VOID",

        ["doc_number"] = "No.",
        ["doc_date"] = "Date",
        ["due_date"] = "Due date",
        ["reference"] = "Reference",
        ["tax_id"] = "Tax ID",
        ["tax_id_short"] = "Tax ID",
        ["phone"] = "Tel",
        ["branch_head_office"] = "Head Office",
        ["branch_number"] = "Branch {0}",

        ["party_customer"] = "Customer",
        ["party_vendor"] = "Vendor",
        ["party_payee"] = "Payee",
        ["party_vendor_or_payee"] = "Vendor / Payee",

        ["col_item"] = "Description",
        ["col_qty"] = "Qty",
        ["col_unit"] = "Unit",
        ["col_unit_price"] = "Unit price",
        ["col_unit_price_incl"] = "Unit price (incl. VAT)",
        ["col_discount"] = "Discount",
        ["col_amount"] = "Amount",
        ["col_amount_incl"] = "Amount (incl. VAT)",

        ["total_before_bill_discount"] = "Before bill discount",
        ["total_bill_discount"] = "Bill discount",
        ["total_discount"] = "Total discount",
        ["total_subtotal"] = "Subtotal (excl. VAT)",
        ["total_after_discount_base"] = "Net after discount (taxable)",
        ["total_vat"] = "VAT",
        ["total_wht"] = "Withholding tax",
        ["total_deposit_applied"] = "Less deposit",
        ["total_grand"] = "Grand total",
        ["total_net"] = "Net total",
        ["total_net_payable"] = "Net payable",
        ["incl_vat_suffix"] = "(incl. VAT)",
        ["after_discount_alloc"] = "after discount allocation",

        ["cn_ref_original"] = "Reference to original tax invoice (Section 86/{0})",
        ["cn_original_number"] = "No. {0} dated {1}",
        ["cn_original_value"] = "Original value",
        ["cn_corrected_value"] = "Corrected value",
        ["cn_difference"] = "Difference",
        ["cn_increase"] = "increase",
        ["cn_decrease"] = "decrease",

        ["payment_info"] = "Payment details",
        ["terms"] = "Terms",
        ["notes"] = "Notes",
        ["gl_posting"] = "Journal entry",
        ["cert_info"] = "Certification details",
        ["cert_reason"] = "Reason no receipt was issued",
        ["cert_certifier"] = "Certified by",
        ["cert_witness"] = "Witness",
        ["cert_position"] = "Position",
        ["payment_date"] = "Payment date",
        ["from_ocr"] = "(from OCR)",
        ["no_content"] = "(no content)",
        ["not_tax_invoice_note"] =
            "* This document is not a tax invoice — a tax invoice will be issued upon service delivery / full payment.",
    };
}
