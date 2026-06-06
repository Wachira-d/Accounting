namespace Accounting.Services.Implementations.Bank;

/// <summary>
/// Independent verification layer that re-validates a proposed match
/// AFTER scoring, producing a structured report of which checks pass /
/// warn / fail. Separates the QUESTION "is this match safe?" from the
/// SCORE "how confident did the scorer claim?" — so a high-confidence
/// proposal that fails an objective check (wrong direction, out of
/// window, contact mismatch) is caught here rather than slipping through
/// because confidence was inflated by a coincidental signal.
///
/// The report is rendered into the match's Reasoning field as a compact
/// badge string ("✓5 ⚠1 ✗0") so the UI surfaces it without a schema
/// change. A FAIL caps confidence regardless of upstream score.
/// </summary>
public static class BankMatchVerification
{
    public enum CheckStatus { Pass, Warn, Fail }

    public sealed record Check(string Name, CheckStatus Status, string? Detail = null)
    {
        public string Icon => Status switch
        {
            CheckStatus.Pass => "✓",
            CheckStatus.Warn => "⚠",
            _                 => "✗",
        };
    }

    public sealed record VerificationReport(
        IReadOnlyList<Check> Checks,
        int Passed,
        int Warned,
        int Failed,
        decimal VerificationScore,          // 0..1 — share of weighted checks passed
        bool HardFail);                     // any FAIL → confidence MUST cap low

    public sealed record VerifyInput(
        decimal BankAmount,
        DateTime BankDate,
        string BankDirection,               // "In" | "Out"
        string? BankMemo,
        string? BankPayee,
        string? BankReference,
        decimal CandidateSum,
        IReadOnlyList<DateTime> CandidateDates,
        IReadOnlyList<string?> CandidateDirections,
        IReadOnlyList<string?> CandidateContacts,
        bool IdentityConfirmed,             // MemoConfirmsIdentity result
        bool ChannelCompatible,             // BankFlowClassifier.IsChannelCompatible
        bool AmountHallucinated,            // realAmountById flagged
        BankFeeDictionary.DeltaKind DeltaKind,
        bool IsAggregatorFlow);

    /// <summary>Run all checks. Order matters for the report; weights are
    /// inside the score formula (a Fail anywhere pulls down).</summary>
    public static VerificationReport Verify(VerifyInput x)
    {
        var checks = new List<Check>(10);

        // ── 1. Amount alignment ─────────────────────────────────────────
        var delta = Math.Abs(x.BankAmount - x.CandidateSum);
        if (delta <= 0.01m)
            checks.Add(new("ยอดตรงพอดี", CheckStatus.Pass));
        else if (x.DeltaKind != BankFeeDictionary.DeltaKind.Unexplained
                 && x.DeltaKind != BankFeeDictionary.DeltaKind.None)
            checks.Add(new("ยอดต่างแบบอธิบายได้", CheckStatus.Warn, $"delta {delta:N2} → {x.DeltaKind}"));
        else
            checks.Add(new("ยอดไม่ตรง", CheckStatus.Fail, $"delta {delta:N2} ไม่อธิบาย"));

        // ── 2. Direction ────────────────────────────────────────────────
        // EVERY candidate must have a direction matching the bank line.
        // Mixed-direction set is OK when it represents a net-settlement
        // (RV − PV) — the SAVE-time guard handles that net; for THIS check
        // we just verify at least one candidate matches bank direction.
        bool dirOk = x.CandidateDirections.Any(d =>
            string.Equals(d, x.BankDirection, StringComparison.OrdinalIgnoreCase));
        checks.Add(dirOk
            ? new("ทิศทางตรง", CheckStatus.Pass)
            : new("ทิศทางไม่ตรง", CheckStatus.Fail,
                  $"bank={x.BankDirection}, candidates=[{string.Join(",", x.CandidateDirections)}]"));

        // ── 3. Date window — candidates dated within ±60 days of bank line
        // (each pass already enforces its category-specific window; this is
        // a SANITY ceiling to catch a broken pass).
        bool dateOk = x.CandidateDates.All(d => Math.Abs((d.Date - x.BankDate.Date).TotalDays) <= 60);
        var maxGap = x.CandidateDates.Count == 0 ? 0
            : x.CandidateDates.Max(d => (int)Math.Abs((d.Date - x.BankDate.Date).TotalDays));
        checks.Add(dateOk
            ? new("วันใน window", CheckStatus.Pass, $"max ห่าง {maxGap}d")
            : new("วันห่างมาก", CheckStatus.Fail, $"max ห่าง {maxGap}d > 60d"));

        // ── 4. Identity — was anyone in the memo confirmed?
        checks.Add(x.IdentityConfirmed
            ? new("ระบุตัวตนได้", CheckStatus.Pass)
            : new("ไม่มี identity signal", CheckStatus.Warn,
                  "ใช้แค่ยอด+วัน — เสี่ยงจับผิดถ้ามีลูกค้ายอดเดียวกันหลายราย"));

        // ── 5. Channel — payment method consistent with bank flow category
        checks.Add(x.ChannelCompatible
            ? new("ช่องทางเข้ากัน", CheckStatus.Pass)
            : new("ช่องทางขัดแย้ง", CheckStatus.Fail,
                  "เช่น KShop deposit ↔ Cheque payment"));

        // ── 6. Hallucination
        checks.Add(x.AmountHallucinated
            ? new("AI ระบุยอดผิด", CheckStatus.Fail,
                  "ต้องตรวจก่อนยืนยัน")
            : new("ยอดที่อ้างตรงกับ DB", CheckStatus.Pass));

        // ── 7. Contact consistency. Three cases:
        //   - Pure same-side M:1 (all In or all Out) with mixed customers →
        //     suspicious unless aggregator (Σ QR/Card from many payers).
        //   - Net-settlement (In + Out present) — each side MUST be the same
        //     contact (RV − PV of one customer paying NET). A net-settlement
        //     pairing two DIFFERENT customers is almost certainly wrong and
        //     should HARD-FAIL — was only a warning before.
        //   - 0 or 1 known contact → trivially pass.
        if (x.CandidateContacts.Count >= 2)
        {
            // Pair candidates with their direction; align by index.
            var pairs = new List<(string? Contact, string? Direction)>(x.CandidateContacts.Count);
            for (int i = 0; i < x.CandidateContacts.Count; i++)
                pairs.Add((x.CandidateContacts[i],
                    i < x.CandidateDirections.Count ? x.CandidateDirections[i] : null));
            var inContacts = pairs.Where(p => string.Equals(p.Direction, "In", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Contact?.Trim().ToLowerInvariant())
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct().ToList();
            var outContacts = pairs.Where(p => string.Equals(p.Direction, "Out", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Contact?.Trim().ToLowerInvariant())
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct().ToList();
            bool netSettle = inContacts.Count > 0 && outContacts.Count > 0;
            if (netSettle && (inContacts.Count > 1 || outContacts.Count > 1))
            {
                // Net-settlement spanning multiple customers on a side =
                // basically guaranteed wrong (you can't net Customer A's
                // RV against Customer B's PV). HARD FAIL.
                checks.Add(new("Net-settlement ลูกค้าหลายราย", CheckStatus.Fail,
                    $"ฝั่งเข้า {inContacts.Count} ราย, ฝั่งออก {outContacts.Count} ราย — RV-PV ต้องเป็นลูกค้าเดียวกัน"));
            }
            else
            {
                var distinct = x.CandidateContacts
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c!.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToList();
                if (distinct.Count <= 1)
                    checks.Add(new("ลูกค้าคนเดียวกัน", CheckStatus.Pass));
                else if (x.IsAggregatorFlow)
                    checks.Add(new("ลูกค้าหลายราย (Aggregator)", CheckStatus.Pass,
                        $"{distinct.Count} ราย — ปกติสำหรับยอดรวม QR/Card"));
                else
                    checks.Add(new("ลูกค้าหลายราย", CheckStatus.Warn,
                        $"{distinct.Count} ราย ใน 1 bank line ที่ไม่ใช่ aggregator"));
            }
        }
        else
        {
            checks.Add(new("1 ลูกค้า", CheckStatus.Pass));
        }

        // ── 8. Date dispersion across candidates (only meaningful for M:1)
        if (x.CandidateDates.Count >= 2)
        {
            var spread = (x.CandidateDates.Max() - x.CandidateDates.Min()).TotalDays;
            if (spread <= 7) checks.Add(new("วันที่กระจาย ≤7d", CheckStatus.Pass));
            else if (x.IsAggregatorFlow || spread <= 35)
                checks.Add(new($"วันที่กระจาย {spread:N0}d", CheckStatus.Warn,
                    "OTA/aggregator สะสมหลายวัน — ตรวจให้แน่ใจ"));
            else
                checks.Add(new($"วันที่กระจายมาก {spread:N0}d", CheckStatus.Fail,
                    "เกินกรอบรวมเอกสารที่สมเหตุสมผล"));
        }

        // ── Score
        int passed = checks.Count(c => c.Status == CheckStatus.Pass);
        int warned = checks.Count(c => c.Status == CheckStatus.Warn);
        int failed = checks.Count(c => c.Status == CheckStatus.Fail);
        decimal score = checks.Count == 0 ? 1m
            : (passed + warned * 0.5m) / checks.Count;
        return new VerificationReport(checks, passed, warned, failed,
            Math.Round(score, 3), HardFail: failed > 0);
    }

    /// <summary>Render the report as a compact one-line summary suitable
    /// for appending to a match's Reasoning field.</summary>
    public static string RenderSummary(VerificationReport r)
    {
        var failList = r.Checks
            .Where(c => c.Status == CheckStatus.Fail)
            .Select(c => "✗" + c.Name)
            .ToList();
        var warnList = r.Checks
            .Where(c => c.Status == CheckStatus.Warn)
            .Select(c => "⚠" + c.Name)
            .ToList();
        var prefix = $"ตรวจ {r.Passed}✓ {r.Warned}⚠ {r.Failed}✗";
        if (failList.Count == 0 && warnList.Count == 0) return prefix;
        var details = failList.Concat(warnList);
        return prefix + " · " + string.Join(", ", details);
    }
}
