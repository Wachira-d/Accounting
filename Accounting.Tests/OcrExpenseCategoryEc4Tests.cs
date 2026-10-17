using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests;

/// <summary>
/// **ฝ่ายค้านของ EC3 (bf0e5f4b) — ทีม EC4** · probe ของผู้ตรวจ (ZzReviewProbeTests) เป็นเทสต์ที่นี่ สองทิศ: ใบที่พังกลับมาถูก + ใบที่ถูกอยู่แล้วไม่ถูกแตะ
/// <list type="bullet">
/// <item>P1-1 วลี "ฟรี/รวม" มีขอบเขต — "รวมค่าแรง" = จ่ายค่าแรง · "ค่าบริการ… ฟรี 1 ปี 8,000" = จ่ายค่าบริการ</item>
/// <item>P1-2 คำงานบริการ (ค่าเดิน… · งาน… · ปู · ทาสี) ชนะคำนามสินค้าบนบรรทัดเดียวกัน — ชื่อร้าน "รับเหมา" ผูกกับเงินอีกครั้ง</item>
/// <item>P1-3 เลขผู้เสียภาษีที่ OCR อ่านเพี้ยน = ไม่รู้ (ในประเทศ) · ชื่อนิติบุคคลต่างประเทศ = ต่างประเทศ (<see cref="ThaiVatTypeRule.ForeignVendorReason"/>)</item>
/// <item>P2-4 ประกันสินค้าระหว่างขนส่ง = ประกันภัย · วลีมัดจำไม่กลืนคำข้างเคียง</item>
/// <item>P2-5 ฐานเกณฑ์ 1,000 ตัวเดียว (<see cref="OcrWhtThresholdBase"/>) · งวดสัญญาต่ำกว่าเกณฑ์บอกกฎหักทุกงวด</item>
/// <item>P2-6 "ค่าบริการขนส่งรวมค่าน้ำมัน" = 8tr 1%</item>
/// <item>P2-7 ผังของทุกกฎมีอยู่จริงในแม่แบบ · ผังสต็อกเฉพาะเมื่อของบนใบเป็นสินค้า master ทุกบรรทัด</item>
/// </list>
/// </summary>
public class OcrExpenseCategoryEc4Tests
{
    private readonly ITestOutputHelper _out;
    public OcrExpenseCategoryEc4Tests(ITestOutputHelper output) => _out = output;

    private const string NeutralVendor = "บริษัท ตัวอย่างดี จำกัด";

    /// <summary>รูปเดียวกับ probe ของผู้ตรวจ (ScanAsync: ยอดรวม · VAT · เลขผู้เสียภาษี · ชนิดกระดาษ ก่อนเรียก ApplyTo)</summary>
    private OcrExtractedData Run(string? vendor, IEnumerable<(string Desc, decimal Amount)> lines, string? taxId = null,
        decimal? vat = null, decimal? totalOverride = null, decimal? subTotal = null, string docType = "Receipt")
    {
        var items = lines.ToList();
        var total = totalOverride ?? items.Sum(i => i.Amount) + (vat ?? 0m);
        var raw = $"{vendor}\nใบเสร็จรับเงิน\nเลขที่ RC-001\n" + string.Join("\n", items.Select(i => $"{i.Desc} {i.Amount:N2}"))
            + $"\nรวมทั้งสิ้น {total:N2}\n";
        var data = new OcrExtractedData
        {
            VendorName = vendor, VendorTaxId = taxId, TotalAmount = total, VatAmount = vat, SubTotal = subTotal, DocumentType = docType,
        };
        foreach (var (d, a) in items) data.Items.Add(new OcrExtractedLineItem { Description = d, Quantity = 1, UnitPrice = a, Amount = a });
        var text = ThaiTextNormalizer.Normalize(raw);
        var paid = data.Items.Where(i => (i.Amount ?? 0m) > 0m).Select(i => i.Description).ToList();
        var priced = data.Items.Select(i => new OcrPricedLine(i.Description, i.Amount ?? 0m)).Where(l => l.Amount > 0m).ToList();
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: vendor, headerDescription: null, lineDescriptions: paid, rawText: text,
            zeroAmountLineDescriptions: new List<string?>(), pricedLines: priced, diagnostics: new List<string>(),
            minorityWhtParts: new List<OcrMinorityWhtPart>());
        if (result != null) ExpenseCategoryResolver.ApplyTo(data, result, text);
        _out.WriteLine($"| {vendor} | {taxId} | {string.Join(" + ", items.Select(i => $"{i.Desc} {i.Amount:N0}"))} | "
            + $"{data.ExpenseCategory ?? "-"} | {data.DebitAccountCode ?? "-"} | conf {result?.Confidence.ToString("0.00") ?? "-"} | "
            + $"{data.SuggestedWhtRate?.ToString("0.##") ?? "-"} | {data.WhtIncomeTypeCode ?? "-"} |");
        foreach (var t in data.ReasoningTrace) _out.WriteLine("    " + t);
        return data;
    }

    private OcrExtractedData One(string line, decimal amount, string? vendor = NeutralVendor, string? taxId = null)
        => Run(vendor, new[] { (line, amount) }, taxId);

    private static List<(string, decimal)> Spec(string spec)
        => spec.Split(';').Select(x => x.Split('|')).Select(p => (p[0], decimal.Parse(p[1]))).ToList();

    private static void NoSuggestion(OcrExtractedData d)
    {
        Assert.Null(d.SuggestedWhtRate);
        Assert.Null(d.WhtIncomeTypeCode);
        Assert.False(d.HasWht);
        Assert.DoesNotContain(d.ReasoningTrace, t => t.Contains("กฎหมายกำหนดให้ผู้จ่ายหัก"));
    }

    private static void Suggests(OcrExtractedData d, string incomeCode)
    {
        Assert.Equal(incomeCode, d.WhtIncomeTypeCode);
        Assert.Equal(ThaiWhtRateTable.RateFor(incomeCode, payeeIsJuristic: true), d.SuggestedWhtRate);
    }

    // ── P1-1: วลี "ฟรี/รวม" มีขอบเขต ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ค่าวัสดุรวมค่าแรง", 20000)]                  // ก่อน: ไม่มีหมวด (วลี "รวมค่า…" กลืน "ค่าแรง") · ก่อน EC3: 3% 40(8)
    [InlineData("รวมค่าแรงติดตั้งระบบไฟฟ้า", 15000)]          // ก่อน: ไม่มีหมวด
    [InlineData("งานติดตั้งรวมค่าแรง", 20000)]                 // ก่อน: หมวดซ่อม ไม่มีอัตรา (เหลือคำสั้น "ติดตั้ง")
    [InlineData("ค่าบริการติดตั้งระบบ ฟรี 1 ปี", 8000)]        // ก่อน: ไม่มีหมวด ("[ไทย]+\s*ฟรี" กลืน "ค่าบริการติดตั้งระบบ ")
    [InlineData("ค่าบริการติดตั้งฟรี 1 ปี", 8000)]             // "ติดตั้งฟรี" ถูกตัด แต่ "ค่าบริการ" ยังอยู่ — บรรทัดมีเงิน = จ่ายค่าบริการ
    public void P1_1_รวมค่าแรงและค่าบริการที่มีเงิน_คือจ่ายค่าแรงจริง_ได้3เปอร์เซ็นต์(string line, int amount)
        => Suggests(One(line, amount), "8");

    [Theory]
    [InlineData("แอร์ 12000BTU บริการล้างแอร์ฟรี 2 ปี", 15000)]   // รับประกันของสินค้า ไม่มีวงเล็บ
    [InlineData("แอร์ 12000BTU (บริการซ่อมฟรี 1 ปี)", 15000)]
    [InlineData("ตู้เย็น 2 ประตู ติดตั้งฟรี", 12900)]
    [InlineData("ทีวี 55 นิ้ว (รวมค่าขนส่ง)", 15900)]
    [InlineData("ปูนซีเมนต์ ฟรีค่าขนส่ง", 4500)]
    public void P1_1_ทิศตรงข้าม_ของแถมบนใบสินค้ายังไม่ใช่หลักฐานค่าบริการ(string line, int amount)
        => NoSuggestion(One(line, amount));

    [Theory]
    [InlineData("ค่าจ้างฟรีแลนซ์ออกแบบโลโก้", NeutralVendor)]
    [InlineData("ค่าจ้างออกแบบโลโก้ (ฟรีแลนซ์)", NeutralVendor)]
    [InlineData("ค่าบริการ เดือน ต.ค.", "ฟรีเวย์ขนส่ง")]
    [InlineData("ค่าซ่อมเครื่องปรับอากาศ ฟรีค่าเดินทาง", NeutralVendor)]
    [InlineData("ค่าซ่อมรวมอะไหล่", NeutralVendor)]
    [InlineData("ค่าซ่อมรถยนต์รวมค่าอะไหล่", NeutralVendor)]
    public void P1_1_ตรวจแล้วถูก_ฟรีแลนซ์_ชื่อร้านมีฟรี_ฟรีค่าเดินทาง_ซ่อมรวมอะไหล่ยัง3เปอร์เซ็นต์(string line, string vendor)
        => Suggests(One(line, 5000, vendor), "8");

    [Fact]
    public void P1_1_ตรวจแล้วถูก_บริษัทชื่อฟรีเวย์ค่าขนส่งยัง1เปอร์เซ็นต์()
        => Suggests(One("ค่าขนส่งสินค้า", 3000, "บริษัท ฟรีเวย์ทรานสปอร์ต จำกัด"), "8tr");

    // ── P1-2: คำงานบริการชนะคำนามสินค้า ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("หจก. รับเหมาไฟฟ้า", "ค่าเดินสายไฟใหม่ทั้งหลัง|25000", "7")]       // ก่อน: ค่ารับเหมา ไม่มีอัตรา ("สายไฟ" = สินค้า)
    [InlineData("ช่างเอก รับเหมาก่อสร้าง", "งานปูกระเบื้องห้องน้ำ|18000", "7")]     // ก่อน: ค่ารับเหมา ไม่มีอัตรา ("กระเบื้อง")
    [InlineData("ช่างเอก รับเหมาก่อสร้าง", "ค่าแรงปูกระเบื้อง|18000", "8")]
    [InlineData("ช่างเอก รับเหมาก่อสร้าง", "งานทาสีอาคาร|30000", "7")]
    [InlineData("ช่างเอก รับเหมาก่อสร้าง", "ค่ารื้อถอนผนังเดิม + ปูน|9000", "7")]
    public void P1_2_งานรับเหมาที่มีคำสินค้าในวลีงาน_ได้อัตราตามประเภทเงินได้(string vendor, string spec, string incomeCode)
    {
        var d = Run(vendor, Spec(spec));
        Suggests(d, incomeCode);
        Assert.DoesNotContain(d.ReasoningTrace, t => t.Contains("ทุกบรรทัดที่มีเงินเป็นสินค้า"));   // trace ไม่อ้างสาเหตุที่ไม่จริง (F2 ข้อ 7)
    }

    [Theory]
    [InlineData("ร้านรับเหมาก่อสร้างและวัสดุ", "ปูนซีเมนต์ 50 กก.|9000", "ปูน")]
    [InlineData("ร้านรับเหมาก่อสร้างและวัสดุ", "กระเบื้องปูพื้น 60x60|1500", "กระเบื้อง")]       // "ปู" ไม่อยู่ต้นคำ = ชื่อสินค้า
    [InlineData("ร้านรับเหมาก่อสร้างและวัสดุ", "สีรองพื้น สำหรับทาสีภายนอก|2700", "สีรองพื้น")]      // "ทาสี" ไม่อยู่ต้นคำ = คำบรรยายสินค้า
    [InlineData("หจก. รับเหมาไฟฟ้า", "สายไฟ VAF 2x2.5 100 เมตร|3000", "สายไฟ")]
    [InlineData("ศูนย์ซ่อมและอะไหล่รถยนต์", "ผ้าเบรคหน้า|2400", "ผ้าเบรค")]
    public void P1_2_ทิศตรงข้าม_ใบสินค้าล้วนจากร้านรับเหมายังไม่เสนอ_และtraceบอกคำสินค้าที่เจอ(string vendor, string spec, string noun)
    {
        var d = Run(vendor, Spec(spec));
        NoSuggestion(d);
        Assert.Contains(d.ReasoningTrace, t => t.Contains("ทุกบรรทัดที่มีเงินเป็นสินค้า") && t.Contains("= " + noun));
    }

    // ── P1-3: ผู้ขายต่างประเทศ ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("010556123456")]            // OCR อ่านหลักหายไปหนึ่งหลัก — ก่อน: "ต่างประเทศ" ไม่มีอัตรา
    [InlineData("0105561234567 00000")]     // เลขสาขาติดมา — ก่อน: ผลเดียวกัน
    [InlineData("0105561234567")]
    [InlineData(null)]
    public void P1_3_ผู้ขายไทยที่เลขอ่านเพี้ยน_ยังได้อัตราในประเทศ(string? taxId)
    {
        var d = One("ค่าโฆษณา", 5000, "บริษัท เอเจนซี่ไทย จำกัด", taxId);
        Suggests(d, "8ad");
        Assert.DoesNotContain(d.ReasoningTrace, t => t.Contains("ต่างประเทศ"));
    }

    [Theory]
    [InlineData("Meta Platforms Ireland Limited", "0993000454995", "Facebook ads")]   // ก่อน: 2% 8ad มั่นใจ 0.95
    [InlineData("Google Asia Pacific Pte. Ltd.", "0993000453000", "ค่าโฆษณา")]
    [InlineData("Google Asia Pacific Pte. Ltd.", null, "ค่าโฆษณา")]
    [InlineData("Amazon Web Services, Inc.", null, "ค่าบริการ cloud service fee")]
    public void P1_3_ชื่อนิติบุคคลต่างประเทศ_ไม่ได้อัตราในประเทศแม้มีเลข13หลัก(string vendor, string? taxId, string line)
    {
        var d = One(line, 5000, vendor, taxId);
        NoSuggestion(d);
        Assert.NotNull(d.ExpenseCategory);
        Assert.Contains(d.ReasoningTrace, t => t.Contains("ผู้ขายต่างประเทศ") && t.Contains("ม.70") && t.Contains("ภ.พ.36"));
    }

    [Theory]
    [InlineData(null, "010556123456", null, false)]
    [InlineData(null, "0105561234567 00000", null, false)]
    [InlineData(null, "0-1055-58000-12-3", null, false)]
    [InlineData(null, "OI05561234567", null, false)]                        // ตัวอักษรที่เป็นตัวเลขอ่านเพี้ยนล้วน ไม่ใช่รหัสประเทศ
    [InlineData(null, "US-99-1234567", null, true)]
    [InlineData(null, "IE9692928F", null, true)]
    [InlineData("SG", null, null, true)]
    [InlineData("TH", "0105558000123", null, false)]
    [InlineData(null, null, "Meta Platforms Ireland Limited", true)]
    [InlineData(null, null, "Acme Data Singapore Ltd.", true)]
    [InlineData(null, null, "Example Software LLC", true)]
    [InlineData(null, null, "Siam Japan Tools Co., Ltd.", false)]          // บริษัทไทยชื่อมีประเทศ แต่ไม่ใช่ "<ประเทศ> Limited"
    [InlineData(null, null, "Thai Agency Co., Ltd.", false)]
    [InlineData(null, null, "บริษัท ไอร์แลนด์ จำกัด", false)]
    public void P1_3_ตัวตัดสินผู้ขายต่างประเทศ_ไม่นับหลัก(string? country, string? taxId, string? name, bool expected)
        => Assert.Equal(expected, ThaiVatTypeRule.LooksForeignVendor(country, taxId, name));

    [Fact]
    public void P1_3_เลขอ่านเพี้ยนบนเส้นอัตรา_VAT_รายบรรทัด_ไม่กลายเป็น0เปอร์เซ็นต์()
    {
        // เส้นสร้างบรรทัด (BuildScanLinesAsync) เรียก Suggest(desc, null, ExtractedVendorTaxId) — เดิมเลข 12 หลัก ⇒ "0" ทั้งที่หัวใบมี VAT
        Assert.Equal("7", ThaiVatTypeRule.Suggest("ค่าโฆษณา", null, "010556123456"));
        Assert.Equal("0", ThaiVatTypeRule.Suggest("Software licence", null, "US-99-1234567"));   // รูปแบบต่างประเทศจริงคงเดิม
    }

    // ── P2-4: ประกันระหว่างขนส่ง · วลีมัดจำมีขอบเขต ───────────────────────────────────────────────────

    [Theory]
    [InlineData("ค่าประกันความเสียหายระหว่างขนส่ง")]     // ก่อน: ไม่มีหมวด (ถูกนับเป็นมัดจำ)
    [InlineData("ค่าประกันสินค้าเสียหายระหว่างขนส่ง")]
    [InlineData("ค่าเบี้ยประกันความเสียหายทรัพย์สิน")]
    public void P2_4_ประกันสินค้าระหว่างขนส่ง_คือประกันภัย(string line)
    {
        var d = One(line, 2500);
        Assert.Equal("ค่าประกัน", d.ExpenseCategory);
        Assert.Equal("54510", d.DebitAccountCode);
    }

    [Fact]
    public void P2_4_วลีมัดจำไม่กลืนค่าเช่าที่อยู่ติดกัน()
    {
        // ก่อน: "[ไทย]*" กลืน "ค่าประกันห้องและค่าเช่าห้อง" ทั้งก้อน ⇒ ไม่มีหมวด
        var d = One("ค่าประกันห้องและค่าเช่าห้อง เดือน ต.ค.", 15000);
        Assert.Equal("ค่าเช่า", d.ExpenseCategory);
        Suggests(d, "5");
    }

    [Theory]
    [InlineData("ค่าประกันห้อง")]
    [InlineData("ค่าประกันความเสียหาย")]
    [InlineData("ค่าประกันมิเตอร์น้ำประปา")]
    [InlineData("เงินประกันการเช่า")]
    public void P2_4_ทิศตรงข้าม_มัดจำล้วนยังไม่มีหมวด(string line)
    {
        var d = One(line, 10000);
        Assert.Null(d.ExpenseCategory);
        Assert.NotEqual("54510", d.DebitAccountCode);
    }

    // ── P2-5: ฐานเกณฑ์ 1,000 บาทตัวเดียว ──────────────────────────────────────────────────────────────

    [Theory]
    // subTotal, vat, total, lineSum, docType ⇒ amount, source, verdict
    [InlineData(null, 66.50, 1016.50, null, "TaxInvoice", 950.00, OcrWhtBaseSource.TotalLessVat, OcrWhtThresholdVerdict.Below)]
    [InlineData(950.00, null, 1016.50, null, "TaxInvoice", 950.00, OcrWhtBaseSource.SubTotal, OcrWhtThresholdVerdict.Below)]
    [InlineData(null, null, 1016.50, null, "TaxInvoice", 1016.50, OcrWhtBaseSource.TotalVatInclusive, OcrWhtThresholdVerdict.Unsure)]
    [InlineData(null, null, 1016.50, null, "Receipt", 1016.50, OcrWhtBaseSource.TotalNoVat, OcrWhtThresholdVerdict.Meets)]
    [InlineData(null, null, 1100.00, null, "TaxInvoice", 1100.00, OcrWhtBaseSource.TotalVatInclusive, OcrWhtThresholdVerdict.Meets)]
    [InlineData(null, null, 900.00, null, "TaxInvoice", 900.00, OcrWhtBaseSource.TotalVatInclusive, OcrWhtThresholdVerdict.Below)]
    [InlineData(1000.00, null, 900.00, null, "Receipt", 900.00, OcrWhtBaseSource.TotalNoVat, OcrWhtThresholdVerdict.Below)]   // ส่วนลดหลัง SubTotal
    [InlineData(1100.00, 63.00, 963.00, null, "TaxInvoice", 900.00, OcrWhtBaseSource.TotalLessVat, OcrWhtThresholdVerdict.Below)] // SubTotal ก่อนลด ไม่ใช่ฐาน
    [InlineData(null, 70.00, 1070.00, null, "TaxInvoice", 1000.00, OcrWhtBaseSource.TotalLessVat, OcrWhtThresholdVerdict.Meets)]  // พอดีเกณฑ์
    [InlineData(null, null, null, 500.00, "Receipt", 500.00, OcrWhtBaseSource.LineSum, OcrWhtThresholdVerdict.Below)]
    [InlineData(null, null, null, null, "Receipt", null, OcrWhtBaseSource.None, OcrWhtThresholdVerdict.Unknown)]
    public void P2_5_ตัวตั้งฐานเกณฑ์(double? sub, double? vat, double? total, double? lines, string docType,
        double? amount, OcrWhtBaseSource source, OcrWhtThresholdVerdict verdict)
    {
        var r = OcrWhtThresholdBase.FromScan((decimal?)sub, (decimal?)vat, (decimal?)total, (decimal?)lines, docType);
        Assert.Equal((decimal?)amount, r.Amount);
        Assert.Equal(source, r.Source);
        Assert.Equal(verdict, r.Verdict);
    }

    [Fact]
    public void P2_5_ขอบล่างของใบกำกับภาษีที่อ่าน_VAT_ไม่ได้คือยอดก่อนVATเจ็ดเปอร์เซ็นต์()
        => Assert.Equal(950.00m, OcrWhtThresholdBase.FromScan(null, null, 1016.50m, null, "TaxInvoice").ExVatLowerBound);

    [Fact]
    public void P2_5_ใบกำกับภาษีอ่าน_VAT_ไม่ได้_ยอดคร่อมเกณฑ์_ไม่เสนอและบอกว่าตัดสินไม่ได้()
    {
        // ก่อน: 950 ก่อน VAT ที่อ่าน VAT ไม่ได้ ⇒ ฐาน 1,016.50 ⇒ ข้อเสนอ 3%
        var d = Run(NeutralVendor, new[] { ("ค่าบริการติดตั้ง", 950m) }, totalOverride: 1016.50m, docType: "TaxInvoice");
        NoSuggestion(d);
        Assert.Equal("ค่าบริการ", d.ExpenseCategory);
        Assert.Contains(d.ReasoningTrace, t => t.Contains("ตัดสินแทนไม่ได้") && t.Contains("950.00"));
        // อ่าน SubTotal ได้แต่ VAT ไม่ได้ ⇒ ฐาน 950 ⇒ ต่ำกว่าเกณฑ์
        var withSub = Run(NeutralVendor, new[] { ("ค่าบริการติดตั้ง", 950m) }, totalOverride: 1016.50m, subTotal: 950m, docType: "TaxInvoice");
        NoSuggestion(withSub);
        Assert.Contains(withSub.ReasoningTrace, t => t.Contains("ต่ำกว่าเกณฑ์") && t.Contains("ยอดก่อน VAT ที่พิมพ์"));
    }

    [Fact]
    public void P2_5_ทิศตรงข้าม_ใบเสร็จผู้ไม่จดVATยอด1016ยังเสนอ_และใบกำกับภาษีที่อ่านVATได้ถึงเกณฑ์ยังเสนอ()
    {
        Suggests(Run(NeutralVendor, new[] { ("ค่าบริการติดตั้ง", 1016.50m) }), "8");
        Suggests(Run(NeutralVendor, new[] { ("ค่าบริการติดตั้ง", 1000m) }, vat: 70m, docType: "TaxInvoice"), "8");
        Suggests(Run(NeutralVendor, new[] { ("ค่าบริการติดตั้ง", 600m), ("ค่าบริการติดตั้ง จุดที่ 2", 600m) }), "8");
    }

    [Theory]
    [InlineData("ค่าบริการรักษาความปลอดภัย ประจำเดือน ต.ค.", 900, "ค่าบริการ", "ประจำเดือน")]
    [InlineData("ค่าเช่าสำนักงาน ประจำเดือน ต.ค.", 800, "ค่าเช่า", "ประจำเดือน")]
    [InlineData("ค่าบริการดูแลระบบ งวดที่ 3", 700, "ค่าบริการ", "งวดที่")]
    public void P2_5_งวดสัญญาต่ำกว่าเกณฑ์_คงหมวด_ไม่ให้อัตรา_และบอกกฎหักทุกงวด(string line, int amount, string category, string phrase)
    {
        var d = One(line, amount);
        NoSuggestion(d);
        Assert.Equal(category, d.ExpenseCategory);
        Assert.Contains(d.ReasoningTrace, t => t.Contains($"'{phrase}'") && t.Contains("หัก ณ ที่จ่ายทุกงวด"));
    }

    [Theory]
    [InlineData("ค่าซ่อมรองเท้า", 80)]
    [InlineData("ค่าพิมพ์นามบัตร", 800)]
    public void P2_5_ทิศตรงข้าม_ใบต่ำกว่าเกณฑ์ที่ไม่ใช่งวดสัญญา_ไม่มีคำเตือนหักทุกงวด(string line, int amount)
    {
        var d = One(line, amount);
        NoSuggestion(d);
        Assert.DoesNotContain(d.ReasoningTrace, t => t.Contains("หัก ณ ที่จ่ายทุกงวด"));
    }

    [Fact]
    public void P2_5_ตัวอ่านกระดาษ_ใช้ฐานตัวเดียวกัน()
    {
        // ParseThaiDocument: กระดาษพิมพ์อัตราหัก 3% — ฐาน 950 (ยอดรวม − VAT) ต่ำกว่าเกณฑ์ · ฐาน 1,000 ⇒ ตั้ง
        // ฝ่ายค้านรอบ 5 P2-e (ทีม EC5): อัตราที่พิมพ์ชนะการคำนวณเกณฑ์ (เกณฑ์วัดทั้งสัญญา) ⇒ ตั้งตามกระดาษ + เตือนว่าฐานใบนี้ต่ำกว่าเกณฑ์ (เดิม HasWht=false — อัตราที่พิมพ์หาย)
        var below = OcrService.ParseThaiDocument("ใบกำกับภาษี\nเลขที่ INV-1\nค่าบริการติดตั้ง 950.00\nภาษีมูลค่าเพิ่ม 66.50\nรวมทั้งสิ้น 1,016.50\nหัก ณ ที่จ่าย 3%\n");
        _out.WriteLine($"below: sub={below.SubTotal} vat={below.VatAmount} total={below.TotalAmount} hasWht={below.HasWht} rate={below.WhtRate}");
        Assert.True(below.HasWht);
        Assert.Equal(3m, below.WhtRate);
        Assert.Contains(below.ReasoningTrace, t => t.Contains("[WHT]") && t.Contains("ต่ำกว่าเกณฑ์"));
        var meets = OcrService.ParseThaiDocument("ใบกำกับภาษี\nเลขที่ INV-2\nค่าบริการติดตั้ง 1,000.00\nภาษีมูลค่าเพิ่ม 70.00\nรวมทั้งสิ้น 1,070.00\nหัก ณ ที่จ่าย 3%\n");
        _out.WriteLine($"meets: sub={meets.SubTotal} vat={meets.VatAmount} total={meets.TotalAmount} hasWht={meets.HasWht} rate={meets.WhtRate}");
        Assert.True(meets.HasWht);
        Assert.Equal(3m, meets.WhtRate);
    }

    // ── P2-6: ค่าบริการขนส่งรวมค่าน้ำมัน ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ค่าบริการขนส่งรวมค่าน้ำมัน")]       // ก่อน: 3% 40(8)
    [InlineData("ค่าบริการขนส่ง รวมค่าน้ำมัน")]
    [InlineData("ค่าบริการจัดส่งสินค้า")]
    public void P2_6_ค่าบริการขนส่ง_ได้ค่าขนส่ง1เปอร์เซ็นต์(string line)
    {
        var d = One(line, 5000);
        Assert.Equal("ค่าขนส่ง / ค่าจัดส่ง", d.ExpenseCategory);
        Suggests(d, "8tr");
    }

    [Fact]
    public void P2_6_ทิศตรงข้าม_ใบน้ำมันจริงยังเป็นค่าน้ำมัน_และค่าบริการทั่วไปยัง40_8()
    {
        var fuel = One("น้ำมันดีเซล 45 ลิตร", 1500, "บริษัท ปตท. จำกัด (มหาชน)");
        Assert.Equal("ค่าน้ำมันเชื้อเพลิง", fuel.ExpenseCategory);
        NoSuggestion(fuel);
        var fuelTotal = One("รวมค่าน้ำมัน", 1500, "สถานีบริการน้ำมัน บางจาก");   // "รวมค่าน้ำมัน" ไม่ต่อท้ายค่าขนส่ง ⇒ ไม่ถูกตัด
        Assert.Equal("ค่าน้ำมันเชื้อเพลิง", fuelTotal.ExpenseCategory);
        Suggests(One("ค่าบริการรายเดือน", 5000), "8");
    }

    // ── P2-7: ผังของทุกกฎ · ผังสต็อก ──────────────────────────────────────────────────────────────────

    [Fact]
    public void P2_7_ผังของทุกกฎมีอยู่จริงในแม่แบบผังมาตรฐาน_ลงรายการได้_ชื่อตรง()
    {
        var tpl = ChartOfAccountTemplates.GetCommonAccounts();
        foreach (var (category, code, name) in ExpenseCategoryResolver.RuleAccounts)
        {
            var row = tpl.SingleOrDefault(t => t.Code == code);
            Assert.True(row != null, $"กฎ '{category}': รหัส {code} ไม่มีในผังมาตรฐาน");
            var isControl = tpl.Any(o => o.Code != code && o.Code.StartsWith(code, StringComparison.Ordinal));
            Assert.False(isControl, $"กฎ '{category}': {code} เป็นบัญชีคุม");
            Assert.Equal(AccountType.Expense, row!.Type);
            Assert.Equal(name, row.NameTh);
        }
    }

    [Theory]
    [InlineData(true, 2, 2, true)]
    [InlineData(true, 2, 1, false)]    // จับคู่ไม่ครบ ⇒ ไม่รู้ ⇒ ค่าใช้จ่ายทั่วไป
    [InlineData(true, 0, 0, false)]    // ใบไม่มีบรรทัด
    [InlineData(false, 2, 2, false)]   // บริษัทไม่ถือสต็อก
    public void P2_7_ผังสต็อกเฉพาะเมื่อของบนใบเป็นสินค้า_master_ทุกบรรทัด(bool keeps, int lines, int matched, bool expected)
    {
        Assert.Equal(expected, OcrDocTypeDefaultAccounts.PurchaseIsStock(keeps, lines, matched));
        var isStock = OcrDocTypeDefaultAccounts.PurchaseIsStock(keeps, lines, matched);
        foreach (var type in new[] { "TaxInvoice", "DebitNote", "Invoice" })
            Assert.Equal(expected ? "51110" : OcrDocTypeDefaultAccounts.GenericExpenseDebit.Code,
                OcrDocTypeDefaultAccounts.For(type, isStock)!.Value.DebitCode);
        Assert.Equal(expected ? "11500" : OcrDocTypeDefaultAccounts.GenericExpenseDebit.Code,
            OcrDocTypeDefaultAccounts.For("PurchaseOrder", isStock)!.Value.DebitCode);
    }

    [Fact]
    public void P2_7_ตรวจแล้วถูก_รหัสตั้งต้น54910_51110_11500อยู่ในผังมาตรฐาน()
    {
        var codes = ChartOfAccountTemplates.GetCommonAccounts().Select(a => a.Code).ToHashSet();
        foreach (var c in new[] { "54910", "51110", "11500" }) Assert.Contains(c, codes);
    }
}
