using Accounting.Helpers;
using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีม OCR 11 (ฝ่ายค้าน OCR 9 P1-5 · CLAUDE F2 ข้อ 9): ผลแกะรุ่น 6–8 ที่ลงบรรทัดสินค้าเป็น 1 × ยอด — รายงานจับ · ซ่อมเฉพาะส่วนที่ปลอดภัย + audit ·
/// ทิศตรงข้าม (บิล PEA "(4,272 หน่วย)" · สแกนรุ่น 9 · alias ก่อน 13:09 UTC · "Flash Drive (64 GB)") ไม่ถูกแตะ · เอกสารที่ออกแล้ว/สต็อก/ชื่อสินค้าไม่ถูกแก้ ·
/// tenant isolation (กฎ M)
/// </summary>
[Trait("Category", "Db")]
public class OcrLumpSumRepairDbTests
{
    private readonly ITestOutputHelper _out;
    public OcrLumpSumRepairDbTests(ITestOutputHelper output) => _out = output;

    private static readonly DateTime After = OcrLumpSumRepair.PollutionStartUtc.AddMinutes(30);
    private static readonly DateTime Before = OcrLumpSumRepair.PollutionStartUtc.AddMinutes(-30);

    private static string Items(params (string Desc, decimal Qty, string Unit, decimal Amount)[] lines)
        => System.Text.Json.JsonSerializer.Serialize(lines.Select(l => new OcrExtractedLineItem
        {
            Description = l.Desc, Quantity = l.Qty, Unit = l.Unit, UnitPrice = l.Amount / l.Qty, Amount = l.Amount,
        }).ToList());

    private sealed record Seed(Guid CompanyId, Guid PollutedScan, Guid PeaScan, Guid V9Scan, Guid DocId, Guid ProductId,
        Guid PollutedAlias, Guid TwinAlias, Guid OldAlias, Guid SpecAlias, Guid LearnerKey, Guid MovementId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, string name, string taxId)
    {
        var company = new Company { Name = name, TaxId = taxId, IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var c = company.Id;
        var contact = new Contact { CompanyId = c, Name = "ร้านวัสดุทดสอบ", IsSupplier = true };
        var product = new Product { CompanyId = c, Code = "P-" + taxId[^4..], Name = "น้ำดื่ม 600ml (12 ขวด)", Unit = "รายการ", CreatedAt = After };
        db.Contacts.Add(contact);
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var doc = new Document
        {
            CompanyId = c, ContactId = contact.Id, DocumentType = DocumentType.PurchaseInvoice, DocumentNumber = "PI-OCR11-" + taxId[^4..],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, SubTotal = 300m, TotalAmount = 300m, Currency = "THB",
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "น้ำดื่ม 600ml (12 ขวด)", Quantity = 1m, UnitPrice = 300m, Amount = 300m });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        var polluted = new OcrScanResult
        {
            CompanyId = c, OriginalFileName = "goods-v7.pdf", ScanStatus = "Completed", ExtractionVersion = 7, RetryCount = 1,
            CreatedDocumentId = doc.Id, StockImportedAt = After,
            ExtractedItemsJson = Items(("น้ำดื่ม 600ml (12 ขวด)", 1m, "รายการ", 300m), ("คอนกรีตผสมเสร็จ (2.75 ลบ.ม.)", 1m, "รายการ", 1500m),
                ("ปากกา", 2m, "ด้าม", 20m)),
        };
        var pea = new OcrScanResult
        {
            CompanyId = c, OriginalFileName = "pea.pdf", ScanStatus = "Completed", ExtractionVersion = 7,
            ExtractedItemsJson = Items(("ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)", 1m, "รายการ", 19381.50m)),
        };
        var v9 = new OcrScanResult
        {
            CompanyId = c, OriginalFileName = "goods-v9.pdf", ScanStatus = "Completed", ExtractionVersion = OcrExtractionVersion.Current,   // ทีม OCR 12: รุ่นปัจจุบัน (9/10 อยู่ในชุดซ่อมแล้ว)
            ExtractedItemsJson = Items(("น้ำดื่ม 600ml (12 ขวด)", 1m, "รายการ", 300m)),
        };
        db.OcrScanResults.AddRange(polluted, pea, v9);

        var aPolluted = new ProductAlias { CompanyId = c, ProductId = product.Id, ContactId = contact.Id, AliasName = "ค่าบริการ (จำนวน 3)",
            NormalizedName = ProductMatcher.Normalize("ค่าบริการ (จำนวน 3)"), TimesUsed = 2, CreatedAt = After };
        var aDup = new ProductAlias { CompanyId = c, ProductId = product.Id, ContactId = contact.Id, AliasName = "น้ำดื่ม 600ml (12 ขวด)",
            NormalizedName = ProductMatcher.Normalize("น้ำดื่ม 600ml (12 ขวด)"), TimesUsed = 1, CreatedAt = After };
        var aTwin = new ProductAlias { CompanyId = c, ProductId = product.Id, ContactId = contact.Id, AliasName = "น้ำดื่ม 600ml",
            NormalizedName = ProductMatcher.Normalize("น้ำดื่ม 600ml"), TimesUsed = 5, CreatedAt = Before };
        var aOld = new ProductAlias { CompanyId = c, ProductId = product.Id, AliasName = "โซดา (12 ขวด)",
            NormalizedName = ProductMatcher.Normalize("โซดา (12 ขวด)"), CreatedAt = Before };
        var aSpec = new ProductAlias { CompanyId = c, ProductId = product.Id, AliasName = "Flash Drive (64 GB)",
            NormalizedName = ProductMatcher.Normalize("Flash Drive (64 GB)"), CreatedAt = After };
        db.ProductAliases.AddRange(aPolluted, aDup, aTwin, aOld, aSpec);

        var learner = new OcrCategoryMapping { CompanyId = c, VendorKey = "0105556000999", DescriptionKeyword = "ค่าไฟฟ้า (4,272 หน่วย)",
            AccountCode = "53300", TimesUsed = 1, CreatedAt = After };
        db.OcrCategoryMappings.Add(learner);

        var move = new StockMovement
        {
            CompanyId = c, ProductId = product.Id, MovementDate = After, MovementType = "IN", Quantity = 1m, UnitCost = 300m, BalanceAfter = 1m,
            Reference = "OCR-IMPORT/INV1", Notes = $"นำเข้าจาก OCR (สแกน {polluted.Id}) — น้ำดื่ม 600ml (12 ขวด)",
        };
        db.StockMovements.Add(move);
        await db.SaveChangesAsync();
        return new Seed(c, polluted.Id, pea.Id, v9.Id, doc.Id, product.Id, aPolluted.Id, aDup.Id, aOld.Id, aSpec.Id, learner.Id, move.Id);
    }

    [Fact]
    public async Task ผลแกะรุ่น7_รายงานจับ_ซ่อมสแกนaliasคีย์ตัวเรียน_ไม่แตะเอกสารสต็อกชื่อสินค้า_ทิศตรงข้ามไม่ถูกแตะ_ข้ามบริษัทไม่เห็น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var a = await SeedAsync(db, "บริษัทซ่อมผลแกะ ก", "0105556001101");
        var b = await SeedAsync(db, "บริษัทซ่อมผลแกะ ข", "0105556001102");

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var report = await new OcrLumpSumRepairService(req, NullLogger<OcrLumpSumRepairService>.Instance).FindAsync(a.CompanyId);
            var scan = Assert.Single(report.Scans);                          // PEA (มิเตอร์) · รุ่น 9 ไม่อยู่ · บริษัท ข ไม่อยู่
            Assert.Equal(a.PollutedScan, scan.ScanId);
            Assert.Equal(new[] { 1, 2 }, scan.Lines.Select(l => l.LineNo));  // ปากกา 2 ด้าม ไม่ใช่ 1 × ยอด
            Assert.False(scan.AlreadyMarked);
            var doc = Assert.Single(report.Documents);
            Assert.Equal(a.DocId, doc.DocumentId);
            Assert.Contains("ใบลดหนี้", doc.WayForward);
            var mv = Assert.Single(report.StockMovements);
            Assert.Equal(a.MovementId, mv.MovementId);
            Assert.Equal(new[] { a.PollutedAlias, a.TwinAlias }.OrderBy(x => x), report.Aliases.Select(x => x.Id).OrderBy(x => x));
            Assert.DoesNotContain(report.Aliases, x => x.Id == a.OldAlias || x.Id == a.SpecAlias);   // ก่อน 13:09 · สเปกสินค้า
            Assert.Equal("ค่าไฟฟ้า", Assert.Single(report.LearnerKeys).Proposed.Split(' ')[0]);
            var prod = Assert.Single(report.Products);
            Assert.Equal("น้ำดื่ม 600ml", prod.Proposed);
        }

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var result = await new OcrLumpSumRepairService(req, NullLogger<OcrLumpSumRepairService>.Instance).RepairAsync(a.CompanyId,
                new OcrLumpSumRepairRequest(new() { a.PollutedScan, a.PeaScan, b.PollutedScan }, new() { a.PollutedAlias, a.TwinAlias, a.SpecAlias },
                    new() { a.LearnerKey }), Guid.NewGuid().ToString());
            Assert.Equal(4, result.RepairedCount);                           // สแกน 1 · alias 2 · คีย์ 1
            Assert.Contains(result.Items, o => o.Id == a.PeaScan && !o.Repaired);
            Assert.Contains(result.Items, o => o.Id == b.PollutedScan && !o.Repaired);   // ข้ามบริษัท
            Assert.Contains(result.Items, o => o.Id == a.SpecAlias && !o.Repaired);
        }

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var scan = await check.OcrScanResults.AsNoTracking().SingleAsync(s => s.Id == a.PollutedScan);
            Assert.True(OcrLumpSumRepair.AlreadyMarked(scan.ProcessingNotes));
            Assert.Equal(2, scan.ProcessingNotes!.Split('\n').Count(l => l.StartsWith(OcrLineQuantityGuard.Tag + " " + OcrLumpSumRepair.RuleCode)));
            Assert.Equal(0, scan.RetryCount);
            Assert.Equal(7, scan.ExtractionVersion);
            Assert.Null((await check.OcrScanResults.AsNoTracking().SingleAsync(s => s.Id == a.PeaScan)).ProcessingNotes);

            var polluted = await check.ProductAliases.AsNoTracking().SingleAsync(x => x.Id == a.PollutedAlias);
            Assert.Equal("ค่าบริการ", polluted.AliasName);
            Assert.Equal(ProductMatcher.Normalize("ค่าบริการ"), polluted.NormalizedName);
            var dup = await check.ProductAliases.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == a.TwinAlias);
            Assert.True(dup.IsDeleted);                                        // รวมกับ "น้ำดื่ม 600ml" ที่มีอยู่
            var twin = await check.ProductAliases.AsNoTracking().SingleAsync(x => x.CompanyId == a.CompanyId && x.AliasName == "น้ำดื่ม 600ml" && !x.IsDeleted);
            Assert.Equal(6, twin.TimesUsed);
            Assert.Equal("Flash Drive (64 GB)", (await check.ProductAliases.AsNoTracking().SingleAsync(x => x.Id == a.SpecAlias)).AliasName);
            Assert.Equal("ค่าไฟฟ้า", (await check.OcrCategoryMappings.AsNoTracking().SingleAsync(x => x.Id == a.LearnerKey)).DescriptionKeyword);

            // ไม่แตะ: เอกสารที่ออกแล้ว · สต็อก · ชื่อสินค้า · บริษัท ข
            var line = await check.DocumentLines.AsNoTracking().SingleAsync(l => l.DocumentId == a.DocId);
            Assert.Equal((1m, 300m), (line.Quantity, line.UnitPrice));
            Assert.Equal(1m, (await check.StockMovements.AsNoTracking().SingleAsync(m => m.Id == a.MovementId)).Quantity);
            Assert.Equal("น้ำดื่ม 600ml (12 ขวด)", (await check.Products.AsNoTracking().SingleAsync(p => p.Id == a.ProductId)).Name);
            Assert.Null((await check.OcrScanResults.AsNoTracking().SingleAsync(s => s.Id == b.PollutedScan)).ProcessingNotes);
            Assert.Equal("ค่าบริการ (จำนวน 3)", (await check.ProductAliases.AsNoTracking().SingleAsync(x => x.Id == b.PollutedAlias)).AliasName);

            // audit chain: หนึ่งแถวต่อสิ่งที่ซ่อม (แถว ChangeTracker อัตโนมัติของสแกนที่ ProcessingNotes มีรหัสกติกาไม่นับ — นับเฉพาะแถวของตัวซ่อม)
            var marker = "\"ruleCode\":\"" + OcrLumpSumRepair.RuleCode + "\"";
            var audited = await check.AuditLogs.AsNoTracking()
                .Where(x => x.CompanyId == a.CompanyId && x.NewValues != null && x.NewValues.Contains(marker)).ToListAsync();
            Assert.Equal(4, audited.Count);
            Assert.All(audited, x => Assert.False(string.IsNullOrEmpty(x.RowHash)));
        }

        // ซ่อมซ้ำ = ข้าม (ไม่ต่อหมายเหตุ/audit ซ้ำ) · รายงานบอกว่าติดป้ายแล้ว
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var svc = new OcrLumpSumRepairService(req, NullLogger<OcrLumpSumRepairService>.Instance);
            var again = await svc.RepairAsync(a.CompanyId, new OcrLumpSumRepairRequest(new() { a.PollutedScan }, null, null), "x");
            Assert.Equal(0, again.RepairedCount);
            var report = await svc.FindAsync(a.CompanyId);
            Assert.True(Assert.Single(report.Scans).AlreadyMarked);
            Assert.Empty(report.Aliases);
            Assert.Empty(report.LearnerKeys);
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RepairAsync(a.CompanyId, new OcrLumpSumRepairRequest(null, null, null), "x"));
        }
    }

    // ── ทีม OCR 12 (ฝ่ายค้านรอบ 3 P1-4 · P2-2 · P2-3) ──
    [Fact]
    public async Task OCR12_ด่านอนุมัติหลังซ่อมมีจริง_ปลดเมื่อแก้บรรทัด_ใบOCRปกติและใบไม่ใช่OCRอนุมัติตามเดิม_บิลมิเตอร์ไม่ถูกฟ้อง_คำเตือนแพ็ก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var company = new Company { Name = "บริษัทด่านอนุมัติ OCR 12", TaxId = "0105556001201", IsVatRegistered = true };
        var other = new Company { Name = "บริษัทอื่น OCR 12", TaxId = "0105556001202", IsVatRegistered = true };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();
        var c = company.Id;
        var contact = new Contact { CompanyId = c, Name = "ร้านน้ำดื่มทดสอบ", IsSupplier = true };
        var product = new Product { CompanyId = c, Code = "P-1201", Name = "น้ำดื่ม", Unit = "ขวด", CreatedAt = Before };
        db.Contacts.Add(contact);
        db.Products.Add(product);
        await db.SaveChangesAsync();

        Document Draft(string no, params (string Desc, decimal Q, decimal P, decimal Amt)[] lines)
        {
            var d = new Document
            {
                CompanyId = c, ContactId = contact.Id, DocumentType = DocumentType.PurchaseInvoice, DocumentNumber = no,
                Status = DocumentStatus.Draft, DocumentDate = DateTime.UtcNow.Date, SubTotal = lines.Sum(l => l.Amt), TotalAmount = lines.Sum(l => l.Amt), Currency = "THB",
            };
            var i = 1;
            foreach (var l in lines)
                d.Lines.Add(new DocumentLine { LineOrder = i++, Description = l.Desc, Quantity = l.Q, UnitPrice = l.P, Amount = l.Amt });
            return d;
        }
        var pollutedDoc = Draft("DRAFT-OCR12-A", ("น้ำดื่ม 600ml (12 ขวด)", 1m, 300m, 300m), ("ปากกา", 2m, 10m, 20m));
        var cleanOcrDoc = Draft("DRAFT-OCR12-B", ("สินค้า", 7m, 14.29m, 100m));
        var manualDoc = Draft("DRAFT-OCR12-C", ("ค่าขนส่ง", 1m, 300m, 300m));
        db.Documents.AddRange(pollutedDoc, cleanOcrDoc, manualDoc);
        await db.SaveChangesAsync();

        var pollutedScan = new OcrScanResult
        {
            CompanyId = c, OriginalFileName = "goods-v7-draft.pdf", ScanStatus = "Completed", ExtractionVersion = 7, CreatedDocumentId = pollutedDoc.Id,
            ExtractedItemsJson = Items(("น้ำดื่ม 600ml (12 ขวด)", 1m, "รายการ", 300m), ("ปากกา", 2m, "ด้าม", 20m)),
        };
        var cleanScan = new OcrScanResult
        {
            CompanyId = c, OriginalFileName = "clean.pdf", ScanStatus = "Completed", ExtractionVersion = OcrExtractionVersion.Current, CreatedDocumentId = cleanOcrDoc.Id,
            ProcessingNotes = "[LINE-QTY] จำนวน 7 · ยอด 100.00 — ระบบคงจำนวน · ค่าที่ระบบตั้ง ⟦7×14.29⟧",
            ExtractedItemsJson = Items(("สินค้า", 7m, "ชิ้น", 100m)),
        };
        // บิล PEA ที่ตัวเขียนรุ่น 7 เขียน "(จำนวน 4,272)" (ป้ายกระดาษ "จำนวนหน่วยที่ใช้") — มิเตอร์จริง ห้ามถูกฟ้อง (P2-2)
        var peaEnergy = new OcrScanResult
        {
            CompanyId = c, OriginalFileName = "pea-energy.pdf", ScanStatus = "Completed", ExtractionVersion = 7,
            ExtractedVendorTaxId = "0994000165501", ExtractedVendorName = "การไฟฟ้าส่วนภูมิภาค",
            RawTextContent = "การไฟฟ้าส่วนภูมิภาค\nจำนวนหน่วยที่ใช้ 4,272 หน่วย ค่าพลังงาน 19,381.50\nค่า Ft 4,272 หน่วย 893.50",
            ExtractedItemsJson = Items(("ค่าพลังงาน (จำนวน 4,272)", 1m, "รายการ", 19381.50m), ("ค่า Ft (จำนวน 4,272)", 1m, "รายการ", 893.50m)),
        };
        // บริษัทอื่นที่ (ผิดพลาด) ชี้เอกสารของบริษัทนี้ พร้อมหมายเหตุซ่อม — ห้ามมีผลข้ามบริษัท (กฎ M)
        var foreign = new OcrScanResult
        {
            CompanyId = other.Id, OriginalFileName = "foreign.pdf", ScanStatus = "Completed", ExtractionVersion = 7, CreatedDocumentId = manualDoc.Id,
            ProcessingNotes = OcrLumpSumRepair.ScanNote(7, 1, "ค่าขนส่ง", 300m),
        };
        db.OcrScanResults.AddRange(pollutedScan, cleanScan, peaEnergy, foreign);
        var packAlias = new ProductAlias { CompanyId = c, ProductId = product.Id, ContactId = contact.Id, AliasName = "โค้ก (6 กระป๋อง)",
            NormalizedName = ProductMatcher.Normalize("โค้ก (6 กระป๋อง)"), CreatedAt = After, Source = "user" };
        var guardAlias = new ProductAlias { CompanyId = c, ProductId = product.Id, ContactId = contact.Id, AliasName = "น้ำดื่ม 600ml (12 ขวด)",
            NormalizedName = ProductMatcher.Normalize("น้ำดื่ม 600ml (12 ขวด)"), CreatedAt = After, Source = "auto" };
        var longLine = "น้ำดื่มสิงห์ 600 มล. แพ็กหกขวด รุ่นพิเศษ ฉลาก (12 ขวด)";
        var truncated = new OcrCategoryMapping { CompanyId = c, VendorKey = "0105556009999", DescriptionKeyword = OcrLumpSumRepair.LearnerKey(longLine),
            AccountCode = "51100", TimesUsed = 1, CreatedAt = After };
        db.ProductAliases.AddRange(packAlias, guardAlias);
        db.OcrCategoryMappings.Add(truncated);
        var longScan = new OcrScanResult
        {
            CompanyId = c, OriginalFileName = "long.pdf", ScanStatus = "Completed", ExtractionVersion = 6,
            ExtractedItemsJson = Items((longLine, 1m, "รายการ", 300m)),
        };
        db.OcrScanResults.Add(longScan);
        await db.SaveChangesAsync();

        async Task<string?> Block(Guid docId)
        {
            using var r = DbTestDatabase.TryCreateContext()!;
            var d = await r.Documents.Include(x => x.Lines).SingleAsync(x => x.Id == docId && x.CompanyId == c);
            return await OcrLumpSumRepairService.ApprovalBlockAsync(r, c, d);
        }

        // ก่อนกดซ่อม: ยังไม่มีหมายเหตุ ⇒ ไม่บล็อก (ข้อความทางไปต่อพูดถึง "หลังกดซ่อม")
        Assert.Null(await Block(pollutedDoc.Id));

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var svc = new OcrLumpSumRepairService(req, NullLogger<OcrLumpSumRepairService>.Instance);
            var report = await svc.FindAsync(c);
            Assert.DoesNotContain(report.Scans, x => x.ScanId == peaEnergy.Id);               // P2-2: บิลมิเตอร์จริงไม่ถูกฟ้อง
            Assert.Contains(report.Scans, x => x.ScanId == pollutedScan.Id);
            Assert.Contains("หลังกดซ่อม", Assert.Single(report.Documents, x => x.DocumentId == pollutedDoc.Id).WayForward);
            Assert.NotNull(Assert.Single(report.Aliases, x => x.Id == packAlias.Id).Warning);   // P2-3: ขนาดแพ็กที่ไม่มีที่มาจากตัวกัน ⇒ เตือน
            Assert.Null(Assert.Single(report.Aliases, x => x.Id == guardAlias.Id).Warning);    // ตรงกับบรรทัดที่ตัวกันเขียน ⇒ ไม่เตือน
            var lk = Assert.Single(report.LearnerKeys, x => x.Id == truncated.Id);              // P2-3: คีย์ที่ถูกตัด 50 ตัวหาเจอแล้ว
            Assert.Equal(OcrLumpSumRepair.LearnerKey("น้ำดื่มสิงห์ 600 มล. แพ็กหกขวด รุ่นพิเศษ ฉลาก"), lk.Proposed);
            var res = await svc.RepairAsync(c, new OcrLumpSumRepairRequest(new() { pollutedScan.Id }, null, new() { truncated.Id }), Guid.NewGuid().ToString());
            Assert.Equal(2, res.RepairedCount);
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(OcrLumpSumRepair.LearnerKey("น้ำดื่มสิงห์ 600 มล. แพ็กหกขวด รุ่นพิเศษ ฉลาก"),
                (await check.OcrCategoryMappings.AsNoTracking().SingleAsync(x => x.Id == truncated.Id)).DescriptionKeyword);

        // หลังซ่อม: ด่านอนุมัติบล็อกจริง (ตัวตัดสิน + ทางเข้าจริงของ DocumentService)
        var reason = await Block(pollutedDoc.Id);
        Assert.NotNull(reason);
        Assert.Contains("น้ำดื่ม 600ml (12 ขวด)", reason);
        Assert.Contains("แกะใหม่", reason);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var docs = new DocumentService(req, new AccountingService(req), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => docs.ApproveDocumentAsync(c, pollutedDoc.Id, "tester", acknowledgeWarnings: true));
            Assert.Equal(OcrPostingReadiness.HumanApprovalRuleCode, ex.RuleCode);
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var d = await check.Documents.AsNoTracking().SingleAsync(x => x.Id == pollutedDoc.Id);
            Assert.Equal((DocumentStatus.Draft, "DRAFT-OCR12-A"), (d.Status, d.DocumentNumber));   // ไม่มีอะไรถูกบันทึก
        }

        // ทิศตรงข้าม: ใบ OCR ปกติ ([LINE-QTY] ธรรมดา) · ใบที่ไม่ใช่ OCR (และสแกนบริษัทอื่นที่ชี้มา) ⇒ ด่านนี้ไม่บล็อก
        Assert.Null(await Block(cleanOcrDoc.Id));
        Assert.Null(await Block(manualDoc.Id));

        // คนแก้บรรทัดตามกระดาษ (12 × 25) ⇒ ปลด
        using (var edit = DbTestDatabase.TryCreateContext()!)
        {
            var line = await edit.DocumentLines.SingleAsync(l => l.DocumentId == pollutedDoc.Id && l.LineOrder == 1);
            line.Quantity = 12m;
            line.UnitPrice = 25m;
            await edit.SaveChangesAsync();
        }
        Assert.Null(await Block(pollutedDoc.Id));
    }
}
