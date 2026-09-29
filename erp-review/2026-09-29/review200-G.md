# รอบ 200 — ฝ่ายค้าน (F3 ข้อ 11) ของทีม G · payment gateway

> base `5c1fe028` → `worktree-agent-af1a982aa3543c331` (`e004a2cc` + `f9914055`) · อ่านอย่างเดียว · ไม่ได้คอมไพล์ (ไม่มี .NET SDK)
> ตรวจ: diff ทั้งหมด 31 ไฟล์ · adapter Omise ทั้งไฟล์ · ผู้เรียกทุกตัวของ endpoint ที่เพิ่มสิทธิ์ · ทุกจุดที่เทียบ `ConfirmedAt` กับช่วงวันที่ ·
> ทุก `innerHTML` ใน `pos.html` · `node --check` สคริปต์ของ 3 หน้า (ผ่าน) · checker ใน worktree: html_attr_escape / onclick_js_string /
> write_permission_gate / payment_provider_boundary / record_arg / nullable_arg / using = เขียวทั้งหมด

## สรุป

| ID | ระดับ | P | เรื่อง |
|---|---|---|---|
| R200G-1 | **CONFIRMED** | **P1** (security) | PG-3 แก้ XSS ไม่ครบ — ตารางสินค้าและปุ่มหมวดใน `pos.html` ยังต่อชื่อสินค้า/รหัส/หมวดเข้า HTML ดิบ |
| R200G-2 | **CONFIRMED** | **P1** (เงิน) | E-3 เปิดช่อง "บัญชีธนาคารรับเงิน" บนหน้าจอ + บังคับบริษัทหลายบัญชีให้ปัก ⇒ บัตรเครดิต/e-Wallet/เช็คของเครื่องนั้นลง **Dr ธนาคาร** แทน 11340/11113/11131 |
| R200G-3 | **CONFIRMED** | P2 | ขอบช่วงเลื่อน 7 ชม. คลาสเดียวกับ G2-1 ยังอยู่ในทางเข้าพี่น้อง `SettlementImportService.LoadIntentRowsAsync` (ประกอบรอบโอนจาก PaymentIntent) |
| R200G-4 | PLAUSIBLE | P2 | ปักรุ่น `2019-05-29` ⇒ บัญชีที่เดิมอยู่รุ่นเก่าจะเริ่มได้ `fee` (ก่อน VAT) ⇒ รอบโอนแรกหลัง deploy ที่ปนแถวเก่า (ตัวประมาณ) กับแถวใหม่ (ค่าจริงก่อน VAT) ยอดไม่ตรงทุกโหมด — ดัง แต่ไม่มีคำเตือนล่วงหน้า |
| R200G-5 | PLAUSIBLE | P3 | ข้อมูลใน `GET /events/{id}` อาจถูกเรนเดอร์ด้วยรุ่นของบัญชี ณ เวลาเกิด event ไม่ใช่หัว `Omise-Version` ของคำขอ — ต้องยืนยันใน sandbox พร้อมข้อ R200G-4 |
| R200G-6 | PLAUSIBLE | P3 | `FeeActual = 0` ที่ได้จาก charge ตอน pending ถูกถือว่า "รู้แล้ว" — ถ้าสถานะไปถึง Succeeded ด้วยทางที่ไม่มีค่าธรรมเนียม (ยืนยันมือ/แจ้งจากภายนอก) ค่าจริงจาก webhook ตามหลังจะไม่ถูกรับ (เป็นมาก่อนรอบนี้ · กติกาใหม่ทำให้เป็นกติกาถาวร) |
| R200G-7 | PLAUSIBLE | P3 | คำเตือนเครื่อง POS `TerminalBankWarning(t.BankAccountId != null, …)` เงียบเมื่อปักผังที่ถูกลบ/ย้ายบริษัทแล้ว ทั้งที่ตอนปิดบิลตกไปทาง `PickBank` และอาจล้ม |
| R200G-8 | PLAUSIBLE | P3 | แจ้งเตือน "รับชำระค้าง" (`PaymentIntentReconcileJob`) บอกให้ "เปิดหน้ารายการรับชำระออนไลน์แล้วกดตรวจสถานะสด" — ผู้รับที่ไม่มี `Bank.View` ได้ 403 |
| — | NOT-A-BUG | — | 11 ข้อ (ดูท้ายไฟล์) รวม: webhook id จริงผ่าน · ผู้เรียก status ทุกตัวยังผ่าน · migration `SettledFeeDeducted` เติมเฉพาะแถวที่พิสูจน์ได้ · `PastRefundBooking` ถูกหลัก · สาขาว่าง = บล็อกไม่ทำงานค้าง · ประเด็นคอมไพล์ทั้ง 6 |

---

## CONFIRMED

### R200G-1 · P1 (security) — XSS ใน `pos.html` ยังเหลือสองจุด ติดกับจุดที่ PG-3 แก้
`git show worktree-agent-af1a982aa3543c331:Accounting/wwwroot/pages/pos.html`
- **บรรทัด 1568–1572 `renderGrid()`**: `<div class="item-name">${this._menuLang==='en'?(i.nameEn||i.name):i.name}</div>` และ
  `<div class="item-code">${i.code}</div>` → `grid.innerHTML`. `i.name`/`i.nameEn`/`i.code` มาจาก `p.name`/`p.nameEn`/`p.code`/`p.sku`
  (บรรทัด 1509–1510) — สินค้า/แพ็กเกจที่ผู้ใช้พิมพ์ นำเข้าไฟล์ หรือมาทาง integration
- **บรรทัด 1520–1521 `buildCategories()`**: `onclick="Page.selectCategory('${c}')">${c}</button>` → `el.innerHTML` · `c` = หมวดสินค้า
  (ข้อความอิสระ) — ทั้งในข้อความ element **และ**ใน JS string ของ onclick (ต้อง `Layout.jsArg` ไม่ใช่ `esc` — กติกา `onclick_js_string_check`)
- **ฉากที่ผิด**: สินค้าชื่อ `<img src=x onerror=…>` ⇒ ทุกครั้งที่แคชเชียร์/เจ้าของเปิดกะ ตารางสินค้า render ⇒ สคริปต์รันด้วย JWT ใน localStorage —
  คลาสเดียวกับที่ PG-3 ประกาศว่าแก้แล้ว (แก้ `${c.name}` ในตะกร้า บรรทัด 1732 แต่ตาราง**ที่อยู่เหนือขึ้นไป 160 บรรทัด**ซึ่งแสดงชื่อเดียวกันก่อนตะกร้าเสมอยังดิบ)
- เหตุที่ไม่มีอะไรฟ้อง: `html_attr_escape_check` ดูเฉพาะ attribute (worktree ตอบ 0 จุด) — ข้อความใน element ไม่มี checker ครอบ ⇒ ต้อง grep `innerHTML` ทั้งไฟล์
  ตามหลักการ F2 ข้อ 1 (ผมไล่ทุก `innerHTML`/`document.write` 33 จุดในไฟล์แล้ว — ที่เหลือผ่าน `Layout.esc`/`esc` หรือเป็นตัวเลข/GUID/ค่าคงที่)
- ข้อสังเกตเล็ก: บรรทัด 2652 `bg = t.color + '40'` ต่อเข้า `style="…background:${bg}…"` — สีโต๊ะเป็นข้อความจาก API (ไม่ผ่านตัวหนี) ⇒ `"` ปิด attribute ได้ถ้า API รับค่าอิสระ (PLAUSIBLE P3 · ไม่ได้ไล่ว่าเซิร์ฟเวอร์ตรวจรูปสีไหม)

### R200G-2 · P1 (เงิน) — ปักบัญชีธนาคารบนเครื่อง = ทุกวิธีจ่ายที่ไม่ใช่เงินสดลงธนาคาร (รวมบัตรเครดิต)
- `Accounting/Services/Implementations/PosService.Orders.cs:2227` (ไม่ได้แก้รอบนี้):
  `var pinned = method == PaymentMethod.Cash ? terminal?.CashAccountId : terminal?.BankAccountId;` แล้ว `return pinnedAccount` ทันที —
  ข้าม `MoneyAccountFallback.KindOf/StandardCode` ⇒ `CreditCard` (ควร 11340 ลูกหนี้ผู้รับบัตร), `EWallet` (11113), `Cheque` (11131 เช็คในมือ) ลง GL ธนาคารที่ปักไว้
  (ผู้เรียก: บรรทัด 1665 ปิดบิล · 512 คืนเงิน · ไม่มี PaymentIntent ⇒ `ResolveGatewayClearingAsync` คืน null ก่อน)
- **รอบนี้ทำให้เส้นนี้เกิดจริง**: ก่อนรอบ 200 ช่อง `BankAccountId` มีแค่ใน API (ทีม G ยืนยันเองใน E-3) ⇒ แทบไม่มีเครื่องไหนปัก ·
  หลังรอบนี้ (ก) หน้าจอมี dropdown `fTermBankAccount` (pos.html ~361) พร้อมคำอธิบาย "(โอน/พร้อมเพย์/หักบัญชี)" (ข) ป้ายแดง + `TerminalBankWarning`
  **สั่ง**บริษัทที่มีบัญชีธนาคาร ≥ 2 ให้ปัก ⇒ ผู้ใช้ทำตามแล้วบิลบัตรเครดิตจากเครื่อง EDC ของเครื่องนั้นลง `Dr ธนาคาร` เต็มยอด:
  ยอดธนาคารในระบบเกินสเตทเมนต์จนกว่าผู้รับบัตรโอน T+n หลังหัก MDR · ค่าธรรมเนียม MDR ไม่มีที่ลง · กระทบยอดธนาคารไม่ได้ —
  ตรงข้ามกับเหตุผลที่ `CardAcquirerClearing` ถูกแยกไว้ใน `MoneyAccountFallback` (รอบ 198 P-1) · เช็คที่ยังไม่นำฝากก็เข้าธนาคาร
- ทิศแก้ที่เสนอ: ใช้ `BankAccountId` เฉพาะ `KindOf(method) == BankDeposit` (ตรงกับข้อความบนหน้าจอ) · ชนิดอื่นไป `StandardCode` · เทสต์สองทิศ
  (บัตร + ปักธนาคาร → 11340 · โอน + ปักธนาคาร → ธนาคารที่ปัก) · ล็อกด้วย `required_call_site_check` · ต้องถามว่ามีบิลบัตรที่ลงธนาคารไปแล้วจากเครื่องที่ปักผ่าน API ไหม (migration/รายงาน)

### R200G-3 · P2 — ขอบช่วง 7 ชม. เหลือในทางเข้าพี่น้อง
- `Accounting/Services/Settlement/SettlementImportService.cs:163` `var to = periodTo is DateTime pt ? ThaiDate.CalendarDateUtc(pt).AddDays(1) : …` แล้ว
  `:171 i.ConfirmedAt < to` — สูตรเดียวกับที่ G2-1 ถอดออกจาก `BuildPlanAsync` (checker ห้าม `ThaiDate.CalendarDateUtc(req.ToDate).AddDays(1)` เฉพาะใน
  `BuildPlanAsync`) · คอมเมนต์บรรทัด 164 เขียนว่า "เงื่อนไขเดียวกับ `GatewaySettlementService.SelectCandidatesAsync`" ซึ่ง**ไม่จริงอีกต่อไป**
- ฉาก: รอบโอน (SettlementBatch · ปุ่ม "ประกอบจากรายการรับชำระ") ปลายช่วง 20/09 ⇒ รายการที่รับเงิน 21/09 00:00–07:00 น. เวลาไทยถูกดึงเข้ารอบ 20/09 ⇒
  ยอดรอบไม่ตรงกับยอดที่ผู้ให้บริการโอน (ตัดรอบเที่ยงคืนไทย) — ผลเดียวกับ G2-1 แต่บนเส้น settlement ใหม่ที่ `DOCUMENT_FLOW` แนะนำให้ใช้ต่อไป
- ทีม G ระบุว่า "ไม่แตะ `Services/Settlement/*`" — ต้องส่งต่อเจ้าของไฟล์ในรอบเดียวกัน (หลักการ F2 ข้อ 1: รูปแบบเดิมเหลือ **1 จุด** ไม่ใช่ 0)
  ใช้ `GatewaySettlementMath.ConfirmedRangeUtc(…).EndUtcExclusive` หรือ `RefundCutoffUtc(periodTo).AddDays(1)` + แถว checker

---

## PLAUSIBLE

### R200G-4 · P2 — ปักรุ่น API เปลี่ยนความหมาย/การมีอยู่ของ `fee` สำหรับบัญชีที่อยู่รุ่นเก่า
อ่าน `OmisePaymentProvider.cs` ทั้งไฟล์ ช่องที่อ่าน: `status` `paid` `amount` `fee` `refunded_amount` `refunds{data,total,voided,amount,metadata.attempt,id}`
`source.scannable_code.image.download_uri` `source.expires_at` `authorize_uri` `failure_code/message` `id` · event: `data.object` `data.metadata.intentId`
- `amount`/`status`/`paid`/`authorize_uri`/`failure_*`/`metadata`/`refunds` — ไม่พบว่าความหมายต่างระหว่าง 2017-11-02 กับ 2019-05-29 (หน่วยสตางค์เหมือนเดิม)
- **`fee`**: รุ่น 2019-05-29 = ค่าธรรมเนียม**ก่อน VAT** (มี `fee_vat`/`net` แยก — ทีม G เขียนไว้เองใน G3-1) · จากความจำของ changelog ผู้ให้บริการ ช่อง fee/fee_vat/net
  ถูกเพิ่มพร้อมรุ่นนี้ (ยืนยันไม่ได้ในเครื่องนี้) ⇒ บัญชีที่เดิมตอบรุ่นเก่า `Fee` = null ⇒ ใช้ `FeeEstimated` · หลัง deploy ได้ `FeeActual` ก่อน VAT ทันที
- ฉาก: บริษัทจด VAT โหมด `None` (ค่าเริ่มต้น) · รอบโอนแรกหลัง deploy มีทั้งแถวเก่า (ตัวประมาณ — มักตั้งรวม VAT) และแถวใหม่ (ก่อน VAT) ⇒ `None` ขาด VAT ของแถวใหม่ ·
  เปลี่ยนเป็น `AddedOnTop` ⇒ แถวเก่าถูกบวก VAT ซ้ำ ⇒ ยอดไม่ตรง**ทุกโหมด** ต้องแก้ค่าธรรมเนียมรายแถว (`PUT fee`) — ดัง (แผนบล็อก) จึงไม่ใช่เงินผิดเงียบ
  แต่ไม่มีคำเตือนล่วงหน้า/หมายเหตุในหน้าตั้งค่า · ควรเข้า sandbox checklist ของ team-G คำถามค้างข้อ 2 พร้อม: อ่าน `fee_vat` (G-7) แทนการพึ่งโหมด
- `source.expires_at`: ในรุ่นนี้วันหมดอายุอยู่ที่ `charge.expires_at` (ความจำ) ⇒ `QrExpiresAt` อาจ null เสมอ — เป็นมาก่อนรอบนี้ ไม่ทำเงินผิด (สถานะ `expired` มาทาง webhook/poll) · P3

### R200G-5 · P3 — รุ่นของข้อมูลใน event
`VerifyWebhookAsync` เชื่อ `data` ที่ fetch จาก `/events/{id}` · ถ้าผู้ให้บริการเก็บ event ตามรุ่นของบัญชี ณ เวลาเกิด (แบบที่ผู้ให้บริการรายอื่นทำ) หัวที่ปักไม่มีผล
⇒ `refunded_amount` อาจหายเฉพาะเส้น webhook (ตกไปใช้ผลรวม `refunds` — มีตาข่ายอยู่แล้ว) · `fee` อาจไม่มี ⇒ null ⇒ ไม่ถูกรับ (ปลอดภัย) — ความเสี่ยงต่ำ แต่ควรอยู่ในรายการทดสอบ sandbox

### R200G-6 · P3 — `FeeActual = 0` จาก pending ถูกนับเป็น "รู้แล้ว"
`PaymentIntentPolicy.ShouldTakeProviderFee` (`feeActual == null || IsOpen`) + เส้น duplicate `PaymentIntentService.cs:282` (`FeeActual == null`) ·
ฉาก: `StartAsync` ได้ charge pending ที่ `fee: 0` ⇒ `FeeActual = 0` → เจ้าหน้าที่กด "ยืนยันด้วยมือ" (ProviderCharge ไม่มี Fee) → Succeeded → webhook `successful`
มาทีหลัง = duplicate ⇒ `FeeActual == 0 ≠ null` ⇒ ค่าจริงไม่ถูกรับ ⇒ รอบโอนไม่ตรง (ดัง · แก้ด้วย `PUT fee`) — มีมาก่อน (ไม่ใช่ถดถอย) แต่ควรพิจารณา "0 จากสถานะเปิด = ยังไม่รู้" ·
และเส้น duplicate ยังเป็นผู้เขียน `FeeActual` คนที่สองนอก `ShouldTakeProviderFee` (หลักการ 4)

### R200G-7 · P3 — คำเตือนเครื่อง POS กับตัวตัดสินตอนปิดบิลไม่ใช่กติกาเดียวกันทั้งหมด
`PosService.cs:280` ส่ง `t.BankAccountId != null` · `PosService.Orders.cs:2229–2240` ถ้าผังที่ปักหาไม่เจอ ⇒ ตกไป `PickBank` (Ambiguous/None ⇒ ล้ม) แต่ป้ายเงียบ ·
และผังที่ปักแต่ `IsActive=false` ถูกใช้ต่อ (ไม่เช็ค IsActive) ขณะที่ `ValidateTerminalMoneyAccountsAsync` ปฏิเสธผังปิดใช้ — สองชั้นขัดกัน · `ValidateTerminalMoneyAccountsAsync`
ไม่ตรวจชนิดผัง (ปักผังรายได้เป็น "บัญชีธนาคารรับเงิน" ผ่าน API ได้)

### R200G-8 · P3 — ทางไปต่อของแจ้งเตือน "รับชำระค้าง"
`Services/Background/PaymentIntentReconcileJob.cs:180–187` ข้อความ + `ActionUrl=/pages/payment-intents.html` · หน้านั้นโหลด `GET pay/intents` ซึ่งตอนนี้ `Bank.View` ·
ถ้า `NotificationEvents.GatewayPaymentStuck` ส่งถึงบทบาทที่ไม่มี `Bank.View` (ไม่ได้ไล่ตาราง subscription) ⇒ ทำตามคำแนะนำแล้วเจอ "โหลดไม่สำเร็จ" — ข้อความ 403 บอกสิทธิ์ที่ต้องขอ จึงยังไม่ใช่ทางตัน ·
เมนู `layout.js:1517` ไม่ gate ด้วยสิทธิ์ (เห็นเมนู กดแล้ว 403) — ตรงกับที่ทีมเขียนในคำถามค้างข้อ 4

---

## NOT-A-BUG (ไล่แล้ว)

1. **ผู้เรียก `GET pay/intents/{id}/status` ทุกตัว** (`git grep pay/intents` ทั้งเรพ): `pay-widget.js:222` โหมดล็อกอิน ใช้จาก `addons.html:374–379` (intent `AddOnPurchase`
   สร้างในบริษัทลูกค้าผ่าน `MeteringController.StartAddOnPayment` ซึ่งบังคับ `BillingManage` = `StartKeyFor(AddOnPurchase)` ⇒ poll ผ่าน) · `payment-intents.html:213`
   (หน้าเดียวกับ list ที่ต้อง `Bank.View`) · หน้าสาธารณะ (`storefront.html:1335/1880`) ใช้ `publicBase` = `public-pay/...` ไม่แตะ endpoint นี้ · POS/เอกสาร/ที่พักไม่มี UI เรียก ·
   ไม่มีผู้เรียกนอก wwwroot ⇒ ไม่มีหน้าที่เคยใช้ได้แล้ว 403 ยกเว้นบทบาทกำหนดเองบนหน้ารายการ (ทีมบอกไว้แล้ว) · ด่านอยู่ก่อน `RefreshAsync` (ลำดับถูก · checker ล็อก)
   · รูปแบบ `_permissions.HasPermissionAsync(companyId, JwtHelper.GetUserIdFromClaims(User), key)` เดียวกับ `DenyKeyAsync` ของ `POST intents`
2. **`GET reconciliation`**: ไม่มีหน้าเรียก · `settlements/pending` เรียกจาก `payment-settlements.html:110` ซึ่งเรียก `preview`/`fee-vat` (`Bank.View`) อยู่แล้ว ⇒ ไม่ถดถอย
3. **`IsWellFormedEventId`** ทิศตรงข้าม: รูปเลข event จริง `evnt_…` / `evnt_test_…` = ตัวพิมพ์เล็ก+ตัวเลข+ขีดล่าง ⇒ ผ่าน (เทสต์ `WH_ทิศตรงข้าม_…`) ·
   ตรวจก่อน `Client(config, ApiBase)` ⇒ ไม่ยิงคำขอเลย · tenant: webhook resolve จาก `metadata.intentId` + ownership R-E1 เดิม
4. **tenant `CompanyId`**: query ใหม่ทุกตัวมีตัวกรอง — `CompanyBankPickAsync` (`b.CompanyId == companyId`) · `ValidateTerminalMoneyAccountsAsync` (`a.CompanyId == companyId`) ·
   `Events`/`Reconciliation`/`FindAsync` เดิม · migration `UPDATE` ต่อแถวตัวเอง (`"PaymentIntents"."Id" = d."Id"`) ไม่ข้ามบริษัท
5. **`ConfirmedRangeUtc`**: `BangkokMidnightUtc(label)` ⇒ ป้าย 14/09 → 13/09 17:00Z ถูก · อินพุต `type=date` (Unspecified 00:00) ผ่าน `CalendarDateUtc` ได้วันเดิม ·
   `RefundCutoffUtc` ใช้ตัวเดียวกัน · ใน `GatewaySettlementService`/`PaymentGatewayController` ไม่เหลือสูตรเก่า (เหลือแค่ R200G-3) · `PaymentIntentAdapter.cs:49`
   `CalendarDateUtc(ConfirmedAt)` = ป้ายวันของ instant — ถูก · ผลข้างเคียงช่วงเปลี่ยนผ่าน: รายการตี 0–7 ของวันแรกในรอบที่บันทึกไปแล้วก่อน deploy ค้างอยู่ใน "รอโอน"
   (มองเห็น · ไม่หายเงียบ) — แนะนำหมายเหตุใน CHANGELOG เท่านั้น
6. **`PastRefundBooking`**: หลักบัญชีถูก — เหตุการณ์เงินออกบันทึก ณ วันเกิด ถ้างวดเปิด · งวดปิด ⇒ ปรับปรุงงวดปัจจุบัน + ข้อความวันจริงบน**บรรทัด Dr** และ event note
   (ไม่ใช่หัวใบสำคัญ — ทีมเขียนว่า "บนใบสำคัญ" ถือว่าตรงพอ) · ขา JE เป็น Dr ลูกหนี้/Cr 11340 ไม่มี VAT ⇒ ไม่ชน ภ.พ.30 ที่ยื่นแล้ว · event `At` = attemptAt มาตั้งแต่ก่อนรอบนี้
   (timeline ของจุดตัดคืนเงินไม่เปลี่ยน) · ตรวจงวดของวันนี้ซ้ำก่อนลง ⇒ วันนี้ปิดด้วย = ล้มพร้อมทางไปต่อเดิม
7. **`SettledFeeDeducted` + migration**: เขียนเฉพาะ candidate ที่ `!AlreadySettled` (`GatewaySettlementService.cs:244–254`) ⇒ แถวคืนเงินหลังรอบไม่ถูกทับด้วย 0 ·
   สูตรย้อนหลัง `Amount − (RefundSettledAmount − RefundDeductedAfterSettlement) − SettledAmount` = `FeeDeducted` ของรอบนั้นพอดี (ยอดคืน ณ วันเงินเข้า = ค่าที่บันทึกตอนแรก) ·
   รับเฉพาะเมื่อเท่ากับ `fee` หรือ `fee + round(fee×0.07,2)` (PostgreSQL `round(numeric)` ปัดครึ่งออกจากศูนย์ = `AwayFromZero`) · แถวสมัยก่อนมี `RefundSettledAmount`/
   `RefundDeductedAfterSettlement` หรือ `FeeActual` ถูกทับทีหลัง ⇒ ไม่ตรง ⇒ NULL = สูตรเดิม · idempotent · แถวที่ยังพิสูจน์ไม่ได้ถูกประเมินใหม่ทุก startup (ถูก ไม่อันตราย)
8. **`ShouldTakeProviderFee`**: `PUT fee` อนุญาตเฉพาะ `IsSettledPositive` + ยังไม่อยู่ในรอบ (`CorrectFeeAsync`) ⇒ กติกา "สถานะเปิดรับทับได้" ไม่ชนค่าที่แก้มือ · pending→successful ยังรับค่าจริง
9. **สาขาผู้ออกใบว่าง = บล็อก**: ผู้เรียก `GatewayFeeVatClaim.Check` มีตัวเดียว (`ClaimFeeVatAsync` — คำขอของผู้ใช้ต่อครั้ง · `[RejectApiKey]`) · ไม่มีงาน/ข้อมูลค้างที่ถูกตรวจซ้ำ ·
   หน้าเว็บตั้ง `00000` ไว้ให้ ⇒ ไม่มีงานค้าง · `FindDuplicate` ไม่ใช้สาขา · `FutureClaimDateMessage` อยู่ก่อนธุรกรรม/`JournalEntryBuilder` (checker ล็อก)
10. **`ManualConfirmBlockReason`**: บล็อกเฉพาะ adapter ที่ถือเงิน + `ProviderRef` ว่าง · ManualSlip (`SettlesDirectlyToBank=true`) และ code ที่ไม่รู้จักยังยืนยันได้ · `ConfirmExternalAsync` ไม่ผ่าน endpoint นี้
11. **คอมไพล์ (อ่านโค้ด)**: `InternalsVisibleTo Include="Accounting.Tests"` มีจริง (`Accounting.csproj:82`) · `lifetime with { FeeDeducted = … }` — `SettlementIntentContribution`
    เป็น `readonly record struct` positional (init) ⇒ ใช้ได้ · `GatewayIntentAmounts(..., SettledFeeDeducted)` ถูกสร้างหลัง `ToListAsync` (LINQ-to-objects) ⇒ ไม่มี CS0854 ·
    `[FromServices] IEnumerable<IPaymentProvider>` + default interface member `SettlesDirectlyToBank` เรียกผ่านตัวแปรชนิด interface ⇒ ได้ · เทสต์สร้าง
    `OmisePaymentProvider(IHttpClientFactory, ISecretProtector, ILogger<>)` ตรงลายเซ็นจริง (บรรทัด 69) · `ISecretProtector` มี `Protect/Unprotect/IsUsable` ครบ ·
    `HttpClient` คัด `DefaultRequestHeaders` เข้า `request.Headers` ก่อนถึง handler ⇒ เทสต์หัวรุ่น API วัดของจริง · `MapTerminal` ผู้เรียก 2 จุดอัปเดตครบ ·
    `PermissionKeys.P = "perm:"` ⇒ ข้อความ "POS.Cashier" ตรงเทสต์ · `node --check` สคริปต์ `pos.html`/`payment-intents.html`/`payment-settlements.html` ผ่าน ·
    `PG-1` คีย์ `from/to` ตรงกับ projection ของ `Events` จริง
