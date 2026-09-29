# รอบ 200 · ทีม W — ภาษีหัก ณ ที่จ่ายจ่ายต่างประเทศ (ภ.ง.ด.54 · ตาราง DTA · ภ.พ.36 / R-A4)

ขอบเขต: `BRIEF.md` แถว W · `DECISIONS.md` ข้อ 13 · `settlement/review198-A.md` R-A4/R-A5 · มุมมอง: นักกฎหมายภาษีไทย + นักบัญชี + วิศวกร

> คอมมิตหลัก: `79f3f8de` · `bash tools/check_all.sh` ผ่านทั้งหมด (checker 53 · simulation 8 · TEST_PLAN §0)
>
> ⚠️ **ยังไม่ได้คอมไพล์** — เครื่องนี้ไม่มี .NET SDK · ต้อง rebuild/test ฝั่ง CI (ดู §5 ความเสี่ยงคอมไพล์)

## 1. สรุป

| # | งาน | สถานะ | ที่ (file) | เทสต์ |
|---|---|---|---|---|
| 1 | ตาราง DTA เป็น OWNER file เดียว | ✅ สร้างโครง + กติกา · **แถว = 0 โดยตั้งใจ** (ยืนยันกับตัวบททางการไม่ได้ — §3) | `Accounting/Helpers/DtaTreatyRates.cs` | `ForeignWhtRateResolverTests.ตารางจริงยังว่าง_ทุกประเทศได้_ม70` |
| 2 | resolver ตัวเดียว (ม.70 → DTA override เมื่อมีแถว + CoR) · log `RuleCode` + `LegalReference` | ✅ | `Accounting/Helpers/ForeignWhtRateResolver.cs` | `ForeignWhtRateResolverTests` (9 เมธอด · 15 InlineData) |
| 3a | ภ.ง.ด.54 รายงาน/ส่งออกรายเดือน | NOT-A-GAP — มีแล้ว (`TaxFilingExportService.ExportPnd54Async` cert-primary · endpoint `GET …/tax-filing/pnd54`) · **เพิ่ม** หมายเหตุอัตราไม่ตรงตัวตัดสิน (`Pnd54RateNote`) | `Services/Implementations/TaxFilingExportService.cs` | TEST_PLAN WHT54-02 (มือ) |
| 3b | กำหนดยื่นจากตารางเดียว | NOT-A-GAP — `Helpers/TaxFilingDeadline` มี `WhtPnd54` (7 วัน · e-Filing +8) ใช้ทั้งปฏิทิน/หน้านำส่ง · ไม่ได้สร้างสำเนา | — | `TaxFilingDeadlineTests` (เดิม) |
| 3c | หนังสือรับรองการหัก | NOT-A-GAP (พฤติกรรมเดิม) — `WithholdingTaxCertService.ResolveWhtFormType` ออกแบบ ภ.ง.ด.54 ให้ผู้รับต่างประเทศแล้ว · settlement ส่ง `fee.WhtForm` · **คำถามกฎหมายค้าง** Q-W2 | `Helpers/SettlementPosting.cs` (`WhtCertificate`) | `SettlementForeignWhtTests` |
| 4 | R-A4 ผู้ไม่จด VAT จ่ายต่างประเทศ ⇒ ภ.พ.36 + VAT เป็นต้นทุน | ✅ **ยืนยันแล้วว่าแก้ไปที่ `84d47dda`** (รอบ 198 ทีม C: `SelfAssessedPp36NotClaimable`) — ติ๊กใน `review198-A.md` · เส้นเอกสารมือ (PV/PI `IsForeignService` บริษัทไม่จด VAT) ก็ถูกอยู่แล้ว: บรรทัด `IsVatClaimable=false` ⇒ VAT เข้าค่าใช้จ่าย + Cr 21912 · หน้านำส่ง ภ.พ.36 ไม่กรองด้วยสถานะจด VAT · **เพิ่มเทสต์ R-A4 × WHT** | `Helpers/SettlementFeeTax.cs` (เดิม) | `RA4_ไม่จดVAT_ต่างประเทศ_ภพ36เกิด_ไม่เคลม_และหักภงด54บนฐานเดิม` + `SettlementPostingTests.RA4_…` (เดิม) |
| 4b | เปิดโหมดหัก WHT ค่าธรรมเนียมแพลตฟอร์มต่างประเทศใน settlement | ✅ แทนการบล็อกเหมา R-A5: 40(2) ค่านายหน้า ⇒ ม.70 15% ลง **21918** + 50 ทวิ แบบ ภ.ง.ด.54 · บล็อกเฉพาะที่ระบบคิดให้ไม่ได้ (§2.2) | `Helpers/SettlementForeignWht.cs` (ใหม่) · `SettlementFeeTax.cs` · `SettlementBatchMath.cs` (เล็กที่สุด) · `SettlementLineTypeRules.cs` · `SettlementPosting.cs` · `SettlementPostingService.cs` | `SettlementForeignWhtTests` (11 เมธอด) |
| 5 | ทางเข้าเอกสารมือ (PI/Expense/PV จ่ายต่างประเทศ) | ✅ คำเตือนตอนอนุมัติเทียบอัตรารายบรรทัดกับ resolver — **เลิกเตือนเหมา** "WHT ≥ 15% ตรวจ DTA ส่วนใหญ่ 5-10%" (ฟ้องใบถูกทุกใบ + ตัวเลขไม่มีแหล่ง) · หักขาด (§54) ดัง | `Services/Implementations/DocumentService.cs` (`CollectApprovalWarningsAsync`) | `ForeignWhtRateResolverTests.เตือนเมื่อหักขาดหรือหักเกิน_เงียบเมื่อตรง` |
| 6 | สำเนาตัวเลข ม.70 | ✅ OCR note · คำอธิบายโหมดช่องทาง · คำเตือนหน้าตั้งค่าช่องทาง อ่านค่าคงที่ของ resolver | `OcrService.cs` · `SettlementReferenceCatalog.cs` · `SettlementChannelService.cs` | — |

## 2. รายละเอียดการตัดสิน

### 2.1 ตัวตัดสิน (pure · `Helpers/ForeignWhtRateResolver.cs`)
1. จำแนกประเภทเงินได้จากรหัสใน `ThaiWhtRateTable` (ตารางประเภทเงินได้ตัวเดียว): 40(2) ค่าธรรมเนียม/นายหน้า · 40(3) ค่าสิทธิ · 40(4)(ก) ดอกเบี้ย ·
   40(4)(ข) ปันผล · 40(5) · 40(6) · **40(1)/(7)/(8) = นอก ม.70** · `40(4)` ไม่ระบุวงเล็บ/รหัสแปลก = ไม่รู้ (ห้ามเดา)
2. นอก ม.70 / ไม่รู้ ⇒ **ไม่มีอัตรา** (`RatePercent = null`) ผู้เรียกบล็อก/เตือน — ห้ามตกไปอัตราในประเทศ (R-A5) และห้ามเงียบเป็น 0
3. มีแถวอนุสัญญา **และ** CoR ครอบวันจ่าย **และ** อัตราอนุสัญญาต่ำกว่า ม.70 ⇒ อัตราอนุสัญญา (`RuleCode = DTA-XX` · `LegalReference` = ชื่ออนุสัญญา+ข้อ)
4. อื่น ๆ ⇒ ม.70 (15% · ปันผล 10%) `RuleCode = RD-70 / RD-70-DIV` · `LegalReference = ป.รัษฎากร ม.70`
5. `RateWarning(decision, usedRate)` — ข้อความเดียวที่ทุกผู้เรียกใช้ (หักขาด อ้าง §54 · หักเกิน · ไม่มีอัตรา) ขึ้นต้นด้วย `[RuleCode · LegalReference]`

### 2.2 settlement (แทนบล็อกเหมา R-A5)
- สัญญาณต่างประเทศบนช่องทาง = `FeeVatMode == ForeignPp36` (ตัวเดียวที่ช่องทางมี) ⇒ WHT แบบ ภ.ง.ด.54 · ผัง 21918 (บทบาทใหม่ `wht_payable_54` → `WhtPayableAccount.Pnd54Code`)
- ออกภาษีแทน: ภาษี = ฐาน × 15/85 (450 → 79.41 · เงินได้บน 50 ทวิ 529.41) · หักเองได้คืน: 450 × 15% = 67.50
- **บล็อก `ForeignWhtNotSupported` เหลือ 3 กรณี** (ทุกกรณีมีทางไปต่อ): ประเภทนอก ม.70 (ค่าบริการ/โฆษณา/ขนส่ง/ค่าธรรมเนียมรับชำระ ที่ตารางประเภทบรรทัดจัดเป็น 40(8))
  เฉพาะบรรทัดนั้น · โหมด "แพลตฟอร์มเป็นตัวแทนหักแทน" (ผู้รับต่างประเทศยื่น ภ.ง.ด.54 แทนผู้จ่ายไม่ได้) · **ผู้ติดต่อของช่องทางมีประเทศ ≠ TH แต่ช่องทางไม่ได้ตั้ง
  `ForeignPp36` และมีขา WHT** (ผู้ให้บริการจด e-Service ที่เก็บ VAT ไทย ⇒ เดิมได้ ภ.ง.ด.53 อัตราในประเทศ = R-A5 อีกรูป — ตัดสินในผู้ลงบัญชีเพราะต้องรู้ประเทศ)
- ด่านเดือนที่ยื่นแล้ว (`TaxPeriodFiled`) อ่านแบบ WHT ตามช่องทาง (53/54) · ข้อความพูดแบบที่ถูก
- ไม่แตะตรรกะของทีม T ใน `SettlementBatchMath` — แก้ 1 บล็อกด่าน (แทนด้วย `SettlementForeignWht.PlanIssues`) + ส่ง `batch.PayoutDate`/`WhtForm`/`foreign` เข้า helper เดิม

### 2.3 ทิศของความเสียหาย (DECISION_DOCTRINE §1)
- ไม่มีแถว/ไม่มี CoR/ไม่รู้ประเทศ ⇒ ม.70 เต็ม = หักเกินถ้าผู้รับมีสิทธิ์ลด ⇒ **มองเห็น** (ผู้รับทวงทันที) · ทิศตรงข้าม (เดาอัตราลด) = หักขาด §54 **มองไม่เห็น**
- ประเภทนอก ม.70 ⇒ บล็อกให้คนจำแนก (ไม่ใช่ "ไม่หัก" เงียบ และไม่ใช่ 15% ที่อาจหักเกินโดยไม่มีฐานกฎหมาย)

## 3. ตาราง DTA — แหล่งอ้างอิงที่พยายามใช้ + เหตุที่ไม่ใส่แถว

**ข้อเท็จจริงของสภาพแวดล้อม**: egress proxy บล็อก `rd.go.th` · `irs.gov` · `mof.gov.sg` · `iras.gov.sg` · `revenue.ie` · `ird.gov.hk` · `mof.go.jp` ·
`chinatax.gov.cn` · `wetten.overheid.nl` · `krisdika.go.th` ทั้ง WebFetch และ curl ⇒ **เปิดตัวบทไม่ได้แม้แต่ฉบับเดียว** · ใช้ได้แค่ WebSearch (บทสรุปของเครื่องมือค้นหา ไม่ใช่ตัวบท)
⇒ ตามคำตัดสินข้อ 13 ("ใส่เฉพาะแถวที่ตรวจกับแหล่งทางการได้") **ไม่ใส่แถวใดเลย**

ตัวบท ม.70 (อัตรา 15% / ปันผล 10% · ครอบ 40(2)(3)(4)(5)(6)) — ยืนยันจากผลค้นหาที่ชี้หน้าทางการ `https://www.rd.go.th/5939.html`
(บทบัญญัติ หมวด 3 มาตรา 65–76) และตรงกับตาราง E ของ CLAUDE.md · ใช้เป็นค่าตั้งต้นของ resolver

| ประเทศ | ข้อที่ต้องยืนยัน (ค่าสิทธิ ข้อ 12 · ดอกเบี้ย ข้อ 11 · ปันผล ข้อ 10 · กำไรธุรกิจ ข้อ 7) | หน้าทางการที่ผลค้นหาชี้ (ยังเปิดไม่ได้) | สิ่งที่พบในบทสรุป (**ยังไม่ยืนยัน — ห้ามใช้**) |
|---|---|---|---|
| สิงคโปร์ (SG) | ฉบับแก้ไข ลงนาม 11 มิ.ย. 2558 | `rd.go.th/fileadmin/download/nation/singapore_t_revise1.pdf` · `rd.go.th/2856.html` (ข้อ 1-5) · `rd.go.th/2855.html` (ข้อ 6-10) | ค่าสิทธิ 5% / 8% / 10% ตามชนิด · ดอกเบี้ย 15% (สถาบันการเงินต่ำกว่า) — จากบทสรุปของสำนักงานกฎหมาย/บริการบริษัท (corporateservices.com · mahanakornpartners.com · luther-lawfirm.com) ไม่ใช่ตัวบท |
| ไอร์แลนด์ (IE) | ข้อ 7/11/12 | `rd.go.th/765.html` (รายชื่ออนุสัญญาที่มีผล) | ไม่ได้ค้น — ไม่เขียนจากความจำ |
| เนเธอร์แลนด์ (NL) | ข้อ 7/11/12 | `rd.go.th/765.html` | ไม่ได้ค้น |
| สหรัฐ (US) | ข้อ 7/11/12 | `rd.go.th/2662.html` (ข้อ 11-15) | ไม่ได้ค้น |
| จีน (CN) · ฮ่องกง (HK) · ญี่ปุ่น (JP) | ข้อ 7/11/12 | `rd.go.th/765.html` · `rd.go.th/14933.html` (ตารางเพดานอัตราค่าสิทธิ) | ไม่ได้ค้น |

**ตั้งใจไม่เขียนอัตราจากความจำลงรายงาน** — ตัวเลขในรายงานมักถูกคัดลอกเข้าโค้ดโดยไม่ตรวจ (ทางเดียวกับที่คำเตือนเดิม "ส่วนใหญ่ลดเหลือ 5-10%" เกิดขึ้น)
**วิธีเติมแถว** (ผู้ทำบัญชีที่เปิดตัวบทได้): เพิ่ม `DtaTreatyRate(...)` ใน `DtaTreatyRates.Rows` พร้อม `LegalReference` (ชื่ออนุสัญญา+ปี+ข้อ/วรรค) + `SourceUrl` + `VerifiedOn` + `EffectiveFrom`
**และในคอมมิตเดียวกัน** เพิ่มช่อง CoR ที่ผู้ติดต่อ (ประเทศถิ่นที่อยู่ + ช่วงที่หนังสือรับรองครอบ) แล้วส่งเข้า `ForeignWhtRateResolver` ทุกผู้เรียก
(`python3 tools/callers.py ForeignWhtRateResolver`) — ไม่งั้นแถวไม่มีผล (ทุกผู้เรียกส่ง `ResidenceCertificate.None`) · แก้เทสต์ `ตารางจริงยังว่าง_…`
**กำไรธุรกิจ (ข้อ 7)**: ค่านายหน้า/ค่าธรรมเนียมแพลตฟอร์มของผู้รับที่ไม่มีสถานประกอบการถาวรในไทย — แถว `FeesCommission` อัตรา 0 + เงื่อนไข "ไม่มีสถานประกอบการถาวร"
+ ต้องมี CoR · ไม่มี CoR ⇒ ม.70 (resolver รองรับแล้ว เทสต์ `มีแถวอนุสัญญา_และCoRครอบวันจ่าย_…`)

## 4. คำถามค้าง (เลือกทิศที่มองเห็นและย้อนได้ไปก่อน)

| ID | คำถาม | ทิศที่ใช้ตอนนี้ |
|---|---|---|
| Q-W1 | ค่าธรรมเนียมรับชำระ/ค่าบริการ/ค่าโฆษณาที่จ่ายแพลตฟอร์มต่างประเทศ เป็น 40(8) (นอก ม.70) หรือ 40(2)/(3) — กรมสรรพากรมีข้อหารือหลายแนว | บล็อกพร้อมทางไปต่อ (ตั้ง "ไม่หัก" หรือทำใบมือ) · ไม่เดา |
| Q-W2 | หนังสือรับรอง "50 ทวิ" ใช้กับการหักตาม ม.70 ไหม (§50 ทวิ ครอบ ม.70?) · แบบฟอร์มมีช่อง ภ.ง.ด.54 ไหม | คงพฤติกรรมเดิม (ออกใบรับรองแบบ ภ.ง.ด.54) — ต้องให้นักกฎหมายยืนยันตัวบท §50 ทวิ |
| Q-W3 | ขยายเวลา e-Filing +8 วัน ครอบ ภ.ง.ด.54 และ ภ.พ.36 ไหม | คงตารางเดิม (`TaxFilingDeadline` ให้ +8) — ถ้าไม่ครอบ ปฏิทินเตือนช้า 8 วัน ⇒ ควรยืนยันก่อน |
| Q-W4 | ช่อง CoR ที่ผู้ติดต่อ | ไม่เพิ่มจนกว่าจะมีแถวอนุสัญญาแรก ("มีช่อง ≠ มีผล") |
| Q-W5 | วันที่จ่ายของค่าธรรมเนียมใน settlement = วันที่รอบโอน (แพลตฟอร์มหักจาก wallet ก่อนหน้านั้น) | ตามสัญญาเดิมของ 50 ทวิ รอบโอน (เดือนภาษี = เดือนรอบโอน) |
| Q-W6 | JE รอบโอนออกภาษีแทนที่ `NetPayout = 0` จะถูก `JournalAnomalyService` ฟ้อง JE-WHT-RATIO (ฐาน Dr = แค่ขาภาษีที่ออกแทน) — เป็นมาแต่เดิมของ W3 ในประเทศด้วย | ไม่แตะ (ตัวสแกนเท่านั้น ไม่บล็อก) |

## 5. ความเสี่ยงคอมไพล์ (ไม่มี .NET SDK)
- `SettlementFeeDocumentPlan` เพิ่มพารามิเตอร์ท้าย `TaxType WhtForm = TaxType.WithholdingTax53` (positional record + default) — ผู้สร้างมีที่เดียว · ไม่มี deconstruct
- `SettlementFeeTax.Compute(..., DateTime? paymentDate = null)` — optional ท้าย · ไม่ได้ใช้ใน EF expression (CS0854 ไม่เกี่ยว)
- `ForeignWhtRateResolver.CategoryOf` / `Resolve(…, treatyTable)` / `DtaTreatyRates.Find(rows, …)` เป็น `internal` (เทสต์เห็นผ่าน `InternalsVisibleTo`)
- cref `ForeignWhtRateResolver.Resolve` มี overload ⇒ อาจได้ warning CS0419 (ไม่ใช่ error)
- pattern `x is not decimal e` แล้วใช้ `e` หลัง `return` (definite assignment) · `a is A or B ? … : …` ใน SettlementFeeTax
- `SettlementPostingService.BuildGateAsync`: เพิ่ม `c.CountryCode` ใน anonymous projection + `gated with { … }` แบบเดียวกับบล็อก StaleDocument ที่มีอยู่
- checker ที่รันผ่าน: using/record_arg/gl_code/string_quote/comment_line_break/identifier_space/undeclared_local/tuple_name_merge/namespace_shadow/dead_helper/test_inventory
  (ผล `check_all.sh` ทั้งชุดดูในข้อความคอมมิต)

## 6. ไฟล์ที่แตะ
ใหม่: `Accounting/Helpers/DtaTreatyRates.cs` · `Accounting/Helpers/ForeignWhtRateResolver.cs` · `Accounting/Helpers/SettlementForeignWht.cs` ·
`Accounting.Tests/ForeignWhtRateResolverTests.cs` · `Accounting.Tests/SettlementForeignWhtTests.cs`
แก้: `Helpers/SettlementFeeTax.cs` · `Helpers/SettlementBatchMath.cs` (บล็อกด่านเดียว + ส่งวันที่/แบบ) · `Helpers/SettlementLineTypeRules.cs` (บทบาท `wht_payable_54`) ·
`Helpers/SettlementPosting.cs` (ป้ายบทบาท · ข้อความด่านเดือนที่ยื่น · 50 ทวิ ใช้ `fee.WhtForm`) · `Helpers/SettlementReferenceCatalog.cs` ·
`Services/Settlement/SettlementPostingService.cs` · `Services/Settlement/SettlementChannelService.cs` · `Services/Implementations/DocumentService.cs` (คำเตือนจ่ายต่างประเทศ) ·
`Services/Implementations/TaxFilingExportService.cs` · `Services/Implementations/OcrService.cs` (ข้อความ [PND54]) · `tools/required_call_site_check.py` (7 แถว) ·
`CLAUDE.md` (ตาราง E: สถานะ DTA) · `DOCUMENT_FLOW.md` (§2.10 ข้อ 4 · 50 ทวิ DTA · ตาราง compliance — แก้ doc ที่อ้างว่ามี DTA override ใน PDF) · `TEST_PLAN.md` ·
`CHANGELOG.md` · `erp-review/2026-09-25/settlement/review198-A.md` (ติ๊ก R-A4/R-A5)
