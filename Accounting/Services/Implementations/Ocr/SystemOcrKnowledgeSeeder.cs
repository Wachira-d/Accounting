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
/// Coverage (~180 Thai vendor brands across all 18 IndustryType values,
/// expanded in 10 iterative rounds for end-to-end SME coverage):
///   • Fuel: PTT / Bangchak / Shell / Esso / Caltex / Susco
///   • Utilities: การไฟฟ้านครหลวง/ส่วนภูมิภาค (MEA/PEA),
///                การประปานครหลวง/ส่วนภูมิภาค (MWA/PWA)
///   • Telecom: AIS / True / DTAC / TOT / 3BB / NT
///   • Office supplies: Office Mate / B2S / SE-ED / Aksorn
///   • Construction: SCG / TPI Polene / Tata Steel / Boon Thavorn /
///                   HomePro / ThaiWatsadu / Global House / Do Home /
///                   Index Living / Modernform / IKEA
///   • Restaurant/Cafe supply: Makro / Lotus's / Big C / Foodland /
///                             Tops / CP Foods / Betagro / Coca-Cola /
///                             Pepsi / Singha / Chang / Café Amazon /
///                             Starbucks / 7-Eleven / FamilyMart
///   • Food delivery: GrabFood / LineMan / Foodpanda / Robinhood
///   • Logistics: Kerry / Flash / J&T / Thai Post / DHL / FedEx / Ninja
///   • Ecommerce: Lazada / Shopee / TikTok Shop / JD Central
///   • Payment gateway: Omise / 2C2P / GBPrimePay / Stripe / PayPal
///   • Advertising: Google / Facebook / TikTok / LINE Ads
///   • Travel: Grab / Bolt / Thai Airways / AirAsia / Nok Air /
///             Bangkok Airways / Agoda / Booking / BTS / MRT / ARL /
///             Expressway / AOT
///   • Banking: SCB / KBANK / BBL / KTB / BAY / TTB / UOB / GSB /
///              BAAC / HSBC / Standard Chartered / Citibank / ICBC
///   • Insurance: AIA / Muang Thai / Viriyah / FWD / Tipayanapha /
///                AXA / Generali / KrungThai-AXA
///   • Manufacturing: PTT Global Chem / IRPC / Indorama / WHA / Amata /
///                    Mitsubishi Electric / Hitachi / Komatsu / Caterpillar
///   • Healthcare: Pfizer / GSK / Sanofi / Novartis / Roche / Abbott /
///                 B.L. Hua / Berlin / BDMS / Bangkok Hospital /
///                 Bumrungrad / Samitivej / BCH / 3M / J&amp;J
///   • Real Estate: AP / Sansiri / Pruksa / L&amp;H / Supalai /
///                  CBRE / JLL / Knight Frank / Colliers
///   • Hotel: Minor / Centara / Dusit / Marriott / Hilton / Hyatt /
///            Shangri-La
///   • Technology/SaaS: JetBrains / GitHub / GitLab / Atlassian /
///                      Adobe / Zoom / Slack / Cloudflare / Netlify /
///                      Vercel / GoDaddy / NameCheap / Notion / Figma /
///                      Canva / LinkedIn / Anthropic / OpenAI / SAP /
///                      Oracle / Salesforce / Intuit
///   • Education: SE-ED / Aksorn / Mac Education / Kinokuniya /
///                Asia Books / Udemy / Coursera / Skooldio
///   • Beauty: Pond's / Unilever / L'Oreal / Estée Lauder / Shiseido /
///             Watsons / Boots / Eveandboy
///   • Agriculture: CP / Mitr Phol / Thai Roong Ruang / Syngenta /
///                  Bayer / Yara / Kubota
///   • Government fees: กรมสรรพากร / ประกันสังคม / DBD / DLT / BOI /
///                      Expressway Authority / AOT
///   • Vehicle service: Bridgestone / Michelin / Yokohama / Goodyear /
///                      Toyota Service / Honda Service / Isuzu /
///                      B-Quik / Cockpit / Fitauto
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

    public record SeedResult(int CategoryMappings, int VendorIntelligence, int AssociationRules,
        int ExistingCategoryMappings, int ExistingVendorIntelligence, int ExistingAssociationRules);

    public async Task<SeedResult> SeedAsync(bool overwrite = false, CancellationToken ct = default)
    {
        int catAdded = await SeedCategoryMappingsAsync(overwrite, ct);
        int viAdded = await SeedVendorIntelligenceAsync(overwrite, ct);
        int arAdded = await SeedAssociationRulesAsync(overwrite, ct);
        // Existing counts surface to the admin UI so "0 added" doesn't look
        // broken when seeds were already populated by the startup auto-seed.
        int catExisting = await _db.SystemOcrCategoryMappings.CountAsync(m => !m.IsDeleted, ct);
        int viExisting = await _db.SystemOcrVendorIntelligence.CountAsync(v => !v.IsDeleted, ct);
        int arExisting = await _db.SystemOcrAssociationRules.CountAsync(r => !r.IsDeleted, ct);
        _logger.LogInformation("System OCR knowledge seeded: cat+{C}/{TC} vi+{V}/{TV} ar+{A}/{TA}",
            catAdded, catExisting, viAdded, viExisting, arAdded, arExisting);
        return new SeedResult(catAdded, viAdded, arAdded, catExisting, viExisting, arExisting);
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
                var vendorKey = Accounting.Helpers.VendorLearningKey.ForName(vendorAlias);
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
                var key = Accounting.Helpers.VendorLearningKey.ForName(alias);
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

        // ═══════════════════════════════════════════════════════════════
        // EXPANSION — 10 rounds covering all 18 IndustryTypes
        // ═══════════════════════════════════════════════════════════════

        // ─── Round 1: Restaurant/Cafe — food & beverage suppliers (Industry: Restaurant=4, Cafe=5) ───
        // Cost of goods sold for restaurants is account 5001 (วัตถุดิบ/สินค้าซื้อมา)
        new(new[] { "makro", "แม็คโคร", "สยามแม็คโคร", "บริษัท สยามแม็คโคร จำกัด (มหาชน)", "siam makro" },
            new[] { "วัตถุดิบ", "อาหาร", "wholesale", "ของชำ", "วัสดุ" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "lotus", "lotus's", "โลตัส", "เทสโก้โลตัส", "tesco lotus", "บริษัท เอก-ชัย ดีสทริบิวชั่น ซิสเทม จำกัด" },
            new[] { "วัตถุดิบ", "ของชำ", "อาหาร" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "big c", "บิ๊กซี", "บริษัท บิ๊กซี ซูเปอร์เซ็นเตอร์ จำกัด (มหาชน)" },
            new[] { "วัตถุดิบ", "ของชำ", "อาหาร" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "foodland", "ฟู้ดแลนด์" },
            new[] { "อาหาร", "ของสด", "วัตถุดิบ" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "tops", "tops market", "ท็อปส์", "central food retail" },
            new[] { "อาหาร", "วัตถุดิบ" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "villa market", "วิลล่ามาร์เก็ต" },
            new[] { "อาหาร", "import" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "gourmet market", "กูร์เมต์", "central food hall" },
            new[] { "อาหาร", "premium" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "cp", "ซีพี", "เครือเจริญโภคภัณฑ์", "cp foods", "บริษัท เจริญโภคภัณฑ์อาหาร จำกัด (มหาชน)", "cpf" },
            new[] { "เนื้อไก่", "ไก่", "หมู", "อาหาร", "อาหารสัตว์" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "betagro", "เบทาโกร", "บริษัท เบทาโกร จำกัด (มหาชน)" },
            new[] { "เนื้อไก่", "หมู", "ไข่" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "coca cola", "coca-cola", "โคคา-โคลา", "บริษัท ไทยน้ำทิพย์ จำกัด", "บริษัท หาดทิพย์ จำกัด (มหาชน)" },
            new[] { "เครื่องดื่ม", "น้ำอัดลม", "โค้ก" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "pepsi", "เป๊ปซี่", "pepsico", "บริษัท เป๊ปซี่-โคล่า (ไทย) เทรดดิ้ง จำกัด" },
            new[] { "เครื่องดื่ม", "น้ำอัดลม" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "singha", "สิงห์", "บุญรอด", "boon rawd", "บริษัท บุญรอดบริวเวอรี่ จำกัด" },
            new[] { "เครื่องดื่ม", "เบียร์", "น้ำดื่ม", "โซดา" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "chang", "ช้าง", "thai beverage", "thaibev", "บริษัท ไทยเบฟเวอเรจ จำกัด (มหาชน)" },
            new[] { "เครื่องดื่ม", "เบียร์", "เหล้า", "สุรา" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "grabfood", "grab food" },
            new[] { "อาหาร", "delivery", "ค่าอาหาร" }, "5404", "ค่ารับรองและอาหาร"),
        new(new[] { "lineman", "line man", "ไลน์แมน" },
            new[] { "อาหาร", "delivery" }, "5404", "ค่ารับรองและอาหาร"),
        new(new[] { "foodpanda", "ฟู้ดแพนด้า" },
            new[] { "อาหาร", "delivery" }, "5404", "ค่ารับรองและอาหาร"),
        new(new[] { "robinhood" },
            new[] { "อาหาร", "delivery" }, "5404", "ค่ารับรองและอาหาร"),
        new(new[] { "starbucks", "สตาร์บัคส์" },
            new[] { "กาแฟ", "เครื่องดื่ม", "ค่ารับรอง" }, "5404", "ค่ารับรองและอาหาร"),
        new(new[] { "amazon", "café amazon", "คาเฟ่อเมซอน" },
            new[] { "กาแฟ", "เครื่องดื่ม" }, "5404", "ค่ารับรองและอาหาร"),
        new(new[] { "7-eleven", "7eleven", "เซเว่น", "เซเว่นอีเลฟเว่น", "cp all", "บริษัท ซีพี ออลล์ จำกัด (มหาชน)" },
            new[] { "ของใช้", "เครื่องดื่ม", "ของชำ" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "familymart", "family mart", "แฟมิลี่มาร์ท" },
            new[] { "ของใช้", "เครื่องดื่ม" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "lawson", "108", "ลอว์สัน" },
            new[] { "ของใช้" }, "5305", "ค่าวัสดุสำนักงาน"),

        // ─── Round 2: Retail/Ecommerce — marketplaces & payment gateways (Industry: Retail=6, Ecommerce=16) ───
        new(new[] { "lazada", "ลาซาด้า", "บริษัท ลาซาด้า จำกัด" },
            new[] { "commission", "ค่าธรรมเนียม", "marketplace", "ขายผ่าน" }, "5101", "ค่าโฆษณาและส่งเสริมการขาย"),
        new(new[] { "shopee", "ช้อปปี้", "บริษัท ช้อปปี้ (ประเทศไทย) จำกัด" },
            new[] { "commission", "ค่าธรรมเนียม", "marketplace" }, "5101", "ค่าโฆษณาและส่งเสริมการขาย"),
        new(new[] { "tiktok shop", "ติ๊กต๊อกช้อป" },
            new[] { "commission", "ค่าธรรมเนียม", "marketplace" }, "5101", "ค่าโฆษณาและส่งเสริมการขาย"),
        new(new[] { "jd central", "เจดี เซ็นทรัล" },
            new[] { "marketplace", "commission" }, "5101", "ค่าโฆษณาและส่งเสริมการขาย"),
        new(new[] { "central online", "central.co.th" },
            new[] { "marketplace" }, "5101", "ค่าโฆษณาและส่งเสริมการขาย"),
        new(new[] { "omise", "โอไมส์", "opn payments" },
            new[] { "payment", "gateway", "ค่าธรรมเนียม", "credit card" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "2c2p", "บริษัท 2ซีทูพี" },
            new[] { "payment", "gateway", "ค่าธรรมเนียม" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "gbprimepay", "gb prime pay" },
            new[] { "payment", "gateway", "ค่าธรรมเนียม" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "stripe" },
            new[] { "payment", "gateway", "ค่าธรรมเนียม" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "paypal", "เพย์พาล" },
            new[] { "payment", "online" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "promptpay", "พร้อมเพย์" },
            new[] { "payment", "transfer", "ค่าธรรมเนียม" }, "5503", "ค่าธรรมเนียมธนาคาร"),

        // ─── Round 3: Construction — building materials & equipment (Industry: Construction=7) ───
        new(new[] { "scg", "เอสซีจี", "ปูนซิเมนต์ไทย", "บริษัท ปูนซิเมนต์ไทย จำกัด (มหาชน)", "siam cement" },
            new[] { "ปูน", "ซีเมนต์", "วัสดุก่อสร้าง", "เหล็ก" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "tpi polene", "ทีพีไอ", "บริษัท ทีพีไอ โพลีน จำกัด (มหาชน)" },
            new[] { "ปูน", "ซีเมนต์", "ก่อสร้าง" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "ssi", "สหวิริยา", "sahaviriya", "บริษัท สหวิริยาสตีลอินดัสตรี จำกัด (มหาชน)" },
            new[] { "เหล็ก", "steel" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "tata steel", "ทาทา สตีล", "บริษัท ทาทา สตีล (ประเทศไทย) จำกัด (มหาชน)" },
            new[] { "เหล็ก", "steel" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "millcon", "มิลล์คอน", "บริษัท มิลล์คอน สตีล จำกัด (มหาชน)" },
            new[] { "เหล็ก", "steel" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "boon thavorn", "บุญถาวร" },
            new[] { "วัสดุก่อสร้าง", "กระเบื้อง", "สุขภัณฑ์" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "index living mall", "อินเด็กซ์", "index" },
            new[] { "เฟอร์นิเจอร์", "furniture" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "sb furniture", "เอสบีเฟอร์นิเจอร์" },
            new[] { "เฟอร์นิเจอร์", "furniture" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "modernform", "โมเดอร์นฟอร์ม" },
            new[] { "เฟอร์นิเจอร์", "office furniture" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "ikea", "อิเกีย", "บริษัท อิคาโน่ (ประเทศไทย) จำกัด" },
            new[] { "เฟอร์นิเจอร์", "furniture", "ของแต่งบ้าน" }, "5305", "ค่าวัสดุสำนักงาน"),

        // ─── Round 4: Manufacturing — industrial chemicals, machinery, raw materials (Industry: Manufacturing=3) ───
        new(new[] { "ptt global chemical", "pttgc", "บริษัท พีทีที โกลบอล เคมิคอล จำกัด (มหาชน)" },
            new[] { "เคมีภัณฑ์", "เม็ดพลาสติก", "พลาสติก", "raw material" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "irpc", "บริษัท ไออาร์พีซี จำกัด (มหาชน)" },
            new[] { "ปิโตรเคมี", "พลาสติก", "raw material" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "indorama", "อินโดรามา", "indorama ventures", "บริษัท อินโดรามา เวนเจอร์ส จำกัด (มหาชน)" },
            new[] { "ขวด pet", "พลาสติก", "เส้นใย" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "wha", "บริษัท ดับบลิวเอชเอ คอร์ปอเรชั่น จำกัด (มหาชน)" },
            new[] { "นิคมอุตสาหกรรม", "industrial estate", "warehouse" }, "5403", "ค่าเช่า"),
        new(new[] { "amata", "อมตะ", "บริษัท อมตะ คอร์ปอเรชัน จำกัด (มหาชน)" },
            new[] { "นิคมอุตสาหกรรม", "industrial estate" }, "5403", "ค่าเช่า"),
        new(new[] { "mitsubishi electric", "มิตซูบิชิ อีเล็คทริค" },
            new[] { "เครื่องจักร", "machinery", "อุปกรณ์ไฟฟ้า" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "hitachi", "ฮิตาชิ" },
            new[] { "เครื่องจักร", "machinery" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "siemens", "ซีเมนส์" },
            new[] { "เครื่องจักร", "automation" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "toyota forklift", "toyota material handling" },
            new[] { "forklift", "รถยก" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "komatsu", "โคมัตสุ" },
            new[] { "เครื่องจักร", "excavator", "รถขุด" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "caterpillar", "cat", "แคทเตอร์พิลล่าร์" },
            new[] { "เครื่องจักร", "construction" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),

        // ─── Round 5: Healthcare — pharma, hospitals, medical supplies (Industry: Healthcare=10) ───
        new(new[] { "pfizer", "ไฟเซอร์" },
            new[] { "ยา", "เวชภัณฑ์", "medicine", "vaccine" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "sanofi", "ซาโนฟี่" },
            new[] { "ยา", "เวชภัณฑ์" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "gsk", "glaxosmithkline", "แกล็กโซสมิทไคลน์" },
            new[] { "ยา", "เวชภัณฑ์" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "novartis", "โนวาร์ทิส" },
            new[] { "ยา", "เวชภัณฑ์" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "roche", "โรช" },
            new[] { "ยา", "เวชภัณฑ์" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "abbott", "แอ๊บบอต" },
            new[] { "ยา", "เวชภัณฑ์", "นมผง" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "b.l. hua", "บีแอลฮั้ว", "บริษัท บี.แอล.ฮั้ว จำกัด" },
            new[] { "ยา", "เวชภัณฑ์" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "berlin pharma", "เบอร์ลินฟาร์มา" },
            new[] { "ยา", "เวชภัณฑ์" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "bdms", "กรุงเทพดุสิตเวชการ", "บริษัท กรุงเทพดุสิตเวชการ จำกัด (มหาชน)" },
            new[] { "โรงพยาบาล", "รักษาพยาบาล", "ตรวจสุขภาพ" }, "5502", "ค่าสวัสดิการพนักงาน"),
        new(new[] { "bangkok hospital", "โรงพยาบาลกรุงเทพ" },
            new[] { "โรงพยาบาล", "รักษาพยาบาล" }, "5502", "ค่าสวัสดิการพนักงาน"),
        new(new[] { "bumrungrad", "บำรุงราษฎร์", "โรงพยาบาลบำรุงราษฎร์" },
            new[] { "โรงพยาบาล", "รักษาพยาบาล" }, "5502", "ค่าสวัสดิการพนักงาน"),
        new(new[] { "samitivej", "สมิติเวช" },
            new[] { "โรงพยาบาล" }, "5502", "ค่าสวัสดิการพนักงาน"),
        new(new[] { "bch", "เกษมราษฎร์", "บริษัท บางกอก เชน ฮอสปิทอล จำกัด (มหาชน)" },
            new[] { "โรงพยาบาล" }, "5502", "ค่าสวัสดิการพนักงาน"),
        new(new[] { "3m", "บริษัท 3เอ็ม" },
            new[] { "อุปกรณ์", "หน้ากาก", "เทป", "เครื่องเขียน" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "johnson", "j&j", "johnson & johnson" },
            new[] { "เวชภัณฑ์", "ของใช้" }, "5305", "ค่าวัสดุสำนักงาน"),

        // ─── Round 6: Real Estate / Hotel (Industry: RealEstate=8, Hotel=15) ───
        new(new[] { "ap thailand", "เอพี", "บริษัท เอพี (ไทยแลนด์) จำกัด (มหาชน)" },
            new[] { "ที่อยู่อาศัย", "คอนโด", "บ้าน" }, "1500", "สินทรัพย์ถาวร"),
        new(new[] { "sansiri", "แสนสิริ", "บริษัท แสนสิริ จำกัด (มหาชน)" },
            new[] { "ที่อยู่อาศัย", "คอนโด", "บ้าน" }, "1500", "สินทรัพย์ถาวร"),
        new(new[] { "pruksa", "พฤกษา", "บริษัท พฤกษา เรียลเอสเตท จำกัด (มหาชน)" },
            new[] { "ที่อยู่อาศัย", "บ้าน", "คอนโด" }, "1500", "สินทรัพย์ถาวร"),
        new(new[] { "land and houses", "แลนด์ แอนด์ เฮ้าส์", "บริษัท แลนด์แอนด์เฮ้าส์ จำกัด (มหาชน)" },
            new[] { "ที่อยู่อาศัย", "บ้าน" }, "1500", "สินทรัพย์ถาวร"),
        new(new[] { "supalai", "ศุภาลัย", "บริษัท ศุภาลัย จำกัด (มหาชน)" },
            new[] { "ที่อยู่อาศัย", "คอนโด" }, "1500", "สินทรัพย์ถาวร"),
        new(new[] { "cbre", "ซีบีอาร์อี" },
            new[] { "นายหน้า", "commission", "real estate" }, "5309", "ค่าธรรมเนียมวิชาชีพ"),
        new(new[] { "jll", "เจแอลแอล" },
            new[] { "นายหน้า", "commission" }, "5309", "ค่าธรรมเนียมวิชาชีพ"),
        new(new[] { "knight frank", "ไนท์แฟรงค์" },
            new[] { "นายหน้า", "commission" }, "5309", "ค่าธรรมเนียมวิชาชีพ"),
        new(new[] { "colliers", "คอลลิเออร์ส" },
            new[] { "นายหน้า", "commission" }, "5309", "ค่าธรรมเนียมวิชาชีพ"),
        new(new[] { "minor", "ไมเนอร์", "บริษัท ไมเนอร์ อินเตอร์เนชั่นแนล จำกัด (มหาชน)" },
            new[] { "โรงแรม", "ที่พัก", "hotel" }, "5403", "ค่าเช่า"),
        new(new[] { "centara", "เซ็นทารา", "บริษัท เซ็นทารา โฮเทล แอนด์ รีสอร์ท จำกัด (มหาชน)" },
            new[] { "โรงแรม", "ที่พัก" }, "5403", "ค่าเช่า"),
        new(new[] { "dusit", "ดุสิต", "บริษัท ดุสิตธานี จำกัด (มหาชน)" },
            new[] { "โรงแรม", "ที่พัก" }, "5403", "ค่าเช่า"),
        new(new[] { "marriott", "แมริออท" },
            new[] { "โรงแรม", "hotel" }, "5403", "ค่าเช่า"),
        new(new[] { "hilton", "ฮิลตัน" },
            new[] { "โรงแรม", "hotel" }, "5403", "ค่าเช่า"),
        new(new[] { "hyatt", "ไฮแอท" },
            new[] { "โรงแรม", "hotel" }, "5403", "ค่าเช่า"),
        new(new[] { "shangri-la", "shangrila", "ชางกรีล่า" },
            new[] { "โรงแรม" }, "5403", "ค่าเช่า"),

        // ─── Round 7: Technology — software subscriptions (Industry: Technology=9) ───
        new(new[] { "jetbrains", "เจ็ทเบรนส์" },
            new[] { "software", "license", "subscription", "ide" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "github", "github inc" },
            new[] { "software", "subscription", "source control" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "gitlab" },
            new[] { "software", "subscription" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "atlassian", "jira", "confluence", "trello" },
            new[] { "software", "subscription", "license" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "adobe", "adobe systems", "creative cloud" },
            new[] { "software", "subscription", "design" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "zoom", "zoom video" },
            new[] { "subscription", "meeting", "video conference" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "slack" },
            new[] { "subscription", "team chat" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "cloudflare" },
            new[] { "cdn", "subscription", "dns" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "netlify" },
            new[] { "hosting", "subscription" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "vercel" },
            new[] { "hosting", "subscription" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "godaddy" },
            new[] { "domain", "hosting" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "namecheap" },
            new[] { "domain", "hosting" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "notion" },
            new[] { "subscription", "software" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "figma" },
            new[] { "subscription", "design" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "canva" },
            new[] { "subscription", "design" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "linkedin" },
            new[] { "subscription", "recruiting", "ads" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "anthropic", "claude" },
            new[] { "subscription", "ai", "api" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "openai", "chatgpt" },
            new[] { "subscription", "ai", "api" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "intuit", "quickbooks" },
            new[] { "software", "accounting" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "sap" },
            new[] { "software", "erp", "license" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "oracle" },
            new[] { "software", "database", "license" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),
        new(new[] { "salesforce" },
            new[] { "software", "crm", "subscription" }, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์"),

        // ─── Round 8: Education / Beauty (Industry: Education=11, Beauty=12) ───
        new(new[] { "se-ed", "se ed", "ซีเอ็ด", "บริษัท ซีเอ็ดยูเคชั่น จำกัด (มหาชน)" },
            new[] { "หนังสือ", "ตำรา", "เครื่องเขียน", "stationery" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "aksorn", "อักษรเจริญทัศน์", "อจท", "บริษัท อักษรเจริญทัศน์ อจท. จำกัด" },
            new[] { "หนังสือ", "ตำรา", "แบบเรียน" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "mac education", "แม็คเอ็ดดูเคชั่น" },
            new[] { "หนังสือ", "ตำรา" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "kinokuniya", "คิโนคุนิยะ" },
            new[] { "หนังสือ", "book" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "asia books", "เอเชียบุ๊คส์" },
            new[] { "หนังสือ", "book" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "udemy" },
            new[] { "course", "training", "อบรม" }, "5701", "ค่าฝึกอบรมและสัมมนา"),
        new(new[] { "coursera" },
            new[] { "course", "training", "อบรม" }, "5701", "ค่าฝึกอบรมและสัมมนา"),
        new(new[] { "skooldio", "สคูลดิโอ" },
            new[] { "course", "อบรม" }, "5701", "ค่าฝึกอบรมและสัมมนา"),
        new(new[] { "pond's", "ponds", "พอนด์ส" },
            new[] { "ผิวพรรณ", "ครีม", "cosmetic" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "unilever", "ยูนิลีเวอร์" },
            new[] { "ของใช้", "สบู่", "แชมพู", "consumer goods" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "l'oreal", "loreal", "ลอรีอัล" },
            new[] { "เครื่องสำอาง", "cosmetic", "ผม" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "estee lauder", "estée lauder", "เอสเต้ ลอเดอร์" },
            new[] { "เครื่องสำอาง", "cosmetic" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "shiseido", "ชิเซโด้" },
            new[] { "เครื่องสำอาง" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "watsons", "วัตสัน", "บริษัท เซ็นทรัล วัตสัน จำกัด" },
            new[] { "ของใช้", "ยา", "เครื่องสำอาง" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "boots", "บู๊ทส์" },
            new[] { "ของใช้", "ยา" }, "5305", "ค่าวัสดุสำนักงาน"),
        new(new[] { "eveandboy", "eve and boy", "อีฟแอนด์บอย" },
            new[] { "เครื่องสำอาง", "beauty" }, "5305", "ค่าวัสดุสำนักงาน"),

        // ─── Round 9: Agriculture / Trading / Service (Industry: Agriculture=14, Trading=1, Service=2) ───
        new(new[] { "mitr phol", "มิตรผล", "บริษัท น้ำตาลมิตรผล จำกัด" },
            new[] { "น้ำตาล", "sugar", "วัตถุดิบ" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "thai roong ruang", "ไทยรุ่งเรือง" },
            new[] { "น้ำตาล" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "khonburi sugar", "ขอนบุรี" },
            new[] { "น้ำตาล" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "betagro feed", "เบทาโกรอาหารสัตว์" },
            new[] { "อาหารสัตว์", "feed" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "syngenta", "ซินเจนทา" },
            new[] { "ปุ๋ย", "ยาฆ่าแมลง", "เคมีเกษตร" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "bayer", "ไบเออร์" },
            new[] { "ปุ๋ย", "ยาฆ่าแมลง" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "yara", "ยารา" },
            new[] { "ปุ๋ย", "fertilizer" }, "5001", "ต้นทุนสินค้า"),
        new(new[] { "kubota", "คูโบต้า", "บริษัท สยามคูโบต้าคอร์ปอเรชั่น จำกัด" },
            new[] { "รถไถ", "tractor", "เครื่องจักรเกษตร" }, "1500", "สินทรัพย์ถาวร"),
        new(new[] { "krungthai-axa", "krungthai axa" },
            new[] { "ประกัน", "ประกันชีวิต" }, "5800", "ค่าใช้จ่ายในการประกัน"),
        new(new[] { "generali", "เจนเนอราลี่" },
            new[] { "ประกัน" }, "5800", "ค่าใช้จ่ายในการประกัน"),
        new(new[] { "axa", "แอกซ่า" },
            new[] { "ประกัน" }, "5800", "ค่าใช้จ่ายในการประกัน"),
        new(new[] { "ergo", "เออร์โก" },
            new[] { "ประกัน" }, "5800", "ค่าใช้จ่ายในการประกัน"),
        new(new[] { "hsbc", "เอชเอสบีซี" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "standard chartered", "สแตนดาร์ดชาร์เตอร์ด" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "citi", "ซิตี้แบงก์", "citibank" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "icbc", "ไอซีบีซี" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "uob", "ยูโอบี", "ธนาคารยูโอบี" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "gsb", "ออมสิน", "ธนาคารออมสิน" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),
        new(new[] { "baac", "ธกส", "ธนาคารเพื่อการเกษตร" },
            new[] { "ค่าธรรมเนียม", "bank fee" }, "5503", "ค่าธรรมเนียมธนาคาร"),

        // ─── Round 10: Transportation / Government / Vehicle maintenance (Industry: Transportation=13, Other=99) ───
        new(new[] { "bts", "บีทีเอส", "รถไฟฟ้าบีทีเอส" },
            new[] { "เดินทาง", "รถไฟฟ้า", "skytrain" }, "5401", "ค่าเดินทาง"),
        new(new[] { "mrt", "เอ็มอาร์ที", "รถไฟฟ้าใต้ดิน" },
            new[] { "เดินทาง", "รถไฟฟ้า", "subway" }, "5401", "ค่าเดินทาง"),
        new(new[] { "arl", "แอร์พอร์ตเรลลิงค์", "airport rail link" },
            new[] { "เดินทาง", "รถไฟฟ้า" }, "5401", "ค่าเดินทาง"),
        new(new[] { "bem", "บีอีเอ็ม", "บริษัท ทางด่วนและรถไฟฟ้ากรุงเทพ จำกัด (มหาชน)" },
            new[] { "ทางด่วน", "tollway", "เดินทาง" }, "5401", "ค่าเดินทาง"),
        new(new[] { "expressway", "การทางพิเศษ", "exat" },
            new[] { "ทางด่วน", "tollway" }, "5401", "ค่าเดินทาง"),
        new(new[] { "easy pass", "easypass", "m-pass", "mpass" },
            new[] { "ทางด่วน", "tollway" }, "5401", "ค่าเดินทาง"),
        new(new[] { "aot", "บริษัท ท่าอากาศยานไทย จำกัด (มหาชน)", "airports of thailand" },
            new[] { "สนามบิน", "airport" }, "5401", "ค่าเดินทาง"),
        new(new[] { "กรมสรรพากร", "revenue department", "rd" },
            new[] { "ภาษี", "tax", "ค่าธรรมเนียม" }, "5900", "ค่าธรรมเนียมราชการและภาษีอากร"),
        new(new[] { "ประกันสังคม", "สำนักงานประกันสังคม", "sso", "social security" },
            new[] { "ประกันสังคม", "เงินสมทบ" }, "5502", "ค่าสวัสดิการพนักงาน"),
        new(new[] { "กรมพัฒนาธุรกิจการค้า", "dbd", "department of business development" },
            new[] { "ค่าธรรมเนียม", "จดทะเบียน", "ราชการ" }, "5900", "ค่าธรรมเนียมราชการและภาษีอากร"),
        new(new[] { "กรมการขนส่งทางบก", "dlt", "land transport department" },
            new[] { "ค่าธรรมเนียม", "ภาษีรถ", "ทะเบียนรถ" }, "5900", "ค่าธรรมเนียมราชการและภาษีอากร"),
        new(new[] { "boi", "สำนักงานคณะกรรมการส่งเสริมการลงทุน" },
            new[] { "ค่าธรรมเนียม", "ส่งเสริม" }, "5900", "ค่าธรรมเนียมราชการและภาษีอากร"),
        new(new[] { "bridgestone", "บริดจสโตน" },
            new[] { "ยาง", "tire", "ยางรถ" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "michelin", "มิชลิน" },
            new[] { "ยาง", "tire" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "yokohama", "โยโกฮาม่า" },
            new[] { "ยาง", "tire" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "goodyear", "กู๊ดเยียร์" },
            new[] { "ยาง", "tire" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "toyota service", "โตโยต้าศูนย์บริการ" },
            new[] { "ซ่อมรถ", "บำรุงรักษา", "อะไหล่" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "honda service", "ฮอนด้าศูนย์บริการ" },
            new[] { "ซ่อมรถ", "บำรุงรักษา" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "isuzu service", "อีซูซุศูนย์บริการ" },
            new[] { "ซ่อมรถ", "บำรุงรักษา" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "cockpit", "ค็อกพิท" },
            new[] { "ยาง", "ซ่อมรถ" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "b-quik", "bquik", "บีควิก" },
            new[] { "ซ่อมรถ", "ยาง", "เปลี่ยนถ่าย" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
        new(new[] { "fitauto", "ฟิตออโต้" },
            new[] { "ซ่อมรถ", "ยาง" }, "5306", "ค่าซ่อมแซมและบำรุงรักษา"),
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

        // ═══════════════════════════════════════════════════════════════
        // EXPANSION — vendor intelligence across all 18 IndustryTypes
        // ═══════════════════════════════════════════════════════════════

        // ─── Round 1: Restaurant/Cafe — food suppliers ───
        new("Siam Makro", new[] { "makro", "แม็คโคร", "สยามแม็คโคร" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("Lotus's", new[] { "lotus", "lotus's", "โลตัส" },
            DocumentType.PaymentVoucher, "5001", "ต้นทุนสินค้า", null, 0),
        new("Big C", new[] { "big c", "บิ๊กซี" },
            DocumentType.PaymentVoucher, "5001", "ต้นทุนสินค้า", null, 0),
        new("CP Foods", new[] { "cp", "cpf", "เครือเจริญโภคภัณฑ์", "cp foods" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("Betagro", new[] { "betagro", "เบทาโกร" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("Thai Beverage", new[] { "thai beverage", "thaibev", "ช้าง" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("Boon Rawd Brewery", new[] { "singha", "สิงห์", "บุญรอด" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("Coca-Cola Thailand", new[] { "coca cola", "coca-cola", "โคคา-โคลา", "ไทยน้ำทิพย์" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("GrabFood", new[] { "grabfood", "grab food" },
            DocumentType.PaymentVoucher, "5404", "ค่ารับรองและอาหาร", null, 0),
        new("LineMan", new[] { "lineman", "line man" },
            DocumentType.PaymentVoucher, "5404", "ค่ารับรองและอาหาร", null, 0),
        new("Foodpanda", new[] { "foodpanda", "ฟู้ดแพนด้า" },
            DocumentType.PaymentVoucher, "5404", "ค่ารับรองและอาหาร", null, 0),
        new("CP All (7-Eleven)", new[] { "7-eleven", "7eleven", "เซเว่น", "cp all" },
            DocumentType.PaymentVoucher, "5305", "ค่าวัสดุสำนักงาน", null, 0),

        // ─── Round 2: Retail/Ecommerce — marketplaces & payment ───
        // Note: Marketplace commissions are typically 3% WHT (service)
        new("Lazada", new[] { "lazada", "ลาซาด้า" },
            DocumentType.PurchaseInvoice, "5101", "ค่าโฆษณาและส่งเสริมการขาย", 3m, 30),
        new("Shopee", new[] { "shopee", "ช้อปปี้" },
            DocumentType.PurchaseInvoice, "5101", "ค่าโฆษณาและส่งเสริมการขาย", 3m, 30),
        new("TikTok Shop", new[] { "tiktok shop", "ติ๊กต๊อกช้อป" },
            DocumentType.PurchaseInvoice, "5101", "ค่าโฆษณาและส่งเสริมการขาย", 3m, 30),
        new("Omise", new[] { "omise", "โอไมส์", "opn payments" },
            DocumentType.PurchaseInvoice, "5503", "ค่าธรรมเนียมธนาคาร", 3m, 30),
        new("2C2P", new[] { "2c2p" },
            DocumentType.PurchaseInvoice, "5503", "ค่าธรรมเนียมธนาคาร", 3m, 30),
        new("GBPrimePay", new[] { "gbprimepay", "gb prime pay" },
            DocumentType.PurchaseInvoice, "5503", "ค่าธรรมเนียมธนาคาร", 3m, 30),

        // ─── Round 3: Construction — building materials ───
        new("SCG", new[] { "scg", "เอสซีจี", "ปูนซิเมนต์ไทย", "siam cement" },
            DocumentType.PurchaseInvoice, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 30),
        new("TPI Polene", new[] { "tpi polene", "ทีพีไอ" },
            DocumentType.PurchaseInvoice, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 30),
        new("Tata Steel Thailand", new[] { "tata steel", "ทาทา สตีล" },
            DocumentType.PurchaseInvoice, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 30),
        new("Boon Thavorn", new[] { "boon thavorn", "บุญถาวร" },
            DocumentType.PaymentVoucher, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 0),
        new("Index Living Mall", new[] { "index living mall", "อินเด็กซ์" },
            DocumentType.PaymentVoucher, "5305", "ค่าวัสดุสำนักงาน", null, 0),
        new("Modernform", new[] { "modernform", "โมเดอร์นฟอร์ม" },
            DocumentType.PurchaseInvoice, "5305", "ค่าวัสดุสำนักงาน", null, 30),
        new("IKEA Thailand", new[] { "ikea", "อิเกีย" },
            DocumentType.PaymentVoucher, "5305", "ค่าวัสดุสำนักงาน", null, 0),

        // ─── Round 4: Manufacturing — raw materials & machinery ───
        new("PTT Global Chemical", new[] { "pttgc", "ptt global chemical" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("IRPC", new[] { "irpc" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("Indorama Ventures", new[] { "indorama", "อินโดรามา" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("WHA Corporation", new[] { "wha" },
            DocumentType.PurchaseInvoice, "5403", "ค่าเช่า", 5m, 30),
        new("Amata Corporation", new[] { "amata", "อมตะ" },
            DocumentType.PurchaseInvoice, "5403", "ค่าเช่า", 5m, 30),
        new("Mitsubishi Electric", new[] { "mitsubishi electric", "มิตซูบิชิ อีเล็คทริค" },
            DocumentType.PurchaseInvoice, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 30),
        new("Komatsu", new[] { "komatsu", "โคมัตสุ" },
            DocumentType.PurchaseInvoice, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 30),

        // ─── Round 5: Healthcare — pharma & hospitals ───
        new("Pfizer", new[] { "pfizer", "ไฟเซอร์" },
            DocumentType.PurchaseInvoice, "5305", "ค่าวัสดุสำนักงาน", null, 30),
        new("GSK", new[] { "gsk", "glaxosmithkline" },
            DocumentType.PurchaseInvoice, "5305", "ค่าวัสดุสำนักงาน", null, 30),
        new("BDMS", new[] { "bdms", "กรุงเทพดุสิตเวชการ" },
            DocumentType.PaymentVoucher, "5502", "ค่าสวัสดิการพนักงาน", null, 15),
        new("Bangkok Hospital", new[] { "bangkok hospital", "โรงพยาบาลกรุงเทพ" },
            DocumentType.PaymentVoucher, "5502", "ค่าสวัสดิการพนักงาน", null, 15),
        new("Bumrungrad", new[] { "bumrungrad", "บำรุงราษฎร์" },
            DocumentType.PaymentVoucher, "5502", "ค่าสวัสดิการพนักงาน", null, 15),
        new("3M", new[] { "3m" },
            DocumentType.PurchaseInvoice, "5305", "ค่าวัสดุสำนักงาน", null, 30),

        // ─── Round 6: Real Estate / Hotel ───
        // Real estate brokers charge 3% WHT on commissions
        new("CBRE", new[] { "cbre" },
            DocumentType.PurchaseInvoice, "5309", "ค่าธรรมเนียมวิชาชีพ", 3m, 30),
        new("JLL", new[] { "jll" },
            DocumentType.PurchaseInvoice, "5309", "ค่าธรรมเนียมวิชาชีพ", 3m, 30),
        new("Minor International", new[] { "minor", "ไมเนอร์" },
            DocumentType.PurchaseInvoice, "5403", "ค่าเช่า", null, 30),
        new("Centara Hotels", new[] { "centara", "เซ็นทารา" },
            DocumentType.PurchaseInvoice, "5403", "ค่าเช่า", null, 30),
        new("Dusit Thani", new[] { "dusit", "ดุสิต" },
            DocumentType.PurchaseInvoice, "5403", "ค่าเช่า", null, 30),
        new("Marriott", new[] { "marriott", "แมริออท" },
            DocumentType.PurchaseInvoice, "5403", "ค่าเช่า", null, 30),
        new("Hilton", new[] { "hilton", "ฮิลตัน" },
            DocumentType.PurchaseInvoice, "5403", "ค่าเช่า", null, 30),

        // ─── Round 7: Technology — SaaS subscriptions ───
        // Foreign SaaS providers typically billed monthly, no Thai WHT
        new("JetBrains", new[] { "jetbrains" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("GitHub", new[] { "github" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("Atlassian", new[] { "atlassian", "jira", "confluence" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("Adobe", new[] { "adobe", "creative cloud" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("Zoom", new[] { "zoom" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("Slack", new[] { "slack" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("Cloudflare", new[] { "cloudflare" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("Vercel", new[] { "vercel" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("Notion", new[] { "notion" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("Figma", new[] { "figma" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("Canva", new[] { "canva" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("Anthropic", new[] { "anthropic", "claude" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),
        new("OpenAI", new[] { "openai", "chatgpt" },
            DocumentType.PurchaseInvoice, "5308", "ค่าซอฟต์แวร์และลิขสิทธิ์", null, 30),

        // ─── Round 8: Education / Beauty ───
        new("SE-ED", new[] { "se-ed", "se ed", "ซีเอ็ด" },
            DocumentType.PurchaseInvoice, "5305", "ค่าวัสดุสำนักงาน", null, 30),
        new("Aksorn", new[] { "aksorn", "อักษรเจริญทัศน์", "อจท" },
            DocumentType.PurchaseInvoice, "5305", "ค่าวัสดุสำนักงาน", null, 30),
        new("Udemy", new[] { "udemy" },
            DocumentType.PurchaseInvoice, "5701", "ค่าฝึกอบรมและสัมมนา", null, 30),
        new("Unilever", new[] { "unilever", "ยูนิลีเวอร์" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("L'Oreal", new[] { "l'oreal", "loreal", "ลอรีอัล" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("Watsons", new[] { "watsons", "วัตสัน" },
            DocumentType.PaymentVoucher, "5305", "ค่าวัสดุสำนักงาน", null, 0),

        // ─── Round 9: Agriculture / Banking / Insurance ───
        new("Mitr Phol", new[] { "mitr phol", "มิตรผล" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("Syngenta", new[] { "syngenta", "ซินเจนทา" },
            DocumentType.PurchaseInvoice, "5001", "ต้นทุนสินค้า", null, 30),
        new("Kubota", new[] { "kubota", "คูโบต้า" },
            DocumentType.PurchaseInvoice, "1500", "สินทรัพย์ถาวร", null, 30),
        new("BBL", new[] { "bbl", "ธนาคารกรุงเทพ", "กรุงเทพ" },
            DocumentType.PaymentVoucher, "5503", "ค่าธรรมเนียมธนาคาร", null, 0),
        new("KTB", new[] { "ktb", "กรุงไทย" },
            DocumentType.PaymentVoucher, "5503", "ค่าธรรมเนียมธนาคาร", null, 0),
        new("BAY", new[] { "bay", "กรุงศรี", "krungsri" },
            DocumentType.PaymentVoucher, "5503", "ค่าธรรมเนียมธนาคาร", null, 0),
        new("TTB", new[] { "ttb", "ทีทีบี" },
            DocumentType.PaymentVoucher, "5503", "ค่าธรรมเนียมธนาคาร", null, 0),
        new("UOB", new[] { "uob", "ยูโอบี" },
            DocumentType.PaymentVoucher, "5503", "ค่าธรรมเนียมธนาคาร", null, 0),
        new("GSB", new[] { "gsb", "ออมสิน" },
            DocumentType.PaymentVoucher, "5503", "ค่าธรรมเนียมธนาคาร", null, 0),
        new("BAAC", new[] { "baac", "ธกส" },
            DocumentType.PaymentVoucher, "5503", "ค่าธรรมเนียมธนาคาร", null, 0),
        new("KrungThai-AXA", new[] { "krungthai-axa", "krungthai axa" },
            DocumentType.PurchaseInvoice, "5800", "ค่าใช้จ่ายในการประกัน", null, 30),
        new("AXA Thailand", new[] { "axa", "แอกซ่า" },
            DocumentType.PurchaseInvoice, "5800", "ค่าใช้จ่ายในการประกัน", null, 30),
        new("Generali", new[] { "generali", "เจนเนอราลี่" },
            DocumentType.PurchaseInvoice, "5800", "ค่าใช้จ่ายในการประกัน", null, 30),
        new("Tipayanapha", new[] { "tipayanapha", "ทิพยประกัน" },
            DocumentType.PurchaseInvoice, "5800", "ค่าใช้จ่ายในการประกัน", null, 30),

        // ─── Round 10: Transportation / Government / Vehicle ───
        // Government fees: no WHT, paid immediately
        new("BTS", new[] { "bts", "บีทีเอส" },
            DocumentType.PaymentVoucher, "5401", "ค่าเดินทาง", null, 0),
        new("MRT", new[] { "mrt" },
            DocumentType.PaymentVoucher, "5401", "ค่าเดินทาง", null, 0),
        new("Expressway Authority", new[] { "expressway", "การทางพิเศษ" },
            DocumentType.PaymentVoucher, "5401", "ค่าเดินทาง", null, 0),
        new("AOT", new[] { "aot", "ท่าอากาศยานไทย" },
            DocumentType.PaymentVoucher, "5401", "ค่าเดินทาง", null, 0),
        new("Revenue Department", new[] { "กรมสรรพากร", "revenue department" },
            DocumentType.PaymentVoucher, "5900", "ค่าธรรมเนียมราชการและภาษีอากร", null, 0),
        new("Social Security Office", new[] { "ประกันสังคม", "สำนักงานประกันสังคม", "sso" },
            DocumentType.PaymentVoucher, "5502", "ค่าสวัสดิการพนักงาน", null, 0),
        new("DBD", new[] { "กรมพัฒนาธุรกิจการค้า", "dbd" },
            DocumentType.PaymentVoucher, "5900", "ค่าธรรมเนียมราชการและภาษีอากร", null, 0),
        new("DLT", new[] { "กรมการขนส่งทางบก", "dlt" },
            DocumentType.PaymentVoucher, "5900", "ค่าธรรมเนียมราชการและภาษีอากร", null, 0),
        new("Bridgestone", new[] { "bridgestone", "บริดจสโตน" },
            DocumentType.PaymentVoucher, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 0),
        new("Michelin", new[] { "michelin", "มิชลิน" },
            DocumentType.PaymentVoucher, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 0),
        new("B-Quik", new[] { "b-quik", "bquik", "บีควิก" },
            DocumentType.PaymentVoucher, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 0),
        new("Cockpit", new[] { "cockpit", "ค็อกพิท" },
            DocumentType.PaymentVoucher, "5306", "ค่าซ่อมแซมและบำรุงรักษา", null, 0),
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

        // ═══════════════════════════════════════════════════════════════
        // EXPANSION — association rules across all 18 IndustryTypes
        // ═══════════════════════════════════════════════════════════════

        // ─── Round 1: Restaurant/Cafe — COGS food materials → 5001 ───
        new(new[] { "brand:makro" }, "acct:5001", 0.9m, 7m),
        new(new[] { "brand:แม็คโคร" }, "acct:5001", 0.9m, 7m),
        new(new[] { "brand:lotus" }, "acct:5001", 0.85m, 6m),
        new(new[] { "brand:โลตัส" }, "acct:5001", 0.85m, 6m),
        new(new[] { "brand:bigc" }, "acct:5001", 0.85m, 6m),
        new(new[] { "brand:บิ๊กซี" }, "acct:5001", 0.85m, 6m),
        new(new[] { "brand:cp", "kw:อาหาร" }, "acct:5001", 0.92m, 8m),
        new(new[] { "brand:cpf" }, "acct:5001", 0.95m, 9m),
        new(new[] { "brand:betagro" }, "acct:5001", 0.95m, 9m),
        new(new[] { "brand:เบทาโกร" }, "acct:5001", 0.95m, 9m),
        new(new[] { "brand:singha" }, "acct:5001", 0.9m, 7m),
        new(new[] { "brand:สิงห์" }, "acct:5001", 0.9m, 7m),
        new(new[] { "brand:chang", "kw:เครื่องดื่ม" }, "acct:5001", 0.92m, 8m),
        new(new[] { "brand:coca", "brand:cola" }, "acct:5001", 0.95m, 9m),
        new(new[] { "brand:pepsi" }, "acct:5001", 0.95m, 9m),
        new(new[] { "brand:grabfood" }, "acct:5404", 0.92m, 8m),
        new(new[] { "brand:lineman" }, "acct:5404", 0.92m, 8m),
        new(new[] { "brand:foodpanda" }, "acct:5404", 0.92m, 8m),
        new(new[] { "brand:starbucks" }, "acct:5404", 0.88m, 6.5m),
        new(new[] { "brand:7-eleven" }, "acct:5305", 0.75m, 4m),
        new(new[] { "brand:เซเว่น" }, "acct:5305", 0.75m, 4m),

        // ─── Round 2: Retail/Ecommerce → 5101 ads, 5503 payment ───
        new(new[] { "brand:lazada" }, "acct:5101", 0.85m, 6m),
        new(new[] { "brand:shopee" }, "acct:5101", 0.85m, 6m),
        new(new[] { "brand:tiktok", "kw:shop" }, "acct:5101", 0.85m, 6m),
        new(new[] { "kw:commission", "kw:marketplace" }, "acct:5101", 0.88m, 6.5m),
        new(new[] { "brand:omise" }, "acct:5503", 0.95m, 9m),
        new(new[] { "brand:2c2p" }, "acct:5503", 0.95m, 9m),
        new(new[] { "brand:gbprimepay" }, "acct:5503", 0.95m, 9m),
        new(new[] { "brand:stripe" }, "acct:5503", 0.95m, 9m),
        new(new[] { "brand:paypal" }, "acct:5503", 0.92m, 8m),
        new(new[] { "kw:payment", "kw:gateway" }, "acct:5503", 0.9m, 7m),

        // ─── Round 3: Construction → 5306 repair / 5305 materials ───
        new(new[] { "brand:scg" }, "acct:5306", 0.85m, 5.5m),
        new(new[] { "brand:เอสซีจี" }, "acct:5306", 0.85m, 5.5m),
        new(new[] { "brand:tpi" }, "acct:5306", 0.85m, 5.5m),
        new(new[] { "kw:ปูน", "kw:ซีเมนต์" }, "acct:5306", 0.9m, 7m),
        new(new[] { "kw:เหล็ก", "kw:steel" }, "acct:5306", 0.85m, 6m),
        new(new[] { "brand:tata", "brand:steel" }, "acct:5306", 0.92m, 8m),
        new(new[] { "brand:boon", "brand:thavorn" }, "acct:5306", 0.92m, 8m),
        new(new[] { "brand:บุญถาวร" }, "acct:5306", 0.92m, 8m),
        new(new[] { "brand:index", "kw:furniture" }, "acct:5305", 0.85m, 5.5m),
        new(new[] { "brand:modernform" }, "acct:5305", 0.92m, 8m),
        new(new[] { "brand:ikea" }, "acct:5305", 0.85m, 5.5m),

        // ─── Round 4: Manufacturing → 5001 raw materials / 5306 machinery ───
        new(new[] { "brand:pttgc" }, "acct:5001", 0.92m, 8m),
        new(new[] { "brand:irpc" }, "acct:5001", 0.92m, 8m),
        new(new[] { "brand:indorama" }, "acct:5001", 0.9m, 7m),
        new(new[] { "kw:เม็ดพลาสติก" }, "acct:5001", 0.92m, 8m),
        new(new[] { "kw:เคมีภัณฑ์" }, "acct:5001", 0.88m, 6.5m),
        new(new[] { "brand:wha", "kw:industrial" }, "acct:5403", 0.9m, 7m),
        new(new[] { "brand:amata", "kw:industrial" }, "acct:5403", 0.9m, 7m),
        new(new[] { "brand:mitsubishi", "kw:electric" }, "acct:5306", 0.85m, 5.5m),
        new(new[] { "brand:komatsu" }, "acct:5306", 0.88m, 6.5m),
        new(new[] { "brand:hitachi" }, "acct:5306", 0.85m, 5.5m),
        new(new[] { "brand:caterpillar" }, "acct:5306", 0.85m, 5.5m),
        new(new[] { "kw:forklift" }, "acct:5306", 0.88m, 6.5m),

        // ─── Round 5: Healthcare → 5305 supplies / 5502 hospital ───
        new(new[] { "brand:pfizer" }, "acct:5305", 0.85m, 5.5m),
        new(new[] { "brand:gsk" }, "acct:5305", 0.85m, 5.5m),
        new(new[] { "brand:sanofi" }, "acct:5305", 0.85m, 5.5m),
        new(new[] { "brand:novartis" }, "acct:5305", 0.85m, 5.5m),
        new(new[] { "kw:ยา", "kw:เวชภัณฑ์" }, "acct:5305", 0.88m, 6.5m),
        new(new[] { "brand:bdms" }, "acct:5502", 0.9m, 7m),
        new(new[] { "brand:กรุงเทพดุสิตเวชการ" }, "acct:5502", 0.9m, 7m),
        new(new[] { "brand:bumrungrad" }, "acct:5502", 0.92m, 8m),
        new(new[] { "brand:บำรุงราษฎร์" }, "acct:5502", 0.92m, 8m),
        new(new[] { "brand:samitivej" }, "acct:5502", 0.9m, 7m),
        new(new[] { "kw:โรงพยาบาล" }, "acct:5502", 0.85m, 6m),
        new(new[] { "kw:รักษาพยาบาล" }, "acct:5502", 0.88m, 6.5m),
        new(new[] { "brand:3m" }, "acct:5305", 0.75m, 4m),

        // ─── Round 6: Real Estate / Hotel → 5309 commission / 5403 rent / 1500 asset ───
        new(new[] { "brand:cbre" }, "acct:5309", 0.92m, 8m),
        new(new[] { "brand:jll" }, "acct:5309", 0.92m, 8m),
        new(new[] { "brand:knight", "brand:frank" }, "acct:5309", 0.92m, 8m),
        new(new[] { "brand:minor", "kw:hotel" }, "acct:5403", 0.88m, 6.5m),
        new(new[] { "brand:centara" }, "acct:5403", 0.92m, 8m),
        new(new[] { "brand:dusit" }, "acct:5403", 0.92m, 8m),
        new(new[] { "brand:marriott" }, "acct:5403", 0.92m, 8m),
        new(new[] { "brand:hilton" }, "acct:5403", 0.92m, 8m),
        new(new[] { "brand:hyatt" }, "acct:5403", 0.92m, 8m),
        new(new[] { "kw:โรงแรม", "kw:ที่พัก" }, "acct:5403", 0.85m, 6m),
        new(new[] { "kw:ค่าเช่า", "kw:สำนักงาน" }, "acct:5403", 0.9m, 7m),

        // ─── Round 7: Technology → 5308 software ───
        new(new[] { "brand:jetbrains" }, "acct:5308", 0.98m, 12m),
        new(new[] { "brand:github" }, "acct:5308", 0.98m, 12m),
        new(new[] { "brand:gitlab" }, "acct:5308", 0.98m, 12m),
        new(new[] { "brand:atlassian" }, "acct:5308", 0.98m, 12m),
        new(new[] { "brand:jira" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:adobe" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:zoom" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:slack" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:cloudflare" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:netlify" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:vercel" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:godaddy" }, "acct:5308", 0.92m, 8m),
        new(new[] { "brand:namecheap" }, "acct:5308", 0.92m, 8m),
        new(new[] { "brand:notion" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:figma" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:canva" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:anthropic" }, "acct:5308", 0.98m, 12m),
        new(new[] { "brand:openai" }, "acct:5308", 0.98m, 12m),
        new(new[] { "brand:chatgpt" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:salesforce" }, "acct:5308", 0.95m, 10m),
        new(new[] { "brand:linkedin" }, "acct:5308", 0.85m, 6m),
        new(new[] { "kw:software", "kw:subscription" }, "acct:5308", 0.92m, 8m),
        new(new[] { "kw:license", "kw:software" }, "acct:5308", 0.92m, 8m),

        // ─── Round 8: Education / Beauty → 5305 / 5701 / 5001 ───
        new(new[] { "brand:se-ed" }, "acct:5305", 0.9m, 7m),
        new(new[] { "brand:ซีเอ็ด" }, "acct:5305", 0.9m, 7m),
        new(new[] { "brand:aksorn" }, "acct:5305", 0.9m, 7m),
        new(new[] { "brand:อักษรเจริญทัศน์" }, "acct:5305", 0.9m, 7m),
        new(new[] { "brand:udemy" }, "acct:5701", 0.95m, 10m),
        new(new[] { "brand:coursera" }, "acct:5701", 0.95m, 10m),
        new(new[] { "brand:skooldio" }, "acct:5701", 0.95m, 10m),
        new(new[] { "kw:training", "kw:course" }, "acct:5701", 0.88m, 6.5m),
        new(new[] { "kw:อบรม", "kw:สัมมนา" }, "acct:5701", 0.92m, 8m),
        new(new[] { "brand:unilever" }, "acct:5001", 0.8m, 5m),
        new(new[] { "brand:loreal" }, "acct:5001", 0.85m, 6m),
        new(new[] { "brand:ลอรีอัล" }, "acct:5001", 0.85m, 6m),
        new(new[] { "brand:shiseido" }, "acct:5001", 0.85m, 6m),
        new(new[] { "brand:watsons" }, "acct:5305", 0.75m, 4m),
        new(new[] { "brand:วัตสัน" }, "acct:5305", 0.75m, 4m),
        new(new[] { "brand:boots" }, "acct:5305", 0.75m, 4m),
        new(new[] { "kw:เครื่องสำอาง" }, "acct:5001", 0.8m, 5m),

        // ─── Round 9: Agriculture / Banking / Insurance ───
        new(new[] { "brand:mitr", "brand:phol" }, "acct:5001", 0.92m, 8m),
        new(new[] { "brand:มิตรผล" }, "acct:5001", 0.92m, 8m),
        new(new[] { "brand:syngenta" }, "acct:5001", 0.92m, 8m),
        new(new[] { "brand:bayer" }, "acct:5001", 0.85m, 6m),
        new(new[] { "brand:yara" }, "acct:5001", 0.92m, 8m),
        new(new[] { "brand:kubota" }, "acct:1500", 0.85m, 6m),
        new(new[] { "brand:คูโบต้า" }, "acct:1500", 0.85m, 6m),
        new(new[] { "kw:ปุ๋ย" }, "acct:5001", 0.85m, 6m),
        new(new[] { "kw:ยาฆ่าแมลง" }, "acct:5001", 0.88m, 6.5m),
        new(new[] { "kw:อาหารสัตว์" }, "acct:5001", 0.88m, 6.5m),
        new(new[] { "kw:รถไถ" }, "acct:1500", 0.85m, 6m),
        new(new[] { "brand:ttb" }, "acct:5503", 0.8m, 5m),
        new(new[] { "brand:uob" }, "acct:5503", 0.8m, 5m),
        new(new[] { "brand:gsb" }, "acct:5503", 0.8m, 5m),
        new(new[] { "brand:ออมสิน" }, "acct:5503", 0.8m, 5m),
        new(new[] { "brand:hsbc" }, "acct:5503", 0.85m, 6m),
        new(new[] { "brand:citibank" }, "acct:5503", 0.85m, 6m),
        new(new[] { "brand:axa" }, "acct:5800", 0.92m, 8m),
        new(new[] { "brand:generali" }, "acct:5800", 0.92m, 8m),
        new(new[] { "brand:ทิพยประกัน" }, "acct:5800", 0.92m, 8m),
        new(new[] { "kw:พรบ", "kw:ประกัน" }, "acct:5800", 0.92m, 8m),

        // ─── Round 10: Transportation / Government / Vehicle ───
        new(new[] { "brand:bts" }, "acct:5401", 0.95m, 9m),
        new(new[] { "brand:บีทีเอส" }, "acct:5401", 0.95m, 9m),
        new(new[] { "brand:mrt" }, "acct:5401", 0.95m, 9m),
        new(new[] { "brand:arl" }, "acct:5401", 0.95m, 9m),
        new(new[] { "brand:expressway" }, "acct:5401", 0.95m, 9m),
        new(new[] { "brand:การทางพิเศษ" }, "acct:5401", 0.95m, 9m),
        new(new[] { "kw:ทางด่วน" }, "acct:5401", 0.92m, 8m),
        new(new[] { "kw:tollway" }, "acct:5401", 0.92m, 8m),
        new(new[] { "brand:aot" }, "acct:5401", 0.9m, 7m),
        new(new[] { "kw:สนามบิน" }, "acct:5401", 0.85m, 6m),
        new(new[] { "brand:กรมสรรพากร" }, "acct:5900", 0.98m, 12m),
        new(new[] { "kw:ภาษี", "kw:สรรพากร" }, "acct:5900", 0.95m, 10m),
        new(new[] { "brand:ประกันสังคม" }, "acct:5502", 0.98m, 12m),
        new(new[] { "kw:ประกันสังคม" }, "acct:5502", 0.95m, 10m),
        new(new[] { "brand:dbd" }, "acct:5900", 0.92m, 8m),
        new(new[] { "brand:กรมพัฒนาธุรกิจการค้า" }, "acct:5900", 0.95m, 10m),
        new(new[] { "brand:dlt" }, "acct:5900", 0.92m, 8m),
        new(new[] { "brand:กรมการขนส่งทางบก" }, "acct:5900", 0.95m, 10m),
        new(new[] { "kw:ค่าธรรมเนียม", "kw:ราชการ" }, "acct:5900", 0.92m, 8m),
        new(new[] { "brand:bridgestone" }, "acct:5306", 0.95m, 9m),
        new(new[] { "brand:michelin" }, "acct:5306", 0.95m, 9m),
        new(new[] { "brand:yokohama" }, "acct:5306", 0.95m, 9m),
        new(new[] { "brand:goodyear" }, "acct:5306", 0.95m, 9m),
        new(new[] { "kw:ยาง", "kw:รถ" }, "acct:5306", 0.92m, 8m),
        new(new[] { "kw:tire" }, "acct:5306", 0.9m, 7m),
        new(new[] { "brand:b-quik" }, "acct:5306", 0.95m, 9m),
        new(new[] { "brand:cockpit" }, "acct:5306", 0.92m, 8m),
        new(new[] { "brand:fitauto" }, "acct:5306", 0.92m, 8m),
        new(new[] { "kw:ซ่อมรถ" }, "acct:5306", 0.95m, 10m),
        new(new[] { "kw:เปลี่ยนถ่าย", "kw:น้ำมัน" }, "acct:5306", 0.92m, 8m),
        new(new[] { "kw:อะไหล่", "kw:รถ" }, "acct:5306", 0.92m, 8m),
    };
}
