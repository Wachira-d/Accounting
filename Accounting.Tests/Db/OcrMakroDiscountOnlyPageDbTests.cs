using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// 2026-10-16 ทีม OCR 7 — Makro หน้า 2/2 (405901117163) สแกนซ้ำ · เจ้าของ: "ยอดรวมลงถูกแต่บอดราย รายการ ทำไมลงแบบนี้ ควรต้องวิเคราะห์ให้ยอดตรงยอดรวมสิ
/// เห็นตารางสรุปรวม ก็ใช้ตารางนั้นได้เลย ถ้ายอดรวมตรง พัฒนาระบบให้ดีขึ้นอย่าแย่ลง"
///
/// <para><b>ตัวสร้างบรรทัดจริงของ service</b> (<see cref="OcrService.PreviewDocumentLinesAsync"/> → <c>BuildScanLinesAsync</c> บน PostgreSQL) — ไม่ใช่ replay harness:
/// harness ไม่มีผังบัญชีจาก DB ⇒ ทำซ้ำเส้นที่ผู้ใช้เห็นไม่ได้ · ก่อนแก้ (6b9bf926 + 687d3a10): แถว SLAB1 ไม่มีผังของตัวเอง (ผังหัวใบ 51510) อีกสองแถวผัง 52120 ⇒
/// <c>KeepItemsReason</c> "ผังบัญชีต่างกัน 2 ผัง" ⇒ แถวส่วนลด 30/60/10 เป็นสินค้า 7% + [Σ-GAP] "Σ บรรทัด 100.00 น้อยกว่ายอดก่อน VAT 10,341.50 — OCR อาจอ่านบรรทัดขาด
/// 10,241.50" = ภาพหน้าจอทุกตัวอักษร · หลังแก้: บรรทัดสรุปตามตาราง ยกเว้น 1,220.00 + 9,121.50/638.50 · ไม่มี [Σ-GAP] · หมายเหตุรายการเดิม (ไม่บล็อก)</para>
/// </summary>
[Trait("Category", "Db")]
public class OcrMakroDiscountOnlyPageDbTests
{
    private readonly ITestOutputHelper _o;
    public OcrMakroDiscountOnlyPageDbTests(ITestOutputHelper o) => _o = o;

    private static async Task<(Guid Company, Guid Scan)> SeedAsync(Accounting.Data.AccountingDbContext db, string raw, string?[] lineAccounts, decimal? presetRate)
    {
        var company = new Company { Name = "Makro2/2 " + Guid.NewGuid(), TaxId = "0203562005871", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.ChartOfAccounts.Add(new ChartOfAccount { CompanyId = company.Id, AccountCode = "52120", AccountName = "ค่าวัสดุสิ้นเปลือง" });
        db.ChartOfAccounts.Add(new ChartOfAccount { CompanyId = company.Id, AccountCode = "51510", AccountName = "ต้นทุนค่าแม่บ้าน/ทำความสะอาด" });
        await db.SaveChangesAsync();
        var rows = new (string Desc, decimal Amount)[]
        {
            ("ซื้อสินค้าฮีโร่ ถุงขยะ ถุงหูหิ้ว ถุงซิป ครบ 359 ลด 30 บาท(*1.0)", 30m),
            ("ลด 60บเมื่อซื้อสินค้ายูนิลีเวอร์ ดูแลเส้นผม ผิวหน้า ผิวกาย ที่ร่วมครบ 599 บ(*1.0)", 60m),
            ("SLAB1(*5.0)", 10m),
        };
        var items = rows.Select((r, i) => new { Description = r.Desc, Amount = r.Amount, VatRate = presetRate, SuggestedAccountCode = lineAccounts[i] }).ToList();
        var scan = new OcrScanResult
        {
            CompanyId = company.Id, OriginalFileName = "makro-405901117163-p2.jpg", ScanStatus = "Completed", DocumentType = "TaxInvoice", OurRole = "Buyer",
            RawTextContent = raw,
            // หัวสแกนตามภาพหน้าจอ: ยอดก่อน VAT 11,208 (ป้าย "รวมภาษีมูลค่าเพิ่ม/ TOTAL") · VAT 638.50 · ยอดรวม 10,980 · ส่วนลด 228
            ExtractedSubTotal = 11208m, ExtractedVatAmount = 638.50m, ExtractedTotalAmount = 10980m, ExtractedDiscountAmount = 228m,
            ExtractedDocumentNumber = "405901117163", ExtractedVendorName = "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)",
            ExtractedItemsJson = System.Text.Json.JsonSerializer.Serialize(items),
            SuggestedAccountsJson = "{\"DebitAccountCode\":\"51510\",\"CreditAccountCode\":null}",
        };
        db.Set<OcrScanResult>().Add(scan);
        await db.SaveChangesAsync();
        return (company.Id, scan.Id);
    }

    private static OcrService Svc(Accounting.Data.AccountingDbContext db)
        => new(db, null!, null!, NullLogger<OcrService>.Instance, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    [Theory]
    [InlineData(false, "52120", "52120", null)]       // เส้นที่ทำซ้ำภาพหน้าจอ (ผังต่างกัน)
    [InlineData(true, "52120", "52120", "52120")]     // ผังเดียวกัน + engine ตั้ง 7% ทุกแถว
    [InlineData(false, null, null, null)]             // ผังหัวใบทุกแถว
    public async Task หน้าที่มีแต่แถวส่วนลด_ตัวสร้างบรรทัดจริง_บรรทัดสรุปตามตาราง_ไม่มีΣGAP(bool preset7, string? a1, string? a2, string? a3)
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _o.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        foreach (var raw in new[] { OcrPaperSamples.MakroPage2of2, OcrMakroPage2VatAddedTwiceTests.MakroPage2of2Tesseract })
        {
            var (company, scan) = await SeedAsync(db, raw, new[] { a1, a2, a3 }, preset7 ? 7m : null);
            using var req = DbTestDatabase.TryCreateContext()!;
            var p = await Svc(req).PreviewDocumentLinesAsync(company, scan, null);
            Assert.Equal(new[] { (1220.00m, -1m, 0.00m), (9121.50m, 7m, 638.50m) },
                p.Lines.Select(l => (l.Amount, l.VatRate, l.VatAmount)).ToArray());
            Assert.All(p.Lines, l => Assert.Equal(-1, l.SourceItemIndex));     // แถวส่วนลดไม่เป็นบรรทัด
            Assert.Equal(10980.00m, p.Lines.Sum(l => l.Amount + l.VatAmount));
            Assert.DoesNotContain("[Σ-GAP]", p.Notes ?? "");
            Assert.Contains("หน้านี้มีแต่แถวส่วนลด 3 แถว", p.Notes);
            Assert.Equal(10341.50m, p.HeaderSubTotal);
            var mixed = a1 != a3;
            Assert.Equal(mixed, (p.Notes ?? "").Contains("รายการเดิม 3 บรรทัดมีหลายผัง — " + Accounting.Helpers.OcrVatCodeLines.MixedDimensionsLead));
            // ผังของบรรทัดสรุป = ผังที่ยอดมากสุด (52120: 90 บาท > ผังหัวใบ 51510: 10 บาท) · ไม่มีผังรายบรรทัด = ผังหัวใบ
            Assert.All(p.Lines, l => Assert.Equal(a1 ?? "51510", l.AccountCode));
        }
    }

    [Fact]
    public async Task ทิศตรงข้าม_ผูกหักณที่จ่าย_บรรทัดสรุปไม่แบกWHT_คงรายการและฟ้อง()
    {
        // ข้อยกเว้นที่คงไว้ (WHT/PO): ใบเดียวกันที่สแกนติดหัก ณ ที่จ่าย ⇒ ไม่แทนด้วยบรรทัดสรุป ⇒ คงรายการ + [Σ-GAP] (ไม่แต่งตัวเลข)
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _o.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (company, scanId) = await SeedAsync(db, OcrPaperSamples.MakroPage2of2, new string?[] { "52120", "52120", "52120" }, null);
        var scan = await db.Set<OcrScanResult>().FindAsync(scanId);
        scan!.HasWht = true; scan.WhtRate = 1m;
        await db.SaveChangesAsync();
        using var req = DbTestDatabase.TryCreateContext()!;
        var p = await Svc(req).PreviewDocumentLinesAsync(company, scanId, null);
        Assert.Equal(3, p.Lines.Count);
        Assert.Contains("[Σ-GAP]", p.Notes);
    }
}
