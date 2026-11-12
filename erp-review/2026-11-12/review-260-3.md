# ฝ่ายค้านรอบ 260-3 — commit 86193689 (+ doc adf7f266)

ผู้ตรวจ: subagent ฝ่ายค้าน (อ่านอย่างเดียว) · 2026-11-12 · ยังไม่ได้คอมไพล์ในเครื่องนี้ (ไม่มี SDK)

สรุป: 0 HIGH · 3 MED · 5 LOW · ตรวจแล้วไม่ใช่บั๊ก 10 ข้อ (ท้ายไฟล์)

---

## MED

### M1 — "ส่งไม่สำเร็จ = ไม่ประทับ" ไม่เป็นจริง: กระดิ่งถูกประทับว่าแจ้งแล้วเสมอ
- `Accounting/Helpers/CustomerCreditAgedNotice.cs:51-69` · `Accounting/Services/Implementations/NotificationEngine.cs:60-195`
- `NotificationEngine.DispatchAsync` ห่อทั้งเมธอดใน `try { … } catch (Exception ex) { _logger.LogError(…) }` และไม่โยนต่อ
  ⇒ `catch` ใน `RunAsync` (บรรทัด 62) ไม่มีวันทำงาน (dead branch) แล้ว `ExecuteUpdateAsync` ประทับ `UnappliedAgedNoticeAt` ทุกครั้ง
- สถานการณ์: DB ช่วงสั้นมีปัญหา หรือ `ResolveRoleAsync` / `DispatchSystemAsync` ล้มในรอบนั้น ⇒ engine กลืน error ⇒ แถวถูกประทับ ⇒
  การรับชำระที่ค้างนานรอบนั้นจะไม่ถูกแจ้งอีกเลย ขณะที่ DECISIONS 260-3-3 กับ doc-comment ของคลาสเขียนว่า "ส่งล้ม = ไม่ประทับ" (F2 #7: ข้อความที่ระบุสาเหตุต้องตรวจสาเหตุนั้นจริง)
  เช่นเดียวกัน กรณี "ไม่มีผู้รับ" (บริษัทไม่มี CompanyUser บทบาท Owner · ผู้รับทุกคน Inactive · ผู้รับตั้งปิดกระดิ่งส่วนตัว) ก็ประทับเงียบเหมือนกัน
  ส่วนกรณีบริษัทปิดคีย์นั้น DECISIONS ตัดสินไว้แล้วว่าตั้งใจ
- Db test R260_3 ใช้ `RecordingEngine` ที่ไม่โยน ⇒ ไม่มีเทสต์ทิศตรงข้ามของกิ่ง "ส่งล้ม"
- แก้ขั้นต่ำ: (ก) ให้มีทางที่ engine คืนผล (`Task<DispatchOutcome>`, หรือ overload `TryDispatchAsync` ที่ไม่กลืน error) แล้วประทับเฉพาะเมื่อ outcome เป็น
  Delivered/SuppressedByCompany หรือ (ข) ถ้าจะไม่แตะ engine ให้แก้ doc/DECISIONS เป็น "ประทับเมื่อสั่งส่งแล้ว (engine กลืน error เอง)" แล้วลบ `catch` ที่เป็น dead code ทิ้ง
  พร้อมเพิ่มเทสต์ engine ที่โยน exception เพื่อล็อกพฤติกรรมที่เลือก

### M2 — บริษัทที่มีแถวค้างนาน ≥ 500 แถวจะไม่ได้รับกระดิ่งของแถวใหม่อีกเลย
- `Accounting/Helpers/CustomerCreditWorkQueue.cs:145-150` (`OrderBy(PaymentDate).Take(Cap=500)`) + `CustomerCreditAgedNotice.cs:28` (`Where(a => a.AgedNoticeAt == null)` กรองหลัง Take)
- `AgedAsync` เรียงเก่าสุดก่อนแล้วตัดที่ 500 **รวมแถวที่แจ้งไปแล้ว** แล้ว `Compose` ค่อยกรอง `AgedNoticeAt == null` ในหน่วยความจำ
- สถานการณ์: ร้านขายบนแพลตฟอร์มมีเงินส่วนเกินเล็ก ๆ ค้าง 600 รายการ (แจ้งไปแล้วทั้งหมด ยังไม่มีใครจัดการ) ⇒ พอแถวที่ 601 อายุครบ 30 วัน มันอยู่นอก 500 แถวแรก ⇒ `Compose` ได้ null ⇒
  ไม่แจ้งไปตลอด และตัวกรองกว้างใน `RunAsync` (บรรทัด 42-45) ก็ยังพาบริษัทนี้เข้าลูปทุกวันโดยไม่มีผลอะไร
  รอบแรกที่ระบบมีแถวเกิน 500 แถว ข้อความ "N รายการ (฿ยอด)" ยังนับไม่ครบด้วย (ถูกตัดที่ 500 โดยไม่บอก)
- แก้ขั้นต่ำ: ให้ `AgedAsync` รับพารามิเตอร์ `onlyUnnotified` แล้วใส่ `p.UnappliedAgedNoticeAt == null` ในฝั่ง SQL ก่อน `Take` (นิยามอายุยังอยู่ที่เดียว) หรือให้ตัวส่ง
  นับยอดรวม/จำนวนด้วย `CountAsync`/`SumAsync` ที่ใช้ predicate ชุดเดียวกัน · เพิ่มเทสต์ 501 แถว: แจ้งแล้ว 500 + ใหม่ 1 ⇒ ต้องแจ้ง 1

### M3 — กล่องใบซื้อร่าง: ตัวกรองตามทางเข้าทำงานกับรายการที่ถูกตัดแล้ว ⇒ ตัวนับบอก N แต่กดแล้วขึ้นว่า "ไม่มีใบในทางเข้านี้"
- `Accounting/Helpers/PurchaseDraftInbox.cs:118` (ตัดที่ 200 ตาม `CreatedAt desc` ก่อนจัดกลุ่มทางเข้า) · `Accounting/wwwroot/pages/purchases.html:151-153`
- สถานการณ์: มีใบร่างสร้างเอง 250 ใบ (ใหม่กว่า) กับใบร่างจาก OCR 80 ใบ ⇒ ปุ่มขึ้น "จากสแกน (OCR) 80" แต่ในรายการ 200 แถวที่ส่งมาไม่มี OCR สักแถว ⇒ กดแล้วหน้าแสดง
  "ไม่มีใบในทางเข้านี้" และ return ก่อนถึงบรรทัดหมายเหตุ `truncated` ⇒ ตัวนับกับรายการขัดกันเอง ซึ่งคำตัดสิน 260-3-5 ห้ามไว้ ("ตัวนับ/รายการจาก query เดียว")
- แก้ขั้นต่ำ: ให้ endpoint รับ `?source=` แล้วกรองฝั่งเซิร์ฟเวอร์ก่อน `Take` (`setInboxSource` เรียก API ใหม่) หรืออย่างน้อยเมื่อ `truncated && items ว่าง` ให้แสดง
  "มี N ใบในทางเข้านี้แต่อยู่นอก 200 ใบล่าสุด" พร้อมลิงก์ไปหน้ารายการเอกสารที่กรองไว้

## LOW

### L1 — หน้าสิทธิ์พอร์ทัลเปิดยอดเงินรอจัดสรรให้ผู้ที่ไม่มีสิทธิ์ฝั่งขาย
- `Accounting/Services/Implementations/PortalService.cs:111-122` · `Accounting/Controllers/PortalController.cs` (`GET access` = `RequirePermission(ContactEdit)` อย่างเดียว)
- เส้นหลักของข้อมูลชุดเดียวกัน (`CustomerCreditController.cs:40-42`) บังคับให้ต้องเห็นฝั่งขาย (`VisibleDirections.Revenue`) แต่ทางเข้าใหม่นี้เช็กแค่ `ContactEdit`
  ⇒ ผู้ใช้ที่มี `Document.Purchase.View` + `ContactEdit` (เช่น ฝ่ายจัดซื้อที่ดูแลผู้ติดต่อ) เห็นยอดเงินรอจัดสรรของลูกค้าทุกราย ซึ่งหน้า customer-credits ปฏิเสธ 403
- แก้: ส่ง `UnappliedCredit` เฉพาะเมื่อ `VisibleDirectionsAsync(...).Revenue` เป็นจริง (ไม่จริงให้ส่ง `null` และหน้าไม่แสดงบรรทัดนี้)

### L2 — response ของ Create/Update สิทธิ์พอร์ทัลคืน `UnappliedCredit = 0` / `ShownToCustomer = false` (กฎ #4A echo)
- `PortalDtos.cs:7-8` (default param) · `PortalService.cs:664` `MapToAccessResponse` ไม่ได้ส่งสองค่านี้
- หน้าเว็บรอดเพราะเรียก `this.load()` ใหม่ทุกครั้ง แต่ผู้เรียกผ่าน API จะได้ค่า "ไม่มีเงิน / ลูกค้าไม่เห็น" ซึ่งเป็นค่าที่แต่งขึ้น (F2 #3/#5)
- แก้: ให้ `MapToAccessResponse` คำนวณสองค่านี้ (หนึ่ง query ผ่าน `UnappliedForContactAsync`) หรือเปลี่ยนเป็น `decimal?`/`bool?` แล้วส่ง null = "ไม่ได้คำนวณ"

### L3 — ตัวแทน `{UnappliedCredit}` ไม่มีในรายการตัวแปรบนหน้าตั้งค่าแม่แบบ
- `Accounting/wwwroot/pages/email-schedule.html:44-47`
- กฎที่ใช้ `BodyTemplate` ของตัวเอง (`EmailScheduleService.cs:544`) จะไม่ได้บรรทัดเงินรอจัดสรรเลย และผู้ใช้ไม่รู้ว่ามีตัวแทนนี้ ⇒ คำตัดสิน 260-3-2 ("อีเมลรายเดือนมีบรรทัดแยก") จริงแค่กับเนื้ออีเมลเริ่มต้น
- อีกเรื่องหนึ่ง: แม่แบบที่ใช้ตัวแทนนี้จะแสดง "0.00" ตอนลูกค้าไม่มีเงินรอจัดสรร (ถ้อยคำอาจทำให้เข้าใจผิด)
- แก้: เพิ่ม `<code>{UnappliedCredit}</code>` (หมายเหตุ "ใบแจ้งยอดรายเดือน") และพิจารณาเพิ่ม `{UnappliedCreditLine}` (HTML เดียวกับเนื้อเริ่มต้น ว่างเมื่อเป็น 0)

### L4 — `LiveConfigs` ใช้ "มีตัวไหนก็ได้" แต่ทางรับเงินจริงใช้ "config แรกตาม SortOrder"
- `Accounting/Helpers/PaymentChannelReadiness.cs:22-23` เทียบกับ `DocumentPayLinkService.cs:47-51` / `PaymentIntentService.cs:239-243`
- สถานการณ์: เปิดใช้ config สองตัว ตัวแรก (SortOrder 0) อยู่โหมดทดสอบ ตัวที่สองอยู่โหมดเงินจริง ⇒ การ์ด/แถบโหมดขึ้น "โหมดใช้งานจริง ✅" แต่ลิงก์ชำระ/QR ไปออกที่ตัวแรก
  (โหมดทดสอบ ไม่มีผลทางบัญชี) · หน้าเดิมก็มีปัญหาเดียวกันมาก่อน (`configs.filter(Live && isActive)`) แต่รอบนี้ย้ายขึ้นมาเป็นตัวตัดสินกลางที่รายการประจำใช้ด้วย
- แก้: ให้ `GatewayLive` = โหมดของ config ที่ `ActiveConfigAsync` เลือกจริง (ตัวเลือกตัวเดียวกัน) หรือแสดงคำเตือนเมื่อ config ที่เปิดใช้มีโหมดปนกัน

### L5 — กล่องใบซื้อร่างโหลดใบร่างฝั่งซื้อทั้งหมดของบริษัทเข้าหน่วยความจำ
- `PurchaseDraftInbox.cs:93-110` — ไม่มี `Take` ในขั้นอ่าน · `docIds.Contains(...)` ส่งรายการ id ทั้งหมดไปค้น `OcrScanResults` (รวม `ExternalMetadataJson`) และ `Contacts`
- บริษัทที่มีใบร่างค้างหลายหมื่นใบ (OCR ที่ไม่มีใครจัดการ · PR/PO ร่าง) จะเจอ payload ใหญ่ทุกครั้งที่เปิดหน้า purchases
- แก้: นับต่อทางเข้าด้วย SQL (`GroupBy` ตาม `PartnerSourceLinkId != null` + join scan) แล้วดึงแถวรายละเอียดแค่ `limit` แถว หรือ project แค่ `sourceChannel`/`OcrEngine` แทน JSON ทั้งก้อน
  ข้อสังเกตเพิ่ม: `PurchaseTypes` รวม `PurchaseRequisition`/`PurchaseOrder` ด้วย ⇒ PR/PO ร่างภายในถูกนับเป็น "ใบซื้อร่างรอตรวจ · สร้างเอง" (ควรยืนยันว่าตั้งใจ)

---

## ตรวจแล้วไม่ใช่บั๊ก (ไม่ต้องรายงานซ้ำ)

1. **ขอบเขตโทเคนพอร์ทัล**: `GetStatement` ใช้ `contactId` จากโทเคน และ `DenyPortalAsync` อ่านธงด้วย `companyId + accessId` ⇒ โทเคนของผู้ติดต่อ X ดูของ Y ไม่ได้ ·
   ถ้าใส่ `companyId` ของบริษัทอื่น ธงจะไม่พบแล้วโดนปฏิเสธ และ `UnappliedForContactAsync` ก็ผูก CompanyId + ContactId อยู่แล้ว
2. **แถว `UnappliedNeedsReview`**: ตัวอ่านทั้งสอง (`CustomerCreditQuery.cs:20-21, 48-49`) ตัดออกแล้ว ⇒ ลูกค้าไม่เห็นยอดที่ยังรอคนยืนยัน
3. **สกุลเงิน**: `DocumentService.cs:16209-16212` ปฏิเสธเงินส่วนเกินสกุลต่างประเทศ (`PaymentExcessPlan.RefuseExcess`) ⇒ `UnappliedAmount` เป็นเงินบาทเสมอ การแสดง "฿" จึงถูก
4. **ยอดที่จองให้ใบร่าง (`HeldForDraftsAsync`)**: ไม่หักจากยอดที่ลูกค้าเห็น ถือว่าถูก เพราะเงินยังไม่ได้จัดสรรจริง และใบร่างก็ไม่ขึ้นในพอร์ทัล (ยอดคงเหลือฝั่งหนี้ก็ยังไม่เปลี่ยน)
5. **HTML ในอีเมลใบแจ้งยอด**: ใส่แต่ตัวเลข decimal (`N2`) ไม่มีค่าที่ผู้ใช้คุมได้ · แม่แบบของผู้ใช้ผ่าน `Render(..., htmlEncodeValues: true)`
6. **portal.html / customer-portal.html**: ใช้ `money()`/`Layout.money()` ทำ `Number(...)` ก่อน ⇒ ไม่มีช่อง XSS
7. **LINE-PAY ไม่ดู payLink.IsOn**: ถูกแล้ว — `ContactLineQueueService.PayUrlAsync` (บรรทัด ~389-398) ตกไปที่ `CurrentUrlAsync` โดยไม่ขึ้นกับค่าตั้งต้น "แนบลิงก์ในอีเมล"
8. **PAY-TRANSFER Warn/Info**: ปิดทั้งโอนและออนไลน์ = Warn (ไม่มีทางรับเงิน) · ปิดโอนแต่ออนไลน์พร้อม = Info · ตรวจไม่ได้ = Warn (ไม่แต่งว่าพร้อม) — สมเหตุสมผล
9. **`AutopilotService.SettingsAsync` ผ่าน `readiness`**: ข้อมูลที่หลุดออกไปมีแค่สถานะช่องทาง + จำนวนสลิปค้าง ซึ่ง `AutopilotController` (`[Authorize]`) กับ `GET payment-settings` เดิม
   ก็เปิดให้สมาชิกทุกคนเห็นอยู่แล้ว ⇒ ไม่มีช่องรั่วใหม่ · `VisibleDirectionsAsync` แบบ legacy ("ไม่มีคีย์ = เห็นทั้งหมด") ก็ตรงกับ `DocumentController` list และ endpoint ฝั่งซื้อเดิม
   (`PartnerPaymentNoticeController`, `CrossTenantWorkflowController`) ไม่ใช่ช่องรั่วใหม่
10. **หลาย instance / migration**: `RunAsync` อยู่ใต้ `JobLock` ของ `OverdueDunningJob` และ `ExecuteUpdateAsync` มี `UnappliedAgedNoticeAt == null` ⇒ ทำซ้ำได้ไม่เกิดผลซ้ำ ·
    คอลัมน์ `timestamp NULL` เข้ากับ `UnappliedResolvedAt` เดิมและ `EnableLegacyTimestampBehavior` · `LiveConfigs` เพิ่มเงื่อนไข `IsActive` ให้รายการประจำ ⇒ แถว PAY-LIVE
    เปลี่ยนแค่ระดับ Info ไม่มีด่านใดบล็อก
