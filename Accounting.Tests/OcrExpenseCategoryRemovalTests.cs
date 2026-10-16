using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Accounting.Tests.Db;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests;

/// <summary>
/// **ถอดช่อง "หมวดค่าใช้จ่าย" ออกจากหน้ารีวิวสแกน** (คำตัดสินเจ้าของ 2026-10-16 "ถอดออกเลย") — ผังบัญชีเดบิตคือการตัดสินใจเดียว
///
/// <para>หมวดเป็นข้อความที่ไม่มีผู้อ่านเชิงธุรกิจ (§82/5 ตัดสินจากกระดาษ · §65 ตรี จากผัง · เอกสารรับ <c>ExpenseCategoryId</c> จากผังเดบิต)
/// แต่มีผู้อ่าน<b>ที่ก่อความเสียหาย</b>: (1) ค่าตั้งต้นตามชนิดกระดาษ (ใบเสร็จ ⇒ "ค่าบริการ") ถูกป้อนตัวจัดหมวดเป็นหลักฐานผูกกับเงิน
/// ⇒ เสนอหัก ณ ที่จ่าย 3% 40(8) บนใบเสร็จซื้อของ · (2) ตัวเรียนผังบันทึกด้วยชื่อหมวด แต่ทำนายด้วยคำอธิบายบรรทัด ⇒ คีย์ไม่เคยตรง</para>
///
/// <para>สองทิศตามกฎเหล็ก #4 H: ใบที่พังกลับมาถูก (ใบเสร็จซื้อของไม่ได้ข้อเสนอ 3%) และใบที่ถูกอยู่แล้วไม่ถูกแตะ
/// (ใบค่าบริการจริงยังได้ข้อเสนอ 3% · ผังเดบิตตั้งต้นของใบเสร็จยังอยู่)</para>
/// </summary>
public class OcrExpenseCategoryRemovalTests
{
    private readonly ITestOutputHelper _out;
    public OcrExpenseCategoryRemovalTests(ITestOutputHelper output) => _out = output;

    /// <summary>ใบเสร็จร้านสะดวกซื้อ — ซื้อของล้วน ไม่มีบริการ (เส้น Tesseract/PDF text-layer ผ่าน ParseThaiDocument)</summary>
    private const string GoodsReceiptText =
        "ร้านสะดวกซื้อ ตัวอย่างพาณิชย์\n" +
        "ใบเสร็จรับเงิน\n" +
        "เลขที่ R-2026-0001\n" +
        "วันที่ 01/10/2026\n" +
        "น้ำดื่ม 600 มล. 24 x 10.00 240.00\n" +
        "ขนมปังโฮลวีท 20 x 45.00 900.00\n" +
        "รวมทั้งสิ้น 1,140.00\n";

    /// <summary>เรียกตัวจัดหมวดด้วยอาร์กิวเมนต์รูปเดียวกับ ScanAsync (headerDescription = null · ล็อกจุดเรียกใน tools/required_call_site_check.py)</summary>
    private static OcrExtractedData ResolveLikePipeline(OcrExtractedData data, string rawText, string? headerDescription = null)
    {
        var text = ThaiTextNormalizer.Normalize(rawText);
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: data.VendorName,
            headerDescription: headerDescription,
            lineDescriptions: data.Items.Select(i => i.Description).ToList(),
            rawText: text);
        if (result != null) ExpenseCategoryResolver.ApplyTo(data, result, text);
        return data;
    }

    // ── ครึ่งที่ 1: ใบที่พังต้องกลับมาถูก ──────────────────────────────────

    [Fact]
    public void ใบเสร็จซื้อของ_ไม่ได้หมวดค่าบริการตั้งต้น_และไม่ได้ข้อเสนอหักณที่จ่าย3เปอร์เซ็นต์()
    {
        var data = OcrService.ParseThaiDocument(GoodsReceiptText);
        Assert.Equal("Receipt", data.DocumentType);
        // ค่าตั้งต้นตามชนิดกระดาษ "ค่าบริการ" ถูกถอดที่ต้นทาง
        Assert.Null(data.ExpenseCategory);

        ResolveLikePipeline(data, GoodsReceiptText);
        _out.WriteLine($"category={data.ExpenseCategory ?? "null"} suggestedWht={data.SuggestedWhtRate?.ToString() ?? "null"} income={data.WhtIncomeTypeCode ?? "null"}");
        Assert.Null(data.SuggestedWhtRate);
        Assert.NotEqual("8", data.WhtIncomeTypeCode);
        Assert.False(data.HasWht);
    }

    [Fact]
    public void หลักฐานบั๊กเดิม_ชื่อหมวดที่ป้อนเป็นหัวเรื่อง_ถูกนับเป็นหลักฐานผูกกับเงิน_ผู้เรียกจึงต้องส่งเฉพาะข้อความกระดาษ()
    {
        // ตัวจัดหมวดเชื่อ headerDescription เต็มน้ำหนัก (ถูกต้องสำหรับหัวเรื่องบนกระดาษ) — ป้อนชื่อหมวดที่ระบบเดาเอง ("ค่าบริการ")
        // ⇒ กฎ ค่าบริการ 40(8) ชนะด้วยคะแนน 2 (มั่นใจ 0.60 = ผ่านเกณฑ์ 0.6 พอดี) แล้วเสนอ 3% บนใบที่ซื้อน้ำดื่ม/ขนมปัง
        // นี่คือพฤติกรรมของ ScanAsync ก่อน 2026-10-16 (ค่าตั้งต้นของ ParseThaiDocument + python ocr-service) — ล็อกไว้ว่า
        // "ห้ามป้อนชื่อหมวดเป็นหัวเรื่อง" ไม่ใช่เรื่องรสนิยม แต่เปลี่ยนข้อเสนอภาษีจริง
        var data = OcrService.ParseThaiDocument(GoodsReceiptText);
        ResolveLikePipeline(data, GoodsReceiptText, headerDescription: "ค่าบริการ");
        Assert.Equal(3m, data.SuggestedWhtRate);
        Assert.Equal("8", data.WhtIncomeTypeCode);
    }

    // ── ครึ่งที่ 2: ใบที่ถูกอยู่แล้วไม่ถูกแตะ ───────────────────────────────

    [Fact]
    public void ใบค่าบริการจริง_ยังได้ข้อเสนอหักณที่จ่าย3เปอร์เซ็นต์จากบรรทัดที่มีเงิน()
    {
        const string text =
            "บริษัท ตัวอย่างเซอร์วิส จำกัด\n" +
            "ใบเสร็จรับเงิน\n" +
            "เลขที่ SV-001\n" +
            "วันที่ 01/10/2026\n" +
            "ค่าบริการรายเดือน ตุลาคม 2569 1 x 5,000.00 5,000.00\n" +
            "รวมทั้งสิ้น 5,000.00\n";
        var data = OcrService.ParseThaiDocument(text);
        if (data.Items.Count == 0)
            data.Items.Add(new OcrExtractedLineItem { Description = "ค่าบริการรายเดือน ตุลาคม 2569", Quantity = 1, UnitPrice = 5000m, Amount = 5000m });
        ResolveLikePipeline(data, text);
        _out.WriteLine($"category={data.ExpenseCategory ?? "null"} suggestedWht={data.SuggestedWhtRate?.ToString() ?? "null"}");
        Assert.Equal(3m, data.SuggestedWhtRate);
        Assert.Equal("8", data.WhtIncomeTypeCode);
    }

    [Fact]
    public void ใบเสร็จยังได้ผังเดบิตตั้งต้นจากชนิดกระดาษ_ถอดเฉพาะหมวด()
    {
        // ผังเดบิตคือการตัดสินใจเดียว — กฎเหล็ก #3 ห้ามปล่อยว่าง: ค่าตั้งต้นตามชนิดกระดาษต้องยังอยู่ (เส้นสร้างเอกสารตั้ง
        // Document.ExpenseCategoryId = ผังเดบิตของสแกน · หน้ารีวิวส่ง expenseCategoryCode = ผังเดบิต)
        // ฝ่ายค้าน EC2 P2-1: รหัสตั้งต้นเป็นบัญชีย่อยจริงในผังมาตรฐาน (เดิม "5300" ไม่มีในแม่แบบ ⇒ ค้นตามชื่อได้บัญชีคุม "54")
        var expected = OcrDocTypeDefaultAccounts.GenericExpenseDebit.Code;
        var data = OcrService.ParseThaiDocument(GoodsReceiptText);
        Assert.Equal(expected, data.DebitAccountCode);
        Assert.Equal("1110", data.CreditAccountCode);
        ResolveLikePipeline(data, GoodsReceiptText);
        Assert.Equal(expected, data.DebitAccountCode);   // ตัวจัดหมวดเติมเฉพาะช่องที่ว่าง — ไม่ทับ
    }

    // ── ต้นเหตุ "ค่าประกัน" บนใบที่ไม่ใช่ประกัน (ทำซ้ำได้ใน resolver · เส้น Azure ที่ยังไม่มีหมวด/ผังมาก่อน) ──────

    [Theory]
    [InlineData("บริษัท ไอที ช็อป จำกัด", "เมาส์ไร้สาย|คีย์บอร์ด", "ใบกำกับภาษี\nเมาส์ไร้สาย 1 450.00\nคีย์บอร์ด 1 650.00\nสินค้ารับประกัน 1 ปี")]
    [InlineData("ร้านวัสดุ", "สายไฟ|ปลั๊ก", "ใบเสร็จรับเงิน\nสายไฟ 1 100\nปลั๊ก 1 50\nPremium Quality")]
    [InlineData("ร้านกาแฟ", "กาแฟ", "ใบเสร็จ\nกาแฟ 2 100\nสมาชิกประกันสังคม")]
    public void คำรับประกันสินค้า_ประกันสังคม_premium_ในข้อความท้ายใบ_ไม่ทำให้ได้หมวดและผังค่าประกัน(string vendor, string lines, string raw)
    {
        // ก่อนแก้: ทั้งสามใบได้ "ค่าประกัน" 5800 มั่นใจ 0.50 จากคำเดี่ยว "ประกัน"/"premium" ในชั้นหลักฐานอ่อน (rawText) แล้ว ApplyTo
        // เติมผังเดบิต 5800 ให้สแกนที่ยังไม่มีผัง
        var data = new OcrExtractedData { VendorName = vendor };
        foreach (var l in lines.Split('|')) data.Items.Add(new OcrExtractedLineItem { Description = l, Amount = 100m });
        ResolveLikePipeline(data, raw);
        Assert.NotEqual("ค่าประกัน", data.ExpenseCategory);
        Assert.NotEqual("5800", data.DebitAccountCode);
    }

    [Theory]
    [InlineData("บริษัท ตัวแทนประกัน จำกัด", "เบี้ยประกันภัยรถยนต์ ชั้น 1")]
    [InlineData("บริษัท เมืองไทยประกันภัย จำกัด (มหาชน)", "ค่าเบี้ย")]
    [InlineData("บริษัท ก จำกัด", "ค่ากรมธรรม์ประกันอัคคีภัย")]
    public void ใบเบี้ยประกันจริง_ยังได้หมวดและผังค่าประกัน(string vendor, string line)
    {
        var data = new OcrExtractedData { VendorName = vendor };
        data.Items.Add(new OcrExtractedLineItem { Description = line, Amount = 12000m });
        ResolveLikePipeline(data, "ใบเสร็จรับเงิน\n" + line + " 12,000.00");
        Assert.Equal("ค่าประกัน", data.ExpenseCategory);
        Assert.Equal("5800", data.DebitAccountCode);
    }

    // ── หน้าเว็บ: ช่องถูกถอดจริง และผังเดบิตยังไหลไปฟอร์มเอกสาร ─────────────

    [Fact]
    public void หน้ารีวิวสแกน_ไม่มีช่องหมวด_ไม่ส่งหมวด_และส่งผังเดบิตเป็นหมวดของเอกสาร()
    {
        var root = RepoRoot();
        var page = File.ReadAllText(Path.Combine(root, "Accounting/wwwroot/pages/document-scan.html"));
        Assert.DoesNotContain("revExpenseCategory", page);
        Assert.DoesNotContain("_mapCategoryToAccountCode", page);
        Assert.DoesNotContain("_mapAccountCodeToCategory", page);
        Assert.DoesNotContain("expenseCategory: v(", page);
        // ทิศตรงข้าม: ผังเดบิตที่ผู้ใช้/ระบบเลือก ยังเป็นค่าที่ส่งไปฟอร์มเอกสาร (Document.ExpenseCategoryId)
        Assert.Contains("expenseCategoryCode: reviewedDebitAcc || suggestedDebitCode || null", page);
        Assert.Contains("debitAccountCode: v('revDebitAccount')", page);

        var mobile = File.ReadAllText(Path.Combine(root, "Accounting/wwwroot/mobile-expense.html"));
        Assert.DoesNotContain("expenseCategory:", mobile);
        Assert.DoesNotContain("id=\"category\"", mobile);
    }

    [Fact]
    public void คำแก้ผลสแกน_ไม่มีช่องหมวดค่าใช้จ่าย_ตัวแปลงJSONทิ้งช่องที่clientรุ่นเก่าส่งมา()
    {
        Assert.Null(typeof(Accounting.Models.DTOs.Ocr.OcrCorrectionRequest).GetProperty("ExpenseCategory"));
        var req = System.Text.Json.JsonSerializer.Deserialize<Accounting.Models.DTOs.Ocr.OcrCorrectionRequest>(
            "{\"expenseCategory\":\"ค่าประกัน\",\"debitAccountCode\":\"53000\"}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("53000", req.DebitAccountCode);
        Assert.DoesNotContain("ExpenseCategory", OcrCorrectedFieldList.From(req));
    }

    // ── คีย์ตัวเรียน: ฝั่งเขียน = ฝั่งอ่าน ────────────────────────────────

    [Fact]
    public void คีย์ตัวเรียน_คือคำอธิบายบรรทัดแรกที่มีข้อความ_ไม่ใช่ชื่อหมวด()
    {
        Assert.Equal("น้ำดื่ม 600 มล.", OcrLearnerDescriptionKey.For(new[] { null, "  ", " น้ำดื่ม 600 มล. ", "ขนมปัง" }));
        Assert.Equal("", OcrLearnerDescriptionKey.For(Array.Empty<string?>()));
        Assert.Equal("", OcrLearnerDescriptionKey.For(null));
        // ฝั่งเขียนอ่านบรรทัดจาก ExtractedItemsJson — รูปเดียวกับที่ไปป์ไลน์ serialize ⇒ คีย์เดียวกับฝั่งทำนาย
        var items = new List<OcrExtractedLineItem>
        {
            new() { Description = "น้ำดื่ม 600 มล.", Amount = 240m },
            new() { Description = "ขนมปังโฮลวีท", Amount = 900m },
        };
        var json = OcrService.SerializeExtractedItems(items);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<List<OcrExtractedLineItem>>(json)!;
        Assert.Equal(OcrLearnerDescriptionKey.For(items.Select(i => i.Description)),
            OcrLearnerDescriptionKey.For(roundTrip.Select(i => i.Description)));
    }

    [Fact]
    [Trait("Category", "Db")]
    public async Task ตัวเรียนผัง_บันทึกด้วยคีย์คำอธิบาย_แล้วทำนายเจอชั้นผู้ขายบวกคำอธิบาย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานคือ job db-test"); return; }
        var company = new Accounting.Models.Entities.Company { Name = "บริษัททดสอบตัวเรียนผัง", TaxId = "0105556000025" };
        var otherCompany = new Accounting.Models.Entities.Company { Name = "บริษัททดสอบตัวเรียนผัง (คีย์เดิม)", TaxId = "0105556000017" };
        db.Companies.AddRange(company, otherCompany);
        await db.SaveChangesAsync();
        var companyId = company.Id;
        var learner = new ExpenseCategoryLearner(db, NullLogger<ExpenseCategoryLearner>.Instance);
        var lines = new[] { "น้ำดื่ม 600 มล.", "ขนมปังโฮลวีท" };
        var key = OcrLearnerDescriptionKey.For(lines);

        await learner.RecordAsync(companyId, "0105556000025", "ร้านทดสอบ", key, "51400", "ค่ารับรอง");
        var (code, _, conf) = await learner.PredictAsync(companyId, "0105556000025", "ร้านทดสอบ", OcrLearnerDescriptionKey.For(lines));
        _out.WriteLine($"exact-key conf={conf}");
        Assert.Equal("51400", code);
        // ชั้น 1 (ผู้ขาย+คำอธิบายตรง): 1 ครั้ง ⇒ 0.333 × โบนัส 2.0 = 0.667 · ชั้นผู้ขายอย่างเดียว = 0.333 × 0.65 × 2 = 0.433
        Assert.True(conf > 0.6m, $"ต้องเจอชั้นผู้ขาย+คำอธิบาย (ได้ {conf})");

        // ทิศเดิม (บั๊ก): บันทึกด้วยชื่อหมวด แล้วทำนายด้วยคำอธิบาย ⇒ ได้แค่ชั้นผู้ขายอย่างเดียว
        var other = otherCompany.Id;
        await learner.RecordAsync(other, "0105556000025", "ร้านทดสอบ", "ค่าบริการ", "51400", "ค่ารับรอง");
        var (code2, _, conf2) = await learner.PredictAsync(other, "0105556000025", "ร้านทดสอบ", OcrLearnerDescriptionKey.For(lines));
        _out.WriteLine($"category-key conf={conf2}");
        Assert.Equal("51400", code2);
        Assert.True(conf2 < 0.6m, $"คีย์ชื่อหมวดต้องไม่ตรงชั้น 1 (ได้ {conf2})");
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }
}
