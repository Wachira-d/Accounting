using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 257 A-H1 — <b>ด่านสิทธิ์ของการเขียนรายการประจำ (ตัวเดียว)</b> · ผู้เรียก: <c>RecurringController</c> Create · Update · Delete · Pause · Resume · RunNow
/// <para>═══ ที่มา ═══ เดิมมีแค่ <c>[Authorize]</c> ⇒ สมาชิกดูอย่างเดียวสร้างรายการ "อนุมัติอัตโนมัติ + ส่งอีเมล" แล้วกด "สร้างทันที"
/// = ใบกำกับมีเลขจริง (§86/4) ถูกอนุมัติและส่งถึงลูกค้าโดยไม่มีผู้มีสิทธิ์อนุมัติคนใดเห็น</para>
/// <para>กติกา: (1) ทุกการเขียน = สิทธิ์สร้างเอกสารชนิดนั้น (<see cref="DocumentPermissionHelper.CanCreateAsync"/> — ตัวเดียวกับหน้าเอกสาร) ·
/// (2) รายการที่ "ระบบอนุมัติ/ส่งแทนคน" (<see cref="NeedsApprove"/> — ตั้งใหม่ · เปิดค้างไว้ตอนแก้ไข · เปิดต่อ · สร้างทันที) = สิทธิ์อนุมัติเอกสารชนิดนั้นด้วย
/// (<see cref="DocumentPermissionHelper.CanApproveAsync"/>) · (3) เทมเพลตสมุดรายวัน = <c>Journal.Manage</c> (อนุมัติอัตโนมัติของสมุดรายวัน = post JE)</para>
/// <para>ไม่มีสิทธิ์ ⇒ 403 + ข้อความไทยที่บอกทางไปต่อ (ขอสิทธิ์ หรือปิด "อนุมัติอัตโนมัติ/ส่งอีเมล" แล้วบันทึกเป็นร่างให้ผู้มีสิทธิ์อนุมัติเอง)</para>
/// </summary>
public static class RecurringWriteGate
{
    private static bool IsJournal(string? templateType)
        => string.Equals(templateType, "journal", StringComparison.OrdinalIgnoreCase);

    /// <summary>ระบบจะอนุมัติ (ออกเลขจริง) หรือส่งเอกสารแทนคนไหม — ส่งอีเมล ⇒ อนุมัติเสมอ (invariant ของ service) · สมุดรายวันไม่มีการส่ง</summary>
    public static bool NeedsApprove(string? templateType, bool autoApprove, bool autoSendEmail)
        => autoApprove || (autoSendEmail && !IsJournal(templateType));

    /// <summary>ธงหลังแก้ไข (ตรรกะเดียวกับ <c>RecurringTransactionService.UpdateAsync</c>: null = คงเดิม · ส่งอีเมล ⇒ อนุมัติ · สมุดรายวันไม่ส่ง)</summary>
    public static (bool AutoApprove, bool AutoSendEmail) AfterUpdate(string? templateType, bool currentAutoApprove, bool currentAutoSendEmail,
        bool? requestedAutoApprove, bool? requestedAutoSendEmail)
    {
        var send = (requestedAutoSendEmail ?? currentAutoSendEmail) && !IsJournal(templateType);
        var approve = (requestedAutoApprove ?? currentAutoApprove) || send;
        return (approve, send);
    }

    public const string JournalMessage =
        "ตั้ง/แก้รายการประจำของสมุดรายวันได้เฉพาะผู้มีสิทธิ์จัดการใบสำคัญ (Journal.Manage) — ขอผู้ดูแลบริษัทเพิ่มสิทธิ์";

    /// <summary>ชื่อชนิดอยู่ในวงเล็บเป็นชื่อ enum (ไม่ทำตารางป้ายชื่อชนิดเอกสารสำเนาที่สอง — F2 ข้อ 4)</summary>
    private static string CreateMessage(DocumentType type)
        => $"ตั้ง/แก้รายการประจำได้เฉพาะผู้มีสิทธิ์สร้างเอกสารชนิดนี้ ({type} · Document.Create) — ขอผู้ดูแลบริษัทเพิ่มสิทธิ์";

    private static string ApproveMessage(DocumentType type)
        => $"รายการนี้ให้ระบบอนุมัติ/ส่งเอกสารแทนคน — ต้องมีสิทธิ์อนุมัติเอกสารชนิดนี้ ({type} · Document.Approve) · "
           + "ขอผู้มีสิทธิ์อนุมัติทำแทน หรือปิด \"อนุมัติอัตโนมัติ\" และ \"ส่งอีเมลอัตโนมัติ\" ให้ระบบสร้างเป็นร่างรออนุมัติ";

    /// <summary>รอบ 257 ฝ่ายค้าน L3: คีย์ API ตั้ง/คง/เปิดต่อ/สร้างทันทีรายการที่ระบบอนุมัติ/ส่งแทนคนไม่ได้ (คีย์ acc_ ถือตัวตนเจ้าของ ⇒ ด่านสิทธิ์ผ่านเสมอ)</summary>
    public const string ApiKeyMessage =
        "รายการนี้ให้ระบบอนุมัติ/ส่งเอกสารแทนคน — ตั้ง/แก้/เปิดต่อ/สร้างทันทีด้วย API key ไม่ได้ · เข้าสู่ระบบบนเว็บด้วยผู้มีสิทธิ์อนุมัติ "
        + "หรือส่งรายการโดยปิด “อนุมัติอัตโนมัติ” และ “ส่งอีเมลอัตโนมัติ” (ระบบสร้างเป็นร่างรออนุมัติ)";

    /// <summary>ผลตัดสิน · null = ผ่าน · ข้อความ = ปฏิเสธ (403) · <paramref name="needsApprove"/> มาจาก <see cref="NeedsApprove"/>/<see cref="AfterUpdate"/> ของรายการ ·
    /// <paramref name="isApiKeyRequest"/> = คำขอยืนยันตัวด้วย API key (<c>OwnerActionGuard.IsApiKeyRequest</c>)</summary>
    public static async Task<string?> DenyAsync(IPermissionService perms, Guid companyId, Guid userId,
        string? templateType, DocumentType? documentType, bool needsApprove, bool isApiKeyRequest = false)
    {
        if (needsApprove && isApiKeyRequest) return ApiKeyMessage;
        if (IsJournal(templateType))
            return await perms.HasPermissionAsync(companyId, userId, PermissionKeys.JournalManage) ? null : JournalMessage;
        var type = documentType ?? DocumentType.Invoice;   // แบบเดียวกับด่านอ่านของรายการประจำ (DenyAutopilotReadAsync)
        if (!await DocumentPermissionHelper.CanCreateAsync(perms, companyId, userId, type)) return CreateMessage(type);
        if (needsApprove && !await DocumentPermissionHelper.CanApproveAsync(perms, companyId, userId, type)) return ApproveMessage(type);
        return null;
    }
}
