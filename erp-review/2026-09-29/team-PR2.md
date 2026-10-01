# รอบ 201 ทีม PR2 — เงินเดือน: คำตัดสิน 69–73 + ผลฝ่ายค้าน PR1 (X1–X6)

**ฐาน**: `9c9580e6` (merge ทีม PR1 + คำตัดสิน 69–73) · ไฟล์ที่ถือ: `PayrollService.cs` · `PayrollController.cs` · `Helpers/Payroll*.cs` · `payroll.html` ·
`employees.html` · เทสต์ Payroll* · แตะนอกขอบเขตเล็กน้อย: `PayrollDtos.cs` (DTO) · `IPayrollService.cs` · `Models/Entities/Payroll.cs` (+1 field) ·
`DatabaseMigrationHelper.cs` (+1 ADD COLUMN) · `api.js` (+2 เมธอด) · `tools/required_call_site_check.py`

**สถานะ**: โค้ด + เทสต์ + checker + เอกสารครบ · **ยังไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้ (ไม่มี .NET SDK) — CI เป็นตัวแรก**

## รายการ

| # | สถานะ | สิ่งที่ทำ | ที่ |
| --- | --- | --- | --- |
| **69** | ✅ | ➕/🗑 รอบ `Approved` ⇒ `Calculated` + ล้าง `ApprovedBy/ApprovedAt` (field จริงของ `PayrollRun` — ตัวเดียวกับที่ `CalculatePayrollAsync` ล้างตอนคำนวณใหม่หลังอนุมัติ) ผ่าน `Helpers/PayrollRosterChange.Apply` ตัวเดียว · ข้อความตอบ (`PayrollRunResponse.Notice` → controller message) บอกให้อนุมัติใหม่ · audit เก็บ `StatusBefore` · ✏️ แก้ยอด**ไม่**ผ่านตัวนี้ (checker forbid) | `Helpers/PayrollRosterChange.cs` · `PayrollService.cs:1585` (Add) · `:1844` (Remove) |
| **70** | ✅ | ปุ่ม "🚫 ยกเลิกรอบ" ที่ footer หน้ารายละเอียดรอบ (`voidRun` · prompt เหตุผล) → `POST runs/{id}/void` body `VoidPayrollRunRequest { reason }` · `PayrollRunResponse.CanVoid/VoidBlockReason` จาก `PayrollRunEditPolicy.CanVoid` (ด่านเดียวกับ service) · กดไม่ได้ ⇒ disabled + title เหตุผล · service: เหตุผล ≥ 5 ตัวอักษร (`BusinessRuleException` ข้อความไทย) + `AddChainedAuditLog` (`void-run` · สถานะก่อน · กลับ JE ไหม) · pre-read เปลี่ยนเป็น `AsNoTracking` (เดิม tracked ⇒ `FromSqlRaw … FOR UPDATE` คืน instance เก่า ด่าน "ถูกยกเลิกไปแล้ว" ใต้ล็อกมองไม่เห็นการยกเลิกซ้อน) · `PAYROLL-DETAIL-LAST` ชี้ปุ่มนี้แทน "ผ่าน API" | `PayrollService.cs:3305` · `PayrollController.cs:688` · `payroll.html:1761` `:1821` |
| **71** | ✅ | `PayrollDetailAmounts.ApplyWorkersCompensation` — สูตรเดียวของ ➕ และ ✏️ เมื่อ**ฐาน ปกส. เปลี่ยน** (แก้แค่รายได้/รายการหักไม่แตะ — เงินทดแทนของแถวคำนวณมาจากค่าจ้างตามกฎหมายซึ่งอาจเกินฐานที่เก็บ) | `Helpers/PayrollDetailAmounts.cs` · `PayrollService.cs:1447` |
| **72** | NOT-A-CHANGE | คำตัดสิน "ยอมรับ" — ไม่มีงาน | — |
| **73** | ✅ | ส่วนประกอบบริบทภาษีย้ายจาก inline ใน `CalculatePayrollAsync` **คำต่อคำ** ไป `Helpers/PayrollWithholdingTax` (`Compute` · `Allowances` · `RemainingPeriodsAfter` · `PriorYtd` · `ItemAmount` · `ItemBuckets`) · ค่าคงที่ `PitPersonalAllowance/PitPerDependantAllowance/PitPvdMaxDeductible` ของ service ถูกถอด (อ้าง `ThaiPitCalculator.Default*` ตัวเลขเดียวกัน) · `LoadPriorYtdDetailsAsync` query ยอดสะสมตัวเดียว · `PitBracketsOf` · พรีวิว `PreviewWithholdingTaxAsync` + `POST runs/{id}/tax-preview` (ไม่บันทึก · ด่าน `PayrollRun`) ใช้ `PayrollDetailAmounts.ApplyFields` (ส่วน "ใส่ค่า" ของตัวเติมยอด — `Apply` = `ApplyFields` + ด่านเดิม) บนสำเนาไม่ติดตาม · UI ปุ่ม "🧮 คำนวณภาษีให้" ใต้ช่องภาษีของโมดัลรายคน (ทั้ง ➕ และ ✏️) — เติมเมื่อผู้ใช้ยังไม่พิมพ์ (`dataset.userTouched`) · พิมพ์แล้ว ⇒ ไม่ทับ + ปุ่ม "ใช้ค่าที่ระบบเสนอ" · กล่องที่มา (`Basis`) | `Helpers/PayrollWithholdingTax.cs` · `PayrollService.cs:1758` `:2473` `:4615` `:4645` · `PayrollController.cs:483` · `payroll.html:2113` |
| **X1** | ✅ | `Helpers/PayrollSsoFlagGuard.Check(subject, base, gross, label)` สองทิศ (`!subject && base > 0` · `subject && base ≤ 0 && gross > 0`) → `PAYROLL-SSO-FLAG-MISMATCH` พร้อมทางไปต่อ (ใส่ 0/กรอกฐาน หรือแก้ธงที่หน้าพนักงาน) · ➕ ทุกครั้ง · ✏️ **เฉพาะเมื่อฐานเปลี่ยน** ⇒ แถวนำเข้าที่ขัดอยู่แล้วยังแก้ช่องอื่นได้ (ไม่ใช่ทางตัน) | `Helpers/PayrollSsoFlagGuard.cs` |
| **X2** | ✅ | ✏️ ธุรกรรม + `FOR UPDATE` (tenant) + อ่านรอบ/แถวใต้ล็อก · จ่าย: pre-read `AsNoTracking` (สถานะ/งวดบัญชี/จ่ายซ้ำเดือน) → อ่านรอบ + Details **ใต้ล็อก** (ครั้งแรกที่ติดตาม) → `NormalizeRunSsoAsync` → `RecomputeRunTotals(run)` ก่อน `CreateJournalEntryAsync` · คอมเมนต์ใน Add (เดิม `:1558` "กันเพิ่มซ้อนกับ คำนวณใหม่/อนุมัติ/จ่าย") เป็นจริงแล้วเพราะทุกเส้นเขียนอ่านใต้ล็อกเดียวกัน (ไม่ต้องแก้ถ้อยคำ) | `PayrollService.cs:1447` `:2717` |
| **X3** | ✅ | รอบ `IsExternalImport` ⇒ `ApplyWorkersCompensation` ไม่แตะ (คืน false) ⇒ ➕ คงศูนย์ + `Notice` · ✏️ ฐานเปลี่ยนก็ไม่แตะ + `Notice` | `Helpers/PayrollDetailAmounts.cs` |
| **X4** | ✅ | `PayrollRun.ManualRosterChangedAt` (`timestamptz NULL` · `DatabaseMigrationHelper` ADD COLUMN IF NOT EXISTS) ประทับโดย `PayrollRosterChange.Apply` · `RecalculateWarning(evidence, manualRosterChangedAt)` (พารามิเตอร์บังคับ — ไม่มี overload ตาย) เพิ่มบรรทัด "เพิ่ม/เอาพนักงานออกด้วยมือ (ล่าสุด dd/MM/yyyy) — คำนวณใหม่จะ…" · `CalculatePayrollAsync` ล้างเป็น null หลังคำนวณ · หน้าเว็บใช้ `recalculateWarning` ใน confirm เดิม (ไม่ต้องแก้ JS) | `Helpers/PayrollRunEditPolicy.cs` · `Models/Entities/Payroll.cs` · `PayrollService.cs:2551` |
| **X5** | ✅ | `PayrollDetailAmounts.SetYtd(d, PayrollWithholdingTax.PriorYtd(...))` — ➕ และ ✏️ (ยอด YTD บนสลิปตามยอดที่แก้) ด้วย `LoadPriorYtdDetailsAsync` ตัวเดียวกับเส้นคำนวณ | `PayrollService.cs` Add/Update |
| **X6** | ✅ | ถอด `includeSalary` (จริงเสมอหลัง `CheckPayrollAccessAsync`) จาก service/interface/controller · `PayrollAddableEmployeeDto.BaseSalary` เป็น `decimal` · ตัด branch "ไม่มีสิทธิ์ดูเงินเดือน" ใน `payroll.html` `_edAddPick` · checker forbid `CanViewPayrollAsync(` ใน `GetAddableEmployees` | `PayrollController.cs:473` |

## Golden ก่อน/หลัง (ข้อ 73)

`PayrollWithholdingTaxTests.Golden_…` — `Reference(...)` คือสูตร inline เดิม**คำต่อคำ**จาก `git show 9c9580e6:Accounting/Services/Implementations/PayrollService.cs`
(~2313–2385 · ค่าคงที่/literal เดิม) เทียบกับ `PayrollWithholdingTax.Compute` ทุกช่องของ `PitResult` บน 2,268 กรณี (พนักงาน 7 แบบ — ลดหย่อนรายช่อง/แบบเดิม/
TaxAllowances ติดลบ/พ้นสภาพปีนี้/ปีก่อน/บริจาค · ตาราง 3 แบบ — null/ค่าเริ่ม/กำหนดเองทุกช่อง · ขั้น 3 แบบ · เดือน 4 · ยอดสะสม 3 · ปกส./PVD 3) +
ตัวเลขตายตัว 50,000 ⇒ 20,450/ปี · 1,704.17/งวด · ส่วน earning/deduction (`ItemAmount`) และ `PriorYtd` เป็นนิพจน์เดิมย้ายที่ (เทสต์แยก)

## เทสต์ + ด่าน

- `Accounting.Tests/PayrollWithholdingTaxTests.cs` (5 Fact) · `PayrollRosterChangeTests.cs` (7 Fact + 1 Theory×5) · `PayrollDetailAmountsTests.cs` +6 Fact +1 Theory×4
- `tools/required_call_site_check.py` บล็อก "รอบ 201 ทีม PR2" 13 แถว + แก้ 2 แถวเดิม (D-09 `PriorTaxBase` ย้ายไปล็อกที่ `PayrollWithholdingTax.PriorYtd` · `GetAddableEmployees`
  ไม่ส่ง `CanViewPayrollAsync`) — ล็อก: คำนวณรอบเรียก `PayrollWithholdingTax.Compute`/`LoadPriorYtdDetailsAsync`/`ItemAmount`/`ItemBuckets` + forbid
  `ThaiPitCalculator.Compute(`/`PitAllowances(`/`item.CalculationType` · พรีวิวห้าม `SaveChangesAsync`/`Apply(` + tenant 4 query · ✏️ `FOR UPDATE` ก่อนด่าน/Include ·
  ด่านธงหลัง `Apply` · ➕ `RecomputeRunTotals` → `PayrollRosterChange.Apply` → save · Remove ห้ามข้อความ "/void" · Void เหตุผล + `CanVoid` + chain ก่อน save ·
  จ่าย: ล็อก → Include → Normalize → Recompute → JE · ตัวแปลง response `CanVoid` + `RecalculateWarning(…ManualRosterChangedAt)` · controller ด่านก่อน service
- **negative test ด้วยมือ** (`importlib` โหลด checker → `check_rule` บนข้อความที่ถอด/สลับ) — 17 เคส ผ่านครบ: โค้ดจริง = ไม่ฟ้อง · ✏️ ถอด `FOR UPDATE` ·
  ✏️ ทิ้งผลด่านธง (`_ = flagProblem`) · ✏️ เรียก `PayrollRosterChange.Apply` (ข้อ 69 ทิศตรงข้าม) · ➕ ถอด `PayrollRosterChange.Apply` · ➕ คิดเงินทดแทนเอง
  (`WorkersCompensationBase.Contribution`) · ➕ ส่ง `false` แทน `run.IsExternalImport` · คำนวณรอบกลับไปเรียก `ThaiPitCalculator.Compute` · คำนวณรอบไม่ล้าง
  `ManualRosterChangedAt` · พรีวิวมี `SaveChangesAsync` · ยกเลิกรอบทิ้ง throw เหตุผล · ยกเลิกรอบใช้ `AuditLogs.Add` · จ่ายไม่ `RecomputeRunTotals` · ข้อความคนสุดท้ายกลับไปชี้
  `/void` · ตัวแปลง response ไม่เรียก `CanVoid` · controller พรีวิวไม่มีด่านสิทธิ์ · controller รายชื่อส่ง `CanViewPayrollAsync` กลับมา — ทุกเคสฟ้อง
- checker อื่นที่รันผ่าน: ดูผล `check_all.sh` ในข้อความคอมมิต

## ความเสี่ยงคอมไพล์ (ยังไม่ได้คอมไพล์)

- `PayrollRun run;` ประกาศก่อน `try` ใน `ProcessPaymentAsync` แล้วกำหนดในบรรทัดแรกของ `try` — ใช้หลัง `try/catch { throw; }` (definite assignment ผ่านเพราะ catch โยนต่อ)
- `prior.GetValueOrDefault(id)` บน `Dictionary<Guid, List<PayrollDetail>>` → `List<PayrollDetail>?` ส่งเข้า `PriorYtd(IEnumerable<PayrollDetail>?)`
- `(await GetPayrollRunAsync(...)) with { Notice = … }` — `PayrollRunResponse` เป็น positional record (with ใช้ได้กับ optional param ท้าย)
- `[FromBody] VoidPayrollRunRequest? request = null` — body ว่างได้ (nullable + default) · ไม่มี body ⇒ service ตอบ 400 ไทย
- `PayrollWithholdingTax.ItemBuckets(IEnumerable<(PayrollItem Item, decimal Amount)>)` รับ `List<(PayrollItem Item, decimal Amount)>` (คำนวณรอบ) และ `IEnumerable` จาก `Select` ที่ตั้งชื่อ tuple ตรงกัน (พรีวิว)
- เทสต์: `new List<PitBracket[]?> { null, … }` · `Assert.Equal(PitResult, PitResult)` (record struct)

## คำถามค้าง

1. **พรีวิวภาษีไม่รวมเบี้ยเลี้ยงประจำที่ "บริษัทตั้งเอง" (`CustomAllowancesJson` แบบ Monthly)** ในฐานประจำที่ฉาย — เส้นคำนวณนับเฉพาะเมื่อพนักงานมีแถวลงเวลาในงวด
   (บล็อก attendance) จึงขึ้นกับข้อมูลลงเวลา · พรีวิวบอกใน `Basis` แล้ว · ถ้าต้องตรงทุกกรณีต้องแยก "ตัวรวมเงินได้รายคนของงวด" (earnings + attendance) ออกเป็น helper ตัวเดียว —
   ใหญ่เกินรอบนี้ (📋)
2. ✏️ แก้ยอดรายคนของรอบ **Approved** ยังคงสถานะ (ตามคำตัดสินข้อ 69) — ถ้าแก้ยอดเงินมาก ผู้อนุมัติก็ไม่เห็นเช่นกัน (เป็นทางเลือกของเจ้าของ ไม่ได้แตะ)
3. ด่านธง ปกส. (X1) ใน ✏️ ทำงานเฉพาะเมื่อฐานเปลี่ยน — แถวนำเข้าที่ขัดอยู่แล้วจะไม่ถูกเตือนจนกว่าจะแตะฐาน · ถ้าต้องการให้เห็นทุกแถว ควรเป็น**คำเตือน**บนหน้ารอบ (ไม่ใช่บล็อก)
