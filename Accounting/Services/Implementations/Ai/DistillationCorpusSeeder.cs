using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ai;

/// <summary>
/// Cold-start seeding for the local distillation models. Inserts a
/// curated set of common Thai vendor + keyword → account-code patterns
/// into SystemOcrCategoryMapping so the GlAccount distillation model
/// can produce useful predictions BEFORE any user has confirmed a
/// single AI suggestion in this tenant.
///
/// The seed is intentionally small + well-known (gas, supplies, rent,
/// utilities, banking) — not a comprehensive ontology. Real coverage
/// comes from the supervised pipeline (DeepSeek + user confirms);
/// this just primes the pump so the first 100 documents aren't all
/// full-cost AI calls.
///
/// Idempotent — every entry has a deterministic key (VendorKey +
/// DescriptionKeyword + AccountCode), upserts in place. Re-running
/// the seeder during deployment is safe.
/// </summary>
public interface IDistillationCorpusSeeder
{
    Task SeedAsync(CancellationToken ct);
}

public class DistillationCorpusSeeder : IDistillationCorpusSeeder
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DistillationCorpusSeeder> _logger;

    public DistillationCorpusSeeder(IServiceProvider services, ILogger<DistillationCorpusSeeder> logger)
    { _services = services; _logger = logger; }

    public async Task SeedAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        var seeds = BuildSeeds();
        var inserted = 0;
        var updated = 0;

        foreach (var s in seeds)
        {
            var existing = await db.SystemOcrCategoryMappings.FirstOrDefaultAsync(
                m => m.VendorKey == s.VendorKey
                     && m.DescriptionKeyword == s.Keyword
                     && m.AccountCode == s.AccountCode, ct);
            if (existing == null)
            {
                db.SystemOcrCategoryMappings.Add(new SystemOcrCategoryMapping
                {
                    VendorKey = s.VendorKey,
                    DescriptionKeyword = s.Keyword,
                    AccountCode = s.AccountCode,
                    TimesUsed = s.SeedWeight,
                });
                inserted++;
            }
            else if (existing.TimesUsed < s.SeedWeight)
            {
                // Bring stale TimesUsed up to the seed floor, but never
                // overwrite a higher learned value.
                existing.TimesUsed = s.SeedWeight;
                updated++;
            }
        }

        if (inserted > 0 || updated > 0)
            await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "DistillationCorpusSeeder: {Ins} inserted, {Upd} refreshed (catalogue size {Total})",
            inserted, updated, seeds.Count);
    }

    private static IReadOnlyList<SeedEntry> BuildSeeds() => new[]
    {
        // ── พลังงาน / น้ำมัน — 5402 ค่าน้ำมันเชื้อเพลิง ─────────────────
        new SeedEntry("name:ปตท", "น้ำมัน", "5402", 20),
        new SeedEntry("name:ptt", "น้ำมัน", "5402", 20),
        new SeedEntry("name:ปตท", "ดีเซล", "5402", 15),
        new SeedEntry("name:ปตท", "เบนซิน", "5402", 15),
        new SeedEntry("name:shell", "น้ำมัน", "5402", 15),
        new SeedEntry("name:เชลล์", "น้ำมัน", "5402", 15),
        new SeedEntry("name:caltex", "น้ำมัน", "5402", 12),
        new SeedEntry("name:bangchak", "น้ำมัน", "5402", 12),
        new SeedEntry("name:บางจาก", "น้ำมัน", "5402", 12),

        // ── สาธารณูปโภค — 5301 ค่าน้ำ ค่าไฟ ค่าโทรศัพท์ ────────────────
        new SeedEntry("name:การไฟฟ้านครหลวง", "ค่าไฟ", "5301", 18),
        new SeedEntry("name:กฟน", "ค่าไฟ", "5301", 18),
        new SeedEntry("name:การไฟฟ้าส่วนภูมิภาค", "ค่าไฟ", "5301", 18),
        new SeedEntry("name:กฟภ", "ค่าไฟ", "5301", 18),
        new SeedEntry("name:การประปานครหลวง", "ค่าน้ำ", "5302", 15),
        new SeedEntry("name:กปน", "ค่าน้ำ", "5302", 15),
        new SeedEntry("name:การประปาส่วนภูมิภาค", "ค่าน้ำ", "5302", 15),

        // ── โทรศัพท์ / อินเทอร์เน็ต — 5303 ────────────────────────────
        new SeedEntry("name:ais", "ค่าโทรศัพท์", "5303", 15),
        new SeedEntry("name:ais", "อินเทอร์เน็ต", "5303", 12),
        new SeedEntry("name:เอไอเอส", "ค่าโทรศัพท์", "5303", 15),
        new SeedEntry("name:true", "ค่าโทรศัพท์", "5303", 15),
        new SeedEntry("name:true", "อินเทอร์เน็ต", "5303", 15),
        new SeedEntry("name:ทรู", "อินเทอร์เน็ต", "5303", 15),
        new SeedEntry("name:dtac", "ค่าโทรศัพท์", "5303", 12),
        new SeedEntry("name:3bb", "อินเทอร์เน็ต", "5303", 12),

        // ── วัสดุ / อุปกรณ์สำนักงาน — 5305 ─────────────────────────────
        new SeedEntry("name:homepro", "วัสดุ", "5305", 10),
        new SeedEntry("name:โฮมโปร", "วัสดุ", "5305", 10),
        new SeedEntry("name:thaiwatsadu", "วัสดุก่อสร้าง", "5305", 10),
        new SeedEntry("name:ไทวัสดุ", "วัสดุก่อสร้าง", "5305", 10),
        new SeedEntry("name:officemate", "อุปกรณ์สำนักงาน", "5306", 12),
        new SeedEntry("name:ออฟฟิศเมท", "อุปกรณ์สำนักงาน", "5306", 12),
        new SeedEntry("name:officemate", "เครื่องเขียน", "5306", 12),
        new SeedEntry("name:b2s", "หนังสือ", "5306", 8),

        // ── ขนส่ง / โลจิสติกส์ — 5404 ─────────────────────────────────
        new SeedEntry("name:kerry", "ขนส่ง", "5404", 12),
        new SeedEntry("name:เคอรี่", "ขนส่ง", "5404", 12),
        new SeedEntry("name:flash", "ขนส่ง", "5404", 12),
        new SeedEntry("name:แฟลช", "ขนส่ง", "5404", 12),
        new SeedEntry("name:j&t", "ขนส่ง", "5404", 10),
        new SeedEntry("name:เจแอนด์ที", "ขนส่ง", "5404", 10),
        new SeedEntry("name:thailand post", "ไปรษณีย์", "5404", 10),
        new SeedEntry("name:ไปรษณีย์ไทย", "ไปรษณีย์", "5404", 10),

        // ── ค่าธรรมเนียมธนาคาร — 5701 ─────────────────────────────────
        new SeedEntry("name:scb", "ค่าธรรมเนียม", "5701", 10),
        new SeedEntry("name:kbank", "ค่าธรรมเนียม", "5701", 10),
        new SeedEntry("name:กสิกร", "ค่าธรรมเนียม", "5701", 10),
        new SeedEntry("name:ไทยพาณิชย์", "ค่าธรรมเนียม", "5701", 10),
        new SeedEntry("name:bbl", "ค่าธรรมเนียม", "5701", 8),
        new SeedEntry("name:กรุงเทพ", "ค่าธรรมเนียม", "5701", 8),

        // ── โฆษณา / การตลาด — 5501 ───────────────────────────────────
        new SeedEntry("name:facebook", "โฆษณา", "5501", 12),
        new SeedEntry("name:google", "โฆษณา", "5501", 12),
        new SeedEntry("name:tiktok", "โฆษณา", "5501", 10),
        new SeedEntry("name:line", "โฆษณา", "5501", 8),

        // ── ค่าเช่าและบริการ — 5102 ───────────────────────────────────
        new SeedEntry("name:cpaxtra", "ค่าเช่า", "5102", 8),
        new SeedEntry("name:agoda", "ค่าที่พัก", "5407", 8),
        new SeedEntry("name:booking", "ค่าที่พัก", "5407", 8),

        // ── อาหารและเครื่องดื่ม (เลี้ยงรับรอง) — 5408 ──────────────────
        new SeedEntry("name:starbucks", "เครื่องดื่ม", "5408", 8),
        new SeedEntry("name:สตาร์บัคส์", "เครื่องดื่ม", "5408", 8),
        new SeedEntry("name:7-11", "เครื่องใช้สำนักงาน", "5306", 6),
        new SeedEntry("name:เซเว่น", "เครื่องใช้สำนักงาน", "5306", 6),

        // ── คอมพิวเตอร์ / ซอฟต์แวร์ — 5306 / 5602 ────────────────────
        new SeedEntry("name:microsoft", "ซอฟต์แวร์", "5306", 10),
        new SeedEntry("name:adobe", "ซอฟต์แวร์", "5306", 10),
        new SeedEntry("name:apple", "อุปกรณ์", "1505", 8),     // 1505 = Equipment asset
        new SeedEntry("name:lazada", "อุปกรณ์สำนักงาน", "5306", 6),
        new SeedEntry("name:shopee", "อุปกรณ์สำนักงาน", "5306", 6),

        // ── ภาษี / ค่าธรรมเนียมราชการ — 5703 ─────────────────────────
        new SeedEntry("name:กรมสรรพากร", "ภาษี", "5703", 15),
        new SeedEntry("name:สำนักงานประกันสังคม", "ประกันสังคม", "2402", 15),
        new SeedEntry("name:dbd", "ค่าธรรมเนียม", "5703", 8),
    };

    private sealed record SeedEntry(string VendorKey, string Keyword, string AccountCode, int SeedWeight);
}
