# JE_POSTING_AUDIT — ความถูกต้องของรายการบัญชี (JE) ที่ระบบสร้างอัตโนมัติทุกเส้น (ทีม V · 2026-10-26)

> คำสั่งผู้ใช้: "ตั้งทีมวิเคราะห์อีกว่ามีส่วนไหนที่ลงบันทึก JE ไม่ถูกต้องอีกไหม" หลังพบเคส **PV-202610-0040** — ใบกำกับ 536
> (ฐาน 500.93 + VAT 35.07) มีส่วนลดหลัง VAT 98 ⇒ จ่ายจริง 438 แต่ระบบ**บีบฐาน**เป็น 402.93 (438 − 35.07) ⇒ ค่าใช้จ่าย/ฐานภาษีซื้อผิด
> (เคสนั้นทีม U แก้ — ทีมนี้ไล่ **คลาสเดียวกัน** = จุดที่ระบบ "บีบ/ยัด/เดา" ยอดเพื่อให้ JE สมดุล แทนที่จะล้มดังหรือลงขาที่อธิบายได้)
>
> ตรวจกับโค้ด ณ `492e8b74` (branch `claude/booking-service-features-sdemob`) · ไม่มี .NET SDK ⇒ **ยังไม่ได้คอมไพล์** ·
> **ยังไม่ผ่านฝ่ายค้าน** · ไม่รายงานซ้ำข้อที่อยู่ใน §"ตรวจแล้วไม่ใช่บั๊ก" ของ `docs/REVIEWS_INDEX.md`
>
> สถานะ: ✅ ถูก · ❌ ผิด → 🔧 แก้รอบนี้ · ⚠ backlog (ถูกเชิงยอดแต่มีความเสี่ยง/ต้องตามต่อ) · ❓ ต้องให้เจ้าของตัดสิน · 👉U = ต้นทางอยู่ในขอบเขตทีม U (ไม่แตะ)
> บรรทัดอ้าง `DocumentService.cs` เป็นเลขก่อนแก้รอบนี้ (หลังบรรทัด 17912 เลื่อน +7)

## 0. สรุป 30 วินาที

| | จำนวน |
| --- | --- |
| เคสที่ตรวจ | **52** |
| ✅ ถูก (มีเทสต์/อ่านโค้ดยืนยัน) | 43 |
| ❌ ผิด → 🔧 แก้แล้วในคอมมิตนี้ | **2** (V-DN-01 · V-CN-03) |
| ⚠ backlog | 5 (V-SL-07 · V-SL-10 · V-PU-08 · V-PAY-03 · V-PAY-06) |
| ❓ ให้เจ้าของตัดสิน | 2 (V-PAY-05 · V-CN-04) |

**5 ข้อสำคัญสุด**
1. **V-DN-01 ❌🔧** ใบเพิ่มหนี้ของใบที่ชำระครบแล้ว: AutoPost ยืมกติกาใบลดหนี้ ("ใบเดิมชำระครบ ⇒ ขาเงินสด") ⇒ **Dr เงินสดตอนอนุมัติทั้งที่ยังไม่ได้รับเงิน**
   แล้วใบเพิ่มหนี้อยู่ใน `PayableDocumentTypes` (ปุ่มรับเงิน `documents.html:11233`) ⇒ ตอนรับชำระ `CreatePaymentJournalAsync` ลง Dr เงินสด / Cr ลูกหนี้อีกรอบ
   ⇒ **เงินสดเกินจริงสองเท่า + ลูกหนี้ติดลบ โดยงบยังสมดุล** (ฝั่งซื้อสมมาตร: เจ้าหนี้ติดเดบิต) — ตัวตัดสินเดียว `AdjustmentNoteAccount.CounterLegIsMoney`
2. **V-CN-03 ❌🔧** ขาเงินของ CN/DN โหมดคืนเงินลง `"111"` ตายตัว ไม่ดูช่องทางเงินของเอกสาร (ธนาคาร/ผังที่เลือก) ⇒ คืนเงินผ่านธนาคารไปโผล่เงินสดในมือ — เดิน `UseMoneyAccount()` เหมือนทุกใบ
3. **V-PAY-05 ❓** ใบแจ้งหนี้บริการที่พัก VAT 21913: รับชำระ**บางส่วน**งวดแรก ⇒ ย้าย 21913→21911 **ทั้งก้อน** (ไม่ใช่สัดส่วน) ⇒ ภ.พ.30 เดือนแรกสูงกว่าเงินที่รับ (§78/1 tax point เกิดตามส่วนที่รับ) — ทิศ "จ่ายภาษีก่อน" ไม่ขาด แต่ไม่ตรงหลัก · เป็นพฤติกรรมที่จดไว้แล้ว (M-6) ⇒ ต้องตัดสิน
4. **V-CN-04 ❓** ใบลดหนี้ของใบที่ชำระครบ = ระบบถือว่า "คืนเงินสดทันที" ตอนอนุมัติ แม้ธุรกิจจริงมักให้ลูกค้าถือเครดิตไปหักใบถัดไป ⇒ GL เงินสดลดทั้งที่เงินยังไม่ออก · ไม่มีสถานะ "เครดิตค้างคืน" (217xx) ให้เลือก
5. **V-PAY-02 👉U** ต้นทางคลาส "บีบฐาน = ยอดจ่าย − VAT" อยู่ที่ขั้น**สกัด/ฟอร์ม** ไม่ใช่ขั้น JE: `OcrService.cs:6407 data.SubTotal = data.TotalAmount - data.VatAmount` ·
   `Ocr/SmartFieldExtractor.cs:587 data.SubTotal = total − vat` — ถ้า TotalAmount ที่สกัดได้คือ "ยอดชำระ" (หลังส่วนลดท้ายบิล) ฐานจะถูกบีบตั้งแต่ก่อนถึง AutoPost ·
   ชั้น JE เอง (`PaymentSettlementAdjustment`) ปฏิเสธส่วนต่างที่ไม่มีบรรทัดปรับอยู่แล้ว (เทสต์ `JePostingAuditTests` + `PaymentSettlementAdjustmentTests`)

**ข้อสังเกตเชิงคลาส ("บีบยอดให้สมดุล") ที่พบทั้งเรพ** — มี 3 จุดที่ระบบดูดผลต่างเข้าบรรทัดอื่นแทนล้มดัง และทุกจุดมีเพดาน/ทิศปลอดภัย:
(a) FX squeeze `DocumentService.cs:19297-19323` — เฉพาะเอกสารต่างสกุล · เพดาน 0.01×n+0.01 บาท · เศษจากการแปลงรายบรรทัดจริง ✅ ·
(b) CIL เศษ VAT หัวใบ `:18229-18234` — ลงบรรทัดแรก · VAT เคลมไม่ได้เป็นต้นทุนอยู่แล้ว · เศษติดลบ ⇒ ไม่สมดุล ⇒ throw ⚠ (V-PU-08) ·
(c) POS รายได้ = สุทธิ − VAT − ทิป `PosService.Orders.cs:1753` — ค่าบริการ + เศษปัดบิลเข้ารายได้ขาย (ตั้งใจ · VAT คิดบนฐานรวมค่าบริการแล้ว) ✅ (V-POS-01)
ที่เหลือทุกเส้นเมื่อ Dr ≠ Cr = **throw** (`:19325` · `CreatePaymentJournalAsync:20018` · POS `CreateJournalEntryAsync` · Integration `:1741/:4305` · ค่าเสื่อม `:1108` · recurring `:963`)

## 1. Matrix

### A. เอกสารขาย/ซื้ออนุมัติ (`DocumentService.AutoPostToJournalAsync` :17444–19495)

| ID | เส้น · เคส | JE ที่คาด | JE ที่ระบบทำ | ผล | หลักฐาน | สถานะ |
| --- | --- | --- | --- | --- | --- | --- |
| V-SL-01 | ใบแจ้งหนี้/ใบกำกับ เครดิต VAT 7 | Dr 113 ยอดรวม · Cr รายได้รายบรรทัด · Cr 21911 | เท่าที่คาด (Cash basis: AR gross = Total+WHT) | ✅ | `:17599-17672` | — |
| V-SL-02 | ใบแจ้งหนี้บริการล้วน (ไม่มีสินค้า TrackStock) | Cr 21913 รอเรียกเก็บ → reclass เมื่อรับเงิน | 21913 ตาม `InvoiceHasTrackedGoodsAsync` ตัวเดียวกับคำเตือน §86 | ✅ | `:17645-17672` · `:16803` | ดู V-PAY-05 |
| V-SL-03 | VAT 0 / ยกเว้น | ไม่มีขา VAT | `if (doc.VatAmount > 0)` | ✅ | `:17645` | — |
| V-SL-04 | หลายอัตราในใบเดียว | ขา VAT = Σ VAT รายบรรทัด | ขาเดียว `doc.VatAmount` (= Σ บรรทัดตาม `DocumentLineVatConvention`) · ไม่ตรง ⇒ `ExplainImbalance` throw | ✅ | `:19325-19330` · `DocumentLineVatConventionTests` | — |
| V-SL-05 | ราคารวม VAT | บรรทัด Amount = ฐานถอด VAT | ถอดตอนบันทึก · AutoPost อ่าน Amount/VatAmount บรรทัด | ✅ | `DocumentLineVatConventionTests` · `OcrVatBackCalcTests` | — |
| V-SL-06 | ส่วนลดต่อบรรทัด | Amount หักส่วนลดแล้ว | เหมือนกัน | ✅ | `DocumentLineKind`/`OcrLineDiscountFixTests` | — |
| V-SL-07 | ส่วนลดท้ายบิลก่อน VAT (`Document.DiscountAmount`) | ขารายได้/ค่าใช้จ่าย = ฐานหลังส่วนลด | AutoPost **ไม่อ่าน `DiscountAmount` เลย** (0 จุดใน 17444–19500) ⇒ ถูกเฉพาะเมื่อตอนบันทึกเฉลี่ยส่วนลดลงบรรทัดแล้ว (SubTotal = Σ บรรทัด + RoundingAdjustment) · ถ้าทางเข้าใด (CSV/API/recurring) ตั้ง SubTotal หักส่วนลดแต่บรรทัดยังเต็ม ⇒ ไม่สมดุล ⇒ throw (ล้มดัง ไม่บีบ) | ⚠ | `:1776` `:3265` (สัญญา SubTotal) | backlog: เทสต์ทางเข้า CSV/API ว่าเฉลี่ยลงบรรทัดจริง |
| V-SL-08 | ผลต่างปัดเศษ (`RoundingAdjustment`) | ขาผังผลต่างปัดเศษในทิศที่ปิดสมดุล · ต่างไม่เท่าที่ประกาศ ⇒ ไม่ลง | `DocumentRounding.JournalLine` | ✅ | `:19274-19291` · `DocumentRoundingTests` | — |
| V-SL-09 | WHT ฝั่งขาย (Accrual/Cash) | Accrual: Dr 11910 ตอนออกใบ · Cash: เลื่อนไปรับชำระ | ตาม `WhtRecognitionBasis` | ✅ | `:17611-17624` · `:18463-18491` | — |
| V-SL-10 | COGS perpetual | Dr 51110 / Cr 11500 = EffectiveUnitCost | เท่าที่คาด · **ไม่มีผัง 511/115 ⇒ แค่ `LogWarning`** (ใบเสร็จมี `AppendInternalNote` แต่ใบแจ้งหนี้ไม่มี) ⇒ สต็อกตัดแต่ GL 11500 ไม่ลด เงียบ | ⚠ | `:17680-17701` vs `:18557-18563` | backlog: หมายเหตุบนเอกสาร/ล้มดังเหมือนเส้นใบเสร็จ |
| V-SL-11 | ใบต่างสกุล | Conv ต่อบรรทัด AwayFromZero · เศษแปลง ≤ 0.01×n+0.01 ดูดเข้าบรรทัดใหญ่สุด **เฉพาะ FX** · เกิน ⇒ throw | เท่าที่คาด (THB ไม่ squeeze) | ✅ | `:19297-19323` · `JournalPostingGuard:116` | — |
| V-PU-01 | PI/Expense VAT เคลมได้ ใบครบ §86/4 | Dr ค่าใช้จ่าย/สินค้า · Dr 11610 · Cr 21210/21220 (ตามชนิด) | เท่าที่คาด · ผังหมวดที่เลือกใช้ไม่ได้ ⇒ ล้มดัง | ✅ | `:18283-18368` | — |
| V-PU-02 | ใบกำกับยังไม่ครบ §86/4 | Dr 11640 + ล้างงวดเคลม · ย้าย 11640→11610 เมื่อครบ | `ResolveInputVatAccountAsync` · `InputVatPostedAsUndue` · `BecameClaimableAt=null` | ✅ | `:18309-18339` · `InputVatAccountPolicyTests` | — |
| V-PU-03 | ภาษีซื้อต้องห้าม §82/5 ต่อบรรทัด | VAT รวมเป็นต้นทุนบรรทัดนั้น · ไม่เข้า 116 | PI/Expense · GRN · PV standalone · CN/DN ฝั่งซื้อ ทำครบ 4 เส้น | ✅ | `:18296-18302` `:18273-18279` `:19084-19086` `:18019-18021` | — |
| V-PU-04 | WHT ฝั่งซื้อ | Accrual: Cr 2191x (แยกผู้ถูกหัก/อัตรา) · Cash: ที่ PV/การจ่าย | `ResolveWhtPayableSplitAsync` (`WhtPayeeSplit.Allocate` Σ = ยอด) | ✅ | `:18378-18406` · `WhtPayeeSplitTests` | — |
| V-PU-05 | ผู้จ่ายออกภาษีแทน | Dr ค่าใช้จ่ายส่วนที่ออกแทนรายบรรทัด · หัวใบไม่ตรง ⇒ ล้มดัง | `WhtGrossUp.ForLines/MismatchReason` | ✅ | `:17576-17594` · `WhtGrossUpTests` | — |
| V-PU-06 | บริการต่างประเทศ ภ.พ.36 | เจ้าหนี้ = ฐาน · Cr 21912 · Dr 11640 | `ForeignServiceVat.SplitCredit` ตัวเดียวกับพรีวิว/ด่าน | ✅ | `:18318-18374` · `ForeignServiceVatTests` | — |
| V-PU-07 | PI อ้าง GRN (3-way) | Dr GR-NI (SubTotal) แทนค่าใช้จ่าย · VAT ต้องห้ามลงต้นทุน | เท่าที่คาด | ✅ | `:18259-18280` | — |
| V-PU-08 | ใบรับรองแทนใบเสร็จ (CIL) | VAT รวมต้นทุนรายบรรทัด · Cr เงิน | เท่าที่คาด · **เศษ `doc.VatAmount − Σ VAT บรรทัด` ถูกยัดบรรทัดแรก** (คลาส "บีบ" แต่ทิศปลอดภัย: VAT เคลมไม่ได้ = ต้นทุนอยู่แล้ว) · เศษติดลบ ⇒ ไม่สมดุล ⇒ throw | ⚠ | `:18229-18234` | backlog: เตือนบนเอกสารเมื่อหัวใบ ≠ Σ บรรทัด แทนดูดเงียบ |
| V-PU-09 | ค่าส่ง/คูปองแพลตฟอร์ม (ทีม K) ที่ไม่อยู่ในใบกำกับ | ส่วนต่างที่ขั้นชำระ: Dr 51120 / Cr 51150 · ฐาน/VAT ใบไม่แตะ | `PaymentSettlementAdjustment` (ชั้นชำระ + ชั้น PV "ยอดชำระจริง") | ✅ | `:14413-14457` `:19220-19269` `:19986-19992` · `PaymentSettlementAdjustmentTests` | — |
| V-PV-01 | PV standalone เงินเชื่อ (legacy) | Cr 21220 แทนเงิน | เท่าที่คาด (ด่านบันทึกกันเคสใหม่แล้ว) | ✅ | `:19134-19146` | — |

### B. ชำระเงิน (`CreatePaymentAsync` :14350–14800 · `CreatePaymentJournalAsync` :19818–20073 · allocation :15040–15210)

| ID | เส้น · เคส | JE ที่คาด | JE ที่ระบบทำ | ผล | หลักฐาน | สถานะ |
| --- | --- | --- | --- | --- | --- | --- |
| V-PAY-01 | จ่ายเต็ม/บางส่วน/หลายงวด | Dr เงิน งวด · Cr AR = เงิน (+WHT งวด Cash basis) · WHT งวดสุดท้ายรับเศษ | `SuggestPaymentWhtAsync(isFinal)` · `arClear = thbAmount + thbWht + thbFee` | ✅ | `:14487-14516` `:19916-19957` · `WhtPaymentSuggestionTests` | — |
| V-PAY-02 | ยอดชำระ ≠ ยอดใบ (ส่วนลดหลัง VAT · คูปอง · ค่าส่ง) — **คลาส PV-202610-0040** | ห้ามแก้ฐาน/VAT ใบ · ส่วนต่างต้องมีบรรทัดปรับ มิฉะนั้นปฏิเสธ | ชั้นชำระ+ชั้น PV ทำถูก (`CheckDocumentLines` ล้มดัง) · **ต้นทางที่บีบฐานอยู่ที่ขั้นสกัด**: `OcrService.cs:6407` / `SmartFieldExtractor.cs:587` `SubTotal = Total − Vat` | ✅ (JE) · 👉U (ต้นทาง) | `JePostingAuditTests.ใบกำกับ536_ส่วนลดหลังVAT98…` · `PaymentSettlementAdjustmentTests` | ทีม U |
| V-PAY-03 | ค่าธรรมเนียมธนาคาร/gateway หักจากยอดโอน | ฝั่งรับ: Dr 54710/ผังที่เลือก · Cr AR รวม fee · ฝั่งจ่าย: ค่าใช้จ่ายแยก | ฝั่งรับถูก (ไม่มีผัง ⇒ ล้มดัง) · **ฝั่งจ่ายปฏิเสธช่อง fee** ให้บันทึกแยก (ไม่มีทางลัด) | ✅ / ⚠ UX | `:14403-14412` `:19928-19957` | backlog UX: ให้ฝั่งจ่ายใช้บรรทัดปรับ 54710 ได้ |
| V-PAY-04 | ปัดเศษ/จ่ายเกิน | ไม่สมดุล ⇒ throw · จ่ายเกิน ⇒ ปฏิเสธเกณฑ์เดียวทุกทางเข้า | ไม่มี squeeze ในเส้นชำระ · `DocumentSettlementState.WouldOverpay` | ✅ | `:20016-20020` `:14462-14465` · `DocumentSettlementStateTests` | — |
| V-PAY-05 | ใบแจ้งหนี้บริการ (21913) รับชำระบางส่วน | §78/1: tax point เกิดตามส่วนที่รับ ⇒ ย้าย 21913→21911 **ตามสัดส่วน** | ย้าย **ทั้งก้อน** `net21913` ตอนงวดแรก (จดไว้เป็นพฤติกรรม M-6) ⇒ ภ.พ.30 เดือนแรกสูงกว่าที่รับจริง · void การชำระกลับได้ | ❓ | `:16830-16840` `:11499-11503` | ให้เจ้าของตัดสิน: ทั้งก้อน (ง่าย/จ่ายก่อน) vs สัดส่วน (ตรง §78/1 · ต้องตาม CN/DN ลูก) |
| V-PAY-06 | ใบเสร็จแปลงจากใบแจ้งหนี้ (settle AR) | Dr เงิน · (Cash basis) Dr 11910 · Cr AR gross ที่ rate ต้นทาง · FX realized | เท่าที่คาด · คอมเมนต์บอก "ดึง WHT จากต้นทาง" แต่โค้ดใช้ `doc.WithholdingTaxAmount` ของใบเสร็จ (สืบทอดจากแปลง) | ✅ / ⚠ doc drift | `:18443-18525` | backlog: แก้คอมเมนต์ หรือ assert เท่าต้นทาง |
| V-PAY-07 | PV แปลงจากใบตั้งหนี้ | Dr AP (ยอดจ่ายผู้รับ + WHT Cash basis) · Cr เงิน · Cr 2191x · ภ.พ.36 ไม่ตั้ง 21912 ซ้ำ | เท่าที่คาด | ✅ | `:18981-19073` · `JournalPostingGuard JE-PP36-DOUBLE` | — |
| V-PAY-08 | เงินก้อนเดียวหลายใบ (allocation) | JE ต่อเอกสาร · ส่วนเกินที่ยังไม่จัดสรร → 21710/11470 (ไม่มีผัง ⇒ ล้มดัง) | เท่าที่คาด | ✅ | `:15137-15210` | — |
| V-PAY-09 | รับ/จ่ายต่างสกุล rate วันชำระ | AR/AP ตัด rate เอกสาร · เงินตาม rate ชำระ · ผลต่าง 42600/54950 | เท่าที่คาด (ไม่มีผัง ⇒ throw) | ✅ | `:19902-19914` `:19958-20011` | — |
| V-PAY-10 | มัดจำ: รับ/หัก/คืน (ที่พัก · CMS · MTO) | หักมัดจำ: Dr เงินสุทธิ + Dr 217xx/21913-21911 ตาม GL จริงของใบมัดจำ (หลายใบรวม = DepositAppliedAmount) · ไม่ตรง ⇒ ไม่สมดุล ⇒ degrade ตั้งหนี้ | เท่าที่คาด · ที่พัก: VAT คิดรายใบ ⇒ `roundingDelta` แค่หมายเหตุ (ส่วนต่างอยู่ที่ VAT ไม่ใช่รายได้) | ✅ | `:18528-18915` · `DepositReversalMathTests` · `LodgingDepositSettlementTests` · `BookingDepositDrawdownTests` · `SiteOrderDepositInvoicingTests` | — |
| V-PAY-11 | เช็ค/PDC/เช็คเด้ง | กลับขาเงิน + คืนลูกหนี้ | มีเทสต์แล้ว | ✅ | `ChequeBounceAndPettyCashTests` | ไม่ตรวจซ้ำ |
| V-PAY-12 | Settlement แพลตฟอร์ม (Shopee/Lazada) + gateway | ค่าธรรมเนียม/VAT ค่าธรรมเนียม/WHT/voucher ตามแผนตัวเดียว · เศษ VAT ล้างด้วย JE แยก | `SettlementBatchMath.Plan` (ToleranceBaht) · `GatewaySettlementMath` · เศษ VAT `GatewaySettlementService:751-755` | ✅ | `SettlementBatchMathTests` · `SettlementPostingTests` · `GatewaySettlementMathTests` | ไม่ตรวจซ้ำ |

### C. ยกเลิก / กลับรายการ / ใบลดหนี้-เพิ่มหนี้

| ID | เส้น · เคส | JE ที่คาด | JE ที่ระบบทำ | ผล | หลักฐาน | สถานะ |
| --- | --- | --- | --- | --- | --- | --- |
| V-VD-01 | ยกเลิกเอกสาร | กลับ JE ต้นฉบับทุกใบ (ไม่กลับตัวกลับซ้ำ) + JV มัดจำที่ชี้มา · ตัวกลับ = บรรทัดที่บันทึกจริง | `DepositApplyJournals.LiveOfSource` · `ReversalMismatch` guard · วันตัวกลับต่อใบ | ✅ | `:9723-9835` · `AccountingService:1040-1090` · `VoidReversalDateTests` | — |
| V-VD-02 | ยกเลิกหลังชำระบางส่วน | กลับ JE การชำระก่อน · เงินก้อนร่วมใบอื่น ⇒ บล็อก | `SharedPaymentVoidBlock` · cascade payments (รวม allocation) | ✅ | `:9617-9700` | — |
| V-VD-03 | ยกเลิกการชำระของใบที่ย้าย 21913→21911 แล้ว | กลับ 21911→21913 เมื่อไม่เหลือการชำระ | `UndoOutputVatOnPaymentVoidAsync` | ✅ | `:11495-11520` | — |
| V-CN-01 | CN/DN: บัญชี VAT ที่กลับ | กลับที่บัญชีเดียวกับที่ใบเดิมพัก (21913/11640/21912) | GL-driven (`src21913Net`) · `InputVatPostedAsUndue` | ✅ | `:18054-18104` | — |
| V-CN-02 | CN/DN + WHT เกณฑ์เงินสด โหมดลูกหนี้ | counter gross · ไม่ลงขา WHT (mirror ใบเดิม) | `cnDnGrossWht` | ✅ | `:17961-17967` `:18108` | — |
| **V-CN-03** | CN/DN โหมดคืนเงิน: ผังขาเงิน | ช่องทางเงินของเอกสาร (ธนาคาร/ผังที่เลือก → 111) เหมือนทุกใบ | **เดิม `FindAccountAsync("111")` ตายตัว** ⇒ คืนผ่านธนาคารไปลงเงินสดในมือ | ❌ → 🔧 | `:17933-17949` | แก้: `UseMoneyAccount()` (ไม่มีช่องทาง = 111 เท่าเดิม) |
| **V-DN-01** | ใบเพิ่มหนี้ของใบที่**ชำระครบแล้ว** (เทอมเครดิต) | Dr ลูกหนี้ (DN = หนี้ก้อนใหม่ · รับชำระทีหลัง) | **เดิม Dr เงินสด** (ยืม `isCashSettlement = source.BalanceDue ≤ 0.01` ของใบลดหนี้) + DN ∈ `PayableDocumentTypes` ⇒ รับชำระแล้ว Dr เงินสด/Cr ลูกหนี้อีกรอบ ⇒ **เงินสด ×2 · ลูกหนี้ติดลบ** (ฝั่งซื้อ: เจ้าหนี้ติดเดบิต) | ❌ → 🔧 | `:17912` · `:11966-11979` (DN ไม่แตะใบเดิม) · `:14039-14043` · `documents.html:11233` | แก้: `AdjustmentNoteAccount.CounterLegIsMoney` (DN ดูเทอมของตัวเอง · CN กติกาเดิม) · เทสต์ `JePostingAuditTests` 8 ข้อ · **data-integrity (อ่านอย่างเดียว — ไม่แก้ข้อมูล)**: DN `Approved/Paid` ที่ `PaymentType ≠ Cash` และ JE อนุมัติมีขา Dr 111xx ก่อนคอมมิตนี้ ⇒ เงินสดเกินเท่ายอด DN — SQL เสนอด้านล่าง §3 |
| V-CN-04 | ใบลดหนี้ของใบที่ชำระครบ | ธุรกิจจริงมี 2 แบบ: คืนเงิน / ลูกค้าถือเครดิตหักใบหน้า | ระบบถือ "คืนเงินทันที" ตอนอนุมัติเสมอ (Cr เงิน) · ไม่มีสถานะเครดิตค้างคืน | ❓ | `:17912` `:11948-11964` | ให้เจ้าของตัดสิน: เพิ่มตัวเลือก "เครดิตค้างคืน" (Cr 217xx) หรือรับได้ |
| V-CN-05 | CN เกินยอดใบเดิม §86/10 | บล็อก | `cnSiblingSum` cap | ✅ | `:11932-11944` | — |

### D. เส้นอื่น

| ID | เส้น · เคส | JE ที่คาด | JE ที่ระบบทำ | ผล | หลักฐาน | สถานะ |
| --- | --- | --- | --- | --- | --- | --- |
| V-POS-01 | POS ขาย (เงินสด/โอน/บัตร · ค่าบริการ · ปัดเศษ · ทิป) | Dr เงินตามวิธีจ่าย · Cr รายได้ · Cr 21911 (ฐานรวมค่าบริการ) · Cr ทิปค้างจ่าย · COGS | รายได้ = Net − VAT − Tip (รวมค่าบริการ + เศษปัดบิลเข้า 41000 — ตั้งใจ) · ไม่มีผัง ⇒ ล้มดัง | ✅ | `PosService.Orders.cs:1690-1843` `:2300-2316` · `JePostingAuditTests.POS_*` · `PosTipLiabilityTests` | หมายเหตุ: ค่าบริการไม่แยกผัง (design) |
| V-POS-02 | คืน/ยกเลิก/แยกบิล/รวมบิล | กลับตามสัดส่วนของขาจริง · ยอดรวมหลังแยกเท่าเดิม | มีเทสต์แล้ว | ✅ | `PosRefundMathTests` · `PosVoidSaleJournalTests` · `PosSplitBillTotalsTests` | ไม่ตรวจซ้ำ |
| V-POS-03 | รายการพิเศษเลือกผังเอง (ทีม K) | แบ่งเครดิตตามสัดส่วน Σ = ยอด | `PosRevenueSplit.Split` | ✅ | `PosRevenueSplitTests` | — |
| V-CC-01 | คอร์ส/เครดิต (ทีม L) | ขาย: Cr 21713 + VAT (ผ่าน DocumentService) · ใช้: Dr 21713 / Cr รายได้ · หมดอายุ: breakage | `JournalEntryBuilder` + ด่านงวดปิด | ✅ | `CourseCreditService.cs:838-862` · `CourseCreditMathTests` · `CreditPassDocumentRuleTests` | — |
| V-DEP-01 | ค่าเสื่อมรายเดือน | Dr ค่าเสื่อม / Cr ค่าเสื่อมสะสม · งวดปิดห้าม · เดือนสุดท้ายรับเศษให้ NBV = ซาก | `DepreciationSchedule.AmountForPeriod` | ✅ | `FixedAssetService.cs:1007-1110` · `DepreciationScheduleTests` | — |
| V-YE-01 | ปิดปี | ล้างรายได้/ค่าใช้จ่าย → กำไรสะสม · ยอดยกมาจาก TB สิ้นรอบ | `OpeningRoll.CarryForward` | ✅ | `AccountingService.YearEndClose.cs` · `YearEndReopenRuleTests` | — |
| V-REC-01 | รายการประจำแบบสมุดรายวัน | Dr = Cr ตอนบันทึก **และ** ตอนรัน | ตรวจสองชั้น | ✅ | `RecurringTransactionService.cs:963` | — |
| V-OCR-01 | OCR auto-approve | เดินด่าน `ApproveDocumentAsync`/AutoPost เดียวกัน | ใช้เส้นเดียวกัน · ต้นทางยอด (SubTotal = Total − VAT) อยู่ขั้นสกัด | ✅ (JE) · 👉U | `OcrService.cs:6407` `:8837-8900` | ทีม U |
| V-INT-01 | API integration (ทีม S) | mapping JE Dr == Cr เป๊ะ · PV ไม่มีผัง WHT ⇒ ล้มดัง · isCashSale AutoPost ล้ม ⇒ degrade ตั้งหนี้ + PartialSuccess (คำตัดสินเดิม) | เท่าที่คาด | ✅ | `IntegrationService.cs:1741` `:4286-4305` `:1213-1228` · `IntegrationVatSplitTests` | — |
| V-PR-01 | payroll | Dr เงินเดือน gross (ต่อ cost center) · Dr SSO/กท./PVD นายจ้าง · Cr 21914/21815/21816/21818 · Cr หักเงินกู้/อื่น · Cr เงิน | เท่าที่คาด · ผังจำเป็นไม่มี ⇒ throw | ✅ | `PayrollService.cs:3030-3150` · `PayrollWithholdingTaxTests` ฯลฯ | — |
| V-GW-01 | gateway refund | Dr ลูกหนี้รอใบลดหนี้ / Cr เงินพัก | `JournalEntryBuilder` | ✅ | `GatewayRefundService.cs:271` · `GatewayRefundReview198E2Tests` | — |

## 2. สิ่งที่แก้ในคอมมิตนี้ (เฉพาะที่ปลอดภัย · ไม่รื้อของที่ถูก)

| ID | ไฟล์ | การแก้ | เทสต์สองทิศ |
| --- | --- | --- | --- |
| V-DN-01 | `Helpers/AdjustmentNoteAccount.cs` (+`CounterLegIsMoney` · `CounterLegSuffix` · `SettledTolerance`) · `DocumentService.cs` AutoPost CN/DN | DN: ขาเงินเฉพาะ `PaymentType == Cash` ของ DN เอง (ตรงกับ `isCashSettled` ตอนบันทึก) · CN: `source.BalanceDue ≤ 0.01` เท่าเดิม | `JePostingAuditTests`: DN เครดิต/ไม่ระบุ ของใบชำระครบ ⇒ ลูกหนี้ · DN Cash ⇒ เงิน · CN 0/0.01/0.02/5000 × 3 เทอม ⇒ เท่าเดิม · ภาพรวมตัวเลข 1,070 |
| V-CN-03 | `DocumentService.cs` AutoPost CN/DN | `counterAcc = isCashSettlement ? UseMoneyAccount() : …` — ช่องทางเงินของเอกสาร · ไม่มีช่องทาง = `111` เท่าเดิม · ช่องทางใช้ไม่ได้ ⇒ ล้มดังพร้อมทางไปต่อ (คลาส ID#7 เดิม) | ครอบโดย `DocumentMoneyChannel` tests เดิม |

**callers.py / grep รูปแบบเดิม**: `isCashSettlement` นิยาม 1 จุด ผู้อ่าน 5 จุด (ทั้งหมดในบล็อก CN/DN) — เหลือรูปแบบเดิม 0 จุด · `"111"` ตายตัวในบล็อก CN/DN เหลือเฉพาะ `counterAccCode` ที่ไม่ถูกใช้ในสาขาเงินแล้ว

## 3. Data-integrity (อ่านอย่างเดียว — ไม่แก้ข้อมูลเอง)

ใบเพิ่มหนี้ที่ลงบัญชีด้วยกติกาเดิม (ก่อนคอมมิตนี้) และมี/จะมีการรับชำระ ⇒ เงินสด GL เกินเท่ายอด DN · ตรวจด้วย:

```sql
-- DN ที่ JE อนุมัติมีขา Dr ผังเงิน (111xx หรือผังที่ผูกธนาคาร) แต่ DN เองเป็นเงินเชื่อ (PaymentType <> 1)
SELECT d."CompanyId", d."DocumentNumber", d."TotalAmount", d."PaidAmount", d."BalanceDue",
       SUM(l."DebitAmount") AS cash_dr_at_approval
FROM "Documents" d
JOIN "JournalEntries" j ON j."SourceDocumentId" = d."Id" AND j."CompanyId" = d."CompanyId"
     AND j."IsAutoGenerated" AND j."OriginalEntryId" IS NULL AND NOT j."IsDeleted" AND j."Status" <> 3 /* Reversed */
     AND j."Reference" IS DISTINCT FROM d."DocumentNumber" || '/PAY'
JOIN "JournalEntryLines" l ON l."JournalEntryId" = j."Id" AND NOT l."IsDeleted" AND l."DebitAmount" > 0
JOIN "ChartOfAccounts" a ON a."Id" = l."AccountId"
WHERE d."DocumentType" = /* DebitNote */ (SELECT 0 WHERE FALSE) -- แทนด้วยค่า enum จริงของ DebitNote
  AND COALESCE(d."PaymentType", 2) <> 1
  AND a."AccountCode" LIKE '111%'
  AND d."Status" NOT IN (/* Voided, Draft, Rejected */)
GROUP BY d."CompanyId", d."DocumentNumber", d."TotalAmount", d."PaidAmount", d."BalanceDue";
```

ทางแก้ข้อมูลที่เสนอ (ให้คนตัดสินต่อใบ): ใบที่ยังไม่รับชำระ ⇒ ยกเลิกแล้วออกใหม่ (JE ใหม่ลงลูกหนี้) · ใบที่รับชำระแล้ว ⇒ ใบสำคัญทั่วไป Dr ลูกหนี้ / Cr เงินสด เท่ายอด DN (คืนลูกหนี้ติดลบ + เงินสดเกิน)

## 4. Backlog / ตัดสิน (สรุป)

| ID | ประเภท | ข้อเสนอ |
| --- | --- | --- |
| V-PAY-05 | ❓ | ย้าย 21913→21911 ตามสัดส่วนเงินที่รับ (รวม CN/DN ลูก) หรือคงทั้งก้อน — ถ้าเปลี่ยนต้องแก้ `UndoOutputVatOnPaymentVoidAsync` คู่กัน |
| V-CN-04 | ❓ | ตัวเลือก "เครดิตค้างคืน" (Cr 217xx) สำหรับ CN ของใบที่ชำระครบ + หักใบถัดไปผ่าน `ApplyDepositToInvoiceAsync` |
| V-SL-07 | ⚠ | เทสต์ทางเข้า CSV/API/recurring ว่าส่วนลดท้ายบิลถูกเฉลี่ยลงบรรทัดก่อนถึง AutoPost |
| V-SL-10 | ⚠ | ใบแจ้งหนี้ที่ข้าม COGS เพราะไม่มีผัง 511/115 ⇒ `AppendInternalNote` เหมือนเส้นใบเสร็จ (ดัง 3 ที่) |
| V-PU-08 | ⚠ | CIL หัวใบ VAT ≠ Σ บรรทัด ⇒ คำเตือนตอนอนุมัติ แทนดูดเศษเข้าบรรทัดแรกเงียบ |
| V-PAY-03 | ⚠ UX | ฝั่งจ่าย: ให้ใช้บรรทัดปรับ 54710 (ค่าธรรมเนียมที่ธนาคารหักเพิ่ม) แทนบังคับบันทึกแยก |
| V-PAY-06 | ⚠ doc | แก้คอมเมนต์ "ดึง WHT จากต้นทาง" ให้ตรงโค้ด หรือ assert `doc.WithholdingTaxAmount == source` |
