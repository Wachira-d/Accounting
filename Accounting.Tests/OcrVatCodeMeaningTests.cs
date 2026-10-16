using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Distillation;
using Accounting.Services.Ai.Prompts;
using Accounting.Services.Implementations.Ocr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำตัดสินเจ้าของ 2026-10-16 (DECISIONS ข้อ 153 ข้อ 2 "ให้โยนไปให้ AI เลย และเอามาสอนระบบอีกที") — ความหมายของรหัส ภ.พ. ที่กระดาษไม่บอก
/// (<see cref="OcrVatCodeMeaning"/> · <see cref="OcrVatCodeMeaningPrompt"/> · <c>OcrAiAugmenter.InferVatCodeMeaningAsync</c>) · สองทิศทุกข้อ ·
/// กระดาษจริง: ข้อความ Tesseract 5.3.4 ของ Makro 2/2 (คำอธิบายรหัส "1«สินค้าได้รับการยกเว้น…" ไม่มี "=")
/// </summary>
public class OcrVatCodeMeaningTests
{
    private static string Norm(string s) => ThaiTextNormalizer.Normalize(s);
    private static string Tesseract => Norm(OcrMakroPage2VatAddedTwiceTests.MakroPage2of2Tesseract);
    private static readonly Guid Cid = Guid.Parse("0a0a0a0a-0000-0000-0000-000000000153");

    private static OcrVatGroup ZeroRow(string code = "1") => new(OcrVatGroupKind.Unknown, code, 1220.00m, 0.00m, 1220.00m, 2, "รหัส 1 ไม่มีคำอธิบาย");
    private static OcrVatGroup SevenRow => new(OcrVatGroupKind.Unknown, "2", 9121.50m, 638.50m, 9760.00m, 59, "รหัส 2 ไม่มีคำอธิบาย");

    // ── ตัวเลขก่อน AI ──

    [Fact]
    public void Tesseractจริง_ถามเฉพาะรหัส1ที่VATศูนย์_รหัส2พิสูจน์ด้วยตัวเลข()
    {
        var table = OcrLineVatMarks.ReadGroups(Tesseract);
        Assert.False(table.AllKnown);
        var asks = OcrVatCodeMeaning.Ambiguous(table);
        Assert.Equal(new[] { "1" }, asks.Select(g => g.PaperCode).ToArray());
        Assert.Contains("สินค้าได้รับการยกเว้น", OcrVatCodeMeaning.LegendText(Tesseract));     // หลักฐานประกอบที่ส่งครู (อ่านเพี้ยนก็ส่ง)
    }

    [Fact]
    public void รหัสที่VATเท่า7เปอร์เซ็นต์ของฐาน_ตัวเลขตอบเองไม่ถาม_รหัสที่VATขัดตัวเลข_คงไม่รู้()
    {
        var table = new OcrVatGroupTable(new[]
        {
            ZeroRow(), SevenRow,
            new OcrVatGroup(OcrVatGroupKind.Unknown, "4", 100.00m, 3.00m, 103.00m, 1, "รหัส 4"),
        }, 10441.50m, 641.50m, 11083.00m, 9, "t");
        Assert.Equal(new[] { "1" }, OcrVatCodeMeaning.Ambiguous(table).Select(g => g.PaperCode).ToArray());
        var r = OcrVatCodeMeaning.Resolve(table, null);
        Assert.Equal(new[] { OcrVatGroupKind.Exempt, OcrVatGroupKind.Standard7, OcrVatGroupKind.Unknown },
            r.Table.Groups.Select(g => g.Kind).ToArray());
        Assert.Equal(OcrVatCodeMeaningSource.Arithmetic, r.Entries.Single(e => e.Code == "2").Source);
        Assert.Equal(OcrVatCodeMeaningSource.ColdStartDefault, r.Entries.Single(e => e.Code == "1").Source);
    }

    // ── ด่านกันมั่ว (เลขคณิตของแถว) ──

    [Theory]
    [InlineData("Exempt", OcrVatGroupKind.Exempt)]
    [InlineData("ZeroRated", OcrVatGroupKind.ZeroRated)]
    [InlineData(" exempt ", OcrVatGroupKind.Exempt)]
    public void ด่าน_แถวVATศูนย์_รับยกเว้นหรือศูนย์(string answer, OcrVatGroupKind expected)
        => Assert.Equal(expected, OcrVatCodeMeaning.Guard(ZeroRow(), answer, out _));

    [Theory]
    [InlineData("Standard7")]
    [InlineData("Taxable")]
    [InlineData("")]
    [InlineData(null)]
    public void ด่าน_ทิศตรงข้าม_7เปอร์เซ็นต์บนแถวVATศูนย์_และคำตอบนอกชุด_ทิ้ง(string? answer)
    {
        Assert.Null(OcrVatCodeMeaning.Guard(ZeroRow(), answer, out var why));
        Assert.NotEmpty(why);
    }

    [Fact]
    public void ด่าน_แถวที่มีVAT7เปอร์เซ็นต์_ตอบยกเว้นถูกทิ้ง_ตอบ7ผ่าน()
    {
        Assert.Null(OcrVatCodeMeaning.Guard(SevenRow, "Exempt", out var why));
        Assert.Contains("ขัดกับแถวที่มี VAT 638.50", why);
        Assert.Equal(OcrVatGroupKind.Standard7, OcrVatCodeMeaning.Guard(SevenRow, "Standard7", out _));
    }

    // ── เลือกคำตอบ: AI / นักเรียน / ค่าตั้งต้น ──

    [Fact]
    public void เลือก_AIมั่นใจ_ใช้คำตอบAI_ป้ายAIแนะนำ_เก็บFeedbackId()
    {
        var fid = Guid.NewGuid();
        var e = OcrVatCodeMeaning.Choose(ZeroRow(), "ZeroRated", 0.92m, hasModelAnswer: true, usedAi: true, fid);
        Assert.Equal((OcrVatGroupKind.ZeroRated, OcrVatCodeMeaningSource.Ai, fid), (e.Kind, e.Source, e.FeedbackId));
        Assert.StartsWith("🤖 AI แนะนำ", OcrVatCodeMeaning.SourceText(e));
    }

    [Fact]
    public void เลือก_ทิศตรงข้าม_ความมั่นใจต่ำ_คำตอบขัดตัวเลข_ไม่มีคำตอบ_ใช้ค่าตั้งต้นยกเว้น_แต่เก็บFeedbackIdและคำตอบดิบ()
    {
        var fid = Guid.NewGuid();
        foreach (var (ans, conf, has) in new[] { ("ZeroRated", 0.60m, true), ("Standard7", 0.99m, true), ((string)null!, 0m, false) })
        {
            var e = OcrVatCodeMeaning.Choose(ZeroRow(), ans, conf, has, usedAi: has, fid);
            Assert.Equal(OcrVatCodeMeaning.ColdStartKind, e.Kind);
            Assert.Equal(OcrVatGroupKind.Exempt, e.Kind);
            Assert.Equal(OcrVatCodeMeaningSource.ColdStartDefault, e.Source);
            Assert.True(e.Confidence < 0.85m);                                   // ไฮไลต์เหลือง (กฎเหล็ก #3 ข้อ 3)
            Assert.Equal(fid, e.FeedbackId);                                     // คำตอบที่ถูกทิ้งก็คือข้อมูลสอน
            Assert.Equal(has ? ans : null, e.AiAnswer);
            Assert.StartsWith("⚙️ ระบบแนะนำ", OcrVatCodeMeaning.SourceText(e));
        }
    }

    [Fact]
    public void ตัดสินซ้ำตอนสร้างบรรทัด_คำตอบที่persistผ่านด่านซ้ำ_ข้อมูลเสียกลับเป็นค่าตั้งต้น()
    {
        var table = OcrLineVatMarks.ReadGroups(Tesseract);
        var fid = Guid.NewGuid();
        var good = OcrVatCodeMeaning.Resolve(table, new[] { new OcrVatCodeMeaningEntry("1", OcrVatGroupKind.ZeroRated, OcrVatCodeMeaningSource.Ai, 0.9m, fid, "ZeroRated") });
        Assert.Equal(OcrVatGroupKind.ZeroRated, good.Table.Groups.Single(g => g.PaperCode == "1").Kind);
        Assert.True(good.Table.AllKnown);
        // persist ผิด (7% บนแถว VAT 0 · ความมั่นใจต่ำ) ⇒ ไม่เชื่อ ⇒ ค่าตั้งต้น (FeedbackId เดิมคงไว้ให้ปิดลูป)
        foreach (var bad in new[]
        {
            new OcrVatCodeMeaningEntry("1", OcrVatGroupKind.Standard7, OcrVatCodeMeaningSource.Ai, 0.9m, fid, "Standard7"),
            new OcrVatCodeMeaningEntry("1", OcrVatGroupKind.ZeroRated, OcrVatCodeMeaningSource.Ai, 0.5m, fid, "ZeroRated"),
        })
        {
            var r = OcrVatCodeMeaning.Resolve(table, new[] { bad });
            var e = r.Entries.Single();
            Assert.Equal((OcrVatGroupKind.Exempt, OcrVatCodeMeaningSource.ColdStartDefault, fid), (e.Kind, e.Source, e.FeedbackId));
        }
        var json = OcrVatCodeMeaning.ToJson(good.Entries);
        Assert.Contains("\"ZeroRated\"", json);                                   // enum เป็นชื่อ (F2 ข้อ 5)
        Assert.Equal(good.Entries, OcrVatCodeMeaning.FromJson(json));
        Assert.Empty(OcrVatCodeMeaning.FromJson("{พัง"));
    }

    [Fact]
    public void ตารางรู้ความหมายครบ_ไม่แตะ_ไม่มีข้อความ()
    {
        var table = OcrLineVatMarks.ReadGroups(Norm(OcrPaperSamples.MakroPage2of2));
        Assert.True(table.AllKnown);
        var r = OcrVatCodeMeaning.Resolve(table, null);
        Assert.Same(table, r.Table);
        Assert.Empty(r.Entries);
        Assert.Null(r.Note);
        Assert.Empty(OcrVatCodeMeaning.Ambiguous(table));
    }

    // ── ต่อกับตัวตัดสินบรรทัด: Tesseract จริงหน้า 2 ⇒ 1,220 ยกเว้น + 9,121.50/638.50 = 10,980 ──

    [Fact]
    public void Tesseractจริงหน้า2_ค่าตั้งต้นตัดสินรหัส1_บรรทัดสรุปต่อรหัส_รวม10980()
    {
        // ก่อน: รหัส 1 ไม่รู้ ⇒ ตารางใช้ไม่ได้ ⇒ บรรทัดสรุปใบเดียว 7% + [Σ-GAP] VatRateMismatch
        var resolved = OcrVatCodeMeaning.Resolve(OcrLineVatMarks.ReadGroups(Tesseract), null).Table;
        var d = OcrVatCodeLines.Decide(new[] { new OcrVatCodeLineInput(30m, null, "ซื้อสินค้าฮีโร่ … ลด 30 บาท(*1.0)"), new OcrVatCodeLineInput(60m, null, "ลด 60บ(*1.0)") },
            Tesseract, resolved, 10341.50m, 638.50m, 10980.00m, 228.00m, false, true);
        Assert.Equal(OcrVatCodeLineSource.TableSummary, d.Source);
        Assert.Equal(new[] { (OcrVatGroupKind.Exempt, 1220.00m, 0.00m), (OcrVatGroupKind.Standard7, 9121.50m, 638.50m) },
            d.TableLines.Select(g => (g.Kind, g.Net, g.Vat)).ToArray());
        Assert.Equal(10980.00m, d.TableLines.Sum(g => g.Net + g.Vat));
        // ไม่มีรายการ (หน้าสุดท้ายหน้าเดียว) — สาขาบรรทัดสรุปต่อกลุ่มเดิม ได้ตัวเลขชุดเดียวกัน
        var groups = OcrTotalDecomposer.SummaryGroupLines(
            OcrTotalDecomposer.Decompose(Tesseract, 10341.50m, 638.50m, 10980.00m, 228.00m) with { Groups = resolved },
            10980.00m, 638.50m, 10341.50m);
        Assert.Equal(new[] { 1220.00m, 9121.50m }, groups.Select(g => g.Net).ToArray());
        // ทิศตรงข้าม: ตารางที่ยังไม่ตัดสิน (เส้นเดิม) ไม่ได้บรรทัดสรุปต่อกลุ่ม
        Assert.Empty(OcrTotalDecomposer.SummaryGroupLines(
            OcrTotalDecomposer.Decompose(Tesseract, 10341.50m, 638.50m, 10980.00m, 228.00m), 10980.00m, 638.50m, 10341.50m));
    }

    [Fact]
    public void Tesseractจริงหน้า2_รายการลงตาราง_รหัส1ตัดสินแล้ว_แบ่งทางเดียว_ยกเว้น1220()
    {
        var resolved = OcrVatCodeMeaning.Resolve(OcrLineVatMarks.ReadGroups(Tesseract), null).Table;
        var plan = OcrVatCodeLines.Plan(new[] { new OcrVatCodeLineInput(1220.00m, null), new OcrVatCodeLineInput(9760.00m, null) },
            Tesseract, resolved, 638.50m, 10980.00m, 0m, true, null);
        Assert.Equal(OcrVatCodeVerdict.Applied, plan.Verdict);
        Assert.Equal(new decimal?[] { -1m, 7m }, plan.Rates);
    }

    // ── ปิดลูปตอนอนุมัติ ──

    [Fact]
    public void คำตอบจริงตอนอนุมัติ_ชนิดของบรรทัดไม่มีVAT_ปนหรือไม่มีไม่บันทึก()
    {
        Assert.Equal(OcrVatGroupKind.Exempt, OcrVatCodeMeaning.ChosenNoVatKind(new[] { -1m, 7m, -1m }));
        Assert.Equal(OcrVatGroupKind.ZeroRated, OcrVatCodeMeaning.ChosenNoVatKind(new[] { 0m, 7m }));
        Assert.Null(OcrVatCodeMeaning.ChosenNoVatKind(new[] { 0m, -1m, 7m }));           // ปน ⇒ ไม่รู้ว่าผู้ใช้ตอบอะไร
        Assert.Null(OcrVatCodeMeaning.ChosenNoVatKind(new[] { 7m }));                     // ผู้ใช้รวมเป็น 7% ทั้งใบ ⇒ ไม่ใช่คำตอบของคำถามนี้
    }

    // ── กุญแจนักเรียน = ผู้ขาย + รหัส (คำอธิบายที่อ่านเพี้ยน/ยอดเงินไม่เข้ากุญแจ) ──

    private static string KeyOf(string? name, string? tax, string code, decimal net, string? legend)
        => AiMemoryKey.Of(OcrVatCodeMeaningPrompt.Build(Cid, Guid.NewGuid(), name, tax, code, net, 0m, net, 2, legend).UserPromptJson);

    [Fact]
    public void กุญแจ_ผู้ขายเดิมรหัสเดิม_คำอธิบายเพี้ยนต่างกันยอดต่างกัน_คำถามเดียวกัน()
        => Assert.Equal(KeyOf("ซีพี แอ็กซ์ตร้า", "0107567000414", "1", 1220m, "1«สินค้าได้รับการยกเว้นภาษิมูลคําเพิ่ม"),
            KeyOf("ซีพี แอ๊กซ์ตร้า จํากัด", "0107567000414", "1", 480m, "1 สินค้าได้รับการยกเว้น"));

    [Fact]
    public void กุญแจ_ทิศตรงข้าม_รหัสต่างหรือผู้ขายต่าง_คนละคำถาม()
    {
        var k = KeyOf("ซีพี", "0107567000414", "1", 1220m, null);
        Assert.NotEqual(k, KeyOf("ซีพี", "0107567000414", "3", 1220m, null));
        Assert.NotEqual(k, KeyOf("ซีพี", "0105556123456", "1", 1220m, null));
        // ไม่มีเลขผู้เสียภาษี ⇒ กุญแจด้วยชื่อ (ไม่ชนกับผู้ขายที่มีเลข)
        Assert.NotEqual(k, KeyOf("ซีพี", null, "1", 1220m, null));
    }

    [Fact]
    public void กุญแจ_คีย์อื่นที่ไม่ใช่paper_evidence_ยังเข้ากุญแจ_ลิสต์ปิด()
    {
        Assert.NotEqual(AiMemoryKey.Of("{\"q\":\"a\",\"paper_evidence_x\":\"x\"}"), AiMemoryKey.Of("{\"q\":\"a\",\"paper_evidence_x\":\"y\"}"));
        Assert.Equal(AiMemoryKey.Of("{\"q\":\"a\",\"paper_evidence\":\"x\"}"), AiMemoryKey.Of("{\"q\":\"a\",\"paper_evidence\":\"y\"}"));
    }

    // ── kill-switch + cold start ผ่าน orchestrator ตัวจริง ──

    private static List<AiProviderConfig> AllProvidersOff() => new()
    {
        new() { ProviderType = AiProviderType.DeepSeek, IsActive = false, IsEnabled = true },
        new() { ProviderType = AiProviderType.OpenAi, IsActive = false, IsEnabled = true },
    };

    private sealed class ConfidentStudent : ILocalDistillationModel
    {
        private readonly string _answer; private readonly decimal _conf;
        public ConfidentStudent(string answer, decimal conf) { _answer = answer; _conf = conf; }
        public AiFeatureKey FeatureKey => AiFeatureKey.OcrVatCodeMeaning;
        public string Version => "fake";
        public bool IsReady => true;
        public Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct) => Task.CompletedTask;
        public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
            => Task.FromResult<LocalPrediction?>(new LocalPrediction(_answer, _conf, Array.Empty<string>(), 4, "fake"));
    }

    private static OcrAiAugmenter Augmenter(IEnumerable<ILocalDistillationModel> students)
        => new(new Accounting.Data.AccountingDbContext(new DbContextOptionsBuilder<Accounting.Data.AccountingDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none").Options),
            new KillSwitchRig.Orchestrator(students, AllProvidersOff()), NullLogger<OcrAiAugmenter>.Instance);

    private static async Task<OcrVatCodeMeaningEntry> AskAndChoose(OcrAiAugmenter aug, OcrVatGroup row)
    {
        var ai = await aug.InferVatCodeMeaningAsync(Cid, Guid.NewGuid(), "ซีพี แอ็กซ์ตร้า", "0107567000414", row, "1«สินค้าได้รับการยกเว้น");
        return OcrVatCodeMeaning.Choose(row, ai.Answer, ai.Confidence, ai.HasModelAnswer, ai.UsedAi, ai.FeedbackId);
    }

    [Fact]
    public async Task KillSwitch_ปิดproviderทุกตัว_tenantใหม่ไม่มีนักเรียน_ค่าตั้งต้นยกเว้น_ไม่throw_มีFeedbackIdให้สอน()
    {
        var e = await AskAndChoose(Augmenter(Array.Empty<ILocalDistillationModel>()), ZeroRow());
        Assert.Equal(OcrVatGroupKind.Exempt, e.Kind);
        Assert.Equal(OcrVatCodeMeaningSource.ColdStartDefault, e.Source);
        Assert.NotNull(e.FeedbackId);                                               // ผู้ใช้อนุมัติแล้ว = สอนนักเรียนได้แม้ปิด AI
        // ตัวสร้างบรรทัดได้ตารางที่ใช้ได้ (cold start) ⇒ 1,220 ยกเว้น + 9,121.50/638.50
        var resolved = OcrVatCodeMeaning.Resolve(OcrLineVatMarks.ReadGroups(Tesseract), new[] { e }).Table;
        Assert.True(resolved.AllKnown);
        Assert.Equal(1220.00m, resolved.Groups.Where(g => g.Kind == OcrVatGroupKind.Exempt).Sum(g => g.Net));
    }

    [Fact]
    public async Task KillSwitch_ปิดprovider_นักเรียนที่เรียนผู้ขายนี้แล้วตอบ_ใช้คำตอบนักเรียน_ป้ายระบบแนะนำไม่ใช่AI()
    {
        var e = await AskAndChoose(Augmenter(new[] { new ConfidentStudent("ZeroRated", 0.80m) }), ZeroRow());
        Assert.Equal((OcrVatGroupKind.ZeroRated, OcrVatCodeMeaningSource.Student), (e.Kind, e.Source));
        Assert.DoesNotContain("🤖", OcrVatCodeMeaning.SourceText(e));
    }

    [Fact]
    public async Task KillSwitch_ทิศตรงข้าม_นักเรียนตอบ7เปอร์เซ็นต์บนแถวVATศูนย์_ถูกทิ้ง_ค่าตั้งต้น()
    {
        var aug = Augmenter(new[] { new ConfidentStudent("Standard7", 0.95m) });
        var ai = await aug.InferVatCodeMeaningAsync(Cid, Guid.NewGuid(), "ซีพี", "0107567000414", ZeroRow(), null);
        Assert.Null(ai.Answer);
        Assert.Contains("ถูกทิ้ง", ai.Reasoning);
        var e = OcrVatCodeMeaning.Choose(ZeroRow(), ai.Answer, ai.Confidence, ai.HasModelAnswer, ai.UsedAi, ai.FeedbackId);
        Assert.Equal(OcrVatCodeMeaningSource.ColdStartDefault, e.Source);
    }

    [Fact]
    public void ทะเบียนนักเรียน_มีนักเรียนของความหมายรหัสภพ()
    {
        Assert.Contains(AiFeatureKey.OcrVatCodeMeaning, DistillationModelRegistry.GenericFeatureKeys);
        Assert.Contains(AiFeatureKey.OcrVatCodeMeaning, KillSwitchRig.RegisteredFeatureKeys());
        Assert.False(DistillationModelRegistry.KnownGapsWithoutStudent.ContainsKey(AiFeatureKey.OcrVatCodeMeaning));
    }

    [Fact]
    public void ป้ายหน้ารีวิว_ข้อความจากเซิร์ฟเวอร์_AIกับระบบแยกกัน()
    {
        var ai = new OcrVatCodeMeaningEntry("1", OcrVatGroupKind.Exempt, OcrVatCodeMeaningSource.Ai, 0.9m, Guid.NewGuid(), "Exempt");
        var cold = new OcrVatCodeMeaningEntry("3", OcrVatGroupKind.Exempt, OcrVatCodeMeaningSource.ColdStartDefault, 0.5m, null, null);
        var note = OcrVatCodeMeaning.ReviewNote(new[] { ai, cold })!;
        Assert.Contains("รหัส 1 = ยกเว้น VAT — 🤖 AI แนะนำ", note);
        Assert.Contains("รหัส 3 = ยกเว้น VAT — ⚙️ ระบบแนะนำ (ค่าตั้งต้น", note);
        Assert.Null(OcrVatCodeMeaning.ReviewNote(Array.Empty<OcrVatCodeMeaningEntry>()));
    }

    // ── คำแนะนำ "VAT ÷ 0.07" ของ OcrAmountIntegrity อ้างตารางรหัส ภ.พ. เมื่อกระดาษมี ──

    [Fact]
    public void คำแนะนำอัตราVAT_กระดาษมีตารางรหัส_อ้างตัวเลขของตาราง_ไม่ประมาณจากVATหาร007()
    {
        // ใบ Makro 2/2 บรรทัดสรุปเดียว 7% (เส้นเดิมก่อนยึดยอด): เดิมแนะนำ "ยอดที่มี VAT ≈ 9,121.43 ⇒ ยกเว้น ≈ 2,086.57" (ฐานผิด = ตัวเลขที่ผู้ใช้ลงบัญชีจริง)
        var table = OcrLineVatMarks.ReadGroups(Norm(OcrPaperSamples.MakroPage2of2));
        var p = OcrAmountIntegrity.Check(new[] { new OcrPlannedLine(11208.00m, 7m, 638.50m) }, 638.50m, 11846.50m, vatCodeTable: table)
            .Problems.Single(x => x.Kind == OcrAmountIntegrityKind.VatRateMismatch);
        Assert.Contains("ตารางรหัส ภ.พ. บนกระดาษ: รหัส 1 ยกเว้น VAT 1,220.00 · รหัส 2 มี VAT ฐาน 9,121.50 VAT 638.50", p.Message);
        Assert.DoesNotContain("≈", p.Message);
        Assert.Equal(OcrAmountIntegrityKind.VatRateMismatch, OcrAmountIntegrity.KindOf(p.Message));   // คำขึ้นต้นเดิม (ผู้อ่านข้อความไม่พัง)
    }

    [Fact]
    public void คำแนะนำอัตราVAT_ทิศตรงข้าม_ไม่มีตารางหรือตารางกลุ่มเดียว7_สูตรเดิม()
    {
        var lines = new[] { new OcrPlannedLine(1500.00m, 7m, 70.00m) };
        foreach (var t in new[] { (OcrVatGroupTable?)null, OcrVatGroupTable.None, OcrLineVatMarks.ReadGroups(OcrPaperSamples.WinePro) })
        {
            var p = OcrAmountIntegrity.Check(lines, 70.00m, 1570.00m, vatCodeTable: t).Problems.Single(x => x.Kind == OcrAmountIntegrityKind.VatRateMismatch);
            Assert.Contains("VAT บนกระดาษบอกว่ายอดที่มี VAT ≈ 1,000.00", p.Message);
            Assert.DoesNotContain("ตารางรหัส ภ.พ. บนกระดาษ", p.Message);
        }
    }
}
