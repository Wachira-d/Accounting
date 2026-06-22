using Accounting.Services.Ai.Prompts;

namespace Accounting.Services.Ai;

/// <summary>
/// Deterministic prior สำหรับ "เครื่องใช้ทน vs วัสดุสิ้นเปลือง" ตาม §65 ตรี (5)
/// + พ.ร.ฎ.145 — กันเคสที่ AI เคยตอบผิด (เช่น "เครื่องปริ้นท์" → "ค่าวัสดุ
/// สิ้นเปลืองสำนักงาน"). pure function — รับ description + amount + candidate
/// chart of accounts คืน (suggested code, confidence).
///
/// ใช้เป็น local pre-prompt prior: ถ้า match keyword ชัด → ตั้ง
/// localBestAccountCode + localConfidence สูง → orchestrator routing
/// short-circuit ที่ ≥0.85 (ไม่เรียก AI provider ลดต้นทุน + ตอบถูกเสมอ).
///
/// Confidence ที่คืน:
///   0.90 — keyword ตรงตัว + ราคา ≥ ฿50,000 (capex threshold §65 ตรี (5))
///   0.85 — keyword ตรงตัว (durable goods ทุกระดับราคา)
///   0.75 — keyword ใกล้เคียง / สังเคราะห์ได้จาก noun-pattern
///   null  — ไม่มี signal ให้ใช้ → fallback AI ตามเดิม
/// </summary>
public static class DurableGoodsHeuristic
{
    // ของใช้ทน (อายุ >1 ปี) → Fixed Asset 12xxx
    private static readonly (string Keyword, string PreferredAccountPrefix)[] Durables = new[]
    {
        // คอมพิวเตอร์/อุปกรณ์ IT — 12220 คอมพิวเตอร์
        ("คอมพิวเตอร์", "12220"),
        ("โน้ตบุ๊ก", "12220"),
        ("notebook", "12220"),
        ("laptop", "12220"),
        ("desktop", "12220"),
        ("imac", "12220"),
        ("macbook", "12220"),
        ("หน้าจอ", "12220"),
        ("monitor", "12220"),
        ("จอภาพ", "12220"),
        ("จอคอม", "12220"),
        ("server", "12220"),
        ("เซิร์ฟเวอร์", "12220"),
        ("ซอฟต์แวร์", "12310"),
        ("software", "12310"),
        ("โปรแกรม", "12310"),
        // อุปกรณ์สำนักงาน — 12210
        ("เครื่องปริ้นท์", "12210"),
        ("เครื่องปริ้น", "12210"),
        ("เครื่องพิมพ์", "12210"),
        ("printer", "12210"),
        ("เครื่องถ่ายเอกสาร", "12210"),
        ("photocopier", "12210"),
        ("copier", "12210"),
        ("เครื่องสแกน", "12210"),
        ("scanner", "12210"),
        ("เครื่องโทรสาร", "12210"),
        ("เครื่องแฟกซ์", "12210"),
        ("fax", "12210"),
        ("เครื่องปรับอากาศ", "12210"),
        ("แอร์", "12210"),
        ("air-conditioner", "12210"),
        ("air conditioner", "12210"),
        // เครื่องตกแต่งสำนักงาน — 12230
        ("โต๊ะ", "12230"),
        ("เก้าอี้", "12230"),
        ("ตู้", "12230"),
        ("ชั้นวาง", "12230"),
        ("เครื่องตกแต่ง", "12230"),
        ("furniture", "12230"),
        // เครื่องจักรและอุปกรณ์ — 12260
        ("เครื่องจักร", "12260"),
        ("machinery", "12260"),
        // ยานพาหนะ — 12240
        ("ยานพาหนะ", "12240"),
        ("รถยนต์", "12240"),
        ("รถกระบะ", "12240"),
        ("มอเตอร์ไซค์", "12240"),
        ("vehicle", "12240"),
        ("car", "12240"),
        ("truck", "12240"),
        // อาคาร — 12270
        ("อาคาร", "12270"),
        ("สิ่งปลูกสร้าง", "12270"),
        ("building", "12270"),
    };

    // วัสดุสิ้นเปลือง (ใช้แล้วหมด/อายุ <1 ปี) → Expense 54420 / 5306
    private static readonly (string Keyword, string PreferredAccountPrefix)[] Consumables = new[]
    {
        ("หมึก", "54420"),
        ("ink", "54420"),
        ("toner", "54420"),
        ("cartridge", "54420"),
        ("ตลับหมึก", "54420"),
        ("กระดาษ", "54420"),
        ("paper", "54420"),
        ("ปากกา", "54420"),
        ("ดินสอ", "54420"),
        ("pen", "54420"),
        ("ลวดเย็บ", "54420"),
        ("คลิป", "54420"),
        ("เทป", "54420"),
        ("แฟ้ม", "54420"),
        ("กล่อง", "54420"),
        ("ซอง", "54420"),
        ("supplies", "54420"),
        ("วัสดุ", "54420"),
    };

    /// <summary>capex threshold §65 ตรี (5) — ราคาถึงเกณฑ์นี้ + อายุใช้งาน >1ปี
    /// ต้อง capitalize เสมอ.</summary>
    public const decimal CapexThreshold = 50_000m;

    public static (string? AccountCode, decimal Confidence) Predict(
        string? description, decimal amount,
        IReadOnlyList<GlAccountPrompt.AccountCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(description) || candidates.Count == 0)
            return (null, 0m);
        var d = description.ToLowerInvariant();

        // 1) durable goods → Fixed Asset
        foreach (var (kw, prefix) in Durables)
        {
            if (!d.Contains(kw, StringComparison.OrdinalIgnoreCase)) continue;
            var match = FindBestMatchingAccount(candidates, prefix);
            if (match == null) continue;
            // ราคาถึงเกณฑ์ capex → confidence สูงสุด (must capitalize §65 ตรี (5))
            var conf = amount >= CapexThreshold ? 0.92m : 0.85m;
            return (match, conf);
        }

        // 2) consumables → Expense
        foreach (var (kw, prefix) in Consumables)
        {
            if (!d.Contains(kw, StringComparison.OrdinalIgnoreCase)) continue;
            var match = FindBestMatchingAccount(candidates, prefix);
            if (match == null) continue;
            return (match, 0.80m);
        }

        // 3) capex by amount only — ราคาเกิน threshold แต่ keyword ไม่ตรง
        // ตามรายการ Durables/Consumables → warn เป็น asset ก่อน (conservative)
        if (amount >= CapexThreshold)
        {
            var assetCandidate = candidates.FirstOrDefault(c =>
                c.Code.StartsWith("12") && c.Type == "Asset");
            if (assetCandidate != null)
                return (assetCandidate.Code, 0.65m);  // ต่ำกว่า short-circuit threshold → AI ตัดสิน
        }

        return (null, 0m);
    }

    /// <summary>หา account ที่ code ตรง prefix ที่สุด. ถ้าไม่มี exact ตามที่
    /// ระบุ → คืน asset/expense ใน group ที่ใกล้ที่สุด (เริ่มต้นด้วย 2 ตัวแรก).</summary>
    private static string? FindBestMatchingAccount(
        IReadOnlyList<GlAccountPrompt.AccountCandidate> candidates, string preferred)
    {
        // exact match
        var exact = candidates.FirstOrDefault(c => c.Code == preferred);
        if (exact != null) return exact.Code;
        // same 3-digit group (เช่น 12210 → 122xx)
        var group3 = preferred.Length >= 3 ? preferred[..3] : preferred;
        var groupMatch = candidates.FirstOrDefault(c => c.Code.StartsWith(group3));
        if (groupMatch != null) return groupMatch.Code;
        // same 2-digit group (12xxx) — Asset family
        var group2 = preferred.Length >= 2 ? preferred[..2] : preferred;
        var familyMatch = candidates.FirstOrDefault(c => c.Code.StartsWith(group2));
        return familyMatch?.Code;
    }
}
