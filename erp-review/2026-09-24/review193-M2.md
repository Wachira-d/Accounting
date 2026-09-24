# ฝ่ายค้านรอบ 193 · งานทีม M2 (`fc503b5`) — เงินเดือน #35 · POS สูตร/void · pos-packages · อัตรา 0

> อ่านอย่างเดียว · ไม่มี .NET SDK = **ยังไม่ได้คอมไพล์** ตรวจจากการอ่านซอร์สบน `claude/erp-system-review-team-660mev`
> (HEAD `a217cc1` — merge ของทีม W หลัง `57a30c6` **ไม่แตะ**ไฟล์ที่ตรวจในรายงานนี้ เลขบรรทัดด้านล่างยืนยันแล้วบน HEAD)
> `bash tools/check_all.sh` = exit 0 (checker 49 ตัว + sim 5 ตัว + TEST_PLAN §0) · `required_call_site_check` เขียว
> สเปก: DECISIONS #35 "คำนวณใหม่เฉพาะรอบที่ยังไม่จ่าย · รอบที่จ่าย/ยื่นแล้วห้ามแก้"

## สรุป

| # | ระดับ | เรื่อง | ทิศ |
|---|---|---|---|
| C1 | CONFIRMED · P0 | หลักฐาน "ยื่นแล้ว" อ่านจาก `ComplianceFiling` ซึ่ง**ไม่มีหน้าจอไหนเขียน** · ที่ที่ผู้ใช้บันทึกการยื่นจริง (ปฏิทินภาษี → `TaxCalendarEvent`) **ไม่ถูกอ่าน** · และ confirm บน payroll.html บอกผู้ใช้ให้ไปบันทึกที่ปฏิทินภาษี "ระบบจะล็อกรอบให้" = **ไม่จริง** | ล็อกหลุด (เงียบ) |
| C2 | CONFIRMED · P1 | ล็อก "นำส่งแล้ว" บนรอบ Approved ทำงาน**เฉพาะกรณีที่ผิด** (เดือนที่มีหลายรอบ) และทางไปต่อที่ข้อความบอก ("กลับรายการนำส่ง") **ไม่มีในระบบ** | ล็อกผิด + ทางไปต่อไม่มีจริง |
| C3 | CONFIRMED · P1 | "✏️ แก้ยอด" รายคนบนรอบ Approved ที่ยื่นแล้วยังแก้ได้ — และข้อความล็อกเอง**ชี้ให้ไปใช้**ทางนี้ | #35 "ห้ามแก้" หลุด |
| C4 | CONFIRMED · P1 | AuditLog "ข้าม JE ขาย" ตอน void เขียน `_db.AuditLogs.Add` ตรง ⇒ อยู่นอก hash chain | กฎ M append-only |
| C5 | CONFIRMED · P1 | คอมมิชชันแพ็คเกจยังคิดกลับด้านตอนขาย ไม่มีคำเตือน ณ จุดที่เงินไหล (ขาย/สรุปคอมพนักงาน) · รายงาน commission-review ไม่มี UI เรียก | เงินผิดต่อเนื่องเงียบ |
| C6 | CONFIRMED · P2 | `required_call_site_check` เป็นแค่ "มีคำนี้ในเมธอดไหม" — mutation จริง 7 แบบ จับได้ 1 · ฟ้องผิดเมื่อขึ้นบรรทัดใหม่ก่อน `.Decide(` | ด่านตรวจไม่ครอบสิ่งที่อ้าง |
| P1–P7 | PLAUSIBLE | ดูหัวข้อ PLAUSIBLE | |

---

## CONFIRMED

### C1 — หลักฐาน "ยื่นแล้ว" อ่านผิดตาราง: หน้าจอที่ผู้ใช้บันทึกการยื่นจริงไม่ถูกนับ (P0)

**ฝั่งอ่าน (ด่าน)** — `PayrollService.cs:4361-4368` อ่าน `ComplianceFilings` (FilingType `RD_WHT`/`SSO_Contribution`, FormCode `PND1`/`SSO1-10`, Status `Filed`/`Accepted`)
+ `TaxReports` ชนิด WHT1/SocialSecurity (สร้างใหม่ไม่ได้แล้ว — ทีม M2 ยืนยันเอง) + `EFilingExports` PND.1 (สร้างใหม่ไม่ได้แล้วเช่นกัน)

**ฝั่งเขียนที่ผู้ใช้เข้าถึงได้จริง**
- `ComplianceFiling` — `api.js:1497-1499` มีเมธอด แต่ **0 หน้า**ใน `wwwroot/pages` เรียก (grep `compliance/filings`/`ComplianceFiling`/`getComplianceFilings` นอก api.js = 0) · เขียน Filed ได้ทาง API ตรงเท่านั้น (`ComplianceController.cs:30` PUT · `:51` submit)
- **ปฏิทินภาษี** (`tax-calendar.html:193-205` → `api.updateTaxEvent` → `TaxCalendarController.cs:30` → `TaxCalendarService.UpdateEventAsync:191` `evt.Status = request.Status`) เขียน **`TaxCalendarEvent`** (TaxFormCode `"ภ.ง.ด.1"` / `"สปส.1-10"` — `TaxCalendarService.cs:80,84` · Month ตรงงวด) — ตารางนี้**ไม่อยู่ใน** `LoadRecalculateLockEvidenceAsync` (และไม่อยู่ใน `required_call_site_check` RULES ข้อ 3)

**ผล**: ผู้ใช้ที่ยื่น ภ.ง.ด.1 แล้วกด "อัพเดต → Filed" ที่ปฏิทินภาษี (ทางเดียวบนจอ) → รอบ Approved ของเดือนนั้นยัง **คำนวณใหม่ได้** ⇒ ตัวเลขในระบบเปลี่ยนหลังยื่น = สิ่งที่ #35 ห้าม · ในทางปฏิบัติ ชั้น "ยื่นแล้ว" ทั้งชั้นแทบไม่เคยเป็นจริงบนข้อมูลใหม่
(ComplianceFiling ไม่มี UI · TaxReport/EFilingExport เกิดใหม่ไม่ได้)

**ที่แย่กว่า — ข้อความโกหกสองจุด**
- `payroll.html:1271` confirm "คำนวณใหม่": *"ให้บันทึกการยื่นที่ปฏิทินภาษีก่อน (ระบบจะล็อกรอบให้)"* — ทำตามแล้ว**ไม่ล็อก** (หลักการ #7 ข้อความที่ระบุสาเหตุต้องตรวจสาเหตุนั้นจริง)
- `PayrollRunEditPolicy.cs:86-87` ทางไปต่อเมื่อถูกล็อก: *"เปลี่ยนสถานะการยื่นของงวดนี้ (หน้าปฏิทินภาษี/รายงานภาษี) กลับเป็นยังไม่ยื่น"* — ถ้าล็อกมาจาก `ComplianceFiling` การแก้ที่ปฏิทินภาษีไม่ปลดอะไร (ปลดได้ทาง `PUT /compliance/filings/{id}` เท่านั้น ซึ่งไม่มีหน้าจอ)

**แนวแก้**: เพิ่มแหล่ง `TaxCalendarEvent` (`CompanyId` · `!IsDeleted` · `Year` · `Month` · `TaxFormCode ∈ {"ภ.ง.ด.1","สปส.1-10"}` · `Status == "Filed"`) ในตัวหาเดียว + เพิ่มคำใน RULES ของ checker + เทสต์ "Filed ที่ปฏิทินภาษี = ล็อก / Pending = ไม่ล็อก" ·
หรือยุบสองตาราง (ComplianceFiling ↔ TaxCalendarEvent เป็นปฏิทินซ้ำสองชุด — ญาติของ "ตัวตั้งตัวเดียว" F2 #4) — เรื่องหลังให้เจ้าของเลือก

### C2 — ล็อก "นำส่งแล้ว" บนรอบ Approved จับได้แต่กรณีที่ผิด · ทางไปต่อไม่มีจริง (P1)

- ยอดนำส่ง ปกส./ภ.ง.ด.1 มาจากรอบ **Paid เท่านั้น**: `StatutoryRemittanceService.cs:121-124` (SSO) · `:145-146` (ภ.ง.ด.1 `d.PayrollRun.Status == Paid`) · ตอนนำส่งประทับ `SsoSettledAt` เฉพาะ Paid (`:1161-1164`)
- รอบ Approved จึง**ไม่เคย**อยู่ในยอดที่นำส่ง — ยกเว้นเคยเป็น Paid แล้วถูกกลับรายการ ซึ่ง `CanRecalculate` ล็อกไว้แล้วด้วย `reopenedAt` (`PayrollRunEditPolicy.cs:74`)
- ⇒ สาขา `RemittedForms` (`PayrollRunEditPolicy.cs:88-92`) ที่เช็คแบบ**รายเดือน** (`PayrollService.cs:4405-4407`) ทำงานเฉพาะเมื่อ**เดือนเดียวกันมีรอบอื่น**ที่ Paid+นำส่งแล้ว

ตัวอย่าง: ก.ย. 2026 รอบเงินเดือน Paid 30/09 + นำส่ง สปส. ที่หน้านำส่ง (แถว `StatutoryRemittance` SsoSps110 9/2026) · รอบโบนัส ก.ย. Approved (ยังไม่จ่าย ยอดไม่อยู่ในเงินที่นำส่ง) →
กดคำนวณใหม่ได้ *"งวดของรอบนี้นำส่ง สปส.1-10 ไปแล้ว … ต้องกลับรายการนำส่งที่หน้านำส่งภาษี/ประกันสังคมก่อน"*
- **ไม่มีเส้นกลับรายการนำส่ง**: `StatutoryRemittanceController` มีแค่ GET ×4 · POST `""` (`:61`) · `pp36/recognize` (`:95`) · `{id}/receipt` (`:125`) — ไม่มี reverse/delete
- `PayrollService.ReverseSsoSettlementAsync` (หน้าเงินเดือน ~`:4646`) ล้าง `SsoSettledAt` แต่ **ไม่แตะแถว `StatutoryRemittance`** (0 จุดอ้างถึงในช่วงเมธอด) ⇒ หลักฐานค้างถาวร
- ⇒ รอบโบนัสคำนวณใหม่ไม่ได้ตลอดไป · ทางเดียวที่เหลือคือ ✏️ แก้ยอดทีละคน (ดู C3)

เทสต์ `รอบที่ยังไม่อยู่ในไฟล์ยื่น_หลักฐานการยื่นของงวดไม่ล็อก` ครอบเฉพาะ Draft/Calculated — ไม่มีเคส "Approved ในเดือนที่รอบอื่นนำส่งแล้ว"

**แนวแก้**: หลักฐานนำส่งสำหรับรอบ Approved ต้องผูกกับ**รอบ** (มีอยู่แล้วคือ `SsoSettledAt` ของรอบนั้น) ไม่ใช่เดือน · หรือถอดชั้นนำส่งออกจากการตัดสินรอบ Approved ทั้งชั้น (ไม่มีกรณีจริงที่ถูก) · แก้ข้อความทางไปต่อให้ตรงกับเส้นที่มีจริง

### C3 — "✏️ แก้ยอด" บนรอบ Approved ที่ยื่นแล้ว ยังแก้ได้ — ข้อความล็อกชี้ให้ไปทางนี้เอง (P1)

- `PayrollRunEditPolicy.CanEditAmounts(status)` (`:29-31`) ดูแค่สถานะ: Calculated/Approved = แก้ได้ · `UpdatePayrollDetailAsync` (`PayrollService.cs:1464`) และ `MapToPayrollRunResponse` (`:4429`) ไม่ส่งหลักฐาน
- ข้อความล็อก `CanRecalculate` เองบอกให้ใช้ ✏️: `:73` (นำเข้า) · `:78` (เคยจ่ายแล้วกลับรายการ — *"ยอดชุดเดิมอาจถูกยื่นแล้ว … แก้รายคนด้วย ✏️"*) · `:97` (ปันต้นทุนแล้ว)
- ⇒ ล็อก "คำนวณใหม่ทั้งรอบ" แต่ปล่อย "แก้เงินเดือน/ภาษี/ปกส. รายคน" บนรอบเดียวกันที่อยู่ในไฟล์ ภ.ง.ด.1/สปส.1-10 แล้ว = คำตัดสิน #35 "รอบที่ยื่นแล้ว**ห้ามแก้**" หลุดทางที่สอง
  (ทีม M2 ยกเป็นคำถามเจ้าของข้อ 2 เอง — แต่ข้อความ #35 ครอบอยู่แล้ว และการที่ข้อความล็อก**แนะนำ**ทางนี้ทำให้กลายเป็นทางหลักของการเลี่ยง)
- อีกเส้นที่ขยับยอดรอบ Approved หลังยื่น: ขั้น "จ่าย" เรียก `NormalizeRunSsoAsync` (`PayrollService.cs:2334`) ปรับยอด ปกส. ฝั่งนายจ้าง — ก่อนมีรอบนี้ก็เป็นแบบนี้ ไม่ใช่ถดถอย แต่ควรอยู่ในคำตัดสินเดียวกัน

### C4 — AuditLog "ข้าม JE ขาย" อยู่นอก hash chain (P1)

- `PosService.Orders.cs:256` `_db.AuditLogs.Add(new AuditLog { … EntityType = "PosOrder.VoidSaleJournalSkipped" … })`
- `AccountingDbContext.SaveChangesAsync` (`:3503-3514`) ใส่ hash เฉพาะแถวจาก `CaptureAuditEntries` · แถวที่ Add ตรง = `RowHash = null` · ตัว verify กรอง `RowHash != null` (`AuditTrailService.cs:107`) ⇒ **มองไม่เห็นทั้งตอนตรวจ chain**
- มี `AddChainedAuditLog` (`AccountingDbContext.cs:3522`) ที่ทีม P2/W เพิ่มในรอบเดียวกันไว้แก้คลาสนี้โดยตรง (คอมเมนต์บอกว่า "ยังมีอีก 9 จุด") — M2 เพิ่มจุดที่ 10
- แถวนี้คือหลักฐานเดียวที่ตอบผู้สอบบัญชีว่า "ทำไมยกเลิกบิลแล้วไม่มี JE กลับรายการ" ⇒ ต้องอยู่ใน chain · แก้: `_db.AddChainedAuditLog(new AuditLog{…})` (ต้องแน่ใจว่า `_db` เป็นชนิด `AccountingDbContext` ใน PosService)

### C5 — คอมมิชชันแพ็คเกจยังคิดกลับด้านตอนขาย ไม่มีคำเตือน ณ จุดที่เงินไหล (P1)

- เส้นขายไม่เปลี่ยน: `PosService.Orders.cs:2084` `CommissionType == Fixed ? value : TotalAmount × value / 100`
- ป้ายเตือนมีแค่ในตารางขั้นตอนของหน้า pos-packages (`pos-packages.html:204-205`) + ฟอร์มแก้ขั้นตอน (`:284-285`)
- `GET …/pos/packages/commission-review` (`PosController.cs:271`) — **0 ผู้เรียก**ใน `wwwroot` · หน้าสรุป/รายละเอียดคอมมิชชันพนักงาน (`PosService.Orders.cs:1956-1991`) แสดง `CommissionAmount` ไม่มีธง · ตอนขายไม่เตือนอะไร
- ตัวอย่าง (แพ็คเกจ 1,000 บาท):

  | ค่าที่เก็บ | ผู้ใช้ตั้งใจ | ตอนขายคิด | ส่วนต่าง |
  |---|---|---|---|
  | 0 · มูลค่า 50 | คงที่ 50 บาท | 1,000 × 50% = **500 บาท** | จ่ายเกิน 450 บาท/ครั้ง |
  | 1 · มูลค่า 10 | 10% = 100 บาท | คงที่ **10 บาท** | จ่ายขาด 90 บาท/ครั้ง |

  พนักงานได้เงินผิดทุกบิลจนกว่าเจ้าของจะตัดสิน (คำถามเจ้าของข้อ 1 ของ M2) — ระหว่างนี้ควรมีป้ายที่หน้าสรุปคอมมิชชัน/การจ่ายคอม หรือบล็อกการ "จ่ายคอม" ของกิจกรรมที่ `ComponentId` ยังต้องตรวจ
- (มีอยู่ก่อนแล้ว ไม่ใช่ของ M2 · เป็นเส้นเดียวกัน) query ขั้นตอนตอนขาย `:2073-2076` ไม่กรอง `!c.IsDeleted` ⇒ ขั้นตอนที่ลบแล้วยังได้ค่าคอม · ไม่กรองบริษัท (อาศัยว่า `ServicePackageId` ผ่านการตรวจแล้ว)

### C6 — `required_call_site_check` ตรวจแค่ "มีคำนี้ในเมธอด" (P2)

รันบนสำเนาใน scratchpad (ไม่แตะเรพ) — ใส่การถดถอยจริงทีละแบบ:

| # | การถดถอยที่ใส่ | ผล |
|---|---|---|
| M1 | ลบ `UnitCostOverride: restockCost` ใน `ApplyRecipeConsumptionAsync` | ✅ จับได้ |
| M2 | refund ยังเรียก `LoadSaleUnitCostsAsync` แต่ไม่ส่ง `saleUnitCosts` ต่อให้ `ApplyRecipeConsumptionAsync` | ❌ ผ่าน |
| M3 | void ยังเรียก `PosVoidSaleJournal.Decide` แต่กลับ `order.JournalEntryId` เสมอ (กลับซ้ำเหมือนเดิม) | ❌ ผ่าน |
| M4 | Calculate โหลดหลักฐานแล้วส่ง `PayrollRunLockEvidence.None` เข้า `CanRecalculate` | ❌ ผ่าน |
| M5 | ถอด `if (!canRecalc) throw` | ❌ ผ่าน |
| M6 | ตัด `"Filed"` ออกจากหลักฐาน + ปิด `SsoSettledAt` | ❌ ผ่าน |
| M7 | ยังเรียก `RecipeCost(moved)` แต่คืน `moved.Sum(...)` (นับวัตถุดิบไม่ track กลับมา) | ❌ ผ่าน |
| M8 | ย้าย `RemoveRange` ขึ้นก่อน `CanRecalculate` + เปลี่ยนชื่อตัวแปร | ⚠️ จับได้ทางอ้อม — ฟ้อง "self-test: สลับ … แล้ว checker ไม่ฟ้อง" (ข้อความชี้ผิด) เพราะกติกา `before` ข้ามเงียบเมื่อหา `RemoveRange(run.Details)` ไม่เจอ |
| FP1 | จัดรูปแบบ `PosVoidSaleJournal\n    .Decide(` (โค้ดถูกต้อง) | ❌ **ฟ้องผิด** "ไม่เรียก Decide" |

- ข้อ 7 ในคอมมิต ("ขาคืนที่ไม่ส่งต้นทุนวันขาย 0 จุด") **ไม่ได้ถูกล็อก** (M2) · ตามหลักการ #6 "checker ที่ฟ้องผิด = checker ที่พัง" — FP1 เป็นรูปแบบปกติของ C#
- ไม่ต้องทิ้ง checker — แต่ควร: ทำ normalize ช่องว่างก่อนค้น · ให้กติกา `before` ที่หา b ไม่เจอฟ้องตรง ๆ · เพิ่มกติกาอาร์กิวเมนต์ (เช่น `userId, saleUnitCosts)` · `lockEvidence);`) · และเขียนในรายงาน/คอมมิตให้ตรงว่าล็อก "มีการเรียก" ไม่ใช่ "ใช้ผลการเรียก"

---

## PLAUSIBLE

- **P1 หลักฐานยื่นผูกกับเดือน ไม่ใช่รอบ** — รอบโบนัสที่อนุมัติ**หลัง**วันยื่นไม่อยู่ในไฟล์ที่ยื่น แต่ถูกล็อก (ทิศเข้ม ยอมรับได้) · ทางปลดมีแค่ API PUT compliance (ดู C1) ·
  `EFilingExport` = "ระบบสร้างไฟล์" ถูกนับเป็น "ถูกบันทึกว่ายื่น" (`PayrollService.cs:4400-4402` → ข้อความ `:84`) ขัดหลัก "ดาวน์โหลด ≠ ยื่น" ที่ทีมเขียนเอง และแถวนั้นไม่มีสถานะ/ลบไม่ได้ ⇒ เดือนนั้นล็อกถาวร (กระทบเฉพาะข้อมูลเก่า)
- **P2 BOT sync ทับแถว Manual ที่ Mid = 0 แต่ Buy/Sell ถูก** — `BotExchangeRateService.cs:103-117` ทับ Buy/Sell/Mid + `Source = "BOT"` ⇒ Buy/Sell ที่ผู้ใช้กรอกหาย (มีแค่ LogInformation) · และขัดกับ `AiSuggestionController.cs:1380-1384` ที่นับ (Buy>0 && Sell>0) เป็น "ใช้ได้" — เกณฑ์ "ใช้ได้" มีสองชุดในคอมมิตเดียว · ขึ้นกับว่าแถว A03 ที่ค้างมี Buy/Sell หรือไม่ (ยังไม่ได้ดูข้อมูล) · แถวที่ Mid > 0 ไม่ถูกทับ ✅
- **P3 ทางเข้าอื่นของ "JE ขายถูกกลับด้วยมือ"** — `RefundOrderAsync` ไม่ดูสาย JE ขาย ⇒ ถ้าผู้ทำบัญชีกลับ JE ขายไปแล้ว การคืนเงินยังลง Dr รายได้/Cr เงินสด ⇒ รายได้ติดลบ (defect class เดียวกับที่ M2 แก้ที่ void)
- **P4** `LoadJournalChainAsync` (`PosService.Orders.cs:1478-1498`) ไม่กรอง `!j.IsDeleted` ⇒ JE ขายที่ถูก soft-delete แต่ยัง Posted จะถูกตัดสิน "กลับใบเดิม" แทนที่จะเข้าข้อความ "หา JE ไม่พบ (อาจถูกลบ)"
- **P5 ต้นทุนคืน** — `SaleUnitCostByProduct` ถัวทั้งบิล: ถ้า FIFO + วัตถุดิบเดียวกันหลายบรรทัดที่ต้นทุนออกต่างกัน มูลค่าคลังที่กลับกับ COGS ที่ JE กลับต่างกันเล็กน้อย · บิลก่อนเฟส 0 (OUT เก็บค่าบวก — `StockMovementSign` บอกไว้) ไม่ผ่าน `Quantity < 0` ⇒ ใช้ต้นทุนวันนี้ (พฤติกรรมเดิม)
- **P6** `JsonStringEnumConverter` (`Program.cs:803`) รับตัวเลขด้วย ⇒ หน้าเก่าที่ cache อยู่ส่ง `1` (ตั้งใจ %) ผ่าน `EnsureDefined` แล้วประทับ `CommissionTypeConfirmedAt` = ลบป้ายของค่าที่กำกวมที่สุดทิ้ง (ความเสี่ยงต่ำ ขึ้นกับ cache)
- **P7** `IsDeactivateOnly` เทียบชื่อแบบไม่สนตัวพิมพ์ (`CommissionPlanRules.cs` SameText) ⇒ ปิดใช้งาน + เปลี่ยนตัวพิมพ์ชื่อพร้อมกัน = ชื่อไม่ถูกบันทึกเงียบ ๆ (silent no-op เล็ก)

---

## ตรวจแล้วไม่มีปัญหา

- **CompanyId** — ทั้ง 5 query ใน `LoadRecalculateLockEvidenceAsync` (`:4361-4392`) · `LoadJournalChainAsync` ทุกขั้น · `LoadSaleUnitCostsAsync` · ชุด TrackStock ของวัตถุดิบ · รายงาน commission-review (ผ่าน `Package.CompanyId`) · BOT sync
- **คีย์งวด** ของหลักฐาน (`run.Year/Month`) ตรงกับไฟล์ยื่น (`TaxFilingExportService.cs:31-32, 89`)
- **ทางเข้าคำนวณใหม่** — `CalculatePayrollAsync` มีผู้เรียกจริง 1 จุด (`PayrollController.cs:331`) · นำเข้า (`ImportPayrollRunAsync`) สร้างรอบใหม่ ไม่ทับ · ปุ่มบนจอและด่านใช้ตัวหาเดียวกัน (`:1607`, `:1653`, `:4417`)
- **`SsoWageBase.ForPeriod`** ประกอบเหมือน inline เดิม (`fc503b5^:PayrollService.cs:2043-2062`) ทุกขั้น: หักลา → `GrossWage` → `PeriodBase` (floor 1,650/เพดานตามปี ผ่าน `Clamp`) → `Contribution` ปัด AwayFromZero · `subjectToSso=false` ยังคืนค่าจ้างให้กองทุนเงินทดแทน = ผลเท่าเดิมทุกสตางค์สำหรับพนักงานปกติ
- **internal** — `InternalsVisibleTo Accounting.Tests` มีอยู่ (`Accounting.csproj:82`) · ผู้เรียก `GrossWage/SalaryPaidThisPeriod/PeriodBase` นอก helper มีแต่เทสต์ (`SsoWageDecisionTests.cs:36,128` · `SsoUnpaidLeaveWageTests.cs:55,130`) · โค้ดจริง 0 จุด
- **`CanRecalculate` 4 อาร์กิวเมนต์** — ผู้เรียกครบ (service 2 · เทสต์ 16) · named arg `allocatedRows:` ตรงพารามิเตอร์
- **`PosVoidSaleJournal`** ถูกต้องตามความหมายของ `ReverseJournalEntryAsync` (กลับเต็มใบเสมอ · ใบเดิม→Reversed + `ReversedByEntryId` · `systemTriggered: true` กลับ "ตัวกลับ" ได้): กลับครบ→ข้าม · กลับแล้วกลับคืน→กลับใบสุดท้าย · สายยาวเกิน 20 ใบ → ใบท้ายเป็น Reversed → บล็อก (ไม่กลับผิดใบ) · ไม่พบเส้นที่ทำให้กลับซ้ำ
- **`LoadSaleUnitCostsAsync`** — `Reference == OrderNumber` ตรงตัว (unique `CompanyId+OrderNumber` — `AccountingDbContext.cs:2378`) · `REFUND-`/`VOID-` ไม่ปน ⇒ คืนเงินบางส่วนหลายครั้งได้ต้นทุนชุดเดียวกัน · สินค้าเดียวกันหลายบรรทัดถัวตามจำนวน
- **`RecipeCost`** ตัดสินด้วย `TrackStock` ชุดเดียวกับฝั่งซื้อ (Dr สินค้าคงเหลือเฉพาะ TrackStock — `DocumentService.cs:14078, 14347`) · ledger ยังขยับจำนวนของทุกวัตถุดิบเหมือนเดิม · ขาคืนไม่ใช้ `TotalCost` ที่คืนมา (JE คืนใช้ COGS ที่ตรึงไว้)
- **migration** `ADD COLUMN IF NOT EXISTS` idempotent · อยู่หลัง `CREATE TABLE "ServiceComponents"` (`DatabaseMigrationHelper.cs:724` < `:3593`) · `timestamp` ใช้กับ legacy switch (`Program.cs:50`)
- **`IsDeactivateOnly`** เปิดช่องแก้อัตราข้ามด่าน**ไม่ได้**: ช่องที่กำหนดแผนต่างจากเดิมช่องใดช่องหนึ่ง → false → `Validate` เต็ม · เปิดใช้งาน → `Validate` เต็ม
- **`CurrencyRateSync`** ไม่ทับแถวที่ Mid > 0 · วันเดียวมีหลายแถวเลือกแถวที่ใช้ได้ก่อน · ธปท. ส่ง 0 → ไม่เขียน
- **ความเสี่ยงคอมไพล์จากการอ่าน** — ไม่พบ: สมาชิก/DbSet/enum ที่ใช้มีจริงทั้งหมด (`ComplianceFilings` `TaxReports` `EFilingExports` `StatutoryRemittances` `EmployeeProjectTimes` · `IsAllocated`/`AllocatedPayrollRunId` · `TaxReportStatus.Draft` · `FilingLockedAt` · `JournalEntryStatus.Reversed` · `AuditAction.Update`) · using ครบ · ไม่มีชื่อตัวแปรซ้ำในเมธอด (`lockEvidence` `canRecalc` `actorId` `saleUnitCosts` `deactivatedCounts`) · `new[] { run }` เข้า `IReadOnlyCollection<PayrollRun>` ได้ · `.Select(MapComponent)` ไม่มีพารามิเตอร์ optional (ไม่ใช่ CS0411) · ไม่มี DI ใหม่ · `ImplicitUsings` เปิดในเทสต์ (`ArgumentNullException`)

## ลำดับที่แนะนำ
1. C1 (ต่อสาย `TaxCalendarEvent` + แก้ข้อความ payroll.html:1271 / Policy:86-87) — ตอนนี้ด่าน #35 "ยื่นแล้ว" ใช้กับข้อมูลใหม่ไม่ได้จริง
2. C2 + C3 ต้องตัดสินพร้อมกัน (หลักฐานผูกกับรอบ · ✏️ ใช้หลักฐานชุดเดียวกัน) — ข้อ C3 ต้องให้เจ้าของยืนยันขอบเขต "ห้ามแก้"
3. C4 เปลี่ยนเป็น `AddChainedAuditLog` (บรรทัดเดียว)
4. C5 ป้ายที่หน้าสรุปคอมมิชชัน/จ่ายคอม ระหว่างรอเจ้าของตัดสินข้อมูลเก่า
5. C6 ปรับ checker ตามข้อเสนอ
