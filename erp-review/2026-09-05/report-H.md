# ทีม H — เงินเดือน/HR และโมดูลรอง: ความต่อเนื่องของข้อมูลระหว่างโมดูลกับบัญชี

> HEAD ที่ตรวจ: `a054340` · branch `claude/erp-system-review-team-660mev` · อ่านโค้ดอย่างเดียว (ไม่มี .NET SDK)
> อ่านก่อนเริ่ม: BRIEF.md (รวมรอบ 2) · ERP_REVIEW_2026-09-05.md §0–§3 · SYSTEM_REVIEW_2026-09.md §6 (ทีม D) §8 (ทีม H) §9 · `git log -40`
> ไฟล์นี้เขียนแบบ append ตั้งแต่ finding แรก — ส่วน "สรุป" อยู่ท้ายไฟล์

## Findings (เรียง P0→P3 — เลขรหัสเรียงตามลำดับที่พบ ไม่ใช่ตามความรุนแรง)

### H-01 [P0][S] ยกเลิกรอบเงินเดือนที่ **นำส่ง สปส. ไปแล้ว** ได้ — กลับ JE จ่าย แต่ไม่แตะ JE นำส่ง ⇒ 21815 ติดลบถาวร
- ไฟล์: `Accounting/Services/Implementations/PayrollService.cs:2753-2830` (VoidPayrollAsync) เทียบ `:2920` (ReopenPaidRunAsync) และ `Accounting/Helpers/PayrollRunEditPolicy.cs:45-66` · `Accounting/Controllers/PayrollController.cs:596-612`
- โค้ด: Void ตรวจแค่ `if (run.Status == "Voided") throw …` แล้วเข้า transaction → `if (run.Status == "Paid" && run.JournalEntryId.HasValue) ReverseJournalEntryAsync(run.JournalEntryId)` → `run.Status = "Voided"` — **ไม่มีบรรทัดไหนอ่าน `run.SsoSettledAt` / `SsoSettlementJournalEntryId`** · ขณะที่ Reopen เรียก `PayrollRunEditPolicy.CanReopen(run.Status, run.SsoSettledAt)` ซึ่งเขียนเหตุผลไว้ชัด: *"นำส่ง สปส. แล้ว = มี JE ก้อนที่สอง (Dr 21815 / Cr Bank) … กลับรายการจ่ายโดยไม่แตะ JE ก้อนนั้น จะเหลือหนี้สิน 21815 ที่ถูกล้างไปแล้วทั้งที่ต้นทางหายไป"* · controller `VoidRun` มีด่านสิทธิ์ (`RequirePayrollWriteAsync … PayrollPay`) แต่ไม่มีด่านสถานะ
- ทำไมพัง: (1) จ่ายเงินเดือน → JE#1 Cr 21815 = X → (2) นำส่ง สปส. (`SettleSsoAsync` :4040-4200 หรือ `StatutoryRemittanceService.RemitAsync` :697-810) → JE#2 Dr 21815 X / Cr ธนาคาร X + `SsoSettledAt` stamp → (3) HR กด "ยกเลิกรอบ" → Void กลับ JE#1 (Dr 21815 X กลับ) แต่ JE#2 ยังอยู่ ⇒ 21815 คงเหลือ **Dr X** (หนี้สินติดลบ) และเงินธนาคารออกไปโดยไม่มีหนี้ต้นทาง · แถว `StatutoryRemittance` (ถ้านำส่งผ่านหน้ารวม) ยังบอกว่า "นำส่งแล้ว" ทั้งที่รอบถูกยกเลิก → dashboard นำส่งไม่ฟ้องอะไร
- ผลกระทบ: งบดุลผิด (หนี้สินติดลบ) · ยอดที่ยื่น สปส.1-10 จริงกับยอดในระบบไม่ตรงโดยไม่มีสัญญาณ · ถ้าสร้างรอบใหม่แล้วจ่าย+นำส่งอีก = นำส่งซ้ำงวดเดียวกัน (RemitAsync กันซ้ำด้วยตาราง remittance แต่ `SettleSsoAsync` กันด้วย `run.SsoSettledAt` ของ**รอบใหม่**ซึ่งเป็น null)
- defect class: "ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี" (Reopen มีด่าน Void ไม่มี — สองทางเข้าที่ทำลายข้อมูลชุดเดียวกัน) · "ด่านสถานะที่เขียนว่า == X เป๊ะ ๆ" (Void ตรวจแค่ Voided)
- ทางแก้: เพิ่ม `PayrollRunEditPolicy.CanVoid(status, ssoSettledAt)` ใช้เหตุผลเดียวกับ CanReopen แล้วเรียกใน `VoidPayrollAsync` ก่อน transaction (บังคับให้ "กลับรายการนำส่ง สปส." ก่อน) · เทสต์: void run ที่ `SsoSettledAt != null` ต้อง throw
- ความมั่นใจ: **สูง** (อ่านทั้งสองเมธอดครบ — ไม่มี call site ของ CanReopen/SsoSettledAt ใน Void)

### H-02 [P1][M] Void/Reopen รอบเงินเดือนไม่ยกเลิก "รายการนำส่ง ภ.ง.ด.1" ที่ทำไปแล้ว — และ `StatutoryRemittance` ไม่มีทางกลับรายการเลย (สถานะปลายทาง)
- ไฟล์: `Accounting/Services/Implementations/StatutoryRemittanceService.cs:697-810` (RemitAsync) · public method ทั้งไฟล์ = GetDashboard/GetFilingCalendar/Preview/Remit/RecognizePp36/GetPp36Docs/AttachReceipt — **ไม่มี Reverse/Delete** · `PayrollService.cs:2920` CanReopen ดูแค่ `SsoSettledAt`
- โค้ด: `"WhtPnd1" => ("ภาษีหัก ณ ที่จ่าย (เงินเดือน)", "ภ.ง.ด.1", "21914")` · RemitAsync ล้าง Dr 21914 / Cr ธนาคาร แล้วบันทึกแถว `StatutoryRemittance` — **ไม่ stamp อะไรบน PayrollRun** (stamp เฉพาะ `SsoSps110` :793-806) · dashboard :84-100 คิดหนี้ ภ.ง.ด.1 จาก `runs.Where(Status == "Paid")` แล้ว net ด้วยแถว remittance
- ทำไมพัง: จ่ายเงินเดือน (Cr 21914 = W) → นำส่ง ภ.ง.ด.1 ผ่านหน้ารวม (Dr 21914 W) → HR กด "กลับรายการจ่าย" (ผ่านได้ เพราะ CanReopen ไม่รู้เรื่อง ภ.ง.ด.1) → แก้ WHT รายคน → จ่ายใหม่ Cr 21914 = W′ ⇒ 21914 ค้าง W′−W ตลอดไป · dashboard: หนี้ = W′ · นำส่งแล้ว = W → แสดง "ค้าง W′−W" แต่ **ไม่มีปุ่มนำส่งส่วนต่าง** (RemitAsync :704-709 กันซ้ำงวดเดิม `dup → throw "นำส่งไปแล้ว"`) และไม่มีปุ่มยกเลิกรายการนำส่งเดิม
- ผลกระทบ: หนี้สินภาษีค้างจ่ายค้างในงบ · ถ้ายื่นแบบเพิ่มเติม (ภ.ง.ด.1 เพิ่มเติม) จริง ระบบบันทึกไม่ได้ · ผู้ใช้ติดที่ "นำส่งไปแล้ว — ดูในประวัติ" โดยไม่มีทางไปต่อ
- defect class: "สถานะปลายทางที่ผู้ใช้ไปต่อไม่ได้ = ฟีเจอร์ที่ยังไม่จบ" · "ด่านที่เพิ่งสร้างต้องไล่ให้ครบทุกทางเข้า" (CanReopen รู้จักแค่ สปส.)
- ทางแก้: (ก) `ReverseRemittanceAsync(remittanceId, reason)` กลับ JE + soft-delete แถว + ล้าง stamp บน run (ใช้ pattern เดียวกับ `ReverseSsoSettlementAsync` :4214-4330) (ข) CanReopen/CanVoid ต้องดูแถว remittance `WhtPnd1` ของงวดนั้นด้วย หรืออนุญาต "นำส่งเพิ่มเติม" (ยกเลิก dup-check เป็น "รวมยอดที่นำส่งแล้วต้องไม่เกินหนี้")
- ความมั่นใจ: สูง (grep ทั้งไฟล์ไม่มี Reverse/Delete; CanReopen รับพารามิเตอร์แค่ 2 ตัว)

### H-03 [P1][M] Import รอบเงินเดือน: `SalaryAdvance` ที่ระบบนอกหักไว้ ถูกยุบเข้า `OtherDeductions` ⇒ JE ลดค่าใช้จ่ายเงินเดือน (contra 541) แทนที่จะล้างลูกหนี้เงินทดรอง (115x)
- ไฟล์: `PayrollService.cs:1219` · `:2362-2389` (JE ขา Cr ของ Loan/Other) · เทียบ `:2392-2434` (advance recovery ที่ Cr บัญชี "ทดรอง")
- โค้ด: `OtherDeductions = line.OtherDeductions + line.SalaryAdvance,` (:1219) · ตอนจ่าย: `if (otherDeductTotal > 0 && salaryAccount != null) lines.Add(new JournalLineRequest(salaryAccount.Id, 0, otherDeductTotal, "รายการหักอื่นจากพนักงาน (ลดค่าใช้จ่ายเงินเดือน)"));` · และ Loan: `if (loanAcc != null) … else otherDeductTotal += loanDeductTotal;`
- ทำไมพัง: (1) เบิกเงินทดรองผ่าน `SalaryAdvanceService.DisburseAsync` :268-330 → PV **Dr 115x ทดรอง / Cr ธนาคาร** (ถูก) → (2) ระบบ payroll ภายนอก (TakeTime ฯลฯ) หักคืนแล้วส่ง `salaryAdvance = A` มาใน import → (3) A ถูกบวกเข้า OtherDeductions → JE ตอนจ่าย **Cr 541 เงินเดือน A** (ค่าใช้จ่ายต่ำกว่าจริง A) ขณะที่ 115x ยังค้าง A ตลอดไป และ `SalaryAdvance.OutstandingAmount` ไม่ลด (import ไม่เรียก `ApplyRepayment`) ⇒ รอบถัดไป advance-recovery ในตัว (:2392-2420) จะ **หักซ้ำ** จาก NetPay อีก A · (4) Loan ที่ไม่มีผัง "เงินกู้พนักงาน" ก็ตกทางเดียวกัน = สินทรัพย์เงินกู้ไม่ลด แต่ค่าใช้จ่ายเงินเดือนลด
- ผลกระทบ: กำไรสูงเกินจริง (ค่าใช้จ่ายเงินเดือนต่ำ A) · ลูกหนี้พนักงานค้างปลอม · พนักงานถูกหักซ้ำสองรอบ · ภ.ง.ด.50 ผิด
- defect class: "ห้าม reuse field ผิดความหมาย" (`SalaryAdvance` ≠ `OtherDeductions` — ผูกกับผังคนละหมวด) · "ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้" (ไม่มีผัง loan → เอาไปลดค่าใช้จ่าย)
- ทางแก้: เก็บ `PayrollDetail.SalaryAdvanceDeduction` แยก (field ใหม่) → ตอนจ่าย Cr บัญชีทดรองตัวเดียวกับ `SalaryAdvanceService.FindAdvanceReceivableAccountAsync` + `ApplyRepayment` กับ advance ที่ค้างของพนักงานคนนั้น · Loan ไม่มีผัง → **throw ชี้ให้สร้างผัง** (เหมือน `ReqAcct`) ไม่ใช่ลดค่าใช้จ่าย
- ความมั่นใจ: สูง (บรรทัดเดียวชัด) — ต้องเช็คต่อ: import path ของ `ImportPayrollRunAsync` มี call site จริงจาก connector ไหนบ้าง (`/api/v1`? TakeTime?) เพื่อประเมินความถี่

### H-04 [P1][S] เงินทดรองที่ถูกหักตอน "จ่าย" (`AdvanceRecovered`) ไม่โผล่บนสลิป/แจ้งเตือน/หน้าจอ — สลิปบอก NetPay แต่เงินเข้าบัญชีน้อยกว่า
- ไฟล์: `PayrollService.cs:2400-2421` (คำนวณ recovery หลัง NetPay ถูกตรึงแล้ว) · `:2437` `cashPaid = run.TotalNetPay - totalAdvanceRecovered` · `:2472` `var net = d.NetPay - d.AdvanceRecovered` · `:2583-2596` แจ้งเตือนพนักงาน `message: $"ยอดสุทธิ {det.NetPay:N2} บาท เข้าบัญชีของท่านแล้ว"` · `PdfGenerationService.Payslip.cs:129,137` พิมพ์ `d.TotalDeductions` และ `d.NetPay` · `:1470-1471` `TotalDeductions = SSO+WHT+PVD+Loan+Other` (ไม่มี AdvanceRecovered)
- โค้ด: `grep -rn AdvanceRecovered Accounting --include=*.cs --include=*.html` นอก PayrollService.cs / Payroll.cs / Migration = **0 จุด**
- ทำไมพัง: (1) คำนวณรอบ → NetPay = N (ยังไม่รู้เรื่องเงินทดรอง) → สลิป/หน้าจอ/อนุมัติ เห็น N → (2) กด "จ่าย" → ระบบหักเงินทดรอง A เงียบ ๆ (`detail.AdvanceRecovered = A`) → Cr ธนาคาร N−A → (3) พนักงานได้ LINE ว่า "ยอดสุทธิ N เข้าบัญชีแล้ว" แต่เงินเข้า N−A · สลิป PDF พิมพ์ N · ไฟล์ สปส./ภ.ง.ด. ไม่กระทบ (ถูก) แต่ **ไม่มีที่ไหนบอก A เลย** · ผู้อนุมัติอนุมัติยอดที่ไม่ใช่ยอดที่จ่ายจริง
- ผลกระทบ: HR ตอบพนักงานไม่ได้ว่าทำไมเงินขาด · กระทบยอดธนาคารกับสลิปไม่ตรง · ผิดหลัก "เอกสารที่ผู้ใช้เห็น = สิ่งที่เกิดจริง"
- defect class: "เก็บแล้วต้อง echo กลับ" (field ถูก persist แต่ไม่มีใครแสดง) · "การกระทำที่ผลไปโผล่ที่อื่น ต้องบอก"
- ทางแก้: หักเงินทดรองตอน **คำนวณ** (เป็นบรรทัดหักบนสลิป) ไม่ใช่ตอนจ่าย; ถ้าคงไว้ตอนจ่าย ต้องพิมพ์ "หักคืนเงินทดรอง" บนสลิป + ข้อความแจ้งเตือนใช้ `NetPay − AdvanceRecovered` + แสดงในโมดัลรอบ
- ความมั่นใจ: สูง

### H-05 [P1][M] เงินสดย่อย: เติมเงิน (Replenish) **ไม่มี JE เลย** · จ่ายออกสร้าง JE ตรง (`new JournalEntry … Status = Posted`) ไม่ผ่าน Builder/เลขรัน/ด่านงวดปิด · ไม่มี void
- ไฟล์: `Accounting/Services/Implementations/PettyCash/PettyCashService.cs:97-129` (Disburse JE) · `:135-155` (Replenish) · `Accounting/Controllers/SmeOperationsController.cs:18,44,60` (`[Authorize]` ระดับคลาสเท่านั้น)
- โค้ด: Disburse: `EntryNumber = $"JV-PC-{txnDate:yyyyMMdd}-{Guid.NewGuid().ToString()[..4].ToUpper()}", … Status = JournalEntryStatus.Posted, … _db.JournalEntries.Add(je);` · Replenish: `fund.CurrentBalance += amount; … _db.PettyCashTransactions.Add(txn); await _db.SaveChangesAsync(ct); return txn;` — ไม่มี JournalEntry/Document ใด ๆ
- ทำไมพัง: (1) ตั้งกอง 5,000 → เติมเงินจากธนาคาร → `fund.CurrentBalance = 5,000` แต่ GL ผังที่ผูก (`LinkedAccountId`) = 0 และธนาคารใน GL ไม่ลด (2) จ่ายออก 800 พร้อมเลือกบัญชีค่าใช้จ่าย → JE Dr ค่าใช้จ่าย / Cr เงินสดย่อย 800 ⇒ GL เงินสดย่อย = **−800** ตั้งแต่รายการแรก และห่างจาก `CurrentBalance` (4,200) เท่ายอดเติมสะสมตลอดไป (3) JE ที่สร้างตรง: เลขรันสุ่ม 4 ตัว (ชนได้ ไม่ต่อเนื่อง) · ไม่ตรวจ `FiscalPeriod.Status == Closed` (เส้น `AccountingService.CreateJournalEntryAsync` ตรวจ ตามคอมมิต 802f149 — เส้นนี้ข้าม) · ไม่ตรวจว่า `expenseAccountId` เป็นของ `companyId` (ส่ง id ผังของบริษัทอื่นได้ → JE ข้าม tenant ที่ระดับ line) · `TotalDebit/TotalCredit` เซ็ตเอง (4) ไม่มี void/ลบรายการ — จ่ายผิดต้องแก้ GL มือ (5) endpoint ไม่มี `RequirePermission` (สมาชิกทุกคนเติม/จ่ายเงินสดย่อยได้) และ `SmeOperationsController` ไม่อยู่ใน allow-list ของ `tools/write_permission_gate_check.py:31-36` (บทเรียน MeteringController ซ้ำ)
- ผลกระทบ: GL เงินสดย่อยติดลบ/ไม่ตรงกองจริงทุกบริษัทที่ใช้ · ลงงวดปิดได้ · ด่านสิทธิ์ขาด
- defect class: C-04 (JE นอก Builder — จุดนี้อยู่ใน 50 จุดหรือไม่ต้องเช็ค แต่ **Replenish ไม่มี JE เลย** เป็นคนละคลาส = E-07) · "checker allow-list ต้องถามว่าไฟล์ที่แตะอยู่ในลิสต์ไหม"
- ทางแก้: Replenish → สร้าง `PaymentVoucher` ผ่าน `IDocumentService` (Dr เงินสดย่อย / Cr ธนาคาร) เหมือน `SalaryAdvanceService` · Disburse → `JournalEntryBuilder` + `SequenceNumber` + ด่านงวด + ตรวจ tenant ของผัง · เพิ่ม `SmeOperationsController` เข้า allow-list checker + `RequirePermission`
- ความมั่นใจ: สูง

### H-06 [P1][M] เช็คเด้ง (Bounced) / ยกเลิกเช็ค ไม่กลับ Payment/JE ที่ผูกไว้ — เงินในบัญชีและ AR/AP ค้างเป็นค่าที่ไม่เคยเกิด
- ไฟล์: `Accounting/Services/Implementations/Cheque/ChequeService.cs:221-256` · `:160-219` (MarkCleared) · `Accounting/Controllers/ChequeController.cs:11,79,93` (`[Authorize]` ระดับคลาส · ไม่มี RequirePermission)
- โค้ด: `MarkBouncedAsync`: `cheque.Status = ChequeStatus.Bounced; cheque.BounceReason = reason; await _db.SaveChangesAsync(ct); _logger.LogWarning(...)` — ไม่แตะ `Payments`/`JournalEntries`/`BankAccount.CurrentBalance` · `VoidAsync` เหมือนกัน · `grep 'Bounced|ChequeStatus' DocumentService*/Payment*` = 0 (ไม่มีใครฟังเหตุการณ์ `cheque.bounced` ในโค้ดบัญชี)
- ทำไมพัง: (1) รับเช็คลูกค้า → `RecordInboundAsync` ผูก `PaymentId` ที่ `CreatePaymentAsync` ลง JE Dr ธนาคาร / Cr AR + ปรับ `CurrentBalance` ไปแล้ว (คอมเมนต์ :180-183 ยืนยัน) → (2) เช็คเด้ง → สถานะเป็น Bounced อย่างเดียว ⇒ **AR ยังถูกล้าง · ธนาคารยังบวก · ใบแจ้งหนี้ยัง Paid** ⇒ ลูกหนี้จริงหายจาก aging · รายงานเงินสดเกินจริง · ถ้าเป็น VAT ณ วันรับเงิน (บริการ §78/1) tax point ที่รับรู้ไปแล้วไม่ถูกถอย (3) เช็คจ่ายที่ Void หลัง Issued: Payment (Dr AP / Cr ธนาคาร) ยังอยู่ · MarkCleared สำหรับเช็คที่ไม่มี Payment ปรับ `CurrentBalance` ตรง **โดยไม่มี JE** (:205-209) = สองความจริง (D-05 ญาติกัน)
- ผลกระทบ: เงิน/ลูกหนี้/เจ้าหนี้ผิด · เช็คเด้งเป็นเหตุการณ์ปกติของ SME ไทย
- defect class: "สถานะปลายทางที่ไปต่อไม่ได้" (Bounced ไม่มีขั้นต่อ) · "ห้าม silent no-op" (บอกว่าเด้งแล้วแต่บัญชีไม่เปลี่ยน)
- ทางแก้: Bounce/Void เช็คที่มี `PaymentId` → เรียก `IDocumentService.VoidPaymentAsync` (มีอยู่แล้วใน DocumentController) ในธุรกรรมเดียว + ตั้งใบแจ้งหนี้กลับเป็นค้างชำระ + ค่าธรรมเนียมเช็คคืนเป็นเอกสารแยก · เช็คไม่มี Payment → บังคับสร้าง Payment ก่อน Clear · ใส่ `RequirePermission` (ChequeController ก็ไม่อยู่ใน allow-list checker)
- ความมั่นใจ: สูง (อ่านครบทั้งไฟล์ 269 บรรทัด)

### H-07 [P1][S] ทิปจาก POS ถูกเครดิตเข้า **21610 "เงินมัดจำรับ"** ทุกบริษัทที่ใช้ผังมาตรฐาน — เพราะ fallback `StartsWith("216")` ไปโดนบัญชีมัดจำ · และตัวจ่ายทิป (`TipPayoutService`) ไม่มี call site + หาผัง "2160"/"1011" ที่ไม่มีในผังมาตรฐาน
- ไฟล์: `Accounting/Services/Implementations/PosService.Orders.cs:1302-1320` · `Accounting/Services/ChartOfAccountTemplates.cs:122-124` · `Accounting/Services/Implementations/Payroll/TipPayoutService.cs:84-100,116-135`
- โค้ด (POS): `var tipAccount = … AccountCode == "2160") ?? … a.AccountCode.StartsWith("216") && a.Level >= 4) ?? … StartsWith("21") && AccountName.Contains("ทิป")` · ผังมาตรฐาน: `new("216", "เงินมัดจำและเงินรับล่วงหน้าอื่น…")` · `new("21610", "เงินมัดจำรับ", "Deposit Received", AccountType.Liability, 4)` · `new("21620", "เงินค้ำประกัน", …)` — **ไม่มี "2160"** และไม่มี "1011x" (เงินสดของผังมาตรฐานคือ 11111) · TipPayout: `AccountCode == "2160"` / `AccountCode.StartsWith("1011")` → `throw "ผังบัญชี 2160 (ทิปค้างจ่าย) หรือ 1011 (เงินสด) ไม่พบ"` · `grep -rn DistributeAndPayoutAsync Controllers wwwroot` = **0**
- ทำไมพัง: (1) ลูกค้าให้ทิป 100 บนบิล POS → JE ขาย Cr 21610 100 (fallback ตัวที่ 2 ผ่านเพราะ 21610 ขึ้นต้น 216 และ Level 4) → (2) ยอดในบัญชี "เงินมัดจำรับ" โตขึ้นทุกวันโดยไม่มีเอกสารมัดจำรองรับ → รายงานกระทบยอดมัดจำ (sub-ledger `IsDeposit` ↔ 21610) ไม่ตรงตลอด · หนี้สินเงินมัดจำในงบดุลเกินจริง (3) จะจ่ายทิปให้พนักงานก็ไม่มีหน้าจอ และถึงมีก็ throw เพราะผังไม่ตรง → ทิปค้างใน 21610 ถาวร (4) SYSTEM_REVIEW §8 แถว POS ระบุ "JE/COGS/tip ถูก" — **ผลตรวจรอบนี้ขัด**: ทิป "ถูก" เฉพาะบริษัทที่สร้างผัง 2160 เองด้วยมือ
- ผลกระทบ: งบดุล/ทะเบียนมัดจำผิด · เงินทิปของพนักงานหายเข้าบัญชีที่มีความหมายอื่น · ถ้าอนาคตมีคน "ล้าง 21610" ตาม sub-ledger จะล้างทิปทิ้ง
- defect class: "เลขผังบัญชี hardcode ชนความหมายผังมาตรฐาน" (`tools/gl_code_check.py` มีอยู่แต่ไม่รู้จัก 2160/1011 — ควรเพิ่ม negative test) · "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" (TipPayoutService) · "ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้" (fallback prefix ทับหมวดอื่น)
- ทางแก้: เพิ่ม `21630 ทิปค้างจ่ายพนักงาน` ในผังมาตรฐาน + migration seed ให้บริษัทเดิม · POS ใช้รหัสเดียว (`ReqAcct` แบบ payroll — ไม่พบให้ throw/InternalNotes ไม่ใช่ fallback prefix) · TipPayout: ตัดสินว่า "ต่อสาย" (ผ่าน PayrollItem เป็นรายได้ ม.40(1) ของพนักงาน — ไม่ใช่ WHT 3% ม.40(2) แบบที่เขียนไว้ `:100`) หรือ "ลบ"
- ความมั่นใจ: **สูง** สำหรับ 21610 (ผังมาตรฐาน + fallback อ่านตรง) · สูงสำหรับ TipPayout ไม่มี call site

### H-08 [P1][M] โมดูลคอมมิชชันเป็นเกาะ: คำนวณ → อนุมัติ → **จบ** — ไม่ไหลเข้า payroll (PayrollItem/PayrollDetail.Commission) ไม่ลง JE ไม่มีเอกสาร
- ไฟล์: `Accounting/Services/Implementations/CommissionService.cs:180-330` (CalculateAsync → `Status = "Calculated"`) · `:336-350` (ApproveAsync) · `Accounting/Controllers/CommissionController.cs:51-59` · `Accounting/wwwroot/pages/commission.html:32,41,178-183` · เทียบ `PayrollService.cs:1653-1673` (commission ของรอบมาจาก `PayrollItem` เท่านั้น)
- โค้ด: ApproveAsync ทั้งเมธอด = `foreach (var calc in calculations) { calc.Status = "Approved"; } await _db.SaveChangesAsync();` · `grep -n 'Payroll\|PayrollItem\|Journal\|Document' CommissionService.cs` = 0 (ยกเว้น query ยอดขายจาก Documents) · หน้าเว็บ: "คำนวณและอนุมัติคอมมิชชันรายเดือน" → ปุ่ม "อนุมัติ" → toast "อนุมัติสำเร็จ"
- ทำไมพัง: ผู้ใช้ตั้งแผน → คำนวณยอดขายพนักงานขาย → อนุมัติ → **ไม่มีอะไรเกิดขึ้นต่อ**: เงินเดือนเดือนนั้นไม่มีคอมมิชชัน (ต้องไปพิมพ์ซ้ำเป็น PayrollItem/แก้ยอดรายคน = ตัวเลขสองชุด) · ไม่มีค่าใช้จ่ายค้างจ่ายใน GL (Dr ค่าคอมมิชชัน / Cr ค้างจ่าย) · ภาษี: คอมมิชชันพนักงาน = ม.40(1) ต้องรวมฐาน ภ.ง.ด.1 — ระบบไม่รู้
- ผลกระทบ: ค่าใช้จ่ายพนักงานขาดจากงบ · ภาษีหักขาด · ผู้ใช้เข้าใจว่า "อนุมัติแล้ว = จ่ายแล้ว"
- defect class: "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้ — มีทุกอย่างยกเว้นสายที่ต่อออก" · "สถานะปลายทางที่ไปต่อไม่ได้"
- ทางแก้: Approve → upsert `PayrollItem(Earning, "Commission", employeeId, month, amount)` หรือเขียนลง `PayrollDetail.Commission` ของรอบ Draft/Calculated + ธง `PaidInPayrollRunId` · ถ้าจ่ายแยก → PV ผ่าน IDocumentService · ตัดสินอย่างตั้งใจ (ต่อสาย/ลบ) ตาม §9 ของ SYSTEM_REVIEW
- ความมั่นใจ: สูง

### H-09 [P1][M] ลาไม่รับค่าจ้างถูกหักเฉพาะรหัส literal `"UnpaidLeave"`/`"ลาไม่รับค่าจ้าง"` — `LeaveType.IsPaid=false` ที่ผู้ใช้สร้างเองไม่ถูกหัก ทั้งที่หน้าลาติดป้าย "⛔ ลาไม่รับค่าจ้าง"
- ไฟล์: `PayrollService.cs:1789-1791` · `Accounting/Models/Entities/Payroll.cs:329-345` (LeaveType มี `public bool IsPaid { get; set; } = true;`) · `Accounting/Controllers/LeaveController.cs:166,308` (UI ตั้ง/อ่าน IsPaid ได้) · `Accounting/wwwroot/pages/leave.html:153,222`
- โค้ด: `var unpaidLeaveDays = approvedLeaves.Where(l => l.LeaveType == "UnpaidLeave" || l.LeaveType == "ลาไม่รับค่าจ้าง").Sum(...)` · `grep -n IsPaid PayrollService.cs` = **0**
- ทำไมพัง: HR สร้างประเภทลา "ลากิจไม่รับค่าจ้าง" (Code `PersonalUnpaid`, IsPaid=false) → หน้าลาโชว์ "⛔ ลาไม่รับค่าจ้าง" → พนักงานลา 3 วัน อนุมัติ → คำนวณเงินเดือน: **จ่ายเต็ม** (ไม่เข้าเงื่อนไข literal) · กลับกัน ถ้าตั้ง `UnpaidLeave` แต่ติ๊ก IsPaid=true (ตั้งใจให้เป็นลาพิเศษมีเงิน) → ถูกหัก · ประเภท seed ค่าเริ่มต้น (:498 Code="UnpaidLeave", IsPaid=false) บังเอิญตรง จึงพลาดเฉพาะประเภทที่ผู้ใช้เพิ่ม
- ผลกระทบ: จ่ายค่าจ้างเกิน/ขาด · ฐาน ปกส./ภาษีผิด · "สองความจริง": หน้าลา (IsPaid) กับ payroll (รหัส) ตัดสินคนละเกณฑ์
- defect class: "ตัวเลขคู่ที่ต้องสอดคล้องกัน ห้ามมาจากคนละแหล่ง — ต้องมีตัวตั้งตัวเดียว" · "รายการที่คัดลอกมาด้วยมือ = drift"
- ทางแก้: โหลด `LeaveType` ของบริษัทมา map `Code → IsPaid` แล้วใช้ `!IsPaid` เป็นเกณฑ์เดียว (คง literal เป็น fallback เมื่อไม่มีแถว LeaveType) · เทสต์: ประเภท custom IsPaid=false ต้องถูกหัก
- ความมั่นใจ: สูง

### H-10 [P1][S] write endpoint ของเงินสดย่อย/เช็ค/ปิดตรวจนับ/โพสต์ FX-reval มีแค่ `[Authorize]` ระดับคลาส — และทั้งสอง controller ไม่อยู่ใน allow-list ของ `write_permission_gate_check`
- ไฟล์: `Accounting/Controllers/SmeOperationsController.cs:18` (`[Authorize]`) `:25,44,60,80,93,107,129,143` (POST/PUT ทั้งหมดไม่มี `RequirePermission`) · `Accounting/Controllers/ChequeController.cs:11,21,39,63,79,93` · `tools/write_permission_gate_check.py:31-36` (มีแค่ Document/Payroll/Metering)
- ทำไมพัง: สมาชิกที่มีสิทธิ์แค่ "ดู" กด `petty-cash/funds/{id}/replenish` (เพิ่มยอดกอง — ไม่มี JE ด้วย ดู H-05) · `disburse` (ลง JE จริง) · `stock-counts/{id}/close` (ปรับสต๊อก) · `fx-revaluation/post` (ลง JE กำไร/ขาดทุนอัตราแลกเปลี่ยน) · `cheques/{id}/clear` (ขยับ `BankAccount.CurrentBalance`) ได้หมด — คลาสเดียวกับที่คอมมิต c587b91 แก้ไป 50 endpoint แต่สอง controller นี้ตกหล่น และ checker รายงานเขียวเพราะไม่ได้มองไฟล์ (บทเรียน MeteringController ใน CLAUDE.md ซ้ำเป็นครั้งที่ 3)
- ผลกระทบ: เงินสด/สต๊อก/GL ถูกแก้โดยผู้ไม่มีสิทธิ์ · ไม่ใช่รั่วข้ามบริษัท (มี `companyId` route guard) แต่เป็นการข้ามชั้น role ภายใน
- defect class: "`[Authorize]` ระดับคลาส = ล็อกอินอยู่ไหม ไม่ใช่มีสิทธิ์ทำสิ่งนี้ไหม" · "checker allow-list ต้องถามทุกครั้งว่าไฟล์ที่เพิ่งแตะอยู่ในลิสต์ไหม"
- ทางแก้: เพิ่มทั้งสองไฟล์เข้า allow-list ของ checker (จะฟ้องทันที) → ใส่ `RequirePermission` ต่อ endpoint ตามผลกระทบ (JE/เงิน = Approve/Pay tier) · เพิ่ม `PermissionKeys.PettyCash*`/`Cheque*` ถ้ายังไม่มี
- ความมั่นใจ: สูง (grep ตรง)

### H-11 [P2][M] Time billing สร้าง `new Document` + `DocumentLine` ตรงลง DbContext (ข้าม `IDocumentService.CreateDocumentAsync`) · ทำเครื่องหมาย time entry เป็น `Billed` ถาวรแม้ Draft ถูกลบ
- ไฟล์: `Accounting/Services/Implementations/TimeBillingService.cs:320-362`
- โค้ด: `var document = new Document { … DocumentNumber = $"DRAFT-{Guid.NewGuid()}", … DocumentDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30), … BalanceDue = totalAmount + vatAmount, … }; _db.Documents.Add(document);` · `entry.IsBilled = true; entry.Status = "Billed"; entry.InvoiceDocumentId = document.Id;` · `grep 'IsBilled = false\|Unbill\|InvoiceDocumentId = null'` = 0
- ทำไมพัง: (1) ข้าม CreateDocumentAsync = ไม่ผ่าน validation/`PaymentTerms` ของคู่ค้า (+30 วันตายตัว — ขัดกับที่ recurring เพิ่งแก้)/DocumentLanguage/Branch/audit log/`ThaiDate.CalendarDateUtc` (DocumentDate = UTC date — H-A3 class: ออกบิลตอน 05:00 น. วันที่ 1 ได้ใบลงวันที่เดือนก่อน → VAT ผิดเดือน) · `DRAFT-{guid}` เต็ม 42 ตัวอักษร ขณะที่ DocumentService ใช้ `DRAFT-` + 8 ตัว (:945) — สองรูปแบบ (2) เอกสารเป็น Draft → ถ้าผู้ใช้ลบ Draft (ทำได้) time entries ยังเป็น `Billed` ผูก `InvoiceDocumentId` ที่ตายแล้ว ⇒ **ชั่วโมงงานนั้นไม่มีวันออกบิลได้อีก** และไม่มีปุ่มปลด
- ผลกระทบ: รายได้หลุด · เอกสารที่สร้างจากเส้นนี้มีคุณสมบัติต่างจากใบที่สร้างจากฟอร์ม (drift) · "โมดูลใหม่ที่มีเงิน ห้ามออกเอกสารเอง — เดินผ่าน IDocumentService" (CLAUDE.md กฎเหล็ก #4)
- defect class: "สำเนาอัลกอริทึมชุดที่สอง" · "สถานะปลายทางที่ไปต่อไม่ได้" · H-A3 (วันที่ UTC)
- ทางแก้: สร้างผ่าน `CreateDocumentRequest`(Invoice, lines) เหมือน `ExpenseClaimService.MarkAsPaidAsync` · ตั้ง `Billed` ตอนใบ **Approved** (hook) ไม่ใช่ตอนสร้าง Draft · เพิ่ม unbill เมื่อใบถูกลบ/void
- ความมั่นใจ: สูง

### H-12 [P2][S] ยังไม่มีทางล้างหนี้ 21816 (กองทุนเงินทดแทน) และ 21818 (PVD รอนำส่ง) — D-R5 ปิดได้ครึ่งเดียว (ภ.ง.ด.1/21914 มีแล้วผ่าน `StatutoryRemittanceService`)
- ไฟล์: `StatutoryRemittanceService.cs:33-45` (`Meta`: SsoSps110/WhtPnd1/WhtPnd3/WhtPnd53/WhtPnd54/VatPp30/VatPp36 — **ไม่มี WorkersComp / PVD**) · `PayrollService.cs:2336-2360` (Cr 21816, Cr 21818 ตอนจ่าย) · `grep -rn '"21816"\|"21818"' --include=*.cs` นอก PayrollService/Templates = 0
- ทำไมพัง: จ่ายเงินเดือนทุกเดือน Cr 21818 (PVD ลูกจ้าง+นายจ้าง) และ Cr 21816 (กท.) → นำส่งกองทุนจริงผ่านธนาคาร → ผู้ใช้ต้องลง JE มือ Dr 21818/Cr ธนาคาร (ไม่มีหน้าจอ ไม่มีร่องรอยเลขรับ) → ส่วนใหญ่ลืม ⇒ หนี้สินสะสมตลอดปี · กท.20ก ยังไม่มีรายงาน (D-S7)
- ผลกระทบ: งบดุลหนี้สินเกินจริง · ไม่มี audit trail การนำส่ง
- defect class: ซ้ำโครง D-R5 (SYSTEM_REVIEW §6) — เปิดอยู่ครึ่งหนึ่ง
- ทางแก้: เพิ่ม type `PvdRemit` (21818) และ `WorkersCompKt20` (21816, งวดปี) ใน `Meta` + แหล่งหนี้จาก `PayrollRun.TotalProvidentFund*`/`TotalWorkersCompensation` — โครง RemitAsync รองรับอยู่แล้ว (ขนาด S)
- ความมั่นใจ: สูง

### H-13 [P2][S] แก้ยอดรายคนได้ในสถานะ **Approved** โดยรอบไม่ถอยกลับเป็น Calculated — ขั้นอนุมัติถูกข้ามได้เงียบ ๆ
- ไฟล์: `Accounting/Helpers/PayrollRunEditPolicy.cs:29-31` (`Calculated or Approved => (true, null)`) · `PayrollService.cs:1367-1500` (UpdatePayrollDetailAsync — `awk '/run\.Status|Approved/'` ในช่วงนี้พบแค่บรรทัดเรียก CanEditAmounts ไม่มีการเซ็ตสถานะกลับ) · `:2049-2066` (ApprovePayrollAsync บังคับ `Status == "Calculated"`)
- ทำไมพัง: ผู้อนุมัติกด "อนุมัติ" ยอดรวม X → HR เปิดแถวพนักงาน แก้โบนัส +50,000 → รอบยังเป็น Approved → กด "จ่าย" ผ่านทันที (ด่าน :2198 ตรวจแค่ `== "Approved"`) ⇒ สิ่งที่ผู้อนุมัติเห็นกับสิ่งที่จ่ายจริงต่างกัน โดย AuditLog มีร่องรอยแก้ยอดแต่ไม่มีการอนุมัติซ้ำ · หน้าจอบอกเหตุผลเฉพาะ Paid/Voided/Draft (ถูกออกแบบให้แก้ได้ใน Approved โดยตั้งใจ — แต่ไม่มี re-approve)
- ผลกระทบ: การควบคุมภายใน (maker/checker) ของเงินเดือนไม่มีผลจริง
- defect class: "ด่านที่ครอบแค่ทางเดียว" (ApproveAsync ตรวจ Calculated แต่ทางแก้ยอดไม่ถอยสถานะ)
- ทางแก้: แก้ยอดใน Approved → เซ็ต `Status = "Calculated"` + `ApprovedBy/At = null` (หรือ block แล้วบอกให้ "ถอนอนุมัติ" ก่อน) · ให้ `CanEditAmounts` คืนข้อความว่าจะต้องอนุมัติใหม่
- ความมั่นใจ: สูงสำหรับพฤติกรรม · กลางสำหรับ "เป็นบั๊ก" (อาจตั้งใจ — แต่ต้องมี re-approve จึงจะเรียกว่าครบ)

### H-14 [P2][S] แก้ข้อมูลตัวตนพนักงานหลังสร้างไม่ได้ (ชื่อ · เลขบัตร · วันเริ่มงาน · ที่อยู่ · ชื่อบัญชีธนาคาร · เลขผู้ประกันตน) — `UpdateEmployeeRequest` ไม่มี 14 ฟิลด์ที่ Create รับ · `EmployeeResponse` ไม่ echo 13 ฟิลด์
- ไฟล์: `Accounting/Models/DTOs/Payroll/PayrollDtos.cs:3-53` (Create) vs `:54-91` (Update) vs `:92-…` (Response) · `Accounting/wwwroot/pages/employees.html:480-505` (payload แก้ไขส่งแค่ position/department/phone/email/baseSalary/bank*/pvd/branch/cost/external/salaryType) · `:463,493` (`socialSecurityNumber: null, socialSecurityHospital: null` hardcode ทั้งสร้างและแก้ · ไม่มี input `fSsn`/`fHospital` ในหน้า)
- โค้ด (สคริปต์เทียบ record ใน scratchpad): Create-only = `Address BankAccountName CitizenId DateOfBirth EmployeeCode EmploymentType FirstNameEn FirstNameTh Gender LastNameEn LastNameTh SocialSecurityNumber StartDate TitleTh` · Create-but-not-in-Response = `Address BankAccountName BankAccountNumber BankName BranchId DateOfBirth DimensionId Gender HasProvidentFund ProvidentFund*Percent SocialSecurityHospital SocialSecurityNumber`
- ทำไมพัง: (1) พิมพ์ชื่อ/เลขบัตรผิดตอนสร้าง → ไม่มีทางแก้จากหน้าจอ (ต้องลบ-สร้างใหม่ ซึ่งเสียประวัติ payroll/ลา) — เลขบัตรผิด = ภ.ง.ด.1/สปส.1-10 ยื่นผิดทุกเดือน (2) เปิดแก้ไข → ช่องธนาคาร/PVD/สาขาว่างเพราะ Response ไม่ส่งมา → ผู้ใช้คิดว่ายังไม่ตั้ง (ไม่หาย เพราะ Update ใช้ `if (x != null)` — แต่ผิดกฎ "เก็บแล้วต้อง echo กลับ") (3) เลขผู้ประกันตน/สถานพยาบาล = SYSTEM_REVIEW **D-U1 ยังเปิด** (T2 ลดหย่อน 8 ฟิลด์ **ปิดแล้ว** — html มี hasSpouseAllowance ฯลฯ)
- defect class: "เก็บแล้วต้อง echo กลับ" · "เพิ่ม field = แตะ record ทั้ง 3 ตัวเสมอ"
- ทางแก้: เติม Update/Response ให้ครบชุด Create + hydrate ฟอร์ม · PII (เลขบัตร/บัญชี) ผ่านด่าน `Pii.View` + mask ตาม PDPA ม.26 (ระวังไม่ให้ Response รั่ว PII ให้ role ที่ไม่มีสิทธิ์ — เหตุที่อาจตั้งใจตัดออกแต่ต้องมีทางเลือกอื่นให้ผู้มีสิทธิ์)
- ความมั่นใจ: สูง (diff เชิงโครงสร้าง) — ต้องเช็คต่อว่า `employees/sync` (:206) ยอมให้ upsert ชื่อได้ไหม (ถ้าได้ = ทางอ้อมสำหรับผู้ใช้ API เท่านั้น)

### H-15 [P2][S] Recurring: JE จากเทมเพลตใช้ `EntryDate: DateTime.UtcNow` ตรง ๆ — `AccountingService.NormalizeDate` แก้แต่ พ.ศ. ไม่แก้ timezone (เอกสารปลอดภัยเพราะ DocumentService แปลงให้)
- ไฟล์: `RecurringTransactionService.cs:651` (`EntryDate: DateTime.UtcNow`) vs `:633` (placeholder ใช้ `ThaiDate.CalendarDateUtc` แล้ว) · `AccountingService.cs:405,2574-2581` (`NormalizeDate` = แปลง พ.ศ.↔ค.ศ. เท่านั้น) · `DocumentService.cs:971` (`CalendarDateUtc(request.DocumentDate)` — เส้นเอกสารถูก) · job ทุก 15 นาที `BackgroundJobService.cs:43`
- ทำไมพัง: `NextRunDate` มาจาก `request.StartDate` ที่หน้าเว็บส่ง — ถ้า serialize เป็นเที่ยงคืนเวลาไทย (= 17:00 UTC วันก่อน) งานจะครบกำหนดตั้งแต่ 00:00 น. ไทย → job รอบ 00:xx–06:59 น. สร้าง JE ที่ `EntryDate` = **วันก่อนหน้าตาม UTC** → JE ค่าเช่า/ค่าเสื่อมของ "1 พ.ย." ตกงวด ต.ค. ที่อาจปิดแล้ว (ด่านงวดปิดจะ throw → งานล้มทุก 15 นาทีจนกว่าจะเลย 07:00 น. — หรือถ้างวดยังเปิดก็ลงผิดเดือนเงียบ ๆ) · เส้นเดียวกันนี้ในไฟล์เดียวกันแก้ให้ placeholder แล้วแต่ไม่แก้ `EntryDate`
- defect class: H-A3 (`DateTime.UtcNow` ตรง ๆ) · "แก้ตัวเดียว เหลือที่เหลือ"
- ทางแก้: `EntryDate: Accounting.Helpers.ThaiDate.CalendarDateUtc(DateTime.UtcNow)` (ใช้ `baseDate` ที่คำนวณไว้แล้ว :633) · หรือดีกว่า: ใช้ `recurring.NextRunDate` เป็นวันที่รายการ (วันที่ตามกำหนด ไม่ใช่วันที่ job ตื่น)
- ความมั่นใจ: **สงสัย-กลาง** — ต้องเช็ค `recurring.html` ว่าส่ง `startDate` เป็น `"YYYY-MM-DD"` (→ UTC 00:00 ปลอดภัย) หรือ `toISOString()` ของ Date local (→ 17:00 UTC วันก่อน = พัง)

### H-16 [P3][S] เบิกค่าใช้จ่าย: PV ที่สร้างอัตโนมัติลงวันที่ `DateTime.UtcNow.Date` และไม่ส่ง `BankAccountId`/วิธีจ่าย — `PaidMethod`/`PaidReference` เก็บบน claim อย่างเดียว JE เข้าบัญชีเงินสด default เสมอ
- ไฟล์: `ExpenseClaimService.cs:452-461` (`CreateDocumentRequest(DocumentType.PaymentVoucher, DateTime.UtcNow.Date, null, contactId, …, ProjectId: claim.ProjectId)` — ไม่มี `BankAccountId:`) · เทียบ `SalaryAdvanceService.cs:294-308` ที่ส่ง `BankAccountId: request.BankAccountId`
- ผลกระทบ: จ่ายคืนพนักงานผ่านโอนธนาคาร แต่ GL ลดเงินสด → กระทบยอดธนาคารไม่ตรงทุกใบเบิก · วันที่ UTC (H-A3) · Void claim ที่ Paid ทำไม่ได้ (:560 throw) และไม่มี hook เมื่อ PV ถูก void จากหน้าเอกสาร → claim ค้าง Paid
- ทางแก้: `PayExpenseClaimRequest` เพิ่ม `BankAccountId` → ส่งต่อ · ใช้ `ThaiDate.CalendarDateUtc` · ฟัง PV void → claim กลับ Approved
- ความมั่นใจ: กลาง (ยังไม่ได้ไล่ว่า AutoPost PV ใช้บัญชีอะไรเมื่อ BankAccountId=null — คาดว่า 111x default ตามรูปแบบ payroll)

### H-17 [P3][S] เงินทดรองสถานะ Disbursed ไม่มีทางออกนอกจากถูกหักจากเงินเดือน — พนักงานลาออกก่อนหักครบ = ลูกหนี้ 115x ค้างถาวร · Terminate ไม่เตือน
- ไฟล์: `SalaryAdvanceService.cs:332-345` (`VoidAsync`: `if (Status == "Disbursed" || "Cleared") throw`) · public methods ทั้งไฟล์ไม่มี WriteOff/Settle-by-cash · `PayrollService.cs:855-893` (TerminateEmployeeAsync ไม่อ่าน SalaryAdvance)
- ทางแก้: `SettleAdvanceAsync(cash|payroll-final|write-off)` ผ่าน IDocumentService (ReceiptVoucher Dr เงินสด / Cr 115x) + เตือนตอน Terminate ว่ามียอดค้าง · รวมเข้า "เงินชดเชย/จ่ายรอบสุดท้าย" (`severance/post` :250 — ยังไม่ได้อ่านว่าหักเงินทดรองไหม)
- ความมั่นใจ: สูงสำหรับ "ไม่มีทางออก" · ยังไม่อ่าน severance path

## ตรวจแล้วไม่ใช่บั๊ก (เพื่อทีมอื่นไม่เสียเวลาซ้ำ)
- **JE จ่ายเงินเดือนสมดุลตามพีชคณิต** (`PayrollService.cs:2250-2530`): Dr = Gross + SSOer + WC + PVDer · Cr = WHT + SSO(emp+er) + WC + PVD(emp+er) + Loan + Other + Advance + (Net−Advance) ⇒ สมดุล ⟺ Gross = Net + WHT + SSOemp + PVDemp + Loan + Other ซึ่งคือ identity ที่ `UpdatePayrollDetailAsync` :1470-1471 และ import :1115-1123 บังคับไว้ · มีด่าน Dr≠Cr ก่อนสร้าง JE พร้อมวินิจฉัยรายคน · `ReqAcct` throw เมื่อไม่พบผัง (D-17 ยังจริงเฉพาะบัญชี **541 เงินเดือน** ที่ยัง `if (salaryAccount != null)` เงียบ → ตกไปฟ้อง "ไม่สมดุล")
- **Void/Reopen/Pay ล็อกแถว `FOR UPDATE` + re-read ใต้ล็อก** ครบ 3 เมธอด · Reopen ตรวจงวดปิด + กลับ JE ลงวัน PayDate + คืนเงินทดรอง + ซ่อม ปกส. + AuditLog · ReverseSsoSettlement มีคู่กับ SettleSso
- **SalaryAdvance disburse ผ่าน IDocumentService (PV Dr 115x / Cr ธนาคาร)** ถูกตามกฎ "โมดูลใหม่ที่มีเงิน ห้ามออกเอกสารเอง" · ExpenseClaim MarkAsPaid ผ่าน PV เช่นกัน (legacy JE ตรงเป็น fallback เมื่อไม่มี DI เท่านั้น)
- **Recurring กันรันซ้ำข้ามเครื่องได้จริง** (`FOR UPDATE SKIP LOCKED` ต่อแถว + reload + เช็ค `NextRunDate > now` ใต้ล็อก · RunNow ก็ล็อก) — B-10 ของทีม B ครอบเรื่องสืบทอด field แล้ว ไม่ซ้ำ
- **Bank manual match ตรวจ tenant ของ JE/Payment + กันจับคู่ซ้ำ** (`BankService.cs:545-565`) · การจับคู่ไม่สร้าง JE (ผูกกับ JE/Payment ที่มีอยู่) ⇒ Unmatch ไม่ต้องกลับ JE — ถูก
- **Lodging**: มัดจำ/เช็คเอาต์/ยกเลิก เดินผ่าน `IDocumentService` (`CreateDepositReceiptAsync` · TaxInvoice+CreatePayment · Refund/RealizeDeposit) · `PaidAmount += …` โดยไม่มีเอกสารเกิดเฉพาะ `LodgingAccountingMode.Off` (PMS-only) ซึ่งเป็นค่าที่ผู้ใช้เลือกเอง — ไม่ใช่บั๊ก
- **CMS commerce** สั่งซื้อ→เอกสาร→JE→payment ผ่าน `_docService` ครบ (สอดคล้อง SYSTEM_REVIEW §8 แถว 3b/4)
- **Terminate → `User.Status = Inactive`** และ `UserLoginPolicy.Evaluate` ถูกเรียกในเส้นล็อกอิน/refresh/SSO (`AuthService.cs:303,358,542`) — ไม่ต้องถอด `UserExternalLogins` ก็เข้าไม่ได้ (DSR erase เป็นอีกเรื่อง — แก้แล้วในคอมมิตก่อน)
- **ลาไม่รับค่าจ้าง seed ค่าเริ่มต้น** (`LeaveController.cs:498` Code="UnpaidLeave") ตรง literal ใน payroll — พังเฉพาะประเภทที่ผู้ใช้เพิ่ม (H-09)
- **Budget**: อ่าน JE lines อย่างเดียว ไม่เขียน GL — ไม่มีอะไรให้พัง

## ซ้ำกับ SYSTEM_REVIEW (ID เดิม + สถานะจริง)
- **D-R5** (21914/21816 ไม่มีทางล้าง) — **ปิดครึ่ง**: 21914 ล้างได้ผ่าน `StatutoryRemittanceService` (WhtPnd1) · 21816 และ 21818 ยังไม่มี (H-12)
- **D-T2** (ลดหย่อน 8 ฟิลด์ไม่มีใครเขียน) — **ปิดแล้วจริง** (คอมมิต 2a5cc83: DTO + employees.html มี hasSpouseAllowance ฯลฯ) ✅ ตรง
- **D-U1** (เลขผู้ประกันตน/สถานพยาบาลไม่มีหน้าไหนเก็บ) — **ยังเปิด** (`employees.html:463,493` hardcode null) รวมใน H-14
- **D-S7** (กท.20ก ไม่มีรายงาน) — ยังเปิด · ญาติกับ H-12
- **D-17** (ERP review: payroll ผังไม่มี 541xx ล้ม "ไม่สมดุล") — ยังจริงเฉพาะบัญชีเงินเดือนหลัก (บัญชีอื่นผ่าน `ReqAcct` แล้ว)
- **H-A3** (UtcNow ตรง ๆ) — พบซ้ำคลาสใน TimeBilling (H-11) · Recurring JE (H-15) · ExpenseClaim (H-16) · TipPayout (`DateTime.UtcNow.Date` :117)
- **H-A7** (ปิดกะไม่ลง JE) — ไม่ได้ตรวจซ้ำ (ทีม E ยืนยันยังเปิด)
- **SYSTEM_REVIEW §8 แถว POS "JE/COGS/tip ถูก"** — **ผลตรวจขัด**: tip ลง 21610 เงินมัดจำรับ (H-07) → ควรแก้ตารางสุขภาพโมดูล
- **C-04** (JE นอก Builder 50 จุด) — `PettyCashService.DisburseAsync` เป็นหนึ่งในรูปแบบนี้ (ถ้ายังไม่อยู่ในลิสต์ 50 ให้เพิ่ม) แต่ **Replenish ไม่มี JE เลย** เป็นคลาส E-07 (H-05)

## ยังไม่ได้อ่าน
- `PayrollService` ส่วน severance (`severance-preview`/`severance/post` :225-250 controller) — JE เงินชดเชย/ภาษี ม.40(1) กรณีเลิกจ้าง · `IssueMonthlyPnd1CertsAsync` หลังแก้ W1 · `EmailScheduleService.OnPayrollPaid` · `PayslipLineDeliveryService`
- `HrAllocationService` · `LeaveService` approve flow (ตรวจแค่ query ฝั่ง payroll) · attendance import
- Bank reconciliation เชิงลึก: `BankService.Reconciliation.cs` group unwind (:251-290) · `BankFeedService` AI fallback · cheque ↔ payment method ในหน้า documents (ไม่มี `Cheque` ใน DocumentService เลย ⇒ เช็คที่รับผ่านฟอร์มเอกสารไม่สร้าง Cheque entity — โมดูลเช็คเป็นเกาะอีกครึ่ง)
- `ProjectCostService` · `BudgetService` เชิง alert · `CmsCommerceService` refund (grep `Refund` = 0 → ไม่มีเส้นคืนเงินออเดอร์ออนไลน์เลย — ช่องว่าง ERP ไม่ใช่บั๊ก)
- `StatutoryRemittanceService` ฝั่ง VatPp30/Pp36 (ทีม C ครอบภาษี)
- ไม่ได้รัน simulation ตัวเลข (ทุกข้อเป็นการอ่านโค้ด + grep call site)

## ข้อเสนอเชิง ERP (สิ่งที่ขาดถ้าจะเป็น ERP ครบ — แยกจากบั๊ก)
1. **Sub-ledger พนักงานตัวเดียว** (ลูกหนี้พนักงาน: เงินทดรอง · เงินกู้ · เบิกล่วงหน้า · ทิปค้างจ่าย · คอมมิชชันค้างจ่าย) ที่ทุกโมดูลเขียนผ่านชั้นเดียวและ payroll หักคืนจากชั้นนั้น — วันนี้ 4 โมดูลเก็บคนละตาราง คนละผัง (115x/2160/ไม่มี) และรวมกันไม่ได้
2. **Payroll posting map ต่อบริษัท** (เหตุการณ์ × ผัง: Gross/SSO/WHT/PVD/WC/Loan/Advance/Tip/Commission/Cash) แทน literal 541xx/218xx/219xx ใน service — ลูกค้าที่นำเข้าผังเอง (D-04) ใช้ payroll ไม่ได้เลยตอนนี้
3. **นำส่งเงินสมทบ/ภาษีทุกชนิดในหน้าเดียว + กลับรายการได้** (H-02/H-12): SSO · ภ.ง.ด.1 · PVD · กท. · ภ.ง.ด.3/53/54 · ภ.พ.30/36 — โครง `StatutoryRemittanceService` เหมาะเป็นฐาน แต่ต้องมี reverse + amended filing
4. **Workflow เงินเดือนแบบ maker/checker จริง**: แก้ยอดหลังอนุมัติ → ต้องอนุมัติซ้ำ (H-13) · JE preview ก่อนจ่าย (SYSTEM_REVIEW D re-design) · ไฟล์โอนธนาคาร (D-U2)
5. **Cheque เป็นวงจรบัญชีเต็ม**: รับ/จ่ายด้วยเช็คจากฟอร์มเอกสาร → Cheque entity อัตโนมัติ · เช็คในมือ 11131 → ธนาคารเมื่อ clear · เด้ง → กลับ payment + ค่าธรรมเนียม (H-06)
6. **Petty cash imprest system**: เติมเงิน = PV · จ่าย = ใบสำคัญเงินสดย่อยที่มี VAT/WHT/หลักฐาน §65 ตรี (9) · ปิดกอง/ตรวจนับ (H-05)
7. **Commission/Tip → payroll**: ทุก "รายได้พนักงานที่คำนวณจากที่อื่น" เข้าเป็น `PayrollItem` ที่มี source ref เดียว ห้ามให้ HR พิมพ์ซ้ำ (H-07/H-08)
8. **HR master ครบวงจร**: แก้ตัวตนได้ · PII ผ่านด่าน · เลขผู้ประกันตน/สถานพยาบาล · ลาออก → เตือนหนี้ค้าง/คืนทรัพย์สิน/ปิดเงินทดรอง (H-14/H-17)

## สรุป 5 บรรทัด
1. **P0 1 ข้อ**: ยกเลิกรอบเงินเดือนที่นำส่ง สปส. แล้วได้ (Void ไม่ตรวจ `SsoSettledAt` ขณะที่ Reopen ตรวจ) ⇒ 21815 ติดลบถาวร + เสี่ยงนำส่งซ้ำ (H-01)
2. **P1 9 ข้อ** — โมดูลรองที่ "มีทุกอย่างยกเว้นสายที่ต่อออก": ทิป POS ลงบัญชีมัดจำรับ 21610 ทุกบริษัทผังมาตรฐาน (H-07) · คอมมิชชันอนุมัติแล้วจบ (H-08) · เงินสดย่อยเติมเงินไม่มี JE + จ่ายสร้าง JE ตรง (H-05) · เช็คเด้งไม่กลับ payment (H-06) · import เงินทดรองยุบเป็นหักอื่น → ลดค่าใช้จ่ายแทนล้างลูกหนี้ (H-03) · เงินทดรองที่หักตอนจ่ายไม่ขึ้นสลิป (H-04) · ลาไม่รับค่าจ้าง custom ไม่ถูกหัก (H-09) · นำส่ง ภ.ง.ด.1 ไม่มี reverse และ Reopen ไม่รู้ (H-02) · SmeOperations/Cheque controller ไม่มีด่านสิทธิ์ + หลุด allow-list checker (H-10)
3. **P2–P3 7 ข้อ**: แก้ยอดหลังอนุมัติไม่ถอยสถานะ (H-13) · แก้ชื่อ/เลขบัตรพนักงานไม่ได้ + Response ไม่ echo 13 ฟิลด์ (H-14) · TimeBilling สร้าง Document ตรง + Billed ถาวร (H-11) · Recurring JE วันที่ UTC (H-15 สงสัย) · 21816/21818 ไม่มีทางล้าง (H-12) · ExpenseClaim PV ไม่มีบัญชีธนาคาร (H-16) · เงินทดรอง Disbursed ไม่มีทางออก (H-17)
4. **ที่ทำดีแล้ว**: JE เงินเดือนสมดุลตามพีชคณิตพร้อมวินิจฉัยรายคน · ล็อกแถว Pay/Void/Reopen · SalaryAdvance/ExpenseClaim/Lodging/CMS ผ่าน IDocumentService · Recurring ล็อกข้ามเครื่องจริง · T2 ปิดแล้วจริง
5. **รากร่วม**: (ก) ด่านสถานะที่ใส่ทางเดียว (Void vs Reopen · Approve vs Edit) (ข) fallback ผังด้วย prefix (216→21610 · 115 · "ทดรอง") แทน posting map (ค) โมดูลรองที่ออก JE/ไม่ออก JE เองแทนเดินผ่านชั้นเอกสาร — ตรงกับ §2 ของ ERP_REVIEW ("ชั้น posting เดียว") และควรเพิ่ม `SmeOperationsController`/`ChequeController` เข้า checker ทันที
