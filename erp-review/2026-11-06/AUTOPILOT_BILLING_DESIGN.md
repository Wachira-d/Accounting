# AUTOPILOT_BILLING_DESIGN.md — "ตั้งครั้งเดียว ลูกค้าจ่ายแล้วระบบจัดการจนจบ" (รายการประจำ · ใบเสนอราคา → รับเงิน → ใบเสร็จ/ใบกำกับ → ส่งเข้าระบบคู่ค้า → 50 ทวิ กลับ)

> ทีมวิเคราะห์กระบวนการ + product/UX · 2026-11-06 · **อ่านอย่างเดียว ไม่ได้แก้โค้ด** · อ้างไฟล์ใต้ `Accounting/` ที่ HEAD `ca8acbe8` (รอบ 247)
> คำขอเจ้าของ (2026-11-06): "รายการเกิดซ้ำเป็นใบแจ้งหนี้ หรือเริ่มจากใบเสนอราคา · ลูกค้าโอนผ่าน gateway ⇒ ระบบสร้างเอกสารอัตโนมัติและส่งทันที ·
> ถ้าลูกค้าใช้ NextAcc ก็ยัดเข้าระบบลูกค้า · ระบบลูกค้าออกใบหัก ณ ที่จ่ายกลับมาเข้าระบบเรา · เมลแจ้งทุกขั้น · ตั้งค่ารายการประจำละเอียด flexible
> มีตรวจความพร้อม · ปุ่มตรวจว่าลูกค้าใช้ NextAcc · ปุ่มส่งเมลขออนุญาตทำอัตโนมัติ · ลูกค้าที่ไม่ใช้ NextAcc อำนวยความสะดวกให้มากที่สุด"
> + ขอบเขตเพิ่ม (ผ่านผู้ประสานงาน): "ลูกค้าที่ตั้งให้จ่ายแบบโอนเข้าบัญชี (ไม่ใช่ gateway) ทำยังไง ระบบมีเครื่องมือตรงไหน" ⇒ §5
>
> **สร้างต่อจาก (ห้ามขัด):** `AUTO_SEND_WHT_DESIGN.md` (+§11) · `LINE_MULTICHANNEL_DESIGN.md` (+§10) · `LINE_OA_TERMS_CHECK.md` · `NOTIFY_PRICING_ANALYSIS.md` ·
> `EWHT_EVIDENCE_RESEARCH.md` · `ETAX_BY_EMAIL_AUDIT.md` · DECISIONS 240-1…246-QR-10 (`erp-review/2026-09-29/DECISIONS.md:295-351`)
>
> ป้าย: ✅ มีจริง (ไฟล์:บรรทัด) · 🟡 มีบางส่วน/มีแต่ไม่ต่อสาย · 📋 ออกแบบในเอกสารนี้ · ⚠️ ต้องยืนยันตัวบท/ผู้ทำบัญชี/ทดสอบ · ❗ ข้อค้นพบที่แก้ข้อความในเอกสารออกแบบเดิม

---

## 0. สรุปสั้นถึงเจ้าของ (TL;DR)

1. **เส้นหลัก "รายการประจำ → ลิงก์ชำระ → gateway → ใบเสร็จ/ใบกำกับ → อีเมล" มีชิ้นส่วนครบ ~70% แล้ว** (รอบ 241/244/246/247) — ที่ขาดคือ "ตัวต่อ" ไม่ใช่ "ตัวทำงาน":
   รายการประจำยังตั้งลิงก์ชำระ/ช่องทาง/หลังรับชำระ**ต่อรายการ**ไม่ได้ (ใช้ค่าบริษัท) · อนุมัติอัตโนมัติติดคำเตือน "ลูกค้าค้างชำระ" แล้วหยุด (ไม่มีทางให้เจ้าของเลือกปล่อยผ่าน) ·
   ไม่มีแผงความพร้อม/ทดลองรัน · ไม่มีอีเมลสถานะถึง "เรา" ทุกขั้น · ไม่มีหน้า Autopilot รวม
2. ❗ **NextAcc↔NextAcc มีโครงอยู่แล้ว** (`TradingPartnership` + `CrossTenantDocumentLink` + `WorkflowAutomationConfig` — `Models/Entities/CrossTenantWorkflow.cs:22-128` ·
   `Services/Implementations/CrossTenantWorkflowService.cs` 710 บรรทัด · `Controllers/CrossTenantWorkflowController.cs` 321 บรรทัด · เรียกจาก `DocumentService.cs:7606` ตอนอนุมัติ)
   — `AUTO_SEND_WHT_DESIGN.md` §1.1 แถว Intercompany เขียนว่า "**ไม่มี** PartnerConnection … ต้องสร้างใหม่" **ผิด** (หลักการ #10: ผลตรวจของทีมต้อง verify) ⇒ ตาราง `TenantPartnerLink` ที่ออกแบบไว้ (§7.1 ของเอกสารนั้น) **ไม่สร้าง** — ขยาย `TradingPartnership` แทน (ตัวตั้งตัวเดียว · หลักการ #4)
   แต่ของเดิม**ยังใช้จริงไม่ได้**: ไม่มีหน้าจอเลย (ไม่มี `.html` ใดเรียก `trading-partners`/`incoming-documents`) · เชิญแล้วบอกว่า "ไม่พบบริษัท…ในระบบ" = **รั่วว่าใครใช้ NextAcc** (`CrossTenantWorkflowController.cs:76-78`) ·
   จับคู่ด้วยเลขภาษีอย่างเดียวไม่ดูสาขา (`CrossTenantWorkflowService.cs:48-49,163-164`) · ไม่มีขอบเขตความยินยอม · ใบแจ้งหนี้/ใบเสร็จที่ส่งไปแค่ "เข้ากล่อง" ไม่กลายเป็นใบซื้อฝั่งคู่ค้า · ล้มแบบ `LogWarning` (`DocumentService.cs:7606-7608`) · ยอมรับคู่ค้าได้โดยไม่ตรวจสิทธิ์ (`CrossTenantWorkflowController.cs:81-87` ไม่มี `RequirePermission`)
3. **ปุ่ม "ตรวจว่าลูกค้าใช้ NextAcc"** ทำได้แบบไม่รั่ว: ตอบได้แค่สถานะ **ความสัมพันธ์** (เชื่อมแล้ว / รอตอบ / ยังไม่เชื่อม) ไม่ตอบสถานะ **การเป็นสมาชิก** — ยกเว้นคู่ค้าเปิด "ให้คู่ค้าค้นเจอ" ไว้เอง (คำถาม Q1) · ปุ่ม "ส่งคำขอเชื่อมอัตโนมัติ" ส่งอีเมลเหมือนกันทุกกรณี — ใช้ NextAcc อยู่ = ล็อกอินกดยอมรับ · ไม่ใช้ = พาไปพอร์ทัลลูกค้า (เส้นไม่ใช้ NextAcc §8)
4. **ทางกฎหมายที่คุมทั้งหมด:** ใบกำกับภาษีที่ "ยัดเข้าระบบคู่ค้า" **ไม่ใช่ใบกำกับภาษีต้นฉบับโดยตัวมันเอง** — ต้นฉบับยังเป็น (ก) กระดาษ หรือ (ข) e-Tax (by Email ที่ ETDA ประทับเวลา / e-Tax & e-Receipt) ⇒ ใบซื้อฝั่งคู่ค้าต้องมีธง "หลักฐานภาษีซื้อ" ของตัวเอง (§9 L-2) ·
   50 ทวิ ต้องออกโดย **ผู้จ่าย** ในเลขรัน/ภ.ง.ด. ของผู้จ่ายเสมอ (ม.50 ทวิ — ตาม `AUTO_SEND_WHT_DESIGN.md` §3.5 L-2) ⇒ ระบบคู่ค้า "ออกให้อัตโนมัติ" ได้ก็ต่อเมื่อ**ผู้มีสิทธิ์ฝั่งคู่ค้าเปิดนโยบายนั้นเอง**
5. **เส้นโอนเข้าบัญชี (ไม่ใช่ gateway)** — ช่องว่างใหญ่สุดคือ **ตัวจับคู่ธนาคารจับกับ "การชำระที่บันทึกไว้แล้ว" ไม่ใช่ "ใบแจ้งหนี้ที่ค้าง"** (`BankService.cs:764-790` อ่าน `_db.Payments`) ⇒ เงินเข้า statement แล้ว ใบยังค้างจนกว่าคนบันทึกรับชำระ ·
   ออกแบบตัวตัดสินเดียว `BankReceiptAutoSettle` (§5): ยอดตรงสุทธิหลัง WHT + มีตัวระบุตัว (เลขใบ/อ้างอิงลิงก์/ผู้โอนตรงผู้ติดต่อ) + ยอดไม่ซ้ำในชุดใบค้าง ⇒ บันทึกรับชำระอัตโนมัติ (ที่มา = รายการเดินบัญชีจริง ไม่ใช่สลิป) ⇒ เข้าเส้นหลังรับชำระเดิม · **สลิปที่ลูกค้าส่งมาเป็นแค่ข้อบ่งชี้** ไม่มีวันปิดหนี้เอง
6. **แผน:** รอบ 248–256 (§12) · ค่าสูงสุดเร็วสุด = **248 รายการประจำแบบ Autopilot (ตั้งค่า + ความพร้อม + ทดลองรัน + นโยบายอนุมัติ)** → 249 อีเมลสถานะ + หน้า Autopilot + ทางรับชำระทุกทางเข้างานหลังรับชำระ → 250 โอนเข้าบัญชีปิดหนี้อัตโนมัติ → 251–253 คู่ค้า NextAcc → 254 พอร์ทัลลูกค้าที่ไม่ใช้ NextAcc …

---

## 1. สภาพปัจจุบัน — แผนที่โค้ด (ห้ามเขียนซ้ำ · reuse)

### 1.1 ชิ้นส่วนที่มีแล้ว

| ขั้น | ชิ้น | ที่อยู่ | สถานะ / หมายเหตุ |
| --- | --- | --- | --- |
| สร้างใบซ้ำ | `RecurringTransaction` | `Models/Entities/RecurringTransaction.cs:9-83` — `AutoApprove:37` · `AutoSendEmail:47` (บังคับคู่ AutoApprove) · `LastRunOutcome:63` · ค่าปรับล่าช้า `:69-83` | ✅ · ฟอร์มเป็น modal เดียว `wwwroot/pages/recurring.html:35-160` (ยอดเดียว `fAmount:76` · ใบหลายบรรทัดมีคำใบ้ `fMultiLineHint:79`) |
| | `RecurringTransactionService` | `ExecuteRecurringAsync:409` · `CreateDocumentFromTemplateAsync:614` · อนุมัติอัตโนมัติ `:824-880` (`ApprovalAckSource.Unattended`) · ส่งอีเมล `:887-905` → `EmailScheduleService.OnRecurringDocumentCreatedAsync:186` | ✅ ล้มดังบนแถว (`RecordRunFailureAsync:529`) |
| | ผลรอบ/ความพร้อมอีเมล | `Helpers/RecurringRunOutcome.cs:49` (`RecurringEmailReadiness`) · `:67` (`RecurringRunOutcome`) | ✅ รอบ 247 — ต้นแบบของแผงความพร้อม §4.4 |
| อนุมัติไม่มีคน | `ApprovalAcknowledgement` | `Helpers/ApprovalAcknowledgement.cs:9-26` (แหล่ง None/User/SystemWorkflow/ApiClient/**Unattended**) · `PassesWithoutPerson:62-71` | ✅ ตัวตัดสินเดียว — คำเตือน "ลูกค้าค้างชำระ" (`DocumentService.cs:22636-22650` ไม่มีป้ายรหัส) **ไม่อยู่ในชุดผ่าน** ⇒ ใบประจำหยุดเป็นร่าง |
| ส่งตามตาราง | `EmailScheduleRule`/`EmailQueue` | `Models/Entities/EmailSchedule.cs` · `EmailScheduleService.cs:58` (อนุมัติ) · `:186` (ประจำ) · `:386` (ใกล้/เกินกำหนด) | ✅ |
| ผู้รับ | `ContactNotifyEndpoint` + `ContactRecipientResolver` | `Services/Implementations/ContactRecipientResolver.cs:27` · `Helpers/ContactRecipientRule.cs:17` · `NotifyTopic` (`Helpers/NotifyEndpointKind.cs:9-23`: Document/Dunning/Receipt/Wht/Sign/Other) | 🟡 L1 — ยังไม่ต่อเข้าเส้นส่ง (245-L1-7) · `PostPaymentAutomationService.cs:260` ยังใช้ `contact?.Email` |
| ผู้ส่งอีเมล | `OnBehalfEmailSender` · `PlatformEmailQuota` | `Helpers/` · `DocumentEmailService.SendDocumentRoutedAsync:185` | ✅ 240-4 · 241-9 |
| e-Tax | `EtaxDeliveryRouter` · `EtaxByEmailSenderGate` · `EtaxByEmailFormat` | `Helpers/EtaxDeliveryRouter.cs:31-36` · `DocumentEmailService.SendEtaxByEmailAsync:351` | ✅ 244-1…244-8 (by Email เฉพาะ SMTP บริษัทที่ทดสอบผ่าน · ระบบเดียว) |
| ลิงก์ชำระ | `DocumentPayLinkPolicy` · `DocumentPayLinkService` | `Helpers/DocumentPayLinkPolicy.cs:19-96` · `Services/Implementations/DocumentPayLinkService.cs:192` (`EnsureLinkAsync`) · `:248` (`ForScheduledEmailAsync`) · `:276` (`PayQrFactsAsync`) · `Document.PayLinkChannelsJson` (`Document.cs:254`) | ✅ 241-1…241-4 · 244-7 (ยอดค้าง = ยอดสุทธิหลัง WHT แล้ว) |
| หน้าชำระ | `PublicPaymentController` | `Controllers/PublicPaymentController.cs:39` · `documents/{token}:174` · `…/intents:184` · `pay-document.html` | ✅ |
| gateway | `PaymentIntentService` · Omise · webhook | `Services/Payments/PaymentIntentService.cs:204` (Start) · `:398` (ApplyCharge) · `:674` (Refresh = ถามผู้ให้บริการซ้ำ) · `Controllers/PaymentGatewayController.cs:912` (`api/pay/webhooks`) · `Helpers/GatewayWebhookRoute.cs` · `Services/Payments/Providers/OmisePaymentProvider.cs` | ✅ ยืนยันแบบของเจ้านั้น (re-fetch) |
| บันทึกรับเงิน gateway | `DocumentPaymentHandler` | `Services/Payments/Handlers/DocumentPaymentHandler.cs:82-111` (`WhtForPayment` → `CreatePaymentAsync` → `EnqueueAsync`) | ✅ |
| โอน + สลิป (ผ่าน intent) | `ManualSlipPaymentProvider` | `Services/Payments/Providers/ManualSlipPaymentProvider.cs` (Succeeded ได้ทางเดียวคือคนกดยืนยัน · `SettlesDirectlyToBank`) | ✅ |
| หลังรับชำระ | `PostPaymentAutomationService` | `Services/Implementations/PostPaymentAutomationService.cs:65` (Enqueue) · `:99` (ProcessDue) · `:131` (Retry) · ขั้น `IssueReceipt:276` → `IssueReceiptForPaymentAsync` (`DocumentService.cs:14716`) · `EmailReceipt:299` · ด่าน §86/4 `:246` | ✅ 241-5…241-11 · **ผู้เรียกมี 2 จุดเท่านั้น**: `DocumentPaymentHandler.cs:111` · `DocumentController.cs:1721` (G-6 ของ AUTO_SEND ยังเปิด) |
| ใบเสร็จ/ใบกำกับ | `SettlementReceiptPolicy` · `ReceiptIssuePolicy` | `Helpers/SettlementReceiptPolicy.cs:16` · `Helpers/ReceiptIssuePolicy.cs:22` | ✅ tax point §78/1 · gap-free |
| QR | `PaymentQrPolicy` · `PaymentQrService` | `Helpers/PaymentQrPolicy.cs:20` · `Services/Implementations/PaymentQrService.cs:156-228` | ✅ 246-QR |
| ลิงก์เซ็น/ยอมรับ | `PublicQuotationController` · `SignRequestService` · `SignEventOutbox` | `Controllers/PublicQuotationController.cs:152` (accept) · `:224` (outbox) · `Services/Implementations/SignRequestService.cs:82,132` | ✅ 240 · **ยอมรับแล้วไม่แปลงเป็นใบแจ้งหนี้อัตโนมัติ** (ไม่มีผู้เรียก `ConvertDocumentAsync` `DocumentService.cs:13335` จากเส้นนี้) |
| แปลงอัตโนมัติ (ภายใน) | `SignatureApprovalService` | `Services/Implementations/SignatureApprovalService.cs:504,584` (`AutoConvert` → "ConvertToInvoice") | ✅ เป็นต้นแบบ |
| 50 ทวิ (เราออก) | `WithholdingTaxCertService` | `IssueAsync:354` · `VoidAsync:413` | ✅ (ฝั่งผู้จ่ายของคู่ค้าจะใช้ตัวนี้) |
| 50 ทวิ (เรารับ) | `WhtCreditService` · `WhtCreditReceived` | `Services/Implementations/WhtCreditService.cs:330` (MarkReceived) · ตัดสูญรอบ 243a | 🟡 กล่องรับ `WhtCertIntake` **ยังไม่มีในโค้ด** (เฟส 244 ของ AUTO_SEND ยังไม่ทำ) |
| พอร์ทัลลูกค้า | `PortalController` · `PortalService` | `Controllers/PortalController.cs:51-110` (payments/documents/pdf/statement/payment-options/upload-slip) · `PortalService.UploadDocumentSlipAsync:507-582` | 🟡 ⚠️ อัปสลิปสร้างแถว `Payment` ทันทีพร้อม `Notes="[PENDING-VERIFY]…"` (`PortalService.cs:563-579`) โดยไม่มีสถานะรอตรวจในตัว entity — ต้องตรวจว่าแถวนี้กระทบ `PaidAmount/BalanceDue` หรือไม่ก่อนต่อสายอัตโนมัติใด ๆ (§5.3 ข้อ G-T7) |
| คู่ค้า NextAcc | `TradingPartnership` ฯลฯ | §0 ข้อ 2 · `CrossTenantWorkflowService.cs:44` (Invite) · `:90` (Accept) · `:137` (`OnDocumentSentAsync`) · `:197` (ApproveIncoming) · `:331` (TryAutoApprove — เฉพาะ QuotationFlow) · `:376` (chain PO/Invoice) | 🟡 backend อย่างเดียว · ช่องโหว่ §6.1 |
| ธนาคาร | statement/feed/จับคู่ | `BankService.StatementImport.cs:28,285` (นำเข้าแล้ว `AutoMatchAsync`) · `BankService.cs:764` (`AutoMatchAsync` — **ผู้สมัคร = Payments ที่มีอยู่**) · `BankService.MatchCandidates.cs:27,344` · `BankFeedService.cs:380-440` (AI/นักเรียนเสนอ **เอกสารค้าง** → `SuggestedDocumentId` · ด่าน `BankMatchArbiter.ScreenAiProposals`) · `OpenBankingService.cs:110,188,226` · `Controllers/BankController.cs:151` (auto-match) · `:195` (bulk-ai-match → `IBulkBankAiMatchService`) · `Helpers/BankMatchScorer.cs` · `BankMatchArbiter.cs` · `BankMatchCurrency.cs` | 🟡 §5 |
| OCR สลิป | `SlipOcrAssistService` | `Services/Implementations/SlipOcrAssistService.cs:1-61` (Tesseract ในบ้าน · rule-based · "advisory — ไม่ auto-approve") | 🟡 ใช้กับสลิปค่าบริการแพลตฟอร์มเท่านั้น |
| API ภายนอก | v1 | `Controllers/V1/DocumentsV1Controller.cs:90` (create + `signCallbackUrl`) · `:287` approve · `:360` sign-link | ✅ |
| Webhook ขาออก | `WebhookService` + `WebhookSignature` + `OutboundUrlGuard` | 240-2/240-3/240-10 | ✅ ใช้เป็นช่อง "แจ้งระบบลูกค้าที่ไม่ใช่ NextAcc" ได้ (§8) |
| งานเบื้องหลัง | `Helpers/JobLock` · SKIP LOCKED | — | ✅ |
| Intercompany | `IntercompanyService` | — | **คนละเรื่อง** (กลุ่มบริษัทเดียวกัน) · ไม่ใช้ |

### 1.2 ช่องว่าง (เรียงตามมูลค่าต่อเจ้าของ)

| # | ช่องว่าง | ผล | แก้ในรอบ |
| --- | --- | --- | --- |
| A-1 | รายการประจำตั้ง "ลิงก์ชำระ/ช่องทาง/หลังรับชำระ/ผู้รับ" ต่อรายการไม่ได้ — ใช้ค่าบริษัท (241-1 "recurring แบบติ๊ก AutoSendEmail = ตามค่าบริษัท") | ลูกค้า A จ่ายบัตร ลูกค้า B โอนเท่านั้น ตั้งไม่ได้ | 248 |
| A-2 | อนุมัติอัตโนมัติหยุดเพราะคำเตือน "ลูกค้าค้างชำระ > 30 วัน" (`DocumentService.cs:22636-22650`) · ไม่มีทางเลือก "ปล่อยผ่านพร้อม audit" | ใบประจำค้างร่าง ส่งไม่ออก ทั้งที่เจ้าของอยากให้ออก | 248 |
| A-3 | ไม่มีแผงความพร้อม/ทดลองรัน — ผู้ใช้ไม่รู้ว่าจะเกิดอะไรรอบหน้า | ตั้งแล้วพังเงียบตอนรัน | 248 |
| A-4 | ทางรับชำระอื่นไม่เข้าเส้นหลังรับชำระ (ผู้เรียก `EnqueueAsync` = 2 จุด) | โอน/settlement/สลิป ไม่ออกใบเสร็จอัตโนมัติ | 249 |
| A-5 | ไม่มีอีเมลสถานะถึง "เรา" ทุกขั้น + ไม่มี digest | เจ้าของต้องเข้าระบบมาดู | 249 |
| A-6 | ไม่มีหน้า Autopilot รวม "ใบนี้อยู่ขั้นไหน · ติดอะไร" | ตามงานไม่ได้ | 249 |
| A-7 | ใบเสนอราคายอมรับออนไลน์แล้วไม่แปลงเป็นใบแจ้งหนี้ | เส้น B หยุดกลางทาง | 249 |
| A-8 | เงินเข้า statement ไม่ปิดใบแจ้งหนี้เอง (จับคู่กับ Payment ที่บันทึกแล้วเท่านั้น) | เส้นโอนเข้าบัญชีต้องคนทำทุกใบ | 250 |
| A-9 | คู่ค้า NextAcc: ไม่มี UI · รั่วสมาชิกภาพ · ไม่ดูสาขา · ไม่มีขอบเขตยินยอม · ไม่สร้างใบซื้อฝั่งคู่ค้า · ล้มเงียบ · ไม่ตรวจสิทธิ์ | ใช้ไม่ได้/ไม่ปลอดภัย | 251–253 |
| A-10 | 50 ทวิ กลับจากคู่ค้า ต้องมีกล่องรับ (AUTO_SEND เฟส 244) ก่อน | เส้น ③ ไม่มีที่ลง | 252 (พึ่ง AUTO_SEND 244) |
| A-11 | ลูกค้าไม่ใช้ NextAcc: พอร์ทัลต้องมีรหัสผ่าน · แก้ข้อมูลภาษีเองไม่ได้ · ไม่มีไฟล์ส่งออกให้นักบัญชีลูกค้า | งานพนักงานซ้ำ | 254 |

---

## 2. เป้าหมาย end-to-end (sequence ในรูปข้อความ)

สัญลักษณ์: `S` = เรา (ผู้ขาย) · `C` = ลูกค้า (คน/อีเมล) · `P` = ลูกค้าที่ใช้ NextAcc (บริษัทผู้เช่าอีกราย) · `GW` = ผู้ให้บริการชำระ (Omise) · `RD/ETDA` = กรมสรรพากร/ETDA ·
`[S→เรา]` = อีเมล/แจ้งเตือนสถานะถึงทีมเรา (§10) · `[→C]` = ถึงลูกค้า

### 2.A รายการประจำ (ใบแจ้งหนี้) → จ่ายผ่าน gateway → จบ

```
T-1d  RecurringJob (JobLock)  ── ตรวจความพร้อม (AutopilotReadiness §4.4) ──┐
                                 ❌ ใดก็ตาม ⇒ ไม่สร้าง? ไม่ — สร้างตามเดิม แต่ [S→เรา] "รอบพรุ่งนี้จะติด: …" + ปุ่มแก้     (ไม่บล็อกการสร้าง — บล็อกเฉพาะขั้นที่ข้อมูลไม่พอ)
T0    CreateDocumentFromTemplateAsync (RecurringTransactionService.cs:614) → ใบแจ้งหนี้ร่าง DRAFT-{guid}
      → ApproveDocumentAsync(ApprovalAckSource.Unattended + AutoApprovePolicy ของรายการนี้ §4.3)   ⇒ เลขจริง gap-free (§86/4 · M)
            ├ ติดคำเตือนที่นโยบายไม่ปล่อย ⇒ ค้างร่าง + RecurringRunOutcome (รอบ 247) + [S→เรา] E-02 "อนุมัติไม่ได้ เพราะ…"  จบ
            └ ผ่าน ⇒ audit APPROVE-UNATTENDED-POLICY (§4.3)
      → EnsureLinkAsync(ช่องทางของรายการ) (DocumentPayLinkService.cs:192) + PayQrFacts (QR พร้อมเพย์ยอดสุทธิหลัง WHT — 246-QR-4)
      → ChannelDispatcher (L2) / SendDocumentRoutedAsync (DocumentEmailService.cs:185) ถึงผู้รับจาก ContactRecipientResolver(NotifyTopic.Document)
            อีเมล: PDF ใบแจ้งหนี้ + ยอดเต็ม/WHT/ยอดโอน + ลิงก์ชำระ + QR · LINE (L2): การ์ด + ปุ่มชำระ
            └ ถ้า P เชื่อมคู่ค้าและเปิดขอบเขต "รับเอกสารขาย" ⇒ PartnerExchangeOutbox(InvoiceDelivered) §6.4 ⇒ ใบซื้อร่างในบัญชี P
      [S→เรา] E-03 "ส่งใบแจ้งหนี้ INV-… ถึง … แล้ว" (หรือรวมใน digest)
T+n   C เปิดลิงก์ → pay-document.html → เลือกวิธี (บัตร/พร้อมเพย์ Omise) → PaymentIntentService.StartAsync (:204)
      GW → webhook api/pay/webhooks/{provider}/{token} → re-fetch charge (Refresh :674 / ApplyCharge :398) — ยืนยันจากผู้ให้บริการเท่านั้น
      → DocumentPaymentHandler: WhtForPayment → CreatePaymentAsync (ยอด + WHT ที่เหลือ) → JE · ทะเบียน WhtCreditReceived (Pending) จาก GL 11910
      → PostPaymentAutomationService.EnqueueAsync (หลัง commit)
      ProcessDue (SKIP LOCKED):
        1 IssueReceipt → IssueReceiptForPaymentAsync — SettlementReceiptPolicy ตัดสิน ใบเสร็จ vs ใบเสร็จ/ใบกำกับภาษี ณ วันรับเงิน (§78/1 บริการ)
                          · ใบต้นทางเป็นใบกำกับแล้ว (ขายสินค้า §78) ⇒ ใบเสร็จเปล่า · VAT ยกเว้น ⇒ ใบเสร็จเปล่า
        2 EmailReceipt → SendDocumentRoutedAsync → EtaxDeliveryRouter:
                          บริษัทโหมด by Email + SMTP ทดสอบผ่าน ⇒ อีเมล e-Tax (To 1 · CC ETDA · หัวเรื่อง EtaxByEmailFormat) — 244-3/244-5
                          ไม่ใช่ ⇒ อีเมลเอกสารปกติ (PDF)
        3 (📋) PartnerDeliver → ถ้าเชื่อม P: ReceiptDelivered (ใบซื้อของ P ถูกปิดด้วยการจ่าย §6.5)
        4 RequestWhtCert → ถ้ามี WHT และไม่ได้เชื่อม P ขอบเขต WHT ⇒ อีเมลขอ 50 ทวิ + ลิงก์อัปโหลด (AUTO_SEND §3.2) · เชื่อมแล้ว ⇒ ไม่ส่งคำขอ (รอ event จาก P)
      [→C] E-11 ใบเสร็จ/ใบกำกับ  ·  [S→เรา] E-05 "รับเงิน 10,400 · ออก RT-… · ส่งแล้ว"
T+n'  ฝั่ง P (ถ้าเชื่อม + ขอบเขต WhtAutoIssue + นโยบาย P อนุญาต): บันทึกจ่ายบนใบซื้อ (§6.5) → WithholdingTaxCertService.IssueAsync (เลขรัน P · ภ.ง.ด.53 P)
      → PartnerExchangeOutbox(WhtCertIssued) → WhtCertIntakeService.SubmitAsync(S, Channel=NextAccLink) → ด่านเชิงกำหนด + ยืนยันอัตโนมัติ (AUTO_SEND §11 ข้อ 1+2)
      → WhtCreditReceived = Received   [S→เรา] E-07 "ได้ 50 ทวิ เลขที่ … จาก P"
      ไม่เชื่อม ⇒ ทวง 7/21/45 วัน + รายเดือน ≤ 6 (AUTO_SEND §11 ข้อ 4) ผ่านลิงก์อัปโหลด/พอร์ทัล/e-WHT
T+close  กระทบยอด: รอบโอน GW (GatewaySettlementService / Settlement batch) ล้าง 11340 · ทะเบียน 50 ทวิ ↔ GL 11910 (WhtCreditService.SummaryAsync) · หน้า Autopilot ใบนี้ = "จบ ✅"
```

### 2.B ใบเสนอราคา → ยอมรับ/เซ็น → แปลง → เหมือน A

```
ใบเสนอราคาอนุมัติ → (240-7 งานเบื้องหลัง ~10 นาที) อีเมลลิงก์ยอมรับ/เซ็นในนามบริษัท (SignRequestService.SendEmailAsync :132)
   └ ถ้า P เชื่อม: QuotationFlow เดิม (CrossTenantWorkflowService.OnDocumentSentAsync) ⇒ P อนุมัติในกล่อง/อัตโนมัติตาม WorkflowAutomationConfig ⇒ PO ใน P (CreateDownstreamPoAsync)
C ยอมรับ (PublicQuotationController.Accept :152) ⇒ SignEventOutbox (QuotationAccept) ⇒ webhook ต้นทาง (240-2)
   📋 QuotationAutopilotPolicy ต่อใบเสนอราคา (ค่าตั้งต้นจากบริษัท): ConvertOnAccept = Off | ToInvoice | ToTaxInvoice | ToDepositInvoice(%) | ToRecurring(รอบ/วันเริ่ม)
   ⇒ งาน QuotationAcceptAutopilotJob (อ่านสถานะ ไม่เกาะเส้น — แบบ 240-7) เรียก ConvertDocumentAsync (DocumentService.cs:13335) — ไม่มีสูตรแปลงที่สอง
      · ยอดในใบเสนอราคาเปลี่ยนหลังลูกค้าเซ็น (`AcceptContentChanged` PdfGenerationService.cs:990) ⇒ **ไม่แปลงอัตโนมัติ** + [S→เรา] "เนื้อหาเปลี่ยนหลังเซ็น"
      · ToRecurring ⇒ สร้าง RecurringTransaction จากบรรทัดใบเสนอราคา (แบบสัญญารายเดือน) — เข้าเส้น A ตั้งแต่รอบแรก
   ⇒ ใบใหม่เดินเส้น A ต่อ (อนุมัติตามนโยบาย → ส่ง + ลิงก์ชำระ …)
```

### 2.C โอนเข้าบัญชี / สลิป / ไม่ผ่าน gateway → เหมือน A แต่มีด่านความมั่นใจ — รายละเอียด §5

```
statement (นำเข้า/feed/OpenBanking) → BankTransaction(credit) → BankReceiptAutoSettle.Decide (pure §5.4)
   AutoSettle ⇒ CreatePaymentAsync(ที่มา BankLine, ผูก MatchedPaymentId) → EnqueueAsync("bank-auto") → เส้น A ขั้น 1–4
   Suggest     ⇒ คิว "เงินเข้าที่รอยืนยัน" (1 คลิก) → ยืนยัน ⇒ ทางเดียวกัน
   None        ⇒ ไม่ทำอะไร (รายการเดินบัญชีค้างตามเดิม)
สลิปจากลูกค้า (พอร์ทัล/LINE/อีเมล) → OCR (SlipOcrAssist ขยาย) → "ข้อบ่งชี้" ผูกกับใบ → **ไม่ปิดหนี้** จนเจอรายการเดินบัญชีจริงหรือคนยืนยัน
```

### 2.D กรณีขอบ (edge cases)

| เหตุ | พฤติกรรมเป้าหมาย | ใช้ของเดิม/ใหม่ |
| --- | --- | --- |
| จ่ายบางส่วน (gateway) | ลิงก์ชำระรับยอดเต็มเท่านั้นวันนี้ (`DocumentPayLinkPolicy.AmountsFor`) · ถ้าเปิด "ผ่อน" ภายหลัง: ใบเสร็จต่องวดตาม `PostPayIncludePartial` (`CompanySettings.cs:343`) · WHT ตามสัดส่วน (`WhtWithheldCounter`) | ✅ ค่าตั้งเดิม · 📋 ผ่อนผ่านลิงก์ = backlog |
| จ่ายเกิน (โอน) | ไม่ปิดเอง · `BankReceiptAutoSettle` = Suggest + เหตุ "เกิน 500.00" · ทางไปต่อ: บันทึกส่วนเกินเป็นเงินรับล่วงหน้า/เครดิตลูกค้า หรือคืนเงิน | §5.5 |
| จ่ายขาด (โอน) | Suggest "ขาด X" — อาจเป็น WHT ที่ลูกค้าหักต่างอัตรา (§5.5 ข้อ c) หรือค่าธรรมเนียมโอน · ไม่ประทับว่าครบ | §5.5 |
| โอนรวมหลายใบ | subset-sum ในชุดใบค้างของผู้ติดต่อเดียว: คำตอบเดียว ⇒ Suggest (ไม่ Auto — สองชุดรวมได้เท่ากัน = Ambiguous แบบ `WhtCertMatchPolicy` AUTO_SEND §3.7) | §5.5 |
| คืนเงิน gateway | `GatewayRefundService.RefundAsync:261` → ใบลดหนี้/ยกเลิกใบเสร็จตามเส้นเดิม → (คู่ค้า) CreditNoteDelivered → ใบลดหนี้ซื้อร่างใน P · 50 ทวิ ที่ออกแล้ว: P ตัดสินเอง (void/ออกแทน) → event → ฝั่ง S กลับเป็นรอใบ (AUTO_SEND §3.5.2) · `CancelAfterRefundAsync` (243-4) | ✅ + 📋 |
| ใบลดหนี้หลังรับเงิน | เส้นเดิม §86/10 (ยอดสะสม ≤ ใบเดิม) · ส่งอีเมล/คู่ค้าตามขอบเขต · ไม่มีเงินคืนอัตโนมัติ (เงินออก = คนกด) | ✅ |
| ลิงก์หมดอายุ/ถูกเพิกถอน | หน้าสาธารณะข้อความเดียว (241-11 S2) · ทวงรอบถัดไปใช้ลิงก์ใหม่อัตโนมัติ (241-4) · [S→เรา] เฉพาะ digest | ✅ |
| gateway ล่ม/ปิดวิธี | `DocumentPayLinkPolicy.Allowed` ตัดสินสดทุกครั้ง ⇒ ช่องหายจากหน้า · ถ้าเหลือ "โอนเข้าบัญชี" ⇒ แสดงบัญชี/QR · **ไม่ส่งลิงก์ที่ใช้ไม่ได้** · แผงความพร้อมเป็น ⚠️ | ✅ |
| webhook หาย | `PaymentIntentService.RefreshAsync:674` + งานถามซ้ำ intent ที่ค้าง (มีใน gateway design) · ห้ามประทับสำเร็จจากหน้าผู้ใช้ | ✅ |
| P ปฏิเสธใบซื้อ | `CrossTenantDocumentLink.Status=Rejected` + เหตุ ⇒ [S→เรา] E-09 ทันที (ไม่ใช่ digest) · ใบของเราไม่ถูกแตะ (ใบกำกับออกแล้วแก้ไม่ได้ — ถ้าผิดจริงต้องออกใบลดหนี้/ยกเลิกแล้วออกใหม่ §86/4) | §6.6 |
| P แก้ข้อมูลในใบซื้อของตัวเอง | เป็นของ P (ผังบัญชี/ศูนย์ต้นทุน) · ห้ามแก้ยอด/VAT/เลขที่ของเรา (ช่องล็อก + เหตุ) · ไม่มี event กลับ | §6.6 |
| WHT ไม่ตรง (P หักต่างจากที่เราคาด) | ฝั่ง gateway: ยอดไม่ตรงสุทธิ ⇒ handler บันทึกตามยอดจริง + ป้าย (241-3 พฤติกรรมเดิม) · 50 ทวิ จาก P ≠ WHT ที่ S บันทึก ⇒ intake ไม่ยืนยันอัตโนมัติ (ด่านเชิงกำหนด) + [S→เรา] | ✅ + AUTO_SEND §3.5 |
| ลูกค้าเลือก "ไม่หัก" บนหน้าชำระ | ได้ + คำเตือนนิติบุคคล + เก็บการยอมรับ (AUTO_SEND §11 ข้อ 3) | 📋 (AUTO_SEND) |
| ต่างประเทศ/ไม่ใช่ THB | ลิงก์ชำระ THB เท่านั้น (241-3) ⇒ ใบสกุลอื่น: ไม่แนบลิงก์ + เหตุในแผงความพร้อม · ลูกค้าต่างประเทศไม่คาด 50 ทวิ (AUTO_SEND L-10) · คู่ค้า NextAcc ต่างประเทศ = ไม่มี (ผู้เช่าเป็นนิติบุคคลไทย) · จับคู่ธนาคารข้ามสกุลไม่ทำ (`BankMatchCurrency`) | ✅ |
| อีเมลเด้ง | ยังไม่มี bounce webhook (240-8) ⇒ แถว Sent ≠ ถึง · แผงความพร้อมแสดง "ส่งล่าสุดสำเร็จเมื่อ…" ไม่ใช่ "ถึงแล้ว" | ✅ ข้อจำกัด |
| โควตาอีเมล/LINE เต็ม | LINE เต็ม ⇒ อีเมลแทน (Q7 ค่าตั้งต้น) · อีเมลระบบเต็ม ⇒ คิว Failed + [S→เรา] + ทางซื้อแพ็ก (L4) · ไม่บล็อกการออกใบ | L4 |

---

## 3. หลักการออกแบบ (10 ข้อ — ใช้ตัดสินทุกหมวด)

1. **ไม่มีตรรกะออกเอกสาร/ลงบัญชีใหม่** — Autopilot คือ "ตัวต่อสาย + ตัวตัดสินนโยบาย" เท่านั้น: ออกใบ = `ApproveDocumentAsync`/`IssueReceiptForPaymentAsync`/`ConvertDocumentAsync` · ส่ง = `SendDocumentRoutedAsync` + Resolver · เงิน = `CreatePaymentAsync` (เหมือน 241-6)
2. **สถานะปลายทางมาจากเหตุการณ์ภายนอกจริงเท่านั้น** (F2 ข้อ 3): "จ่ายแล้ว" = ผู้ให้บริการยืนยัน (re-fetch) หรือรายการเดินบัญชีจริง · "ส่งถึงคู่ค้าแล้ว" = ฝั่งรับ commit แล้ว · "ได้ 50 ทวิ" = ผู้จ่ายออกจริง/ไฟล์ผ่านด่าน · "ประทับเวลา e-Tax" = ไฟล์ ETDA กลับมา (244-6)
3. **ตัวตั้งตัวเดียว**: ความพร้อม = `AutopilotReadiness` (pure) ใช้ทั้งแผง UI · งานรัน · ทดลองรัน · การจับคู่ธนาคาร = `BankReceiptAutoSettle` (pure) ใช้ทั้งงานและหน้ากระทบยอด · ความสัมพันธ์คู่ค้า = `TradingPartnership` ตัวเดิม (ไม่สร้าง `TenantPartnerLink`)
4. **ค่าทับเป็นชั้น**: บริษัท › ผู้ติดต่อ › รายการประจำ/ใบเสนอราคา › ตารางส่ง — resolver เดียว `AutopilotSettingsResolver` (ขยาย `AutoSendSettingsResolver` ที่ AUTO_SEND §9 เฟส 243 ข้อ 7 เสนอ — ยังไม่มีในโค้ด ⇒ สร้างครั้งเดียวในชื่อนี้)
5. **อัตโนมัติไม่บล็อกเงิน**: งานหลังรับเงินล้ม ≠ เงินไม่ลง (241-5) · ทุกขั้นหลังเงินเข้า = outbox/คิว retry ได้ · ล้มดัง 3 ที่ (แถว · แจ้งเตือน · หน้า Autopilot)
6. **ข้ามบริษัทผ่าน service ระบบตัวเดียว** (`PartnerExchangeService`) — เขียนลงบัญชีผู้รับด้วย `CompanyId` ของผู้รับ · ไม่มี HTTP สาธารณะ · ทุกข้อความมี payload hash + eventId คงที่
7. **ไม่รั่วสมาชิกภาพ**: ไม่มี API ใดตอบ "บริษัทเลขภาษีนี้ใช้ NextAcc ไหม" ให้คนที่ยังไม่ใช่คู่ (ยกเว้นคู่ค้าเลือกเปิดเผยเอง Q1)
8. **ผู้จ่ายตัดสินภาษีของผู้จ่าย**: WHT/ประเภทเงินได้ฝั่ง P คิดด้วยตัวตัดสินของ P (`PerLineWht`/`ThaiWhtRateTable`) — ตัวเลขของ S เป็นข้อมูลอ้างอิงเท่านั้น (§54 · AUTO_SEND §3.5.3)
9. **AI = ครู ไม่ใช่ด่าน** (กฎ #1): การจับคู่เงิน/ตรวจ 50 ทวิ/เติม GL ฝั่ง P ใช้ local ก่อน (`BankMatchDistillationModel` · `GlAccountDistillationModel`) · ปิด AI ทั้งหมดแล้วทุกเส้นยังทำงาน (ระดับ "เสนอ" แทน "อัตโนมัติ" ได้ แต่ไม่พัง)
10. **ทุกการปล่อยผ่านที่เจ้าของเลือก = มีร่องรอย**: นโยบายอนุมัติข้ามคำเตือน · ปล่อยไม่หัก WHT · ยืนยันอัตโนมัติ ⇒ `AddChainedAuditLog` + `RuleCode` + ข้อความที่ใช้ตัดสิน

---

## 4. รายการประจำแบบ Autopilot — ออกแบบหน้าตั้งค่าใหม่ (UX/UI)

### 4.1 โครงหน้า: wizard 8 ขั้น (เดินหน้า/ถอยได้ · บันทึกเป็นร่างได้ทุกขั้น) + แผงความพร้อมด้านขวา (มือถือ = แถบล่างพับได้)

```
┌ สร้างรายการประจำ ─────────────────────────────────────────────────────────┬─ ตรวจความพร้อม ─────────────────┐
│ ① เอกสาร&รอบ ② รายการ&ราคา ③ ภาษี ④ ส่งถึงใคร ⑤ รับเงิน ⑥ หลังรับเงิน     │ ระดับ: Autopilot เต็ม 🟢 / บางส่วน 🟡 │
│ ⑦ หัก ณ ที่จ่าย ⑧ อนุมัติ&แจ้งทีม                                          │ ✅ SMTP บริษัททดสอบผ่าน (12 ต.ค.)   │
│ ──────────────────────────────────────────────────────────────────────── │ ✅ e-Tax by Email · อีเมลลงทะเบียน   │
│  (เนื้อหาขั้นปัจจุบัน — ดู 4.2)                                           │ ⚠️ ลูกค้าไม่มีเลขสาขา → ออกใบกำกับ   │
│                                                                          │    เต็มรูปไม่ได้ [แก้ที่ผู้ติดต่อ ↗] │
│                                                                          │ ✅ Omise เปิด: บัตร · พร้อมเพย์      │
│                                                                          │ ✅ QR พร้อมเพย์บัญชีกสิกร xxx-1234   │
│                                                                          │ ❌ ลูกค้าค้างชำระ 2 ใบ > 30 วัน —    │
│                                                                          │    อนุมัติอัตโนมัติจะหยุด [ตั้งนโยบาย ⑧]│
│                                                                          │ ⓘ คู่ค้า NextAcc: ยังไม่เชื่อม        │
│                                                                          │    [ตรวจ/ส่งคำขอเชื่อม]              │
│                                                                          │ ✅ โควตาอีเมลเดือนนี้ 1,240/6,000    │
│ [ยกเลิก]                         [ทดลองรัน 🔍]  [บันทึกร่าง]  [บันทึก ▶] │ [ตรวจใหม่]  อัปเดต 10:14          │
└──────────────────────────────────────────────────────────────────────────┴──────────────────────────────────┘
```

- แผงขวาเรียก `GET …/recurring/readiness?draft=` ทุกครั้งที่ขั้นเปลี่ยน (debounce 600ms) — **เซิร์ฟเวอร์คำนวณทั้งหมด** (หลักการ #5 · JS ไม่มีตารางกติกา)
- "ระดับ Autopilot" 3 ค่า: 🟢 **เต็ม** (สร้าง→อนุมัติ→ส่ง→รับเงิน→ใบเสร็จ→ส่ง ไม่มีคนแตะ) · 🟡 **บางส่วน** (ระบุว่าขั้นไหนต้องมีคน) · 🔴 **สร้างร่างเท่านั้น**
- บันทึกได้เสมอ (รอบ 247: `SaveWarnings` ไม่บล็อก) — ยกเว้นข้อมูลที่ทำให้สร้างเอกสารไม่ได้เลย (ตามด่าน `ValidateTemplateAsync:1044` เดิม)

### 4.2 เนื้อหาแต่ละขั้น

**① เอกสาร & รอบ**
- ชนิด: ใบแจ้งหนี้ · ใบกำกับภาษี (ขายสินค้า — tax point §78 ตอนออก) · ใบแจ้งหนี้/ใบกำกับภาษี · ใบวางบิล · ค่าใช้จ่าย (ฝั่งซื้อ — Autopilot ไม่ใช้) — คำอธิบายใต้ตัวเลือกจากเซิร์ฟเวอร์ "บริการ: ออกใบแจ้งหนี้ก่อน ใบกำกับออกตอนรับเงิน (§78/1) — แนะนำ"
- รอบ: รายวัน/สัปดาห์/เดือน/ไตรมาส/ปี/**ทุก N เดือน**/**วันทำการสุดท้ายของเดือน** (`Helpers/RecurringSchedule.cs` ขยาย) · วันที่ยึด (`PreferredDay`) · เริ่ม/สิ้นสุด/จำนวนครั้งสูงสุด · **สร้างล่วงหน้า N วันก่อนวันเอกสาร** (ส่งก่อนครบกำหนด) · วันครบกำหนด = เครดิตเทอมของผู้ติดต่อ (resolver เดิม) หรือ "วันที่ X ของเดือนถัดไป"
- พรีวิว 6 รอบถัดไป (วันเอกสาร · วันส่ง · วันครบกำหนด) — จาก `RecurringSchedule` ตัวเดียว
- หยุดอัตโนมัติเมื่อ: ผู้ติดต่อถูกปิด/ลบ · สัญญาสิ้นสุด · ค้างชำระเกิน N ใบ (ตัวเลือก — "หยุดออกใบใหม่ให้ลูกค้าที่ไม่จ่าย")

**② รายการ & ราคา**
- **หลายบรรทัด** (แทน `fAmount` ยอดเดียว) · ดึงจากสินค้า/บริการ · ตัวแปรในคำอธิบาย `<<month>>` `<<period_start>>` `<<period_end>>` `<<year_be>>` (มีอยู่แล้ว `_updateVarPreview`) — พรีวิวรอบถัดไป
- ราคาแปรผัน: คงที่ · **ขั้นบันไดตามรอบ** (เดือน 1–3 ราคา A ต่อจากนั้น B) · **ปรับขึ้น X% ทุก 12 เดือน** · **ปริมาณจากแหล่งข้อมูล** (backlog — มิเตอร์/API v1 ส่งปริมาณมาก่อนวันรัน; ไม่มี ⇒ ใช้ค่าตั้งต้นหรือหยุดพร้อมเหตุ)
- ส่วนลด · ค่าปรับล่าช้า (มีแล้ว `LateFee*`)

**③ ภาษี**
- VAT ต่อบรรทัด (7/0/ยกเว้น) · ราคารวม/ไม่รวม VAT · ประเภทเงินได้ + อัตรา WHT ที่**คาด** (คำใบ้ `fWhtRateHint` เดิม) — ข้อความเซิร์ฟเวอร์ "ลูกค้าเป็นบุคคลธรรมดา — ไม่มีหน้าที่หัก (241-11 S3)" / "ลูกค้าต่างประเทศ — ไม่คาด 50 ทวิ"
- ภาษาเอกสาร (resolver เดิม) · หัวเอกสาร/เทมเพลต

**④ ส่งถึงใคร (ช่องทาง)**
- อ่านจาก `ContactNotifyEndpoint` (L1) — แสดงรายการปลายทางของผู้ติดต่อพร้อมหัวข้อที่รับ (Document/Dunning/Receipt/Wht) · ติ๊กช่องทางต่อรายการ: อีเมล ☑ · LINE 1:1 ☐ (L2) · กลุ่ม LINE ☐ (L3) · **ระบบคู่ค้า NextAcc** ☐ (เปิดได้เมื่อ `TradingPartnership` Active + ขอบเขต §6.2)
- ทางถอย: LINE เต็ม/ไม่ถึง ⇒ อีเมล (Q7 ค่าตั้งต้น) · ส่งสองทางพร้อมกันได้
- เวลาส่ง: ทันทีที่อนุมัติ · เวลาที่กำหนด (เช่น 09:00) · ข้ามวันหยุด (`PlatformHolidayReadiness` มีแล้ว)
- แนบ: PDF · ไฟล์ e-Tax · ใบแจ้งยอดค้าง (ตัวเลือก)
- ปุ่มลัด: [เพิ่มอีเมลผู้รับ ↗ หน้าผู้ติดต่อ] — ไม่มีอีเมล ⇒ ช่องอีเมลปิดพร้อมเหตุ (รอบ 247 `RecurringEmailReadiness`)

**⑤ รับเงิน**
- โหมดรับเงิน **ต่อรายการ** (A-1): `ตามค่าผู้ติดต่อ` (ค่าตั้งต้น) · `ออนไลน์ (gateway)` · `โอนเข้าบัญชีเท่านั้น` · `ทั้งสอง` · `ไม่แนบ` — ช่องทางย่อยตัดกับ `DocumentPayLinkPolicy.Allowed` สดทุกครั้ง (241-2)
- บัญชีรับโอน + QR (246-QR-3 ตัวเลือกบัญชีเดิม) · พรีวิว "ยอดในลิงก์/QR รอบหน้า = 10,400.00 (หลังหัก ณ ที่จ่าย 300.00)"
- ทวงชำระ: ก่อนครบกำหนด N วัน · วันครบ · หลังครบ 3/7/14/30 วัน (ใช้ trigger DueSoon/Overdue เดิม — ตั้ง**ต่อรายการ**ได้ผ่านกฎเสมือน) · หยุดทวงเมื่อจ่าย/ลูกค้าแจ้งโอนแล้ว (สลิปรอตรวจ = พักทวง 3 วัน)

**⑥ หลังรับเงิน**
- ออกใบเสร็จอัตโนมัติ ☑ (`PostPayEmailReceipt` ต่อรายการ) · ส่งทุกงวด/เฉพาะครบ (`PostPayIncludePartial`)
- ชนิดใบที่ออก — **แสดงอย่างเดียว** (ผลของ `SettlementReceiptPolicy` + โหมด e-Tax): "จะออก: ใบเสร็จรับเงิน/ใบกำกับภาษี (บริการ · ณ วันรับเงิน) · ส่งทาง e-Tax by Email" — ไม่ให้เลือกขัดกฎ
- ส่งเข้าระบบคู่ค้า ☑ (ถ้าเชื่อม) · แจ้ง webhook ระบบลูกค้า (API ลูกค้าที่ไม่ใช่ NextAcc · §8.6)

**⑦ หัก ณ ที่จ่าย**
- คาดว่าลูกค้าจะหัก: ใช่/ไม่/ตามชนิดลูกค้า · ช่องทางรับ 50 ทวิ: คู่ค้า NextAcc ออกอัตโนมัติ (ถ้าเชื่อม) · ลิงก์อัปโหลดในอีเมลใบเสร็จ · พอร์ทัล · ลูกค้าใช้ e-WHT (ค่าเสนอ — ยังต้องมีหลักฐาน `EWHT_EVIDENCE_RESEARCH.md`) · จังหวะทวง 7/21/45 + รายเดือน ≤ 6 (อนุมัติแล้ว §11 ข้อ 4) ปรับต่อรายการได้

**⑧ อนุมัติ & แจ้งทีม** — §4.3 + §10

### 4.3 นโยบายอนุมัติอัตโนมัติ (ตอบ A-2 + "ติ๊กอนุมัติได้แม้ค้างชำระ/เครดิต พร้อม audit")

```
┌ ⑧ อนุมัติอัตโนมัติ ─────────────────────────────────────────────────────────┐
│ ☑ อนุมัติอัตโนมัติเมื่อสร้างเอกสาร (ได้เลขจริงทันที — จำเป็นสำหรับการส่ง)       │
│                                                                              │
│ เมื่อพบคำเตือนต่อไปนี้ ระบบจะ:                    [หยุดรอคน ▾] / [อนุมัติต่อ+บันทึก]│
│  • ลูกค้าค้างชำระเกิน 30 วัน (AR-OVERDUE)          [อนุมัติต่อ+บันทึก ▾] ⓘ      │
│  • ยอดเกินวงเงินเครดิตลูกค้า (AR-CREDIT-LIMIT) 📋   [หยุดรอคน ▾]                │
│  • สินค้าคงเหลือติดลบ (STOCK-NEG)                  [หยุดรอคน ▾]                │
│  • อัตรา WHT ไม่ตรงประเภทเงินได้ (WHT-RATE)          ผ่านอัตโนมัติอยู่แล้ว (นโยบายระบบ)│
│  ── ข้อที่ปล่อยผ่านไม่ได้ (กฎหมาย/ความถูกต้อง) ──                                │
│  • ข้อมูลผู้ซื้อไม่ครบ §86/4 · ยอดไม่ตรงกระดาษ · งวดบัญชีปิด  — หยุดเสมอ ⓘ เหตุ     │
│ ผู้ตั้งนโยบาย: คุณสมชาย (ต้องมีสิทธิ์อนุมัติเอกสารชนิดนี้) · บันทึกเมื่อ …          │
└──────────────────────────────────────────────────────────────────────────────┘
```

- รายการคำเตือน + ข้อที่ "ปล่อยได้" มาจากเซิร์ฟเวอร์: ตัวตัดสินเดียว **ขยาย `Helpers/ApprovalAcknowledgement`** — เพิ่ม `ApprovalAckSource.UnattendedWithPolicy` + พารามิเตอร์ `AutoApprovePolicy` (ชุดรหัสคำเตือนที่เจ้าของเลือกปล่อย) · `Unacknowledged` = ข้อที่ไม่อยู่ในชุดเดิม (`PassesWithoutPerson`) **และ**ไม่อยู่ในชุดนโยบาย **และ**ไม่อยู่ในชุดห้ามปล่อย (`NeverPassable`: §86/4 ผู้ซื้อ · `[Σ-GAP]` · VAT ไม่อยู่บนกระดาษ)
- ต้องให้คำเตือนมีรหัส (ปัจจุบันเป็นข้อความล้วน) ⇒ ป้ายนำหน้า `[AR-OVERDUE]` ที่ `DocumentService.cs:22649` ผ่าน helper `ArOverdueApprovalWarning` (แบบ `WhtLineRateWarning.IsApprovalWarning`) — ห้าม match ข้อความไทย
- ⚠️ ไม่พบคำเตือน "เกินวงเงินเครดิต" จาก `Contact.CreditLimit` (`DocumentService.cs:14445` เขียน · ไม่พบผู้อ่านใน `CollectApprovalWarningsAsync:21797`) ⇒ ข้อนี้เพิ่มเป็นคำเตือนใหม่ได้ (คำถาม Q6) — ห้ามแสดงในรายการจนกว่ามีตัวจริง (มี ≠ ถูกเรียก)
- ร่องรอย: หมายเหตุภายในบนเอกสาร + `AddChainedAuditLog` `RuleCode=APPROVE-UNATTENDED-POLICY` + รายการคำเตือนที่ปล่อย + Id ของนโยบาย/ผู้ตั้ง · แจ้ง [S→เรา] E-02b "อนุมัติพร้อมข้อสังเกต" (ใช้ `NotificationEvents.RecurringApprovedWithNotes` เดิม)
- ผู้ตั้งนโยบายต้องมีสิทธิ์อนุมัติเอกสารชนิดนั้น · ผู้ตั้งออกจากบริษัท ⇒ นโยบายหยุดมีผล (แบบ `CompanyMembership.IsActiveMemberAsync` ใน `CrossTenantWorkflowService.cs:341`) + แผงความพร้อม ❌
- เกณฑ์วงเงินอนุมัติตรง (`RequireApprovalForDocuments` `DocumentService.cs:6958`) **ไม่ปล่อยผ่านด้วยนโยบายนี้** (เป็นการควบคุมภายในของบริษัท) — แสดงเป็น ❌ พร้อมทางไปต่อ

### 4.4 ตรวจความพร้อม — ตัวตัดสิน `Helpers/AutopilotReadiness` (pure) + `AutopilotReadinessService`

`Evaluate(Facts) → Report { Level, Items[] { Code, Stage, Verdict(Ok|Warn|Fail|Info), Title, Why, FixUrl, FixLabel } }` — ข้อความไทยจากที่เดียว · Facts รวบรวมใน service ครั้งเดียว (ไม่ N+1)

| Code | ขั้น | ✅ เมื่อ | ⚠️/❌ + ทางแก้ | อ่านจาก |
| --- | --- | --- | --- | --- |
| `SMTP` | ส่ง | SMTP บริษัทตั้ง+ทดสอบผ่าน | ⚠️ ใช้อีเมล NextAcc ในนามบริษัท (เพดาน 200/วัน 240-6 · ผู้รับ ≤5) · ❌ ถ้าโหมด e-Tax by Email (ต้อง SMTP บริษัท 244-5) → `settings#email` | `OnBehalfEmailSender` · `EtaxByEmailSenderGate` |
| `ETAX` | ส่ง | โหมด e-Tax ตรงกับชนิดใบที่จะออก | ⚠️ ไม่ได้เปิด e-Tax — ใบกำกับส่งเป็น PDF (ลูกค้าต้องได้ต้นฉบับกระดาษ) · ⚠️ โหมด `Both` ค้าง (244-1) | `EtaxDeliveryRouter.Decide` |
| `BUYER-TAX` | ภาษี | ผู้ซื้อนิติบุคคลมี เลข 13 หลัก checksum + สาขา 5 หลัก + ที่อยู่ | ❌ (ใบกำกับ/ใบเสร็จ-ใบกำกับจะออกไม่ได้ · งานหลังรับชำระจะ Blocked 241-8) → หน้าผู้ติดต่อ | `TaxInvoiceCompletenessChecker.BuyerBlockingFields` |
| `RECIPIENT` | ส่ง | มีปลายทางหัวข้อ Document+Receipt | ❌ ไม่มีอีเมล/LINE ที่ใช้ได้ | `ContactRecipientResolver` / `RecurringEmailReadiness` |
| `GATEWAY` | รับเงิน | วิธีที่เลือก ∈ `PlatformPayMethods.Effective` | ⚠️ วิธีถูกปิด/คีย์ไม่ครบ → `payment-settings` | `DocumentPayLinkPolicy.UnavailableReason` |
| `PAY-QR` | รับเงิน | บัญชีของใบมี QR (พร้อมเพย์/รูป) | ⚠️ ไม่มี QR (ลูกค้าต้องพิมพ์เลขบัญชีเอง) → `bank` | `PaymentQrService.PickDocumentAccountAsync` |
| `THB` | รับเงิน | สกุล THB | Info: ลิงก์ชำระ THB เท่านั้น — ไม่แนบลิงก์ | 241-3 |
| `APPROVE` | อนุมัติ | ทดลองคำเตือนบนใบจำลอง = ว่างหลังนโยบาย | ❌ "จะหยุดเพราะ [AR-OVERDUE] ลูกค้าค้าง 2 ใบ" → ⑧ | `PreviewApprovalWarningsAsync` (`DocumentService.cs:6560`) บนใบจำลองที่ไม่บันทึก (📋 โหมด in-memory) |
| `PERIOD` | อนุมัติ | วันเอกสารรอบหน้าอยู่ในงวดเปิด | ❌ งวดปิด | ด่านงวดเดิม |
| `WHT-SETUP` | WHT | ชนิดลูกค้า/อัตราสอดคล้อง | ⚠️ บุคคลธรรมดาแต่ตั้ง WHT (241-11 S3) | `ContactTypeResolver` |
| `PARTNER` | คู่ค้า | Active + ขอบเขตครบ | Info "ยังไม่เชื่อม" [ส่งคำขอ] · ⚠️ ขอบเขตบางอย่างปิด · ❌ ถูกเพิกถอน | `TradingPartnership` (§6) |
| `QUOTA` | ส่ง | โควตาเดือนพอสำหรับรอบหน้า (ประมาณ) | ⚠️ ใกล้เต็ม → ซื้อแพ็ก (L4) | `NotifyQuotaService` |
| `POSTPAY` | หลังรับเงิน | เปิดออก/ส่งใบเสร็จ | Info ปิด ⇒ ระดับ 🟡 | `PostPaymentAutomationPolicy.Effective` |
| `BANK-AUTO` | รับเงิน (โอน) | บัญชีรับโอนมี feed/นำเข้า statement ภายใน 3 วันล่าสุด + เปิด `BankReceiptAutoSettle` | ⚠️ ไม่มี statement เข้า ⇒ โอนต้องยืนยันมือ | §5 |
| `OWNER-POLICY` | อนุมัติ | ผู้ตั้งนโยบายยังเป็นสมาชิก | ❌ | `CompanyMembership` |

### 4.5 ทดลองรัน (dry-run) — `POST /api/companies/{cid}/recurring/{id}/dry-run` (หรือ `…/recurring/dry-run` กับร่าง)

- **ไม่เขียนอะไรเลย** (ไม่ออกเลข · ไม่สร้างลิงก์ · ไม่ส่ง · ไม่จองโควตา) — สร้าง `Document` ในหน่วยความจำจาก `RecurringTemplate` เดิม แล้วถามตัวตัดสินทุกตัวแบบอ่านอย่างเดียว
- คืนลำดับเหตุการณ์: วันสร้าง/เลขที่จะได้ (รูปแบบ `INV-2569xx-????` — "เลขจริงออกตอนอนุมัติ") · บรรทัด/ยอด/VAT/WHT/ยอดในลิงก์ · ผลอนุมัติ (ผ่าน/หยุดเพราะ…) · ผู้รับ + ช่องทาง + ทางถอย · ผู้ส่ง (บริษัท/NextAcc) · เส้น e-Tax · ข้อความอีเมล (พรีวิว HTML จากตัวประกอบเดียวกับตอนส่งจริง) · หลังรับเงินจะออกใบอะไร · คู่ค้าจะได้อะไร · 50 ทวิ คาดเท่าไร
- ปุ่ม "ส่งทดสอบถึงอีเมลฉัน" (ส่งถึงผู้ใช้ที่กดเท่านั้น · หัว "[ทดสอบ]" · ไม่มีลิงก์ชำระจริง — ใช้ลิงก์ตัวอย่างที่เปิดหน้า "ทดสอบ") — นับโควตาตามปกติ
- `required_call_site_check`: dry-run ต้องเรียก `AutopilotReadiness.Evaluate` + `ApprovalAcknowledgement.Unacknowledged` + `DocumentPayLinkPolicy.AmountsFor` + `EtaxDeliveryRouter.Decide` (ห้ามสูตรของตัวเอง) · **ห้าม**เรียก `SaveChangesAsync`/`EnsureLinkAsync`/`Send*` (Db test: นับแถวก่อน-หลัง = เท่ากัน)

### 4.6 หน้ารายการประจำ (ตาราง)

คอลัมน์: ชื่อ · ลูกค้า · รอบถัดไป · **ระดับ Autopilot** (🟢/🟡/🔴 + จำนวนข้อ ⚠️/❌) · ผลรอบล่าสุด (รอบ 247) · ยอดค้างของลูกค้า · คู่ค้า NextAcc (ป้าย "เชื่อมแล้ว") · ตัวกรอง "ติดปัญหา" · ปุ่มต่อแถว [ทดลองรัน] [สร้างงวดนี้เลย] [หยุดชั่วคราว]

---

## 5. เส้น "โอนเข้าบัญชี" (ลูกค้าที่ตั้งให้จ่ายโดยโอน — ไม่ผ่าน gateway)

### 5.1 ตั้งช่องทางชำระต่อผู้ติดต่อ / ต่อรายการประจำ

| ระดับ | วันนี้ | เพิ่ม (📋) |
| --- | --- | --- |
| บริษัท | `CompanySettings.PayLinkDefaultEnabled/ChannelsJson` (`CompanySettings.cs:337-339`) | — |
| **ผู้ติดต่อ** | ❌ ไม่มี (Contact `Models/Entities/Document.cs:966+` มีแค่ `PaymentDueDays:130`/`PaymentTerms:133`) | `Contact.PaymentMode` (`Inherit`/`Online`/`TransferOnly`/`Both`/`None`) + `Contact.PayLinkChannelsJson` (ตรงกับที่ AUTO_SEND §7.1 เสนอไว้) + `Contact.PreferredBankAccountId` · ผู้อ่าน = `AutopilotSettingsResolver` ⇒ `EnsureLinkAsync`/`ForScheduledEmailAsync`/QR |
| ตารางส่ง | `EmailScheduleRule.AttachPayLink/PayLinkChannelsJson` (241-1) | — |
| รายการประจำ | ❌ (ใช้ค่าบริษัท) | `RecurringTransaction.PaymentMode/PayLinkChannelsJson/BankAccountId` (§4.2 ⑤) |
| เอกสาร | `Document.PayLinkChannelsJson` (`Document.cs:254`) · `PaymentAccountId/BankAccountId` (246-QR-3) | ไหลจากแม่ผ่าน resolver (กฎ #4 A "เอกสารลูกต้องสืบทอด") |

`TransferOnly` ⇒ ลิงก์ชำระยังออกได้ (หน้าแสดงบัญชี/QR + ยอดสุทธิ — ช่อง `ManualSlip` 241-2) แต่ไม่มีบัตร/พร้อมเพย์ผ่าน Omise · ถ้าไม่ต้องการหน้าเว็บเลย ⇒ `None` + QR บนกระดาษ (246)

### 5.2 ของที่มีแล้ว (อ้างไฟล์)

| เครื่องมือ | ไฟล์ | ใช้ในเส้นโอนอย่างไร |
| --- | --- | --- |
| QR บัญชี/พร้อมเพย์บนเอกสาร + หน้าชำระ | `Helpers/PaymentQrPolicy.cs:20` · `Services/Implementations/PaymentQrService.cs:156-228` · `DocumentPayLinkService.PayQrFactsAsync:276` · `pay-document.html` (กล่องโอน 246-QR-8) | QR พร้อมเพย์ฝังยอดสุทธิหลัง WHT ⇒ ยอดเข้าบัญชีตรงเป๊ะ — **ตัวช่วยจับคู่ที่ดีที่สุด** (ยอดตรง) · ⚠️ QR พร้อมเพย์ส่วนบุคคล/นิติบุคคลไม่มีช่อง "เลขอ้างอิงใบ" ที่ธนาคารผู้รับแสดงได้แน่นอน — จึงยึดยอด+วัน+ชื่อผู้โอน |
| สลิปผ่าน intent (คนยืนยัน) | `Services/Payments/Providers/ManualSlipPaymentProvider.cs` (Succeeded = คนกดเท่านั้น · `SettlesDirectlyToBank` Dr ธนาคาร) | ทางยืนยันมือที่ไปเส้นเดียวกับ gateway (intent → handler → `EnqueueAsync`) ✅ · 241-2: หน้าชำระสาธารณะ**ไม่เปิดอัปสลิปแบบไม่ล็อกอิน** |
| สลิปผ่านพอร์ทัล (ล็อกอิน) | `Controllers/PortalController.cs:110` → `PortalService.UploadDocumentSlipAsync:507-582` | ⚠️ สร้าง `Payment` จริงทันที + `Notes="[PENDING-VERIFY]"` · **ไม่ผ่าน `ManualSlip` intent** (สองความจริงของ "จ่ายหรือยัง" — defect class ที่ `ManualSlipPaymentProvider` ระบุไว้ในคอมเมนต์หัวไฟล์) · ไฟล์เก็บ `StoragePath = absPath` (`:556` — ต้องตรวจว่าผ่าน `IFileStorage` ตามกฎ #4 D) |
| นำเข้า statement | `BankService.StatementImport.cs:28` (`ImportParsedStatementRowsAsync`) → `:285-287` จับคู่อัตโนมัติหลังนำเข้า · `OpenBankingService.ImportFileAsync:226` | ✅ แหล่งรายการเดินบัญชีจริง |
| bank feed / Open Banking | `BankFeedService.cs:82` (`SyncAsync`) · `:173` (SyncAll) · `OpenBankingService.SyncTransactionsAsync:110` · `ProcessAutoSyncAsync:188` | ✅ (ขึ้นกับว่าธนาคาร/ผู้ให้บริการเชื่อมจริงในโปรดักชัน — ⚠️ ตรวจกับเจ้าของ Q8) |
| จับคู่อัตโนมัติ | `BankService.AutoMatchAsync:764-900` · คะแนน `Helpers/BankMatchScorer.cs` · ตัดสิน `Helpers/BankMatchArbiter.cs` · สกุล `Helpers/BankMatchCurrency.cs` | 🟡 **ผู้สมัคร = `Payments` ที่บันทึกแล้ว** (`:786-790`) + JE ⇒ ปิด "กระทบยอด" ไม่ใช่ "ปิดหนี้" |
| AI/นักเรียนเสนอเอกสารค้าง | `BankFeedService.cs:380-440` (ชุดเอกสารค้าง `BalanceDue>0` สกุลตรง ±90/7 วัน · `BankMatchArbiter.ScreenAiProposals` · `SuggestedDocumentId` · ป้าย `BANK-MATCH-AI-DOC`/`LOCAL-DOC`) · `BankService.MatchCandidates.cs:344` (`SuggestMatchAsync`) | 🟡 เสนอได้ แต่**ไม่สร้างการชำระ** · ⚠️ ไม่พบหน้า `.html` ที่อ่านช่อง `suggestedDocumentId` ตรง (แสดงผ่านชุดคู่ใน `BankService.MatchResolution.cs:91` เท่านั้น) และไม่พบปุ่ม "บันทึกรับชำระจากรายการนี้" ⇒ ต้องตรวจตอนลงมือ |
| จับคู่จำนวนมาก | `Controllers/BankController.cs:195,227` → `IBulkBankAiMatchService` (+ distillation `BulkBankStatementMatch` ตามกฎ #1) | 🟡 เสนอทั้งหน้า · ผลลัพธ์กลับมาเรียน |
| เรียนแพตเทิร์น | `BankService.Learning.cs` (คอมเมนต์ `:159`: AutoMatch ไม่บันทึกแพตเทิร์น) · `BankMatchDistillationModel` | ✅ |
| OCR สลิป | `Services/Implementations/SlipOcrAssistService.cs` (Tesseract ในบ้าน · rule-based · `SlipOcrAmount`/`SlipOcrAmountMatches`) | 🟡 ใช้กับสลิปค่าบริการแพลตฟอร์ม (`SubscriptionService.cs:4464`) เท่านั้น |
| กระทบยอดโอนแพลตฟอร์ม | `Services/Payments/PlatformTransferReconciliationService.cs:25-40` · `Helpers/PlatformTransferMatch.cs` | ✅ ต้นแบบ "เงินจริงเข้า ⇒ จับคู่ ⇒ ร่างเอกสาร" (ฝั่ง NextAcc เก็บเงินผู้เช่า) |

### 5.3 ช่องว่างที่ต้องปิด (ให้เส้นโอนเท่ากับเส้น gateway)

| # | ช่องว่าง | ปิดอย่างไร |
| --- | --- | --- |
| G-T1 | เงินเข้า statement ไม่สร้างการรับชำระของใบค้าง | ตัวตัดสิน `BankReceiptAutoSettle` (§5.4) + `BankReceiptSettleService.ApplyAsync` เรียก `CreatePaymentAsync` (ตัวเดียว) แล้วผูก `BankTransaction.MatchedPaymentId` ในธุรกรรมเดียว + `EnqueueAsync(source:"bank-auto")` หลัง commit |
| G-T2 | ไม่มีเกณฑ์ "มั่นใจพอปิดหนี้เอง" | §5.4 — เชิงกำหนดก่อน · AI/นักเรียนช่วยได้แค่ระดับ Suggest |
| G-T3 | WHT: ลูกค้าโอนยอดสุทธิ | ใช้ยอดคาดของใบ `DocumentPayLinkPolicy.AmountsFor` (ยอดค้างเป็นสุทธิแล้ว 244-7) + `WhtForPayment` ⇒ ส่ง WHT ที่เหลือเข้า `CreatePaymentAsync` เหมือน `DocumentPaymentHandler.cs:82-97` — **ตัวเดียวกัน** ห้ามสูตรใหม่ |
| G-T4 | โอนรวมหลายใบ / ขาด / เกิน | §5.5 — ไม่ Auto · Suggest พร้อมคำอธิบาย + ปุ่มจัดสรร (ใช้ settlement หลายใบเดิม `SettlementPostingService`) |
| G-T5 | สลิปซ้ำ/สลิปปลอม | §5.6 — hash ไฟล์ + เลขอ้างอิงธนาคารจาก OCR + (ยอด, วันเวลา, บัญชีปลายทาง) unique · สลิปไม่ปิดหนี้เอง |
| G-T6 | ทางรับชำระนี้ไม่เข้า `EnqueueAsync` | G-6 ของ AUTO_SEND — ทุกทางบันทึกรับชำระเรียก (ล็อก `required_call_site_check`) |
| G-T7 | สลิปพอร์ทัลสร้าง `Payment` ก่อนตรวจ | ย้ายไปสร้าง `PaymentIntent(ManualSlip, AwaitingReview)` แทน (ทางเดียวกับ `ManualSlipPaymentProvider`) · migration: แถวเก่า `Notes LIKE '[PENDING-VERIFY]%'` ⇒ รายงานให้ผู้ใช้ตัดสิน (ไม่แปลงเงียบ — หลักการ #9) · ⚠️ ตรวจก่อนว่าแถวเหล่านี้ลด `BalanceDue` อยู่หรือไม่ |
| G-T8 | หน้าเสนอเอกสารค้างไม่มีปุ่มบันทึกรับชำระ | หน้ากระทบยอดเพิ่มคิว "เงินเข้าที่รอยืนยัน" (§5.7) |
| G-T9 | ไม่มีคำสั่งให้ลูกค้าระบุอ้างอิง | อีเมล/หน้าชำระ/กระดาษ: "โปรดระบุเลขที่ INV-… ในบันทึกการโอน" + QR พร้อมเพย์ยอดเป๊ะ (ตัวระบุที่ได้ผลจริงที่สุด) |

### 5.4 ตัวตัดสิน `Helpers/BankReceiptAutoSettle` (pure · ไม่เรียก AI)

```
Decide(BankLine line, IReadOnlyList<OpenInvoice> candidates, Settings s) → Verdict { Kind: AutoSettle | Suggest | None, InvoiceIds[], Allocations[], WhtPerInvoice[], Reason, RuleCode }

ชุดผู้สมัคร: ใบขายค้าง (Approved/PartiallyPaid/Overdue) · BalanceDue>0 · สกุล = สกุลบัญชี (BankMatchCurrency) · CompanyId ตรง ·
             วันเอกสาร ≤ วันเงินเข้า + 7 · ไม่มี PaymentIntent กำลังดำเนิน (กันนับซ้ำกับ gateway) · ไม่มีการชำระที่จับคู่กับรายการเดินบัญชีนี้แล้ว

ยอดคาดต่อใบ = ExpectedNet(inv) = AmountsFor(inv).Transfer   (ยอดค้างสุทธิหลัง WHT ที่ยังไม่ถูกหัก — ตัวเดียวกับลิงก์/QR)

AutoSettle เมื่อ **ครบทุกข้อ** (เปิดค่าตั้งบริษัท `BankAutoSettleEnabled` + ผู้ติดต่อไม่ปิด):
  (1) line มาจากแหล่งธนาคารจริง: feed / OpenBanking / ไฟล์ statement ที่นำเข้า  (ไม่ใช่สลิป · ไม่ใช่พิมพ์มือ)
  (2) |line.Amount − ExpectedNet(inv)| ≤ 0.00   (ตรงเป๊ะ — ไม่มีค่าเผื่อ เพราะเงิน)
  (3) มีตัวระบุตัวอย่างน้อย 1:  เลขเอกสาร/เลขอ้างอิงลิงก์ชำระอยู่ในคำอธิบายรายการ  หรือ  ชื่อ/บัญชีผู้โอนตรงกับผู้ติดต่อที่เรียนไว้ (BankMatchScorer.HasIdentitySignal)
  (4) ยอดไม่ซ้ำ: ไม่มีใบค้างอื่น (ทุกผู้ติดต่อ) ที่ ExpectedNet เท่ากันในกรอบวันเดียวกัน — ถ้ามี ต้องมีข้อ (3) แบบ "เลขเอกสาร" เท่านั้นจึงผ่าน
  (5) วันเงินเข้าอยู่ในงวดเปิด · ใบไม่อยู่ระหว่างยกเลิก
Suggest เมื่อ: ผ่าน (1)(5) แต่ขาด (3) หรือ (4) · ยอดตรงยอดเต็มก่อนหัก WHT (ลูกค้าไม่หัก) · subset-sum หลายใบผู้ติดต่อเดียวมีคำตอบเดียว · ขาด/เกิน ≤ 2% หรือ ≤ ฿50 (ค่าธรรมเนียม) ·
          นักเรียน/AI เสนอ (BankFeedService เดิม) — ป้าย ⚙️/🤖 ซื่อสัตย์
None: อื่น ๆ
```

- **ไม่มี AI ในด่าน AutoSettle** (ตัวเลขล้วนไม่มีการสะกดผิด — F2 ข้อ 3) · AI/นักเรียนทำได้แค่ Suggest และคำตอบต้องอยู่ในชุดผู้สมัคร (`BankMatchArbiter.ScreenAiProposals` เดิม)
- ผลลัพธ์ AutoSettle: `CreatePaymentAsync(Amount=line.Amount, WithholdingTaxAmount=WhtForPayment(...), PaymentDate=line.Date, PaymentMethod=BankTransfer, BankAccountId, Reference="BANK:{lineId}")` → `line.MatchedPaymentId` · `ReconciledBy = BankMatchAttribution.BankAutoSettle` (ค่าใหม่) · `MatchRuleCode=BANK-AUTO-SETTLE` · audit chain · หลัง commit `EnqueueAsync(..., "bank-auto")`
- ย้อนได้: "ยกเลิกการจับคู่" ⇒ `VoidPaymentAsync` เส้นเดิม (ใบเสร็จที่ออกแล้วต้องยกเลิกตาม §86/4 — ป้ายเตือนก่อนกด) — Suggest ที่ผู้ใช้ปฏิเสธ ⇒ บันทึกเป็นแพตเทิร์น "ไม่ใช่" (`BankService.AiReconciliation.cs:927` แบบเดิม)
- ค่าตั้งต้น: `BankAutoSettleEnabled = false` (บริษัทเปิดเอง — คำถาม Q4) · เปิดแล้วแสดงสถิติ 30 วัน "จะปิดอัตโนมัติได้ X ใบ / เสนอ Y" ก่อนยืนยัน (คำนวณย้อนหลังแบบอ่านอย่างเดียว)

### 5.5 WHT · โอนรวม · ขาด · เกิน

| กรณี | ตัวอย่าง | คำตัดสิน |
| --- | --- | --- |
| a ลูกค้าหัก 3% ตามคาด | ใบ 10,700 (VAT 700) WHT 300 → เข้า 10,400 | AutoSettle (ถ้าข้อ 3–4 ผ่าน) · WHT 300 ⇒ แถว `WhtCreditReceived` Pending จาก GL ⇒ ขอ 50 ทวิ ตาม AUTO_SEND |
| b ลูกค้าไม่หัก | เข้า 10,700 | Suggest "ยอดเต็ม — ลูกค้าไม่หัก ณ ที่จ่าย?" (ผู้ซื้อนิติบุคคลมีหน้าที่หัก — ไม่เดาแทน) · ยืนยัน ⇒ WHT=0 · ถ้าผู้ซื้อเป็นบุคคลธรรมดา (241-11 S3 ยอดคาด = เต็ม) ⇒ เป็นกรณี a |
| c หักต่างอัตรา (1% e-WHT / 2% โฆษณา) | เข้า 10,600 | Suggest "ส่วนต่าง 200 อาจเป็น WHT 1% (e-WHT)" + ปุ่ม [บันทึก WHT 100 · e-WHT] (ตั้ง `EvidenceKind=EWithholding` ตาม `EWHT_EVIDENCE_RESEARCH.md`) / [ค่าธรรมเนียม] / [ยังค้าง] |
| d โอนรวม 3 ใบ | 31,200 = 10,400×3 | subset หาได้ชุดเดียว ⇒ Suggest + จัดสรรอัตโนมัติในหน้ายืนยัน → `SettlementPostingService` (หลายใบ · `SettlementReceiptWht.Remaining`) → `EnqueueAsync` ต่อการชำระ |
| e โอนขาด | 10,000 | Suggest "ขาด 400" — [รับบางส่วน] (ใบยังค้าง 400 · ใบเสร็จต่องวดตาม `PostPayIncludePartial`) / [ค่าธรรมเนียม/ส่วนลด ↗ ลงผัง] |
| f โอนเกิน | 11,000 | Suggest "เกิน 600" — [รับเต็ม + บันทึกเงินรับล่วงหน้า/เครดิตลูกค้า] / [คืนเงิน] · ห้ามปิดใบอื่นเอง |
| g โอนก่อนออกใบ (มัดจำ) | ไม่มีใบค้าง | None · ถ้าผู้ใช้ต่อ ⇒ ใบเสร็จมัดจำเส้นเดิม |

### 5.6 สลิป: OCR + กันซ้ำ (เป็น "ข้อบ่งชี้" เท่านั้น)

- ทางเข้า: พอร์ทัล (ล็อกอิน) · LINE (L2+ รับรูปในแชต 1:1 ของผู้ติดต่อที่ผูก) · อีเมลขาเข้า (backlog — ยังไม่มีโครงรับเมล AUTO_SEND §3.1) · พนักงานอัปโหลดแทน
- ขยาย `SlipOcrAssistService` เป็นตัวกลาง `SlipEvidenceService.SubmitAsync(companyId, documentId?, file, channel)` (ทางเข้าเดียว — แบบ `WhtCertIntakeService.SubmitAsync`):
  - เก็บไฟล์ผ่าน `IFileStorage` · `Sha256` · OCR ในบ้าน: ยอด · วันเวลา · ธนาคาร/บัญชีปลายทาง (4 ตัวท้าย) · **เลขอ้างอิงรายการ (Transaction Ref)** · ชื่อผู้โอน · (QR ตรวจสลิปบนสลิปไทย — ถอดได้ด้วย ZXing ที่มีในระบบ? ⚠️ ตรวจไลบรารี)
  - **กันซ้ำ** (ด่านเชิงกำหนด): `UNIQUE(CompanyId, FileSha256)` · `UNIQUE(CompanyId, BankRef)` เมื่ออ่านได้ · (ยอด, วันเวลา±1 นาที, บัญชีปลายทาง) ซ้ำกับสลิปเดิม ⇒ "สลิปนี้เคยส่งแล้วกับ INV-…" (ไม่รับ)
  - บัญชีปลายทางไม่ใช่บัญชีบริษัท ⇒ ปฏิเสธพร้อมเหตุ · ยอดไม่ตรงใบ ⇒ รับไว้ + ป้าย
  - ผล: `PaymentIntent(ManualSlip)` สถานะ AwaitingReview (G-T7) + ผูกสลิปกับใบ ⇒ พักทวง 3 วัน + แจ้ง [S→เรา] E-06 "ลูกค้าแจ้งโอน INV-… 10,400 (สลิป)"
  - **ปิดหนี้เมื่อ**: (ก) รายการเดินบัญชีจริงมาถึงและ `BankReceiptAutoSettle` จับได้ — สลิปเป็นตัวระบุตัวข้อ (3) ได้ (ref/ยอด/เวลา ตรง) ⇒ AutoSettle · (ข) คนกดยืนยันใน `ManualSlip` (ทางเดิม) · **ไม่มีทาง (ค)** ที่ OCR สลิปปิดเอง
- ไม่มี AI ภายนอกในเส้นนี้ (ถ้าภายหลังใช้ Vision-LLM ⇒ ต้องผ่าน orchestrator + distillation ตามกฎ #1 — ไม่อยู่ในแผนนี้)

### 5.7 หน้าจอ: คิว "เงินเข้าที่รอยืนยัน" (ในหน้ากระทบยอดธนาคาร)

```
┌ เงินเข้าที่ระบบจับคู่ได้ — 7 รายการ ─────────────────────────────────────────────────┐
│ 05 พ.ย. 10,400.00  "TRF FR บจก.พี INV-2569110012"  → INV-…0012 บจก.พี  ✅ ปิดอัตโนมัติแล้ว │
│                                                     RT-…0451 ส่งแล้ว 10:02 · [ยกเลิก]      │
│ 05 พ.ย. 31,200.00  "TRF FR KASIKORN 123-4-xxxx"     → 3 ใบของ บจก.คิว (รวมพอดี)  [ยืนยัน] │
│ 06 พ.ย. 10,600.00  "PROMPTPAY 0105…"                → INV-…0019 ขาด 200 — WHT 1%? ▾      │
│ 06 พ.ย. 10,400.00  ไม่มีชื่อ/อ้างอิง                → 2 ใบยอดเท่ากัน — เลือก ▾  (ไม่ปิดเอง) │
│ 📎 สลิปจากลูกค้าที่ยังไม่เจอเงินเข้า: 2 (INV-…0021 แจ้งเมื่อ 04 พ.ย.)                     │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 6. NextAcc ↔ NextAcc — "เชื่อมระบบคู่ค้า"

### 6.1 ของเดิมและสิ่งที่ต้องแก้ก่อนใช้จริง (❗ หลักการ #10 — แก้คำกล่าวใน AUTO_SEND §1.1)

| # | ปัญหาในโค้ดเดิม | หลักฐาน | แก้ |
| --- | --- | --- | --- |
| P-1 | เชิญด้วยเลขภาษีแล้วตอบ 404 "ไม่พบบริษัท…ในระบบ — แจ้ง partner สมัครใช้งานก่อน" = **ใครก็เช็กได้ว่าเลขภาษีใดใช้ NextAcc** | `CrossTenantWorkflowController.cs:68-79` · `CrossTenantWorkflowService.cs:44-50` | คำตอบเหมือนกันทุกกรณี "ส่งคำขอแล้ว" (§6.3) |
| P-2 | จับคู่ `TaxId` อย่างเดียว · `FirstOrDefault` (สองบริษัทเลขเดียวกันต่างสาขา = สุ่ม) | `:48-49` · `:163-164` | จับด้วย TaxId + BranchCode (`Company.cs:9-10`) · กำกวม ⇒ ไม่ส่ง + ให้ฝั่งรับเลือก |
| P-3 | ไม่ตรวจสิทธิ์ตอนเชิญ/ยอมรับ/อนุมัติเอกสารเข้า (มี `RequirePermission` จุดเดียวที่ PUT config `:142`) | controller | `CompanySettingsEdit` + `TaxFile` (ขอบเขต WHT) + สิทธิ์อนุมัติเอกสารซื้อ (ขอบเขต AP) |
| P-4 | ไม่มีขอบเขต/ข้อความยินยอม/เวอร์ชัน | entity `:22-53` | ตาราง `TradingPartnershipScope` (§6.2) |
| P-5 | ใบแจ้งหนี้/ใบเสร็จที่ส่งไปเป็นแค่แถวกล่องเข้า — ไม่สร้างใบซื้อ · `TryAutoApproveAsync` ทำเฉพาะใบเสนอราคา | `:137-190` · `:333` | §6.4–6.5 |
| P-6 | ส่งข้ามบริษัทในธุรกรรมอนุมัติแบบ best-effort + `LogWarning` (ล้มเงียบ — กฎ #4 F2 ข้อ 7) | `DocumentService.cs:7602-7608` | outbox ในธุรกรรมเดียวกับอนุมัติ + worker (§6.7) |
| P-7 | ใบเสร็จที่ออกอัตโนมัติ (`IssueReceiptForPaymentAsync`) ไม่ผ่าน `ApproveDocumentAsync` ⇒ ไม่ถูกส่ง (เหมือน G-7 AUTO_SEND) | | เพิ่มจุด enqueue ใน `PostPaymentAutomationService` ขั้น PartnerDeliver |
| P-8 | `AutoCreateReceiptFromIncomingPayment` ไม่มีผู้อ่าน · `CrossTenantLinkType.PaymentFlow` ไม่มีผู้สร้าง | `CrossTenantWorkflow.cs:113` · `AllEnums.cs:845-857` | ต่อสายใน §6.5 หรือลบ (`settings_reader_check`) |
| P-9 | ยกเลิกเอกสารต้นทางแล้วฝั่งรับไม่รู้ (`Withdrawn` ไม่มีผู้ตั้ง?) | enum `:859-866` | event `DocumentVoided` (§6.6) |
| P-10 | ไม่มีหน้าจอ | ไม่มี `.html` เรียก endpoint | หน้า `partners.html` (§6.8) |

### 6.2 ขอบเขต (scopes) — ยินยอมแยกต่อขอบเขต ต่อทิศทาง

| Scope | ทิศ (ผู้ส่ง → ผู้รับ) | ผู้รับได้อะไร | ผู้ยินยอมฝั่งรับต้องมีสิทธิ์ | ค่าตั้งต้น |
| --- | --- | --- | --- | --- |
| `SalesDocsAsBills` | S → P | ใบแจ้งหนี้/ใบกำกับ/ใบเพิ่มหนี้/ใบลดหนี้ของ S เป็น**ใบซื้อร่าง/ใบลดหนี้ซื้อ**ใน P | อนุมัติเอกสารซื้อ | เปิดเมื่อยอมรับ |
| `BillAutoPost` | (นโยบายของ P เท่านั้น) | P อนุมัติใบซื้อจาก S อัตโนมัติตามเงื่อนไข (§6.4) | อนุมัติเอกสารซื้อ + `CompanySettingsEdit` | ปิด |
| `ReceiptsAndPaymentStatus` | S → P | ใบเสร็จ/สถานะรับเงิน ⇒ ปิดใบซื้อ/ร่างการจ่ายใน P | จ่ายเงิน | เปิด |
| `WhtCertBack` | P → S | P ออก 50 ทวิ แล้วส่งเข้ากล่องรับของ S (AUTO_SEND §3.5) | P: `TaxFile` + จ่าย · S: `TaxFile` | เปิด (ส่ง) · การ**ออกอัตโนมัติ** = `WhtAutoIssue` |
| `WhtAutoIssue` | (นโยบายของ P) | ออก 50 ทวิ อัตโนมัติเมื่อบันทึกจ่ายใบซื้อจาก S | `TaxFile` | ปิด |
| `QuotationsAndPOs` | ทั้งสองทาง | เส้นเดิม QuotationFlow/PoFlow | เดิม | ตามเดิม |
| `Statements` | S → P | ใบแจ้งยอดรายเดือน (อ่านอย่างเดียว) | ดูบัญชี | ปิด |

- แต่ละฝั่งปิดขอบเขตใดก็ได้ทันที (kill switch ต่อขอบเขต) · ปิด "ทั้งหมด" = `Suspended` (มีอยู่แล้วใน enum)
- ระดับแพลตฟอร์ม: สวิตช์แอดมิน `PartnerExchange:Enabled` (ปิด = worker ไม่ส่ง · ทุกอย่างถอยไปอีเมล) + ต่อบริษัท `CompanySettings.PartnerExchangeEnabled`

### 6.3 ค้นพบ + เชิญ แบบไม่รั่ว ("ตรวจว่าลูกค้าใช้ NextAcc")

```
ปุ่ม [ตรวจ/ส่งคำขอเชื่อม NextAcc] ที่หน้าผู้ติดต่อ · แผงความพร้อม · หน้าคู่ค้า
  GET  …/contacts/{id}/partner-status  → { state: Linked | PendingOurs | PendingTheirs | Suspended | NotLinked , scopes[] , discoverable?: bool }
         • NotLinked ไม่บอกว่ามี/ไม่มีบัญชี — ยกเว้นบริษัทปลายทางเปิด `Company.PartnerDiscoverable` (Q1) ⇒ discoverable=true "ใช้ NextAcc (เปิดให้คู่ค้าค้นเจอ)"
  POST …/contacts/{id}/partner-invite { scopes[], message }   (สิทธิ์ CompanySettingsEdit)
         ⇒ เสมอ: อีเมลถึงปลายทาง NotifyTopic.Other ของผู้ติดต่อ (ผ่าน OnBehalfEmailSender + โควตา) — เนื้อหาเหมือนกันทุกกรณี:
            "บริษัท S ขอเชื่อมระบบเพื่อส่งใบแจ้งหนี้/ใบเสร็จเข้าระบบบัญชีของท่าน และรับหนังสือรับรอง 50 ทวิ อัตโนมัติ"
            ปุ่ม [ดูคำขอ] → หน้า partner-invite.html?t=…  (token hash · อายุ 30 วัน · ใช้ครั้งเดียว · rate limit)
         ⇒ ถ้าพบบริษัท TaxId+Branch ตรงแบบไม่กำกวม: แถว TradingPartnership Pending + กล่องในแอปของผู้มีสิทธิ์ฝั่งนั้น (ไม่บอกผลนี้กับ S)
         ⇒ คำตอบ API ถึง S: "ส่งคำขอแล้ว" เสมอ (P-1)
  หน้า partner-invite.html:
     • ล็อกอิน NextAcc แล้วผู้ใช้เป็นสมาชิกบริษัทที่ TaxId+Branch ตรงผู้ติดต่อ ⇒ หน้ายอมรับ: ขอบเขตทีละข้อ (ติ๊กได้) + ข้อความยินยอมเวอร์ชัน + ผู้กดต้องมีสิทธิ์ตามขอบเขต
     • ไม่มีบัญชี ⇒ ทางเลือก (ก) "สมัคร NextAcc ฟรี แล้วเชื่อม" (ข) "ใช้พอร์ทัลลูกค้า (ไม่ต้องสมัคร)" → §8
     • ผู้ใช้ล็อกอินแต่บริษัทไม่ตรงเลขภาษี ⇒ "คำขอนี้ส่งถึงบริษัทเลขผู้เสียภาษี 0-1055-xxxxx-xx-1 สาขา 00000 — บัญชีของท่านไม่ตรง" (แสดงเลขแบบ mask)
  ยอมรับแล้ว ⇒ Active + อีเมลแจ้ง S [S→เรา] E-10 · ตอนนี้ S ถึงรู้ว่า P ใช้ NextAcc
```

- ทางที่ P เริ่มเอง (AUTO_SEND §3.5.1 ข้อ 1) ใช้ endpoint เดียวกันกลับทิศ — P เห็นผู้ขายของตัวเองที่ใช้ NextAcc ได้ **เฉพาะที่ผู้ขายเปิด discoverable** (มิฉะนั้นก็ส่งคำขอแบบเดียวกัน)
- ยกเลิก/ปฏิเสธ: บันทึกเหตุ · เชิญซ้ำได้หลัง 7 วัน (กันสแปม) · เพดาน 20 คำเชิญ/บริษัท/วัน

### 6.4 ส่งเอกสารขาย → ใบซื้อฝั่งคู่ค้า (SalesDocsAsBills)

```
S: ใบแจ้งหนี้/ใบกำกับอนุมัติ (ทุกทางเข้า: มือ/ประจำ/API/แปลง) — ในธุรกรรมอนุมัติ: PartnerExchangeOutbox.Enqueue(InvoiceDelivered, docId, version)  (แทน try/LogWarning DocumentService.cs:7606)
worker (JobLock + SKIP LOCKED): ตรวจ TradingPartnershipPolicy.CanDeliver(pair, scope) สด → ประกอบ PartnerDocumentPayload (§6.9) + PDF จาก renderer เดิม + ไฟล์ e-Tax (ถ้ามี)
   → PartnerExchangeService.DeliverAsync(targetCompanyId=P):
       CrossTenantDocumentLink (เดิม · LinkType=InvoiceFlow) + Snapshot + PayloadSha256 + EventId
       สร้าง Document(PurchaseInvoice, Draft) ใน P:
          ผู้ขาย = Contact ของ P ที่ TaxId+Branch ตรง S (FindPartnerContactAsync เดิม :485) — ไม่มี ⇒ สร้างผู้ติดต่อใหม่จากข้อมูล S (§86/4 ครบ)
          SupplierInvoiceNumber = เลขของ S · วันที่ · บรรทัด · VAT ต่อบรรทัด · ยอด — ล็อก (แก้ไม่ได้ ช่องล็อก + เหตุ)
          ผังบัญชี/ศูนย์ต้นทุน: GlAccountDistillationModel/VendorIntelligence ของ P (local ก่อน — กฎ #1 · ปิด AI แล้วยังเติมได้จากประวัติ/ค่าตั้งต้นผู้ขาย)
          WHT ที่ P ต้องหัก: ตัวตัดสินของ P (PerLineWht / ThaiWhtRateTable) — หลักการ #8
          InputVatEvidence (📋 §9 L-2): EtaxVerified | EtaxPendingTimestamp | PaperRequired | NotTaxInvoice
       ⇒ ฝั่งรับ commit แล้วเท่านั้น ⇒ outbox Sent (หลักการ #2) · ล้มหลัง retry 1m→24h ×8 = Dead + [S→เรา] E-09b + fallback อีเมลเดิมยังทำงาน
P: BillAutoPost (ถ้าเปิด) — อนุมัติด้วย ApprovalAckSource.Unattended เมื่อครบ:
     ยอด ≤ เพดานต่อคู่ (TradingPartnership.AutoApproveAmountLimit เดิม) · ทุกบรรทัดมีผัง (confidence ≥ 0.85) · ไม่มีคำเตือนนอกชุดปล่อยผ่าน ·
     หลักฐานภาษีซื้อ ≠ PaperRequired (หรือ P ติ๊ก "ลงภาษีซื้อเมื่อได้ต้นฉบับ")
   ไม่ผ่าน ⇒ กล่อง "ใบซื้อจากคู่ค้า NextAcc" รอคน 1 คลิก (กฎ #3 เติมครบแล้ว)
```

### 6.5 สถานะรับเงิน → ฝั่งคู่ค้า + 50 ทวิ กลับ

- S รับเงิน (ทางใดก็ได้) ⇒ ขั้น `PartnerDeliver` ใน `PostPaymentAutomationService` ⇒ `ReceiptDelivered` (ใบเสร็จ/ใบกำกับ + ยอด + WHT ที่ S บันทึก + วันที่ + ช่องทาง)
- P: ใบซื้อที่ผูก ⇒ **ร่างการจ่าย** (บัญชีเงินที่จ่าย: ถ้าจ่ายผ่าน gateway ของ S ⇒ ไม่รู้ว่าออกจากบัญชีไหนของ P ⇒ ใช้ "บัญชีจ่ายตั้งต้นสำหรับคู่ค้านี้" ที่ P ตั้ง · ไม่ตั้ง ⇒ ร่างรอคน — ห้ามเดา)
  - P เปิด `WhtAutoIssue` + ร่างการจ่ายถูกยืนยัน (อัตโนมัติถ้า P ตั้งบัญชีจ่ายตั้งต้นและนโยบายอนุญาต) ⇒ `WithholdingTaxCertService.TryAutoIssueForDocumentAsync`/`IssueAsync` ของ P (เลข/ผู้ลงนามของ P — ตาม AUTO_SEND §6.9) ⇒ outbox `WhtCertIssued` ⇒ `WhtCertIntakeService.SubmitAsync(S, Channel=NextAccLink)` ⇒ ด่านเชิงกำหนด (เลขภาษี/คู่/ยอด/อัตรา/ปีภาษี) + ยืนยันอัตโนมัติ (AUTO_SEND §11 ข้อ 1+2)
  - ยอด WHT ของ P ≠ ของ S ⇒ ส่งได้ แต่ intake ไม่ยืนยันเอง + [S→เรา] + [P] เห็นป้าย "ผู้ขายบันทึกหัก 300 · ท่านออก 100"
  - P ยังไม่ถึงเกณฑ์ 1,000 บาทรวมทั้งสัญญา (ท.ป.4/2528 · CLAUDE.md §E) ⇒ ตัวตัดสินของ P บอก "ไม่ต้องหัก" ⇒ ไม่ออก · S เห็นสถานะ "ผู้จ่ายแจ้งว่าไม่ต้องหัก" (ไม่ทวง)
- e-WHT: P จ่ายผ่าน e-WHT ⇒ ไม่มี 50 ทวิ ⇒ P ส่ง event `WhtViaEWithholding` (จาก `Document.WhtViaEWithholding` `Document.cs:671` ของ P) ⇒ S ตั้ง `EvidenceKind=EWithholding` รอหลักฐาน (ไม่ทวง)

### 6.6 ขัดแย้ง / ยกเลิก / ปฏิเสธ

| เหตุ | ผล |
| --- | --- |
| P ปฏิเสธใบซื้อ (เหตุบังคับ) | event `BillRejected` → S: ป้ายบนใบ + E-09 ทันที · ไม่แตะใบของ S |
| S ยกเลิกใบ (void) | `DocumentVoided` → P: ใบซื้อร่าง ⇒ ยกเลิกอัตโนมัติ · อนุมัติแล้ว ⇒ ป้ายแดง "ผู้ขายยกเลิก — ตรวจ/กลับรายการ" (ไม่ย้อนเงียบ) · `CrossTenantDocumentLink.Status=Withdrawn` |
| S ออกใบแทน/ใบลดหนี้ | `CreditNoteDelivered` → ใบลดหนี้ซื้อร่างใน P อ้างใบเดิม (§86/10 ฝั่ง P) |
| payload เวอร์ชันใหม่ของเอกสารเดิม (เช่น e-Tax ประทับเวลาแล้ว) | `DocumentUpdated(version+1)` แนบไฟล์ — P อัปเดตหลักฐาน VAT เท่านั้น (ไม่แตะยอด) |
| ส่งซ้ำ | `EventId = sha256(source company, doc, eventType, version)` ⇒ unique ฝั่งรับ ⇒ no-op |
| เพิกถอนความสัมพันธ์ระหว่างทาง | worker ตรวจสดก่อนส่ง ⇒ Skipped + เหตุ · ของที่ส่งแล้วคงอยู่ (เอกสารภาษีของผู้รับ 5 ปี) |

### 6.7 กลไกข้ามบริษัท (security · idempotency)

- **ไม่มี URL สาธารณะ**: ทุกการส่งเป็นการเรียกในกระบวนการผ่าน `PartnerExchangeService` (DI) — ผู้เช่าทั้งสองอยู่บนแพลตฟอร์ม/ฐานเดียว · ข้อความ "signed" = HMAC-SHA256 ของ payload ด้วยกุญแจแพลตฟอร์ม `PARTNER_EXCHANGE_KEY` (fail-fast ใน production — กฎ #4 C) เก็บทั้ง outbox ฝั่งส่งและแถวฝั่งรับ ⇒ ตรวจย้อนได้ว่าไม่มีใครแก้ระหว่างทาง (เตรียมไว้หากอนาคตแยก instance/ภูมิภาค)
- `PartnerExchangeOutbox` (ฝั่งผู้ส่ง · TenantEntity): `Id, CompanyId, PartnershipId, TargetCompanyId, EventType, SourceEntityId, Version, EventId(unique), PayloadJson, PayloadSha256, Signature, Status(Pending|Sent|Skipped|Failed|Dead), Attempts, NextAttemptAt, ResultRef, Error` — **รวม `WhtCertExchangeOutbox` ที่ AUTO_SEND §7.1 ออกแบบไว้เข้าตารางนี้** (ตัวตั้งเดียว · EventType `WhtCertIssued/Voided/Replaced`)
- เขียนบัญชีผู้รับ: ด้วย `CompanyId=target` ทุก query (raw SQL ใส่เอง) · ไม่มี API ให้ผู้ส่งอ่านข้อมูลอื่นของผู้รับ · audit chain **ทั้งสองบริษัท** (`AddChainedAuditLog` ต่อบริษัท — 201: ล็อกต่อบริษัท)
- worker: `Helpers/JobLock` + SKIP LOCKED + ถอยเวลา 1m→24h ×8 (แบบ 240-3) · Dead ⇒ [S→เรา] + ปุ่มส่งซ้ำ
- `required_call_site_check`: การเขียนข้ามบริษัทอยู่ใน `PartnerExchangeService` ไฟล์เดียว · ต้องเรียก `TradingPartnershipPolicy.CanDeliver` ก่อนเขียน · `DocumentService` ห้ามมี `_crossTenantWorkflow.OnDocumentSentAsync` แบบ try/LogWarning (ล็อกจำนวน = 0)

### 6.8 หน้า "คู่ค้าที่ใช้ NextAcc" (`partners.html` · เมนูผู้ติดต่อ)

```
┌ คู่ค้าที่เชื่อมระบบ ────────────────────────────────────────────────────────────┐
│ [ทั้งหมด] [รอเรายอมรับ 1] [รอเขาตอบ 2] [เชื่อมแล้ว 5] [ระงับ]     [+ ส่งคำขอเชื่อม] │
│ บจก.พี (สนญ.)  เชื่อมแล้ว 12 ต.ค. โดยคุณเอ ↔ คุณบี                                │
│   ส่งให้เขา: ☑ ใบแจ้งหนี้/ใบกำกับ ☑ ใบเสร็จ+สถานะเงิน ☐ ใบแจ้งยอด                  │
│   รับจากเขา: ☑ 50 ทวิ   ·  นโยบายของเขา: ออก 50 ทวิ อัตโนมัติ ✅ · ลงใบซื้ออัตโนมัติ ✖ │
│   เดือนนี้: ส่ง 4 ใบ · เขาอนุมัติ 3 · ปฏิเสธ 0 · 50 ทวิ ได้ 2  [ดูประวัติ] [ระงับ] [เพิกถอน]│
│ ─ กล่องเข้า: ใบซื้อจากคู่ค้า (3 รอตรวจ) ─ ใบเสนอราคาเข้า (1) ─                      │
│ ─ ตั้งค่าของเรา: ☐ ให้คู่ค้าค้นเจอว่าเราใช้ NextAcc · อนุมัติใบซื้อจากคู่ค้าอัตโนมัติ ≤ [50,000] │
│   ออก 50 ทวิ อัตโนมัติเมื่อจ่าย ☑ ผู้ลงนาม [คุณสมชาย ▾] · บัญชีจ่ายตั้งต้น [กสิกร ▾]   │
└──────────────────────────────────────────────────────────────────────────────────┘
```

### 6.9 ข้อมูลที่ข้ามบริษัท + ระยะเก็บ

- ข้าม: เลข/วันที่/ชนิดเอกสาร · ผู้ขาย/ผู้ซื้อ (ชื่อ · เลขภาษี · สาขา · ที่อยู่ — ซึ่งอยู่บนใบอยู่แล้ว) · บรรทัด (คำอธิบาย · จำนวน · ราคา · VAT ต่อบรรทัด) · ยอด · WHT ที่ S บันทึก · วันครบกำหนด · PDF · ไฟล์ e-Tax/XML · สถานะรับเงิน (วัน · ยอด · ช่องทางแบบหยาบ "ออนไลน์/โอน")
- **ไม่ข้าม**: ผังบัญชี/ต้นทุน/กำไร/หมายเหตุภายใน/ไฟล์แนบภายในของ S · ข้อมูลบัตร/บัญชีของ P · ผู้ใช้/อีเมลพนักงาน (เว้นชื่อผู้ลงนามที่อยู่บนเอกสาร)
- ระยะเก็บ: ฝั่งรับ = เอกสารภาษีของผู้รับ ⇒ `RetentionUntil` ตามกฎของผู้รับ (5 ปีนับแต่ปิดบัญชี พ.ร.บ.การบัญชี ม.14 — `RETENTION_LAW_CHECK.md`) + legal hold · ฝั่งส่ง outbox payload ⇒ เก็บ 5 ปีเท่ากัน (เป็นหลักฐานการส่งมอบ) · เพิกถอน/DSR ไม่ลบเอกสารที่ส่งแล้ว (MAX retention)

---

## 7. ใบเสนอราคา → Autopilot (เส้น B)

- ค่าตั้งต่อใบเสนอราคา (ค่าตั้งต้นจากบริษัท): `AcceptAutopilot` = `Off` · `ConvertToInvoice` · `ConvertToTaxInvoice` · `ConvertToDeposit(%)` · `ConvertToRecurring(frequency, start)` + `AutoApproveConverted` (ใช้นโยบาย §4.3) + `SendConverted` (ช่องทาง §4.2 ④⑤)
- งาน `QuotationAcceptAutopilotJob` (JobLock · ทุก 5 นาที · watermark = เวลาเปิดค่าตั้ง — แบบ 240-7): เลือกใบเสนอราคา `QuotationAcceptedAt != null` + ยังไม่มีลูก + ค่าตั้งเปิด ⇒ `ConvertDocumentAsync` (ตัวเดียว) ⇒ อนุมัติ/ส่งตามนโยบาย · กันซ้ำ = มีใบลูกแล้ว (`DocumentConversionProgress` เดิม) + advisory lock ต่อใบ
- ด่าน: `AcceptContentChanged` (เนื้อเปลี่ยนหลังเซ็น) ⇒ หยุด · ใบเสนอราคาหมดอายุก่อนยอมรับ ⇒ หยุด + แจ้ง · ยอมรับผ่านคู่ค้า NextAcc (QuotationFlow) นับเป็นการยอมรับเหมือนกัน
- ลูกค้ายอมรับแล้ว [S→เรา] E-01 "ลูกค้ายอมรับ QT-… (ลงชื่อ: …)" + "ระบบออก INV-… และส่งแล้ว" ในอีเมลเดียวถ้าทันในรอบงาน

---

## 8. ลูกค้าที่ไม่ใช้ NextAcc — อำนวยความสะดวกสูงสุด

### 8.1 พอร์ทัลลูกค้าแบบไม่ต้องจำรหัสผ่าน (ยกระดับ `PortalController`)

- เข้าด้วย **ลิงก์วิเศษ/OTP ทางอีเมล** (อีเมลต้องอยู่ใน `ContactNotifyEndpoint` ของผู้ติดต่อ) — เป็นการ "ล็อกอิน" จริง (เซสชันพอร์ทัล) ⇒ สอดคล้อง 241-2 ที่ห้ามอัปสลิป**แบบไม่ล็อกอิน** · รหัสผ่านเดิมยังใช้ได้ (`PortalAccess` `Intelligence.cs:744-761`)
- ทุกอีเมลเอกสาร/ทวง/ใบเสร็จมีปุ่ม "ดูทุกเอกสารของท่าน" (ลิงก์เข้าพอร์ทัล อายุสั้น 24 ชม. · ผูกผู้รับคนนั้น)
- เมนู: ใบค้างชำระ (จ่ายรวมหลายใบ — 📋 ลิงก์ชำระรวม = backlog เพราะ 241-4 หนึ่งลิงก์ต่อใบ) · ใบเสร็จ/ใบกำกับ/ไฟล์ e-Tax (ดาวน์โหลด PDF/A-3 + XML) · ใบแจ้งยอด (`PortalController.cs:88`) · แจ้งโอน + สลิป (§5.6) · อัปโหลด 50 ทวิ / แจ้ง e-WHT (AUTO_SEND §3.2 — ใช้ endpoint `api/portal/{companyId}/wht-certs` ตามแบบนั้น) · ผู้รับเอกสาร (เพิ่ม/ลบอีเมลของบริษัทตัวเอง — สร้าง `ContactNotifyEndpoint` สถานะ "รอผู้ขายยืนยัน")
- **แก้ข้อมูลภาษีเอง** (ชื่อ · เลขภาษี · สาขา · ที่อยู่ · อีเมลรับใบกำกับ): สร้าง `ContactChangeRequest` → ผู้ขายกด "ยืนยัน" 1 คลิก (เลข 13 หลัก checksum + สาขา 5 หลักตรวจก่อน) — ข้อมูลไม่เปลี่ยนจนผู้ขายยืนยัน (ใบที่ออกแล้วไม่ถูกแก้ — §86/4 ห้ามแก้ย้อนหลัง) · ประโยชน์: แก้ ❌ `BUYER-TAX` ในแผงความพร้อมได้โดยไม่ต้องโทรตาม
- ส่งออกข้อมูลซื้อให้นักบัญชีของลูกค้า: Excel/CSV (MiniExcel) "รายการใบกำกับภาษีซื้อจาก S" (วันที่ · เลขที่ · เลขภาษี/สาขา S · มูลค่า · VAT · WHT) — รูปแบบ **ทั่วไป** ก่อน · รูปแบบนำเข้าเฉพาะโปรแกรม (PEAK/FlowAccount/Express) ⚠️ ต้องได้ไฟล์ตัวอย่างจริงของแต่ละเจ้า (ไม่เดารูปแบบ — คำถาม Q9)

### 8.2 ช่องทางอื่น

| ช่องทาง | ทำอะไร | พึ่ง |
| --- | --- | --- |
| LINE 1:1 / กลุ่ม | การ์ดใบแจ้งหนี้ + ปุ่มชำระ + ปุ่มอัปโหลด 50 ทวิ/สลิป | L2/L3 · `LINE_OA_TERMS_CHECK.md` รูปแบบ A/B (ธุรกรรมเท่านั้น) |
| ทวงอัตโนมัติ | DueSoon/Overdue ต่อรายการ (§4.2 ⑤) + พักเมื่อแจ้งโอน | เดิม |
| ใบแจ้งยอดรายเดือน | `MonthlyStatement` เดิม + ลิงก์พอร์ทัล | เดิม |
| Webhook ระบบลูกค้า (ใช้ ERP อื่น) | ลูกค้าให้ URL ⇒ `invoice.issued` / `receipt.issued` / `payment.confirmed` ลงนาม `WebhookSignature` + `OutboundUrlGuard` (240-2/240-10) + JSON โครงสร้างเดียวกับ `PartnerDocumentPayload` (§6.9) | 📋 ขยาย webhook ต่อผู้ติดต่อ (เดิมเป็นต่อบริษัท/ต่อเอกสาร) |
| อีเมลขาเข้า | ส่งสลิป/50 ทวิ ตอบกลับอีเมลได้ | backlog (ไม่มีโครงรับเมล) |
| e-WHT | ลูกค้าบอก "หักผ่าน e-WHT" บนหน้าชำระ/พอร์ทัล ⇒ หยุดทวงกระดาษ | `EWHT_EVIDENCE_RESEARCH.md` |

---

## 9. ข้อกฎหมายและการปฏิบัติตาม (citation + ความมั่นใจ)

| # | ประเด็น | หลัก | ผลต่อการออกแบบ | มั่นใจ |
| --- | --- | --- | --- | --- |
| L-1 | ออกใบกำกับ/ใบเสร็จอัตโนมัติหลังรับเงิน | ป.รัษฎากร §86 (ออกทันทีเมื่อความรับผิดเกิด) · §78/1 tax point บริการ = รับชำระ · §86/4 รายการ · เลขรัน gap-free | ใช้ `SettlementReceiptPolicy` + `IssueReceiptForPaymentAsync` เดิม (241-6) · ด่าน §86/4 ผู้ซื้อ (241-8) | สูง |
| L-2 | **ส่งใบกำกับเข้าระบบคู่ค้า = ใบกำกับต้นฉบับไหม** | ใบกำกับรูปแบบอิเล็กทรอนิกส์ที่ใช้แทนกระดาษต้องเป็นระบบที่อธิบดีอนุมัติ — e-Tax by Email (ETDA ประทับเวลา) หรือ e-Tax Invoice & e-Receipt (ประกาศอธิบดีฯ เรื่อง e-Tax) · ไม่พบหลักว่าการส่ง PDF ผ่านแพลตฟอร์มทำให้เป็นต้นฉบับ | ใบซื้อฝั่ง P มี `InputVatEvidence`: บริษัท S โหมด by Email ⇒ e-Tax ยังต้องส่งทางอีเมล To ผู้ซื้อ + CC ETDA (244-5) — แพลตฟอร์มแนบไฟล์ประทับเวลาเมื่อได้ (event version+1) ⇒ `EtaxVerified` · S โหมด Direct/H2H ⇒ ไฟล์ที่ส่งกรมแล้ว = หลักฐาน (⚠️ ยืนยันว่าส่งมอบผ่านแพลตฟอร์มนับเป็นการส่งมอบ) · S ไม่ใช้ e-Tax ⇒ `PaperRequired` — P ลงภาษีซื้อเมื่อได้ต้นฉบับ (ระบบเตือน · ไม่บล็อกการบันทึกค่าใช้จ่าย) | กลาง ⚠️ ผู้ทำบัญชี |
| L-3 | ผู้ซื้อยินยอมรับใบกำกับอิเล็กทรอนิกส์ | แนวปฏิบัติ e-Tax: ส่งมอบตามช่องทางที่ผู้ซื้อตกลง | ขอบเขต `SalesDocsAsBills` มีข้อความยินยอม "รับเอกสารภาษีอิเล็กทรอนิกส์ผ่าน NextAcc" (เวอร์ชัน + hash) | กลาง |
| L-4 | 50 ทวิ ออกโดยระบบของผู้จ่าย "อัตโนมัติ" | ม.50 ทวิ ผู้จ่ายเป็นผู้ออก ณ ขณะหัก · ลายมือชื่อผู้มีหน้าที่หัก | ออกในบัญชี P · ผู้ลงนามที่ P ตั้ง (ภาพลายเซ็น/ตรา) · นโยบายอัตโนมัติเปิดโดยผู้มี `TaxFile` ของ P = P มอบหมายให้ระบบของตนออก (เหมือนพิมพ์ใบจากโปรแกรม) · การยืนยันฝั่ง S อัตโนมัติ = คำตัดสินเจ้าของ AUTO_SEND §11 ข้อ 1+2 · ⚠️ ระดับลายมือชื่ออิเล็กทรอนิกส์ (AUTO_SEND L-5) ยังเปิดอยู่ | กลาง |
| L-5 | เกณฑ์หัก 1,000 บาท / ผู้มีหน้าที่หัก | ท.ป.4/2528 · ม.3 เตรส | ตัวตัดสินของ P เท่านั้น (หลักการ #8) | สูง |
| L-6 | PDPA ข้ามผู้เช่า | ม.24(3) สัญญา · ม.24(6) หน้าที่ตามกฎหมาย (ภาษี) · ม.23 แจ้งวัตถุประสงค์ · ม.39 RoPA · NextAcc = ผู้ประมวลผลของแต่ละผู้เช่า; การส่งระหว่างผู้เช่าเป็นตามคำสั่งของผู้ควบคุม (ผู้ส่ง) ไปยังผู้ควบคุมอีกราย (ผู้รับ) | ส่วนใหญ่เป็นข้อมูลนิติบุคคล · ผู้ประกอบการบุคคลธรรมดา = ข้อมูลส่วนบุคคล (ไม่ใช่ sensitive ม.26) ⇒ mask เลข 13 หลักนอก `pii:view` · RoPA `ProcessingActivity`: "แลกเปลี่ยนเอกสารภาษีระหว่างคู่ค้าที่ใช้ NextAcc" · ข้อตกลงการใช้บริการ (DPA) ต้องมีข้อนี้ ⚠️ ฝ่ายกฎหมาย | กลาง |
| L-7 | เปิดเผยการเป็นลูกค้า NextAcc | ข้อมูลความสัมพันธ์ทางการค้าของผู้เช่า (ความลับทางการค้า + PDPA ถ้าบุคคล) | §6.3 ไม่รั่ว · `PartnerDiscoverable` opt-in | สูง |
| L-8 | อีเมลในนามบริษัท | พ.ร.บ.คอมพิวเตอร์ ม.11 (ไม่ปลอมแหล่งที่มา) — 240-5 | From แพลตฟอร์ม + "ในนาม {บริษัท}" · e-Tax ต้อง SMTP บริษัท (244-5) | สูง |
| L-9 | ส่งอีเมลสถานะถึงลูกค้า | อีเมลธุรกรรม ≠ การตลาด (ไม่เข้า ม.11 วรรคสอง เรื่องสแปมเชิงพาณิชย์) | ไม่มีเนื้อหาโฆษณา · ลิงก์ "หยุดรับอีเมลแจ้งเตือนสถานะ" สำหรับชนิดไม่จำเป็น (§10) | กลาง |
| L-10 | ระยะเก็บ | พ.ร.บ.การบัญชี ม.14 (5 ปี) · ป.รัษฎากร §87/3 รายงาน 5 ปี · คืนภาษี 3 ปี | MAX + legal hold ทั้งสองฝั่ง (§6.9) | สูง (เลขมาตราตาม `RETENTION_LAW_CHECK.md`) |
| L-11 | จับคู่เงินอัตโนมัติแล้วออกใบเสร็จ | ใบเสร็จออกเมื่อได้รับเงินจริง | AutoSettle เฉพาะรายการเดินบัญชีจริง (§5.4) · สลิป ≠ เงินเข้า | สูง |
| L-12 | อนุมัติข้ามคำเตือนตามนโยบาย | การควบคุมภายใน (ไม่ใช่กฎหมาย) · §86/4 ห้ามปล่อย | ชุด `NeverPassable` + audit | สูง |

---

## 10. อีเมล/แจ้งเตือนสถานะทุกขั้น

### 10.1 แค็ตตาล็อกเหตุการณ์

| Code | เหตุการณ์ | ถึงลูกค้า | ถึงเรา | ค่าตั้งต้น (เรา) | ปิดได้ |
| --- | --- | --- | --- | --- | --- |
| E-01 | ลูกค้ายอมรับ/เซ็นใบเสนอราคา | ยืนยันการยอมรับ (สำเนาลายเซ็น) | ✅ ทันที | ทันที | เรา: ได้ |
| E-02 | รอบประจำสร้างใบไม่ได้/อนุมัติไม่ได้ | — | ✅ ทันที (`RecurringRunOutcome`) | ทันที | ไม่ได้ (ล้มดัง) |
| E-02b | อนุมัติพร้อมข้อสังเกต/นโยบายปล่อยผ่าน | — | ✅ | digest | ได้ |
| E-03 | ส่งใบแจ้งหนี้แล้ว | **ตัวเอกสาร** (ธุรกรรม) | ✅ | digest | เรา: ได้ |
| E-04 | ลูกค้าเปิดดู/เริ่มชำระ (intent สร้าง) | — | ✅ | ปิด | ได้ |
| E-05 | รับเงินแล้ว + ออกใบเสร็จ/ใบกำกับ + ส่งแล้ว | **ใบเสร็จ/ใบกำกับ** (ธุรกรรม) | ✅ | ทันที | เรา: ได้ |
| E-05b | งานหลังรับเงิน Blocked/Failed | — | ✅ ทันที (`document.post_payment_blocked`) | ทันที | ไม่ได้ |
| E-06 | ลูกค้าแจ้งโอน/ส่งสลิป | รับแจ้งแล้ว รอตรวจ | ✅ | ทันที | ลูกค้า: ได้ |
| E-07 | ได้ 50 ทวิ (ทุกช่องทาง) | ขอบคุณ/ยืนยันรับ | ✅ | digest | ลูกค้า: ได้ |
| E-08 | ทวง 50 ทวิ | คำขอ + ลิงก์อัปโหลด (AUTO_SEND cadence) | — | — | ลูกค้า: ได้ (หยุดทวง) |
| E-09 | คู่ค้าปฏิเสธใบซื้อ | — | ✅ ทันที | ทันที | ไม่ได้ |
| E-09b | ส่งเข้าระบบคู่ค้าล้ม (Dead) | — | ✅ ทันที | ทันที | ไม่ได้ |
| E-10 | คู่ค้ายอมรับ/เพิกถอนการเชื่อม | — | ✅ | ทันที | ไม่ได้ |
| E-11 | ใบเสร็จ/ใบกำกับ (e-Tax) | ตามช่องทาง/EtaxDeliveryRouter | — | — | ไม่ได้ (เอกสารภาษี) |
| E-12 | ทวงชำระ ใกล้/เกินกำหนด | ตามกฎ | — | — | ลูกค้า: ได้ (ไม่ใช่เอกสารภาษี) |
| E-13 | จบกระบวนการ (จ่ายครบ + ใบเสร็จส่ง + 50 ทวิ ได้/ไม่ต้อง) | — | ✅ | digest | ได้ |
| E-14 | เงินเข้าที่จับคู่อัตโนมัติ/รอยืนยัน | — | ✅ | digest | ได้ |
| E-15 | สรุป Autopilot ประจำวัน/สัปดาห์ | — | ✅ | รายวัน 08:40 | ได้ |

### 10.2 กติกา

- ช่อง: อีเมล + กระดิ่งในแอป + กลุ่ม LINE ภายในของบริษัท (ถ้าผูก — ห้ามกลุ่มกลาง Q1(b)) ผ่านตัวส่งแจ้งเตือนเดิม (`_notify.DispatchAsync` + `NotificationEvents`) — event ใหม่เพิ่มใน `Models/Constants/NotificationEvents.cs`
- ผู้รับฝั่งเรา: เจ้าของรายการประจำ (`CreatedBy`) · ผู้ใช้ที่ติดตาม · บทบาทตามค่าตั้ง (เช่น "การเงินรับทุกเรื่องรับเงิน") — `NotificationAudiencePolicy` เดิม
- **digest**: รวมเหตุการณ์ "digest" ต่อผู้ใช้ต่อวัน (เวลาตั้งได้) — ตาราง `AutopilotDigestItem` (หรืออ่านจากแถวสถานะจริงตอนประกอบ — แนะนำแบบหลัง: ไม่มี state ซ้ำ) · งานรวม `AutopilotDigestJob` (JobLock + watermark ต่อบริษัท)
- เพดาน: ถึงลูกค้า ≤ 3/เอกสาร/วัน และ ≤ 20/ผู้รับ/วัน (240-6 ชุดเดียวกัน) · ทวงไม่ซ้ำวันเดียวกับใบเสร็จ · ถึงเรา "ทันที" รวมเป็นก้อนถ้า > 10 เรื่อง/ชั่วโมง
- ภาษา: ถึงลูกค้า = ภาษาเอกสารของผู้ติดต่อ (resolver เดิม) · ถึงเรา = ภาษาผู้ใช้
- ยกเลิกรับ: อีเมลถึงลูกค้าที่ไม่ใช่เอกสารภาษี มีลิงก์ "หยุดรับเรื่องนี้" ⇒ ปิดหัวข้อนั้นในแถว `ContactNotifyEndpoint` (ไม่ลบปลายทาง) + บอกผู้ขายในแผงความพร้อม · เอกสารภาษี/ใบเสร็จยกเลิกไม่ได้ (เปลี่ยนช่องได้)
- นับโควตา: อีเมลผ่าน NextAcc ทุกฉบับนับ (Q4 — L4) · อีเมลถึง "เรา" ไม่นับ (แจ้งเตือนระบบ)

---

## 11. โมเดลข้อมูล · บริการ · งาน · API

### 11.1 ตาราง/คอลัมน์ (ผ่าน `DatabaseMigrationHelper` — ห้าม EF Migrations)

```
RecurringTransaction (+)
  PaymentMode varchar NULL ('Inherit'…)  · PayLinkChannelsJson text NULL · BankAccountId uuid NULL
  PostPayEmailReceipt bool NULL · PostPayIncludePartial bool NULL · DeliveryChannelsJson text NULL (email/line/group/partner)
  SendAtLocalTime time NULL · LeadDays int NULL · StopWhenOverdueCount int NULL
  AutoApprovePolicyJson text NULL  -- { passCodes:["AR-OVERDUE"], setBy, setAt }  (ผู้อ่าน: ApprovalAcknowledgement ผ่าน RecurringTransactionService)
  LinesJson → อยู่ใน TemplateData เดิม (หลายบรรทัด) · PriceScheduleJson text NULL (ขั้นบันได/ปรับ %)
  WhtExpectation varchar NULL · WhtReminderOverrideDaysJson text NULL
Contact (+)  PaymentMode · PayLinkChannelsJson · PreferredBankAccountId · BankAutoSettleDisabled bool
             (+ คอลัมน์ที่ AUTO_SEND §7.1 เสนอ: WhtExpectation, AutoSendDisabled … — ใช้ชุดเดียว)
CompanySettings (+) BankAutoSettleEnabled bool DEFAULT false · PartnerExchangeEnabled bool DEFAULT false
                    QuotationAcceptAutopilotDefault varchar NULL · AutopilotDigestTime time NULL
Company (+) PartnerDiscoverable bool DEFAULT false
Document (+) QuotationAcceptAutopilot varchar NULL · AutopilotPolicyId? (ไม่ — ใช้ RecurringId/SourceDocumentId เดิม)
TradingPartnership (+) CompanyABranchCode, CompanyBBranchCode varchar(5) · InviteTokenHash varchar(64) · InviteExpiresAt · SuspendedByCompanyId · RevokedAt/By/Reason
TradingPartnershipScope (ใหม่) Id, PartnershipId, OwnerCompanyId (ฝั่งที่ตั้ง), Scope varchar, Enabled bool,
             ConsentTextVersion, ConsentTextSha256, ConsentByUserId, ConsentAt, DisabledByUserId, DisabledAt
             UNIQUE(PartnershipId, OwnerCompanyId, Scope)
WorkflowAutomationConfig (+) AutoPostBillsFromPartners bool · AutoPostBillMaxAmount numeric · DefaultPartnerPayBankAccountId uuid · AutoIssueWhtToPartners bool · WhtSignerUserId uuid
CrossTenantDocumentLink (+) EventId varchar(64) UNIQUE · Version int · PayloadSha256 · Signature · InputVatEvidence varchar
PartnerExchangeOutbox (ใหม่ · §6.7)  -- แทน WhtCertExchangeOutbox ของ AUTO_SEND
BankTransaction (+) AutoSettleVerdict varchar NULL · AutoSettleReason text NULL   (ReconciledBy ค่าใหม่ BankAutoSettle)
SlipEvidence (ใหม่ · TenantEntity) Id, CompanyId, DocumentId?, ContactId?, Channel, FileAttachmentId, FileSha256, BankRef, Amount, PaidAt, DestAccountLast4,
             PayerName, OcrJson, PaymentIntentId?, MatchedBankTransactionId?, Status(Received|Duplicate|Rejected|Matched)
             UNIQUE(CompanyId, FileSha256) · UNIQUE(CompanyId, BankRef) WHERE BankRef IS NOT NULL
ContactChangeRequest (ใหม่) Id, CompanyId, ContactId, FieldsJson(before/after), SubmittedVia(Portal), SubmittedByEmail, Status, ReviewedBy/At, Reason
PortalLoginToken (ใหม่) TokenHash, ContactId, Email, ExpiresAt, UsedAt   (หรือขยาย PortalAccess)
```

### 11.2 Helpers (pure · เทสต์ตาราง) / Services

| ชื่อ | ชนิด | หน้าที่ | reuse |
| --- | --- | --- | --- |
| `Helpers/AutopilotReadiness` | pure | §4.4 | ต่อยอด `RecurringEmailReadiness` |
| `Helpers/AutopilotSettingsResolver` | pure | บริษัท › ผู้ติดต่อ › รายการ/ใบ › ตาราง | ขยาย `PostPaymentAutomationPolicy.Effective` |
| `Helpers/ApprovalAcknowledgement` (+`UnattendedWithPolicy`, `NeverPassable`) | pure | §4.3 | ตัวเดิม |
| `Helpers/ArOverdueApprovalWarning` | pure | ป้าย `[AR-OVERDUE]` | แบบ `WhtLineRateWarning` |
| `Helpers/BankReceiptAutoSettle` | pure | §5.4 | `BankMatchScorer` · `DocumentPayLinkPolicy.AmountsFor/WhtForPayment` |
| `Helpers/TradingPartnershipPolicy` | pure | CanDeliver/CanAccept(สิทธิ์) | — |
| `Helpers/PartnerDocumentPayload` + `PartnerExchangeSignature` | pure | payload + HMAC canonical เดียว (กฎ #4 C) | แบบ `WebhookSignature` |
| `Helpers/InputVatEvidence` | pure | §9 L-2 | `EtaxDeliveryRouter` |
| `Helpers/AutopilotStage` | pure | สถานะขั้นของใบ (หน้า Autopilot) | อ่านแถวจริง |
| `AutopilotReadinessService` · `RecurringDryRunService` | service | §4.4–4.5 | |
| `BankReceiptSettleService` | service | ApplyAsync (CreatePayment + match + Enqueue) | `IDocumentService.CreatePaymentAsync` |
| `SlipEvidenceService` | service | §5.6 | `SlipOcrAssistService` |
| `PartnerExchangeService` + `PartnerExchangeWorker` | service/job | §6.7 | `CrossTenantWorkflowService` (ย้ายส่วนส่ง) |
| `QuotationAcceptAutopilotJob` · `AutopilotDigestJob` | job (JobLock) | §7 · §10 | |

### 11.3 API

| Method | Path | สิทธิ์ |
| --- | --- | --- |
| GET | `api/companies/{cid}/recurring/readiness` (query: id หรือ draft body ผ่าน POST) | Accounting.View |
| POST | `…/recurring/{id}/dry-run` · `…/recurring/dry-run` | Accounting.View |
| POST | `…/recurring/{id}/dry-run/send-test` | ผู้ใช้ที่กด (ถึงตัวเอง) |
| PUT | `…/recurring/{id}/approve-policy` | สิทธิ์อนุมัติชนิดเอกสาร |
| GET | `…/contacts/{id}/partner-status` · POST `…/contacts/{id}/partner-invite` | Accounting.View · CompanySettingsEdit |
| GET/POST | `…/partners[/{id}/scopes|suspend|revoke]` · `…/partner-inbox[/{linkId}/approve|reject]` | ตามขอบเขต (§6.2) |
| GET/POST | `api/public/partner-invite/{token}` (ข้อมูลหน้า · ไม่มีข้อมูลผู้รับ) · `…/accept` (ต้องล็อกอิน) | AllowAnonymous + rate limit / Authorize |
| GET | `…/autopilot/pipeline?status=&from=` · `…/autopilot/issues` | Accounting.View |
| GET/POST | `…/bank/auto-settle/queue` · `…/bank/auto-settle/{lineId}/confirm|reject` · `…/bank/auto-settle/simulate?days=30` | Bank.Reconcile |
| POST | `api/portal/{cid}/login-link` · `api/portal/{cid}/slips` · `api/portal/{cid}/contact-change` · `api/portal/{cid}/exports/purchases` | portal |
| POST | `…/contact-change-requests/{id}/approve|reject` | Contacts.Edit |

ทุก endpoint: `CompanyId == companyId` · business error = `BusinessRuleException(RuleCode, LegalReference)` · RuleCode ใหม่: `AUTOPILOT-READINESS` · `APPROVE-UNATTENDED-POLICY` · `BANK-AUTO-SETTLE` · `SLIP-DUP` · `PARTNER-SCOPE` · `PARTNER-CONSENT` · `PARTNER-AMBIGUOUS-BRANCH`

### 11.4 หน้า Autopilot (`autopilot.html`) — "ใบนี้อยู่ขั้นไหน"

```
┌ Autopilot — 30 วันล่าสุด ───────────────────────────────────────────────────────────────┐
│ จบแล้ว 41 · กำลังเดิน 18 · ติดเรื่องที่ระบบทำต่อไม่ได้ 3 ❗                                  │
│ เอกสาร       ลูกค้า     สร้าง  อนุมัติ  ส่ง   ชำระ   ใบเสร็จ  ส่งใบเสร็จ  คู่ค้า  50ทวิ   สถานะ  │
│ INV-…0012   บจก.พี     ✅     ✅      ✅    ✅GW   ✅RT-451  ✅e-Tax    ✅รับ   ✅      จบ      │
│ INV-…0019   บจก.คิว    ✅     ✅      ✅    🟡รอยืนยันโอน ·   ·          —      ·       [ยืนยัน] │
│ INV-…0021   หจก.อาร์   ✅     ❌ ค้างชำระ 2 ใบ (AR-OVERDUE)                            [เปิดใบร่าง]│
│ RT-…0450    บจก.เอส    ·      ·       ·     ✅    ✅       ❌ ผู้ซื้อขาดสาขา §86/4      [แก้ผู้ติดต่อ]│
└───────────────────────────────────────────────────────────────────────────────────────────┘
```

- เป็น **read model** จากแถวจริง (Document · DocumentEmailLogs · PaymentIntent · Payments · PostPaymentAutomationRuns · EtaxInvoice · CrossTenantDocumentLink · WhtCreditReceived/Intake · BankTransaction) — `AutopilotStage.Of(facts)` ตัวเดียว · ไม่มีตารางสถานะซ้ำ (หลักการ #3/#4)
- "เรื่องที่ระบบทำต่อไม่ได้" = union ของ: รอบประจำล้ม · อนุมัติอัตโนมัติไม่ผ่าน · งานหลังรับเงิน Blocked/Failed · คิวอีเมล Failed · outbox คู่ค้า Dead · intake 50 ทวิ ไม่ผ่านด่าน · เงินเข้ารอยืนยัน > 3 วัน — แต่ละแถวมีปุ่มไปต่อ (ไม่ใช่แค่ข้อความ)
- query รวมแบบชุด (ไม่ N+1) · เปิดได้จากแดชบอร์ด (การ์ด "Autopilot ติด 3 เรื่อง")

---

## 12. แผนลงมือ (รอบ 248+) — เรียงตามคุณค่าถึงเจ้าของเร็วสุด

> พึ่งพา: L2/L4 (LINE) แตะเส้นส่งชุดเดียวกัน — รอบ 248–249 ใช้ "อีเมลผ่าน Resolver" ก่อน (L1 ต่อสายในรอบ 249) · กล่องรับ 50 ทวิ = AUTO_SEND เฟส 244 (**ยังไม่มีในโค้ด**) ต้องเสร็จก่อนรอบ 253

### รอบ 248 — รายการประจำ Autopilot (ไม่มีคู่ค้า · ไม่มีธนาคาร)
งาน: wizard 8 ขั้น (`recurring.html` แยกเป็นส่วน · หลายบรรทัด) · คอลัมน์ RecurringTransaction §11.1 · `AutopilotSettingsResolver` (รายการ › ผู้ติดต่อ › บริษัท) ต่อเข้า `EnsureLinkAsync`/`PostPaymentAutomationService.SettingsForAsync`/`OnRecurringDocumentCreatedAsync` · `AutopilotReadiness` + แผง · dry-run · `ApprovalAcknowledgement.UnattendedWithPolicy` + `[AR-OVERDUE]` · `Contact.PaymentMode/PayLinkChannelsJson`
เทสต์ยอมรับ:
- นโยบายปล่อย `AR-OVERDUE` ⇒ ใบประจำอนุมัติ + audit `APPROVE-UNATTENDED-POLICY` · **ทิศตรงข้าม:** ไม่ตั้งนโยบาย ⇒ ค้างร่างเหมือนเดิม (รอบ 247 ไม่ถดถอย) · นโยบายพยายามปล่อย §86/4 ⇒ บันทึกไม่ได้ (400 + เหตุ) · ผู้ตั้งนโยบายออกจากบริษัท ⇒ ไม่ปล่อย
- รายการตั้ง `TransferOnly` ⇒ ลิงก์ไม่มีบัตร · **ทิศตรงข้าม:** `Inherit` ⇒ ตามบริษัทเหมือนเดิม
- dry-run: นับแถว Documents/EmailQueue/DocumentEmailLogs/PaymentLinks ก่อน-หลัง = เท่ากัน (Db test) · ผลอนุมัติจำลอง = ผลจริงของรอบนั้น (golden)
- readiness: JS ไม่มีรหัสกติกา (grep) · ผู้ซื้อขาดสาขา ⇒ `BUYER-TAX` ❌ · **ทิศตรงข้าม:** ผู้ซื้อบุคคลธรรมดา ⇒ ไม่ฟ้อง
`required_call_site_check`: `RecurringTransactionService` อนุมัติด้วย `UnattendedWithPolicy` + ส่งนโยบายจากแถว · dry-run ไม่เรียก `SaveChangesAsync`/`Send*`/`EnsureLinkAsync` · resolver ถูกเรียกในผู้อ่าน 3 จุด

### รอบ 249 — อีเมลสถานะ + หน้า Autopilot + ทุกทางรับเงินเข้างานหลังรับชำระ + ใบเสนอราคาแปลงอัตโนมัติ
งาน: `NotificationEvents` E-01…E-15 + digest · `autopilot.html` + `AutopilotStage` · G-6/G-T6: `EnqueueAsync` ใน `SettlementPostingService` (`:313`) · ยืนยันสลิปพอร์ทัล · `PaymentImportTarget` · CMS/Lodging · ยืนยัน ManualSlip · `QuotationAcceptAutopilotJob` · L1 Resolver เข้า `PostPaymentAutomationService` (แทน `contact?.Email` `:260`)
เทสต์: settlement หลายใบ ⇒ แถว run ต่อการชำระ · **ทิศตรงข้าม:** ปิดค่าตั้ง ⇒ ไม่มีแถว · ยอมรับใบเสนอราคา ⇒ INV 1 ใบแม้งานรันสองเครื่อง (Db JobLock) · เนื้อเปลี่ยนหลังเซ็น ⇒ ไม่แปลง + E-01 บอกเหตุ · digest รวม 1 อีเมล/ผู้ใช้/วัน
`required_call_site_check`: รายชื่อผู้เรียก `EnqueueAsync` ครบ 8 จุด (ตาม AUTO_SEND §9 เฟส 243) · `QuotationAcceptAutopilotJob` ผ่าน `JobLock` + เรียก `ConvertDocumentAsync` (ห้ามสร้าง Document เอง)

### รอบ 250 — โอนเข้าบัญชีปิดหนี้อัตโนมัติ (§5)
งาน: `BankReceiptAutoSettle` + `BankReceiptSettleService` + คิว §5.7 + simulate 30 วัน · G-T7 สลิปพอร์ทัล → `ManualSlip` intent (+ รายงานแถว `[PENDING-VERIFY]` เดิม) · `SlipEvidenceService` (hash/ref กันซ้ำ) · ข้อความ "ระบุเลขใบ" ในอีเมล/หน้าชำระ
เทสต์ (ตาราง 5.5 ทุกแถว): a ⇒ Auto + WHT 300 + Pending 50 ทวิ 1 แถว + ใบเสร็จส่ง · **ทิศตรงข้าม:** ยอดเท่ากันสองใบ ไม่มีเลขอ้างอิง ⇒ Suggest (ไม่ Auto) · สลิปไม่มีรายการเดินบัญชี ⇒ ใบยังค้าง · สลิปเดิมส่งซ้ำ ⇒ `SLIP-DUP` · รายการเดินบัญชีจาก "พิมพ์มือ" ⇒ ไม่ Auto · ยกเลิกการจับคู่ ⇒ การชำระ void + ใบเสร็จต้องยกเลิกตามเส้นเดิม · kill-switch AI ⇒ Auto ยังทำงาน (ไม่มี AI ในด่าน)
`required_call_site_check`: `BankReceiptSettleService` เรียก `BankReceiptAutoSettle.Decide` → `DocumentPayLinkPolicy.WhtForPayment` → `CreatePaymentAsync` → `EnqueueAsync` ตามลำดับ · ห้าม `Payments.Add(` ใน `PortalService`

### รอบ 251 — คู่ค้า NextAcc: ฐานที่ปลอดภัย (P-1…P-4, P-10)
งาน: แก้ Invite ไม่รั่ว + TaxId+Branch + สิทธิ์ · `TradingPartnershipScope` + ข้อความยินยอม · `partner-status`/`partner-invite` + หน้า `partner-invite.html` + `partners.html` · `PartnerDiscoverable` · RoPA
เทสต์: เชิญเลขภาษีที่ไม่มีในระบบ vs มี ⇒ คำตอบ API ไบต์เดียวกัน (ไม่รั่ว) · สองบริษัทเลขเดียวกันต่างสาขา ⇒ ไม่ส่งจนเลือก · ผู้ใช้ไม่มีสิทธิ์ ⇒ 403 · **ทิศตรงข้าม:** คู่ Accepted เดิม (ก่อนรอบนี้) ยังทำงาน — migration ตั้งขอบเขต `QuotationsAndPOs` ให้คู่เดิม (ไม่เปิดขอบเขตใหม่เงียบ ๆ)

### รอบ 252 — ส่งเอกสารขายเป็นใบซื้อฝั่งคู่ค้า (§6.4, 6.6, 6.7)
งาน: `PartnerExchangeOutbox` + worker + `PartnerExchangeService` · แทน try/LogWarning (`DocumentService.cs:7606`) · ใบซื้อร่าง + `InputVatEvidence` + BillAutoPost · reject/void/CN events · ขั้น `PartnerDeliver` ในงานหลังรับเงิน
เทสต์: อนุมัติ INV ⇒ P มีใบซื้อร่าง 1 ใบ (ส่งซ้ำ 3 ครั้ง ⇒ 1 ใบ) · P ปฏิเสธ ⇒ S เห็น + E-09 · S void ⇒ ใบซื้อร่างของ P ยกเลิก · **ทิศตรงข้าม:** ขอบเขตปิด/ระงับ ⇒ ไม่มีอะไรข้ามบริษัท อีเมลเดิมทำงาน · ใบของ S ที่ไม่ใช่ e-Tax ⇒ `PaperRequired` (ไม่ลงภาษีซื้ออัตโนมัติ) · query ฝั่งรับทุกจุดมี CompanyId ของผู้รับ · kill-switch AI ⇒ ใบซื้อยังเติมผังจากประวัติ/ค่าตั้งต้น

### รอบ 253 — สถานะเงิน + 50 ทวิ กลับอัตโนมัติ (§6.5) — **พึ่ง AUTO_SEND เฟส 244 (กล่องรับ)**
งาน: `ReceiptDelivered` ⇒ ร่างการจ่ายใน P · `WhtAutoIssue` ⇒ `WithholdingTaxCertService` ของ P ⇒ outbox `WhtCertIssued` ⇒ `WhtCertIntakeService.SubmitAsync(Channel=NextAccLink)` · e-WHT event (= AUTO_SEND เฟส 246 ย้ายมาใช้ `TradingPartnership` + outbox รวม)
เทสต์: ชำระผ่าน gateway ⇒ S: ใบเสร็จ + intake Received · P: ใบซื้อปิด + 50 ทวิ ออก — **ครั้งเดียว** แม้ webhook ซ้ำ · ยอด WHT ต่างกัน ⇒ ไม่ยืนยันอัตโนมัติ · ต่ำกว่า 1,000 รวมสัญญา ⇒ ไม่ออก + S ไม่ทวง · P void ใบ ⇒ S กลับรอใบ · **ทิศตรงข้าม:** P ไม่เปิด `WhtAutoIssue` ⇒ 50 ทวิ ไม่ออกเอง (ใบเข้า "รอออกใบ" ของ P)

### รอบ 254 — พอร์ทัลลูกค้าที่ไม่ใช้ NextAcc (§8)
งาน: ลิงก์วิเศษ/OTP · ดาวน์โหลดเอกสาร/e-Tax · แจ้งโอน (ผ่าน `SlipEvidenceService`) · อัปโหลด 50 ทวิ (AUTO_SEND 244) · `ContactChangeRequest` · ส่งออกข้อมูลซื้อ (ทั่วไป) · webhook ต่อผู้ติดต่อ
เทสต์: ลิงก์วิเศษใช้ครั้งเดียว/หมดอายุ · อีเมลที่ไม่อยู่ในปลายทาง ⇒ ไม่ได้ลิงก์ (ข้อความเดียวกัน ไม่รั่ว) · แก้เลขภาษีผิด checksum ⇒ ปฏิเสธ · **ทิศตรงข้าม:** คำขอแก้ยังไม่ยืนยัน ⇒ ใบใหม่ใช้ข้อมูลเดิม

### รอบ 255+ (backlog)
ลิงก์ชำระรวมหลายใบ · ผ่อนผ่านลิงก์ · ราคาจากปริมาณ (มิเตอร์/API) · อีเมลขาเข้า (สลิป/50 ทวิ) · รูปแบบนำเข้าเฉพาะโปรแกรม (PEAK/FlowAccount/Express) · bounce webhook · คืนเงินอัตโนมัติจาก CN · ใบแจ้งยอดข้ามบริษัท

### เอกสารที่ต้องขยับในแต่ละรอบ (หลักการ #10)
`DOCUMENT_FLOW.md` (รายการประจำ · หลังรับเงิน · ธนาคาร · คู่ค้า) · `ACCOUNT_STRUCTURE.md` (`TradingPartnership` ระดับบริษัท · ค่าตั้งบริษัทใหม่) · `TEST_PLAN.md` · `CHANGELOG.md` · `AUTO_SEND_WHT_DESIGN.md` §1.1/§7.1 (แก้ "ไม่มี PartnerConnection" + ยกเลิก `TenantPartnerLink`/`WhtCertExchangeOutbox` ⇒ ชี้มาเอกสารนี้) · DECISIONS (คำตอบ §13) · RoPA

---

## 13. คำถามถึงเจ้าของ (การตัดสินใจทางธุรกิจเท่านั้น)

| # | คำถาม | ข้อเสนอทีม |
| --- | --- | --- |
| Q1 | ปุ่ม "ตรวจว่าลูกค้าใช้ NextAcc" — ยอมให้ตอบ "ใช้/ไม่ใช้" ตรง ๆ ไหม หรือบอกได้แค่สถานะการเชื่อม (เว้นบริษัทที่เปิด "ให้คู่ค้าค้นเจอ") | บอกได้แค่สถานะการเชื่อม + opt-in `PartnerDiscoverable` (ค่าตั้งต้น **ปิด**) — กันใช้ NextAcc เป็นเครื่องสอดส่องลูกค้าคู่แข่ง |
| Q2 | ใบซื้อที่ส่งเข้าระบบคู่ค้า: ให้คู่ค้าเปิด "ลงบัญชีอัตโนมัติ" ได้ไหม | ได้ — ปิดเป็นค่าตั้งต้น · มีเพดานยอดต่อคู่ · ใบที่ไม่ใช่ e-Tax ไม่ลงภาษีซื้ออัตโนมัติ |
| Q3 | นโยบายอนุมัติข้ามคำเตือน (§4.3) — ให้ตั้ง**ต่อรายการประจำ**เท่านั้น หรือมีค่าบริษัท/ต่อผู้ติดต่อด้วย | ต่อรายการ + ต่อผู้ติดต่อ (ไม่มีค่าบริษัท — กันปล่อยทั้งบริษัทโดยไม่ตั้งใจ) |
| Q4 | ปิดหนี้จากรายการเดินบัญชีอัตโนมัติ (§5.4) ค่าตั้งต้น เปิด/ปิด | **ปิด** · เปิดแล้วแสดงผลจำลอง 30 วันก่อน |
| Q5 | ใบเสนอราคาที่ลูกค้ายอมรับ: ค่าตั้งต้นบริษัทให้แปลงเป็นอะไร | `Off` (ตั้งต่อใบ) — เพราะหลายธุรกิจต้องส่งของก่อนออกใบ |
| Q6 | เพิ่มคำเตือน "เกินวงเงินเครดิตลูกค้า" (ตอนนี้ไม่มีตัวเตือนจาก `Contact.CreditLimit`) ไหม | เพิ่ม — เป็นคำเตือน (ไม่บล็อก) และเลือกปล่อยได้ด้วยนโยบาย |
| Q7 | อีเมลสถานะถึงทีมเรา: ค่าตั้งต้น "ทันที" หรือ "สรุปรายวัน" | ล้ม/ติด = ทันที · สำเร็จ = สรุปรายวัน 08:40 |
| Q8 | ธนาคาร/ผู้ให้บริการ feed ที่ลูกค้าใช้จริงในโปรดักชัน (OpenBanking/BankFeed เชื่อมเจ้าใด) | ต้องการรายชื่อเพื่อจัดลำดับ — ถ้ายังไม่มี เส้นโอนพึ่งการนำเข้า statement รายวัน |
| Q9 | ส่งออกข้อมูลซื้อให้นักบัญชีลูกค้า — ต้องการรูปแบบโปรแกรมใดก่อน (ขอไฟล์ตัวอย่างนำเข้าจริง) | เริ่มรูปแบบทั่วไป (Excel) · เพิ่มเจ้าที่มีไฟล์ตัวอย่าง |
| Q10 | การเชื่อมคู่ค้าเป็นฟีเจอร์ทุกแพ็กเกจ หรือแพ็กเกจสูง | ทุกแพ็กเกจ (ผลเครือข่าย — ลูกค้าดึงคู่ค้ามาใช้ NextAcc) · เพดานจำนวนคู่ต่อแพ็กเกจได้ |
| Q11 | ฝ่ายกฎหมาย/ผู้ทำบัญชี ยืนยัน L-2 (ใบกำกับที่ส่งผ่านแพลตฟอร์ม ใช้ลงภาษีซื้อได้เมื่อใด) + เพิ่มข้อการแลกเปลี่ยนข้ามผู้เช่าใน DPA | ต้องยืนยันก่อนรอบ 252 เปิด `BillAutoPost` ลงภาษีซื้อ |

---

## ภาคผนวก — ไฟล์ที่ตรวจในการวิเคราะห์นี้

`Models/Entities/RecurringTransaction.cs` · `Services/Implementations/RecurringTransactionService.cs` · `wwwroot/pages/recurring.html` · `Helpers/RecurringRunOutcome.cs` · `Helpers/ApprovalAcknowledgement.cs` ·
`Services/Implementations/DocumentService.cs` (6560 · 6575 · 6958 · 7602-7608 · 13335 · 14716 · 14819 · 21797 · 22636-22650 · 22884) · `Services/Implementations/PostPaymentAutomationService.cs` · `Helpers/PostPaymentAutomationPolicy.cs` ·
`Services/Payments/Handlers/DocumentPaymentHandler.cs` · `Services/Payments/PaymentIntentService.cs` · `Services/Payments/Providers/ManualSlipPaymentProvider.cs` · `Controllers/PublicPaymentController.cs` · `Controllers/PaymentGatewayController.cs:912` ·
`Helpers/DocumentPayLinkPolicy.cs` · `Services/Implementations/DocumentPayLinkService.cs` · `Helpers/PaymentQrPolicy.cs` · `Services/Implementations/PaymentQrService.cs` · `Helpers/EtaxDeliveryRouter.cs` · `Services/Implementations/DocumentEmailService.cs` ·
`Services/Implementations/EmailScheduleService.cs` · `Services/Implementations/ContactRecipientResolver.cs` · `Helpers/ContactRecipientRule.cs` · `Helpers/NotifyEndpointKind.cs` · `Controllers/PublicQuotationController.cs` · `Services/Implementations/SignRequestService.cs` ·
`Models/Entities/CrossTenantWorkflow.cs` · `Services/Implementations/CrossTenantWorkflowService.cs` · `Controllers/CrossTenantWorkflowController.cs` · `Models/Enums/AllEnums.cs:845-866` · `Models/Entities/Company.cs:9-10` ·
`Controllers/PortalController.cs` · `Services/Implementations/PortalService.cs:507-582` · `Services/Implementations/BankService.cs:764-900` · `BankService.StatementImport.cs` · `BankService.MatchCandidates.cs` · `BankService.MatchResolution.cs` ·
`Services/Implementations/BankFeedService.cs:380-440` · `Services/Implementations/OpenBankingService.cs` · `Controllers/BankController.cs:137-292` · `Services/Implementations/SlipOcrAssistService.cs` · `Services/Payments/PlatformTransferReconciliationService.cs` ·
`Services/Implementations/WithholdingTaxCertService.cs` · `Services/Implementations/WhtCreditService.cs` · `Controllers/V1/DocumentsV1Controller.cs` · `Models/Entities/CompanySettings.cs:179-343` · `Models/Constants/NotificationEvents.cs`
