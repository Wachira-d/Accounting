using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>แผนที่ TypeCode ↔ ชื่อเอกสาร ↔ schema ของ e-Tax Invoice (ETDA
/// ขมธอ.3-2560) — **ที่เดียวของระบบ**
///
/// ═══ ทำไมต้องมี ═══
/// เดิมแผนที่ชุดนี้ถูก**คัดลอกด้วยมือไว้ 4 ที่** (สร้าง XML · metadata PDF/A-3
/// สองไฟล์ · TypeCode) — Schematron ของ ETDA (TIV-Document-003 และญาติ) บังคับให้
/// TypeCode กับชื่อในเอกสาร **คู่กันตรงตัวอักษร** สำเนาไหนตามไม่ทันหนึ่งธง =
/// XML ถูก reject ทั้งใบ (defect class "รายการที่คัดลอกมาด้วยมือ = drift แน่นอน")
///
/// ═══ ข้อห้ามสำคัญ ═══
/// ชื่อในแผนที่นี้เป็น **canonical ตามประกาศ ETDA** — ห้ามเอาหัวกระดาษที่ผู้ใช้
/// ตั้งเอง (CustomTitle / DocumentTitleOverridesJson) มาใส่แทนเด็ดขาด:
/// หัวกระดาษปรับแต่งได้ แต่ชื่อใน XML/metadata ต้องคู่กับ TypeCode เสมอ
/// (นี่คือเหตุที่แผนที่นี้ *จงใจ* ไม่เดินผ่าน ComputeDocumentTitle)
/// </summary>
public static class EtaxDocumentTypeMap
{
    /// <summary>TypeCode ตาม UN/EDIFACT 1001 + codelist ของ ETDA
    /// — ลำดับเงื่อนไขสำคัญ: ขายเงินสด (T03) ต้องมาก่อนใบรวม (T02)</summary>
    public static string TypeCode(Document doc) => doc.DocumentType switch
    {
        DocumentType.TaxInvoice when doc.IssuedAsCashReceipt => "T03",
        DocumentType.TaxInvoice when doc.CombinedInvoiceTaxInvoice => "T02",
        DocumentType.TaxInvoice => "388",
        DocumentType.Receipt => "T03",
        DocumentType.DebitNote => "80",
        DocumentType.CreditNote => "81",
        _ => "388",
    };

    /// <summary>ชื่อไทยที่คู่กับ TypeCode — ต้องตรงตัวอักษรตาม Schematron</summary>
    public static string NameTh(Document doc) => doc.DocumentType switch
    {
        DocumentType.TaxInvoice when doc.IssuedAsCashReceipt => "ใบเสร็จรับเงิน/ใบกำกับภาษี",   // T03
        DocumentType.TaxInvoice when doc.CombinedInvoiceTaxInvoice => "ใบแจ้งหนี้/ใบกำกับภาษี", // T02
        DocumentType.TaxInvoice => "ใบกำกับภาษี",                                              // 388
        DocumentType.Receipt => "ใบเสร็จรับเงิน/ใบกำกับภาษี",                                   // T03
        DocumentType.DebitNote => "ใบเพิ่มหนี้",                                                // 80
        DocumentType.CreditNote => "ใบลดหนี้",                                                  // 81
        _ => "ใบกำกับภาษี",
    };

    /// <summary>ชื่อ root element ของ XML schema (T03 ใช้ schema TaxInvoice)</summary>
    public static string SchemaRoot(DocumentType type) => type switch
    {
        DocumentType.DebitNote or DocumentType.CreditNote => "DebitCreditNote_CrossIndustryInvoice",
        _ => "TaxInvoice_CrossIndustryInvoice",
    };

    /// <summary>namespace suffix ของ ram (คู่กับ <see cref="SchemaRoot"/> เสมอ)</summary>
    public static string RamSuffix(DocumentType type) => type switch
    {
        DocumentType.DebitNote or DocumentType.CreditNote
            => "DebitCreditNote_ReusableAggregateBusinessInformationEntity",
        _ => "TaxInvoice_ReusableAggregateBusinessInformationEntity",
    };
}
