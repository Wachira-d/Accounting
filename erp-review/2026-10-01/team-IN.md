# รอบ 201 ทีม IN — สต็อก / สินทรัพย์ / ค่าตั้งบริษัท / ที่พัก

> ฐาน `5eed54bf` (branch `claude/erp-system-review-team-660mev`) · ขอบเขต `BACKLOG.md` §1.8 (A-IN1..7) + หมวด C ที่ BRIEF มอบ (C-5 · C-6) ·
> คำตัดสิน `erp-review/2026-09-29/DECISIONS.md` ข้อ 30 · 35 · 36 · 38 · 78 · 79 · **ยังไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้ (ไม่มี .NET SDK) — CI คือตัวแรก**

## 1. สถานะรายข้อ

| ID | สถานะ | ที่แก้ (file) | เทสต์ / ด่าน |
|---|---|---|---|
| A-IN1 `CostingMethod` ไม่มีผู้เขียน | ✅ | `Helpers/CostingMethodPolicy.cs` (ใหม่) · `Models/DTOs/Product/ProductDtos.cs` (Create/Update/Response + สถานะล็อก) · `ProductService.CreateAsync/UpdateAsync/GetByIdAsync/GetAllAsync` · `products.html` (ช่อง + hydrate/reset/ล็อก) · `HelpContentSeeder.cs` (ข้อความศูนย์ช่วยเหลือ) | `InventoryCostingMethodTests` · `tools/product_form_contract_sim.js` (ใหม่ · negative 6) · required_call_site ×2 |
| A-IN2 คิว FIFO/WAC rebuild มองแค่ IN/OUT | ✅ | `Helpers/InventoryCostFlow.cs` (ใหม่) · `Inventory/InventoryCostingService.cs` (`ResolveOutboundCostAsync` FIFO · `RebuildAverageCostAsync` · `LoadCostMovementsAsync` ใหม่ — รวมแถวที่ ledger เพิ่มแต่ยังไม่ save · กรอง `CompanyId`) | golden FIFO/WAC สองทิศใน `InventoryCostingMethodTests` · required_call_site ×2 |
| A-IN3 เปลี่ยนประมาณการค่าเสื่อมไปข้างหน้า | ✅ | `Helpers/DepreciationEstimateChange.cs` (ใหม่) · `FixedAssetService.AdjustUsefulLifeAsync` (วิธีคิดใหม่ · ยืนยันค่าเดิม = ทบทวน · audit chain แทน `AuditLogs.Add`) · `AdjustUsefulLifeRequest.NewDepreciationMethod` (optional ท้าย) · `FixedAssetValuationEdit` ข้อความชี้ปุ่ม · `fixed-assets.html` (วิธี + เหตุผล) | `DepreciationEstimateChangeTests` (golden 120,000/60 → DDB) · required_call_site ×1 |
| A-IN4 เล่มเลขเอกสารต่อสาขา | 🔨 ส่วนเครื่องออกเลข · 📋 สวิตช์ | `Helpers/DocumentNumberBook.cs` (ใหม่) · `DocumentNumberGenerator.NextAsync(…, string? branchCode = null)` · `ISettingsService/SettingsService.GetNextNumberAsync(…, branchCode = null)` — **พารามิเตอร์ optional ท้ายเท่านั้น ไม่แตะผู้เรียกของทีมอื่น** | `DocumentNumberBookTests` · required_call_site ×1 (ตัวนำหน้าเล่มก่อนล็อก) |
| A-IN5 ออกใบเช็คเอาต์ใหม่หลัง void | ✅ | `Helpers/LodgingCheckoutReissue.cs` (ใหม่) · `LodgingService.Lifecycle.cs` (`ReissueFinalDocumentAsync` ใหม่ · แยก `BuildFinalInvoiceLinesAsync` / `UpsertFinalDraftAsync` / `ApplyFinalDepositPlanAsync` ออกจากเส้นเช็คเอาต์คำต่อคำ) · `LodgingService.Operations.cs` (`CanReissueFinalDocument` + ข้อความ) · `LodgingDtos.cs` · `ILodgingService.cs` · `LodgingController` `POST reservations/{id}/reissue-final` (`[RequirePermission(LodgingManage)]`) · `lodging.html` · `api.js` | `LodgingCheckoutReissueTests` · required_call_site ×2 (ตัวสร้างเดียวกันทั้งสองเส้น · ห้ามนับมิเตอร์/รับเงินซ้ำ) |
| A-IN6 สีบริษัทไม่ตรวจรูป | ✅ | `DocumentTemplateStyle.ColorRejectReason` (ใหม่ · `RejectReasons` ใช้ตัวเดียวกัน) · `SettingsService.UpdateSettingsAsync` (ผิดรูป ⇒ 400 `SET-COLOR` · เก็บ `#RRGGBB`) + `MapToResponse` อ่านผ่าน `Hex` · `DocumentIssuerIdentity.Resolve` กรองสีบริษัท | `CompanySettingsInRound201Tests` · required_call_site ×1 |
| A-IN7 เปลี่ยน IndustryType ไม่ seed ประเภทมัดจำ | ✅ | `DepositKindSeed.AddedByIndustryChange` (ใหม่) · `CompanyService.UpdateAsync` เรียก `EnsureSeededAsync` เมื่อธุรกิจใหม่มีกุญแจที่เดิมไม่มี | `CompanySettingsInRound201Tests` · required_call_site ×1 |
| C-5 `ReconcileProductTotalsAsync` | ✅ (ต่อสายเป็นเครื่องมือแอดมิน) | `Helpers/StockTotalsReconciliation.cs` (ใหม่) · `IStockLedger`/`StockLedger`: `FindProductTotalMismatchesAsync` (อ่านอย่างเดียว) + `RepairProductTotalsAsync` (แถวที่เลือก · ค่ายังเท่าที่เห็น · ประวัติไม่ตรงต้องยืนยันตรวจนับ · audit chain · ล็อกต่อบริษัท) — ลบเมธอดซ่อมเงียบเดิม · แก้คอมเมนต์ที่อ้าง "งานตรวจเรียกเป็นระยะ" · `ProductController` GET/POST (`Inventory.View`/`Inventory.Adjust`) · `products.html` เมนู + โมดัล | `StockTotalsReconciliationTests` · required_call_site ×3 |
| C-6 `UsefulLifeReviewedAt` ไม่มีผู้อ่าน | ✅ | `Helpers/UsefulLifeReview.cs` (ใหม่) · `PreCloseChecklistService.RunAsync` ข้อ `USEFUL_LIFE_REVIEW` เฉพาะเดือนสุดท้ายของรอบบัญชี · ทางไปต่อ = ปุ่ม “ปรับอายุการใช้งาน” ยืนยันค่าเดิมได้ (A-IN3) | `DepreciationEstimateChangeTests` ครึ่ง C-6 · required_call_site ×1 |

## 2. 📋 ที่เหลือพร้อมเหตุผล

- **A-IN4 สวิตช์ "แยกเล่มต่อสาขา" + ผู้เรียกส่งสาขา**: ผู้เรียกเครื่องออกเลข 19 จุดอยู่ในไฟล์ของทีมอื่น (`DocumentService.cs` ช่วง Approve = TX ·
  `DocumentService.Reissue.cs` = DV · `IntegrationService.cs` = GW · `PosService.Orders.cs` · `SampleDataController`) — BRIEF ห้ามแก้ผู้เรียกของทีมอื่นในรอบนี้ ·
  เปิดสวิตช์ก่อนทุกทางเข้าส่งสาขา = ใบของสาขาเดียวกันกระจายสองเล่ม (เลขไม่ต่อเนื่องต่อเล่ม — §86/4) จึง**ไม่เพิ่มคอลัมน์ค่าตั้ง/ช่องบนหน้า** (ค่าตั้งที่ไม่มีผล = F2 ข้อ 2)
  งานรอบถัดไป: `CompanySettings.DocumentNumberPerBranch` (ADD COLUMN · ค่าเริ่มต้นปิด) + ตัวตัดสินสวิตช์ (เปลี่ยนได้เฉพาะก่อนมีเลขในปีภาษี) + ทุกผู้เรียกส่ง
  `Branch.Code` ของเอกสาร (`callers.py NextAsync` · `GetNextNumberAsync`) + `sequence_lock_check` + เทสต์สองทิศ (ปิด = เลขเดิม)
- **วิธีราคาเจาะจง (specific identification)**: ไม่มีใน enum `CostingMethod` — ต้องผูกล็อต/ซีเรียลกับการขาย (งานใหญ่) · ต้นทุนมาตรฐานยังเลือกไม่ได้เพราะไม่ลงผลต่างราคา
- **การเปลี่ยนวิธีคิดต้นทุนของสินค้าที่มีความเคลื่อนไหว** (เปลี่ยนนโยบายบัญชี ปรับย้อนหลัง): ตั้งใจไม่ทำ — ทางไปต่อคือรหัสสินค้าใหม่ + ปรับสต็อกโอนยอด (คำตัดสินข้อ 30)

## 3. คำตอบ F3 ข้อ 7–12 (สรุป)

7. `callers.py`: `CostingMethod` ผู้อ่าน 5 จุดเดิม + ผู้เขียนใหม่ 2 (Create/Update) · `ReconcileProductTotalsAsync` ผู้เรียก 0 → ลบ, แทนด้วยสองเมธอดที่มีผู้เรียก 1 ต่อเมธอด ·
   `RebuildAverageCostAsync` ผู้เรียก 1 (void ใบซื้อ) ได้สูตรใหม่ · `GetNextNumberAsync`/`NextAsync` ผู้เรียก 6/19 ไม่ต้องแก้ (optional ท้าย) · รูปแบบ "อ่านแค่ IN/OUT" ในคิวต้นทุนเหลือ 0 จุด
   (`AiSuggestionController` อ่าน OUT/TRANSFER_OUT เพื่อสถิติการใช้ ไม่ใช่ต้นทุน — ไม่แตะ)
8. ทางเข้าสร้างสินค้าอื่น (OCR `OcrController` · นำเข้าไฟล์ · CMS) ไม่ส่งวิธีคิด ⇒ ค่าเริ่มต้นถัวเฉลี่ย = พฤติกรรมเดิม · ทางเข้าแก้สินค้ามีเส้นเดียว (`UpdateAsync`) ·
   ปรับประมาณการค่าเสื่อม: หน้าแก้ไขปฏิเสธ (ด่านเดิม) + เส้นเดียว `adjust-life` · ออกใบเช็คเอาต์ใหม่: เส้นเดียวในโมดูลที่พัก (หน้าเอกสารยังออกเองได้ตามเดิม)
9. เข้มขึ้น: เปลี่ยนวิธีคิดต้นทุนหลังมีความเคลื่อนไหว (ทางไปต่อในข้อความ) · สีผิดรูป (เลือกจาก input color ได้เสมอ) · ออกใบใหม่ยอดต่าง (ทางไปต่อใบลด/เพิ่มหนี้) ·
   ซ่อมยอดเมื่อประวัติไม่ตรง (ติ๊กยืนยันตรวจนับ) — เทสต์ทิศตรงข้าม: `เปลี่ยนวิธีของสินค้าที่ยังไม่มีความเคลื่อนไหว_ผ่าน` · `สีถูกรูปหรือว่าง_รับได้` ·
   `เช็คเอาต์แล้ว_ใบถูกยกเลิก_ยอดเท่าเดิม_ออกได้` · `ประวัติไม่ตรง_ยืนยันตรวจนับแล้ว_ซ่อมได้`
10. ค่าที่ persist: สีผิดรูปที่เก็บไว้ ⇒ อ่านผ่าน `Hex` ทั้ง API และหัวเอกสาร (ไม่ migrate ทับค่าผู้ใช้) · ค่าเฉลี่ยที่เคย rebuild ด้วยสูตรเก่า ⇒ แก้เมื่อ rebuild ครั้งถัดไป
    (ไม่ไล่ rebuild ย้อนหลังเงียบ — COGS ที่ลงแล้วไม่ขยับ) · ยอด `CurrentStock` ที่คลาด ⇒ เครื่องมือ C-5 (รายงานก่อน ซ่อมเมื่อกด) · ไม่มีคอลัมน์ใหม่ ⇒ ไม่มี migration
11. ฝ่ายค้าน: **ยังไม่ได้ส่ง** (subagent ของทีมไม่มีเครื่องมือเรียก subagent อื่น) — ขอ main agent ส่ง diff รอบ merge โดยเฉพาะ A-IN5 (เส้นมัดจำ) และ A-IN2 (COGS FIFO)
12. DOCUMENT_FLOW §3.2 ข้อ 8 · §5.6 · §6.5 + บล็อก Last verified · ACCOUNT_STRUCTURE แถวเลขเอกสารต่อสาขา (🔨) · TEST_PLAN บล็อก IN-01..22 · CHANGELOG (append)

## 4. ความเสี่ยงคอมไพล์ที่เหลือ (ไม่มี SDK)

- `ProductResponse(… CostingMethod CostingMethod = CostingMethod.WeightedAverage, …)` และ `CostingMethod? CostingMethod` ใน record — อาศัยกติกา Color Color
  (แบบเดียวกับ `ProductType ProductType` ที่มีอยู่แล้ว และ `Product.CostingMethod` ที่ initializer เดียวกัน)
- `ExecuteUpdateAsync(set => set.SetProperty(p => p.CurrentStock, target))` (EF Core 8 · มีใช้แล้วในเรพ) · `GroupBy(new { ProductId, MovementType })` + `Sum(Math.Abs(…))` (Npgsql แปล `abs`)
- โปรเจกชัน `new CostMovement(m.MovementType, m.Quantity, m.UnitCost)` (record struct constructor ใน Select สุดท้าย) · `ChangeTracker.Entries<StockMovement>()`
- ตัวแปร `uid` ใน `Guid.TryParse(..., out var uid) ? uid : null` (target-typed conditional ไป `Guid?`)
- `char.IsAsciiDigit` (.NET 7+)

## 5. คำถามค้าง (เลือกทิศที่มองเห็น/ย้อนได้แล้ว)

1. **A-IN5 ไม่ตั้ง `ReplacesDocumentId`/`ReplacedByDocumentId`** — ช่องคู่นี้ในระบบหมายถึงใบแทน §86/6 (ไม่ลงบัญชี · กรองออกจากรายงานภาษี) หรือใบแทนของ DV ที่ "พา posting ไปด้วย"
   ⇒ ตั้งบนใบเช็คเอาต์ใหม่จะทำให้การอนุมัติข้าม JE · จึงอ้างเลขใบเดิมในหมายเหตุ + audit · ถ้าต้องการลิงก์เชิงโครงสร้าง ต้องมีช่องใหม่ (ประสานทีม DV)
2. **A-IN5 การรับชำระเดิม** — void ใบเดิมต้องยกเลิกการรับชำระก่อนอยู่แล้ว ⇒ ใบใหม่ไม่รับเงินอัตโนมัติ (ข้อความบอกให้บันทึกรับชำระที่ใบใหม่) · `Reservation.PaidAmount` ไม่ถูกปรับ
   (พฤติกรรมเดิมหลัง void) — ควรให้ทีมที่พักทบทวนว่าจะลด `PaidAmount` ตอน void หรือไม่
3. **C-5 ทิศการซ่อม** — ยึด "แถวคลัง = ความจริง" ตาม migration เฟส 0 · ถ้าเจ้าของต้องการให้ซ่อมอีกทิศ (สร้างแถวคลังจาก `CurrentStock`) ต้องผ่าน ledger + movement ปรับยอด
4. **C-6** เตือนเป็น Warning (ไม่บล็อกการปิด) — ถ้าต้องการบล็อก แก้ severity บรรทัดเดียว
