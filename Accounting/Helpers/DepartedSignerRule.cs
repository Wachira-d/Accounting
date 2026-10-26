namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียว "ผู้ลงนามที่ออกจากบริษัทแล้ว" บนเอกสารที่พิมพ์ — W9 R-61 (2)
///
/// <para>ที่มา: ช่องลายเซ็นผูกกับผู้ใช้ (ผู้สร้าง/ผู้อนุมัติ/ผู้จ่ายเงิน 50 ทวิ) · เมื่อคนนั้นถูกถอดออกจากบริษัท
/// <c>CompanyUser</c> หายไปแต่ <c>User.SignatureImageBase64</c> ยังอยู่ ⇒ เอกสารที่พิมพ์ภายหลังยังวาดลายเซ็นของคนที่ไม่อยู่แล้ว
/// (= แต่งลายเซ็นให้คนที่ออกไป · G3) · กฎตรา (<see cref="DocumentStampPolicy.Decision.SignerAllowed"/>) ปล่อยผ่านเมื่อไม่ตั้งกฎ
/// จึงต้องมีด่านนี้แยก</para>
///
/// <para>กติกา: ช่องที่เจ้าของเป็น<b>ผู้ใช้ในระบบ</b> (<paramref name="slotUser"/> มีค่า) แต่<b>ไม่มีแถวสมาชิกในบริษัทนี้</b>
/// (<paramref name="currentRole"/> = null — ตัวอ่าน <c>SignerRoleAsync</c> คืน null เมื่อไม่พบ <c>CompanyUser</c>) ⇒ ไม่วาดรูปลายเซ็น ·
/// คงชื่อไว้ (ประวัติว่าใครทำ) + หมายเหตุ <c>DocumentLabels.SigDeparted</c> ใต้ชื่อ · ทั้ง HTML และ QuestPDF อ่านธง
/// <c>DocumentSigner.Departed</c> ตัวเดียว · ช่องที่ไม่ใช่ผู้ใช้ (ลูกค้าเซ็นรับ · คู่ค้าส่งมา) ไม่เกี่ยว ·
/// ทิศตรงข้าม: สมาชิกปัจจุบัน (บทบาทใดก็ได้ รวมบัญชีที่ปิดใช้งานแต่ยังเป็นสมาชิก) วาดเหมือนเดิม</para>
/// </summary>
public static class DepartedSignerRule
{
    public const string RuleCode = "STAMP-SIGNER-DEPARTED";

    /// <summary>true = ผู้ใช้ในระบบที่ไม่อยู่ในบริษัทนี้แล้ว ⇒ ห้ามวาดลายเซ็น</summary>
    public static bool IsDeparted(Guid? slotUser, string? currentRole)
        => slotUser.HasValue && string.IsNullOrWhiteSpace(currentRole);
}
