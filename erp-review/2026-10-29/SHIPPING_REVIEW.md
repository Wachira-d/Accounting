# รอบ 209 ทีม B — จัดส่ง / รับสินค้า ของร้านออนไลน์ CMS (2026-10-29)

อ่านอย่างเดียว · หลักฐาน `file:line` (path ย่อจาก `Accounting/`) · ✅ ทำได้ / ⚠️ ทำได้แต่ขาด / ❌ ไม่มี (รวม "มีแต่ไม่ถูกเรียก" · CLAUDE.md F2 #2) · ขนาด S/M/L ·
มุมมอง: เจ้าของร้านเบเกอรี่/ค้าปลีก ตั้งค่าจัดส่งเองโดยไม่มีโปรแกรมเมอร์

## §0 สรุป

| หัวข้อ | สถานะ | สรุปสั้น |
| --- | --- | --- |
| แกนคิดค่าส่ง (เหมาจ่าย · ตามน้ำหนัก · ฟรี · ส่งฟรีเมื่อครบ · โซนจังหวัด/รหัสไปรษณีย์/ประเทศ) | ✅ | ตัวคิดตัวเดียว `Helpers/ShippingFeeRules.cs:52-77` + ตัวจับโซน `CmsShippingService.cs:196-237` · หน้าเช็กเอาต์กับการสร้างออเดอร์ใช้ `ComputeCheckoutAsync` ตัวเดียว (`CmsCommerceService.cs:2666-2766`) · เทสต์ครบ (`MtoLeadTimeShippingPaymentTests` · `ShippingZoneMatchTests` · `ShippingVatAllocationTests`) |
| VAT ค่าส่ง + บรรทัดในเอกสาร ERP | ✅ | §79 อัตราตามสินค้าในบิล · ค่าตั้ง `ShippingVatAllocation` (max_rate/proportional) ตรึงบนออเดอร์ · `BuildOrderErpLines` :1358-1372 |
| **เจ้าของร้านตั้งโซน/อัตราเองได้ไหม** | ❌ | มีแต่ API (`CmsCommerceExtController.cs:112-185`) — **ไม่มีหน้า UI ใด ๆ** (`grep -ri "shipping/zones\|ShippingZone" wwwroot` = 0 · `js/api.js` ไม่มีฟังก์ชัน shipping) ทั้งที่หน้าตั้งค่าบอกว่า "ตั้งที่อัตราค่าส่งของโซน" (`cms-orders.html:66`) |
| ปิดจัดส่ง (ร้านรับเองอย่างเดียว) | ⚠️ | `EnableShipping=false` ⇒ ค่าส่ง 0 แต่ลูกค้ายังเลือก "จัดส่ง" ได้และต้องกรอกที่อยู่ (`CmsCommerceService.cs:2684` · หน้าร้านไม่รู้ธงนี้ — `CheckoutQuoteResponse` ไม่มี `ShippingEnabled` `CommerceDtos.cs:455-470`) · ไม่มีช่องตั้งค่าใน UI |
| ปิดมารับเอง (ส่งอย่างเดียว) | ✅ | `EnablePickup` ค่าเริ่มต้น false · หน้าร้านซ่อน radio (`storefront.html:1260`) · เซิร์ฟเวอร์กัน (`:2671-2672`) |
| หลายสาขารับของ · ช่วงเวลารับ/ส่ง | ❌ | ไม่มี entity/ค่าตั้ง (grep `PickupLocation|PickupSlot|DeliverySlot|TimeSlot` นอกโมดูลจอง = 0) · `Site.BranchId` สาขาเดียว (`CmsSite.cs:30`) |
| ผู้ขนส่ง · เลขพัสดุ · ลิงก์ติดตาม · ใบปะหน้า | ⚠️/❌ | พนักงานกรอกเลขพัสดุได้ (`cms-orders.html:549,593-599` → `:781,792`) · **ลูกค้าไม่เห็น** (storefront ไม่มีคำว่า trackingNumber เลย · หน้า order-success เรียก endpoint ที่ต้องเป็น Admin ⇒ 401 `CmsCommerceController.cs:226-227` vs `storefront.html:1422`) · ชื่อผู้ขนส่งไม่ถูกเก็บบนออเดอร์ · `ShippingTrackingService` มีแต่ไม่ต่อกับออเดอร์ (เรียกได้แค่ `GovernmentServiceController.cs:171-179`) · ใบปะหน้า/manifest = 0 |
| แจ้งลูกค้าเมื่อ Shipped/Delivered | ❌ | `CmsCommerceService.cs` ไม่มีการเรียก notification ใด (grep notif/line/email = 0) · `NotifyOnNewOrder`/`OrderNotificationEmails` ไม่มีผู้อ่าน |
| COD | ❌ | `SiteCommerceConfig.EnableCod` (`CmsSite.cs:171`) — ไม่มี UI · ไม่มีผู้อ่าน (อยู่ใน `tools/settings_reader_baseline.txt:37`) · payment-options ไม่มี COD |
| ค่าส่งตามระยะทาง / รถส่งเอง | ❌ | backlog R-30 "ไม่ทำรอบนี้" (`docs/BACKLOG_REGISTER_2026-10-26.md:73,252`) · `ShippingFeeRules.cs:42` บอกเหตุ |
| คืนค่าส่งเมื่อยกเลิก | ⚠️ | ใบลดหนี้เสนอจาก**ทุกบรรทัด**ของใบขาย (รวมค่าจัดส่ง) `:930-985` ⇒ คืนค่าส่งเสมอ แม้ของออกไปแล้ว — ไม่มีตัวเลือก "ไม่คืนค่าส่ง" |
| ฝั่งลูกค้า: สมุดที่อยู่ · จังหวัด dropdown · วันส่งประมาณ | ⚠️ | `SiteCustomerAddress` มี (`CmsCustomer.cs:68-91`) แต่ใช้ได้แค่ API หลังบ้าน (`CmsCustomerController.cs:90-103`) — หน้าร้านไม่ใช้ · จังหวัด = ช่องพิมพ์อิสระ (`storefront.html:1215`) เซิร์ฟเวอร์ normalize ให้ (`ThaiAddressParser.cs:25`) · วันส่งแสดงเป็น "(1–3 วัน)" ที่ตัวเลือก (`:1273`) ไม่คิดเป็นวันที่ ไม่อยู่บนออเดอร์ |

**ภาพรวม:** เครื่องยนต์คิดเงิน/ภาษีแข็ง (ตัวตัดสินเดียว + เทสต์) แต่ "ประสบการณ์เจ้าของร้าน + ลูกค้า" ยังขาดครึ่งหลัง: ตั้งโซนไม่ได้จากหน้าจอ · ลูกค้าไม่เห็นสถานะ/เลขพัสดุ · ไม่มีการแจ้งเตือน · สวิตช์ 2 ตัวตาย (EnableCod · EnableShippingCalculation) · enum `Calculated` ตาย · คูปองส่งฟรีไม่มีผล

## §1 สวิตช์ / ค่าตั้ง — field · UI · ผู้อ่าน · ค่าเริ่มต้น · verdict

### 1.1 `SiteCommerceConfig` (`Models/Entities/CmsSite.cs`)

| field | UI | ผู้อ่านเชิงธุรกิจ | default | verdict |
| --- | --- | --- | --- | --- |
| `EnableShipping` :132 | ❌ ไม่มีช่อง (grep `enableShipping` ใน wwwroot = 0) | `CmsCommerceService.cs:2684` — false ⇒ fee 0 เท่านั้น · หน้าร้านไม่รู้ | true | ⚠️ ปิดแล้ว = "ส่งฟรี" ไม่ใช่ "ไม่ส่ง" — ร้านรับเองอย่างเดียวทำไม่ได้จริง · `settings_reader_baseline.txt:42` ล้าสมัย (checker รายงาน "กลับมามีผู้อ่านแล้ว") |
| `EnableShippingCalculation` :133 | ❌ | ❌ (เฉพาะ `CmsCommerceConfigController.cs:69,153` เก็บ/echo) | false | ❌ ตาย (baseline :43) |
| `FlatShippingRate` :134 | `cms-orders.html:63,296,337` | `:2733` `ShippingFeeRules.SiteFlatFee` (เฉพาะร้านไม่มีโซน) | null | ✅ · baseline :48 ล้าสมัย |
| `FreeShippingThreshold` :135 | `cms-orders.html:64,297,338` | `:2733` เฉพาะทางเหมาจ่าย · ทางโซนใช้ `rate.FreeAboveAmount` ต่ออัตราแทน (`ShippingFeeRules.cs:59`) | null | ⚠️ ร้านที่มีโซนตั้ง "ส่งฟรีเมื่อครบ" ระดับร้านแล้ว**ไม่มีผล** — UI ไม่เตือน |
| `EnablePickup` :192 | `cms-orders.html:61,295,336` | `:2670-2680` + หน้าร้าน `storefront.html:1260-1268` | false | ✅ (สาขาเดียว · ไม่แสดงที่อยู่ร้าน/เวลารับให้ลูกค้า) |
| `EnableCod` :171 | ❌ | ❌ | false | ❌ ตาย (baseline :37) — ร้านที่อยากเก็บเงินปลายทางไม่มีทาง |
| `ShippingVatAllocation` :174 | `cms-orders.html:77,304-306,343` (ตัวเลือก+คำอธิบายจากเซิร์ฟเวอร์) | `:2765` → ตรึง `:727` → VAT ออเดอร์ `:737-742` → บรรทัด ERP `:1363` | max_rate | ✅ (ข้อ 165 · เทสต์ `ShippingVatAllocationTests`) |
| `NotifyOnNewOrder` / `OrderNotificationEmails` :162-163 | — | ❌ ไม่มีผู้อ่าน | true / null | ❌ (เกี่ยวโดยตรงกับ "แจ้ง Shipped") |

### 1.2 `SiteShippingZone` / `SiteShippingRate` (`Models/Entities/CmsCommerceExtensions.cs:56-94`)

| field | UI | ผู้อ่าน | verdict |
| --- | --- | --- | --- |
| Zone `CountryCodes` / `Provinces` / `PostalCodePatterns` / `SortOrder` / `IsActive` | ❌ ไม่มีหน้าตั้งค่า (API เท่านั้น) | `CmsShippingService.ZoneMatchesAddress` :196-237 · `:2687-2697` | ⚠️ ตรรกะดี (ว่าง=TH · โซนจำกัดพื้นที่+ที่อยู่ว่าง=ไม่ตรง · `10*` prefix) แต่เจ้าของร้านแตะไม่ได้ |
| Rate `Name` | — | หน้าร้าน `storefront.html:1273` | ✅ |
| Rate `CarrierName` | — | `ShippingOptionResponse` (`CmsShippingService.cs:184`) เท่านั้น — **ไม่อยู่ใน `CheckoutShippingOption`** (`CommerceDtos.cs:444-452`) · ไม่ถูกเก็บบนออเดอร์ | ❌ ลูกค้า/พนักงานไม่รู้ว่าส่งด้วยเจ้าไหน |
| Rate `RateType` Flat / ByWeight / Free | — | `ShippingFeeRules.Quote` :58-66 | ✅ |
| Rate `RateType.Calculated` (`AllEnums.cs:1617`) | — | ไม่มีสาขาเฉพาะ — ตก `_ => BaseRate` (:65) = เหมือน Flat · grep `ShippingRateType.Calculated` ทั้งเรพ = 0 | ❌ enum ตาย (เลือกแล้วเงียบ ๆ เป็นเหมาจ่าย) |
| Rate `BaseRate` / `PerKgRate` / `FreeAboveAmount` / `MinWeightKg` / `MaxWeightKg` | — | :55-66 | ✅ เทสต์ `MtoLeadTimeShippingPaymentTests.cs:60-79` |
| Rate `EstimatedDeliveryDaysMin/Max` | — | `storefront.html:1273` "(1–3 วัน)" | ⚠️ ไม่คิดเป็นวันที่ · ไม่สืบทอดลงออเดอร์/ไม่แสดงหลังสั่ง |
| Rate `SortOrder` / `IsActive` | — | `:2688,2698` | ✅ |

### 1.3 field อื่นที่เกี่ยว

| field | UI | ผู้อ่าน | verdict |
| --- | --- | --- | --- |
| `SiteProduct.WeightKg` (`CmsCommerce.cs:53`) | `cms-edit.html:2438,2470` | `:2686` รวมน้ำหนักฝั่งเซิร์ฟเวอร์ | ✅ |
| `SiteProductVariant.WeightKg` (`CmsCommerceExtensions.cs:110`) | — | ❌ `:2686` ใช้แค่ `Sp.WeightKg` | ❌ ไซส์ใหญ่หนักกว่าไม่ถูกคิด |
| `SiteCoupon.FreeShipping` (`CmsCommerceExtensions.cs:34`) + `CouponDiscountType.FreeShipping` | หน้าคูปอง | `CmsCouponService.cs:157,172` คืนธงให้ตะกร้า — **`ComputeCheckoutAsync` ไม่อ่าน** (grep FreeShipping ใน CmsCommerceService = 0) | ❌ คูปอง "ส่งฟรี" ไม่ลดค่าส่งจริง |
| `SiteOrder.ShippingMethod` (`CmsCommerce.cs:198`) | `cms-orders.html:487` | รับจาก client (`CommerceDtos.cs:269`) — หน้าร้านไม่ส่ง (`storefront.html:1374-1391`) · pickup เซ็ตป้าย `:719-720` | ⚠️ ออเดอร์จัดส่งมี `ShippingRateId` แต่ไม่มีชื่ออัตรา/ผู้ขนส่งอ่านได้ |
| `SiteOrder.TrackingNumber` :199 | หลังบ้าน `cms-orders.html:549,593-599` | `:781` (NoOp) · `:792` (เปลี่ยนสถานะ) | ⚠️ หลังบ้านครบ · ลูกค้า ❌ |
| `SiteOrder.ShippingProvince/PostalCode/ShippingRateId` :238-241 | `cms-orders.html:482-488` | เก็บตอนสั่ง `:715-718` · echo `:1032-1033` | ✅ ตรวจย้อนโซนได้ |
| `SiteCustomerAddress` (`CmsCustomer.cs:68-91` · `SiteAddressType`) | หลังบ้าน API `CmsCustomerController.cs:90-103` | หน้าร้าน ❌ | ❌ สมุดที่อยู่ไม่ถึงลูกค้า |

## §2 Flow — step · status · evidence · gap · size

| # | ขั้น | สถานะ | หลักฐาน | ช่องว่าง | ขนาด |
| --- | --- | --- | --- | --- | --- |
| 1 | เจ้าของตั้งโซน/อัตรา | ❌ | API ครบ CRUD + calculate (`CmsCommerceExtController.cs:112-185`) · UI = 0 | ต้องยิง API เอง — ร้านจริงทำไม่ได้ | M |
| 2 | ลูกค้าเลือก จัดส่ง/รับเอง | ✅ | `storefront.html:1259-1268` radio เฉพาะเมื่อ `pickupEnabled` · pickup ซ่อนที่อยู่+ปลด required | ไม่แสดงที่อยู่ร้าน/เวลาเปิดให้คนมารับ | S |
| 3 | กรอกที่อยู่ | ⚠️ | ที่อยู่ textarea + จังหวัด input + รหัสไปรษณีย์ `maxlength=5` (`:1213-1216`) · เซิร์ฟเวอร์ `ThaiAddressParser.NormalizeProvince` (77 จังหวัด · กทม. หลายแบบ — เทสต์ `ShippingZoneMatchTests.cs:32-36`) | ไม่มี dropdown · รหัสไปรษณีย์ไม่ตรวจ `^[0-9]{5}$` ฝั่งเซิร์ฟเวอร์ · ไม่มีสมุดที่อยู่ | S–M |
| 4 | คิดค่าส่ง (quote) | ✅ | `ComputeCheckoutAsync` `:2684-2736` · น้ำหนักรวมฝั่งเซิร์ฟเวอร์ `:2686` · ไม่ตรงโซน ⇒ `Problem` พร้อมทางไปต่อ `:2725-2728` · ไม่มีโซน ⇒ เหมาจ่ายร้าน `:2733` | `EnableShipping=false` ⇒ ส่งฟรี (ไม่ใช่ปิดส่ง) · ส่งฟรีระดับร้านไม่ใช้กับโซน · คูปองส่งฟรีไม่มีผล · น้ำหนัก variant ไม่คิด | S |
| 5 | เลือกอัตรา | ✅ | 1 ตัวเลือกเลือกให้ · หลายตัวบังคับเลือก `:2710-2719` · เลือกอัตราที่ใช้ไม่ได้ ⇒ `SHIP-RATE` | — | — |
| 6 | สร้างออเดอร์ | ✅ | ตัวคิดเดียวกับ quote · `Problem` ⇒ โยน `CHECKOUT` `:711-712` · เก็บ fee/จังหวัด/รหัส/rateId `:713-718` | `ShippingMethod` ว่างสำหรับจัดส่ง · ไม่เก็บชื่อผู้ขนส่ง/วันส่งประมาณ | S |
| 7 | VAT ค่าส่ง + ยอดออเดอร์ | ✅ | `:737-743` (สูตรเดียวกับใบกำกับ) · เทสต์ `MtoLeadTimeShippingPaymentTests.cs:88-115` | — | — |
| 8 | บรรทัดค่าส่งในเอกสาร ERP | ✅ | `BuildOrderErpLines` `:1358-1372` ("ค่าจัดส่ง" · หลายอัตราแยกบรรทัด) · เทสต์ `CmsCommerceOrderLinesTests.cs:136-158` | `AccountId: null` ⇒ ผังรายได้ขายมาตรฐาน — **ไม่มีค่าตั้ง "ผังรายได้ค่าจัดส่ง"** (SHIPPING_INCOME ใน `IntegrationService.cs:5173,5180` ใช้กับ import แพลตฟอร์มเท่านั้น) | S |
| 9 | เตรียมของ → จัดส่ง (Shipped) | ⚠️ | ปุ่ม `cms-orders.html:218` · `ShippedAt` `:797` · กรอกเลขพัสดุ `:549` | ไม่มีช่องผู้ขนส่ง · ไม่มีใบปะหน้า/manifest (grep = 0) · `IShippingTrackingService.DetectCarrier` มีแต่ไม่ถูกเรียกจากออเดอร์ | M |
| 10 | แจ้งลูกค้า Shipped/Delivered (email/LINE) | ❌ | `CmsCommerceService.cs` ไม่มีการเรียก notification (grep = 0) | ลูกค้าไม่รู้ว่าของออกแล้ว | M |
| 11 | ลูกค้าดูสถานะ/เลขพัสดุ | ❌ | `storefront.html:1422` เรียก `GET /commerce/orders/{id}` ซึ่ง `[RequireSiteRole(Admin, OrderManager)]` (`CmsCommerceController.cs:226-227`) ⇒ แขก 401 ⇒ `order=null` (catch เงียบ `:1425`) · ไม่มีหน้า "คำสั่งซื้อของฉัน" · ไม่มี `trackingNumber` ใน storefront | **หน้า order-success แสดงได้แค่ GUID** (ไม่มีเลขที่ออเดอร์/ยอด/วิธีรับ) — ล้มเงียบ (F2 #7) | M |
| 12 | ส่งถึง / ลูกค้ามารับ (Delivered) | ✅ | `DeliveredAt` `:798` · pickup ข้าม Shipped ได้ (`cms-orders.html:217-218`) · โหมดมัดจำ settle ตอนส่งมอบ `:808-835` | — | — |
| 13 | ยกเลิก → คืนค่าส่ง | ⚠️ | `ProposeCreditNoteOnCancelAsync` `:930-985` CN ร่างจาก**ทุก**บรรทัดใบขาย (รวมค่าจัดส่ง) | ของออกไปแล้วค่อยยกเลิก = คืนค่าส่งด้วยเสมอ · ไม่มีตัวเลือก | S |
| 14 | COD | ❌ | `EnableCod` ไม่มีผู้อ่าน · `GetPaymentOptions` (`:1189+`) ไม่มี COD · ไม่มี `IPaymentProvider` COD | ร้านส่งของในพื้นที่ที่เก็บเงินปลายทางใช้ไม่ได้ | M |
| 15 | ค่าส่งตามระยะทาง / รถร้าน | ❌ | `ShippingFeeRules.cs:42` + R-30 | backlog ตามคำขอ | L |
| 16 | ช่วงเวลารับ/ส่ง · หลายสาขารับของ | ❌ | grep = 0 · `Site.BranchId` เดี่ยว | เบเกอรี่ที่มี 2 หน้าร้านเลือกจุดรับไม่ได้ | L |

**ตรวจแล้วไม่ใช่บั๊ก:** (ก) โซนจำกัดพื้นที่ + ไม่กรอกจังหวัด ⇒ ไม่ตรง — ตั้งใจ (ฝ่ายค้านทีม K ข้อ 2 · เทสต์ `ShippingZoneMatchTests.cs:21`) · (ข) VAT ค่าส่งบิลปน 7% = ทิศปลอดภัยที่เจ้าของ/นักบัญชีเลือกได้ (ข้อ 165) · (ค) `CmsShippingService.CalculateShippingOptionsAsync` กับ `ComputeCheckoutAsync` คิดคนละที่แต่ใช้ `ShippingFeeRules.Quote` + `ZoneMatchesAddress` ตัวเดียวกัน — ไม่ drift

## §3 ข้อเสนอพัฒนา

| รหัส | เรื่อง | ไฟล์ | เทสต์ | "ทิศตรงข้าม" ที่ต้องคง | ขนาด |
| --- | --- | --- | --- | --- | --- |
| R-209-B1 | **หน้าตั้งค่าโซน/อัตรา** ใน `cms-orders.html` modal ⚙️ (ตารางโซน → อัตรา · ชนิด Flat/ByWeight/Free · ผู้ขนส่ง · วันส่ง · น้ำหนัก) ต่อ API เดิม + ฟังก์ชันใน `js/api.js` · ปุ่ม "ทดลองคิดค่าส่ง" เรียก `shipping/calculate` | `wwwroot/pages/cms-orders.html` · `wwwroot/js/api.js` | `node --check` · ฝั่งเซิร์ฟเวอร์ไม่แตะ | API/DTO เดิมไม่เปลี่ยน · ร้านที่ไม่มีโซนยังใช้เหมาจ่าย | M |
| R-209-B2 | **ปิดจัดส่งให้เป็น "ไม่ส่ง" จริง**: เพิ่ม `CheckoutQuoteResponse.ShippingEnabled` · `EnableShipping=false && EnablePickup=true` ⇒ default pickup + ซ่อนที่อยู่ · ทั้งคู่ false ⇒ `Problem` "ร้านยังไม่เปิดรับคำสั่งซื้อ" · เพิ่มช่อง "เปิดจัดส่ง" ใน modal เดียวกับ `mtoPickup` | `CmsCommerceService.cs:2666-2690` · `CommerceDtos.cs` · `cms-orders.html` · `storefront.html:1259-1270` | ใหม่: `ComputeCheckout_ปิดส่งเปิดรับ_defaultPickup` + ทิศตรงข้าม `เปิดทั้งคู่_พฤติกรรมเดิม` | ร้านเดิม (`EnableShipping=true`) ไม่เปลี่ยน | S |
| R-209-B3 | **ลูกค้าเห็นสถานะ/เลขพัสดุ**: endpoint `[AllowAnonymous] GET orders/{id}/public` คืน DTO จำกัด (เลขที่ · สถานะ · วิธีรับ · ค่าส่ง · ยอด · เลขพัสดุ · ผู้ขนส่ง · ลิงก์ติดตาม) · ใช้ GUID ที่เดาไม่ได้ + `ChatRateLimiter` ต่อ IP · หน้า order-success + ลิงก์ "ติดตามพัสดุ" (template URL ต่อผู้ขนส่ง ใน `Helpers/` ตัวเดียว) | `CmsCommerceController.cs` · `CmsCommerceService.cs` (DTO ใหม่ ไม่ reuse `OrderResponse` — มี `InternalNotes`/สลิป) · `storefront.html:1415-1460` | ใหม่: DTO สาธารณะไม่มี `InternalNotes` · 404 เมื่อ siteId ไม่ตรง | endpoint Admin เดิมคงสิทธิ์เดิม | M |
| R-209-B4 | **ผู้ขนส่งบนออเดอร์**: `SiteOrder.ShippingCarrier` (+ `ADD COLUMN IF NOT EXISTS`) · ตั้งจาก `rate.CarrierName` ตอนสั่ง · `ShippingMethod` = ชื่ออัตราเมื่อ client ไม่ส่ง · กรอก/แก้คู่กับเลขพัสดุใน `cms-orders.html:549` · echo ใน `OrderResponse` (กฎเหล็ก #4 B) | `CmsCommerce.cs` · `DatabaseMigrationHelper.cs` · `CmsCommerceService.cs:708-720,781,792,1004-1033` · `cms-orders.html` | ใหม่ใน `CmsCommerceOrderLinesTests` หรือไฟล์ใหม่: ออเดอร์ pickup ไม่มี carrier | ออเดอร์เก่า null = แสดง "-" | S |
| R-209-B5 | **แจ้งเตือน Shipped/Delivered** (อีเมล/LINE ผ่าน notification service ที่มีอยู่) + ต่อสาย `NotifyOnNewOrder`/`OrderNotificationEmails` ให้มีผู้อ่าน | `CmsCommerceService.UpdateOrderStatusLockedAsync` (หลัง `ApplyStatus` `:836`) | ใหม่: เปลี่ยน NoOp ไม่ส่งซ้ำ | ล้มของการแจ้งเตือนห้ามล้มการเปลี่ยนสถานะ (log + ประทับใน `InternalNotes`) | M |
| R-209-B6 | **สวิตช์ตาย**: `EnableCod` → (ก) ทำ COD เป็น payment option (`ManualSlipPaymentProvider` แบบ "เก็บปลายทาง" · บันทึกรับเงินตอน Delivered · ผัง COD_RECEIVED) หรือ (ข) ลบ field+ตัด baseline · `EnableShippingCalculation` → ลบ · `ShippingRateType.Calculated` → ลบ หรือ implement ผ่าน `IShippingTrackingService.GetShippingRatesAsync` (ตอนนี้เป็นตารางคงที่ `ShippingTrackingService.cs:90-110`) | `CmsSite.cs:133,171` · `AllEnums.cs:1617` · `ShippingFeeRules.cs:62-66` · `tools/settings_reader_baseline.txt:37,42,43,48` | `Quote` ปฏิเสธ/ไม่รับ `Calculated` (negative) | ค่าที่ persist เป็น Calculated ⇒ migration เป็น Flat (F2 #9) | S (ลบ) / M (COD) |
| R-209-B7 | **คูปองส่งฟรีมีผล**: `ComputeCheckoutAsync` รับธง `FreeShipping` ของคูปองในตะกร้า ⇒ fee 0 (ก่อน VAT) | `CmsCommerceService.cs:2666-2736` (+ ส่งธงจาก `CreateOrderAsync :625-650`) | ใหม่: คูปองส่งฟรี ⇒ fee 0 · ทิศตรงข้าม: คูปองลดราคาธรรมดา fee เดิม | ส่วนลดสินค้าไม่เปลี่ยน | S |
| R-209-B8 | **ส่งฟรีระดับร้านกับโซน + น้ำหนัก variant**: ให้ `FreeShippingThreshold` ใช้ได้แม้มีโซน (หรือ UI เตือนชัดว่าไม่มีผล — รอคำตัดสิน §4) · น้ำหนัก = `variant.WeightKg ?? product.WeightKg` | `ShippingFeeRules.Quote`/`:2686,2700` | เทสต์เดิม `ค่าส่งเหมาจ่าย_ตามน้ำหนัก_ส่งฟรีเมื่อครบยอด` + เคส variant | อัตราที่มี `FreeAboveAmount` เองคงเดิม | S |
| R-209-B9 | **ฟอร์มที่อยู่**: จังหวัด dropdown 77 จังหวัดจากเซิร์ฟเวอร์ (`ThaiAddressParser` เป็นแหล่งเดียว — F2 #5) · รหัสไปรษณีย์ `^[0-9]{5}$` ตรวจฝั่งเซิร์ฟเวอร์ใน `ComputeCheckoutAsync` · สมุดที่อยู่ให้ลูกค้าที่ล็อกอิน (ใช้ `SiteCustomerAddress` เดิม) | `storefront.html:1212-1218` · `CmsCommerceService.cs` · `CmsStorefrontController.cs` | ใหม่: รหัส 4 หลัก ⇒ `Problem` | พิมพ์ "กทม." ยังตรงโซนกรุงเทพ (เทสต์เดิม) | M |
| R-209-B10 | **ค่าส่งตอนยกเลิกหลังส่ง**: ค่าตั้ง `RefundShippingOnCancelAfterShipped` (default true = เดิม) ⇒ CN ไม่รวมบรรทัดค่าจัดส่งเมื่อ `ShippedAt != null` | `CmsCommerceService.cs:930-985` · `Helpers/SiteOrderCancelPlan` | ใหม่: ยกเลิกก่อนส่ง = คืนครบ (ทิศตรงข้าม) | default = พฤติกรรมเดิม | S |
| R-209-B11 | **วันส่งประมาณเป็นวันที่** ตรึงบนออเดอร์ (`EarliestReadyDate` + `EstimatedDeliveryDaysMax`, ข้ามวันปิดร้านด้วย `ShopCalendar` เดิม) แสดงหน้าร้าน/หลังบ้าน | `ComputeCheckoutAsync :2745-2752` · `CmsCommerce.cs` | เทสต์ pure ใน `MtoLeadTime` | pickup ไม่มีวันส่ง | S |
| R-209-B12 | backlog ตัดสินใหม่: ระยะทาง (กม.) · ช่วงเวลารับ/ส่ง · หลายสาขารับของ (`SitePickupLocation` ผูก `Branch`) · ใบปะหน้า/manifest · ติดตามพัสดุอัตโนมัติ (`ShippingTrackingService` มี Thailand Post API จริงเมื่อตั้ง `Shipping:ThailandPost:ApiKey` `:155-175` · ที่เหลือ pending-result) | — | — | — | L |

ลำดับที่แนะนำ (ไม่แตะเงินก่อน): B3 → B1 → B2 → B4 → B7 → B6(ลบ) → B9 → B5 → B8/B10/B11 → B12

## §4 คำตัดสินที่ต้องถามเจ้าของ

| # | คำถาม | ทางเลือก | ถ้าไม่ตอบ ระบบทำอะไรตอนนี้ |
| --- | --- | --- | --- |
| 1 | เก็บเงินปลายทาง (COD) ต้องมีไหม | (ก) ทำเป็นวิธีจ่าย + รับเงินตอน Delivered (ข) ลบ `EnableCod` | สวิตช์มีแต่ไม่ทำอะไร |
| 2 | ยกเลิกหลังของออกแล้ว คืนค่าส่งไหม | (ก) คืนเสมอ (เดิม) (ข) ค่าตั้งต่อร้าน default คืน | คืนเสมอ |
| 3 | ช่องทางแจ้งลูกค้าเมื่อจัดส่ง | อีเมล / LINE / ทั้งคู่ · ข้อความแก้ได้ไหม | ไม่แจ้ง |
| 4 | "ส่งฟรีเมื่อครบ" ระดับร้าน ควรทับอัตราในโซนไหม | (ก) ทับทุกอัตรา (ข) เฉพาะร้านไม่มีโซน (เดิม) + UI เตือน | เดิม เงียบ |
| 5 | `ShippingRateType.Calculated` | ลบ / คงไว้รอต่อ API ผู้ขนส่ง | เลือกแล้วเป็นเหมาจ่ายเงียบ ๆ |
| 6 | ร้านหลายหน้าร้าน ต้องเลือกจุดรับไหม · ต้องมีช่วงเวลารับ/ส่งไหม · ค่าส่งตามระยะทางยังอยู่ backlog ไหม | ทำ/ไม่ทำ (L) | ไม่มี |
| 7 | ผังรายได้ค่าจัดส่งแยกจากรายได้ขายไหม | ค่าตั้งผังต่อร้าน / คงรวมรายได้ขาย | รวมรายได้ขาย (AccountId null) |

_Last verified against codebase: 2026-10-29 (ทีม B รอบ 209 · อ่านอย่างเดียว · ยังไม่ได้คอมไพล์ในเครื่องนี้)_
