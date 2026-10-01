# รอบ 201 ทีม TX — ภาษี / ด่านอนุมัติ / มัดจำ (ระยะ 1)

> คอมมิตหลัก: `2d7020af` · ฐาน: `5eed54bf` (branch `claude/erp-system-review-team-660mev`) · ขอบเขต: BACKLOG §1.5 (A-TX1…A-TX10 · A-TX11 = ระยะ 2 ไม่ทำรอบนี้) + C-7 · C-21 · B-7
> ทุกข้อเปิดไฟล์จริงที่ HEAD ก่อนแก้ · **ยังไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้ (ไม่มี .NET SDK) — CI คือ compiler ตัวแรก**

## 1. ตารางรายการ

| ID | สถานะ | ที่แก้ (file:line ณ คอมมิตนี้) | เทสต์ |
|---|---|---|---|
| A-TX1 §65 ตรี ก่อนด่านคำเตือน | ✅ | `DocumentService.cs:13945` `EvaluateSection65TerAsync` (อ่านอย่างเดียว · เพิ่ม tenant บนผังบัญชี) · `:13933` `ApplySection65TerAsync` = ประเมิน→บล็อก→บันทึก · `:19314` ตัวรวบรวมคำเตือนเรียกตัวเดียวกัน · `Helpers/Section65TerApprovalWarnings.cs` (ชุดชนิด `AppliesTo` ตัวเดียว + ข้อที่ยกขึ้น) · `Section65TerValidator.cs` คำอังกฤษทั้งคำ + ตัดคำไทยคล้าย | `Section65TerApprovalWarningGoldenTests` (วัดก่อนเปิด) |
| A-TX3 SoD สองความจริง | ✅ (ตัวตัดสินเดียว + โหมดเงา) · 📋 สวิตช์บังคับ | `ApprovalControlPolicy.cs:63` `SelfApproval` (สามสถานะ `SodVerdict`) · `:76` `DocumentUnknownMakerMode = Shadow` · `SettlementPosting.cs` `SodSelfApproval` ×2 เรียกตัวเดียวกัน (พฤติกรรมเดิม) · `DocumentService.cs:5982` + `:6270` audit `SodShadowWouldBlock` ในธุรกรรมอนุมัติ (`AddChainedAuditLog`) | `Round201TxTests.ATX3_*` · เทสต์รอบโอนเดิมผ่านตัวใหม่ |
| A-TX2 tax point ภ.พ.36 = วันจ่าย | ✅ ตอนอนุมัติ · 📋 เส้นจ่ายชำระภายหลัง | `TaxPointResolver.cs:65` `SupplyKind.ReverseCharge` = `PaymentDate ?? DocumentDate` · `IsReverseCharge`/`KindForApproval` · `DocumentService.cs:6289` | `Round201TxTests.ATX2_*` |
| A-TX9 JE-WHT-RATIO รอบโอนออกภาษีแทน | ✅ | `JournalPostingGuard.cs:91` พารามิเตอร์ `externalWhtIncomeBase` (ใช้ค่าที่มากกว่า) · `JournalAnomalyService.cs:64-93` เงินได้ 50 ทวิ (ออกแล้ว) ของใบค่าธรรมเนียมในรอบโอน · ยืนยัน: ลงบัญชีรอบโอนไม่ผ่าน guard นี้ (ตัวสแกนเท่านั้น) | `Round201TxTests.ATX9_*` (W3 3% · 15% · ทิศตรงข้าม 107%) |
| A-TX7 ธงริบตัดสินด้วย UTC | ✅ | `DepositKindDocumentRules.RealizeDateOrToday` · `DocumentService.cs:3783` · `:4143` | `Round201TxTests.ATX7_*` (31/01 17:30 UTC ⇒ 01/02) |
| A-TX5 แถว Modified ก่อนล็อก | ✅ | `DepositKindDocumentRules.LockReloadPlan` · `DocumentService.cs:16963` ล้มดัง `DEPOSIT-LOCK-ORDER` (500) — ตรวจผู้เรียกครบ 6 จุด ไม่พบจุดที่แก้ก่อนล็อก · แถวที่ล็อกแล้วในธุรกรรมเดียวกัน (ล็อกซ้ำ re-entrant) ยกเว้น (คอมมิตตามหลัง) | `Round201TxTests.ATX5_*` |
| A-TX6 resolve ได้ใบเดียวจากสองเลข | ✅ | `DepositReversalMath.cs:80` (`referencedCount` · `UnattributedUndue`) · `DocumentService.cs:8896` | `Round201TxTests.ATX6_*` |
| A-TX8 ธ.ค.→ม.ค. ก่อนวันที่ 15 + หมายเหตุ §86 | ✅ | `DepositPolicyResolver.cs:825` ตัดเงื่อนไขข้ามปี (กำหนดยื่นจาก `TaxFilingDeadline` ตัวเดียว · ปีบัญชีที่ปิดครอบด้วย `depositPeriodLocked`) · `Section86LateIssueNote` ต่อท้ายทุกหมายเหตุต่างเดือน | `DepositRound194R2Tests.R21_*` (ปรับเทสต์ที่ล็อกพฤติกรรมเดิม + ทิศตรงข้ามสองแบบ) |
| A-TX10 sync สแกนบันทึกทั้ง context | 📋 | — | — |
| A-TX4 ใบแทนเข้า flow ลายเซ็นเต็ม | 📋 | — | — |
| A-TX11 ใบกำกับรายงวด | ระยะ 2 (ไม่ทำรอบนี้ตามคำสั่ง) | — | — |
| C-21 IsRetailApproved ก่อนสวิตช์ ภ.พ.06 | ✅ | `AbbreviatedTaxInvoiceRule.cs:101` · ข้อความหน้า `admin/site-settings.html` | `PosSlipHeaderTests.C21_*` + ปรับสองเทสต์เดิม |
| B-7 ปฏิทิน ภ.ง.ด.54/ภ.พ.36 | ✅ (ส่วนที่ทำได้ก่อน) | `TaxFilingDeadline.cs:115` `WarnBy`/`WarnByFor`/`EFilingCaveat` · `StatutoryRemittanceService.cs:404` (`BuildItem`) · `:761` (`BuildCell`) · DTO `WarnDueDate/WarnNote` · `TaxComplianceChecker.DeadlineFor` · `tax-remittance.html` | `Round201TxTests.B7_*` |
| C-7 ทิป 40(1)/40(2) | 📋 | — | — |

**คอมมิต**: `2d7020af` (งานหลัก) · `0a3e4f48` (ติ๊กต้นทาง) · `f8ec4a9e` (A-TX5 ล็อกซ้ำ re-entrant + ย้ายกติกา B-10) · `93c407c2` (merge `b95ed89a` + doc-comment `SodSelfApproval` หลัง A-ST7)
· หลัง merge: `required_call_site_check` เต็มชุด **763 กติกาผ่าน** (+ negative test ในตัว) · checker ชุด F รายตัวผ่าน · `test_inventory --check` ล้มเพราะจำนวนเทสต์ (§0 ให้ main agent วางทับตาม BRIEF)

**required_call_site_check**: บล็อก "รอบ 201 ทีม TX" 13 แถว (must/must_re/before/forbid/call_args) · negative test มือ: โหลด checker ด้วย `importlib` แล้ว `check_rule` กับข้อความที่ถอด
ทุก must / must_re · ใส่ทุก forbid · ส่ง null แทนอาร์กิวเมนต์ call_args — ฟ้องครบทุกแถว (0 พลาด)

## 2. รายละเอียดการตัดสินใจ

- **A-TX1 วัดก่อนเปิด**: ไม่มี `ApprovalWarningGolden` ในเรพ (DECISION_AUDIT T-A ยังเป็นแผน) ⇒ ทำ golden เฉพาะของคำเตือนชุดนี้บนตัวตรวจจริง: ใบปกติ 11 แบบ (เงินสดย่อยไม่มีเลขภาษี ·
  คีย์มือไม่มีเลขใบกำกับ · ค่ารับรองไม่เกินเพดาน · อุปกรณ์ < 50,000 · refined/reservation/fine-tuning · ค่าปรับปรุง · เครื่องปรับอากาศ · เงินเพิ่มเติม) = 0 คำเตือน ·
  ข้อที่ฟ้องใบปกติจำนวนมาก ((11)(18) ไม่บล็อก · (9) · (8)) **บันทึกอย่างเดียว** (ยังอยู่ใน `NonDeductibleRuleJson` + กล่อง "ข้อสังเกต §65 ตรี" หน้าเอกสาร) ·
  การเปิดคำเตือนเผยบั๊กตัวตรวจคำเดิม 3 คลาส ("fine" จับ "refined" · "reserve" จับ "reservation" · "ค่าปรับ" จับ "ค่าปรับปรุง") ⇒ แก้ในคอมมิตเดียวกัน (CLAUDE §H)
  — ผลข้างเคียง: ใบใหม่ที่เคยถูกบวกกลับผิดจากคำเหล่านี้จะไม่ถูกบวกกลับแล้ว (ทิศที่ถูก) · ใบที่อนุมัติไปแล้วคงค่าเดิม (ไม่ย้อน)
- **A-TX3 โหมดเงา**: ไม่มีที่วางสวิตช์ "เจ้าของกดบังคับ" ในไฟล์ที่ทีมถือ (หน้าแอดมิน/`AdminController` = ทีม PL) ⇒ โหมดเป็นค่าคงที่ `DocumentUnknownMakerMode`
  (เปลี่ยนเป็น `Enforce` = คอมมิตเดียว + เทสต์ล็อกค่าปัจจุบันไว้) · ร่องรอย = audit chain (ค้นด้วย ruleCode)
- **A-TX2**: เดิมรายงาน ภ.พ.36 (`TaxPointDate ?? DocumentDate` — MIN กับวันบนใบผู้ขาย) กับหน้านำส่ง (`PaymentDate ?? DocumentDate`) เป็นสองความจริง ·
  ตอนนี้ตอนอนุมัติได้ค่าเดียวกัน · **ยังไม่ครบ**: ใบตั้งหนี้บริการต่างประเทศที่จ่ายทีหลัง — `CreatePaymentAsync` ไม่ปรับ `TaxPointDate`/`PaymentDate` เป็นวันจ่าย (ระยะ 2 · หลัง DV merge)
- **C-21**: ใบที่ออกเป็นอย่างย่อตอนสวิตช์ปิดและบริษัทไม่ใช่ขายปลีก คงเดิม (เลข/บทบาทตรึงตอนอนุมัติ §86/4) — ไม่มี migration
- **B-7**: ปฏิทินภาษี (`TaxCalendarService`) เตือนตาม `DueDate` = วันกระดาษ และแสดง `EFilingDueDate` คู่กันอยู่แล้ว (ไม่ต้องแก้) · จุดที่เตือนตามวัน e-Filing คือหน้านำส่ง/ปฏิทินนำส่ง/ตัวตรวจรายงาน — ย้ายไปอ่าน `WarnBy` ตัวเดียว
- **A-TX9**: ฐาน = max(ขา Dr ที่ไม่ใช่ภาษี, เงินได้ตาม 50 ทวิ ที่ออกแล้วของใบค่าธรรมเนียมในรอบ) — ไม่บวกซ้ำขาภาษีที่ออกแทน · 50 ทวิ ยังไม่ออก ⇒ ฟ้องตามเดิม (มองเห็น)

## 3. 📋 ที่ไม่ทำรอบนี้ (เหตุผล)

- **A-TX4**: ทั้งไฟล์ `DocumentService.Reissue.cs` เป็นของทีม DV · ขึ้นกับ A-TX3 ที่ยังเป็นโหมดเงา ⇒ ทำหลัง DV merge + เจ้าของสั่งบังคับ SoD
- **A-TX10**: คำตัดสินข้อ 58 ให้รอเทสต์ DB จริง (A-PL2) · ทางที่เสนอ (`ExecuteUpdate` เฉพาะคอลัมน์) **ข้าม audit hash chain** ของแถว `OcrScanResult` (audit เกิดจาก ChangeTracker ใน
  `SaveChangesAsync`) — ต้องตัดสินก่อนว่ายอมให้แถวสแกนออกจาก audit ได้ไหม หรือใช้ context แยก · ตาข่ายเดิม `DiscardUnsavedEntry` ยังอยู่
- **C-7**: ตรวจที่ HEAD พบว่า **`TipPayoutService` ไม่มีทางเข้าเลย** (ไม่มี controller/หน้า/ผู้เรียก — มีแต่ลงทะเบียน DI) ⇒ ค่าตั้งต่อบริษัทที่สร้างตอนนี้ = "มีช่อง ≠ มีผล" ·
  และ "ยอดสะสมจริงต่อผู้รับ" ไม่มีที่เก็บ (JE รวมยอดตามชื่อ · 50 ทวิ ออกเฉพาะเมื่อหัก) ⇒ ต้องมีตารางบันทึกการจ่ายทิปรายคน = entity ใหม่ใน `AccountingDbContext` (ไฟล์ทีม PL) ·
  ข้อนี้เป็น "ต่อสาย หรือ ลบ" ที่ต้องให้เจ้าของเลือก (ห้ามเดา)

## 4. ความเสี่ยงคอมไพล์ (ไม่มี SDK)

- `Section65TerApprovalWarnings` (namespace `Accounting.Helpers`) อ้าง `Accounting.Services.Implementations.Tax.Section65TerValidator` ผ่าน using — Helpers ตัวแรกที่ทำแบบนี้ (namespace_shadow_check ผ่าน)
- `DepositKindDocumentRules.LockReloadPlan` รับ `IEnumerable<(Guid Id, Microsoft.EntityFrameworkCore.EntityState State)>` — ผู้เรียกส่ง `(e.Entity.Id, e.State)` (ชื่อ tuple ต่างกันได้)
- `ApprovalControlPolicy.BlocksDocumentApproval` เป็น `internal` (เทสต์เห็นผ่าน `InternalsVisibleTo`)
- record ที่เพิ่มพารามิเตอร์ optional ท้าย: `DrivesUnrealizeSplit(…, decimal UnattributedUndue = 0m)` · `PendingRemittanceItem(…, DateTime? WarnDueDate = null, string? WarnNote = null)` ·
  `FilingCalendarCell(…, DateTime? WarnDueDate = null)` — ผู้สร้างทุกจุดอยู่ใน `StatutoryRemittanceService` (callers.py) · ไม่มีใน EF expression
- `JournalPostingGuard.Validate(…, decimal externalWhtIncomeBase = 0m)` — ผู้เรียกเดิม 3 จุด + เทสต์ไม่ต้องแก้
- `JournalAnomalyService` ใช้ `foreach (var (jeId, feeIds) in dict)` (deconstruct KeyValuePair · .NET 8 มี)

## 5. คำถามค้าง

1. **API v1 กับคำเตือน §65 ตรี** — ตอนนี้เป็นคำเตือนทั่วไป ⇒ API v1 ปฏิเสธพร้อม `APPROVE-WARNINGS-NEED-ACK` (เหมือนคำเตือนชนิดอื่น) สำหรับใบที่มีรายจ่ายต้องห้ามจริง ·
   ถ้าต้องการให้ API ผ่านแล้วคืนธง (แบบ [Σ-GAP]) ต้องขยายคำตัดสินข้อ 12 — ทิศที่เลือก = มองเห็น (คนรับทราบ)
2. **A-TX3 เปิดบังคับ** — ดูจำนวนแถว `SOD-SHADOW-MAKER-UNKNOWN` ใน audit ก่อน แล้วเจ้าของสั่ง (เปลี่ยนค่าคงที่ หรือให้ทีม PL ทำสวิตช์บนหน้าแอดมิน)
3. **C-7** — ต่อสาย `TipPayoutService` (endpoint + หน้า + ตารางการจ่ายทิปรายคน) หรือลบ
4. **A-TX10** — แถวสแกนออกจาก audit chain ได้ไหม (ExecuteUpdate) หรือใช้ DbContext แยก

## 6. ไฟล์ทีมอื่นที่แตะ (เล็กที่สุด)

- `wwwroot/admin/site-settings.html` (ข้อความช่วยเหลือของสวิตช์ ภ.พ.06 2 บรรทัด — ข้อความเดิมผิดหลัง C-21)
- `Services/Implementations/JournalAnomalyService.cs` · `StatutoryRemittanceService.cs` · `Tax/TaxComplianceChecker.cs` · `Models/DTOs/StatutoryRemittanceDtos.cs` — ไม่มีทีมถือใน §5
- `Helpers/SettlementPosting.cs` เฉพาะ `SodSelfApproval` ×2 (ตาม §5) · `Helpers/TaxFilingDeadline.cs` เมธอดใหม่ต่อท้ายคลาส (ทีม PL B-9 จะแก้ `RollToBusinessDay` — คนละช่วง)

## 7. แก้ผลฝ่ายค้าน (RTX-1..9) — คอมมิต `f13f4a23` · หลัง merge `3436774f` (worktree เดิม · merge origin `b8376803`)

| ID | สถานะ | ที่แก้ | เทสต์ |
|---|---|---|---|
| RTX-1 (P1) | ✅ | `Section65TerApprovalWarnings` — `RD-65ter(5)` เข้าชุดบันทึกอย่างเดียว · `ApprovalAckSource.Unattended` (รูปสามอาร์กิวเมนต์ของ `ApproveDocumentAsync` = ใบประจำ · ใบเบิก · เบิกล่วงหน้า · LINE ×2 · OCR อนุมัติอัตโนมัติ · integration) + `ApiClient` ส่งผ่านชุด §65 ตรี (`Section65TerApprovalWarnings.IsWarning`) · หมายเหตุ/audit `APPROVE-UNATTENDED-PASSED-S65` · API v1 คืน `nonDeductibleExpense`/`nonDeductibleNotes` · หน้าเว็บ/มือถือ/อนุมัติหลายใบ (`None`) ยังต้องรับทราบ | golden 54410 60k · 51210 120k · 52130 250k = 0 · `RTX1_ทางเข้าไม่มีคน_*` (สองทิศ) |
| RTX-2 (P2) | ✅ ไม่ยกขึ้น | `RD-65ter(4)` เข้าชุดบันทึกอย่างเดียว | golden "ค่ารับรองต้นปี" = 0 คำเตือน แต่ finding ยังมียอด 47,000 |
| RTX-3 (P2) | ✅ | `Section65TerValidator` — (6)(6 ทวิ)(1)(2)(3) เฉพาะผัง 5xxxx/CIT/ไม่ผูกผัง ⇒ **ตัวรวม ภ.ง.ด.50 บวกกลับน้อยลงสำหรับใบใหม่** (บันทึกใน DOCUMENT_FLOW §3.2/§5.3) | golden 21920/11920/31200 `TotalAddBack = 0` · ทิศตรงข้าม 59100 CIT = 6bis · ไม่ผูกผัง = (3) |
| RTX-4 (P2) | ✅ | regex `EnglishPenaltyWord` (surcharges · fined · ยกเว้น fuel surcharge) | golden สองทิศ |
| RTX-5 (P2) | ✅ | `Helpers/ExpenseClaimPayVoucher` (`StepFor` · `WarningsMessage`) · `ExpenseClaimService.MarkAsPaidAsync` ผูก PV ร่างก่อนอนุมัติ · กดซ้ำใช้ใบเดิม · ออกแล้ว = ไม่อนุมัติซ้ำ · `DocumentApprovalWarningsException` ⇒ 422 `EXPENSE-PAY-PV-WARNINGS` | `RTX5_*` |
| RTX-6 (P3) | ✅ | เรียง `WarnDueDate ?? EFilingDueDate` · `WarnByFor(…, holidays)` + `TaxComplianceChecker` โหลด `PlatformHolidayStore` · `EFilingCaveat(type, due, today)` บอก "เลยวันกระดาษแล้ว ยื่นอินเทอร์เน็ตได้ถึง … (ยังไม่ยืนยัน)" | `RTX6_*` |
| RTX-7 (P3) | ✅ | ฟิลด์ล็อกมัดจำย้ายขึ้นก่อน doc-comment ของ `LockDepositBalancesAsync` | — |
| RTX-8 (P3) | 📋 | ต้องนับในฐานจริงก่อน — backfill ค่าตรึงแบบเดาไม่ได้ (ไม่มีประวัติสวิตช์ ภ.พ.06 ณ วันออกใบ) · ผลของ C-21 กับใบเก่า: เกิดเฉพาะเมื่อ **สวิตช์ ภ.พ.06 ถูกปิดอยู่ตอนนี้** + บริษัทไม่ใช่ขายปลีก + ใบ `IsTaxInvoiceByLaw IS NULL` · คำค้นให้ main agent/เจ้าของรันก่อนตัดสิน (ด้านล่าง) | — |
| RTX-9 (P3) | ✅ | รูปสามอาร์กิวเมนต์ = `withAiHints: false` (ครอบ 6 ทางเข้าที่ไม่มีหน้าจอ) | — |

คำค้น RTX-8 (อ่านอย่างเดียว):
```sql
SELECT d."CompanyId", count(*)
FROM "Documents" d JOIN "Companies" c ON c."Id" = d."CompanyId"
WHERE d."IsTaxInvoiceByLaw" IS NULL AND NOT d."IsDeleted" AND d."VatAmount" > 0
  AND d."Status" NOT IN (0, 1)            -- ตรวจเลขสถานะ Draft/WaitingApproval กับ enum ก่อนรัน
  AND c."IsVatRegistered" AND NOT c."IsRetailApproved"
  AND (SELECT s."RequirePhoR06ForAbbreviatedTaxInvoice" FROM "SiteSettings" s LIMIT 1) = false
GROUP BY d."CompanyId";
```
ถ้าได้ 0 แถว (สวิตช์ไม่เคยปิด = ค่าตั้งต้น) ⇒ ไม่ต้อง backfill · ถ้ามี ⇒ ตรึงด้วย DO block ใน `DatabaseMigrationHelper` (คีย์ advisory lock คงที่) ให้ค่าตรึง = หัวที่พิมพ์ก่อน C-21

**คำตอบคำถามค้าง (main agent)**: Q1 = ข้อ 110 (ทำแล้ว) · Q2/Q3 = ข้อ 108 (SoD คงเงา · ทิป: ต่อสาย `TipPayoutService` ก่อน — รอบถัดไป) · Q4 = รอ db-test และห้ามแถวสแกนหลุด audit chain

**ความเสี่ยงคอมไพล์เพิ่ม**: `ApprovalAckSource.Unattended = 4` (switch expression อื่นในเรพไม่ครอบ enum นี้แบบครบชุด — ตรวจแล้วมีแค่ในไฟล์เดียว) · `EFilingCaveat` overload (string) / (string, (DateTime, DateTime)?, DateTime?) · `WarnByFor` 4 อาร์กิวเมนต์ ·
`existingPv` ternary ระหว่าง anonymous type กับ `null` · ชื่อ `ExpenseClaimPayVoucher.StepFor` (เดิมชื่อ `Decide` — `nullable_arg_check` สับสนกับ `Decide(bool)` ตัวอื่น ⇒ เปลี่ยนชื่อ)
