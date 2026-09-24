namespace Accounting.Helpers;

/// <summary>สิ่งที่การดึงอัตรา ธปท. ต้องทำกับแถวอัตราของวันนั้น</summary>
public enum CurrencyRateSyncAction
{
    /// <summary>ยังไม่มีแถวของวันนั้น → เพิ่ม</summary>
    Insert = 0,
    /// <summary>มีแถวแต่อัตรากลาง ≤ 0 (ค้างจากบั๊กฟอร์ม A03 ก่อนรอบ 193) → เขียนทับด้วยอัตรา ธปท.</summary>
    ReplaceUnusableRow = 1,
    /// <summary>มีแถวที่ใช้ได้อยู่แล้ว (ผู้ใช้ตั้งเอง/ดึงไปแล้ว) → ไม่แตะ</summary>
    KeepExisting = 2,
    /// <summary>อัตราที่ ธปท. ส่งมาเองใช้ไม่ได้ (≤ 0) → ไม่เขียนอะไร (ห้ามสร้างแถว 0 ใหม่)</summary>
    SkipInvalidIncoming = 3,
}

/// <summary>
/// ตัวตัดสินเดียวของการ sync อัตราแลกเปลี่ยน ธปท. ต่อแถว (รอบ 193 · ฝ่ายค้าน M2)
///
/// <para><b>ที่มา</b>: ทีม F (A03) ทำให้ตัวอ่านทุกตัวใน <c>CurrencyService</c> กรอง <c>MidRate &gt; 0</c> ⇒ แถว 0 ที่ค้าง
/// = "ไม่มีอัตรา" · แต่ <c>BotExchangeRateService.SyncRatesToCompanyAsync</c> ข้ามวันที่<b>มีแถวอยู่แล้ว</b>โดยไม่ดูค่า ⇒
/// วันที่มีแถว 0 ค้าง ธปท. ไม่มีวันเติมให้ และตัวอ่านถอยไปใช้อัตรา<b>วันก่อนหน้า</b>เงียบ ๆ (แปลงเงิน/ตีราคา
/// สิ้นงวดด้วยอัตราผิดวัน) · กติกา "ใช้ได้" ต้องตรงกับตัวอ่าน: อัตรากลาง &gt; 0</para>
/// </summary>
public static class CurrencyRateSync
{
    /// <summary>อัตรากลางที่ "ใช้ได้" — ตัวเดียวกับเงื่อนไขกรองของตัวอ่าน (<c>MidRate &gt; 0</c>)</summary>
    public static bool IsUsable(decimal midRate) => midRate > 0m;

    public static CurrencyRateSyncAction Decide(decimal? existingMidRate, decimal incomingMidRate)
    {
        if (!IsUsable(incomingMidRate)) return CurrencyRateSyncAction.SkipInvalidIncoming;
        if (existingMidRate is not decimal existing) return CurrencyRateSyncAction.Insert;
        return IsUsable(existing) ? CurrencyRateSyncAction.KeepExisting : CurrencyRateSyncAction.ReplaceUnusableRow;
    }
}
