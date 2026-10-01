# ฝ่ายค้าน รอบ 200 — ทีม P2 (settlement เฟส 2: payment gateway เข้ารอบโอน)

> diff `5c1fe028..worktree-agent-a77ddfc5892bc280c` (4d3b7ea9 + 7f668940) · อ่านอย่างเดียว · ไม่มี .NET SDK (ยังไม่ได้คอมไพล์)
> คำถามหลัก: **ตัวเลขเข้าบัญชีผิดได้ไหม** · อ้างบรรทัดตามไฟล์บน branch ของทีม

## สรุป

| ID | ระดับ | สถานะ | เรื่อง |
|---|---|---|---|
| X-1 | **P1** | CONFIRMED | ด่านโหมดภาษี (ModeMismatch) อยู่แค่ "ประกอบรอบโอน" กับ "บันทึกช่องทาง" — **ไม่มีที่การลงบัญชี** ⇒ รอบโอนจาก intent ที่นำเข้าก่อน deploy (คู่ค่าเริ่มต้น config None + ช่องทาง ThaiVat7) ลงบัญชีแล้วได้ภาษีซื้อ 11630 ที่แต่งขึ้น (7/107 ของค่าธรรมเนียม) · §5 ของรายงานทีมบอกผิดว่า "ลงได้ตามเดิม (VAT อาจต่างเป็นสตางค์)" |
| X-2 | P2 | CONFIRMED | ขอบช่วงวันที่ของเส้นใหม่เลื่อน 7 ชั่วโมง (ป้ายวันไทย 00:00 UTC เทียบกับ `ConfirmedAt` ที่เป็นเวลาจริง) — บั๊กเดียวกับที่ทีม G รอบเดียวกันเพิ่งแก้ในเส้นเดิมด้วย `ConfirmedRangeUtc` ⇒ หลัง merge สองเส้นเลือก intent คนละชุดจากช่วงวันเดียวกัน |
| X-3 | P2 | CONFIRMED | "สูตรเดียว" ไม่จริงในคู่โหมดที่ด่านอนุญาต — config None ↔ ช่องทาง ForeignPp36 (เส้น batch ตั้งหนี้ ภ.พ.36 · เส้นเดิมไม่มี) และ config ไม่หัก ↔ ช่องทาง "หักแล้วได้คืน" W2 (เส้น batch ตั้ง WHT 21917 + ลูกหนี้ · เส้นเดิมไม่มี) — ทั้งสองเส้นยังเปิดคู่กัน ⇒ ภาษีของ intent ชุดเดียวกันขึ้นกับปุ่มที่ผู้ใช้กด |
| X-4 | P3 | CONFIRMED | ด่านบังคับให้ config "หัก 3%" ต้องคู่กับช่องทาง W3 แต่เส้น batch คิด WHT **รายบรรทัด** (บรรทัดมี VAT ระบุต่อรายการ) ⇒ ภ.ง.ด.53/50 ทวิ สูงกว่าคิดรวม (เส้นเดิมบล็อก WHT ทั้งหมด) — ทีมรู้ (B-6) แต่ทางเข้านี้ยังเปิด |
| X-5 | P3 | PLAUSIBLE | เติมรอบโอนเดิม (PayoutRef ซ้ำ) ใช้วันเงินเข้าจากหัวคำขอเป็นจุดตัดยอดคืน แต่รอบโอนใช้วันที่เดิม |
| X-6 | P3 | PLAUSIBLE | "หนึ่งเจ้าของ + ล็อกเดียวกัน" จริงเฉพาะทางเข้าประกอบจาก intent — การจับคู่ไฟล์/จับคู่มือ/rematch ประทับ `SettlementBatchId` โดยไม่ถือล็อก gateway (ตาข่ายที่การลงบัญชีกันเงินผิดไว้ได้ แต่รอบค้าง) |
| X-7 | P3 | PLAUSIBLE | ตาข่ายยอดคืนใต้ล็อก (`EnsureIntentRefundCapacityAsync`) รันเฉพาะเส้น intent — นำเข้าไฟล์ของช่องทาง gateway (ไม่มีล็อก gateway) แข่งกับเส้น intent ได้ |
| X-8 | P3 | PLAUSIBLE | `PeriodTo` ว่าง ⇒ ไม่มีขอบบน ⇒ ดึง intent ที่รับเงิน**หลัง**วันเงินเข้า (เส้นเดิมบังคับ ToDate) |
| ✅ <pending> รอบ 201 ทีม GW X-9 (A-GW5) | P3 | PLAUSIBLE | VAT ค่าธรรมเนียมของ intent ที่รอบโอน batch เป็นเจ้าของไม่อยู่ในหน้า "เคลม VAT ค่าธรรมเนียม gateway" (อ่านเฉพาะ JE เส้นเดิม) — ใบกำกับรายเดือนใบเดียวต้องแยกเคลมสองทาง |
| X-10 | P3 | PLAUSIBLE | ด่านโหมดตอนบันทึกช่องทางบล็อกการแก้ทุกช่อง (รวมปิดใช้งาน/เปลี่ยนชื่อ) ของช่องทางเดิมที่โหมดขัด · บริษัทไม่จด VAT ที่คู่ None↔ThaiVat7 ได้ตัวเลขเท่ากันก็ถูกบล็อก |

NOT-A-BUG (ตรวจแล้ว): สูตรค่าธรรมเนียม/VAT/เงินคืน 3 โหมด + ไม่จด VAT · การปัด AwayFromZero · `RefundCutoffUtc` ตรงกับทีม G · race เส้นเดิม↔เส้น intent · ยกเลิก batch · `LateRefundInBatch` · EF แปล expression · endpoint/tenant · คอมไพล์ (ดู §3)

---

## 1. CONFIRMED

### X-1 (P1) — รอบโอนที่ค้างอยู่ก่อน deploy ลงบัญชีได้ภาษีซื้อแต่งขึ้น · ด่านโหมดไม่อยู่ที่การลงบัญชี

- ด่าน `GatewayBatchIntentRules.ModeMismatch` ถูกเรียกแค่ 2 จุด: `SettlementImportService.Gateway.cs:45` (ประกอบรอบโอน) และ `SettlementChannelService.cs:143` (บันทึกช่องทาง)
  — grep ทั้งเรพ ไม่มีใน `SettlementPostingService.cs` (`:330` replan · `:460` Plan) และไม่มีใน `ImportFileAsync` (`SettlementImportService.cs:95-117`)
- `SettlementBatchMath.Plan` ยังแยก 7/107 จากบรรทัดค่าธรรมเนียมที่ `VatAmount == null` เมื่อช่องทางเป็น ThaiVat7 (`SettlementBatchMath.cs:540-541` → `SettlementFeeTax.cs` สาขา `else` คิด `deducted − round(deducted×100/107)`)
  — เทสต์ของทีมเองพิสูจน์: `SettlementGatewayPhase2Tests.cs:217-227` ได้ `InputVat = 2.73` จากค่าธรรมเนียม 41.79 ที่ไม่มี VAT
- ค่าเริ่มต้นคือคู่ที่ผิด: ช่องทาง `FeeVatMode = ThaiVat7` (`Models/Entities/Settlement.cs:42` · DTO `SettlementDtos.cs:189`) · config gateway `GatewayFeeVatMode.None`
- **ฉาก**: รอบ 198–199 ผู้ใช้สร้างช่องทาง Gateway ด้วยค่าเริ่มต้น แล้วกด "ประกอบรอบโอนจากรายการรับชำระ" (เส้นเดิมของเฟส 1 ใส่ค่าธรรมเนียมเป็นยอดเต็ม `VatAmount = null`) → รอบโอนสถานะ Imported/Matched ยังไม่ลงบัญชี → deploy รอบ 200 → กด "ลงบัญชี" → ไม่มีด่านใดถามเรื่องโหมด → ใบค่าธรรมเนียม Dr 11630 2.73 / Dr ค่าธรรมเนียม 39.06 ต่อค่าธรรมเนียม 41.79 ที่ไม่มี VAT จริง (ภาษีซื้อเกินจริง ≈ 6.54% ของค่าธรรมเนียมทุกรอบ)
- รายงานทีม `team-P2.md:77` เขียนว่ารอบโอนที่ยังไม่ลงบัญชี "โหมดอื่นลงได้ตามเดิม (VAT อาจต่างเป็นสตางค์)" — **ผิด** สำหรับคู่ None↔ThaiVat7 (ต่างเต็ม 7/107 ไม่ใช่สตางค์) และ SQL ใน §5 ถูกวางเป็น "ให้นักบัญชีตรวจรอบที่ลงแล้ว" ไม่ใช่ด่านก่อนลง
- ช่องทางเดิมที่โหมดขัดยังนำเข้า**ไฟล์** gateway ได้ (ไฟล์ที่ไม่มีคอลัมน์ VAT ⇒ 7/107 เช่นกัน) — ข้อ 8 ของรายงาน ("โหมดของช่องทางถูกบังคับให้ตรง config ตั้งแต่ตอนบันทึกช่องทาง") จริงเฉพาะช่องทางที่บันทึก**หลัง** deploy
- config เปลี่ยนโหมดหลังนำเข้า (B-3 · `PaymentSettingsController.cs:176-177` ไม่ตรวจ) แล้วลงบัญชีรอบที่นำเข้าไว้แล้ว — ก็ไม่ผ่านด่านเช่นกัน
- **ทางแก้ที่เสนอ**: เรียก `ModeMismatch` ในด่านก่อนลงบัญชี (`SettlementPostingService` ช่วงสร้าง plan) เมื่อช่องทางผูก config (อย่างน้อยเมื่อรอบมีบรรทัดที่พก `PaymentIntentId`) · ข้อความทางไปต่อเดียวกัน (แก้โหมด → ยกเลิกรอบ → ประกอบใหม่) · + เทสต์ "รอบเก่า VatAmount null + คู่ None/ThaiVat7 ⇒ ลงบัญชีไม่ได้" และทิศตรงข้าม "คู่ที่ตรงกันลงได้" · ล็อกจุดเรียกใน `required_call_site_check.py`

### X-2 (P2) — ขอบช่วงวันที่เลื่อน 7 ชั่วโมง (สองเส้นเลือกคนละชุดหลัง merge ทีม G)

- `SettlementImportService.Gateway.cs:91-92`: `from = ThaiDate.CalendarDateUtc(pf)` · `to = ThaiDate.CalendarDateUtc(pt).AddDays(1)` — ได้ **ป้ายวันไทย ที่ 00:00 UTC** (= 07:00 น. เวลาไทย) แล้วเทียบกับ `ConfirmedAt` ซึ่งเป็นเวลา UTC จริง (`GatewayBatchIntentRules.cs:83-84`)
- ทีม G รอบเดียวกัน (branch `worktree-agent-af1a982aa3543c331`, `GatewaySettlementMath.cs:175-184` `ConfirmedRangeUtc` = เที่ยงคืนไทย −7h) แก้บั๊กนี้ในเส้นเดิม (`GatewaySettlementService.cs:445`) และรายงานกระทบยอด — เส้น P2 ยังใช้สูตรเก่า
- **ฉาก**: เลือกช่วง 20–20 ก.ย. · intent A รับเงิน 20 ก.ย. 03:00 น. (19 ก.ย. 20:00Z) ⇒ ถูกตัดออก + ขึ้นคำเตือน "ก่อนต้นช่วง" (`olderUnclaimed` ใช้ขอบเดียวกัน `:99-102`) · intent B รับเงิน 21 ก.ย. 05:00 น. (20 ก.ย. 22:00Z) ⇒ ถูกดึงเข้ามา + ถูกประทับเป็นของรอบนี้ ⇒ รอบโอนไม่ลงตัว หรือ (ถ้าผู้ใช้ "ปิดส่วนต่าง" ด้วยบรรทัดปรับปรุง) intent B ถูกล้างผังพักในรอบที่ไม่ได้โอนมันจริง
- P2-5 (ใช้ `PeriodFrom` ที่เคยถูกเพิกเฉย) ทำให้ขอบล่างที่เลื่อนมีผลเป็นครั้งแรก — เป็นบั๊กใหม่ของรอบนี้ ไม่ใช่แค่ของเดิม
- **ทางแก้**: หลัง merge ทีม G ใช้ `GatewaySettlementMath.ConfirmedRangeUtc` (หรือตัวช่วยเที่ยงคืนไทยตัวเดียวกัน) ทั้งขอบล่าง/บน + เทสต์ intent ที่ 03:00 น. ไทยวันแรก / 05:00 น. วันถัดจากวันสุดท้าย

### X-3 (P2) — คู่โหมดที่ด่านอนุญาตแต่สองเส้นให้ภาษีต่างกัน

- `GatewayBatchIntentRules.cs:51-53`: config `None` ↔ ช่องทาง `ForeignPp36` ผ่าน · เส้น batch: `SettlementFeeTax` ตั้ง `Pp36Payable = 7% × ค่าธรรมเนียม` (Dr 11640 / Cr 21912 · ไม่จด VAT ⇒ เป็นต้นทุนเพิ่ม) · เส้นเดิม (`GatewaySettlementMath.PlanCore`) ไม่มีแนวคิด ภ.พ.36 เลย
- `GatewayBatchIntentRules.cs:58-60`: config `WhtOnFee = None` ↔ ช่องทาง `SelfWithholdReimbursed` (W2) ผ่าน · เส้น batch ลง Dr ลูกหนี้แพลตฟอร์ม / Cr 21917 + 50 ทวิ (`SettlementBatchMath.AddWhtLegs`) · เส้นเดิมไม่ลง
- ทั้งสองเส้นยังเปิดสำหรับ provider เดียวกัน (B-2 ยังไม่ทำ) ⇒ intent 1,000 รายการของเดือนเดียว ภาษี ภ.พ.36/WHT ที่เกิดขึ้นจริงขึ้นกับว่าผู้ใช้กดหน้า "รอบโอน gateway เดิม" หรือ "รอบโอน settlement" — "สองความจริง" ที่คำตัดสิน "สูตรเดียว" (docstring `GatewayBatchIntentRules.cs:19-20` · DOCUMENT_FLOW §2.10) บอกว่าไม่มี
- ทีมยอมรับใน Q1/Q4 (`team-P2.md` §8) ว่าเส้น batch "ถูกกว่า" — ข้อนี้ไม่ใช่ขอให้ถอย แต่ต้อง (ก) แก้ถ้อยคำ "สูตรเดียว" ให้ระบุข้อยกเว้น และ (ข) ให้เจ้าของตัดสินว่าจะบล็อกเส้นเดิมสำหรับ config ที่ช่องทางผูกเป็น ForeignPp36/W2 (หรือ B-2) — ไม่งั้นหนี้ ภ.พ.36 หายเงียบเมื่อผู้ใช้เลือกเส้นเดิม
- เทสต์ `SettlementGatewayPhase2Tests.cs:204` ล็อกว่า None↔ForeignPp36 "ผ่าน" แต่ไม่มีเทสต์ที่เทียบกับเส้นเดิมเหมือนคู่อื่น

### X-4 (P3) — WHT ออกภาษีแทนคิดรายบรรทัด (ทางเข้าเปิดแล้ว)

- บรรทัดค่าธรรมเนียมจาก intent ในโหมด Included/AddedOnTop มี `VatAmount` เสมอ (`PaymentIntentAdapter.cs:63-65`) ⇒ `SettlementBatchMath.cs:538-539` คิด `SettlementFeeTax.Compute` ทีละบรรทัด รวมถึง WHT `round(preVat×3/97)` ต่อบรรทัด
- **ตัวเลข**: ค่าธรรมเนียม 3.65 (รวม VAT) × 100 รายการ — VAT/รายการ 0.24 · ก่อน VAT 3.41 · WHT รายบรรทัด 0.11 ×100 = **11.00** · คิดรวม `WhtOnFee(341.00)` = **10.55** ⇒ ภ.ง.ด.53/50 ทวิ/ค่าใช้จ่ายสูงไป 0.45 ต่อรอบ
- ด่าน `ModeMismatch` บังคับคู่ config Withhold3Percent ↔ ช่องทาง W3 (`:58-59`) แต่เส้นเดิมบล็อก WHT ทั้งหมด (`WhtCertificateRequired`) ⇒ เส้น batch เป็นทางเดียวที่ลง WHT ได้และลงด้วยยอดรายบรรทัด · ทีมจดเป็น B-6 — เสนอให้บล็อก W3 ของรอบที่มีบรรทัด intent จนกว่า B-6 แก้ (หรือแก้ใน `BuildFeeLines` ให้รวมฐานก่อนคิด WHT)

## 2. PLAUSIBLE

### X-5 (P3) — เติมรอบโอนเดิม: จุดตัดยอดคืนไม่ใช่วันเงินเข้าของรอบ
`Gateway.cs:56-59,113` ใช้ `h.PayoutDate` ของคำขอ · `PersistAsync` (`SettlementImportService.cs:306-308`) เมื่อ PayoutRef ซ้ำ ใช้ `batch.PayoutDate` เดิมแล้วแค่เตือน ⇒ ถ้าวันที่ต่างกัน ยอดคืน ณ วันเงินเข้าถูกคิดกับวันผิด (คืนเงินตกผิดรอบ · รอบไม่ลงตัว) — เสนอ: ถ้ามี batch เดิม ใช้ `PayoutDate` ของ batch เป็นจุดตัด หรือปฏิเสธเมื่อวันที่ไม่ตรง

### X-6 (P3) — ล็อก "เจ้าของ" ครอบแค่ทางเข้าเดียว
`SyncIntentStampsAsync` (`SettlementImportService.Lines.cs:159-176`) ประทับ `SettlementBatchId` ให้ intent ที่บรรทัดอ้าง — เรียกจาก `PersistAsync` ของ**ไฟล์** (`GatewayProviderCode = null` ⇒ ไม่ถือล็อก gateway `:198-201`), จับคู่มือ, rematch — และไม่ตรวจ `SettlementJournalEntryId` ใต้ล็อก · ถ้า `GatewaySettlementService.RecordAsync` (ล็อก gateway) commit ระหว่างนั้น intent ได้สองเจ้าของ
— ตาข่าย: `SettlementPostingService.cs:590` + `SettlementSaleMatch.IntentSettledElsewhere` (`:262-264`) บล็อกการลงบัญชี ⇒ ไม่เป็นเงินผิด แต่รอบค้าง · docstring `GatewayBatchIntentRules.cs:13-16` ("ทั้งสองเส้น…ถือล็อกตัวเดียวกัน") ควรระบุขอบเขต

### X-7 (P3) — ตาข่ายยอดคืนไม่ครอบเส้นไฟล์
`EnsureIntentRefundCapacityAsync` รันเมื่อ `GatewayProviderCode != null` เท่านั้น (`SettlementImportService.cs:219-220`) · เส้นไฟล์จัดสรรยอดคืนใน `MatchLinesAsync` (`Lines.cs:~95-133`) ใต้ล็อกช่องทางอย่างเดียว ⇒ ช่องทาง A (ไฟล์) กับช่องทาง B (intent) ที่ผูก config เดียวกันกดพร้อมกันได้บรรทัดคืนเงินของ intent เดียวกันสองรอบ (ทั้งคู่ "อยู่ในผังพักแล้ว") — ผลเห็นเป็นรอบไม่ลงตัว ไม่เงียบ

### X-8 (P3) — `PeriodTo` ว่าง = ไม่มีขอบบน
`Gateway.cs:92` + `UnclaimedForBatch :84` ⇒ ดึงทุก intent ที่ยังไม่มีเจ้าของจนถึงวันนี้ รวมที่รับเงินหลังวันเงินเข้า (ผู้ให้บริการโอนมาในรอบนี้ไม่ได้) และประทับเป็นของรอบนี้ · เส้นเดิม `ToDate` บังคับ · เสนอ: ปลายช่วงว่าง ⇒ ใช้ `RefundCutoffUtc(payoutDate)` เป็นขอบบน (ของเดิม — แต่รอบนี้แตะเมธอดนี้แล้ว)

### X-9 (P3) — VAT ค่าธรรมเนียมสองทางเคลม
`GatewaySettlementService.LoadFeeVatAgingAsync` (`:556-583`) นับ 11630 เฉพาะ JE ที่ `SettlementJournalEntryId` ชี้ · VAT ของ intent ที่ batch เป็นเจ้าของไปอยู่ในใบ `PaymentVoucher` ของรอบโอน (`SettlementPosting.cs:531`) ⇒ ใบกำกับรายเดือนของ provider ใบเดียวต้องแยกเคลมสองหน้า และ `GatewayFeeVatClaim.Check(req.VatAmount, aging.Outstanding, …)` ปฏิเสธยอดเต็มใบ — ไม่ใช่ยอดผิดแต่เป็นทางที่ผู้ใช้จะลงผิดได้ (ควรอยู่ใน backlog คู่ B-1)

### X-10 (P3) — ทิศตรงข้ามของด่านช่องทาง
`SettlementChannelService.cs:143` ตรวจทุกครั้งที่บันทึก (ไม่ใช่เฉพาะเมื่อโหมด/การผูกเปลี่ยน) ⇒ ช่องทางเดิมที่คู่ค่าเริ่มต้นขัดกันแก้ชื่อ/ปิดใช้งาน/เปลี่ยนบัญชีไม่ได้จนกว่าจะแก้โหมด (ข้อความมีทางไปต่อ — ยอมรับได้) · บริษัท**ไม่จด VAT** คู่ None↔ThaiVat7 ได้ตัวเลขเท่ากันทุกช่อง (`VatNotClaimable` ⇒ ค่าใช้จ่ายเต็มจำนวน) แต่ถูกบล็อก · ไม่มีเทสต์ "แก้ช่องอื่นของช่องทางเดิมที่โหมดขัด"

## 3. NOT-A-BUG (ตรวจแล้ว)

- **สูตรค่าธรรมเนียม/VAT/เงินคืน** (`PaymentIntentAdapter.cs:61-72` ↔ `GatewaySettlementMath.Contribution :214-240`) คำนวณมือ:
  - AddedOnTop ค่าธรรมเนียม 39.06: VAT `round(2.7342)=2.73` · หัก 41.79 · batch: `SettlementFeeTax` เชื่อ VAT ที่ระบุ ⇒ ก่อน VAT 39.06 · ภาษีซื้อ 2.73 = เส้นเดิม
  - IncludedInFee 10.00: `round(10×7/107=0.6542)=0.65` ⇒ ก่อน VAT 9.35 ทั้งสองเส้น (×3 = 1.95 ทั้งคู่)
  - IncludedInFee 3.65: `round(0.23879)=0.24` · ก่อน VAT 3.41 ทั้งสองเส้น
  - None 41.79: ไม่มี VAT ทั้งสองเส้น (ช่องทาง None) · ไม่จด VAT: 41.79 เป็นค่าใช้จ่ายทั้งสองเส้น
  - คืนเต็ม 1,000 ก่อนรอบ: ขาย 1,000 − คืน 1,000 − ค่าธรรมเนียม = `Contribution.Net` (clearing 0 − fee) ✓
  - ปัด `MidpointRounding.AwayFromZero` ทุกจุด (`GatewaySettlementMath.cs:391` · `SettlementFeeTax.R`)
- **`RefundCutoffUtc`** (`GatewaySettlementMath.cs:168-169`) = เที่ยงคืนไทย −7h — ตรงกับ `BangkokMidnightUtc` ของทีม G ตัวอักษรต่อตัวอักษร · ต่างเฉพาะช่วงวันที่ (X-2)
- **race เส้นเดิม ↔ เส้น intent**: เส้นเดิมถือล็อก `GatewaySettlement` **ก่อน** `BuildPlanAsync` (`GatewaySettlementService.cs:194-198`) และกรอง `SettlementBatchId == null` (`:374`) · เส้น intent ถือล็อกเดียวกัน (`SettlementImportService.cs:198-201`) แล้วตรวจ `taken` ใต้ล็อก (`:204-214`) ก่อนประทับใน tx เดียว ⇒ ไม่มีลำดับที่ intent ได้สองเจ้าของ (ระหว่างสองเส้นนี้)
- **ยกเลิก batch**: `VoidBatchAsync` ปลดประทับ (`Lines.cs` `SyncIntentStampsAsync(..., empty)`) → intent กลับเป็นไม่มีเจ้าของ (ถูกต้อง) · บล็อกเมื่อรอบถัดไปมีบรรทัดคืนเงินของ intent ในรอบนี้ (`SETTLEMENT-VOID-ORDER`) · การปลดไม่ถือล็อก gateway แต่ "ปลด" อย่างเดียวไม่ทำให้ซ้ำ
- **`LateRefundInBatch`**: `SettlementJournalEntryId == null && SettlementBatchId != null` ⇒ ของเส้นเดิมไม่เข้า (เส้นเดิมหักเองผ่าน `RefundSettledAmount` `:387-390`) · ยอด = `AsOf − InLines` · คีย์ `refund@{AsOf}` ต่างกันทุกยอดสะสม · บรรทัดที่ถูกยกเลิกไม่นับ (query filter IsDeleted)
- **EF**: `UnclaimedForBatch` จับ `DateTime?` จากพารามิเตอร์เมธอด ⇒ EF parameterize (`@p IS NULL OR …`) · ค่ามี `Kind = Utc` (Npgsql timestamptz ผ่าน) · projection เข้า private record ใน `Select` สุดท้ายรองรับ
- **`EnsureIntentRefundCapacityAsync` ใต้ล็อกจริง**: หลัง `LockChannelAsync` + `pg_advisory_xact_lock(GatewaySettlement)` และก่อน `_db.SettlementLines.Add` (`SettlementImportService.cs:197-220`) · ใช้ `RefundedAmount` สะสมเป็นเพดาน (ถูก)
- **endpoint/tenant**: `POST batches/from-payment-intents` = `[Authorize]` + `RequirePermission(SettlementPermissionScope.Import)` (ไม่เปลี่ยน) · query ใหม่ทุกตัวมี `CompanyId == companyId` (intent · event · line · config)
- **คอมไพล์ (อ่านโค้ด)**: partial `SettlementImportService.Gateway.cs` — using ครบ (`Helpers` มี `BusinessRuleException`/`ThaiDate`/`GatewaySettlementMath` · `Adapters` · `DTOs.Settlement` · `Enums` · EF) · สมาชิกที่อ้าง (`LoadChannelAsync` · `EnsureActive` · `GatewayClearingMatchesAsync` · `PersistAsync` · `PersistInput`) มีในไฟล์หลัก · ไม่มีสมาชิกซ้ำ (grep ทั้งเรพ: `LoadIntentRowsAsync`/`RefundInLinesAsync`/`EnsureIntentRefundCapacityAsync`/`GatewayIntentRow`/`GatewayIntentLoad` นิยาม 1 ที่) · `out var` ใน lambda LINQ-to-objects · `(decimal?)null : -part.FeeVat` · tuple array → `IEnumerable<(Guid IntentId, decimal Amount)>` identity conversion · local function ชื่อ `AsOf` กับชื่อ element tuple `AsOf` ไม่ชนกัน · ผู้เรียก `BuildRows` ครบ 12 จุด (เทสต์ 11 + service 1) ทุกจุดส่ง `GatewayFeeVatMode` · `SettlementImportTests.cs` มี `using Accounting.Models.Enums`
  - checker ที่รันใน worktree ของทีม (อ่านอย่างเดียว): `using_check` · `record_arg_check` · `accessibility_check` · `nullable_arg_check` · `tuple_name_merge_check` · `string_quote_close_check` · `comment_line_break_check` · `identifier_space_check` = 0 ปัญหา · `required_call_site_check` รันไม่จบใน 10 นาทีในเครื่องนี้ (ไม่ได้ผล — ต้องดู CI)

## 4. ตอบ 3 คำถามฝ่ายค้าน (F3 ข้อ 11)

1. **ทางเข้าอื่น?** — การลงบัญชี (X-1) · นำเข้าไฟล์ของช่องทาง gateway เดิม (X-1/X-7) · จับคู่มือ/rematch (X-6) · บันทึก config gateway (B-3 ทีมรู้แล้ว — แต่ผลไม่ใช่ "บล็อกครั้งถัดไป" อย่างเดียว เพราะรอบที่นำเข้าไว้แล้วลงได้โดยไม่ตรวจ)
2. **ทิศตรงข้าม?** — ช่องทางเดิมที่โหมดขัดแก้ช่องอื่นไม่ได้ + บริษัทไม่จด VAT ถูกบล็อกทั้งที่ตัวเลขเท่ากัน (X-10) · มีทางไปต่อในข้อความ
3. **สถานะปลายทางประทับเอง?** — `SettlementBatchId` ประทับตอนนำเข้า (ก่อนลงบัญชี) ด้วยช่วงวันที่ที่เลื่อน (X-2) / ไม่มีขอบบน (X-8) ⇒ intent ที่ไม่ได้อยู่ในรอบโอนจริงถูกประกาศเป็นของรอบนั้นจนกว่าคนจะลบบรรทัด

---

## สถานะการแก้ (รอบ 200 ทีม SF · รายงาน `team-SF.md` · คำตัดสิน DECISIONS ข้อ 26)

| ID | สถานะ | ที่แก้ / เหตุผล |
|---|---|---|
| X-1 | ✅ c7bad3f5 | ด่านลงบัญชี `SettlementPostingService.BuildGateAsync` → `GatewayBatchIntentRules.PostingIssue(ModeMismatch(…))` บล็อก `GatewayModeMismatch` (37) · นำเข้าไฟล์ `ImportFileAsync` · บันทึกค่าตั้ง gateway `PaymentSettingsController.Save` (`ConfigChangeRefusal` — B-3) · แก้ถ้อยคำ `team-P2.md` §5 · เทสต์ `X1_ด่านลงบัญชี_…` · `X1_บันทึกค่าตั้งgateway_…` |
| X-2 | — | นอกขอบเขต (ทีม GF ทำ `ConfirmedRangeUtc`) |
| X-3 | ✅ c7bad3f5 | `ModeMismatch(…, companyVatRegistered)` นิยาม "ตรงกัน" = สองเส้นให้ผลภาษีเท่ากัน: ภ.พ.36 ไม่ตรงเสมอ (ทางไปต่อ: ช่องทางไม่ผูก config + นำเข้าไฟล์) · ไม่หัก ↔ W2 ไม่ตรง · W1 ตรง (ไม่มีขา JE/50 ทวิ) · DOCUMENT_FLOW §2.10 · docstring แก้ "สูตรเดียว" · เทสต์ `X3_โหมดต้องให้ผลภาษีเท่ากันทั้งสองเส้น` (Theory 10) · `X3_ForeignPp36_…` |
| X-4 | ✅ c7bad3f5 | `SettlementBatchMath.BuildFeeLines` คิด WHT จากฐานก่อน VAT รวมของบรรทัดใบครั้งเดียว (`SettlementFeeTax.WhtOnBase` สูตรเดียว — `Compute` เรียกตัวเดียวกัน) · ไม่ขัดตรรกะรายบรรทัดอื่น (VAT ยังต่อรายการ · ก้อนเดียวตัวเลขเท่าเดิม) · เทสต์ `X4_…10_55ไม่ใช่11_00` + ทิศตรงข้าม |
| X-5 | ✅ c7bad3f5 | `PersistAsync`: เติมรอบโอน gateway (PayoutRef ซ้ำ) ด้วยวันเงินเข้าต่างจากรอบเดิม ⇒ 400 `SETTLEMENT-GATEWAY-PAYOUT-DATE` (เส้นไฟล์คงคำเตือนเดิม) · checker |
| X-6 | 📋 | ล็อก gateway ครอบเส้นไฟล์/จับคู่มือ/rematch — ต้องแตะ `SettlementImportService.Lines` หลายเส้น (ไฟล์ทีม T/I) · ตาข่ายที่การลงบัญชีกันเงินผิดแล้ว (รอบค้าง ไม่ใช่เงินผิด) |
| X-7 | 📋 | คู่ของ X-6 (ตาข่ายยอดคืนเส้นไฟล์) — ผลเห็นเป็นรอบไม่ลงตัว ไม่เงียบ |
| X-8 | ✅ ทีม SG (R2M-6 · sha ใน team-SG.md) | `PeriodTo` ว่าง = ไม่มีขอบบน — ทีม GF ไม่ได้ทำ (ฝ่ายค้านรอบสอง R2M-6) ⇒ ทีม SG: `LoadIntentRowsAsync` ใช้ `ConfirmedToExclusiveUtc(periodTo ?? วันเงินเข้า − 1)` · เดิม: "แก้พร้อม X-2 (ทีม GF)" |
| ✅ <pending> รอบ 201 ทีม GW X-9 (A-GW5) | 📋 | หน้าเคลม VAT ค่าธรรมเนียม gateway ไม่เห็น VAT ของ intent ที่ batch เป็นเจ้าของ — คู่ B-1 (ไฟล์ทีม G) |
| X-10 | ✅ c7bad3f5 | `SettlementChannelService.SaveAsync` ตรวจเฉพาะช่องทางใหม่/เมื่อการผูกหรือโหมดเปลี่ยน (`ChannelModeTouched`) · บริษัทไม่จด VAT คู่ VAT ไทยผ่าน · เทสต์ `X10_…` |
