using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>บรรทัด JE ที่ใช้ตัดสินยอดสุทธิ — บัญชี · เดบิต · เครดิต</summary>
public readonly record struct PosJournalLineAmount(Guid AccountId, decimal Debit, decimal Credit);

/// <summary>JE หนึ่งใบในสาย "ใบเดิม → ตัวกลับ → ตัวกลับของตัวกลับ …" (ตาม <c>ReversedByEntryId</c>)</summary>
public sealed record PosJournalChainEntry(
    Guid Id, string EntryNumber, JournalEntryStatus Status, IReadOnlyList<PosJournalLineAmount> Lines);

/// <summary>สิ่งที่การยกเลิกบิลต้องทำกับ JE ขาย</summary>
public enum PosVoidSaleJournalAction
{
    /// <summary>กลับรายการ JE ใบที่ระบุ (<see cref="PosVoidSaleJournalDecision.ReverseEntryId"/>)</summary>
    Reverse = 0,
    /// <summary>JE ขายถูกกลับครบแล้ว (เช่นผู้ทำบัญชีกลับด้วยมือ) — ห้ามกลับซ้ำ · บันทึก audit แล้วไปต่อ</summary>
    SkipAlreadyReversed = 1,
    /// <summary>ไปต่ออัตโนมัติไม่ได้ — ต้องให้คนแก้ก่อน (ข้อความบอกทางไปต่อ)</summary>
    Block = 2,
}

public sealed record PosVoidSaleJournalDecision(
    PosVoidSaleJournalAction Action, Guid? ReverseEntryId, string Message);

/// <summary>
/// ตัวตัดสินเดียวของ "ยกเลิกบิล POS แล้วต้องทำอะไรกับ JE ขาย" (รอบ 193 · ฝ่ายค้าน M2)
///
/// <para><b>ที่มา</b>: <c>VoidOrderAsync</c> เรียก <c>ReverseJournalEntryAsync</c> กับ JE ขายเสมอ ·
/// JE ขาย POS ไม่ผูกเอกสาร (<c>SourceDocumentId</c> ว่าง) ⇒ ผู้ทำบัญชีกลับรายการเองจากหน้า JE ได้ ·
/// ถ้ากลับไปแล้ว <c>ReverseJournalEntryAsync</c> โยน "กลับได้เฉพาะ Posted" ⇒ ยกเลิกบิลล้ม<b>ทุกครั้ง</b>
/// (ถูกห่อเป็น POS-VOID-JE-REVERSAL ที่บอกให้ "เปิดงวด" ซึ่งไม่ใช่สาเหตุ) และผู้ใช้ไม่มีทางไปต่อเลย</para>
///
/// <para><b>กติกา</b> — ตัดสินจาก <b>ยอดสุทธิรายบัญชีของทั้งสาย</b> (นับเฉพาะสถานะที่อยู่ใน GL:
/// Posted + Reversed — ตัวเดียวกับรายงานงบ) ไม่ใช่จากสถานะของใบเดิมอย่างเดียว:
/// <list type="bullet">
/// <item>สุทธิเป็นศูนย์ทุกบัญชี ⇒ ถูกกลับครบแล้ว → <see cref="PosVoidSaleJournalAction.SkipAlreadyReversed"/> (ห้ามกลับซ้ำ)</item>
/// <item>สุทธิเท่ากับยอดของใบสุดท้ายในสายที่ยัง Posted ⇒ กลับใบนั้นใบเดียวแล้วสุทธิเป็นศูนย์พอดี →
///   <see cref="PosVoidSaleJournalAction.Reverse"/> (กรณีปกติ = ใบเดิมใบเดียว · กรณี "กลับแล้วกลับคืน" = ใบสุดท้าย)</item>
/// <item>นอกนั้น (ใบเดิมเป็นร่าง · สายไม่ครบ · ยอดค้างไม่เท่าใบใดใบหนึ่ง) → <see cref="PosVoidSaleJournalAction.Block"/>
///   พร้อมข้อความไทยที่บอกว่าต้องทำอะไร</item>
/// </list>
/// ข้อจำกัดที่รู้ตัว: ใบสำคัญปรับปรุงที่ผู้ใช้ทำเอง<b>นอกสาย</b> (ไม่ได้กดกลับรายการ) มองไม่เห็นจากตรงนี้</para>
/// </summary>
public static class PosVoidSaleJournal
{
    private const decimal Tolerance = 0.005m;

    public static PosVoidSaleJournalDecision Decide(IReadOnlyList<PosJournalChainEntry> chain, string orderNumber)
    {
        if (chain.Count == 0)
            return new(PosVoidSaleJournalAction.Block, null,
                $"ยกเลิกบิล #{orderNumber} ไม่ได้: หา JE ขายของบิลนี้ไม่พบ (อาจถูกลบ) — บิลยังไม่ถูกยกเลิก · "
                + "ให้ผู้ทำบัญชีตรวจว่ารายได้ของบิลนี้อยู่ใน GL หรือไม่ ก่อนยกเลิกบิล");

        var original = chain[0];
        if (original.Status == JournalEntryStatus.Draft)
            return new(PosVoidSaleJournalAction.Block, null,
                $"ยกเลิกบิล #{orderNumber} ไม่ได้: JE ขาย {original.EntryNumber} ยังเป็นร่าง — "
                + "ลงบัญชี (Post) หรือยกเลิกร่างนั้นก่อน แล้วยกเลิกบิลใหม่");
        if (original.Status == JournalEntryStatus.Voided)
            return new(PosVoidSaleJournalAction.SkipAlreadyReversed, null,
                $"JE ขาย {original.EntryNumber} ถูกยกเลิก (Voided) ไปก่อนแล้ว — ไม่มียอดใน GL ให้กลับ");

        // กรณีปกติ: ใบเดิมยัง Posted และไม่มีใครกลับ ⇒ กลับใบเดิม (ไม่ดูยอด — ใบยอด 0 ก็ต้องปิดสายให้ครบ)
        if (chain.Count == 1 && original.Status == JournalEntryStatus.Posted)
            return new(PosVoidSaleJournalAction.Reverse, original.Id, $"กลับรายการ JE ขาย {original.EntryNumber}");

        var net = NetByAccount(chain.Where(InLedger));
        if (IsZero(net))
        {
            var by = chain.Skip(1).Where(InLedger).Select(e => e.EntryNumber).ToList();
            return new(PosVoidSaleJournalAction.SkipAlreadyReversed, null,
                $"JE ขาย {original.EntryNumber} ถูกกลับรายการครบแล้ว"
                + (by.Count > 0 ? $" ด้วย {string.Join(", ", by)}" : "")
                + " — ไม่กลับซ้ำ (กลับซ้ำ = รายได้/ภาษีขายติดลบ)");
        }

        var last = chain[^1];
        if (last.Status == JournalEntryStatus.Posted && SameNet(net, NetByAccount(new[] { last })))
            return new(PosVoidSaleJournalAction.Reverse, last.Id,
                $"กลับรายการ {last.EntryNumber} (ใบสุดท้ายในสายของ JE ขาย {original.EntryNumber})");

        var open = net.Count(kv => Math.Abs(kv.Value) >= Tolerance);
        return new(PosVoidSaleJournalAction.Block, null,
            $"ยกเลิกบิล #{orderNumber} ไม่ได้: JE ขาย {original.EntryNumber} ถูกกลับรายการไปแล้วบางส่วน "
            + $"(ยังค้างยอด {open} บัญชี · สาย: {string.Join(" → ", chain.Select(e => e.EntryNumber))}) — "
            + "ระบบกลับอัตโนมัติไม่ได้เพราะจะกลับซ้ำส่วนที่กลับไปแล้ว · ให้ผู้ทำบัญชีทำใบสำคัญปรับปรุงให้ยอดขายของบิลนี้"
            + "เป็นศูนย์ แล้วยกเลิกบิลอีกครั้ง หรือทำ \"คืนเงิน\" รายการที่เหลือแทนการยกเลิก");
    }

    private static bool InLedger(PosJournalChainEntry e)
        => e.Status is JournalEntryStatus.Posted or JournalEntryStatus.Reversed;

    private static Dictionary<Guid, decimal> NetByAccount(IEnumerable<PosJournalChainEntry> entries)
    {
        var net = new Dictionary<Guid, decimal>();
        foreach (var l in entries.SelectMany(e => e.Lines))
            net[l.AccountId] = (net.TryGetValue(l.AccountId, out var v) ? v : 0m) + l.Debit - l.Credit;
        return net;
    }

    private static bool IsZero(Dictionary<Guid, decimal> net) => net.Values.All(v => Math.Abs(v) < Tolerance);

    private static bool SameNet(Dictionary<Guid, decimal> a, Dictionary<Guid, decimal> b)
    {
        foreach (var key in a.Keys.Union(b.Keys))
        {
            var x = a.TryGetValue(key, out var av) ? av : 0m;
            var y = b.TryGetValue(key, out var bv) ? bv : 0m;
            if (Math.Abs(x - y) >= Tolerance) return false;
        }
        return true;
    }
}
