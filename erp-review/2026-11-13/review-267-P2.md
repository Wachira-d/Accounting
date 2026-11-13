# รอบ 267 แพ็ก P2 — ผลตรวจฝ่ายค้าน (ผู้ลงนาม · บัญชีหลักฐาน · ฉบับที่ลูกค้าเซ็น)

ขอบเขต: 8b6861de + bda2d3a8 · fd2d1951 · 52b70290 · 86c74e0d · b14168bd (ถึง 2b32aacb) · อ่านโค้ดอย่างเดียว ไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้ ·
สรุป: **1 HIGH · 3 MED · 8 LOW** · ทุกข้อชี้ไฟล์:บรรทัดที่อ่านจริงบน FETCH_HEAD

---

## HIGH

### H1 — ข้อมูลส่วนบุคคลของผู้ลงนามถูกคัดลอกลง audit (append-only) ⇒ ลบตามคำขอ (ม.33) แล้วยังมีสำเนาเหลือ
- `Accounting/Helpers/AuditRedaction.cs:46-60` — `PiiFields` ไม่มี `CustomerDocumentAction` และไม่มี `CustomerSignOtp`
- `Accounting/Services/Implementations/AuditTrailService.cs:180-239` — `CaptureAuditEntries` เก็บทุกช่องของ entity ที่ Added ⇒ แถว Create ของ
  `CustomerDocumentAction` (`CustomerDocumentResponseService.cs:227/338/438` `_db.CustomerDocumentActions.Add`) มี `SignerFirstName/LastName/Title/OrgName/Email/Message/Ip/UserAgentHash/SignatureFilePath`
  เป็นข้อความเปล่า — fd2d1951 เห็นแถวนี้แล้ว (เทสต์ `AcceptFullIdentity` assert ว่ามีแถว Create) แต่ไม่ได้ปิดค่า
- `CustomerDocumentResponseService.cs:229-245, 340-355, 440-454` — audit ที่เขียนเองใส่ `acceptedBy/respondedBy/signedBy`, `signerTitle`, `signerOrgName`, `message` ใน NewValues และ IP ใน `AuditLog.IpAddress`
- `PdpaService` erase (diff 8b6861de) ล้างเฉพาะแถวหลักฐาน — audit แตะไม่ได้ (hash chain) และ `RedactJson(json, entityType)` ตอนแสดงก็ไม่ครอบเพราะไม่มี entry
- สถานการณ์: ผู้ลงนามยื่นลบหลังพ้นอายุเก็บ → แถวหลักฐานเป็น "ข้อมูลถูกลบ" แต่ `GET /audit/logs` (ทุกบทบาทในบริษัทอ่านได้) ยังแสดงชื่อ นามสกุล ตำแหน่ง อีเมล IP ข้อความ ตลอดไป
  = ขัดกฎ #2 J (Retention by purpose · DSR erase) และหลัก "PII ห้ามคัดลอกลง audit" ของรอบ 214 B1
- แก้ขั้นต่ำ: เพิ่ม `["CustomerDocumentAction"] = { SignerFirstName, SignerLastName, SignerTitle, SignerOrgName, SignerEmail, Message, Ip, UserAgentHash, SignatureFilePath, SignedPdfPath }`
  และ `["CustomerSignOtp"] = { EmailHash, CodeHash }` ใน `PiiFields` (ปิดทั้งตอนเขียนและตอนแสดงแถวเก่า) · audit ที่เขียนเองอ้าง `customerActionId` แทนชื่อ/ตำแหน่ง/ข้อความ
  (คง IP ใน audit เฉพาะถ้าตัดสินว่าเป็น legal hold — แล้วจดใน RoPA) · เทสต์ทิศตรงข้าม: แถว Create ของหลักฐานใน audit ไม่มีชื่อ/อีเมลของผู้ลงนาม

## MED

### M1 — hash ของรหัส OTP อยู่ใน audit และไม่มีกุญแจลับ ⇒ ผู้อ่าน audit กู้รหัสได้ในเสี้ยววินาที
- `CustomerSignRule.cs` `OtpHash = SHA256("{docId:N}|{email}|{code}")` (ไม่มี HMAC/secret) · `CustomerDocumentResponseService.cs:591-597` `_db.CustomerSignOtps.Add` + `SaveChanges`
  ⇒ auto-audit เก็บ `CodeHash` + `EmailHash` (ชื่อช่องลงท้าย `Hash` เฉย ๆ — ไม่เข้า `SensitiveSuffixes` ที่มีแค่ `KeyHash/TokenHash`)
- สถานการณ์: พนักงานบทบาทใดก็ได้อ่าน audit → รู้ docId · ลองอีเมลผู้ติดต่อเทียบ `EmailHash` → วนรหัส 10⁶ ค่า (ทันที) → ภายใน 10 นาที ออกลิงก์ใหม่ (OTP ผูกใบ+อีเมล ไม่ผูก token)
  แล้วยอมรับในนามลูกค้าด้วยแถว `EmailVerified=true` — การ์ดเดียวที่แยก "ลูกค้ายืนยันอีเมลจริง" ออกจาก "พนักงานกดเอง" ถูกปลอมได้
- แก้: H1 (ใส่ `CustomerSignOtp` ใน PiiFields) + `OtpHash` เป็น HMAC-SHA256 ด้วยกุญแจฝั่งเซิร์ฟเวอร์ (fail-fast ใน production ตามกฎ #4 C)

### M2 — ลิงก์รุ่นก่อน (L1/L2) ข้าม "หนึ่งคำตอบต่อ Rev" และข้ามเกณฑ์ OTP
- `CustomerDocumentResponseService.cs:132` `otpRequired = viaPage && …` · `:201` `(!viaPage || d.CustomerResponse == null)` · `:213` เขียน `CustomerResponse = Accepted` ทับ
- ใบที่ออกลิงก์รอบ 240–266 มี `SignLinkIssuedAt` (เดิม `SetNewToken` ประทับ — `git show 67eddfe6~1:Accounting/Helpers/SignRequestKinds.cs:76`) และ `QuotationAcceptToken` ยังมีชีวิตจนหมดอายุ ·
  ถ้าใบเดียวกันมี token ของหน้าเอกสาร (ออกจาก `IDocumentPayLinkService` โดยไม่ผ่าน `MarkIssued`) ปุ่มไม่รับ/ยอมรับบนหน้าเปิดได้
- สถานการณ์: ลูกค้ากด "ไม่รับ" บนหน้า → ต่อมาอีกคน (หรือคนเดิม) เปิดลิงก์ยอมรับเก่าในอีเมล → ยอมรับสำเร็จทับคำตอบ Declined · ใบเกินเกณฑ์ OTP ก็ยอมรับได้โดยไม่มีรหัส → Autopilot แปลงใบ
- แก้: ตัด `!viaPage ||` (ทุกเส้นเคารพ `CustomerResponse`) และให้ลิงก์รุ่นก่อนตอบ 409 "กรุณาใช้ลิงก์ใหม่จากผู้ขาย" เมื่อยอดถึงเกณฑ์ OTP · เทสต์: Decline ทางหน้า → Accept ทาง legacy = 409

### M3 — ฉบับที่ลูกค้าเซ็นที่สร้างไม่ทันก่อนออก Rev = หายถาวร · หน้าลูกค้าขึ้น "กำลังเตรียม" ตลอดไป · พนักงานไม่ถูกแจ้ง
- `CustomerDocumentResponseService.cs:664-665` `SnapshotStillValid` ไม่ผ่าน ⇒ `Done=true, HasFile=false`
- `SignEventDispatcher.cs:151-156` Done ⇒ ประทับ `SignedCopyDoneAt` + `LastError` เท่านั้น (ไม่เรียก `NotifySignedCopyFailedAsync` — เรียกเฉพาะตอนครบจำนวนครั้ง)
- `CustomerDocumentResponseService.cs:560` `SignedCopyExpected = signs && Channel==Link && ErasedAt==null` ไม่รู้ว่าเลิกสร้างแล้ว ⇒ `pay-document.html` แสดง `signedCopyPending` ("เปิดหน้านี้อีกครั้งในอีกสักครู่") ไม่มีวันจบ
- `CustomerSignText.cs:107-…` `Done(Accepted)` บอกลูกค้า "ดาวน์โหลดฉบับที่ลงนามได้จากหน้านี้" แม้ `signedCopyReady=false`
- สถานการณ์: ลูกค้ายอมรับ → render ทันทีล้ม (Chromium ชั่วคราว) → ก่อน outbox ลองใหม่ พนักงานแก้ใบ (กดรับทราบ acknowledgeRevisionResetsAcceptance) →
  ไม่มีไฟล์ของฉบับที่ลงนามเลย ทั้งที่ consent สัญญาว่าเก็บ "สำเนาเอกสารฉบับที่ท่านเห็น" (F2 ข้อ 7: ล้มเงียบ/บอกสิ่งที่ไม่จริง)
- แก้ขั้นต่ำ: ในเส้น revision (`DocumentService.cs:3066-3105`) เรียก `EnsureSnapshotAsync` ของแถวหลักฐานของ Rev เดิมก่อน `RevisionNumber++` (ล้ม = ไม่ให้ออก Rev จนกว่าจะสร้างได้ หรือ render จาก `DocumentRevision.SnapshotJson`)
  · ทางที่เลิกสร้างประทับสถานะบนแถวหลักฐาน/outbox ให้ `SignedCopyExpected=false` + แจ้งพนักงานเหมือนเส้นครบจำนวนครั้ง · ข้อความ Done เลือกตาม `signedCopyReady`

## LOW

- **L1 TOCTOU ระหว่างตรวจ Rev/hash กับ render** — `CustomerDocumentResponseService.cs:664` ตรวจ → `:666` render (หลายวินาที) → `:672-674` จองแถวหลักฐานโดยไม่ตรวจใบซ้ำ ·
  พนักงานออก Rev ในช่วงนั้น ⇒ PDF ของ Rev ใหม่ (renderer `CustomerEvidenceAsync` กรอง `RevisionNumber == doc.RevisionNumber` ⇒ ไม่มีบล็อกผู้ลงนามด้วย) ถูกประทับเป็น "ฉบับที่ลงนาม" ของ Rev เดิมพร้อม SHA-256 ·
  แก้: ในธุรกรรมจอง อ่านใบ `FOR UPDATE` แล้วเทียบ Rev + `DocumentSignedContent.Hash` อีกรอบ ไม่ตรง = rollback ไม่เขียนไฟล์
- **L2 trigger ยอมการ "ลบ PII" ที่เขียนค่าใหม่อะไรก็ได้** — `CustomerSignMigration.cs:81,103` `erasing` เปิดให้ทุกช่อง PII เปลี่ยนเป็นค่าใด ๆ (ปลอมชื่อพร้อมประทับ ErasedAt ได้) และให้เติม `SignedPdfSha256/Path` ค่าใดก็ได้ในคำสั่งเดียวกัน ·
  `CreatedBy/UpdatedBy` ไม่ถูกคุม · ไม่มีกัน `TRUNCATE` · แก้: ตอน erasing บังคับ `NEW.<pii> IS NULL` (ชื่อ = ค่าคงที่ `Anonymized`) และ `NEW.SignedPdfSha256 IS NOT DISTINCT FROM OLD`, เพิ่ม `CreatedBy` ในชุดห้ามแก้, `BEFORE TRUNCATE` trigger
  (ข้อ 4 ของผู้เขียน: ระหว่างเติมฉบับเซ็นแบบไม่ erasing ช่องอื่นแก้ไม่ได้จริง — ยืนยันแล้ว)
- **L3 ตัวนับ OTP แข่งกันได้** — `CustomerDocumentResponseService.cs:618-625` อ่าน `Attempts` นอกธุรกรรมแล้วเพิ่มแบบไม่มีเงื่อนไข ⇒ คำขอขนานจากหลาย IP ผ่านด่าน `attempts >= 5` พร้อมกัน ·
  แก้: `ExecuteUpdate(... Where Attempts < OtpMaxAttempts)` แล้วถือว่า 0 แถว = TooManyAttempts ก่อนเทียบรหัส
- **L4 ข้อความยินยอมไม่ตรงกับสิ่งที่เกิดจริงสำหรับ "ไม่รับ/ขอแก้"** — `CustomerSignText.Consent` ใช้ข้อความเดียวทุกชนิด: "บันทึก…สำเนาเอกสารฉบับที่ท่านเห็น" และ "ชื่อ ตำแหน่ง และวันเวลาจะพิมพ์บนเอกสารฉบับนี้"
  แต่ Declined/ChangeRequested ไม่สร้างฉบับเซ็นและไม่พิมพ์อะไร (`EnsureSnapshotAsync:656`) · ประกาศตาม PDPA ม.23 ต้องตรงจริง · แก้: ข้อความต่อชนิด + ขยับ `Version`
  (อ้างกฎหมาย ม.9/ม.11 เท่านั้น — ไม่พบการอ้างเกินบนหน้า/PDF)
- **L5 hash เนื้อหาที่ลูกค้าเห็นไม่บังคับ** — `CustomerDocPage.cs:194` ตรวจเฉพาะเมื่อส่งมา · ช่องที่แก้ได้โดยไม่ขยับ Rev หลังเปิดหน้า ⇒ แถวหลักฐานเก็บ hash ของเนื้อหาที่ลูกค้าไม่ได้เห็น · แก้: `requireSeen` ⇒ hash ว่าง = 409
- **L6 ฝั่งพนักงาน** — `DocumentController GetCustomerSignedCopy` ไม่ลง `LogPiiAccessAsync` (PDF มีชื่อ/ตำแหน่ง/ลายเซ็น) ทั้งที่แผงหลักฐานลง · IP/อีเมลผู้ลงนามเปิดให้ทุกคนที่อ่านเอกสารได้ (ไม่มีชั้น `pii:view`) — พิจารณาซ่อน IP นอกบทบาทนั้น
- **L7 อีเมลรหัสส่งไปที่อยู่ใดก็ได้** — `RequestOtpAsync:581-603` ผู้ถือลิงก์ใส่อีเมลใดก็ได้ ระบบส่งเลขใบ + ชื่อผู้ขายในนามอีเมลระบบ (1 ครั้ง/นาที/ใบ · 30/วัน/IP) — ใช้เป็นช่องสแปม/รั่วเลขเอกสารได้เล็กน้อย · พิจารณาเพดานต่อใบต่อวัน
- **L8 "ผู้ขายได้รับแจ้งแล้ว" ประทับก่อนแจ้งจริง** — `CustomerSignText.Done` (Accepted/Declined) บอกลูกค้าว่าแจ้งแล้ว ขณะที่การแจ้งอยู่ใน outbox (F2 ข้อ 3) · เปลี่ยนเป็น "ระบบจะแจ้งผู้ขาย"
- **L9 รายการช่องซ้ำจาก merge** — `SettlementPaidReissue.cs:409` กับ `:427` (`CustomerResponse/CustomerResponseAt`) และ `ReplacementOfNumber/Date` ซ้ำสองที่ใน `DocumentNotCarriedFields` · ไม่ผิดผล แต่เทสต์ "จัดกลุ่มครั้งเดียว" ในอนาคตจะล้ม — ลบชุดหนึ่ง

## รับทราบแล้ว (ไม่นับเป็นข้อค้น — 267-1-43)
- S11 ผู้ขายยกเลิกการยอมรับ ยังไม่มี (ทางแทน = ออก Rev) · `ExternalApprovalController` (`SignatureApprovalController.cs:124-160`) ไม่ลงแถวหลักฐาน/`CustomerResponse` ⇒ ใบที่ระบบภายนอกอนุมัติแล้วหน้าลูกค้ายังเปิดปุ่มตอบรับได้ — ควรปิดพร้อมแพ็กเก็บกวาด

## ตรวจแล้วไม่ใช่บั๊ก
- ข้อ 2 (แข่งยอมรับ/ใช้รหัสครั้งเดียว/ย้อนไฟล์ลายเซ็น): จองแบบมีเงื่อนไข + ประทับ `ConsumedAt` ในธุรกรรมเดียว · แพ้ = rollback + ลบไฟล์ (`:217-224, 255-264`) ถูก
- ข้อ 3 (ไฟล์ฉบับเซ็นเขียนก่อน commit): commit ล้ม = ไฟล์กำพร้าที่ path กำหนดตายตัวต่อแถว ไม่มีแถวชี้ · รอบถัดไปเขียนทับ path เดิม — ยอมรับได้
- XSS: `documents.html` แผงหลักฐาน escape ทุกค่า · `pay-document.html` ประวัติ/ฟอร์ม `esc()` · สอง renderer ผ่าน `CustomerSignerLines` + `HtmlEncode` ฝั่ง HTML (ไม่ drift) · อีเมล OTP/สำเนา encode · ข้อความแจ้งพนักงาน encode ใน `NotificationEngine:398`
- path traversal: ตำแหน่งฉบับเซ็นคำนวณจาก Guid + `IsOwnSignedCopyLocation` ตรวจตอนอ่าน · ลายเซ็นผ่าน `SignatureImageValidator`
- เหตุผลปฏิเสธเป็นรายการปิด · คำแนะนำแปลงไม่สร้างเอกสารเอง · `localStorage` เก็บแค่ชื่อ/ตำแหน่ง/อีเมล (opt-in) ไม่มี token · query ใหม่มี `CompanyId` ครบ · ข้อความรับรองมีรุ่น + SHA-256 ของข้อความที่ติ๊ก
