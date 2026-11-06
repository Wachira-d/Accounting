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

    /// <summary>ใบต้นทางชนิดนี้วัดความคืบหน้าด้วยยอดเงิน (ข้อ 138) ได้ไหม — ใบขอซื้อ<b>ไม่</b>: ราคาบนใบขอซื้อเป็นราคาประมาณ
    /// ใบสั่งซื้อที่ต่อรองได้ถูกกว่าจะไม่มีวันถึง "ครบ" ทั้งที่สั่งครบทุกชิ้น (ฝ่ายค้าน 2026-10-08) ⇒ ใช้จำนวน</summary>
    public static bool MeasuresByValue(DocumentType sourceType) => sourceType != DocumentType.PurchaseRequisition;

    /// <summary>ทีมตรวจเส้นแปลง ข้อ 9 (2026-10-08): ใบวางบิล "จบ" ได้สองทาง — รับชำระครบ (ยอดคงค้าง) <b>หรือ</b> แปลงเป็นใบแจ้งหนี้/ใบกำกับ/
    /// ใบเสร็จครบ · เดิมไม่อยู่ในชุดที่โหลดความคืบหน้า ⇒ วางบิลแล้วออกใบแจ้งหนี้ครบก็ยังขึ้น "⏳ รอรับชำระ" ตลอดไป ·
    /// ไม่ใส่ใน <see cref="SourceTypes"/> เพราะใบวางบิลยังจบด้วยการรับชำระได้ (ป้ายต้องดูทั้งสองทาง ไม่ใช่การแปลงอย่างเดียว)</summary>
    public static bool ClosesByConversionOrPayment(DocumentType type) => type == DocumentType.BillingNote;

    /// <summary>ชนิดที่ต้องโหลดความคืบหน้าการแปลงมาประกอบป้าย — array ให้ EF แปลเป็น IN ได้ (ตัวกรอง "คงค้างนาน" ใช้ชุดเดียวกับชิป)</summary>
    public static readonly DocumentType[] SummaryTypes = SourceTypes.Append(DocumentType.BillingNote).ToArray();

    public static bool NeedsConversionSummary(DocumentType type) => Array.IndexOf(SummaryTypes, type) >= 0;

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

    /// <param name="ByHeaderTotal">true = "ครบ" ตัดสินจากยอดรวมทั้งใบของใบลูกใบเดียว (คำตัดสินข้อ 242-B) ไม่ใช่จากรายการที่จับคู่ — ป้ายต้องบอกที่มา</param>
    public readonly record struct Progress(decimal? Percent, ConversionProgressState State, bool ByHeaderTotal = false);

    /// <summary>ใบลูกที่ผูกใบต้นทางด้วยหัวใบ (<c>RelatedDocumentId</c>) และยังมีผล — ข้อเท็จจริงสำหรับกติกา "ยอดเท่ากันทั้งใบ" (ข้อ 242-B)</summary>
    /// <param name="LineAmountTotal">Σ <c>DocumentLine.Amount</c> ของใบลูก (ฐานก่อน VAT — ฐานเดียวกับยอดใบต้นทางที่ส่งเข้า <see cref="EvaluateByValue"/>)</param>
    /// <param name="IsDeposit">ใบมัดจำ — ไม่ใช่การเติมเต็มรายการ ห้ามนับเป็น "ครบ"</param>
    public readonly record struct HeaderLinkedChild(DocumentType ChildType, decimal LineAmountTotal, bool IsDeposit);

    /// <summary>
    /// คำตัดสินข้อ 242-B (เจ้าของ 2026-11-06 · PO-20260930-0002 → PI-20261001-0005 ยอด 3,584.50 เท่ากันแต่ป้าย "ไม่ทราบสัดส่วน"):
    /// <b>ไม่มีบรรทัดใดของใบลูกชี้บรรทัดของใบนี้เลย</b> (จับคู่รายการไม่ได้ — มักเป็นใบจากสแกน/ฟอร์มที่คำอธิบายไม่ตรงใบต้นทาง) แต่มีใบลูกที่ยังมีผล
    /// ผูกหัวใบ<b>ใบเดียว</b> (ไม่ใช่ใบมัดจำ · แกนส่งมอบ/วางบิล) และยอดฐานก่อน VAT ของใบลูก = ยอดฐานของใบนี้ภายในครึ่งสตางค์ ⇒ ถือว่าออกครบ 100%
    /// (ข้อเท็จจริงเชิงกำหนด: ยอดเงินทั้งใบเท่ากัน · ใบเดียว ⇒ ไม่มีใบอื่นแบ่งยอด) · ใบลูกหลายใบ/ยอดไม่เท่า/วัดด้วยจำนวน (ใบขอซื้อ) ⇒ ยัง "ไม่ทราบสัดส่วน"
    /// (ซื่อสัตย์กว่าสัดส่วนที่แต่งขึ้น) · มีแถวจับคู่รายบรรทัดอยู่แล้ว ⇒ ใช้แถวจับคู่ (หลักฐานละเอียดกว่าชนะ) · pure
    /// </summary>
    internal static bool SoleChildMatchesTotal(decimal totalSourceAmount, int consumedRowCount,
        IReadOnlyCollection<HeaderLinkedChild>? headerLinkedChildren)
    {
        if (consumedRowCount > 0 || totalSourceAmount <= AmountEpsilon) return false;
        if (headerLinkedChildren == null || headerLinkedChildren.Count != 1) return false;
        var c = headerLinkedChildren.First();
        if (c.IsDeposit || AxisOf(c.ChildType) == Axis.Other) return false;
        return Math.Abs(c.LineAmountTotal - totalSourceAmount) <= AmountEpsilon;
    }

    /// <summary>
    /// ตัดสินความคืบหน้า — <paramref name="totalSourceQty"/> = Σ จำนวนบรรทัดของใบต้นทาง ·
    /// <paramref name="consumed"/> = แถว (ชนิดใบลูก, จำนวนที่ยกไป) เฉพาะใบลูกที่ยังมีผล ·
    /// <paramref name="hasActiveLinkedChild"/> = มีใบลูกที่ยังมีผลอ้าง <c>RelatedDocumentId</c> มาที่ใบนี้
    /// (เช่น ใบแจ้งหนี้มัดจำที่ไม่ได้ยกรายการ) ⇒ อย่างน้อย "บางส่วน" ห้ามบอกว่ายังไม่ออก
    /// </summary>
    public static Progress Evaluate(decimal totalSourceQty,
        IEnumerable<(DocumentType ChildType, decimal Quantity)> consumed, bool hasActiveLinkedChild,
        decimal epsilon = QtyEpsilon)
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
            return new Progress(null, hasActiveLinkedChild || furthest > epsilon
                ? ConversionProgressState.Partial : ConversionProgressState.None);

        if (furthest >= totalSourceQty - epsilon)
            return new Progress(100m, ConversionProgressState.Full);

        if (furthest > epsilon)
        {
            var pct = Math.Round(furthest / totalSourceQty * 100m, 1, MidpointRounding.AwayFromZero);
            // ยังไม่ครบ ⇒ ห้ามโชว์ 100 (ปัดขึ้นจาก 99.96)
            return new Progress(Math.Min(pct, 99.9m), ConversionProgressState.Partial);
        }
        // รอบ 250 WP-01 (เทสต์รอบ 232/242 เพิ่งรันครั้งแรก): มีใบลูกผูกหัวแต่ไม่มีแถวจับคู่ ⇒ "ไม่ทราบสัดส่วน" = null ไม่ใช่ 0 —
        // เดิมคืน 0 ⇒ ป้ายข้อความถูก (PercentText ถือ ≤ 0 เป็นไม่ทราบ) แต่ ConversionCompletionPercent ใน API = 0 ⇒ หน้ารายละเอียดขึ้น "แปลง 0%"
        // (ตัวเลขที่แต่งขึ้น · CLAUDE.md F2 ข้อ 5 undefined ≠ 0) · ไม่มีใบลูกเลย ⇒ 0 + None เดิม
        return hasActiveLinkedChild
            ? new Progress(null, ConversionProgressState.Partial)
            : new Progress(0m, ConversionProgressState.None);
    }

    /// <summary>
    /// คำตัดสินข้อ 138 (2026-10-08 · "ยอดเงินรวมสำคัญที่สุด" · "ถ้าครบพอดี ใบเสนอราคาต้องแสดงผลถูกต้อง"): วัดความคืบหน้าด้วย<b>ยอดเงิน</b>
    /// (Σ Amount ก่อน VAT ของบรรทัดที่ใบลูกยกไป เทียบ Σ Amount ของใบต้นทาง) เมื่อใบต้นทางมีราคา — แบ่งงวด 1 ชิ้นเป็นสองใบครึ่งราคา
    /// = 50% ไม่ใช่ 100% (สูตรจำนวนเดิมขึ้น "ครบ" ตั้งแต่ใบแรก) และจำนวนต่างหน่วยไม่ถูกบวกปนกัน ·
    /// ใบต้นทางที่ไม่มีราคา (เช่น ใบขอซื้อที่ไม่ใส่ราคา) ⇒ ใช้จำนวนแทน (สูตรเดิม) ไม่แต่ง %
    /// </summary>
    /// <param name="headerLinkedChildren">ใบลูกที่ยังมีผลซึ่งผูกหัวใบมาที่ใบนี้ (null = ผู้เรียกไม่ได้โหลด ⇒ ไม่ใช้กติกาข้อ 242-B) —
    /// ใช้กับ <see cref="SoleChildMatchesTotal"/> เท่านั้น</param>
    public static Progress EvaluateByValue(decimal totalSourceQty, decimal totalSourceAmount,
        IEnumerable<(DocumentType ChildType, decimal Quantity, decimal Amount)> consumed, bool hasActiveLinkedChild,
        IReadOnlyCollection<HeaderLinkedChild>? headerLinkedChildren = null)
    {
        var rows = consumed.ToList();
        if (SoleChildMatchesTotal(totalSourceAmount, rows.Count, headerLinkedChildren))
            return new Progress(100m, ConversionProgressState.Full, ByHeaderTotal: true);
        return totalSourceAmount > AmountEpsilon
            ? Evaluate(totalSourceAmount, rows.Select(r => (r.ChildType, r.Amount)), hasActiveLinkedChild, AmountEpsilon)
            : Evaluate(totalSourceQty, rows.Select(r => (r.ChildType, r.Quantity)), hasActiveLinkedChild);
    }

    /// <summary>ความคลาดเคลื่อนของยอดเงิน (ครึ่งสตางค์) — เศษปัดต่อบรรทัดของใบลูกไม่ทำให้ "ครบพอดี" กลายเป็น 99.9%</summary>
    public const decimal AmountEpsilon = 0.005m;

    /// <summary>ใบลูกที่จะเอ่ยชื่อในป้าย (ล่าสุดที่ยังมีผล)</summary>
    public readonly record struct ChildRef(string DocumentNumber, DocumentType DocumentType, DocumentStatus Status);

    /// <summary>
    /// ป้าย lifecycle ของเอกสารต้นทาง — (LifecycleStatus, LifecycleReason) ·
    /// <paramref name="activeChildCount"/> = จำนวนใบลูกที่ยังมีผลทั้งหมด (รวม <paramref name="latest"/>) ⇒ เกิน 1 ใบต่อท้าย "+N"
    /// <list type="bullet">
    /// <item>ครบ: "✓ ออกใบแจ้งหนี้ INV-001 แล้ว" (ใบลูกยังเป็นร่าง ⇒ "✓ ร่างใบแจ้งหนี้ไว้แล้ว (ยังไม่อนุมัติ)")</item>
    /// <item>บางส่วน: "◐ ออกใบแจ้งหนี้ INV-001 แล้ว 60%" · ไม่ทราบสัดส่วน ⇒ "… แล้ว · ไม่ทราบสัดส่วน (รายการของใบลูกไม่ได้จับคู่กับรายการใบนี้)"
    /// (<see cref="UnknownShareText"/> — เกิดเมื่อบรรทัดใบลูกไม่มี/ชี้ผิด <c>SourceLineId</c> · รอบ 232 ทางเข้าสดประทับให้เองผ่าน <see cref="SourceLineBackfill"/>)</item>
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
                // ข้อ 242-B: ครบตามยอดรวมทั้งใบ (รายการไม่ได้จับคู่) ⇒ บอกที่มาให้ตามต่อได้ ไม่ใช่ทำเหมือนจับคู่ครบทุกบรรทัด
                var basis = progress.ByHeaderTotal ? " · " + HeaderTotalText : "";
                return ("Done", latest.HasValue
                    ? $"✓ {Did(latest.Value)}{more}{basis}"
                    : "✓ ออกเอกสารต่อครบแล้ว" + basis);
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

    /// <summary>ข้อความ "ไม่ทราบสัดส่วน" ตัวเดียว (ป้าย · เทสต์ · doc อ้างตัวนี้)</summary>
    public const string UnknownShareText = "ไม่ทราบสัดส่วน (รายการของใบลูกไม่ได้จับคู่กับรายการใบนี้)";

    /// <summary>
    /// ฝ่ายค้านรอบ 242 N6: ใบต้นทางที่ป้ายขึ้น "ครบตามยอดรวมทั้งใบ" (<see cref="Progress.ByHeaderTotal"/>) — ด่านกันแปลงเกินอ่านรายบรรทัดจึงไม่เห็นยอดที่ออกไปแล้ว ⇒
    /// การแปลงไปแกนเดียวกับใบลูกใบนั้นต้องหยุดถาม (ต้องกดรับทราบ) · คืนข้อความเตือน หรือ null = ไม่ต้องถาม · ตัวตัดสินเดียวกับป้าย (ห้ามสำเนาเงื่อนไข) · pure
    /// </summary>
    /// <param name="latestChildType">ชนิดใบลูกที่ป้ายเอ่ยถึง (ใบเดียวที่ผูกหัวใบ)</param>
    public static string? HeaderTotalConvertWarning(Progress progress, string? sourceNumber, string? childNumber,
        DocumentType? latestChildType, DocumentType targetType)
    {
        if (!progress.ByHeaderTotal || latestChildType is not DocumentType child) return null;
        if (AxisOf(child) != AxisOf(targetType)) return null;
        return $"{sourceNumber} ออก{DocumentTypeNames.Title(child, "th")} {childNumber} ครบตามยอดรวมทั้งใบแล้ว (รายการไม่ได้จับคู่รายบรรทัด — ระบบนับคงเหลือรายบรรทัดไม่ได้) "
             + "· การแปลงอีกครั้งอาจออกซ้ำ — ตรวจยอดก่อนยืนยัน";
    }

    /// <summary>ข้อความ "ครบตามยอดรวมทั้งใบ" ตัวเดียว (ข้อ 242-B · ป้าย · เทสต์ · doc อ้างตัวนี้)</summary>
    public const string HeaderTotalText = "ครบตามยอดรวมทั้งใบ (รายการไม่ได้จับคู่รายบรรทัด)";

    private static string PercentText(decimal? pct)
    {
        // ไม่ทราบสัดส่วน = ใบลูกผูกแค่หัว (RelatedDocumentId) แต่ไม่มีบรรทัดใดชี้บรรทัดของใบนี้ — บอกสาเหตุให้คนตามต่อได้ (รอบ 232:
        // ทางเข้าสดประทับให้เอง + SourceLineBackfillJob ซ่อมข้อมูลเก่าเมื่อจับคู่แน่นอน · จับไม่ได้ = ป้ายนี้ซื่อสัตย์กว่าสัดส่วนที่แต่งขึ้น)
        if (!pct.HasValue || pct.Value <= 0m) return " · " + UnknownShareText;
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
