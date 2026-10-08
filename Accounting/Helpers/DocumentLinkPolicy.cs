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
/// </summary>
public static class DocumentLinkPolicy
{
    public const string RuleCode = "DOC-LINK";

    public static readonly DocumentType[] ChildTypes = { DocumentType.Invoice, DocumentType.TaxInvoice };
    public static readonly DocumentType[] SourceTypes = { DocumentType.Quotation };

    public readonly record struct ChildFacts(
        DocumentType Type, DocumentStatus Status, bool HasParent, bool IsDeposit,
        decimal DepositBaseDeducted, bool IsReplacement, bool AnyLineHasSource);

    /// <summary>ใบลูกนี้ผูกเข้าใบต้นทางได้ไหม — null = ได้ · ข้อความ = เหตุผล (แสดงผู้ใช้ตรง ๆ)</summary>
    public static string? ChildBlockReason(ChildFacts c)
    {
        if (Array.IndexOf(ChildTypes, c.Type) < 0)
            return "ผูกกับใบเสนอราคาได้เฉพาะใบแจ้งหนี้/ใบกำกับภาษี";
        if (c.Status is DocumentStatus.Voided or DocumentStatus.Rejected)
            return "เอกสารนี้ถูกยกเลิก/ปฏิเสธแล้ว — ผูกไม่ได้";
        if (c.HasParent || c.AnyLineHasSource)
            return "เอกสารนี้ผูกกับเอกสารต้นทางอยู่แล้ว — ยกเลิกการผูกเดิมก่อน";
        if (c.IsDeposit || c.DepositBaseDeducted > 0.005m)
            return "ใบมัดจำ/ใบที่หักมัดจำ ผูกกับใบเสนอราคาภายหลังไม่ได้ (ยอดไม่ใช่มูลค่าสินค้าตามใบเสนอราคา)";
        if (c.IsReplacement)
            return "ใบที่ออกแทนใบอื่น ผูกกับใบเสนอราคาภายหลังไม่ได้";
        return null;
    }

    /// <summary>ใบต้นทางรับการผูกได้ไหม (ฝั่งที่ต้องรู้จากฐาน: คู่ค้า/สกุลเงินตรงกัน)</summary>
    public static string? SourceBlockReason(DocumentType sourceType, DocumentStatus sourceStatus, bool sameContact, bool sameCurrency)
    {
        if (Array.IndexOf(SourceTypes, sourceType) < 0)
            return "ผูกได้เฉพาะกับใบเสนอราคา";
        if (sourceStatus is DocumentStatus.Voided or DocumentStatus.Rejected)
            return "ใบเสนอราคานี้ถูกยกเลิก/ปฏิเสธแล้ว";
        if (!sameContact)
            return "ใบเสนอราคาเป็นของคู่ค้าคนละราย — ผูกได้เฉพาะคู่ค้าเดียวกัน";
        if (!sameCurrency)
            return "สกุลเงินไม่ตรงกัน — เทียบยอดกันไม่ได้";
        return null;
    }

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
