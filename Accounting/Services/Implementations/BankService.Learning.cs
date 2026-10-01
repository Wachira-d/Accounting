using System.Globalization;
using System.Text.RegularExpressions;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class BankService
{
    // ============================================================
    // AI reconciliation pattern memory ("data mining")
    // ------------------------------------------------------------
    // Every confirmed reconciliation contributes one row per
    // (bankTxn, item) pair to BankReconciliationPatterns. The pattern
    // captures:
    //   • a tokenised signature of the bank txn's description/payee/ref
    //   • a coarse amount bucket so similar-magnitude txns share a row
    //   • the target item type and (if available) the counterparty ContactId
    //
    // GetLearnedSuggestionsAsync uses the same lookup key on an
    // unmatched bank txn and returns candidate items ordered by pattern
    // confidence (TimesConfirmed × recency-decay × signature-overlap).
    //
    // Repeated identical patterns increment TimesConfirmed in place so
    // the table stays compact; min/max/avg amount stats update with each
    // hit so the suggester can detect outliers.
    // ============================================================

    private static readonly Regex _tokenRegex = new(@"[a-z0-9ก-๙]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Lower-case alphanumeric tokens, length ≥ 3, joined by "|". Strips
    /// common noise words ("transfer", "kbank", "pay", date fragments) so
    /// the signature focuses on the parts likely to recur — counterparty
    /// names, account-tail digits, reference codes.
    /// </summary>
    public static string ComputeDescriptionSignature(string? description, string? reference, string? payee)
    {
        var combined = string.Join(" ", new[] { description, reference, payee }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(combined)) return "(empty)";

        var matches = _tokenRegex.Matches(combined.ToLowerInvariant());
        var tokens = matches
            .Select(m => m.Value)
            .Where(t => t.Length >= 3)
            .Where(t => !_stopWords.Contains(t))
            .Distinct()
            .Take(8)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        return tokens.Count == 0 ? "(empty)" : string.Join("|", tokens);
    }

    private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "transfer", "deposit", "withdraw", "withdrawal", "credit", "debit",
        "atm", "kbank", "scb", "bbl", "ktb", "bay", "ttb", "tmb", "uob", "cimb",
        "bank", "channel", "code", "fee", "interest",
        "pay", "payment", "payee", "ref",
        "เงิน", "โอน", "ฝาก", "ถอน", "ดอกเบี้ย", "ค่าธรรมเนียม",
    };

    /// <summary>Magnitude bucket — same buckets used both when recording and
    /// when looking up so a 950-baht receipt matches the 1000-baht pattern.</summary>
    public static string ComputeAmountBucket(decimal amount)
    {
        var a = Math.Abs(amount);
        return a switch
        {
            < 100m => "<100",
            < 1_000m => "100-1k",
            < 10_000m => "1k-10k",
            < 100_000m => "10k-100k",
            < 1_000_000m => "100k-1m",
            _ => "1m+",
        };
    }

    /// <summary>
    /// บันทึกแพตเทิร์น 1 แถวต่อคู่ (bankTxn, matchItem) ของกลุ่มที่ยืนยันแล้ว.
    ///
    /// ⚠ **เดิมห่อทั้งเมธอดด้วย `try/catch` + `LogWarning`** (`Learning.cs:145` ·
    /// `DECISION_AUDIT_2026-09-18.md` §3 D4-8) ⇒ ถ้าการบันทึกล้ม คลังเรียนรู้
    /// **หยุดโตอย่างเงียบสนิท** ไม่มีใครรู้ตลอดกาล — ตรงกับ F2 ข้อ 7
    /// ("`LogWarning` ไม่ใช่การดัง"). ตอนนี้ล้มแล้ว **โยนต่อ** และผู้เรียกเรียก
    /// ตัวนี้ **ก่อน commit** ⇒ ผู้ใช้เห็น error แล้วกดใหม่ได้ (ความเสียหาย
    /// มองเห็นและแก้ทัน — G5) ดีกว่าคลังที่ไม่โตโดยไม่มีอะไรฟ้อง
    /// </summary>
    /// <param name="sourceByItemId">แหล่งของคำยืนยันต่อรายการฝั่งเอกสาร (รอบ 201 ทีม AI · A-AI1) — รายการที่ไม่อยู่ใน
    /// แผนที่/ค่า null = <see cref="UserChoiceSource.Implicit"/> (ค่าปลอดภัย: นับเป็น "เคยเห็น" แต่ไม่ดันความมั่นใจ)</param>
    public async Task RecordReconciliationPatternsAsync(Guid companyId, Guid groupId,
        IReadOnlyDictionary<Guid, UserChoiceSource>? sourceByItemId = null)
    {
        var group = await _db.ReconciliationGroups.AsNoTracking()
            .Include(g => g.Items)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId);
        if (group == null) return;

        var bankItems = group.Items.Where(i => i.ItemType == ReconciliationItemType.BankTransaction).ToList();
        var matchItems = group.Items.Where(i => i.ItemType != ReconciliationItemType.BankTransaction).ToList();
        if (bankItems.Count == 0 || matchItems.Count == 0) return;

        // Pull bank txn descriptors in one shot.
        var bankIds = bankItems.Select(b => b.ItemId).ToList();
        var bankTxns = await _db.Set<BankTransaction>().AsNoTracking()
            .Where(t => bankIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Description, t.Reference, t.Payee })
            .ToListAsync();

        // Map items' contacts when possible (best-effort).
        var paymentContactMap = await _db.Set<Payment>().AsNoTracking()
            .Where(p => matchItems
                .Where(i => i.ItemType == ReconciliationItemType.Payment)
                .Select(i => i.ItemId).Contains(p.Id))
            .Select(p => new { p.Id, ContactId = (Guid?)p.Document.ContactId })
            .ToDictionaryAsync(x => x.Id, x => x.ContactId);
        var docContactMap = await _db.Documents.AsNoTracking()
            .Where(d => matchItems
                .Where(i => i.ItemType == ReconciliationItemType.Document)
                .Select(i => i.ItemId).Contains(d.Id))
            .Select(d => new { d.Id, ContactId = (Guid?)d.ContactId })
            .ToDictionaryAsync(x => x.Id, x => x.ContactId);

        foreach (var b in bankItems)
        {
            var info = bankTxns.FirstOrDefault(x => x.Id == b.ItemId);
            if (info == null) continue;
            var sig = ComputeDescriptionSignature(info.Description, info.Reference, info.Payee);
            var bucket = ComputeAmountBucket(b.AllocatedAmount);

            foreach (var item in matchItems)
            {
                Guid? contactId = null;
                if (item.ItemType == ReconciliationItemType.Payment)
                    paymentContactMap.TryGetValue(item.ItemId, out contactId);
                else if (item.ItemType == ReconciliationItemType.Document)
                    docContactMap.TryGetValue(item.ItemId, out contactId);

                var itemSource = sourceByItemId != null && sourceByItemId.TryGetValue(item.ItemId, out var src)
                    ? src : UserChoiceSource.Implicit;
                await UpsertPatternAsync(companyId, group.BankAccountId, sig, bucket,
                    item.ItemType, contactId, Math.Abs(item.AllocatedAmount), itemSource);
            }
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// **ปิดฝั่ง "ยืนยัน" ของวงจรเรียนรู้** (กฎเหล็ก #1 ขั้น CAPTURE).
    ///
    /// รอบที่แล้วต่อฝั่ง **ถอน** แล้ว (`UnmatchTransactionAsync` →
    /// `BankMatchExclusion` = ตัวอย่างลบ) แต่ฝั่ง **ยืนยัน** ยังเปิดอยู่:
    /// การจับคู่ 1:1 (`ReconcileAsync`) · batch (`BatchReconcileAsync`) ·
    /// อัตโนมัติ (`AutoMatchAsync`) **ไม่บันทึกแพตเทิร์นเลย** ⇒ ระบบไม่เคย
    /// ฉลาดขึ้นจากการจับคู่ที่คนยืนยัน มีแต่กลุ่ม M:N เท่านั้นที่สอน
    /// (`DECISION_AUDIT_2026-09-18.md` §3 D4-8)
    ///
    /// ตัวนี้ **ไม่เรียก `SaveChanges` เอง** — ผู้เรียกบันทึกพร้อมกับการจับคู่
    /// ในทรานแซกชันเดียวกัน เพื่อให้ "จับคู่สำเร็จ" กับ "สอนแล้ว" เป็นจริงพร้อมกัน
    /// เสมอ (ไม่มีสถานะครึ่ง ๆ ที่ไม่มีใครเห็น)
    /// </summary>
    /// <param name="txn">บรรทัดธนาคารที่เพิ่งถูกยืนยัน</param>
    /// <param name="items">คู่ที่ถูกยืนยัน — (ชนิด, id, ยอดในสกุลบัญชีธนาคาร)</param>
    /// <param name="source">ผู้ใช้เลือกคู่เอง (Explicit) หรือระบบเสนอแล้วปล่อยผ่าน (Implicit/BulkApprove) —
    /// รอบ 201 ทีม AI · A-AI1: ความมั่นใจของคลังนับเฉพาะ Explicit (<c>Helpers/BankPatternEvidence</c>)</param>
    public async Task CaptureConfirmedMatchAsync(
        Guid companyId, BankTransaction txn,
        IReadOnlyList<(ReconciliationItemType Type, Guid Id, decimal Amount)> items,
        UserChoiceSource source = UserChoiceSource.Implicit)
    {
        if (txn == null || items == null || items.Count == 0) return;

        var sig = ComputeDescriptionSignature(txn.Description, txn.Reference, txn.Payee);
        var bucket = ComputeAmountBucket(txn.Amount);

        // ContactId ของคู่ — ทำให้แพตเทิร์นจำได้ว่า "ข้อความแบบนี้ = คู่ค้ารายนี้"
        var paymentIds = items.Where(i => i.Type == ReconciliationItemType.Payment)
            .Select(i => i.Id).ToList();
        var docIds = items.Where(i => i.Type == ReconciliationItemType.Document)
            .Select(i => i.Id).ToList();
        var paymentContacts = paymentIds.Count == 0
            ? new Dictionary<Guid, Guid?>()
            : await _db.Payments.AsNoTracking()
                .Where(p => paymentIds.Contains(p.Id) && p.CompanyId == companyId)
                .Select(p => new { p.Id, ContactId = (Guid?)p.Document.ContactId })
                .ToDictionaryAsync(x => x.Id, x => x.ContactId);
        var docContacts = docIds.Count == 0
            ? new Dictionary<Guid, Guid?>()
            : await _db.Documents.AsNoTracking()
                .Where(d => docIds.Contains(d.Id) && d.CompanyId == companyId)
                .Select(d => new { d.Id, ContactId = (Guid?)d.ContactId })
                .ToDictionaryAsync(x => x.Id, x => x.ContactId);

        foreach (var (type, id, amount) in items)
        {
            Guid? contactId = null;
            if (type == ReconciliationItemType.Payment) paymentContacts.TryGetValue(id, out contactId);
            else if (type == ReconciliationItemType.Document) docContacts.TryGetValue(id, out contactId);

            await UpsertPatternAsync(companyId, txn.BankAccountId, sig, bucket,
                type, contactId, Math.Abs(amount), source);
        }

        // ผู้ใช้ยืนยันคู่นี้แล้ว → ถ้าเคยมี "ตัวอย่างลบ" ของคู่เดียวกันค้างอยู่
        // ต้องถอนออก มิฉะนั้นคลังเก็บสองคำตอบที่ขัดกันของเหตุการณ์เดียวกัน
        // (§3 กันคลังเอียง — "เก็บสองทิศ" ไม่ได้แปลว่าเก็บทิศที่ถูกกลับแล้วด้วย)
        var confirmedIds = items.Select(i => i.Id).ToList();
        var staleExclusions = await _db.Set<BankMatchExclusion>()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted
                && x.BankTransactionId == txn.Id
                && confirmedIds.Contains(x.CandidateId))
            .ToListAsync();
        foreach (var ex in staleExclusions)
        {
            ex.IsDeleted = true;
            ex.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task UpsertPatternAsync(Guid companyId, Guid bankAccountId, string sig, string bucket,
        ReconciliationItemType targetType, Guid? contactId, decimal amount, UserChoiceSource source)
    {
        // ทุกคำยืนยันนับเป็น "เคยเห็น" (TimesConfirmed) · เฉพาะที่ผู้ใช้เลือกเองนับเป็นหลักฐาน (ExplicitConfirmCount)
        var explicitInc = Accounting.Helpers.BankPatternEvidence.ExplicitIncrement(source);
        var existing = await _db.BankReconciliationPatterns
            .FirstOrDefaultAsync(p => p.CompanyId == companyId
                && p.BankAccountId == bankAccountId
                && p.DescriptionSignature == sig
                && p.AmountBucket == bucket
                && p.TargetType == targetType
                && p.ContactId == contactId);
        if (existing != null)
        {
            existing.TimesConfirmed += 1;
            existing.ExplicitConfirmCount += explicitInc;
            existing.LastUsedAt = DateTime.UtcNow;
            existing.AvgAmount = ((existing.AvgAmount * (existing.TimesConfirmed - 1)) + amount) / existing.TimesConfirmed;
            if (amount < existing.MinAmount || existing.MinAmount == 0) existing.MinAmount = amount;
            if (amount > existing.MaxAmount) existing.MaxAmount = amount;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.BankReconciliationPatterns.Add(new BankReconciliationPattern
            {
                CompanyId = companyId,
                BankAccountId = bankAccountId,
                DescriptionSignature = sig,
                AmountBucket = bucket,
                TargetType = targetType,
                ContactId = contactId,
                TimesConfirmed = 1,
                ExplicitConfirmCount = explicitInc,
                LastUsedAt = DateTime.UtcNow,
                AvgAmount = amount,
                MinAmount = amount,
                MaxAmount = amount,
            });
        }
    }

    /// <summary>
    /// เสนอรายการที่จะจับคู่กับบรรทัดธนาคาร 1 บรรทัด จากคลังแพตเทิร์นที่เรียนไว้ (ปุ่ม "✨ AI จับคู่จากประวัติ")
    ///
    /// ═══ รอบ 201 ทีม AI ═══
    /// • <b>A-AI3 (H-5) — มาตรฐานเดียว</b>: เดิมเส้นนี้มีสูตรคะแนนเป็นของตัวเอง (0..1 จากความเกี่ยวข้องของแพตเทิร์น
    ///   · หน้าเว็บติ๊กให้เมื่อ ≥ 0.4) = "สูตรที่ 3" ที่ <c>BankMatchArbiter</c> ประกาศว่าปิดแล้ว ⇒ คนเห็นอันดับหนึ่งแบบ
    ///   เครื่องประทับอีกแบบ · ตอนนี้แพตเทิร์นทำหน้าที่ "หาว่าจะเสนอรายการไหน" อย่างเดียว · คะแนนของทุกรายการมาจาก
    ///   <see cref="Accounting.Helpers.BankMatchScorer.Score"/> (สเกล 0..100 ตัวเดียวกับหน้าจับคู่/จับคู่อัตโนมัติ) และ
    ///   การติ๊กให้อัตโนมัติ (<c>AutoSelect</c>) ตัดสินด้วย <see cref="Accounting.Helpers.BankMatchArbiter.Decide"/> บน
    ///   <b>รายการค้างทั้งหมด</b> (ไม่ใช่เฉพาะรายการของแพตเทิร์น — มิฉะนั้นคู่แข่งที่ดีพอกันจะหายจากการเทียบ) ·
    ///   เซิร์ฟเวอร์ตัดสิน หน้าเว็บแค่อ่านธง (หลักการข้อ 5)
    /// • <b>A-AI1 (H-1) — ห้ามสอนตัวเอง</b>: ความเกี่ยวข้องของแพตเทิร์นนับเฉพาะคำยืนยันที่ผู้ใช้เลือกคู่เอง
    ///   (<see cref="Accounting.Helpers.BankPatternEvidence.Relevance"/>) · รายการที่ติ๊กจากปุ่มนี้ถูกส่งกลับเป็น
    ///   Implicit ตอนยืนยันกลุ่ม ⇒ ไม่ดันหลักฐานของแพตเทิร์นที่เสนอเอง
    /// </summary>
    public async Task<LearnedSuggestionsResponse> GetLearnedSuggestionsAsync(Guid companyId, Guid bankTransactionId)
    {
        var txn = await _db.Set<BankTransaction>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == bankTransactionId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการธนาคาร");

        var sig = ComputeDescriptionSignature(txn.Description, txn.Reference, txn.Payee);
        var bucket = ComputeAmountBucket(txn.Amount);
        var sigTokens = sig.Split('|').Where(t => t.Length > 0).ToHashSet();

        // Pull patterns that overlap on signature tokens OR amount bucket —
        // ranking step below handles the actual scoring.
        var rawPatterns = await _db.BankReconciliationPatterns.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.BankAccountId == txn.BankAccountId
                && (p.DescriptionSignature == sig || p.AmountBucket == bucket))
            .ToListAsync();

        // ความเกี่ยวข้องของแพตเทิร์น (ใช้ "หาว่าจะเสนอรายการไหน" เท่านั้น — ไม่ใช่คะแนนจับคู่)
        var now = DateTime.UtcNow;
        var relevant = rawPatterns.Select(p =>
        {
            var theirTokens = p.DescriptionSignature.Split('|').Where(t => t.Length > 0).ToHashSet();
            var overlap = sigTokens.Intersect(theirTokens).Count();
            var unionSize = Math.Max(1, sigTokens.Union(theirTokens).Count());
            var relevance = Accounting.Helpers.BankPatternEvidence.Relevance(
                overlap / (double)unionSize, p.AmountBucket == bucket,
                p.ExplicitConfirmCount, (now - p.LastUsedAt).TotalDays);
            return (Pattern: p, Relevance: relevance);
        })
        .Where(x => x.Relevance > Accounting.Helpers.BankPatternEvidence.MinRelevance)
        .OrderByDescending(x => x.Relevance)
        .Take(20)
        .ToList();

        if (relevant.Count == 0)
            return new LearnedSuggestionsResponse(new List<LearnedSuggestion>(), sig, bucket, rawPatterns.Count);

        // รายการค้างทั้งบัญชี — โหลดครั้งเดียว (เดิมโหลดซ้ำทุกแพตเทิร์น สูงสุด 20 รอบ)
        var pool = await GetUnmatchedItemsAsync(companyId, txn.BankAccountId, search: null, fromDate: null, toDate: null);

        // ── คะแนนสเกลเดียวกับทุกเส้น (BankMatchScorer) + คำตัดสินของ arbiter บนรายการค้างทั้งหมด ──
        var bankAmount = Math.Abs(txn.Amount);
        Accounting.Helpers.BankMatchScorer.Result ScoreItem(UnmatchedItem it)
            => Accounting.Helpers.BankMatchScorer.Score(new Accounting.Helpers.BankMatchScorer.Input(
                CandidateAmount: Math.Abs(it.BankLegAmount ?? it.Amount),
                BankAmount: bankAmount,
                CandidateDate: it.Date,
                BankDate: txn.TransactionDate,
                CandidateRef: null,
                CandidateNotes: it.Description,
                CandidateDocNumber: it.Number,
                CandidateName: it.ContactName,
                BankDescription: txn.Description,
                BankReference: txn.Reference,
                BankPayee: txn.Payee));
        var allItems = pool.Payments.Concat(pool.JournalEntries).Concat(pool.Documents).ToList();
        var scoreById = new Dictionary<(string, Guid), Accounting.Helpers.BankMatchScorer.Result>();
        var options = new List<Accounting.Helpers.BankMatchArbiter.Option>(allItems.Count);
        foreach (var it in allItems)
        {
            var r = ScoreItem(it);
            scoreById[(it.ItemType, it.Id)] = r;
            options.Add(new Accounting.Helpers.BankMatchArbiter.Option(it.Id, it.ItemType, r.Score, r.HasIdentitySignal, r.Reason));
        }
        var decision = Accounting.Helpers.BankMatchArbiter.Decide(options);

        // Materialise candidate items based on the suggested (targetType, contactId)
        // tuples — for each top-pattern, surface the actually-unmatched items.
        var suggestions = new List<LearnedSuggestion>();
        var seenItems = new HashSet<(string, Guid)>();
        foreach (var (pat, relevance) in relevant)
        {
            foreach (var it in ResolveUnmatchedItemsForPattern(pool, pat, bucket))
            {
                var key = (it.ItemType, it.Id);
                if (!seenItems.Add(key)) continue;
                var sc = scoreById.TryGetValue(key, out var found) ? found : ScoreItem(it);
                var isChosen = decision.Chosen != null && decision.Chosen.Id == it.Id
                    && string.Equals(decision.Chosen.ItemType, it.ItemType, StringComparison.Ordinal);
                var autoSelect = isChosen && decision.Verdict == Accounting.Helpers.BankMatchVerdict.Apply;
                var patternWhy = Accounting.Helpers.BankPatternEvidence.Reason(
                    pat.ExplicitConfirmCount, pat.TimesConfirmed, pat.LastUsedAt);
                suggestions.Add(new LearnedSuggestion(
                    it.ItemType, it.Id, it.Number, it.Date, it.Description,
                    it.Amount, it.ContactName,
                    Confidence: sc.Score / 100.0,
                    Reason: string.IsNullOrEmpty(sc.Reason) ? patternWhy : $"{sc.Reason} · {patternWhy}",
                    Score: sc.Score,
                    Verdict: isChosen ? decision.Verdict.ToString() : Accounting.Helpers.BankMatchVerdict.None.ToString(),
                    AutoSelect: autoSelect,
                    ExplicitConfirmations: pat.ExplicitConfirmCount,
                    PatternRelevance: Math.Round(relevance, 3, MidpointRounding.AwayFromZero)));
                if (suggestions.Count >= 10) break;
            }
            if (suggestions.Count >= 10) break;
        }

        // เรียงด้วยคะแนนสเกลเดียวกับหน้าจับคู่ก่อน แล้วค่อยความเกี่ยวข้องของแพตเทิร์น
        suggestions = suggestions
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.PatternRelevance)
            .ToList();
        return new LearnedSuggestionsResponse(suggestions, sig, bucket, rawPatterns.Count);
    }

    private static List<UnmatchedItem> ResolveUnmatchedItemsForPattern(
        UnmatchedItemsResponse pool, BankReconciliationPattern pattern, string bucket)
    {
        // Reuse the master unmatched-items pool then filter to the pattern's
        // target type + contact.
        var typed = pattern.TargetType switch
        {
            ReconciliationItemType.Payment => pool.Payments,
            ReconciliationItemType.JournalEntry => pool.JournalEntries,
            ReconciliationItemType.Document => pool.Documents,
            _ => new List<UnmatchedItem>(),
        };

        // When the pattern has a ContactId, prefer items from the same contact.
        // Without ContactId, prefer items in the same amount bucket.
        return typed
            .Where(it => ComputeAmountBucket(it.Amount) == bucket
                || (pattern.MaxAmount > 0 && it.Amount <= pattern.MaxAmount * 1.2m && it.Amount >= pattern.MinAmount * 0.8m))
            .OrderBy(it => Math.Abs(it.Amount - pattern.AvgAmount))   // closest-to-mean first
            .Take(5)
            .ToList();
    }
}
