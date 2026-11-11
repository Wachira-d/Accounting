# ฝ่ายค้าน รอบ 259 แพ็ก B — ส่งทาง LINE (7dc53e0c · 78dcdbca · 4c86ae76)

ผู้ตรวจ: subagent ฝ่ายค้าน (อ่านอย่างเดียว · ไม่คอมมิต) · วันที่ 2026-11-11 · ฐาน = `origin/claude/booking-service-features-sdemob`
ขอบเขต: ลิงก์ชำระ (ส่งเอง LINE/อีเมล) · ลิงก์อัปโหลด 50 ทวิ บนการ์ด LINE · ผู้รับ/กันกดซ้ำ · migration ตัดหัวข้อ · webhook/ตัวจำกัด · HtmlEncode · ทางเข้าอื่น · tenant

สรุป: **HIGH 0 · MED 1 · LOW 6** — ด่านสิทธิ์/tenant/ลายเซ็น webhook/token 50 ทวิ แข็งแรง ปัญหาหลักคือ "ลิงก์ชำระถึงกลุ่มซ่อนยอด" ที่ขัดกับเหตุผลของคำตัดสิน 259-2-1 เอง

---

## MED

### M1 — ลิงก์ชำระส่งเข้ากลุ่ม LINE ที่ตั้ง "ซ่อนยอด" ได้ ⇒ ยอด/รายการ/ชื่อผู้ซื้อหลุดผ่านหน้า pay-link
- `Accounting/Services/Implementations/ContactLineQueueService.cs:349-353` (`BuildCardAsync`): `payUrl` คำนวณจาก `outstanding` อย่างเดียว ไม่ดู `showAmounts` — ปุ่ม "ชำระ" ถูกใส่ในการ์ดของกลุ่มซ่อนยอดด้วย ขณะที่ปุ่มดู PDF ถูกตัดด้วย `LineDocumentViewLink.EndpointAllows` (บรรทัด 355)
- `Accounting/Services/Implementations/DocumentLineSendService.cs:58-66`: `includePayLink` ใช้กับทุกปลายทางที่เลือก ไม่ตรวจว่ามีกลุ่ม `ShowAmounts=false` อยู่ในนั้น · `DocumentLineSendRule.Target` ไม่มีธงว่าปลายทางนี้ได้ปุ่มชำระไหม
- หน้า `wwwroot/pay-link.html:69-76` แสดง `buyerName · invoiceNumber · amountDue · lines`
- สถานการณ์: ผู้ติดต่อผูกกลุ่ม LINE (มีคนนอกฝ่ายบัญชีอยู่ในกลุ่ม) และตั้ง "ซ่อนยอด" → พนักงานกด 💬 ส่งทาง LINE ติ๊ก "ใส่ปุ่มชำระเงินออนไลน์" เลือกกลุ่มนั้น → การ์ดไม่มียอด แต่ใครในกลุ่มก็กดปุ่มชำระแล้วเห็นยอดค้าง รายการสินค้า และชื่อผู้ซื้อได้ทันที เส้นตารางส่ง (กฎที่ติ๊กลิงก์ชำระ) รั่วแบบเดียวกันตั้งแต่ก่อนรอบนี้ แต่รอบนี้เปิดให้ผู้ใช้เลือกเองได้ชัด ๆ ด้วย
- ขัดกับคำตัดสิน 259-2-1 ("กลุ่มซ่อนยอด ⇒ ไม่มีปุ่มอัปโหลด (หน้าอัปโหลดแสดงยอด)") และฝ่ายค้าน M1 ของรอบ 258 (ตัดปุ่ม PDF) — เหตุผลเดียวกันแต่ไม่ได้ใช้กับปุ่มชำระ
- แก้ขั้นต่ำ: (ก) `BuildCardAsync`: `payUrl = outstanding && LineDocumentViewLink.EndpointAllows(kind, showAmounts, null, null) ? await PayUrlAsync(...) : null` (ตัวตัดสินเดียวกับ PDF/อัปโหลด) · (ข) `DocumentLineSendRule.Target` เพิ่ม `PayLink` (= `PdfLink`) และให้ `SendAsync` ปฏิเสธ 400 พร้อมเหตุเมื่อ `includePayLink` แล้วเลือกกลุ่มซ่อนยอด (หรือบอกในคำตอบว่ากลุ่มนั้นจะไม่มีปุ่ม) · (ค) เทสต์ทิศตรงข้าม: กลุ่มแสดงยอด/ผู้ใช้ LINE ยังได้ปุ่มชำระ

---

## LOW

### L1 — ติ๊ก "ใส่ลิงก์ชำระ" บนอีเมลที่ถูกส่งทาง e-Tax แล้วหายเงียบ (silent no-op)
- `Accounting/Services/Implementations/DocumentEmailService.cs:239-256` (`SendDocumentRoutedAsync`): เส้น e-Tax สร้าง `SendEtaxByEmailRequest` โดยไม่ส่ง/ไม่ตรวจ `req.IncludePayLink`
- หน้าเว็บกันไว้แค่ `channel === 'EtaxByEmail'` (`documents.html:12045`) แต่ฝั่งเซิร์ฟเวอร์ไปเส้น e-Tax ด้วยเมื่อ `PlainCopyAfterEtax && ConfirmEtaxResend` ⇒ ผู้ใช้ติ๊กลิงก์ชำระ + ติ๊กส่ง e-Tax ซ้ำ → ส่งสำเร็จแต่ไม่มีลิงก์ ไม่มีข้อความบอก (คนเรียกผ่าน API ก็เจอแบบเดียวกัน) · ขัดกับ 259-2-2 "e-Tax by Email ไม่มีช่องลิงก์ชำระ" ที่ควรเป็นการปฏิเสธ ไม่ใช่การทิ้งค่า
- แก้: `if (etaxPath && req.IncludePayLink) throw new BusinessRuleException("e-Tax by Email ใส่ลิงก์ชำระไม่ได้ — เอาติ๊กออก…", "DOC-EMAIL-PAYLINK", 409);` + ซ่อนช่องในหน้าเมื่อ `PlainCopyAfterEtax && #seEtaxResend` ถูกติ๊ก

### L2 — ลิงก์ชำระที่ผู้ใช้ติ๊กตรวจตอนเข้าคิวเท่านั้น ตอนส่งหายไปได้แต่แถวยังขึ้น "ส่งแล้ว"
- `ContactLineQueueService.cs:381-382`: `IncludePayLink == true` ⇒ `CurrentUrlAsync` คืน null ได้ (ลิงก์ถูกเพิกถอน หรือบริษัทปิดช่องทาง ระหว่างคิว ~1 นาที หรือกด "ส่งใหม่" แถวที่ล้มหลายชั่วโมงต่อมา) → การ์ดส่งไปโดยไม่มีปุ่ม และแถวประทับ Sent โดยไม่มีหมายเหตุ ขัดกับ 259-2-2 ("ไม่ส่งโดยไม่มีลิงก์")
- แก้: ใน `SendOneAsync` เมื่อ `item.IncludePayLink == true && doc.BalanceDue > 0 && payUrl == null` ⇒ `Finish(Skipped, "ลิงก์ชำระใช้ไม่ได้แล้ว (เพิกถอน/ช่องทางปิด) — ส่งใหม่จากหน้าเอกสาร", DocumentLineSendRule.RuleCode)` (หรืออย่างน้อยเขียนเหตุลง `ErrorMessage` ของแถวที่ส่งแล้ว)

### L3 — `payLinkOffered` ในหน้าต่างคำนวณจาก `BalanceDue > 0` อย่างเดียว
- `DocumentLineSendService.cs:46`: PO / CN-DN ฝั่งซื้อ / ใบสกุลเงินต่างประเทศ / ใบที่มีใบกำกับแทนแล้ว ก็เห็นช่องติ๊ก แต่กดส่งแล้วได้ 409 จาก `DocumentPayLinkPolicy.IssueRefusal` (หน้าเว็บ "ให้ติ๊กได้แล้วปฏิเสธ" — F2 ข้อ 5: server ควรบอกตั้งแต่แรก)
- แก้: ใช้เหตุปฏิเสธตัวเดียวตอนเปิดหน้าต่าง (`PayQrFactsAsync(...).Refusal` หรือเปิด `RefusalAsync` ที่ห่อ `Refusal(d)`) แล้วส่งเป็น `payLinkNote`

### L4 — กันกดซ้ำแบบ "นาที UTC": 23505 กลืนปลายทางที่ไม่ชนไปด้วย · ข้ามนาทีได้สองการ์ด
- `DocumentLineSendService.cs:87-96`: คำขอ A เลือก {E1} และคำขอ B เลือก {E1,E2} พร้อมกันในนาทีเดียว → B ชนที่ E1 → `ChangeTracker.Clear()` ทิ้ง E2 ด้วย แล้วตอบว่า "2 ปลายทางเพิ่งเข้าคิวไปแล้ว" ทั้งที่ E2 ยังไม่เข้าคิว (เงียบ) · กดสองครั้งคร่อม hh:mm:59/hh:mm+1:00 ได้การ์ดสองใบ (เทสต์ Db บรรทัด 203 ยอมรับไว้แล้ว) — ปุ่มถูก disable ระหว่างคำขอ จึงเกิดยาก
- แก้: เข้าคิวทีละปลายทางด้วย `INSERT … ON CONFLICT DO NOTHING` (หรือ SaveChanges ต่อแถว) แล้วนับ queued/already ตามจริง · ถ้าต้องการกันคร่อมนาที ให้ใช้ nonce ต่อการเปิดหน้าต่างที่หน้าเว็บส่งมา

### L5 — ติ๊กลิงก์ชำระ = ออกลิงก์ใหม่ทับลิงก์ที่ถูก "เพิกถอน" (ทั้ง LINE และอีเมล)
- `DocumentPayLinkService.EnsureLinkAsync:224-226`: ลิงก์ที่ `PayLinkRevokedAt != null` ⇒ `LinkUsable=false` ⇒ ออก token ใหม่ · การเพิกถอนต้องใช้สิทธิ์ Void (`DocumentAutoSendController.cs:87`) แต่การออกใหม่ใช้แค่ Create ⇒ พนักงานขายติ๊กช่องตอนส่งแล้วเปิดรับชำระใบที่ผู้มีสิทธิ์ Void ตั้งใจปิดไว้ได้ (เช่น ใบที่กำลังมีข้อพิพาท) โดยไม่มีคำเตือน
- เป็นพฤติกรรมเดิมของ `POST pay-link` (ใช้ด่านเดียวกัน) — รอบนี้แค่เพิ่มทางเข้าอีก 2 ทาง · แก้: ถ้ามี `PayLinkRevokedAt` ให้ปฏิเสธพร้อมเหตุ ("ลิงก์ถูกเพิกถอนโดย … — ออกใหม่ที่หน้าลิงก์ชำระ") ในเส้นส่งเอง หรือให้คำตัดสินระบุว่าตั้งใจให้เป็นแบบนี้

### L6 — ลิงก์อัปโหลดของการ์ด LINE ไม่ตรวจหมดอายุ/เพิกถอนตอนส่ง + ข้อความเหตุไม่ตรงความจริง
- `WhtCertUploadLinkService.cs:100-116` (`LineReminderUrlAsync`): ดึงแค่ `TokenHash` ไม่ดู `ExpiresAt`/`RevokedAt` ⇒ ถ้ากด "ส่งใหม่" แถวที่ล้มหลังลิงก์ถูกเพิกถอน (หรือหลัง 180 วัน) จะได้ปุ่มที่ใช้ไม่ได้ (ล้อตามเส้นอีเมล `ReminderUrlAsync` ที่เป็นแบบเดียวกัน) · ข้อความ "งานทวงรอบหน้าจะเข้าคิวใหม่" ไม่จริง เพราะบันทึกทวงของขั้นนั้นเป็น `Queued` แล้ว และ `RunCompanyAsync:91` จะไม่ทำขั้นนี้ซ้ำ
- แก้: เพิ่ม `l.RevokedAt == null && l.ExpiresAt > now` ในเงื่อนไข (ทั้งสองตัว) และเปลี่ยนข้อความเป็น "ส่งลิงก์อัปโหลดเองจากหน้า 50 ทวิ"
- หมายเหตุขอบเขต token: ลิงก์ครอบทั้งผู้ติดต่อ (`DocumentId=null`) อายุ 180 วัน · ออก 1 ลิงก์ต่อแถวคิวต่อขั้น ⇒ ผู้ติดต่อที่มีหลายปลายทางจะมีลิงก์ใช้ได้พร้อมกันหลายอัน (เหมือนเส้นอีเมล) — ยอมรับได้ แต่ควรมีการเพิกถอนรวมเมื่อได้ใบครบ (ยังไม่มีในรอบนี้)

---

## ตรวจแล้วผ่าน (ไม่รายงานซ้ำ)
- **ด่านสิทธิ์ line-send = ส่งอีเมล**: `DocumentController.cs:136-166` เรียก `DenyDocAsync(Create ตามชนิด)` + `DenySensitiveAsync` เหมือน `send-email` · การออกลิงก์ชำระใช้ Create เหมือน `DocumentAutoSendController.IssueLink` · `send-types` อยู่ใต้ `[Authorize]` ของคลาส
- **ใบที่ส่งได้**: `DocumentLineSendRule.DocumentProblem` ปฏิเสธใบที่ไม่พบ/ไม่อยู่ในชนิดที่ส่งได้/Voided/ยังไม่อนุมัติ · ตอนส่ง `ScheduledEmailSendCheck.SkipReason` ตรวจซ้ำ (Void/กลับเป็นร่างหลังเข้าคิว ⇒ Skipped) · ลิงก์ชำระผ่าน `IssueRefusal` (ฝั่งขาย · ยอดค้าง · สกุลเงิน)
- **tenant**: query ใหม่ทุกจุดมี `CompanyId` (`DocumentLineSendService` ทั้งสองเมธอด · `LineReminderUrlAsync` · `SendWhtCertRequestAsync` ep/contact/company · `GetRecentAsync` contactNames · webhook stamp) — Db test ข้ามบริษัทได้ "ไม่พบ"
- **ผู้รับผิดคน**: `Pick` เลือกได้เฉพาะ option จากตัวหาผู้รับของ `doc.ContactId` (เรื่อง Document · ต้อง Available) · ตอนส่ง resolve ซ้ำ + `ep.ContactId == item.ContactId` · การ์ด 50 ทวิ resolve ซ้ำด้วยเรื่อง Wht
- **ไม่ประทับสถานะเอง**: แถวเป็น Pending จนกว่า `PushAsync` จะได้คำตอบ Sent จาก LINE · ข้อความหลังเข้าคิวบอก "เข้าคิว" ไม่ใช่ "ส่งแล้ว" · ประกาศผูกกลุ่มตอบตามผลจริง (`AnnounceOutcome`)
- **token 50 ทวิ**: HMAC-SHA256(กุญแจที่ได้จาก `Jwt:Secret`, `whtl:{company}:{lineQueueId}`) — เดาไม่ได้ · คนละป้ายกับอีเมล · เก็บแค่ hash · UNIQUE (CompanyId, LineQueueId) · กลุ่มซ่อนยอดไม่ได้ลิงก์ · ผู้ส่งไม่ส่งถ้าไม่มีแถวลิงก์/hash ไม่ตรง
- **webhook**: ประทับ `LineLastWebhookAt` หลังตรวจลายเซ็น **และ** `destinationAccepted` (`CustomerLineWebhookController.cs:57-95`) · มีเงื่อนไข 5 นาที · ตัวจำกัดรูป/ไฟล์ใช้ key `hash(destination,userId)` ผ่านตารางกลาง `failClosed: true` · ไม่มีตัวจำกัด ⇒ ไม่ตอบ
- **migration**: `Round259LineMigration` จำกัดที่ `Kind IN ('LineUser','LineGroup')` · ทำซ้ำได้ · `[]` หลังตัด = "ไม่รับเรื่องใด" (`NotifyTopics.Parse` ⇒ รายการว่าง ไม่ใช่ "ทุกเรื่อง") · Sign/Other ไม่มีเส้นส่งทาง LINE จริง (`NotifyTopic.Sign` ใช้เฉพาะ `ForEmail` ใน `SignRequestService`) ⇒ ไม่ได้ทำลายค่าที่มีผล · เป็นคำสั่งระดับ schema ครอบทุก tenant ตามธรรมเนียมของ `DatabaseMigrationHelper` (ไม่มี audit ต่อแถว — ยอมรับได้)
- **HtmlEncode**: หน้าต่างใหม่ใน documents.html/settings.html/email-schedule.html ใช้ `esc`/`Layout.esc` ทุกค่าจากเซิร์ฟเวอร์ · บล็อกอีเมล `EmailBlockInner` encode URL · การ์ด Flex เป็น JSON ผ่าน `LineDocumentCard.Clean`
- **ทางเข้าอื่น**: `DocumentDeliveryEvidence` นับแถว Manual ที่ Sent เป็นหลักฐานส่ง (ตั้งใจ) · `LineDocumentViewController` รับเฉพาะ `EntityType=="Document"` ⇒ แถว 50 ทวิ ไม่มีลิงก์ PDF · `FallbackAsync` ไม่ทำงานกับแถว Manual/50 ทวิ (`FallbackToEmail=false`) · Autopilot แยกป้ายแถว 50 ทวิ

## ช่องว่างของเทสต์ (กฎ #4 G)
- ไม่มีเทสต์ใดที่ส่ง `IncludePayLink=true` เลย (LINE หรืออีเมล) — ทั้งทางผ่าน (การ์ดมีปุ่ม/อีเมลมีบล็อก) ทางปฏิเสธ 409 และ M1 (กลุ่มซ่อนยอด)
- ไม่มีเทสต์ Db ที่ให้ผู้ส่งคิวส่งแถว `ManualSend` จริง (มีแค่ unit `SenderSupports`)
- ไม่มีเทสต์ webhook ทิศตรงข้าม: ลายเซ็นผิด/บอทไม่ตรง ⇒ `LineLastWebhookAt` ไม่ขยับ
