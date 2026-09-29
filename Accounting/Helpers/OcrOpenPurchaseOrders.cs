namespace Accounting.Helpers;

/// <summary>ใบสั่งซื้อที่ยังเปิดอยู่ของผู้ขาย (ทุกสาขาของนิติบุคคลเดียวกัน) — ใหม่สุดก่อน</summary>
public sealed record OcrOpenPo(Guid Id, string DocumentNumber);

/// <summary>ผลของ <see cref="OcrOpenPurchaseOrders.Plan"/></summary>
/// <param name="Display">รายการที่แสดงบนแบนเนอร์ (ใบที่กระดาษอ้างถึงมาก่อน แล้วใหม่สุด · ไม่เกินเพดาน)</param>
/// <param name="Total">จำนวน PO ค้างทั้งหมด (ไม่ใช่เพดานของแบนเนอร์)</param>
/// <param name="AutoLink">PO ที่เลขพิมพ์บนกระดาษ<b>ใบเดียว</b> ⇒ ผูกอัตโนมัติ · null = ไม่มี/กำกวม</param>
public sealed record OcrOpenPoPlan(IReadOnlyList<OcrOpenPo> Display, int Total, OcrOpenPo? AutoLink);

/// <summary>
/// **แบนเนอร์ "ผู้ขายรายนี้มีใบสั่งซื้อค้าง" + ผูก PO อัตโนมัติจากเลขบนกระดาษ** (pure · รอบ 200 ทีม K · ฝ่ายค้าน r199 A-5)
///
/// <para>═══ ที่มา ═══ รอบ 197 (K-3) ขยาย PO ค้างเป็น "ทุกสาขาของนิติบุคคลเดียวกัน" แต่ query ยัง <c>Take(5)</c> ใบล่าสุด<b>ก่อน</b>
/// เทียบเลข PO บนกระดาษ ⇒ ผู้ขายเครือใหญ่ที่มี PO ค้าง &gt; 5 ใบ (ทุกสาขารวม) ใบที่กระดาษอ้างจริงหลุดจาก 5 ใบ = ไม่ผูกอัตโนมัติ ·
/// ข้อความ "ค้าง N ใบ" ก็ถูกเพดาน 5 (ตัวเลขที่ผู้ใช้เห็นผิด)</para>
///
/// <para>กติกา: เทียบเลขบนกระดาษกับ PO ค้าง<b>ทั้งหมด</b> · ตรงใบเดียว (เลข ≥ 4 ตัวอักษร · ไม่สนตัวพิมพ์) = ผูก · ตรงหลายใบ = ให้คนเลือก ·
/// ตัดเพดานเฉพาะรายการที่<b>แสดง</b> (ใบที่ถูกอ้างอยู่หัวรายการเสมอ) · จำนวนรวมเป็นจำนวนจริง</para>
/// </summary>
public static class OcrOpenPurchaseOrders
{
    /// <summary>เพดานรายการบนแบนเนอร์ (เท่าเดิม)</summary>
    public const int DisplayLimit = 5;

    /// <param name="newestFirst">PO ค้างทั้งหมด เรียงใหม่สุดก่อน</param>
    /// <param name="paperText">ข้อความบนกระดาษ (ว่าง = ไม่ผูกอัตโนมัติ)</param>
    public static OcrOpenPoPlan Plan(IReadOnlyList<OcrOpenPo> newestFirst, string? paperText, int displayLimit = DisplayLimit)
    {
        if (newestFirst == null || newestFirst.Count == 0)
            return new OcrOpenPoPlan(Array.Empty<OcrOpenPo>(), 0, null);
        var text = paperText ?? "";
        var hits = newestFirst
            .Where(p => !string.IsNullOrEmpty(p.DocumentNumber) && p.DocumentNumber.Length >= 4
                && text.Contains(p.DocumentNumber, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var display = hits.Concat(newestFirst.Where(p => !hits.Contains(p)))
            .Take(Math.Max(1, displayLimit))
            .ToList();
        return new OcrOpenPoPlan(display, newestFirst.Count, hits.Count == 1 ? hits[0] : null);
    }
}
