using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ด่าน "ใบแจ้งหนี้ที่มีภาษีมูลค่าเพิ่ม รับชำระบางส่วนไม่ได้ — แปลงเป็นใบกำกับภาษีก่อน" ฝั่งเซิร์ฟเวอร์ (ตัวเดียว)** — ฝ่ายค้านรอบ 258 M5
/// <para>ที่มา: จุดความรับผิด VAT ของบริการ (ม.78/1) เกิดเต็มจำนวนตั้งแต่เงินงวดแรก — ระบบย้ายภาษีขายทั้งก้อน 21913 → 21911 ตอนรับเงินครั้งแรก
/// แต่ใบเสร็จของการรับเงินบางส่วนถือ VAT เต็มของใบไม่ได้ ⇒ ภาษีเข้า ภ.พ.30 โดยไม่มีใบกำกับ (ม.86) · เดิมด่านนี้อยู่ใน JS ของหน้ารับชำระอย่างเดียว
/// (<c>documents.html</c> "รับชำระบางส่วนของใบที่มี VAT") ⇒ การจัดสรรจากเงินรอจัดสรร/การผูกใบอื่นในแผงเงินเกิน/REST ตรง เดินผ่านไปได้</para>
/// <para>ผู้เรียก (ล็อกใน <c>tools/required_call_site_check.py</c>): <c>CreatePaymentAsync</c> (ทางเข้าที่คนบันทึกเอง) · <c>CreateMultiDocPaymentAsync</c> ·
/// <c>CustomerCreditAllocation.Check</c> · ทางเข้าอัตโนมัติ (gateway/integration/นำเข้าไฟล์/ธนาคาร) คงพฤติกรรมเดิม — ยอดรับจริงจากภายนอกปฏิเสธไม่ได้</para>
/// </summary>
public static class VatInvoiceSettlementGate
{
    public const string RuleCode = "RD-78/1-PARTIAL-VAT-INVOICE";
    public const string LegalReference = "ป.รัษฎากร ม.78/1 · ม.86";

    /// <summary>เหตุปฏิเสธ (null = รับได้) — ใบแจ้งหนี้ (ไม่ใช่ใบกำกับ) ที่มี VAT และยอดที่ปิดครั้งนี้น้อยกว่ายอดค้าง</summary>
    /// <param name="settledNow">ยอดหนี้ที่การรับชำระ/การจัดสรรครั้งนี้ปิด (เงิน + ค่าธรรมเนียม + บรรทัดปรับ)</param>
    public static string? PartialRefusal(DocumentType type, decimal vatAmount, decimal settledNow, decimal balanceDue, string documentNumber)
    {
        if (type != DocumentType.Invoice || vatAmount <= 0.005m) return null;
        if (settledNow + DocumentSettlementState.PaymentOvershootAllowance >= balanceDue) return null;
        return $"ใบแจ้งหนี้ {documentNumber} มีภาษีมูลค่าเพิ่ม — รับชำระ/จัดสรรบางส่วน ({settledNow.ToString("#,##0.00", CultureInfo.InvariantCulture)} จากยอดค้าง "
            + $"{balanceDue.ToString("#,##0.00", CultureInfo.InvariantCulture)}) ไม่ได้ เพราะภาษีขายถึงกำหนดเต็มจำนวนตั้งแต่เงินงวดแรก (มาตรา 78/1) แต่ใบเสร็จบางส่วนถือภาษีเต็มของใบไม่ได้ · "
            + "แปลงใบแจ้งหนี้เป็นใบกำกับภาษีก่อน แล้วรับชำระ/จัดสรรที่ใบกำกับ หรือปิดเต็มยอดค้างในครั้งเดียว";
    }
}
