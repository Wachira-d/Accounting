using System.Text.Json;
using Accounting.Models.DTOs.Notification;

namespace Accounting.Services.Interfaces;

/// <summary>ผลของ event กลุ่ม — <paramref name="Reply"/> ข้อความตอบ (null = ไม่ตอบ) · <paramref name="LeaveAfterReply"/> = บอทออกจากกลุ่มหลังตอบ (Q8)</summary>
public sealed record LineGroupEventOutcome(string? Reply, bool LeaveAfterReply)
{
    public static LineGroupEventOutcome Nothing { get; } = new(null, false);
}

/// <summary>รอบ 254 WP-11 L3 — กลุ่ม LINE ของผู้ติดต่อผ่าน <b>LINE OA ของบริษัท</b> (LINE_MULTICHANNEL_DESIGN §3.c · Q1(b)/Q2/Q5/Q8):
/// webhook (join · #HeyNextAcc · #ByeNextAcc · leave · สมาชิกเข้า/ออก) + ผู้ดูแลบริษัทผูกด้วยรหัส (preview/confirm) + บอทออกเมื่อไม่เหลือการผูก</summary>
public interface IContactLineGroupService
{
    /// <summary>webhook OA บริษัท: event ที่มาจากกลุ่ม/ห้อง — คืนข้อความตอบ (reply · ฟรี) + ต้องออกจากกลุ่มหลังตอบไหม · ข้อความอื่นในกลุ่มไม่ประมวลและไม่เก็บ</summary>
    Task<LineGroupEventOutcome> HandleCompanyGroupEventAsync(Guid companyId, string? destination, JsonElement ev, CancellationToken ct = default);

    /// <summary>หลังตอบ #ByeNextAcc: ไม่เหลือการผูกของกลุ่มนี้กับบริษัทนี้ ⇒ บอทออกจากกลุ่ม (Q8)</summary>
    Task LeaveGroupIfUnboundAsync(Guid companyId, string? destination, string groupId, CancellationToken ct = default);

    /// <summary>รอบ 256 (คำตัดสิน 256-2): webhook <b>OA กลาง</b> — event กลุ่ม/ห้องที่ต้องทำอะไร (<c>LineGroupBindRule.CentralGroupEventActionable</c>) ·
    /// #ByeNextAcc/leave ปลดปลายทาง nextacc ของกลุ่มทุกบริษัท (PDPA ม.22) · ออกรหัสเฉพาะบริษัทผู้ให้บริการ · ไม่ได้ตั้ง ⇒ ตอบ CentralOaNotSupported เฉพาะคำสั่ง</summary>
    Task<LineGroupEventOutcome> HandleCentralGroupEventAsync(string? destination, JsonElement ev, CancellationToken ct = default);

    /// <summary>รอบ 256-2: หลังตอบ #ByeNextAcc บน OA กลาง — ไม่เหลือการผูกของบริษัทผู้ให้บริการ ⇒ บอทกลางออก (Q8 · ไม่ออกจากกลุ่มกลางของแอดมิน)</summary>
    Task LeaveCentralGroupIfUnboundAsync(string? destination, string groupId, CancellationToken ct = default);

    /// <summary>กันประมวล webhook ซ้ำ — true = ครั้งแรก (ประมวลได้) · ไม่มี id = true</summary>
    Task<bool> MarkEventSeenAsync(string? webhookEventId, CancellationToken ct = default);

    /// <summary>ฝ่ายค้าน fix2 L1: ถอนการประทับเมื่อประมวลผลล้ม (LINE ส่งซ้ำแล้วประมวลได้อีก)</summary>
    Task UnmarkEventSeenAsync(string? webhookEventId, CancellationToken ct = default);

    /// <summary>ผู้ดูแลบริษัทตรวจรหัสก่อนผูก (ผู้เรียกตรวจ Contact.Edit แล้ว)</summary>
    Task<ContactLineGroupPreview> PreviewAsync(Guid companyId, Guid contactId, string? code, Guid? actorUserId, CancellationToken ct = default);

    /// <summary>ผูกกลุ่มกับผู้ติดต่อ — รหัสใช้ครั้งเดียว · ต้องรับรองสิทธิ์ของสมาชิก · บอทประกาศในกลุ่ม</summary>
    Task<ContactNotifyOverviewResponse> ConfirmAsync(Guid companyId, Guid contactId, ConfirmContactLineGroupRequest request, ContactNotifyActor actor, CancellationToken ct = default);

    /// <summary>หลังยกเลิกปลายทางกลุ่ม: ไม่เหลือการผูกของกลุ่มนี้ (แฮช) กับบริษัทนี้ ⇒ บอทออกจากกลุ่ม (Q8) · <paramref name="protectedGroupId"/> = ค่าเข้ารหัสก่อนถูกล้าง ·
    /// รอบ 256-2: <paramref name="oaScope"/> = OA ที่ปลายทางผูก (nextacc ⇒ บอทกลางออกเมื่อไม่เหลือการผูกในบริษัทใด · เฉพาะบริษัทผู้ให้บริการ) ·
    /// คืนประโยคถึงผู้ใช้เมื่อบอทยังอยู่ในกลุ่มและผู้ใช้ต้องเอาออกเอง (null = ไม่มีอะไรต้องบอก)</summary>
    Task<string?> LeaveIfUnboundAsync(Guid companyId, string? protectedGroupId, string? lineIdHash, CancellationToken ct = default, string? oaScope = null);
}
