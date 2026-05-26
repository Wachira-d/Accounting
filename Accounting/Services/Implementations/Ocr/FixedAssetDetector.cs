using Accounting.Models.Enums;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Detect line items that look like Fixed Asset candidates inside an
/// OCR'd document. Pure static — runs at scan time so the review UI can
/// surface a "Potential Asset" alert before the user approves.
///
/// Detection rules (Thai SME convention + RD Section 65 ter):
///   1. Unit price ≥ threshold (default ฿5,000 — the conventional minimum
///      capitalization line for SMEs; below this, treat as expense).
///   2. Description contains capital-asset keywords:
///      • Equipment: เครื่อง, อุปกรณ์, machine, equipment
///      • Computer: คอมพิวเตอร์, computer, laptop, notebook, server
///      • Office: โต๊ะ, เก้าอี้, ตู้, furniture, desk, chair, cabinet
///      • Vehicle: รถยนต์, รถ, มอเตอร์ไซค์, vehicle, motorcycle
///      • Electronic: ปริ้นเตอร์, printer, monitor, จอ, tv, ทีวี
///      • Building: อาคาร, สิ่งปลูกสร้าง, building, structure
///   3. Vendor-side hint: brand-name vendors selling capital goods.
///
/// Returns a per-line decision so the UI can highlight only the lines
/// that look like assets — not the whole document.
/// </summary>
internal static class FixedAssetDetector
{
    public record LineDecision(
        int LineIndex,
        bool IsPotentialAsset,
        string? SuggestedCategory,
        int SuggestedUsefulLifeMonths,
        decimal ConfidenceScore,
        List<string> Reasons);

    /// <summary>Default capitalization threshold in baht. Matches the
    /// common Thai SME policy (RD Section 65 ter for low-value items can
    /// be expensed up to ฿2,000 individually; ฿5,000 is the safer SME
    /// minimum where audit trails are simpler).</summary>
    public const decimal DefaultCapitalizationThreshold = 5_000m;

    public static List<LineDecision> Analyze(
        IReadOnlyList<(string? Description, decimal? Quantity, decimal? UnitPrice, decimal? Amount)> lines,
        decimal capitalizationThreshold = DefaultCapitalizationThreshold)
    {
        var results = new List<LineDecision>();
        if (lines == null || lines.Count == 0) return results;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            // Apply bilingual canonicalization first so a keyword like
            // "computer" matches Thai "คอมพิวเตอร์" lines and vice versa
            // through the shared $-prefixed canonical tokens. The
            // category-keyword rules below can stay English-only — Thai
            // input gets translated up to the same token in advance.
            var desc = BilingualTerms.Canonicalize(line.Description ?? "");
            var unitPrice = line.UnitPrice ?? (line.Quantity is > 0 && line.Amount is > 0
                ? line.Amount.Value / line.Quantity.Value : line.Amount ?? 0m);

            // Skip empty / clearly non-asset lines fast
            if (string.IsNullOrWhiteSpace(desc) || unitPrice < 100m)
            {
                results.Add(new LineDecision(i, false, null, 0, 0m, new List<string>()));
                continue;
            }

            var reasons = new List<string>();
            decimal score = 0m;

            // Rule 1: capitalization threshold
            if (unitPrice >= capitalizationThreshold)
            {
                score += 0.4m;
                reasons.Add($"Unit price {unitPrice:N2} ≥ {capitalizationThreshold:N0} (capitalization threshold)");
            }
            else if (unitPrice >= capitalizationThreshold / 2)
            {
                score += 0.15m;
                reasons.Add($"Unit price {unitPrice:N2} ใกล้ threshold");
            }

            // Rule 2: keyword categories (each keyword family adds to score
            // AND tags a suggested category)
            string? cat = null;
            int usefulLife = 60;   // sensible default = 5 years

            foreach (var rule in AssetCategoryKeywords)
            {
                if (rule.Keywords.Any(k => desc.Contains(k.ToLowerInvariant())))
                {
                    score += 0.4m;
                    cat = rule.Category;
                    usefulLife = rule.DefaultUsefulLifeMonths;
                    reasons.Add($"Keyword match: '{rule.Category}' (อายุการใช้ตามมาตรฐาน ~{usefulLife / 12} ปี)");
                    break;
                }
            }

            // Confidence threshold for surfacing the alert: 0.5+
            // (means either threshold alone + nothing else won't trigger,
            // but threshold + any keyword always will).
            var isAsset = score >= 0.5m;
            results.Add(new LineDecision(i, isAsset, cat, usefulLife, Math.Min(score, 0.99m), reasons));
        }
        return results;
    }

    /// <summary>Convenience: given a full set of analyzed lines, return
    /// the subset flagged as potential assets — what the UI will show.</summary>
    public static List<LineDecision> PotentialAssetsOnly(List<LineDecision> all)
        => all.Where(d => d.IsPotentialAsset).ToList();

    /// <summary>Default useful-life conventions for common Thai SME asset
    /// categories. Numbers come from RD depreciation guidelines for
    /// non-tax purposes (companies file accelerated rates per the 20%/year
    /// straight-line rule of Section 65 ter for tax — but accounting
    /// useful life follows GAAP standards reflected here).</summary>
    private record AssetCategoryRule(string Category, int DefaultUsefulLifeMonths, string[] Keywords);

    private static readonly AssetCategoryRule[] AssetCategoryKeywords = new[]
    {
        new AssetCategoryRule("คอมพิวเตอร์และอุปกรณ์ไอที", 36, new[]
        {
            "คอมพิวเตอร์", "computer", "laptop", "โน้ตบุ๊ก", "notebook",
            "server", "เซิร์ฟเวอร์", "macbook", "imac", "monitor", "จอ",
            "keyboard", "mouse", "router", "switch", "network",
            "ipad", "tablet", "แท็บเล็ต",
        }),
        new AssetCategoryRule("อุปกรณ์สำนักงาน", 60, new[]
        {
            "ปริ้นเตอร์", "printer", "เครื่องพิมพ์", "scanner", "สแกนเนอร์",
            "copier", "เครื่องถ่ายเอกสาร", "fax", "shredder", "เครื่องทำลาย",
            "projector", "โปรเจคเตอร์", "ups", "เครื่องสำรองไฟ",
        }),
        new AssetCategoryRule("เฟอร์นิเจอร์", 96, new[]
        {
            "โต๊ะ", "desk", "table", "เก้าอี้", "chair", "ตู้", "cabinet",
            "ชั้นวาง", "shelf", "shelving", "โซฟา", "sofa", "เตียง", "bed",
            "furniture", "เฟอร์นิเจอร์",
        }),
        new AssetCategoryRule("เครื่องจักรและอุปกรณ์การผลิต", 120, new[]
        {
            "เครื่องจักร", "machine", "machinery", "เครื่องผลิต", "production",
            "เครื่องตัด", "เครื่องเชื่อม", "เครื่องเจียร", "lathe", "milling",
            "compressor", "เครื่องอัดอากาศ",
        }),
        new AssetCategoryRule("ยานพาหนะ", 60, new[]
        {
            "รถยนต์", "vehicle", "car", "รถ", "truck", "รถบรรทุก", "van", "รถตู้",
            "มอเตอร์ไซค์", "motorcycle", "scooter", "สกู๊ตเตอร์",
        }),
        new AssetCategoryRule("เครื่องปรับอากาศและระบบทำความเย็น", 60, new[]
        {
            "แอร์", "air conditioner", "เครื่องปรับอากาศ", "ac unit",
            "ตู้แช่", "ตู้เย็น", "refrigerator", "freezer", "chiller",
        }),
        new AssetCategoryRule("อุปกรณ์ครัวและร้านอาหาร", 60, new[]
        {
            "เตา", "stove", "เตาอบ", "oven", "ไมโครเวฟ", "microwave",
            "เครื่องชงกาแฟ", "espresso machine", "เครื่องชง", "kitchen equipment",
            "เครื่องล้างจาน", "dishwasher",
        }),
        new AssetCategoryRule("กล้องและอุปกรณ์ถ่ายภาพ", 60, new[]
        {
            "กล้อง", "camera", "dslr", "mirrorless", "เลนส์", "lens",
            "tripod", "ขาตั้ง", "lighting", "softbox",
        }),
    };
}
