using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>คู่ TypeCode ↔ ชื่อไทย ↔ schema ของ e-Tax — Schematron ของ ETDA
/// บังคับให้คู่กัน**ตรงตัวอักษร** (TIV-Document-003 และญาติ) ผิดหนึ่งธง = XML
/// ถูก reject ทั้งใบ. เดิมแผนที่นี้ถูกคัดลอกด้วยมือไว้ 4 ที่ — เทสต์ชุดนี้ล็อก
/// ค่า canonical ไว้หลังยุบเหลือ <see cref="EtaxDocumentTypeMap"/> ตัวเดียว
/// (control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control)</summary>
public class EtaxDocumentTypeMapTests
{
    private static Document Doc(DocumentType type,
        bool cashReceipt = false, bool combined = false) => new()
    {
        DocumentType = type,
        IssuedAsCashReceipt = cashReceipt,
        CombinedInvoiceTaxInvoice = combined,
    };

    [Fact]
    public void คู่_TypeCode_กับชื่อ_ตาม_ETDA_ครบทุกชนิด()
    {
        // (เอกสาร, TypeCode, ชื่อที่ Schematron บังคับ)
        var cases = new (Document D, string Code, string Name)[]
        {
            (Doc(DocumentType.TaxInvoice), "388", "ใบกำกับภาษี"),
            (Doc(DocumentType.TaxInvoice, combined: true), "T02", "ใบแจ้งหนี้/ใบกำกับภาษี"),
            (Doc(DocumentType.TaxInvoice, cashReceipt: true), "T03", "ใบเสร็จรับเงิน/ใบกำกับภาษี"),
            (Doc(DocumentType.Receipt), "T03", "ใบเสร็จรับเงิน/ใบกำกับภาษี"),
            (Doc(DocumentType.DebitNote), "80", "ใบเพิ่มหนี้"),
            (Doc(DocumentType.CreditNote), "81", "ใบลดหนี้"),
        };
        foreach (var (d, code, name) in cases)
        {
            Assert.Equal(code, EtaxDocumentTypeMap.TypeCode(d));
            Assert.Equal(name, EtaxDocumentTypeMap.NameTh(d));
        }
    }

    [Fact]
    public void ขายเงินสดชนะใบรวม_เมื่อทั้งสองธงเปิดพร้อมกัน()
    {
        // cash sale = receipt+tax invoice ในใบเดียว → T03 มาก่อน T02 เสมอ
        var d = Doc(DocumentType.TaxInvoice, cashReceipt: true, combined: true);
        Assert.Equal("T03", EtaxDocumentTypeMap.TypeCode(d));
        Assert.Equal("ใบเสร็จรับเงิน/ใบกำกับภาษี", EtaxDocumentTypeMap.NameTh(d));
    }

    [Fact]
    public void schema_root_กับ_ram_ต้องเป็นตระกูลเดียวกันเสมอ()
    {
        foreach (var t in new[] { DocumentType.TaxInvoice, DocumentType.Receipt,
            DocumentType.DebitNote, DocumentType.CreditNote })
        {
            var root = EtaxDocumentTypeMap.SchemaRoot(t);
            var ram = EtaxDocumentTypeMap.RamSuffix(t);
            // "TaxInvoice_..." คู่ "TaxInvoice_..." · "DebitCreditNote_..." คู่กัน
            Assert.Equal(root.Split('_')[0], ram.Split('_')[0]);
        }
        Assert.Equal("TaxInvoice_CrossIndustryInvoice",
            EtaxDocumentTypeMap.SchemaRoot(DocumentType.Receipt));   // T03 ใช้ schema TaxInvoice
        Assert.Equal("DebitCreditNote_CrossIndustryInvoice",
            EtaxDocumentTypeMap.SchemaRoot(DocumentType.CreditNote));
    }
}
