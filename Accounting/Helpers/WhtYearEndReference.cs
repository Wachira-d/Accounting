namespace Accounting.Helpers;

/// <summary>
/// อ้างอิงของ JE "ปิดปีภาษีถูกหัก ณ ที่จ่าย" — **ฟังก์ชันเดียว** ที่ทั้งฝั่งเขียน
/// (<c>WhtCreditService.SettleYearEndAsync</c>) และฝั่งอ่าน (การกระทบยอด 11910) ใช้
///
/// <para>กฎ CLAUDE.md: "hash/signature มี canonical function เดียว ใช้ร่วมทั้งฝั่งเขียน
/// และฝั่ง verify — ห้ามเขียน format string สองที่" ใช้กับสตริงที่เป็น **ตัวคัดกรอง**
/// แบบนี้เท่ากัน (เขียน <c>$"WHT-{year}"</c> ที่เดียว แล้วอีกที่เทียบด้วยสตริงที่
/// พิมพ์เองซ้ำ = วันหนึ่งจะไม่ตรงกันโดยไม่มีอะไรฟ้อง)</para>
/// </summary>
public static class WhtYearEndReference
{
    /// <summary>อ้างอิงของ JE ปิดปี — ต้องไม่ชนกับ <c>WHT-{เลขที่ใบ 50 ทวิ}</c>
    /// ซึ่งมีรูป <c>WHT-yyyyMM-nnnn</c> (ยาวกว่าและมีขีดที่สอง)</summary>
    public static string For(int taxYear) => $"WHT-{taxYear}";
}
