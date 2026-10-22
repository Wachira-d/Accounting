# POS_MTO_DESIGN.md — POS (คิวการจอง · ช่างรายบรรทัด · รายการพิเศษ) + สินค้าตามสั่ง

> ทีม K · 2026-10-22 · ขอบเขต: งาน 3.2 (POS) + 3.3 (Made-to-order ร้านออนไลน์) · ต่อจาก `POS_MULTI_BRANCH_ANALYSIS.md`
> · **ตัวตัดสินทุกตัวอยู่ใน `Helpers/` + เทสต์** — service แค่ดึงข้อมูล (กรอง `CompanyId`) แล้วส่งเข้า · หน้าเว็บแสดงผลอย่างเดียว

## 0. สถานะสั้น ๆ

| งาน | สถานะ | ตัวตัดสิน (Helpers/) | เทสต์ |
| --- | --- | --- | --- |
| คิวการจองของวัน → เปิดบิล (เว็บ + โต๊ะ) · เติมลูกค้า/บริการ/ช่าง · ผูก booking กับ order ไม่ให้เปิดซ้ำ | ✅ | `PosBookingIntake` | `PosCustomItemAndBookingIntakeTests` |
| ช่าง/นักบำบัดระดับบรรทัด → ฐานคอมมิชชัน + ต้นทุนแรงงาน | ✅ | `PosStaffAttribution` · `CommissionPlanRules.PosStaffSalesBasis` | `PosStaffAttributionTests` |
| ปุ่ม "รายการพิเศษ" (ชื่อ · ราคา · ประเภท · บัญชีรายได้) — ไม่ตัดสต็อก · ลงบัญชีเหมือนสินค้า | ✅ (VAT ตามอัตราบิล — ดู §4) | `PosCustomItemRule` · `PosRevenueSplit` | `PosCustomItemAndBookingIntakeTests` · `PosRevenueSplitTests` |
| Dynamic option builder (radio/dropdown/checkbox/ข้อความ/รูป · บังคับ · ราคาบวก · min–max · วันผลิตเพิ่ม) — ต่อยอด modifier เดิม | ✅ | `ModifierSelectionRules` · `ModifierInputType` | `ModifierSelectionRulesTests` |
| Lead time "สั่งวันนี้ รับได้ใน N วัน" + ตามตัวเลือก + ข้ามวันหยุดร้าน | ✅ | `MtoLeadTime` | `MtoLeadTimeShippingPaymentTests` |
| การรับ: มารับเอง / จัดส่ง · ค่าส่ง เหมาจ่าย · ตามน้ำหนัก · ตามโซน · ส่งฟรีเมื่อครบ · บรรทัดค่าส่งพร้อม VAT | ✅ (ตามระยะทาง กม. = backlog) | `ShippingFeeRules` · `FulfillmentMethod` | `MtoLeadTimeShippingPaymentTests` · `CmsCommerceOrderLinesTests` |
| เงื่อนไขชำระ เต็ม/มัดจำ %/มัดจำคงที่/ชำระเมื่อรับ — ตัวคำนวณ "ยอดที่ต้องจ่ายตอนนี้" ตัวเดียว | ✅ | `MtoPaymentTerms` · `PaymentTermMode` | `MtoLeadTimeShippingPaymentTests` |

## 1. คิวการจอง → เปิดบิล

- **ทางเข้า**: `GET pos/booking-queue?date=` (รวม `SiteBooking` จากเว็บ + `PosReservation` โต๊ะ/คิว ของวันตามปฏิทินไทย) ·
  `POST pos/booking-queue/open {sessionId, source, bookingId, staffId?}` (สิทธิ์ `POS.Cashier`) · หน้า POS ปุ่ม **📅 คิวจอง**
- **ตัวตัดสินเดียว** `PosBookingIntake.Decide` ใช้ทั้งป้ายบนคิวและด่านตอนเปิดจริง: ยกเลิก/ไม่มา/เสร็จแล้ว = เปิดไม่ได้ ·
  มีบิลที่ยังไม่ยกเลิก = เปิดซ้ำไม่ได้ (ทางไปต่อ "ไปที่บิล") · ระบบจองออก**เอกสารรายได้**แล้ว (ใบกำกับของการจองแบบรับประกัน
  รวมร่าง) = เปิดไม่ได้ (รายได้/ภาษีขายซ้ำ) · **มีมัดจำ = เปิดได้พร้อมคำเตือน** (POS ยังหักมัดจำไม่ได้ — §4)
- **กันเปิดซ้ำที่ชั้นฐานข้อมูล**: `PosOrders.SourceBookingType/SourceBookingId` + unique partial index
  `UX_PosOrders_SourceBooking_Open` (ไม่นับบิล Voided) — ตั้งตั้งแต่ INSERT แรก (`CreateOrderCoreAsync`) · สองเครื่องกดพร้อมกัน =
  ใบที่สองได้ข้อความ "เพิ่งถูกเปิดจากอีกเครื่อง"
- **เติมให้**: ลูกค้า (ชื่อ/Contact ผ่าน `SiteCustomer.ContactId`) · โต๊ะ/จำนวนคน · เวลานัด · บริการ (บริการที่ผูกสินค้า = บรรทัดสินค้า ·
  ไม่ผูก = รายการพิเศษชนิดบริการ ราคาตามการจอง) · ช่าง (ที่แคชเชียร์เลือก > ที่จองไว้ → บันทึกกลับลงการจอง `AssignedStaffId`)
- **สถานะการจองตามบิล**: เปิด → โต๊ะ `Seated` / เว็บ `InProgress` · ยกเลิกบิล → ปล่อยการจอง (`ReleaseSourceBookingAsync`) ·
  ปิดบิล → โต๊ะ `Completed` · เว็บ `Completed` **เฉพาะเมื่อไม่มีเงิน/เอกสารผูกกับการจอง** (มีมัดจำ ⇒ ปิดที่หน้าการจองซึ่งเป็นเส้นตัดมัดจำ —
  POS ห้ามประทับ "เสร็จ" แทนเส้นเงิน) · รวมบิล: ย้ายการผูกไปบิลปลายทางสองจังหวะ · การจองคนละรายการรวมกันไม่ได้

## 2. ช่างรายบรรทัด + คอมมิชชัน

- `PosOrderItem.StaffId/StaffName` (ตรึงชื่อตอนขาย) · ตรวจว่าเป็นสมาชิกบริษัท (`ResolveStaffAsync` — sync ออฟไลน์ผ่อนผัน: ช่างถูกถอด
  ระหว่างออฟไลน์ ⇒ บรรทัดไม่มีช่าง + `LogError` ไม่ทำให้บิลที่ขายแล้ว sync ไม่ได้)
- ตั้ง/เปลี่ยนช่าง: ปุ่ม 👤 บนบรรทัด → `PUT pos/orders/{id}/items/{itemId}/staff` (บิลปิดแล้ว = เปลี่ยนไม่ได้ ฐานคอมตรึง) · แพ็กเกจบริการ:
  ขั้นตอนที่ยังไม่มีคน/ยังเป็นช่างเดิมของบรรทัดตามช่างใหม่ (ค่าคอมต่อขั้นตอนเดิมทำงานต่อ)
- **ยอดของช่าง** (`PosStaffAttribution`): ยอดบรรทัด × สัดส่วนส่วนลดท้ายบิล (`PosRefundMath.DiscountFactor` ตัวเดียวกับคืนเงิน) ×
  สัดส่วนก่อน VAT ของบิล × (จำนวนที่ไม่ถูกคืน) · ไม่รวมค่าบริการ/ทิป · ปัด 2 ตำแหน่ง AwayFromZero
- **ต่อเข้า commission เดิม**: ฐานใหม่ `PosStaffSales` ใน `CommissionPlanRules.Bases` (หน้า `commission.html` อ่านชุดฐานจากเซิร์ฟเวอร์) ·
  `CommissionService.CalculateAsync` นับบิลที่ปิดในเดือน (เวลาไทย) ของผู้ใช้ที่ผูกกับการมอบหมาย (UserId หรือ Employee.UserId) ·
  มอบหมายแผนฐานนี้ให้พนักงานที่ไม่ผูกผู้ใช้ = บล็อกพร้อมทางไปต่อ · ข้อมูลเก่าที่หลุด ⇒ หมายเหตุบนแถวผล (`CommissionCalcResponse.Note`)
- รายงาน `GET pos/reports/staff-sales?from&to` (ต้นทุนแรงงาน/ยอดรายช่าง) — ตัวคิดเดียวกับแผนคอม

## 3. รายการพิเศษ (Quick Add)

- ปุ่ม **➕ รายการพิเศษ** บน POS: ชื่อ (พิมพ์บนสลิป/ใบกำกับ) · ราคา/หน่วย (รวม VAT) · จำนวน · ประเภท (บริการ/สินค้าไม่ตัดสต็อก) ·
  บัญชีรายได้ (ผังหมวดรายได้ที่ลงรายการได้ของบริษัท — `PostingAccountGuard` + `AccountType.Revenue`) · ช่าง
- `PosOrderItem.IsCustomItem/CustomItemKind/RevenueAccountId` · ไม่มี ProductId ⇒ ไม่แตะ ledger · COGS 0
- **ลงบัญชี**: เครดิตรายได้แบ่งตามบัญชีของบรรทัดด้วย `PosRevenueSplit` **ทั้ง JE ขายและ JE คืนเงิน** (คืนเข้าบัญชีเดียวกับที่ขาย) ·
  บิลที่ไม่มีบัญชีพิเศษ = กองเดียว 41000 หน้าตาเดิมทุกสตางค์ · บัญชีที่ตรึงถูกปิดภายหลัง ⇒ ล้มดังตอนปิดบิล (ไม่โยนเข้า 41000 เงียบ)
- ส่งช่องบัญชีรายได้มากับสินค้า/แพ็กเกจปกติ ⇒ โยนพร้อมเหตุ (ห้าม silent no-op)
- สืบทอด: แยกบิล/รวมบิล คัดลอกช่าง + ธงรายการพิเศษ + บัญชีรายได้ (รวมบิลเดิมตั้ง VAT รายบรรทัดเป็น 0 ⇒ คืนเงินบิลที่รวมแล้วไม่กลับภาษีขาย — แก้ให้คัดลอกตามจริงแบบเดียวกับแยกบิล)

## 3.5 สินค้าตามสั่ง (ร้านออนไลน์)

- **Option builder = modifier เดิมของ POS** (`ProductModifierGroup/Option`) เพิ่ม `InputType` (null = อนุมานจาก `AllowMultiple` — กลุ่มเดิมไม่เปลี่ยน) ·
  `MinSelect/MaxSelect/MaxTextLength/InputPriceAdjustment/InputExtraLeadDays` + `Option.ExtraLeadDays` · หน้า `pos-modifiers.html`
  ตั้งชนิดช่อง/ขอบเขต/วันเพิ่ม และ**ผูกสินค้า** (เดิมหน้าเว็บส่ง `productIds: []` เสมอ ⇒ กลุ่มไม่เคยถูกใช้กับสินค้าใดเลย ·
  เดิมรับ id สินค้าดิบ ⇒ ผูกสินค้าบริษัทอื่นได้ — ตรวจแล้ว)
- **ราคา server-side**: ตะกร้ารับแค่ "เลือกอะไร" (`CartModifierChoice` ไม่มีราคา) → `ModifierSelectionRules.Evaluate` จากฐานข้อมูล ·
  ผิด (ไม่เลือกช่องบังคับ · เกินจำนวน · ตัวเลือกปิด/ไม่อยู่ในสินค้า · รูปไม่อยู่ในตะกร้านี้) = 400 พร้อมเหตุ · ตัวเลือกตรึงเป็น JSON บน
  `SiteCartItem/SiteOrderLine.ModifierSelectionsJson` · บรรทัดเดียวกันรวมได้เฉพาะตัวเลือกเหมือนกัน · คำอธิบายตัวเลือกไปบน
  บรรทัดเอกสารขาย/ใบเสนอราคา (`SummaryOf` ตัวเดียว)
- **รูปตัวอย่าง**: `POST commerce/cart/{cartId}/option-image` (JPG/PNG/WEBP ≤ 5 MB · ≤ 10 รูป/ตะกร้า) → `IFileAttachmentService.UploadAnonymousBytesAsync`
  (ที่เก็บไฟล์กลาง · ผู้อัปโหลด null) ชนิด `SiteOrder` (ด่านสิทธิ์ `CmsOrderManage`) · ตอนสั่งซื้อย้าย `EntityId` จากตะกร้าเป็นคำสั่งซื้อ
- **วันรับ**: `SiteProduct.LeadTimeDays` (null = ไม่ใช่สินค้าตามสั่ง) + วันเพิ่มของตัวเลือก (ผลรวม) · ปฏิทินร้าน `SiteCommerceConfig.MtoClosedWeekdays/
  MtoCutoffHour/MtoSkipCompanyHolidays` (+ `PublicHolidays` ของบริษัท) · ออเดอร์หลายรายการ = วันช้าสุด · ตรึง `SiteOrder.EarliestReadyDate`
- **ค่าส่ง** (`ComputeCheckoutAsync` ตัวเดียวของหน้าเช็กเอาต์ `POST cart/{id}/checkout-quote` และ `CreateOrderAsync`): จัดส่ง = อัตราของโซนที่ตรงที่อยู่
  (เหมาจ่าย/ตามน้ำหนักจาก `SiteProduct.WeightKg` ฝั่งเซิร์ฟเวอร์/ฟรี/ส่งฟรีเมื่อครบ) · ร้านไม่ตั้งโซน = ค่าเหมาจ่ายระดับร้าน
  (`FlatShippingRate/FreeShippingThreshold` — เดิมไม่มีผู้อ่าน) · มารับเอง (`EnablePickup`) = 0 · client ส่งแค่ rate id
  **เดิม `SiteOrder.ShippingAmount` ไม่มีผู้เขียนเลย** (ลูกค้าเห็นค่าส่งแต่ไม่ถูกเก็บ) · บรรทัดค่าส่งในเอกสารขาย VAT ตามสินค้าในบิล (§79 — เดิม 0 ⇒ ภ.พ.30 จัดเป็นส่งออก)
- **ยอดที่ต้องจ่ายตอนนี้**: `MtoPaymentTerms.Compute` ตอนสั่ง (ใช้เฉพาะออเดอร์ที่มีสินค้าตามสั่ง · ไม่มี = เต็มจำนวน) → ตรึง `PaymentTermMode/DepositRequiredAmount` ·
  ทุกทางเข้าหลังสั่ง (`GetOrderAsync` · `RecordPaymentSlipAsync` · `PublicPaymentResolver.ResolveOrderAsync`) ใช้ `FromSnapshot` ตัวเดียว ·
  **การลงบัญชีไม่เปลี่ยน** — มัดจำ = รับชำระบางส่วนของเอกสารขายผ่าน `ConfirmPaymentAsync` เดิม (ทีม E ตรวจเส้นเงิน · ไม่มี posting ใหม่)

## 4. Backlog (ยังไม่ทำ + เหตุผล)

1. **รายการพิเศษ "ยกเว้น VAT" รายบรรทัดบน POS** — POS คิด VAT ระดับบิลอัตราเดียว (`RecalculateOrder`) และใบกำกับเต็มรูปจาก POS
   (`PosTaxInvoiceLines.Build`) เขียนอัตรากฎหมายเดียวทุกบรรทัด ⇒ ต้องแก้ทั้งสามชั้น (ยอดบิล · ใบกำกับ · รายงานภาษีขายแยกคอลัมน์ 7%/0%/ยกเว้น)
   พร้อมกัน — ทำครึ่งเดียว = ใบกำกับยอดไม่ตรง · หน้าจอบอกตรง ๆ ว่ายังไม่รองรับ (ไม่มีช่องให้เลือกแล้วไม่มีผล)
2. **หักมัดจำของการจองในบิล POS** — POS ไม่มีวิธีชำระ "ตัดมัดจำ" และเส้นมัดจำเป็นของ `DepositService` (ทีม E) · ตอนนี้: เตือนก่อนเปิดบิล +
   ไม่ประทับการจองเว็บว่าเสร็จเมื่อมีเงินผูก · ต่อสายเมื่อเส้นมัดจำมี API "ตัดมัดจำเข้าใบขาย POS" ตัวเดียว
3. **ค่าส่งตามระยะทาง (กม.)** — ระยะทางจาก client เชื่อไม่ได้ และยังไม่มีตัวหาระยะฝั่งเซิร์ฟเวอร์ (geocoder) · ใช้โซนตามจังหวัด/รหัสไปรษณีย์แทนได้
4. **ตัวเลือกสินค้าบน POS** — `pos.html` ยังไม่แสดงกลุ่มตัวเลือก (เดิมส่ง `modifiers: []` เสมอ) และ POS ยังรับ `PriceAdjustment` จาก client ·
   ทำต่อ = หน้าต่างเลือกตัวเลือกบน POS ที่ส่งแค่ option id แล้วเซิร์ฟเวอร์คิดราคา (`ModifierSelectionRules` ตัวเดียวกัน) — ระวังบิลออฟไลน์ที่ราคาเปลี่ยนระหว่าง sync
5. **`ServicePackage.RevenueAccountId` ไม่มีผู้อ่าน** — `PosRevenueSplit` พร้อมแล้ว · ต่อสายได้โดยตรึงบัญชีลง `PosOrderItem.RevenueAccountId` ตอนเพิ่มบรรทัดแพ็กเกจ
   (เปลี่ยน JE ของร้านที่ตั้งค่าไว้ — ต้องให้เจ้าของยืนยันก่อน)
6. **`StaffCommissionSummaries` ไม่มีผู้เขียน** (แท็บค่าคอมใน `pos-reports.html` ว่างเสมอ) — แทนได้ด้วยรายงาน `reports/staff-sales` + ค่าคอมต่อขั้นตอน
7. **หน้าจัดการการจองบนเว็บ (CMS)** ยังไม่มีช่องเลือกช่าง — ช่างถูกบันทึกจากคิว POS ตอนเปิดบิล (`SiteBooking.AssignedStaffId`)
8. **ใบสั่งผลิตจากคำสั่งซื้อตามสั่ง** (ครัว/โรงงาน) + แจ้งลูกค้าเมื่อพร้อมรับ — ข้อมูลพร้อมแล้ว (`EarliestReadyDate` · ตัวเลือก JSON)
