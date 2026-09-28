using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 199 ทีม K3 — แก้ผลฝ่ายค้าน <c>erp-review/2026-09-25/review-r199-ocr.md</c>
///
/// <para>A-1: ผู้ขายในเครือ (ชื่อซ้อน · เลขต่าง) ถูกนับเป็น "เรา" แล้วเส้นสร้างเอกสารสร้างผู้ติดต่อซ้ำ/ผู้ติดต่อที่เป็นตัวเรา ·
/// A-2: เลขศูนย์ล้วน/placeholder ขยายเป็น "นิติบุคคลเดียวกัน" · A-3: คะแนนรายช่องไม่ย้ายตามค่าที่ถูกสลับฝั่ง ·
/// C-2: หัวพิมพ์ซ้ำคำนวณสดจากธงบริษัท ไม่ใช่บทบาทที่ตรึงตอนออกเลข · B-2/B-3/B-4 (P3)</para>
///
/// <para>สองครึ่งตาม CLAUDE.md §H ทุกข้อ: "ใบที่พังกลับมาถูก" และ "ใบที่ถูกอยู่แล้วไม่ถูกแตะ"</para>
/// </summary>
public class OcrReview199Tests
{
    // เลขทดสอบผ่าน mod-11 จริง (ThaiTaxId.IsValid) — ไม่ใช่เลขของนิติบุคคลจริง
    private const string ParagonGroupTin = "0105550123451";      // บริษัทในเครือ (ผู้ขาย)
    private const string ParagonDevTin = "0105537000015";        // tenant เรา
    private const string ParagonDev = "บริษัท สยามพารากอน ดีเวลลอปเม้นท์ จำกัด";
    private const string ParagonGroup = "สยามพารากอน";
    private const string OurTin = "0203562005871";
    private const string OurName = "หจก.แอม แฮปปี้เนส";
    private const string CpTin = "0107567000414";
    private const string CpAxtra = "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)";

    [Fact]
    public void เลขทดสอบผ่านchecksum()
    {
        Assert.True(ThaiTaxId.IsValid(ParagonGroupTin));
        Assert.True(ThaiTaxId.IsValid(ParagonDevTin));
        Assert.True(ThaiTaxId.IsValid(OurTin));
        Assert.True(ThaiTaxId.IsValid(CpTin));
    }

    // ═════════ A-1 — เลขภาษีตัดสินก่อนชื่อ ═════════

    [Fact]
    public void บริษัทในเครือชื่อซ้อน_เลขต่าง_ไม่ใช่เรา()
    {
        // ก่อนแก้: IsSelf ยอม "ชื่อกระดาษสั้นกว่าชื่อเรา" เสมอ ⇒ true ⇒ เส้นสร้างเอกสารสร้างผู้ติดต่อซ้ำทุกใบ
        Assert.True(OcrSelfPartyGuard.IsSelf(ParagonGroup, ParagonDev));                 // ชื่ออย่างเดียวยังซ้อน (พฤติกรรมเดิมของ IsSelf)
        Assert.False(OcrSelfPartyGuard.IsOurContact(ParagonGroupTin, ParagonGroup, ParagonDevTin, ParagonDev, null));
    }

    [Fact]
    public void เลขเรา_คือเรา_แม้ชื่อบนกระดาษจะเป็นชื่ออื่น()
        => Assert.True(OcrSelfPartyGuard.IsOurContact("0-2035-62005-87-1", "ร้านอะไรก็ได้", OurTin, OurName, null));

    [Fact]
    public void ชื่อเราถูกตัด_ไม่มีเลข_ยังเป็นเรา()
    {
        Assert.True(OcrSelfPartyGuard.IsOurContact(null, "แอม แฮปปี้", OurTin, OurName, null));
        Assert.True(OcrSelfPartyGuard.IsOurContact("", "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส", OurTin, OurName, null));
    }

    [Fact]
    public void เลขที่อ่านมาไม่ผ่านchecksum_ชื่อยังตัดสิน()
    {
        // เลขเราอ่านเพี้ยนหลักเดียว (checksum ตก) ⇒ ไม่ใช่เลขที่ใช้ได้ ⇒ ถอยไปชื่อ (ไม่ถือว่าเป็นคนละนิติบุคคล)
        Assert.False(ThaiTaxId.IsValid("0203562005872"));
        Assert.True(OcrSelfPartyGuard.IsOurContact("0203562005872", OurName, OurTin, OurName, null));
    }

    [Fact]
    public void บริษัทเรายังไม่มีเลข_ชื่อตัดสิน()
        => Assert.True(OcrSelfPartyGuard.IsOurContact(ParagonGroupTin, ParagonGroup, "-", ParagonDev, null));

    [Fact]
    public void ทิศตรงข้าม_ผู้ขายจริง_ไม่ใช่เรา()
    {
        Assert.False(OcrSelfPartyGuard.IsOurContact(CpTin, CpAxtra, OurTin, OurName, null));
        Assert.False(OcrSelfPartyGuard.IsOurContact(null, CpAxtra, OurTin, OurName, null));
    }

    [Fact]
    public void Fallback_ผู้ขายคือเรา_ไม่สร้างผู้ติดต่อ_และบอกทางไปต่อ()
    {
        var outcome = OcrSelfPartyGuard.DecideVendorContactFallback(hasContact: false, vendorIsUs: true, sameTaxIdRows: 0, hasVendorName: true);
        Assert.Equal(OcrVendorContactFallback.BlockVendorIsUs, outcome);
        var msg = OcrSelfPartyGuard.VendorContactBlockMessage(outcome);
        Assert.NotNull(msg);
        Assert.Contains("ไม่สร้างผู้ติดต่อ", msg);
        Assert.Contains("เปลี่ยนชนิดเอกสาร", msg);      // ทางไปต่อ 1: สำเนาใบขายของเรา
        Assert.Contains("เลือกผู้ติดต่อ", msg);         // ทางไปต่อ 2: ผู้ขายจริงเป็นรายอื่น
    }

    [Fact]
    public void Fallback_เลขมีแถวอยู่แล้ว_ห้ามสร้างแถวซ้ำ()
    {
        var outcome = OcrSelfPartyGuard.DecideVendorContactFallback(hasContact: false, vendorIsUs: false, sameTaxIdRows: 2, hasVendorName: true);
        Assert.Equal(OcrVendorContactFallback.BlockExistingRows, outcome);
        Assert.NotNull(OcrSelfPartyGuard.VendorContactBlockMessage(outcome));
    }

    [Fact]
    public void Fallback_ทิศตรงข้าม_ผู้ขายใหม่จริง_ยังสร้างผู้ติดต่อได้_และผู้ใช้เลือกแล้วชนะ()
    {
        Assert.Equal(OcrVendorContactFallback.CreateNew,
            OcrSelfPartyGuard.DecideVendorContactFallback(hasContact: false, vendorIsUs: false, sameTaxIdRows: 0, hasVendorName: true));
        // ผู้ใช้เลือกผู้ติดต่อเอง (หรือตัวเลือกสาขาผูกได้) — ใช้ตามนั้น แม้ผู้ขายบนกระดาษจะเป็นเรา
        Assert.Equal(OcrVendorContactFallback.Keep,
            OcrSelfPartyGuard.DecideVendorContactFallback(hasContact: true, vendorIsUs: true, sameTaxIdRows: 3, hasVendorName: true));
        // ไม่มีชื่อให้สร้าง ⇒ ตกด่าน "ไม่มีผู้ติดต่อ" ตามเดิม (ไม่ใช่ข้อความ "เป็นเรา")
        var noName = OcrSelfPartyGuard.DecideVendorContactFallback(hasContact: false, vendorIsUs: false, sameTaxIdRows: 0, hasVendorName: false);
        Assert.Equal(OcrVendorContactFallback.Keep, noName);
        Assert.Null(OcrSelfPartyGuard.VendorContactBlockMessage(noName));
        Assert.Null(OcrSelfPartyGuard.VendorContactBlockMessage(OcrVendorContactFallback.CreateNew));
    }

    [Fact]
    public void สายเต็ม_บริษัทในเครือ_ผูกแถวเดิมได้ไม่ถูกบล็อก_สำเนาใบเรา_ถูกบล็อก()
    {
        // บริษัทในเครือ: IsOurContact = false ⇒ เส้นสร้างเอกสารเข้าบล็อกเลือกแถว (ผูกแถวเดิมของเลขนั้น) ⇒ fallback ไม่ถูกเรียกให้สร้าง
        var groupIsUs = OcrSelfPartyGuard.IsOurContact(ParagonGroupTin, ParagonGroup, ParagonDevTin, ParagonDev, null);
        Assert.False(groupIsUs);
        Assert.Equal(OcrVendorContactFallback.Keep,
            OcrSelfPartyGuard.DecideVendorContactFallback(hasContact: true, vendorIsUs: groupIsUs, sameTaxIdRows: 1, hasVendorName: true));
        // สำเนาใบขายของเราเอง: เลขเรา ⇒ เป็นเรา ⇒ ไม่มีผู้ติดต่อผูก ⇒ บล็อก (ไม่ใช่ new Contact ชื่อเรา+เลขเรา)
        var ownIsUs = OcrSelfPartyGuard.IsOurContact(OurTin, OurName, OurTin, OurName, null);
        Assert.True(ownIsUs);
        Assert.Equal(OcrVendorContactFallback.BlockVendorIsUs,
            OcrSelfPartyGuard.DecideVendorContactFallback(hasContact: false, vendorIsUs: ownIsUs, sameTaxIdRows: 0, hasVendorName: true));
    }

    // ═════════ A-2 — เลข placeholder ไม่ใช่ "นิติบุคคลเดียวกัน" ═════════

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

    [Theory]
    [InlineData("0000000000000")]       // ลูกค้าทั่วไปของ POS หลายเจ้า
    [InlineData("0-0000-00000-00-0")]
    [InlineData("000")]
    [InlineData("0107567000415")]       // 13 หลักไม่ผ่าน mod-11
    public void เลขใช้ไม่ได้_เฉพาะตัวเอง_ไม่ขยายข้ามคู่ค้า(string anchor)
    {
        var rows = new[]
        {
            new ContactKeyCandidate(A, anchor, null),
            new ContactKeyCandidate(B, anchor, null),                // ลูกค้าทั่วไปอีกรายที่เก็บเลขเดียวกัน
            new ContactKeyCandidate(C, "0000000000000", "00000"),
        };
        Assert.Equal(new[] { A }, ContactTaxBranchKey.SameEntityIds(A, anchor, rows));
    }

    [Fact]
    public void ทิศตรงข้าม_เลขจริง_ยังขยายทุกสาขา()
    {
        var rows = new[]
        {
            new ContactKeyCandidate(A, CpTin, "00000"),
            new ContactKeyCandidate(B, "0-1075-67000-41-4", "00005"),
            new ContactKeyCandidate(C, "0000000000000", null),
        };
        Assert.Equal(new[] { B, A }, ContactTaxBranchKey.SameEntityIds(B, CpTin, rows));
    }

    // ═════════ A-3 — คะแนนรายช่องย้ายตามค่า ═════════

    private static double? Conf(IDictionary<string, double> c, string key) => c.TryGetValue(key, out var v) ? v : null;

    [Fact]
    public void สาขาที่ย้ายมาจากช่องผู้ซื้อ_ไม่ถือคะแนนของช่องผู้ขายเดิม()
    {
        // ก่อนแก้: ช่องผู้ขายได้สาขา "00005" ที่มาจากบล็อกผู้ซื้อ แต่คะแนน 0.85 (ป้ายผู้ขาย) ยังค้าง ⇒ ผ่านเกณฑ์สร้างผู้ติดต่อสาขาถาวร
        var conf = new Dictionary<string, double> { [OcrFieldKeys.SellerBranchCode] = 0.85 };
        OcrPartyResolver.FollowFieldConfidence(conf,
            vendorBefore: new OcrPartyBlock(OurName, OurTin, null, null),
            buyerBefore: new OcrPartyBlock(CpAxtra, CpTin, null, "00005"),
            vendorAfter: new OcrPartyBlock(CpAxtra, CpTin, null, "00005"),
            buyerAfter: new OcrPartyBlock(OurName, OurTin, null, null));
        Assert.Null(Conf(conf, OcrFieldKeys.SellerBranchCode));                      // ไม่รู้ ≠ เชื่อได้ (K-6)
        Assert.False(OcrVendorBranchContact.IsReliableBranch(Conf(conf, OcrFieldKeys.SellerBranchCode)));
    }

    [Fact]
    public void ApplySide_เราอยู่ช่องผู้ซื้อผิด_คะแนนชื่อเลขสาขาย้ายตามค่า()
    {
        var us = new OcrOurIdentity(OurName, null, OurTin, "00003", "12 ถนนเรา");
        var vendorBefore = new OcrPartyBlock(CpAxtra, CpTin, "99 ถนนพระราม 4", null);
        var buyerBefore = new OcrPartyBlock(OurName, OurTin, "12 ถนนเรา", "00003");
        var conf = new Dictionary<string, double>
        {
            [OcrFieldKeys.SellerName] = 0.90, [OcrFieldKeys.BuyerName] = 0.60,
            [OcrFieldKeys.SellerTaxId] = 0.95, [OcrFieldKeys.BuyerTaxId] = 0.50,
            [OcrFieldKeys.BuyerBranchCode] = 0.85,
        };
        var (v, b) = OcrPartyResolver.ApplySide(vendorBefore, buyerBefore, OcrSelfSide.Seller, us);
        Assert.Equal(OurName, v.Name);                     // เราย้ายไปช่องผู้ขาย
        Assert.Equal("00003", v.BranchCode);
        OcrPartyResolver.FollowFieldConfidence(conf, vendorBefore, buyerBefore, v, b);
        Assert.Equal(0.60, Conf(conf, OcrFieldKeys.SellerName));
        Assert.Equal(0.90, Conf(conf, OcrFieldKeys.BuyerName));
        Assert.Equal(0.50, Conf(conf, OcrFieldKeys.SellerTaxId));
        Assert.Equal(0.95, Conf(conf, OcrFieldKeys.BuyerTaxId));
        Assert.Equal(0.85, Conf(conf, OcrFieldKeys.SellerBranchCode));             // สาขาย้ายมา ⇒ คะแนนของที่มาจริงตามมา
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่สลับ_หรือสาขาเท่ากันสองฝั่ง_คะแนนคงเดิม()
    {
        var conf = new Dictionary<string, double> { [OcrFieldKeys.SellerBranchCode] = 0.85, [OcrFieldKeys.BuyerBranchCode] = 0.70 };
        var v = new OcrPartyBlock(CpAxtra, CpTin, null, "00005");
        var b = new OcrPartyBlock(OurName, OurTin, null, "00000");
        OcrPartyResolver.FollowFieldConfidence(conf, v, b, v, b);
        Assert.Equal(0.85, Conf(conf, OcrFieldKeys.SellerBranchCode));
        Assert.Equal(0.70, Conf(conf, OcrFieldKeys.BuyerBranchCode));

        // "00000" ทั้งสองฝั่งเป็นเรื่องปกติ — ชื่อสลับแต่สาขาเท่ากัน ⇒ ห้ามตีว่าสาขาย้าย
        var conf2 = new Dictionary<string, double> { [OcrFieldKeys.SellerBranchCode] = 0.85 };
        OcrPartyResolver.FollowFieldConfidence(conf2,
            new OcrPartyBlock(OurName, null, null, "00000"), new OcrPartyBlock(CpAxtra, null, null, "00000"),
            new OcrPartyBlock(CpAxtra, null, null, "00000"), new OcrPartyBlock(OurName, null, null, "00000"));
        Assert.Equal(0.85, Conf(conf2, OcrFieldKeys.SellerBranchCode));
    }

    // ═════════ C-2 — หัวพิมพ์ซ้ำตามบทบาทที่ตรึง ═════════

    private static Document WalkInReceipt(DocumentStatus status, bool? frozen) => new()
    {
        DocumentType = DocumentType.Receipt,
        DocumentNumber = "REC-202609-0001",
        Status = status,
        IsTaxInvoiceByLaw = frozen,
        VatAmount = 7m,
        TotalAmount = 107m,
        Contact = new Contact { Name = "ลูกค้าเงินสด", IsWalkInCustomer = true },
    };

    private static string ReprintTitle(Document d, bool companyMayIssueNow)
        => PdfGenerationService.ComputeDocumentTitle(d, new DocumentTemplate(), null, "th",
            AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated(
                DocumentStatusRules.IsIssued(d.Status), d.IsTaxInvoiceByLaw, companyMayIssueNow));

    [Fact]
    public void ใบREC_ที่ตรึงว่าไม่ใช่ใบกำกับ_บริษัทได้สิทธิ์ทีหลัง_พิมพ์ซ้ำยังเป็นใบเสร็จรับเงิน()
    {
        // ก่อนแก้: หัวคำนวณจากธงบริษัทวันนี้ ⇒ "ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ" บนใบเลข REC ที่รายงาน/e-Tax ไม่นับเป็นใบกำกับ
        var d = WalkInReceipt(DocumentStatus.Approved, frozen: false);
        Assert.Equal("ใบเสร็จรับเงิน", ReprintTitle(d, companyMayIssueNow: true));
        Assert.False(AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated(true, false, companyMayIssueNow: true));
    }

    [Fact]
    public void ทิศกลับ_ใบอย่างย่อที่ตรึงแล้ว_เจ้าของเอาธงขายปลีกออก_พิมพ์ซ้ำยังเป็นอย่างย่อ()
    {
        var d = WalkInReceipt(DocumentStatus.Paid, frozen: true);
        Assert.Equal("ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ", ReprintTitle(d, companyMayIssueNow: false));
        // ใบยกเลิกก็เคยออกจริง ⇒ ตามค่าที่ตรึง
        Assert.True(AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated(
            DocumentStatusRules.IsIssued(DocumentStatus.Voided), true, companyMayIssueNow: false));
    }

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Rejected)]
    public void ทิศตรงข้าม_ใบร่าง_ใช้สิทธิ์ปัจจุบันเสมอ(DocumentStatus status)
    {
        // ค่าที่ติดมากับใบร่าง (สร้างจากช่องทางที่ตั้งค่าไว้ล่วงหน้า) ไม่ใช่ "ตรึงตอนออกเลข" ⇒ ไม่ใช้
        Assert.Equal("ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ", ReprintTitle(WalkInReceipt(status, frozen: false), companyMayIssueNow: true));
        Assert.Equal("ใบเสร็จรับเงิน", ReprintTitle(WalkInReceipt(status, frozen: true), companyMayIssueNow: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ทิศตรงข้าม_ใบเก่าที่ไม่เคยตรึง_null_ใช้สิทธิ์ปัจจุบัน(bool companyMayIssueNow)
        => Assert.Equal(companyMayIssueNow,
            AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated(documentIssued: true, frozenTaxInvoiceRole: null, companyMayIssueNow));

    // ═════════ B-2 / B-3 — คำเตือน VAT ตอนอนุมัติ ═════════

    [Fact]
    public void ผู้ใช้แก้VATเป็นเลขบนกระดาษแล้ว_คำเตือนหาย()
    {
        // สแกนอ่าน VAT 777.77 (ไม่มีบนกระดาษ) · ผู้ใช้แก้บรรทัดเป็น 235.06 ตามกระดาษ WinePro
        Assert.Equal(OcrHeaderVatSource.NotOnPaper, OcrHeaderVatEvidence.Classify(OcrPaperSamples.WinePro, null, 777.77m, null));
        var src = OcrHeaderVatEvidence.ClassifyPosted(OcrPaperSamples.WinePro, null, scanVat: 777.77m, postedVat: 235.06m, ocrEngine: null);
        Assert.Equal(OcrHeaderVatSource.Labelled, src);
        Assert.Null(OcrApprovalGapWarning.VatDerivedWarning(src, 777.77m, 235.06m));
    }

    [Fact]
    public void ทิศตรงข้าม_ยังไม่แก้หรือแก้เป็นเลขที่ไม่มีบนกระดาษ_ยังเตือน()
    {
        Assert.Equal(OcrHeaderVatSource.NotOnPaper,
            OcrHeaderVatEvidence.ClassifyPosted(OcrPaperSamples.WinePro, null, 777.77m, postedVat: 777.77m, ocrEngine: null));
        Assert.Equal(OcrHeaderVatSource.NotOnPaper,
            OcrHeaderVatEvidence.ClassifyPosted(OcrPaperSamples.WinePro, null, 777.77m, postedVat: 888.88m, ocrEngine: null));
        // ไม่รู้ VAT ของบรรทัด ⇒ ตัดสินจากสแกนตามเดิม
        Assert.Equal(OcrHeaderVatSource.NotOnPaper,
            OcrHeaderVatEvidence.ClassifyPosted(OcrPaperSamples.WinePro, null, 777.77m, postedVat: null, ocrEngine: null));
        var w = OcrApprovalGapWarning.VatDerivedWarning(OcrHeaderVatSource.NotOnPaper, 777.77m, 888.88m);
        Assert.NotNull(w);
        Assert.StartsWith(OcrApprovalGapWarning.VatDerivedPrefix, w);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void ไม่มีข้อความสแกน_ไม่รู้_ยังเตือนแต่ไม่อ้างว่าไม่ได้พิมพ์(string? raw)
    {
        var src = OcrHeaderVatEvidence.Classify(raw, null, 70.00m, null);
        Assert.Equal(OcrHeaderVatSource.NoTextToCheck, src);
        // ไม่รู้ ≠ ผ่าน: ยังติดแท็กหยุดอนุมัติเอง + เตือนตอนอนุมัติ (ต้องกดรับทราบ · API ธงเดียวกัน)
        var note = OcrHeaderVatEvidence.DerivedNote(src, 70.00m);
        Assert.NotNull(note);
        Assert.Contains("ไม่มีข้อความสแกน", note);
        Assert.DoesNotContain("ไม่ได้พิมพ์อยู่บนกระดาษ", note);
        var w = OcrApprovalGapWarning.VatDerivedWarning(src, 70.00m, 70.00m)!;
        Assert.StartsWith(OcrApprovalGapWarning.VatUncheckedPrefix, w);
        Assert.True(OcrApprovalGapWarning.IsVatDerivedWarning(w));
        Assert.True(OcrApprovalGapWarning.IsGapWarning(w));
        Assert.Equal(new[] { w }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.SystemWorkflow, new[] { w }));
    }

    [Fact]
    public void ไม่มีข้อความแต่มีร่องรอยถอด7_107_ยังเป็นNotOnPaper()
    {
        var notes = "[Reasoning]\n" + VatBackCalcGuard.BackCalcTag + " แยก VAT จากยอดรวม";
        Assert.Equal(OcrHeaderVatSource.NotOnPaper, OcrHeaderVatEvidence.Classify(null, null, 70.00m, null, notes, 1070.00m));
        // e-Tax XML ที่ลงนาม และไม่มี VAT ไม่ถูกแตะ
        Assert.Equal(OcrHeaderVatSource.Labelled, OcrHeaderVatEvidence.Classify(null, null, 70.00m, OcrHeaderVatEvidence.SignedXmlEngine));
        Assert.Equal(OcrHeaderVatSource.NoVat, OcrHeaderVatEvidence.Classify(null, null, 0m, null));
    }

    // ═════════ B-4 — "แทน" คนละความหมายในบรรทัดถัดไป ═════════

    [Theory]
    [InlineData("ยกเลิกใบกำกับภาษีอย่างย่อได้ภายใน 7 วัน และคืนสินค้า\nผู้แทนขายติดต่อ 02-000-0000")]
    [InlineData("ยกเลิกใบกำกับภาษีอย่างย่อได้ภายใน 7 วัน และ\nใช้แทนใบเสร็จรับเงินได้")]
    public void แทนคนละความหมายบรรทัดถัดไป_ใบอย่างย่อจริงยังถูกจับ(string text)
        => Assert.True(OcrDocumentRoleInferrer.ContainsAnyNotNegated(text, "ใบกำกับภาษีอย่างย่อ"));

    [Fact]
    public void ทิศตรงข้าม_หมายเหตุScommerceตัดบรรทัดหลังคำเชื่อม_ยังเป็นคำปฏิเสธ()
        => Assert.False(OcrDocumentRoleInferrer.ContainsAnyNotNegated(
            "หมายเหตุ :เป็นการยกเลิกใบกำกับภาษีอย่างย่อเลขที่ d20260918000097 วันที่ 18/09/2026 และออกใบกำกับภาษี\nอิเล็กทรอนิกส์ฉบับใหม่แทน",
            "ใบกำกับภาษีอย่างย่อ"));
}
