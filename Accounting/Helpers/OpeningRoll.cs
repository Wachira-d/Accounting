namespace Accounting.Helpers;

/// <summary>
/// **ยอดยกมาจากการปิดปี — กติกาตัวเดียว** (ฝ่ายค้านรอบส่งออก 2026-10-15 P1-1 · erp-review/2026-10-14/EXPORT_AUDIT.md
/// §"ฝ่ายค้านรอบส่งออก")
///
/// <para><b>บั๊กที่มา:</b> <c>RollOpeningBalancesAsync</c> เดิมคิด "ยอดยกมา ม.ค. ปี N" = รายการทุกใบตั้งแต่เปิดบริษัทก่อน 1 ม.ค. N
/// + ยอดยกมา ม.ค. ปี N−1 ซึ่ง<b>รวมทุกปีก่อนหน้าไว้แล้ว</b> ⇒ ตั้งแต่ปิดปีครั้งที่สอง ปีเก่าถูกนับซ้ำทุกปี · งบทดลอง/บัญชีแยกประเภท/
/// ยอด ณ วันที่ (แดชบอร์ด/งบกระแสเงินสด) อ่านฐานเดียวกันจึงผิด "ตรงกัน" ทั้งระบบ</para>
///
/// <para><b>นิยามใหม่ (เท่ากับงบทดลอง ณ 31 ธ.ค. N−1):</b> ฐาน = งวดล่าสุดที่มียอดยกมาและเริ่ม<b>ก่อน</b> 1 ม.ค. N (ยอดยกมาปิดปีก่อน
/// หรือยอดนำเข้าจากระบบเดิม — รวมยอดนำเข้ากลางปีที่เดิมไม่ถูกนับเลย) + รายการ (สินทรัพย์/หนี้สิน/ทุน) ตั้งแต่วันเริ่มงวดฐานถึงก่อน 1 ม.ค. N ·
/// ไม่มีฐาน = รายการตั้งแต่เปิดบริษัท</para>
///
/// <para><b>ของที่เก็บผิดไปแล้ว</b> (กฎ F2 ข้อ 9): <c>FindOpeningRollMismatchesAsync</c> (รายงานอ่านอย่างเดียว) →
/// <c>RepairOpeningRollAsync</c> (ซ่อมเฉพาะปีที่ผู้ใช้เลือก + audit chain) · ไม่มีงานไหนซ่อมเองตอนเปิดระบบ ·
/// งวดที่มียอดนำเข้า (<see cref="IsImported"/>) ไม่ถูกซ่อมอัตโนมัติ (ผู้ใช้ตั้งใจลงยอดเอง)</para>
/// </summary>
public static class OpeningRoll
{
    public const string RuleCode = "GL-OPENING-ROLL";

    /// <summary>หมายเหตุที่ตัวลงยอดยกมาจากระบบเดิม (<c>MigrationWizardService.CommitAsync</c>) ประทับ — ตัวตั้งเดียว</summary>
    public const string ImportedNotePrefix = "Migrated via session";

    /// <summary>หมายเหตุของแถวที่การปิดปีเขียน (แถวรุ่นเก่าเป็น null)</summary>
    public static string RolledNote(int year) => $"ยกมาจากการปิดปี {year - 1}";

    /// <summary>แถวยอดยกมานี้มาจากการนำเข้า (ไม่ใช่จากการปิดปี)</summary>
    public static bool IsImported(string? notes)
        => notes != null && notes.StartsWith(ImportedNotePrefix, StringComparison.Ordinal);

    /// <summary>ยอดยกมาใหม่ = ฐาน + รายการเคลื่อนไหว (ต่อบัญชี · เดบิต/เครดิตรวม) · บัญชีที่เป็น 0 ทั้งสองข้างถูกตัดทิ้ง</summary>
    public static Dictionary<Guid, (decimal Debit, decimal Credit)> Combine(
        IReadOnlyDictionary<Guid, (decimal Debit, decimal Credit)> anchor,
        IReadOnlyDictionary<Guid, (decimal Debit, decimal Credit)> movements)
    {
        var result = new Dictionary<Guid, (decimal Debit, decimal Credit)>();
        foreach (var id in anchor.Keys.Union(movements.Keys))
        {
            anchor.TryGetValue(id, out var a);
            movements.TryGetValue(id, out var m);
            var d = a.Debit + m.Debit;
            var c = a.Credit + m.Credit;
            if (d == 0m && c == 0m) continue;
            result[id] = (d, c);
        }
        return result;
    }

    /// <summary>บัญชีที่ยอดสุทธิที่เก็บไว้ ≠ ยอดที่ควรเป็น (เทียบสุทธิ เพราะงบทดลองอ่านสุทธิ — เดบิต/เครดิตรวมต่างกันแต่สุทธิเท่ากัน = ไม่ผิด)</summary>
    public static List<(Guid AccountId, (decimal Debit, decimal Credit) Stored, (decimal Debit, decimal Credit) Expected)> Diff(
        IReadOnlyDictionary<Guid, (decimal Debit, decimal Credit)> stored,
        IReadOnlyDictionary<Guid, (decimal Debit, decimal Credit)> expected)
    {
        var list = new List<(Guid, (decimal, decimal), (decimal, decimal))>();
        foreach (var id in stored.Keys.Union(expected.Keys))
        {
            stored.TryGetValue(id, out var s);
            expected.TryGetValue(id, out var e);
            if (s.Debit - s.Credit != e.Debit - e.Credit) list.Add((id, s, e));
        }
        return list;
    }
}
