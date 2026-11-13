namespace Accounting.Helpers;

/// <summary>
/// **ช่อง 2 (0%) / ช่อง 3 (ยกเว้น) ของรายงาน ภ.พ.30 จาก "ส่วนของแต่ละใบ" + แถวที่ยัง "ใช้" — ตัวรวมตัวเดียว (pure · รอบ 269 ชุดสอง M1)**
///
/// <para>ที่มา: แถวสรุป <c>ZERO_RATED_SALES</c>/<c>EXEMPT_SALES</c> ถูกสร้างครั้งเดียวตอน generate · ติ๊กแถวออก/ปุ่มดึงเปลี่ยนแถว §87 แต่ไม่แตะช่อง 2/3 ⇒
/// ช่อง 4 (= Σแถว − ช่อง 2) ติดลบ/ผิด · ตอนนี้ทั้งตอน generate และหลังแก้ (ติ๊ก · ดึง · สร้างใหม่คืนติ๊ก · ตัวทบทวน) รวมด้วยตัวนี้ตัวเดียว</para>
///
/// <para>กติกา: ส่วนของใบ (ตัดสินแล้วโดย <c>OutputVatOwnership.SalesBoxOwner</c>/<c>ExemptBoxOwner</c>/<c>AdjustmentNoteCounts</c>) นับเมื่อ
/// (ก) ใบนั้น<b>ไม่มีแถวรายการ</b>ในรายงาน (ใบต้นทางที่ไม่มี VAT — ขายยกเว้นบนใบแจ้งหนี้/ใบสำคัญรับ) หรือ (ข) มีแถวรายการที่<b>ยังใช้อยู่</b>อย่างน้อยหนึ่งแถว ·
/// แถวเตือน (⚠️/🚫/[รอใบกำกับ]) และแถวสรุปไม่ใช่ "แถวรายการ"</para>
/// </summary>
public static class Pp30SalesBoxSummary
{
    public readonly record struct Part(decimal ZeroRated, decimal Exempt);

    public readonly record struct Row(Guid? DocumentId, string? Description, string? IncomeTypeCode, bool IsExcluded);

    public static Part Totals(IEnumerable<Row> rows, IReadOnlyDictionary<Guid, Part> partsByDocument)
    {
        var docRows = rows
            .Where(r => r.DocumentId.HasValue
                && VatReportLineKind.SideOf(r.IncomeTypeCode) != VatReportSide.Summary
                && !TaxReportPullEligibility.IsWarningLineDescription(r.Description))
            .GroupBy(r => r.DocumentId!.Value)
            .ToDictionary(g => g.Key, g => g.Any(r => !r.IsExcluded));
        decimal zero = 0m, exempt = 0m;
        foreach (var (docId, part) in partsByDocument)
        {
            if (docRows.TryGetValue(docId, out var anyActive) && !anyActive) continue;
            zero += part.ZeroRated;
            exempt += part.Exempt;
        }
        return new Part(zero, exempt);
    }

    /// <summary>บวกส่วนของใบ (ใบลดหนี้ส่งค่าติดลบมาเอง)</summary>
    public static void Add(Dictionary<Guid, Part> parts, Guid documentId, decimal zeroRated, decimal exempt)
    {
        if (zeroRated == 0m && exempt == 0m) return;
        parts[documentId] = parts.TryGetValue(documentId, out var p) ? new Part(p.ZeroRated + zeroRated, p.Exempt + exempt) : new Part(zeroRated, exempt);
    }
}
