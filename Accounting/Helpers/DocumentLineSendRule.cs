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

    internal static bool Sendable(DocumentType t) => SendableTypes.Contains(t);

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
    /// <paramref name="PayLink"/> = การ์ดมีปุ่มชำระได้ (ฝ่ายค้าน 259 M1 — หน้าชำระแสดงยอด/รายการ/ชื่อผู้ซื้อ ⇒ ตัวตัดสินเดียวกับปุ่ม PDF) ·
    /// <paramref name="ShowAmounts"/> false = กลุ่มซ่อนยอด (การ์ดไม่แสดงยอด/ไม่มีปุ่มดู PDF/ไม่มีปุ่มชำระ)</summary>
    /// <param name="PageLink">รอบ 267 P3: การ์ดของปลายทางนี้ได้ปุ่มลิงก์หน้าเอกสาร ("ดู/ยอมรับ" ฯลฯ — <see cref="PageLinkAllowed"/>) ·
    /// <paramref name="PageLinkNote"/> = เหตุเมื่อไม่ได้ (หน้าเว็บแสดงข้อความนี้ตรงตัว)</param>
    public sealed record Target(Guid EndpointId, string Kind, string KindLabel, string Display, bool Available, string? Reason,
        bool ShowAmounts, bool PdfLink, bool PayLink, bool PageLink = false, string? PageLinkNote = null);

    /// <summary>
    /// รอบ 267 P3 (คำตัดสิน 267-1-7 · 267-1-61) — <b>ตัวตัดสินเดียว</b>ว่าการ์ด LINE ของปลายทางนี้ได้ <b>ลิงก์หน้าเอกสาร</b> (token ของใบ = สิทธิ์ดู · จ่าย ·
    /// ตอบรับเมื่อผู้ขายเปิดช่วงตอบรับ) ไหม:
    /// <list type="bullet">
    /// <item>หน้าเอกสารแสดงยอด/รายการ/ชื่อผู้ซื้อ ⇒ กลุ่มที่ซ่อนยอด/ยกเลิกผูก/บล็อก = ไม่ได้ (ตัวเดิมของปุ่มดู PDF <see cref="CustomerDocPage.PageLinkAllowedForLineEndpoint"/>)</item>
    /// <item>ใบที่มีปุ่มตอบรับ (ใบเสนอราคา · ใบส่งของ — <see cref="CustomerDocPage.HasRespondAction"/>) ⇒ <b>1:1 เท่านั้น</b> · กลุ่ม = ปุ่มดูเท่านั้น
    /// (ใครในกลุ่มก็กดยอมรับได้ — ผู้ขายไม่รู้ว่าใคร)</item>
    /// </list>
    /// ผู้เรียก: หน้าต่างส่ง (<c>DocumentLineSendService.OptionsAsync</c>) · ตอนเข้าคิว (<c>SendAsync</c>) · ตอนส่งจริง (<c>ContactLineQueueService</c> ตรวจซ้ำ — ค่าตั้งของปลายทางเปลี่ยนได้หลังเข้าคิว)
    /// </summary>
    public static bool PageLinkAllowed(string? kind, bool showAmounts, DocumentType type)
        => PageLinkProblem(kind, showAmounts, type) == null;

    /// <summary>เหตุที่ปลายทางนี้ไม่ได้ลิงก์หน้าเอกสาร (null = ได้) — ข้อความถึงพนักงานบนหน้าต่าง "ส่งให้ลูกค้า"</summary>
    public static string? PageLinkProblem(string? kind, bool showAmounts, DocumentType type)
    {
        if (!NotifyEndpointKind.IsLine(kind)) return "ไม่ใช่ปลายทาง LINE";
        var show = kind != NotifyEndpointKind.LineGroup || showAmounts;
        if (!CustomerDocPage.PageLinkAllowedForLineEndpoint(kind, show, null, null))
            return "กลุ่มนี้ตั้ง \"ซ่อนยอด\" — หน้าเอกสารแสดงยอดและรายการ ⇒ การ์ดไม่มีปุ่มลิงก์ (ส่งลิงก์ทาง LINE 1:1 หรืออีเมลแทน)";
        if (kind == NotifyEndpointKind.LineGroup && CustomerDocPage.HasRespondAction(type))
            return "กลุ่ม LINE ได้ปุ่มดูเท่านั้น — ใครในกลุ่มก็กดยอมรับ/เซ็นรับได้ (ส่งให้ผู้ลงนามทาง LINE 1:1 หรืออีเมล)";
        return null;
    }

    /// <summary>ฝ่ายค้าน 259 M1: ปลายทางนี้ได้ปุ่มชำระไหม — ตัวเดียวกับปุ่มดู PDF/ปุ่มอัปโหลด 50 ทวิ (กลุ่มซ่อนยอด ⇒ ไม่ได้)</summary>
    public static bool PayLinkAllowed(string? kind, bool showAmounts)
        => LineDocumentViewLink.EndpointAllows(kind, kind != NotifyEndpointKind.LineGroup || showAmounts, null, null);

    /// <summary>ฝ่ายค้าน 259 M1: ติ๊กลิงก์ชำระแล้วเลือกกลุ่มซ่อนยอด ⇒ ปฏิเสธทั้งคำขอพร้อมเหตุ (ไม่ส่งการ์ดที่หลุดยอดผ่านหน้าชำระ · ไม่ส่งบางส่วนเงียบ)</summary>
    public static string? PayLinkProblem(bool includePayLink, IEnumerable<ContactRecipientRule.RecipientOption> chosen)
    {
        if (!includePayLink) return null;
        var hidden = chosen.Where(o => !PayLinkAllowed(o.Kind, o.ShowAmounts)).Select(o => o.Display).ToList();
        return hidden.Count == 0 ? null
            : $"ส่งปุ่มชำระเข้ากลุ่มที่ตั้ง \"ซ่อนยอด\" ไม่ได้ (หน้าชำระแสดงยอดและรายการ): {string.Join(", ", hidden)} — เอาติ๊กลิงก์ชำระออก หรือไม่เลือกกลุ่มนี้";
    }

    /// <summary>ฝ่ายค้าน 259 L5 (คำตัดสิน 259-2-8): ลิงก์ชำระของใบถูกเพิกถอน (ผู้มีสิทธิ์ยกเลิกเอกสารตั้งใจปิด) ⇒ หน้าต่างส่งเอง (LINE/อีเมล) ไม่ออกใหม่ทับ ·
    /// null = ไม่ได้ถูกเพิกถอน</summary>
    private static string? RevokedPayLinkProblem(DateTime? revokedAtUtc)
        => revokedAtUtc == null ? null : PayLinkRevokedException.StaffMessage;

    /// <summary>ฝ่ายค้าน 259 L3: ช่องติ๊กลิงก์ชำระบนหน้าต่าง — ตัดสินตอนเปิดหน้าต่างด้วยเหตุปฏิเสธตัวเดียวของตัวออกลิงก์ (ไม่ใช่ยอดค้างอย่างเดียว)</summary>
    public static (bool Offered, string? Note) PayLinkOffer(bool serviceAvailable, decimal balanceDue, string? issueRefusal, DateTime? revokedAtUtc)
    {
        if (!serviceAvailable) return (false, "บริการลิงก์ชำระเงินยังไม่พร้อม");
        if (balanceDue <= 0) return (false, "ใบนี้ไม่มียอดค้างชำระ — ไม่มีลิงก์ชำระเงิน");
        if (!string.IsNullOrWhiteSpace(issueRefusal)) return (false, issueRefusal);
        if (RevokedPayLinkProblem(revokedAtUtc) is string revoked) return (false, revoked);
        return (true, null);
    }

    /// <summary>ฝ่ายค้าน 259 L2: แถวที่ผู้ใช้ติ๊กลิงก์ชำระ แต่ตอนส่งลิงก์ใช้ไม่ได้แล้ว ⇒ ไม่ส่ง (ไม่ใช่ส่งโดยไม่มีปุ่มแล้วขึ้น "ส่งแล้ว")</summary>
    /// <summary>รอบ 267 P3: แถวที่ส่งลิงก์หน้าเอกสาร แต่ตอนส่งลิงก์ใช้ไม่ได้แล้ว (เพิกถอน/หมดอายุ/ใบที่ลูกค้าไม่เห็นแล้ว) ⇒ ไม่ส่ง</summary>
    public const string PageLinkGoneAtSend =
        "ไม่ส่ง — ลิงก์หน้าเอกสารของใบนี้ใช้ไม่ได้แล้ว (ถูกยกเลิก/หมดอายุ) · ออกลิงก์ใหม่แล้วส่งจาก \"📤 ส่งให้ลูกค้า\" อีกครั้ง";

    /// <summary>รอบ 267 P3: ค่าตั้งของปลายทางเปลี่ยนหลังเข้าคิว (เช่น กลุ่มเปลี่ยนเป็นซ่อนยอด) จนไม่ได้ลิงก์หน้าเอกสารแล้ว ⇒ ไม่ส่ง</summary>
    public const string PageLinkNotAllowedAtSend =
        "ไม่ส่ง — ปลายทางนี้รับลิงก์หน้าเอกสารไม่ได้แล้ว (ค่าตั้งเปลี่ยนหลังเข้าคิว เช่น กลุ่มตั้งซ่อนยอด) · ส่งใหม่จาก \"📤 ส่งให้ลูกค้า\"";

    public const string PayLinkGoneAtSend =
        "ไม่ส่ง — ลิงก์ชำระที่เลือกใส่ใช้ไม่ได้แล้ว (ถูกเพิกถอน/หมดอายุ/บริษัทปิดช่องทาง) · ส่งใหม่จากหน้าเอกสาร (ติ๊กหรือไม่ติ๊กลิงก์ชำระ)";

    public static IReadOnlyList<Target> Targets(IReadOnlyList<ContactRecipientRule.RecipientOption> options, DocumentType? type = null)
        => options.Where(o => o.EndpointId != null && NotifyEndpointKind.IsLine(o.Kind))
            .Select(o =>
            {
                var show = o.Kind != NotifyEndpointKind.LineGroup || o.ShowAmounts;
                var pageProblem = type is DocumentType t ? PageLinkProblem(o.Kind, o.ShowAmounts, t) : "ไม่ได้ตรวจ";
                return new Target(o.EndpointId!.Value, o.Kind, NotifyEndpointKind.Label(o.Kind), o.Display, o.Available, o.Available ? null : o.Reason,
                    show, LineDocumentViewLink.EndpointAllows(o.Kind, show, null, null), PayLinkAllowed(o.Kind, o.ShowAmounts),
                    type != null && pageProblem == null, type == null ? null : pageProblem);
            }).ToList();

    /// <summary>รอบ 267 P3 (267-1-61): ใบที่มีปุ่มตอบรับ + ส่งลิงก์หน้าเอกสาร ⇒ ปลายทาง 1:1 ที่ติ๊กเรื่อง "ลิงก์เซ็น" (แต่ไม่ได้ติ๊ก "เอกสาร") ก็รับได้ ·
    /// รวมสองชุดของตัวหาผู้รับ: ส่งได้ในชุดใดชุดหนึ่ง ⇒ ส่งได้ (เรื่องที่บันทึกบนแถวคิว = เรื่องที่ทำให้ส่งได้ — ผู้ส่งคิวตรวจซ้ำด้วยเรื่องนั้น)</summary>
    public static IReadOnlyList<(ContactRecipientRule.RecipientOption Option, NotifyTopic Topic)> MergeSignTopic(
        IReadOnlyList<ContactRecipientRule.RecipientOption> document, IReadOnlyList<ContactRecipientRule.RecipientOption>? sign)
    {
        var list = new List<(ContactRecipientRule.RecipientOption, NotifyTopic)>();
        foreach (var o in document)
        {
            var viaSign = !o.Available && o.Kind == NotifyEndpointKind.LineUser
                ? sign?.FirstOrDefault(x => x.EndpointId == o.EndpointId && x.Available) : null;
            list.Add(viaSign != null ? (viaSign, NotifyTopic.Sign) : (o, Topic));
        }
        foreach (var x in sign ?? Array.Empty<ContactRecipientRule.RecipientOption>())
            if (x.Available && x.Kind == NotifyEndpointKind.LineUser && document.All(o => o.EndpointId != x.EndpointId))
                list.Add((x, NotifyTopic.Sign));
        return list;
    }

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

    /// <summary>ฝ่ายค้าน 259 L4: ข้อความผลต่อปลายทาง (เข้าคิว / มีแล้วในนาทีนี้)</summary>
    /// <param name="PageLink">รอบ 267 P3: แถวนี้มีปุ่มลิงก์หน้าเอกสาร · <paramref name="Note"/> = เหตุที่ไม่มี (กลุ่มซ่อนยอด · กลุ่มของใบที่ตอบรับได้)</param>
    public sealed record EndpointOutcome(Guid EndpointId, string Display, bool Queued, bool PageLink = false, string? Note = null);

    /// <summary>กุญแจเหตุการณ์ — ใบ + นาที (UTC) ⇒ กดซ้ำในนาทีเดียวกันได้แถวเดิม (unique ต่อปลายทางผ่าน <see cref="ContactLineChannel.QueueKey"/>)</summary>
    public static string BaseKey(Guid documentId, DateTime nowUtc) => $"doc:{documentId}:{ContactLineChannel.ManualTrigger}:{nowUtc:yyyyMMddHHmm}";

    /// <summary>ข้อความตอบหลังเข้าคิว (ไม่ใช่ "ส่งแล้ว" — ส่งแล้ว = LINE ตอบรับจริงเท่านั้น)</summary>
    public static string QueuedMessage(int queued, int already)
        => (queued > 0 ? $"เข้าคิว LINE แล้ว {queued} ปลายทาง — ระบบส่งภายในประมาณ 1 นาที" : "")
           + (already > 0 ? (queued > 0 ? " · " : "") + $"{already} ปลายทางเพิ่งเข้าคิวไปแล้วในนาทีนี้ (ไม่ส่งซ้ำ)" : "")
           + " · ดูผลการส่งที่ ส่งเอกสารอัตโนมัติ › คิว LINE";
}
