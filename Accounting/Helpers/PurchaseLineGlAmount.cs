namespace Accounting.Helpers;

/// <summary>
/// **ยอดที่บรรทัดใบซื้อ (ใบปกติ ไม่อ้างใบรับสินค้า) ลงไว้บนผังของตัวเองใน GL** — สกุลเงินของเอกสาร (ยังไม่แปลงบาท)
///
/// ═══ ที่มา (รอบ 249 · ASSET_MULTI_DOCUMENT_DESIGN G10) ═══
/// สูตรนี้เคยอยู่เป็นบรรทัดเดียวใน <c>DocumentService.ResolveLinePostedGlAmountAsync</c> (private) —
/// ตัวย้ายผังบัญชีต้องย้ายเท่านี้ และ <b>ต้นทุนสินทรัพย์ที่ผูกจากหลายใบ</b> ก็ต้องเท่านี้เป๊ะ
/// (มากกว่า = ต้นทุนรวม VAT ที่เคลมไปแล้ว · น้อยกว่า = VAT ต้องห้ามค้างผังต้นทางหลังโอน) ⇒ ย้ายมาเป็นตัวตั้งเดียว
/// ให้ทั้งสองเส้นเรียก (F2 ข้อ 4)
///
/// <para>AutoPost ฝั่งซื้อลง <c>Dr = Amount</c> เมื่อเคลม VAT ได้ (VAT ไป 116xx) และ <c>Dr = Amount + VatAmount</c>
/// เมื่อเป็นภาษีซื้อต้องห้าม §82/5 (VAT รวมเป็นต้นทุน)</para>
/// </summary>
public static class PurchaseLineGlAmount
{
    /// <summary>ยอด Dr ของบรรทัด (สกุลเอกสาร) — ผู้เรียกแปลงบาทด้วย <see cref="DocumentFx.ToBaht"/></summary>
    public static decimal Standalone(decimal amount, decimal vatAmount, bool isVatClaimable)
        => isVatClaimable ? amount : amount + vatAmount;

    /// <summary>ส่วนของ <see cref="Standalone"/> ที่เป็น VAT ต้องห้าม (สกุลเอกสาร) — 0 เมื่อเคลมได้</summary>
    public static decimal NonClaimableVat(decimal vatAmount, bool isVatClaimable)
        => isVatClaimable ? 0m : vatAmount;
}
