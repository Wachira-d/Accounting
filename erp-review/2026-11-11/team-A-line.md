# ทีม A — LINE OA ครบวงจร (อ่านอย่างเดียว) · 2026-11-11 · HEAD 81521510

ขอบเขต: ตั้งค่า → ผูก → ส่งออก → รับเข้า → ส่งอัตโนมัติ → UX · ทุกข้อเปิดโค้ดตรงจุดแล้ว (path จาก `Accounting/`) · ยังไม่ได้คอมไพล์/รันในเครื่องนี้ (ไม่มี .NET SDK)

**ผลรวม: พร้อมแบบมีเงื่อนไข — ต้องแก้ H1–H3 ก่อนเปิดให้ลูกค้าจริง** · แกนคิว/ผูก/กลุ่ม/ลายเซ็นแน่น ช่องโหว่อยู่ที่ "ทางเข้ารอบข้าง" (สิทธิ์ · หน้าซ้ำ · ทางผูกที่สอง)

## 1. ของรอบ 257 ที่ตรวจซ้ำแล้วว่าแก้จริง
| ข้อเดิม | หลักฐาน |
| --- | --- |
| C-H1 OA กลางถูกใส่เป็น OA ร้าน | `Controllers/LineConfigController.cs:72-86` (`LineOwnOaGuard.Refusal`/`CentralValueRefusal`) · หน้าเว็บซ่อนส่วนขั้นสูง `wwwroot/pages/settings.html:2183-2186` |
| C-M1 ล้างช่องไม่ได้ | `LineConfigController.cs:92-106` ("" = ล้าง) · ปุ่มล้าง `settings.html:2262-2276` |
| C-M3 อีเมลซ้ำเมื่อ e-Tax บังคับ | `Services/Implementations/EmailScheduleService.cs:1011,1123,1155-1159` |
| C-M4 รหัส GB ของ OA กลาง | `Helpers/LineGroupBindRule.cs:86-91` (`CentralCode`) |
| C-M5/M6 ข้อความขัดกัน | `Helpers/ContactLineChannel.cs:64-75` · `settings.html:1090-1092` |
| C-M2 แพ็กที่ใช้ไม่ได้ | `Services/Implementations/NotifyQuotaService.cs:530,613` |
| E-H1 การ์ด go-live ไม่มี LINE | `Helpers/PlatformGoLiveChecklist.cs:116-127` |
| A-M8 สลิปถูกปฏิเสธส่งซ้ำไม่ได้ | `Helpers/SlipEvidenceRule.cs:92-96` |

ยังเปิดอยู่จากรอบก่อน (LOW เดิม): ไม่บอกว่า LINE เรียก webhook แล้วหรือยัง (→ M5) · ช่อง U…/C… ที่หาในแอปไม่ได้ (→ M6)

## 2. ข้อค้นพบ

### HIGH
**H1 — ตารางส่ง (กฎ + คิวอีเมล) ไม่มีด่านสิทธิ์เลย** · `Controllers/EmailScheduleController.cs:68-102` (PUT rules) · `:104-109` (DELETE) · `:130-136` (retry) · `:160-165` (cancel) มีแค่ `[Authorize]` — ขณะที่คิว LINE ในไฟล์เดียวกันมีด่าน `CompanySettings.Edit` (`:37-42,145,154`) · บริการก็ไม่ตรวจ (`EmailScheduleService.cs:1325`) · ไม่มีตัวกั้นบทบาทกลาง (`Program.cs:905-913` มีแค่ TenantGuard + ApiKeyScope)
- เส้นทางจริง: สมาชิกบทบาท Viewer/Staff หรือ API key ที่มีสิทธิ์เขียน ยิง PUT rules ใส่ `bccEmails` เป็นอีเมลตัวเอง ⇒ ใบแจ้งหนี้/ใบกำกับ PDF ทุกใบที่อนุมัติถูกสำเนาออกนอกบริษัท (`EmailScheduleService.cs:1075`) · หรือแก้เนื้ออีเมล/ติ๊กช่อง LINE/ลบกฎทวงหนี้ · คลาสเดียวกับ A-H1 รอบ 257 ที่แก้เฉพาะรายการประจำ
- แก้: `[RequirePermission(CompanySettingsEdit)]` + `[RejectApiKey]` บน PUT/DELETE rules และ retry/cancel คิวอีเมล (ใช้ `RequireSettingsAsync` ตัวเดิม) · เพิ่มใน `tools/required_call_site_check.py` · เทสต์ทิศตรงข้าม: ผู้มีสิทธิ์ยังบันทึกได้ · GET คงเดิม

**H2 — ผูก LINE พนักงานด้วยการพิมพ์ userId ดิบ (ทางผูกที่สอง ข้ามทุกด่าน)** · `Controllers/NotificationConfigController.cs:70-77` → `Services/Implementations/NotificationConfigService.cs:150-160` เขียน `User.LineUserId` ตรง ๆ · หน้า `wwwroot/pages/notifications.html:60-80,283-292`
- `User.LineUserId` คือ**กุญแจยืนยันตัวของบอทกลาง** (`Services/Implementations/LineBotService.cs:227,445,796` — สร้างเอกสาร · อนุมัติ · ดูยอด) · ทางผูกด้วยรหัส (`LineBotService.cs:176-222`) มีจำกัดความถี่ + ถอดบัญชีอื่นที่ถือค่าเดิม + audit + อีเมลแจ้งเจ้าของ — ทางนี้ไม่มีสักอย่าง
- ผล: (ก) ใครได้ session ของเหยื่อชั่วครู่ ตั้ง LINE ของตัวเองเป็นของเหยื่อ ⇒ สั่งบอทในนามเหยื่อถาวรโดยไม่มีอีเมลเตือน/ร่องรอย (ข) ค่าซ้ำสองบัญชี ⇒ `FirstOrDefaultAsync` หยิบบัญชีไหนก็ได้ (บั๊ก E-07 ที่ทางผูกด้วยรหัสปิดไปแล้ว กลับมา) (ค) คำแนะนำบนหน้า (`notifications.html:71`) ให้เอา userId จาก "LINE OA ของบริษัท" แต่ผู้ส่งจริงคือ OA กลาง (`Services/Implementations/NotificationEngine.cs:397`) — userId ต่าง provider ใช้ไม่ได้ ⇒ ส่งไม่ถึงเงียบ (`:399` LogWarning)
- แก้: ลบ PUT `line-binding/me` (คง GET/DELETE · DELETE เพิ่ม audit) · แท็บ LINE ของ notifications.html แสดงสถานะ + ปุ่มไป `line-bind.html` · migration: ล้าง `Users.LineUserId` ที่ซ้ำกันหลายบัญชี (เก็บแถวที่มีแถว `LineBindCodes.UsedByLineUserId` ตรง) + บันทึก audit

**H3 — หน้าแจ้งเตือนซ้ำสองที่ และที่หนึ่งล้างค่าของอีกที่** · `wwwroot/pages/settings.html:1187-1199,2314-2371` (แท็บ "การแจ้งเตือน") กับ `wwwroot/pages/notifications.html:123-205` ตั้งตารางเดียวกัน · settings.html ส่งแถวไม่มี `enableLineGroup` (`:2354-2357`) ⇒ DTO ตั้งต้น false (`Models/DTOs/Notification/NotificationConfigDtos.cs:20`) ⇒ `NotificationConfigService.cs:61` เขียน false ทับ
- เส้นทางจริง: ผู้ดูแลติ๊ก "กลุ่ม LINE ของบริษัท" ที่ notifications.html → วันหลังเปิดตั้งค่า › การแจ้งเตือน แล้วกดบันทึก ⇒ ประกาศเข้ากลุ่มทีมหายทุกเหตุการณ์ ไม่มีอะไรบอก · ป้ายบทบาทเป็นสำเนาใน JS สองชุดคำไม่ตรงกัน (`settings.html:2322` vs `notifications.html:91-94`) · PUT `config/settings` ไม่มีด่านสิทธิ์ (`NotificationConfigController.cs:34-41`)
- แก้: ถอดตารางออกจาก settings.html เหลือลิงก์ไป notifications.html (หน้าเดียว) · ฝั่งเซิร์ฟเวอร์ `EnableLineGroup` เป็น `bool?` (null = คงเดิม) กันผู้เรียกเก่า · ใส่ `RequirePermission(CompanySettingsEdit)` ที่ PUT

### MED
**M1 — รายการประจำ: การ์ด LINE ซ้ำ + เตือนผิด** (ก) `EmailScheduleService.cs:314-319` ด่าน "กฎเมื่ออนุมัติครอบแล้ว" ดูเฉพาะ `EmailQueues` ⇒ กฎเมื่ออนุมัติที่ติ๊ก LINE อย่างเดียว (แถวอยู่ใน `LineQueues`) ไม่ถูกนับ ⇒ กฎ "รายการประจำสร้างเอกสาร" เดินต่อ ⇒ ลูกค้าได้การ์ด LINE สองใบ (BaseKey ต่างกัน `:157` vs `:338`) หรือ LINE + อีเมล (ข) `Services/Implementations/RecurringTransactionService.cs:1083-1088` เตือน "ไม่มีอีเมล" + ตั้งธง `EmailSkippedNoContactEmail` แม้ LINE เข้าคิวสำเร็จ (ค) หน้าความพร้อมของรายการประจำไม่รู้จัก LINE เลย (`Services/Implementations/RecurringAutopilotService.cs:440-447` · `wwwroot/pages/recurring.html` ไม่มีคำว่า LINE) ⇒ เหตุที่ LINE ไม่ออกเห็นได้ที่คิวหน้าตารางส่งเท่านั้น
- แก้: `approvedQueued` รวม `LineQueues` (BaseKey = approvedKey · Status ≠ Skipped) · ข้อ (ข) ข้ามเมื่อ `enqueueOutcome` เข้าคิวแล้ว · เพิ่มบรรทัด "ช่องทางที่จะส่ง: อีเมล/LINE (ปลายทาง n) หรือเหตุ" ในการ์ดความพร้อม (เซิร์ฟเวอร์คำนวณจาก `ContactLineChannel.Plan`)

**M2 — บอทกลางตอบด้วย push (เสียโควตา) และไม่จำกัดคนแปลกหน้า** · `Controllers/LineWebhookController.cs:107-108` → `LineBotService.cs:1017-1023` (`TryPushViaNextAccAsync`) ทั้งที่ event มี replyToken (กลุ่มใช้ reply อยู่แล้ว `:146`) · ผู้ที่ยังไม่ผูกได้คำตอบทุกข้อความ (`LineBotService.cs:227-230`) ไม่มีตัวจำกัด
- เส้นทางจริง: ใครก็ได้เพิ่มเพื่อน OA กลางแล้วส่งข้อความรัว ๆ ⇒ โควตา push รายเดือนของ OA กลางหมด ⇒ แจ้งเตือนจอง/คำสั่งซื้อ/บิลของทุกบริษัทหยุด
- แก้: ตอบด้วย `ICustomerLineLinkService.ReplyAsync(ScopeNextAcc, null, replyToken, …)` ก่อน ถอยไป push เฉพาะเมื่อ reply ล้ม · คนที่ยังไม่ผูก จำกัดผ่าน `IChatRateLimiter` (เช่น 3/นาที · 20/วัน)

**M3 — webhook กลาง 1:1 ไม่กัน event ซ้ำ** · `LineWebhookController.cs:75-108` ไม่เรียก `MarkEventSeenAsync` (เส้นกลุ่ม `:138` และ webhook บริษัท `Controllers/CustomerLineWebhookController.cs:79` เรียก) · งานทำในคำขอ (OCR `LineBotService.cs:603` · สร้าง+อนุมัติ `:359-384`) ใช้เวลาหลายวินาที
- เส้นทางจริง: เปิด Webhook redelivery ที่ OA กลาง + คำขอช้า ⇒ "จ่าย เซเว่น 250" ลงค่าใช้จ่ายอนุมัติแล้วสองใบ · รูปบิลมีกันซ้ำ 10 นาที (`:533-550`) แต่สองคำขอพร้อมกันยังหลุดได้
- แก้: ใช้ `MarkEventSeenAsync(webhookEventId)` กับ event 1:1 ด้วย (ตารางเดิม) · ถอนประทับเมื่อล้มแบบเส้นกลุ่ม

**M4 — เรื่องที่ติ๊กได้บนปลายทาง LINE แต่ไม่มีวันส่ง** · ค่าตั้งต้น LINE ผู้ใช้ = เอกสาร · ทวง · **ใบเสร็จ · ลิงก์เซ็น** (`Helpers/NotifyEndpointKind.cs:131`) แต่ LINE ส่งได้ 4 trigger (`Helpers/ContactLineChannel.cs:29-32`) · งานหลังรับชำระไม่มี LINE (`Services/Implementations/PostPaymentAutomationService.cs` ไม่มีเส้น LINE) · หน้า `wwwroot/pages/contacts.html:1229,1360` แสดงทุกเรื่องให้ติ๊ก · คำตอบของบอท "เรื่องที่ส่ง: …" (`LineGroupBindRule.cs:94-96`) ก็อ้างเรื่องที่ไม่ส่ง
- เส้นทางจริง: ลูกค้าที่มีแต่ LINE (ไม่มีอีเมล) ได้ใบแจ้งหนี้ทาง LINE → จ่ายผ่านลิงก์ → ไม่ได้ใบเสร็จทางไหนเลย ทั้งที่ปลายทางติ๊ก "ใบเสร็จ" · DOCUMENT_FLOW จดเป็น "ยังไม่ทำ (L6)" แต่หน้าจอไม่บอก
- แก้ขั้นต่ำ: `NotifyTopics.LineSendable` ตัวเดียว ⇒ เซิร์ฟเวอร์ส่งธงต่อเรื่อง · หน้าล็อกช่อง + "(ยังไม่ส่งทาง LINE)" · ถอดออกจากค่าตั้งต้น/ข้อความบอท · contacts.html:1360 เลิกฝังค่าตั้งต้น `Document/Dunning` ใน JS (ใช้ค่าจากเซิร์ฟเวอร์)

**M5 — "พร้อม" ไม่ได้ตรวจว่า LINE เรียก webhook ถึง** · `Helpers/CompanyLineReadiness.cs:13-19` ดูแค่ เปิด + token + secret · ไม่มีที่เก็บเวลาที่ webhook เข้าล่าสุด (`Services/Implementations/Cms/CustomerLineLinkService.cs:289-310` จำแค่บอท) · Basic ID ไม่อยู่ในเงื่อนไข (`Services/Implementations/ContactLineBindingService.cs:45-49,82-85`)
- เส้นทางจริง: ร้านใส่ token/secret แต่ลืมวาง Webhook URL/เปิด Use webhook ⇒ ปุ่มสร้างลิงก์กดได้ → ผู้รับยินยอม → ส่งรหัสในแชต → เงียบ ไม่มีใครรู้เหตุ · ไม่ใส่ Basic ID ⇒ ผู้รับไม่มีปุ่มเพิ่มเพื่อน (`wwwroot/pages/contact-line-join.html:89`) พนักงานไม่ถูกเตือนตอนสร้างลิงก์
- แก้: ประทับ `LineWebhookLastSeenAt` หลังลายเซ็นผ่าน (`CustomerLineWebhookController.cs:57`) · แสดง "✅ ได้รับจาก LINE ล่าสุด … / ⚠️ ยังไม่เคยได้รับ" ที่ settings + ในหมายเหตุของลิงก์เชิญ · เตือนเมื่อไม่มี Basic ID

**M6 — ช่องที่ต้องพิมพ์ ID ซึ่งแอปไม่เคยบอก** · "LINE กลุ่มของทีม" (`settings.html:1148-1151`) และ "ทดสอบส่ง" (`:1171-1176`) ต้องพิมพ์ C…/U… · บอทในกลุ่มตอบเฉพาะรหัส GB ของผู้ติดต่อ (`Services/Implementations/ContactLineGroupService.cs:71-92`) · ทดสอบล้มตอบ "ดู log" (`Services/Implementations/LineNotifyService.cs:363-367`) ที่ผู้ใช้เปิดไม่ได้
- แก้: รับรหัส GB ในช่องกลุ่มทีม (ใช้ `LineGroupBindCode` เดิม แล้วเก็บ groupId) + ปุ่ม "ทดสอบส่งเข้ากลุ่มทีม" · `TestConfigAsync` ใช้ `PushCoreAsync` แล้วคืนเหตุของ LINE (ตัดแล้ว 200 ตัวอักษรตามเดิม)

**M7 — ผูกกลุ่ม: ตอบ "บอทประกาศในกลุ่มแล้ว" โดยไม่ตรวจ** · `ContactLineGroupService.cs:440-442` ประกาศล้ม = LogWarning · `Controllers/ContactNotifyEndpointsController.cs:54` ตอบสำเร็จเสมอ · ข้อความรับรอง (`LineGroupBindRule.cs:74-76`) สัญญาว่าประกาศทันที
- แก้: `ConfirmAsync` คืนผลประกาศ ⇒ ข้อความ "ผูกแล้ว แต่ประกาศในกลุ่มไม่สำเร็จ: {เหตุ}" + เก็บใน `LastFailure` ของปลายทาง

**M8 — ลูกค้าส่งสลิป/ไฟล์ในแชต OA บริษัท = เงียบ** · `CustomerLineWebhookController.cs:101-112` รับเฉพาะข้อความตัวอักษร · รูป/ไฟล์ไม่ตอบ ไม่เก็บ · การ์ดชวนจ่ายในแชตเดียวกัน (`Helpers/LineDocumentCard.cs:52-53`)
- แก้ขั้นต่ำ: ผู้ส่งที่เป็นปลายทางผู้ติดต่อ (`LineIdHash` ตรง) ส่งรูป/ไฟล์ ⇒ ตอบครั้งเดียวต่อวันว่า "แนบสลิปที่ปุ่มชำระเงินบนการ์ด" (ไม่รับไฟล์เข้าระบบ — ทางรับสลิปเดียวคือหน้าชำระ)

**M9 — หน้าผูก LINE พนักงานฝังชื่อบอทตายตัว** · `wwwroot/pages/line-bind.html:48` "@nextacc-bot" + "สแกน QR" ที่ไม่มีรูป · Basic ID จริงอยู่ที่ `INextAccLineConfig` (`Services/Implementations/NextAccLineConfig.cs:32-44`)
- แก้: `POST line-bot/issue-bind-code` คืน `basicId` + `addFriendUrl` (`CustomerLineLinkRule.AddFriendUrl`) + QR · ยังไม่ตั้ง ⇒ บอกเหตุแทนรหัส

### LOW
- L1 `Services/Implementations/DocumentLineDeliveryService.cs` ลงทะเบียน (`Program.cs:888`) แต่ไม่มีผู้เรียก · อ่าน `Contact.LineUserId` (ย้ายออกแล้วรอบ 245) · มีตัวประกอบการ์ดชุดที่สอง ⇒ ลบ (F2 ข้อ 2)
- L2 Webhook URL แสดงสองที่ในแท็บเดียว (`settings.html:1136-1141` และ `:2240`) ⇒ เหลือที่เดียว
- L3 บันทึกค่าใดก็ตามรีเซ็ต `LineConfigured=false` (`LineConfigController.cs:116`) ⇒ บรรทัด "ทดสอบล่าสุด … OK" เป็นสีแดง (`settings.html:2203-2207`) · `lastTestStatus` เข้า innerHTML ไม่ผ่าน esc (ค่าวันนี้คงที่ — กันไว้ก่อน)
- L4 ปุ่มล้างรหัสบอก "กลับไปส่งผ่าน LINE ของ NextAcc" (`settings.html:2265`) — จริงเฉพาะจอง/คำสั่งซื้อ · เอกสารถึงผู้ติดต่อจะหยุด
- L5 กด "ส่งอีกครั้ง" แถว LINE ไม่ล้าง `FallbackQueuedAt` (`Services/Implementations/ContactLineQueueService.cs:343-355`) ⇒ ลูกค้าที่ได้อีเมลถอยไปแล้วได้ LINE อีก (ตั้งใจได้ แต่ควรบอกในกล่องยืนยัน)
- L6 `OnDocumentApprovedAsync` กลืนข้อผิดพลาด (`EmailScheduleService.cs:158`) — เข้าคิว LINE ล้มตอนอนุมัติไม่มีร่องรอยบนใบ (ของเดิม · เส้นรายการประจำคืนผลแล้ว)
- L7 `EmailScheduleWorker.cs:36-48` คิวอีเมลโยน ⇒ คิว LINE รอบนั้นไม่ถูกเรียก (try เดียวกัน)
- L8 ยังไม่มี (จดใน DOCUMENT_FLOW §"ยังไม่ทำ" แล้ว): ลิงก์ดูเอกสารบนการ์ด · ชั่วโมงเงียบ · กระดิ่งเมื่อคิว LINE ล้มซ้ำ (มีแต่โควตาเดือนหมด `ContactLineQueueService.cs:47-72`) — กฎ LINE อย่างเดียว ⇒ ลูกค้าไม่เห็นตัวเอกสาร (ฟอร์มบอกไว้ `wwwroot/pages/email-schedule.html:178`)
- L9 `POST line-config/test` ไม่มี `RejectApiKey` (`LineConfigController.cs:137-139`)

## 3. เส้นที่ตรวจแล้วใช้ได้ครบ
- ลายเซ็น webhook: ตัวเดียว constant-time ไม่มี secret = ปฏิเสธ (`Helpers/LineWebhookSignature.cs:25-32`) · บริษัท: ดูค่าตั้งก่อนอ่าน body + จำกัด 256 KB + บอทไม่ตรง = ไม่ประมวล (`CustomerLineWebhookController.cs:46-73`)
- ผูก LINE ผู้ติดต่อ 1:1: token สุ่ม/เก็บ hash · ยินยอม PDPA + RoPA · รหัสใช้ครั้งเดียวแบบมีเงื่อนไข · ล็อกต่อผู้ติดต่อ · จำกัดความถี่ทั้ง IP และ LINE user · ทุก query มี CompanyId (`ContactLineBindingService.cs:39-229`) · "หยุด" ⇒ ยกเลิก + ถอนยินยอม (`:231-257`) · บล็อก/เพิ่มเพื่อนกลับ (`:259-269`)
- ผูกกลุ่ม: รหัส GB ผูกบริษัทที่ออก (`CompanyIdHint`) · OA กลางเฉพาะบริษัทผู้ให้บริการ ตรวจซ้ำใต้ล็อก · กลุ่มกลางของแอดมิน/เกิน 200 คน ปฏิเสธ · #ByeNextAcc/ถูกเตะ ปลดทุกบริษัท (`ContactLineGroupService.cs:106-160,299-389`) · สิทธิ์ `ContactEdit` + ห้ามคีย์ (`ContactNotifyEndpointsController.cs:34-140`)
- คิว LINE: จอง `SKIP LOCKED` · ตรวจใบ/ปลายทางซ้ำตอนส่ง · retry key คงที่ · "ส่งแล้ว" เฉพาะ LINE ตอบรับ · ล้มถาวรถอยไปอีเมลครั้งเดียว · เหตุอยู่บนแถว · ส่งซ้ำ/ยกเลิกมีด่านสิทธิ์ (`ContactLineQueueService.cs:77-320` · `email-schedule.html:74-87,460-500`)
- กฎช่องทาง: trigger ที่ LINE ไม่รองรับ/ไม่มี OA บันทึกไม่ได้พร้อมเหตุ (`ContactLineChannel.cs:40-58` · `EmailScheduleService.cs:1341-1349`) · ผู้ติดต่อ "ไม่ส่งอัตโนมัติ" ชนะทุกช่อง (`ContactLineChannel.cs:107-110`)
- สลิปจากหน้าชำระ: โทเคน · ชนิดไฟล์จากเนื้อไฟล์ · ≤10 MB · 5 ครั้ง/ชม. · กันซ้ำ · ผล = รอผู้ขายยืนยัน + แจ้งพนักงาน (`Controllers/PublicPaymentController.cs:217-263` · `Services/Implementations/SlipEvidenceService.cs:61-158`)
- บอทพนักงาน: ผูกด้วยรหัส 6 หลักมีจำกัดความถี่ + audit + อีเมล (`LineBotService.cs:176-222`) · ด่านสถานะบัญชีและสิทธิ์อนุมัติทุกทาง (`:236,378,453,804,821`) · โควตา OCR ตัด/คืนถูก (`:553-613`)
