using System.Xml.Linq;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 250 WP-04 ข้อ 7 (AUDIT_249 · C F7 §3) — ฟิลด์ XML e-Tax ตาม ETDA ขมธอ.3-2560 จากตัวสร้างจริง (<c>EtaxInvoiceService.BuildEtaxXml</c>) ·
/// ทุกข้อสองทิศ: DocumentTypeCode ต่อชนิด (388/T02/T03/80/81 + root schema) · IssueDateTime +07:00 (เวลา UTC ตอนดึกต้องเป็นวันถัดไปของไทย) ·
/// TXID = เลข 13 + สาขา 5 หลัก (ผู้ขาย/ผู้ซื้อ) · บุคคลธรรมดา NIDN · VAT/ยอดรวม (ยอดรวมไม่หัก WHT) · InvoiceCurrencyCode ·
/// ใบลด/เพิ่มหนี้อ้างเลขที่+วันที่ใบเดิม (ใบกำกับไม่มีการอ้าง) · ด่านเลขผู้ขาย/ผู้ซื้อก่อนสร้าง XML (<see cref="EtaxPartyTaxIdGuard"/>)
/// </summary>
public class EtaxXmlRound250Tests
{
    private const string SellerTaxId = "0105556000009";
    private const string BuyerTaxId = "0107537000017";

    private static Company Company(string? branch = "00000") => new()
    {
        Name = "บริษัท ผู้ขาย 250 จำกัด", TaxId = SellerTaxId, BranchCode = branch,
        Address = "99 ถนนสุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพมหานคร 10110", PostalCode = "10110",
        Province = "กรุงเทพมหานคร", District = "คลองเตย", SubDistrict = "คลองเตย",
    };

    private static Document Doc(DocumentType type, string? currency = "THB", ContactType contactType = ContactType.JuristicPerson,
        string buyerTaxId = BuyerTaxId, string? buyerBranch = "00003")
    {
        var d = new Document
        {
            DocumentType = type, DocumentNumber = type + "-250-0001", Status = DocumentStatus.Approved,
            DocumentDate = new DateTime(2026, 11, 5, 20, 30, 0, DateTimeKind.Utc),   // = 6 พ.ย. 03:30 เวลาไทย
            SubTotal = 1_000m, VatAmount = 70m, WithholdingTaxAmount = 30m, TotalAmount = 1_040m, Currency = currency,
            Contact = new Contact
            {
                Name = "บริษัท ผู้ซื้อ 250 จำกัด", ContactType = contactType, TaxId = buyerTaxId, BranchCode = buyerBranch,
                Address = "1 ถนนพระราม 4 กรุงเทพมหานคร 10500", PostalCode = "10500", IsCustomer = true,
            },
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        return d;
    }

    private static XElement Root(Document doc, Document? original = null, Company? company = null)
        => XDocument.Parse(EtaxInvoiceService.BuildEtaxXml(doc, company ?? Company(), "ETAX-T", original)).Root!;

    private static IEnumerable<XElement> All(XElement root, string local) => root.Descendants().Where(e => e.Name.LocalName == local);
    private static XElement One(XElement root, string local) => Assert.Single(All(root, local));
    private static XElement Child(XElement parent, string local) => parent.Elements().Single(e => e.Name.LocalName == local);

    private static string TypeCodeOf(XElement root) => Child(One(root, "ExchangedDocument"), "TypeCode").Value;

    [Theory]
    [InlineData(DocumentType.TaxInvoice, false, false, "388", "ใบกำกับภาษี", "TaxInvoice_CrossIndustryInvoice")]
    [InlineData(DocumentType.TaxInvoice, true, false, "T02", "ใบแจ้งหนี้/ใบกำกับภาษี", "TaxInvoice_CrossIndustryInvoice")]
    [InlineData(DocumentType.TaxInvoice, true, true, "T03", "ใบเสร็จรับเงิน/ใบกำกับภาษี", "TaxInvoice_CrossIndustryInvoice")]   // ขายเงินสดชนะใบรวม
    [InlineData(DocumentType.Receipt, false, false, "T03", "ใบเสร็จรับเงิน/ใบกำกับภาษี", "TaxInvoice_CrossIndustryInvoice")]
    [InlineData(DocumentType.DebitNote, false, false, "80", "ใบเพิ่มหนี้", "DebitCreditNote_CrossIndustryInvoice")]
    [InlineData(DocumentType.CreditNote, false, false, "81", "ใบลดหนี้", "DebitCreditNote_CrossIndustryInvoice")]
    public void DocumentTypeCode_คู่ชื่อและschemaต่อชนิด(DocumentType type, bool combined, bool cash, string code, string name, string root)
    {
        var doc = Doc(type);
        doc.CombinedInvoiceTaxInvoice = combined;
        doc.IssuedAsCashReceipt = cash;
        var original = type is DocumentType.CreditNote or DocumentType.DebitNote ? Doc(DocumentType.TaxInvoice) : null;
        var x = Root(doc, original);
        Assert.Equal(root, x.Name.LocalName);
        Assert.Equal(code, TypeCodeOf(x));
        Assert.Equal(name, Child(One(x, "ExchangedDocument"), "Name").Value);
        // ทิศตรงข้าม: ชื่อ/รหัสคู่กับแผนที่กลางตัวเดียว (ห้ามมีสำเนาที่สอง)
        Assert.Equal(EtaxDocumentTypeMap.TypeCode(doc), code);
        Assert.Equal(EtaxDocumentTypeMap.NameTh(doc), name);
    }

    [Fact]
    public void IssueDateTime_เวลาไทยพร้อมOffset0700_ไม่ใช่UTCเปล่า()
    {
        var issue = Child(One(Root(Doc(DocumentType.TaxInvoice)), "ExchangedDocument"), "IssueDateTime").Value;
        Assert.Equal("2026-11-06T03:30:00.000+07:00", issue);   // 20:30 UTC = 03:30 วันถัดไปของไทย
        Assert.DoesNotContain("2026-11-05", issue);              // ทิศตรงข้าม: ไม่ได้วันที่ "เมื่อวาน"
        Assert.DoesNotContain("Z", issue);
        var created = Child(One(Root(Doc(DocumentType.TaxInvoice)), "ExchangedDocument"), "CreationDateTime").Value;
        Assert.EndsWith("+07:00", created);
    }

    [Fact]
    public void TXID_ผู้ขายผู้ซื้อ13บวกสาขา5หลัก_สาขาสั้นเติมศูนย์หน้า_บุคคลธรรมดาเป็นNIDN()
    {
        var x = Root(Doc(DocumentType.TaxInvoice), company: Company(branch: "1"));
        var seller = Child(Child(One(x, "SellerTradeParty"), "SpecifiedTaxRegistration"), "ID");
        Assert.Equal("TXID", seller.Attribute("schemeID")!.Value);
        Assert.Equal(SellerTaxId + "00001", seller.Value);                         // สาขา "1" ⇒ 00001 (BranchID 5 หลัก)
        var buyer = Child(Child(One(x, "BuyerTradeParty"), "SpecifiedTaxRegistration"), "ID");
        Assert.Equal("TXID", buyer.Attribute("schemeID")!.Value);
        Assert.Equal(BuyerTaxId + "00003", buyer.Value);
        Assert.Equal(18, buyer.Value.Length);
        // สำนักงานใหญ่ (ไม่ระบุสาขา) ⇒ 00000
        var hq = Child(Child(One(Root(Doc(DocumentType.TaxInvoice, buyerBranch: null)), "BuyerTradeParty"), "SpecifiedTaxRegistration"), "ID");
        Assert.Equal(BuyerTaxId + "00000", hq.Value);
        // ทิศตรงข้าม: ผู้ซื้อบุคคลธรรมดา ⇒ NIDN 13 หลัก ไม่มีสาขาต่อท้าย
        var person = Child(Child(One(Root(Doc(DocumentType.TaxInvoice, contactType: ContactType.Individual, buyerTaxId: "1101700207030")),
            "BuyerTradeParty"), "SpecifiedTaxRegistration"), "ID");
        Assert.Equal("NIDN", person.Attribute("schemeID")!.Value);
        Assert.Equal("1101700207030", person.Value);
    }

    [Fact]
    public void VAT_ยอดรวม_GrandTotalเท่าฐานบวกVAT_ไม่หักWHT()
    {
        var sum = One(Root(Doc(DocumentType.TaxInvoice)), "SpecifiedTradeSettlementHeaderMonetarySummation");
        Assert.Equal("70", Child(sum, "TaxTotalAmount").Value);
        Assert.Equal("1000", Child(sum, "TaxBasisTotalAmount").Value);
        Assert.Equal("1070", Child(sum, "GrandTotalAmount").Value);    // TotalAmount ของใบ = 1,040 (หัก WHT) ⇒ ห้ามใช้
        Assert.NotEqual("1040", Child(sum, "GrandTotalAmount").Value);
        Assert.Empty(sum.Elements().Where(e => e.Name.LocalName is "OriginalInformationAmount" or "DifferenceInformationAmount"));   // ใบกำกับไม่มีช่องของ CN/DN
    }

    [Theory]
    [InlineData("THB", "THB")]
    [InlineData(null, "THB")]      // ไม่ระบุ = บาท
    [InlineData("USD", "USD")]     // ทิศตรงข้าม: สกุลอื่นไม่ถูกบังคับเป็น THB
    public void InvoiceCurrencyCode_ISO4217(string? currency, string expected)
    {
        var cc = One(Root(Doc(DocumentType.TaxInvoice, currency: currency)), "InvoiceCurrencyCode");
        Assert.Equal(expected, cc.Value);
        Assert.Equal("ISO 4217 3A", cc.Attribute("listID")!.Value);
    }

    [Fact]
    public void ใบลดหนี้อ้างเลขที่และวันที่ใบเดิม_ใบกำกับไม่มีการอ้าง()
    {
        var original = Doc(DocumentType.TaxInvoice);
        original.DocumentNumber = "TIV-250-0099";
        original.DocumentDate = new DateTime(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc);
        var cn = Doc(DocumentType.CreditNote);
        cn.SubTotal = 200m; cn.VatAmount = 14m;
        var x = Root(cn, original);
        var refDoc = One(x, "AdditionalReferencedDocument");
        Assert.Equal("TIV-250-0099", Child(refDoc, "IssuerAssignedID").Value);
        Assert.Equal("388", Child(refDoc, "ReferenceTypeCode").Value);
        Assert.Equal("20261015", One(refDoc, "DateTimeString").Value);
        var sum = One(x, "SpecifiedTradeSettlementHeaderMonetarySummation");
        Assert.Equal("1000", Child(sum, "OriginalInformationAmount").Value);
        Assert.Equal("200", Child(sum, "DifferenceInformationAmount").Value);
        // ทิศตรงข้าม: ใบกำกับภาษีไม่อ้างใบอื่น
        Assert.Empty(All(Root(Doc(DocumentType.TaxInvoice)), "AdditionalReferencedDocument"));
    }

    // ═══ ด่านเลขผู้ขาย/ผู้ซื้อก่อนสร้าง XML (เดิม inline ใน GenerateAsync :231-268) ═══

    [Theory]
    [InlineData(SellerTaxId, true)]
    [InlineData("0-1055-56000-00-9", true)]   // ขีด/ช่องว่างตัดทิ้ง
    [InlineData("010555600000", false)]       // 12 หลัก
    [InlineData("01055560000091", false)]     // 14 หลัก
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Seller_เลข13หลักเท่านั้น(string? taxId, bool ok)
        => Assert.Equal(ok, EtaxPartyTaxIdGuard.Seller(taxId) == null);

    // รอบ 250 WP-04 ต่อยอด (ฝ่ายค้าน): ชนิดผู้ติดต่อ · checksum · สาขาของเลข 18 หลัก · บุคคลธรรมดาไม่มีเลข = ผ่าน (ETDA OTHR "N/A")
    [Theory]
    [InlineData(ContactType.JuristicPerson, BuyerTaxId, null, true)]
    [InlineData(ContactType.JuristicPerson, BuyerTaxId + "00003", "00003", true)]   // 18 = เลข + สาขาตรงกับช่องสาขา
    [InlineData(ContactType.JuristicPerson, BuyerTaxId + "00003", "3", true)]       // สาขา "3" normalize = 00003
    [InlineData(ContactType.JuristicPerson, BuyerTaxId + "00003", "00004", false)]  // สาขาไม่ตรง ⇒ PDF/XML คนละสาขา
    [InlineData(ContactType.JuristicPerson, BuyerTaxId + "00000", null, true)]      // สำนักงานใหญ่
    [InlineData(ContactType.JuristicPerson, "0107537000018", null, false)]          // checksum ผิด
    [InlineData(ContactType.JuristicPerson, "010753700001", null, false)]           // 12 หลัก — ตัวประกอบ TXID เคยเติมศูนย์แต่งเลข
    [InlineData(ContactType.JuristicPerson, "0107537000017000", null, false)]       // 16 หลัก
    [InlineData(ContactType.JuristicPerson, "  ", null, false)]                     // นิติบุคคลต้องมีเลข
    [InlineData(ContactType.GovernmentAgency, null, null, false)]
    [InlineData(ContactType.Unknown, null, null, false)]                            // ไม่รู้ชนิด ⇒ ไม่เดาว่าไม่จด VAT
    [InlineData(ContactType.Individual, null, null, true)]                          // บุคคลไม่จด VAT ไม่มีเลข ⇒ OTHR
    [InlineData(ContactType.Individual, "1101700207030", null, true)]
    [InlineData(ContactType.Individual, "1101700207031", null, false)]              // checksum ผิด
    [InlineData(ContactType.Individual, "1101700207030" + "00000", null, false)]    // 18 หลักไม่ใช่รูปของบุคคล
    public void Buyer_ชนิด_checksum_สาขา_สองทิศ(ContactType type, string? taxId, string? branch, bool ok)
    {
        var problem = EtaxPartyTaxIdGuard.Buyer("บริษัท ผู้ซื้อ 250 จำกัด", taxId, type, branch);
        Assert.Equal(ok, problem == null);
        if (!ok && !string.IsNullOrWhiteSpace(taxId)) Assert.Contains("บริษัท ผู้ซื้อ 250 จำกัด", problem);
    }

    [Fact]
    public void TXID_เลข18หลักใช้13หลักแรกบวกสาขาของผู้ติดต่อ_บุคคลไม่มีเลขเป็นOTHR()
    {
        var x = Root(Doc(DocumentType.TaxInvoice, buyerTaxId: BuyerTaxId + "00003", buyerBranch: "00003"));
        Assert.Equal(BuyerTaxId + "00003", Child(Child(One(x, "BuyerTradeParty"), "SpecifiedTaxRegistration"), "ID").Value);
        // ตัวประกอบไม่เชื่อ 5 หลักท้ายของเลข — ใช้สาขาของผู้ติดต่อ (ตรงกับที่ PDF พิมพ์) · ด่านบล็อกกรณีไม่ตรงก่อนถึงที่นี่
        var y = Root(Doc(DocumentType.TaxInvoice, buyerTaxId: BuyerTaxId + "00003", buyerBranch: "00004"));
        Assert.Equal(BuyerTaxId + "00004", Child(Child(One(y, "BuyerTradeParty"), "SpecifiedTaxRegistration"), "ID").Value);
        // ทิศตรงข้าม: บุคคลธรรมดาไม่มีเลข ⇒ OTHR "N/A" (ไม่แต่งเลข)
        var person = Child(Child(One(Root(Doc(DocumentType.TaxInvoice, contactType: ContactType.Individual, buyerTaxId: "")), "BuyerTradeParty"),
            "SpecifiedTaxRegistration"), "ID");
        Assert.Equal("OTHR", person.Attribute("schemeID")!.Value);
        Assert.Equal("N/A", person.Value);
    }

    // ฝ่ายค้าน WP-04 ข้อ 6: วันที่ใบเดิมของใบลด/เพิ่มหนี้ต้องเป็นวันเดียวกับ IssueDateTime (+07:00) ใน XML ของใบเดิม — ค่าที่เก็บตามกติกา
    // (ThaiDate.CalendarDateUtc: วันไทย ณ 00:00Z) ได้วันเดิม (ทิศที่ต้องไม่ถูกแตะ) · แถวเก่าที่เก็บเวลาจริง ≥ 17:00Z = วันไทยถัดไป
    [Theory]
    [InlineData(0, "20261015")]
    [InlineData(16, "20261015")]
    [InlineData(17, "20261016")]
    [InlineData(23, "20261016")]
    public void ใบลดหนี้อ้างวันที่ใบเดิม_วันเดียวกับIssueDateTimeของใบเดิม(int hourUtc, string expected)
    {
        var original = Doc(DocumentType.TaxInvoice);
        original.DocumentDate = new DateTime(2026, 10, 15, hourUtc, 0, 0, DateTimeKind.Utc);
        var x = Root(Doc(DocumentType.CreditNote), original);
        Assert.Equal(expected, One(One(x, "AdditionalReferencedDocument"), "DateTimeString").Value);
        // วันเดียวกับที่ XML ของใบเดิมประกาศ
        var originalIssue = Child(One(Root(original), "ExchangedDocument"), "IssueDateTime").Value;
        Assert.StartsWith($"{expected[..4]}-{expected[4..6]}-{expected[6..]}", originalIssue);
    }
}
