namespace Accounting.Helpers;

/// <summary>
/// **คำเรียกสินค้า (alias) ที่เรียนไว้กับผู้ขาย ใช้ได้กับแถวไหนบ้าง** (pure · รอบ 200 ทีม K · K-3b จากฝ่ายค้าน K-3 รอบ 197)
///
/// <para>═══ ที่มา ═══ รอบ 197 เส้น OCR เริ่มผูกผู้ติดต่อ<b>ของสาขา</b> (ใบ Makro สาขาชลบุรี 00005 ⇒ แถวใหม่แยกจากสำนักงานใหญ่) — ถูกตาม
/// §86/4 · แต่ <c>ProductAlias.ContactId</c> / <c>ProductNegativeAlias.ContactId</c> / ประวัติการซื้อ ถูกผูก<b>รายแถว</b> ⇒ คำเรียกสินค้า
/// "น้ำดื่ม 600มล. x12" ที่ผู้ใช้สอนไว้กับแถว สนญ. 50 ครั้ง ไม่ช่วยใบของสาขาเลย (สินค้าชิ้นเดิม ผู้ขายนิติบุคคลเดิม) · เกณฑ์ยอมรับอัตโนมัติ
/// ที่ลดตามจำนวน alias ก็กลับไปเริ่มที่ 0.85 · คำแนะนำ "นำเข้าสต๊อก" หายเพราะแถวใหม่ "ไม่เคยมีประวัติ"</para>
///
/// <para>กติกา: ฝั่ง<b>อ่าน</b>ขยายเป็นทุกแถวของนิติบุคคลเดียวกัน — ขอบเขตตัวเดียวของระบบ
/// <see cref="ContactTaxBranchKey.SameEntityIdsAsync"/> (เลขภาษีที่ใช้ได้จริงเท่านั้น · walk-in/เลขศูนย์ล้วน = เฉพาะตัวเอง) · ฝั่ง<b>เขียน</b>คงผูก
/// แถวของใบนั้น (รู้ว่าเรียนจากสาขาไหน · อ่านได้ทุกสาขาอยู่แล้ว) · alias ไม่ผูกผู้ขาย (<c>ContactId = null</c>) คงเป็นชั้น global ตามเดิม</para>
/// </summary>
public static class OcrVendorAliasScope
{
    /// <summary>ชุดแถวผู้ติดต่อที่นับเป็น "ผู้ขายรายนี้" — ไม่มีผู้ขาย = ว่าง · ตัวเองอยู่ในชุดเสมอ (แม้ผู้เรียกส่งชุดที่ไม่มีตัวเองมา)</summary>
    public static IReadOnlyList<Guid> VendorIds(Guid? vendorContactId, IEnumerable<Guid>? sameEntityIds)
    {
        if (vendorContactId is not Guid self) return Array.Empty<Guid>();
        var ids = new List<Guid> { self };
        if (sameEntityIds != null)
            foreach (var id in sameEntityIds)
                if (!ids.Contains(id)) ids.Add(id);
        return ids;
    }

    /// <summary>alias/คำปฏิเสธแถวนี้เป็นของผู้ขายรายนี้ไหม (แถวใดแถวหนึ่งของนิติบุคคลเดียวกัน) · <c>null</c> = ไม่ผูกผู้ขาย ⇒ <b>ไม่ใช่</b>
    /// (ชั้น global ถูกตัดสินแยก)</summary>
    public static bool IsVendorAlias(Guid? aliasContactId, IReadOnlyCollection<Guid> vendorIds)
        => aliasContactId is Guid id && vendorIds.Contains(id);
}
