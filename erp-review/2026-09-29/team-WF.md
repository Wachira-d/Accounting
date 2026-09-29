# รอบ 200 · ทีม WF — แก้ผลฝ่ายค้านทีม W (ม.70 · ภ.ง.ด.54 · ภ.พ.36)

ขอบเขต: `review200-W.md` W-1…W-10 · `DECISIONS.md` ข้อ 13 · **40** (ฐาน ภ.พ.36 รวมภาษีออกแทน) · **41** (ค่าธรรมเนียมแพลตฟอร์มต่างประเทศ 40(2) + ตั้งต่อช่องทาง)

> คอมมิต: `939bbdfc` (เติมในคอมมิตตามหลัง ห้าม amend) · **ยังไม่ได้คอมไพล์** — เครื่องนี้ไม่มี .NET SDK ⇒ rebuild/test ฝั่ง CI

## 1. สรุป

| ID | สถานะ | ที่ (file) | เทสต์ |
|---|---|---|---|
| W-1 (P1) | ✅ | `Helpers/SettlementForeignWht.cs` `GateWhtForm` · `Helpers/SettlementPosting.cs` `Evaluate` · `Services/Settlement/SettlementPostingService.cs` `BuildGateAsync` | `SettlementForeignWhtTests.ด่านเดือนที่ยื่นแล้ว_ต่างประเทศพูดว่า_ภงด54` (ไม่ส่ง `WhtFormType` ก็พูด 54) · `SettlementForeignWhtFollowupTests.W1_แผนมีขาภงด54_ชนะแบบที่ผู้เรียกส่งมา_ไม่ว่าจะส่งอะไร` · `W1_ทิศตรงข้าม_ช่องทางไทย_ผู้ลงบัญชีเป็นเจ้าของแบบ_3หรือ53ตามผู้รับ` |
| W-2 (P1) | ✅ ก่อนทีมนี้ (`34d4dd6f`) | ยืนยันแล้ว — ตอนแก้ W-1 เจอกติกาทีม T ที่ล็อก `WhtFormType: whtForm` จึงส่งผลของ `GateWhtForm` (เรียกซ้ำได้ผลเดิม) | required_call_site_check |
| W-3 (P1 · ข้อ 40) | ✅ | `Helpers/ForeignServiceVat.cs` (`Pp36Base` · `SelfAssessedVatOn` · `Pp36Shortfall`) · `Helpers/SettlementFeeTax.cs` (ภ.พ.36 หลัง WHT) · `TaxService.GeneratePp36Report` (ฐานบรรทัด + ภาษีออกแทนจาก 50 ทวิ "ออกให้ตลอดไป" ที่ผูกใบ) · `WithholdingTaxCertService.IssueWarningsAsync` (เส้นคีย์มือ/OCR) | `W3_ออกภาษีแทน_450_เงินได้52941_ภพ36เป็น3706` · `W3_ทิศตรงข้าม_หักจากเงินที่จ่าย_ไม่หัก_ช่องทางไทย_ฐานเดิม` · `W3_ไม่จดVAT_ออกภาษีแทน_ภพ36บนฐานรวม_เป็นต้นทุน` · `W3_เอกสารคีย์มือ_ภพ36ต่ำกว่าฐานรวมภาษีออกแทน_บอกส่วนขาด_ตรงแล้วเงียบ` · ปรับตัวเลขใน `SettlementForeignWhtTests` 2 เมธอด (31.50 → 37.06 · 481.50 → 487.06) |
| W-4 (P2 · ข้อ 41) | ✅ | ใหม่ `Helpers/SettlementWhtIncomeType.cs` · `SettlementBatchMath.ComputeTax` (1 บรรทัด + block body) · `SettlementForeignWht.PlanIssues` · entity `SettlementChannel.WhtIncomeTypeMapJson` + `DatabaseMigrationHelper` (ADD COLUMN IF NOT EXISTS) · `SettlementChannelUpsertRequest`/`SettlementChannelView` (+`WhtIncomeTypes`) · `SettlementChannelService` (ตรวจ/บันทึก/audit snapshot/echo) · `SettlementReferenceCatalog` (`FeeIncomeTypes` · `WhtIncomeCodes`) · `settlement-channels.html` | `W4_*` 7 เมธอด (ต่างประเทศ 4 ประเภท 40(2) · ไทยเหมือนเดิมทุกประเภท · ค่าตั้ง none/40(3)/8 · ตัวอ่านปฏิเสธคีย์ค่าเสีย · ค่าตั้งเสียบล็อก · ตัวเลือกหน้าจอ) |
| W-5 (P2) | ✅ | `wht.html` (แบบ ภ.ง.ด.54 · `data-foreign-rate` · userTouched · แสดงคำเตือน) · `/api/reference/income-types` (+`foreignRate`/`foreignRuleCode`) · `WithholdingTaxCertService.CreateAsync/UpdateAsync` → `Warnings` · `GetTaxFormName` ใช้ `WhtUnissuedCertGate.FormLabel` | `ForeignWhtPayeeCheckTests.หนังสือรับรองภงด54ที่ออกเอง_เตือนรายแถว_แถวที่ถูกเงียบ` |
| W-6 (P3) | ✅ (ข้อความเท่านั้น) | `SettlementForeignWht.GatewayConfigHint` ต่อท้ายทางไปต่อของ `PlanIssues` เมื่อช่องทางผูก config gateway — **ไม่แตะ `GatewayBatchIntentRules.ModeMismatch`/เส้นลงบัญชีของทีม SF** | `W6_ช่องทางผูกgateway_…` |
| W-7 (P3) | ✅ | ใหม่ `Helpers/ForeignWhtPayeeCheck.cs` · `DocumentService.CollectApprovalWarningsAsync` | `ForeignWhtPayeeCheckTests` 4 เมธอด |
| W-8 (P3) | 📋 | ปิดพร้อมแถวอนุสัญญาแรก + ช่อง CoR (Q-W4) | — |
| W-9 (P3) | ✅ | ค่าถอนเงินของช่องทางต่างประเทศ = 40(2) (ผ่านตัวตัดสิน W-4 · ในประเทศยังไม่หัก) | `W4_ต่างประเทศ_ค่าธรรมเนียมแพลตฟอร์ม_402_หัก15_ไม่บล็อก(WithdrawalFee)` |
| W-10 (P3) | ✅ | `documents.html` แบนเนอร์อ่าน `GET /api/reference/foreign-wht` (ตัวเลขจาก `ForeignWhtRateResolver`) | TEST_PLAN WF-06 (มือ) |

## 2. กติกาที่ตั้ง (ตัวตั้งเดียวทุกข้อ)

- **แบบ ภ.ง.ด. ของขา WHT รอบโอน** (`GateWhtForm`): ขาค่าธรรมเนียมผู้ให้บริการต่างประเทศ ⇒ แผนเป็นเจ้าของ (54 · ตัวเดียวกับ 50 ทวิ/21918) · ในประเทศ ⇒ ผู้ลงบัญชี
  (3/53 ตามผู้รับ C-11) · ผู้เรียกลืมส่งแบบ ⇒ ยังพูด 54 กับรอบโอนต่างประเทศ
- **ฐาน ภ.พ.36** = มูลค่าบริการ + ภาษีเงินได้ที่ผู้จ่ายออกแทน (ข้อ 40) · ลำดับ: เงินได้รวมภาษีออกแทน → WHT → ภ.พ.36 · 450 ⇒ 529.41 ⇒ **37.06** · หักจากเงินที่จ่าย/ไม่หัก/ประเภท
  นอก ม.70 (ไม่มีภาษีออกแทนจริง) ⇒ ฐานเดิม 450 ⇒ 31.50 · JE รอบโอน: ใบค่าธรรมเนียม VAT override = 37.06 (Dr 11640 หรือเข้าต้นทุนเมื่อไม่จด VAT / Cr 21912) — ขาเงินอื่นไม่เปลี่ยน
- **รหัสประเภทเงินได้ของค่าธรรมเนียม** (`SettlementWhtIncomeType`): ค่าตั้งช่องทาง (`"none"` = ไม่หัก) → ต่างประเทศ: ค่าคอม/ค่าธรรมเนียมรับชำระ/ค่าบริการแพลตฟอร์ม/
  ค่าถอนเงิน = 40(2) → ตารางประเภทบรรทัด (ไทยเหมือนเดิม) · ค่าโฆษณา/ค่าขนส่งยังเป็น 40(8) (ข้อ 41 ครอบแค่ค่าคอม/ค่าธรรมเนียม) ⇒ บล็อกพร้อมทางไปต่อ "ตั้งที่ช่องทางครั้งเดียว"
- **ขอบเขตผู้รับในคำเตือน** (`ForeignWhtPayeeCheck.ScopeOf`): มีเลขนิติบุคคลไทยที่ checksum ถูก ⇒ ไม่รู้ (อาจมีสาขา/สถานประกอบการถาวร หรือแค่จด e-Service) · ตรวจชนิดด้วย
  `WhtPayeeKind.Detect` ตัวเดียว — นิติบุคคล ⇒ ม.70 · บุคคลธรรมดา ⇒ เงียบ · ไม่มีสัญญาณ ⇒ ไม่รู้ · "ไม่รู้" = ข้อความ "ระบบตัดสินไม่ได้…" (ไม่ใช่ "หักขาด")

## 3. ตอบ F3 ข้อ 7–12

7. `callers.py`: `SettlementLineTypeRule.WhtIncomeCode` ในเส้นภาษีรอบโอนเหลือ 0 จุด (ComputeTax/PlanIssues ผ่านตัวตัดสินใหม่ · อื่นเป็นตารางต้นทาง + `Options()`) ·
   `pp36 = R(deducted * VatRate / 100m)` 0 จุด (forbid ใน checker) · ตัดสินแบบ WHT จากช่องทางใน `BuildGateAsync` 0 จุด · `ForeignWhtRateResolver.RateWarning` ใน
   `CollectApprovalWarningsAsync` 0 จุด (ผ่าน `ForeignWhtPayeeCheck.Warning`) · `GetTaxFormName` สำเนาป้ายแบบ 0 จุด
8. ทางเข้าอื่น: รอบโอน (แผน + ผู้ลงบัญชี + พรีวิว = ตัวเดียว) · เอกสารคีย์มือ/OCR (ไม่มีโหมดออกภาษีแทนในเอกสาร ⇒ ตรวจตอนออก 50 ทวิ "ออกให้ตลอดไป" + รายงาน ภ.พ.36) ·
   ออก 50 ทวิ ด้วยมือ (คำเตือน) · `Pnd54RateNote` ในไฟล์ส่งออก**ยังใช้** `RateWarning` ตรง (ไม่ผ่านขอบเขตผู้รับ — ใบในไฟล์ 54 เป็นผู้รับต่างประเทศทั้งหมด แต่อาจมีบุคคลธรรมดา ⇒ ดูคำถามค้าง Q-WF3)
9. เข้มขึ้น: `WhtIncomeTypeMapInvalid` (ค่าตั้งเสีย ⇒ บล็อก · ทางไปต่อ = บันทึกค่าตั้งใหม่ · ทิศตรงข้าม `W4_ค่าตั้งที่เก็บไว้อ่านไม่ได้_บล็อกทุกช่องทางที่หัก_ไม่หักไม่แตะ`) ·
   ตีกลับตอนบันทึกช่องทางเมื่อค่าเสีย (`SETTLEMENT-CHANNEL-INCOMETYPE`) · หลวมขึ้น: ต่างประเทศ PaymentFee/ServiceFee/WithdrawalFee ไม่บล็อกแล้ว (หัก 15%)
10. ค่าที่ persist: รอบโอนที่ **ลงบัญชีแล้ว** ด้วย ภ.พ.36 31.50 (โหมดออกภาษีแทน + ต่างประเทศ) ไม่ถูกคิดใหม่ — ทางนี้เพิ่งเปิดในรอบ 200 (ทีม W) และยังไม่ถึงผู้ใช้จริง ⇒
    ไม่ทำ migration · ถ้ามี ให้ยกเลิกการลงบัญชีแล้วลงใหม่ (รายงาน ภ.พ.36 แสดงฐานใหม่ทันที แต่ VAT ใบเดิมยังเป็นค่าเก่า) · คอลัมน์ใหม่ `WhtIncomeTypeMapJson` = NULL ทุกแถว (ค่าตั้งต้น)
11. ฝ่ายค้าน: ยังไม่ได้ส่ง diff ให้ subagent ฝ่ายค้าน (ทีมนี้คือผู้แก้ผลฝ่ายค้าน) — main agent ควรส่งรอบถัดไป
12. DOCUMENT_FLOW §2.10 ข้อ 4/8 + §5.4 · TEST_PLAN §0 + WF-01…06 · CHANGELOG · CLAUDE.md ตาราง E (ภ.พ.36 ฐาน · 40(2) ต่างประเทศ) · ACCOUNT_STRUCTURE ไม่เกี่ยว

## 4. เครื่องมือที่รัน

`required_call_site_check` (ผ่านทั้งกติกา + self-test · +10 แถว · ปรับ 2 แถว: `BuildGateAsync` · `CollectApprovalWarningsAsync`) · record_arg · nullable_arg · using ·
gl_code · filing_deadline_single_source · html_attr_escape · enum_number_compare · string_quote_close · comment_line_break · dead_helper (`ForeignDefault` → private) ·
settings_reader · accessibility · tuple_name_merge · undeclared_local · js_dup_method · onclick_js_string · identifier_space · namespace_shadow · arg_type ·
dto_nullable_contract · write_permission_gate · escape_helper · css_var · deep_link_param · test_inventory --check — **0 ปัญหา** · `node --check` ทุก `<script>` ใน
`wht.html`/`documents.html`/`settlement-channels.html` ผ่าน · awk brace ทุก `.cs` ที่แตะสมดุล (ไฟล์เทสต์ใหม่ต่าง 1 เพราะสตริง `"{oops"` ในเทสต์ JSON เสีย — ไม่ใช่โค้ด) ·
ไม่ได้รัน `check_all.sh` ทั้งชุด (เครื่องโหลดหนัก — ตามคำสั่ง)

## 5. ความเสี่ยงคอมไพล์ (ไม่มี .NET SDK)

- `SettlementChannelView`/`WithholdingTaxCertResponse`/`SettlementReferenceData` เพิ่มพารามิเตอร์ท้ายแบบมีค่าเริ่มต้น (positional record) — ผู้สร้างเดิมไม่ต้องแก้ ·
  `created with { Warnings = await … }` (await ใน initializer ของ `with` ในเมธอด async)
- `ForeignWhtPayeeCheck.CertificateWarnings` รับ `IReadOnlyList<(string? IncomeTypeCode, decimal TaxRate, DateTime PaymentDate)>` — ผู้เรียกส่ง
  `List<(string?, decimal, DateTime)>` (ชื่อ tuple ไม่มีผลกับ identity conversion)
- `switch` statement ที่ประกาศ `var` ใน `default:` · pattern `is not decimal expected || …` แล้วใช้ `expected` หลัง `return`
- `SettlementWhtIncomeType.ParseMap`: `out var type` ในนิพจน์ของ local declaration แล้ว parse ซ้ำเป็น `t` (ไม่พึ่ง definite assignment ข้ามบรรทัด)
- `ContactType.Unknown` ใน `DocumentService`/`WithholdingTaxCertService` (enum ตัวเดียวใน `Models.Enums`)
- endpoint ใหม่ `GET /api/reference/foreign-wht` `[AllowAnonymous]` แบบเดียวกับ `income-types` (ข้อมูลกฎหมายสาธารณะ ไม่มีข้อมูลบริษัท)

## 6. คำถามค้าง (เลือกทิศที่มองเห็นและย้อนได้)

| ID | คำถาม | ทิศที่ใช้ |
|---|---|---|
| Q-WF1 | ค่าบริการแพลตฟอร์ม (`ServiceFee`) ของต่างประเทศนับเป็น "ค่าธรรมเนียม" ตามข้อ 41 ไหม | นับเป็น 40(2) (หักไว้ก่อน — ทิศมองเห็น) · ผู้ทำบัญชีเปลี่ยนที่ช่องทางได้ |
| Q-WF2 | รายงาน ภ.พ.36 ของใบที่ผูก 50 ทวิ "ออกให้ตลอดไป" แต่ VAT ของใบยังคิดบนฐานเดิม (ใบคีย์มือก่อนรอบนี้) | ฐานในรายงานแสดงค่าใหม่ (529.41) · VAT = ของใบ ⇒ อัตราในแถวต่ำกว่า 7% เห็นได้ + คำเตือนตอนออก 50 ทวิ · ไม่แก้ VAT ของใบให้เอง (เอกสารอนุมัติแล้ว) |
| Q-WF3 | `Pnd54RateNote` (ไฟล์ส่งออก) ควรผ่านขอบเขตผู้รับ `ForeignWhtPayeeCheck` ด้วยไหม (บุคคลธรรมดาต่างประเทศในไฟล์ 54) | คงเดิม (หมายเหตุท้ายไฟล์ ไม่บล็อก) — ต้องยืนยันก่อนว่าบุคคลธรรมดาต่างประเทศควรอยู่ในไฟล์ ภ.ง.ด.54 หรือไม่ |
| Q-WF4 | ช่องสถานประกอบการถาวรในไทยบนผู้ติดต่อ | ยังไม่เพิ่ม — ใช้ "มีเลขนิติบุคคลไทย" เป็นสัญญาณ "ไม่รู้" (ไม่ประกาศหักขาด) · เพิ่มช่องพร้อม CoR (W-8) ได้ |
