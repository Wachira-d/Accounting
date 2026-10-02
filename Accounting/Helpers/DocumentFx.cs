namespace Accounting.Helpers;

/// <summary>
/// <b>แปลงยอดสกุลเอกสาร → บาท ตัวเดียวกับที่ JE ใช้</b> (รอบ 203 ทีม F3 · PP36_REVIEW E-4)
/// <para>JE ของเอกสาร (<c>AutoPostToJournalAsync</c> · <c>Conv</c>) แปลงทุกยอดด้วย <c>Document.ExchangeRate</c> ปัด 2 ตำแหน่ง AwayFromZero ·
/// เส้นภาษีที่อ่านยอดจากเอกสารตรง ๆ (ยอดค้าง/รายงาน/รับรู้ ภ.พ.36) เคยใช้ยอดสกุลเอกสารดิบ ⇒ ใบ USD ที่อัตรา 36 นำส่ง "70" แทน 2,520 บาท ·
/// <c>DocumentService.ToGlAmount</c> เรียกตัวนี้ (สูตรเดียว ห้ามเขียนซ้ำ)</para>
/// </summary>
public static class DocumentFx
{
    /// <summary>ยอดเป็นบาท · อัตรา ≤ 0 (ข้อมูลเก่า) ถือเป็น 1</summary>
    public static decimal ToBaht(decimal docAmount, decimal exchangeRate)
    {
        var fx = exchangeRate <= 0m ? 1m : exchangeRate;
        return fx == 1m ? docAmount : Math.Round(docAmount * fx, 2, MidpointRounding.AwayFromZero);
    }
}
