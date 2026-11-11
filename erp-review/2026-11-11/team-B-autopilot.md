# ทีม B — Autopilot / สร้าง-ส่งเอกสารอัตโนมัติ (อ่านอย่างเดียว) · HEAD 81521510 · 2026-11-11

วิธีตรวจ: เปิดโค้ดตรงจุด (grep + อ่านช่วง) · ไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้ · path ทั้งหมดอยู่ใต้ `Accounting/` · ข้อที่เขียน "ต้องตรวจ" = ยังไม่ได้เปิดโค้ดครบทุกทาง

**BLOCKER (ข้อมูลบัญชีเสีย): ไม่พบ** · **HIGH 2 · MED 7 · LOW 13** · ของรอบ 257 ที่ตรวจแล้วว่าลงจริงอยู่ท้ายไฟล์

---

## 1. ผลตรวจ (เรียงตามความรุนแรง)

### HIGH

**B-H1 — ตารางส่งอีเมล: แก้/ลบกฎ + ลองใหม่/ยกเลิกคิวอีเมล ไม่มีด่านสิทธิ์เลย**
- หลักฐาน: `Controllers/EmailScheduleController.cs:12` มีแค่ `[Authorize]` · `:64-66` GET rules · `:68-102` PUT rules · `:104-109` DELETE · `:111-128` GET queue · `:130-135` retry · `:160-165` cancel — ไม่เรียก `RequireSettingsAsync` · ขณะที่คิว LINE ในไฟล์เดียวกันมีด่าน `CompanySettings.Edit` (`:142-158` · ด่าน `:36-41`) = คู่สมมาตรที่แก้ฝั่งเดียว (F2 ข้อ 1) · ไม่มีตารางสิทธิ์ตามเส้นทางกลางมาครอบ (grep `email-schedule` ใน Middleware/Filters = 0)
- ทางของผู้ใช้จริง: สมาชิกสิทธิ์ดูอย่างเดียว (หรือคีย์ API — ไม่มี `[RejectApiKey]`) PUT กฎ `PayrollPaid` / `WhtCertIssued` / `DocumentApproved` โดยใส่ `BccEmails` เป็นอีเมลตัวเอง ⇒ ได้สลิปเงินเดือน/50 ทวิ/ใบกำกับทุกใบของบริษัท (ผู้อ่าน Bcc: `Services/Implementations/EmailScheduleService.cs:203,265,539,1075`) = ข้อมูลส่วนบุคคลรั่ว (PDPA ม.37) · หรือแก้ `BodyTemplate` + เปิดแนบลิงก์ชำระ ส่งถึงลูกค้าทุกรายในนามบริษัท · หรือลบกฎ ⇒ ใบแจ้งหนี้/ทวงหยุดส่งเงียบ
- แก้ขั้นต่ำ: PUT/DELETE rules + retry/cancel คิวอีเมล เรียก `RequireSettingsAsync` (ตัวเดียวกับ LINE) · PUT เพิ่ม `[RejectApiKey]` · GET rules/queue ต้อง `Accounting.View` (มีอีเมลลูกค้า) · เพิ่มแถวใน `tools/required_call_site_check.py` + เทสต์ 403

**B-H2 — คิวอีเมลค้าง "กำลังส่ง" ตลอดไป (ไม่มีตัวกู้ · ไม่มีทางออกของคน)**
- หลักฐาน: จองแถวเป็น `Sending` `EmailScheduleService.cs:627-641` · วนส่ง `:655-786` — `catch … when (ex is not OperationCanceledException)` (`:770`) ⇒ ปิดระบบ/ deploy กลางแบตช์ = แถวที่เหลือ (สูงสุด 50) ค้าง `Sending` · ไม่มีโค้ดใดคืนแถว `Sending` ที่ค้าง (grep `"Sending"` ทั้ง Services = เฉพาะจุดจอง) — ต่างจากคิว LINE ที่มี `StaleSending` 30 นาที (`ContactLineQueueService.cs:34,80-91`) และงานหลังรับชำระที่ใช้สัญญาเช่า 10 นาที (`PostPaymentAutomationService.cs:142`)
- ทางตัน: "ลองใหม่" รับเฉพาะ `Failed` (`EmailScheduleService.cs:1428`) · "ยกเลิก" รับเฉพาะ `Pending` (`:1442`) · หน้า Autopilot นับเฉพาะ `Failed` (`AutopilotService.cs:161`) · แถวรายการประจำบอกว่า "เข้าคิวแล้ว" ⇒ ใบกำกับงวดนั้นไม่ถึงลูกค้าและไม่มีที่ใดบอก
- ทางของผู้ใช้จริง: รายการประจำรันเที่ยงคืน 200 ใบ → deploy ระหว่างคิวกำลังส่ง → 50 ใบหายเงียบ
- แก้ขั้นต่ำ: ต้น `ProcessPendingQueueAsync` — แถว `Sending` ที่ `UpdatedAt < now − 30 นาที` ⇒ `Failed` + เหตุ "ค้างระหว่างส่ง (ระบบหยุดกลางคัน) — ตรวจว่าลูกค้าได้รับหรือยัง แล้วกดลองใหม่" (ไม่คืนเป็น Pending เอง: ผู้ให้บริการอาจรับไปแล้ว = ส่งซ้ำ) ⇒ โผล่ในเรื่องค้างของ Autopilot + กดลองใหม่ได้ · Db test: จอง → ไม่ส่ง → รอบถัดไปเห็น Failed

### MED

**B-M1 — อนุมัติอัตโนมัติของรายการประจำไม่ผ่าน = ไม่แจ้งทีมทันที (ขัดคำที่หน้าเว็บ/แค็ตตาล็อกบอก)**
- หลักฐาน: catch ของการอนุมัติ `RecurringTransactionService.cs:1022-1042` เขียนแค่หมายเหตุบนใบ + `LastRunNotice` + ErrorLog — ไม่มี `DispatchAsync` · `RecurringRunFailed` ถูกส่งที่เดียวคือ "สร้างเอกสารไม่ได้" (`:695`) · แต่แค็ตตาล็อก `Helpers/AutopilotNotice.cs:35` เขียน "E-02 สร้าง/**อนุมัติ**เอกสารไม่ได้ = ทันที" และ `wwwroot/pages/autopilot.html:98` "ล้ม/ติด = แจ้งทันที"
- ที่มองเห็นได้ตอนนี้: แถวหน้ารายการประจำ · หมายเหตุบนใบ · เรื่องค้างหน้า Autopilot (`AutopilotService.cs:142-149`) · สรุป 08:40 วันถัดไป ⇒ ใบกำกับค้างร่างได้ถึง ~32 ชม. โดยไม่มีใครถูกเรียก (ใบที่ตั้งส่งอีเมลก็ไม่ออก `:1073-1077`)
- แก้ขั้นต่ำ: ใน catch (`!outcome.Committed`) ส่ง `RecurringRunFailed` (หัว "…สร้างแล้วแต่ค้างร่าง") ถึงผู้อนุมัติของรายการ · หนึ่งครั้งต่อใบ

**B-M2 — แผงความพร้อม/ทดลองรัน ไม่รู้จักด่าน "ผู้อนุมัติของรายการหมดสิทธิ์" (รอบ 257 L4)**
- หลักฐาน: `AutoApproverRefusalAsync` มีผู้เรียกเดียวคือรอบรันจริง (`RecurringTransactionService.cs:990`) · `Helpers/AutopilotReadiness.cs:207-211` ยังตอบ "อนุมัติอัตโนมัติจะผ่าน" · มีแค่ Warn เรื่องค่าเริ่มต้น (`:222-223`)
- ทางของผู้ใช้จริง: ผู้สร้างรายการลาออก → แผงเขียว → ทุกงวดค้างร่าง (`RECURRING-APPROVER-GONE`)
- แก้ขั้นต่ำ: `RecurringAutopilotService` เรียกตัวเดียวกันแล้วส่งเข้า facts ⇒ ข้อ Fail "ผู้อนุมัติของรายการไม่มีสิทธิ์แล้ว" + ลิงก์ขั้นอนุมัติ

**B-M3 — ผู้มีสิทธิ์อนุมัติแก้ยอด/ผู้ติดต่อ/ตารางเวลา ⇒ ค่าเริ่มต้น 256-1 (และใบรับรองนโยบาย) หยุดมีผลเงียบ ๆ ตอนบันทึก**
- หลักฐาน: `UpdateAsync` ไม่รับผู้แก้ (`RecurringTransactionService.cs:178` · `Controllers/RecurringController.cs:210`) · `UpgradeStampsBeforeEdit` ประทับค่า**ก่อน**แก้ (`RecurringApprovePolicyLoader.cs:54-63`) ⇒ หลังแก้ไม่ตรง ⇒ `BindingChangedReason` (`:83-85`, `:102-103`) · หน้าเว็บบันทึกนโยบายซ้ำเฉพาะเมื่อแตะแถวนโยบาย (`wwwroot/pages/recurring.html:1292`) · `SaveWarnings` มีแค่เรื่องอีเมล (`RecurringTransactionService.cs:644-649`)
- ทั้งที่หลัง 257-1-1 ผู้แก้รายการที่เปิดอนุมัติอัตโนมัติ**ต้องมีสิทธิ์อนุมัติอยู่แล้ว** (`RecurringController.cs:201-205`) — ด่านลายนิ้วมือจึงซ้อนกับด่านสิทธิ์และกลายเป็นตัวทำให้ค้าง
- ทางของผู้ใช้จริง: ปรับราคารายปี → เดือนถัดไปลูกค้าที่มีใบค้าง = ใบใหม่ค้างร่าง ไม่ส่ง (ตรงข้ามคำตัดสินเจ้าของ 256-1)
- แก้ขั้นต่ำ: controller ส่ง userId + "ผ่านด่านอนุมัติแล้ว" ⇒ ประทับ `DefaultPolicyBindingHash` ใหม่ + `DefaultPolicyStampedBy` = ผู้แก้ · รายการที่มีใบรับรองนโยบาย ⇒ `SaveWarnings` "ต้องรับรองนโยบายใหม่" + เปิดขั้น ⑤ · ต้องมีคำตัดสินทับ 257-4-7

**B-M4 — เทมเพลตสมุดรายวันที่ค้างหลายงวด: ไล่ออกย้อนหลังทุก 15 นาที ลงวันที่วันนี้ทุกใบ**
- หลักฐาน: `SkipOverduePeriodsAsync` คืน null ให้สมุดรายวัน (`RecurringTransactionService.cs:515`) · `EntryDate = วันนี้` (`:1146`) · ลงบัญชีอัตโนมัติ (`:1162`)
- ทางของผู้ใช้จริง: รายการตั้งค่าใช้จ่ายค้างจ่ายรายเดือนล้ม 3 เดือน (บัญชีถูกปิดใช้ · งวดปิด) → แก้แล้ว 45 นาทีถัดมาได้ JE 3 ใบลงเดือนปัจจุบัน ⇒ ค่าใช้จ่ายเดือนนี้ 3 เท่า เดือนก่อน ๆ เป็น 0 (TFRS for NPAEs — คลาสเดียวกับ A-H3)
- แก้ขั้นต่ำ: ใช้ `CatchUp` กับสมุดรายวันด้วย (ออกงวดล่าสุด + ข้อความ "ข้าม N งวด — ลงเองตามงวด") หรือให้ใบย้อนหลังลงวันที่ของงวด (ผ่านด่านปิดงวดเดิม)

**B-M5 — แจ้งเตือนรอบล้ม/ข้อสังเกต ส่งถึง "ผู้สร้าง" โดยไม่ตรวจว่ายังเป็นสมาชิก**
- หลักฐาน: `RecurringTransactionService.cs:702` และ `:1012` ใส่ `RecipientUserId = CreatedBy` ตรง ๆ · ขณะที่แจ้งล่วงหน้าในไฟล์เดียวกันตรวจสมาชิกที่ใช้งาน (`:487-491`)
- ผล: ผู้สร้างออกจากบริษัท ⇒ ไม่มีคนในบริษัทได้รับ (และอดีตพนักงานอาจได้ชื่อรายการ/ลูกค้า — ต้องตรวจเส้น `DirectToRecipient` ใน `NotificationEngine.cs:75-79`)
- แก้ขั้นต่ำ: helper เดียว "ผู้รับ = ผู้สร้างที่ยังเป็นสมาชิก ไม่งั้น null (ค่าตั้งต้นของคีย์)" ใช้ทั้ง 3 จุด

**B-M6 — e-Tax โหมดส่งตรง: ลงนามแล้วส่งกรมไม่สำเร็จ = LogWarning อย่างเดียว · ไม่มีงานส่งตามกำหนดวันที่ 15**
- หลักฐาน: `EtaxInvoiceService.cs:466-481` · ผู้เรียก `SubmitToRevenueAsync` นอกจุดนี้มีแต่ปุ่มใน `Controllers/EtaxController.cs:104,126,217,233` · ไม่มี BackgroundService ใดเรียก (CLAUDE.md §F "Submission cron … retry queue")
- ผล: รายการประจำอนุมัติเอง → e-Tax ลงนามแล้วค้าง "Signed" ไม่มีเรื่องค้าง/แจ้งเตือน (ต้องตรวจ: โหมดส่งตรงเปิดให้ลูกค้าจริงหรือยัง — by Email เดินคนละเส้นและมีด่านครบ)
- แก้ขั้นต่ำ: เรื่องค้างหน้า Autopilot "e-Tax ลงนามแล้วยังไม่ส่ง" + แจ้งวันที่ 10 ของเดือนถัดไป · งานส่งอัตโนมัติไว้รอบหลัง

**B-M7 — กฎตามวัน (ใบแจ้งยอดรายเดือน · ใกล้ครบกำหนด · เกินกำหนดแบบไม่ซ้ำ) เทียบ "วันนี้ = วันเป้าหมาย" เท่านั้น**
- หลักฐาน: `EmailScheduleService.cs:429` (`todayBkk.Day != sendDay`) · `:585` (`DueDate == target`) · `lastScan` อยู่ในหน่วยความจำ (`Services/Background/EmailScheduleWorker.cs:28`)
- ผล: ระบบหยุดทั้งวันนั้น (เวลาไทย) ⇒ ใบแจ้งยอดเดือนนั้น/คำเตือนของใบนั้นไม่ถูกส่งและไม่มีร่องรอย — ไม่ท่วม แต่หายเงียบ
- แก้ขั้นต่ำ: watermark ต่อกฎ (วันที่สแกนล่าสุด) แล้วสแกนช่วง (ล่าสุด, วันนี้] ย้อนไม่เกิน 3 วัน — กุญแจกันซ้ำเดิมกันส่งซ้ำอยู่แล้ว (`UX_EmailQueue_Idempotency` `Data/DatabaseMigrationHelper.cs:5720`)

### LOW

- **L1** บริษัทหมดอายุเกินช่วงผ่อนผัน: รอบรายการประจำยังถูกหยิบทุก 15 นาที (ไม่กรองบริษัท `RecurringTransactionService.cs:407-411`) แล้วล้มด้วย "ไม่มีสิทธิ์ใช้ระบบเอกสาร" (`DocumentService.cs:1212-1213`) — ไม่บอกว่าเพราะแพ็กเกจหมดอายุ · ต่ออายุแล้วออกเฉพาะงวดล่าสุด (ถูก) · ต้องตรวจ: กด "หยุดชั่วคราว" ขณะหมดอายุถูก 402 ไหม
- **L2** ระบบล้มระหว่าง `CreateDocumentAsync` กับ `MarkRunAsync` (`RecurringTransactionService.cs:938-940`) ⇒ รอบถัดไปออกใบงวดเดิมซ้ำ · `RecurringTransactionId` ประทับทีหลังนอกธุรกรรม (`:951-953`) · ไม่มีกุญแจ "รายการ+งวด" บนเอกสาร
- **L3** "สร้างทันที" ก่อนถึงกำหนด = รอบพิเศษไม่กินงวด (`:357`) — กดซ้ำทีละครั้งได้ใบเพิ่มไม่จำกัด (ล็อกกันเฉพาะกดพร้อมกัน)
- **L4** ชื่อผู้กระทำของระบบไม่มีตัวตั้งเดียว: `"System"` (`RecurringTransactionService.cs:434`) · `"autopilot"` (`QuotationAcceptAutopilotService.cs:23`) · `"bank-auto-settle"` (`BankReceiptSettleService.cs:307`) กับ `"system:bank-auto-settle"` (`Helpers/BankMatchAttribution.cs:51`) · `"ระบบ (ส่งอัตโนมัติหลังรับชำระ)"` (`PostPaymentAutomationService.cs:36`) · `"email-schedule"` (`EmailScheduleService.cs:901`) ⇒ กรอง audit "ระบบทำ" ไม่ได้ด้วยเงื่อนไขเดียว
- **L5** `OnDocumentApprovedAsync` กลืนทุกข้อผิดพลาดเป็น LogWarning (`EmailScheduleService.cs:160`) — อนุมัติด้วยมือ + มีกฎ "เมื่ออนุมัติ" แล้วเข้าคิวล้ม = ไม่มีอีเมล ไม่มีร่องรอย (เส้นรายการประจำมีทางสำรอง `:303-311`)
- **L6** GET รายการประจำ/รายละเอียด/email-readiness ไม่มีด่านอ่าน (`RecurringController.cs:149-176`) ทั้งที่ความพร้อม/ทดลองรันมี (`:30-34`)
- **L7** ไม่ติ๊ก "ส่งอีเมล" ที่รายการ แต่บริษัทมีกฎ "เมื่ออนุมัติ"/"เมื่อรายการประจำสร้าง" ⇒ ยังส่ง (`EmailScheduleService.cs:317-345`) — ตัวคุม 4 ชั้นซ้อนกัน (ติ๊กรายการ · กฎ 2 ชนิด · "ไม่ส่งอัตโนมัติ" ของผู้ติดต่อ) · คำอธิบายที่ช่องติ๊กต้องบอกว่า "ไม่ติ๊ก ≠ ไม่ส่ง" (ต้องตรวจข้อความ `autoEmailHint`)
- **L8** ใบเสนอราคาที่ล้มนอกด่าน ประทับ Failed แต่ไม่แจ้งทันที (`QuotationAcceptAutopilotService.cs:63-75` เทียบ `:108`,`:137`)
- **L9** คิวที่เลยเวลาระหว่างระบบหยุดถูกส่งทั้งหมดเมื่อกลับมา (50/นาที) ไม่ตัดของเก่า — "ครบกำหนดอีก 3 วัน" อาจถึงหลังครบกำหนด (ใบที่จ่ายแล้วถูกข้ามถูกต้อง `:662`)
- **L10** สร้างงานหลังรับชำระไม่สำเร็จ = LogError อย่างเดียว ไม่มีแถว/แจ้ง (`PostPaymentAutomationService.cs:131-136`)
- **L11** ค่าตั้งไม่มีผู้อ่าน: `AutoCloseMonthEnd` (เขียน/echo `SettingsService.cs:235,749` · อยู่ใน ratchet แล้ว) · `EtaxXmlOutputPath` (ไม่มีทั้งผู้อ่านและช่อง — ลบได้)
- **L12** `OverdueDunningJob` ใช้คำ "หนังสือทวงหนี้" แต่ส่งแค่แจ้งเตือนภายใน (`Services/Background/OverdueDunningJob.cs:112-116`) — ผู้ทำบัญชีเข้าใจว่าลูกค้าได้รับแล้ว · ตัวส่งถึงลูกค้าจริงคือกฎ "เกินกำหนด" ของตารางส่ง
- **L13** `EmailScheduleWorker` ไม่มี `JobLock` ระดับงาน (F4 ข้อ 6) — พึ่ง SKIP LOCKED + unique index · สองเครื่องสแกนพร้อมกัน ⇒ unique violation ล้มทั้ง `SaveChanges` ของรอบสแกน (`EmailScheduleService.cs:613`) แล้วรอบถัดไปซ่อมเอง (ไม่ส่งซ้ำ แต่ log แดง)

---

## 2. ลูกโซ่ต้นจนจบ (✅ ระบบทำ · 👤 ต้องคน · ❌ พัง/ขาด)

**ก. รายการประจำ → ใบกำกับ → ส่ง → รับเงิน → ใบเสร็จ**
1. ✅ สร้างเทมเพลต + ด่านสิทธิ์ทุกทาง (`RecurringController.cs:186-260` · `Helpers/RecurringWriteGate.cs:51-62`)
2. ✅ รอบ 15 นาที · ล็อกต่อรายการ · วันตามปฏิทินไทย (`BackgroundJobService.cs:139-144` · `RecurringTransactionService.cs:421`)
3. ✅ ร่าง → อนุมัติ (เลขออกตอนอนุมัติ) · ค่าเริ่มต้น 256-1 (`:988-994`) — ❌ ค้างร่างไม่แจ้งทันที (B-M1) · ❌ แผงความพร้อมไม่รู้ด่านผู้อนุมัติ (B-M2) · ❌ แก้ยอดแล้วค่าเริ่มต้นดับเงียบ (B-M3)
4. ✅ เข้าคิวอีเมล/LINE + ลิงก์ชำระ (`EmailScheduleService.cs:279-388`) — ❌ คิวค้าง "กำลังส่ง" (B-H2)
5. ✅ ลูกค้าจ่ายผ่านลิงก์ → งานหลังรับชำระ (สัญญาเช่า · ลองใหม่ · แจ้งเมื่อหยุด `PostPaymentAutomationService.cs:139-218`)
6. ✅ ออก/ส่งใบเสร็จ · 👤 เมื่อหยุด (ปุ่มลองใหม่หน้า Autopilot `AutopilotService.cs:158-159`)

**ข. ใบเสนอราคา → ร่างใบแจ้งหนี้/ใบกำกับ**: ✅ ลูกค้ายอมรับ → ✅ แปลงเป็นร่าง (ประทับ compare-and-set `QuotationAcceptAutopilotService.cs:144-156`) → 👤 อนุมัติ (ตั้งใจ) · หยุด = แจ้งทันที ยกเว้นล้มนอกด่าน (L8)

**ค. เงินโอนเข้า → ปิดหนี้**: ✅ ไฟล์ statement ที่ผู้อัปโหลดมีสิทธิ์อนุมัติเท่านั้นปิดเอง · feed/พิมพ์มือ/API = เสนอ (`Helpers/BankReceiptAutoSettle.cs:42-50` · `BankReceiptSettleService.cs:161-164`) → ✅ กันซ้ำด้วยเลขอ้างอิง + กู้หลังล้ม (`:354-420`) → 👤 ยืนยันข้อเสนอ · ล้ม 3 ครั้ง = พักให้คนดู (`:262-268`)

**ง. 50 ทวิ ขาออก**: ✅ ออกตอนอนุมัติ/จ่าย ผ่าน `WhtCertIssuePlan` (`DocumentService.cs:7696`, `:15545`) → ✅ ส่งตามกฎ (`WithholdingTaxCertService.cs:401,831`) · ล้ม = ป้ายบนใบ + ลิสต์รอออกใบ (ไม่ได้ไล่ลึกรอบนี้ — ทีม B รอบก่อนครอบแล้ว)

**จ. e-Tax**: ✅ สร้าง/ลงนามหลังออกใบ (`IssuedDocumentHooks.cs:171`) · by Email ผ่านคิวเดียวกัน (`EmailScheduleService.cs:692`) → ❌ โหมดส่งตรง: ส่งกรมล้มเงียบ + ไม่มีงานตามกำหนด (B-M6)

**ฉ. ฝั่งซื้อ (สแกน/คู่ค้า → ใบซื้อร่าง → หลักฐานภาษีซื้อ)**: ไม่ได้เปิดโค้ดรอบนี้ — อ่านเฉพาะคำตัดสิน 257-3-1…14 (สถานะ `AwaitingTaxInvoice` · PO ปลายทางเป็นร่าง) ⇒ **ต้องตรวจ** โดยทีมที่ถือ `CrossTenantWorkflowService.Partners.cs`

**ช. ตารางส่งอีเมล/LINE**: ❌ ไม่มีด่านสิทธิ์ (B-H1) → ✅ เวลาไทย (`EmailScheduleService.cs:133-135`) · วันที่ 1–28 (`EmailScheduleController.cs:56-60`) → ✅ กันซ้ำข้ามเครื่อง → ❌ วันที่พลาดไม่ตามเก็บ (B-M7)

---

## 3. ค่าตั้ง Autopilot — มีผู้อ่านจริงไหม

| ค่าตั้ง | หน้า | ผู้อ่านเชิงธุรกิจ | หมายเหตุ |
| --- | --- | --- | --- |
| อนุมัติอัตโนมัติ / ส่งอีเมล (รายการ) | recurring | `RecurringTransactionService.cs:974,1073` | ส่งอีเมล ⇒ บังคับอนุมัติ (ตั้งใจ `:120`) |
| ปิดหนี้อัตโนมัติจากเงินโอน | autopilot | `BankReceiptSettleService.cs:231-233` | ค่าตั้งต้นปิด = ปลอดภัย |
| ใบเสนอราคาที่ลูกค้ายอมรับ | autopilot + ต่อใบ | `QuotationAcceptAutopilotService.cs:45-52` | ไม่แปลงย้อนหลัง (watermark) |
| เวลาสรุปประจำวัน | autopilot | `AutopilotDigestService.cs:44-51` | |
| ส่งใบเสร็จหลังรับชำระ (+บางส่วน) | settings · email-schedule · recurring | `Helpers/AutopilotSettingsResolver.cs` | 3 หน้า 4 ชั้น (กฎ › รายการ › ผู้ติดต่อ › บริษัท) — ตั้งใจ แต่ควรมีบรรทัด "ค่าที่มีผลตอนนี้มาจากชั้นไหน" |
| ปิดงวดสิ้นเดือนอัตโนมัติ | settings | **ไม่มี** | L11 |
| e-Tax ส่งกรมอัตโนมัติ | settings | `EtaxInvoiceService.cs:466` | ล้มเงียบ (B-M6) |

ซ้อน/ขัดกัน: ติ๊กส่งอีเมลของรายการ × กฎตารางส่ง (L7) · "หนังสือทวงหนี้" ภายใน × กฎเกินกำหนดถึงลูกค้า (L12)

---

## 4. ของรอบ 257 ที่ตรวจแล้วว่าลงจริง

- A-H1 สิทธิ์เขียนรายการประจำครบ 6 ทาง + คีย์ API (`RecurringController.cs:178-260` · `RecurringWriteGate.cs`)
- A-H2 ข้อความค่าเริ่มต้นจากตัวโหลดเดียว (`RecurringController.cs:117`)
- A-H3 สมุดรายวันห้ามสร้างล่วงหน้า ทั้งสร้าง/แก้ (`RecurringTransactionService.cs:83,222-224`)
- A-M4 ค่าเริ่มต้นผูกลายนิ้วมือ · A-M5 ผู้ติดต่อ "หยุดรอคน" ชนะ (`RecurringApprovePolicyLoader.cs:75-85`) · A-M6 แผงบอกเหตุ (`AutopilotReadiness.cs:222`)
- A-M7 บันทึกค่าอื่นได้โดยไม่ต้องมีสิทธิ์ธนาคาร (`AutopilotController.cs:54-61`)
- A-M9 วันที่ตามปฏิทินไทย (`RecurringTransactionService.cs:864,1146` · `RecurringSchedule.DocumentDateUtc`)
- A-L11 สรุปนับเกณฑ์เดียวกับคิว (`AutopilotService.cs` DigestFactsAsync) · A-L13 เตือนเดือนภาษีย้าย (`AutopilotReadiness.cs:226-230`)
- ฝ่ายค้าน L4 ผู้อนุมัติตรวจตอนรัน ทั้งเอกสารและสมุดรายวัน (`RecurringTransactionService.cs:990,1157`)
- ทำงานถูกอยู่แล้ว: งวดค้างหลายงวดของเอกสารออกงวดเดียว + บอกบนแถว (`:513-527`) · รอบล้มแจ้งครั้งแรก/เมื่อเหตุเปลี่ยน (`:672-691`) · ด่านปิดงวดอยู่ในทางสร้าง/อนุมัติร่วม (`DocumentService.cs:1351`) · ชุดคำเตือนห้ามปล่อย (`Helpers/AutoApprovePolicy.cs:75-82`)

---

## 5. ชุดแก้ที่เสนอ (เรียงลำดับ)

1. **ชุด ก (ความปลอดภัย)** B-H1 — `Controllers/EmailScheduleController.cs` · `tools/required_call_site_check.py` · เทสต์ 403
2. **ชุด ข (ส่งไม่หายเงียบ)** B-H2 · B-M7 · L5 · L9 — `Services/Implementations/EmailScheduleService.cs` · `Services/Background/EmailScheduleWorker.cs` · `Models/Entities/EmailSchedule.cs` + `Data/DatabaseMigrationHelper.cs` (watermark) · Db test
3. **ชุด ค (รายการประจำค้างต้องดัง)** B-M1 · B-M2 · B-M3 · B-M5 · L6 — `RecurringTransactionService.cs` · `RecurringApprovePolicyLoader.cs` · `RecurringAutopilotService.cs` · `Helpers/AutopilotReadiness.cs` · `Controllers/RecurringController.cs` · `wwwroot/pages/recurring.html` · DECISIONS (ทับ 257-4-7)
4. **ชุด ง (งวดบัญชี)** B-M4 · L2 · L3 — `RecurringTransactionService.cs` · `Helpers/RecurringSchedule.cs` + เทสต์ pure
5. **ชุด จ (e-Tax/ชื่อระบบ/ข้อความ)** B-M6 · L1 · L4 · L8 · L10–L12 — `EtaxInvoiceService.cs` · `AutopilotService.cs` · helper ชื่อผู้กระทำของระบบตัวเดียว
ทุกชุด: `DOCUMENT_FLOW.md` §2.7/Autopilot + `TEST_PLAN.md` ขยับในคอมมิตเดียวกัน · ชุด ก/ค ผ่านฝ่ายค้านก่อนรวม
