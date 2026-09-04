namespace Accounting.Helpers;

/// <summary>สูตรต้นทุนถัวเฉลี่ยถ่วงน้ำหนัก (TFRS for NPAEs บทที่ 8) — **ที่เดียวของระบบ**
///
/// <para>ที่มา: สูตรนี้เคยถูกเขียนซ้ำ 3 ที่ (<c>InventoryCostingService.RegisterReceiptAsync</c> ·
/// <c>DocumentService.ApplyStockMovementsAsync</c> inline · และกำลังจะเป็นที่ที่ 4 ใน
/// <c>StockLedger</c>) — defect class "ตารางความรู้/สูตรที่คัดลอกไปเขียนใหม่ = drift แน่นอน
/// แค่รอเวลา" ที่ CLAUDE.md ห้ามไว้ · แยกเป็นฟังก์ชัน**บริสุทธิ์**เพื่อให้เทสต์ได้และให้
/// ledger เรียกได้โดยไม่ต้องผ่าน service ที่ <c>SaveChangesAsync</c> เอง (การ save กลางทาง
/// จะ flush entity ครึ่ง ๆ กลาง ๆ ของผู้เรียกไปด้วย)</para></summary>
public static class WeightedAverageCost
{
    /// <summary>ต้นทุนถัวเฉลี่ยใหม่หลังรับเข้า
    /// <c>newAvg = (oldStock×oldAvg + qty×receiptCost) / (oldStock+qty)</c>
    ///
    /// <para>ยอดคงเหลือติดลบถูกปัดเป็น 0 ก่อนคิด (สต็อกติดลบเกิดได้เมื่อบริษัทเปิด
    /// <c>AllowNegativeStock</c> — ถ้าเอามาถ่วงน้ำหนักจะได้ค่าเฉลี่ยติดลบซึ่งไม่มีความหมาย)
    /// และเมื่อยอดรวมหลังรับ ≤ 0 ให้ถือว่าต้นทุนที่รับเข้าคือค่าเฉลี่ยตั้งต้น</para></summary>
    public static decimal Next(decimal oldStock, decimal oldAverage, decimal costPrice,
        decimal receiptQuantity, decimal receiptUnitCost)
    {
        var stock = Math.Max(0m, oldStock);
        var avg = oldAverage > 0m ? oldAverage : costPrice;
        var total = stock + receiptQuantity;
        if (total <= 0m) return Math.Round(receiptUnitCost, 4, MidpointRounding.AwayFromZero);
        return Math.Round((stock * avg + receiptQuantity * receiptUnitCost) / total, 4,
            MidpointRounding.AwayFromZero);
    }
}
