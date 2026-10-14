using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Distillation;
using Accounting.Services.Ai.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 205 ทีม A — จับคู่คอลัมน์ไฟล์นำเข้า (<see cref="ImportColumnMapper"/> · <see cref="ImportFieldSynonymSeed"/> · <see cref="ImportSourceSeed"/> ·
/// <see cref="ImportHeaderSignature"/> · <see cref="ImportSchemas"/>) + นักเรียน ImportColumnMatch/ImportDataReview + ด่านคำตอบ AI (กฎเหล็ก #1)
///
/// <para>⚠️ ติด <c>Category=Db</c> โดยตั้งใจ ทั้งที่<b>ไม่แตะฐานข้อมูล</b>: CI รันเฉพาะเทสต์กลุ่ม Db (เทสต์อื่นแค่คอมไพล์) ⇒ พฤติกรรมหลักของตัวจับคู่
/// ต้องอยู่กลุ่มนี้จึงจะถูก "รันจริง" (คำสั่งร่วมของรอบ 205) · ไม่มีฐาน = ยังรันได้ครบ</para>
///
/// <para>ทุกกลุ่มล็อกสองทิศ: ไฟล์ที่ต้องจับคู่ได้ (PEAK/FlowAccount/Express/ธนาคาร/ลูกหนี้ยกมา — AI ปิด) กับสิ่งที่ต้อง<b>ไม่</b>เกิด
/// (สองคอลัมน์ได้ field เดียว · AI ตอบนอก schema · AI ทับชั้นที่สูงกว่า · ป้าย AI ทั้งที่ไม่ได้เรียก)</para>
/// </summary>
[Trait("Category", "Db")]
public class ImportColumnMappingDbTests
{
    private static ImportColumnInput Col(int i, string header, params string[] samples) => new(i, header, samples);

    private static ImportColumnPlan Map(string entity, IReadOnlyList<ImportColumnInput> cols,
        IReadOnlyList<ImportTemplateColumnDto>? template = null, IReadOnlyList<ImportColumnAiAnswer>? ai = null,
        IReadOnlyList<ImportColumnAssignment>? user = null, IReadOnlyList<ImportLearnedHeader>? learned = null,
        IReadOnlyList<ImportFieldDto>? targets = null)
        => ImportColumnMapper.Resolve(new ImportColumnMapRequest(entity, targets ?? ImportSchemas.For(entity)!, cols, template, learned, ai, user));

    private static ImportColumnPlanDto At(ImportColumnPlan p, int idx) => p.Columns.Single(c => c.SourceIndex == idx);

    // ═══ ไฟล์จากโปรแกรมอื่น — AI ปิด ต้องได้ field บังคับครบ ═══

    [Fact]
    public void PEAK_ผู้ติดต่อ_จับคู่ครบโดยไม่มี_AI_และรหัสระบบเดิมไม่ถูกลากไปช่องอื่น()
    {
        var p = Map("contacts", new[]
        {
            Col(0, "contactCode", "C0001", "C0002"), Col(1, "contactName", "บริษัท สยามพาณิชย์ จำกัด", "หจก. รวมมิตร"),
            Col(2, "taxNumber", "0105556012341", "0105556012341"), Col(3, "branchCode", "00000", "สำนักงานใหญ่"),
            Col(4, "phone", "02-123-4567", "081-234-5678"), Col(5, "email", "ap@siam.co.th", "info@ruammit.com"), Col(6, "address", "99 ถ.สุขุมวิท", "1 ม.2"),
        });
        Assert.Empty(p.MissingRequired);
        Assert.False(p.UsedAi);
        Assert.Equal("peak", p.SourceProfile?.Profile.SourceSystem);
        Assert.Null(At(p, 0).TargetField);                 // รหัสผู้ติดต่อของ PEAK — ไม่ใช่ field ของเรา
        Assert.Equal("Name", At(p, 1).TargetField);
        Assert.Equal("TaxId", At(p, 2).TargetField);
        Assert.Equal("BranchCode", At(p, 3).TargetField);
        Assert.Equal("Phone", At(p, 4).TargetField);
        Assert.Equal("Email", At(p, 5).TargetField);
        Assert.Equal("Address", At(p, 6).TargetField);
        Assert.All(p.Columns, c => Assert.False(c.UsedAi));
        Assert.DoesNotContain(p.Uncertain, c => c.SourceIndex == 0);   // คอลัมน์ที่ "ไม่ใช้" ไม่ต้องเสีย token ถาม AI
    }

    [Fact]
    public void FlowAccount_สินค้า_จับคู่ครบโดยไม่มี_AI()
    {
        var p = Map("products", new[]
        {
            Col(0, "product code"), Col(1, "product name"), Col(2, "unit"), Col(3, "selling price"),
            Col(4, "cost price"), Col(5, "vat rate"), Col(6, "category"), Col(7, "barcode"),
        });
        Assert.Empty(p.MissingRequired);
        Assert.Equal("Code", At(p, 0).TargetField);
        Assert.Equal("Name", At(p, 1).TargetField);
        Assert.Equal("Unit", At(p, 2).TargetField);
        Assert.Equal("SellingPrice", At(p, 3).TargetField);
        Assert.Equal("CostPrice", At(p, 4).TargetField);
        Assert.Equal("VatRate", At(p, 5).TargetField);
    }

    [Fact]
    public void Express_ผังบัญชีหัวไทย_และ_Express_ลูกค้าหัวไทย_จับคู่ครบโดยไม่มี_AI()
    {
        var coa = Map("chart-of-accounts", new[]
        {
            Col(0, "รหัสบัญชี"), Col(1, "ชื่อบัญชี"), Col(2, "ชื่ออังกฤษ"), Col(3, "ประเภทบัญชี"), Col(4, "หมายเหตุ"),
        });
        Assert.Empty(coa.MissingRequired);
        Assert.Equal(new[] { "AccountCode", "AccountName", "AccountNameEn", "AccountType", "Description" },
            coa.Columns.Select(c => c.TargetField).ToArray());

        var contacts = Map("contacts", new[]
        {
            Col(0, "รหัสลูกค้า", "AR-001", "AR-002"), Col(1, "ชื่อลูกค้า", "บริษัท ก จำกัด", "ร้าน ข"), Col(2, "ที่อยู่", "1 ถ.พระราม 4"),
            Col(3, "เลขประจำตัวผู้เสียภาษี", "๐๑๐๕๕๕๖๐๑๒๓๔๑"), Col(4, "สาขา", "สำนักงานใหญ่"), Col(5, "โทรศัพท์", "๐๒-๑๒๓-๔๕๖๗"),
            Col(6, "อีเมล"), Col(7, "ผู้ติดต่อ", "คุณสมชาย"),
        });
        Assert.Empty(contacts.MissingRequired);
        Assert.Equal("express", contacts.SourceProfile?.Profile.SourceSystem);
        Assert.Null(At(contacts, 0).TargetField);           // "รหัสลูกค้า" ⊃ "ลูกค้า" แต่ห้ามกลายเป็น "เป็นลูกค้า"
        Assert.Equal(ImportMapSource.Synonym, At(contacts, 0).Source);
        Assert.Equal("Name", At(contacts, 1).TargetField);
        Assert.Equal("TaxId", At(contacts, 3).TargetField);
        Assert.Equal("BranchCode", At(contacts, 4).TargetField);
        Assert.Equal("ContactPerson", At(contacts, 7).TargetField);
    }

    [Fact]
    public void statement_ธนาคารไทย_หัวไทย_จับคู่ทุกคอลัมน์()
    {
        var p = Map("banktransactions", new[]
        {
            Col(0, "วันที่"), Col(1, "เวลา"), Col(2, "รายการ"), Col(3, "ถอนเงิน"), Col(4, "ฝากเงิน"), Col(5, "ยอดคงเหลือ"),
            Col(6, "ช่องทาง"), Col(7, "รายละเอียด"),
        }, targets: ImportSchemas.BankStatementLines);
        Assert.Equal(new[] { "TransactionDate", "TransactionTime", "Description", "Withdrawal", "Deposit", "Balance", "Channel", "Details" },
            p.Columns.Select(c => c.TargetField).ToArray());
        Assert.Equal("bank:generic-th", p.SourceProfile?.Profile.SourceSystem);
    }

    [Fact]
    public void ลูกหนี้ยกมา_หัวไทยทั่วไป_ได้ช่องบังคับครบ()
    {
        var p = Map("opening-ar", new[]
        {
            Col(0, "ลูกค้า"), Col(1, "เลขที่ใบกำกับ"), Col(2, "วันที่"), Col(3, "ครบกำหนด"), Col(4, "ยอดคงค้าง"),
        });
        Assert.Empty(p.MissingRequired);
        Assert.Equal(new[] { "ContactName", "InvoiceNumber", "InvoiceDate", "DueDate", "Amount" }, p.Columns.Select(c => c.TargetField).ToArray());
    }

    // ═══ ลำดับชั้น ═══

    [Fact]
    public void แม่แบบชนะคำพ้อง_และไม่มีแม่แบบ_คำพ้องตัดสิน()
    {
        var cols = new[] { Col(0, "รหัสสินค้า"), Col(1, "ชื่อสินค้า"), Col(2, "หน่วย"), Col(3, "ราคา") };
        var plain = Map("products", cols);
        Assert.Equal("SellingPrice", At(plain, 3).TargetField);       // ทิศตรงข้าม: ไม่มีแม่แบบ = คำพ้อง "ราคา" → ราคาขาย
        Assert.Equal(ImportMapSource.Synonym, At(plain, 3).Source);

        var withTemplate = Map("products", cols, template: new[] { new ImportTemplateColumnDto("ราคา", "CostPrice") });
        Assert.Equal("CostPrice", At(withTemplate, 3).TargetField);
        Assert.Equal(ImportMapSource.Template, At(withTemplate, 3).Source);
        Assert.Equal("📌 แม่แบบ", At(withTemplate, 3).SourceLabel);
        Assert.DoesNotContain(withTemplate.Columns, c => c.TargetField == "SellingPrice");
    }

    [Fact]
    public void ผู้ใช้ชนะแม่แบบ_และผู้ใช้เลือกไม่ใช้คอลัมน์_ช่องบังคับขาดแสดงให้เห็น()
    {
        var cols = new[] { Col(0, "รหัสสินค้า"), Col(1, "ชื่อสินค้า"), Col(2, "หน่วย"), Col(3, "ราคา") };
        var p = Map("products", cols, template: new[] { new ImportTemplateColumnDto("ราคา", "CostPrice") },
            user: new[] { new ImportColumnAssignment(3, "SellingPrice"), new ImportColumnAssignment(2, null) });
        Assert.Equal("SellingPrice", At(p, 3).TargetField);
        Assert.Equal(ImportMapSource.User, At(p, 3).Source);
        Assert.Null(At(p, 2).TargetField);
        Assert.Equal(ImportMapSource.User, At(p, 2).Source);
        Assert.Contains(p.MissingRequired, f => f.Field == "Unit");
    }

    [Fact]
    public void สองคอลัมน์ได้_field_เดียวกันไม่ได้()
    {
        var p = Map("products", new[] { Col(0, "รหัสสินค้า"), Col(1, "รหัส"), Col(2, "ชื่อสินค้า") });
        Assert.Single(p.Columns, c => c.TargetField == "Code");
        Assert.Equal("Code", At(p, 0).TargetField);                    // ชื่อตรงชนะคำพ้อง
        Assert.Null(At(p, 1).TargetField);
        Assert.Contains(At(p, 1).Alternatives, a => a.TargetField == "Code");   // ผู้ใช้ยังเห็นว่าเลือกได้
        foreach (var et in ImportSchemas.EntityTypes)
        {
            var all = Map(et, ImportSchemas.For(et)!.Select((f, i) => Col(i, f.Label)).Concat(
                ImportSchemas.For(et)!.Select((f, i) => Col(100 + i, f.Field))).ToList());
            var used = all.Columns.Where(c => c.TargetField != null).Select(c => c.TargetField!).ToList();
            Assert.Equal(used.Count, used.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void ค่าในคอลัมน์ตัดสินคำพ้องกำกวม_ลูกค้า_ที่มีแต่_YN_คือ_เป็นลูกค้า_ไม่ใช่ชื่อ()
    {
        var p = Map("contacts", new[] { Col(0, "ชื่อบริษัท", "บริษัท ก จำกัด", "หจก. ข"), Col(1, "ลูกค้า", "Y", "N", "Y") });
        Assert.Equal("Name", At(p, 0).TargetField);
        Assert.Equal("IsCustomer", At(p, 1).TargetField);
    }

    [Fact]
    public void รูปข้อมูล_เลข13หลักผ่านchecksum_เป็นเลขภาษี_เมื่อหัวไม่บอกอะไร()
    {
        var p = Map("contacts", new[] { Col(0, "ชื่อ"), Col(1, "เลข", "0105556012341", "0-1055-56012-34-1", "0105556012341") });
        Assert.Equal("TaxId", At(p, 1).TargetField);
        Assert.Equal(ImportMapSource.DataPattern, At(p, 1).Source);
        Assert.True(At(p, 1).Confidence < 0.85m);                       // ต่ำกว่า 0.85 ⇒ หน้าเว็บระบายเหลือง (ยังมีค่า)
        var none = Map("contacts", new[] { Col(0, "ชื่อ"), Col(1, "เลข", "15000", "2500.50") });
        Assert.Null(At(none, 1).TargetField);                           // ทิศตรงข้าม: ตัวเลขทั่วไปไม่ถูกเดาเป็นเลขภาษี
    }

    // ═══ ด่านคำตอบ AI (กฎเหล็ก #1) ═══

    [Fact]
    public void คำตอบ_AI_นอก_schema_หรือมั่นใจต่ำถูกปัด_และห้ามทับชั้นที่สูงกว่า()
    {
        var cols = new[] { Col(0, "ชื่อลูกค้า"), Col(1, "คอลัมน์ X") };
        var p = Map("contacts", cols, ai: new[]
        {
            new ImportColumnAiAnswer(1, "Salary", 0.95m, "แต่งขึ้น", true),     // ไม่มีใน schema ผู้ติดต่อ
            new ImportColumnAiAnswer(1, "Email", 0.50m, null, true),           // มั่นใจต่ำ
            new ImportColumnAiAnswer(0, "Email", 0.99m, null, true),           // คอลัมน์ 0 ชั้นคำพ้องตัดสินแล้ว
            new ImportColumnAiAnswer(1, "ContactPerson", 0.90m, "ชื่อคน", true),
        });
        Assert.Equal("Name", At(p, 0).TargetField);
        Assert.Equal(ImportMapSource.Synonym, At(p, 0).Source);
        Assert.Equal("ContactPerson", At(p, 1).TargetField);
        Assert.Equal(ImportMapSource.Ai, At(p, 1).Source);
        Assert.True(At(p, 1).UsedAi);
        Assert.Equal("🤖 AI แนะนำ", At(p, 1).SourceLabel);
        Assert.True(p.UsedAi);
        Assert.Equal(2, p.RejectedAiAnswers);
        Assert.DoesNotContain(p.Columns, c => c.TargetField == "Salary");
    }

    [Fact]
    public void คำตอบจากนักเรียน_ไม่ติดป้าย_AI()
    {
        var p = Map("contacts", new[] { Col(0, "ชื่อลูกค้า"), Col(1, "คอลัมน์ X") },
            ai: new[] { new ImportColumnAiAnswer(1, "ContactPerson", 0.90m, null, UsedAi: false) });
        Assert.Equal("ContactPerson", At(p, 1).TargetField);
        Assert.False(At(p, 1).UsedAi);
        Assert.False(p.UsedAi);
        Assert.NotEqual(ImportMapSource.Ai, At(p, 1).Source);
    }

    [Fact]
    public void ด่าน_AI_คอลัมน์ละคำตอบ_field_ละคอลัมน์()
    {
        var targets = ImportSchemas.For("contacts")!;
        var g = ImportColumnMapper.GuardAiAnswers(new[]
        {
            new ImportColumnAiAnswer(0, "email", 0.80m, null, true),
            new ImportColumnAiAnswer(1, "Email", 0.95m, null, true),
            new ImportColumnAiAnswer(7, "Phone", 0.99m, null, true),     // คอลัมน์ไม่มีในไฟล์
        }, targets, new[] { 0, 1 });
        var only = Assert.Single(g);
        Assert.Equal(1, only.SourceIndex);
        Assert.Equal("Email", only.TargetField);                       // คืนสะกดของ schema
    }

    // ═══ signature / schema / seed ═══

    [Fact]
    public void signature_ไม่ขึ้นกับลำดับ_ช่องว่าง_ตัวพิมพ์_เครื่องหมายบังคับ_แต่ชุดต่างได้ค่าต่าง()
    {
        var a = ImportHeaderSignature.Compute(new[] { "ชื่อลูกค้า", "TaxId", "โทรศัพท์" });
        var b = ImportHeaderSignature.Compute(new[] { " โทรศัพท์ ", "ชื่อ ลูกค้า*", "tax_id", "", null });
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, ImportHeaderSignature.Compute(new[] { "ชื่อลูกค้า", "TaxId" }));
        Assert.Equal(ImportHeaderSignature.Normalize("เลข๑๓หลัก"), ImportHeaderSignature.Normalize("เลข13หลัก"));
    }

    [Fact]
    public void schema_หาได้ทุกสะกด_และทุก_field_มีป้ายไทย()
    {
        Assert.Same(ImportSchemas.For("chartofaccounts"), ImportSchemas.For("Chart-Of-Accounts"));
        Assert.Null(ImportSchemas.For("ไม่มีชนิดนี้"));
        Assert.Equal("chart-of-accounts", ImportSchemas.CanonicalEntityType("ChartOfAccounts"));
        Assert.Equal("ข้อมูล", ImportSchemas.EntityLabel("unknown"));
        foreach (var et in ImportSchemas.EntityTypes)
            Assert.All(ImportSchemas.For(et)!, f => Assert.False(string.IsNullOrWhiteSpace(f.Label)));
        Assert.Equal(ImportFieldType.Decimal, ImportSchemas.ParseFieldType("decimal"));
        Assert.Equal(ImportFieldType.TaxId, ImportSchemas.ParseFieldType("string", "ContactTaxId"));
        Assert.Equal(ImportFieldType.Amount, ImportSchemas.ParseFieldType("Amount"));
        Assert.Equal(ImportFieldType.Text, ImportSchemas.ParseFieldType("7"));         // เลขล้วนไม่ใช่ชื่อชนิด
    }

    [Fact]
    public void โปรไฟล์โปรแกรมต้นทาง_ยังเป็นการเดาทั้งหมด_และชี้เฉพาะ_field_ที่มีจริง()
    {
        foreach (var prof in ImportSourceSeed.All)
        {
            Assert.False(prof.Confirmed, $"{prof.SourceSystem}/{prof.EntityType}: ยังไม่มีไฟล์จริงยืนยัน — ห้ามติดว่ายืนยันแล้ว");
            var fields = (ImportSchemas.For(prof.EntityType) ?? Array.Empty<ImportFieldDto>()).Concat(ImportSchemas.BankStatementLines)
                .Select(f => f.Field).ToHashSet(StringComparer.Ordinal);
            Assert.All(prof.ColumnMap.Values.Where(v => v != null), v => Assert.Contains(v!, fields));
        }
        Assert.Null(ImportSourceSeed.Detect("contacts", new[] { "ชื่อ", "เลขภาษี" }));   // น้อยกว่า 3 หัว = ไม่เดา
    }

    [Fact]
    public void เดาชนิดข้อมูลจากหัวตาราง_PEAK_สินค้า_ได้สินค้า()
    {
        var guess = ImportColumnMapper.GuessEntityType(new[]
        {
            Col(0, "productCode"), Col(1, "productName"), Col(2, "unit"), Col(3, "sellingPrice"), Col(4, "costPrice"), Col(5, "vatRate"),
        });
        Assert.Equal("products", guess[0].EntityType);
        Assert.Equal("peak", guess[0].SourceSystem);
    }

    [Fact]
    public void คำตอบรูปมาตรฐาน_เรียงตามคอลัมน์_และไม่ขึ้นกับลำดับที่ส่ง()
    {
        var a = ImportColumnMapper.CanonicalAnswer(new (int, string?)[] { (2, "Email"), (0, "Name"), (1, null) });
        var b = ImportColumnMapper.CanonicalAnswer(new (int, string?)[] { (0, "Name"), (1, ""), (2, "Email") });
        Assert.Equal(a, b);
        Assert.Equal("{\"mappings\":[{\"sourceIndex\":0,\"targetField\":\"Name\"},{\"sourceIndex\":1,\"targetField\":null},{\"sourceIndex\":2,\"targetField\":\"Email\"}]}", a);
    }

    // ═══ นักเรียน ImportColumnMatch (26) — cold start + เรียนรู้ ═══

    private static string ColumnPrompt(params (int Index, string Header)[] cols)
        => ImportPrompts.BuildColumnMatch(Guid.NewGuid(), null, "contacts", ImportPrompts.ToTargetFields(ImportSchemas.For("contacts")!),
            cols.Select(c => new ImportPrompts.ColumnMatchInput(c.Index, c.Header, Array.Empty<string>(), null, 0)).ToList()).UserPromptJson;

    [Fact]
    public void นักเรียนจับคู่คอลัมน์_coldstart_ตอบจากคำพ้อง_รูปเดียวกับครู()
    {
        var pred = ImportColumnMatchDistillationModel.Predict(ColumnPrompt((0, "ชื่อลูกค้า"), (1, "เลขประจำตัวผู้เสียภาษี"), (2, "หัวแปลก")),
            Array.Empty<ImportLearnedHeader>(), "t");
        Assert.NotNull(pred);
        using var doc = JsonDocument.Parse(pred!.StructuredJson!);
        var m = doc.RootElement.GetProperty("mappings").EnumerateArray()
            .ToDictionary(x => x.GetProperty("sourceIndex").GetInt32(), x => x.GetProperty("targetField").GetString());
        Assert.Equal("Name", m[0]);
        Assert.Equal("TaxId", m[1]);
        Assert.Null(m[2]);
        Assert.True(pred.Confidence < 0.85m);                          // คอลัมน์ที่ไม่รู้ดึงความมั่นใจลง ⇒ ครูยังได้ตอบ (Hybrid)
        Assert.Equal(ImportColumnMapper.CanonicalAnswer(new (int, string?)[] { (0, "Name"), (1, "TaxId"), (2, null) }), pred.PrimaryAnswer);
        Assert.Null(ImportColumnMatchDistillationModel.Predict(ColumnPrompt((0, "หัวแปลก")), Array.Empty<ImportLearnedHeader>(), "t"));
    }

    [Fact]
    public void นักเรียนจับคู่คอลัมน์_เรียนคู่ที่ผู้ใช้ยืนยันแล้วตอบได้ด้วยความมั่นใจสูง()
    {
        var learned = new[] { new ImportLearnedHeader("contacts", ImportHeaderSignature.Normalize("หัวแปลก"), "ContactPerson", 5) };
        var pred = ImportColumnMatchDistillationModel.Predict(ColumnPrompt((0, "หัวแปลก")), learned, "t");
        Assert.NotNull(pred);
        Assert.Equal(ImportColumnMapper.CanonicalAnswer(new (int, string?)[] { (0, "ContactPerson") }), pred!.PrimaryAnswer);
        Assert.True(pred.Confidence >= 0.85m);                          // ครบด้วยคู่ที่เรียนไว้ ⇒ ไม่ต้องจ่าย AI
        Assert.Equal(1, pred.SupportingSamples);
        // คู่ของชนิดอื่นไม่รั่วมา
        Assert.Null(ImportColumnMatchDistillationModel.Predict(ColumnPrompt((0, "หัวแปลก")),
            new[] { learned[0] with { EntityType = "products" } }, "t"));
    }

    [Fact]
    public async Task ปิด_provider_ทุกตัว_augmenter_ได้คำตอบนักเรียน_ป้ายไม่ใช่_AI()
    {
        var student = new ImportColumnMatchDistillationModel(new ServiceCollection().BuildServiceProvider(),
            NullLogger<ImportColumnMatchDistillationModel>.Instance);
        var orch = new KillSwitchRig.Orchestrator(new ILocalDistillationModel[] { student }, new List<Accounting.Models.Entities.AiProviderConfig>
        {
            new() { ProviderType = AiProviderType.DeepSeek, IsActive = false, IsEnabled = true },
        });
        var aug = new ImportAiAugmenter(OfflineDb(), orch, NullLogger<ImportAiAugmenter>.Instance);
        var res = await aug.SuggestColumnMappingsWithFeedbackAsync(Guid.NewGuid(), null, "contacts",
            ImportPrompts.ToTargetFields(ImportSchemas.For("contacts")!),
            new[]
            {
                new ImportPrompts.ColumnMatchInput(0, "contactName", new[] { "บริษัท ก จำกัด" }, null, 0),
                new ImportPrompts.ColumnMatchInput(1, "taxNumber", new[] { "0105556012341" }, null, 0),
                new ImportPrompts.ColumnMatchInput(2, "หัวแปลก", Array.Empty<string>(), null, 0),
            });
        Assert.False(res.UsedAi);
        Assert.True(res.FromLocalModel);
        Assert.Contains(res.Suggestions, s => s.SourceIndex == 0 && s.TargetField == "Name");
        Assert.Contains(res.Suggestions, s => s.SourceIndex == 1 && s.TargetField == "TaxId");
        Assert.DoesNotContain(res.Suggestions, s => s.SourceIndex == 2);
    }

    [Fact]
    public async Task augmenter_ปัดคำตอบครูที่นอก_schema_และคืน_FeedbackId_ไม่ทิ้ง()
    {
        var fid = Guid.NewGuid();
        var orch = new FakeOrchestrator(new AiResponse
        {
            Status = AiCallStatus.Success, UsedAi = true, FeedbackId = fid,
            RawResponseJson = "{\"mappings\":[{\"sourceIndex\":1,\"targetField\":\"Salary\",\"confidence\":0.99},"
                + "{\"sourceIndex\":0,\"targetField\":\"name\",\"confidence\":0.95,\"reasoning\":\"ชื่อบริษัท\"}]}",
        });
        var aug = new ImportAiAugmenter(OfflineDb(), orch, NullLogger<ImportAiAugmenter>.Instance);
        var res = await aug.SuggestColumnMappingsWithFeedbackAsync(Guid.NewGuid(), Guid.NewGuid(), "contacts",
            ImportPrompts.ToTargetFields(ImportSchemas.For("contacts")!),
            new[] { new ImportPrompts.ColumnMatchInput(0, "ผู้ซื้อ", Array.Empty<string>(), null, 0),
                    new ImportPrompts.ColumnMatchInput(1, "เงิน", Array.Empty<string>(), null, 0) });
        Assert.Equal(fid, res.FeedbackId);
        Assert.True(res.UsedAi);
        Assert.Equal(1, res.Rejected);
        var s = Assert.Single(res.Suggestions);
        Assert.Equal("Name", s.TargetField);
        Assert.False(orch.LastRequest!.ForceProviderCall);              // นักเรียนต้องได้ตอบก่อน (เดิม true = ข้าม routing + งบ)
    }

    [Fact]
    public async Task ผังบัญชีและจับคู่ข้อมูลเดิม_คำตอบนอกชุดถูกปัด_ใช้คำตอบ_local()
    {
        var accounts = new[] { new ImportPrompts.AccountCandidate("1110", "เงินสด", "Asset"), new ImportPrompts.AccountCandidate("1120", "เงินฝากธนาคาร", "Asset") };
        var bad = new ImportAiAugmenter(OfflineDb(), new FakeOrchestrator(new AiResponse
            { Status = AiCallStatus.Success, UsedAi = true, PrimaryAnswer = "9999", Confidence = 0.97m, FeedbackId = Guid.NewGuid() }),
            NullLogger<ImportAiAugmenter>.Instance);
        var pick = await bad.SuggestAccountMapAsync(Guid.NewGuid(), null, "1101", "เงินสดย่อย", null, accounts, new[] { "1110", "1120" }, "1110", 0.6m);
        Assert.Equal("1110", pick.Answer);
        Assert.True(pick.Rejected);
        Assert.False(pick.UsedAi);

        var good = new ImportAiAugmenter(OfflineDb(), new FakeOrchestrator(new AiResponse
            { Status = AiCallStatus.Success, UsedAi = true, PrimaryAnswer = "1120", Confidence = 0.9m }), NullLogger<ImportAiAugmenter>.Instance);
        var ok = await good.SuggestAccountMapAsync(Guid.NewGuid(), null, "1102", "เงินฝาก กสิกร", null, accounts, new[] { "1110", "1120" }, "1110", 0.5m);
        Assert.Equal("1120", ok.Answer);
        Assert.True(ok.UsedAi);

        var id = Guid.NewGuid().ToString();
        var entity = new ImportAiAugmenter(OfflineDb(), new FakeOrchestrator(new AiResponse
            { Status = AiCallStatus.Success, UsedAi = true, PrimaryAnswer = Guid.NewGuid().ToString(), Confidence = 0.99m }),
            NullLogger<ImportAiAugmenter>.Instance);
        var m = await entity.ResolveEntityAsync(Guid.NewGuid(), null, "contacts", "บจก. สยาม", null,
            new[] { new ImportPrompts.EntityCandidate(id, "บริษัท สยาม จำกัด", null, 0.91m) }, "NEW", 0.5m);
        Assert.Equal("NEW", m.Answer);                                  // id ที่ AI แต่งขึ้น ⇒ ปัดตก
        Assert.True(m.Rejected);
        var newOk = new ImportAiAugmenter(OfflineDb(), new FakeOrchestrator(new AiResponse
            { Status = AiCallStatus.Success, UsedAi = true, PrimaryAnswer = id.ToUpperInvariant(), Confidence = 0.8m }),
            NullLogger<ImportAiAugmenter>.Instance);
        Assert.Equal(id, (await newOk.ResolveEntityAsync(Guid.NewGuid(), null, "contacts", "บจก. สยาม", null,
            new[] { new ImportPrompts.EntityCandidate(id, "บริษัท สยาม จำกัด", null, 0.91m) }, "NEW", 0.5m)).Answer);
    }

    // ═══ นักเรียน ImportDataReview (27) ═══

    [Fact]
    public async Task นักเรียนตรวจข้อมูล_coldstart_ตอบรูปเดียวกับครู_และเลขภาษีผิดถูกจับ()
    {
        var model = new ImportDataReviewDistillationModel(new ServiceCollection().BuildServiceProvider(),
            NullLogger<ImportDataReviewDistillationModel>.Instance);
        var targets = ImportPrompts.ToTargetFields(ImportSchemas.For("contacts")!);
        var req = ImportPrompts.BuildDataReview(Guid.NewGuid(), Guid.NewGuid(), "contacts", targets, new[] { "Name", "TaxId" },
            new[]
            {
                new Dictionary<string, string?> { ["Name"] = "บริษัท ก จำกัด", ["TaxId"] = "0105556012340" },
                new Dictionary<string, string?> { ["Name"] = "ร้าน ข", ["TaxId"] = "0-1055-56012-34-1" },
            }, Array.Empty<ImportPrompts.ExistingEntityRef>());
        Assert.False(req.ForceProviderCall);
        var pred = await model.PredictAsync(Guid.NewGuid(), req.UserPromptJson, default);
        Assert.NotNull(pred);
        Assert.True(model.IsReady);
        using var doc = JsonDocument.Parse(pred!.StructuredJson!);
        var v = Assert.Single(doc.RootElement.GetProperty("field_validation").EnumerateArray());
        Assert.Equal("taxid-invalid", v.GetProperty("code").GetString());
        Assert.Equal(0, v.GetProperty("rowIndex").GetInt32());
        var n = Assert.Single(doc.RootElement.GetProperty("normalizations").EnumerateArray());
        Assert.Equal("0105556012341", n.GetProperty("normalized").GetString());
        Assert.True(pred.Confidence < 0.85m);
    }

    [Fact]
    public void นักเรียนตรวจข้อมูล_เรียนเลิกเตือนที่ถูกปฏิเสธประจำ_แต่ไม่เลิกเตือนเลขภาษีผิด()
    {
        var prompt = "{\"entity_type\":\"contacts\"}";
        var rejectFormat = ImportDataReviewDistillationModel.ChoiceJson(Array.Empty<ImportReviewChoice>(),
            new[] { new ImportReviewChoice("TaxId", "taxid-format"), new ImportReviewChoice("TaxId", "taxid-invalid") });
        var rows = Enumerable.Repeat(((string?)prompt, (string?)rejectFormat), 5).ToList();
        // ทิศตรงข้าม: ปฏิเสธ 3 รับ 1 ⇒ ยังไม่พอ (≥ 80%)
        rows.AddRange(Enumerable.Repeat(((string?)prompt, (string?)ImportDataReviewDistillationModel.ChoiceJson(Array.Empty<ImportReviewChoice>(),
            new[] { new ImportReviewChoice("Name", "thai-digits") })), 3));
        rows.Add((prompt, ImportDataReviewDistillationModel.ChoiceJson(new[] { new ImportReviewChoice("Name", "thai-digits") },
            Array.Empty<ImportReviewChoice>())));
        var s = ImportDataReviewDistillationModel.BuildSuppressions(rows);
        var set = Assert.Contains("contacts", (IDictionary<string, HashSet<(string, string)>>)s);
        Assert.Contains(("TaxId", "taxid-format"), set);
        Assert.DoesNotContain(("TaxId", "taxid-invalid"), set);        // ปัญหาที่มีผลทางกฎหมาย ปิดด้วยการเรียนรู้ไม่ได้
        Assert.DoesNotContain(("Name", "thai-digits"), set);
    }

    // ═══ heuristics บน ImportValueNormalizer (ตัวเดียวกับที่ลงข้อมูลจริง) ═══

    [Fact]
    public void ตัวตรวจ_rule_based_ใช้ตัวแปลงค่ากลาง_ใช่ไม่ใช่และสาขาไทย()
    {
        var targets = ImportPrompts.ToTargetFields(ImportSchemas.For("contacts")!);
        var r = ImportReviewHeuristics.Review("contacts", targets, new[] { "IsCustomer", "BranchCode", "Name" },
            new[] { new Dictionary<string, string?> { ["IsCustomer"] = "ใช่", ["BranchCode"] = "สำนักงานใหญ่", ["Name"] = "บริษัท ก จำกัด" } },
            Array.Empty<ImportPrompts.ExistingEntityRef>());
        Assert.Contains(r.Normalizations, x => x.Field == "IsCustomer" && x.Normalized == "true" && x.Code == "flag-format");
        Assert.Contains(r.Normalizations, x => x.Field == "BranchCode" && x.Normalized == "00000");
        Assert.Empty(r.FieldValidations);
        Assert.DoesNotContain(r.Normalizations, x => x.Field == "Name");    // ข้อความถูกต้องอยู่แล้วไม่ถูกแตะ
    }

    // ═══ ตัวช่วย ═══

    private static Accounting.Data.AccountingDbContext OfflineDb()
        => new(new DbContextOptionsBuilder<Accounting.Data.AccountingDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none").Options);

    private sealed class FakeOrchestrator : IAiOrchestrator
    {
        private readonly AiResponse _resp;
        public AiRequest? LastRequest { get; private set; }
        public FakeOrchestrator(AiResponse resp) => _resp = resp;
        public Task<AiResponse> AskAsync(AiRequest request, CancellationToken ct = default) { LastRequest = request; return Task.FromResult(_resp); }
        public Task<AiResponse> AskAndRecordChoiceAsync(AiRequest request, string userChoice, CancellationToken ct = default) => AskAsync(request, ct);
        public Task RecordUserChoiceAsync(Guid feedbackId, string chosenAnswer, bool acceptedAi, CancellationToken ct = default,
            UserChoiceSource source = UserChoiceSource.Implicit) => Task.CompletedTask;
    }
}
