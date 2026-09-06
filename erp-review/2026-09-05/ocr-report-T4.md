# ทีม T4 — UX 1-click + ความต่อเนื่องสแกน→เอกสาร

รอบตรวจ: 2026-09-06 · working tree ปัจจุบัน (มีการแก้ฝั่ง server ที่ยังไม่ commit จาก main agent)
ขอบเขต: `Accounting/wwwroot/pages/document-scan.html` (4,316 บรรทัด) · `Controllers/OcrController.cs` ·
`Services/Implementations/OcrService.cs` (`MapToResponse` / `CreateDocumentFromScanAsync` / `SubmitCorrectionAsync`) ·
`Models/DTOs/Ocr/OcrDtos.cs` · `wwwroot/pages/documents.html` · `wwwroot/mobile-expense.html` ·
`Services/Implementations/LineBotService.cs` · `wwwroot/pages/review-queue.html`

โจทย์: **"อัพใบกำกับซื้อ 1 ใบ วันนี้ต้องกดกี่ครั้ง กรอกอะไรเอง กว่าจะได้เอกสาร Approved + JE"** และ
**"ค่าที่ OCR อ่านได้ หลุดหายที่รอยต่อไหน"**

## สรุป 8 บรรทัด
1. **ทางที่สั้นที่สุดวันนี้ = 3 คลิก + Enter 1 ครั้ง** (เลือกไฟล์ → เลือกในไดอะล็อก → "ตรวจสอบ" → Enter)
   ซึ่ง **ดีกว่าที่คาด** และ batch ทำ 20 ใบได้ใน ~22 คลิก — โครง UX ถูกทางแล้ว
2. แต่ทั้งสองเส้น **บอกผลผิด**: server เพิ่ม `[APPROVE-SKIP]` แล้ว UI ยังหาแต่ `[APPROVE-FAIL]`
   ⇒ ใบที่ยังเป็น Draft ถูกบอกว่า "อนุมัติแล้ว" (T4-01) · batch นับผลด้วยสตริงไทยที่เซิร์ฟเวอร์
   ไม่เคยเขียน ⇒ นับ "ลงบัญชีแล้ว" ทุกใบเสมอ (T4-02)
3. โน้ตใหม่ของเซิร์ฟเวอร์ **5 แท็ก** (`APPROVE-SKIP · Σ-GAP · DATE-UNKNOWN · WHT-CERT ·
   TAX-INV-PENDING`) **grep ใน wwwroot = 0 จุด** — มองไม่เห็นทั้งหมด (T4-03)
4. ความต่อเนื่องขาด 6 จุด: per-line `VatRate`/`Unit` (มีใน DTO ไม่มีคอลัมน์) · `Currency`
   (ไม่มีใน DTO ⇒ เส้น "แก้ในฟอร์ม" ได้ THB เสมอ) · `DiscountAmount` · `WhtIncomeType` ·
   `UserNotes` · `GlAccountAiFeedbackId` (สายขาด ⇒ เส้นฟอร์มไม่ CAPTURE เลย)
5. **ฉาก "ใบแจ้งหนี้บริการมี WHT" ทำ 1-click ไม่ได้จริง** — ไม่มีช่อง "ประเภทเงินได้ ม.40"
   ทั้งสาย ⇒ ต้องไปเปิดเอกสารกรอกเองก่อนออก 50 ทวิ / ภ.ง.ด.3/53 (T4-06)
6. UI ยัง **เดา `00000`** แทน 2 จุดหลังเซิร์ฟเวอร์เลิกเดาแล้ว (T4-05) — defect class เดิม ย้ายชั้น
7. สามช่องทาง (เว็บ / LINE / มือถือ) ใช้ **สามนโยบาย** — LINE auto-create + อนุมัติ 1 แตะ
   โดยการ์ดไม่แสดงคำเตือนภาษีเลย · มือถือสร้าง Draft แล้วบอก "ส่งเบิกแล้ว" (T4-15)
8. `review-queue.html` **ไม่มีทางเข้าจากเมนูใด ๆ ทั้งระบบ** ทั้งที่เป็นปลายทางของทุกใบที่
   ระบบตัดสินเองไม่ได้ (T4-14) · checker 4 ตัวที่สั่งรัน **ผ่านหมด 0 จุด**

**รวม 20 findings** — 🔴 P1 6 · 🟠 P1 4 · P2 6 · P3 4 · 🔵 ข้อเสนอ 6

---

## §0 ตารางนับคลิก — "อัพแล้วจบ" วันนี้ใช้กี่คลิก

นับจากที่ผู้ใช้อยู่ในหน้า `/pages/document-scan.html` แล้ว (ยังไม่นับคลิกเมนู) ·
"พิมพ์เอง" = ช่องที่ผู้ใช้ต้องพิมพ์/เลือกเองเพราะระบบไม่เติมให้ หรือเติมแล้วผิดบ่อยจนต้องแตะ

| ฉาก | คลิกน้อยสุด (happy path) | คลิกจริงเมื่อค่าไม่ครบ | ต้องพิมพ์/เลือกเองอะไร |
|---|---|---|---|
| **A. ใบกำกับซื้อธรรมดา** (Azure DI อ่านครบ ผู้ขายเคยมีในระบบ) | **4** = เลือกไฟล์(1) + เลือกในไดอะล็อก(1) + "ตรวจสอบ & สร้างเอกสาร"(1) + "⚡ ยืนยัน + ลงบัญชี"(1) — คลิกสุดท้ายแทนด้วย **Enter** ได้ (`:2564`) ⇒ เหลือ 3 | +1 ถ้าไม่มี `suggestedAccounts.debitAccountCode` (ต้องเลือกบัญชีเดบิตเอง `:2313`) · +1–2 ถ้าไม่ match ผู้ติดต่อ (ค้น+เลือก `:2483`) | ปกติ **0 ช่อง** · ถ้าไม่ match: บัญชีเดบิต, ผู้ติดต่อ |
| **B. บิลเงินสดค่าน้ำมัน** (สลิปถ่ายมือถือ) | **4** เท่ากัน | +1 กด "ตรวจสอบ" อ่าน banner `[VAT-CLAIM]` (§82/5(6) รถยนต์นั่ง) · +1 เปลี่ยน "เอกสารที่จะสร้าง" เป็น `PaymentVoucher` ถ้าอนุมานผิด · **หมวดค่าใช้จ่าย/บัญชีเดบิตต้องเลือกเองบ่อย** (บิลน้ำมันมักไม่มีเลขผู้เสียภาษี ⇒ ไม่มี vendor history) | หมวดค่าใช้จ่าย, บัญชีเดบิต, บัญชีเครดิต (แหล่งเงิน), บ่อยครั้ง **วันที่** (สลิปความร้อนอ่านไม่ออก) |
| **C. ใบแจ้งหนี้บริการมีหัก ณ ที่จ่าย 3%** | **5** = 4 ของฉาก A + ติ๊ก `revHasWht` และเลือก 3% (`:2331–2332`) ถ้าระบบไม่ตั้งให้ | +2 ถ้าต้องแก้เครดิตเทอม/วันครบกำหนด · **+N คลิกในหน้าเอกสาร** เพราะ **ไม่มีช่อง "ประเภทเงินได้" (ม.40(2)/(3)/(6)/(7)(8)) ใน review เลย** ⇒ ต้องไปเปิดเอกสารแล้วเลือกเองก่อนออก 50 ทวิ | อัตรา WHT, **ประเภทเงินได้ (บังคับสำหรับ ภ.ง.ด.3/53)**, บางครั้งเครดิตเทอม |

**สรุปตัวเลข:** ทางที่สั้นที่สุดคือ **3 คลิก + Enter 1 ครั้ง** (ดีกว่าที่คาดไว้มาก) และมี
ทางลัด **batch** (ติ๊กหลายใบ → "อนุมัติเป็นชุด") ที่ทำให้ 20 ใบเหลือ ~22 คลิก —
แต่ทั้งสองเส้นมีปัญหา "เงียบ" ที่ทำให้ผู้ใช้**คิดว่าจบแล้วทั้งที่ยังไม่จบ** (ดู T4-01/T4-02)
และฉาก C ยังทำ **1-click ไม่ได้จริง** เพราะประเภทเงินได้ไม่มีที่ให้กรอก (T4-06)

### กฎเหล็ก #3 ข้อ 4 — "ห้ามมี required-field validation error ตอนกด approve"
✅ **ผ่านฝั่ง client**: `createDocFromReview` (`:3190`) ไม่ validate อะไรเลยก่อนยิง —
ไม่มี `return`/`toast('กรุณา…')` ขวางทาง · ปุ่มหลักเป็น `btn-primary` "⚡ ยืนยัน + ลงบัญชี" (`:471`)
และ Enter ผูกกับปุ่มนั้น (`:2564–2578`) ตรงตามข้อ 4
⚠️ **ไม่ผ่านฝั่งความจริง**: server ยัง reject/skip ได้ แล้ว UI แปลผลผิด (T4-01)
❌ `createJeFromReview` (`:3243`) **มี** required validation (`'กรุณาเลือกบัญชีเดบิตและเครดิตก่อน'`)
— แต่เป็นปุ่มรอง จึงไม่ผิดข้อ 4 โดยตรง

---

## §1 Findings

### T4-01 [🔴 P1][S] เซิร์ฟเวอร์ "ข้ามการอนุมัติ" แต่ UI บอกว่า **"สร้าง + อนุมัติเอกสารแล้ว"** แล้วพาออกจากหน้า
- ไฟล์: `wwwroot/pages/document-scan.html:3217–3226` · `Controllers/OcrController.cs:317–345`
- โค้ด (UI): `if (r.processingNotes && /\[APPROVE-FAIL\]/.test(r.processingNotes)) { … } else { Layout.toast('สร้าง + อนุมัติเอกสารแล้ว'); }`
- โค้ด (server, งานใหม่ของ main agent): `ProcessingNotes = … + "\n[APPROVE-SKIP] " + skipReason;`
  โดย `skipReason` = `"อ่านวันที่บนกระดาษไม่ได้ …"` หรือ `"ไม่มีสิทธิ์อนุมัติเอกสารประเภท {cType} …"`
- ทำไมพัง: (1) server เจอ 2 เคสที่ **ไม่เรียก `ApproveDocumentAsync` เลย** → เอกสารคง Draft
  (2) มันแปะโน้ต `[APPROVE-SKIP]` ไม่ใช่ `[APPROVE-FAIL]` (3) UI regex หาเฉพาะ `APPROVE-FAIL`
  ⇒ ตกไป `else` แล้วขึ้น toast **"สร้าง + อนุมัติเอกสารแล้ว"** (4) แล้ว `location.href` พาไป
  `documents.html?openDoc=…` ทันทีใน 600ms — toast หายไปพร้อมการนำทาง
- ผลกระทบ: ใบที่ **วันที่อ่านไม่ออก** (ระบบเติมวันนี้) และใบที่ผู้ใช้ **ไม่มีสิทธิ์อนุมัติ**
  จะถูกบอกว่าอนุมัติแล้ว ⇒ ผู้ใช้ไม่กลับมาแก้วันที่ ⇒ ใบค้าง Draft ไม่เข้า ภ.พ.30 เดือนนั้น
  (หรือเข้าเมื่ออนุมัติทีหลังในงวดผิด) · ตรงกับกติกา "ห้าม silent no-op" ของกฎเหล็ก #4 A
- defect class: **"ห้าม silent no-op"** + **"ครึ่งเซิร์ฟเวอร์ของการแก้ ship ไปคนเดียว"**
  (server แปะแท็กใหม่ 5 ตัว UI ไม่รู้จักสักตัว)
- ทางแก้: ทำ **ตัวอ่านโน้ตกลางตัวเดียว** ใน JS — `parseScanNotes(notes)` คืน
  `{approveSkip, approveFail, sigmaGap, dateUnknown, whtCert, taxInvPending, vatClaim[], vatNote[]}`
  แล้ว (ก) `createDocFromReview` เช็ค `approveSkip` ก่อน `approveFail` → toast สีเหลือง
  "สร้างเป็นใบร่างแล้ว ยังไม่ลงบัญชี: <เหตุผล>" + **ไม่ redirect อัตโนมัติ** (ให้ผู้ใช้กดเอง)
  (ข) การ์ดสแกนติดป้าย "ร่าง — รออนุมัติ" แทน "สร้างแล้ว"
- ความมั่นใจ: **สูง** (เปิดทั้งสองไฟล์ · grep `APPROVE-SKIP` ใน wwwroot = 0 จุด)

### T4-02 [🔴 P1][S] "อนุมัติเป็นชุด" นับผลผิดทุกใบ — เทียบสตริงไทยที่เซิร์ฟเวอร์ไม่เคยเขียน
- ไฟล์: `wwwroot/pages/document-scan.html:1365`
- โค้ด: `if ((d.processingNotes || '').includes('อนุมัติไม่สำเร็จ')) draft++; else ok++;`
- ทำไมพัง: `grep -rn "อนุมัติไม่สำเร็จ" --include=*.cs` ⇒ เจอเฉพาะ **คอมเมนต์** ใน
  `JournalEntryBuilder.cs:138` / `DocumentService.cs:14766` / `AdvisoryLockKey.cs:12` และ
  `LineBotService.cs:643` (ข้อความของบอท LINE คนละเส้น) — **ไม่มีจุดไหนเขียนสตริงนี้ลง
  `ProcessingNotes` ของ OCR เลย** ⇒ เงื่อนไขเป็นเท็จเสมอ ⇒ `ok++` ทุกใบ
- ผลกระทบ: ผู้ใช้ติ๊ก 20 ใบ กด "อนุมัติเป็นชุด" ได้ toast **"ลงบัญชีแล้ว 20 ใบ"** ทั้งที่
  บางใบเป็น Draft (§86/4 ไม่ครบ / ไม่มีสิทธิ์ / วันที่ไม่รู้) — และ batch เป็นเส้นที่
  **ตั้งใจให้ไม่ต้องเปิดดูรายใบ** ⇒ ไม่มีใครไปเจอใบร่างนั้นอีกเลยจนปิดงบ
- defect class: **"สำเนามือของกติกาเซิร์ฟเวอร์ใน JS"** + "ห้าม silent no-op"
- ทางแก้: ใช้ `parseScanNotes` ตัวเดียวกับ T4-01 · ที่ถูกกว่าคือให้ **server คืนสถานะจริง**
  (`documentStatus` ของใบที่สร้าง) ในผลลัพธ์ แล้ว UI นับจากสถานะ ไม่ใช่จากการอ่านข้อความ
  (กติกา "เซิร์ฟเวอร์ส่งค่าที่คำนวณแล้วมา หน้าเว็บแสดงอย่างเดียว")
- ความมั่นใจ: **สูง**

### T4-03 [🔴 P1][S] แท็บเตือนใหม่ของเซิร์ฟเวอร์ 5 ตัว **มองไม่เห็นเลยในหน้าเว็บ** — และ 4 ใน 5 ซ่อนอยู่ใต้ปุ่ม "ข้อมูลดิบ"
- ไฟล์ (server เขียน): `OcrService.cs:991` `[DATE-UNKNOWN]` · `:4616` `[WHT-CERT]` ·
  `:4882` `[TAX-INV-PENDING]` · `:5101` `[Σ-GAP]` · `OcrController.cs:336` `[APPROVE-SKIP]`
- ไฟล์ (UI): `document-scan.html:2033/2044` อ่านเฉพาะ `[VAT-CLAIM]` / `[VAT-NOTE]` ·
  `:3218` อ่านเฉพาะ `[APPROVE-FAIL]` · `:2469` ก้อน `processingNotes` ดิบอยู่ใน **debugPanel**
  ซึ่งเปิดเฉพาะเมื่อกดปุ่ม "🔍 ข้อมูลดิบ" (`:1467`) หรือส่ง `{debug:true}`
- ทำไมพัง: `grep -c "APPROVE-SKIP\|Σ-GAP\|DATE-UNKNOWN\|WHT-CERT\|TAX-INV-PENDING" wwwroot -r` = **0**
- ผลกระทบรายตัว:
  - `[DATE-UNKNOWN]` — ระบบเติม "วันนี้" ให้แล้วบอกในโน้ต แต่ช่องวันที่ในโมดัลแสดงวันนี้
    **พร้อมป้ายความมั่นใจ "—"** (ไม่ใช่คำเตือน) ⇒ ผู้ใช้กด Enter ยืนยันวันผิด ⇒ tax point/งวด ภ.พ.30 ผิด
  - `[Σ-GAP]` — ผลรวมบรรทัด ≠ ยอดบนกระดาษ (`OcrLineReconciler` ตัวใหม่) **เป็นสัญญาณที่
    นักบัญชีต้องเห็นที่สุด** แต่ผู้ใช้ไม่มีทางเห็นเพราะ note นี้เกิดตอน *create* แล้วผู้ใช้
    ถูกพาออกจากหน้าไปทันที
  - `[TAX-INV-PENDING]` — VAT พักที่ 11640 รอเลขใบกำกับ (§82/5(1)) ⇒ ต้องมี worklist ให้ตามเก็บ
  - `[WHT-CERT]` — เราถูกหักภาษี ระบบลงเครดิตให้แทนการสร้างเอกสารขาย ⇒ ผู้ใช้ที่ตั้งใจ
    สร้างใบขายจะงงว่า "กดแล้วไม่เกิดอะไร" (silent no-op ในสายตาผู้ใช้)
- defect class: **"ครึ่งเซิร์ฟเวอร์ของการแก้ ship ไปคนเดียว"** (ตรงกับบทเรียน `OcrCorrectionRequest`)
- ทางแก้: `parseScanNotes` + แถบ "สิ่งที่ระบบตัดสินใจแทนคุณ" บนหัวโมดัล (ไม่ใช่ debug panel)
  แต่ละบรรทัดมี **ปุ่มไปแก้จุดนั้น** (วันที่ → focus `revDate`; Σ-GAP → scroll ตารางบรรทัด)
- ความมั่นใจ: **สูง**

---

## §2 Continuity matrix — ค่าที่ OCR อ่านได้ หลุดที่รอยต่อไหน

เส้นทาง: `OcrScanResult` → `MapToResponse` (`OcrService.cs:6699`) → `OcrResultResponse` →
`document-scan.html` (review modal) → **A)** `createDocument` → `CreateDocumentFromScanAsync` (`:4470+`)
หรือ **B)** `openInDocumentForm` (`:3938`) → `sessionStorage['ocr_handoff_payload']` →
`documents._consumeScanHandoff` (`documents.html:1806`)

| ค่า | อยู่ใน DTO? | review แสดง? | review แก้ได้? | ส่งกลับ (correct)? | เส้น A → Document | เส้น B → ฟอร์ม | สรุป |
|---|---|---|---|---|---|---|---|
| SellerName / TaxId | ✅ | ✅ `:2185/:2190` | ✅ | ✅ | ✅ Contact | ✅ | ครบ |
| **SellerBranchCode** | ✅ | ✅ `:2195` | ✅ | ✅ | ✅ `:4926` (paper→Contact, ไม่ fabricate 00000 แล้ว) | ⚠️ `|| '00000'` `:4215` | **UI ยังเดา 00000 (T4-05)** |
| SellerAddress | ✅ | ✅ `:2246` | ✅ | ✅ | ✅ | ✅ `supplierAddress` | ครบ |
| **BuyerTaxId** | ✅ | ✅ `:2230` | ✅ | ✅ | ⚠️ ใช้ตอนตรวจ mismatch เท่านั้น | ❌ ไม่อยู่ใน handoff | ขาดเส้น B |
| **BuyerBranchCode / BuyerName / BuyerAddress** | ✅ | ✅ `:2236/:2252/:2258` | ✅ | ✅ | ⚠️ ฝั่งขายเท่านั้น | ❌ ไม่อยู่ใน handoff | ขาดเส้น B |
| PaymentTermsDays | ✅ | ✅ `:2264` (+ซ้ำอ่านอย่างเดียว `:2343`) | ✅ | ✅ | ✅ → `DueDate` `:4757` | ✅ | **แสดงซ้ำสองที่ (T4-09)** |
| **WhtRate + HasWht** | ✅ | ✅ `:2331` | ✅ | ✅ | ✅ `headerWht` `:4794` | ✅ | ครบ |
| **ประเภทเงินได้ WHT (ม.40)** | ❌ ไม่มีใน DTO | ❌ | ❌ | ❌ | ❌ | ❌ | **หายทั้งสาย (T4-06)** |
| **per-line GL account** | ✅ `SuggestedAccountCode` | ✅ dropdown ในตาราง `:2374` | ✅ | ✅ ผ่าน `saveLineFields` (endpoint แยก) | ✅ | ✅ `l.accountCode` | ครบ |
| **per-line VatRate / VatAmount** | ✅ (E-OCR-01 ทำแล้ว `:6763`) | ❌ **ไม่มีคอลัมน์** | ❌ | ❌ | ✅ ใช้ค่าใน JSON | ⚠️ คิดใหม่ `vatRate: …?7:0` `:4026` | **half-shipped (T4-04)** |
| **per-line Unit** | ✅ `:6761` | ❌ ไม่มีคอลัมน์ | ❌ | ❌ | ✅ | ✅ | มองไม่เห็น |
| **discount (หัวใบ)** | ✅ `ExtractedDiscountAmount` | ❌ **ไม่แสดงเลย** | ❌ | ❌ ไม่มีใน correction DTO | ✅ `DiscountAmount` `:4967` | ✅ `billDiscountAmount` | **แก้ไม่ได้ (T4-07)** |
| **Currency** | ❌ **ไม่มีใน DTO** | ❌ | ❌ | ❌ | ✅ `InferCurrency(rawText)` `:4941` | ❌ `scan.currency` = undefined ⇒ THB เสมอ `:4173` | **สองเส้นให้คนละคำตอบ (T4-08)** |
| Project (รายบรรทัด) | ✅ | ✅ + "apply ทุกบรรทัด" `:2360` | ✅ | ✅ endpoint แยก | ✅ | ✅ | ครบ — ดีที่สุดในไฟล์ |
| payment source (บัญชีเครดิต) | ✅ `suggestedAccounts` | ✅ `:2323` | ✅ | ✅ | ✅ → `PaymentAccountId`/`BankAccountId` `:4831` | ✅ `paymentChannelCode` | ครบ |
| InputVatClaimable | ผ่าน `[VAT-CLAIM]` ใน notes | ✅ banner `:2033` | — | — | ✅ `vatNotClaimable` `:4868` | ✅ `inputVatClaimWarning` | ครบ (แต่เป็นการ parse ข้อความ) |
| tax point vs invoice date | — | ❌ ไม่มีช่อง DeliveryDate | ❌ | ❌ | ⚠️ ตั้งใน service | ✅ `deliveryDate = extractedDate` `:4222` | เส้น A/B ไม่ตรงกัน |
| SupplierInvoiceNumber | ✅ (`ExtractedDocumentNumber`) | ✅ `:2201` | ✅ | ✅ | ✅ (มี guard T1-20) | ✅ | ครบ |
| **UserNotes (เหตุผลทางธุรกิจ §65ตรี)** | ❌ ไม่มีใน DTO | ❌ ไม่มีช่องในเว็บ | ❌ | ❌ ไม่ส่ง `notes` | ✅ `result.UserNotes` `:4981` | ❌ ทับด้วย "สร้างจากการสแกน…" `:4194` | **เว็บกรอกไม่ได้ (T4-10)** |
| **GlAccountAiFeedbackId** | ❌ ไม่มีใน DTO (มีบน entity `Intelligence.cs:240`) | — | — | — | ✅ | ❌ สายขาด `:4228` | **วงจร distill ขาด (T4-11)** |
| fieldConfidence | ✅ | ✅ ป้าย % + ไฮไลต์ `:2503` | — | — | — | ✅ ส่งต่อ | ครบ |

---

### T4-04 [🟠 P1][S] `VatRate` / `VatAmount` / `Unit` รายบรรทัดถึงหน้า review แล้ว **แต่ตารางไม่มีคอลัมน์ให้ดู/แก้**
- ไฟล์: `Models/DTOs/Ocr/OcrDtos.cs:164–172` (doc-comment เขียนเองว่า *"E-OCR-01: ต้องส่งถึงหน้า
  review ให้ผู้ใช้เห็น/แก้ได้"*) · `OcrService.cs:6761–6764` map ครบ · `document-scan.html:2372–2375`
  ตารางมี **7 คอลัมน์: ลบ · รายละเอียด · จำนวน · ราคา · รวม · บัญชี · โครงการ**
- ทำไมพัง: ฝั่งเซิร์ฟเวอร์ทำครบ ฝั่ง UI ไม่เคยเพิ่มคอลัมน์ ⇒ บรรทัดที่ระบบเดา VatRate ผิด
  (mixed rate: ค่าสินค้า 7% + ค่าขนส่งยกเว้น) ไหลเข้าเอกสารโดยไม่มีใครทัดทาน แล้วไปโผล่
  **ผิดคอลัมน์ในรายงานภาษีซื้อ §87** — ซึ่งเป็นผลเสียที่ doc-comment ระบุไว้เองตรง ๆ
- ที่แย่กว่า: เส้น B (`openInDocumentForm:4026`) **ไม่อ่าน `it.vatRate` เลย** แต่คำนวณใหม่
  `vatRate: scan.extractedVatAmount && scan.extractedSubTotal ? 7 : 0` ⇒ ใบ mixed-rate ที่ผ่าน
  เส้น B จะกลายเป็น 7% ทุกบรรทัด (หรือ 0% ทุกบรรทัด) = **สำเนามือของตรรกะที่เซิร์ฟเวอร์ทำถูกแล้ว**
- defect class: **"ครึ่งเซิร์ฟเวอร์ของการแก้ ship ไปคนเดียว"** + "สำเนามือ"
- ทางแก้ (S): เพิ่ม 2 คอลัมน์ในตาราง — `หน่วย` (text) และ `VAT` (select: 7 / 0 / ยกเว้น(-1))
  ผูก `saveLineFields` เดิม (มี endpoint `ModifyExtractedLineAsync` อยู่แล้ว ต้องเช็คว่ารับ
  vatRate ไหม) · และเส้น B เปลี่ยนเป็น `vatRate: it.vatRate ?? (…เดิม…)`
- ความมั่นใจ: **สูง** (นับคอลัมน์ในบรรทัดเดียวที่ render ทั้งแถว)

### T4-05 [🟠 P2][S] เซิร์ฟเวอร์เลิกเดา `"00000"` แล้ว **แต่ UI ยังเดาแทนให้ 2 จุด**
- ไฟล์: `document-scan.html:4215` `(document.getElementById('revVendorBranch')?.value||'').trim() || scan.vendorBranchCode || '00000'`
  · `documents.html:1918` `setVal('fSupplierBranchCode', payload.supplierBranchCode || '00000')`
- ทำไมพัง: main agent ถอด `?? "00000"` ออกจาก `OcrService` เพราะ "ค่า default ที่แต่งขึ้น
  อันตรายกว่าการไม่ตอบ" — แต่ทั้งสองจุดนี้เติมกลับให้เหมือนเดิมบนเส้น B ⇒ ใบของ **สาขาที่ 3**
  ที่ระบบอ่านสาขาไม่ออก จะถูกกรอก `00000` ไว้ในฟอร์มพร้อมแล้ว ผู้ใช้กด "บันทึก" = ประกาศ
  สำนักงานใหญ่ผิด (ประกาศอธิบดีฯ 199 · §86/4 · §87)
- defect class: **"ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้"** (ซ้ำรอย `?? "00000"` เดิม
  แค่ย้ายชั้นจาก server มา client) + **"แก้ตัวเดียว เหลือที่เหลือ"**
- ทางแก้: ปล่อยว่าง + ทำช่องเป็นสถานะ "ยังไม่รู้" (พื้นเหลือง + placeholder
  "ระบุสาขาบนใบ — 00000 = สำนักงานใหญ่") และให้ด่านอนุมัติเป็นตัวบังคับ ไม่ใช่เติมให้เงียบ
- ความมั่นใจ: **สูง**

### T4-06 [🔴 P1][M] ฉาก "ใบแจ้งหนี้บริการมี WHT" ทำ 1-click **ไม่ได้จริง** — ไม่มี "ประเภทเงินได้" ทั้งสาย
- ไฟล์: `OcrDtos.cs` (ทั้งไฟล์ ไม่มี IncomeType) · `document-scan.html:2329–2340` มีแค่ checkbox + อัตรา%
- ทำไมพัง: ภ.ง.ด.3/53 และหนังสือรับรอง 50 ทวิ บังคับ **ประเภทเงินได้ตามมาตรา 40** (ค่าบริการ
  40(2)/(7)(8) · ค่าสิทธิ 40(3) · ค่าเช่า 40(5) · วิชาชีพอิสระ 40(6)) — อัตรา % **ไม่ได้บอก
  ประเภท** (3% เป็นได้ทั้ง 40(2), 40(6), 40(3)) ⇒ ระบบเดาไม่ได้จากอัตราอย่างเดียว
  ⇒ ผู้ใช้ต้องเปิดเอกสารไปเลือกเองอีกหน้า = ทำลาย "อัพแล้วจบ" ของฉากที่พบบ่อยที่สุดฉากหนึ่ง
- ผลกระทบ: ยื่น ภ.ง.ด.3/53 ผิดประเภท หรือค้างไม่ได้ยื่นเพราะช่องว่าง
- ทางแก้: (ก) เพิ่ม `WhtIncomeType` ลง `OcrScanResult` + DTO + correction DTO
  (ข) เดาจาก `ExpenseCategory` ที่ระบบมีอยู่แล้ว (ค่าบริการ→40(2), ค่าเช่า→40(5), ค่าที่ปรึกษา→40(6))
  ผ่าน **ตารางกลางใน `Helpers/`** (ห้ามพิมพ์อัตราซ้ำใน JS — บทเรียน "ตารางความรู้เชิงกฎหมาย
  ที่คัดลอกไปเขียนใหม่") (ค) dropdown ใน review สร้างจาก endpoint ไม่ใช่ hardcode
- ความมั่นใจ: **สูง** (grep `IncomeType` ใน OcrDtos = 0)

### T4-07 [🟠 P2][S] ส่วนลดท้ายบิลที่ OCR อ่านได้ **ไม่แสดง ไม่แก้ได้** ในหน้า review
- ไฟล์: DTO มี `ExtractedDiscountAmount` (`OcrDtos.cs:65`) · `grep extractedDiscountAmount
  document-scan.html` = **0 จุด** · แต่ `openInDocumentForm` ใช้ `billDiscountAmount` ที่คำนวณ
  จาก reconcile และ `CreateDocumentFromScanAsync:4967` ใช้ `result.ExtractedDiscountAmount` ตรง ๆ
- ทำไมพัง: ค่าที่มีผลกับยอดสุทธิ (และกับ `headerSubTotal` `:4791`) ผู้ใช้มองไม่เห็นเลยในจอที่
  เขากด "ยืนยัน" ⇒ OCR อ่านส่วนลดเกิน/ขาด = ยอดผิดโดยไม่มีอะไรให้สงสัย
- ทางแก้: เพิ่มช่อง "ส่วนลดท้ายบิล" ข้าง SubTotal/VAT/Total + เพิ่ม `DiscountAmount` ลง
  `OcrCorrectionRequest` (วันนี้ไม่มี) — ครบทั้ง ฟอร์ม→payload→DTO→persist ตามเช็กลิสต์ #4 B
- ความมั่นใจ: **สูง**

### T4-08 [🔴 P1][S] สกุลเงิน: เส้น "⚡ ยืนยัน" ได้ USD · เส้น "📝 แก้ในฟอร์มก่อน" ได้ THB เสมอ
- ไฟล์: `OcrService.cs:4941` `Currency = InferCurrency(result.RawTextContent) ?? "THB"` ·
  `document-scan.html:4173` `const currency = scan.currency || scan.detectedCurrency || 'THB';`
- ทำไมพัง: `OcrResultResponse` **ไม่มีฟิลด์ Currency เลย** (grep `Currency` ใน `OcrDtos.cs` = 0)
  ⇒ ทั้ง `scan.currency` และ `scan.detectedCurrency` เป็น `undefined` ตลอดกาล ⇒ handoff ส่ง
  `'THB'` เสมอ ⇒ `documents.html:1946` เซ็ต dropdown เป็น THB
- ผลกระทบ: ใบ USD/EUR ที่ผ่านเส้น B ถูกบันทึกเป็นบาทด้วยตัวเลขเดิม = **ยอดผิด ~35 เท่า** เงียบสนิท
  (บทเรียนในโค้ดเองเขียนไว้ว่า "ตัวเลขเท่าเดิมแต่ความหมายผิด") · และผู้ใช้ **ไม่มีทางเห็น**
  ว่าระบบอ่านสกุลอะไรได้ เพราะ review ไม่แสดงสกุลเงินเลย
- defect class: **"อ่านค่าที่ไม่มีใครเขียน"** (ญาติของบทเรียน localStorage key) + "สองเส้นทางให้คนละคำตอบ"
- ทางแก้: เพิ่ม `Currency` ลง `OcrResultResponse` (จาก `InferCurrency` ตัวเดิม — อย่าคำนวณซ้ำใน JS)
  + แสดงเป็นช่องเลือกในโมดัลเมื่อ ≠ THB (พร้อมป้าย "อ่านจากกระดาษ") + เพิ่มใน `OcrCorrectionRequest`
- ความมั่นใจ: **สูง**

### T4-09 [🟠 P2][S] เครดิตเทอมแสดงสองที่ในโมดัลเดียว — ช่องแก้ได้ + ช่องอ่านอย่างเดียวที่ไม่อัปเดตตาม
- ไฟล์: `document-scan.html:2262–2266` (input `revPaymentTermsDays`) และ `:2343`
  `${scan.paymentTermsDays ? '<div class="review-field"><label>เงื่อนไขชำระ</label><span …>' + scan.paymentTermsDays + ' วัน</span></div>' : ''}`
- ทำไมพัง: บล็อกที่สองเป็นซากของ UI รุ่นก่อนที่ยังอ่านอย่างเดียว — render จาก `scan.*`
  ไม่ผูกกับ input ⇒ ผู้ใช้แก้ 30 → 60 แล้วยังเห็น "30 วัน" อยู่ข้างล่างในจอเดียวกัน
- ผลกระทบ: UX สับสน (ผู้ใช้ไม่รู้ว่าอันไหนคือค่าจริง) — ไม่ทำให้ข้อมูลผิด เพราะ
  payload อ่านจาก input · แต่เป็น "จอเดียวเล่าสองเรื่อง" แบบเดียวกับที่ไฟล์นี้ห้ามไว้
- ทางแก้: ลบบล็อก `:2343` ทิ้ง
- ความมั่นใจ: **สูง**

### T4-10 [🟠 P1][S] หน้าเว็บไม่มีช่อง "เหตุผลทางธุรกิจ" ทั้งที่ DTO + persist + มือถือมีครบแล้ว
- ไฟล์: `OcrCorrectionRequest.Notes` (`OcrDtos.cs:222–231`, doc-comment อธิบาย §65 ตรี(3)/(14)) ·
  `OcrService.cs:3607` persist ลง `result.UserNotes` · `OcrService.cs:4981` ไหลเข้า `Document.Notes` ·
  `mobile-expense.html:75/205` มีช่องและส่งจริง · **`document-scan.html` grep `notes:` = มีจุดเดียวที่
  `:4194` ซึ่งเป็นข้อความระบบ** และ `_buildReviewCorrection()` (`:3045`) **ไม่มีคีย์ `notes` เลย**
- ทำไมพัง: เส้นทางหลัก (เดสก์ท็อป) ที่ทำเอกสารส่วนใหญ่ **ไม่มีที่ให้พิมพ์เหตุผล** ·
  ที่แย่กว่า: เส้น B (`openInDocumentForm:4194`) **เขียนทับ** ด้วย
  `"สร้างจากการสแกน OCR: <ไฟล์>"` ⇒ ถ้าผู้ใช้เคยพิมพ์เหตุผลผ่านมือถือแล้วมาแก้ในเว็บ เหตุผลนั้นหาย
- ผลกระทบ: §65 ตรี(3)/(14) — รายจ่ายที่พิสูจน์ไม่ได้ว่าเกี่ยวกับกิจการ = **รายจ่ายต้องห้าม**
  (ค่ารับรอง/ค่าเดินทาง/ของขวัญ ที่ต้องมีคำอธิบาย)
- defect class: **"ครึ่งเซิร์ฟเวอร์ของการแก้ ship ไปคนเดียว"** (ซ้ำรูปเดิมกับ `VendorBranchCode` เป๊ะ)
- ทางแก้: เพิ่ม `<textarea id="revNotes">` ในโมดัล + `notes: v('revNotes')` ใน `_buildReviewCorrection`
  + เพิ่ม `UserNotes` ลง `OcrResultResponse` เพื่อ hydrate ค่าที่มือถือพิมพ์ไว้ + เส้น B ให้ต่อท้ายแทนทับ
- ความมั่นใจ: **สูง**

### T4-11 [🔴 P1][S] `glAccountAiFeedbackId` — สายขาดกลางทาง ⇒ เส้น "แก้ในฟอร์ม" ไม่เคย CAPTURE
- ไฟล์: `document-scan.html:4228` `glAccountAiFeedbackId: scan.glAccountAiFeedbackId || null` ·
  `documents.html:2076–2078` `if (payload.glAccountAiFeedbackId) { firstFid.dataset.feedbackId = … }` ·
  `documents.html:8191` `if (fid) line.glAccountAiFeedbackId = fid;` (ส่งกลับตอน save) ·
  entity มีจริง `Models/Entities/Intelligence.cs:240` · **`OcrResultResponse` ไม่มีฟิลด์นี้**
- ทำไมพัง: ทั้งฝั่งผลิตและฝั่งบริโภคเขียนถูก แต่ DTO ตรงกลางไม่มีฟิลด์ ⇒ `scan.glAccountAiFeedbackId`
  เป็น `undefined` ตลอด ⇒ `dataset.feedbackId` ไม่เคยถูกตั้ง ⇒ ผู้ใช้เปลี่ยนผังบัญชีในฟอร์ม
  **ระบบไม่มีทางรู้ว่าคำตอบจริงคืออะไร** (กฎเหล็ก #1 ขั้น CAPTURE ขาด)
- defect class: **"สองฝั่งเขียนถูกทั้งคู่ แต่สายขาดหนึ่งเส้น"** (ตรงกับบทเรียน `ApiKey.Scopes`)
- หมายเหตุ: เกี่ยวข้องกับ **T3-03** (เส้น 1-click ไม่ CAPTURE) แต่**คนละจุด** — นี่คือเส้น
  "📝 แก้ในฟอร์มก่อน" ซึ่งเป็นเส้นที่ผู้ใช้แก้ผังบัญชีบ่อยที่สุด
- ทางแก้: เพิ่ม `Guid? GlAccountAiFeedbackId` ลง `OcrResultResponse` + map ใน `MapToResponse`
- ความมั่นใจ: **สูง** (`grep -rn GlAccountAiFeedbackId` — DTO ไม่ปรากฏ)

### T4-12 [🟠 P2][S] "บันทึก & สอนระบบ" กดได้ครั้งเดียวต่อการเปิดโมดัล — ปุ่มค้าง disabled ตลอดไป
- ไฟล์: `document-scan.html:3166–3189`
- โค้ด: `btn.disabled = true; … try { … btn.textContent = '✅ บันทึกและสอนแล้ว'; btn.className='btn btn-success'; }`
  — **branch สำเร็จไม่เคยตั้ง `btn.disabled = false`** (branch catch ตั้ง)
- ทำไมพัง: ผู้ใช้แก้ 3 ช่อง → กดสอน → เห็นค่าอีกช่องผิด → แก้ต่อ → **กดสอนอีกไม่ได้**
  ต้องปิดแล้วเปิดโมดัลใหม่ · (การแก้ยังถูก persist ตอนกดสร้างเพราะ `_persistReviewEdits`
  แต่ **ผู้ใช้ไม่รู้** และถ้าเขาปิดโมดัลเลย การแก้รอบสองหาย)
- ทางแก้: `finally { btn.disabled = false; }` + คงข้อความ "✅ บันทึกแล้ว" ชั่วคราวแล้วคืนป้ายเดิม
- ความมั่นใจ: **สูง**

### T4-13 [🟠 P2][S] การ์ด "ไม่สำเร็จ" ไม่บอกสาเหตุและไม่บอกทางไปต่อ ทั้งที่เซิร์ฟเวอร์เขียนไว้แล้ว
- ไฟล์: `OcrService.cs:2393` `ProcessingNotes += "\n[Error] " + ex.Message` · `:550` `"[ทุก Tier ล้มเหลว] …"` /
  `"[Azure-only] …"` · `document-scan.html:1405` `statusBadge = '<span class="badge badge-danger">ไม่สำเร็จ</span>'`
- ทำไมพัง: การ์ดโชว์แค่ป้ายแดง · เหตุผลอยู่ใน `processingNotes` ซึ่ง render เฉพาะใน **debugPanel**
  (`:2469`) ที่ต้องกดปุ่ม "🔍 ข้อมูลดิบ" ก่อน ⇒ ผู้ใช้ทั่วไปเห็น "ไม่สำเร็จ" เปล่า ๆ
- ผลกระทบ: เคส "Azure โควตาหมด" กับ "ไฟล์เป็นภาพเปล่า" ต้องทำคนละอย่าง แต่หน้าจอบอกเหมือนกัน
  · `_rawTextPanel` (`:1675`) แก้ปัญหานี้ได้ดีมากแล้ว **แต่มันอยู่ใน debug panel เหมือนกัน**
- ทางแก้: ดึง `[Error]`/`[ทุก Tier ล้มเหลว]`/`[Azure-only]` ผ่าน `parseScanNotes` เดียวกับ T4-01
  แล้วโชว์ใต้ป้ายแดงบนการ์ด + ปุ่ม "แกะใหม่" ที่มีอยู่แล้ว
- ความมั่นใจ: **สูง**

### T4-14 [🟠 P1][S] `review-queue.html` **ไม่มีทางเข้าเลยทั้งระบบ**
- ไฟล์: `wwwroot/pages/review-queue.html` (มี `Layout.init('review-queue')` + เรียก
  `GET /ocr/review-queue`) · `grep -rn "review-queue" wwwroot/js/layout.js` = **0 จุด** ·
  ทั้งเรพอ้างถึงชื่อนี้แค่ในคอมเมนต์ `document-scan.html:542`
- ทำไมพัง: หน้านี้คือ "คิวใบที่รอคนตรวจ" — ซึ่งเป็นปลายทางของทุกใบที่ระบบตัดสินใจเองไม่ได้
  (batch ไม่ผ่าน / มีลายมือ / เป็นสินทรัพย์ / `[APPROVE-SKIP]`) แต่ไม่มีเมนู ไม่มีลิงก์
  ⇒ **ไม่มีใครเคยเปิด** ⇒ ใบที่ตกจากทางด่วนไม่มีที่รวม
- defect class: **"ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้"**
- ทางแก้: ต่อสาย — เพิ่มเมนูใต้กลุ่ม "เอกสาร" + badge จำนวนค้าง และให้เป็นปลายทางของ
  T4-01/T4-02 (ใบที่ยังไม่อนุมัติจาก batch) · ถ้าไม่ต่อสาย **ต้องลบ** (ห้ามปล่อยไว้)
- ความมั่นใจ: **สูง** (grep 3 นามสกุล)

### T4-15 [🟠 P1][M] สามทางเข้า สามนโยบาย — LINE auto-create + อนุมัติ 1 แตะ · เว็บบังคับ review · มือถือสร้าง Draft เงียบ
- ไฟล์: `LineBotService.cs:480` `ScanAsync(…, autoCreate: true)` + `:533` การ์ด Flex ปุ่ม
  "✅ อนุมัติเลย" (postback `approve:{docId}`) · `OcrController.cs:69–92` เว็บ = `autoCreate` null ⇒ false ·
  `mobile-expense.html:208` `create-document?targetType=Expense` (ไม่ `approve=true`) ⇒ Draft
- ทำไมเป็นปัญหา: กระดาษใบเดียวกันได้ผลต่างกันตามช่องทาง —
  (ก) **LINE**: การ์ด Flex แสดงแค่ ชนิด/ผู้ขาย/ยอดรวม/VAT — **ไม่แสดง `[VAT-CLAIM]` §82/5,
  `complianceIssues`, `[Σ-GAP]`, `[DATE-UNKNOWN]`** ⇒ ผู้ใช้แตะ "อนุมัติเลย" โดยเห็นข้อมูล
  **น้อยกว่า** เว็บมาก ทั้งที่เป็นการลงบัญชีจริงเหมือนกัน
  (ข) **มือถือ**: ตอบ "✅ ส่งเบิกแล้ว" แต่ใบเป็น Draft — ถ้าไม่มีใครไปอนุมัติ ค่าใช้จ่ายนั้น
  ไม่มีอยู่ในระบบเลย และหน้าจอไม่บอกว่า "รออนุมัติจากใคร"
  (ค) มือถือ **ไม่แสดงคำเตือน §82/5 / RD compliance เลยสักตัว**
- ทางแก้: (1) การ์ด Flex เพิ่มบรรทัดคำเตือนจาก `parseScanNotes` + `complianceIssues` และ
  **ปิดปุ่ม "อนุมัติเลย" เมื่อมี issue severity=error** (2) มือถือแสดงสถานะจริง + คำเตือน
  (3) นโยบาย auto-create/auto-approve ควรเป็น **ค่าตั้งระดับบริษัท** ตัวเดียว ไม่ใช่ hardcode
  ต่อช่องทาง (ดู §3 ข้อเสนอ)
- ความมั่นใจ: **สูง** สำหรับข้อเท็จจริง · **กลาง** สำหรับผลกระทบ (ยังไม่ได้อ่าน postback handler
  ว่ามีด่านตรวจอะไรก่อน approve — `LineBotService.cs:643` มี try/catch รายงาน error)

### T4-16 [🟠 P2][S] มือถือกับเว็บส่ง "หมวดค่าใช้จ่าย" คนละภาษา ลงช่องเดียวกัน
- ไฟล์: `mobile-expense.html:52–57` `<option value="travel|meals|office|fuel|utilities|other">` →
  ส่งเข้า `correct` เป็น `expenseCategory` (`:203`) · `OcrService.cs:3589`
  `result.ExpenseCategory = correction.ExpenseCategory` (รับตรง ๆ ไม่ normalize) ·
  `ExpenseCategoryResolver.cs:34` ระบุชัดว่า `string Category, // Thai display label ("ค่าน้ำมัน")` ·
  `document-scan.html:2295–2307` dropdown เป็นภาษาไทย 13 ค่า
- ทำไมพัง: ช่องเดียวกันมี **2 คำศัพท์** ⇒ (1) เปิดใบที่ส่งจากมือถือในหน้า review เดสก์ท็อป
  dropdown หาค่า `"meals"` ไม่เจอ ⇒ แสดง "-- เลือก --" = **ค่าที่ผู้ใช้เลือกหายจากจอ**
  (2) `_categoryLearner` (ถ้าถูกเรียก) จะเรียนคำศัพท์ปน (3) กติกาที่ผูกกับหมวด
  (§82/5(4) ค่ารับรอง) เทียบชื่อไทย ⇒ `"meals"` **ไม่ถูกจับเป็นค่ารับรอง**
- ผลกระทบภาษี: หมวด `meals` = "อาหาร/รับรอง" คือ §65 ตรี(4) + §82/5(4) — มือถือ
  **ไม่มีคำเตือนใด ๆ** และหมวดไม่เข้ากติกาไทย ⇒ ค่ารับรองไหลเข้าเป็นรายจ่ายปกติ + เคลม VAT
- defect class: **"ตารางความรู้เชิงกฎหมายที่คัดลอกไปเขียนใหม่"** (ลิสต์หมวดถูกพิมพ์ 3 ที่:
  server rules · document-scan.html · mobile-expense.html)
- ทางแก้: endpoint เดียว `GET /ocr/expense-categories` สร้างจาก `ExpenseCategoryResolver`
  แล้วทั้งสองหน้า render จากนั้น (กลไกเดียวกับ `MENU_SECTIONS`/`complianceIssues`)
- ความมั่นใจ: **สูง** สำหรับ vocabulary mismatch · **กลาง** สำหรับผลต่อ §82/5
  (ต้องเช็คว่า `ProhibitedInputVatScreener` เทียบด้วย category หรือด้วย description)

### T4-17 [🟠 P2][M] Confidence รายบรรทัดไม่มีเลย — บรรทัดคือจุดที่ OCR ผิดบ่อยที่สุด
- ไฟล์: `Helpers/OcrFieldKeys.cs:34–50` — คีย์ทั้ง 17 ตัวเป็น **ระดับหัวใบ** ไม่มีคีย์ของบรรทัด ·
  `OcrLineItemDto` (`OcrDtos.cs:153–172`) ไม่มีฟิลด์ confidence · ตาราง review (`:2374`)
  ไม่มีป้าย/ไฮไลต์ใด ๆ ต่อแถว
- ทำไมสำคัญ: กฎเหล็ก #3 ข้อ 3 บอกว่า field ที่ confidence < 0.85 ต้องไฮไลต์เหลือง —
  แต่ **"ทุกบรรทัดในตาราง" ไม่มีคะแนนเลย** ⇒ ผู้ใช้ที่กด Enter ยืนยันจะไม่มีทางรู้ว่า
  บรรทัดไหนน่าสงสัย (โดยเฉพาะใบที่ `lineSplitUsedAi = true` ซึ่ง AI แตกบรรทัดให้)
- ทางแก้ (M): เพิ่ม `Confidence` ลง `OcrLineItemDto` + `OcrFieldKeys.Line(i, field)` แล้ว
  ไฮไลต์แถว · ระยะสั้น (S): ใช้ `[Σ-GAP]` (T4-03) เป็นสัญญาณระดับตาราง — ถ้าผลรวมบรรทัด
  ≠ ยอดหัวใบ ให้ตีกรอบแดงรอบตาราง + ห้ามอยู่ใน batch
- ความมั่นใจ: **สูง**

### T4-18 [🟠 P3][S] 3 คีย์ confidence ที่เซิร์ฟเวอร์ผลิตจริง ไม่มีช่องปลายทางให้ไฮไลต์
- ไฟล์: `OcrFieldKeys.cs` มี `WhtRate` · `ExpenseCategory` · `DebitAccount` ·
  แต่ map ใน `document-scan.html:2503–2517` มี 14 คู่ **ไม่มี 3 ตัวนี้**
- ผลกระทบ: อัตรา WHT ที่อ่านมาไม่มั่นใจจะไม่ถูกไฮไลต์ ทั้งที่เป็นตัวเลขที่ผิดแล้วผู้จ่าย
  รับผิดตาม §54 · ทางแก้: เติม 3 คู่ (`revWhtRate` · `revExpenseCategory` · `revDebitAccount`)
- ความมั่นใจ: **สูง**

### T4-19 [🟠 P3][S] ข้อความสถานะอัปโหลดโชว์ชื่อ enum ภาษาอังกฤษให้ผู้ใช้
- ไฟล์: `document-scan.html:966–968` `` `สแกนสำเร็จ — ระบบแนะนำ: ${scan.targetDocumentType}` ``
- ผลกระทบ: ผู้ใช้เห็น "ระบบแนะนำ: PaymentVoucher" / "PurchaseInvoice" — ทั้งที่ไฟล์นี้มี
  `typeMap`/dropdown ที่แปลเป็นไทยอยู่แล้วห่างกันไม่กี่ร้อยบรรทัด (`:1383` และ `:2128`)
- ทางแก้: ใช้แผนที่ label ตัวเดียว (ควรมาจากเซิร์ฟเวอร์ตามผลตรวจทีม I) — **ห้ามพิมพ์ชุดที่ 3**
- ความมั่นใจ: **สูง**

---

## §3 Correction loop — ช่องที่แก้ได้ vs ช่องที่ส่งจริง

เทียบ `_buildReviewCorrection()` (`document-scan.html:3045–3082`) กับ `OcrCorrectionRequest`
(`OcrDtos.cs:176–232`):

| ทิศ | รายการ |
|---|---|
| **แก้ได้ใน UI แต่ไม่ส่ง (silent no-op)** | **ไม่มี** ✅ — ทั้ง 20 ช่องที่มี `id="rev*"` ถูกส่งครบ (`vendorBranchCode`/`buyerTaxId`/`buyerBranchCode`/`vendorAddress`/`buyerName`/`buyerAddress`/`paymentTermsDays` เพิ่งต่อสายครบแล้ว — ยืนยันว่า E-OCR-05 ปิดจริง) |
| **เซิร์ฟเวอร์รับได้แต่ UI ไม่มีช่อง** | `Notes` (T4-10) · และ **ไม่มี** `DiscountAmount` / `Currency` / `WhtIncomeType` ในทั้งสองฝั่ง (T4-06/07/08) |
| **แก้ผ่าน endpoint แยก (ไม่ผ่าน correct)** | บรรทัด: `POST /ocr/{id}/line-fields` (desc/qty/price/accountCode) · `modify-line` (add/delete) · `set-line-project` · `match-contact` — ทั้งหมด **บันทึกทันทีที่ `onchange`** (ดี) แต่ **ไม่มี vatRate/unit** (T4-04) |
| **บันทึกอัตโนมัติก่อนสร้าง** | ✅ `_persistReviewEdits()` ถูกเรียกใน `createDocFromReview` (`:3205`) และ `onRoleChanged` (`:3122`) ⇒ ผู้ใช้ไม่ต้องกด "สอนระบบ" ก่อน (ปิดบั๊กเดิม "ค่าที่เลือกหาย") |
| **หลัง handoff ไป documents.html แก้แล้วบันทึกอะไรไหม** | ⚠️ **บางส่วน** — `documents.html:8191` ส่ง `line.glAccountAiFeedbackId` กลับได้ **แต่ค่านั้นไม่เคยถูกตั้ง** (T4-11) ⇒ ในทางปฏิบัติ **การแก้ในฟอร์มไม่ถูก capture เลย** · ช่องหัวใบ (ผู้ขาย/เลขที่/วันที่/สาขา) ที่ผู้ใช้แก้ในฟอร์ม **ไม่มีเส้นย้อนกลับไป `SubmitCorrectionAsync`** ⇒ ระบบไม่เรียนจากการแก้ที่เกิดในหน้าเอกสาร |

**สรุปข้อ (4) ของโจทย์**: ไม่พบ silent no-op ฝั่ง correction แล้ว (เป็นข่าวดี — รอบก่อนมี 3 ช่อง)
แต่ **การแก้ที่เกิดหลัง handoff ยังหายทั้งหมด** ซึ่งเป็นเส้นที่ผู้ใช้แก้เยอะที่สุด

---

## §4 Confidence UX (ข้อ 3 ของโจทย์)
- ✅ อ่าน `scan.fieldConfidence` จริง (`:1990`) · ป้าย % ต่อช่อง (`:1977`) ·
  ไฮไลต์ <0.85 ผ่าน `Layout.applyOcrConfidenceHints` (`:2503`) · **ไม่ fallback ไป
  confidence ทั้งใบ** — ช่องที่ไม่มีคะแนนแสดง "—" พร้อม tooltip (ถูกต้องตาม "ไม่รู้ = บอกว่าไม่รู้")
- ✅ ป้ายเกรด A–D + tooltip เหตุผล/คำแนะนำบนการ์ด (`:1534`) · ป้าย "✋ ลายมือ"
- ❌ **บรรทัดไม่มี confidence เลย** (T4-17) · ❌ 3 คีย์ไม่มีปลายทาง (T4-18)
- ⚠️ ช่องที่ระบบ **เดา** ให้ (สาขา `00000` บนเส้น B, VAT 7% บนเส้น B) ไม่มีป้ายบอกว่าเป็นการเดา

---

## §5 Error / Edge UX (ข้อ 5 ของโจทย์)

| เรื่อง | สถานะ | หมายเหตุ |
|---|---|---|
| เตือนไฟล์ซ้ำ (client) | ⚠️ อ่อน | `checkDuplicate:1043` เทียบ **ชื่อไฟล์** กับ `this.scans` ซึ่งเป็นสแกน **หน้าปัจจุบัน** เท่านั้น (มี pagination `:1566`) ⇒ ไฟล์เดิมที่อยู่หน้า 2 หรือถูกเปลี่ยนชื่อ = ไม่เตือน · ตัวจริงคือ dedup ด้วย SHA-256 ฝั่งเซิร์ฟเวอร์ (`OcrScanSnapshot`) จึงไม่เป็นบั๊กด้านข้อมูล แต่ modal นี้ให้ความมั่นใจเกินจริง (**T4-20 P3**) |
| เตือนเอกสารซ้ำ (business) | ✅ ดี | `openInDocumentForm:4260–4283` เรียก `checkDuplicateDocument` ก่อนพาไปฟอร์ม + confirm พร้อมรายการใบที่ชน · **แต่มีเฉพาะเส้น B** — เส้น "⚡ ยืนยัน + ลงบัญชี" ไม่ผ่านด่านนี้ (server มี `duplicate gate :4467+` แยกต่างหาก) |
| §82/5 | ✅ banner แดง/เหลืองแยกเหตุ + ดึงครบทุกบรรทัด (`:2033/:2044`) | เส้นมือถือ **ไม่มีเลย** (T4-15) |
| "อาจอัพโหลดผิดบริษัท" | ✅ | ย้ายมาเซิร์ฟเวอร์แล้ว (`complianceIssues`) หน้าเว็บแสดงอย่างเดียว (`:1487–1501`) — ยืนยันว่าบทเรียนเดิมยังถูกบังคับอยู่ |
| ข้อความ Failed | ❌ | T4-13 |
| retry semantics | ✅ ดีมาก | `retryScan:1646` ใช้ scanId (ไม่ใช่ fileAttachmentId — บั๊กเดิมปิดแล้ว) · ข้อความมาจากเซิร์ฟเวอร์ · `_rawTextPanel:1675` แยก `Cached`/`EtaxXml`/engine จริง ไม่เดาสาเหตุแทนผู้ใช้ |
| batch หลายไฟล์ | ✅ | `handleFiles:857` มี worker pool + แสดงทุกแถวทันที + กัน dup in-flight · modal ซ้ำเข้าคิวทีละอัน (`_dupModalChain`) + MutationObserver กัน promise ค้างเมื่อกด ESC |
| mobile-expense | ⚠️ | ใช้ pipeline เดียวกันจริง (`/ocr/upload` → `/correct` → `/create-document`) · prefill 3 ช่อง · **แต่ไม่มี review, ไม่มีคำเตือนภาษี, สร้าง Draft แล้วบอกว่า "ส่งเบิกแล้ว"** (T4-15) · หมวดคนละคำศัพท์ (T4-16) |
| LINE bot | ⚠️ | pipeline เดียวกัน + `autoCreate:true` + การ์ด Flex 1 แตะอนุมัติ · ✅ **มีด่านสิทธิ์ครบ** (`LineBotService.cs:600–620`: UserLoginPolicy → tenant guard → `DocumentPermissionHelper.CanApproveAsync`) · ❌ การ์ดไม่แสดงคำเตือนใด ๆ (T4-15) |

---

## §6 Dead code / drift (ข้อ 6 ของโจทย์)

รันจาก `/home/user/Accounting`:
```
python3 tools/js_dup_method_check.py        → 0 จุด
python3 tools/localstorage_key_check.py     → 0 ตัว (คีย์ที่อ่านได้ 28)
python3 tools/dead_link_check.py            → 0 จุด
python3 tools/html_attr_escape_check.py     → 0 จุด (169 ไฟล์)
```
สแกนเพิ่มเองบน `document-scan.html` (4,316 บรรทัด):
- `this.X()` / `Page.X()` ที่ไม่มีนิยาม → **0 จุดจริง** (ผู้ต้องสงสัย 6 ตัวเป็น false positive ทั้งหมด:
  `_duplicateResolve`/`_poMapPick`/`_poPickerChange` เป็น property ที่ assign runtime · `closest` เป็น DOM ·
  `rescan` อยู่ในคอมเมนต์เตือนว่า "ถูกลบแล้ว อย่าสร้างกลับมา" · `registerAsset` อยู่ในคอมเมนต์เก่า
  ที่ชื่อไม่ตรงของจริง `openAssetRegisterDialog` → **คอมเมนต์ค้าง ควรแก้** )
- `typeof this.x === 'function'` → **0 จุด** (ดีมาก — ไฟล์นี้ไม่มี smell นั้น)
- `getElementById` ที่ไม่มี id ในหน้า → 2 ตัว (`aiVerdictPanel`, `ocrAiRawOverlay`) ทั้งคู่**สร้าง
  ด้วย JS ก่อนอ่าน** → ไม่ใช่บั๊ก
- **สำเนามือของกติกาเซิร์ฟเวอร์ใน JS ที่ยังเหลือ**: `:1365` สตริง `'อนุมัติไม่สำเร็จ'` (**T4-02**) ·
  `:4026` สูตร VatRate (**T4-04**) · `:4215`/`documents.html:1918` `'00000'` (**T4-05**) ·
  `:2295` ลิสต์หมวดค่าใช้จ่าย (**T4-16**) · `:3094` `_resolveDocSide` — ตัวนี้ **ถูกต้อง** เพราะใช้
  `scan.documentSideMap` จากเซิร์ฟเวอร์เป็นหลัก และ legacy list ใช้เฉพาะเซิร์ฟเวอร์รุ่นเก่า
- **`review-queue.html` ไม่มีเมนู** (T4-14)

---

### T4-20 [🟠 P3][S] modal "ไฟล์ซ้ำ" ให้ความมั่นใจเกินจริง — เทียบชื่อไฟล์กับสแกน "หน้าปัจจุบัน" เท่านั้น
- ไฟล์: `document-scan.html:1043–1048` `this.scans.find(s => s.originalFileName.toLowerCase() === file.name.toLowerCase())`
  · `this.scans` ถูกเซ็ตจาก `loadScans()` ซึ่งมี pagination (`renderPagination:1566`)
- ผลกระทบ: ไฟล์เดิมที่อยู่หน้า 2 ขึ้นไป หรือถูกเปลี่ยนชื่อ (มือถือตั้งชื่อใหม่ทุกครั้ง) จะไม่เตือน
  ⇒ ผู้ใช้เข้าใจว่า "ระบบเช็คซ้ำให้แล้ว" ทั้งที่เช็คได้แค่เศษเสี้ยว · ตัวจริงคือ dedup ด้วย
  SHA-256 ฝั่งเซิร์ฟเวอร์ (จึงไม่เสียหายด้านข้อมูล)
- ทางแก้: คำนวณ SHA-256 ฝั่ง client (SubtleCrypto) แล้วถาม endpoint เดียว หรือเปลี่ยนข้อความ
  เป็น "ชื่อไฟล์ซ้ำกับใบที่แสดงอยู่" ให้ตรงกับสิ่งที่ตรวจจริง
- ความมั่นใจ: **สูง**

---

## §7 🔵 ข้อเสนอ Target UX "อัพแล้วจบ" (พร้อม "ฝ่ายค้านจะว่าอย่างไร")

### 🔵 T4-P1 [P1][M] `ScanNotes` — ตัวอ่านโน้ตกลางตัวเดียว + แถบ "สิ่งที่ระบบตัดสินใจแทนคุณ"
ปิด T4-01/02/03/13 พร้อมกัน. เซิร์ฟเวอร์**ควร**เลิกส่งเป็นข้อความปนกันแล้วส่งเป็น
`List<ScanNoteDto>{code, severity, message, action}` (code = `APPROVE_SKIP`/`SIGMA_GAP`/…)
หน้าเว็บวาดอย่างเดียว
- *ฝ่ายค้าน*: "ProcessingNotes เป็น free text มาตลอด แตกเป็น DTO = งานใหญ่"
- *ตอบ*: ระยะแรกทำ `parseScanNotes()` ใน JS **ที่เดียว** (S, ~40 บรรทัด) แล้วค่อยย้ายไป
  เซิร์ฟเวอร์ — แต่ **ห้ามให้แต่ละหน้าจอ regex เอง** ซึ่งเป็นสิ่งที่เกิดอยู่ตอนนี้ (3 จุด 3 แบบ)
  · precedent มีแล้ว: `complianceIssues` ย้ายมาเซิร์ฟเวอร์สำเร็จและ drift เป็นศูนย์

### 🔵 T4-P2 [P1][M] นโยบาย auto-approve ตาม tier ความมั่นใจ = **ค่าตั้งระดับบริษัท** ตัวเดียว
`CompanyOcrPolicy { AutoApproveMinConfidence, AutoApproveMaxAmount, RequireReviewWhenVatClaimBlocked,
RequireReviewForNewVendor, AllowLineAutoApprove }` — ใช้ร่วมกัน **ทั้ง 3 ช่องทาง** (เว็บ/LINE/มือถือ)
แทนการ hardcode `autoCreate:true` ที่ LINE และ `false` ที่เว็บ
- *ฝ่ายค้าน*: "auto-approve = ลงบัญชีโดยไม่มีคนดู เสี่ยงกฎหมาย"
- *ตอบ*: (ก) ค่าเริ่มต้น = ปิด ต้องเจ้าของกิจการเปิดเอง (ข) เพดานยอด (ค) ห้าม auto เมื่อมี
  `complianceIssues severity=error` / `[VAT-CLAIM]` / `[Σ-GAP]` / `[DATE-UNKNOWN]` / ผู้ขายใหม่ /
  ลายมือ / สินทรัพย์ — เกณฑ์พวกนี้ **มีอยู่แล้วครบใน `_isBatchApprovable` (`:1314`)** แค่ยกขึ้นเป็น
  นโยบายที่เซิร์ฟเวอร์บังคับแทนที่จะเป็น JS ในหน้าเดียว (ง) ทุกใบที่ auto ต้องเข้า
  `review-queue` (T4-14) เพื่อสุ่มตรวจย้อนหลัง
- *ฝ่ายค้าน 2*: "ผู้ใช้จะไม่กล้าเปิด" · *ตอบ*: เริ่มจาก **shadow mode** — ระบบบอกว่า "ถ้าเปิด
  อัตโนมัติ เดือนนี้จะประหยัด N คลิก และไม่มีใบไหนถูกแก้หลังอนุมัติเลย" จากสถิติจริงของ tenant นั้น

### 🔵 T4-P3 [P2][M] Review surface เดียว — ยุบ "review modal" กับ "ฟอร์มเอกสาร" ไม่ให้มีสองมาตรฐาน
วันนี้มี **2 mapping ที่เขียนมือคนละชุด** (`CreateDocumentFromScanAsync` กับ `openInDocumentForm`
handoff 30+ คีย์) ⇒ ทุกฟิลด์ใหม่ต้องแก้ 2 ที่ และวันนี้ก็ drift แล้วจริง (T4-04/05/08/10)
- ข้อเสนอ: ปุ่ม "📝 แก้ในฟอร์มก่อน" → **สร้าง Draft ด้วย `CreateDocumentFromScanAsync` แล้วเปิด
  ใบนั้นในฟอร์มแก้ไข** (ไม่มี handoff payload อีกต่อไป) ⇒ mapping เหลือชุดเดียว
- *ฝ่ายค้าน*: "ผู้ใช้อาจไม่อยากให้มีใบเกิดขึ้นถ้าสุดท้ายไม่บันทึก"
- *ตอบ*: Draft ใช้เลข `DRAFT-{guid}` ไม่กิน sequence §86/4 อยู่แล้ว (`OcrService.cs:4909`)
  และลบ Draft ไม่ทิ้ง gap — ต้นทุนของการมี Draft ค้างต่ำกว่าต้นทุนของ mapping สองชุดที่ drift

### 🔵 T4-P4 [P2][M] Evidence crop ในบรรทัดที่สงสัย
โมดัลมีรูปเต็มอยู่แล้ว (`:1936`) แต่ผู้ใช้ต้องกวาดตาหาเอง — ถ้า Azure DI คืน bounding box
(มีใน `AzureDocumentIntelligenceService`) ให้ตัดเฉพาะกรอบของช่องนั้นมาโชว์ใต้ช่องที่ confidence ต่ำ
- *ฝ่ายค้าน*: "เส้น Tesseract/Python ไม่มี bbox ⇒ ทำได้ไม่ครบ" · *ตอบ*: มีก็โชว์ ไม่มีก็ไม่โชว์
  (ปฏิบัติเหมือน confidence: "ไม่รู้ = ไม่แสดง" ไม่ใช่แสดงกรอบมั่ว)

### 🔵 T4-P5 [P2][S] Keyboard flow เต็มรูป
วันนี้มี Enter = ยืนยัน (ดี) แต่ยังขาด: `Esc` ปิด (มีจาก Layout) · **`J`/`K` หรือ `→` ไปใบถัดไป
โดยไม่ปิดโมดัล** · `Ctrl+Enter` ยืนยันแม้โฟกัสอยู่ในช่องกรอก (ตอนนี้ `:2571` return ทันทีเมื่อ
โฟกัสอยู่ใน INPUT — ผู้ใช้ที่เพิ่งพิมพ์ค่าต้อง Tab ออกก่อน)
- *ฝ่ายค้าน*: "คนบัญชีใช้เมาส์" · *ตอบ*: คนที่ทำ 200 ใบ/เดือนไม่ใช้ · ต้นทุนต่ำมาก

### 🔵 T4-P6 [P1][S] ประสบการณ์เมื่อ **ปิด AI ทั้งหมด** (กฎเหล็ก #1 kill-switch)
ตรวจแล้ว: ป้าย `🤖 AI แนะนำ` / `🤖 AI แตกรายการให้` / ปุ่ม "🤖 ขอ AI ตรวจอีกครั้ง" /
"🔍 ดู AI response เต็ม" **แสดงตามค่าจาก server** (`glAccountUsedAi`, `lineSplitUsedAi`) จึงหายไป
เองเมื่อ AI ปิด ✅ — **แต่ปุ่ม `reviewAiBtn` (`:456`) แสดงเสมอ** ไม่ว่าจะมี provider active หรือไม่
⇒ ผู้ใช้กดแล้วได้ error/คำตอบว่างโดยไม่รู้ว่าเพราะอะไร
- ทางแก้ (S): เพิ่ม `aiEnabled` ลงผลลัพธ์ (หรือ `/ocr/engines` ที่หน้านี้เรียกอยู่แล้ว `:689`)
  แล้วซ่อน/disable ปุ่มพร้อมเหตุผล — ตามกติกา "ห้ามโชว์ปุ่มที่จะพังแน่ ๆ"
- *ฝ่ายค้าน*: "AI ปิดชั่วคราวเพราะงบหมด ปุ่มควรอยู่" · *ตอบ*: อยู่ได้ แต่ต้อง **บอกเหตุผล +
  บอกว่าใครเปิดให้ได้** ไม่ใช่กดแล้วเงียบ

---

## §"ตรวจแล้วไม่ใช่บั๊ก" (ทีมอื่นไม่ต้องเสียเวลาซ้ำ)
1. **Enter = ยืนยัน** ใช้งานได้จริง — เช็ค `classList.contains('active')` ตรงกับที่ `Layout.openModal` ใส่ (`:2569`) และไม่ยิงเมื่อโฟกัสอยู่ในช่องกรอก
2. **กฎเหล็ก #3 ข้อ 4** — `createDocFromReview` ไม่มี client-side required validation ขวางการอนุมัติ
3. **`_persistReviewEdits` ถูกเรียกก่อนสร้างจริง** (`:3205`) — การแก้ในโมดัลไม่หายอีกแล้ว
4. **ป้าย % ไม่ fallback ไป confidence ทั้งใบ** (`:1990–1998`) — บทเรียน "00000 · 95%" ยังถูกบังคับอยู่
5. **คำเตือน RD compliance คำนวณที่เซิร์ฟเวอร์** (`OcrScanComplianceEvaluator` → `complianceIssues`) หน้าเว็บแสดงอย่างเดียว · `undefined` ≠ `[]` ถูกเคารพทั้งที่วาดคำเตือนและที่ `_isBatchApprovable`
6. **LINE postback "อนุมัติเลย" มีด่านสิทธิ์ครบ** — `UserLoginPolicy` → tenant membership → `DocumentPermissionHelper.CanApproveAsync` (`LineBotService.cs:600–620`)
7. **`retryScan` ส่ง scanId ถูกชนิด** — บั๊ก `Page.rescan(fileAttachmentId)` เดิมถูกลบแล้วจริง และมีคอมเมนต์กันสร้างซ้ำ (`:1610`)
8. **`_rawTextPanel` ไม่เดาสาเหตุแทนผู้ใช้** — แยก `Cached`/`EtaxXml`/engine จริง (`:1675`)
9. **`_resolveDocSide` ใช้ `documentSideMap` จากเซิร์ฟเวอร์** (`:3094`) — legacy list เป็น fallback ของเซิร์ฟเวอร์รุ่นเก่าเท่านั้น ไม่ใช่ drift
10. **checker 4 ตัวที่โจทย์สั่งรัน ผ่านหมด 0 จุด** · ไม่มี `typeof this.x==='function'` · ไม่มี method ซ้ำ · ไม่มี DOM id ที่อ่านแล้วไม่มี
11. **E-OCR-05 ปิดจริง** — `vendorBranchCode`/`buyerTaxId`/`buyerBranchCode`/`vendorAddress`/`buyerName`/`buyerAddress`/`paymentTermsDays` มีครบทั้งฟอร์ม → payload builder → DTO → persist

## §"ยังไม่ได้ตรวจ" (เสนอให้รอบถัดไป)
- `documents.html` หลัง handoff: ฟอร์มไฮไลต์ confidence จริงไหม · `_applyOcrConfidenceHints` ครบกี่ช่อง · ปุ่ม "ดึงรายการจาก OCR" (`repopulate-lines`)
- `review-queue.html` เนื้อในหน้า (อ่านแค่ว่าไม่มีทางเข้า)
- `openStockImport` / `confirmStockImport` (`:3367–3790`, ~420 บรรทัด) — เส้นนำเข้าสต็อกจาก OCR
- `openPoPicker`/`link-po` mapping UI (`:1714–1856`)
- `aiReviewScan`/`_applyAiCorrections` (`:2912–3014`) — ทีม T3 §T3-07 ครอบแล้ว ไม่ตรวจซ้ำ
- `ProhibitedInputVatScreener` เทียบด้วย category หรือ description (ค้างจาก T4-16)
- LIFF / vendor-portal ที่อาจมีเส้น OCR อีกทาง
