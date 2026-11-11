namespace Accounting.Helpers;

/// <summary>
/// รอบ 259 (C-M5 · คำตัดสิน 259-4-8) — engine คืนตารางรายการ<b>ไม่ครบ</b> (Σ บรรทัด &lt; ยอดหัวใบ · "LinesShort") ⇒ ลอง "ตารางจากข้อความ"
/// (<see cref="RawTextLineSplitter"/>) เป็นผู้สมัครที่สอง · ตัวตัดสินเดียว · pure
/// <para>เดิม: ตัวแตกบรรทัดจากข้อความทำงานเฉพาะเมื่อ engine คืน 0 บรรทัด (<c>OcrService.TrySplitLineItemsAsync</c> <c>if (Items.Count &gt; 0) return</c>) ⇒
/// Azure คืน 5 จาก 8 แถว ⇒ <c>[Σ-GAP] LinesShort</c> ผู้ใช้ต้องพิมพ์แถวที่ขาดเอง (ผิดกฎ #3 "ยืนยันอย่างเดียว")</para>
/// <para>รับตารางข้อความ<b>เฉพาะเมื่อ</b>: (1) ยอดรวมทั้งสิ้นของหัวใบ<b>พิสูจน์แล้ว</b> (<c>OcrTotalAnchor</c> Proven/Confirmed — หัวใบที่ยังไม่พิสูจน์ห้ามใช้ชี้ขาดบรรทัด ·
/// บทเรียน "น้ำดื่ม × 3 แถว หัวใบอ่านเพี้ยน") (2) ตารางของ engine ขาด (Σ ต่ำกว่าทั้งฐานและยอดรวม — Σ เกิน = แถวซ้ำ/สำเนา เป็นงานของ
/// <c>OcrDuplicateLineGuard</c> ไม่ใช่ที่นี่) (3) ตารางข้อความผ่านด่านเดียวกับเส้นแตกบรรทัด (<see cref="OcrLineSplitGuard"/> — Σ ตรงยอด ±1 บาท)
/// หลังตัดแถวสรุป/ชำระ (<see cref="OcrNonItemRow"/>) และ (4) ได้แถว<b>มากกว่า</b> engine · ไม่ครบเงื่อนไข ⇒ คงตารางของ engine (<c>[Σ-GAP]</c> เดิมฟ้อง)</para>
/// <para>ไม่เรียก AI (kill-switch ผ่านโดยโครงสร้าง) · ไม่แต่งตัวเลข — ทุกยอดมาจากข้อความบนกระดาษ</para>
/// </summary>
public static class OcrTextTableFallback
{
    public const string Tag = "[LineSplit]";

    public sealed record Result(bool Replace, IReadOnlyList<OcrLineSplitGuard.SplitLine> Lines, string? Note, string? DedupeNote = null)
    {
        public static readonly Result Keep = new(false, Array.Empty<OcrLineSplitGuard.SplitLine>(), null);
    }

    /// <param name="engineAmounts">ยอดของบรรทัดตามที่ engine คืน (ลำดับเดิม)</param>
    /// <param name="subTotal">ยอดก่อนภาษีของหัวใบ (null/0 = ไม่รู้)</param>
    /// <param name="provenTotal">ยอดรวมทั้งสิ้นที่กระดาษพิสูจน์ (null = ยังไม่พิสูจน์ ⇒ ไม่แตะ)</param>
    /// <param name="rawText">ข้อความดิบของใบ</param>
    public static Result Decide(IReadOnlyList<decimal> engineAmounts, decimal? subTotal, decimal? provenTotal, string? rawText)
    {
        if (engineAmounts is not { Count: > 0 } || provenTotal is not > 0m || string.IsNullOrWhiteSpace(rawText)) return Result.Keep;
        var total = provenTotal.Value;
        var sub = subTotal is > 0m ? subTotal.Value : 0m;
        var engineSum = engineAmounts.Sum();
        var tol = OcrLineSplitGuard.ToleranceBaht;
        // ตารางของ engine ลงยอดอยู่แล้ว (ฐานหรือรวม) ⇒ ห้ามแตะ · Σ เกินยอดรวม ⇒ ไม่ใช่ "ขาด" (แถวซ้ำ/สำเนา — ด่านอื่น)
        if ((sub > 0m && Math.Abs(engineSum - sub) <= tol) || Math.Abs(engineSum - total) <= tol) return Result.Keep;
        if (engineSum > total + tol) return Result.Keep;

        var json = RawTextLineSplitter.SplitToJson(rawText, sub > 0m ? sub : null, total);
        if (string.IsNullOrWhiteSpace(json)) return Result.Keep;
        var guard = OcrLineSplitGuard.Evaluate(json, sub, total);
        if (!guard.Accepted) return Result.Keep;
        var lines = guard.Lines.Where(l => !OcrNonItemRow.IsSummaryOrTenderRow(l.Description)).ToList();
        if (lines.Count <= engineAmounts.Count) return Result.Keep;
        var sum = lines.Sum(l => l.Amount);
        var reconciles = (sub > 0m && Math.Abs(sum - sub) <= tol) || Math.Abs(sum - total) <= tol;
        if (!reconciles) return Result.Keep;
        var note = $"{Tag} ตารางของเครื่องอ่านไม่ครบ ({engineAmounts.Count} บรรทัด รวม ฿{engineSum:N2} ไม่ถึงยอดบนกระดาษ) — "
                   + $"ใช้ตารางจากข้อความแทน {lines.Count} บรรทัด (รวม ฿{sum:N2} ตรงกับยอดบนกระดาษที่พิสูจน์แล้ว) — กรุณาตรวจก่อนยืนยัน";
        return new Result(true, lines, note, guard.DedupeNote);
    }
}
