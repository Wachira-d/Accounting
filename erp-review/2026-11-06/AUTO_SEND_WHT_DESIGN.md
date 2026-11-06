# AUTO_SEND_WHT_DESIGN.md — ส่งเอกสารอัตโนมัติครบทุกเอกสาร + รับใบ 50 ทวิ เข้าระบบอัตโนมัติ

> ทีมวิเคราะห์กระบวนการ + UX/UI · 2026-11-06 · **อ่านอย่างเดียว ไม่ได้แก้โค้ด** (รอบ 241 กำลังแก้ working tree อยู่)
> คำขอเจ้าของ (2026-11-06): "เคสส่งเอกสารอัตโนมัติ … ถ้ามีหัก ณ ที่จ่ายจะทำยังไง เปิดกี่ช่องทาง เพื่อให้ได้รับเอกสารเข้าระบบอัตโนมัติ ·
> แนบลิงก์ให้ลูกค้าอัปโหลดใบหัก ณ ที่จ่ายได้ไหม · ลูกค้าที่ใช้ NextAcc เหมือนกัน ติ๊กให้ระบบออกใบหัก ณ ที่จ่ายอัตโนมัติแล้วส่งให้ผู้ให้บริการเลย ·
> ใบเสร็จ/ใบกำกับที่มีหัก ณ ที่จ่ายแต่ยังไม่มีใบหัก ณ ที่จ่ายในระบบ มีแท็กเตือนรึยัง"
>
> สถานะป้าย: ✅ มีจริง (อ้างไฟล์:บรรทัด) · 🔨 รอบ 241 กำลังทำ (working tree ยังไม่ commit) · 📋 ออกแบบในเอกสารนี้ · ⚠️ ต้องยืนยันตัวบท/ทดสอบ
> เลขบรรทัดอ้าง HEAD `8bdd9e43` + working tree ณ 2026-11-06 — ไฟล์ของรอบ 241 ยังขยับได้ ให้ `python3 tools/callers.py <Symbol>` ก่อนลงมือ

---

## 0. คำตอบสั้นถึงเจ้าของ (TL;DR)

| คำถาม | คำตอบ |
| --- | --- |
| **ใบเสร็จ/ใบกำกับที่มี WHT แต่ยังไม่มีใบ 50 ทวิ — มีแท็กเตือนรึยัง?** | **ยังไม่มีบนตัวเอกสาร** ❌ — มีแค่ (1) หน้า "ภาษีถูกหัก (เครดิต ภงด.50)" `wht-credit.html:37` แสดงยอดรวม "รอหนังสือรับรอง" และกรองสถานะ Pending ได้ (2) แบนเนอร์ "ปิดยอดแล้วแต่ไม่ได้บันทึกภาษีถูกหัก" `wht-credit.html:254-260` (3) ยอด Pending ในรายงาน ภ.ง.ด.50 `TaxService.cs:2267` · **ไม่มี**ป้ายในรายการเอกสาร (`documents.html`) · ไม่มีในหน้าผู้ติดต่อ · ไม่มีการ์ดแดชบอร์ด · ไม่มีการทวงซ้ำอัตโนมัติ (รอบ 241 ส่งอีเมลขอใบ **ครั้งเดียว** หลังรับชำระ) · ป้ายที่มีอยู่ในหน้าเอกสาร ("50 ทวิ ต้องตรวจ" `documents.html:2400`) เป็น**ฝั่งซื้อ** (ใบที่เราต้องออก) ไม่ใช่ฝั่งขาย |
| **แนบลิงก์ให้ลูกค้าอัปโหลด 50 ทวิ ได้ไหม?** | **ได้ และแนะนำเป็นช่องทางหลัก** — ลิงก์ token ต่อ "ลูกค้า+ชุดเอกสาร" (ไม่ต้อง login) แนบใน: อีเมลใบเสร็จ · หน้าชำระสำเร็จ · อีเมลทวง · พอร์ทัลลูกค้า · ไฟล์เข้า "กล่องรับ 50 ทวิ" → OCR เติมช่อง + จับคู่ยอดให้ → ผู้ขายกด "ยืนยัน" 1 คลิก (หรือยืนยันอัตโนมัติเมื่อทุกช่องตรง ถ้าเปิดค่าตั้ง) |
| **เปิดกี่ช่องทาง?** | **4 ช่องทางตอนเปิดตัว + 2 ภายหลัง** — ทุกช่องเข้าประตูเดียว `WhtCertIntakeService.SubmitAsync` (§3) · เปิดตัว: ① ลิงก์อัปโหลด (อีเมล/หน้าชำระสำเร็จ/พอร์ทัล ใช้ endpoint เดียว) ② ผู้ขายอัปโหลด/สแกนเอง (มีแล้ว — ยกระดับ) ③ NextAcc↔NextAcc อัตโนมัติ ④ ติ๊ก "ลูกค้าหักผ่าน e-WHT (ไม่มีใบ)" พร้อมหลักฐาน · ภายหลัง: ⑤ LINE OA ⑥ อีเมลขาเข้า (ต้องมีโครงรับเมลก่อน — วันนี้ไม่มี) |
| **ลูกค้าใช้ NextAcc เหมือนกัน ติ๊กให้ออก 50 ทวิ อัตโนมัติแล้วส่งให้ผู้ขายได้ไหม?** | **ได้ — แต่ "ผู้ออก" ต้องเป็นบริษัทผู้จ่ายเสมอ** (ม.50 ทวิ) ⇒ การติ๊กต้องเกิดใน**บัญชี NextAcc ของผู้จ่าย** โดยผู้ใช้ที่มีสิทธิ์จ่ายเงิน+ออก 50 ทวิ (ไม่ใช่บนหน้าชำระสาธารณะแบบไม่ login) · ใบออกด้วยเลขรัน/แบบ ภ.ง.ด.3/53 ของผู้จ่ายผ่านเส้นเดิม `WithholdingTaxCertService` แล้ว**ส่งเข้าทะเบียนเครดิตของผู้ขายเป็น "ได้รับแล้ว" อัตโนมัติ** (ข้อมูลโครงสร้าง + PDF + hash · ไม่ต้อง OCR) · ต้องมี "การเชื่อมคู่ค้า" แบบยินยอมสองฝ่ายก่อน (PDPA) · ไม่ต้องรอหน้าชำระ: เฟสแรกทำที่ "ออก 50 ทวิ แล้ว" ในบัญชีผู้จ่าย (ไม่ว่าจ่ายทางไหน) — §3.5 |
| **ส่งเอกสารอัตโนมัติครบทุกเอกสารแล้วหรือยัง?** | ครบ ~70% — มีตาราง `EmailScheduleRule` (อนุมัติ/ใกล้ครบกำหนด/เกินกำหนด/รายเดือน/recurring/50 ทวิ/สลิปเงินเดือน) + รอบ 241 (ลิงก์ชำระ + ใบเสร็จหลังรับชำระ) · ช่องว่างหลัก: ใบเสร็จที่ออกอัตโนมัติไม่ผ่าน hook อนุมัติ, ทางรับชำระอื่น (กระทบธนาคาร/สลิปพอร์ทัล/นำเข้า/จัดสรรหลายใบ) ยังไม่เข้างานหลังรับชำระ, ไม่มีการทวง 50 ทวิ ซ้ำ, ไม่มีหน้าเมทริกซ์รวม — §1.3 |

---

## 1. สภาพปัจจุบัน (Current-state inventory)

### 1.1 โครงสร้างพื้นฐานที่มีอยู่แล้ว (ห้ามเขียนซ้ำ — reuse)

| ชิ้น | ที่อยู่ | บทบาท |
| --- | --- | --- |
| `EmailScheduleRule` + `EmailQueue` | `Models/Entities/EmailSchedule.cs:1-128` | ตารางส่งอัตโนมัติ trigger: `DocumentApproved` · `DocumentDueSoon` · `DocumentOverdue` · `PayrollPaid` · `WhtCertIssued` · `RecurringInvoiceCreated` · `MonthlyStatement` · คีย์กันซ้ำ `IdempotencyKey` (`:128`) |
| `EmailScheduleService` | `Services/Implementations/EmailScheduleService.cs` — `OnDocumentApprovedAsync:36` · `OnPayrollPaidAsync:58` · `OnWhtCertIssuedAsync:112` · `ScanMonthlyStatementsAsync:254` · `ScanDueSoonAndOverdueAsync:377` | ผู้เขียนคิวอีเมลเดียว · DueSoon/Overdue เลือกใบ `BalanceDue > 0` ตรงวัน |
| หน้า "ส่งอีเมลอัตโนมัติ" | `wwwroot/pages/email-schedule.html` · เมนู `layout.js:1780` | ตั้งกฎทีละแถว (ยังไม่ใช่เมทริกซ์) |
| 🔨 ลิงก์ชำระของเอกสาร | `Helpers/DocumentPayLinkPolicy.cs` (ใหม่) · `Services/Implementations/DocumentPayLinkService.cs` (ใหม่) · `Document.PayLinkToken:245` · `CompanySettings.PayLinkDefault*` · `EmailScheduleRule.AttachPayLink` | หนึ่งลิงก์ต่อใบ · ยอด Gross/WHT/Net จาก `DocumentPayLinkPolicy.AmountsFor` · อีเมลบอก "กรุณาส่งหนังสือรับรอง" (`EmailBlock`) |
| 🔨 งานหลังรับชำระ | `Services/Implementations/PostPaymentAutomationService.cs` · `Helpers/PostPaymentAutomationPolicy.cs` · `Models/Entities/PostPaymentAutomationRun.cs` | 1 แถว/การชำระ (unique) · ขั้น `IssueReceipt` / `EmailReceipt` / `EmailSourceAsReceipt` / `RequestWhtCert` / `EmailWhtCertToSupplier` · ล้มดัง 3 ที่ · retry 4 ครั้ง |
| 🔨 ขอ 50 ทวิ หลังรับชำระ | `PostPaymentAutomationService.cs:241-243` (ตรวจว่ามีแถว `WhtCreditReceived` ที่ไม่ใช่ Pending ไหม) · `:325-340` ส่งอีเมล `WhtCertRequestText` | **ครั้งเดียว** · **ไม่มีลิงก์อัปโหลด** · ไม่มีการทวงซ้ำ |
| ทะเบียนเครดิตภาษีถูกหัก | `Models/Entities/WhtCreditReceived.cs:21` · `WhtCreditStatus` (`AllEnums.cs:2534`: Pending/Received/Claimed/Expired) | 1 แถว **ต่อเอกสารขาย** (`DocumentId:57`) · `PaymentId:61` มีคอลัมน์แต่เส้น GL ไม่เติม · `AttachmentId:64` ไฟล์เดียว |
| ผู้สร้างแถว Pending | `DocumentService.SyncWhtCreditReceivedAsync:142` (อ่าน Dr 11910 จาก JE จริง · เรียกหลังรับชำระ `:14966` · อนุมัติ `:7678` · void `:11738`) | แถวเกิดจาก GL เท่านั้น ⇒ ทะเบียนกระทบ GL ได้ · ข้ามแถวที่มี `CertificateNumber` (`:176`) |
| ทางยืนยันได้รับใบ | `WhtCreditService.MarkReceivedAsync:289` (บังคับเลขที่ใบ · ผูกไฟล์) · หน้า `wht-credit.html` | ⚠️ โยน `InvalidOperationException` (`:293`) — ขัด กฎ #4 E (ควรเป็น `BusinessRuleException`) |
| OCR ใบ 50 ทวิ ที่ได้รับ | `OcrService.EnsureWhtCreditFromCertAsync:4443` · จับคู่แถว Pending ผู้จ่ายเดียวกัน ปีเดียวกัน ยอดต่าง ≤ ฿1 (`:4496-4505`) | ใช้ได้กับ "1 ใบ ↔ 1 แถว" เท่านั้น · ใบเดียวครอบหลายใบแจ้งหนี้ = จับไม่ได้ → สร้างแถวใหม่ (เสี่ยงซ้ำ) |
| ตัวตัดสินสถานะ 50 ทวิ ฝั่งแพลตฟอร์ม | `Helpers/PlatformWhtCertificateStatus.cs:23` (`State.NotRequired/AwaitingUpload/AwaitingReview/Confirmed/Rejected` · `IsOverdue` · `RemindRefusal` 24 ชม.) · `PlatformWhtCertificateService` | **ต้นแบบที่จะยกเป็นตัวกลางของทุกบริษัท** (§4) — ตอนนี้ใช้เฉพาะ NextAcc เก็บ 50 ทวิ จากผู้เช่า |
| ฝั่งซื้อ (เราออก 50 ทวิ) | `WithholdingTaxCertService.TryAutoIssueForDocumentAsync:851` · `IssueAsync:354` → `OnWhtCertIssuedAsync` (`:401`, `:831`) · `WhtCertIssuePlan` (มี e-Withholding label `:91`) · `WhtUnissuedCertGate` | ✅ ครบ — เส้นนี้คือสิ่งที่ "ผู้จ่ายที่เป็นลูกค้า NextAcc" จะใช้ออกใบให้ผู้ขาย (§3.5) |
| e-Withholding ฝั่งซื้อ | `Document.WhtViaEWithholding:671` · `WhtCertIssuePlan.EWithholdingLabel` | ✅ ฝั่งเราหัก · **ฝั่งขาย (ลูกค้าหักเราผ่าน e-WHT) ไม่มีธง** ❌ |
| พอร์ทัลลูกค้า | `PortalController.cs:110` `upload-slip` (ลูกค้าอัปโหลดสลิปต่อเอกสาร · login พอร์ทัล) | ต้นแบบ endpoint อัปโหลด — เพิ่ม `upload-wht-cert` คู่กัน |
| ลิงก์สาธารณะแบบ token | `PublicQuotationController` (`api/public/quotation/{token}` + rate limit `:52`) · `api/public/delivery/{token}/sign` · `PayslipPublicController` · `PlatformPaymentLinks.TokenHash` (`PlatformBillingPhase2Migration.cs:51-52`) | ต้นแบบ: **เก็บ hash ของ token** (แบบ PlatformPaymentLinks) — หมายเหตุ `Document.PayLinkToken` ของรอบ 241 เก็บ token ตรง ๆ (⚠️ ข้อสังเกตถึงทีม 241) |
| ผู้ส่งในนามบริษัท | `Helpers/OnBehalfEmailSender.cs` · `Helpers/PlatformEmailQuota.cs` · `DocumentEmailService.ResolveOnBehalfSenderAsync` (รอบ 240) | ส่งทุกอีเมลทวง/ลิงก์ผ่านตัวนี้ (โควตา + ผู้ส่ง) |
| คู่ค้าข้ามผู้เช่า (มีอยู่แล้ว) | `Models/Entities/CrossTenantWorkflow.cs:22` `TradingPartnership` · `Services/Implementations/CrossTenantWorkflowService.cs` · `Controllers/CrossTenantWorkflowController.cs` | ✅ การจับคู่บริษัท NextAcc↔NextAcc มีแล้ว · **ยังไม่ต่อกับการส่งเอกสาร/50 ทวิ** ⇒ เฟส 246 ต่อยอดตัวนี้ (แก้รอบ 250 WP-05 · คำตัดสิน 250-30: ต่อยอด `TradingPartnership` ที่มีอยู่ — ดู AUTOPILOT_BILLING_DESIGN §6 รอบ 251–253 · ไม่สร้างตารางสำเนา) |
| Intercompany | `IntercompanyTransaction` (`DimensionalAccounting.cs:75`) · `IntercompanyService` | **คนละเรื่อง** — รายการระหว่างบริษัทในกลุ่มเดียวกัน (ภายใต้เจ้าของเดียว) ไม่ใช่การแลกเอกสารข้ามผู้เช่า · **ไม่มี** `PartnerConnection`/Peppol/การผูก Contact กับ tenant อื่น ⇒ NextAcc↔NextAcc ต้องสร้างใหม่ (§3.5) |
| ต้นแบบ "ผู้เช่า ↔ NextAcc" | `PlatformBillingDocumentIssuer` (ออกใบลงบัญชีผู้เช่า `:320` เรียก `CreatePaymentAsync(tenantId…)`) · `PlatformWhtSuggestion` · `PlatformWhtCertificateService` | ✅ เคยเขียนข้อมูลข้าม tenant แล้วหนึ่งคู่ (แพลตฟอร์ม↔ผู้เช่า) — ใช้เป็นแบบของการส่ง 50 ทวิ ข้ามบัญชี |

### 1.2 เมทริกซ์ "เอกสารที่ลูกค้า/คู่ค้าเห็น × ส่งอัตโนมัติได้ไหมวันนี้"

| เอกสาร | ตอนอนุมัติ | ตามตารางเวลา (ใกล้/เกินกำหนด) | หลังรับชำระ | อื่น ๆ / ลิงก์สาธารณะ | หลักฐาน |
| --- | --- | --- | --- | --- | --- |
| ใบเสนอราคา | ✅ กฎ `DocumentApproved` (DocumentType=null = ทุกชนิด) | — | — | ✅ ลิงก์ยอมรับ/เซ็น `PublicQuotationController` · 🔨 รอบ 240 อีเมลลิงก์เซ็นในนามบริษัท | `EmailScheduleService.cs:36-56` |
| ใบแจ้งหนี้ / ใบวางบิล | ✅ | ✅ DueSoon/Overdue (`BalanceDue>0`) | — | 🔨 แนบลิงก์ชำระ + ช่องทาง | `:377-400` · `DocumentPayLinkService` |
| ใบกำกับภาษี (เต็มรูป) | ✅ | ✅ | 🔨 ถ้าใบต้นทางทำหน้าที่ใบเสร็จ (`EmailSourceAsReceipt`) | 🔨 ลิงก์ชำระ | `PostPaymentAutomationService.cs:178` |
| ใบเสร็จรับเงิน | ⚠️ ใบที่**ผู้ใช้กดอนุมัติ**เท่านั้น — ใบที่ออกอัตโนมัติจากการชำระ (`IssueReceiptForPaymentAsync:14554`) ไม่ได้ผ่าน `ApproveDocumentAsync:7693` | — | 🔨 `IssueReceipt` + `EmailReceipt` | — | |
| ใบเสร็จ/ใบกำกับภาษี | เหมือนใบเสร็จ | — | 🔨 (ตัดสินด้วย `SettlementReceiptPolicy.CarriesTaxInvoiceRole`) | — | `PostPaymentAutomationService.cs:96` |
| ใบกำกับภาษีอย่างย่อ (POS/หน้าร้าน) | ❌ ไม่พบเส้นอีเมลอัตโนมัติถึงลูกค้า (POS พิมพ์สลิป) | — | — | e-Tax ของออร์เดอร์เว็บผ่าน `SiteOrderPaymentHandler` (ไม่ได้ยืนยันว่าส่งอีเมล) | ⚠️ ต้องตรวจต่อ |
| ใบลดหนี้ / ใบเพิ่มหนี้ | ✅ กฎ DocumentApproved | ใบเพิ่มหนี้ฝั่งขาย: ✅ (มียอดค้าง) | 🔨 ใบเพิ่มหนี้รับชำระได้ผ่านลิงก์ (`DocumentPayLinkPolicy.IsSaleSide`) | — | |
| ใบส่งของ | ✅ กฎ DocumentApproved | — | — | ✅ ลิงก์เซ็นรับของ `api/public/delivery/{token}` | |
| ใบเสร็จมัดจำ | เหมือนใบเสร็จ | — | 🔨 | — | |
| ใบแจ้งยอด (Statement) | — | ✅ `MonthlyStatement` วันที่ N ของเดือน | — | ✅ พอร์ทัล `GET statement` | `EmailScheduleService.cs:254` · `PortalController.cs:88` |
| หนังสือทวงถาม (Dunning) | — | ⚠️ `OverdueDunningJob` แจ้ง**ภายใน**เท่านั้น (ไม่ส่งลูกค้า) · การทวงถึงลูกค้า = กฎ `DocumentOverdue` | — | — | `Services/Background/OverdueDunningJob.cs:3-13` |
| 50 ทวิ ที่**เรา**ออก (ฝั่งซื้อ) | ✅ กฎ `WhtCertIssued` | — | 🔨 `EmailWhtCertToSupplier` | — | `WithholdingTaxCertService.cs:401,831` |
| สลิปเงินเดือน | — | — | ✅ `PayrollPaid` | ✅ LINE (`PayslipLineDeliveryService`) + ลิงก์สาธารณะ | |
| ใบสั่งซื้อถึงผู้ขาย | ✅ กฎ DocumentApproved (DocumentType=PurchaseOrder) ใช้อีเมลของผู้ติดต่อ | — | — | ✅ พอร์ทัลผู้ขาย `vendor-portal.html` | `VendorPortalController.cs:28` |
| ยืนยันการจอง (CMS booking) | — | — | ✅ | ✅ | `CmsBookingService.ShopNotify.cs:76` |
| ยืนยันการจองที่พัก | — | — | ✅ ผ่านช่องกลาง `TryNotifyAsync` (รอบ 212) | ✅ หน้าแขก | `LodgingService.cs:21` |
| ออร์เดอร์ร้านค้าเว็บ | — | — | ✅ (e-Tax ใน handler) | — | `SiteOrderPaymentHandler.cs:14` |
| **คำขอ 50 ทวิ จากลูกค้า (ฝั่งขาย)** | — | ❌ ไม่มีทวงซ้ำ | 🔨 ครั้งเดียว ไม่มีลิงก์อัปโหลด | ❌ ไม่มีหน้าอัปโหลดสาธารณะ | |

### 1.3 ช่องว่าง (Gaps) — เรียงตามผลกระทบ

| # | ช่องว่าง | ผลต่อผู้ใช้ | แก้ในเฟส |
| --- | --- | --- | --- |
| G-1 | **ไม่มีป้าย "รอ 50 ทวิ" บนใบเสร็จ/ใบกำกับ/ใบแจ้งหนี้** ในรายการเอกสาร หน้าเอกสาร หน้าผู้ติดต่อ แดชบอร์ด | ไม่รู้ว่าต้องตามใบไหน — ยอด 11910 ค้าง ใช้เครดิต ภ.ง.ด.50 ไม่ได้ | 243 |
| G-2 | ไม่มีการทวงซ้ำ — รอบ 241 ขอครั้งเดียว | ลูกค้าลืม = เสียเครดิตทั้งก้อน | 244 |
| G-3 | ไม่มีช่องให้ลูกค้าส่งไฟล์เอง (ต้องส่งอีเมลส่วนตัว/LINE แล้วพนักงานอัปโหลดต่อ) | งานคนซ้ำ · ไฟล์หาย | 244 |
| G-4 | ทะเบียน 1 แถว/เอกสาร · ไฟล์เดียว · ไม่รองรับ "หลายใบต่อเอกสาร" (ผ่อนหลายงวด ลูกค้าออก 50 ทวิ ทุกงวด) และ "ใบเดียวครอบหลายใบแจ้งหนี้" | ยืนยันไม่ได้ / OCR สร้างแถวซ้ำ → เครดิตเกิน | 244 (ตาราง allocation) |
| G-5 | ไม่มีธง "ลูกค้าหักผ่าน e-WHT — ไม่มีกระดาษ" ฝั่งขาย | แถว Pending ค้างตลอดกาล หรือผู้ใช้ "ตัดสูญ" ผิด ๆ | 245 |
| G-6 | ทางรับชำระอื่นยังไม่เข้า `PostPaymentAutomationService.EnqueueAsync` (คอมเมนต์คลาสระบุเฉพาะ gateway + `DocumentController.CreatePayment`) — กระทบธนาคาร/settlement หลายใบ (`SettlementPostingService.cs:313`) · สลิปพอร์ทัล · นำเข้า (`PaymentImportTarget.cs:184`) · CMS/Lodging | ใบเสร็จ+คำขอ 50 ทวิ ไม่ออกในทางเหล่านี้ | 243 (ประสานกับ 241) |
| G-7 | ใบเสร็จที่ออกอัตโนมัติไม่ trigger กฎ `DocumentApproved` | ผู้ตั้งกฎ "ส่งใบเสร็จตอนอนุมัติ" คาดว่าได้ แต่ไม่ได้ (silent no-op) | 241 ครอบบางส่วนด้วย post-pay — ต้องให้หน้าเมทริกซ์บอกชัด (243) |
| G-8 | ~~ไม่มีการเชื่อมคู่ค้าข้ามผู้เช่า (NextAcc↔NextAcc)~~ มี `TradingPartnership` + `CrossTenantWorkflowService/Controller` แล้ว แต่**ยังไม่ต่อกับการส่งเอกสาร/50 ทวิ** (แก้รอบ 250 WP-05 · คำตัดสิน 250-30: ต่อยอด `TradingPartnership` ที่มีอยู่ — ดู AUTOPILOT_BILLING_DESIGN §6 รอบ 251–253 · ไม่สร้างตารางสำเนา) | ลูกค้า NextAcc ต้องดาวน์โหลด PDF แล้วส่งเอง | 246–247 |
| G-9 | `WhtCreditService.ExpireAsync` (`:360`) ไม่ลง JE — 11910 ค้างหลัง "ตัดสูญ" (ต้องผ่าน `SettleYearEndAsync`) | GL กับทะเบียนไม่ตรงระหว่างปี | ตรวจใน 245 |
| G-10 | `MarkReceivedAsync`/`ExpireAsync`/`CreateAsync` โยน `InvalidOperationException` | ข้อความไทยอาจถูก middleware กลืน (กฎ #4 E) | 243 (แก้ระหว่างทาง) |
| G-11 | ไม่มีหน้าเมทริกซ์รวม "เอกสาร × จังหวะส่ง" — ตั้งกฎทีละแถว | ไม่รู้ว่าเอกสารไหนส่งอัตโนมัติแล้วบ้าง | 243 |
| G-12 | ใบกำกับอย่างย่อ/POS ไม่มีทางส่งอีเมล/LINE ให้ลูกค้าที่ขอ | ลูกค้าขอใบทางอีเมลต้องทำมือ | backlog |

---

## 2. แผนผังกระบวนการ (End-to-end process maps)

### 2.1 ฝั่งขาย — "เราถูกหัก" (ลูกค้าจ่ายเรา)

```
[ใบเสนอราคา] --อนุมัติ/อีเมล+ลิงก์เซ็น--> [ลูกค้ายอมรับ]
      │ แปลง
      ▼
[ใบแจ้งหนี้ / ใบกำกับภาษี / ใบวางบิล]  ← WHT คาดการณ์ = Document.WithholdingTaxAmount (ตัวคำนวณ WHT เดิมตอนออกใบ)
      │  อีเมลตอนอนุมัติ (+ ลิงก์ชำระ 🔨 · + บล็อก "ยอดหลังหัก ณ ที่จ่าย")
      │  เตือนก่อนครบกำหนด / เกินกำหนด (DocumentDueSoon/Overdue)
      ▼
[รับชำระ] ── ช่องทาง:
      ├─ (A) ออนไลน์ผ่านลิงก์ (PaymentIntent → DocumentPaymentHandler) — ยอด Net, WHT = DocumentPayLinkPolicy.WhtForPayment
      ├─ (B) ลูกค้าส่งสลิป (พอร์ทัล upload-slip / ManualSlip) → พนักงานยืนยัน
      ├─ (C) กระทบรายการธนาคาร (bank feed match / settlement หลายใบ)
      ├─ (D) เงินสด/เช็ค/บันทึกมือ (DocumentController.CreatePayment)
      └─ (E) นำเข้าไฟล์ (PaymentImportTarget)
      │  ทุกทาง → DocumentService.CreatePaymentAsync → JE Dr เงิน + Dr 11910 (WHT) / Cr ลูกหนี้
      │                                              → SyncWhtCreditReceivedAsync ⇒ แถว WhtCreditReceived = Pending
      │  📋 ทุกทาง → PostPaymentAutomationService.EnqueueAsync (G-6)
      ▼
[งานหลังรับชำระ 🔨] ออกใบเสร็จ(/ใบกำกับ) → อีเมล → 📋 อีเมลมี "ลิงก์อัปโหลด 50 ทวิ" + หน้าชำระสำเร็จมีปุ่มอัปโหลด
      ▼
[รอ 50 ทวิ] 📋 ป้าย "รอ 50 ทวิ (n วัน)" บนเอกสาร/ผู้ติดต่อ/แดชบอร์ด · ทวงตามรอบ (วันที่ 7/21/45 — ตั้งได้)
      │ ช่องทางรับใบ (§3): ① ลิงก์อัปโหลด ② ผู้ขายสแกน ③ NextAcc↔NextAcc ④ e-WHT ⑤ LINE ⑥ อีเมลขาเข้า
      ▼
[กล่องรับ 50 ทวิ 📋] OCR เติมช่อง → จับคู่ (เลขผู้เสียภาษีเรา = ผู้ถูกหัก · ผู้จ่าย = ลูกค้า · ยอด · วันที่) → ตรวจอัตรา ท.ป.4/2528
      │ ผ่านทุกด่าน → "รอยืนยัน" (1 คลิก) หรือ ยืนยันอัตโนมัติ (ค่าตั้ง)
      ▼
[ได้รับแล้ว — Received] → เครดิตได้ (WhtCreditService.CreditableAmountAsync)
      ▼
[ภ.ง.ด.51 / ภ.ง.ด.50] ClaimAsync → Claimed · ปิดปี SettleYearEndAsync (ใช้กับ CIT / ขอคืน / ยกไป)
      ▼
[ไม่ได้ใบภายในกำหนดปิดปี] → รายงาน "50 ทวิ ที่ยังขาด" (§4.5) → ตามต่อ (ขอคืนได้ 3 ปี — WHT_CREDIT_PLAN L5) หรือ ตัดสูญ (Expire + JE)
```

**กรณีพิเศษ (ทุกข้อต้องมีเทสต์):**

| กรณี | พฤติกรรมที่ออกแบบ |
| --- | --- |
| **ชำระบางส่วน (ผ่อนหลายงวด)** | WHT ต่องวด = สัดส่วน (มีแล้ว — `Payment.WithholdingTaxAmount` + `DocumentPayLinkPolicy.AmountsFor` หัก WHT ที่หักไปแล้ว) · ลูกค้ามักออก 50 ทวิ **ทุกงวด** ⇒ ทะเบียนต้องรับ "หลายใบต่อเอกสาร" (allocation §7.1) · ป้ายแสดง "ได้รับ 1/3 ใบ · ขาด ฿x" |
| **มัดจำ** | ใบเสร็จมัดจำที่ลูกค้าหัก WHT ⇒ เกิด Pending ตามปกติ · ตอนหักกลบมัดจำในใบแจ้งหนี้สุดท้าย **ห้ามนับ WHT ซ้ำ** (ยอดคาดการณ์ของใบสุดท้าย = WHT ทั้งสัญญา − WHT ที่หักไปกับมัดจำ) — ตัวคำนวณอยู่ฝั่ง `AmountsFor` แล้ว ต้องเทสต์คู่มัดจำ |
| **ใบลดหนี้หลังรับชำระ** | ฐานเงินได้ลด ⇒ WHT ที่ลูกค้าหักไว้อาจเกิน · ลูกค้าต้อง**ออก 50 ทวิ ใบใหม่/แก้ใบ** (ผู้จ่ายแก้) · ระบบ: ถ้ามีใบ Received แล้ว ⇒ ป้าย "ยอดบนใบเกินฐานหลังลดหนี้ — ขอใบแก้ไข" (เตือน ไม่บล็อก) · ใช้ `WhtCreditNoteRemainder` (มีแล้ว) คิดส่วนเหลือ |
| **ยกเลิกการชำระ (void payment)** | `SyncWhtCreditReceivedAsync` ลบแถว Pending/Received เมื่อ GL เป็น 0 (`DocumentService.cs:184-191`) · 📋 ถ้ามีไฟล์ใบรับแล้ว ⇒ **ห้ามลบเงียบ**: แถว intake กลับเป็น "ไม่มีรายการให้จับคู่" + แจ้งผู้ขาย (ไฟล์ยังเก็บ 5 ปี) |
| **void ใบกำกับต้นทาง** | ใบเดิมต้องยกเลิก+ออกใหม่ (§86/4) ⇒ แถวเครดิตย้ายไปผูกใบใหม่ผ่าน `ReplacedByDocumentId` (resolver กลาง — ห้ามผูกเอง) |
| **ชำระครั้งเดียวหลายใบแจ้งหนี้** | settlement จัดสรร (`SettlementPostingService`) ⇒ Pending หลายแถว · ลูกค้ามัก**ออก 50 ทวิ ใบเดียว**รวม ⇒ allocation 1 ใบ → n แถว (§3.7) |
| **ลูกค้าต่างประเทศ** | ผู้จ่ายนอกประเทศไม่ใช่ผู้มีหน้าที่หักตาม ป.รัษฎากรไทย ⇒ **ไม่คาด WHT** · ม.70 ใช้กับ "เราจ่ายออกนอก" ไม่ใช่ฝั่งขาย ⇒ ถ้าเอกสารขายถึงผู้ติดต่อต่างประเทศมี WHT > 0 ⇒ เตือน "ตรวจ — ลูกค้าต่างประเทศโดยปกติไม่หักภาษีไทย" (warn-gate · DOCTRINE §2.3) · ภาษีที่ประเทศผู้จ่ายหักไว้ (foreign tax credit ตาม DTA) **คนละทะเบียน** — ไม่ใช่ 50 ทวิ (backlog) |
| **ลูกค้าบุคคลธรรมดา** | บุคคลธรรมดาทั่วไปไม่มีหน้าที่หักตาม ท.ป.4/2528 ⇒ ค่าตั้งต้น "ไม่คาด WHT" · ถ้าหักมาจริง (บางคนหัก) ยังต้องขอใบเหมือนเดิม — ตัวตัดสินดูจาก**การชำระที่บันทึก WHT จริง** ไม่ใช่ชนิดลูกค้า |
| **หน่วยงานรัฐ** | ส่วนราชการหัก 1% (ม.3 เตรส/ม.69 ทวิ ⚠️ ยืนยันอัตรา) · มักออกใบกระดาษช้า ⇒ ค่าตั้งต้นกำหนดทวงยาวกว่า (ต่อผู้ติดต่อ) |
| **เกณฑ์ 1,000 บาท** | ผู้จ่ายไม่ต้องหักถ้ายอดสัญญา < 1,000 แต่ถ้ารวมทั้งสัญญา ≥ 1,000 ต้องหักทุกงวด — ฝั่งขายเรา**ไม่บังคับ**ลูกค้า แต่ใช้ `ThaiWhtRateTable.ShouldWithhold` คิด "WHT คาดการณ์" บนลิงก์ชำระ (ไม่สร้างตารางอัตราที่สอง) |
| **อัตราตามประเภทเงินได้** | ตรวจด้วย `ThaiWhtRateTable` + `WhtCertIncomeType.Infer` (มีแล้ว) — ยอดบนใบผิดอัตรา ⇒ `RateWarning` (มีใน `WhtCreditRow`) แสดงบนกล่องรับ |
| **e-WHT** | ลูกค้าจ่ายผ่านธนาคารที่ร่วม e-Withholding ⇒ ธนาคารหักและนำส่ง · **ไม่มีกระดาษ 50 ทวิ** · ผู้ถูกหักตรวจข้อมูลในระบบ e-Withholding Tax ของกรมสรรพากร ⇒ สถานะ "e-WHT — ไม่มีใบ" ต้องมี**หลักฐาน** (§3.6) · อัตรา e-WHT 1% (ค่าบริการ) ถึง 31 ธ.ค. 2570 ⚠️ (ข่าว ปชส. 14/2569 — ต้องยืนยัน) |

### 2.2 ฝั่งซื้อ — "เราหักคนอื่น" (เราจ่ายผู้ขาย)

```
[ใบสั่งซื้อ] --อนุมัติ/อีเมล (กฎ DocumentApproved · PO)/พอร์ทัลผู้ขาย--> [รับของ GRN]
      ▼
[ใบแจ้งหนี้ซื้อ / ค่าใช้จ่าย / ใบสำคัญจ่าย] — WHT ต่อบรรทัด (PerLineWht · ThaiWhtRateTable · WhtPayeeKind)
      ▼
[จ่ายเงิน] → JE Cr 2191x (หนี้ภาษีหัก ณ ที่จ่าย)
      ▼
[ออก 50 ทวิ] TryAutoIssueForDocumentAsync (ติ๊ก "สร้างอัตโนมัติเมื่ออนุมัติ/จ่าย" documents.html:1074) — เลขรันของเรา · ภ.ง.ด.3/53/2/54
      │  e-WHT ⇒ WhtCertIssuePlan.NotApplicable (ไม่ออก)
      ├─ อีเมลถึงผู้ขาย: กฎ WhtCertIssued ✅ · 🔨 post-pay EmailWhtCertToSupplier
      └─ 📋 ผู้ขายเป็นผู้เช่า NextAcc ที่เชื่อมแล้ว ⇒ ส่งเข้ากล่องรับ 50 ทวิ ของผู้ขาย (§3.5)
      ▼
[ยื่น ภ.ง.ด.3/53 + นำส่ง] วันที่ 7 (กระดาษ) / 15 (e-Filing) · WhtUnissuedCertGate กันยื่นเมื่อยังมีใบไม่ออก
```

ฝั่งซื้อ **ครบแล้ว** — ต่อเติมเพียงจุดส่งข้ามบัญชี (§3.5) และป้ายส่งถึงผู้ขายแล้ว/ยัง (ใช้ `EmailQueue` สถานะเดิม)

---

## 3. ช่องทางรับ 50 ทวิ เข้าระบบ (Intake channels)

### 3.0 หลักการ: ประตูเดียว

ทุกช่องทาง **ห้ามเขียน `WhtCreditReceived` ตรง** — ต้องผ่าน:

```
WhtCertIntakeService.SubmitAsync(companyId, IntakeSubmission { Channel, ContactHint, DocumentHints[], File?, StructuredData?, EvidenceKind, SubmittedBy })
   ├─ 1. เก็บไฟล์ผ่าน storage abstraction กลาง (ห้ามเขียน disk) + SHA-256 (กันไฟล์ซ้ำ: unique CompanyId+FileHash)
   ├─ 2. สกัดข้อมูล: StructuredData (NextAcc) > OCR (เส้น OcrService เดิม — มี distillation แล้ว) > ไม่มี (คนกรอก)
   ├─ 3. จับคู่: WhtCertMatchPolicy.Match(cert, candidates)   ← pure helper (§3.7)
   ├─ 4. ตัดสินสถานะ: WhtCertIntakeVerdict.Decide(...)          ← pure helper (auto-confirm ได้ไหม)
   ├─ 5. เขียน WhtCertIntake + WhtCertAllocation (+ MarkReceived ถ้า auto) ใน tx เดียว + AddChainedAuditLog
   └─ 6. แจ้งผู้ขาย (กระดิ่ง/LINE กลุ่ม) + ตอบผู้ส่ง ("ได้รับแล้ว — กำลังตรวจ")
```

ล็อกด้วย `tools/required_call_site_check.py`: ทุกผู้เรียก (`WhtCertPublicController.Upload` · `PortalController.UploadWhtCert` · `WhtCreditController.Upload` · `OcrService.EnsureWhtCreditFromCertAsync` · `TenantWhtCertExchange.DeliverAsync` · `LineWebhook…`) ต้องเรียก `SubmitAsync` และห้ามมี `_db.WhtCreditsReceived.Add` นอก `DocumentService.SyncWhtCreditReceivedAsync` + `WhtCreditService.CreateAsync`

### 3.1 สรุปคำแนะนำ — เปิด 4 ช่องทางตอนเปิดตัว + 2 ภายหลัง

| # | ช่องทาง | ใครส่ง | ความพยายามของลูกค้า | ความน่าเชื่อถือของหลักฐาน | ต้นทุนพัฒนา | ลำดับ |
| --- | --- | --- | --- | --- | --- | --- |
| ① | **ลิงก์อัปโหลดปลอดภัย** (อีเมลใบเสร็จ · หน้าชำระสำเร็จ · อีเมลทวง · พอร์ทัล) | ลูกค้า | ต่ำ (กดลิงก์ ลากไฟล์) | ไฟล์จริง + OCR | กลาง (endpoint สาธารณะ + หน้า mobile) | **P1 — 244** |
| ② | **ผู้ขายอัปโหลด/สแกนเอง** (หน้า wht-credit + สแกนเอกสาร) | พนักงานเรา | — | ไฟล์จริง + OCR | ต่ำ (มีแล้ว — ย้ายเข้าประตูเดียว + หลายไฟล์) | **P1 — 243** |
| ③ | **NextAcc ↔ NextAcc อัตโนมัติ** | ระบบของผู้จ่าย | ศูนย์ (หลังเชื่อมครั้งแรก) | **สูงสุด** — ข้อมูลโครงสร้างจากผู้ออกจริง + PDF + hash | สูง (consent ข้ามบัญชี) | **P2 — 246/247** |
| ④ | **e-WHT (ไม่มีใบ)** | พนักงานเรา / ระบบเสนอจากรายการธนาคาร | ศูนย์ | หลักฐานจากข้อมูลกรมสรรพากร/ธนาคาร | ต่ำ-กลาง | **P1 — 245** |
| ⑤ | LINE OA ของบริษัท | ลูกค้า | ต่ำ (ส่งรูปในแชท) | รูปถ่าย (คุณภาพต่ำกว่า) + OCR | กลาง (`CustomerLineWebhookController` มีโครงรับ CMS — ต้องผูกผู้ติดต่อ) | P3 — backlog |
| ⑥ | อีเมลขาเข้า (ตอบกลับพร้อมไฟล์แนบ) | ลูกค้า | ต่ำที่สุด (ตอบเมล) | ไฟล์จริง + OCR · ผู้ส่ง spoof ได้ | **สูง** — วันนี้ไม่มีโครงรับเมล (ไม่มี MX/inbound provider ในโค้ด) | P3 — ต้องตัดสินโครงก่อน |

**ทำไมไม่เปิด ⑤⑥ ตั้งแต่แรก:** ① ครอบเคสเดียวกันด้วยต้นทุนต่ำกว่าและตัวตนผูก token อยู่แล้ว · ⑥ ต้องตั้ง MX/SPF/DKIM ขาเข้า + กัน spoof (คนนอกส่งใบปลอมเข้ากล่อง) · ⑤ ต้องผูก LINE userId กับผู้ติดต่อก่อน (ตอนนี้ผูกเฉพาะลูกค้า CMS — `CustomerLineLinkService`) · ทั้งสองเข้าประตูเดียวกันได้ภายหลังโดยไม่แก้แกน

### 3.2 ช่องทาง ① — ลิงก์อัปโหลดปลอดภัย

**Token**
- ตาราง `WhtCertUploadLink` (§7.1) — `TokenHash` (SHA-256 hex · **ไม่เก็บ token ตรง** — แบบ `PlatformPaymentLinks.TokenHash`) · scope = `CompanyId + ContactId` + รายการเอกสารที่ "รอ 50 ทวิ" ตอนออก (ลิงก์เดียวใช้ได้กับทุกใบที่ค้างของลูกค้านั้น — ลูกค้ามักออกใบรวม)
- อายุ: ค่าตั้งต้น 180 วัน หรือ 60 วันหลังสิ้นรอบบัญชีของใบล่าสุด (เลือกค่ามากกว่า) · ต่ออายุอัตโนมัติเมื่อมีอีเมลทวงใหม่ · เพิกถอนได้
- token 256 บิตจาก CSPRNG (`DocumentPayLinkPolicy.NewToken` ใช้ซ้ำได้) · URL: `/wht-upload.html?c={companyId}&t={token}`
- หน้าเปิดได้โดยไม่ login · **ไม่แสดงยอด/ชื่อเอกสารของลูกค้ารายอื่น** · แสดงเฉพาะ: ชื่อบริษัทเรา, เลขผู้เสียภาษี+สาขาของเรา (ให้ลูกค้ากรอกใบถูก), รายการเอกสารของลูกค้านี้ที่รอใบ (เลขที่ · วันที่ชำระ · ยอด WHT)

**การตรวจไฟล์ (server)**
- ชนิด: PDF / JPEG / PNG / HEIC → แปลง · ตรวจ magic bytes (ไม่เชื่อ Content-Type) · ≤ 10 MB/ไฟล์ · ≤ 10 ไฟล์/ครั้ง
- PDF: ปฏิเสธไฟล์ที่มี JavaScript/EmbeddedFile (ยกเว้น PDF/A-3 ที่ฝัง XML ของ NextAcc — ช่อง ③)
- rate limit ต่อ token + ต่อ IP (pattern `PublicQuotationController.cs:52` → `IRateLimiter.TryConsumeAsync`, DB-backed — กฎ #4 D ห้าม static dict)
- hash ซ้ำในบริษัทเดียวกัน ⇒ ตอบ "ได้รับไฟล์นี้แล้วเมื่อ …" (idempotent ไม่สร้างแถวใหม่)
- ทุกค่าที่สะท้อนกลับในหน้า/อีเมล HtmlEncode (กฎ #4 C)

**หลังส่ง** — สถานะของลิงก์แต่ละใบเปลี่ยนเป็น "ส่งแล้ว — รอตรวจ" ทันที (ลูกค้าเห็นว่าส่งสำเร็จ · ผู้ขายเห็นในกล่องรับ) · ระบบทวงหยุดสำหรับใบที่ถูกจับคู่ (ไม่ทวงซ้ำระหว่างรอตรวจ)

**จุดที่แสดงลิงก์ (endpoint เดียว ต่างแค่ที่มา `?src=`):**
1. อีเมลใบเสร็จหลังรับชำระ (241 `EmailReceipt`) — บล็อก "ส่งหนังสือรับรองหัก ณ ที่จ่ายให้เรา" เมื่อการชำระมี WHT > 0
2. อีเมลขอ 50 ทวิ (241 `RequestWhtCert`) + อีเมลทวง (§4.3)
3. หน้าชำระสำเร็จของลิงก์ชำระ (`pay-document.html` 🔨) — ปุ่ม "อัปโหลดหนังสือรับรองตอนนี้" (ถ้าลูกค้ามีใบพร้อมแล้ว)
4. พอร์ทัลลูกค้า — แท็บ "หนังสือรับรองหัก ณ ที่จ่าย" (login พอร์ทัล — `PortalController` เพิ่ม `upload-wht-cert` คู่ `upload-slip:110`)
5. ปุ่ม "คัดลอกลิงก์อัปโหลด" ในหน้าเอกสาร/ผู้ติดต่อ (ผู้ขายส่งทาง LINE ส่วนตัวเอง)

### 3.3 ช่องทาง ② — ผู้ขายอัปโหลด/สแกนเอง (ยกระดับของเดิม)

- หน้า `wht-credit.html` ปุ่ม "+ อัปโหลดใบ 50 ทวิ" รับหลายไฟล์ → `SubmitAsync(Channel=Manual)` → กล่องรับ (แทนการกรอกแบบฟอร์มเปล่า)
- หน้าเอกสาร (แผง 50 ทวิ ฝั่งขาย §6.6) ลากไฟล์ลงได้ — DocumentHint = ใบนั้น (จับคู่แม่นขึ้น)
- สแกนเอกสารทั่วไป: `OcrDocumentRoleInferrer` แยกได้ว่าเป็น 50 ทวิ ที่ได้รับ → `EnsureWhtCreditFromCertAsync` **ต้องย้ายมาเรียก `SubmitAsync`** (ไม่สร้างแถวเอง — ปิดความเสี่ยงซ้ำ G-4)
- กฎ #3: หลัง OCR ฟอร์มยืนยันต้องเติมครบ: เลขที่ใบ · วันที่ · ผู้จ่าย (ชื่อ/เลขภาษี/สาขา) · ผู้ถูกหัก (ตรวจว่าเป็นเรา) · ประเภทเงินได้ · ยอดเงินได้ · อัตรา · ภาษี · แบบ (ภ.ง.ด.3/53) · เอกสารที่จับคู่ — ค่าความมั่นใจ < 0.85 ไฮไลต์เหลืองแต่ไม่ว่าง

### 3.4 ช่องทาง ⑦ (ไม่ใช่ช่องแยก) — OCR + จับคู่อัตโนมัติ

- **ไม่ต้องเพิ่ม `AiFeatureKey` ใหม่** — การอ่านใบใช้ OCR pipeline เดิม (มี distillation/kill-switch ครบตามกฎ #1 อยู่แล้ว) · การจับคู่เป็นเลขคณิต + กุญแจ (เลขผู้เสียภาษี) = **คำนวณได้ ⇒ ห้ามถาม AI** (DOCTRINE §2.1 "สิ่งที่คำนวณได้")
- ถ้าภายหลังต้องการ AI ช่วยเลือก "ใบนี้ครอบเอกสารไหน" เมื่อกุญแจไม่พอ (เช่น ลูกค้าเขียนเลขอ้างอิงเป็นข้อความอิสระ) ⇒ ต้องทำตามกฎ #1 ครบ: `AiFeatureKey.WhtCertAllocation` + `GenericFeedbackDistillationModel` ไม่พอ (คำตอบเป็นชุด) ⇒ bespoke model · candidate set = เอกสาร Pending ของผู้จ่ายรายนั้น · ยอดเงินที่ AI อ้างต้องเขียนทับด้วยยอดจริงจาก DB ก่อนคิดคะแนน (DOCTRINE §2.4 ข้อ 3) · **ไม่อยู่ในแผนเฟสแรก**
- Round-trip learning (กฎ #3 ข้อ 5): ช่องที่ผู้ขายแก้ในฟอร์มยืนยัน → `RecordUserChoiceAsync` ผ่าน feedback ของ OCR เดิม

### 3.5 ช่องทาง ③ — NextAcc ↔ NextAcc (ผู้จ่ายออก 50 ทวิ อัตโนมัติ แล้วส่งถึงผู้ขาย)

**ข้อเท็จจริงทางกฎหมายที่คุมการออกแบบ:** หนังสือรับรอง 50 ทวิ **ต้องออกโดยผู้จ่ายเงินได้** (ป.รัษฎากร ม.50 ทวิ) ใช้เลขที่ของผู้จ่าย ผูกกับแบบ ภ.ง.ด.3/53 ที่**ผู้จ่าย**ยื่น ⇒ NextAcc/ผู้ขาย **ออกแทนผู้จ่ายไม่ได้** · สิ่งที่ระบบทำได้คือ "ทำให้ผู้จ่ายออกได้ในคลิกเดียว และส่งถึงผู้ขายเอง"

#### 3.5.1 การเชื่อมคู่ค้า (Tenant link) — ครั้งเดียวต่อคู่

```
ผู้ขาย S (ผู้เช่า NextAcc)                          ผู้จ่าย P (ผู้เช่า NextAcc)
Contact(P) TaxId+Branch ──── ตรงกับ ────→ Company(P).TaxId + BranchCode (Company.cs:9-10) และ P active (สมาชิกภาพไม่หมดอายุ)
                ระบบ "ไม่บอก S ว่า P ใช้ NextAcc" จนกว่า P ยินยอม (ข้อมูลการเป็นลูกค้า NextAcc = ข้อมูลของ P)
```

ทางเกิดลิงก์ (สองทาง):
1. **P เริ่ม** (แนะนำ): ในบัญชี P หน้า "คู่ค้าที่ใช้ NextAcc" → ระบบเสนอผู้ขายที่ Contact ของ P ตรงกับ Company ที่ active (P เห็นได้ เพราะเป็นผู้ติดต่อของ P เอง) → P กด "เชื่อมเพื่อส่ง 50 ทวิ อัตโนมัติ" → S ได้คำขอ → S กดยอมรับ
2. **S เชิญ**: S กด "เชิญลูกค้าเชื่อม" ในหน้าผู้ติดต่อ → อีเมลถึงอีเมลผู้ติดต่อ (ไม่เปิดเผยว่า P ใช้ NextAcc หรือไม่) → ถ้า P ใช้อยู่ login แล้วยอมรับ · ถ้าไม่ใช้ ลิงก์พาไปช่องทาง ① แทน

`TenantPartnerLink` (§7.1): `SellerCompanyId · BuyerCompanyId · SellerContactIdInBuyer · BuyerContactIdInSeller · Scope (WhtCert ตอนนี้ · ขยายเป็น e-invoice ได้) · Status (Requested/Active/Revoked) · ConsentBySeller(UserId, At, TextVersion, Hash) · ConsentByBuyer(...) · RevokedAt/By/Reason`
- ฝ่ายใดเพิกถอนได้ทันที · เพิกถอนแล้วไม่ส่งใหม่ (ของที่ส่งไปแล้วคงอยู่ — เป็นเอกสารภาษีของผู้รับ 5 ปี)
- ผู้กดยอมรับ: ฝั่ง P ต้องมี `PermissionKeys.TaxFile` (ออก 50 ทวิ/ยื่นแบบ) **และ** สิทธิ์จ่าย (`ExpensePay` หรือ `DocumentApprove`) · ฝั่ง S ต้องมี `TaxFile`

#### 3.5.2 การส่ง (เฟส 246 — ไม่ต้องพึ่งหน้าชำระ)

```
บัญชี P: ใบแจ้งหนี้ซื้อ/ใบสำคัญจ่าย ของผู้ติดต่อ S → จ่ายเงิน (ทางไหนก็ได้) → WithholdingTaxCertService.IssueAsync (เลขรันของ P · ภ.ง.ด.53 ของ P)
         → OnWhtCertIssuedAsync (มีแล้ว :401/:831) → 📋 TenantWhtCertExchange.OnIssuedAsync(P, certId)
              ├─ ผู้รับเงิน (PayeeContactId) มี TenantPartnerLink Active ไปยัง S และ P เปิด "ส่งอัตโนมัติ" ? ไม่ใช่ ⇒ จบ (ทางอีเมลเดิมยังทำงาน)
              ├─ สร้าง WhtCertExchangeOutbox (unique P+certId+version) — ส่งหลัง commit · retry · SKIP LOCKED (แบบ PostPaymentAutomationRuns)
              └─ worker: render PDF (renderer เดิมของ 50 ทวิ) + payload โครงสร้าง (JSON) + SHA-256 ของทั้งคู่
                    → WhtCertIntakeService.SubmitAsync(S, Channel=NextAccLink, StructuredData, File, SourceCompanyId=P, SourceCertId)
บัญชี S: intake → จับคู่ด้วย "เลขที่ใบแจ้งหนี้ของ S ที่ P บันทึกไว้ใน SupplierInvoiceNumber" + ยอด + เลขภาษี → Received อัตโนมัติ (ค่าตั้งต้นเปิด — หลักฐานชั้นสูงสุดตาม DOCTRINE G1: ข้อมูลจากระบบผู้ออก > กระดาษ > OCR)
```

**ทำไม Received อัตโนมัติไม่ขัดหลักการ #3 ("ห้ามประทับสถานะปลายทางเอง"):** สถานะ "ได้รับใบ" ประทับเมื่อ**ผู้จ่ายออกใบจริง**ในระบบของผู้จ่าย (เหตุการณ์ภายนอกเกิดจริง) ไม่ใช่ระบบเดา · และถ้า P ยกเลิกใบ (`VoidAsync:413`) → ส่ง event void → แถวของ S กลับเป็น "รอใบ (ผู้จ่ายยกเลิกใบ X)" + แจ้งเตือน · ถ้าแถวถูกใช้เครดิตในแบบไปแล้ว (Claimed) ⇒ ไม่ย้อนเงียบ: ป้ายแดง "ใบที่ใช้เครดิตแล้วถูกผู้ออกยกเลิก — ตรวจแบบที่ยื่น" + audit

#### 3.5.3 ติ๊กบนหน้าชำระ (เฟส 247 — ตามที่เจ้าของขอ)

หน้าชำระสาธารณะ (`pay-document.html`) **ไม่ login** ⇒ ผู้กดอาจเป็นใครก็ได้ (พนักงานบัญชี/คนนอก) ⇒ **ติ๊กบนหน้าสาธารณะไม่มีอำนาจผูกบริษัท P** · การออกแบบ:

1. หน้าชำระตรวจฝั่งเซิร์ฟเวอร์ว่า Contact ของใบนี้มี `TenantPartnerLink` Active (หรือเลขภาษีตรงผู้เช่า active **และ** S เปิด "เสนอให้ลูกค้า NextAcc เชื่อม") → แสดงกล่อง "ชำระผ่านบัญชี NextAcc ของบริษัทคุณ (ออก 50 ทวิ ให้อัตโนมัติ)" — ถ้ายังไม่เชื่อม ข้อความไม่ระบุว่า P ใช้ NextAcc (แสดงเหมือนกันกับทุกลูกค้าที่เลขภาษี 13 หลักเป็นนิติบุคคล — กันการสอดส่อง)
2. กดแล้ว → login NextAcc (SSO เดิม) → ระบบเลือกบริษัท P ที่ผู้ใช้เป็นสมาชิกและเลขภาษีตรง → หน้า "ชำระใบแจ้งหนี้จากผู้ขาย" **ในบัญชี P**:
   - สร้าง/ใช้ ใบแจ้งหนี้ซื้อฉบับร่างในบัญชี P จากข้อมูลใบของ S (ผู้ขาย · เลขที่ · วันที่ · รายการ · VAT) — กฎ #3 เติมครบ · P ยืนยัน
   - ส่วน WHT: ประเภทเงินได้ + อัตรา คิดด้วยตัวตัดสินฝั่งซื้อของ P (`PerLineWht` / `ThaiWhtRateTable`) — **ไม่ใช่ตัวเลขที่ S เสนอ** (ผู้จ่ายรับผิดตาม §54)
   - ☑ "ให้ระบบออกหนังสือรับรอง 50 ทวิ อัตโนมัติเมื่อชำระ และส่งให้ผู้ขายทาง NextAcc" (ค่าตั้งต้นตาม `TenantPartnerLink.BuyerAutoIssueDefault`)
   - ปุ่มชำระ → PaymentIntent ของ S (ยอด Net) + บันทึกจ่ายในบัญชี P เมื่อ intent สำเร็จ → `TryAutoIssueForDocumentAsync` → ส่งตาม §3.5.2
3. ผู้ใช้ที่ติ๊กต้องมีสิทธิ์ `TaxFile` + สิทธิ์จ่าย (`ExpensePay`) ใน P · ไม่มีสิทธิ์ ⇒ ซ่อนช่องติ๊ก + ข้อความ "ผู้มีสิทธิ์ออก 50 ทวิ ของบริษัทคุณจะได้รับคำขอ" (สร้างงานรออนุมัติในบัญชี P · ห้าม silent no-op)

**ความล้มเหลว (failure modes):**

| เหตุ | พฤติกรรม |
| --- | --- |
| ข้อมูล P ไม่ครบ (ที่อยู่/เลขภาษี) ⇒ ออก 50 ทวิ ไม่ได้ | ใบเข้า "เอกสารรอออกใบ" ของ P (เส้นเดิม `WhtCertIssuePlan`) · S เห็น "ผู้จ่ายยังไม่ได้ออกใบ" · ทวงตามปกติ |
| จ่ายแล้วแต่ intent ล้ม/ยกเลิก | ไม่ออกใบ (ออกตอนบันทึกจ่ายสำเร็จเท่านั้น) |
| ส่งข้ามบัญชีล้ม (S ถูกระงับ/ลิงก์ถูกเพิกถอนระหว่างทาง) | outbox → Failed หลัง retry · P เห็นสถานะบนใบ "ส่งผ่าน NextAcc ไม่สำเร็จ — ส่งอีเมลแทน" + fallback อีเมล PDF (เส้นเดิม) |
| ยอดบนใบของ P ≠ WHT ที่ S บันทึก | intake ไม่ auto-confirm · S เห็นผลต่าง + เหตุ (อัตรา/ฐานต่างกัน) · เตือน ไม่บล็อก |
| P ยกเลิก/ออกใบแทน | event void/replace → S อัปเดต (§3.5.2) |
| S ไม่ได้เปิดรับ | P ใช้อีเมลตามเดิม — ไม่มีอะไรหาย |

**ความเป็นส่วนตัวระหว่างผู้เช่า:** ส่งเฉพาะข้อมูลที่อยู่บน 50 ทวิ (ซึ่ง S มีสิทธิ์ได้รับตามกฎหมายอยู่แล้ว) · ไม่มี API ให้ S อ่านข้อมูลอื่นของ P · ไม่ผูก `CompanyId` ข้ามใน query ปกติ — ตัวส่งเป็น service ฝั่งระบบ (เหมือน `PlatformBillingDocumentIssuer`) ที่เขียนลงบัญชีผู้รับผ่าน `SubmitAsync(companyId: S)` เท่านั้น · audit ทั้งสองฝั่ง (hash chain ของแต่ละบริษัท)

### 3.6 ช่องทาง ④ — e-WHT (ลูกค้าหักผ่าน e-Withholding)

- ธง `WhtCreditReceived.EvidenceKind = EWithholding` (enum §7.1) — **ไม่ใช่ `CertificateNumber` ว่าง** (G3: "ไม่มีใบเพราะ e-WHT" ≠ "ยังไม่ได้ใบ")
- ตั้งได้ 3 ทาง: (ก) ผู้ขายติ๊ก "ลูกค้าหักผ่าน e-WHT" บนการรับชำระ/แผง 50 ทวิ (ข) ค่าตั้งต่อผู้ติดต่อ "ลูกค้ารายนี้ใช้ e-WHT เสมอ" (ค่าเสนอ — ยังต้องมีหลักฐาน) (ค) ระบบ**เสนอ** (ไม่ประทับ) เมื่อรายการธนาคารมีคำบอก e-WHT/อัตรา 1% กับค่าบริการ — warn-gate
- **หลักฐานบังคับก่อนนับเครดิต:** ไฟล์/ภาพจากระบบ e-Withholding Tax ของกรมสรรพากร (รายการภาษีที่ถูกหักของเรา) หรือไฟล์นำเข้า (CSV) ที่มีเลขอ้างอิงธนาคาร · ไม่มีหลักฐาน ⇒ สถานะ "e-WHT — รอหลักฐาน" (ไม่เครดิต) · มีหลักฐาน ⇒ "e-WHT — ยืนยันแล้ว" = นับเครดิตเหมือน Received
- 📋 ภายหลัง: นำเข้ารายงาน e-WHT ทั้งเดือน (CSV) → จับคู่หลายแถวทีเดียว (เข้าประตูเดียว Channel=EWhtImport)
- ⚠️ ต้องยืนยันกับผู้ทำบัญชี: รูปแบบหลักฐาน e-WHT ที่สรรพากรยอมรับตอนตรวจ ภ.ง.ด.50

### 3.7 กติกาจับคู่ (WhtCertMatchPolicy — pure · DOCTRINE G6)

อินพุต: ข้อมูลบนใบ (ผู้จ่ายเลขภาษี+สาขา · ผู้ถูกหักเลขภาษี · วันที่ · เงินได้ · ภาษี · ประเภทเงินได้ · ข้อความอ้างอิง) + candidates (แถว Pending/บางส่วน ของบริษัทนี้ ทุกปีที่เปิด)

| ชั้น | กติกา | ผล |
| --- | --- | --- |
| K0 ข้อเท็จจริงแข็ง (short-circuit) | เลขผู้เสียภาษี**ผู้ถูกหัก** ≠ ของเรา | **ปฏิเสธ** "ใบนี้ไม่ได้ออกให้บริษัทเรา" (ไม่จับคู่ ไม่เครดิต) |
| K1 | เลขผู้เสียภาษีผู้จ่าย = Contact.TaxId ของแถว (+ สาขาถ้ามี) — ตัวเลขล้วน ห้าม fuzzy (หลักการ #3) | จำกัด candidates เหลือผู้จ่ายรายนี้ · ไม่ตรงใครเลย ⇒ "ผู้จ่ายไม่อยู่ในรายชื่อลูกค้า" (รอคน) |
| K2 | DocumentHint (มาจากลิงก์/หน้าเอกสาร/NextAcc) | ใช้เป็นชุดแรก |
| K3 ยอดเดี่ยว | `|ภาษีบนใบ − WHT ค้างของแถว| ≤ ฿1` (ค่าเดิม `OcrService.cs:4496` — ย้ายเป็น `public const` ใน helper) | จับคู่ 1:1 |
| K4 ยอดรวม (ใบเดียวหลายเอกสาร) | หา subset ของแถว Pending ผู้จ่ายเดียวกันที่ Σ = ภาษีบนใบ ±฿1 · ใช้วันที่ชำระใกล้วันที่บนใบก่อน · subset ไม่เกิน 12 แถว | จับคู่ 1:n · **หลาย subset ได้ผลเท่ากัน ⇒ ไม่เลือกเอง** (เสนอให้คนเลือก) |
| K5 บางส่วน | ภาษีบนใบ < WHT ค้างของแถวเดียว (ผ่อนงวด) | allocation บางส่วน · แถว "ได้รับบางส่วน" |
| K6 เกิน | ภาษีบนใบ > Σ ทุกแถวของผู้จ่ายนี้ | ไม่ auto · "ยอดบนใบมากกว่ายอดที่บันทึกว่าถูกหัก — ตรวจการรับชำระ" (มักเป็นการบันทึกรับชำระไม่ใส่ WHT ⇒ ลิงก์ไป "stranded AR" เดิม) |
| K7 อัตรา | ภาษี ÷ เงินได้ ≠ อัตราตาม `ThaiWhtRateTable` ของประเภทเงินได้ | เตือน (`RateWarning`) ไม่บล็อก |
| K8 ปี | วันที่บนใบอยู่นอกรอบบัญชีของแถว | เตือน "ข้ามรอบ — เครดิตใช้ในรอบของวันที่บนใบ" (กติกาเดียวกับ `CreditableInPeriod`) |

ผลลัพธ์: `readonly record struct MatchResult(MatchLevel Level, IReadOnlyList<Allocation> Allocations, string Reason)` · `Level ∈ {Rejected, NoCandidate, Ambiguous, Partial, Exact}` (ไม่มี `null` — G3)

**Auto-confirm (WhtCertIntakeVerdict):** ยืนยันอัตโนมัติได้เมื่อ **ทั้งหมด**: Level=Exact · K0/K1 ผ่าน · มีเลขที่ใบ+วันที่ · (ช่อง ③ = เสมอ) หรือ (ช่อง ①② และค่าตั้ง `WhtIntakeAutoConfirm=true` และ OCR confidence ≥ 0.85 ทุกช่องเงิน/เลขภาษี) · ไม่เข้าเงื่อนไข ⇒ "รอยืนยัน" (ฟอร์มเติมครบ — 1 คลิก)

---

## 4. ป้าย/การเตือน (Tags & alerts)

### 4.1 ตัวตัดสินเดียว — `Helpers/WhtCertIntakeStatus` (pure · ยกจาก `PlatformWhtCertificateStatus`)

```csharp
public enum State {
    NotApplicable,        // ไม่มี WHT ในการชำระใด ๆ ของใบนี้
    NotYetPaid,           // คาดว่าจะถูกหัก (Document.WithholdingTaxAmount>0) แต่ยังไม่รับชำระ — ไม่ใช่การค้าง
    AwaitingCert,         // ถูกหักแล้ว (Pending) ยังไม่มีไฟล์
    Submitted,            // มีไฟล์เข้ากล่องรับ รอตรวจ/รอเลือกคู่
    PartiallyReceived,    // ได้บางใบ/บางยอด
    Received,             // ครบ (รวม e-WHT ที่มีหลักฐาน)
    EWhtAwaitingEvidence, // แจ้งว่า e-WHT แต่ยังไม่มีหลักฐาน
    Rejected,             // ไฟล์ถูกตีกลับ (ไม่ใช่ใบของเรา/อ่านไม่ได้) — รอส่งใหม่
    Claimed,              // ใช้เครดิตในแบบแล้ว
    WrittenOff            // ตัดสูญ (Expired)
}
public static Verdict Of(Facts f, DateTime nowUtc, int overdueDays);   // Verdict(State, Label, Tone, DaysWaiting, IsOverdue, NextAction)
```

- `Facts` = ยอด WHT ที่ถูกหัก (จาก GL/ทะเบียน) · Σ allocation ที่ยืนยันแล้ว · มีไฟล์รอตรวจไหม · เหตุตีกลับ · EvidenceKind · วันที่รับชำระครั้งแรกที่มี WHT · สถานะ Claimed/Expired
- **ผู้เรียกทุกจุด** (list · detail · contact · dashboard · reminder job · year-end report · หน้าอัปโหลดสาธารณะ) เรียก `Of` ตัวเดียว · JS แสดงผล `label/tone` จากเซิร์ฟเวอร์ (หลักการ #5 — ห้ามสำเนาป้ายใน JS) · enum ออกเป็นชื่อ
- `PlatformWhtCertificateStatus` ภายหลังเรียกตัวนี้ (ห้ามมีสองสูตร) — หรือคงไว้เป็น wrapper ที่ map ชื่อ state ของแพลตฟอร์ม
- เกิน: `IsOverdue = State ∈ {AwaitingCert, Rejected, EWhtAwaitingEvidence, PartiallyReceived} && DaysWaiting > overdueDays` (ค่าตั้งต้น 30 วัน — ต่อบริษัท/ผู้ติดต่อ)

### 4.2 ป้ายบนหน้าจอ

| ที่ | ป้าย | ตัวอย่าง |
| --- | --- | --- |
| รายการเอกสาร (`documents.html`) คอลัมน์สถานะ ใต้ป้ายชำระ | ชิปเล็ก + tooltip | `🟡 รอ 50 ทวิ · 12 วัน` · `🔴 50 ทวิ เกินกำหนด · 45 วัน` · `🔵 ส่งแล้ว รอตรวจ` · `🟢 ได้รับ 50 ทวิ` · `⚪ e-WHT (ไม่มีใบ)` · `🟠 ได้รับบางส่วน 1/3` |
| ตัวกรองรายการเอกสาร | ดรอปดาวน์ "50 ทวิ": ทั้งหมด / รอใบ / เกินกำหนด / รอตรวจ / ได้รับแล้ว | |
| หน้าเอกสาร (ฝั่งขาย) | แผง "หนังสือรับรองหัก ณ ที่จ่าย" + ไทม์ไลน์ (§6.6) | |
| ใบเสร็จที่ออกจากใบแจ้งหนี้ | แสดงสถานะของ**เอกสารต้นทาง** (resolver กลาง: แถวทะเบียนผูก `DocumentId` = ใบแจ้งหนี้/ใบกำกับ — ใบเสร็จหาแถวผ่าน `RelatedDocumentId`/`SettlementPaymentId` ตัวเดียวกับ `PostPaymentAutomationService.cs:79`) | |
| หน้าผู้ติดต่อ (`contacts.html`) | การ์ด "50 ทวิ ที่ต้องตามจากลูกค้ารายนี้: n ใบ ฿x · เก่าสุด y วัน" + ปุ่ม "ส่งลิงก์อัปโหลด" / "คัดลอกลิงก์" | |
| แดชบอร์ด (`dashboard.html`) | การ์ด "50 ทวิ ที่ต้องตาม" (§6.7) | |
| กระดิ่ง/LINE กลุ่ม | event ใหม่ `wht_cert.received` (ลูกค้าส่งไฟล์) · `wht_cert.overdue` (สรุปรายสัปดาห์ ไม่ใช่รายใบ — กัน spam) | `NotificationEvents` |
| หน้า ภ.ง.ด.50/51 | แถบ "ยังขาด 50 ทวิ ฿x (n ใบ) — เครดิตไม่ได้จนกว่าจะได้ใบ" + ลิงก์ไปรายการ (มียอดอยู่แล้ว `TaxService.cs:2267` — เพิ่มลิงก์+รายละเอียด) | |

DTO: `DocumentListItem` เพิ่ม `WhtCertState` (ชื่อ enum) · `WhtCertLabel` · `WhtCertTone` · `WhtCertDaysWaiting` — คำนวณด้วย query รวม (GROUP BY DocumentId บน `WhtCreditsReceived` + allocation) ครั้งเดียวต่อหน้า (ห้าม N+1) · `undefined` = ไม่ได้ตรวจ ≠ NotApplicable (หลักการ #5)

### 4.3 จังหวะทวง (reminder cadence)

| วันหลังรับชำระ (ค่าตั้งต้น · ตั้งได้) | การกระทำ | ถึงใคร |
| --- | --- | --- |
| 0 | 🔨 อีเมลขอ 50 ทวิ (รอบ 241) + 📋 ลิงก์อัปโหลด | ลูกค้า |
| 7 | ทวงครั้งที่ 1 (สุภาพ) | ลูกค้า |
| 21 | ทวงครั้งที่ 2 (+ รายการทุกใบที่ค้างของลูกค้านี้ในอีเมลเดียว — รวมรายลูกค้า ไม่ส่งรายใบ) | ลูกค้า |
| 30 (= overdueDays) | ป้ายแดง + สรุปภายใน | ผู้ดูแลบัญชี (กระดิ่ง/LINE) |
| 45 | ทวงครั้งที่ 3 (ระบุว่าบริษัทต้องใช้ยื่น ภ.ง.ด.50) + CC ผู้ติดต่อฝ่ายบัญชีของลูกค้า (ถ้ามี) | ลูกค้า |
| ทุก 30 วันหลังจากนั้น | ทวงรวมรายเดือน (สูงสุด N ครั้ง — ค่าตั้งต้น 6) | ลูกค้า |
| ก่อนครบกำหนดยื่น ภ.ง.ด.51 / ภ.ง.ด.50 30 วัน | สรุป "ยอดเครดิตที่ยังขาดใบ" | ภายใน + ทวงครั้งพิเศษถึงลูกค้าทุกรายที่ค้าง |

- งาน `WhtCertReminderJob` (BackgroundService ใหม่ → **ต้องผ่าน `Helpers/JobLock`** — F4 ข้อ 6) · watermark ต่อบริษัท · หยุดทวงเมื่อ State ∉ {AwaitingCert, Rejected, PartiallyReceived, EWhtAwaitingEvidence} · หยุดถ้าผู้ติดต่อปิด "ไม่ต้องทวง" · หยุดเมื่อครบจำนวนครั้ง
- กันซ้ำ: `EmailQueue.IdempotencyKey = whtreq:{contactId:N}:{yyyyMMdd-bucket}` (รวมรายลูกค้า) · ส่งผ่าน `OnBehalfEmailSender` + `PlatformEmailQuota` (โควตาเดิม — เต็มโควตา ⇒ ล้มดัง ไม่ทิ้งเงียบ)
- ปุ่ม "ทวงตอนนี้" ในหน้าเอกสาร/ผู้ติดต่อ — จำกัด 1 ครั้ง/24 ชม. (`RemindRefusal` แบบเดิม)
- รอบ 241 `RequestWhtCert` = ครั้งที่ 0 ของ cadence นี้ (ไม่ทำซ้ำ: job นับครั้งที่ 0 จากแถว `PostPaymentAutomationRun` ที่มีขั้น `RequestWhtCert` done)

### 4.4 Escalation

- เกินกำหนด → กระดิ่งผู้ดูแลบัญชี (รายสัปดาห์) · เกิน 90 วัน → แสดงในสรุปผู้บริหาร + ป้าย "เสี่ยงเสียเครดิต"
- ไม่ได้รับใบ ณ ปิดรอบ ⇒ ไม่ตัดสูญอัตโนมัติ (หลักการ #3) — แสดงรายงานให้ผู้ทำบัญชีเลือก: ยกไปตามต่อ (ขอคืนได้ภายใน 3 ปี — WHT_CREDIT_PLAN L5) / ตัดสูญ (Expire + JE โอน 11910 → ค่าใช้จ่ายต้องห้าม §65 ตรี — ⚠️ ผู้ทำบัญชียืนยันผัง)

### 4.5 รายงานปลายปี "50 ทวิ ที่ยังขาด"

คอลัมน์: ลูกค้า · เลขผู้เสียภาษี · เอกสาร · วันที่รับชำระ · ยอดเงินได้ · ภาษีถูกหัก · ได้รับแล้ว · ขาด · สถานะ · วันค้าง · ทวงล่าสุด · ทวงกี่ครั้ง · ช่องทางที่ลูกค้าเคยใช้
- กรองตามรอบบัญชี · รวมรายลูกค้า · ส่งออก Excel (MiniExcel) · ปุ่ม "ทวงทุกรายที่ค้าง" (เข้าคิวรวมรายลูกค้า)
- ตัวเลขกระทบ GL: Σ(ขาด) + Σ(ได้รับ ยังไม่ใช้) + Σ(ใช้แล้ว) = ยอด 11910 ของรอบ (ใช้ `WhtCreditService.GlWhtBalanceAsync` เดิม) — ไม่เท่า ⇒ แถบเตือนพร้อมลิงก์ "stranded AR"

---

## 5. ค่าตั้ง (Settings) — บริษัท › ผู้ติดต่อ › ตารางส่ง (ค่าเฉพาะชนะค่าทั่วไปทีละช่อง)

ลำดับชั้นผ่าน **resolver กลางตัวเดียว** `AutopilotSettingsResolver` (แก้รอบ 250 · คำตัดสิน 250-29 — เดิมเขียน ~~`AutoSendSettingsResolver.Effective(company, contact, schedule)`~~) (กฎ #4 A ห้าม inline `??`) — ขยายจาก `PostPaymentAutomationPolicy.Effective` (`:28`) ที่รอบ 241 ทำไว้ (บริษัท › ตาราง) โดยเพิ่มชั้นผู้ติดต่อ

| ค่าตั้ง | บริษัท | ผู้ติดต่อ | ตารางส่ง | ที่เก็บ |
| --- | --- | --- | --- | --- |
| เมทริกซ์ส่งอัตโนมัติ (ชนิดเอกสาร × จังหวะ) | ✅ | ปิดได้ทั้งราย ("ไม่ส่งอัตโนมัติถึงรายนี้") | ✅ (แต่ละกฎ = 1 ช่องเมทริกซ์) | `EmailScheduleRule` เดิม (1 แถว/ช่อง) |
| แนบลิงก์ชำระ + ช่องทาง | 🔨 `PayLinkDefaultEnabled/ChannelsJson` | 📋 `Contact.PayLinkChannelsJson` (เช่น ลูกค้ารายนี้โอนเท่านั้น) | 🔨 `AttachPayLink/PayLinkChannelsJson` | |
| หลังรับชำระ: ส่งใบเสร็จ / รวมงวดย่อย | 🔨 `PostPayEmailReceipt/IncludePartial` | 📋 | 🔨 | |
| ขอ 50 ทวิ หลังรับชำระ | 🔨 `PostPayRequestWhtCert` | 📋 | 🔨 | |
| **แนบลิงก์อัปโหลด 50 ทวิ** | 📋 `WhtUploadLinkEnabled` (ค่าตั้งต้น = เปิดเมื่อเปิดขอ 50 ทวิ) | 📋 | 📋 `AttachWhtUploadLink` | |
| จังหวะทวง (วัน) + จำนวนครั้งสูงสุด | 📋 `WhtReminderDaysJson` = `[7,21,45]`, `WhtReminderMaxCount`=6, `WhtOverdueDays`=30 | 📋 override (เช่น หน่วยงานรัฐ 60 วัน) / ปิดทวง | — | |
| ยืนยันอัตโนมัติเมื่อทุกช่องตรง | 📋 `WhtIntakeAutoConfirm` (ค่าตั้งต้น ปิด) | — | — | |
| ลูกค้าคาดว่าจะหัก WHT | — | 📋 `WhtExpectation ∈ {Auto, Never, Always}` (Auto = ตามการชำระจริง) | — | |
| ลูกค้าใช้ e-WHT เป็นปกติ | — | 📋 `WhtViaEWhtDefault` (ค่าเสนอ) | — | |
| NextAcc↔NextAcc: รับใบอัตโนมัติ / เสนอให้ลูกค้าเชื่อม | 📋 `PartnerWhtReceiveEnabled` · `PartnerInviteOnPayPage` | สถานะลิงก์ต่อราย | — | `TenantPartnerLink` |
| NextAcc↔NextAcc (ฝั่งผู้จ่าย): ส่ง 50 ทวิ ผ่าน NextAcc อัตโนมัติ / ติ๊กเริ่มต้น | 📋 `PartnerWhtSendEnabled` · `PartnerAutoIssueDefault` | ต่อคู่ค้า | — | |
| ภาษาเอกสาร/อีเมล | `CompanySettings.DocumentLanguage` ✅ | `Contact.DocumentLanguage` ✅ | ตามใบ (`Document.DocumentLanguage:97`) | resolver ภาษาเดิม (`ResolveDocumentHeadingAsync`) |
| CC/BCC | 📋 `AutoSendCcEmails` | 📋 `Contact.DocumentCcEmails` (ฝ่ายบัญชีลูกค้า) | ✅ `BccEmails` | |
| อีเมลรับเอกสารของลูกค้า | — | 📋 `Contact.DocumentEmail` (ต่างจากอีเมลติดต่อทั่วไป) | — | |

ทุกค่า: echo กลับใน DTO + hydrate ฟอร์ม (กฎ #4 A "เก็บแล้วต้อง echo") · มี "ผู้อ่านเชิงธุรกิจ" จริง (`tools/settings_reader_check.py`) · ค่าที่เซิร์ฟเวอร์ไม่รับในบางบริบท ⇒ ล็อกช่อง + บอกเหตุผล (ห้าม silent no-op — เช่น "แนบลิงก์ชำระ" ในกฎของใบเสนอราคา = ล็อก "ใบเสนอราคายังรับชำระไม่ได้")

---

## 6. UX/UI

> ทุกหน้า: vanilla HTML + Tailwind CDN · ข้อความไทย (EN ตามภาษาเอกสาร) · ทุกค่าจากผู้ใช้/OCR ผ่าน `Layout.esc` · ปุ่มหลักเด่น 1 ปุ่ม · ป้ายสี + ข้อความ + ไอคอน (ไม่สื่อด้วยสีอย่างเดียว — a11y) · เป้ากด ≥ 44px บนมือถือ · `aria-live="polite"` สำหรับผลอัปโหลด · โฟกัสคีย์บอร์ดเห็นชัด

### 6.1 หน้า "ส่งเอกสารอัตโนมัติ" (เมทริกซ์) — `pages/email-schedule.html` (เพิ่มแท็บ "ภาพรวม")

```
┌ ส่งเอกสารอัตโนมัติ ──────────────────────────────────────────────────────────────────────┐
│ [ภาพรวม] [กฎทั้งหมด] [หลังรับชำระ] [ทวง 50 ทวิ] [คู่ค้า NextAcc]                         │
│                                                                                           │
│ ⚠ ยังไม่ได้ตั้งรับชำระออนไลน์ — "แนบลิงก์ชำระ" จะไม่แสดงในอีเมล  [ไปตั้งค่าการชำระเงิน →] │
│                                                                                           │
│ เอกสาร ↓ / จังหวะ →   อนุมัติ   ก่อนครบกำหนด  เกินกำหนด   หลังรับชำระ   รายเดือน   ลิงก์   │
│ ─────────────────────────────────────────────────────────────────────────────────────────  │
│ ใบเสนอราคา            [✓ ทันที]    —             —            —             —       เซ็น ✓  │
│ ใบแจ้งหนี้/ใบวางบิล    [✓ ทันที]  [✓ 3 วัน]    [✓ 7,14]       —            [✓ วันที่5] ชำระ ✓ │
│ ใบกำกับภาษี            [✓ ทันที]  [✓ 3 วัน]    [✓ 7,14]   [✓ ใบเดิม=ใบเสร็จ]  —       ชำระ ✓ │
│ ใบเสร็จ/ใบเสร็จ-ใบกำกับ [ⓘ ดูหลังรับชำระ] —           —        [✓ ทุกงวด]     —      50ทวิ ✓ │
│ ใบลดหนี้/ใบเพิ่มหนี้    [  ปิด  ]    —            —            —             —        —     │
│ ใบส่งของ              [✓ ทันที]    —             —            —             —       เซ็น ✓  │
│ 50 ทวิ (เราออก)        [✓ เมื่อออก]  —             —        [✓ ถึงผู้ขาย]    —        —     │
│ ใบสั่งซื้อ (ถึงผู้ขาย)  [  ปิด  ]    —             —            —             —        —     │
│ สลิปเงินเดือน           —            —             —        [✓ เมื่อจ่าย]    —        —     │
│ คำขอ 50 ทวิ (ลูกค้า)    —            —             —        [✓ ทันที]   ทวง 7/21/45 อัปโหลด ✓│
│                                                                                           │
│ แต่ละช่อง: คลิก → แผงด้านขวา (ส่งเมื่อ · เวลา · หัวเรื่อง/เนื้อหา · BCC · แนบ PDF/XML ·   │
│           ลิงก์ชำระ+ช่องทาง · ลิงก์อัปโหลด 50 ทวิ · ทดสอบส่งถึงตัวเอง)                   │
│ "—" = จังหวะนี้ใช้กับเอกสารชนิดนี้ไม่ได้ (tooltip บอกเหตุ)                                 │
│ [ⓘ ใบเสร็จที่ระบบออกอัตโนมัติจากการชำระ จะไม่ผ่านกฎ "อนุมัติ" — ตั้งที่คอลัมน์หลังรับชำระ] │
└───────────────────────────────────────────────────────────────────────────────────────────┘
```

- ช่อง "—" มาจากเซิร์ฟเวอร์ (`GET auto-send/matrix` คืนช่องที่ใช้ได้ + เหตุ) — JS ไม่มีตารางกติกาเอง
- แท็บ "ทวง 50 ทวิ": จังหวะวัน (ชิปแก้ได้ 7 · 21 · 45 + เพิ่ม) · จำนวนครั้งสูงสุด · วันเกินกำหนด · ยืนยันอัตโนมัติ (สวิตช์ + คำอธิบายความเสี่ยง) · ตัวอย่างอีเมล (พรีวิวจริงจากเซิร์ฟเวอร์)
- แท็บ "คู่ค้า NextAcc": สวิตช์รับ/ส่ง + รายการคู่ค้าที่เชื่อม/รอยอมรับ (§6.9)

### 6.2 แผงตั้งค่าในตารางส่ง (schedule panel — ต่อจากของรอบ 241)

```
┌ กฎ: ใบแจ้งหนี้ — เกินกำหนด 7 วัน ─────────────────────── [บันทึก] ┐
│ ส่งเมื่อ  [เกินกำหนด ▾] [ 7 ] วัน  ทวงซ้ำทุก [ 7 ] วัน  เวลา [09:00▾] │
│ ถึง      ● อีเมลรับเอกสารของลูกค้า (ถ้าไม่มี ใช้อีเมลติดต่อ)          │
│ CC       [ac@customer.co.th          ] (ค่าบริษัท + ผู้ติดต่อ รวมกัน)  │
│ แนบ      ☑ PDF  ☐ XML (e-Tax)                                        │
│ ── ลิงก์ชำระเงิน ──────────────────────────────────────────────────── │
│ ○ ตามค่าบริษัท (เปิด)  ● เปิด  ○ ปิด                                  │
│ ช่องทาง  ☑ บัตร  ☑ PromptPay  ☑ โอน+แนบสลิป   (ที่บริษัทเปิดอยู่เท่านั้น)│
│ ── หลังลูกค้าชำระ ──────────────────────────────────────────────────── │
│ ใบเสร็จ   ○ ตามค่าบริษัท  ● ส่ง  ○ ไม่ส่ง     งวดย่อย ☑ ส่งทุกงวด    │
│ 50 ทวิ   ○ ตามค่าบริษัท  ● ขอ + แนบลิงก์อัปโหลด  ○ ไม่ขอ              │
│ ตัวอย่างอีเมล ▸ (พรีวิวจากเซิร์ฟเวอร์ — ใบล่าสุดที่เข้ากฎ)              │
└──────────────────────────────────────────────────────────────────────┘
```

### 6.3 หน้าชำระ (ฝั่งลูกค้า) — `pay-document.html` 🔨 + ส่วน WHT 📋

```
┌──────────────── (มือถือ 360px) ───────────────┐
│ [โลโก้] บริษัท เอ จำกัด                          │
│ ใบแจ้งหนี้ INV-2026-0123 · ครบกำหนด 15 พ.ย. 69  │
│                                                  │
│ ยอดตามเอกสาร                   10,700.00 บาท    │
│ ── หัก ณ ที่จ่าย ─────────────────────────────── │
│ ท่านเป็นผู้จ่ายแบบใด?                            │
│ ( ) นิติบุคคล — หัก 3% ค่าบริการ   −300.00      │
│ ( ) บุคคลธรรมดา / ไม่หัก              0.00      │
│ ( ) หักผ่าน e-Withholding ของธนาคาร  (ⓘ)        │
│ ยอดที่ต้องชำระ                  10,400.00 บาท    │
│ ⓘ ฐานภาษี 10,000.00 (ก่อน VAT) · ประเภท ม.40(2)  │
│                                                  │
│ ┌ ✨ บริษัทของคุณใช้ NextAcc? ────────────────┐ │
│ │ ชำระผ่านบัญชี NextAcc ของบริษัทคุณ          │ │
│ │ ระบบออกหนังสือรับรอง 50 ทวิ และส่งให้ผู้ขาย  │ │
│ │ อัตโนมัติ — ไม่ต้องส่งเอกสารเอง               │ │
│ │ [ เข้าสู่ระบบ NextAcc เพื่อชำระ ]            │ │
│ └──────────────────────────────────────────────┘ │
│                                                  │
│ ช่องทางชำระ: [บัตร] [PromptPay] [โอน+แนบสลิป]     │
│ [        ชำระ 10,400.00 บาท        ]            │
│ ข้อมูลของท่านใช้เพื่อการชำระและออกเอกสารนี้เท่านั้น │
│ (นโยบายความเป็นส่วนตัว)                           │
└──────────────────────────────────────────────────┘
```

กติกา:
- ตัวเลือก "ผู้จ่ายแบบใด" แสดงเฉพาะใบที่ `Document.WithholdingTaxAmount > 0` หรือบริษัทเปิด "ให้ลูกค้าเลือกหัก" · ค่าเริ่ม = ตามเอกสาร · **ยอดทุกตัวคิดที่เซิร์ฟเวอร์** (`DocumentPayLinkPolicy.AmountsFor` + ตัวเลือก) — หน้าเว็บส่งแค่ตัวเลือก (แบบ `PlatformWhtSuggestion` "ส่งแค่ true/false")
- เลือก "ไม่หัก" ⇒ ยอดเต็ม (ไม่มี 50 ทวิ ต้องตาม · State=NotApplicable) · เลือก "e-WHT" ⇒ ยอด Net · หลังรับเงิน แถวทะเบียน EvidenceKind=EWithholding (รอหลักฐาน)
- กล่อง NextAcc แสดงตาม §3.5.3 ข้อ 1 (ไม่เปิดเผยการเป็นผู้ใช้)
- ภาษาตามเอกสาร (EN: "Withholding tax you will withhold", "Pay via your company's NextAcc account")

### 6.4 หน้าชำระสำเร็จ + ลิงก์อัปโหลด

```
┌────────────────────────────────────────────────┐
│ ✅ ชำระเงินสำเร็จ 10,400.00 บาท                  │
│ ใบเสร็จรับเงิน/ใบกำกับภาษี จะส่งถึง a***@cust.co.th │
│ ภายในไม่กี่นาที                                   │
│ ───────────────────────────────────────────────  │
│ 📄 ท่านหักภาษี ณ ที่จ่ายไว้ 300.00 บาท            │
│ กรุณาส่งหนังสือรับรอง (50 ทวิ) ให้เรา               │
│ ออกในนาม: บริษัท เอ จำกัด                         │
│ เลขผู้เสียภาษี 0105556000000 · สำนักงานใหญ่        │
│ [ 📤 อัปโหลดหนังสือรับรองตอนนี้ ]                  │
│ ยังไม่มีใบ? ไม่เป็นไร — เราส่งลิงก์ไว้ในอีเมลแล้ว     │
└────────────────────────────────────────────────┘
```

(เลขผู้เสียภาษีของ**เรา**แสดงได้ — เป็นข้อมูลที่ต้องพิมพ์บนใบกำกับอยู่แล้ว · อีเมลลูกค้าแสดงแบบ mask)

### 6.5 หน้าอัปโหลด 50 ทวิ สาธารณะ — `wwwroot/wht-upload.html` (mobile-first)

```
┌──────────────── 360px ─────────────────┐
│ [โลโก้] บริษัท เอ จำกัด                  │
│ ส่งหนังสือรับรองการหักภาษี ณ ที่จ่าย      │
│                                          │
│ โปรดออกใบในนาม                            │
│ บริษัท เอ จำกัด  [คัดลอก]                 │
│ 0105556000000 · สำนักงานใหญ่ [คัดลอก]     │
│ 99/1 ถ.สุขุมวิท ... [คัดลอก]              │
│                                          │
│ เอกสารที่รอหนังสือรับรอง (3)               │
│ ☑ INV-0123 · ชำระ 3 พ.ย. 69 · ภาษี 300.00 │
│ ☑ INV-0130 · ชำระ 3 พ.ย. 69 · ภาษี 150.00 │
│ ☐ INV-0098 · ชำระ 2 ต.ค. 69 · ภาษี 90.00  │
│   (ติ๊กใบที่ไฟล์นี้ครอบ — ไม่แน่ใจ ข้ามได้) │
│                                          │
│ ┌──────────────────────────────────────┐ │
│ │   📷 ถ่ายรูป   หรือ   📁 เลือกไฟล์     │ │
│ │   PDF / JPG / PNG · ไม่เกิน 10 MB      │ │
│ └──────────────────────────────────────┘ │
│ ▸ ใบ_50ทวิ_พย.pdf  1.2 MB  ✓ พร้อมส่ง     │
│                                          │
│ ชื่อผู้ส่ง (ไม่บังคับ) [            ]      │
│ [          ส่งหนังสือรับรอง          ]    │
│                                          │
│ ไฟล์จะใช้เพื่อบันทึกภาษีของบริษัท เอ เท่านั้น │
│ และเก็บตามกฎหมาย 5 ปี · นโยบายความเป็นส่วนตัว│
└──────────────────────────────────────────┘
หลังส่ง (aria-live):
│ ✅ ได้รับแล้ว 1 ไฟล์ — กำลังตรวจ                 │
│ อ่านได้: เลขที่ 2569/0456 · ภาษี 450.00 บาท      │
│ ครอบ INV-0123, INV-0130 (ตรงยอด)                 │
│ [ส่งไฟล์เพิ่ม]                                   │
```

- ผลอ่านที่สะท้อนกลับ = เฉพาะสิ่งที่อยู่ในไฟล์ที่ลูกค้าเพิ่งส่ง (ไม่รั่วข้อมูลอื่น) · OCR ทำ async — ถ้ายังไม่เสร็จ แสดง "ได้รับแล้ว — จะตรวจภายหลัง"
- K0 ไม่ผ่าน (ใบออกในนามคนอื่น) ⇒ บอกทันที "ใบนี้ออกในนาม บริษัท บี — โปรดออกในนาม บริษัท เอ" (ช่วยลูกค้าแก้ก่อนผู้ขายต้องตีกลับ)
- ลิงก์หมดอายุ/เพิกถอน ⇒ หน้าบอกเหตุ + อีเมลติดต่อผู้ขาย (ไม่ใช่ 404 เปล่า)
- ทุกเอกสารรับแล้ว ⇒ "ได้รับหนังสือรับรองครบแล้ว ขอบคุณค่ะ"

### 6.6 หน้าเอกสาร (ผู้ขาย) — แผง "หนังสือรับรองหัก ณ ที่จ่าย" + ไทม์ไลน์

```
┌ หนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) ───────── 🟡 รอใบ · 12 วัน ┐
│ ถูกหัก 300.00 · ได้รับ 0.00 · ขาด 300.00 · ประเภท ม.40(2) 3%    │
│                                                                   │
│ ● 3 พ.ย. 69 10:12  รับชำระ 10,400 (หัก 300) — ลิงก์ชำระออนไลน์    │
│ ● 3 พ.ย. 69 10:13  ออกใบเสร็จ RC-0456 + ส่งอีเมลถึง a***@…       │
│ ● 3 พ.ย. 69 10:13  ขอ 50 ทวิ + ลิงก์อัปโหลด                      │
│ ● 10 พ.ย. 69 09:00 ทวงครั้งที่ 1                                  │
│ ○ 24 พ.ย. 69       ทวงครั้งที่ 2 (กำหนดไว้)                        │
│                                                                   │
│ [📤 อัปโหลดใบ] [🔗 คัดลอกลิงก์อัปโหลด] [✉ ทวงตอนนี้]              │
│ [ลูกค้าหักผ่าน e-WHT…] [หยุดทวงใบนี้…] [ตัดสูญ…]                  │
└───────────────────────────────────────────────────────────────────┘
เมื่อมีไฟล์รอตรวจ:
┌ 🔵 ลูกค้าส่งใบแล้ว — รอยืนยัน ───────────────────────────────────┐
│ [ภาพตัวอย่างใบ]   เลขที่ [2569/0456 ]  วันที่ [03/11/2569]        │
│                   ผู้จ่าย บจก.ลูกค้า 0105…(ตรง ✓)                │
│                   ผู้ถูกหัก บจก.เอ 0105…(เรา ✓)                  │
│                   เงินได้ [10,000.00] อัตรา 3% ภาษี [300.00] (ตรง ✓)│
│                   ครอบ: INV-0123 (300.00)                          │
│ [   ✔ ยืนยัน — บันทึกเป็นได้รับแล้ว   ]   [ตีกลับพร้อมเหตุผล…]    │
└───────────────────────────────────────────────────────────────────┘
```

- ปุ่มยืนยันเด่น ช่องเติมครบ (กฎ #3) · ช่อง confidence < 0.85 พื้นเหลือง + tooltip ที่มา (OCR/ข้อมูลระบบ NextAcc)
- "ตีกลับ" ⇒ เหตุผล (เลือกจากรายการ + ข้อความ) ⇒ อีเมลถึงลูกค้าพร้อมลิงก์เดิม (State=Rejected)
- ปุ่มที่ทำไม่ได้แสดงแบบล็อกพร้อมเหตุ (เช่น "ตัดสูญ" ล็อกเมื่อ Claimed)

### 6.7 การ์ดแดชบอร์ด "50 ทวิ ที่ต้องตาม"

```
┌ 50 ทวิ ที่ต้องตามจากลูกค้า ──────────────── [ดูทั้งหมด →] ┐
│ ฿ 18,450.00 ยังเครดิตไม่ได้  · 23 ใบ · 9 ลูกค้า              │
│ 🔴 เกินกำหนด 7 ใบ ฿6,200    🔵 รอยืนยัน 3 ใบ                  │
│ ลูกค้าที่ค้างมากสุด: บจก.ลูกค้า ฿4,500 (5 ใบ · 62 วัน) [ทวง]  │
│ ⏰ ครบกำหนดยื่น ภ.ง.ด.50 อีก 41 วัน                          │
└──────────────────────────────────────────────────────────────┘
```
(เห็นเฉพาะผู้มีสิทธิ์โมดูลภาษี/บัญชี — ตามสิทธิ์หน้า wht-credit เดิม)

### 6.8 รายการเอกสาร — ชิปและตัวกรอง

```
เลขที่       ลูกค้า          ยอด        สถานะ
INV-0123    บจก.ลูกค้า     10,700.00   [ชำระแล้ว] [🟡 รอ 50 ทวิ · 12 วัน]
INV-0098    บจก.ซี          3,210.00   [ชำระแล้ว] [🔴 50 ทวิ เกินกำหนด · 45 วัน]
RC-0450     บจก.ดี          5,350.00   [ชำระแล้ว] [🟢 ได้รับ 50 ทวิ]
INV-0101    หจก.อี          2,140.00   [ชำระแล้ว] [⚪ e-WHT · รอหลักฐาน]
ตัวกรอง: [50 ทวิ: ทั้งหมด ▾]  (รอใบ · เกินกำหนด · รอยืนยัน · ได้รับแล้ว · e-WHT)
```

### 6.9 ฝั่งผู้จ่าย (ลูกค้าที่ใช้ NextAcc) — ยืนยันการออก 50 ทวิ อัตโนมัติ

ในบัญชี P หน้าใบแจ้งหนี้ซื้อ (จาก §3.5.3) / หน้าจ่ายเงิน:

```
┌ ชำระใบแจ้งหนี้จาก บริษัท เอ จำกัด (เชื่อมผ่าน NextAcc ✓) ───────────┐
│ INV-0123 · 10,700.00 · ครบกำหนด 15 พ.ย. 69                           │
│ หัก ณ ที่จ่าย  ม.40(2) ค่าบริการ 3% ของ 10,000.00 = 300.00 (ระบบคำนวณ)│
│ ยอดจ่าย 10,400.00   จาก [บัญชีกสิกร xxx-1234 ▾]                     │
│                                                                       │
│ ☑ ออกหนังสือรับรอง 50 ทวิ อัตโนมัติเมื่อชำระ และส่งให้ บริษัท เอ ทาง NextAcc│
│   ออกในนาม: บริษัท พี จำกัด (ผู้จ่าย) · เลขที่ใบตามลำดับของบริษัทคุณ ·    │
│   ยื่นใน ภ.ง.ด.53 เดือน พ.ย. 2569 ของบริษัทคุณ · ลงนามโดย: [คุณสมชาย ▾] │
│   (ผู้มีอำนาจลงนามที่ตั้งไว้ในข้อมูลบริษัท)                              │
│ ⓘ ข้อมูลที่ส่งให้ผู้ขาย = ข้อมูลบนหนังสือรับรองเท่านั้น                   │
│ [          ยืนยันและชำระ          ]                                    │
└───────────────────────────────────────────────────────────────────────┘
หลังสำเร็จ:  ✅ ชำระแล้ว · ✅ ออก 50 ทวิ เลขที่ 2569/0456 · ✅ ส่งถึง บริษัท เอ (รับแล้ว 10:14)
```

- สถานะ "ส่งถึง … (รับแล้ว)" ตั้งเมื่อ intake ฝั่ง S ตอบสำเร็จจริง (หลักการ #3) — ระหว่างรอ = "กำลังส่ง"
- หน้า "คู่ค้าที่ใช้ NextAcc" (บัญชี P/S): รายการคู่ · สถานะ (รอยอมรับ/ใช้งาน/เพิกถอน) · ขอบเขต · ผู้ยินยอม+เวลา · [เพิกถอน]

### 6.10 ข้อความ (copy) หลัก — ไทย/อังกฤษ

| คีย์ | ไทย | EN |
| --- | --- | --- |
| chip.awaiting | รอ 50 ทวิ · {n} วัน | Awaiting WHT cert · {n}d |
| chip.overdue | 50 ทวิ เกินกำหนด · {n} วัน | WHT cert overdue · {n}d |
| chip.submitted | ส่งแล้ว รอตรวจ | Submitted · pending review |
| chip.partial | ได้รับบางส่วน {a}/{b} | Partially received {a}/{b} |
| chip.received | ได้รับ 50 ทวิ | WHT cert received |
| chip.ewht | e-WHT (ไม่มีใบ) | e-WHT (no certificate) |
| email.request.subject | ขอหนังสือรับรองการหักภาษี ณ ที่จ่าย — {DocNumber} | Withholding tax certificate request — {DocNumber} |
| upload.issueTo | โปรดออกใบในนาม | Please issue the certificate to |

ข้อความเอกสาร/อีเมลผ่าน `DocumentLabels`/ตัวสร้างข้อความกลาง (ห้าม literal กระจาย — กฎ #4 A) · ป้ายสถานะมาจาก `WhtCertIntakeStatus.Label`

---

## 7. โมเดลข้อมูล + API

### 7.1 ตาราง/คอลัมน์ใหม่ (ผ่าน `DatabaseMigrationHelper` `ADD COLUMN/CREATE TABLE IF NOT EXISTS` — ห้าม EF Migrations)

> ⚠️ (แก้รอบ 250 WP-05 · คำตัดสิน 250-30: ต่อยอด `TradingPartnership` ที่มีอยู่ — ดู AUTOPILOT_BILLING_DESIGN §6 รอบ 251–253 · ไม่สร้างตารางสำเนา) — `TenantPartnerLink` ข้างล่าง**ยกเลิก** (เก็บไว้เป็นประวัติ) · คอลัมน์ที่ต้องการให้เติมบน `TradingPartnership`/ขอบเขตตาม AUTOPILOT §6.2

```
WhtCertIntake (TenantEntity)                 -- 1 แถว = 1 ไฟล์/ใบที่เข้ามา (ทุกช่องทาง)
  Id, CompanyId
  Channel            varchar  -- UploadLink | Portal | Manual | Scan | NextAccLink | EWhtEvidence | Line | InboundEmail
  Status             varchar  -- Received(เข้ากล่อง) | Matched | AwaitingConfirm | Confirmed | Rejected | Superseded
  EvidenceKind       varchar  -- PaperCertificate | ElectronicCertificate(NextAcc) | EWithholding
  FileAttachmentId   uuid?    -- FileAttachment (EntityType="WhtCertIntake") · storage abstraction
  FileSha256         varchar(64)   -- UNIQUE(CompanyId, FileSha256) WHERE NOT IsDeleted
  ExtractedJson      jsonb    -- ค่าที่อ่าน/รับ + confidence ต่อช่อง + ที่มา (OCR/Structured)
  CertificateNumber, CertificateDate, PayerTaxId, PayerBranchCode, PayerName, PayeeTaxId,
  IncomeTypeCode, IncomeAmount, WhtAmount, WhtRate, PayerFormType
  PayerContactId     uuid?
  UploadLinkId       uuid?    -- ช่อง ①
  SourceCompanyId    uuid?    -- ช่อง ③ (บริษัทผู้จ่าย) ; SourceCertId uuid? ; SourceVersion int
  OcrScanResultId    uuid?    -- เชื่อม feedback ของ OCR (round-trip learning)
  MatchLevel         varchar ; MatchReason text
  SubmittedByName    varchar? ; SubmittedIp varchar? (ลบ/ทำให้นิรนามหลัง 1 ปี — PDPA)
  ConfirmedBy/At, RejectedReason, RejectedBy/At
  (Created/Updated/IsDeleted ตาม TenantEntity)

WhtCertAllocation (TenantEntity)              -- ใบ ↔ แถวเครดิต (n:m)
  Id, CompanyId, IntakeId, WhtCreditId, Amount numeric(18,2), Status(Proposed|Confirmed|Reversed)
  UNIQUE(CompanyId, IntakeId, WhtCreditId)

WhtCreditReceived (เพิ่มคอลัมน์)
  EvidenceKind       varchar  NOT NULL DEFAULT 'PaperCertificate'
  ReceivedAmount     numeric(18,2) DEFAULT 0     -- Σ allocation ที่ยืนยัน (denormalized · เขียนโดย intake service เท่านั้น)
  FirstWithheldAt    timestamptz?                 -- วันที่รับชำระครั้งแรกที่มี WHT (ฐานนับวันค้าง)
  ReminderCount int DEFAULT 0 ; LastRemindedAt timestamptz? ; RemindersPaused bool DEFAULT false ; PausedReason text?
  -- Status เดิมคงความหมาย: Pending (ReceivedAmount < WhtAmount) / Received (ครบ) — ตั้งโดย intake service

WhtCertUploadLink (TenantEntity)
  Id, CompanyId, ContactId, TokenHash varchar(64) UNIQUE, ExpiresAt, RevokedAt/By/Reason,
  CreatedFrom varchar (ReceiptEmail|Reminder|PaySuccess|Manual), ViewCount, LastViewedAt, LastUploadAt

TenantPartnerLink (ตารางระดับแพลตฟอร์ม — ไม่ใช่ TenantEntity ปกติ; อ่าน/เขียนผ่าน service ระบบเท่านั้น)
  Id, SellerCompanyId, BuyerCompanyId, SellerContactIdInBuyer, BuyerContactIdInSeller,
  Scope varchar ('WhtCert'), Status (Requested|Active|Revoked),
  RequestedBy(Company/User/At), SellerConsent(UserId, At, TextVersion, TextSha256), BuyerConsent(...),
  BuyerAutoIssueDefault bool, RevokedByCompanyId/UserId/At/Reason
  UNIQUE(SellerCompanyId, BuyerCompanyId, Scope) WHERE Status <> 'Revoked'

WhtCertExchangeOutbox (ฝั่งผู้จ่าย P)
  Id, CompanyId(P), CertId, Version, EventType (Issued|Voided|Replaced), TargetCompanyId(S),
  Status (Pending|Sent|Failed), Attempts, NextAttemptAt, PayloadSha256, ResultIntakeId uuid?, Error
  UNIQUE(CompanyId, CertId, Version, EventType)

CompanySettings (เพิ่ม)  WhtUploadLinkEnabled, WhtReminderDaysJson, WhtReminderMaxCount, WhtOverdueDays,
                         WhtIntakeAutoConfirm, PartnerWhtReceiveEnabled, PartnerInviteOnPayPage,
                         PartnerWhtSendEnabled, PartnerAutoIssueDefault, AutoSendCcEmails
Contact (เพิ่ม)          DocumentEmail, DocumentCcEmails, WhtExpectation, WhtViaEWhtDefault,
                         WhtReminderOverrideDaysJson, WhtRemindersDisabled, PayLinkChannelsJson, AutoSendDisabled
EmailScheduleRule (เพิ่ม) AttachWhtUploadLink bool? ; Trigger ใหม่ "WhtCertReminder" (OffsetDays/RepeatEveryDays)
```

**ผลต่อผู้อ่านเดิม (ต้องแก้พร้อมกัน — หลักการ #1):**
- `WhtCreditService.CreditableAmountAsync` / `CreditableInPeriod` / `TaxService` ภ.ง.ด.50 (`:2260-2270`): เครดิต = `Received/Claimed` เต็มแถว **บวก** `Pending` ที่มี `ReceivedAmount>0` เฉพาะส่วนที่ได้รับ ⇒ เขียนตัวคำนวณเดียว `WhtCreditableAmount.Of(row)` แล้วให้ทุกผู้อ่านเรียก (ห้ามสองสูตร) · เทสต์: ผ่อน 3 งวด ได้ใบ 1 ใบ ⇒ เครดิต = ใบเดียว
- `DocumentService.SyncWhtCreditReceivedAsync`: ยังเป็นผู้ตั้ง `WhtAmount` จาก GL · **ห้ามแตะ `ReceivedAmount`/allocation** · ถ้า GL ลดจน `WhtAmount < ReceivedAmount` ⇒ ไม่ลบแถว · ติดธง "ยอดใบเกินยอดที่ถูกหัก" (G4 เก็บผู้แพ้) · เงื่อนไข "ข้ามแถวที่มี CertificateNumber" (`:176`) เปลี่ยนเป็น "ข้ามแถวที่มี allocation Confirmed" (ครอบคลุมกว่า)
- `OcrService.EnsureWhtCreditFromCertAsync`: เปลี่ยนเป็นเรียก `SubmitAsync(Channel=Scan)` — ลบเส้นสร้างแถวเอง (`:4523`) ⇒ ไม่มีแถวซ้ำ · migration: แถวเก่าที่เกิดจากสแกน (มี CertificateNumber และไม่มี DocumentId) ⇒ สร้าง `WhtCertIntake` ย้อนหลัง 1:1 (Confirmed) เพื่อให้ไทม์ไลน์/ป้ายเห็น
- `PostPaymentAutomationService` (`:241-243`) เงื่อนไข `whtPending` ⇒ ใช้ `WhtCertIntakeStatus.Of` (ตัวเดียว)
- `PlatformWhtCertificateService` ⇒ ไฟล์ที่ผู้เช่าส่ง = `SubmitAsync(Channel=Portal, companyId=platform)` (ภายหลัง — ไม่บังคับในเฟสแรก แต่ห้ามมีสูตรสถานะที่สอง)

### 7.2 Endpoints

| Method | Path | สิทธิ์ | หมายเหตุ |
| --- | --- | --- | --- |
| GET | `api/companies/{cid}/auto-send/matrix` | Accounting.View | ช่องที่ใช้ได้ + สถานะกฎ + เหตุ "—" |
| PUT | `api/companies/{cid}/auto-send/settings` | Organization.Manage | ขยาย DTO ของรอบ 241 (`UpdateDocumentAutoSendSettings`) |
| GET | `api/companies/{cid}/wht-certs/inbox?status=` | Tax.File | กล่องรับ |
| POST | `api/companies/{cid}/wht-certs/inbox` (multipart) | Tax.File | ช่อง ② · `documentId?` |
| POST | `…/inbox/{id}/confirm` | Tax.File | body: ช่องที่แก้ + allocations · บันทึก feedback OCR |
| POST | `…/inbox/{id}/reject` | Tax.File | reason (บังคับ) → อีเมลลูกค้า |
| POST | `api/companies/{cid}/wht-credits/{id}/ewht` | Tax.File | ตั้ง EvidenceKind=EWithholding (+ ไฟล์หลักฐาน) |
| POST | `…/wht-credits/{id}/remind` · `…/pause-reminders` | Tax.File | 1 ครั้ง/24 ชม. |
| POST | `api/companies/{cid}/contacts/{contactId}/wht-upload-link` | Accounting.View | ออก/ใช้ลิงก์เดิม (คืน URL ครั้งเดียว — เก็บแค่ hash ⇒ "คัดลอกลิงก์" ต้องออกใหม่หรือเก็บ token แบบเข้ารหัส — **ตัดสินใจ: เก็บ hash + ออก token ใหม่ทุกครั้งที่กดคัดลอก (ลิงก์เก่ายังใช้ได้จนหมดอายุ)**) |
| GET | `api/companies/{cid}/wht-credits/missing-report?fiscalYear=` | Tax.File | รายงาน §4.5 (+ `.xlsx`) |
| GET | `api/public/wht-upload/{companyId}/{token}` | AllowAnonymous + rate limit | ข้อมูลหน้าอัปโหลด |
| POST | `api/public/wht-upload/{companyId}/{token}` (multipart) | AllowAnonymous + rate limit + size limit | → `SubmitAsync(Channel=UploadLink)` |
| POST | `api/portal/{companyId}/wht-certs` (multipart) | portal token | → `SubmitAsync(Channel=Portal)` |
| GET/POST | `api/companies/{cid}/partner-links[/…/accept|revoke]` | Tax.File (+ จ่าย สำหรับ buyer) | §3.5.1 |
| — (ภายใน) | `TenantWhtCertExchange.DeliverAsync` | service ระบบ | ไม่มี HTTP สาธารณะ (ผู้เช่าเดียวกันบนแพลตฟอร์มเดียว — เรียกตรงผ่าน DI) |

ทุก endpoint: `CompanyId == companyId` ในทุก query (raw SQL ใส่เอง) · business error = `BusinessRuleException(RuleCode, LegalReference)` · RuleCode ชุดใหม่: `WHT-INTAKE-K0` (ไม่ใช่ใบของเรา · RD ม.50 ทวิ) · `WHT-INTAKE-DUP` · `WHT-CREDIT-L1` (เครดิตเฉพาะมีใบ) · `WHT-EWHT-EVIDENCE` · `PARTNER-CONSENT`

### 7.3 งาน/อีเวนต์

| งาน | ทำอะไร | กันซ้ำ |
| --- | --- | --- |
| `WhtCertIntakeOcrWorker` | OCR ไฟล์ใน intake (async) → Match → Verdict | แถวต่อ intake · SKIP LOCKED |
| `WhtCertReminderJob` | §4.3 | `JobLock` + watermark ต่อบริษัท + IdempotencyKey รายลูกค้า/รอบ |
| `WhtCertExchangeWorker` (ฝั่ง P) | ส่ง outbox → S | unique outbox · SKIP LOCKED · retry 4 ครั้ง (แบบ `PostPaymentAutomationPolicy.RetryDelay`) |
| Hook `PostPaymentAutomationService.EnqueueAsync` | ทุกทางรับชำระ (G-6) | unique (CompanyId, PaymentId) เดิม |
| Hook `OnWhtCertIssuedAsync` / `VoidAsync` (ฝั่ง P) | เขียน outbox | unique (CertId, Version, EventType) |
| Notification | `wht_cert.submitted` · `wht_cert.overdue_digest` · `wht_cert.partner_delivery_failed` | ต่อแถว/ต่อสัปดาห์ |

### 7.4 Idempotency · tenant isolation · audit · PDPA · retention

- **Idempotency:** ไฟล์ซ้ำ (hash) · outbox unique · reminder key · confirm ซ้ำ ⇒ no-op พร้อมข้อความ "ยืนยันไปแล้วโดย … เมื่อ …"
- **Tenant isolation:** หน้าอัปโหลดสาธารณะ resolve `companyId` จาก URL + TokenHash ตรงคู่ (token ของบริษัทอื่นใช้ไม่ได้) · `TenantPartnerLink` อ่านได้เฉพาะสองบริษัทในคู่ · ไฟล์ของ intake อยู่ใน `CompanyId` ผู้รับเท่านั้น (ฝั่ง P เก็บสำเนาของตัวเองในทะเบียน 50 ทวิ ที่ออก)
- **Audit:** ทุก confirm/reject/ewht/ตัดสูญ/เชื่อม-เพิกถอนคู่ค้า ⇒ `AddChainedAuditLog` (`RuleCode` + `LegalReference`) · ความยินยอมเก็บ TextVersion + SHA-256 ของข้อความที่แสดงตอนกด
- **PDPA:** ใบ 50 ทวิ ที่ผู้จ่าย/ผู้รับเป็นบุคคลธรรมดามีเลขประจำตัวประชาชน = ข้อมูลส่วนบุคคล (ไม่ใช่ sensitive ม.26) ⇒ แสดงแบบ mask ตามกฎ J (`1-XXXX-XXXXX-XX-3`) ยกเว้นผู้มี `Pii.View` · หน้าอัปโหลดมีประกาศวัตถุประสงค์ (ม.23) · ฐานกฎหมาย = ปฏิบัติตามกฎหมาย (ม.24(6)) สำหรับการเก็บใบภาษี · การแลกข้ามผู้เช่า = ฐานสัญญา/ยินยอมของคู่ (นิติบุคคลเป็นส่วนใหญ่) + บันทึกใน RoPA (`ProcessingActivity`: "รับ-ส่งหนังสือรับรองหัก ณ ที่จ่ายระหว่างคู่ค้าที่ใช้ NextAcc") · IP/ชื่อผู้ส่งบนหน้าอัปโหลด ⇒ เก็บ 1 ปีแล้วทำให้นิรนาม · DSR erase ⇒ ไฟล์ใบภาษีอยู่ใต้ legal hold (MAX retention)
- **Retention:** ไฟล์ intake + แถวเครดิต = `RetentionUntil = max(วันสิ้นรอบบัญชี, วันยื่น ภ.ง.ด.50 ที่ใช้เครดิต) + 5 ปี` (พ.ร.บ.การบัญชี — repo อ้าง ม.10; ⚠️ ตัวบทระยะเก็บเอกสารคือ ม.14 — ให้ผู้ทำบัญชียืนยันแล้วแก้ CLAUDE.md ตามหลักการ #10) + กรณีขอคืนภาษี (3 ปี) ⇒ MAX · ห้ามลบจริงในช่วงนี้ (soft delete + legal_hold)

---

## 8. ตรวจทางกฎหมาย (Legal checks)

| ข้อ | หลัก | ผลต่อระบบ | สถานะ |
| --- | --- | --- | --- |
| L-1 ผู้มีหน้าที่หัก | ป.รัษฎากร ม.3 เตรส + คำสั่ง ท.ป.4/2528: ผู้จ่ายที่เป็นบริษัท/ห้างหุ้นส่วนนิติบุคคล/นิติบุคคลอื่น (รวมหน่วยงานรัฐ) จ่ายค่าบริการ ฯลฯ ≥ 1,000 บาท (รวมทั้งสัญญา) · บุคคลธรรมดาทั่วไปไม่มีหน้าที่ (ยกเว้นกรณีเฉพาะ) | ฝั่งขาย: คาด WHT จากชนิดลูกค้า **เป็นค่าเสนอ** — ตัวจริงคือการชำระที่บันทึก WHT · ฝั่งซื้อ: ตัวตัดสินเดิม | ✅ ตารางอยู่ CLAUDE.md §E / `ThaiWhtRateTable` |
| L-2 ผู้ออก 50 ทวิ | ม.50 ทวิ: **ผู้จ่ายเงินได้**ออกหนังสือรับรองให้ผู้ถูกหัก ตามแบบที่อธิบดีกำหนด | ระบบ/ผู้ขาย ออกแทนลูกค้าไม่ได้ · ช่อง ③ ออกในบัญชีผู้จ่ายเท่านั้น | ✅ |
| L-3 รายการบนใบ | แบบ 50 ทวิ: ชื่อ/ที่อยู่/เลขผู้เสียภาษีผู้จ่ายและผู้ถูกหัก · ลำดับที่ในแบบ (ภ.ง.ด.1ก/1ก พิเศษ/2/3/2ก/3ก/53) · ประเภทเงินได้ · วันเดือนปีที่จ่าย · จำนวนเงินที่จ่าย · ภาษีที่หัก · เงื่อนไข (หัก ณ ที่จ่าย/ออกให้ตลอดไป/ออกให้ครั้งเดียว) · ลายมือชื่อผู้มีหน้าที่หัก + ตราประทับ (ถ้ามี) | กล่องรับต้องสกัด/ตรวจครบ (K0–K8) · ช่องลายมือชื่อ: OCR ตรวจไม่ได้ ⇒ ผู้ขายยืนยันด้วยตา (ไม่ auto ในช่อง ①② เว้นเปิดค่าตั้ง) | ✅ `WhtCertFormText.StampLines:147` มีโครงตรา/ลายมือชื่อ |
| L-4 กำหนดเวลาให้ใบ | ม.50 ทวิ: ออกให้ผู้ถูกหัก**ในขณะที่หักภาษี** (กรณีทั่วไป) | ช่อง ③ ออก+ส่งทันทีที่บันทึกจ่าย = สอดคล้องที่สุด · cadence ทวงของฝั่งขายเป็นเรื่องติดตาม (ไม่ใช่หน้าที่เรา) | ⚠️ ข้อยกเว้นตามประกาศ (เช่น ส่งภายหลังได้) ให้ผู้ทำบัญชียืนยัน |
| L-5 ใบอิเล็กทรอนิกส์/ลายมือชื่ออิเล็กทรอนิกส์ | พ.ร.บ.ว่าด้วยธุรกรรมทางอิเล็กทรอนิกส์ 2544 (ม.7–9 ข้อมูล/ลายมือชื่ออิเล็กทรอนิกส์มีผลทางกฎหมาย · ม.26 ลายมือชื่อที่เชื่อถือได้) + แนวปฏิบัติกรมสรรพากรเรื่องหนังสือรับรองในรูปอิเล็กทรอนิกส์ | ช่อง ③: PDF ที่ระบบผู้จ่ายออก + ลายมือชื่อ/ตราอิเล็กทรอนิกส์ที่ผู้มีอำนาจตั้งไว้ + hash + audit ทั้งสองฝั่ง · ⚠️ ระดับลายมือชื่อที่ต้องใช้ (ภาพลายเซ็น vs ใบรับรองดิจิทัล) ต้องยืนยันกับประกาศอธิบดีฯ ที่ใช้อยู่ — **ห้ามเปิด auto-Received ช่อง ③ ใน production จนกว่ายืนยัน** (เฟส 246 เปิดเป็น "รอยืนยัน" ก่อนได้) | ⚠️ |
| L-6 เครดิตต้องมีใบ | การนำภาษีถูกหักไปเครดิต ภ.ง.ด.50/51 ต้องมีหลักฐาน (หนังสือรับรอง) | ยอด Pending/ส่วนที่ไม่มีใบ **ห้ามนับเครดิต** (WHT_CREDIT_PLAN L1) · ReceivedAmount บางส่วนนับเฉพาะส่วนที่มีใบ | ✅ หลักเดิม |
| L-7 e-Withholding | ผู้จ่ายที่หักผ่าน e-WHT ไม่ต้องออก 50 ทวิ/ยื่น ภ.ง.ด.3/53 สำหรับรายการนั้น · ผู้ถูกหักตรวจข้อมูลผ่านระบบของกรมสรรพากร · อัตราลดเหลือ 1% (ค่าบริการ) ถึง 31 ธ.ค. 2570 | EvidenceKind=EWithholding + หลักฐานจากระบบกรมสรรพากร | ⚠️ ยืนยันวันสิ้นสุด/อัตรา (ข่าว ปชส. 14/2569) + รูปหลักฐานที่ยอมรับตอนตรวจ |
| L-8 ระยะเก็บ | พ.ร.บ.การบัญชี 2543 ม.14 (≥ 5 ปีนับแต่วันปิดบัญชี · อธิบดีสั่งให้เก็บเกินได้ไม่เกิน 7 ปี — แก้รอบ 250 WP-06) + ป.รัษฎากร (ขอคืนได้ 3 ปีนับแต่วันสุดท้ายของกำหนดยื่น) | §7.4 MAX retention + legal hold | ⚠️ เลขมาตรา (ม.10 vs ม.14) |
| L-9 PDPA ข้ามผู้เช่า | พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล 2562 ม.19 (ยินยอม) · ม.23 (แจ้งวัตถุประสงค์) · ม.24(3)(6) (สัญญา/หน้าที่ตามกฎหมาย) · ม.39 (RoPA) | การเชื่อมคู่ค้าต้องยินยอมสองฝ่าย + ข้อความเวอร์ชัน + เพิกถอนได้ · ไม่เปิดเผยการเป็นผู้ใช้ NextAcc ก่อนยินยอม | 📋 |
| L-10 ลูกค้าต่างประเทศ | ผู้จ่ายนอกราชอาณาจักรไม่อยู่ใต้ ม.50/ม.3 เตรส · ม.70 = ผู้จ่ายในไทยจ่ายออกนอก (ฝั่งซื้อของเรา) | ฝั่งขายไม่คาด 50 ทวิ จากลูกค้าต่างประเทศ (เตือนถ้ามี WHT) | ✅ |
| L-11 VAT ไม่ใช่ฐาน WHT | ฐานหัก = ยอดก่อน VAT | ใช้ฐานจากเอกสาร (`AmountsFor` / `PlatformWhtSuggestion.BaseOf*` แบบเดียวกัน) · ตรวจ K7 | ✅ |
| L-12 รายจ่ายต้องห้ามเมื่อตัดสูญ | ภาษีเงินได้นิติบุคคลที่จ่ายล่วงหน้าแต่เครดิตไม่ได้ ⇒ ไม่ใช่รายจ่ายหักได้ (§65 ตรี (6 ทวิ)) | JE ตัดสูญลงผังที่ `Section65TerValidator` ตีเป็น nonDeductible | ⚠️ ผู้ทำบัญชียืนยันผัง |

---

## 9. แผนลงมือ (รอบ 243+)

### 9.0 สิ่งที่รอบ 241 ครอบแล้ว — ห้ามทำซ้ำ

| ของรอบ 241 | ใช้ต่อในแผนนี้อย่างไร |
| --- | --- |
| `DocumentPayLinkPolicy` / `DocumentPayLinkService` / `pay-document.html` / ค่าตั้ง PayLink* | ต่อส่วน WHT (§6.3) + ปุ่มอัปโหลดบนหน้าสำเร็จ (§6.4) — แก้ในไฟล์เดิม ไม่สร้างตัวใหม่ |
| `PostPaymentAutomationService` + `PostPaymentAutomationRun` + `PostPaymentAutomationPolicy` | `RequestWhtCert` = ทวงครั้งที่ 0 · เพิ่มบล็อกลิงก์อัปโหลดใน `WhtCertRequestText`/`EmailReceipt` · เงื่อนไข `whtPending` เปลี่ยนเป็น `WhtCertIntakeStatus` (243) |
| ค่าทับบริษัท › ตาราง (`PostPaymentAutomationPolicy.Effective`) | ขยายเป็น บริษัท › ผู้ติดต่อ › ตาราง ใน resolver เดียว (243) |
| แถว Pending ใน `WhtCreditReceived` จาก GL | ใช้ตามเดิม (ไม่สร้างแถวที่สอง) |
| `NotificationEvents.PostPaymentAutomationBlocked` | ใช้คู่กับ event ใหม่ของ 50 ทวิ |

> ⚠️ ข้อสังเกตถึงทีม 241 (ไม่ใช่งานของเอกสารนี้): (1) `Document.PayLinkToken` เก็บ token ตรง — พิจารณาเก็บ hash แบบ `PlatformPaymentLinks.TokenHash` (2) brief ระบุ "ลงทะเบียน 50 ทวิ ที่รอ" แต่แถว Pending เกิดจาก `SyncWhtCreditReceivedAsync` อยู่แล้ว — 241 ไม่ควรสร้างแถวเพิ่ม (เสี่ยงซ้ำ) (3) เส้นรับชำระอื่นนอก gateway/DocumentController ยังไม่เรียก `EnqueueAsync` (G-6)

### เฟส 243 — "เห็นก่อน": ป้าย + ตัวตัดสินกลาง + เมทริกซ์ (ไม่มีช่องทางใหม่)

งาน:
1. `Helpers/WhtCertIntakeStatus` (pure) + เทสต์ตาราง state ครบ 10 ค่า
2. `DocumentListItem`/`DocumentResponse` + `WhtCertState/Label/Tone/DaysWaiting` (query รวม · ไม่ N+1) · ชิป + ตัวกรองใน `documents.html` · แผงไทม์ไลน์อ่านอย่างเดียว (§6.6 ส่วนบน)
3. การ์ดผู้ติดต่อ + การ์ดแดชบอร์ด (§6.7) · ลิงก์จากแถบ ภ.ง.ด.50
4. `GET auto-send/matrix` + แท็บ "ภาพรวม" ใน email-schedule.html (อ่าน+สลับเปิดปิดกฎเดิม)
5. G-6: ทุกทางรับชำระเรียก `PostPaymentAutomationService.EnqueueAsync` (settlement · พอร์ทัลสลิปเมื่อยืนยัน · นำเข้า · CMS · Lodging) — ประสานเจ้าของไฟล์ 241
6. G-10: `WhtCreditService` เปลี่ยน `InvalidOperationException` → `BusinessRuleException`
7. ~~`AutoSendSettingsResolver` (บริษัท › ผู้ติดต่อ › ตาราง)~~ **ชั้น "ผู้ติดต่อ" ใน `Helpers/AutopilotSettingsResolver` ตัวเดียว** (คำตัดสิน 250-29 — ไม่สร้าง resolver ที่สอง) + คอลัมน์ Contact ชุดแรก (`DocumentEmail`, `DocumentCcEmails`, `AutoSendDisabled`)

เทสต์ยอมรับ:
- ใบที่ชำระมี WHT 300 ไม่มีใบ ⇒ `AwaitingCert` · วันที่ 31 ⇒ `IsOverdue=true` · **ทิศตรงข้าม:** ใบที่ไม่มี WHT ⇒ `NotApplicable` (ไม่มีชิป) · ใบที่ยังไม่ชำระแต่มี WHT คาด ⇒ `NotYetPaid` (ไม่ใช่ค้าง — กันคำเตือนฟ้องใบถูก)
- ใบเสร็จที่ออกจากใบแจ้งหนี้แสดงสถานะเดียวกับต้นทาง (resolver เดียว)
- รับชำระผ่าน settlement หลายใบ ⇒ มีแถว `PostPaymentAutomationRun` ต่อการชำระ (Db test) · **ทิศตรงข้าม:** ปิดค่าตั้งทั้งหมด ⇒ ไม่มีแถว
- matrix: ช่อง "หลังรับชำระ × ใบเสนอราคา" คืน "—" + เหตุ · JS ไม่มีตารางกติกา (grep)
- resolver: ผู้ติดต่อ `AutoSendDisabled=true` ชนะกฎบริษัท · ตารางชนะผู้ติดต่อเฉพาะช่องที่ตั้ง

`required_call_site_check` เพิ่ม:
- `WhtCertIntakeStatus.Of` ต้องถูกเรียกใน: `DocumentService` (list/detail mapper) · `WhtCreditService.ListAsync` · `PostPaymentAutomationService.ExecuteAsync` · dashboard service — และห้ามมี `WhtCreditStatus.Pending` เปรียบเทียบตรงในไฟล์ UI-mapper (ห้ามประกอบเอง)
- `PostPaymentAutomationService.EnqueueAsync` ต้องอยู่ใน: `DocumentPaymentHandler` · `DocumentController.CreatePayment` · `SettlementPostingService` · `PortalService` (ยืนยันสลิป) · `PaymentImportTarget` · `CmsBookingService.Workflow` · `LodgingService.Lifecycle/TaxTiming` · `CmsCommerceService`

> **สถานะ (รอบ 252 · WP-09 · ยังไม่ได้คอมไพล์ในเครื่องนี้):** เฟส 243 ข้อ 1–3 ✅ (ตัวตัดสิน `Helpers/WhtCertIntakeStatus` · ชิป/ตัวกรองรายการเอกสาร · การ์ดผู้ติดต่อ/แดชบอร์ด · ไทม์ไลน์หน้าเอกสาร ⏳) · ข้อ 4 เมทริกซ์ ⏳ · ข้อ 5 ✅ (รอบ 250 WP-02) · ข้อ 6 ✅ (243a) ·
> เฟส 244 ข้อ 1–4 ✅ (ตาราง + migration · `SubmitAsync` + `WhtCertMatchPolicy` + `WhtCertIntakeVerdict` · `WhtCreditableAmount` ทุกผู้อ่าน · เส้นสแกนเข้าประตูเดียว + หน้า wht-credit อัปโหลดหลายไฟล์/แผงยืนยัน) · ข้อ 5–6 ⏳ WP-10 ·
> คำตัดสิน §11 ข้อ 1–2 (AI ตรวจของจริง) ✅ — DOCUMENT_FLOW §2.12j · DECISIONS 252-WP09-1…4

### เฟส 244 — "รับเข้าได้": กล่องรับ + ลิงก์อัปโหลด + allocation

งาน:
1. ตาราง `WhtCertIntake`, `WhtCertAllocation`, `WhtCertUploadLink`, คอลัมน์ `WhtCreditReceived` (§7.1) + migration แถวสแกนเก่า → intake
2. `WhtCertIntakeService.SubmitAsync` (ประตูเดียว) + `WhtCertMatchPolicy` + `WhtCertIntakeVerdict` (pure)
3. `WhtCreditableAmount.Of` + เปลี่ยนผู้อ่านเครดิตทุกจุด (`CreditableAmountAsync`, `CreditableInPeriod`, `TaxService`)
4. ย้าย `OcrService.EnsureWhtCreditFromCertAsync` เข้า `SubmitAsync` · `wht-credit.html` อัปโหลดหลายไฟล์ → กล่องรับ · แผงยืนยัน (§6.6 ส่วนล่าง)
5. หน้า `wht-upload.html` + `WhtCertPublicController` (rate limit · magic bytes · hash) + endpoint พอร์ทัล
6. ลิงก์อัปโหลดในอีเมลใบเสร็จ/คำขอ 50 ทวิ (แก้ `PostPaymentAutomationPolicy.WhtCertRequestText` + `EmailBlock`) + หน้าชำระสำเร็จ

เทสต์ยอมรับ:
- ใบเดียว ฿450 ครอบ INV-0123 (300) + INV-0130 (150) ⇒ allocation 2 แถว · ทั้งคู่ Received · **ทิศตรงข้าม:** subset กำกวม (สองชุดรวมได้ 450) ⇒ `Ambiguous` ไม่ auto
- ผ่อน 3 งวด ได้ใบงวดแรก ⇒ เครดิต ภ.ง.ด.50 = เฉพาะงวดแรก (Db test ผ่าน `TaxService`)
- ใบออกในนามบริษัทอื่น ⇒ `Rejected` K0 · ไม่มี allocation · ลูกค้าเห็นเหตุบนหน้าอัปโหลด
- อัปโหลดไฟล์เดิมซ้ำ ⇒ ไม่มีแถวใหม่ (unique hash) · สแกนใบเดียวกันจากหน้าสแกน หลังลูกค้าอัปโหลดแล้ว ⇒ ไม่มีเครดิตซ้ำ (เคสเดิมของ `OcrService.cs:4485` คอมเมนต์)
- token บริษัท A ใช้กับ URL บริษัท B ⇒ 404 · token หมดอายุ ⇒ หน้าบอกเหตุ · rate limit เกิน ⇒ 429 พร้อมข้อความไทย
- ไฟล์ .exe เปลี่ยนนามสกุลเป็น .pdf ⇒ ปฏิเสธ (magic bytes)
- void การชำระหลังได้ใบ ⇒ แถวเครดิตไม่หายเงียบ: intake คง · ป้าย "ยอดใบเกินยอดที่ถูกหัก" · **ทิศตรงข้าม:** void การชำระที่ยังไม่มีใบ ⇒ แถว Pending ลบตามเดิม
- `GenerateCitReport` kill-switch AI ปิดทั้งหมด ⇒ กล่องรับยังจับคู่ได้ (ไม่มี AI ในเส้นนี้)

`required_call_site_check` เพิ่ม:
- ห้าม `WhtCreditsReceived.Add(` นอก `DocumentService.SyncWhtCreditReceivedAsync` + `WhtCreditService.CreateAsync` (ล็อกจำนวนจุด = 2)
- `WhtCertIntakeService.SubmitAsync` ต้องถูกเรียกใน `WhtCertPublicController` · `PortalService` · `WhtCreditController` · `OcrService` (เส้น 50 ทวิ)
- ผู้อ่านเครดิตทุกตัวต้องเรียก `WhtCreditableAmount.Of` (ห้าม `Sum(w => w.WhtAmount)` บนแถว Received ตรงในไฟล์ภาษี)

> ✅ รอบ 253 (WP-10 · DOCUMENT_FLOW §2.12m): เฟส 244 ข้อ 5–6 บางส่วน — หน้า `wht-upload.html` + `WhtCertPublicController` (rate limit · magic bytes · hash) · ลิงก์ในอีเมลใบเสร็จ (`IReceiptEmailSectionProvider`) · ปุ่มลิงก์ในรายงาน "ยังขาด" · ยังไม่ทำ: endpoint พอร์ทัล · ปุ่มบนหน้าชำระสำเร็จ

### เฟส 245 — "ตามให้เอง": ทวงอัตโนมัติ + e-WHT + รายงานปลายปี

งาน: `WhtCertReminderJob` (JobLock) + trigger `WhtCertReminder` + ค่าตั้ง/override ผู้ติดต่อ · ปุ่มทวง/หยุดทวง · EvidenceKind=EWithholding + หลักฐาน · ตัวเลือก e-WHT บนหน้าชำระ · รายงาน §4.5 + Excel · G-9 ตัดสูญลง JE (ยืนยันผังกับผู้ทำบัญชี) · สรุปภายในรายสัปดาห์

เทสต์: ใบค้าง 7/21/45 วัน ⇒ อีเมล 1 ฉบับต่อ**ลูกค้า**ต่อรอบ (ไม่ใช่ต่อใบ) · ได้ใบแล้ว ⇒ หยุดทวง · **ทิศตรงข้าม:** ใบที่ `RemindersPaused` / ผู้ติดต่อปิดทวง / State=Submitted ⇒ ไม่ส่ง · สองเครื่องรันพร้อมกัน ⇒ ส่งครั้งเดียว (Db test JobLock) · e-WHT ไม่มีหลักฐาน ⇒ ไม่นับเครดิต · มีหลักฐาน ⇒ นับ · รายงานปลายปีกระทบ GL 11910 ตรง

`required_call_site_check`: `WhtCertReminderJob` ผ่าน `JobLock` · ส่งผ่าน `OnBehalfEmailSender`/คิวอีเมลเดิม (ห้าม `IEmailService` ตรง)

> ✅ รอบ 253 (WP-10 · DOCUMENT_FLOW §2.12m · คำตัดสิน 253-WP10-3…6): `WhtCertReminderJob` (JobLock · watermark ต่อบริษัท · ค่าตั้งต้นปิด) · ทวงทางอีเมลเท่านั้น (LINE backlog) · e-WHT แบบ N4 (แนบเป็นใบธรรมดา) · "ไม่หัก" บนหน้าชำระ (§11 ข้อ 3) · ยังไม่ทำ: ปุ่มทวง/หยุดทวง · ตัวเลือก e-WHT บนหน้าชำระ · รายงานปลายปีส่วนที่เหลือ · G-9 · สรุปภายในรายสัปดาห์

### เฟส 246 — NextAcc↔NextAcc (ส่งจาก "ออก 50 ทวิ แล้ว")

> ⚠️ (แก้รอบ 250 WP-05 · คำตัดสิน 250-30: ต่อยอด `TradingPartnership` ที่มีอยู่ — ดู AUTOPILOT_BILLING_DESIGN §6 รอบ 251–253 · ไม่สร้างตารางสำเนา) — ชื่อ `TenantPartnerLink`/`TenantWhtCertExchange` ข้างล่างอ่านเป็น "ขอบเขต WhtCert บน `TradingPartnership`"

งาน: `TenantPartnerLink` + หน้า "คู่ค้าที่ใช้ NextAcc" (เชิญ/ยอมรับ/เพิกถอน) · `WhtCertExchangeOutbox` + worker · hook `OnWhtCertIssuedAsync` / `VoidAsync` · intake Channel=NextAccLink (structured) · RoPA entry · เปิด auto-Received หลังยืนยัน L-5 เท่านั้น (ก่อนนั้น = รอยืนยัน 1 คลิก)

เทสต์: P ออกใบ ⇒ S ได้ intake ภายในรอบ worker · P void ⇒ S กลับเป็นรอใบ + แจ้ง · S Claimed แล้ว P void ⇒ ไม่ย้อนเงียบ (ป้ายแดง + audit) · **ทิศตรงข้าม:** ไม่มีลิงก์/ลิงก์ Revoked/ S ปิดรับ ⇒ ไม่มีอะไรข้ามบัญชี (อีเมลเดิมทำงาน) · ผู้ใช้ P ไม่มี `TaxFile` ⇒ ยอมรับลิงก์ไม่ได้ · S ไม่เห็นว่า P ใช้ NextAcc ก่อน P ยินยอม (API ไม่คืนข้อมูลนี้) · query ทุกจุดมี CompanyId

`required_call_site_check`: การเขียนข้ามบริษัทอยู่ใน `TenantWhtCertExchange` ตัวเดียว · ต้องเรียก `TenantPartnerLinkPolicy.CanDeliver` ก่อน `SubmitAsync`

### เฟส 247 — ติ๊กบนหน้าชำระ (ชำระผ่านบัญชี NextAcc ของผู้จ่าย)

งาน: กล่อง "ชำระผ่าน NextAcc" บน `pay-document.html` · SSO → เลือกบริษัท P · หน้า "ชำระใบแจ้งหนี้จากผู้ขาย" ใน P (สร้างใบซื้อร่างจากข้อมูล S — กฎ #3) · ช่องติ๊ก + ผู้ลงนาม · PaymentIntent ของ S ↔ การจ่ายใน P (idempotent ทั้งสองฝั่ง) · ไม่มีสิทธิ์ ⇒ งานรออนุมัติใน P

เทสต์: จ่ายสำเร็จ ⇒ P มีใบซื้อ+การจ่าย+50 ทวิ, S มีการรับชำระ+ใบเสร็จ+intake Received — **ครั้งเดียว**แม้ webhook ซ้ำ · intent ล้ม ⇒ ไม่มี 50 ทวิ · ผู้ใช้ไม่มีสิทธิ์ ⇒ ไม่มีช่องติ๊ก + งานรออนุมัติ (ไม่ silent) · **ทิศตรงข้าม:** ลูกค้าที่ไม่ใช่ผู้เช่า/ไม่เชื่อม ⇒ หน้าชำระเหมือนเดิมทุกอย่าง · กล่อง NextAcc ไม่เปิดเผยสถานะการเป็นผู้ใช้

### backlog (P3)

- ⑤ LINE OA (ผูก LINE userId ↔ ผู้ติดต่อ — ขยาย `CustomerLineLinkService`) → `SubmitAsync(Channel=Line)`
- ⑥ อีเมลขาเข้า (ตัดสินผู้ให้บริการรับเมล + กัน spoof: รับเฉพาะผู้ส่งที่ตรงอีเมลผู้ติดต่อ + ยังต้องผ่าน K0/K1)
- นำเข้ารายงาน e-WHT ทั้งเดือน (CSV)
- ส่งใบกำกับภาษีอย่างย่อ/สลิป POS ทางอีเมล/LINE เมื่อลูกค้าขอ (G-12)
- ขยาย `TenantPartnerLink.Scope` เป็นการแลกใบแจ้งหนี้/ใบกำกับระหว่างผู้เช่า (e-document exchange) — ใช้โครงเดียวกัน
- เครดิตภาษีต่างประเทศ (DTA) — ทะเบียนแยก

### 9.x เอกสารที่ต้องขยับพร้อมโค้ด (หลักการ #10)

`DOCUMENT_FLOW.md` (§2.12g + ส่วนใหม่ "รับ 50 ทวิ") · `WHT_CREDIT_PLAN.md` (allocation · EvidenceKind · ช่องทาง — ป้าย ✅/🔨/📋) · `TEST_PLAN.md` · `ACCOUNT_STRUCTURE.md` (เฟส 246: `TenantPartnerLink` เป็นความสัมพันธ์ระดับบริษัท) · `CHANGELOG.md` · RoPA

---

## 10. คำถามที่ต้องให้เจ้าของ/ผู้ทำบัญชีตัดสิน

1. **ยืนยันอัตโนมัติ (ช่อง ①②)** ค่าตั้งต้น ปิด (แนะนำ) หรือ เปิด? — ปิด = ผู้ขายกด 1 คลิก · เปิด = เร็วแต่เสี่ยงใบปลอม/ลายมือชื่อไม่ครบ
2. **ช่อง ③ auto-Received** เปิดได้เมื่อยืนยัน L-5 (รูปแบบลายมือชื่ออิเล็กทรอนิกส์ที่สรรพากรยอมรับ) — ใครยืนยัน?
3. **ให้ลูกค้าเลือก "ไม่หัก" บนหน้าชำระได้ไหม** (ยอดเต็ม) — แนะนำ ได้ (ผู้จ่ายเป็นผู้รับผิดตาม §54 ไม่ใช่เรา) แต่ป้ายบอกผู้ขาย
4. จังหวะทวงตั้งต้น 7/21/45 + รายเดือนสูงสุด 6 ครั้ง — เหมาะไหม
5. e-WHT: หลักฐานแบบไหนที่สำนักงานบัญชีของลูกค้าใช้จริง (ภาพหน้าจอระบบกรมสรรพากร / รายงานธนาคาร)
6. ตัดสูญ 11910 ลงผังใด (ค่าใช้จ่ายต้องห้าม) และทำได้ตอนไหน (สิ้นรอบเท่านั้น?)
7. CLAUDE.md อ้าง "พ.ร.บ.บัญชี ม.10" เรื่องเก็บ 5 ปี — ตัวบทระยะเก็บคือ ม.14 — แก้ doc ไหม

## 11. คำตัดสินเจ้าของ (2026-11-06) — ตอบ §10

1+2. **ยืนยันอัตโนมัติ (ทุกช่องทาง รวม ① ② ③)** — ใบที่ลูกค้าส่งเข้ามา ถ้า OCR ได้ข้อมูลครบ ⇒ ให้ AI ช่วยตรวจว่าเป็นเอกสารจริง (ผ่าน `IAiOrchestrator` + distillation model ตามกฎ #1 — student ก่อน · ปิด AI แล้ว local ต้องตัดสินได้) ⇒ **มั่นใจ = ยืนยันอัตโนมัติ** · ไม่มั่นใจ = ผู้ใช้ตรวจแล้วกดยืนยัน · ด่านเชิงกำหนด (เลขผู้เสียภาษี checksum · ผู้จ่าย/ผู้รับตรงคู่สัญญา · ยอด/อัตราตรงใบเสร็จ · ปีภาษี) ต้องผ่านก่อนเสมอ ไม่ใช่ให้ AI ตัดสินแทน
3. **ลูกค้าเลือก "ไม่หัก ณ ที่จ่าย" บนหน้าชำระได้** — ถ้าลูกค้าเป็นนิติบุคคล ต้องขึ้นคำเตือน (หน้าที่หักตาม ท.ป.4/2528 · ม.50/§3 เตรส) และ**บันทึกการยอมรับ** (ผู้กด · เวลา · IP · ข้อความที่แสดง · version) เป็นหลักฐาน
4. **จังหวะทวง 7/21/45 วัน + รายเดือนสูงสุด 6 ครั้ง** — อนุมัติ
5. **e-WHT หลักฐาน** — ตั้งทีมวิเคราะห์/หาข้อมูล เปิดช่องทางรองรับให้มากที่สุด (ผลอยู่ที่ `erp-review/2026-11-06/EWHT_EVIDENCE_RESEARCH.md`)
6. **ตัดสูญ 11910** — ตั้งทีมวิเคราะห์และดำเนินการได้เลย
7. **ม.10 vs ม.14 พ.ร.บ.การบัญชี** — ตั้งทีมตรวจสอบละเอียด + ฝ่ายค้าน ตรวจจนมั่นใจแล้วแก้เอกสารได้เลย
