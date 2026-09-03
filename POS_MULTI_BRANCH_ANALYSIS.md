# POS_MULTI_BRANCH_ANALYSIS.md — POS หลายสาขา + วัตถุดิบ (กรณีร้านชานมไข่มุก)

> วิเคราะห์ 2026-09-03 · ทุกข้อเท็จจริงในโค้ดตรวจด้วย `grep`/อ่านไฟล์จริง (มี file:line)
> · ส่วน "ทีมถกเถียง" คือมุมมอง 5 ด้านที่นำมาเถียงกันก่อนสรุปแบบเดียวกับ
> `LODGING_LICENSING_PLAN.md` · เอกสารนี้เขียนให้ Opus ลงมือพัฒนาต่อได้โดยไม่ต้องไล่ใหม่

---

## 0. คำตอบสั้น ๆ

**ยังไม่ครบ — ใช้ขายหน้าร้านได้ แต่ "หลายสาขา" กับ "วัตถุดิบ" ยังไม่มีจริง**

| คำถามของเจ้าของร้าน | สถานะวันนี้ |
| --- | --- |
| เปิด POS ที่สาขา A, B, C พร้อมกันได้ไหม | ✅ เปิดได้ (สร้าง `PosTerminal` หลายเครื่อง) — แต่ระบบ**ไม่รู้ว่าเครื่องไหนอยู่สาขาไหน** (`Location` เป็นข้อความอิสระ ไม่ใช่ FK ไป `Branch`) |
| ขายชานม 1 แก้ว → ตัดชา/นม/ไข่มุก/แก้ว/หลอด อัตโนมัติ | ❌ ไม่มี — POS ตัดสต็อกเฉพาะ "สินค้าที่ขาย" ตัวเดียว (`product.CurrentStock -= qty`) ไม่มีสูตร (recipe) ผูกกับการขาย |
| สต็อกวัตถุดิบแยกรายสาขา | ❌ `Product.CurrentStock` เป็น**ตัวเลขเดียวทั้งบริษัท** · ตาราง `WarehouseStock` มีอยู่แต่เป็น**ระบบคู่ขนานที่ POS/ซื้อ/รับสินค้าไม่เคยแตะ** |
| โอนวัตถุดิบจากครัวกลาง → สาขา | ⚠️ มีหน้าโอนคลัง (`warehouse.html`) แต่โอนใน `WarehouseStock` ซึ่งไม่ใช่ตัวเลขที่ POS ใช้ตัด ⇒ โอนแล้วยอดที่ POS เห็นไม่ขยับ |
| ยอดขาย/กำไรรายสาขา | ❌ รายงานมีแค่ `GetDailySummaryAsync(companyId, date)` ทั้งบริษัท · JE จาก POS ไม่มีมิติสาขา |
| ราคาต่างกันตามสาขา (ห้าง vs ตลาด) | ❌ ไม่มี price list |
| พนักงานสาขา A เปิดเครื่องสาขา B ไม่ได้ | ❌ ไม่มี scope สาขาบนผู้ใช้/บทบาท |
| ใบกำกับภาษีอย่างย่อบนสลิปมีรหัสสาขาถูกต้อง (§86/4, §86/6) | ❌ สลิป POS พิมพ์หัว "ใบเสร็จรับเงิน / ใบกำกับภาษีอย่างย่อ" **โดยไม่ตรวจ ภ.พ.06 และไม่มีรหัสสาขา** · ใบกำกับเต็มรูปจาก POS ไม่ตั้ง `Document.BranchId` |

สรุปสถาปัตยกรรม: ของที่ต้องมีอยู่ครบเกือบทุกชิ้น (`Branch` · `Warehouse.BranchId` ·
`WarehouseStock` · `StockTransfer` · `BillOfMaterials` · `Document.BranchId` · KDS ·
modifier) แต่ **ไม่มีเส้นสายเชื่อมกัน** — เป็น defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้"
ที่ CLAUDE.md บอกว่าใหญ่ที่สุดในเรพนี้ ปรากฏพร้อมกัน 4 จุดในโมดูลเดียว

---

## 1. ภาพวันทำงานจริงของลูกค้าเป้าหมาย

ร้านชานมไข่มุก "บริษัท ชาดี จำกัด" · 5 สาขา (3 ในห้าง 2 ตลาดนัด) · ครัวกลาง 1 แห่ง
ต้มไข่มุก/ชงชาเข้มข้นแล้วส่งสาขาทุกเช้า · จด VAT · ขายเฉลี่ย 300 แก้ว/สาขา/วัน

```
06:00 ครัวกลาง   ผลิตไข่มุก 20 กก. ชาเข้มข้น 30 ลิตร → ตัดใบชา/แป้ง/น้ำตาล
07:30 ครัวกลาง   ส่งสาขาละ 4 กก. + 6 ลิตร + แก้ว 400 + หลอด 400  (ใบโอน)
09:00 สาขา       รับของ นับตรงไหม · เปิดกะ POS · แคชเชียร์ล็อกอิน
09:05–21:00      ขาย 300 แก้ว · แต่ละแก้วกิน ชา 200ml นม 50ml ไข่มุก 50g แก้ว 1 หลอด 1
                 ท็อปปิ้ง (modifier) +บุก/+ครีมชีส กินวัตถุดิบเพิ่ม
                 จ่ายเงินสด 40% · PromptPay 55% · บัตร 5%
14:00 สาขา       ไข่มุกใกล้หมด → ขอเบิกเพิ่มจากครัวกลาง / ซื้อฉุกเฉินร้านใกล้
21:00 สาขา       ปิดกะ นับเงินสด · X/Z report · นับสต็อกคงเหลือ (ไข่มุกทิ้งทุกวัน = waste)
สิ้นวัน  เจ้าของ  ดูยอดขาย 5 สาขาเทียบกัน · สาขาไหน waste สูง · สาขาไหนต้นทุนวัตถุดิบ/แก้ว สูงผิดปกติ
สิ้นเดือน บัญชี  P&L รายสาขา · ภ.พ.30 รวม (สาขาเดียวกันทางภาษี หรือแยก ภ.พ.30 รายสาขาถ้าจดแยก)
                 · ใบกำกับอย่างย่อจากทุกสาขาต้องมีรหัสสาขาของตัวเอง
```

**เส้นที่ระบบวันนี้เดินได้**: 09:00 เปิดกะ · ขาย · จ่ายเงิน 3 ทาง · KDS · ปิดกะ X/Z ·
ออกใบกำกับเต็มรูปให้ลูกค้าที่ขอ · JE ขายลงบัญชี
**เส้นที่เดินไม่ได้**: ทุกอย่างที่มีคำว่า "สาขา" หรือ "วัตถุดิบ"

---

## 2. ข้อเท็จจริงในโค้ด (ตรวจแล้ว)

### 2.1 สาขา — มีตาราง แต่ POS ไม่รู้จัก

| ที่ | ข้อเท็จจริง |
| --- | --- |
| `Models/Entities/Pos.cs:7-13` | `PosTerminal` มี `Location` (string) **ไม่มี `BranchId`/`WarehouseId`** |
| `Pos.cs:36-52` | `PosOrder` → `SessionId` → `Terminal` — ไม่มีสาขาในสายนี้เลย |
| `DimensionalAccounting.cs:37-55` | `Branch` มีครบ: `TaxBranchCode` (§86/4) · `DimensionId` (มิติบัญชี) · ที่อยู่ |
| `AdvancedOperations.cs:324-333` | `Warehouse.BranchId` **มีอยู่แล้ว** — คลังผูกสาขาได้ |
| `Payroll.cs:43` | `Employee.BranchId` มี · แต่ `User`/`CompanyUser`/`Role` **ไม่มี** scope สาขา |
| `PosService.Orders.cs` (grep `BranchId`/`Dimension`) | **0 จุด** — JE ขาย/คืนเงิน ไม่ติดมิติสาขา ⇒ P&L รายสาขาทำไม่ได้ |
| `PosService.Orders.cs:395-445` (`IssueTaxInvoiceAsync`) | สร้าง `Document` โดย**ไม่ตั้ง `BranchId`** ⇒ resolver `DocumentIssuerIdentity` ตกไปสำนักงานใหญ่เสมอ = รหัสสาขาบนใบกำกับผิดสำหรับทุกใบที่ออกจากสาขา |
| `pos.html:2250` | สลิปพิมพ์ "ใบเสร็จรับเงิน / ใบกำกับภาษีอย่างย่อ" — grep `IsRetailApproved`/`PhoR06` ใน PosService = **0** ⇒ พิมพ์คำว่า "ใบกำกับภาษีอย่างย่อ" โดยไม่มีด่านอนุมัติ ภ.พ.06 และไม่มีรหัสสาขา (§86/6 บังคับ) |
| `IPosService` | รายงาน: `GetDailySummaryAsync(companyId, date)` · `GetZReportAsync` · `GetXReportAsync` — ไม่มีพารามิเตอร์สาขา/เครื่อง |
| `PosService.Orders.cs:1508-1530` | บัญชีรับเงินเลือกจาก `PaymentMethod` เท่านั้น (เงินสด 1011 / โอน 1012 / บัตร 1131) — สาขาที่มีบัญชีธนาคารคนละบัญชีลงบัญชีเดียวกันหมด |

### 2.2 สต็อก — สองระบบที่ไม่คุยกัน

| ที่ | ข้อเท็จจริง |
| --- | --- |
| `Product.cs:44` | `Product.CurrentStock` = ตัวเลขเดียวต่อสินค้า**ทั้งบริษัท** |
| `PosService.Orders.cs:555-575` (และอีก 3 จุด) | ขาย/คืน/void: `product.CurrentStock ± qty` + `StockMovement` **โดยไม่ระบุ `WarehouseId`** (ฟิลด์มีแต่ไม่ใส่) |
| `DocumentService.cs` (grep `Warehouse`) | **0 จุด** — รับสินค้า (GRN)/ซื้อ ไม่แตะคลังเลย เข้าที่ `CurrentStock` รวม |
| `AdvancedOperations.cs:341-357` | `WarehouseStock` (คลัง×สินค้า) — เขียนโดย `WarehouseService` **ตัวเดียว** (โอนคลัง) |
| `WarehouseService.cs:156-353` | สร้าง/ส่ง/รับใบโอน — เดินบน `WarehouseStock` **ไม่แตะ `Product.CurrentStock` และไม่เขียน `StockMovement`** |
| ผลรวม | โอนไข่มุก 4 กก. ครัวกลาง→สาขา A: `WarehouseStock` ขยับ แต่ยอดที่ POS ตัดตอนขาย (`CurrentStock`) ไม่เกี่ยวอะไรกับคลังไหนเลย ⇒ **สองตัวเลขที่ไม่มีวันตรงกัน** |
| `Product.cs:168-184` | `StockCount.WarehouseId` มี (nullable) — นับสต็อกต่อคลังได้ แต่ปรับยอดลงตัวไหน? |
| `ProductType` enum `:558-564` | `Product / Service / NonStock / Supplies` — **ไม่มี "วัตถุดิบ" (RawMaterial)** · `Supplies` = วัสดุสิ้นเปลืองที่ลงค่าใช้จ่ายทันที ไม่ใช่ต้นทุนขาย |

### 2.3 สูตร/วัตถุดิบ — มี BOM แต่ใช้ผลิตล่วงหน้าเท่านั้น

| ที่ | ข้อเท็จจริง |
| --- | --- |
| `BankAccount.cs:557-580` (ไฟล์ชื่อผิดที่ แต่ entity อยู่ตรงนี้จริง) | `BillOfMaterials` + `BomLine(ComponentProductId, QuantityPerParent)` — โครงสร้างสูตร**มีแล้ว** |
| `Production/ProductionOrderService.cs:121-170` | `CompleteAsync` ตัด component ตาม BOM + รับ parent เข้าสต็อก (make-to-stock) — **ไม่มีคลัง** (ProductionOrder ไม่มี `WarehouseId`) |
| `PosService*.cs` (grep `Bom`/`Recipe`) | **0 จุด** — การขายไม่เคยอ่าน BOM |
| `Pos.cs:257-296` | `ProductModifierOption` มีแค่ `PriceAdjustment` — ท็อปปิ้ง "+ไข่มุกเพิ่ม" ไม่ผูกวัตถุดิบ |
| `pos-kds.html` | ครัวเห็นออเดอร์ (ใช้กับบาร์ชงได้เลย) |

### 2.4 สิ่งที่ใช้ได้ดีอยู่แล้ว (ไม่ต้องทำใหม่)

- `PosBusinessMode.Cafe` มีอยู่ · modifier group/option (ระดับหวาน/ท็อปปิ้ง) · KDS · X/Z report ·
  offline sync (`SyncOfflineOrderAsync`) · คูปอง/ทิป/รวมโต๊ะ · คืนเงินบางส่วนพร้อม JE กลับ ·
  ใบกำกับเต็มรูปจากออเดอร์ · `InventoryCostingService` (ถัวเฉลี่ย/FIFO) · `StockTransfer`
  มี lot/serial · `Branch` มีมิติบัญชี · `Document.BranchId` + `IssuerBranchCode` snapshot
  (รอบ 116) · `TaxBranchCode` resolver `Helpers/TaxBranchCode.cs`

---

## 3. ตารางความพร้อม (capability matrix)

| # | ความสามารถ | สถานะ | ต้องทำ |
| --- | --- | --- | --- |
| 1 | เครื่อง POS ผูกสาขา + คลังตั้งต้น | ❌ | `PosTerminal.BranchId` + `WarehouseId` (FK) — ห้ามใช้ `Location` string ต่อ |
| 2 | ขายแล้วตัด**คลังของสาขา** | ❌ | ทุก `StockMovement` จาก POS ต้องมี `WarehouseId` = คลังของเครื่อง |
| 3 | สต็อกต่อคลังเป็น**ความจริงหนึ่งเดียว** | ❌ | ยุบ `Product.CurrentStock` ให้เป็น**ผลรวม**ของ `WarehouseStock` (ห้ามเป็นตัวเลขอิสระ) · ทุกผู้เขียนสต็อก (POS/GRN/โอน/นับ/ผลิต) เขียน `WarehouseStock` + `StockMovement(WarehouseId)` ผ่านตัวเดียว |
| 4 | สูตร: ขาย 1 หน่วยกินวัตถุดิบตาม BOM | ❌ | `Product.ConsumesBomOnSale` + POS ตัด component ตาม `BomLine` ณ คลังของเครื่อง · modifier option ผูก BOM ย่อยได้ |
| 5 | ชนิดสินค้า "วัตถุดิบ" | ❌ | `ProductType.RawMaterial` (ต้นทุนขายผ่านสูตร ไม่ใช่ค่าใช้จ่ายทันทีแบบ Supplies) |
| 6 | ครัวกลางผลิต → โอนสาขา | ⚠️ | `ProductionOrder.WarehouseId` (รับ parent เข้าคลังครัวกลาง) + ใบโอนที่แตะสต็อกจริง (ข้อ 3) |
| 7 | ราคาต่อสาขา | ❌ | `PriceList(BranchId?)` + `PriceListItem` · resolver ราคา: สาขา → บริษัท |
| 8 | สิทธิ์ผู้ใช้ต่อสาขา | ❌ | `CompanyUser.AllowedBranchIds` (ว่าง = ทุกสาขา) · เปิดกะได้เฉพาะเครื่องในสาขาที่มีสิทธิ์ |
| 9 | ยอดขาย/กำไร/waste รายสาขา | ❌ | รายงานรับ `branchId`/`terminalId` + JE ขาย/COGS ติด `Branch.DimensionId` |
| 10 | ใบกำกับอย่างย่อ §86/6 บนสลิป | ❌ | ด่าน `Company.IsRetailApproved`+`PhoR06ApprovedDate` · หัว+รหัสสาขา+เลขรันต่อสาขา · ถ้าไม่ผ่าน ภ.พ.06 พิมพ์ "ใบเสร็จรับเงิน" เท่านั้น |
| 11 | ใบกำกับเต็มรูปจาก POS มีรหัสสาขาถูก | ❌ | `IssueTaxInvoiceAsync` ตั้ง `BranchId = terminal.BranchId` |
| 12 | บัญชีรับเงินต่อสาขา (ธนาคารคนละบัญชี) | ⚠️ | `PosTerminal.CashAccountId/BankAccountId` override → `ResolvePaymentAccountAsync(terminal, method)` |
| 13 | นับสต็อกสิ้นวัน + waste | ⚠️ | `StockCount.WarehouseId` มีแล้ว · เพิ่มเหตุผลผลต่าง (`Waste/Spoilage/Theft/CountError`) → JE waste แยกบัญชี |
| 14 | เบิกวัตถุดิบระหว่างสาขา | ⚠️ | ใช้ `StockTransfer` เดิมหลังแก้ข้อ 3 |
| 15 | KDS/ระดับหวาน/ท็อปปิ้ง/คูปอง/X-Z | ✅ | ใช้ได้ |

---

## 4. ทีมถกเถียง (5 มุมมอง)

### A — เจ้าของร้าน 5 สาขา
> "ผมไม่สนว่าระบบเก็บสต็อกกี่ตาราง ผมอยากรู้ว่า **ไข่มุกหายไปไหน** สาขาไหนใช้ไข่มุกต่อแก้ว
> เยอะกว่าปกติ = พนักงานตักเยอะ หรือขโมย · และอยากเห็นยอด 5 สาขาบนจอเดียวตอน 21:30"
- ต้องการ: ยอดขาย/แก้ว/ต้นทุนวัตถุดิบต่อแก้ว/waste **รายสาขา** · โอนของจากครัวกลางแล้วสาขา
  ยืนยันรับ · ราคาห้างแพงกว่าตลาด
- ไม่ต้องการ: กรอกอะไรเพิ่มที่หน้าร้าน — พนักงานลาออกบ่อย สอนยาก

### B — ผู้จัดการสาขา / แคชเชียร์
> "ตอนขายผมกดแก้วเดียว ระบบต้องรู้เองว่ากินอะไรบ้าง · ตอนของหมดกลางวันผมซื้อจากโลตัส
> ข้างร้าน ต้องบันทึกได้เร็ว ๆ · ตอนปิดร้านนับไข่มุกที่เหลือแล้วทิ้ง ต้องบันทึกเป็น waste
> ไม่ใช่ปล่อยให้สต็อกค้างเป็นของที่ไม่มีจริง"
- **โต้ A**: "waste รายวันของไข่มุกคือของจริง ถ้าระบบเอาไปตีความว่า 'พนักงานตักเยอะ' ทุกครั้ง
  ผมโดนสงสัยฟรี — ต้องแยก waste ที่นับได้ ออกจาก variance ที่หาสาเหตุไม่ได้"
- ต้องการ: นับสต็อกสิ้นวันบนมือถือ · เหตุผลผลต่างเลือกได้ · โอน/เบิกมีปุ่ม "รับของแล้ว"

### C — CPA / ภาษี
> "สลิปที่พิมพ์คำว่า 'ใบกำกับภาษีอย่างย่อ' โดยบริษัทยังไม่ได้อนุมัติ ภ.พ.06 คือ**ออกใบกำกับ
> โดยไม่มีสิทธิ์** (§86/6 · ประกาศอธิบดีฯ) และใบอย่างย่อทุกใบต้องมี**รหัสสาขา**ที่ออก —
> ตอนนี้ระบบพิมพ์คำนั้นบนสลิปทุกใบโดยไม่ตรวจอะไรเลย นี่คือความเสี่ยงที่ต้องปิดก่อนเรื่องสต็อก"
- **โต้ A/B**: "ต้นทุนวัตถุดิบต่อแก้วที่เจ้าของอยากเห็นจะ**ถูกต้องก็ต่อเมื่อ** วัตถุดิบเป็น
  RawMaterial ที่เข้าสินค้าคงเหลือ (บัญชี 1xxx) แล้วออกเป็นต้นทุนขายตอนขาย — ถ้าเก็บเป็น
  Supplies (ลงค่าใช้จ่ายทันทีตอนซื้อ) ต้นทุนต่อแก้วจะเป็นศูนย์ในเดือนที่ไม่ได้ซื้อ และงบ
  สิ้นเดือนผิดตาม TFRS for NPAEs บทที่ 8"
- **ภ.พ.30**: สาขาที่จดทะเบียนภาษีมูลค่าเพิ่มแยก ต้องยื่น ภ.พ.30 แยกสาขา (§83/4) — ระบบ
  ต้องรู้ว่าใบไหนออกจากสาขาไหน ซึ่งวันนี้ไม่รู้
- ต้องการ: ด่าน ภ.พ.06 · รหัสสาขาบนใบอย่างย่อ+เต็มรูป · เลขรันใบอย่างย่อ**ต่อสาขา**
  (gap-free ตามสาขา) · RawMaterial เป็นสินค้าคงเหลือ · waste เป็นบัญชีแยก (ตรวจสอบ
  ตาม §65 ตรี ได้ว่าเป็นของเสียตามปกติธุรกิจ)

### D — สถาปนิกระบบ
> "ปัญหาจริงคือมี**สองความจริงของสต็อก** — `Product.CurrentStock` กับ `WarehouseStock` —
> และ POS/ซื้อ/รับ เขียนตัวแรก ส่วนโอนคลังเขียนตัวหลัง ถ้าเพิ่มสาขาโดยไม่ยุบก่อน จะได้
> ความจริงที่สาม ต้องยุบให้เหลือหนึ่ง **ก่อน**เขียนฟีเจอร์ใหม่ทุกตัว"
- **โต้ C**: "การเพิ่ม RawMaterial เป็น ProductType ใหม่ง่าย แต่ตัวที่ยากคือ costing —
  `InventoryCostingService` ถัวเฉลี่ยต่อสินค้าทั้งบริษัท ถ้าสาขาห้างซื้อไข่มุกแพงกว่า
  ตลาด ต้นทุนถัวเฉลี่ยจะเป็นระดับบริษัท ไม่ใช่ระดับคลัง — ต้องตัดสินว่ายอมรับได้ไหม
  (TFRS NPAEs ให้ถัวเฉลี่ยระดับกิจการได้ ผมเสนอยอมรับ = ไม่ต้อง costing ต่อคลัง ลดงานครึ่ง)"
- **โต้ A**: "'ระบบรู้เองว่าแก้วหนึ่งกินอะไร' ต้องมีคนตั้งสูตรครั้งแรก — สูตรผิด = สต็อก
  เพี้ยนทุกแก้วเงียบ ๆ ต้องมีรายงาน variance (สูตรบอกใช้ X นับจริงเหลือ Y) ให้จับสูตรผิดได้"
- ต้องการ: `IStockLedger` ตัวเดียวที่ทุกผู้เขียนสต็อกต้องผ่าน (POS · GRN · โอน · นับ ·
  ผลิต · ปรับปรุง) — ห้ามมีใคร `CurrentStock -=` เองอีก · `CurrentStock` กลายเป็น
  derived (ผลรวมทุกคลัง) · advisory lock ต่อ (คลัง, สินค้า)

### E — Product / UX
> "ร้านชานม 1 สาขาที่เพิ่งเปิดต้องไม่ถูกบังคับให้เข้าใจคำว่า 'คลัง' — สาขาเดียว = คลังเดียว
> สร้างให้เองเงียบ ๆ · ฟีเจอร์หลายสาขาต้องโผล่**เมื่อมีสาขาที่สอง**เท่านั้น"
- **โต้ D**: "ยุบสต็อกเป็นตัวเดียว = migration ที่แตะทุกบริษัทที่ใช้อยู่ ต้องมีโหมดที่
  บริษัทเดิมไม่รู้สึกอะไรเลย: ทุกบริษัทมี 'คลังหลัก' อัตโนมัติ ยอดเดิมย้ายไปคลังนั้นทั้งก้อน"
- **โต้ C**: "ด่าน ภ.พ.06 ถูก แต่ห้ามบล็อกการขาย — ถ้ายังไม่อนุมัติให้พิมพ์หัว 'ใบเสร็จรับเงิน'
  แทนโดยอัตโนมัติ ไม่ใช่ error ตอนกดจ่ายเงิน (บทเรียนโควตาเอกสาร: ห้ามบล็อกเงินที่รับแล้ว)"
- ต้องการ: setup wizard "เพิ่มสาขา" ทำ 3 อย่างในคลิกเดียว (Branch + Warehouse ผูกกัน +
  Terminal ผูกทั้งคู่) · หน้า POS โชว์ชื่อสาขาชัด · dashboard 5 สาขาแบบการ์ด

### ข้อสรุปที่ทีมตกลงกัน

1. **ลำดับ**: ยุบสต็อกเป็นหนึ่งความจริง (D) → ผูกเครื่องกับสาขา/คลัง (A/E) → ด่านภาษี
   ใบอย่างย่อ+รหัสสาขา (C) → สูตร (A/B) → รายงานรายสาขา (A) → ราคา/สิทธิ์ต่อสาขา
2. **costing ระดับบริษัท ไม่ใช่ระดับคลัง** (D ชนะ C บางส่วน — TFRS NPAEs อนุญาต · ลดงานครึ่ง ·
   จดเป็นข้อจำกัดที่รู้ตัว) — ถ้าวันหนึ่งต้องการ ให้เพิ่ม `WarehouseStock.AverageUnitCost`
   โดยไม่ต้องรื้อ
3. **RawMaterial เป็นสินค้าคงเหลือ** (C ชนะ) — `Supplies` คงไว้สำหรับของที่ไม่เข้าต้นทุนขาย
   (กระดาษทิชชู่ น้ำยาล้าง) · ไข่มุก/ชา/นม/แก้ว/หลอด = RawMaterial
4. **waste กับ variance แยกกัน** (B ชนะ A) — waste ที่นับได้ตอนปิดร้าน = บัญชี "ของเสีย
   ตามปกติ" · ผลต่างที่เหลือหลังหัก waste = variance ให้เจ้าของไล่
5. **ห้ามบล็อกการขายด้วยด่านภาษี** (E ชนะ C) — ไม่ผ่าน ภ.พ.06 = หัวสลิปเป็น "ใบเสร็จรับเงิน"
   อัตโนมัติ + ป้ายเตือนเจ้าของ ไม่ใช่ error ที่แคชเชียร์
6. **สาขาเดียว = มองไม่เห็นคำว่าคลัง** (E ชนะ) — migration สร้าง "คลังหลัก" ให้ทุกบริษัท
   และย้าย `CurrentStock` เดิมเข้าไปทั้งก้อน

---

## 5. การออกแบบ

### 5.1 Data model (additive · `ADD COLUMN IF NOT EXISTS` · ห้าม EF Migrations)

```
PosTerminal      + BranchId (FK Branch, nullable ตอน migrate → บังคับหลัง backfill)
                 + WarehouseId (FK Warehouse) — คลังที่เครื่องนี้ตัดสต็อก
                 + CashAccountId? · BankAccountId? — override บัญชีรับเงินต่อเครื่อง/สาขา
                 + AbbreviatedInvoicePrefix? · AbbreviatedInvoiceNextSeq — เลขรันใบอย่างย่อ**ต่อสาขา**
PosOrder         + BranchId (snapshot จาก terminal ตอนเปิดออเดอร์ — ห้าม resolve สดตอนรายงาน
                   เพราะเครื่องย้ายสาขาได้) + WarehouseId (snapshot เดียวกัน)
                 + AbbreviatedInvoiceNumber? · IssuerBranchCode? (snapshot §86/6)
Product          + ConsumesBomOnSale (bool) — ขายแล้วตัด component แทนตัวเอง
ProductType      + RawMaterial = 5
ProductModifierOption + BomId? — ท็อปปิ้ง/เพิ่มไข่มุก ตัดวัตถุดิบเพิ่มตามสูตรย่อย
BillOfMaterials  + WastePercent (decimal, 0-100) — เผื่อหกหล่นตามปกติ (ถัวเข้าต้นทุน)
ProductionOrder  + WarehouseId (คลังที่รับ parent) + ComponentWarehouseId (คลังที่ตัด component)
StockCount       + VarianceReason enum {Waste, Spoilage, Theft, CountError, Unknown} ต่อบรรทัด
                 + WasteExpenseAccountId? (default 5xxxx "ของเสียตามปกติธุรกิจ")
PriceList        (ใหม่) Id · CompanyId · Name · BranchId? · IsActive · EffectiveFrom/To
PriceListItem    (ใหม่) PriceListId · ProductId · UnitPrice · (ModifierOptionId? · PriceAdjustment?)
CompanyUser      + AllowedBranchIdsJson? (null = ทุกสาขา)
```

**สิ่งที่ห้ามทำ**: ห้ามเพิ่ม `BranchId` ลง `StockMovement` (มี `WarehouseId` แล้วและ
`Warehouse.BranchId` บอกสาขาได้) — สองช่องที่ต้องตรงกันเสมอจะ drift แน่นอน

### 5.2 `IStockLedger` — ผู้เขียนสต็อกตัวเดียว (หัวใจของทั้งแผน)

```csharp
public interface IStockLedger
{
    /// ทุกการเคลื่อนไหวเดินผ่านนี่ — เขียน WarehouseStock + StockMovement(WarehouseId)
    /// + ปรับ Product.CurrentStock (= Σ WarehouseStock) ในธุรกรรมเดียว ภายใต้
    /// AdvisoryLockKey.For(companyId, StockAdjust, $"{warehouseId}:{productId}")
    Task<StockMovement> MoveAsync(StockMoveRequest r, CancellationToken ct = default);
}
public sealed record StockMoveRequest(
    Guid CompanyId, Guid WarehouseId, Guid ProductId, decimal Quantity /* + เข้า − ออก */,
    string MovementType, string Reference, Guid? DocumentId = null, Guid? PosOrderId = null,
    decimal? UnitCostOverride = null, string? Notes = null, string? CreatedBy = null);
```

- ผู้เรียก 8 ไฟล์ที่วันนี้ `StockMovements.Add` เอง (grep นับได้ 8) ต้องย้ายมาเรียกตัวนี้
  **ทั้งหมดในเฟสเดียว** — เหลือตัวเดียวที่เขียนเองไม่ได้ = กลับไปเป็นสองความจริงทันที
  → เพิ่ม `tools/stock_writer_check.py`: ฟ้องทุก `StockMovements.Add`/`CurrentStock +=`/`-=`
  ที่อยู่นอก `StockLedger.cs` (shape-based · negative test ด้วยการใส่กลับ 1 จุด)
- `WarehouseService` โอนคลัง → `MoveAsync(OUT จากคลังต้นทาง)` + `MoveAsync(IN คลังปลายทาง)`
  พร้อม `TransferPairId` เดียวกัน (ฟิลด์มีอยู่แล้ว `StockMovement.TransferPairId`)
- **Migration**: ทุกบริษัทได้ `Warehouse` "คลังหลัก" (`IsDefault=true`) ถ้ายังไม่มี ·
  `WarehouseStock` ของคลังหลัก = `Product.CurrentStock` เดิม (INSERT … WHERE NOT EXISTS) ·
  ตรวจสอบ: `Σ WarehouseStock.Quantity == Product.CurrentStock` ทุกสินค้า ก่อนสลับให้
  `CurrentStock` เป็น derived

### 5.3 การขายที่กินสูตร (sell-consumes-BOM)

```
CompleteOrderAsync / SyncOfflineOrderAsync (PosService.Orders.cs:555 และคู่ของมัน)
  for item in order.Items:
    product = item.Product
    if product.ConsumesBomOnSale && activeBom(product):
        for line in bom.Lines:
            qty = line.QuantityPerParent × item.Quantity × (1 + bom.WastePercent/100)
            ledger.MoveAsync(OUT, warehouse = order.WarehouseId, product = line.ComponentProduct, qty,
                             MovementType = "SALE-BOM", PosOrderId = order.Id)
        // ตัว parent (ชานม) ไม่ตัด — ไม่เคยเข้าสต็อกอยู่แล้ว (TrackStock=false)
        for mod in item.Modifiers where mod.Option.BomId != null:
            ตัดตามสูตรย่อยแบบเดียวกัน
    elif product.TrackStock:
        ledger.MoveAsync(OUT, order.WarehouseId, product, item.Quantity)   // พฤติกรรมเดิม
```

- **COGS**: Σ (component qty × AverageUnitCost) ณ วันขาย → JE ขาย Dr ต้นทุนขาย / Cr
  สินค้าคงเหลือ(วัตถุดิบ) — ใช้ `InventoryCostingService` เดิม (costing ระดับบริษัท ตาม
  ข้อสรุป 2)
- **Void/Refund**: กลับรายการ component ตามที่ตัดไปจริง (อ่านจาก `StockMovement` ที่
  `PosOrderId` ตรง ไม่คำนวณใหม่จากสูตร — สูตรอาจเปลี่ยนไปแล้ว)
- **Offline**: สูตรต้องถูกส่งลง `sw.js`/IndexedDB พร้อมสินค้า เพื่อให้ตอน sync ตัดตาม
  สูตร ณ วันขาย ไม่ใช่สูตรวันที่ sync — ส่ง `BomSnapshotJson` มากับ `SyncOfflineOrderAsync`
- **ติดลบ**: วัตถุดิบติดลบได้ (ขายก่อนรับของเข้าระบบเป็นเรื่องปกติหน้าร้าน) แต่ต้องขึ้น
  ป้ายแดงในรายงานสิ้นวัน ห้ามบล็อกการขาย

### 5.4 รายงานรายสาขา

- `GetDailySummaryAsync(companyId, date, branchId?, terminalId?)` + endpoint รวม
  "ทุกสาขาในหน้าเดียว" `GET pos/branches/summary?date=` คืนแถวละสาขา:
  ยอดขาย · จำนวนบิล · แก้ว · ต้นทุนวัตถุดิบ · %ต้นทุน · waste · variance · เงินสดที่นับ vs
  คาด
- JE ขาย/COGS/คืนเงิน/waste ติด `JournalLineDimension(Branch.DimensionId)` ทุกบรรทัด →
  P&L รายสาขาใช้กลไกมิติบัญชีที่มีอยู่แล้ว (`DimensionalAccountingService`)
- **variance รายวัน** = (ที่ควรเหลือตามสูตร) − (นับจริง) − (waste ที่บันทึก) ต่อวัตถุดิบต่อสาขา
  — นี่คือรายงานที่ A ต้องการจริง และ B ยอมรับได้เพราะ waste ถูกแยกออกแล้ว

### 5.5 ภาษี (ต้องเสร็จก่อนเปิดใช้หลายสาขาจริง)

| กฎ | สิ่งที่ต้องทำ |
| --- | --- |
| §86/6 ใบกำกับอย่างย่อ | สลิปพิมพ์คำว่า "ใบกำกับภาษีอย่างย่อ" **เฉพาะ** `Company.IsRetailApproved && PhoR06ApprovedDate != null` (ฟิลด์มีอยู่แล้ว — CLAUDE.md กฎเหล็ก #2 A) · ไม่ผ่าน = หัว "ใบเสร็จรับเงิน" + ป้ายเตือนเจ้าของในหน้าตั้งค่า POS (ไม่ใช่ error ที่แคชเชียร์) |
| รหัสสาขาบนใบอย่างย่อ | `IssuerBranchCode` = `Branch.TaxBranchCode` ของ `terminal.BranchId` ผ่าน `Helpers/TaxBranchCode.cs` ตัวเดิม — snapshot ลง `PosOrder` ตอนจ่ายเงิน |
| เลขที่ใบอย่างย่อ | ต่อสาขา gap-free: `POS-{BranchCode}-{yyyyMM}-{seq}` ภายใต้ `AdvisoryLockKey.For(companyId, DocumentSequence, $"POSABB-{branchId}")` — ห้ามใช้ `OrderNumber` (นับต่อบริษัท และนับใบที่ void ด้วย) |
| ใบกำกับเต็มรูปจาก POS | `IssueTaxInvoiceAsync` ตั้ง `BranchId = order.BranchId` → resolver เดิมจัดการรหัสสาขา/ที่อยู่/TXID e-Tax ให้ |
| รายงานภาษีขาย §87 | เพิ่มคอลัมน์สาขา + ตัวกรอง — บริษัทที่จด VAT แยกสาขายื่น ภ.พ.30 แยก (§83/4) |
| §65 ตรี ของเสีย | waste เข้าบัญชี "ของเสียตามปกติธุรกิจ" มีใบนับสต็อกเป็น source document (ข้อ (9)) · waste เกินเกณฑ์ที่บริษัทตั้ง (เช่น >5% ของวัตถุดิบ) ขึ้นเตือนให้เตรียมคำอธิบายไว้ก่อนสรรพากรถาม |

### 5.6 UX

- Wizard "เพิ่มสาขา" 1 หน้า: ชื่อ · รหัสสาขาสรรพากร · ที่อยู่ → สร้าง `Branch` + `Warehouse`
  (ผูกกัน) + `PosTerminal` เครื่องแรก (ผูกทั้งคู่) ในธุรกรรมเดียว
- หน้า POS: มุมบนซ้ายโชว์ **ชื่อสาขา** (จาก terminal.Branch) — เครื่องที่ยังไม่ผูกสาขาขึ้น
  ป้ายเหลือง "ยังไม่ระบุสาขา — รายงานรายสาขาจะไม่นับเครื่องนี้"
- หน้านับสต็อกสิ้นวัน: มือถือ · แสดง "ควรเหลือ" · กรอก "นับได้" · เลือกเหตุผลผลต่าง ·
  ปุ่มเดียว "ปิดวัน" → JE waste + ปรับสต็อก
- Dashboard สาขา: การ์ดละสาขา (ยอด · แก้ว · %ต้นทุน · waste) เรียงตามยอด · สีแดงเมื่อ
  %ต้นทุนเกินค่าเฉลี่ยบริษัท +N%

---

## 6. แผนพัฒนาสำหรับ Opus (เรียงตามการพึ่งพา)

| เฟส | งาน | ไฟล์หลัก | เกณฑ์ผ่าน |
| --- | --- | --- | --- |
| **0** ยุบสต็อก | `IStockLedger` + migration คลังหลัก + ย้ายผู้เขียน 8 ไฟล์ + `tools/stock_writer_check.py` + เทสต์ Σ คลัง = CurrentStock | `Services/Implementations/StockLedger.cs` (ใหม่) · `PosService.Orders.cs` 4 จุด · `DocumentService.ApplyStockMovementsAsync` · `WarehouseService` · `ProductionOrderService` · `ProductService` (ปรับปรุง) | โอนคลังแล้ว POS เห็นยอดขยับ · checker negative test ผ่าน · บริษัทเดิมสาขาเดียว "ไม่รู้สึกอะไร" |
| **1** เครื่องผูกสาขา | `PosTerminal.BranchId/WarehouseId` + snapshot ลง `PosOrder` + wizard เพิ่มสาขา + ป้ายบนหน้า POS + `ResolvePaymentAccountAsync(terminal, method)` | `Pos.cs` · `PosService.cs:22-60` · `pos.html` · `DatabaseMigrationHelper` | เครื่องใหม่บังคับเลือกสาขา · เครื่องเก่า backfill = คลังหลัก/สำนักงานใหญ่ |
| **2** ภาษี | ด่าน ภ.พ.06 บนสลิป · รหัสสาขา+เลขรันใบอย่างย่อต่อสาขา · `IssueTaxInvoiceAsync` ตั้ง `BranchId` · รายงานภาษีขายมีสาขา | `PosService.Orders.cs:353-445` · `pos.html:2250` · `TaxService` | สลิปบริษัทที่ไม่มี ภ.พ.06 ไม่มีคำว่า "ใบกำกับ" · ใบกำกับจากสาขา 3 พิมพ์ "สาขาที่ 00003" |
| **3** สูตร | `ProductType.RawMaterial` · `ConsumesBomOnSale` · ตัด component ตอนขาย/คืน/void/offline · modifier ผูก BOM · COGS จาก component · หน้าตั้งสูตรบนสินค้า | `PosService.Orders.cs` (CompleteOrder/SyncOffline/Refund/Void) · `products.html` · `sw.js` | ขายชานม 1 แก้ว → ไข่มุก −50g ชา −200ml แก้ว −1 ที่**คลังของสาขานั้น** · void แล้วกลับครบ |
| **4** ปิดวัน + waste | `StockCount.VarianceReason` · JE waste · รายงาน variance | `ProductService` (StockCount) · หน้าใหม่ `pos-eod.html` | waste ที่นับได้ไม่ปนกับ variance |
| **5** รายงานรายสาขา | JE ติดมิติสาขา · `GetDailySummaryAsync(branchId)` · `pos/branches/summary` · dashboard การ์ด | `PosService.Orders.cs` (CreateSalesJournalEntry ทุกตัว) · `pos-reports.html` | P&L รายสาขาจาก `DimensionalAccountingService` ตรงกับผลรวม POS |
| **6** ราคา + สิทธิ์ | `PriceList` + resolver · `CompanyUser.AllowedBranchIds` + ด่านเปิดกะ | `Product.cs` · `PermissionService` · `PosService.OpenSessionAsync` | ห้างขาย 65 ตลาดขาย 55 จากสินค้าตัวเดียว · แคชเชียร์ B เปิดเครื่อง A ไม่ได้ |
| **7** ครัวกลาง | `ProductionOrder.WarehouseId` · ผลิตแล้วโอนอัตโนมัติ (optional) | `ProductionOrderService` | ผลิตไข่มุกที่ครัวกลาง → โอน 5 สาขา → สาขาเห็นยอด |

ทุกเฟส: เทสต์ pure class ในคอมมิตเดียว (`StockLedgerTests` · `BomConsumptionTests` ·
`AbbreviatedInvoiceNumberTests`) · sync `DOCUMENT_FLOW.md` §JE ของ POS + `ACCOUNT_STRUCTURE.md`
§3.1a สาขา · ผ่าน checker 22 ตัว + ตัวใหม่

**ประมาณขนาด**: เฟส 0 = ใหญ่สุดและเสี่ยงสุด (แตะทุกผู้เขียนสต็อก) แต่ทุกเฟสหลังพึ่งมัน
ห้ามข้าม · เฟส 1-2 เล็ก ทำได้ทันทีหลัง 0 · เฟส 3 กลาง · 4-7 เล็ก-กลาง

### 6.1 บันทึกการสร้างจริง (อัปเดตทุกคอมมิตที่ปิดเฟส)

| เฟส | สถานะ | หมายเหตุ |
| --- | --- | --- |
| **0** ยุบสต็อก | ✅ | `IStockLedger` + `StockLedger` · migration 6 ขั้น (คลังหลัก/ย้ายยอด/backfill `WarehouseId`/unique index) · ย้ายผู้เขียน **ครบทุกไฟล์ในรอบเดียว**: `PosService.Orders` 4 จุด · `DocumentService.ApplyStockMovementsAsync` 2 จุด · `ProductService` 3 จุด · `CmsCommerceService` 3 จุด · `ImportExportService` 2 จุด · `ProductionOrderService` 2 จุด · `StockCountService` · `ConsignmentService` · **`WarehouseService` ใบโอนคลัง 3 จุด** (ฝั่งที่เคยเขียน `WarehouseStock` อย่างเดียว) · `tools/stock_writer_check.py` (negative test ผ่าน) |
| **1** เครื่องผูกสาขา | 🔨 backend เสร็จ | entity + migration + snapshot `PosOrder.BranchId/WarehouseId` (สืบทอดตอนแยกบิล · ตรึงตอนเปิดบิล ห้าม resolve สด) — เหลือ API/หน้าตั้งค่าเครื่อง + ป้ายสาขาบนหน้า POS + `ResolvePaymentAccount` |
| **7** ครัวกลาง | 🔨 บางส่วน | `ProductionOrder.WarehouseId` + เบิก/รับที่คลังนั้น + ด่านของขาดดูยอด**ในคลัง** — เหลือการโอนอัตโนมัติหลังผลิต |

**สิ่งที่พบเพิ่มระหว่างทำเฟส 0** (ไม่อยู่ในผลตรวจรอบแรก — เจอเพราะต้องอ่านทุกผู้เขียน):

1. **`ProductService.AdjustStockAsync` ขัดแย้งกับตัวเอง** — เขียน
   `movement.Quantity = request.Quantity` (ไม่มีเครื่องหมาย ⇒ OUT ก็เป็นบวก) แต่
   `ws.Quantity += qty` (มีเครื่องหมาย) ⇒ รายงานที่ SUM จาก `StockMovement` อ่านการเบิก
   เป็น "รับเข้า" ทุกแถว ขณะที่ยอดในคลังถูก · และแตะ `WarehouseStock` **เฉพาะตอนระบุคลังมา**
   ⇒ การปรับสต็อกแบบไม่ระบุคลังทำให้สองตัวเลขห่างกันขึ้นเรื่อย ๆ
2. **การนับสต็อกเขียนทับยอดรวมทุกคลัง** — ทั้ง `ProductService.ApplyStockCountAsync` และ
   `StockCountService.CloseAsync` ตั้ง `product.CurrentStock = line.CountedQty` ตรง ๆ ทั้งที่
   ใบตรวจนับผูกคลังได้ (`StockCount.WarehouseId`) ⇒ นับสาขา A ได้ 10 ⇒ ระบบเชื่อว่าทั้งบริษัท
   มี 10 · ของที่สาขา B **หายจากระบบเงียบ ๆ** (แก้ด้วย `SetAbsolute` ที่ตั้งยอดของคลังนั้น
   แล้วให้ยอดรวมขยับตามผลต่าง — ล็อกไว้ด้วย `StockLedgerDeltaTests`)
3. **สูตร WAC ถูกเขียนซ้ำ 3 ที่** (`InventoryCostingService` · `DocumentService` inline ·
   เกือบเป็นที่ 4 ใน ledger) → ยุบเป็น `Helpers/WeightedAverageCost` + `WeightedAverageCostTests`
4. **`RegisterReceiptAsync` เรียก `SaveChangesAsync` ข้างใน** ⇒ ถ้า ledger เรียกมัน จะ flush
   entity ที่ผู้เรียกประกอบค้างไว้ (เอกสารที่ยังไม่ครบบรรทัด) กลางทาง → ledger ใช้สูตรกลาง
   ปรับบน entity ที่ track อยู่แทน
5. **`CmsCommerceService` มี fallback ที่เขียนสต็อกเอง** เมื่อ DI ไม่ครบ — และ
   `ReverseStockIfDeductedAsync` ก็เขียนเองอีกจุด (ไม่แตะ `WarehouseStock` เลย)
6. **ใบโอนคลังทำให้ยอดรวมบริษัทลดลงระหว่างทาง** (goods in transit) — ถูกต้องแล้วในเชิงบัญชี
   และตอนนี้มี `TransferPairId` ให้รายงานแยกออกจากการขาย/ซื้อได้
7. **นำเข้าสต็อกยกมาซ้ำไฟล์เดิม** — เดิมล้างยอดเก่าด้วย `CurrentStock -=` อย่างเดียว
   ⇒ ยอดต่อคลังบวมขึ้นทุกรอบทั้งที่ยอดรวมถูก

---

## 7. คำถามที่เจ้าของระบบต้องตัดสิน

1. **costing ระดับบริษัท พอไหม** (ข้อสรุป 2) — หรือลูกค้าเป้าหมายมีสาขาที่ต้นทุนวัตถุดิบ
   ต่างกันมากจนต้องแยกต่อคลัง (ซื้อจากซัพพลายเออร์คนละราย)
2. **บริษัทเดิมที่มีสต็อกอยู่** — migration สร้างคลังหลักแล้วย้ายยอดทั้งก้อน ยอมรับได้ไหม
   หรือต้องให้เจ้าของกดยืนยันก่อน (เสนอ: อัตโนมัติ + AuditLog + แจ้งเตือนครั้งเดียว)
3. **ใบกำกับอย่างย่อ**: บริษัทลูกค้าส่วนใหญ่มี ภ.พ.06 หรือยัง — ถ้าส่วนใหญ่ไม่มี ค่าเริ่มต้น
   หัวสลิปควรเป็น "ใบเสร็จรับเงิน" และให้เปิด "อย่างย่อ" เองหลังอนุมัติ
4. **สูตรตั้งใครตั้ง** — เจ้าของ/ผู้จัดการเท่านั้น (สิทธิ์ `Product.Recipe.Manage`) หรือให้
   AI เสนอสูตรจากชื่อสินค้า (กฎเหล็ก #1: ต้องมี distillation + validate กับสินค้าจริง)
5. ทำเป็น **add-on** ไหม — "หลายสาขา" เป็นฟีเจอร์ที่ร้านสาขาเดียวไม่ต้องจ่าย เข้าข่าย
   `AddOnCodes` แบบ `lodging.multi-property` (ราคาต่อสาขาที่เพิ่มจากแห่งแรก) · สูตรวัตถุดิบ
   ควรฟรี (คือ Aha-moment ที่ทำให้ติด)
