namespace Accounting.Helpers;

/// <summary>
/// **อัตราภาษีขายที่บริษัทนี้มีสิทธิ์เรียกเก็บ — ตัวตัดสินตัวเดียว**
///
/// ═══ ทำไมต้องมีที่เดียว ═══
/// เส้นที่สร้างเอกสารขาย <b>เองโดยไม่ผ่าน <c>ApproveDocumentAsync</c></b>
/// (POS · Time Billing · integration) ต้องตัดสินอัตราเองทุกเส้น — เขียนกฎซ้ำ
/// ทุกที่เมื่อไรก็ drift เมื่อนั้น และผลของ drift คือ<b>ตัวเลขภาษีบนเอกสารจริง</b>:
/// <list type="bullet">
/// <item>เก็บ VAT ทั้งที่ยังไม่จดทะเบียน = ผิด §90/2 (โทษอาญา)</item>
/// <item>ไม่เก็บทั้งที่จดแล้ว = ภ.พ.30 ขาด แล้วต้องออกใบเพิ่มหนี้ตามทีหลัง
///   (§86/9) ซึ่งลูกค้าอาจไม่ยอมจ่ายส่วนต่าง</item>
/// </list>
///
/// <para>═══ ที่มา (บั๊กจริง · ผลตรวจ H-A2) ═══ <c>TimeBillingService</c> ตั้ง
/// <c>VatAmount = 0, VatRate = 0</c> <b>ตายตัว</b> ไม่เคยอ่าน
/// <c>IsVatRegistered</c> เลย ⇒ ใบแจ้งหนี้ค่าบริการของบริษัทที่จด VAT
/// ไม่คิด VAT 7% ทุกใบ</para>
/// </summary>
public static class OutputVatRate
{
    /// <summary>อัตราที่ใช้ได้จริง — ไม่จดทะเบียน = 0% เสมอ (§90/2)</summary>
    public static decimal ForCompany(bool isVatRegistered, decimal companyVatRate)
        => isVatRegistered && companyVatRate > 0m ? companyVatRate : 0m;

    /// <summary>ภาษีขายของยอดฐาน (ยอดเป็น net ก่อน VAT ตาม convention ของเรพ)</summary>
    public static decimal VatOn(decimal netAmount, decimal vatRatePercent)
        => vatRatePercent <= 0m
            ? 0m
            : Math.Round(netAmount * vatRatePercent / 100m, 2, MidpointRounding.AwayFromZero);
}
