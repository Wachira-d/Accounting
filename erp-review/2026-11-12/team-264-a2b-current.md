# รอบ 264 — "บริษัท A ขาย → บริษัท B (ใช้ NextAcc ทั้งคู่) จ่าย → ระบบทำต่อจนจบ": ของที่มีวันนี้ (อ่านอย่างเดียว)

> 2026-11-12 · HEAD `8bc7f199` (claude/booking-service-features-sdemob · หลังรอบ 263) · path นับจาก `Accounting/` · ทุกข้ออ่านจากไฟล์ตรงจุด ·
> **ยังไม่ได้คอมไพล์/รันในเครื่องนี้** (ไม่มี .NET SDK) — ผลข้างล่างมาจากการอ่านโค้ด ไม่ได้ทดสอบกับบริษัทจริง ·
> ต่อจาก `erp-review/2026-11-11/team-F-billing-chain.md` (รอบ 258) · `review-258-billing-chain.md` · `team-D-lump-payment.md` · `erp-review/2026-11-12/team-260-2-slip-bank-account.md`

สัญลักษณ์: ✅ อัตโนมัติและใช้ได้ · ⚙️ ใช้ได้แต่ต้องเปิดค่าตั้ง (ค่าตั้งต้นปิด) · 👤 ต้องมีคนกด · ❌ ไม่มี

## 0. คำตอบสั้น

ฝั่ง **A (ผู้ขาย)** เดินได้เกือบจบแล้วเมื่อจ่ายผ่าน gateway (ใบเสร็จ/ใบกำกับ §78/1 ออกเอง → ส่งอีเมล/LINE → ส่งเข้าระบบ B) ·
ถ้าจ่ายด้วย**สลิป** A ยังต้องมีคนกด "ยืนยันรับเงิน" (หรือเปิดปิดหนี้อัตโนมัติจากรายการเดินบัญชีที่ค่าตั้งต้นปิด) ·
ฝั่ง **B (ผู้ซื้อ)** ยัง**ไม่อัตโนมัติ**โดยตั้งใจ (คำตัดสิน 258-3-7 "ไม่บันทึกบัญชีแทนผู้ซื้อ"): ได้แค่ "การแจ้ง" + ปุ่มเดียว · ต้องอนุมัติใบซื้อร่างเองก่อน ·
**แหล่งเงินออกของ B ไม่มีหลักฐานใดเลย** — กล่องยืนยันตั้งต้นที่ "บัญชีของใบซื้อ หรือบัญชีแรกที่ใช้งาน" (ค่าที่แต่งขึ้น) · 50 ทวิ ของ B ออกเองเมื่อบันทึกจ่าย
แล้วส่งกลับเข้ากล่องรับของ A ได้ (⚙️ ทั้งสองฝั่ง) แต่**ยืนยันเครดิตภาษีฝั่ง A ต้องผ่านคำตอบ "ของจริง" ของนักเรียน/AI** ⇒ ปิด AI ทั้งระบบแล้วใบที่มาจากคู่ค้าค้างรอคน ·
**ไม่มีหน้าความยินยอม** — มีแค่ติ๊ก 2 ช่องระดับบริษัทที่ไม่มี audit ไม่มีขอบเขตต่อคู่ค้า · **สลิปอ่านได้แค่ ยอด/วันที่/เลขอ้างอิง** ด้วย Tesseract ในเครื่อง ไม่ถอด QR ไม่อ่านบัญชีผู้รับ/ผู้โอน

## 1. โซ่ทั้งเส้น (A ขาย → B จ่าย → ทั้งคู่ลงบัญชี)

| # | ขั้น | ผลวันนี้ | หลักฐาน (file:line) |
|---|---|---|---|
| 1 | A อนุมัติใบแจ้งหนี้/ใบกำกับ ⇒ ส่งเข้าระบบ B | ⚙️ ทั้งสองฝ่ายเป็นคู่ค้า Accepted + เลขภาษี/สาขาของผู้ติดต่อตรงบริษัท B · **ไม่ขึ้นกับช่องทางส่ง** (LINE/อีเมล/ลิงก์ เป็นแค่ช่องแจ้งลูกค้า — การส่งข้ามบริษัทเป็นงานฝั่งเซิร์ฟเวอร์ตอนอนุมัติ) | `Services/Implementations/DocumentService.cs:7839` → `CrossTenantWorkflowService.RouteSentDocumentAsync` `CrossTenantWorkflowService.cs:334-356` → `OnDocumentSentAsync` `:244-322` (จับคู่ `PartnerCompanyMatch.Pick` · กำกวม/สาขาไม่ตรง = ล้มดัง + ป้าย `[PARTNER-ROUTE-FAILED]`) |
| 2 | B ได้ใบซื้อร่าง | ⚙️ฝั่ง B `CreatePurchaseDraftsFromPartners` (ค่าตั้งต้นปิด) · คัดลอกยอด/บรรทัด/VAT/WHT/ประเภทเงินได้ — **ไม่คัดลอกผังบัญชี** · สกุลต่างประเทศ/ยอด 0/เกินเพดานคู่ = ไม่สร้าง (เหตุบนแถวกล่องเข้า) · หลักฐานภาษีซื้อ = `PaperRequired`/`AwaitingTimestamp`/`AwaitingTaxInvoice` — **ระบบไม่เคยตั้งค่าที่ปลดภาษีซื้อเอง** | `CrossTenantWorkflowService.Partners.cs:170-402` · `CopyPartnerMoney` `:450-482` · `Helpers/PartnerExchangeRules.cs:62-77` (DraftProblem) · `PartnerInputVatEvidence.Of` `:323-324` · `BlocksClaim` `:378-382` |
| 3 | B อนุมัติใบซื้อร่าง | 👤 ตั้งใจ (255 N3 — ใบซื้อร่างจากคู่ค้าไม่อนุมัติเอง) · ต้องเลือกผังบัญชีเอง | `PartnerPaymentNoticeRule.For` ปฏิเสธใบร่าง `Helpers/PartnerPaymentNoticeRule.cs:78-79` |
| 4a | B จ่ายผ่านลิงก์ชำระของ A (gateway) | ✅ หน้าชำระสาธารณะ (ไม่ล็อกอิน) · ผู้ซื้อนิติบุคคล ⇒ ยอดในลิงก์ = ยอดค้าง − WHT | `Controllers/PublicPaymentController.cs:39-40,191` · `Helpers/DocumentPayLinkPolicy.cs:186,199,228` |
| 4b | B โอนเอง + ส่งสลิป (พอร์ทัล/หน้าชำระ/แชต LINE OA ของ A) | ✅ เป็น "แจ้งโอน" = `PaymentIntent(ManualSlip)` รอยืนยัน · **อีเมลขาเข้าไม่มี** (ไม่มีตัวรับเมลในระบบ — ไม่มี IMAP/inbound webhook) | `Services/Implementations/SlipEvidenceService.cs:49` (ช่อง `:40-45`) · `LineChatSlipService.cs:109-228,326` |
| 5a | A: gateway สำเร็จ ⇒ รับชำระ + ตัดหนี้ | ✅ กันซ้ำด้วย `PAY-INTENT-{id}` · เงินลงบัญชีพัก 11340 ของผู้ให้บริการ (ไม่ใช่ธนาคาร) | `Services/Payments/Handlers/DocumentPaymentHandler.cs:46-126` · `Services/Payments/GatewayAccountResolver.cs:54,79` |
| 5b | A: สลิป ⇒ รับชำระ | 👤 พนักงานกด "ยืนยันรับเงิน" เลือกบัญชีเงินเข้าได้ (รอบ 260-2) · หรือ ⚙️ ปิดหนี้อัตโนมัติจากรายการเดินบัญชีที่เลขอ้างอิงตรงสลิป (ค่าตั้งต้นปิด) | `Controllers/PaymentGatewayController.cs:383-396,546` · `Helpers/SlipReceivingAccount.cs:28-` · `BankReceiptSettleService.cs:143-179` (`SlipRefMatched`) |
| 6 | A: ออกใบเสร็จ/ใบกำกับ ณ วันรับเงิน | ✅ (`ReceiptIssuePolicy` · ล้ม = ป้าย `[RECEIPT-ISSUE-FAILED]` + แจ้ง) | `DocumentService.cs:14957` (`CreateSettlementReceiptAsync`) · `DocumentService.SettlementFollowUp.cs:22-54` |
| 7 | A: ส่งใบเสร็จถึง B (อีเมล/LINE/e-Tax by Email) | ⚙️ `PostPayEmailReceipt` (ค่าตั้งต้นปิด) · e-Tax by Email ต้องมีอีเมล (LINE อย่างเดียว ⇒ หยุด) | DOCUMENT_FLOW §2.12g-258c · `Helpers/PostPaymentAutomationPolicy.cs` |
| 8 | A: ใบเสร็จที่เป็นใบกำกับ ⇒ ระบบ B (หลักฐานภาษีซื้อ) | ✅ เมื่อคู่ค้า + ฝั่ง B เปิดใบซื้อร่าง · แนบเข้าใบซื้อเดิม (ไม่สร้างใบที่สอง) · อย่างย่อ ⇒ ไม่นับ · VAT ไม่ตรง ⇒ `AwaitingTaxInvoice` · **ยังต้องให้ B กด "ได้รับต้นฉบับแล้ว"** (ไม่มีการตรวจ ETDA) | `DocumentService.SettlementFollowUp.cs:22-28` → `Partners.cs:172-306` · `PartnerExchangeRules.IsTaxInvoiceCarrier` `:169-175` |
| 9 | A ⇒ B: "ผู้ขายรับเงินแล้ว" | ✅ เฉพาะเงินที่ผ่าน `DocumentPaymentHandler` (gateway + สลิปที่ยืนยันที่คิว) · **ไม่เกิด**เมื่อ A บันทึกรับชำระที่หน้าเอกสาร / ปิดหนี้จากรายการเดินบัญชี / นำเข้าไฟล์ / ชำระหลายใบ | ผู้เรียกเดียว `DocumentPaymentHandler.cs:128-130` → `PartnerPaymentNoticeService.cs:43-117` (`python3 tools/callers.py NotifySellerReceivedAsync` = 1 ผู้เรียกจริง) |
| 10 | B: บันทึกจ่าย (ใบสำคัญจ่าย/การจ่ายของใบซื้อ) | 👤 ปุ่ม "บันทึกจ่ายตามที่ผู้ขายรับแล้ว" · ยอด/WHT จาก `PartnerPaymentNoticeRule.For` (ครบพร้อมหัก · ครบไม่หักพร้อมคำเตือน ม.50/54 · อื่น ๆ ไม่เดา) · **จ่ายบางส่วน = ไม่รับ** · ผู้บันทึก = ผู้กด · `PaymentEntrySource.Manual` | `Helpers/PartnerPaymentNoticeRule.cs:76-97` · `PartnerPaymentNoticeService.cs:206-274` · `Controllers/PartnerPaymentNoticeController.cs:62-80` · `wwwroot/pages/partners.html:211` |
| 11 | B: แหล่งเงินออก | ❌ ไม่มีหลักฐาน — ดู §3.2 | `PartnerPaymentNoticeService.cs:159-161,181-191` |
| 12 | B: ออก 50 ทวิ | ✅ ตอนบันทึกจ่าย (`WhtCertAutoIssue` ค่าตั้งต้นเปิด) | `Models/Entities/Document.cs:681` · `WithholdingTaxCertService.cs:851-1003` |
| 13 | B ⇒ A: ส่ง 50 ทวิ เข้ากล่องรับของ A | ⚙️ฝั่ง B `SendWhtCertsToPartners` (ปิด) · เฉพาะ ภ.ง.ด.3/53 · เฉพาะใบที่ออกหลังวันเปิด · รันใน AutopilotJob · "ส่งแล้ว" ประทับเมื่อกล่องรับตอบจริง · **ผู้ติดต่อของ A ในสมุด B ต้องมี `ExternalSystem="NextAccTenant"`** — ผู้ติดต่อที่ผูกระบบอื่นไว้แล้ว ⇒ ข้ามเงียบ (ไม่ประทับเหตุ) | `PartnerWhtCertExchangeService.cs:48-165` (ตัวกรอง `:76,90` · แบบ `:97-101`) · `CrossTenantWorkflowService.cs:725-782` (ผูกกุญแจเฉพาะเมื่อ ExternalSystem ว่าง `:774`) |
| 14 | A: ยืนยันเครดิตภาษีหัก ณ ที่จ่าย | ⚠️ ด่านเชิงกำหนดผ่าน ⇒ **ยังเรียก `IAiOrchestrator` ตรวจ "ของจริง"** แม้ใบมาจากระบบเราเอง (ช่อง `NextAccLink`) · ไม่มีคำตอบ/ไม่ถึงเกณฑ์ ⇒ AwaitUser | `WhtCertIntakeService.cs:149-166` · `Helpers/WhtCertIntakeVerdict.cs:81-89` |
| 15 | ยกเลิก/คืนเงินหลังทำอัตโนมัติ | ⚠️ A ยกเลิก**เอกสาร** ⇒ ใบซื้อ B ติด `SourceVoided` · A ยกเลิก**การรับชำระ** ⇒ **การแจ้งของ B ไม่ถูกแตะ** (Open ยังกดบันทึกได้ · Recorded ไม่มีใครรู้) · คืนเงิน gateway ไม่มี handler | `DocumentService.cs:10723` → `Partners.cs:581` · `VoidPaymentAsync` `DocumentService.cs:11753` (ไม่มี hook คู่ค้า) · `Services/Payments/IPaymentCompletionHandler.cs:19-27` (มีแต่ Succeeded) |

## 2. สลิปถูกอ่านอย่างไรวันนี้ (ตรงตัว)

1. **ตัวอ่าน**: `ISlipImageReader` → `TesseractSlipImageReader` (`Services/Implementations/TesseractSlipImageReader.cs:7-44`) = Tesseract ในเครื่อง (`EmbeddedTesseractOcrService`) ·
   ไม่มี tessdata ⇒ `Reading.Empty` · ล้ม ⇒ LogError + `Empty` (ไม่โยน) · **ไม่เรียก AI** (คำตัดสิน 260-1-2) ⇒ kill-switch ไม่มีผล
2. **ตัวแยกข้อความ**: `Helpers/OcrSlipText.Parse` (`Helpers/OcrSlipText.cs:53-60`) คืน `Reading(Amounts, Amount, Date, Ref, LooksLikeSlip)` เท่านั้น
   - ยอด: regex ทศนิยม 2 ตำแหน่ง (`:29`) · มั่นใจเมื่อบรรทัดมีคำว่า จำนวน/ยอดเงิน/Amount ชี้ยอดเดียว หรือทั้งภาพมียอดเดียว (`:75-90`)
   - เลขอ้างอิง: ชุด A-Z0-9 ยาว 10–25 ที่มีตัวเลข — เลือก "ยาวสุด" (`:93-99`) — **ไม่ใช่ค่าจาก QR**
   - วันที่: dd/mm/yy(yy) หรือ "12 พ.ย. 69" (`:102-122`)
   - **ไม่มี**: ชื่อ/เลขบัญชี (ที่ถูกปิดบางหลัก) ของผู้โอนและผู้รับ · ชื่อธนาคาร · เวลา · ค่าธรรมเนียม · พร้อมเพย์ปลายทาง
3. **QR ท้ายสลิป**: ไม่ถอด — ไม่มีตัวถอด QR จากรูปในระบบ (`QRCoder 1.6.0` เป็นตัว**สร้าง** QR เท่านั้น · `Accounting.csproj:42`) ·
   เหตุผลที่จดไว้: QR บนสลิปไทยเป็นรหัสตรวจสอบรายการ (มีรหัสธนาคารผู้โอน + เลขอ้างอิงรายการ) ไม่มียอด/เลขบัญชี — ต้องเรียกบริการตรวจสลิปของธนาคาร/ผู้ให้บริการภายนอก (DECISIONS 260-1-2)
   ⇒ ข้อเท็จจริงสำคัญต่อการออกแบบ: **QR ไม่เคยให้เลขบัญชีผู้รับ/ผู้โอน** — ให้ได้แค่ "ธนาคารผู้โอน" + "เลขอ้างอิงที่ธนาคารออก" (กุญแจกันซ้ำที่แข็งกว่า OCR)
   *(โครงสร้าง QR ตรวจสลิปตามมาตรฐานของธนาคารแห่งประเทศไทย — ทีมนี้อ่านจากความรู้เดิม ไม่ได้เปิดเอกสารมาตรฐานในรอบนี้ · แพ็ก P2 ต้องยืนยันกับเอกสารทางการ + ตัวอย่างสลิปจริงก่อนเขียนตัวถอด)*
4. **ใช้ผลอ่านทำอะไร**: แชต LINE → `LineChatSlipRule.Decide` (ยอดตรงใบเดียว + ดูเป็นสลิป ⇒ แจ้งโอนอัตโนมัติ · หลายใบ ⇒ ปุ่มเลือก · อื่น ๆ ⇒ คิวพนักงาน) ·
   เลขอ้างอิงจาก OCR **ไม่ถูกประทับ**ลง `PaymentIntent.SlipBankRef` (260-1-8) — ใช้แค่กันซ้ำในแถวแชต (`LineChatSlipService.cs:135-137,280-287`) ·
   สลิปพอร์ทัล/หน้าชำระ: ไม่ OCR — เก็บ `SlipFileSha256` + `SlipBankRef` ที่ลูกค้าพิมพ์ (`Models/Entities/Payments.cs:217-220`) · สลิปค่าบริการ SaaS: `SlipOcrAssistService` (badge ยอดตรง)
5. **ไฟล์สลิปไม่เดินข้ามบริษัท** — สลิปที่ B ส่งให้ A อยู่ในบริษัท A (ไฟล์แนบเอกสารของ A) · การแจ้งที่ B ได้ไม่มีรูปสลิป

## 3. บัญชีเงินถูกเลือกอย่างไรวันนี้

### 3.1 ฝั่ง A — "แหล่งเงินเข้า"
- gateway ⇒ บัญชีพัก 11340 ของผู้ให้บริการ (`GatewayAccountResolver.cs:54-81`) — เลือกธนาคารไม่ได้ (ถูก — เงินยังไม่ถึงธนาคาร) · เข้าธนาคารตอนลงบัญชีรอบโอน (§2.10)
- สลิป ⇒ ตัวตัดสินเดียว `SlipReceivingAccount` (`Helpers/SlipReceivingAccount.cs:28-309`): พนักงานเลือก > แหล่งเงินของใบที่เป็นธนาคาร > บัญชีที่มี QR ที่แสดงให้ลูกค้าโอน > บัญชีรับโอนบัญชีเดียว > ไม่แนะนำ ·
  ประทับ `PaymentIntent.ReceivingBankAccountId/ReceivingAccountOrigin` (`Payments.cs:224-225`) · handler อ่านค่าที่ประทับ (`DocumentPaymentHandler.cs:85-90`) · **ชั้น "หลักฐานจากสลิปเอง" มีช่องแต่ยังไม่มีผู้ป้อน** (`SlipReceivingAccount.cs:102-103`)
- ปิดหนี้จากรายการเดินบัญชี ⇒ บัญชีของรายการเอง (แข็งที่สุด)

### 3.2 ฝั่ง B — "แหล่งเงินออก" (ไม่มีตัวตัดสิน)
- กล่องยืนยันของการแจ้ง: บัญชีตั้งต้น = `doc.BankAccountId` ของใบซื้อ (ถ้ายังใช้งาน) **ไม่งั้น "บัญชีแรกที่ใช้งาน" ตามลำดับชื่อธนาคาร** (`PartnerPaymentNoticeService.cs:159-161`) — ไม่มีหลักฐานว่าเงินออกจากบัญชีนั้น
  (ค่าที่แต่งขึ้น · คนกดยืนยันก็จริง แต่ปุ่ม default = รับค่าที่เดาไว้) · ตัวเลือก "เงินสด / ไม่ระบุบัญชี" ⇒ `OverrideBankAccountId=null` ⇒ `DocumentMoneyChannel.ForPayment` สืบทอดแหล่งเงินของใบซื้อ ไม่มี ⇒ **เงินสด 111** (`team-260-2` §3)
- `AiSuggestionController` `payment-channel/suggest` (`Controllers/AiSuggestionController.cs:686-800` · `AiFeatureKey.PaymentChannelSuggestion`): ประวัติ 12 การจ่ายล่าสุดของผู้ขายรายนั้น → **บัญชีแรกที่ใช้งาน → เงินสด** ·
  ทะเบียน distillation จดเป็น `GapCallsAi` (`Services/Ai/Distillation/DistillationModelRegistry.cs:104`) · ไม่ได้ถูกเรียกจากเส้นคู่ค้า
- 21230 ในผังนี้ = **เจ้าหนี้กรรมการ** (แหล่งเงินจริงของ "กรรมการจ่ายแทน" — `wwwroot/pages/documents.html:1045,2265` · `OcrService.cs:2495,5353,7126`) — เป็นค่าที่ถูกต้อง**เฉพาะเมื่อมีหลักฐาน**ว่ากรรมการจ่าย · ใช้เป็นค่าตั้งต้นของ "ไม่รู้" = สร้างหนี้กรรมการปลอม
- gateway ด้วยบัตร: ไม่มีชนิดบัญชี "บัตรเครดิตบริษัท" ใน `BankAccount.AccountType` (`Savings/Current/Fixed` · `Models/Entities/BankAccount.cs:14`) ⇒ ตัวเลือก "บัตรเครดิต/เดบิต" เปลี่ยนแค่ `PaymentMethod` ไม่เปลี่ยนแหล่งเงิน

## 4. ความยินยอม (consent) วันนี้

| สิ่งที่มี | ที่ | ช่องว่าง |
|---|---|---|
| คู่ค้า `TradingPartnership` Pending→Accepted (สองฝ่ายยอมรับ · trust anchor) · ระงับ = หยุดทุกเส้นอัตโนมัติ | `Models/Entities/CrossTenantWorkflow.cs:5-63` · `Partners.cs:117-157` | ไม่มี "ขอบเขต" — Accepted = ทุกเส้นที่ค่าตั้งของแต่ละฝ่ายเปิด |
| ติ๊กระดับบริษัท `CreatePurchaseDraftsFromPartners` · `SendWhtCertsToPartners` (+`Since`) · `PartnerDiscoverable` | `CrossTenantWorkflow.cs:139-144` · หน้า `partners.html:75-82` · `PUT workflow-automation` `CrossTenantWorkflowController.cs:212-247` | **ไม่มี audit** ของการเปิด/ปิด (ใคร/เมื่อไร/ข้อความที่เห็น) · ไม่แยกต่อคู่ค้า · ไม่มีเวอร์ชันข้อความยินยอม · ไม่มี "อนุญาตให้บันทึกจ่ายเอง" |
| `AutoCreateReceiptFromIncomingPayment` | `CrossTenantWorkflow.cs:124` | เก็บ/echo แต่**ไม่มีผู้อ่านเชิงธุรกิจ** (F-L1 รอบ 258 ยังเปิด) |
| เพดานยอดใบซื้อร่างต่อคู่ `PurchaseDraftLimitA/B` | `CrossTenantWorkflow.cs:59-62` | เป็นตัวอย่างเดียวของ "ค่าตั้งต่อคู่" |
| A ฝั่ง "ส่งให้ B ออกเอง" | — | ❌ ไม่มีแนวคิดนี้เลย (A ตั้งอะไรแทน B ไม่ได้ — ถูกต้อง: ความยินยอมต้องเป็นของ B) |

## 5. ข้อค้นพบ (เรียงความรุนแรง)

| # | ระดับ | เรื่อง | หลักฐาน |
|---|---|---|---|
| C-01 | HIGH | แหล่งเงินออกของ B ตั้งต้นที่ "บัญชีแรกที่ใช้งาน" — ค่าที่แต่งขึ้น (F2 ข้อ 3) · ไม่มีชั้นหลักฐานใด ๆ | `PartnerPaymentNoticeService.cs:159-161` |
| C-02 | HIGH | ปิด AI ทั้งระบบ ⇒ 50 ทวิ ที่มาจากคู่ค้า NextAcc (หลักฐานแข็งที่สุดที่ระบบมี — แถวจริงในบริษัทผู้จ่าย) ค้าง AwaitUser · เรียก AI กับข้อมูลที่พิสูจน์ที่มาได้ด้วยฐานเอง = จ่าย token เปล่า + ข้อมูลออกนอก (กฎ #1 ข้อ 1/5) | `WhtCertIntakeService.cs:149-166` · `WhtCertIntakeVerdict.cs:86-87` |
| C-03 | HIGH | A ยกเลิกการรับชำระ ⇒ การแจ้งของ B ไม่ถูกแตะ — B ยังกดบันทึกจ่ายได้ (เงินที่ไม่ได้รับจริง) · ที่บันทึกแล้วไม่มีใครรู้ | `DocumentService.cs:11753` · `PartnerPaymentNoticeService` ไม่มีเมธอดถอน |
| C-04 | MED | การแจ้ง "ผู้ขายรับเงินแล้ว" เกิดเฉพาะเส้น `DocumentPaymentHandler` — A รับชำระมือ/ปิดหนี้จากรายการเดินบัญชี/นำเข้า/หลายใบ ⇒ B ไม่รู้ | `DocumentPaymentHandler.cs:128` (ผู้เรียกเดียว) |
| C-05 | MED | จ่ายบางส่วน/จ่ายเกิน ⇒ "ไม่เดา" ทุกกรณี (ถูกทิศ แต่ไม่มีทางอัตโนมัติ) · เงินเกินฝั่ง A ไม่มีขาออกอัตโนมัติถึงเงินรอจัดสรร (ดู team-D) | `PartnerPaymentNoticeRule.cs:94-96` · `DocumentPaymentHandler.cs:79-83` |
| C-06 | MED | ความยินยอมไม่มี audit/ขอบเขต/เวอร์ชัน/ถอน — สำคัญเมื่อจะให้ระบบลงบัญชีในสมุดของ B | `CrossTenantWorkflowController.cs:212-247` |
| C-07 | MED | สลิปให้ข้อมูลบัญชีไม่ได้เลย (ไม่มีตัวอ่านชื่อ/เลขบัญชีปิดบาง/ธนาคาร · ไม่ถอด QR) ⇒ ทั้ง "แหล่งเงินเข้า" ของ A และ "แหล่งเงินออก" ของ B ไม่มีชั้นหลักฐานจากสลิป | `OcrSlipText.cs:23` · `TesseractSlipImageReader.cs:18` |
| C-08 | MED | ใบซื้อ B ไม่มีผังบัญชี (ไม่คัดลอกโดยถูกต้อง) + ต้องอนุมัติเอง ⇒ ถ้าจะ "แทบไม่ต้องแตะ" ต้องมีการอนุมัติอัตโนมัติภายใต้ความยินยอม + ผังจากนักเรียนผังบัญชี | `Partners.cs:448-482` · `PartnerPaymentNoticeRule.cs:78-79` |
| C-09 | MED | สลิปจาก A ยืนยันแล้วไม่ไปถึง B (B ไม่มีหลักฐานการจ่ายในสมุดตัวเอง · ม.65 ตรี(8)/(9) ต้องมีหลักฐานการจ่าย/เอกสารประกอบ) | §2 ข้อ 5 |
| C-10 | LOW | ส่ง 50 ทวิ ข้ามเงียบเมื่อผู้ติดต่อผูกระบบภายนอกอื่นไว้ (ไม่ประทับเหตุ) | `PartnerWhtCertExchangeService.cs:76,90` · `CrossTenantWorkflowService.cs:774` |
| C-11 | LOW | คืนเงินผ่าน gateway ไม่มี handler (ทั้ง A ใบลดหนี้/คืนเงิน และ B รับเงินคืน) | `IPaymentCompletionHandler.cs:19-27` |
| C-12 | LOW | ไม่มีช่องรับสลิปทางอีเมล (เจ้าของพูดถึง "LINE/email/portal") | ไม่มี inbound mail ใน `Services/Implementations/Email/*` |
| C-13 | LOW | หน้าชำระสาธารณะไม่รู้ว่าผู้จ่ายเป็นบริษัท NextAcc (ไม่ล็อกอิน) ⇒ เลือกแหล่งเงินของ B บนหน้านั้นไม่ได้โดยไม่เปิดช่องรั่ว | `PublicPaymentController.cs:39-40` |

## 6. ตรวจแล้ว ไม่ใช่บั๊ก (อย่ารายงานซ้ำ)
- ช่องทางที่ A ใช้ส่งใบ (LINE/อีเมล/ลิงก์) ไม่ใช่ตัวตัดสินการส่งข้ามบริษัท — ส่งตอนอนุมัติด้วยเลขภาษี+สาขา ⇒ "ส่งทาง LINE แล้ว B ไม่ได้ใบซื้อร่าง" ต้องดูที่คู่ค้า/ผู้ติดต่อ ไม่ใช่ช่องทาง
- หลักฐานภาษีซื้อ: ระบบไม่เคยตั้ง `EtaxVerified` เอง (`PartnerInputVatEvidence.Of` `:323-324`) — ถูกต้อง (ไม่มีการตรวจ ETDA) · อย่างย่อไม่ใช่หลักฐาน (`IsTaxInvoiceCarrier` `:169-170`)
- กันซ้ำทั้งสองฝั่ง: `PAY-INTENT-{id}` (A) · `PARTNER-PAID-{intent}` + unique `(CompanyId, PaymentIntentId)` (B) · จองแถวแบบ compare-and-set (`PartnerPaymentNoticeService.cs:224-229`)
- audit ข้ามบริษัทใน SaveChanges เดียว — chain แยกต่อบริษัทถูก (review-258 "ตรวจแล้ว")
