using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวให้คะแนนการจับคู่รายการธนาคารตัวเดียวของทั้งระบบ** (OWNER file).
///
/// ก่อนรอบนี้สูตรให้คะแนนมี **5 สำเนา** ที่ให้อันดับต่างกัน
/// (`DECISION_AUDIT_2026-09-18.md` §3 D4-1 · `DECISION_DOCTRINE.md` §4.2 GAP-1):
///
/// | ที่ | สูตรเดิม | อาการ |
/// | --- | --- | --- |
/// | `BankService.AutoMatchAsync` | 50/30/20 · กรองเฉพาะ `PaymentMethod==BankTransfer` | PromptPay/เช็คหลุด · **ไม่ดูชื่อผู้โอนเลย** แล้วประทับ `Matched` |
/// | `BankService.GetMatchCandidatesAsync` | 60/30/10/22 + ชื่อผู้โอน | รายการที่ **มนุษย์เห็น** |
/// | `BankService.FindBestPaymentMatch` (AiSmartMatch) | 0.40/0.25/0.20/0.10 | คนละสเกล |
/// | `BankFeedService.TryAutoMatchAsync` | ยอดตรงเป๊ะอย่างเดียว | — |
/// | `OpenBankingService` | ยอด + ≤3 วัน · `FirstOrDefault` | ใครมาก่อนชนะ |
///
/// ⇒ อันดับที่ "คนเห็นบนจอ" กับที่ "เครื่องประทับให้" ไม่ใช่อันเดียวกัน.
/// สูตรที่เหลือไว้คือสูตรของรายการที่มนุษย์เห็น (60/30/10/22) เพราะเป็น
/// สูตรเดียวที่ **ดูชื่อผู้โอน** ซึ่งเป็นสัญญาณเดียวที่แยกใบที่ "ยอดเท่ากัน +
/// วันเดียวกัน" ออกจากกันได้ (แบงก์ไทยส่งชื่อผู้โอนมาเกือบทุกบรรทัด
/// แต่แทบไม่เคยส่งเลขเอกสาร)
///
/// ตัวนี้ **ให้คะแนนอย่างเดียว ไม่ตัดสิน** — การตัดสินว่า "ประทับ / เสนอ /
/// ไม่ตอบ" อยู่ที่ <see cref="BankMatchArbiter"/> ตัวเดียวเท่านั้น
/// (แยก "คะแนน" ออกจาก "คำตัดสิน" ตาม `DECISION_DOCTRINE` §1)
/// </summary>
public static class BankMatchScorer
{
    /// <summary>คะแนนเต็ม — ตัดที่ 100 เสมอ</summary>
    public const int MaxScore = 100;

    /// <summary>คะแนนจากยอดเงินเมื่อ "ตรงเป๊ะ" — ใช้เป็นฐานของเกณฑ์ที่ arbiter ใช้</summary>
    public const int ExactAmountScore = 60;

    /// <summary>คะแนนจากวันที่เมื่อ "วันเดียวกัน"</summary>
    public const int SameDayScore = 30;

    /// <summary>คะแนนเพิ่มเมื่อชื่อผู้โอนตรงแบบมั่นใจ</summary>
    public const int ConfidentNameBonus = 22;

    /// <summary>อินพุตของการให้คะแนน 1 คู่ — ฝั่งกระดาษธนาคาร + ฝั่งผู้สมัคร</summary>
    /// <param name="CandidateAmount">ยอดของ Payment/JE ที่กำลังพิจารณา</param>
    /// <param name="BankAmount">ยอดบนบรรทัด statement (ค่าสัมบูรณ์)</param>
    /// <param name="CandidateDate">วันที่ของ Payment/JE (ตัดเวลาออกแล้วก็ได้)</param>
    /// <param name="BankDate">วันที่บนบรรทัด statement</param>
    /// <param name="CandidateRef">เลขอ้างอิงของผู้สมัคร</param>
    /// <param name="CandidateNotes">หมายเหตุของผู้สมัคร</param>
    /// <param name="CandidateDocNumber">เลขที่เอกสารต้นทาง</param>
    /// <param name="CandidateName">ชื่อคู่ค้าของผู้สมัคร (จากเอกสารต้นทาง)</param>
    /// <param name="BankDescription">ช่อง Description ของ statement</param>
    /// <param name="BankReference">ช่อง Reference ของ statement</param>
    /// <param name="BankPayee">ช่อง Payee ของ statement — บางแบงก์ใส่ชื่อผู้โอนไว้ที่นี่แทน</param>
    public sealed record Input(
        decimal CandidateAmount,
        decimal BankAmount,
        DateTime CandidateDate,
        DateTime BankDate,
        string? CandidateRef = null,
        string? CandidateNotes = null,
        string? CandidateDocNumber = null,
        string? CandidateName = null,
        string? BankDescription = null,
        string? BankReference = null,
        string? BankPayee = null);

    /// <summary>ผลการให้คะแนน — คะแนน 0..100 + เหตุผลภาษาไทยที่แสดงบนจอได้
    /// + ธงว่ามี "สัญญาณระบุตัวตน" (ชื่อผู้โอน/เลขอ้างอิง) หรือไม่
    /// (arbiter ใช้ธงนี้ตัดสินเคส "ยอดเท่ากันหลายใบ")</summary>
    public sealed record Result(int Score, string Reason, bool NameConfident, bool ReferenceCited)
    {
        /// <summary>มีหลักฐานระบุตัวตนอย่างน้อย 1 อย่าง (ชื่อผู้โอน หรือ เลขอ้างอิง)</summary>
        public bool HasIdentitySignal => NameConfident || ReferenceCited;
    }

    /// <summary>
    /// ให้คะแนน 1 คู่ — **ย้ายมาจาก `BankService.MatchCandidates.ScoreCandidate`
    /// แบบคงพฤติกรรมเดิมทุกกิ่ง** (คะแนน/ข้อความเหตุผลเท่าเดิม) เพิ่มเฉพาะ
    /// ธงระบุตัวตนที่เดิมคำนวณแล้วทิ้งไปในสตริงเหตุผล
    ///
    /// คะแนน (ตัดที่ 100):
    ///  - ยอดเงิน:    ตรงเป๊ะ = 60, ±0.5% = 50, ±1% = 40, ±2% = 25, อื่น = 0
    ///  - วันที่:     วันเดียวกัน = 30, ±1d = 25, ±3d = 20, ±7d = 15, ±14d = 8, ±30d = 3
    ///  - เลขอ้างอิง: ตรงตัวอักษร = +10
    ///  - ชื่อผู้โอน: มั่นใจ = +22, ใกล้เคียง = +8..17 ตามระดับความมั่นใจ
    /// </summary>
    public static Result Score(Input x)
    {
        int score = 0;
        var reasons = new List<string>();

        var bankDescLower = (x.BankDescription ?? "").ToLowerInvariant();
        var bankRefLower = (x.BankReference ?? "").ToLowerInvariant();
        var bankPayee = x.BankPayee ?? "";

        // === Amount (max 60) ===
        var amountDiff = Math.Abs(x.CandidateAmount - x.BankAmount);
        if (x.BankAmount > 0)
        {
            var pctDiff = amountDiff / x.BankAmount;
            if (amountDiff < 0.005m) { score += ExactAmountScore; reasons.Add("ยอดตรงเป๊ะ"); }
            else if (pctDiff <= 0.005m) { score += 50; reasons.Add("ยอดต่างน้อยมาก"); }
            else if (pctDiff <= 0.01m) { score += 40; reasons.Add("ยอดต่าง ≤1%"); }
            else if (pctDiff <= 0.02m) { score += 25; reasons.Add("ยอดต่าง ≤2%"); }
        }

        // === Date proximity (max 30) ===
        var dateDiff = Math.Abs((x.CandidateDate.Date - x.BankDate.Date).TotalDays);
        if (dateDiff == 0) { score += SameDayScore; reasons.Add("วันเดียวกัน"); }
        else if (dateDiff <= 1) { score += 25; reasons.Add("ห่างกัน 1 วัน"); }
        else if (dateDiff <= 3) { score += 20; reasons.Add("ห่างกัน ≤3 วัน"); }
        else if (dateDiff <= 7) { score += 15; reasons.Add("ห่างกัน ≤7 วัน"); }
        else if (dateDiff <= 14) { score += 8; }
        else if (dateDiff <= 30) { score += 3; }

        // === เลขอ้างอิง / เลขเอกสาร (max 10) ===
        bool refMatch = false;
        var allCandidateText = ($"{x.CandidateRef} {x.CandidateNotes} {x.CandidateDocNumber} {x.CandidateName}").ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(x.CandidateRef) && !string.IsNullOrWhiteSpace(bankRefLower)
            && allCandidateText.Contains(bankRefLower)) refMatch = true;

        if (!string.IsNullOrWhiteSpace(x.CandidateDocNumber)
            && bankDescLower.Contains(x.CandidateDocNumber.ToLowerInvariant())) refMatch = true;

        if (refMatch) { score += 10; reasons.Add("เลขอ้างอิงตรงกัน"); }

        // === ชื่อผู้โอน (max 22) ===
        bool nameConfident = false;
        if (!string.IsNullOrWhiteSpace(x.CandidateName))
        {
            var nm = Accounting.Services.Implementations.Matching.CounterpartyNameMatcher.Match(
                $"{bankDescLower} {bankPayee}", x.CandidateName);
            if (nm.Score >= Accounting.Services.Implementations.Matching.CounterpartyNameMatcher.ConfidentThreshold)
            {
                score += ConfidentNameBonus;
                nameConfident = true;
                reasons.Add("ชื่อผู้โอนตรงกัน" + (nm.CrossScript ? " (ข้ามภาษา)" : ""));
            }
            else if (nm.Score >= Accounting.Services.Implementations.Matching.CounterpartyNameMatcher.MinimumUsefulThreshold)
            {
                // 0.45–0.79 → 8–17 คะแนน ไล่ตามความมั่นใจ ไม่กระโดด
                var partial = (int)Math.Round(8 + 9 * (nm.Score - 0.45) / 0.35);
                score += Math.Clamp(partial, 8, 17);
                reasons.Add($"ชื่อผู้โอนใกล้เคียง ({nm.Reason})");
            }
        }

        return new Result(
            Math.Min(score, MaxScore),
            reasons.Count > 0 ? string.Join(", ", reasons) : "",
            nameConfident,
            refMatch);
    }
}
