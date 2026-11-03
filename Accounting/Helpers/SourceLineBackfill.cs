using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสิน<b>ตัวเดียว</b>ของการ "จับคู่บรรทัดใบลูกกับบรรทัดใบต้นทางแบบแน่นอน" แล้วประทับ <c>DocumentLine.SourceLineId</c>
/// (รอบ 232 · บั๊กเจ้าของ: หน้ารวมใบสั่งซื้อขึ้น "◐ ออกใบแจ้งหนี้ซื้อ PI-… แล้ว · ไม่ทราบสัดส่วน" ทั้งที่ยอด PI = ยอด PO เป๊ะ)
///
/// <para>═══ ที่มา ═══ ป้าย lifecycle (<see cref="DocumentConversionProgress"/>) อ่านความคืบหน้าจาก <c>SourceLineId</c> ของบรรทัดใบลูก
/// <b>เท่านั้น</b> — ใบลูกที่ผูกแค่หัว (<c>RelatedDocumentId</c>) แต่บรรทัดไม่ชี้บรรทัดต้นทาง = "ไม่ทราบสัดส่วน" ตลอดกาล · ทางเข้าที่เกิดได้:
/// (ก) สร้างใบมือ/API โดยเลือกใบต้นทางบนฟอร์ม (<c>CreateDocumentAsync</c> ส่ง <c>RelatedDocumentId</c> แต่บรรทัดไม่มี SourceLineId) ·
/// (ข) สแกน OCR ที่ผูกใบสั่งซื้อ (<c>/link-po</c>) แต่ไม่ได้จับคู่รายบรรทัด ·
/// (ค) <b>แก้ไขใบต้นทาง</b> หลังแปลงไปแล้ว — <c>UpdateDocumentAsync</c> ลบบรรทัดเดิมทิ้งแล้วสร้างใหม่ (Id ใหม่) ⇒ SourceLineId ของใบลูก
/// ชี้บรรทัดที่ไม่มีอยู่แล้ว (dangling) ·
/// (ง) แก้ไขใบลูกผ่านทางเข้าที่ไม่ส่ง <c>sourceLineId</c> กลับมา (ฟอร์ม/API รุ่นเก่า) ⇒ บรรทัดใหม่ไม่มี SourceLineId</para>
///
/// <para>═══ กติกา "แน่นอน" (ห้ามเดา — หลักการ F2 ข้อ 3) ═══ จำนวนบรรทัดเท่ากัน <b>และ</b> ทุกคู่ตามลำดับบรรทัด: รหัสสินค้าตรงกัน
/// (ถ้าทั้งคู่มี) หรือคำอธิบายตรงกัน (ไม่สนช่องว่าง/ตัวพิมพ์ — ตัว normalize เดียวกับ <see cref="DocumentLinkPolicy.SuggestLineMap"/>)
/// และ (เมื่อ <c>matchQuantity</c>) จำนวนเท่ากันภายใน <see cref="DocumentConversionProgress.QtyEpsilon"/> · กุญแจซ้ำกันในชุดเดียวกัน ⇒ บังคับเทียบจำนวน
/// ยังซ้ำทั้งคู่ ⇒ ไม่จับ (ฝ่ายค้าน S3) · ผิดคู่เดียว = ไม่จับทั้งใบ
/// (คืน null) — ป้าย "ไม่ทราบสัดส่วน" ซื่อสัตย์กว่าสัดส่วนที่แต่งขึ้น · <see cref="DocumentLinkPolicy.SuggestLineMap"/> เป็น
/// "ข้อเสนอให้คนแก้" (เดาได้) — ตัวนี้เป็น "ประทับเองโดยไม่ถามคน" จึงเข้มกว่า</para>
/// </summary>
public static class SourceLineBackfill
{
    public const string RuleCode = "SRC-LINE-BACKFILL";

    /// <summary>กุญแจของบรรทัดที่ใช้เทียบ — <paramref name="Quantity"/> ใช้เฉพาะเมื่อ <c>matchQuantity</c></summary>
    public readonly record struct Line(Guid Id, int LineOrder, string? ProductCode, string? Description, decimal Quantity);

    public static Line Of(DocumentLine l) => new(l.Id, l.LineOrder, l.ProductCode, l.Description, l.Quantity);

    /// <summary>
    /// จับคู่ <paramref name="childLines"/> → <paramref name="sourceLines"/> ทีละคู่ตามลำดับบรรทัด — คืน (Id ลูก → Id ต้นทาง) ครบทุกบรรทัด
    /// หรือ <c>null</c> เมื่อจับไม่ได้แน่นอน (จำนวนบรรทัดไม่เท่า · คู่ใดกุญแจไม่ตรง · ชุดว่าง)
    /// </summary>
    private static Dictionary<Guid, Guid>? CertainMap(IReadOnlyList<Line> childLines, IReadOnlyList<Line> sourceLines, bool matchQuantity)
    {
        if (childLines.Count == 0 || childLines.Count != sourceLines.Count) return null;
        var child = childLines.OrderBy(l => l.LineOrder).ToList();
        var source = sourceLines.OrderBy(l => l.LineOrder).ToList();
        // ฝ่ายค้าน S3: กุญแจ (รหัส/คำอธิบาย) ซ้ำกันภายในชุดเดียวกัน ⇒ ลำดับบรรทัดอย่างเดียวแยกไม่ออกว่าใครคือใคร (ผู้ใช้สลับแถวได้)
        // ⇒ บังคับเทียบจำนวนด้วย · ยังซ้ำทั้งกุญแจและจำนวน ⇒ null ไม่เดา (คง dangling + log · job ลองใหม่)
        if (HasDuplicateKey(child, withQuantity: false) || HasDuplicateKey(source, withQuantity: false))
        {
            matchQuantity = true;
            if (HasDuplicateKey(child, withQuantity: true) || HasDuplicateKey(source, withQuantity: true)) return null;
        }
        var map = new Dictionary<Guid, Guid>(child.Count);
        for (var i = 0; i < child.Count; i++)
        {
            if (!SameKey(child[i], source[i], matchQuantity)) return null;
            map[child[i].Id] = source[i].Id;
        }
        return map;
    }

    private static string KeyOf(Line l)
    {
        var code = (l.ProductCode ?? "").Trim();
        return code.Length > 0 ? "c:" + code.ToUpperInvariant() : "d:" + DocumentLinkPolicy.NormalizeDescription(l.Description);
    }

    private static bool HasDuplicateKey(List<Line> lines, bool withQuantity)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in lines)
        {
            var key = withQuantity
                ? KeyOf(l) + "|q:" + Math.Round(l.Quantity, 4, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : KeyOf(l);
            if (!seen.Add(key)) return true;
        }
        return false;
    }

    private static bool SameKey(Line a, Line b, bool matchQuantity)
    {
        var aCode = (a.ProductCode ?? "").Trim();
        var bCode = (b.ProductCode ?? "").Trim();
        bool keyMatch;
        if (aCode.Length > 0 && bCode.Length > 0)
            keyMatch = string.Equals(aCode, bCode, StringComparison.OrdinalIgnoreCase);
        else if (aCode.Length == 0 && bCode.Length == 0)
        {
            var d = DocumentLinkPolicy.NormalizeDescription(a.Description);
            keyMatch = d.Length > 0 && d == DocumentLinkPolicy.NormalizeDescription(b.Description);
        }
        else keyMatch = false;   // ฝั่งหนึ่งมีรหัสสินค้า อีกฝั่งไม่มี = คนละชนิดข้อมูล ไม่เดาจากคำอธิบาย
        if (!keyMatch) return false;
        return !matchQuantity || Math.Abs(a.Quantity - b.Quantity) <= DocumentConversionProgress.QtyEpsilon;
    }

    /// <summary>
    /// ประทับ <c>SourceLineId</c> ให้บรรทัดใบลูกจากบรรทัดใบต้นทางเมื่อจับคู่ได้แน่นอน — คืนจำนวนบรรทัดที่ประทับ (0 = ไม่แตะอะไร) ·
    /// ไม่แตะเมื่อบรรทัดใดชี้บรรทัดของใบต้นทางนี้อยู่แล้ว (ใบที่แปลงมาจริง/ผูกแล้ว — ค่าที่มีอยู่ชนะเสมอ) ·
    /// บรรทัดที่ชี้ที่อื่น (ใบอื่น/บรรทัดที่ถูกลบไปแล้ว) ถูกทับเฉพาะเมื่อ <paramref name="overwriteForeign"/> (งานซ่อมข้อมูลเก่าที่พิสูจน์แล้วว่า dangling)
    /// </summary>
    public static int Stamp(IReadOnlyList<DocumentLine> childLines, IReadOnlyList<DocumentLine> sourceLines,
        bool matchQuantity, bool overwriteForeign = false)
    {
        var active = childLines.Where(l => !l.IsDeleted).ToList();
        var activeSource = sourceLines.Where(l => !l.IsDeleted).ToList();
        if (active.Count == 0 || activeSource.Count == 0) return 0;
        var sourceIds = activeSource.Select(l => l.Id).ToHashSet();
        if (active.Any(l => l.SourceLineId is Guid s && sourceIds.Contains(s))) return 0;
        if (!overwriteForeign && active.Any(l => l.SourceLineId.HasValue)) return 0;

        var map = CertainMap(active.Select(Of).ToList(), activeSource.Select(Of).ToList(), matchQuantity);
        if (map == null) return 0;
        var n = 0;
        foreach (var l in active)
            if (map.TryGetValue(l.Id, out var src)) { l.SourceLineId = src; n++; }
        return n;
    }

    /// <summary>
    /// ใบต้นทางถูกแก้ไข (บรรทัดเดิมถูกลบ · บรรทัดใหม่ Id ใหม่): ย้าย <c>SourceLineId</c> ของบรรทัดผู้พึ่งพา (<paramref name="dependents"/> — บรรทัดของใบลูก
    /// ที่ชี้บรรทัดเดิม) ไปชี้บรรทัดใหม่ที่ตรงกัน · คืนจำนวนบรรทัดที่ย้าย · จับคู่ไม่ได้แน่นอน = ไม่แตะ (ชี้ dangling ต่อไป ⇒ ป้าย "ไม่ทราบสัดส่วน"
    /// แล้วงานซ่อม <c>SourceLineBackfillJob</c> ลองใหม่ด้วยกติกาจำนวน — watermark ดู <c>UpdatedAt</c> ของใบต้นทาง) · ไม่เทียบจำนวน — การแก้จำนวนบนใบต้นทางคือเหตุผลที่คนแก้ใบ ·
    /// ยกเว้นกุญแจซ้ำในชุดเดียวกัน (ฝ่ายค้าน S3) ⇒ บังคับเทียบจำนวน ยังกำกวม ⇒ ไม่ย้าย
    /// </summary>
    public static int Remap(IReadOnlyList<DocumentLine> dependents, IReadOnlyList<DocumentLine> previousLines, IReadOnlyList<DocumentLine> newLines)
    {
        if (dependents.Count == 0) return 0;
        var map = CertainMap(previousLines.Where(l => !l.IsDeleted).Select(Of).ToList(),
            newLines.Where(l => !l.IsDeleted).Select(Of).ToList(), matchQuantity: false);
        if (map == null) return 0;
        var n = 0;
        foreach (var d in dependents)
            if (d.SourceLineId is Guid old && map.TryGetValue(old, out var now) && now != old) { d.SourceLineId = now; n++; }
        return n;
    }

    /// <summary>
    /// ใบลูกถูกแก้ไขผ่านทางเข้าที่ไม่ส่ง <c>sourceLineId</c> กลับมา (ทุกบรรทัดใหม่ไม่มี SourceLineId แต่บรรทัดเดิมมี): ยกค่าจากบรรทัดเดิมที่ตรงกัน
    /// มาให้บรรทัดใหม่ · ถ้าบรรทัดใหม่บรรทัดใดมี SourceLineId อยู่แล้ว (ฟอร์มส่งกลับครบ) = ไม่แตะ · คืนจำนวนที่ยก
    /// </summary>
    public static int CarryFromPrevious(IReadOnlyList<DocumentLine> previousLines, IReadOnlyList<DocumentLine> newLines)
    {
        var fresh = newLines.Where(l => !l.IsDeleted).ToList();
        var prev = previousLines.Where(l => !l.IsDeleted && l.SourceLineId.HasValue).ToList();
        if (fresh.Count == 0 || prev.Count == 0 || fresh.Any(l => l.SourceLineId.HasValue)) return 0;
        var map = CertainMap(fresh.Select(Of).ToList(),
            previousLines.Where(l => !l.IsDeleted).Select(Of).ToList(), matchQuantity: false);
        if (map == null) return 0;
        var prevById = prev.ToDictionary(l => l.Id, l => l.SourceLineId!.Value);
        var n = 0;
        foreach (var l in fresh)
            if (map.TryGetValue(l.Id, out var oldId) && prevById.TryGetValue(oldId, out var src)) { l.SourceLineId = src; n++; }
        return n;
    }
}
