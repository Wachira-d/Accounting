namespace Accounting.Helpers;

/// <summary>
/// **50 ทวิ ของใบต่างสกุลที่ออกก่อนทีม F — ยอดเป็นสกุลเอกสารหรือบาท** (pure · ฝ่ายค้านทีม F ข้อ 3)
///
/// <para>ที่มา: ก่อนแก้ <c>WithholdingTaxCertService.GenerateFromDocument</c> เขียนเงินได้/ภาษีของใบต่างสกุลเป็นตัวเลขสกุลเอกสาร (ใบ USD@36 ภาษี 30 แทน 1,080) ·
/// 50 ทวิ ที่ออกแล้ว<b>ห้ามแก้ย้อนหลังเงียบ ๆ</b> (ส่งผู้รับ/ยื่นแล้ว) ⇒ รายงานตรวจข้อมูลแสดงให้ผู้ทำบัญชียกเลิก/ออกใหม่รายใบ</para>
///
/// <para>ตัดสินจากภาษีบนใบเทียบ "ภาษีสกุลเอกสารที่ใบนี้ควรครอบ" (<paramref name="expectedDocCurrencyTax"/> — ยอดงวดจ่ายถ้ามี ไม่งั้นทั้งใบ) — สองสมมติฐาน:
/// สกุลเอกสาร ≈ expected · บาท ≈ expected × อัตรา ⇒ เลือกข้างที่ <b>อัตราส่วน</b>ใกล้กว่า (log) ⇔ เทียบกับ expected × √อัตรา ·
/// ทนต่อใบที่ครอบแค่บางส่วน (หลายผู้ถูกหัก) · อัตราใกล้ 1 (0.8–1.25) แยกไม่ออก ⇒ ไม่ฟ้อง (ไม่เดา)</para>
/// </summary>
public static class WhtCertCurrencyCheck
{
    public const string Kind = "ForeignCurrencyWhtCertNotInBaht";

    /// <summary>ยอดภาษีบน 50 ทวิ น่าจะเป็นสกุลเอกสาร (ไม่ใช่บาท) ไหม</summary>
    public static bool LooksDocumentCurrency(decimal certTax, decimal expectedDocCurrencyTax, decimal exchangeRate)
    {
        if (certTax <= 0m || expectedDocCurrencyTax <= 0m || exchangeRate <= 0m) return false;
        if (exchangeRate is >= 0.8m and <= 1.25m) return false;
        var pivot = expectedDocCurrencyTax * (decimal)Math.Sqrt((double)exchangeRate);
        return exchangeRate > 1m ? certTax < pivot : certTax > pivot;
    }
}
