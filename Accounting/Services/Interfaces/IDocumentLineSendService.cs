using Accounting.Helpers;

namespace Accounting.Services.Interfaces;

/// <summary>รอบ 259 (E-H3): หน้าต่าง "ส่งทาง LINE" ของเอกสาร — <paramref name="CanSend"/> false ⇒ <paramref name="Problem"/> (เหตุ + ทางไปต่อ) ·
/// <paramref name="Targets"/> = ปลายทาง LINE ทุกตัวของผู้ติดต่อ (ใช้ไม่ได้ = มีเหตุ · เลือกไม่ได้) · <paramref name="PayLinkOffered"/> = แสดงช่องติ๊กลิงก์ชำระ</summary>
public sealed record DocumentLineSendOptions(bool CanSend, string? Problem, IReadOnlyList<DocumentLineSendRule.Target> Targets,
    bool PayLinkOffered, string? PayLinkNote);

/// <summary>คำขอส่ง — ปลายทางที่เลือก + ใส่ลิงก์ชำระไหม</summary>
public sealed record DocumentLineSendRequest(List<Guid>? EndpointIds, bool IncludePayLink = false);

/// <summary>ผล — จำนวนแถวที่เข้าคิว / ที่เข้าคิวแล้วในนาทีนี้ (ไม่ส่งซ้ำ) · ข้อความถึงผู้ใช้ (ไม่ใช่ "ส่งแล้ว")</summary>
/// <param name="Endpoints">ฝ่ายค้าน 259 L4: ผลต่อปลายทาง (เข้าคิว / มีแล้วในนาทีนี้)</param>
public sealed record DocumentLineSendResult(int Queued, int AlreadyQueued, string Message,
    IReadOnlyList<DocumentLineSendRule.EndpointOutcome>? Endpoints = null);

public interface IDocumentLineSendService
{
    /// <param name="forShare">รอบ 267 P3: หน้าต่าง "ส่งให้ลูกค้า" (ลิงก์หน้าเอกสาร) — ใบที่มีปุ่มตอบรับรวมปลายทาง 1:1 ที่ติ๊ก "ลิงก์เซ็น" ด้วย
    /// (<see cref="DocumentLineSendRule.MergeSignTopic"/>)</param>
    Task<DocumentLineSendOptions> OptionsAsync(Guid companyId, Guid documentId, CancellationToken ct = default, bool forShare = false);

    /// <summary>เข้าคิว LINE ถึงปลายทางที่เลือก — ใบไม่พร้อม/ปลายทางใช้ไม่ได้/ลิงก์ชำระออกไม่ได้ ⇒ <see cref="BusinessRuleException"/> (ไม่ส่งบางส่วน)</summary>
    /// <param name="includePageLink">รอบ 267 P3 (267-1-61): การ์ดมีปุ่มลิงก์หน้าเอกสาร (ตัดสิน<b>ต่อปลายทาง</b>ด้วย <see cref="DocumentLineSendRule.PageLinkAllowed"/> —
    /// ปลายทางที่ไม่ได้ ⇒ การ์ดดูอย่างเดียว/ไม่มีปุ่ม พร้อมเหตุในผลต่อปลายทาง) · ลิงก์ออกผ่าน <c>EnsureCustomerLinkAsync</c> ตัวเดียว (เพิกถอนไว้ ⇒ 409)</param>
    /// <param name="fromShare">ฝ่ายค้าน 267-P3 MED-2: หน้าต่าง "ส่งให้ลูกค้า" — ปุ่มชำระตัดสินต่อปลายทาง (กลุ่มซ่อนยอด = ไม่ใส่ + เหตุ · ลิงก์ชำระออกไม่ได้ =
    /// การ์ดไม่มีปุ่มชำระ + เหตุ) แทนการปฏิเสธช่องทาง LINE ทั้งช่อง · เส้นเดิม (<c>line-send</c>) = false</param>
    Task<DocumentLineSendResult> SendAsync(Guid companyId, Guid documentId, IReadOnlyCollection<Guid>? endpointIds, bool includePayLink,
        Guid? userId, string? actor, CancellationToken ct = default, bool includePageLink = false, bool fromShare = false);
}
