# ทีม T5 — คุณภาพ · การพิสูจน์ · ตัวชี้วัด (OCR → Document pipeline) — รอบตรวจซ้ำ 2026-09-06

> ทีม: test architect + numerical-invariants specialist + SRE/observability + performance engineer
> repo `/home/user/Accounting` · **working tree ปัจจุบัน (มีการแก้ที่ main agent ทำแล้วแต่ยังไม่ commit)**
> รอบนี้เป็นการตรวจ **ทับของเดิม** (รายงาน T5 รอบ 2026-09-05 ถูกเขียนทับตามคำสั่ง) — เนื้อหาที่ยัง
> เป็นจริงถูกยกมาพร้อม**สถานะใหม่**หลังการแก้ ส่วนที่ถูกแก้แล้วย้ายไป §0 "ตรวจการแก้ของ main agent"
> กติกา: เปิดไฟล์จริงทุกข้อ (file:line) · ห้ามแก้โค้ด · ยืนยัน/หักล้าง T1–T3 ด้วยหลักฐานของตัวเอง

## สรุปหัวเรื่อง
- ✅ **แก้ถูก 6**: `Failed` ไม่ถูกทับ · `AmountTripleExtractor` ไม่ทับยอด engine (+ครอบใบ 0%) · ตรรกะ `OcrLineReconciler` (เลขเคส A/สมดุล/เครื่องหมาย ถูกหมด) · §82/5 บนเส้น JE · ฐาน WHT = total−VAT ทั้งสองเส้น · ผัง 11610/21911/21916/21917 ตรงเทมเพลตจริง
- 🔴 **P0 ใหม่ (T5-N1)**: อัปไฟล์เดิมซ้ำ (cache hit) **ทิ้งธง `[VAT-CLAIM]`** ⇒ เคลมภาษีซื้อต้องห้ามได้ทั้งเส้นเอกสารและเส้น JE
- 🔴 **การแก้ที่ยังพัง (T5-N2)**: regex เลขที่เอกสาร ยังจับที่อยู่ — `เลขที่ 123/45 ถนน…` ⇒ ได้ `123/4` (พิสูจน์ 7 เคส)
- 🔴 **บั๊กใหม่ในตัวการแก้ (T5-N4)**: reconcile เคสส่วนลดแปลงเป็น % 2 ตำแหน่ง ⇒ Σ บรรทัดคลาดได้ถึง **33 บาท** และตัวยัดเศษยอมแพ้เงียบ ⇒ **อนุมัติแล้วเด้ง "ไม่สมดุล"**
- 🔴 **T5-N3**: JE ฝั่งผู้ขายคำนวณ WHT แล้ว **ทิ้งทั้งก้อน** · **T5-N8**: เหตุผล `[APPROVE-SKIP]` ไม่ถูกบันทึกลงฐาน

---

## §0 ตรวจการแก้ของ main agent (งานสำคัญที่สุดของรอบนี้)

สรุป: **แก้ถูก 6 · แก้ไม่ครบ/ยังพัง 3 · เกิดผลข้างเคียงใหม่ 1**

| การแก้ที่แจ้งมา | ผลตรวจ | หลักฐาน |
| --- | --- | --- |
| `Failed` ไม่ถูกทับเป็น `Completed` | ✅ **ถูกต้อง** | `OcrService.cs:629–630` `if (scanResult.ScanStatus != "Failed") scanResult.ScanStatus = "Completed";` · เส้น cache อ่านเฉพาะ `ScanStatus == "Completed"` (:163) จึงไม่คัดลอกแถวล้มเหลว · refund `OcrController.cs:202` ทำงานได้จริงแล้ว |
| `AmountTripleExtractor` ไม่ทับยอดที่ engine อ่าน | ✅ **ถูกต้องและครอบเคส 0%** | `SmartFieldExtractor.cs:795–812` `agreesWithEngineTotal` + เพิ่มเคส `v == 0 && |s−t| < 1` ที่ :786–790 (ใบส่งออก/ยกเว้นไม่ถูกแต่ง VAT 7/107 ทับอีก) · มี ReasoningTrace บอกว่าไม่ทับ (ดัง — ดีกว่าเงียบ) |
| line reconciliation → `Helpers/OcrLineReconciler` | ✅ **เลขถูก** (ตรวจทีละเคส ด้านล่าง) แต่ **ดังไม่ถึงผู้ใช้** | ดู T5-N1 |
| JE จากสแกน: บล็อก §82/5 · WHT ฐาน total−VAT · 11610/21916/21917 | ✅ **ถูกต้อง** (ผังตรงเทมเพลตจริง) | ดู "ตรวจเลข JE" ด้านล่าง — แต่มีรูฝั่ง**ผู้ขาย** (T5-N3) |
| `OcrController` บังคับสิทธิ์ create/approve + ข้าม auto-approve เมื่อวันที่ไม่รู้ | ✅ มีจริง `:301` `CanCreateAsync` · `:329` `CanApproveAsync` · `:336` `[APPROVE-SKIP]` | ผลข้างเคียง: UI ไม่รู้จักแท็กใหม่ (ยืนยัน T4-01/T4-03) |
| student answer ถูกใช้จริง (`HasModelAnswer`) | ✅ มีใน diff `OcrAiAugmenter.cs` | ยังไม่มีเทสต์ (ดู §1) |
| `AzureDiPatternLearner` เรียนเฉพาะ ≥0.85 | ✅ มีใน diff | ยังไม่มีเทสต์ |
| regex เลขที่เอกสารไม่จับที่อยู่ | ❌ **ยังพังในเคสที่พบบ่อยที่สุด** — พิสูจน์ด้วย simulation | **T5-N2** |
| feedback ถูกบันทึกในเส้น create | ✅ `OcrService.cs:5205–5210` แนบ `GlAccountAiFeedbackId` ลงบรรทัดแรก + บรรทัดสรุป (:5283) | |

### ตรวจเลขของ `OcrLineReconciler` ทีละเคส (pure — ตรวจได้ด้วยตาและเลข)
- ลำดับด่าน `Classify` (`OcrLineReconciler.cs:70–120`): NoHeader → **B** (`|gross−sub|≤1`) → **C** (`|gross−disc−sub|≤1`) → **A** (`vat>0 && |gross−total|≤1`) → เส้น sub≤0 → **D** (`gross<sub−1`) → Ambiguous
- **สมดุลของเคส A** ✅: ผู้เรียกเขียน `Amount = round(amount − lineVat, 2)` (`OcrService.cs:5245`) ⇒ Σ Amount = grossSum − headerVat ≈ total − vat = sub · ตรงกับ `Document.SubTotal` ที่คิดจาก :4785–4792 — สมการที่เคยทำให้ Dr ≠ Cr เท่ายอด VAT (IKEA 1,396) ปิดแล้วจริง
- **สมดุลของเคส C** ✅ ในกรณีปกติ: หลังกระจาย % ผู้เรียกยัดเศษบรรทัดสุดท้ายให้ Σ = `TargetLineSum` เป๊ะ (`:5090–5095`) และ `DiscountAmount = qty×price − Amount` (:5225) จึงสอดคล้องกันเอง
- **เครื่องหมาย `UnreconciledGap`** ✅ สม่ำเสมอทั้ง 4 ทางออก (บวก = กระดาษมากกว่าบรรทัด)
- **การถอด sub เมื่อกระดาษไม่พิมพ์ยอดก่อน VAT** ✅ อยู่ที่ผู้เรียก (`:5073–5074 netSubForRecon = hdrTotal − hdrVatHdr`) — ถ้าไม่มีบรรทัดนี้ ใบที่ OCR อ่าน sub ไม่ได้จะถูกตีเป็น "บรรทัดขาดเท่ายอด VAT" ทุกใบ **แต่ตรรกะนี้อยู่นอกคลาส** ⇒ ผู้เรียกรายที่สองจะพลาด (ดู T5-N1c)

---

## §1 Findings ใหม่ของรอบนี้ — ข้อบกพร่อง**ในตัวการแก้เอง** (ค่าสูงสุดของรายงานนี้)

### T5-N1 🔴 [P0][S] เส้น cache "ไฟล์ซ้ำ" **ทิ้งธง §82/5 `[VAT-CLAIM]` ทั้งหมด** ⇒ อัปไฟล์เดิมซ้ำ = เคลมภาษีซื้อต้องห้ามได้
- ไฟล์: `Helpers/OcrScanSnapshot.cs:74` (`nameof(OcrScanResult.ProcessingNotes)` อยู่ใน **deny-list** — ไม่คัดลอก) →
  `OcrService.cs:207` `scanResult.ProcessingNotes = $"Duplicate of scan {duplicateOf.Id} (engine: …)";` (**เขียนทับด้วยข้อความเดียว**) → `return` ที่ :210 **ก่อน** `ProhibitedInputVatScreener` ที่ :910–950
- ทำไมพัง (เป็นขั้น):
  1. คำตัดสิน §82/5 ของทั้งระบบเก็บอยู่ที่เดียว คือ **สตริงใน `ProcessingNotes`** — ไม่มีคอลัมน์ `InputVatClaimable` บน `OcrScanResult` (ไล่ field ทั้ง entity `Models/Entities/Intelligence.cs:115–245` = ไม่มี)
  2. ผู้อ่านธงนี้มี 5 จุด: ด่านฝั่งเอกสาร `OcrService.cs:4868` `var vatNotClaimable = (result.ProcessingNotes ?? "").Contains("[VAT-CLAIM]");` · ด่าน **JE ตรงที่เพิ่งเพิ่มใหม่** `:5547` · UI 3 จุด (`document-scan.html:2033/4140/4239`)
  3. อัปโหลดไฟล์เดิมซ้ำ (hash ตรง) → เดินเส้น cache → ProcessingNotes เหลือ `"Duplicate of scan …"` → **ทั้ง 5 จุดอ่านได้ "เคลมได้"**
- ผลกระทบ: ใบกำกับ**อย่างย่อ** (§82/5(2)) · ค่ารับรอง (§82/5(4)) · น้ำมันรถยนต์นั่ง (§82/5(6)) ที่อัปซ้ำ → VAT เข้า 11610 → **ยื่น ภ.พ.30 เกินสิทธิ์** · ผู้ใช้ไม่เห็น banner เตือนด้วย (UI อ่านช่องเดียวกัน) · เส้นนี้เดินบ่อยโดยตั้งใจ — cache มีไว้ให้อัปซ้ำ และ partner integration ยิงซ้ำเป็นปกติ
- defect class: **"ค่าที่ตัดสินเรื่องกฎหมายเก็บเป็นสตริงในช่องข้อความ"** (ญาติของ T5-N7/`ProcessingNotes` text-ledger) + "ด่านที่ครอบแค่ทางเดียว"
- ทางแก้ (S): เก็บผลของ screener ลง **ช่องของตัวเอง** (`InputVatClaimable bool?` + `InputVatBlockReason`, `ADD COLUMN IF NOT EXISTS`) ให้ `OcrScanSnapshot` คัดลอกตามอัตโนมัติ (deny-list ไม่มีชื่อมัน) แล้วให้ 5 จุดอ่านช่องนั้นแทนการ `Contains` · แก้เฉพาะหน้าแบบ S ที่สุด: บนเส้น cache ให้ **ต่อ**ข้อความแทนการทับ และคัดลอกเฉพาะบรรทัดที่ขึ้นต้นด้วย `[VAT-CLAIM]/[VAT-NOTE]/[TAX-INV-PENDING]` จากต้นฉบับ
- ความมั่นใจ: **สูง** (เปิดครบทั้ง deny-list · จุดเขียนทับ · จุดอ่านทั้ง 5)

### T5-N2 🔴 [P1][S] regex เลขที่เอกสาร **ยังจับที่อยู่อยู่** — negative lookahead วางหลัง quantifier ที่ backtrack ได้ (พิสูจน์ด้วย simulation)
- ไฟล์: `Ocr/SmartFieldExtractor.cs:884–895` (`notAddress` ต่อท้าย `([A-Za-z0-9][A-Za-z0-9\-/]{2,})`)
- ทำไมพัง: `{2,}` เป็น greedy **และถอยได้** — เจอ `เลขที่ 123/45 ถนนพระราม 4` เครื่องจับ `123/45` ก่อน แล้ว lookahead ไม่ผ่าน จึง **ถอยหนึ่งตัว** เป็น `123/4` ซึ่งตัวถัดไปคือ `5` (ไม่ใช่คำบอกที่อยู่) ⇒ negative lookahead **ผ่าน** ⇒ ได้เลขที่เอกสาร = `123/4`
- simulation (python, semantics เดียวกับ .NET สำหรับโครงสร้างนี้ — `<scratch>/docno.py`):

  | ข้อความบนกระดาษ | เลขที่จริงในใบ | ผลลัพธ์**หลังแก้** |
  | --- | --- | --- |
  | `เลขที่ 99/1 ถ.สุขุมวิท` + `เลขที่ INV-2569-0042` | INV-2569-0042 | ✅ INV-2569-0042 (รอดเพราะด่าน "ต้องมีเลข ≥3 ตัว" ตัด `99/` ทิ้ง) |
  | `เลขที่ 123/45 ถนนพระราม 4` + `เลขที่ INV-2569-0042` | INV-2569-0042 | ❌ **`123/4`** |
  | `เลขที่ 199/12 ถ.สุขุมวิท` + `เลขที่ TIV-680012` | TIV-680012 | ❌ **`199/1`** |
  | `เลขที่ 55/123 ซอยลาดพร้าว 15` + `เลขที่ PI-20260820-0005` | PI-20260820-0005 | ❌ **`55/12`** |
  | `No. 123/45 Sukhumvit Rd.` + `Invoice No. INV-77012` | INV-77012 | ❌ **`123/45`** |
- ข้อบกพร่องที่สอง (โครงสร้าง): ตัวเลือกภาษาอังกฤษ `Rd\b|Road\b|Soi\b|Moo\b|Street\b|St\.` **แทบไม่มีวันแมตช์** เพราะที่อยู่อังกฤษวางคำบอกถนน **ท้าย**ชื่อถนน (`123/45 Sukhumvit Rd.`) ส่วน lookahead ดูแค่ token ถัดจากตัวเลขทันที (ไทยวางไว้หน้า จึงแมตช์ได้) — คือเขียนไว้แต่ไม่ทำงาน
- ผลกระทบ: เลขที่ที่ผิดไหลเข้า `SupplierInvoiceNumber` → **รายงานภาษีซื้อ §87 ระบุเลขใบกำกับผิด** · เข้า `ComputeContentFingerprint` และด่านกันซ้ำ `FindDuplicateDocumentWarningAsync` ⇒ ด่านซ้ำไม่ทำงาน (เลขคนละตัวทุกใบ) · ที่ร้ายกว่า "ว่าง" คือ **มีค่าที่ดูสมเหตุสมผล** ⇒ ผู้ใช้กด "ยืนยัน" ผ่าน
- ทางแก้ (S, พิสูจน์แล้ว): ปิดท้ายกลุ่มก่อนด้วย `(?![A-Za-z0-9\-/])` แล้วค่อยตามด้วยด่านที่อยู่ที่มองทั้ง **บรรทัดที่เหลือ** ไม่ใช่ token เดียว:
  `([A-Za-z0-9][A-Za-z0-9\-/]{2,})(?![A-Za-z0-9\-/])(?![^\n]{0,40}(?:ถ\.|ถนน|หมู่|ม\.\s*\d|ซ\.|ซอย|ต\.|ตำบล|อ\.|อำเภอ|แขวง|เขต|จ\.|จังหวัด|Rd\b|Road\b|Soi\b|Moo\b|Street\b|St\.))`
  รันชุดเดิม: 7/7 เคสถูก (รวมภาษาอังกฤษ) · ผลข้างเคียงที่ยอมรับได้ 1 เคส — เลขที่เอกสารที่มีคำบอกที่อยู่อยู่ในบรรทัดเดียวกัน (`เลขที่ IV-2569-0042 ออกที่ ถ.สุขุมวิท`) จะถูกทิ้ง แต่ pattern แรก (ป้ายเฉพาะ) รับไว้ก่อนแล้ว
- ต้องมีเทสต์: `SmartFieldExtractorDocNumberTests` ด้วย 7 เคสข้างบน (ตอนนี้ **grep `DocumentNumber` ใน `Accounting.Tests/` = ไม่มีไฟล์ที่ทดสอบ extractor ตัวนี้เลย**)
- ความมั่นใจ: **สูง** (simulation reproduce ได้ทั้งก่อน/หลัง — negative test ผ่านตามกติกา CLAUDE.md)

### T5-N3 🔴 [P1][S] JE จากสแกน: ฝั่ง **ผู้ขาย** คำนวณ WHT แล้ว **ทิ้งทั้งก้อน** — ลูกหนี้เกินจริง + ไม่บันทึกภาษีถูกหัก
- ไฟล์: `OcrService.cs:5561–5566` (คำนวณ `wht` โดยไม่ดู `isSeller`) · `:5587–5591` resolve `whtAcc` เป็น **หนี้สิน** 21916/21917 · `:5599–5605` สาขา `if (isSeller)` **ไม่มีบรรทัด WHT เลย**
- ทำไมพัง: `if (request.PostWht && result.HasWht && result.WhtRate is > 0m)` ไม่ได้กัน `isSeller` ⇒ ผู้ใช้ที่เป็นผู้ขายติ๊ก "ลง WHT" ได้ ระบบคำนวณให้ แล้ว JE ที่ออกมาไม่มีผลของมัน — `Dr ลูกหนี้ = total` เต็มจำนวน ทั้งที่ลูกค้าจะโอนมาแค่ `total − wht`
- ผลกระทบ: (1) ลูกหนี้ค้างเท่ายอด WHT ตลอดไป (ไม่มีอะไรมาล้าง) (2) **ไม่บันทึก "ภาษีถูกหัก ณ ที่จ่าย" เป็นสินทรัพย์** ⇒ เครดิตภาษีหายจากการคำนวณ CIT ⇒ จ่ายภาษีเกิน (3) เงียบสนิท — JE สมดุลและตัวเลขดูปกติ
- ข้อสังเกตซ้อน: ผังมาตรฐาน**มีบัญชีที่ถูกต้องอยู่แล้ว** `ChartOfAccountTemplates.cs:59` `11910 ภาษีถูกหัก ณ ที่จ่าย (Asset)` — เป็นเคส "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้"
- ทางแก้: ฝั่งผู้ขาย → `Dr 11910 (wht)` + `Dr ลูกหนี้ (total − wht)` + `Cr รายได้/ภาษีขาย` และ resolve บัญชีเป็น `AccountType.Asset` codes `{"11910","119"}` · ถ้ายังไม่ทำ ต้อง **ปฏิเสธดัง ๆ** (`throw` พร้อมบอกทางไปต่อ) ไม่ใช่คำนวณแล้วทิ้ง
- ความมั่นใจ: **สูง** (อ่านทั้งสองสาขาครบ)

### T5-N4 🟠 [P1][S] `OcrLineReconciler` เคส C: Σ บรรทัด **ไม่ตรง**ยอดก่อน VAT ได้ถึงหลายสิบบาท และตัวยัดเศษยอมแพ้เงียบ ๆ
- ไฟล์: `Helpers/OcrLineReconciler.cs:85` (`pct = round(discount/gross×100, **2**)`) · ผู้เรียก `OcrService.cs:5085–5095` — ยัดเศษบรรทัดสุดท้าย **เฉพาะเมื่อ `|rem| ≤ Tolerance (1 บาท)`**
- ทำไมพัง: ส่วนลดถูกแปลงเป็น **เปอร์เซ็นต์ 2 ตำแหน่ง** ⇒ ความคลาดเคลื่อน ≤ `gross × 0.005%`; ยิ่งใบใหญ่ยิ่งเกิน 1 บาท ⇒ ตัวยัดเศษไม่ทำงาน ⇒ ไม่มีโน้ตด้วย (เคส C ได้ `UnreconciledGap = 0` จึงตกไปกิ่ง `[Σ]` ที่พูดเรื่องส่วนลดอย่างเดียว)
- simulation (decimal, half-up):

  | ใบ | ส่วนลดบนกระดาษ | % ที่ได้ | Σ บรรทัดหลังลด | ยอดก่อน VAT บนกระดาษ | ส่วนต่าง | ยัดเศษ? |
  | --- | --- | --- | --- | --- | --- | --- |
  | 1 บรรทัด 1,000 | 50 | 5.00 | 950.00 | 950 | 0.00 | ✅ |
  | 20 บรรทัด รวม 100,000 | 3,333 | 3.33 | 96,670.00 | 96,667 | **−3.00** | ❌ เงียบ |
  | 1 บรรทัด 250,000 | 7,777 | 3.11 | 242,225.00 | 242,223 | **−2.00** | ❌ เงียบ |
  | 40 บรรทัด รวม 480,000 | 12,345 | 2.57 | 467,664.00 | 467,655 | **−9.00** | ❌ เงียบ |
  | 1 บรรทัด 999,999 | 33,333 | 3.33 | 966,699.03 | 966,666 | **−33.03** | ❌ เงียบ |
- ผลกระทบ: `Document.SubTotal` (คิดจากหัวใบ `:4785–4792`) ≠ `Σ Line.Amount` ในใบเดียวกัน · เส้น JE ซื้อรวมเดบิตจากบรรทัด (`DocumentService.cs:12803` `Sum(l => l.Amount)` ต่อบัญชี) ขณะที่เครดิตเจ้าหนี้มาจากยอดหัว ⇒ **อนุมัติแล้วเด้ง "การบันทึกบัญชีไม่สมดุล"** โดยผู้ใช้ไม่มีทางรู้ว่าเลขไหนผิด (ญาติของบั๊ก IKEA ที่เพิ่งปิดไป) — *ต้องเช็คต่อ: เส้น JE ของ DocumentType ที่ OCR สร้างจริง ใช้ Σ บรรทัดหรือยอดหัวเป็นเดบิต*
- ทางแก้ (S): เก็บ **จำนวนเงินส่วนลดต่อบรรทัด** แทนการแปลงเป็น % (คำนวณ `d_i = round(discount × g_i / gross, 2)` แล้วยัดเศษบรรทัดสุดท้ายให้ Σ = discount เป๊ะ — สูตรเดียวกับที่ `SpreadHeaderVat` ใช้กับ VAT อยู่แล้ว) · ถ้าจำเป็นต้องคง `DiscountPercent` ไว้ให้ UI ก็คำนวณย้อนจากยอดจริง · และปลดเพดาน `≤ 1 บาท` ของตัวยัดเศษเป็น "ยัดเสมอเมื่ออยู่ในเคส C" — ตัวเลขเป้าหมายมาจากกระดาษ ไม่ใช่การเดา
- ความมั่นใจ: **สูง** ในเรื่องส่วนต่าง (simulation) · **กลาง** ในเรื่องผลปลายทาง (ยังไม่ไล่เส้น JE ของทุก DocumentType)

### T5-N5 🟠 [P1][S] `[Σ-GAP]` — สัญญาณสำคัญที่สุดของ reconciler ตัวใหม่ **ไม่มีใครแสดง**
- ไฟล์ (เขียน): `OcrService.cs:5100–5102` `result.ProcessingNotes += "\n[Σ-GAP] " + recon.Note;` · (อ่าน): `grep -rn "Σ-GAP" Accounting/wwwroot` = **0**
- ทำไมพัง: โน้ตถูกเขียนตอน **create-document** ซึ่งเป็นจังหวะที่ UI พาผู้ใช้ออกจากหน้าไป `documents.html` ทันที (ยืนยัน T4-01/T4-03) ⇒ ต่อให้หน้าอ่านแท็กนี้เป็น ก็ไม่มีใครกลับมาดู
- ผลกระทบ: เคส D (OCR อ่านบรรทัดขาด) และ Ambiguous — เดิม "แต่งบรรทัดผี" (ผิดแบบเงียบ) ตอนนี้ "ไม่แต่ง" (ถูก) แต่ผลลัพธ์ต่อผู้ใช้เท่ากัน: **ได้เอกสารที่บรรทัดไม่ครบโดยไม่มีใครบอก** — คือย้ายจาก "ตัวเลขผิด" เป็น "ตัวเลขขาด" ซึ่งดีกว่าแต่ยังไม่จบ
- ทางแก้: (ก) ย้ายการจำแนกไปทำ **ตอนสแกน** ด้วย (เรียก `Classify` ที่ `:5052` ของ ScanAsync ได้เลย — input มีครบ) เพื่อให้โน้ตอยู่บนการ์ดก่อนผู้ใช้กดยืนยัน (ข) นับ `UnreconciledGap != 0` เป็น **blocking reason** ของ auto-approve (ดู §4 D2) (ค) UI: `parseScanNotes` ตัวเดียวตามข้อเสนอ T4
- ความมั่นใจ: สูง

### T5-N6 🟠 [P2][S] WHT สองสูตรยัง **ปัดเศษคนละแบบ** ในไฟล์เดียวกัน — และมีสูตรที่สามที่ใช้ฐานผิด
- ไฟล์: เส้นเอกสาร `OcrService.cs:4796` `Math.Round(whtBase * whtRate / 100m, 2)` — **ไม่มี `MidpointRounding.AwayFromZero`** · เส้น JE `:5568` `Math.Round(whtBase * WhtRate / 100m, 2, MidpointRounding.AwayFromZero)` ✅ · สูตรที่สาม `Ocr/SmartFieldExtractor.cs:696` `Math.Round(data.SubTotal.Value * data.WhtRate.Value / 100m, 2)` — ฐานเป็น **SubTotal** (บนใบมีส่วนลดคือยอด**ก่อน**หักส่วนลด) และไม่มี AwayFromZero
- ทำไมพัง: ฐานที่ตกจุดกึ่งกลางเกิดจริง (เช่น 101.50 × 3% = 3.045) ⇒ เส้นเอกสารได้ **3.04** (banker's) เส้น JE ได้ **3.05** ⇒ ใบเดียวกันได้ยอดหักต่างกันตามปุ่มที่ผู้ใช้กด — เป็นอาการเดิมของ T1-03 ที่แก้ **ฐาน**ไปแล้วแต่ยังเหลือ **โหมดปัดเศษ**
- ผลกระทบ: 50 ทวิ กับ ภ.ง.ด.3/53 ต่างกัน 0.01 บาท · ตรงกับกฎเหล็ก #4 E ("`Math.Round` ระบุ `AwayFromZero` เสมอ")
- ทางแก้: ใส่ `MidpointRounding.AwayFromZero` ที่ `:4796` · สูตรที่ `SmartFieldExtractor.cs:696` ใช้แค่เตือนใน ReasoningTrace (ไม่เขียนค่ากลับ — ตรวจแล้ว `:700–713` เขียนแค่ trace) จึงเป็น P3 แต่ควรยุบมาใช้ helper ตัวเดียว (`Helpers/OcrWhtBase.Compute(total, vat, rate)`) ให้ทั้ง 3 จุดเรียก
- ความมั่นใจ: สูง

### T5-N7 🟠 [P2][S] `ResolveTaxAccountAsync` — fallback ตามชื่อของ WHT จะเลือก **ภ.ง.ด.1 (เงินเดือน)**
- ไฟล์: `OcrService.cs:5663–5673` — เมื่อไม่พบรหัสในลิสต์ จะค้นชื่อ `Contains("หัก ณ ที่จ่าย")` แล้ว `OrderByDescending(AccountCode.Length).ThenBy(AccountCode)`
- ทำไมพัง: ผังมาตรฐานมี 21914–21918 ยาวเท่ากันหมด (`ChartOfAccountTemplates.cs:145–149`) ⇒ `ThenBy` ได้ **21914 = ภ.ง.ด.1 (เงินเดือน)** ซึ่งผิดประเภทสำหรับการจ่ายผู้ขาย
- ตรวจแล้ว**ถูก** 2 ตัว: `ภาษีซื้อ` → 11610 ชนะ (11610 < 11620/11630/11640) ✅ · `ภาษีขาย` → 21911 ✅ — จึงเป็นเฉพาะ WHT
- ผลกระทบจำกัด (P2): ต้องไม่มีทั้ง 21917 และ 21916 ในผังก่อน — เกิดเฉพาะผังที่ลูกค้าแก้เอง · แต่เมื่อเกิดจะไปโผล่ที่แบบยื่นผิดฉบับ
- ทางแก้: fallback ของ WHT ให้ใช้ keyword เจาะจง (`"ภ.ง.ด. 53"` / `"ภ.ง.ด. 3"`) หรือไม่ fallback เลยแล้ว throw พร้อมบอกให้เพิ่มผัง (ห้ามเดาบัญชีภาษี)

### T5-N8 🔴 [P1][S] เหตุผล `[APPROVE-SKIP]` / `[APPROVE-FAIL]` **ไม่ถูกบันทึกลงฐาน** — มีอยู่ใน HTTP response ครั้งเดียวแล้วหายถาวร
- ไฟล์: `OcrController.cs:334–338` และ `:348–352` — ทั้งสองกิ่งเขียนด้วย `result = result with { ProcessingNotes = … }` ซึ่งเป็นการทำสำเนา **record DTO** · ไม่มี `_db.SaveChangesAsync()` ในเมธอดนี้เลย (`grep SaveChanges` ในช่วง :285–357 = 0)
- ทำไมพัง: `result` มาจาก `CreateDocumentFromScanAsync` ที่ save ไปแล้วก่อนหน้า ⇒ การต่อสตริงตรงนี้แตะแค่ object ที่กำลังจะ serialize ⇒ เปิดสแกนใบเดิมอีกครั้ง (`GET /ocr/{id}`) จะ **ไม่เห็นเหตุผล** และหน้าเว็บก็ไม่ได้อ่านแท็กนี้อยู่แล้ว (ยืนยัน T4-01/T4-03: `grep APPROVE-SKIP` ใน `wwwroot` = 0)
- ผลกระทบ: ใบที่ระบบ "ตั้งใจไม่อนุมัติ" (วันที่อ่านไม่ออก / ผู้ใช้ไม่มีสิทธิ์) กลายเป็น **Draft ที่ไม่มีใครรู้ว่าทำไม** — ไม่มี worklist, ไม่มีป้ายบนการ์ด, ไม่มีร่องรอยใน DB ⇒ ตกจาก ภ.พ.30 ของงวดนั้นเงียบ ๆ · การแก้นี้จึงยัง **ไม่ปิดวงจร**: ด่านถูกต้องแล้ว แต่ "เสียงของด่าน" ดังแค่ 1 วินาทีในเบราว์เซอร์
- defect class: **"`LogWarning` แล้วเดินต่อ = ดังในที่ที่ไม่มีคนดู"** (รูปแบบใหม่: ดังใน response ที่ไม่มีใครอ่าน) + "ครึ่งเซิร์ฟเวอร์ของการแก้ ship ไปคนเดียว"
- ทางแก้ (S): เขียนลง `OcrScanResult.ProcessingNotes` จริง (โหลดแถวแล้ว `SaveChangesAsync`) **และ** ลง `Document.InternalNotes` ของใบที่เพิ่งสร้าง (ช่องภายใน ไม่พิมพ์บนกระดาษ — ตามบทเรียน `Notes` vs `InternalNotes`) เพื่อให้หน้าเอกสารบอกได้ว่าทำไมยังเป็นร่าง

---

## §2 สถานะของ finding รอบก่อน (T5-01…T5-18) หลังการแก้ — ตรวจซ้ำทุกข้อ

| ID เดิม | เรื่อง | สถานะวันนี้ | หลักฐานปัจจุบัน |
| --- | --- | --- | --- |
| T5-01 | `Failed` ถูกทับเป็น `Completed` | ✅ **ปิดแล้ว** | `OcrService.cs:629` |
| T5-02 | `CreateDocumentFromScanAsync` 0 เทสต์ | 🟠 **ดีขึ้น ยังไม่ปิด** — Case A–D ย้ายออกแล้วมี 9 เทสต์ (`OcrLineReconcilerTests`) แต่ WHT prefill · Σ gate · credit-account pick · fingerprint · duplicate gate ยัง inline 0 เทสต์ | `grep CreateDocumentFromScanAsync Accounting.Tests/` = 0 |
| T5-03 | ไม่มี invariant ตัวเลขไหน **ระงับ auto-create** | 🔴 **ยังเปิด** | `[Σ-GAP]` เขียนเป็นข้อความอย่างเดียว (`:5100`) · gateway ยังปรับแค่ confidence · ด่านใหม่ที่เพิ่มเป็นด่าน **สิทธิ์/วันที่** ไม่ใช่ด่าน **ตัวเลข** |
| T5-04 | `DeleteScanAsync` ไม่ตรวจ `CreatedJournalEntryId` | 🔴 **ยังเปิด** | `:6501–6560` — ด่านมีเฉพาะ `CreatedDocumentId` (:6515) · ไม่มีบรรทัดใดอ่าน `CreatedJournalEntryId` (grep ทั้งเมธอด = 0) · ยัง `File.Delete` + `Remove(file)` (:6553) และ `catch { }` กลืน error การลบไฟล์ |
| T5-05 | `Math.Round` ขาด `AwayFromZero` | 🟠 **ยังเปิดที่จุดสำคัญที่สุด** | `:4796` (WHT ของเส้นเอกสาร) — ดู T5-N6 |
| T5-06 | แถว `Processing` ค้าง + โควตาไม่คืน | 🔴 **ยังเปิด** | `grep "Processing" OcrSelfCorrectionService.cs BackgroundJobService.cs` = 0 ⇒ ไม่มี sweep · refund ยังอยู่แต่ใน `OcrController.cs:193/202` (ต้องมี response กลับมาก่อน) |
| T5-07 | อัปโหลด synchronous > 6 นาที | 🔴 **ยังเปิด** | ไม่มีการเปลี่ยนเป็น 202+poll ใน diff |
| T5-08 | create-document ไม่ idempotent | 🟠 **ยังเปิด** | ไม่มี advisory lock ใน `CreateDocumentFromScanAsync` (grep `AdvisoryLockKey` ในไฟล์ = 0) |
| T5-09 | Σ VAT รายบรรทัด ≠ หัวใบ → log อย่างเดียว | 🟠 **ยังเปิด** | `:5140–5147` ยังเป็น `_logger.LogWarning` ล้วน — เทียบกับ `[Σ-GAP]` ที่อยู่ห่างกัน 40 บรรทัดและเขียนลง ProcessingNotes แล้ว = **สองมาตรฐานในเมธอดเดียว** |
| T5-10 | `admin/ocr-accuracy` วัดด้วย `UpdatedAt != null` | 🔴 **ยังเปิด** | `OcrSelfCorrectionService.cs:50–52` `CountAsync(... r.UpdatedAt != null)` · เพิ่มเติมที่เพิ่งเห็น: เป็น **N+1 ต่อบริษัท** (3 query × จำนวนบริษัททั้งระบบ ในลูป `:46–67`) |
| T5-11 | maintenance ลบไฟล์ต้นฉบับหลัง 30 วัน | 🔴 ยังเปิด | ไม่มีการเปลี่ยนใน diff |
| T5-12 | PDPA/retention ของ `OcrScanResult` | 🔴 ยังเปิด | `grep RetentionUntil Models/Entities/Intelligence.cs` = 0 |
| T5-13 | pure class เสี่ยงสูงไม่มีเทสต์ | 🟠 **ดีขึ้นบางส่วน** — `AmountTripleExtractor` ถูกแก้แล้วแต่ **ยังไม่มีเทสต์** (`grep AmountTriple Accounting.Tests/` = 0) · `EtaxPdfXmlExtractor`/`VendorIntelligenceService`/`ProductMatcher`/`ScanQualityGrader` ยัง 0 เทสต์ | |
| T5-14 | contact match โหลด Contacts ทั้งบริษัท | 🟠 ยังเปิด | |
| T5-15 | `_submitGate` static ต่อ process | 🟠 ยังเปิด | |
| T5-16 | `ProcessingNotes` เป็น text ledger | 🔴 **แย่ลง** — รอบนี้เพิ่มแท็กใหม่อีก 5 ตัว (`[Σ-GAP]`/`[Σ]`/`[APPROVE-SKIP]`/`[APPROVE-FAIL]`/`[DATE-UNKNOWN]`) และ **T5-N1 พิสูจน์ว่าการเก็บกติกากฎหมายไว้ในสตริงนี้ทำให้ธงหายจริง** | |
| T5-17 | ชื่อไฟล์เทสต์ทำให้ประเมิน coverage ผิด | 🟠 **แย่ลงเล็กน้อย** — ตอนนี้มี **ทั้ง** `OcrLineReconcileTests.cs` (ทดสอบ `SanitizeVatSplitArtifacts`) และ `OcrLineReconcilerTests.cs` (ทดสอบ `OcrLineReconciler`) ต่างกันตัวอักษรเดียว ⇒ คนถัดไปจะเปิดผิดไฟล์แน่นอน | `ls Accounting.Tests/ \| grep Reconcil` |
| T5-18 | fingerprint รวมวันที่ · หน้าต่าง 90 วัน | 🟠 ยังเปิด (P3) | |

---

## §3 Test coverage map (สถานะปัจจุบัน)

### 3.1 ไฟล์เทสต์ที่แตะเส้น OCR — 12 ไฟล์ / **118 เคส** (จาก 124 ไฟล์เทสต์ทั้งเรพ)
| ไฟล์ | ทดสอบคลาส | เคส | ครอบขั้นไหน |
| --- | --- | --- | --- |
| **`OcrLineReconcilerTests` (ใหม่)** | `Helpers/OcrLineReconciler` | **9** | Case A–D + Ambiguous + บิลไม่มี VAT ✅ |
| `OcrLineReconcileTests` (ชื่อคล้ายมาก) | `OcrService.SanitizeVatSplitArtifacts` | 8 | qty×price ≠ amount รายบรรทัด |
| `OcrAmountTriangleTests` | `OcrConfidenceGateway.Validate` | 8 | สามช่อง Sub/VAT/Total ขัดกัน |
| `OcrLineSplitGuardTests` | `Helpers/OcrLineSplitGuard` | 11 | ด่านผล AI แตกบรรทัด |
| `OcrScanSnapshotTests` | `Helpers/OcrScanSnapshot` | 6 | คัดลอกแถว cache (reflection ทุกช่อง) |
| `OcrDocumentRoleInferrerTests` | `OcrDocumentRoleInferrer` | 15 | ผู้ซื้อ/ผู้ขาย → TargetDocumentType |
| `OcrScanComplianceEvaluatorTests` | evaluator + extractor | 10 | คำเตือนบนการ์ด |
| `RdComplianceOcrNoiseTests` | validator + extractor | 13 | EAN-13 vs เลขภาษี |
| `ProhibitedInputVatScreenerTests` | screener §82/5 | 8 | keyword |
| `BranchCodeExtractorTests` | `BranchCodeExtractor` | 9 | รหัสสาขา |
| `ThaiVatTypeRuleTests` | `ThaiVatTypeRule` | 15 | อัตรารายบรรทัด + `SpreadHeaderVat` |
| `WhtCertAmountResolverTests` | ยอด 50 ทวิ | 6 | (เรียกจาก DocumentService) |
> `GatewayReconciliationTests` = **payment gateway** ไม่เกี่ยว OCR (ชื่อชวนเข้าใจผิด — T5-17)

### 3.2 ราย stage — ✅ pure+เทสต์ · ⚪ pure ไม่มีเทสต์ · ❌ inline ในเมธอด 2,270 บรรทัด
| ขั้น (บรรทัดปัจจุบัน) | สถานะ | หมายเหตุ |
| --- | --- | --- |
| cache/duplicate :150–210 | ✅ (snapshot) แต่ **ตรรกะเขียนทับ ProcessingNotes ไม่มีเทสต์** | T5-N1 หลุดมาได้เพราะช่องว่างนี้ |
| Tier 0 e-Tax XML / `EtaxPdfXmlExtractor` (524 บรรทัด) | ⚪ 0 เทสต์ | |
| Tier 1 Azure map `MapAzureDiToExtractedDataAsync` | ❌ | |
| Tier 2 python HTTP | ❌ | |
| Tier 3 `ParseThaiDocument` | ❌ | |
| `SmartFieldExtractor.Enrich` (931 บรรทัด) | ⚪ **บางส่วน** (ผ่าน RdComplianceOcrNoiseTests เฉพาะเลขภาษี) — **`TryExtractDocumentNumber` 0 เทสต์** | นี่คือช่องที่ T5-N2 หลุด |
| `AmountTripleExtractor` (เพิ่งแก้) | ⚪ **0 เทสต์** | แก้แล้วไม่มีอะไรล็อกไว้ |
| status machine Failed/Completed (เพิ่งแก้) | ❌ 0 เทสต์ | เคสถอยกลับได้ทุกเมื่อ |
| `OcrConfidenceGateway` | ✅ (triangle) / ⚪ (rule 7,8) | |
| `OcrDocumentRoleInferrer` · `ProhibitedInputVatScreener` | ✅ | |
| `VendorIntelligenceService` (966) · `ProductMatcher` · `ScanQualityGrader` · `FieldPatternLibrary` | ⚪ 0 เทสต์ | |
| credit account 3-tier :1516→ปัจจุบัน | ❌ | |
| `ComputeContentFingerprint` (private) | ❌ | |
| **`CreateDocumentFromScanAsync`** (~830 บรรทัด) | ❌ ยกเว้นส่วนที่ย้ายออกแล้ว | `grep` ใน Tests = 0 |
| ├ header amounts + WHT :4780–4802 | ❌ **สูตรเงิน 0 เทสต์** | T5-N6 อยู่ตรงนี้ |
| ├ line reconcile :5076 | ✅ (ตัวจำแนก) / ❌ (การใช้ผล: ยัดเศษ · เพดาน 1 บาท) | T5-N4 อยู่ใน "การใช้ผล" |
| ├ Σ VAT gate :5140 | ❌ | |
| └ 50 ทวิ | ❌ | |
| `CreateJournalEntryFromScanAsync` :5527 (เพิ่งแก้ 3 เรื่อง) | ❌ **0 เทสต์** | T5-N3 อยู่ตรงนี้ |

### 3.3 Top-10 pure-class extraction ที่เสนอ (ปรับตามของที่ทำไปแล้ว)
| # | ชื่อ | ย้ายจาก | input → output | ขนาด |
| --- | --- | --- | --- | --- |
| 1 | ~~`OcrLineReconciler`~~ | — | **ทำแล้ว ✅** | — |
| 2 | `Helpers/OcrHeaderAmounts.Resolve(sub, vat, total, discount, whtRate)` → `(SubTotal, WhtBase, WhtAmount, NetTotal, SubTies)` | `:4780–4802` | ตัวเลขล้วน | S |
| 3 | `Helpers/OcrDiscountSpread.Allocate(lineGross[], discount)` → `decimal[]` (จำนวนเงิน ไม่ใช่ %) | `:5085–5095` | ปิด T5-N4 | S |
| 4 | `Helpers/OcrWhtBase.Compute(total, vat, rate)` (AwayFromZero ที่เดียว) | `:4794–4796` · `:5567–5568` · `SmartFieldExtractor:696` | ปิด T5-N6 | S |
| 5 | `Helpers/OcrDocNumberExtractor.Extract(rawText)` | `SmartFieldExtractor:860–908` | ปิด T5-N2 + เทสต์ได้ | S |
| 6 | `Helpers/OcrScanFlags` (แทน `ProcessingNotes.Contains`) | 5 จุดอ่าน + ~10 จุดเขียน | ปิด T5-N1/T5-16 | M |
| 7 | `Helpers/CreditAccountPicker.Pick(targetType, coaCodes, override, default)` | `:1516–1580` | | S |
| 8 | `Ocr/AzureDiFieldMapper.Map(AzureDiResult)` → `OcrExtractedData` | `MapAzureDiToExtractedDataAsync` | ไม่มี DB | M |
| 9 | `Ocr/ScanOutcomeLedger` (status machine + tier ledger แบบ structured) | `:290–630`, `:2396` | | S |
| 10 | `Ocr/PostingReadiness.Evaluate(...)` (ของใหม่ — §6 D2) | — | | M |
> ฝ่ายค้าน: "เมธอดยาวเพราะต้องอ่าน DB ตลอด แยกไม่ได้" — คำตอบ: ข้อ 2–5 ไม่มี `await` แม้แต่ตัวเดียวในช่วงที่ย้าย · และ **การย้าย `OcrLineReconciler` ออกไปในรอบนี้ทำให้บั๊ก T5-N4 มองเห็นได้ด้วยตาราง 5 แถว** ซึ่งตอนอยู่ใน 2,270 บรรทัดไม่มีใครเห็นมาเป็นปี

---

## §4 Invariant matrix (สถานะหลังการแก้)
ระดับ: **BLOCK** = throw/ระงับ · **NOTE** = เขียนลง ProcessingNotes (คงอยู่ในฐาน) · **UI-only** = อยู่ใน response ครั้งเดียว · **LOG** = `_logger` เท่านั้น · **—** = ไม่มี

| # | Invariant | บังคับที่ไหน (file:line) | ระดับ | ช่องโหว่ |
| --- | --- | --- | --- | --- |
| I1 | Σ บรรทัด = ยอดก่อน VAT | `OcrLineReconciler.Classify` → `OcrService.cs:5098–5102` | **NOTE** (ดีขึ้นจาก "แต่งบรรทัดผี") | ไม่มีใครแสดง (T5-N5) · ไม่ระงับ auto-create · เคส C ยังคลาด (T5-N4) |
| I2 | subtotal + VAT = total | `OcrConfidenceGateway` → ปรับ `Confidence` เท่านั้น · `subTies` `:4790–4792` | **—** (ลดคะแนน) | ไม่มีจุดไหน BLOCK — T5-03 |
| I3 | VAT รายบรรทัด = อัตรา × ฐาน | `ThaiVatTypeRule.Suggest` + `SpreadHeaderVat` (AwayFromZero ✅) · ด่าน Σ `:5140–5147` | **LOG** | `LogWarning` ล้วน ทั้งที่ `[Σ-GAP]` ห่างไป 40 บรรทัดเขียนลง NOTE — สองมาตรฐาน (T5-09) |
| I4 | ฐาน WHT = ยอดก่อน VAT | `:4794` และ `:5567` ใช้ `total − vat` ✅ **เหมือนกันแล้ว** | auto | **โหมดปัดเศษต่างกัน** (T5-N6) |
| I5 | เลขผู้ซื้อ = บริษัทเรา | `RdComplianceValidator` TENANT_BUYER_MISMATCH → complianceIssues | **UI-only** | ไม่ block การสร้าง/อนุมัติ |
| I6 | เลขผู้ขาย ≠ เรา | role inferrer สลับบทบาทให้ | auto-fix | ✅ มีเทสต์ 15 เคส |
| I7 | วันที่ไม่แต่งขึ้น | `[DATE-UNKNOWN]` `:990–991` (**NOTE ✅ ใหม่**) + `OcrController:322` ห้าม auto-approve (**BLOCK ✅ ใหม่**) | **BLOCK** | เหตุผลไม่ถูก persist (T5-N8) · เอกสารยังได้ `docDate = today` |
| I8 | ไม่ลงงวดปิด | `AccountingService.CreateJournalEntryAsync:428–436` throw | **BLOCK** ✅ | |
| I9 | tax point §78/§78/1 | `grep TaxPoint` ใน OcrService = **0** | **—** | input คือ `docDate` ที่อาจเป็น "วันนี้" |
| I10 | ซ้ำ (ผู้ขาย+เลขที่+ยอด) | fingerprint + `FindDuplicateDocumentWarningAsync` → BusinessRuleException | **BLOCK** ✅ | เลขที่อาจเป็นที่อยู่ (T5-N2) ⇒ ด่านนี้พลาด · ไม่มี lock (T5-08) |
| I11 | ยอดกระดาษ − เอกสาร = 0 | **ไม่มี assertion ใดก่อน `SaveChanges`** | **—** | T5-N4 ทำให้ Σ บรรทัด ≠ SubTotal ได้จริง |
| I12 | สกุลเงินสอดคล้อง | `InferCurrency(RawText) ?? "THB"` · ไม่ตั้ง ExchangeRate | **—** | |
| I13 | มีบรรทัด > 0 | fallback บรรทัดสรุป `:5275–5290` | auto-fix | ไม่มีป้ายว่า "สังเคราะห์" |
| I14 | Dr = Cr | `AccountingService.cs:327` throw | **BLOCK** ✅ | T5-N4 อาจทำให้เด้งโดยผู้ใช้ไม่รู้สาเหตุ |
| I15 | §82/5 ห้ามเคลม | `:4868` (เอกสาร) · `:5547` (JE ใหม่ ✅) | **BLOCK** | **ธงหายบนเส้น cache — T5-N1** |

**สรุป: BLOCK จริง 5 (เพิ่มจาก 3) · NOTE 2 · UI-only 1 · LOG 1 · ไม่มีเลย 4**
ยังคงไม่มี invariant **เชิงตัวเลข** ตัวไหนระงับ auto-create (`:2342`/`:2354` = confidence + contact + criticalFields + !duplicate เท่านั้น · ระงับได้เฉพาะ FixedAsset `:2281` และ Handwriting `:2296`)

---

## §5 Observability & KPI

### 5.1 บันทึกอะไรต่อสแกน (ปัจจุบัน)
`Confidence` (ตัวเดียวทั้งใบ) · `FieldConfidenceJson` (ชื่อช่องกลางผ่าน `OcrFieldKeys.Canonicalize` `:998`) · `OcrEngine` ·
`ProcessingNotes` (text ledger — ตอนนี้มีแท็ก **≥ 12 ตัว**: `[Azure]/[Python]/[Tesseract]` · `[Reasoning]` · `[VAT-CLAIM]/[VAT-NOTE]` ·
`[Content Duplicate]` · `[FixedAsset]` · `[Handwriting]` · `[Recurring]` · `[Suggest]` · `[DATE-UNKNOWN]` · `[Σ]/[Σ-GAP]` ·
`[TAX-INV-PENDING]` · `[WHT-CERT]`) · ธง `GlAccountUsedAi/TargetDocTypeUsedAi/LineSplitUsedAi` + FeedbackId 4 ตัว ·
`IsDuplicate/DuplicateOfScanId` · `ContentFingerprint` · `RetryCount` · `ProcessedAt` · `CreatedDocumentId/CreatedJournalEntryId` ·
`RdComplianceIssuesJson` · `BuildQualityDto` → `ScanQualityGrader` (A–D + advice)

**ที่ยังไม่มี**: เวลาต่อ tier · จำนวนช่องที่ผู้ใช้แก้ · "ยืนยันโดยไม่แก้" · เวลาอัปโหลด→เอกสาร→อนุมัติ ·
เหตุผลที่ไม่อนุมัติ (T5-N8 ไม่ persist) · `UnreconciledGap` เป็นตัวเลข (มีแต่ข้อความ)

### 5.2 endpoint ที่มีอยู่
| endpoint | ให้อะไร | ใช้เป็น KPI ได้ไหม |
| --- | --- | --- |
| `GET admin/ocr-usage-summary` (`AdminController.cs:3025`) | totalScans · thisMonthScans · เครดิต | ปริมาณเท่านั้น |
| `GET admin/ocr-accuracy` → `OcrSelfCorrectionService.ComputeAccuracyAsync:34–67` | `CorrectedScans = COUNT(UpdatedAt != null)` | ❌ **ไม่มีความหมาย** (`UpdatedAt` ถูกเซ็ตทุก `SaveChanges` — รวม `CreatedDocumentId`/`RetryCount`) + เป็น **N+1 ต่อบริษัท** |
| `GET ai-admin/accuracy` (`AiAdminController.cs:335`) | ai/local accuracy ต่อ FeatureKey | ✅ (มี selection bias) |
| `GET admin/ai-usage/summary\|export` (`AiUsageReportController.cs:91–121`) | `ถึงAIจริง` · `localตอบเอง` · `อัตราใช้AI` รายวัน/ฟีเจอร์/บริษัท | ✅ **ตัวชี้วัดกฎเหล็ก #1 ข้อ 6 มีจริง** |

### 5.3 KPI ขั้นต่ำที่เสนอ — "มีข้อมูลแล้ว" vs "ต้องเพิ่ม"
| KPI | มีแล้ว? | ที่มา / ต้องเพิ่ม |
| --- | --- | --- |
| **First-pass accept rate** (สร้างเอกสารโดยไม่แก้ช่องใดเลย) | ❌ | เพิ่ม `AcceptedWithoutEdit bool` + `UserCorrectedFieldsJson` เขียนใน `SubmitCorrectionAsync` (diff before/after) |
| ช่องที่ถูกแก้บ่อยที่สุด (ต่อ engine, ต่อผู้ขาย) | ❌ | ช่องเดียวกัน group by `OcrEngine` / `ExtractedVendorTaxId` — **นี่คือ KPI ที่บอกว่าควรไปปรับปรุงตรงไหน** |
| อัตราการเรียก AI ที่ลดลง (กฎเหล็ก #1 ข้อ 6) | ✅ | `admin/ai-usage` |
| ความแม่นราย engine ต่อช่อง | ❌ | ต้องมี golden set (§6 D1) — feedback อย่างเดียววัดไม่ได้ |
| time-to-document / time-to-approve | ⚠️ query ได้เลย | `Document.CreatedAt/ApprovedAt − OcrScanResult.CreatedAt` (ไม่ต้องเพิ่มคอลัมน์) |
| tier fallback rate | ⚠️ | อยู่ใน `ProcessingNotes` เป็นข้อความ → ควรเป็น `TierLedgerJson` |
| Stuck `Processing` / `Failed` | ✅ **ใช้ได้แล้ว** | หลังแก้ T5-01 ตัวเลข Failed เชื่อถือได้ (ก่อนหน้านี้ = 0 เสมอ) |
| **อัตราที่ auto-approve ถูกข้าม + เหตุผล** | ❌ | ต้องแก้ T5-N8 ก่อน (ตอนนี้เหตุผลไม่ลงฐาน) |
| Σ-GAP rate (บรรทัดไม่ครบ) | ⚠️ regex บนข้อความ | ควรเป็น `UnreconciledGap decimal?` |

---

## §6 Performance & resilience (นับจากโค้ดปัจจุบัน)
- **DB round-trip ใน `ScanAsync` (:124–2400)**: `await _db.` / `ToListAsync` / `FirstOrDefaultAsync` / `AnyAsync` / `CountAsync` = **46 จุด** + `SaveChangesAsync` **5 ครั้ง** (:191 · :208 · :1994 · :2061 · :2396) + helper ที่ยิง DB เองอีก ≥ 20 ตัว ⇒ **≥ 70 query ต่อสแกน**
- **ช่องว่างระหว่าง save ที่ 2 (:208) กับที่ 3 (:1994) ≈ 1,790 บรรทัด** ซึ่งรวม I/O ภายนอกทั้งหมด ⇒ pod restart ระหว่างนั้น = แถวค้าง `Processing` ตลอดกาล + โควตาไม่คืน (**T5-06 ยังเปิด · ไม่มี sweep**)
- **Timeout ที่วัดได้**: Azure 2 นาที (`AzureDocumentIntelligenceService.cs:68, :224`) + backoff 429 · python 120 วิ (`OcrService.cs:3380`) · AI doc-type 15 วิ (:841) · GL 15 วิ (:1385) · vendor 10 วิ (:1901) · line-split 20 วิ (:6984) · DBD 15 วิ (`DbdLookupService.cs:97`)
  ⇒ **worst case ของ 1 HTTP request ≈ 2 + 2 + Tesseract + 1 นาที > 5–6 นาที** ทั้งหมด synchronous · `Program.cs` ไม่มี request timeout (**T5-07 ยังเปิด**)
- **N+1**: `ComputeAccuracyAsync:46–67` (3 query × ทุกบริษัท) · contact match ฝั่งขายโหลด `Contacts` ทั้งบริษัท · credit-prefix loop
- **Multi-instance**: `_submitGate` static ต่อ process (เพดาน Azure คูณจำนวน instance) · maintenance job ไม่มี `JobLock`
- **Idempotency**: `CreateDocumentFromScanAsync` อ่านธง `CreatedDocumentId` ต้นเมธอด → เขียนท้ายเมธอด ห่างกัน ≥ 10 query **ไม่มี advisory lock** (`grep AdvisoryLockKey` ในไฟล์ = 0) ⇒ double-click = 2 Draft

---

## §7 Data lifecycle / PDPA
- `OcrScanResult` **ไม่มี `RetentionUntil`** (grep ใน `Models/Entities/Intelligence.cs` = 0) · `PdpaService` ไม่แตะ `OcrScanResult`/`RawTextContent` ⇒ **DSR access/erase ตอบไม่ครบ** (ข้อความทั้งหน้ากระดาษมีชื่อ/ที่อยู่/เลขภาษีบุคคลธรรมดา)
- `DeleteScanAsync:6501` — hard-delete แถว + `File.Delete` ไฟล์ต้นฉบับ · ด่านมีเฉพาะ `CreatedDocumentId` · **ไม่ตรวจ `CreatedJournalEntryId`** ⇒ ลบหลักฐานประกอบ JE ได้ (พ.ร.บ.การบัญชี ม.10 · §65 ตรี(9)) · `catch { }` กลืน error ตอนลบไฟล์ (:6553) แล้วลบแถว DB ต่อ
- maintenance ลบ **ไฟล์ต้นฉบับ** ของสแกน "Completed แต่ยังไม่สร้างเอกสาร" หลัง 30 วัน โดยคงแถวไว้ (FK ชี้ไฟล์ที่หายแล้ว) — ขัดกับหน้าต่างเคลม §82/3 **6 เดือน**
- แถว `Processing` ค้าง ไม่มีใครเก็บ

---

## §8 🔵 ข้อเสนอออกแบบ (มี "ฝ่ายค้านจะว่าอย่างไร + คำตอบ" ทุกข้อ)

### T5-D1 🔵 [M] Evaluation harness — golden set + replay ราย stage + regression gate
**รูปแบบ** `Accounting.Tests/Golden/ocr/<case-id>/`:
- `engine.json` — ผลดิบของ engine ที่บันทึกไว้ (Azure DI JSON · python JSON · ข้อความ Tesseract) — **ไม่เก็บรูป** (PDPA + ขนาดเรพ) ผ่าน anonymiser ที่แทนเลขภาษี/ชื่อ/ที่อยู่ด้วยค่าสังเคราะห์ที่ **คง checksum mod-11 และรูปทรงเดิม** (ไม่งั้นเทสต์ EAN-13 vs เลขภาษีจะไม่มีความหมาย)
- `expected.document.json` — `Document` + `Lines` + WHT + `TargetDocumentType` + `Contact` ที่นักบัญชียืนยัน (ที่มาที่ดีที่สุด: เอกสารที่ **Approved จริง** ซึ่งเกิดจากสแกน)
- `expected.flags.json` — `[VAT-CLAIM]` · `Σ-GAP` · `IsDuplicate` · complianceIssues ที่ควรขึ้น
- `meta.json` — **ชั้นของกระดาษ** (ค้าปลีกผสม 7%/ยกเว้น · บริการมี WHT · ส่งออก 0% · ใบลดหนี้ · 50 ทวิ · เล่มเขียนมือ · ใบที่มีที่อยู่ "เลขที่ 123/45") เพื่อรายงานความแม่น **ต่อชั้น** ไม่ใช่ค่าเฉลี่ยรวม
**Seam ที่ต้องมี 2 จุด**: (1) `IOcrEngineTier` ที่ inject `engine.json` แทน HTTP (2) แยกส่วน pure ของ `CreateDocumentFromScanAsync` ออก (extraction #2–#5 §3.3) ให้รันได้โดยไม่มี DB
**ตัวชี้วัด**: per-field exact/tolerance (ยอด ±0.01 · วันที่ · เลขที่ · เลขภาษี · สาขา · TargetDocumentType · GL รายบรรทัด · WHT) + "posting-equivalent" (Σ Dr/Cr ต่อบัญชีเท่ากัน) + **regression gate**: ห้ามลดลงแม้ 1 เคสในชั้นใด
- ฝ่ายค้าน: *"env นี้ไม่มี .NET SDK · ผลดิบ engine อยู่บนเครื่องลูกค้า · anonymise แล้วยังเป็น PII"* — คำตอบ: harness รันฝั่งผู้ใช้ด้วย `dotnet test` เหมือนเทสต์ทุกตัวในเรพนี้ (เราไม่ได้รันเทสต์อยู่แล้ว) · ผลดิบดึงจากสแกนของ **บริษัทตัวอย่าง/บริษัทของเจ้าของ** ที่ยินยอม · anonymiser เป็นโค้ดที่ทดสอบได้เอง และเก็บเฉพาะ `engine.json` (ข้อความ) ไม่เก็บรูป
- ทางเลือกที่ปฏิเสธ: วัดจาก `AiSuggestionFeedbacks` อย่างเดียว (คนแก้เฉพาะที่ผิดชัด ⇒ selection bias) · e2e ด้วยรูปจริงผ่าน engine (ไม่ deterministic · เสียเงิน · ช้า)
- **หลักฐานว่าคุ้ม**: บั๊ก 3 ตัวของรอบนี้ (T5-N1 · T5-N2 · T5-N4) ตรวจจับได้ทั้งหมดด้วย golden case ละ 1 ใบ

### T5-D2 🔵 [M] Posting-readiness score — ตัดสิน auto-approve จาก **invariant** ไม่ใช่ confidence ตัวเดียว
`Ocr/PostingReadiness.Evaluate(draft, scan, gateway, company)` → `{ Score, Blocking[], Review[], Info[] }`
- **Blocking (ห้าม auto-create/auto-approve)**: I2 ตัวเลขขัดกัน · I11 Σ บรรทัด ≠ SubTotal · `UnreconciledGap != 0` · I5 เลขผู้ซื้อไม่ใช่เรา · I7 `ExtractedDate == null` · I10 ซ้ำ · `[VAT-CLAIM]` ที่ยังเคลมอยู่
- **Review (สร้างได้ ห้ามอนุมัติ)**: field confidence < 0.85 บนช่อง §86/4 · GL จาก AI ที่ student ยังไม่ยืนยัน · สาขา `00000` ที่มาจาก default
- **Auto-approve**: Blocking = 0 **และ** Review = 0 **และ** ผู้ขายรายนี้มีประวัติ ≥ N ใบที่ผู้ใช้ไม่เคยแก้ (ต้องมี KPI §5.3 ก่อน)
- ฝ่ายค้าน: *"มีตัวตรวจ 3 ตัวแล้ว (`OcrConfidenceGateway` · `RdComplianceValidator` · `OcrScanComplianceEvaluator`) จะเอาตัวที่ 4 ทำไม"* — คำตอบ: ทั้ง 3 เป็น **ตัวตรวจที่ผลิตคำเตือน** ไม่มีตัวไหนเป็น **ตัวตัดสิน** · วันนี้ปลายทางของทั้งสามคือ "ลด `Confidence`" หรือ "ข้อความ" ⇒ §4 พิสูจน์แล้วว่า **ไม่มี invariant เชิงตัวเลขตัวไหนระงับ auto-create เลย** · D2 ไม่เพิ่มตัวตรวจ แต่เพิ่ม **ตัวรวมผลที่มีอำนาจสั่งหยุด**
- ทางเลือกที่ปฏิเสธ: ดัน `Ocr:AutoCreateThreshold` ให้สูงขึ้น — แก้ปลายเหตุ (ใบ 922.44/68/990 ได้ confidence 0.95 ทั้งที่ตัวเลขขัดกัน)

### T5-D3 🔵 [S] เลิกเก็บกติกากฎหมายในสตริง — `Helpers/OcrScanFlags`
`ScanFlagsJson` (structured: `{vatClaimable:false, reasons:["82/5(2)"], sigmaGap:-33.03, dateUnknown:true, approveSkip:"…"}`) แล้วให้ `ProcessingNotes` เป็น **การ render** ของมัน (ผู้ใช้ยังอ่านได้เหมือนเดิม)
- แก้ T5-N1 (ธงตามไปกับ snapshot อัตโนมัติ) · T5-N5/N8 (UI อ่าน field ไม่ใช่ regex) · T5-16 · และทำให้ KPI §5.3 คำนวณด้วย SQL ได้
- ฝ่ายค้าน: *"เพิ่มคอลัมน์อีกแล้ว ตารางมี ~49 คอลัมน์"* — คำตอบ: เป็น JSON เดียว และ **แทนที่** การ `Contains` 5 จุดที่วันนี้ตัดสินเรื่องภาษีซื้อ — ต้นทุนของการคงสถานะเดิมคือ T5-N1 ซึ่งเป็นการยื่นภาษีเกินสิทธิ์

### T5-D4 🔵 TEST_PLAN.md — entry ที่เสนอเพิ่ม (หมวด 5 OCR)
| ID | ระดับ | เคส | คาดหวัง |
| --- | --- | --- | --- |
| OCR-U-10 | U | `เลขที่ 123/45 ถนนพระราม 4` + `เลขที่ INV-2569-0042` | ได้ `INV-2569-0042` (วันนี้ได้ `123/4`) |
| OCR-U-11 | U | `No. 123/45 Sukhumvit Rd.` + `Invoice No. INV-77012` | ได้ `INV-77012` |
| OCR-U-12 | U | เคส C ส่วนลด 3,333 บนบิล 100,000 (20 บรรทัด) | Σ บรรทัด = 96,667 **เป๊ะ** |
| OCR-U-13 | U | WHT ฐาน 101.50 อัตรา 3% ทั้งเส้นเอกสารและเส้น JE | ได้ **3.05** เท่ากันทั้งสองเส้น |
| OCR-U-14 | U | status machine: ทุก tier ล้ม | `ScanStatus=Failed` คงอยู่ · quality = null · refund ถูกเรียก |
| OCR-U-15 | U | `AmountTripleExtractor`: Makro 1,000/49/1,049 + สามค่าปลอม 951/49/1,000 | ไม่ทับ · มี ReasoningTrace |
| OCR-I-05 | I | อัปไฟล์เดิมซ้ำ (cache hit) ของใบกำกับ**อย่างย่อ** | สแกนสำเนายังมี `[VAT-CLAIM]` · สร้างเอกสารแล้ว VAT ไม่เข้า 11610 |
| OCR-I-06 | I | `DeleteScanAsync` บนสแกนที่ `CreatedJournalEntryId != null` | throw · ไฟล์ยังอยู่ |
| OCR-I-07 | I | create-document 2 request พร้อมกันจากสแกนเดียว | 1 Draft + 1 BusinessRuleException |
| OCR-I-08 | I | JE จากสแกน ฝั่ง **ผู้ขาย** ที่มี WHT | มีบรรทัด `Dr 11910` และลูกหนี้ = total − wht (วันนี้ทิ้ง WHT) |
| OCR-G-01 | Golden | ชั้นค้าปลีกผสม 7%/ยกเว้น 10 ใบ | VAT รายบรรทัดถูก 100% · ไม่มีบรรทัดผี |
| OCR-G-02 | Golden | ชั้นบริการมี WHT 10 ใบ | Blocking = "ต้องยืนยัน WHT/ประเภทเงินได้" ไม่ auto-approve |

---

## §9 ยืนยัน / หักล้าง ทีมอื่น (จากหลักฐานของ T5 เอง)
| รายการ | ผล | หลักฐาน |
| --- | --- | --- |
| **T2-01** Failed→Completed | ✅ **ยืนยันว่าปิดแล้วจริง** | `:629` |
| **T2-09** AmountTripleExtractor ทับค่าที่ engine อ่านถูก | ✅ **ยืนยันว่าปิดแล้ว** (และครอบเคส 0% เพิ่มด้วย) | `SmartFieldExtractor.cs:786–812` — แต่ **ยังไม่มีเทสต์ล็อกไว้** |
| **T2-10** regex เลขที่จับที่อยู่ | ⚠️ **หักล้าง "แก้แล้ว"** — ยังพังในเคสที่พบบ่อยที่สุด (บ้านเลขที่ ≥3 หลักหลัง `/`) พร้อม simulation 7 เคส | T5-N2 |
| **T2-05 / T1-09** Case A–D | ✅ **ยืนยันว่าตรรกะปิดแล้ว** (ไม่แต่งบรรทัดผี · ส่วนลดคิดบนฐานก่อน VAT) | `OcrLineReconciler` + 9 เทสต์ — **แต่พบบั๊กใหม่ในตัวการแก้** (T5-N4) |
| **T1-03** WHT สองสูตร | ⚠️ **ยืนยันครึ่งเดียว** — ฐานตรงกันแล้ว (`total − vat` ทั้ง `:4794` และ `:5567`) แต่ **โหมดปัดเศษยังต่างกัน** | T5-N6 |
| **T1-01** ผัง VAT/WHT ของ JE ตรง | ✅ **ยืนยันว่าปิดแล้ว** — 11610/21911/21916/21917 มีจริงในผังมาตรฐาน (`ChartOfAccountTemplates.cs:47,142,147,148`) และ fallback ตามชื่อเลือก 11610/21911 ถูก | แต่ fallback ของ **WHT** จะได้ 21914 (ภ.ง.ด.1) — T5-N7 |
| **T1-02** JE ตรงข้ามด่านภาษี | ✅ **ยืนยันว่าปิด §82/5 แล้ว** (`:5547` throw) · **ยังเปิด**: §82/3 6 เดือน · tax point · ฝั่งผู้ขายไม่ลง WHT (T5-N3) | |
| **T1-04** วันที่ = วันนี้ | ✅ **ยืนยันว่าปิดครึ่งบน** — มี `[DATE-UNKNOWN]` + ห้าม auto-approve · **ยังเปิดครึ่งล่าง**: เอกสารยังได้ `docDate = today` และเหตุผลไม่ persist | T5-N8 |
| **T1-06** สิทธิ์ create/approve | ✅ ยืนยันว่ามีจริง `OcrController.cs:301, :329` | |
| **T4-01/T4-03** UI ไม่รู้จักแท็กใหม่ | ✅ **ยืนยันด้วยหลักฐานฝั่ง server เพิ่ม**: แท็กเหล่านั้น **ไม่ถูกบันทึกลงฐานด้วยซ้ำ** ⇒ ต่อให้ UI อ่านเป็น ก็หายอยู่ดีเมื่อ reload | T5-N8 |
| **T3-13** ไม่มี evaluation harness | ✅ ยืนยัน + ให้ spec | T5-D1 |

## §10 ตรวจแล้วไม่ใช่บั๊ก (ทีมอื่นไม่ต้องเสียเวลาซ้ำ)
- **เลขในเคส A ของ `OcrLineReconciler` ถูกต้อง**: `Amount = round(amount − lineVat, 2)` (`:5245`) ⇒ Σ Amount = gross − headerVat ≈ SubTotal · สมการที่เคยทำให้ Dr เกิน Cr เท่ายอด VAT (IKEA 1,396) ปิดแล้วจริง
- **เครื่องหมาย `UnreconciledGap`** สม่ำเสมอทั้ง 4 ทางออก (บวก = กระดาษมากกว่าบรรทัด) — ไม่มีการสลับทิศ
- **การถอดยอดก่อน VAT เมื่อกระดาษไม่พิมพ์ subtotal** มีจริงที่ผู้เรียก (`:5073–5074`) ⇒ ไม่เกิดอาการ "บรรทัดขาดเท่ายอด VAT ทุกใบ" ตามที่ผมสงสัยตอนอ่านคลาสเดี่ยว ๆ (จดไว้ตามกติกา "รายงานผิดต้องบันทึกว่าผิด") — ข้อสังเกตที่เหลือคือ **ตรรกะนี้อยู่นอกคลาส** ผู้เรียกรายที่สองจะพลาด
- **`ResolveTaxAccountAsync` fallback ของ "ภาษีซื้อ" และ "ภาษีขาย" ถูกต้อง** บนผังมาตรฐาน (11610 ชนะ 11620/11630/11640 · 21911 ชนะ 21912/21913) — ที่ผิดคือ WHT เท่านั้น
- **`ApplyTotalWithWhtMath` (`SmartFieldExtractor:691–713`) ไม่เขียนค่ากลับ** — เขียนเฉพาะ `ReasoningTrace` ⇒ สูตร WHT ที่ใช้ฐาน SubTotal ตรงนั้น **ไม่ทำให้ยอดผิด** (เป็นแค่ข้อความ) — จึงเป็น P3 ไม่ใช่ P1
- **เส้น cache อ่านเฉพาะแถว `Completed`** (`:163`) ⇒ ไม่มีการคัดลอกผลจากแถวที่ล้มเหลว
- **`ExtractedDate == null` ใช้ตัดสินการข้าม auto-approve ได้จริง** — `CreateDocumentFromScanAsync` เติม "วันนี้" ลง `Document.DocumentDate` ไม่ใช่ลงแถวสแกน ⇒ ธงยังเป็น null ตามที่ตั้งใจ
- **JE ฝั่งผู้ซื้อสมดุลเสมอ**: Dr (total−vat) + Dr vat = total = Cr wht + Cr (total−wht) — ตรวจด้วยพีชคณิตแล้ว ไม่ต้องสงสัย

## §11 ยังไม่ได้ตรวจ (ทีมรอบหน้าเริ่มตรงนี้)
- `DocumentService` เส้น JE ของ `DocumentType` ที่ OCR สร้างจริง ใช้ **Σ บรรทัด** หรือ **ยอดหัว** เป็นเดบิต — คำตอบตัดสินว่า T5-N4 จบที่ "อนุมัติไม่ผ่าน" หรือ "GL ต่างจากกระดาษ"
- `PostWith429RetryAsync` มีเพดานจำนวนรอบไหม (ขอบบนของ T5-07)
- `OcrQuotaService.RefundAsync` คืนกี่หน้าเมื่อ PDF หลายหน้า
- `IntegrationService` เส้น partner → `ScanAsync(autoCreate: true)` แล้วอนุมัติต่อเองไหม (ถ้าใช่ T5-03 ยกระดับเป็น P0 สำหรับ partner)
- `OcrAiAugmenter.HasModelAnswer` — เส้น student ถูกเรียกจริงกี่ feature และ threshold เท่าไร (มีใน diff แต่ยังไม่ได้ไล่ call site ครบ)
- `AzureDiPatternLearner` ≥0.85 — ยังไม่ได้ตรวจว่า pattern ที่ค้างในฐานจากก่อนแก้ถูกล้างหรือยัง (บทเรียน `OcrLearnedPatterns.ExtractionRegex`: แก้โค้ดอย่างเดียวไม่พอ)
- `ocr-service/` (python) timeouts/retries ภายใน · `EmbeddedTesseractOcrService` เวลาต่อหน้า

---

## §12 ภาคผนวก — ปิดคำถามค้างของ T5-N4 (ตรวจเพิ่มหลังเขียน §11)
เปิด `DocumentService.AutoPostToJournalAsync` (:12929) แล้วไล่สาขา `PurchaseInvoice`/`Expense`:
- ขาเดบิตค่าใช้จ่ายสร้าง **ต่อบรรทัด** จาก `docLine.Amount` (`AddLine(expenseAccountId.Value, debitAmount, 0, desc, docLine.ProjectId)`)
- ขาเครดิตเจ้าหนี้ใช้ **ยอดระดับหัวเอกสาร** (`AddLine(apAccount.Id, 0, apAmountAtInvoice, …)`) · VAT ใช้ `doc.VatAmount` (หัว)
⇒ เมื่อ `Σ Line.Amount ≠ Document.SubTotal` (เคส C ของ T5-N4) **เดบิตไม่เท่าเครดิต** ⇒ `AccountingService.cs:327` โยน
"การบันทึกบัญชีไม่สมดุล" ตอนกดอนุมัติ — ผู้ใช้เห็น error ที่ไม่บอกว่าเลขไหนผิด และแก้เองไม่ได้เพราะยอดทุกช่อง "ดูถูก"
**ยกระดับ T5-N4 เป็น 🔴 [P1] ความมั่นใจสูง** (เดิมระบุว่ากลาง) และตัดข้อนี้ออกจาก §11

## §13 สรุป
รอบนี้ตรวจ **การแก้ 9 อย่างของ main agent** แล้วยืนยันว่า **6 อย่างถูกต้องจริง** (status machine · AmountTriple ·
ตรรกะ reconcile · §82/5 บนเส้น JE · ฐาน WHT · ผัง 11610/21911/21916/21917) · **3 อย่างยังไม่จบ**
(regex เลขที่เอกสาร · การใช้ผล reconcile ในเคสส่วนลด · เสียงของด่านอนุมัติ) และพบ **ผลข้างเคียงใหม่ 1 เรื่องระดับ P0**
(เส้น cache ทิ้งธง §82/5) รวม **finding ใหม่ 8 ข้อ (T5-N1…N8)** + **ยืนยันสถานะ finding เดิม 18 ข้อ** (ปิดแล้ว 1 · ยังเปิด 15 · ดีขึ้นบางส่วน 2)
