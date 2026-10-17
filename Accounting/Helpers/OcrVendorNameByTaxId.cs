namespace Accounting.Helpers;

/// <summary>
/// **ชื่อผู้ขายที่ผูกกับเลขผู้เสียภาษีผู้ขาย** (กุญแจ = เลข · หลัก <c>DbdIdentityGuard</c>) — ตัวตัดสินเดียวของ <c>OcrService.ParseThaiDocument</c>
/// (ทีม OCR 13 เขียนไว้ใน service · ทีม OCR 15 ย้ายมาที่นี่พร้อมเงื่อนไข "คอลัมน์เดียวกัน")
///
/// <para>═══ ที่มา ═══ ทีม OCR 13: ป้าย "ผู้ขาย" บนใบ BS2026100001 คือช่องพนักงานขาย (คอลัมน์ขวาของบรรทัดเลขผู้ขาย) ⇒ ชื่อใกล้ป้าย = ชื่อลูกค้า ·
/// จึงเลือกชื่อนิติบุคคลที่อยู่<b>เหนือเลขผู้ขายใกล้ที่สุด</b> (≤ 300 ตัว · ไม่มีเลข 13 หลักอื่นคั่น) · ฝ่ายค้าน OCR 13 P1-2: หัวกระดาษสองคอลัมน์
/// "ผู้ขาย⇥ผู้ซื้อ / บริษัท เอ จำกัด⇥บริษัท บี จำกัด / เลข… v⇥เลข… b" ⇒ ชื่อที่ใกล้เลข v ที่สุดคือ<b>ท้ายบรรทัด</b> = ชื่อผู้ซื้อ ⇒ ชื่อสองฝั่งสลับ ขณะที่เลขไม่สลับ
/// (ผลเหมือนกันทั้งคอลัมน์ tab และเว้นวรรคเดียว ⇒ ข้อความ Tesseract ก็โดน)</para>
///
/// <para>═══ กติกา ═══ ไล่ชื่อจากใกล้เลขที่สุดขึ้นไป · ชื่อต้องอยู่<b>คอลัมน์เดียวกับเลข</b> (<see cref="SameColumn"/>):
/// คนละคอลัมน์ ⇒ ข้ามไปชื่อถัดไป · ตัดสินคอลัมน์ไม่ได้ (เว้นวรรคเดียว — ข้อความไม่มีข้อมูลคอลัมน์) ⇒ <b>ไม่ตอบ</b> ให้ผู้เรียกใช้คำตอบเดิม
/// (ชื่อใกล้ป้ายผู้ขาย — คำตอบของ OCR 10 base 7ca8c0d8) · ไม่มีชื่อที่ผ่าน ⇒ ไม่ตอบ</para>
/// </summary>
public static class OcrVendorNameByTaxId
{
    /// <summary>ระยะสูงสุดจากชื่อถึงเลข (ตัวอักษร) — บล็อกหัวผู้ขายเดียวกัน</summary>
    public const int MaxDistance = 300;

    /// <param name="text">ข้อความที่ผู้เรียกใช้หาตำแหน่ง (normalize แล้ว — tab ของข้อความ PDF ตามพิกัดรอด)</param>
    /// <param name="names">ชื่อนิติบุคคลที่เป็นตัวเลือกผู้ขาย (ตำแหน่งคำนำหน้า)</param>
    /// <param name="allNames">ตำแหน่งชื่อนิติบุคคล<b>ทุกชื่อ</b>บนข้อความ (รวมชื่อที่ผู้เรียกตัดทิ้ง) — นับว่าบรรทัดมีหลายชื่อไหม</param>
    /// <param name="taxIdPos">ตำแหน่งเลขผู้ขาย</param>
    /// <param name="allTaxIdPos">ตำแหน่งเลข 13 หลักทุกตัวบนข้อความ</param>
    /// <returns>ชื่อที่ผูกกับเลข · null = ไม่ตอบ (ผู้เรียกคงคำตอบเดิม)</returns>
    public static string? Pick(string text, IReadOnlyList<(string FullName, int Position)> names, IReadOnlyList<int> allNames,
        int taxIdPos, IReadOnlyList<int> allTaxIdPos)
    {
        var candidates = names
            .Where(c => c.Position < taxIdPos && taxIdPos - c.Position <= MaxDistance
                        && !allTaxIdPos.Any(t => t > c.Position && t < taxIdPos))
            .OrderByDescending(c => c.Position);
        foreach (var c in candidates)
        {
            switch (SameColumn(text, c.Position, allNames, taxIdPos, allTaxIdPos))
            {
                case true: return c.FullName;
                case false: continue;
                default: return null;
            }
        }
        return null;
    }

    /// <summary>
    /// ชื่อที่ <paramref name="namePos"/> อยู่คอลัมน์เดียวกับเลขที่ <paramref name="taxIdPos"/> ไหม — true / false / null (ตัดสินไม่ได้)
    /// <list type="bullet">
    /// <item>บรรทัดเดียวกัน ⇒ true (ชื่อกับเลขของตัวเองบนแถวเดียว · "ชื่อ⇥X⇥เลขประจำตัว…⇥v")</item>
    /// <item>ทั้งสองบรรทัดมี tab (ข้อความ PDF ตามพิกัด — <see cref="PdfPositionalText.ColumnSeparator"/>) ⇒ ลำดับช่องเท่ากัน</item>
    /// <item>ไม่มี tab ทั้งคู่ ⇒ true เฉพาะเมื่อบรรทัดชื่อมีชื่อเดียว<b>และ</b>บรรทัดเลขมีเลขเดียว (หัวใบเรียงลงคอลัมน์เดียว) · อื่น ๆ ⇒ null
    /// (สองคอลัมน์คั่นเว้นวรรคเดียว — ไม่รู้ว่าคำไหนอยู่คอลัมน์ไหน)</item>
    /// <item>มี tab ข้างเดียว ⇒ true เฉพาะเมื่อข้างที่มี tab อยู่ช่องแรก และอีกข้างมีชื่อ/เลขตัวเดียว · อื่น ๆ ⇒ null</item>
    /// </list>
    /// </summary>
    internal static bool? SameColumn(string text, int namePos, IReadOnlyList<int> allNames, int taxIdPos, IReadOnlyList<int> allTaxIdPos)
    {
        var (nStart, nEnd) = LineOf(text, namePos);
        var (tStart, tEnd) = LineOf(text, taxIdPos);
        if (nStart == tStart) return true;
        var nCell = CellOf(text, nStart, nEnd, namePos);
        var tCell = CellOf(text, tStart, tEnd, taxIdPos);
        if (nCell is int a && tCell is int b) return a == b;
        var nSingle = allNames.Count(p => p >= nStart && p < nEnd) <= 1;
        var tSingle = allTaxIdPos.Count(p => p >= tStart && p < tEnd) <= 1;
        if (nCell == null && tCell == null) return nSingle && tSingle ? true : null;
        if (nCell == 0 && tSingle) return true;
        if (tCell == 0 && nSingle) return true;
        return null;
    }

    private static (int Start, int End) LineOf(string text, int pos)
    {
        var start = pos <= 0 ? 0 : text.LastIndexOf('\n', pos - 1) + 1;
        var end = text.IndexOf('\n', pos);
        return (start, end < 0 ? text.Length : end);
    }

    /// <summary>ลำดับช่อง (จำนวน tab ก่อนตำแหน่งบนบรรทัด) · null = บรรทัดไม่มี tab (ไม่มีข้อมูลคอลัมน์)</summary>
    private static int? CellOf(string text, int start, int end, int pos)
    {
        if (text.IndexOf('\t', start, end - start) < 0) return null;
        var n = 0;
        for (var i = start; i < pos; i++) if (text[i] == '\t') n++;
        return n;
    }
}
