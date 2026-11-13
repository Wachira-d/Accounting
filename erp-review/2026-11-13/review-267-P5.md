# ฝ่ายค้าน รอบ 267 แพ็ก P5: LINE Login จำผู้ลงนาม · หลักฐานเส้น API · ผู้ขายรับทราบใบสั่งซื้อ · ใบวางบิลจ่ายทั้งใบ

- **ขอบเขต:** คอมมิต `20628ad0` (โค้ด) · `1423625a` · `09445a8b` (Db test) · `96ab543d` (รายชื่อ webhook event) บน `claude/booking-service-features-sdemob`
  - คำตัดสิน 267-1-9 · 267-1-13 · 267-1-70…77
  - DOCUMENT_FLOW §2.12n-P5
- **วิธีตรวจ:** checkout ปัจจุบัน (`3aecedd7`) ยังไม่มีคอมมิตชุดนี้ จึง `git archive 96ab543d` แตกไว้ใน scratchpad แล้วอ่านตรงจุด
  - เลขบรรทัดในรายงานนี้อ้างจากต้นไม้ของ `96ab543d`
  - **ไม่ได้คอมไพล์ และไม่ได้รันเทสต์** ในรอบตรวจนี้
- **ข้อจำกัด:** รายงานนี้อ่านอย่างเดียว ไม่ได้แก้โค้ด และไม่ได้คอมมิต

## สรุป

**ไม่พบ HIGH · MED 7 ข้อ · LOW 10 ข้อ** (รายการ "ตรวจแล้วไม่ใช่บั๊ก" อยู่ท้ายรายงาน)

คำตอบสั้นต่อคำถาม 5 หมวดที่สั่งมา:

1. **PDPA**
   - โปรไฟล์ไม่รั่วผ่าน API, audit, log หรือ webhook (ดู `AuditRedaction`, audit ที่ไม่มี PII, และ prefill ที่คืนเฉพาะผู้ถือตั๋วที่ LINE ยืนยันแล้ว)
   - แต่บนเครื่องใช้ร่วม คนถัดไปได้ prefill ของคนก่อนผ่านเซสชัน LINE ที่ค้างอยู่ (MED-1)
   - หลักฐานความยินยอมผูกกับข้อความที่ผู้ลงนามไม่เคยเห็น (MED-2)
   - DSR ที่ค้นด้วยชื่ออย่างเดียวดึงข้อมูลของคนชื่อซ้ำมาด้วย (MED-3)
   - endpoint ของ DSR ใช้ด่าน DPO ครบ
   - ถอนความยินยอมลบโปรไฟล์จริง แต่อีเมล, IP และ UserAgent ค้างอยู่บนแถว `PdpaConsentRecord` ตลอดไป (MED-4)
   - purge 24 เดือนถูกต้อง โปรไฟล์ไม่อยู่ใต้ legal hold ซึ่งถูกแล้ว แต่มี race เล็ก (LOW-6)
2. **LINE Login**
   - state ผูกบริษัท + token ของใบ + อายุ 10 นาที
   - redirect URI ได้มาจาก `SystemBaseUrl` ไม่ใช่จาก Host header
   - ตั๋วผูกบริษัท + hash ของ token ของใบ + อายุ 30 นาที ใช้ข้ามใบหรือข้ามบริษัทไม่ได้ แต่ใช้ข้ามเครื่องได้ถ้ามีตั๋ว (ตั๋วเป็น bearer)
   - production ไม่มีคีย์ = boot ไม่ขึ้น (`Program.cs:161`)
   - ที่เหลือคือ LOW-1 ถึง LOW-3
3. **เงิน**
   - ยอดไม่ตรงล้มดัง, WHT ถูกตัดออกจริง, และ VAT ตาม tax point ลงรายใบ
   - แต่เส้นใบวางบิลข้ามงานหลังรับชำระ เช่นการส่งใบเสร็จให้ลูกค้า (MED-5)
   - การคืนเงินผ่านผู้ให้บริการค้างป้าย "ต้องออกใบลดหนี้" ถาวร (LOW-7)
4. **หลักฐานเส้น API** ไม่ลงซ้ำเมื่อเรียกซ้ำด้วยลายเซ็นเดิม แต่ "ผู้เขียนเดียวครอบทุกทาง" ไม่จริง (MED-6)
5. **ผู้ขายรับทราบใบสั่งซื้อ**
   - HTML กับ QuestPDF ใช้บรรทัดชุดเดียวกัน และฝั่ง HTML ผ่าน HtmlEncode แล้ว
   - วันส่งมีด่านขั้นต่ำ (≥ วันที่ใบ) และขั้นสูง (≤ 3 ปี)
   - event ส่งได้จากเส้นเดียวที่มีอยู่ (หน้าเอกสาร)
   - แต่ออก Rev ใหม่ได้โดยไม่ยืนยันและไม่สร้างฉบับลงนามก่อน (MED-7)

---

## MED

### MED-1: เครื่องใช้ร่วม: คนถัดไปได้ prefill (ชื่อ · ตำแหน่ง · อีเมล) ของคนก่อนผ่านเซสชัน LINE ที่ค้างอยู่

- **ที่มา:**
  - `Helpers/CustomerSignerProfileRule.cs:99-101` `AuthorizeUrl` ส่งแค่ `response_type`, `client_id`, `redirect_uri`, `state` และ `scope=openid`
  - ไม่มี `prompt=login` / `max_age=0`
  - `CompleteLoginAsync` (`CustomerSignerProfileService.cs:~95-115`) คืน `Prefill` ทันทีที่ LINE คืน code
- **สถานการณ์ล้ม:**
  1. ผู้ลงนาม A เข้าด้วย LINE บนคอมพิวเตอร์สำนักงานหรือร้านที่ใช้ร่วมกัน แล้วติ๊กให้จำ
  2. เว็บ LINE จำเซสชันไว้ที่ `access.line.me` (auto-login / SSO cookie)
  3. คนถัดไป B เปิดลิงก์เอกสารของบริษัทเดียวกัน แล้วกด "เข้าด้วย LINE"
  4. LINE อนุมัติให้ทันทีในฐานะ A ⇒ ฟอร์มถูกเติมชื่อ, นามสกุล, ตำแหน่ง และอีเมลของ A
  5. ถ้า B ติ๊ก "จำไว้" ⇒ `RememberAsync` เขียนทับโปรไฟล์ของ A ด้วยข้อมูลของ B (คนละคน แต่กุญแจเจ้าของข้อมูลเดียวกัน)
  6. แถวหลักฐานของ B ยังอ้าง `SignerProfileId` ของ A ด้วย (`AppendAsync` → `SignerProfileId ??= _signerProfileId`)
- **ทำไมเป็น MED:** เป็นการเปิดเผยข้อมูลต่อคนอื่นที่ไม่ใช่เจ้าของ (PDPA ม.37 · กฎ #2 J) ซึ่งเป็นคำถามที่สั่งให้ตรวจตรง ๆ
- **แนะนำ:**
  - ใส่ `prompt=login` (หรือ `max_age=0`) ใน `AuthorizeUrl`
  - บนหน้าแสดง "เข้าเป็น {ชื่อที่จำไว้}" พร้อมปุ่ม "ไม่ใช่ฉัน" ซึ่งเคลียร์ตั๋ว
  - เพิ่มเทสต์ pure ที่ล็อกว่า URL มีพารามิเตอร์นี้

### MED-2: หลักฐานความยินยอม (EvidenceHash) ผูกกับข้อความที่ผู้ลงนามไม่เคยเห็น

- **ที่มา:**
  - `CustomerSignerProfileService.cs:149` ใช้ `ConsentText(false, sellerName)` ซึ่งเป็นภาษาไทยเสมอ
  - ข้อความนี้ยาว และมีประโยค "ข้อมูลที่ไม่ได้ใช้ 24 เดือนจะถูกลบ · ยังต้องติ๊กรับรองเองทุกครั้ง"
  - ค่า hash ของข้อความนี้ลงใน `ConsentEvidenceHash`
  - แต่สิ่งที่หน้าแสดงข้างช่องติ๊กคือ `F.lineRemember` (`CustomerSignText.cs:185` · `pay-document.html:524`) ซึ่งเป็นคนละข้อความ สั้นกว่า และไม่มีเรื่อง 24 เดือน
  - หน้าที่เป็นภาษาอังกฤษก็ยังได้ hash ของข้อความภาษาไทย
  - grep ทั้งเรพ: `CustomerSignerProfileRule.ConsentText` มีผู้เรียก 1 จุด และไม่มีจุดใดที่ส่งข้อความนี้ไปที่หน้า
- **สถานการณ์ล้ม:**
  1. เจ้าของข้อมูลหรือ PDPC ถามว่า "ผู้ลงนามยินยอมต่อข้อความใด"
  2. หลักฐานชี้ไปที่ข้อความที่ไม่เคยปรากฏต่อผู้ใช้ ⇒ พิสูจน์ความยินยอมที่ "ชัดแจ้ง" ตาม ม.19 ไม่ได้
  3. เงื่อนไขการเก็บ 24 เดือนก็ไม่เคยถูกแจ้งบนหน้า
- **แนะนำ:**
  - ส่ง `ConsentText(en, seller)` ตัวเดียวไปเป็นป้ายของช่องติ๊ก
  - ส่งภาษาที่หน้าใช้เข้า `RememberAsync`
  - เพิ่มเทสต์ที่ล็อกว่าข้อความบนหน้าตรงกับข้อความที่ถูก hash

### MED-3: DSR ที่ระบุแค่ชื่อและนามสกุล ดึงและลบข้อมูลของคนที่ชื่อซ้ำ

- **ที่มา:** `CustomerSignerProfileService.cs:285-303` (`Matches` · `MatchingEvidenceAsync`)
  - ถ้าส่งแค่ first + last ระบบจับคู่ทุกแถวของบริษัทที่ชื่อตรง โดยไม่ดูอีเมล, ผู้ติดต่อ หรือองค์กร
- **สถานการณ์ล้ม:**
  1. DPO รับคำขอ access/portability จาก "สมชาย ใจดี" (ชื่อพบบ่อย)
  2. ไฟล์ JSON ที่ส่งให้เจ้าของข้อมูลมีแถวหลักฐานของ "สมชาย ใจดี" อีกคนจากบริษัทคู่ค้าอื่น ซึ่งมีอีเมล, IP, ข้อความ และองค์กร
  3. ฝั่ง erase ก็ลบโปรไฟล์ LINE ของอีกคน และล้างแถวหลักฐานที่พ้นอายุเก็บของอีกคน (ย้อนกลับไม่ได้)
  4. ผลคือเปิดเผยข้อมูลบุคคลที่สามผ่านช่องทาง DSR เอง
- **ด่านสิทธิ์:** ครบแล้ว (`RequireDpoAsync` ทุกเส้น · erase มี `RejectApiKey`) จึงไม่ใช่ปัญหาว่าใครเรียกได้ แต่เป็นปัญหาว่าผลลัพธ์ "เป็นของใคร"
- **แนะนำ:**
  - ชื่อล้วนให้เป็นโหมด "พรีวิวให้ DPO เลือกแถว" ไม่ใช่ export หรือ erase ทันที
  - หรือบังคับอีเมล หรือชื่อ + ผู้ติดต่อ/องค์กร
  - เพิ่มเทสต์ทิศตรงข้าม: คนชื่อซ้ำต่างอีเมลต้องไม่ถูกแตะ

### MED-4: ถอนความยินยอม / DSR erase / purge ไม่ล้างอีเมลและ IP บนแถวความยินยอม

- **ที่มา:**
  - `CustomerSignerProfileService.cs:152` เก็บ `SubjectContact = email` (plaintext — แถวความยินยอมไม่ได้เข้ารหัส), `IpAddress` และ `UserAgent`
  - `RemoveAsync` (`:211-213`) และ `PurgeAsync` (`:246-249`) ตั้งแค่ `WithdrawnAt`/`WithdrawnReason`
  - `EraseAsync` ไม่แตะแถวความยินยอมเลย
  - `AccessAsync` ก็ไม่รายงานอีเมลและ IP ที่อยู่บนแถวนั้น
- **สถานการณ์ล้ม:**
  1. ผู้ลงนามกด "ลบข้อมูลที่จำไว้" หรือยื่น DSR erase
  2. ระบบตอบว่า "ลบแล้ว" แต่อีเมลส่วนตัว, IP และ UserAgent ยังอยู่ในฐานแบบ plaintext โดยไม่มีอายุสิ้นสุด
  3. รายงาน DSR access ก็ไม่บอกว่ามีข้อมูลนี้อยู่ (ม.30 ไม่ครบ)
- **ประเด็นที่ต้องตัดสิน:** ถ้าตั้งใจเก็บแถวความยินยอมไว้พิสูจน์การยินยอมและการถอน ต้องมีคำตัดสินและอายุเก็บ
  - ทางหนึ่งคือเก็บแค่ hash ของอีเมล
  - อีกทางคือล้างอีเมลและ IP เมื่อถอนครบระยะพิสูจน์
- **แนะนำเพิ่ม:** ใส่แถวความยินยอมใน `AccessAsync` ด้วย

### MED-5: ใบวางบิลจ่ายทั้งใบข้ามงานหลังรับชำระ (ส่งใบเสร็จ ฯลฯ) และติดป้ายแหล่งเงินเป็น "Manual"

- **ที่มา:** `DocumentPaymentHandler.cs:144-173` เรียก `CreateMultiDocPaymentAsync` ตรง
  - `CreateMultiDocPaymentAsync` (`DocumentService.cs:16098…`) ไม่มีพารามิเตอร์ `source`
  - และไม่เรียก `PostPaymentAutomationService.EnqueueCoreAsync`
  - grep ใน DocumentService พบ `EnqueueCoreAsync` 2 จุด คือ `:15927` (`CreatePaymentAsync`) และ `:16094` (`AfterPaymentRecordedAsync`) ไม่มีในเส้นหลายใบ
  - เส้นหลายใบแจ้งคู่ค้าด้วย `PaymentEntrySource.Manual` แบบตายตัว (`:16448`)
  - handler ไม่เรียก `AfterPaymentRecordedAsync` ตามหลัง
- **สถานการณ์ล้ม:**
  1. บริษัทตั้ง "หลังรับชำระ ⇒ ส่งใบเสร็จทางอีเมล/LINE"
  2. ลูกค้าจ่ายรายใบออนไลน์ ⇒ ได้ใบเสร็จ
  3. ลูกค้าคนเดียวกันจ่ายทั้งใบวางบิลออนไลน์ ⇒ ไม่ได้อะไรเลย (ใบเสร็จถูกออกในระบบ แต่ไม่ถูกส่ง)
  4. ผู้ซื้อคู่ค้าเห็นการแจ้งว่าเป็น "บันทึกมือ"
  5. ขัดกับสัญญา "ทุกทางเข้า 1:1" ของรอบ 250/264 และคำตัดสิน 267-1-9 ที่ว่า "ไม่มีเส้นเงินใหม่"
- **แนะนำ:**
  - หลัง `CreateMultiDocPaymentAsync` สำเร็จ ให้เรียก `AfterPaymentRecordedAsync(companyId, payment.Id, PaymentEntrySource.Gateway, …)`
  - หรือเพิ่ม `source` เข้าเส้นหลายใบ แล้วล็อกด้วย `required_call_site_check`

### MED-6: "ผู้เขียนหลักฐานเดียวของทุกทางลงนาม" ไม่จริง: การอนุมัติข้ามบริษัทและขั้น Customer ภายในไม่ลงแถวหลักฐาน และเส้น API ไม่ตั้งภาพฉาย

- **ที่มา:**
  - `CrossTenantWorkflowService.cs:~574-590` สร้าง `DocumentApproval` ApproverRole "Customer" (ลายเซ็นคู่ค้า) โดยไม่เรียก `AppendEvidenceAsync`
  - `SignatureApprovalService.ApproveAsync` (`:236…`) ขั้นที่ตั้งเป็น Customer ก็ไม่เรียก
  - DOCUMENT_FLOW §2.12n-P5 ข้อ 2 และ doc-comment ของ `AppendEvidenceAsync` อ้างว่า "ทุกทางลงนามใช้ตัวนี้"
  - เส้น API (`SignatureApprovalService.cs:532-557`) ลงแถว `Accepted` แต่ไม่ตั้ง `CustomerResponse` หรือ `QuotationAcceptedAt` ผลที่ตามมา:
    - `EvidenceForStaffAsync` (`CustomerDocumentResponseService.cs:1016-1017`) ตัดสิน `accepted = CustomerResponse == "Accepted"` ⇒ **คำเตือนอากรแสตมป์ (267-1-14) ไม่ขึ้นเลย**สำหรับใบเสนอราคางานบริการที่อนุมัติผ่าน API
    - เส้นนี้ไม่มี outbox ⇒ ผู้ขายไม่ได้รับแจ้งเตือนอากรแสตมป์ทาง `SignEventDispatcher` ด้วย
- **เรียกซ้ำใน Rev เดียวกัน:**
  - ลายเซ็นและชื่อเดิม ⇒ ไม่ลงซ้ำ (`reusable` branch) ถูกต้อง
  - ชื่อหรือลายเซ็นต่าง ⇒ ลงแถว `Accepted` แถวที่สองของ Rev เดิม และแถวแรกไม่ถูกทำเครื่องหมาย superseded (ขัด 267-1-11)
  - `ApproveDocumentAsync` ล้มหลัง commit ⇒ แถวหลักฐานบอก "ยอมรับ" ทั้งที่ใบยังไม่อนุมัติ
- **สถานการณ์ล้ม:**
  1. คู่ค้าอนุมัติใบเสนอราคางานบริการ 2 ล้านผ่าน API
  2. แผงหลักฐานไม่เตือนอากรแสตมป์ ⇒ ผู้ขายพลาดกำหนด 15 วัน
  3. DSR และรายงานหลักฐานไม่มีการอนุมัติข้ามบริษัท
- **แนะนำ:**
  - ต่อสาย CrossTenant ให้ใช้ผู้เขียนเดียว (หรือแก้ doc ให้ตรงขอบเขตจริง)
  - ให้คำเตือนอากรแสตมป์อ่านจากแถวหลักฐาน `Accepted` ของ Rev ปัจจุบัน ไม่ใช่จากภาพฉาย `CustomerResponse` อย่างเดียว

### MED-7: ใบสั่งซื้อที่ผู้ขายรับทราบแล้ว ออก Rev ใหม่ได้ทันที: ไม่ต้องยืนยัน และไม่สร้างฉบับลงนามก่อน

- **ที่มา:**
  - `DocumentService.cs:3071-3083` ตั้ง `bindingEvidence` เฉพาะเมื่อมี `QuotationAcceptedAt` หรือ `DeliverySignedAt`
  - ใบสั่งซื้ออยู่ใน `RevisableTypes` (`:4166`) และ "รับทราบ" เก็บใน `CustomerResponse` ⇒ ทั้งด่าน `AcknowledgeRevisionResetsAcceptance` และด่าน (2b) `EnsureSnapshotsBeforeRevisionAsync` ถูกข้าม
- **สถานการณ์ล้ม:**
  1. ผู้ขายรับทราบ PO Rev 0 พร้อมวันส่งที่ยืนยัน
  2. `TrySnapshotNowAsync` ล้มชั่วคราว ⇒ ฉบับที่ลงนาม "กำลังจัดเตรียม"
  3. ฝ่ายจัดซื้อแก้ราคาใน PO ⇒ Rev 1 ถูกออกเงียบ ๆ โดยไม่มีคำเตือนว่าการรับทราบของผู้ขายจะหมดผล
  4. ฉบับที่ลงนามของ Rev 0 ไม่ถูกสร้างก่อนเนื้อหาเปลี่ยน ⇒ หลักฐานที่สัญญาไว้กับผู้ขายหาย (บทเรียนฝ่ายค้าน 267-P2 M3 ซ้ำ)
- **แนะนำ:**
  - เพิ่ม `CustomerResponse == PoAcknowledged` เข้า `bindingEvidence`
  - เพิ่มเทสต์ Db "แก้ PO ที่รับทราบแล้วโดยไม่ยืนยัน ⇒ 400"

---

## LOW

- **LOW-1 (OAuth state ไม่ผูกเบราว์เซอร์):** `StateValid` ตรวจแค่บริษัท + token ของใบ + อายุ
  - สิ่งที่กัน login-CSRF ได้ตอนนี้มีแค่ฝั่ง JS: `pay-document.html:82` รับ `code/state` เฉพาะเมื่อ sessionStorage มี `LINE_RETURN`
  - สถานการณ์: ผู้โจมตีที่มีลิงก์ใบเดียวกันส่ง `?code=&state=` ของบัญชีตัวเองให้เหยื่อที่กำลังอยู่กลางทางไป LINE ⇒ เหยื่อได้ตั๋วของผู้โจมตี ⇒ ถ้าเหยื่อติ๊กจำ ข้อมูลเหยื่อจะเข้าโปรไฟล์ LINE ของผู้โจมตี
  - ทำได้ยากมาก แต่ด่านอยู่ฝั่ง client เท่านั้น
  - แนะนำ: ผูก nonce ของ state กับ cookie `SameSite=Lax` ฝั่งเซิร์ฟเวอร์
- **LOW-2 (กุญแจเจ้าของข้อมูลถูกส่งให้เบราว์เซอร์):** ตั๋ว = `{exp}.{subjectKey}.{mac}` (`CustomerSignerProfileRule.cs:84-96`)
  - `SubjectKey` (`:64-65`) ไม่ผูกบริษัท ⇒ เป็นตัวระบุคงที่ของคนเดียวกันข้ามทุกบริษัทและทุกเอกสาร แล้วไปอยู่ใน sessionStorage และ network log
  - ทั้งที่ดีไซน์บอกว่า "ต่อบริษัท ไม่แชร์ข้ามบริษัท" (267-1-13)
  - แนะนำ: เปลี่ยนเป็น `Mac("subject|line|{companyId}|…")` และไม่ส่งคีย์ดิบในตั๋ว (ใช้ HMAC หรือเข้ารหัส)
  - ต้องมี migration ของแถวเดิม (F2 ข้อ 9)
- **LOW-3 (หมุน ENCRYPTION_KEY ทำโปรไฟล์กำพร้าเงียบ ๆ):**
  - `SubjectKey` และกุญแจตั๋วได้มาจาก ENCRYPTION_KEY
  - หมุนคีย์ ⇒ ผู้ลงนามทุกคนหาโปรไฟล์ไม่เจอ (ไม่เติม และไม่มีข้อความบอก) แล้วแถวเดิมค้าง 24 เดือนจน purge
  - ควรมีบันทึกใน runbook ของการหมุนคีย์ หรือแยกคีย์อิสระ
- **LOW-4 (PiiAccessLog ไม่ระบุเจ้าของข้อมูล):** `PdpaController.cs:300/316/340` บันทึก `Guid.Empty, "*"`
  - ม.37 / กฎ J ต้องการ `dataSubjectId`
  - ตอนนี้ย้อนไม่ได้ว่า DPO ดึงข้อมูลของใคร (ไม่มีอีเมลหรือชื่อที่ค้น แม้แต่แบบ hash)
- **LOW-5 (DSR เพดาน 5000 แถวเงียบ):** `MatchingEvidenceAsync` ใช้ `Take(5000)` โดยไม่บอกว่าตัดทิ้ง
  - ผลคือ erase/access ไม่ครบโดยไม่มีสัญญาณ (F2 ข้อ 7)
- **LOW-6 (purge race):** `PurgeAsync` (`:233-252`) อ่านรายการแล้วลบตาม Id โดยไม่ตรวจ `LastUsedAt` ซ้ำใต้ WHERE
  - ผู้ลงนามที่เข้าด้วย LINE ช่วงงานกลางคืนรันอยู่ จะถูกลบโปรไฟล์และถอนความยินยอมทันทีหลังเพิ่งใช้
  - แก้ได้ด้วยการใส่ `LastUsedAt < cutoff || consent withdrawn` ใน `ExecuteDeleteAsync`
- **LOW-7 (คืนเงินของการจ่ายทั้งใบวางบิล ค้างป้าย "ต้องออกใบลดหนี้" ถาวร):**
  - `GatewayRefundService.CreditNoteStatesAsync` (`:946-966`) ถือว่า SourceKind Document "ตรวจย้อนได้" แล้วหาใบลดหนี้ที่ `RelatedDocumentId == ใบวางบิล` ซึ่งไม่มีวันมี
  - พนักงานออกใบลดหนี้ที่ใบแจ้งหนี้ครบแล้ว ป้ายก็ยังขึ้น ⇒ เป็นคำเตือนที่ฟ้องทุกใบ (F2 ข้อ 8)
  - 267-1-72 ยอมรับข้อจำกัดนี้ไว้แล้ว แต่ควรกรองชนิดใบวางบิลให้เป็น "ตรวจย้อนไม่ได้" แทนการฟ้องถาวร
  - `GatewaySourceEvidence` (`:52-56`) ก็หาการชำระมือด้วย `DocumentId == ใบวางบิล` ซึ่งไม่เจอการชำระของใบในบิล
    - ด่านกันรับซ้ำชั้นแรกจึงบอดสำหรับเส้นนี้
    - ด่านตัดสินชุดใบตอนเงินเข้ายังกันเงินซ้ำได้ ⇒ เงินไม่ลงผิด แต่สัญญาณเตือนชั้นแรกหาย
- **LOW-8 (ค่าคลาดเคลื่อน ±0.01 ลงไม่สะอาด):** `SettleRefusal` ยอมรับยอดที่ห่าง ≤ 0.01 (`BillingNoteCombinedPay.cs:72`)
  - เงินเกิน 0.01 ⇒ `CreateMultiDocPaymentAsync` เห็นเป็นยอดส่วนเกิน ⇒ ต้องมี `ExcessReason` ⇒ ล้มด้วยข้อความที่ไม่เกี่ยวกับใบวางบิล
  - เงินขาด 0.01 ⇒ จัดสรรรวมเกิน `Payment.Amount` 0.01 ⇒ ลูกหนี้ปิดเต็ม แต่การชำระบันทึกต่ำกว่าที่จัดสรร
  - แนะนำ: ให้เท่ากันพอดี (เงินบาทจากผู้ให้บริการเป็นสตางค์เต็มอยู่แล้ว)
- **LOW-9 (หน้าใบวางบิลยิง query หนัก ไม่ล็อกอิน):** หน้าเปิดหนึ่งครั้งเรียก `BillingNotePlanAsync` 2–3 รอบ (`AmountsAsync` + `PayRefusalAsync` ×2)
  - แต่ละรอบทำ `LoadDocAsync` + `AmountsAsync` ต่อใบ ≤ 50 ใบ ⇒ หลายร้อย query ต่อการเปิดหนึ่งครั้งบนเส้น anonymous
  - ควร memo plan ต่อคำขอ
- **LOW-10 (webhook ใบสั่งซื้อไม่มีข้อมูลที่คำอธิบายสัญญา):**
  - คำอธิบายใน `96ab543d` บอกว่า "optional confirmed delivery date" และ "with a reason"
  - แต่ `SignEventOutbox.BuildPayload` (`Helpers/SignEventOutbox.cs:63-80`) ไม่มีวันส่งหรือเหตุ ⇒ ผู้เชื่อมต่อต้องเรียก API ซ้ำเองโดยไม่รู้
  - แนะนำ: เพิ่ม `promisedDate` ใน payload หรือแก้คำอธิบาย
- **LOW ปลีกย่อย (ยังไม่ยืนยัน):** `DecidePromisedDate` เทียบกับ `poDate.Date` ของค่าที่เก็บเป็น UTC
  - ถ้า `DocumentDate` ถูกเก็บเป็นเที่ยงคืนเวลาไทย (= 17:00Z ของวันก่อน) จะยอมรับวันส่งที่ก่อนวันที่ใบ 1 วัน
  - ไม่ได้ตรวจว่าระบบเก็บ `DocumentDate` แบบใด: ควรเทียบด้วยวันปฏิทินไทย (`ThaiDate.CalendarDateUtc`)

---

## ตรวจแล้วไม่ใช่บั๊ก

1. **โปรไฟล์ไม่รั่วผ่าน audit, log หรือ webhook**
   - audit ของ Remember/Remove/Purge ไม่มีชื่อหรืออีเมล
   - `AuditRedaction` มี `CustomerSignerProfile` + `SubjectKey`
   - `AuthService` log แค่ข้อความ exception
   - payload ของ webhook ไม่มีข้อมูลโปรไฟล์
   - ไม่มี endpoint อื่นอ่าน `CustomerSignerProfiles` (grep: ผู้ใช้เฉพาะ service, DSR และ purge)
2. **ด่านสิทธิ์และ production fail-fast**
   - DSR `dsr/signer/*` ผ่าน `RequireDpoAsync` ทุกเส้น และ erase มี `RejectApiKey` + "I confirm"
   - production ไม่มีคีย์ ⇒ `RequireUsable` throw ตอนบูต (`Program.cs:161-163`)
3. **ความปลอดภัยของ state, ตั๋ว และการแลก code**
   - ตั๋วข้ามใบหรือข้ามบริษัทไม่ได้: MAC ครอบบริษัท + hash ของ token ของใบ + subject + เวลาหมดอายุ (`:84-96`)
   - การเทียบใช้ `FixedTimeEquals`
   - code ของ LINE ใช้ได้ครั้งเดียว และถูกผูก `redirect_uri` ที่ LINE ⇒ code ของหน้าเอกสารกับ code ของหน้าล็อกอินระบบสลับกันใช้ไม่ได้
   - เส้นผู้ลงนามไม่รับ id_token จากเบราว์เซอร์ (`ResolveSignerLineUserAsync`)
4. **ถอนความยินยอมลบจริง**
   - `RemoveAsync` ใช้ `ExecuteDeleteAsync` และประทับ `WithdrawnAt` ในธุรกรรมเดียว
   - `ActiveProfileAsync` ไม่เติมเมื่อความยินยอมถูกถอน
5. **purge 24 เดือนถูกต้อง**
   - `AddMonths(-24)` + ความยินยอมหายหรือถอน ⇒ ลบ
   - ไม่ต้องมี legal hold เพราะเป็นข้อมูลฐานความยินยอม
   - แถวหลักฐานการลงนาม (ฐานสัญญา) ไม่ถูกแตะ
   - รันใต้ JobLock เดิม
6. **เงิน: ยอดไม่ตรงล้มดัง ไม่ลงเงียบ** (`SettleRefusal` → `BusinessRuleException` → ประวัติ intent + แจ้งเจ้าของ)
   - ใบในชุดถูกชำระ ยกเลิก หรือลดหนี้ระหว่างจ่าย ⇒ plan ใหม่มียอดต่าง ⇒ ล้ม
   - `CreateMultiDocPaymentAsync` ล็อกแถว `FOR UPDATE` และกันจัดสรรเกินยอดค้างอีกชั้น
   - webhook ซ้ำถูกกันด้วย `Reference` ก่อนถึงเส้นใบวางบิล
7. **WHT ถูกตัดออกจริง**
   - ชุดที่มี WHT ที่ผู้ซื้อต้องหักถูกปฏิเสธตั้งแต่ plan
   - allocation ส่ง `WithholdingTaxAmount: 0m` แบบ HasValue ⇒ ไม่ตกไปใช้ยอดแนะนำ
8. **VAT และใบเสร็จรายใบ**
   - เส้นหลายใบเรียก `TryReclassifyUndueOutputVatAsync` ต่อใบด้วยวันชำระ (tax point §78/1 รายใบ)
   - เส้นหลายใบออกใบเสร็จรายใบผ่าน `TryAutoIssueAllocationReceiptAsync`
9. **กระดาษ PO สองตัว**
   - บล็อกผู้ขายบน HTML และ QuestPDF ใช้ `SupplierAckLines` ตัวเดียว ป้ายมาจาก `DocumentLabels` และฝั่ง HTML ผ่าน HtmlEncode ทุกบรรทัด
   - ช่อง `SupplierAckSlot=3` ไม่ชนช่องเทมเพลต (ไม่มี `SignatureLabel4`)
10. **event ของ PO:** มีเส้นตอบเดียว (หน้าเอกสาร) และ enqueue outbox ในธุรกรรมเดียวกับการจอง `CustomerResponse`
    - ออก Rev ใหม่ล้างคำตอบ (`CloseRespondOnRevision`)
    - `MarkIssued` คง `PoAcknowledged` ไว้ ซึ่งถูกตามดีไซน์
11. **เส้นแต่ละส่วนไม่พึ่งกัน**
    - จำข้อมูลไม่สำเร็จไม่ย้อนการลงนาม และบอกผู้ลงนามตรง ๆ
    - ปิด LINE Login ⇒ ไม่มีปุ่ม แต่ลงนามได้ครบ (kill-switch ผ่าน · ไม่มี AI ในเส้นนี้)

---

## สถานะการแก้ (ชุดแก้ฝ่ายค้าน — คำตัดสิน 267-1-78…85)

| ข้อ | สถานะ | ที่แก้ / เหตุที่ไม่แก้ |
|---|---|---|
| MED-1 | แก้แล้ว | `AuthorizeUrl` + `max_age=0&disable_auto_login=true` · หน้าล้างตั๋วหลังลงนาม + ปุ่ม "ไม่ใช่ฉัน" · `SignedAsProfile` ก่อนอ้างโปรไฟล์ · เขียนทับได้เฉพาะ LINE identity เดียวกัน (กุญแจ) |
| MED-2 | แก้แล้ว | `FormLabelsWithConsent` (ป้ายช่องติ๊ก = `ConsentText` ภาษาของหน้า) · `signerConsentVersion` · `PolicyVersion` = รุ่น/ภาษา · hash ครอบรุ่น + เวลาแบบที่ฐานเก็บ |
| MED-3 | แก้แล้ว | `SignerQuery` (อีเมล หรือ ชื่อ+นามสกุล+เลขเอกสาร) · `SignerSubjectAmbiguousException` ⇒ 409 + อีเมลปิดบัง · เทสต์ทิศตรงข้ามคนชื่อซ้ำ |
| MED-4 | แก้แล้ว | `AnonymizeConsentsAsync` ในถอน / erase / purge · รายงาน access มีแถวความยินยอม |
| MED-5 | แก้แล้ว | `AfterPaymentRecordedAsync(Gateway)` + `AfterCommitByCaller` · งานหลังรับชำระส่งใบเสร็จรายใบ · Db test แถวงาน Source Gateway |
| MED-6 | แก้แล้ว (บางส่วนตามเหตุ) | ข้ามบริษัท + พนักงานบันทึกแทน ลงแถวผ่าน `ExternalEvidence` → `AppendEvidenceAsync` · API/ข้ามบริษัทตั้ง `CustomerResponse` · ไม่ทำ superseded บนแถวเดิม / แถวเมื่ออนุมัติล้ม / outbox ของ API — เหตุใน 267-1-83 |
| MED-7 | แก้แล้ว | `bindingEvidence` รวม `PoAcknowledged` · `PendingSignedCopyIdsAsync` รวม `PoAcknowledged` · Db test |
| LOW-1 | แก้แล้ว | nonce ใน cookie `na_signer_line_nonce` (HttpOnly · Secure · SameSite=Lax) · `StateValid` ตรวจ nonce |
| LOW-2 | แก้แล้ว | `SubjectKey(companyId, …)` · ตั๋ว AES-GCM · ย้ายแถวกุญแจรุ่นแรกเมื่อเจ้าของกลับมา |
| LOW-3 | ไม่แก้โค้ด (เหตุ) | ไม่แยกคีย์อิสระ — เพิ่มคีย์ที่ต้องดูแลโดยไม่ได้ประโยชน์ (ข้อมูลตามความยินยอม กรอกใหม่ได้) · บันทึก runbook ใน DOCUMENT_FLOW §2.12n-P5 ข้อ 7 + doc-comment ของ `ConfigureKey` |
| LOW-4 | แก้แล้ว | `LogSubject` (Guid จาก hash ของตัวระบุ + ชื่อช่อง) |
| LOW-5 | แก้แล้ว | `DsrEvidenceCap` — เกิน = ล้มดังก่อนลบอะไร |
| LOW-6 | แก้แล้ว | เงื่อนไขซ้ำใน WHERE ของการลบ · ถอน/ล้างเฉพาะความยินยอมของแถวที่ลบจริง |
| LOW-7 | แก้แล้ว | `BillingNoteChildren` — ใบลดหนี้ของใบในบิลนับ (ออกครบ = งานปิดได้) · `GatewaySourceEvidence` ดูการจัดสรรของใบในบิล |
| LOW-8 | แก้แล้ว | `SettleRefusal` เท่ากันพอดี |
| LOW-9 | แก้แล้ว | จำผลชุดใบ 5 วินาทีต่อการเปิดหน้า (`BeginPlanMemo`) |
| LOW-10 | แก้แล้ว | `promisedDate` ใน payload · คำอธิบาย event ไม่รับ แก้ให้ตรง (เหตุไม่อยู่ใน payload) |
| LOW ปลีกย่อย | แก้แล้ว | `DecidePromisedDate` เทียบ `ThaiDate.CalendarDateUtc(poDate)` |
