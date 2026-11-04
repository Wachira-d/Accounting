# ความพร้อมส่งเว็บให้ Omise ตรวจ (Omise merchant review) — รอบ 232 "Public checkout + นโยบาย 2 ภาษา" (2026-11-03)

> คำขอเจ้าของ: Omise แจ้งว่า *"Add Shopping Cart and Checkout Page — checkout page must be fully functional. The entire payment process must be completed
> directly within your website or application. The checkout flow cannot require customers to contact you or be redirected to external platforms (such as LINE,
> Facebook Messenger, etc.) to finalize the payment."* + เจ้าของถาม: จะพัฒนาตรงไหน · เปิดหน้าทดลองจ่ายของแอดมินเป็น public ชั่วคราวดีไหม · ครบลูปหรือยัง
>
> **คำตัดสินผู้ประสานงาน (232-Checkout-2):** ไม่เปิดหน้าทดลองจ่ายของแอดมิน (`platform-pay-test.html` = ยอดคงที่ ไม่มีตะกร้า/ไม่มีสินค้า — ไม่ผ่านเกณฑ์ "cart + checkout") ·
> สร้าง **หน้าซื้อแพ็กเกจสาธารณะของ NextAcc** เป็น cart + checkout จริงที่จบในเว็บ: `/pricing.html` → `/checkout.html` → `/checkout-success.html`
> (โค้ดครบในรอบนี้ · **ยังไม่ได้คอมไพล์ในเครื่องนี้** — ไม่มี .NET SDK · ตรวจด้วย checker ใน `tools/` แล้ว)
>
> อ่านประกอบ: `erp-review/2026-11-02/PLATFORM_OMISE_AUDIT.md` (ช่องทาง Omise ของแพลตฟอร์ม · ด่าน live · webhook) · `DOCUMENT_FLOW.md` §2.12 · `ACCOUNT_STRUCTURE.md` §6.2i

## 1. ลูปที่ Omise จะเห็น (ทั้งหมดในเว็บเรา — ไม่มีขั้นที่ต้องติดต่อเรา/LINE)

```
/pricing.html  ──เลือกแพ็กเกจ+รอบบิล (+คูปอง) ใส่ตะกร้า──▶  /checkout.html
                                                            │ 1. บัญชี: สร้างบัญชี+บริษัท (api/auth/register) หรือเข้าสู่ระบบ (api/auth/login) ในหน้าเดียวกัน
                                                            │ 2. ยอดของบริษัท: GET api/public/checkout/companies/{id}/quote (ราคาพิเศษ+คูปอง — ตัวเดียวกับในระบบ)
                                                            │ 3. ติ๊กยอมรับ "ข้อกำหนด + นโยบายการยกเลิกและคืนเงิน" (ข้อความ/ฉบับจากเซิร์ฟเวอร์ · ลิงก์เปิดอ่านได้)
                                                            │ 4. POST …/orders ⇒ รายการค่าแพ็กเกจ (ยอดจากเซิร์ฟเวอร์ · resume รายการค้าง · อีเมลยืนยัน)
                                                            │    ชนิดคำสั่งซื้อตัดสินก่อน (`PublicCheckoutPolicy.DecideOrder` · ฝ่ายค้าน B1): ทดลองใช้/หมดอายุ = ซื้อใหม่ ·
                                                            │    Active แพ็กเกจ+รอบเดิม = ต่ออายุ (`SubmitPaymentAsync` ราคาต่ออายุ) · อัปเกรด/รอบยาวขึ้น = จ่ายก่อนมีผล ·
                                                            │    ดาวน์เกรด/รอบสั้นลง = ปฏิเสธก่อนมี side-effect ("ลดแพ็กเกจได้ที่หน้าแพ็กเกจในระบบ — มีผลสิ้นรอบ")
                                                            │ 5. PayWidget (publicBase = …/orders/{paymentId}) — บัตร (โทเคนจาก Omise.js) / พร้อมเพย์ QR ในหน้าเดียวกัน
                                                            ▼
/checkout-success.html ── poll GET …/orders/{paymentId} ── ผลลัพธ์จากเซิร์ฟเวอร์: เปิดสิทธิ์แล้ว / ได้เงินรอตรวจ / ทดสอบ / ยังไม่จ่าย / ล้มเหลว + ใบเสร็จ PDF
```

- เงินเข้า (webhook/poll ของ Omise) ⇒ `SubscriptionPaymentHandler` เดิม ⇒ `ReviewPaymentAsync(systemConfirmed)` ⇒ ต่ออายุแพ็กเกจ + **ใบเสร็จ/ใบกำกับของแพลตฟอร์ม** (`PlatformBillingDocumentIssuer`) ⇒ ดาวน์โหลดจากหน้าสำเร็จ (`…/orders/{paymentId}/receipt`)
- โหมดทดสอบ (คีย์ test ของ Omise ระหว่างรอเปิดบัญชี): ลูปเดียวกันทั้งหมด + ป้าย **"🧪 โหมดทดสอบ"** ทุกหน้า (`PublicCheckoutPolicy.Decide` → `isTestMode`) · เงินทดสอบ**ไม่มีผลทางธุรกิจ** (232-Omise-2: ไม่เปิดสิทธิ์ ไม่ออกใบเสร็จ — หน้าสำเร็จบอกตรง ๆ `TestPaid`)
- **เริ่มชำระเส้นเดียวทั้ง Live/Test** (ฝ่ายค้าน S1): `IPlatformPaymentService.StartSubscriptionPaymentAsync(…, allowTestMode: avail.IsTestMode)` — ด่านเดิมครบ (ยอดตรงราคาปัจจุบัน · ไม่ซ้อนสลิป/พัก · วิธีที่เปิด) ·
  ต่างกันเฉพาะด่านช่องทาง `PlatformPayAvailability.StartRefusal` ยอมให้โหมดทดสอบผ่าน · ผู้เรียกเดิม (ศูนย์บิล · ลิงก์ชำระ · ตัดบัตรอัตโนมัติ) ไม่ส่ง ⇒ โหมดทดสอบถูกปฏิเสธเหมือนเดิม (กติกา `required_call_site_check` ห้ามส่ง)
- **สวิตช์ปิด** (ฝ่ายค้าน S2): แคตตาล็อก · ยอดตะกร้า · สร้างรายการ · เริ่มชำระ ⇒ 404 + เหตุผล · **สถานะ intent / สถานะคำสั่งซื้อ / ใบเสร็จของรายการเดิมยังเปิด (ตั้งใจ)** — คนที่จ่ายก่อนปิดสวิตช์ต้องดูผลและโหลดใบเสร็จได้ · ทุกเส้นยังต้องเป็นเจ้าของบริษัท

## 2. Checklist ตามข้อกำหนด Omise

| ข้อกำหนด | สถานะ | ที่อยู่ / หลักฐาน | URL ที่ส่งให้ Omise |
|---|---|---|---|
| Shopping cart (เลือกสินค้า · จำนวน/รอบ · ส่วนลด · ยอดรวม) | ✅ โค้ดครบ | `wwwroot/pricing.html` — ตะกร้า (แพ็กเกจ + รอบบิล + คูปอง) · ยอดจาก `GET api/public/checkout/quote` (ราคาตั้ง) · เก็บตะกร้าใน localStorage + query | `{AppBaseUrl}/pricing.html` |
| Checkout page ใช้งานได้จริง (ข้อมูลผู้ซื้อ · สรุปยอด · ยืนยัน) | ✅ โค้ดครบ | `wwwroot/checkout.html` — สมัคร/เข้าสู่ระบบในหน้า · เลือกบริษัท · ยอดสุดท้าย `…/companies/{id}/quote` · ติ๊กนโยบาย · `POST …/orders` | `{AppBaseUrl}/checkout.html` |
| ชำระเงินจบในเว็บ (ไม่พาออก LINE/Messenger/ติดต่อเรา) | ✅ | `pay-widget.js` ในหน้า checkout: บัตร (Omise.js token — เลขบัตรไม่ผ่านเซิร์ฟเวอร์เรา) · พร้อมเพย์ QR · 3-D Secure กลับมาที่ `/checkout-success.html` แล้วถามสถานะจากเซิร์ฟเวอร์ | — |
| หน้าสำเร็จ/สถานะคำสั่งซื้อ | ✅ | `wwwroot/checkout-success.html` — poll 2→10 วิ สูงสุด 15 นาที · ผลลัพธ์ตัดสินที่เซิร์ฟเวอร์ (`PublicCheckoutPolicy.Resolve`) | `{AppBaseUrl}/checkout-success.html?company=…&payment=…` |
| ใบเสร็จ/หลักฐานการชำระ | ✅ (เส้นเดิม) | `PlatformBillingDocumentIssuer` ออกใบเสร็จ/ใบกำกับเมื่อเงินเข้า · ดาวน์โหลด `GET …/orders/{paymentId}/receipt` · อีเมลยืนยันคำสั่งซื้อตอนสร้างรายการ (`IEmailService` — ต้องตั้งอีเมลระบบ) | — |
| Terms of service | ✅ | `/terms.html` (แอดมินแก้ได้ที่ `/admin/legal.html` · ลิงก์บนหน้า checkout ก่อนจ่าย) · **2 ภาษา** `?lang=en` (ฉบับเดียวกัน) | `{AppBaseUrl}/terms.html` · `…?lang=en` |
| Refund / cancellation policy | ✅ | `/refund-policy.html` (ฉบับตั้งต้น 1.0 ในโค้ด — **แอดมินควรเผยแพร่ฉบับของตัวเอง** · ด่าน live ของแพลตฟอร์มต้องการ `RefundPublished`) · 2 ภาษา | `{AppBaseUrl}/refund-policy.html` |
| Privacy policy | ✅ | `/privacy.html` · 2 ภาษา · ตัวตนผู้ควบคุมข้อมูลจาก SiteSettings (ต้องกรอก "ใบเสร็จค่าบริการ" ให้ครบ ไม่งั้นหน้าเตือน) | `{AppBaseUrl}/privacy.html` |
| Contact page (ช่องทางติดต่อบนเว็บ) | ✅ (มีอยู่แล้ว) | `/contact.html` — ลิงก์จาก pricing/checkout/success · ข้อมูลจาก SiteSettings (ContactPhone/Email/Address) — **ต้องกรอก** | `{AppBaseUrl}/contact.html` |
| ป้ายโหมดทดสอบเมื่อยังใช้คีย์ test | ✅ | แบนเนอร์ทุกหน้า + ปุ่ม "ชำระเงิน (ทดสอบ)" + หน้าสำเร็จ `TestPaid` + widget `isTestMode` | — |
| สวิตช์เปิด/ปิดหน้าสาธารณะ | ✅ | `SiteSettings.PublicCheckoutEnabled` (ค่าเริ่มต้น **ปิด**) · แอดมิน: `/admin/site-settings.html` แท็บ "บริการ" การ์ด "หน้าซื้อแพ็กเกจสาธารณะ" (`api/admin/public-checkout` · audit chain) | — |
| SSL/HTTPS บนโดเมนจริง | ❓ ตรวจไม่ได้จากโค้ด | ขึ้นกับ hosting/reverse proxy ของเจ้าของ — Omise.js และ 3-D Secure ต้องการ https · ตั้ง `AppBaseUrl` เป็น https | — |
| สลับ Test → Live | ✅ (เส้นเดิม) | `/admin/platform-gateway.html` → ด่าน `PaymentLiveGate` (ทดลองจ่ายผ่าน · webhook จริง · นโยบายคืนเงินเผยแพร่) → `PlatformLiveConfirmedAt` · หน้าสาธารณะอ่านค่าเดียวกัน (Live แต่ยังไม่ผ่านด่าน ⇒ ปิดรับเงิน ไม่ตกไปโหมดทดสอบเงียบ) | — |
| ส่วนเสริม/โควตาเพิ่มในตะกร้า | ❌ ยังไม่ทำ (ดู §4) | ซื้อได้หลังเข้าระบบที่ hub "แพ็กเกจและการชำระเงิน" (เส้นเดิม `platform-pay/addons` · `quota-addons`) | — |

## 3. สิ่งที่เจ้าของต้องทำเอง (ตามลำดับ)

1. **ตั้ง "URL ของระบบ"** (`/admin/site-settings.html` แท็บทั่วไป → `AppBaseUrl` = `https://<โดเมนจริง>`) — URL ที่ส่งให้ Omise · ลิงก์ในอีเมลยืนยัน · `returnUrl` หลัง 3-D Secure · webhook (รอบ 232 B2) ใช้ค่านี้ทั้งหมด
2. **ตั้งช่องทาง Omise ของแพลตฟอร์ม** (`/admin/platform-gateway.html`) ด้วยคีย์ **test** ก่อน · เปิดวิธี Card + PromptPay · ตรวจว่า `GET /api/public/checkout/catalog` ตอบ `isTestMode=true` + `methodOptions` มี 2 วิธี
3. **เปิดสวิตช์** "หน้าซื้อแพ็กเกจสาธารณะ" (การ์ดใหม่ใน site-settings แท็บบริการ) — การ์ดแสดง URL สองอันที่จะส่งให้ Omise + ป้ายโหมด
4. **ตรวจแพ็กเกจ** (`/admin/plans.html`): แพ็กเกจที่ขายต้อง `IsActive` + ราคา > 0 + ไม่ใช่ฟรีถาวร (ฟรีถาวร/ราคา 0 ไม่ขึ้นหน้าขาย — ไปทางสมัครธรรมดา)
5. **เนื้อหานโยบาย**: เผยแพร่นโยบายการยกเลิกและคืนเงินฉบับของตัวเอง (`/admin/legal.html` แท็บ ↩️) · กรอกตัวตนผู้ให้บริการ (ชื่อนิติบุคคล/ที่อยู่/เลขผู้เสียภาษี/อีเมล) ·
   **ฉบับภาษาอังกฤษ — มีคำแปลตั้งต้นแล้ว (รอบ 232 ต่อ):** ฉบับตั้งต้นของระบบแสดง EN ให้อัตโนมัติ · ถ้าเผยแพร่ฉบับไทยของตัวเองแล้ว กดปุ่ม "🌐 ใส่คำแปลตั้งต้น (อังกฤษ)"
   ในช่อง EN ของร่าง/ฉบับปัจจุบัน แล้ว**ตรวจ/ปรับให้ตรงกับไทยที่แก้ไป**ก่อนบันทึก (ฉบับที่เผยแพร่เองไม่ถูกเติมอัตโนมัติ — ไม่ใส่ = ผู้อ่านเห็นไทย + "English version not available yet") ·
   ข้อความดิบสำหรับคัดลอก/ส่งนักกฎหมายตรวจ: `erp-review/2026-11-03/legal-en/` (privacy · terms · refund + แม่แบบร้าน shop-*) · ไทยเป็นฉบับผูกพัน (ประโยคท้ายทุกฉบับ)
6. **ข้อมูลติดต่อ** (site-settings: โทร/อีเมล/ที่อยู่) — หน้า `/contact.html` และโทเคนในนโยบายอ่านจากที่เดียวกัน
7. **อีเมลระบบ** (site-settings → อีเมลระบบ) — อีเมลยืนยันคำสั่งซื้อส่งผ่านตัวนี้ (ไม่ตั้ง = ไม่ส่ง · log warning · คำสั่งซื้อยังอยู่)
8. ทดลองครบลูปด้วยบัตรทดสอบของ Omise (`4242 4242 4242 4242`) + พร้อมเพย์ทดสอบ → หน้าสำเร็จต้องขึ้น "ชำระสำเร็จ (โหมดทดสอบ)" · ไม่เปิดสิทธิ์ (ถูกต้องตาม 232-Omise-2)
9. ส่ง URL ให้ Omise: `/pricing.html` · `/checkout.html` · `/terms.html` · `/refund-policy.html` · `/privacy.html` · `/contact.html`
10. Omise อนุมัติ → ใส่คีย์ live → ผ่านด่าน `PaymentLiveGate` ในหน้าแอดมิน → `PlatformLiveConfirmedAt` ⇒ หน้าสาธารณะรับเงินจริงอัตโนมัติ (ป้ายทดสอบหายเอง — ไม่ต้องแก้อะไรเพิ่ม)

## 4. ที่ยังไม่ทำ + เหตุผล

| เรื่อง | เหตุผล | ทางไปต่อ |
|---|---|---|
| ส่วนเสริม (add-on) / โควตาเพิ่มในตะกร้าสาธารณะ | ต้องมี `CompanyFeature`/`CompanyQuotaAddOn` ของบริษัทก่อน (ยอด = `AmountDueAsync`/`QuoteAsync` รายบริษัท) และ Omise ต้องการแค่ cart+checkout ที่ใช้งานได้ — แพ็กเกจ+รอบบิล+คูปองเพียงพอ · ใส่พร้อมกันในรอบเดียวจะแตะ `Services/Payments/**` ที่ทีม Omise กำลังแก้ | ซื้อหลังเข้าระบบที่ hub (เส้นเดิม) · รอบถัดไป: บรรทัดที่สองของตะกร้าผ่าน `IQuotaAddOnPurchaseService.CreateAsync` หลังสร้างบริษัท |
| Db test ของ `PublicCheckoutService` | ยังไม่มี — ล็อกด้วย `required_call_site_check` (กติกา `_R232_WHY` · รวมลำดับ "ตัดสินชนิดก่อน side-effect" + จุดเรียกเริ่มชำระเดียว) + pure test ของ `PublicCheckoutPolicy`/`PlatformPayAvailability.StartRefusal` แทน | CI (`claude/**`) คอมไพล์ · ทดลองจริงตาม §3 ข้อ 8 · รอบถัดไป: Db test สองทิศของ `CreateOrderAsync` (ต่ออายุ/ดาวน์เกรดไม่มี `ScheduledPlan`) |
| ~~**Rate-limit ของการสมัครสมาชิก (`api/auth/register`) ยัง in-memory**~~ (ฝ่ายค้าน S4) — **ปิดแล้ว รอบ 235 (DECISIONS 235-X2)** | ตรวจซ้ำ: เส้น auth นับที่ DB อยู่แล้วแต่ fail-open + ต่อ IP อย่างเดียว · รอบ 235: `Helpers/AuthRateLimitPolicy` — สมัครบัญชี (เส้นของ checkout) + ลืม/ตั้งรหัสผ่าน = `IChatRateLimiter` ต่อ IP (IPv6 /64) **fail-closed** ข้าม instance · ถังทั้งระบบ = เตือนผู้ดูแลแพลตฟอร์ม (ไม่ปฏิเสธ — กันผู้โจมตีปิดการสมัครของทุกคน) · ตารางนับใช้ไม่ได้ = 503 "ระบบไม่พร้อมรับการสมัครชั่วคราว" · ล็อกอินคง fail-open | เทสต์ `AuthRateLimitRound235Tests` + Db `AuthRateLimitSharedDbTests` (สองเครื่องนับถังเดียวกัน) · ค่าตั้ง `RateLimit:Register:*` |
| คูปองบนการต่ออายุ | เส้นต่ออายุ (`SubmitPaymentAsync`) ไม่มีคูปอง — ใส่คูปองตอนต่ออายุ ⇒ ปฏิเสธพร้อมเหตุ (ไม่เพิกเฉยเงียบ) | ติดต่อแอดมินให้แก้ยอด (`OverridePaymentAmountAsync`) · หรือรอบถัดไปเปิดคูปองบนเส้นต่ออายุ |
| แจ้งเตือน "มีการชำระเงินรอตรวจสอบ" ตอนสร้างรายการต่ออายุ | `SubmitPaymentAsync` (เส้นเดิม · ไฟล์ทีมอื่น) แจ้งเจ้าของ/แอดมินของบริษัททุกครั้งที่สร้างรายการ ทั้งที่ยังไม่จ่าย | รอบถัดไป: เพิ่มพารามิเตอร์ไม่แจ้งใน `SubscriptionService` หรือเปิดเส้นต่ออายุผ่าน `RequestPlanPaymentAsync` |
| ~~ฉบับอังกฤษของแม่แบบนโยบายร้าน (CMS)~~ — **ทำแล้ว รอบ 232 ต่อ** | `LegalDocumentDefaults.ShopTemplateEn` (โหมดแม่แบบแสดงคำแปลอัตโนมัติ · ร้านเขียนไทยเอง ⇒ EN เฉพาะที่ร้านเขียน) | ให้นักกฎหมายตรวจคำแปล (`legal-en/shop-*.en.md`) ก่อนอ้างกับลูกค้าต่างชาติ |
| SSL / โดเมน / DNS | นอกโค้ด | hosting ของเจ้าของ |
