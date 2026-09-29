# รอบ 200 ทีม P2 — settlement เฟส 2: รายการ payment gateway (PaymentIntent) เข้ารอบโอน `SettlementBatch`

> ขอบเขต: `BRIEF.md` แถว P2 · คำตัดสิน `DECISIONS.md` ข้อ 12 (เฟส 2 ทำเลย) · แหล่ง `settlement/report-S1.md` §2 (JE G1–G4) · `report-S2.md` §3–§4 ·
> `review198-B.md` R-B4 · `review198-E2.md` · โค้ดที่ HEAD `5c1fe028` · **ยังไม่ได้คอมไพล์** (ไม่มี .NET SDK ในเครื่อง — รบกวน rebuild ฝั่งคุณ/อ่านผล CI)

## 0. verify ก่อนเชื่อ — เฟส 2 ส่วนใหญ่ "มีโครงแล้ว" ตั้งแต่เฟส 1

เปิดโค้ดจริงแล้วพบว่าทีม B/S3 รอบ 198 ทำทางเข้า "ประกอบรอบโอนจาก PaymentIntent" ไว้แล้ว (`ImportFromPaymentIntentsAsync` · `PaymentIntentAdapter` ·
endpoint `POST settlement/batches/from-payment-intents` · ปุ่มโหมด `intents` ใน `settlements.html`) · ด่านกันซ้ำ**ต่อ intent สองทิศมีแล้ว** (เส้นใหม่กรอง
`SettlementJournalEntryId == null && SettlementBatchId == null` · เส้นเดิม `GatewaySettlementService.cs:374` กรอง `SettlementBatchId == null`) · ล็อก
`AdvisoryLockKey.GatewaySettlement` ตัวเดียวกัน · R-B4 (ไม่มีร่องรอย ⇒ ใบสรุป) ปิดแล้วที่ทีม S3 (ค้น intent ทั้งบริษัทเสมอ · พบแต่ใช้ไม่ได้ ⇒ `Unmatched`) ·
บรรทัดที่พก `PaymentIntentId` ถูก `SettlementBatchMath.Plan` นับ "อยู่ในผังพักแล้ว" (ไม่ใช่ขายใหม่)

ดังนั้นงานรอบนี้ = **ทำให้เส้นใหม่ให้ตัวเลขเดียวกับเส้นเดิม และปิดช่องที่ทำให้มี "สองความจริง"** — ไม่ได้สร้างทางเข้าใหม่

## 1. คำตัดสิน: สองเส้นอยู่ร่วมกันอย่างไร

**"หนึ่งรายการ หนึ่งเจ้าของ · สูตรเดียว · ข้อเท็จจริงเดียว"** (เขียนเป็นกติกาใน `Accounting/Helpers/GatewayBatchIntentRules.cs:8-27`)

| หลัก | กติกา | ล็อกด้วย |
|---|---|---|
| หนึ่งเจ้าของ | intent เข้ารอบโอนได้ครั้งเดียว — เส้นเดิมประทับ `SettlementJournalEntryId` · เส้นใหม่ประทับ `SettlementBatchId` · ทั้งสองเส้นเลือกเฉพาะ intent ที่ **ทั้งสองช่องว่าง** · ถือล็อก `GatewaySettlement` ตัวเดียวกัน | `UnclaimedForBatch` (expression ตัวเดียว · EF แปล SQL · เทสต์ compile รันกับวัตถุจริง) · `required_call_site_check`: `LoadIntentRowsAsync` ห้ามเขียนเงื่อนไขเจ้าของเอง · `GatewaySettlementService.SelectCandidatesAsync` ต้องมี `i.SettlementBatchId == null` (ทิศกลับ) |
| คืนเงินภายหลังตามเจ้าของ | เจ้าของเดิม ⇒ เส้นเดิมหักรอบถัดไปของมัน · เจ้าของ batch ⇒ บรรทัดคืนเงินในรอบโอนถัดไปของ batch · ยอดคืนก้อนเดียวไม่ถูกหักสองเส้น | `LateRefundInBatch` (`SettlementJournalEntryId == null && SettlementBatchId != null`) · ตาข่ายใต้ล็อก `RefundLinesOverRefunded` |
| สูตรเดียว (ทีม SF: จริงเฉพาะคู่โหมดที่ให้ผลภาษีเท่ากัน — คู่ ภ.พ.36 / "หักเองแล้วได้คืน" ถูกนิยามเป็น "ไม่ตรง" ตาม DECISIONS ข้อ 26) | ค่าธรรมเนียมที่ถูกหัก + VAT ค่าธรรมเนียม = `GatewaySettlementMath.Contribution` ตามโหมดของ config · ยอดคืน ณ วันเงินเข้า = `RefundCutoffUtc` + `RefundedAsOf` · ภาษีของใบค่าธรรมเนียม = `SettlementFeeTax.Compute` (รับ VAT ที่ระบุต่อรายการ) | `required_call_site_check` แถว `PaymentIntentAdapter.BuildRows` (ห้าม `FeeActual ?? FeeEstimated` · ห้าม 7/107 · ห้าม `SettlementFeeTax.` ในตัวประกอบ) · เทสต์ parity |
| ข้อเท็จจริงเดียว | "ผู้ให้บริการคิด VAT บนค่าธรรมเนียมไหม · เราหัก ณ ที่จ่ายไหม" เก็บสองที่ (config gateway · ช่องทาง) ⇒ ต้องตรงกัน ไม่งั้นบล็อกพร้อมทางไปต่อ (ระบบไม่เลือกฝั่งให้) | `ModeMismatch` ที่ทางเข้า 2 ทาง (ประกอบรอบโอน · บันทึกช่องทาง) |

**ไม่ปิดเส้นเดิม** ในรอบนี้ (ทางเลือกที่ย้อนได้): ทั้งสองเส้นให้ตัวเลขเท่ากันแล้ว และ intent อยู่ได้เส้นเดียว ⇒ ไม่มีเงินซ้ำ · การปิดเส้นเดิมต่อ provider
(เมื่อมีช่องทาง batch ผูกอยู่) เป็นงาน UX + ไฟล์ของทีม G — ดู §4 B-2

## 2. รายการที่แก้ (✅) — ทุกข้อเป็นบั๊กที่ทำให้ตัวเลขเข้าบัญชีผิดหรือบล็อกถาวร

| ID | ปัญหา (ที่ HEAD เดิม) | ผลกับเงิน | แก้ที่ | เทสต์ |
|---|---|---|---|---|
| P2-1 | `PaymentIntentAdapter` ใส่ค่าธรรมเนียม `FeeActual ?? FeeEstimated` **ไม่ดู `GatewayFeeVatMode`** · config "บวก VAT เพิ่ม" ⇒ ยอดที่ผู้ให้บริการหักจริง = ค่าธรรมเนียม + VAT แต่บรรทัดมีแค่ก่อน VAT | รอบโอนไม่ลงตัวทุกรอบ (ต่าง = VAT) ⇒ บล็อกถาวร หรือผู้ใช้ "แก้" ด้วยบรรทัดปรับปรุงผิดผัง | `Adapters/PaymentIntentAdapter.cs:61-65` ใช้ `GatewaySettlementMath.Contribution` · บรรทัด `PaymentFee` = ยอดที่ถูกหัก · `VatAmount` = VAT ต่อ charge | `บวกVATเพิ่ม_…เท่าเส้นเดิม` (61.32 = 57.31 + 11630 4.01) |
| P2-2 | config "ไม่แยก VAT" + ช่องทาง "VAT ไทย 7%" (ค่าเริ่มต้นหน้าช่องทาง = ThaiVat7 · ค่าเริ่มต้น config = None) ⇒ `SettlementFeeTax` แยก 7/107 จากค่าธรรมเนียมที่ไม่มี VAT | **ภาษีซื้อแต่งขึ้น** (11630) — เส้นเดิมได้ 0 · ตัวอย่าง 41.79 ⇒ ภาษีซื้อปลอม 2.73 | `GatewayBatchIntentRules.ModeMismatch` (`Helpers/GatewayBatchIntentRules.cs:47`) ที่ `SettlementImportService.Gateway.cs:45` + `SettlementChannelService.cs:143` | `เหตุผลของด่านโหมด_…แต่งภาษีซื้อ…` · Theory 11 คู่สองทิศ |
| P2-3 | config "รวม VAT ในค่าธรรมเนียม" ⇒ รอบโอนแยก VAT จาก**ก้อนรวม**ครั้งเดียว · เส้นเดิมปัดต่อรายการ | VAT ต่างกันเป็นสตางค์ (10.00 ×3: 1.95 vs 1.96) = สองความจริงของภาษีซื้อ | VAT ต่อรายการใน `VatAmount` ⇒ `SettlementBatchMath.BuildFeeLines` คิดทีละบรรทัด (เชื่อค่าที่ระบุ) | `รวมVATในค่าธรรมเนียม_VATปัดต่อรายการ…` |
| P2-4 | ยอดคืนใช้ `RefundedAmount` สะสม ณ วันกดปุ่ม · เส้นเดิมใช้ยอดคืน ณ วันเงินเข้า (R-E2) | คืนเงินหลังวันเงินเข้า (ผู้ให้บริการหักรอบถัดไป) ถูกนับในรอบนี้ ⇒ ยอดไม่ลงตัว | `LoadIntentRowsAsync` (`SettlementImportService.Gateway.cs:88-144`) — `RefundCutoffUtc(PayoutDate)` + `RefundedAsOf` · แยกไม่ได้ ⇒ 400 `SETTLEMENT-REFUND-TIMING-UNKNOWN` (ห้ามเดา · ข้อความเดียวกับเส้นเดิม) | `คืนเงินหลังวันเงินเข้า_ไม่อยู่ในรอบนี้…` (+รอบถัดไป −200 · ทิศไม่รู้) |
| P2-5 | หัวรอบโอน `PeriodFrom` ถูกเพิกเฉยเงียบ (ดูแค่ปลายช่วง) — เส้นเดิมใช้ทั้งสองฝั่ง | ช่องบนจอไม่มีผล (silent no-op) · รายการเก่าที่ไม่เกี่ยวถูกดึงเข้ามาทำให้ไม่ลงตัว | `UnclaimedForBatch(…, from, to)` · รายการเก่ากว่าต้นช่วงที่ยังไม่มีเจ้าของ ⇒ **คำเตือนพร้อมจำนวน** (ไม่หายไปจากทุกรอบ) | `รายการที่ยังไม่มีเจ้าของเท่านั้น…` (มี/ไม่มีช่วง) |
| P2-6 | คืนเงินภายหลังอ่าน "ยอดที่อยู่ในบรรทัดแล้ว" **นอกล็อก** · สองช่องทางผูก config เดียวกันได้ (ไม่มี unique) | กดพร้อมกัน ⇒ บรรทัดคืนเงินซ้ำสองช่องทาง ⇒ ผังพักถูกหักสองครั้ง | `EnsureIntentRefundCapacityAsync` เรียกใน `PersistAsync` หลังล็อกช่องทาง+ล็อก gateway ก่อนเพิ่มบรรทัด (`SettlementImportService.cs:218-220`) → `RefundLinesOverRefunded` | `ตาข่ายใต้ล็อก_…` (เกิน/ไม่เกิน/เศษ 0.01) |
| P2-7 | `FeeActual = 0` (รู้แล้วว่าไม่มีค่าธรรมเนียม) ถูกนับว่า "ไม่รู้ค่าธรรมเนียม" | คำเตือนผิด (ฟ้องรายการที่ถูก) | `PaymentIntentAdapter.cs:66` นับไม่รู้เฉพาะ `FeeActual == null` และประมาณการ 0 | `ค่าธรรมเนียมจริงเป็นศูนย์_…` |

ทิศตรงข้าม (ของที่ถูกอยู่แล้วต้องไม่ถูกแตะ) — ล็อกในไฟล์เดียวกัน: โหมดที่ตรงกัน 5 คู่ผ่าน · "ไม่แยก VAT" + ช่องทางไม่มี VAT ได้ค่าใช้จ่ายทั้งก้อนเท่าเดิม ·
บริษัทไม่จด VAT ไม่มีภาษีซื้อ (เท่าเส้นเดิม) · intent ของเส้นเดิม/ของรอบโอนอื่น/คืนก่อนระบบเก็บยอด/Pending/บริษัทอื่น/provider อื่น ไม่ถูกดึง · คืนภายหลังของเส้นเดิมไม่เข้า batch ·
บรรทัดขายจากไฟล์ที่ไม่มีร่องรอยยังได้ใบขายสรุปตามเดิม (marketplace ไม่ถูกแตะ) · marketplace/gateway ไม่ผูก config ประกอบจาก intent ไม่ได้ · ผลรวมบรรทัดต่อ intent =
`Contribution.Net` ทั้ง 3 โหมด (รวมคืนเต็ม/คืนบางส่วน/ค่าประมาณ) · เทสต์เดิม `SettlementImportTests.intentใหม่_…` ตัวเลขเดิมทุกตัว (โหมด None)

## 3. ไฟล์ที่แตะ

| ไฟล์ | สิ่งที่ทำ |
|---|---|
| `Accounting/Helpers/GatewayBatchIntentRules.cs` (ใหม่) | `ChannelRefusal` · `ModeMismatch` · `UnclaimedForBatch` · `LateRefundInBatch` · `RefundLinesOverRefunded` · `RefundTimingRefusal` |
| `Accounting/Services/Settlement/SettlementImportService.Gateway.cs` (ใหม่ · partial) | ย้าย `ImportFromPaymentIntentsAsync`/`LoadIntentRowsAsync` มาจาก `SettlementImportService.cs` + P2-2/4/5/6 · `RefundInLinesAsync` · `EnsureIntentRefundCapacityAsync` |
| `Accounting/Services/Settlement/SettlementImportService.cs` | ตัดสองเมธอดออก (ย้าย) · จุดต่อ 1 บรรทัดใน `PersistAsync` (ตาข่ายใต้ล็อก) |
| `Accounting/Services/Settlement/Adapters/PaymentIntentAdapter.cs` | `BuildRows(…, GatewayFeeVatMode)` · สูตร `Contribution` · VAT ต่อรายการ · ความหมาย `RefundedAmount` = ณ วันเงินเข้า |
| `Accounting/Services/Settlement/SettlementChannelService.cs` | ทางเข้าที่สองของข้อเท็จจริงเดียวกัน: ผูก gateway ที่โหมดขัดกันไม่ได้ (400 `SETTLEMENT-CHANNEL-GATEWAY-MODE`) |
| `Accounting.Tests/SettlementGatewayPhase2Tests.cs` (ใหม่) · `SettlementImportTests.cs` (ลายเซ็น `BuildRows`) | 12 Fact + 2 Theory (14 InlineData) |
| `tools/required_call_site_check.py` | +9 แถว (P2) · ย้ายไฟล์ของ 2 แถวเดิม (`ImportFromPaymentIntentsAsync` · E2-10 `loaded.RefundUnknown > 0`) |
| `DOCUMENT_FLOW.md` §2.10 · `ACCOUNT_STRUCTURE.md` §3.1d · `TEST_PLAN.md` §0 + SPP2-01..10 · `CHANGELOG.md` · `settlement/report-S2.md` (ติ๊กเฟส 2 บางส่วน) | doc ตามโค้ด |

**ไม่แตะ** (ตามขอบเขต): ตรรกะ orphan/unpost (V2) · ตัวอ่านไฟล์ (I) · `SettlementBatchMath` / เวลา/ภาษีใบสรุป (T) · `GatewaySettlementService` / `PaymentGatewayController` (G — เพิ่มแค่แถว checker ที่อ่านไฟล์ G ไม่แก้โค้ด)

## 4. 📋 backlog / ส่วนที่เหลือของเฟส 2 (แผน)

| ID | เรื่อง | ทำไมยังไม่ทำ | แผน |
|---|---|---|---|
| B-1 | รายงานกระทบยอด gateway (`PaymentGatewayController.cs:194` `IsSettled = i.SettlementJournalEntryId != null`) ไม่รู้จักรอบโอน batch ⇒ intent ที่ batch ลงบัญชีแล้วแสดง "ยังไม่โอน" ตลอด (รายงานอย่างเดียว ไม่ใช่ตัวเลขบัญชี) | ไฟล์ทีม G (G-8 ทุก endpoint) | `IsSettled` = JE ไม่ว่าง **หรือ** batch ที่ `Status ∈ {Posted, BankMatched}` · `SettledAmount` ของเส้น batch = `Contribution.Net` ณ วันลงบัญชี (ให้ผู้ลงบัญชีรอบโอนประทับ — ไฟล์ V2) หรือคำนวณในรายงานจากบรรทัด |
| B-2 | ปิดเส้นเดิมต่อ provider เมื่อมีช่องทาง batch ที่ผูก config อยู่ (เหลือไว้แค่คืนเงินภายหลังของ intent ที่เส้นเดิมเป็นเจ้าของ) | ต้องแก้ `GatewaySettlementService.RecordAsync` + หน้า `payment-settlements.html` (ทีม G) · ปัจจุบันไม่มีเงินซ้ำแล้ว (หนึ่งเจ้าของ) ⇒ เป็นเรื่อง UX ไม่ใช่ความถูกต้อง | ด่านใน `RecordAsync`: provider มีช่องทาง Gateway active ⇒ บล็อก intent ใหม่พร้อมลิงก์หน้ารอบโอน · คงเส้นคืนเงินภายหลัง |
| B-3 | เปลี่ยนโหมด VAT/WHT ที่หน้า "ตั้งค่าการรับชำระเงินออนไลน์" หลังผูกช่องทางแล้ว ไม่ตรวจกับช่องทาง | ไฟล์ทีม G · ผลกระทบ: การประกอบรอบโอนครั้งถัดไปถูกบล็อกพร้อมทางไปต่อ (มองเห็น ไม่เงียบ) | เรียก `GatewayBatchIntentRules.ModeMismatch` ตอนบันทึก config ที่มีช่องทางผูกอยู่ |
| B-4 | `fee_vat` จาก API ผู้ให้บริการ (G-7 · Omise อ่านแค่ `fee`) | ทีม G · ต้องยืนยันกับ sandbox | เมื่อได้ `fee_vat` จริง ⇒ ใส่ใน intent แล้วใช้แทน VAT ที่คำนวณ (`VatAmount` ของบรรทัดรองรับอยู่แล้ว) |
| B-5 | จับคู่ไฟล์ settlement ของ gateway กับ intent — เส้น CSV มีแล้ว (R-B4 · `IntentCandidate` ค้นด้วย `ProviderRef`) · adapter เฉพาะเจ้า (Omise payout report) | DECISIONS ข้อ 12: **รอไฟล์ตัวอย่างจริง** ห้ามเดารูปแบบ | ใช้ `GenericColumnMapAdapter` ไปก่อน · เมื่อมีไฟล์ ⇒ adapter + golden fixture |
| B-6 | ภาษีหัก ณ ที่จ่ายแบบออกภาษีแทน (config "หัก 3%" ↔ ช่องทาง "เราออกภาษีแทน") ของเส้น batch: บรรทัดที่ระบุ VAT ต่อรายการถูกคิด WHT **ทีละบรรทัด** ใน `SettlementBatchMath.BuildFeeLines` ⇒ ยอดบน 50 ทวิ อาจต่างจากคิดรวมทั้งใบเป็นสตางค์ | ไฟล์ทีม T · เส้นเดิมบล็อก WHT ทั้งหมดอยู่แล้ว (ไม่มีตัวเทียบ) | ให้ `BuildFeeLines` รวมฐานก่อน VAT ของทั้งกลุ่มก่อนคิด WHT ครั้งเดียว (VAT ยังต่อรายการ) + เทสต์ |
| B-7 | ผังค่าธรรมเนียม: เส้นเดิมลง `PaymentProviderConfig.FeeExpenseAccountId`/54710 · เส้น batch ลง 53170 (`PaymentFee` role) | ไม่ใช่ตัวเลขผิด (D6: 53170 สำหรับเส้นใหม่ ไม่ย้ายย้อนหลัง) แต่ค่าธรรมเนียม provider เดียวกระจายสองผังระหว่างช่วงเปลี่ยนผ่าน | ให้ `SettlementChannelService` เติม `FeeAccountMapJson["payment_fee"]` จาก config เมื่อผูกครั้งแรก (ถ้าเจ้าของต้องการ) |
| B-8 | `GatewaySettlementService.ListPendingAsync` ตัวนับ "คืนก่อนระบบเก็บยอด" (`:158`) ไม่กรอง `SettlementBatchId` (R-B16 ค้าง · ไฟล์ทีม G) | แสดงผลอย่างเดียว | เพิ่ม `&& i.SettlementBatchId == null` |

## 5. ค่าที่ persist ไว้ก่อนแก้ (F3 ข้อ 10) — ไม่มี migration อัตโนมัติ (JE ในงวดปิดห้ามแก้เงียบ · สอดคล้อง DECISIONS ข้อ 20)

- รอบโอนที่ประกอบจาก intent **ยังไม่ลงบัญชี**: โหมด "บวก VAT" ⇒ ไม่ลงตัว (ถูกบล็อกอยู่แล้ว) — ทางไปต่อ: ยกเลิกรอบแล้วประกอบใหม่ (ได้บรรทัดสูตรใหม่) · ~~โหมดอื่นลงได้ตามเดิม (VAT อาจต่างเป็นสตางค์)~~ **แก้ถ้อยคำ (ฝ่ายค้าน X-1 · ทีม SF):** ผิดสำหรับคู่ค่าเริ่มต้น config "ไม่แยก VAT" ↔ ช่องทาง "VAT ไทย 7%" — ต่าง**เต็ม 7/107** ของค่าธรรมเนียม ไม่ใช่สตางค์ (ภาษีซื้อแต่งขึ้น ≈ 6.54%) · ตั้งแต่ทีม SF ด่านลงบัญชี (`BuildGateAsync` → `GatewayBatchIntentRules.PostingIssue`) บล็อกรอบโอนที่โหมดสองที่ให้ผลภาษีต่างกัน (`GatewayModeMismatch`) ⇒ รอบที่ค้างก่อน deploy ต้องแก้โหมดให้ตรงแล้วยกเลิก/ประกอบใหม่ — SQL ด้านล่างยังใช้คัดกรองรอบที่**ลงไปแล้ว**
- รอบโอนที่**ลงบัญชีแล้ว**ขณะโหมดขัดกัน (ภาษีซื้อ 11630 อาจแต่งขึ้น): รายงานให้นักบัญชีตรวจ — query อ่านอย่างเดียว (โหมด ณ วันนี้ ไม่ใช่ ณ วันนำเข้า — ใช้คัดกรองเท่านั้น):

```sql
SELECT b."CompanyId", b."Id", b."PayoutRef", b."PayoutDate", b."Status", ch."DisplayName",
       ch."FeeVatMode" AS channel_vat /* 1 ThaiVat7 · 2 ForeignPp36 · 3 None */,
       cfg."FeeVatMode" AS gateway_vat /* 0 None · 1 IncludedInFee · 2 AddedOnTop */
FROM "SettlementBatches" b
JOIN "SettlementChannels" ch ON ch."Id" = b."ChannelId" AND ch."CompanyId" = b."CompanyId"
JOIN "PaymentProviderConfigs" cfg ON cfg."Id" = ch."PaymentProviderConfigId" AND cfg."CompanyId" = ch."CompanyId"
WHERE b."IsDeleted" = false AND b."SourceKind" = 2 /* PaymentIntents */
  AND ((cfg."FeeVatMode" = 0 AND ch."FeeVatMode" = 1) OR (cfg."FeeVatMode" <> 0 AND ch."FeeVatMode" <> 1));
```
  VAT ที่ค้าง 11630 เกิน 6 เดือนมีคำเตือนอยู่แล้ว (DECISIONS ข้อ 8) — นักบัญชีตัดสินและลงรายการเอง

## 6. ตอบ F3 ข้อ 7–12

7. `callers.py PaymentIntentAdapter` — ผู้เรียก `BuildRows` จริง 1 จุด (+เทสต์ 1 จุดแก้แล้ว) · เงื่อนไข "ยังไม่มีเจ้าของ" แบบเขียนมือในเส้นใหม่เหลือ **0** (checker ห้าม) · เส้นเดิมยังเขียนมือ 1 จุด (ไฟล์ทีม G — ล็อกให้มี `SettlementBatchId == null`)
8. ทางเข้าที่แตะข้อเท็จจริงเดียวกัน: ประกอบรอบโอน ✅ · บันทึกช่องทาง ✅ · บันทึก config gateway ❌ (B-3 ทีม G — ผลคือบล็อกครั้งถัดไป มองเห็น) · นำเข้า CSV ของช่องทาง gateway: ค่าธรรมเนียมมาจากไฟล์ (ไฟล์คือความจริง) แต่โหมดของช่องทางถูกบังคับให้ตรง config ตั้งแต่ตอนบันทึกช่องทาง
9. เข้มขึ้น 4 จุด — ทุกจุดมีทางไปต่อในข้อความ + เทสต์ทิศตรงข้าม: โหมดขัดกัน (แก้สองที่ให้ตรง · Theory 5 คู่ผ่าน) · ยอดคืนแยกไม่ได้ (แก้วันที่/เติมยอดรายครั้ง · ทิศ Known) · ตาข่ายยอดคืน (กดใหม่ · ทิศไม่เกิน) · ต้นช่วง (เลื่อนวัน · คำเตือนจำนวน · ทิศไม่ระบุช่วง)
10. §5 ข้างบน — ไม่มี migration (ตั้งใจ) + query อ่านอย่างเดียว
11. ฝ่ายค้าน: ยังไม่ได้ส่ง diff ให้ subagent ฝ่ายค้าน (ทีมนี้เป็น subagent เอง ไม่มีเครื่องมือเรียกทีมอื่น) — **ขอ main agent ส่งรอบฝ่ายค้าน** ด้วย 3 คำถาม (ทางเข้าอื่น? ทิศตรงข้าม? สถานะปลายทางประทับเองไหม? — รอบนี้ไม่ประทับสถานะใหม่)
12. DOCUMENT_FLOW §2.10 ✅ · ACCOUNT_STRUCTURE §3.1d ✅ · TEST_PLAN §0 + SPP2 ✅ · CHANGELOG ✅

## 7. ความเสี่ยงคอมไพล์ (ไม่มี SDK)

- `GatewayBatchIntentRules.UnclaimedForBatch/LateRefundInBatch` คืน `Expression<Func<PaymentIntent,bool>>` ที่จับตัวแปร `DateTime?` (`fromUtc == null || i.ConfirmedAt >= fromUtc`) — EF แปลได้ (รูปเดียวกับ `to == null ||` ของเดิม) · เทสต์เรียก `.Compile()`
- projection เข้า private record `GatewayIntentRow(...)` ใน `Select` สุดท้าย (EF รองรับ constructor projection) · `ToDictionaryAsync(key, element, ct)` (มีใช้ในเรพแล้ว)
- `RefundLinesOverRefunded` ใช้ `out var` ใน lambda ธรรมดา (ไม่ใช่ expression tree) · `const decimal RefundTolerance = GatewaySettlementMath.ToleranceBaht` (const → const)
- อาร์กิวเมนต์ `(decimal?)null : -part.FeeVat` เขียนชนิดชัด · tuple array `new[] { (a, -50m) }` → `IEnumerable<(Guid IntentId, decimal Amount)>` (identity conversion)
- ย้ายเมธอดข้ามไฟล์ partial — ถ้าทีมอื่นแก้ `ImportFromPaymentIntentsAsync`/`LoadIntentRowsAsync` ใน `SettlementImportService.cs` พร้อมกันจะ conflict (ต้องย้ายการแก้ของเขามาไฟล์ `.Gateway.cs`)

## 8. คำถามค้าง (เลือกทิศที่มองเห็น/ย้อนได้ไว้แล้ว)

- Q1 config "ไม่หัก WHT" + ช่องทาง "ตัวแทนหัก"/"หักแล้วได้คืน" — อนุญาต (config ไม่มีคำให้เลือกสองแบบนี้ · เส้นเดิมไม่ลง WHT อยู่แล้ว) · ถ้าเจ้าของเห็นว่าต้องตรงเป๊ะ แก้ `ModeMismatch` บรรทัดเดียว
- Q2 ควรปิดเส้นเดิมต่อ provider เมื่อมีช่องทาง batch (B-2) หรือคงสองเส้นถาวร — ข้อเสนอ: ปิด intent ใหม่ คงคืนเงินภายหลัง
- Q3 ผังค่าธรรมเนียมช่วงเปลี่ยนผ่าน (B-7) — ใช้ผังของ config ต่อหรือ 53170 ตาม D6
- Q4 gateway ต่างประเทศ (config "ไม่แยก VAT" ↔ ช่องทาง "ต่างประเทศ ภ.พ.36" — อนุญาต): เส้น batch ประเมิน ภ.พ.36 บนค่าธรรมเนียม (§83/6) ส่วนเส้นเดิมไม่มีคำให้เลือกและลงค่าใช้จ่ายทั้งก้อน —
  ยอดที่ถูกหัก/ผังพักเท่ากัน ต่างเฉพาะภาษีที่เส้นเดิมไม่รู้จัก (เส้น batch ถูกกว่า) · intent อยู่ได้เส้นเดียวจึงไม่นับซ้ำ · ถ้าจะให้เส้นเดิมรู้ต้องเพิ่มค่าใน `GatewayFeeVatMode` (ทีม G)
