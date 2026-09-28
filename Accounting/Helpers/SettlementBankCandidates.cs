using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>หัวของรอบโอนที่ตัวคัดผู้สมัครต้องรู้ (ค่าจากแถว <c>SettlementBatch</c> ของบริษัทนี้)</summary>
public sealed record SettlementBankBatchFacts(
    Guid BatchId, SettlementBatchStatus Status, decimal NetPayout, DateTime PayoutDate, Guid? BankAccountId,
    Guid? PayoutJournalEntryId, Guid? CurrentBankTransactionId);

/// <summary>รายการเดินบัญชี 1 แถว (ของบริษัทนี้ · ยังไม่กระทบยอด) + รอบโอนอื่นที่ผูกไว้แล้ว (ถ้ามี)</summary>
public sealed record SettlementBankTxnRow(
    Guid Id, Guid BankAccountId, DateTime TransactionDate, BankTransactionType Type, decimal Amount,
    ReconciliationStatus Status, Guid? MatchedJournalEntryId, Guid? LinkedBatchId, string? Description, string? Reference);

/// <summary>ผู้สมัคร 1 แถวที่หน้าเว็บแสดง — <c>CanMatch</c>/<c>Message</c> มาจาก <see cref="SettlementBankMatch.Check"/> ตัวเดียวกับตอนกดจับคู่จริง</summary>
public sealed record SettlementBankCandidate(
    Guid BankTransactionId, DateTime TransactionDate, string TransactionType, decimal Amount, string? Description, string? Reference,
    int DaysFromPayout, bool CanMatch, string Message);

/// <summary>
/// **ผู้สมัคร "เงินเข้าธนาคารจริง" ของรอบโอน** (รอบ 198 เฟส 1 ทีม D) — คัดจากรายการเดินบัญชีที่ยังไม่กระทบยอดของบัญชีที่รอบโอนระบุ
///
/// <para>ไม่มีเกณฑ์ของตัวเอง: ทุกแถวถูกตัดสินด้วย <see cref="SettlementBankMatch.Check"/> (ตัวเดียวกับ <c>MatchBankTransactionAsync</c>) ⇒
/// ปุ่ม "จับคู่" ที่หน้าเว็บเปิดให้ = คำตอบเดียวกับที่ service จะให้ตอนกด · แถวที่จับคู่ไม่ได้ยังแสดงพร้อมเหตุผล (ยอดไม่เท่า/คนละทิศ)
/// ให้ผู้ใช้รู้ว่าทำไม ไม่ใช่ลิสต์ว่างเงียบ · เรียง: จับคู่ได้ก่อน → ใกล้วันเงินเข้า · กรองช่วง ±<see cref="WindowDays"/> วัน</para>
///
/// <para>G6: pure</para>
/// </summary>
public static class SettlementBankCandidates
{
    /// <summary>ช่วงวันรอบวันเงินเข้า (ธนาคารลงรายการช้า/เร็วกว่าวันที่แพลตฟอร์มแจ้ง)</summary>
    public const int WindowDays = 10;
    public const int MaxRows = 30;

    public static IReadOnlyList<SettlementBankCandidate> Evaluate(SettlementBankBatchFacts batch, IEnumerable<SettlementBankTxnRow> rows)
    {
        var payoutDay = batch.PayoutDate.Date;
        return rows
            .Select(t => new { t, days = (int)Math.Round((t.TransactionDate.Date - payoutDay).TotalDays, MidpointRounding.AwayFromZero) })
            .Where(x => Math.Abs(x.days) <= WindowDays)
            .Select(x =>
            {
                var d = SettlementBankMatch.Check(batch.Status, batch.BatchId, batch.NetPayout, batch.BankAccountId,
                    batch.PayoutJournalEntryId, batch.CurrentBankTransactionId, x.t.Id,
                    new SettlementBankTxnFacts(true, x.t.BankAccountId, x.t.Type, x.t.Amount, x.t.Status,
                        x.t.MatchedJournalEntryId, x.t.LinkedBatchId));
                return new SettlementBankCandidate(x.t.Id, x.t.TransactionDate, x.t.Type.ToString(), x.t.Amount, x.t.Description,
                    x.t.Reference, x.days, d.Ok, d.Message);
            })
            .OrderByDescending(c => c.CanMatch)
            .ThenBy(c => Math.Abs(c.DaysFromPayout))
            .ThenBy(c => c.TransactionDate)
            .Take(MaxRows)
            .ToList();
    }
}
