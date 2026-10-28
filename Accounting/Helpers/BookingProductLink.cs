namespace Accounting.Helpers;

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "บริการจองหน้าเว็บที่ผูกสินค้า/บริการในระบบ ส่งอะไรเข้าเอกสาร" (รอบ 207 · คำขอเจ้าของข้อ 1)
/// <para>ที่มา: <c>SiteBookingService.ProductId</c> มีมาแต่ (1) หน้า CMS ไม่มีช่องเลือก (2) เส้นสร้างเอกสารไม่ Include สินค้า ⇒ รหัส/บัญชีรายได้/
/// อัตรา VAT ของสินค้าไม่เคยถึงเอกสาร · POS ดึงคิวใช้ <c>ProductId</c> ได้ถูกอยู่แล้ว (PosService.Bookings)</para>
/// <para>อัตรา VAT: บริษัทไม่จด VAT ⇒ 0 เสมอ (§90/2) · สินค้าตั้ง 0% (§80/1) หรือยกเว้น (§81) ⇒ ใช้ของสินค้า · อัตราปกติ ⇒ อัตราของบริษัท
/// (อัตราปกติตัวเดียวของบริษัท — สินค้าที่ตั้ง 7 ค้างไว้ไม่ทำให้อัตราเพี้ยน) · ไม่ผูกสินค้า ⇒ อัตราของบริษัท (เดิม)</para>
/// </summary>
public static class BookingProductLink
{
    public const string RuleCode = "CMS-BOOKING-PRODUCT";

    public static decimal VatRate(bool companyVatRegistered, decimal companyRate, decimal? productRate)
    {
        if (!companyVatRegistered) return 0m;
        if (productRate is decimal p && (p == ThaiVatTypeRule.ExemptRate || p == 0m)) return p;
        return companyRate;
    }

    /// <summary>คำเตือนบนรายการบริการ (server ประกอบ — หน้าเว็บแสดงตรง) · null = ไม่มีอะไรต้องเตือน</summary>
    public static string? Warning(bool linked, bool productActive, decimal servicePrice, decimal? productPrice)
    {
        if (!linked) return "ยังไม่ผูกสินค้า/บริการในระบบ — เอกสารและ POS จะไม่มีรหัสสินค้า/บัญชีรายได้ของบริการนี้";
        if (!productActive) return "สินค้า/บริการที่ผูกไว้ถูกลบหรือปิดใช้ — เลือกใหม่";
        if (productPrice is decimal pp && pp > 0m && Math.Abs(pp - servicePrice) > 0.005m)
            return $"ราคาบนเว็บ {servicePrice:N2} ไม่ตรงราคาสินค้า {pp:N2} — การจองใช้ราคาบนเว็บ";
        return null;
    }

    /// <summary>รหัสสินค้าใหม่ตอนสร้างจากบริการ — SVC-0001 ต่อจากเลขที่มีอยู่ (ตัวเลขล้วนท้ายรหัส)</summary>
    public static string NextCode(IEnumerable<string> existingCodes, string prefix = "SVC-")
    {
        var max = 0;
        foreach (var c in existingCodes)
            if (c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(c[prefix.Length..], out var n) && n > max) max = n;
        return $"{prefix}{max + 1:0000}";
    }
}
