using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Pdf;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ PP36 ทีม F2 · คำตัดสินข้อ 131 — "ยอดจ่ายผู้รับเงิน" ของใบซื้อบริการต่างประเทศ (§83/6) + predicate "ใบนี้เป็นเจ้าของ ภ.พ.36 ไหม"
///
/// <para>เลขจริงจากใบที่ผู้ใช้ส่งมา (PP36_REVIEW): <b>PV-20260901-0001</b> Booking.com B.V. ค่าคอมมิชชั่น ฐาน 5,908.00 · VAT ประเมินเอง 413.56 ·
/// TotalAmount 6,321.56 ⇒ เงินออกจริง 5,908.00 · เดิมทุกเส้น "เงินออก/ยอดค้าง" อ่าน TotalAmount ⇒ ค้างปลอม 413.56 / จ่ายเกิน 7% /
/// PDF พิมพ์ "ยอดรวมสุทธิ 6,321.56"</para>
///
/// <para>ทุกหัวข้อมีสองครึ่ง (F2 ข้อ 8): ใบต่างประเทศได้ยอดจ่ายผู้รับเงิน <b>และ</b> ใบไทย/ใบขาย/ใบที่ไม่มี VAT ได้ TotalAmount เท่าเดิมทุกสตางค์</para>
/// </summary>
public class ForeignServicePayeeAmountTests
{
    private const decimal Base = 5_908.00m;
    private const decimal Vat = 413.56m;
    private const decimal Total = 6_321.56m;

    private static Document Pv(bool foreign = true, decimal total = Total, decimal vat = Vat, Guid? related = null,
        DocumentType type = DocumentType.PaymentVoucher)
        => new()
        {
            DocumentType = type, IsForeignService = foreign, TotalAmount = total, VatAmount = vat,
            SubTotal = total - vat, RelatedDocumentId = related,
        };

    // ═════ PayeeAmount ═════

    [Fact]
    public void ใบจริง_PV_20260901_0001_ยอดจ่ายผู้รับเงิน_5908_ไม่ใช่_6321_56()
    {
        Assert.Equal(Base, ForeignServiceVat.PayeeAmount(Pv()));
        // ตัวเดียวกับขาเครดิตของ JE (SplitCredit) — ยอดเอกสารกับ GL ไม่มีวันแยกทาง
        Assert.Equal(ForeignServiceVat.SplitCredit(true, Total, Vat).PayeeCredit, ForeignServiceVat.PayeeAmount(Pv()));
    }

    [Theory]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    public void ใบซื้อบริการต่างประเทศทุกชนิดที่ตั้ง_ภพ36_ได้ยอดจ่ายผู้รับเงิน(DocumentType type)
        => Assert.Equal(Base, ForeignServiceVat.PayeeAmount(Pv(type: type)));

    [Theory]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.CreditNote)]
    [InlineData(DocumentType.CertificateInLieu)]
    public void ทิศตรงข้าม_ใบไทยปกติ_ยอดเท่า_TotalAmount_ทุกสตางค์(DocumentType type)
        => Assert.Equal(Total, ForeignServiceVat.PayeeAmount(Pv(foreign: false, type: type)));

    [Theory]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.CertificateInLieu)]
    public void ธงหลุดไปอยู่บนชนิดที่ไม่ตั้ง_ภพ36_ไม่มีผลกับยอดจ่าย(DocumentType type)
        // E-8: AutoPost ไม่แยก 21912 ให้ชนิดเหล่านี้ ⇒ ยอดจ่ายต้องไม่ถูกหัก VAT ออก (ไม่งั้น GL กับเอกสารแยกทาง)
        => Assert.Equal(Total, ForeignServiceVat.PayeeAmount(Pv(type: type)));

    [Fact]
    public void ใบต่างประเทศที่ไม่มี_VAT_ยอดเท่าเดิม()
        => Assert.Equal(1_000m, ForeignServiceVat.PayeeAmount(Pv(total: 1_000m, vat: 0m)));

    [Fact]
    public void หัก_WHT_แล้ว_ยอดจ่าย_เท่า_ฐานลบ_WHT_ตามสูตรเดิม()
    {
        // ฐาน 1,000 · VAT ประเมินเอง 70 · หัก ม.70 15% = 150 ⇒ Total = 1,000 + 70 − 150 = 920 ⇒ จ่ายผู้รับเงิน 850
        Assert.Equal(850m, ForeignServiceVat.PayeeAmount(DocumentType.PaymentVoucher, true, 920m, 70m));
    }

    [Fact]
    public void ใบสกุลเงินต่างประเทศ_คิดในสกุลเอกสาร()
    {
        // USD 150.00 + VAT ประเมินเอง 10.50 = 160.50 ⇒ จ่ายผู้รับเงิน USD 150.00 (แปลงบาทที่ชั้น JE ตาม ExchangeRate เหมือนเดิม)
        var d = Pv(total: 160.50m, vat: 10.50m);
        d.Currency = "USD"; d.ExchangeRate = 36.25m;
        Assert.Equal(150.00m, ForeignServiceVat.PayeeAmount(d));
    }

    [Fact]
    public void ใบสำคัญจ่ายเก่าที่ปิดหนี้ใบต้นทางเจ้าของ_ภพ36_แต่ไม่ได้สืบทอดธง_ยังได้ยอดจ่ายผู้รับเงิน()
    {
        // ใบลูกก่อนรอบนี้ไม่มีธง แต่บรรทัดที่แปลงมาพก VAT ประเมินเองของใบต้นทาง (ConvertCoreAsync ยก VatAmountOverride)
        var pv = Pv(foreign: false, related: Guid.NewGuid());
        Assert.Equal(Total, ForeignServiceVat.PayeeAmount(pv));
        Assert.Equal(Base, ForeignServiceVat.PayeeAmount(pv, sourceOwnsPp36: true));
        // ใบลดหนี้ของใบต้นทางเจ้าของ ภ.พ.36 ก็เหมือนกัน (E-7)
        Assert.Equal(Base, ForeignServiceVat.PayeeAmount(Pv(foreign: false, type: DocumentType.CreditNote), sourceOwnsPp36: true));
        // ทิศตรงข้าม: ใบเสร็จฝั่งขาย ไม่มีความหมายของ ภ.พ.36
        Assert.Equal(Total, ForeignServiceVat.PayeeAmount(Pv(foreign: false, type: DocumentType.Receipt), sourceOwnsPp36: true));
    }

    [Fact]
    public void จ่ายครบ_5908_ใบปิดเป็นชำระแล้ว_ไม่มียอดค้างปลอม_413_56()
    {
        // ตัวตัดสินสถานะเดิม (DocumentSettlementState) + ตัวตั้งใหม่ ⇒ ใบตั้งหนี้ PI ต่างประเทศ จ่าย 5,908 = ปิด
        var pi = Pv(type: DocumentType.PurchaseInvoice);
        var after = DocumentSettlementState.Apply(ForeignServiceVat.PayeeAmount(pi), Base, DocumentStatus.Approved);
        Assert.Equal(DocumentStatus.Paid, after.Status);
        Assert.Equal(0m, after.BalanceDue);
        // ด่านจ่ายเกิน: ยอดค้าง 5,908 ⇒ จ่าย 6,321.56 (ยอดรวม VAT) ต้องถูกปฏิเสธ
        Assert.True(DocumentSettlementState.WouldOverpay(ForeignServiceVat.PayeeAmount(pi), Total));
        // ก่อนแก้ (ตัวตั้ง = TotalAmount): จ่าย 5,908 ยังค้าง 413.56 — ล็อกว่าบั๊กเดิมคือสิ่งนี้
        Assert.Equal(Vat, DocumentSettlementState.Apply(Total, Base, DocumentStatus.Approved).BalanceDue);
    }

    // ═════ OwnsPp36 ═════

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice, true, 413.56, false, true)]
    [InlineData(DocumentType.PurchaseInvoice, true, 413.56, true, true)]   // PO→PI ก็ยังตั้ง 21912 เอง
    [InlineData(DocumentType.Expense, true, 413.56, false, true)]
    [InlineData(DocumentType.PaymentVoucher, true, 413.56, false, true)]   // ใบสำคัญจ่ายตั้งต้น
    [InlineData(DocumentType.PaymentVoucher, true, 413.56, true, false)]   // ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทาง — ไม่นับซ้ำ
    [InlineData(DocumentType.PaymentVoucher, true, 0, false, false)]       // ไม่มี VAT
    [InlineData(DocumentType.PaymentVoucher, false, 413.56, false, false)] // ไม่ติ๊ก
    [InlineData(DocumentType.CertificateInLieu, true, 413.56, false, false)] // AutoPost ไม่แยก 21912
    [InlineData(DocumentType.TaxInvoice, true, 413.56, false, false)]
    public void OwnsPp36_ตรงกับ_JE_ที่ตั้ง_Cr_21912_จริง(DocumentType type, bool foreign, double vat, bool related, bool expected)
        => Assert.Equal(expected, ForeignServiceVat.OwnsPp36(type, foreign, (decimal)vat, related));

    [Fact]
    public void รูป_EF_ของ_PayeeAmount_และ_OwnsPp36_ให้ผลเท่าตัว_CSharp_ทุกชนิดเอกสาร()
    {
        // ห้ามเขียนสูตรซ้ำใน LINQ — ตัวนี้ล็อกว่าสำเนารูป Expression ไม่ drift จากตัวจริง
        var payee = ForeignServiceVat.PayeeAmountQuery.Compile();
        var owns = ForeignServiceVat.OwnsPp36Query.Compile();
        foreach (var type in Enum.GetValues<DocumentType>())
        foreach (var foreign in new[] { true, false })
        foreach (var vat in new[] { 0m, Vat, -5m })
        foreach (var related in new Guid?[] { null, Guid.NewGuid() })
        {
            var d = Pv(foreign, Total, vat, related, type);
            Assert.Equal(ForeignServiceVat.PayeeAmount(d), payee(d));
            Assert.Equal(ForeignServiceVat.OwnsPp36(d), owns(d));
        }
    }

    // ═════ ForeignServiceEvidence (RD-83/6-UNFLAGGED) — ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทาง ═════

    [Fact]
    public void คำเตือนลืมติ๊ก_ปิดเมื่อใบต้นทางเป็นเจ้าของ_ภพ36_แล้ว()
        => Assert.False(ForeignServiceEvidence.Judge(false, Vat, "NL", null, true,
            settledSourceNumber: "PI-20260901-0001", settledSourceOwnsPp36: true).Suspect);

    [Fact]
    public void ใบต้นทางยังไม่ติ๊ก_คำเตือนชี้ไปแก้ที่ใบต้นทาง_ไม่สั่งให้ติ๊กบนใบลูก()
    {
        var v = ForeignServiceEvidence.Judge(false, Vat, "NL", null, true, settledSourceNumber: "PI-20260901-0001");
        Assert.True(v.Suspect);
        Assert.Contains("PI-20260901-0001", v.Reason);
        Assert.Contains("ห้ามติ๊กบนใบนี้", v.Reason);
        Assert.Contains(ForeignServiceEvidence.RuleCode, v.Reason);
    }

    [Fact]
    public void ทิศตรงข้าม_ใบตั้งต้นยังได้ข้อความเดิม_ให้ติ๊กบนฟอร์ม()
    {
        var v = ForeignServiceEvidence.Judge(false, Vat, "NL", null, true);
        Assert.True(v.Suspect);
        Assert.Contains("ติ๊กช่อง", v.Reason);
        Assert.DoesNotContain("ห้ามติ๊กบนใบนี้", v.Reason);
    }

    // ═════ T-4b (คำตัดสินข้อ 130) — ผู้รับต่างประเทศ · ไม่หัก · ไม่จำแนกเงินได้ ═════

    private static readonly DateTime PayDay = new(2026, 9, 1);

    [Fact]
    public void ผู้รับนิติบุคคลต่างประเทศ_ไม่หักไม่จำแนก_เตือน_ม70_อัตราจากตัวตัดสินเดียว()
    {
        var w = ForeignWhtPayeeCheck.UnclassifiedNoWithholdingWarning(ForeignPayeeWhtScope.Section70, "NL", PayDay,
            anyLineWithheld: false, anyLineClassified: false, hasWhtCertificate: false);
        Assert.NotNull(w);
        Assert.Contains(ForeignWhtPayeeCheck.UnclassifiedRuleCode, w);
        Assert.Contains(ForeignWhtRateResolver.Section70Reference, w);
        Assert.Contains($"{ForeignWhtRateResolver.Section70GeneralRate:0.##}%", w);
        Assert.Contains("ภ.ง.ด.54", w);
        Assert.Contains("อนุสัญญา", w);
    }

    [Theory]
    [InlineData(true, false, false)]   // มีบรรทัดหักแล้ว — ตัวตรวจรายบรรทัดรับช่วง
    [InlineData(false, true, false)]   // จำแนกเงินได้แล้ว
    [InlineData(false, false, true)]   // มี 50 ทวิ ผูกใบแล้ว (รอบโอน OTA หักที่ JE รอบโอน)
    public void ทิศตรงข้าม_หักหรือจำแนกหรือมี_50ทวิ_แล้ว_ไม่เตือน(bool withheld, bool classified, bool cert)
        => Assert.Null(ForeignWhtPayeeCheck.UnclassifiedNoWithholdingWarning(ForeignPayeeWhtScope.Section70, "NL", PayDay,
            withheld, classified, cert));

    [Fact]
    public void บุคคลธรรมดาต่างประเทศ_ไม่อยู่ใต้_ม70_เงียบ()
        => Assert.Null(ForeignWhtPayeeCheck.UnclassifiedNoWithholdingWarning(ForeignPayeeWhtScope.Individual, "NL", PayDay,
            false, false, false));

    [Fact]
    public void ผู้รับที่มีเลขนิติบุคคลไทย_เตือนแบบบอกว่าไม่รู้_พร้อมทางเลือก_ภงด53()
    {
        var w = ForeignWhtPayeeCheck.UnclassifiedNoWithholdingWarning(ForeignPayeeWhtScope.ThaiRegisteredUnknown, "NL", PayDay,
            false, false, false);
        Assert.NotNull(w);
        Assert.Contains("ภ.ง.ด.53", w);
    }

    // ═════ E-12 §65 ตรี ═════

    private static readonly Guid Acc = Guid.NewGuid();

    private static Document ForeignExpenseDoc() => new()
    {
        DocumentType = DocumentType.PaymentVoucher, IsForeignService = true,
        TotalAmount = Total, VatAmount = Vat, SubTotal = Base, DocumentDate = PayDay,
        Lines = new List<DocumentLine>
        {
            new() { AccountId = Acc, Amount = Base, VatAmount = Vat, IsVatClaimable = true, Description = "ค่าคอมมิชชั่น Booking.com" },
        },
    };

    private static Section65TerValidator.Result Eval65(Document d, string? taxId, bool payeeIsForeign, bool? linked = null)
        => Section65TerValidator.Evaluate(d,
            new Dictionary<Guid, (string Code, string Name)> { [Acc] = ("52150", "ค่าคอมมิชชั่น") },
            "Booking.com B.V.", taxId,
            new Section65TerValidator.Context(10_000_000m, 1_000_000m, LinkedToThaiOperation: linked, PayeeIsForeign: payeeIsForeign));

    [Fact]
    public void ผู้รับต่างประเทศที่มี_VAT_number_ไม่ฟ้อง_ไม่มีเลขผู้เสียภาษี()
        => Assert.DoesNotContain(Eval65(ForeignExpenseDoc(), "NL805734958B01", payeeIsForeign: true).Findings,
            f => f.RuleCode == "RD-65ter(11)(18)");

    [Fact]
    public void ผู้รับต่างประเทศไม่มีเลขเลย_ข้อความบอกให้เก็บเลขของประเทศผู้รับ_ไม่ใช่เลขไทย()
    {
        var f = Assert.Single(Eval65(ForeignExpenseDoc(), null, payeeIsForeign: true).Findings, x => x.RuleCode == "RD-65ter(11)(18)");
        Assert.Contains("ประเทศผู้รับ", f.Message);
        Assert.Contains("ไม่ต้องใช้เลขไทย", f.Message);
    }

    [Fact]
    public void ทิศตรงข้าม_ผู้รับไทยไม่มีเลข_ข้อความเดิมทุกตัวอักษร()
    {
        var f = Assert.Single(Eval65(ForeignExpenseDoc(), null, payeeIsForeign: false).Findings, x => x.RuleCode == "RD-65ter(11)(18)");
        Assert.Equal("ไม่มีเลขประจำตัวผู้เสียภาษีของผู้รับเงิน — เสี่ยงถูกถือเป็นรายจ่ายต้องห้าม โปรดเพิ่มก่อนปิดรอบ", f.Message);
    }

    [Fact]
    public void ข้อ19_บวกกลับฐานค่าใช้จ่าย_ไม่ใช่ยอดรวม_VAT_ประเมินเอง()
    {
        var f = Assert.Single(Eval65(ForeignExpenseDoc(), "NL805734958B01", true, linked: false).Findings, x => x.RuleCode == "RD-65ter(19)");
        Assert.Equal(Base, f.AddBackAmount);   // เดิม 6,321.56 (TotalAmount) — VAT 413.56 เป็นภาษีซื้อ 11640 ไม่ใช่ค่าใช้จ่าย
    }

    [Fact]
    public void ข้อ19_บรรทัด_VAT_เคลมไม่ได้_รวม_VAT_ในยอดบวกกลับ()
    {
        // บริษัทไม่จด VAT: VAT ภ.พ.36 เคลมไม่ได้ ⇒ เป็นต้นทุน ⇒ บวกกลับรวม VAT (สูตรเดียวกับรายบรรทัด)
        var d = ForeignExpenseDoc();
        d.Lines.First().IsVatClaimable = false;
        var f = Assert.Single(Eval65(d, "NL805734958B01", true, linked: false).Findings, x => x.RuleCode == "RD-65ter(19)");
        Assert.Equal(Total, f.AddBackAmount);
    }

    // ═════ PDF — ตัวตัดสินแถวยอดของสอง renderer ═════

    [Fact]
    public void PDF_ใบต่างประเทศ_แถวสุดท้ายเป็นยอดจ่ายผู้รับเงิน_แถว_VAT_บอกว่าไม่จ่ายผู้รับเงิน()
    {
        var L = DocumentLabels.For("th");
        var t = PdfGenerationService.ResolvePrintTotals(Pv(), L);
        Assert.True(t.SelfAssessedVat);
        Assert.Equal(Base, t.Grand);
        Assert.Equal("ยอดจ่ายผู้รับเงิน", t.NetLabel);
        Assert.Contains("ภ.พ.36", t.VatLabel);
        Assert.Contains("ไม่จ่ายให้ผู้รับเงิน", t.VatLabel);
    }

    [Fact]
    public void PDF_ทิศตรงข้าม_ใบไทยปกติพิมพ์เหมือนเดิมทุกตัวอักษร()
    {
        var L = DocumentLabels.For("th");
        var t = PdfGenerationService.ResolvePrintTotals(Pv(foreign: false), L);
        Assert.False(t.SelfAssessedVat);
        Assert.Equal(Total, t.Grand);
        Assert.Equal("ภาษีมูลค่าเพิ่ม 7%", t.VatLabel);   // ข้อความที่ QuestPDF เคยฝังตรง ๆ — ไทยต้องเหมือนเดิม
        Assert.Equal(L.TotalNet, t.NetLabel);
        Assert.Equal("ยอดรวมสุทธิ", t.NetLabel);
    }

    [Fact]
    public void PDF_โหมดอังกฤษไม่มีอักษรไทยหลุด_ทั้งใบปกติและใบต่างประเทศ()
    {
        var L = DocumentLabels.For("en");
        var normal = PdfGenerationService.ResolvePrintTotals(Pv(foreign: false), L);
        Assert.Equal("VAT 7%", normal.VatLabel);   // เดิม QuestPDF พิมพ์ "ภาษีมูลค่าเพิ่ม 7%" ในโหมดอังกฤษ
        var foreign = PdfGenerationService.ResolvePrintTotals(Pv(), L);
        foreach (var s in new[] { normal.VatLabel, normal.NetLabel, foreign.VatLabel, foreign.NetLabel })
            Assert.DoesNotContain(s, c => c >= '฀' && c <= '๿');
        Assert.Equal(Base, foreign.Grand);
    }

    [Fact]
    public void PDF_มี_JE_แต่หักล้างเป็นศูนย์_ไม่บอกว่าไม่มี_JE()
    {
        var s = PdfGenerationService.NettedToZeroJournalReason(2);
        Assert.Contains("2 รายการ", s);
        Assert.Contains("หักล้างกันเป็นศูนย์", s);
        Assert.DoesNotContain("ยังไม่มีรายการในสมุดรายวัน", s);
    }
}

/// <summary>รอบ PP36 ทีม F2 — migration ซ่อมยอดจ่ายแล้ว/ค้างของใบต่างประเทศเดิม (F2 หลักการ 9): ล็อกเลข enum ในคำสั่ง SQL + ขอบเขตแถวที่แตะ</summary>
public class ForeignServicePayeeBalanceMigrationTests
{
    [Fact]
    public void เลข_enum_ในคำสั่งตรงกับ_enum_จริง()
    {
        Assert.Equal(8, (int)DocumentType.PurchaseInvoice);
        Assert.Equal(9, (int)DocumentType.Expense);
        Assert.Equal(13, (int)DocumentType.PaymentVoucher);
        Assert.Equal(6, (int)DocumentStatus.Voided);
        Assert.Equal(5, (int)DocumentStatus.Paid);
        Assert.Equal(new[] { 2, 3, 4, 7 },
            new[] { DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.PartiallyPaid, DocumentStatus.Overdue }.Select(s => (int)s));
        Assert.Equal(1, (int)PaymentType.Cash);
    }

    [Fact]
    public void ทุกคำสั่งจำกัดเฉพาะใบต่างประเทศที่มี_VAT_ไม่ถูกลบ_ไม่ยกเลิก()
    {
        foreach (var sql in ForeignServicePayeeBalanceMigration.Statements().Append(ForeignServicePayeeBalanceMigration.OverpaidReportSql))
        {
            Assert.Contains("\"IsForeignService\" = true", sql);
            Assert.Contains("\"VatAmount\" > 0", sql);
            Assert.Contains("\"IsDeleted\" = false", sql);
            Assert.Contains("\"Status\" <> 6", sql);
        }
    }

    [Fact]
    public void ใบสำคัญจ่ายเงินสด_แตะเฉพาะแถวสภาพเดิม_ตั้งต้น_ไม่อ้างใบต้นทาง()
    {
        var sql = ForeignServicePayeeBalanceMigration.CashVoucherSql;
        Assert.Contains("\"PaidAmount\" = \"TotalAmount\" - \"VatAmount\"", sql);
        Assert.Contains("\"PaidAmount\" = \"TotalAmount\" AND \"BalanceDue\" = 0", sql);   // สูตรเดิมทุกตัวอักษร ⇒ รันซ้ำไม่ตรงอีก
        Assert.Contains("\"RelatedDocumentId\" IS NULL", sql);   // ใบที่ปิดหนี้ใบต้นทาง: เงินออกเดิมรวม VAT ⇒ คนซ่อม
        Assert.Contains("\"PaymentType\" = 1", sql);
    }

    [Fact]
    public void ใบตั้งหนี้_ห้ามแตะใบที่จ่ายเกินยอดจ่ายผู้รับเงินแล้ว_และรายงานใบนั้นแทน()
    {
        var sql = ForeignServicePayeeBalanceMigration.PayableSql;
        Assert.Contains("\"PaidAmount\" <= \"TotalAmount\" - \"VatAmount\" + 0.005", sql);
        Assert.Contains("ABS(\"BalanceDue\" - (\"TotalAmount\" - \"PaidAmount\")) <= 0.005", sql);   // สูตรเดิม ⇒ idempotent
        Assert.Contains("GREATEST(\"TotalAmount\" - \"VatAmount\" - \"PaidAmount\", 0)", sql);
        // สองชุดแถวต้องไม่ทับกัน: รายงานนับเฉพาะแถวที่ PayableSql ไม่แตะ (จ่ายเกิน)
        Assert.Contains("\"PaidAmount\" > \"TotalAmount\" - \"VatAmount\" + 0.005", ForeignServicePayeeBalanceMigration.OverpaidReportSql);
        Assert.DoesNotContain(";", ForeignServicePayeeBalanceMigration.OverpaidReportSql);   // SqlQueryRaw ห้ามมี ; ท้าย
    }
}

/// <summary>
/// ฝ่ายค้าน F2+F3 (merge 6a1baa8a) P1-1 / P1-2 — ธงบนใบสำคัญจ่ายที่ปิดหนี้ใบต้นทาง ต้องเท่ากับ "ใบต้นทางเป็นเจ้าของ ภ.พ.36" ·
/// ด่าน JE ใช้ตัวตัดสินเดียวกับ JE (<c>VatNotPaidToPayee</c>) · ใบเพิ่มหนี้ของใบเจ้าของ ภ.พ.36 ค้างเฉพาะยอดจ่ายผู้รับเงิน
/// </summary>
public class ForeignServiceLinkedVoucherTests
{
    private const decimal Base = 5_908.00m, Vat = 413.56m, Total = 6_321.56m;

    private static List<Accounting.Services.JournalPostingGuard.LineFacts> SettlementJe(decimal amount) => new()
    {
        new("21210", AccountType.Liability, amount, 0m),   // Dr เจ้าหนี้การค้า
        new("11120", AccountType.Asset, 0m, amount),       // Cr ธนาคาร
    };

    private static Accounting.Services.JournalPostingGuard.DocFacts Pv(bool flag, bool sourceOwns)
        => new(DocumentType.PaymentVoucher, Base, Vat, 0m, Total, IsForeignService: flag, SourceOwnsPp36: sourceOwns);

    // ── (ก) PV ร่างเก่าที่แปลงจากใบ Booking (ธงยังไม่ตาม) อนุมัติ ⇒ JE จ่าย 5,908 ตามใบต้นทาง ⇒ ด่านต้องคาด 5,908 ด้วย ──
    [Fact]
    public void ก_PV_เก่าไม่มีธงแต่ใบต้นทางเจ้าของ_ภพ36_ด่านไม่ตีตก_JE_ยอดจ่ายผู้รับเงิน()
    {
        var f = Accounting.Services.JournalPostingGuard.Validate(SettlementJe(Base), Pv(flag: false, sourceOwns: true));
        Assert.DoesNotContain(f, x => x.RuleCode == "JE-NO-COUNTERPART");
        // ก่อนแก้ (ด่านดูธงของใบลูกอย่างเดียว): ตีตกด้วยข้อความผิด — ล็อกว่านี่คือบั๊กที่ถูกแก้
        Assert.Contains(Accounting.Services.JournalPostingGuard.Validate(SettlementJe(Base), Pv(false, false)),
            x => x.RuleCode == "JE-NO-COUNTERPART");
    }

    [Fact]
    public void ทิศตรงข้าม_PV_จ่ายใบไทยเต็มยอด_ด่านผ่านเหมือนเดิม_และจ่ายขาด_VAT_ยังถูกจับ()
    {
        Assert.DoesNotContain(Accounting.Services.JournalPostingGuard.Validate(SettlementJe(Total), Pv(false, false)),
            x => x.IsError);
        Assert.Contains(Accounting.Services.JournalPostingGuard.Validate(SettlementJe(Base), Pv(false, false)),
            x => x.RuleCode == "JE-NO-COUNTERPART");
    }

    // ── (ข) ปลดธงบน PV ที่ใบต้นทางเป็นเจ้าของ ⇒ ปฏิเสธพร้อมทางไปต่อ ──
    [Fact]
    public void ข_ปลดธงบนใบลูกของใบเจ้าของ_ภพ36_ปฏิเสธ_ชี้ใบต้นทาง()
    {
        var d = ForeignServiceVat.LinkedVoucherFlag(sourceOwnsPp36: true, currentFlag: true, requested: false, "PI-20260901-0001");
        Assert.NotNull(d.Refusal);
        Assert.Contains("PI-20260901-0001", d.Refusal);
        Assert.True(d.Flag);   // ไม่เปลี่ยน
    }

    // ── (ค) ติ๊กธงบน PV ที่จ่ายใบไทย 10,700 ⇒ ปฏิเสธ (เดิมจ่าย 10,000 ใบต้นทางค้าง 700 ถาวรเงียบ) ──
    [Fact]
    public void ค_ติ๊กธงบนใบลูกของใบไทย_ปฏิเสธ_ไม่ให้จ่ายขาด_VAT()
    {
        var d = ForeignServiceVat.LinkedVoucherFlag(sourceOwnsPp36: false, currentFlag: false, requested: true, "PI-TH-0001");
        Assert.NotNull(d.Refusal);
        Assert.False(d.Flag);
        Assert.Equal(10_700m, ForeignServiceVat.PayeeAmount(DocumentType.PaymentVoucher, false, 10_700m, 700m, sourceOwnsPp36: false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ไม่ได้ระบุค่า_ระบบตั้งตามใบต้นทาง_และบอกให้จดหมายเหตุ(bool sourceOwns, bool current)
    {
        var d = ForeignServiceVat.LinkedVoucherFlag(sourceOwns, current, requested: null, "SRC-1");
        Assert.Null(d.Refusal);
        Assert.Equal(sourceOwns, d.Flag);
        Assert.True(d.ChangedBySystem);
        Assert.Contains(ForeignServiceVat.LinkedFlagRuleCode, ForeignServiceVat.LinkedFlagNote(d.Flag, "SRC-1"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ทิศตรงข้าม_ค่าที่ส่งมาตรงใบต้นทาง_ผ่านโดยไม่จดหมายเหตุ(bool owns)
    {
        var d = ForeignServiceVat.LinkedVoucherFlag(owns, owns, requested: owns, "SRC-1");
        Assert.Null(d.Refusal);
        Assert.Equal(owns, d.Flag);
        Assert.False(d.ChangedBySystem);
    }

    // ── P1-2: ใบเพิ่มหนี้/ใบลดหนี้ของใบเจ้าของ ภ.พ.36 — ยอดหนี้ผู้ขาย = ยอดจ่ายผู้รับเงิน (ตรงกับ JE ที่ตั้งเจ้าหนี้ 1,000) ──
    [Theory]
    [InlineData(DocumentType.DebitNote)]
    [InlineData(DocumentType.CreditNote)]
    public void ใบเพิ่มลดหนี้ของใบเจ้าของ_ภพ36_ยอดหนี้_1000_ไม่ใช่_1070(DocumentType type)
    {
        Assert.Equal(1_000m, ForeignServiceVat.PayeeAmount(type, false, 1_070m, 70m, sourceOwnsPp36: true));
        Assert.Equal(1_070m, ForeignServiceVat.PayeeAmount(type, false, 1_070m, 70m, sourceOwnsPp36: false));   // ทิศตรงข้าม
    }

    // ── migration: เงื่อนไข "ใบต้นทางเจ้าของ" ใน SQL ตรงกับ OwnsPp36Query · แตะเฉพาะใบที่ยังไม่อนุมัติ ──
    [Fact]
    public void migration_ธงใบลูกและใบเพิ่มลดหนี้_แตะเฉพาะใบยังไม่อนุมัติ_สองทิศ()
    {
        var flag = ForeignServicePayeeBalanceMigration.LinkedVoucherFlagSql;
        Assert.Contains("\"Status\" IN (0, 1, 8)", flag);
        Assert.Contains("\"IsForeignService\" <> x.owns", flag);   // สองทิศ + รันซ้ำไม่แตะอีก
        Assert.Contains("d2.\"CompanyId\"", flag);
        var adj = ForeignServicePayeeBalanceMigration.LinkedAdjustmentNoteSql;
        Assert.Contains("\"DocumentType\" IN (5, 6)", adj);
        Assert.Contains("\"BalanceDue\" = d.\"TotalAmount\"", adj);   // สูตรเดิมเท่านั้น ⇒ idempotent
        Assert.Contains("\"Status\" IN (0, 1, 8)", adj);
        Assert.Equal(0, (int)DocumentStatus.Draft);
        Assert.Equal(1, (int)DocumentStatus.WaitingApproval);
        Assert.Equal(8, (int)DocumentStatus.Rejected);
        Assert.Equal(5, (int)DocumentType.DebitNote);
        Assert.Equal(6, (int)DocumentType.CreditNote);
        Assert.Contains("s.\"DocumentType\" IN (8, 9) OR (s.\"DocumentType\" = 13 AND s.\"RelatedDocumentId\" IS NULL)",
            ForeignServicePayeeBalanceMigration.SourceOwnsSql);
        Assert.Equal(4, ForeignServicePayeeBalanceMigration.Statements().Count);
    }
}
