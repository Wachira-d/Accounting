using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>คำตัดสินของ <see cref="BankMatchArbiter"/> — ไม่ใช่ "คะแนน" แต่เป็น "ทำอะไรต่อ"</summary>
public enum BankMatchVerdict
{
    /// <summary>ไม่มีผู้สมัครที่ดีพอ — ปล่อย `Unmatched` ไว้ ห้ามแต่งคำตอบ</summary>
    None = 0,

    /// <summary>มีผู้สมัครที่น่าสนใจ แต่ **ยังไม่พอจะประทับเอง** → `Suggested`
    /// (ต้องเก็บ id ของคู่ที่เสนอไว้เสมอ มิฉะนั้นเป็น "สถานะกับความว่าง")</summary>
    Suggest = 1,

    /// <summary>มั่นใจพอจะประทับ `Matched` ให้เอง — ต้องเก็บ id ของคู่เสมอ</summary>
    Apply = 2,
}

/// <summary>
/// **ตัวตัดสินการจับคู่รายการธนาคารตัวเดียวของทั้งระบบ** (OWNER file · pure).
///
/// ที่มา: `DECISION_AUDIT_2026-09-18.md` §3 D4-1/D4-2 · `DECISION_DOCTRINE.md` §4.2 GAP-1
///
/// สามอาการที่ตัวนี้มาปิด:
/// 1. **"ใครมาก่อนชนะ"** — ทุกเส้นเดิมใช้ `score &gt; bestScore` / `FirstOrDefault`
///    ⇒ ใบสองใบที่ยอด+วันเท่ากันเป๊ะ ผลลัพธ์ขึ้นกับลำดับแถวใน DB
///    ⇒ รันสองครั้งได้คนละคำตอบ. **เสมอกัน = ไม่ตัดสิน** (F2 ข้อ 3:
///    "ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ")
/// 2. **ประทับสถานะปลายทางเอง** (R1) — `Matched` คือสถานะปลายทางที่บอกผู้ใช้ว่า
///    "เงินก้อนนี้มีที่มาที่ไปแล้ว" ตั้งได้เฉพาะเมื่อมีคู่จริงและห่างจากคู่รองพอ
/// 3. **สองมาตรฐาน** — อันดับที่คนเห็น (`GetMatchCandidatesAsync`) กับที่เครื่อง
///    ประทับ (`AutoMatchAsync`) คนละสูตร ⇒ คนกับเครื่องเห็นโลกคนละใบ
///
/// ทิศปลอดภัยของโดเมนนี้ = **ลดชั้นลงมาเป็น "เสนอ"** ไม่ใช่ "เงียบ":
/// รายการที่ถูกเสนอยังโผล่ให้คนเห็นและกดยืนยันได้ (ความเสียหายมองเห็น+แก้ทัน)
/// ส่วนการประทับผิดคือเงินที่ถูกกระทบยอดกับใบผิดโดยไม่มีใครรู้
/// </summary>
public static class BankMatchArbiter
{
    /// <summary>คะแนนขั้นต่ำที่ยอมให้ **ประทับเอง** — 80 = "ยอดตรงเป๊ะ (60) +
    /// ห่างกันไม่เกิน 3 วัน (20)" เป็นอย่างน้อย. ต่ำกว่านี้ให้คนตัดสิน</summary>
    public const int DefaultApplyMinScore = 80;

    /// <summary>คะแนนขั้นต่ำที่ยอม **เสนอ** ขึ้นจอ — 60 = อย่างน้อยยอดตรงเป๊ะ
    /// หรือยอดใกล้เคียง+วันใกล้กัน. ต่ำกว่านี้เป็นเสียงรบกวน</summary>
    public const int DefaultSuggestMinScore = 60;

    /// <summary>ช่องว่างขั้นต่ำระหว่างที่ 1 กับที่ 2 ที่ยอมให้ประทับเอง.
    /// 10 คะแนน ≈ "ที่ 1 มีหลักฐานที่ที่ 2 ไม่มี" (เลขอ้างอิง +10 /
    /// ชื่อผู้โอนใกล้เคียง +8..17 / ชื่อผู้โอนมั่นใจ +22).
    /// ห่างน้อยกว่านี้ = แยกไม่ออกจริง ⇒ ลดชั้นเป็น "เสนอ"</summary>
    public const int DefaultApplyMinMargin = 10;

    /// <summary>ผู้สมัคร 1 ราย ที่ผ่านการให้คะแนนจาก <see cref="BankMatchScorer"/> มาแล้ว</summary>
    /// <param name="Id">id ของ Payment / JournalEntry / Document ที่จะถูกเก็บลงคอลัมน์คู่</param>
    /// <param name="ItemType">"Payment" | "JournalEntry" | "Document" — ต้องเก็บคู่กับ id เสมอ</param>
    /// <param name="Score">คะแนน 0..100 จาก <see cref="BankMatchScorer"/></param>
    /// <param name="HasIdentitySignal">มีชื่อผู้โอน/เลขอ้างอิงยืนยันตัวตนไหม</param>
    /// <param name="Reason">เหตุผลภาษาไทยที่จะโชว์บนจอ</param>
    public sealed record Option(
        Guid Id,
        string ItemType,
        int Score,
        bool HasIdentitySignal,
        string Reason = "");

    /// <summary>เกณฑ์ที่ใช้ตัดสิน — ทุกเส้นควรใช้ค่า default ตัวเดียวกัน
    /// เว้นแต่มีเหตุผลเขียนไว้ตรงจุดเรียก</summary>
    public sealed record Options(
        int ApplyMinScore = DefaultApplyMinScore,
        int SuggestMinScore = DefaultSuggestMinScore,
        int ApplyMinMargin = DefaultApplyMinMargin)
    {
        public static readonly Options Default = new();
    }

    /// <summary>ผลการตัดสิน — `Chosen` เป็น null ได้เฉพาะเมื่อ `Verdict == None`</summary>
    /// <param name="RuleCode">รหัสกฎที่ตัดสิน — ลง audit ได้ตาม กฎเหล็ก #2 M</param>
    public sealed record Decision(
        BankMatchVerdict Verdict,
        Option? Chosen,
        Option? RunnerUp,
        int Margin,
        string RuleCode,
        string Reason)
    {
        internal static Decision NoMatch(string ruleCode, string reason)
            => new(BankMatchVerdict.None, null, null, 0, ruleCode, reason);
    }

    /// <summary>
    /// ตัดสินจากรายชื่อผู้สมัครที่ให้คะแนนแล้ว. **ตัวนี้ไม่แตะฐานข้อมูล
    /// ไม่เรียก AI ไม่มี side effect** — ผู้เรียกเป็นคนเขียนสถานะตามคำตัดสิน
    ///
    /// ลำดับการตัดสิน (บนลงล่าง หยุดที่ข้อแรกที่เข้าเงื่อนไข):
    /// 1. ไม่มีผู้สมัคร → `None`
    /// 2. คะแนนที่ 1 &lt; `SuggestMinScore` → `None` (ไม่ใช่ "เสนอมั่ว")
    /// 3. คะแนนที่ 1 &lt; `ApplyMinScore` → `Suggest`
    /// 4. ที่ 1 ห่างที่ 2 &lt; `ApplyMinMargin` → `Suggest` (**เสมอกัน = ไม่ตัดสิน**)
    /// 5. มีผู้สมัครคะแนนเท่ากันที่ 1 มากกว่า 1 ราย → `Suggest` เสมอ
    ///    (ต่อให้ margin กับที่ 3 จะเยอะแค่ไหน)
    /// 6. เหลือ 1 รายชัดเจน → `Apply`
    /// </summary>
    public static Decision Decide(IReadOnlyList<Option> candidates, Options? options = null)
    {
        var opt = options ?? Options.Default;
        if (candidates == null || candidates.Count == 0)
            return Decision.NoMatch("BANK-MATCH-NONE", "ไม่มีผู้สมัครในกรอบที่กำหนด");

        // จัดอันดับแบบ deterministic: คะแนนมาก่อน, แล้วมีหลักฐานระบุตัวตนก่อน,
        // แล้วเรียงตาม Id เพื่อให้ "ลำดับแถวใน DB" ไม่มีผลต่อคำตอบ
        // (ตัวเดิมใช้ score > bestScore ⇒ ใครมาก่อนชนะ)
        var ranked = candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.HasIdentitySignal)
            .ThenBy(c => c.Id)
            .ToList();

        var best = ranked[0];
        var runnerUp = ranked.Count > 1 ? ranked[1] : null;
        var margin = runnerUp == null ? best.Score : best.Score - runnerUp.Score;

        if (best.Score < opt.SuggestMinScore)
            return Decision.NoMatch("BANK-MATCH-WEAK",
                $"คะแนนสูงสุด {best.Score} ต่ำกว่าเกณฑ์เสนอ {opt.SuggestMinScore}");

        // ── เสมอกันที่หัวตาราง = แยกไม่ออก ⇒ ห้ามประทับเด็ดขาด ──────────
        // นี่คือเคส "ยอดเท่ากัน วันเดียวกัน ไม่มีชื่อผู้โอน 2 ใบ" ที่เดิม
        // ประทับใบที่ DB คืนมาก่อน
        var tiedAtTop = ranked.Count(c => c.Score == best.Score);
        if (tiedAtTop > 1)
            return new Decision(BankMatchVerdict.Suggest, best, runnerUp, 0,
                "BANK-MATCH-TIE",
                $"มีผู้สมัครคะแนนเท่ากัน {tiedAtTop} รายการที่ {best.Score} คะแนน — " +
                "แยกไม่ออกด้วยหลักฐานที่มี (ยอด/วัน/ชื่อผู้โอน) จึงเสนอให้ตรวจ ไม่ประทับเอง");

        if (best.Score < opt.ApplyMinScore)
            return new Decision(BankMatchVerdict.Suggest, best, runnerUp, margin,
                "BANK-MATCH-LOW",
                $"คะแนน {best.Score} ต่ำกว่าเกณฑ์ประทับอัตโนมัติ {opt.ApplyMinScore}");

        if (runnerUp != null && margin < opt.ApplyMinMargin)
        {
            // ── ข้อยกเว้นเดียวของกฎระยะห่าง: หลักฐาน "คนละชั้น" ─────────────
            // คะแนนถูกตัดที่ 100 (`BankMatchScorer.MaxScore`) ⇒ ใบที่ยอด+วันตรง
            // อยู่แล้วได้ 90 การบวกชื่อผู้โอน 22 คะแนนจึงโดนตัดเหลือ +10
            // ⇒ ระยะห่างที่วัดได้เล็กกว่าน้ำหนักหลักฐานจริง. เมื่อที่ 1 มี
            // **หลักฐานระบุตัวตน** (ชื่อผู้โอนมั่นใจ / เลขอ้างอิงตรงตัวอักษร)
            // และที่ 2 **ไม่มีเลย** นี่ไม่ใช่ "คะแนนใกล้กัน" แต่เป็นหลักฐาน
            // คนละชั้น (`DECISION_DOCTRINE` §1: เรียงด้วยระยะห่างจากของจริง
            // ไม่ใช่สะสมสัญญาณอ่อน) — นี่คือเคส "ยอดเท่ากัน วันเดียวกัน 2 ใบ
            // ชื่อผู้โอนต่างกัน → ต้องเลือกตามชื่อ"
            if (best.HasIdentitySignal && !runnerUp.HasIdentitySignal)
                return new Decision(BankMatchVerdict.Apply, best, runnerUp, margin,
                    "BANK-MATCH-IDENTITY",
                    $"ที่ 1 มีหลักฐานระบุตัวตน (ชื่อผู้โอน/เลขอ้างอิง) ที่ที่ 2 ไม่มี — {best.Reason}");

            return new Decision(BankMatchVerdict.Suggest, best, runnerUp, margin,
                "BANK-MATCH-CLOSE",
                $"ที่ 1 ({best.Score}) ห่างที่ 2 ({runnerUp.Score}) เพียง {margin} คะแนน " +
                $"— น้อยกว่าระยะห่างที่ปลอดภัย {opt.ApplyMinMargin}");
        }

        return new Decision(BankMatchVerdict.Apply, best, runnerUp, margin,
            "BANK-MATCH-APPLY",
            string.IsNullOrWhiteSpace(best.Reason)
                ? $"คะแนน {best.Score} ห่างที่ 2 {margin} คะแนน"
                : $"{best.Reason} (คะแนน {best.Score}, ห่างที่ 2 {margin})");
    }

    // ══════════════════════════════════════════════════════════════════
    //  ด่านกรองคำตอบจาก AI ก่อนถึง Decide
    // ══════════════════════════════════════════════════════════════════

    /// <summary>ที่มาของข้อเสนอ — ใช้เรียงลำดับตาม `DECISION_DOCTRINE` §1 G1
    /// "ลำดับชั้นหลักฐานเรียงด้วยระยะห่างจากของจริง ไม่ใช่ confidence ที่ผู้เสนอแต่งเอง"</summary>
    public enum MatchSource
    {
        /// <summary>เซิร์ฟเวอร์คำนวณจากข้อมูลใน DB — ใกล้ของจริงที่สุด</summary>
        Server = 0,
        /// <summary>โมเดลท้องถิ่น (นักเรียน)</summary>
        LocalModel = 1,
        /// <summary>AI ภายนอก — ไกลของจริงที่สุด ตัวเลข confidence เป็นค่าที่ตัวเองแต่ง</summary>
        Ai = 2,
    }

    /// <summary>ข้อเสนอ 1 รายการจาก AI ก่อนผ่านด่าน</summary>
    public sealed record AiProposal(Guid CandidateId, string ItemType, decimal Confidence, string Reason = "");

    /// <param name="Accepted">ข้อเสนอที่อยู่ใน candidate set จริงและมั่นใจพอ</param>
    /// <param name="RejectedUnknownIds">id ที่ AI แต่งขึ้น — ไม่มีใน candidate set</param>
    /// <param name="RejectedLowConfidence">อยู่ใน set แต่ confidence ต่ำกว่าเกณฑ์</param>
    public sealed record ProposalScreening(
        IReadOnlyList<AiProposal> Accepted,
        IReadOnlyList<Guid> RejectedUnknownIds,
        IReadOnlyList<Guid> RejectedLowConfidence);

    /// <summary>ความมั่นใจขั้นต่ำของคำตอบ AI ที่จะรับมาพิจารณาต่อ
    /// (`DECISION_DOCTRINE` §2: write-gate ≥ 0.70 + ต้องอยู่ใน candidate set)</summary>
    public const decimal DefaultAiMinConfidence = 0.70m;

    /// <summary>
    /// **ด่านกันคำตอบที่แต่งขึ้น** — AI ตอบ id ที่ไม่มีอยู่จริงเป็นเรื่องปกติ
    /// (hallucination) ถ้าเอาไปใช้ตรง ๆ จะได้ "จับคู่แล้ว" กับเอกสารที่ไม่มีตัวตน.
    /// ทุกเส้นที่รับคำตอบ AI มาแตะสถานะการกระทบยอดต้องผ่านตัวนี้ก่อน
    /// (กฎเหล็ก #1 — anti-hallucination guard)
    /// </summary>
    public static ProposalScreening ScreenAiProposals(
        IEnumerable<AiProposal>? proposals,
        IReadOnlyCollection<Guid> knownCandidateIds,
        decimal minConfidence = DefaultAiMinConfidence)
    {
        var accepted = new List<AiProposal>();
        var unknown = new List<Guid>();
        var lowConf = new List<Guid>();
        if (proposals == null)
            return new ProposalScreening(accepted, unknown, lowConf);

        var known = knownCandidateIds as HashSet<Guid> ?? new HashSet<Guid>(knownCandidateIds ?? Array.Empty<Guid>());
        foreach (var p in proposals)
        {
            if (p.CandidateId == Guid.Empty || !known.Contains(p.CandidateId))
            {
                unknown.Add(p.CandidateId);
                continue;
            }
            if (p.Confidence < minConfidence)
            {
                lowConf.Add(p.CandidateId);
                continue;
            }
            accepted.Add(p);
        }
        return new ProposalScreening(accepted, unknown, lowConf);
    }

    /// <summary>
    /// เรียงข้อเสนอที่ชนกันด้วย **ที่มาก่อน แล้วค่อย confidence**
    /// (`DECISION_AUDIT` §3 D4-8: `BulkBankAiMatchService.DeduplicateMatches`
    /// เรียงด้วย `Confidence` อย่างเดียว ⇒ AI ที่แต่ง conf 0.99 ชนะเซิร์ฟเวอร์
    /// ที่คำนวณจาก DB ได้ 0.90 — ขัด G1)
    /// </summary>
    public static IOrderedEnumerable<T> RankBySourceThenConfidence<T>(
        IEnumerable<T> items, Func<T, MatchSource> source, Func<T, decimal> confidence)
        => items.OrderBy(source).ThenByDescending(confidence);
}
