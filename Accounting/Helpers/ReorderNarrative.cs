using System.Globalization;

namespace Accounting.Helpers;

/// <summary>หนึ่งแถวของรายงานจุดสั่งซื้อ (ผลของ <c>InventoryReorderForecastService</c>)</summary>
/// <param name="Sku">รหัสสินค้า</param>
/// <param name="Name">ชื่อสินค้า</param>
/// <param name="CurrentStock">คงเหลือปัจจุบัน</param>
/// <param name="AvgDailyDemand">ยอดขายเฉลี่ยต่อวัน (Croston)</param>
/// <param name="DaysOfStockRemaining">จะหมดในกี่วัน — <c>-1</c> = ไม่มีการเคลื่อนไหว (∞)</param>
/// <param name="SuggestedOrderQuantity">จำนวนที่แนะนำให้สั่ง</param>
/// <param name="Urgency">"Critical" | "Warning" | "OK" | "Idle"</param>
public readonly record struct ReorderRow(
    string? Sku, string? Name, decimal CurrentStock, decimal AvgDailyDemand,
    decimal DaysOfStockRemaining, decimal SuggestedOrderQuantity, string? Urgency);

/// <summary>
/// **เล่าเรื่องรายงานจุดสั่งซื้อเป็นภาษาไทย — ด้วยเลขคณิต ไม่ใช่ AI** (pure, ไม่มี I/O)
///
/// ═══ ทำไมถอด AI ออก (D-5 · <c>DECISION_AUDIT_2026-09-18.md</c> §9.3) ═══
/// <para>endpoint <c>inventory/reorder-narrative</c> เดิมส่งตารางที่<b>เราคำนวณเสร็จแล้ว</b>
/// (คงเหลือ · ยอดขายเฉลี่ย · จะหมดในกี่วัน · ควรสั่งเท่าไร) ไปให้ DeepSeek เรียบเรียง
/// เป็นร้อยแก้ว — ผิดเกณฑ์ <c>DECISION_DOCTRINE</c> §2.1 สองข้อพร้อมกัน:</para>
/// <list type="number">
/// <item><b>"ห้ามถามสิ่งที่เราเพิ่งคำนวณคำตอบเองไปแล้ว"</b> — ทุกตัวเลขในประโยคมาจาก
///   payload ที่เราส่งไปเอง ครูไม่ได้รู้อะไรที่เราไม่รู้</item>
/// <item><b>"คำตอบต้องเป็นชุดปิดที่ตรวจกลับได้"</b> — ร้อยแก้วไม่มี candidate set
///   ⇒ ไม่มีด่าน ⇒ ตัวเลขที่โมเดลพิมพ์ผิดจะออกหน้าจอโดยไม่มีอะไรทัก
///   (ญาติของ "ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ")</item>
/// </list>
///
/// <para>และผลข้างเคียงที่วัดได้: <c>AiFeatureKey.ReorderForecast</c> เป็น<b>ตัวเดียว
/// ในระบบ</b>ที่ยิง provider โดยไม่มี <c>ILocalDistillationModel</c> ⇒ ปิด provider
/// ทุกตัวแล้ว endpoint ตอบ "AI ปิดอยู่" = <b>kill-switch test ไม่ผ่านตรง ๆ</b>
/// (กฎเหล็ก #1 ข้อ 5). การ register นักเรียนให้มันจะได้ "ร้อยแก้วของใบเก่ามาตอบ
/// ใบใหม่" ซึ่งแย่กว่าไม่ตอบ ⇒ ทางที่ถูกคือ<b>ไม่ถามตั้งแต่แรก</b></para>
///
/// <para><b>ทางไปต่อของผู้ใช้</b>: ข้อความที่ได้ครอบคลุมสิ่งเดิมทุกข้อ (ลำดับความเร่งด่วน ·
/// SKU ที่ต้องสั่งก่อน · จำนวน · เหตุผล) และ<b>ทำงานเมื่อ AI ดับ/เกินงบ/ไม่มีเน็ต</b></para>
/// </summary>
public static class ReorderNarrative
{
    /// <summary>จำนวน SKU สูงสุดที่เอ่ยชื่อในประโยค — มากกว่านี้สรุปเป็นยอดรวม
    /// (ประโยคที่ยาวเกินคนไม่อ่าน = เท่ากับไม่มีคำเตือน)</summary>
    public const int MaxNamedSkus = 5;

    private static string Qty(decimal v)
        => v == Math.Floor(v)
            ? v.ToString("#,##0", CultureInfo.InvariantCulture)
            : v.ToString("#,##0.##", CultureInfo.InvariantCulture);

    private static string Label(ReorderRow r)
    {
        var sku = string.IsNullOrWhiteSpace(r.Sku) || r.Sku == "-" ? null : r.Sku!.Trim();
        var name = string.IsNullOrWhiteSpace(r.Name) ? null : r.Name!.Trim();
        if (sku != null && name != null) return $"{sku} ({name})";
        return sku ?? name ?? "(ไม่ระบุสินค้า)";
    }

    /// <summary>"จะหมดใน N วัน" — <c>-1</c>/ไม่มีการเคลื่อนไหว ⇒ บอกตรง ๆ ว่าไม่รู้
    /// ห้ามแปลงเป็นเลขสวย ๆ (ไม่รู้ = บอกว่าไม่รู้)</summary>
    private static string DaysPhrase(ReorderRow r)
        => r.DaysOfStockRemaining < 0 || r.AvgDailyDemand <= 0m
            ? "ยังไม่มียอดขายในช่วงที่วิเคราะห์ จึงประเมินวันหมดไม่ได้"
            : r.DaysOfStockRemaining <= 0m
                ? "หมดแล้ว"
                : $"จะหมดใน {Qty(r.DaysOfStockRemaining)} วัน";

    /// <summary>สรุปรายงานเป็นภาษาไทย 3–5 ประโยค — <b>ทุกตัวเลขมาจากแถวที่รับเข้ามา</b>
    /// ไม่มีการประมาณ/แต่งเพิ่ม · แถวว่าง ⇒ คืนข้อความว่า "ไม่มีรายการเร่งด่วน"
    /// (ไม่ใช่สตริงว่าง ซึ่งหน้าจอแยกไม่ออกจาก "ยังไม่ได้ประมวลผล")</summary>
    public static string Build(IReadOnlyList<ReorderRow> rows)
    {
        if (rows == null || rows.Count == 0)
            return "ไม่มีรายการที่ต้องสั่งซื้อเร่งด่วนในรอบนี้ — สต็อกทุกรายการยังสูงกว่าจุดสั่งซื้อ";

        var critical = rows.Where(r => string.Equals(r.Urgency, "Critical", StringComparison.OrdinalIgnoreCase)).ToList();
        var warning = rows.Where(r => string.Equals(r.Urgency, "Warning", StringComparison.OrdinalIgnoreCase)).ToList();
        var toOrder = rows.Where(r => r.SuggestedOrderQuantity > 0m)
            // เรียงด้วย "ใกล้หมดที่สุดก่อน" — แถวที่ประเมินไม่ได้ (−1) ไปท้าย
            .OrderBy(r => r.DaysOfStockRemaining < 0 ? decimal.MaxValue : r.DaysOfStockRemaining)
            .ThenByDescending(r => r.SuggestedOrderQuantity)
            .ToList();

        var parts = new List<string>
        {
            $"รอบนี้มีสินค้าที่ต้องดู {rows.Count} รายการ — เร่งด่วน (Critical) {critical.Count} รายการ "
            + $"และใกล้ถึงจุดสั่งซื้อ (Warning) {warning.Count} รายการ",
        };

        if (toOrder.Count == 0)
        {
            parts.Add("ยังไม่มีรายการใดที่คำนวณแล้วต้องสั่งเพิ่ม (คงเหลือยังครอบคลุม lead time + รอบทบทวน)");
        }
        else
        {
            var named = toOrder.Take(MaxNamedSkus).ToList();
            parts.Add("เรียงตามความเร่งด่วน: " + string.Join(" · ", named.Select(r =>
                $"{Label(r)} คงเหลือ {Qty(r.CurrentStock)} · {DaysPhrase(r)} → แนะนำสั่ง {Qty(r.SuggestedOrderQuantity)}")));
            if (toOrder.Count > named.Count)
                parts.Add($"อีก {toOrder.Count - named.Count} รายการที่ต้องสั่งเพิ่ม ดูรายละเอียดในตารางด้านบน");
        }

        var first = toOrder.FirstOrDefault(r => r.DaysOfStockRemaining >= 0m);
        if (first.SuggestedOrderQuantity > 0m)
            parts.Add($"ควรออกใบสั่งซื้อของ {Label(first)} ก่อน เพราะ{DaysPhrase(first)} "
                      + "ซึ่งสั้นกว่าระยะเวลารอของจากผู้ขาย");

        var idle = rows.Count(r => string.Equals(r.Urgency, "Idle", StringComparison.OrdinalIgnoreCase));
        if (idle > 0)
            parts.Add($"อีก {idle} รายการไม่มีการเคลื่อนไหวเลยในช่วงที่วิเคราะห์ — พิจารณาลดสต็อก/ยกเลิกการสั่งซ้ำ");

        return string.Join(" · ", parts);
    }
}
