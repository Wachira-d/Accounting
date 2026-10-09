using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียวของ "ผูกเอกสารที่มีอยู่แล้วเข้ากับใบต้นทาง" (คำตัดสินข้อ 139 · 2026-10-08)
///
/// <para>ที่มา (เจ้าของ): สร้างใบแจ้งหนี้ใบที่ 2 จากการแปลงไม่ได้ ผู้ใช้จึงสร้างใบแจ้งหนี้แยกเอง — ต้องการ "เอาใบนี้ไปผูกกับใบเสนอราคา
/// ได้ผลเหมือนแปลงมา ถ้าครบพอดีใบเสนอราคาแสดงผลถูกต้อง"</para>
///
/// <para>ขอบเขตรุ่นแรก (ปลอดภัย): ใบลูก = ใบแจ้งหนี้/ใบกำกับภาษี · ใบต้นทาง = ใบเสนอราคา (ไม่มี JE/สต็อก) · การผูกเขียนเฉพาะ
/// <c>RelatedDocumentId</c> + <c>SourceLineId</c> (ไม่พิมพ์บนใบแจ้งหนี้/ใบกำกับ — §86/4 เนื้อกระดาษไม่เปลี่ยน) · ห้ามผูกใบรับเงิน
/// (หลายรายงานตีความ "ไม่มีต้นทาง" = ขายสด) · ห้ามผูกใบกำกับเข้าใบแจ้งหนี้ (กลไกแทนที่ทำงานตอนอนุมัติ)</para>
///
/// <para>รุ่นสอง (คำตัดสินเจ้าของ 2026-10-08 "Add DN + GRN"):
/// <list type="bullet">
/// <item>ใบแจ้งหนี้/ใบกำกับ → <b>ใบส่งของ</b>: ใบส่งของไม่ขยับสต็อก/ไม่ลง JE (ใบแจ้งหนี้เป็นผู้ตัดสต็อกเสมอ) ⇒ ผูกภายหลังเปลี่ยนแค่ความคืบหน้า
/// เหมือนใบเสนอราคา · ใบที่อนุมัติแล้วผูกได้</item>
/// <item>ใบแจ้งหนี้ซื้อ → <b>ใบรับสินค้า</b>: <b>เปลี่ยนการลงบัญชีตอนอนุมัติ</b> (ล้าง 21240 แทน Dr สินค้า + ไม่รับสต็อกซ้ำ) ⇒ ผูกได้เฉพาะ
/// <b>ใบร่าง</b> และใบรับสินค้าต้องอนุมัติแล้ว (ลง GR-NI แล้ว — ไม่งั้นใบแจ้งหนี้ซื้อจะลงแบบรับของเองแล้วใบรับสินค้าลงซ้ำ) ·
/// เป็นทางซ่อมของใบร่างจากสแกน/API ที่ผูกใบสั่งซื้อตรง (ด่าน <c>PI-PO-HAS-GRN</c>): ย้ายจากใบสั่งซื้อไปใบรับสินค้าของใบสั่งซื้อเดียวกันได้</item>
/// </list></para>
/// </summary>
public static class DocumentLinkPolicy
{
    public const string RuleCode = "DOC-LINK";

    public static readonly DocumentType[] ChildTypes = { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.PurchaseInvoice };

    /// <summary>ชนิดใบต้นทางที่ใบลูกชนิดนี้ผูกได้ (ว่าง = ผูกไม่ได้)</summary>
    public static DocumentType[] SourceTypesFor(DocumentType childType) => childType switch
    {
        DocumentType.Invoice or DocumentType.TaxInvoice => new[] { DocumentType.Quotation, DocumentType.DeliveryNote },
        DocumentType.PurchaseInvoice => new[] { DocumentType.GoodsReceiptNote },
        _ => Array.Empty<DocumentType>(),
    };

    /// <summary>ชนิดนี้เป็นใบต้นทางที่ผูกได้สักทางไหม (ใช้บอกทางไปต่อในข้อความ)</summary>
    public static bool IsLinkSource(DocumentType type) =>
        ChildTypes.Any(c => Array.IndexOf(SourceTypesFor(c), type) >= 0);

    /// <summary>ชื่อใบต้นทางสำหรับข้อความ/ปุ่ม</summary>
    public static string SourceLabel(DocumentType childType) => childType == DocumentType.PurchaseInvoice
        ? "ใบรับสินค้า" : "ใบเสนอราคา/ใบส่งของ";

    /// <param name="ParentIsPurchaseOrder">ใบแจ้งหนี้ซื้อที่ผูกใบสั่งซื้ออยู่ (ทางซ่อม: ย้ายไปใบรับสินค้าของใบสั่งซื้อเดียวกัน)</param>
    public readonly record struct ChildFacts(
        DocumentType Type, DocumentStatus Status, bool HasParent, bool IsDeposit,
        decimal DepositBaseDeducted, bool IsReplacement, bool AnyLineHasSource, bool ParentIsPurchaseOrder = false);

    /// <summary>ใบลูกนี้ผูกเข้าใบต้นทางได้ไหม — null = ได้ · ข้อความ = เหตุผล (แสดงผู้ใช้ตรง ๆ)</summary>
    public static string? ChildBlockReason(ChildFacts c)
    {
        if (Array.IndexOf(ChildTypes, c.Type) < 0)
            return "ผูกกับเอกสารต้นทางภายหลังได้เฉพาะใบแจ้งหนี้/ใบกำกับภาษี (กับใบเสนอราคา/ใบส่งของ) และใบแจ้งหนี้ซื้อฉบับร่าง (กับใบรับสินค้า)";
        if (c.Status is DocumentStatus.Voided or DocumentStatus.Rejected)
            return "เอกสารนี้ถูกยกเลิก/ปฏิเสธแล้ว — ผูกไม่ได้";
        var isPurchase = c.Type == DocumentType.PurchaseInvoice;
        // การผูกใบรับสินค้าเปลี่ยนการลงบัญชีตอนอนุมัติ ⇒ ใบที่ลงบัญชี/รับสต็อกไปแล้วผูกภายหลังไม่ล้าง 21240 (ได้แต่ความคืบหน้าที่โกหก)
        if (isPurchase && c.Status != DocumentStatus.Draft)
            return "ใบแจ้งหนี้ซื้อที่ส่งอนุมัติ/อนุมัติแล้วลงสต็อกและบัญชีไปแล้ว — ผูกใบรับสินค้าภายหลังไม่แก้รายการบัญชี · "
                   + "ยกเลิกใบนี้แล้วออกใหม่จากใบรับสินค้า (แปลงเป็นใบแจ้งหนี้ซื้อ)";
        // ใบแจ้งหนี้ซื้อที่ผูกใบสั่งซื้ออยู่ = ทางซ่อม (ย้ายไปใบรับสินค้าของใบสั่งซื้อเดียวกัน) · ต้นทางอื่นต้องยกเลิกการผูกเดิมก่อน
        var relinkFromPo = isPurchase && c.HasParent && c.ParentIsPurchaseOrder;
        if ((c.HasParent || c.AnyLineHasSource) && !relinkFromPo)
            return "เอกสารนี้ผูกกับเอกสารต้นทางอยู่แล้ว — ยกเลิกการผูกเดิมก่อน";
        if (c.IsDeposit || c.DepositBaseDeducted > 0.005m)
            return "ใบมัดจำ/ใบที่หักมัดจำ ผูกกับเอกสารต้นทางภายหลังไม่ได้ (ยอดไม่ใช่มูลค่าสินค้าตามต้นทาง)";
        if (c.IsReplacement)
            return "ใบที่ออกแทนใบอื่น ผูกกับเอกสารต้นทางภายหลังไม่ได้";
        return null;
    }

    /// <summary>ใบต้นทางรับการผูกได้ไหม (ฝั่งที่ต้องรู้จากฐาน: คู่ค้า/สกุลเงินตรงกัน)</summary>
    /// <param name="childPoMismatch">ใบลูกผูกใบสั่งซื้ออยู่ แต่ใบรับสินค้านี้ไม่ใช่ของใบสั่งซื้อนั้น</param>
    public static string? SourceBlockReason(DocumentType childType, DocumentType sourceType, DocumentStatus sourceStatus,
        bool sameContact, bool sameCurrency, bool childPoMismatch = false)
    {
        if (Array.IndexOf(SourceTypesFor(childType), sourceType) < 0)
            return childType == DocumentType.PurchaseInvoice
                ? "ใบแจ้งหนี้ซื้อผูกภายหลังได้เฉพาะกับใบรับสินค้า"
                : "ใบแจ้งหนี้/ใบกำกับผูกภายหลังได้เฉพาะกับใบเสนอราคาหรือใบส่งของ";
        if (sourceStatus is DocumentStatus.Voided or DocumentStatus.Rejected)
            return "เอกสารต้นทางนี้ถูกยกเลิก/ปฏิเสธแล้ว";
        // ใบรับสินค้าต้องลง GR-NI แล้ว — ใบแจ้งหนี้ซื้อล้าง 21240 เฉพาะเมื่อใบรับสินค้ามี JE (GetReceivedViaGrnAccrualAccountAsync)
        if (sourceType == DocumentType.GoodsReceiptNote && sourceStatus is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
            return "ใบรับสินค้านี้ยังไม่อนุมัติ (ยังไม่รับสต็อก/ตั้ง 21240) — อนุมัติใบรับสินค้าก่อนแล้วจึงผูก";
        if (!sameContact)
            return "เอกสารต้นทางเป็นของคู่ค้าคนละราย — ผูกได้เฉพาะคู่ค้าเดียวกัน";
        if (!sameCurrency)
            return "สกุลเงินไม่ตรงกัน — เทียบยอดกันไม่ได้";
        if (childPoMismatch)
            return "ใบแจ้งหนี้ซื้อนี้ผูกใบสั่งซื้ออยู่ — ย้ายได้เฉพาะไปใบรับสินค้าของใบสั่งซื้อเดียวกัน";
        return null;
    }

    /// <summary>ยกเลิกการผูกภายหลังได้ไหม — ใบแจ้งหนี้ซื้อที่ไม่ใช่ร่างแล้ว ลงบัญชีโดยอาศัยการผูก (ล้าง 21240) ⇒ ถอดไม่ได้</summary>
    public static string? UnlinkBlockReason(DocumentType childType, DocumentStatus childStatus) =>
        childType == DocumentType.PurchaseInvoice && childStatus != DocumentStatus.Draft
            ? "ใบแจ้งหนี้ซื้อนี้ลงบัญชีโดยล้าง 21240 ของใบรับสินค้าแล้ว — ยกเลิกการผูกไม่ได้ (ยกเลิกทั้งใบแทน)"
            : null;

    public readonly record struct LineKey(Guid Id, int LineOrder, string? ProductCode, string? Description);

    /// <summary>
    /// เสนอการจับคู่บรรทัด (ผู้ใช้แก้ได้): รหัสสินค้าตรงกัน → คำอธิบายตรงกัน (ไม่สนช่องว่าง/ตัวพิมพ์) → ลำดับบรรทัดเมื่อจำนวนบรรทัดเท่ากัน ·
    /// จับไม่ได้ = null (ไม่นับในความคืบหน้าแต่ไม่กันการผูก) · บรรทัดลูกหลายบรรทัดจับบรรทัดต้นทางเดียวกันได้ (แบ่งงวด)
    /// </summary>
    public static List<(Guid ChildLineId, Guid? SourceLineId)> SuggestLineMap(
        IReadOnlyList<LineKey> childLines, IReadOnlyList<LineKey> sourceLines)
    {
        static string Norm(string? s) => string.Join(' ', (s ?? "").Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var byCode = sourceLines.Where(s => !string.IsNullOrWhiteSpace(s.ProductCode))
            .GroupBy(s => s.ProductCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var byDesc = sourceLines.Where(s => Norm(s.Description).Length > 0)
            .GroupBy(s => Norm(s.Description)).ToDictionary(g => g.Key, g => g.First().Id);
        var sameCount = childLines.Count == sourceLines.Count;
        var srcByOrder = sourceLines.OrderBy(s => s.LineOrder).ToList();
        var childByOrder = childLines.OrderBy(c => c.LineOrder).ToList();

        var result = new List<(Guid, Guid?)>();
        for (var i = 0; i < childByOrder.Count; i++)
        {
            var c = childByOrder[i];
            Guid? match = null;
            if (!string.IsNullOrWhiteSpace(c.ProductCode) && byCode.TryGetValue(c.ProductCode.Trim(), out var byC)) match = byC;
            else if (byDesc.TryGetValue(Norm(c.Description), out var byD)) match = byD;
            else if (sameCount) match = srcByOrder[i].Id;
            result.Add((c.Id, match));
        }
        return result;
    }
}
