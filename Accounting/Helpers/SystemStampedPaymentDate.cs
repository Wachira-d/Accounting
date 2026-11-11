namespace Accounting.Helpers;

/// <summary>
/// **"วันที่รับชำระ" ที่ระบบประทับให้ใบ (ตอนจัดสรรจากเงินรอจัดสรร) ไม่ใช่ค่าที่ผู้ใช้กรอก — ห้ามไหลต่อ** (ฝ่ายค้านรอบ 258 M3)
/// <para><c>Document.PaymentDate</c> ที่ผู้ใช้กรอกสืบทอดไปเอกสารลูกตอนแปลง (กฎ #4 A) และเป็นตัวตั้งของ <c>TaxPointResolver</c> (MIN) ตอนอนุมัติ ·
/// ค่าที่<b>ระบบ</b>ประทับจากการจัดสรร (<c>PaymentAllocation.StampedDocumentPaymentDate</c>) ถ้าไหลไปใบลูก ⇒ ใบกำกับที่แปลงทีหลังได้จุดความรับผิดย้อนเข้างวดของวันรับเงิน
/// โดยไม่ผ่านด่านงวดที่ยื่นแล้ว ⇒ ใบลูก<b>ไม่สืบทอด</b>ค่าที่ระบบประทับ (ผู้ใช้กรอกเองบนใบลูกได้ — เดินด่านอนุมัติเดิม)</para>
/// </summary>
public static class SystemStampedPaymentDate
{
    /// <summary>ค่าที่ใบลูกสืบทอด — ค่าที่ระบบประทับ = null (ไม่สืบทอด) · ค่าที่ผู้ใช้กรอก = ตามเดิม</summary>
    public static DateTime? InheritToChild(DateTime? sourcePaymentDate, bool stampedBySystem)
        => stampedBySystem ? null : sourcePaymentDate;
}
