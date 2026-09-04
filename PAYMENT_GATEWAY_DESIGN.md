# PAYMENT_GATEWAY_DESIGN.md — รับชำระเงินออนไลน์ผ่าน Payment Gateway (Omise ก่อน · เปลี่ยนเจ้าได้)

> ออกแบบ 2026-09-03 · ข้อเท็จจริงในโค้ดตรวจด้วย `grep`/อ่านไฟล์จริง · ทีม 5 มุมมองถกก่อนสรุป
> · เขียนให้ Opus ลงมือได้โดยไม่ต้องไล่ใหม่ · **ข้อมูลเชิง API ของ Omise (Opn Payments) ในเอกสาร
> นี้มาจากความรู้ทั่วไป ต้องยืนยันกับเอกสารทางการ ณ วันลงมือ — เขียนกำกับไว้ทุกจุดที่เป็นสมมติฐาน**

---

## 0. คำตอบสั้น ๆ

**วันนี้ระบบยังไม่มีการเชื่อม gateway ใดเลย** — มีแค่ "ป้ายชื่อ" (`PaymentGatewayType.Omise = 7`)
กับช่องเก็บคีย์ที่เข้ารหัสไว้แล้ว**ไม่มีโค้ดตัวไหนอ่าน** ทุกทางที่ลูกค้าจ่ายเงินเข้าระบบ
วันนี้คือ **อัปโหลดสลิปให้คนตรวจ** (4 เส้นทางแยกกัน) ส่วน webhook ที่มีอยู่รับได้เฉพาะ
payload ที่ "ตัวกลางแปลงมาให้ก่อน" ซึ่งตัวกลางนั้นไม่มีอยู่จริง

สิ่งที่ต้องสร้าง = **ชั้นกลางตัวเดียว** (`IPaymentProvider` + `PaymentIntent`) ที่ทุกทางเข้า
(เว็บขายของ · portal ลูกค้า · มัดจำที่พัก · ค่าบริการ SaaS · POS) เรียกเหมือนกัน แล้ว Omise
เป็นแค่ adapter ตัวแรก — เปลี่ยนเจ้าคือเขียน adapter ใหม่ตัวเดียว ไม่แตะทางเข้า 5 ทาง

---

## 1. ข้อเท็จจริงในโค้ด (ตรวจแล้ว)

| ที่ | ข้อเท็จจริง |
| --- | --- |
| `grep -i omise --include=*.cs` | **0 ไฟล์** — ที่ grep เจอใน `.html` 20 ไฟล์คือคำว่า `Promise` ทั้งหมด ไม่ใช่ Omise |
| `Models/Enums/AllEnums.cs:1246-1255` | `PaymentGatewayType { BankTransfer, PromptPay, CreditCard, PayPal, Stripe, TwoC2P, Omise, Custom }` — เป็น enum ที่ไม่มี implementation ใดรองรับ ยกเว้น 2 ตัวแรกที่เป็น manual |
| `Models/Entities/CmsCommerce.cs:267-300` | `SitePaymentGateway` ต่อ**เว็บไซต์** (ไม่ใช่ต่อบริษัท): `ApiKeyEncrypted`/`SecretKeyEncrypted`/`WebhookSecret`/`IsTestMode`/PromptPay/บัญชีธนาคาร |
| `CmsCommerceService.cs:732-800` | CRUD gateway: เข้ารหัสด้วย `EncryptionHelper.Encrypt(_encryptionKey)` — **คนละทาง**กับ `ISecretProtector` ที่ webhook ใช้ถอด `WebhookSecret` (คอมเมนต์ในโค้ดเองบอกว่า "ต้องผ่าน ISecretProtector") = สองทางเข้ารหัสในไฟล์เดียว |
| `CmsCommerceService.cs:1619-1640` | `GetStorefrontPaymentOptionsAsync` คืน**เฉพาะ** PromptPay ID / บัญชีธนาคาร — `HasPaymentMethod` = มีอย่างใดอย่างหนึ่ง · คีย์ API ที่เก็บไว้**ไม่เคยถูกอ่าน** |
| `Controllers/CmsWebhookController.cs` | รับ `POST /api/cms-webhook/{siteId}/payment` · ตรวจ HMAC-SHA256 ของ body ด้วย `WebhookSecret` · payload ต้องเป็น**รูปแบบกลางของเรา** `{orderId, paymentId, status, gatewayRef…}` · doc-comment บอกให้มี "adapter (Edge Function / Cloudflare Worker) แปลงจาก schema ของ vendor ก่อน" — **adapter นั้นไม่มีอยู่ในเรพ** · ปลายทางเรียก `ConfirmPaymentAsync` (idempotent · sync ERP · JE · ตัดสต็อก · e-Tax) — **ส่วนนี้ดีและใช้ต่อได้** |
| 4 เส้นทางรับเงินแบบสลิป | (1) เว็บขายของ `RecordPaymentSlipAsync:1309` (2) portal ลูกค้า `PortalController:110 upload-slip` (3) มัดจำที่พัก `LodgingService.UploadSlipByTokenAsync` (4) ค่าบริการ SaaS `SubscriptionController:164 payments/{id}/slip` — แต่ละทางมีโค้ดของตัวเอง ถ้าต่อ gateway ทีละทางจะได้ 4 สำเนา |
| `PosService.Orders.cs:1508-1530` | POS รับเงินสด/โอน/บัตร โดยแคชเชียร์กด "รับแล้ว" — บัตร/QR ผ่านเครื่อง EDC ภายนอก ไม่มีการยืนยันจากระบบ |
| `Payment.cs` (เอกสาร) | `Payment(DocumentId, PaymentMethod, Reference, BankAccountId)` — ไม่มีช่องเก็บ charge id/fee/settlement |
| `Program.cs:427` | `AddHttpClient()` ทั่วไป · ไม่มี named client · ไม่มี SDK ของ gateway ใน `.csproj` |
| `Middleware/SecurityMiddleware.cs:102` | `script-src` ไม่มี `cdn.omise.co` ⇒ **omise.js โหลดไม่ได้** ถ้าไม่เพิ่ม (บทเรียน Google SSO: ถูก CSP ของเราเองบล็อกเงียบ) |
| `BankFeedService` + `BankService.MatchResolution` | มีกลไกจับคู่รายการธนาคารกับ JE — ใช้ต่อกับยอด settlement ของ gateway ได้ |
| `Helpers/AdvisoryLockKey` · `IJobRunRecorder` · `BackgroundService` pattern | ใช้ทำ job กระทบยอด/poll สถานะได้ทันที |

**สรุปสิ่งที่ "มีแล้วและดี"**: `ConfirmPaymentAsync` orchestrator · webhook HMAC + idempotency ·
`ISecretProtector` · `Site.CaptchaProvider` แบบ per-site config · bank reconciliation
**สิ่งที่ "มีแต่ใช้ไม่ได้"**: คีย์ที่เข้ารหัสไว้ · enum Omise · `IsTestMode` ที่ไม่มีใครอ่าน

---

## 2. Omise (Opn Payments) — สิ่งที่ต้องรู้ก่อนออกแบบ

> ⚠️ ยืนยันกับ docs ทางการ ณ วันลงมือ — เขียนจากความรู้ทั่วไป ไม่ใช่จากการทดสอบในรอบนี้

| เรื่อง | สิ่งที่รู้ | ผลต่อการออกแบบ |
| --- | --- | --- |
| คีย์ | คู่ **public** (`pkey_test_…`/`pkey_live_…`) ใช้ฝั่งเบราว์เซอร์ · **secret** (`skey_test_…`/`skey_live_…`) ใช้ฝั่งเซิร์ฟเวอร์เท่านั้น · test กับ live เป็นคนละคู่ | `IsTestMode` ต้องเลือก**คู่คีย์** ไม่ใช่แค่ธง · secret ห้ามออกจากเซิร์ฟเวอร์ (PDPA ม.37 + PCI) |
| บัตร | `omise.js` ทำ tokenization ในเบราว์เซอร์ → ได้ `tokn_…` → เซิร์ฟเวอร์สร้าง charge ด้วย token · เลขบัตร**ไม่ผ่านเซิร์ฟเวอร์เรา** (PCI DSS SAQ-A) · 3-D Secure ผ่าน `return_uri` | ห้ามรับเลขบัตรใน API ของเรา · ต้องมีหน้า return + ตรวจสถานะ charge อีกครั้งหลังกลับมา (ห้ามเชื่อ query string) |
| PromptPay / โอน / วอลเล็ต | สร้าง **source** (`promptpay`, `internet_banking_*`, `mobile_banking_*`, `truemoney` …) → charge ที่ `pending` → ลูกค้าจ่าย → webhook `charge.complete` | flow เป็น **asynchronous** — ต้องรอ webhook หรือ poll · QR หมดอายุ (ต้องเก็บ `expires_at`) |
| Webhook | ส่ง **Event object** (`charge.complete`, `charge.create`, `refund.create` …) · **ไม่มีลายเซ็น HMAC** — วิธียืนยันที่แนะนำคือ **fetch `GET /events/{id}` กลับไปที่ Omise ด้วย secret key** แล้วเชื่อข้อมูลจากที่ fetch ไม่ใช่จาก body | webhook receiver เดิม (HMAC ของเรา) **ใช้กับ Omise ตรง ๆ ไม่ได้** ต้องมี endpoint ต่อ provider ที่ยืนยันแบบของเจ้านั้น |
| Idempotency | อ้างอิงออเดอร์ผ่าน `metadata` บน charge · การกันสร้าง charge ซ้ำเป็นหน้าที่**ฝั่งเรา** | 1 `PaymentIntent` ต่อ (ต้นทาง, ยอด) · เก็บ `ProviderRef` ทันทีที่สร้าง · advisory lock ตอนสร้าง |
| Refund | API คืนเงินบางส่วน/เต็ม · บัตรใช้เวลาหลายวันกว่าจะเห็น | refund ต้องออก**ใบลดหนี้** (§86/10) ไม่ใช่แค่กลับ JE |
| Settlement | โอนเข้าบัญชีร้าน T+n **หักค่าธรรมเนียม** (net) · ค่าธรรมเนียมมี VAT 7% · มีใบกำกับภาษีจาก Omise รายเดือน | ต้องมี**บัญชีพักเงินรอรับจาก gateway** (clearing) และกระทบยอดกับยอดที่เข้าธนาคารจริง |
| Sandbox | test mode ใช้บัตรทดสอบ/QR ทดสอบที่ Omise กำหนด · webhook ยิงได้ทั้ง test/live (ตั้ง URL ในแดชบอร์ด) | หน้าตั้งค่าต้องมีปุ่ม "ทดสอบ" ที่สร้าง charge ทดสอบจริง 1 บาท (ไม่ใช่แค่ตรวจว่าคีย์ไม่ว่าง) |
| Fee (เพื่อวางแผน ไม่ใช่ตัวเลขยืนยัน) | บัตร ~3-4% · PromptPay/โอน ~1-2% · ตามสัญญาแต่ละร้าน | เก็บอัตราคาดหมายไว้ต่อ provider config เพื่อ**ประมาณ** fee ตอน charge แล้ว**ปรับเป็นจริง**ตอน settlement |

---

## 3. ทีมถกเถียง (5 มุมมอง)

### A — Security / PCI
> "เส้นแดงข้อเดียว: **เลขบัตรห้ามแตะเซิร์ฟเวอร์เรา** · secret key ห้ามลง localStorage/JS ·
> ห้ามเชื่อ query string ตอน 3DS กลับมา · webhook ต้องยืนยันกับ provider เสมอ ไม่ใช่เชื่อ body"
- **โต้ D**: "ชั้นกลางที่ดีคือชั้นที่**ห้ามให้ adapter ทำผิดได้** — `IPaymentProvider` ต้องไม่มีเมธอด
  ที่รับเลขบัตรเลย มีแต่รับ token · และการยืนยัน webhook ต้องเป็น**ความรับผิดชอบของ adapter**
  ไม่ใช่ controller กลาง (แต่ละเจ้ายืนยันคนละแบบ)"
- ต้องการ: คีย์ผ่าน `ISecretProtector` ตัวเดียว (เลิก `EncryptionHelper` ตรง ๆ) · ระบบ log
  ทุก call ไป provider โดย**ไม่ log secret/token** · rate-limit endpoint สร้าง intent
  (กัน card testing attack) · webhook endpoint ต่อ provider แยก URL

### B — CPA / ภาษี
> "เงินที่ลูกค้าจ่าย 107 บาท เข้าธนาคารร้านจริง 103.xx บาท ใน 2 วันถัดมา — ถ้าระบบลง
> Dr ธนาคาร 107 ตอน webhook มา ยอดธนาคารในบัญชีจะไม่ตรงกับสมุดบัญชีจริง**ตลอดไป**
> และค่าธรรมเนียมที่หายไปไม่มีใบกำกับให้เคลมภาษีซื้อ"
- โมเดลที่ถูก: ตอน charge สำเร็จ **Dr ลูกหนี้ payment gateway (11xxx) 107 / Cr ลูกหนี้การค้า
  107** (ลูกค้าหมดหนี้ · ใบเสร็จ/ใบกำกับออกได้ทันที §78/1 tax point = วันรับเงิน) · ตอน settlement
  **Dr ธนาคาร 103.xx + Dr ค่าธรรมเนียม 3.5 + Dr ภาษีซื้อ 0.245 / Cr ลูกหนี้ gateway 107**
- **โต้ C**: "การ 'ออกใบเสร็จทันทีตอน charge' ถูกต้องตาม tax point แม้เงินยังไม่เข้าธนาคาร —
  §78/1 นับวันที่**ได้รับชำระ** ซึ่งคือวันที่ gateway รับแทนเรา ไม่ใช่วัน settle"
- **หัก ณ ที่จ่าย**: ค่าธรรมเนียม gateway = ค่าบริการ ม.40(8) → ร้านที่เป็นนิติบุคคล**ต้องหัก 3%**
  และออก 50 ทวิให้ Omise (ท.ป.4/2528) — แต่ Omise หักค่าธรรมเนียมจากเงินก่อนโอน ร้านไม่ได้
  "จ่าย" โดยตรง ในทางปฏิบัติ gateway ส่วนใหญ่มีกลไก "หนังสือรับรอง WHT"/หักภายหลัง
  **ต้องถามนักบัญชีของลูกค้าและอ่านสัญญา Omise ก่อนตัดสิน** — ระบบต้องรองรับทั้งสองทาง
  (ตั้งค่า `WhtOnFee: None | Withhold3Percent`)
- ต้องการ: บัญชี clearing ต่อ provider · ใบกำกับภาษีค่าธรรมเนียมรายเดือนเข้าเป็น
  PurchaseInvoice ปกติ (เคลมภาษีซื้อได้ §82/3 ภายใน 6 เดือน) · refund → ใบลดหนี้ ·
  รายงานกระทบยอด: Σ charge − Σ fee − Σ refund = Σ settlement

### C — เจ้าของร้าน / ประสบการณ์ลูกค้า
> "ลูกค้ากด 'ชำระเงิน' แล้วต้องจบในหน้าเดียว — QR ขึ้นทันที สแกนจ่าย หน้าเปลี่ยนเป็น
> 'ชำระแล้ว' เอง ไม่ต้องอัปโหลดสลิป ไม่ต้องรอผมกดยืนยัน · และผมอยากเปิดใช้ได้เองจาก
> หน้าตั้งค่า ใส่คีย์ 2 ตัว กดทดสอบ เห็นว่าผ่าน แล้วเปิดจริง"
- **โต้ B**: "ผมไม่อยากเห็นบัญชี 'ลูกหนี้ gateway' บนจอทุกวัน — ให้มันทำงานเบื้องหลัง
  ผมแค่อยากรู้ว่า 'เดือนนี้ Omise หักไปเท่าไร' บรรทัดเดียว"
- **โต้ A**: "ทดสอบ sandbox ต้องง่ายกว่านี้ — ไม่ใช่ให้ผมไปหาบัตรทดสอบในเว็บ Omise เอง
  ระบบต้องมีปุ่ม 'ยิงทดสอบ' ที่บอกผลเป็นภาษาคน"
- ต้องการ: หน้าตั้งค่า 1 หน้า (คีย์ test/live · เปิดวิธีจ่ายที่ต้องการ · ทดสอบ · สลับ live) ·
  ลูกค้าจ่ายแล้วหน้าเปลี่ยนเอง (polling/SSE) · ถ้า gateway ล่ม **ต้องยังมีทางอัปโหลดสลิป**
  (ห้ามให้ระบบขายของไม่ได้เพราะ Omise ล่ม)

### D — สถาปนิกระบบ
> "มี 5 ทางเข้าที่จะรับเงิน (เว็บขาย · portal · ที่พัก · SaaS · POS) และวันนี้แต่ละทางมีโค้ด
> สลิปของตัวเอง ถ้าต่อ Omise ทีละทาง = 5 สำเนา + 5 webhook + 5 วิธีกระทบยอด
> ต้องมี **`PaymentIntent` ตัวเดียว**ที่ทุกทางเข้าสร้าง แล้ว intent เป็นคนคุยกับ provider"
- **โต้ C**: "polling ทุก 2 วินาทีจากหน้าลูกค้า 100 คน = 50 req/s เข้า DB — ใช้ webhook เป็น
  เส้นหลัก polling เป็น fallback ที่ถอยห่างขึ้นเรื่อย ๆ (2s → 5s → 10s) และมี job กระทบยอด
  ทุก 5 นาทีสำหรับ intent ที่ค้าง `Pending` เกิน 1 นาที (webhook หาย = เรื่องปกติ)"
- **โต้ A**: "webhook endpoint ต่อ provider ถูก แต่ **route ต้องไม่มี siteId/companyId ใน URL**
  เพราะ Omise ตั้ง webhook URL ได้**ที่เดียวต่อบัญชี** — ถ้าลูกค้ามีหลายเว็บ/หลายบริษัทใต้
  บัญชี Omise เดียว ต้อง resolve จาก `metadata.intentId` ใน event ไม่ใช่จาก URL"
- ต้องการ: state machine ชัด (`Created → Pending → Succeeded | Failed | Expired → Refunded`)
  · ทุก transition มี advisory lock ต่อ intent · `ProviderRef` unique index · outbox
  สำหรับ side-effect (ออกใบเสร็จ/JE) ที่ retry ได้โดยไม่ซ้ำ

### E — Ops / Support
> "เคสที่จะโทรมาแน่: (1) ลูกค้าบอกจ่ายแล้วแต่ระบบยังไม่ขึ้น (2) จ่ายซ้ำสองครั้ง (3) ยอดเข้า
> ธนาคารไม่ตรง (4) ลืมสลับจาก test เป็น live แล้วขายจริงด้วยคีย์ทดสอบทั้งวัน"
- **โต้ C**: "ปุ่ม 'สลับ live' ต้องมีด่าน — ต้องผ่านการทดสอบสำเร็จอย่างน้อย 1 ครั้งก่อน และ
  หน้าตั้งค่าต้องโชว์ป้ายใหญ่ 'โหมดทดสอบ — เงินไม่เข้าจริง' ตลอดเวลาที่ยังเป็น test"
- ต้องการ: หน้า "รายการชำระผ่าน gateway" ที่ค้นด้วยเลขอ้างอิง Omise ได้ · ปุ่ม "ตรวจสถานะ
  กับ provider ตอนนี้" (fetch charge สด) · ปุ่ม "บันทึกว่าได้รับแล้วด้วยมือ" พร้อมเหตุผล +
  AuditLog สำหรับเคสที่ webhook หายถาวร · แจ้งเตือนเมื่อ intent ค้าง Pending > 30 นาที
  ในโหมด live · แจ้งเตือนถ้าโหมด test ถูกใช้สร้าง intent เกิน N ครั้ง/วันบนเว็บที่ published

### ข้อสรุปที่ทีมตกลงกัน

1. **ชั้นกลางตัวเดียว** (D) — `PaymentIntent` + `IPaymentProvider` · ทางเข้าทั้ง 5 ไม่รู้จัก
   Omise · ทางสลิปเดิม**คงไว้**เป็น fallback (C) และเป็น provider ตัวหนึ่งชื่อ `ManualSlip`
2. **บัญชี clearing ต่อ provider** (B ชนะ C บนหลักการ · C ชนะบน UI: ซ่อนไว้เบื้องหลัง
   โชว์แค่สรุป "ค่าธรรมเนียมเดือนนี้")
3. **Webhook ต่อ provider + ยืนยันแบบของเจ้านั้น + resolve ด้วย metadata ไม่ใช่ URL** (A+D)
   · controller กลางเดิมคงไว้สำหรับ adapter ภายนอกที่ใครอยากใช้
4. **ห้ามสลับ live ก่อนทดสอบผ่าน** (E) · โหมดทดสอบมีป้ายตลอด
5. **ใบเสร็จออกตอน charge สำเร็จ** (B — tax point §78/1) · **JE ธนาคารออกตอน settlement**
   · refund = ใบลดหนี้ ไม่ใช่ลบใบเดิม
6. **WHT บนค่าธรรมเนียม = ตั้งค่าได้** (B) · default `None` จนกว่านักบัญชีลูกค้าจะยืนยัน
   · ระบบเตือนครั้งเดียวตอนเปิดใช้ว่ามีประเด็นนี้
7. **Polling เป็น fallback** (D) · webhook เส้นหลัก · job กระทบยอดทุก 5 นาที

---

## 4. การออกแบบ

### 4.1 Abstraction

```csharp
/// ชั้นกลางที่ทางเข้าทุกทางเห็น — ไม่มีคำว่า Omise ใน interface นี้
public interface IPaymentProvider
{
    string ProviderCode { get; }                       // "omise" · "manual-slip" · (อนาคต "2c2p")
    PaymentCapabilities Capabilities { get; }          // Card · PromptPay · MobileBanking · Refund · PartialRefund
    Task<ProviderCharge> CreateChargeAsync(PaymentIntent intent, ChargeRequest req, CancellationToken ct);
    Task<ProviderCharge> GetChargeAsync(PaymentIntent intent, CancellationToken ct);   // poll / ตรวจสด
    Task<ProviderRefund> RefundAsync(PaymentIntent intent, decimal amount, string reason, CancellationToken ct);
    /// ยืนยัน webhook **แบบของเจ้านั้น** แล้วคืน event ที่เชื่อถือได้ — null = ปฏิเสธ
    Task<VerifiedWebhookEvent?> VerifyWebhookAsync(HttpRequest request, PaymentProviderConfig cfg, CancellationToken ct);
    Task<ProviderHealth> TestConnectionAsync(PaymentProviderConfig cfg, CancellationToken ct);  // ปุ่ม "ทดสอบ"
}

// ChargeRequest ไม่มีช่องเลขบัตร — มีแต่ CardToken (จาก omise.js) หรือ SourceType
public sealed record ChargeRequest(PaymentMethodKind Kind, string? CardToken, string? ReturnUrl,
    string CustomerEmail, string? CustomerPhone, string Description);
```

- `OmisePaymentProvider` (adapter ตัวแรก) · `ManualSlipPaymentProvider` (ห่อ flow สลิปเดิม
  ให้เดินผ่าน intent เดียวกัน — ทำให้ทุกทางเข้ามีโค้ดชุดเดียว)
- adapter คุยกับ provider ผ่าน `IHttpClientFactory.CreateClient("omise")` — named client ที่
  ตั้ง base address/timeout/retry ที่ `Program.cs` · **ไม่ใช้ SDK** (เรพนี้เลี่ยง transitive deps
  ตามกฎ `MiniExcel`-only) · ยืนยัน TLS ตามปกติ

### 4.2 Data model (additive)

```
PaymentProviderConfig (ใหม่ · ต่อบริษัท ไม่ใช่ต่อเว็บ)
  Id · CompanyId · ProviderCode · DisplayName
  TestPublicKey · TestSecretKeyProtected · LivePublicKey · LiveSecretKeyProtected   ← ISecretProtector เท่านั้น
  WebhookSecretProtected? (เจ้าที่มี)  · Mode {Test, Live} · LiveEnabledAt? · LastTestPassedAt?
  EnabledMethodsJson (["card","promptpay","mobile_banking"])
  ClearingAccountId (FK ChartOfAccount 11xxx "ลูกหนี้ payment gateway — {provider}")
  FeeExpenseAccountId · ExpectedFeePercentByMethodJson · WhtOnFee {None, Withhold3Percent}
  IsActive · SortOrder

PaymentIntent (ใหม่ · TenantEntity)
  Id · CompanyId · ProviderConfigId · ProviderCode
  SourceKind {SiteOrder, Document, LodgingReservation, SubscriptionPayment, PosOrder}
  SourceId (Guid) · SiteId? · ContactId?
  Amount · Currency · Description · CustomerEmail/Phone (masked ตอน log)
  Status {Created, Pending, Succeeded, Failed, Expired, Refunded, PartiallyRefunded}
  MethodKind · ProviderRef (charge id · unique index ต่อ provider) · ProviderStatusRaw
  QrPayload? · QrExpiresAt? · ReturnUrl? · AuthorizeUrl? (3DS)
  FailureCode? · FailureMessage?
  FeeEstimated · FeeActual? · SettledAmount? · SettledAt? · SettlementRef?
  ConfirmedAt? · ConfirmedBy ("webhook:omise" | "poll" | "manual:{user}")
  ReceiptDocumentId? · JournalEntryId? (JE ตอน confirm) · SettlementJournalEntryId?
  AttemptCount · LastPolledAt? · IdempotencyKey (unique: "{SourceKind}:{SourceId}:{Amount}:{seq}")

PaymentIntentEvent (ใหม่ · append-only)
  IntentId · At · Source {Webhook, Poll, Manual, System} · FromStatus · ToStatus · PayloadJson (ตัด PII) · Note

Payment (เดิม)          + PaymentIntentId? · GatewayFeeAmount? · GatewayRef?
SiteOrderPayment (เดิม) + PaymentIntentId?
SubscriptionPayment     + PaymentIntentId?
LodgingReservation      + DepositIntentId?
PosPayment              + PaymentIntentId?  (POS แสดง QR บนจอ/เครื่องลูกค้า)
```

- `SitePaymentGateway` เดิม: **คงไว้สำหรับ PromptPay/บัญชีธนาคารแบบ manual** (ต่อเว็บ) ·
  ช่อง `ApiKeyEncrypted/SecretKeyEncrypted/IsTestMode` ที่ไม่มีใครอ่าน **ย้ายเข้า**
  `PaymentProviderConfig` ด้วย migration แล้วเลิกใช้ (ไม่ลบคอลัมน์ทันที — ทิ้งหมายเหตุ)
- ทำไมต่อ**บริษัท**ไม่ใช่ต่อเว็บ: บัญชี Omise ผูกกับนิติบุคคล (เอกสารจดทะเบียน) · ทางเข้า
  4 ใน 5 ไม่มี `SiteId` (portal · ที่พัก · SaaS · POS) · ค่าธรรมเนียมลงบัญชีระดับบริษัท

### 4.3 Flow หลัก (PromptPay บนเว็บขายของ — เส้นที่ใช้บ่อยสุดในไทย)

```
1. ลูกค้ากด "ชำระด้วย PromptPay"  →  POST /api/pay/intents  {sourceKind:SiteOrder, sourceId}
   เซิร์ฟเวอร์: ตรวจ source ยังค้างชำระ · advisory lock (companyId, "pay-intent", sourceId)
   · ถ้ามี intent Pending ที่ยังไม่หมดอายุของ source นี้ → คืนตัวเดิม (ห้ามสร้าง QR ใหม่ซ้อน)
   · ไม่งั้นสร้าง PaymentIntent(Created) → provider.CreateChargeAsync(promptpay)
   → ได้ charge id + QR payload + expires → บันทึก Pending · คืน {intentId, qr, expiresAt}
2. หน้าเว็บโชว์ QR + นับถอยหลัง · subscribe สถานะ: GET /api/pay/intents/{id}/status
   (polling ถอยห่าง 2s→5s→10s · หยุดเมื่อ Succeeded/Failed/Expired)
3. ลูกค้าสแกนจ่าย → Omise ยิง webhook  POST /api/pay/webhooks/omise
   → OmisePaymentProvider.VerifyWebhookAsync: อ่าน event id จาก body → **fetch GET /events/{id}
   ด้วย secret key ของ config ที่ตรงกับ metadata.companyId** → เชื่อเฉพาะข้อมูลที่ fetch มา
   → resolve intent จาก charge.metadata.intentId (ไม่ใช่จาก URL)
4. PaymentIntentService.MarkSucceededAsync(intent, providerCharge)
   [advisory lock ต่อ intent · idempotent: ถ้า Succeeded แล้ว = no-op]
   → เขียน PaymentIntentEvent
   → ทางเข้าเดิมทำงานต่อ: SiteOrder → ConfirmPaymentAsync (มีอยู่แล้ว ดีอยู่แล้ว)
                          Document → PaymentService.CreatePaymentAsync(..., IntentId)
                          Lodging  → ConfirmAsync(deposit) · SaaS → ApprovePayment · POS → AddPayment
   → JE: Dr ลูกหนี้ gateway (ClearingAccountId) / Cr ลูกหนี้การค้า (หรือ Cr รายได้+VAT ถ้าขายสด)
5. Settlement (T+n): job อ่าน transfer/settlement จาก provider (หรือจับคู่จาก BankFeed)
   → Dr ธนาคาร (net) + Dr ค่าธรรมเนียม + Dr ภาษีซื้อ(ค่าธรรมเนียม) / Cr ลูกหนี้ gateway (gross)
   → intent.FeeActual/SettledAt · ผลต่าง FeeEstimated vs FeeActual ปรับที่บรรทัดค่าธรรมเนียม
```

**บัตร**: ขั้น 1 หน้าเว็บโหลด `omise.js` (ต้องเพิ่ม `https://cdn.omise.co` ใน CSP `script-src`
+ `frame-src` สำหรับ 3DS) → tokenize → ส่ง `tokn_…` มาขั้น 1 · ถ้า charge คืน `authorize_uri`
→ redirect 3DS → กลับมา `ReturnUrl` → หน้า return **ไม่เชื่อ query string** เรียก
`GET intents/{id}/status` ซึ่งไป `provider.GetChargeAsync` สดถ้ายัง Pending

### 4.4 Sandbox / ตั้งค่า / สลับ live

หน้า `pages/payment-settings.html` (ต่อบริษัท · สิทธิ์ `Settings.Manage`):
1. เลือก provider (Omise) → กรอกคีย์ **test** ก่อน (secret ถูก mask หลังบันทึก แสดง 4 ตัวท้าย)
2. เลือกวิธีจ่ายที่เปิด (บัตร / PromptPay / mobile banking …) ตาม `Capabilities`
3. ปุ่ม **"ทดสอบเชื่อมต่อ"** → `TestConnectionAsync`: (ก) เรียก API ด้วย secret เพื่อยืนยันคีย์
   (ข) สร้าง charge ทดสอบ 20 บาท PromptPay แล้วโชว์ QR ทดสอบ (ค) รอ webhook/poll → ผ่าน =
   `LastTestPassedAt` · ข้อความผลเป็นภาษาคน ("คีย์ถูกต้อง แต่ webhook ยังไม่ถึง — ตรวจ URL
   ในแดชบอร์ด Omise: …") · **ห้ามข้าม (ค)**: คีย์ถูกแต่ webhook ไม่ถึงคือเคสที่พบบ่อยที่สุด
4. กรอกคีย์ **live** → ปุ่ม "เปิดใช้จริง" **ปิดอยู่จนกว่า** `LastTestPassedAt != null` · เปิดแล้ว
   บันทึก `LiveEnabledAt` + AuditLog + อีเมลแจ้งเจ้าของ
5. ป้ายโหมด: test = แถบเหลืองใหญ่ "โหมดทดสอบ — เงินไม่เข้าจริง" ทั้งหน้าตั้งค่า**และ**หน้าจ่าย
   ของลูกค้า (storefront/portal) · live = ไม่มีป้าย
6. แสดง webhook URL ที่ต้องไปตั้งในแดชบอร์ด Omise + ปุ่มคัดลอก + สถานะ "webhook ล่าสุดเมื่อ…"

### 4.5 Job / กระทบยอด

| งาน | ทำอะไร |
| --- | --- |
| `PaymentIntentReconcileJob` (ทุก 5 นาที · `AdvisoryLockKey.For(PaymentReconcile, "tick")`) | intent `Pending` ที่ `LastPolledAt` เกิน 1 นาที → `GetChargeAsync` สด → sync สถานะ (กัน webhook หาย) · `Pending` เกิน `QrExpiresAt` → `Expired` · live intent ค้าง > 30 นาที → แจ้งเตือน |
| `PaymentSettlementJob` (รายวัน) | ดึง settlement ของเมื่อวาน (API ของ provider หรือจับคู่ยอดโอนเข้าจาก `BankFeed` ด้วย `SettlementRef`) → JE settlement · ผูก `SettledAt` ทุก intent ในรอบ · ยอดที่จับคู่ไม่ได้ → รายการรอตรวจ (ห้ามเงียบ) |
| รายงาน "กระทบยอด gateway" | Σ Succeeded − Σ Refund − Σ Fee = Σ Settlement ต่อเดือน · แถวที่ไม่สมดุลชี้ให้เห็น |

### 4.6 UI ฝั่งลูกค้าที่จ่าย (ทุกทางเข้าใช้ component เดียว)

`wwwroot/js/pay-widget.js` — รับ `{intentEndpoint, sourceKind, sourceId}` → วาดวิธีจ่ายที่เปิด →
QR/บัตร/redirect → poll → เปลี่ยนหน้าเป็น "ชำระแล้ว" · มีปุ่ม "อัปโหลดสลิปแทน" เสมอ
(fallback เมื่อ gateway ล่ม — C) · ใช้ใน `storefront.html` (checkout) · `portal.html` (openPay) ·
หน้าจองที่พัก (`/reservation/{token}`) · `subscription.html` (ค่าบริการ) · `pos.html`
(โหมด "ให้ลูกค้าสแกนที่จอ")

---

## 5. บัญชีและภาษี — สรุปสำหรับนักบัญชี

| เหตุการณ์ | Dr | Cr | หมายเหตุ |
| --- | --- | --- | --- |
| charge สำเร็จ (ขายเชื่อที่มีใบแจ้งหนี้) | ลูกหนี้ gateway 107 | ลูกหนี้การค้า 107 | ใบเสร็จออกวันนี้ (§78/1) |
| charge สำเร็จ (ขายสด storefront/POS) | ลูกหนี้ gateway 107 | รายได้ 100 + ภาษีขาย 7 | ใบเสร็จ/ใบกำกับออกวันนี้ |
| settlement T+2 | ธนาคาร 103.24 · ค่าธรรมเนียม 3.50 · ภาษีซื้อ 0.245 (ปัดตามใบกำกับจริง) | ลูกหนี้ gateway 107 | ค่าธรรมเนียม+VAT ตามใบกำกับที่ provider ออก (เคลม §82/3 ใน 6 เดือน) |
| WHT บนค่าธรรมเนียม (ถ้าตั้ง `Withhold3Percent`) | ค่าธรรมเนียม 3.50 | ภาษีหัก ณ ที่จ่ายค้างจ่าย 0.105 · ลูกหนี้ gateway (ลดลง) | ออก 50 ทวิ · ภ.ง.ด.53 — **ต้องตรงกับวิธีที่ provider ยอมรับจริง** |
| refund เต็ม/บางส่วน | ลูกหนี้การค้า/รายได้+VAT (ตามใบลดหนี้) | ลูกหนี้ gateway | **ต้องออกใบลดหนี้ §86/10** อ้างใบเดิม · ห้ามลบ/แก้ใบเดิม |
| chargeback (บัตร) | เหมือน refund + ค่าปรับ (ถ้ามี) เป็นค่าใช้จ่าย | | ค่าปรับ chargeback ตรวจ §65 ตรี(6) — ค่าปรับตามสัญญาเอกชนหักได้ ไม่ใช่ค่าปรับอาญา |

**ผังบัญชีที่ต้อง seed**: `11xxx ลูกหนี้ payment gateway — Omise` · `5xxxx ค่าธรรมเนียม payment gateway`
(ตรวจกับ `tools/gl_code_check.py` ไม่ให้ชนความหมายผังมาตรฐาน)

---

## 6. ความปลอดภัย / compliance checklist (ต้องครบก่อนเปิด live ให้ลูกค้ารายแรก)

- [ ] เลขบัตรไม่ผ่านเซิร์ฟเวอร์ (มีแต่ token) — `ChargeRequest` ไม่มีช่องรับ PAN
- [ ] secret key ผ่าน `ISecretProtector` เท่านั้น · ไม่มีใน response ใด · ไม่ log · หน้าตั้งค่าแสดง 4 ตัวท้าย
- [ ] webhook ยืนยันกับ provider (Omise = re-fetch event) · ไม่เชื่อ body · endpoint ไม่ระบุ tenant ใน URL
- [ ] `ProviderRef` unique index · advisory lock ต่อ intent · idempotent ทุก transition
- [ ] rate-limit `POST /api/pay/intents` ต่อ IP/ต่อ source (กัน card testing)
- [ ] CSP: `script-src` + `frame-src` เพิ่มโดเมนของ provider · ผ่าน `tools/csp_external_ref_check.py`
- [ ] ห้ามสลับ live ก่อน `LastTestPassedAt` · ป้ายโหมดทดสอบทั้งฝั่งร้านและฝั่งลูกค้า
- [ ] PDPA: อีเมล/เบอร์ลูกค้าที่ส่งไป provider = การเปิดเผยข้อมูลให้บุคคลที่สาม → ระบุใน RoPA
  (ม.39) + นโยบายความเป็นส่วนตัวของเว็บ · `PaymentIntentEvent.PayloadJson` ตัด PII ก่อนเก็บ
- [ ] tax point §78/1 = วัน charge สำเร็จ · refund = ใบลดหนี้ · ค่าธรรมเนียม = ใบกำกับจาก provider
- [ ] fallback สลิปยังใช้ได้เสมอ (ห้ามให้ระบบขายของไม่ได้เพราะ provider ล่ม)
- [ ] เทสต์ pure: state machine · idempotency key · fee estimate/actual · webhook resolver ·
  QR expiry · `ChargeRequest` ไม่มี PAN (เทสต์ compile-time ด้วย reflection ว่าไม่มี property ชื่อ
  Card/Pan/CVV)

---

## 7. แผนพัฒนาสำหรับ Opus

| เฟส | งาน | เกณฑ์ผ่าน |
| --- | --- | --- |
| **1** แกน | `PaymentIntent`/`Event`/`ProviderConfig` + migration · `IPaymentProvider` + `ManualSlipPaymentProvider` (ห่อ flow สลิปเดิม 4 ทาง ให้เดินผ่าน intent) · `PaymentIntentService` (state machine + lock + idempotency) · เทสต์ | ทางเข้าทั้ง 4 ยังทำงานเหมือนเดิม 100% แต่มีแถว intent เกิดขึ้น · ห้ามมี behavior เปลี่ยนในเฟสนี้ |
| **2** Omise sandbox | `OmisePaymentProvider` (PromptPay + บัตร) · named HttpClient · webhook `/api/pay/webhooks/omise` (re-fetch event) · CSP · หน้าตั้งค่า + ทดสอบเชื่อมต่อ · `pay-widget.js` ต่อ storefront ก่อนทางเดียว | จ่าย PromptPay ทดสอบบน storefront แล้วออเดอร์เปลี่ยนเป็นชำระแล้วเองภายใน 5 วิ · ปิด webhook แล้ว poll ยังจับได้ใน 1 นาที |
| **3** บัญชี | clearing account seed · JE ตอน confirm ผ่านทางเข้าเดิม (`ConfirmPaymentAsync`/`PaymentService`) + `PaymentIntentId` บน `Payment` · settlement job + JE · รายงานกระทบยอด · WHT option | ยอด Σ charge − fee − refund = settlement ตรงในรายงาน · ธนาคารในระบบ = ยอดจริง |
| **4** ทางเข้าที่เหลือ | portal (`openPay`) · มัดจำที่พัก (`/reservation/{token}`) · ค่าบริการ SaaS · POS แสดง QR | ทุกทางใช้ `pay-widget.js` ตัวเดียว · ไม่มีโค้ด provider ในทางเข้า |
| **5** Live + ops | ด่านสลับ live · ป้ายโหมด · หน้ารายการ intent (ค้น/ตรวจสด/บันทึกมือ) · แจ้งเตือนค้าง Pending · refund → ใบลดหนี้ | ผู้ใช้จริงเปิด live ได้เองโดยไม่ต้องให้เราช่วย |
| **6** provider ที่สอง (พิสูจน์ abstraction) | เขียน adapter ตัวที่สอง (2C2P หรือ KBank QR) โดย**ไม่แตะ**ทางเข้าและ intent service | ถ้าต้องแก้ไฟล์นอกโฟลเดอร์ adapter = abstraction รั่ว ต้องแก้ก่อนปิดเฟส |

### 7.1 บันทึกการสร้างจริง

| เฟส | สถานะ | หมายเหตุ |
| --- | --- | --- |
| **1** แกน | ✅ | `PaymentProviderConfig`/`PaymentIntent`/`PaymentIntentEvent` + migration (unique index กันซ้ำ 2 ชั้น: idempotency key ต่อบริษัท + charge id ต่อ provider) · `IPaymentProvider` · `ManualSlipPaymentProvider` · `Helpers/PaymentIntentPolicy` (บริสุทธิ์ + 14 เทสต์) · `PaymentIntentService` (ล็อกต่อ source ตอนสร้าง · ต่อ intent ตอนเปลี่ยนสถานะ) · `Payment`/`PosPayment` มี `PaymentIntentId` · `tools/payment_provider_boundary_check.py` — **ยังไม่เปลี่ยนพฤติกรรมของทางเข้าใดเลย** ตามเกณฑ์ผ่านของเฟสนี้ |

| **2** Omise sandbox | ✅ | `OmisePaymentProvider` (PromptPay + บัตร + mobile/internet banking + TrueMoney) · named HttpClient timeout 20 วิ · `POST /api/pay/webhooks/{provider}` ยืนยันด้วยการ **re-fetch event** · หน้าตั้งค่า API (คีย์ · ทดสอบ · สลับโหมด) พร้อม**ด่านเปิด live** · CSP ประกอบจาก `IPaymentProvider.CspNeeds` · `pages/payment-settings.html` (ขั้นตอน 4 ข้อพร้อมติ๊กว่าทำถึงไหน · ปุ่มคัดลอก URL แจ้งเตือน · ปุ่มเปิด live ที่ถูกล็อก) · `js/pay-widget.js` (หน้าจ่ายตัวเดียวของทุกทางเข้า) · `PaymentIntentReconcileJob` ทุก 5 นาที |

| **4** ทางเข้า | ✅ | ตัวจัดการครบ 5 ชนิดต้นทาง: `SiteOrderPaymentHandler` · `DocumentPaymentHandler` (portal · idempotent ด้วย `PAY-INTENT-{id}`) · `LodgingReservationPaymentHandler` (→ `ConfirmAsync` เดิม · ปล่อย `LODGING-OVERSOLD` ขึ้นไปบันทึกในประวัติ ห้ามกลืน) · `SubscriptionPaymentHandler` (SaaS — **จงใจไม่เรียก resolver บัญชีพัก** เพราะเป็นรายได้ของแพลตฟอร์มไม่ใช่ของผู้เช่า) · `PosOrderPaymentHandler` (AddPayment → Complete · รองรับจ่ายหลายวิธี) · ทุกตัวห้ามเงียบเมื่อหา source ไม่เจอ/บิลถูก void/ยอดไม่ตรง |
| **5** Live + ops | ✅ | ด่านสลับ live + ป้ายโหมด (เฟส 2) · `pages/payment-intents.html` — ค้น/กรอง/**ตรวจสถานะสด**/**ยืนยันด้วยมือ** (บังคับเหตุผล · เดินผ่าน `ApplyChargeAsync` เส้นเดียวกับ webhook ⇒ ต้นทางถูกดำเนินการต่อครบ)/**คืนเงิน**/ดูประวัติ · แจ้งเตือนค้าง Pending ผ่าน `INotificationEngine` (`gateway.payment_stuck`) **ครั้งเดียวต่อรายการ** โดยใช้ประวัติ intent เป็นตัวจำ · refund **ไม่ออกใบลดหนี้อัตโนมัติ** — คำตอบชี้ทางต่อแทน (ดูเหตุผลข้อ 28) |
| **3** บัญชี | ✅ | บัญชี **11340** ในผังมาตรฐาน · `Helpers/GatewayReconciliation` (บริสุทธิ์ + 7 เทสต์) + `GET pay/reconciliation` · **ขา "เงินเข้า" แยกธนาคาร/บัญชีพักด้วย `IGatewayAccountResolver`** — adapter ประกาศ `SettlesDirectlyToBank` เอง (เส้นสลิป = true → Dr ธนาคารตามเดิม · gateway = false → Dr 11340) แล้วส่งผ่าน `ConfirmPaymentAsync(moneyInAccountId:)` → `CreatePaymentRequest.OverridePaymentAccountId` ที่มีอยู่แล้ว · **settlement**: `Helpers/GatewaySettlementMath` (บริสุทธิ์ + 14 เทสต์) + `GatewaySettlementService` (ล็อกต่อ provider · JE Dr ธนาคาร + Dr ค่าธรรมเนียม = Cr 11340 (+ Cr ภ.ง.ด.53 เมื่อเปิดหัก)) + `pages/payment-settlements.html` (ดูตัวอย่างก่อนบันทึกเสมอ) · **WHT บนค่าธรรมเนียม gross-up** ตาม `GatewayFeeWhtMode` |

**สิ่งที่พบระหว่างทำเฟส 5-6:**

27. **เจอทางลัดที่ข้ามชั้นกลางทั้งชั้น** — `POST /api/cms-webhook/{siteId}/payment`
    (ของเดิมก่อนมีการออกแบบนี้) เรียก `ConfirmPaymentAsync` **ตรง ๆ** ⇒ เงินเข้าสำเร็จ
    แต่ **ไม่มีแถวใน `PaymentIntent` เลย**: ไม่โผล่ในหน้ารายการรับชำระ · ไม่เข้าบัญชีพัก
    11340 · ไม่อยู่ในรายงานกระทบยอด — คือ "สองความจริงของ *ลูกค้าจ่ายหรือยัง*" ซึ่งเป็น
    สิ่งที่ทั้งการออกแบบนี้ตั้งใจกำจัด · และมันยืนยันด้วย **HMAC ของ secret ที่เราตั้งเอง**
    ซึ่งเป็นรูปแบบที่เอกสารนี้ปฏิเสธไว้ตั้งแต่ต้น (ต้องยืนยันแบบของเจ้านั้น)
    <br>**ทางแก้ที่เลือก**: ไม่ลบ URL (ส่งออกไปแล้ว แก้ย้อนหลังไม่ได้ — กติกาเดียวกับ
    `/pages/dashboard.html`) แต่ให้เดินผ่าน `RecordExternalSuccessAsync` ซึ่ง
    find-or-create intent แล้วส่งต่อให้ตัวจัดการเดิม ⇒ **ความจริงเดียว** โดยผู้เรียก
    ภายนอกไม่รู้สึกถึงความต่างเลย
28. **refund ห้ามออกใบลดหนี้ให้อัตโนมัติ** — §86/10 บังคับ "เหตุผล" ตาม closed list ·
    ต้องอ้างใบกำกับเดิม · และ `SUM(ใบลดหนี้) ≤ ยอดใบเดิม` · สามอย่างนี้ระบบเดาแทนไม่ได้
    จึงคืนเงินให้จริงแล้ว **บอกขั้นถัดไปให้ชัด** ว่าต้องไปออกใบลดหนี้จากเอกสารต้นทาง
    (ซึ่งมีด่าน §86/10 ครบอยู่แล้ว) — ดีกว่าออกใบที่เหตุผลผิดแล้วผู้ใช้ไม่รู้ตัว
29. **แจ้งเตือนที่ถี่เกินจนถูกเมิน = ไม่ได้แจ้ง** — job เดินทุก 5 นาที ถ้าแจ้งรายการค้าง
    ทุกรอบ ผู้ใช้จะได้ 12 ข้อความ/ชั่วโมงต่อ 1 รายการแล้วเลิกอ่านทั้งหมด · แจ้ง
    **ครั้งเดียวต่อรายการ** โดยใช้ประวัติของ intent เป็นตัวจำ (ไม่ต้องเพิ่มคอลัมน์/แคช
    ที่จะกลายเป็น state ข้าม instance)
30. **ตรวจ "abstraction รั่วไหม" ตามเกณฑ์ผ่านเฟส 6** — `grep` ชื่อเจ้าทั้งเรพ:
    หลุดนอกโฟลเดอร์ adapter **เฉพาะ `Program.cs`** (บรรทัด DI ซึ่งเป็นตะเข็บที่ตั้งใจ)
    · CSP มาจาก `CspNeeds` · วิธีจ่ายมาจาก `Capabilities` · ธนาคาร vs บัญชีพักมาจาก
    `SettlesDirectlyToBank` · หน้าเว็บสร้างตัวเลือกจาก API ทั้งหมด ⇒ **ตะเข็บครบ**

**⚠️ เฟส 6 (adapter ตัวที่สอง) — ยังไม่ทำ และนี่คือเหตุผล:**

การเขียน adapter ของเจ้าที่ **ทดสอบกับ sandbox จริงไม่ได้** = เดา endpoint · เดาโครง
payload · เดารูปแบบ webhook แล้วเอาตัวเลขที่ได้ไปลงเป็นรายการบัญชีจริง ซึ่งเอกสารนี้
และ CLAUDE.md ห้ามไว้ตรง ๆ ("ห้าม merge ครึ่ง ๆ กลาง ๆ" · "feature ที่ยังไม่มีจริง
ห้ามเขียนว่ามีแล้ว") · สิ่งที่ทำแทนคือ**ตรวจว่าตะเข็บพร้อมรับเจ้าที่สอง** (ข้อ 30)

**เช็กลิสต์สำหรับคนที่จะเขียน adapter ตัวที่สอง** — ถ้าต้องแก้ไฟล์นอก
`Services/Payments/Providers/**` นอกจาก 1 บรรทัดใน `Program.cs` แปลว่า abstraction รั่ว:
1. `ProviderCode` · `Capabilities` (วิธีจ่าย/refund/webhook)
2. `CspNeeds` + `ClientScriptUrl` ถ้าต้องโหลดสคริปต์ฝั่งเบราว์เซอร์
3. `SettlesDirectlyToBank` (เกือบทุกเจ้า = `false` — ค่า default ถูกอยู่แล้ว)
4. `CreateChargeAsync` / `GetChargeAsync` / `RefundAsync`
5. `VerifyWebhookAsync` — **ยืนยันแบบของเจ้านั้น** แล้ว resolve intent จาก metadata
   ของ charge ไม่ใช่จาก URL
6. `TestConnectionAsync` — ต้องตรวจถึงขั้น **webhook มาถึงจริง**
7. named `HttpClient` + 1 บรรทัด DI ใน `Program.cs`

**สิ่งที่พบระหว่างทำเฟส 4 (เริ่ม):**

19. **การส่งต่อให้ทางเข้าเดิมต้องทำ *นอก* ธุรกรรมของการยืนยันเงิน** — การบันทึกว่า
    "เงินเข้าแล้ว" เป็นข้อเท็จจริงที่ต้องเก็บให้ได้เสมอ · ถ้ารวมไว้ในธุรกรรมเดียวกันแล้ว
    orchestrator ปลายทางล้ม (สต็อกไม่พอ/e-Tax ล่ม) การบันทึกจะถูกกลับด้วย ⇒ **ลูกค้า
    จ่ายแล้วระบบลืมสนิท** ซึ่งกู้ยากกว่าออเดอร์ที่ค้างอยู่แต่รู้ว่าจ่ายแล้ว
20. **ต้นทางที่ยังไม่มีตัวจัดการต้องดัง ไม่ใช่เงียบ** — ไม่งั้นระบบกลายเป็น defect class
    "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" ทันที (เงินเข้าสำเร็จ · แถวถูกต้อง · แต่ออเดอร์
    ค้างชำระตลอดกาล) · บันทึกทั้ง log และ **ประวัติของ intent** ที่ผู้ใช้เปิดดูได้

**สิ่งที่พบระหว่างทำเฟส 3 (ต่อ · JE + settlement):**

21. **กลไกที่ต้องใช้มีอยู่แล้วครบ — ขาดแค่คนส่งค่าให้** `Payment.OverridePaymentAccountId`
    มีมาก่อนหน้านี้ พร้อม doc-comment ที่เขียนคำว่า *"clearing"* ไว้เองด้วยซ้ำ และ
    `CreatePaymentJournalAsync` ใช้เป็นลำดับที่ 1 ของการหาผังขาเงินสดอยู่แล้ว ⇒ งานจริง
    คือ **ต่อสาย** ไม่ใช่เขียนกลไกใหม่ · ก่อนสร้างอะไรที่ดูเหมือนต้องมี ให้ `grep`
    หาของเดิมก่อนเสมอ (defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" ในทิศกลับ)
22. **ตัวตัดสิน "เงินเข้าธนาคารแล้วหรือยัง" ต้องอยู่ที่ adapter ไม่ใช่ลิสต์ตรงกลาง** —
    ถ้าเก็บเป็นรายชื่อเจ้าที่ไหนสักที่ การเพิ่มเจ้าใหม่จะต้องแก้ไฟล์นอกโฟลเดอร์ adapter
    = ผิดเกณฑ์ผ่านเฟส 6 · จึงเป็น `IPaymentProvider.SettlesDirectlyToBank` ที่ default
    เป็น `false` — **fail-safe ทางบัญชี**: ลงบัญชีพักเกินไว้เห็นได้และล้างได้ตอน
    settlement ส่วนลงธนาคารเกินไว้ = กระทบยอดพังเงียบตลอดไป
23. **resolver ตัวนี้ห้ามอยู่ใน `PaymentIntentService`** — ตัวนั้นรับ
    `IEnumerable<IPaymentCompletionHandler>` ส่วน handler ต้องเรียก resolver ⇒ **วงกลม DI**
    ที่พังตอนรันไม่ใช่ตอนคอมไพล์ · แยกเป็น `IGatewayAccountResolver` ของตัวเอง
    (`tools/di_cycle_check.py` มีไว้จับเคสนี้พอดี)
24. **ยอดที่โอนเข้าจริงต้องเป็น "ตัวตั้ง" และไม่ตรงต้องบล็อก** — เขียนให้ระบบเฉลี่ย
    ส่วนต่างเข้าค่าธรรมเนียมให้ลงตัวนั้นง่ายกว่ามาก แต่จะได้ JE ที่ยอดธนาคาร**ไม่ตรง
    สเตทเมนต์** ซึ่งกระทบยอดไม่ได้ตลอดไปและไม่มีใครรู้ว่าเริ่มเพี้ยนตั้งแต่เมื่อไร ·
    ผลต่างเป็น**ข้อมูล** ไม่ใช่สิ่งที่ต้องกลบ (ยอมรับเฉพาะเศษ ≤ ฿0.01 ซึ่งเป็นการปัด
    ของผู้ให้บริการเอง)
25. **หัก ณ ที่จ่ายบนค่าธรรมเนียมต้อง gross-up** — ผู้ให้บริการหักค่าธรรมเนียมไป
    **เต็มจำนวนแล้ว** ยอดนั้นคือ *สุทธิ* ที่เขาได้รับ ⇒ ฐานภาษีคือยอดก่อนหัก
    (`ภาษี = สุทธิ × 3/97`) ไม่ใช่ 3% ของสุทธิ · คิดผิดทาง = ฐานบน 50 ทวิ ต่ำกว่าจริง
    _(⚠️ บันทึกไว้ให้ตรงความจริง: **สูตรทางเลือก `round(สุทธิ/0.97, 2)` ไม่ได้ผิด** —_
    _ไล่ทุกสตางค์ ฿0.01–฿5,000 แล้วต่างกัน 0 ยอด · ที่เลือกรูป "ก่อนหัก = สุทธิ + ภาษี"_
    _เพราะทำให้ `ก่อนหัก − ภาษี = สุทธิ` จริง**โดยนิยาม** ไม่ใช่เพราะไปแก้บั๊กที่มีอยู่ ·_
    _ตามกฎ CLAUDE.md "ลืม AwayFromZero ≠ ยอดผิด — ต้องพิสูจน์ก่อนเรียกว่าบั๊ก")_
26. **ดึงรอบโอนอัตโนมัติ = ยังไม่ทำ และจดไว้ว่ายังไม่ทำ** — `IPaymentProvider` ยังไม่มี
    `ListSettlementsAsync` และไม่มีเจ้าไหน implement · เส้นหลักคือ**บันทึกจากสเตทเมนต์**
    ซึ่งมีตัวตั้งที่ตรวจสอบได้จริง · การเขียน adapter ยิง endpoint รายงานยอดโอนโดยที่
    ทดสอบกับของจริงไม่ได้ = เดาโครงข้อมูลแล้วเอาไปลงบัญชี (ห้ามตามกฎเหล็ก #3 ท้ายข้อ 2)

**สิ่งที่พบระหว่างทำเฟส 3:**

16. **ต้องแยก "ต่างเพราะยังไม่ถึงรอบโอน" ออกจาก "ต่างแบบอธิบายไม่ได้"** — เงินเข้าธนาคาร
    T+n เป็นสภาพปกติทุกวัน ถ้านับรวมเป็นผลต่าง รายงานจะแดงตลอดจนไม่มีใครดู
    แล้วผลต่างของจริง (ค่าธรรมเนียมไม่ตรงที่คาด/เงินหาย) จะถูกกลบ
17. **ค่าธรรมเนียมที่ยังเป็นตัวประมาณต้องติดป้ายบอก** (`FeeIsEstimated`) — ตัวเลขประมาณ
    ที่ไม่ติดป้ายจะถูกอ่านเป็นตัวจริงแล้วนำไปตัดสินใจผิด
18. **ยอดที่คืนเงินไปแล้วไม่นับเป็น "ยังไม่ถึงรอบโอน"** — มันจะไม่มีวันโอนเข้า

**สิ่งที่พบระหว่างทำเฟส 2 (ต่อ · ฝั่งหน้าเว็บ + job):**

11. **ป้าย "โหมดทดสอบ" ต้องอยู่บนหน้าจ่ายของ *ลูกค้า*** ไม่ใช่แค่หน้าตั้งค่าของร้าน —
    ไม่งั้นร้านทดลองเองแล้วเข้าใจว่าเก็บเงินได้จริง
12. **ปุ่มเปิด live ถูกล็อกโดย `canEnableLive` ที่ *เซิร์ฟเวอร์* คำนวณ** — หน้าเว็บไม่ตัดสินเอง
    ไม่งั้นกลายเป็นด่านชุดที่สองที่ drift (defect class "สำเนามือฝั่ง JS")
13. **หน้ากลับจาก 3-D Secure ห้ามเชื่อ query string** — ใครก็เติม `?status=success` เองได้ ·
    ต้องถามสถานะจากเซิร์ฟเวอร์ (ซึ่งไปถาม provider สดถ้ายังค้าง)
14. **poll ต้องถอยห่าง** 2s→5s→10s แล้วหยุดเมื่อจบ — ลูกค้าใช้เวลาสแกนจ่ายเป็นนาที
    การถามทุกวินาทีไม่ช่วยอะไรแต่เปลืองทั้งเซิร์ฟเวอร์เราและโควตา API ของ provider
15. **job กระทบยอดคือตาข่ายรับ ไม่ใช่ของฟุ่มเฟือย** — webhook หายได้จริงหลายทาง
    (ส่งพลาด · เซิร์ฟเวอร์รีสตาร์ต · ยังไม่ตั้ง URL · ไฟร์วอลล์) และอาการที่ผู้ใช้เจอคือ
    "ลูกค้าจ่ายแล้วออเดอร์ยังค้าง" ซึ่งเจ้าของร้านไม่รู้จนลูกค้าโทรมา ·
    รายการ **โหมดจริง** ที่ค้างเกิน 30 นาทีต้อง log ระดับ warning

**สิ่งที่พบระหว่างทำเฟส 2:**

6. **CSP เป็นจุดที่ abstraction รั่วได้ง่ายที่สุด** — เป็น allow-list ที่ต้องระบุโดเมนตรง ๆ
   ถ้าเขียนใน `SecurityMiddleware` การเพิ่มเจ้าใหม่จะต้องแก้ไฟล์นอกโฟลเดอร์ adapter
   = ผิดเกณฑ์ผ่านเฟส 6 ของเอกสารนี้เอง · checker จับได้ทันที (3 จุด) →
   ให้ adapter **ประกาศโดเมนที่ต้องใช้เอง** (`IPaymentProvider.CspNeeds`) แล้ว
   middleware ประกอบ CSP จากรายการนั้น ⇒ เพิ่มเจ้าใหม่ไม่ต้องแตะ CSP อีกเลย
7. **สถานะที่ไม่รู้จักต้องเป็น `Pending` ไม่ใช่ `Failed`** — เดาว่าล้มเหลวแล้วปิดใบทิ้งทั้งที่
   เงินอาจเข้าจริง แพงกว่าการรอต่ออีกนิด
8. **webhook ต้องตอบ 200 เสมอแม้ปฏิเสธ** — ตอบ error = provider retry ไม่รู้จบ ·
   สิ่งที่เกิดขึ้นถูกบันทึกฝั่งเราแล้ว
9. **"ทดสอบผ่าน" ต้องรวม webhook มาถึงจริง** — คีย์ถูกแต่ลืมตั้ง URL ในแดชบอร์ดคือเคสที่
   พบบ่อยที่สุด และเป็นเคสที่ลูกค้าจ่ายเงินแล้วออเดอร์ไม่อัปเดตโดยเจ้าของร้านไม่รู้ตัว ·
   **เปลี่ยนคีย์แล้วต้องล้างผลทดสอบเดิม** ไม่งั้นใส่คีย์ผิดแล้วยัง "ผ่าน" อยู่จากผลเก่า
10. **คีย์ที่เว้นว่างตอนบันทึก = ไม่เปลี่ยน ไม่ใช่ล้างทิ้ง** — หน้าเว็บไม่เคยได้ secret กลับไป
    จึงส่งกลับมาไม่ได้ · ถ้าตีความว่างเป็น "ล้าง" ผู้ใช้จะลบคีย์ตัวเองทุกครั้งที่แก้ชื่อที่แสดง

**สิ่งที่พบระหว่างทำเฟส 1:**

1. **"ล้มเหลว/หมดอายุ" ต้องเดินหน้าไป "สำเร็จ" ได้** — provider ตัดสิน timeout ที่ 10 นาที
   แต่ธนาคารยืนยันการโอนที่นาทีที่ 11 เกิดขึ้นจริง · ถ้าห้ามไว้ = ลูกค้าจ่ายแล้วระบบไม่รับ
   ซึ่งเป็นเคสร้องเรียนที่แก้ยากที่สุด (ต้องคืนเงินแล้วให้จ่ายใหม่)
2. **webhook ซ้ำต้องเป็น no-op ไม่ใช่ error** — ตอบ error ให้ provider = มัน retry ไม่รู้จบ
3. **ล็อกสองระดับคนละคีย์** — ตอนถามว่า "มี intent อยู่แล้วไหม" ต้องล็อกที่ **source**
   (สองแท็บกดจ่ายพร้อมกันต้องได้ QR ใบเดียว) · ตอนเปลี่ยนสถานะล็อกที่ **intent**
   (webhook กับ job ต้องไม่เขียนทับกัน) · ใช้คีย์เดียวกันจะล็อกกว้างเกินจนจ่ายพร้อมกัน
   คนละบิลไม่ได้
4. **เรียก provider หลังบันทึกแถวแล้วเท่านั้น** — ถ้าสร้าง charge สำเร็จแต่เราล้มก่อนบันทึก
   จะมี charge ลอยที่ผูกกับอะไรไม่ได้ = เงินลูกค้าหายในระบบเรา
5. **`checker` รุ่นแรกฟ้องผิด 16 จุด** — จับ "ชื่อเจ้า" เปล่า ๆ แล้วไปโดน `stripe`
   (สลับสีแถวตาราง) และตารางคำสำคัญ OCR/สเตทเมนต์ธนาคารที่มีชื่อ gateway อย่างถูกต้อง ·
   ตามกฎ "checker ที่ฟ้องผิด = checker ที่พังแล้ว" เปลี่ยนมาจับเฉพาะ **ร่องรอยการเชื่อมต่อ**
   (โดเมน API/CDN · prefix ของคีย์) ซึ่งโผล่โดยบังเอิญไม่ได้ + เพิ่ม regression guard
   ใน negative test

**ต้องสร้าง checker**: `tools/payment_provider_boundary_check.py` — ฟ้องเมื่อไฟล์นอก
`Services/Payments/Providers/**` อ้างชื่อ provider (`omise`, `api.omise.co`, `pkey_`, `skey_`)
หรือเมื่อ `ChargeRequest`/DTO ใดมี property ชื่อคล้ายเลขบัตร · negative test ทั้งสองแบบ

---

## 8. คำถามที่เจ้าของระบบต้องตัดสิน

1. **บัญชี Omise ของใคร** — ลูกค้าแต่ละรายสมัคร Omise เอง (ตั้งค่าคีย์ของตัวเอง · เงินเข้า
   บัญชีเขาตรง · เราไม่แตะเงิน = ไม่ต้องขอใบอนุญาต) **หรือ** เราเป็น platform รับแทน
   (ต้องพิจารณา พ.ร.บ.ระบบการชำระเงิน 2560 — ผู้ให้บริการรับชำระเงินแทนต้องขอใบอนุญาต ธปท.)
   → **เสนอทางแรก** และออกแบบข้างบนบนสมมติฐานนี้
2. **WHT บนค่าธรรมเนียม** — default `None` แล้วเตือน หรือบังคับถามตอนเปิดใช้
3. **ค่าบริการ SaaS ของเราเอง** ผ่าน gateway เดียวกัน (บัญชี Omise ของแพลตฟอร์ม) — ใช้
   `PaymentProviderConfig` ของ **tenant ผู้ให้บริการ** (§6.1 ACCOUNT_STRUCTURE) ได้เลย
   โดยไม่ต้องมีโค้ดพิเศษ
4. **POS แสดง QR บนจอ** ทำในเฟสแรกไหม — ร้านส่วนใหญ่มี QR ธนาคารตั้งโต๊ะอยู่แล้ว (ไม่มีค่า
   ธรรมเนียม) ประโยชน์ของ gateway ที่ POS คือ**ยืนยันอัตโนมัติ**ไม่ต้องดูสลิป — ให้เป็นตัวเลือก
   ต่อเครื่อง ไม่บังคับ
5. **ทำเป็น add-on** — การเชื่อม gateway เข้าข่าย `AddOnCodes` ระดับบริษัท (เช่น
   `payments.gateway` ค่าเหมารายเดือน หรือฟรีแล้วคิดต่อรายการที่ยืนยันสำเร็จผ่าน
   `UsageEvent`) — ทีม C เตือนว่าลูกค้าจ่ายค่าธรรมเนียม Omise อยู่แล้ว ถ้าเราคิดซ้อนอีกชั้น
   ต้องต่ำมาก (≤ ฿1/รายการ) หรือรวมในแพ็กเกจ
