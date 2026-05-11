using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Cold-start seeder: populate the three system-wide knowledge tables
/// (SystemOcrCategoryMappings, SystemOcrVendorIntelligence,
/// SystemOcrAssociationRules) with hand-curated Thai-SME defaults so
/// brand-new tenants get reasonable predictions on day one — BEFORE
/// they've trained the system with their own data.
///
/// Coverage (top ~40 Thai vendor brands SMEs commonly invoice from):
///   • Fuel: PTT / Bangchak / Shell / Esso / Caltex / Susco
///   • Utilities: การไฟฟ้านครหลวง/ส่วนภูมิภาค (MEA/PEA),
///                การประปานครหลวง/ส่วนภูมิภาค (MWA/PWA)
///   • Telecom: AIS / True / DTAC / TOT / 3BB / NT
///   • Office supplies: Office Mate / B2S / SE-ED
///   • Hardware / construction: HomePro / ThaiWatsadu / Global House /
///                              Do Home / Index Living Mall
///   • Logistics: Kerry / Flash / J&T / Thai Post / DHL / FedEx / Ninja
///   • Advertising: Google / Facebook / TikTok / LINE Ads
///   • Travel: Grab / Bolt / Thai Airways / AirAsia / Nok Air /
///             Bangkok Airways / Agoda / Booking
///   • Banking: SCB / KBANK / BBL / KTB / BAY / TTB / UOB
///   • Insurance: AIA / Muang Thai / Viriyah / FWD / Tipayanapha
///   • Cloud / IT: AWS / Microsoft / Google Cloud / DigitalOcean
///
/// Idempotency: each table is checked separately. The seeder only adds
/// missing rows — it never updates or deletes existing data, so an
/// admin who has hand-tuned a row keeps their version.
///
/// Run-mode: triggered by POST /admin/ocr-config/seed-knowledge OR
/// auto-runs on app startup when SystemOcrAssociationRules has 0 rows
/// (controlled by appsettings flag — opt-in).
/// </summary>
public class SystemOcrKnowledgeSeeder
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<SystemOcrKnowledgeSeeder> _logger;

    public SystemOcrKnowledgeSeeder(AccountingDbContext db, ILogger<SystemOcrKnowledgeSeeder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record SeedResult(int CategoryMappings, int VendorIntelligence, int AssociationRules);

    public async Task<SeedResult> SeedAsync(bool overwrite = false, CancellationToken ct = default)
    {
        int catAdded = await SeedCategoryMappingsAsync(overwrite, ct);
        int viAdded = await SeedVendorIntelligenceAsync(overwrite, ct);
        int arAdded = await SeedAssociationRulesAsync(overwrite, ct);
        _logger.LogInformation("System OCR knowledge seeded: cat+{C} vi+{V} ar+{A}", catAdded, viAdded, arAdded);
        return new SeedResult(catAdded, viAdded, arAdded);
    }

    // ─────────────────────────────────────────────────────────────────
    // SystemOcrCategoryMappings — (vendor name → keyword → account)
    // ─────────────────────────────────────────────────────────────────
    private async Task<int> SeedCategoryMappingsAsync(bool overwrite, CancellationToken ct)
    {
        // For each VendorAlias × KeywordAlias × Account combo we want a row.
        // VendorAlias covers OCR variations a Thai SME might see: short brand,
        // long official name, English form, with/without prefix.
        var seeds = BuildCategorySeeds();
        int added = 0;
        foreach (var s in seeds)
        {
            foreach (var vendorAlias in s.VendorAliases)
            {
                var vendorKey = $"name:{vendorAlias.Trim().ToLowerInvariant()}";
                foreach (var kwAlias in s.KeywordAliases)
                {
                    var keyword = kwAlias.Trim().ToLowerInvariant();
                    var exists = await _db.SystemOcrCategoryMappings
                        .AnyAsync(m => m.VendorKey == vendorKey
                            && m.DescriptionKeyword == keyword
                            && m.AccountCode == s.AccountCode && !m.IsDeleted, ct);
                    if (exists && !overwrite) continue;
                    if (exists && overwrite) continue;   // never overwrite — leave admin tuning alone

                    _db.SystemOcrCategoryMappings.Add(new SystemOcrCategoryMapping
                    {
                        VendorKey = vendorKey,
                        DescriptionKeyword = keyword,
                        AccountCode = s.AccountCode,
                        AccountName = s.AccountName,
                        TimesUsed = 10,    // seed weight — equivalent to "we've seen this 10 times"
                        LastUsedAt = DateTime.UtcNow,
                        CreatedBy = "system-seed",
                    });
                    added++;
                }
            }
        }
        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }

    // ─────────────────────────────────────────────────────────────────
    // SystemOcrVendorIntelligence — per-vendor typical doc type / WHT habits
    // ─────────────────────────────────────────────────────────────────
    private async Task<int> SeedVendorIntelligenceAsync(bool overwrite, CancellationToken ct)
    {
        var seeds = BuildVendorIntelligenceSeeds();
        int added = 0;
        foreach (var s in seeds)
        {
            foreach (var alias in s.VendorAliases)
            {
                var key = $"name:{alias.Trim().ToLowerInvariant()}";
                var existing = await _db.SystemOcrVendorIntelligence
                    .FirstOrDefaultAsync(v => v.VendorKey == key && !v.IsDeleted, ct);
                if (existing != null && !overwrite) continue;
                if (existing != null) continue;

                _db.SystemOcrVendorIntelligence.Add(new SystemOcrVendorIntelligence
                {
                    VendorKey = key,
                    VendorName = s.CanonicalName,
                    MostCommonDocumentType = s.TargetDocType.ToString(),
                    MostCommonDocumentTypeCount = 10,
                    TotalDocuments = 10,
                    DocumentTypeBreakdownJson = JsonSerializer.Serialize(
                        new Dictionary<string, int> { [s.TargetDocType.ToString()] = 10 }),
                    MostCommonDebitAccountCode = s.DebitAccountCode,
                    MostCommonDebitAccountName = s.DebitAccountName,
                    MostCommonDebitAccountCount = 10,
                    DebitAccountBreakdownJson = JsonSerializer.Serialize(
                        new Dictionary<string, int> { [s.DebitAccountCode] = 10 }),
                    TypicallyHasWht = s.WhtRate.HasValue && s.WhtRate.Value > 0,
                    TypicalWhtRate = s.WhtRate,
                    WhtUsageCount = s.WhtRate.HasValue && s.WhtRate.Value > 0 ? 8 : 0,
                    TypicalPaymentTermsDays = s.PaymentTermsDays,
                    LastTrainedAt = DateTime.UtcNow,
                    CreatedBy = "system-seed",
                });
                added++;
            }
        }
        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }

    // ─────────────────────────────────────────────────────────────────
    // SystemOcrAssociationRules — Apriori-style {brand+keyword} → account
    // ─────────────────────────────────────────────────────────────────
    // These hit reliably because AssociationRuleMiner.FindBestMatchAsync
    // tokenizes the current scan's vendor name AND line descriptions, then
    // matches rules whose antecedent tokens are a subset. Brand tokens use
    // the "brand:" prefix; line-description tokens use "kw:".
    private async Task<int> SeedAssociationRulesAsync(bool overwrite, CancellationToken ct)
    {
        var seeds = BuildAssociationRuleSeeds();
        int added = 0;
        foreach (var s in seeds)
        {
            var antecedentJson = JsonSerializer.Serialize(s.Antecedent);
            var exists = await _db.SystemOcrAssociationRules
                .AnyAsync(r => r.AntecedentJson == antecedentJson
                    && r.Consequent == s.Consequent && !r.IsDeleted, ct);
            if (exists) continue;

            _db.SystemOcrAssociationRules.Add(new SystemOcrAssociationRule
            {
                AntecedentJson = antecedentJson,
                Consequent = s.Consequent,
                Support = 0.05m,
                Confidence = s.Confidence,
                Lift = s.Lift,
                TransactionCount = 50,    // seed evidence strength
                MinedAt = DateTime.UtcNow,
                CreatedBy = "system-seed",
            });
            added++;
        }
        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }

    // ─────────────────────────────────────────────────────────────────
    // SEED DATA
    // ─────────────────────────────────────────────────────────────────

    private record CategorySeed(string[] VendorAliases, string[] KeywordAliases, string AccountCode, string AccountName);
    private record VendorIntelSeed(string CanonicalName, string[] VendorAliases,
        DocumentType TargetDocType, string DebitAccountCode, string DebitAccountName,
        decimal? WhtRate, int? PaymentTermsDays);
    private record AssociationSeed(string[] Antecedent, string Consequent, decimal Confidence, decimal Lift);

    private static List<CategorySeed> BuildCategorySeeds() => new()
    {
        // ─── Fuel — 5402 ───
        new(new[] { "ปตท", "ptt", "pttor", "บริษัท ปตท จำกัด (มหาชน)", "บริษัท ปตท. น้ำมันและการค้าปลีก จำกัด (มหาชน)" },
            new[] { "น้ำมัน", "เบนซิน", "ดีเซล", "fuel", "petrol" }, "5402", "ค่าน้ำมันเชื้อเพลิง"),
        new(new[] { "บางจาก", "bangchak", "bcp", "บริษัท บางจาก คอร์ปอเรชั่น จำกัด (มหาชน)" },
            new[] { "น้ำมัน", "เบนซิน", "ดีเซล", "แก๊สโซฮอล์" }, "5402", "ค่าน้ำมันเชื้อเพลิง"),
        new(new[] { "shell", "เชลล์", "บริษัท เชลล์แห่งประเทศไทย จำกัด" },
            new[] { "น้ำมัน", "fuel", "diesel", "petrol" }, "5402", "ค่าน้ำมันเชื้อเพลิง"),
        new(new[] { "esso", "บริษัท เอสโซ่ (ประเทศไทย) จำกัด (มหาชน)" },
            new[] { "น้ำมัน", "fuel" }, "5402", "ค่าน้ำมันเชื้อเพลิง"),
        new(new[] { "caltex", "คาลเท็กซ์" }, new[] { "น้ำมัน", "fuel" }, "5402", "ค่าน้ำมันเชื้อเพลิง"),
        new(new[] { "susco" }, new[] { "น้ำมัน" }, "5402", "ค่าน้ำมันเชื้อเพลิง"),

        // ─── Electricity — 5303 ───
        new(new[] { "การไฟฟ้านครหลวง", "mea", "metropolitan electricity authority" },
            new[] { "ค่าไฟ", "ไฟฟ้า", "electricity" }, "5303", "ค่าไฟฟ้า"),
        new(new[] { "การไฟฟ้าส่วนภูมิภาค", "pea", "provincial electricity authority" },
            new[] { "ค่าไฟ", "ไฟฟ้า", "electricity" }, "5303", "ค่าไฟฟ้า"),

        // ─── Water — 5302 ───
        new(new[] { "การประปานครหลวง", "mwa", "metropolitan waterworks authority" },
            new[] { "ค่าน้ำ", "น้ำประปา", "water" }, "5302", "ค่าน้ำประปา"),
        new(new[] { "การประปาส่วนภูมิภาค", "pwa", "provincial waterworks authority" },
            new[] { "ค่าน้ำ", "น้ำประปา", "water" }, "5302", "ค่าน้ำประปา"),

        // ─── Telecom — 5304 ───
        new(new[] { "ais", "เอไอเอส", "บริษัท แอดวานซ์ อินโฟร์ เซอร์วิส จำกัด (มหาชน)",
                    "advanced info service", "บริษัท แอดวานซ์ ไวร์เลส เน็ทเวอร์ค จำกัด" },
            new[] { "โทรศัพท์", "อินเทอร์เน็ต", "internet", "ค่าบริการ", "ค่าสัญญาณ" },
            "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต"),
        new(new[] { "true", "ทรู", "บริษัท ทรู คอร์ปอเรชั่น จำกัด (มหาชน)", "true corporation",
                    "บริษัท ทรู มูฟ เอช ยูนิเวอร์แซล คอมมิวนิเคชั่น จำกัด" },
            new[] { "โทรศัพท์", "อินเทอร์เน็ต", "internet" }, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต"),
        new(new[] { "dtac", "ดีแทค", "บริษัท ดีแทค ไตรเน็ต จำกัด" },
            new[] { "โทรศัพท์", "อินเทอร์เน็ต" }, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต"),
        new(new[] { "tot", "ทีโอที", "บริษัท ทีโอที จำกัด (มหาชน)" },
            new[] { "โทรศัพท์", "อินเทอร์เน็ต" }, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต"),
        new(new[] { "3bb", "บริษัท ทริปเปิลที บรอดแบนด์ จำกัด" },
            new[] { "อินเทอร์เน็ต", "internet", "fiber" }, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต"),
        new(new[] { "nt", "เอ็นที", "บริษัท โทรคมนาคมแห่งชาติ จำกัด (มหาชน)", "national telecom" },
            new[] { "โทรศัพท์", "อินเทอร์เน็ต" }, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต"),

        // ─── Office supplies — 5305 ───
        new(new[] { "office mate", "ออฟฟิศเมท", "บริษัท ออฟฟิศเมท (ไทย) จำกัด", "officemate" },
            new[] { "วัสดุสำนักงาน", "office supplies", "กระดาษ", "ปากกา", "หมึก" },
            "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "b2s", "บีทูเอส", "บริษัท บีทูเอส จำกัด" },
            new[] { "วัสดุสำนักงาน", "หนังสือ", "stationery" }, "5305", "ค่าวัสดุสำนักงาน"),

        // ─── Hardware / construction material — 5305 (office) or 5306 (repair) ───
        new(new[] { "homepro", "โฮมโปร", "บริษัท โฮม โปรดักส์ เซ็นเตอร์ จำกัด (มหาชน)", "home pro" },
            new[] { "วัสดุ", "ของใช้", "ก่อสร้าง" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "thaiwatsadu", "ไทยวัสดุ", "บริษัท สยามโกลบอลเฮ้าส์ จำกัด (มหาชน)" },
            new[] { "วัสดุ", "ก่อสร้าง" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "global house", "บริษัท สยามโกลบอลเฮ้าส์ จำกัด (มหาชน)" },
            new[] { "วัสดุ", "ก่อสร้าง" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "do home", "ดูโฮม", "บริษัท ดูโฮม จำกัด (มหาชน)" },
            new[] { "วัสดุ", "ก่อสร้าง" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),

        // ─── Logistics — 5102 ───
        new(new[] { "kerry", "เคอรี่", "บริษัท เคอรี่ เอ็กซ์เพรส (ประเทศไทย) จำกัด (มหาชน)" },
            new[] { "ขนส่ง", "shipping", "delivery", "จัดส่ง" }, "5102", "ค่าขนส่ง"),
        new(new[] { "flash", "แฟลช", "บริษัท แฟลช เอ็กซ์เพรส จำกัด", "flash express" },
            new[] { "ขนส่ง", "shipping" }, "5102", "ค่าขนส่ง"),
        new(new[] { "j&t", "เจแอนด์ที", "บริษัท เจแอนด์ที เอ็กซ์เพรส (ประเทศไทย) จำกัด" },
            new[] { "ขนส่ง", "shipping" }, "5102", "ค่าขนส่ง"),
        new(new[] { "thai post", "ไปรษณีย์ไทย", "บริษัท ไปรษณีย์ไทย จำกัด" },
            new[] { "ขนส่ง", "ems", "ลงทะเบียน", "พัสดุ" }, "5102", "ค่าขนส่ง"),
        new(new[] { "dhl", "บริษัท ดีเอชแอล" }, new[] { "ขนส่ง", "freight", "international" }, "5102", "ค่าขนส่ง"),
        new(new[] { "fedex", "บริษัท เฟดเอ็กซ์" }, new[] { "ขนส่ง", "express" }, "5102", "ค่าขนส่ง"),
        new(new[] { "ninja van", "ninja", "บริษัท นินจาแวน จำกัด" }, new[] { "ขนส่ง" }, "5102", "ค่าขนส่ง"),

        // ─── Advertising — 5101 ───
        new(new[] { "google", "google asia pacific", "google ads", "google llc" },
            new[] { "ads", "โฆษณา", "advertising", "search ads" }, "5101", "ค่าโฆษณาและส่งเสริมการขาย"),
        new(new[] { "facebook", "meta", "meta platforms", "facebook ads" },
            new[] { "ads", "โฆษณา", "social media" }, "5101", "ค่าโฆษณาและส่งเสริมการขาย"),
        new(new[] { "tiktok", "tiktok ads", "tiktok pte" },
            new[] { "ads", "โฆษณา" }, "5101", "ค่าโฆษณาและส่งเสริมการขาย"),
        new(new[] { "line", "line ads", "line corp", "line man" },
            new[] { "ads", "โฆษณา" }, "5101", "ค่าโฆษณาและส่งเสริมการขาย"),

        // ─── Travel — 5401 ───
        new(new[] { "grab", "grab taxi", "grabthai", "บริษัท แกร็บแท็กซี่ (ประเทศไทย) จำกัด" },
            new[] { "เดินทาง", "taxi", "rider", "delivery" }, "5401", "ค่าเดินทาง"),
        new(new[] { "bolt" }, new[] { "เดินทาง", "taxi" }, "5401", "ค่าเดินทาง"),
        new(new[] { "thai airways", "การบินไทย", "บริษัท การบินไทย จำกัด (มหาชน)" },
            new[] { "เดินทาง", "สายการบิน", "ตั๋ว", "flight" }, "5401", "ค่าเดินทาง"),
        new(new[] { "airasia", "แอร์เอเชีย", "บริษัท ไทยแอร์เอเชีย จำกัด" },
            new[] { "เดินทาง", "สายการบิน", "flight" }, "5401", "ค่าเดินทาง"),
        new(new[] { "nokair", "นกแอร์", "บริษัท สายการบินนกแอร์ จำกัด (มหาชน)" },
            new[] { "เดินทาง", "สายการบิน" }, "5401", "ค่าเดินทาง"),
        new(new[] { "bangkok airways", "บางกอกแอร์เวย์ส" }, new[] { "เดินทาง", "สายการบิน" }, "5401", "ค่าเดินทาง"),
        new(new[] { "agoda" }, new[] { "ที่พัก", "hotel", "booking" }, "5401", "ค่าเดินทาง"),
        new(new[] { "booking.com", "booking" }, new[] { "ที่พัก", "hotel" }, "5401", "ค่าเดินทาง"),

        // ─── Banking fees — 5503 ───
        new(new[] { "scb", "ไทยพาณิชย์", "ธนาคารไทยพาณิชย์ จำกัด (มหาชน)" },
            new[] { "ค่าธรรมเนียม", "bank fee", "ค่าโอน" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "kbank", "กสิกร", "กสิกรไทย", "ธนาคารกสิกรไทย จำกัด (มหาชน)" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "bbl", "กรุงเทพ", "ธนาคารกรุงเทพ จำกัด (มหาชน)" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "ktb", "กรุงไทย", "ธนาคารกรุงไทย จำกัด (มหาชน)" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "bay", "กรุงศรี", "ธนาคารกรุงศรีอยุธยา จำกัด (มหาชน)", "krungsri" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "ttb", "ทีทีบี", "ทีเอ็มบีธนชาต", "ธนาคารทหารไทยธนชาต จำกัด (มหาชน)" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),

        // ─── Insurance — 5800 ───
        new(new[] { "aia", "เอไอเอ", "บริษัท เอไอเอ จำกัด" },
            new[] { "ประกัน", "insurance", "เบี้ยประกัน" }, "5800", "ค่าใช้จ่ายในการประกัน"),
        new(new[] { "เมืองไทยประกัน", "muang thai", "บริษัท เมืองไทยประกันชีวิต จำกัด (มหาชน)" },
            new[] { "ประกัน", "ประกันชีวิต" }, "5800", "ค่าใช้จ่ายในการประกัน"),
        new(new[] { "viriyah", "วิริยะ", "บริษัท วิริยะประกันภัย จำกัด (มหาชน)" },
            new[] { "ประกัน", "ประกันภัย", "พรบ", "ประกันรถ" }, "5800", "ค่าใช้จ่ายในการประกัน"),
        new(new[] { "fwd" }, new[] { "ประกัน" }, "5800", "ค่าใช้จ่ายในการประกัน"),
        new(new[] { "tipayanapha", "ทิพยประกัน", "บริษัท ทิพยประกันภัย จำกัด (มหาชน)" },
            new[] { "ประกัน", "พรบ" }, "5800", "ค่าใช้จ่ายในการประกัน"),

        // ─── Cloud / IT — 5304 (telecom-adjacent) ───
        new(new[] { "aws", "amazon web services", "amazon" },
            new[] { "cloud", "hosting", "compute" }, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต"),
        new(new[] { "microsoft", "azure", "office 365", "microsoft 365" },
            new[] { "cloud", "subscription", "software" }, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต"),
        new(new[] { "google cloud", "google workspace", "gcp" },
            new[] { "cloud", "subscription" }, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต"),
        new(new[] { "digitalocean", "digital ocean" }, new[] { "cloud", "hosting" }, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต"),
    };

    private static List<VendorIntelSeed> BuildVendorIntelligenceSeeds() => new()
    {
        // Most Thai utility / retail invoices are paid immediately → PaymentVoucher
        new("ปตท. น้ำมันและการค้าปลีก จำกัด (มหาชน)",
            new[] { "ปตท", "ptt", "pttor", "บริษัท ปตท จำกัด (มหาชน)", "บริษัท ปตท. น้ำมันและการค้าปลีก จำกัด (มหาชน)" },
            DocumentType.PaymentVoucher, "5402", "ค่าน้ำมันเชื้อเพลิง", null, 0),
        new("บางจาก คอร์ปอเรชั่น", new[] { "บางจาก", "bangchak", "bcp" },
            DocumentType.PaymentVoucher, "5402", "ค่าน้ำมันเชื้อเพลิง", null, 0),
        new("Shell ประเทศไทย", new[] { "shell", "เชลล์" },
            DocumentType.PaymentVoucher, "5402", "ค่าน้ำมันเชื้อเพลิง", null, 0),

        new("การไฟฟ้านครหลวง", new[] { "การไฟฟ้านครหลวง", "mea" },
            DocumentType.PaymentVoucher, "5303", "ค่าไฟฟ้า", null, 15),
        new("การไฟฟ้าส่วนภูมิภาค", new[] { "การไฟฟ้าส่วนภูมิภาค", "pea" },
            DocumentType.PaymentVoucher, "5303", "ค่าไฟฟ้า", null, 15),
        new("การประปานครหลวง", new[] { "การประปานครหลวง", "mwa" },
            DocumentType.PaymentVoucher, "5302", "ค่าน้ำประปา", null, 15),
        new("การประปาส่วนภูมิภาค", new[] { "การประปาส่วนภูมิภาค", "pwa" },
            DocumentType.PaymentVoucher, "5302", "ค่าน้ำประปา", null, 15),

        // Telecom — 3% WHT applies to service contracts
        new("AIS", new[] { "ais", "เอไอเอส", "advanced info service" },
            DocumentType.PaymentVoucher, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต", 3m, 15),
        new("True Corporation", new[] { "true", "ทรู" },
            DocumentType.PaymentVoucher, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต", 3m, 15),
        new("DTAC", new[] { "dtac", "ดีแทค" },
            DocumentType.PaymentVoucher, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต", 3m, 15),
        new("3BB", new[] { "3bb" }, DocumentType.PaymentVoucher, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต", 3m, 30),

        // Office supplies — purchase invoice with credit terms
        new("Office Mate", new[] { "office mate", "ออฟฟิศเมท", "officemate" },
            DocumentType.PurchaseInvoice, "5305", "ค่าวัสดุสำนักงาน", null, 30),
        new("HomePro", new[] { "homepro", "โฮมโปร", "home pro" },
            DocumentType.PaymentVoucher, "5305", "ค่าวัสดุสำนักงาน", null, 0),
        new("ThaiWatsadu", new[] { "thaiwatsadu", "ไทยวัสดุ" },
            DocumentType.PaymentVoucher, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 0),
        new("Global House", new[] { "global house" },
            DocumentType.PaymentVoucher, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 0),

        // Logistics — 1% WHT for transportation services
        new("Kerry Express", new[] { "kerry", "เคอรี่" },
            DocumentType.PaymentVoucher, "5102", "ค่าขนส่ง", 1m, 0),
        new("Flash Express", new[] { "flash", "แฟลช", "flash express" },
            DocumentType.PaymentVoucher, "5102", "ค่าขนส่ง", 1m, 0),
        new("J&T Express", new[] { "j&t", "เจแอนด์ที" },
            DocumentType.PaymentVoucher, "5102", "ค่าขนส่ง", 1m, 0),
        new("Thai Post", new[] { "thai post", "ไปรษณีย์ไทย" },
            DocumentType.PaymentVoucher, "5102", "ค่าขนส่ง", null, 0),

        // Advertising — 2% WHT
        new("Google Asia Pacific", new[] { "google", "google ads" },
            DocumentType.PurchaseInvoice, "5101", "ค่าโฆษณาและส่งเสริมการขาย", 2m, 30),
        new("Meta", new[] { "facebook", "meta", "meta platforms" },
            DocumentType.PurchaseInvoice, "5101", "ค่าโฆษณาและส่งเสริมการขาย", 2m, 30),
        new("TikTok", new[] { "tiktok", "tiktok ads" },
            DocumentType.PurchaseInvoice, "5101", "ค่าโฆษณาและส่งเสริมการขาย", 2m, 30),

        // Travel
        new("Grab", new[] { "grab" },
            DocumentType.PaymentVoucher, "5401", "ค่าเดินทาง", null, 0),
        new("Thai Airways", new[] { "thai airways", "การบินไทย" },
            DocumentType.PurchaseInvoice, "5401", "ค่าเดินทาง", null, 30),
        new("Agoda", new[] { "agoda" },
            DocumentType.PaymentVoucher, "5401", "ค่าเดินทาง", null, 0),

        // Banking fees
        new("SCB", new[] { "scb", "ไทยพาณิชย์" },
            DocumentType.PaymentVoucher, "5503", "ค่าธรรมเนียมธนาคาร", null, 0),
        new("KBANK", new[] { "kbank", "กสิกร", "กสิกรไทย" },
            DocumentType.PaymentVoucher, "5503", "ค่าธรรมเนียมธนาคาร", null, 0),

        // Insurance
        new("Viriyah Insurance", new[] { "viriyah", "วิริยะ" },
            DocumentType.PurchaseInvoice, "5800", "ค่าใช้จ่ายในการประกัน", null, 30),
        new("AIA", new[] { "aia", "เอไอเอ" },
            DocumentType.PurchaseInvoice, "5800", "ค่าใช้จ่ายในการประกัน", null, 30),

        // Cloud / IT
        new("Amazon Web Services", new[] { "aws", "amazon web services" },
            DocumentType.PurchaseInvoice, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต", null, 30),
        new("Microsoft", new[] { "microsoft", "azure", "office 365" },
            DocumentType.PurchaseInvoice, "5304", "ค่าโทรศัพท์และอินเทอร์เน็ต", null, 30),
    };

    private static List<AssociationSeed> BuildAssociationRuleSeeds() => new()
    {
        // High-confidence brand+keyword → account combos. Lift > 5 means
        // the antecedent is FAR more predictive than picking the account
        // at random — these are essentially deterministic mappings.

        // Fuel
        new(new[] { "brand:ปตท" }, "acct:5402", 0.95m, 8m),
        new(new[] { "brand:ptt" }, "acct:5402", 0.92m, 7.5m),
        new(new[] { "brand:บางจาก" }, "acct:5402", 0.95m, 8m),
        new(new[] { "brand:bangchak" }, "acct:5402", 0.92m, 7.5m),
        new(new[] { "brand:shell" }, "acct:5402", 0.92m, 7m),
        new(new[] { "brand:เชลล์" }, "acct:5402", 0.92m, 7m),
        new(new[] { "brand:esso" }, "acct:5402", 0.9m, 6.5m),
        new(new[] { "brand:caltex" }, "acct:5402", 0.9m, 6.5m),
        new(new[] { "kw:น้ำมัน", "kw:เบนซิน" }, "acct:5402", 0.85m, 5m),
        new(new[] { "kw:น้ำมัน", "kw:ดีเซล" }, "acct:5402", 0.85m, 5m),

        // Electricity
        new(new[] { "brand:การไฟฟ้านครหลวง" }, "acct:5303", 0.98m, 12m),
        new(new[] { "brand:การไฟฟ้าส่วนภูมิภาค" }, "acct:5303", 0.98m, 12m),
        new(new[] { "brand:mea" }, "acct:5303", 0.95m, 10m),
        new(new[] { "brand:pea" }, "acct:5303", 0.95m, 10m),

        // Water
        new(new[] { "brand:การประปานครหลวง" }, "acct:5302", 0.98m, 12m),
        new(new[] { "brand:การประปาส่วนภูมิภาค" }, "acct:5302", 0.98m, 12m),
        new(new[] { "brand:mwa" }, "acct:5302", 0.95m, 10m),
        new(new[] { "brand:pwa" }, "acct:5302", 0.95m, 10m),

        // Telecom
        new(new[] { "brand:ais" }, "acct:5304", 0.92m, 7m),
        new(new[] { "brand:เอไอเอส" }, "acct:5304", 0.92m, 7m),
        new(new[] { "brand:true" }, "acct:5304", 0.9m, 6.5m),
        new(new[] { "brand:ทรู" }, "acct:5304", 0.9m, 6.5m),
        new(new[] { "brand:dtac" }, "acct:5304", 0.92m, 7m),
        new(new[] { "brand:ดีแทค" }, "acct:5304", 0.92m, 7m),
        new(new[] { "brand:3bb" }, "acct:5304", 0.95m, 8m),

        // Office supplies
        new(new[] { "brand:officemate" }, "acct:5305", 0.95m, 9m),
        new(new[] { "brand:office", "brand:mate" }, "acct:5305", 0.93m, 8m),
        new(new[] { "brand:b2s" }, "acct:5305", 0.9m, 7m),

        // Hardware / construction
        new(new[] { "brand:homepro" }, "acct:5305", 0.85m, 5m),
        new(new[] { "brand:โฮมโปร" }, "acct:5305", 0.85m, 5m),
        new(new[] { "brand:thaiwatsadu" }, "acct:5306", 0.85m, 5m),
        new(new[] { "brand:ไทยวัสดุ" }, "acct:5306", 0.85m, 5m),
        new(new[] { "brand:do", "brand:home" }, "acct:5306", 0.8m, 4.5m),
        new(new[] { "brand:globalhouse" }, "acct:5306", 0.85m, 5m),

        // Logistics
        new(new[] { "brand:kerry" }, "acct:5102", 0.95m, 9m),
        new(new[] { "brand:เคอรี่" }, "acct:5102", 0.95m, 9m),
        new(new[] { "brand:flash" }, "acct:5102", 0.92m, 8m),
        new(new[] { "brand:แฟลช" }, "acct:5102", 0.92m, 8m),
        new(new[] { "brand:j&t" }, "acct:5102", 0.9m, 7.5m),
        new(new[] { "brand:ไปรษณีย์ไทย" }, "acct:5102", 0.92m, 8m),
        new(new[] { "brand:dhl" }, "acct:5102", 0.9m, 7m),
        new(new[] { "brand:ninja" }, "acct:5102", 0.85m, 6m),

        // Advertising
        new(new[] { "brand:google", "kw:ads" }, "acct:5101", 0.92m, 8m),
        new(new[] { "brand:facebook", "kw:ads" }, "acct:5101", 0.92m, 8m),
        new(new[] { "brand:meta" }, "acct:5101", 0.85m, 6m),
        new(new[] { "brand:tiktok", "kw:ads" }, "acct:5101", 0.9m, 7m),

        // Travel
        new(new[] { "brand:grab" }, "acct:5401", 0.9m, 7m),
        new(new[] { "brand:airasia" }, "acct:5401", 0.92m, 7.5m),
        new(new[] { "brand:nokair" }, "acct:5401", 0.92m, 7.5m),
        new(new[] { "brand:agoda" }, "acct:5401", 0.92m, 7.5m),
        new(new[] { "brand:booking.com" }, "acct:5401", 0.92m, 7.5m),
        new(new[] { "brand:thai", "brand:airways" }, "acct:5401", 0.92m, 8m),

        // Banking — note: kw match because bank fees often appear in
        // line description of statement uploads.
        new(new[] { "kw:ค่าธรรมเนียม", "kw:ธนาคาร" }, "acct:5503", 0.85m, 6m),
        new(new[] { "brand:scb" }, "acct:5503", 0.8m, 5m),
        new(new[] { "brand:kbank" }, "acct:5503", 0.8m, 5m),
        new(new[] { "brand:bbl" }, "acct:5503", 0.8m, 5m),

        // Insurance
        new(new[] { "kw:ประกันภัย" }, "acct:5800", 0.88m, 6.5m),
        new(new[] { "kw:เบี้ยประกัน" }, "acct:5800", 0.9m, 7m),
        new(new[] { "brand:viriyah" }, "acct:5800", 0.92m, 8m),
        new(new[] { "brand:วิริยะ" }, "acct:5800", 0.92m, 8m),
        new(new[] { "brand:aia" }, "acct:5800", 0.9m, 7m),

        // Cloud / IT subscription
        new(new[] { "brand:aws" }, "acct:5304", 0.9m, 7m),
        new(new[] { "brand:amazon", "kw:cloud" }, "acct:5304", 0.88m, 6.5m),
        new(new[] { "brand:microsoft", "kw:subscription" }, "acct:5304", 0.88m, 6.5m),
        new(new[] { "brand:azure" }, "acct:5304", 0.9m, 7m),
        new(new[] { "brand:digitalocean" }, "acct:5304", 0.95m, 9m),
    };
}
