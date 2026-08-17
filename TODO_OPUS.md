# TODO_OPUS.md — งานคงเหลือรวมศูนย์ (รอทีม Opus)

> รวบรวมจาก: (1) feedback ผู้ใช้รอบล่าสุด (หน้า import สรรพากร + Excel + ใบลดหนี้ +
> ป้ายประเภทเอกสาร) — **วิเคราะห์แล้ว ยังไม่ได้แก้** (2) backlog ภาษี 14 ข้อจาก
> audit 3 ทีม (DOCUMENT_FLOW.md รอบ 29) (3) งานค้าง Task Force รอบก่อน
>
> กติกา: ทำตาม CLAUDE.md ทุกข้อ (กฎเหล็ก #1-#4) · ทุกข้อที่แตะเงิน/ภาษี ต้องมี
> เทสต์/simulation ในคอมมิตเดียวกัน · แก้ flow แล้วอัปเดต DOCUMENT_FLOW.md ทันที
> · env นี้ไม่มี .NET SDK — รัน checker ทั้ง 5 ตัว (tools/) + node --check แทน

---

## A. งานใหม่จาก feedback ล่าสุด (วิเคราะห์เสร็จ พร้อมลงมือ)

### A1. 🔴 ไฟล์ e-Filing ภงด.3/53 (.txt) ให้ import เข้าระบบสรรพากรได้ตรง ๆ

**ปัญหา:** ไฟล์ปัจจุบัน (`TaxFilingExportService.cs` → `ExportPnd3Async` ~:98,
`ExportPnd53Async` ~:144) มีบรรทัด `H|...` header และ `T|...` trailer ซึ่ง
**หน้า import ของสรรพากรไม่ต้องการ** และคอลัมน์ detail ไม่ตรง layout ที่หน้า
import คาด (ผู้ใช้ต้อง map มือและบางช่องว่าง)

**สิ่งที่หน้า import RD คาด (จาก screenshot ผู้ใช้ — ยืนยันกับหน้าจริงอีกครั้งตอนทำ):**
- **ไม่มี header/trailer** — detail rows ล้วน, pipe-delimited
- ลำดับคอลัมน์ที่ map สำเร็จบนหน้า RD:
  `Col5` = ชื่อสกุล (บุคคลธรรมดา; นิติบุคคล = ว่าง), `Col6` = วันเดือนปีที่จ่าย
  รูปแบบ **`ddMMyyyy` ค.ศ. ไม่มีตัวคั่น** (เช่น `01072026`), `Col7` = ประเภท
  เงินได้ (200), `Col8` = จำนวนเงินได้ (15,2 → `15000.00`), `Col9` = อัตราภาษี
  (4,2 → `3.00`), `Col10` = จำนวนภาษีที่หัก (15,2 → `450.00`), และช่อง
  "เงื่อนไขการหักภาษี (1)" (1=หัก ณ ที่จ่าย, 2=ออกให้ตลอดไป, 3=ออกให้ครั้งเดียว)
- อนุมาน Col1-4: ลำดับ | เลขผู้เสียภาษี 13 หลัก | สาขา (53) หรือคำนำหน้า (3) |
  ชื่อ (นิติ=ชื่อบริษัทเต็ม / บุคคล=ชื่อตัว) — **ต้อง cross-check กับหน้า import
  จริง** (หน้า RD มีช่องที่อยู่แยก ~13 ช่องให้ map เพิ่มได้ — optional)
- วันที่ปัจจุบันเราเขียน `dd/MM/ปีพ.ศ.` มี slash → ต้องเป็น `ddMMyyyy` ค.ศ.
  (หน้า RD มี radio พ.ศ./ค.ศ. — ผู้ใช้เลือก ค.ศ.)

**งาน:** ปรับ `ExportPnd3Async` + `ExportPnd53Async` เป็น detail-only layout ตามบน
และให้ `BuildPndAsync` (`TaxService.EFiling.cs` ~:110) ใช้ layout ชุดเดียวกัน
(สองทางออกไฟล์ต้องได้ไฟล์เดียวกัน) + เทสต์ format (ห้ามมี H|/T|, วันที่ 8 หลัก,
ทศนิยม 2 ตำแหน่ง, นิติ → Col5 ว่าง)

### A2. 🔴 แถว "⚠️ ยังไม่ออกหนังสือรับรอง" ต้อง default = ไม่ใช้ (IsExcluded)

**เคสจริง:** Excel ภงด. ลำดับ 21, 27 — เอกสารที่หนังสือรับรองถูก**ยกเลิก**/ยังไม่
ออก กลับมีสถานะ "ใช้" นับเข้ายอดนำส่ง ⇒ ยอดบนจอ**เกิน**ไฟล์ e-Filing (ไฟล์นับ
เฉพาะ cert Issued/Printed — ถูกแล้ว)

**งาน:** supplement rows ใน `GenerateWhtReport` (`TaxService.cs` ~:1671-1725
ทั้งสาขารายบรรทัดและสาขา doc-level) ตั้ง `IsExcluded = true` ตั้งแต่ generate +
เปลี่ยนข้อความเป็น "⚠️ ยังไม่ออกหนังสือรับรอง — ออกใบที่หน้าหนังสือรับรองแล้วกด
'สร้างใหม่'". ยอดหัว/บรรทัด [สรุป] กรอง excluded อยู่แล้ว (แก้รอบ 28c) จึง
สอดคล้องทันที: **ยอดจอ = ยอดไฟล์ = ยอดทะเบียน cert เสมอ** และนักบัญชีติ๊กกลับ
เข้ามือได้เมื่อจงใจ. เพิ่มเทสต์: supplement row เกิดมา excluded / ยอดหัวไม่รวม.

### A3. 🟠 ใบลดหนี้: ช่อง "เลขที่ใบลดหนี้" + แสดงในรายงานภาษี (เฉพาะ CN มี VAT)

**วิเคราะห์:**
- ฝั่งขาย (CN เราออก): เลขใบลดหนี้ = `DocumentNumber` ของ CN เอง — ตรวจ
  enrichment `GetTaxReportAsync` (`TaxService.cs` ~:2400 บริเวณ `invNo =
  isInput ? SupplierInvoiceNumber : DocumentNumber`) ว่าแถว CN ฝั่งขายแสดงเลข
  CN แล้วจริง (คาดว่าใช่ — verify)
- ฝั่งซื้อ (CN รับจากผู้ขาย): **ไม่มีช่องกรอกเลขใบลดหนี้ของผู้ขาย** — ปัจจุบันมีแต่
  `fCnExternalRef` (documents.html:378) ซึ่งคือ "เลขใบกำกับ**เดิม**ที่ถูกลด"
  (สำหรับ gate §86/10) คนละความหมาย. ช่อง `fSupplierInvoiceNumber`
  (documents.html:570) แสดงเฉพาะ PI/Expense — ไม่โชว์บน CN
- Field รองรับมีแล้ว: `Document.SupplierInvoiceNumber` (entity + Create/Update/
  Response ครบ — ไม่ต้อง migrate)

**งาน:** (1) ฟอร์ม CN ฝั่งซื้อ (side=expense) แสดงช่อง "เลขที่ใบลดหนี้จากผู้ขาย"
→ เก็บลง `SupplierInvoiceNumber` + hydrate ตอนแก้ (2) enrichment รายงานภาษีซื้อ:
แถว CN ฝั่งซื้อใช้ `SupplierInvoiceNumber` เป็นเลขที่ใบกำกับ (แทนเลข CN ภายใน
ของเรา) (3) เงื่อนไข: มีผลเฉพาะ CN ที่ `VatAmount != 0` (4) gate อนุมัติ CN ซื้อ
ที่มี VAT: เตือนถ้าเลขใบลดหนี้ผู้ขายว่าง (soft warning — §86/10 รายงานต้องอ้าง
เลขใบลดหนี้จริงของผู้ออก) (5) renderer ×2 ถ้าพิมพ์เลขนี้บนกระดาษ + TEST_PLAN

### A4. 🟠 ป้าย "ประเภทเอกสาร" ทั้งระบบ แสดงตามหัวกระดาษ

**เป้า:** ทุกที่ที่โชว์ชนิดเอกสาร (list หน้า documents, detail, dropdown อ้างอิง,
รายงาน) แสดงตามหัวจริง: `TaxInvoice+combined` → "ใบแจ้งหนี้/ใบกำกับภาษี",
`+cash` → "ใบกำกับภาษี/ใบเสร็จรับเงิน", `+declined` → "ใบเสร็จรับเงิน",
`TaxInvoice` เปล่า → "ใบกำกับภาษี", จ่ายครบไม่มีใบเสร็จแยก (ServedAsReceipt) →
"ใบกำกับภาษี/ใบเสร็จรับเงิน"

**งาน:** (1) helper กลางตัวเดียว `Layout.docHeaderLabel(doc)` ใน layout.js —
รับ object ที่มี documentType + combinedInvoiceTaxInvoice + issuedAsCashReceipt
+ buyerDeclinedTaxInvoice (DocumentResponse echo ครบแล้ว) (2) ServedAsReceipt
คำนวณตอน render ฝั่ง server — ถ้าจะให้ list รู้ ต้อง expose flag ใน
DocumentResponse (field ใหม่ → แตะ record ตามลำดับกฎเหล็ก #4 B: Response +
mapper — read-only ไม่ต้องแตะ Create/Update) (3) sweep จุดที่ใช้
`Layout.docTypeLabel(d...)` กับ **เอกสารทั้งใบ** (มี object) ให้เรียก helper ใหม่
— จุดที่มีแต่ enum string คงเดิม (4) ตัวกรอง "ประเภท" หน้า list อาจเพิ่มตัวเลือก
หัวรวม (filter ด้วย flag) — เฟสถัดไปได้

### A5. 🔴 ทางรับเงินต้อง "ทางเดียวต่อสถานการณ์" — บันทึกชำระเงิน vs แปลงเป็นใบเสร็จ

**เคสจริงที่ผู้ใช้งง (2 ชั้น):**

1. กด "บันทึกชำระเงิน" บนใบ **ใบแจ้งหนี้/ใบกำกับภาษี** (combined) แล้วเจอติ๊ก
   'ใบกำกับนี้จะเป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน" ในตัว...' → ผู้ใช้ไม่รู้ว่า
   **จะมีเอกสารใหม่เกิดไหม** และข้อความหัวไม่ตรงใบ combined (จ่ายครบแล้วหัวจริง
   คือ 3-in-1 "ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน" ไม่ใช่ "ใบกำกับภาษี/ใบเสร็จ
   รับเงิน") — ข้อความอยู่ `documents.html:8429` (static ไม่ดู combined flag)
2. มี **2 ปุ่มที่จบเรื่องเดียวกัน**: "บันทึกชำระเงิน" กับ "แปลงเอกสาร → ใบเสร็จ
   รับเงิน" (backend `ValidConversions`: Invoice/TaxInvoice → Receipt/RV —
   `DocumentService.cs:7186-7193`)

**ความต่างเชิงกลไก (ยืนยันจากโค้ดแล้ว):**

| | 💰 บันทึกชำระเงิน (CreatePaymentAsync) | 🔁 แปลง → ใบเสร็จ (Convert+Approve) |
|---|---|---|
| แนวคิด | "เงิน-first" — บันทึกการรับเงิน ใบเสร็จเป็นผลพลอยได้ | "กระดาษ-first" — สร้างเอกสารใบเสร็จก่อน เงินลงตอนอนุมัติ |
| JE | Dr เงิน / Cr ลูกหนี้ ต่องวด | settlement JE ตอนอนุมัติใบเสร็จ (เต็มใบ) |
| จ่ายบางส่วน/หลายงวด | ✅ (WHT proportional + ปิดยอดงวดสุดท้าย) | ❌ ต้องเต็มใบเท่านั้น (`ValidateConversionAsync` block ถ้า PaidAmount>0) |
| หักมัดจำ / ค่าธรรมเนียม / FX / override บัญชี | ✅ ครบ | ❌ |
| carryVat (INV มี VAT จ่ายครบ → ใบเสร็จถือ VAT หัวรวม) | ✅ ผ่าน issueReceipt + smart default | ✅ ผ่าน settlement approve |
| หัวใบต้นทางอัปเกรด (ServedAsReceipt) | ✅ | — (ใบเสร็จเป็นกระดาษหลักแทน) |

⇒ **ทับซ้อน 100% เฉพาะเคส "จ่ายเต็มใบ"** และ convert ทำได้น้อยกว่าในทุกมิติ

**ข้อเสนอ (ตัดสินใจแล้ว — Opus ทำตามนี้):** เก็บทั้งสองกลไก แต่**ทางเข้าเดียวต่อ
สถานการณ์** (policy เดียวกับ dropdown หัวเอกสาร):
1. **เอกสารที่ตั้งหนี้แล้ว (Invoice/TaxInvoice/DebitNote ที่มี AR):** ปุ่ม
   "บันทึกชำระเงิน" เป็นทางเดียว — ซ่อน "ใบเสร็จรับเงิน/ใบสำคัญรับ" ออกจาก
   convert modal สำหรับแหล่งพวกนี้ (`openConvert` กรอง + backend
   `ValidConversions` คงไว้เพื่อ compatibility แต่ UI ไม่เสนอ) — ถ้าผู้ใช้เปิด
   convert modal ใส่ hint ชี้ไปปุ่มบันทึกชำระแทน (ห้าม silent — กฎเหล็ก #4 A)
2. **แหล่ง pre-revenue (Quotation/BillingNote):** convert → Receipt (ขายสด
   รับรู้รายได้เอง) ยังอยู่ — recordPayment ใช้ไม่ได้เพราะยังไม่มี AR
3. **แก้ข้อความติ๊กใน modal บันทึกชำระ ให้ตอบคำถาม "จะมีเอกสารใหม่ไหม" ตรง ๆ
   และรู้จัก combined:**
   - ไม่ติ๊ก (default เมื่อจ่ายครบ): `ไม่สร้างเอกสารใหม่ — ใบเดิมใบเดียวทำหน้าที่
     ครบ หัวพิมพ์จะเปลี่ยนเป็น "<หัวจริงตามใบ: 3-in-1 สำหรับ combined /
     ใบกำกับภาษี/ใบเสร็จรับเงิน สำหรับ TIV เปล่า>"`
   - ติ๊ก: `สร้างใบเสร็จรับเงินเพิ่มอีก 1 ใบ (หลักฐานรับเงิน — ไม่ลงบัญชีซ้ำ)
     ใบเดิมหัวคงเดิม`
   - ต้องอ่าน `combinedInvoiceTaxInvoice` ของ doc มาเลือกชื่อหัว (มีใน
     `_currentDoc` แล้ว)
4. DOCUMENT_FLOW.md อัปเดตตาราง entry/exit ของ Receipt + TEST_PLAN: เคส
   INV→convert modal ไม่มีตัวเลือกใบเสร็จ / QT ยังมี / ข้อความติ๊กตรงหัวจริง /
   ใบ combined จ่ายครบไม่ติ๊ก → พิมพ์ได้หัว 3-in-1

**เกณฑ์ตรวจรับ:** ผู้ใช้ที่ถือใบตั้งหนี้ เจอปุ่มเดียวสำหรับ "รับเงิน" · คำตอบของ
ติ๊กใบเสร็จอ่านครั้งเดียวรู้ว่ามี/ไม่มีเอกสารใหม่ · ไม่มีเส้นทางที่กดแล้ว error
หรือหายเงียบ

---

## ลำดับการทำที่แนะนำ + Definition of Done (สำหรับ Opus)

**ลำดับ (พึ่งพากันตามนี้):**
1. `A2` (แถวเตือน default excluded) — เล็ก จบเร็ว ปลดความสับสนยอดจอ≠ไฟล์ทันที
2. `A1` (format ไฟล์ RD) — อิสระ ทำคู่ B5 (ภงด.54 cert-primary) จะได้แตะไฟล์เดียวรอบเดียว
3. `A5` + `A4` — คู่กัน (ทั้งคู่คือ "ระบบพูดภาษาหัวกระดาษ"): ทำ A4 helper ก่อนแล้ว A5 ใช้ชื่อหัวจาก helper
4. `A3` (เลขใบลดหนี้ผู้ขาย) — อิสระ
5. หมวด B ตามความรุนแรง: B1→B2→B3 (นับซ้ำ WHT — เกี่ยวเนื่องกัน ควรทำชุดเดียว), B11-B14 (VAT correctness), B4 (regenerate), B5-B10 (แบบรายปี/54/51)
6. หมวด C ตาม Task Force เดิม

**Definition of Done ทุกข้อ:**
- [ ] โค้ด + เทสต์/simulation (logic เงิน-ภาษี = pure test ในคอมมิตเดียวกัน;
      frontend = node harness ดึงโค้ดจริง + negative test)
- [ ] checker ครบ: `using_check` `record_arg_check` `gl_code_check`
      `di_cycle_check` `nullable_arg_check` = 0 + brace balance + `node --check`
- [ ] DOCUMENT_FLOW.md (และ TEST_PLAN.md) อัปเดตในคอมมิตเดียวกัน
- [ ] ติ๊ก ✅ ในไฟล์นี้ + ระบุ commit sha
- [ ] แจ้งผู้ใช้ rebuild — env นี้คอมไพล์ไม่ได้

**ข้อควรระวังที่เจ็บมาแล้ว (อ่านก่อนเริ่ม):**
- อย่าเชื่อรายงานของ agent/ทีมตรวจโดยไม่เปิดโค้ดยืนยัน (เคย false report เรื่อง
  สถานะ Overdue)
- เลขบัญชี**ห้ามเดาจาก prefix** — 21510/53xx/2191x/114x/115x เคยพังมาแล้วคนละรอบ
  (ดู tools/gl_code_check.py)
- "สองที่ต้อง sync" ทุกคู่ให้ mirror comment สองฝั่ง: JE จริง↔พรีวิว GL,
  จอ↔ไฟล์ยื่น, backend↔frontend conversion map
- แก้ sed กับข้อความไทยห้ามใช้ shell escape — ใช้ python patch + assert count==1

---

## B. Backlog ภาษี 14 ข้อ (จาก audit 3 ทีม — DOCUMENT_FLOW.md รอบ 29)

| # | เรื่อง | จุดโค้ด | หมายเหตุ |
|---|---|---|---|
| B1 | หนังสือรับรองคีย์มือไม่มี `DocumentId` → นับซ้ำกับแถวเตือนเอกสารเดียวกัน | `WithholdingTaxCertDtos.cs:6-12`, `WithholdingTaxCertService.CreateAsync` | เพิ่ม DocumentId ใน DTO+UI สร้าง cert + certCovered ครอบ |
| B2 | cert เต็มใบ (ตอน approve) + cert รายงวด (ตอนจ่าย) ออกซ้ำใบเดียวกัน | `WithholdingTaxCertService.cs:403-418` (idempotency แยกสาขา), call sites `DocumentService.cs:4488, 8930` | สาขา sourcePaymentId ต้องเช็ค cert เต็มใบ active ก่อน |
| B3 | Accrual basis: PI (เดือนตั้งหนี้) + cert ของ PV (เดือนจ่าย) นับซ้ำข้ามเดือน | `GenerateWhtReport` cert block ไม่รู้จัก WhtRecognitionBasis | นิยามเดือนนำส่งตามกฎหมาย = เดือนจ่ายเสมอ — พิจารณาตัด doc-mining เดือน accrual |
| B4 | Regenerate ภงด. ไม่ snapshot ติ๊ก/แก้มือ + ล้าง audit fields (RdAck*, EFilingExportedAt, RejectionReason, ReversalJournalEntryId) + ไม่ atomic + `TaxCalendarEvent.TaxReportId` ค้าง | `TaxService.cs` RegenerateTaxReportAsync ~:3006-3032 | snapshot non-VAT ด้วย + preserve audit + transaction เดียว |
| B5 | `ExportPnd54Async` ยัง doc-mined — ต่างจากจอ 13 จุด (นิยาม foreign, ชนิดเอกสาร, สถานะ, วันที่, dedup, IsExcluded, income type hardcode "6") | `TaxFilingExportService.cs:815-860` | ย้ายเป็น cert-primary แบบ Pnd3/53 |
| B6 | ภงด.51 คำนวณจาก JE ล้วน ไม่ใช้ add-back §65 ตรี/เพดานค่ารับรอง — คนละฐานกับ ภงด.50 + ไม่มี TaxType/หน้าจอ | `ExportPnd51Async` (`TaxFilingExportService.cs:876-936`) | |
| B7 | ขาดทุนยกมา 5 ปี (§65 ตรี(12)) ไม่ถูกหักใน CIT เลย + โค้ดเครดิตยกมาเป็น dead code (`NetVat<0` = ขาดทุน ไม่ใช่ชำระเกิน) | `TaxService.cs` GenerateCitReport ~:1905-1911 | |
| B8 | ค่าเสื่อมภาษี CIT ดึงตามปีปฏิทิน (`d.Year == year`) ขณะรายได้ตามรอบบัญชี FiscalYearStartMonth | `TaxService.cs:1831-1834` | |
| B9 | CIT unique ต่อ (ปี+เดือน) — สร้างได้ 12 ใบ/ปี, e-Filing หยิบ `FirstOrDefault(Year)` ใบไหนก็ได้ | `TaxService.cs:45-53`, `EFiling.cs:96-98,254-257` | unique รายปีสำหรับ CIT/PIT91 |
| B10 | ภงด.91: สร้างจาก UI ไม่ได้ + ไฟล์กับจอใช้ประชากร payroll คนละเงื่อนไข (`Status != Draft/Voided` vs `Approved/Paid`) + ไฟล์ไม่มีบรรทัดชำระเพิ่ม/เกิน | `ExportPnd91Async` (:626-675) vs `GeneratePnd91Report` (:2089+) | |
| B11 | `RecalcVatTotals` default-to-output — IncomeTypeCode ใหม่/สะกดผิดไหลเข้าภาษีขายเงียบ | `TaxService.cs` ~:2564 | เปลี่ยนเป็น allow-list สองฝั่ง + throw เมื่อไม่รู้จัก |
| B12 | §82/3 ฐานวันที่ generate (`DocumentDate`) ≠ pull (`SupplierTaxInvoiceDate ?? TaxPointDate ?? DocumentDate`) — เอกสารเดียวตอบต่างกันแล้วแต่ทางเข้า | `TaxService.cs` ~:823-825 vs ~:2734-2765 | ฐานที่ถูก = วันที่ใบกำกับผู้ขาย |
| B13 | Receipt ที่ใบต้นทางถูก soft-delete → output VAT หายเงียบ (global filter ตัด relatedDoc) | `TaxService.cs` ~:261-270, 486-493 | IgnoreQueryFilters ตอน resolve + แถวเตือน |
| B14 | auto-save ภงด./VAT เป็น all-or-nothing + UI เปิดติ๊กบรรทัด 🚫/⚠️ ที่ server จะ throw — ติ๊กผิด 1 บรรทัด rollback ทั้งชุดโดยไม่บอกบรรทัดไหน | `tax.html` renderVatRow + `TaxService.cs:2480-2510` | disabled checkbox ตาม marker + error ระบุบรรทัด |

## C. งานค้าง Task Force รอบก่อน (สถานะเดิม — ดู NextAcc_TaskForce_Status)

- **P1**: CSP เลิก unsafe-inline (nonce middleware — แตะ ~115 หน้า) · test harness
  WebApplicationFactory/Testcontainers + เทสต์ critical-path ที่เหลือ (JE posting,
  tenant isolation, ภ.พ.30 round-trip, payroll)
- **P2**: แตก DocumentService (13.5k บรรทัด) · IFileStorage + rate-limit แบบ
  distributed · CompanyId บน JournalEntryLine + balance snapshot · migration
  versioning + CREATE INDEX CONCURRENTLY
- **P3**: refresh token (S6) · ย้าย OCR upload ออกจาก wwwroot (S8) ·
  UseForwardedHeaders (S9) · S10-S14 · GetGeneralLedgerAsync memory (P4) ·
  N+1 (P5-P7) · audit middleware rows (Q3) · ConfirmPaymentAsync catch{} (Q5)
- **รอเจ้าของตัดสินใจ**: A1 default การรับรู้ VAT มัดจำ (ต้องนักบัญชียืนยัน) ·
  StrictPayeeIdentification switch

---

_สร้าง: 2026-08-17 · อัปเดตเมื่องานข้อใดเสร็จ: ติ๊ก ✅ + ลิงก์ commit + ย้ายเข้า
DOCUMENT_FLOW.md ตามรอบ_
