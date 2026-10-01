using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>วิธีคิดต้นทุนสินค้า (<see cref="CostingMethod"/>) — ตัวตัดสินเดียวว่า "เลือกได้ไหม · เปลี่ยนได้ไหม" (รอบ 201 ทีม IN · A-IN1 ·
/// คำตัดสินข้อ 30)
///
/// ═══ ที่มา ═══
/// <c>Product.CostingMethod</c> มีผู้อ่าน 5 จุด (ledger · costing service · ใบขาย · POS · void) แต่ไม่มีผู้เขียนเลย — DTO และหน้าสินค้า
/// ไม่มีช่อง ⇒ ทุกสินค้าเป็นถัวเฉลี่ยเสมอ ขณะที่โค้ด FIFO ทั้งชุดรออยู่ (ค่าตั้งที่ไม่มีผู้เขียน = เงื่อนไขตาย · F2 ข้อ 2)
///
/// ═══ กติกา (TFRS for NPAEs บทที่ 8) ═══
/// • เลือกได้: ถัวเฉลี่ยถ่วงน้ำหนัก · เข้าก่อน-ออกก่อน (FIFO) · <b>ห้ามเข้าหลังออกก่อน (LIFO)</b> — ไม่มีใน enum เลย ค่าตัวเลขนอก enum ถูกปฏิเสธ
/// • ต้นทุนมาตรฐาน (<see cref="CostingMethod.Standard"/>) <b>เลือกไม่ได้</b>: ระบบยังไม่ลงผลต่างราคา (purchase price variance) — แค่ log
///   ⇒ มูลค่าสินค้าคงเหลือจะไม่ใช่ราคาทุนจริง · วิธีราคาเจาะจง (specific identification) ยังไม่มีใน enum (📋 รอบถัดไป)
/// • เปลี่ยนวิธีของสินค้าที่<b>มีความเคลื่อนไหวสต็อกแล้ว = ปฏิเสธพร้อมทางไปต่อ</b> — COGS ที่ลงไปแล้วคิดด้วยวิธีเดิม และการเปลี่ยนคือ
///   การเปลี่ยนนโยบายบัญชี (ต้องใช้สม่ำเสมอ · ปรับย้อนหลังโดยนักบัญชี) ไม่ใช่การกดสลับแล้ว COGS งวดถัดไปขยับเงียบ ๆ
/// </summary>
public static class CostingMethodPolicy
{
    public const string RuleCode = "TFRS-NPAES-8-COSTING";
    public const string LegalReference = "TFRS for NPAEs บทที่ 8 (สินค้าคงเหลือ)";

    /// <summary>วิธีที่หน้าสินค้า/DTO เลือกได้ (ลำดับ = ลำดับในตัวเลือก · ตัวแรก = ค่าเริ่มต้น)</summary>
    public static readonly IReadOnlyList<CostingMethod> Selectable = new[] { CostingMethod.WeightedAverage, CostingMethod.Fifo };

    /// <summary>ค่าเริ่มต้นของสินค้าใหม่ที่ไม่ได้ระบุ (พฤติกรรมเดิมของระบบ)</summary>
    public const CostingMethod Default = CostingMethod.WeightedAverage;

    /// <summary>ชื่อไทยของวิธี (ข้อความถึงผู้ใช้)</summary>
    public static string Label(CostingMethod m) => m switch
    {
        CostingMethod.WeightedAverage => "ถัวเฉลี่ยถ่วงน้ำหนัก",
        CostingMethod.Fifo => "เข้าก่อน-ออกก่อน (FIFO)",
        CostingMethod.Standard => "ต้นทุนมาตรฐาน",
        _ => m.ToString(),
    };

    /// <summary>ข้อความข้างช่องที่ล็อกบนหน้าสินค้า (สินค้าที่มีความเคลื่อนไหวแล้ว)</summary>
    public const string LockedNotice =
        "สินค้านี้มีความเคลื่อนไหวสต็อกแล้ว — เปลี่ยนวิธีคิดต้นทุนไม่ได้ (TFRS for NPAEs บทที่ 8 ให้ใช้วิธีเดิมสม่ำเสมอ) · "
        + "ถ้าต้องการวิธีอื่น ให้สร้างรหัสสินค้าใหม่ด้วยวิธีที่ต้องการ แล้วปรับสต็อกออกจากรหัสเดิมเข้ารหัสใหม่ด้วยต้นทุนเดียวกัน";

    /// <summary>null = เลือกได้ · ข้อความ = ปฏิเสธ (ไทย พร้อมทางไปต่อ)</summary>
    public static string? RejectReason(CostingMethod requested)
    {
        if (!Enum.IsDefined(typeof(CostingMethod), requested))
            return "วิธีคิดต้นทุนไม่ถูกต้อง — เลือก “ถัวเฉลี่ยถ่วงน้ำหนัก” หรือ “เข้าก่อน-ออกก่อน (FIFO)” "
                + "(วิธีเข้าหลังออกก่อน/LIFO ใช้ไม่ได้ตาม TFRS for NPAEs บทที่ 8)";
        if (!Selectable.Contains(requested))
            return $"วิธี “{Label(requested)}” ยังเลือกไม่ได้ — ระบบยังไม่ลงผลต่างราคาซื้อกับต้นทุนมาตรฐาน มูลค่าสินค้าคงเหลือจะไม่ใช่ราคาทุนจริง · "
                + "เลือก “ถัวเฉลี่ยถ่วงน้ำหนัก” หรือ “เข้าก่อน-ออกก่อน (FIFO)”";
        return null;
    }

    /// <summary>ตัดสินการเปลี่ยนวิธีของสินค้าที่มีอยู่แล้ว — null = เปลี่ยนได้ (หรือไม่ได้เปลี่ยน) · ข้อความ = ปฏิเสธ</summary>
    /// <param name="hasStockMovements">สินค้ามีแถว <c>StockMovement</c> ใด ๆ แล้ว (รวมแถวที่ลบแบบ soft — ทิศปลอดภัย)</param>
    public static string? ChangeRejectReason(CostingMethod current, CostingMethod requested, bool hasStockMovements)
    {
        if (requested == current) return null;
        if (RejectReason(requested) is { } bad) return bad;
        if (hasStockMovements)
            return $"เปลี่ยนวิธีคิดต้นทุนจาก “{Label(current)}” เป็น “{Label(requested)}” ไม่ได้ — " + LockedNotice;
        return null;
    }
}
