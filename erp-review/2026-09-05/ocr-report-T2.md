# ทีม T2 — วิศวกรรมการสกัดข้อมูล (paper → structured data)

ผู้ตรวจ: document-AI/OCR engineer + Thai NLP specialist + numerical-verification expert
วันที่: 2026-09-05 · เรพ: /home/user/Accounting · เขียนแบบ append ทีละส่วน (rate-limit safe)
กติกา: ทุกข้อเปิดไฟล์จริง (file:line) · ไม่แก้โค้ด · ไม่รายงานซ้ำสิ่งที่ปิดแล้ว (OCR-BRIEF ข้อ 6)

## สรุป 5 บรรทัด
1. 🔴 P0 `AmountTripleExtractor` **ทับค่าที่ engine อ่านถูก**ด้วยสามค่าที่แต่งขึ้น บนบิลผสม 7%/ยกเว้น (Makro → subtotal = เงินทอน 951), 7-Eleven (total = เงินสด 100) และใบส่งออก 0% (VAT 3,271 ที่ไม่มีจริง) — ทุก engine, confidence 0.95 ปิดปากด่านทุกตัว (T2-09)
2. 🔴 P1 ×3: สถานะ Failed ถูกทับเป็น Completed + โควตาไม่คืน (T2-01) · reconciliation เทียบ Σ บรรทัดก่อน VAT กับยอดรวมหลัง VAT ⇒ บิลส่วนลดได้ "ค่าขนส่งผี 16.50" หรือเอกสาร 912 ≠ กระดาษ 856 (T2-05) · "เลขที่ 99/1" ของที่อยู่ชนะเลขที่ใบกำกับ (T2-10)
3. 🟠 ของที่จ่ายเงินซื้อมาแล้วถูกทิ้ง: Azure ProductCode/DueDate/PurchaseOrder/TaxDetails/Items.TaxRate (T2-04) · e-Tax Unit/VAT category/InvoiceReferencedDocument + SubTotal เลือกก่อนส่วนลด (T2-08) · ชื่อไฟล์เลือกโมเดลและล็อกชนิดเอกสาร (T2-14)
4. 🟠 ด่านที่ไม่ใช่ด่าน/ฟ้องผิด: WHT rule 8 เทียบสูตรกับตัวเอง (T2-02) · rule 3 และ rule 7 ฟ้องบิลผสม/ราคารวม VAT ทุกใบ (T2-03, T2-16) · ExemptKeywords "นม/ผัก/หนังสือ" substring (T2-07) · layout-table "จำนวน" กิน "จำนวนเงิน" (T2-11) · OcrLineItemSplit ไม่มี student (T2-12)
5. ช่องว่างที่นักบัญชีเก่งเห็นแต่ระบบไม่เห็น: เดือนเต็มภาษาไทย (T2-13) · Service Charge/ปัดเศษ (T2-18) · preprocessing ให้ engine ที่อ่อนที่สุด (T2-17) · confidence รายบรรทัด (T2-20) — 20 ข้อ ทุกข้อมี file:line + sim ใน `scratchpad/T2/*.py`

---
## §0 ตารางความสามารถราย tier (อ่านจาก ScanAsync :290–560 + ตัวสกัดของแต่ละ tier)

| Tier | ไฟล์:บรรทัด | ช่องที่คืน | Line items | Confidence รายช่อง | รหัสสาขา | บล็อกผู้ซื้อ | ตำแหน่งบนกระดาษ |
|---|---|---|---|---|---|---|---|
| 0 e-Tax XML | OcrService :290–323 → `EtaxPdfXmlExtractor` :334–440 | เลขที่/วันที่/ชนิด(388/T03/80/81)/ผู้ขาย+ผู้ซื้อ ชื่อ·เลขภาษี·สาขา·ที่อยู่/LineTotal/TaxBasis/VAT/GrandTotal/Currency | ✅ LineNo·Description·Qty·**Unit**·UnitPrice·Amount (ไม่มี VAT rate รายบรรทัด) | ❌ ไม่ตั้ง FieldConfidence เลย (ค่าจาก XML = authoritative แต่ UI ไม่รู้) | ✅ ทั้งสองฝั่ง | ✅ ชื่อ+เลข+สาขา+ที่อยู่ | ไม่เกี่ยว |
| 1 Azure DI | :324–400 → `ExtractWithAzureDiAsync` :2620 → `MapAzureDiToExtractedDataAsync` :2666 | Vendor/Merchant Name·TaxId·Address·Phone / Customer Name·TaxId·Address / InvoiceId·InvoiceDate / SubTotal·TotalTax·InvoiceTotal(??AmountDue) + K-V fallback + barcode | ✅ Description·Qty·UnitPrice·Amount (**ProductCode อ่านมาแล้วทิ้ง** — AzureDI svc :467 vs OcrService :2720–2728) ; ไม่อ่าน Items.Unit / Items.Tax / Items.TaxRate / Items.Date | ✅ 10 ช่อง (AzureDI svc :441–452 → `OcrFieldKeys.Canonical`) | ❌ ไม่มีจาก schema — พึ่ง `EnrichFromRawText` :6979 (regex บน raw text) | ✅ ชื่อ/เลขภาษี/ที่อยู่ (ไม่มีสาขา) | ❌ ไม่เก็บ bounding box (มีใน JSON ของ Azure แต่ `ParseResult` ไม่อ่าน) |
| 2 Python local | :404–440 → `ExtractWithLocalServiceAsync` :3352–3505 | vendor/buyer name+taxid, docnum, date, sub/vat/total, expense_category, has_wht, wht_rate, payment_terms_days, suggested_accounts | ✅ description/quantity/unit_price/amount/suggested_account_code (ไม่มี unit) | ✅ ถ้า service ส่ง `field_confidence` (main.py :80–100 ใช้ชื่อชุด Azure → canonical ที่ปลายทาง) ; **default 0.5 ถ้าไม่มีช่อง confidence** :3400 | ❌ (พึ่ง EnrichFromRawText) | ✅ ชื่อ+เลขภาษี เท่านั้น (ไม่มีที่อยู่ผู้ซื้อ) | ❌ (python มี box แต่ไม่ส่ง) |
| 3a PDF text-layer + Tesseract | :450–540 → `ParseThaiDocument` :4078 + `SmartFieldExtractor.Enrich` :504 | ชนิดเอกสารจาก keyword, ชื่อบริษัท regex, เลขภาษี regex, phone/email/address, docnum, date, ยอด | ❌ ข้อความล้วน → พึ่ง `TrySplitLineItemsWithAiAsync` :6830 (AI) | บางช่องผ่าน SmartFieldExtractor (TotalAmount/DocumentNumber ≈0.9) ; **Confidence เอกสาร floor 0.90** :502 | ✅ `BranchCodeExtractor` :4210 | ชื่อ (heuristics ใกล้คำว่า "ผู้ซื้อ") ไม่มีที่อยู่ | ❌ |
| 3b Tesseract only | `ExtractWithEmbeddedTesseractAsync` :3292 | เหมือน 3a | ❌ (AI split) | เหมือน 3a ; Confidence = **max(word-conf, keyword-conf 0.95)** :3342 | ✅ | เหมือน 3a | ❌ |

ข้อสังเกตข้ามตาราง: ทุก tier ผ่าน `EnrichFromRawText` (:635) → `ApplyLearnedPatternsAsync` (:644) → `ApplyZoneAnalysisFallbackAsync` (:651, เฉพาะไม่มีทั้ง VendorName และ Total) → `TrySplitLineItemsWithAiAsync` (:677, เฉพาะ Items ว่าง) → `SanitizeVatSplitArtifacts` (:712) — ยืนยันแล้วว่า **call site ทั้งหมดอยู่บนเส้นทางหลักที่ทุก engine เดินผ่าน** (grep :429/:525/:635/:644/:651/:2662/:3348/:3499/:4210/:6979) ⇒ บทเรียน BranchCodeExtractor-เฉพาะ-Tesseract ใน CLAUDE.md **ยังแก้อยู่จริง**

---
## §1 Findings

### T2-01 🔴 [P1][S] สถานะ `Failed` ถูกเขียนทับเป็น `Completed` เสมอ — ทุก tier ล้มแล้วผู้ใช้เห็น "สแกนเสร็จ" + โควตาไม่ถูกคืน
- ไฟล์: `Accounting/Services/Implementations/OcrService.cs:541–551` และ `:621`
- โค้ด: :549 `scanResult.ScanStatus = "Failed";` (บล็อก `if (extractedData == null)`) … 70 บรรทัดถัดมา :621 `scanResult.ScanStatus = "Completed";` **ไม่มีเงื่อนไข**
- ทำไมพัง: (1) ผู้ใช้เลือก engine `azure` แล้ว Azure ล้ม → ไม่มี tier ไหนทำงาน → :541 สร้าง `OcrExtractedData{Confidence=0}` + ตั้ง `Failed` (2) โค้ดเดินต่อลง :621 ตั้ง `Completed` ทับ (3) คอมเมนต์ :537–540 บอกว่า *"We refund quota in the controller via the OcrEngine == null / status != Completed gate"* — แต่ `ocrEngineUsed = ocrEngineUsed ?? "None"` (:543) ทำให้ OcrEngine ≠ null และ status = Completed ⇒ ด่านคืนโควตาไม่ทำงานทั้งสองเงื่อนไข
- ผลกระทบ: สแกนที่ล้มทั้งหมดถูกบันทึกเป็น "Completed" ด้วยช่องว่างทุกช่อง (ขัดกฎเหล็ก #3 ข้อ 1 — DTO ต้องไม่มี null ถึงมือผู้ใช้ และขัด "tier failure ต้องดัง") · โควตาถูกตัดโดยไม่ได้อะไร · ข้อความเหตุผลอยู่ใน `ReasoningTrace` ซึ่ง (ดู T2-06) ไม่ persist
- defect class: "ห้าม silent no-op" / "ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อ" (`?? "None"`)
- ทางแก้: ย้าย :621 เข้าเงื่อนไข `if (scanResult.ScanStatus != "Failed")` หรือย้ายบล็อก Failed ไปหลัง :621 ; เทสต์: กรณี enginePref="azure" + Azure ล้ม → status ต้องเป็น Failed และ controller คืนโควตา
- ความมั่นใจ: **สูง** (อ่านลำดับบรรทัดตรง ๆ) — ต้องเช็คต่อ: grep ว่ามีจุดตั้ง `ScanStatus` อีกหลัง :621 ไหม (ทำใน T2 batch 2)
- ฝ่ายค้าน: "controller เช็ค `result.Confidence == 0` แทน" → คำตอบ: คอมเมนต์ในโค้ดเองระบุว่าเช็ค status/engine; และ Confidence 0 ก็เกิดจาก gateway penalty ได้ (MaxPenalty 0.60 → ไม่ถึง 0 แต่ modelConfidence 0.5 − 0.6 → clamp 0) จึงแยกไม่ออกอยู่ดี

### T2-02 🟠 [P2][S] ด่าน WHT ของ Gateway (rule 8) เทียบ "สูตร" กับ "สูตรเดียวกัน" — ผ่านเสมอ = ไม่มีด่าน
- ไฟล์: `OcrService.cs:572–580` (ผู้เรียก) · `Ocr/OcrConfidenceGateway.cs:204–214` (rule 8)
- โค้ด: :572 `decimal? whtAmt = null; if (whtRatePct.HasValue && extractedData.SubTotal.HasValue) whtAmt = Math.Round(SubTotal × rate/100, 2, AwayFromZero);` → ส่งเข้า Gateway ซึ่ง :208 `expectedWht = Math.Round(subTotal × rate / 100m, 2)` แล้วเทียบ `|expected − whtAmount| > 2.0`
- ทำไมพัง: ค่าที่ส่งเข้าไม่ใช่ยอด WHT **ที่อ่านจากกระดาษ** แต่คือผลคำนวณจาก subtotal เดียวกัน ⇒ ต่างกันได้สูงสุด 0.01 (banker's vs AwayFromZero) < tolerance 2.00 ⇒ rule 8 เป็น tautology · หมายเหตุ: `Math.Round(…, 2)` ที่ :208 ไม่ระบุ `MidpointRounding` (กฎเหล็ก #4 E) — เป็นการป้องกัน ไม่ใช่ยอดผิด เพราะผลถูกเทียบด้วย tolerance 2 บาท
- ผลกระทบ: ใบที่กระดาษพิมพ์ "หัก ณ ที่จ่าย 3% = 300" แต่ OCR อ่านอัตราเป็น 1% (หรือ subtotal อ่านผิด) ไม่มีวันถูกฟ้อง — ยอด WHT ที่ลง 50 ทวิ/ภ.ง.ด.53 ผิดเงียบ
- defect class: "control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control" / "ด่านที่เขียนไว้ครึ่งเดียว"
- ทางแก้: ต้องมีช่อง `WhtAmount` ที่อ่านจากกระดาษ (`SmartFieldExtractor` มี regex "หัก ณ ที่จ่าย … ยอด" หรือไม่ — ตรวจใน batch 2) แล้วส่งค่านั้นเข้า Gateway; ถ้าไม่มีให้ส่ง null (ปิดด่านอย่างซื่อสัตย์) และเติม `MidpointRounding.AwayFromZero` ที่ :208
- ฝ่ายค้าน: "ก็ยังตรวจ subtotal×rate ได้" → ตอบ: ตรวจกับตัวเองไม่ใช่การตรวจ ทดสอบง่าย ๆ: ใส่ rate 99% ก็ผ่าน

### T2-03 🟠 [P2][S] Gateway rule 3 ฟ้อง "อัตรา VAT ผิดปกติ" กับบิลผสม 7%/ยกเว้น (Makro/BigC) ทุกใบ — ขัดกับเจตนาที่ rule 2b เขียนไว้เอง
- ไฟล์: `Ocr/OcrConfidenceGateway.cs:92–107` (2b) vs `:110–119` (rule 3)
- โค้ด: :112 `var rate = vatAmount / subTotal * 100m; if (rate < 6.5m || rate > 7.5m) { warnings.Add("อัตรา VAT ผิดปกติ…"); penalty += 0.10 }`
- ทำไมพัง: คอมเมนต์ 2b (:86–91) อธิบายถูกต้องว่า "ใบที่มีหลายอัตรา VAT ≠ 7% ของยอดรวมโดยชอบธรรม แต่ Sub+VAT=Total เป๊ะ" และออกแบบ 2b ให้ไม่ฟ้อง — แต่ rule 3 บรรทัดถัดมาไม่มีเงื่อนไขนั้น · ตัวเลขจริง: Makro ยอดก่อนภาษี 1,000 (สินค้ามี VAT 700 + ยกเว้น 300) → VAT 49 → rate 4.9% → เตือน + −0.10 ทุกใบ
- ผลกระทบ: ใบที่ถูกต้องตามกฎหมาย (§81 ยกเว้น + §80 7% ในใบเดียว) ได้ confidence ต่ำ 0.10 โดยไม่มีเหตุ ⇒ ตกเกณฑ์ auto-create/short-circuit · ผู้ใช้ชินกับคำเตือนปลอมแล้วมองข้ามคำเตือนจริง ("checker ที่ฟ้องผิด = checker ที่พังแล้ว" ใช้กับคำเตือนถึงผู้ใช้)
- ทางแก้: ให้ rule 3 ฟ้องเฉพาะเมื่อ `!mathConsistent` หรือเมื่อ rate > 7.5% (สูงกว่า 7% เป็นไปไม่ได้ในใบเดียว — ต่ำกว่าเป็นไปได้จากส่วนผสมยกเว้น/0%) และเปลี่ยนข้อความเป็น "มีสินค้ายกเว้น VAT ปนอยู่ — ตรวจอัตรารายบรรทัด" (เป็นสัญญาณให้ `ThaiVatTypeRule` แทนคำเตือน)
- ฝ่ายค้าน: "rate ต่ำก็ควรเตือนเพราะ OCR อาจอ่าน VAT ขาดหลัก" → ตอบ: กรณีนั้น Sub+VAT≠Total อยู่แล้ว rule 2/2b จับได้ — เงื่อนไข `mathConsistent` แยกสองเคสนี้ออกจากกันพอดี

### T2-04 🟠 [P1][M] Azure DI: ช่องที่ prebuilt-invoice ให้มาแล้วถูกทิ้ง — ProductCode / Unit / Tax รายบรรทัด / DueDate / PurchaseOrder / TaxDetails / PaymentTerm / TotalDiscount
- ไฟล์: `Ocr/AzureDocumentIntelligenceService.cs:399–470` (สิ่งที่อ่านจาก JSON) · `OcrService.cs:2666–2728` (สิ่งที่ map ต่อ)
- โค้ด: AzureDI svc :467 `ProductCode = GetStringField(itemObj, "ProductCode")` แต่ OcrService :2720–2728 map เฉพาะ `Description/Quantity/UnitPrice/Amount` ⇒ ProductCode หายที่รอยต่อ · AzureDI svc :426 `PurchaseOrder`, :428 `DueDate`, :435 `PreviousUnpaidBalance` ถูกอ่านเข้า `AzureDiResult` แล้ว **grep `azure.DueDate|azure.PurchaseOrder|azure.PreviousUnpaidBalance` ใน OcrService = 0 จุด**
- ช่องของ prebuilt-invoice ที่**ไม่ถูกอ่านเลย** (ตาม schema Azure DI 2024-11-30 invoice): `Items[].Unit`, `Items[].Tax`, `Items[].TaxRate`, `Items[].Date`, `TaxDetails[] (Amount, Rate)` — ตัวหลังคือคำตอบตรง ๆ ของโจทย์ VAT ผสม (E-OCR-01) ที่ปัจจุบันต้อง**เดา**รายบรรทัดจาก keyword (`ThaiVatTypeRule.Suggest` :5040) · `PaymentTerm`, `TotalDiscount`, `ServiceStartDate/EndDate`, `RemittanceAddress`, `VendorAddressRecipient`, `CustomerId`, `CurrencyCode` ของ `InvoiceTotal.valueCurrency`
- ทำไมเป็นช่องว่าง: (1) `DueDate` มีอยู่แล้วแต่ `EnrichFromRawText` :7060 ไป regex "ครบกำหนด" จาก raw text ใหม่ (2) `PurchaseOrder` มีอยู่แล้วแต่ PO linkage (:4522/:7165) ให้ผู้ใช้เลือก PO เอง (3) `Items.TaxRate` มีแล้วแต่ระบบเดาอัตราจากคำอธิบายสินค้า (4) `ProductCode` มีแล้วแต่ product xref (:1074) จับคู่ด้วยชื่อ
- ผลกระทบ: ความแม่นที่**จ่ายเงินซื้อมาแล้ว** ถูกทิ้ง แล้วไปเดาแทน — ตรงข้ามกับโจทย์ "อัพแล้วจบ" · กฎเหล็ก #3 ข้อ 1 ต้องการ `PaymentTerms` + `VatRate` รายบรรทัด ซึ่ง Azure ให้ได้ตรง ๆ
- ทางแก้ (M): เพิ่ม `Unit/TaxRate/TaxAmount/ProductCode/Date` ใน `AzureDiLineItem` + map ที่ :2720 ; เพิ่ม `TaxDetails` → ถ้ามี rate เดียว = 7 และ Σ ตรง → ตั้ง VatRate ทุกบรรทัด, ถ้ามีหลาย rate → ใช้ Items[].TaxRate ก่อน keyword ; `DueDate` → `PaymentTermsDays` ก่อน regex ; `PurchaseOrder` → auto-link PO เมื่อเลขตรงกับ PO เปิดของ vendor เดียวกัน ; `PreviousUnpaidBalance` → เตือนเมื่อ `AmountDue ≠ InvoiceTotal`
- ฝ่ายค้าน: "Azure บนเอกสารไทยให้ TaxRate ไม่ค่อยได้" → ตอบ: ใช้เป็นชั้นแรกแบบ fill-if-present เท่านั้น เดิมมี fallback อยู่แล้ว ไม่มีอะไรแย่ลง; ค่าที่ Azure ให้มีข้อดีคือมี confidence กำกับ
- ความมั่นใจ: สูงสำหรับ ProductCode/DueDate/PurchaseOrder/PreviousUnpaidBalance (เห็นการอ่านและไม่เห็นการใช้) · กลางสำหรับรายชื่อช่อง Azure ที่ไม่ได้อ่าน (อ้าง schema สาธารณะของ Azure ไม่ใช่โค้ดในเรพ)

### T2-05 🔴 [P1][M] Reconciliation 4 เคส (:4970–5010) เทียบ "ผลรวมบรรทัดก่อน VAT" กับ "ยอดรวมหลัง VAT" ⇒ บิลที่มีส่วนลดกลายเป็น "ราคารวม VAT + ค่าขนส่งผี" หรือ "ส่วนลดผิด %" — ยอดเอกสารไม่ตรงกระดาษ
- ไฟล์: `OcrService.cs:4980–4985` (เกณฑ์ Case A) · `:5008–5016` (Case C)
- โค้ด: :4984 `pricesIncludeVatFlag = … grossSum > hdrSub + TOL && grossSum <= hdrTotal + TOL` · :5008 `else if (grossSum > hdrTotal + TOL …) docDiscountPercent = (grossSum − hdrTotal)/grossSum`
- ทำไมพัง: `grossSum` = Σ UnitPrice×Qty ของบรรทัด (ก่อน VAT ในใบส่วนใหญ่) แต่ตัวเทียบทั้งสองเคสคือ `hdrTotal` (หลัง VAT) — คนละฐาน · ใบที่มีส่วนลดท้ายบิลจึงไปเข้าเคสผิดเสมอ (ส่วนลดคือเหตุที่ grossSum > subtotal)
- reproduce (`scratchpad/T2/recon_sim.py`, ตรรกะลอกจาก :4980–5016 + :5150):
  1. สินค้า 1,000 ลด 50 → กระดาษ sub 950 · VAT 66.50 · total 1,016.50 ⇒ grossSum 1,000 อยู่ระหว่าง 951–1,017.5 → **Case A**: `PricesIncludeVat=true` + **เพิ่มบรรทัด "ค่าขนส่ง/บริการอื่น 16.50" ที่ไม่มีบนกระดาษ** (ส่วนลด 50 หายไป กลายเป็นค่าขนส่ง 16.50 ⇒ ผังบัญชีผิด 2 บรรทัด)
  2. สินค้า 1,000 ลด 200 → sub 800 · VAT 56 · total 856 ⇒ **Case C** ส่วนลด **14.40%** (จริง 20%) → บรรทัด 856 ถูกถือเป็นยอดก่อน VAT → เอกสาร sub 856 + VAT 56 = **912 ≠ 856**
  3. ร้านอาหารราคารวม VAT 1,070 ลด 3% (32.10) → total 1,037.90 ⇒ Case C 3% ถูกโดยบังเอิญ แต่ `PricesIncludeVat=false` ⇒ เอกสารรวม **1,105.80 ≠ 1,037.90**
  4. ราคารวม VAT จริง (IKEA 1,396) → Case A ถูก ✅ · ราคาแยก VAT ไม่มีส่วนลด → B ✅ (เคสที่เทสต์ `OcrLineReconcileTests` ครอบ — ต้องเช็คว่ามีเคสส่วนลดไหม)
- ด่านตรวจ Σ ที่ตามมา (:5052–5060) ตรวจเฉพาะ Σ VAT รายบรรทัด vs VAT หัวใบ และ**แค่ `LogWarning`** — ไม่มีด่านไหนเทียบ Σ Line.Amount กับ SubTotal หัวใบก่อน `_db.Documents.Add` ⇒ ทั้งสามเคสผ่านเงียบ
- ผลกระทบ: เอกสารซื้อที่ยอดรวมไม่ตรงใบกำกับ (ภาษีซื้อ §87 ยื่นฐานผิด, เจ้าหนี้ตั้งผิด) และบรรทัด "ค่าขนส่ง" ที่แต่งขึ้น (defect class "ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อ" — โค้ดยอมรับเองในคอมเมนต์ :4972 ว่าเคยตีค่าขนส่ง 50 เป็นส่วนลด 600 แล้วแก้มาเป็นทิศตรงข้าม)
- ทางแก้: (1) ใช้ `data.DiscountAmount` ที่ `EnrichFromRawText` :7093 อ่านจากกระดาษได้แล้ว **แต่ไม่มีใครใน CreateDocumentFromScan อ่าน** (grep ใน batch 3) เป็นสัญญาณตัวแรก (2) จัดลำดับเคสใหม่: ถ้า |grossSum − hdrSub| ≤ TOL → B · ถ้า |grossSum − hdrTotal| ≤ TOL → A (ราคารวม VAT) · ถ้า grossSum > hdrSub และ Σ(gross − hdrSub) ≈ DiscountAmount → C โดยคิด % จาก **hdrSub** ไม่ใช่ hdrTotal · อื่น ๆ → D + เตือนบนตัวเอกสาร (3) เพิ่มด่านสุดท้าย `|Σ Line.Amount − SubTotal| ≤ 0.02` ก่อน Add ไม่ผ่าน = เขียน `InternalNotes` + `RdComplianceStatus` ไม่ใช่ LogWarning
- ฝ่ายค้าน: "ผู้ใช้ตรวจในหน้า review ก่อนอนุมัติอยู่แล้ว" → ตอบ: กฎเหล็ก #3 ตั้งเป้า 1-click approve และ auto-create (:2318) สร้างเอกสารโดยไม่มีคนดู; ยอดที่ผิด 16.50 บาทบนใบ 1,016.50 ไม่มีใครเห็นด้วยตา
- ความมั่นใจ: สูง (จำลองด้วยตรรกะที่ลอกบรรทัดต่อบรรทัด) — ต้องเช็ค: `OcrLineReconcileTests` ครอบเคสส่วนลดไหม (batch 3)

### T2-06 🟠 [P2][S] Confidence ของเส้น Tesseract/hybrid ไม่เคยสะท้อนคุณภาพ OCR — `max(word-conf, keyword-conf)` + floor 0.90
- ไฟล์: `OcrService.cs:3342` · `:500–502` · `:4098–4108` (ParseThaiDocument ตั้ง 0.95 เมื่อเจอ "ใบกำกับภาษี")
- โค้ด: :3342 `data.Confidence = Math.Max(result.Confidence, data.Confidence);` · :502 `extractedData.Confidence = Math.Max(extractedData.Confidence, Math.Max(0.90m, embeddedResult.Data.Confidence));`
- ทำไมพัง: Tesseract อ่านได้ word-confidence 0.40 (ภาพเบลอ) แต่เจอคำว่า "ใบกำกับภาษี" ⇒ Confidence เอกสาร = 0.95 ; hybrid ตั้ง floor 0.90 แม้ text-layer จะเป็นขยะ (PDF ที่ text-layer เป็น glyph เพี้ยน) · Gateway หักเฉพาะ**ปัญหาที่ตรวจพบ** — ถ้าทุกช่องมีค่า (ผิดแต่ครบ) ก็ไม่หัก ⇒ ≥ 0.85 = ผ่าน `Ocr:AutoCreateThreshold` (:2259) → auto-create ได้
- ผลกระทบ: ค่าที่ควรทำหน้าที่ "ตัวบอกว่าอ่านชัดแค่ไหน" ถูกทับด้วย "ตัวบอกว่าเป็นเอกสารชนิดไหน" — สองความหมายในช่องเดียว (ญาติของ "ห้าม reuse field ผิดความหมาย") · ป้าย % ที่ผู้ใช้เห็นบอกว่า 95% ทั้งที่ engine เองบอก 40%
- ทางแก้: แยก `TypeConfidence` ออกจาก `OcrConfidence`; เอกสาร = min หรือ weighted (เช่น 0.6×word + 0.4×type) ; hybrid ให้ floor เฉพาะเมื่อ `PdfTextLayerExtractor.HasUsableText` **และ** ParseThaiDocument พบ ≥ 3 ช่องหลัก
- ฝ่ายค้าน: "Tesseract word-conf บนภาษาไทยต่ำเสมอ ถ้าใช้จะไม่มีใบไหน auto-create" → ตอบ: นั่นคือคำตอบที่ถูก — เส้น Tesseract **ไม่ควร** auto-create; ปัจจุบันมันทำได้เพราะเลขถูกแต่ง

### T2-07 🟠 [P2][S] `ThaiVatTypeRule.ExemptKeywords` มีคำ 2 ตัวอักษร ("นม", "ผัก", "หนังสือ", "milk", "ผลไม้") ใช้ `Contains` ⇒ ขนมปัง · น้ำผลไม้ · หนังสือค้ำประกัน · milk tea กลายเป็น "ยกเว้น VAT" — ภาษีซื้อของบรรทัดนั้นถูกผลักไปบรรทัดอื่น รายงาน §87 คอลัมน์ผิด (ทิศกลับกับบั๊กที่เพิ่งแก้ 5e3a323)
- ไฟล์: `Helpers/ThaiVatTypeRule.cs:31–43` (keywords) · `:49–53` (`d.Contains(k)`) · ผู้เรียก `OcrService.cs:5040–5046`
- reproduce: "ขนมปังโฮลวีท" ⊃ "นม" → Exempt · "น้ำผลไม้รวม 100%" ⊃ "ผลไม้" → Exempt (น้ำผลไม้บรรจุขวด = 7%) · "ค่าธรรมเนียมหนังสือค้ำประกัน" ⊃ "หนังสือ" → Exempt · "Thai milk tea" ⊃ "milk" → Exempt · "ผักกาดดองกระป๋อง" ⊃ "ผัก" → Exempt (แปรรูป = 7%)
- ผลกระทบ: `SpreadHeaderVat` (:5049) เฉลี่ย VAT หัวใบเฉพาะบรรทัดที่ rate>0 ⇒ Σ ยังตรงหัวใบ (เงียบสนิท) แต่บรรทัดที่ถูกตียกเว้นได้ VAT 0 และบรรทัดอื่นได้ VAT เกินจริง ⇒ ฐานภาษีซื้อรายบรรทัด/รายงาน §87 แยกคอลัมน์ผิด — ถ้าใบมีบรรทัดเดียวและถูกตีว่ายกเว้น: `taxableIdx.Count == 0` → VAT ทุกบรรทัด = 0 แต่หัวใบมี VAT → ด่าน :5052 แค่ `LogWarning` (ดังในที่ที่ไม่มีคนดู — บทเรียน `LogWarning แล้วเดินต่อ`) แล้วสร้างเอกสารที่ Σ VAT บรรทัด = 0 ≠ VAT หัวใบ
- ทางแก้ (S): (1) คำสั้น ≤ 3 ตัวอักษรต้อง match แบบ**ขอบคำ**/token ไม่ใช่ substring (2) เพิ่ม negative list ("ขนม", "น้ำ…", "ค้ำประกัน", "tea") (3) เมื่อ `taxableIdx.Count == 0 && headerVat > 0` ให้ถอยกลับเป็น 7% ทุกบรรทัด + เตือนบนเอกสาร ไม่ใช่ปล่อยศูนย์ (4) ยกด่าน :5052 จาก LogWarning เป็น `InternalNotes` + `RdComplianceStatus`
- ฝ่ายค้าน: "ผู้ใช้แก้ได้และเรียนกลับผ่าน VatTypeInference" → ตอบ: ใบซื้อจาก OCR ที่ auto-create ไม่มีจุดให้ผู้ใช้เห็นอัตรารายบรรทัดก่อนลงบัญชี; และ feedback loop ของ `AiFeatureKey.VatTypeInference` ต้องมีคนกดแก้ก่อน — บั๊ก false-exempt เงียบสนิทตามที่โค้ดเองบรรยาย

### T2-08 🟠 [P2][M] e-Tax XML tier 0 — "ค่าที่ถูกต้อง 100%" ถูก map ทิ้งไปหลายช่อง: SubTotal เลือก LineTotal ก่อน TaxBasis (ส่วนลดหาย) · Unit/VAT category รายบรรทัดไม่ส่งต่อ · FieldConfidence 1.0 ถูกตั้งให้ช่องที่ XML ไม่มีค่า · ไม่อ่าน InvoiceReferencedDocument/PurposeCode (§86/9–10)
- ไฟล์: `OcrService.cs:6728–6790` (`MapEtaxToOcrData`) · `Ocr/EtaxPdfXmlExtractor.cs:387–440`
- โค้ด: :6745 `SubTotal = etax.LineTotal ?? etax.TaxBasis` — ในสคีมา ETDA (CII) `LineTotalAmount` = Σ บรรทัด**ก่อน**ส่วนลด/ค่าธรรมเนียมระดับเอกสาร ส่วน `TaxBasisTotalAmount` = ฐาน VAT (หลังส่วนลด) ⇒ ใบที่มี `SpecifiedTradeAllowanceCharge` ได้ SubTotal สูงเกิน → `Sub + VAT ≠ Total` → Gateway rule 2 หัก 0.20 บน**เอกสารที่ authoritative ที่สุด** และ DiscountAmount ไม่ถูกตั้ง
- :6754–6763 ตั้ง `FieldConfidence[...] = 1.0` ให้ 13 ช่องรวม `BuyerName/BuyerTaxId/BuyerBranchCode/SellerAddress` **โดยไม่เช็คว่าค่านั้น null ไหม** ⇒ หน้า review โชว์ "— · 100%" ข้างช่องว่าง (ขัดกับกติกาที่ UI เขียนไว้เอง :1988–1993 "ไม่รู้ = บอกว่าไม่รู้")
- :6766–6773 map บรรทัดเฉพาะ Description/Quantity/UnitPrice/Amount — `LineItem.Unit` (extractor :418 อ่าน `unitCode` มาแล้ว) ถูกทิ้ง → `UnitInferrer` เดาแทน · `ApplicableTradeTax` รายบรรทัด (CategoryCode S/Z/E + RateApplicablePercent) ไม่อ่านเลย → ใบ e-Tax ที่ผสม 7%/ยกเว้นไปเดาด้วย keyword (T2-07) ทั้งที่ XML ระบุตรง ๆ
- ไม่อ่าน `InvoiceReferencedDocument` (เลขที่+วันที่ใบเดิมของ CN/DN — บังคับตาม §86/9–10 และ CLAUDE.md ข้อ A) และ `PurposeCode` (เหตุผลลดหนี้) ⇒ CN จาก e-Tax ยังต้องให้ผู้ใช้เลือกใบเดิม/เหตุผลเอง (ขัดกฎเหล็ก #3)
- ทางแก้: SubTotal = TaxBasis ?? LineTotal · DiscountAmount = LineTotal − TaxBasis เมื่อ > 0 · ตั้ง confidence 1.0 เฉพาะช่องที่ไม่ null · map Unit + VatRate รายบรรทัดจาก ApplicableTradeTax · อ่าน InvoiceReferencedDocument → `OriginalTaxInvoiceId` lookup + PurposeCode → `CreditNoteReason`
- ฝ่ายค้าน: "ใบ e-Tax ที่มีส่วนลดระดับเอกสารมีน้อย" → ตอบ: ห้างค้าปลีกรายใหญ่ (ผู้ออก e-Tax หลักของประเทศ) ใช้ส่วนลดท้ายบิลเป็นปกติ; และการตั้ง 100% ให้ช่องว่างเป็นเรื่องความซื่อสัตย์ของ UI ไม่ขึ้นกับความถี่

> ✏️ แก้ T2-01: ที่เขียนว่า "ReasoningTrace ไม่ persist (ดู T2-06)" — **ผิด** ตรวจแล้ว :1512–1514 join trace ทั้งหมดลง `ProcessingNotes` ตอนท้าย pipeline (คอมเมนต์ :527 "in-memory only" เป็นคอมเมนต์ค้าง) · เส้น Failed ที่ :551 append เฉพาะบรรทัดสุดท้าย แต่พอเดินต่อถึง :1514 ก็ได้ครบ — ผลกระทบของ T2-01 จึงเหลือ "สถานะผิด + โควตาไม่คืน" เท่านั้น (บันทึกตามกติกา "รายงานตัวเองผิดต้องจดว่าผิด")

### T2-09 🔴 [P0][S] `AmountTripleExtractor` **ทับค่าที่ engine อ่านถูก** ด้วยสามค่าที่แต่งขึ้น — บิลผสม 7%/ยกเว้น (Makro/7-11/BigC) ยอดรวมกลายเป็น "เงินสดที่จ่าย" และใบส่งออก 0% ได้ VAT 3,271 บาทที่ไม่มีบนกระดาษ — ทุก engine ทุกใบ
- ไฟล์: `Ocr/SmartFieldExtractor.cs:777–806` (`TryExtractAmountsFromRawText` — เรียกเป็นขั้น 0b ของ `Enrich` :54 ซึ่งรันบน **ทุก tier** :2662/:3348/:3499/:504) · `Ocr/AmountTripleExtractor.cs:39–117`
- โค้ด: :788 `existingConsistent = |s+v−t| < 1 && |v/s − 0.07| < 0.01` → ถ้าไม่ผ่านให้ :791 `Extract(text)` แล้ว :795 **"OVERRIDE existing partial / inconsistent values"** เมื่อ `sub+vat≈total` · ใน Extract: :84 อัตรานอก 7%±2% → `continue` (ทิ้ง triple ที่ถูก) · :104–115 ไม่พบ triple → **หยิบตัวเลขใหญ่สุดบนหน้า** เป็น Total แล้วแตก 7% ให้เอง เมื่อมีคำว่า ใบกำกับภาษี/TAX INVOICE
- ทำไมพัง (ขั้นตอน): (1) บิลผสม 7%/ยกเว้น ⇒ VAT/Sub < 5% ⇒ `existingConsistent=false` แม้ engine อ่านถูกทุกช่อง (2) Extract ปฏิเสธ triple ที่ถูก (อัตรา 4.9%) แล้วไปหา triple อื่นที่ "บวกลงตัว" — บนใบเสร็จมี **เงินสด/เงินทอน** เสมอ ⇒ (เงินทอน + VAT = รวมสินค้า) ผ่านคณิตศาสตร์ (3) ไม่พบ → หยิบ **เงินสดที่ลูกค้าจ่าย** (เลขใหญ่สุด) เป็น Total แล้วแต่ง VAT 7% (4) ตั้ง `FieldConfidence = 0.95` ให้ค่าที่แต่งขึ้น (:799–801) ⇒ ไฮไลต์เหลืองไม่ขึ้น
- reproduce (`scratchpad/T2/triple_sim.py` — ลอกตรรกะบรรทัดต่อบรรทัด):
  - **Makro**: Azure อ่านถูก sub 1,000 · VAT 49 · total 1,049 (สินค้ามีภาษี 700 / ยกเว้น 300) · บนใบมี "เงินสด 2,000 / เงินทอน 951" ⇒ **OVERRIDE → sub 951 · VAT 49 · total 1,000** (subtotal = เงินทอน!)
  - **7-Eleven**: อ่านถูก 30.54 / 0.46 / 31.00 (น้ำดื่ม 7% + นสพ./นม ยกเว้น) · เงินสด 100 ⇒ **OVERRIDE → 93.46 / 6.54 / 100.00**
  - **ใบส่งออก 0% (§80/1)**: อ่านถูก 50,000 / 0 / 50,000 ⇒ VAT 0 ทำให้ไม่ consistent → ไม่มี triple (0.00 ถูกกรองที่ :48 `>= 1`) → fallback แตก 7% ⇒ **OVERRIDE → 46,728.97 / 3,271.03 / 50,000** = ภาษีซื้อ/ขาย 3,271 บาทที่ไม่มีจริง
- ผลกระทบ: ภ.พ.30 ผิดทั้งฐานและภาษี บนเอกสารกลุ่มที่พบ**ทุกวัน** · ทับค่าที่ Azure (จ่ายเงินซื้อ) อ่านถูกแล้ว · confidence 0.95 ปิดปากด่านทุกตัวที่ตามมา (Gateway rule 2/2b/3 เห็นสามค่าที่สอดคล้องกันเอง) · ใบส่งออกที่คอมมิต 6489723 เพิ่งแก้ให้เข้าช่อง 0% ของ ภ.พ.30 จะถูกยัด VAT กลับเข้าไปตั้งแต่ขั้น OCR
- defect class: "ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้ อันตรายกว่าการไม่ตอบ" + "ด่านที่ 'ทับค่า' ต้องถามว่าฝั่งที่ยึดเป็นความจริงถูกไหม" (บทเรียน ปกส.) + "threshold ที่ตั้งจากตัวอย่างชนิดเดียวพังกับชนิดอื่น" (7%±2% ตั้งจากใบ 7% ล้วน)
- ทางแก้ (S): (1) `existingConsistent` ต้องถือว่า **`s+v≈t` อย่างเดียวก็พอ** (อัตรา < 7% ชอบธรรมตาม §81/§80(1)) และถือ VAT=0 ที่ `s=t` ว่า consistent (2) ห้าม override ค่าที่ engine ให้มาครบสามช่อง — ทำได้แค่ **เติมช่องว่าง** + เตือน (3) fallback "หยิบเลขใหญ่สุด" ต้องตัดตัวเลขที่อยู่หลัง anchor เงินสด/เงินทอน/รับมา/Change/Cash/Tendered ออก และ**ห้ามแตก 7% เมื่อมีคำว่า ยกเว้น/0%/Export/ภ.พ.36** บนหน้า (4) triple ที่ Sub มาจากตำแหน่ง**หลัง** Total บนหน้า (เงินทอนอยู่ท้ายเสมอ) ต้องถูกลงโทษ ไม่ใช่แค่ +20 ให้ Total ที่อยู่ท้าย
- เทสต์ที่ต้องมี: 3 เคสข้างบนเป็น negative test ของ `AmountTripleExtractor` โดยตรง (ปัจจุบัน `OcrAmountTriangleTests` ทดสอบเฉพาะ Gateway 2b — ตัวสกัดไม่มีเทสต์เลย ต้องเช็คต่อ)
- ฝ่ายค้าน: "Azure path มี existingConsistent กันอยู่แล้ว ใบ 7% ล้วนไม่โดน" → ตอบ: ถูก — แต่ใบที่โดนคือใบผสม/0% ซึ่งเป็นกลุ่มที่ระบบเพิ่งลงทุนแก้ (E-OCR-01, ThaiVatTypeRule, ภ.พ.30 ช่อง 7/8) ทั้งหมด**ไร้ผล**ถ้าตัวเลขหัวใบถูกทับตั้งแต่ขั้น 0b · "เงินทอนบวก VAT เท่ารวมสินค้าเป็นเรื่องบังเอิญ" → ตอบ: ไม่บังเอิญ — เงินทอน = เงินสด − total ⇒ บนใบที่ลูกค้าจ่ายแบงก์ 1,000/2,000 ตัวเลขเหล่านี้สัมพันธ์กันเป็นระบบ และ sim สองใบจากสองร้านโดนทั้งคู่
- ความมั่นใจ: **สูง** สำหรับกลไก (ลอกโค้ด) · ต้องเช็ค: ค่าคงที่ `MathTolerance`/`VatRateTolerance` (:1–38) และ `LooksLikeVatDoc` (:151) ให้ตรงกับที่จำลอง (ทำต่อใน batch 4)

### T2-10 🔴 [P1][S] "เลขที่" ของ**ที่อยู่**ผู้ขาย (เลขที่ 99/1 ถ.สุขุมวิท / No. 123/45 Sukhumvit Rd.) ชนะ "เลขที่ใบกำกับ" — ทั้ง ParseThaiDocument และ SmartFieldExtractor
- ไฟล์: `OcrService.cs:4218–4228` (ParseThaiDocument pattern 1 `เลขที่\s*[:：]?\s*([A-Za-z0-9\-/]+\d+)`) · `Ocr/SmartFieldExtractor.cs:880–897` (pattern 2 `เลขที่(?!ใบแจ้งหนี้|สัญญา|บัญชี|ผู้เสียภาษี)…` และ pattern 3 `(?:^|\s)No\.\s*(…)` ซึ่งมาก่อน pattern 4 `Invoice No.`)
- reproduce (`scratchpad/T2/docnum_sim.py`, `docnum_smart_sim.py`): หัวกระดาษ "บริษัท เอบีซี จำกัด / **เลขที่ 99/1** ถ.สุขุมวิท / ใบกำกับภาษี / เลขที่ IV6609-0001" → ParseThaiDocument = `99/1` · SmartFieldExtractor pattern 2 = `99/1` (`Regex.Matches` เรียงตามตำแหน่ง ตัวแรกที่มี ≥3 หลัก… "99/1" มี 3 หลักพอดี → รับ) · อังกฤษ "No. 123/45 Sukhumvit Rd. … Invoice No. INV-2026-0042" → `123/45` (pattern 3 ก่อน pattern 4)
- เกราะที่มีอยู่ไม่ช่วย: `TryExtractDocumentNumberFromRawText` :828 ข้ามเมื่อค่าเดิม "ยาว ≥4 และมีตัวเลข" ⇒ `99/1`/`123/45` ผ่าน · `EnsureDocumentNumberPlausible` :554 ตัดเฉพาะค่าที่**ไม่มีตัวเลข** · `DocumentNumberSanitizer` :595 จับเฉพาะ "สองเลขต่อกัน"
- ขอบเขต: เส้น Tesseract/hybrid/python-rule-based (เต็ม ๆ) + เส้น Azure เมื่อ `InvoiceId` ว่าง/สั้น <4 (:828) · แบบฟอร์มหัวจดหมายไทยพิมพ์ที่อยู่เป็น "เลขที่ … หมู่ … ถนน …" เป็นมาตรฐาน
- ผลกระทบ: เลขที่ใบกำกับผิด → รายงานภาษีซื้อ §87 คอลัมน์เลขที่ผิด · ด่านกันสแกนซ้ำ (เลขที่+ยอด :602) ไม่จับใบเดิม · เลขที่ที่ผิดถูก `AzureDiPatternLearner`/`VendorKnownGood` สอนต่อ (ญาติของบทเรียน VendorKnownGoodCorrector)
- ทางแก้ (S): ก่อนค้นเลขที่ ให้ **ตัด span ที่อยู่ออก** — "เลขที่/No." ที่ตามด้วย `หมู่|ม\.|ซอย|ซ\.|ถนน|ถ\.|ตำบล|ต\.|อำเภอ|อ\.|แขวง|เขต|จังหวัด|Moo|Soi|Rd|Road|Street` ภายใน 40 ตัวอักษร = ที่อยู่ ไม่ใช่เลขเอกสาร · ให้ pattern `Invoice No.`/`เลขที่ใบกำกับ` มาก่อน `No.` เปล่า · เพิ่มเทสต์จากสองใบข้างบน
- ฝ่ายค้าน: "เลข 99/1 กับ IV6609-0001 หน้าตาต่างกันชัด ใช้ shape ตัดสินได้" → ตอบ: เห็นด้วยว่าเสริมได้ (เลขเอกสารมักมีตัวอักษรนำ/ยาว ≥6) แต่ใบเล่มมีสำเนา "เลขที่ 0339" ก็เป็นตัวเลขล้วนสั้น — เกณฑ์บริบท (ที่อยู่) แม่นกว่าเกณฑ์รูปทรง

### T2-11 🟠 [P2][S] Azure layout-table fallback จับคอลัมน์จาก header ด้วย `Contains` แล้ว "จำนวน" กิน "จำนวนเงิน" — ตารางบริการ 3 คอลัมน์ได้ qty = ยอดเงิน, ราคา/หน่วย = ฿1
- ไฟล์: `Ocr/AzureDocumentIntelligenceService.cs:520–535` (การจับคอลัมน์) · `:545–560` (`if (amountCol < 0) amountCol = colCount − 1`)
- reproduce (`scratchpad/T2/azure_table_sim.py`): header `[ลำดับ, รายการ, จำนวนเงิน]` → qty=2, amount=2 (**คอลัมน์เดียวกัน**) ⇒ บรรทัด "ค่าบริการ 1,000" ได้ qty 1,000 amount 1,000 → `SanitizeVatSplitArtifacts` 1b (:3160) เห็น up=0,qty>0 → **UnitPrice = 1.00** ⇒ เอกสาร "ค่าบริการ 1,000 ชิ้น × ฿1" · header `[ลำดับ, รายการ, จำนวนเงิน, หมายเหตุ]` → amount = คอลัมน์หมายเหตุ → `ParseAmount` null ทุกแถว → `continue` ⇒ **ไม่ได้บรรทัดเลย** ทั้งที่ตารางอ่านได้ครบ · `ปริมาณ`/`ราคา` (คำไทยปกติ) ไม่อยู่ในลิสต์ → qty ตกไปที่ "จำนวนเงิน"
- ผลกระทบ: เส้นนี้คือทางรอดเมื่อ prebuilt-invoice ไม่คืน Items (ใบแจ้งหนี้ค่าบริการ/layout ไทย) ⇒ ใบที่ต้องการมันมากที่สุดได้ผลผิดรูป
- ทางแก้ (S): จับคอลัมน์ "จำนวนเงิน/Amount/รวม" **ก่อน** "จำนวน/Qty" · เพิ่ม `ปริมาณ`, `ราคา`, `หน่วยละ`, `ราคาต่อหน่วย`, `หน่วย` (unit column → `Unit`) · ห้ามให้ qty กับ amount ชี้คอลัมน์เดียวกัน · ParseAmount รับค่าลบ (บรรทัดส่วนลดในตาราง)
- ฝ่ายค้าน: "เคสนี้เกิดเฉพาะ layout fallback" → ตอบ: ใช่ แต่ fallback ถูกเรียก**ทุกครั้ง**ที่ Items ว่าง (:476, :373) ซึ่งคือใบบริการไทยส่วนใหญ่

### T2-12 🟠 [P1][M] `AiFeatureKey.OcrLineItemSplit` เรียก AI ทุกใบที่ Items ว่าง แต่**ไม่มี student** (`ILocalDistillationModel`) และ feedback ที่เก็บไว้ไม่มีใครอ่าน — ขัดกฎเหล็ก #1 ข้อ 2 (feature parity) + "learner write-only"
- ไฟล์: `Program.cs:531–592` (รายชื่อ student ทั้งหมด — ไม่มี OcrLineItemSplit ทั้งใน bespoke และ generic list :561–569) · `Services/Ai/Prompts/AdvancedPrompts.cs:538–548` (`LocalPrimaryAnswer = null, LocalModelVersion = "HeaderSummaryLine-v1"`) · `OcrService.cs:5769–5781` (ปิด loop `RecordUserChoiceAsync` — ฝั่ง**เขียน** feedback มี)
- ทำไมเป็นช่องว่าง: orchestrator ไม่มี student ให้ short-circuit ⇒ ทุกใบเส้น Tesseract/python ยิง provider **เสมอ** (ค่า token ต่อใบ ไม่ลดลงตามการใช้งาน — ตัวชี้วัด `UsedAi` ของกฎเหล็ก #1 ข้อ 6 อ่านไม่ได้) · feedback rows ที่ผู้ใช้แก้รายการถูกบันทึกแต่ไม่มี `LoadFromFeedbackAsync` ตัวไหน mine ⇒ write-only ตั้งแต่วันแรก (defect class เดียวกับ federated learner ใน CLAUDE.md)
- kill-switch: ผ่าน (local path = บรรทัดสรุปใบเดียว :5155) แต่ "ผ่านแบบเสื่อมถอย" — ปิด AI แล้วเอกสารจาก Tesseract **ไม่มีรายการสินค้าเลย** ตลอดกาล ไม่มีวันดีขึ้น
- ทางแก้ (M): student แบบ vendor-template — เรียนจาก feedback ว่า "ผู้ขายรายนี้ บรรทัดสินค้าอยู่ระหว่าง anchor X..Y รูปแบบ `desc qty price amount`" แล้ว regex split เอง (ข้อมูล `OcrLearnedPatterns` ต่อผู้ขายมีอยู่แล้ว) · ระหว่างยังไม่มี ให้ register `GenericFeedbackDistillationModel(OcrLineItemSplit)` อย่างน้อยเพื่อให้ exact-input cache ทำงานกับใบซ้ำ (ใบค่าไฟ/ค่าเช่ารายเดือน) และให้ตัวชี้วัดนับได้
- ฝ่ายค้าน: "output เป็น structured/bulk ต้องเขียน bespoke — ยังไม่คุ้ม" → ตอบ: กฎเหล็ก #1 ข้อ 2 ไม่มีข้อยกเว้น "ยังไม่คุ้ม"; ใบซ้ำรายเดือน (ค่าไฟ/ค่าเช่า/ค่าโทรศัพท์) คือกลุ่มที่ exact-input cache ตัด provider ได้ทันทีโดยไม่ต้องเขียน model ใหม่

### T2-13 🟠 [P2][S] วันที่แบบเดือนเต็มภาษาไทย ("5 กันยายน 2569") ไม่ถูกอ่านบนเส้น Tesseract/python — `FieldPatternLibrary` รู้จักแต่เข้าถึงได้ทางเดียวคือ zone-fallback ที่รันเฉพาะเมื่อ "ไม่มีทั้งชื่อผู้ขายและยอดรวม"
- ไฟล์: `OcrService.cs:4231–4236` (datePatterns มีแต่ตัวย่อ ม.ค./ก.พ.) · `ocr-service/app/ai_engine.py:246–275` (ตัวย่อเท่านั้น + `year > 2500` ไม่รองรับปีย่อ) · `Ocr/FieldPatternLibrary.cs:393–394` (มี "มกราคม","กุมภาพันธ์"…) · call site ของ FieldPatternLibrary = `DocumentZoneAnalyzer` เท่านั้น → `ApplyZoneAnalysisFallbackAsync` :6907 `if (VendorName != null || TotalAmount > 0) return`
- ผลกระทบ: ใบเสร็จเขียนมือ/แบบฟอร์มราชการ/สัญญาเช่า ที่พิมพ์ "วันที่ 5 กันยายน 2569" → `DocumentDate` null → Gateway −0.20 + เอกสารลงวันที่วันนี้ (ผิดงวด ภ.พ.30) + ผู้ใช้กรอกเอง (ขัดกฎเหล็ก #3) — ทั้งที่ตัวแยกมีอยู่ในเรพแล้ว ("ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" ทรง "เรียกจากที่เดียวที่ไม่ใช่เส้นทางหลัก")
- ทางแก้ (S): ย้าย date parser ของ FieldPatternLibrary มาเป็นตัวกลางตัวเดียว (`Helpers/ThaiDate.TryParse`) ให้ ParseThaiDocument · SmartFieldExtractor · python ใช้ร่วม (สำเนา regex วันที่ตอนนี้มี ≥3 ชุด = drift รออยู่)

### T2-14 🟠 [P1][S] ชื่อไฟล์ตัดสินโมเดล Azure และ**ชนิดเอกสาร**: "receipt_xxx.jpg"/"…cafe…" → prebuilt-receipt (ไม่มีช่องเลขผู้เสียภาษี/ผู้ซื้อในสคีมา) + `MapAzureDocType` บังคับ "Receipt" ⇒ ใบกำกับภาษีเต็มรูปกลายเป็นใบเสร็จ → ภาษีซื้อถูกปิดเคลม §82/5(1)
- ไฟล์: `Ocr/AzureDiRequestPlanner.cs:203–218` (`DetectModelFromFilename`: "receipt" · "ใบเสร็จ" · "เซเว่น" · "7-11" · "cafe" · "คาเฟ่") · `OcrService.cs:3079–3082` (`if (modelId.Contains("receipt")) return "Receipt";` **ก่อน**ดู docType ที่ Azure ตอบ) · ผลต่อ VAT: `:5177–5185` ปิด `IsVatClaimable` เมื่อเอกสารต้นทางไม่ใช่ใบกำกับเต็มรูป
- ทำไมพัง: ผู้ใช้ตั้งชื่อไฟล์ตามที่เขาเรียก ("ใบเสร็จร้านกาแฟ.jpg" ซึ่งจริง ๆ เป็น ใบเสร็จรับเงิน/ใบกำกับภาษี เต็มรูป) → โมเดล receipt ไม่คืน `VendorTaxId/CustomerName/CustomerTaxId` (สคีมา receipt ของ Azure มี Merchant* เท่านั้น — `MerchantTaxId` ที่โค้ด :403 อ่านไม่มีในสคีมา) → ช่อง §86/4 ว่างทั้งฝั่ง → พึ่ง regex บน raw text · ชนิดถูกล็อกเป็น Receipt โดย**ชื่อไฟล์** แม้กระดาษพิมพ์ "ใบกำกับภาษี" — `OcrDocumentRoleInferrer` (:746) อาจแก้ได้ถ้าอ่าน raw text แต่ค่าเริ่มต้นถูกตั้งจากชื่อไฟล์ ไม่ใช่กระดาษ
- ผลกระทบ: (ก) ภาษีซื้อของใบที่เคลมได้ถูกปิดเคลม (จ่ายภาษีเกิน) (ข) ฟีเจอร์ "ใบเสร็จ→ออกใบกำกับเต็มรูปแทน" (12c722b) ถูกกระตุ้นผิดใบ (ค) พฤติกรรมต่างกันตามชื่อไฟล์ — ผู้ใช้ไล่หาสาเหตุไม่ได้ (defect class "ค่า default ที่แต่งขึ้น" ในรูป heuristic)
- ทางแก้ (S): ให้ชื่อไฟล์เป็นแค่ **hint ลำดับท้าย** — เลือกโมเดลจากขนาดภาพ/สัดส่วน (สลิปยาวแคบ) และเมื่อใช้ receipt model แล้ว raw text มี "ใบกำกับภาษี" (ไม่ใช่ "อย่างย่อ") ให้ re-run prebuilt-invoice หรืออย่างน้อย**ไม่บังคับ** DocumentType จาก modelId (:3081 ลบทิ้ง ให้ ParseThaiDocument-style keyword จาก raw text ตัดสิน)
- ความมั่นใจ: สูงสำหรับกลไกในโค้ด · กลางสำหรับสคีมา receipt ของ Azure (อ้างเอกสารสาธารณะ) — ต้องเช็ค: `OcrDocumentRoleInferrer` เขียนทับ `DocumentType` จาก keyword บนกระดาษไหม (ทีม role inference)

### T2-15 🟠 [P2][S] `OcrLineSplitGuard` ตรวจแค่ "Σ ลงตัว" — ไม่ตรวจว่าตัวเลขแต่ละบรรทัด**มีอยู่บนกระดาษ** · บรรทัดส่วนลด (ลบ) ถูกตัดทิ้งก่อนบวก ⇒ บิลที่มีส่วนลดรายบรรทัดไม่มีวันผ่านด่าน
- ไฟล์: `Helpers/OcrLineSplitGuard.cs:94–95` (`if (amount is not > 0m) continue;`) · `:108–113` (Σ ±1 บาทกับ sub **หรือ** total) · prompt `AdvancedPrompts.cs:479–496` ("NEVER invent" — เป็นคำสั่งถึงโมเดล ไม่ใช่ด่าน)
- ทำไมเป็นช่องว่าง: (1) CLAUDE.md กฎเหล็ก #1 บังคับ anti-hallucination guard "validate คำตอบ AI กับข้อมูลจริง" — ที่นี่ข้อมูลจริง = token ตัวเลขใน raw text; AI ที่แตก 1,000 เป็น 600+400 (ไม่มีทั้งสองบนกระดาษ) ผ่านด่านได้ (2) ใบที่มี "ส่วนลด −50" เป็นบรรทัด: ตัดทิ้ง → Σ = 1,050 ≠ 1,000 → **ทิ้งทั้งชุด** → บรรทัดสรุปใบเดียว — จึงยิง AI (จ่าย token) แล้วทิ้งผลทุกครั้งสำหรับใบชนิดนี้ (3) `prices_include_vat` ในพรอมป์ตัดสินจาก "ไม่มี subtotal" เท่านั้น (:527) — ใบที่พิมพ์ทั้ง subtotal และราคารวม VAT รายบรรทัด (ห้างค้าปลีก) ให้สัญญาณผิดแก่โมเดล
- ทางแก้ (S): (1) ทุก `amount` (และ `unit_price` ถ้ามี) ต้องพบเป็น token ในข้อความ (เทียบหลังตัด `,` และช่องว่าง) — ไม่พบ ≥1 บรรทัด = ทิ้งทั้งชุด (2) รับบรรทัดลบเมื่อ description มี ส่วนลด/discount และนำเข้า Σ (3) หลังรับ ให้ส่งบรรทัดผ่าน `SanitizeVatSplitArtifacts` เหมือนเส้นอื่น (ปัจจุบันเข้าอยู่แล้วที่ :712 ✅)
- ฝ่ายค้าน: "OCR อาจอ่านตัวเลขบรรทัดเพี้ยน token check จะปฏิเสธผลที่ถูก" → ตอบ: ผลที่ "ถูก" แต่ตัวเลขไม่อยู่บนกระดาษที่ระบบเห็น = ระบบพิสูจน์ไม่ได้ — ทิ้งแล้วใช้บรรทัดสรุปคือทางที่โค้ดเลือกไว้เองใน doc-comment (:8–13)

### T2-16 🟠 [P2][S] Gateway rule 7 (Σ รายการ ≈ SubTotal) ฟ้อง**ทุกใบที่ราคาบรรทัดรวม VAT** (ค้าปลีก/ร้านอาหาร) — ยังไม่รู้จัก Case A ที่ตัวสร้างเอกสารรู้จัก
- ไฟล์: `Ocr/OcrConfidenceGateway.cs:171–185` · ผู้เรียก `OcrService.cs:562–590` (รัน**ก่อน** reconciliation :4970 ที่ตรวจ Case A)
- reproduce (`scratchpad/T2/gateway_rule7_sim.py`): IKEA บรรทัด 1,396 (รวม VAT) · sub 1,304.67 → diff 91.33 > 2.0 → "ผลรวมรายการ 1,396 ≠ ยอดก่อน VAT 1,304.67" + penalty 0.15 + `mathConsistent=false` — บนใบที่ตัวเลข**ถูกทุกช่อง**
- ทางแก้: ผ่านเมื่อ `|lineSum − subTotal| ≤ tol` **หรือ** `|lineSum − total| ≤ tol` (ราคารวม VAT) และส่งธง `pricesIncludeVat` กลับให้ผู้เรียกใช้แทนการอนุมานซ้ำที่ :4984 (ตัวตัดสินตัวเดียว — ตอนนี้ Gateway กับ CreateDocumentFromScan อนุมานเรื่องเดียวกันคนละสูตร = สำเนา)
- ฝ่ายค้าน: "penalty 0.15 ไม่ทำให้ auto-create ตก" → ตอบ: 0.95 − 0.15 = 0.80 < 0.85 ⇒ ตกจริง และ `mathConsistent=false` ถูกใช้ต่อที่อื่น (ต้องเช็ค call site ของ `MathConsistent`)

### T2-17 🟠 [P2][M] python ocr-service: ไม่มี preprocessing เลย (ไม่ EXIF-rotate / deskew / denoise / binarize) และ C# ส่งไฟล์ดิบไปตรง ๆ — ในขณะที่เส้น Azure ได้ `ImagePreprocessor` และ Tesseract ได้ grayscale+upscale
- ไฟล์: `ocr-service/app/ocr_engine.py:76–147` (PaddleOCR `use_angle_cls=True` = แก้แค่ 0°/180° · ไม่มีขั้น deskew/threshold) · `OcrService.cs:3376–3383` (`fileContent = new ByteArrayContent(fileData)` ดิบ — ไม่ผ่าน `Ocr.ImagePreprocessor.Process` ที่ :2637 ใช้กับ Azure) · `Ocr/EmbeddedTesseractOcrService.cs:320` (`PreprocessAsync` grayscale+upscale เท่านั้น)
- ทำไมเป็นช่องว่าง: ภาพจากมือถือ (เอียง 5–15°, EXIF orientation 6/8, แสงไม่สม่ำเสมอ) คือ input หลักของ SME — engine ที่อ่อนที่สุดสองตัวได้ภาพที่แย่ที่สุด · `ImagePreprocessor` ที่มีอยู่แล้ว ("ของที่สร้างไว้แล้ว") ถูกเรียกจากที่เดียว
- เพิ่มเติม: C# ไม่อ่าน `pages`/`page_count`/`multi_document_count` ที่ python ส่งมา (:3395–3470 ไม่มี TryGetProperty ของสามช่องนี้) ⇒ PDF หลายหน้าถูกรวมเป็นใบเดียวโดยมีแค่ warning ข้อความ · `field_confidence` ของ python (`main.py:80–128`) เทียบ substring ⇒ ค่า "1000" match กับ detection "10000" ได้ และ default 0.5 เมื่อไม่พบ = ตัวเลขที่ไม่ได้วัด
- ทางแก้ (M): เรียก `ImagePreprocessor.Process` ก่อนส่ง python (S) · เพิ่ม deskew (Hough/minAreaRect) + adaptive threshold ใน `extract_text` ก่อน PaddleOCR (M) · อ่าน `page_count` แล้วเก็บลง `ProcessingNotes` แบบมีโครง
- ฝ่ายค้าน: "PaddleOCR ทน rotation ระดับหนึ่งอยู่แล้ว" → ตอบ: ทนตัวอักษรเอียงได้ แต่ **การเรียงบรรทัด** (:145 sort ด้วย y แล้ว x ของ box) พังเมื่อภาพเอียง — ตัวเลขคอลัมน์ขวาไปอยู่บรรทัดถัดไป ⇒ ทุก regex ที่อิง "ป้ายกับค่าอยู่บรรทัดเดียว" (SmartFieldExtractor/AmountTriple AnchorBonus ≤100 chars) หลุดหมด

### T2-18 🟠 [P2][S] ไม่มีตัวสกัด "Service Charge 10%" และ "ปัดเศษ/เศษสตางค์" เลยทั้งเรพ — บิลร้านอาหาร (งานประจำวัน) Σ บรรทัด ≠ subtotal เสมอ → Case D เงียบ / Gateway rule 7 ฟ้อง / AI split ถูกทิ้งทั้งชุด
- ไฟล์: `grep 'ปัดเศษ|เศษสตางค์|service charge|Service Charge|ServiceCharge' OcrService.cs SmartFieldExtractor.cs AmountTripleExtractor.cs` = **0 จุด** · `EnrichFromRawText` :7093–7135 สกัดได้แค่ ส่วนลด + ค่าขนส่ง
- ทำไมเป็นช่องว่าง: บิลอาหาร: อาหาร 1,000 · SC 10% 100 · sub 1,100 · VAT 77 · total 1,177 · ปัดเศษ −0.25 ⇒ ระบบเห็นบรรทัด Σ 1,000 vs sub 1,100 → Case D (:5017 "ไม่ทำอะไร user แก้เอง") ⇒ เอกสารมีบรรทัด 1,000 แต่ header 1,100 (ขัดกันเองในใบเดียว — defect class "สองความจริง") · `OcrLineSplitGuard` ±1 บาท ทิ้งผล AI ทั้งชุดเพราะ SC ไม่ใช่ "line ที่ปรากฏบนกระดาษเป็นสินค้า" (พรอมป์ข้อ 4 บอกให้ตัดแถวสรุปทิ้ง) · เอกสารมี `ServiceChargePercent` อยู่แล้ว (POS ใช้) แต่สาย OCR ไม่เคยตั้ง
- ทางแก้ (S): ใน `EnrichFromRawText` เพิ่ม regex `(?:service\s*charge|ค่าบริการ)\s*(\d{1,2})\s*%?\s*([\d,]+\.\d{2})` → `data.ServiceChargePercent/Amount` แล้วให้ reconciliation ยอมรับ `Σ lines × (1+SC%) ≈ sub` · regex `(?:ปัดเศษ|เศษสตางค์|rounding)\s*[-−]?\s*(\d\.\d{2})` → เก็บเป็น `RoundingAdjustment` และรวมใน tolerance แทนการยอม ±1 บาทเหมา ๆ
- ฝ่ายค้าน: "ร้านอาหารส่วนใหญ่ออกใบกำกับอย่างย่อ เคลมไม่ได้อยู่แล้ว" → ตอบ: ค่าใช้จ่ายยังต้องลงบัญชีถูกยอด (§65 ตรี(4) ค่ารับรองมี cap ที่คิดจากยอดจริง) และใบกำกับเต็มรูปจากร้านอาหาร/โรงแรมเป็นปกติสำหรับบริษัท

### T2-19 🟠 [P2][S] (สงสัย — ต้องเช็ค culture ของ process) `DateTime.TryParse(value)` ไม่ระบุ culture 2 จุดในเส้น OCR — ถ้า server รัน th-TH ปฏิทินพุทธจะทำให้วันที่ ค.ศ. จาก Azure K-V/python ถูกอ่านเป็น ค.ศ. 1483 แล้วถูกล้างเป็น null
- ไฟล์: `OcrService.cs:2839` (`ApplyKeyValueFallback`: `DateTime.TryParse(value, out var d)`) · `:3418` (python: `DateTime.TryParse(dateStr, out var parsedDate)` กับ ISO "2026-09-05") · `Ocr/AzureDocumentIntelligenceService.cs:595–600` (`GetDateField` TryParse ทั้ง valueDate และ content) · Program.cs: **ไม่มี** `DefaultThreadCurrentCulture`/`InvariantGlobalization` (grep = 0)
- ทำไมสงสัย: CLAUDE.md บันทึกบั๊กจริงแล้วว่า `DateTime.ToString` ภายใต้ th-TH ให้ปี พ.ศ. — ทิศกลับกัน (Parse) ให้ 2026 ถูกตีเป็น พ.ศ. 2026 = ค.ศ. 1483 → `ValidateAndNormalizeDate` :540 `year < 1990` → ล้างค่า + confidence 0 ⇒ ทุกใบจาก python/K-V ไม่มีวันที่ · ขึ้นกับ `LANG`/culture ของคอนเทนเนอร์ที่รันจริง จึงจัดเป็น "สงสัย"
- ทางแก้ (S): ใช้ `DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, …)` หรือ `TryParseExact` กับรูปแบบที่รู้ · ตั้ง `CultureInfo.DefaultThreadCurrentCulture = InvariantCulture` ใน Program.cs (แล้วใช้ th-TH เฉพาะจุดแสดงผล)
- ต้องเช็ค: `Dockerfile`/`appsettings` ว่าตั้ง `LANG`/`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT` ไหม

### T2-20 🔵 [P3][M] ข้อเสนอออกแบบ: confidence **รายบรรทัด** ไม่มีอยู่ในโมเดลข้อมูล + `SanitizeVatSplitArtifacts` ยุบบรรทัดชื่อซ้ำโดยไม่ดูราคา
- `OcrExtractedLineItem` (:7322–7345) ไม่มีช่อง confidence ⇒ กฎเหล็ก #3 ข้อ 3 (ไฮไลต์ <0.85) ทำได้แค่ระดับหัวใบ — บรรทัดที่ `Qty×UnitPrice≠Amount` (Gateway rule 7b นับได้ว่ามีกี่บรรทัด) ไม่รู้ว่าบรรทัดไหน · Azure ให้ confidence ต่อ Items[].* อยู่แล้ว (ไม่ถูกอ่าน) · python มี `detailed[].confidence` ต่อ box
- `SanitizeVatSplitArtifacts` :3250–3275 ยุบบรรทัดที่ description เท่ากันเสมอ — "ค่าแรง 500" + "ค่าแรง 300" → qty 2 × 400 (ราคาต่อหน่วยที่ไม่มีบนกระดาษ) · ควรยุบเฉพาะเมื่อ UnitPrice เท่ากันด้วย
- ทางเลือกที่ปฏิเสธ: "ใช้ confidence หัวใบกับทุกบรรทัด" — คือสิ่งที่ UI เพิ่งถูกแก้ไม่ให้ทำ (:1988–1993)
- ฝ่ายค้าน: "เพิ่มช่องต้องแก้ DTO/JSON/UI หลายชั้น" → ตอบ: ใช่จึงเป็น M และ 🔵 — แต่มันคือสิ่งเดียวที่ทำให้ "แก้เฉพาะช่องที่ผิด" เป็นไปได้ในตารางรายการ

---
## §2 คำตอบคำถามร่วมของทุกทีม (มุมการสกัดข้อมูล)
- **นักบัญชีตัดสินอะไรจากกระดาษ / ระบบตัดสินข้อไหน**: ชนิดเอกสาร (ระบบ: keyword+role inferrer, แต่ชื่อไฟล์ทับได้ — T2-14) · ยอดสามช่อง (ระบบ: อ่านแล้ว**ทับ**ด้วยสูตร — T2-09) · ราคารวม/แยก VAT (ระบบ: อนุมานจากเลข ผิดเมื่อมีส่วนลด — T2-05) · อัตรา VAT รายบรรทัด (ระบบ: เดาจาก keyword — T2-07; Azure/e-Tax มีคำตอบตรง ๆ แต่ไม่อ่าน — T2-04/T2-08) · เลขที่ใบ (ระบบ: หยิบเลขที่บ้านได้ — T2-10) · วันที่ (ระบบ: ไม่อ่านเดือนเต็มไทย — T2-13) · service charge/ปัดเศษ (ระบบ: ไม่รู้จัก — T2-18) · WHT ยอดจริงบนกระดาษ (ระบบ: คำนวณเอาเอง ไม่อ่าน — T2-02)
- **กฎเหล็ก #3 field ที่ยัง null ถึงมือผู้ใช้**: `DocumentDate` (เดือนเต็มไทย) · `Unit` (e-Tax มีแต่ทิ้ง) · `VatRate` รายบรรทัดที่ถูก (เดา) · `PaymentTerms` (Azure DueDate มีแต่ทิ้ง) · `OriginalTaxInvoiceId`/`CreditNoteReason` (e-Tax XML มีแต่ไม่อ่าน) · รายการสินค้าทั้งตารางเมื่อ AI split ถูกทิ้ง (ส่วนลด/SC)
- **กฎเหล็ก #1**: `OcrLineItemSplit` ไม่มี student (T2-12) — feature เดียวในเส้น OCR ที่ยิง provider เสมอ
- **ค่า default ที่แต่งขึ้น (grep)**: `?? "00000"` :6695/:6697 (มี confidence 0.30 กำกับแล้ว — ยอมรับได้) · `?? DateTime.UtcNow` :2896/:4696/:5486/:6188 + `OcrController.cs:792` (วันที่เอกสาร = วันนี้เมื่ออ่านไม่ได้ → ผิดงวด ภ.พ.30 เงียบ) · `?? "Receipt"` :3407 · `?? "ชิ้น"` UnitInferrer:58 · **ตัวใหญ่สุด: AmountTriple fallback แตก 7% จากเลขใหญ่สุด (T2-09)** และ "ค่าขนส่ง/บริการอื่น" ที่แต่งขึ้นใน Case A (T2-05)

## §3 ตรวจแล้วไม่ใช่บั๊ก (เพื่อทีมอื่นไม่ต้องซ้ำ)
- **FieldConfidence key 3 ชุด** (บทเรียน CLAUDE.md) — ปิดแล้วจริง: Azure canonical ที่ต้นทาง (AzureDI svc :445) · Gateway canonical (:141–148) · persist canonical (:984) · response ทั้ง fresh และ reload canonical (:6664–6666, :6451–6460) · UI ถามด้วยชื่อกลางครบ 14 ช่อง (document-scan.html :2186–2266) และไม่ fallback ไป confidence ทั้งใบ (:1988–1993) ✅
- **BranchCodeExtractor เฉพาะ Tesseract** — ปิดแล้วจริง: `EnrichFromRawText` :6979 เรียกบนทุก engine ✅ · **DocumentZoneAnalyzer.Analyze ไม่มี call site** — ปิดแล้ว (:6914) แต่เงื่อนไขเข้าแคบ (T2-13 อธิบายผลข้างเคียง)
- **OcrScanSnapshot** (444d2cb) — ไม่ได้ตรวจซ้ำ (นอกขอบเขต T2)
- `SpreadHeaderVat` ใช้ `MidpointRounding.AwayFromZero` ครบ · WHT รายบรรทัดใช้ remainder-on-last-line ⇒ Σ ตรงหัวใบ ✅ · `Math.Round(...,2)` ที่ไม่ระบุ mode ใน Gateway rule 8 / SanitizeVatSplitArtifacts / SmartFieldExtractor `/1.07` — ตามบทเรียน `VatRoundingModeTests` สูตร `/1.07` ไม่ตกจุดกึ่งกลาง; ที่เหลือถูกเทียบด้วย tolerance ≥ 0.5 ⇒ **ไม่ใช่ยอดผิด** เป็นแค่การป้องกัน
- `ThaiAmountInWords` (จำนวนเงินตัวอักษร → ตัวเลข) มีและถูกเรียกใน `Enrich` ขั้น 12 ✅ — ด่านที่แรงจริง (แต่รันหลัง AmountTriple ทับค่าไปแล้ว — ถ้าแก้ T2-09 ด่านนี้ควรเป็นตัวชี้ขาดแทน)
- python `/ocr/correct` มี caller จริง (OcrService :3834) — ไม่ใช่ learner write-only
- Tier ล้มเหลว "ดัง" พอในระดับ trace: `tierTrace` ทุก tier ถูก prepend เข้า ReasoningTrace (:554–556) และ persist ลง ProcessingNotes (:1512–1514) ✅ — ยกเว้นสถานะ (T2-01)
- Embedded Tesseract รองรับ PDF หลายหน้า (cap 10 หน้า, เฉลี่ย confidence) ✅ · Azure multi-document เตือนชัด (:379–383) ✅
- Duplicate check เลขที่+ยอด (:602) และ `DocumentNumberSanitizer` ทำงาน — แต่รับ input ที่ผิดจาก T2-10

## §4 ยังไม่ได้ตรวจ
- `OcrDocumentRoleInferrer` (725 บรรทัด) — ทิศทางซื้อ/ขาย, 50 ทวิ (ทีม role/flow) · `VendorIntelligenceService`, `ProductMatcher`, `ExpenseCategoryResolver`, GL classification teacher→student (:1314) — เป็นการ "ตัดสิน" ไม่ใช่ "สกัด"
- `OcrFullReviewDistillationModel` และปุ่ม "ขอ AI ตรวจอีกครั้ง" (aiReviewScan) — student อ่าน feedback ครบไหม
- `RepopulateDocumentLinesFromScanAsync` :5304 / `ModifyExtractedLineAsync` :5721 — มีสำเนา reconciliation ชุดที่สองไหม (grep `grossSum` พบที่เดียว :4981 ⇒ น่าจะไม่มี แต่ยังไม่อ่านตัวเมธอด)
- `AzureDiPatternLearner` / `OcrLearnedPatterns` ฝั่งอ่าน `ApplyLearnedPatternsTo` (:6808) — ทำงานจริงบน Azure path หรือถูก `data` ที่เต็มแล้วข้ามหมด
- `PdfTextLayerExtractor.HasUsableText` เกณฑ์ (ผลต่อ floor 0.90 ใน T2-06)
- UI "กดกี่ครั้ง/กรอกอะไรเอง" — ต้องรัน harness DOM ของ document-scan.html (4,316 บรรทัด) ไม่ทันในรอบนี้
- ocr-service `learning.py` / `training_data` และ `EXTRACTION_PROMPT` ของ Ollama (ai_engine.py :10–68) — ขอ items/VAT-incl ไหม
