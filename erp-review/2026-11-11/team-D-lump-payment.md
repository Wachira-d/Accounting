# ทีม D — "ลูกค้าโอนก้อนเดียวปิด 2 ยอด แต่เราออกใบแจ้งหนี้ไว้ใบเดียว" (วิเคราะห์ + ออกแบบ)

> 2026-11-11 · อ่านอย่างเดียว (ไม่แก้โค้ด · ไม่ commit) · HEAD `81521510` · **ยังไม่ได้คอมไพล์/รันอะไรในเครื่องนี้** — ทุกข้อคือการอ่านโค้ดตรงจุด
> ตัวย่อ: `DS` = `Accounting/Services/Implementations/DocumentService.cs` · path อื่นนับจาก `Accounting/`

## 0. คำตอบสั้น

**วันนี้ระบบไม่มีทางที่ใช้งานได้จริงจากหน้าจอ** สำหรับ "เงินเข้ามากกว่าใบที่ออกไว้ แล้วผูกส่วนเกินกับใบที่ออกทีหลัง":

1. ทุกทางเข้ารับชำระ**ปฏิเสธยอดเกินยอดค้าง** (ถูกต้องในแง่กัน AR ติดลบ) แต่**ไม่มีทางไปต่อ** — ผู้ใช้เจอ toast แล้วจบ
2. มีของ "ครึ่งตัว" อยู่ 2 ชิ้นที่เกือบใช่ แต่ต่อไม่ถึงกัน
   - **เงินส่วนเกินรอจัดสรร** ของเส้นชำระหลายใบ (ลง Cr 217xx จริง) — **มีขาเข้า ไม่มีขาออก** ไม่มีหน้าจอเรียก ไม่มี endpoint จัดสรรทีหลังทั้งที่ doc-comment สัญญาไว้
   - **ระบบมัดจำ** (ใบเสร็จมัดจำ → `apply-deposit` ตัดใบแจ้งหนี้ที่ออกทีหลัง · คืนเงิน · ริบ · VAT §78/1 ครบ) — ใช้ได้ถ้าผู้ใช้ "รู้ล่วงหน้า" ว่าต้องแยกบันทึกเงินก้อนเดียวเป็น 2 รายการเอง (รับชำระใบ 1 + ออกใบเสร็จมัดจำอีกใบ) ซึ่งไม่มีอะไรบนจอพาไป และ statement ธนาคารจะจับคู่ 1 บรรทัดกับ 2 รายการได้เฉพาะผ่านกลุ่มกระทบยอดมือ
3. ผลที่เจ้าของกังวลเกิดจริง: ถ้าพนักงานบันทึกแค่ส่วนของใบ 1 → เงินอีกครึ่ง**ไม่อยู่ในบัญชี** (ธนาคารไม่ตรง) · ถ้ารอออกใบ 2 ก่อน → ใบ 1 ค้าง + ถูกทวง (อีเมลเกินกำหนดถึงลูกค้า + แจ้งเตือนทีม) ทั้งที่เงินเข้าแล้ว

---

## ส่วนที่ 1 — ของที่มีวันนี้ (หลักฐาน file:line)

### 1.1 รับชำระใบเดียว (`CreatePaymentAsync`)
| ข้อเท็จจริง | หลักฐาน |
| --- | --- |
| รับได้เฉพาะ Invoice/TaxInvoice/DebitNote/PI/Expense สถานะ Approved/Sent/PartiallyPaid/Overdue | `DS:15163-15180` · `PayableDocumentTypes` `DS:14772` |
| **จ่ายเกิน = throw** "เงินสุทธิ + ค่าธรรมเนียม มากกว่ายอดค้างชำระ" (ยอมเกิน 0.01) | `DS:15251-15254` · ตัวตัดสิน `Helpers/DocumentSettlementState.cs:54` (`WouldOverpay`) |
| ชำระบางส่วนได้ → `PartiallyPaid` · ตัวคิดสถานะเดียว `DocumentSettlementState.Apply` | `Helpers/DocumentSettlementState.cs:62-84` |
| ค่าธรรมเนียมหักจากยอดโอน (ฝั่งขาย) `FeeAmount` → Dr 54710 | `DS:15182-15191` · DOCUMENT_FLOW §3.4 |
| บรรทัดปรับส่วนต่าง (`SettlementAdjustments`) **ฝั่งซื้อเท่านั้น · THB เท่านั้น** — ฝั่งขายไม่มีช่อง "ปัดเศษ/ส่วนต่าง" นอกจาก fee | `DS:15206-15216` |
| WHT ต่องวด: เพดาน = WHT ของ**ใบนั้น** (`GrossCap`) เกิน = throw | `DS:15285-15291` |
| ล็อกแถวเอกสาร `FOR UPDATE` ใน transaction (กันจ่ายซ้อน) | `DS:15148-15160` |
| หน้าเว็บกันเองก่อนส่ง: toast "ยอดที่ปิดหนี้มากกว่ายอดค้างชำระ" แล้ว `return` — **ไม่มีปุ่ม/ทางเลือกต่อ** | `wwwroot/pages/documents.html:13122` |

### 1.2 รับชำระหลายใบในครั้งเดียว (`CreateMultiDocPaymentAsync`) — มีแต่ฝั่ง API
| ข้อเท็จจริง | หลักฐาน |
| --- | --- |
| `CreatePaymentRequest.Allocations[]` → Payment แม่ 1 แถว + `PaymentAllocation` ต่อใบ + JE ต่อใบ (`PAY-…/n`) | `DS:15762-16001` · `Models/Entities/Payment.cs:129-156` |
| ทุกใบต้อง**ผู้ติดต่อเดียวกัน** · จัดสรรต่อใบ ≤ ยอดค้าง · Σ จัดสรร ≤ ยอดเงิน | `DS:15804-15824` · `DS:15778-15783` |
| **Σ จัดสรร < ยอดเงิน = รับได้** → ส่วนเหลือลง JE "เงินส่วนเกินยังไม่จัดสรร" Dr เงินสด / Cr `21710` → ไม่มี = ผังแรกใต้ `217` · ไม่มีผัง = throw | `DS:16006-16068` |
| ผลตอบกลับมี `UnappliedCredit` · doc-comment: "operator can later attach … via `/payments/{id}/allocations`" | `Models/DTOs/Document/DocumentDtos.cs:1777-1783` · `DS:15740` |
| **endpoint นั้นไม่มีอยู่จริง** — `DocumentController` มีแค่ `POST payments` / `payments/{id}/void` | `Controllers/DocumentController.cs:1700-1765` (grep `allocations"` ทั้ง `Controllers/` = เจอเฉพาะ `DimensionController:39,43`) |
| **หน้าเว็บไม่ส่ง `allocations` เลย** (grep `wwwroot/**` = 0 จุด นอกจาก dimension) → ผู้ใช้เว็บเข้าเส้นนี้ไม่ได้ | `wwwroot/js/api.js:1906-1908` เป็นจุดเดียวที่มีคำนี้ |
| ส่วนเกิน**ไม่ถูกเก็บเป็นคอลัมน์** (คิดสด `Amount − Σalloc` ตอนตอบ/ตอน void) · `Payment` ไม่มี `ContactId` · `GetPaymentsAsync` ไม่คืน `UnappliedCredit` และกรอง `p.DocumentId == documentId` (ใบที่ 2+ ของการชำระหลายใบมองไม่เห็นการชำระ) | `DS:16144` · `DS:12362` · `DS:16158-16163` |
| ไม่มีค่าธรรมเนียม · บรรทัดปรับ = throw · **ไม่ออกใบเสร็จอัตโนมัติ** · WHT เกินไม่บล็อก (เตือน) | `DS:15766-15770` · `Helpers/PostPaymentAutomationPolicy.cs:35-51` · `DS:15903-15931` |
| void: กลับ JE ทุกใบที่ `Reference == PAY-… หรือ PAY-…/n` (รวม JE ส่วนเกิน) + คืนยอดธนาคารรวมส่วนเกิน | `DS:12330-12375` |

**ข้อบกพร่องที่พบระหว่างอ่าน (ของเดิม ไม่เกี่ยวกับดีไซน์ใหม่แต่ต้องปิดไปด้วย)**
- **P-1 ผังพักผิดตัว**: ผังมาตรฐานไม่มี `21710` (`Services/ChartOfAccountTemplates.cs:135-139` มี 21711 ค่าเช่ารับล่วงหน้า…21714 ดอกเบี้ยค้างจ่าย) → ตก prefix `217` = "ผังแรกที่ postable" (`FindAccountAsync` `DS:16623-16628`) · ฝั่งจ่าย `11470` ไม่มี → prefix `114` = **11400 เงินให้กู้ยืมระยะสั้น** (`ChartOfAccountTemplates.cs:46-47`) = เงินจ่ายล่วงหน้าเจ้าหนี้ไปโผล่เป็นเงินให้กู้
- **P-2 ส่วนเกินถูกหยิบไปตัดใบ "ของใครก็ได้"**: JE ส่วนเกินไม่มี `SourceDocumentId` และ Cr 217xx → เข้าเงื่อนไข `SearchJournalDepositsAsync` (`DS:6395-6412`) → `apply-journal-deposit` ตัดใบแจ้งหนี้ได้โดย**ไม่ตรวจผู้ติดต่อ** (`DS:6446-6500` ไม่มีเงื่อนไข contact — JE ไม่มีผู้ติดต่อ) · หลังจากนั้น void การชำระจะกลับ JE ส่วนเกิน (`DS:12330-12345`) ทั้งที่ถูกใช้ตัดหนี้ไปแล้ว ⇒ 217xx ติดลบ ไม่มีด่านใดจับ (ทางเข้านี้มีจริงผ่าน API เท่านั้นเพราะข้อ 1.2 แต่เป็นหลุมเปิด)

### 1.3 ใบเสร็จ/ใบสำคัญรับที่ "แปลง" จากใบแจ้งหนี้ (settlement doc)
- เพดานเข้ม: ยอดใบเสร็จ > ยอดค้างใบต้นทาง = throw (`DS:12543-12548`) · WHT สะสมเกิน WHT ใบต้นทาง = throw (`DS:12557-12577`) · เกณฑ์เงินสดปิดยอดแต่ WHT ขาด = throw พร้อมทางแก้ (`DS:12591-12622`)
- ใบเสร็จ 1 ใบอ้างใบต้นทางได้**ใบเดียว** (`RelatedDocumentId`) — ไม่มีใบเสร็จใบเดียวปิดหลายใบแจ้งหนี้ (ใบวางบิลรวมชำระไม่ได้ ต้องชำระทีละใบ `DS:15173-15175` · `documents.html:11476-11480`)
- ใบเสร็จไม่มีใบต้นทาง = ขายเงินสด (รายได้) — ไม่ใช่การตัดหนี้

### 1.4 เงินรับล่วงหน้า / มัดจำ (มีครบและแข็งแรง — แต่เป็น "คนละประตู")
| ข้อเท็จจริง | หลักฐาน |
| --- | --- |
| ใบเสร็จ/ใบกำกับที่ติ๊ก `IsDeposit` → หนี้สิน 21712/21713 (หรือเงินประกัน 21530/21620) · 3 โหมด VAT (`VatImmediate` §78/1 · `VatPendingUndue` 21913 · `FullDeposit`) | `Models/Entities/Document.cs:296-309` · `Helpers/DepositPolicyResolver.cs:197-242` · ฟอร์ม `documents.html:944-945` |
| **ตัดใบแจ้งหนี้ที่ออกทีหลังได้**: `POST {invoiceId}/apply-deposit` → JV Dr 217xx(+VAT พัก) / Cr ลูกหนี้ · ลด `BalanceDue` · ผู้ติดต่อเดียวกัน · สกุล/อัตราเดียวกัน · ≤ มัดจำคงเหลือ · ≤ ยอดค้างใบ | `Controllers/DocumentController.cs:666-678` · `DS:6021-6091` |
| **one-shot**: มัดจำ 1 ใบ → ใบปลายทาง 1 ใบ (ทยอยหักใบเดิมซ้ำได้ · ใบอื่น = `DEPOSIT-APPLIED-ELSEWHERE`) | `DS:6092-6113` |
| มัดจำที่ออกใบกำกับแล้ว (VAT ทันที) **ห้ามตัดชำระแบบ gross** กับใบกำกับเต็มยอด (VAT ซ้ำ) → ต้อง "หักมัดจำเป็นฐาน" ที่ใบสุดท้าย | `DS:6041-6051` (`GrossApplyBlocked`) |
| คืนเงินมัดจำ `refund-deposit` · รับรู้/ริบ `realize-deposit` · ศูนย์มัดจำ + GL tie-out | `Controllers/DocumentController.cs:604-631, 652-664, 709-722` |
| JV มัดจำไม่มีเอกสาร → `apply-journal-deposit` **หักเต็ม JV เท่านั้น** (JV > ยอดค้างใบ = throw) | `DS:6446-6500` |
| ปิดยอดด้วยมัดจำแล้วย้าย VAT พัก 21913→21911 + ลง WHT ที่เหลือ | `DS:6557-6566` |

### 1.5 Statement ธนาคาร / feed / Autopilot
- ตัวตัดสินเงินโอนปิดหนี้ `Helpers/BankReceiptAutoSettle.Decide`: ปิดเองเฉพาะ**ยอดตรงเป๊ะกับใบเดียว** + ตัวระบุตัว · ขาด/เกิน ≤ ฿50 หรือ 2% = `Suggest` · **นอกนั้น = `None` "ไม่มีใบค้างที่ยอดตรง"** · "ไม่ปิดหลายใบรวม (subset-sum = backlog)" (`Helpers/BankReceiptAutoSettle.cs:13-14, 85-86, 163-175`)
- ⇒ เงินเข้า = ใบ 1 + ยอดที่ยังไม่ออกใบ (เกินเท่าตัว) → `None` → บรรทัดค้าง `Unmatched` ไม่ขึ้นคิว "เงินเข้ารอยืนยัน" · เงินเข้า = ผลรวมหลายใบ → `None` เช่นกัน
- คนกดยืนยันในคิว → `CreatePaymentAsync(Amount = ยอดบรรทัดเต็ม)` (`Services/Implementations/BankReceiptSettleService.cs:381-397`) ⇒ ถ้าเกินยอดค้าง = throw จาก `DS:15251` → ประทับล้ม + แจ้ง E-14b (ไม่เงียบ แต่ไม่มีทางไปต่อ)
- กลุ่มกระทบยอดมือ (`reconciliation-groups`) จับ 1 บรรทัดธนาคาร ↔ หลาย Payment/JE ได้ · ยอดสองฝั่งต้องเท่ากัน (≤1 บ. / ≤20 บ. มีเหตุผล) (`Services/Implementations/BankService.Reconciliation.cs:86-155`) — เป็นแค่การ**จับคู่** ไม่สร้างการรับชำระ
- `transactions/{id}/create-je`: สร้าง JE Dr ธนาคาร / Cr ผังที่เลือก**เต็มยอดบรรทัด** (`Controllers/BankController.cs:370-431`) — ทางลัดที่พนักงานจะใช้: Cr รายได้/ลูกหนี้ตรง ๆ ⇒ ใบแจ้งหนี้ยังค้างตลอดกาล (ตรงกับที่เจ้าของกลัว) หรือ Cr 217xx แล้วไปชน P-2/`apply-journal-deposit` หักเต็ม JV

### 1.6 Gateway / ลิงก์ชำระ / สลิป / Integration / นำเข้าไฟล์
- ลิงก์ชำระ/gateway: ยอด intent ตั้งจากยอดค้างของ**ใบเดียว** · เงินเข้าเกินยอดค้าง → log error แล้ว `CreatePaymentAsync` ปฏิเสธ (บันทึกลงประวัติ intent + แจ้งเจ้าของ) (`Services/Payments/Handlers/DocumentPaymentHandler.cs:69-75, 103-112`)
- Integration `ProcessPaymentAsync`: เกิน = throw · ชำระครบแล้ว = `Skipped` (`Services/Implementations/IntegrationService.cs:1315-1333`) · นำเข้าไฟล์: เกิน = error ต่อแถว (`Services/Import/Targets/PaymentImportTarget.cs:122-123`)
- ทุกทาง = "ใบเดียว ยอดไม่เกินค้าง" — ไม่มีทางใดรับ "เงินที่ยังไม่มีใบ"

### 1.7 ใบที่ออกทีหลัง + tax point
- `TaxPointResolver.Resolve`: บริการ = MIN(PaymentDate, IssueDate, ServiceUsedDate) · สินค้า = MIN(Delivery, Ownership, Payment, Issue) (`Services/Implementations/Tax/TaxPointResolver.cs`) — **snapshot ตอนอนุมัติเท่านั้น** (`DS:7575-7579`) และอ่าน `doc.PaymentDate` ของ**ตัวเอกสาร** ไม่ได้อ่านจากการรับเงินที่เกิดก่อนใบ
- ⇒ ใบ 2 ที่ออกทีหลังเงินเข้า: ถ้าผู้ใช้ไม่กรอก `PaymentDate` ย้อน → tax point = วันที่ใบ (เดือนถัดไปได้) = **VAT บริการนำส่งช้า 1 งวดโดยระบบไม่รู้** · ถ้ากรอก → tax point ถูก และมีคำเตือนตอนอนุมัติเมื่องวดนั้นยื่นแล้ว (`DS:22178-22197`) — มีด่านแต่ไม่มีอะไรป้อนข้อมูลให้ด่าน
- ใบแจ้งหนี้บริการพัก VAT ที่ 21913 จนได้รับเงิน (`TryReclassifyUndueOutputVatAsync` ถูกเรียกหลังรับชำระ/หักมัดจำ `DS:16081-16083`, `DS:6557`) — ใช้ต่อได้ในดีไซน์ใหม่

### 1.8 Statement ลูกค้า / aging / ทวงหนี้ / พอร์ทัล
- ทวงภายใน `OverdueDunningJob`: ใบ `BalanceDue > 0.005` + `AgingDays ≥ 30` ต่อ**ใบ** ไม่ดูเงินรอจัดสรร/มัดจำของผู้ติดต่อ (`Services/Background/OverdueDunningJob.cs:63-73`)
- อีเมลเกินกำหนด**ถึงลูกค้า** `EmailScheduleService`: `BalanceDue > 0` + `DueDate` ต่อใบ (`Services/Implementations/EmailScheduleService.cs:578-586`) ⇒ ลูกค้าที่โอนครบแล้วยังถูกทวงใบที่ยังไม่ได้ตัด
- Aging (`AgingReportService.cs:70,112`) และพอร์ทัล (`PortalService.cs:318-336`) = `BalanceDue` ต่อใบ — ไม่มีคอลัมน์เครดิตคงเหลือ
- Statement ผู้ติดต่อ (`Controllers/CustomerStatementController.cs:57-63, 100-108`): เดบิต = ใบตั้งหนี้ · เครดิต = **เอกสาร** CN/Receipt/ReceiptVoucher เท่านั้น — **ไม่อ่านตาราง `Payments`** ⇒ การรับชำระที่ไม่มีใบเสร็จ (เส้นหลายใบไม่ออกใบเสร็จ) ไม่ลดยอดวิ่ง ทั้งที่ส่วน aging ด้านล่างใช้ `BalanceDue` (สองตัวเลขในหน้าเดียวไม่ตรงกัน) · เงินส่วนเกินไม่ปรากฏเลย

### 1.9 WHT ที่ลูกค้าหักจากยอดรวม 2 ยอด
- ฝั่งรับชำระ: WHT ของงวดเกิน WHT ของใบ = throw (`DS:15285-15291`) ⇒ บันทึก WHT ของ "ยอดที่ยังไม่มีใบ" ไม่ได้จนกว่าจะมีใบ 2
- กล่องรับ 50 ทวิ: ภาษีบนใบ > ยอดถูกหักที่บันทึกทั้งหมดของผู้จ่าย → ระดับ `Over` พร้อมข้อความ "ตรวจการรับชำระ…" (ไม่จัดสรรเอง) (`Helpers/WhtCertMatchPolicy.cs:103-107`) · เมื่อมีใบ 2 + รับชำระแล้ว ชุด subset (K4) จับ 1 ใบรับรอง ↔ 2 รายการได้ (`:109-116`) — ส่วนนี้ใช้ได้ทันทีที่เงินถูกจัดสรรครบ

---

## ส่วนที่ 2 — ทุกสถานการณ์ (X = ยอดใบ 1 ที่ออกแล้ว · Y = ยอดที่ยังไม่ออกใบ · ลูกค้าโอน X+Y)

| # | สถานการณ์ | วันนี้เกิดอะไร | จัดชั้น | หลักฐาน |
| --- | --- | --- | --- | --- |
| a | บันทึกรับ X+Y ที่ใบ 1 (ยอดตรงผลรวมพอดี) | ปฏิเสธ: หน้าเว็บ toast · เซิร์ฟเวอร์ throw | **บล็อกชัด แต่ไม่มีทางไปต่อ** | `documents.html:13122` · `DS:15251` |
| b | พยายาม "จ่ายเกิน" ใบ 1 | เหมือน a (ไม่มีทางเกิด overpay เงียบ — ดี) | บล็อก | `DocumentSettlementState.cs:54` |
| c | บันทึกแค่ X ที่ใบ 1 ทิ้ง Y | ใบ 1 = Paid · **Y ไม่อยู่ใน GL** · บรรทัดธนาคาร X+Y จับคู่กับ Payment X ไม่ได้ (กลุ่มกระทบยอดต้องเท่ากัน) → ค้าง Unmatched หรือพนักงานกด `create-je` ทั้งก้อน (เงินซ้ำ X) | **ผิดเงียบ / ค้าง** | `BankService.Reconciliation.cs:141-155` · `BankController.cs:370-431` |
| c2 | ไม่บันทึกอะไร รอออกใบ 2 | ใบ 1 เข้า aging → ทวงทีม 30/60/90 วัน + อีเมลเกินกำหนดถึงลูกค้า | **ผิดต่อลูกค้า** | `OverdueDunningJob.cs:63-73` · `EmailScheduleService.cs:578-586` |
| d | ออกใบ 2 ก่อน แล้วรับเงินครั้งเดียว 2 ใบ | API ทำได้ (`Allocations`) · **หน้าเว็บทำไม่ได้** → ต้องบันทึก 2 ครั้ง (2 Payment) แล้วจับคู่ธนาคารด้วยกลุ่ม 1↔2 · ไม่มีใบเสร็จอัตโนมัติถ้าใช้เส้นหลายใบ | ทำได้แบบอ้อม | `DS:15762` · grep `wwwroot` = 0 · `PostPaymentAutomationPolicy.cs:51` |
| e | บันทึกเงินไปแล้ว ใบ 2 ออกทีหลัง → จัดสรรทีหลัง | (1) ถ้าเงินส่วน Y ถูกบันทึกเป็น**ใบเสร็จมัดจำ** → `apply-deposit` ได้ (2) ถ้าเป็น "ส่วนเกิน" ของเส้นหลายใบ → **ไม่มีทาง** (นอกจากหลุม P-2) (3) ถ้าเป็น JE มือ Cr 217 → หักได้เฉพาะเต็ม JV | ครึ่งตัว | `DS:6021` · `DocumentDtos.cs:1781` · `DS:6494-6498` |
| f | เงินเข้าทาง statement/feed | X+Y เทียบใบ 1 → `None` (เกิน > 2%) ไม่ขึ้นคิว · ผลรวมหลายใบ → `None` · คนยืนยันยอดเกิน → ล้ม + แจ้ง | **ค้างเงียบในหน้าธนาคาร** | `BankReceiptAutoSettle.cs:163-175` · `BankReceiptSettleService.cs:381-397` |
| g | Gateway/LINE pay-link/สลิป | ยอดถูกตรึงที่ยอดค้างใบเดียว → ลูกค้าจ่าย Y ผ่านลิงก์ไม่ได้ · เงินเกิน (ยอดใบถูกแก้หลังสร้างลิงก์) = ล้มดัง + แจ้ง | บล็อกชัด | `DocumentPaymentHandler.cs:69-75` |
| h | ลูกค้าหัก WHT จาก X+Y | WHT ส่วน Y บันทึกไม่ได้ (เพดาน = WHT ใบ 1) · 50 ทวิ ที่อัปโหลด = `Over` ค้างกล่อง · เครดิตภาษี 11910 ขาดจนกว่าจะมีใบ 2 | ค้าง (มีข้อความ) | `DS:15285-15291` · `WhtCertMatchPolicy.cs:103-107` |
| i | ธนาคารหักค่าธรรมเนียม/ปัดเศษ | ใบเดียว: `FeeAmount` ได้ · หลายใบ: ไม่มี fee · ฝั่งขายไม่มีบรรทัดปรับปัดเศษ (เกิน 0.01 = บล็อก) | ช่องว่าง | `DS:15182-15191, 15206` |
| j | Y เป็นมัดจำงานอนาคต vs ของ/บริการที่ส่งแล้ว | มัดจำ: ระบบมัดจำรองรับ VAT 3 โหมด · ส่งแล้วแต่ลืมออกใบ: ต้องออกใบย้อน — ไม่มีอะไรบอก tax point | ผู้ใช้ต้องรู้เอง | `DepositPolicyResolver.cs:197-242` · `TaxPointResolver.cs` |
| k | เงินเข้าเดือน N · ใบ 2 ออกเดือน N+1 | บริการ: tax point ควร = วันรับเงิน (เดือน N) แต่ระบบใช้วันที่ใบ เว้นแต่ผู้ใช้กรอก `PaymentDate` ย้อน → VAT เลื่อนงวดเงียบ · กรอกแล้วงวด N ยื่นแล้ว = มีคำเตือนอนุมัติ | **เสี่ยงภาษี เงียบ** | `DS:7575-7579` · `DS:22178-22197` |
| l | ลูกค้าโอนเกินโดยผิดพลาด → คืนเงิน | ไม่มีทางคืน "เงินเกิน" (คืนได้เฉพาะใบมัดจำ `refund-deposit` หรือ CN แบบคืนเงินสดของใบที่ชำระครบ) | **ไม่มีฟีเจอร์** | `DocumentController.cs:652-664` · `DS:12653-12672` |
| m | สกุลต่างประเทศ | ใบเดียว/หลายใบมี FX gain/loss · ส่วนเกินแปลงด้วยอัตราใบแรก (ไม่มีอัตราของตัวเอง) · หักมัดจำข้ามสกุล/อัตรา = บล็อก | ใช้ได้จำกัด | `DS:16015-16018` · `DS:6053-6066` |
| n | เงินของสาขา/ผู้ติดต่ออื่น (เลขภาษีเดียว คนละสาขา = คนละ Contact) | หลายใบ: ต้อง Contact เดียว = บล็อก · หักมัดจำ: Contact เดียว = บล็อก · `apply-journal-deposit`: **ไม่ตรวจเลย** | บล็อก / หลุม (P-2) | `DS:15804-15807` · `DS:6038-6039` · `DS:6446+` |
| o | ยกเลิก/ลดหนี้หลังจัดสรร | void การชำระหลายใบ: กลับ JE + คืนยอดทุกใบ + ถอย VAT รายใบ (ครบ) · ลบร่างที่มีการชำระ = บล็อก · CN ใบที่ชำระครบ = โหมดคืนเงินสด · void ใบปลายทางของมัดจำ = คืนยอดมัดจำ | ใช้ได้ | `DS:12330-12420` · `DS:10802` · `DS:12653-12672` |
| p | งวดบัญชีปิด/ภ.พ.30 ยื่นแล้ว | JE รับชำระ/ส่วนเกิน/หักมัดจำ ผ่าน `RequireOpenFiscalPeriodAsync` (throw) · ใบที่ tax point ตกงวดยื่นแล้ว = คำเตือนอนุมัติ | บล็อกชัด | `DS:16034` · `DS:6176` · `DS:22192-22197` |
| q | ใบจากรายการประจำ/Autopilot | ใบใหม่ไม่ถูกหักจากเงินรอจัดสรร/มัดจำอัตโนมัติ · เงินโอนล่วงหน้าทั้งปีเทียบใบเดือนเดียว = `None` | ค้าง | `BankReceiptAutoSettle.cs:175` |
| r | โอนก้อนเดียวถูกบันทึกซ้ำ | มือ: ไม่มีกุญแจกันซ้ำ (กันได้ทางอ้อมเมื่อใบปิดแล้ว → overpay) · ใบที่ยังค้างบางส่วนบันทึกซ้ำได้ · gateway/bank/integration มีกุญแจ `Reference` | ช่องว่าง (มือ) | `DS:15251` · `DocumentPaymentHandler.cs:45-51` · `BankReceiptSettleService.cs:386-392` |
| s | เงินเข้าก่อน ยังไม่มีใบเลยสักใบ | ต้องออกใบเสร็จมัดจำ/ขายเงินสดเอง หรือ `create-je` | ผู้ใช้ต้องรู้เอง | §1.4 · §1.5 |
| t | ลูกค้าจ่ายขาดเล็กน้อย (X+Y−ค่าโอน) | ใบ 1 partial ค้างเศษ · ไม่มี write-off ฝั่งขายนอกจาก fee | ช่องว่าง | `DS:15206` |

---

## ส่วนที่ 3 — ดีไซน์ (เล็กที่สุดที่ครบ · ต่อจากของเดิม ไม่สร้างฟีเจอร์คู่ขนาน)

### 3.0 หลักคิด
"เงินที่รับแล้วแต่ยังไม่ผูกกับใบ" มี**สถานะกลางเดียว** = **เงินรับรอจัดสรร** ซึ่งระบบมีครึ่งแรกอยู่แล้ว (ส่วนเกินของ `Payment` + `PaymentAllocation`) — ดีไซน์นี้**เติมครึ่งหลัง** (เก็บยอดคงเหลือ · จัดสรรทีหลัง · คืน · ย้ายเป็นมัดจำ) แล้วเปิดให้ทุกทางเข้าเดินมาที่เดียวกัน · **ไม่แตะกลไกมัดจำ** (เงินที่เป็นมัดจำจริง = ย้ายไปเป็นใบมัดจำ ใช้ `DepositPolicyResolver`/`apply-deposit` เดิมทั้งหมด)

สถานะกลางต้อง**อายุสั้นและมีเจ้าของ**: ทุกแถวต้องมีเหตุผล + ขึ้นคิว + แจ้งเตือนเมื่อค้าง — เพราะสำหรับบริการ เงินที่รับแล้ว = tax point เกิดแล้ว (§78/1) ปล่อยค้าง = ออกใบกำกับช้า

### 3.1 Data model
**`Payments` (เพิ่มคอลัมน์)**
| คอลัมน์ | ความหมาย |
| --- | --- |
| `ContactId uuid NULL` | เจ้าของเงิน (วันนี้ต้องเดาจากใบแรก) |
| `UnappliedAmount numeric(18,2) NOT NULL DEFAULT 0` | ยอดรอจัดสรรคงเหลือ (สกุลของการชำระ) — invariant: `Amount − Σ PaymentAllocations(มีผล) − Σ PaymentCreditMovements` |
| `UnappliedAccountId uuid NULL` | ผังที่ Cr ไว้จริงตอนรับ (ตัดออกต้องใช้ผังเดิม — บทเรียนเดียวกับ `OriginalArApAccount`) |
| `UnappliedReason varchar(40) NULL` | ชื่อ enum `UnappliedReason`: `UninvoicedSupply` (ค้างออกใบ) · `Advance` (ล่วงหน้า/มัดจำ) · `Overpaid` (จ่ายเกิน/รอคืน) · `Unknown` |
| `UnappliedNote text NULL` · `UnappliedResolvedAt timestamptz NULL` | หมายเหตุ · เวลาที่ยอดเหลือ 0 |

**`PaymentAllocations` (เพิ่มคอลัมน์)** — ใช้ตารางเดิมเป็น "การจัดสรรทีหลัง" ด้วย
`AppliedAt timestamptz NULL` (null = จัดสรรพร้อมรับเงิน) · `JournalEntryId uuid NULL` (JE ของการจัดสรรทีหลัง) · `IsLateAllocation boolean NOT NULL DEFAULT false`

**ตารางใหม่ 1 ตาราง `PaymentCreditMovements`** (ขาออกที่ไม่ใช่การตัดใบ): `Id, CompanyId, PaymentId, Kind('Refund'|'ToDeposit'|'WriteOff'), Amount, MovedAt, JournalEntryId, TargetDocumentId NULL, BankAccountId NULL, Note, CreatedBy, CreatedAt, IsDeleted` + index `(CompanyId, PaymentId)`

**ผังบัญชี**: เพิ่ม `21630 เงินรับจากลูกค้ารอจัดสรร` (ใต้ 216 "เงินมัดจำและเงินรับล่วงหน้าอื่น" — 21610/21620 ใช้แล้ว `ChartOfAccountTemplates.cs:132-134`) ในแม่แบบ + ตัวเติมบริษัทเดิม · ตัวหาเดียว `Helpers/UnappliedReceiptAccount` (รหัส 21630 → ไม่มี = **ล้มดังพร้อมปุ่มสร้างผัง** ไม่ตก prefix 217 อีก — ปิด P-1) · ฝั่งจ่าย: `11530 เงินจ่ายล่วงหน้าเจ้าหนี้รอจัดสรร` (ตรวจเลขว่างก่อน) หรือปิดส่วนเกินฝั่งจ่ายไปก่อน (คำตัดสิน D-8)

**`DatabaseMigrationHelper.ApplyMissingColumns`** (รูปแบบเดิม):
```sql
ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "ContactId" uuid NULL;
ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "UnappliedAmount" numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "UnappliedAccountId" uuid NULL;
ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "UnappliedReason" varchar(40) NULL;
ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "UnappliedNote" text NULL;
ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "UnappliedResolvedAt" timestamptz NULL;
ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "AppliedAt" timestamptz NULL;
ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "JournalEntryId" uuid NULL;
ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "IsLateAllocation" boolean NOT NULL DEFAULT false;
CREATE TABLE IF NOT EXISTS "PaymentCreditMovements" (...);
CREATE INDEX IF NOT EXISTS "IX_Payments_Company_Unapplied" ON "Payments" ("CompanyId","ContactId") WHERE "UnappliedAmount" > 0 AND NOT "IsDeleted";
```
**Backfill ครั้งเดียว (marker `r258-unapplied-backfill`)** — หลักการ F2 ข้อ 9: การชำระหลายใบเดิมที่มีส่วนเกิน: `UnappliedAmount = Amount − Σ alloc(ไม่ลบ)` เฉพาะแถวที่มี JE "เงินส่วนเกินยังไม่จัดสรร" (`Reference = PaymentNumber`, ไม่ถูกกลับ) และ JE นั้น `DepositAppliedToDocumentId IS NULL` · `UnappliedAccountId` = ผังขา Cr ของ JE นั้น · `ContactId` = ผู้ติดต่อของ `DocumentId` · แถวที่ JE ถูก `apply-journal-deposit` ไปแล้ว (P-2) = ไม่ backfill + ขึ้นรายงานตรวจข้อมูล (อ่านอย่างเดียว)

### 3.2 GL ต่อขั้น (THB · ตัวอย่าง X=10,700 Y=5,350 โอน 16,050)
| ขั้น | JE | หมายเหตุ |
| --- | --- | --- |
| 1 รับเงินเกิน | Dr ธนาคาร 16,050 / Cr ลูกหนี้ (ใบ 1) 10,700 / Cr **21630** 5,350 | ของเดิม `DS:16006-16068` เปลี่ยนตัวหาผัง + ประทับคอลัมน์ · วันที่ = วันเงินเข้า |
| 2 จัดสรรทีหลังให้ใบ 2 | Dr 21630 5,350 / Cr ลูกหนี้ (ผังที่ใบ 2 ตั้งจริง — `OriginalArApAccount`) 5,350 | วันที่ JE = วันจัดสรร (≥ วันที่ใบ 2 · งวดเปิด) · `SourceDocumentId` = ใบ 2 · `Reference = PAY-…/A{n}` (ให้ void หาเจอด้วยกติกาเดิม `DS:12330`) |
| 2b ใบ 2 เป็นบริการที่พัก VAT 21913 | + `TryReclassifyUndueOutputVatAsync` ด้วย**วันเงินเข้า** (ไม่ใช่วันจัดสรร) ผ่านตัวตัดสินงวด (ดู 3.3 ข้อ 6) | §78/1 |
| 2c ใบ 2 มี WHT และปิดยอดครบ | `TryRecognizeSalesWhtOnSettlementAsync` (ตัวเดิมของเส้นหักมัดจำ `DS:6563-6566`) หรือผู้ใช้ระบุ WHT ของการจัดสรร → Dr 11910 / Cr ลูกหนี้ | ปิดสถานการณ์ h |
| 3 คืนเงิน | Dr 21630 / Cr ธนาคาร (+ ขยับ `BankAccount.CurrentBalance`) | `PaymentCreditMovement(Refund)` · สร้าง `BankTransaction`? ไม่ — ให้จับคู่กับบรรทัดเงินออกจริง |
| 4 ย้ายเป็นมัดจำ (เงินล่วงหน้าจริง) | ออกใบมัดจำตามโหมดบริษัท โดยขา "เงินเข้า" ของใบ = **Dr 21630** แทน Dr ธนาคาร: Dr 21630 5,350 / Cr 21712 5,000 / Cr 21911 350 (โหมด VAT ทันที) | `PaymentCreditMovement(ToDeposit, TargetDocumentId)` · ใบมัดจำลงวันที่**วันเงินเข้า** (tax point §78/1) → เข้าด่านงวดยื่นแล้ว `DS:22178` ตามเดิม · หลังจากนี้คือระบบมัดจำเดิมทุกอย่าง |
| 5 ตัดเศษ (≤ เพดาน) | Dr 21630 / Cr 42xxx รายได้อื่น (หรือ Dr 54710 กรณีขาด) | `WriteOff` · เพดานเดียวกับ `BankReconciliationTolerance` (≤ ฿20 + เหตุผล) |
| void การชำระ | กลับ JE ทุกใบของ `PAY-…*` (เดิม) — **บล็อกถ้ามี movement Refund/ToDeposit ที่ยังมีผล** (ต้องยกเลิกขานั้นก่อน) | ปิดหลุมแบบ P-2 |

VAT ของขั้น 1: **ไม่ลง VAT ที่ 21630** — 21630 ไม่ใช่ที่พักรายได้ เป็นที่พัก "เงินที่ยังไม่ได้จำแนก" · หน้าที่ภาษีถูกบังคับผ่านเหตุผล + กำหนดเวลา (3.3 ข้อ 5–6) ไม่ใช่ผ่านการเดา VAT (หลักการ F2 ข้อ 3: ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ)

### 3.3 ตัวตัดสิน (pure · `Helpers/` OWNER ตัวเดียว · เรียกได้ในประโยคเดียว)
1. **`Helpers/PaymentExcessPlan.Decide(amount, fee, balanceDue, hasOtherOpenDocs, companyVatRegistered)`** → `{Excess, Options[], DefaultOption}` — ตอบว่า "เงินเกินเท่าไร เสนออะไรได้" (ผูกใบอื่น · ออกใบที่ขาด · เก็บรอจัดสรร · มัดจำ · คืน) · หน้าเว็บวาดจากลิสต์นี้ (Server computes · page displays)
2. **`Helpers/CustomerCreditAllocation.Check(credit, target, amount, applyDate, …)`** → `Refusal?` + `RuleCode` — **ตัวเดียวตัดสินความชอบของการจัดสรร** (ตอนรับเงิน · ทีหลัง · จาก bank queue): บริษัทเดียวกัน · `credit.ContactId == target.ContactId` (ข้อยกเว้นเลขภาษีเดียวคนละสาขา = คำตัดสิน D-4) · ชนิด ∈ `ArApScope.ReceivableTypes` และ ∈ `PayableDocumentTypes` · สถานะรับชำระได้ · สกุล/อัตราเดียวกับการชำระ (ต่าง = ปฏิเสธพร้อมทางไปต่อ เหมือน `DS:6053`) · `0 < amount ≤ credit.UnappliedAmount` · ไม่ `WouldOverpay(target.BalanceDue, amount)` (ใช้ `DocumentSettlementState` เดิม) · `applyDate ≥ max(วันเงินเข้า, วันที่ใบ)` · การชำระไม่ถูกยกเลิก/ไม่ใช่ของรอบโอน (`SettlementBatchId`) · ใบกำกับ e-Tax ที่ห้ามแตะตามด่านเดิม
3. **`Helpers/UnappliedReceiptAccount`** — ตัวหาผัง 21630 ตัวเดียว (ข้อ 3.1)
4. **`Helpers/UnappliedCreditBalance`** — สูตร invariant ตัวเดียว (ฝั่งเขียนและฝั่งตรวจ GL tie-out ใช้ตัวเดียวกัน — บทเรียน hash chain)
5. **`Helpers/UnappliedAging.Decide(reason, receivedAt, today, vatRegistered)`** → ระดับเตือน: `UninvoicedSupply` เกิน 3 วัน / `Unknown` เกิน 7 วัน / ข้ามสิ้นเดือน = แจ้งทีมทันที ("เงินรับแล้วยังไม่ออกใบกำกับ — บริการ: ภาษีถึงกำหนดวันรับเงิน")
6. **งวด VAT ของการจัดสรรทีหลัง** — ใช้ตัวตัดสินแบบเดียวกับการริบมัดจำ (`DepositPolicyResolver.ForfeitTaxPointDecision` `Helpers/DepositPolicyResolver.cs:804`): งวดของวันรับเงินยังไม่ยื่น/ไม่ล็อก/งวดบัญชีเปิด ⇒ ใช้วันรับเงิน · ไม่งั้น ⇒ งวดปัจจุบัน + ธง `[LATE-VAT]` + ข้อความ §89/1 · **ยกส่วนกลางออกเป็น `Helpers/LateReceiptTaxPoint` แล้วให้ทั้งสองเส้นเรียก** (ไม่เขียนสำเนา)
7. ใบที่ "ออกจากเงินรอจัดสรร" (ปุ่มออกใบที่ขาด): เซิร์ฟเวอร์เติม `doc.PaymentDate = วันเงินเข้า` ให้เอง ⇒ `TaxPointResolver` ได้ MIN ถูกโดยไม่ต้องให้ผู้ใช้รู้ (ปิดสถานการณ์ k) · วันที่ใบ = วันนี้ (ห้าม backdate) · ถ้าต่างเดือน ⇒ คำเตือนอนุมัติเดิม `DS:22178-22197` ทำงานเอง

### 3.4 Service + API (ไม่เพิ่มของลง `DocumentService.cs` — F4.7)
**`Services/Implementations/CustomerCreditService.cs`** (ใหม่ · `ICustomerCreditService`) — ถือ transaction + `FOR UPDATE` แถว Payment และใบปลายทาง (เรียง Id กัน deadlock แบบ `DS:15796-15800`) · ลง JE ผ่าน `Journal.JournalEntryBuilder` (`Services/Implementations/Journal/JournalEntryBuilder.cs`) · ขั้นหลังจัดสรร (ย้าย VAT พัก · WHT · ธงสลิป · คิวงานหลังรับชำระ) เรียก**เมธอดเดิม**ของ `IDocumentService` (เปิด `AfterPaymentRecordedAsync` แบบรับ documentId เพิ่ม 1 overload — ไม่คัดลอกลำดับ)

| Endpoint (ใต้ `api/companies/{cid}/`) | สิทธิ์ | ทำอะไร |
| --- | --- | --- |
| `GET customer-credits?contactId=&status=` | Accounting.View | คิว "เงินรอจัดสรร" + KPI + GL tie-out (21630 ใน GL vs Σ `UnappliedAmount`) |
| `GET customer-credits/contacts/{contactId}/summary` | Document.View | ยอดรอจัดสรร + มัดจำคงเหลือ (รวม `GetContactDepositSummaryAsync` เดิม) — ใช้ในฟอร์มเอกสาร/หน้าผู้ติดต่อ |
| `GET document/{id}/payment-excess-plan?amount=&fee=` | Document.View | ผล `PaymentExcessPlan` สำหรับ dialog รับชำระ |
| `POST document/payments` (เดิม) | Approve ทุกใบ (เดิม) | รับ `Allocations` + ใหม่ `ExcessReason`, `ExcessNote` (บังคับเมื่อ Σalloc < Amount) · เพิ่ม `FeeAmount` ในเส้นหลายใบ (ผูกกับการชำระ ไม่ใช่ใบ) |
| `POST customer-credits/{paymentId}/allocate` `{documentId, amount, applyDate, withholdingTaxAmount?}` | Approve ชนิดของใบปลายทาง | ขั้น 2 |
| `POST customer-credits/{paymentId}/allocations/{allocationId}/undo` | Void | กลับ JE จัดสรร + คืนยอดใบ + คืน `UnappliedAmount` |
| `POST customer-credits/{paymentId}/refund` `{amount, date, bankAccountId, note}` | Approve + Bank.Reconcile | ขั้น 3 |
| `POST customer-credits/{paymentId}/to-deposit` `{amount, depositKindId?}` | Document.Create+Approve | ขั้น 4 (เรียก `CreateDocumentAsync` + `ApproveDocumentAsync(SystemWorkflow)` แบบ `IssueForfeitTaxInvoiceAsync`) |
| `POST customer-credits/{paymentId}/issue-invoice` `{lines…}` | Document.Create | สร้าง**ร่าง**ใบแจ้งหนี้/ใบกำกับของส่วนที่ขาด (ผู้ติดต่อ/สกุล/`PaymentDate` เติมให้) + จอง "จัดสรรอัตโนมัติเมื่ออนุมัติ" (`Document.PendingCreditPaymentId` — ไม่ใช่ช่องฟอร์ม) |
| `PUT customer-credits/{paymentId}/reason` | Document.Edit | เปลี่ยนเหตุผล/หมายเหตุ (audit) |

ทุก query มี `CompanyId == companyId` · ทุกการเขียนลง `AddChainedAuditLog` พร้อม `RuleCode` (`CC-ALLOC`, `CC-REFUND`, `CC-TO-DEPOSIT`, `CC-LATE-VAT`) + `LegalReference` (§78/1 · §86 · §89/1) · ธุรกิจผิดกติกา = `BusinessRuleException` (ไม่ใช่ `InvalidOperationException`)

ปิด P-2 ในแพ็กแรก: `SearchJournalDepositsAsync`/`ApplyJournalDepositToInvoiceAsync` ต้อง**ตัดออก** JE ที่ `Reference` เป็นเลข `PAY-` ของการชำระที่มี `UnappliedAmount`/เคยมีส่วนเกิน (ให้ใช้ทางใหม่ที่ตรวจผู้ติดต่อ)

### 3.5 UI
1. **Dialog รับชำระ (`documents.html` `recordPayment` ~`:12711-13190`)** — แทน toast ที่ `:13122`: เมื่อยอด > ยอดค้าง แสดงแผง "เงินเกิน ฿Y — จะทำอย่างไร" (ตัวเลือกจากเซิร์ฟเวอร์):
   - ◉ **ผูกกับใบค้างอื่นของลูกค้ารายนี้** (ตารางใบค้าง + ช่องยอด · เติมอัตโนมัติเก่าสุดก่อน · ผู้ใช้แก้ได้ `userTouched`)
   - ○ **ออกใบแจ้งหนี้/ใบกำกับส่วนที่ขาดตอนนี้** → บันทึกเงินเป็นรอจัดสรร (เหตุผล `UninvoicedSupply`) แล้วเปิดฟอร์มเอกสารที่เติมผู้ติดต่อ + ป้าย "ใบนี้จะถูกตัดด้วยเงินที่รับเมื่อ dd/MM" + คำเตือน tax point
   - ○ **เก็บเป็นเงินรับล่วงหน้า/มัดจำ** → ขั้น 4 (แสดงพรีวิวโหมด VAT จาก `DepositPolicyResolver.PreviewReceipt` เดิม)
   - ○ **ลูกค้าโอนเกิน — รอคืนเงิน** (เหตุผล `Overpaid`)
   - ○ ยังไม่ทราบ (เหตุผล `Unknown` · เตือนว่าจะขึ้นคิวและแจ้งเตือน)
   ส่วน WHT: ถ้าลูกค้าหักจากยอดรวม ให้กรอก WHT ต่อใบที่ผูก · ส่วนของยอดที่ยังไม่มีใบ → เก็บใน `UnappliedNote` + เตือน "บันทึก WHT ได้เมื่อจัดสรรให้ใบ" (ช่อง `withholdingTaxAmount` ของ `allocate`)
2. **หน้าใหม่ `pages/customer-credits.html` "เงินรอจัดสรร"** (เมนูขาย/รายรับ ข้าง "เงินมัดจำ"): คอลัมน์ ลูกค้า · วันรับ · เลขการชำระ · ยอดคงเหลือ · เหตุผล · อายุ · ปุ่ม [จัดสรรให้ใบ] [ออกใบที่ขาด] [ย้ายเป็นมัดจำ] [คืนเงิน] · แถบ GL tie-out · ปุ่มมาจากเซิร์ฟเวอร์ (`ActionPath` แบบหน้า Autopilot)
3. **ปุ่ม "ใช้เงินรอจัดสรร ฿…" บนใบแจ้งหนี้/ใบกำกับที่ค้าง** (แถบ action `documents.html:~11476-11486` + ในฟอร์มข้างปุ่ม "หักมัดจำ" `:3913-4210`) — แสดงเมื่อ summary ของผู้ติดต่อ > 0
4. **คำเตือนตอนอนุมัติใบขาย** (`CollectApprovalWarningsAsync`): "ลูกค้ารายนี้มีเงินรอจัดสรร ฿… (รับเมื่อ …) — จัดสรรให้ใบนี้หลังอนุมัติ?" + ถ้าใบถูกจอง `PendingCreditPaymentId` = จัดสรรเองหลังอนุมัติ (ขั้นหลัง commit · ล้ม = ป้ายบนใบ + แจ้ง ไม่เงียบ)
5. **หน้าผู้ติดต่อ + statement**: บรรทัด "เงินรอจัดสรร" · statement เพิ่มแถวเครดิตจาก `Payments` (แก้ §1.8) และยอดปิด = Σ ค้าง − เงินรอจัดสรร (แสดงแยก 2 บรรทัด)
6. **หน้าธนาคาร/คิว Autopilot**: ดู 3.6
7. เอกสารที่พิมพ์: **ไม่มี field ใหม่บนกระดาษ** ในแพ็กหลัก (ไม่ต้องแตะ renderer ×2) · ใบเสร็จของการรับหลายใบ = คำตัดสิน D-6

### 3.6 Statement/feed/Autopilot
- `BankReceiptAutoSettle.Decide` เพิ่ม 2 คำตัดสิน**แบบ Suggest เท่านั้น** (ไม่มีวัน AutoSettle):
  - `SuggestSplit`: subset ของใบค้าง**ผู้ติดต่อเดียว** (≤ 6 ใบ · ชุดเดียวเท่านั้น · มีตัวระบุตัว) ที่ Σ `ExpectedNet` = ยอดบรรทัดเป๊ะ → เสนอชุด (ใช้อัลกอริทึม subset เดิมของ `WhtCertMatchPolicy.FindSubsets` ยกเป็น `Helpers/SubsetSum` ตัวเดียว)
  - `SuggestExcess`: มีตัวระบุตัวชี้ใบ/ผู้ติดต่อเดียว และยอดบรรทัด > ยอดที่ต้องโอนเกินเกณฑ์ near → เสนอ "รับเต็ม ผูกใบ … ส่วนเกิน ฿… ให้คนเลือก"
- `POST autopilot/bank-queue/{id}/confirm` รับ `allocations[] + excessReason` → `CreateMultiDocPaymentAsync` (`Reference = BANK:{lineId}` กุญแจกันซ้ำเดิม) → ผูกบรรทัด Matched กับ Payment ก้อนเดียว (ยอดบรรทัด = `Payment.Amount` ตรงกัน — ปิดสถานการณ์ c/f)
- หน้า `bank.html` บรรทัดเงินเข้า Unmatched: ปุ่ม "บันทึกเป็นเงินรับจากลูกค้า" (เลือกผู้ติดต่อ → dialog เดียวกับข้อ 3.5-1 แบบไม่มีใบตั้งต้น = รับเป็นรอจัดสรรทั้งก้อน) — แทนการใช้ `create-je` Cr รายได้/ลูกหนี้ตรง ๆ · `create-je` เพิ่มคำเตือนเมื่อผังคู่เป็นลูกหนี้ 113xx ("ใบแจ้งหนี้จะไม่ถูกปิด — ใช้ 'บันทึกเป็นเงินรับจากลูกค้า'")
- **ห้ามลงส่วนเกินเองโดยไม่มีคนเลือกเหตุผล** ทุกทางเข้าอัตโนมัติ (gateway/integration/นำเข้าไฟล์) คงปฏิเสธยอดเกินเหมือนเดิม — ข้อความ error เติมทางไปต่อ ("บันทึกที่หน้าเอกสารแล้วเลือกวิธีจัดการเงินเกิน")

### 3.7 แจ้งเตือน / ทวงหนี้
- **`Helpers/DunningSuppression.Decide(docBalanceDue, contactUnapplied, contactOpenDeposit)`** (pure): `contactUnapplied ≥ docBalanceDue` ⇒ **ไม่ทวงลูกค้า** + แจ้งทีม "ลูกค้ามีเงินรอจัดสรรพอปิดใบนี้" · `0 < contactUnapplied < balance` ⇒ ทวงได้ แต่ข้อความภายในมียอดเครดิต · เรียกจาก 2 จุด: `EmailScheduleService` (`:578-609` — บันทึกแถว "ข้าม" พร้อมเหตุแบบเดียวกับผู้ติดต่อปิดรับ) และ `OverdueDunningJob` (`:63-110`) + ล็อกใน `required_call_site_check.py`
- คีย์แจ้งเตือนใหม่ `customer_credit.unallocated_aging` (กระดิ่ง + อีเมลเจ้าของ · ตาม `UnappliedAging`) + บรรทัดในสรุปประจำวัน Autopilot ("เงินรอจัดสรร n รายการ ฿…") · งานพื้นหลัง: เพิ่มเป็น**ขั้น**ใน `AutopilotJob` (มี `JobLock` อยู่แล้ว — ไม่สร้าง `BackgroundService` ใหม่ F4.6)
- พอร์ทัลลูกค้า: แสดง "ยอดที่ชำระแล้วรอออกเอกสาร ฿…" (ลูกค้าไม่ตกใจว่าใบยังค้าง)

### 3.8 รายงาน
- AR aging (`AgingReportService`): เพิ่มคอลัมน์ต่อผู้ติดต่อ "เงินรอจัดสรร" + "สุทธิ" — **ไม่หักออกจาก bucket** (ยอดค้างรายใบยังจริงจนกว่าจะจัดสรร) · ส่งออกไฟล์ = ตัวเลขเดียวกับหน้า (กติกา §6.0a-x)
- งบดุล: 21630 = หนี้สินหมุนเวียน · รายงานตรวจข้อมูล: GL 21630 ≠ Σ `UnappliedAmount` · แถวค้างข้ามสิ้นเดือนของบริษัทจด VAT เหตุผล `UninvoicedSupply`
- ภ.พ.30: ไม่เปลี่ยนสูตร — ใบที่ออกจากเงินรอจัดสรรมี `PaymentDate`/`TaxPointDate` ถูก → เข้างวดถูกเอง

### 3.9 เทสต์ (ทั้งสองทิศ)
**Pure (`Accounting.Tests/`)** — `PaymentExcessPlanTests` · `CustomerCreditAllocationTests` · `UnappliedCreditBalanceTests` · `DunningSuppressionTests` · `UnappliedAgingTests` · `LateReceiptTaxPointTests` · `BankReceiptAutoSettleSplitTests`
- ทิศบวก: X+Y → เกิน Y · จัดสรรพอดี/บางส่วน/หลายครั้ง · subset ชุดเดียว = Suggest
- ทิศตรงข้าม (ใบที่ถูกอยู่แล้วไม่ถูกแตะ): ยอดพอดี = ไม่มีแผงเงินเกิน/ตัวเลขเดิมทุกสตางค์ · เกิน ≤ 0.01 = พฤติกรรมเดิม · ผู้ติดต่อคนละราย/สกุลต่าง/ใบ Voided/เกินคงเหลือ/วันจัดสรรก่อนวันเงินเข้า = ปฏิเสธพร้อม RuleCode · เครดิต 0 = ทวงตามเดิม · subset 2 ชุด = ไม่เสนอ · ไม่มีตัวระบุตัว = None เดิม
**Db (`Accounting.Tests/Db/CustomerCreditDbTests.cs`)**
1. รับ 16,050 ใบ 1 10,700 → 21630 = 5,350 · ธนาคาร +16,050 · `UnappliedAmount` = 5,350
2. ออกใบ 2 → allocate → ใบ 2 Paid · 21630 = 0 · ลูกหนี้ 0 · VAT พัก 21913 ย้ายด้วยวันเงินเข้า
3. allocate ซ้อนสองคำขอ = สำเร็จครั้งเดียว (lock)
4. undo allocation → ยอดกลับครบ · void การชำระที่มี Refund = 409
5. refund → ธนาคารลด · movement · คงเหลือ 0
6. to-deposit → ใบมัดจำ Dr 21630 ไม่แตะธนาคาร · `apply-deposit` ต่อได้
7. tenant: จัดสรรข้ามบริษัท = 404 · ข้ามผู้ติดต่อ = 409
8. งวดปิด = throw ไม่มีอะไรถูกเขียน
9. P-2: `apply-journal-deposit` กับ JE ส่วนเกิน = ปฏิเสธ
10. backfill: แถวเดิมได้ `UnappliedAmount` ถูก · แถวที่ถูก apply แล้วไม่ถูกแตะ
**`tools/required_call_site_check.py`** (บล็อก "รอบ 258 เงินรอจัดสรร"): `CustomerCreditService` ทุกเมธอดเขียนต้องเรียก `CustomerCreditAllocation.Check` ก่อน JE · `CreateMultiDocPaymentAsync` ต้องใช้ `UnappliedReceiptAccount` (ห้าม `"21710"`/`"217"` literal) · `EmailScheduleService` + `OverdueDunningJob` ต้องเรียก `DunningSuppression.Decide` · `BankReceiptSettleService` ห้ามเรียก `CreatePaymentAsync` ด้วยยอดบรรทัดเมื่อคำตัดสินเป็น Split/Excess · `write_permission_gate_check` เฝ้า controller ใหม่ · `html_interpolation_escape_check` เฝ้าหน้าใหม่

### 3.10 เอกสารที่ต้องขยับ (คอมมิตเดียวกับโค้ด)
`DOCUMENT_FLOW.md` §3.4 (เงินเกิน/จัดสรรทีหลัง) · §3.7 (ย้ายเป็นมัดจำ) · §2.12k (Suggest split/excess) · §2.12g-0 (ระงับทวง) · §8 quick reference · `TEST_PLAN.md` §0 · `ACCOUNT_STRUCTURE.md` (ผัง 21630 ในแม่แบบ) · `docs/REVIEWS_INDEX.md` (helper OWNER ใหม่ 6 ตัว) · `CHANGELOG.md` · `docs/ui-glossary.md` (คำว่า "เงินรอจัดสรร")

### 3.11 แพ็กงาน (เรียงตามลำดับทำ · แต่ละแพ็กส่งได้เอง)
| แพ็ก | ขอบเขต | ไฟล์หลัก | ขึ้นกับ |
| --- | --- | --- | --- |
| **P1 ฐาน + ปิดหลุมเดิม** | คอลัมน์/ตาราง/backfill · ผัง 21630 + `UnappliedReceiptAccount` (แก้ P-1) · ปิด P-2 · `UnappliedCreditBalance` · ประทับ `ContactId/UnappliedAmount` ในเส้นหลายใบเดิม · `GetPaymentsAsync` คืนยอดคงเหลือ + หาใบที่ 2+ ผ่าน allocations | `Data/DatabaseMigrationHelper.cs` · `Models/Entities/Payment.cs` · ใหม่ `Models/Entities/PaymentCreditMovement.cs` · `Data/AccountingDbContext.cs` · `Services/ChartOfAccountTemplates.cs` · ใหม่ `Helpers/UnappliedReceiptAccount.cs`, `Helpers/UnappliedCreditBalance.cs` · `DS:16006-16068`, `DS:6395-6412`, `DS:6446+`, `DS:16158+` (แก้เฉพาะจุด) · `Helpers/DocumentPurgeOrder.cs` | — |
| **P2 จัดสรรทีหลัง (แกน)** | `CustomerCreditAllocation` · `CustomerCreditService` (allocate/undo) · controller + คิว GET · ด่าน void · VAT พัก/WHT หลังจัดสรร · `LateReceiptTaxPoint` (ยกจากมัดจำ) · เทสต์ pure + Db 1–4,7–9 | ใหม่ `Helpers/CustomerCreditAllocation.cs`, `Helpers/LateReceiptTaxPoint.cs`, `Services/Implementations/CustomerCreditService.cs`, `Services/Interfaces/ICustomerCreditService.cs`, `Controllers/CustomerCreditController.cs`, `Models/DTOs/CustomerCreditDtos.cs` · `Program.cs` (DI) · `Helpers/DepositPolicyResolver.cs:804` (เรียกตัวกลาง) · `tools/required_call_site_check.py` | P1 |
| **P3 หน้าจอรับเงินเกิน** | `PaymentExcessPlan` + endpoint · dialog รับชำระ (ผูกใบอื่น/รอจัดสรร/เหตุผล) · ส่ง `allocations` จากเว็บครั้งแรก · fee ในเส้นหลายใบ · หน้า `customer-credits.html` · ปุ่ม "ใช้เงินรอจัดสรร" บนใบ · คำเตือนอนุมัติ | ใหม่ `Helpers/PaymentExcessPlan.cs`, `wwwroot/pages/customer-credits.html` · `wwwroot/pages/documents.html` · `wwwroot/js/api.js` · `Controllers/DocumentController.cs:1722-1753` · `DS:15762+` (รับ `ExcessReason`/fee) · เมนู layout | P2 |
| **P4 ออกใบที่ขาด + ย้ายเป็นมัดจำ + คืนเงิน** | `issue-invoice` (ร่าง + `PendingCreditPaymentId` + เติม `PaymentDate`) · จัดสรรเองหลังอนุมัติ · `to-deposit` (ขาเงินเข้า = 21630) · `refund` · write-off เศษ · Db 5–6 | `CustomerCreditService.cs` · `Models/Entities/Document.cs` (+1 คอลัมน์ภายใน) · จุดเลือกผังเงินเข้าของ AutoPost ใบมัดจำ (เพิ่มพารามิเตอร์เมธอด ไม่ใช่ช่อง request — แบบ `depositChannelVatRate`) · `documents.html` | P2 (ควรหลัง P3) |
| **P5 ธนาคาร/Autopilot** | `SuggestSplit`/`SuggestExcess` · `Helpers/SubsetSum` · confirm รับ allocations · ปุ่มบนบรรทัด Unmatched · คำเตือน `create-je` | `Helpers/BankReceiptAutoSettle.cs` · `Services/Implementations/BankReceiptSettleService.cs` · `Controllers/AutopilotController.cs` · `Controllers/BankController.cs:370-431` · `wwwroot/pages/autopilot.html`, `bank.html` · `Helpers/WhtCertMatchPolicy.cs` (ใช้ SubsetSum กลาง) | P3 |
| **P6 ทวงหนี้ + รายงาน + statement** | `DunningSuppression` 2 จุดเรียก · `UnappliedAging` + ขั้นใน `AutopilotJob` + คีย์แจ้งเตือน · aging/statement/พอร์ทัล/ส่งออก · รายงานตรวจข้อมูล | ใหม่ `Helpers/DunningSuppression.cs`, `Helpers/UnappliedAging.cs` · `Services/Implementations/EmailScheduleService.cs:578-609` · `Services/Background/OverdueDunningJob.cs` · `Services/Implementations/AgingReportService.cs` · `Controllers/CustomerStatementController.cs` · `Services/Implementations/PortalService.cs` · `Helpers/AutopilotNotice.cs` | P2 |

ลำดับแนะนำ: **P1 → P2 → P3** (ตอบโจทย์เจ้าของครบ: รับก้อนเดียว → ผูกใบที่ออกทีหลัง) → P6 (หยุดทวงผิด) → P4 → P5 · ทุกแพ็กแตะเงิน/ภาษี ⇒ ส่ง diff ให้ subagent ฝ่ายค้าน 1 รอบ (F3 ข้อ 11)

### 3.12 คำตัดสินที่ต้องขอเจ้าของ (พร้อมค่าแนะนำ)
| # | คำถาม | ค่าแนะนำ |
| --- | --- | --- |
| D-1 | ผังพักเงินรอจัดสรร | **21630 ใหม่ใต้ 216** (ไม่ปนกับ 21712/21713 ที่ผูกกลไกมัดจำ/รายได้รอรับรู้) · บริษัทเดิมเติมผังอัตโนมัติ |
| D-2 | เงินเกินบังคับเลือกเหตุผลไหม | **บังคับ** (4 ค่า) — `Unknown` ได้แต่ขึ้นคิว + แจ้งเตือน 7 วัน |
| D-3 | บริษัทจด VAT + เหตุผล "ล่วงหน้า/มัดจำ" | **บังคับย้ายเป็นใบมัดจำทันที** (VAT ตามโหมดบริษัท §78/1) ไม่ให้ค้างใน 21630 · "ค้างออกใบ" ให้เวลา 3 วัน/ไม่ข้ามสิ้นเดือน แล้วแจ้งเตือน (ไม่บล็อก) |
| D-4 | จัดสรรข้ามผู้ติดต่อ (เลขภาษีเดียว คนละสาขา) | **ไม่อนุญาตในรุ่นแรก** — ข้อความชี้ทาง "คืนเงินรอจัดสรรแล้วรับใหม่ที่ผู้ติดต่อที่ถูก" · เปิดทีหลังด้วยสิทธิ์เฉพาะ + audit |
| D-5 | งวด VAT ของใบบริการที่ออกหลังเงินเข้า | **วันเงินเข้า** ถ้างวดนั้นยังไม่ยื่น/ไม่ล็อก · ยื่นแล้ว = งวดปัจจุบัน + ธง `[LATE-VAT]` (กติกาเดียวกับริบมัดจำ รอบ 194 R2-1) |
| D-6 | ใบเสร็จของการรับเงินก้อนเดียวหลายใบ | **ออกใบเสร็จแยกต่อใบแจ้งหนี้** ตามนโยบาย `SettlementReceiptPolicy` เดิม (ยกเลิกข้อยกเว้น MultiDoc เฉพาะเมื่อทุกใบเป็นชนิดเดียวกัน) · ใบเสร็จรวมหลายใบ = backlog (ต้องแก้ renderer ×2) |
| D-7 | Autopilot ปิดหลายใบเองได้ไหม | **ไม่** — Suggest เท่านั้นทั้ง split และ excess (คนยืนยัน 1 คลิก) |
| D-8 | ฝั่งจ่าย (จ่ายเจ้าหนี้เกิน) | **ปิดเส้นส่วนเกินฝั่งจ่ายไปก่อน** (ปฏิเสธพร้อมทางไปต่อ) เพราะวันนี้ลงผิดผัง 11400 (P-1) — ทำสมมาตรในรอบถัดไป |
| D-9 | ระงับอีเมลทวงลูกค้า | **ระงับเมื่อเงินรอจัดสรรของลูกค้า ≥ ยอดค้างของใบ** + แจ้งทีมแทน · น้อยกว่า = ทวงตามเดิม |
| D-10 | เพดานตัดเศษ (write-off) จากเงินรอจัดสรร | **≤ ฿20 ต้องมีเหตุผล** (เกณฑ์เดียวกับ `BankReconciliationTolerance`) · เกิน = ต้องคืนเงิน/จัดสรร |
| D-11 | กันบันทึกซ้ำ (มือ) | คำเตือนก่อนบันทึกเมื่อพบการชำระของผู้ติดต่อเดียวกัน **วัน+ยอด+บัญชีเดียวกัน** ใน 3 วัน (รับทราบแล้วไปต่อได้ — ไม่บล็อก) |
| D-12 | แถวส่วนเกินเดิมที่เคยถูก `apply-journal-deposit` (P-2) | รายงานตรวจข้อมูลอ่านอย่างเดียว ให้คนตัดสินรายแถว — ไม่ migrate อัตโนมัติ |

### 3.13 สิ่งที่ตั้งใจ "ไม่ทำ"
- ไม่ยอมให้ `BalanceDue` ติดลบ/overpay บนใบ (ด่าน `WouldOverpay` คงเดิมทุกทางเข้า)
- ไม่เดา VAT ของเงินที่ยังไม่จำแนก · ไม่สร้างใบกำกับอัตโนมัติจากเงินเข้า (เลขที่ §86/4 ต้องเกิดจากคนตัดสิน)
- ไม่สร้าง subledger เครดิตลูกค้าตัวที่สองขนานกับมัดจำ — มัดจำจริงไปอยู่ระบบมัดจำ
- ไม่เพิ่ม `BackgroundService` · ไม่เพิ่มเมธอดธุรกิจลง `DocumentService.cs` (แก้เฉพาะจุดที่ระบุใน P1/P3)
