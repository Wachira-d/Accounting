# ทีม C — OCR + รับเอกสารเข้า (ตรวจอ่านอย่างเดียว) · HEAD 81521510 · 2026-11-11

> วิธีตรวจ: เปิดโค้ดตรงจุด (ไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้ — ไม่มี .NET SDK) · path ทั้งหมดอยู่ใต้ `Accounting/` ·
> ข้อที่ติด "(อนุมานจากโค้ด)" = ยังไม่มีเทสต์/การรันยืนยัน ต้อง reproduce ก่อนแก้ (กฎ #4 G)

## 0. คำตัดสินรวม

- **BLOCKER: ไม่มี** · ทุกทางเข้าเดิน `OcrService.ScanAsync` ตัวเดียว (ไม่มีไปป์ไลน์สำเนา) · ทุกจุดเรียก AI ผ่าน orchestrator + มีนักเรียน
- **คำถามเจ้าของ "ทำไมจำนวนผิด · ต้องมีฟังก์ชันเทียบยอดรวมแล้วเลือกตัวที่ลงยอด"**: ตัว**ตรวจ+ธง**ระดับใบมีครบและถูกต่อสาย
  (`OcrTotalAnchor` → `OcrLineReconciler` เคส A–G → `[Σ-GAP]` ห้ามอนุมัติเอง) แต่ตัว**เลือกผู้สมัคร**ด้วยยอดรวมมีแค่ 4 เรื่อง
  (สำเนาทั้งหน้า · ตาราง ภ.พ. ชนะรายการ · ส่วนลดท้ายบิล · สัญลักษณ์ VAT) — **ระดับบรรทัด (จำนวน/ราคา/ยอด) ไม่มี**:
  `OcrLineQuantityGuard` ตัดสินทีละบรรทัดโดย**ยึดยอดบรรทัดเป็นจริงเสมอและไม่เห็นยอดหัวใบ** ⇒ เมื่อ engine อ่าน "ยอดบรรทัด" เพี้ยน
  ระบบ**แก้จำนวนให้ผิดตาม** (จำนวน = ยอด ÷ ราคา) แล้วค่อยฟ้อง [Σ-GAP] — นี่คือช่องที่เจ้าของเจอ (H1)

## 1. ทางเข้า (ข้อ 1)

| ทางเข้า | จุดเรียก | โควตา | ด่านอนุมัติเอง | หมายเหตุ |
| --- | --- | --- | --- | --- |
| หน้าสแกน `pages/document-scan.html` → `POST ocr/upload` | `OcrController.cs:249` | TryConsume + `OcrQuotaRefundRule` (:195,:262) | — (ผู้ใช้กด) | ✅ |
| ปุ่มลัดหน้าเอกสาร `documents.html:9134` | upload เดียวกัน แล้วกระโดดไปหน้ารีวิว | เดียวกัน | — | ✅ |
| `pages/ai-tools.html:413` (แท็บ OCR) | upload เดียวกัน | เดียวกัน | **ไม่มีหน้ารีวิว** กด "สร้างเอกสาร" ตรง | ⚠️ M3 |
| มือถือ `mobile-expense.html:129` | upload → `/correct` → `create-document?targetType=Expense` | เดียวกัน | Draft | ⚠️ M2 |
| Connect `connect/workbench.html:178` | upload เดียวกัน | เดียวกัน | — | ✅ (ไฮไลต์ <0.85 มี) |
| LINE `LineBotService.cs:603` | `ScanAsync(autoCreate:true, actingUserId)` | TryConsume + refund rule (:553,:613) | `OcrPostingReadiness.Evaluate` 4 อาร์กิวเมนต์ (:697) + ตรวจสด (:863–874) | ✅ เข้มสุด |
| 2 ขั้น `POST ocr/scan/{fileAttachmentId}` | `OcrController.cs:287` | **ไม่หักโควตา** | — | ⚠️ M4 |
| สแกนซ้ำ `POST ocr/{id}/retry` | `:353` forceRescan | ไม่หัก (เพดาน `OcrMaxRetriesPerScan`) | — | ✅ ตั้งใจ |
| API v1 `POST v1/ocr/scan` | `OcrV1Controller.cs:60` | meter หลังสแกน | — | ⚠️ H3 |
| กล่องรับ 50 ทวิ / ลิงก์อัปโหลดลูกค้า | `WhtCertIntakeController.cs:87` · `WhtCertUploadLinkService.cs:216` | ไม่หัก (มีเพดานต่อ IP) | ไม่สร้างเอกสารขาย | ✅ (จด M4) |
| อีเมลเข้า / intake box | **ไม่มีในโค้ด** (grep imap/inbound = 0) | — | — | ไม่มีฟีเจอร์ — ไม่ใช่บั๊ก |

## 2. การกระทบยอด จำนวน/ราคา/ยอด (ข้อ 2) — สิ่งที่มีจริง

ลำดับจริงใน `ScanAsync`: `ApplyTotalFirst` (`OcrService.cs:665` → `:8716` `OcrTotalAnchor.Find/Plan` ยึดยอดรวมก่อน) →
`TrySplitLineItemsAsync` (:866) → `SanitizeVatSplitArtifacts` (:911 → :4638) → … → ด่านความมั่นใจ `OcrConfidenceGateway.cs:250` ·
ตอนพรีวิว/สร้างเอกสาร `BuildScanLinesAsync` (:7652) ทำซ้ำด้วยตัวตัดสินเดียวกัน

| ชั้น | ตัวตัดสิน | ทำอะไร | ใครชนะเมื่อขัดกัน |
| --- | --- | --- | --- |
| ยอดรวม | `Helpers/OcrTotalAnchor.cs:209 Find` (Proven ≥2 ชั้นหลักฐาน) · `:449 Plan` · `:504 DocumentOverstatement` | เขียนยอดรวม/ฐาน/VAT หัวสแกน · `[TOTAL-CONFLICT]/[TOTAL-UNSURE]` | กระดาษที่พิสูจน์แล้ว > engine |
| หัวใบสลับป้าย | `OcrHeaderAmounts.Normalize/IsSwapped/ReconSubTotal` | สลับ Sub↔Total กลับ | — |
| บรรทัดเดี่ยว | `Helpers/OcrLineQuantityGuard.cs:713 Decide` (10 action) เรียกที่ `OcrService.cs:4717` | เติม/แก้ราคา · แก้จำนวน · ส่วนลดบรรทัดที่พิมพ์ · บรรทัดมิเตอร์ 1×ยอด · เศษปัด | **ยอดบรรทัดชนะเสมอ** (`if (amt <= 0) Keep` · ทุกสาขาคง `amt`) |
| แถวผี/สำเนา | `:4750–4790` (phantom ≤1 บาท) · `OcrDuplicateLineGuard.Decide` (:4815) + `PageCopyEvidence` (:4814 รอบ 257) | ตัดแถวที่ถูกอ่านซ้ำเมื่อ Σ หลังตัดลงหัวใบ · `[DUP-ROWS]` | **ยอดหัวใบชี้ขาด** (ตัวเดียวที่ทำแบบที่เจ้าของขอ) |
| ยุบแถว | `:4795–4830` (ชื่อ+ราคาเท่ากัน ⇒ บวกจำนวน+ยอด) | ไม่ดูหัวใบ | — |
| Σ ↔ หัวใบ | `Helpers/OcrLineReconciler.cs:132 Classify` (B/C/A/E/F/G · LinesShort · Ambiguous) ผ่าน `OcrVatCodeLines.Decide` (`OcrService.cs:7761`) | ตั้ง PricesIncludeVat · กระจาย/ลงส่วนลดท้ายบิล · `[Σ-GAP]` เมื่อ gap ≠ 0 (:7886) | หัวใบ; **ไม่แต่งบรรทัด** |
| ตาราง ภ.พ. | `OcrVatCodeLines` (ตารางชนะรายการ · `SubsetsSumming` :776) | แทนรายการด้วยบรรทัดสรุปกลุ่ม `[NO-ITEMS]` | ตารางที่ลงยอด |
| VAT/ยอดรวมหลังวางแผน | `OcrAmountIntegrity.Check` (`OcrService.cs:8423`) · `OcrApprovalGapWarning.Build` (`DocumentService.cs:23200`) | `[Σ-GAP]` TotalMismatch/VatMismatch · เตือนตอนอนุมัติ | — |
| ห้ามอนุมัติเอง | `OcrPostingReadiness.BlockingTags` | `[Σ-GAP] [LINE-QTY] [DUP-ROWS] [TOTAL-CONFLICT] …` | — |

**สรุป: ไม่มีทางที่ Σ ไม่ลงแล้ว "รับเงียบ"** เกิน 1 บาท — ทุกทางได้ `[Σ-GAP]` + หยุดอนุมัติเอง (ตรวจแล้วทำงาน) ·
ที่ขาดคือ "เลือกค่าที่ลงยอด" ระดับบรรทัด ⇒ ผู้ใช้เห็น**จำนวนผิดบนจอ**แล้วต้องแก้เอง (ผิดกฎ #3 "ยืนยันอย่างเดียว")

## 3. ข้อค้นพบ (เรียงความรุนแรง)

### H1 — [สูง] บรรทัดที่ "ยอดบรรทัด" อ่านเพี้ยน: ระบบแก้จำนวนให้ผิดตาม เพราะตัวกันบรรทัดไม่เห็นยอดหัวใบ
- **หลักฐาน**: `Helpers/OcrLineQuantityGuard.cs:713–892` — ลายเซ็น `Decide(quantity, unitPrice, amount, lineDiscount, paper, …)` ไม่มีพารามิเตอร์ยอดหัวใบ
  (grep `headerSubTotal` ในไฟล์ = เฉพาะ `PaperDiscountPricesIncludeVat` :1188) · สาขาท้าย `FixQuantity` ตั้ง `newQty = amt ÷ up` · สาขา `KeepPrintedGap`
  เก็บส่วนต่างเป็น "ส่วนลดบรรทัด" ให้ยอดเท่ายอดที่อ่านได้ · ผู้เรียก `OcrService.cs:4717` ส่งแค่ค่าบรรทัด ทั้งที่ `data.SubTotal/TotalAmount` ถูกยึดแล้วตั้งแต่ :665
- **เส้นผู้ใช้**: ใบ 3 บรรทัด ฐาน 10,000 · บรรทัด 2 กระดาษ `12 × 250.00 = 3,000.00` engine อ่านยอดเป็น `2,500.00` ⇒ จอแสดงจำนวน **10** (ไม่พบ 10 บนแถว ⇒ `[LINE-QTY]`)
  หรือ ถ้า 12 พิมพ์คู่หน่วย ⇒ จำนวน 12 + **ส่วนลดบรรทัด 500 ที่ไม่มีบนกระดาษ** · Σ 9,500 ⇒ `[Σ-GAP] LinesShort 500` · ทั้งที่ `12×250` ลงฐานพอดี
- **โซนเงียบเพิ่ม**: ช่องว่าง 0.01 < |จำนวน×ราคา − ยอด| ≤ max(1, 2%·ยอด) ⇒ ตัวกันตอบ `Keep` ไม่มีโน้ต (:771 `Math.Abs(computed - amt) <= tol`) แต่ตอนสร้างบรรทัด
  `OcrTotalDecomposer.LineGross` (`OcrTotalDecomposer.cs:181`) ให้ **จำนวน×ราคาชนะ** เมื่อห่างเกิน 0.01 ⇒ สองตัวตัดสินใช้เกณฑ์/ผู้ชนะคนละตัว ·
  ผู้ใช้ได้แค่ "พบรายการที่ Qty × UnitPrice ≠ Amount จำนวน n บรรทัด" (`OcrConfidenceGateway.cs:290`) ไม่ชี้บรรทัด
- **แก้ขั้นต่ำ (helper ใหม่ตัวเดียว — ยังไม่มีในเรพ)**: `Helpers/OcrLineCandidateChooser` (pure) —
  `Choose(lines[{qty, up, amt, disc}], anchoredBase, anchoredTotal, vat, billDiscount, anchorVerdict)`:
  (1) ทำงานเฉพาะเมื่อยอดหัวใบ **Proven/Confirmed** (`OcrTotalAnchor`) และ Σ ปัจจุบันไม่ลง (LinesShort/Ambiguous) ·
  (2) ผู้สมัครต่อบรรทัด = {ยอดที่อ่าน, จำนวน×ราคา} — เฉพาะบรรทัดที่สองค่าต่างกัน > 0.01 (ปกติ 1–3 บรรทัด ⇒ ≤ 2^k ชุด จำกัด k ≤ 8) ·
  (3) รับเฉพาะเมื่อมี**ชุดเดียว**ที่ `OcrLineReconciler.Classify(Σ, …)` คืนเคสลงตัว (B/A/C/E/F/G · gap 0) — 0 หรือ ≥2 ชุด ⇒ ไม่แตะ (ไม่รู้ = ไม่เดา · F2 ข้อ 3) ·
  (4) บรรทัดที่ถูกเลือกค่า จำนวน×ราคา ⇒ ตั้ง `Amount` ใหม่ + โน้ต `[LINE-QTY-NOTE]` (ไม่บล็อก — ลงยอดกระดาษที่พิสูจน์แล้ว) ·
  (5) เรียก**ก่อน** `OcrLineQuantityGuard.Decide` ใน `SanitizeVatSplitArtifacts` (บรรทัดที่ผู้เลือกตัดสินแล้ว ข้ามตัวกัน) + ใน `OcrReplayHarness.BuildLines` + ล็อก `tools/required_call_site_check.py`
- **เทสต์จากกระดาษจริง (สองทิศ)**: (ก) ใบที่เจ้าของรายงานจำนวนผิด (ขอไฟล์ — ยังไม่มีใน corpus) ⇒ จำนวน/ยอดกลับตรงกระดาษ ไม่มี `[Σ-GAP]` ·
  (ข) ทิศตรงข้าม: HardwareBillDiscount (ส่วนลดบรรทัดจริง) · Lazada (เศษ 0.01) · บิลมิเตอร์ (MeteredWater) · golden ทุกใบใน `OcrReplayLineGoldenTests` **0 ใบเปลี่ยน** ·
  (ค) ใบที่สองชุดลงยอดทั้งคู่ ⇒ ไม่แตะ + `[Σ-GAP]` เดิม

### H2 — [สูง] `create-document?approve=true` (อนุมัติรวดหน้าเว็บ/คีย์พาร์ทเนอร์) ใช้ด่านอ่อนกว่า LINE — กติกาอยู่ใน JS
- **หลักฐาน**: `Controllers/OcrController.cs:442` เรียก `OcrPostingReadiness.Evaluate(notes, hasDate)` (2 อาร์กิวเมนต์) — ไม่ส่ง `ComplianceIssues`/เกรดคุณภาพ และไม่ตรวจ
  `OverstatedNow`/`TableMismatch` สด · LINE ส่งครบ (`LineBotService.cs:697` 4 อาร์กิวเมนต์ · `:863–874`) — คอมเมนต์ที่ LINE เองบันทึกว่าช่องนี้คือบั๊ก E-03 ·
  ด่านเทียบเท่าของเว็บอยู่ใน `wwwroot/pages/document-scan.html:1402 _isBatchApprovable` (compliance error · เกรด D · ซ้ำ · ลายมือ · สินทรัพย์ · ไม่มีผู้ขาย) = สำเนากติกาใน JS (F2 ข้อ 5)
- **เส้นผู้ใช้**: ผู้เรียก API/คีย์ `acc_`/`int_` ยิง `?approve=true` กับสแกนที่ "ผู้ซื้อไม่ใช่บริษัทนี้" หรือเกรด D ⇒ เซิร์ฟเวอร์ไม่กัน (เหลือแค่ด่านใน `ApproveDocumentAsync(Unattended)`)
- **แก้**: ย้ายเงื่อนไข `_isBatchApprovable` เข้า `OcrPostingReadiness` ตัวเดียว (overload ที่รับ DTO) แล้วให้ `OcrController` + LINE + JS (อ่านธง `canAutoApprove`/`reason` จาก DTO) ใช้ร่วม
- **เทสต์**: สแกน compliance error + `approve=true` ⇒ Draft + `[APPROVE-SKIP]` · ทิศตรงข้าม: ใบสะอาด ⇒ อนุมัติได้เหมือนเดิม

### H3 — [สูง · เงิน] API v1 คิดค่าบริการสแกนที่ล้ม/คัดลอกผลเดิม
- **หลักฐาน**: `Controllers/V1/OcrV1Controller.cs:60–64` — `ScanAsync` **ไม่ throw** เมื่อ engine ล้ม (ตั้ง `ScanStatus="Failed"` ที่ `OcrService.cs:613,3581` แล้วคืน 200) ·
  ตัวควบคุมเรียก `MeterAsync` ทันทีโดยไม่ดู `result.ScanStatus`/`OcrEngine` ทั้งที่คอมเมนต์เขียน "คิดเงินหลังงานสำเร็จเท่านั้น" · เส้นเว็บ/LINE ใช้ `OcrQuotaRefundRule.ShouldRefund`
- **แก้**: `if (!OcrQuotaRefundRule.ShouldRefund(result.ScanStatus, result.OcrEngine)) Meter…` + ตอบ `scanStatus` ให้ลูกค้าเห็น · เทสต์: Failed/Cached/e-Tax XML = ไม่คิด · Completed = คิด 1

### M1 — [กลาง] สำเนาบางส่วน/ซ้ำไม่เท่ากัน: ขั้นยุบยังบวกจำนวน ×2 บางบรรทัด แม้ Σ หลังตัดลงหัวใบที่พิสูจน์แล้ว
- **หลักฐาน**: `Helpers/OcrDuplicateLineGuard.cs:97–104` `!uniformRepeat` คืนก่อนเทียบหัวใบ · `PageCopyEvidence` ถูกคิดเฉพาะเมื่อทุกแถวชื่อเดียว (`OcrService.cs:4813`) ·
  ขั้นยุบ `:4795` บวกจำนวนไม่มีเงื่อนไข · คลาสนี้จดไว้ว่า "ยอมรับ" (docs/lessons/ocr-pipeline.md §ตารางคลาส #9)
- **เส้นผู้ใช้**: PDF ต้นฉบับ+สำเนา ที่ engine อ่านแถวหนึ่งของหน้าสำเนาเพี้ยน 1 ช่อง ⇒ 4 จาก 5 บรรทัดจำนวน ×2 + `[Σ-GAP]`
- **แก้**: รวมเข้า `OcrLineCandidateChooser` — ผู้สมัครเพิ่ม "นับลายเซ็นซ้ำครั้งเดียว" และ "แถวใกล้เคียง (ต่าง 1 ช่อง) ถือเป็นสำเนา" **เฉพาะเมื่อหัวใบ Proven และ Σ ลงพอดี** · ยังติด `[DUP-ROWS]`
- **เทสต์**: BoonsapOriginalCopyPages ดัดแถวเดียว ⇒ ลงยอด · ทิศตรงข้าม water-genuine-repeat-rows + "2 แถวซ้ำ + 1 เดี่ยว หัวใบไม่ Proven" ⇒ ไม่แตะ

### M2 — [กลาง] มือถือเบิกค่าใช้จ่าย: 3 ทางตัน/เงียบ
- `wwwroot/mobile-expense.html:190–199` — `/correct` เป็น best-effort (`.catch(() => {})` + ไม่ดู `r.ok`) ⇒ คำแก้ยอด/ผู้ขายล้ม (403/409) แล้วยังสร้างเอกสารด้วยค่าที่ OCR อ่าน = ยอดที่ผู้เบิกพิมพ์หายเงียบ (กฎ #4 E "ห้ามกลืน error เส้นเงิน")
- `:139–142,:182` — อัปโหลดล้ม (โควตาหมด 429 / engine ล้ม) บอก "กรอกเองได้เลย" แต่ตอนส่งบังคับ `scanId` ⇒ "ต้องถ่ายรูปใบเสร็จก่อน" ทั้งที่ถ่ายแล้ว = ทางตัน · สแกน `Failed` (200 + id) ⇒ `create-document` ตอบ `OCR-SCAN-NOT-COMPLETED`
- ใบซ้ำ: เซิร์ฟเวอร์ตอบ "กดยืนยันสร้างซ้ำในกล่องเตือน" (`OcrService.cs:6630`) แต่มือถือไม่มีกล่อง/ไม่ส่ง `allowDuplicate`
- `/correct` ส่งแค่ `totalAmount` ⇒ `SubmitCorrectionAsync` (`OcrService.cs:5197–5199`) เขียนยอดรวมอย่างเดียว ฐาน/VAT เดิมค้าง ⇒ ใบไม่มีรายการ: ฐาน = ยอดใหม่ − VAT เก่า (`OcrHeaderAmounts.NetSubTotal`) VAT ไม่ใช่ 7% ของฐาน ·
  ใบมีรายการ: Σ บรรทัดชนะ ยอดที่พิมพ์ไม่ถูกใช้ (อนุมานจากโค้ด — ต้อง reproduce)
- **แก้**: ตรวจ `r.ok` ของ `/correct` แล้วหยุด+แสดงข้อความ · อัปโหลดล้ม ⇒ แสดงเหตุจริง (โควตา/ภาพ) + ปุ่มถ่ายใหม่ · ส่ง `allowDuplicate` หลังผู้ใช้ยืนยัน · ฝั่งเซิร์ฟเวอร์: แก้ยอดรวมอย่างเดียว ⇒ ถอดฐาน/VAT ใหม่ผ่าน `OcrVatBackCalc` หรือโน้ตบล็อก
- **เทสต์**: OCR อ่าน 500 ผู้ใช้พิมพ์ 535 ⇒ เอกสาร 535 (ฐาน 500 · VAT 35) · ทิศตรงข้าม: ไม่แก้ยอด ⇒ ฐาน/VAT เดิมทุกสตางค์

### M3 — [กลาง · ความปลอดภัย+จอซ้ำ] แท็บ OCR ใน `pages/ai-tools.html` เป็นหน้าสแกนสำเนาที่ล้าหลัง
- `wwwroot/pages/ai-tools.html:425–433` ต่อ `d.extractedVendorName / extractedVendorTaxId / extractedDocumentNumber / documentType` เข้า `innerHTML` **ไม่ผ่าน `Layout.esc`** ·
  `:403–406` เช่นกัน (`r.fileName`, `vendorName`) — ข้อความจากกระดาษ/ชั้นข้อความ PDF/e-Tax XML ที่ผู้ส่งเอกสารคุมได้ ⇒ XSS → ขโมย JWT (กฎ #4 C)
- `:400–407` อ่าน `r.status / r.fileName / r.extractedData` ที่ DTO ไม่มี (ของจริง `scanStatus / originalFileName / extracted*`) ⇒ ทุกแถวขึ้น "กำลังประมวลผล" ไม่มีปุ่ม ·
  `:424` ขึ้น "สแกนสำเร็จ!" แม้ `scanStatus = Failed` · ปุ่ม "สร้างเอกสาร" ข้ามหน้ารีวิว/คำเตือนซ้ำ/ช่องว่างยอด
- **แก้**: ถอดแท็บนี้ เหลือลิงก์ไป `document-scan.html` (ตัวเลือกที่ถูกตามกฎ "ของซ้ำต้องเลือก: ต่อสายหรือลบ") — หรืออย่างน้อย `Layout.esc` ทุกช่อง

### M4 — [กลาง] `POST ocr/scan/{fileAttachmentId}` ไม่หักโควตา OCR
- `Controllers/OcrController.cs:266–290` ไม่มี `_quota.TryConsumeAsync` (ต่างจาก `/upload` :195) ⇒ ผู้ใช้/พาร์ทเนอร์ที่มีไฟล์แนบชนิด OcrScan สแกนใหม่ได้ไม่จำกัด (ไม่มีเพดานแบบ `/retry`) ·
  หน้าเว็บไม่ใช้เส้นนี้แล้ว (`api.ocrScan` ไม่มีผู้เรียก) เหลือเฉพาะ flow 2 ขั้นของพาร์ทเนอร์ · เส้น 50 ทวิ ก็ไม่หัก (ตั้งใจ? — ต้องให้เจ้าของตัดสิน)
- **แก้**: ใช้ด่านโควตาเดียวกับ `/upload` (ดึงเป็น helper ส่วนตัวของ controller) · เทสต์: โควตา 0 ⇒ 429 ทั้งสองเส้น · สแกนล้ม ⇒ คืน

### M5 — [กลาง] engine คืนตารางไม่ครบ (LinesShort) ไม่ลอง "ตารางจากข้อความ" เป็นผู้สมัครที่สอง
- `OcrService.cs:11062` `if (data.Items.Count > 0) return;` — `RawTextLineSplitter`/นักเรียน `OcrLineItemSplit` ทำงานเฉพาะเมื่อ engine คืน 0 บรรทัด ·
  ใบที่ Azure คืน 5 จาก 8 แถว ⇒ `[Σ-GAP] LinesShort` ผู้ใช้กด "➕ เพิ่มบรรทัด" (ได้แถวว่าง 1 × 0 — `OcrService.cs:9627`) แล้วพิมพ์เอง
- **แก้**: ใน `OcrLineCandidateChooser` เพิ่มผู้สมัครระดับตาราง: ถ้า Σ engine ไม่ลง แต่ `RawTextLineSplitter` + `OcrLineSplitGuard.Evaluate` ลงหัวใบที่ Proven ⇒ ใช้ตารางข้อความ (โน้ต `[LineSplit]` + ติดธงตรวจ) ·
  UX ขั้นต่ำก่อนถึงตรงนั้น: ปุ่ม "เพิ่มบรรทัดเท่าช่องว่าง {gap}" เติมยอดให้ (ผู้ใช้พิมพ์แค่ชื่อ)
- รวมคลาส #15 เดิม (ค่าขนส่ง/service charge ใต้ตาราง — ยังเปิดอยู่ตามตารางใน lessons)

### L — ต่ำ
- **L1** ผู้ขายจาก AI ไม่มีธง `VendorMatchUsedAi` ใน DTO (`Models/DTOs/Ocr/OcrDtos.cs` มี 5 ธง: GlAccount/LineSplit/TargetDocType/OurRole/VatCode) และ `OcrService.cs:2940`
  ตั้ง `AiSuggestedContactId` แม้คำตอบมาจากนักเรียน ⇒ `acceptedAi` ที่ `:7530,:9715` นับคำตอบนักเรียนเป็น "รับ AI" (ตัวชี้วัดกฎ #1 ข้อ 6 เพี้ยน)
- **L2** `Retry` ตอบผลไปที่ "แถวสแกนใหม่" (`OcrController.cs:353–362`) — หน้าเว็บจัดการแล้ว แต่รายการสแกนจะมีแถวเก่าค้าง (ไม่ใช่บั๊กเงิน)
- **L3** ไม่พบคู่ค้า ⇒ `OCR-NO-COUNTERPARTY` ตอนสร้าง (`OcrService.cs:6946`) — ขัดตัวอักษรกฎ #3 ข้อ 4 แต่ถูกตาม F2 ข้อ 3 (ไม่แต่งชื่อ) · คงไว้ ·
  ควรย้ายไปเป็นป้าย "ต้องเลือกผู้ขาย" บนการ์ด**ก่อน**กดสร้าง (ธงใน DTO)
- **L4** ซ้ำด้วยเนื้อหา: `FindDuplicateDocumentWarningAsync` (`OcrService.cs:10545`) ต้องมีเลขเอกสาร ≥ 3 ตัว ⇒ ใบที่อ่านเลขไม่ได้ + ไฟล์ต่าง (ถ่ายใหม่) ไม่ถูกจับซ้ำ (ผู้ขาย+วันที่+ยอด) — ข้อเสนอ: เตือน (ไม่บล็อก)

## 4. ตรวจแล้วทำงาน (ห้ามรายงานซ้ำ)

- **กฎ #1**: จุดเรียก AI ของ OCR ทั้ง 7 (`Services/Ai/OcrAiAugmenter.cs` — Vendor canon · GL · บทบาท · ชนิดเอกสาร · รหัส ภ.พ. · โครงการ · แตกบรรทัด) ผ่าน `AskAsync` ทั้งหมด (7 จุด) ·
  ไม่มีการเรียก provider/HTTP ตรงนอก `Services/Ai/` · นักเรียนครบผ่าน `DistillationModelRegistry` (bespoke + generic) · FeedbackId เก็บ (`GlAccountAiFeedbackId` · `TargetDocTypeAiFeedbackId` · `AiSuggestionFeedbackId`) ·
  ด่านกันแต่ง: ชนิดเอกสาร `Enum.TryParse + DocumentSide.MatchesRole` (:1315) · ผังบัญชีต้องอยู่ในผังบริษัทและ active (:2075) · ผู้ติดต่อต้องเป็นของบริษัท (:2929) · แตกบรรทัด `OcrLineSplitGuard` (:11107) ·
  ปิด provider: แตกบรรทัดตกไป `RawTextLineSplitter` (:11093) · ป้าย `UsedAi` ตั้งจาก `cls.UsedAi`/`glResult.UsedAi` จริง
- **ปิดลูปเรียนรู้**: `SubmitCorrectionAsync` → `RecordUserChoiceAsync` (:5630 GL · :5665 ตัวปิดรวม) · ตอนสร้างเอกสาร (:7516–7529 ชนิด/บทบาท/ผู้ขาย) · จับคู่ผู้ติดต่อ (:9716) · แตกบรรทัด (:9673)
- **ยอดรวม-ก่อน**: `ApplyTotalFirst` ก่อนตัวกันบรรทัด · `[Σ-GAP]` ล้างต้น `BuildScanLinesAsync` แล้วเขียนใหม่ทุกรอบ (:7660) · ตรวจสดตอนอนุมัติ (`DocumentService.cs:23193–23207`)
- **§82/5**: เตือน/ปิดเคลม (:1365–1420 · :7117–7135 `[TAX-INV-PENDING]` พัก 11640 · :7349–7401 `[VAT-CLAIM]`) · เส้น JE ตรงมีด่านเดียวกัน (:9158–9166 `RD-82/5-JE`)
- **ซ้ำ**: hash ไฟล์ (`OcrDuplicateReusePolicy`) · เลขใบ+ผู้ขาย+ยอด (`OcrDuplicateScanRule`) บล็อกจนผู้ใช้ยืนยัน `allowDuplicate` · upload ซ้ำใน 1 นาทีคืนผลเดิม (`OcrController.cs:185`)
- **สถานะ**: สแกนล้ม = `Failed` + `[Error]` ใน notes + ตัวกรอง "ล้มเหลว" + ปุ่มสแกนซ้ำ (document-scan.html:1069,1304,1733) · สแกนเป็น synchronous ไม่มีคิวค้าง
- **หน้ารีวิว**: แก้จำนวน/ราคา/ยอดต่อบรรทัด (`line-fields`) · เพิ่ม/ลบบรรทัด (`modify-line`) · พรีวิวบรรทัดก่อนสร้าง (`line-preview`) · ตัวอ่านแท็กตัวเดียว (:1471) · ความมั่นใจรายช่อง (:2350)
- **LINE**: หักโควตา+คืนตามกติกาเดียวกับเว็บ · ด่านอนุมัติจากแชทตรวจสด 3 ชั้น (ยอดเกิน · ตาราง ภ.พ. · `[LINE-QTY]`)

## 5. ชุดแก้ที่เสนอ (เรียงลำดับทำ)

| แพ็ก | เนื้อหา | ไฟล์ |
| --- | --- | --- |
| P1 (H1+M1+M5) | `OcrLineCandidateChooser` ตัวเลือกผู้สมัครด้วยยอดที่พิสูจน์แล้ว (ยอดบรรทัด ↔ จำนวน×ราคา · นับแถวซ้ำครั้งเดียว · ตารางข้อความ) | ใหม่ `Helpers/OcrLineCandidateChooser.cs` · `Services/Implementations/OcrService.cs` (:4695–4830 · :11062) · `Helpers/OcrDuplicateLineGuard.cs` · `Accounting.Tests` (เทสต์ pure + `OcrReplayHarness.BuildLines` + golden) · `tools/required_call_site_check.py` · `docs/lessons/ocr-pipeline.md` (ตารางคลาส #9/#15) · `DOCUMENT_FLOW.md` · `TEST_PLAN.md` |
| P2 (H2) | ด่านอนุมัติเองตัวเดียวฝั่งเซิร์ฟเวอร์ | `Helpers/OcrPostingReadiness.cs` · `Controllers/OcrController.cs:420–450` · `Services/Implementations/LineBotService.cs` · `wwwroot/pages/document-scan.html:1402` · DTO ธง `canAutoApprove` |
| P3 (H3+M4) | คิดเงิน/โควตาตามกติกาเดียว | `Controllers/V1/OcrV1Controller.cs` · `Controllers/OcrController.cs:266–290` · เทสต์ `OcrQuotaRefundRule` |
| P4 (M2) | มือถือ: ไม่กลืน error · ทางไปต่อ · แก้ยอดรวมแล้วถอดฐาน/VAT | `wwwroot/mobile-expense.html` · `OcrService.SubmitCorrectionAsync` (:5197) |
| P5 (M3) | ถอด/ซ่อมแท็บ OCR ใน ai-tools | `wwwroot/pages/ai-tools.html` |
| P6 (L1,L3,L4) | ธง `VendorMatchUsedAi` · ป้ายต้องเลือกผู้ขาย · เตือนซ้ำด้วยเนื้อหา | `Models/DTOs/Ocr/OcrDtos.cs` · `OcrService.cs` · `document-scan.html` |

**ก่อนลงมือ P1 (กฎ #4 H)**: ขอไฟล์ใบที่เจ้าของเห็นจำนวนผิดเข้า `OcrPaperSamples` ก่อน · รัน golden ก่อน-หลัง แล้วอธิบายทุกใบที่คำตอบเปลี่ยน (เป้า: เปลี่ยนเฉพาะใบใหม่) ·
ผู้เลือกต้องทำงานเฉพาะเมื่อหัวใบ Proven/Confirmed — หัวใบที่ยังไม่พิสูจน์ห้ามใช้ชี้ขาดบรรทัด (บทเรียน "น้ำดื่ม × 3 แถว หัวใบอ่านเพี้ยน")
