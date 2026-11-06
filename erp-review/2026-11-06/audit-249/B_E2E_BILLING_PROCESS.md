# B — ตรวจกระบวนการ End-to-End "รายการประจำ → ส่ง → รับเงิน → ออกใบ → แจ้งเตือน" (รอบ 241–249)

_ตรวจ 2026-11-06 · อ่านโค้ดอย่างเดียว (ไม่มี .NET SDK — ไม่ได้คอมไพล์/รันเทสต์) · HEAD `43cfdba1` · ไม่แก้โค้ด_
_เกณฑ์ที่ใช้ทุกขั้น: (a) เส้นทางมีจริงและมีผู้เรียก · (b) ค่าตั้งถูก "ผู้อ่านเชิงธุรกิจ" อ่าน ไม่ใช่แค่เก็บ+echo · (c) มีด่าน + เทสต์ทิศตรงข้าม · (d) ล้มดัง 3 ที่ (ข้อมูลที่ผู้ใช้เปิดดู · สถานะงาน · คำตอบผู้เรียก) · (e) ทุกทางเข้าที่แตะข้อมูลชุดเดียวกันเดินด่านเดียวกัน_

## สรุปผล

| ขั้น | สถานะ | สรุปสั้น |
| --- | --- | --- |
| 1 ตั้งรายการประจำ (โหมดรับเงิน · นโยบายอนุมัติ · ความพร้อม · ทดลองรัน · อีเมลพร้อมไหม) | ✅ ครบ (มีช่องโหว่ 1 จุดที่แผงความพร้อม — F-2) | ตัวตัดสิน pure ครบ · ผู้อ่านจริงครบ · ด่านสิทธิ์/คีย์ API ครบ · เทสต์ทิศตรงข้ามครบ |
| 2 รัน → สร้างใบ → อนุมัติ → ผลรอบ | ⚠️ มีแต่ไม่ครบ | เส้นหลักถูกต้อง · แต่ธง `EmailEnqueueFailed` ไม่มีวันถูกตั้ง (F-1) |
| 3 ส่ง (คิว · ลิงก์ชำระ · QR · SMTP สำรอง · e-Tax by Email · LINE) | ⚠️ มีแต่ไม่ครบ | อีเมล/e-Tax/QR ครบและไม่ drift · ลิงก์ชำระเงียบเมื่อไม่ได้ตั้ง URL ระบบ (F-2) · LINE L1 ยังไม่ต่อสาย (ตามแผน) · ส่งซ้ำได้เมื่อมีกฎสองตัว (F-4) |
| 4 รับเงิน → บันทึก → ออก/ส่งใบเสร็จ → แจ้งเตือน | ⚠️ มีแต่ไม่ครบ | gateway + มือ 1:1 เดินด่านครบ · WHT ไม่ลบซ้ำ (ยืนยัน 244-7) · แจ้งเตือนฝั่งเงินถูก/ลิงก์เต็ม/ปิดค่าตั้งต้นได้ · ทางเข้าบันทึกรับชำระอีก 7 จุดไม่เข้างานหลังรับชำระ (F-3) |
| 5 ลูกค้าโอนเข้าบัญชี | ✅ ครบตามขอบเขตที่ประกาศ | QR/เลขบัญชีบนกระดาษ+หน้าชำระ · ไม่มีอะไรประทับ "ชำระแล้ว" โดยไม่มีหลักฐาน · จุดที่ต้องมีคน = บันทึกรับชำระ |
| 6 ค่าตั้งที่เพิ่มในรอบ 241–249 | ✅ ครบ (มี 1 กลุ่มที่ไม่มีผู้อ่านโดยตั้งใจ) | ทุกคอลัมน์มีผู้อ่านเชิงธุรกิจ + echo กลับฟอร์ม · ยกเว้น `ContactNotifyEndpoints` (L1 — รอ L2) · ratchet `settings_reader_check` ไม่ครอบ 4 ตาราง (F-7) |
| 7 Kill-switch (#1) | ✅ ครบ | ไม่มีจุดเรียก AI ใหม่ใน 9 รอบ · ทดลองรัน `noAi` ตัดทั้งชั้นเรียนรู้/AI · ใบประจำอนุมัติ `withAiHints:false` |
| 8 กฎเอกสาร (#2) | ✅ ครบ | ใบเสร็จอัตโนมัติผ่าน `IssueReceiptForPaymentAsync` (ด่าน §86/4 ผู้ซื้อก่อน · เลขออกตอนอนุมัติ · วันที่ = วันรับเงิน §78/1) · e-Tax by Email ตาม ETDA (1 ไฟล์ PDF/A-3 ฝัง XML ≤ 3 MB · CC csemail · To 1) · ใบย่อไม่เข้า e-Tax |

---

## ขั้น 1 — ตั้งรายการประจำ (wizard · RecurringController · RecurringTransactionService) — ✅

**(a) เส้นทาง + ผู้เรียกจริง**
- โหมดรับเงินต่อรายการ/ผู้ติดต่อ: `Helpers/AutopilotSettingsResolver.cs:74-100` (`FromMode`/`Resolve`) — ผู้เรียกจริง 3 เส้นตามที่ CHANGELOG อ้าง: `EmailScheduleService.cs:266-276` (`RecurringPayLayerAsync` → `:215-224` ใส่เป็นชั้นใต้ตารางส่ง · `:283-285` เติมเฉพาะช่องที่ตารางว่าง), `DocumentPayLinkService.cs:255-266` (`ContactPayLayerAsync`) → `:201-203` (`EnsureLinkAsync`) และ `:273-276` (`ForScheduledEmailAsync`) · ตรวจตอนบันทึก `RecurringTransactionService.cs:48-52` (`ValidateMode/ValidateChannels` → `BusinessRuleException`) และ `RecurringAutopilotService.cs:394-397` (ผู้ติดต่อ).
- นโยบายอนุมัติ: `Helpers/AutoApprovePolicy.cs` (Catalogue `:58-62` · NeverList `:64-73` · `Validate :93-105` · `BindingHash :149-183` · `MatchesBinding :186`) · ตัวโหลดเดียว `RecurringApprovePolicyLoader.cs:30-53` (ไม่มีใบรับรองระดับรายการ = ไม่ปล่อย `:35` · ลายนิ้วมือไม่ตรง `:46-47` · ผู้ตั้งหมดสิทธิ์ `:49-51`) — ผู้เรียกจริง: เส้นอนุมัติ `RecurringTransactionService.cs:858-863` และแผงความพร้อม `RecurringAutopilotService.cs:222`.
- `ApprovalAckSource.UnattendedWithPolicy`: `Helpers/ApprovalAcknowledgement.cs:30` · ตัวตัดสิน 3 อาร์กิวเมนต์ `:100-106` (ชุดห้ามปล่อยตรวจซ้ำ) · `EffectiveSource :207-208` (ไม่ปล่อยอะไร = Unattended เดิม) — ผู้เรียก `DocumentService.cs:6680, 6747-6751`.
- ความพร้อม: `Helpers/AutopilotReadiness.cs:90-190` — ผู้รวบรวมข้อเท็จจริง `RecurringAutopilotService.BuildAsync :110-305` · endpoint `RecurringController.cs:45-78` (GET `{id}/readiness` · POST `readiness` · POST `{id}/dry-run` · POST `dry-run`).
- ทดลองรัน `noAi`: `RecurringAutopilotService.cs:214` → `DocumentService.PreviewApprovalWarningsForUnsavedAsync :6572-6584` → `CollectApprovalWarningsAsync(noAi: true)` → `:22248-22250` ข้ามทั้ง `AdviseWhtApplicabilityAsync` (orchestrator) · ไม่มี `SaveChanges`/ออกลิงก์/จองโควตาใน `BuildAsync` (ตรวจแล้วทั้งเมธอด — มีแต่ `AsNoTracking` reads).
- อีเมลพร้อมไหม: `Helpers/RecurringRunOutcome.cs:49-65` (`RecurringEmailReadiness`) — ผู้อ่าน: ผู้ส่งจริง `EmailScheduleService.cs:760` · แผง `AutopilotReadiness.cs:157` · ผลรอบ `RecurringTransactionService.cs:949` · บันทึก `:525-536` (`SaveWarnings`) · endpoint `GET recurring/email-readiness` (`RecurringController.cs:139`).

**(b) ค่าตั้งถูกอ่านจริง** — ดูตารางขั้น 6.

**(c) ด่าน + เทสต์ทิศตรงข้าม**
- สิทธิ์: ตั้งนโยบาย = ต้องมีสิทธิ์อนุมัติชนิดนั้น (`RecurringController.cs:88, 123`) + `[RejectApiKey]` (`:82, 105, 118`) · ช่องทางผู้ติดต่อ = `Contact.Edit` (`:111`) · ความพร้อม/ทดลองรัน = สิทธิ์สร้างเอกสาร (`DenyAutopilotReadAsync :32-35`).
- เทสต์: `Accounting.Tests/AutopilotRound248Tests.cs` — ไม่ตั้งนโยบาย ⇒ ยังหยุด (`:33`) · ปล่อย AR-OVERDUE (`:45`) · ทิศตรงข้าม นโยบายไม่ปล่อยคำเตือนอื่น (`:57`) · [Σ-GAP]/VAT ไม่อยู่บนกระดาษ (`:65`) · ทางเข้าอื่นไม่ได้สิทธิ์ (`:77`) · ลายนิ้วมือ (`:123`) · MED-3 (`:141`) · โหมด/ช่องทางที่ไม่รู้จัก (`:220`) · BUYER-TAX Fail (`:256`) · OWNER-POLICY (`:283`) · TransferOnly ไม่มีอีเมล (`:292`) · Db: `RecurringAutopilotRound248DbTests.cs:106` (ทดลองรันไม่เขียน) `:184` (ผู้ตั้งหมดสิทธิ์) `:202/:245` (เปลี่ยนผู้ติดต่อ/ยอดหลังตั้ง) `:268` (นโยบายผู้ติดต่อไม่มีผลถ้าไม่รับรอง) `:323` (MED-4 ไม่ถาม AI) `:357` (ตั้งปล่อย §86/4 ถูกปฏิเสธ).

**(d) ล้มดัง** — ค่าผิด = `BusinessRuleException` พร้อมรหัส `AUTOPILOT-PAYMODE`/`APPROVE-UNATTENDED-POLICY` ถึงผู้เรียก · บันทึกได้แต่มี `SaveWarnings` ⇒ toast (`recurring.html:1054`) · แผงความพร้อมบอกข้อ Fail พร้อมปุ่มทางไปต่อ (`AutopilotReadiness.cs:107-186`).

**(e) ทางเข้าอื่น** — ค่าตั้งผู้ติดต่อ (PaymentMode/นโยบาย) แก้ได้ทางเดียวคือ `PUT recurring/contacts/{id}/autopilot|approve-policy` (ผ่าน wizard `recurring.html:354, 396-416`) · `contacts.html` ไม่มีช่องนี้ (ตรวจ grep: มีแต่แท็บ L1 `:64-67, 335`) · `RecurringTransactionService.UpdateAsync :205-211` ไม่รับ `AutoApprovePolicyJson` (ถูกต้อง — ผ่าน endpoint เฉพาะ) · ลายนิ้วมือกันผู้ไม่มีสิทธิ์แก้ยอดหลังตั้ง.

**ข้อสังเกต (ไม่ใช่บั๊ก แต่เป็นกับดัก UX)**: ปุ่ม "บันทึกนโยบาย" (`recurring.html:396-416`) คำนวณลายนิ้วมือจากแถวใน DB ณ ตอนกด · ถ้าผู้ใช้แก้บรรทัด/ยอดในฟอร์มแล้วกดบันทึกนโยบายก่อนกด "บันทึก" ฟอร์ม ⇒ หลังบันทึกฟอร์ม นโยบายหยุดมีผลทันที (`BindingChangedReason`) — ดังอยู่ที่แผงความพร้อม (`OWNER-POLICY` Fail) และที่ผลรอบ (`RecurringTransactionService.cs:908-910`) จึงไม่เงียบ · แต่ wizard ไม่เตือนก่อนกด → ดู F-6.

---

## ขั้น 2 — รัน → สร้างใบ → อนุมัติ → ผลรอบ — ⚠️

**(a)** `ProcessDueRecurringTransactionsAsync :337-393` / `RunNowAsync :284-323` → `JobLock.RunExclusiveAsync` ต่อรายการ (`:295, :355` — F4 ข้อ 6 ผ่าน) → `ExecuteRecurringAsync` → `CreateDocumentFromTemplateAsync` → `CreateDocumentAsync :813` → `MarkRunAsync :816` (ประทับงวดก่อนขั้นหลัง) → โหลดนโยบาย `:858` → `ApproveDocumentAsync(…, UnattendedWithPolicy|Unattended, withAiHints:false, passed, autoApprovePolicy) :861-863`.
- ใน `ApproveDocumentAsync`: คำเตือน → `Unacknowledged(ackSource, warnings, policy) :6680` → ถ้ายังเหลือ throw `DocumentApprovalWarningsException :6732` (ใบค้างร่าง) · ถ้าปล่อยตามนโยบาย: `policyReleased :6747-6749` → `EffectiveSource :6751` → หมายเหตุภายใน `AutoApprovePolicy.NoteLine :6754` → audit hash chain `:6757-6781` (`ruleCode=APPROVE-UNATTENDED-POLICY` · `policyReleased/policyScope/policySetBy/policySetAt` · `legalReference` จาก `ArOverdueApprovalWarning.LegalReference :246`).
- ผลรอบ: `facts` (`RecurringRunFacts`) เขียนที่ `SetRunNoticeAsync :468-477` (คอลัมน์ `LastRunOutcome`) → อ่าน `MapWithOutcomeAsync :494-525` → `RecurringRunOutcome.Build :95-218` (อ่านสถานะใบ "ตอนนี้" `:98, :140`) → หน้า `recurring.html:582, 1206`.

**(c)** เทสต์: `RecurringRunOutcomeTests.cs` + `AutopilotRound248Tests.cs:311` (ผลรอบตัดป้ายรหัส) · Db `RecurringAutopilotRound248DbTests.cs:143-184` (ไม่ตั้งนโยบาย/ปล่อย/ผู้ตั้งหมดสิทธิ์ บนเส้นอนุมัติจริง).

**(d) ล้มดัง** — อนุมัติล้ม = `RecordAutoApproveFailureAsync :894` (หมายเหตุบนใบ) + `notices`/`facts` (แถวรายการประจำ) + `ErrorLog :906` ✓ · ขั้นหลังสร้างใบล้ม = `FollowUpError :958-966` ✓ · รอบ cron ล้มก่อนสร้างใบ = `RecordRunFailureAsync :377` ✓.

**❌ จุดที่ไม่ดัง — F-1**: `OnRecurringDocumentCreatedAsync` กลืน exception ทุกชนิดเป็น `LogWarning` (`EmailScheduleService.cs:262`) และคืน `Task` เปล่า ⇒ บล็อก `catch (Exception ex2)` ที่ `RecurringTransactionService.cs:930-938` (ตั้ง `EmailEnqueueFailed = true` + notice "เข้าคิวส่งอีเมล…ไม่สำเร็จ") **ไม่มีวันทำงาน** · ผลรอบจึงบอกได้แค่ "ไม่มีอีเมล/ใบยังร่าง" (`:943-955`) — ถ้าเข้าคิวล้มด้วยเหตุอื่น (DB · ออกลิงก์ชำระโยน · PDF heading โยน) ผู้ใช้เห็นรอบ "สำเร็จ" ทั้งที่ไม่มีอีเมลในคิว · คลาส F2 ข้อ 7 (`LogWarning` ไม่ใช่การดัง) — และข้อความใน `RecurringRunOutcome.cs:184-185` (`EmailEnqueueFailed`) เป็นโค้ดที่ไม่มีทางถึง.

---

## ขั้น 3 — ส่ง — ⚠️

### 3.1 คิวอีเมล (EmailScheduleService)
- ทริกเกอร์: `OnRecurringDocumentCreatedAsync :186-263` — ไม่ส่งใบร่าง (`:199-206`) · กฎ `RecurringInvoiceCreated` ชนะธง `AutoSendEmail` (`:216-224` vs `:230-260`) · ชั้นรายการประจำต่อเข้ากฎ (`WithRecurringPayLayer :280-286`) และกฎเสมือน (`:254-255`) ✓.
- ลิงก์ชำระในอีเมล: `EnqueueDocumentAsync :776-777` → `PayLinkFor :38-39` → `DocumentPayLinkService.ForScheduledEmailAsync :268-286` (แนบไหม = ตารางส่ง(รวมชั้นรายการ) › ผู้ติดต่อ › บริษัท ผ่าน `AutopilotSettingsResolver.Resolve :274-276`) → `EnsureLinkAsync :192-251` (ช่องทาง = ตารางส่ง › ผู้ติดต่อ › ค่าบริษัท ∩ ที่บริษัทเปิด `:200-206` · หนึ่งลิงก์ต่อใบ `:211-218` · token ใหม่เขียนแบบมีเงื่อนไข `:228-244`) · คิวเก็บ `{PayLink}` ไม่เก็บ token (`:777, :44-45`) · URL ประกอบตอนส่ง `ComposeQueueMessageAsync :651-654, :726-727` ✓.
- ตรวจซ้ำตอนส่ง: `RecheckDocumentAtSendAsync :839-867` (ยกเลิก/ร่าง/ชำระครบ ⇒ ข้ามพร้อมเหตุ · เขียนเนื้อใหม่ · MED-5 ต่อชั้นรายการจากกุญแจ `:857-858` — `ScheduledEmailSendCheck.RecurringIdFromIdempotencyKey :29` มีจริง) ✓.
- SMTP สำรองของ NextAcc: `ComposeQueueMessageAsync :646-729` — ผู้ส่งจาก `DocumentEmailService.ResolveOnBehalfSenderAsync :763-780` ตัวเดียว · เมื่อ `plan.OnBehalf`: นับผู้รับรวม BCC ≤ 5 (`:667-670`) · เพดานรายวัน/ต่อผู้รับ (`:673-679`) · ห่อเทมเพลตคงที่ + ข้อความผู้ใช้เป็น plain text (`:700-718` `WrapPlatformDocumentEmail`) · From=แพลตฟอร์ม Reply-To=บริษัท (`:720-722`) · เกินเพดาน = Failed + บันทึก `DocumentEmailLog` (`:689-697`) ✓ · หน้าตั้งค่าบอก `SenderNote` (`DocumentPayLinkService.cs:93-96`) และแผงความพร้อมบอก `SMTP` Warn (`AutopilotReadiness.cs:163-166`) ✓.
- ด่าน/เทสต์: `DocumentAutoSendRound241Tests.cs:183` (คิวเก็บที่หมาย) · Db `DocumentAutoSendRound241DbTests.cs:355, 662, 698` (ตารางแนบลิงก์ · เกินเพดานไม่ส่ง · SMTP บริษัทพฤติกรรมเดิม).

**⚠️ F-2 (ลิงก์เงียบ)**: `EnsureLinkAsync :246-248` คืน refusal "ยังไม่ได้ตั้ง URL ของระบบ (App:BaseUrl)" · `ForScheduledEmailAsync :279-284` รับ refusal แล้ว **`LogInformation` + คืน null** ⇒ อีเมลส่งโดยไม่มีลิงก์ (ถ้าเทมเพลตมี `{PayLink}` กลายเป็นค่าว่าง `DocumentPayLinkPolicy.ResolvePlaceholder :224-232`) · `PayQrFactsAsync :317-319` ⇒ ไม่มี QR หน้าชำระบนกระดาษ · `NotificationEngine.ExternalLinkAsync :434-436` ⇒ LINE/อีเมลไม่มีลิงก์ · ทั้งสามจุดเงียบเท่ากัน · **แผงความพร้อม `AutopilotReadiness.Evaluate :168-186` ไม่มีข้อ "URL ระบบ"** ⇒ ขึ้น `GATEWAY Ok "แนบลิงก์ชำระ: …"` และ `payPlan` "แนบลิงก์ชำระ … ยอดในลิงก์ …" (`RecurringAutopilotService.cs:291-295`) ทั้งที่รันจริงจะไม่แนบ — คลาส F2 ข้อ 3 (ด่านรายงานผลที่ไม่เกิดจริง) · ไม่มีเทสต์ทิศนี้.

**⚠️ F-4 (ส่งซ้ำ)**: `ApproveDocumentAsync :7759` เรียก `OnDocumentApprovedAsync` ทุกแหล่งรวม Unattended · ใบประจำที่อนุมัติอัตโนมัติจึงเข้าคิวได้ 2 ฉบับเมื่อบริษัทมีทั้งกฎ `DocumentApproved` และ (กฎ `RecurringInvoiceCreated` หรือธง `AutoSendEmail`) — กุญแจกันซ้ำต่างกัน (`doc:{id}:DocumentApproved:approved` vs `doc:{id}:RecurringInvoiceCreated:recurring-{rid}|autosend-{rid}` · `EmailScheduleService.cs:74, 219-221, 258-259, 762`) · ไม่มีด่านข้ามทริกเกอร์ใน `EnqueueDocumentAsync :755-800` · ลูกค้าได้ 2 อีเมล (ลิงก์เดียวกัน) · ผลรอบ (`RecurringRunOutcome.cs:176-177, 186-187`) อธิบายกรณี "อนุมัติเองทีหลัง" ได้ แต่ไม่มีคำเตือนตอนตั้งค่า.

### 3.2 QR บนเอกสาร (รอบ 246) — ✅ ไม่ drift
- resolver เดียว `PaymentQrService.ResolveCoreAsync :262-304` (อ่าน `template.ShowPaymentQr/ShowPayLinkQr :265, :271, :300` · `PaymentQrBankAccountId :285` · `BankAccount.PaymentQrSource/PaymentQrImagePath :85-86, :110-115, :291`) → `PaymentQrBlock` ตัวเดียว.
- สอง renderer อ่านบล็อกเดียวกันและข้อความใต้ QR จากฟังก์ชันเดียว `PaymentQrCaption :20-33`: HTML `PaymentQrHtml :39-57` (เรียก `PdfGenerationService.cs:2486`) · QuestPDF `ComposePaymentQr :60-75` (เรียก `DocumentRenderer.cs:1057`) · ทุกทางพิมพ์ส่ง `paymentQr` เดียวกัน (`PdfGenerationService.cs:102 → :122 (QuestPDF pdfA · WithoutPayLink) / :144-145 (HTML/Chromium) / :183-184 (HTML preview)` · `DocumentRenderer.cs:303-304`) · ไฟล์ e-Tax ตัด QR หน้าชำระ (`WithoutPayLink :122, PaymentQr.cs:83`) ✓.
- เทสต์: `PaymentQrRound246Tests.cs:318-381` (HTML มี/ไม่มี · HtmlEncode · QuestPDF · ข้อความใต้ QR ตัวเดียวของสอง renderer) + `:178, :182, :189, :223` ทิศตรงข้าม ✓.

### 3.3 e-Tax by Email (รอบ 244) — ✅
- ตัวเลือกช่องทาง `Helpers/EtaxDeliveryRouter.cs:36-47` (pure) → `DocumentEmailService.RouteForAsync :213-227` · ผู้เรียกครบ 3 ทาง: ปุ่มส่ง (`SendDocumentRoutedAsync :185-203`) · คิว (`EmailScheduleService.SendViaEtaxIfRequiredAsync :590-632` — ด่านปฏิเสธ = Failed ไม่ถอยเป็นอีเมลธรรมดา `:594-600, :621-631`) · หลังรับชำระ (`PostPaymentAutomationService.SendReceiptAsync :357-364` → `blocked` + `halt`).
- ด่านผู้ส่ง `Helpers/EtaxByEmailSenderGate.cs:100-119` (SMTP/บัญชีบริษัทที่ทดสอบผ่าน + กล่องตรงอีเมลที่ลงทะเบียน · ไม่ถอยไปแพลตฟอร์ม) — เรียกก่อนออกแถว e-Tax (`:195-201`) และในตัวส่ง (`:390-391`).
- รูปแบบ ETDA `Helpers/EtaxByEmailFormat.cs`: หัวเรื่อง `[ddMMyyyy พ.ศ.][INV|…][เลขที่]` (`:71-127` · รหัสจาก `EtaxDocumentTypeMap`) · To 1 + CC `csemail@etax.teda.th`/UAT (`:43-45, :148-178`) · 1 ไฟล์ ≤ 3 MB (`:182-189`) · ไฟล์แนบ = PDF/A-3 ที่ฝัง XML เท่านั้น (`DocumentEmailService.cs:422-476` — สร้างไม่ได้ = Refuse 409 `:466-468`, ขนาด `:476-477`).
- `EtaxByEmailUseUat`: ผู้เขียน `EtaxController.cs:384` · ผู้อ่าน `DocumentEmailService.cs:302, :362` → `TimestampAddress(useUat)` · echo `:412-417` + `settings.html` ✓ (ไม่ใช่ `EtaxTestMode`).
- เทสต์ `EtaxByEmailRound244Tests.cs` + Db `EtaxByEmailRound244DbTests.cs` (ชื่อในรายการเทสต์).

### 3.4 ช่องทาง LINE ของผู้ติดต่อ (รอบ 245 L1) — ⚠️ ยังไม่ต่อสาย (ตามแผน)
- `ContactRecipientResolver` ถูกอ้างเฉพาะ `ContactNotifyEndpointsController.cs:21-25` และ `ContactNotifyEndpointService.cs:18-20` (grep ทั้ง `Accounting/`) · **ไม่มีเส้นส่งใดอ่าน** (`EmailScheduleService.EnqueueDocumentAsync :758-760` ยังอ่าน `Contact.Email` · `PostPaymentAutomationService :268` เช่นกัน) — ตรงกับที่ CHANGELOG/DOCUMENT_FLOW §2.12h ประกาศว่า L2 ค่อยต่อ และ UI มีคำเตือน (`contacts.html` แท็บ L1) · นับเป็น "มี ≠ ถูกเรียก" ที่ตั้งใจ — ต้องมีกำหนดปิดช่องว่าง (F-5).

---

## ขั้น 4 — รับเงิน → บันทึก → ออก/ส่งใบเสร็จ → แจ้งเตือน — ⚠️

**(a) เส้นทาง**
- `PublicPaymentController.cs:174-192` (`documents/{token}` · `[AllowAnonymous]` · token = hash ค้น + เทียบเวลาคงที่ `DocumentPayLinkService.PublicViewAsync :366-371` · ลิงก์ใช้ไม่ได้ = ไม่เปิดเผยอะไร `:376-377, :459-466` · ตัดสินสด ยกเลิก/ชำระครบ/บริษัทปิดออนไลน์/จ่ายผ่านลิงก์นี้แล้ว `:385-390`) → `StartAsync :198-255` (ปฏิเสธวิธีนอก `AllowedMethods :213-216` · เพดาน 20/ชม. `:218-229`) → `PaymentIntentService.StartAsync` → Omise/ManualSlip → webhook → `DocumentPaymentHandler.HandleSucceededAsync :40-112` (`TestModePaymentPolicy.GuardAsync :43` · กันซ้ำด้วย Reference `:47-53` · หาใบไม่พบ = โยน `:59-69`).
- **WHT (ยืนยัน 244-7 ไม่ลบซ้ำ)**: `Document.BalanceDue` เป็นยอดสุทธิหลังหักอยู่แล้ว (`TotalAmount = Sub+VAT−WHT` ตามคอมเมนต์ `DocumentPayLinkPolicy.cs:153-158` · เส้นใบจ่าย `DocumentService.cs:3234-3243`) · `AmountsFor(balanceDue, whtRemaining, 0) :160-166` ⇒ `Net = balanceDue` (ไม่ลบ WHT อีก) · `Gross = Net + WHT` · handler: `PaymentWhtFiguresAsync :15457-15465` (ใบลดหนี้ + ตัวนับหักไปแล้ว `WhtWithheldCounter`) → `ExpectedWht` (บุคคลธรรมดา = 0 `:140`) → `WhtForPayment(intent.Amount, doc.BalanceDue, …) :170-174` ⇒ เงินเข้าเท่ายอดค้างจึงบันทึก WHT ที่เหลือทั้งก้อน (`DocumentPaymentHandler.cs:85-95`) · ลิงก์ (`DocumentPayLinkService.AmountsAsync :160-169`) และ QR (`PayQrFactsAsync :307`) ใช้ตัวคิดเดียวกัน ✓ · เทสต์ `DocumentAutoSendRound241Tests.cs:91, :109` + Db `:465, :492, :513, :532, :549` (บุคคลธรรมดา · ใบลดหนี้) ✓.
- งานหลังรับชำระ: `PostPaymentAutomationService.EnqueueAsync :65-97` (ค่าบริษัททับด้วยตารางที่ส่งใบ `SettingsForAsync :52-63` · ปิด = ไม่สร้างงาน `:77` · 1 แถวต่อการชำระ `ON CONFLICT :84-88`) → `EmailScheduleWorker.cs:45` → `ProcessDueAsync :99-129` (SKIP LOCKED + lease) → `ExecuteAsync :206-328` (ตัดสินด้วย `PostPaymentAutomationPolicy.Decide :253-261` รวม §86/4 ผู้ซื้อ `:246-247` · ออกใบเสร็จด้วยเส้นเดิม `IssueReceiptForPaymentAsync :280` · ใบเสร็จร่าง = หยุด `:283-287` · ส่งด้วย `SendDocumentRoutedAsync :357` · ขั้นที่ทำแล้วจำ `MarkDoneAsync :190-198`).
- แจ้งเตือน: `DocumentService.CreatePaymentAsync :15375-15386` → `PaymentNotificationText.Build :40-60` (ฝั่งจาก `IsCashInflowDocAsync` ตัวเดียวกับยอดธนาคาร `:15331`) → คีย์ `payment_received`/`payment_made` · `NotificationEngine.DispatchAsync :60-100` ("ยังไม่ตั้ง" = ไม่มีแถวเลย `:84-87` · แถวปิดทุกช่อง = ไม่ส่ง `:98`) · ลิงก์เต็ม `ExternalLinkAsync :427-444` → `NotificationLink.Absolute :105-124` (ไม่รู้โดเมน = ไม่ใส่ลิงก์) · migration `NotificationEventSplit.MigrationSql` รันที่ `DatabaseMigrationHelper.cs:3868` หลัง `EnableLineGroup :3841` และ `NotificationPreferences :3846` ✓ (idempotent ด้วย NOT EXISTS + ON CONFLICT).

**(c) ด่าน + เทสต์** — `PaymentNotificationRound249Tests.cs:24-151` (PI ไม่ขึ้น "รับชำระ" · ทิศตรงข้าม · path สัมพัทธ์ · URL เต็ม · คีย์ในแคตตาล็อก · migration) · Db `PaymentNotificationRound249DbTests.cs:60-92` (ปิดค่าตั้งต้นได้ · ยังไม่ตั้ง = ค่าตั้งต้น · migration) ✓ · `DocumentAutoSendRound241DbTests.cs:144-611` (กันซ้ำ · ออกด้วยเส้นเดิม · ผู้ซื้อขาดข้อมูล · ใบยกเลิก · ลองใหม่ไม่ส่งซ้ำ) ✓ · `required_call_site_check` R241 (`tools/…:14452-14545`) / R249 (`:10927-10972`) ล็อกจุดเรียก ✓.

**(d) ล้มดัง** — งานหลังรับชำระ: สถานะ+เหตุบนแถว (`run.Status/Message :161-162`) · แจ้งเตือน `PostPaymentAutomationBlocked :370-388` · บันทึกอีเมล Failed บนเอกสาร · หน้าเอกสารเห็น `Runs` ผ่าน `StatusAsync :349-361` + ปุ่มลองใหม่ (`DocumentAutoSendController.cs:95`) ✓ · ทางเข้า gateway ไม่โยนหลังเงินบันทึกแล้ว (`DocumentPaymentHandler.cs:109-111`, `EnqueueAsync :91-96` LogError) — ยอมรับได้เพราะ "ไม่มีแถวงาน" ตรวจได้ที่หน้าเอกสาร แต่ผู้ใช้ไม่ได้รับแจ้งว่าแถวไม่ถูกสร้าง (ความเสี่ยงต่ำ: ล้มเฉพาะ DB ล่ม).

**❌ (e) ทางเข้าอื่น — F-3**: `CreatePaymentAsync` ถูกเรียกจาก 10 จุด (grep `Accounting/`): `DocumentController.cs:1716` และ `DocumentPaymentHandler.cs:97` เรียก `EnqueueAsync` (`:1719-1721`, `:110-111`) · **อีก 8 จุดไม่เรียก**: `CreateMultiDocPaymentAsync` (`DocumentController.cs:1715` — ตั้งใจ · `SettlementReceiptPolicy`) · `Services/Settlement/SettlementPostingService.cs:313` (ใบขายที่ลูกค้าจ่ายผ่านช่องทางชำระที่ settle เป็นงวด — ใบเดียวกับที่ลิงก์ชำระออก) · `Services/Import/Targets/PaymentImportTarget.cs:184` (นำเข้า CSV รับชำระ — ทางที่บริษัทรับโอนธนาคารใช้บ่อย) · `PlatformBillingDocumentIssuer.cs:320` · `CmsCommerceService.cs:1684` · `CmsBookingService.Workflow.cs:425` · `LodgingService.Lifecycle.cs:1778` · `LodgingService.TaxTiming.cs:421` · ⇒ ค่าตั้ง `PostPayEmailReceipt` "ลูกค้าชำระแล้วส่งใบเสร็จอัตโนมัติ" มีผลเฉพาะ 2 ทางเข้า · ผู้ใช้ที่นำเข้าการรับชำระจากไฟล์ธนาคาร หรือใบที่ settle ผ่านช่องทางชำระ จะไม่ได้ใบเสร็จ/อีเมลอัตโนมัติโดยไม่มีคำเตือนใด (silent no-op ของค่าตั้ง) · R241 ใน `required_call_site_check` ล็อกเฉพาะ 2 จุด (`:14465-14469`).
  _หมายเหตุ_: CMS/Lodging/PlatformBilling มีเส้นออกใบเสร็จของตัวเอง — ที่ต้องตัดสินจริงคือ `SettlementPostingService` และ `PaymentImportTarget`.

**ข้อสังเกตรอง**: `NotificationEngine.DispatchAsync :74-78` — เมื่อผู้เรียกระบุ `RecipientUserId` และบริษัทปิดทุกช่องของคีย์ไว้ (`savedRows` มีแต่ปิดหมด) ยังส่งตรงถึงผู้ใช้ด้วยค่าตั้งต้น (เช็ค `settings.Count == 0` ก่อนเช็ค `savedRows`) — คีย์รับ/จ่ายชำระไม่ระบุ `RecipientUserId` จึงไม่กระทบรอบ 249 แต่เป็นคลาสเดียวกับบั๊ก (3) ที่เพิ่งแก้ สำหรับคีย์ที่ระบุผู้รับ (เช่น `RecurringApprovedWithNotes` `RecurringTransactionService.cs:880`) → F-8.

---

## ขั้น 5 — ลูกค้าโอนเข้าบัญชี — ✅ (ตามขอบเขต 241-2 / 246)

สิ่งที่มีวันนี้: (1) กระดาษ: QR พร้อมเพย์ฝังยอดสุทธิ/รูป QR ที่อัปโหลด ข้างข้อมูลบัญชี (`PaymentQrService.ResolveCoreAsync` · ยอดจาก `PayQrFactsAsync`) · (2) หน้าชำระสาธารณะ: กล่องโอน + QR พร้อมเพย์ ยอดสุทธิ (`DocumentPayLinkService.PublicViewAsync :400-416`, `DocumentAccountTransferAsync :438-454`) · (3) `TransferOnly` = ไม่แนบลิงก์ gateway (`AutopilotSettingsResolver.cs:80`) · หน้า "เลขบัญชีอย่างเดียว" = รอบ 250 (คอมเมนต์ `:16`).

จุดที่ต้องมีคน: **บันทึกรับชำระ** (`DocumentController.CreatePayment`) หรือนำเข้าไฟล์ (`PaymentImportTarget`) — จากนั้นงานหลังรับชำระทำต่อเฉพาะทางแรก (F-3).

ยืนยัน "ไม่มีอะไรประทับชำระโดยไม่มีหลักฐาน": `BankFeedService.TryAutoMatchAsync :313-340` จับคู่รายการธนาคารกับ **Payment ที่มีอยู่แล้ว** (`ScoreLocalPaymentCandidatesAsync :458-478`) เปลี่ยนเฉพาะ `ReconciliationStatus` ไม่สร้าง Payment/ไม่แตะ `BalanceDue` (grep `CreatePaymentAsync|Status = DocumentStatus.Paid` ใน BankFeed/OpenBanking/AdvancedArAp = 0) · gateway ประทับเฉพาะเมื่อผู้ให้บริการยืนยัน (`HandleSucceededAsync` หลัง intent `Succeeded` + `TestModePaymentPolicy`) · ไม่มีแนบสลิปแบบไม่ล็อกอินสำหรับเอกสาร (`StartAsync :202-204` ปฏิเสธ `ManualSlip`) · git log ตั้งแต่ 2026-11-05 ไม่มีผู้เขียน `DocumentStatus.Paid` ใหม่ ✓.

---

## ขั้น 6 — ค่าตั้งที่เพิ่มในรอบ 241–249 — ผู้อ่านเชิงธุรกิจ + round-trip

| ค่าตั้ง (คอลัมน์) | ผู้อ่านเชิงธุรกิจ (file:line) | echo/hydrate ฟอร์ม | ผล |
| --- | --- | --- | --- |
| `CompanySettings.PayLinkDefaultEnabled` | `DocumentPayLinkService.cs:270-276` (ForScheduledEmailAsync) · `RecurringAutopilotService.cs:234` | `settings.html:1416` ↔ `:1442` | ✅ |
| `CompanySettings.PayLinkDefaultChannelsJson` | `DocumentPayLinkService.ChannelsAsync :187-189` → `Allowed :204, :316, :343, :380` · `RecurringAutopilotService.cs:235` | `settings.html:1419-1430` ↔ `:1439-1443` | ✅ |
| `CompanySettings.PostPayEmailReceipt` / `PostPayIncludePartial` | `PostPaymentAutomationService.SettingsForAsync :54-56` → `EnqueueAsync :76-77`, `ExecuteAsync :220` | `settings.html:1431-1432` ↔ `:1444-1445` | ✅ (ผลจำกัดตาม F-3) |
| `CompanySettings.EtaxByEmailUseUat` | `DocumentEmailService.cs:302, :362` → `TimestampAddress` | `EtaxController.cs:384` ↔ `:412-417` · `settings.html` | ✅ |
| `EmailScheduleRule.AttachPayLink` / `PayLinkChannelsJson` | `DocumentPayLinkService.ForScheduledEmailAsync :275, :278` · `EmailScheduleService.UpsertRuleAsync :955-963` (ด่าน) | `email-schedule.html:269, :273` ↔ `:324-325` | ✅ |
| `EmailScheduleRule.PostPayEmailReceipt` / `PostPayIncludePartial` | `PostPaymentAutomationService.SettingsForAsync :58-62` (ผ่าน `Document.AutoSendRuleId` ที่ `EmailScheduleService.cs:779-781` ประทับ) | `email-schedule.html:270-271` ↔ `:326-327` | ✅ |
| `RecurringTransaction.PaymentMode` / `PayLinkChannelsJson` | `EmailScheduleService.RecurringPayLayerAsync :266-276` (เส้นส่งจริง) · `RecurringAutopilotService.cs:231` | `recurring.html:301-302` ↔ `:1141-1142` · `GetByIdAsync` → `MapToResponse :1070-1071` | ✅ |
| `RecurringTransaction.AutoApprovePolicyJson` | `RecurringApprovePolicyLoader.cs:34-52` → `RecurringTransactionService.cs:858-863` | `recurring.html:303` ↔ `savePolicy :396-416` · `MapToResponse :1072` | ✅ |
| `RecurringTransaction.LastRunOutcome` | `MapWithOutcomeAsync :494-525` → `RecurringRunOutcome.Build` | หน้าแสดง `recurring.html:582, :1206` (อ่านอย่างเดียว) | ✅ |
| `Contact.PaymentMode` / `PayLinkChannelsJson` | `DocumentPayLinkService.ContactPayLayerAsync :258-266` (ทั้ง `EnsureLinkAsync` และ `ForScheduledEmailAsync`) · `RecurringAutopilotService.cs:233` | `recurring.html:346, :354` (ใน wizard เท่านั้น · `contacts.html` ไม่มี) | ✅ (UI อยู่หน้าเดียว) |
| `Contact.AutoApprovePolicyJson` | `RecurringApprovePolicyLoader.cs:37-42` (มีผลเฉพาะรายการที่รับรอง `inherit`) | `recurring.html:369` ↔ `savePolicy('contact')` | ✅ |
| `DocumentTemplate.ShowPaymentQr` / `ShowPayLinkQr` / `PaymentQrBankAccountId` | `PaymentQrService.ResolveCoreAsync :265, :271, :285, :300` | `document-templates.html:409-415` ↔ `:1022, :1037, :1044-1050` | ✅ |
| `BankAccount.PaymentQrSource` / `PromptPayId` / `PaymentQrImagePath` | `PaymentQrService.cs:85-86, :110-115, :291` | `bank.html:2731-2733` ↔ `:2800-2805` (เส้น `bank/accounts/{id}/payment-qr*`) | ✅ |
| `ContactNotifyEndpoints.*` (อีเมล/LINE/เรื่อง/ความยินยอม) | **ไม่มีผู้อ่านเชิงธุรกิจ** — `ContactRecipientResolver` ถูกเรียกจาก controller/service ของตัวเองเท่านั้น | `contacts.html` แท็บ L1 | ⚠️ ตั้งใจ (L2) — ดู F-5 |
| `NotificationSettings` คีย์ `document.payment_made` (ไม่มีคอลัมน์ใหม่) | `NotificationEngine.DispatchAsync :60-100` | `notifications.html` อ่านแคตตาล็อกจากเซิร์ฟเวอร์ (เทสต์ `PaymentNotificationRound249Tests.cs:141`) | ✅ |

`tools/settings_reader_check.py` รันผ่าน ("ค่าตั้งทุกตัวนอก baseline มีผู้อ่าน") — **แต่ขอบเขต `ENTITIES` (`:44-75`) ครอบ `CompanySettings`, `DocumentTemplate`, … ไม่ครอบ `RecurringTransaction`, `Contact`, `EmailScheduleRule`, `BankAccount`** ⇒ แถวในตารางบนที่ตรวจด้วยมือ 7 แถวอยู่นอก ratchet (F-7).

---

## ขั้น 7 — Kill-switch (#1) — ✅

- grep `AskAsync|IAiOrchestrator` ในไฟล์ `.cs` ที่เปลี่ยนตั้งแต่ 2026-11-05 (257 ไฟล์) = เฉพาะ `Program.cs` (ลงทะเบียน) · ไฟล์ OCR ของรอบ 242 (`OcrLineVatMarks` · `OcrTotalAnchor` · `OcrHeaderAmounts` · `OcrVatCodeLines` · `OcrConfidenceGateway` · `OcrService`) ไม่มีจุดเรียก AI ใหม่ (เป็น pure helper + เทสต์กระดาษจริง `OcrVatRateTableTests`).
- จุด AI เดิมในเส้นนี้: (1) คำแนะนำ WHT ตอนรวบรวมคำเตือน `DocumentService.cs:22248-22250` — ทดลองรัน `noAi` ⇒ `WhtAdvice` ว่าง (เตือนตามกฎ local) · เส้นอนุมัติจริงผ่าน orchestrator (student-first ตามคอมเมนต์ `:22244-22247`) · (2) AI hints ตอนหยุด `:6690` — ใบประจำส่ง `withAiHints:false` (`RecurringTransactionService.cs:863`) · (3) จับคู่ bank feed `BankFeedService.cs:362` — local ก่อน (`:316`) นอกขอบเขตรอบ.
- ทดลองรันไม่เขียน feedback row (ไม่เรียก orchestrator เลย) ✓ · Db test `RecurringAutopilotRound248DbTests.cs:323` ล็อก MED-4.

---

## ขั้น 8 — กฎเอกสาร (#2) — ✅

- ใบเสร็จ/ใบเสร็จ-ใบกำกับหลังรับชำระ: `PostPaymentAutomationService.ExecuteAsync :246-247` เรียก `TaxInvoiceCompletenessChecker.BuyerBlockingFields` ก่อน (ขาด = `Blocked` + แจ้งคน `PostPaymentAutomationPolicy.Decide`) · ออกใบด้วย `IssueReceiptForPaymentAsync :14830-14925` ตัวเดิม: `FOR UPDATE` บน Payment · idempotent (`existing :14848-14863`) · `SettlementReceiptPolicy.WhyCannotIssue` · `CarriesTaxInvoiceRole` · เลขจริงเฉพาะเมื่อผู้ออกมีสิทธิ์อนุมัติ (`canApprove :14897-14900`) ไม่งั้น `DRAFT-` (`CreateSettlementReceiptAsync` บรรทัด 42-43 ของเมธอด) · วันที่ใบ = `payment.PaymentDate` (บรรทัด 51 ของเมธอด — tax point §78/1 · เลขเรียงตามวันรับเงิน `GetNextDocumentNumber(…, payment.PaymentDate)`) · `CreatePaymentAsync` ปฏิเสธวันจ่ายอนาคต (`:14944`) ✓ · ไม่มีโค้ดใหม่ที่ออกเลขนอก `ApproveDocumentAsync`/เส้นนี้.
- การอนุมัติใบประจำยังเดิน `ApproveDocumentAsync` เต็ม (ด่าน CN/DN เหตุผล `:6790-6799` · โควตา · OCR line block) ✓ · นโยบายปล่อยได้เฉพาะ `[AR-OVERDUE]` — `BUYER-86/4`, `Σ-GAP`, `VAT-NOT-ON-PAPER`, `PERIOD-CLOSED`, `APPROVAL-LIMIT` อยู่ใน `NeverList :64-73` และ `Validate :98-100` ปฏิเสธตอนตั้ง + `NeverPassable :76-77` ตรวจซ้ำตอนรัน ✓.
- e-Tax by Email: ขอบเขตเดียวกับ hook ออก e-Tax (`EtaxDeliveryRouter` ใช้ `EtaxAutoSkip` — ใบย่อ/walk-in/ไม่ใช่ใบกำกับเต็มรูป = อีเมลธรรมดา `:49-60`) · รูปแบบตาม ETDA ดู 3.3 ✓.
- ใบกำกับอย่างย่อ → ภาษีซื้อ: ไม่มีโค้ดในรอบนี้แตะ (`AbbreviatedTaxInvoiceRule` ยังเป็นตัวตัดสินเดียวที่ `PdfGenerationService.cs:1729-1731, :1809-1812`) — ไม่ได้ตรวจซ้ำเส้นภาษีซื้อ (นอกขอบเขต 241–249).

---

## รายการสิ่งที่พบ (เรียงตามความรุนแรง)

| # | ระดับ | ที่ | สถานการณ์ที่พัง | วิธีแก้ที่เสนอ | ขนาด |
| --- | --- | --- | --- | --- | --- |
| F-3 | **สูง** (ค่าตั้งเป็น silent no-op ใน 2 ทางเข้าที่ใช้จริง) | `Services/Settlement/SettlementPostingService.cs:313` · `Services/Import/Targets/PaymentImportTarget.cs:184` (และ CMS/Lodging/PlatformBilling อีก 5 จุด) vs ผู้เรียก `EnqueueAsync` เพียง `DocumentController.cs:1721` + `DocumentPaymentHandler.cs:111` | บริษัทเปิด "ส่งใบเสร็จหลังรับชำระ" · ลูกค้าโอน · บัญชีนำเข้าไฟล์รับชำระจากธนาคาร (หรือใบถูก settle ผ่านช่องทางชำระ) ⇒ เงินลง AR ตัด แต่ไม่มีแถว `PostPaymentAutomationRuns` ⇒ ไม่ออก/ไม่ส่งใบเสร็จ ไม่มีแจ้งเตือน ไม่มีสถานะให้ดู · ผู้ใช้เชื่อว่าระบบส่งแล้ว | ย้ายการเรียก `EnqueueAsync` เข้าไปหลัง commit ใน `CreatePaymentAsync` เอง (1:1 · source จากพารามิเตอร์) ให้ทุกทางเข้าเดินด่านเดียว หรืออย่างน้อยเพิ่มที่ `PaymentImportTarget` + `SettlementPostingService` · เพิ่มกติกา R241 ใน `required_call_site_check` ให้ล็อกจุดเรียกทุกทางเข้าเอกสารขาย · Db test "นำเข้ารับชำระ ⇒ มีแถวงาน" | M |
| F-2 | **กลาง** | `DocumentPayLinkService.cs:246-248, :279-284` · `AutopilotReadiness.cs:168-186` (ไม่มีข้อ URL ระบบ) · `RecurringAutopilotService.cs:291-295` · `PayQrFactsAsync :317-319` · `NotificationEngine.cs:434-436` | ระบบใหม่/ย้ายเครื่องที่ยังไม่ได้ตั้ง `SiteSettings.AppBaseUrl`/`App:BaseUrl` (หรือยังเป็น `app.nextacc.com`) ⇒ แผงความพร้อมขึ้น Full "แนบลิงก์ชำระ" · ทดลองรันบอกยอดในลิงก์ · แต่รอบจริง: อีเมลไม่มีลิงก์ (`{PayLink}` ว่าง) · กระดาษไม่มี QR หน้าชำระ · LINE/อีเมลไม่มีลิงก์ — ทุกจุด `LogInformation` | เพิ่ม Fact `SystemBaseUrlConfigured` ใน `AutopilotReadiness` (ข้อ `GATEWAY` Warn "ยังไม่ได้ตั้ง URL ของระบบ — ลิงก์/QR จะไม่ถูกแนบ" + ปุ่มไปหน้าแอดมิน) · ให้ `ForScheduledEmailAsync` คืน refusal กลับไปให้ `EnqueueDocumentAsync` ใส่ `ErrorMessage` บนแถวคิว (ส่งได้แต่บอกว่าไม่มีลิงก์เพราะอะไร) · เทสต์ทิศตรงข้าม "ไม่ตั้ง BaseUrl ⇒ readiness ไม่ใช่ Full" | S |
| F-1 | **กลาง** | `EmailScheduleService.cs:262` (catch ทั้งหมด → `LogWarning`) ทำให้ `RecurringTransactionService.cs:930-938` (`EmailEnqueueFailed`) และ `RecurringRunOutcome.cs:184-185` เป็นโค้ดตาย | เข้าคิวอีเมลล้ม (เช่น `PdfGenerationService.ResolveDocumentHeadingAsync` โยน · ออกลิงก์ชนกัน · DB) ⇒ ผลรอบไม่มีเรื่องต้องทำต่อ ⇒ ใบอนุมัติแล้วแต่ไม่เคยถูกส่ง ไม่มีใครรู้ | ให้ `OnRecurringDocumentCreatedAsync` คืนผล (`EnqueueOutcome`: Enqueued/Skipped(เหตุ)/Failed(เหตุ)) หรือโยนต่อเฉพาะ exception ที่ไม่ใช่ "ข้ามโดยตั้งใจ" · ผู้เรียกตั้ง `EmailEnqueueFailed` จากผลนั้น · เทสต์: fake `IEmailScheduleService` โยน ⇒ facts มี `EmailEnqueueFailed` | S |
| F-4 | **กลาง-ต่ำ** | `DocumentService.cs:7759` + `EmailScheduleService.cs:74, :219-221, :258-259, :762` | บริษัทตั้งกฎ "เมื่ออนุมัติเอกสาร" ไว้ (เพื่อใบคีย์มือ) และติ๊ก `AutoSendEmail` ที่รายการประจำ ⇒ ใบประจำที่อนุมัติอัตโนมัติเข้าคิว 2 ฉบับ (กุญแจต่างกัน) ⇒ ลูกค้าได้ 2 อีเมล · กินโควตา NextAcc ×2 | ใน `OnRecurringDocumentCreatedAsync` ข้ามกฎเสมือน/กฎ `RecurringInvoiceCreated` เมื่อมีกฎ `DocumentApproved` ที่ครอบชนิดนี้และใบอนุมัติแล้ว (หรือกลับกัน: `OnDocumentApprovedAsync` ข้ามใบที่มีกุญแจ `recurring-`/`autosend-` ของใบเดียวกันในคิว) · แผงความพร้อมเตือน "มีกฎทั้งสองแบบ" · เทสต์ Db "ทั้งสองกฎ ⇒ 1 แถว" | S |
| F-5 | **ต่ำ** (ตามแผน แต่เป็นหนี้ที่ต้องมีกำหนด) | `ContactRecipientResolver` ไม่มีผู้เรียกฝั่งส่ง · `EmailScheduleService.cs:758-760` · `PostPaymentAutomationService.cs:268` | ผู้ติดต่อตั้งอีเมลรองรับ "ใบแจ้งหนี้" ที่แท็บ L1 แล้ว แต่ใบยังไปที่ `Contact.Email` เดิม · ตั้งแล้วไม่มีผล (UI เตือนไว้ · แต่ค่าตั้งที่เก็บแต่ไม่มีผู้อ่าน = F2 ข้อ 2) | ปิด L2 ในรอบถัดไป: ให้ `EnqueueDocumentAsync`/`PostPayment` ถาม `IContactRecipientResolver` (เรื่อง `invoice`/`receipt`) ก่อนตก `Contact.Email` · จนกว่าจะถึงตอนนั้น ให้แท็บ L1 แสดงป้าย "ยังไม่มีผล" ทุกแถว (ไม่ใช่แค่คำเตือนหัวแท็บ) | M |
| F-6 | **ต่ำ** (UX) | `recurring.html:396-416` | แก้ยอดในฟอร์ม → กด "บันทึกนโยบาย" → กด "บันทึก" ⇒ นโยบายหยุดมีผลทันที (ลายนิ้วมือ) — ดังที่แผงความพร้อม แต่ผู้ใช้เพิ่งกดบันทึกนโยบายไปเมื่อครู่ | ปิดปุ่มบันทึกนโยบายเมื่อฟอร์ม dirty (`_buildPayload` ต่างจากค่าที่โหลด) + ข้อความ "บันทึกรายการก่อน" · หรือให้ `save()` ที่มีนโยบายเดิมอยู่และผู้ใช้มีสิทธิ์อนุมัติ รับรองซ้ำอัตโนมัติ (ถามยืนยัน) | S |
| F-7 | **ต่ำ** (ratchet ไม่ครอบ) | `tools/settings_reader_check.py:44-75` (`ENTITIES`) | ค่าตั้งใหม่บน `RecurringTransaction`/`Contact`/`EmailScheduleRule`/`BankAccount` (7 คอลัมน์ของรอบ 241–248) อยู่นอก ratchet — รอบหน้าเพิ่มคอลัมน์แล้วไม่มีใครอ่าน checker ไม่ฟ้อง | เพิ่ม 4 entity เข้า `ENTITIES` (property set เฉพาะค่าตั้ง · หน้า `recurring.html`/`email-schedule.html`/`bank.html`) + ใส่ baseline ว่างสำหรับชุดนี้ (ตอนนี้ทุกตัวมีผู้อ่าน จึงไม่ต้องเพิ่มแถว baseline) | S |
| F-8 | **ต่ำ** | `NotificationEngine.cs:74-78` vs `:84-87` | คีย์ที่ระบุ `RecipientUserId` (เช่น `RecurringApprovedWithNotes`): บริษัทปิดทุกช่องของคีย์แล้ว ⇒ ยังส่งตรงถึงผู้ใช้ด้วยค่าตั้งต้น (เงื่อนไข `settings.Count == 0` ตรวจก่อน `savedRows.Count == 0`) — คลาสเดียวกับบั๊ก (3) ของรอบ 249 ที่แก้เฉพาะเส้นปกติ | สลับลำดับ: ถ้า `savedRows.Count > 0 && settings.Count == 0` ⇒ return (ปิดจริง) ก่อนสาขา `RecipientUserId` · เทสต์ Db เพิ่มเคส "ปิดทุกช่อง + ระบุผู้รับ ⇒ ไม่ส่ง" | S |
| F-9 | **ต่ำ** (วินัยเอกสาร) | `DOCUMENT_FLOW.md` มี `commit <เติมในคอมมิตตามหลัง>` ค้าง 22 จุด (เช่น `:3466, :7768-7769`) · `CHANGELOG.md` 27 จุด | sha ของรอบ 241/244/246 ยังไม่ถูกเติม ⇒ `doc_commit_sha_check` อ้างอิงไม่ได้ · ผู้อ่านไม่รู้ว่าข้อความ "Last verified" ผูกกับคอมมิตไหน | เติม sha ในคอมมิตตามหลัง (ตาม F2 ข้อ 10) — งาน doc ล้วน | S |

---

## ตรวจแล้วไม่ใช่บั๊ก (ห้ามรายงานซ้ำ)

1. **WHT ลบซ้ำ (244-7)** — `AmountsFor(balanceDue, wht, 0)` คืน `Net = balanceDue` และ `Gross = balanceDue + wht` (`DocumentPayLinkPolicy.cs:160-166`) · `BalanceDue` เป็นยอดสุทธิอยู่แล้ว · handler ส่ง `WithholdingTaxAmount = whtRemaining` เฉพาะเมื่อเงินเข้า = ยอดค้าง (`:170-174`) · ทดลองรันคิด `BalanceDue = total − wht` แล้ว `AmountsFor(total − wht, wht)` สอดคล้อง (`RecurringAutopilotService.cs:178, :200`).
2. **`EnsureLinkAsync` ส่ง `companyAttach: true`** (`DocumentPayLinkService.cs:203`) — ถูกต้อง: เมธอดนี้ตอบ "ช่องทางไหน" เท่านั้น · "แนบไหม" ตัดสินที่ `ForScheduledEmailAsync :270-277` ก่อน · ปุ่ม "สร้างลิงก์" บนหน้าเอกสาร (`DocumentAutoSendController.cs:69`) ตั้งใจให้ออกได้เสมอ.
3. **`WithRecurringPayLayer` แก้แถว `EmailScheduleRule`** — แถวโหลดด้วย `AsNoTracking` (`:208`) และใน `RecheckDocumentAtSendAsync :844` เช่นกัน ⇒ ไม่ถูกบันทึกกลับ.
4. **ใบจำลองของทดลองรันตั้ง `TotalAmount = Sub+VAT`** (`RecurringAutopilotService.cs:198`) ต่างจากใบจริง (`−WHT`) — ใช้เฉพาะรวบรวมคำเตือน · ยอดในผลบอกชัดว่า "ประมาณการ" (`:296`) · ไม่ persist.
5. **`PostPaymentAutomationService.EnqueueAsync` กลืน exception** (`:91-96`) — เงิน commit แล้ว การชำระต้องไม่ล้ม · ไม่มีแถวงาน = ตรวจได้ที่หน้าเอกสาร (`StatusAsync.Runs`) · ยอมรับได้ (ต่างจาก F-1 ที่ไม่มีที่ให้ดู).
6. **`OnBehalf` ของ e-Tax** — `SendReceiptAsync :351` ส่ง `plan.OnBehalf && !etaxRoute` ⇒ เส้น e-Tax ไม่มีข้อความ "ในนาม NextAcc" (N4) ✓ · ด่านผู้ส่งปฏิเสธแพลตฟอร์มอยู่แล้ว.
7. **`DispatchAsync` ไม่มีใน `CreateMultiDocPaymentAsync`** — grep `DispatchAsync|PaymentNotificationText` ช่วง `:15700-16100` = 0 · เส้นกระจายหลายใบไม่เคยแจ้งเตือน (ไม่ใช่ถดถอยของรอบ 249) — ถ้าต้องการให้แจ้ง เป็นฟีเจอร์ใหม่.
8. **QR หน้าชำระในไฟล์ e-Tax** — ตัดทิ้งทั้ง QuestPDF (`PdfGenerationService.cs:122`) และ `ResolvePaymentQrAsync(pdfA:true)` (`PaymentQr.cs:83`) · ไฟล์ e-Tax by Email ใช้ `GenerateDocumentPdfAsync` เดียวกัน (`DocumentEmailService.cs:447-449`).
9. **นโยบายผู้ติดต่อไม่มีผลถ้ารายการไม่รับรอง** — ตั้งใจ (HIGH/MED-2 · `AutoApprovePolicy.Effective :140-145`) · Db test `:268` ล็อก.
10. **`ApiKey` ตั้งนโยบาย/ช่องทางไม่ได้** — `[RejectApiKey]` ครบ 3 endpoint (`RecurringController.cs:82, :105, :118`) · `owner_action_wiring_check` R248-K1..K3.
11. **ความพร้อม/ทดลองรันเป็น POST ที่ไม่เขียน** — `write_permission_gate` รอบ 248 (`ca10f623`) + Db test `:106` ล็อกว่าไม่มีแถวใหม่.
12. **LINE กลุ่มบริษัทรับแจ้งเตือน** — `NotificationEngine.DispatchCompanyGroupAsync :402-416` → `LineNotifyService.TryPushCompanyGroupAsync` (OA ของบริษัท) · ไม่มีถอยไปกลุ่มกลาง (ตรวจรอบ 249 แล้ว).
13. **`AutopilotSettingsResolver` ชั้น Inherit + ช่องทาง** (`:83-84`) — ตั้งเฉพาะช่องทางโดยไม่ตัดสินเรื่องแนบ = ตั้งใจ (คอมเมนต์ · เทสต์ `AutopilotRound248Tests.cs:180-181`).
14. **`Document.AutoSendRuleId` ประทับโดยตารางแรก** (`EmailScheduleService.cs:779-781` · เงื่อนไข `AutoSendRuleId == null`) — กฎเสมือน (`Id = Guid.Empty`) ไม่ประทับ ⇒ ใบที่ส่งด้วยธง `AutoSendEmail` ใช้ค่าบริษัทสำหรับงานหลังรับชำระ (ตรงกับ "หลังรับเงินต่อรายการ = ยังไม่ทำ" ใน CHANGELOG รอบ 248).

---

_สิ่งที่ไม่ได้ตรวจในรอบนี้_: การล็อกงวดบัญชีตอน `CreatePaymentAsync` ลงวันที่ย้อนหลัง (อยู่ในเส้นเดิมก่อนรอบ 241) · เส้นภาษีซื้อของใบกำกับอย่างย่อ (ไม่ถูกแตะ) · เนื้อหา `AUTOPILOT_BILLING_DESIGN.md`/`DECISIONS.md` เทียบโค้ดทีละข้อ (อ่านเฉพาะ CHANGELOG + DOCUMENT_FLOW §2.12g-i)._
