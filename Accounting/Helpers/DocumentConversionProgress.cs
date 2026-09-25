using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>สถานะ "ออกเอกสารต่อแล้วหรือยัง" ของเอกสารต้นทาง (ใบเสนอราคา/ใบขอซื้อ/ใบสั่งซื้อ/ใบรับสินค้า/ใบส่งของ)
/// — ออกไปหน้าเว็บและพารามิเตอร์ตัวกรองเป็น<b>ชื่อ</b> ("None"/"Partial"/"Full") ห้ามเลข</summary>
public enum ConversionProgressState
{
    /// <summary>ยังไม่ออกเอกสารต่อ</summary>
    None,
    /// <summary>ออกไปแล้วบางส่วน (หรือมีใบลูกผูกอยู่แต่ไม่ได้ยกรายการ — ไม่ทราบสัดส่วน)</summary>
    Partial,
    /// <summary>ออกครบทุกจำนวนแล้ว</summary>
    Full,
}

/// <summary>
/// ตัวตัดสิน<b>ตัวเดียว</b>ว่าเอกสารต้นทาง "ออกเอกสารต่อไปแล้วเท่าไร" + ข้อความป้าย lifecycle ของมัน
///
/// <para>═══ ที่มา (รอบ 196 · ทีม Q) ═══ ผู้ใช้: "จากหน้ารวมใบเสนอราคา จะรู้ได้ยังไงว่าใบไหนออกใบแจ้งหนี้แล้ว
/// โดยไม่ต้องไล่เปิดทีละใบ" — หน้ารวมขึ้น "⏳ รอดำเนินการต่อ" <b>ทุกใบ</b> เพราะ <c>GetDocumentsAsync</c> ไม่ส่ง % การแปลง
/// เข้า <c>MapDocumentToResponse</c> (มีแต่หน้ารายละเอียดที่คำนวณ) ⇒ ป้ายโกหกกับใบที่ออกใบแจ้งหนี้ไปแล้ว.
/// รอบนี้ list · detail · ตัวกรองบนหน้ารวม เรียกสูตรนี้ตัวเดียว (ห้ามสองสูตร — defect class "ตัวตั้งตัวเดียว")</para>
///
/// <para>═══ สูตร ═══ Σ จำนวนที่ใบลูก (ยังมีผล — ไม่ Voided/Rejected/ลบ) ยกบรรทัดไปผ่าน <c>SourceLineId</c> แยกตาม
/// <b>แกน</b> (ส่งมอบ · วางบิล/แจ้งหนี้ · อื่น) แล้วเอาแกนที่ไปไกลสุดเทียบ Σ จำนวนของใบต้นทาง. แยกแกนเพราะใบเสนอราคา
/// ที่ส่งของ 50% + ออกใบแจ้งหนี้ 50% (ของก้อนเดียวกัน) <b>ไม่ใช่</b> "ครบ 100%" (สูตรเดิมรวมทุกแกน ⇒ ขึ้นครบทั้งที่ยังค้าง
/// ครึ่งหนึ่ง) — แกนเดียวกับ <c>ComputeConsumptionAsync</c> ที่กันแปลงเกินตอน convert</para>
/// </summary>
public static class DocumentConversionProgress
{
    /// <summary>ความคลาดเคลื่อนของจำนวน (เท่ากับ <c>DocumentService.QtyEpsilon</c> ของด่านแปลงเกิน)</summary>
    public const decimal QtyEpsilon = 0.0001m;

    /// <summary>ชนิดที่ "จบหน้าที่เมื่อถูกแปลงต่อ" (ไม่ใช่เมื่อจ่ายเงิน) — ชุดเดียวที่ป้าย lifecycle · ตัวกรอง · หน้าเว็บ ใช้</summary>
    public static readonly DocumentType[] SourceTypes =
    {
        DocumentType.Quotation,
        DocumentType.PurchaseRequisition,
        DocumentType.PurchaseOrder,
        DocumentType.GoodsReceiptNote,
        DocumentType.DeliveryNote,
    };

    /// <summary>สถานะใบลูกที่<b>ไม่นับ</b> (ถูกยกเลิก/ปฏิเสธ = ต้นทางกลับไปเป็น "ยังไม่ออก") — array ให้ EF แปลเป็น NOT IN ได้ตรง ๆ</summary>
    public static readonly DocumentStatus[] InactiveChildStatuses =
    {
        DocumentStatus.Voided,
        DocumentStatus.Rejected,
    };

    public static bool IsConversionBearing(DocumentType type) => Array.IndexOf(SourceTypes, type) >= 0;

    /// <summary>แกนการเติมเต็มของใบลูก — ตารางเดียวกับด่านกันแปลงเกิน (<c>DocumentService.GetFulfillmentAxis</c> เรียกตัวนี้)</summary>
    public enum Axis { Other, Delivery, Billing }

    public static Axis AxisOf(DocumentType childType) => childType switch
    {
        // ส่งมอบของจริง: ใบส่งของฝั่งขาย · ใบรับสินค้าฝั่งซื้อ (รับบางส่วนหลายใบเป็นเรื่องปกติ)
        DocumentType.DeliveryNote or DocumentType.GoodsReceiptNote => Axis.Delivery,
        // แจ้งหนี้/วางบิล
        DocumentType.Invoice or DocumentType.TaxInvoice or DocumentType.BillingNote
            or DocumentType.PurchaseInvoice or DocumentType.Expense
            or DocumentType.PurchaseOrder => Axis.Billing,
        _ => Axis.Other,
    };

    public readonly record struct Progress(decimal? Percent, ConversionProgressState State);

    /// <summary>
    /// ตัดสินความคืบหน้า — <paramref name="totalSourceQty"/> = Σ จำนวนบรรทัดของใบต้นทาง ·
    /// <paramref name="consumed"/> = แถว (ชนิดใบลูก, จำนวนที่ยกไป) เฉพาะใบลูกที่ยังมีผล ·
    /// <paramref name="hasActiveLinkedChild"/> = มีใบลูกที่ยังมีผลอ้าง <c>RelatedDocumentId</c> มาที่ใบนี้
    /// (เช่น ใบแจ้งหนี้มัดจำที่ไม่ได้ยกรายการ) ⇒ อย่างน้อย "บางส่วน" ห้ามบอกว่ายังไม่ออก
    /// </summary>
    public static Progress Evaluate(decimal totalSourceQty,
        IEnumerable<(DocumentType ChildType, decimal Quantity)> consumed, bool hasActiveLinkedChild)
    {
        decimal delivery = 0m, billing = 0m, other = 0m;
        foreach (var (childType, qty) in consumed)
        {
            switch (AxisOf(childType))
            {
                case Axis.Delivery: delivery += qty; break;
                case Axis.Billing: billing += qty; break;
                default: other += qty; break;
            }
        }
        var furthest = Math.Max(delivery, Math.Max(billing, other));

        // ไม่มีจำนวนให้เทียบ (ไม่มีบรรทัด/จำนวนรวม 0) — บอกได้แค่ว่ามีใบลูกหรือไม่ ไม่แต่ง %
        if (totalSourceQty <= 0m)
            return new Progress(null, hasActiveLinkedChild || furthest > QtyEpsilon
                ? ConversionProgressState.Partial : ConversionProgressState.None);

        if (furthest >= totalSourceQty - QtyEpsilon)
            return new Progress(100m, ConversionProgressState.Full);

        if (furthest > QtyEpsilon)
        {
            var pct = Math.Round(furthest / totalSourceQty * 100m, 1, MidpointRounding.AwayFromZero);
            // ยังไม่ครบ ⇒ ห้ามโชว์ 100 (ปัดขึ้นจาก 99.96)
            return new Progress(Math.Min(pct, 99.9m), ConversionProgressState.Partial);
        }
        return new Progress(0m, hasActiveLinkedChild ? ConversionProgressState.Partial : ConversionProgressState.None);
    }

    /// <summary>ใบลูกที่จะเอ่ยชื่อในป้าย (ล่าสุดที่ยังมีผล)</summary>
    public readonly record struct ChildRef(string DocumentNumber, DocumentType DocumentType, DocumentStatus Status);

    /// <summary>
    /// ป้าย lifecycle ของเอกสารต้นทาง — (LifecycleStatus, LifecycleReason) ·
    /// <paramref name="activeChildCount"/> = จำนวนใบลูกที่ยังมีผลทั้งหมด (รวม <paramref name="latest"/>) ⇒ เกิน 1 ใบต่อท้าย "+N"
    /// <list type="bullet">
    /// <item>ครบ: "✓ ออกใบแจ้งหนี้ INV-001 แล้ว" (ใบลูกยังเป็นร่าง ⇒ "✓ ร่างใบแจ้งหนี้ไว้แล้ว (ยังไม่อนุมัติ)")</item>
    /// <item>บางส่วน: "◐ ออกใบแจ้งหนี้ INV-001 แล้ว 60%" · ไม่ทราบสัดส่วน ⇒ "… แล้ว · ไม่ทราบสัดส่วน"</item>
    /// <item>ยังไม่ออก: "⏳ ยังไม่ออกเอกสารต่อ"</item>
    /// </list>
    /// ชื่อชนิดมาจาก <see cref="DocumentTypeNames"/> (ตารางเดียวกับหัวกระดาษ)
    /// </summary>
    public static (string Status, string Reason) Lifecycle(Progress progress, ChildRef? latest, int activeChildCount)
    {
        var more = latest.HasValue && activeChildCount > 1 ? $" +{activeChildCount - 1}" : "";
        switch (progress.State)
        {
            case ConversionProgressState.Full:
                return ("Done", latest.HasValue
                    ? $"✓ {Did(latest.Value)}{more}"
                    : "✓ ออกเอกสารต่อครบแล้ว");
            case ConversionProgressState.Partial:
                var share = PercentText(progress.Percent);
                return ("PartiallyDone", latest.HasValue
                    ? $"◐ {Did(latest.Value)}{share}{more}"
                    : $"◐ ออกเอกสารต่อแล้ว{share}");
            default:
                return ("Open", "⏳ ยังไม่ออกเอกสารต่อ");
        }
    }

    private static string Did(ChildRef c)
    {
        var name = DocumentTypeNames.Title(c.DocumentType, "th");
        // ใบลูกยังไม่ออกจริง (ร่าง/รออนุมัติ — เลขยังเป็น DRAFT-…) ⇒ ห้ามพูดว่า "ออก" และห้ามโชว์เลขชั่วคราว
        return DocumentStatusRules.IsIssued(c.Status)
            ? $"ออก{name} {c.DocumentNumber} แล้ว"
            : $"ร่าง{name}ไว้แล้ว (ยังไม่อนุมัติ)";
    }

    private static string PercentText(decimal? pct)
    {
        if (!pct.HasValue || pct.Value <= 0m) return " · ไม่ทราบสัดส่วน";
        // ปัดลงเสมอ — "99.9" ห้ามกลายเป็น "100%" บนป้ายของใบที่ยังไม่ครบ
        var whole = Math.Floor(pct.Value);
        return whole < 1m ? " <1%" : $" {whole:0}%";
    }

    /// <summary>ตัวเลือกตัวกรองบนหน้ารวม (ค่า = ชื่อ enum · ป้ายไทย) — หน้าเว็บสร้าง &lt;option&gt; จากชุดนี้ ห้ามพิมพ์เอง</summary>
    public static readonly (ConversionProgressState Value, string Label)[] FilterOptions =
    {
        (ConversionProgressState.None, "⏳ ยังไม่ออกเอกสารต่อ"),
        (ConversionProgressState.Partial, "◐ ออกเอกสารต่อบางส่วน"),
        (ConversionProgressState.Full, "✓ ออกเอกสารต่อครบแล้ว"),
    };

    /// <summary>แปลงค่าพารามิเตอร์ตัวกรอง (ชื่อ enum ไม่สนตัวพิมพ์) — ว่าง = ไม่กรอง · ตัวเลขล้วนไม่รับ (enum ออกเป็นชื่อเสมอ)</summary>
    public static bool TryParseFilter(string? raw, out ConversionProgressState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var s = raw.Trim();
        if (s.All(char.IsDigit)) return false;
        if (!Enum.TryParse<ConversionProgressState>(s, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed)) return false;
        state = parsed;
        return true;
    }
}
