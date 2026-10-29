# ทีม D (รอบ 209) — แผนที่ค่าตั้ง CMS + จุดเชื่อม ERP (2026-10-29)

> อ่านอย่างเดียว · หลักฐาน `file:line` (path นับจาก `Accounting/`) · เกณฑ์ตาม CLAUDE.md §F2 ข้อ 2: **"เก็บ+echo กลับครบ ≠ มีผล"**
> — ค่าที่มีแค่ writer ใน `CmsCommerceConfigController.UpdateConfig` + echo ใน `MapConfig` = ❌ ·
> ผู้อ่านนับเฉพาะ "โค้ดธุรกิจที่เปลี่ยนพฤติกรรม" (ไม่นับ echo / hydrate ฟอร์ม / ชื่อซ้ำของ entity อื่น)
>
> วิธีตรวจ: (1) `python3 tools/settings_reader_check.py --all` (baseline ของ `SiteCommerceConfig` 38 แถว) ·
> (2) สคริปต์ค้นผู้อ่านรายฟิลด์ทุก entity CMS (C# นอก entity/DTO/DbContext/migration + JS ใน wwwroot) แล้ว**เปิดไฟล์ตรงจุด**ทุกแถวที่ตัดสิน ·
> (3) ไล่จุดเรียกข้ามโมดูลทีละเส้น (§3)
>
> สถานะ: ✅ มีช่อง + มีผู้อ่านเชิงธุรกิจ · ⚠️ มีผลแต่ขาด (ไม่มีช่องบนจอ/ผลอ่อน/ทำเพียงบางทาง) · ❌ ไม่มีผู้อ่าน (หรือมีแต่ echo) · ขนาดแก้ S/M/L

---

## §0 สรุป

### ตัวเลข (ฟิลด์ค่าตั้งที่ผู้ใช้ตั้งได้ — ไม่นับ id/audit/สถานะที่ระบบเขียนเอง)

| entity | ฟิลด์ | ✅ | ⚠️ | ❌ |
| --- | ---: | ---: | ---: | ---: |
| `SiteCommerceConfig` | 52 | 13 | 1 | **38** |
| `Site` | 36 | 18 | 9 | 9 |
| `SiteBookingSettings` | 16 | 16 | 0 | 0 |
| `SiteBookingService` (ค่าตั้งรายบริการ) | 27 | 25 | 2 | 0 |
| `SiteForm` | 10 | 1 | 3 | 6 |
| `SiteCoupon` | 13 | 0 | 12 | 1 |
| `SiteShippingZone` / `SiteShippingRate` | 15 | 0 | 15 | 0 |
| `SitePaymentGateway` | 12 | 5 | 2 | 5 |
| `SiteCustomer` (ค่าตั้งลูกค้า/ยินยอม) | 5 | 0 | 2 | 3 |
| `SiteProductVariant` (ฟิลด์เชิงตั้งค่า) | 3 | 0 | 0 | 3 |
| **รวม** | **189** | **78** | **46** | **65** |

ข้อสังเกตสำคัญ:
1. **`SiteCommerceConfig` = กล่องสวิตช์ที่ไม่ต่อสาย 73%** — มี API GET/PUT ครบ แต่**ไม่มีหน้าตั้งค่าร้านค้าเลย** (`cmsUpdateCommerceConfig` ถูกเรียกแค่ `pages/cms-orders.html:352` (โมดัลสินค้าตามสั่ง 15 ช่อง) และ `:400` (ท้ายใบเสนอราคา)) · ฟิลด์ที่เหลือ 37 ตัวไม่มีทั้งช่องและผู้อ่าน
2. **checker นับขาด 3 ตัว** — `DefaultVatRate` · `PricesIncludeVat` · `ErpDocumentType` ไม่อยู่ใน baseline เพราะชื่อซ้ำกับ `CompanySettings`/`Document`/`SiteForm` (over-approximate ตามที่ออกแบบ) แต่ของ `SiteCommerceConfig` ไม่มีผู้อ่านจริง (sync ฮาร์ดโค้ด `PricesIncludeVat: true` ที่ `CmsCommerceService.cs:1280` และเลือกชนิดเอกสารเองที่ `:1254`) ⇒ ของจริงคือ 38 ไม่ใช่ 35 · และ checker แจ้ง ℹ️ ให้ตัด `EnableShipping`/`FlatShippingRate`/`FreeShippingThreshold` ออกจาก baseline ได้แล้ว (ยังไม่ตัด)
3. ฝั่งจอง (`SiteBookingSettings`/`SiteBookingService`) **ต่อสายดีที่สุดในโมดูล** — ทุกค่ามีช่องใน `cms-edit.html` และผู้อ่านใน `BookingSlotEngine`/`CmsBookingService`
4. คูปอง · โซน/อัตราค่าส่ง · ตัวเลือกสินค้า (variant) **มี service + API แต่ไม่มีหน้าแอดมิน** (ไม่มีใน `api.js` ด้วย) ⇒ ร้านตั้งค่าเองไม่ได้

### Top-10 สวิตช์ที่ขาด (ร้านอยากปิด/เปลี่ยน แต่ทำไม่ได้)

| # | ฟังก์ชัน | ปัจจุบัน | หลักฐาน |
| --- | --- | --- | --- |
| 1 | รีวิวสินค้า | เปิดเสมอ (`POST reviews` `[AllowAnonymous]`) · `EnableReviews`/`ReviewAutoApprove`/`ReviewRequirePurchase` ไม่มีผู้อ่าน | `CmsCommerceExtController.cs:262` · `CmsReviewService.cs:54` (`IsApproved = false` ตายตัว) |
| 2 | อีเมลกู้ตะกร้าค้าง | ส่งทุกเว็บทุกบริษัท หลัง 3 ชม. ตายตัว — ไม่ดู `EnableAbandonedCartRecovery`/`AbandonedCartHours` **และไม่ดู `AcceptMarketing`** (PDPA) | `AbandonedCartService.cs:66,83` |
| 3 | วิธีชำระ (ออนไลน์/โอน/COD) | `EnableOnlinePayment`/`EnableBankTransfer`/`EnableCod` ไม่มีผู้อ่าน — หน้าร้านแสดงตามแถว `SitePaymentGateway` ตัวแรก · COD ไม่มีเลย | `CmsCommerceService.cs:2510-2533` |
| 4 | โหมดชำระ/ขอใบเสนอราคา (`CheckoutMode`, `AutoGenerateQuotation`) | ไม่มีผู้อ่าน — ทุกเว็บ "สั่ง → จ่าย" และลูกค้ากดแปลงเป็นใบเสนอราคาเองได้เสมอ | `CmsCommerceController.cs:312` |
| 5 | สมัครสมาชิก/บังคับล็อกอิน (`RequireCustomerAccount`, `EnableGuestCheckout`) | ไม่มีผู้อ่าน · หน้าร้านไม่มี UI ล็อกอินเลย ⇒ guest checkout อย่างเดียว | `storefront.html` (ไม่มีเรียก `customers/` auth) |
| 6 | ขอใบกำกับภาษีที่หน้าร้าน (`EnableTaxInvoice`) | ช่อง "ขอใบกำกับ" แสดงเสมอ | `storefront.html:1223` |
| 7 | ตัดสต็อกอัตโนมัติ/ติดตามสต็อก (`AutoDeductStock`, `EnableStockTracking`, `DefaultStockBehavior`) | ตัดเสมอตอนยืนยันชำระ/ส่งมอบ | `CmsCommerceService.cs:1576,1834` |
| 8 | แจ้งร้านเมื่อมีออเดอร์ใหม่ (`NotifyOnNewOrder`, `OrderNotificationEmails`) | ไม่มีการแจ้งร้านเลย (ไม่มีจุดเรียก email/LINE ใน `CreateOrderAsync`) | `CmsCommerceService.cs:606-766` |
| 9 | ซิงก์ ERP อัตโนมัติ (`AutoSyncToErp`, `ErpDocumentType`) | ซิงก์เสมอตอนยืนยันชำระ · ชนิดเอกสารตัดสินจากธง VAT + `RequestTaxInvoice` | `CmsCommerceService.cs:1509,1254` |
| 10 | คำนำหน้าเลขออเดอร์ (`OrderNumberPrefix`) | ฮาร์ดโค้ด `"WEB-"` | `CmsCommerceService.cs:1993` |

(รอง: คุกกี้แบนเนอร์ `CookieConsentEnabled` echo ไปหน้าร้านแต่ `storefront.html` ไม่อ่าน · wishlist/คูปอง API เปิดเสมอแต่หน้าร้านไม่มี UI · ฟอร์มติดต่อไม่ส่งอีเมลแจ้ง/ตอบกลับอัตโนมัติ)

### Top-10 จุดเชื่อมที่ขาด

| # | จาก → ถึง | ปัญหา | หลักฐาน |
| --- | --- | --- | --- |
| 1 | ออเดอร์/จอง → สาขา | `Site.BranchId` ถูกใช้แค่เขียนข้อความ `"Branch: {id}"` ลง `InternalNotes` — **ไม่ส่ง `BranchId` เข้า `CreateDocumentRequest`** ⇒ ใบขายเว็บไม่ขึ้นสาขา (เลขรันต่อสาขา §86/4 · รายงานสาขาเพี้ยน) | `CmsCommerceService.cs:1295-1299` · DTO มี `BranchId` ที่ `DocumentDtos.cs:117` |
| 2 | ออเดอร์ → คลังสินค้า | `Site.DefaultWarehouseId` / `UseGlobalInventory` ไม่มีผู้อ่าน · `DeductStockAsync` ไม่ส่งคลัง ⇒ ตัดคลังตั้งต้นเสมอ | `CmsCommerceService.cs:2105-2126` |
| 3 | ตัวเลือกสินค้า (variant) → สต็อก/สินค้า ERP | `SiteProductVariant.Stock` / `ErpProductId` ไม่มีผู้อ่านเชิงธุรกิจ · ตัดสต็อกที่สินค้าแม่เสมอ | `CmsVariantService.cs:30-56` (เขียน/echo เท่านั้น) |
| 4 | ผู้ให้บริการ (จอง) → พนักงาน/คอมมิชชัน | คอมมิชชันคิดได้**เฉพาะเมื่อเปิดบิลที่ POS** (`PosStaffSalesBasis`) · จองที่ปิดงานทางหน้าเอกสาร/ไม่ผ่าน POS ไม่เข้าฐาน · `SiteBookingAssignment.SharePercent` ไม่มีผู้อ่านนอก CMS | `CommissionService.cs:328-345` · `PosService.Bookings.cs:254` |
| 5 | PDPA DSR → ข้อมูล CMS | `PdpaService` แตะเฉพาะ `Users/Contacts/Documents/Payments/AuditLogs/UserExternalLogins` — **ไม่รวม `SiteCustomer`/`SiteOrder`/`SiteBooking`/`CmsLead`/`SiteFormSubmission`** · ลบข้อมูลลูกค้าเว็บมีแค่ `ProcessDataDeletionAsync` แยก (ไม่ลบ guest fields บนออเดอร์/จอง/lead) | `Pdpa/PdpaService.cs:255-564` · `CmsCustomerService.cs:274-292` |
| 6 | แจ้งเตือนร้าน (ออเดอร์/จอง/lead/ฟอร์ม) | `BookingNotifier` ส่งถึง**ลูกค้า**เท่านั้น (เตือนนัด/ได้คิว) · ไม่มีการแจ้งร้านเมื่อมีออเดอร์/จอง/lead ใหม่ · `SiteForm.NotifyEmails` ไม่มีผู้อ่าน | `Cms/BookingNotifier.cs:22-41` |
| 7 | ฟอร์มติดต่อ → เอกสาร ERP / CRM | `CreateErpDocument` สร้าง `Document` ด้วย `_db.Documents.Add` ตรง **ข้าม `IDocumentService`** และไม่มี `ContactId` · ไม่สร้าง `CmsLead` | `CmsCustomerService.cs:497-511` |
| 8 | ลูกค้าเว็บ → Contact | resolver **3 สำเนาที่กติกาต่างกัน** (`ResolveErpContactForOrderAsync` ไม่กรอง `IsDeleted`; `ResolveOrCreateContactFromOrderAsync` กรอง; booking สำเนาที่สาม; lead ใช้กุญแจเลขภาษี) — ขัด §A "Resolver กลาง" | `CmsCommerceService.cs:1911,2434` · `CmsBookingService.cs:1771-1797` · `CmsLeadService.cs:267-324` |
| 9 | ออเดอร์ → รายงาน/แดชบอร์ด | ไม่มีรายงานใดอ่าน `SiteOrders` ตรง — เห็นในบัญชีเฉพาะหลัง sync (ซึ่งเกิดตอนยืนยันชำระ) ⇒ ออเดอร์ค้างจ่าย/ยกเลิก/อัตราแปลงไม่อยู่ในแดชบอร์ด | grep `SiteOrders` นอก CMS: แค่ `SettingsService`/`AdminCompanyUsageController.Audit`/`IssuedDocumentHooks`/`DocumentService.Reissue` |
| 10 | `SitePaymentGateway` (API key/merchant) → ผู้ให้บริการชำระ | `ApiKeyEncrypted`/`SecretKeyEncrypted`/`MerchantId`/`SupportedCurrencies` ไม่มีผู้อ่าน — เงินออนไลน์เดินผ่าน `PaymentIntent` + provider ระดับบริษัท · แถวเว็บใช้แค่ PromptPay/บัญชีโอน ⇒ ช่องคีย์ต่อเว็บเป็นของหลอก | `CmsCommerceService.cs:1195` (echo `HasApiKey` เท่านั้น) |

---

## §1 ตารางค่าตั้ง (จัดกลุ่มตาม entity)

ย่อ: **CCC** = `Controllers/CmsCommerceConfigController.cs` (writer :59-138 / echo :147-181) · **CCS** = `Services/Implementations/CmsCommerceService.cs` ·
**CBS** = `Services/Implementations/CmsBookingService.cs` · **BSE** = `Helpers/BookingSlotEngine.cs` · **mto-modal** = `pages/cms-orders.html:290-352`

### 1.1 `SiteCommerceConfig` (`Models/Entities/CmsSite.cs:115-206`)

| ฟิลด์ | ชนิด/ค่าเริ่มต้น | UI | ผู้อ่าน (เชิงธุรกิจ) | สถานะ |
| --- | --- | --- | --- | --- |
| CheckoutMode | enum / `PaymentRequired` | ไม่มี | — (echo CCC:150) | ❌ |
| AutoGenerateQuotation | bool / false | ไม่มี | — | ❌ |
| RequireCustomerAccount | bool / false | ไม่มี | — | ❌ |
| EnableGuestCheckout | bool / true | ไม่มี | — | ❌ |
| DefaultVatRate | decimal / 7 | ไม่มี | — (ผู้อ่านชื่อเดียวกันเป็นของ `CompanySettings` — `CompanyVatStatus.cs:72`) · อัตราจริงมาจาก `Product.VatRate` CCS:662 | ❌ (checker นับพลาด) |
| PricesIncludeVat | bool / true | ไม่มี | — · sync ฮาร์ดโค้ด `PricesIncludeVat: true` CCS:1280 | ❌ (checker นับพลาด) |
| EnableTaxInvoice | bool / true | ไม่มี | — · ช่องขอใบกำกับแสดงเสมอ `storefront.html:1223` | ❌ |
| EnableShipping | bool / true | **ไม่มี** | CCS:2684 (ปิด ⇒ ไม่คิดค่าส่ง) | ⚠️ (ไม่มีช่อง) |
| EnableShippingCalculation | bool / false | ไม่มี | — (คิดโซนเสมอเมื่อมีโซน CCS:2690-2733) | ❌ |
| FlatShippingRate | decimal? | mto-modal `mtoFlatRate` | CCS:2733 `ShippingFeeRules.SiteFlatFee` | ✅ |
| FreeShippingThreshold | decimal? | mto-modal `mtoFreeAbove` | CCS:2733 | ✅ |
| EnableStockTracking | bool / true | ไม่มี | — | ❌ |
| AutoDeductStock | bool / true | ไม่มี | — · ตัดเสมอ CCS:1576,1834 | ❌ |
| DefaultStockBehavior | enum / `InStockOnly` | ไม่มี | — (ใช้ `SiteProduct.StockBehavior` รายสินค้า CCS:2089) | ❌ |
| ShowStockQuantity | bool / false | ไม่มี | — | ❌ |
| LowStockThresholdJson | string? | ไม่มี | — (ไม่มีแม้ writer) | ❌ |
| EnableCoupons | bool / true | ไม่มี | — · `apply-coupon` เปิดเสมอ `CmsCommerceExtController.cs:91` | ❌ |
| EnableTierPricing | bool / true | ไม่มี | — | ❌ |
| EnableReviews | bool / true | ไม่มี | — | ❌ |
| ReviewAutoApprove | bool / false | ไม่มี | — (`IsApproved=false` ตายตัว `CmsReviewService.cs:54`) | ❌ |
| ReviewRequirePurchase | bool / false | ไม่มี | — | ❌ |
| EnableWishlist | bool / true | ไม่มี | — | ❌ |
| EnableAbandonedCartRecovery | bool / true | ไม่มี | — (`AbandonedCartService.cs:66`) | ❌ |
| CartExpiryDays | int / 7 | ไม่มี | — | ❌ |
| AbandonedCartHours | int / 3 | ไม่มี | — (ฮาร์ดโค้ด `AddHours(-3)`) | ❌ |
| OrderNumberPrefix | string / "WEB" | ไม่มี | — (ฮาร์ดโค้ด CCS:1993) | ❌ |
| AutoConfirmOrders | bool / false | ไม่มี | — | ❌ |
| NotifyOnNewOrder | bool / true | ไม่มี | — | ❌ |
| OrderNotificationEmails | string? | ไม่มี | — | ❌ |
| AutoSyncToErp | bool / false | ไม่มี | — (sync เสมอ CCS:1509) | ❌ |
| ErpDocumentType | enum / Invoice | ไม่มี | — (ผู้อ่านชื่อเดียวกันเป็นของ `SiteForm`/`SiteBooking`) · CCS:1254 ตัดสินเอง | ❌ (checker นับพลาด) |
| EnableOnlinePayment | bool / true | ไม่มี | — | ❌ |
| EnableCod | bool / false | ไม่มี | — | ❌ |
| EnableBankTransfer | bool / true | ไม่มี | — | ❌ |
| ShippingVatAllocation | string / MaxRate | mto-modal `mtoShipVat` | CCS:2765 → ตรึงบนออเดอร์ :727 → :1363 | ✅ |
| DepositForfeitOnCancelPercent | decimal? | mto-modal `mtoForfeitPct` | CCS:897 | ✅ |
| DefaultProductSort | string / "newest" | ไม่มี | — | ❌ |
| ProductsPerPage | int / 20 | ไม่มี | — | ❌ |
| ShowComparePrice | bool / true | ไม่มี | — | ❌ |
| ShowSku | bool / false | ไม่มี | — | ❌ |
| OrderConfirmMessageTh / En | string? | ไม่มี | — | ❌ ×2 |
| QuotationMessageTh | string? | `cms-orders.html:364` `gwQuoteFooter` | CCS:2327 (ท้ายใบเสนอราคา) | ✅ |
| QuotationMessageEn | string? | ไม่มี | — | ❌ |
| EnablePickup | bool / false | mto-modal `mtoPickup` | CCS:2670 | ✅ |
| MtoPaymentTermMode | string? | mto-modal `mtoTerm` | CCS:2753 | ✅ |
| MtoDepositPercent / FixedAmount | decimal? | mto-modal | CCS:2754 | ✅ ×2 |
| MtoClosedWeekdays | csv? | mto-modal | CCS:2661 | ✅ |
| MtoCutoffHour | int? | mto-modal | CCS:2660 | ✅ |
| MtoSkipCompanyHolidays | bool / true | mto-modal | CCS:2651 | ✅ |
| MtoDepositInvoicing | string | mto-modal | CCS:2761 | ✅ |

### 1.2 `Site` (`Models/Entities/CmsSite.cs:7-110`) — UI: `cms-sites.html` (สร้าง/ตั้งเว็บ) · `cms-edit.html` (ช่อง `s*` SEO/ติดต่อ/captcha)

| ฟิลด์ | ค่าเริ่มต้น | UI | ผู้อ่าน | สถานะ |
| --- | --- | --- | --- | --- |
| Name / NameEn / Slug | — | cms-sites | rendering ทั่วไป | ✅ |
| Status | Draft | cms-sites | publish/rendering | ✅ |
| SiteType | Corporate | cms-sites, cms-edit | `CmsSiteService.cs:268` → `CmsModuleResolver` · `storefront.html:1046` | ✅ |
| IndustryType | General | cms-sites | seeder `CmsSiteService.cs:132` · `CmsModuleResolver.IsLodgingSite` | ✅ |
| RenderMode | ServerRendered | cms-sites | — (echo `CmsRenderingService.cs:202`) | ❌ |
| Subdomain / CustomDomain | — | cms-sites (subdomain) · custom: API | การหาเว็บตามโดเมน (ผ่าน `SiteDomain`) | ⚠️ (custom domain ไม่มีช่อง) |
| SslEnabled | false | ไม่มี | — (echo `CmsSiteService.cs:613`) | ❌ |
| BranchId | null | **ไม่มี** | `CCS:1295` เขียนลง `InternalNotes` อย่างเดียว | ⚠️ (ผลอ่อนมาก — ดู §3 #1) |
| DefaultWarehouseId | null | ไม่มี | — | ❌ |
| UseGlobalInventory | true | cms-sites | — | ❌ (มีช่อง ไม่มีผล) |
| ThemeId | null | cms-edit | rendering ผ่าน navigation `Theme` · `CmsSiteService.cs:737` | ✅ |
| LogoUrl / FaviconUrl | null | **ไม่มีใน cms pages** | `storefront.html:332,396` | ⚠️ ×2 (API เท่านั้น) |
| MetaTitle / MetaDescription / OgImageUrl | null | cms-edit `sMetaTitle` ฯลฯ | `CmsRenderingService.cs:270,335` | ✅ ×3 |
| MetaKeywords | null | cms-edit | — ระดับเว็บ (มีแต่ระดับหน้า `:98`) | ⚠️ |
| CanonicalUrl / RobotsDirective | null | ไม่มี | `CmsRenderingService.cs:270,283` | ⚠️ ×2 |
| GoogleAnalyticsId / GoogleTagManagerId / MetaPixelId | null | cms-edit | `storefront.html:471-487` | ✅ ×3 |
| CustomHeadScripts / CustomBodyScripts | null | ไม่มี | — | ❌ ×2 |
| DefaultLanguage | th | cms-sites | `CmsRenderingService.cs:281` · `AbandonedCartService.cs:125` | ✅ |
| DefaultCurrency | THB | cms-sites | CCS:615 (สกุลออเดอร์) | ✅ |
| CaptchaProvider / CaptchaSiteKey | null | cms-edit | `CmsCustomerService.cs:465` · `storefront.html:784` | ✅ ×2 |
| ContactPhone / ContactEmail / LineId / FacebookUrl / InstagramUrl | null | cms-edit | `CmsRenderingService.cs:131` · `storefront.html:2018` | ✅ (นับ 1 กลุ่ม) |
| CookieConsentEnabled | true | ไม่มี | echo `CmsRenderingService.cs:254` แต่ **`storefront.html` ไม่อ่าน** (ไม่มีแบนเนอร์) | ❌ |
| PrivacyPolicyUrl | null | ไม่มี | — (echo `CmsSiteService.cs:319`) | ❌ |
| TermsOfServiceUrl | null | ไม่มี | — (ไม่มีแม้ writer) | ❌ |
| MaxProducts / MaxPages | null | cms-edit (แสดงโควตา) | `CmsQuotaService.cs:29-53` | ✅ ×2 |
| MaxStorageBytes / MaxBandwidthBytesPerMonth | null | ไม่มี | `CmsQuotaService.cs:87,107` (รายงานโควตาเท่านั้น ไม่บังคับ) | ⚠️ ×2 |

### 1.3 `SiteBookingSettings` (`CmsBookingResource.cs:8-44`) — UI ทั้งหมดที่ `cms-edit.html` (~:2960, prefix `bss*`)

| ฟิลด์ | ค่าเริ่มต้น | ผู้อ่าน | สถานะ |
| --- | --- | --- | --- |
| DurationMinutes · BufferBefore/AfterMinutes · MinAdvanceHours · MaxAdvanceDays | 60/0/0/1/30 | `BSE:509-527` (ค่าตั้งต้นเมื่อบริการ `UseSiteDefaults`) · CBS:723,742 | ✅ ×5 |
| GuaranteedInvoiceTiming | TaxInvoiceAtBooking | CBS:389,399 (`BookingInvoiceTimingPolicy`) | ✅ |
| BookingHoursJson | null | CBS:328 (เขียนจาก `body.schedule` `cms-edit.html:2976`) | ✅ |
| WaitlistHoldMinutes | 0 | CBS:950 | ✅ |
| ReminderEnabled / ReminderHoursBefore / ReminderTemplate | true/csv/null | `Background/BookingReminderJob.cs:90,113,124` | ✅ ×3 |
| OnlineDepositEnabled | false | `Payments/PublicPaymentResolver.cs:149` · `CmsBookingService.Payment.cs:245` | ✅ |
| ProviderAllocationEnabled / ProviderRecommendStrategy | null(=true)/LeastLoadedDay | CBS:352 · `BSE:528` · `cms-booking-providers.html` | ✅ ×2 |
| FormFieldsJson (ช่องที่กรอกตอนจอง) | null | CBS:304,801 (`BookingFormFields.Parse`) | ✅ |
| CompletionDocument | None | CBS:390,1214 | ✅ |

### 1.4 `SiteBookingService` ค่าตั้งรายบริการ (`CmsBooking.cs:7-80`) — UI `cms-edit.html` ~:3140-3215 (prefix `bs*`)

| ฟิลด์ | ผู้อ่าน | สถานะ |
| --- | --- | --- |
| DurationMinutes · BufferMinutes · BufferBeforeMinutes · MaxCapacity · Min/MaxAdvanceBooking* | `BSE:507-516` · CBS:719-743 | ✅ ×7 |
| SlotMode · SlotIntervalMinutes · MaxUnitsPerBooking · HalfDaySplitTime | `BSE:159-160,507,515` | ✅ ×4 |
| CapacityUnit · CapacityGroup · EnforceSlotCapacity · OverlapLimit | `BSE:264,291,362,516` · CBS:705-708 | ✅ ×4 |
| WaitlistEnabled · ResourceMode · UseSiteDefaults | CBS:765 · `CmsBookingService.Notify.cs:120` · CBS:723 | ✅ ×3 |
| Price · DepositAmount · DepositPercent · BookingType | CBS:311 · `PublicPaymentResolver.cs:141-152` · `Workflow.cs:303` | ✅ ×4 |
| ProductId (สินค้า ERP สำหรับบรรทัดเอกสาร) | `CmsBookingService.Products.cs:46-49` · CBS:290 คำเตือน | ✅ |
| AutoConfirm | CBS:858 | ✅ |
| FreeCancellationHours · CancellationFeePercent | `Reschedule.cs:65` · `Payment.cs:256-298` | ✅ ×2 |
| Category / Tags | หน้าร้านจัดกลุ่ม (ไม่มีตัวกรองเชิงธุรกิจ) | ⚠️ ×2 |

### 1.5 `SiteForm` (`CmsCustomer.cs:107-136`) — UI `cms-edit.html` (`frmName/frmType/frmNotify/frmAutoReply/frmCaptcha/frmCreateErp`)

| ฟิลด์ | UI | ผู้อ่าน | สถานะ |
| --- | --- | --- | --- |
| NotifyEmails | `frmNotify` | — (ผู้อ่านชื่อเดียวกันเป็นของ `LodgingProperty`) | ❌ (มีช่อง ไม่มีผล) |
| SendAutoReply | `frmAutoReply` | — | ❌ (มีช่อง ไม่มีผล) |
| AutoReplySubject / AutoReplyBody | ไม่มี | — | ❌ ×2 |
| SuccessMessage / RedirectUrl | ไม่มี | — | ❌ ×2 |
| RequireCaptcha | `frmCaptcha` | `CmsCustomerService.cs:462` | ✅ |
| RateLimitPerHour | ไม่มี | `CmsCustomerService.cs:479-483` | ⚠️ |
| CreateErpDocument / ErpDocumentType | `frmCreateErp` | `CmsCustomerService.cs:498-511` — **สร้าง Document ตรงด้วย `_db.Documents.Add` ไม่ผ่าน `IDocumentService` และไม่มี `ContactId`** | ⚠️ ×2 (ผิดสถาปัตยกรรม) |

### 1.6 คูปอง / ค่าส่ง / ช่องทางชำระ / ลูกค้า / variant

| entity.ฟิลด์ | UI | ผู้อ่าน | สถานะ |
| --- | --- | --- | --- |
| `SiteCoupon` Code · DiscountType · DiscountValue · MaxDiscountAmount · MinOrderAmount · Scope · CategoryIdsJson · ProductIdsJson · StartsAt/ExpiresAt · MaxUses · MaxUsesPerCustomer · IsFirstOrderOnly · FreeShipping | **ไม่มีหน้าแอดมิน** (API `CmsCommerceExtController.cs:44-91` · ไม่มีใน `api.js`) · หน้าร้านไม่มีช่องกรอกคูปอง | `CmsCouponService.cs:108-263` ตรวจครบ · ใช้จริงตอนสร้างออเดอร์ + `RecordCouponUsageAsync` `CmsCommerceController.cs:246` | ⚠️ ×12 |
| `SiteCoupon.CustomerGroupTag` | ไม่มี | — (ชื่อเดียวกันอ่านใน `SitePricingTier`) | ❌ |
| `SiteShippingZone` Name · CountryCodes · Provinces · PostalCodePatterns · IsActive · SortOrder | **ไม่มีหน้าแอดมิน** (API `CmsCommerceExtController.cs:112-179`) | `CmsShippingService.cs:201-221` · CCS:2690-2701 | ⚠️ ×6 (ดูทีม B) |
| `SiteShippingRate` CarrierName · RateType · BaseRate · PerKgRate · FreeAboveAmount · Min/MaxWeightKg · EstimatedDeliveryDaysMin/Max | ไม่มี | `CmsShippingService.cs:177-186` · CCS:2701 | ⚠️ ×9 |
| `SitePaymentGateway` PromptPayId · PromptPayQrUrl · BankName · BankAccountNumber · BankAccountName | `cms-orders.html:360-384` | CCS:2510-2533 (หน้าร้านแสดง QR/บัญชี) | ✅ ×5 |
| `SitePaymentGateway.WebhookSecret` | ไม่มี | `CmsWebhookController.cs:94,112` (HMAC เส้นเก่า) | ⚠️ |
| `SitePaymentGateway.IsTestMode` | ไม่มี | `storefront.html:1587` แสดงป้ายอย่างเดียว | ⚠️ |
| `SitePaymentGateway` ApiKeyEncrypted · SecretKeyEncrypted · MerchantId · SupportedCurrencies · GatewayType (ฝั่งชำระ) | ไม่มี | — (echo `HasApiKey` CCS:1195) | ❌ ×5 |
| `SiteCustomer.AcceptMarketing` | ไม่มีบนหน้าร้าน | — (**อีเมลกู้ตะกร้าไม่ดูค่านี้**) | ❌ |
| `SiteCustomer.ConsentGiven/WithdrawnAt` | API `CmsCustomerService.cs:265-267` | ไม่มีด่านใดอ่านก่อนส่งข้อความ/ประมวลผล | ⚠️ |
| `SiteCustomer.PreferredLanguage` / `PreferredCurrency` | ไม่มี | — (`AbandonedCartService` ใช้ภาษาเว็บ) | ❌ ×2 |
| `SiteCustomer.CustomerGroup` / `Tags` | API | ราคาแบบขั้น (tier) อ่าน group | ⚠️ |
| `SiteProductVariant` Stock · ErpProductId · Price | ไม่มีหน้าแอดมิน | — (เขียน/echo `CmsVariantService.cs:30-176`) | ❌ ×3 |

---

## §2 ฟังก์ชันที่ปิดไม่ได้ (พฤติกรรมฮาร์ดโค้ดที่ร้านอาจอยากปิด)

| ฟังก์ชัน | พฤติกรรมตอนนี้ | มีธง? | ธงมีผล? | หลักฐาน | ขนาด |
| --- | --- | --- | --- | --- | --- |
| รีวิวสินค้า (ส่ง/แสดง) | API รับรีวิวจากใครก็ได้ · รอแอดมินอนุมัติเสมอ | `EnableReviews` · `ReviewAutoApprove` · `ReviewRequirePurchase` | ❌ | `CmsCommerceExtController.cs:262` · `CmsReviewService.cs:54` | S |
| Wishlist | API เปิดเสมอ (หน้าร้านไม่มี UI) | `EnableWishlist` | ❌ | `CmsCommerceExtController.cs:341-367` | S |
| คูปอง | ใช้ได้เสมอผ่าน API (หน้าร้าน/แอดมินไม่มี UI) | `EnableCoupons` | ❌ | `CmsCommerceExtController.cs:91` | S (+M หน้าแอดมิน) |
| ราคาแบบขั้น | คำนวณเสมอ | `EnableTierPricing` | ❌ | — | S |
| อีเมลกู้ตะกร้า | ส่งทุกเว็บหลัง 3 ชม. · ไม่ดูความยินยอมการตลาด | `EnableAbandonedCartRecovery` · `AbandonedCartHours` | ❌ | `AbandonedCartService.cs:66-93` | S (PDPA ⇒ ทำก่อน) |
| ฟอร์ม lead/ติดต่อบนหน้าร้าน | เปิดทุกเว็บ (`CmsModuleResolver` ใส่ `leads` เมื่อมีเว็บ) | ไม่มี | — | `Helpers/CmsModuleResolver.cs:46` · `storefront.html:891` | S |
| แชท | ไม่พบวิดเจ็ตแชทใน `storefront.html` | — | — | — | — |
| วิดเจ็ตจองบนหน้าร้าน | แสดงเมื่อมีบริการ (เปิด/ปิดได้ทางอ้อมด้วย `SiteBookingService.IsActive`) | ทางอ้อม | ✅ ทางอ้อม | `CmsModuleResolver.cs:44` | — |
| หมายเหตุออเดอร์ | ช่อง "หมายเหตุ" แสดงเสมอ | ไม่มี | — | `storefront.html:1221` | S |
| ขอใบกำกับภาษี | ช่องแสดงเสมอ (แม้บริษัทไม่จด VAT — server เปลี่ยนเป็นใบแจ้งหนี้เงียบ ๆ CCS:1254) | `EnableTaxInvoice` | ❌ | `storefront.html:1223` | S |
| แปลงออเดอร์เป็นใบเสนอราคา (ฝั่งลูกค้า) | ลูกค้ากดได้เสมอ `[AllowAnonymous]` | `AutoGenerateQuotation`/`CheckoutMode` | ❌ | `CmsCommerceController.cs:312` | S |
| Guest checkout / บัญชีลูกค้า | guest อย่างเดียว (ไม่มี UI ล็อกอินหน้าร้าน) | `EnableGuestCheckout` · `RequireCustomerAccount` | ❌ | `storefront.html` (ไม่มีเรียก auth) | M |
| วิธีชำระ | แสดงตามช่องทางแถวแรก · COD ไม่มี | `EnableOnlinePayment/BankTransfer/Cod` | ❌ | CCS:2510 | M |
| ตัดสต็อก | ตัดเสมอตอนยืนยันชำระ/ส่งมอบ | `AutoDeductStock` · `EnableStockTracking` | ❌ | CCS:1576,1834 | S |
| ซิงก์ ERP | ซิงก์เสมอตอนยืนยันชำระ | `AutoSyncToErp` | ❌ | CCS:1509 | S (+ต้องคิดผลบัญชี) |
| ยืนยันออเดอร์อัตโนมัติ | ไม่มี (รอพนักงาน/webhook) | `AutoConfirmOrders` | ❌ | — | S |
| คุกกี้แบนเนอร์ | ไม่มีแบนเนอร์เลย | `CookieConsentEnabled` | ❌ | `CmsRenderingService.cs:254` (ส่ง) · `storefront.html` (ไม่อ่าน) | M |
| แจ้งร้านเมื่อมีออเดอร์/จอง/lead | ไม่มีการแจ้ง | `NotifyOnNewOrder` · `SiteForm.NotifyEmails` | ❌ | — | M |

**สวิตช์โมดูลที่มีอยู่จริง** = `Helpers/CmsModuleResolver.Resolve` ตัดสินจาก "ข้อเท็จจริง" (มีเว็บพาณิชย์/มีบริการจอง/มีที่พัก/มีเว็บ) — **ไม่ใช่สวิตช์ที่ร้านกดปิดได้** · ระดับฟังก์ชันย่อยไม่มีเลย

---

## §3 แผนที่จุดเชื่อม CMS ↔ ระบบ

| จาก → ถึง | กลไก | สถานะ | หลักฐาน | ช่องว่าง |
| --- | --- | --- | --- | --- |
| ออเดอร์ → เอกสาร ERP (ใบแจ้งหนี้/ใบกำกับ) | `SyncOrderToErpCoreAsync` → `IDocumentService.CreateDocumentAsync` (gap-free · tax point · JE ตอนอนุมัติ) | ✅ | CCS:1233-1292 · เรียกจาก `ConfirmPaymentLockedAsync` CCS:1509 + ปุ่มพนักงาน `CmsCommerceController.cs:256` | ไม่ส่ง `BranchId` (§0 #1) · ไม่มีสวิตช์ `AutoSyncToErp` |
| ออเดอร์ → มัดจำ/ใบส่วนที่เหลือ | `IssueOrderDepositReceiptAsync` · `SettleFinalInvoiceAsync` | ✅ | CCS:1666,1740 | — |
| ออเดอร์ → JE | ผ่านการอนุมัติเอกสาร (`ApproveOrderDocumentIfPendingAsync`) | ✅ | CCS:1620 | `SiteOrder.ErpJournalEntryId` ไม่เห็นผู้เขียน (ใช้ผ่านเอกสาร) |
| ออเดอร์ยกเลิก → ใบลดหนี้/ริบมัดจำ | `ProposeCreditNoteOnCancelAsync` · `ForfeitDepositsOnCancelAsync` | ✅ | CCS:893,930 | — |
| ออเดอร์ → Contact | `ResolveErpContactForOrderAsync` / `ResolveOrCreateContactFromOrderAsync` (ผูก `SiteCustomer.ContactId`) | ⚠️ | CCS:1911,2434 | 3 สำเนา กติกาต่างกัน (§0 #8) · จับคู่ด้วยอีเมลอย่างเดียว ไม่ใช้กุญแจเลขภาษี+สาขา (ต่างจาก lead) |
| ลูกค้าเว็บ (`SiteCustomer`) → Contact | `ContactId` ถูกตั้งเมื่อมีออเดอร์/จองครั้งแรก | ⚠️ | `CmsCustomer.cs:23` | สมัครสมาชิกอย่างเดียวไม่สร้าง/ผูก Contact |
| สินค้าเว็บ → Product ERP | `SiteProduct.ProductId` (บังคับ) · `AutoPublishMasterCatalogAsync` | ✅ | `CmsCommerce.cs:12` · CCS:312 | — |
| ออเดอร์ → สต็อก | `IProductService.AdjustStockAsync` (fallback `IStockLedger.MoveAsync`) | ✅ | CCS:2070-2135 | ไม่ส่งคลัง (`DefaultWarehouseId` ไม่ถูกอ่าน) · variant ไม่ตัด (§0 #2-3) |
| ยกเลิก → คืนสต็อก | `RestoreStockAsync` / `ReverseStockIfDeductedAsync` | ✅ | CCS:2150,2473 | — |
| เว็บ ↔ POS (สินค้า) | ใช้ `Product` ตัวเดียวกัน ⇒ สต็อก/ราคา ERP ร่วมกัน | ✅ | `SiteProduct.ProductId` | ราคาเว็บ override แยก (`OverrideSellingPrice`) — ตั้งใจ |
| จองเว็บ → POS | `PosService.Bookings` ดึง `SiteBookings` · เปิดบิล ⇒ `PosOrderId` + `InProgress` | ✅ | `PosService.Bookings.cs:66-79,251-255` | ไม่กรองสาขา (เว็บมี `BranchId` แต่ไม่ถูกใช้) |
| จอง → เอกสาร ERP | `SyncBookingToErpAsync` (นโยบาย `CmsBookingErpPolicy`) | ✅ | CBS:885,1739-1841 | Contact สำเนาที่สาม (CBS:1771-1797) |
| จอง → เครดิต/แพ็กเกจคลาส | `PrepareCreditBookingAsync` → CourseCredit | ✅ | CBS:865 · `CmsBookingService.Credits.cs:26-42` | (รายละเอียดดูทีม C) |
| ชำระออนไลน์ ออเดอร์/จอง | `PaymentIntent` + `SiteOrderPaymentHandler` / `SiteBookingPaymentHandler` · แขกผ่าน `PublicPaymentResolver` | ✅ | `Services/Payments/Handlers/SiteOrderPaymentHandler.cs:17` · `SiteBookingPaymentHandler.cs:18` · `PublicPaymentResolver.cs:141-152` | เส้นเก่า `CmsWebhookController` (HMAC ต่อเว็บ) ยังเปิดอยู่คู่กัน — สองทางเข้าเงินเดียว |
| ช่องทางชำระต่อเว็บ (`SitePaymentGateway`) → provider | — | ❌ | CCS:1195 | คีย์ต่อเว็บไม่ถูกใช้ (§0 #10) |
| ผู้ให้บริการ (`SiteBookingResource.StaffUserId`) → User | `AssignedStaffId` บนการจอง | ✅ | `Workflow.cs:128-129` | — |
| ผู้ให้บริการ → Employee/คอมมิชชัน/เงินเดือน | เฉพาะผ่านบิล POS (`PosStaffSalesBasis`) | ⚠️ | `CommissionService.cs:328-345` | จองที่ไม่ผ่าน POS ไม่เข้าฐาน · ไม่มี basis "ยอดบริการจากการจอง" · `SharePercent` ไม่มีผู้อ่าน |
| แจ้งลูกค้า (จอง) | `BookingNotifier` → `IEmailService` / `ILineNotifyService` (เตือนนัด · ได้คิว) | ✅ | `Cms/BookingNotifier.cs:22-41` · `BookingReminderJob.cs:113-124` | ไม่มี SMS · ไม่มีอีเมลยืนยันการจองทันที |
| แจ้งลูกค้า (ออเดอร์) | อีเมลกู้ตะกร้าเท่านั้น | ⚠️ | `AbandonedCartService.cs:92` | ไม่มีอีเมลยืนยันออเดอร์ (`OrderConfirmMessage*` ไม่ถูกอ่าน) · ไม่ดูความยินยอม |
| แจ้งร้าน (ออเดอร์/จอง/lead/ฟอร์ม) | — | ❌ | — | §0 #6 |
| หน้าร้าน lead → CRM | `POST storefront/leads` → `CmsLeadService` · แปลงเป็นใบเสนอราคาผ่าน `IDocumentService` + `EnsureContactLinkedAsync` | ✅ | `storefront.html:891` · `CmsLeadService.cs:203-324` | — |
| ออเดอร์ → lead (แปลงเป็นใบเสนอราคา) | `ConvertOrderToQuotationAsync` สร้าง `CmsLead` | ✅ | CCS:2395-2415 | — |
| ฟอร์มติดต่อ (`SiteForm`) → lead/เอกสาร | `_db.Documents.Add` ตรง | ❌ | `CmsCustomerService.cs:497-511` | ข้าม DocumentService · ไม่มี Contact · ไม่สร้าง lead (§0 #7) |
| CMS → รายงาน/แดชบอร์ด | ผ่านเอกสาร ERP หลัง sync เท่านั้น | ⚠️ | §0 #9 | ไม่มีรายงานยอดขายเว็บ/ช่องทาง/อัตราแปลง |
| CMS → PDPA DSR | ไม่มี | ❌ | `Pdpa/PdpaService.cs` | §0 #5 · ไม่มีแบนเนอร์คุกกี้ · ไม่มีช่องยินยอมบนหน้าร้าน |
| เว็บ → สาขา/คลัง | `Site.BranchId`/`DefaultWarehouseId` | ❌ (ผลอ่อน) | CCS:1295 | §0 #1-2 · ไม่มีช่องบนจอด้วย |
| ภาษา/สกุลเงินเว็บ → ออเดอร์/อีเมล | `DefaultCurrency` → ออเดอร์ · `DefaultLanguage` → อีเมลตะกร้า | ✅ | CCS:615 · `AbandonedCartService.cs:125` | `SiteLocale.ExchangeRateToBase` ไม่ตรวจในรอบนี้ |

---

## §4 ข้อเสนอพัฒนา

### R-209-D1 — `SiteFeatureFlags` + ตัวตัดสินกลาง `Helpers/CmsFeatureFlags` (L · ทำก่อน)

เหตุผล: ธงฟังก์ชันที่ไม่มีผล 20+ ตัวกระจายอยู่ใน `SiteCommerceConfig` (ไม่มีหน้าจอ) และฟังก์ชันใหม่ (lead form · หมายเหตุ · ขอใบกำกับ) ไม่มีธงเลย ·
ต่อสายทีละธงแบบเดิม = สำเนาเงื่อนไขในทั้ง server + `storefront.html` (ขัด §F2 ข้อ 5)

**ไม่สร้างตารางใหม่** — ใช้ฟิลด์เดิมใน `SiteCommerceConfig` ที่มีอยู่แล้ว + เพิ่มเฉพาะที่ขาด:

| ธง | ฟิลด์ (เดิม/ใหม่) | ค่าเริ่มต้น = พฤติกรรมปัจจุบัน | ผู้อ่านที่ต้องต่อ |
| --- | --- | --- | --- |
| reviews | `EnableReviews` (เดิม) | true | `CmsReviewService.Create` + list (ปิด ⇒ 404 พร้อมเหตุ) |
| reviewAutoApprove · reviewRequirePurchase | เดิม | false · false | `CmsReviewService.cs:54` |
| wishlist | `EnableWishlist` | true | `CmsWishlistService` |
| coupons | `EnableCoupons` | true | `ValidateAndApplyCouponAsync` + `CreateOrderAsync` |
| tierPricing | `EnableTierPricing` | true | ตัวคิดราคาขั้น |
| abandonedCart · abandonedCartHours | เดิม | true · 3 | `AbandonedCartService` (+ ต้องมี `AcceptMarketing` — คำตัดสิน D-Q2) |
| guestCheckout · requireAccount | เดิม | true · false | `CreateOrderAsync` |
| taxInvoiceRequest | `EnableTaxInvoice` | true | หน้าร้าน + `CreateOrderAsync` (บริษัทไม่จด VAT ⇒ บังคับ false ผ่าน `CompanyVatStatus`) |
| quoteConversion | ใหม่ `EnableQuoteConversion` | true | `CmsCommerceController.cs:312` |
| orderNotes | ใหม่ `EnableOrderNotes` | true | หน้าร้าน + ตัด `CustomerNotes` |
| leadForm | ใหม่ `EnableLeadForm` | true | `storefront/leads` + `CmsModuleResolver` |
| autoDeductStock · autoSyncToErp · autoConfirm | เดิม | **true · true · false** (ค่าเริ่มต้นใน entity ตอนนี้ `AutoSyncToErp=false` ขัดพฤติกรรมจริง ⇒ ต้อง migration ตั้งเป็น true ก่อนต่อสาย — F2 ข้อ 9) | CCS:1509,1576 |
| paymentMethods | `EnableOnlinePayment/BankTransfer/Cod` | true/true/false | `GetStorefrontPaymentOptionsAsync` + ตรวจตอนสร้าง intent |
| cookieBanner | `Site.CookieConsentEnabled` | true (แต่ตอนนี้ไม่มีแบนเนอร์ ⇒ ต้องสร้างแบนเนอร์) | `storefront.html` |

ตัวตัดสิน: `CmsFeatureFlags.Resolve(SiteCommerceConfig?, Site, CompanyVatProfile) → CmsFeatureSet` (record ชื่อธง → bool + เหตุผลเมื่อถูกบังคับปิด) ·
server ทุกทางเข้าเรียกตัวนี้ · ส่ง `features` ใน payload `CmsRenderingService` ให้หน้าร้านแสดง/ซ่อนตามค่าที่ server คิดแล้ว ·
หน้าแอดมิน "ตั้งค่าร้านค้า" ใหม่ใน `cms-edit.html` (แท็บ) อ่าน/เขียนผ่าน `commerce/config` เดิม ·
ล็อกด้วย `tools/required_call_site_check.py` (ทุก endpoint ฟังก์ชันต้องเรียก resolver) + เทสต์ทิศตรงข้าม "ปิดแล้ว API ตอบ 404/400 พร้อมเหตุ · เปิด (ค่าเริ่มต้น) แล้วผลเหมือนเดิมทุกสตางค์"

### R-209-D2 — ลบ/ต่อสายฟิลด์ที่ไม่มีผู้อ่าน (M)
- **ลบ (หรือซ่อนจาก DTO)**: `DefaultVatRate`, `PricesIncludeVat`, `ErpDocumentType` ของ `SiteCommerceConfig` (ตัวตัดสินจริงคือ `Product.VatRate` + `CompanyVatStatus` — มีสองความจริงอันตราย) · `LowStockThresholdJson` · `EnableShippingCalculation` · `RenderMode` · `SslEnabled` · `SitePaymentGateway.ApiKey/Secret/MerchantId/SupportedCurrencies` · `SiteCustomer.PreferredCurrency`
- **ต่อสาย**: `OrderNumberPrefix` (CCS:1993 · ระวังเลขซ้ำข้ามเว็บ — prefix เป็นส่วนของกุญแจ lock) · `OrderConfirmMessageTh/En` + `QuotationMessageEn` · `DefaultProductSort`/`ProductsPerPage`/`ShowComparePrice`/`ShowSku`/`ShowStockQuantity` (ส่งผ่าน rendering payload) · `SiteForm.NotifyEmails`/`SendAutoReply`/`AutoReply*`/`SuccessMessage`/`RedirectUrl` · `PrivacyPolicyUrl`/`TermsOfServiceUrl` (ลิงก์ท้ายหน้าร้าน + ใต้ปุ่มสั่งซื้อ)
- อัปเดต `tools/settings_reader_baseline.txt` (ตัด 3 แถวที่ ℹ️) และเพิ่ม `SiteBookingSettings`/`SiteForm`/`Site` (ชุดค่าตั้ง) เข้า `ENTITIES` ของ checker พร้อมหน้าแก้ (`cms-edit.html`, `cms-sites.html`)

### R-209-D3 — สาขา/คลังของเว็บ (M)
`Site.BranchId` → `CreateDocumentRequest.BranchId` ทั้งออเดอร์ (CCS:1264) และจอง (CBS:1841) · `DefaultWarehouseId` → `StockAdjustmentRequest`/`StockMoveRequest` (CCS:2105,2119) · `UseGlobalInventory` ตัดสินว่าแสดงสต็อกรวมหรือคลังเว็บ · ช่องเลือกสาขา/คลังใน `cms-sites.html` · POS booking list กรองสาขา · ข้อมูลเดิม: ใบที่ออกแล้วไม่แตะ (ห้ามแก้ย้อนหลัง) — มีผลกับใบใหม่เท่านั้น

### R-209-D4 — Contact resolver ตัวเดียว (S-M)
`Helpers/CmsContactResolver` (หรือ service) ใช้ร่วมทั้ง 4 ทาง (ออเดอร์ ×2 · จอง · lead) — ลำดับ: `SiteCustomer.ContactId` → กุญแจเลขภาษี+สาขา (แบบ `CmsLeadService`) → อีเมล (กรอง `IsDeleted`) → สร้างใหม่ · ล็อก call site ด้วย `required_call_site_check`

### R-209-D5 — แจ้งร้าน + ยืนยันลูกค้า (M)
ตัวกลาง `ICmsShopNotifier` (อีเมล + LINE OA ของบริษัท) เรียกจาก `CreateOrderAsync` · `CreateBookingAsync` · `CmsLeadService.Create` · form submit — อ่าน `NotifyOnNewOrder`/`OrderNotificationEmails`/`SiteForm.NotifyEmails` · ส่งไม่ถึง ⇒ ประทับบนแถว (แบบ `NotifyLastError`) ไม่ใช่ LogWarning (F2 ข้อ 7) · อีเมลยืนยันออเดอร์ใช้ `OrderConfirmMessage*`

### R-209-D6 — PDPA ครอบ CMS (M · compliance)
`PdpaService` access/portability/erase ต้องรวม `SiteCustomer`(+addresses/wishlist) · guest fields ของ `SiteOrder`/`SiteBooking`/`CmsLead`/`SiteFormSubmission` (เก็บที่ hold ตาม พ.ร.บ.บัญชีเมื่อผูกเอกสารแล้ว — MAX(retention)) · อีเมลกู้ตะกร้าต้องผ่านด่าน `AcceptMarketing` · แบนเนอร์คุกกี้ตาม `CookieConsentEnabled` + `SiteCookieConsent`

### R-209-D7 — ฟอร์มติดต่อเข้าเส้นมาตรฐาน (S)
`CreateErpDocument` → สร้าง `CmsLead` (LeadType ตามชนิดฟอร์ม) แล้วให้พนักงานแปลงผ่าน `CmsLeadService` (มี Contact + `IDocumentService`) แทน `_db.Documents.Add` ตรง · แถวที่สร้างไปแล้ว: รายงานใบร่างที่ไม่มีผู้ติดต่อให้ลบ/ผูก (migration อ่านอย่างเดียว)

### R-209-D8 — คอมมิชชันจากการจอง (M)
basis ใหม่ `BookingServiceBasis` = ยอดการจองสถานะ Completed ในเดือนที่ `AssignedStaffId` (หรือ `SiteBookingAssignment.SharePercent`) ตรงผู้ใช้ — **ไม่นับซ้ำ**กับบิล POS ที่มี `PosOrderId` แล้ว · ต้องตัดสินว่าใช้ยอดก่อน/หลัง VAT (D-Q4)

### R-209-D9 — หน้าแอดมินคูปอง + variant (M)
คูปอง (`CmsCommerceExtController` coupons) + ช่องกรอกคูปองหน้าร้าน (เมื่อธง coupons เปิด) · variant: ตัดสินก่อน (D-Q5) ว่าจะใช้ `Product` ลูกของ ERP (`ErpProductId`) เป็นสต็อกจริง หรือยกเลิก `Stock` บน variant

---

## §5 คำตัดสินที่ต้องถามเจ้าของ

| # | คำถาม | ตัวเลือก | ผลต่อ |
| --- | --- | --- | --- |
| D-Q1 | ธงฟังก์ชันอยู่ใน `SiteCommerceConfig` เดิม (เพิ่มคอลัมน์) หรือแยกตาราง `SiteFeatureFlags` | ก) ใช้ตารางเดิม (แนะนำ — ฟิลด์ 2 ใน 3 มีอยู่แล้ว) · ข) ตารางใหม่ (ย้ายข้อมูล) | R-209-D1 |
| D-Q2 | อีเมลกู้ตะกร้า: ต้องได้ความยินยอมการตลาดก่อนไหม | ก) ต้อง `AcceptMarketing=true` (PDPA เข้ม · guest ไม่ได้รับ) · ข) ถือเป็นการแจ้งธุรกรรม ส่งได้ + ลิงก์ยกเลิก | R-209-D1/D6 |
| D-Q3 | `AutoSyncToErp` ปิดได้จริงไหม (ถ้าปิด เงินเข้าแล้วไม่มีเอกสาร = รายได้หลุดบัญชี) | ก) ไม่ให้ปิด — ลบธง · ข) ปิดได้ แต่ต้องมีรายการ "ออเดอร์จ่ายแล้วยังไม่ออกเอกสาร" เตือนทุกวัน | §2 · R-209-D2 |
| D-Q4 | คอมมิชชันจากการจองคิดจากยอดก่อน VAT / หลัง VAT / ยอดที่รับเงินจริง | — | R-209-D8 |
| D-Q5 | สต็อก variant: ใช้ `Product` ลูกใน ERP (หนึ่ง variant = หนึ่งสินค้า) หรือยกเลิกสต็อกระดับ variant | — | §0 #3 · R-209-D9 |
| D-Q6 | `SitePaymentGateway` คีย์ต่อเว็บ: ลบทิ้ง (ทุกเว็บใช้ provider ระดับบริษัท) หรือรองรับหลายบัญชีร้านต่อเว็บ | ก) ลบ (แนะนำ — `PAYMENT_GATEWAY_DESIGN.md` ทางเข้าเดียว) · ข) รองรับ (L) | R-209-D2 |
| D-Q7 | ปิดเส้น `CmsWebhookController` (HMAC ต่อเว็บ) เมื่อทุกเว็บย้ายไป `PaymentIntent` แล้วหรือยัง | — | §3 ชำระออนไลน์ |
| D-Q8 | `OrderNumberPrefix` ต่อเว็บ: อนุญาตซ้ำข้ามเว็บในบริษัทเดียวได้ไหม (ถ้าได้ ต้องรวม siteId ในกุญแจ lock) | — | R-209-D2 |
