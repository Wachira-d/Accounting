# C — ตรวจความถูกต้องเงิน/ภาษีของฟีเจอร์ 3 วันล่าสุด (รอบ 241–249)

> ตรวจอ่านอย่างเดียว · 2026-11-06 · HEAD ที่ตรวจ = `fe9036f7` (branch `claude/booking-service-features-sdemob`) · ไม่มี .NET SDK ในเครื่อง ⇒ ไล่จากโค้ด + เทสต์ + ผล CI
> ขอบเขต: `git log --since="3 days ago"` — สินทรัพย์หลายใบ (369bc403 · c314266c · 43cfdba1 · fe9036f7) · WHT ในลิงก์ชำระ (241/244) · e-Tax by Email (244) · แจ้งเตือนรับ/จ่าย (249) · นโยบายอนุมัติอัตโนมัติ (248) · การปัดเศษ
>
> **สถานะ CI ณ เวลาตรวจ**: 43cfdba1 แดง (CS1501 `CostSourceVoidBlockAsync` รับ 5 อาร์กิวเมนต์ที่ DocumentService.cs:11191) → แก้ใน fe9036f7 แล้ว: `dotnet build (Release)` ✅ · `dotnet test (PostgreSQL · Category=Db)` ✅ · Static checks ยังรันอยู่ตอนตรวจ

สรุปคะแนน: **1 ✅ | 2 ⚠️ | 3 ✅ | 4 ✅ | 5 ✅ | 6 ✅** — ไม่พบ ❌ ที่ทำให้ลงบัญชีผิดเงียบ ๆ ในเส้นหลัก · พบ ⚠️ 3 จุดที่ควรปิดก่อนใช้งานจริงกว้าง (F1 · F2 · F3 ด้านล่าง)

---

## 1. สินทรัพย์ 1 ชิ้น ↔ เอกสารซื้อหลายใบ — ✅ (มี ⚠️ 3 จุด)

ตัวตัดสินเดียว `Helpers/AssetCostSourceRules.cs` (pure) · ผู้เขียน `FixedAssetService.CostSources.cs` ที่เดียว · ด่านใน `DocumentService` 7 จุด (8324 · 8890 · 9151 · 9888 · 9912 · 10296 · 11191–11193) · `AccountingService` 2 จุด (1039 · 1263) · `IntegrationService:3529` · `OcrService:10215`

### 1.1 TFRS for NPAEs บทที่ 10 — องค์ประกอบต้นทุน + วันเริ่มคิดค่าเสื่อม

| ประเด็น | ผล | หลักฐาน |
| --- | --- | --- |
| ต้นทุน = ราคาซื้อ + ต้นทุนทางตรงที่ทำให้พร้อมใช้ (มัดจำ · งวด · ใบสุดท้าย · ค่าติดตั้ง/ขนส่ง) | ✅ | `AssetCostSourceRules.CostOf` :79-85 → `PurchaseLineGlAmount.Posted` :33-34 (ฐานไม่รวม VAT เคลมได้ · รวม VAT ต้องห้าม · รวมภาษีที่ผู้จ่ายออกแทน) · `KindFor` :68-75 ป้าย Auxiliary ผ่าน `AssetRegistrationPlanner.IsAuxiliary` |
| ไม่รวม VAT ที่เคลมได้ · รวม VAT ต้องห้าม §82/5 (เช่น (6) รถยนต์นั่ง) | ✅ | `PurchaseLineGlAmount.Standalone` :18-19 `isVatClaimable ? amount : amount + vatAmount` = สูตรเดียวกับ AutoPost ฝั่งซื้อ (DocumentService.cs:18829) · เทสต์ `VAT_ต้องห้าม_รถยนต์นั่ง_รวมเป็นต้นทุน` + Db `Passenger_car_non_claimable_vat_is_part_of_cost` |
| ใบรับรองแทนใบเสร็จ (§82/4 เคลมไม่ได้เสมอ) | ✅ | `Posted(isCertificateInLieu: true)` = ฐาน+VAT ไม่ว่าธงบรรทัดจะเป็นอะไร — ตรงกับ AutoPost CIL (DocumentService.cs:19022 `docLine.Amount + cilLineVat`) |
| WHT ไม่กระทบต้นทุน · ภาษีที่ผู้จ่ายออกแทนรวมเป็นต้นทุน | ✅ | `LoadCostLineFactsAsync` :109-121 ใช้ `WhtGrossUp.ForLines` บนบรรทัดทั้งใบ (สัดส่วนถูก) = ตัวเดียวกับ `AddPayerBorneWhtExpense` (DocumentService.cs:18360-18378) |
| แปลงบาทตามอัตราของใบ | ✅ | `DocumentFx.ToBaht(..., exchangeRate)` :82-83 · Db `Foreign_currency_deposit_placed_into_computer_account` |
| ใบลดหนี้ฝั่งซื้อ = ติดลบ · ใบเพิ่มหนี้ = บวก · ฝั่งไม่รู้ ⇒ ไม่รับ | ✅ | `SignFor` :58-65 (`adjustmentIsPurchaseSide == null` ⇒ null ⇒ `LineProblem` ปฏิเสธ) |
| ระหว่างจัดหา: ไม่คิดค่าเสื่อม · ไม่มี JE | ✅ | `CreateFromSourcesAsync` :436 `DepreciationMethod.None` · `LinkLinesCoreAsync` :642-647 สะสมต้นทุนอย่างเดียว · `DepreciationSchedule.AmountForPeriod` คืน 0 เมื่อ None |
| **เริ่มคิดค่าเสื่อมเมื่อพร้อมใช้** | ⚠️ **F3** | `PlaceInServiceAsync` :1040 `asset.PurchaseDate = availableDate` ⇒ งวดแรก = **เดือนถัดจาก**เดือนพร้อมใช้ (`DepreciationSchedule.MonthIndexFor` :45-46 ลบ 1) · เครื่องพร้อมใช้ 1 มี.ค. ⇒ ค่าเสื่อมงวดแรก เม.ย. = หายไป 1 เดือนเต็ม (ทุน 1,020,000 / 120 เดือน = 8,500 บาท) · ธรรมเนียมเดิมของระบบ (คำถามเจ้าของ §6 ข้อ 3) — ไม่ใช่ถดถอยใหม่ แต่ พ.ร.ฎ.145 ม.4 ให้เฉลี่ยตามส่วนของระยะเวลาในรอบแรก |
| วันพร้อมใช้ ≥ ใบต้นทุนล่าสุด · ไม่อยู่ในอนาคต | ✅ | `PlaceInServiceProblem` :259-264 · เทสต์ `วันพร้อมใช้ก่อนใบต้นทุนล่าสุด_ไม่ได้_วันเดียวกันได้` · Db `Available_date_before_latest_source_document_is_refused` |
| ที่ดิน/CIP เป็นปลายทางไม่ได้ · ที่ดินต้อง None | ✅ | :249-252 · เทสต์ `ที่ดินต้องไม่คิดค่าเสื่อม_และทิศตรงข้ามผ่าน` |

### 1.2 §65 ตรี(5) รายจ่ายฝ่าทุน

✅ บรรทัดที่ลงผังค่าใช้จ่ายผูกเป็นต้นทุนไม่ได้ (`RoleOf` → `NotEligible` ⇒ `LineProblem` :127-130 บอกทางไปต่อ "แก้ผังบัญชี") — กันทั้ง "เป็นค่าใช้จ่าย" และ "เป็นต้นทุน" พร้อมกัน · เทสต์ `บรรทัดผังค่าใช้จ่ายถูกกัน_พร้อมทางไปต่อ` · Db `Expense_coded_line_is_refused_with_way_forward` · ทิศตรงข้าม: `PlaceInServiceAsync` :979-990 นับ `ineligible` เมื่อบรรทัดถูกย้ายผังไปค่าใช้จ่ายหลังผูก (แต่ด่าน :8324 กันการย้ายอยู่แล้ว)

### 1.3 §82/3 · §82/5 VAT — ✅ ไม่แตะ

VAT ของแต่ละใบอยู่ในใบนั้น (116xx ผ่าน AutoPost เดิม · tax point/ภ.พ.30 ไม่เปลี่ยน) · JE โอน (`TransferLegs` :211-230) ย้ายเฉพาะฐานบนผัง 118xx/12280/12xxx — ไม่มีขา 116xx · VAT ต้องห้ามที่รวมอยู่ถูกบันทึกแยกใน `NonClaimableVatIncluded` (เฉลี่ยตามสัดส่วนเมื่อผูกบางส่วน :590-591 ปัด AwayFromZero)

### 1.4 JE สมดุล · วันที่ · งวดปิด · FiscalPeriodId

| ประเด็น | ผล | หลักฐาน |
| --- | --- | --- |
| Σ Dr = Σ Cr | ✅ | `TransferLegs` คำนวณผลรวมจากขาสุทธิต่อผังแล้ววางขาปลายทางเท่ากัน · `PostAssetTransferJournalAsync` :801-805 ตรวจซ้ำแล้วโยน · เทสต์ `JE_โอน_มัดจำ_งานระหว่างก่อสร้าง_เข้าเครื่องจักร_ผังเดียวกันไม่ลง` + `ใบลดหนี้บนผังงานระหว่างก่อสร้าง_กลับขา_และยังสมดุล` |
| วันที่ JE โอน = วันพร้อมใช้ · JE ปรับหลังโอน = max(วันใบ, วันพร้อมใช้) หรือวันที่ผู้ใช้ระบุที่ไม่ก่อนนั้น | ✅ | :1034 · :702-708 |
| งวดปิด/ล็อก ⇒ 409 + บอกให้ระบุวันในงวดเปิด | ✅ | `EnsurePeriodOpenForAssetJournalAsync` :250-266 ใช้ `ClosedPeriodRanges` ตัวเดียวกับระบบ · คืน `FiscalPeriodId` ใส่ JE :781 |
| ยอดคงเหลือผังต้นทางพอให้เครดิต (มัดจำถูกเคลียร์ด้วยใบเต็มราคาแล้ว) | ✅ | `SourceBalanceProblem` :273-282 · :757-775 ยอดทั้งบริษัทถึงวันลงรายการ · Db `Deposit_already_cleared_elsewhere_cannot_be_transferred_again` |
| JE โอนกลับ/แก้จากสมุดรายวันไม่ได้ | ✅ | `EnsureNotAssetCostTransferAsync` :231-241 ← AccountingService.cs:1039, :1263 |

**ตัวอย่างตัวเลข (ตาม design §4)**: มัดจำ 300,000 (11810) + งวด 400,000 (12280) + สุดท้าย 300,000 + ติดตั้ง 20,000 (12260) ⇒ ต้นทุน 1,020,000 · โอน: Dr 12260 700,000 / Cr 11810 300,000 / Cr 12280 400,000 (ยอดที่อยู่ 12260 แล้ว 320,000 ไม่ลง) ✅ · ใบลดหนี้ −10,000 บน 12260 หลังโอน ⇒ ไม่มี JE · ทุน 1,010,000 · `Adjust` :311-318 ฐานใหม่ = NBV−ซาก กระจายบนอายุที่เหลือ (งวดที่ลงแล้วไม่แตะ) — เทสต์ `ใบมาหลังมีค่าเสื่อม_ปรับไปข้างหน้า_งวดที่ลงแล้วไม่แตะ` + Db `Credit_note_after_placement_reduces_cost_and_rebuilds_plan`

### 1.5 ด่านกันคิดต้นทุนซ้ำ — รวม "ผลต่างมีเครื่องหมาย" (OverRegistrationIncrease / OrphanIncrease)

ชั้นที่ซ้อนกัน 4 ชั้น: (ก) ต่อบรรทัด `AllocationProblem` :144-154 Σ ผูก ≤ |ยอด GL ของบรรทัด| · (ข) ต่อใบ/ต่อผัง `ExcessOverGl` :162-174 ยอดผูกต่อผังต้นทาง ≤ GL ที่ใบลงผังนั้น (ไม่พึ่งชนิดใบ · มัดจำ 118 รวมด้วย) · (ค) ต่อใบบนผังสินทรัพย์ `OverRegistrationIncrease` :180-185 / `OrphanIncrease` :189-194 · (ง) ร่างเดี่ยว/สแกน `CanAbsorbLegacyDraft` :198-201 + `scanAssetByDoc` :100-107 + `OcrService.cs:10215`

**ตัวตัดสินใหม่ถูกต้องทุกชุดเครื่องหมายหรือไม่** — นิยาม: `over(x) = max(0, sign·x)`, `orphan(x) = max(0, −sign·x)`, ผลลัพธ์ = `max(0, after − before)` โดย x = ทะเบียน − GL ผังสินทรัพย์ของใบ

| # | ใบ (sign) | ก่อน x | หลัง x | OverIncrease | OrphanIncrease | ความหมาย | ผล |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PI (+1) ใบงวด 400,000 ยังไม่มีทะเบียน | −400,000 | 0 | 0 | 0 | ผูกครบพอดี (เคสที่ CI 7cb6bd08 ฟ้องผิดในรุ่นแรก) | ✅ ผ่าน |
| 2 | PI (+1) ผูกบางส่วน 300,000 | −400,000 | −100,000 | 0 | 0 (ลดลง) | ยังค้าง 100,000 แต่ไม่แย่ลง | ✅ ผ่าน |
| 3 | PI (+1) ค่าติดตั้ง 20,000 ถูกเฉลี่ยเข้าทะเบียนเดี่ยวที่ยืนยันแล้ว (ทะเบียน = GL) แล้วผูกซ้ำ | 0 | +20,000 | 20,000 | 0 | ต้นทุนสองครั้ง | ✅ 409 (เทสต์ `ค่าประกอบที่เฉลี่ยเข้าทะเบียนเดี่ยวแล้ว_ผูกซ้ำ_ยังถูกจับ_ทิศตรงข้าม` · Db `Linking_aux_line_already_averaged_into_a_confirmed_single_asset_is_refused`) |
| 4 | PI (+1) ใบที่ "เกินอยู่แล้ว" +500 (ข้อมูลเก่า) ผูกบรรทัดอื่นที่ยังไม่มีทะเบียน | +500 | +500 | 0 | 0 | ไม่ทำให้แย่ลง (ด่าน "ห้ามแย่ลง" ไม่ใช่ "ต้องถูกเป๊ะ") | ✅ ผ่าน — ถูกต้องตามเจตนา |
| 5 | PI (+1) รวมร่าง (ทุน 420,000 = เครื่อง 400,000 + ติดตั้ง 20,000) โดยผูกเฉพาะบรรทัดเครื่อง | 0 | −20,000 | 0 | 20,000 | ค่าติดตั้งค้างไม่มีทะเบียน | ✅ 409 เฉพาะเมื่อ `absorbed.Count > 0` (:633) · เทสต์ `รวมร่างโดยทิ้งค่าประกอบ_ยอดค้างเพิ่ม_ถูกจับ` · Db `Absorbing_draft_without_its_install_line_is_refused_then_passes_with_it` |
| 6 | CN ซื้อ (−1) Cr 12260 10,000 ⇒ GL = −10,000 · ยังไม่ผูก | reg 0 − gl(−10,000) = +10,000 → over = max(0, −10,000) = 0 | ผูก −10,000 ⇒ 0 | 0 | 0 | ลดหนี้ครบพอดี | ✅ ผ่าน |
| 7 | CN ซื้อ (−1) ผูก −10,000 สองสินทรัพย์ (−20,000) | +10,000 → 0 | −10,000 → over = 10,000 | 10,000 | 0 | ลดต้นทุนเกินที่ GL ลด | ✅ 409 (และถูก `AllocationProblem` จับก่อนอยู่แล้ว) |
| 8 | DN ซื้อ (+1) | เหมือน #1–#3 | | | | | ✅ |
| 9 | PI ที่บรรทัดอยู่ 118xx (มัดจำ) | 0 (118 ไม่ใช่ผังสินทรัพย์ — `AssetGlBackingAsync` :301-316 ตัดทั้งสองข้าง) | 0 | 0 | 0 | ด่านนี้ไม่เกี่ยว — มัดจำถูกคุมด้วย `ExcessOverGl` | ✅ |

สรุป: สูตร `max(0, after) − max(0, before)` ถูกต้องทุกชุดเครื่องหมายที่เข้าถึงได้ และสมมาตรระหว่าง over/orphan · เคสที่ "before เกินอยู่แล้ว" (#4) ปล่อยผ่านโดยตั้งใจ (ทะเบียนเก่าที่ผิดมาก่อนไม่ถูกบล็อกจากการผูกบรรทัดอื่น) — ยอมรับได้แต่ควรมี problem row บอกผู้ใช้ (ดู F6)

**เครื่องหมายของใบใน `PlaceInServiceAsync` :993** คำนวณจากแถวผูก (`Any < 0 && All ≤ 0 ⇒ −1`) แทน `SignFor` — ถูกต้องเพราะแถวของใบเดียวกันมีเครื่องหมายเดียว · ต่างจาก `LinkLinesCoreAsync` :518 ที่ใช้ `SignFor` — สองวิธีให้ผลเท่ากัน (ยอมรับได้ แต่ไม่ใช่ "ตัวตั้งเดียว" — L)

### 1.6 ด่านทางเข้าอื่น (purge · void · reclassify · resync · OCR · CN/DN · ต่างประเทศ)

| ทางเข้า | ด่าน | ผล |
| --- | --- | --- |
| ยกเลิกใบ (`VoidDocumentAsync`) | :9888 โอนแล้ว ⇒ 409 (`VoidBlock`) · :9912 ถือคีย์ก่อนล็อกแถวใบ · :10296 ปลดแถวที่ยังถือ + คิดต้นทุนใหม่ในธุรกรรมเดียวกัน · :10311 ข้ามลบร่างที่มีแถวต้นทุน | ✅ Db `Voiding_a_capitalized_source_is_blocked_but_a_held_source_is_released` · `Unlinked_document_void_gate_is_silent` |
| ลบถาวร (purge) | `DocumentCascadePurgePolicy.AssetCostCapitalized` :217-219 Block · :10964 ถือคีย์ · :11191-11193 กันซ้ำ + ปลดแถวที่ถือ · :11312 ข้ามลบร่างที่มีแถว | ✅ (ไม่มี Db test เฉพาะเส้น purge+สินทรัพย์หลายใบ — ดู F7) |
| แก้ผังบรรทัด (`ReclassifyLineAccountAsync`) | :8324 ผูกอยู่ ⇒ 409 | ✅ (เทสต์ pure `LineChangeBlock` เท่านั้น — ไม่มี Db negative test ที่เรียกผ่าน DocumentService) |
| ย้ายฝั่ง CN/DN · สลับบริการต่างประเทศ · resync จากระบบภายนอก | :9151 · :8890 · IntegrationService.cs:3529 `CostSourceDocumentChangeBlockAsync` | ✅ (ไม่มีเทสต์ · ดู F7) |
| OCR "ลงทะเบียนสินทรัพย์" ซ้ำจากสแกนที่ใบถูกผูกแล้ว / ผูกใบที่สแกนลงทะเบียนไปแล้ว | OcrService.cs:10215 ⇒ 409 · `LoadCostLineFactsAsync` :145-147 | ✅ (ไม่มีเทสต์) |
| อนุมัติใบ (auto-register) ข้ามบรรทัดที่ผูก · ค่าประกอบที่เหลือไม่กลายเป็นสินทรัพย์ใหม่ | FixedAssetService.cs:1602-1617 | ✅ Db `Approval_auto_register_skips_lines_already_linked` + `Approval_still_registers_the_unlinked_line_of_the_same_document` |
| ใบตัดชำระ (PV/CIL อ้าง PI) · ใบอ้าง GRN | `RouteOf` :99-106 + `LineProblem` · ตาข่าย `ExcessOverGl` | ✅ เทสต์ `ใบยังไม่อนุมัติ_ใบตัดชำระ_ใบอ้างใบรับสินค้า_ถูกกัน` · Db `Line_without_gl_on_its_account_cannot_be_linked` |
| จำหน่าย/ตัดจำหน่าย/ตีราคา/ปรับอายุ/แก้วิธี ระหว่างจัดหา | `EnsureNotUnderAcquisition` ← FixedAssetService.cs:413, 481, 628, 738, 1178 | ✅ |
| ลบทะเบียนที่ยังผูก / soft-delete เมื่อมีประวัติ | FixedAssetService.cs:319-357 · แถวที่ปลดแล้วคงไว้ (L7) · `AssetIdsWithCostSourcesAsync` ใช้ `IgnoreQueryFilters` :214 | ✅ Db `Under_acquisition_asset_is_not_depreciated_and_cannot_be_deleted_while_linked` |
| Tenant | ทุก query ใน CostSources.cs กรอง `CompanyId` (บรรทัดผ่าน `l.Document.CompanyId`) | ✅ Db `Other_company_line_is_not_found` |

### 1.7 ลำดับล็อก (M2)

ทุกเส้นเขียน: `pg_advisory_xact_lock(AssetCostSource, companyId)` → `Documents FOR UPDATE` (เรียง Id) → `FixedAssets FOR UPDATE` (:421 · :471-476 · :881-886 · :944-949) · void/purge ถือคีย์ตั้งแต่ต้นธุรกรรมผ่าน `LockCostSourcesIfLinkedAsync` (:9912 · :10964) ✅

⚠️ **F2** ช่องแคบ: `LockCostSourcesIfLinkedAsync` :198-204 ตัดสินจาก `AnyAsync` **นอกล็อก** — ถ้าใบถูกผูกระหว่าง "ตรวจ" กับ "FOR UPDATE แถวใบ" การยกเลิกจะเดินโดยไม่ถือคีย์ แล้วมาถือใน `ReleaseCostSourcesForVoidAsync` :350 **หลัง**ล็อกแถวใบ ⇒ ลำดับกลับด้าน (ใบ→คีย์) กับผู้ผูก (คีย์→ใบ) ⇒ PostgreSQL ตรวจ deadlock แล้วยกเลิกหนึ่งธุรกรรม (ล้มดัง · ExecutionStrategy ลองใหม่) — ไม่ใช่ข้อมูลเสีย แต่เป็นเส้นที่ "มีเทสต์ไม่ได้" และไม่ควรมี · ทางแก้: ถือคีย์เสมอเมื่อ `AnyAsync` เป็นจริง **หรือ** ย้าย `LockCostSourceWritersAsync` ไปก่อน `FOR UPDATE` แถวใบโดยไม่มีเงื่อนไข (ต้นทุน = advisory lock 1 ครั้งต่อการยกเลิก)

### 1.8 รายงาน/งานปิดงวด ไม่นับสินทรัพย์ระหว่างจัดหา (L8)

✅ `SubLedgerReconciliationService.cs:168` (กระทบยอด GL↔ทะเบียน) · `PreCloseChecklistService.cs:110` (เตือน "ยังไม่รันค่าเสื่อม") · ตัวคิดค่าเสื่อมข้ามเองเพราะ `DepreciationMethod.None`
⚠️ ผู้อ่าน `FixedAssets` อื่นที่ **ไม่กรอง** `IsUnderAcquisition`: `ImportExportService.cs:1639` (ส่งออกทะเบียน) · `OpeningBalanceReconciliation.cs:78` · `FixedAssetImportTarget.cs:47,112,123` · `AccountingService.cs:2034` · `BulkCleanupController.cs:59` — ส่วนใหญ่เป็นการนับ/ส่งออก ไม่ใช่ตัวเลขบัญชี · เฉพาะ `OpeningBalanceReconciliation` (กระทบยอดยกมา) ควรกรองหรือติดป้าย (ยอดระหว่างจัดหาอยู่ 118xx/12280 ไม่ใช่ผังสินทรัพย์) — **F6**

### 1.9 "`ResolveLinePostedGlAmountAsync` ยังใช้ `Standalone`" — ขนาดผลกระทบ (**F1 · ⚠️ Medium**)

`DocumentService.cs:17320-17336`:
- `purchaseSide` :17323-17324 = PI/Expense/PV **ไม่รวม `CertificateInLieu`** ⇒ บรรทัด CIL คืน `line.Amount` (ฐานอย่างเดียว) ทั้งที่ AutoPost ลง Dr = ฐาน + VAT (:19022) และ CIL ที่ตัดชำระใบตั้งหนี้ (route Settlement) ไม่ถูกคืน 0 เหมือน PV (:17328 ตรวจเฉพาะ PV)
- :17336 ใช้ `Standalone` ⇒ ไม่รวม **ภาษีที่ผู้จ่ายออกแทน** (`AddPayerBorneWhtExpense` ลงเพิ่มบนผังของบรรทัด)

ผู้เรียกมีที่เดียว = `ReclassifyLineAccountAsync` :8413 ⇒ ผลกระทบจำกัดที่ "แก้ผังบัญชี" ไม่ใช่เส้นผูกสินทรัพย์ (ซึ่งใช้ `Posted` ถูกต้อง)

ตัวอย่าง: CIL ค่าติดตั้ง 10,000 + VAT 700 ลงผัง 54xxx ⇒ GL Dr 54xxx 10,700 · ผู้ใช้ "แก้ผัง" ไป 12260 ⇒ JE ย้าย Dr 12260 10,000 / Cr 54xxx 10,000 ⇒ **700 ค้างที่ 54xxx ถาวร** (Dr=Cr สมดุลจึงไม่มีด่านจับ — คลาสเดียวกับ P-6 เดิม) · จากนั้นผูกบรรทัดนี้เป็นต้นทุน: `CostOf` = 10,700 แต่ GL 12260 = 10,000 ⇒ `ExcessOverGl` 409 "ลงบัญชีบนผังต้นทางน้อยกว่ายอดที่ผูก 700" (ล้มดัง ✅) ⇒ ผู้ใช้ผูกได้แค่ 10,000 ⇒ ต้นทุนทะเบียนต่ำ 700 · 700 กลายเป็นค่าใช้จ่ายปีนี้แทนค่าเสื่อม 10 ปี (§65 ตรี(5) ผิดทิศ 700 บาท · ภ.ง.ด.50 ปีแรกรายจ่ายสูงเกิน 700 − 70 = 630)
- ภาษีที่ออกแทน: PI ค่าบริการ 10,000 หัก 3% ออกแทน (gross-up 10,000×3/97 = 309.28) ⇒ GL บนผังบรรทัด 10,309.28 · แก้ผังย้าย 10,000 ⇒ 309.28 ค้างผังเก่า
- ความถี่: CIL/ออกแทน ที่ต้องย้ายเข้าผังสินทรัพย์ — ต่ำ · ความรุนแรงต่อใบ ≤ 7% ของฐาน (+ gross-up) · **ไม่เงียบ** เมื่อผูกสินทรัพย์ แต่ **เงียบ** เมื่อแค่ย้ายผังค่าใช้จ่าย↔ค่าใช้จ่าย
- แก้: `ResolveLinePostedGlAmountAsync` เพิ่ม CIL เข้า `purchaseSide` · CIL ที่ `RelatedDocumentId` เป็น PI/Expense คืน 0 เหมือน PV · ใช้ `PurchaseLineGlAmount.Posted(isCIL, …, payerBorneShare)` โดยคิด share จาก `WhtGrossUp.ForLines` (ลอกจาก `LoadCostLineFactsAsync` :111-121 ไปเป็น helper ร่วม) · เทสต์ Db: reclassify CIL มี VAT ⇒ ผังเก่าสุทธิ 0 · effort S (½ วัน)

### 1.10 เทสต์ที่ล็อกด่าน (ชื่อ) + ด่านที่ไม่มี negative test

- pure: `AssetCostSourceRulesTests` 38 เทสต์ (ครอบ RoleOf · SignFor · CostOf · Allocation · ExcessOverGl · Over/OrphanIncrease ทั้งสองทิศ · PlaceInServiceProblem ทุกข้อ · Adjust · Release · VoidBlock)
- Db: `AssetMultiDocumentDbTests` 18 เทสต์ (ชื่อในตารางข้างบน)
- checker: `tools/required_call_site_check.py` กติกา R249FA (:15219-15228)
- **ไม่มี negative test**: ด่าน :9151 (ย้ายฝั่ง CN/DN) · :8890 (ต่างประเทศ) · IntegrationService:3529 (resync) · OcrService:10215 · purge :11191 · `EnsureNotAssetCostTransferAsync` ผ่าน AccountingService (มีเฉพาะข้อความ pure `ด่านJEตัดสูญ…` ของ WHT ไม่ใช่ตัวนี้) · reclassify :8324 ผ่าน DocumentService · ลำดับล็อก (F2) — **F7**

### 1.11 ตรวจแล้วไม่ใช่บั๊ก (สินทรัพย์)

- `AssetGlBackingAsync` นับแถวผูกบนผัง 12280/12xxx เป็น "ทะเบียนรองรับ" ทั้งที่ยังไม่มี JE — ถูก: GL ของใบนั้น Dr ผังนั้นอยู่แล้ว ทะเบียน (ระหว่างจัดหา) รองรับยอดนั้น
- `LinkExcessOverGlAsync` รวม JE สถานะ `Reversed` — ถูก: ใบที่ถูกกลับรายการมีคู่หักล้างกัน ⇒ สุทธิ 0 ⇒ ผูกไม่ได้
- `ApplyPostCapitalizationCostAsync` ประทับ `CapitalizedAt` แม้ `legs.Count == 0` (ผังเดียวกัน ไม่มี JE) — ถูกตามออกแบบ §4 ขั้น 6 · `CapitalizationJournalEntryId` = null ชัดเจน
- `ReleaseCostSourcesForVoidAsync` ลด `PurchaseCost/NetBookValue` ตรง ๆ โดยไม่ผ่าน `Adjust` — ถูก: แถวที่ปลดได้มีเฉพาะที่ยังไม่โอน (ระหว่างจัดหา) ซึ่งยังไม่มีค่าเสื่อม · โอนแล้วโยน 409 ก่อน (:357-360)
- `PurchaseDate = AvailableForUseDate` ตอนโอน — เป็นการ "ยืม field" (กฎ #4 E) แต่มี `AvailableForUseDate` เก็บแยก + design §2.3 ระบุชัด + ผู้อ่าน `PurchaseDate` ทุกจุดคือจุดตั้งต้นค่าเสื่อม ⇒ ยอมรับได้ · ข้อสังเกต: รายงานทะเบียนจะโชว์ "วันที่ซื้อ" = วันพร้อมใช้ สำหรับสินทรัพย์หลายใบ (ควรติดป้ายในหน้า/รายงาน — รวมใน F6)
- ข้อกังวล "มัดจำก้อนเดียวแบ่งสองเครื่อง" — `AllocationProblem` + เทสต์ `แบ่งมัดจำก้อนเดียวให้สองสินทรัพย์ได้_ทิศตรงข้าม` + Db `Same_line_cannot_be_capitalized_twice_but_can_be_split_between_assets` ✅
- `ContractPriceWarning` เป็นคำเตือนไม่บล็อก — ตามคำถามเจ้าของ §6 ข้อ 1 ที่ยังไม่ตัดสิน ✅

---

## 2. WHT ในลิงก์ชำระ + งานหลังรับชำระ (รอบ 241/244 · คำตัดสิน 244-7) — ⚠️ (ถูกต้อง แต่มีช่องสังเกต)

### 2.1 ตัวอย่างตัวเลข: ค่าบริการ 10,000 + VAT 7% − WHT 3%

| ขั้น | ค่า | ที่มา |
| --- | --- | --- |
| เอกสาร | ฐาน 10,000 · VAT 700 · WHT 300 · `TotalAmount` = 10,400 (สุทธิหลังหัก) · `BalanceDue` = 10,400 | `DocumentSettlementState.Apply(PayeeAmount(doc), Paid, …)` DocumentService.cs:15216-15218 — ยอดค้างเป็นสุทธิอยู่แล้ว |
| ลิงก์ชำระ `AmountsFor(10,400, 300, 0)` | Net = 10,400 · Wht = 300 · **Gross = 10,700** (= Net + Wht) | `DocumentPayLinkPolicy.AmountsFor` :160-166 — **ไม่ลบ WHT ซ้ำ** (รุ่นก่อน 244-7 เรียก 10,100) ✅ · อีเมลแสดง "ยอดก่อนหักภาษี ณ ที่จ่าย 10,700" :249-252 |
| ลูกค้าโอน 10,400 ⇒ `WhtForPayment(10,400, 10,400, 300)` | 300 (ตรงยอดค้าง ±0.01) | :170-174 · ไม่ตรง ⇒ null ⇒ ตัวบันทึกรับชำระแนะนำเอง (`WhtPaymentSuggestion.Compute`) |
| `PaymentIntent.Amount` | 10,400 | `DocumentPaymentHandler.cs:92-96` ส่ง `WithholdingTaxAmount: 300` |
| `CreatePaymentAsync` | ตรวจ `paymentWht ≤ GrossCap` (:15096) · บันทึก Payment(10,400 · WHT 300) :15160 · `BalanceDue` = 10,400 − 10,400 = 0 | ✅ ไม่หักซ้ำ |
| GL | Accrual basis: AR ตั้งไว้ที่ 10,400 (DocumentService.cs:18389-18392 `arAmountAtInvoice = TotalAmount`) · WHT asset 11910 300 รับรู้ตอนออกใบ ⇒ รับชำระ Dr ธนาคาร 10,400 / Cr AR 10,400 · Cash basis: AR 10,700 ⇒ Dr ธนาคาร 10,400 / Dr 11910 300 / Cr AR 10,700 | ✅ ทั้งสองฐานสมดุล |
| ใบเสร็จ settlement | `CreateSettlementReceiptAsync` :14759 `WithholdingTaxAmount = carryVatFromSource ? invoice.WHT : 0` · ยอดใบเสร็จ = ยอดรับ 10,400 | ✅ |
| ทะเบียนรอ 50 ทวิ | `PostPaymentAutomationService.cs:249-251` ตรวจมีแถว `WhtCreditsReceived` (สร้างโดยเส้นรับชำระ) — **ตรวจอย่างเดียว ไม่สร้างสำเนา** | ✅ เทสต์ `จุดต่อ_WHT_ขั้นต่ำ_ตรวจทะเบียนรอ_50ทวิ_ไม่หยุดงาน` |

ผู้ซื้อ**บุคคลธรรมดา** (ไม่มีหน้าที่หัก): `BuyerWithholds` :127-137 ⇒ false ⇒ `ExpectedWht` = 0 ⇒ ลิงก์เรียก 10,400 (ยอดค้างตามใบซึ่งหัก WHT ไว้แล้ว) + `WhtMismatchNote` :143-146 เตือนว่ายอดอาจขาด 300 · handler ส่ง WHT 0 ชัด ๆ (:93) ⇒ ไม่ออก 50 ทวิ เท็จ ✅ (ฝ่ายค้าน S1) — ⚠️ ยอดที่ขาด 300 ค้างเป็น `BalanceDue` 0? **ไม่** — ลูกค้าจ่าย 10,400 = ยอดค้าง ⇒ ปิดใบ ทั้งที่ควรได้ 10,700 · ระบบเตือนที่หน้าลิงก์/อีเมลแล้ว (ไม่เงียบ) แต่ใบจะ "ชำระครบ" โดยรายได้ขาด 300 — ต้นเหตุคือใบตั้ง WHT ให้บุคคลธรรมดา (ผิดตั้งแต่สร้างใบ) ⇒ ควรเป็นคำเตือนตอน**อนุมัติใบ** ไม่ใช่ตอนส่งลิงก์ (**F5**)

### 2.2 เกณฑ์ 1,000 บาท (ท.ป.4/2528 ข้อ 12) + 50 ทวิ ฝั่งผู้จ่าย

- ตัวตั้งเดียว `ThaiWhtRateTable.MinimumThresholdBaht = 1000m` :532 · ตัดสินตอน**สร้าง/อนุมัติใบ** (DocumentService.cs:22199 คำเตือน · OCR `OcrWhtThresholdBase` · แพลตฟอร์ม `PlatformWhtSuggestion` :90-93 "ทั้งสัญญา ≥ 1,000 ⇒ หักทุกงวด") — ลิงก์ชำระ/handler ไม่คิดเกณฑ์ใหม่ (ใช้ WHT ของใบ) ✅ ถูกหลัก (เกณฑ์เป็นของการจ่าย/สัญญา ไม่ใช่ของงวดโอน)
- 50 ทวิ ฝั่งผู้จ่าย (เราจ่าย PI/ค่าใช้จ่าย): ออกตอนอนุมัติใบจ่ายผ่าน `WhtCertIssuePlan` (DocumentService.cs:916-922 · :7525) — ไม่เปลี่ยนในรอบนี้ ✅ · ฝั่งผู้รับ (ลิงก์ชำระ) เราเป็น**ผู้ถูกหัก** ⇒ ไม่ออก 50 ทวิ เอง (ถูก: ผู้จ่ายเป็นผู้ออก) ✅ ทะเบียนรอใบอยู่ที่ `WhtCreditsReceived`

### 2.3 ตัดสูญ 11910 (รอบ 243a) — ✅

- JE: Dr **58010** ภาษีเงินได้ถูกหักที่ขอเครดิตไม่ได้ / Cr **11910** ทั้งยอดของแถว ผ่าน `JournalEntryBuilder` (เลข JE + ด่านงวดปิด) · `WhtCreditService.cs:580-586` · ยอด `Math.Round(…, AwayFromZero)` :578
- §65 ตรี (6 ทวิ): 58010 อยู่ใต้กลุ่ม 580 ⇒ `CitCalculation.IsCitExpenseAccount` :117-121 ⇒ `ExceptCitExpense` ตัดออกจากรายจ่าย ภ.ง.ด.50/51 อัตโนมัติ · `WhtCreditWriteOff.NonDeductibleAmount` บันทึกใน audit · เทสต์ pure `ผังตัดสูญต้องเป็นกลุ่มภาษีเงินได้_บวกกลับเต็มจำนวน` + Db `ภงด50_ตัดสูญ58010_ตัดออกจากรายจ่ายครั้งเดียว` ✅
- ด่านยอดตามปีภาษีของแถว (`TaxYearWhtAvailableAsync` ฝ่ายค้าน S1) กันตัดสูญซ้ำหลังปิดปี · ได้ใบภายหลัง ⇒ กลับ JE (ม.63 ขอคืนใน 3 ปี) · JE ตัดสูญกลับ/แก้จากสมุดรายวันไม่ได้ · Db 8 เทสต์ครอบทุกทิศ ✅
- ⚠️ เล็ก: `Decide` ปฏิเสธแถว `Expired` ที่ไม่มี JE (ข้อมูลก่อน 243a) ด้วยข้อความบอกให้ตรวจ 11910 เอง — ถูกหลัก F2 ข้อ 3 (ไม่เดา) ✅

### 2.4 เทสต์ที่ล็อก
`DocumentAutoSendRound241Tests.เงินเข้ายอดสุทธิพอดี_บันทึก_WHT_ที่เหลือทั้งก้อน_ไม่ตรง_ให้ตัวเดิมตัดสิน` · `จุดต่อ_WHT_ขั้นต่ำ…` · `WhtCreditWriteOffTests` 12 + Db 8 · **ไม่มี**: เทสต์ Db เส้น handler ที่ผู้ซื้อบุคคลธรรมดา + ใบมี WHT (ยืนยันว่า Payment.WHT = 0 และใบปิดที่ 10,400) — **F5/F7**

---

## 3. e-Tax by Email (รอบ 244) — ✅

| ข้อกำหนด | ผล | หลักฐาน |
| --- | --- | --- |
| XML UBL 2.1 + ETDA profile · root/namespace ตามชนิด | ✅ | `EtaxDocumentTypeMap.SchemaRoot/RamSuffix` :49-61 · `EtaxInvoiceService.cs:846-854` |
| `DocumentTypeCode` | ✅ (⚠️ หมายเหตุ) | `TypeCode` :25-35: 388 ใบกำกับ · T02 ใบแจ้งหนี้/ใบกำกับ · T03 ใบเสร็จ/ใบกำกับ (เงินสด) · 80 ใบเพิ่มหนี้ · 81 ใบลดหนี้ — ตรง codelist ETDA · CLAUDE.md §F เขียน "T01–T04" เป็นชื่อหมวดหยาบ ไม่ใช่ตารางจริง (ตารางจริงที่นี่ถูกกว่า) · `Receipt` → T03 เสมอ: ใบเสร็จที่ **ไม่มี VAT** (T01) ไม่ควรเข้าเส้น e-Tax — `EtaxAutoIssueScope` กันตั้งแต่ต้นทาง (เทสต์ `ขอบเขต_ใบเสนอราคาและใบกำกับอย่างย่อ_ไม่ใช่eTax`) แต่ T01 "ใบรับ" (รับเงินล่วงหน้า) ยังไม่มีในแผนที่ — จดเป็นงานค้าง ไม่ใช่บั๊กรอบนี้ |
| `IssueDateTime` ISO 8601 +07:00 | ✅ | `FormatIso` :1074-1078 บวก 7 ชม. จาก UTC แล้วต่อ "+07:00" · `CreationDateTime` ใช้ตัวเดียวกัน |
| BranchID 5 หลัก (ผู้ขาย/ผู้ซื้อ) | ✅ | `ComposeTxId` :1125-1129 (TXID = 13+5 · ไม่มี ⇒ 00000) · ผู้ขายล้างให้ 5 หลักก่อน :241-244 · ผู้ซื้อ 13 หลักไม่ครบ ⇒ throw ก่อนยื่นเลขปลอม :265-268 |
| PDF/A-3 ฝัง XML · 1 ไฟล์ · ≤ 3 MB · ไม่แนบ XML แยก | ✅ | `EtaxByEmailFormat.Recipients` :148-180 · `AttachmentProblem` :182-192 · `DocumentEmailService.cs:423-477` (ไฟล์ที่เก็บไว้ก่อน ⇒ เอกสารที่เคยส่งนิ่ง · สร้างไม่สำเร็จ ⇒ ไม่ส่ง) · ไฟล์ e-Tax ไม่มี QR หน้าชำระ (PdfGenerationService:`paymentQr?.WithoutPayLink()`) |
| CC `csemail@etax.teda.th` · To = ผู้ซื้อ 1 อีเมล · ไม่มี BCC/CC อื่น | ✅ | :43 · :148-180 · เทสต์ `ผู้รับ_To1อีเมล_CCเฉพาะETDA_production` + `…CCที่อยู่ETDAซ้ำ_ตัดซ้ำ_CCอื่น_BCC_หลายTo_ปฏิเสธ` |
| ธง UAT ต่อบริษัท (`csemail@uat.etax.teda.th`) · ค่าตั้งต้น false · แยกจาก `EtaxTestMode` เดิม | ✅ | `CompanySettings.EtaxByEmailUseUat` :198 · `TimestampAddress(useUat)` :132 · Db `บริษัทเดิมEtaxTestModeเปิด_ไม่ติ๊กทดสอบByEmail_ยังส่งCCไปproduction` + `ทดสอบByEmail_CCไปUAT_ไม่นับว่าถึงผู้ประทับเวลา` |
| `EtdaTimestampToken` — ประทับโดย**ผู้ใช้บันทึกใบที่ ETDA ส่งกลับ** ไม่ใช่ระบบเดา | ✅ | `EtaxByEmailStage` :8-16 · `DocumentEmailService.cs:595-615` (audit + legalReference) · Db `บันทึกการประทับเวลา_ต้องส่งก่อน_ไฟล์หลังส่ง_แล้วถือเป็นตอบรับ` · ⚠️ token ถูกตัดที่ 500 ตัวเงียบ ๆ (:595) — ถ้า ETDA ส่ง token ยาวกว่านั้นหลักฐานขาด ⇒ ควรปฏิเสธ/เก็บเป็นไฟล์แทน (L) |
| SME ≤ 30 ล้าน | ⚠️ เตือน ไม่บล็อก | `RevenueWarning` :208-212 (รายได้ปีก่อนในบัญชี) — แสดงที่หน้าตั้งค่า/ตัวอย่างอีเมล (`DocumentEmailService.cs:314`) · ไม่ใช่ด่านตอนส่ง · ตรงข้อเสนอ E12 ใน `ETAX_BY_EMAIL_AUDIT.md:107` ("เตือนเมื่อรายได้ปีล่าสุดเกิน 30 ล้าน") เพราะสิทธิจริงตัดสินโดย ก.อ.01 ที่ RD อนุมัติ ไม่ใช่ยอดในระบบ ⇒ ยอมรับได้ · **ไม่มี** ช่อง "วันที่/เลขที่อนุมัติ ก.อ.01" ให้ล็อกจริง — งานค้าง (L) |
| ผู้ส่งต้องเป็นอีเมลที่ลงทะเบียน (ไม่ถอยไปอีเมลแพลตฟอร์ม) | ✅ | `EtaxByEmailSenderGate.Refusal` :39-… · Db `SMTPยังไม่ทดสอบ_ปฏิเสธพร้อมคำแนะนำ_ไม่เรียกผู้ส่งของแพลตฟอร์ม_บันทึกFailedบนเอกสาร` |
| หัวเรื่อง `[ddMMyyyy พ.ศ.][INV|DBN|CDN|RCT][เลข][เลขเดิม]` | ✅ | `Subject` :71-131 · เทสต์ 7 ตัว (รวมใกล้เที่ยงคืน UTC) |
| **สอง renderer ไม่ drift** (HTML `PdfGenerationService` ↔ QuestPDF `DocumentRenderer`) ผ่าน `DocumentLabels` | ✅ | รอบ 244/246 แตะ renderer เฉพาะกล่อง QR: `PdfGenerationService.PaymentQr.cs` ตัวสร้าง caption เดียว (`QrCaptionOf` :25-32 · `PaymentQrHtml` + `ComposePaymentQr` :60) ใช้ `L.PayQr*` ทั้งหมด · `DocumentLabels` เพิ่มคีย์ `payqr_*` ครบคู่ th/en (6a8e7ab3) · grep literal ไทย (`"สแกน` `"ยอดใน` `"ใช้ได้ถึง` `"พร้อมเพย์` `"ชื่อบัญชี`) ใน `PdfGenerationService*.cs` + `EtaxInvoiceService*.cs` = **0 จุด** ✅ |

เทสต์: `EtaxByEmailRound244Tests` 24 + Db 9 · **ไม่มี negative test**: XML `DocumentTypeCode`/`IssueDateTime`/BranchID (เป็นโค้ดเดิมก่อนรอบนี้ — ไม่มีเทสต์ XML เลยในชุดรอบ 244) — **F7**

---

## 4. แจ้งเตือนรับ/จ่ายชำระ (รอบ 249) — ✅

### 4.1 ฝั่งเงิน `IsCashInflowDocAsync` (DocumentService.cs:14652-14667) ต่อทุก `DocumentType`

| DocumentType | ผล | ถูก? | หมายเหตุ |
| --- | --- | --- | --- |
| Invoice · TaxInvoice · Receipt · ReceiptVoucher · BillingNote | เข้า | ✅ | ใบมัดจำขาย (`IsDeposit` บน Invoice/Receipt) = เข้า ✅ |
| DebitNote อ้าง PI/Expense/CIL | ออก | ✅ | :14656-14665 ดูใบต้นทาง |
| DebitNote อ้างใบขาย / ไม่มีใบต้นทาง | เข้า | ✅ (ไม่มีใบต้นทาง = เดาเป็นขาย — ชนิดเอกสารรอบนี้ให้ DN ขายเป็นค่าตั้งต้น) | |
| PurchaseInvoice · Expense · PaymentVoucher · CertificateInLieu · PurchaseOrder · GRN · PR | ออก | ✅ | ตัวอย่างเจ้าของ PI-20260917-0007 ⇒ "💸 จ่ายชำระเงินแล้ว · ให้ <ผู้ขาย>" ✅ เทสต์ `จ่ายชำระใบตั้งหนี้ซื้อ_ไม่ขึ้นรับชำระ_คีย์payment_made_และบอกให้ผู้ขาย` |
| **CreditNote** (ทั้งฝั่งขาย = คืนเงินลูกค้า และฝั่งซื้อ = รับเงินคืนจากผู้ขาย) | ออก **เสมอ** | ⚠️ ฝั่งซื้อผิดทิศ — แต่**ไปไม่ถึง** | `PayableDocumentTypes` :14588-14592 ไม่มี CreditNote/Receipt/PV/CIL ⇒ บันทึกการชำระบน CN ไม่ได้ (:14978-14985 โยน) ⇒ ตัวแจ้งเตือน (:15374-15386 หลัง `CreatePaymentAsync`) และตัวปรับยอดธนาคาร (:11906 · :12182 · :15331 · :15790) ไม่เคยเห็น CN · คืนเงิน gateway เดินผ่าน `GatewayRefundCreditNoteCounter`/`PlatformRefund…` ไม่ใช่ `CreatePaymentAsync` ⇒ **ตรวจแล้วไม่ใช่บั๊กวันนี้** · กับดักในอนาคต (F8): ถ้าเปิด "บันทึกรับเงินคืนบน CN ซื้อ" ตัวนี้จะเป็น "ออก" และ**ยอดธนาคารจะติดลบด้วย** เพราะใช้ตัวเดียวกัน (:11906) — ควรย้ายเป็น `Helpers/CashFlowDirection` ที่รู้ฝั่ง CN จาก `AdjustmentNoteAccount.ResolveSide` ตั้งแต่ตอนนี้ |
| Quotation · DeliveryNote | เข้า? (ไม่อยู่ในชุด ⇒ ออก) | n/a | ชำระไม่ได้ |

`PaymentNotificationText.Build` :46-65 ผู้เรียก**เลือกคีย์เองไม่ได้** (คีย์มาจาก `IsInflow`) ✅ · ยอดรูปแบบ Invariant `N2` + สกุล :78-83 ✅ · ไม่แต่งค่าเมื่อไม่มีชื่อ/บรรทัด ✅ (เทสต์ `ไม่มีชื่อคู่ค้าหรือบรรทัด_ไม่แต่งค่า_ไม่มีบรรทัดนั้น`)

### 4.2 Migration `NotificationEventSplit` — idempotent · tenant

- SQL :21-44 คัดลอก `payment_received → payment_made` **ต่อ CompanyId** เฉพาะบริษัทที่ยังไม่มีแถวคีย์ใหม่ (`NOT EXISTS` ไม่กรอง `IsDeleted` ของปลายทาง = ถ้าผู้ใช้เคยลบแถวใหม่ ก็ไม่สร้างกลับ ✓) · `ON CONFLICT DO NOTHING` มีผลจริงเพราะมี unique index `IX_NotificationSettings_CompanyId_EventKey_RecipientRole` (DatabaseMigrationHelper.cs:3830) และ `IX_NotificationPreferences_CompanyId_UserId_EventKey` (:3863) ⇒ บูตพร้อมกันหลายเครื่องไม่ซ้ำ ✅ · ลำดับหลังคอลัมน์ `EnableLineGroup` + ตาราง `NotificationPreferences` (:3866-3868) ✅
- ไม่มี `CompanyId` รั่วข้ามบริษัท (SELECT จากแถวของบริษัทเดียวกัน) ✅ · Db `ค_migrationคัดลอกค่าตั้งฝั่งรับไปฝั่งจ่าย_เฉพาะบริษัทที่ยังไม่มีแถว_idempotent` ✅
- ⚠️ เล็ก: `NOT EXISTS` ดู `CompanyId + EventKey` ไม่ดู `RecipientRole` ⇒ บริษัทที่มีแถว payment_made ของ role หนึ่งแล้ว (เช่น Accountant ที่ตั้งเองก่อนบูต) จะไม่ได้แถว Owner คัดลอกมา — ตรงเจตนา "ไม่ทับค่าที่ผู้ใช้ตั้งเอง" (doc comment) ✅ ไม่ใช่บั๊ก

### 4.3 ไม่รั่วไปกลุ่ม LINE กลางของแพลตฟอร์ม
`NotificationAudiencePolicy.AudienceOf("document.payment_made")` ไม่ตรง prefix `platform.ops.`/`subscription.`… :77-86 ⇒ `Tenant` ⇒ `RouteLine` ปฏิเสธ `PlatformGroup` :96-97 (`NOTIFY-TENANT-NOT-PLATFORM`) ✅ · `DefaultMatrix` :138-142 ส่งกลุ่ม LINE **ของบริษัท**เฉพาะเมื่อผูกไว้ · ปิดค่าตั้งต้นได้จริง: Db `ก_เอาติ๊กค่าตั้งต้นออกแล้วบันทึก_ไม่ประกาศกลุ่มกลับมา` ✅

### 4.4 ลิงก์ `NotificationLink.Absolute` :22-42
ไม่รู้โดเมน ⇒ **ไม่ใส่ลิงก์** (ไม่ใช่ลิงก์กดไม่ได้) · ปฏิเสธ `//host` · `javascript:` · backslash ✅ เทสต์ 5 ตัว

---

## 5. นโยบายอนุมัติอัตโนมัติ (รอบ 248) — ✅

### 5.1 ชุดห้ามปล่อยครอบทุกคำเตือนที่กฎหมายต้องมีคน?

กลไกเป็น**โครงสร้าง** ไม่ใช่รายชื่อ: `Releases(policy, w)` ปล่อยได้เฉพาะคำเตือนที่ `CodeOf(w)` ไม่ null (:79-81) และ `Catalogue` :58-63 มี**รหัสเดียว** = `AR-OVERDUE` ⇒ ทุกคำเตือนอื่นปล่อยไม่ได้โดยไม่ต้องอยู่ใน `NeverPassable` · `NeverPassable` :76-77 ([Σ-GAP] · VAT ไม่อยู่บนกระดาษ) ตรวจซ้ำอีกชั้นที่ `ApprovalAcknowledgement.Unacknowledged` :105 ✅

| คำเตือน/ด่าน | ปล่อยด้วยนโยบายได้? | หลักฐาน |
| --- | --- | --- |
| ผู้ซื้อ §86/4 ไม่ครบ | ❌ ไม่ได้ (ไม่มีป้าย) | เทสต์ `R248_ทิศตรงข้าม_นโยบายไม่ปล่อยคำเตือนที่ไม่มีป้าย_ผู้ซื้อ86_4_ยังหยุด` |
| [Σ-GAP] · VAT-NOT-ON-PAPER | ❌ | `NeverPassable` + เทสต์ `R248_ทิศตรงข้าม_SigmaGap_และVATไม่อยู่บนกระดาษ_ไม่มีวันถูกปล่อย` |
| งวดปิด · งบประมาณบล็อก · วงเงินเครดิตบล็อก · วงเงินอนุมัติ | ❌ — เป็น **exception** ไม่ใช่ warning ⇒ นโยบายไม่แตะ | `NeverList` :65-72 ระบุ `APPROVAL-LIMIT` เพื่ออธิบาย · `Validate` :101-102 ปฏิเสธรหัสที่ไม่อยู่ใน Catalogue |
| §65 ตรี | ⚠️ ผ่าน**โดยไม่ต้องมีนโยบาย** สำหรับทุกทางเข้าไม่มีคน (`PassesWithoutPerson` :65-72 ← คำตัดสินข้อ 110 · ก่อนรอบ 248) — ทิ้งร่องรอย `APPROVE-UNATTENDED-PASSED-S65` + หมายเหตุบนใบ | ไม่ใช่ของรอบ 248 · ยอมรับตามคำตัดสินเดิม |
| AR-OVERDUE (ควบคุมสินเชื่อภายใน) | ✅ ปล่อยได้เมื่อตั้ง + ลงหมายเหตุ + audit | ตรงเจตนา |

### 5.2 `BindingHash` :149-183 — canonical · เสถียร
- `v1|c=<guid D>|t=<type>|aa|ae|pricesIncludeVat|billDiscount…|currency|whtCertType|L,qty,unitPrice,vatRate,whtRate,discount%,discountAmt,accountId,productCode…` · ตัวเลข `"0.########"` `InvariantCulture` (ทั้ง Number และ String-ที่-parse-ได้) · bool → 1/0 · JSON อ่านไม่ได้ ⇒ `|unreadable` ⇒ ค่าเฉพาะ · SHA-256 hex · ผู้เขียน/ผู้ตรวจใช้ฟังก์ชันเดียว (`RecurringApprovePolicyLoader.CurrentBindingHash` :25-26 ← :46) ✅ กฎ #4 C
- ⚠️ สังเกต (ไม่กระทบเงิน): (ก) ไม่รวม `description`/จำนวนบรรทัดโดยตรง — แต่จำนวนบรรทัดสะท้อนผ่านจำนวน `|L` ✅ (ข) String ที่ parse เป็น decimal ได้ (`productCode "007"` → `"7"`) ชนกับ `"7"` — เปลี่ยนรหัสสินค้า 007→7 ไม่ทำให้ hash เปลี่ยน · ไม่กระทบยอด (ค) ทศนิยมเกิน 8 ตำแหน่งถูกตัด — ไม่มีค่าเงินเกิน 8 ตำแหน่งในระบบ
- ประทับตอนตั้งนโยบาย + เขียน audit chain: `RecurringAutopilotService.cs:362` `AddChainedAuditLog` ✅ · นโยบายที่ตั้งก่อนมีการผูก (`BindingHashValue == null`) = ไม่ตรง ⇒ หยุด (:186-187) ✅ ระวังไว้ก่อน

### 5.3 เทสต์
`AutopilotRound248Tests` (2 ตัวทิศตรงข้ามข้างบน + ชุดอื่น) · `RecurringTransactionDbTests` · **ไม่มี**: เทสต์ที่พิสูจน์ว่า hash เปลี่ยนเมื่อแก้ `unitPrice`/`accountId`/`contactId` และ**ไม่เปลี่ยน**เมื่อแก้ `description` (ล็อก canonical ทั้งสองทิศ) — **F7**

---

## 6. การปัดเศษ · ชนิดเงิน · ยืม field — ✅

- `git log -p --since=3d` บรรทัดที่เพิ่ม `Math.Round` ใน `Accounting/**/*.cs` = **29 จุด · ทุกจุด `MidpointRounding.AwayFromZero`** (ไม่พบ `Math.Round(x, 2)` เปล่า) ✅
- เงินเป็น `decimal` ทุก field ใหม่ (`FixedAssetCostSource.Amount/NonClaimableVatIncluded numeric(18,2)` · `ContractPrice decimal?` · `Amounts` record · `PaymentNotificationFacts.Amount/BalanceDue`) ✅ · `AmountText` ใช้ `InvariantCulture` ไม่พึ่ง culture เครื่อง ✅
- ยืม field: `FixedAsset.PurchaseDate` ← วันพร้อมใช้ (ดู §1.11) — มี `AvailableForUseDate` แยก · ยอมรับได้ · `PaymentIntent.Amount` = ยอดสุทธิ (ไม่ใช่ gross) — สอดคล้องความหมายเดิม "ยอดที่ลูกค้าโอน" ✅ · ไม่พบการเอา field เดิมมาเก็บคนละเรื่อง

---

## 7. รายการผลตรวจเรียงลำดับ

| # | ระดับ | เรื่อง | สถานการณ์ที่ผิด | แก้ | effort |
| --- | --- | --- | --- | --- | --- |
| F1 | **Medium** | `ResolveLinePostedGlAmountAsync` (DocumentService.cs:17320-17336) ยังใช้ `Standalone` + ไม่รวม CIL ใน `purchaseSide` + ไม่คืน 0 ให้ CIL ที่ตัดชำระ | แก้ผังบรรทัด CIL (VAT 7% ค้างผังเก่าเงียบ) · บรรทัดที่มีภาษีออกแทน (gross-up ค้างผังเก่า) · ผูกสินทรัพย์หลังย้ายผัง ⇒ 409 แล้วต้นทุนทะเบียนต่ำเท่า VAT | ใช้ `PurchaseLineGlAmount.Posted(isCIL, …, payerBorneShare)` · แยก helper "ส่วนออกแทนต่อบรรทัด" จาก `LoadCostLineFactsAsync` :111-121 ให้ทั้งสองเส้นเรียก · เพิ่ม CIL/route Settlement · Db test reclassify CIL ⇒ ผังเก่าสุทธิ 0 | S |
| F2 | Low-Med | ลำดับล็อกกลับด้านในช่อง "ตรวจ AnyAsync นอกล็อก → ล็อกใบ → ถือคีย์" (`LockCostSourcesIfLinkedAsync` :198-204 + `ReleaseCostSourcesForVoidAsync` :350) | ผูก/ยกเลิกใบเดียวกันพร้อมกันพอดีช่วงมิลลิวินาที ⇒ deadlock ⇒ ธุรกรรมหนึ่งถูกยกเลิก (ล้มดัง ไม่เสียข้อมูล) | ถือคีย์ก่อน `FOR UPDATE` แถวใบเสมอในการยกเลิก/ลบถาวร (ต้นทุน advisory lock 1 ครั้ง) หรือ re-check ใต้ล็อกใบแล้วถือคีย์ก่อนอ่านแถวผูกโดยไม่ล็อกใบซ้ำ | S |
| F3 | Low-Med (นโยบาย) | ค่าเสื่อมงวดแรก = เดือนถัดจากวันพร้อมใช้ (`DepreciationSchedule.MonthIndexFor`) — พ.ร.ฎ.145 ม.4 เฉลี่ยตามระยะเวลา | เครื่องพร้อมใช้ต้นเดือน ขาดค่าเสื่อม 1 เดือนในปีแรก (ตัวอย่าง 8,500) ไปโผล่ปีสุดท้าย — เป็นธรรมเนียมเดิมทั้งระบบ ไม่ใช่ถดถอย | รอคำตัดสินเจ้าของ §6 ข้อ 3 (รายวัน/เดือนเดียวกัน) · ถ้าเปลี่ยน ต้องเปลี่ยนที่ helper เดียว + migration แผนที่ยังไม่โพสต์ | M (ตัดสินใจ) |
| F4 | Low | `EtdaTimestampToken` ตัดที่ 500 ตัวเงียบ (DocumentEmailService.cs:595) | token ยาว ⇒ หลักฐานไม่ครบ | ปฏิเสธพร้อมบอก หรือเก็บไฟล์หลักฐานเป็นหลัก (มีอยู่แล้ว) แล้วเก็บ token เต็มเป็น text | XS |
| F5 | Low | ใบที่ตั้ง WHT ให้ผู้ซื้อบุคคลธรรมดา เตือนเฉพาะตอนออกลิงก์ (`WhtMismatchNote`) · ลูกค้าโอนยอดค้างสุทธิ ⇒ ใบปิดโดยรายได้ขาดเท่า WHT | ต้นเหตุตอนสร้างใบ | ยก `WhtMismatchNote` ไปเป็นคำเตือนก่อนอนุมัติ (ชุด WHT-PAYEE มีอยู่แล้ว) + Db test handler เส้นบุคคลธรรมดา | S |
| F6 | Low | ป้าย/ผู้อ่านที่ยังไม่รู้จักสถานะ "ระหว่างจัดหา": `OpeningBalanceReconciliation.cs:78` · ส่งออกทะเบียน `ImportExportService.cs:1639` · รายงานโชว์ "วันที่ซื้อ" = วันพร้อมใช้ · เคส #4 (ใบที่เกินอยู่ก่อน) ไม่มี problem row | ตัวเลขกระทบยอดยกมา/ส่งออกปนสินทรัพย์ที่ยังไม่อยู่บนผังสินทรัพย์ | กรอง/ติดป้าย `IsUnderAcquisition` + เพิ่ม problem "ใบนี้มีทะเบียนเกิน GL อยู่ก่อน" ใน `GetCostSourcesAsync` | S |
| F7 | Low (เทสต์) | ด่านที่ไม่มี negative test ผ่านเส้นจริง: purge+สินทรัพย์หลายใบ · ย้ายฝั่ง CN/DN · ต่างประเทศ · resync · OCR 10215 · reclassify :8324 · `EnsureNotAssetCostTransferAsync` · XML e-Tax (TypeCode/IssueDateTime/BranchID) · BindingHash สองทิศ · handler ผู้ซื้อบุคคลธรรมดา | ถอดการเรียกใน service แล้วเทสต์ยังเขียว (F2 ข้อ 6) | เพิ่มกติกา `required_call_site_check` สำหรับ 7 จุดเรียกนอก FixedAssetService (R249FA ครอบเฉพาะไฟล์ CostSources) + Db test ละ 1 ตัว | M |
| F8 | Low (กับดัก) | `IsCashInflowDocAsync` คืน "ออก" ให้ CreditNote ทุกฝั่ง (วันนี้ไปไม่ถึง) · ใช้ร่วมกับตัวปรับยอดธนาคาร | ถ้าเปิดบันทึกเงินคืนบน CN ซื้อในอนาคต ⇒ ธนาคารติดลบ + แจ้งเตือนผิดฝั่ง | ย้ายเป็น `Helpers/CashFlowDirection` รับ `(type, relatedType, cnDnSideOverride)` ผ่าน `AdjustmentNoteAccount.ResolveSide` + เทสต์ทุกชนิด | S |
| F9 | Info | CLAUDE.md §F เขียน `DocumentTypeCode (T01–T04)` — โค้ดใช้ 388/80/81/T02/T03 ตาม codelist ETDA (ถูก) · `Receipt` ไม่มี VAT (T01) ยังไม่อยู่ในแผนที่ | doc ≠ โค้ด (doc ผิด) | แก้ CLAUDE.md §F ให้ชี้ `Helpers/EtaxDocumentTypeMap` | XS |

---

## 8. ตรวจแล้วไม่ใช่บั๊ก (รวมทุกหมวด)

1. "ลิงก์ชำระลบ WHT ซ้ำ" — แก้แล้วจริงที่ `AmountsFor` (Net = ยอดค้างซึ่งสุทธิอยู่แล้ว · Gross = Net + WHT) · เทสต์ `เงินเข้ายอดสุทธิพอดี…` · CHANGELOG 6448
2. `WhtForPayment` คืน null เมื่อยอดไม่ตรง ⇒ ตัวบันทึกรับชำระแนะนำเอง (`WhtPaymentSuggestion.Compute` สัดส่วนตามยอด ไม่เกิน NetCap) — ไม่ใช่ "ไม่บันทึก WHT"
3. `OverRegistrationIncrease` ปล่อยเคส "ใบที่เกินอยู่ก่อน" (#4) — ตั้งใจ ("ห้ามแย่ลง") ไม่ใช่ตัวตัดสินผิด
4. `PlaceInServiceAsync` คำนวณเครื่องหมายจากแถวผูกแทน `SignFor` — ให้ผลเท่ากันเสมอ (แถวของใบเดียวกันเครื่องหมายเดียว)
5. `ApplyPostCapitalizationCostAsync` `elapsed` นับจาก `PurchaseDate` (= วันพร้อมใช้หลังโอน) ด้วย `MonthIndexFor` เดียวกับตัวโพสต์ค่าเสื่อม ⇒ งวดที่ลงแล้วไม่ถูกแตะ (สอดคล้อง `EffectiveDepParams` :861-873)
6. `NotificationEventSplit` ไม่กรอง `IsDeleted` ของแถวปลายทางใน `NOT EXISTS` — ตั้งใจไม่สร้างกลับเมื่อผู้ใช้เคยลบ
7. `RevenueWarning` e-Tax ≤ 30 ล้านเป็นคำเตือน — ตามข้อเสนอ E12 (สิทธิจริงมาจาก ก.อ.01)
8. e-Tax `Receipt → T03` สำหรับใบเสร็จที่ถือ VAT — ถูก (ใบเสร็จธรรมดา/ย่อถูกกันโดย `EtaxAutoIssueScope`)
9. `NeverList` ใส่ `PERIOD-CLOSED`/`APPROVAL-LIMIT` ที่ไม่ใช่ warning — เพื่อแสดงในหน้า ไม่ใช่ช่องโหว่
10. การปัดเศษ `NonClaimableVatIncluded` ตามสัดส่วนเมื่อผูกบางส่วน (:590-591) อาจให้ Σ ส่วนต่างจาก VAT ทั้งบรรทัด ±0.01 — เป็นค่าแสดงผล/ตรวจ ไม่ใช่ค่าที่ลง GL (`Amount` ลง GL)

---

## 9. สิ่งที่ยังไม่ได้ยืนยันในเครื่องนี้

- ไม่ได้คอมไพล์/รันเทสต์ในเครื่อง (ไม่มี .NET SDK) — อ้างผล CI ของ `fe9036f7`: build ✅ · Db tests ✅ · static checks ยังรันอยู่ตอนตรวจ
- ไม่ได้ตรวจ Schematron ของ ETDA กับ XML ตัวอย่างจริง (ไม่มี fixture ในชุดรอบ 244)
- F2 เป็นการไล่ลำดับล็อกจากโค้ด ไม่ได้จำลองพร้อมกันบน PostgreSQL
