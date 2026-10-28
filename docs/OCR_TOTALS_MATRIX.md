# OCR — เมทริกซ์ "ยอดบรรทัด ↔ หัวใบ" ทุกรูปแบบเอกสาร (สถานะปัจจุบัน · ทีม T 2026-10-26)

> ที่มา: ผู้ใช้รายงานซ้ำหลัง deploy ทีม R (ใบ e-Tax `SP-IN2026-10-00933` Rapbizz Direct): "ทำไมยังบันทึกได้ไม่ตรง … ให้ครอบคลุมทุกรูปแบบเอกสาร ของเดิมที่ทำได้ดีอยู่แล้วอย่าไปปรับให้เสียหาย"
> ไฟล์นี้คือ **สถานะปัจจุบัน** ของตัวตัดสินยอด (แก้เมื่อโค้ดเปลี่ยน ไม่ append — CLAUDE.md F4 ข้อ 5) · ประวัติรอบอยู่ `CHANGELOG.md` · บทเรียนดิบอยู่ `docs/lessons/ocr-pipeline.md`
> ตัวตัดสินทั้งหมดเป็น pure helper: เส้นกระดาษ `Helpers/OcrLineReconciler.Classify` (หลัง `OcrHeaderAmounts.Normalize`) · เส้น e-Tax `Helpers/OcrEtaxLineNormalizer.NormalizeInvoice` → `HeaderBillDiscount` → Classify ตัวเดียวกัน

## §1 ใบ SP-IN2026-10-00933 — เส้นจริงหลังทีม R ทั้งสาย (ตัวเลขทุกขั้น)

กระดาษ: เก้าอี้ 2 × 920.00 = 1,840.00 · ค่าขนส่ง 1 × 170.00 · "จำนวนเงิน Amount 1,878.51" · "ส่วนลด 538.32" · "ยอดรวม Total 1,340.19" · VAT 7% 93.81 · "ราคารวม Sub Total 1,434.00" (ป้ายสลับ)
XML: NetLineTotal 1,840 / 170 · AllowanceTotal 576.00 · LineTotal = TaxBasis 1,340.19 · Tax 93.81 · Grand 1,434.00

| ขั้น | โค้ด (ตัวจริง) | ตัวเลขที่ออก | ตรงกระดาษ? |
| --- | --- | --- | --- |
| 1 ตัดสินบรรทัด XML | `OcrEtaxLineNormalizer.NormalizeInvoice` → `HeaderProvenInclusive` | PricesIncludeVat=true · บรรทัด 2×920=1,840 · 1×170 (รวม VAT) · BillAllowanceInclVat 576.00 · หมายเหตุ `[e-Tax]` 1 บรรทัด | ✅ |
| 1b ส่วนลดหัวใบ | `HeaderBillDiscount` ⇒ `ExtractedDiscountAmount` | 576 ÷ 1.07 = **538.32** (ก่อน VAT · เท่าหน้า PDF) | ✅ |
| 1c หัวใบ | `MapEtaxToOcrData` | ExtractedSubTotal 1,340.19 · VAT 93.81 · Total 1,434.00 | ✅ |
| 2 ตำแหน่งส่วนลด | `ScanBillDiscount` → `OcrTotalDecomposer.ForScan(RawText = XML)` | ไม่ใช่ PostInvoice ⇒ 538.32 คงเดิม · ไม่มี `[PAY≠TOTAL]` | ✅ (ตัวจำลองเดิมข้ามขั้นนี้ — เติมแล้ว) |
| 3 Document.SubTotal | `ResolveHeaderSubTotal` = `OcrHeaderAmounts.NetSubTotal` | 1,434.00 − 93.81 = **1,340.19** | ✅ |
| 4 กระทบยอด | `OcrVatCodeLines.Decide` → `OcrLineReconciler.Classify(2,010 · 1,340.19 · 93.81 · 1,434 · 538.32)` | เคส F `DiscountPreVatOnInclusivePrices` · 28.66% · เป้า Σ = 1,434.00 · gap 0 | ✅ |
| 5 ส่วนลดท้ายบิล (คำตัดสินข้อ 159) | `BuildScanLinesAsync`: `recon.BillDiscountPreVat` ⇒ `Document.BillDiscountAmount` | **538.32** (ก่อน VAT · ตามกระดาษ) · บรรทัด 1,840 / 170 **ไม่มีส่วนลด** · ไม่กระจาย | ✅ ตรงกระดาษทุกช่อง |
| 6 ยอดบรรทัด | `DocumentService.PlanLineAmounts` (ตัวตั้งเดียว = AllocateBillDeductions → ComputeLineAmounts → ReconcileTaxRounding) | ฐานก่อนลด 1,719.63 + 158.88 = **1,878.51** (= "จำนวนเงิน Amount") · จัดสรร 492.79 / 45.53 ⇒ Amount **1,226.84 / 113.35** · VAT **85.88 / 7.93** | ✅ Σ 1,340.19 / 93.81 |
| 7 ยอดหัวเอกสาร | `CreateDocumentFromScanCoreAsync` | SubTotal 1,340.19 · VAT 93.81 · Total **1,434.00** · RoundingAdjustment 0 · `DiscountAmount` 0 · `BillDiscountAmount` 538.32 | ✅ ตัวเลข + หน้าตา (ท้ายเอกสาร "ยอดรวมก่อนส่วนลดท้ายบิล 1,878.51 · ส่วนลดท้ายบิล −538.32 · ก่อน VAT 1,340.19") |
| 8 อนุมัติเอง | `OcrPostingReadiness.Evaluate` | ไม่มีแท็กหยุด ⇒ อนุมัติเองได้ | ✅ |
| 9 ฟอร์มแก้ไข | `documents.html` hydrate: `pricesIncludeVat` จาก entity · `billDiscountAmount` 538.32 โหมด ฿ · บรรทัดไม่มีส่วนลด | ส่ง `billDiscountAmount` 538.32 · `lines[].discountAmount` null | ✅ |
| 10 บันทึกซ้ำ | `UpdateDocumentAsync`: AllocateBillDeductions → ComputeLineAmounts → ReconcileTaxRounding (สูตรเดียวกับ `PlanLineAmounts` ที่ตัวสร้างใช้) | 1,226.84 / 85.88 · 113.35 / 7.93 ⇒ 1,340.19 + 93.81 = 1,434.00 | ✅ ไม่ขยับ (โดยสร้าง — ไม่มีสำเนาสูตร) |
| 10b ทิศตรงข้าม | ถ้า `billDiscountAmount` หาย (หน้าเว็บส่ง 0 เหมือนก่อนข้อ 159) | ฐาน **1,878.51** · VAT 131.50 · 2,010.01 ≠ 1,434 | ❌ — จุดที่เปลี่ยนกลับได้ · `OcrLinePreviewResponse.BillDiscountAmount` + หน้าเว็บส่งต่อ · ล็อกด้วยเทสต์ `ทีมT_ข้อ159_ทิศตรงข้าม_*` |
| 10c รูปกระจายเดิม (เคส C/E) | ส่วนลดบาทต่อบรรทัดหาย ⇒ คิดจาก % | 1,840 × 28.66% = 527.34 ⇒ 1,433.94 | ❌ (ล็อกไว้ — ฟอร์มส่งบาทโหมด ฿) |

เทสต์ที่ล็อก: `OcrRapbizzSpIn00933Tests.ทีมT_เส้นจริงทั้งสาย_*` · `ทีมT_ทิศตรงข้าม_ถ้าฟอร์มส่งแต่เปอร์เซ็นต์_*` · `ทีมT_เส้นกระดาษของใบเดียวกัน_ScanBillDiscount*` · `EtaxGoldenCorpusTests["RapbizzSpIn00933"]`
**สรุปข้อ 1:** ตัวเลขที่บันทึก (ฐาน · VAT · สุทธิ) หลังทีม R **ตรง** ทุกขั้นในโค้ด — ไม่พบขั้นที่เปลี่ยนกลับเมื่อผู้ใช้กด "ยืนยัน" หรือเปิดแก้แล้วบันทึก
สิ่งที่ "ไม่ตรง" ที่ยืนยันได้จากหลักฐานที่มี = (ก) กล่องเหลือง "ระบบไม่แน่ใจ" ทั้งที่หัวใบพิสูจน์แล้ว (§2 · แก้แล้ว) (ข) หน้าตาเอกสารต่างจากกระดาษแม้ยอดเท่ากัน (§4 · **ตัดสินแล้ว ข้อ 159 — ลงส่วนลดท้ายบิลตามกระดาษ**)
สิ่งที่ตรวจไม่ได้จนกว่าจะเห็นตัวเลขที่บันทึกจริง: สแกน/ร่างที่สร้าง**ก่อน** deploy ทีม R ยังถือบรรทัดเดิม (920/170 ก่อน VAT · ไม่มีส่วนลด · [Σ-GAP]) — ต้องลบร่างแล้วอัปไฟล์เดิมใหม่ (ทีม R จดไว้แล้ว)

## §2 ระดับคำเตือนบนหน้าตรวจ

| แท็ก | ความหมายฝั่งเซิร์ฟเวอร์ | เดิมบนหน้า | ตอนนี้ |
| --- | --- | --- | --- |
| `[e-Tax]` (`OcrEtaxLineNormalizer.NoteTag`) | ข้อสังเกต — ระบบพิสูจน์ด้วยตัวเลขแล้ว**ปรับให้แล้ว** (หัวใบพิสูจน์ราคารวม VAT · เศษจากถอด VAT · ขั้นปัด VAT) · ไม่บล็อก | อยู่ใน `warnings` ⇒ กล่องเหลือง "⚠️ ตรวจก่อนยืนยัน — ระบบไม่แน่ใจ" | กล่องน้ำเงิน "ℹ️ ระบบตรวจพบและปรับให้แล้ว" (`parseScanNotes.adjusted`) · ยังแสดงเหตุผลครบ |
| `[ETAX-HEADER-GAP]` · `[ETAX-LINE-CHECK]` | ไม่ลงตัว / ตัดสินฐานไม่ได้ · `BlockingTags` | กล่องเหลือง | กล่องเหลือง (คงเดิม — ไม่แน่ใจจริง) |
| `[Σ-GAP]` และแท็กอื่นใน `warnings` | ตามเดิม | กล่องเหลือง | คงเดิม |

Confidence ของสแกน e-Tax = 1.0 ทุกช่อง (`MapEtaxToOcrData`) ⇒ ไม่มี highlight เหลืองจาก confidence · กล่องเหลืองมาจากแท็กอย่างเดียว · LINE card (`LineBotService`) ไม่เคยอ่าน `[e-Tax]` อยู่แล้ว

## §3 เมทริกซ์ความครอบคลุม — {ราคาบรรทัด} × {ส่วนลด} × {ป้าย} × {VAT} × {XML} × {บาทถ้วน}

กติกาอ่านตาราง: "ล็อก" = เทสต์/golden ที่ล้มถ้าคำตอบเปลี่ยน · "สังเคราะห์" = ตัวเลขสมจริงที่ยังไม่มีกระดาษจริง · ป้ายสลับ (Sub Total = สุทธิ) ถูก `OcrHeaderAmounts.Normalize` แก้ก่อนทุกเคส จึงไม่แยกแถว (ล็อก: `OcrHeaderAmountsTests` ลักกี้เวย์ 667↔623.36 · เคส G swapped) · บาทถ้วน (`OcrTotalAnchor` อ่าน "…บาทถ้วน") เลือก**ยอดรวม**ที่เชื่อ ไม่เปลี่ยนเคส (ล็อก: `OcrTotalAnchorTests`)

| # | ราคาบรรทัด | ส่วนลด | VAT | มี XML | เคส/ตัวตัดสิน | ล็อกด้วย | สถานะ |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | ก่อน VAT | ไม่มี | 7% | — | B `LinesMatchSubTotal` | `OcrLineReconcilerTests.ราคาแยกVATมาตรฐาน` · replay telecom-* | ✅ จริง |
| 2 | ก่อน VAT | ต่อบรรทัด | 7% | — | `ProvenLineDiscount` ⇒ Σ ยอดหลังลด → B | replay `pramie-line-discount-re111` / `pramie-re111-positional` | ✅ จริง |
| 3 | ก่อน VAT | ท้ายบิล **ก่อน VAT** | 7% | — | C `DiscountOnSubTotal` | Hardware 69.75 · Lazada 216.82 (replay + unit) | ✅ จริง |
| 4 | ก่อน VAT | ท้ายบิล **รวม VAT** | 7% | — | **G `DiscountInclVatOnExclusivePrices` (เพิ่มรอบนี้ · วางหลัง F · เข้าได้เฉพาะใบที่เดิม Ambiguous) ⇒ BillDiscountAmount = ส่วนลด ÷ 1.07 (ข้อ 159)** | `OcrLineReconcilerTests.เคสG_*` (+ทิศตรงข้าม) · baseline §baseline | 🧪 สังเคราะห์ |
| 5 | รวม VAT | ไม่มี | 7% | — | A `PricesIncludeVat` | WinePro · IKEA 1,396 | ✅ จริง |
| 6 | รวม VAT | ต่อบรรทัด | 7% | — | Σ ยอดหลังลด → A | `crc-thaiwatsadu-srcie75384-positional` | ✅ จริง |
| 7 | รวม VAT | ท้ายบิล **รวม VAT** | 7% | — | E `DiscountOnTotalInclVat` | Supermarket 38.70 · Makro p1p2 | ✅ จริง |
| 8 | รวม VAT | ท้ายบิล **ก่อน VAT** | 7% | — | F `DiscountPreVatOnInclusivePrices` (ทีม R) ⇒ **BillDiscountAmount ตามกระดาษ (ข้อ 159)** | `OcrRapbizzSpIn00933Tests.กระดาษ_เคสF_*` | ✅ จริง (ข้อความ PDF · ยังไม่อยู่ใน `OcrPaperSamples` — §5) |
| 9 | ใด ๆ | ท้ายบิล | **0% / ยกเว้นทั้งใบ** | — | ฐาน = สุทธิ ⇒ C · ไม่มีฐาน ⇒ C-legacy `DiscountOnTotal` | `OcrLineReconcilerTests` (500 · 0 · 0 · 450 · 50) · เคส G ทิศตรงข้าม VAT 0 | ✅ |
| 10 | ใด ๆ | ท้ายบิล | **หลายอัตรา** | — | B/C/E ไม่พึ่งอัตรา (ใช้ Σ) · F/G ต้อง 7% ⇒ ไม่เดา → `OcrVatCodeLines` แบ่งส่วนลดต่อรหัส ภ.พ. (ตารางบนกระดาษ) · ไม่มีตาราง ⇒ Ambiguous `[Σ-GAP]` | Makro 2/2 · `OcrVatCodeLinesTests` · ทิศตรงข้าม F หลายอัตรา | ⚠️ ช่องว่าง: รูป F/G + หลายอัตรา + ไม่มีตาราง = คนตัดสิน (ถูกต้องตามหลัก "ไม่รู้ = บอกว่าไม่รู้") |
| 11 | XML รวม VAT (NetIncl ≠ Net ต่อบรรทัด) | ต่อบรรทัด | 7% | ✓ | `ProveInclusive` ทุกบรรทัด + `TiesOutInclusive` ⇒ ค่าตามกระดาษ | `EtaxGoldenCorpusTests` CRC ×3 | ✅ จริง |
| 12 | XML ดูก่อน VAT แต่หัวใบพิสูจน์รวม VAT | AllowanceTotal **รวม VAT** | 7% | ✓ | `HeaderProvenInclusive` ⇒ 538.32 ก่อน VAT ⇒ F ⇒ BillDiscountAmount (ข้อ 159) | `EtaxGoldenCorpusTests["RapbizzSpIn00933"]` · `ETax_หลังแก้_*` | ✅ จริง |
| 13 | XML ก่อน VAT (CII มาตรฐาน) | AllowanceTotal ก่อน VAT (= LineTotal − TaxBasis) | 7% | ✓ | `HeaderBillDiscount` = LineTotal − TaxBasis ⇒ C | `ETax_ทิศตรงข้าม_ใบมาตรฐานCII_*` | ✅ |
| 14 | XML ก่อน VAT | AllowanceTotal | **0% / หลายอัตรา** | ✓ | `HeaderProvenInclusive` ไม่ตัดสิน (อ่านได้สองแบบ / หลายอัตรา) ⇒ ทางเดิม | `ETax_ทิศตรงข้าม_*` | ✅ |
| 15 | XML รวม VAT ต่อบรรทัด (แบบ CRC) | AllowanceTotal **รวม VAT** (ท้ายบิล ไม่อยู่ในบรรทัด) | 7% | ✓ | `TiesOutInclusive` ไม่ลงตัว ⇒ `[ETAX-HEADER-GAP]` หยุดอนุมัติเอง (ลงตามกระดาษ · ให้คนตรวจ) | — | ⚠️ ช่องว่าง (สังเคราะห์ · ยังไม่มีใบจริง) — ไม่เพิ่มกติกาจนกว่าจะมีกระดาษ: ต้องรู้ว่าผู้ขายคิด VAT ก่อนหรือหลังส่วนลด |
| 16 | XML | ไม่มี | 7% | ✓ | ทางเดิม (Shopee) | `EtaxGoldenCorpusTests` Shopee | ✅ |
| 17 | รวม VAT | **หลัง VAT** ("ส่วนลดพิเศษ/voucher" พิมพ์หลัง "รวมทั้งสิ้น" · ยอดชำระ = รวม − ส่วนลด) | 7% | — | `OcrTotalDecomposer` **PostInvoice** (ตัวตัดสินเดิม — ไม่ลดฐาน/VAT) → **ทีม U**: `OcrPostVatDeduction.FromPlan` ⇒ ช่อง `Document.PostVatDeductionAmount` ก้อนเดียว (ผัง 51150/ผังค่าใช้จ่ายหลัก) + `[POST-VAT-DEDUCTION]` **เฉพาะ "ส่วนลดล้วน"** (ทุกรายการปรับเป็นลบ) · ขา JE Cr ตอนอนุมัติ (`Helpers/PostVatDeduction`) · **ข้อยกเว้น (ฝ่ายค้านข้อ 2)**: รายการปรับ**ผสม** (ใบ U จริง: ค่าจัดส่ง +37 / Shopee Voucher −135) **ไม่ยุบ** — ค่าส่ง 37 ต้องอยู่ 51120 ⇒ คงเส้นรอบ 193 adjusting lines 51120 +37 / 51150 −135 (`OcrSettlementProposal`) · จ่าย**มากกว่า**ใบ (ค่าส่งล้วน) ยังทางเดิม · เส้นฟอร์มยกช่องหักไปได้ (adjusting lines ยกไม่ได้ — ช่องว่างเดิมของแบบผสมยังอยู่) | `OcrPostVatDeductionTests` (ใบ U ผสม ⇒ null + plan 51120/51150 · ใบ U ตัดหมายเหตุ = ส่วนลดล้วน ⇒ หัก 98/51150 · ทิศตรงข้าม Lazada ก่อน VAT = null) · `OcrTotalDecomposerTests` Uptoyou · `PostVatDeductionTests` (ด่าน VAT ≠ ฐาน × อัตรา — PV-202610-0040 402.93/35.07 บล็อก) | ✅ จริง (ตัวอย่าง `OcrPaperSamples.UptoyouShopee` · ข้อความ pdftotext ของใบนี้ยังไม่เข้า — ดู §5) |

### §baseline — หลักฐาน "ใบที่เดิมตัดสินได้ไม่เปลี่ยน" ของเคส G (ขอบเขตที่พิสูจน์แล้วจริง)

วิธี: port Python ของ `OcrLineReconciler.Classify` (รวม `OcrHeaderAmounts.IsSwapped` · `PreVatDiscountOnInclusiveLines`) จาก `git show 74a4796c:Accounting/Helpers/OcrLineReconciler.cs` แล้วรันก่อน/หลังเพิ่มเคส G บน
(ก) vector ทุกตัวที่ไฟล์เทสต์เรียก `OcrLineReconciler.Classify(` ด้วย literal — 28 ตัว ⇒ **0 เปลี่ยน** (ข) เมทริกซ์สังเคราะห์ 24 ช่อง ⇒ เปลี่ยน 2 = ช่อง "ก่อน VAT × ส่วนลดพิมพ์รวม VAT" ทั้งสองป้าย (Ambiguous → G) เท่านั้น
ขอบเขต: นี่คือการเทียบเท่าของ C# ด้วย Python ไม่ใช่การรัน C# · vector ที่ประกอบจากตัวแปร (replay harness · VatCodeLines · golden e-Tax) **ยังไม่ได้พิสูจน์ในเครื่องนี้** — ต้องรอ CI (`OcrReplayLineGoldenTests` · `EtaxGoldenCorpusTests` · `OcrVatCodeLinesTests`) · เชิงโครงสร้าง เคส G อยู่หลัง F และบังคับ Σ > ฐาน + 1 บาท จึงถึงได้เฉพาะเส้นที่เดิมคืน Ambiguous

## §4 "ตรงตัวเลข แต่หน้าตาต่างจากกระดาษ" — **ตัดสินแล้ว: คำตัดสินเจ้าของข้อ 159 (2026-10-26 · ตามหลักข้อ 158 "ข้อมูลที่ครบ/ตรงกระดาษที่สุด")**

**กติกาตอนนี้:** กระดาษพิมพ์ "ส่วนลดท้ายบิล" เป็นยอดก่อน VAT (เคส F) หรือรวม VAT บนราคาก่อน VAT (เคส G) ⇒ `OcrLineReconcileResult.BillDiscountPreVat` ⇒ `BuildScanLinesAsync` ตั้ง `Document.BillDiscountAmount`
ตามกระดาษ (538.32) · บรรทัด = จำนวน × ราคาตามกระดาษ ไม่มีส่วนลด · ยอดบรรทัด (ก่อน VAT · VAT) จาก `DocumentService.PlanLineAmounts` (ตัวตั้งเดียวของ Create/Update — ไม่มีสำเนาสูตรฝั่ง OCR) ⇒
ฐาน/VAT/สุทธิ **เท่าเดิมทุกสตางค์** (1,340.19 / 93.81 / 1,434.00) และท้ายเอกสารพิมพ์ "ยอดรวมก่อนส่วนลดท้ายบิล 1,878.51 · ส่วนลดท้ายบิล −538.32 · ยอดรวมก่อน VAT 1,340.19" ตรงกระดาษทุกช่อง ·
e-Tax XML ที่ Allowance ระดับเอกสาร (ใบนี้) เดินเส้นเดียวกัน (HeaderBillDiscount ⇒ เคส F) · เคส C (ส่วนลดก่อน VAT บนราคาก่อน VAT — ร้านวัสดุ/Lazada) และ E (ส่วนลดรวม VAT บนราคารวม VAT — ซูเปอร์) **ยังกระจายลงบรรทัดเหมือนเดิม**
(เทสต์ทิศตรงข้าม `ทีมT_ข้อ159_ทิศตรงข้าม_*`: `BillDiscountPreVat` = null) · ใบเดิมที่สร้างไปแล้วไม่เปลี่ยน (ไม่มี migration — ดึงรายการซ้ำ/อัปใหม่จึงได้รูปใหม่) · หน้าตรวจ (`OcrLinePreviewResponse.BillDiscountAmount` → ฟอร์ม "แก้ในฟอร์มก่อน" + พรีวิว) ·
ฟอร์มแก้ไข (hydrate `billDiscountAmount` โหมด ฿) · renderer HTML/QuestPDF (แสดง `BillDiscountAmount` อยู่แล้ว) — ไม่แตะ

**ก่อน-หลัง (§H):** ใบใน replay corpus (`OcrReplayLineGoldenTests`) ไม่มีใบใดมี ReconCase F/G (golden ล็อก ReconCase ต่อใบ · F/G ไม่ปรากฏ) และ EtaxGoldenCorpus เข้าเคส F เฉพาะ Rapbizz ⇒ ใบที่เปลี่ยน = **Rapbizz ใบเดียว**
(golden: ส่วนลดบรรทัด 527.28/48.72 → 0 + BillDiscount 538.32 · ฐาน/VAT ทุกบรรทัดเท่าเดิม) · ยืนยันด้วย CI (ไม่มี SDK ในเครื่อง)

<details><summary>ข้อเสนอเดิม (ก่อนตัดสิน · เก็บไว้เป็นประวัติ)</summary>

ก่อนข้อ 159 ทุกเคสที่มีส่วนลดท้ายบิล (C/E/F/G) **กระจายส่วนลดลงบรรทัด** (`DocumentLine.DiscountAmount`) และ `Document.BillDiscountAmount = 0` ⇒ สำหรับใบนี้เอกสารแสดง:
บรรทัด 2 × 920.00 ส่วนลด 527.28 · 1 × 170.00 ส่วนลด 48.72 · "ส่วนลด 576.00" · ฐาน 1,340.19 · VAT 93.81 · 1,434.00 — ขณะที่กระดาษแสดง 1,840.00 / 170.00 (ส่วนลดบรรทัด 0.00) · จำนวนเงิน 1,878.51 · ส่วนลด 538.32 · 1,340.19 · 93.81 · 1,434.00
นักบัญชีที่เทียบทีละช่องจะเห็น "ไม่ตรง" (576 ≠ 538.32 · 1,226.84 ≠ 1,840) แม้ยอดภาษีทุกตัวเท่ากัน

ทางเลือกที่คำนวณไว้แล้ว (ยังไม่ทำ — เปลี่ยนรูปการบันทึกของทุกใบที่มีส่วนลดท้ายบิล ต้องให้เจ้าของตัดสิน): ลง `Document.BillDiscountAmount = 538.32` (ก่อน VAT) + บรรทัดราคารวม VAT ไม่มีส่วนลด
`DocumentService.AllocateBillDiscount` จะได้ฐานก่อนลด 1,719.63 + 158.88 = **1,878.51** (= "จำนวนเงิน Amount" บนกระดาษ) · จัดสรร 492.79 / 45.53 · ฐาน 1,226.84 / 113.35 · VAT 85.88 / 7.93 ⇒ 1,340.19 · 93.81 · 1,434.00 **เท่าเดิมทุกสตางค์** และท้ายเอกสารพิมพ์ "ยอดรวมก่อนส่วนลดท้ายบิล 1,878.51 · ส่วนลดท้ายบิล −538.32 · ยอดรวมก่อน VAT 1,340.19" ตรงกระดาษทุกช่อง
ผลกระทบ: `BuildScanLinesAsync` ต้องเลิกกระจายส่วนลดเองในเคส F (หรือทุกเคส C/E/F/G) และให้ `CreateDocumentFromScanCoreAsync` คำนวณบรรทัดด้วยสูตร `AllocateBillDiscount` (สำเนาสูตร = ต้องล็อก rcs) · harness/golden ระดับบรรทัดทุกใบที่มีส่วนลดจะเปลี่ยน (ยอดรวมไม่เปลี่ยน) ⇒ ต้องรัน §H ก่อน-หลังทั้งชุด

</details>

## §5 กระดาษใบนี้ใน `OcrPaperSamples`

ข้อความหน้า PDF อยู่ที่ `OcrRapbizzSpIn00933Tests.PageText` (ใช้กับ `OcrBillDiscount`/`OcrLineReconciler`/`OcrTotalDecomposer` แล้ว) · ยังไม่ย้ายเข้า `OcrPaperSamples` เพราะ `tools/ocr_golden_corpus_check.py` บังคับว่าทุก `public const` ต้องมี `ReplayPaper` ใน Corpus **และ** golden row ใน `OcrReplay*GoldenTests` ซึ่งรวม `OcrReplayPartyGoldenTests` (ชื่อ/เลข/สาขาผู้ซื้อ-ผู้ขายตามที่ `SmartFieldExtractor` ตอบ**วันนี้**) — ค่านั้นต้องมาจากการรัน dotnet ไม่ใช่เขียนจากความจำ (F2 ข้อ 3)
ขั้นที่ต้องทำเมื่อมี SDK: (1) ย้าย `PageText` → `OcrPaperSamples.RapbizzSpIn00933PdfText` (2) `new ReplayPaper("rapbizz-spin00933-prevat-discount-on-incl", …, EngineSubTotal 1340.19, EngineVat 93.81, EngineTotal 1434.00, VendorTaxId "0105559170207")` (3) รัน `OcrReplayLineGoldenTests`/`OcrReplayPartyGoldenTests` ให้พิมพ์ snapshot แล้ววางเป็น golden (คาด ReconCase = `DiscountPreVatOnInclusivePrices` · บรรทัด 1,226.84 / 113.35) (4) `python3 tools/ocr_golden_corpus_check.py` เขียว

**ใบ U Shopee INV2026090199 (ทีม U 2026-10-26 · ช่อง 17)** — `OcrPaperSamples.UptoyouShopee` มีอยู่แล้ว (ข้อความถอดจากหน้า PDF แบบอ่านได้ · ล็อกด้วย `OcrTotalDecomposerTests`/`OcrPostVatDeductionTests`) ·
ข้อความ **pdftotext -layout** ของไฟล์จริง (ผู้ใช้ส่ง 2026-10-26) ตัวอักษรไทยแตกตำแหน่งสระ/วรรณยุกต์ ("สว่ นลดพิเศษ" · "จำานวนเงินรวมทัง้ สิน้ ") ซึ่งไม่ใช่ข้อความที่ engine OCR ให้
ตอนสแกนจริง (Azure อ่านจากภาพ) ⇒ **ไม่ใส่เป็น sample/เทสต์** จนกว่าจะรันเส้นจริงได้ (F2 ข้อ 3 — ค่าที่เขียนจากความจำ = ค่าที่แต่ง) · ขั้นเมื่อมี SDK: ป้อน PDF ผ่าน `EtaxPdfXmlExtractor`
(ใบนี้เป็น e-Tax ของ Shopee — มี XML แนบหรือไม่ต้องดูไฟล์) หรือ render → engine แล้วพิมพ์ snapshot เป็น golden · คาด: `OcrDiscountPlacement.PostInvoice` · `PostVatDeductionAmount` 98 · ฐาน 500.93 / VAT 35.07 ไม่เปลี่ยน
