namespace Accounting.Helpers;

/// <summary>ผู้ลงนาม/ลายเซ็นของการอนุมัติเอกสารที่คู่ค้าส่งมา (workflow ข้ามบริษัท) — ตัวตัดสินเดียว (pure) · รอบ 2026-10-14 ทีม F4 (ID#10)
///
/// <para>═══ ที่มา ═══ <c>ApproveIncomingAsync</c> รับ <c>SignatureUserId</c> อะไรก็ได้ และตั้งค่า <c>DefaultApproverUserId</c>/<c>DefaultSignatureId</c>
/// ได้โดยไม่ตรวจว่าเป็นสมาชิกของบริษัทผู้อนุมัติ ⇒ ประทับลายเซ็นของใครก็ได้ (รวมผู้ใช้ของบริษัทอื่น) ลงเอกสารของคู่ค้า · และผู้อนุมัติเริ่มต้น
/// ที่ลาออกไปแล้วยัง "อนุมัติอัตโนมัติ + ลงนาม" ต่อในนามบริษัท</para>
///
/// <para>กติกา: ผู้ลงนามที่ระบุมา ต้องเป็นสมาชิก (ไม่ใช่ ⇒ ปฏิเสธพร้อมทางไปต่อ) · ไม่ระบุ ⇒ ผู้อนุมัติเริ่มต้นถ้ายังเป็นสมาชิก ·
/// ผู้อนุมัติเริ่มต้นออกไปแล้ว ⇒ ลงนามในชื่อผู้กดอนุมัติแทน + คำเตือนให้ตั้งผู้อนุมัติเริ่มต้นใหม่ (ไม่ลงนามในชื่อคนที่ออกไปแล้ว) ·
/// การอนุมัติอัตโนมัติไม่มี "ผู้กด" ⇒ ผู้อนุมัติเริ่มต้นออกไปแล้ว = ไม่อนุมัติอัตโนมัติ (รอคนอนุมัติในกล่องเอกสารเข้า)</para></summary>
public static class CrossTenantSignerResolver
{
    public const string RuleCode = "XTENANT-SIGNER";

    /// <summary>ผล: ผู้ลงนาม · ข้อความปฏิเสธ (ไม่ null = ห้ามอนุมัติ) · คำเตือน (อนุมัติได้แต่ต้องแจ้ง)</summary>
    public sealed record Resolution(Guid SignerUserId, string? Problem, string? Notice);

    /// <summary>ผู้เรียกระบุผู้ลงนามมาเองไหม (null/Guid ว่าง = ไม่ระบุ ⇒ ใช้ผู้อนุมัติเริ่มต้น/ผู้กด) — ตัวเดียวของ service และ controller</summary>
    public static bool IsSignerRequested(Guid? requestedSignerUserId) => requestedSignerUserId is { } r && r != Guid.Empty;

    public static Resolution Resolve(Guid? requestedSignerUserId, Guid? defaultApproverUserId, Guid actingUserId,
        IReadOnlySet<Guid> activeMemberIds)
    {
        if (IsSignerRequested(requestedSignerUserId))
        {
            var requested = requestedSignerUserId!.Value;
            if (requested == actingUserId) return new Resolution(actingUserId, null, null);
            if (!activeMemberIds.Contains(requested))
                return new Resolution(actingUserId,
                    "ผู้ลงนามที่เลือกไม่ได้เป็นสมาชิกของบริษัทนี้ (หรือถูกปิดบัญชีแล้ว) — เลือกผู้ลงนามใหม่จากรายชื่อสมาชิก หรือเว้นว่างเพื่อลงนามในชื่อคุณเอง",
                    null);
            // ฝ่ายค้าน F4: สมาชิกคนอื่น ≠ ยินยอมให้ประทับลายเซ็นของตน — ลงนามแทนได้เฉพาะผู้อนุมัติเริ่มต้นที่บริษัทตั้งไว้ (ผ่านด่านสิทธิ์ตั้งค่าบริษัท)
            if (requested != defaultApproverUserId)
                return new Resolution(actingUserId,
                    "ลงนามได้เฉพาะในชื่อคุณเอง หรือผู้อนุมัติเริ่มต้นที่บริษัทตั้งไว้ — ถ้าต้องการลายเซ็นของคนอื่น ให้คนนั้นเข้ามากดอนุมัติเอง",
                    null);
            return new Resolution(requested, null, null);
        }
        if (defaultApproverUserId is { } def && def != Guid.Empty)
            return activeMemberIds.Contains(def)
                ? new Resolution(def, null, null)
                : new Resolution(actingUserId, null, DepartedDefaultApproverNotice);
        return new Resolution(actingUserId, null, null);
    }

    /// <summary>ลายเซ็นเริ่มต้นใช้ได้เฉพาะเมื่อเป็นของผู้ลงนามคนนั้นเอง (ลายเซ็นผูกกับผู้ใช้ ไม่ใช่บริษัท)</summary>
    public static bool SignatureBelongsTo(Guid? signatureOwnerUserId, Guid signerUserId)
        => signatureOwnerUserId is { } owner && owner == signerUserId;

    /// <summary>ผู้อนุมัติเริ่มต้นไม่ได้เป็นสมาชิกแล้ว — ใช้ทั้งหน้าตั้งค่า · หมายเหตุบนเอกสารเข้าที่ไม่ได้อนุมัติอัตโนมัติ · คำตอบการอนุมัติด้วยมือ</summary>
    public const string DepartedDefaultApproverNotice =
        "ผู้อนุมัติเริ่มต้นของ workflow ข้ามบริษัทไม่ได้เป็นสมาชิกบริษัทแล้ว — ระบบไม่อนุมัติ/ลงนามอัตโนมัติในนามคนนั้น · "
        + "ตั้งผู้อนุมัติเริ่มต้นคนใหม่ที่การตั้งค่า workflow อัตโนมัติ (workflow-automation) · ระหว่างนี้เอกสารที่คู่ค้าส่งมารออนุมัติด้วยมือในกล่องเอกสารเข้า";

    /// <summary>ตั้งค่า: ผู้อนุมัติเริ่มต้น/ลายเซ็นเริ่มต้นที่ส่งมา — null = บันทึกได้</summary>
    public static string? ConfigProblem(Guid? newDefaultApproverUserId, bool approverIsMember,
        Guid? newDefaultSignatureId, bool signatureFound, Guid? signatureOwnerUserId, Guid? effectiveApproverUserId)
    {
        if (newDefaultApproverUserId is { } a && a != Guid.Empty && !approverIsMember)
            return "ผู้อนุมัติเริ่มต้นต้องเป็นสมาชิกของบริษัทนี้ที่ยังใช้งานอยู่ — เลือกจากรายชื่อสมาชิก";
        if (newDefaultSignatureId is { } s && s != Guid.Empty)
        {
            if (!signatureFound) return "ไม่พบลายเซ็นที่เลือก (อาจถูกลบหรือปิดใช้งาน) — เลือกลายเซ็นใหม่";
            if (effectiveApproverUserId is not { } approver || !SignatureBelongsTo(signatureOwnerUserId, approver))
                return "ลายเซ็นเริ่มต้นต้องเป็นลายเซ็นของผู้อนุมัติเริ่มต้นเอง — เลือกผู้อนุมัติเริ่มต้นก่อน แล้วเลือกลายเซ็นของคนนั้น";
        }
        return null;
    }
}
