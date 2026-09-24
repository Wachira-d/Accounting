# ทีม C — สถาปนิกลำดับความคิด: "หายอดรวมทั้งสิ้นก่อน แล้วค่อยแตกยอดอื่นให้รวมได้ยอดนี้" (รอบ 192)

> โจทย์เจ้าของ: "สิ่งแรกที่ต้องถูกต้องก่อนเลยคือการหาว่าอันไหนคือยอด total ของเอกสาร … แล้วค่อยไล่หา วิเคราะห์ค่าอื่น ๆ
> นำมารวมกันให้ตรงยอดนี้ตามเหตุและผล" · หลักการรอบ 190 ยังใช้: **เพิ่มวิธีคิด ไม่รื้อ**
>
> **ขอบเขต:** งานออกแบบ ไม่ได้แก้โค้ด (read-only) · ทุก file:line เปิดยืนยันแล้ววันนี้ · **ยังไม่ได้คอมไพล์/รันอะไร** ·
> สิ่งที่ไม่รู้เขียนว่าไม่รู้ (โดยเฉพาะ "Azure ติดป้ายยอดไหนของใบ M/U/S จริง" — ต้องดู ReasoningTrace ของสแกนจริง)

---

## 0. สรุปคำตอบ

1. วันนี้ระบบ**ไม่มีขั้น "เลือกยอดรวม"** — ยอดรวมคือค่าที่ engine ติดป้ายมา (`OcrService.cs:3463` `TotalAmount = azure.InvoiceTotal ?? azure.AmountDue`
   ⇒ `AmountDue` ถูก**ทิ้ง**เมื่อมี InvoiceTotal) แล้วชั้นถัดไปทำได้แค่ **ลดความมั่นใจ** เมื่อหลักฐานอื่นค้าน
   (`SmartFieldExtractor.cs:147` ตัวอักษรไม่ตรง ⇒ conf 0.35 แต่**ไม่เปลี่ยนค่า**) หรือซ่อมกรณีเดียวคือป้ายสลับ (`OcrHeaderAmounts.IsSwapped`)
2. ใบ M คือหลักฐานว่าป้ายภาษาอังกฤษ "TOTAL" ไม่ใช่ยอดรวมทั้งสิ้นเสมอ: 24,110.00 มีหลักฐาน 1 ชั้น (ป้าย) · 23,812.25 มี **6 ชั้นอิสระ**
   (AMOUNT · NET AMOUNT · ตัวอักษร · แถวชำระ · แถว "รวม" ตารางรหัส ภ.พ. · ฐาน 22,663.97 + VAT 1,148.28) **และ**ผู้แพ้ถูกอธิบายได้
   (24,110.00 − ส่วนลด 297.75 = 23,812.25) ⇒ ต้องมีตัวตัดสินที่ **นับชั้นหลักฐานอิสระ + บังคับให้อธิบายผู้แพ้** ไม่ใช่เชื่อป้าย
3. "Total" มี **สองความหมายที่ต้องแยกช่อง ห้ามรวมเป็นช่องเดียว**: `TaxInvoiceTotal` (ฐาน + VAT ตามใบกำกับ = `Document.TotalAmount`
   · ฐาน §87) กับ `AmountSettled` (เงินที่จ่ายจริง) — ใบ U: 536.00 กับ 438.00 · ตัวแยกคือ **"VAT ถูกคิดจากยอดไหน"** (35.07 = 536 × 7/107)
4. ขั้น 2 = ตั้งสมมติฐานการแตกยอด 6 แบบ (H0–H5) แล้ว**ให้ตัวเลขที่พิมพ์บนกระดาษเป็นกรรมการ**: เลือกแบบที่อธิบายตัวเลขที่พิมพ์ได้มากที่สุด
   ด้วยสมมติฐานน้อยที่สุด · ไม่มีแบบไหนลงตัว = `Unknown` + แท็ก ห้ามแต่งตัวเลข
5. ขั้น 3 **ไม่เขียนตัวใหม่** — แปลผลขั้น 2 เป็น input ของ `OcrLineReconciler` (เคส A–E) · `OcrLineVatMarks` · ตัวกระจายส่วนลดเดิม ·
   `OcrAmountIntegrity` เป็นกรรมการตัวสุดท้ายเหมือนเดิม · ของใหม่มีแค่ "บรรทัดสรุปรายกลุ่ม VAT" เมื่อไม่มีรายการ (ใบ M หน้า 3)
6. ชั้นใหม่ = pure helper 2 ตัว (`OcrTotalAnchor` · `OcrTotalDecomposer`) + เมธอดเพิ่ม 1 ตัวใน `OcrLineVatMarks` (`ReadGroups`) ·
   ต่อสาย 3 จุด · **ไม่ลบโค้ดเดิมบรรทัดเดียว** · ทุกใบในชุด replay เดิมต้องได้คำตอบเดิม (ตาราง §5)

---

## 1. สภาพปัจจุบันกับกระดาษสามใบ (อ่านจากโค้ด — ผลจริงขึ้นกับว่า engine ติดป้ายอะไร)

| ใบ | ถ้า engine ให้ Total = | เส้นทางวันนี้ (file:line) | ผล |
|---|---|---|---|
| M | 24,110.00 (ป้าย TOTAL) | ตัวอักษร 23,812.25 ≠ ⇒ conf 0.35 เท่านั้น (`SmartFieldExtractor.cs:147`) · `OcrBillDiscount.Read` ได้ 297.75 · ด่านคณิตข้อ 0 (`OcrConfidenceGateway.cs:68`) ไม่เข้า (22,663.97 − 297.75 + 1,148.28 = 23,514.50 ≠ 24,110) ⇒ `[MATH]` · สร้างเอกสาร: `NetSubTotal` = 24,110 − 1,148.28 = **22,961.72** (`OcrHeaderAmounts.cs:65`) · ไม่มีรายการ ⇒ บรรทัดสรุป 7% ใบเดียว (`OcrService.cs:6814`) ⇒ `[Σ-GAP]` อัตรา × ยอด | **ยอดรวมผิด 297.75 · ฐานผิด 297.75 · ติดธงถูก แต่ผู้ใช้ต้องแก้ 3 ช่อง + แยกบรรทัดเอง** |
| M | 23,812.25 | ข้อ 0 ไม่เข้า (ไม่ต้อง) · บรรทัดสรุป 7% ยอด 22,663.97 VAT 1,148.28 ⇒ 7% × 22,663.97 = 1,586.48 ≠ 1,148.28 ⇒ `[Σ-GAP]` | ยอดถูก แต่**แยกกลุ่มยกเว้น 6,260.00 ไม่ได้** ทั้งที่กระดาษพิมพ์ไว้ (ตารางแบบ `จำนวนชิ้น รหัส ฐาน VAT รวม` ไม่เข้า regex `SummaryRow` ของ `OcrLineVatMarks.cs:62` ซึ่งต้องการ `ตัวอักษร อัตรา ฐาน VAT รวม`) |
| U | 536.00 | ส่วนลด "ส่วนลดพิเศษ 98.00" ถูกอ่าน (`OcrBillDiscount` ป้าย `ส่วนลด[ก-๙]*`) · ตัวจำแนก: B ✗ · C ✗ · **A ✓** (536 = 536) ⇒ ราคารวม VAT · `Document.DiscountAmount` = 0 (ทีม M) | เอกสารถูก · **แต่ 438.00 (เงินที่จ่ายจริง) หายจากระบบ** ⇒ ตอนจับคู่บัตร/ธนาคาร ส่วนต่าง 98 ไม่มีที่มา |
| U | 438.00 (ป้ายสุดท้าย "ยอดชำระ") | ข้อ 0 ของด่าน**เข้า** (500.93 − 98 + 35.07 = 438.00) ⇒ ตีว่า 500.93 เป็นยอด "ก่อนหักส่วนลด" · ตัวจำแนกเข้า **เคส E** (536 − 98 = 438) ⇒ กระจายส่วนลด 98 ลงบรรทัด ⇒ ฐาน 402.93 คู่ VAT 35.07 ⇒ `OcrAmountIntegrity` ข้อ 3 ฟ้อง (7% × 402.93 = 28.21) | **คำอธิบายผิดที่ลงตัวทางคณิต** — ส่วนลดหลัง VAT ถูกตีเป็นส่วนลดในใบกำกับ · ดังด้วย `[Σ-GAP]` แต่ข้อความชี้สาเหตุผิด ("อัตราผิด") |
| S | 5,024.00 | ส่วนลด 216.82 (แถว `(0.00)` ถูกข้าม — แถวยอด 0) · `NetSubTotal` = 4,695.33 · เคส C (4,912.16 − 216.82 ≈ 4,695.33 ±0.01) | น่าจะถูกอยู่แล้ว — **เป็นใบ "ห้ามแตะ"** ของรอบนี้ · ถ้ามา XML: `SubTotal = LineTotal`, `DiscountAmount = LineTotal − TaxBasis` (`OcrService.cs:8631–8637`) ก็ได้ผลเดียวกัน |

ข้อสังเกตข้างเคียง (ไม่แก้รอบนี้ · ล็อกด้วยเทสต์): ตัวอ่านค่าขนส่ง `OcrService.cs:9122` เติมบรรทัด "ค่าขนส่ง" **โดยไม่ต้องกระทบยอด** (ต่างจาก
service charge `:9160` ที่ต้องลงตัว) — ใบ U พิมพ์ "ค่าจัดส่ง +฿37" ใน**หมายเหตุหลังยอดรวม** รอดเพราะเครื่องหมาย `+` คั่นเท่านั้น ถ้า engine
ถอดเป็น "ค่าจัดส่ง ฿37" จะได้บรรทัดผี 37 บาท ⇒ ใส่ในชุดเทสต์ของ §6 (ทิศห้ามแตะ) และเป็น backlog ให้เดินด่านกระทบยอดแบบ `:9160`

---

## 2. ขั้นที่ 1 — หา "ยอดรวมทั้งสิ้น" (Total anchor)

### 2.1 นิยาม — ช่องแยก ไม่ conflate

| ช่อง | ความหมาย | ใช้ที่ | ใบ M | ใบ U | ใบ S |
|---|---|---|---|---|---|
| `TaxInvoiceTotal` | Σ ฐานหลังส่วนลดทุกกลุ่ม + VAT (+ ค่าอื่นที่**อยู่ในใบกำกับ**) — ยอดที่ VAT ถูกคิด | `Document.TotalAmount` · ฐาน §87 · ภ.พ.30 | 23,812.25 | 536.00 | 5,024.00 |
| `PreDiscountTotal` | ยอดก่อนหักส่วนลดที่อยู่ในใบ (บางใบติดป้าย "TOTAL") | ข้อมูลประกอบ/อธิบายผู้แพ้ | 24,110.00 | — | 4,912.15 (ก่อน VAT) |
| `AmountSettled` | เงินที่ผู้ซื้อจ่ายจริงสำหรับใบนี้ (หลังคูปองแพลตฟอร์ม/ค่าส่งภายนอก/หัก ณ ที่จ่าย/หักมัดจำ/ปัดเศษ) | จับคู่ชำระเงิน/ธนาคาร (ไม่ใช่ยอดใบกำกับ) | 23,812.25 | **438.00** | 5,024.00 |
| `CashTendered` / `Change` | เงินที่ยื่น / เงินทอน | **ห้าม**เป็นยอดรวม — ใช้ได้แค่ `Tendered − Change` | — | — | — |

เหตุผลที่ `Document.TotalAmount` = `TaxInvoiceTotal` เสมอ: VAT บนกระดาษถูกคิดจากยอดนี้ · §86/4 บังคับให้ยอดในใบกำกับเป็นยอดที่ลง
รายงาน §87 · ถ้าลง 438 คู่ VAT 35.07 = ฐานภาษีซื้อ ≠ ใบกำกับ (ข้อเท็จจริงเดียวกับที่ `OcrAmountIntegrity` ข้อ 3 จับได้)

### 2.2 แหล่งหลักฐาน — เรียงตามระยะห่างจากของจริง (DOCTRINE G1) และจัด "ชั้นอิสระ"

| ชั้น | ชนิดหลักฐาน (`OcrTotalEvidenceKind`) | บอกอะไร | ความแข็ง |
|---|---|---|---|
| X | `EtaxXml` (GrandTotalAmount ที่ลงนาม) | ข้อเท็จจริง — **short-circuit** (G2) | จบเลย |
| C | `VatClosure` — ฐานที่พิมพ์ + VAT ที่พิมพ์ = ผู้สมัคร (±0.02) หรือ VAT = ผู้สมัคร × 7/107 เมื่อทั้งใบมี VAT | **บทบาท** "ยอดที่ VAT ถูกคิด" = `TaxInvoiceTotal` | แข็งสุดเรื่องบทบาท |
| C | `VatSummaryTotal` — แถว "รวม" ของตารางสรุป VAT/รหัส ภ.พ. (Σ แถวกลุ่มต้องเท่ากันด้วย) | ยอดใบกำกับแยกกลุ่ม | แข็ง |
| W | `AmountInWords` (`ThaiAmountInWords.FindInText`) | "ยอดประเภทรวม" ที่ผู้ออกใบยืนยันสองรูปแบบ — **บทบาทต้องดูจากป้ายที่ติดกัน** (อาจเป็นยอดชำระหลังหัก ณ ที่จ่าย) | แข็ง |
| L | `LabelGrandTotal` (รวมทั้งสิ้น/จำนวนเงินรวมทั้งสิ้น/Grand Total/Total Amount) · `LabelNetPayable` (ยอดสุทธิ/NET AMOUNT/AMOUNT/ยอดชำระ/ที่ต้องชำระ) · `LabelPreDiscount` (บรรทัดที่ตามด้วยแถว "หักส่วนลด" แล้วแถวผลลัพธ์) · `LabelSubtotal` · `LabelDepositDeducted` | ป้ายบนกระดาษ | กลาง — **ป้ายเดียวไม่ชนะชั้น C** |
| P | `PaymentRow` (CC_PreAuth/บัตร/โอน/QR/"ยอดเงินรวม") · เงินสดต้องเป็น `Tendered − Change` | เงินที่จ่าย ⇒ `AmountSettled` | กลาง — ยืนยันยอดรวมได้เมื่อไม่มีการหักหลังใบกำกับ |
| S | `LineSum` (Σ บรรทัด ± ส่วนลดที่อ่านได้) | สอดคล้อง | อ่อน (บรรทัดอาจขาด — ใบ M) |
| E | `EngineInvoiceTotal` · `EngineAmountDue` · `AmountTriple` | ผลอ่านเครื่อง — **ไม่ใช่ชั้นอิสระจาก L** (engine อ่านป้ายเดียวกัน) | อ่อนสุด |

**กติกานับคะแนน** (G2 — ห้ามรวม confidence ข้ามชั้น): นับ "จำนวน**ชั้น**อิสระ" ที่ชี้ยอดเดียวกัน (L สองป้ายที่ยอดเดียวกัน = 1 ชั้น L · E ไม่นับ
ถ้ามี L ของยอดเดียวกันแล้ว) · ทุกการเทียบเงินใช้ ±0.02 (`ExactTol`) ไม่ใช่ ±1

### 2.3 ด่านโครงสร้าง (hard — ใช้ก่อนนับคะแนน)

1. **Chain-top** (สรุปรวมของ `OcrHeaderAmounts.IsSwapped`): ผู้สมัคร T ที่มีเลขที่พิมพ์ X อีกตัว โดย **T + VAT = X** (VAT > 0) ⇒ T ไม่ใช่
   `TaxInvoiceTotal` (เป็นฐาน) — ใบลักกี้เวย์ในชุด replay ป้าย "จำนวนเงินรวมทั้งสิ้น" ติดกับ 1,000 แต่ 1,000 + 70 = 1,070 ⇒ 1,000 ตก · **ต้องเรียก
   `OcrHeaderAmounts.Normalize` ตัวเดิม** ไม่เขียนเงื่อนไขซ้ำ
2. **Tender guard** (บทเรียน T2-09 Makro 951/49/1,000): แถวเงินสดรับ/เงินทอนเป็นผู้สมัครเดี่ยวไม่ได้ · ใช้ยืนยันได้แค่ `Tendered − Change`
3. **แถวยอด 0 ไม่ใช่หลักฐาน** (RG-02) — "หักเงินมัดจำ 0.00" · "Coupon 0.00" ไม่ทำให้ยอดใดกลายเป็น `AmountSettled`
4. **มัดจำที่มีเงิน** (`OcrDepositMarker.Decide` เป็นเจ้าของ) ⇒ ชั้นนี้คืน `Unknown` เรื่องบทบาท "ยอดหลังหักมัดจำ" ไม่ตัดสินแทน

### 2.4 ตัดสิน (`OcrTotalVerdict`)

| ค่า | เงื่อนไข | การกระทำ |
|---|---|---|
| `NotChecked` | ไม่มีข้อความ / มาจาก e-Tax XML | ไม่แตะ (XML ชนะ) |
| `Unknown` | ไม่มีผู้สมัครผ่านด่านโครงสร้าง | ไม่แตะ · `[TOTAL-UNSURE]` (เหลือง) |
| `Probable` | ชั้นอิสระ 1 ชั้น หรือ L+E เท่านั้น และไม่มีผู้ค้าน | ไม่แตะค่า engine · trace เท่านั้น (**พฤติกรรมเดิม**) |
| `Proven` | ≥ `ProvenMinClasses` (=2) ชั้น **โดยต้องมี C หรือ W อย่างน้อย 1** **และ**ผู้สมัครที่แพ้ทุกตัวที่มี ≥1 ชั้น **ถูกอธิบายบทบาทได้** (T + ส่วนลด = X ⇒ `PreDiscountTotal` · T − ปรับหลังใบ = X ⇒ `AmountSettled` · T − VAT = X ⇒ ฐาน) | เขียน `TotalAmount` = T ได้ (ดู §4) · conf 0.95 · ผู้แพ้ลง trace (G4) |
| `Conflict` | ผู้สมัคร ≥2 ตัวต่างมี C/W และอธิบายกันไม่ได้ | **ไม่แตะค่า** · `[TOTAL-CONFLICT]` (ห้ามอนุมัติเอง) · conf ≤ 0.80 |

ใบ M: 23,812.25 = C(VatClosure 22,663.97+1,148.28) + C(VatSummaryTotal) + W + L(AMOUNT/NET AMOUNT) + P(CC_PreAuth) ⇒ 5 ชั้น · 24,110.00 = L(TOTAL)
1 ชั้น อธิบายได้ด้วย −297.75 (มีป้าย "หักส่วนลด") ⇒ **Proven 23,812.25** · ใบ U: 536.00 = C(500.93+35.07 · 536×7/107=35.07) + W + L + S ⇒ Proven ·
438.00 = L("ยอดชำระ") + P ⇒ อธิบายเป็น `AmountSettled` (536 − 98 · ป้าย "ส่วนลดพิเศษ" อยู่**หลัง**ป้ายยอดรวมทั้งสิ้นและหลัง VAT) — **ไม่ใช่ผู้ค้าน**

---

## 3. ขั้นที่ 2 — แตกยอดให้รวมได้ Total ที่ยึดไว้ (Decomposition)

สมการกลาง: `TaxInvoiceTotal = Σ_g (Gross_g − Disc_g) + VAT + OtherInside` โดย g ∈ {ยกเว้น §81 · 0% §80/1 · 7%} และ
`VAT = round(0.07 × Net_7)` (ยอมเศษ ±max(0.02, 0.01×จำนวนบรรทัดมี VAT) — ค่าเดียวกับ `OcrAmountIntegrity`) ·
`AmountSettled = TaxInvoiceTotal + PostInvoiceAdjustment`

### 3.1 สมมติฐาน (แต่ละแบบ "ทำนาย" ตัวเลขที่ควรพิมพ์บนกระดาษ)

| H | ชื่อ (`OcrDiscountPlacement`) | ทำนาย | ใบตัวอย่าง | map เข้าตัวจำแนกเดิม |
|---|---|---|---|---|
| H0 | ตารางสรุปกลุ่มเท่านั้น | Σ แถวกลุ่ม = แถวรวม = T · VAT กลุ่ม 7% = 7% ฐาน · VAT กลุ่มยกเว้น = 0 | M | ไม่มีรายการ ⇒ บรรทัดสรุปรายกลุ่ม (§4) |
| H1 | `None` | ฐาน + VAT = T | Wine Pro · Makro 951/49/1,000 · ส่งออก | B หรือ A |
| H2 | `PreVatPerGroup` (ส่วนลดก่อน VAT) | Gross − Disc = ฐาน · 7% × ฐาน = VAT | ร้านวัสดุ · S (แยกรายกลุ่ม) | C |
| H3 | `InclVat` (ส่วนลดในโลกราคารวม VAT) | GrossIncl − Disc = T · VAT = T₇ × 7/107 | ซูเปอร์ · M (297.75) | E |
| H4 | `PostInvoice` (ปรับหลัง VAT/หลังยอดใบกำกับ) | VAT = T × 7/107 **ก่อน**หัก · T − Adj = ยอดชำระ · ป้ายปรับอยู่**หลัง**ป้ายยอดรวมทั้งสิ้น | U (−98 = +37 −135) · หัก ณ ที่จ่าย (`service-wht-printed`) | A/B โดยส่ง discount = 0 |
| H5 | ป้ายสลับ | `OcrHeaderAmounts.Normalize` (ตัวเดิม) | ลักกี้เวย์ | เดิม |

### 3.2 วิธีเลือก (ตัวเลขบนกระดาษเป็นกรรมการ)

1. ตัวเลขที่พิมพ์ = ชุด P (เฉพาะที่ติดป้ายยอด/ส่วนลด/VAT/กลุ่ม — ไม่รวมราคาต่อหน่วย) · ต่อ H: `Explained` = จำนวนใน P ที่ H ทำนายตรง ±`ExactTol`
   · `Assumptions` = จำนวนค่าที่ H ต้องสมมุติโดยไม่มีบนกระดาษ (เช่นแยกยอดยกเว้นเองโดยไม่มีตาราง = 1)
2. เลือก Explained สูงสุด → เสมอ: Assumptions ต่ำสุด → ยังเสมอ: **แบบที่ตรงกับเคสของ `OcrLineReconciler` เดิม** (เสถียรกว่า = ไม่ถดถอย)
3. **ต้องผ่านขั้นต่ำ**: อธิบาย T และ VAT ได้ทั้งคู่ · ไม่ผ่าน = `Unknown` ⇒ ไม่ส่งอะไรใหม่ให้ขั้น 3 (ไหลตามเส้นเดิม) · แท็ก `[Σ-GAP]` ข้อความ
   "ยอดรวมทั้งสิ้น T ยึดแล้วจาก … แต่แตกเป็น ฐาน+VAT ไม่ลงตัวกับตัวเลขบนกระดาษ: …(รายการตัวเลขที่อธิบายไม่ได้)" — **ไม่แต่งตัวเลข**
4. H ที่ต่างกันแต่ Explained เท่ากันและให้ **ฐาน/VAT ต่างกัน** ⇒ `Unknown` (ไม่เดาทิศ — G3)

ตัวอย่างเทียบ (U, ถ้าไม่มี H4): H3 ทำนาย VAT = 438 × 7/107 = 28.65 ≠ 35.07 ✗ · H4 ทำนาย VAT = 35.07 ✓ ฐาน 500.93 ✓ ยอดชำระ 438 ✓ ⇒ H4 ชนะขาด
(นี่คือเหตุผลที่ข้อ 0 ของด่านคณิตกับเคส E วันนี้ "ลงตัวแต่ผิดเรื่อง" — ทั้งสองไม่ได้ถามว่า VAT ถูกคิดจากยอดไหน)
ใบ M: H0 อธิบาย 6,260.00 · 16,403.97 · 1,148.28 · 17,552.25 · 22,663.97 · 23,812.25 (+ 0.00) และ H3 อธิบาย 24,110 − 297.75 เพิ่ม ⇒ ผลรวม H0+H3:
ส่วนลด 297.75 **อยู่ในยอดกลุ่มแล้ว** (ตารางเป็นยอดหลังหัก) ⇒ `InsideDiscount` = 297.75 แต่ **ห้ามกระจายซ้ำ** (ส่ง discount = 0 ให้ขั้น 3)
ใบ S: H2 รายกลุ่ม — ยกเว้น 0.00 − (0.00) = 0.00 · 7%: 4,912.15 − 216.82 = 4,695.33 · 7% = 328.67 · รวม 5,024.00 ⇒ Explained ทุกตัว

---

## 4. ขั้นที่ 3 — บรรทัด + จุดต่อสาย (ไม่รื้อ)

### 4.1 ขั้น 3 ใช้ของเดิม แค่ป้อน input ที่ถูกความหมาย
- มีรายการ ⇒ `OcrLineReconciler.Classify(grossSum, headerSubTotal: NetAfterDiscount, headerVat, headerTotal: TaxInvoiceTotal, headerDiscount: InsideDiscountToSpread)`
  — H4 ส่ง `0` (U ⇒ เคส A) · H0 ส่ง `0` (ยอดกลุ่มหลังลดแล้ว) · H2/H3 ส่งส่วนลดตามเดิม · ตัวกระจายส่วนลดเดิม (`OcrService.cs:6606`) ไม่แตะ
- อัตรารายบรรทัด: `OcrLineVatMarks.Assign` ก่อน (เดิม) · ถ้ากระดาษมีกลุ่ม (M/S) แต่ไม่มีสัญลักษณ์ — **ไม่เพิ่มตัวเดา subset-sum รอบนี้** (backlog P2:
  ใช้ได้เฉพาะคำตอบ**เดียว**ที่ตรงถึงสตางค์ · n ≤ 20) ⇒ ปล่อยให้ `OcrAmountIntegrity` ข้อ 3 ฟ้องพร้อมข้อความ "กระดาษแยกไว้: ยกเว้น X · มี VAT Y" (ส่งยอดกลุ่มเข้า `paperTaxable/paperNonTaxable` ที่มีอยู่แล้ว)
- **ไม่มีรายการ + กลุ่มพิสูจน์แล้ว (H0)** — ของใหม่ชิ้นเดียว: แทนบรรทัดสรุป 7% ใบเดียว (`OcrService.cs:6814`) ด้วย **บรรทัดสรุปต่อกลุ่ม**
  ("สินค้ายกเว้น VAT ตามรหัส ภ.พ. 1 · 17 ชิ้น" 6,260.00 อัตรา −1 · "สินค้ามี VAT รหัส 2 · 134 ชิ้น" 16,403.97 @7% VAT 1,148.28) — ทุกเลขพิมพ์บนกระดาษ
  ⇒ `OcrAmountIntegrity` ผ่านเอง · ไม่มีกลุ่ม ⇒ บรรทัดเดียวเหมือนเดิม
- **ชุดหน้าไม่ครบ**: กระดาษพิมพ์ "หน้า 3 จาก 3 / Page 3 of 3" แต่ไฟล์มี 1 หน้า (Azure `PageCount` มีอยู่ `OcrService.cs:3508` แต่ไม่ถูกเก็บ) ⇒
  `[PAGES-PARTIAL] รายการอยู่หน้า 1–2 ที่ไม่ได้อัปโหลด — ยอดรวม/VAT ยืนยันจากตารางสรุปแล้ว แต่ผังบัญชีรายสินค้าแยกไม่ได้` (เหลือง · จะบล็อกไหม = คำถามเจ้าของ §8)
  · มีรายการบางส่วน ⇒ เคส D `LinesShort` เดิม (ห้ามแต่งบรรทัด)

### 4.2 จุดต่อสาย (ลำดับไปป์ไลน์จริง)

| # | จุด | ทำอะไร | แทน/ป้อน |
|---|---|---|---|
| W0 | `OcrService.cs:3463` + `OcrExtractedData` | เก็บ `EngineAmountDue` เป็นช่องใหม่ (ไม่เปลี่ยน `TotalAmount = InvoiceTotal ?? AmountDue`) | ป้อน anchor เท่านั้น |
| W1 | `OcrService.cs` หลังตรวจวันที่ (`:587–619`) **ก่อน**ด่านคณิต `:655` | `OcrTotalAnchor.Find(...)` · `Proven` + engine Total ≠ T ⇒ เขียน `TotalAmount`/conf 0.95/`Note(PaperLabel, หลักฐาน)`/trace `[TOTAL]` · `Conflict` ⇒ ต่อ `ProcessingNotes` `[TOTAL-CONFLICT]` · อื่น ๆ ไม่แตะ | **ป้อน** ด่านคณิต (ตัวด่านไม่แก้) · `CrossCheckAmountInWords` (`SmartFieldExtractor.cs:125`) คงเดิม — anchor ใช้ผลเดียวกัน |
| W1b | ตำแหน่งเดียวกัน | ถ้า `Decompose` = Fits: SubTotal/VAT ของหัวใบที่ขัดกับการแตกยอด (เช่น SubTotal = 24,110 − VAT) ⇒ เขียนตามการแตกยอด **เฉพาะเมื่อทุกค่าพิมพ์บนกระดาษ** | ป้อน gateway ข้อ 0/2/2b/3 |
| W2 | `BuildScanLinesAsync` ต้นเมธอด `OcrService.cs:6548` (ข้าง `OcrLineVatMarks.Read`) | เรียก anchor+decomposer ซ้ำจาก `RawTextContent` + ค่าที่ persist (สแกนเก่าได้ประโยชน์โดยไม่ migrate — แบบเดียวกับ `OcrLineVatMarks`) ⇒ ได้ `hdrDiscToSpread` · กลุ่มสำหรับบรรทัดสรุป · ยอดกลุ่มสำหรับ `AppendAmountIntegrityGaps` | ป้อน `OcrLineReconciler.Classify` `:6601` · สาขาไม่มีรายการ `:6814` |
| W3 | `ResolveHeaderSubTotal` `:6984` (3 ผู้เรียก: สร้าง `:6015` · พรีวิว `:6942` · repopulate `:7092`) | Fits ⇒ คืน `NetAfterDiscount` · ไม่ใช่ ⇒ `NetSubTotal` เดิม | ป้อน |
| W4 | `OcrScanResult` (ช่องใหม่ phase 2) | `ExtractedAmountSettled` + `[PAY≠TOTAL]` note — phase 1 ใช้ note อย่างเดียว (ไม่แตะ schema) | ใหม่ |

**ที่ต้องเหมือนเดิมทุกประการ**: `OcrHeaderAmounts.IsSwapped/Normalize` · `AmountTripleExtractor` + ด่าน agreesWithEngineTotal (`SmartFieldExtractor.cs:921`) ·
`OcrBillDiscount.Read` · `OcrLineReconciler` เคส A–E · ตัวกระจายส่วนลด · `OcrLineVatMarks.Read/Assign` · `OcrAmountIntegrity.Check` · เส้น e-Tax XML ·
`ApplyExternalAmountOverrides` (`:806` — ค่าพาร์ทเนอร์ชนะ anchor เพราะรันทีหลัง) · `PaperWhtReader` (หัก ณ ที่จ่ายเป็น `AmountSettled` ไม่ใช่ยอดรวม)

---

## 5. ล็อกถดถอย — คำตอบที่ต้อง "เหมือนเดิม" (รัน `OcrReplayHarness.DiffTable` ก่อน/หลัง · ทุกแถวที่เปลี่ยนต้องอธิบายได้ — CLAUDE.md #4 H)

| ใบ | ยอดรวม (ก่อน = หลัง) | anchor ที่ต้องได้ | ทำไมไม่ถูกแตะ |
|---|---|---|---|
| Wine Pro | 3,593.00 | Proven (L Total · P Card · C แถว V 3,357.94+235.06) | engine ตรงอยู่แล้ว ⇒ ไม่เขียน |
| ร้านวัสดุ 5% | 1,418.02 | Proven (W · L · C 1,325.25+92.77) · H2 | discount ส่งต่อเหมือนเดิม ⇒ เคส C |
| ซูเปอร์ลดสมาชิก | 735.30 | Proven (L ยอดสุทธิ · C 687.20+48.10) · H3 · 774 = PreDiscount | เคส E เหมือนเดิม |
| ค้าส่ง V/N | 792.00 | Proven · H1 + กลุ่มจาก `ยอดที่ต้องเสีย/ไม่ต้องเสียภาษี` | `OcrLineVatMarks` ยังเป็นคนติดอัตรา |
| Makro 951/49/1,000 | 1,000.00 | Proven (L · C 951+49) | ไม่มีแถวเงินทอน · tender guard กันเวอร์ชัน T2-09 |
| ลักกี้เวย์ป้ายสลับ | 1,070.00 (หลัง Normalize) | chain-top ตัด 1,000 · Probable/Proven 1,070 | ใช้ `Normalize` ตัวเดิม |
| IKEA ราคารวม VAT | ตามเดิม | Proven (C: VAT = T × 7/107) | เคส A |
| service-wht-printed | 10,700.00 | Proven · 10,400 = `AmountSettled` (H4 หัก ณ ที่จ่าย) | **ห้าม**กลายเป็น 10,400 |
| deposit-form-row-zero | 1,000.00 | แถว 0.00 ไม่ใช่หลักฐาน | RG-02 |
| export 0% | 100,000.00 | Proven (C: VAT 0 · ฐาน = T) | — |

---

## 6. API (pure · `Helpers/` · ไม่มี I/O · ไม่ throw · เกณฑ์เป็น `public const` — DOCTRINE G6)

```csharp
public enum OcrTotalVerdict { NotChecked = 0, Unknown = 1, Probable = 2, Proven = 3, Conflict = 4 }
public enum OcrTotalRole { Unknown = 0, TaxInvoiceTotal, PreDiscountTotal, NetBeforeVat, AmountSettled,
                           CashTendered, Change, DepositDeducted, WhtDeducted, GroupTotal }
public enum OcrTotalEvidenceKind { EtaxXml, VatClosure, VatSummaryTotal, AmountInWords, LabelGrandTotal,
                                   LabelNetPayable, LabelPreDiscount, LabelSubtotal, LabelDepositDeducted,
                                   PaymentRow, LineSum, EngineInvoiceTotal, EngineAmountDue, AmountTriple }
public readonly record struct OcrTotalEvidence(OcrTotalEvidenceKind Kind, decimal Amount, string PaperText, int LineNo);
public sealed record OcrTotalCandidate(decimal Amount, OcrTotalRole Role, IReadOnlyList<OcrTotalEvidence> Evidence,
                                       int IndependentClasses, string? ExplainedAs);   // "24,110.00 − ส่วนลด 297.75"
public readonly record struct OcrEngineAmounts(decimal? SubTotal, decimal? Vat, decimal? InvoiceTotal,
                                               decimal? AmountDue, bool FromSignedXml);
public sealed record OcrTotalAnchorResult(OcrTotalVerdict Verdict, decimal? TaxInvoiceTotal, decimal? AmountSettled,
    decimal? PreDiscountTotal, IReadOnlyList<OcrTotalCandidate> Candidates, decimal Confidence, string Reason);

public static class OcrTotalAnchor
{
    public const int ProvenMinClasses = 2;
    public const decimal ExactTol = 0.02m;
    public const decimal ProvenConfidence = 0.95m, ConflictConfidenceCap = 0.80m;
    public static OcrTotalAnchorResult Find(string? rawText, OcrEngineAmounts engine,
        IReadOnlyList<decimal>? lineAmounts, decimal? billDiscount /* OcrBillDiscount.Read */);
}

public enum OcrDiscountPlacement { Unknown = 0, None, PreVatPerGroup, InclVat, PostInvoice, SummaryOnly }
public enum OcrVatGroupKind { Unknown = 0, Standard7, ZeroRated, Exempt }
public sealed record OcrVatGroup(OcrVatGroupKind Kind, string? PaperCode, decimal Gross, decimal Discount,
                                 decimal Net, decimal Vat, int? ItemCount, string Evidence);
public enum OcrDecompositionVerdict { NotChecked = 0, Unknown = 1, Fits = 2 }
public sealed record OcrTotalDecomposition(OcrDecompositionVerdict Verdict, OcrDiscountPlacement Placement,
    decimal NetAfterDiscount, decimal Vat, decimal InsideDiscount, decimal DiscountToSpread,
    decimal PostInvoiceAdjustment, IReadOnlyList<OcrVatGroup> Groups,
    int Explained, int Assumptions, IReadOnlyList<decimal> UnexplainedPrinted, string Reason);

public static class OcrTotalDecomposer
{
    public static OcrTotalDecomposition Decompose(OcrTotalAnchorResult anchor, string? rawText,
        decimal? paperSubTotal, decimal? paperVat, decimal? billDiscount, OcrPaperVatSplit vatSplit,
        IReadOnlyList<OcrVatGroup> groups);
}

// เพิ่มใน OcrLineVatMarks (Read/Assign เดิมไม่แตะ): ตารางกลุ่มทั้งแบบ "V 7 ฐาน VAT รวม" และแบบ Makro
// "จำนวนชิ้น รหัส ฐาน VAT รวม" + คำอธิบาย "1=…ยกเว้น… 2=…ต้องเสียภาษี…" · ความหมายรหัสต้องมาจากคำอธิบาย
// บนกระดาษ หรือพิสูจน์ด้วย VAT = 0 / VAT = 7% ฐาน · Σ แถว ≠ แถว "รวม" ⇒ คืนว่าง (ใช้ไม่ได้ทั้งตาราง)
public static IReadOnlyList<OcrVatGroup> ReadGroups(string? rawText);
```

## 7. เทสต์ (สองครึ่ง — DOCTRINE G7) · ข้อความกระดาษ M/U/S เพิ่มใน `OcrPaperSamples` (`MakroPage3of3` · `UptoyouShopee` · `ScommerceLazada`)

**ครึ่ง "พัง → ถูก"** (`OcrTotalAnchorTests` · `OcrTotalDecomposerTests` · `OcrLineVatMarksTests` +)
1. `Makro_ป้ายTOTALเป็นยอดก่อนลด_ยอดรวมคือ23812_25_Proven` (engine Total 24,110 ⇒ 23,812.25 · ผู้แพ้ ExplainedAs "−297.75")
2. `Makro_engineTotalว่าง_ได้23812_25จากตาราง+ตัวอักษร` · 3. `Makro_ReadGroups_รหัสตัวเลข_ยกเว้น6260_มีVAT16403_97`
4. `Makro_ไม่มีรายการ_บรรทัดสรุปสองกลุ่ม_Integrityผ่าน` · 5. `Makro_ส่วนลดอยู่ในยอดกลุ่มแล้ว_DiscountToSpreadเป็นศูนย์`
6. `Makro_หน้า3จาก3_PAGES_PARTIAL` · 7. `Uptoyou_ยอดใบกำกับ536_ยอดชำระ438_PostInvoice`
8. `Uptoyou_engineหยิบ438_anchorคืน536_ไม่เข้าเคสE` (ล็อกว่าคำอธิบาย "ส่วนลดในใบกำกับ" ถูกปฏิเสธเพราะ VAT = 536×7/107)
9. `Uptoyou_DiscountToSpreadศูนย์_ตัวจำแนกได้เคสA` · 10. `Scommerce_ส่วนลดรายกลุ่มก่อนVAT_Fits_ทุกตัวเลขอธิบายได้`
11. `Conflict_สองยอดต่างมีตัวอักษรและปิดVATได้_ไม่แตะค่า_TOTAL_CONFLICT` · 12. `ReadGroups_Σแถวไม่เท่าแถวรวม_คืนว่าง`

**ครึ่ง "ห้ามแตะ"** — ทุกใบใน §5 (Wine Pro · ร้านวัสดุ · ซูเปอร์ · ค้าส่ง · Makro 951/49/1,000 · ลักกี้เวย์ · IKEA · หัก ณ ที่จ่าย · มัดจำ 0 · ส่งออก)
ได้ `TotalAmount` เดิมและเคสตัวจำแนกเดิม + เคสเฉพาะ: `แถวเงินทอน_ไม่เป็นยอดรวม` · `ตัวอักษรตรงยอดชำระหลังหักณที่จ่าย_Totalยังเป็นยอดใบกำกับ` ·
`ป้ายเดียวไม่มีหลักฐานอื่น_Probable_ไม่เขียนทับ` · `EtaxXml_NotChecked` · `Scommerce_แถวส่วนลด0_00ไม่ใช่หลักฐาน` ·
`หมายเหตุค่าจัดส่งหลังยอดรวม_ไม่กลายเป็นบรรทัด` (ล็อกพฤติกรรม `:9122` วันนี้)
**Replay**: เพิ่ม 3 ใบ + ช่อง `AnchorTotal` · `AnchorVerdict` · `AmountSettled` · `DiscountPlacement` ใน `OcrReplayHarness.Run` · `OcrReplayGoldenTests`
แถวเดิม**ห้ามเปลี่ยน** (ช่องใหม่เป็นแถวใหม่เท่านั้น)

## 8. แท็ก / ข้อความหน้าจอ (ไทย · มีตัวเลขเสมอ · อ่านด้วย `parseScanNotes` ตัวเดียว)

| แท็ก | บล็อกอนุมัติเอง? | ตัวอย่างข้อความ |
|---|---|---|
| `[TOTAL]` | ไม่ (ข้อสังเกต) | "ยอดรวมทั้งสิ้น 23,812.25 (ยืนยัน 5 ทาง: ตัวอักษร · ตารางรหัส ภ.พ. · ฐาน+VAT · NET AMOUNT · แถวชำระ) — ป้าย 'TOTAL 24,110.00' คือยอดก่อนหักส่วนลด 297.75" |
| `[TOTAL-CONFLICT]` | **ใช่** (เพิ่มใน `OcrPostingReadiness.BlockingTags` `:23`) | "พบยอดรวมสองค่าที่ต่างมีหลักฐาน: 1,250.00 (ตัวอักษร) กับ 1,520.00 (ฐาน+VAT) — ตรวจกับกระดาษว่าอันไหนคือยอดรวมทั้งสิ้น" |
| `[TOTAL-UNSURE]` | ไม่ (เหลือง conf < 0.85) | "หายอดรวมทั้งสิ้นที่มีหลักฐานยืนยันไม่ได้ — ใช้ค่าที่เครื่องอ่าน 999.00" |
| `[PAY≠TOTAL]` | ไม่ | "ยอดตามใบกำกับ 536.00 · ยอดที่ชำระจริง 438.00 (ต่าง −98.00: 'ส่วนลดพิเศษ' หลังยอดรวม — หมายเหตุ ค่าจัดส่ง +37 · Shopee Voucher −135) — ลงเอกสารที่ 536.00; ส่วนต่างจัดการตอนบันทึกชำระ" |
| `[PAGES-PARTIAL]` | ตามคำตัดสินเจ้าของ | ดู §4.1 |
| `[Σ-GAP]` (เดิม) | ใช่ | เพิ่มรูปข้อความ "ยอดรวม T ยึดแล้ว แต่แตกเป็นฐาน+VAT ไม่ลงตัว: ตัวเลขที่อธิบายไม่ได้ …" |
ระวัง: ชื่อแท็กใหม่ต้องไม่เป็น substring ของแท็กที่บล็อก (`notes.Contains` ที่ `OcrPostingReadiness.cs:98`) — `[TOTAL]` ไม่อยู่ใน `[TOTAL-CONFLICT]` ✓

## 9. คำถามเจ้าของ (ผลต่างกันคนละเรื่อง — ไม่เดาแทน)
1. **ส่วนต่าง 98 ของใบ U ลงบัญชีอย่างไร?** (ก) เอกสาร 536 · ตอนจ่าย 438 ⇒ คูปองแพลตฟอร์ม 135 เป็นรายได้อื่น/ส่วนลดรับ (ไม่ลดฐาน VAT เพราะไม่ใช่ส่วนลดจากผู้ขาย) ·
   ค่าจัดส่ง 37 เป็นค่าใช้จ่ายไม่มีใบกำกับ (ภาษีซื้อเคลมไม่ได้) (ข) ตัดส่วนต่างสุทธิ −98 เข้าบัญชีเดียว (ค) ให้ผู้ใช้เลือกทุกครั้ง — ต้องมีผังบัญชีปลายทางก่อนทำ W4
2. **ใบที่หน้าไม่ครบ (M หน้า 3/3)** บล็อกการอนุมัติอัตโนมัติไหม? ยอด/VAT พิสูจน์ได้ แต่ต้นฉบับ §87/3 ต้องเก็บครบทุกหน้า
3. **บรรทัดสรุปรายกลุ่ม VAT** (แทนบรรทัดเดียว 7%) ยอมรับเป็นรายการในเอกสารซื้อไหม — ถ้าไม่ ผู้ใช้ต้องแยกเองแต่ระบบบอกตัวเลขกลุ่มให้
4. เก็บ `AmountSettled`/`EngineAmountDue` เป็นคอลัมน์ (ต้อง migration + DTO echo ตาม #4 B) หรือเป็น note พอ

## 10. ลำดับทำ + สิ่งที่ doc ต้องขยับ (ทีมห้ามแตะ — main agent รวม)
ลำดับ: (1) samples M/U/S + เทสต์ครึ่ง "ห้ามแตะ" + replay golden **ก่อน**แตะโค้ด (2) `OcrLineVatMarks.ReadGroups` (3) `OcrTotalAnchor` (4) `OcrTotalDecomposer`
(5) W2/W3 (ฝั่งสร้าง — กลับได้ง่าย) (6) W0/W1 (ฝั่งสแกน — เขียนทับยอด) แยกคอมมิต · ทุกขั้นรัน `DiffTable`
**DOCUMENT_FLOW.md** ทางเข้า OCR: ขั้นยึดยอดรวม + นิยาม TaxInvoiceTotal/AmountSettled + แท็กใหม่ · **TEST_PLAN.md** ไฟล์เทสต์ใหม่ + §0 ·
**OCR_PIPELINE_REVIEW** backlog: ตัวอ่านค่าขนส่ง `:9122` ไม่กระทบยอด · `AmountDue` ถูกทิ้ง `:3463` ·
**docs/lessons/ocr-pipeline.md** บทเรียน: *"ลงตัวทางคณิตไม่พอ ต้องถามว่า VAT ถูกคิดจากยอดไหน"* (ใบ U เข้าข้อ 0 ของด่านและเคส E ได้ทั้งที่ผิดเรื่อง) ·
kill-switch: ชั้นนี้ไม่เรียก AI เลย (ผ่านโดยโครงสร้าง) · ถ้ารอบหน้าจะให้ `OcrReviewGuard` (`:131`) ปฏิเสธยอดจาก AI ที่ขัด anchor `Proven` = งานแยก
