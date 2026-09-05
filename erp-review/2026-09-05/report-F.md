# ทีม F — รายงาน/แดชบอร์ด/การส่งออก ต้องเล่าเรื่องเดียวกับสมุดบัญชี ("สองความจริง")

(เขียนแบบ append ตั้งแต่ finding แรก — HEAD 444d2cb · 2026-09-05)

## Findings (ดิบ เรียงตามที่พบ — จัดลำดับ P0→P3 ตอนท้าย)

### F-01 [P1][S] แดชบอร์ดหลัก (app.html) นับใบปิดบัญชี (IsClosingEntry) เข้ารายได้/ค่าใช้จ่าย — งบกำไรขาดทุนและ simple.html ไม่นับ ⇒ ตัวเลข "รายได้เดือนนี้" คนละค่าในสองหน้าแรกของระบบ
- ไฟล์: Services/Implementations/DashboardService.cs:46-58 (KPI), :70-82 (งวดก่อน), :202-214 (RevenueTrends), :233-245 (ExpenseTrends) vs AccountingService.cs:1957-1958 (`GetSnapshotTotalsAsync` — simple.html month-snapshot), :1978-1983 (`GetProfitAndLossAsync`), :2030-2033 (Cash flow)
- โค้ด (Dashboard): `.Where(l => l.JournalEntry.CompanyId == companyId && (l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed) && l.JournalEntry.EntryDate >= fromDate && l.JournalEntry.EntryDate <= toDate).GroupBy(l => l.Account.AccountType)` — ไม่มี `!IsClosingEntry`
- โค้ด (P&L): `&& !j.IsClosingEntry // ★ C-T02: ใบปิดบัญชี ... รวมเข้ามาเมื่อไร ยอดของงวดที่ปิดแล้วกลายเป็น 0 ทันที`
- ทำไมพัง: (1) `YearEndCloseAsync` สร้าง JE ที่ Dr รายได้ / Cr ค่าใช้จ่าย ทั้งปี ลงวันสิ้นรอบ พร้อม `IsClosingEntry = true` (AccountingService.YearEndClose.cs:158-167, :203) → (2) C-T02 แก้ให้ P&L/snapshot/cash-flow ตัดใบนี้ออก แต่ **ไม่ได้ไล่ไป DashboardService** (grep `IsClosingEntry` ทั้ง Services = 4 ไฟล์: TaxFilingExport · TaxService · AccountingService ×2 — ไม่มี Dashboard/Executive/ReportBuilder) → (3) ผู้ใช้เปิด app.html เลือกช่วง "ปีนี้" หรือเดือน ธ.ค. หลังปิดปี: KPI รายได้ = 0, ค่าใช้จ่าย = 0, กราฟแนวโน้ม 6 เดือนเดือน ธ.ค. ดิ่งเป็น 0 (หรือติดลบถ้าเลือกเฉพาะ ธ.ค. ที่มีรายได้จริงน้อยกว่ายอดปิด) ขณะที่ simple.html (แหล่งเดียวกันแต่ผ่าน `GetSnapshotTotalsAsync`) และหน้า reports.html P&L โชว์ยอดจริง → (4) `revenueGrowthPercent` งวดก่อนก็เพี้ยนตาม (งวดก่อนที่คาบวันสิ้นปี)
- ผลกระทบ: ตัวเลขหัวแดชบอร์ดที่ผู้บริหารดูทุกวันผิดทั้งปีหลังปิดบัญชี และ "สองหน้าแรก" ของระบบขัดกันเอง — ผู้ใช้ไม่รู้ว่าอันไหนจริง
- defect class: "แก้ตัวเดียว เหลือที่เหลือ" (C-T02 ปิดที่ AccountingService 3 จุด แต่ตัวอ่าน JE ตาม AccountType มีอีก ≥3 service) · "Resolver กลาง ห้ามคำนวณเอง" (5 ที่เขียน filter Posted||Reversed + วันที่ + IsClosing เอง)
- ทางแก้: ยุบ predicate "รายการ GL ที่นับเป็นผลการดำเนินงาน" เป็น `Expression<Func<JournalEntry,bool>>` ตัวเดียว (แบบ `AiCallBilling.BillableRow`) ให้ Dashboard/Executive/ReportBuilder/P&L ใช้ร่วม + เทสต์ที่สร้าง closing JE แล้วยืนยันว่า Dashboard KPI = P&L
- ความมั่นใจ: สูง (grep ยืนยันว่าไม่มี IsClosingEntry ใน DashboardService; YearEndClose ติดธงจริง :203)

### F-02 [P2][S] "AR/AP ค้าง" บนแดชบอร์ด ≠ ยอดรวมรายงานอายุหนี้ โดยโครงสร้าง (ชนิดเอกสารคนละชุด · CN ไม่หัก)
- ไฟล์: DashboardService.cs:92-113 vs AgingReportService.cs:40-55, :96-98
- โค้ด (Dashboard AP): `d.DocumentType == DocumentType.PurchaseInvoice || ... Expense || ... PaymentVoucher || ... CertificateInLieu` · `arApStatuses = { Approved, Sent, PartiallyPaid, Overdue }` · `SumAsync(d => d.BalanceDue)` — ไม่มี CreditNote
- โค้ด (Aging): `// PaymentVoucher/CertificateInLieu = หลักฐานการจ่ายเงินสด ... ห้ามอยู่ใน aging (เคยมีบั๊ก voucher BalanceDue>0 จาก data ไม่ครบ → ขึ้น aging หลอกว่าค้างจ่าย)` · `var sign = doc.DocumentType == negativeType ? -1m : 1m;`
- ทำไมพัง: (1) aging ตัด PV/CIL ออกเพราะเคยมี data BalanceDue>0 หลอก — dashboard ยังรวมอยู่ ⇒ ข้อมูลเสียชุดเดียวกันโผล่บนแดชบอร์ดแต่ไม่โผล่ใน aging (2) aging หักใบลดหนี้ที่ยังไม่ apply (BalanceDue>0) เป็นลบ — dashboard ไม่หัก ⇒ AR แดชบอร์ด > AR aging เท่ายอด CN ค้าง (3) สถานะ: aging รับทุกสถานะยกเว้น Draft/Waiting/Rejected/Voided/Paid — dashboard รับเฉพาะ 4 สถานะ ⇒ ใบที่สถานะอื่น (เช่น `Sent` อยู่ทั้งคู่ แต่สถานะที่เพิ่มทีหลังจะหลุดฝั่ง dashboard)
- ผลกระทบ: ผู้ใช้กดจากการ์ด "ลูกหนี้ค้าง ฿X" ไปหน้า aging แล้วเห็น ฿Y — ไม่มีคำอธิบายว่าต่างเพราะอะไร (ไม่ใช่เงินผิด แต่เป็น "สองความจริง" ที่ผู้ใช้ต้องเดา)
- defect class: "รายการที่คัดลอกมาด้วยมือ = drift แน่นอน" (ชุด DocumentType/Status ของ AR/AP เขียนซ้ำ ≥2 ที่)
- ทางแก้: `Helpers/ArApScope` ตัวเดียว (ชนิด+สถานะ+เครื่องหมาย) ให้ Dashboard/Aging/Executive ใช้ร่วม
- ความมั่นใจ: สูง (อ่านโค้ดทั้งสองไฟล์ครบ)

### F-03 [P1][S] รายงานอายุหนี้ (AR/AP aging) ใส่ใบลดหนี้/ใบเพิ่มหนี้ **ทั้งสองฝั่ง** โดยไม่ดู `CnDnPurchaseSideOverride` — CN ที่เราออกให้ลูกค้าไปลดยอด "เจ้าหนี้" · DN ที่ผู้ขายออกมาเพิ่มยอด "ลูกหนี้"
- ไฟล์: Services/Implementations/AgingReportService.cs:50-55, :97 · เทียบ TaxService.cs:481-483 · DocumentService.cs:13254-13255, :15578
- โค้ด: `var positiveTypes = reportType == AccountsReceivable ? new[] { Invoice, TaxInvoice, BillingNote, DebitNote } : new[] { PurchaseInvoice, Expense, DebitNote }; var negativeType = DocumentType.CreditNote;` … `var sign = doc.DocumentType == negativeType ? -1m : 1m;` — ไม่มีการอ่าน `CnDnPurchaseSideOverride` หรือ `RelatedDocument` เลย (grep `CnDnPurchaseSideOverride` ทั้ง Services: OcrService · TaxService ×3 · DocumentService ×12 — **ไม่มี AgingReportService**)
- ทำไมพัง: (1) `Helpers/DocumentSide` ระบุ CN/DN เป็น `BothSides` — "ต้องรู้บทบาทก่อนถึงจะบอกฝั่งได้" และระบบมีช่อง `Document.CnDnPurchaseSideOverride` ที่ DocumentService ตั้งให้จากใบต้นทางตอนสร้าง (:1097-1098) และ TaxService ใช้ตัดสิน ภ.พ.30 → (2) aging query ดึง `allTypes = positive ∪ {CreditNote}` ทุกใบที่ `BalanceDue > 0` โดยไม่กรองฝั่ง → (3) บริษัทที่ออก CN ให้ลูกค้า (ฝั่งขาย, ยังไม่ apply) จะเห็นยอดนั้นเป็น **ลบใน AP aging** ของ contact เดียวกัน (ถ้าลูกค้ารายนั้นเป็นผู้ขายด้วย ⇒ AP ของเขาต่ำเกินจริง; ถ้าไม่ ⇒ contact โผล่ใน AP aging ด้วยยอดติดลบ) และ DN ที่ผู้ขายออก (ฝั่งซื้อ) โผล่ใน **AR aging** เป็นบวก → (4) `totals.TotalBalance` ของ AR/AP ทั้งรายงานเพี้ยนเท่ายอด CN/DN ค้างทั้งหมด
- ผลกระทบ: ค่าเผื่อหนี้สงสัยจะสูญ (TFRS NPAEs บทที่ 9 — T-18 วางแผนจะสร้างจาก AgingReportService ตัวนี้) จะคำนวณจากฐานผิด · ใบทวงหนี้/จดหมายยืนยันยอด (confirmation) ที่ดึงจาก aging ส่งยอดผิดให้ลูกค้า
- defect class: "ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้" (ถือว่าชนิดอย่างเดียวบอกฝั่งได้ ทั้งที่ helper กลางบอกว่าไม่ได้) · "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" (`CnDnPurchaseSideOverride` มี แต่ aging ไม่อ่าน)
- ทางแก้: กรอง CN/DN ด้วย `doc.CnDnPurchaseSideOverride ?? DocumentSide.IsPurchase(type)` ให้ตรงฝั่งรายงาน (เหมือน TaxService:482) · เทสต์: CN ฝั่งขาย 1 ใบ ต้องปรากฏใน AR aging เท่านั้น
- ความมั่นใจ: สูง (โค้ดชัด) — สิ่งที่ควรเช็คต่อ: CN ที่ apply แล้ว BalanceDue เป็น 0 ไหม (ถ้า CN ทุกใบถูก apply ทันที ผลกระทบจริงเล็ก)

### F-04 [P1][S] Executive Summary นับใบ **Draft และ Voided** เป็น "ใบแจ้งหนี้/เจ้าหนี้เกินกำหนด" เพราะไม่กรอง Status เลย และการ Void **ไม่ล้าง BalanceDue**
- ไฟล์: Services/Implementations/ExecutiveReportService.Summary.cs:39-48 (invoices/overdue), :95-99 (overduePay) · DocumentService.cs:1406 (`doc.BalanceDue = doc.TotalAmount` ตอนสร้าง Draft) · :7098-7100 (void)
- โค้ด (Summary): `.Where(d => d.CompanyId == companyId && !d.IsDeleted).Where(d => d.DocumentType == Invoice || TaxInvoice).Where(d => d.DocumentDate >= fromDate && d.DocumentDate <= toDate)` → `overdueInvoices = invoices.Where(i => i.BalanceDue > 0 && i.DueDate < DateTime.UtcNow)` — **ไม่มี Status filter**
- โค้ด (void): `doc.Status = DocumentStatus.Voided; doc.AgingDays = null; doc.AgingLastEvaluatedAt = DateTime.UtcNow;` — ไม่มี `BalanceDue = 0` (grep `BalanceDue` ในช่วง :7000-7300 มีแค่ :7012 คำนวณ `Total - Paid`)
- ทำไมพัง: (1) Draft ได้ `BalanceDue = TotalAmount` ตั้งแต่สร้าง → (2) Void คงค่าเดิมไว้ → (3) Summary กรองแค่ `!IsDeleted` ⇒ ใบร่างที่ยังไม่อนุมัติและใบที่ยกเลิกแล้วซึ่ง DueDate ผ่านไป ถูกนับเป็น "เกินกำหนด" ทั้งจำนวนและยอดเงิน (`overdueAmount`, `overduePay`) และ `invoiceCount` นับ Draft/Voided/Rejected ทั้งหมด → (4) หน้า executive-reports.html โชว์ยอดค้างชำระ/เกินกำหนดที่ **สูงกว่า** dashboard (ซึ่งกรอง `!= Voided && != Paid && != Draft` — DashboardService.cs:150) และ aging (ซึ่งกรอง 5 สถานะ)
- ผลกระทบ: ผู้บริหารเห็นลูกหนี้เกินกำหนดเกินจริง ตัดสินใจทวงหนี้/ตั้งค่าเผื่อจากตัวเลขที่รวมใบที่ไม่มีผลทางกฎหมาย · Aging vs Executive vs Dashboard = 3 ค่า
- defect class: "รวม Draft/Voided โดยไม่ตั้งใจ" · "ค่าที่ควรถูกล้างตอนเปลี่ยนสถานะปลายทางถูกทิ้งไว้" (ญาติของ "idempotent by skip" — ตัวชี้วัด `BalanceDue > 0` ถูกใช้แทน "ค้างจริง" ทั้งระบบ แต่ void ไม่รักษา invariant นั้น)
- ทางแก้: (ก) void → `BalanceDue = 0` (+ migration `UPDATE Documents SET BalanceDue=0 WHERE Status=6 AND BalanceDue>0`) (ข) Summary ใช้ scope เดียวกับ F-02
- ความมั่นใจ: สูง — ควรเช็คต่อ: มี void path อื่น (:8343, :8520, :12110) ที่ล้าง BalanceDue ไหม (สุ่มดู :7098 ไม่ล้าง)

### F-05 [P2][S] รายงานที่อิงเอกสารกรองแค่ `!= Voided && != Draft` ⇒ นับใบ **WaitingApproval และ Rejected** เป็นยอดขาย/ยอดซื้อ/ลูกค้าอันดับต้น
- ไฟล์: DashboardService.cs:268 (TopCustomers), :411,:418 (VAT fallback) · ExecutiveReportService.Customers.cs:21,:107,:153 · Suppliers.cs:17,:74 · Sales.cs:36,:64 · Ratios.cs:89 · ReportBuilderService.cs:360-375 (Documents source **ไม่กรองอะไรเลย** ถ้าผู้ใช้ไม่ส่ง `status`)
- โค้ด: `.Where(d => d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft)` — enum มี `WaitingApproval = 1`, `Rejected = 8` (AllEnums.cs:501,508) และมีจุดตั้ง Rejected จริง (SignatureApprovalService.cs:294, MobileApiService.cs:415)
- ทำไมพัง: ใบที่ส่งอนุมัติแล้วถูกปฏิเสธ (Rejected) หรือรออนุมัติ ไม่มี JE ไม่ใช่รายได้ แต่ถูกรวมเข้า "ยอดขาย/ลูกค้า Top 10/ยอดซื้อ Top supplier/ยอดขายตามชนิดเอกสาร" ขณะที่ P&L (JE) ไม่มี ⇒ ยอดขายฝั่ง executive > รายได้ฝั่ง P&L โดยโครงสร้าง · AgingReportService (:65-69) กรองสองสถานะนี้ออกถูกแล้ว = กติกา 2 มาตรฐานในระบบเดียว
- ผลกระทบ: P2 — ตัวเลขวิเคราะห์ (ไม่ใช่ตัวเลขยื่น) แต่ทำให้ "สองความจริง" กว้างขึ้นทุกครั้งที่มี workflow อนุมัติ
- defect class: "รายการที่คัดลอกมาด้วยมือ = drift" (allow-list สถานะเขียนซ้ำ ≥10 จุด)
- ทางแก้: `DocumentStatusScope.Recognised` (Approved/Sent/PartiallyPaid/Paid/Overdue) เป็น Expression กลางตัวเดียว
- ความมั่นใจ: สูง

### F-06 [P2][S] Custom Report Builder แหล่ง "JournalEntries" กรอง `Status == Posted` เท่านั้น ⇒ รายการที่ถูกกลับ (Reversed) หายแต่ใบกลับรายการ (Posted) ยังอยู่ = ยอด GL ในรายงานที่ผู้ใช้สร้างเองด้านเดียว
- ไฟล์: Services/Implementations/ReportBuilderService.cs:303-307 vs AccountingService.cs:1856, DashboardService.cs:48 (`Posted || Reversed`)
- โค้ด: `.Where(l => l.JournalEntry.CompanyId == companyId && l.JournalEntry.Status == JournalEntryStatus.Posted)`
- ทำไมพัง: (1) กลับรายการ = ต้นฉบับ → `Status = Reversed` (AccountingService.cs:1079) + ใบใหม่ `Posted` ที่มี `OriginalEntryId` → (2) TB/P&L/Dashboard รวมทั้งคู่ (หักกันเป็น 0) → (3) Report Builder ตัดต้นฉบับทิ้งแต่เก็บใบกลับ ⇒ บัญชีที่ถูกกลับรายการโชว์ยอด **ติดลบ** เท่ายอดเดิม · DashboardService.cs:292-293 เลือกอีกทาง (`Posted && OriginalEntryId == null`) ซึ่งถูก — 3 filter สำหรับคำถามเดียว
- ผลกระทบ: รายงานที่ผู้ใช้สร้างเองไม่ตรงกับงบทดลองในเดือนที่มีการกลับรายการ (เช่น payroll reopen · void ใบที่ JE ลงแล้ว)
- defect class: "Resolver กลาง ห้ามคำนวณเอง"
- ทางแก้: ใช้ predicate กลางเดียวกับ F-01
- ความมั่นใจ: สูง

### F-07 [P3][S] simple.html ติดป้าย "💰 เงินเข้าเดือนนี้ / 💸 เงินออกเดือนนี้" + คำอธิบาย "เริ่มจากใบกำกับ + ใบเสร็จที่อนุมัติแล้ว" แต่แหล่งคือ P&L เกณฑ์คงค้าง (JE รายได้/ค่าใช้จ่าย)
- ไฟล์: wwwroot/simple.html:100-107, :245-246 · AccountingService.cs:1950-1972 (`GetSnapshotTotalsAsync`)
- โค้ด: `// P&L = revenue (เงินเข้า) − expense (เงินออก). Easiest server-side source.` · ป้าย `เริ่มจากใบกำกับ + ใบเสร็จที่อนุมัติแล้ว`
- ทำไมพัง: ใบกำกับที่ยังไม่ได้รับเงินถูกนับเป็น "เงินเข้า" · ใบเสร็จตัดชำระ (Dr เงินสด/Cr ลูกหนี้) **ไม่** เพิ่มตัวเลขนี้ทั้งที่ป้ายบอกว่านับ · เงินเดือน/ค่าเสื่อม (JE ตรง) เข้า "เงินออก" ทั้งที่ยังไม่จ่าย — ผู้ใช้โหมดง่าย (กลุ่มที่ไม่รู้ศัพท์บัญชี) คือกลุ่มที่จะเชื่อป้ายตามตัวอักษร
- defect class: ป้ายกับแหล่งไม่ตรง (ญาติของ "สำเนามือฝั่ง JS ที่ตามหลังอยู่ไม่กี่ธง")
- ทางแก้: เปลี่ยนป้ายเป็น "รายได้/ค่าใช้จ่าย" หรือเปลี่ยนแหล่งเป็น cash-basis (Payments) ให้ตรงป้าย — ต้องเลือกอย่างตั้งใจ
- ความมั่นใจ: สูง

### F-08 [P1][M] ใบวางบิล (BillingNote) ถูกนับเป็น "ลูกหนี้" ใน Dashboard · Aging · Sub-ledger recon — แต่เป็นเอกสาร operational **ไม่มี JE** ⇒ AR ฝั่งเอกสาร > GL 11310 เสมอเมื่อใช้ใบวางบิล และซ้ำกับใบแจ้งหนี้ที่มันครอบ
- ไฟล์: DashboardService.cs:100-101 · AgingReportService.cs:51 · SubLedgerReconciliationService.cs:69-72 · DocumentService.cs:14407 (`// Operational documents (Quotation, DeliveryNote, BillingNote, PR, PO) → no JE`), :9150-9155 (`QT/BN ไม่ตั้งหนี้`), :15225 (UI ถือเป็น "Settlement-bearing receivable"), :1406 (Draft ได้ `BalanceDue = TotalAmount`)
- โค้ด (aging): `? new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.BillingNote, DocumentType.DebitNote }`
- ทำไมพัง: (1) BN ถูกสร้าง/อนุมัติ → ไม่มี JE (ถูกต้องตามหลัก — ใบวางบิลเป็นแค่จดหมายแจ้งยอด) แต่ `BalanceDue = TotalAmount` และ Status = Approved/Sent → (2) ทุกรายงานที่นิยาม AR = "เอกสารในลิสต์ที่ BalanceDue>0" นับ BN เป็นลูกหนี้ → (3) ถ้า BN ครอบ Invoice ที่ Approved อยู่แล้ว = ยอดเดียวกันถูกนับ 2 ครั้ง (Invoice + BN) → (4) `SubLedgerReconciliationService` ที่ตั้งใจให้นักบัญชี "กระทบยอดลูกหนี้กับ GL" จะรายงานผลต่างเท่ายอด BN ทุกเดือน ⇒ เครื่องมือกระทบยอดฟ้องผลต่างที่ไม่มีทางแก้ (ไล่แล้วไม่เจอ)
- ผลกระทบ: AR บนแดชบอร์ด/aging สูงเกินจริง · เครื่องมือ recon ใช้ไม่ได้กับบริษัทที่วางบิล (ธุรกิจ B2B ไทยส่วนใหญ่วางบิลก่อนรับเช็ค)
- defect class: "ค่า default ที่แต่งขึ้น" (`BalanceDue = TotalAmount` กับเอกสารที่ไม่ใช่หนี้) · "สองความจริง" (นิยาม AR ฝั่งเอกสาร ≠ ฝั่ง GL โดยโครงสร้าง)
- ทางแก้: BN ไม่ควรมี BalanceDue (=0 หรือ null) หรือ scope AR กลางต้องตัด BN ออก · เทสต์: บริษัทที่มี Invoice 1 ใบ + BN ครอบ 1 ใบ → AR aging ต้องเท่า GL 11310
- ความมั่นใจ: กลาง-สูง — ต้องเช็คต่อ: ตอนแปลง BN→Invoice/Receipt ฝั่ง source ถูกตั้ง BalanceDue=0 ไหม (DocumentService.cs:8725,8783 คำนวณ `Total - Paid` ซึ่งไม่ลดถ้า PaidAmount ไม่ถูกเขียน) และผู้ใช้จริงสร้าง BN แบบ standalone (ไม่ convert) บ่อยแค่ไหน

### F-09 [P2][M] นิยาม "ลูกหนี้/เจ้าหนี้ค้าง" มี **≥5 สำเนา** ที่ต่างกันทีละนิด — ตารางเทียบ
| ที่ | ชนิด AR | ชนิด AP | สถานะที่ตัด | CN หัก? |
|---|---|---|---|---|
| DashboardService:92-113 | Inv/TIV/BN/DN | PI/Exp/**PV/CIL** | allow {Approved,Sent,PartiallyPaid,Overdue} | ไม่ |
| AgingReportService:50-70 | Inv/TIV/BN/DN | PI/Exp/DN (ห้าม PV/CIL) | deny {Draft,Waiting,Rejected,Voided,Paid} | ใช่ (ไม่ดูฝั่ง — F-03) |
| ExecutiveReportService.Summary:32-33 | **GL 113*** (รวม 11320 ลูกหนี้อื่น · 11330 กรรมการ · 11340 gateway) | **GL 212*** | — | — |
| ExecutiveReportService.Summary:39-48,95-99 | Inv/TIV | PI/Exp/CIL | **ไม่กรองเลย** (F-04) | ไม่ |
| SubLedgerReconciliationService:60-72,100-107 | Inv/TIV/BN/DN | PI/Exp/**PV** | deny {Draft,Voided,Rejected} (รวม Waiting) | ? |
- ผลกระทบ: หน้าเดียวกัน 3 การ์ดให้ 3 ค่า; ผู้ใช้ไม่มีทางรู้ว่าอันไหน "จริง" — และทุกครั้งที่มีคนเพิ่มสถานะ/ชนิดใหม่ (เช่น `Rejected` ที่เพิ่มทีหลัง) สำเนาบางตัวตามไม่ทัน
- defect class: "รายการที่คัดลอกมาด้วยมือ = drift แน่นอน แค่รอเวลา" (CLAUDE.md ระบุกลไกแก้ไว้แล้ว: แหล่งเดียว runtime)
- ทางแก้: `Helpers/ReceivableScope` / `PayableScope` เป็น `Expression<Func<Document,bool>>` + ตัวเลข sign ต่อชนิด ให้ 5 ที่นี้เรียก · เทสต์ reflection ว่าไม่มี service ไหนเขียน allow-list ของ DocumentType เอง (checker `arap_scope_check.py` — negative test = โค้ดปัจจุบัน)
- ความมั่นใจ: สูง (อ่านครบทั้ง 5 จุด)

### F-10 [P3][S] การ์ด "VAT ต้องนำส่ง" บนแดชบอร์ดอ่านจาก `TaxReports` ที่ generate ค้างไว้ โดยไม่ refresh — tax.html เรียก `auto-refresh` 2 เดือนทุกครั้งที่เปิด ⇒ สองหน้าเห็นยอดต่างกันจนกว่าผู้ใช้จะเปิดหน้าภาษี
- ไฟล์: DashboardService.cs:384-402 · wwwroot/pages/tax.html:222 (`await api.autoRefreshTaxReports(2)`) · TaxService.cs:3554 (`AutoRefreshReportsAsync` regenerate เฉพาะที่ไม่ Filed)
- ทำไมพัง: แดชบอร์ดเลือก "ให้ตรงกับยอดที่ยื่นจริง" (ถูกสำหรับเดือนที่ Filed) แต่เดือนปัจจุบันที่ยังเป็น Draft ยอดคือ snapshot ณ ครั้งสุดท้ายที่ใครสักคนเปิด tax.html — เอกสารที่อนุมัติหลังจากนั้นไม่เข้า; fallback (คำนวณจากเอกสาร) ทำงานเฉพาะเมื่อ **ไม่มี** รายงานเลย
- ทางแก้: เดือนที่ยังไม่ Filed ให้ใช้ `TaxGlReconciliationService`/live compute หรือแสดง "ณ วันที่ generate" ข้างตัวเลข
- ความมั่นใจ: สูง (อ่านโค้ด) — ผลกระทบเป็น UX/ความน่าเชื่อถือ ไม่ใช่ยอดยื่นผิด
