# รอบ 200 · ทีม SG — แก้ผลฝ่ายค้านรอบสอง ด้านเงิน/ภาษี settlement + ภ.พ.36

ขอบเขต: `review200-round2-money.md` R2M-2..R2M-13 (R2M-1 แก้แล้วที่ `0ce0ae2f`) · คำตัดสิน `DECISIONS.md` ข้อ **26 · 27 · 40 · 41** ·
worktree reset ไปที่ `506d0417` (origin/claude/erp-system-review-team-660mev) ก่อนเริ่ม · คอมมิตโค้ด `<pending>` (sha เติมในคอมมิตตามหลัง ห้าม amend) ·
**ยังไม่ได้คอมไพล์** (ไม่มี .NET SDK ในเครื่อง — รบกวน rebuild / อ่านผล CI)

## 1. สรุปรายการ

| ID | สถานะ | ที่แก้ (file) | เทสต์ (`Accounting.Tests/SettlementReview200SgTests.cs`) |
|---|---|---|---|
| **R2M-2** (P2 · ข้อ 26) | ✅ | `Helpers/GatewayBatchIntentRules.cs` `VatProblem(…, gatewayWht, …)` — บริษัทไม่จด VAT ผ่อนคู่ VAT ไทย**เฉพาะเมื่อ gateway ไม่หัก ณ ที่จ่าย** · หัก 3% ⇒ กติกาเดียวกับจด VAT (ฐานก่อน VAT ของสองเส้นต้องเท่ากัน) + ข้อความเฉพาะ "ฐานหัก ณ ที่จ่าย/50 ทวิ ต่างกัน" | `R2M2_ไม่จดVAT_หัก3_…3_31กับ3_09_ต้องไม่ตรง` (107 ⇒ 3.31 vs 3.09 + กลับทิศ) · `R2M2_…ด่านผ่านก็ต่อเมื่อWHTสองเส้นเท่ากันจริง` (Theory 4 คู่ — ผูกด่านกับผลของ `GatewaySettlementMath.WhtOnFee` และ `SettlementFeeTax.Compute` จริง ไม่ใช่ตารางจากความจำ) · `R2M2_ทิศตรงข้าม_…X10ไม่ถูกแตะ` |
| **R2M-3** (P2 · ข้อ 40) | ✅ | ใหม่ `ForeignServiceVat.BorneTaxOutsideLines(serviceValue, documentWht, certIncome, certTax)` — ตัดสินจากข้อเท็จจริงบนเอกสาร: เงินได้บน 50 ทวิ ≈ ยอดบรรทัด + ภาษี ⇒ บวก · ≈ ยอดบรรทัด ⇒ gross-up แล้ว ไม่บวก · อื่น ๆ (50 ทวิ บางงวด) ⇒ เอกสารมี WHT บนตัว = รวมแล้ว / ไม่มี = บวก · ผู้เรียก: `TaxService.GeneratePp36Report` (โหลด `TotalIncomeAmount` ด้วย) + `WithholdingTaxCertService.IssueWarningsAsync` | `R2M3_เอกสารคีย์grossup529_41…` (ฐาน **529.41** ไม่ใช่ 608.82 · ไม่เตือน "VAT ขาด 5.56" + ล็อกว่าสูตรเดิมให้ 608.82/5.56 จริง) · `R2M3_ทิศตรงข้าม_ใบค่าธรรมเนียมรอบโอน450…` (ยังได้ 529.41/37.06 · ใบคีย์มือฐานเดิมยังเตือนขาด 5.56) · `R2M3_50ทวิครอบบางงวด…` |
| **R2M-4** (P3) | ✅ | ใหม่ `SettlementFeeTax.Pp36Legs(treatment, serviceValue, payerBorneTax)` = สูตร ภ.พ.36 ตัวเดียว (ภ.พ.36 · ภาษีซื้อ · ค่าใช้จ่าย) ใช้ทั้ง `Compute` (รายก้อน) และ `SettlementBatchMath.BuildFeeLines` (หลายส่วน ⇒ คิดใหม่จากฐานรวม + `whtBorne` ตัวที่ลง 50 ทวิ) | `R2M4_ต่างประเทศออกภาษีแทน_3_65คูณ100…` (50 ทวิ 64.41/429.41 · ภ.พ.36 **30.06** ไม่ใช่ 30.00 · ฐานตรง 50 ทวิ · JE สมดุล) · `R2M4_ทิศตรงข้าม_…` (ก้อนเดียว 30.06 เท่าเดิม · ไม่จด VAT ค่าใช้จ่าย 395.06 ภาษีซื้อ 0 · W2 25.55) |
| **R2M-5** (P3 · ข้อ 26×41) | ✅ | `ModeMismatch(…, companyVatRegistered, channelIncomeTypeMapJson)` → `IncomeTypeProblem` (private): gateway หัก 3% ⇒ ประเภทเงินได้ของ "ค่าธรรมเนียมรับชำระเงิน" (ชนิดเดียวที่ `PaymentIntentAdapter` สร้าง) ตามตัวตัดสิน `SettlementWhtIncomeType.Resolve` ต้องได้อัตรา = `ServiceWhtRate` · ผู้เรียกครบ 5 ทาง: ประกอบจากรายการรับชำระ · นำเข้าไฟล์ · บันทึกช่องทาง (**เปลี่ยนค่าตั้งประเภทเงินได้ = แตะโหมด** ตรวจด้วยค่าใหม่) · บันทึกค่าตั้ง gateway (`ConfigChangeRefusal` tuple 4 ช่อง) · ด่านลงบัญชี | `R2M5_…` (Theory 6: 8ad/8tr/none บล็อก · 8/ค่าตั้งต้น/ประเภทอื่นผ่าน) · `R2M5_ทิศตรงข้าม_gatewayไม่หัก…` |
| **R2M-6** (P3 · X-8) | ✅ | `SettlementImportService.Gateway.LoadIntentRowsAsync`: `DateTime? to = ConfirmedToExclusiveUtc(periodTo ?? payoutDate.AddDays(-1))` (= เที่ยงคืนต้นวันเงินเข้า = จุดตัดยอดคืน) · ติ๊ก X-8 ใน `review200-P2.md` | `R2M6_ไม่กรอกปลายช่วง…` (รับเงิน 23:00 วันที่ 19 เข้า · 10:00 วันเงินเข้าไม่เข้า — ผ่าน `UnclaimedForBatch` จริง) · `R2M6_ทิศตรงข้าม_กรอกปลายช่วง…` |
| **R2M-7** (P3) | ✅ | ทางไปต่อตัวเดียว `GatewayBatchIntentRules.ForeignPp36BoundNextStep` — ข้อความด่านโหมดของช่องทาง ภ.พ.36 ไม่มีส่วน "แก้สองที่ให้ตรงกัน" แล้ว · `PostingIssue(modeMismatch, channelVat)` เลือกทางไปต่อตามชนิดช่องทาง · `SettlementForeignWht.GatewayConfigHint` ใช้ข้อความเดียวกัน (เดิมบอกให้แก้ config gateway ให้ตรง ซึ่งทำตามแล้วยังบล็อก) | `R2M7_…` · `W6_…` (ปรับ — ล็อกว่าสองข้อความบนพรีวิวเดียวกันเป็นทางเดียวกัน) |
| **R2M-8** (P3) | ✅ | ทางไปต่อระบุครบ: สร้างช่องทางใหม่ชนิด Gateway ไม่ผูก config (ผังพักเดียวกับ gateway · โหมด ภ.พ.36) + นำเข้าไฟล์ · ช่องทางเดิมถอดการผูกไม่ได้ ⇒ ยกเลิกรอบค้าง · ห้ามใช้หน้า "บันทึกรอบโอน" เส้นเดิม · **หน้ารายการค้างโอนเส้นเดิม** (`GatewaySettlementService.ListPendingAsync` → `FeeVatWarning`) เตือนเมื่อมีช่องทาง ภ.พ.36 ผูก config นั้น (`LegacyForeignChannelWarning` + `JoinWarnings`) | `R2M8_…` |
| R2M-9 (P3) | 📋 | ประเมินแล้ว — ดู §3 | — |
| **R2M-10** (P3) | ✅ | รายงาน ภ.พ.36 นับ 50 ทวิ ผ่าน `WhtCertFilingScope.Filed` (Issued/Printed) — ร่าง/ยกเลิกไม่นับ | `R2M10_…` + required_call_site (forbid `w.Status != WithholdingTaxCertStatus.Voided`) |
| **R2M-11** (P3) | ✅ | ใหม่ `MoneyAccountFallback.RefundAccountFromSale(method, saleLegDescription, saleJournalLines)` — บัตร/e-Wallet/เช็ค ⇒ ผังเดียวของขาขายเดิมใน JE ของบิล (สองผัง/ไม่พบ ⇒ กติกาปัจจุบัน · เงินสด/โอนใช้กติกาปัจจุบันเสมอ) · `PosService.CreateRefundJournalEntryAsync` อ่าน JE ขาย (tenant ผ่าน `JournalEntry.CompanyId`) · ข้อความขาเงินผ่านตัวสร้างเดียว `SaleMoneyLegDescription` (เส้นขาย + เส้นคืน) | `R2M11_บิลบัตรก่อนdeploy…` · `R2M11_ทิศตรงข้าม_…` |
| **R2M-12** (P3 · ข้อ 15) | ✅ | รหัสใหม่ `SummarySupplementDuplicate = 61` (แยกจาก `SummarySaleDuplicate` ที่ยืนยันไม่ได้) · `SettlementDuplicateSale.DistinctConfirmable` · `SplitDuplicates(…, confirmedDistinct)` — บรรทัดที่ยืนยันแล้วไม่นับเป็นหลักฐาน · ยืนยันครบทุกบรรทัดที่ตรงรอบแรกจึงปลด · `ConfirmRefusal` (เหตุผลบังคับ · บรรทัดต้องอยู่ในปัญหานั้นของด่านปัจจุบัน) · `ISettlementPostingService.ConfirmDistinctLinesAsync` (สิทธิ์ `Settlement.Post` ใน service · ล็อกช่องทาง · ด่านสดใต้ล็อก · ประทับ `SettlementLine.DistinctConfirmedAt/By/Reason` + `AddChainedAuditLog` รายบรรทัด) · `POST settlement/batches/{id}/lines/confirm-distinct` (`[RejectApiKey]` + `[RequirePermission(Post)]`) · ปุ่มใน `settlements.html` + `api.confirmSettlementDistinctLines` · migration ADD COLUMN IF NOT EXISTS 3 คอลัมน์ | `R2M12_ยืนยันทุกบรรทัดแล้วเป็นใบเพิ่มเติม…` · `R2M12_ด่านแยกรหัส_…` |
| **R2M-13** (P3 · ข้อ 27) | ✅ | ใหม่ `SettlementReceiptWht.Remaining` + `SettlementPostingService.RemainingWhtAsync` (การรับชำระที่ไม่ถูกลบ + ใบเสร็จ/ใบสำคัญที่อ้างใบซึ่งไม่ใช่ร่าง/ยกเลิก/ปฏิเสธ — ชุดเดียวกับเพดาน `CreatePaymentAsync` · tenant ทุก query) ใช้ทั้งด่าน (`BuildGateAsync` → `SettlementReceiptTarget.DocumentWht`) และ `EnsureReceiptAsync` | `R2M13_WHTของใบบันทึกครบ…ส่ง0` · `R2M13_ทิศตรงข้าม_…` |

## 2. การเปลี่ยนพฤติกรรมที่ผู้ใช้เห็น (F2 ข้อ 8)

- **เข้มขึ้น** (ทุกข้อมีทางไปต่อในข้อความ):
  (1) บริษัทไม่จด VAT + gateway หัก 3% + โหมด VAT สองที่ต่างกัน ⇒ บล็อกทุกทางเข้า — ทางไปต่อ: แก้โหมดสองที่ให้ตรง (ประกอบใหม่) · ทิศตรงข้าม `R2M2_ทิศตรงข้าม_…`
  (2) ช่องทางผูก gateway หัก 3% ที่ตั้งประเภทเงินได้ค่าธรรมเนียมรับชำระ ≠ 3% ⇒ บล็อก — ทางไปต่อ: ตั้งประเภทเงินได้ให้ได้อัตราเดียวกัน หรือปิดการหักที่หน้า gateway · ทิศตรงข้าม `R2M5_ทิศตรงข้าม_…`
  (3) ประกอบรอบโอนไม่กรอก "ถึงวันที่" ⇒ รายการที่รับเงินตั้งแต่วันเงินเข้าไม่เข้ารอบนี้ (ค้างรอบถัดไป · ถ้าผู้ให้บริการรวมมาจริงให้กรอก "ถึงวันที่" เอง) · ทิศตรงข้าม `R2M6_ทิศตรงข้าม_…`
- **ผ่อนลง**: ใบสรุปเพิ่มเติมที่หน้าตาเหมือนรอบแรกยืนยันรายบรรทัดได้ (แทนการยกเลิกรอบอย่างเดียว) · งวดกลางของใบที่ WHT บันทึกครบแล้วไม่บล็อก ·
  PV ที่คีย์ gross-up ไม่ถูกเตือน "VAT ขาด" ผิดอีก
- **ตัวเลขเปลี่ยน**: ภ.พ.36 บนใบค่าธรรมเนียมหลายส่วน (ไฟล์มีคอลัมน์ VAT) คิดจากฐานรวม (30.00 → 30.06 · หักจากเงินที่จ่าย 26.00 → 25.55) · รายงาน ภ.พ.36 ของใบ gross-up ฐานลดกลับเป็นยอดบรรทัด
  · POS คืนเงินบัตร/e-Wallet/เช็คของบิลก่อน deploy R200G-2 ลงธนาคารที่ปัก (เดิม 11340)

## 3. R2M-9 — 📋 (ประเมินแล้ว ไม่แก้รอบนี้) · ✅ 07baa11b รอบ 201 ทีม ST (A-ST9)

เคสแคบ (id + ป้าย + ยอดเท่ากัน **และ** วันที่ของอีกรายการ = วันที่ตามตัวอักษรของแถวนี้ — คืนเงินบางส่วนยอดเท่ากันสองครั้งข้ามเที่ยงคืนไทย/UTC หรือไฟล์ MDY ที่ตัวแปร DMY ชน) ·
**ไม่เงียบ**: แถวถูกข้ามพร้อมคำเตือนรายแถว "ข้าม…แถวที่นำเข้าแล้ว" · สมการรอบโอน (Σ บรรทัด = ยอดโอน) ไม่ลงตัว ⇒ `Unbalanced` บล็อกที่ลงบัญชี ·
ทางแก้ที่ปลอดภัยต้องเลือกระหว่าง "ถอดคีย์ `Assign(literal)` ออกจากชุดรุ่นก่อน" (ไฟล์เดิมที่นำเข้าก่อนรอบ 200 ด้วยกติกาวันที่ตามตัวอักษรนำเข้าซ้ำได้ — ทิศที่เงียบกว่าและเสี่ยงรายได้ซ้ำ)
กับ "จับคู่รุ่นก่อนเฉพาะแถวที่รอบโอนเดิมนำเข้าด้วยรุ่นนั้น" (ต้องเก็บรุ่นคีย์ต่อบรรทัด = คอลัมน์ใหม่ + migration) ⇒ ตัดสินพร้อมเจ้าของไฟล์ `SettlementTxnKey` (ทีม I) และเทสต์ไฟล์จริง

## 4. F3 ข้อ 7–12

7. `ModeMismatch` ลายเซ็นเดิม (5 อาร์กิวเมนต์) เหลือ **0** (โค้ด 4 + `ConfigChangeRefusal` + เทสต์ 6 จุดอัปเดต) · `PostingIssue(` 1 อาร์กิวเมนต์ เหลือ **0** (โค้ด 1 + เทสต์ 2) ·
   `SplitDuplicates(` 2 อาร์กิวเมนต์ เหลือ **0** (โค้ด 1 + เทสต์ 3) · `Decide(target.WithholdingTaxAmount` / ส่ง WHT ทั้งใบเข้าด่าน เหลือ **0** (forbid) ·
   `ForeignServiceVat.Pp36Base(preVat, borne)` ใน `Compute` ย้ายเข้า `Pp36Legs` (ผู้เรียก 2: Compute + BuildFeeLines) · ข้อความขาเงิน POS `$"รับเงิน {methodLabel} POS #` เหลือ **0** ·
   `IntentConfirmedUpperUtc` ที่ร่างไว้ถูกลบ (dead_helper ฟ้อง `ConfirmedToExclusiveUtc` ไม่มีผู้เรียกนอกไฟล์ ⇒ ผู้ประกอบเรียกตรง)
8. ทางเข้าที่แตะโหมดภาษีค่าธรรมเนียม: ประกอบ ✅ · นำเข้าไฟล์ ✅ · บันทึกช่องทาง ✅ (รวมเปลี่ยนค่าตั้งประเภทเงินได้) · ค่าตั้ง gateway ✅ · ลงบัญชี ✅ · เส้นรอบโอน gateway เดิมใช้ config อย่างเดียว (เตือนเรื่อง ภ.พ.36) ·
   ฐาน ภ.พ.36: รายงาน ✅ · คำเตือนตอนสร้าง/แก้ 50 ทวิ ✅ · ไฟล์ยื่น ภ.พ.36 (`ComputePp36ReportAsync` เรียก `GeneratePp36Report` ตัวเดียวกัน) ✅ ·
   WHT ลูกค้าในรอบโอน: ด่าน ✅ · `EnsureReceiptAsync` ✅ · POS คืนเงิน: เส้นเดียว `CreateRefundJournalEntryAsync`
9. ดู §2 · เทสต์ทิศตรงข้ามชื่อมี "ทิศตรงข้าม"
10. ค่าที่ persist: รายงาน ภ.พ.36 ที่**บันทึกแล้ว**ด้วยฐานเกิน (ใบ gross-up + 50 ทวิ ออกให้ตลอดไป) — ฐานในแถวรายงานเป็นแค่การแสดง (ยอดนำส่ง = VAT ของใบ ไม่เปลี่ยน) ⇒ สร้างรายงานใหม่ได้ ·
    ไม่มี migration ข้อมูล · ใบค่าธรรมเนียมหลายส่วนที่**ลงบัญชีแล้ว**ด้วย ภ.พ.36 รายส่วน (ต่างเป็นสตางค์) ไม่คิดใหม่อัตโนมัติ (ข้อ 20) · JE คืนเงิน POS ที่ลง 11340 ไปแล้ว — SQL ของ `team-GF.md` ใช้หาได้ ·
    คอลัมน์ใหม่ `SettlementLines.DistinctConfirmed*` = NULL ทุกแถว (พฤติกรรมเดิม)
11. ฝ่ายค้าน: งานนี้คือการแก้ผลฝ่ายค้านรอบสอง — ยังไม่ได้ส่ง diff ของทีม SG ให้ฝ่ายค้าน (agent นี้ไม่มีเครื่องมือเรียก subagent) ⇒ ขอ main agent ส่ง
12. DOCUMENT_FLOW §2.10 (แถว "ด่านเพิ่ม รอบ 200 ทีม SG") + §5.4 + Last verified ✅ · TEST_PLAN §0 + SSG-01..09 ✅ · CHANGELOG ✅ · `review200-round2-money.md` ตารางสถานะ ✅ ·
    `review200-P2.md` ติ๊ก X-8 ✅ · ACCOUNT_STRUCTURE ไม่แตะ

## 5. checker ที่รัน (worktree นี้)

ผ่าน: `required_call_site_check` (ตัวเต็ม — ผลอยู่ท้ายรายงานคำตอบ) + negative test มือ 11 เคส (ย้อนการแก้ทีละจุดแล้วกติกาฟ้องทุกเคส) · `settlement_line_type_rules_check` · `record_arg_check` ·
`nullable_arg_check` · `using_check` · `undeclared_local_check` (1,570 ไฟล์) · `gl_code_check` · `dead_helper_check` (ไม่มีตัวใหม่) · `tuple_name_merge_check` · `string_quote_close_check` ·
`comment_line_break_check` · `identifier_space_check` · `html_attr_escape_check` · `onclick_js_string_check` · `enum_number_compare_check` · `accessibility_check` · `arg_type_check` ·
`write_permission_gate_check` · `service_interface_check` · `dto_nullable_contract_check` · `owner_action_wiring_check` · `namespace_shadow_check` · `verbatim_string_check` ·
`test_inventory --check` · `node --check` (settlements.html · api.js) · brace/วงเล็บ ทุก .cs ที่แก้ (PosService.Orders.cs ต่างจากศูนย์เท่า HEAD — สตริง interpolation ตัวนับหยาบ) · U+FFFD ·
**ไม่ได้รัน** `check_all.sh` เต็ม (เครื่องโหลดหนัก)

## 6. ความเสี่ยงคอมไพล์ (ไม่มี SDK)

- `ModeMismatch` เพิ่มพารามิเตอร์บังคับ `string? channelIncomeTypeMapJson` (ท้าย) · `PostingIssue` เพิ่ม `SettlementFeeVatMode channelVat` · `ConfigChangeRefusal` tuple 4 ช่อง — ผู้เรียกทุกตัวแก้แล้ว (grep ทั้งเรพ)
- `SplitDuplicates` เพิ่ม `IReadOnlySet<Guid> confirmedDistinct` — ส่ง `HashSet<Guid>` (ToHashSet)
- `SettlementDuplicateSale` เพิ่มพารามิเตอร์ท้ายแบบ default (`DistinctConfirmable = false`) — ผู้สร้างเดิม 3 จุดสร้างนอก EF expression (หลัง `ToListAsync`)
- `SettlementFeeTax.Compute`: `decimal inputVat, expense;` แล้ว deconstruction assignment `(pp36, inputVat, expense) = Pp36Legs(…)` ในสาขาหนึ่ง / กำหนดตรงในอีกสาขา (definite assignment ครบทั้งสองสาขา)
- `BuildFeeLines`: `var (linePp36, lineInputVat, lineExpense) = cond ? Pp36Legs(…) : (Σ…, Σ…, Σ…)` — สองสาขาเป็น `(decimal, decimal, decimal)` · tuple_name_merge 0
- `TaxService`: `Dictionary<Guid, (decimal Income, decimal Tax)>` ใน ternary กับ `ToDictionary(… (Income: …, Tax: …))` · `filedCertStatuses.Contains(w.Status)` (array ใน EF)
- `SettlementPostingService`: `private sealed record ReceiptDocRow(...)` ใน `Select` ของ EF (ไม่มีพารามิเตอร์ optional) · `new List<ReceiptDocRow>()` กับ `await …ToListAsync(ct)` ใน ternary ·
  `RemainingWhtAsync` ใช้ `d.RelatedDocumentId!.Value` ใน projection · `ConfirmDistinctLinesAsync` static local `Fail` ถูกเรียกใน lambda ของ `JobLock` · `reason.Trim()` หลัง `IsNullOrWhiteSpace` guard
- `PosService.CreateRefundJournalEntryAsync`: ส่ง `IEnumerable<(Guid AccountId, decimal DebitAmount, string? Description)>` เข้าพารามิเตอร์ `(Guid AccountId, decimal Debit, string? Description)` (ชื่อ tuple ไม่มีผลกับ identity conversion)
- `SettlementController.ConfirmDistinctLines`: `request?.LineIds is not { Count: > 0 } ids` แล้ว `request!.Reason`
- `GatewayBatchIntentRules`: `<see cref="IncomeTypeProblem"/>` ชี้เมธอด private (doc comment เท่านั้น)

## 7. คำถามค้าง (เลือกทิศที่มองเห็น/ย้อนได้ไว้แล้ว)

1. **R2M-12 ยืนยันเป็นชุด** — หน้าจอยืนยันทุกบรรทัดของปัญหาด้วยเหตุผลเดียว (ประทับ + audit ทีละบรรทัด) · ถ้าต้องการเลือกทีละบรรทัดบนหน้าจอ ต้องเพิ่มช่องติ๊กในตารางบรรทัด (API รองรับรายการ id อยู่แล้ว)
2. **R2M-12 การยืนยันผูกกับเนื้อหาบรรทัด** — บรรทัดนำเข้าแล้วแก้เนื้อหาไม่ได้ (แก้ได้แค่ประเภท/การจับคู่) จึงไม่ผูก hash · ถ้าอนาคตเปิดให้แก้ยอด/วันที่ ต้องล้างการยืนยันตอนแก้
3. **R2M-11 บิลที่จ่ายบัตรสองทาง (EDC + gateway)** — ขาขายสองผังสำหรับวิธีเดียว ⇒ ไม่เดา ใช้กติกาปัจจุบัน (อาจลงผิดผังเหมือนเดิมในเคสนี้ — มองเห็นได้จากรายงานผังพัก)
4. **R2M-4 ผลข้างเคียง W2 หลายส่วน** (26.00 → 25.55) — สอดคล้องฐานในรายงาน ภ.พ.36 (7.00%) · ถ้าเจ้าของต้องการ "VAT ต่อรายการ" สำหรับ ภ.พ.36 ต้องกลับไปรวมรายส่วนทั้ง W2/W3 (และรายงานจะแสดงอัตราไม่ใช่ 7% เป็นสตางค์)

## ฝ่ายค้านรอบสาม (main agent ส่ง · 2026-10-01) — ผลและการแก้

ไม่พบความเสี่ยงคอมไพล์ · ทดเลขในเทสต์ทุกตัวตรง (3.31/3.09 · 529.41/37.06 · 64.41/429.41/30.06 · 395.06 · 25.55)

| ID | P | สถานะ | ที่แก้ / เหตุผล |
|---|---|---|---|
| SG-1 | P2 | ✅ | R2M-8 เตือนแค่หน้ารายการค้างโอน — `PreviewAsync`/`RecordAsync` ผ่านเงียบ ⇒ `GatewaySettlementService.BuildPlanAsync` บล็อก `SettlementBlockReason.ForeignPp36Bound = 8` (ข้อความ+ทางไปต่อตัวเดียวกับคำเตือน) · query ยุบเป็น `ForeignBoundChannelNamesAsync` ตัวเดียว · required_call_site 2 แถว |
| SG-2 | P3 | 📋 | ไม่กรอกปลายช่วง ⇒ ขอบบน = เที่ยงคืนต้นวันเงินเข้า · ผู้ให้บริการโอน T+0 ⇒ รายการเช้าวันเงินเข้าหลุด ยอดไม่ลงตัว (มองเห็นได้ · ผู้ใช้กรอกปลายช่วงเองได้) — ควรเพิ่มข้อความทางไปต่อตอนยอดไม่ลงตัว | · ✅ 07baa11b รอบ 201 ทีม ST (A-ST3)
