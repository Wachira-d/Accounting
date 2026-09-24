using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>ผลทางบัญชี/สต็อกที่ action ของสแกนสร้างขึ้น</summary>
public enum OcrScanPostingTarget
{
    /// <summary>ขึ้นทะเบียนสินทรัพย์ + JE ตั้งสินทรัพย์ (register-asset · import-stock ปลายทาง FixedAsset)</summary>
    FixedAsset,
    /// <summary>สร้างสินค้า + รับเข้าสต็อก/วัสดุสิ้นเปลือง (import-stock ปลายทาง Stock/Supplies)</summary>
    StockReceipt,
}

/// <summary>
/// **action ของสแกนที่สร้างผลทางบัญชี/สต็อก ต้องถือคีย์ของโมดูลปลายทาง** — ตารางเดียวของ <c>OcrController</c>
///
/// <para>═══ ที่มา (ฝ่ายค้านรอบสอง 193 · R2-C3) ═══ ด่านของสแกน "ยังไม่ผูก" เป็นระดับสมาชิก (พื้นที่ทำงานก่อนลงบัญชี — แก้บรรทัด ·
/// จับคู่ผู้ติดต่อ · ลบสแกน) แต่ค่าเดียวกันไปถึง <c>register-asset</c> (FixedAsset + JE ตั้งสินทรัพย์) และ <c>import-stock</c>
/// (สร้างสินค้า + ขยับสต็อก + สร้างสินทรัพย์) ⇒ สมาชิกทุกคนลงทะเบียนสินทรัพย์/รับของเข้าคลังได้ ขณะที่ <c>FixedAssetController</c>
/// บังคับ <c>Asset.Manage</c> · <c>create-document</c>/<c>create-journal-entry</c> มีด่านโมดูลของตัวเองอยู่แล้ว (สองเส้นนี้ไม่มี —
/// R5 "ทางเข้าอื่นไม่เดินด่านเดียวกัน")</para>
/// </summary>
public static class OcrScanPostingKeys
{
    /// <summary>คีย์ที่เปิดผลนั้น (ถือตัวใดตัวหนึ่ง) — ชุดเดียวกับหน้าของโมดูลนั้น</summary>
    public static IReadOnlyList<string> AnyOf(OcrScanPostingTarget target) => target switch
    {
        OcrScanPostingTarget.FixedAsset => new[] { PermissionKeys.AssetManage },
        // รับของเข้าคลัง (GR) หรือผู้ดูแลทะเบียนสินค้า — import-stock ทำทั้งสองอย่างในคำสั่งเดียว
        OcrScanPostingTarget.StockReceipt => new[] { PermissionKeys.InventoryReceive, PermissionKeys.ProductEdit },
        _ => Array.Empty<string>(),   // ผลที่ไม่รู้จัก = ไม่มีคีย์ไหนเปิด (ทิศปิด)
    };

    /// <summary>ผลที่คำขอนำเข้าสต็อกหนึ่งครั้งสร้าง — ตรวจ<b>ทุก</b>ผล (บรรทัดสินทรัพย์ปนบรรทัดสินค้า = ต้องมีทั้งสองชุด)</summary>
    public static IReadOnlyList<OcrScanPostingTarget> TargetsForImport(bool anyFixedAssetLine, bool anyStockOrSuppliesLine)
    {
        var list = new List<OcrScanPostingTarget>(2);
        if (anyFixedAssetLine) list.Add(OcrScanPostingTarget.FixedAsset);
        if (anyStockOrSuppliesLine) list.Add(OcrScanPostingTarget.StockReceipt);
        return list;
    }

    /// <summary>ข้อความไทยเมื่อไม่มีคีย์ — บอกคีย์ที่ต้องขอ ไม่ใช่ 403 เปล่า</summary>
    public static string DeniedMessage(OcrScanPostingTarget target)
    {
        var keys = string.Join(" หรือ ", AnyOf(target).Select(k => k.StartsWith("perm:", StringComparison.Ordinal) ? k[5..] : k));
        return target switch
        {
            OcrScanPostingTarget.FixedAsset =>
                $"ไม่มีสิทธิ์ขึ้นทะเบียนสินทรัพย์จากสแกน (ลง JE ตั้งสินทรัพย์) — ต้องมีสิทธิ์ {keys} จากเจ้าของบริษัท",
            OcrScanPostingTarget.StockReceipt =>
                $"ไม่มีสิทธิ์นำสินค้าจากสแกนเข้าสต็อก — ต้องมีสิทธิ์ {keys} จากเจ้าของบริษัท",
            _ => "ไม่มีสิทธิ์ทำรายการนี้จากสแกน",
        };
    }
}
