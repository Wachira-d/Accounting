using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 259 แพ็ก B (E-H3 · คำตัดสิน 259-2-2) — <b>ส่งเอกสารถึงลูกค้าเองจากหน้าเอกสาร: ชนิดที่ส่งได้ + ส่งทาง LINE</b> (pure · ตัวเดียว)
/// <list type="bullet">
/// <item><see cref="SendableTypes"/> = ชนิดที่ปุ่ม "📧 ส่งอีเมล" / "💬 ส่งทาง LINE" แสดง — เดิมเป็นรายการใน documents.html (สำเนากติกาในหน้า · F2 ข้อ 5) ⇒ เซิร์ฟเวอร์ส่ง</item>
/// <item>ปลายทาง = ตัวหาผู้รับเดียว (<c>IContactRecipientResolver</c> เรื่อง "เอกสาร") — ยกเลิก/บล็อก/ถอนยินยอม/OA ไม่พร้อม/ไม่ได้ติ๊กเรื่องนี้ ⇒ แสดงพร้อมเหตุแต่เลือกไม่ได้ ·
/// ซ่อนยอดของกลุ่ม + ปุ่มดู PDF ตามตัวตัดสินเดียว (<see cref="LineDocumentViewLink.EndpointAllows"/>)</item>
/// <item>ส่งผ่านคิว LINE ตัวเดิม (<c>ContactLineQueueService</c> · trigger <see cref="ContactLineChannel.ManualTrigger"/>) — "ส่งแล้ว" = LINE ตอบรับจริงเท่านั้น ·
/// กดซ้ำในนาทีเดียวกัน = แถวเดิม (กันกดสองครั้ง)</item>
/// </list>
/// </summary>
public static class DocumentLineSendRule
{
    public const string RuleCode = "DOC-LINE-SEND";

    /// <summary>เรื่องของการส่งเอง (ปลายทางที่ไม่ได้ติ๊ก "เอกสาร" ไม่ได้รับ — ค่าของผู้รับชนะ)</summary>
    public const NotifyTopic Topic = NotifyTopic.Document;

    /// <summary>ชนิดเอกสารที่ส่งถึงคู่ค้าเองได้ (ฝั่งขาย + ใบสั่งซื้อถึงผู้ขาย)</summary>
    public static readonly IReadOnlyList<DocumentType> SendableTypes = new[]
    {
        DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.Quotation,
        DocumentType.BillingNote, DocumentType.CreditNote, DocumentType.DebitNote, DocumentType.DeliveryNote, DocumentType.PurchaseOrder,
    };

    public static bool Sendable(DocumentType t) => SendableTypes.Contains(t);

    /// <summary>ใบนี้ส่งทาง LINE ได้ไหม (ก่อนดูปลายทาง) — null = ได้ · ข้อความ = เหตุถึงผู้ใช้</summary>
    public static string? DocumentProblem(bool found, DocumentType type, DocumentStatus status)
    {
        if (!found) return "ไม่พบเอกสาร";
        if (!Sendable(type)) return "เอกสารชนิดนี้ส่งถึงคู่ค้าทาง LINE ไม่ได้";
        if (status == DocumentStatus.Voided) return "เอกสารถูกยกเลิกแล้ว — ส่งทาง LINE ไม่ได้";
        if (!DocumentStatusRules.IsIssued(status))
            return "อนุมัติเอกสารก่อนส่งทาง LINE — ระบบออกเลขเอกสารจริงตอนอนุมัติ (เลข DRAFT ห้ามส่งให้ลูกค้า §86/4)";
        return null;
    }

    /// <summary>หนึ่งปลายทาง LINE ของผู้ติดต่อบนหน้าต่าง "ส่งทาง LINE" — <paramref name="PdfLink"/> = การ์ดมีปุ่มดู PDF ·
    /// <paramref name="ShowAmounts"/> false = กลุ่มซ่อนยอด (การ์ดไม่แสดงยอด/ไม่มีปุ่มดู PDF)</summary>
    public sealed record Target(Guid EndpointId, string Kind, string KindLabel, string Display, bool Available, string? Reason,
        bool ShowAmounts, bool PdfLink);

    public static IReadOnlyList<Target> Targets(IReadOnlyList<ContactRecipientRule.RecipientOption> options)
        => options.Where(o => o.EndpointId != null && NotifyEndpointKind.IsLine(o.Kind))
            .Select(o =>
            {
                var show = o.Kind != NotifyEndpointKind.LineGroup || o.ShowAmounts;
                return new Target(o.EndpointId!.Value, o.Kind, NotifyEndpointKind.Label(o.Kind), o.Display, o.Available, o.Available ? null : o.Reason,
                    show, LineDocumentViewLink.EndpointAllows(o.Kind, show, null, null));
            }).ToList();

    /// <summary>ปลายทางที่ผู้ใช้เลือก ⇒ ตัวเลือกที่ส่งได้ · เลือกว่าง / ไม่ใช่ LINE ของผู้ติดต่อนี้ / ใช้ไม่ได้ ⇒ เหตุ (ไม่ส่งบางส่วนเงียบ ๆ — ทั้งคำขอถูกปฏิเสธ)</summary>
    public static (IReadOnlyList<ContactRecipientRule.RecipientOption> Chosen, string? Problem) Pick(
        IReadOnlyList<ContactRecipientRule.RecipientOption> options, IReadOnlyCollection<Guid>? requested)
    {
        var ids = (requested ?? Array.Empty<Guid>()).Where(g => g != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return (Array.Empty<ContactRecipientRule.RecipientOption>(), "เลือกปลายทาง LINE อย่างน้อย 1 ปลายทาง");
        var chosen = new List<ContactRecipientRule.RecipientOption>();
        foreach (var id in ids)
        {
            var o = options.FirstOrDefault(x => x.EndpointId == id && NotifyEndpointKind.IsLine(x.Kind));
            if (o == null) return (Array.Empty<ContactRecipientRule.RecipientOption>(), "ปลายทางที่เลือกไม่ใช่ LINE ของผู้ติดต่อของเอกสารนี้ (หรือถูกลบแล้ว) — เปิดหน้าต่างใหม่");
            if (!o.Available)
                return (Array.Empty<ContactRecipientRule.RecipientOption>(), $"ส่งถึง {o.Display} ไม่ได้ — {o.Reason ?? "ปลายทางนี้ใช้ไม่ได้"}");
            chosen.Add(o);
        }
        return (chosen, null);
    }

    /// <summary>กุญแจเหตุการณ์ — ใบ + นาที (UTC) ⇒ กดซ้ำในนาทีเดียวกันได้แถวเดิม (unique ต่อปลายทางผ่าน <see cref="ContactLineChannel.QueueKey"/>)</summary>
    public static string BaseKey(Guid documentId, DateTime nowUtc) => $"doc:{documentId}:{ContactLineChannel.ManualTrigger}:{nowUtc:yyyyMMddHHmm}";

    /// <summary>ข้อความตอบหลังเข้าคิว (ไม่ใช่ "ส่งแล้ว" — ส่งแล้ว = LINE ตอบรับจริงเท่านั้น)</summary>
    public static string QueuedMessage(int queued, int already)
        => (queued > 0 ? $"เข้าคิว LINE แล้ว {queued} ปลายทาง — ระบบส่งภายในประมาณ 1 นาที" : "")
           + (already > 0 ? (queued > 0 ? " · " : "") + $"{already} ปลายทางเพิ่งเข้าคิวไปแล้วในนาทีนี้ (ไม่ส่งซ้ำ)" : "")
           + " · ดูผลการส่งที่ ส่งเอกสารอัตโนมัติ › คิว LINE";
}
