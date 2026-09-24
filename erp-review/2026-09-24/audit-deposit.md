# ทีมตรวจ D — วงจรเงินมัดจำทั้งระบบ (ตรวจโค้ดบน branch ปัจจุบัน · 2026-09-24)

> โจทย์เจ้าของ: "มัดจำจะรับเต็มยอด / พัก VAT รอนำส่ง / ออก VAT เลย / เคสอื่น ๆ ต้องตั้งค่าได้ทั้งหมด · ทุกส่วนงานต้องเรียกการตั้งค่าเดียวกัน"
> บริบท: DECISIONS ข้อ 34 · ทีม L2 ทำ enum แยก branch — รายงานนี้คือ **ช่องว่างของโค้ดปัจจุบัน** ที่ L2 ต้องปิด
> วิธีตรวจ: อ่านซอร์สทุกเส้น (ไม่มี .NET SDK) · ทุกข้อมี file:line · "ไม่ทราบ" = ยังไม่ได้เปิดยืนยัน
> ข้อกฎหมายที่ใช้: §78 สินค้า / §78/1 บริการ (รับชำระราคา = จุดรับผิด) · คำสั่งกรมสรรพากร ป.86/2542 (เงินจอง/มัดจำ/ล่วงหน้า
> ที่เป็นส่วนของราคา = จุดรับผิด · **เงินประกัน**ที่ต้องคืนไม่ใช่ค่าตอบแทน) — *เลขคำสั่งให้นักบัญชียืนยันอีกครั้ง*

## 0. ตัวอย่างตัวเลขที่ใช้ทั้งรายงาน
ห้องพัก 7,450 รวม VAT (ฐาน 6,962.62 · VAT 487.38) · มัดจำ 2,000 รวม VAT (ฐาน 1,869.16 · VAT 130.84) · ส่วนที่เหลือ 5,450 (ฐาน 5,093.46 · VAT 356.54)
ตรวจ: 130.84 + 356.54 = 487.38 ✓

## 1. สรุปคำตอบ 5 ข้อ
1. **การตั้งค่าที่มีวันนี้มีแค่ธง boolean เดียว** `DepositOutputVatDeferred` (เอกสาร `Document.cs:240` · ที่พัก `Lodging.cs:89`) — ไม่มีค่าระดับบริษัท
   ไม่มีค่าเริ่มต้นตามประเภทธุรกิจ ไม่มีตัวเลือก "มัดจำเต็มยอดไม่มี VAT" (ทำได้ทางอ้อมด้วยการตั้ง VAT บรรทัด = 0) ไม่มีการตั้งค่า WHT/ริบ/คืน/วิธีหักใบสุดท้าย
2. **ไม่มี resolver กลาง** — ทุกทางเข้าอ่าน/ฮาร์ดโค้ดเอง: เอกสารมือ (radio) · ที่พัก (ธงของ property) · CMS (ฮาร์ดโค้ด immediate + VAT 7) · OCR (immediate เสมอ) ·
   integration (ธงอยู่บน**ใบสุดท้าย**ไม่ใช่ใบมัดจำ) · POS/API v1/recurring (ไม่มีมัดจำเลย)
3. **P0 3 ข้อ** — (ก) เช็คเอาต์ที่พักไม่ตัดมัดจำเลย เก็บเงินเต็ม 7,450 ซ้ำ (ข) ยกเลิก/no-show ที่มี VAT ล้มทุกครั้ง + ครึ่งทางคืนเงินซ้ำได้
   (ค) มัดจำแบบ VAT ทันที + หักเข้าใบกำกับเต็มจำนวน = ใบกำกับภาษีสองใบสำหรับ VAT ก้อนเดียว และแถวมัดจำหายจากรายงานภาษีขายของเดือนที่ออกใบ
4. ทางที่**ถูกอยู่แล้ว**: ใบเสร็จมัดจำ (JE `Cr 217xx + Cr 21911|21913`) · ขายเงินสดใบเดียว + หักมัดจำแบบ drives · หักมัดจำแบบ deferred · คืนมัดจำ + ใบลดหนี้ ·
   รับรู้ VAT ที่พักไว้ (`RecognizeDepositOutputVat`) · ด่าน e-Tax สำหรับมัดจำที่ยังพัก VAT
5. ฝั่งซื้อ**ไม่มีวงจรมัดจำเลย** (OCR แค่เสนอผัง 118xx) · WHT บนมัดจำนับซ้ำตอนใบสุดท้าย · การริบมัดจำไม่มีเส้นของตัวเอง (ยืม Realize ที่รับยอด "ฐาน")

## 2. Matrix — ทางเข้า × อ่านค่าตั้ง × การลงบัญชี × จังหวะ VAT × ถูกไหม
ย่อ: Imm = VAT ทันที (Cr 21911) · Und = VAT พักรอ (Cr 21913) · NoVat = มัดจำเต็มยอดไม่มี VAT (Cr 217xx ทั้งก้อน)

| # | ทางเข้า (file:line) | อ่านค่าตั้งจาก / ฮาร์ดโค้ด | การลงบัญชี | จังหวะ VAT / รายงาน | ถูก? |
|---|---|---|---|---|---|
| 1 | ฟอร์มเอกสาร "เงินมัดจำ" `documents.html:935-944, 8836-8845` → `DocumentService.cs:1084-1088` | radio ต่อใบ (ค่าเริ่มต้น = Imm) · NoVat = ต้องแก้ VAT บรรทัดเป็น 0 เอง · **ไม่อ่านค่าบริษัท** | `:14927-14963` Dr เงินสด(+11910) / Cr 217xx (ค่าเริ่มต้น 21712) / Cr 21911 หรือ 21913 | Imm: เดือนที่รับเงิน · Und: เดือน `RecognizedAt` (`TaxService.cs:596-604`) | ✅ ตอนรับเงิน (ดูข้อ 13-14 ตอนหัก) |
| 2 | IsDeposit บนชนิดอื่น | `:1084` ทิ้งธงเงียบ ๆ ถ้าไม่ใช่ Receipt/RV | ลงเป็นรายได้ | — | ⚠️ silent no-op (ใบแจ้งหนี้มัดจำ/งวดงานทำไม่ได้) |
| 3 | OCR ฝั่งขาย `OcrService.cs:2029-2037, 6217` | `OcrDepositMarker` ตัดสินว่า "เป็นมัดจำ" · **ไม่ตั้ง Deferred เลย** | เหมือนข้อ 1 (Imm) | Imm เสมอ | ⚠️ ไม่ดูว่ากระดาษเป็นใบกำกับไหม |
| 4 | OCR ฝั่งซื้อ `[DEPOSIT-BUY]` `OcrService.cs:2050-2084` | `PurchaseDepositAccount.PreferredCodes` | แค่เสนอ Dr 11810/11820 · ไม่มี IsDeposit ฝั่งซื้อ | ภาษีซื้อเคลมตามใบ | ❌ ไม่มีการหักตอนใบจริงมา (P1-5) |
| 5 | ที่พัก ยืนยัน/รับมัดจำ + gateway `LodgingService.Lifecycle.cs:344-373` · `LodgingReservationPaymentHandler.cs:63` | property `DepositOutputVatDeferred`, `DepositDeferredAccountCode` · อัตรา VAT จาก `EffectiveVatRateAsync` | เหมือนข้อ 1 | ตามธง property (ค่าเริ่มต้น Imm §78/1 ✓) | ✅ |
| 6 | ที่พัก เช็คเอาต์ `:581, 599-624` | ส่ง `DepositAppliedDrivesJournal=true` บน **TaxInvoice/Invoice เครดิต** | `:13669-13745` Dr AR 7,450 เต็ม — **ไม่มีใครอ่าน drives** · แล้ว `CreatePaymentAsync(BalanceDue=7,450)` | ใบกำกับรายงาน 487.38 + ใบมัดจำยังรายงาน 130.84 | ❌ **P0-1** |
| 7 | ที่พัก ยกเลิก/no-show `:727-737` | policy snapshot + `CancellationFeeAccountCode` | Refund(gross) ✓ · Realize(**gross ส่งเข้าช่องฐาน**) | — | ❌ **P0-2** |
| 8 | CMS PrePayment `CmsBookingService.cs:486-521` | **ฮาร์ดโค้ด** VAT 7 (ถ้าจด VAT) + Imm (ไม่ส่งธง) | Receipt IsDeposit ยอดมัดจำ | Imm | ⚠️ ไม่มีค่าตั้ง |
| 9 | CMS Completed `:320-339` | — | Realize(ฐานคงค้าง) → รายได้ | — | ❌ ยอดส่วนที่เหลือของบริการไม่เคยถูกออกเอกสาร (P1-3) |
| 10 | CMS Cancelled `:345-353` | — | `VoidDocumentAsync` ใบมัดจำทั้งใบ · error ถูกกลืนเป็น LogWarning | ไม่มีใบลดหนี้ · ไม่มีการริบ | ❌ P1-3 |
| 11 | POS จองโต๊ะ `PosReservationController.cs:153-161` · `PublicReservationController.cs:158-161` | `DepositAmount`, `LateCancelRefundPercent` | **ไม่มีเอกสาร/JE** — แค่ธง `DepositPaid` | ไม่มี VAT | ❌ P1-4 เงินรับจริงอยู่นอกบัญชี |
| 12 | `PosOrder.IsDeposit` `Pos.cs:126-134` | doc-comment อ้างว่า Cr 217xx | **ไม่มีผู้อ่านทั้งเรพ** | — | ⚠️ dead field (หลัก F2 ข้อ 2) |
| 13 | ปุ่ม "หักมัดจำ" `ApplyDepositToInvoiceCoreAsync` `DocumentService.cs:3942-4205` | อ่าน GL ของใบมัดจำ (family-net) · guard งวดยื่นแล้ว `:3968-3990` เฉพาะ Imm | Dr 217xx + Dr 21911/21913 / Cr AR | Imm: แถวมัดจำ**ถูกข้าม**เมื่อหักแล้ว (`TaxService.cs:606-620`) ⇒ VAT ย้ายไปเดือนใบสุดท้าย | ✅ Und/NoVat · ❌ Imm (**P0-3**) |
| 14 | หักแบบ drives ในใบเสร็จ/ขายเงินสดใบเดียว `:14534-14915` (branch `:14415-14420`) | อ่าน GL ใบมัดจำ/JV | Dr เงินสดสุทธิ + Dr 217xx + Dr 21911/21913 | เหมือนข้อ 13 **แต่ไม่มี guard งวดยื่นแล้ว** | ✅ GL · ❌ Imm (P0-3) |
| 15 | Realize (มือ/หน้า deposit-center) `:3246-3366` | Amount = **ฐาน** | Dr 217xx / Cr รายได้ (+ย้าย 21913→21911 **ทั้งก้อน** `:3291-3308`) | Und: เดือนที่ realize | ⚠️ P1-2 หลังคืนบางส่วน |
| 16 | Refund `:3747-3900` | Amount = **gross** | Dr 217xx + Dr 21911/21913 / Cr "111" (ฮาร์ดโค้ด) + ใบลดหนี้ถ้า VAT เคยรายงาน `:3853` | ลดในเดือนที่คืน | ✅ ไม่มี WHT · ❌ มี WHT (P1-6) |
| 17 | รับรู้ VAT พักรอ `:3066-3130` · ปุ่ม 🧾 | — | Dr 21913 / Cr 21911 | เดือนที่ระบุ | ✅ |
| 18 | TakeTime/integration `IntegrationService.cs:945-952, 971-995, 4316` | มัดจำ = JV ผ่าน mapping `DEPOSIT_RECEIVED 111→215` (VAT ตามที่ TakeTime ส่ง) · ธง `DepositOutputVatDeferred` ถูก**เก็บบนใบสุดท้าย** | IsCashSale → drives ✓ · เครดิต → mapping JE ไม่อ่าน drives | JV ไม่เคยเข้า ภ.พ.30 (รายงานสแกน Documents) ⇒ VAT มัดจำไปโผล่เดือนใบสุดท้ายเสมอ | ⚠️ ถูกเฉพาะ NoVat/Und |
| 19 | API v1 `DocumentsV1Controller` · recurring `RecurringTransactionService` | ไม่ map IsDeposit | — | — | ⚠️ ไม่รองรับมัดจำ |
| 20 | PDF HTML `PdfGenerationService.cs:1184-1187, 1442-1446, 2136-2151` · QuestPDF `DocumentRenderer.cs:841, 908, 928-934` | `IsDeferredVatDeposit` | หัว: Imm = "ใบกำกับภาษี/ใบเสร็จรับเงิน (เงินมัดจำ)" · Und = ใบเสร็จไม่โชว์ VAT · ใบสุดท้ายหัก "มัดจำ" **หลังยอดรวม** (VAT เต็ม) | — | ⚠️ ป้าย literal ไทยทั้งสอง renderer (`:2148`, `:932`) · QuestPDF ไม่มี breakdown หลายใบ |
| 21 | e-Tax `EtaxInvoiceService.cs:132` | ห้ามเฉพาะ Und ที่ยังไม่ recognize | — | — | ❌ P1-7 drift กับ PDF |
| 22 | หน้า deposit-center / deposits / GetDeposits `DocumentService.cs:3373-3550` | prefix 215/217 + ชื่อบัญชี | — | chip VAT | ⚠️ P2 (215 = ค่าสาธารณูปโภคค้างจ่าย, 21714 = ดอกเบี้ยค้างจ่าย ในผังมาตรฐาน) |
| 23 | TaxPointResolver `Tax/TaxPointResolver.cs:47-81` | ไม่รู้จักมัดจำ/เงินประกัน | — | ใบ Und ถูกประทับ TaxPointDate = วันรับเงิน (แต่รายงานใช้ RecognizedAt แทน) | ⚠️ ไม่มีแนวคิด "ไม่ใช่ค่าตอบแทน" |
| 24 | พรีวิว GL ก่อนอนุมัติ `PdfGenerationService.cs:776-786, 807` | อ่าน `DepositOutputVatDeferred` แต่ไม่อ่าน IsDeposit | โชว์ Cr รายได้ แทน Cr 217xx | — | ⚠️ P2 พรีวิวไม่ตรง JE |

## 3. Findings เรียงความรุนแรง

### ✅ 5721ebd (+198fb5c C2) P0-1 เช็คเอาต์ที่พัก: "หักมัดจำ" ไม่มีผลต่อบัญชีเลย → เก็บเงินเต็มซ้ำ + VAT ซ้ำ
- `LodgingService.Lifecycle.cs:581` สร้าง `TaxInvoice` (หรือ `Invoice` เมื่อไม่มี VAT) แบบเครดิต + `DepositAppliedDrivesJournal=true` (`:609-611`)
- แต่ drives ถูกอ่าน**เฉพาะ** branch ใบเสร็จ/ขายเงินสดใบเดียว (`DocumentService.cs:14415-14420, 14534`) · branch ใบกำกับเครดิต `:13669-13745` ลง `Dr AR = TotalAmount` เต็ม ·
  `BalanceDue = TotalAmount` (`:1469`) · ไม่มีผู้อ่าน `DepositAppliedAmount` อื่นใน approve/payment (ไล่ผู้อ่านครบ 33 จุดแล้ว)
- `CollectBalanceNow` → `CreatePaymentAsync(Amount: approved.BalanceDue)` (`:616-622`) = **7,450** ไม่ใช่ 5,450
- ผล (มัดจำ Imm): เงินสดใน GL +9,450 (จริง 7,450) · 21712 ค้าง 1,869.16 ตลอดไป · 21911 = 618.22 (ควร 487.38) · ภ.พ.30 เดือนมัดจำ 130.84 + เดือนเช็คเอาต์ 487.38
  (แถวมัดจำไม่ถูกข้ามเพราะ `DepositAppliedToDocumentId` ไม่ถูกตั้ง) · `r.PaidAmount` = 9,450 · PDF พิมพ์ "ยอดชำระสุทธิ 5,450" แต่ลูกหนี้/ชำระ 7,450
- ผล (มัดจำ Und): 21913 ค้าง 130.84 → ครบ 90 วันหน้า ภ.พ.30 เตือน (`TaxService.cs:1417-1440`) ให้กด "รับรู้ VAT" → **กดแล้วเกิด VAT ซ้ำ 130.84**
- DOCUMENT_FLOW `:2529` เขียนว่า "DocumentService ตัด 217xx + guard over-apply ให้" = **ไม่จริง** · ไม่มีเทสต์ใดครอบเส้นนี้
- ทางแก้: ให้ credit branch อ่าน drives ผ่านตัวสร้างขาเดียวกับ `:14569-14915` (ห้ามเขียนชุดที่สอง) และ `BalanceDue = Total − ส่วนที่หัก` · หรือเช็คเอาต์เรียก `ApplyDepositToInvoiceAsync` หลัง approve · ถ้ายังไม่แก้ ต้อง throw เมื่อ drives อยู่บนใบที่ไม่รองรับ (ห้าม silent no-op)

### ✅ 5721ebd (+198fb5c C3/C6) P0-2 ยกเลิก/no-show ที่พักที่มี VAT ล้มเสมอ และล้มกลางทาง
- `LodgingService.Lifecycle.cs:728-736` ส่ง `forfeit` (ยอด **gross**) เข้า `RealizeDepositRequest.Amount` ซึ่งเป็น **ฐาน** (`DocumentService.cs:3267-3273` เทียบกับ `SubTotal`)
- no-show: fee 7,450 → forfeit 2,000 > ฐานคงค้าง 1,869.16 → `InvalidOperationException` → **no-show/ยกเลิกแบบไม่คืนเงินทำไม่ได้เลย** เมื่อที่พักจด VAT
- ยกเลิกค่าปรับ 1,000: Refund 1,000 **commit แล้ว** (tx ของตัวเอง `:3767-3899` + ใบลดหนี้) → Realize 1,000 > ฐานคงเหลือ 934.58 → throw → การจองยังเป็น Confirmed
  → กดซ้ำ: guard คืนเงินผ่านอีกรอบ (refundBase 934.58 ≤ 934.58) = **คืน/ใบลดหนี้ซ้ำ**
- ไม่มี transaction ครอบ `CancelCoreAsync` · ไม่มีเทสต์ (`grep` เทสต์พบแค่ `DepositReversalMathTests` กับ path-parity ใน `SimulationRound7Tests.cs:87-90`)

### ✅ 5721ebd (+979eefe N1 ทุกเส้นหักฐานก่อน VAT · ⚠️ R3-1 ยังเปิด) P0-3 มัดจำ "VAT ทันที" + หักเข้าใบกำกับเต็มจำนวน = ใบกำกับสองใบ, แถวภาษีขายย้ายเดือน
- ใบมัดจำ Imm พิมพ์หัว "ใบกำกับภาษี/ใบเสร็จรับเงิน (เงินมัดจำ)" (`PdfGenerationService.cs:1442-1446, 1472`) และออก e-Tax ได้ (`EtaxInvoiceService.cs:132` ไม่กัน)
- ใบสุดท้ายคิด VAT **เต็ม 487.38** แล้วหักมัดจำ gross หลังยอดรวม (`:2136-2151` / `DocumentRenderer.cs:928-934`) ⇒ ลูกค้านิติบุคคลถือใบกำกับรวม VAT 618.22 สำหรับ VAT จริง 487.38
- `TaxService.cs:606-620` **ข้ามแถวมัดจำ Imm ที่ถูกหักแล้ว** ⇒ ถ้าออกรายงานเดือนมัดจำหลังเช็คเอาต์ (มัดจำ 25 ส.ค. · เช็คเอาต์ 2 ก.ย. · ทำ ภ.พ.30 ส.ค. วันที่ 10 ก.ย.)
  ใบกำกับที่ออกจริงเดือน ส.ค. หายจากรายงานภาษีขาย ส.ค. (§87) และ VAT 130.84 ไปจ่ายเดือน ก.ย. (ช้า §78/1 → เงินเพิ่ม)
- guard งวดยื่นแล้วมีเฉพาะปุ่ม Apply (`DocumentService.cs:3968-3990`) — **เส้น drives `:14534-14915` ไม่มี** ⇒ ถ้า ส.ค. ยื่นแล้ว: ก.ย. รายงาน 487.38 เต็ม = จ่าย 130.84 สองครั้ง และ GL 21911 ≠ แบบที่ยื่น
- DOCUMENT_FLOW `:658` ยอมรับเองว่าวิธี B "ต้องออกเฉพาะยอดคงเหลือ · ห้ามกดหักมัดจำ" แต่ระบบ**ไม่มีโหมด "หักมัดจำก่อนคิด VAT"** ให้เลือก และเส้นอัตโนมัติ (ที่พัก/TakeTime) กดหักให้เสมอ
- ทางที่ถูกสำหรับ Imm: ใบสุดท้ายหักฐานมัดจำก่อนคิด VAT (VAT 356.54) + แถวมัดจำคงอยู่ในเดือนของมัน · หรือออกใบลดหนี้ยกเลิกใบกำกับมัดจำก่อน (CN §86/10) แล้วค่อยออกเต็ม

### P1
1. **ไม่มีการตั้งค่ากลาง** (ข้อ 34 โดยตรง) — ค่าเริ่มต้นกระจาย 5 ที่: radio (`documents.html:938`), property (`Lodging.cs:89`), CMS ไม่ส่งธง (`CmsBookingService.cs:521`),
   OCR ไม่ส่งธง (`OcrService.cs:6217`), integration ส่งบนใบผิดใบ (`IntegrationService.cs:951`) · `Company.IndustryType` มีอยู่แล้ว (`Company.cs:13`) แต่ไม่มีใครใช้ตัดสินเรื่องนี้
2. **Realize หลังคืนบางส่วน (Und) ย้าย VAT ทั้งก้อน** — `vatMove = doc.VatAmount` (`DocumentService.cs:3291-3308`) ไม่หักส่วนที่ Refund เคย Dr 21913 ไปแล้ว:
   มัดจำ Und 2,000 คืน 1,000 (Dr 21913 65.42) แล้วริบที่เหลือ → Dr 21913 130.84 ⇒ 21913 ติดลบ 65.42 · ภ.พ.30 รายงาน 130.84 (ควร 65.42)
3. **CMS**: PrePayment รับรู้เฉพาะยอดมัดจำ (`:330-335`) ยอดที่เหลือของบริการไม่มีเอกสาร/รายได้/VAT · ยกเลิก = Void ใบมัดจำ (ไม่ใช่ Refund+CN, ไม่มีการริบตามนโยบาย) และถ้า Void ถูกบล็อก (งวดยื่นแล้ว) error ถูกกลืนที่ `:348-352`
4. **POS จองโต๊ะรับมัดจำโดยไม่มีบัญชี** — `MarkDeposit` แค่ตั้งธง (`PosReservationController.cs:153-161`) · คืน/ริบตาม `LateCancelRefundPercent` ไม่มี JE
5. **ฝั่งซื้อไม่มีวงจรมัดจำ** — IsDeposit รับเฉพาะ Receipt/RV (`DocumentService.cs:1084-1086`) · OCR แค่เสนอ Dr 118xx (`OcrService.cs:2050-2084`) ⇒ ใบกำกับเต็มจำนวนของผู้ขายมาทีหลัง
   = ค่าใช้จ่าย + ภาษีซื้อเต็ม ขณะที่ภาษีซื้อของใบมัดจำเคลมไปแล้ว (เคลมซ้ำ) และ 118xx ค้าง · WHT ที่เราหักตอนจ่ายมัดจำจะถูกหักซ้ำบนใบสุดท้าย
6. **WHT บนมัดจำนับซ้ำ** — ใบมัดจำ Dr 11910 ตามบรรทัด (`:14916-14921`); ใบสุดท้าย drives Dr 11910 เต็มอีกครั้ง. ตัวอย่างบริการ 10,000 + VAT 700, WHT 3%, มัดจำ 30%:
   มัดจำ Dr 11910 90 · ใบสุดท้าย (หัก 3,210) Dr 11910 300 ⇒ 11910 = 390 (จริง 300) และเงินสดลง 7,190 (จริง 7,280) · Refund ใช้ `VatAmount/TotalAmount` (`:3776`) ที่ Total สุทธิ WHT แล้ว
   ⇒ คืนเต็ม 3,120 ทิ้ง 217xx 90 + 11910 90 ค้าง · `WhtCreditService` ไม่รู้จักมัดจำ (ไม่พบคำว่า Deposit ในไฟล์)
7. **e-Tax ≠ PDF สำหรับมัดจำ Und ที่ถูกหักแล้ว** — drives ประทับ `DepositOutputVatRecognizedAt` (`:14653, 14794`) ⇒ ด่าน e-Tax (`EtaxInvoiceService.cs:132`) ปล่อยผ่านเป็น T03
   ขณะที่ PDF ตัดสินว่า "ไม่ใช่ใบกำกับ" (`IsDeferredVatDeposit` `:1184-1187`) และใบสุดท้ายออก e-Tax เต็มอีกใบ
8. **Integration**: ใบกำกับเครดิต (IsCashSale=false) ที่ส่ง drives ถูกลง mapping JE เต็ม (`IntegrationService.cs:991-994`) โดย drives ไม่มีผล — ญาติ P0-1 ·
   มัดจำเป็น JV (`DEPOSIT_RECEIVED 111→215` `:4316`) ⇒ VAT ของมัดจำ (ถ้า TakeTime ลง 21911) ไม่เคยอยู่ใน ภ.พ.30 ของเดือนรับเงิน

### P2
1. ป้ายหักมัดจำเป็น literal ไทยทั้งสอง renderer (`PdfGenerationService.cs:2148` · `DocumentRenderer.cs:932`) — ผิดกฎ A ข้อ 1 (`DocumentLabels`) · QuestPDF ไม่มีแถวแยกหลายใบแบบ HTML `:2136-2143`
2. รายการ/KPI มัดจำจับด้วย prefix 215/217 + ชื่อบัญชี (`DocumentService.cs:3398-3405, 4080-4083`; `IntegrationService.cs:3113-3127` + `Notes.Contains("มัดจำ")`) —
   ผังมาตรฐาน 215 = ค่าสาธารณูปโภคค้างจ่าย, 21714 = ดอกเบี้ยค้างจ่าย (`ChartOfAccountTemplates.cs:121-135`) ⇒ ค่าไฟค้างจ่ายโผล่เป็น "มัดจำ"
3. `deposits.html:174` แสดง "✓ ถึงกำหนด (21911)" ให้มัดจำที่ไม่มี VAT · มีหน้ามัดจำ 2 หน้า (`deposits.html`, `deposit-center.html`) กติกาไม่ตรงกัน
4. คืนมัดจำลง Cr "111" ตายตัว (`DocumentService.cs:3796`) ไม่รับบัญชีธนาคาร/เกตเวย์ (11340) → กระทบยอดธนาคารไม่ตรง
5. ป้ายตั้งค่าที่พัก "VAT มัดจำรอเกิดตอนเช็คอิน" (`lodging-settings.html:129`) — โค้ดรับรู้ตอนเช็คเอาต์/realize ไม่ใช่เช็คอิน · property null → 21712 "ค่าสินค้ารับล่วงหน้า" สำหรับค่าห้อง (บริการ)
6. พรีวิว GL (`PdfGenerationService.cs:776-786`) แสดงขารายได้แทน 217xx สำหรับใบมัดจำ · `JournalPostingGuard.cs:91` ข้ามการตรวจ JE มัดจำทั้งหมด
7. `TaxPointResolver` ไม่มีแนวคิด "เงินประกันไม่ใช่ค่าตอบแทน" — ความหมาย Und ฝังอยู่ในธงเอกสารอย่างเดียว
8. API v1 / recurring รับมัดจำไม่ได้ (ไม่ได้ map `IsDeposit`) · `PosOrder.IsDeposit` เป็น dead field ที่ doc-comment อ้างพฤติกรรม

## 4. Doc ↔ โค้ดไม่ตรง (โค้ดคือความจริง — ต้องแก้ doc ในคอมมิตที่แก้โค้ด)
| DOCUMENT_FLOW | เขียนว่า | โค้ดจริง |
|---|---|---|
| `:1448-1455` §3.7 Apply | ขั้น 2 "เรียก `RealizeDepositAsync`" · อ้าง `:1389` | ไม่เรียก — ลง Dr 217xx/VAT / Cr AR เอง (`DocumentService.cs:4005-4010`) · เมธอดอยู่ `:3911` |
| `:2529` §6.5 เช็คเอาต์ | "DocumentService ตัด 217xx + guard over-apply ให้" | ใบเครดิตไม่อ่าน drives (P0-1) |
| `:2530` §6.5 ยกเลิก | ส่วนริบ `RealizeDepositAsync` | ส่ง gross เข้าช่องฐาน → throw (P0-2) |
| `:658` วิธี B | "✅ แต่ห้ามกดหักมัดจำ" | เส้นอัตโนมัติ (ที่พัก/TakeTime drives) หักให้เสมอ, ไม่มี guard งวดยื่น (P0-3) |
| `:880-882` CMS PrePayment | realize 217xx → 41000 | ครบแค่ยอดมัดจำ ส่วนที่เหลือไม่ถูกออกเอกสาร (P1-3) |
| `Pos.cs:126-129` (doc-comment) | POS มัดจำ Cr 217xx | ไม่มีผู้อ่าน · DOCUMENT_FLOW `:945` เขียนถูก ("ยังไม่ enabled") |

## 5. การตั้งค่า: มีแล้ว vs ต้องมี
**มีแล้ว**: `Document.IsDeposit / DepositOutputVatDeferred / DepositDeferredAccountCode / DepositApplied* / DepositAppliedDrivesJournal` ·
`LodgingProperty.DepositOutputVatDeferred / DepositDeferredAccountCode / CancellationFeeAccountCode / DepositPercent·Fixed·Min·Max / NoShowChargePercent / ChargeVat / PricesIncludeVat` ·
CMS `BookingService.DepositAmount·DepositPercent·BookingType` · POS `PosReservation.DepositAmount·LateCancelRefundPercent·FreeCancelHoursBefore` ·
`CompanySettings.WhtRecognitionBasis` · payload integration 4 ช่อง · mapping `DEPOSIT_RECEIVED` · `Company.IndustryType` (ยังไม่ถูกใช้กับมัดจำ)

**ต้องมี** (ระดับบริษัท → override โมดูล/ที่พัก/บริการ → override ต่อใบ):
1. `DepositVatTreatment` = `NoVatUntilFinal` (มัดจำเต็มยอด) · `VatUndue` (พัก 21913) · `VatImmediate` (ใบกำกับตอนรับเงิน) · `SecurityDeposit` (เงินประกันที่ต้องคืน ไม่ใช่ค่าตอบแทน — ไม่มี VAT, ไม่หักเป็นราคาจนกว่าจะเปลี่ยนสภาพ)
2. ค่าเริ่มต้นตาม `IndustryType` (ข้อ 34): บริการ/ที่พัก/ร้านอาหาร/สปา → `VatImmediate` · สินค้า → คงเดิม (`VatImmediate` = พฤติกรรมวันนี้) · ไม่รู้ → คงเดิม + บังคับให้เจ้าของเลือก + คำเตือนเมื่อบริการเลือกเลื่อน VAT
3. `DepositFinalDeduction` = `DeductBaseBeforeVat` (ต้องใช้กับ `VatImmediate`) · `FullInvoiceAfterCreditNote` · `FullInvoiceDeductGross` (ใช้ได้เฉพาะ NoVat/Undue) — resolver ต้อง**ปฏิเสธคู่ที่ผิดกฎหมาย**
4. `DepositWhtMode` = ลูกค้าหัก ณ ที่จ่ายตอนมัดจำหรือไม่ → ใบสุดท้ายคิด WHT เฉพาะส่วนที่เหลือ (ขาย) · ฝั่งซื้อ: เราหักตอนจ่ายมัดจำ
5. `DepositForfeitTreatment` = ริบเป็นรายได้ (VAT คงไว้ถ้าเคยออก) · ริบเป็นค่าเสียหาย (ไม่ใช่ค่าตอบแทน — ถ้าเคยออก VAT ต้องทำ CN?) — **ต้องให้นักบัญชี/เจ้าของตัดสิน**
6. บัญชีพักค่าเริ่มต้นตามชนิด (สินค้า 21712 · บริการ 21713 · ค่าเช่า 21711 · ห้องพัก 21510) + บัญชีรับ/คืนเงิน (ห้าม "111" ตายตัว)
7. ฝั่งซื้อ: `PurchaseDepositTreatment` + ผัง 11810/11820 + ตัวหักตอนใบจริงมา

## 6. ข้อเสนอ: resolver ตัวเดียว (owner file)
```
Helpers/DepositPolicyResolver.cs   (pure · มีเทสต์ด้วยตัวเลขข้อ 0)
enum DepositVatTreatment { NoVatUntilFinal=1, VatUndue=2, VatImmediate=3, SecurityDeposit=4 }
enum DepositFinalDeduction { DeductBaseBeforeVat=1, FullInvoiceAfterCreditNote=2, FullInvoiceDeductGross=3 }
record DepositPolicy(DepositVatTreatment Vat, DepositFinalDeduction Deduction, bool WhtAtDeposit,
                     DepositForfeitTreatment Forfeit, string DeferredAccountCode, string Source /*Document|Property|Service|Company|Industry*/);
static DepositPolicy Resolve(CompanyDepositSettings co, IndustryType industry, ModuleDepositOverride? module,
                             DocumentDepositOverride? doc, SupplyKind kind);        // ห้ามคืน "ไม่รู้" — ไม่รู้ = ค่าเริ่มต้น + Source=Industry
static string? Reject(DepositPolicy p);   // คู่ผิดกฎหมาย เช่น VatImmediate + FullInvoiceDeductGross → ข้อความไทย + RuleCode
static (decimal Base, decimal Vat, decimal Wht) SplitReceived(decimal gross, DepositPolicy p, decimal vatRate, decimal whtRate);
static FinalInvoiceDeposit ComputeFinal(DepositSnapshot dep, decimal finalGross, DepositPolicy p); // ฐาน/VAT/WHT ที่เหลือ
```
กติกา:
- **ตอนรับมัดจำ ตรึง snapshot ลงใบมัดจำ** (`DepositVatTreatment`, `DepositFinalDeduction`, `PolicySource`) เหมือน `IsTaxInvoiceByLaw` — ทุกขั้นหลังจากนั้น (หัก/ริบ/คืน/รายงาน/PDF/e-Tax)
  อ่าน **snapshot ของใบมัดจำ** ไม่อ่าน payload ของใบสุดท้าย (ปิด P1-8) และไม่อ่านค่าบริษัทซ้ำ (เปลี่ยนค่าตั้งแล้วใบเก่าไม่เพี้ยน)
- ผู้เรียกที่ต้องต่อสาย (ทุกตัวในตาราง §2): `CreateDocumentAsync` · `AutoPostToJournalAsync` (ใบมัดจำ + ใบเครดิต + drives — ตัวสร้างขาชุดเดียว) · `Realize/Refund/Apply/Recognize` + `ForfeitDepositAsync` ใหม่ (รับ gross)
  · `TaxService` (แถวมัดจำ Imm อยู่เดือนของมัน **เสมอ**) · `PdfGenerationService.IsDeferredVatDeposit` + ตัวคำนวณแถวหักทั้งสอง renderer · `EtaxInvoiceService` · Lodging (confirm/checkout/cancel) · CMS · POS reservation · OCR · Integration · API v1
- ป้าย UI ทุกที่มาจาก server (`Source` + ชื่อ treatment) — ใบมัดจำ/ใบสุดท้าย/หน้ามัดจำ/หน้าจอง แสดง "มัดจำแบบ: ออกใบกำกับทันที (ค่าตั้งบริษัท · ธุรกิจบริการ)"
- checker ใหม่ `tools/deposit_policy_single_source_check.py`: ฟ้องการอ่าน `DepositOutputVatDeferred`/treatment นอก owner file + ผู้เรียกที่อนุญาต · negative test = ใส่ `?? false` ใน CMS กลับแล้วต้องฟ้อง
- migration: ใบเก่า `DepositOutputVatDeferred=false & VatAmount>0` → `VatImmediate` · `true` → `VatUndue` · `VatAmount=0` → `NoVatUntilFinal` · ซ่อมข้อมูลเช็คเอาต์ที่พักที่เกิด P0-1 แล้ว (ใบกำกับที่มี `DepositAppliedDrivesJournal=true` และชนิดเครดิต — หาได้ด้วย query เดียว)
- เทสต์ขั้นต่ำ (ทั้งสองทิศ): ตัวเลขข้อ 0 × 4 treatment × {หัก, คืนบางส่วน, ริบทั้งหมด, ริบบางส่วน, มี WHT} + "ใบขายสดไม่มีมัดจำไม่ถูกแตะ"

## 7. ต้องให้เจ้าของ/นักบัญชีตัดสิน (ห้ามเดาแทน)
1. เงินมัดจำที่ถูกริบ: เป็นค่าตอบแทน (VAT คงไว้) หรือค่าเสียหาย (ไม่ใช่ฐาน VAT — ต้องออกใบลดหนี้คืน VAT ที่เคยออกไหม) — ต่อธุรกิจหรือต่อบริษัท
2. ค่าเริ่มต้นของใบสุดท้ายสำหรับ `VatImmediate`: `DeductBaseBeforeVat` (แนะนำ) หรือ `FullInvoiceAfterCreditNote`
3. ผังพักมัดจำที่พัก: ใช้ 21510 (ผังโรงแรม) หรือ 21713 (ผังมาตรฐาน) เป็นค่าเริ่มต้น และจะเลิกนับ 215xx ทั้งกลุ่มเป็น "มัดจำ" ไหม
4. POS จองโต๊ะ: ต้องออกใบเสร็จมัดจำจริง หรือยอมให้อยู่นอกบัญชีแต่ต้องเตือน
