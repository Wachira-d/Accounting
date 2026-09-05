# ทีม E — สายสินค้า/สต็อก/ซื้อ/ขาย (PR→PO→GRN→PI · Quotation→Invoice→DN→Receipt · POS · Warehouse · Costing)

_(รายงานเขียน append ตั้งแต่ finding แรก — ส่วน "สรุป 5 บรรทัด" เติมท้ายสุด)_

## Findings (เรียง P0→P3 — ลำดับสุดท้ายจัดตอนจบ)

### E-01 [P0][S] รายงานสต็อกทุกตัวใน ProductService **ลบ** ยอด OUT ที่ ledger เก็บเป็น **ลบอยู่แล้ว** ⇒ ขายของแล้วรายงานบอกว่าสต็อก *เพิ่ม* · COGS ในรายงานติดลบ
- ไฟล์: `Accounting/Services/Implementations/Inventory/StockLedger.cs:197-200` (ฝั่งเขียน) · `Accounting/Services/Implementations/ProductService.cs:617-621, 635` (สินค้าคงเหลือ ณ วันที่) · `:883-900, 915-921` (สรุปเคลื่อนไหวสต็อก)
- โค้ด (ฝั่งเขียน): `// เครื่องหมายบอกทิศเสมอ … Quantity = delta,` — delta ของขาออกเป็นลบ (IStockLedger.cs: "บวก = เข้า · ลบ = ออก")
- โค้ด (ฝั่งอ่าน :617-618): `TotalIn = g.Where(m => m.MovementType == "IN").Sum(m => m.Quantity), TotalOut = g.Where(m => m.MovementType == "OUT").Sum(m => m.Quantity),` แล้ว :635 `qty = mv.TotalIn - mv.TotalOut + mv.TotalAdjust;` · :900 `CostOut = g.Where(m => m.MovementType == "OUT").Sum(m => m.Quantity * m.UnitCost)` · :921 `closing = opening + totalIn - totalOut + totalAdj;` · :883-885 opening ใช้สูตรเดียวกัน
- ทำไมพัง: (1) เฟส 0 ของ POS_MULTI_BRANCH ยุบผู้เขียนทั้งหมดเข้า ledger และ**บังคับเครื่องหมาย** (OUT = ลบ) (2) รายงานเดิมเขียนสมัยที่ OUT เก็บเป็นบวก จึง `TotalIn − TotalOut` (3) วันนี้ TotalOut ของสินค้าที่ขาย 10 ชิ้น = −10 ⇒ `qty = In − (−10)` = **บวกเพิ่ม 10** · `CostOut` = −(10×ต้นทุน) ⇒ "ต้นทุนขาย" ในรายงานเป็นลบ · `closing` เพี้ยนทิศเดียวกัน (4) `InventoryCostingService` รู้เรื่องนี้แล้ว (":153 ต้อง Σ|Quantity|" + `RebuildAverageCostAsync:216 Math.Abs`) แต่ ProductService ไม่ถูกแก้ตาม — defect class "แก้ตัวเดียว เหลือที่เหลือ"
- ผลกระทบ: หน้า "สินค้าคงเหลือ ณ วันที่" (ใช้ปิดงวด/ตรวจนับ/งบ) และ "สรุปเคลื่อนไหวสต็อก" ให้ยอดคงเหลือและ COGS ผิดทิศทุกสินค้าที่มีการขาย · ผู้ใช้เทียบกับ `Product.CurrentStock` (ถูก) แล้วไม่ตรง ⇒ เชื่ออันไหน? · มูลค่าสินค้าคงเหลือปลายงวดในรายงานสูงเกินจริง ⇒ ถ้าใช้ยื่น ภ.ง.ด.50 กำไรผิด
- defect class: "แก้ตัวเดียว เหลือที่เหลือ" · "สองแหล่งอ่านค่าเดียวกันคนละสูตร" (สำเนามือ R1)
- ทางแก้: ยุบเป็น helper ตัวเดียว `StockMovementSign` หรือให้รายงานใช้ `Sum(Quantity)` ตรง ๆ (เครื่องหมายอยู่ในข้อมูลแล้ว) และ `CostOut = Sum(|Quantity|×UnitCost)` · เขียน simulation เทสต์ที่ป้อน movement ชุดเดียวกันเข้า ledger แล้วรายงานต้องได้ = `CurrentStock`
- ความมั่นใจ: **สูง** (อ่านทั้งสองฝั่งแล้ว) — ต้องเช็คต่อ: migration เฟส 0 แปลงเครื่องหมายแถวเก่าหรือไม่ (ถ้าไม่ ข้อมูลเก่าปนสองระบบ — รายงานยิ่งกู้ไม่ได้)

### E-02 [P1][M] Costing/รายงาน รู้จักแค่ `"IN"/"OUT"/"ADJUST"` แต่ ledger ถูกป้อนด้วย `"OPENING"`, `"TRANSFER_IN"`, `"TRANSFER_OUT"` ⇒ สต็อกยกมาหายจาก FIFO/WAC-rebuild/รายงานทั้งหมด
- ไฟล์: `ImportExportService.cs:926` (`MovementType: "OPENING"`) · `WarehouseService.cs:303,349,389` (`TRANSFER_*`) · ฝั่งอ่าน: `InventoryCostingService.cs:141` (`m.MovementType == "IN"` = layer FIFO) · `:153` (`== "OUT"` = ที่บริโภคแล้ว) · `:209,216` (rebuild WAC) · `ProductService.cs:551, 617-621, 817, 883-900, 907, 964, 1142` · `FinancialManagementService.cs:473`
- ทำไมพัง: (1) นำเข้าสต็อกยกมา 100 @ 50 ⇒ movement ชนิด OPENING (ไม่ใช่ IN) (2) FIFO: `inLayers` ว่าง ⇒ `taken < quantity` ⇒ ต้นทุน fallback `product.CostPrice` (บังเอิญถูกเพราะ import ตั้ง CostPrice) — แต่พอมีใบซื้อใหม่ 10 @ 80 แล้วขาย 5: FIFO ต้องได้ 50 กลับได้ **80** (layer 50 ไม่มีอยู่) (3) `RebuildAverageCostAsync` (ถูกเรียกหลัง void ใบซื้อ ที่ `DocumentService.cs:12478`) เดินเฉพาะ IN/OUT ⇒ หลัง void ใบซื้อ 10@80 ที่มียกมา 100@50 → avg กลายเป็น **80** (ควร 50) ⇒ COGS ทุกใบถัดไปผิด (4) รายงาน "สินค้าคงเหลือ ณ วันที่" `qty = In − Out + Adjust` ไม่มี OPENING ⇒ บริษัทที่ย้ายระบบมา (ทุกบริษัทที่ import) **ยอดยกมาไม่อยู่ในรายงาน** · `TRANSFER_*` หายด้วย (สุทธิบริษัทเป็น 0 ก็จริง แต่ของที่ค้างระหว่างทางไม่โชว์)
- ผลกระทบ: COGS/มูลค่าคงเหลือผิดสำหรับทุกบริษัทที่นำเข้ายอดยกมา (= ลูกค้าย้ายระบบทุกราย) · GL 11500 (ที่ลงจากใบซื้อจริง) กับรายงานสต็อกไม่ตรงถาวร
- defect class: "ชื่อ key คนละชุดระหว่างฝั่งเขียนกับฝั่งอ่าน" (กฎเหล็ก #4 รูปแบบที่ 4) · สำเนาชุดค่าคงที่ (string literal ไม่มี enum)
- ทางแก้: ทำ `MovementTypes` เป็น static class ที่เดียว + นิยาม "เป็น layer ขาเข้าไหม" (`IN, OPENING, TRANSFER_IN, ADJUST>0`) / "บริโภคไหม" ให้ costing และรายงานเรียกตัวเดียว · เทสต์ FIFO ที่เริ่มจาก OPENING
- ความมั่นใจ: **สูง**

### E-03 [P1][S] ด่าน "สต็อกติดลบ" (`AllowNegativeStock`) ทำงานคนละมาตรฐานตามทางเข้า: เอกสารไม่เคยเช็ค · POS **บล็อกการขาย** (ขัดกับดีไซน์ที่บอกห้ามบล็อก) · โอนคลังเช็ค
- ไฟล์: `InventoryCostingService.cs:116-125` (ด่านตัวเดียว อยู่ใน `ResolveOutboundCostAsync`) · `StockLedger.cs:172-174` (`unitCost = r.UnitCostOverride ?? await _costing.ResolveOutboundCostAsync(...)`) · `DocumentService.cs:12645-12657` (ส่ง `UnitCostOverride: unitCost` ทุกบรรทัด) · `DocumentService.cs:12758-12765` (FIFO เรียก costing แต่ `catch (InvalidOperationException)` แล้ว fallback) · `PosService.Orders.cs:614-624, 1057-1067, 1147-1158` (ไม่ส่ง override ⇒ ด่านทำงาน ⇒ throw)
- โค้ด: `unitCost = r.UnitCostOverride ?? await _costing.ResolveOutboundCostAsync(r.ProductId, Math.Abs(delta), ct);` — ด่านอยู่ *ข้างใน* ตัวคิดต้นทุน ⇒ ใครส่งต้นทุนมาเอง = ข้ามด่านโดยไม่รู้ตัว
- ทำไมพัง: (1) ด่านถูกฝังในเมธอด "คิดต้นทุน" (2) DocumentService คิดต้นทุนเองแล้วส่ง override ⇒ ใบกำกับขายของที่ไม่มีสต็อก **ผ่านเงียบ** ไม่ว่าตั้งค่าไว้อย่างไร (แม้ FIFO ก็ catch ทิ้ง — doc-comment :12744 ยอมรับเอง "ไม่ block") (3) POS ไม่ส่ง override ⇒ ด่านทำงาน ⇒ ขายชานมที่วัตถุดิบ (BOM) ในระบบเป็น 0 → `InvalidOperationException("สต๊อกไม่พอ…")` → **ปิดบิลไม่ได้ทั้งใบ** ขัดกับ POS_MULTI_BRANCH §5.3 "วัตถุดิบติดลบได้ … ห้ามบล็อกการขาย" (4) ด่านเช็ค `product.CurrentStock` (ยอดรวมบริษัท) ไม่ใช่ยอดคลังที่ขาย ⇒ สาขา A ขายของที่มีอยู่แค่ที่สาขา B ผ่านได้ (แถว WarehouseStock ติดลบ) แต่บริษัทคลังเดียวถูกบล็อก
- ผลกระทบ: หน้าร้านค้าง ณ จุดขายเพราะข้อมูลสต็อกในระบบยังไม่อัปเดต (สินค้าเข้าแล้วแต่ GRN ยังไม่อนุมัติ = เรื่องปกติของหน้าร้าน) ในขณะที่ฝั่งเอกสารขายทะลุสต็อกได้เสรี ⇒ กติกาเดียวกัน**สองมาตรฐาน**
- defect class: "ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี" · "ด่านที่เขียนไว้ครึ่งเดียว"
- ทางแก้: ย้ายด่านออกจาก costing มาไว้ที่ `StockLedger.MoveAsync` (ขาออกทุกทาง) โดยเช็ค**ยอดคลังนั้น** และให้ผู้เรียกบอกนโยบาย (`BlockIfNegative` / `WarnOnly`) — POS = warn + ธงแดงในรายงานสิ้นวันตามดีไซน์ · โยน `BusinessRuleException` ไม่ใช่ `InvalidOperationException`
- ความมั่นใจ: **สูง** สำหรับ (1)-(3) · (4) สูง (อ่านโค้ดตรง)

### E-04 [P0][S] `StockTransferController` (route `stock-transfers`, ใช้จริงจากหน้า `sme-config.html`) เขียน `StockMovement` **ตรง ๆ** ข้าม `IStockLedger` — ไม่แตะ `WarehouseStock`/`CurrentStock` เลย = "สองความจริง" กลับมาแล้ว และ checker มองไม่เห็น
- ไฟล์: `Accounting/Controllers/StockTransferController.cs:110-125` (ship) · `:146-172` (receive) · `:186-200` (cancel) · UI: `Accounting/wwwroot/pages/sme-config.html:430, 476, 490` (`fetch(\`/api/companies/${cid}/stock-transfers/${id}/${action}\`)`) · เมนู `layout.js:1207` "ตั้งค่าขั้นสูง (อนุมัติ·สต๊อก·กะ)" · checker: `tools/stock_writer_check.py:44` `RE_MOVEMENT_ADD = r"\bStockMovements\s*\.\s*Add\b"`
- โค้ด (:112-125): `_db.Set<StockMovement>().Add(new StockMovement { … MovementType = "TRANSFER_OUT", Quantity = -l.Quantity, UnitCost = 0, BalanceAfter = 0, // recompute later by stock-balance service … });` — ไม่มี `WarehouseStocks` ไม่มี `CurrentStock` ไม่มี costing
- ทำไมพัง: (1) เรพมี**ใบโอนคลังสองชุด**: `WarehouseService` (route `warehouses/transfers`, api.js:714-717 — ผ่าน ledger ครบ) กับ `StockTransferController` (route `stock-transfers`) ที่ยังเป็นโค้ดยุคก่อนเฟส 0 (2) ผู้ใช้ที่เข้าทางเมนู "ตั้งค่าขั้นสูง" กด Ship/รับ ⇒ ได้แถว movement (UnitCost 0) แต่**ยอดคลังไม่ขยับสักตัว** ⇒ stock card บอกว่าย้ายแล้ว ยอดคงเหลือทั้งสองคลังเท่าเดิม (3) `ReconcileProductTotalsAsync` ตรวจ `CurrentStock == ΣWarehouseStock` เท่านั้น — สองตัวนี้ยังตรงกัน จึง**ตรวจไม่เจอ** (4) checker จับเฉพาะ `StockMovements.Add` แต่ที่นี่ใช้ `_db.Set<StockMovement>().Add` ⇒ ผ่านเขียวตลอด (negative test ของ checker ไม่มีรูปแบบนี้)
- ผลกระทบ: ใบโอนที่ทำจากหน้านี้ไม่มีผลต่อสต็อกจริง · รายงานที่ SUM จาก movement (E-01/E-02) กับยอดคลังเดินคนละทาง · UnitCost 0 ทำให้ movement-based costing (FIFO layer ของคลังปลายทางถ้าอนาคตแยกคลัง) เพี้ยน
- defect class: "ของสองชุดที่ทำเรื่องเดียวกัน" (R1) · "checker ที่ negative test ไม่ครอบ = ด่านที่ไม่มี" · "แก้ตัวเดียว เหลือที่เหลือ" (เฟส 0 ย้าย WarehouseService แต่ไม่เห็นตัวนี้)
- ทางแก้: ให้ `StockTransferController` มอบต่อ `IWarehouseService.Ship/Receive/Void` (หรือลบ controller + เปลี่ยน sme-config ไปเรียก `warehouses/transfers`) · ขยาย checker ให้จับ `Set<StockMovement>()\s*\.\s*Add` ด้วย + เพิ่ม negative test
- ความมั่นใจ: **สูง**

### E-05 [P0][M] PO แปลงเป็น GRN (แกน Delivery) และแปลงเป็น PI ตรง (แกน Billing) ได้**ทั้งคู่โดยอิสระ** ⇒ ของเข้าสต็อก 2 รอบ · Dr 11500 2 รอบ · เจ้าหนี้-รับสินค้ายังไม่วางบิล (21240) ค้างตลอดกาล
- ไฟล์: `DocumentService.cs:8877-8881` (PO → `{GoodsReceiptNote, PurchaseInvoice}`) · `:8982-8993` (`GetFulfillmentAxis`: GRN = Delivery · PurchaseInvoice = Billing) · `:9515-9525` (ด่าน `remaining` ตรวจ**ต่อแกน**) · `:11329-11343` (`GetReceivedViaGrnAccrualAccountAsync` คืน null ถ้า `RelatedDocumentId` ไม่ใช่ GRN) · `:12577-12579` (PI ข้าม stock เฉพาะเมื่ออ้าง GRN) · `:13546-13556` (PI ล้าง GR-NI เฉพาะเมื่ออ้าง GRN)
- โค้ด (:8880): `DocumentType.PurchaseInvoice, // skip GRN when buying services` และ (:9518-9520) `var used = axis == FulfillmentAxis.Delivery ? consumption[srcLine.Id].Delivery : consumption[srcLine.Id].Billing; var remaining = srcLine.Quantity - used;`
- ทำไมพัง: (1) คลังรับของ → แปลง PO→GRN 100 ชิ้น (Delivery ใช้ครบ) → อนุมัติ GRN: stock +100, Dr 11500 / Cr 21240 (2) บัญชีได้ใบกำกับจากผู้ขาย → เปิด PO แล้วกด "แปลงเป็นใบแจ้งหนี้ซื้อ" (ทางที่ธรรมชาติที่สุด — เพราะเอกสารต้นทางที่บัญชีถืออยู่คือ PO) → ด่าน remaining ดูแกน Billing ซึ่งยัง 0 ⇒ **ผ่าน 100 ชิ้น** (3) PI ใบนี้ `RelatedDocumentId = PO` ⇒ `GetReceivedViaGrnAccrualAccountAsync` = null ⇒ อนุมัติแล้ว **stock +100 อีกรอบ** + Dr 11500 อีกรอบ + Cr 21210 เจ้าหนี้ (4) 21240 ไม่มีใครล้าง — ค้างเป็นหนี้สินผีในงบ · สต็อกเบิ้ล · มูลค่าสินค้าคงเหลือเบิ้ล (5) ซ้ำเติมด้วย A-06/A-07 ของทีม A: ปุ่มแปลง PO→GRN **ไม่มีใน UI** ⇒ ผู้ใช้ที่ทำ GRN ต้องสร้าง GRN standalone (ไม่ผูก PO) แล้วแปลง PO→PI ⇒ เจอเคสเดียวกันแบบไม่มีทางเลี่ยง
- ผลกระทบ: ทุกบริษัทที่ใช้ GRN + PO พร้อมกัน — สินค้าคงเหลือและต้นทุนสูงเกินจริง ⇒ กำไร/ภ.ง.ด.50 ผิด · 21240 บวมโดยไม่มีขาออก
- defect class: "กติกาเดียวกันสองมาตรฐาน" · "ด่านที่ครอบแค่ทางเดียว" · เงื่อนไข `RelatedDocumentId == GRN` เป็น "ค่าที่แต่งขึ้นเพื่อให้โค้ดเดินต่อ" (สมมติว่า PI ที่มาจาก GRN ต้องอ้าง GRN เสมอ)
- ทางแก้: (ก) ตอนแปลง PO→PI ให้ตรวจว่า PO นี้มี GRN Approved อยู่ไหม — ถ้ามีต้อง**บังคับแปลงจาก GRN** (หรือให้ PI สืบทอด `SourceLineId` ของ GRN line โดยอัตโนมัติ) (ข) ใน `ApplyStockMovementsAsync`/`AutoPost` ตัดสินจาก "มี movement IN ของ SourceLine ต้นทางแล้วไหม" ไม่ใช่จากชนิดของ `RelatedDocumentId` (ค) เพิ่มเทสต์ PO→GRN→(PO→PI) แล้ว assert stock = 100 และ 21240 = 0
- ความมั่นใจ: **สูง** ฝั่งโค้ด · ต้องเช็คต่อ: UI `documents.html` แสดงตัวเลือกแปลง PO→PI จริง (ทีม A ยืนยันแล้วว่า PO→GRN ไม่มี ⇒ PO→PI คือทางเดียวที่มี)

### E-06 [P1][M] Consignment ขาออก: ตัดสต็อกตอนส่งของไปฝาก (ไม่มี JE — ถูก) แต่ตอน "ขายได้จริง" สร้าง Invoice ที่**ไม่มี ProductCode** ⇒ COGS ไม่ถูกลงเลยตลอดชีวิตของสินค้าฝากขาย + VAT ถูก hard-code 0
- ไฟล์: `Services/Implementations/Consignment/ConsignmentService.cs:108-118` (MoveAsync OUT ตอน dispatch) · `:142-178` (สร้าง Document ตอน consumption) · `DocumentService.cs:12813-12830` (`ComputeSalesCogsAsync` หา product จาก `l.ProductCode`) · `:12583-12590` (ApplyStockMovements ข้ามบรรทัดที่ไม่มี ProductCode)
- โค้ด (:166-176): `Lines = new List<DocumentLine> { new() { … Description = $"สินค้าฝากขาย ({record.Direction}) — บริโภคจริง", Quantity = consumedQuantity, … VatRate = 0m, VatAmount = 0m, } }` — ไม่มี `ProductCode`, ไม่มี `AccountId`
- ทำไมพัง: (1) dispatch: ledger OUT ที่ต้นทุนถัวเฉลี่ย ⇒ subledger สต็อกลด แต่ GL 11500 ไม่ลด (ตั้งใจ — ของยังเป็นของเรา) (2) consumption: Invoice ไม่มี ProductCode ⇒ `ComputeSalesCogsAsync` = 0 ⇒ JE ขายมีแค่ Dr AR / Cr รายได้ **ไม่มี Dr COGS / Cr 11500** (3) ⇒ 11500 ใน GL ค้างมูลค่าสินค้าที่ขายไปแล้วถาวร · กำไรขั้นต้นสูงเกินจริง · รายงาน tie-out สต็อก-GL (`InventoryGlTieOutReport`) ไม่มีวันตรง (4) `VatRate = 0m` ตายตัว — บริษัทจด VAT ขายสินค้าผ่านผู้รับฝากขายต้องมี VAT 7% (ผู้ฝากคือผู้ขาย §77/1) ⇒ ภ.พ.30 ขาดยอดขาย (5) ฝั่ง Inbound (`ReceiveInboundAsync:63-79`) ไม่แตะสต็อกเลย (ถูก — ของคนอื่น) แต่ตอน consumption สร้าง PI ที่ไม่มี ProductCode ⇒ ไม่มี stock IN/OUT และไม่มีทางรู้ว่าของฝากคนอื่นอยู่ในร้านเท่าไร (ไม่มี off-balance tracking ต่อคลัง)
- ผลกระทบ: เงิน/ภาษี — COGS ขาด · VAT ขายขาด · GL–subledger drift
- defect class: "ค่า default ที่แต่งขึ้น" (VatRate 0) · "เอกสารที่สร้างจากโมดูลรองต้องเดินผ่าน `IDocumentService`" (กฎในไฟล์ CLAUDE.md หัวข้อโมดูลที่มีเงิน) — ที่นี่ `_db.Documents.Add` เอง
- ทางแก้: สร้างผ่าน `IDocumentService.CreateDocumentAsync` พร้อม `ProductCode`, VatRate ของสินค้า/บริษัท, และธง "สต็อกตัดแล้วตอน dispatch" (ห้ามตัดซ้ำ) แต่ COGS ต้องลงจาก movement ตอน dispatch ที่ผูก consignment record
- ความมั่นใจ: **สูง**

### E-07 [P1][M] ปรับสต็อก / ตรวจนับ / ใบโอนคลัง **ไม่มี JE เลยสักเส้น** — doc-comment ของ `StockCountService` บอก "caller posts it" แต่ไม่มี caller ไหนทำ
- ไฟล์: `Inventory/StockCountService.cs:18` (`/// caller posts it via the existing JournalEntryService.`) · `:110-127` (CloseAsync — MoveAsync อย่างเดียว) · `ProductService.cs:203-247` (AdjustStockAsync — ไม่มี JE) · `:498-534` (ApplyStockCountAsync — ไม่มี JE) · caller: `Controllers/ProductController.cs:205`, `Controllers/SmeOperationsController.cs:114` (grep `Journal` ในสองไฟล์ = 0)
- ทำไมพัง: (1) นับสต็อกเจอของหาย 10 ชิ้น × 50 บาท ⇒ ledger ลด WarehouseStock/CurrentStock + movement ADJUST −10 (2) ไม่มี Dr ขาดทุนจากสินค้าสูญหาย / Cr 11500 ⇒ GL 11500 ยังถือ 500 บาทของของที่ไม่มีแล้ว (3) ทำซ้ำทุกรอบนับ ⇒ variance สะสม · `InventoryGlTieOutReport` (ProductService:576-590) จะฟ้อง diff ตลอดโดยผู้ใช้ไม่รู้ว่ามาจากไหน (4) เทียบกับ `UseSuppliesAsync:1017-1046` ที่ลง JE ครบ — กติกาเดียวกันสองมาตรฐานในไฟล์เดียว
- ผลกระทบ: งบแสดงสินค้าคงเหลือสูงเกินจริงเท่ากับของหาย/เสีย สะสมทุกงวด ⇒ กำไร/ภ.ง.ด.50 สูงเกิน · ของเกินจากนับ (found) ก็ไม่ลงรายได้อื่น
- defect class: "doc-comment ที่บอกว่าด่านอยู่ที่ X ≠ ด่านที่ถูกเรียก" · "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้"
- ทางแก้: ledger คืน `StockMoveResult.TotalCost` อยู่แล้ว → ผู้เรียก ADJUST ลง JE Dr 5xxxx ขาดทุนสินค้า / Cr 11500 (หรือกลับด้าน) ในธุรกรรมเดียว · ถ้าตั้งใจให้ผู้ใช้ลงเอง ต้องบอกที่หน้าจอ + ผูก `JournalEntryId` ไว้ (ห้าม silent)
- ความมั่นใจ: **สูง**

### E-08 [P1][S] `AdjustStockAsync` ทิ้งเครื่องหมายเมื่อชนิด = "ADJUST" ⇒ ผู้ใช้กรอก −5 (ของหาย) กลายเป็น +5 (รับเข้า)
- ไฟล์: `ProductService.cs:219` · UI `wwwroot/pages/products.html:190` (`<option value="ADJUST">ปรับยอด (ADJUST)`) · `:696` (`quantity:parseFloat(adjQty)` ส่งตรง)
- โค้ด: `var qty = request.MovementType == "OUT" ? -Math.Abs(request.Quantity) : Math.Abs(request.Quantity);`
- ทำไมพัง: (1) UI มี 3 ตัวเลือก IN/OUT/ADJUST และช่องจำนวนรับค่าลบได้ (2) เลือก ADJUST + −5 ⇒ `Math.Abs` ⇒ +5 ⇒ ledger บวกเข้า (3) movement ถูกเก็บชนิด "ADJUST" +5 ⇒ รายงาน (E-01 `+ TotalAdjust`) และ FIFO/WAC (E-02 ไม่รู้จัก ADJUST) เพี้ยนต่อ · เส้น OCR import (`OcrController.cs:902`) ส่ง "IN" จึงไม่โดน
- ผลกระทบ: ของหายกลายเป็นของเพิ่ม เงียบสนิท (toast "ปรับสต็อกสำเร็จ")
- defect class: "ค่า default ที่แต่งขึ้น" (Abs) · ปุ่มที่กดแล้วผลตรงข้ามเจตนา (ญาติ `toggleVatClaim`)
- ทางแก้: ADJUST ให้เคารพเครื่องหมายที่ผู้ใช้กรอก (หรือ UI บังคับเลือกทิศ) · ห้าม `Math.Abs` กับชนิดที่ทิศไม่แน่
- ความมั่นใจ: **สูง**

### E-09 [P1][M] นำเข้าสต็อกจากสแกน OCR (ไม่มี JE) แล้วสร้างใบแจ้งหนี้ซื้อจากสแกนเดียวกัน ⇒ ของเข้า 2 รอบ
- ไฟล์: `Controllers/OcrController.cs:723-727` (กัน import ซ้ำด้วย `StockImportedAt`) · `:896-906` (AdjustStockAsync "IN" ต่อบรรทัด — ไม่มี JE) · `:940` (`scan.StockImportedAt = DateTime.UtcNow`) · grep `StockImportedAt` ทั้งเรพ = มีแค่ OcrController + snapshot helper — **สายสร้างเอกสารจากสแกนไม่เคยอ่านธงนี้** · `document-scan.html:1442-1454` ปุ่ม "สร้างเอกสาร" เปิดตาม `createdDocumentId` เท่านั้น ไม่ดู `stockImportedAt`
- ทำไมพัง: (1) ผู้ใช้กด "นำเข้าสต๊อก" จากผลสแกน → stock +qty (ไม่มี JE) (2) กด "สร้างเอกสาร" → PI Draft → อนุมัติ → `ApplyStockMovementsAsync` PI standalone ⇒ **+qty อีกรอบ** + Dr 11500 (3) ธง `StockImportedAt` กันแค่การนำเข้าซ้ำ ไม่ได้กันเส้นเอกสาร และในทางกลับกันถ้าสร้างเอกสารก่อน ปุ่มนำเข้าสต็อกก็ยังกดได้ (ไม่ดู createdDocumentId)
- ผลกระทบ: สต็อกเบิ้ลบนใบที่ OCR อ่านได้ครบที่สุด (ยิ่งใช้ฟีเจอร์ครบยิ่งผิด)
- defect class: "ด่านที่เขียนไว้ครึ่งเดียว" · สองปุ่มที่ทำเรื่องเดียวกันคนละครึ่ง
- ทางแก้: ถ้า `StockImportedAt != null` ให้ PI ที่สร้างจากสแกนติดธง "ไม่ตัดสต็อก" (หรือให้การนำเข้าสต็อกออก GRN แทน AdjustStock เพื่อเข้าสาย 3-way ตามปกติ) · UI ซ่อน/เตือนปุ่มอีกฝั่ง
- ความมั่นใจ: **กลาง** — ต้องเช็ค `OcrService.CreateDocumentFromScan*` ว่ามีการอ่าน `StockImportedAt` ในชื่ออื่นไหม (grep ตรงชื่อ = ไม่มี)

### E-10 [P2][S] รับโอนคลังขาดจำนวน: ส่ง 100 รับ 90 ⇒ 10 ชิ้น "หายระหว่างทาง" ถาวรโดยไม่มี movement/JE/สถานะ · รับเกินได้ · บรรทัดที่ไม่มีในใบโอนถูกทิ้งเงียบ
- ไฟล์: `WarehouseService.cs:334-360` (`ReceiveTransferAsync`)
- โค้ด: `var transferLine = lines.FirstOrDefault(l => l.ProductId == receivedLine.ProductId); if (transferLine != null) { transferLine.ReceivedQuantity = receivedLine.ReceivedQuantity; … MoveAsync(+ReceivedQuantity) }` แล้ว `transfer.Status = "Received";` — ไม่เทียบ `ReceivedQuantity` กับ `Quantity` ไม่มี else
- ทำไมพัง: (1) TRANSFER_OUT −100 จากต้นทาง (CurrentStock บริษัท −100) (2) TRANSFER_IN +90 ⇒ CurrentStock −10 ถาวร (3) ไม่มีใบปรับ/JE ขาดทุนระหว่างทาง ไม่มีสถานะ "รับไม่ครบ" · รับ 110 ก็ผ่าน (สร้างของจากอากาศ) · ProductId ที่ไม่อยู่ในใบ → `if (transferLine != null)` ข้ามเงียบ
- ผลกระทบ: ของหายระหว่างสาขาไม่มีร่องรอยในบัญชี (ปลายทางรับ 11500 เท่าเดิม แต่ของจริงน้อยกว่า) · เป็นช่องรั่ว (shrinkage/ทุจริต) ที่รายงานไม่ฟ้อง
- defect class: "silent no-op" · "สถานะปลายทางที่ผู้ใช้ไปต่อไม่ได้"
- ทางแก้: ถ้า Σ Received ≠ Σ Shipped ⇒ สถานะ `ReceivedWithVariance` + บังคับใบปรับ (ADJUST พร้อม JE ตาม E-07) · reject ProductId แปลก · block รับเกิน
- ความมั่นใจ: **สูง**

### E-11 [P2][S] รายงานกระทบยอด subledger สต็อก vs GL ใช้ `CurrentStock × CostPrice` และหาบัญชี prefix `113` ทั้งที่ระบบลง 11500/115 ด้วยต้นทุนถัวเฉลี่ย ⇒ variance ไม่มีวันเป็น 0
- ไฟล์: `Services/Implementations/SubLedgerReconciliationService.cs:125-140`
- โค้ด: `.Where(a => … a.AccountCode.StartsWith("113") && (a.AccountName.Contains("สินค้าคงเหลือ") …))` และ `var subBal = products.Sum(p => p.CurrentStock * p.CostPrice);`
- ทำไมพัง: (1) DocumentService/POS/GRN ลงสินค้าคงเหลือที่ `FindAccountAsync("11500") ?? "115"` (DocumentService.cs:12798, PosService.Orders.cs:1349) — ไม่ใช่ 113 ⇒ บัญชีที่รายงานเลือกมาเทียบไม่ใช่บัญชีที่มีการเคลื่อนไหวจริง (ถ้าผังไม่มี 113 ก็ไม่มีบรรทัดเลย) (2) มูลค่า subledger ต้องเป็น `Σ WarehouseStock × AverageUnitCost` (WAC) หรือ Σ layer (FIFO) ตาม `CostingMethod` ไม่ใช่ `CostPrice` (ราคาทุนตั้งต้น) — สำเนาสูตรที่สามที่ต่างจาก `ProductService.GetInventoryGlTieOutAsync` (:540-590 ใช้ movement IN)
- ผลกระทบ: เครื่องมือตรวจสอบที่ควรจับ E-05/E-06/E-07 กลับฟ้องผิดตลอด ⇒ ไม่มีใครเชื่อ ⇒ ไม่มีใครใช้
- defect class: สำเนามือ (R1) · hardcode รหัสผัง (`gl_code_check` ควรครอบ? — ตรวจต่อ)
- ทางแก้: resolver บัญชีสินค้าคงเหลือตัวเดียว (ตัวเดียวกับ `BuildPurchaseLineAccountResolverAsync`) + มูลค่าจาก `Product.InventoryAccountId` grouping × WAC
- ความมั่นใจ: **สูง** — ยืนยันแล้ว: ในผังของเรพ `113` คือ **ลูกหนี้การค้า** (`AccountingService.cs:1655, 2087-2090` ใช้ `StartsWith("113")` เป็น AR · `DatabaseMigrationHelper.cs:4275` "Default AR = 113 prefix") ส่วนสินค้าคงเหลือคือ `115xx`/`11500` (`ChartOfAccountTemplates.cs`) ⇒ filter `113 && ชื่อมี "สินค้า"` ได้ 0 บัญชี ⇒ **บรรทัดกระทบยอดสต็อกไม่ปรากฏเลย** (silent absence — ผู้ใช้ไม่รู้ว่ามันควรมี)

### E-12 [P2][S] `Product.SalesAccountId` / `PurchaseAccountId` เก็บได้ในทะเบียนสินค้า แต่**ไม่มีใครอ่าน**ตอนลงบัญชีขาย/ซื้อ
- ไฟล์: `Models/Entities/Product.cs:41-44` · ProductService.cs:113-114,182-183,1180-1181 (เก็บ/คืน) · DocumentService.cs:12890-12900 (`RevenueLegAccountId(l) => l.AccountId ?? defaultRevenue`) — grep `SalesAccountId` ใน DocumentService/documents.html = **0** · `PurchaseAccountId` อ่านที่เดียวคือ OcrService.cs:2577 (แนะนำผัง) — `BuildPurchaseLineAccountResolverAsync:12805-12810` ใช้แค่ `InventoryAccountId`
- ทำไมพัง: ผู้ใช้ตั้งบัญชีรายได้ต่อสินค้า (เช่น 41100 ขายสินค้า A / 42000 บริการ) ⇒ ทุกใบขายลง 41000 เหมือนกันหมด เว้นแต่เลือก AccountId รายบรรทัดเอง (ซึ่ง UI "ยังไม่ expose" ตาม comment :12866)
- ผลกระทบ: P&L แยกหมวดรายได้ไม่ได้แม้ตั้งค่าไว้ · ผู้ใช้เข้าใจว่าตั้งแล้วมีผล (silent no-op)
- defect class: "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" · "ห้าม silent no-op"
- ทางแก้: `CreateDocumentAsync` เติม `line.AccountId ??= product.SalesAccountId` (ขาย) / `PurchaseAccountId` (ซื้อ non-stock) เมื่อ ProductCode ตรง — หรือถอดช่องออกจาก UI จนกว่าจะต่อสาย
- ความมั่นใจ: **สูง**

### E-13 [P2][S] ลบสินค้าได้ทั้งที่ยังมีสต็อก/มูลค่าใน GL — soft-delete แล้วหายจากทุกรายงานและ reconcile แต่ 11500 ยังถือมูลค่า
- ไฟล์: `ProductService.cs:192-199` (`DeleteAsync`: `product.IsDeleted = true;` ไม่ตรวจอะไร) · ledger `ReconcileProductTotalsAsync:227` และรายงานทุกตัวกรอง `!p.IsDeleted`
- ทำไมพัง: สินค้าคงเหลือ 50 ชิ้น ลบทิ้ง ⇒ หายจากรายงานมูลค่า/ tie-out ⇒ GL 11500 กับ subledger ต่างกันเท่ามูลค่านั้นถาวร โดยไม่มี movement/JE อธิบาย
- ทางแก้: block ถ้า `CurrentStock != 0` หรือมี WarehouseStock ≠ 0 พร้อมบอกให้ปรับสต็อกออก (มี JE ตาม E-07) ก่อน
- ความมั่นใจ: **สูง**

### E-14 [P3][S] ledger ขาเข้าที่ต้นทุน 0 (ของแถม/รับบริจาค) ตกไปใช้ต้นทุนถัวเฉลี่ยเดิม ⇒ มูลค่าสต็อกบวมโดยไม่มี JE รองรับ
- ไฟล์: `StockLedger.cs:143-170` — `if (r.UnitCostOverride is decimal receipt && receipt > 0m) {…} else { unitCost = product.AverageUnitCost > 0m ? product.AverageUnitCost : product.CostPrice; }` · UI products.html:696 คอมเมนต์เอง `/* 0 = ของแถม/รับบริจาค */`
- ทำไมพัง: ผู้ใช้ตั้งใจส่ง 0 แต่ `> 0m` ทำให้ 0 ถูกตีเป็น "ไม่ได้ส่ง" ⇒ movement stamp ต้นทุนเฉลี่ย และ WAC ไม่ถูกเฉลี่ยลง (ของฟรี 100 ชิ้นควรดึงต้นทุนเฉลี่ยลงครึ่งหนึ่ง)
- ทางแก้: แยก `null` (ไม่รู้) จาก `0m` (รู้ว่าฟรี) — ญาติของกฎ "ค่าที่แปลว่ายังไม่ระบุต้องแยกจากค่าที่มีความหมายจริง"
- ความมั่นใจ: **สูง**

### E-15 [P3][S] `RebuildAverageCostAsync` ถูกเรียกกลาง void transaction แล้ว `SaveChangesAsync` เอง + ล้มเหลวแล้ว `LogWarning` เดินต่อ
- ไฟล์: `DocumentService.cs:12475-12486` (`try { await _inventoryCosting.RebuildAverageCostAsync(product.Id); } catch (Exception ex) { _logger.LogWarning(…) }`) · `InventoryCostingService.cs:225` (`await _db.SaveChangesAsync(ct)`) · เทียบคำเตือนในตัว ledger เองที่ห้ามเรียก `RegisterReceiptAsync` ด้วยเหตุผลเดียวกัน (StockLedger.cs:140-146)
- ผลกระทบ: void ใบซื้อสำเร็จแต่ WAC ค้างค่าเดิม ⇒ COGS ทุกใบถัดไปผิด — ร่องรอยอยู่ใน log เท่านั้น (กฎ "LogWarning แล้วเดินต่อ = กลืน error") · รวมกับ E-02 (rebuild ไม่รู้จัก OPENING) ทำให้ผลลัพธ์ผิดแม้ไม่ throw
- ความมั่นใจ: สูง

## สรุป 5 บรรทัด
1. เฟส 0 ("หนึ่งความจริงของสต็อก" ผ่าน `IStockLedger`) ทำได้ดีที่ **ฝั่งเขียน** — แต่**ฝั่งอ่าน**ไม่ถูกแก้ตาม: รายงานสต็อกทุกตัวยัง `In − Out` ทั้งที่ OUT เก็บเป็นลบแล้ว (E-01) และ costing/รายงานรู้จักแค่ IN/OUT/ADJUST ขณะที่ ledger ถูกป้อน OPENING/TRANSFER_* (E-02) ⇒ ยอดยกมาหาย · COGS ผิดทิศ
2. ยังมีผู้เขียนสต็อกที่หลุด checker อยู่ 1 ตัว: `StockTransferController` (`_db.Set<StockMovement>().Add`) ที่หน้า `sme-config.html` เรียกจริง — เขียน movement แต่ไม่แตะยอดคลัง = สองความจริงกลับมา (E-04)
3. สาย 3-way match รั่วตรง "PO → PI ตรง" ที่เปิดคู่กับ "PO → GRN" โดยนับ fulfilment คนละแกน ⇒ ของเข้า 2 รอบ · 21240 ไม่มีใครล้าง (E-05) — เป็นเส้นที่ผู้ใช้เดินทุกวัน
4. **ไม่มี JE** ในปรับสต็อก/ตรวจนับ/รับโอนขาด (E-07, E-10) และ consignment ไม่ลง COGS + VAT 0 (E-06) ⇒ GL 11500 กับ subledger ห่างกันขึ้นทุกงวด ขณะที่เครื่องมือกระทบยอดที่มีอยู่ (E-11) หาบัญชีผิด prefix จึงไม่มีวันฟ้อง
5. ด่านสต็อกติดลบทำงาน**สองมาตรฐาน** (เอกสารข้ามเสมอ · POS บล็อกทั้งบิลแม้ดีไซน์บอกห้ามบล็อก) (E-03) · ของที่ตั้งค่าได้แต่ไม่มีใครอ่าน: `Product.SalesAccountId/PurchaseAccountId` (E-12) · `AllowNegativeStock` ฝั่งเอกสาร (E-03)

## ตรวจแล้วไม่ใช่บั๊ก (ทีมอื่นไม่ต้องซ้ำ)
- `StockLedger.MoveAsync`: ล็อก `pg_advisory_xact_lock` ต่อ (คลัง, สินค้า) ด้วย `AdvisoryLockKey` ✅ · `SetAbsolute` แปลงยอดนับเป็นผลต่างของคลังนั้น ✅ · WAC ผ่าน `WeightedAverageCost.Next` ก่อนบวก delta ✅ · `CurrentStock += delta` = Σคลัง (มี `ReconcileProductTotalsAsync` — แต่**ไม่มี job/endpoint ไหนเรียก** นอก migration comment; ไม่นับเป็นบั๊กเพราะ invariant ยังถือได้ตราบที่ไม่มีผู้เขียนนอก ledger — ซึ่ง E-04 ทำลายไปแล้ว)
- `DocumentService.ApplyStockMovementsAsync` (:12440-12660): void กลับ movement ตามคลังต้นทางที่ต้นทุนเดิม ✅ · DN ไม่ตัดสต็อก (ตั้งใจ) · PI ที่อ้าง GRN ไม่ตัดซ้ำ ✅ · CN Return/DN ExtraGoods ทิศถูก รวมฝั่งซื้อ ✅ · CN อ้าง PV/Receipt/RV ไม่แตะสต็อก ✅ · IsDeposit ข้าม ✅ · landed cost เกลี่ยตามมูลค่า ✅ · COGS ใบขายและ CN ผ่าน `ComputeSalesCogsAsync(outbound:false)` (:13425) ✅
- 3-way match ฝั่ง PI ที่อ้าง GRN: บล็อกบิลเกิน qty/amount รวมใบอื่นที่บิลไปแล้ว (:4906-4946) ✅ · PO→GRN บางส่วนถูกกัน over-receipt ด้วย `ComputeConsumption` แกน Delivery (:9515-9525) ✅ · GRN ไม่มี PO อนุญาต (ตั้งใจ) · GRN โยนแทน return เงียบเมื่อสร้าง 21240 ไม่ได้ ✅
- POS: ขาย/sync ออฟไลน์/void/refund เดินผ่าน ledger + BOM (`ApplyRecipeConsumptionAsync`) ครบ 4 เส้น ✅ · refund คิด `discountFactor` ระดับบิล + JE กลับ Dr รายได้/ภาษีขาย Cr เงินสด + Dr 11500/Cr COGS ✅ · void กลับ JE ผ่าน `ReverseJournalEntryAsync` ✅ · หัวสลิป/เลขอย่างย่อผ่าน `PosSlipHeader.Resolve` ที่อ่าน `IsRetailApproved+PhoR06ApprovedDate` (PosService.Orders.cs:1180-1192) ✅ · `IssueTaxInvoiceAsync` สร้าง Document Approved ตรง (ไม่ผ่าน Approve ⇒ ไม่ตัดสต็อกซ้ำ) ✅ · แยกบิลสืบทอด % (CLAUDE.md ยืนยันแล้ว — ไม่ได้อ่านซ้ำ)
- LIFO: enum `CostingMethod` ไม่มี (AllEnums:367-380) ✅ (ซ้ำ G)
- Production `CompleteAsync`: เบิก component ที่คลังของใบผลิต · รับ FG ที่ต้นทุนรวม/หน่วย ผ่าน ledger ✅ (ไม่มี JE — ถูกถ้า RM/FG อยู่ 11500 เดียวกัน; ดูข้อเสนอ ERP)
- `UseSuppliesAsync` ลง JE Dr ค่าวัสดุ / Cr วัสดุ ✅ (มาตรฐานที่ E-07 ควรทำตาม)
- `WarehouseService.ShipTransferAsync` ตรวจ `AvailableQuantity` ของคลังต้นทางก่อนส่ง ✅ · `VoidTransferAsync` คืนของกลับต้นทาง ✅

## ซ้ำกับ SYSTEM_REVIEW (ID เดิม + ยังเปิดอยู่จริงไหม)
- **H-A8** (POS เขียน CurrentStock ตรง / FIFO ไม่ผ่าน costing) — **ปิดแล้วจริงโดยเฟส 0** (PosService.Orders.cs ทุกจุดเรียก `_stock.MoveAsync` ไม่ส่ง override ⇒ FIFO/WAC ผ่าน costing) แต่ในไฟล์ยังไม่ติ๊ก ✅ — ควรติ๊ก (คอมมิต 2621de2/3d58bb0 ช่วง POS เฟส 0-3) · ผลข้างเคียงใหม่คือ E-03 (ด่านติดลบกลับมาบล็อก POS)
- **H-A9** (การขายไม่แตะ WarehouseStock) — **ปิดแล้ว** โดย ledger; ยังไม่ติ๊ก · ยกเว้น E-04 ที่หลุด
- **H-A7** (ปิดกะไม่ลง JE เงินขาด/เกิน) — **ยังเปิดอยู่จริง**: `PosService.cs:169-178` เก็บ `ExpectedBalance/ClosingBalance` ไม่มี JE
- **H-A15** (JE ไม่ติด BranchId) — POS ปิดแล้ว (`BranchId: order.BranchId` :1362) · ฝั่ง DocumentService ไม่ได้ตรวจซ้ำ
- **A-06 / A-07** (ทีม A รอบนี้) — E-05 ต่อยอด: UI `documents.html:11161` มี `PurchaseOrder: ['GoodsReceiptNote','PurchaseInvoice']` ทั้งคู่ ⇒ ถ้า A-07 หมายถึงจุดอื่น ให้เทียบกัน; ไม่ว่าทางไหน PO→PI ตรงยังเปิด
- **§9 ไม่ใช่บั๊ก** — ไม่มีข้อที่ทับกับ finding ของทีม E

## ยังไม่ได้อ่าน
- `CmsCommerceService.cs:1230-1320, 1600-1620` (stock ฝั่งเว็บ — ทีม H ตรวจแล้วรอบก่อน) · `ImportExportService.cs:1081` (import stock อีกจุด) · `OcrService` สายสร้างเอกสารจากสแกน (E-09 ต้องยืนยัน) · `DocumentService` PO/PR approve + budget commitment · `InventoryReorderForecastService` · `ProductService` GetInventoryGlTieOutAsync รายละเอียด · หน้า `inventory-reports.html`/`warehouses.html` เชิงตรรกะ · lot/expiry (`StockTransferController.expiring-lots`) · `PosService.Packages.cs` · `ECommerceService`

## ข้อเสนอเชิง ERP (สิ่งที่ขาดถ้าจะเป็น ERP ครบ — แยกจากบั๊ก)
1. **สถานะ PO/SO**: `DocumentStatus` ไม่มี Open/PartiallyReceived/FullyReceived/Closed — วันนี้รู้ได้แค่จาก endpoint fulfilment ต่อใบ; ควรมี derived status + ปิด PO อัตโนมัติเมื่อรับ+บิลครบ และ "ปิดส่วนที่เหลือ" ด้วยมือ
2. **การจอง (Reservation/ATP)**: `WarehouseStock.ReservedQuantity` มีคอลัมน์แต่**ไม่มีใครเขียน** (grep = ledger init 0 เท่านั้น) — SO/ใบเสนอราคาที่ยืนยันแล้วไม่จองของ ⇒ ขายซ้อนได้
3. **หน่วยนับหลายระดับ**: `UnitConversion` มี CRUD แต่บรรทัดเอกสาร/POS ไม่แปลงหน่วย (ขาย 1 ลัง ตัด 1 หน่วยฐาน) — ต้องเก็บ `BaseQuantity` บนบรรทัด
4. **ราคาหลายระดับ / PriceList ต่อสาขา-ลูกค้า**: ไม่มี entity เลย (POS_MULTI_BRANCH เฟส 6 ค้าง)
5. **Costing ต่อคลัง** vs ระดับบริษัท (ข้อสรุป 2 ของแผน POS) — ตอนนี้ WAC เป็นระดับบริษัท ต้นทุนต่างสาขาถูกเฉลี่ย
6. **LCNRV / ค่าเผื่อสินค้าเสื่อม** (TFRS NPAEs บทที่ 8) — ไม่มี (H ระบุแล้ว) · **สินค้าหมดอายุ/lot FEFO**: lot มีบน movement แต่ไม่มีการเลือก lot ตอนขาย
7. **WIP/ต้นทุนแปรสภาพ** ในการผลิต: `ProductionOrder` คิดแค่วัตถุดิบ ไม่มีค่าแรง/โสหุ้ย/ของเสีย (`WastePercent` ที่ดีไซน์ไว้ไม่มีในโค้ด) และไม่มี JE RM→WIP→FG
8. **ใบปรับปรุงสต็อกเป็นเอกสาร** (มีเลขที่ · อนุมัติ · JE · เหตุผล) แทน endpoint ปรับตรง — จะปิด E-07/E-08/E-10 พร้อมกันและเข้าด่านสิทธิ์ (`write_permission_gate_check` ยังไม่ครอบ ProductController/SmeOperationsController)
9. **Consignment ทั้งสองทิศเป็น off-balance subledger ต่อคลัง** + ออกเอกสารผ่าน `IDocumentService` (E-06)
10. **Backorder / Drop-ship / RMA (ใบรับคืนสินค้า)** — วันนี้การรับคืนผูกกับ CN Return เท่านั้น ไม่มีขั้นตรวจสภาพ/แยกของเสีย
11. **Inventory period lock**: ปิดงวดแล้ว movement ยังลงย้อนหลังได้ (MovementDate จากผู้เรียก) — ควรผูกกับด่านงวดปิดเดียวกับ JE
12. **Checker**: ขยาย `stock_writer_check` ให้จับ `Set<StockMovement>()` + เพิ่ม `movement_type_vocab_check` (string literal ชนิด movement ต้องมาจาก `MovementTypes` ที่เดียว)
