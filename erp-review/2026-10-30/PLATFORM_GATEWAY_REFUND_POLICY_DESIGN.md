# ช่องทางรับชำระของ NextAcc (Omise) · หน้าทดสอบ · นโยบายยกเลิกและคืนเงิน (รอบ 219 ส่วน B)

> คำถามเจ้าของ: "ช่องทางเชื่อมต่อ payment gateway Omise และ เปิด ปิด หน้าการทดสอบ · ทำหน้า policy ยกเลิกและคืนเงิน
> มีให้ตั้งค่า เตรียมพร้อมในส่วน admin รึยัง หน้าก่อนการชำระเงินมีแสดง policy ให้ลูกค้าเห็น"
>
> สถานะ: ออกแบบรอบแรก (ตรวจอย่างเดียว) · **§2 ทำแล้วโดยเฟส 0 (ข้อ 178) · §3–§4 ทำแล้วในรอบ 219 ส่วน B (ข้อ 179) — ดู §7** · · ตรวจจากโค้ดที่ `c18ca0ce` · ยังไม่ได้คอมไพล์/รันในเครื่องนี้ (ไม่มี .NET SDK) ·
> ข้อเท็จจริงเชิง API/สัญญาของ Omise (Opn Payments) และข้อกฎหมายผู้บริโภคในเอกสารนี้**มาจากความรู้ทั่วไป ต้องยืนยันกับเอกสารทางการ/นักกฎหมายก่อนลงมือ**
> (เขียนกำกับ "ต้องยืนยัน" ไว้ทุกจุด) · ยังไม่ผ่านฝ่ายค้าน

---

## 0. คำตอบสั้น

| คำถาม | คำตอบ |
| --- | --- |
| Omise ของ **ร้าน (tenant)** ตั้งค่าได้ไหม | ✅ ได้ — `pages/payment-settings.html` (คีย์ test/live เข้ารหัส · ทดสอบเชื่อมต่อ · ด่านเปิด live · URL webhook มีรหัสลับ · audit) |
| Omise ของ **NextAcc เอง** (เก็บค่าแพ็กเกจ/ส่วนเสริมจากลูกค้า) ตั้งค่าในแอดมินได้ไหม | ❌ **ไม่มีเลย** — ไม่มีหน้าแอดมิน ไม่มีคีย์ใน env/appsettings · และพบว่า **เงินค่าส่วนเสริมถูกเก็บผ่าน gateway ของร้านเอง** (ดู §2 · P0) |
| หน้าทดสอบ (ทดลองจ่าย) + เปิด/ปิด | ❌ ไม่มีหน้าทดลองจ่ายแยก ทั้งฝั่งแพลตฟอร์มและฝั่งร้าน — ฝั่งร้านมีแค่ป้าย "โหมดทดสอบ" บนหน้าจ่ายจริง · "ทดสอบเชื่อมต่อ" ตรวจแค่ `GET /account` + เคยมี webhook ไหม |
| นโยบายยกเลิกและคืนเงินของ **NextAcc** | ❌ ไม่มีเอกสารแยก — ข้อกำหนดการใช้งานข้อ 5 พูดเรื่องค่าบริการแต่**ไม่พูดเรื่องคืนเงิน/ยกเลิกเลย** · ระบบแอดมินแก้ได้เฉพาะ privacy/terms |
| นโยบายคืนเงินของ **ร้าน** | ⚠️ มีเป็นหัวข้อในแม่แบบข้อกำหนดของร้าน (ข้อ 5 "การจัดส่ง การคืนสินค้า และการคืนเงิน") · ที่พักมีนโยบายยกเลิกแบบโครงสร้าง + snapshot · ไม่มีหน้า "นโยบายคืนเงิน" แยกที่ร้านตั้งค่าได้ |
| หน้าก่อนชำระเงินแสดง policy ไหม | ⚠️ บางหน้า — ที่พักแสดงชื่อนโยบายยกเลิก · จองโต๊ะลิงก์นโยบาย/ข้อกำหนด · เช็กเอาต์ร้านมีแค่ช่องยินยอม PDPA · **หน้าจ่ายของแพลตฟอร์ม (อัปเกรด/ส่วนเสริม) ไม่แสดงอะไรเลย** |

---

## 1. สภาพปัจจุบัน (หลักฐาน file:line ที่ `c18ca0ce`)

### 1.1 Gateway

| เรื่อง | สถานะ | หลักฐาน |
| --- | --- | --- |
| ชั้นกลาง `PaymentIntent` + `IPaymentProvider` · Omise adapter (PromptPay/บัตร/mobile banking/TrueMoney) | ✅ | `Services/Payments/Providers/OmisePaymentProvider.cs` · `PAYMENT_GATEWAY_DESIGN.md` §7.1 เฟส 1–5 |
| คีย์ลับของร้านเข้ารหัสก่อนเก็บ · ไม่ส่งกลับ (แสดง 4 ตัวท้าย) | ✅ | `Controllers/PaymentSettingsController.cs:64-68` (Hint) · `:189-199` (Protect) |
| ตั้งค่า/ทดสอบ/สลับโหมด = เจ้าของบริษัทเท่านั้น | ✅ | `PaymentSettingsController.cs:165` `[HttpPut]` + `RequireOwner` · `:242` test · `:317` mode |
| ด่าน "ห้ามเปิด live ก่อนทดสอบผ่าน" | ✅ (ด่านอยู่) | `PaymentSettingsController.cs:330` (`LastTestPassedAt == null` ⇒ 400) · `:339` ประทับ `LiveEnabledAt` + audit hash chain |
| ทดสอบเชื่อมต่อ = เรียก Omise `GET /account` + ดูว่าเคยมี webhook | ⚠️ | `OmisePaymentProvider.cs:416-445` — **ทดสอบด้วยคีย์ของโหมดปัจจุบัน** (`SecretKey()` `:109-113`) ⇒ ตอนอยู่โหมดทดสอบ **คีย์ live ไม่เคยถูกตรวจ** ก่อนสลับ · "เคยมี webhook" นับ webhook ของโหมดทดสอบ ⇒ webhook ฝั่ง live (ตั้งแยกในแดชบอร์ด Omise — ต้องยืนยัน) ไม่เคยถูกพิสูจน์ |
| ใส่คีย์ live ใหม่ล้างผลทดสอบ | ⚠️ | `PaymentSettingsController.cs:196-199` ล้าง `LastTestPassedAt` แต่การทดสอบรอบใหม่ยังตรวจคีย์ **test** ⇒ ด่านผ่านโดยที่คีย์ live ยังไม่ได้ตรวจ |
| ขั้น "สร้าง charge ทดสอบ ฿20 แล้วรอ webhook" ตามแบบ §4.4(ข)(ค) | ❌ | แบบใน `PAYMENT_GATEWAY_DESIGN.md:281-284` · โค้ดไม่มี — ผู้ใช้ต้องไปหาทางจ่ายจริงเอง |
| ป้าย "โหมดทดสอบ — เงินไม่เข้าจริง" บนหน้าจ่ายของลูกค้า | ✅ | `wwwroot/js/pay-widget.js:139-141` · `storefront.html:1826` · `:2749` · `:3699` |
| การเข้ารหัสคีย์ fail-fast ใน production (กฎ #4 C) | ⚠️ ต้องตรวจ deploy | `Program.cs:125-141` fail-fast เฉพาะ `ENCRYPTION_KEY` (ใช้กับคอลัมน์ PII) · แต่ `Helpers/SecretProtector.cs:38-49` อ่าน `Security:EncryptionKey` **อย่างเดียว** ไม่มีโค้ดเขียนค่า env กลับเข้า config (`grep 'Security:EncryptionKey"] ='` = 0 จุด · `appsettings.json:46` ว่าง) ⇒ ถ้า production ตั้งแค่ `ENCRYPTION_KEY` คีย์ลับ Omise จะถูกเข้ารหัสด้วย **dev key ใน source** โดยมีแค่ LogWarning |
| **คีย์ Omise ของแพลตฟอร์ม** (env / appsettings / หน้าแอดมิน) | ❌ | `grep -i omise appsettings*.json` = 0 · `Program.cs:327-331` แค่ register adapter · `wwwroot/admin/*` ไม่มีหน้า gateway (`admin/payments.html` = ตรวจสลิปค่าแพ็กเกจ) |
| แนวคิด "tenant ผู้ให้บริการ" | ✅ มีแล้ว | `Models/Entities/SiteSettings.cs:298` `PlatformCompanyId` · ตั้งที่ `admin/site-settings.html:891-935` · ใช้ออกใบเสร็จแพลตฟอร์ม `PlatformBillingDocumentIssuer.cs:76,293-302` — **แต่ gateway ไม่ได้ใช้ค่านี้เลย** |

### 1.2 เงินค่าบริการของแพลตฟอร์มไหลผ่าน gateway ของใคร — **พบปัญหาร้ายแรง (P0)**

| เรื่อง | สถานะ | หลักฐาน |
| --- | --- | --- |
| ค่าส่วนเสริม "💳 ชำระออนไลน์" สร้าง intent ด้วย `companyId` ของ **ร้าน** | ❌ | `Controllers/MeteringController.cs:226-228` `intents.StartAsync(companyId, … AddOnPurchase …)` |
| `StartAsync` เลือก provider config ของ `companyId` นั้น | ❌ | `Services/Payments/PaymentIntentService.cs:224-228` `c.CompanyId == companyId` ⇒ **charge ถูกสร้างบนบัญชี Omise ของร้านเอง** — เงินเข้ากระเป๋าร้าน แต่ `AddOnPurchasePaymentHandler` เปิดสิทธิ์ส่วนเสริมให้ (`Handlers/AddOnPurchasePaymentHandler.cs:33-51`) |
| ปุ่ม "ชำระออนไลน์" โผล่เมื่อ **ร้าน** เปิด gateway | ❌ | `wwwroot/pages/addons.html:294-298` ถาม `/api/companies/{cid}/public-pay/methods` ของร้าน |
| โหมดทดสอบของร้านเปิดส่วนเสริมจริงได้ | ❌ | ไม่มีด่านโหมดใน `DispatchSucceededAsync` (`PaymentIntentService.cs:370-391`) และ `AddOnPurchaseService.MarkPaidByGatewayAsync` ⇒ ร้านตั้ง Omise โหมดทดสอบ จ่ายด้วยบัตรทดสอบ = ได้ส่วนเสริมฟรี |
| ค่าแพ็กเกจ (`SubscriptionPayment`) เริ่ม intent จากฝั่งร้านได้ | ❌ | `Controllers/PaymentGatewayController.cs:63-80` รับ `SourceKind` ใดก็ได้จาก body (ด่านแค่สิทธิ์ `Billing.Manage` — `Helpers/PaymentGatewayPermissionScope.cs:64-65`) · ยอดมาจาก body |
| "ยืนยันรับเงินด้วยมือ" ของร้าน ส่งต่อให้ handler ค่าแพ็กเกจ/ส่วนเสริม | ❌ | `PaymentGatewayController.cs:323-357` ไม่กันชนิดต้นทาง · เส้นสลิป `ManualSlipPaymentProvider` (`SettlesDirectlyToBank = true` `:37`) ⇒ `ManualConfirmBlockReason` คืน null (`Helpers/PaymentIntentPolicy.cs:110-114`) ⇒ **เจ้าของร้านที่มีสิทธิ์ Bank.Reconcile อนุมัติค่าแพ็กเกจของตัวเองได้** (handler เรียก `ReviewPaymentAsync(…, systemConfirmed: true)` — `Handlers/SubscriptionPaymentHandler.cs:72-77`) |
| handler ค่าแพ็กเกจไม่ตรวจว่ารายการเป็นของบริษัทเดียวกับ intent | ❌ | `SubscriptionPaymentHandler.cs:41-44` `Where(p => p.Id == intent.SourceId)` ไม่มี `CompanyId` (เทียบ `AddOnPurchasePaymentHandler.cs:34` ที่มี) |
| หน้าอัปเกรดแพ็กเกจมีขั้นจ่ายเงิน | ⚠️ | `wwwroot/pages/subscription.html:440-466` ยืนยันแล้ว `POST convert` ⇒ `SubscriptionService.ConvertTrialAsync` ตั้ง `Active` ทันที (`SubscriptionService.cs:395-399`) · ชำระภายหลังผ่านสลิป/ใบแจ้งต่ออายุ |

> ⚠️ ทั้งสายใน §1.2 ไล่จากโค้ด **ยังไม่ได้รันพิสูจน์** — ข้อแรกของ P0 คือเขียนเทสต์ reproduce (กฎ #4 G) ก่อนแก้
> ข้อความใน `PAYMENT_GATEWAY_DESIGN.md:538-540` ("ใช้ `PaymentProviderConfig` ของ tenant ผู้ให้บริการได้เลยโดยไม่ต้องมีโค้ดพิเศษ") **ผิด** —
> ไม่มีโค้ดใดส่งเส้นแพลตฟอร์มไปที่ `PlatformCompanyId` ต้องแก้ doc ในคอมมิตที่แก้โค้ด

### 1.3 นโยบายยกเลิก/คืนเงิน และการแสดงก่อนชำระ

| เรื่อง | สถานะ | หลักฐาน |
| --- | --- | --- |
| เอกสารกฎหมายของแพลตฟอร์มแก้/ออกฉบับได้จากแอดมิน (append-only · วันมีผล · ประกาศล่วงหน้า) | ✅ (privacy/terms เท่านั้น) | `Helpers/LegalDocumentPolicy.cs` · `Controllers/AdminLegalDocumentController.cs:54-138` · `wwwroot/admin/legal.html:45-46,118` |
| ชนิดเอกสาร = 2 ชนิดตายตัว | ❌ สำหรับ refund | `Helpers/LegalDocumentDefaults.cs:17-27` (`Kinds = {privacy, terms}` · `Title`/`PlatformPath`/`PlatformBuiltIn`/`ShopTemplate` เป็น ternary สองทาง) · `Helpers/CmsLegalPages.cs:18` (`Slug` ternary) · `CmsRenderingService.cs:137-139,153` (`isTerms ? … : privacy`) ⇒ **เพิ่มชนิดที่ 3 แบบไม่ระวัง = ตกไปเป็น privacy เงียบ ๆ** |
| ข้อกำหนดแพลตฟอร์มพูดถึงคืนเงิน/ยกเลิกแพ็กเกจ | ❌ | `LegalDocumentDefaults.cs:197-208` ข้อ 5–6 มีราคา/ทดลอง/แจ้งล่วงหน้า 30 วัน/เก็บข้อมูล — ไม่มี "คืนเงินได้ไหม · ยกเลิกแล้วใช้ได้ถึงเมื่อไร · ส่วนเสริม/แพ็กเอกสารคืนได้ไหม" |
| ยกเลิกแพ็กเกจ = ตัดทันที ไม่มีคืนตามสัดส่วน | ⚠️ (นโยบายต้องสะท้อน) | `SubscriptionService.cs:729-750` ตั้ง `Cancelled` ทันที ไม่มีถึงสิ้นรอบ/คืนเงิน |
| คืนเงินค่าแพ็กเกจ + ใบลดหนี้แพลตฟอร์ม | ❌ | `SubscriptionPaymentStatus` ไม่มี Refunded (`Models/Enums/AllEnums.cs:836-843`) · `PlatformBillingDocumentIssuer` ไม่มีเส้นใบลดหนี้ |
| ร้าน: privacy/terms ตั้งเอง/ลิงก์ภายนอก/แม่แบบ | ✅ | `Models/Entities/CmsSite.cs:77-90` · `Helpers/CmsLegalPages.cs:23-36` |
| ร้าน: หัวข้อคืนเงินในแม่แบบข้อกำหนด | ⚠️ | `LegalDocumentDefaults.cs:346-350` (ชำรุด/ไม่ตรง ภายใน 7 วัน) · ข้อ 3 การจองอ้าง "นโยบายยกเลิก/ค่าปรับที่แสดงตอนจอง" `:338` |
| ร้าน: ค่าตั้งเชิงตัวเลขที่มีอยู่แล้ว | ✅ (กระจาย) | ริบมัดจำออเดอร์ `CmsSite.cs:183` `DepositForfeitOnCancelPercent` · ที่พัก `LodgingCancellationPolicy` + snapshot ตอนจอง `Models/Entities/Lodging.cs:317,394-395` · จองโต๊ะ `PosReservation.cs:58` `FreeCancelHoursBefore` · คอร์ส `CourseCredit.cs:65` `FreeCancelHours` |
| เช็กเอาต์ร้าน (สินค้า) แสดงนโยบายคืนเงิน | ❌ | `storefront.html:1290-1311` มีขอใบกำกับ + `p6ConsentHtml()` (ช่องยินยอม PDPA `:3754-3762`) — ไม่มีลิงก์ข้อกำหนด/คืนเงิน |
| ที่พัก แสดงนโยบายยกเลิกก่อนจ่ายมัดจำ | ⚠️ | `storefront.html:3499` (ชื่อ + "ไม่คืนเงิน") · `:3272,3289` (หน้าห้อง) · ช่องติ๊กมีแค่กติกาที่พัก `:3484` · มี snapshot ตอนจอง = หลักฐานเนื้อหา แต่ไม่มีหลักฐานว่าลูกค้ากดยอมรับ |
| จองโต๊ะ (booking.html) | ✅ บางส่วน | `booking.html:133-134` กล่องนโยบายการจอง · `:150-153` ลิงก์นโยบาย/ข้อกำหนดของร้าน "การจองถือว่ารับทราบ" |
| จองบริการ CMS แสดงนโยบายยกเลิก | ⚠️ | `storefront.html:2696` แสดง `cancelPolicyNote` บนหน้ารายละเอียดการจอง (หลังจอง) |
| หน้าจ่ายแพลตฟอร์ม (ส่วนเสริม · อัปเกรด) แสดง policy | ❌ | `addons.html:362-375` · `subscription.html:440-453` ไม่มีลิงก์ข้อกำหนด/คืนเงิน |
| ตารางหลักฐานการยอมรับ | ✅ (มีให้ reuse) | ผู้ใช้แพลตฟอร์ม: `Models/Entities/PlatformPolicyAcceptance.cs` + `Helpers/PlatformPolicyAcceptanceRules.cs` (hash `PdpaConsentEvidence.ComputeHash`) · ลูกค้าร้าน: `Helpers/CmsDataConsent.cs:72-89` (`PdpaConsentRecord`) |

---

## 2. P0 — ปิดรูก่อนทำของใหม่: เงินของแพลตฟอร์มต้องเข้าบัญชีของแพลตฟอร์มเท่านั้น

ตัวตัดสินเดียว **`Helpers/PlatformRevenueSource.cs`** (pure):
```
IsPlatformRevenue(PaymentSourceKind k) => k is SubscriptionPayment or AddOnPurchase   // + แพ็กเอกสาร/แพ็กแจ้งเตือนในอนาคต
```
ด่านที่ต้องต่อสาย (ล็อกด้วย `tools/required_call_site_check.py`):

1. `PaymentGatewayController.Create` / `PublicPaymentController` / `confirm-manually` / `redispatch` / `refund` ของร้าน — ถ้า `IsPlatformRevenue(kind)` ⇒ 400
   "รายการค่าบริการ NextAcc ชำระผ่านหน้าค่าบริการเท่านั้น" (ร้านไม่มีสิทธิ์ยืนยัน/คืนเงินของแพลตฟอร์ม)
2. `SubscriptionPaymentHandler` — เพิ่ม `p.CompanyId == intent.PayerCompanyId` (ดูข้อ 3) + ปฏิเสธเมื่อ intent ไม่ได้ผูก config ของแพลตฟอร์ม
3. เส้นแพลตฟอร์มสร้าง intent ผ่าน **`IPlatformPaymentService.StartAsync(payerCompanyId, kind, sourceId, method)`** ตัวเดียว:
   `intent.CompanyId = PlatformCompanyId` (เงิน/บัญชีพัก/รอบโอน/ประวัติ อยู่ในสมุดของผู้ให้บริการ) · คอลัมน์ใหม่ `PaymentIntent.PayerCompanyId`
   (บริษัทที่จ่าย — ใช้ตรวจสิทธิ์ poll สถานะและให้ handler หาแถวต้นทาง) · ยอดคำนวณที่เซิร์ฟเวอร์เสมอ (ไม่รับจาก body)
   — **ไม่ใช่** ให้ intent อยู่ใต้ร้านแต่ชี้ config ข้ามบริษัท (ร้านจะเห็นปุ่มคืนเงินที่ดึงเงินจากบัญชีแพลตฟอร์ม)
4. ไม่ได้ตั้ง `PlatformCompanyId` หรือแพลตฟอร์มยังไม่มี config ที่ active ⇒ ไม่มีปุ่มชำระออนไลน์ (เหลือแนบสลิป — พฤติกรรมเดิมของร้านที่ไม่เปิด gateway)
   หน้าเว็บถาม `GET /api/platform-pay/methods` แทน `public-pay/methods` ของร้าน (`addons.html:296`)
5. **intent โหมดทดสอบของแพลตฟอร์มห้ามให้สิทธิ์จริง** — handler ตรวจ `config.Mode`: Test ⇒ บันทึก "ทดสอบสำเร็จ" ในประวัติ ไม่เปิดสิทธิ์/ไม่ต่ออายุ
   ยกเว้นบริษัทที่แอดมินติ๊กเป็น "บริษัททดสอบ" (ดู §3.4)
6. Migration: ตรวจ `PaymentIntents` ที่ `SourceKind ∈ {SubscriptionPayment, AddOnPurchase}` และ `CompanyId ≠ PlatformCompanyId` ที่ Succeeded แล้ว ⇒
   รายงานอ่านอย่างเดียวในแอดมิน (เงินอาจเข้ากระเป๋าร้าน/เป็นโหมดทดสอบ) ให้คนตัดสิน — ห้ามย้อนสิทธิ์อัตโนมัติ (F2 ข้อ 9)
7. เทสต์ทิศตรงข้าม: ร้านเริ่ม intent ชนิดอื่น (ออเดอร์/จอง/ใบแจ้งหนี้) ยังทำงานเหมือนเดิม · ร้านที่เป็น `PlatformCompanyId` เองยังเห็นรายการของตัวเองในหน้า payment-intents
8. `SecretProtector` — production ต้อง throw เมื่อคีย์ว่าง/เป็น dev key และรับ `ENCRYPTION_KEY` เป็นแหล่งเดียวกับ `Program.cs:125` (ต้องย้ายคีย์เดิม: ค่าที่เคยเข้ารหัสด้วย dev key ⇒ ถอดด้วย dev key แล้วเข้ารหัสใหม่ใน migration ครั้งเดียว · `IsUsable` เป็นตาข่าย)

---

## 3. ออกแบบ: หน้าแอดมิน "ช่องทางรับชำระของ NextAcc"

### 3.1 เก็บที่ไหน — reuse ไม่ทำสำเนา
- ใช้ `PaymentProviderConfig` **ของ `PlatformCompanyId`** (ตาราง/การเข้ารหัส/`WebhookToken`/`GatewayWebhookRoute`/ด่าน live เดิมทั้งหมด) — ไม่สร้างตาราง config ที่สอง
- แยก logic ของ `PaymentSettingsController` (Upsert/Test/SetMode/RotateToken) ออกเป็น **`IPaymentProviderConfigService`** ตัวเดียว ⇒ controller ของร้าน (`RequireOwner`)
  และ controller แอดมินใหม่ `AdminPlatformGatewayController` (`[Authorize(Roles=SystemAdmin)]` + `RejectApiKey`) เรียกตัวเดียวกัน (กฎ #4 A ห้าม drift)
- ยังไม่ได้ตั้ง `PlatformCompanyId` ⇒ หน้าแอดมินแสดงขั้นแรก "เลือกบริษัทผู้ให้บริการ" (ลิงก์ไป `site-settings.html` ส่วนออกใบเสร็จแพลตฟอร์ม)

### 3.2 หน้า `admin/platform-gateway.html`
| ส่วน | รายละเอียด |
| --- | --- |
| สถานะ | ป้ายใหญ่ ปิด / **โหมดทดสอบ** (เหลือง) / **ใช้งานจริง** (เขียว) · บริษัทผู้ให้บริการ · ทดสอบผ่านล่าสุด · webhook ล่าสุด (แยก test/live) · เปิด live เมื่อ/โดย |
| คีย์ | public/secret ของ test และ live · secret เข้ารหัส (`ISecretProtector`) · แสดง 4 ตัวท้าย · เว้นว่าง = ใช้ค่าเดิม |
| วิธีชำระที่เปิด | จาก `IPaymentProvider.Capabilities` (ห้าม hard-code ใน JS) |
| URL webhook | URL มีรหัสลับ + ปุ่มคัดลอก + "หมุนรหัส" (`RotateWebhookToken` เดิม) · คำเตือนว่าต้องตั้งในแดชบอร์ด Omise **ทั้งฝั่ง test และ live** (ต้องยืนยันกับเอกสาร Omise ว่า endpoint ตั้งแยกต่อโหมด) |
| ทดสอบการเชื่อมต่อ | (ก) `GET /account` ด้วยคีย์ **ทั้ง test และ live** แยกผล (อ่านอย่างเดียว ไม่สร้าง charge บน live) · (ข) ตรวจว่า `livemode` ของบัญชีตรงกับโหมดคีย์ (ต้องยืนยันชื่อช่องใน API รุ่น `2019-05-29`) · (ค) ปุ่ม "ทดลองจ่าย ฿20" สร้าง charge PromptPay **โหมดทดสอบ** แล้วรอ webhook ภายใน 2 นาที ⇒ ผ่านครบ = ประทับ `LastTestPassedAt` |
| เปิดใช้งานจริง | ปุ่มล็อกจนกว่า: ทดสอบ (ก)(ข)(ค) ผ่าน **หลังการแก้คีย์ครั้งล่าสุด** + คีย์ live ผ่าน `/account` + เผยแพร่นโยบายคืนเงินแพลตฟอร์มแล้ว (§4.1 — เงื่อนไขของ Omise ต้องยืนยัน) · กดแล้วต้องพิมพ์ "เปิดใช้จริง" ยืนยัน · audit hash chain + แจ้งอีเมลแอดมินทุกคน |
| หลังเปิด live | แถบเตือน "ยังไม่เคยได้รับ webhook โหมดจริง" จนกว่าจะมี webhook live ครั้งแรก (`LastWebhookAt` แยกโหมด — คอลัมน์ใหม่ `LastLiveWebhookAt`) |
| ปิดฉุกเฉิน | ปุ่ม "หยุดรับชำระออนไลน์" (`IsActive=false`) — หน้าเว็บของลูกค้ากลับไปเส้นแนบสลิปทันที ไม่ error (กฎ #1 ข้อ 4 แนวเดียวกัน) |

### 3.3 แก้ด่านเดิมของร้านไปพร้อมกัน (service ตัวเดียว ⇒ ร้านได้ด้วย)
- `CanEnableLive` ต้องรวม "คีย์ live ผ่าน `/account`" (เดิมตรวจแค่ไม่ว่าง — `PaymentSettingsController.cs:91-93`)
- ทดสอบ = ตรวจทั้งสองชุดคีย์ไม่ขึ้นกับโหมดปัจจุบัน (เพิ่มพารามิเตอร์ mode ใน `TestConnectionAsync` · `IPaymentProvider.cs:174`)
- เพิ่ม "ทดลองจ่าย ฿20 (โหมดทดสอบ)" ให้ร้านด้วย — source ชนิดใหม่ `PaymentSourceKind.GatewaySelfTest` + handler no-op (ไม่ลงบัญชี ไม่แตะเอกสาร)

### 3.4 "หน้าการทดสอบ" — เปิด/ปิด
- หน้า `/platform-pay-test.html` (ทดลองซื้อส่วนเสริมราคา ฿20 ที่ไม่มีจริง · ใช้ `PayWidget` ตัวเดิม · source `GatewaySelfTest`)
- ค่าตั้งใน `SiteSettings`: `PlatformPayTestPageMode ∈ {Off, AdminsOnly, Public}` (ค่าเริ่ม `AdminsOnly`)
  - `AdminsOnly` = เข้าได้เฉพาะ SystemAdmin ที่ล็อกอิน · `Public` = เปิดให้ผู้ตรวจของ Omise/เดโม · `Off` = 404
  - **บังคับ: หน้าทดสอบใช้คีย์โหมดทดสอบเสมอ** แม้ config อยู่โหมด live (ไม่มีเงินจริงไหลผ่านหน้าทดสอบ) — ถ้าไม่มีคีย์ test = หน้าแจ้ง "ยังไม่ได้ตั้งคีย์ทดสอบ"
- "บริษัททดสอบ" (`Company.IsPlatformSandbox` ตั้งได้เฉพาะแอดมิน) — intent โหมดทดสอบของบริษัทนี้ให้สิทธิ์ได้ (ทดสอบ end-to-end ทั้งสาย ส่วนเสริม→เปิดสิทธิ์) · บริษัทอื่น = ไม่ให้สิทธิ์ (§2 ข้อ 5)
- เปลี่ยนโหมดหน้า = audit

---

## 4. ออกแบบ: นโยบายยกเลิกและคืนเงิน

### 4.1 แพลตฟอร์ม — เอกสารชนิดที่ 3 `refund` บนโครงสร้างรอบ 214 (reuse ไม่ลอก)
- `LegalDocumentDefaults`: `KindRefund = "refund"` · `Kinds = {privacy, terms, refund}` · **แปลง ternary ทุกตัวเป็น `switch` ที่ throw เมื่อไม่รู้จัก**
  (`Title`/`PlatformPath`/`PlatformBuiltIn`/`ShopTemplate`/`CmsLegalPages.Slug`/`CmsRenderingService` `isTerms`) — กันชนิดใหม่ตกเป็น privacy เงียบ ๆ ·
  เทสต์ "ทุก kind ใน `Kinds` ได้ Title/Path/Slug ไม่ซ้ำกัน"
- **แยก `ConsentKinds = {privacy, terms}`** — `LegalDocumentPolicy.ConsentVersion` และแถบ "นโยบายฉบับใหม่" (`LegalDocumentService.cs:78-79,265-266`) ยังใช้แค่ 2 ชนิด
  (นโยบายคืนเงินเป็นเงื่อนไขการซื้อ ไม่ใช่ความยินยอม PDPA — การยอมรับไปผูกกับการซื้อแต่ละครั้ง §4.3) · ออกฉบับใหม่ของ refund ⇒ แจ้งอีเมลผู้ใช้ที่มีแพ็กเกจจ่ายเงิน (ไม่บล็อก)
- หน้า public `/refund-policy.html` (ลอกโครง `terms.html` · เนื้อจาก `GET /api/legal/documents/refund`) · ลิงก์ใน footer ของ `index.html`/`register.html`/`terms.html`/`privacy.html`
- `admin/legal.html`: เพิ่มปุ่มแท็บที่สาม (รายการปุ่มมาจาก API `kinds` ไม่ hard-code — แก้ `:118` ที่เช็ก `'terms'||'privacy'`)
- เนื้อฉบับตั้งต้น 1.0 (ร่างให้นักกฎหมายตรวจ) — ต้องตรงกับพฤติกรรมระบบจริง:
  1. ทดลองใช้ฟรี — ไม่มีการเก็บเงิน
  2. แพ็กเกจรายเดือน/รายปี — ยกเลิกได้ทุกเมื่อ · **ต้องตัดสิน**: ใช้ได้ถึงสิ้นรอบ (ต้องแก้ `CancelSubscriptionAsync` ที่ตัดทันที `SubscriptionService.cs:737-738`) หรือคืนตามสัดส่วน
  3. ส่วนเสริม/แพ็กเอกสาร/แพ็กแจ้งเตือนที่ใช้ไปแล้ว — ไม่คืน · ที่ยังไม่ใช้ภายใน N วัน — คืนได้ (ต้องตัดสิน N)
  4. เรียกเก็บซ้ำ/ผิดพลาดของระบบ — คืนเต็มเสมอ
  5. ช่องทางคืน = ช่องทางเดิม (บัตร → คืนเข้าบัตรผ่าน Omise · PromptPay/โอน → โอนคืนบัญชีที่แจ้ง) · ระยะเวลาดำเนินการ · ใบลดหนี้ออกให้ทุกครั้ง
  6. ช่องทางติดต่อ/ร้องเรียน (token ชุดเดิม `LegalDocumentDefaults.PlatformTokens`)

### 4.2 ร้าน — นโยบายคืนเงินต่อเว็บ (reuse `CmsLegalPages`)
- `CmsSite.RefundPolicyUrl` / `RefundPolicyMarkdown` / `RefundPolicyUpdatedAt` · ลำดับเดิม ลิงก์ภายนอก > ร้านเขียนเอง > แม่แบบ (`CmsLegalPages.Resolve` ไม่เปลี่ยน)
- slug `legal/refund` · `shop-policy.html` แท็บที่สาม (`&k=refund`) สำหรับจองโต๊ะ
- **แม่แบบอ่านค่าตั้งเชิงตัวเลขที่มีอยู่แล้วเป็น token** (ห้ามให้ร้านพิมพ์ตัวเลขซ้ำ = สองความจริง):
  `{{shop.depositForfeitPercent}}` (`CmsSite.DepositForfeitOnCancelPercent`) · `{{shop.freeCancelHours}}` (จองโต๊ะ) · รายการนโยบายยกเลิกที่พัก
  (`LodgingCancellationPolicy` — แสดงเป็นตารางจาก `cancellationSummary` เดิม) · ค่าส่งไม่คืนหลังจัดส่ง (คำตัดสินข้อ 168 ฉ · `SiteOrderRefundPlan`)
  ⇒ ร้านที่เปลี่ยนค่าตั้งแล้วหน้านโยบายเปลี่ยนตาม · ร้านที่เขียนเองแล้วตัวเลขขัดกับค่าตั้ง ⇒ คำเตือนในหน้าแก้ (ตัวตรวจ pure ใน `CmsLegalPages`)
- ย้ายหัวข้อ 5 ในแม่แบบข้อกำหนดของร้าน (`LegalDocumentDefaults.cs:346-350`) เป็นประโยคเดียวชี้ไปหน้านโยบายคืนเงิน (กันสองฉบับขัดกัน)
- หน้าแก้ใน `pages/cms-edit.html` ส่วนเดียวกับนโยบาย/ข้อกำหนดเดิม (+พรีวิวผ่าน `PreviewLegalPageAsync`)

### 4.3 แสดงก่อนชำระ + เก็บหลักฐานการยอมรับ

หลัก: **ลิงก์ต้องอยู่บนหน้าเดียวกับปุ่มจ่าย** · ติ๊กยอมรับเฉพาะจุดที่มีเงื่อนไขไม่คืนเงิน/ค่าปรับ/การต่ออายุอัตโนมัติ (ภาระพิสูจน์ของผู้ขาย) ·
หลักฐาน = **ฉบับ/hash ของนโยบายที่แสดง ผูกกับรายการซื้อ** (ไม่ใช่ตาราง consent PDPA)

| หน้า | ตอนนี้ | ต้องทำ | ติ๊กยอมรับ | เก็บหลักฐานที่ |
| --- | --- | --- | --- | --- |
| อัปเกรด/ต่ออายุแพ็กเกจ `pages/subscription.html:440-466` | ❌ | ลิงก์ข้อกำหนด + นโยบายคืนเงิน + สรุป 1 บรรทัด (ยกเลิกแล้วเป็นอย่างไร) | ✅ บังคับ | `SubscriptionPayment.RefundPolicyVersion` + `PolicyAcceptedAt` + `PolicyEvidenceHash` |
| ซื้อส่วนเสริม `pages/addons.html:362-375` | ❌ | ลิงก์ + "ส่วนเสริมที่เปิดใช้แล้วไม่คืนเงิน" (ถ้าตัดสินเช่นนั้น) | ✅ บังคับ | `CompanyFeature` / แถวซื้อแพ็ก (เพิ่ม 3 ช่องเดียวกัน) |
| ซื้อแพ็กเอกสาร/แพ็กแจ้งเตือน (`MeteringController` top-up) | ❌ | เหมือนส่วนเสริม | ✅ | แถวซื้อแพ็ก |
| หน้าทดสอบแพลตฟอร์ม §3.4 | — | ลิงก์อย่างเดียว | — | — |
| เช็กเอาต์สินค้าร้าน `storefront.html:1290-1311` | ❌ | ลิงก์ข้อกำหนด + นโยบายคืนเงินของร้านเหนือปุ่ม "ยืนยันสั่งซื้อ" | ⚠️ เฉพาะเมื่อตะกร้ามีสินค้าตามสั่ง/มัดจำริบได้ (`DepositForfeitOnCancelPercent>0`) — เซิร์ฟเวอร์บอกใน quote ว่าต้องติ๊กไหม | `SiteOrder.RefundPolicyHash` + `PolicyAcceptedAt` |
| จองบริการ CMS (มัดจำ) `storefront.html:2477` ฟอร์มจอง | ⚠️ แสดงหลังจอง `:2696` | แสดงนโยบายยกเลิก (`cancelPolicyNote`) **ก่อน**ส่งฟอร์ม + ลิงก์นโยบายคืนเงิน | ✅ เมื่อมีมัดจำ | การจอง (ช่องเดียวกัน) |
| ที่พัก `storefront.html:3484-3499` | ⚠️ ชื่อนโยบาย | ขยายเป็นข้อความเต็มจาก snapshot + ลิงก์ | ✅ เมื่อ `nonRefundable` หรือมีค่าปรับ | `LodgingReservation` (มี `CancellationPolicySnapshotJson` แล้ว `Lodging.cs:395` — เพิ่มแค่ `PolicyAcceptedAt`) |
| หน้าจ่ายจาก token (ออเดอร์ `:1825` · การจอง `:2749` · ที่พัก `:3699`) | ❌ | ลิงก์นโยบายคืนเงินใต้ปุ่มจ่าย (ยอมรับไปแล้วตอนสั่ง/จอง — ไม่ติ๊กซ้ำ) | — | — |
| จองโต๊ะ `booking.html:150-153` | ✅ ลิงก์ privacy/terms | เพิ่มลิงก์ `&k=refund` เมื่อมีมัดจำ | ⚠️ เมื่อมีมัดจำ | `PosReservation` |
| portal ใบแจ้งหนี้ (`portal.html`) | ❌ | ลิงก์นโยบายคืนเงินของบริษัท (ถ้าบริษัทมีเว็บ CMS — ใช้ของเว็บหลัก) | — | — |

- ข้อความ/ลิงก์/"ต้องติ๊กไหม" มาจากเซิร์ฟเวอร์ใน DTO ที่หน้านั้นอ่านอยู่แล้ว (quote/info) — JS ห้ามตัดสินเอง (F2 ข้อ 5) · ด่านจริงที่เซิร์ฟเวอร์ (ไม่ติ๊ก = 400 พร้อมข้อความ)
- hash หลักฐาน = `PdpaConsentEvidence.ComputeHash` ตัวเดิม (canonical ตัวเดียว · กฎ #4 C) ป้อน (subject=อีเมล/โทร, purpose=`refund-policy`, version=ฉบับหรือ sha256 ของ markdown ที่ใช้จริง, at, channel)
- นโยบายของร้านไม่มีเลขฉบับ ⇒ ใช้ sha256 ของ markdown ที่เรนเดอร์แล้ว + `RefundPolicyUpdatedAt` (พิสูจน์ได้ว่าเห็นข้อความใดโดยไม่ต้องสร้างตารางฉบับของร้าน) — เก็บข้อความเต็มเฉพาะเมื่อร้านเปลี่ยนนโยบาย (ตาราง `CmsLegalSnapshot` append-only · P2)
- คงเก็บตามอายุเอกสารบัญชี (5 ปี · MAX retention · กฎ M)

### 4.4 กลไกคืนเงิน + ใบลดหนี้ (อ้าง helper เดิม)
- **ฝั่งร้าน** (มีแล้ว — ไม่ต้องสร้างใหม่): `GatewayRefundService` + `Helpers/GatewayRefundMath` (เรียก Omise `POST /charges/{id}/refunds` · 5xx/408 = ผลไม่แน่ชัด ล็อกคืนซ้ำ · JE Dr ลูกหนี้ / Cr 11340) ·
  ใบลดหนี้ร่างจาก `Helpers/SiteOrderRefundPlan` (Σ ใบลดหนี้ ≤ ใบเดิม §86/10 · ค่าส่งหลังจัดส่งไม่คืน) · มัดจำที่พัก `DepositRefundCreditNoteBookingMigration`/`LodgingFolioOnCancel` ·
  ค่าปรับยกเลิก `BookingCancellationFee`/`CmsBookingCancelPolicy` — งานรอบนี้มีแค่ **ให้หน้านโยบายอ่านตัวเลขจาก helper เหล่านี้**
- **ฝั่งแพลตฟอร์ม** (ยังไม่มี):
  1. `SubscriptionPaymentStatus.Refunded/PartiallyRefunded` + ปุ่ม "คืนเงิน" ใน `admin/payments.html` (เหตุผลบังคับ · ยอด ≤ ยอดรับ − คืนแล้ว)
  2. เงินออก: intent ของแพลตฟอร์มอยู่ใต้ `PlatformCompanyId` (§2 ข้อ 3) ⇒ เรียก `GatewayRefundService` ตัวเดิมได้เลย (ไม่เขียนเส้นคืนเงินที่สอง) · ชำระด้วยสลิป ⇒ บันทึกการโอนคืนด้วยมือ + แนบหลักฐาน
  3. เอกสาร: `PlatformBillingDocumentIssuer.IssueRefundCreditNoteAsync` ออก**ใบลดหนี้ใน tenant ผู้ให้บริการ**ผ่าน `IDocumentService` เดิม (อ้างใบกำกับเดิม · `Reason` จาก enum ปิด · ผลต่าง + VAT ผลต่าง) —
     ด่าน §86/10 เดิมของ `DocumentService` ทำงานเอง · ต้องตัดสิน: คืนเงินลดวันใช้งาน/ระงับส่วนเสริมทันทีไหม
  4. ไม่ได้ตั้ง `PlatformCompanyId` ⇒ บันทึกสถานะคืนเงินได้ แต่แจ้ง "ยังไม่ได้ออกใบลดหนี้ (ยังไม่ตั้งบริษัทผู้ให้บริการ)" ดัง ๆ ในหน้ารายการ (ห้ามเงียบ)

### 4.5 หมายเหตุกฎหมาย/ผู้ให้บริการ (ต้องยืนยันทุกข้อ)
- **พ.ร.บ.คุ้มครองผู้บริโภค 2522** (สคบ.) — ข้อความโฆษณา/เงื่อนไขต้องชัดเจนไม่ทำให้เข้าใจผิด · ข้อสัญญาที่ไม่เป็นธรรม (พ.ร.บ.ว่าด้วยข้อสัญญาที่ไม่เป็นธรรม 2540) เช่น "ไม่คืนเงินทุกกรณี" อาจใช้บังคับไม่ได้เต็มที่ — ต้องให้นักกฎหมายตรวจแม่แบบของร้าน
- **พ.ร.บ.ขายตรงและตลาดแบบตรง 2545** — การขายออนไลน์อาจเข้าข่าย "ตลาดแบบตรง" (จดทะเบียน · สิทธิเลิกสัญญา/คืนเงินตามกำหนดเวลา) — ร้านแต่ละรายรับผิดชอบเอง · แม่แบบต้องไม่ตัดสิทธิตามกฎหมาย (มีประโยค "สิทธิตามกฎหมายคุ้มครองผู้บริโภคไม่ถูกจำกัด" อยู่แล้ว `LegalDocumentDefaults.cs:350`)
- **NextAcc ในฐานะแพลตฟอร์มที่ร้านใช้ขาย** — ตรวจว่าเข้าข่าย พ.ร.ฎ.ธุรกิจบริการแพลตฟอร์มดิจิทัล 2565 (ETDA) หรือไม่ (เราเป็นผู้ให้ซอฟต์แวร์ ไม่ใช่ตลาดกลาง — ต้องยืนยัน)
- **พ.ร.บ.ระบบการชำระเงิน 2560** — คงหลักเดิม (`PAYMENT_GATEWAY_DESIGN.md` §8 ข้อ 1): เงินของลูกค้าร้านเข้าบัญชี Omise ของร้านเอง · บัญชี Omise ของ NextAcc รับเฉพาะค่าบริการของ NextAcc — §2 คือการบังคับหลักนี้ในโค้ด
- **ข้อกำหนดของ Omise สำหรับร้านค้า** (จากความรู้ทั่วไป — ต้องยืนยันกับเอกสาร Omise/สัญญา): เว็บไซต์ต้องแสดงนโยบายคืนเงิน/ยกเลิก · ข้อกำหนด · ข้อมูลติดต่อ · ราคาสินค้า/บริการ ก่อนผ่านการตรวจเปิดบัญชี live ⇒ ด่านเปิด live ของแพลตฟอร์มผูกกับ "เผยแพร่นโยบายคืนเงินแล้ว" (§3.2) และหน้าตั้งค่า gateway ของร้านเตือนเมื่อเว็บร้านยังใช้แม่แบบที่ข้อมูลร้านไม่ครบ (`CmsLegalPages.IdentityGaps`)
- **บัตร**: คืนเงินผ่าน Omise เข้าบัตรเดิม (ระยะเวลาขึ้นกับธนาคารผู้ออกบัตร · ค่าธรรมเนียมเดิมอาจไม่คืน — ต้องยืนยัน) · คืนบางส่วนได้ · **PromptPay/mobile banking อาจคืนผ่าน API ไม่ได้** ⇒ นโยบายต้องเขียนว่าคืนด้วยการโอน (adapter ประกาศ `SupportsRefund` ต่อวิธีอยู่แล้ว — ใช้ค่านั้นแสดงในนโยบาย ไม่พิมพ์ซ้ำ)
- **§86/10** คืนเงินหลังออกใบกำกับ = ใบลดหนี้เสมอ (ทั้งร้านและแพลตฟอร์ม) · ใบลดหนี้ต้องออกในเดือนภาษีเดียวกับเหตุ/เดือนถัดไป (เกิน ⇒ `LateReason`)

---

## 5. แผน (P0/P1/P2)

| # | งาน | ขนาด | ไฟล์หลัก |
| --- | --- | --- | --- |
| **P0-1** | เทสต์ reproduce: ร้านซื้อส่วนเสริมผ่าน gateway ตัวเอง (รวมโหมดทดสอบ) · ร้านยืนยันมือค่าแพ็กเกจตัวเอง · handler แพ็กเกจข้ามบริษัท | S | `Accounting.Tests/Payments/*` |
| **P0-2** | `Helpers/PlatformRevenueSource` + ด่านใน Create/confirm-manually/redispatch/refund/PublicPayment + `CompanyId` ใน `SubscriptionPaymentHandler` + checker call-site | S–M | `PaymentGatewayController.cs` · `PublicPaymentController.cs` · `Handlers/SubscriptionPaymentHandler.cs` · `tools/required_call_site_check.py` |
| **P0-3** | ปิดปุ่ม "ชำระออนไลน์" ของส่วนเสริมจนกว่า P1-1 เสร็จ (เหลือแนบสลิป) · รายงาน intent ค่าบริการที่เคยไหลผ่านร้าน | S | `addons.html:296` · `MeteringController.cs:200-240` · แอดมินรายงาน |
| **P0-4** | `SecretProtector` fail-fast production + แหล่งคีย์เดียวกับ `ENCRYPTION_KEY` + migration เข้ารหัสใหม่ | S | `Helpers/SecretProtector.cs` · `Program.cs` |
| **P1-1** | `IPaymentProviderConfigService` (ยกจาก controller) · `PaymentIntent.PayerCompanyId` · `IPlatformPaymentService` · `GET /api/platform-pay/methods` · handler ตรวจโหมด/บริษัททดสอบ | M–L | `PaymentSettingsController.cs` · `PaymentIntentService.cs` · `Models/Entities/Payments.cs` · `DatabaseMigrationHelper.cs` · `MeteringController.cs` · handlers |
| **P1-2** | `admin/platform-gateway.html` + `AdminPlatformGatewayController` (คีย์ · ทดสอบสองชุด · ทดลองจ่าย ฿20 · live ยืนยันพิมพ์ · webhook แยกโหมด · ปิดฉุกเฉิน · audit) | M | ใหม่ 2 ไฟล์ · `admin-layout.js` (เมนู) · `OmisePaymentProvider.TestConnectionAsync` |
| **P1-3** | ด่าน live ร้านเข้มขึ้น (คีย์ live ผ่าน `/account` · ทดสอบสองชุด) + "ทดลองจ่าย ฿20" ฝั่งร้าน (`GatewaySelfTest`) | M | `PaymentSettingsController.cs` · `pages/payment-settings.html` · `IPaymentProvider.cs` |
| **P1-4** | legal kind `refund` (switch ทุกจุด · `ConsentKinds` แยก) · `/refund-policy.html` · แท็บแอดมิน · ร่างฉบับ 1.0 (รอคำตัดสิน §6) | M | `LegalDocumentDefaults.cs` · `LegalDocumentPolicy.cs` · `LegalDocumentService.cs` · `LegalController.cs` · `AdminLegalDocumentController.cs` · `admin/legal.html` · `wwwroot/refund-policy.html` |
| **P1-5** | ลิงก์ + ติ๊กยอมรับบนหน้าจ่ายแพลตฟอร์ม (อัปเกรด/ส่วนเสริม/แพ็ก) + 3 ช่องหลักฐาน + ด่านเซิร์ฟเวอร์ | M | `subscription.html` · `addons.html` · `SubscriptionController.cs` · `MeteringController.cs` · entities + migration |
| **P2-1** | นโยบายคืนเงินของร้าน (`CmsSite.RefundPolicy*` · slug `legal/refund` · แม่แบบอ่านค่าตั้งตัวเลข · ตัวเตือนตัวเลขขัด · แท็บ shop-policy) | M | `CmsSite.cs` · `CmsLegalPages.cs` · `CmsRenderingService.cs` · `CmsSiteService.cs` · `cms-edit.html` · `shop-policy.html` |
| **P2-2** | ลิงก์/ติ๊กบน storefront (เช็กเอาต์ · จองบริการ · ที่พัก · หน้าจ่ายจาก token) + จองโต๊ะ + หลักฐานต่อรายการ | M | `storefront.html` · `booking.html` · `CmsCommerceService` · `LodgingService` · DTO quote/info |
| **P2-3** | คืนเงินค่าบริการแพลตฟอร์ม (สถานะ · ปุ่มแอดมิน · `GatewayRefundService` เดิม · ใบลดหนี้ใน tenant ผู้ให้บริการ) | M–L | `admin/payments.html` · `AdminController.cs` · `PlatformBillingDocumentIssuer.cs` · `AllEnums.cs` |
| **P2-4** | หน้าทดสอบ `/platform-pay-test.html` + `PlatformPayTestPageMode` + `Company.IsPlatformSandbox` | S–M | ใหม่ · `SiteSettings.cs` · `admin/site-settings.html` |

ลำดับพึ่งพา: P0 ทั้งหมดก่อน → P1-1 → (P1-2, P1-3 ขนานได้) → P1-4 → P1-5 → P2 ·
doc ที่ต้องขยับพร้อมโค้ด: `PAYMENT_GATEWAY_DESIGN.md` (§0 · §4.4 · §8 ข้อ 3 ที่ผิด) · `ACCOUNT_STRUCTURE.md` §6.1 (เส้นเงินแพลตฟอร์ม) · `DOCUMENT_FLOW.md` (ใบลดหนี้แพลตฟอร์ม) · `TEST_PLAN.md`

---

## 6. คำถามที่เจ้าของต้องตัดสิน
1. ยกเลิกแพ็กเกจ: **ใช้ได้ถึงสิ้นรอบที่จ่ายแล้ว** (แนะนำ — แก้ `CancelSubscriptionAsync`) หรือคืนเงินตามสัดส่วน
2. ส่วนเสริม/แพ็กเอกสาร: ไม่คืนเมื่อเปิดใช้แล้ว · คืนได้ภายใน N วันถ้ายังไม่ใช้ (N = ?)
3. หน้าทดสอบแพลตฟอร์มค่าเริ่ม `AdminsOnly` ใช่ไหม (เปิด `Public` เฉพาะช่วงที่ Omise ตรวจเว็บ)
4. เช็กเอาต์สินค้าทั่วไปของร้าน: แสดงลิงก์อย่างเดียว (แนะนำ) หรือบังคับติ๊กทุกออเดอร์
5. ระหว่างรอ P1-1: ปิดปุ่ม "ชำระออนไลน์" ของส่วนเสริมทันที (แนะนำ · P0-3) ได้ไหม

---

## 7. สิ่งที่ทำจริงในรอบ 219 ส่วน B (คำตัดสินข้อ 179 · ยังไม่ได้คอมไพล์ในเครื่องนี้)

คำตัดสินเจ้าของ: ไม่คืนเงินค่าบริการ · ยกเลิก = ใช้ถึงสิ้นรอบ · ติ๊กยอมรับนโยบายบังคับทุกหน้าชำระ (แพลตฟอร์ม + ร้าน) พร้อมเก็บฉบับ · ยังไม่ตั้ง Omise ของ NextAcc (ระบบพร้อม) ·
ไม่มีบริษัททดสอบ · หน้าทดลองจ่ายค่าเริ่ม AdminsOnly · ตั้งค่าได้ยืดหยุ่น

| ข้อออกแบบ | สถานะ | ที่อยู่ |
| --- | --- | --- |
| §3.1 config ตัวเดียว + service ร่วม | ✅ | `Services/Payments/PaymentProviderConfigService.cs` (ร้าน `PaymentSettingsController` + แอดมิน `AdminPlatformGatewayController`) |
| §3.2 หน้าแอดมิน | ✅ | `wwwroot/admin/platform-gateway.html` · เมนู "ช่องทางรับชำระของ NextAcc" |
| §3.3 ด่าน live เข้มขึ้นฝั่งร้าน + ทดลองจ่าย | ✅ | `Helpers/PaymentLiveGate` · `payment-settings/{code}/self-test` · `pages/payment-settings.html` |
| §3.4 หน้าทดลองจ่าย | ✅ | `wwwroot/platform-pay-test.html` · `PlatformPayTestController` · `Helpers/PlatformPayTestPage` (ช่องทางโหมดจริง ⇒ หน้าปิดเอง — ไม่มีเงินจริงผ่านหน้าทดสอบ) · ไม่มี "บริษัททดสอบ" (คำตัดสินเจ้าของ) |
| §4.1 legal kind `refund` | ✅ | `LegalDocumentDefaults` (switch โยน · `ConsentKinds`) · `/refund-policy.html` · แท็บ `admin/legal.html` · ฉบับตั้งต้น = ไม่คืนเงิน + ยกเลิกสิ้นรอบ |
| §4.1 ยกเลิกสิ้นรอบ | ✅ | `Helpers/SubscriptionCancelPolicy` · `Subscription.CancelAtPeriodEnd` · `cancel/undo` · ไม่เข้า PastDue · ไม่ออกใบต่ออายุ |
| §4.2 นโยบายคืนเงินของร้าน | ✅ | `Site.Refund*`/`PolicyAcceptanceText` · `CmsLegalPages` (โทเคน `shop.*` · `RefundContradictions` · `ShopPolicyVersion`) · `cms-edit.html` · `shop-policy.html` · footer storefront |
| §4.3 ติ๊ก + หลักฐาน | ✅ | `Helpers/PurchasePolicyAcceptance` · ตาราง `PolicyAcceptances` · ออเดอร์/จองบริการ/ที่พัก/ขอแพ็กเกจ/สลิปค่าแพ็กเกจ/ส่วนเสริม/ชำระออนไลน์ · component `js/policy-accept.js` (ศูนย์ชำระค่าบริการเรียก `EnforcePlatformAsync(…ChannelPlatformInvoice…)` หรือ `POST platform-pay/policy-acceptance`) |
| ชำระค่าบริการออนไลน์ผ่านแพลตฟอร์ม | ✅ | `IPlatformPaymentService` · `PlatformPaymentController` · ปุ่มเฉพาะเมื่อแพลตฟอร์มเปิด live (`Helpers/PlatformPayAvailability`) |
| §2 P0-4 SecretProtector fail-fast | ✅ (เฟส 0 ฝ่ายค้าน `497e69fc`) | `Helpers/EncryptionKeyResolver` · `SecretReprotectJob` — ไม่ใช่งานของรอบนี้ |
| §4.4 คืนเงิน + ใบลดหนี้ค่าบริการแพลตฟอร์ม | ⏳ | นโยบายไม่คืนเงิน — ข้อยกเว้นทำด้วยมือ · เส้นอัตโนมัติรอศูนย์ชำระค่าบริการ |
| หน้าจองโต๊ะ (ไม่มีชำระเงิน) | ลิงก์อย่างเดียว | `booking.html` ลิงก์นโยบายคืนเงิน — ไม่มีขั้นจ่ายจึงไม่บังคับติ๊ก |

ทิศตรงข้ามที่ล็อกด้วยเทสต์: พนักงานบันทึกแทนไม่ถูกบังคับ · ชนิดต้นทางอื่นไม่ถูกด่านทดลองจ่าย · แพ็กเกจที่ไม่ได้ขอยกเลิกยังเข้า PastDue · ค่าบริการที่ยอมรับแล้วไม่ต้องติ๊กซ้ำ
แต่รายการอื่นต้องติ๊ก · ข้อความนโยบายที่ตรงค่าตั้งไม่ถูกเตือน — `PlatformGatewayRefundPolicyTests` · `Db/PurchasePolicyAcceptanceDbTests`
