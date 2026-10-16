using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests;

/// <summary>
/// **ฝ่ายค้านของ f5b10674 (ถอดหมวดค่าใช้จ่ายจาก OCR)** — ถดถอยที่ค่าตั้งต้น "ค่าบริการ" เคยกลบไว้ (กฎเหล็ก #4 H "ห้ามทำให้แย่ลง")
///
/// <para>P1-A: ใบค่าบริการจริงบนเส้น python/Tesseract เสียข้อเสนอหัก ณ ที่จ่าย — คำของกฎยาว &lt; 6 ตัวได้ 1 คะแนน (มั่นใจ 0.50 &lt; เกณฑ์ 0.60)
/// ⇒ เติมคำยาวที่แปลว่า "จ่ายค่า…" ให้กฎที่ถูก (อัตรามาจาก <see cref="ThaiWhtRateTable"/> ตัวเดียว) · ทิศตรงข้าม: ใบซื้อของไม่ได้หัก</para>
/// <para>P1-B: ใบประกันจริงที่ไม่มีคำว่า เบี้ย/ประกันภัย/กรมธรรม์ ได้ผัง 5800 กลับมา · ประกันสังคม/รับประกันสินค้า/เงินประกันสัญญา ยังไม่ได้</para>
/// </summary>
public class OcrExpenseCategoryRegressionTests
{
    private readonly ITestOutputHelper _out;
    public OcrExpenseCategoryRegressionTests(ITestOutputHelper output) => _out = output;

    private const string NeutralVendor = "บริษัท ตัวอย่างดี จำกัด";

    /// <summary>อาร์กิวเมนต์รูปเดียวกับ <c>OcrService.ScanAsync</c> (บรรทัดที่มีเงิน · แถวยอด 0 · บรรทัดพร้อมยอด · ข้อความ normalize ·
    /// headerDescription = null) — เส้น python ocr-service/Tesseract ที่ engine คืนบรรทัดมา</summary>
    internal static OcrExtractedData ResolveLikePipeline(string? vendor, IEnumerable<(string Desc, decimal Amount)> lines,
        string rawText, string? debitAlready = null)
    {
        var data = new OcrExtractedData { VendorName = vendor, DebitAccountCode = debitAlready };
        foreach (var (d, a) in lines) data.Items.Add(new OcrExtractedLineItem { Description = d, Quantity = 1, UnitPrice = a, Amount = a });
        var text = ThaiTextNormalizer.Normalize(rawText);
        var paid = data.Items.Where(i => (i.Amount ?? 0m) > 0m || (i.UnitPrice ?? 0m) > 0m).Select(i => i.Description).ToList();
        var zero = data.Items.Where(i => (i.Amount ?? 0m) <= 0m && (i.UnitPrice ?? 0m) <= 0m).Select(i => i.Description).ToList();
        var priced = data.Items.Select(i => new OcrPricedLine(i.Description, i.Amount ?? 0m)).Where(l => l.Amount > 0m).ToList();
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: data.VendorName, headerDescription: null, lineDescriptions: paid, rawText: text,
            zeroAmountLineDescriptions: zero, pricedLines: priced, diagnostics: new List<string>(),
            minorityWhtParts: new List<OcrMinorityWhtPart>());
        if (result != null) ExpenseCategoryResolver.ApplyTo(data, result, text);
        return data;
    }

    private static string Receipt(string line, decimal amount)
        => $"{NeutralVendor}\nใบเสร็จรับเงิน\nเลขที่ RC-001\nวันที่ 01/10/2026\n{line} {amount:N2}\nรวมทั้งสิ้น {amount:N2}\n";

    private OcrExtractedData Probe(string line, decimal amount = 5000m)
    {
        var data = ResolveLikePipeline(NeutralVendor, new[] { (line, amount) }, Receipt(line, amount));
        _out.WriteLine($"| {line} | {data.ExpenseCategory ?? "-"} | {data.DebitAccountCode ?? "-"} | "
            + $"{data.SuggestedWhtRate?.ToString("0.##") ?? "-"} | {data.WhtIncomeTypeCode ?? "-"} |");
        return data;
    }

    // ── P1-A ครึ่งที่ 1: ใบค่าบริการจริง 21 ใบ (13 ใบที่ 3% ถูก + 4 ใบที่ผิดมาก่อน + 4 ใบที่ไม่เคยเสีย) ──────────────────

    /// <summary>อัตราที่คาด = อัตราของประเภทเงินได้ในตารางเจ้าของ (ห้ามเขียนตัวเลขซ้ำในเทสต์ — ถามตาราง)</summary>
    [Theory]
    // 13 ใบที่เสียข้อเสนอ 3% ที่ถูกต้อง (f5b10674)
    [InlineData("ค่าซ่อมแอร์ ห้องประชุม", "8")]
    [InlineData("ค่าซ่อมรถยนต์ ทะเบียน กข 1234", "8")]
    [InlineData("ค่าดูแลระบบ MA รายปี", "8")]
    [InlineData("ค่าทนายความ คดีแพ่ง", "6")]
    [InlineData("ค่าทำบัญชีรายเดือน ตุลาคม 2569", "6")]
    [InlineData("ค่าสอบบัญชี ประจำปี 2569", "6")]
    [InlineData("ค่าวิชาชีพ", "6")]
    [InlineData("ค่ารับเหมาก่อสร้าง งวดที่ 2", "7")]
    [InlineData("เงินมัดจำค่าก่อสร้าง 30%", "7")]
    [InlineData("ค่ารักษาความปลอดภัย เดือนตุลาคม", "8")]
    [InlineData("ค่าออกแบบโลโก้", "8")]
    [InlineData("ค่าพิมพ์ใบปลิว 5,000 แผ่น", "8")]
    [InlineData("ค่าตัดหญ้า ดูแลสวน", "8")]
    // 4 ใบที่ผิดมาตั้งแต่ก่อน f5b10674 (ค่าตั้งต้น "ค่าบริการ" ชนะ ⇒ 3% 40(8) ทั้งที่ตารางกำหนดอัตราอื่น)
    [InlineData("ค่าโฆษณาออนไลน์ เดือนตุลาคม", "8ad")]
    [InlineData("ค่าโฆษณาวิทยุ 30 วินาที", "8ad")]
    [InlineData("ค่าขนส่งสินค้า กรุงเทพ-ระยอง", "8tr")]
    [InlineData("ค่าขนย้ายเฟอร์นิเจอร์สำนักงาน", "8tr")]
    // 4 ใบที่ไม่เคยเสีย (คำยาว ≥ 6 ตัวอยู่แล้ว) — ห้ามถูกแตะ
    [InlineData("ค่าบริการรายเดือน ตุลาคม 2569", "8")]
    [InlineData("ค่าเช่าสำนักงาน เดือนตุลาคม", "5")]
    [InlineData("ค่าทำความสะอาดสำนักงาน", "8")]
    [InlineData("ค่าที่ปรึกษาธุรกิจ", "6")]
    public void ใบค่าบริการจริงบนเส้นที่ไม่มีหัวเรื่อง_ได้ประเภทเงินได้และอัตราตามตารางเจ้าของ(string line, string incomeCode)
    {
        var data = Probe(line);
        Assert.Equal(incomeCode, data.WhtIncomeTypeCode);
        Assert.Equal(ThaiWhtRateTable.RateFor(incomeCode, payeeIsJuristic: true), data.SuggestedWhtRate);
        Assert.Equal(WhtEvidenceSource.Statute, data.SuggestedWhtSource);
        Assert.False(data.HasWht);   // ข้อเสนอ ≠ ยอดที่กระดาษพิมพ์ — ผู้ใช้ยืนยันเอง
        // ข้อเสนอต้องขึ้นเหลืองให้ตรวจ (กฎเหล็ก #3 ข้อ 3)
        Assert.True(data.FieldConfidence[OcrFieldKeys.WhtRate] < 0.85);
    }

    // ── P1-A ครึ่งที่ 2 (ทิศตรงข้าม): ใบซื้อของไม่ได้หัก ─────────────────────────────────────────────

    [Theory]
    [InlineData("ร้านวัสดุก่อสร้าง เจริญพาณิชย์", "ปูนซีเมนต์ 50 กก.|เหล็กเส้น 12 มม.|ทรายหยาบ")]
    [InlineData("บริษัท ก่อสร้างภัณฑ์ จำกัด", "ท่อพีวีซี 4 นิ้ว|ข้อต่อ")]
    [InlineData("ร้านอะไหล่แอร์", "คอมเพรสเซอร์แอร์|น้ำยาแอร์ R32")]
    [InlineData("ร้านเครื่องเขียน", "กระดาษ A4|ปากกาลูกลื่น|แฟ้มเอกสาร")]
    [InlineData("ร้านสะดวกซื้อ ตัวอย่างพาณิชย์", "น้ำดื่ม 600 มล.|ขนมปังโฮลวีท")]
    [InlineData("ร้านอุปกรณ์ไฟฟ้า", "หลอดไฟ LED 18W|สายไฟ VAF 2x1.5")]
    // ชื่อสินค้าที่มีคำของงานบริการ — กฎใช้วลี "ค่า…/บริการ…/จ้าง…" จึงไม่ติด
    [InlineData("ร้านเครื่องมือเกษตร", "เครื่องตัดหญ้า 2 จังหวะ|อุปกรณ์ดูแลสวน ชุดเล็ก")]
    [InlineData("ร้านอุปกรณ์คลังสินค้า", "รถเข็นขนย้าย 4 ล้อ|กล่องลังกระดาษ")]
    [InlineData("ร้านกล้องวงจรปิด", "กล้องวงจรปิด ระบบรักษาความปลอดภัย|สายแลน CAT6")]
    [InlineData("ร้านวัสดุหลังคา", "วัสดุซ่อมแซมหลังคา|ซิลิโคนอุดรอยรั่ว")]
    public void ใบซื้อของ_ไม่ได้ข้อเสนอหักณที่จ่าย(string vendor, string lines)
    {
        var items = lines.Split('|').Select(d => (d, 1200m)).ToList();
        var raw = $"{vendor}\nใบเสร็จรับเงิน\n" + string.Join("\n", items.Select(i => $"{i.d} {i.Item2:N2}")) + "\nรวมทั้งสิ้น 3,600.00";
        var data = ResolveLikePipeline(vendor, items, raw);
        _out.WriteLine($"{vendor}: category={data.ExpenseCategory ?? "-"} wht={data.SuggestedWhtRate?.ToString() ?? "-"} income={data.WhtIncomeTypeCode ?? "-"}");
        Assert.Null(data.SuggestedWhtRate);
        Assert.Null(data.WhtIncomeTypeCode);
        Assert.False(data.HasWht);
    }

    [Fact]
    public void คำว่าค่าบริการบนใบซื้อของจากร้านปลีก_เป็นบรรทัดส่วนน้อย_ไม่ได้หัก()
    {
        // "ค่าบริการจัดส่ง 50" บนใบซื้อเครื่องใช้ไฟฟ้า 12,900 — คำยาวผูกกับเงินแต่เป็นส่วนน้อยของมูลค่า (OcrLineValueShare)
        var items = new[] { ("พัดลมตั้งพื้น 16 นิ้ว", 2900m), ("ตู้เย็น 2 ประตู", 10000m), ("ค่าบริการจัดส่ง", 50m) };
        var raw = "ร้านไฟฟ้าดี\nใบเสร็จรับเงิน\nพัดลมตั้งพื้น 16 นิ้ว 2,900.00\nตู้เย็น 2 ประตู 10,000.00\nค่าบริการจัดส่ง 50.00\nรวมทั้งสิ้น 12,950.00";
        var data = ResolveLikePipeline("ร้านไฟฟ้าดี", items, raw);
        Assert.Null(data.SuggestedWhtRate);
        Assert.Null(data.WhtIncomeTypeCode);
    }

    [Fact]
    public void ค่าขนส่งบรรทัดส่วนน้อยบนใบซื้อของ_ไม่ได้หัก1เปอร์เซ็นต์ทั้งใบ()
    {
        // คำยาว "ค่าขนส่ง" ที่เติมใหม่ต้องไม่ทะลุด่านบรรทัดส่วนน้อย (ค่าส่งของที่ผู้ขายเก็บ = ส่วนหนึ่งของราคาสินค้า)
        var items = new[] { ("ปูนซีเมนต์ 50 กก.", 9000m), ("ค่าขนส่ง", 300m) };
        var raw = "ร้านวัสดุ\nใบเสร็จรับเงิน\nปูนซีเมนต์ 50 กก. 9,000.00\nค่าขนส่ง 300.00\nรวมทั้งสิ้น 9,300.00";
        var data = ResolveLikePipeline("ร้านวัสดุ", items, raw);
        Assert.Null(data.SuggestedWhtRate);
        Assert.NotEqual("8tr", data.WhtIncomeTypeCode);
    }

    [Fact]
    public void หลักฐานอ่อน_มั่นใจต่ำกว่าเกณฑ์_ไม่เขียนว่ากฎหมายกำหนดให้หัก()
    {
        // "ซ่อม" คำสั้นบนบรรทัดเดียว = 1 คะแนน (มั่นใจ 0.50 < 0.60) — ข้อความที่อ้าง "สาเหตุ" (กฎหมายกำหนดให้หัก) ต้องเขียนเฉพาะเมื่อตัดสินแล้ว
        var data = ResolveLikePipeline(NeutralVendor, new[] { ("งานซ่อม", 1500m) }, Receipt("งานซ่อม", 1500m));
        foreach (var t in data.ReasoningTrace) _out.WriteLine(t);
        Assert.Null(data.SuggestedWhtRate);
        Assert.DoesNotContain(data.ReasoningTrace, t => t.Contains("กฎหมายกำหนดให้ผู้จ่ายหัก"));
        Assert.Contains(data.ReasoningTrace, t => t.Contains("หลักฐานยังไม่พอ"));
    }

    [Fact]
    public void หลักฐานพอ_ยังเขียนว่ากฎหมายกำหนดให้หัก_เมื่อกระดาษไม่พิมพ์ยอดหัก()
    {
        var data = Probe("ค่าซ่อมแอร์ ห้องประชุม");
        Assert.Contains(data.ReasoningTrace, t => t.Contains("กฎหมายกำหนดให้ผู้จ่ายหัก 3%"));
    }

    [Fact]
    public void อัตราของทุกกฎ_มาจากตารางเจ้าของ_ไม่มีตัวเลขซ้ำในตัวจัดหมวด()
    {
        // ตัวตั้งตัวเดียว (CLAUDE.md F2 ข้อ 4): ไฟล์ตัวจัดหมวดห้ามมี "StatutoryWhtRate: <ตัวเลข>" — อัตราถามจาก ThaiWhtRateTable ด้วยรหัส ม.40
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/Services/Implementations/Ocr/ExpenseCategoryResolver.cs"));
        Assert.DoesNotMatch(@"StatutoryWhtRate:\s*\d", src);
        // ดอกเบี้ย 40(4)(ก) ยังเป็นอัตราที่ขึ้นกับชนิดผู้รับ — ApplyTo ไม่เติมเมื่อไม่รู้ชนิดผู้รับ (พฤติกรรมเดิม)
        var r = ExpenseCategoryResolver.Resolve(null, null, new[] { "ดอกเบี้ยเงินกู้ เดือนตุลาคม" }, "ดอกเบี้ยเงินกู้ เดือนตุลาคม 5,000.00");
        Assert.NotNull(r);
        Assert.Equal("4a", r!.WhtIncomeTypeCode);
        Assert.Equal(ThaiWhtRateTable.Find("4a")!.IndividualRate, r.StatutoryWhtRate);
    }

    // ── P1-B: ประกัน ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ค่าประกันรถยนต์ ชั้น 1")]
    [InlineData("ต่อประกันรถ ทะเบียน กข 1234")]
    [InlineData("ประกันอุบัติเหตุกลุ่มพนักงาน")]
    [InlineData("ประกันอัคคีภัย อาคารสำนักงาน")]
    [InlineData("ค่าประกันทรัพย์สิน คลังสินค้า")]
    [InlineData("ประกันกลุ่ม สุขภาพพนักงาน 20 คน")]
    [InlineData("ผู้รับประกันภัย ค่าเบี้ย")]   // "รับประกันภัย" ไม่ใช่ "รับประกันสินค้า"
    public void ใบประกันจริง_เส้นAzureที่ยังไม่มีผัง_ได้ผังค่าประกัน(string line)
    {
        var data = ResolveLikePipeline(NeutralVendor, new[] { (line, 12000m) }, Receipt(line, 12000m));
        _out.WriteLine($"{line}: category={data.ExpenseCategory ?? "-"} debit={data.DebitAccountCode ?? "-"}");
        Assert.Equal("ค่าประกัน", data.ExpenseCategory);
        Assert.Equal("54510", data.DebitAccountCode);
        Assert.Null(data.SuggestedWhtRate);   // กฎประกันไม่เสนออัตรา (เบี้ยชีวิต/วินาศภัยคนละเรื่อง — พฤติกรรมเดิม)
    }

    [Theory]
    [InlineData("ค่าประกันสังคม ส่วนนายจ้าง")]
    [InlineData("เงินประกันสังคม")]
    [InlineData("สินค้ารับประกัน 1 ปี")]
    [InlineData("รับประกันงานซ่อม 6 เดือน")]
    [InlineData("เงินประกันสัญญา 5%")]
    [InlineData("เงินประกันซอง")]
    [InlineData("Premium Quality")]
    public void คำประกันที่ไม่ใช่ซื้อความคุ้มครอง_ไม่ได้ผังค่าประกัน(string line)
    {
        var data = ResolveLikePipeline(NeutralVendor, new[] { (line, 12000m) }, Receipt(line, 12000m));
        _out.WriteLine($"{line}: category={data.ExpenseCategory ?? "-"} debit={data.DebitAccountCode ?? "-"}");
        Assert.NotEqual("ค่าประกัน", data.ExpenseCategory);
        Assert.NotEqual("54510", data.DebitAccountCode);
    }

    // ── สินค้าใน corpus จริง (ทิศตรงข้ามบนกระดาษจริง) ───────────────────────────────────────────────

    public static IEnumerable<object[]> GoodsPapers()
        => OcrReplayHarness.Corpus.Where(p => GoodsPaperNames.Contains(p.Name)).Select(p => new object[] { p.Name });

    /// <summary>ใบซื้อของใน corpus (ข้อความ/บรรทัดตามกระดาษจริง) — ห้ามได้ข้อเสนอหัก ณ ที่จ่าย · หกใบแรก = ใบเสร็จที่ก่อน f5b10674 ได้ 3% 40(8)
    /// จากค่าตั้งต้น "ค่าบริการ" ล้วน (ใบที่เจ็ดของชุดนั้นคือ deposit-real = มัดจำค่าก่อสร้างจริง ⇒ ดูเทสต์ถัดไป)</summary>
    internal static readonly HashSet<string> GoodsPaperNames = new()
    {
        "luckyway-swapped", "buyer-name-truncated", "deposit-form-row-zero", "row-index-receipt", "sku-code-on-row",
        "weighed-item-qty-not-printed",
        "makro-correct", "winepro-vat-included", "hardware-bill-discount", "hardware-bill-discount-amounts-only",
        "supermarket-member-discount", "wholesale-mixed-vat", "makro-page3-total-first", "makro-page2-vat-added-twice",
        "uptoyou-shopee-pay-not-total", "scommerce-lazada-prevat-discount", "boonsap-original-copy-pages",
        "water-genuine-repeat-rows", "pramie-line-discount-re111",
    };

    [Theory]
    [MemberData(nameof(GoodsPapers))]
    public void ใบซื้อของในคลังกระดาษจริง_ไม่ได้ข้อเสนอหักณที่จ่าย(string name)
    {
        var p = OcrReplayHarness.Corpus.Single(x => x.Name == name);
        var parsed = OcrService.ParseThaiDocument(p.RawText);
        var lines = PipelineLines(p, parsed);
        var data = ResolveLikePipeline(parsed.VendorName ?? p.VendorNameFromEngine, lines, p.RawText, parsed.DebitAccountCode);
        _out.WriteLine($"{name}: lines={lines.Count} category={data.ExpenseCategory ?? "-"} wht={data.SuggestedWhtRate?.ToString() ?? "-"} income={data.WhtIncomeTypeCode ?? "-"}");
        Assert.Null(data.SuggestedWhtRate);
        Assert.False(data.HasWht);
    }

    [Fact]
    public void ใบมัดจำค่าก่อสร้างจริงในคลัง_ได้ค่ารับเหมา40_7_กลับมา()
    {
        // ใบที่เจ็ดของชุด "ได้ 3% จากค่าตั้งต้น" — เป็นมัดจำค่าก่อสร้างจริง (หัก ณ ที่จ่ายตอนจ่ายมัดจำ) · ก่อน f5b10674 ได้ 40(8) ด้วยความบังเอิญ ·
        // f5b10674 ไม่มีข้อเสนอ · ตอนนี้ได้จากกระดาษเอง: กฎค่ารับเหมา 40(7)
        var p = OcrReplayHarness.Corpus.Single(x => x.Name == "deposit-real");
        var parsed = OcrService.ParseThaiDocument(p.RawText);
        var data = ResolveLikePipeline(parsed.VendorName, PipelineLines(p, parsed), p.RawText, parsed.DebitAccountCode);
        Assert.Equal("7", data.WhtIncomeTypeCode);
        Assert.Equal(ThaiWhtRateTable.RateFor("7", payeeIsJuristic: true), data.SuggestedWhtRate);
    }

    // ── P2-A: ด่านวางฝั่งตัดเดบิตแล้ว หมวดไม่มีผัง ⇒ ผังตั้งต้นของชนิดกระดาษ (ห้ามเดบิตว่าง) ─────────────────────────

    [Fact]
    public void เดบิตแทนของด่านวางฝั่ง_ตกไปผังตั้งต้นของชนิดกระดาษแล้วผังค่าใช้จ่ายทั่วไป()
    {
        // ฝ่ายค้าน EC2 P2-1: รหัสเป็นบัญชีย่อยจริงในผังมาตรฐาน (เดิม 5100/5300 ไม่มีในแม่แบบ ⇒ ได้บัญชีคุม) — ล็อกกับแม่แบบจริงใน OcrExpenseCategoryEc3Tests
        var generic = OcrDocTypeDefaultAccounts.GenericExpenseDebit;
        Assert.Equal(new[] { generic }, OcrDocTypeDefaultAccounts.DebitFallbackSeeds("Receipt"));
        Assert.Equal(new[] { generic }, OcrDocTypeDefaultAccounts.DebitFallbackSeeds("TaxInvoice"));   // ไม่ถือสต็อก ⇒ ไม่ใช่ต้นทุนขาย
        Assert.Equal(new[] { ("51110", "ต้นทุนสินค้า"), generic }, OcrDocTypeDefaultAccounts.DebitFallbackSeeds("TaxInvoice", purchaseIsStock: true));
        Assert.Equal(new[] { generic }, OcrDocTypeDefaultAccounts.DebitFallbackSeeds(null));
        // ใบลดหนี้: ผังตั้งต้นเดบิตเป็นเจ้าหนี้ (หนี้สิน) — ผู้เลือกตัดทิ้ง ⇒ ผังค่าใช้จ่ายทั่วไปเป็นชั้นถัดไปเสมอ
        Assert.Equal(generic.Code, OcrDocTypeDefaultAccounts.DebitFallbackSeeds("CreditNote").Last().Code);
        // ตารางเดียวกับ ParseThaiDocument (เส้น Tesseract)
        var parsed = OcrService.ParseThaiDocument("ใบกำกับภาษี\nเลขที่ INV-1\nรวมทั้งสิ้น 1,070.00\n");
        Assert.Equal(OcrDocTypeDefaultAccounts.For(parsed.DocumentType)!.Value.DebitCode, parsed.DebitAccountCode);
    }

    [Fact]
    public void ผังที่ตัวเรียนสอนเป็นเจ้าหนี้กรรมการ_ถูกตัด_แล้วได้ผังค่าใช้จ่ายของบริษัทแทน_ไม่ว่าง()
    {
        // จำลองด่านวางฝั่งของ ScanAsync บนผังมาตรฐานจริง (ChartOfAccountTemplates): Dr 21230 (หนี้สิน) / Cr 11111 ⇒ ตัดเดบิต · ใบไม่มีกฎหมวดไหนตรง
        // ฝ่ายค้าน EC2 P2-1: เดิมเทสต์นี้ใช้ "53000" ที่แต่งขึ้น — บนผังจริงได้บัญชีคุม "54" ⇒ ตอนสร้างเอกสารเดบิตว่าง
        var tpl = Accounting.Services.ChartOfAccountTemplates.GetCommonAccounts();
        GlAccountCandidate Of(string code) { var t = tpl.Single(x => x.Code == code); return new GlAccountCandidate(t.Code, t.NameTh, t.Type); }
        var placement = OcrAccountPlacement.Check(Of("21230"), Of("11111"), debitIsSystemSuggested: true);
        Assert.Equal(OcrAccountPlacement.Verdict.DebitRejected, placement.Verdict);
        Assert.Null(placement.DebitCode);
        // บัญชีคุม = มีรหัสอื่นขึ้นต้นด้วยรหัสนี้ (แม่แบบผูกแม่ด้วยรหัสต้น) — ผู้ค้นจริงตัดด้วย WhereNotControlAccount
        var leaves = tpl.Where(t => !tpl.Any(o => o.Code != t.Code && o.Code.StartsWith(t.Code)))
            .Select(t => new GlAccountCandidate(t.Code, t.NameTh, t.Type)).ToList();
        GlAccountCandidate? picked = null;
        foreach (var fb in OcrDocTypeDefaultAccounts.DebitFallbackSeeds("Receipt"))
        {
            // รูปเดียวกับ PickCategorySeededDebitAsync: รหัสขึ้นต้นด้วย seed หรือชื่อมีคำเด่นของ seed · ไม่ใช่บัญชีคุม
            picked = GlDebitAccountPicker.Pick(leaves.Where(a => a.Code.StartsWith(fb.Code) || a.Name.Contains("บริหาร")), fb.Code);
            if (picked != null) break;
        }
        Assert.Equal(OcrDocTypeDefaultAccounts.GenericExpenseDebit.Code, picked?.Code);
    }

    // ── P2-C: ใบไม่มีบรรทัด — คีย์ตัวเรียนตามชนิดกระดาษ + อ่านแถวรุ่นเก่าครั้งเดียว ───────────────────────────────

    [Fact]
    public void คีย์ตัวเรียนของใบไม่มีบรรทัด_คือชนิดกระดาษ_ไม่ใช่ว่างและไม่ใช่ชื่อหมวด()
    {
        var k = OcrLearnerDescriptionKey.For(Array.Empty<string?>(), "Receipt");
        Assert.Equal("#ไม่มีรายการ#Receipt", k);
        Assert.StartsWith(OcrLearnerDescriptionKey.LinelessPrefix, k);
        Assert.NotEqual("ค่าบริการ", k);   // ไม่ใช่หมวดตั้งต้นของใบเสร็จ (บั๊กเดิม — ชื่อหมวดเป็นคีย์)
        // มีบรรทัด ⇒ คำอธิบายบรรทัดแรกเหมือนเดิม (ไม่แตะชั้น "ผู้ขาย+คำอธิบาย" ของใบที่มีบรรทัด)
        Assert.Equal("ค่าซ่อมแอร์", OcrLearnerDescriptionKey.For(new[] { " ค่าซ่อมแอร์ " }, "Receipt"));
        // NaiveBayes ใช้รูปไม่มีชนิดกระดาษ (ฝึกจากบรรทัดเอกสารจริง) — ว่างเหมือนเดิม
        Assert.Equal("", OcrLearnerDescriptionKey.For(Array.Empty<string?>()));
    }

    [Fact]
    public async Task ตัวทำนาย_อ่านแถวรุ่นเก่าเฉพาะใบไม่มีบรรทัดที่คีย์ใหม่ไม่ถึงเกณฑ์()
    {
        // ตัวเรียนจำลอง: แถวรุ่นเก่าคีย์ "ค่าบริการ" (Tier 1 = 0.667) · คีย์อื่นตกชั้นผู้ขายอย่างเดียว 0.433
        var calls = new List<string>();
        Task<(string?, string?, decimal)> Predict(string key)
        {
            calls.Add(key);
            return Task.FromResult<(string?, string?, decimal)>(key == "ค่าบริการ" ? ("51400", "ค่ารับรอง", 0.667m) : ("51400", "ค่ารับรอง", 0.433m));
        }
        var r = await OcrLearnerDescriptionKey.PredictAsync(Predict, Array.Empty<string?>(), "Receipt", 0.55m);
        Assert.True(r.FromLegacyRow);
        Assert.Equal(0.667m, r.Confidence);
        Assert.Equal(new[] { "#ไม่มีรายการ#Receipt", "ค่าบริการ" }, calls);

        // ทิศตรงข้าม 1: ใบที่มีบรรทัด ห้ามอ่านคีย์รุ่นเก่า (ชื่อหมวดไม่ใช่หลักฐานของใบที่มีบรรทัด)
        calls.Clear();
        var withLines = await OcrLearnerDescriptionKey.PredictAsync(Predict, new[] { "น้ำดื่ม" }, "Receipt", 0.55m);
        Assert.False(withLines.FromLegacyRow);
        Assert.Equal(new[] { "น้ำดื่ม" }, calls);

        // ทิศตรงข้าม 2: คีย์ใหม่ถึงเกณฑ์แล้ว ⇒ ไม่อ่านแถวเก่า (ผู้ใช้สอนด้วยคีย์ใหม่แล้วชนะเสมอ)
        calls.Clear();
        Task<(string?, string?, decimal)> PredictNew(string key)
        {
            calls.Add(key);
            return Task.FromResult<(string?, string?, decimal)>(("53000", "ค่าใช้จ่ายบริหาร", 0.667m));
        }
        var fresh = await OcrLearnerDescriptionKey.PredictAsync(PredictNew, null, "Receipt", 0.55m);
        Assert.False(fresh.FromLegacyRow);
        Assert.Single(calls);
    }

    [Fact]
    [Trait("Category", "Db")]
    public async Task ตัวเรียนผัง_ใบไม่มีบรรทัด_สอนครั้งเดียวแล้วใบถัดไปได้ชั้นผู้ขายบวกคีย์()
    {
        using var db = Accounting.Tests.Db.DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานคือ job db-test"); return; }
        var company = new Accounting.Models.Entities.Company { Name = "บริษัททดสอบใบไม่มีรายการ", TaxId = "0105556000025" };
        var legacyCompany = new Accounting.Models.Entities.Company { Name = "บริษัททดสอบใบไม่มีรายการ (แถวเก่า)", TaxId = "0105556000017" };
        db.Companies.AddRange(company, legacyCompany);
        await db.SaveChangesAsync();
        var learner = new ExpenseCategoryLearner(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ExpenseCategoryLearner>.Instance);
        Func<string, Task<(string?, string?, decimal)>> PredictFor(Guid cid)
            => key => learner.PredictAsync(cid, "0105556000025", "ร้านทดสอบ", key);

        // ฝั่งเขียน (SubmitCorrectionAsync): ใบไม่มีบรรทัด ชนิดกระดาษก่อนแก้ = Receipt
        await learner.RecordAsync(company.Id, "0105556000025", "ร้านทดสอบ",
            OcrLearnerDescriptionKey.For(Array.Empty<string?>(), "Receipt"), "51400", "ค่ารับรอง");
        var r = await OcrLearnerDescriptionKey.PredictAsync(PredictFor(company.Id), Array.Empty<string?>(), "Receipt", 0.55m);
        _out.WriteLine($"lineless-key conf={r.Confidence}");
        Assert.Equal("51400", r.Code);
        Assert.True(r.Confidence >= 0.55m, $"ต้องผ่านเกณฑ์ทับ 0.55 ตั้งแต่สอนครั้งแรก (ได้ {r.Confidence})");
        Assert.False(r.FromLegacyRow);

        // แถวรุ่นก่อน 2026-10-16 (คีย์ = หมวดตั้งต้นของใบเสร็จ) ยังให้ผลเท่าเดิม — อ่านครั้งเดียว ไม่เขียนคีย์เก่าเพิ่ม
        await learner.RecordAsync(legacyCompany.Id, "0105556000025", "ร้านทดสอบ", "ค่าบริการ", "51400", "ค่ารับรอง");
        var legacy = await OcrLearnerDescriptionKey.PredictAsync(PredictFor(legacyCompany.Id), Array.Empty<string?>(), "Receipt", 0.55m);
        _out.WriteLine($"legacy-row conf={legacy.Confidence}");
        Assert.True(legacy.FromLegacyRow);
        Assert.True(legacy.Confidence >= 0.55m, $"แถวเก่าต้องไม่ตกชั้นผู้ขายอย่างเดียว (ได้ {legacy.Confidence})");
    }

    // ── P2-D: ไฮไลต์ช่องผังค่าใช้จ่ายของฟอร์มเอกสาร ผูกกับความมั่นใจของผังเดบิต ────────────────────────────────

    [Fact]
    public void ฟอร์มเอกสาร_ไฮไลต์ช่องผังค่าใช้จ่ายตามความมั่นใจของผังเดบิตที่เซิร์ฟเวอร์ส่ง()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/wwwroot/pages/documents.html"));
        Assert.Contains($"'{OcrFieldKeys.DebitAccount}': 'fExpenseCategory'", page);
        Assert.DoesNotContain("'ExpenseCategory': 'fExpenseCategory'", page);
        // ฝั่งเซิร์ฟเวอร์เขียนความมั่นใจของผังเดบิตด้วยคีย์นี้จริง (ตัวจัดหมวดเติมผังให้สแกนที่ยังไม่มีผัง)
        var data = ResolveLikePipeline(NeutralVendor, new[] { ("ค่าซ่อมแอร์ ห้องประชุม", 5000m) }, Receipt("ค่าซ่อมแอร์ ห้องประชุม", 5000m));
        Assert.True(data.FieldConfidence.ContainsKey(OcrFieldKeys.DebitAccount));
    }

    [Fact]
    public void ชุดใบซื้อของในคลัง_มีครบทุกชื่อ()
    {
        // กันชื่อใน corpus เปลี่ยนแล้ว Theory ข้างบนเหลือศูนย์แถวเงียบ ๆ
        Assert.Equal(GoodsPaperNames.Count, GoodsPapers().Count());
    }

    /// <summary>บรรทัดของใบตามไปป์ไลน์: engine คืนบรรทัด (<c>Lines</c> ของ corpus) หรือ — เส้น Tesseract ที่ไม่มีบรรทัด — ตัวแตกบรรทัดจากข้อความ
    /// (<c>TrySplitLineItemsAsync</c> ทางไม่มีโมเดล: <see cref="RawTextLineSplitter"/> + <see cref="OcrLineSplitGuard"/> เทียบยอดหัวใบ)</summary>
    private static List<(string Desc, decimal Amount)> PipelineLines(ReplayPaper p, OcrExtractedData parsed)
    {
        if (p.Lines != null) return p.Lines.Select(l => (l.Description ?? "", l.Amount)).ToList();
        var sub = p.EngineSubTotal ?? parsed.SubTotal ?? 0m;
        var total = p.EngineTotal ?? parsed.TotalAmount ?? 0m;
        if (sub <= 0m && total <= 0m) return new();
        var json = RawTextLineSplitter.SplitToJson(p.RawText);
        if (string.IsNullOrWhiteSpace(json)) return new();
        var guard = OcrLineSplitGuard.Evaluate(json, sub, total);
        return guard.Accepted
            ? guard.Lines.Where(l => !OcrNonItemRow.IsSummaryOrTenderRow(l.Description))
                .Select(l => (l.Description ?? "", l.Amount)).ToList()
            : new();
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }
}
