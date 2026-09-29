# ฝ่ายค้านรอบสอง · เงิน/ภาษี/settlement — ตรวจงานแก้ของทีม SF · WF · IF · GF (รอบ 200)

> อ่านอย่างเดียว · ทุก file:line อ้าง **`8a85213b`** (`git show 8a85213b:<path>`) · ไม่มี .NET SDK ⇒ คอมไพล์ด้วย checker + อ่านโค้ด
> ขอบเขต diff: SF `b350edff..e20c18e7` · WF `2f0b0bfd..e839a680` (คอมมิตของทีมเอง `939bbdfc`) · IF `c26138d8..6874a906` · GF `f1810dd6..b296f0a4` ·
> merge `65d7f60d` (GF) · `3b9c6b02` (SF) · `48fe0cc8` (IF) · `ed3bb347` (WF)

## สรุป

| ID | ระดับ | ชนิด | เรื่อง |
|---|---|---|---|
| R2M-1 | **P1** (CI แดง) | CONFIRMED | `required_call_site_check` ล้มที่ HEAD หลังรวม SF X-4 + WF W-3 (กติกา literal `borne = wht`) — ยังค้างที่ `36a0aad5` |
| R2M-2 | **P2** | CONFIRMED | บริษัท**ไม่จด VAT**: `ModeMismatch` ปล่อยคู่โหมด VAT ไทยทุกคู่ แต่ฐาน WHT ของสองเส้นต่างกันเมื่อหัก ณ ที่จ่าย (ขัดข้อ 26) |
| R2M-3 | **P2** | CONFIRMED (เส้นโค้ด) | ฐาน ภ.พ.36 บวกภาษีออกแทนซ้ำ + คำเตือน "VAT ขาด" ที่ผิด เมื่อเอกสารคีย์มือ gross-up ไว้แล้วแล้ว 50 ทวิ ถูกตั้งเป็น "ออกให้ตลอดไป" |
| R2M-4 | P3 | CONFIRMED | SF X-4 (WHT รวมระดับบรรทัดใบ) × WF W-3 (ภ.พ.36 หลัง WHT): ภ.พ.36 ยังคิดรายส่วนด้วยภาษีออกแทนรายส่วน ⇒ ภ.พ.36 ขาดเป็นสตางค์ และไม่ตรงฐานในรายงาน |
| R2M-5 | P3 | CONFIRMED | ค่าตั้งประเภทเงินได้ต่อช่องทาง (WF) ไม่อยู่ใน `ModeMismatch` (SF) ⇒ ช่องทางที่ผูก gateway ให้ WHT ต่างจากเส้นเดิมได้ |
| R2M-6 | P3 | CONFIRMED (ช่องว่างระหว่างทีม) | X-8 (`PeriodTo` ว่าง = ไม่มีขอบบน) SF ยกให้ GF แต่ GF ไม่ได้ทำ |
| R2M-7 | P3 | CONFIRMED | ทางไปต่อสองข้อขัดกันบนช่องทางต่างประเทศ ภ.พ.36 ที่ผูก gateway (W-6 บอกให้แก้โหมดให้ตรง · X-3 บอกว่าตรงไม่ได้เลย) |
| R2M-8 | P3 | PLAUSIBLE | ช่องทางต่างประเทศ ภ.พ.36 ที่ผูก gateway ซึ่งเดิมคิดถูก ถูกบล็อกตอนลงบัญชี (รวมรอบที่นำเข้าจากไฟล์) · ทางออกต้องสร้างช่องทางใหม่ แต่ข้อความไม่บอก |
| R2M-9 | P3 | PLAUSIBLE | คีย์กันซ้ำตาม "วันที่ตามตัวอักษร" (`Assign(literal)`) จับแถวจริงของไฟล์ใหม่ว่าซ้ำได้ในเคสแคบ |
| R2M-10 | P3 | PLAUSIBLE | รายงาน ภ.พ.36 นับ 50 ทวิ "ออกให้ตลอดไป" ที่ยังเป็นร่าง (Draft) |
| R2M-11 | P3 | PLAUSIBLE | POS: คืนเงินบัตรหลัง deploy ของบิลก่อน deploy ⇒ ขายลงธนาคารที่ปัก แต่คืนลง 11340 |
| R2M-12 | P3 | PLAUSIBLE | T-2: ใบสรุปเพิ่มเติมที่เป็นรายการจริงหน้าตาเหมือนรอบแรกทุกบรรทัด (R-B5) ⇒ บล็อก ทางไปต่อมีแค่ "ยกเลิกรอบ" |
| R2M-13 | P3 | PLAUSIBLE (ทีมรู้แล้ว) | T-1: บล็อกงวดกลางของใบที่ WHT ถูกหักครบจากงวดก่อนแล้ว |

**ตัวเลขเข้าบัญชีผิดทางใหม่ที่ยืนยันได้**: R2M-2 (WHT/50 ทวิ ต่างกันตามหน้าที่กด) · R2M-3 (รายงาน ภ.พ.36 ฐานเกิน + คำแนะนำที่ทำให้ยื่นเกิน) · R2M-4 (สตางค์)
**การรวมโค้ด**: ไม่มีโค้ดของทีมใดหายหรือซ้ำ (ตรวจด้วยสคริปต์ทีละบรรทัดที่ทีมเพิ่ม) · enum 37→60 ปลอดภัย · ผู้เรียกลายเซ็นที่เปลี่ยนครบทุกจุด — แต่ **checker ล้ม 1 กติกา** (R2M-1)

---

## CONFIRMED

### R2M-1 (P1 · CI) — `required_call_site_check` ล้มที่ `8a85213b` เพราะ merge SF+WF

- กติกาทีม WF: `tools/required_call_site_check.py:2529` `before=[("borne = wht", "ForeignServiceVat.Pp36Base(preVat, borne)")]`
- โค้ดหลังรวม: `Helpers/SettlementFeeTax.cs:131` `(wht, certIncome, borne) = WhtOnBase(preVat, rate, whtMode);` (SF X-4 ย้ายสูตรไป `WhtOnBase`) — ไม่มี literal `borne = wht` แล้ว
- รันบน `git archive 8a85213b` สะอาด: `❌ Helpers/SettlementFeeTax.cs:89 Compute ไม่เรียก 'borne = wht'` · **exit 1** ⇒ `check_all.sh` / CI แดง
- ความหมายยังถูก (borne มาจาก `WhtOnBase` ก่อน `Pp36Base` บรรทัด 138) — ผิดที่กติกา · **ยังไม่ถูกแก้ที่ `36a0aad5`** (กติกาเดิมที่บรรทัด 2601)
- แก้: `before=[("WhtOnBase(preVat, rate, whtMode)", "ForeignServiceVat.Pp36Base(preVat, borne)")]` (+ ใส่ใน `must`) · ข้อสังเกตกระบวนการ: คอมมิต "TEST_PLAN §0 หลัง merge WF" ไม่ได้รัน checker ชุดเต็มหลังรวม

### R2M-2 (P2) — บริษัทไม่จด VAT: คู่โหมดที่ `ModeMismatch` ปล่อย ให้ WHT ต่างกันสองเส้น

- `Helpers/GatewayBatchIntentRules.cs:75` `if (!companyVatRegistered) return null;` — SF X-10 ให้เหตุผลว่า "VAT เป็นค่าใช้จ่ายทั้งก้อนทั้งสองเส้น" ซึ่งจริงเฉพาะ**ขาค่าใช้จ่าย** แต่**ฐาน WHT ไม่เท่า**:
  - เส้นเดิม `GatewaySettlementMath` (config "ไม่แยก VAT"): `feeBeforeVat = FeeDeducted` เต็ม ⇒ `WhtOnFee(feeBeforeVat)`
  - เส้นรอบโอน (ช่องทาง "VAT ไทย 7%"): `SettlementFeeTax.Compute` แยก `preVat = R(ยอด×100/107)` ⇒ WHT บนฐานก่อน VAT (`SettlementFeeTax.cs:131`)
- ฉาก: บริษัทไม่จด VAT · config gateway "ไม่แยก VAT + หัก 3%" · ช่องทาง "VAT ไทย 7% + เราออกภาษีแทน" · ค่าธรรมเนียม 107.00 ⇒ เส้นเดิม WHT **3.31** (107×3/97) · เส้นรอบโอน **3.09** (100×3/97) — 50 ทวิ/ภ.ง.ด.53/ค่าใช้จ่ายภาษีที่ออกแทนต่างกันตามหน้าที่กด = ขัดคำตัดสินข้อ 26 ตรงตัว · กลับทิศก็เกิด (config "รวมใน/บวกเพิ่ม" ↔ ช่องทาง "ไม่มี VAT": เส้นรอบโอนหักบนยอดรวม VAT)
- ไม่มีเทสต์ทิศนี้ (Theory X3 แถวไม่จด VAT ไม่มี WHT) · แก้: ผ่อนคู่ VAT ไทยของบริษัทไม่จด VAT **เฉพาะเมื่อไม่มีขา WHT** (หรือ `WhtProblem` ต้องดูว่าฐานก่อน VAT ของสองเส้นเท่ากัน)

### R2M-3 (P2) — ฐาน ภ.พ.36 บวกภาษีออกแทนซ้ำเมื่อเอกสารคีย์มือ gross-up แล้ว

- รายงาน: `Services/Implementations/TaxService.cs:1597-1618` บวก `TotalTaxAmount` ของ 50 ทวิ `PayAlways` ที่ผูกเอกสาร เข้าฐาน**ทุกเอกสาร**บริการต่างประเทศ ไม่ดูว่าบรรทัดของเอกสารรวมภาษีที่ออกแทนไว้แล้วหรือยัง
- คำเตือนตอนออก/แก้ 50 ทวิ: `WithholdingTaxCertService.cs:205-216` `Pp36Shortfall(serviceValue, cert.TotalTaxAmount, doc.VatAmount)` — ตรรกะเดียวกัน
- ทางเข้า: 50 ทวิ อัตโนมัติตอนจ่ายเกิดเป็น `Withhold` ผูก `DocumentId` (`WithholdingTaxCertService.cs:546`) · ผู้ใช้แก้เป็น "ออกให้ตลอดไป" ได้ (`:253` · `wht.html` `fCertType`) หรือออกใบมือแบบ PayAlways ผูกเอกสาร
- ฉาก (วิธีคีย์ gross-up ที่ถูกต้องของนักบัญชี): ใบซื้อบริการต่างประเทศ บรรทัด 529.41 · หัก 15% = 79.41 บนเอกสาร (จ่าย 450) · VAT ภ.พ.36 = 37.06 (ถูกแล้ว) · 50 ทวิ ตั้ง "ออกให้ตลอดไป" ⇒
  รายงาน ภ.พ.36 ฐาน **608.82** (529.41 + 79.41) กับภาษี 37.06 (อัตราแสดง 6.09%) · คำเตือนบอก **"VAT ขาด 5.56 — แก้ VAT ของเอกสารก่อนนำส่ง"** ⇒ ผู้ใช้ทำตาม = ยื่น ภ.พ.36 เกิน + ภาษีซื้อ 11640 เกิน
- ใบค่าธรรมเนียมจากรอบโอนไม่โดน (บรรทัด = ฐานก่อนภาษีออกแทน · ใบไม่มี WHT บนตัว — `SettlementPosting.cs` `FeeDocument`) · แก้: บวกภาษีออกแทนเฉพาะเมื่อฐานของเอกสาร**ยังไม่รวม** (สัญญาณขั้นต่ำ: `doc.WithholdingTaxAmount == 0` หรือเฉพาะใบที่รอบโอนสร้าง) + เทสต์ทิศตรงข้าม "เอกสาร gross-up แล้วไม่ถูกบวกซ้ำ"

### R2M-4 (P3) — X-4 × W-3: ภ.พ.36 คิดรายส่วน ขณะที่ WHT/50 ทวิ คิดจากฐานรวม

- `Helpers/SettlementBatchMath.cs:613` (SF X-4) คิด `whtAmount/whtBorne` ใหม่จากฐานรวมเมื่อ `parts.Count > 1` แต่ `:622` `Pp36Payable = Σ ส่วน` ซึ่งแต่ละส่วนคิด `Pp36Base(preVat_i, borne_i)` ด้วย borne ที่ปัดรายส่วน (`SettlementFeeTax.cs:131-138`) — ลำดับ "WHT → ภ.พ.36" ของข้อ 40 ไม่ได้ใช้ WHT ตัวที่ลง 50 ทวิ
- ฉาก: ช่องทางต่างประเทศ ภ.พ.36 · ออกภาษีแทน 40(2) · ไฟล์มีคอลัมน์ VAT = 0 (ทุกบรรทัดเป็นส่วนของตัวเอง) · 100 × 3.65 ⇒ 50 ทวิ ภาษี 64.41 (ฐานรวม) · ภ.พ.36 บนใบ **30.00** (Σ R((3.65+0.64)×7%)) ขณะที่ที่ถูก = R((365+64.41)×7%) = **30.06** · รายงาน ภ.พ.36 แสดงฐาน 429.41 ภาษี 30.00 (6.99%) · นำส่งขาด 0.06
- ไม่ขัดสมดุล JE (ภาษีซื้อ = หนี้ ภ.พ.36 ทั้งคู่ Σ ส่วน) · แก้: หลังคิด `whtBorne` ของบรรทัดใบ ให้คิด `Pp36Payable` (และ InputVat/Expense ของ NotClaimable) ใหม่จาก `Pp36Base(Σ preVat, whtBorne)` แบบเดียวกับ WHT

### R2M-5 (P3) — ค่าตั้งประเภทเงินได้ของช่องทาง (WF ข้อ 41) อยู่นอกตัวตัดสิน "สองเส้นเท่ากัน" (SF ข้อ 26)

- `SettlementBatchMath.cs:638` ใช้ `SettlementWhtIncomeType.For(rule.Type, channel)` (ค่าตั้ง `WhtIncomeTypeMapJson` ชนะ) · `GatewayBatchIntentRules.ModeMismatch` (`:57`) ดูแค่โหมด VAT/WHT
- ฉาก: ช่องทางผูก gateway (config "หัก 3%" ↔ ช่องทาง "เราออกภาษีแทน" = ตรงกัน) · ผู้ทำบัญชีตั้ง `PaymentFee` = `none` / `8ad` (2%) / `8tr` (1%) ที่หน้าช่องทาง ⇒ รอบโอน settlement หัก 0/2/1% · เส้นเดิมหัก 3% คงที่ (`GatewaySettlementMath.cs:329`) — ด่านทุกจุด (นำเข้า · บันทึกช่องทาง · บันทึกค่าตั้ง gateway · ลงบัญชี) ไม่ฟ้อง
- แก้: ช่องทางที่ผูก config ⇒ รหัสของ `PaymentFee` ต้องได้อัตรา = อัตราของ config (หรือห้ามตั้งค่า map บนช่องทางที่ผูก gateway) + เพิ่มใน `ModeMismatch`

### R2M-6 (P3) — X-8 หล่นระหว่างทีม

- `review200-P2.md` ตารางสถานะ: X-8 "📋 … แก้พร้อม X-2 (ทีม GF)" · `team-GF.md` ไม่มี X-8 · โค้ด `Services/Settlement/SettlementImportService.Gateway.cs:96` `var to = periodTo is DateTime pt ? … : (DateTime?)null;` ⇒ ยังไม่มีขอบบนเมื่อไม่กรอก `PeriodTo` ⇒ intent ที่รับเงิน**หลัง**วันเงินเข้าถูกประทับ `SettlementBatchId` ของรอบนี้ (สมการไม่ลงตัว — มองเห็น แต่ intent ติดเจ้าของผิดจนลบบรรทัด)
- แก้: ไม่มี `PeriodTo` ⇒ ใช้ `ConfirmedToExclusiveUtc(วันเงินเข้า − 1)` (ขอบเดียวกับจุดตัดยอดคืน) หรือบังคับกรอก · ติดสถานะในตาราง P2 ให้ชี้เจ้าของใหม่

### R2M-7 (P3) — ทางไปต่อขัดกันหลังรวม W-6 กับ X-3

- `Helpers/SettlementForeignWht.cs:89` `GatewayConfigHint` (WF W-6) ต่อท้าย `ForeignWhtNotSupported` ว่า "แก้ 'หัก ณ ที่จ่ายค่าธรรมเนียม' ที่หน้า gateway ให้ตรงกัน (ระบบตรวจว่าสองที่ตอบตรงกัน)" · แต่ SF X-3 ทำให้ช่องทาง `ForeignPp36` ที่ผูก config **ไม่ตรงเสมอ** (`GatewayBatchIntentRules.cs:72-74`)
- ช่องทางต่างประเทศที่ผูก gateway ได้สองข้อความบนพรีวิวเดียวกัน: ข้อหนึ่งให้แก้โหมดสองที่ อีกข้อบอกว่าไม่มีโหมดที่ตรง — ทำตาม W-6 แล้วยังถูกบล็อก · แก้: `GatewayConfigHint` ของช่องทาง ภ.พ.36 ให้ชี้ทางเดียวกับ X-3 (ช่องทางไม่ผูก config)

---

## PLAUSIBLE

- **R2M-8 (P3)** — `SettlementPostingService.cs:604-614` ด่าน `GatewayModeMismatch` ใช้กับ**ทุก**รอบของช่องทางที่ผูก config (รวมรอบที่นำเข้าจากไฟล์) · ช่องทาง ภ.พ.36 ที่ผูก config ซึ่งเส้นรอบโอนคิด §83/6 ถูกอยู่แล้ว ถูกบล็อกตามข้อ 26 (ตั้งใจ) แต่ช่องทางที่มีรอบโอนแล้ว**ถอดการผูกไม่ได้** (`SettlementChannelService.cs:183` `Locked("การตั้งค่า gateway ที่ผูก")`) ⇒ ทางเดียวคือสร้างช่องทางใหม่ (ชนิด Gateway ไม่ผูก config · ผังพักเดียวกับ gateway เพื่อผ่าน R-A1) + ยกเลิกรอบเดิม — ข้อความ X-3 บอกแค่ "ใช้ช่องทางที่ไม่ผูก" · ทางที่ง่ายกว่าสำหรับผู้ใช้คือหน้ารอบโอน gateway เดิม ซึ่ง**ไม่ตั้ง ภ.พ.36** (ภาษีผิดทางเดิม) ⇒ ควรบอกให้ชัดว่าห้ามใช้เส้นเดิมกับผู้ให้บริการต่างประเทศ
- **R2M-9 (P3)** — `Helpers/SettlementTxnKey.cs:94-96` เพิ่มคีย์รุ่นก่อนที่คิดด้วยวันที่ตามตัวอักษร รวม `Assign(literal)` (กติกาปัจจุบัน) ⇒ แถวใหม่ที่ถูกต้องชนกับบรรทัดจริงอีกรายการได้เมื่อ **id + ป้าย + ยอด เท่ากัน** และวันที่ของอีกรายการ = วันที่ตามตัวอักษรของแถวนี้ (เช่น คืนเงินบางส่วนยอดเท่ากันสองครั้งของออเดอร์เดียว ครั้งแรก 09:00 ไทยวันที่ 12 · ครั้งสอง 18:00 UTC วันที่ 12 = ไทยวันที่ 13) หรือไฟล์ที่ผู้ใช้เลือกเดือน/วัน (MDY) แล้วตัวแปร DMY ชน ⇒ แถวถูกข้ามที่ `SettlementImportService.cs:215` (มีคำเตือน "ข้าม…แถวที่นำเข้าแล้ว" รายแถว — ไม่เงียบ) · ไม่มีเทสต์ทิศนี้
- **R2M-10 (P3)** — `TaxService.cs:1601-1602` กรองแค่ `Status != Voided` ⇒ 50 ทวิ "ออกให้ตลอดไป" ที่เป็นร่างถูกบวกเข้าฐาน ภ.พ.36 (เส้นรอบโอนออกเป็น Issued ทันที — เสี่ยงแค่ใบมือ)
- **R2M-11 (P3)** — GF R200G-2 ถูกต้องสำหรับบิลใหม่ · บิลบัตร/e-Wallet/เช็คที่ปิด**ก่อน** deploy (Dr ธนาคารที่ปัก) แล้วคืนเงิน**หลัง** deploy ⇒ `ResolvePaymentAccountAsync` ให้ Cr 11340/11113/11131 ⇒ ธนาคารเกิน + ผังพักติดลบในช่วงเปลี่ยนผ่าน · SQL ใน `team-GF.md` หาเฉพาะขาขาย — ควรเพิ่มคืนเงินที่เกิดหลัง deploy ของบิลก่อน deploy
- **R2M-12 (P3)** — `Helpers/SettlementPostingChecks.cs:139-160` `SplitDuplicates`: ทุกบรรทัดของใบสรุปเพิ่มเติมเนื้อหาตรงรอบที่ออกใบแรก ⇒ `SummarySaleDuplicate` (บล็อก) · ร้านเล็กที่มีรายการไม่มีเลขหน้าตาเหมือนกันจริงในสอง payout วันเดียวกัน (R-B5 — ที่ `SettlementContentOverlap` เองเลือก "ไม่บล็อก") ไม่มีทางไปต่อนอกจากยกเลิกรอบ
- **R2M-13 (P3 · ทีม SF ระบุเองในคำถามค้าง 3)** — `SettlementPosting.cs:270-273` `Decide` ไม่รู้ว่า WHT ของใบถูกหักครบจากงวดก่อนแล้ว ⇒ งวดกลางถูกบล็อก ทั้งที่ `CreatePaymentAsync` ให้ 0 ได้ถูก (ทางไปต่อมี)

---

## NOT-A-BUG (ตรวจแล้ว — ห้ามรายงานซ้ำ)

1. **enum 37 → 60** (`SettlementBatchMath.cs:78,81`): ไม่มีการ persist เป็นตัวเลข (enum ออก JSON เป็นชื่อ `Program.cs:826` `JsonStringEnumConverter` · ไม่มีคอลัมน์/`(int)` cast/ตัวเลข 37 ใน JS หรือเทสต์) · การบล็อกตัดสินจาก `Blocking` ที่ผู้สร้างใส่เอง (`SettlementWhtIncomeType.cs:125` `true`) ไม่ใช่ช่วงเลข ⇒ 60 อยู่ในช่วง "แจ้งให้ทราบ 50+" เป็นแค่ความไม่เป็นระเบียบของคอมเมนต์ · ผู้ใช้ทั้งสองชื่อ (`GatewayBatchIntentRules.cs:110` · `SettlementWhtIncomeType.cs:125` · เทสต์ 3 จุด) อ้างด้วยชื่อ
2. **โค้ดหาย/ซ้ำหลังรวม**: สคริปต์เทียบทุกบรรทัดที่แต่ละทีมเพิ่ม (`git diff -U0`) กับไฟล์ที่ HEAD — ไม่มีบรรทัดหาย ยกเว้น `WhtIncomeTypeMapInvalid = 37` ที่ตั้งใจเปลี่ยน · `remerge-diff` ของ 4 merge: conflict โค้ดมีไฟล์เดียว (`SettlementBatchMath.cs`) · `BuildGateAsync` มี whtForm (WF) · ModeMismatch (SF) · ContentOverlap (I/IF) · receipt WHT (SF) ครบ ลำดับถูก ไม่ซ้ำ · `SettlementPostingGuards` (V1+SF) · `SettlementImportService` (SF X-1/X-5 + IF) · `SettlementChannelService` (SF touched + WF map) รวมครบ
3. **ลายเซ็นที่เปลี่ยน — ผู้เรียกครบทุกจุด (รวมเทสต์)**: `ModeMismatch` 5 อาร์กิวเมนต์ (โค้ด 4 + `ConfigChangeRefusal` + เทสต์ 6) · `ReceiptPayment(…, SettlementReceiptWhtKind)` (โค้ด 1 + เทสต์ 4) · `AcknowledgeOrphanAsync(…, Guid? checkedBatchId, ct)` (interface/impl/controller) · `ImportFileAsync(…, memoryBlockedReason, ct)` (interface/impl/controller — `OpenBankingService.ImportFileAsync` คนละเมธอด) · `IsSummaryRow` 7 อาร์กิวเมนต์ (adapter 1 + เทสต์ 10) · `SettlementFeeTax.Compute` ลายเซ็นไม่เปลี่ยน · positional record: `SettlementPostingFacts` (24 positional + named ตรงลำดับ) · `SettlementReceiptTarget` (`DocumentWht` ท้าย default) · `SettlementChannelView`/`SettlementReferenceData`/`SettlementParsedRow`/`SettlementParseContext` (พารามิเตอร์ท้ายมี default) · checker บน archive สะอาด: record_arg · using · nullable_arg · undeclared_local · arg_type · tuple_name_merge · accessibility · service_interface · dead_helper · test_inventory — **ผ่านทั้งหมด** (ล้มตัวเดียวคือ R2M-1)
4. **สูตร ภ.พ.36 ของรอบโอน (ข้อ 40)** ตรวจมือ: 450 × 15/85 = 79.4117 → **79.41** · ฐาน 529.41 × 7% = 37.0587 → **37.06** · 50 ทวิ เงินได้ 529.41 ภาษี 79.41 (= 15% ของ 529.41) · JE: ใบค่าธรรมเนียม Dr ค่าธรรมเนียม 450 + Dr 11640 37.06 = Cr ผังพัก 450 + Cr 21912 37.06 · JE รอบโอน Dr ค่าธรรมเนียม (ภาษีออกแทน) 79.41 / Cr 21918 79.41 — สมดุล · รายงาน ภ.พ.36 ของใบจากรอบโอน: ฐาน = บรรทัด 450 (ไม่รวมภาษีออกแทน) + 50 ทวิ 79.41 = 529.41 · ภาษี 37.06 — **ไม่นับซ้ำ** (ใบไม่มี WHT บนตัว · 50 ทวิ ใบเดียวต่อใบค่าธรรมเนียม idempotent `SettlementPostingService.cs:219-235`) · W2/ไม่หัก/นอก ม.70 ⇒ ฐานเดิม 31.50
5. **T-1 (ข้อ 27)**: `BalanceDue` สุทธิหลัง WHT (`DocumentService.cs:1591` Total = SubTotal + VAT − WHT) · เกณฑ์งวดสุดท้ายเดียวกับ `CreatePaymentAsync` (+0.01) · `null` เฉพาะงวดสุดท้าย ⇒ `remainingCap` · JE ตามเกณฑ์ Cash/Accrual ของ `CreatePaymentJournalAsync` เดิม · `EnsureReceiptAsync` ตัดสินจากข้อมูลสด — ไม่มีทางส่ง 0 เงียบอีก
6. **40(2) ตั้งต้นต่างประเทศ**: ทำงานเฉพาะ `FeeVatMode == ForeignPp36` (`SettlementWhtIncomeType.Resolve`) — ช่องทางไทยได้ตารางเดิมทุกประเภท · ก่อนแก้ประเภทเหล่านี้ถูกบล็อก จึงไม่มีรอบที่ลงไปแล้วด้วยตัวเลขเก่า
7. **ModeMismatch คู่ที่บล็อกเพิ่ม** (gateway ไม่หัก ↔ ช่องทาง W2 · ช่องทาง ภ.พ.36) = ตามข้อ 26 · คู่จด VAT (รวมใน/บวกเพิ่ม ↔ 7% · ไม่แยก ↔ ไม่มี VAT) ให้ผลเท่าเส้นเดิม รวม WHT ฐานรวม (X-4 ทำให้ตรง `GatewaySettlementMath.cs:329`) — ไม่บล็อกลูกค้าที่จด VAT และตั้งถูก
8. **POS บัญชีที่ปัก (GF)**: `MoneyAccountFallback.TerminalPinFor` — ธนาคารที่ปักใช้เฉพาะโอน/พร้อมเพย์/หักบัญชี · ผู้เรียกเดียว `PosService.Orders.cs:2229` · ไม่มีสำเนาอื่นในเรพ
9. **`ChangeTracker.Clear()` ใน `UnpostCoreAsync`/`AcknowledgeOrphanCoreAsync` (SF V2-C3)**: `LoadAsync` เป็น AsNoTracking ทั้งหมด · ขั้นยกเลิกเอกสาร/การรับชำระ/ถอนจับคู่ SaveChanges ของตัวเองก่อนถึง Clear — ไม่มีการเปลี่ยนแปลงค้างที่ถูกทิ้ง
10. **คีย์กันซ้ำ IF กับเส้น PaymentIntent**: แถว intent ไม่มี `LiteralDates` ⇒ `LegacyKeys` เท่าเดิม · กรอง SQL `StartsWith(v2:row | row:)` ชุดเดียวกับ `IsRowKey`
11. **ข้อ 39 (IF) แถวไม่มีเลขที่พิสูจน์ไม่ได้ว่าเป็นแถวสรุปถูกนำเข้า**: ถ้าจริง ๆ เป็นแถวสรุป สมการรอบโอน (Σ บรรทัด = ยอดโอน + ส่วนต่าง wallet) ไม่ลงตัว ⇒ `Unbalanced` บล็อก — ไม่เงียบ
