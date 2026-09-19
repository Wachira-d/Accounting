using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **"เลขผู้เสียภาษีใบนี้เป็นของนิติบุคคลหรือบุคคลธรรมดา" — ตัวตัดสินตัวเดียวของระบบ**
///
/// ═══ ทำไมต้องมีที่เดียว (บั๊กจริง · DECISION_AUDIT_2026-09-18 §3 D8-3) ═══
/// <para>กติกาเดียวกันเคยมี **สามสำเนา** ที่ตัดสินไม่เหมือนกัน:</para>
/// <list type="bullet">
/// <item><c>ContactsV1Controller</c> — <c>taxId.Length == 13 ⇒ JuristicPerson</c>
///   ⇒ <b>ผิดทุกราย</b> เพราะเลขผู้เสียภาษีไทย**ทุกแบบ**ยาว 13 หลัก รวมเลขบัตร
///   ประชาชนของบุคคลธรรมดา ⇒ คู่ค้าบุคคลธรรมดาที่พาร์ตเนอร์ sync เข้ามากลายเป็น
///   นิติบุคคลทั้งหมด ⇒ ระบบเลือก <b>ภ.ง.ด.53 แทน ภ.ง.ด.3</b> และอัตราหัก ณ ที่จ่าย
///   ของบางประเภทเงินได้ผิด (ดอกเบี้ย ม.40(4)(ก) บุคคล 15% · นิติบุคคล 1%)</item>
/// <item><c>DocumentsV1Controller</c> — <c>Length == 13 &amp;&amp; StartsWith('0')</c>
///   (ถูกเรื่องหลักแรก แต่<b>ไม่ตรวจ checksum</b> ⇒ เลขมั่วที่ขึ้นต้น 0 ผ่าน)</item>
/// <item><c>IntegrationService.ResolveContactType</c> — <c>ThaiTaxId.IsJuristic</c>
///   (ตัวที่ถูก) — จึงยกกติกาของเส้นนี้ขึ้นมาเป็นเจ้าของ แล้วให้อีกสองเส้นเรียกตาม</item>
/// </list>
///
/// ═══ "ไม่รู้" ต้องเป็นคำตอบได้ (DECISION_DOCTRINE §1 G-series) ═══
/// <para>เลขว่าง / ไม่ครบ 13 หลัก / checksum ไม่ผ่าน = <b>ตัดสินไม่ได้</b> —
/// <see cref="Resolve"/> คืน <c>null</c> ไม่ใช่เดาเป็นบุคคลธรรมดา. ผู้เรียก
/// ที่กำลัง<b>อัปเดต</b>แถวเดิมต้องใช้ <see cref="Apply"/> ซึ่งคงค่าเดิมไว้
/// (ห้ามเขียนทับค่าที่คนตั้งใจแก้ด้วยการเดา)</para>
///
/// <para>⚠️ <c>ContactType</c> ยัง<b>ไม่มี</b>ค่า <c>Unknown</c> — เป็นคำถามที่ค้าง
/// ให้เจ้าของโปรเจกต์ตัดสิน (<c>DECISION_DOCTRINE.md</c> §4.1d) จึงยังเติม enum เองไม่ได้
/// ⇒ ที่นี่ใช้ <c>ContactType?</c> เป็นตัวแทนของ "ยังไม่รู้" แทน</para>
/// </summary>
public static class ContactTypeFromTaxId
{
    /// <summary>
    /// ประเภทผู้ติดต่อที่ **พิสูจน์ได้จากเลขผู้เสียภาษี** — <c>null</c> = ตัดสินไม่ได้
    ///
    /// <para>หลักแรก <c>0</c> = ทะเบียนนิติบุคคล · <c>1–8</c> = บุคคลธรรมดา/ต่างด้าว
    /// (ดู <see cref="ThaiTaxId.IsValid"/> ซึ่งบังคับ checksum mod-11 ของสำนักทะเบียนกลาง)</para>
    /// </summary>
    public static ContactType? Resolve(string? taxId)
    {
        if (!ThaiTaxId.IsValid(taxId)) return null;
        return ThaiTaxId.IsJuristic(taxId) ? ContactType.JuristicPerson : ContactType.Individual;
    }

    /// <summary>
    /// ค่าที่ควรเขียนลงแถว เมื่อระบบต้นทางไม่ได้ส่ง <c>contactType</c> มาเอง —
    /// ตัดสินไม่ได้ = <b>คงค่าเดิม</b> (ไม่เขียนทับ)
    /// </summary>
    public static ContactType Apply(ContactType current, string? taxId) => Resolve(taxId) ?? current;
}
