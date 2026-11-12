# ฝ่ายค้าน รอบ 260-4 — LINE quiet hours · กระดิ่งล้มซ้ำ · test-targets · A-L6 · collection

ขอบเขต: 2f2579a6 · c40109a8 · d36c96d3 · 118c3bb6 · 0dc5eea8 (branch `claude/booking-service-features-sdemob`) · คำตัดสิน 260-4-1…6
วิธี: อ่านโค้ดอย่างเดียว ไม่ได้คอมไพล์หรือรัน · ข้อที่ตามโค้ดไม่ถึงถูกตัดออกแล้ว

## HIGH

### H1 — ส่งข้อความทดสอบถึงผู้ติดต่อที่ถอนความยินยอมแล้ว / ไม่มีความยินยอม (PDPA ม.19)
`Accounting/Services/Implementations/LineTestTargets.cs:22-29` (`Usable`) กรองแค่ `UnlinkedAt == null && BlockedAt == null && OaScope == company`
ไม่ตรวจ `ConsentRecordId` และไม่ตรวจ `PdpaConsentRecord.WithdrawnAt` ตัวหาผู้รับกลางตรวจทั้งสองอย่าง
(`ContactRecipientRule.cs:234-237` คือ no-consent / consent-withdrawn) ส่วน test-targets ข้ามตัวตัดสินนั้นไป
- ลำดับเหตุการณ์: เจ้าของข้อมูลถอนความยินยอมผ่านเส้น PDPA ทั่วไป `PdpaService.WithdrawConsentAsync` (`Pdpa/PdpaService.cs:1047-1058`)
  เส้นนี้ประทับ `WithdrawnAt` อย่างเดียว **ไม่ปลด endpoint** ⇒ `UnlinkedAt` ยังเป็น null ⇒ ปลายทางนั้นยังอยู่ใน `GET line-config/test-targets`
  และ `POST test` ส่งข้อความ "✅ ทดสอบ LINE Messaging API สำเร็จ / บริษัท: {companyId GUID}" (`LineNotifyService.cs:360-361`) ถึงคนที่ถอนความยินยอมแล้ว
  แถว endpoint แบบเดิมที่ `ConsentRecordId == null` ก็ถูกแสดงในรายการเหมือนกัน
- ช่องโหว่ข้างเคียงใน `ResolveAsync` (`LineTestTargets.cs:54`): ไม่ join `Contacts` จึงส่งถึงปลายทางของผู้ติดต่อที่ถูกลบ (soft-delete) ได้ ทั้งที่ `ListAsync` กรองผู้ติดต่อที่ถูกลบออกแล้ว
- วิธีแก้ขั้นต่ำ: ตัด endpoint ที่ consent ใช้ไม่ได้ออกทั้งใน list และ resolve โดยใช้ตัวตัดสินเดียว (`ContactRecipientRule` ผ่าน
  `IContactRecipientResolver` หรือดึง `EndpointFacts` ชุดเดียวกัน) ห้ามเขียนเงื่อนไขขึ้นใหม่ · ให้ `ResolveAsync` join contact `!IsDeleted` ·
  ข้อความทดสอบที่ส่งถึงผู้ติดต่อต้องไม่มี GUID และไม่มีศัพท์เทคนิค ใช้ข้อความ "ทดสอบการแจ้งเตือนจาก {ชื่อบริษัท}" ·
  เพิ่ม Db test สองทิศ: ถอนผ่าน PdpaService ⇒ ไม่อยู่ในรายการและส่ง `ep:` ไม่ได้ / ยังยินยอม ⇒ อยู่ในรายการ

## MED

### M1 — กระดิ่งไม่ดังเมื่อถอดรหัสไม่ได้ ซึ่งเป็นอาการของ "กุญแจต่างกันข้ามเครื่อง"
`ContactLineQueueService.cs:294` และ `:347`: เมื่อถอดรหัส LINE id ไม่ได้ จะ `Finish(Failed)` แล้ว return ก่อนถึง `PushAsync` ⇒ ไม่เคยเรียก `AlertRepeatedFailureAsync` (`:422`)
ถ้ากุญแจ OA ของบริษัทเองถอดไม่ได้ ตัวหาผู้รับจะถือว่า OA ไม่พร้อม (`ContactRecipientResolver.cs:223` `IsUsable`) ⇒ ตอนสแกนจะได้แค่แถว `line:none` แบบ **Skipped**
(`EmailScheduleService.cs:1323-1333`) ซึ่งตัวนับของกระดิ่งไม่นับ เพราะนับเฉพาะ `Status == Failed`
- คำอธิบายใน 0dc5eea8 (แต่ละคลาสเทสต์ใช้ SecretProtector คนละกุญแจ) เป็นเรื่องของเทสต์ แต่**สภาพเดียวกันเกิดใน production ได้**:
  ถ้ามีเครื่องหนึ่งตั้ง `ENCRYPTION_KEY` ผิด (fail-fast ตรวจแค่ว่าว่างหรือเป็น placeholder ไม่ได้ตรวจว่าทุกเครื่องใช้กุญแจเดียวกัน)
  แถวที่เครื่องนั้นจองไปจะล้มถาวรพร้อมคำแนะนำผิดว่า "ให้ผู้รับผูกใหม่" แล้วถอยไปอีเมล ส่วนสแกนของเครื่องนั้นได้แค่ Skipped
  สุดท้ายไม่มีกระดิ่ง และบอกได้แค่จาก log
- วิธีแก้ขั้นต่ำ: (ก) เรียก `AlertRepeatedFailureAsync` จากสองจุดที่ถอดรหัสไม่ได้ด้วย (ข) ให้การถอดรหัสไม่ได้นับเป็น `Retry` สักหนึ่งรอบ
  เพื่อให้เครื่องอื่นมีโอกาสหยิบไป ก่อนประทับ Failed (ค) ข้อความไม่ควรสั่ง "ผูกใหม่" ตราบใดที่ยังไม่ได้ตัดเรื่องกุญแจของเครื่องออก

### M2 — `AutoSendEnqueueFailed` ยังไม่ครอบทั้ง defect class (F2 ข้อ 1)
`EmailScheduleService.cs:251` (`OnPayrollPaidAsync`) และ `:313` (`OnWhtCertIssuedAsync`) ยังเป็น `catch (Exception) { LogWarning }` แบบเดิม
ถ้าการเข้าคิวส่ง 50 ทวิ อัตโนมัติถึงผู้ถูกหัก หรือการส่งสลิปเงินเดือนล้ม ก็ยังเงียบเหมือนปัญหา A-L6 ก่อนแก้
- วิธีแก้: ใช้ `NotifyApproveEnqueueFailedAsync` แบบรับชนิดเอกสารเป็นพารามิเตอร์ (เปลี่ยน EntityType หรือ EntityId ต่อชนิด) กับสองจุดนี้ แล้วล็อกด้วย `required_call_site_check`

### M3 — A-L6: entity ที่ล้มยังค้างใน change tracker ของคำขอ ⇒ กระดิ่งอาจบอกผิด
`EmailScheduleService.cs:144-168`: เมื่อ `SaveChangesAsync` ล้ม แถว `EmailQueue`/`LineQueue` ที่ `Add` ไว้ยังถูก track อยู่ใน `_db` ซึ่งเป็น scoped ตัวเดียวกับ `DocumentService`
ต่อมา `DocumentService.cs:7956` เรียก `AllocateCreditHoldsAfterApproveAsync` → `ICustomerCreditService.AfterDocumentApprovedAsync` ซึ่ง SaveChanges อีกครั้ง (คาดว่าใช้ context เดียวกัน)
- กรณีล้มชั่วคราว: แถวคิวถูกบันทึกสำเร็จในรอบหลัง แต่กระดิ่งบอกไปแล้วว่า "ลูกค้ายังไม่ได้รับเอกสาร · ส่งเองจากหน้าเอกสาร"
  ถ้าเจ้าของส่งเองตามนั้น ลูกค้าจะได้เอกสารสองครั้ง
- กรณีล้มถาวร เช่นชน unique: การจัดสรรเงินรอจัดสรรล้มตามไปด้วย และได้หมายเหตุ `[CC-HOLD-FAILED]` ทั้งที่การจัดสรรเองไม่ได้ผิด
- ข้อบกพร่องนี้มีมาก่อน แต่รอบนี้เพิ่มคำสั่งให้ผู้ใช้ทำต่อตามข้อความที่อาจไม่จริง
- วิธีแก้: ใน catch ให้ detach entity ที่ Add ในรอบนี้ (`ChangeTracker.Entries<EmailQueue/LineQueue>().Where(Added)` → `Detached`) ก่อนแจ้ง
- ข้อสังเกตต่อเนื่อง (LOW): `when (ex is not OperationCanceledException)` ทำให้ TaskCanceledException จาก HTTP timeout ภายใน enqueue (เช่นตอนสร้างลิงก์ชำระ) หลุดออกไปถึง catch `LogWarning` ของ DocumentService ⇒ ไม่มีกระดิ่ง
  ควรใช้ `when (!ct.IsCancellationRequested)` แทน

### M4 — กันซ้ำ 24 ชม. ใช้ร่วมกันระหว่าง "ล้มหลายครั้ง" กับ "Token เสีย"
`ContactLineQueueService.cs:226` กันซ้ำด้วย `EntityType` เดียว โดยไม่แยกว่าเป็นชนิดไหน
- ลำดับเหตุการณ์: 10:00 ลูกค้า 5 รายเลิกเป็นเพื่อน ⇒ กระดิ่ง "ส่งไม่สำเร็จหลายครั้ง" · 11:00 token ถูกเพิกถอน (401)
  ⇒ การ์ดทุกใบล้มไปอีก 23 ชม. โดยไม่มีกระดิ่ง "Token ใช้ไม่ได้" · Autopilot (`AutopilotService.cs` ที่ group ด้วย `(EntityType, EntityId)`) ก็แสดงแค่เรื่องเก่า
- วิธีแก้: กันซ้ำแยกตามชนิด เช่นใส่ EntityId เป็น Guid คงที่ต่อชนิด หรือใช้ EntityType ย่อย และให้ `channelBroken` ดังได้แม้เพิ่งมีกระดิ่งชนิด repeated

## LOW

- **L1 ค่าตั้งต้นของตัวเลือกทดสอบเป็นลูกค้า**: `settings.html:2253` ใช้ `sel.value = ts[0].key` ⇒ ถ้าบริษัทยังไม่ตั้งกลุ่มทีม ตัวเลือกแรกจะเป็นผู้ติดต่อจริง
  มีแค่ `confirm()` กั้นไว้ · ควรตั้งค่าเริ่มต้นเป็น "พิมพ์เอง" หรือกลุ่มทีมเท่านั้น
- **L2 ไม่มีร่องรอยการส่งถึงผู้ติดต่อ**: `POST test` ไปที่ `ep:` ถอดรหัส LINE id ของผู้ติดต่อแล้วส่งข้อความในนามบริษัท โดยไม่เขียน audit หรือ PiiAccessLog (ม.37)
  ควรเขียน audit แถวเดียว `{endpointId, actor}`
- **L3 กฎตาราง `SendAtHour` ที่ตกในช่วงเงียบถูกเลื่อนเงียบ ๆ**: ฟอร์มกฎใน `email-schedule.html` ไม่มีคำเตือน (grep "ชั่วโมงเงียบ"/Quiet ไม่พบ)
  หลังเลื่อนแล้วหน้าคิวยังแสดง ScheduledFor ใหม่กับหมายเหตุ ⇒ ข้อมูลไม่หาย แต่ตอนตั้งกฎไม่มีอะไรบอก
  ควรให้เซิร์ฟเวอร์ส่งประโยคเตือนใน DTO ของกฎเมื่อ `InWindow(SendAtHour)` และกฎมีช่อง LINE
  และกฎเดียวกันที่ติ๊กอีเมลด้วยจะได้อีเมลตอน 22:00 กับ LINE ตอน 08:00 ควรบอกเรื่องนี้ด้วย
- **L4 ใบเสร็จได้รับยกเว้นแม้ไม่ได้มาจากการกระทำของลูกค้า**: `ReceiptIssued` อาจมาจากการจับคู่รายการเดินบัญชีหรือนำเข้าตอนกลางคืน ไม่ใช่ลูกค้าเพิ่งจ่าย
  ⇒ ใบเสร็จออกไปตอนตี 2 ได้ · ถ้าจะให้ตรงเจตนาของ 260-4-2 ควรยกเว้นเฉพาะการชำระผ่าน gateway/ลิงก์ (PaymentIntent) ไม่ใช่การนำเข้าธนาคาร
- **L5 ปิดชั่วโมงเงียบแล้วแถวที่เลื่อนไว้ไม่ถูกปล่อย**: แถวที่เลื่อนแล้วยังรอถึง `ScheduledFor` เดิม (08:00) · ยอมรับได้ แต่ควรเขียนในประโยคสถานะ
  หรือให้ PUT ที่ปิดอัปเดต `ScheduledFor = now` ของแถวที่ `RuleCode = LINE-QUIET-HOURS` และยังรอส่ง
- **L6 Autopilot แสดงเรื่องค้าง 30 วันแม้แก้แล้ว**: เรื่อง `LineRepeatedFailure` และ `AutoSendEnqueueFailed` ยังแสดงต่อหลังตั้ง token ใหม่หรือหลังส่งเองแล้ว (ไม่มีเงื่อนไขว่าแก้แล้ว)
  ควรซ่อนเมื่อมีแถวคิว Sent ภายหลังกระดิ่ง หรือเมื่อเอกสารมีแถวคิว/ส่งเองแล้ว
- **L7 ไม่มีใครรู้เมื่อ OA กลางล้ม**: `ContactLineQueueService.cs:410` มีแค่ `LogWarning` ซึ่ง F2 ข้อ 7 ถือว่าไม่ใช่การล้มดัง และการพังครั้งเดียวกระทบทุก tenant · ควรมีกระดิ่งผู้ดูแลระบบครั้งเดียวต่อวัน
- **L8 กระดิ่งถึงเจ้าของเท่านั้น**: บริษัทที่เจ้าของไม่ได้ใช้งานแล้ว (เหลือแต่ Admin ที่มี `CompanySettingsEdit`) จะไม่มีผู้รับเลย ทั้งที่ได้กระดิ่งไปแล้ว 0 คน การกันซ้ำจึงไม่ถูกตั้ง
  ไม่สแปม แต่ไม่มีใครรู้ · ควรถอยไปผู้ที่มี `CompanySettingsEdit` เมื่อไม่มีเจ้าของที่ยังใช้งาน
- **L9 สแกนทุกบริษัทโดยไม่มีล็อกข้ามเครื่อง (มีก่อนรอบนี้ แต่ race ในเทสต์ชี้มาที่นี่)**: `EmailScheduleWorker.cs:75` → `ScanDueSoonAndOverdueAsync` ไม่ผ่าน `JobLock`
  และการกันซ้ำของ LINE ตรวจ `BaseKey` ด้วย `AnyAsync` ก่อน insert ขณะที่ unique index อยู่ที่ `IdempotencyKey` (`AccountingDbContext.cs:3792`)
  ⇒ ถ้าสองเครื่องสแกนพร้อมกันแล้วตัวหาผู้รับให้ผลต่างกัน (กุญแจต่างกันตาม M1 หรือมีการผูก/ถอนระหว่างสองการอ่าน) จะได้ทั้งแถว `line:none` กับแถวปลายทาง
  และ `FallbackNow` เข้าคิวอีเมลเพิ่ม ⇒ ลูกค้าได้ทั้ง LINE และอีเมล
  การย้าย collection ใน 118c3bb6/0dc5eea8 แก้ได้แค่เทสต์ ไม่ได้แก้ production · ควรครอบการสแกนด้วย `JobLock` ตามกฎ #4 D

## ตรวจแล้ว ไม่ใช่บั๊ก

- **การเลื่อนไม่วนซ้ำ**: แถวที่เลื่อนได้ `ScheduledFor` = สิ้นช่วง ซึ่งมากกว่า now ⇒ ไม่ถูกจองซ้ำในช่วงเงียบ · ไม่แตะ RetryCount/RetryKey
  · คำนวณข้ามเที่ยงคืนได้ถูก (Start>End ใช้ `+1 วัน` เมื่อสิ้นช่วง ≤ now) · Bangkok ไม่มีเวลาออมแสง · `DeferNote` แปลงเวลาเป็น +07 ผ่าน `CalendarDateUtc` แล้ว
- **คำทวงหลังเลยวันครบกำหนด**: การ์ดแสดงวันครบกำหนดเป็นวันที่ตายตัว (`LineDocumentCard.cs:42-53`) ไม่มีคำว่า "ใกล้ครบกำหนด" ⇒ ส่งช้าไป 1 คืนไม่ทำให้ข้อความผิด
  · ใบที่ชำระครบหรือถูกยกเลิกระหว่างเลื่อนจะถูกข้ามตอนส่ง (`ScheduledEmailSendCheck.SkipReason`)
- **ทำงานหลายเครื่อง**: การเลื่อนเขียนลงฐานหลังจองด้วย SKIP LOCKED · กระดิ่งซ้ำใช้ `pg_advisory_xact_lock` ครอบ check+insert ในธุรกรรมเดียว
  (`NotificationService.SendAsync` ใช้ context ของ scope เดียวกัน) ⇒ กันซ้ำข้ามเครื่องถูกต้อง
- **สิทธิ์ของ test-targets**: มี `RequirePermission(CompanySettingsEdit)` + `RejectApiKey` ตรงกับ `POST test` · ทุก query ผูก CompanyId ·
  ไม่มี LINE id ส่งออกนอกเซิร์ฟเวอร์ (ใช้ key `ep:{N}`) · ปลายทางที่บล็อกหรือยกเลิกการผูกแล้วถูกตัดออก (แต่ consent ยังไม่ถูกตรวจ ดู H1)
- **ปิดเป็นค่าตั้งต้น**: บริษัทที่มีอยู่เดิมพฤติกรรมไม่เปลี่ยน ถูกตาม F2 ข้อ 8 · ฝั่ง PUT ตรวจช่วงเวลาเฉพาะตอนเปิดใช้
