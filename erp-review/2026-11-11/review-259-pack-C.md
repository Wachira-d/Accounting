# ฝ่ายค้าน รอบ 259 แพ็ก C — "รวมบ้าน · ไม่ซ้อน" (95b419a3 · e3e6f40a · ae37c503 · c74c2ba9)

ผู้ตรวจ: subagent ฝ่ายค้าน (อ่านโค้ดอย่างเดียว · ไม่ได้คอมไพล์ · ไม่ได้รันเทสต์) · วันที่ 2026-11-11

สรุป: **ไม่มี HIGH** · MED 4 · LOW 7 · ทุกข้อเปิดไฟล์ยืนยันแล้ว ข้อที่ไล่หลักฐานไม่ได้ตัดทิ้ง

ตรวจแล้วไม่พบปัญหา:
- pay-link ของพอร์ทัล: ความเป็นเจ้าของผ่าน `PortalDocuments` (CompanyId + ContactId จากโทเคน + ใบที่ยังไม่ออกไม่นับ) + `IsPortalVisibleAsync` ส่วนธงสิทธิ์ผ่าน `GetAccessFlagsAsync` ซึ่งกรอง CompanyId ⇒ ลูกค้า X ขอใบของลูกค้า Y หรือใบของบริษัทอื่นได้ 404/403 (มีเทสต์ Db ทิศ "ใบลูกค้าอื่นไม่พบ")
- ใบยกเลิก/ชำระครบ/ร่าง ⇒ `IssueRefusal` ใน `EnsureLinkAsync` และ `TransferForDocumentAsync`
- ยอดสลิปที่ไม่ส่งยอดมา = `TransferForDocumentAsync.Net` ⇐ `AmountsAsync` ⇐ `PaymentWhtFiguresAsync` + `ExpectedWht` ซึ่งเป็นตัวคิดเดียวกับหน้าชำระและ `DocumentPaymentHandler` (ไม่มีสำเนาสูตร WHT ใน JS หรือในโค้ดใหม่) ด่านยอดค้าง/ยอดรอตรวจ/สลิปซ้ำอยู่ที่ `SlipEvidenceService` เดิม ฝั่ง handler ลง WHT จาก `WhtForPayment` ⇒ ใบปิดครบ
- รายการ/เพิกถอนลิงก์พอร์ทัลผู้ขาย: กรอง CompanyId และ `TenantGuardFilter` ครอบ route `{companyId}` (มีเทสต์ "บริษัทอื่นไม่เห็น") เพิกถอนซ้ำ = 400 พร้อมเหตุ
- อีเมลเชิญ: ทุกค่าผ่าน `HtmlEncode` ส่วน `rawToken` เป็น base64url
- XSS: vendor-portal-admin, etax (แถว RD), fiscal checklist, autopilot hub, getting-started, contacts panel และ portal.html ใช้ `Layout.esc`/`jsArg`/`Number()` ครบทุก innerHTML ใหม่ ส่วน `formDialog` ใส่ค่าด้วย textContent/value
- ด่าน confirm แบบเดิมที่ถูกแทน ใช้ `if (!await Layout.confirm(...)) return` ครบทุกจุด ส่วน `formDialog`/`promptText` คืน null ตอนยกเลิก/×/Esc และผู้เรียกทุกตัว `return` ที่ไม่มีกล่องยืนยันแยกแล้ว:
  - `rejectAndReverse` รวมคำเตือนลงใน promptText แบบ danger
  - `refund` ใช้ formDialog แบบ danger
  - ยอดคืนยังตรวจซ้ำที่ `GatewayRefundMath.Check`
- e-Tax: modal "ตรวจแล้ว" ย้ายมาครบทั้ง markup และ JS (allowedOutcomes · ช่องเลขอ้างอิง · note) และเรียก endpoint เดิม
- แผงผู้ติดต่อ: ทุกช่องที่ส่งตรงกับ `UpdateContactAutopilotRequest` (null = ไม่แตะ · "Inherit"/"" = ล้าง) · มี echo (`ContactAutopilotSettingsResponse`) · `canEdit` มาจากด่าน Contact.Edit ตัวเดียวกับ PUT
- wht-credit: PUT ส่งเฉพาะช่องเดียว (DTO รองรับ null = ไม่แตะ)

---

## MED

### M1 — ลูกค้ากด "ชำระ" ในพอร์ทัลแล้วล้างการเพิกถอนลิงก์ชำระของพนักงาน และเขียนช่องทางของลิงก์ที่ส่งอีเมลไปแล้วทับ โดยไม่มี audit
- `Accounting/Services/Implementations/PortalService.cs:724` → `DocumentPayLinkService.EnsureLinkAsync` (`DocumentPayLinkService.cs:227-252`)
- **ล้างการเพิกถอน:**
  - พนักงานกด "ยกเลิกลิงก์" (`RevokeAsync` ประทับ `PayLinkRevokedAt` + audit) เช่นเพราะลิงก์หลุดหรือใบอยู่ระหว่างโต้แย้ง
  - ลูกค้าที่มีสิทธิ์ pay กดปุ่มชำระในพอร์ทัล ⇒ `LinkUsable` = false ⇒ ออก token ใหม่และ `SetProperty(PayLinkRevokedAt, null)`
  - ผล: การเพิกถอนถูกล้างด้วยการกระทำของลูกค้า พนักงานไม่รู้ เพราะเส้นนี้ไม่มีแถว audit
  - เดิมการออก token ใหม่หลังเพิกถอนเกิดได้เฉพาะเมื่อ "ส่งอีเมลรอบใหม่" (คนหรือตารางของบริษัทสั่ง) ตาม doc-comment บรรทัด 17-18
- **เขียนช่องทางทับ:**
  - เรียกด้วย `scheduleChannels: null` ⇒ ถ้า `PayLinkChannelsJson` ต่างจากค่าผู้ติดต่อ/บริษัท จะ `ExecuteUpdate` ทับช่องทางที่ตารางส่งตั้งให้ลิงก์ในอีเมล (บรรทัด 229-232)
  - ผล: ลิงก์ที่ส่งไปแล้วเปลี่ยนชุดช่องทางตามการคลิกของลูกค้า
- **แก้ขั้นต่ำ:** แยกเมธอดอ่านอย่างเดียวสำหรับพอร์ทัล
  - มีลิงก์ที่ยังใช้ได้ ⇒ คืน URL เดิมโดยไม่แตะ channels
  - ไม่มีลิงก์ หรือหมดอายุ ⇒ ออกใหม่ได้
  - ถูกเพิกถอน (`PayLinkRevokedAt != null` และ `>= PayLinkIssuedAt`) ⇒ ห้ามออกใหม่จากพอร์ทัล ให้ตกไปที่ SlipFallback หรือ refusal "ติดต่อร้าน"
  - เขียน audit `PayLinkIssued{source=portal}`
  - เทสต์ทิศตรงข้าม: "เพิกถอนแล้ว ลูกค้ากดชำระ ⇒ ไม่มีลิงก์ใหม่" และ "ลิงก์ที่ใช้ได้ ⇒ URL เดิม + channels ไม่เปลี่ยน"

### M2 — เช็กลิสต์ปิดงวดยังเป็นสองชุด: หน้าบอก "✓ พร้อมปิดงวด" แต่ปุ่มปิดจริงปฏิเสธ (หรือกลับกัน)
- `Accounting/wwwroot/pages/fiscal.html:346-352` (ใช้ `PreCloseChecklistService.ReadyToClose` = ไม่มีข้อ Error) เทียบกับ `AccountingService.CloseFiscalPeriodAsync` (`AccountingService.cs:2864-2897`) ซึ่งเป็นด่านจริงของปุ่ม "ยืนยันปิดงวด"
- กติกาที่ขัดกัน:

  | เรื่อง | PreCloseChecklistService | CloseFiscalPeriodAsync |
  |---|---|---|
  | รายการธนาคารยังไม่กระทบยอด 1–4 รายการ | Warning ⇒ ready | **บล็อก** |
  | เอกสาร Draft ในงวด | **ไม่มีข้อนี้** (มีแค่ WaitingApproval = Warning) — แถวนี้หายจากจอเพราะ JS เดิมเคยแสดง | **บล็อก** |
  | ไม่มี ภ.พ.30/ภ.ง.ด.3,53 (ถ้า severity เป็น Error) | แดง | ปิดได้ |
  | ช่วงวันที่ | เดือนของ `startDate` | `FiscalPeriodId`/StartDate–EndDate |

- **สถานการณ์:** งวดมีใบแจ้งหนี้ร่าง 1 ใบ ⇒ หน้าขึ้นเขียว "ผ่านการตรวจสอบทุกรายการ" ⇒ กดปิด ⇒ ได้ข้อความ "ยังมีเอกสาร Draft 1 ฉบับ" (ล้มดัง แต่จอโกหกก่อนหน้านั้น)
- คำตัดสิน 259-3-6 เขียนไว้ว่า "ชุดเดียว" แต่จริงยังมีสองชุดที่กติกาต่างกัน (ขัด F2 #4 และ #3)
- อีกเรื่อง: ตัวตรวจ AI เดิม "เอกสารอนุมัติแล้วไม่มี JE" ถูกถอดออกโดยไม่มีข้อทดแทนใน PreClose — ถ้าตั้งใจให้หายต้องจดในคำตัดสิน
- **แก้ขั้นต่ำ:** ให้ `CloseFiscalPeriodAsync` เรียก `PreCloseChecklistService.RunAsync` แล้วบล็อกด้วย `ReadyToClose` ตัวเดียว หรือย้ายกติกาของ Close (Draft docs · bank ใด ๆ) เข้า PreClose เป็น Error
  - เทสต์ล็อก "ready ⇔ ปิดได้" ทั้งสองทิศ
  - ส่ง year/month ของ period (`period.year/month`) แทนการ parse `startDate`

### M3 — อีเมลเชิญพอร์ทัลผู้ขาย: ส่งตรงผ่าน SMTP ของแพลตฟอร์มจากคำขอที่ไม่มีด่านสิทธิ์ ไม่ปฏิเสธ API key ไม่ตรวจรูปอีเมล และไม่ผ่านคิว/โควตา
- `Accounting/Controllers/VendorPortalController.cs:32-58` (Issue: `[Authorize]` อย่างเดียว · อยู่ใน baseline ของ `write_permission_gate`) + `VendorPortalService.SendInviteAsync` → `IEmailService.TrySendAsync` (`EmailService.cs:98` = SMTP ระบบ ส่งทันที)
- เดิม endpoint นี้แค่ออก token (ความเสี่ยงจำกัด) **แพ็กนี้เพิ่มการส่งอีเมลออกนอกระบบ** ⇒ สมาชิกบริษัทคนใดก็ได้ (หรือ integration key ที่มี scope เขียน เพราะไม่มี `RejectApiKey`) วนเรียก Issue ด้วย `recipientEmail` อะไรก็ได้ ⇒ ระบบส่งอีเมล "พอร์ทัลผู้ขาย — {บริษัท}" ทันทีครั้งละหนึ่งฉบับ ไม่มีเพดาน
- ข้อความ "เรียน {contactName}" มาจากชื่อผู้ติดต่อที่ผู้ใช้ตั้งเองได้ (encode แล้วแต่ยังเป็นข้อความหลอกลวงได้)
- เปรียบเทียบ: ลิงก์วิเศษพอร์ทัลลูกค้า (`PortalService.RequestMagicLinkAsync`) จงใจ "เข้าคิว EmailQueue (โควตา/เพดานอีเมลของแพลตฟอร์ม · ไม่ส่งตรงจากคำขอ)" ตามฝ่ายค้านรอบ 255 M6
- ฝั่ง customer-portal admin ใช้ `RequirePermission(ContactEdit)` + `RejectApiKey` แต่ฝั่งผู้ขายไม่มี
- `GET …/vendor-portal/tokens` ใหม่ก็ไม่มีด่านสิทธิ์ (ทุกบทบาทเห็นอีเมลผู้รับ/ผู้ขายทั้งหมด)
- **แก้ขั้นต่ำ:**
  - Issue/Revoke/List ใส่ `RequirePermission(ContactEdit)` (หรือสิทธิ์ AP) + `RejectApiKey("ออกลิงก์พอร์ทัลผู้ขาย")`
  - ตรวจ `recipientEmail` ด้วยตัวตรวจอีเมลกลาง (อีเมลเดียว)
  - ส่งผ่าน EmailQueue (หรือ rate limit ต่อบริษัท) แทน TrySendAsync ตรง
  - เอาแถวออกจาก baseline ของ checker

### M4 — recurring.html: กดบันทึกหลักเมื่อโหลดตัวเลือกไม่สำเร็จ = ล้างช่องทางรับเงินของผู้ติดต่อเงียบ ๆ
- `Accounting/wwwroot/pages/recurring.html` `_saveContactAutopilot` ส่ง `paymentMode: fCustPayMode.value || ''` เสมอ ("" = **ล้าง** ตาม DTO) ขณะที่ช่องหลังชำระ guard ด้วย `this._apOpts` แล้ว
- **สถานการณ์ (ทางเข้าใหม่ของแพ็กนี้):**
  - `getRecurringAutopilotOptions` ล้ม ⇒ select ไม่มี option ⇒ value = ""
  - ผู้ใช้ติ๊ก "ไม่ส่งเอกสารอัตโนมัติ" (`change` ใน `fContactPayGroup` ⇒ `_contactDirty`)
  - กดบันทึกรายการ ⇒ ระบบบันทึกค่าผู้ติดต่อต่อให้ ⇒ `PaymentMode` ที่ตั้งไว้ (เช่น TransferOnly) ถูกล้างเป็น Inherit
  - ผล: ลิงก์ชำระของทุกใบของผู้ติดต่อเปลี่ยนช่องทาง
- เดิมเกิดเฉพาะเมื่อกดปุ่ม "บันทึกที่ผู้ติดต่อ" เอง ตอนนี้เกิดจากปุ่มหลัก
- แผงใน contacts.html guard ถูกแล้ว (`this._apOpts ? … : null`)
- **แก้ขั้นต่ำ:** `paymentMode: this._apOpts ? (v || '') : null` ให้สมมาตรกับ contacts.html

---

## LOW

- **L1** — `PortalService.GetDocumentPayLinkAsync:727-729`: เมื่อไม่มีกล่องโอน `Refusal` = `link.Refusal` ซึ่งเป็นข้อความถึงพนักงาน ("ยังไม่ได้ตั้ง URL ของระบบ (App:BaseUrl)…" / "ตรวจช่องทางในตารางส่งเอกสาร/ค่าตั้งส่งเอกสารอัตโนมัติ") และแสดงตรงให้ลูกค้าใน `portal.html` (`esc(link.refusal)`)
  - แก้: map เหตุภายในเป็นข้อความลูกค้า "ร้านยังไม่เปิดช่องทางชำระ — กรุณาติดต่อร้าน" แล้ว log/แจ้งเจ้าของแทน
- **L2** — `PortalService.cs:720`: `_payLinks == null` โยน `InvalidOperationException` ซึ่ง controller `DocumentPayLink` จับแค่ `KeyNotFoundException` ⇒ ไปตก middleware (ขัด §4E "business error ชนิดเฉพาะ") ให้คืน `PortalPayLinkResponse(null, เหตุ, false)` แทน
- **L3** — `PortalDtos.cs:30-34`: doc-comment ของ `PortalSlipUploadResponse` ถูกแทรกคั่น ⇒ `PortalPayLinkResponse` มี `<summary>` สองก้อน ส่วน `PortalSlipUploadResponse` ไม่มีเลย ให้ย้ายก้อนเดิมกลับไปอยู่เหนือ record ของมัน
- **L4** — `layout.js formDialog`:
  - `min`/`max` ไม่ถูกตรวจใน `accept()` (มีแค่ `Number.isFinite`) ⇒ `refund()` เสียด่านฝั่งหน้าเดิม `amount > 0 && ≤ max` (เซิร์ฟเวอร์ `GatewayRefundMath.Check` ยังกันอยู่ ⇒ ไม่ใช่เงินหลุด แต่ข้อความผิดมาช้า) และ `resolveRefund`/`recordLegacyRefund` รับยอดติดลบไปถึงเซิร์ฟเวอร์
  - เรียก formDialog ซ้อนขณะยังเปิดอยู่ ⇒ promise ตัวแรกไม่ resolve (handler ถูกแทน)
  - แก้: ตรวจ `f.min`/`f.max` ใน accept และ resolve(null) ของตัวเก่าก่อนเปิดใหม่
- **L5** — `wht-credit.html setWhtReminder`: PUT `document-auto-send/settings` ต้องเป็นเจ้าของ (`EnsureOwnerAccessAsync`) แต่ checkbox เปิดให้ทุกคนที่ GET ได้ ⇒ ผู้ไม่ใช่เจ้าของกดแล้วได้ 403 + ย้อนค่า (ไม่เงียบ แต่ขัด §4A "ล็อกช่อง + บอกเหตุ")
  - แก้: GET ส่ง `canEdit` แล้วหน้าล็อกพร้อมข้อความ "เฉพาะเจ้าของบริษัท"
- **L6** — `VendorPortalService.ListAsync` `Take(500)`: บริษัทที่ออกลิงก์เกิน 500 รายการจะไม่เห็นลิงก์เก่า (รวมลิงก์ลูกค้าเดิมที่ยังใช้ได้ถึง 365 วัน) ⇒ เพิกถอนไม่ได้
  - แก้: กรองสถานะ Active ก่อน หรือแบ่งหน้า
  - ข้อสังเกตตามคำตัดสิน 259-3-1: ลิงก์บทบาท Customer เดิมยังข้ามธงสิทธิ์พอร์ทัลลูกค้าจนหมดอายุ ไม่มีปุ่ม "เพิกถอนลิงก์ลูกค้าเดิมทั้งหมด" ⇒ ช่องโหว่ E-H2 ยังเปิดอยู่จนลิงก์สุดท้ายหมดอายุ
- **L7** — `documents.html` แถบ e-Tax เหลือลิงก์ไป `etax.html#etaxRdReview` ซึ่งไม่ได้เช็กว่าผู้ใช้หรือบริษัทเข้าเมนู e-Tax ได้ไหม (feature/permission) ⇒ ผู้ใช้ที่เคยกด "ตรวจแล้ว" ได้ที่หน้าเอกสาร อาจไม่มีทางไปต่อแล้ว
  - แก้: แสดงลิงก์ตามสิทธิ์เมนู หรือคงปุ่ม ack ไว้ให้ผู้ที่เข้าหน้า etax ไม่ได้

## ทางเข้าอื่น / ทิศตรงข้าม (ตอบ 3 คำถามฝ่ายค้าน)
1. **ทางเข้าอื่น:** สลิปจากหน้าชำระสาธารณะส่งยอดมาเอง จึงไม่เข้ากิ่ง net (ถูก) ส่วนอีเมล/ตารางส่งเรียก `EnsureLinkAsync` พร้อม scheduleChannels — ชนกับพอร์ทัลตาม M1
2. **ทิศตรงข้ามที่ไม่มีเทสต์:**
   - "ลิงก์ชำระถูกเพิกถอน ⇒ พอร์ทัลไม่ออกใหม่" (M1)
   - "เช็กลิสต์ ready ⇔ ปิดได้" (M2)
   - "ผู้ไม่มีสิทธิ์ Contact.Edit ออก/ดูลิงก์ผู้ขายไม่ได้" (M3)
3. **สถานะปลายทางที่ระบบประทับเอง:** "ส่งอีเมลแล้ว" มาจาก `TrySendAsync` ที่คืน true จริง (ถูก) ส่วน `PayLinkRevokedAt = null` ถูกประทับจากการกระทำของลูกค้า (M1)
