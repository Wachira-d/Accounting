# MTO_ORDER_REVIEW.md — สินค้าตามสั่ง (made-to-order) ร้านออนไลน์ end-to-end (รอบ 209 · ทีม A)

> 2026-10-29 · อ่านอย่างเดียว · ตรวจคำอ้างใน `docs/POS_MTO_DESIGN.md` §0/§3.5/§3.6/§4 เทียบโค้ดจริง · หลักฐาน `file:line` (path ย่อจาก `Accounting/`)
> สถานะ ✅ ทำได้ · ⚠️ ทำได้แต่ขาด · ❌ ไม่มี / มีแต่ไม่มีผู้อ่าน (F2 ข้อ 2) · ขนาด S/M/L · **ยังไม่ได้คอมไพล์/รันในเครื่องนี้ — ตรวจจากโค้ด**

## §0 สรุป

1. **ตัวตัดสินแกนกลางของ MTO ใช้ได้จริงและต่อสายแล้ว**: ตัวเลือก (`ModifierSelectionRules`) · วันพร้อม (`MtoLeadTime`) · ค่าส่ง (`ShippingFeeRules`) · ยอดที่ต้องจ่ายตอนนี้ (`MtoPaymentTerms`) · ใบมัดจำ/ใบส่วนที่เหลือ (`SiteOrderDepositInvoicing`) ทุกตัวเรียกผ่าน `ComputeCheckoutAsync` ตัวเดียว (`Services/Implementations/CmsCommerceService.cs:2665`) — คำอ้างใน §0 ของ doc **ตรงกับโค้ด**
2. ค่าตั้ง MTO 11 ช่องมีครบสามขา (field → UI `pages/cms-orders.html:54-92` → ผู้อ่าน) ✅
3. **❌ ร้ายแรงสุด: หน้า "สั่งซื้อสำเร็จ" ของลูกค้าแขกอ่านออเดอร์ไม่ได้** — `storefront.html:1422` เรียก `GET commerce/orders/{id}` ซึ่งเป็น `[Authorize]` + `RequireSiteRole(Admin, OrderManager)` (`Controllers/CmsCommerceController.cs:14,226-227`) ⇒ แขกได้ 401 ⇒ `order = null` ⇒ ยอดมัดจำ/วันพร้อมไม่แสดง · QR PromptPay สร้างยอด ฿0 (`storefront.html:1431,1441`) · กล่องจ่ายออนไลน์ถูกลบเพราะ `due <= 0` (`storefront.html:1587`)
4. **❌ ค่าตั้งร้านค้า 20 ตัวไม่มีผู้อ่าน และส่วนใหญ่ไม่มีช่องบนจอ**: `CheckoutMode` · `EnableCod` · `EnableOnlinePayment` · `EnableBankTransfer` · `AutoConfirmOrders` · `NotifyOnNewOrder` · `OrderNotificationEmails` · `AutoSyncToErp` · `OrderConfirmMessageTh/En` · `EnableGuestCheckout` · `RequireCustomerAccount` · `AutoDeductStock` · `EnableStockTracking` · `EnableTaxInvoice` ฯลฯ (§1)
5. **❌ ปิดการจัดส่งไม่ได้** (ร้านที่ให้มารับเองอย่างเดียว เช่น ร้านเค้ก) — `FulfillmentMethod.Normalize` ค่าว่าง = จัดส่ง และไม่มีด่าน "ปิดจัดส่ง" · `EnableShipping=false` แค่ทำให้ค่าส่งเป็น 0 (`CmsCommerceService.cs:2684`) ไม่ได้ซ่อนตัวเลือกจัดส่ง
6. **❌ ฟอร์มสั่งซื้อตายตัว**: ชื่อ/เบอร์/อีเมลบังคับใน HTML (`storefront.html:1208-1210`) แต่เซิร์ฟเวอร์ไม่ตรวจเลย (`CreateOrderAsync` :606-625) · ไม่มีค่าตั้งแบบ `BookingFormFields` · ไม่มีช่องเพิ่มเอง · ไม่มีช่อง "วันที่ต้องการรับ"
7. **❌ ไม่มีการแจ้งเตือนใด ๆ ของออเดอร์ร้านออนไลน์** — `CmsCommerceService` ไม่รับ service อีเมล/LINE เลย (constructor :31-36) · เจ้าของไม่รู้ว่ามีออเดอร์ใหม่ · ลูกค้าไม่ได้ข่าวเมื่อของพร้อม/จัดส่งแล้ว
8. **❌ หลังบ้านไม่มีคิวผลิต**: รายการออเดอร์ไม่มีวันพร้อม/วิธีรับ (`OrderListResponse` `Models/DTOs/Cms/CommerceDtos.cs:481-492`) · กรอง/เรียงตามวันพร้อมไม่ได้ · ไม่มีสถานะ "พร้อมรับ" · ไม่มีใบสั่งผลิต (doc §4 ข้อ 9 ยอมรับว่ายังไม่ทำ)
9. ⚠️ จ่ายส่วนที่เหลือ: ทางจ่ายออนไลน์ใช้ `FromSnapshot` ถูกต้อง แต่ลูกค้าเข้าไม่ถึงเพราะข้อ 3 + ไม่มีลิงก์ส่งหาลูกค้า (ข้อ 7)
10. ข้อเสนอ: R-209-A1 (เปิดอ่านออเดอร์สาธารณะแบบจำกัดฟิลด์ · S) → A2 (ฟอร์มสั่งซื้อตั้งค่าได้ · M) → A3 (สวิตช์วิธีรับ/วิธีจ่ายที่มีผลจริง · M) → A4 (แจ้งเตือน · M) → A5 (คิวผลิต + สถานะพร้อมรับ · M)

---

## §1 ตารางสวิตช์ตั้งค่า (field · UI · reader · default · verdict)

entity = `Models/Entities/CmsSite.cs:115-219` (`SiteCommerceConfig`) · API = `Controllers/CmsCommerceConfigController.cs:47-150` (บันทึกได้ทุกช่อง) · UI ตั้งค่าร้านค้าที่มีอยู่จริง = modal `#mtoModal` ใน `pages/cms-orders.html:54-92` (ตัวเดียว · `cms-edit.html` ไม่มีช่องของ commerce config — grep ทั้ง `wwwroot` ไม่พบ `checkoutMode|enableCod|autoSyncToErp|notifyOnNewOrder|...`)

### 1A. ค่าตั้งระดับสินค้า (MTO)

| ค่าตั้ง | UI | ผู้อ่าน | ค่าเริ่มต้น | ผล |
| --- | --- | --- | --- | --- |
| `SiteProduct.LeadTimeDays` (null = ไม่ใช่ MTO) `CmsCommerce.cs:51` | `cms-edit.html:640` (`prdLeadTime`) + hydrate :2437 | `CmsCommerceService.cs:97,153-154` เก็บ · :2740-2745 คิดวันพร้อม · หน้าสินค้า `storefront.html:1082-1083` | null | ✅ |
| `SiteProduct.WeightKg` | `cms-edit.html:2438` | `CmsCommerceService.cs:2687` (ค่าส่งตามน้ำหนัก) | null | ✅ |
| กลุ่มตัวเลือก (`ProductModifierGroup.InputType/MinSelect/MaxSelect/ExtraLeadDays` …) | `pages/pos-modifiers.html` (18 จุดอ้าง `inputType/extraLeadDays/minSelect`) | `EvaluateOptionsAsync` :2602 → `ModifierSelectionRules` · ตอนเพิ่มตะกร้า :528 | ไม่มีกลุ่ม | ✅ (ตั้งที่หน้า POS — ⚠️ เจ้าของร้านออนไลน์หาไม่เจอ ไม่มีลิงก์จาก `cms-edit` สินค้า) |
| `SiteProduct.StockBehavior` (InStockOnly/AllowBackorder/AllowPreorder) | **ไม่มี** (`cms-edit.html` ไม่มี `stockBehavior`) | `DeductStockAsync` :2091 | InStockOnly | ⚠️ สินค้าทำตามสั่งไม่มีสต็อกจะตกไปเส้น `AdjustStockAsync` ตอนยืนยันเงิน (:1576) — ต้องตรวจว่าสินค้าบริการ/ไม่นับสต็อกข้ามได้หรือล้มแล้วประทับ "ตัดสต๊อกไม่สำเร็จ" (ยังไม่ยืนยันจากโค้ด `ProductService`) |

### 1B. ค่าตั้งระดับร้าน (`SiteCommerceConfig`)

| ค่าตั้ง | UI | ผู้อ่าน (นอก entity/controller/tests) | ค่าเริ่มต้น | ผล |
| --- | --- | --- | --- | --- |
| `EnablePickup` | `cms-orders.html:61` | `CmsCommerceService.cs:2670-2672` (ด่าน) · :2680 ส่ง `PickupEnabled` | false | ✅ |
| `FlatShippingRate` / `FreeShippingThreshold` | `cms-orders.html:63-64` | `:2733` → `ShippingFeeRules.SiteFlatFee` | null | ✅ (⚠️ หน้าเว็บส่ง `?? 0` `cms-orders.html:337-338` ⇒ ล้างค่าไม่ได้ กลายเป็น 0 — ดู SHIPPING_REVIEW) |
| `MtoPaymentTermMode` / `MtoDepositPercent` / `MtoDepositFixedAmount` | `cms-orders.html:68-71` | `:2753-2754` · `CreateOrderAsync` :749-750 | null (= เต็ม) | ✅ |
| `MtoDepositInvoicing` | `cms-orders.html:74-75` (คำอธิบายจากเซิร์ฟเวอร์) | `:2761` → ตรึง `SiteOrder.DepositInvoicingMode` :718 | `full_at_first_payment` | ✅ |
| `ShippingVatAllocation` | `cms-orders.html:77-78` | `:727-740` | MaxRate | ✅ |
| `DepositForfeitOnCancelPercent` | `cms-orders.html:79` | `:897` `ForfeitDepositsOnCancelAsync` | null | ✅ |
| `MtoClosedWeekdays` / `MtoCutoffHour` / `MtoSkipCompanyHolidays` | `cms-orders.html:83-86` | `LoadShopCalendarAsync` :2651-2661 | null / null / true | ✅ |
| `EnableShipping` | **ไม่มี** | `:2684` (แค่ค่าส่ง = 0) | true | ❌ ไม่มี UI + ปิดแล้ว "จัดส่ง" ยังเลือกได้และฟรี (ผิดความหมาย) |
| `EnableShippingCalculation` | ไม่มี | 0 | false | ❌ ไม่มีผู้อ่าน |
| `CheckoutMode` (PaymentRequired/QuotationOnly/QuotationThenPayment/InquiryOnly) | ไม่มี | 0 | PaymentRequired | ❌ — ร้านเฟอร์นิเจอร์ที่ต้อง "ขอใบเสนอราคาก่อน" ตั้งไม่ได้ (มีแค่ปุ่มขอใบเสนอราคาหลังสั่ง `storefront.html:1517-1527`) |
| `AutoGenerateQuotation` | ไม่มี | 0 | false | ❌ |
| `EnableOnlinePayment` / `EnableBankTransfer` / `EnableCod` | ไม่มี | 0 | true / true / false | ❌ — ช่องจ่ายจริงตัดสินจาก `SitePaymentGateway` (`:2510-2532`) + `public-pay/methods` · **COD ไม่มีเลย** (มีแต่โหมด `pay_on_pickup` ของ MTO) |
| `AutoConfirmOrders` | ไม่มี | 0 | false | ❌ |
| `NotifyOnNewOrder` / `OrderNotificationEmails` | ไม่มี | 0 | true / null | ❌ ค่าเริ่ม "แจ้งเตือน = เปิด" แต่ไม่มีใครส่ง ⇒ หลอกตา |
| `OrderConfirmMessageTh/En` · `QuotationMessageTh/En` | ไม่มี | 0 | null | ❌ |
| `AutoSyncToErp` / `ErpDocumentType` | ไม่มี | 0 (ผู้อ่าน `ErpDocumentType` ที่เจอเป็นของ `SiteForm` `CmsCustomerService.cs:384`) | false / Invoice | ❌ — sync จริงเกิดตอนยืนยันเงินเสมอ (ปิดไม่ได้) |
| `EnableGuestCheckout` / `RequireCustomerAccount` | ไม่มี | 0 | true / false | ❌ |
| `EnableStockTracking` / `AutoDeductStock` / `ShowStockQuantity` | ไม่มี | 0 | true / true / false | ❌ — ตัดสต็อกตอนมีใบขายเสมอ (ปิดไม่ได้) |
| `EnableTaxInvoice` | ไม่มี | 0 | true | ❌ — ช่อง "ขอใบกำกับเต็มรูป" แสดงเสมอ `storefront.html:1222-1225` |
| `EnableCoupons` / `EnableTierPricing` / `EnableReviews` / `EnableWishlist` / `EnableAbandonedCartRecovery` / `CartExpiryDays` / `AbandonedCartHours` | ไม่มี | 0 | true… | ❌ (นอกขอบเขต MTO — ส่งให้ทีม D) |
| `OrderNumberPrefix` | ไม่มี | 0 นอก DbContext (`GenerateOrderNumber` :1989 ไม่อ่าน — ตรวจซ้ำในทีม D) | "WEB" | ❌ |

**สรุป §1**: MTO 11/11 ✅ · ค่าตั้งร้านค้าทั่วไป **0 ช่องบนจอ** และ ~20 ตัวไม่มีผู้อ่าน · ค่าที่ "เปิดโดยค่าเริ่มต้นแต่ไม่ทำงาน" (หลอกตา) = `NotifyOnNewOrder`, `EnableOnlinePayment`, `EnableBankTransfer`, `EnableTaxInvoice`, `AutoDeductStock`

---

## §2 ตารางขั้นตอน flow

### 2A. เจ้าของตั้งร้าน

| ขั้น | สถานะ | หลักฐาน | ช่องว่าง | ขนาด |
| --- | --- | --- | --- | --- |
| ตั้งสินค้าเป็น MTO (N วัน) | ✅ | `cms-edit.html:640` · `CmsCommerceService.cs:153` | — | — |
| ตัวเลือกสินค้า (ขนาด/รส/ข้อความบนเค้ก/รูปตัวอย่าง) | ✅ | `pos-modifiers.html` · `ModifierSelectionRules` · อัปโหลดรูป `CmsCommerceController.cs:184` | อยู่หน้า POS — ไม่มีทางลัดจากหน้าสินค้าเว็บ | S |
| ปฏิทินร้าน (ปิดประจำ · ตัดรอบ · วันหยุดบริษัท) | ✅ | `cms-orders.html:83-86` · `MtoLeadTime.EarliestReadyDate` `Helpers/MtoLeadTime.cs:29` | ไม่มีวันหยุดเฉพาะร้าน (ใช้ปฏิทินบริษัทของเงินเดือน) · ไม่มี "รับได้กี่ออเดอร์/วัน" (กำลังผลิต) | M |
| เงื่อนไขชำระ / มัดจำ / วิธีออกใบกำกับมัดจำ / ริบมัดจำ | ✅ | `cms-orders.html:68-79` | — | — |
| เปิด/ปิดวิธีรับ (มารับเอง ✅ · จัดส่ง ❌ ปิดไม่ได้) | ⚠️ | `ShippingFeeRules.cs:12` ค่าว่าง = จัดส่ง · ไม่มีด่านปิดจัดส่ง | ร้านมารับเองอย่างเดียวทำไม่ได้ | S |
| เปิด/ปิดวิธีจ่าย (โอน/ออนไลน์/COD) | ❌ | §1 | สวิตช์มีใน entity แต่ไม่มีผล | M |
| โหมดเช็กเอาต์ (ต้องจ่าย/ขอใบเสนอราคา/สอบถาม) | ❌ | `CheckoutMode` ไม่มีผู้อ่าน | ร้านเฟอร์นิเจอร์สั่งทำต้องตีราคาก่อน | M |

### 2B. ลูกค้า

| ขั้น | สถานะ | หลักฐาน | ช่องว่าง | ขนาด |
| --- | --- | --- | --- | --- |
| ดูสินค้า + ป้าย "ทำตามสั่ง N วัน รับได้เร็วสุด …" | ✅ | `storefront.html:1082-1085` · `CmsCommerceService.cs:218` | — | — |
| เลือกตัวเลือก (ราคาคิดฝั่งเซิร์ฟเวอร์) + แนบรูป | ✅ | `storefront.html:1064,1358` · `AddToCartAsync` :528 | — | — |
| ตะกร้า | ✅ | `storefront.html:1126` | ตะกร้าไม่แสดงวันพร้อม (เห็นครั้งแรกที่เช็กเอาต์) | S |
| ใบเสนอยอดก่อนยืนยัน (ค่าส่ง · วันพร้อม · ยอดจ่ายตอนนี้ · ส่วนที่เหลือ) | ✅ | `refreshQuote` `storefront.html:1240-1287` → `checkout-quote` → `ComputeCheckoutAsync` :2665 | — | — |
| เลือกวิธีรับ (ปุ่มแสดงเมื่อเปิดมารับเอง) | ✅ | `storefront.html:1260-1268` | — | — |
| **เลือกวันที่/ช่วงเวลารับเอง** | ❌ | ไม่มีช่อง · `SiteOrder` ไม่มี `RequestedReadyDate` | เค้กวันเกิดต้องระบุวัน — ตอนนี้ลูกค้าต้องพิมพ์ในหมายเหตุ ร้านจับคิวไม่ได้ | M |
| กรอกฟอร์ม | ⚠️ | `storefront.html:1206-1231` | ตายตัว + เซิร์ฟเวอร์ไม่ตรวจ (§3) · ใบกำกับ: ไม่ตรวจเลขผู้เสียภาษี 13 หลัก/checksum/สาขาฝั่งเซิร์ฟเวอร์ (`CreateOrderAsync` :619-620 รับตรง) | M |
| ยืนยันสั่งซื้อ → ออเดอร์ + ตรึงยอด/มัดจำ/วันพร้อม | ✅ | `CreateOrderAsync` :698-737 · ราคาจากตะกร้า (คิดฝั่งเซิร์ฟเวอร์ตอนเพิ่ม) | ไม่มี idempotency key — กดซ้ำเร็ว ๆ ได้ 2 ออเดอร์ (ปุ่ม disable ฝั่ง client เท่านั้น `storefront.html:1395`) · ตะกร้าว่างก็สร้างออเดอร์ 0 บาทได้ถ้าเรียก API ตรง | S |
| **หน้าสั่งซื้อสำเร็จ (ยอดที่ต้องจ่าย · QR · วันพร้อม)** | ❌ | `storefront.html:1422` เรียก endpoint ที่ต้องเป็นพนักงาน `CmsCommerceController.cs:14,227` | แขกเห็นเลขออเดอร์เป็น GUID · QR ฿0 · ไม่เห็นมัดจำ · ปุ่มจ่ายออนไลน์หาย | S |
| จ่าย: gateway (Omise) | ⚠️ | `storefront.html:1571-1600` → `public-pay/orders/{siteId}/{orderId}/intents` → `PublicPaymentResolver.ResolveOrderAsync` (`FromSnapshot`) | ถูกต้องที่เซิร์ฟเวอร์ แต่ถูกบังด้วยแถวบน | — |
| จ่าย: โอน + แนบสลิป | ✅ | `upload-slip` `CmsCommerceController.cs:290` (anonymous) | ยอดบน QR ผิดเพราะแถวบน | — |
| จ่าย: COD / ชำระเมื่อรับ | ⚠️ | `pay_on_pickup` ✅ (`MtoPaymentTerms.cs:9`) · COD ❌ | ใช้ได้เฉพาะออเดอร์ที่มีสินค้า MTO | S |
| ยืนยันทางอีเมล/LINE | ❌ | ไม่มี service แจ้งเตือนใน `CmsCommerceService` | — | M |
| ติดตามออเดอร์ / จ่ายส่วนที่เหลือทีหลัง | ❌ | ไม่มีหน้า "ออเดอร์ของฉัน" แบบแขก · ไม่มีลิงก์ส่งหาลูกค้า | — | M |

### 2C. หลังบ้าน

| ขั้น | สถานะ | หลักฐาน | ช่องว่าง | ขนาด |
| --- | --- | --- | --- | --- |
| สถานะ Pending→Confirmed→Processing→Shipped/Delivered | ✅ | ปุ่มตามสถานะ `cms-orders.html:215-220` · ด่าน `SiteOrderStatusTransition.Check` (`Helpers/SiteOrderStatusTransition.cs:15`) | ด่านกันแค่ "สถานะปิด" — ข้ามจาก Pending ไป Delivered ผ่าน API ได้ (ปุ่มไม่มีแต่ endpoint ไม่กัน) | S |
| สถานะ "กำลังผลิต" / "พร้อมรับ" | ⚠️ | enum `AllEnums.cs:1458-1468` ไม่มี `ReadyForPickup` · Processing = "กำลังเตรียม" | ร้านมารับเองไม่มีจุด "ของพร้อมแล้ว — แจ้งลูกค้า" | M |
| คิวผลิตตามวันพร้อม | ❌ | `GetOrdersAsync` :1086-1108 เรียงตาม `CreatedAt` · `OrderListResponse` ไม่มี `EarliestReadyDate/FulfillmentMethod` | ครัวต้องเปิดทีละออเดอร์ | M |
| รายละเอียดออเดอร์ (วันพร้อม · เงื่อนไขชำระ · ตัวเลือก · รูป) | ✅ | `cms-orders.html:490-500` · `OrderLineResponse.ModifierSummary` | — | — |
| ใบสั่งผลิต | ❌ | `Production/ProductionOrderService.cs` ไม่อ้าง `SiteOrder` | doc §4 ข้อ 9 | L |
| รับเงินหลายงวด / ใบส่วนที่เหลือ | ✅ | `ConfirmPaymentLockedAsync` :1406 · `SettleOnHandoverAsync` :1816 · ประทับเหตุล้มบนออเดอร์ | — | — |
| ตัดสต็อก | ✅/⚠️ | ตัดพร้อมใบขายเท่านั้น :1574-1581 · idempotent ต่อบรรทัด :2085 | MTO สินค้าไม่มีสต็อก + `InStockOnly` (ค่าเริ่ม ไม่มี UI) ⇒ เสี่ยงประทับ "ตัดสต๊อกไม่สำเร็จ" ทุกออเดอร์ · วัตถุดิบ (BOM) ไม่ถูกตัด | M |
| เอกสาร ERP + JE + e-Tax | ✅ | `SyncOrderToErpCoreAsync` :1233 · `ResolveErpContactForOrderAsync` :1911 · hooks e-Tax :39 | สวิตช์ `AutoSyncToErp/ErpDocumentType` ไม่มีผล (§1) | — |
| ยกเลิก / คืนเงิน / ริบมัดจำ | ✅ | :837-890 · `SiteOrderCancelPlan` · `SiteOrderDepositForfeit` · ใบลดหนี้ร่าง | ลูกค้ายกเลิกเองไม่ได้ (มีแต่แปลงเป็นใบเสนอราคาตอนยังไม่จ่าย :2304-2306) — เหมาะสม | — |
| ลิงก์ POS (ลูกค้ามารับแล้วจ่ายส่วนที่เหลือที่หน้าร้าน) | ❌ | POS ไม่มีวิธีชำระ "ตัดออเดอร์เว็บ" (doc §4 ข้อ 2 คล้ายกัน) · ต้องกด "ยืนยันรับเงิน" ที่หน้าออเดอร์แทน | M |

### 2D. การเชื่อมต่อ

| ด้าน | สถานะ | หลักฐาน / ช่องว่าง |
| --- | --- | --- |
| ผู้ติดต่อ ERP | ✅ | `ResolveErpContactForOrderAsync` :1911 · `ResolveOrCreateContactFromOrderAsync` :2434 |
| สินค้า/สต็อก | ⚠️ | ผ่าน `IProductService.AdjustStockAsync`/`IStockLedger` (:2104-2125) · variant `VariantOptionsJson` มีบนบรรทัดแต่ MTO ใช้ modifier |
| เอกสาร/ภาษี | ✅ | ใบขาย/ใบมัดจำ/ใบลดหนี้ · VAT ค่าส่ง · `RD-78(1)(b)` |
| Payment gateway | ⚠️ | เซิร์ฟเวอร์ถูก (`FromSnapshot`) · หน้าลูกค้าพัง (§2B) |
| แจ้งเตือน | ❌ | ไม่มีทั้งเจ้าของและลูกค้า |
| รายงาน | ⚠️ | การ์ดนับสถานะ `cms-orders.html:420-424` · ไม่มีรายงาน "ยอดค้างรับ/มัดจำคงค้างตามวันพร้อม" |
| PDPA | ⚠️ | ฟอร์มเช็กเอาต์ไม่มีข้อความแจ้งวัตถุประสงค์/ลิงก์นโยบาย (grep `consent|pdpa` ใน `storefront.html` = 0) · ข้อมูลผู้รับ (ชื่อ/เบอร์/ที่อยู่) เก็บเป็นข้อความธรรมดาบน `SiteOrder` |

---

## §3 ฟอร์มสั่งซื้อตั้งค่าได้ — ปัจจุบัน vs ที่ควรเป็น

### ปัจจุบัน

| ช่อง | หน้าเว็บ | เซิร์ฟเวอร์ |
| --- | --- | --- |
| ชื่อ · เบอร์ · อีเมล | บังคับ (`required` `storefront.html:1208-1210`) | ไม่ตรวจ (`CreateOrderAsync` :617-618) |
| ที่อยู่ | บังคับเมื่อจัดส่ง (:1268-1269) | ไม่ตรวจ (มารับเองเติม "มารับเองที่ร้าน" ฝั่ง client :1391) |
| จังหวัด/รหัสไปรษณีย์ | ไม่บังคับ (แต่โซนต้องใช้ ⇒ quote บอกเหตุ) | ใช้คิดค่าส่ง ✅ |
| หมายเหตุ | ไม่บังคับ | ไม่ตรวจ |
| ขอใบกำกับ + ผู้ซื้อ | ไม่บังคับ | ไม่ตรวจ 13 หลัก/checksum/สาขา |
| ช่องเพิ่มเอง (เช่น "ชื่อบนเค้ก", "วันจัดงาน", "ขนาดห้อง") | ❌ | ❌ (ใช้ modifier ชนิดข้อความแทนได้ — แต่ผูกต่อสินค้า ไม่ใช่ต่อออเดอร์) |
| วันที่/เวลาที่ต้องการรับ | ❌ | ❌ |

เทียบการจอง: `Helpers/BookingFormFields.cs` (`Phone/Email/Notes` = Required/Optional/Hidden · ชื่อบังคับเสมอ · ห้ามซ่อนทั้งเบอร์และอีเมล) → เก็บ `FormFieldsJson` (`CmsBookingResource.cs:41`) → ตรวจที่เซิร์ฟเวอร์ `CmsBookingService.cs:805` → ส่ง `FormFields` ให้หน้าเว็บ (`CmsBookingService.cs:304`) → storefront อ่านค่าที่เซิร์ฟเวอร์ส่ง (`storefront.html:1663-1664`) · UI ตั้งค่า `cms-edit.html:2987-3009` ⇒ **ร้านออนไลน์ไม่มีอะไรแบบนี้เลย**

### ที่ควรเป็น (ข้อเสนอ — ใช้แบบแผน BookingFormFields)

1. **ตัวตัดสินใหม่ `Helpers/OrderFormFields.cs`** (OWNER file เดียว)
   - `OrderFormFieldsConfig(Phone, Email, Notes, ShippingAddress, PostalCode, RequestedDate, CustomFields[])` ใช้ enum `BookingFieldRequirement` เดิม (Required/Optional/Hidden)
   - `CustomFields`: `{ Key, Label, LabelEn, Type: text|textarea|date|select|checkbox, Options[], Requirement, MaxLength, AppliesTo: all|mto_only|pickup_only|delivery_only }` · จำกัด ≤ 10 ช่อง · ข้อความ ≤ 500
   - `RequestedDate`: ตรวจ `>= EarliestReadyDate` และไม่ตรงวันปิดร้าน (`MtoLeadTime.ParseClosedWeekdays` · วันหยุด) · ช่วงเวลารับ (slot) เลือกได้จากรายการที่ร้านตั้ง
   - `Check(cfg, input, fulfilment, hasMto, readyDate)` → `(Errors, Cleaned)` · ค่าเดิม (JSON null) = ฟอร์มเดิม + **เซิร์ฟเวอร์บังคับชื่อ + (เบอร์ หรือ อีเมล)** เป็นขั้นต่ำ (ตอนนี้ไม่บังคับอะไรเลย)
   - ที่อยู่: จัดส่ง ⇒ ที่อยู่บังคับเสมอ (ตั้ง Hidden ไม่ได้) · ขอใบกำกับ ⇒ ชื่อ/ที่อยู่/เลขผู้เสียภาษี (checksum ตัวกลางที่มีอยู่) /สาขา 5 หลัก บังคับเสมอ (§86/4 · ตั้งปิดไม่ได้)
2. **Persist**: `SiteCommerceConfig.OrderFormFieldsJson` (ADD COLUMN IF NOT EXISTS) · `SiteOrder.RequestedReadyDate` (date) · `SiteOrder.RequestedTimeSlot` · `SiteOrder.CustomFieldsJson` (ตรึงป้าย+ค่า ณ ตอนสั่ง เพื่อไม่ให้ร้านแก้ป้ายแล้วความหมายเพี้ยนย้อนหลัง)
3. **ทางเข้า**: `CreateOrderAsync` เรียก `OrderFormFields.Check` ก่อนสร้างออเดอร์ (ล็อกจุดเรียกใน `tools/required_call_site_check.py`) · `checkout-quote` คืน `FormFields` + `AllowedDates` ให้หน้าเว็บวาดฟอร์ม (ห้ามสำเนากติกาใน JS · F2 ข้อ 5)
4. **Echo**: `OrderResponse` + รายละเอียดออเดอร์หลังบ้าน + `OrderListResponse` (วันรับที่ขอ) + บรรทัดหมายเหตุบนเอกสารขาย (ผ่าน summary ตัวเดียวแบบ `SummaryOf`) · HtmlEncode ทุกค่า (F4-C)
5. **UI ตั้งค่า**: ส่วน "ฟอร์มสั่งซื้อ" ใน `#mtoModal` หรือ modal ใหม่ "⚙️ ตั้งค่าร้านค้า" — ตัวเลือก Required/Optional/Hidden จาก `RequirementOptions()` ของเซิร์ฟเวอร์ · ตัวสร้างช่องเพิ่มเอง
6. **เทสต์**: `OrderFormFieldsTests` (ค่าเดิม = ขั้นต่ำ · ซ่อนช่องแล้วค่าที่ส่งมาถูกตัด · จัดส่งซ่อนที่อยู่ไม่ได้ · ขอใบกำกับไม่ครบ = ผิด · วันที่ก่อนวันพร้อม/วันปิด = ผิด · ช่องเพิ่มเอง select นอกตัวเลือก = ผิด · **ทิศตรงข้าม**: ฟอร์มเดิมที่กรอกครบยังผ่าน) + เทสต์จุดเรียกใน `CreateOrderAsync`

---

## §4 ข้อเสนอพัฒนาเรียงตามคุณค่า

| รหัส | เรื่อง | ไฟล์ที่แตะ | เทสต์ | ขนาด |
| --- | --- | --- | --- | --- |
| **R-209-A1** | หน้าสั่งซื้อสำเร็จของแขก: endpoint สาธารณะอ่านออเดอร์แบบจำกัดฟิลด์ (เลข · สถานะ · ยอด · `NextPayableAmount` · วันพร้อม · วิธีรับ · หมายเหตุมัดจำ · `OnlinePaymentBlockedNote` — **ไม่มี** ที่อยู่/เบอร์/อีเมล/InternalNotes) ด้วยโทเค็นสุ่มต่อออเดอร์ (ไม่ใช่ GUID ล้วน) · ตั้ง `SiteOrder.PublicToken` ตอนสร้าง · storefront เปลี่ยนไปเรียก endpoint นี้ · ไม่มีออเดอร์ = แสดงข้อความ ไม่ใช่ QR ฿0 | `CmsCommerceController.cs` (หรือ `CmsStorefrontController`) · `CmsCommerceService.GetPublicOrderAsync` · DTO `PublicOrderResponse` · `DatabaseMigrationHelper` · `storefront.html:1416-1460` | เทสต์ DTO ไม่มี PII · โทเค็นผิด = 404 · ทิศตรงข้าม: หน้าพนักงานยังเห็นครบ | S |
| **R-209-A2** | ฟอร์มสั่งซื้อตั้งค่าได้ + วันที่ต้องการรับ (§3) | `Helpers/OrderFormFields.cs` (ใหม่) · `CmsSite.cs` · `CmsCommerce.cs` · `CommerceDtos.cs` · `CreateOrderAsync` · `CheckoutQuoteAsync` · `cms-orders.html` · `storefront.html:1189-1231,1369-1392` · `tools/required_call_site_check.py` | `OrderFormFieldsTests` + จุดเรียก | M |
| **R-209-A3** | สวิตช์วิธีรับ/วิธีจ่ายที่มีผลจริง: `EnableDelivery` (ความหมายใหม่ของ `EnableShipping` — ปิด = จัดส่งเลือกไม่ได้ · ต้องเปิดอย่างน้อย 1 วิธี) · `EnableBankTransfer`/`EnableOnlinePayment` คุมกล่องจ่ายบนหน้าสำเร็จ + `public-pay` · COD = ตัดสินใจก่อน (§5) · ทั้งหมดอยู่ใน helper `StorefrontCheckoutOptions` ตัวเดียว + UI | `ShippingFeeRules.cs` · `ComputeCheckoutAsync` :2668-2684 · `GetStorefrontPaymentOptionsAsync` :2510 · `PublicPaymentResolver` · `cms-orders.html` · `storefront.html` | ปิดจัดส่ง + ส่ง delivery = 400 · ปิดทั้งสองวิธี = บันทึกไม่ได้ · ปิดโอน ⇒ ไม่มีกล่องสลิป | M |
| **R-209-A4** | แจ้งเตือนออเดอร์: เจ้าของ (ออเดอร์ใหม่ · สลิปเข้า) ผ่าน `NotifyOnNewOrder/OrderNotificationEmails` + LINE · ลูกค้า (ยืนยันสั่ง · รับเงินแล้ว · พร้อมรับ/จัดส่งแล้ว + เลขพัสดุ · ลิงก์ A1 สำหรับจ่ายส่วนที่เหลือ) ใช้ `OrderConfirmMessageTh/En` · ล้มไม่บล็อกออเดอร์แต่ประทับบนออเดอร์ (F2 ข้อ 7) | `CmsCommerceService` (inject notifier) · ใช้ pattern `CmsBookingService.Notify.cs` · `cms-orders.html` ช่องตั้งค่า | เทสต์ปิดสวิตช์ = ไม่ส่ง · ส่งล้ม = มีโน้ตบนออเดอร์ | M |
| **R-209-A5** | คิวผลิต: `OrderListResponse` + `EarliestReadyDate/RequestedReadyDate/FulfillmentMethod/BalanceAmount` · กรอง/เรียงตามวัน · มุมมอง "วันนี้/พรุ่งนี้ต้องทำ" + พิมพ์ใบงาน (ตัวเลือก/ข้อความบนเค้ก/รูป) · สถานะใหม่ `ReadyForPickup` (ต่อจาก Processing · ไม่ใช่จุดส่งมอบ §78 ⇒ ไม่ออกใบส่วนที่เหลือ) | `AllEnums.cs` · `SiteOrderStatusTransition` · `SiteOrderDepositInvoicing.IsHandoverStatus` (ยืนยันว่าไม่รวม) · `GetOrdersAsync` · `cms-orders.html` | เทสต์ Ready ไม่ออกใบ · Ready→Delivered ออกใบ (โหมดมัดจำ) | M |
| **R-209-A6** | ด่านลำดับสถานะจริง (Pending→Delivered ตรงไม่ได้ ถ้ายังไม่ชำระตามเงื่อนไข เว้น `pay_on_pickup`) | `Helpers/SiteOrderStatusTransition.cs` | เทสต์ทั้งสองทิศ | S |
| **R-209-A7** | ค่าตั้งที่ไม่มีผู้อ่าน: ต่อสาย (`CheckoutMode`, `AutoConfirmOrders`, `EnableTaxInvoice`, `OrderNumberPrefix`) **หรือลบ** (F2 ข้อ 2) · `AutoSyncToErp/AutoDeductStock/EnableStockTracking` ไม่ควรเปิดให้ปิด (เงิน/สต็อกต้องลง) ⇒ ลบออกจาก entity/DTO พร้อม migration ความหมาย | `CmsSite.cs` · `CmsCommerceConfigController.cs` · `CommerceConfigDtos.cs` · `tools/settings_reader_check.py` (ratchet) | settings_reader ratchet ลดลง | M |
| **R-209-A8** | สต็อกของ MTO: UI `StockBehavior` ในหน้าสินค้าเว็บ + ค่าเริ่ม "ไม่ตัดสต็อกสินค้าสำเร็จรูป" เมื่อ `LeadTimeDays != null` (หรือตัดวัตถุดิบตาม BOM ผ่าน `IStockLedger`) — ต้องคำตัดสินเจ้าของ | `cms-edit.html` · `DeductStockAsync` :2070 | ออเดอร์ MTO ยืนยันเงินแล้วไม่มีโน้ต "ตัดสต๊อกไม่สำเร็จ" | M |
| **R-209-A9** | กันสั่งซ้ำ/ออเดอร์ว่าง: idempotency key จาก client + ปฏิเสธตะกร้าว่าง (400) | `CreateOrderAsync` :631-668 · `storefront.html:1369` | เทสต์ตะกร้าว่าง = 400 · คีย์ซ้ำ = ออเดอร์เดิม | S |
| **R-209-A10** | ตรวจผู้ซื้อใบกำกับฝั่งเซิร์ฟเวอร์ (13 หลัก+checksum · สาขา 5 หลัก · ชื่อ/ที่อยู่) เมื่อ `RequestTaxInvoice` | `CreateOrderAsync` · ตัว checksum กลางที่มีอยู่ | ทั้งสองทิศ | S |
| R-209-A11 | ข้อความ PDPA + ลิงก์นโยบายบนฟอร์ม (ข้อความจากค่าตั้งไซต์) | `storefront.html` · `CmsSite` | — | S |
| R-209-A12 | ลิงก์ POS: เปิดบิล POS จากออเดอร์เว็บที่ลูกค้ามารับ (จ่ายส่วนที่เหลือที่หน้าร้าน → `ConfirmPaymentAsync` เส้นเดียว) | `PosService` · `pos.html` | — | L |

---

## §5 คำตัดสินที่ต้องถามเจ้าของ

1. **อ่านออเดอร์สาธารณะ (A1)**: ใช้โทเค็นสุ่มใหม่ต่อออเดอร์ (แนะนำ) หรือยอมให้ GUID ออเดอร์เป็นกุญแจ (แบบเดียวกับ `upload-slip`/`public-pay` ตอนนี้)? และหน้าแขกแสดงข้อมูลใดได้บ้าง
2. **COD**: เปิดเป็นวิธีจ่ายของร้านค้าทั่วไป (ไม่ใช่เฉพาะ MTO `pay_on_pickup`) ไหม — ถ้าเปิด ใบกำกับ/ตัดสต็อกเกิดตอนจัดส่ง (จุดส่งมอบ §78) และเงินเข้าจากผู้ขนส่งตามรอบโอน (ประสานทีม B)
3. **ฟอร์มสั่งซื้อค่าเริ่ม**: เซิร์ฟเวอร์ต้องบังคับขั้นต่ำ "ชื่อ + (เบอร์หรืออีเมล)" ทันที (ผู้เรียก API ที่ส่งว่างจะเริ่มได้ 400) หรือคงไม่บังคับจนกว่าร้านจะตั้งค่า (แบบ BookingFormFields รอบ 207)?
4. **วันที่ต้องการรับ**: บังคับเฉพาะออเดอร์ที่มีสินค้า MTO หรือให้ร้านเลือก · ต้องมี "จำนวนออเดอร์สูงสุดต่อวัน" (กำลังผลิต) ไหม
5. **สถานะ "พร้อมรับ"**: เพิ่ม enum ใหม่ (กระทบรายงาน/การ์ดนับ) หรือใช้ Processing + ธง `ReadyAt`?
6. **สต็อกของสินค้าทำตามสั่ง (A8)**: ไม่ตัดเลย · ตัดสินค้าสำเร็จรูปแบบติดลบได้ · หรือตัดวัตถุดิบตาม BOM?
7. **ค่าตั้งที่ไม่มีผู้อ่าน (A7)**: ตัวไหนต่อสาย ตัวไหนลบ — โดยเฉพาะ `CheckoutMode` (ร้านสั่งทำที่ต้องตีราคาก่อนจ่าย) และ `AutoSyncToErp`/`AutoDeductStock` (แนะนำลบ: เงิน/สต็อกต้องลงเสมอ)
8. **แจ้งเตือนลูกค้า (A4)**: ช่องทางที่ต้องมีขั้นแรก — อีเมลอย่างเดียว หรืออีเมล + LINE OA ของร้าน
