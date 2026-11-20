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

    /// <summary>รอบ 275 แพ็ก C (คำตัดสิน 275-C-1 · 4.1) — ด่านของ<b>บันทึกรับชำระใบเดียวด้วยมือ</b>: ใบแจ้งหนี้<b>งานบริการ</b> (ภาษีขายพัก 21913 จนรับเงิน) รับเป็นงวดได้แล้ว
    /// เพราะใบเสร็จทุกงวดเป็นใบกำกับ ณ วันรับเงิน VAT ตามส่วน (<see cref="SettlementTaxInvoiceRole"/>) · ขายสินค้า/ไม่รู้ว่าพัก ⇒ ด่านเดิม <see cref="PartialRefusal"/> ·
    /// ทางเข้าอื่น (จัดสรรหลายใบ · เงินรอจัดสรร · ย้ายการรับชำระ) ยังใช้ด่านเดิม (ไม่ออกใบกำกับรายงวดจากการจัดสรร — รับงวดที่หน้ารับชำระของใบ)</summary>
    public static string? PartialRefusalForSingleDocument(DocumentType type, decimal vatAmount, decimal settledNow, decimal balanceDue, string documentNumber,
        bool vatParkedUntilPayment)
        => type == DocumentType.Invoice && vatParkedUntilPayment ? null : PartialRefusal(type, vatAmount, settledNow, balanceDue, documentNumber);

    /// <summary>ด่านหลังตัดสิน (เส้นมือ · ใบเดียว): รับบางส่วนของใบแจ้งหนี้ที่มี VAT แล้วตัวตัดสินไม่ออกใบกำกับ ณ วันรับเงิน (ใบลดหนี้ก่อนรับ ·
    /// การตัดยอดที่ไล่ไม่ได้) ⇒ ปฏิเสธพร้อมเหตุ + ทางไปต่อ (ไม่ปล่อยการขายที่รับเงินแล้วไม่มีใบกำกับเพิ่มจากเส้นที่คนบันทึกเอง) · null = ผ่าน</summary>
    public static string? InstallmentWithoutTaxInvoiceRefusal(DocumentType type, decimal vatAmount, decimal balanceDueAfter,
        SettlementTaxInvoiceRole.Decision decision, string documentNumber)
    {
        if (type != DocumentType.Invoice || vatAmount <= 0.005m || balanceDueAfter <= 0.005m || decision.CarriesRole) return null;
        if (decision.Reason is SettlementTaxInvoiceRole.Why.FullVatAlreadyIssued or SettlementTaxInvoiceRole.Why.SupersededByTaxInvoice) return null;
        return $"ใบแจ้งหนี้ {documentNumber} มีภาษีมูลค่าเพิ่ม — รับชำระบางส่วนครั้งนี้ระบบออกใบกำกับภาษี ณ วันรับเงินให้ไม่ได้ · "
            + (SettlementTaxInvoiceRole.ReasonText(decision.Reason) ?? "")
            + " · ทางไปต่อ: แปลงใบแจ้งหนี้เป็นใบกำกับภาษีก่อน แล้วรับชำระที่ใบกำกับ หรือปิดเต็มยอดค้างในครั้งเดียว";
    }
}
