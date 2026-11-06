# LINE_MULTICHANNEL_DESIGN.md — ส่งเอกสาร/แจ้งเตือนหลายช่องทาง (อีเมล · LINE 1:1 · กลุ่ม LINE) ผูกปลายทางไว้ที่ผู้ติดต่อ

> ทีมวิเคราะห์ + ออกแบบ · 2026-11-06 · **เฟส 1 อ่านโค้ดอย่างเดียว ไม่ได้แก้โค้ด** (ทีม e-Tax/อีเมลรอบ 244 กำลังแก้ไฟล์ชุดเดียวกัน)
> ทุกการอ้างไฟล์อยู่ใต้ `Accounting/` · เลขบรรทัดเป็นของ HEAD `26ec41b4`
> คำขอเจ้าของ (2026-11-06): ติ๊กเลือกช่องทางในตารางส่งได้ (อีเมล/LINE) เมื่อช่องทางพร้อม · มีช่องของบริษัทเองใช้ของบริษัท ถ้าไม่มีใช้ช่องของ NextAcc แล้วตัดโควตา
> แอดมินแพลตฟอร์มกำหนดโควตาได้ และบริษัทซื้อเพิ่มได้ · LINE ส่งหาคนโดยตรงได้ (คนนั้นสมัครและผูก LINE แล้วเป็นผู้ใช้ฝั่งลูกค้าหรือผู้ขาย) หรือส่งเข้ากลุ่ม (ดึงบอทเข้ากลุ่ม พิมพ์ `#HeyNextAcc` แล้วได้ groupId ไปตั้งค่า)
> **ข้อมูลเพิ่มจากเจ้าของ:** ผูก LINE id และ group id ไว้ที่**ข้อมูลผู้ติดต่อ** เพื่อให้การแจ้งเตือนอื่นใช้ปลายทางชุดเดียวกันได้ ⇒ เอกสารนี้ออกแบบให้ **ผู้ติดต่อ (Contact) เป็นเจ้าของปลายทาง** ส่วนตารางส่งแค่อ่านปลายทางเหล่านี้

---

## 0. สรุปสั้น (TL;DR)

1. **ของที่มีอยู่แล้ว ไม่ต้องสร้างใหม่:** ตัวส่ง LINE push ตัวเดียว (`LineNotifyService`) · ตัวตัดสินว่าข้อความไปถึงใครได้ (`NotificationAudiencePolicy`) · การผูก LINE ด้วยรหัสใช้ครั้งเดียว `LN-XXXXXXXX` (`CustomerLineLinkRule`) · webhook ของ OA กลางและ OA ของบริษัท · ตัวนับโควตาข้อความช่องกลางรายเดือนพร้อมการซื้อแพ็กเพิ่ม (`NotifyQuotaService` · `notify.topup`) · ตัวเลือกผู้ส่งอีเมลในนามบริษัท (`OnBehalfEmailSender`) · คิวตั้งเวลาที่จองแถวด้วย SKIP LOCKED · Flex การ์ดเอกสารที่ยังไม่มีผู้เรียก (`DocumentLineDeliveryService`) · ลิงก์ชำระ (รอบ 241) และลิงก์เซ็น (รอบ 240)
2. **ช่องว่างหลัก:** ตารางส่งรู้จักแค่อีเมล · ผู้ติดต่อไม่มีปลายทาง LINE ที่ใช้งานได้จริง (`Contact.LineUserId` ไม่มีโค้ดใดเขียนค่า) · บอทเพิกเฉยข้อความในกลุ่ม (`LineWebhookController.cs:84`) · ไม่มีคำสั่ง `#HeyNextAcc` · นโยบาย 232-Notify **ห้าม**ส่งเข้ากลุ่มผ่าน OA กลาง · อีเมลตามตารางที่ส่งผ่าน SMTP ของ NextAcc ถูกจำกัดแค่เพดานรายวัน ไม่ได้ตัดโควตารายเดือน · ตัวนับรายเดือนเป็นก้อนเดียว ไม่แยกอีเมลกับ LINE
3. **แกนของการออกแบบ:** ตารางใหม่ `ContactNotifyEndpoint` เก็บปลายทางของผู้ติดต่อ 3 ชนิด ได้แก่ อีเมล · ผู้ใช้ LINE · กลุ่ม LINE แต่ละแถวเลือกได้ว่ารับเรื่องใด (เอกสาร · ทวงชำระ · ใบเสร็จ · 50 ทวิ · ลิงก์เซ็น · อื่น ๆ)
   ทุกเส้นที่ส่งถึงผู้ติดต่อเรียก **`ContactRecipientResolver` ตัวเดียว** แล้วส่งผ่าน **`ChannelDispatcher` ตัวเดียว** ซึ่งเลือกช่องของบริษัทก่อน ถ้าไม่มีจึงใช้ช่องของ NextAcc และจองโควตาก่อนส่ง
4. **กลุ่ม LINE:** บอทถูกเชิญเข้ากลุ่ม → มีคนพิมพ์ `#HeyNextAcc` → บอทตอบด้วย reply (ไม่เสียค่าข้อความ) บอกชื่อกลุ่ม groupId และรหัสผูก `GB-XXXXXXXX` อายุ 30 นาที
   → ผู้ดูแลบริษัทนำ**รหัส**ไปใส่ที่ผู้ติดต่อ › แท็บช่องทางแจ้งเตือน › เพิ่มกลุ่ม LINE · ระบบไม่ยอมผูกด้วย groupId อย่างเดียว เพื่อกันส่งข้อมูลเข้ากลุ่มผิด
   → ระบบแสดงชื่อและรูปกลุ่มให้ยืนยัน → บอทประกาศในกลุ่มว่าผูกกับบริษัทใดแล้ว · บอทถูกเตะออก ⇒ ปลดการผูกอัตโนมัติและแจ้งบริษัท
5. **ต้องให้เจ้าของตัดสิน** (§9): ให้ส่งเข้ากลุ่มผ่าน OA กลางได้หรือไม่ (ขัดกับคำตัดสิน 232-Notify ในรูปแบบเดิม เราเสนอข้อยกเว้นที่แคบ) · ส่งเข้ากลุ่มหนึ่งครั้งนับโควตากี่หน่วย · โควตาแยกหรือรวม · โควตาเต็มแล้วจะถอยไปอีเมลหรือไม่ · จะแสดงยอดเงินในกลุ่มหรือไม่
6. **แผนงาน:** เฟส L1–L6 (§8) ต่อจากรอบ 244 · เลขรอบ 245–247 ชนกับแผน `AUTO_SEND_WHT_DESIGN.md` §9 จึงใช้ชื่อเฟส `L*` แล้วให้ผู้ประสานงานเรียงลำดับ

---

## 1. สภาพปัจจุบัน (Current-state inventory)

### 1.1 ตารางส่งเอกสาร (อีเมลอย่างเดียว)

| ชิ้น | หลักฐาน | สิ่งที่ทำ |
| --- | --- | --- |
| `EmailScheduleRule` | `Models/Entities/EmailSchedule.cs:30-89` | กฎต่อ trigger (Approved/DueSoon/Overdue/PayrollPaid/WhtCertIssued/Recurring/MonthlyStatement) · เทมเพลต · BCC · รอบ 241 เพิ่ม `AttachPayLink`/`PayLinkChannelsJson`/`PostPay*` · **ไม่มีช่องเลือกช่องทาง** |
| `EmailQueue` | `EmailSchedule.cs:95-127` | คิวทีละผู้รับ ใช้ `ToEmail` (เป็นอีเมลเท่านั้น) · idempotency `(CompanyId, EntityType, EntityId, RuleId, ScheduledFor)` |
| เข้าคิว | `Services/Implementations/EmailScheduleService.cs:55-425` | `OnDocumentApprovedAsync` · `OnPayrollPaidAsync` · `OnWhtCertIssuedAsync` · `OnRecurringDocumentCreatedAsync` · `ScanMonthlyStatementsAsync` · `ScanDueSoonAndOverdueAsync` |
| ส่ง | `EmailScheduleService.cs:428-540` | จองแถวด้วย `FOR UPDATE SKIP LOCKED` (`:432-447`) · ตรวจเอกสารซ้ำตอนส่ง `RecheckDocumentAtSendAsync` (`:746`) |
| ผู้ส่ง | `EmailScheduleService.cs:545-640` → `DocumentEmailService.ResolveOnBehalfSenderAsync` → `Helpers/OnBehalfEmailSender.cs:47` | ผู้ส่งของบริษัทที่ทดสอบผ่านแล้ว ⇒ ใช้ของบริษัท · ไม่งั้นใช้ SMTP ของ NextAcc ในนาม "{บริษัท} ผ่าน NextAcc" + Reply-To ของบริษัท |
| เพดานอีเมลช่องกลาง | `Helpers/PlatformEmailQuota.cs:16` + `Helpers/SignEmailThrottle.cs:14-21` | **เพดานรายวัน** 200 ฉบับ (ยังไม่ยืนยันตัวตน 20) · ต่อผู้รับ 3 ฉบับ/เอกสาร/วัน และ 20 ฉบับ/วัน · ผู้รับไม่เกิน 5 · **ไม่ตัดโควตารายเดือน `NotifyUsageCounters`** |
| ลิงก์ชำระ | `EmailScheduleService.cs:35-42`, `:682-684` | คิวเก็บที่หมาย `{PayLink}` แล้วประกอบ URL ตอนส่ง (ไม่เก็บ token ลงฐาน) |
| คอนโทรลเลอร์/หน้า | `Controllers/EmailScheduleController.cs:27-92` · `wwwroot/pages/email-schedule.html` | CRUD กฎ · ดูคิว · ส่งซ้ำ/ยกเลิก |
| Worker | `Services/Background/EmailScheduleWorker.cs` | วนประมวลคิว + งานหลังรับชำระ (รอบ 241) |

### 1.2 LINE

| ชิ้น | หลักฐาน | สิ่งที่ทำ / ข้อจำกัด |
| --- | --- | --- |
| ตัวส่ง push ตัวเดียว | `Services/Implementations/LineNotifyService.cs:66-97` (`PushCoreAsync`) | คืนผลพร้อมเหตุ · ไม่บันทึก userId เต็มใน log · **ยังไม่ส่ง `X-Line-Retry-Key`** · ส่ง push ทีละครั้ง |
| เส้นของบริษัท | `LineNotifyService.cs:116-143` | `TryPushCompanyGroupAsync` (กลุ่มเดียวของบริษัท `CompanySettings.LineDefaultGroupId`) · `TryPushToUserAsync`/`TryPushFlexToUserAsync` ใช้ OA ของบริษัทเท่านั้น |
| เส้น OA กลาง | `LineNotifyService.cs:165-211` | `TryPushViaNextAccAsync`/`TryPushFlexViaNextAccAsync` ส่งถึง userId 1:1 · `TryPushPlatformOpsAsync` เข้ากลุ่มของผู้ดูแลแพลตฟอร์ม |
| ตัวตัดสินผู้รับ | `Helpers/NotificationAudiencePolicy.cs:75-110` | `TenantBusiness` + OA กลาง + `CompanyGroup` ⇒ **Deny** `NOTIFY-TENANT-NO-COMPANY-OA` (`:91-93` "กลุ่มของบริษัทผ่าน OA กลาง = ไม่มี") · OA กลาง + `TenantMember` ⇒ Ok |
| ค่าตั้ง OA บริษัท | `Models/Entities/CompanySettings.cs:233-254` | `LineEnabled` · token/secret (เข้ารหัส) · `LineDefaultGroupId` (กรอก groupId เองแบบดิบ ไม่มีการพิสูจน์ว่าเป็นกลุ่มของใคร) · `LineOaBasicId` · `LineOaDestination` |
| ค่าตั้ง OA กลาง | `Models/Entities/SiteSettings.cs:170-181` + `NextAccLineConfig` | token/secret/basicId/botUserId/GroupId ของผู้ดูแล |
| webhook OA กลาง | `Controllers/LineWebhookController.cs:33-114` | ตรวจลายเซ็น · follow/unfollow · ข้อความ/postback/รูปจาก **user เท่านั้น** — `:84` `if (source.type != "user") continue; // ignore group/room for now` · **ไม่มี join/leave** |
| webhook OA บริษัท | `Controllers/CustomerLineWebhookController.cs:40-102` | `/api/line/webhook/c/{companyId}` · ตรวจด้วย secret ของบริษัท · ประมวลเฉพาะ user (`:72`) · ผูกรหัส `ผูก LN-…` · ตอบด้วย reply token |
| ผูก LINE ลูกค้าเว็บ | `Models/Entities/CustomerLineLink.cs` · `Helpers/CustomerLineLinkRule.cs` · `Services/Implementations/Cms/CustomerLineLinkService.cs` | ผูกกับ `SiteId` + `TargetKind ∈ {member, booking, order}` (CMS) · เก็บ `OaScope` และ `OaDestination` · บล็อกเมื่อ unfollow · ผูกกับหลักฐานยินยอม `ConsentRecordId` · รหัสเก็บเป็น hash ใช้ครั้งเดียว อายุ 30 นาที |
| ผูก LINE ผู้ใช้ ERP | `LineBotService.cs:80-200` + `LineBindCode` | `ผูก 123456` ผ่าน OA กลาง ⇒ `User.LineUserId` |
| `Contact.LineUserId` | `Models/Entities/Document.cs:1042` | **ไม่มีโค้ดใดเขียนค่า** มีแต่ PDPA erase ที่ล้างค่า (`PdpaService.cs:870`) · ผู้อ่าน (แก้ตามฝ่ายค้าน L1 S1 — เดิมเขียนว่ามีคนเดียว ผิด): `DocumentLineDeliveryService` (ไม่มีผู้เรียก) **และ** แจ้งบัตรคอร์สใกล้หมดอายุ `CourseCreditService.Membership` (ส่งเป็นทางสำรองให้ `BookingNotifier`) — ตัวหลังถูกถอดในรอบ 245 L1 (ไม่รู้ OA ⇒ เสี่ยงส่งผิดคน · ใช้ `CustomerLineLink` แทน) |
| Flex เอกสาร | `Services/Implementations/DocumentLineDeliveryService.cs:41-137` | การ์ดแสดงเลขที่ ยอด และวันครบกำหนด · ปุ่มเปิด `portal.html?company=` (ไม่ใช่ลิงก์เฉพาะใบ) · **ไม่มีผู้เรียก** (คำตัดสินข้อ 101: "ต่อสายรอบถัดไป") · คอมเมนต์ระบุว่าต้องหาผู้รับผ่าน `ICustomerLineLinkService.ResolveLineUserIdAsync` ไม่ใช่คอลัมน์นี้ |
| หลักฐานการส่ง | `Helpers/DocumentDeliveryEvidence.cs:14-17` | นับแค่ `DocumentEmailLog` ที่ Sent · ระบุไว้ว่าถ้าต่อสาย LINE ต้องเพิ่มช่องทาง LINE ในตัวตัดสินนี้ด้วย |
| สลิปเงินเดือนทาง LINE | `PayslipLineDeliveryService.cs` | ส่งถึงพนักงาน 1:1 ผ่าน OA กลาง |

### 1.3 โควตาและการซื้อเพิ่ม

| ชิ้น | หลักฐาน | สิ่งที่ทำ |
| --- | --- | --- |
| นโยบาย | `Helpers/NotifyQuotaPolicy.cs:4-80` | นับเฉพาะช่องกลาง (`SrcNextAccOa`/`SrcSystemSmtp`) · 1 หน่วย = 1 push ต่อผู้รับ หรือ 1 อีเมลต่อผู้รับ · งวด = เดือนไทย · ใช้ค่าเฉพาะบริษัทก่อน แล้วจึงค่าของแพ็กเกจ · เต็มแล้วใช้โบนัส · เตือนที่ 80/100% · ข้อความจำเป็นนับแต่ไม่ระงับ |
| ตัวนับ | `Models/Entities/CustomerNotify.cs:50-60` `NotifyUsageCounter(CompanyId, Period, Used, BonusUsed, Warned*)` | **ก้อนเดียว** รวมอีเมลและ LINE |
| เพดาน | `Subscription.MaxSharedMessagesPerMonth` (`Subscription.cs:125`) · `PlanTemplate.MaxSharedMessagesPerMonth` / `TrialMax…` (`:268`, `:297`) · โบนัส `NotifyBonus*` | |
| บริการ | `NotifyQuotaService` (claim/reserve/release/complete/purchase) | **ผู้เรียกจริงมีแค่ `BookingNotifier` · `CustomerNotifyResendService` · สรุปรายวัน** (`Program.cs:747`, `:777`) — ตารางส่งเอกสารไม่ได้เรียก |
| แอดมิน | `Controllers/AdminNotifyQuotaController.cs:47-58` · `admin-quota.html` | ตั้งเพดาน · ขนาด/ราคาแพ็ก (`Metering.cs:57-60`) |
| ซื้อเพิ่ม | `QuotaAddOnPurchaseService` · `QuotaAddOnPaymentHandler` · `QuotaAddOnPricing` (รอบ 213/229/235) | ซื้อ/ชำระ/ออกใบ · WHT ของค่าบริการแพลตฟอร์มใช้กติกาเดิม |

### 1.4 ผู้ใช้ฝั่งลูกค้า/ผู้ขาย

| ชิ้น | หลักฐาน | สิ่งที่ทำ |
| --- | --- | --- |
| พอร์ทัลลูกค้า | `PortalAccess` (`Models/Entities/Intelligence.cs:744-761`) · `PortalController` | 1 แถวต่ออีเมลต่อผู้ติดต่อ · รหัสผ่าน · สิทธิ์ดูใบแจ้งหนี้/ชำระ |
| พอร์ทัลผู้ขาย | `VendorPortalToken` (`BankAccount.cs:657-677`) · `VendorPortalController` | magic link ต่อผู้ติดต่อ (Role Vendor) |
| `CompanyUser.Role` | `Models/Enums/AllEnums.cs:4-16` | บทบาทพนักงานเท่านั้น **ไม่มีบทบาทลูกค้า/ผู้ขาย** — ห้ามนำคนนอกเข้ามาเป็น CompanyUser |

### 1.5 ช่องว่าง (เรียงตามผลกระทบ)

| # | ช่องว่าง | ผลกระทบ |
| --- | --- | --- |
| G1 | ตารางส่งมีแต่อีเมล ไม่มีช่องเลือก LINE | ทำตามคำขอหลักไม่ได้ |
| G2 | ผู้ติดต่อไม่มีปลายทาง LINE ที่ใช้งานได้ (`Contact.LineUserId` ไม่มีผู้เขียน และไม่ระบุว่าผูกกับ OA ใด) | ส่ง 1:1 ไม่ได้ · ข้อมูลเดิมที่อาจมีในคอลัมน์นี้ใช้ส่งไม่ได้ เพราะ userId เป็นค่าต่อ provider |
| G3 | บอทเพิกเฉยเหตุการณ์ในกลุ่ม และไม่มี join/leave/`#HeyNextAcc` | ผูกกลุ่มไม่ได้ · ถูกเตะออกแล้วระบบไม่รู้ |
| G4 | 232-Notify ห้ามกลุ่มผ่าน OA กลาง | บริษัทที่ไม่มี OA ของตัวเองส่งเข้ากลุ่มไม่ได้ (ขัดกับคำขอ — ต้องตัดสิน §6) |
| G5 | อีเมลตามตารางผ่าน SMTP ของ NextAcc ไม่ตัดโควตารายเดือน | ขัดกับคำขอให้ "กินโควตา" · เสียงบไม่จำกัดต่อเดือน (มีแต่เพดานรายวัน) |
| G6 | ตัวนับรายเดือนเป็นก้อนเดียว | แอดมินตั้งเพดานหรือขายแพ็กแยก LINE กับอีเมลไม่ได้ · ต้นทุนของสองช่องต่างกันมาก |
| G7 | Flex เอกสารไม่มีผู้เรียก ใช้ปุ่มพอร์ทัลรวม และไม่มีลิงก์ดูเอกสารเฉพาะใบ | ผู้รับเปิดดู PDF ใบนั้นไม่ได้หากไม่มีบัญชีพอร์ทัล |
| G8 | หลักฐานการส่งไม่นับ LINE | เมื่อต่อสายแล้ว ด่าน "ส่งให้ลูกค้าแล้ว" (เช่น การยกเลิกใบ) จะไม่เห็นการส่งทาง LINE |
| G9 | `LineDefaultGroupId` รับ groupId ดิบโดยไม่พิสูจน์ | พิมพ์ผิดหรือวางผิดกลุ่ม ⇒ ข้อมูลรั่วเข้ากลุ่มของคนอื่น |
| G10 | ไม่เก็บ `webhookEventId` | LINE ส่ง webhook ซ้ำได้ ⇒ ผูกหรือตอบซ้ำ |

---

## 2. หลักการออกแบบ

1. **ผู้ติดต่อเป็นเจ้าของปลายทาง** (ตามที่เจ้าของเพิ่มมา) — ปลายทางทุกชนิดอยู่ใน `ContactNotifyEndpoint` และทุกเส้นอ่านผ่าน `ContactRecipientResolver` ตัวเดียว (กฎเหล็ก #4 F2 ข้อ 4) · ตารางส่งทับเฉพาะกฎได้ แต่ไม่ต้องผูกปลายทางซ้ำ
2. **ช่องของบริษัทก่อน แล้วจึงช่องของ NextAcc** — ตัดสินที่ `ChannelSenderPolicy` (pure) ตัวเดียว · ใช้ช่องของ NextAcc ⇒ จองโควตาก่อนส่ง และคืนโควตาเมื่อส่งล้ม (แพตเทิร์นของ `BookingNotifier`)
3. **ปลายทาง LINE ผูกกับ OA** — userId และ groupId ใช้ได้เฉพาะกับ OA ที่เห็นค่านั้น (เป็นค่าต่อ provider) · ปลายทางจึงเก็บ `OaScope` + `OaDestination` (บอทที่รับการผูก) · บริษัทเปลี่ยน OA ⇒ ปลายทางเดิมใช้ไม่ได้ และต้องเชิญผูกใหม่ (ใช้ `CustomerRelinkInvite` แบบรอบ 212 B)
4. **Server computes · page displays** — ความพร้อมของแต่ละช่อง ป้ายสถานะ เหตุผล และโควตาคงเหลือคำนวณที่เซิร์ฟเวอร์ทั้งหมด หน้าเว็บแสดงอย่างเดียว
5. **ล้มดังสามที่** — แถวคิวต่อช่องมีสถานะและเหตุ · หน้าเอกสารแสดงไทม์ไลน์การส่ง · กระดิ่งแจ้งบริษัทเมื่อถูกระงับหรือล้มซ้ำ · ห้ามใช้ `catch {}`
6. **ไม่มีสถานะที่ระบบประทับเอง** — "ส่งทาง LINE แล้ว" = LINE ตอบ 200 เท่านั้น (บันทึก `x-line-request-id`) · LINE ไม่ใช่การส่ง e-Tax by Email (§7.3)
7. **ส่งให้น้อยที่สุดที่พอใช้งาน** (PDPA ม.22) — ไม่ส่งเลขผู้เสียภาษีหรือเลขบัตรทาง LINE · ไม่แนบไฟล์ · ใช้ลิงก์ที่มีอายุ · ห้ามส่งสลิปเงินเดือนเข้ากลุ่ม

---

## 3. กระบวนการตั้งแต่ต้นจนจบ

### 3.a ตั้งค่าบริษัท — ช่องทางของตัวเองหรือของ NextAcc

```
ตั้งค่า › ช่องทางส่งข้อความ (หน้าใหม่ settings-channels · รวมของเดิมจากหน้าอีเมลและหน้า LINE)
 ┌ อีเมล ─────────────────────────────────────────────────────────────┐
 │ ● ใช้อีเมลของบริษัท (SMTP / Microsoft / Google)  [สถานะ: ทดสอบผ่าน 05/11]│
 │ ○ ใช้อีเมลของ NextAcc ในนามบริษัท (ตัดโควตาอีเมล · เพดานรายวัน 200)       │
 └──────────────────────────────────────────────────────────────────┘
 ┌ LINE ─────────────────────────────────────────────────────────────┐
 │ ○ ใช้ LINE OA ของบริษัท  [token ✓ · secret ✓ · webhook ✓ · bot U12…]   │
 │ ● ใช้ LINE OA ของ NextAcc (@nextacc) ในนามบริษัท (ตัดโควตา LINE)        │
 │   ⓘ ลูกค้าจะเห็นแชตชื่อ NextAcc และข้อความขึ้นต้นว่า "ในนาม {บริษัท}"       │
 └──────────────────────────────────────────────────────────────────┘
 โควตาเดือนนี้:  อีเมล ███████░░ 712/1,000   LINE ██░░░░░░ 180/1,000 [ซื้อเพิ่ม]
```

- ตัวตัดสินช่องอีเมลคือ `OnBehalfEmailSender.Resolve` ตามเดิม · ตัวตัดสินช่อง LINE คือ `ChannelSenderPolicy.Line` ซึ่งคืน `CompanyOa` เมื่อ `LineEnabled && token ถอดได้ && secret ถอดได้ && LineOaDestination != null` (webhook ต้องเคยยิงเข้ามาแล้ว) ไม่เช่นนั้นคืน `NextAccOa` เมื่อแอดมินเปิดให้ใช้และ OA กลางพร้อม ถ้าไม่ครบทั้งสองคืน `None` พร้อมเหตุ
- **ไม่ถอยข้าม OA ตอนส่ง** — ปลายทาง LINE ผูกกับ OA ใด ต้องส่งผ่าน OA นั้น (§2 ข้อ 3) · "ใช้ของบริษัทก่อน" จึงหมายถึง**ตอนผูกปลายทาง** ว่าจะให้ผู้รับเพิ่มเพื่อน OA ใด
- บริษัทเปลี่ยนจาก OA กลางไปเป็น OA ของตัวเอง ⇒ ปลายทางเดิมยังส่งผ่าน OA กลางได้ (นับโควตา · มีกลไกเดิม `CustomerNotifyRequest.LineViaNextAcc`) และระบบเสนอให้เชิญผูกใหม่

### 3.b ผู้รับ: LINE 1:1 ถึงคนของผู้ติดต่อ

**ใครเป็นผู้รับ:** "บุคคลของผู้ติดต่อ" (เช่น ฝ่ายบัญชีของลูกค้า หรือเซลส์ของผู้ขาย) ที่**สมัครเป็นผู้ใช้พอร์ทัล** และถูกผูกกับผู้ติดต่อในบทบาทลูกค้าหรือผู้ขาย
ขยาย `PortalAccess` ให้มี `Role ∈ {Customer, Vendor}` แทนการสร้าง `CompanyUser` เพราะคนนอกต้องไม่ได้สิทธิ์ใน ERP

```
ผู้ติดต่อ › ช่องทางแจ้งเตือน › [+ เชิญผู้ใช้ให้ผูก LINE]
  ชื่อ: คุณสมหญิง (บัญชี)   อีเมล: somying@abc.co.th   บทบาท: ลูกค้า
  รับเรื่อง: ☑เอกสาร ☑ทวงชำระ ☑ใบเสร็จ ☐50 ทวิ ☑ลิงก์เซ็น
  ช่องที่ส่งคำเชิญ: (•) อีเมล  ( ) คัดลอกลิงก์ / QR
         │
         ▼  ระบบสร้าง PortalAccess(Role=Customer, ContactId, IsActive=false) + ContactLinkInvite(token hash, 7 วัน)
อีเมล "{บริษัท} เชิญคุณรับเอกสารทาง LINE" → หน้า /pages/contact-line-join.html?t=…
  1) ตั้งรหัสผ่าน หรือเข้าสู่ระบบพอร์ทัล (ถ้ามีบัญชีแล้ว)
  2) แสดงหนังสือแจ้งความเป็นส่วนตัว + ☑ ยินยอมรับข้อความเอกสารทาง LINE (PdpaConsentRecord · version · evidenceHash)
  3) ปุ่ม [เพิ่มเพื่อน @oa] (AddFriendUrl) → [ส่งรหัส] (SendCodeUrl เปิดแชตพร้อมข้อความ "ผูก LN-XXXXXXXX")
         │
         ▼  webhook ของ OA ที่ออกรหัส (กลางหรือของบริษัท) → TryRedeemAsync (เส้นเดิม · เพิ่ม TargetKind = "contact-person")
  ContactNotifyEndpoint(Kind=LineUser, ContactId, PortalAccessId, LineUserId, OaScope, OaDestination, ConsentRecordId, Topics)
  ตอบกลับ (reply · ฟรี): "ผูกสำเร็จ — คุณจะได้รับเอกสารจาก {บริษัท} ทางนี้ · พิมพ์ 'หยุด {บริษัท}' เพื่อยกเลิก"
  กระดิ่งบริษัท: "คุณสมหญิง (ABC) ผูก LINE แล้ว"
```

- **ใช้กลไก `LN-` เดิมทั้งชุด** (คำตัดสินข้อ 170 ไม่ใช้ LINE Login/LIFF) และเพิ่ม `CustomerLineLinkRule.KindContactPerson` · `CustomerLineLinkCode.SiteId` ต้องเป็น nullable (migration: `ALTER COLUMN DROP NOT NULL`) หรือใช้ `SiteId = Guid.Empty` — **เลือกแบบ nullable** เพราะ Guid.Empty คือการ reuse ค่าผิดความหมาย
- **ทางลัดที่ไม่ต้องสมัคร (ตัวเลือก):** ผู้ดูแลบริษัทกด "สร้าง QR ผูก LINE" บนหน้าผู้ติดต่อ แล้วให้ลูกค้าสแกนต่อหน้า ⇒ ได้ปลายทางที่ `PortalAccessId = null` · ต้องให้เจ้าของตัดสิน (§9 Q6) เพราะคำขอระบุว่า "ต้องสมัครสมาชิก"
- **ยกเลิกการผูก (ทิศตรงข้าม):** มีสามทาง (1) ผู้รับพิมพ์ `หยุด` หรือ `#ยกเลิก` ⇒ `UnlinkedAt`, เหตุ `user` · (2) ผู้รับ unfollow OA ⇒ `BlockedAt` (เส้นเดิม `SetBlockedAsync`) และ follow กลับ ⇒ ล้าง · (3) ผู้ดูแลบริษัทกดลบ หรือปิด PortalAccess ⇒ `UnlinkedAt`, เหตุ `company` และบอทตอบหนึ่งครั้งว่าถูกยกเลิก · ทั้งสามทางลง audit chain

### 3.c ผู้รับ: กลุ่ม LINE (`#HeyNextAcc`)

```
[ผู้ดูแลบริษัท / ลูกค้า] สร้างกลุ่ม LINE → เชิญ @nextacc (หรือ OA ของบริษัท) เข้ากลุ่ม
      │  LINE ส่ง event "join" (source.type=group, groupId)
      ▼
บอท (reply · ฟรี): "สวัสดีครับ NextAcc พร้อมใช้งาน — พิมพ์ #HeyNextAcc เพื่อรับรหัสผูกกลุ่มนี้กับบริษัทของคุณ
                  บอทจะไม่อ่านหรือเก็บข้อความอื่นในกลุ่ม"
      │  สมาชิกพิมพ์ "#HeyNextAcc"
      ▼
LineGroupCommandParser.Parse(text) == HeyNextAcc   (ไม่สนตัวพิมพ์ · ตัดช่องว่าง · รับ ＃ แบบเต็มความกว้าง · ข้อความอื่น ⇒ ไม่ทำอะไรและไม่เก็บ)
rate-limit ต่อ groupId (IChatRateLimiter · DB): 5 ครั้งต่อชั่วโมง
GET /v2/bot/group/{groupId}/summary  → groupName, pictureUrl
GET /v2/bot/group/{groupId}/members/count → memberCount
LineGroupBindCode(CodeHash, GroupId, OaScope, OaDestination, GroupName, MemberCount, ExpiresAt=+30m) ← บันทึก
      ▼
บอท (reply · ฟรี):
  "🔗 ผูกกลุ่ม "{groupName}" กับ NextAcc
   รหัสผูก: GB-7KQ2M9XA (หมดอายุ 14:35 น.)
   Group ID: C1a2b3…  (ใช้คู่กับรหัสเท่านั้น)
   ขั้นต่อไป: ผู้ดูแลบริษัทเปิด NextAcc › ผู้ติดต่อ › {เลือกผู้ติดต่อ} › ช่องทางแจ้งเตือน › เพิ่มกลุ่ม LINE › ใส่รหัสนี้"
      │
      ▼  ผู้ดูแลบริษัท (สิทธิ์ contacts.notify.manage) ใส่รหัส GB-… (วาง groupId ได้ แต่ต้องมีรหัสคู่กันเสมอ)
POST /api/companies/{cid}/contacts/{contactId}/notify-endpoints/line-group/preview {code}
  → ตรวจ hash · ยังไม่หมดอายุ · ยังไม่ใช้ · OA ของรหัสต้องตรงกับ OA ที่บริษัทใช้อยู่ (ตาม ChannelSenderPolicy)
  → คืน {groupName, pictureUrl, memberCount, oaLabel} ให้หน้าแสดง "ยืนยันผูกกลุ่ม 'ABC × ร้านเรา – บัญชี' (12 คน) กับผู้ติดต่อ ABC?"
POST …/line-group/confirm {code, topics[], showAmounts, acceptResponsibility:true}
  → UPDATE LineGroupBindCode SET UsedAt=now WHERE UsedAt IS NULL (ใช้ครั้งเดียว · ชนะได้คำขอเดียว)
  → ContactNotifyEndpoint(Kind=LineGroup, GroupId, OaScope, OaDestination, GroupNameSnapshot, BoundByUserId, Topics, ShowAmounts)
  → audit chain (RuleCode LINE-GROUP-BIND) 
  → บอท push ประกาศหนึ่งครั้ง (ไม่นับโควตา: ข้อความระบบ) "กลุ่มนี้ผูกกับ {บริษัท} สำหรับผู้ติดต่อ {ABC} แล้ว · รับเรื่อง: เอกสาร, ทวงชำระ · พิมพ์ #ByeNextAcc เพื่อยกเลิก"
```

**ความปลอดภัย (กันส่งเข้ากลุ่มผิด)**

| ภัย | ตัวกัน |
| --- | --- |
| วาง groupId ผิดกลุ่ม หรือคนนอกให้ groupId มา | **ต้องใช้รหัสผูก** ซึ่งเห็นได้เฉพาะคนที่อยู่ในกลุ่มนั้น · groupId ดิบอย่างเดียวผูกไม่ได้ (แก้ G9 · `LineDefaultGroupId` เดิมย้ายเป็นปลายทาง "กลุ่มทีม" ผ่านรหัสเดียวกัน §3.c-2) |
| ขโมยรหัส | อายุ 30 นาที · เก็บเฉพาะ SHA-256 · ใช้ครั้งเดียว · rate-limit ฝั่งผู้ใส่รหัส 10 ครั้งต่อชั่วโมงต่อผู้ใช้ · หน้ายืนยันแสดงชื่อและรูปกลุ่ม · บอทประกาศในกลุ่มทันทีเมื่อผูก (คนในกลุ่มเห็นและยกเลิกได้) |
| ผูกกลุ่มกับผู้ติดต่อผิดราย | หน้ายืนยันแสดงชื่อผู้ติดต่อตัวใหญ่ · ประกาศในกลุ่มระบุชื่อผู้ติดต่อ |
| สมาชิกกลุ่มเปลี่ยนไป | event `memberJoined`/`memberLeft` อัปเดต `MemberCountSnapshot` (ไม่เก็บ userId สมาชิก) · ทุก 30 วันบอทเตือนผู้ดูแลบริษัทให้ทบทวน (ตัวเลือก) |
| บอทถูกเตะออก | event `leave` ⇒ ทุกปลายทางที่ใช้ groupId + OA นั้น `UnlinkedAt=now`, เหตุ `bot-removed` · กระดิ่งถึงผู้ดูแลบริษัท · แถวคิวที่ค้างอยู่ ⇒ `Skipped` พร้อมเหตุ |
| ยกเลิกจากในกลุ่ม | `#ByeNextAcc` ⇒ ถ้ากลุ่มผูกกับบริษัทเดียว ปลดทันที · ถ้าหลายบริษัท ตอบเป็นรายการ "#ByeNextAcc 1" · หลังปลดบอทออกจากกลุ่ม (`POST /v2/bot/group/{id}/leave`) เมื่อไม่มีการผูกเหลือ (ค่าตั้งต้น · §9 Q8) |
| ข้อความอื่นในกลุ่ม | ไม่ประมวลและไม่เก็บ (เก็บเฉพาะ `webhookEventId` ไว้กันซ้ำ) |
| webhook ซ้ำ | ตาราง `LineWebhookEventSeen(webhookEventId PK, SeenAt)` ใช้ `INSERT … ON CONFLICT DO NOTHING` · ลบแถวที่เก่ากว่า 7 วัน (งานผ่าน `JobLock`) |

**3.c-2 กลุ่มทีมภายในของบริษัท** (แทน `LineDefaultGroupId`) — ใช้รหัสผูกเดียวกัน แต่ผูกที่ "ตั้งค่า › แจ้งเตือน › กลุ่ม LINE ของทีม" (`CompanyNotifyEndpoint`, ไม่มี ContactId) · ผ่าน OA ของบริษัทได้ตามเดิม · **ผ่าน OA กลางต้องรอเจ้าของตัดสิน** (§9 Q1b) เพราะเป็นกรณีที่ 232-Notify ห้ามไว้ตรง ๆ
Migration: ค่า `LineDefaultGroupId` เดิม ⇒ แถว `CompanyNotifyEndpoint(Kind=LineGroup, OaScope=company, Verified=false, Source=legacy)` ที่ส่งได้เหมือนเดิม (ไม่ถดถอย) และแสดงป้าย "ยังไม่ยืนยันด้วยรหัส — พิมพ์ #HeyNextAcc ในกลุ่มเพื่อยืนยัน"

**3.c-3 กลุ่มเดียวผูกกับหลายบริษัท** (เช่น สำนักงานบัญชีที่มีลูกค้าหลายราย หรือกลุ่มที่ลูกค้าคุยกับผู้ขายหลายเจ้า) — รองรับได้ เพราะแต่ละบริษัทผูกด้วยรหัสของตัวเอง (สร้างรหัสใหม่ทุกครั้งที่พิมพ์ `#HeyNextAcc`) และทุกข้อความมีป้าย "ในนาม {บริษัท}" · ระบบไม่เปิดเผยว่าบริษัทใดผูกกลุ่มนี้อยู่บ้าง (คำตอบ `#ByeNextAcc` แสดงเฉพาะชื่อบริษัท ซึ่งคนในกลุ่มเห็นจากข้อความอยู่แล้ว)

**3.c-4 ข้อจำกัดของ LINE ที่กระทบการออกแบบ** (§7.4) — หนึ่งกลุ่มเชิญ OA ได้ทีละหนึ่งบัญชี (ต้องยืนยัน) ⇒ บริษัทที่ใช้ OA ของตัวเองให้เชิญ OA ของตัวเองเข้ากลุ่ม แล้วพิมพ์ `#HeyNextAcc` ได้เหมือนกัน (เส้น webhook ของบริษัท) · OA ต้องเปิด "Allow bot to join group chats" ใน LINE Official Account Manager · หน้า `settings-channels` ตรวจ `LineOaGroupJoinVerified` จาก event `join` ครั้งแรก

### 3.d ตารางส่ง: ติ๊กช่องทางต่อกฎ

```
กฎ: ใบแจ้งหนี้ — เมื่ออนุมัติ — ส่งทันที (09:00)
 ช่องทาง                          สถานะ (เซิร์ฟเวอร์คำนวณ)
 ☑ อีเมล                           ✅ พร้อม · ผู้ส่ง: อีเมลบริษัท (ไม่ตัดโควตา)
 ☑ LINE ผู้ใช้ของผู้ติดต่อ          ✅ พร้อม · OA กลาง · ตัดโควตา LINE (เหลือ 820)
 ☑ กลุ่ม LINE ของผู้ติดต่อ          ⚠ ผู้ติดต่อ 41/60 รายยังไม่มีกลุ่มที่รับเรื่อง "เอกสาร"
 ☐ ถ้า LINE ส่งไม่ได้ ให้ส่งอีเมลแทน (เฉพาะใบที่ไม่ได้ติ๊กอีเมล)
 ผู้รับ: (•) ตามช่องทางแจ้งเตือนของผู้ติดต่อ (เรื่อง: เอกสาร)   ( ) ทับเฉพาะกฎนี้ [เลือกปลายทาง…]
 [ดูตัวอย่างการ์ด LINE]  [ดูตัวอย่างอีเมล]
```

- **ฟิลด์ใหม่บน `EmailScheduleRule`:** `ChannelEmail bool = true` (ค่าตั้งต้น = พฤติกรรมเดิม) · `ChannelLineUser bool = false` · `ChannelLineGroup bool = false` · `LineFallbackToEmail bool = false` · `RecipientMode ∈ {ContactEndpoints, Override}` = ContactEndpoints · `OverrideEndpointIdsJson` · `Topic` (ได้จาก trigger ผ่าน `NotifyTopicMap.ForTrigger` ถ้าไม่ตั้ง)
- **ความพร้อมคำนวณที่เซิร์ฟเวอร์:** `GET …/email-schedule/rules/{id}/channel-readiness` เรียก `ChannelReadinessPolicy.Evaluate(companyFacts, rule, contactCoverage, quota)` (pure) แล้วคืนผลต่อช่องเป็น `{ready, senderLabel, metered, quotaLeft, coverage:{withEndpoint,total}, reason}` · ช่องที่ "ไม่พร้อม" ยังติ๊กได้ แต่ต้องแสดงเหตุ เพราะความพร้อมเปลี่ยนตามผู้ติดต่อแต่ละราย
- **ด่านตอนบันทึก:** ถ้า `ChannelLineUser/Group` เปิดอยู่แต่บริษัทไม่มีช่อง LINE เลย (`ChannelSenderPolicy.Line == None`) ⇒ `BusinessRuleException` "ยังไม่มีช่อง LINE (ตั้ง OA ของบริษัท หรือรอผู้ดูแลเปิด OA กลาง)" พร้อมลิงก์ไปหน้าตั้งค่า · trigger `PayrollPaid` + `ChannelLineGroup` ⇒ ปฏิเสธ (สลิปเป็นข้อมูลส่วนบุคคล — ส่งได้เฉพาะ 1:1 ถึงพนักงานตามเส้นเดิม)
- **ใบกำกับภาษีเมื่อบริษัทใช้ e-Tax by Email (รอบ 244):** ติ๊ก LINE ได้ แต่ถ้าปิดอีเมล ⇒ คำเตือนที่ต้องกดรับทราบ "LINE เป็นการแจ้งเตือนพร้อมลิงก์ ไม่ใช่การส่งใบกำกับภาษีอิเล็กทรอนิกส์ — ใบกำกับยังต้องส่งทาง e-Tax by Email" และ `EtaxDeliveryRouter` (รอบ 244) ยังเป็นผู้ตัดสินเส้นอีเมลเหมือนเดิม

### 3.e ไปป์ไลน์การส่ง

```
event (อนุมัติ / ครบกำหนด / เกินกำหนด / ออก 50 ทวิ / รับชำระ→ใบเสร็จ / ขอเซ็น / ทวง 50 ทวิ)
   │
   ▼  EmailScheduleService.Enqueue* (ชื่อคงเดิม)
ContactRecipientResolver.ResolveAsync(companyId, contactId, topic, rule)    ← ตัวเดียว · tenant · ปลายทางที่ Active + topic ตรง + consent ยังมีผล
   → RecipientSet { Emails[], LineUsers[], LineGroups[] }   (ทับด้วย OverrideEndpointIds ถ้ากฎตั้งไว้)
   │
   ├─ ช่องอีเมล  → EmailQueue (เดิม · ToEmail = ปลายทาง Kind=Email หรือ Contact.Email เดิม)
   └─ ช่อง LINE  → LineQueue ต่อปลายทาง  IdempotencyKey = {rule}:{entity}:{endpoint}:{scheduledFor:yyyyMMddHH}
   │
   ▼  EmailScheduleWorker (เดิม) เพิ่มขั้น ProcessLineQueueAsync — SKIP LOCKED แบบเดียวกับ :432-447
 1. Recheck ตอนส่ง (ตัวเดิม RecheckDocumentAtSendAsync / RecheckWhtCertAtSendAsync) → ใบถูกยกเลิก/จ่ายครบ ⇒ Skipped + เหตุ
 2. ปลายทางยัง Active ไหม (unlink/block/bot-removed/consent ถอน) ⇒ ไม่ ⇒ Skipped + เหตุ
 3. ชั่วโมงเงียบ (LINE เท่านั้น · ค่าตั้งบริษัท 21:00–08:00) ⇒ เลื่อน ScheduledFor ไป 08:00 (ไม่ใช่ Failed)
 4. ChannelSenderPolicy.ForEndpoint(endpoint.OaScope, companyFacts) → CompanyOa | NextAccOa | Unavailable(เหตุ)
 5. NotificationAudiencePolicy.LineRoute(TenantBusiness, oa, TenantMember|ContactGroup, …) → Deny ⇒ Failed + RuleCode
 6. NextAccOa ⇒ NotifyQuotaService.TryReserveAsync(channel: line, units: 1 หรือ memberCount §5.3) → เต็ม ⇒ HeldQuota (+ ถอยไปอีเมลถ้าติ๊กไว้)
 7. LineDocumentCard.Build(doc, heading, links, showAmounts, brand)  (pure · ภาษาเดียวกับ PDF ผ่าน ResolveDocumentHeadingAsync)
 8. LineNotifyService.TryPushFlexAsync(oa, to, card, retryKey = LineQueue.RetryKey)   ← เมธอดใหม่ที่รับ OA ชัดเจน · ส่ง X-Line-Retry-Key
 9. ผล: 200 ⇒ Sent + LineRequestId + DocumentDeliveryLog(Channel=Line…) · ล้ม ⇒ คืนโควตา + จำแนก (ตารางด้านล่าง)
10. ล้มถาวรหรือล้มซ้ำครั้งที่ 3 ⇒ กระดิ่งถึงผู้สร้างกฎ/เจ้าของ (รวมเป็นหนึ่งข้อความต่อวันต่อกฎ)
```

**การจำแนกผลจาก LINE** (ตัวตัดสินเดียว `Helpers/LinePushOutcome` · pure · ทดสอบได้)

| LINE ตอบ | ความหมาย | การกระทำ |
| --- | --- | --- |
| 200 | ส่งถึงแล้ว | Sent |
| 409 พร้อม retry key เดิม | ส่งไปแล้วในครั้งก่อน | Sent (ไม่นับโควตาซ้ำ) |
| 400 "The user hasn't added the LINE Official Account as a friend" / ไม่พบผู้รับ | ผู้รับบล็อกหรือไม่ได้เป็นเพื่อน | Failed (ไม่ลองซ้ำ) + `BlockedAt` บนปลายทาง |
| 400 อื่น ๆ (payload ผิด) | บั๊กของเรา | Failed + LogError (ไม่ลองซ้ำ) |
| 401/403 | token เสียหรือถูกเพิกถอน | Failed + กระดิ่ง "ตั้งค่า LINE ใหม่" + ปิดความพร้อมของช่อง |
| 429 "monthly limit" | **OA ของบริษัท**ใช้ข้อความฟรีของ LINE หมด | HeldQuota (เหตุ `line-oa-monthly-limit`) + กระดิ่ง (ไม่ถอยไปใช้ OA กลาง เพราะ userId ผูกกับ OA) |
| 429 rate | ส่งถี่เกิน | Pending + backoff (1 นาที → 5 นาที → 30 นาที) |
| 5xx / timeout | ฝั่ง LINE ขัดข้อง | Pending + backoff ส่งซ้ำด้วย retry key เดิม (กันส่งซ้ำ) · ครบ 3 ครั้ง ⇒ Failed |

**ภาษา** — ตัวกลางเดียวกับ PDF (`ResolveDocumentHeadingAsync`) · ชื่อปุ่มและป้ายผ่าน `DocumentLabels` (ห้ามใช้ string literal ตาม §4 A)
**ส่งซ้ำเอง** — หน้าคิวมีปุ่มส่งซ้ำ (เดิม `RetryFailedAsync`) ซึ่งใช้ retry key ใหม่ เพราะผู้ใช้ตั้งใจส่งอีกครั้ง · แถว HeldQuota ส่งซ้ำได้หลังเติมโควตา (ลอก `CustomerNotifyResend.Evaluate` · อายุไม่เกิน 30 วัน · ใบยังอยู่ในสถานะที่ตรงกับเนื้อหา)

### 3.f การ์ด LINE (Flex) — ไม่มีไฟล์แนบ มีแต่ลิงก์

```
┌──────────────────────────────────────┐
│ ในนาม บริษัท ร้านเรา จำกัด  · ผ่าน NextAcc │ ← เฉพาะ OA กลาง (บังคับ · §6)
│ ใบแจ้งหนี้                                │
│ INV-2026-11-0042                         │
│ ยอดชำระ          ฿12,840.00   (ซ่อนได้)     │
│ ครบกำหนด 20/11/2569                       │  ← พ.ศ. เฉพาะเมื่อเอกสารเป็นภาษาไทย
│ ถึง: บริษัท เอบีซี จำกัด                    │
├──────────────────────────────────────┤
│ [ดูเอกสาร (PDF)]  ← ลิงก์ดูใบนี้ (token 30 วัน) │
│ [ชำระเงิน]       ← ลิงก์ชำระรอบ 241           │  ← เฉพาะใบที่มียอดค้างและเปิดรับชำระ
│ [เซ็นรับ/อนุมัติ] ← ลิงก์เซ็นรอบ 240           │  ← เฉพาะเมื่อมีคำขอเซ็นที่ยังเปิดอยู่
│ ไม่ต้องการรับ? พิมพ์ "หยุด"                    │  ← 1:1 · ในกลุ่มใช้ "#ByeNextAcc"
└──────────────────────────────────────┘
altText (≤ 400 ตัวอักษร): "ใบแจ้งหนี้ INV-… จาก {บริษัท} ครบกำหนด 20/11/2569"
```

- **ลิงก์ดูเอกสาร (ใหม่):** `DocumentViewLink` เก็บ token เป็น hash แบบเดียวกับลิงก์ชำระ · อายุ 30 วัน · เปิดได้ทั้งใบที่ไม่ใช่ใบเพื่อชำระ (ใบเสร็จ, 50 ทวิ) · `GET /api/public/doc-view/{token}` คืน PDF ด้วย renderer เดิม · ใบถูกยกเลิกแล้ว ⇒ แสดงแถบ "ยกเลิกแล้ว" · บันทึกการเปิดดู (หลักฐานว่าผู้รับเปิดแล้ว · ไม่ใช่หลักฐานการส่งตาม §1.2 G8)
- **ต่อสาย `DocumentLineDeliveryService`:** แยกเนื้อการ์ดเป็น pure builder `Helpers/LineDocumentCard` (ทดสอบได้) แล้วให้บริการนี้เรียก Resolver และ Dispatcher ตัวกลาง · ลบการอ่าน `Contact.LineUserId` (ตรงกับคำตัดสินข้อ 101 และคอมเมนต์ `:10-12`)
- **HtmlEncode ไม่จำเป็นใน Flex** (เป็น JSON) แต่ต้องตัดอักขระควบคุมและจำกัดความยาวด้วย `LineMessageLimits.Truncate` · URL ทุกตัวสร้างจาก `SystemBaseUrl.ResolveAsync` เท่านั้น
- **อีเมลแนบ PDF ได้ตามเดิม ส่วน LINE ไม่แนบ** — LINE Messaging API ไม่มีชนิดข้อความไฟล์สำหรับ push จากบอท จึงใช้ลิงก์แทน

### 3.g เส้นทางที่ใช้ปลายทางของผู้ติดต่อชุดเดียวกัน

| เส้น | หัวข้อ (`NotifyTopic`) | ทางเข้าปัจจุบัน | สิ่งที่เปลี่ยน |
| --- | --- | --- | --- |
| ส่งเอกสารตอนอนุมัติ/ใบประจำ | `Document` | `EmailScheduleService.OnDocumentApproved/OnRecurring…` | Resolver + LINE |
| ทวงชำระ (DueSoon/Overdue) + ใบแจ้งยอด | `Dunning` | `ScanDueSoonAndOverdueAsync` · `ScanMonthlyStatementsAsync` | Resolver + LINE (การ์ดใบแจ้งยอดแสดงยอดรวมและลิงก์พอร์ทัล) |
| ลิงก์ชำระ (รอบ 241) | ตามหัวข้อของเส้นหลัก | `DocumentPayLinkService` | ใส่ในการ์ดเป็นปุ่ม · คงหลัก "คิวเก็บที่หมาย ไม่เก็บ token" |
| ใบเสร็จอัตโนมัติหลังรับชำระ (รอบ 241) | `Receipt` | `PostPaymentAutomationService` | Resolver แทนการอ่าน `Contact.Email` ตรง ๆ + LINE |
| ลิงก์เซ็น (รอบ 240) | `Sign` | `SignRequestService.SendEmailAsync` | เพิ่มทาง LINE · ช่องผู้รับเดิม `SignLinkLastRecipients` ยังเป็นช่องที่ผู้ใช้กรอกเองได้ (ทับ) |
| ส่ง/ทวง 50 ทวิ (`AUTO_SEND_WHT_DESIGN` §4.3) | `Wht` | แผนรอบ 245 ของทีม WHT | ใช้ Resolver ตัวนี้ ห้ามมีตัวหาผู้รับของตัวเอง (ประสานงาน §8) |
| แจ้งเตือนอื่นถึงผู้ติดต่อ (เช่น สถานะการส่งของ หรือใบสั่งซื้อถึงผู้ขาย) | `Other` | — | ต่อสายภายหลัง โดยใช้ Resolver ตัวเดียวกัน |
| แจ้งเตือนภายในของบริษัท (NotificationEngine) | — | `NotificationEngine` | **ไม่ใช้** ปลายทางของผู้ติดต่อ (เป็นของพนักงาน) · กลุ่มทีมย้ายไป `CompanyNotifyEndpoint` |

`required_call_site_check` ล็อกว่าทุกเส้นในตารางนี้เรียก `ContactRecipientResolver.ResolveAsync` และห้ามอ่าน `Contact.Email`/`Contact.LineUserId` เพื่อหาผู้รับเอง (รายการไฟล์อยู่ใน §8)

---

## 4. โมเดลข้อมูล (ผ่าน `DatabaseMigrationHelper` · `CREATE TABLE/ADD COLUMN IF NOT EXISTS` · ห้าม EF Migrations)

### 4.1 `ContactNotifyEndpoint` (ใหม่ · TenantEntity)

| คอลัมน์ | ชนิด | หมายเหตุ |
| --- | --- | --- |
| `ContactId` | uuid FK | ต้องเป็นบริษัทเดียวกัน (ตรวจตอนเขียน + query มี `CompanyId`) |
| `Kind` | text | `Email` · `LineUser` · `LineGroup` (ตัวตั้ง `Helpers/NotifyEndpointKind`) |
| `Label` | text? | เช่น "ฝ่ายบัญชี", "กลุ่ม ABC–บัญชี" |
| `Email` | text? | Kind=Email เท่านั้น · ผ่าน `OnBehalfEmailSender.IsPlausibleEmail` |
| `PortalAccessId` | uuid? | ผู้ใช้พอร์ทัลที่ผูก (Kind=LineUser) |
| `LineUserId` | text? | **เข้ารหัสเก็บ** (`ISecretProtector`) · ห้าม log เต็ม |
| `LineGroupId` | text? | เข้ารหัสเก็บ |
| `LineIdHash` | text? | SHA-256 ของ (OaDestination + id) ใช้ค้นตอน webhook (unfollow/leave) โดยไม่ต้องถอดรหัสทุกแถว |
| `OaScope` / `OaDestination` | text / text? | `nextacc` · `company` + bot userId |
| `GroupNameSnapshot` / `MemberCountSnapshot` | text? / int? | จาก summary API ตอนผูกและจาก event สมาชิก |
| `TopicsJson` | text | ชื่อ enum `NotifyTopic` · ค่าตั้งต้น Email=ทุกหัวข้อ · LineUser=`["Document","Dunning","Receipt","Sign"]` · LineGroup=`["Document","Dunning"]` |
| `ShowAmounts` | bool | กลุ่ม: ค่าตั้งต้นตาม §9 Q5 · 1:1 = true |
| `IsPrimary` | bool | อีเมลหลักของผู้ติดต่อ (แทน `Contact.Email` ในเส้นส่ง) |
| `Verified` | bool | กลุ่มที่ผูกด้วยรหัส = true · แถว legacy = false |
| `Source` | text | `invite` · `qr` · `group-code` · `legacy-contact-email` · `legacy-line-userid` · `legacy-company-group` |
| `ConsentRecordId` | uuid? | 1:1 = บังคับ · กลุ่ม = หลักฐานการรับรองของผู้ดูแลบริษัท (`acceptResponsibility`) |
| `BoundByUserId` / `LinkedAt` | | |
| `BlockedAt` / `UnlinkedAt` / `UnlinkReason` | | `user` · `company` · `bot-removed` · `relinked` · `erase` · `consent-withdrawn` |
| `LastSentAt` / `LastFailure` | | สำหรับแสดงบนหน้าผู้ติดต่อ |

Index: `(CompanyId, ContactId) WHERE UnlinkedAt IS NULL` · `(LineIdHash)` · unique `(CompanyId, ContactId, Kind, LineIdHash) WHERE UnlinkedAt IS NULL AND IsDeleted=false` (ตามแนวทาง `SOFT_DELETE_UNIQUE_KEY_PLAN.md`)

**`Contact.Email` ยังเป็นค่าหลัก** — ไม่ลบคอลัมน์ · Resolver ใช้ `Contact.Email` เป็นปลายทางอีเมลโดยปริยายเมื่อผู้ติดต่อไม่มีแถว Kind=Email (ไม่ถดถอย) · หน้าแก้ไขผู้ติดต่อเขียน `Contact.Email` ตามเดิม (echo กลับครบตาม §4 A)

### 4.2 ตารางอื่น

| ตาราง | หน้าที่ |
| --- | --- |
| `CompanyNotifyEndpoint` | กลุ่มทีมของบริษัท (แทน `LineDefaultGroupId`) · โครงเดียวกันแต่ไม่มี ContactId |
| `LineGroupBindCode` | `CodeHash` · `GroupIdEnc` · `OaScope` · `OaDestination` · `CompanyIdHint` (null สำหรับ OA กลาง · = บริษัทสำหรับ OA ของบริษัท) · `GroupName` · `MemberCount` · `ExpiresAt` · `UsedAt` · `UsedByCompanyId` · `EndpointId` — ระดับแพลตฟอร์ม (OA กลางยังไม่รู้ว่าเป็นของบริษัทใด) จึงเป็น BaseEntity · ลบเมื่อหมดอายุเกิน 1 วัน |
| `ContactLinkInvite` | token hash · ContactId · PortalAccessId · ช่องที่ส่ง · ExpiresAt 7 วัน · UsedAt |
| `LineQueue` | ลอก `EmailQueue`: RuleId · EntityType/Id · `EndpointId` · `Topic` · `ScheduledFor` · `Status ∈ {Pending, Sending, Sent, Failed, Cancelled, Skipped, HeldQuota}` · RetryCount · `RetryKey` (uuid · X-Line-Retry-Key) · `QuotaKind`/`QuotaUnits` (ใช้คืนโควตา) · `OaUsed` · `LineRequestId` · ErrorMessage · `RuleCode` · IdempotencyKey (unique) — **ไม่ใส่ LINE id ในคิว** แต่อ้าง EndpointId |
| `DocumentDeliveryLog` (ใหม่ หรือเพิ่ม `Channel` ใน `DocumentEmailLog`) | **เลือกตารางใหม่** — `DocumentEmailLog` มี ToEmail/SenderMode ที่เป็นเรื่องอีเมล ถ้าใส่ LINE ลงไปจะเป็นการ reuse field ผิดความหมาย (§4 E) · `DocumentDeliveryEvidence` อ่านทั้งสองตาราง |
| `DocumentViewLink` | TokenHash · DocumentId · ExpiresAt · CreatedVia (`line`/`email`) · `LastOpenedAt` · OpenCount |
| `LineWebhookEventSeen` | webhookEventId PK · SeenAt (กันประมวลซ้ำ · หลายเครื่อง) |
| `NotifyUsageCounter` + `Channel` | §5.2 |
| `PortalAccess` + `Role` | `Customer` (ค่าตั้งต้นของแถวเดิม) · `Vendor` |
| `EmailScheduleRule` + ฟิลด์ §3.d | |
| `CompanySettings` + `LineQuietStartHour`=21 · `LineQuietEndHour`=8 · `LineChannelPreference ∈ {Auto, CompanyOa, NextAccOa}` · `LineOaGroupJoinVerified` | |
| `SiteSettings` + `NextAccOaTenantDeliveryEnabled` (สวิตช์หลักของแอดมิน · ค่าตั้งต้น false จนกว่าเจ้าของจะตอบ §9 Q1) | |

### 4.3 Migration ข้อมูลเดิม (F2 ข้อ 9)

| ข้อมูลเดิม | ทำอย่างไร | เหตุผล |
| --- | --- | --- |
| `Contact.LineUserId IS NOT NULL` | นับก่อน (`tools/` สคริปต์อ่านอย่างเดียว) · ย้ายเป็นแถว `Source=legacy-line-userid, Verified=false, OaScope=unknown`, **ไม่ใช้ส่ง** (Resolver ข้ามแถวที่ `OaScope=unknown`) · หน้าผู้ติดต่อแสดงป้าย "พบ LINE เดิมที่ไม่รู้ว่าผูกกับ OA ใด — [ส่งคำเชิญผูกใหม่]" · หลังย้ายแล้วล้างคอลัมน์ (คอลัมน์ยังอยู่จนลบในรอบถัดไป) | ไม่มีโค้ดเขียนคอลัมน์นี้ ค่าที่มีอยู่จึงมาจากการ import หรือการกรอกเอง และ userId ใช้ได้เฉพาะ OA ที่ออกค่านั้น ถ้าเดา OA แล้วส่งไป LINE จะตอบ 400 หรือส่งผิดคน |
| `CompanySettings.LineDefaultGroupId` | ⇒ `CompanyNotifyEndpoint(Source=legacy-company-group, OaScope=company, Verified=false)` ส่งได้ตามเดิม · migration ใช้ information_schema guard แบบ `NotificationLineGroupBackfill` (ครั้งเดียว) | ไม่ถดถอยกับบริษัทที่ใช้งานอยู่ |
| `Contact.Email` | **ไม่ย้าย** (Resolver ใช้เป็นค่าปริยาย) · สร้างแถว Kind=Email เฉพาะเมื่อผู้ใช้ตั้งหัวข้อหรือเพิ่มอีเมลที่สอง | ไม่สร้างข้อมูลซ้ำ 2 แหล่ง |
| `CustomerLineLink` (CMS) | ไม่ย้าย — เป็นของสมาชิกเว็บ/การจอง ไม่ใช่ผู้ติดต่อ · ถ้า SiteCustomer ผูกกับ Contact อยู่แล้ว Resolver อ่านเพิ่มได้ภายหลัง (L6) | ขอบเขตต่างกัน |
| `NotifyUsageCounter` (ก้อนเดียว) | แถวเดิมได้ `Channel='all'` · งวดใหม่บันทึกแยก `line`/`email` · ตัวตัดสินอ่านทั้งสองแบบ (§5.2) | งวดปัจจุบันไม่รีเซ็ตกลางเดือน |
| `EmailScheduleRule` เดิม | `ChannelEmail=true` อื่น ๆ false | พฤติกรรมเดิม |

---

## 5. โควตา

### 5.1 อะไรนับ อะไรไม่นับ (ต่อจาก `NotifyQuotaPolicy.IsMetered`)

| ข้อความ | นับ? |
| --- | --- |
| push ผ่าน OA กลางถึงผู้ใช้หรือกลุ่มของผู้ติดต่อ | **นับ** (โควตา LINE) |
| อีเมลผ่าน SMTP ของ NextAcc ในนามบริษัท — ตารางส่ง · ลิงก์เซ็น · ใบเสร็จอัตโนมัติ · อีเมลเอกสารที่กดส่งเอง | **นับ** (โควตาอีเมล) · ปัจจุบันยังไม่นับ (G5) ⇒ เปิดนับใน L4 |
| OA/SMTP ของบริษัทเอง | ไม่นับ (LINE หรือผู้ให้บริการอีเมลคิดเงินบริษัทเอง) |
| reply ของบอท (`#HeyNextAcc` · ผลการผูก · ผลยกเลิก) | ไม่นับ (LINE ไม่คิดเงินข้อความ reply) |
| ประกาศ "ผูกกลุ่มแล้ว" (push หนึ่งครั้ง) และข้อความแจ้งยกเลิกการผูก | ไม่นับ (ข้อความระบบ · NextAcc รับภาระ) |
| ข้อความแพลตฟอร์ม→เจ้าของบริษัท (โควตาใกล้เต็ม ฯลฯ) | ไม่นับ (`PlatformAccount`) |
| อีเมลเชิญผูก LINE | นับอีเมล (ตามแพตเทิร์น "อีเมลในนามบริษัท") — §9 Q4 |

**หน่วยของกลุ่ม:** LINE คิดเงิน push เข้ากลุ่ม**ตามจำนวนสมาชิกที่ได้รับ** (ต้องยืนยันกับเอกสารทางการ §7.4) ⇒ ค่าที่แนะนำคือ 1 push เข้ากลุ่ม = `max(1, memberCount − 1)` หน่วย (ไม่นับบอท) · อ่าน `members/count` ตอนส่ง และเก็บใน `LineQueue.QuotaUnits` · ทางเลือกคือนับหนึ่งหน่วยต่อกลุ่ม (§9 Q2)

### 5.2 ตัวนับและเพดาน

- `NotifyUsageCounter` เพิ่มคอลัมน์ `Channel` (`all` สำหรับแถวเดิม · `line` · `email`) · PK/unique เปลี่ยนเป็น `(CompanyId, Period, Channel)` (migration: เพิ่มคอลัมน์ default 'all' แล้วสร้าง unique index ใหม่ ก่อนลบ index เดิม)
- เพดานใหม่บน `PlanTemplate`/`Subscription`: `MaxSharedLineMessagesPerMonth` · `MaxSharedEmailsPerMonth` (+ Trial) · null ⇒ ใช้ `MaxSharedMessagesPerMonth` เดิมเป็นก้อนรวม (`NotifyQuotaPolicy.PlanLimitFor(channel, …)` pure)
- โบนัสแยกต่อช่อง: `NotifyBonusLine*`/`NotifyBonusEmail*` · แถวข้อเสนอ `notify.topup.line` · `notify.topup.email` (ขนาดแพ็ก ราคา อายุ เช่นเดียวกับ `notify.topup`) · `notify.topup` เดิมยังขายได้ในฐานะแพ็กรวม จนกว่าแอดมินจะปิด
- **เพดานรายวันของอีเมล (`PlatformEmailQuota`) ยังคงไว้** เพื่อกันการใช้ในทางที่ผิดและรักษาชื่อเสียงของโดเมน · เพดานรายเดือนเป็นเรื่องงบประมาณ · ต้องผ่านทั้งสองด่าน

### 5.3 เมื่อชนเพดาน

1. จองโควตาไม่ได้ ⇒ แถวคิว `HeldQuota` (รหัส `NotifySuppressReason.QuotaFull` เดิม) · ไม่ทิ้งงาน
2. ถ้ากฎติ๊ก `LineFallbackToEmail` และมีอีเมลของผู้ติดต่อ ⇒ เข้าคิวอีเมลแทน (ถ้าอีเมลก็ใช้ช่องกลาง ⇒ ต้องจองโควตาอีเมล)
3. แจ้งเจ้าของบริษัทที่ 80% และ 100% ครั้งเดียวต่องวดต่อช่อง (กลไกเดิม) ผ่านกระดิ่งและ LINE 1:1 ของเจ้าของผ่าน OA กลาง (`PlatformAccount` · ไม่เข้ากลุ่มกลาง)
4. หน้า "ข้อความที่ค้างเพราะโควตา" (ต่อจากรอบ 213) แสดงแถว HeldQuota ทั้ง LINE และอีเมล + [ซื้อเพิ่มแล้วส่งที่ค้าง]
5. ทวงชำระที่ค้างนานกว่า 7 วันไม่ส่งย้อนหลัง (เนื้อหาล้าสมัย) ⇒ `Skipped` เหตุ `stale` · กติกาอยู่ใน `CustomerNotifyResend.Evaluate`
6. **ไม่มีข้อความจำเป็น (essential) ในเส้นนี้** — เอกสารและทวงชำระไม่ใช่เรื่องความปลอดภัย จึงถูกระงับเมื่อโควตาเต็ม

### 5.4 แอดมินแพลตฟอร์ม (`admin-quota.html` · `AdminNotifyQuotaController`)

```
แพ็กเกจ        LINE/เดือน   อีเมล/เดือน   ทดลองใช้ LINE/อีเมล
Basic          [ 300 ]      [ 1,000 ]      [100]/[200]      (คำแนะนำ: NotifyQuotaPolicy.SuggestedLimit)
Pro            [1,000]      [ 5,000 ]
บริษัท ABC (ทับ): LINE [ 3,000 ] เหตุผล: [ลูกค้าองค์กร ตกลง 2026-11]  ← บังคับใส่เหตุผล · audit chain
ข้อเสนอแพ็กเพิ่ม: LINE 1,000 ข้อความ ฿390 อายุ 60 วัน · อีเมล 5,000 ฉบับ ฿190
สวิตช์: [✓] เปิดให้ผู้เช่าส่งถึงผู้ติดต่อผ่าน OA กลาง   [✓] เปิดกลุ่ม LINE ผ่าน OA กลาง
มิเตอร์ต้นทุน: ข้อความ OA กลางเดือนนี้ 48,200 / โควตาของ OA ที่ LINE ให้ 50,000 (GET /v2/bot/message/quota + /quota/consumption)
```

- **เพิ่มด่านต้นทุน:** งาน `NextAccOaQuotaWatch` (`JobLock` · ทุกชั่วโมง) อ่าน quota/consumption ของ OA กลาง ถ้าเกิน 90% ⇒ แจ้งกลุ่มของผู้ดูแล (`PlatformOps`) · ถ้าเกิน 100% ⇒ LINE ตอบ 429 และแถวกลายเป็น HeldQuota (เหตุ `nextacc-oa-limit`) โดยไม่ตัดโควตาของผู้เช่า
- การซื้อเพิ่มใช้ `QuotaAddOnPurchaseService` เดิม (ออกใบ · ชำระผ่าน gateway · WHT ของค่าบริการแพลตฟอร์มตามรอบ 235) — เพิ่มแค่ ResourceKey

---

## 6. นโยบาย/กฎหมาย

### 6.1 คำตัดสิน 232-Notify กับคำขอนี้

232-Notify แก้ปัญหา "เหตุการณ์ธุรกิจของผู้เช่าเด้งเข้า**ห้องของผู้ดูแลแพลตฟอร์ม**" และถือหลักว่า "OA กลางแจ้งเฉพาะเรื่องระบบ" · แต่ก็ยอมให้ "1:1 ถึงลูกค้าของบริษัทที่ผูกกับบอทกลางเอง" (`RuleTenantMemberViaNextAcc`) อยู่แล้ว
คำขอวันนี้ต้องเพิ่มปลายทางใหม่คือ **กลุ่มของผู้ติดต่อที่ผูกด้วยรหัส** ซึ่งไม่ใช่ห้องของแพลตฟอร์ม · ข้อเสนอที่แคบที่สุดมีดังนี้

```csharp
public enum LineTargetKind { CompanyGroup, TenantMember, PlatformGroup, ContactGroup /* ใหม่ */ }
// TenantBusiness + NextAccOa + ContactGroup ⇒ Ok("NOTIFY-TENANT-CONTACT-GROUP-NEXTACC")
//   เฉพาะเมื่อ: endpoint.Verified (ผูกด้วยรหัส) && endpoint.CompanyId == companyId && SiteSettings.NextAccOaTenantDeliveryEnabled
//   && topic ∈ ContactFacingTopics (Document/Dunning/Receipt/Sign/Wht) — ไม่ใช่แจ้งเตือนภายใน
// TenantBusiness + NextAccOa + CompanyGroup ⇒ Deny เหมือนเดิม (กลุ่มทีม — รอ §9 Q1b)
// PlatformGroup ⇒ Deny ทุกกรณีของ TenantBusiness (เดิม)
```
`LineRoute` ต้องรับ `verifiedBinding` เพิ่ม และล็อกจุดเรียกให้ `ChannelDispatcher` เรียกตัวนี้ทุกครั้ง

### 6.2 ข้อความในนามบริษัทผ่าน OA กลาง

- ทุกการ์ดผ่าน OA กลางต้องมีบรรทัด "ในนาม {ชื่อบริษัท} · ผ่าน NextAcc" (ชื่อบริษัทผ่าน `OnBehalfEmailSender.SafeDisplayName`) ทั้งใน altText และในการ์ด · ตัวประกอบเดียว `LineDocumentCard.Brand`
- ข้อความใน OA กลางถึงผู้ติดต่อเป็น**ข้อความธุรกรรม** (เอกสารที่เขามีส่วนเกี่ยวข้อง) ไม่ใช่การตลาด · **ห้าม** broadcast/narrowcast/ข้อความโปรโมชันของผู้เช่าผ่าน OA กลาง (ไม่มี API ให้ทำ)
- การยกเลิก: 1:1 พิมพ์ "หยุด" (ยกเลิกเฉพาะบริษัทนั้น) หรือ "หยุดทั้งหมด" (ทุกบริษัทบน OA กลาง) · กลุ่มพิมพ์ `#ByeNextAcc`
- คำตอบเมื่อผู้รับพิมพ์ข้อความอื่นถึง OA กลาง: "ข้อความนี้ไม่ถึง {บริษัท} — ติดต่อ {บริษัท} ได้ที่ {อีเมล/โทร}" (reply · ไม่นับโควตา) · ห้ามผูกคนนอกเข้ากับบอทบัญชีของ ERP
- ป้าย `NotificationAudiencePolicy.NoCompanyOaMessage` ต้องแก้ข้อความ (ปัจจุบันบอกว่า "ไม่ส่งผ่าน LINE OA กลาง") ให้แยก "แจ้งเตือนภายใน" ออกจาก "ส่งถึงผู้ติดต่อ"

### 6.3 PDPA

| ประเด็น | ออกแบบ |
| --- | --- |
| ฐานกฎหมาย | การส่งเอกสารตามสัญญาเป็นการปฏิบัติตามสัญญา (ม.24(3)) · แต่การผูก LINE ส่วนตัวเป็น**ช่องทางที่เจ้าของข้อมูลเลือกเอง** ⇒ บันทึกความยินยอมต่อปลายทาง (`PdpaConsentRecord` · purpose `contact-line-notify` · version · evidenceHash) เพื่อพิสูจน์การเลือกและการถอน |
| ความยินยอมต่อปลายทาง | 1:1 = ผู้รับกดยินยอมเองบนหน้าเชิญ · กลุ่ม = ผู้ดูแลบริษัทรับรองว่าสมาชิกกลุ่มมีสิทธิ์เห็นเอกสารของผู้ติดต่อนั้น (`acceptResponsibility` + ข้อความ version) และประกาศในกลุ่มคือการแจ้งสมาชิก (ม.23) |
| ถอนความยินยอม | "หยุด" หรือปุ่มบนพอร์ทัล ⇒ `UnlinkedAt` + `ConsentRecord.WithdrawnAt` · ผลภายในไม่กี่วินาที (Resolver อ่านทุกครั้ง · ไม่มี cache) |
| ส่งให้น้อยที่สุด | ไม่ส่งเลขผู้เสียภาษี เลขบัตร ที่อยู่ หรือรายการสินค้าทาง LINE · กลุ่มซ่อนยอดได้ (`ShowAmounts`) · 50 ทวิ ส่งแค่ "มีหนังสือรับรองใหม่ + ลิงก์" · สลิปเงินเดือนห้ามเข้ากลุ่ม |
| เข้ารหัส | LINE userId และ groupId เข้ารหัสเก็บ (ไม่ใช่ข้อมูลอ่อนไหวตาม ม.26 แต่เป็นตัวระบุตัวบุคคล) · log ใช้ `Mask` |
| DSR | access/portability รวมปลายทางของผู้ติดต่อ · erase ⇒ ปลดทุกปลายทาง + ล้าง id (แบบ `CustomerLineLinkClosure`) · log การส่งเก็บแค่ EndpointId ไม่มี id ของ LINE |
| retention | `LineQueue`/`DocumentDeliveryLog` เก็บตามอายุเอกสาร (หลักฐานการส่งเอกสารบัญชี — MAX(retention) · `RETENTION_LAW_CHECK.md`) · `LineGroupBindCode` ลบภายใน 1 วันหลังหมดอายุ · `LineWebhookEventSeen` 7 วัน |
| ผู้ประมวลผลข้อมูล | LINE เป็นผู้รับข้อมูลรายใหม่ ⇒ เพิ่มใน RoPA (`ProcessingActivity` · recipients="LY Corporation (LINE)") และอยู่ในหนังสือแจ้งของหน้าเชิญ |

### 6.4 อื่น ๆ

- **e-Tax by Email (รอบ 244):** LINE ไม่ใช่ช่องทาง e-Tax · `DocumentDeliveryEvidence` นับ LINE เป็นหลักฐาน "ส่งให้ลูกค้าแล้ว" สำหรับด่านยกเลิกใบ แต่**ไม่**นับเป็น "ส่ง e-Tax แล้ว" (ตัวตัดสินคนละตัว)
- **§86/4:** ลิงก์ดูเอกสารเปิด PDF จาก renderer เดิม · ส่งเฉพาะใบที่อนุมัติแล้ว (คิวตรวจซ้ำตอนส่ง) ไม่ส่งใบที่เลขยังเป็น `DRAFT-`

---

## 7. API · งาน · webhook · หน้าจอ

### 7.1 Endpoints (ทุกตัว `[Authorize]` + permission + `CompanyId == companyId` · ยกเว้นที่ระบุว่า public)

| Method | Path | สิทธิ์ |
| --- | --- | --- |
| GET | `/api/companies/{cid}/contacts/{id}/notify-endpoints` | contacts.view — คืนรายการพร้อมป้ายสถานะที่เซิร์ฟเวอร์ประกอบ (`EndpointStatusLabel`) |
| POST | `…/notify-endpoints/email` · PUT `…/{eid}` (หัวข้อ · label · ShowAmounts) · DELETE `…/{eid}` | contacts.notify.manage |
| POST | `…/notify-endpoints/invite` {name,email,role,topics,channel} ⇒ PortalAccess + ContactLinkInvite | contacts.notify.manage |
| POST | `…/notify-endpoints/line-user/qr` ⇒ รหัส LN + URL (ทางลัด · §9 Q6) | contacts.notify.manage |
| POST | `…/notify-endpoints/line-group/preview` {code, groupId?} · `…/line-group/confirm` | contacts.notify.manage (Owner/Accountant) · rate-limit |
| POST | `/api/companies/{cid}/notify-endpoints/team-group/confirm` | settings.notifications |
| GET | `/api/companies/{cid}/email-schedule/rules/{id}/channel-readiness` | |
| GET | `/api/companies/{cid}/channels/status` (อีเมล/LINE · ผู้ส่ง · โควตาต่อช่อง) | |
| GET/POST | `/api/companies/{cid}/line-queue` · `…/{id}/retry` · `…/{id}/cancel` · `…/held/resend` | |
| public | GET `/api/public/contact-line-join/{token}` · POST `…/consent` (ออกรหัส LN) | token |
| public | GET `/api/public/doc-view/{token}` | token |
| admin | `AdminNotifyQuotaController` + ช่อง line/email · `AdminLineMessagingController` + สวิตช์ OA กลางสำหรับผู้เช่า + มิเตอร์ต้นทุน | SystemAdmin |

### 7.2 Webhook (ทั้ง `LineWebhookController` และ `CustomerLineWebhookController`)

แยกตัวจัดการ `LineGroupEventHandler` ที่ทั้งสองคอนโทรลเลอร์เรียก (สมมาตร · F2 ข้อ 1) และรับ `(scope, companyId?, destination)`:

| event | source | การกระทำ |
| --- | --- | --- |
| (ทุก event) | — | `webhookEventId` ⇒ `LineWebhookEventSeen` ON CONFLICT ⇒ ซ้ำแล้วข้าม |
| `join` | group | reply คำแนะนำ · ตั้ง `LineOaGroupJoinVerified` (เฉพาะ OA ของบริษัท) |
| `message` text | group/room | `LineGroupCommandParser`: `#HeyNextAcc` ⇒ ออกรหัส + reply · `#ByeNextAcc [n]` ⇒ ปลด · อื่น ๆ ⇒ ไม่ทำอะไร **และไม่เก็บ** · room (แชตหลายคนแบบเก่า) ⇒ ตอบว่ารองรับเฉพาะกลุ่ม |
| `leave` | group | ปลดทุกปลายทาง (หาโดย `LineIdHash` + OaDestination) · กระดิ่งบริษัท · แถวคิวค้าง ⇒ Skipped |
| `memberJoined`/`memberLeft` | group | อัปเดต `MemberCountSnapshot` (ไม่เก็บ userId) |
| `follow`/`unfollow` | user | เส้นเดิม `SetBlockedAsync` + ขยายไปที่ `ContactNotifyEndpoint` |
| `message` "ผูก LN-…" | user | `TryRedeemAsync` เดิม + kind `contact-person` |
| `message` "หยุด…" | user | ปลดปลายทาง 1:1 ของ userId นั้น (ทุกบริษัท หรือบริษัทที่ระบุ) |

- ลายเซ็น: OA กลางใช้ secret กลาง · OA บริษัทใช้ secret ของบริษัทจาก URL (`LineWebhookSignature.Verify` ตัวเดียว) · **multi-tenant บน OA กลาง:** event ในกลุ่มไม่ได้บอกบริษัท ⇒ รหัสผูกไม่ผูกกับบริษัทตอนออก บริษัทที่ใส่รหัสเป็นผู้อ้างสิทธิ์ · `leave` ปลดทุกบริษัทที่ผูก groupId นั้นกับ OA นั้น
- ตอบด้วย reply token เสมอ (ฟรี) · ถ้า reply token หมดอายุ ไม่ถอยไปใช้ push (เพราะเสียเงินและอาจส่งเข้ากลุ่มที่ยังไม่ผูก)

### 7.3 งานเบื้องหลัง (ทุกตัวผ่าน `Helpers/JobLock` · ไม่มี static cache)

| งาน | รอบ | หมายเหตุ |
| --- | --- | --- |
| `EmailScheduleWorker` + `ProcessLineQueueAsync` | ทุก 1 นาที (เดิม) | SKIP LOCKED |
| `NextAccOaQuotaWatch` | ทุกชั่วโมง | §5.4 |
| `LineBindingHousekeeping` | ทุกคืน | ลบรหัสที่หมดอายุ · ลบ event ที่เก่ากว่า 7 วัน · ลิงก์ดูเอกสารที่หมดอายุ |
| `ContactRelinkInvite` (ขยายของเดิม) | เมื่อบริษัทเปลี่ยน OA | เชิญผู้รับ 1:1 ให้ผูกใหม่ทางอีเมล (นับโควตาอีเมลเมื่อใช้ช่องกลาง) |

### 7.4 ข้อจำกัดของแพลตฟอร์ม LINE (ต้องยืนยันกับเอกสารทางการก่อน L3)

> ⚠ เซสชันนี้เข้า `developers.line.biz` ไม่ได้ (เครือข่ายของเซสชันไม่อนุญาตโฮสต์นี้) · ข้อมูลด้านล่างมาจากความรู้ของทีมและผลค้นจากแหล่งรองที่อ้างไว้ ·
> **ต้องให้คนเปิดเอกสารทางการตรวจทุกข้อที่ติด ⚠ ก่อน merge L3** (หลัก DECISION_DOCTRINE: ห้ามแต่งค่ากติกาภายนอก)

| ข้อ | สิ่งที่ทีมเข้าใจ | ผลต่อการออกแบบ | สถานะ |
| --- | --- | --- | --- |
| push ส่งได้ถึง user/group/room | ยืนยันจากผลค้น (คู่มือ group chats และ SDK) | ใช้ push เข้ากลุ่มได้ | ✅ (แหล่งรอง) |
| `groupId` อยู่ใน `source` ของ event | ยืนยันจากผลค้น | ใช้เป็นตัวผูก | ✅ |
| `join`/`leave`/`memberJoined`/`memberLeft` | ยืนยันจากเอกสาร SDK | §7.2 | ✅ (แหล่งรอง) |
| `GET /v2/bot/group/{id}/members/count` · summary · `POST …/leave` | ยืนยันจาก SDK | §3.c | ✅ (แหล่งรอง) |
| `GET /v2/bot/message/quota` + `/quota/consumption` | ยืนยันจาก SDK | §5.4 | ✅ (แหล่งรอง) |
| ข้อความ reply ไม่คิดเงิน/ไม่นับโควตา · push นับ | ความรู้ของทีม | ตอบ `#HeyNextAcc` ด้วย reply | ⚠ |
| push เข้ากลุ่มนับตามจำนวนผู้รับในกลุ่ม | ความรู้ของทีม | §5.1 หน่วยของกลุ่ม | ⚠ |
| หนึ่งกลุ่มมี OA ได้ทีละหนึ่งบัญชี · OA ต้องเปิด "Allow bot to join group chats" | ความรู้ของทีม | §3.c-4 | ⚠ |
| push ถึง user ได้เฉพาะคนที่เพิ่มเพื่อนและไม่ได้บล็อก | ความรู้ของทีม | ผูกด้วย "เพิ่มเพื่อน + ส่งรหัส" | ⚠ |
| header `X-Line-Retry-Key` (UUID) กันส่งซ้ำเมื่อลองใหม่ · ส่งซ้ำด้วย key เดิมได้ 409 | ความรู้ของทีม | §3.e | ⚠ |
| กลุ่มมีสมาชิกได้สูงสุด 500 คน | ความรู้ของทีม | ปฏิเสธการผูกกลุ่มที่ memberCount > 200 (ค่าตั้งของแอดมิน) เพื่อกันต้นทุน | ⚠ |
| ราคา OA ไทย: ฟรี 300 ข้อความ/เดือน · Basic ฿1,280 = 15,000 ข้อความ (ไม่รวม VAT) | แหล่งรอง (SleekFlow) ไม่ตรงกับแหล่งอื่นที่บอก 500 | แอดมินใช้ตั้งราคาแพ็ก | ⚠ ต้องเปิด LINE OA Manager ของ NextAcc ดูราคาจริง |
| userId/groupId เป็นค่าต่อ provider | ยืนยันแล้วในโค้ดและคำตัดสินข้อ 170 | §2 ข้อ 3 | ✅ |
| ข้อตกลงการใช้ OA — การใช้ OA เดียวส่งข้อความในนามหลายธุรกิจ | **ไม่ทราบ** | อาจกระทบโมเดล OA กลางทั้งหมด (รวมรอบ 211/212 ที่ใช้อยู่แล้ว) | ⚠ ต้องให้ฝ่ายกฎหมายอ่าน LINE Official Account Terms of Use |

### 7.5 หน้าจอ

**ก. ผู้ติดต่อ › แท็บ "ช่องทางแจ้งเตือน"** (`contacts.html` ปัจจุบันเป็น modal ฟอร์มเดียว ⇒ เพิ่มแท็บ "ข้อมูลทั่วไป | ช่องทางแจ้งเตือน")

```
ABC จำกัด — ช่องทางแจ้งเตือน
┌ อีเมล ─────────────────────────────────────────────────────────────┐
│ ★ ap@abc.co.th (อีเมลหลักของผู้ติดต่อ)  เรื่อง: ทั้งหมด                │
│   somying@abc.co.th  "บัญชี"  เรื่อง: เอกสาร ทวงชำระ ใบเสร็จ   [แก้][ลบ] │
│ [+ เพิ่มอีเมล]                                                       │
├ ผู้ใช้ LINE ─────────────────────────────────────────────────────────┤
│ 🟢 คุณสมหญิง (ลูกค้า · พอร์ทัล) · OA: NextAcc · ผูก 01/11 · ส่งล่าสุด 05/11 │
│    เรื่อง: ☑เอกสาร ☑ทวงชำระ ☑ใบเสร็จ ☐50 ทวิ ☑ลิงก์เซ็น ☐อื่น ๆ  [ยกเลิก] │
│ 🟡 คุณวิชัย — รอผูก (เชิญเมื่อ 04/11 · หมดอายุ 11/11) [ส่งคำเชิญอีกครั้ง]  │
│ 🔴 LINE เดิมที่ไม่รู้ว่าผูกกับ OA ใด — [ส่งคำเชิญผูกใหม่]                    │
│ [+ เชิญผู้ใช้ให้ผูก LINE]  [QR ผูกทันที]                                  │
├ กลุ่ม LINE ──────────────────────────────────────────────────────────┤
│ 🟢 "ABC × ร้านเรา – บัญชี" (12 คน) · OA: NextAcc · ผูกโดย คุณเอ 02/11     │
│    เรื่อง: ☑เอกสาร ☑ทวงชำระ  แสดงยอดเงิน: [✓]                 [ยกเลิก]   │
│ 🔴 "ABC – จัดซื้อ" — บอทถูกนำออกจากกลุ่มเมื่อ 03/11                         │
│ [+ เพิ่มกลุ่ม LINE]                                                      │
└──────────────────────────────────────────────────────────────────────┘
ป้ายช่อง (เซิร์ฟเวอร์ประกอบ): "LINE ของบริษัทใช้ OA กลางของ NextAcc · ตัดโควตา LINE (เหลือ 820)"
```

**ข. หน้าต่าง "เพิ่มกลุ่ม LINE"**

```
ขั้น 1  เชิญ @nextacc เข้ากลุ่ม LINE   [QR เพิ่มเพื่อน] [คัดลอก @id]
        ⓘ ถ้าเชิญไม่ได้ ให้ตรวจว่ากลุ่มยังไม่มี OA อื่นอยู่
ขั้น 2  พิมพ์ #HeyNextAcc ในกลุ่ม
ขั้น 3  ใส่รหัสที่บอทตอบ:  [ GB-________ ]   (วาง Group ID ด้วยได้ แต่ต้องมีรหัสเสมอ)   [ตรวจ]
        ┌───────────────────────────────────────────────┐
        │ [รูป] "ABC × ร้านเรา – บัญชี" · 12 คน · OA NextAcc │
        │ ผูกกับผู้ติดต่อ: **ABC จำกัด**                    │
        │ เรื่องที่ส่ง: ☑เอกสาร ☑ทวงชำระ ☐ใบเสร็จ ☐50 ทวิ   │
        │ แสดงยอดเงินในกลุ่ม: [✓]                         │
        │ ☑ ฉันยืนยันว่าสมาชิกทุกคนในกลุ่มนี้มีสิทธิ์เห็นเอกสารของ ABC │
        └───────────────────────────────────────────────┘
        [ยืนยันผูกกลุ่ม]  → บอทประกาศในกลุ่มทันที
ข้อผิดพลาด (เซิร์ฟเวอร์ประกอบ): รหัสหมดอายุ — พิมพ์ #HeyNextAcc ใหม่ · รหัสนี้มาจาก OA อื่น (บริษัทใช้ OA ของตัวเอง — เชิญ OA ของบริษัทแทน) · ใช้ไปแล้ว
```

**ค. ตารางส่ง** — §3.d · ช่องติ๊กสามช่อง + ป้ายความพร้อม + ตัวเลือกผู้รับ · หน้าคิวมีตัวกรอง "ช่องทาง" และสถานะ `ค้างเพราะโควตา` · ไทม์ไลน์ในหน้าเอกสาร: "05/11 09:00 อีเมล ✅ ap@abc… · LINE ✅ คุณสมหญิง · กลุ่ม ⏸ ค้างโควตา"

**ง. มิเตอร์โควตาและซื้อเพิ่ม** — การ์ดบนแดชบอร์ดและหน้าตั้งค่าช่องทาง: สองแถบ (LINE/อีเมล) · ปุ่ม [ซื้อเพิ่ม] เปิดหน้าซื้อ add-on เดิมโดยเลือก resource ไว้ให้ · ข้อความ 80/100% มาจาก `NotifyQuotaStatus`

**จ. หน้าเชิญ (public)** `wwwroot/pages/contact-line-join.html` — mobile-first · 3 ขั้น (บัญชี → ยินยอม → เพิ่มเพื่อนและส่งรหัส) · แสดงชื่อบริษัทผู้เชิญและ "ผ่าน NextAcc"

---

## 8. แผนลงมือ (ต่อจากรอบ 244)

> **การพึ่งพา:** L2 และ L4 แตะ `EmailScheduleService` · `DocumentEmailService` · `PostPaymentAutomationService` ซึ่งรอบ 244 (e-Tax by Email · `EtaxDeliveryRouter`) กำลังแก้ ⇒ **เริ่มหลังรอบ 244 merge** · ส่วน L1/L3 ไม่แตะไฟล์ชุดนั้น จึงเริ่มขนานได้
> แผน WHT (`AUTO_SEND_WHT_DESIGN.md` เฟส 245 ทวง 50 ทวิ) ต้องใช้ `ContactRecipientResolver` ⇒ **L1 ต้องเสร็จก่อนเฟส 245 ของ WHT** หรือให้ WHT ใช้ `Contact.Email` ชั่วคราวผ่าน Resolver รุ่นอีเมลอย่างเดียว

### L1 — ปลายทางของผู้ติดต่อ + Resolver (อีเมลอย่างเดียว · ไม่มี LINE)

- ตาราง `ContactNotifyEndpoint` · `NotifyTopic` · `ContactRecipientResolver` (pure core `ContactRecipientRule` + service) · แท็บ "ช่องทางแจ้งเตือน" (ส่วนอีเมล) · migration `Contact.LineUserId` → แถว legacy (ไม่ใช้ส่ง) + สคริปต์นับ
- ต่อสาย Resolver เข้า: `EmailScheduleService.Enqueue*` · `PostPaymentAutomationService` · `SignRequestService` (ค่าตั้งต้น ช่องที่ผู้ใช้กรอกยังชนะ) — ✅ **รอบ 250 WP-08** (ตารางส่ง/ทวงชำระ/ใบแจ้งยอด/50 ทวิ/ใบเสร็จหลังรับชำระ/ลิงก์เซ็น/ความพร้อมรายการประจำ · ยกเลิกรับ ⇒ แถวคิว "ข้าม" พร้อมเหตุ · LINE ยังปิดจน L2 · ล็อก R250L1 · ดู DOCUMENT_FLOW §2.12h) — commit 31479320
- **เทสต์:** `ContactRecipientRuleTests` — (ก) ผู้ติดต่อไม่มีแถว ⇒ ได้ `Contact.Email` เหมือนเดิม (**ทิศตรงข้าม: ไม่ถดถอย**) (ข) แถวหัวข้อ Dunning ไม่ได้รับเอกสาร Receipt (ค) แถวที่ unlink/consent ถอน ⇒ ไม่ได้ (ง) แถว legacy `OaScope=unknown` ⇒ ไม่ได้ (จ) ผู้ติดต่อของบริษัทอื่น ⇒ ไม่ได้ (tenant) · Db test: ตารางส่ง DueSoon ส่งถึงอีเมลที่สองของผู้ติดต่อ
- **required_call_site_check:** `EmailScheduleService.EnqueueDocumentAsync` · `ScanDueSoonAndOverdueAsync` · `ScanMonthlyStatementsAsync` · `PostPaymentAutomationService.*Send*` · `SignRequestService.SendEmailAsync` ต้องเรียก `ContactRecipientResolver.ResolveAsync` · ห้ามมี `.Contact.Email` / `.Contact!.Email` ใช้เป็นผู้รับในไฟล์เหล่านี้

### L2 — คิว LINE + Flex + ลิงก์ดูเอกสาร + ติ๊กช่องทาง (1:1 ผ่าน OA ของบริษัทก่อน)

> ✅ **รอบ 254 WP-11 L2 (บางส่วน — ดูรายการยังไม่ทำ)**: `LineQueue` · `ContactLineChannel` (แทน `ChannelSenderPolicy`/`ChannelReadinessPolicy` — ความพร้อมต่อปลายทางอยู่ที่
> `ContactRecipientRule.LineGate` + `CompanyLineReadiness`) · `LinePushOutcome` · `LineDocumentCard` · ฟิลด์กฎ §3.d (`LineFallbackToEmail` ค่าตั้งต้น **true** ตาม Q7) ·
> `TryPushContactAsync(…, retryKey)` · หลักฐานการส่งนับ LINE (ใช้ `LineQueues` Sent แทนตาราง `DocumentDeliveryLog` ใหม่) · ลิงก์/QR ผูก LINE ผ่าน OA บริษัท (Q6 ทางลัด — ดึงจาก L5) ·
> **ยังไม่ทำ**: `DocumentViewLink` (การ์ดมีปุ่มชำระเมื่อมีลิงก์ชำระเท่านั้น) · ชั่วโมงเงียบ · `DocumentLineDeliveryService` ยังไม่ต่อสาย (ผู้ส่งใหม่คือ `ContactLineQueueService`) ·
> หน้า readiness ต่อกฎ (`channel-readiness`) · กระดิ่งเมื่อล้มซ้ำ · ทาง OA กลาง (L4) · DECISIONS 254-WP11-1…6

- `LineQueue` · `ChannelSenderPolicy` · `ChannelReadinessPolicy` · `LinePushOutcome` · `LineDocumentCard` · `DocumentViewLink` · ฟิลด์กฎ §3.d · `LineNotifyService.TryPushFlexAsync(oa, …, retryKey)` · `DocumentDeliveryLog` + `DocumentDeliveryEvidence` นับ LINE · ต่อสาย `DocumentLineDeliveryService` (หรือลบแล้วใช้ dispatcher — เลือกต่อสายตามคำตัดสินข้อ 101)
- **เทสต์:** การ์ดไม่มีเลขผู้เสียภาษี · altText ≤ 400 · ภาษา en/th ตรงกับ PDF · `LinePushOutcomeTests` ครบทุกแถวของตาราง §3.e · retry ใช้ key เดิม (ส่งซ้ำไม่เกิดสองข้อความ) · **ทิศตรงข้าม:** กฎเดิม (`ChannelEmail` อย่างเดียว) ไม่สร้างแถว LineQueue · ใบยกเลิกหลังเข้าคิว ⇒ Skipped · ชั่วโมงเงียบเลื่อนเวลา ไม่ใช่ Failed · `PayrollPaid` + กลุ่ม ⇒ บันทึกกฎไม่ได้
- **required_call_site_check:** `ProcessLineQueueAsync` ต้องเรียกตามลำดับ Recheck → `ChannelSenderPolicy` → `NotificationAudiencePolicy.LineRoute` → (ถ้า NextAcc) `TryReserveAsync` → push · ห้ามเรียก `PushCoreAsync`/HTTP ตรง · ปุ่มในการ์ดต้องมาจาก `DocumentPayLinkPolicy` / `DocumentViewLink` / ลิงก์เซ็น เท่านั้น

### L3 — กลุ่ม LINE + `#HeyNextAcc` + webhook

- `LineGroupEventHandler` (เรียกจากทั้งสอง webhook) · `LineGroupCommandParser` · `LineGroupBindCode` · `LineWebhookEventSeen` · preview/confirm · ประกาศในกลุ่ม · leave/unbind · `CompanyNotifyEndpoint` + migration `LineDefaultGroupId` · `ContactGroup` ใน `NotificationAudiencePolicy` (เปิดผ่าน OA กลางก็ต่อเมื่อเจ้าของตอบ §9 Q1 และแอดมินเปิดสวิตช์)
- **เทสต์:** parser รับ `#HeyNextAcc`/`#heynextacc`/`＃HeyNextAcc` และไม่รับ `HeyNextAcc` ที่ไม่มี # หรือ `#HeyNextAccX` · ข้อความอื่นในกลุ่มไม่ถูกเก็บ (ตรวจว่าไม่มีแถวใหม่) · รหัสใช้ครั้งเดียวแม้มีสองคำขอพร้อมกัน (Db test) · รหัสของ OA กลางใส่ในบริษัทที่ใช้ OA ของตัวเอง ⇒ ปฏิเสธพร้อมทางไปต่อ · groupId โดยไม่มีรหัส ⇒ ปฏิเสธ · `leave` ปลดทุกบริษัทที่ผูก · webhook ซ้ำไม่ออกรหัสใหม่ · **ทิศตรงข้าม:** `TenantBusiness` + OA กลาง + `CompanyGroup` ยัง Deny · `PlatformGroup` ยัง Deny · แถว legacy ของกลุ่มบริษัทยังส่งได้ (ไม่ถดถอย 232 B1)
- **required_call_site_check:** `LineWebhookController.Receive` และ `CustomerLineWebhookController.Receive` ต้องเรียก `LineGroupEventHandler.HandleAsync` และ `LineWebhookEventSeen` ก่อนประมวล · ห้ามมี `continue // ignore group` หลงเหลือ

### L4 — โควตาแยกช่อง + อีเมลตามตารางตัดโควตา + ซื้อเพิ่ม + แอดมิน

- `Channel` ใน `NotifyUsageCounter` · เพดานต่อช่อง · `notify.topup.line/email` · ต่อ `TryReserveAsync` ในเส้นอีเมลช่องกลางทุกเส้น (ตารางส่ง · ส่งเอง · ลิงก์เซ็น · ใบเสร็จอัตโนมัติ) · HeldQuota + ส่งค้างหลังซื้อ · `NextAccOaQuotaWatch` · หน้าแอดมิน
- **เทสต์:** `NotifyQuotaPolicy.PlanLimitFor` (ต่อช่อง null ⇒ ใช้ก้อนรวม) · งวดปัจจุบันที่มีแถว `all` ไม่รีเซ็ต · กลุ่ม 12 คนจอง 11 หน่วย (ตามคำตอบ Q2) และส่งล้มคืน 11 หน่วย · **ทิศตรงข้าม:** SMTP/OA ของบริษัทไม่ตัดโควตา · ข้อความ reply ไม่ตัด · โควตาเต็มไม่บล็อกการอนุมัติเอกสาร · เพดานรายวันยังทำงานคู่กับรายเดือน
- **required_call_site_check:** ทุกจุดที่ `plan.OnBehalf == true` แล้วส่งอีเมล ต้องเรียก `NotifyQuotaService.TryReserveAsync(channel: email)` ก่อน `SendAsync`

### L5 — เชิญผู้ใช้ของผู้ติดต่อ + พอร์ทัล (ลูกค้า/ผู้ขาย) + ความยินยอม

- `PortalAccess.Role` · `ContactLinkInvite` · หน้า `contact-line-join.html` · kind `contact-person` ใน `CustomerLineLinkRule` (+ `SiteId` nullable) · ยกเลิกด้วย "หยุด" · DSR/RoPA · relink เมื่อเปลี่ยน OA
- **เทสต์:** คำเชิญหมดอายุหรือใช้แล้ว · ผูกโดยไม่ยินยอม ⇒ ไม่มีปลายทาง · "หยุด" ปลดเฉพาะบริษัทนั้น · erase ล้าง id · ผู้ใช้พอร์ทัลบทบาท Vendor ไม่เห็นใบแจ้งหนี้ขาย · **ทิศตรงข้าม:** รหัส `ผูก 123456` ของ ERP ยังผูกบัญชีพนักงานได้ (ไม่ชนกับ LN)

### L6 — ขยายเส้นที่เหลือ

- ทวง 50 ทวิ (ร่วมกับทีม WHT) · การแจ้งเตือนอื่นถึงผู้ติดต่อ · อ่าน `CustomerLineLink` ของ SiteCustomer ที่ผูกกับ Contact · ล้างคอลัมน์ `Contact.LineUserId` (หลังยืนยันว่าไม่มีผู้อ่าน)

### เอกสารที่ต้องขยับในแต่ละเฟส

`DOCUMENT_FLOW.md` (ช่องทางส่งเอกสาร) · `ACCOUNT_STRUCTURE.md` (โควตาต่อช่อง · add-on · ค่าตั้ง OA) · `TEST_PLAN.md` · `docs/lessons/` (ถ้าพบ defect class ใหม่) · DECISIONS (บันทึกคำตอบ §9)

---

## 9. คำถามถึงเจ้าของ (เฉพาะเรื่องที่เป็นการตัดสินใจทางธุรกิจ)

| # | คำถาม | ข้อเสนอของทีม |
| --- | --- | --- |
| Q1 | (a) อนุญาตให้ส่งเอกสาร**ถึงผู้ติดต่อ** (1:1 และกลุ่มที่ผูกด้วยรหัส) ผ่าน OA กลางของ NextAcc ในนามบริษัทได้ใช่ไหม (เป็นข้อยกเว้นของ 232-Notify สำหรับเรื่องที่ผู้ติดต่อเห็น) (b) **กลุ่มทีมภายใน**ของบริษัทที่ไม่มี OA ของตัวเอง จะให้รับแจ้งเตือนภายในผ่าน OA กลางด้วยไหม | (a) ใช่ ตาม §6.1 (b) ยังไม่เปิด (ตรงกับคำพูดเดิม "OA กลางแจ้งเฉพาะเรื่องระบบ") |
| Q2 | ส่งเข้ากลุ่มหนึ่งครั้งนับโควตากี่หน่วย: 1 หน่วย หรือ ตามจำนวนสมาชิก | ตามจำนวนสมาชิก (ตรงกับต้นทุนที่ LINE เรียกเก็บ) + จำกัดขนาดกลุ่มที่ผูกได้ไม่เกิน 200 คน |
| Q3 | โควตาแยก LINE กับอีเมล หรือคงก้อนรวมเดิม | แยก แต่ถ้าแอดมินไม่ตั้งค่าต่อช่อง ให้ใช้ก้อนรวมเดิม |
| Q4 | อีเมลตามตารางและอีเมลเชิญที่ส่งผ่าน SMTP ของ NextAcc เริ่มตัดโควตารายเดือนด้วยไหม (วันนี้ยังไม่ตัด) และตั้งค่าเพดานอีเมลตั้งต้นต่อแพ็กเกจเท่าไร | ตัด · ตั้งค่าตั้งต้นให้สูงพอที่ลูกค้าปัจจุบันไม่สะดุด (วัดยอดใช้จริงจาก `DocumentEmailLogs` ก่อน) |
| Q5 | ในกลุ่ม LINE จะแสดงยอดเงินเป็นค่าตั้งต้นไหม | แสดง (กลุ่มที่ผูกกับผู้ติดต่อเป็นกลุ่มที่คุยกับลูกค้ารายนั้น) แต่ปิดได้ต่อกลุ่ม |
| Q6 | ผู้รับ 1:1 **ต้อง**สมัครเป็นผู้ใช้พอร์ทัลก่อนเสมอ หรือให้ทางลัด "สแกน QR ผูกทันที" โดยไม่ต้องสมัคร | ให้มีทั้งสองแบบ · QR ต้องกดยินยอมบนมือถือของผู้รับ |
| Q7 | โควตาเต็มแล้ว ถ้าผู้ติดต่อมีอีเมล ให้ส่งอีเมลแทน LINE อัตโนมัติไหม | ไม่อัตโนมัติ · ให้ติ๊กต่อกฎ (`LineFallbackToEmail`) |
| Q8 | เมื่อปลดการผูกกลุ่มครบแล้ว ให้บอทออกจากกลุ่มเองไหม | ออก (ลดต้นทุนและความสับสน) |
| Q9 | ราคาแพ็ก LINE ที่ขายเพิ่ม (ต้นทุน OA กลางของ NextAcc ต่อข้อความ) | ให้เจ้าของเปิด LINE OA Manager ดูแผนจริงของ @nextacc แล้วตั้งราคา — ตัวเลขในผลค้นขัดกันเอง (§7.4) |
| Q10 | ให้ฝ่ายกฎหมายตรวจ LINE Official Account Terms ว่าใช้ OA เดียวส่งในนามหลายบริษัทได้ไหม (กระทบรอบ 211/212 ที่ใช้อยู่แล้วด้วย) | ตรวจก่อนเปิดสวิตช์ OA กลางสำหรับกลุ่ม |

---

## ภาคผนวก — แหล่งอ้างอิงภายนอก (แหล่งรอง · ต้องยืนยันกับเอกสารทางการ)

- คู่มือ group chats ของ LINE (ผลค้น · เปิดตรงไม่ได้จากเซสชันนี้): https://developers.line.biz/en/docs/messaging-api/group-chats/
- Messaging API pricing (ผลค้น): https://developers.line.biz/en/docs/messaging-api/pricing/
- SDK ที่ระบุ endpoint ของกลุ่มและ quota: https://pypi.org/project/line-bot-sdk/1.20.0/ · https://metacpan.org/pod/release/BEDOSHI/LINE-Bot-API-1.20/lib/LINE/Bot/API.pm · https://github.com/WayneChang65/linebot
- ราคา OA ไทย (แหล่งรอง · ตัวเลขขัดกับแหล่งอื่น): https://sleekflow.io/blog/line-business-account-set-up · https://www.salesmartly.com/en/blog/docs/line-official-account-price · https://thestandard.co/?p=273801

## 10. คำตอบเจ้าของ (2026-11-06) — ตอบ §9

| # | คำตัดสิน |
| --- | --- |
| Q1 | (a) **ได้** — ส่งเอกสารถึงผู้ติดต่อ (1:1 และกลุ่มที่ผูกด้วยรหัส) ผ่าน OA กลางของ NextAcc ในนามบริษัท แสดงข้อมูลผู้ส่งให้ชัดเจน (b) **ห้าม**ส่งเข้า groupId กลางของระบบที่ตั้งไว้ที่หน้า admin — กลุ่มกลางรับเฉพาะเรื่องระบบ (ไม่งั้นทุกบริษัทแจ้งเข้ามามากเกิน) |
| Q2 | นับตามจำนวนสมาชิกในกลุ่ม · จำกัดกลุ่มที่ผูกได้ ≤ 200 คน |
| Q3 | โควตา LINE กับอีเมล**แยกกัน**ตั้งแต่ออกแบบ (ไม่ใช้ก้อนรวม) |
| Q4 | อีเมลผ่านระบบหักโควตารายเดือน · เพดานตั้งต้นสูงพอให้ลูกค้าปัจจุบันไม่สะดุด (วัดจาก `DocumentEmailLogs` ก่อนตั้ง) |
| Q5 | แสดงยอดเงินในกลุ่มเป็นค่าตั้งต้น · ปิดได้รายกลุ่ม |
| Q6 | มีทั้งสองทาง: สมัครก่อน หรือสแกน QR ผูกทันทีโดยกดยินยอมบนมือถือ |
| Q7 | ติ๊กเลือกเป็นรายกฎ · **ค่าตั้งต้น = อัตโนมัติ** (โควตา LINE เต็ม ⇒ ส่งอีเมลแทน) · และตั้งให้ส่ง**ทั้งสองทาง**พร้อมกันได้ |
| Q8 | ปลดการผูกครบ ⇒ บอทออกจากกลุ่มเอง |
| Q9 | ตั้งทีมวิเคราะห์ราคาแพ็ก LINE/อีเมล (ต้นทุนจริง) — ผลที่ `erp-review/2026-11-06/NOTIFY_PRICING_ANALYSIS.md` |
| Q10 | ตั้งทีมตรวจข้อตกลง LINE OA อย่างละเอียด — ผลที่ `erp-review/2026-11-06/LINE_OA_TERMS_CHECK.md` |

## 11. เงื่อนไข C1–C10 (LINE_OA_TERMS_CHECK §5) → จุดในโค้ด (รอบ 254 WP-11 ข้อ 1)

| เงื่อนไข | จุดในโค้ด ณ รอบ 254 | สถานะ |
| --- | --- | --- |
| C1 NextAcc คุม OA กลางผู้เดียว | token/secret OA กลางอยู่ที่ `INextAccLineConfig` เท่านั้น · ผู้เช่าไม่มีทางเข้า · เส้นผู้ติดต่อ (`TryPushContactAsync`) ไม่อ่าน token กลางเลย (R254 forbid) | ✅ |
| C2 เนื้อหาเป็นแม่แบบที่ระบบประกอบ | `Helpers/LineDocumentCard` (ป้าย `DocumentLabels` · ไม่มีข้อความอิสระของผู้เช่า) · `Helpers/LineOnBehalfText` สำหรับข้อความ text ผ่าน OA กลาง | ✅ การ์ดเอกสาร · ⏳ ชนิดอื่น (L6) |
| C3 ป้ายผู้ส่งชัด | การ์ด: "จาก {บริษัท}" (OA บริษัท) / "ในนาม {บริษัท} · ผ่าน NextAcc" (OA กลาง) ทั้งการ์ดและ altText · `BookingNotifier` ผ่าน OA กลาง = `LineOnBehalfText.ForNextAcc` | ✅ |
| C4 ผู้รับเลือกรับเอง | ลิงก์/QR ผูก: กดยินยอม (`PdpaConsentRecord` hash) ก่อนได้รหัส → ผู้รับส่งรหัสเอง (`ContactLineBindingService`) · ปลายทางไม่มีความยินยอม = ไม่ส่ง (`ContactRecipientRule.Status`) | ✅ 1:1 · ⏳ กลุ่ม (L3) |
| C5 ยกเลิกง่าย | การ์ดทุกใบมีบรรทัดยกเลิก · "หยุด"/stop ปลด + ถอนยินยอม (`TryStopAsync`) · unfollow ⇒ บล็อก · "หยุดทั้งหมด"/`#ByeNextAcc`/ปุ่มพอร์ทัล = L3/L5 | ✅ บางส่วน |
| C6 ไม่เปิดเผย LINE id แก่ผู้เช่า | คิว/หน้าคิว/ภาพรวมไม่มี LINE id (R254 forbid ใน `GetRecentAsync`/`EnqueueLineAsync`) · id เข้ารหัส · log ปิดบัง | ✅ |
| C7 จำกัดความถี่ข้ามบริษัทบน OA กลาง | ยังไม่มีเส้นผู้ติดต่อผ่าน OA กลาง (N8) | ⏳ L4 |
| C8 คัดกรองผู้เช่าก่อนใช้ OA กลาง | ไม่มีเส้นผ่าน OA กลาง | ⏳ L4 |
| C9 ข้อตกลงกับผู้เช่า | เงื่อนไขบริการ (Y6 · เจ้าของกดเผยแพร่) | ⏳ เจ้าของ |
| C10 กลุ่ม ≤ 200 · ไม่มีสลิปในกลุ่ม · ซ่อนยอด | `ContactRecipientRule.MaxGroupMembers` · สลิป ⇒ `ContactLineChannel.RuleProblem` ปฏิเสธ · `ShowAmounts` ⇒ การ์ดซ่อนยอด · กลุ่มกลางของแอดมินกันสองชั้น | ✅ ตัวตัดสิน · ⏳ ผูกกลุ่ม (L3) |

