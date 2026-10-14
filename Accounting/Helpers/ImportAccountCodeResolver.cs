using Accounting.Models.Enums;
using Accounting.Services.Implementations.Ocr;

namespace Accounting.Helpers;

/// <summary>บัญชีหนึ่งตัวในผังของบริษัท (ป้อน <see cref="ImportAccountCodeResolver"/>) · <see cref="IsPostable"/> = ลงยอดได้ (ไม่ใช่บัญชีคุมที่มีบัญชีย่อย)</summary>
public sealed record ImportChartAccount(Guid AccountId, string Code, string Name, AccountType Type, bool IsPostable);

/// <summary>ความจำการจับคู่ที่ผู้ใช้ยืนยันแล้วในอดีต (AccountMappings ของรอบย้ายระบบก่อน ๆ ของบริษัทเดียวกัน)</summary>
/// <param name="TimesUsed">จำนวนครั้งที่รหัสเดิมนี้ถูกจับไปบัญชีนี้</param>
public sealed record ImportAccountMemory(string LegacyCode, string? LegacyName, Guid AccountId, int TimesUsed);

/// <summary>ผู้สมัครหนึ่งตัว (dropdown + ป้ายแหล่ง)</summary>
/// <param name="Kind">"Code" · "Mapping" · "Ngram" · "Category"</param>
public sealed record ImportAccountCandidate(Guid AccountId, string Code, string Name, decimal Score, string Kind);

/// <summary>
/// ผลหาบัญชี · <see cref="AccountId"/> มีค่า = <b>ระบบเลือกให้เอง</b> (คะแนนถึงเกณฑ์ <see cref="ImportAccountCodeResolver.AutoApplyThreshold"/>) ·
/// null = แนะนำเท่านั้น (ผู้ใช้เลือกจาก <see cref="Candidates"/>) — <b>ห้ามเลือกให้ต่ำกว่าเกณฑ์</b>
/// </summary>
/// <param name="Kind">ชั้นที่ตัดสิน: "Code" · "Mapping" · "Ngram" · "Category" · "None"</param>
/// <param name="Score">ความมั่นใจของผู้สมัครอันดับหนึ่ง (0–1) — แม้ไม่ได้เลือกให้ ก็บอกผู้ใช้ว่าใกล้แค่ไหน</param>
/// <param name="Reason">เหตุผลภาษาไทย (tooltip)</param>
public sealed record ImportAccountResolution(
    Guid? AccountId,
    string? AccountCode,
    string? AccountName,
    string Kind,
    decimal Score,
    string Reason,
    IReadOnlyList<ImportAccountCandidate> Candidates)
{
    public bool AutoApplied => AccountId.HasValue;
}

/// <summary>
/// **จับคู่รหัสบัญชีจากระบบเดิม → ผังบัญชีของบริษัท — ตัวตัดสินเดียว** (รอบ 205 ทีม D · IMPORT_ETL_REVIEW §2.3/§2.6)
///
/// <para>ผู้ใช้: งบทดลองยกมา (Migration wizard · ศูนย์นำเข้า) · บรรทัดเอกสารที่ระบุรหัสบัญชี · AI <c>ImportAccountMap</c> (58) ใช้ผลนี้เป็น
/// local / cold start (กฎเหล็ก #1 — ปิด AI ทั้งระบบแล้วทุกชั้นนี้ยังทำงาน)</para>
///
/// <para>ลำดับชั้น (ชั้นบนชนะ · ชั้นล่างห้ามทับชั้นบน):</para>
/// <list type="number">
/// <item><b>รหัสตรง</b> (เทียบแบบตัดช่องว่าง/ขีด/จุด) กับบัญชีที่ลงยอดได้ — ชื่อขัดกันชัดเจน (n-gram &lt; 0.30 และหมวดเลขหน้าต่างกัน) ⇒ ลดเป็นคำแนะนำ</item>
/// <item><b>ความจำ</b> — รหัสเดิมเดียวกันที่บริษัทนี้เคยยืนยันแล้ว (AccountMappings) · ยืนยันไปทางเดียว = เลือกให้ · เคยไปหลายทาง = แนะนำ</item>
/// <item><b>ชื่อคล้าย</b> (<see cref="CharNgramSimilarity"/>) — เลือกให้เฉพาะ ≥ <see cref="NgramAutoThreshold"/> และห่างอันดับสอง ≥ 0.05 · หมวดเลขหน้าไม่ตรง = ลดคะแนน</item>
/// <item><b>หมวดเลขหน้า</b> 1–5 (สินทรัพย์/หนี้สิน/ทุน/รายได้/ค่าใช้จ่าย ตามผังมาตรฐานไทย) — แนะนำผู้สมัครในหมวดเท่านั้น ไม่เลือกให้</item>
/// </list>
/// <para>G6: pure · ไม่มี I/O · ไม่ขึ้นกับ culture · ตัวเลขคะแนนเป็น decimal 4 ตำแหน่ง (ตรง <c>ImportStagedRow.MatchScore numeric(5,4)</c>)</para>
/// </summary>
public static class ImportAccountCodeResolver
{
    /// <summary>คะแนนขั้นต่ำที่ระบบ "เลือกให้" — ต่ำกว่านี้เป็นคำแนะนำเสมอ (กฎเหล็ก #3 ข้อ 3: &lt; 0.85 = เหลือง)</summary>
    public const decimal AutoApplyThreshold = 0.85m;

    /// <summary>ชื่อคล้ายต้องสูงกว่านี้จึงเลือกให้ (ชื่อบัญชีสั้น — "ลูกหนี้การค้า" vs "ลูกหนี้อื่น" ใกล้กันมาก)</summary>
    public const decimal NgramAutoThreshold = 0.90m;

    /// <summary>ชื่อคล้ายต่ำกว่านี้ไม่เสนอเป็นผู้สมัคร (0.6+ = "น่าพิจารณา" ตามเอกสารของ CharNgramSimilarity)</summary>
    public const decimal NgramSuggestThreshold = 0.55m;

    private const int MaxCandidates = 5;

    /// <summary>หาบัญชีของแถวหนึ่ง — <paramref name="chart"/> = ผังของบริษัท (active · ไม่ถูกลบ) · <paramref name="memory"/> = ความจำของบริษัทเดียวกัน</summary>
    public static ImportAccountResolution Resolve(string? legacyCode, string? legacyName,
        IReadOnlyList<ImportChartAccount> chart, IReadOnlyList<ImportAccountMemory>? memory = null)
    {
        var code = NormalizeCode(legacyCode);
        var name = (legacyName ?? "").Trim();
        var cls = ClassFromCode(legacyCode);
        var candidates = new List<ImportAccountCandidate>();

        // ── ชั้น 1: รหัสตรง ──
        if (code.Length > 0)
        {
            var exact = chart.Where(a => NormalizeCode(a.Code) == code).ToList();
            var postable = exact.FirstOrDefault(a => a.IsPostable);
            if (postable != null)
            {
                var nameSim = name.Length == 0 ? 1m : Sim(name, postable.Name);
                var conflicting = name.Length > 0 && nameSim < 0.30m && cls.HasValue && cls.Value != postable.Type;
                if (!conflicting)
                    return Picked(postable, "Code", 1m,
                        name.Length == 0 || nameSim >= 0.6m
                            ? $"รหัส {postable.Code} ตรงกับผังบัญชี"
                            : $"รหัส {postable.Code} ตรงกับผังบัญชี (ชื่อในระบบ \"{postable.Name}\" ต่างจากไฟล์ \"{name}\" — ตรวจอีกครั้ง)",
                        candidates);
                candidates.Add(new(postable.AccountId, postable.Code, postable.Name, 0.60m, "Code"));
            }
            else if (exact.Count > 0)
            {
                // รหัสตรงกับบัญชีคุม (มีบัญชีย่อย) — ลงยอดที่บัญชีคุมไม่ได้ ⇒ เสนอบัญชีย่อยใต้รหัสนั้น
                foreach (var child in chart.Where(a => a.IsPostable && NormalizeCode(a.Code).StartsWith(code, StringComparison.Ordinal))
                             .OrderByDescending(a => name.Length == 0 ? 0m : Sim(name, a.Name)).ThenBy(a => a.Code, StringComparer.Ordinal).Take(3))
                    candidates.Add(new(child.AccountId, child.Code, child.Name, 0.50m, "Code"));
            }
        }

        // ── ชั้น 2: ความจำของบริษัท ──
        if (code.Length > 0 && memory is { Count: > 0 })
        {
            var byAccount = memory.Where(m => NormalizeCode(m.LegacyCode) == code)
                .GroupBy(m => m.AccountId)
                .Select(g => (AccountId: g.Key, Times: g.Sum(x => x.TimesUsed)))
                .OrderByDescending(x => x.Times).ToList();
            var live = byAccount.Select(x => (x.Times, Acc: chart.FirstOrDefault(a => a.AccountId == x.AccountId && a.IsPostable)))
                .Where(x => x.Acc != null).ToList();
            if (live.Count == 1)
                return Picked(live[0].Acc!, "Mapping", 0.95m,
                    $"เคยจับรหัสเดิม {legacyCode?.Trim()} ไปบัญชี {live[0].Acc!.Code} แล้ว {live[0].Times} ครั้ง", candidates);
            foreach (var x in live)
                candidates.Add(new(x.Acc!.AccountId, x.Acc.Code, x.Acc.Name, 0.80m, "Mapping"));
        }

        // ── ชั้น 3: ชื่อคล้าย ──
        if (name.Length > 0)
        {
            var scored = chart.Where(a => a.IsPostable)
                .Select(a =>
                {
                    var s = Sim(name, a.Name);
                    if (cls.HasValue && cls.Value != a.Type) s = Math.Round(s * 0.8m, 4, MidpointRounding.AwayFromZero);
                    return (Acc: a, Score: s);
                })
                .Where(x => x.Score >= NgramSuggestThreshold)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Acc.Code, StringComparer.Ordinal)
                .Take(MaxCandidates).ToList();
            if (scored.Count > 0)
            {
                var top = scored[0];
                var gap = scored.Count > 1 ? top.Score - scored[1].Score : 1m;
                if (top.Score >= NgramAutoThreshold && gap >= 0.05m && candidates.Count == 0)
                    return Picked(top.Acc, "Ngram", top.Score, $"ชื่อ \"{name}\" ใกล้เคียงบัญชี {top.Acc.Code} {top.Acc.Name}", candidates);
                foreach (var x in scored)
                    candidates.Add(new(x.Acc.AccountId, x.Acc.Code, x.Acc.Name, x.Score, "Ngram"));
            }
        }

        // ── ชั้น 4: หมวดเลขหน้า (แนะนำเท่านั้น) ──
        if (cls.HasValue && candidates.Count < MaxCandidates)
        {
            var inClass = chart.Where(a => a.IsPostable && a.Type == cls.Value && candidates.All(c => c.AccountId != a.AccountId))
                .Select(a => (Acc: a, Score: name.Length == 0 ? 0m : Sim(name, a.Name)))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Acc.Code, StringComparer.Ordinal)
                .Take(MaxCandidates - candidates.Count);
            foreach (var x in inClass)
                candidates.Add(new(x.Acc.AccountId, x.Acc.Code, x.Acc.Name, Math.Min(0.40m, Math.Max(0.20m, x.Score)), "Category"));
        }

        var ranked = candidates.GroupBy(c => c.AccountId).Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score).ThenBy(c => c.Code, StringComparer.Ordinal).Take(MaxCandidates).ToList();
        if (ranked.Count == 0)
            return new(null, null, null, "None", 0m,
                "ไม่พบบัญชีที่ใกล้เคียงในผังบัญชี — เลือกบัญชีเอง หรือสร้างบัญชีในผังบัญชีก่อนแล้ววิเคราะห์ใหม่", ranked);
        var best = ranked[0];
        var kindLabel = KindLabel(best.Kind);
        return new(null, null, null, best.Kind, best.Score,
            $"ยังไม่เลือกให้ (ความมั่นใจ {best.Score:0.00} ต่ำกว่าเกณฑ์ {AutoApplyThreshold:0.00}) — แนะนำ {best.Code} {best.Name} ({kindLabel})", ranked);
    }

    /// <summary>หมวดบัญชีจากเลขหน้าตามผังมาตรฐานไทย: 1 สินทรัพย์ · 2 หนี้สิน · 3 ส่วนของเจ้าของ · 4 รายได้ · 5 ค่าใช้จ่าย · อื่น/ไม่มีเลข = null (ไม่เดา)</summary>
    public static AccountType? ClassFromCode(string? code)
    {
        var c = NormalizeCode(code);
        if (c.Length == 0 || !char.IsAsciiDigit(c[0])) return null;
        return c[0] switch
        {
            '1' => AccountType.Asset,
            '2' => AccountType.Liability,
            '3' => AccountType.Equity,
            '4' => AccountType.Revenue,
            '5' => AccountType.Expense,
            _ => null,
        };
    }

    /// <summary>รหัสรูปเทียบ — ตัดช่องว่าง/ขีด/จุด/ทับ · เลขไทย → อารบิก · ตัวพิมพ์ใหญ่ ("1110-01" = "111001" = "๑๑๑๐๐๑")</summary>
    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var sb = new System.Text.StringBuilder(code.Length);
        foreach (var ch in code.Trim())
        {
            if (ch is ' ' or '-' or '.' or '/' or '_' or ' ') continue;
            if (ch >= '๐' && ch <= '๙') sb.Append((char)('0' + (ch - '๐')));
            else sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>ป้ายแหล่งภาษาไทยของชั้น — ป้ายเดียวกันทุกหน้า (เซิร์ฟเวอร์ตัดสิน · F2 ข้อ 5) · AI ติดป้ายเฉพาะผู้เรียกที่เรียก AI จริง</summary>
    public static string KindLabel(string? kind) => kind switch
    {
        "Code" => "📌 รหัสตรง",
        "Mapping" => "🧠 เคยจับคู่",
        "Ngram" => "📚 ชื่อคล้าย",
        "Category" => "⚙️ หมวดบัญชี",
        "Ai" => "🤖 AI แนะนำ",
        "User" => "✋ ผู้ใช้เลือก",
        _ => "—",
    };

    private static ImportAccountResolution Picked(ImportChartAccount a, string kind, decimal score, string reason,
        List<ImportAccountCandidate> others)
    {
        var list = new List<ImportAccountCandidate> { new(a.AccountId, a.Code, a.Name, score, kind) };
        list.AddRange(others.Where(o => o.AccountId != a.AccountId));
        return new(a.AccountId, a.Code, a.Name, kind, score, reason, list.Take(MaxCandidates).ToList());
    }

    private static decimal Sim(string a, string b)
        => Math.Round((decimal)CharNgramSimilarity.Similarity(Squash(a), Squash(b)), 4, MidpointRounding.AwayFromZero);

    /// <summary>ตัดช่องว่างซ้ำ/วงเล็บท้ายชื่อ ("เงินสด (สนญ.)") ก่อนเทียบ</summary>
    private static string Squash(string s)
        => string.Join(' ', s.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
