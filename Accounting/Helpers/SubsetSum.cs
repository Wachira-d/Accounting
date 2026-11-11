namespace Accounting.Helpers;

/// <summary>
/// **ค้นชุดรายการที่ยอดรวมเท่าเป้า — ตัวเดียวของระบบ** (รอบ 259 P5 · ยกจาก <c>WhtCertMatchPolicy.FindSubsets</c> ตามดีไซน์
/// erp-review/2026-11-11/team-D-lump-payment.md §3.6 — ห้ามมีอัลกอริทึม subset ตัวที่สอง)
/// <para>ลึกก่อน ตามลำดับที่ผู้เรียกเรียงมา (ผลจึงคงที่ — deterministic) · ตัดกิ่งเมื่อยอดเกินเป้า (ยอดทุกตัวต้อง &gt; 0) ·
/// หยุดเมื่อพบ <c>maxSolutions</c> ชุด (พอบอกว่า "ชุดเดียว" หรือ "กำกวม") หรือเมื่อเดินครบงบ <c>nodeBudget</c> (กันรายการมากแล้วค้าง —
/// งบหมด = <see cref="Result.Exhausted"/> ⇒ ผู้เรียกต้องถือว่า "ไม่แน่ใจ" ไม่ใช่ "ไม่มีชุด")</para>
/// </summary>
public static class SubsetSum
{
    /// <param name="Solutions">ชุดที่พบ (ดัชนีของรายการ เรียงตามลำดับเดิม)</param>
    /// <param name="Exhausted">เดินครบงบก่อนค้นจบ — ผลไม่ครบ</param>
    public sealed record Result(IReadOnlyList<IReadOnlyList<int>> Solutions, bool Exhausted);

    /// <summary>ค้นชุดที่ Σ = <paramref name="target"/> ± <paramref name="tolerance"/> ขนาด <paramref name="minCount"/>..<paramref name="maxCount"/></summary>
    public static Result Find(IReadOnlyList<decimal> amounts, decimal target, decimal tolerance, int minCount, int maxCount,
        int maxSolutions = 2, int nodeBudget = 200_000)
    {
        var found = new List<IReadOnlyList<int>>();
        var pick = new List<int>();
        var nodes = 0;
        var exhausted = false;

        void Walk(int start, decimal sum)
        {
            if (found.Count >= maxSolutions || exhausted) return;
            if (++nodes > nodeBudget) { exhausted = true; return; }
            if (pick.Count >= minCount && Math.Abs(sum - target) <= tolerance)
            {
                found.Add(pick.ToArray());
                return;
            }
            if (sum > target + tolerance || pick.Count >= maxCount) return;
            for (var i = start; i < amounts.Count && found.Count < maxSolutions && !exhausted; i++)
            {
                if (amounts[i] <= 0m) continue;
                pick.Add(i);
                Walk(i + 1, sum + amounts[i]);
                pick.RemoveAt(pick.Count - 1);
            }
        }

        Walk(0, 0m);
        return new Result(found, exhausted);
    }
}
