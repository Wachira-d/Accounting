namespace Accounting.Helpers;

/// <summary>ระดับผลจับคู่ใบ 50 ทวิ กับแถวเครดิตที่รอใบ (AUTO_SEND §3.7) — ไม่มี null (DOCTRINE G3)</summary>
public enum WhtCertMatchLevel
{
    /// <summary>K0 — ใบนี้ไม่ได้ออกให้บริษัทเรา</summary>
    Rejected,
    /// <summary>K1 — ผู้จ่ายไม่อยู่ในแถวที่รอใบ / ไม่มีแถวรอใบ</summary>
    NoCandidate,
    /// <summary>K4 — หลายชุดรวมได้ยอดเท่ากัน หรือมากกว่าหนึ่งแถวยอดเท่ากัน ⇒ ไม่เลือกเอง</summary>
    Ambiguous,
    /// <summary>K5 — ภาษีบนใบน้อยกว่ายอดค้างของแถวเดียว (ผ่อนงวด)</summary>
    Partial,
    /// <summary>K6 — ภาษีบนใบมากกว่ายอดค้างทั้งหมดของผู้จ่ายนี้</summary>
    Over,
    /// <summary>K3/K4 — ตรงพอดี (1:1 หรือ 1:n)</summary>
    Exact,
}

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §3.7) — <b>กติกาจับคู่ใบ 50 ทวิ ↔ แถวเครดิตที่รอใบ</b> (pure · DOCTRINE G6) ·
/// การจับคู่เป็นเลขคณิต + กุญแจ (เลขผู้เสียภาษี) = คำนวณได้ ⇒ <b>ห้ามถาม AI</b> (DOCTRINE §2.1) · ตัวเลขล้วนห้าม fuzzy (หลักการ #3)
///
/// <list type="number">
/// <item>K0 เลขผู้เสียภาษี<b>ผู้ถูกหัก</b>บนใบ ≠ ของเรา ⇒ Rejected (ไม่จับคู่ ไม่เครดิต)</item>
/// <item>K1 จำกัดแถวเหลือผู้จ่ายรายนี้ (เลขผู้เสียภาษีตรงเป๊ะ · ใบไม่มีเลข ⇒ ใช้ผู้ติดต่อที่ระบบจับได้)</item>
/// <item>K2 เอกสารที่ผู้ส่งระบุ (ลิงก์/หน้าเอกสาร) เป็นชุดแรก</item>
/// <item>K3 ยอดเดี่ยว |ภาษีบนใบ − ยอดค้างของแถว| ≤ <see cref="Tolerance"/> ⇒ 1:1 (หลายแถวตรง ⇒ Ambiguous + เสนอแถวที่วันที่ใกล้สุด · ไม่ auto)</item>
/// <item>K4 subset ≤ <see cref="MaxSubsetRows"/> แถวของผู้จ่ายเดียวกันที่รวมได้ภาษีบนใบ ⇒ 1:n · หลายชุด ⇒ Ambiguous</item>
/// <item>K5 ภาษีบนใบ &lt; ยอดค้างของแถวเดียว ⇒ Partial (เสนอ allocation บางส่วนที่แถวเก่าสุด — ไม่ auto)</item>
/// <item>K6 ภาษีบนใบ &gt; Σ ยอดค้างของผู้จ่าย ⇒ Over (มักเป็นการรับชำระที่ไม่ได้บันทึก WHT)</item>
/// </list>
/// </summary>
public static class WhtCertMatchPolicy
{
    /// <summary>ส่วนต่างที่ยอมได้ (บาท) — ค่าเดิมของ <c>OcrService.EnsureWhtCreditFromCertAsync</c> (matchTolerance) ย้ายมาเป็นค่าคงที่เดียว</summary>
    public const decimal Tolerance = 1m;
    public const int MaxSubsetRows = 12;

    /// <summary>ข้อมูลบนใบที่ใช้จับคู่</summary>
    public sealed record Cert(string? PayerTaxId, Guid? PayerContactId, string? PayeeTaxId, DateTime? CertificateDate, decimal WhtAmount);

    /// <summary>แถวเครดิตที่รอใบ (<c>Outstanding</c> = ถูกหัก − ได้ใบแล้ว)</summary>
    public sealed record Candidate(Guid CreditId, Guid? DocumentId, string? PayerTaxId, Guid? PayerContactId, decimal Outstanding, DateTime WithheldAt);

    public sealed record Allocation(Guid CreditId, decimal Amount);

    public sealed record MatchResult(WhtCertMatchLevel Level, IReadOnlyList<Allocation> Allocations, string Reason);

    public static MatchResult Match(Cert cert, string? ourTaxId, IReadOnlyList<Candidate> candidates, IReadOnlyCollection<Guid>? documentHints = null)
    {
        // K0 — ข้อเท็จจริงแข็ง: ใบออกให้คนอื่น (ไม่รู้ผู้ถูกหัก ≠ คนอื่น — ด่าน WhtCertIntakeVerdict.Gates จะไม่ให้ auto แทน)
        var payee = ThaiTaxId.Normalize(cert.PayeeTaxId);
        var ours = ThaiTaxId.Normalize(ourTaxId);
        if (payee.Length == 13 && ours.Length == 13 && payee != ours)
            return new(WhtCertMatchLevel.Rejected, Array.Empty<Allocation>(), "ใบนี้ไม่ได้ออกให้บริษัทเรา — เลขผู้เสียภาษีของผู้ถูกหักบนใบไม่ตรงกับบริษัท");

        if (cert.WhtAmount <= 0m)
            return new(WhtCertMatchLevel.NoCandidate, Array.Empty<Allocation>(), "อ่านยอดภาษีที่ถูกหักบนใบไม่ได้ — กรอกยอดแล้วเลือกเอกสารเอง");

        // K1 — ผู้จ่ายรายนี้เท่านั้น (เลขตรงเป๊ะ · ใบไม่มีเลขผู้จ่าย ⇒ ผู้ติดต่อที่ระบบจับได้)
        var payer = ThaiTaxId.Normalize(cert.PayerTaxId);
        var pool = candidates.Where(c => c.Outstanding > 0.005m && (payer.Length == 13
                ? ThaiTaxId.Normalize(c.PayerTaxId) == payer
                : cert.PayerContactId is Guid pc && c.PayerContactId == pc))
            .ToList();
        if (pool.Count == 0)
            return new(WhtCertMatchLevel.NoCandidate, Array.Empty<Allocation>(),
                payer.Length == 13
                    ? "ไม่มีรายการรอ 50 ทวิ ของผู้จ่ายรายนี้ — ตรวจว่าบันทึกรับชำระพร้อมยอดหัก ณ ที่จ่ายแล้ว หรือบันทึกเป็นรายการใหม่"
                    : "ใบไม่มีเลขผู้เสียภาษีของผู้จ่าย และระบบจับผู้ติดต่อไม่ได้ — เลือกเอกสารเอง");

        // K2 — เอกสารที่ผู้ส่งระบุเป็นชุดแรก
        if (documentHints is { Count: > 0 })
        {
            var hinted = pool.Where(c => c.DocumentId is Guid d && documentHints.Contains(d)).ToList();
            if (hinted.Count > 0)
            {
                var r = MatchWithin(cert, hinted);
                if (r.Level == WhtCertMatchLevel.Exact) return r;
            }
        }
        return MatchWithin(cert, pool);
    }

    private static MatchResult MatchWithin(Cert cert, List<Candidate> pool)
    {
        var tax = cert.WhtAmount;
        var anchor = cert.CertificateDate;
        long Dist(Candidate c) => anchor is DateTime a ? Math.Abs((c.WithheldAt.Date - a.Date).Ticks) : 0L;

        // K3 — ยอดเดี่ยว
        var singles = pool.Where(c => Math.Abs(c.Outstanding - tax) <= Tolerance).OrderBy(Dist).ThenBy(c => c.CreditId).ToList();
        if (singles.Count == 1)
            return new(WhtCertMatchLevel.Exact, new[] { new Allocation(singles[0].CreditId, R(Math.Min(tax, singles[0].Outstanding))) },
                "ยอดภาษีบนใบตรงกับรายการรอใบหนึ่งรายการ");
        // ฝ่ายค้าน WP-09 L1: หลายรายการยอดเท่ากัน ⇒ กำกวมเสมอ (วันที่ใกล้กว่าไม่ใช่หลักฐานว่าเป็นใบของรายการนั้น) — เสนอรายการที่ใกล้วันที่บนใบที่สุด
        // เป็น "ข้อเสนอ" ตัวเดียว (Allocations[0]) ให้คนยืนยัน · ไม่ auto (ด่าน Level ≠ Exact)
        if (singles.Count > 1)
            return new(WhtCertMatchLevel.Ambiguous, new[] { new Allocation(singles[0].CreditId, R(Math.Min(tax, singles[0].Outstanding))) },
                $"มี {singles.Count} รายการที่ยอดเท่ากับใบนี้ — ระบบเสนอรายการที่ถูกหักใกล้วันที่บนใบที่สุด ตรวจแล้วเลือกเอกสารที่ใบนี้ครอบ");

        var total = pool.Sum(c => c.Outstanding);
        // K6 — เกินยอดค้างทั้งหมด
        if (tax > total + Tolerance)
            return new(WhtCertMatchLevel.Over, Array.Empty<Allocation>(),
                $"ภาษีบนใบ {tax:N2} มากกว่ายอดที่บันทึกว่าถูกหักทั้งหมดของผู้จ่ายรายนี้ ({total:N2}) — ตรวจการรับชำระที่ไม่ได้บันทึกหัก ณ ที่จ่าย");

        // K4 — subset ที่รวมได้ภาษีบนใบ (ใกล้วันที่บนใบก่อน · ไม่เกิน MaxSubsetRows แถว)
        var ordered = pool.OrderBy(Dist).ThenBy(c => c.CreditId).Take(MaxSubsetRows).ToList();
        var found = new List<List<Candidate>>();
        FindSubsets(ordered, 0, new List<Candidate>(), 0m, tax, found);
        var multi = found.Where(s => s.Count >= 2).ToList();
        if (multi.Count == 1)
            return new(WhtCertMatchLevel.Exact, multi[0].Select(c => new Allocation(c.CreditId, R(c.Outstanding))).ToList(),
                $"ใบเดียวครอบ {multi[0].Count} รายการ — ยอดรวมตรงกับภาษีบนใบ");
        if (multi.Count > 1)
            return new(WhtCertMatchLevel.Ambiguous, Array.Empty<Allocation>(),
                $"มีหลายชุดรายการที่รวมกันได้ยอดเท่ากับใบนี้ ({multi.Count} ชุด) — เลือกเอกสารที่ใบนี้ครอบเอง");

        // K5 — บางส่วน (ใบน้อยกว่ายอดค้างของแถวใดแถวหนึ่ง) ⇒ เสนอแถวที่ใกล้วันที่บนใบที่สุด
        var partial = pool.Where(c => c.Outstanding > tax + Tolerance).OrderBy(Dist).ThenBy(c => c.WithheldAt).ThenBy(c => c.CreditId).FirstOrDefault();
        if (partial != null)
            return new(WhtCertMatchLevel.Partial, new[] { new Allocation(partial.CreditId, R(tax)) },
                $"ภาษีบนใบ {tax:N2} น้อยกว่ายอดค้างของรายการ ({partial.Outstanding:N2}) — เสนอเป็นได้รับบางส่วน (ผ่อนงวด) ให้ผู้ใช้ยืนยัน");

        return new(WhtCertMatchLevel.Ambiguous, Array.Empty<Allocation>(),
            "ยอดภาษีบนใบไม่ตรงกับรายการหรือชุดรายการใดของผู้จ่ายรายนี้ — เลือกเอกสารที่ใบนี้ครอบเอง");
    }

    /// <summary>ค้นทุกชุดที่รวมได้ <paramref name="target"/> ±Tolerance — หยุดที่ 2 ชุด (พอบอกว่ากำกวมแล้ว)</summary>
    private static void FindSubsets(List<Candidate> items, int start, List<Candidate> pick, decimal sum, decimal target, List<List<Candidate>> found)
    {
        if (found.Count >= 2) return;
        if (pick.Count >= 2 && Math.Abs(sum - target) <= Tolerance)
        {
            found.Add(new List<Candidate>(pick));
            return;
        }
        if (sum > target + Tolerance) return;
        for (var i = start; i < items.Count && found.Count < 2; i++)
        {
            pick.Add(items[i]);
            FindSubsets(items, i + 1, pick, sum + items[i].Outstanding, target, found);
            pick.RemoveAt(pick.Count - 1);
        }
    }

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
