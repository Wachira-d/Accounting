# ทีม F — โซ่ "บิลรายเดือน → รับเงินผ่าน gateway → ใบเสร็จ/ใบกำกับ → 50 ทวิ กลับจากลูกค้าที่ใช้ระบบเรา → LINE" (อ่านอย่างเดียว) · 2026-11-11

HEAD ที่ตรวจ: `FETCH_HEAD 74e1bb37` (claude/booking-service-features-sdemob · รอบ 258) · path ข้างล่างนับจาก `Accounting/` · ทุกข้อเปิดโค้ดตรงจุดแล้ว ·
ยังไม่ได้คอมไพล์/รันในเครื่องนี้ (ไม่มี .NET SDK) — ผลข้างล่างมาจากการอ่านโค้ด ไม่ได้ทดสอบกับบริษัทจริง

สัญลักษณ์: ✅ อัตโนมัติและใช้ได้ · ⚙️ ใช้ได้แต่ต้องตั้งค่า (ค่าตั้งต้นปิด) · 👤 ต้องมีคนกด · ❌ ไม่มี/พัง

## คำตอบสั้นถึงเจ้าของ

**ทำได้ "เกือบสุดทาง" ฝั่งเรา แต่ยังไม่สุดทางฝั่งลูกค้า และ LINE ขาดช่วงหลังรับเงิน**
- ฝั่งเรา (ตั้งบิลรายเดือน → อนุมัติ → ส่งอีเมล/LINE พร้อมลิงก์ชำระ → ลูกค้าจ่ายผ่าน Omise → ลงรับชำระ → ออกใบเสร็จ/ใบกำกับภาษี §78/1 พร้อมเลขที่ → e-Tax → ส่งใบเสร็จทางอีเมล) **อัตโนมัติได้ครบ** เมื่อเปิดค่าตั้งให้ครบ แต่**ค่าตั้งต้นของเกือบทุกขั้นคือปิด**
- **ใบเสร็จทาง LINE ยังไม่มี** (ข้อ M4 ของทีม A ยังเปิดอยู่) — ลูกค้าที่ไม่มีอีเมลจะไม่ได้ใบเสร็จทางไหนเลย
- **ลูกค้าที่เป็นบริษัทในระบบเรา:** ส่วนที่มีจริงคือ ใบแจ้งหนี้ของเราเข้าเป็น "ใบซื้อร่าง" ฝั่งเขา และ 50 ทวิ ที่เขา "ออกแล้ว" ส่งกลับเข้ากล่องรับ 50 ทวิ ของเราเองโดยไม่มีใครต้องอัปโหลด
  แต่ **ยังมีจุดที่คนต้องกด 2 จุดในระบบของเขา** (อนุมัติใบซื้อร่าง + บันทึกจ่ายชำระ) — การจ่ายผ่าน gateway ของเรา**ไม่แจ้ง**ระบบของเขาว่าจ่ายแล้ว ⇒ ถ้าเขาไม่บันทึกจ่ายเอง 50 ทวิ จะไม่ถูกออก
  และ**ใบกำกับภาษีที่ออกตอนรับเงินไม่ถูกส่งไปหาเขา** (ข้อ F-H2)

## 1. ตารางโซ่ทั้งเส้น

| # | ขั้น | ผล | หลักฐาน (file:line) |
| --- | --- | --- | --- |
| 1.1 | ตั้งรายการประจำรายเดือน + สร้างใบอัตโนมัติ | ✅ | `Services/Implementations/RecurringTransactionService.cs:92,104` (Frequency/IntervalMonths · `RecurringSchedule`) · ตามรอบที่พลาด `:512` (`CatchUp`) |
| 1.2 | อนุมัติอัตโนมัติ (เลขจริงออกตอนอนุมัติ) | ⚙️ ติ๊ก "อนุมัติอัตโนมัติ" (`fAutoApprove` ค่าตั้งต้นปิด `Models/Entities/RecurringTransaction.cs:40`) · ติ๊กส่งอีเมล ⇒ บังคับอนุมัติให้ (`RecurringTransactionService.cs:273`) | ผ่านนโยบายข้ามคำเตือน + ผู้รับผิดชอบ `RecurringTransactionService.cs:1007-1025` · ใบค้างชำระไม่หยุดใบใหม่ (คำตัดสิน 256-1) · อนุมัติล้ม ⇒ ป้ายบนใบ + แจ้งทีม `:1056-1077` |
| 1.3 | PDF | ✅ แนบในอีเมล | `EmailScheduleService.cs` (เส้นส่งเอกสาร) · การ์ด LINE **ไม่แนบไฟล์** `Helpers/LineDocumentCard.cs:11` |
| 1.4 | ส่งทางอีเมล | ⚙️ ติ๊ก "📧 ส่งอีเมลเอกสาร" (`fAutoSendEmail` ค่าตั้งต้นปิด `RecurringTransaction.cs:50`) หรือสร้างกฎ "เมื่อรายการประจำสร้างเอกสาร" ที่หน้าตารางส่ง | `EmailScheduleService.cs:292-395` (กฎชนะธง · กฎเมื่ออนุมัติครอบแล้วไม่ส่งซ้ำ `:319-337`) |
| 1.5 | ส่งทาง LINE | ⚙️ ต้องสร้าง**กฎ**ในหน้า ตารางส่ง (email-schedule.html) ติ๊ก "LINE ผู้ใช้/กลุ่ม LINE ของผู้ติดต่อ" + LINE OA ของบริษัทพร้อม + ผู้ติดต่อผูก LINE แล้ว · **ธง `fAutoSendEmail` อย่างเดียวไม่ส่ง LINE** (กฎเสมือน `ChannelEmail=true` ช่อง LINE=false `Models/Entities/EmailSchedule.cs:96-102`) | trigger ที่ส่ง LINE ได้ `Helpers/ContactLineChannel.cs:29-32` · เข้าคิว `EmailScheduleService.cs:1237-1280` · หน้า recurring.html **ไม่มีคำว่า LINE เลย** (0 จุด) ⇒ ผู้ใช้ไม่รู้ว่าต้องไปตั้งที่อื่น |
| 1.6 | ลิงก์ชำระในข้อความ | ⚙️ อีเมล: ติ๊ก "แนบลิงก์ชำระเงิน" (`PayLinkDefaultEnabled=false` `Models/Entities/CompanySettings.cs:337`) หรือ "ช่องทางรับเงินของรายการนี้" (`fPaymentMode` recurring.html:219) · LINE: ปุ่ม "ชำระเงิน" บนการ์ดเมื่อใบมีลิงก์ | อีเมล `EmailScheduleService.cs:124-128,1200-1202` (ไม่เก็บ token ในคิว) · LINE `Helpers/LineDocumentCard.cs:53-54` + `Services/Implementations/ContactLineQueueService.cs:294` |
| 2.1 | ช่องทางรับเงินต่อบริษัท/ผู้ติดต่อ | ⚙️ payment-settings.html (Omise: คีย์ test/live · ช่องทาง `EnabledMethodsJson`) · ชั้นผู้ติดต่อ `Contact.PaymentMode/PayLinkChannelsJson` (`Models/Entities/Document.cs:1130-1132`) | ชั้น ตาราง › รายการ › ผู้ติดต่อ › บริษัท `EmailScheduleService.cs:344,410-424` |
| 2.2 | test → live | 👤 ด่านเปิดเงินจริง: ทดสอบคีย์ + webhook ผ่าน (`LastTestPassedAt`) + คีย์ live ตรวจผ่าน | `Services/Payments/PaymentProviderConfigService.cs:120-170` (`PaymentLiveGate` · audit) · ค่าตั้งต้น `Mode=Test` `Models/Entities/Payments.cs:34` · เงินโหมดทดสอบไม่มีผลทางบัญชี `Services/Payments/Handlers/DocumentPaymentHandler.cs:41` |
| 2.3 | ยืนยัน webhook (Omise re-fetch) | ✅ | `Services/Payments/Providers/OmisePaymentProvider.cs:633` (`GET /events/{id}`) |
| 2.4 | โอน + แนบสลิป (ManualSlip) | 👤 คนตรวจสลิปแล้วกดยืนยัน | `Services/Payments/Providers/ManualSlipPaymentProvider.cs:2` · `Controllers/PublicPaymentController.cs:217` |
| 3.1 | gateway จ่ายแล้ว → บันทึกรับชำระ + ตัดหนี้ | ✅ กันซ้ำด้วย Reference | `DocumentPaymentHandler.cs:38-112` → `DocumentService.CreatePaymentAsync` (`DocumentService.cs:15226`) |
| 3.2 | ออกใบเสร็จ / ใบกำกับภาษี-ใบเสร็จ (§78/1) | ✅ ใบแจ้งหนี้ (VAT ยังไม่ถึงกำหนด) รับครบงวดเดียว ⇒ ใบเสร็จถือ VAT = ใบกำกับ ณ วันรับเงิน · ใบกำกับที่ออกไปแล้ว ⇒ ใบรับเปล่า · เลขออกทันที (ผู้บันทึก = `"payment-gateway"` ไม่ใช่ GUID ⇒ ถือว่ามีสิทธิ์อนุมัติ) | `DocumentService.cs:15570-15625` (`ReceiptIssuePolicy` · `SettlementReceiptPolicy.CarriesTaxInvoiceRole`) · ค่าตั้งต้นบริษัท `ReceiptIssueMode.Combined` (`CompanySettings.cs:386`) ⇒ ออกใบแยก · **ออกใบล้ม = `LogWarning` แล้วข้าม** (`DocumentService.cs:~15622`) — มีตาข่ายเฉพาะเมื่อเปิดส่งใบเสร็จอัตโนมัติ (งานหลังรับชำระออกให้ใหม่ `PostPaymentAutomationService.cs:325-337`) |
| 3.3 | e-Tax | ⚙️ เมื่อบริษัทเปิด e-Tax | hook หลังออกใบเสร็จ `DocumentService.cs:15674-15675` → `Services/Implementations/IssuedDocumentHooks.cs:44-47` |
| 3.4 | ส่งใบเสร็จทางอีเมล | ⚙️ ติ๊ก "หลังลูกค้าชำระ: ออกใบเสร็จ … แล้วส่ง" (`PostPayEmailReceipt=false` `CompanySettings.cs:341` · หน้า ตั้งค่า › การตั้งค่าเอกสาร › ส่งเอกสารอัตโนมัติ `wwwroot/pages/settings.html:505-511`) · ทับได้ต่อรายการประจำ/ผู้ติดต่อ/ตาราง | `PostPaymentAutomationService.cs:58-90,348-365` · e-Tax by Email ไปทาง e-Tax `:383-392` · ค่าตั้งต้น "เฉพาะงวดที่ชำระครบ" (`PostPayIncludePartial=false`) |
| 3.5 | ส่งใบเสร็จทาง LINE | ❌ | `PostPaymentAutomationService.cs` ไม่มีเส้น LINE (คำว่า line ไม่มีในไฟล์นอกจาก `:293` ที่ไม่เกี่ยว) · trigger LINE มีแค่ 4 (`ContactLineChannel.cs:29-32`) · แต่ค่าตั้งต้นปลายทาง LINE ผู้ใช้ติ๊ก "ใบเสร็จ" ให้ (`Helpers/NotifyEndpointKind.cs:131`) ⇒ **M4 ทีม A ยังเปิด** |
| 4.1 | ลูกค้าหัก ณ ที่จ่ายบนหน้าชำระ | ✅ นิติบุคคล/หน่วยราชการ ⇒ ยอดในลิงก์ = ยอดค้าง − WHT · บุคคลธรรมดา ⇒ ยอดเต็ม | `Helpers/DocumentPayLinkPolicy.cs:183-193` · handler บันทึก WHT ปิดหนี้ `DocumentPaymentHandler.cs:84-99,111` |
| 4.2 | ตัวเลือก "ไม่หัก" | ✅ ปิดไว้ตามคำตัดสิน 253-WP10-8 (ปฏิเสธก่อนเก็บเงิน) | `Helpers/NoWhtChoicePolicy.cs:45-53` |
| 4.3 | ติดตาม 50 ทวิ ฝั่งเรา | ✅ แถวรอ 50 ทวิ ในทะเบียนเครดิตภาษี (wht-credit.html) | `DocumentService.cs:145` (`SyncWhtCreditReceivedAsync`) |
| 4.4 | ลิงก์อัปโหลด 50 ทวิ ในอีเมลใบเสร็จ · ทวงอัตโนมัติ | ⚙️ ลิงก์มากับอีเมลใบเสร็จ (ต้องเปิด 3.4) · ทวง 7/21/45 วันทางอีเมล: ติ๊ก `asWhtReminder` (`WhtCertReminderEnabled=false` `CompanySettings.cs:346`) · **ทวงทาง LINE ❌** | `Program.cs:354-356,852` · DOCUMENT_FLOW "ยังไม่ทำ: ส่งทวงทาง LINE" |
| 5.1 | ลูกค้าเป็นบริษัทในระบบ: ใบแจ้งหนี้ของเรา → กล่องเข้าของเขา | ⚙️ ทั้งสองฝ่ายรับเป็นคู่ค้า (partners.html · `TradingPartnership Accepted`) + เลขภาษี/สาขาของผู้ติดต่อตรงบริษัทเขา | เรียกตอนอนุมัติ `DocumentService.cs:7829` → `Services/Implementations/CrossTenantWorkflowService.cs:244-322` · ล้ม ⇒ ป้ายบนใบ + ปุ่มส่งใหม่ `:334-356` |
| 5.2 | ฝั่งเขาได้ใบซื้อร่างอัตโนมัติ | ⚙️ฝั่งเขา ติ๊ก "สร้างใบซื้อร่าง…" (`ptDrafts` · `CreatePurchaseDraftsFromPartners=false` `Models/Entities/CrossTenantWorkflow.cs:139`) · 👤 เขาต้องตรวจ+อนุมัติ (ตั้งใจ — ไม่อนุมัติเอง) | `CrossTenantWorkflowService.Partners.cs:170-200,330-350` · คัดลอกยอด/WHT/ประเภทเงินได้รายบรรทัด `CopyPartnerMoney` (`Partners.cs` ~ :450-480) |
| 5.3 | เขาจ่ายผ่าน gateway ของเรา ⇒ ฝั่งเขาบันทึกจ่ายเอง | ❌ ไม่มีกลไก — handler ของเราไม่แตะสมุดของเขา (ไม่มีการอ้าง `CrossTenantDocumentLinks` ใน `Services/Payments/**` และ `PostPaymentAutomationService.cs`) | ⇒ 👤 เขาต้องบันทึกจ่ายชำระใบซื้อเอง |
| 5.4 | ฝั่งเขาออก 50 ทวิ อัตโนมัติ | ✅ เมื่อเขาบันทึกจ่าย (ติ๊ก `WhtCertAutoIssue` ค่าตั้งต้น **เปิด** `Models/Entities/Document.cs:678`) | `DocumentService.cs:~15650` → `WithholdingTaxCertService.cs:851` (ล้ม = ป้ายบนใบ + ลิสต์รอออกใบ) |
| 5.5 | 50 ทวิ ส่งกลับเข้ากล่องรับของเรา | ⚙️ฝั่งเขา ติ๊ก "ส่งหนังสือรับรอง 50 ทวิ … เข้าระบบของคู่ค้า" (`ptWht` · `SendWhtCertsToPartners=false` `CrossTenantWorkflow.cs:141`) · เฉพาะ ภ.ง.ด.3/53 · เฉพาะใบที่ออกหลังวันเปิด | `Services/Implementations/PartnerWhtCertExchangeService.cs:40-165` · รันใน `Services/Background/AutopilotJob.cs:38` (JobLock) · "ส่งแล้ว" ประทับเมื่อกล่องรับตอบจริง `:139-154` |
| 5.6 | ฝั่งเรายืนยันเครดิตภาษีอัตโนมัติ | ✅ ตรงชัดเจน ⇒ ยืนยันเอง · กำกวม/ปีที่ยื่นแล้ว ⇒ รอคนใน wht-credit.html | `Services/Implementations/WhtCertIntakeService.cs:162-276` |
| 5.7 | ใบกำกับภาษีที่ออกตอนรับเงิน → ฝั่งเขา (หลักฐานภาษีซื้อ) | ❌ ใบเสร็จ settlement ออกเป็น `Paid` ตรงไม่ผ่าน `ApproveDocumentAsync` ⇒ ไม่ถูกส่งเข้าระบบคู่ค้า | `DocumentService.cs:14927-14990` (`Status = issueApproved ? Paid : Draft`) · ผู้เรียก `RouteSentDocumentAsync` มีที่เดียว `:7829` · เส้นรับ ReceiptFlow ฝั่งผู้รับมีแล้วแต่ไม่มีใครป้อน `CrossTenantWorkflowService.Partners.cs:172-195` |
| 6 | LINE แจ้งเตือนแต่ละเหตุการณ์ | ดูตาราง §3 | |
| 7 | เห็นความล้มเหลว | ดูตาราง §4 | |

## 2. Checklist ตั้งค่าให้เดินสุดทางวันนี้ (ตามลำดับ)

**ฝั่งบริษัทเรา (ผู้ขาย)**
1. **รับชำระออนไลน์** — ตั้งค่า › รับชำระเงิน (payment-settings.html): ใส่คีย์ Omise test → ใส่ Webhook URL ที่หน้าแสดงลงใน Omise Dashboard → กด "ทดสอบ" ให้ผ่านทั้งคีย์+webhook → ใส่คีย์ live → กด "เปิดรับเงินจริง" · เลือกช่องทาง (PromptPay/บัตร ฯลฯ) · ตั้งบัญชีพักเงิน gateway/บัญชีค่าธรรมเนียม
2. **ค่าตั้งต้นส่งเอกสาร** — ตั้งค่า › การตั้งค่าเอกสาร › กล่อง "ส่งเอกสารอัตโนมัติ": ✔ แนบลิงก์ชำระเงิน · ✔ หลังลูกค้าชำระ: ออกใบเสร็จ…แล้วส่ง · (เลือก) ส่งทุกงวด · ✔ ทวง 50 ทวิ อัตโนมัติ
3. **e-Tax** (ถ้าใช้) — เปิด e-Tax ที่ตั้งค่า (ใบเสร็จ-ใบกำกับที่ออกตอนรับเงินจะออก e-Tax เอง)
4. **LINE OA ของบริษัท** — ตั้งค่า › แจ้งเตือนทาง LINE: เปิดใช้ + Channel Access Token + Channel Secret + Basic ID · วาง Webhook URL ใน LINE Developers และเปิด Use webhook (ระบบยังไม่ตรวจว่า webhook ถึง — M5 ทีม A)
5. **ผู้ติดต่อ** — ใส่อีเมล (จำเป็น — ใบเสร็จไปทางอีเมลเท่านั้น) · แท็บ LINE: สร้างลิงก์เชิญให้ลูกค้าผูก LINE หรือผูกกลุ่มด้วยรหัส GB · ตรวจเลขภาษี+สาขา (ใช้ทั้งการหัก WHT และจับคู่บริษัทคู่ค้า)
6. **กฎ LINE** — หน้า ตารางส่ง (email-schedule.html): สร้างกฎ "เมื่อรายการประจำสร้างเอกสาร" (หรือ "เมื่ออนุมัติเอกสาร") ติ๊ก อีเมล + LINE ผู้ใช้/กลุ่ม (ธงบนรายการประจำอย่างเดียวส่งแค่อีเมล)
7. **รายการประจำ** (recurring.html): ความถี่รายเดือน + วันที่ · ✔ อนุมัติอัตโนมัติ · ✔ ส่งอีเมลเอกสาร · ช่องทางรับเงินของรายการนี้ · (ถ้าต้องการ) นโยบายอนุมัติข้ามคำเตือน
8. **แจ้งทีม** — notifications.html: เปิดกระดิ่ง/อีเมล/กลุ่ม LINE ของบริษัทสำหรับ "รับชำระ" "รายการประจำล้ม" "งานหลังรับชำระหยุด" "รายการชำระค้าง" · **อย่ากดบันทึกที่ ตั้งค่า › แท็บการแจ้งเตือน** (ล้างติ๊กกลุ่ม LINE — H3 ทีม A)
9. **คู่ค้า** — partners.html: เชิญลูกค้าด้วยเลขภาษี → รอเขารับ

**ฝั่งลูกค้าที่ใช้ระบบเรา (ผู้ซื้อ)** — ต้องให้เขาทำเอง
10. partners.html: รับคำเชิญ · ✔ สร้างใบซื้อร่างจากใบแจ้งหนี้ที่คู่ค้าส่งมา · ✔ ส่ง 50 ทวิ ที่เราออกให้คู่ค้าเข้าระบบของเขา
11. ทุกเดือน: เปิดใบซื้อร่าง → ตรวจผังบัญชี → อนุมัติ → **บันทึกจ่ายชำระ** (หลังจ่ายผ่านลิงก์ของเรา) ⇒ 50 ทวิ ออกเอง ⇒ ส่งเข้ากล่องรับของเราในรอบถัดไปของ Autopilot

## 3. LINE ต่อเหตุการณ์

| เหตุการณ์ | ถึงลูกค้า | ถึงทีมเรา |
| --- | --- | --- |
| ออกใบแจ้งหนี้ | ⚙️ การ์ด LINE + ปุ่มชำระ (กฎตารางส่ง · OA บริษัท · ผู้ติดต่อผูก LINE) · ไม่มี PDF/ลิงก์ดูเอกสาร | ⚙️ `DocumentApproved` (notifications.html · กลุ่ม LINE ของบริษัท) |
| รับชำระแล้ว | ❌ ไม่มีข้อความยืนยันรับเงิน | ✅ ค่าตั้งต้น กระดิ่งเจ้าของ + กลุ่ม LINE ของบริษัทถ้าผูก (`DocumentService.cs:~15681-15691` · `PaymentNotificationText`) |
| ส่งใบเสร็จ | ❌ อีเมลเท่านั้น (M4) | — |
| ทวง 50 ทวิ | ❌ อีเมลเท่านั้น | — |
| 50 ทวิ เข้ากล่องรับ (จากคู่ค้า) | — | ❌ ไม่มีแจ้งเมื่อใบมาถึง/รอคนยืนยัน (`WhtCertIntakeService.cs` ไม่มีผู้แจ้ง) · มีแค่ตอนคู่ค้ายกเลิกใบ `PartnerWhtCertExchangeService.cs:186-210` |
| ล้ม | — | ดู §4 |

## 4. ความล้มเหลวเห็นที่ไหน

| ขั้นที่ล้ม | ที่เห็น | ช่องโหว่ |
| --- | --- | --- |
| รายการประจำสร้าง/อนุมัติไม่ได้ | Autopilot (`RecurringFailed`/`RecurringDraft` `Services/Implementations/AutopilotService.cs`) + แจ้ง `RecurringRunFailed` (`RecurringTransactionService.cs:686,725`) | — |
| อีเมลเอกสารส่งไม่ออก | Autopilot `EmailFailed` (`AutopilotService.cs:163-175`) | — |
| LINE ส่งไม่ออก | คิวในหน้าตารางส่งเท่านั้น · กระดิ่งเฉพาะโควตาเดือนหมด (`ContactLineQueueService.cs:48-72`) | ❌ ไม่อยู่ใน Autopilot · ไม่มีกระดิ่งเมื่อล้มซ้ำ |
| เงินเข้าแต่ลงบัญชีไม่ได้ | แจ้ง `GatewayPaymentUnfulfilled` (`Services/Payments/PaymentIntentService.cs:662`) · ค้าง ⇒ `GatewayPaymentStuck` (`Services/Background/PaymentIntentReconcileJob.cs:180`) · หน้า payment-intents.html | ❌ ไม่อยู่ใน Autopilot issues (มีแค่ 5 ชนิด) |
| ออกใบเสร็จตอนรับเงินล้ม | `LogWarning` เท่านั้น ถ้าปิดงานหลังรับชำระ | ❌ ล้มเงียบ (F2 ข้อ 7) |
| งานหลังรับชำระหยุด (ไม่มีอีเมล/ข้อมูลผู้ซื้อไม่ครบ/ใบร่าง) | Autopilot `PostPayment` + แจ้ง `PostPaymentAutomationBlocked` | — |
| ส่งเข้าระบบคู่ค้าล้ม | ป้ายบนเอกสาร + ปุ่มส่งใหม่ (`CrossTenantWorkflowService.cs:334-356`) | ⚠️ ไม่มีกระดิ่ง/ไม่อยู่ใน Autopilot |
| 50 ทวิ ส่งถึงคู่ค้าไม่ได้ (ฝั่งเขา) | เหตุ + จำนวนครั้งบนใบ 50 ทวิ ของเขา (`PartnerWhtCertExchangeService.cs:168-180`) | ⚠️ ฝั่งเราไม่รู้ว่ามีใบที่ส่งไม่ถึง |

## 5. ช่องโหว่เรียงลำดับ

### HIGH
**F-H1 — ใบเสร็จ/ใบกำกับหลังรับเงินไม่ไปทาง LINE (M4 ทีม A ยังเปิดที่ 74e1bb37)** · ลูกค้าที่มีแต่ LINE ได้ใบแจ้งหนี้ทาง LINE → จ่าย → ไม่ได้ใบเสร็จทางไหนเลย ขณะที่ปลายทาง LINE ติ๊ก "ใบเสร็จ" ไว้ตั้งแต่ต้น (`NotifyEndpointKind.cs:131`) · ใบกำกับ §86/4 ที่ออก ณ วันรับเงินต้องส่งมอบผู้ซื้อ
- แก้ขั้นต่ำ: `PostPaymentAutomationPolicy` เพิ่มขั้น `LineReceipt` (การ์ดเอกสารใบเสร็จผ่าน `ContactLineQueueService` เดิม · trigger ใหม่ `ReceiptIssued` ใน `ContactLineChannel.LineTriggers`) — ระหว่างยังไม่ทำ: `NotifyTopics.LineSendable` ตัวเดียว ⇒ ถอด "ใบเสร็จ" จากค่าตั้งต้น LINE + ล็อกช่องบน contacts.html พร้อมเหตุ
- ไฟล์: `Helpers/PostPaymentAutomationPolicy.cs` · `Services/Implementations/PostPaymentAutomationService.cs` · `Helpers/ContactLineChannel.cs` · `Services/Implementations/EmailScheduleService.cs` (EnqueueLineAsync) · `Helpers/NotifyEndpointKind.cs` · `wwwroot/pages/contacts.html` · เทสต์ policy + ทิศตรงข้าม (ลูกค้ามีอีเมล + LINE ไม่ได้สองฉบับเกินที่ตั้ง)

**F-H2 — ใบกำกับภาษีที่ออกตอนรับเงิน (§78/1) ไม่ถึงระบบของลูกค้าคู่ค้า** · ใบแจ้งหนี้ของเราเข้าเป็นใบซื้อร่างแบบ `PaperRequired` (ไม่ใช่ใบกำกับ) · ใบกำกับจริงเกิดตอนรับเงินผ่าน `CreateSettlementReceiptAsync` (`DocumentService.cs:14927` → `Paid` ตรง) ซึ่งไม่เรียก `RouteSentDocumentAsync` (ผู้เรียกเดียว `:7829`) ⇒ เส้น ReceiptFlow ที่ทำไว้แล้วฝั่งผู้รับ (`Partners.cs:172-195`) ไม่มีใครป้อน ⇒ ภาษีซื้อของลูกค้าค้าง 11640 ตลอด จนเขากด "ได้รับต้นฉบับแล้ว" เอง
- แก้ขั้นต่ำ: หลังออกใบเสร็จ settlement ที่อนุมัติแล้ว (ทุกทางเข้า — มือ/gateway/งานหลังรับชำระ/ออกย้อนหลัง `:~15190-15220`) เรียก `_crossTenantWorkflow.RouteSentDocumentAsync(receipt)` ต่อจาก `_issuedHooks.RunAsync` · ล็อกจุดเรียกด้วย `tools/required_call_site_check.py`
- ไฟล์: `Services/Implementations/DocumentService.cs` · `tools/required_call_site_check.py` · DOCUMENT_FLOW §คู่ค้า · Db test: จ่ายผ่าน gateway ⇒ ใบซื้อร่างของผู้รับได้หลักฐานใบกำกับ · ทิศตรงข้าม: ใบเสร็จเปล่า (ไม่มี VAT) ไม่สร้างใบซื้อใหม่

**F-H3 — จ่ายผ่าน gateway ของเราแล้ว ฝั่งลูกค้าคู่ค้าไม่รู้ ⇒ 50 ทวิ ไม่ออกถ้าเขาไม่บันทึกจ่ายเอง** · ไม่มีกลไกใดใน `Services/Payments/**` แตะ `CrossTenantDocumentLinks` · โซ่ "50 ทวิ กลับอัตโนมัติ" จึงขาดตรงกลางเสมอ (เจ้าของถามว่า "ไปถึงสุดทาง")
- แก้ขั้นต่ำ (ไม่บันทึกบัญชีแทนเขา — F2 ข้อ 3): เมื่อ `DocumentPaymentHandler` ลงรับชำระของใบที่มี link InvoiceFlow ⇒ แจ้งฝั่งผู้รับ (`AutopilotPartnerExchange`: "จ่าย {เลขที่} ผ่านลิงก์แล้ว ยอด … WHT … — บันทึกจ่ายชำระใบซื้อ {PI}") + ป้าย "ผู้ขายรับเงินแล้ว" บนใบซื้อร่าง + ปุ่มเดียว "บันทึกจ่ายตามที่ผู้ขายรับ" (ยอด/วัน/WHT จาก intent · ผู้รับกดยืนยัน) · เฟสถัดไป: ค่าตั้งฝั่งผู้รับ "บันทึกจ่ายอัตโนมัติเมื่อจ่ายผ่านลิงก์ของคู่ค้า" ใช้ค่าตั้งที่ประกาศไว้แล้วแต่ไม่มีผู้อ่าน
- ไฟล์: `Services/Payments/Handlers/DocumentPaymentHandler.cs` · `Services/Implementations/CrossTenantWorkflowService.Partners.cs` · `Controllers/CrossTenantWorkflowController.cs` · `wwwroot/pages/partners.html` (หรือหน้าใบซื้อร่าง)

### MED
**F-M1 — ค่าตั้งต้นปิดเกือบทุกขั้น และไม่มีหน้าเดียวที่บอกว่าโซ่ขาดตรงไหน** · ต้องเปิด 6–8 ที่ใน 5 หน้า (§2) · recurring.html ไม่พูดถึง LINE · ธง "ส่งอีเมล" ไม่ส่ง LINE · แก้: การ์ด "ความพร้อมทั้งเส้น" ต่อรายการประจำ (เซิร์ฟเวอร์คำนวณ: อนุมัติ ✓ · อีเมล ✓/เหตุ · LINE ✓/เหตุ (`ContactLineChannel.Plan`) · ลิงก์ชำระ ✓/เหตุ · gateway live ✓ · ใบเสร็จหลังรับเงิน ✓ · คู่ค้า ✓) — รวม A-M1(ค) · ไฟล์ `Services/Implementations/RecurringAutopilotService.cs` · `wwwroot/pages/recurring.html`

**F-M2 — ออกใบเสร็จตอนรับเงินล้ม = LogWarning** (`DocumentService.cs:~15622`) · ปิดงานหลังรับชำระ ⇒ ใบกำกับ §78/1 ไม่ถูกออกและไม่มีใครเห็น · แก้: ป้ายบนเอกสาร + แถว Autopilot "ออกใบเสร็จไม่สำเร็จ" + แจ้ง `PostPaymentAutomationBlocked` (ตัวเดิม) · ไฟล์ `DocumentService.cs` · `AutopilotService.cs`

**F-M3 — Autopilot ไม่เห็น LINE ล้ม · gateway ค้าง · ส่งคู่ค้าล้ม** · มีแค่ 5 ชนิด issue · แก้: เพิ่ม `LineFailed` (LineQueues Failed) · `GatewayUnfulfilled` (PaymentIntents ที่ handler ล้ม) · `PartnerRouteFailed` (ป้าย `PartnerRoutingFailedNote`) · ไฟล์ `Services/Implementations/AutopilotService.cs` · `Helpers/AutopilotIssue*`

**F-M4 — 50 ทวิ มาถึงกล่องรับแต่ไม่มีใครถูกแจ้ง** (`WhtCertIntakeService.cs` ไม่มีผู้แจ้ง) · ใบที่ต้องคนยืนยันค้างเงียบ · แก้: แจ้ง `AutopilotPartnerExchange` (หรือคีย์ใหม่ `wht.cert_received`) เมื่อผล AwaitUser/Proposed · ไฟล์ `WhtCertIntakeService.cs` · `Models/Constants/NotificationEvents.cs`

### LOW
- F-L1 `WorkflowAutomationConfig.AutoCreateReceiptFromIncomingPayment` (`Models/Entities/CrossTenantWorkflow.cs:124`) — เก็บ/echo ผ่าน API (`Controllers/CrossTenantWorkflowController.cs:177,223`) แต่ไม่มีผู้อ่านเชิงธุรกิจและไม่มีบนหน้า (F2 ข้อ 2) ⇒ ต่อสาย (F-H3 เฟสถัดไป) หรือลบ
- F-L2 ลูกค้าไม่ได้ข้อความ "ได้รับเงินแล้ว" ทาง LINE (รวมใน F-H1)
- F-L3 ทวง 50 ทวิ ทาง LINE ยังไม่มี (DOCUMENT_FLOW จดไว้แล้ว) · การ์ด LINE ไม่มีลิงก์ดูเอกสาร/PDF (A-L8)
- F-L4 50 ทวิ ผ่านคู่ค้าเฉพาะ ภ.ง.ด.3/53 (`PartnerWhtCertExchangeService.cs:91-101`) — ตั้งใจ แต่หน้า partners.html ควรบอก

## 6. แพ็กงาน

| แพ็ก | ข้อ | ไฟล์หลัก |
| --- | --- | --- |
| P1 ใบเสร็จทาง LINE | F-H1 · F-L2 · (A-M4) | PostPaymentAutomationPolicy/Service · ContactLineChannel · EmailScheduleService.EnqueueLineAsync · NotifyEndpointKind · contacts.html · เทสต์ policy |
| P2 คู่ค้า: ใบกำกับตอนรับเงิน + แจ้งจ่ายแล้ว | F-H2 · F-H3 · F-L1 | DocumentService (CreateSettlementReceipt ผู้เรียก) · DocumentPaymentHandler · CrossTenantWorkflowService(.Partners) · CrossTenantWorkflowController · partners.html · required_call_site_check · Db test |
| P3 มองเห็นทั้งเส้น | F-M1 · F-M2 · F-M3 · F-M4 | RecurringAutopilotService · recurring.html · AutopilotService · WhtCertIntakeService · NotificationEvents |

ทุกแพ็กแตะเงิน/ภาษี ⇒ ส่ง diff ให้ฝ่ายค้าน 1 รอบ (F3 ข้อ 11) · ขยับ DOCUMENT_FLOW §2.12g/§คู่ค้า + TEST_PLAN ในคอมมิตเดียวกัน
