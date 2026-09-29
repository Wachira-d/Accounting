using Accounting.Models.Enums;

namespace Accounting.Helpers;

// ═══════════════════════════════════════════════════════════════════════
// Settlement — ตัวตัดสินบริสุทธิ์ของรอบ 200 ทีม T (เวลา/ภาษีของรอบโอน) · ผู้เรียก = SettlementPostingService.BuildGateAsync
// (หาข้อเท็จจริงจากฐาน) → SettlementPostingGate.Evaluate (แปลงเป็นปัญหาของแผน) · จุดเรียกล็อกด้วย tools/required_call_site_check.py
// ═══════════════════════════════════════════════════════════════════════

/// <summary>รอบโอนก่อนหน้าของช่องทางเดียวกัน (ไม่ถูกยกเลิก/ลบ · เรียงตามวันเงินเข้า) — ข้อเท็จจริงจากฐาน</summary>
public sealed record SettlementWalletPrevious(string PayoutRef, DateTime PayoutDate, decimal ClosingWalletBalance);

public enum SettlementWalletContinuityKind
{
    /// <summary>ยอดต้นรอบ = ยอดปลายรอบของรอบก่อน (±เกณฑ์ของสมการรอบโอน)</summary>
    Continuous = 1,
    /// <summary>ต่างกันเกินเกณฑ์ — ผังพักจะคลาดจาก wallet จริงถาวร</summary>
    Gap = 2,
    /// <summary>ไม่มีรอบก่อนให้เทียบ (รอบแรกของช่องทาง) — <b>ไม่รู้</b> ไม่ใช่ "ต่อเนื่อง" (DOCTRINE §1)</summary>
    NoPreviousBatch = 3,
}

/// <param name="Difference">ยอดต้นรอบ − ยอดปลายรอบของรอบก่อน (0 เมื่อไม่มีรอบก่อน)</param>
public sealed record SettlementWalletContinuityResult(
    SettlementWalletContinuityKind Kind, decimal Opening, SettlementWalletPrevious? Previous, decimal Difference);

/// <summary>
/// **ยอด wallet ต่อเนื่องข้ามรอบโอนไหม** (review198-A R-A12 · รอบ 200 ทีม T)
/// <para>สมการของรอบโอน Σ บรรทัด = ยอดโอน + (ปลายรอบ − ต้นรอบ) ตรวจได้แค่ "ภายในรอบ" — ยอดยกมา/ยกไปที่ถูกกรอกให้พอดีกับบรรทัด ทำให้สมการเป็นจริง
/// ตามนิยาม (F2 ข้อ 6: ด่านที่ป้อนผลของสูตรที่ตัวเองตรวจ = ผ่านตลอดกาล) ⇒ ตรวจกับข้อเท็จจริงอิสระ = ยอดปลายรอบของรอบก่อนในช่องทางเดียวกัน ·
/// ต่างกัน = ผังพักคลาดจาก wallet จริง (ยอดที่แพลตฟอร์มหัก/เติมนอกรอบโอน หรือรอบโอนที่ขาดไป)</para>
/// </summary>
public static class SettlementWalletContinuity
{
    public static SettlementWalletContinuityResult Judge(decimal opening, SettlementWalletPrevious? previous)
    {
        if (previous is null)
            return new SettlementWalletContinuityResult(SettlementWalletContinuityKind.NoPreviousBatch, opening, null, 0m);
        var diff = opening - previous.ClosingWalletBalance;
        return new SettlementWalletContinuityResult(
            SettlementBatchMath.IsBalanced(diff) ? SettlementWalletContinuityKind.Continuous : SettlementWalletContinuityKind.Gap,
            opening, previous, diff);
    }
}

/// <summary>บรรทัดขายของ<b>รอบโอนอื่น</b>ที่ยังไม่ลงบัญชี ซึ่งจับคู่ใบขายใบเดียวกับที่รอบนี้จะรับชำระ — ข้อเท็จจริงจากฐาน (tenant แล้ว)</summary>
/// <param name="AlreadyInClearing">บรรทัดอ้าง PaymentIntent/การรับชำระ (แผนนับว่าอยู่ในผังพักแล้ว — ไม่รับชำระใบ)</param>
public sealed record SettlementOtherBatchSaleLine(
    Guid BatchId, string PayoutRef, Guid DocumentId, SettlementLineType LineType, decimal Amount,
    bool AlreadyInClearing, SettlementMatchStatus MatchStatus);

/// <summary>ยอดที่รอบโอนอื่นที่ยังไม่ลงบัญชีจะรับชำระใบหนึ่ง</summary>
public sealed record SettlementPendingReceipt(decimal Amount, IReadOnlyList<string> PayoutRefs);

/// <summary>
/// **รับชำระเกินข้ามรอบโอน** (review198-B R-B13 · รอบ 200 ทีม T) — ด่าน "ยอดค้าง ≥ ยอดโอน" เห็นแค่การรับชำระที่ลงแล้ว ⇒ สองรอบโอนที่ยังไม่ลง
/// จับคู่ใบ 1,000 เดียวกันคนละ 1,000 ผ่านทั้งคู่ ⇒ รอบแรกที่ลงรับชำระ · รอบที่สองล้มกลางทางหรือ (ถ้าลงคนละผู้ใช้/จังหวะ) ใบถูกรับเกิน ·
/// ตัวนี้รวม "ยอดที่รอบอื่นจะรับ" ต่อใบด้วยกติกาเดียวกับแผน (<see cref="SettlementBatchMath.Plan"/>: องค์ประกอบขาย · ไม่อ้างเงินที่อยู่ในผังพักแล้ว ·
/// ไม่ใช่ยอดไม่ตรงที่ยังไม่ยืนยัน · รวมต่อใบต่อรอบแล้วต้องเป็นบวก) · รอบที่รับชำระใบนั้นไปแล้ว (ลงค้างครึ่งทาง — ยอดค้างลดแล้ว) <b>ไม่นับซ้ำ</b>
/// </summary>
public static class SettlementCrossBatchReceipts
{
    public static IReadOnlyDictionary<Guid, SettlementPendingReceipt> PendingElsewhere(
        IEnumerable<SettlementOtherBatchSaleLine> lines, IReadOnlySet<(Guid BatchId, Guid DocumentId)> alreadyReceived)
    {
        var perBatchDoc = lines
            .Where(l => l.Amount != 0m && !l.AlreadyInClearing && l.MatchStatus != SettlementMatchStatus.AmountMismatch
                && SettlementLineTypeRules.For(l.LineType).Posting == SettlementPostingKind.SaleComponent
                && !alreadyReceived.Contains((l.BatchId, l.DocumentId)))
            .GroupBy(l => (l.BatchId, l.DocumentId))
            .Select(g => (g.Key.DocumentId, Ref: g.First().PayoutRef, Amount: g.Sum(l => l.Amount)))
            .Where(x => x.Amount > 0m);
        return perBatchDoc.GroupBy(x => x.DocumentId)
            .ToDictionary(g => g.Key,
                g => new SettlementPendingReceipt(g.Sum(x => x.Amount), g.Select(x => x.Ref).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList()));
    }
}

/// <summary>ใบขายสรุปของวันเดียวกันจากรอบโอนอื่นของช่องทางนี้ที่ยังมีผล (รอบโอนไม่ถูกยกเลิก · ใบไม่ถูกยกเลิก)</summary>
/// <param name="Issued">ออกเลขแล้ว (<see cref="DocumentStatusRules.IsIssued"/>)</param>
public sealed record SettlementSameDaySummary(string Number, string PayoutRef, bool Issued, DateTime CreatedAt);

/// <summary>ใบขายสรุปของรอบนี้เป็นใบสรุปเพิ่มเติมของวันนั้น — อ้างใบแรกของวัน</summary>
public sealed record SettlementSupplementarySummary(
    DateTime Day, IReadOnlyList<Guid> LineIds, string FirstNumber, string FirstPayoutRef, bool FirstIssued, int ExistingCount);

/// <summary>
/// **ใบสรุปเพิ่มเติมของวันเดียวกัน** (คำตัดสินรอบ 200 ข้อ 15 · review198-C C-9 / O-2) — payout หลายรอบมีออเดอร์ของวันเดียวกันเป็นเรื่องปกติของทุกแพลตฟอร์ม ⇒
/// เดิมบล็อก <c>SummarySaleDuplicate</c> ⇒ ผู้ใช้ต้องออกเอกสารขายเองทุกครั้ง · ตอนนี้: เลขใหม่ · อ้าง<b>ใบแรกของวัน</b> (สร้างก่อนสุด) · จุดความรับผิดวันเดิม ·
/// ใบแรกยังไม่ออกเลข (ลงบัญชีรอบนั้นค้าง) ⇒ บล็อกพร้อมทางไปต่อ (อ้างเลข DRAFT ไม่ได้) · กันรายได้ซ้ำระดับออเดอร์ยังอยู่ (ออเดอร์เดียวกันลงแล้ว ·
/// ออเดอร์มีเอกสารของตัวเอง) · ใบสรุปของรอบโอนที่<b>ยกเลิกแล้ว</b>ยังเป็นรายได้ซ้ำ (ไม่ใช่ใบแรกของใบเพิ่มเติม)
/// </summary>
public static class SettlementSummarySupplement
{
    public static SettlementSupplementarySummary? Judge(DateTime day, IReadOnlyList<Guid> lineIds, IReadOnlyList<SettlementSameDaySummary> sameDay)
    {
        if (sameDay.Count == 0) return null;
        var first = sameDay.OrderBy(s => s.CreatedAt).ThenBy(s => s.Number, StringComparer.Ordinal).First();
        return new SettlementSupplementarySummary(day, lineIds, first.Number, first.PayoutRef, first.Issued, sameDay.Count);
    }
}

/// <summary>ลักษณะกิจการเรื่องสต็อก ณ ใบขายสรุป (review198-C C-15)</summary>
public enum SettlementStockStance
{
    /// <summary>ผู้เรียกไม่ได้ตรวจ — ไม่ใช่ "ไม่มีสต็อก"</summary>
    NotChecked = 0,
    /// <summary>รู้ว่ากิจการไม่ถือสต็อก (บริการ ฯลฯ) — ไม่ต้องเตือน</summary>
    NoInventory = 1,
    /// <summary>กิจการถือสต็อก (ซื้อมาขายไป · ผลิต · ค้าปลีก · ออนไลน์ …)</summary>
    KeepsInventory = 2,
    /// <summary>ยังไม่ได้ระบุประเภทธุรกิจ — ไม่รู้ ⇒ เตือนแบบให้ไปตั้งค่า</summary>
    Unknown = 3,
}

public static class SettlementStock
{
    /// <summary>ตัวตัดสิน "กิจการถือสต็อกไหม" ตัวเดียวของระบบ (<see cref="InventoryIndustry"/>) — ห้ามเขียนรายชื่อประเภทธุรกิจซ้ำ</summary>
    public static SettlementStockStance StanceOf(IndustryType? industry)
        => InventoryIndustry.IsUnknown(industry) ? SettlementStockStance.Unknown
            : InventoryIndustry.KeepsInventory(industry) ? SettlementStockStance.KeepsInventory
            : SettlementStockStance.NoInventory;
}
