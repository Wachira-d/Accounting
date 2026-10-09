namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียวของ "ใบแจ้งหนี้ซื้อที่อ้างใบสั่งซื้อตรง ทั้งที่บรรทัดนั้นรับของผ่านใบรับสินค้าแล้ว" (ERP_REVIEW E-05 · ทีมตรวจงานค้าง 2026-10-08 C-03/C-09)
///
/// <para>PO → GRN (สต็อกเข้า + Cr 21240 GR-NI) แล้วออกใบแจ้งหนี้ซื้อที่ <c>RelatedDocumentId</c> = PO (ไม่ใช่ GRN) ⇒ ตอนอนุมัติหา GR-NI ไม่เจอ ⇒
/// สต็อกเข้า<b>รอบสอง</b> + Dr สินค้าซ้ำ + 21240 ค้างตลอดกาล · เดิมกันเฉพาะทาง "แปลง PO → PI" (และกันทั้งใบเมื่อมี GRN ใดก็ได้ ⇒ PO ผสม
/// สินค้า/บริการทางตัน) ขณะที่ใบจากสแกน/API/สร้างมือที่ผูก PO ผ่านไปได้</para>
/// <para>กติกา: มีใบรับสินค้าที่ยังมีผลของ PO นี้ และ (ใบแจ้งหนี้ไม่ได้ระบุบรรทัด PO เลย — ถือว่าทั้งใบ · หรือบรรทัด PO ที่บิลทับกับบรรทัดที่ GRN รับแล้ว)</para>
/// </summary>
public static class PurchaseReceiptRoute
{
    public const string RuleCode = "PI-PO-HAS-GRN";

    /// <param name="anyActiveGrn">PO นี้มีใบรับสินค้าที่ไม่ยกเลิก/ปฏิเสธ</param>
    /// <param name="billedPoLineIds">บรรทัด PO ที่ใบแจ้งหนี้ซื้ออ้าง (ว่าง = ไม่ระบุบรรทัด ⇒ ถือว่าทั้งใบ)</param>
    /// <param name="receivedPoLineIds">บรรทัด PO ที่ GRN รับไปแล้ว</param>
    /// <param name="grnWithoutLineLink">มี GRN ที่ไม่ได้ยกบรรทัด (ไม่มี SourceLineId) ⇒ ถือว่ารับทั้งใบ</param>
    public static bool PoBillBlockedByGrn(bool anyActiveGrn, IReadOnlyCollection<Guid> billedPoLineIds,
        IReadOnlyCollection<Guid> receivedPoLineIds, bool grnWithoutLineLink)
    {
        if (!anyActiveGrn) return false;
        if (billedPoLineIds.Count == 0 || grnWithoutLineLink) return true;
        return billedPoLineIds.Any(receivedPoLineIds.Contains);
    }

    public static string Message(string grnNumber) =>
        $"รายการในใบสั่งซื้อนี้รับของผ่านใบรับสินค้า {grnNumber} แล้ว — ใบแจ้งหนี้ซื้อของรายการนั้นต้องออก/ผูกจากใบรับสินค้า (3-way match) " +
        "ไม่ใช่จากใบสั่งซื้อตรง ไม่งั้นสต๊อกและบัญชีสินค้าคงเหลือจะลงซ้ำสองรอบ (21240 ค้าง) · ใบร่างที่ผูกผิด: กด \"🔗 ผูกกับเอกสารต้นทาง\" " +
        "เพื่อย้ายไปผูกใบรับสินค้าของใบสั่งซื้อนี้ (ไม่ต้องคีย์ใหม่) หรือยกเลิกแล้วแปลงจากใบรับสินค้า";
}
