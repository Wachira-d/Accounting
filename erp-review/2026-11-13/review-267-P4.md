# ฝ่ายค้าน รอบ 267 แพ็ก P4 (หลังจ่าย) — 17680548 + 8a36f7ac · b6f248b5 · eb62bb4a

ผู้ตรวจ: subagent ฝ่ายค้าน (อ่านโค้ดอย่างเดียว · ไม่ได้คอมไพล์/รันเทสต์) · branch `claude/booking-service-features-sdemob` @ FETCH_HEAD 2026-11-13

สรุป: **HIGH 0 · MED 3 · LOW 4** · ข้อที่ตรวจแล้วผ่านอยู่ท้ายรายงาน

---

## MED-1 — รายการใบเสร็จบนหน้าเอกสารแสดงใบเสร็จของ "ใบอื่น" เมื่อการชำระกระจายหลายใบ (ขัดคำตัดสิน 267-1-46)

`Accounting/Services/Implementations/DocumentPayLinkService.AfterPay.cs:30-46` (`PaymentReceiptsAsync`)

doc-comment (บรรทัด 22) และ 267-1-46 เขียนว่า "การชำระที่กระจายหลายใบไม่นับ" แต่โค้ด**ไม่ได้ตัดออก** ตัวกรองคือ
`(receiptIds.Contains(x.Id) || paymentIds.Contains(x.SettlementPaymentId))` โดยไม่ผูก `RelatedDocumentId`

สถานการณ์: การชำระ P บันทึกบนใบ X (`Payment.DocumentId = X`) แล้วจัดสรรให้ X, Y, Z (`PaymentAllocations`) → พนักงานออกใบเสร็จรายใบ
(`IssueReceiptForPaymentAsync(forDocumentId: Y)` — `DocumentService.cs:15364` ประทับ `SettlementPaymentId = P`, `RelatedDocumentId = Y` และ**ไม่**ตั้ง `ReceiptDocumentId`)
- หน้าเอกสารของ **X** แสดงใบเสร็จของ Y และ Z ป้าย "ใบเสร็จ/ใบกำกับภาษีจากการชำระของท่าน" และดาวน์โหลดได้ด้วย (เพราะ `ResolvePdfDocumentAsync` ใช้ชุด related ชุดเดียวกัน) แปลว่าข้อมูลผิดใบ ผู้ติดต่อเดียวกันจึงไม่รั่วข้ามราย แต่ผู้ถือลิงก์ของ X ได้ใบกำกับของ Y/Z ไปด้วย
- หน้าของ **Y** ไม่แสดงใบเสร็จของตัวเอง (P ไม่ใช่ `DocumentId == Y`) แล้วขึ้น `ReceiptPendingNote` ว่า "ผู้ขายยังไม่ได้ออกใบเสร็จ" ซึ่ง**ไม่จริง** (F2 ข้อ 3)

ไม่มีเทสต์ครอบ: `CDP267Db_ReceiptOnPageOnlyForOwnDoc` ไม่มีแถว allocation

แก้ขั้นต่ำ: ใบเสร็จต้องเป็นของใบนี้จริง คือ `x.RelatedDocumentId == docId` หรือ `x.Id == payment.ReceiptDocumentId` ของการชำระที่ไม่มีแถวจัดสรรไปใบอื่น
และเพิ่มชุดการชำระที่ `PaymentAllocations.DocumentId == docId` (ฝั่ง Y) · เพิ่มเทสต์ Db ทั้งสองทิศ: X ไม่เห็นของ Y และ Y เห็นของตัวเอง

## MED-2 — ปุ่ม "ชำระใบนี้" ออก token ใหม่ให้คนนอกตรง ๆ ซึ่งเส้น request-link ตั้งใจห้าม และการออกครั้งแรกไม่ลง audit

`DocumentPayLinkService.AfterPay.cs:126-149` → `EnsureCustomerLinkAsync` → `DocumentPayLinkService.cs:262-311`

- `RequestLink` (`PublicDocumentPageController.cs:213-215`) วางหลักไว้ว่า "**ไม่ออก token ให้คนนอก** — บันทึก audit แล้วแจ้งพนักงาน"
  แต่ `RelatedPageLink` ให้ผู้ถือ token ของใบวางบิล (หรือของใบใด ๆ ที่มีใบลูก/ใบต้นทางชนิดที่รับชำระและมียอดค้าง) **ได้ token ดิบของใบอื่นกลับไปใน response**:
  - ใบที่ผู้ขายยังไม่เคยออก/ส่งลิงก์ ระบบออกให้ใหม่ และเพราะ `oldHash == null` จึง**ไม่ลง audit เลย** (บรรทัด 283) พนักงานไม่รู้ว่ามีลิงก์สาธารณะของใบนั้นเกิดขึ้น
  - ใบที่ลิงก์หมดอายุ ระบบหมุนออกใหม่ได้อายุเต็ม `NewLinkExpiry` แม้เส้นของใบนั้นเองบังคับให้ "ขอให้ผู้ขายส่งใหม่"
- สิทธิ์จึงลามได้: ใบวางบิลที่ส่งต่อให้บุคคลที่สาม → token ของใบแจ้งหนี้ทุกใบในบิลอายุใหม่ → จากใบเหล่านั้นไปต่อที่ใบลูกชนิดที่รับชำระ (ใบเพิ่มหนี้) ได้อีก
  ขอบเขตยังอยู่ใน "ผู้ติดต่อเดียวกัน + ชุดที่หน้าแสดง" (ตรวจแล้ว: นอกชุด/บริษัทอื่น/hash แทน token = 404 · revoked = 409 ไม่ล้างการเพิกถอน)

แก้ขั้นต่ำ: (ก) ออกได้เฉพาะเมื่อใบนั้นมีลิงก์ที่ยัง `Usable` อยู่แล้ว (คืนลิงก์เดิม) ส่วนหมดอายุหรือไม่เคยออก ให้ไปเส้น request-link ของใบนั้น (บันทึก+แจ้งพนักงาน)
หรือ (ข) ถ้าเจ้าของยืนยันว่าให้ออกได้ ก็ต้องลง audit ทุกการออกจากคำขอสาธารณะ (`@event = "pay-link-issued-from-related"` + ใบต้นทางที่ขอ) และตั้งอายุไม่เกินอายุของ token ต้นทาง
เรื่องนี้ต้องให้เจ้าของตัดสินและบันทึกเป็นคำตัดสินเพิ่มต่อจาก 267-1-47 ซึ่งยังไม่ได้พูดถึงกรณีลิงก์หมดอายุ/ไม่เคยออก

## MED-3 — ใบแทน (SettlementPaidReissue) ไม่พาค่า "ปิดใบเสร็จอัตโนมัติ" ที่ผู้ขายตั้งไว้ไปด้วย ระบบจึงส่งใบเสร็จถึงลูกค้าทั้งที่ผู้ขายสั่งปิด

`Accounting/Helpers/SettlementPaidReissue.cs:413, 458` (b6f248b5/eb62bb4a ใส่ `PostPayEmailReceipt/PostPayIncludePartial` ลงรายการ "ไม่ตามไป")

สถานการณ์: ใบกำกับรับชำระมาแล้วบางส่วน ผู้ขายตั้ง **Off** ไว้ที่ใบ (เช่น ลูกค้าขอให้ส่งใบเสร็จรวมทีเดียว) แล้วต้องออกใบแทนเพื่อแก้ชื่อหรือที่อยู่ผู้ซื้อ (266-B ย้ายการชำระไปใบแทน)
→ ใบแทนกลายเป็น Inherit → บริษัท/ผู้ติดต่อเป็น On → งวดถัดไประบบออก**และอีเมล**ใบเสร็จ/ใบกำกับให้ลูกค้าเอง ซึ่งขัดกับสิ่งที่ผู้ขายตั้งใจไว้ และไม่มีอะไรเตือน
ใบแทนเป็น "ธุรกรรมเดิมฉบับแก้" ไม่ใช่ใบลูกหรือโคลน กฎเหล็ก #4 A "เอกสารลูกต้องสืบทอด (null เป็น null)" จึงใช้กับกรณีนี้
ส่วน clone/convert ที่ไม่ตามไปนั้นสมเหตุสมผล

แก้ขั้นต่ำ: ย้ายสองช่องนี้ไปฝั่ง "ตามไป" เฉพาะเส้นใบแทน (คงไม่สืบทอดใน clone/convert) และแก้ 267-1-44 ให้ตรงกัน
ถ้าเจ้าของยืนยันว่าไม่ให้ตามไป อย่างน้อยหน้าออกใบแทนต้องบอกว่า "ค่าตั้งใบเสร็จอัตโนมัติของใบเดิม (ปิด) ไม่ตามไป" เพื่อไม่ให้เป็น silent drop

---

## LOW-1 — สลิปที่ "ล้มเพราะระบบ" ขึ้นป้ายว่า "ผู้ขายไม่รับสลิปนี้ · ผู้ขายไม่พบยอดโอน"

`Accounting/Helpers/CustomerDocPageAfterPay.cs:54-62` (`SlipState`) · ตัวโหลด `DocumentPayLinkService.AfterPay.cs:92-101`

`Failed` ทุกแบบถูกแปลเป็น "ผู้ขายปฏิเสธ" รวมถึงกรณี intent ManualSlip ที่ล้มตอนสร้าง (`PaymentIntentService.cs:378-387` `create_charge_failed`)
ข้อความกลาง "ผู้ขายไม่พบยอดโอนตามสลิปนี้" เป็นการแต่งเหตุที่ไม่ได้เกิดขึ้น (F2 ข้อ 3)
แก้: โหลด `FailureCode` ด้วย แล้วใช้ `slip_rejected` → ป้ายปฏิเสธ ส่วนกรณีอื่นให้ขึ้น "ส่งสลิปไม่สำเร็จ — แนบใหม่" (ใช้ตัวตัดสินเดิมตัวเดียว)

## LOW-2 — ข้อความถึงลูกค้าตอนปฏิเสธสลิปไม่อยู่ใน audit/ประวัติ และเขียนแยกจาก ApplyChargeAsync

`Accounting/Controllers/PaymentGatewayController.cs:624-629`

`CustomerVisibleRejectReason` (ข้อความที่พนักงานเขียนถึงลูกค้าแล้วลูกค้าเห็น) เขียนด้วย `ExecuteUpdate` แยกต่างหาก ไม่ลง event หรือ audit จึงตรวจย้อนหลังไม่ได้ว่าใครบอกอะไรลูกค้า
ถ้าคำสั่งที่สองล้ม (หลังการปฏิเสธ commit แล้ว) ผู้ใช้จะได้ 500 ทั้งที่สลิปถูกปฏิเสธไปแล้ว
เหตุผลภายใน (`FailureMessage`) **ไม่รั่ว** ตรวจแล้ว: ตัวโหลดเลือกแค่ `CustomerVisibleRejectReason`
แก้: ส่งข้อความเข้า `ApplyChargeAsync` (ช่องใน `ProviderCharge` หรือ note ของ event) ให้ลงธุรกรรมเดียวกัน หรืออย่างน้อยใส่ใน note ของ event ปฏิเสธ

## LOW-3 — `PostPaymentAutomationService.cs:332-333` นับการชำระด้วย `p.DocumentId` เปล่า (ไม่ใช่ `MovedToDocumentId ?? DocumentId`)

ไม่ใช่โค้ดของ P4 (blame 26ec41b4) และ `EnqueueCoreAsync:151-158` ก็อ่าน `pay.DocumentId` เปล่าเหมือนกัน
บนใบแทน การชำระที่ย้ายเข้ามาไม่ถูกนับ → `paymentCount == 1` → `mayCarryVat` ประเมินว่า "งวดเดียว" ซึ่งเป็นทิศระวัง (กันมากขึ้น) จึงไม่ทำให้ส่งผิด
แต่ไม่สอดคล้องกับ `DocumentService.cs:15347` ที่ใช้ `(MovedToDocumentId ?? DocumentId)` แล้ว
แก้: ใช้นิพจน์เดียวกัน (หรือ `PaymentEffectiveDocument`) ทั้งสองจุด แล้วล็อกด้วย checker ของ 266-B ถ้ามี

## LOW-4 — ฟอร์มร่าง: เปลี่ยนชนิดจาก "รับชำระ" เป็นชนิดอื่น ค่า On/Off ที่ตั้งไว้ยังค้างในแถว

`wwwroot/pages/documents.html` `_postPayPayload` (ช่องซ่อน = ส่ง null = ไม่แตะ)

ฝั่งอ่านไม่ใช้ค่านี้แล้ว (`ResolveForAsync` ตรวจ `DocumentLayerAllowed`) จึงไม่มีผลเชิงธุรกิจ แต่ยังเก็บค่าที่ไม่มีผู้อ่าน (ขัด ApplyDocumentLayer ที่บอกว่า "ไม่เก็บค่าที่ไม่มีผู้อ่าน") และจะกลับมามีผลถ้าเปลี่ยนชนิดกลับ
แก้: ถ้าชนิดใหม่ตั้งไม่ได้ ให้ส่ง "Inherit" (ล้างค่า) แทน null

---

## ตรวจแล้วผ่าน (ไม่ต้องรายงานซ้ำ)

- **postMessage ของพอร์ทัล**: ทั้งสองฝั่งตรวจ `e.origin === location.origin` และ `e.source` (parent / `frame.contentWindow`) และส่งด้วย targetOrigin = `location.origin` · token ไม่อยู่ใน URL · หน้าเปิดตรงโดยไม่มี parent จะปฏิเสธ · `SecurityMiddleware` ตั้ง `X-Frame-Options: SAMEORIGIN` + `frame-ancestors 'self'` จึงกัน clickjacking ข้ามโดเมนได้
- **XSS**: ทุกค่าใน `afterPayHtml`/`relatedHtml` ผ่าน `esc()` · path ผ่าน `safePath` · `CSS.escape(relId)`
- **Tenant isolation**: ทุก query ใหม่ผูก `CompanyId` · `ResolveForAsync` ใช้ `IgnoreQueryFilters` แต่มี `CompanyId ==` · เทสต์ข้ามบริษัทของ RelatedPageLink มีแล้ว
- **ใบเสร็จข้ามผู้ติดต่อ/บริษัท**: กรอง `ContactId` + `CompanyId` + ไม่ใช่ Draft/WaitingApproval/Rejected/Voided + `IsCustomerFacing` (ข้อบกพร่องเหลือแค่ "ข้ามใบ" ใน MED-1)
- **PUT …/post-pay**: `[RejectApiKey]` · สิทธิ์ `CanCreateAsync` ระดับเดียวกับออก/ยกเลิกลิงก์ชำระของ controller เดียวกัน · ใบ Voided = 409 · ชนิดที่ตั้งไม่ได้ = 400 · audit chain · ไม่ลงซ้ำเมื่อไม่เปลี่ยน · ใบเสร็จที่ออกอัตโนมัติยังใช้สิทธิ์อนุมัติของผู้บันทึกรับชำระ (`run.CreatedBy`) จึงไม่เป็นทางยกสิทธิ์
- **กฎเหล็ก #4 B ของช่องใหม่**: ฟอร์ม (markup+id) · payload (สร้าง null / แก้ "Inherit") · Create/UpdateDocumentRequest · entity · migration `ADD COLUMN IF NOT EXISTS` · `DocumentResponse` echo · hydrate ตอน openEdit · reset ตอนฟอร์มใหม่ · ไม่พิมพ์บนเอกสาร (ไม่ต้องแตะ renderer) · DOCUMENT_FLOW/TEST_PLAN ขยับ · clone ฝั่งเซิร์ฟเวอร์ไม่คัดลอก (ตามคำตัดสิน) — ยกเว้นใบแทนตาม MED-3
- **50 ทวิ จากหน้า**: เพดานต่อ IP ก่อนแตะฐาน + ต่อ token หลังยืนยันว่ามีจริง (failClosed) · `RequestSizeLimit` · ตรวจ magic bytes ใน `UploadCoreAsync` · token payOnly/หมดอายุ/เพิกถอน ไม่มีการ์ด (WhtCertTargetAsync ผ่าน PublicViewAsync) · hint จำกัดที่ใบนี้ · `expectedPayerContactId` = ผู้ติดต่อของใบ · ส่งแล้วรอตรวจ = 409 · ไม่หักโควตา OCR ตรงตาม 260-5-4 · ชื่อผู้ส่งผ่าน `SanitizeSenderName`
- **ป้าย e-Tax**: ขึ้นเฉพาะ `ByEmailSentAt != null` และ `VoidedAt == null` (ตรงกับ Status=Voided ทุกจุดที่เขียน) · ข้อความไม่อ้างว่าไฟล์บนหน้าคือฉบับที่ประทับเวลา
- **ย้ายการชำระ (266-B) ใน PaymentReceiptsAsync**: ใช้ `(MovedToDocumentId ?? DocumentId)` ถูกแล้ว
