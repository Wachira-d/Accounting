using System.Linq.Expressions;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>สิ่งที่การดึงอัตรา ธปท. ต้องทำกับแถวอัตราของวันนั้น</summary>
public enum CurrencyRateSyncAction
{
    /// <summary>ยังไม่มีแถวของวันนั้น → เพิ่ม</summary>
    Insert = 0,
    /// <summary>มีแถวแต่ใช้ไม่ได้ (อัตรากลาง ≤ 0 และไม่มีราคาซื้อ/ขายครบ — ค้างจากบั๊กฟอร์ม A03) → เขียนทับด้วยอัตรา ธปท.</summary>
    ReplaceUnusableRow = 1,
    /// <summary>มีแถวที่ใช้ได้อยู่แล้ว (ผู้ใช้ตั้งเอง/ดึงไปแล้ว) → ไม่แตะ</summary>
    KeepExisting = 2,
    /// <summary>อัตราที่ ธปท. ส่งมาเองใช้ไม่ได้ (≤ 0) → ไม่เขียนอะไร (ห้ามสร้างแถว 0 ใหม่)</summary>
    SkipInvalidIncoming = 3,
}

/// <summary>
/// ตัวตัดสินเดียวของการ sync อัตราแลกเปลี่ยน ธปท. ต่อแถว + <b>นิยาม "แถวอัตราที่ใช้ได้" ตัวเดียว</b> (รอบ 193 · M2)
///
/// <para><b>ที่มา</b>: ทีม F (A03) ทำให้ตัวอ่านใน <c>CurrencyService</c> กรอง <c>MidRate &gt; 0</c> ⇒ แถว 0 ที่ค้าง
/// = "ไม่มีอัตรา" · แต่ <c>BotExchangeRateService.SyncRatesToCompanyAsync</c> ข้ามวันที่<b>มีแถวอยู่แล้ว</b>โดยไม่ดูค่า ⇒
/// วันที่มีแถว 0 ค้าง ธปท. ไม่มีวันเติมให้ และตัวอ่านถอยไปใช้อัตรา<b>วันก่อนหน้า</b>เงียบ ๆ</para>
///
/// <para><b>หลังฝ่ายค้าน (P2)</b>: คอมมิตแรกมีเกณฑ์ "ใช้ได้" สองชุด — sync ใช้ <c>Mid &gt; 0</c> ขณะที่ตัวแนะนำอัตรา
/// (<c>AiSuggestionController</c>) นับ <c>Buy &gt; 0 &amp;&amp; Sell &gt; 0</c> ด้วย ⇒ sync เขียนทับแถวที่ผู้ใช้กรอกราคาซื้อ/ขาย
/// ไว้ (Mid = 0) จนราคาซื้อ/ขายของผู้ใช้หายเงียบ · ตอนนี้ทั้งสองจุดใช้ <see cref="IsUsableRow"/> ตัวเดียว</para>
///
/// <para>⚠️ ตัวอ่านของ <c>CurrencyService</c> (ทีม F) ยังกรอง <c>MidRate &gt; 0</c> อย่างเดียว — แถวที่มีแต่ราคาซื้อ/ขาย
/// ถูกมองว่า "ไม่มีอัตรา" ที่นั่น (ทิศปลอดภัย: ถอยไปวันก่อน/ฟ้อง ไม่ใช่คูณด้วย 0) · ยุบให้ใช้ตัวนี้ = คำถามเจ้าของ</para>
/// </summary>
public static class CurrencyRateSync
{
    /// <summary>แถวอัตราที่ "ใช้ได้" — อัตรากลาง &gt; 0 หรือมีราคาซื้อและขาย &gt; 0 ครบ (ใช้ใน query EF ได้ตรง ๆ)</summary>
    public static readonly Expression<Func<CurrencyRate, bool>> IsUsableRow =
        r => r.MidRate > 0m || (r.BuyRate > 0m && r.SellRate > 0m);

    private static readonly Func<CurrencyRate, bool> IsUsableRowCompiled = IsUsableRow.Compile();

    /// <summary>นิยามเดียวกับ <see cref="IsUsableRow"/> สำหรับค่าที่อยู่ในหน่วยความจำ</summary>
    public static bool IsUsable(decimal midRate, decimal buyRate, decimal sellRate)
        => IsUsableRowCompiled(new CurrencyRate { MidRate = midRate, BuyRate = buyRate, SellRate = sellRate });

    /// <param name="existing">แถวของวันนั้น (null = ยังไม่มี) — ค่า Mid/Buy/Sell</param>
    /// <param name="incomingMidRate">อัตรากลางจาก ธปท.</param>
    public static CurrencyRateSyncAction Decide(
        (decimal Mid, decimal Buy, decimal Sell)? existing, decimal incomingMidRate)
    {
        if (incomingMidRate <= 0m) return CurrencyRateSyncAction.SkipInvalidIncoming;
        if (existing is not { } row) return CurrencyRateSyncAction.Insert;
        return IsUsable(row.Mid, row.Buy, row.Sell)
            ? CurrencyRateSyncAction.KeepExisting
            : CurrencyRateSyncAction.ReplaceUnusableRow;
    }
}
