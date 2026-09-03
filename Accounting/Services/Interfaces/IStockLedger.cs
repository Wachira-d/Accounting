using Accounting.Models.Entities;

namespace Accounting.Services.Interfaces;

/// <summary>คำขอเคลื่อนไหวสต็อก 1 รายการ
///
/// <para><paramref name="Quantity"/> ใช้เครื่องหมายบอกทิศ: **บวก = เข้า · ลบ = ออก**
/// — เดิมแต่ละที่เขียนคนละแบบ (บางที่ `Quantity = -qty` บางที่ `Quantity = qty` แล้วดู
/// `MovementType` เอา) ซึ่งทำให้รายงานที่รวมยอดจาก `StockMovement` ตรงบางที่ผิดบางที่</para>
///
/// <para><paramref name="WarehouseId"/> = null → ledger เลือก **คลังหลัก** ของบริษัทให้
/// (บริษัทที่ยังไม่ใช้ระบบคลังต้องทำงานได้เหมือนเดิมโดยไม่รู้ว่ามีคำว่า "คลัง")</para></summary>
public sealed record StockMoveRequest(
    Guid CompanyId,
    Guid ProductId,
    decimal Quantity,
    string MovementType,
    string? Reference = null,
    Guid? WarehouseId = null,
    Guid? DocumentId = null,
    Guid? PosOrderId = null,
    DateTime? MovementDate = null,
    // ต้นทุนต่อหน่วยที่ผู้เรียกยืนยันเอง (ใบซื้อ/ปรับปรุง) — null = ให้ ledger คิดจาก
    // costing (ขาเข้าใช้ต้นทุนที่ส่งมา · ขาออกใช้ต้นทุนถัวเฉลี่ย/FIFO)
    // (ใช้ // ไม่ใช่ /// — XML doc บนพารามิเตอร์ของ positional record ทำให้เกิด CS1587)
    decimal? UnitCostOverride = null,
    string? LotNumber = null,
    string? SerialNumber = null,
    Guid? TransferPairId = null,
    string? Notes = null,
    string? CreatedBy = null,
    // true = ตั้งยอดเป็นค่านี้ตรง ๆ (ใช้กับการนับสต็อก) แทนการบวก/ลบ
    // — `Quantity` กลายเป็น "ยอดที่นับได้" ไม่ใช่ "ผลต่าง"
    bool SetAbsolute = false)
{
    /// <summary>ผลต่างที่ต้องเขียนลงคลัง เมื่อยอดปัจจุบัน**ของคลังนั้น**คือ
    /// <paramref name="currentInWarehouse"/>
    ///
    /// <para>แยกออกมาเป็นฟังก์ชันบริสุทธิ์เพราะเป็นจุดที่เคยพลาดจริง: การนับสต็อก
    /// เขียน <c>product.CurrentStock = countedQty</c> ตรง ๆ ⇒ นับคลังเดียวแล้ว
    /// **เขียนทับยอดรวมทุกคลัง** (นับสาขา A ได้ 10 ⇒ ระบบเชื่อว่าทั้งบริษัทมี 10
    /// ทั้งที่สาขา B ยังมีของ). ที่ถูกคือแปลง "ยอดที่นับได้" เป็น "ผลต่างของคลังนั้น"
    /// แล้วให้ยอดรวมขยับตามผลต่าง</para></summary>
    public decimal DeltaFrom(decimal currentInWarehouse)
        => SetAbsolute ? Quantity - currentInWarehouse : Quantity;
}

/// <summary>ผลของการเคลื่อนไหว 1 รายการ</summary>
public sealed record StockMoveResult(
    StockMovement Movement,
    Guid WarehouseId,
    decimal QuantityAfterInWarehouse,
    decimal QuantityAfterTotal,
    decimal UnitCostUsed)
{
    /// <summary>ต้นทุนรวมของการเคลื่อนไหวนี้ (ใช้ลง COGS ฝั่งขาออก)</summary>
    public decimal TotalCost => Math.Abs(Movement.Quantity) * UnitCostUsed;
}

/// <summary>
/// **ผู้เขียนสต็อกตัวเดียวของระบบ**
///
/// ═══ ที่มา (บั๊กจริง — POS_MULTI_BRANCH_ANALYSIS.md §2.2) ═══
/// เดิมมี **สองความจริงของสต็อก** ที่ไม่คุยกัน:
/// <list type="bullet">
/// <item><c>Product.CurrentStock</c> — ตัวเลขเดียวทั้งบริษัท · POS/ใบซื้อ/รับสินค้า/
///   นับสต็อก/ผลิต/ขายออนไลน์ เขียนตัวนี้ (9 ไฟล์)</item>
/// <item><c>WarehouseStock</c> — สต็อกต่อคลัง · เขียนโดย <c>WarehouseService</c> ตัวเดียว
///   (ใบโอนคลัง) และ<b>ไม่แตะ <c>CurrentStock</c> เลย</b></item>
/// </list>
/// ผลคือ: โอนวัตถุดิบจากครัวกลางไปสาขา → <c>WarehouseStock</c> ขยับ แต่ยอดที่ POS ตัด
/// ตอนขายคือ <c>CurrentStock</c> ซึ่งไม่เกี่ยวกับคลังไหนเลย ⇒ ตัวเลขสองชุดที่**ไม่มีวัน
/// ตรงกัน** และไม่มีใครรู้ว่าอันไหนคือของจริง
///
/// ═══ กติกาหลังรอบนี้ (hard requirement) ═══
/// <list type="number">
/// <item><c>WarehouseStock</c> เป็น**ความจริง** · <c>Product.CurrentStock</c> เป็น
///   **ผลรวมที่ derive มา** (Σ ทุกคลัง) — ห้ามมีใครตั้งค่ามันเองอีก</item>
/// <item>ทุกการเคลื่อนไหวเขียน <c>StockMovement</c> ที่มี <c>WarehouseId</c> เสมอ
///   (เดิมฟิลด์มีแต่ไม่มีใครใส่ ⇒ รายงานแยกคลังทำไม่ได้)</item>
/// <item>ห้ามเขียน <c>StockMovements.Add</c> หรือ <c>CurrentStock ±=</c> นอกคลาสนี้ —
///   บังคับด้วย <c>tools/stock_writer_check.py</c></item>
/// </list>
///
/// <para><b>ล็อก</b>: <c>pg_advisory_xact_lock</c> ต่อ (คลัง, สินค้า) — read-modify-write
/// ของยอดคงเหลือแข่งกันได้จริงตอน POS หลายเครื่องขายสินค้าตัวเดียวกันพร้อมกัน ·
/// ต้องอยู่ในธุรกรรมของผู้เรียก (ล็อกปล่อยตอนจบธุรกรรม — ถ้าไม่มีธุรกรรมก็ไม่กันอะไรเลย
/// ตามบทเรียน <c>AdvisoryLockKey</c>)</para>
///
/// <para><b>ไม่ SaveChanges เอง</b> — ผู้เรียกคุมธุรกรรมของตัวเอง (ขายของ 1 ครั้งมีหลาย
/// บรรทัด + JE + เอกสาร ต้องลงพร้อมกันหรือไม่ลงเลย)</para>
/// </summary>
public interface IStockLedger
{
    /// <summary>เคลื่อนไหวสต็อก 1 รายการ — เขียน <c>WarehouseStock</c> +
    /// <c>StockMovement</c> + ปรับ <c>Product.CurrentStock</c> ให้ตรงกับผลรวม</summary>
    Task<StockMoveResult> MoveAsync(StockMoveRequest request, CancellationToken ct = default);

    /// <summary>คลังหลักของบริษัท — สร้างให้ถ้ายังไม่มี (บริษัทที่ไม่เคยใช้ระบบคลัง
    /// ต้องทำงานได้โดยไม่ต้องตั้งค่าอะไร)</summary>
    Task<Guid> GetOrCreateDefaultWarehouseIdAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>คลังที่ควรใช้สำหรับสาขานี้ — <c>branchId = null</c> หรือสาขานั้นไม่มีคลัง
    /// ผูกไว้ → คลังหลักของบริษัท
    ///
    /// <para>เอกสาร (ใบซื้อ/ใบขาย) ผูก **สาขา** ไม่ได้ผูกคลัง ⇒ ต้องมีตัวแปลงตัวเดียว
    /// ห้ามให้แต่ละ service เดาเอง (ไม่งั้นใบซื้อกับใบขายของสาขาเดียวกันลงคนละคลัง)</para></summary>
    Task<Guid> ResolveWarehouseIdAsync(Guid companyId, Guid? branchId, CancellationToken ct = default);

    /// <summary>ยอดคงเหลือของสินค้าในคลังนั้น (0 ถ้าไม่มีแถว)</summary>
    Task<decimal> GetQuantityAsync(Guid companyId, Guid productId, Guid warehouseId, CancellationToken ct = default);

    /// <summary>ซ่อม <c>Product.CurrentStock</c> ให้เท่ากับ Σ <c>WarehouseStock</c>
    /// — ใช้หลัง migration และในงานตรวจสอบความสอดคล้อง</summary>
    Task<int> ReconcileProductTotalsAsync(Guid companyId, CancellationToken ct = default);
}
