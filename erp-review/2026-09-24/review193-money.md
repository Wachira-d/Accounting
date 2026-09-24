# ฝ่ายค้าน รอบ 193 — เงิน/ภาษี/เงินเดือน (E · P2 · O2 ข้อ 20 · F)

อ่านโค้ดอย่างเดียว ไม่ได้คอมไพล์ · ทุกข้อมี file:line ที่เปิดดูแล้ว

## CONFIRMED

1. **P2 #35: คำนวณรอบ Approved ใหม่ได้ ทั้งที่รอบ Approved ยื่นแบบไปแล้วได้.** `PayrollRunFilingScope.FilingStatuses = {Approved, Paid}` และ `TaxFilingExportService.EnsureFilableRunsAsync` ให้ดาวน์โหลดไฟล์ ภ.ง.ด.1/สปส.1-10 จากรอบ Approved (คอมเมนต์ในโค้ดเขียนว่า "HR เตรียมไฟล์ก่อนวันจ่ายเป็นเรื่องปกติ") รายงาน 50 ทวิรายปี (`PayrollService:2878`) และ ภ.ง.ด.91 (`TaxService:2377`) ก็นับรอบ Approved ด้วย แต่ `CanRecalculate` ไม่ตรวจเลยว่ามีการยื่นแล้วหรือยัง ทั้งที่มีแถว `EFilingExport` (FormType/ปี/เดือน) อยู่แต่ไม่มีใครอ่าน ตัวอย่าง: พนักงานเงินเดือน 17,500 ลาไม่รับค่าจ้างทั้งเดือน สปส.1-10 ยื่นไปแล้วที่ 875/ค่าจ้าง 17,500 → กดคำนวณใหม่ → 0 → JE ตอนจ่ายลง 0 ⇒ ตัวเลขในแบบที่ยื่นกับในบัญชีไม่ตรงกัน และไม่มีคำเตือน **ขัดคำตัดสิน "ยื่นแล้วห้ามแก้"** · ส่วนที่ไม่หลงเหลือค้าง: JE · การหักคืนเงินทดรอง · 50 ทวิรายเดือน · การนำส่ง ทำเฉพาะตอน Paid
   → ผลพ่วง (PLAUSIBLE): `HrAllocationService:275` จัดสรรค่าแรงเข้าโครงการได้ตั้งแต่ Approved ⇒ `ProjectCostEntry` ค้างยอดเก่า และแถวเวลาถูกติด `IsAllocated=true` ⇒ อนุมัติใหม่แล้วจัดสรรซ้ำไม่ได้
2. **F · pos-packages: ข้อมูลที่บันทึกไว้แล้วกลับด้าน และไม่มี migration.** หน้าเว็บเดิมใช้ 0="คงที่" 1="เปอร์เซ็นต์" แต่ enum คือ Fixed=1 Percentage=2 และสูตร (`PosService.Orders:1970`) คือ `== Fixed ? value : total*value/100`
   - แถวที่ตั้ง "คงที่ 50฿" (เก็บเป็น 0) ถูกคิดเป็น % ⇒ บิล 500 ได้ 250 แทน 50
   - แถวที่ตั้ง "10%" (เก็บเป็น 1) ⇒ ได้ 10฿ แทน 50
   - ฟอร์มใหม่เปิดแถวที่เก็บ 0 ไม่เจอ option ที่ตรง ⇒ select ว่าง ⇒ บันทึกส่ง `""` ⇒ แปลงเป็น enum ไม่ได้ ⇒ 400
   - migration ต้องให้เจ้าของตัดสิน: แถวที่สร้างจากหน้าเว็บแปลง 0→1, 1→2 ได้ แต่แถวที่สร้างผ่าน API แยกไม่ออก
3. **O2 · API v1 สัญญาสิ่งที่ไม่มีจริง.** `ContactsV1Controller` ตอบ `matched:false` พร้อมข้อความ "ระบบจะสร้างผู้ติดต่อของสาขานี้แยกเมื่อสร้างเอกสาร" แต่ `DocumentsV1Controller.ResolveContactAsync:302` ส่ง `branchCode: null` และ payload เอกสารไม่มีช่องสาขา ⇒ ได้แถว สนญ./แถวรหัสต่ำสุด ⇒ ใบกำกับของผู้ซื้อสาขา 8 ออกในนามแถว สนญ. (§86/4 ผิดสาขา)
4. **O2 · แถวเก่าที่ไม่มีสาขายังถูกเขียนทับ.** `Pick` นับแถวที่ `BranchCode` ว่างเป็น "สนญ." เมื่อ payload เป็น สนญ./ไม่ระบุ แต่**ก็ยก**แถวเดียวกันนี้ให้ payload สาขา 00008 ด้วย (`UnspecifiedBranchRow` · เทสต์ `LegacyRowWithoutBranch_ClaimedByBranchPayload_NoDuplicate`) จากนั้นบล็อก update ใน `IntegrationService.ProcessCustomerAsync` เขียน `BranchCode=00008` + `Address` ทับแถวนั้น ⇒ ยังเป็นบั๊กเดิมที่คอมเมนต์บอกว่าแก้แล้ว สำหรับผู้ติดต่อทุกรายที่สร้างก่อนมีช่องสาขา · payload สนญ. ครั้งถัดไปหาไม่เจอ ⇒ สร้างแถว สนญ. ใหม่ ส่วนประวัติ AR ค้างอยู่กับแถวที่กลายเป็น "สาขา 8" (เอกสารเก่ามี snapshot สาขาไว้แล้วจึงไม่พัง)
5. **E · COGS สูตรรวมวัตถุดิบที่ไม่ได้ติดตามสต็อก.** `ApplyRecipeConsumptionAsync` บวก `move.TotalCost` ของส่วนประกอบทุกตัวโดยไม่ดู `TrackStock` แต่ตอนซื้อของที่ `TrackStock=false` ระบบลงเป็นค่าใช้จ่าย (เส้นซื้อแบบ perpetual ใน DocumentService ทำเฉพาะของที่ `TrackStock`) ⇒ ถ้วย+หลอดที่ไม่ติดตามสต็อก 3.00/แก้ว: แต่ละแก้ว Dr 51110 3.00 / Cr 11500 3.00 ⇒ ค่าใช้จ่ายถูกนับซ้ำ และสินค้าคงเหลือใน GL ลดลงเรื่อย ๆ (เดิมตอนขายเป็น 0 ส่วนเส้นคืนเงินมีบั๊กเดียวกันในทิศกลับ) · ผลกระทบขึ้นกับว่ามีวัตถุดิบแบบนี้จริงไหม
6. **P2 · ยังเสกฐานค่าจ้าง ปกส. ได้.** `PeriodBase(statutoryWage, max, grossIncome)` ใช้ `grossIncome` ซึ่งรวม `nonTaxableExtra` และเบี้ยเลี้ยงที่ไม่ใช่ค่าจ้าง ม.5 ⇒ ลาไม่รับค่าจ้างทั้งเดือนแต่ได้เบี้ยเลี้ยง 500 ⇒ ฐาน 1,650 ⇒ หักลูกจ้าง 82.50 + นายจ้าง 82.50 และ สปส.1-10 ประกาศค่าจ้าง 1,650 ในเดือนที่ได้ค่าจ้าง 0 ⇒ defect class เดียวกับ D-02 ที่ตั้งใจปิด (การตีความข้อกฎหมายให้เจ้าของยืนยัน)

## PLAUSIBLE

- **Lodging** `FindOrCreateContactAsync` ไม่ดู `TaxIdExists` ⇒ ไปจับด้วยอีเมล/โทรศัพท์แทน ⇒ แขกที่มีเลขภาษี X อาจถูกผูกกับผู้ติดต่อเลข Z เมื่อใช้อีเมลเอเจนซีร่วมกัน (ขัดกติกาของ O2 เอง "ห้ามถอยไปจับด้วยชื่อ/อีเมล")
- **จุดที่ยังจับด้วยเลขภาษีอย่างเดียว** และสร้าง/ผูกคู่ค้าของเอกสารจริง: `CmsCustomerService:346` (ลูกค้าร้านออนไลน์มี `BranchCode`) · `CrossTenantWorkflowService:384/472` · `PlatformBillingDocumentIssuer:292`
- **Commission Update** ตรวจทั้งแผนหลังผสานค่าเดิม ⇒ แผนเก่าที่อัตราเป็น null หรือฐานเป็น "Quantity" **ปิดใช้งานไม่ได้** (`IsActive=false`) ถ้าไม่แก้ค่าอื่นก่อน
- **อัตราแลกเปลี่ยน** แถว MidRate=0 ที่ค้างอยู่ทำให้ BOT sync ข้ามวันนั้น (`BotExchangeRateService:91` ข้ามเมื่อมีแถวอยู่แล้ว) ⇒ ตัวอ่านใช้อัตราวันก่อนหน้าเงียบ ๆ
- **โครงการ** `RevenueRecognitionMethod` ไม่มีผู้อ่านเลยนอกจาก mapper ⇒ เลือก "CostRecovery" ได้แต่ไม่มีผลอะไร (silent no-op · อ้าง TFRS บทที่ 6 แต่ไม่ได้ทำจริง)
- **POS void** บิลที่ JE ขายถูกกลับรายการด้วยมือไปแล้ว ⇒ ยกเลิกไม่สำเร็จทุกครั้ง (`ReverseJournalEntryAsync` โยน "ถูกกลับรายการไปแล้ว") และไม่มีทางไปต่อ
- **คืนเงิน/void สินค้าสูตร** คืนวัตถุดิบเข้าคลังที่ต้นทุนเฉลี่ยวันนี้ (ไม่ส่ง `UnitCostOverride`) ขณะที่ JE กลับต้นทุนที่ตรึงไว้ ⇒ มูลค่าคลังกับ GL คลาดกันเมื่อต้นทุนเฉลี่ยขยับ

## Checked-fine

- **ปกส. พนักงานปกติ:** leaveDeduction=0 ⇒ `SalaryPaidThisPeriod` = prorated ⇒ `PeriodBase` = Clamp เดิม ⇒ ตรงกันทุกสตางค์
- **CanRecalculate** กัน Paid/Voided/รอบที่เคยจ่ายแล้วกลับรายการ/รอบนำเข้าจากระบบนอก · ล้างผู้อนุมัติแล้ว · UI เตือนว่ายอดที่แก้มือรายคนจะหาย
- **COGS สินค้าที่ติดตามสต็อก:** ต้นทุนขาออกของ ledger (WA ใช้ avg>0 ไม่งั้น CostPrice · Standard ใช้ CostPrice) เท่าสูตรเดิม · FIFO เปลี่ยนเป็นต้นทุน layer ตามที่ตั้งใจ · `TotalCost` เป็นบวก · คอลัมน์เป็น `numeric` ไม่จำกัดทศนิยม ⇒ คืนครบรวมกลับได้ยอดขายเป๊ะ
- **Void:** กลับ JE ใน `voidTxn` (`AccountingService` ใช้ `CurrentTransaction` ที่มีอยู่) · Reference ของ JE คืนเงินตรงกัน · ตัวกลับรายการถูกกันออกด้วย `OriginalEntryId == null`
- **`EmployeeTaxIdentity`:** `Employee.TaxId` ไม่เคยมีผู้เขียน ⇒ สลับลำดับแล้วไม่เปลี่ยนข้อมูลเดิม
- **คอมไพล์:** เปิดดูแล้วว่ามีจริง — `PiiMask.*` · `ThaiTaxIdValidator.Check/ValidationResult` · `TaxBranchCode.Normalize/TryNormalize/HeadOffice` · ctor `BusinessRuleException(msg, inner, code)` · พารามิเตอร์ใหม่ใน DTO (`TaxId`, `BankName`…, `EmployeeCodeLocked`, `CanRecalculate`, `IsActive`, `InitialRate`) · `FieldEdit` เป็น internal เข้าถึงได้ผ่าน InternalsVisibleTo · ชื่อ tuple ที่ต่างกันใน `AddRange` เป็น identity conversion ⇒ **ไม่พบความเสี่ยงคอมไพล์**

## เทสต์ที่ยังเขียวแม้ถอด fix ออก

- `SsoUnpaidLeaveWageTests` ประกอบสูตรขึ้นใหม่เองในเทสต์ ⇒ ย้อน `PayrollService:2043` กลับเป็น `proratedBaseSalary` ก็ยังผ่าน
- `EmployeeTaxIdentityTests` ทดสอบแค่ `Resolve` ⇒ ย้อนด่าน D-01 (`:3360`) กลับเป็น `emp.TaxId` ก็ยังผ่าน
- `PayrollRunRecalculatePolicyTests` ไม่มีอะไรยืนยันว่า `CalculatePayrollAsync` เรียกมันจริง
- `ContactTaxBranchKeyTests` ไม่ครอบการเขียนทับข้อ 4
- `PosCogsBookingTests` / `PosVoidPlanTests` เป็นเทสต์ pure ⇒ ลำดับ "ตัดสต็อกก่อน JE" ถูกบังคับด้วยลายเซ็นของเมธอดเท่านั้น
