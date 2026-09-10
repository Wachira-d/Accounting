using System.Text;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ระดับความแน่นของหลักฐานที่บอกว่า "สแกนใบนี้ต่อเนื่องจากเอกสารใบนั้น"</summary>
public enum PredecessorMatchStrength
{
    None = 0,
    /// <summary>รายการบนกระดาษคล้ายบรรทัดในเอกสารเดิม — อ่อน: เสนอให้คนเลือกเท่านั้น</summary>
    LineOverlap = 1,
    /// <summary>ยอดรวมตรงกัน (±1 บาท) และเป็นคู่ค้ารายเดียวกัน — ผูกอัตโนมัติได้เมื่อมีใบเดียว</summary>
    ExactTotal = 2,
    /// <summary>เลขที่ของเอกสารเดิมพิมพ์อยู่บนกระดาษ — แน่นที่สุด</summary>
    ExplicitReference = 3,
}

/// <summary>เอกสารในระบบที่อาจเป็น "ใบต้นทาง" ของสแกน (โหลดโดย service — helper นี้ไม่แตะ DB)</summary>
public sealed record PredecessorCandidate(
    Guid Id, DocumentType Type, string DocumentNumber, DateTime DocumentDate,
    decimal TotalAmount, decimal BalanceDue, IReadOnlyList<string> LineDescriptions);

/// <summary>ข้อเท็จจริงจากกระดาษที่ใช้เทียบ</summary>
public sealed record PredecessorScanFacts(
    string? RawText, decimal? TotalAmount, decimal? SubTotal, IReadOnlyList<string> LineDescriptions);

public sealed record RankedPredecessor(PredecessorCandidate Candidate, PredecessorMatchStrength Strength, int Score, string Reason);

/// <summary>ผลตัดสิน: <see cref="AutoLink"/> = ผูกให้ทันที (มีเมื่อชัดพอ) · <see cref="Candidates"/> =
/// รายการเรียงตามความน่าจะเป็นให้คนเลือก (ว่าง = ไม่มีอะไรเกี่ยว)</summary>
public sealed record PredecessorDecision(RankedPredecessor? AutoLink, IReadOnlyList<RankedPredecessor> Candidates, string Summary);

/// <summary>
/// **ตัวตัดสิน "สแกนใบนี้ต่อเนื่องจากเอกสารใบไหนในระบบ" — ตัวเดียวของทุกชนิดเอกสาร**
///
/// <para>ที่มา (ผู้ใช้ 2026-09-10): "ในระบบมี PO ของบริษัทบุญทรัพย์ยอด 599 อยู่แล้ว พอ OCR ใบแจ้งหนี้ซื้อ
/// ของบุญทรัพย์ยอด 599 เข้ามา ระบบควรวิเคราะห์และผูกใบกันให้เลย ถ้ามีเลข PO อ้างอิงตรงยิ่งชัวร์ —
/// คิดให้ครอบคลุมทุกประเภทเอกสาร". เดิมมีแค่ PO→ใบซื้อ และผูกอัตโนมัติเฉพาะเมื่อเลข PO พิมพ์
/// บนกระดาษ ยอดตรงกันไม่นับ · ฝั่งขาย (ใบเสนอราคา→ใบแจ้งหนี้ · ใบแจ้งหนี้→ใบเสร็จ) ไม่มีเลย</para>
///
/// <para>กติกา (จากแน่นไปอ่อน):
/// <list type="number">
/// <item><b>เลขที่เอกสารเดิมอยู่บนกระดาษ</b> (เทียบแบบตัดช่องว่าง/ขีด, ต้องยาวพอไม่ให้ชนตัวเลขอื่น) —
/// ใบเดียว ⇒ ผูกอัตโนมัติ · หลายใบ ⇒ ให้คนเลือก (กระดาษอ้างหลายใบ = วางบิลรวม)</item>
/// <item><b>ยอดรวมตรง ±1 บาท</b> (เทียบทั้ง TotalAmount และยอดค้าง — ใบที่วางบิลบางส่วนแล้ว) และ
/// คู่ค้าเดียวกัน (service กรองมาแล้ว) — ใบเดียว ⇒ ผูกอัตโนมัติ · หลายใบยอดเท่ากัน ⇒ ให้คนเลือก
/// ("ค่าที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้ อันตรายกว่าการไม่ตอบ")</item>
/// <item><b>รายการคล้ายกัน</b> — เสนอเท่านั้น ไม่ผูกเอง (ผู้ขายรายเดียวขายของซ้ำ ๆ ทุกเดือน)</item>
/// </list>
/// ไม่มีหลักฐานเลย = ไม่เสนอ (ไม่ใช่เสนอใบล่าสุดเพราะ "น่าจะใช่") — และ helper นี้ <b>ไม่รู้จัก DB</b>
/// เพื่อให้เทสต์ล็อกทุกกฎด้วยข้อมูลจริงได้</para>
/// </summary>
public static class OcrPredecessorMatcher
{
    /// <summary>ยอดต่างได้ไม่เกินนี้ถือว่า "ตรง" — กันเศษปัด/OCR อ่านสตางค์เพี้ยน แต่ไม่กว้างจนใบคนละยอดผ่าน</summary>
    public const decimal AmountTolerance = 1.00m;

    /// <summary>ความคล้ายของบรรทัด (bigram Dice) ที่นับว่า "บรรทัดเดียวกัน" — ค่าเดียวกับตัวจับคู่บรรทัด PO บนหน้าเว็บ</summary>
    public const double LineSimilarityThreshold = 0.5;

    /// <summary>สัดส่วนบรรทัดบนกระดาษที่ต้องเจอคู่ ถึงจะนับเป็นหลักฐาน LineOverlap</summary>
    public const double LineOverlapRatio = 0.5;

    public const int MaxCandidates = 5;

    public static PredecessorDecision Decide(PredecessorScanFacts facts, IEnumerable<PredecessorCandidate> candidates)
    {
        var list = candidates?.ToList() ?? new List<PredecessorCandidate>();
        if (list.Count == 0) return new PredecessorDecision(null, Array.Empty<RankedPredecessor>(), "ไม่มีเอกสารต้นทางที่เปิดอยู่ของคู่ค้ารายนี้");

        var rawNorm = NormalizeForReference(facts.RawText);
        var ranked = new List<RankedPredecessor>();
        // เรียงใหม่ก่อนสุดก่อน — คะแนน "ความใหม่" เล็ก ๆ ใช้ตัดสินลำดับในลิสต์เท่านั้น ไม่มีผลต่อการผูกอัตโนมัติ
        var byRecency = list.OrderByDescending(c => c.DocumentDate).ToList();
        for (var i = 0; i < byRecency.Count; i++)
        {
            var c = byRecency[i];
            var reasons = new List<string>();
            var strength = PredecessorMatchStrength.None;
            var score = 0;

            if (ReferenceAppears(rawNorm, c.DocumentNumber))
            {
                strength = PredecessorMatchStrength.ExplicitReference;
                score += 100;
                reasons.Add($"เลขที่ {c.DocumentNumber} พิมพ์อยู่บนเอกสาร");
            }

            var totalHit = AmountMatches(facts, c);
            if (totalHit != null)
            {
                if (strength < PredecessorMatchStrength.ExactTotal) strength = PredecessorMatchStrength.ExactTotal;
                score += 50;
                reasons.Add(totalHit);
            }

            var overlap = LineOverlap(facts.LineDescriptions, c.LineDescriptions);
            if (overlap >= LineOverlapRatio)
            {
                if (strength < PredecessorMatchStrength.LineOverlap) strength = PredecessorMatchStrength.LineOverlap;
                score += (int)Math.Round(30 * overlap, MidpointRounding.AwayFromZero);
                reasons.Add($"รายการคล้ายกัน {Math.Round(overlap * 100, MidpointRounding.AwayFromZero):0}%");
            }

            if (strength == PredecessorMatchStrength.None) continue;
            score += Math.Max(0, 5 - i); // ใบใหม่กว่าอยู่บนกว่าเมื่อหลักฐานเท่ากัน
            ranked.Add(new RankedPredecessor(c, strength, score, string.Join(" · ", reasons)));
        }

        ranked = ranked.OrderByDescending(r => r.Score).ThenByDescending(r => r.Candidate.DocumentDate).ToList();
        var shown = ranked.Take(MaxCandidates).ToList();

        var explicitHits = ranked.Where(r => r.Strength == PredecessorMatchStrength.ExplicitReference).ToList();
        if (explicitHits.Count == 1)
            return new PredecessorDecision(explicitHits[0], shown,
                $"ผูกอัตโนมัติ: {explicitHits[0].Reason}");
        if (explicitHits.Count > 1)
            return new PredecessorDecision(null, shown,
                $"กระดาษอ้างเลขที่เอกสาร {explicitHits.Count} ใบ ({string.Join(", ", explicitHits.Select(h => h.Candidate.DocumentNumber))}) — ให้ผู้ใช้เลือก");

        var totalHits = ranked.Where(r => r.Strength == PredecessorMatchStrength.ExactTotal).ToList();
        if (totalHits.Count == 1)
            return new PredecessorDecision(totalHits[0], shown,
                $"ผูกอัตโนมัติ: {totalHits[0].Reason} (คู่ค้าเดียวกัน · ใบเดียวที่ยอดตรง)");
        if (totalHits.Count > 1)
            return new PredecessorDecision(null, shown,
                $"ยอดตรงกับเอกสาร {totalHits.Count} ใบ ({string.Join(", ", totalHits.Select(h => h.Candidate.DocumentNumber))}) — ให้ผู้ใช้เลือก");

        if (shown.Count > 0)
            return new PredecessorDecision(null, shown,
                $"พบเอกสารที่รายการคล้ายกัน {shown.Count} ใบ แต่ยอด/เลขที่ไม่ตรง — ให้ผู้ใช้เลือก");

        return new PredecessorDecision(null, Array.Empty<RankedPredecessor>(),
            $"คู่ค้ารายนี้มีเอกสารเปิดอยู่ {list.Count} ใบ แต่ไม่มีใบไหนที่เลขที่/ยอด/รายการตรงกับกระดาษ");
    }

    /// <summary>เลขที่เอกสารเดิมปรากฏบนกระดาษไหม — เทียบหลังตัดช่องว่าง/ขีด/จุด (OCR มักอ่าน "PO-2026-0012"
    /// เป็น "PO 2026 0012") · เลขสั้นเกินหรือเป็นตัวเลขล้วนสั้น ๆ ไม่นับ (จะไปชนยอดเงิน/วันที่บนกระดาษ)</summary>
    public static bool ReferenceAppears(string normalizedRawText, string? documentNumber)
    {
        if (string.IsNullOrWhiteSpace(normalizedRawText) || string.IsNullOrWhiteSpace(documentNumber)) return false;
        var n = NormalizeForReference(documentNumber);
        if (n.Length < 6) return false;
        var hasLetter = n.Any(char.IsLetter);
        if (!hasLetter && n.Length < 8) return false;
        return normalizedRawText.Contains(n, StringComparison.Ordinal);
    }

    /// <summary>ตัวพิมพ์ใหญ่ · เหลือเฉพาะตัวอักษร/ตัวเลข (ไทยรวมด้วย) — ใช้ทั้งฝั่งกระดาษและฝั่งเลขที่</summary>
    public static string NormalizeForReference(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        return sb.ToString();
    }

    private static string? AmountMatches(PredecessorScanFacts facts, PredecessorCandidate c)
    {
        if (facts.TotalAmount is { } t && t > 0)
        {
            if (Math.Abs(t - c.TotalAmount) <= AmountTolerance) return $"ยอดรวม {t:N2} ตรงกับเอกสาร";
            if (c.BalanceDue > 0 && Math.Abs(t - c.BalanceDue) <= AmountTolerance) return $"ยอดรวม {t:N2} ตรงกับยอดค้างของเอกสาร";
        }
        // กระดาษบางใบพิมพ์แต่ยอดก่อน VAT ขณะที่เอกสารเดิมเป็นยอดรวม VAT (หรือกลับกัน)
        if (facts.SubTotal is { } s && s > 0 && Math.Abs(s - c.TotalAmount) <= AmountTolerance)
            return $"ยอดก่อน VAT {s:N2} ตรงกับยอดเอกสาร";
        return null;
    }

    /// <summary>สัดส่วนบรรทัดบนกระดาษที่หาคู่ในเอกสารเดิมได้ (0–1) — ใช้ bigram Dice เพราะข้อความไทยไม่มีช่องว่าง</summary>
    public static double LineOverlap(IReadOnlyList<string>? scanLines, IReadOnlyList<string>? docLines)
    {
        var a = (scanLines ?? Array.Empty<string>()).Select(NormalizeLine).Where(x => x.Length >= 2).ToList();
        var b = (docLines ?? Array.Empty<string>()).Select(NormalizeLine).Where(x => x.Length >= 2).ToList();
        if (a.Count == 0 || b.Count == 0) return 0;
        var used = new HashSet<int>();
        var matched = 0;
        foreach (var x in a)
        {
            var bestIdx = -1; var best = 0.0;
            for (var j = 0; j < b.Count; j++)
            {
                if (used.Contains(j)) continue;
                var s = Similarity(x, b[j]);
                if (s > best) { best = s; bestIdx = j; }
            }
            if (bestIdx >= 0 && best >= LineSimilarityThreshold) { used.Add(bestIdx); matched++; }
        }
        return (double)matched / a.Count;
    }

    private static string NormalizeLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static double Similarity(string x, string y)
    {
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal)) return 1;
        var bx = Bigrams(x); var by = Bigrams(y);
        if (bx.Count == 0 || by.Count == 0) return 0;
        var inter = bx.Count(g => by.Contains(g));
        return 2.0 * inter / (bx.Count + by.Count);
    }

    private static HashSet<string> Bigrams(string s)
    {
        var set = new HashSet<string>();
        for (var i = 0; i + 1 < s.Length; i++) set.Add(s.Substring(i, 2));
        return set;
    }
}
