using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests;

/// <summary>
/// **ฝ่ายค้านของ EC2 (7fd7a0a5) — ทีม EC3** · ทุก probe ในผลฝ่ายค้านเป็นเทสต์ที่นี่ (สองทิศ: ใบที่พังกลับมาถูก + ใบที่ถูกอยู่แล้วไม่ถูกแตะ)
/// <list type="bullet">
/// <item>P1-1 คำซ้อนกัน ("ค่าขนส่ง" + "ขนส่ง") นับครั้งเดียว · วลีของแถม/รวมในราคา (ฟรี… · รวมค่า… · …ฟรี) ไม่ใช่หลักฐานค่าบริการ</item>
/// <item>P1-2 ยอดต่ำกว่าเกณฑ์ 1,000 บาท ไม่เสนออัตรา (ตัวตัดสิน <see cref="ThaiWhtRateTable.ShouldWithhold"/>) — หมวดคงอยู่</item>
/// <item>P1-3 "ค่าประกัน&lt;ห้อง/มิเตอร์/ความเสียหาย…&gt;" = เงินมัดจำ ไม่ใช่ 5800 · ประกันภัยจริงยังได้ 5800</item>
/// <item>P1-4 ชื่อผู้ขายมี ซ่อม/รับเหมา แต่ทุกบรรทัดเป็นสินค้า ⇒ ไม่เสนอหัก</item>
/// <item>P1-5 ผู้ขายต่างประเทศ ⇒ ไม่เสนออัตราในประเทศ (ภ.ง.ด.3/53)</item>
/// <item>P2-1 ผังเดบิตตั้งต้นเป็นบัญชีย่อยจริงในผังมาตรฐาน · ไม่ใช่ต้นทุนขายของบริษัทที่ไม่ถือสต็อก</item>
/// <item>P2-5 ค่าส่วนกลางที่จ่ายนิติบุคคลอาคารชุด ⇒ ไม่เสนอหัก</item>
/// </list>
/// </summary>
public class OcrExpenseCategoryEc3Tests
{
    private readonly ITestOutputHelper _out;
    public OcrExpenseCategoryEc3Tests(ITestOutputHelper output) => _out = output;

    private const string NeutralVendor = "บริษัท ตัวอย่างดี จำกัด";

    /// <summary>รูปเดียวกับ <see cref="OcrExpenseCategoryRegressionTests.ResolveLikePipeline"/> + ยอดรวมของใบ + เลขผู้เสียภาษีผู้ขาย
    /// (ScanAsync มีทั้งสองค่าก่อนเรียก ApplyTo)</summary>
    private OcrExtractedData Run(string? vendor, IEnumerable<(string Desc, decimal Amount)> lines, string? vendorTaxId = null)
    {
        var items = lines.ToList();
        var total = items.Sum(i => i.Amount);
        var raw = $"{vendor}\nใบเสร็จรับเงิน\nเลขที่ RC-001\n" + string.Join("\n", items.Select(i => $"{i.Desc} {i.Amount:N2}"))
            + $"\nรวมทั้งสิ้น {total:N2}\n";
        var data = new OcrExtractedData { VendorName = vendor, VendorTaxId = vendorTaxId, TotalAmount = total };
        foreach (var (d, a) in items) data.Items.Add(new OcrExtractedLineItem { Description = d, Quantity = 1, UnitPrice = a, Amount = a });
        var text = ThaiTextNormalizer.Normalize(raw);
        var paid = data.Items.Where(i => (i.Amount ?? 0m) > 0m).Select(i => i.Description).ToList();
        var priced = data.Items.Select(i => new OcrPricedLine(i.Description, i.Amount ?? 0m)).Where(l => l.Amount > 0m).ToList();
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: vendor, headerDescription: null, lineDescriptions: paid, rawText: text,
            zeroAmountLineDescriptions: new List<string?>(), pricedLines: priced, diagnostics: new List<string>(),
            minorityWhtParts: new List<OcrMinorityWhtPart>());
        if (result != null) ExpenseCategoryResolver.ApplyTo(data, result, text);
        _out.WriteLine($"| {vendor} | {string.Join(" + ", items.Select(i => $"{i.Desc} {i.Amount:N0}"))} | "
            + $"{data.ExpenseCategory ?? "-"} | {data.DebitAccountCode ?? "-"} | conf {result?.Confidence.ToString("0.00") ?? "-"} | "
            + $"{data.SuggestedWhtRate?.ToString("0.##") ?? "-"} | {data.WhtIncomeTypeCode ?? "-"} |");
        foreach (var t in data.ReasoningTrace) _out.WriteLine("    " + t);
        return data;
    }

    private OcrExtractedData One(string line, decimal amount, string? vendor = NeutralVendor, string? taxId = null)
        => Run(vendor, new[] { (line, amount) }, taxId);

    private static void NoSuggestion(OcrExtractedData d)
    {
        Assert.Null(d.SuggestedWhtRate);
        Assert.Null(d.WhtIncomeTypeCode);
        Assert.False(d.HasWht);
        Assert.DoesNotContain(d.ReasoningTrace, t => t.Contains("กฎหมายกำหนดให้ผู้จ่ายหัก"));
    }

    // ── P1-1: คำซ้อน + วลีของแถม ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ทีวี 55 นิ้ว (รวมค่าขนส่ง)", 15900)]
    [InlineData("ปูนซีเมนต์ ฟรีค่าขนส่ง", 4500)]
    [InlineData("แอร์ 12000BTU (บริการซ่อมฟรี 1 ปี)", 15000)]
    [InlineData("เสื้อยืดสกรีน (ฟรีค่าออกแบบ)", 3000)]
    [InlineData("ตู้เย็น 2 ประตู ส่งฟรี", 12900)]
    [InlineData("Washing machine 10kg free installation", 14500)]
    public void P1_1_วลีของแถมหรือรวมในราคา_ไม่ใช่หลักฐานค่าบริการ(string line, int amount)
        => NoSuggestion(One(line, amount));

    [Theory]
    [InlineData("ค่าขนส่งสินค้า กรุงเทพ-ระยอง")]   // "ค่าขนส่ง" ⊃ "ขนส่ง"
    [InlineData("บริการซ่อมเครื่องถ่ายเอกสาร")]     // "บริการซ่อม" ⊃ "ซ่อม"
    public void P1_1_คำที่เป็นส่วนหนึ่งของคำที่นับแล้ว_ไม่บวกคะแนนซ้ำ(string line)
    {
        var r = ExpenseCategoryResolver.Resolve(null, null, new[] { line }, null);
        Assert.NotNull(r);
        // คำยาวหนึ่งคำบนบรรทัดที่มีเงิน = 2 คะแนน = 0.60 (เดิม 3 คะแนน = 0.70 จากคำเดียวบนกระดาษ)
        Assert.Equal(0.60m, r!.Confidence);
    }

    [Fact]
    public void P1_1_ทิศตรงข้าม_ค่าขนส่งจริงและบริการซ่อมจริงยังได้อัตราตามตาราง()
    {
        var tr = One("ค่าขนส่งสินค้า กรุงเทพ-ระยอง", 5000);
        Assert.Equal("8tr", tr.WhtIncomeTypeCode);
        Assert.Equal(ThaiWhtRateTable.RateFor("8tr", payeeIsJuristic: true), tr.SuggestedWhtRate);
        var rp = One("บริการซ่อมเครื่องถ่ายเอกสาร", 5000);
        Assert.Equal("8", rp.WhtIncomeTypeCode);
        Assert.Equal(ThaiWhtRateTable.RateFor("8", payeeIsJuristic: true), rp.SuggestedWhtRate);
        var ad = One("ค่าโฆษณาออนไลน์ เดือนตุลาคม", 5000);
        Assert.Equal("8ad", ad.WhtIncomeTypeCode);
        Assert.Equal(ThaiWhtRateTable.RateFor("8ad", payeeIsJuristic: true), ad.SuggestedWhtRate);
        // "ซ่อม…ฟรีค่าเดินทาง" — วลีของแถมถูกตัดเฉพาะส่วน "ฟรี…" ค่าซ่อมยังเป็นหลักฐาน
        var free = One("ค่าซ่อมแอร์ ฟรีค่าเดินทาง", 5000);
        Assert.Equal("8", free.WhtIncomeTypeCode);
    }

    // ── P1-2: เกณฑ์ 1,000 บาท ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ค่าซ่อมรองเท้า", 80, NeutralVendor)]
    [InlineData("ค่าขนส่ง", 50, "Lalamove")]
    [InlineData("ค่าพิมพ์เอกสาร", 120, NeutralVendor)]
    [InlineData("ค่าพิมพ์นามบัตร", 800, NeutralVendor)]
    [InlineData("ค่าซ่อมแอร์ ห้องประชุม", 999.99, NeutralVendor)]
    public void P1_2_ยอดต่ำกว่าเกณฑ์_คงหมวดแต่ไม่เสนออัตรา_และบอกเหตุผล(string line, double amount, string vendor)
    {
        var d = One(line, (decimal)amount, vendor);
        NoSuggestion(d);
        Assert.NotNull(d.ExpenseCategory);   // หมวด/ผังยังอยู่ (ทิ้งเฉพาะข้อเสนออัตรา)
        Assert.Contains(d.ReasoningTrace, t => t.Contains("ต่ำกว่าเกณฑ์") && t.Contains("ท.ป.4/2528 ข้อ 12"));
    }

    [Theory]
    [InlineData("ค่าซ่อมแอร์ ห้องประชุม", 1000)]   // ถึงเกณฑ์พอดี = ต้องหัก (≥ 1,000)
    [InlineData("ค่าพิมพ์นามบัตร", 1500)]
    public void P1_2_ทิศตรงข้าม_ถึงเกณฑ์ยังเสนอ(string line, int amount)
    {
        var d = One(line, amount);
        Assert.Equal("8", d.WhtIncomeTypeCode);
        Assert.Equal(ThaiWhtRateTable.RateFor("8", payeeIsJuristic: true), d.SuggestedWhtRate);
    }

    [Fact]
    public void P1_2_ฐานคือยอดรวมหักVAT_สูตรเดียวกับโน้ตตอนสร้างเอกสาร()
    {
        // 1,070 รวม VAT 70 ⇒ ฐาน 1,000 ⇒ ถึงเกณฑ์ · 1,060 รวม VAT 69.35 ⇒ ฐาน 990.65 ⇒ ไม่ถึง
        var r = ExpenseCategoryResolver.Resolve(null, null, new[] { "ค่าซ่อมแอร์ ห้องประชุม" }, "ค่าซ่อมแอร์ ห้องประชุม")!;
        var at = new OcrExtractedData { TotalAmount = 1070m, VatAmount = 70m };
        ExpenseCategoryResolver.ApplyTo(at, r, "ค่าซ่อมแอร์ ห้องประชุม");
        Assert.NotNull(at.SuggestedWhtRate);
        var below = new OcrExtractedData { TotalAmount = 1060m, VatAmount = 69.35m };
        ExpenseCategoryResolver.ApplyTo(below, r, "ค่าซ่อมแอร์ ห้องประชุม");
        Assert.Null(below.SuggestedWhtRate);
        // ไม่รู้ยอดเลย ⇒ ไม่ตัดสินแทน (พฤติกรรมเดิม — ข้อเสนอยังขึ้นเหลืองให้ตรวจ)
        var unknown = new OcrExtractedData();
        ExpenseCategoryResolver.ApplyTo(unknown, r, "ค่าซ่อมแอร์ ห้องประชุม");
        Assert.NotNull(unknown.SuggestedWhtRate);
    }

    // ── P1-3: ค่าประกัน<มัดจำ> ──────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> DepositPapers() => new[]
    {
        new object[] { "ค่าประกันห้อง|10000;ค่าเช่าห้อง|5000" },
        new object[] { "ค่าประกันมิเตอร์น้ำ|2000" },
        new object[] { "ค่าประกันความเสียหาย|5000;ค่าเช่าเต็นท์|3000" },
        new object[] { "ค่าประกันสัญญา 5%|25000" },
        new object[] { "ค่าประกันการเช่า 2 เดือน|20000" },
        new object[] { "ค่าประกันซอง|10000" },
        new object[] { "ค่าประกันผลงาน|15000" },
        new object[] { "ค่าประกันไฟฟ้า|3000" },
        new object[] { "ค่าประกันมิเตอร์ไฟ|2000" },
    };

    [Theory]
    [MemberData(nameof(DepositPapers))]
    public void P1_3_ค่าประกันที่เป็นเงินมัดจำ_ไม่ได้ผังค่าเบี้ยประกัน(string spec)
    {
        var lines = spec.Split(';').Select(x => x.Split('|')).Select(p => (p[0], decimal.Parse(p[1]))).ToList();
        var d = Run(NeutralVendor, lines);
        Assert.NotEqual("ค่าประกัน", d.ExpenseCategory);
        Assert.NotEqual("5800", d.DebitAccountCode);
        // ไม่มีผังเงินมัดจำในกฎ ⇒ ไม่ได้ผังค่าใช้จ่ายที่แต่งขึ้น · ทั้งใบไม่ได้ข้อเสนออัตราของหมวดที่เดาจากบรรทัดส่วนน้อย
        Assert.Null(d.SuggestedWhtRate);
        // ใบมัดจำล้วน ⇒ ไม่มีหลักฐานของหมวดค่าใช้จ่ายใดเลย ⇒ ไม่มีหมวด (เดิม "ค่าประกันการเช่า" ⇒ ค่าเช่า · "ค่าประกันไฟฟ้า" ⇒ ค่าไฟฟ้า)
        if (lines.Count == 1) Assert.Null(d.ExpenseCategory);
    }

    [Theory]
    [InlineData("เบี้ยประกันภัยรถยนต์")]
    [InlineData("ประกันภัยทรัพย์สิน")]
    [InlineData("ค่าประกันรถยนต์ ชั้น 1")]
    [InlineData("ประกันอัคคีภัย อาคารสำนักงาน")]
    [InlineData("ประกันชีวิตกลุ่ม")]
    [InlineData("กรมธรรม์เลขที่ 123456")]
    [InlineData("ค่าประกันไฟไหม้ โกดัง")]
    [InlineData("ค่าประกันน้ำท่วม")]
    public void P1_3_ทิศตรงข้าม_ประกันภัยจริงยังได้5800(string line)
    {
        var d = One(line, 12000);
        Assert.Equal("ค่าประกัน", d.ExpenseCategory);
        Assert.Equal("5800", d.DebitAccountCode);
    }

    // ── P1-4: ชื่อผู้ขายมีคำของงานบริการ แต่บรรทัดเป็นสินค้าล้วน ────────────────────────────────────

    [Theory]
    [InlineData("ศูนย์ซ่อมและอะไหล่รถยนต์", "ผ้าเบรคหน้า|2400")]
    [InlineData("ช.ซ่อมยนต์ อะไหล่", "ผ้าเบรคหน้า|2400")]
    [InlineData("ห้างฯ ผู้รับเหมา วัสดุ", "ปูนซีเมนต์ 50 กก.|9000;ทรายหยาบ 2 คิว|3000")]
    [InlineData("ร้านรับเหมาก่อสร้างและวัสดุ", "เหล็กเส้น 12 มม.|6000;อิฐมอญ 1,000 ก้อน|1500")]
    public void P1_4_ชื่อผู้ขายไม่ใช่หลักฐานค่าบริการเมื่อทุกบรรทัดเป็นสินค้า(string vendor, string spec)
    {
        var lines = spec.Split(';').Select(x => x.Split('|')).Select(p => (p[0], decimal.Parse(p[1]))).ToList();
        var d = Run(vendor, lines);
        NoSuggestion(d);
        Assert.Contains(d.ReasoningTrace, t => t.Contains("ทุกบรรทัดที่มีเงินเป็นสินค้า"));
    }

    [Theory]
    [InlineData("ศูนย์ซ่อมรถยนต์ ช่างเอก", "ค่าแรงเปลี่ยนผ้าเบรค|3000;ผ้าเบรคหน้า|2400")]   // มีบรรทัดค่าแรง (ส่วนใหญ่ของมูลค่า) ⇒ ไม่ใช่สินค้าล้วน
    [InlineData("อู่ช่างเอก บริการซ่อมรถยนต์", "เช็คระยะ 10,000 กม.|3500")]                  // บรรทัดไม่ใช่สินค้า ⇒ ชื่อร้านยังเป็นหลักฐาน (เดิม)
    [InlineData("อู่ช่างเอก บริการซ่อมรถยนต์", "เปลี่ยนผ้าเบรคหน้า|2400")]                   // "เปลี่ยน…" = งานซ่อม ไม่ใช่สินค้าล้วน
    [InlineData("ห้างฯ ผู้รับเหมา ช่างดี", "งานเทพื้นคอนกรีต งวดที่ 1|50000")]
    public void P1_4_ทิศตรงข้าม_ใบงานบริการจากร้านเดียวกันยังเสนอ(string vendor, string spec)
    {
        var lines = spec.Split(';').Select(x => x.Split('|')).Select(p => (p[0], decimal.Parse(p[1]))).ToList();
        var d = Run(vendor, lines);
        Assert.NotNull(d.SuggestedWhtRate);
        Assert.Equal(ThaiWhtRateTable.RateFor(d.WhtIncomeTypeCode, payeeIsJuristic: true), d.SuggestedWhtRate);
    }

    // ── P1-5: ผู้ขายต่างประเทศ ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Meta Platforms Ireland Limited", "IE9692928F", "Facebook ads", 3000)]
    [InlineData("Google Asia Pacific Pte. Ltd.", "200817984R", "ค่าโฆษณา", 5000)]
    [InlineData("Amazon Web Services, Inc.", "US 91-1646860", "ค่าบริการ cloud service fee", 8000)]
    public void P1_5_ผู้ขายต่างประเทศ_ไม่ได้อัตราในประเทศ(string vendor, string taxId, string line, int amount)
    {
        var d = One(line, amount, vendor, taxId);
        NoSuggestion(d);
        Assert.NotNull(d.ExpenseCategory);   // หมวด/ผังยังเติม
        Assert.Contains(d.ReasoningTrace, t => t.Contains("ม.70") && t.Contains("ภ.พ.36"));
    }

    [Fact]
    public void P1_5_ทิศตรงข้าม_ผู้ขายไทย13หลักยังได้2เปอร์เซ็นต์()
    {
        var d = One("ค่าโฆษณา", 5000, "บริษัท โฆษณาไทย จำกัด", "0105556000025");
        Assert.Equal("8ad", d.WhtIncomeTypeCode);
        Assert.Equal(ThaiWhtRateTable.RateFor("8ad", payeeIsJuristic: true), d.SuggestedWhtRate);
    }

    // ── P2-5: ค่าส่วนกลาง ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("นิติบุคคลอาคารชุด ตัวอย่างคอนโด", "ค่าบำรุงรักษาส่วนกลาง ต.ค. 2569")]
    [InlineData("นิติบุคคลอาคารชุด ตัวอย่างคอนโด", "ค่าส่วนกลาง 12 เดือน")]
    [InlineData(NeutralVendor, "ค่าส่วนกลาง เดือนตุลาคม")]
    public void P2_5_ค่าส่วนกลาง_ไม่เสนอหัก(string vendor, string line)
    {
        var d = One(line, 3600, vendor);
        NoSuggestion(d);
        Assert.Equal("ค่าส่วนกลาง", d.ExpenseCategory);
        Assert.Contains(d.ReasoningTrace, t => t.Contains("บริษัท/ห้างฯ") && t.Contains("40(8)"));   // บอกเงื่อนไขที่ระบบตัดสินแทนไม่ได้
    }

    [Fact]
    public void P2_5_ทิศตรงข้าม_ค่าบำรุงรักษาลิฟต์จากบริษัทยังได้3เปอร์เซ็นต์()
    {
        var d = One("ค่าบำรุงรักษาลิฟต์ รายปี", 5000);
        Assert.Equal("8", d.WhtIncomeTypeCode);
    }

    // ── "ตรวจแล้วถูก" ของฝ่ายค้าน — ล็อกไว้ไม่ให้ถูกแตะ ──────────────────────────────────────────────

    [Theory]
    [InlineData("ค่าขนส่ง|800;กระเบื้องปูพื้น 60x60|1500")]
    [InlineData("ข้าวผัดกุ้ง|1200;ค่าบริการ 10%|120")]
    [InlineData("ค่าห้องพัก Deluxe|3000;ค่าบริการ|300")]
    public void ตรวจแล้วถูก_บรรทัดส่วนน้อยไม่ถูกหักทั้งใบ(string spec)
    {
        var lines = spec.Split(';').Select(x => x.Split('|')).Select(p => (p[0], decimal.Parse(p[1]))).ToList();
        NoSuggestion(Run("ร้านตัวอย่าง", lines));
    }

    [Theory]
    [InlineData("ค่าประกันสังคม ส่วนนายจ้าง")]
    [InlineData("สินค้ารับประกัน 1 ปี")]
    [InlineData("เงินประกันสัญญา 5%")]
    public void ตรวจแล้วถูก_ข้อยกเว้นประกันยังทำงาน(string line)
        => Assert.NotEqual("5800", One(line, 12000).DebitAccountCode);

    // ── P2-1: ผังเดบิตตั้งต้นต้องเป็นบัญชีย่อยจริงในผังมาตรฐาน ───────────────────────────────────────────

    /// <summary>ผังมาตรฐานจริง (ทุกประเภทกิจการ) — "บัญชีคุม" = มีรหัสอื่นที่ขึ้นต้นด้วยรหัสนี้ (ลำดับชั้นของแม่แบบผูกแม่ด้วยรหัสต้น
    /// <c>AccountingService</c> ระดับ 2/3/4 = 1/2/3 ตัวแรก) · ชื่อเดียวกับ PostingAccountRule ("มีบัญชีย่อย")</summary>
    private static List<(GlAccountCandidate Acc, bool IsControl)> RealTemplate(IndustryType industry)
    {
        var tpl = ChartOfAccountTemplates.GetCommonAccounts().Concat(ChartOfAccountTemplates.GetIndustryAccounts(industry))
            .GroupBy(t => t.Code).Select(g => g.First()).ToList();
        return tpl.Select(t => (new GlAccountCandidate(t.Code, t.NameTh, t.Type),
            tpl.Any(o => o.Code != t.Code && o.Code.StartsWith(t.Code, StringComparison.Ordinal)))).ToList();
    }

    [Theory]
    [InlineData(IndustryType.Service)]
    [InlineData(IndustryType.Trading)]
    [InlineData(IndustryType.Hotel)]
    public void P2_1_รหัสเดบิตตั้งต้นทุกตัวที่เป็นค่าใช้จ่ายหรือสินทรัพย์_มีอยู่จริงและไม่ใช่บัญชีคุม(IndustryType industry)
    {
        var coa = RealTemplate(industry);
        var keeps = InventoryIndustry.KeepsInventory(industry);
        foreach (var type in new[] { "TaxInvoice", "Invoice", "Receipt", "PurchaseOrder", "DebitNote", "CreditNote", "WHT", null })
        foreach (var seed in OcrDocTypeDefaultAccounts.DebitFallbackSeeds(type, keeps))
        {
            var row = coa.SingleOrDefault(a => a.Acc.Code == seed.Code);
            if (row.Acc.Code == null) { Assert.True(seed.Code.StartsWith('2'), $"{type}: {seed.Code} ไม่มีในผังมาตรฐาน"); continue; }  // หนี้สิน (ตัวเลือกตัดเอง)
            Assert.False(row.IsControl, $"{type}: {seed.Code} เป็นบัญชีคุม");
            Assert.Equal(seed.Name, row.Acc.Name);
            // รูปเดียวกับ PickCategorySeededDebitAsync (รหัสขึ้นต้นด้วย seed · ตัดบัญชีคุม) ⇒ ได้ seed ตัวเอง
            var picked = GlDebitAccountPicker.Pick(coa.Where(a => !a.IsControl && a.Acc.Code.StartsWith(seed.Code)).Select(a => a.Acc), seed.Code);
            Assert.Equal(seed.Code, picked?.Code);
        }
    }

    [Fact]
    public void P2_1_ใบกำกับภาษีของบริษัทที่ไม่ถือสต็อก_ไม่ใช่ต้นทุนขาย_บริษัทถือสต็อกได้ต้นทุนสินค้า()
    {
        var service = OcrDocTypeDefaultAccounts.For("TaxInvoice", keepsInventory: false)!.Value;
        Assert.False(service.DebitCode.StartsWith("51"), service.DebitCode);
        Assert.Equal(OcrDocTypeDefaultAccounts.GenericExpenseDebit.Code, service.DebitCode);
        var trading = OcrDocTypeDefaultAccounts.For("TaxInvoice", keepsInventory: true)!.Value;
        Assert.Equal("51110", trading.DebitCode);
        // ParseThaiDocument ยังไม่รู้จักบริษัท ⇒ ค่าใช้จ่ายทั่วไป + ไฮไลต์เหลือง (P2-3)
        var parsed = OcrService.ParseThaiDocument("ใบกำกับภาษี\nเลขที่ INV-1\nรวมทั้งสิ้น 1,070.00\n");
        Assert.Equal(OcrDocTypeDefaultAccounts.GenericExpenseDebit.Code, parsed.DebitAccountCode);
        Assert.True(parsed.FieldConfidence[OcrFieldKeys.DebitAccount] < 0.85);
        // ต่ำกว่าเกณฑ์ทับของตัวเรียน (0.55) — ไม่เปลี่ยนว่าใครทับค่าตั้งต้นได้
        Assert.True(parsed.FieldConfidence[OcrFieldKeys.DebitAccount] < 0.55);
    }

    [Fact]
    public void P2_1_ผังจริง_ค้นตามชื่อโดยไม่ตัดบัญชีคุม_ได้บัญชีคุม_ตัดแล้วได้บัญชีย่อย()
    {
        // บั๊กเดิม: seed "5300 ค่าใช้จ่ายบริหาร" ค้นชื่อ "บริหาร" บนผังจริง ⇒ "54 ค่าใช้จ่ายในการบริหาร" (บัญชีคุม)
        var coa = RealTemplate(IndustryType.Service);
        var byName = coa.Where(a => a.Acc.Name.Contains("บริหาร")).ToList();
        var unfiltered = GlDebitAccountPicker.Pick(byName.Select(a => a.Acc), "5300");
        Assert.True(coa.Single(a => a.Acc.Code == unfiltered!.Value.Code).IsControl);
        var filtered = GlDebitAccountPicker.Pick(byName.Where(a => !a.IsControl).Select(a => a.Acc), "5300");
        Assert.False(coa.Single(a => a.Acc.Code == filtered!.Value.Code).IsControl);
    }
}
