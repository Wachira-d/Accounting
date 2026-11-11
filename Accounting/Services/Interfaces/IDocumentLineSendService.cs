using Accounting.Helpers;

namespace Accounting.Services.Interfaces;

/// <summary>รอบ 259 (E-H3): หน้าต่าง "ส่งทาง LINE" ของเอกสาร — <paramref name="CanSend"/> false ⇒ <paramref name="Problem"/> (เหตุ + ทางไปต่อ) ·
/// <paramref name="Targets"/> = ปลายทาง LINE ทุกตัวของผู้ติดต่อ (ใช้ไม่ได้ = มีเหตุ · เลือกไม่ได้) · <paramref name="PayLinkOffered"/> = แสดงช่องติ๊กลิงก์ชำระ</summary>
public sealed record DocumentLineSendOptions(bool CanSend, string? Problem, IReadOnlyList<DocumentLineSendRule.Target> Targets,
    bool PayLinkOffered, string? PayLinkNote);

/// <summary>คำขอส่ง — ปลายทางที่เลือก + ใส่ลิงก์ชำระไหม</summary>
public sealed record DocumentLineSendRequest(List<Guid>? EndpointIds, bool IncludePayLink = false);

/// <summary>ผล — จำนวนแถวที่เข้าคิว / ที่เข้าคิวแล้วในนาทีนี้ (ไม่ส่งซ้ำ) · ข้อความถึงผู้ใช้ (ไม่ใช่ "ส่งแล้ว")</summary>
public sealed record DocumentLineSendResult(int Queued, int AlreadyQueued, string Message);

public interface IDocumentLineSendService
{
    Task<DocumentLineSendOptions> OptionsAsync(Guid companyId, Guid documentId, CancellationToken ct = default);

    /// <summary>เข้าคิว LINE ถึงปลายทางที่เลือก — ใบไม่พร้อม/ปลายทางใช้ไม่ได้/ลิงก์ชำระออกไม่ได้ ⇒ <see cref="BusinessRuleException"/> (ไม่ส่งบางส่วน)</summary>
    Task<DocumentLineSendResult> SendAsync(Guid companyId, Guid documentId, IReadOnlyCollection<Guid>? endpointIds, bool includePayLink,
        Guid? userId, string? actor, CancellationToken ct = default);
}
