# รอบ 200 — ทีม K2 (OCR: แก้ผลฝ่ายค้านทีม K + ข้อ OCR ที่ทีม R ส่งต่อ)

คอมมิตงาน: `fb459244` (sha เติมในคอมมิตตามหลัง — ห้าม amend)

แหล่ง: `erp-review/2026-09-29/review200-K.md` (R1–R8) · คำตัดสินเจ้าของ **ข้อ 19 · 28 · 29** (`DECISIONS.md`) · `team-R.md` §4 (ส่งต่อ K) →
`erp-review/2026-09-21/report-C-ocr.md` C-01..C-03 · C-05..C-10 (verify ที่ HEAD `9ecff37f` ก่อนแก้ทุกข้อ — ยังจริงทุกข้อ ยกเว้น C-01 ที่รอบ 197 แก้ไปครึ่งหนึ่ง)

**ยังไม่ได้คอมไพล์** (เครื่องนี้ไม่มี .NET SDK) — เทสต์ใหม่ยังไม่เคยถูกรัน · ต้องรอ CI/rebuild ฝั่งผู้ใช้

> worktree ถูกสร้างจาก `origin/master` (คนละประวัติ) — `git reset --hard 9ecff37f` (หัว `claude/erp-system-review-team-660mev`) ก่อนเริ่ม · worktree สะอาด

## ตารางรายการ

| ID | P | สถานะ | ที่แก้ (file) | เทสต์ / ด่าน |
|---|---|---|---|---|
| **R1** | P2 | ✅ | `Helpers/OcrVendorBranchContact.VendorKeyTouched` (นับ `VendorBranchConfirmed` · สาขาเปลี่ยน · เลขภาษีเปลี่ยน) · `OcrService.SubmitCorrectionAsync` | `OcrReview200K2Tests.R1_Makro_UserRetypesTheSameBranch_*` · ทิศตรงข้าม `R1_WebEchoesTheStoredBranch_*` · `R1_TaxIdKey_*` · required_call_site `call_args` |
| **R2 → ข้อ 28** | P2 | ✅ | `Helpers/OcrPostedTruth.WhtTouched` (+ `OcrPostedWhtLine`) · `DocumentService.SyncScanToPostedDocumentAsync` merge `HasWht/WhtRate/WhtIncomeTypeCode` (ฝั่งซื้อ) · **ย้ายปิดลูป GL + sync สแกนขึ้นก่อน `_vendorIntel.TryTrainAsync`** ใน `ApproveDocumentAsync` · แก้ถ้อยคำ `team-K.md` ข้อ 9 | `Wht28_UserTicksWhtInDocumentForm_*` · `Wht28_UserChangesRate_*` · ทิศตรงข้าม `Wht28_DocumentCreatedFromScanUntouched_*` · required_call_site `before` (sync ก่อน train) |
| **R3 → ข้อ 29** | P3 | ✅ | คอลัมน์ใหม่ `OcrScanResults.VendorAddressUserTyped` (`DatabaseMigrationHelper` · DEFAULT false · **ไม่ backfill** = ไม่รู้) · ผู้เขียน `OcrCorrectedFieldList.VendorAddressTyped` (เฉพาะกติกา baseline) · ผู้อ่าน `DecideScanVendorBranchContactAsync` · `OcrScanSnapshot.RowIdentityFields` (ไม่คัดลอกไปสำเนา) | `Addr29_LegacyRow_*` · `Addr29_UserReallyTypes_*` · `Addr29_WebEchoes_*` · `OcrScanSnapshotTests` (reflection) |
| R4 | P3 | 📋 | — | แถวสาขาที่สร้างตอนแก้ผลสแกนถาวร: มองเห็นได้ (`[Auto-Create] … ตอนแก้ผลสแกน`) · สอดคล้องเส้นสแกน · ทางเข้มขึ้นต้องออกแบบกับ contact-hygiene (รายงานแถว OCR ไม่มีเอกสาร) |
| **R5** | P3 | ✅ | `OcrVendorBranchContact.ScanAlreadyPosted(CreatedDocumentId, CreatedJournalEntryId)` ⇒ `ShouldRedecideOnCorrection(documentCreated:)` | `R5_JeOnlyScan_*` · ทิศตรงข้าม `R5_ScanNotPostedYet_*` · required_call_site `call_args` |
| **R6** | P3 | ✅ | `OcrContactCreateLock.MayAdoptAfterLock` + `AdoptSkippedNote` · สแกน: `OcrService.AdoptTaxIdUnderOcrContactLockAsync` (ธุรกรรมสั้น + ล็อก + ถามคีย์ซ้ำ) แทน `existing.TaxId = …` · สร้างเอกสาร: จำแถวที่ถูกเติมเลข (`AdoptTaxId` ฝั่งขาย · backfill ฝั่งซื้อ) แล้วถามซ้ำหลัง `BeginTransactionAsync` — มีแถวอื่นถือคีย์ ⇒ ถอนการเติม (`SetValues(OriginalValues)`) + โน้ต | `R6_*` · required_call_site (`AdoptTaxIdUnderOcrContactLockAsync` · `before BeginTransactionAsync`) |
| R7 | P3 | 📋 | — | เท่าพฤติกรรมเดิม (ไม่ถอย) · แถวไม่มีเลขภาษีไม่มีกุญแจให้ `FindAsync` เห็น — ต้องล็อกด้วยชื่อ (นอกคำตัดสินข้อ 19) |
| **R8** | P3 | ✅ | `ScanAsync`: `try { … commit } catch (Exception) when (contactCreateTx != null) { UndoOcrContactCreateAfterRollback(…); throw; }` — ถอดแถวผู้ติดต่อที่เกิดในบล็อก · คืน `MatchedContactId` เดิม · บังคับเขียนแถวสแกนทั้งแถวตอนบันทึกท้าย | required_call_site (ไม่มีเทสต์ DB ในเรพ — ต้องรอ CI/ทดสอบระบบ) |
| **C-01** | P1 | ✅ | `Helpers/OcrCounterpartyMatch.PickBuyerByName` (ชื่อเท่ากันหลัง normalize · หรือส่วนหนึ่งของชื่อ**ลูกค้ารายเดียว** · แก่น < 4 ตัว/กำกวม = ไม่จับ + `[BUYER]`) · `ContactTaxBranchKey.NameCore` (แก่นชื่อตัวเดียวกับ `NameMatchKind`) · `OcrService.ResolveSalesCounterpartyAsync` ตัวเดียว (ใบต้นทาง → คีย์ → SoftScope + ตัวจับคู่ → `AdoptTaxId`) · ถอยไป `MatchedContactId` ได้เฉพาะผู้ใช้เลือกเอง + ไม่ใช่บริษัทเรา (เดิมถอยเงียบ = ใบขายออกให้ตัวเราเอง) | `C01_*` (5 เทสต์ · สองทิศ: RG-03 "แอม แฮปปี้" ยังจับได้ · "บริษัท/จำกัด/บจก./แอม" ไม่จับ · สองราย ไม่เดา · ผู้ขายล้วนไม่ใช่ลูกค้า) · required_call_site `forbid c.Name.Contains(buyerNm)` |
| **C-02** | P1 | ✅ | `OcrLinePreviewResponse.Counterparty` (`OcrLinePreviewCounterparty` — เซิร์ฟเวอร์ตัดสินตามฝั่ง: ขาย = ผู้ซื้อผ่านตัวหาเดียวกับเส้นสร้างเอกสาร (อ่านอย่างเดียว) · ซื้อ = ผู้ขาย/`MatchedContactId`) · `document-scan.html openInDocumentForm` ใช้ `preview.counterparty` | required_call_site `PreviewDocumentLinesAsync` · `node --check` |
| **C-03** | P1 | ✅ | `OcrCounterpartyMatch.NoCounterpartyMessage` (ไทย · ชี้ “ชื่อผู้ซื้อ/ชื่อผู้ขาย” + “จับคู่ผู้ติดต่อ”) `OCR-NO-COUNTERPARTY` · สแกนยังไม่เสร็จ `OCR-SCAN-NOT-COMPLETED` · ไม่พบสแกน `KeyNotFoundException` ไทย | `C03_*` · required_call_site `forbid "Cannot create document: no contact"` |
| **C-05** | P2 | ✅ | `_applyAiCorrections` เติม `revBuyerName` · `revBuyerTaxId` (ด่าน userTouched/ค่าปิดบังเดิมครอบ) · ลบคอมเมนต์ที่ไม่จริง | `node --check` (ไม่มี sim — ตัวเติมเป็น `set()` เดิม) |
| **C-06** | P2 | ✅ | เส้น 1-click ปิดลูป `OurRoleAiFeedbackId` (Implicit · label เทียบ `OurRoleAiSuggested`) · `OcrAiLabelScope.ImplicitMayRecord` — ช่องที่ผู้ใช้แก้เองในหน้ารีวิว (Explicit แล้ว) ไม่ถูกลดชั้นเป็น Implicit (ครอบ `TargetDocumentType` ด้วย) · ตรวจแล้ว `*AiFeedbackId` อื่นบนสแกน: `LineSplitAiFeedbackId` ปิดแล้ว · `GlAccountAiFeedbackId` ปิดผ่านบรรทัด · `AiSuggestionFeedbackId` ปิดแล้ว | `C06_*` · required_call_site |
| **C-07** | P2 | ✅ (สองปุ่มที่รายงาน) | ลงทะเบียนสินทรัพย์ · บันทึก JE เท่านั้น ผ่าน `API.post` (401/403/ไม่ใช่ JSON) · ด่านกันซ้ำ JE อ่านจาก `err.message` | `node --check` · 📋 fetch GET อีก 2 จุด (`ai-feedback/latest` ในแผง AI raw) — จัดการ 404 เอง ไม่อยู่ในรายงาน · checker `fetch('/api/` ในหน้า 📋 |
| **C-08** | P2 | ✅ | `loadWhtIncomeTypes` ไม่แคชความล้มเหลว · `_whtIncomeTypePayload` (ไม่แตะ = null · แตะแล้วว่าง = "") | **sim ใหม่** `tools/wht_income_type_payload_sim.js` (โค้ดจริง · ซอร์ส `9ecff37f` ล้ม 5 ข้อ · negative test 3 แบบ) |
| **C-09** | P2 | ✅ (`InferCurrency`) | `Helpers/OcrCurrencyEvidence` (ย้ายจาก `OcrService.InferCurrency` — ลบตัวเดิม · 3 ผู้เรียก) · หน้ามีทั้งบาท+ต่างประเทศ ⇒ ดูบรรทัดยอดรวม · ตัดสินไม่ได้ ⇒ บาท + `[CURRENCY-UNSURE]` (อยู่ใน `OcrPostingReadiness.BlockingTags` ⇒ ติดไปกับสำเนา · LINE/เว็บแสดง) | `C09_*` (สองครึ่ง) · 📋 `InferCreditNoteReason` / `ResolveHeaderSubTotal` / `MapAzureDocType` + ratchet ของ `ocr_helper_test_check` สำหรับ static ใน `OcrService.cs` (ไม่มีข้อบกพร่องที่ยืนยันในสามตัวนั้น) |
| **C-10** | P3 | ✅ | `MidpointRounding.AwayFromZero` 12 จุดใน `OcrService.cs` + 1 จุด `SmartFieldExtractor.cs` (ทุก `Math.Round` ในสองไฟล์) | **checker ใหม่** `tools/ocr_round_midpoint_check.py` (scope 2 ไฟล์ · negative test ในตัว) |

## กันถดถอย (CLAUDE.md §H) — รันกระดาษจริงก่อน/หลัง

ไม่มี SDK ⇒ ตัวจำลอง Python ของตัวอ่านสกุลเงินเดิม (`InferCurrency`) เทียบตัวใหม่ (`OcrCurrencyEvidence.Read`) บน **ข้อความกระดาษทุกค่าคงที่ใน `Accounting.Tests/*.cs`**
(ต่อสตริงที่ `+` กัน · 263 ใบก่อนเพิ่มเทสต์ของทีมนี้):

| ตัวตัดสิน | ใบที่คำตอบเปลี่ยน | อธิบาย |
|---|---|---|
| สกุลเงิน (C-09) | ค่า**เปลี่ยน 0/263** · ติดธง `[CURRENCY-UNSURE]` 1/263 | ใบสองสกุล (`OcrTotalAnchorTests.C2_ใบสองสกุลเงิน…`) — ยอดรวมมีทั้ง USD และ THB ⇒ "ไม่รู้" (ค่าเดิมบาทคงไว้ + ธงหยุดอนุมัติเอง) = ทิศที่เทสต์ของใบนั้นเองระบุ ("ไม่รู้ ไม่ใช่ขัดกัน") |

ตัวตัดสินอื่นของรอบนี้ไม่อ่านข้อความกระดาษ (สาขา/ผู้ติดต่อ/WHT ตอนอนุมัติ/ชื่อผู้ซื้อกับรายชื่อผู้ติดต่อ) — ทิศที่ถูกอยู่แล้วล็อกด้วยเทสต์ครึ่งหลังทุกหัวข้อ
(ใบ Makro ที่ส่งค่าเดิม · เอกสารที่สร้างจากสแกนไม่มีคนแตะ · ชื่อที่ถูกตัด "แอม แฮปปี้" ยังจับลูกค้ารายเดิม)

## F3 ข้อ 7–12

7. รูปแบบเดิมทั้งเรพ: `vendorKeyChanged` นิพจน์มือ 1 → 0 (helper) · `documentCreated: CreatedDocumentId.HasValue` 1 → 0 · `existing.TaxId = extractedData.VendorTaxId` (สแกน) 1 → 0 ·
   เส้นเติมเลขเข้าแถวเดิมใน OCR 3/3 อยู่ใต้ล็อก · `c.Name.Contains(buyerNm)` 1 → 0 · `contactId ??= result.MatchedContactId` 1 → 0 · `InferCurrency(` ผู้เรียก 3 → 0 (ลบเมธอด) ·
   `Math.Round` ไม่ระบุ midpoint ในสองไฟล์ OCR 13 → 0 (ทั้งเรพยังเหลือ ~250 นอก scope — ไม่กวาด F4 ข้อ 3) · `fetch(` ในหน้าสแกน 4 → 2 (GET แผง AI raw)
8. ทางเข้าอื่น: LINE / มือถือ / API v1 autoCreate สร้างเอกสารผ่าน `CreateDocumentFromScanAsync` ตัวเดียว ⇒ ได้ C-01/C-03/C-06/R6 ครบ · อนุมัติทุกทางเข้า (เว็บ/ลายเซ็น/มือถือ/
   LINE) ผ่าน `ApproveDocumentAsync` ⇒ ข้อ 28 ครบ (TryTrain ของ SignatureApproval/Mobile ถูกเรียกซ้ำหลังนั้นแต่ idempotent — `OcrIntelTrainedAt`) · ข้อ 29 ผู้เขียนเดียว
   (`SubmitCorrectionAsync`) · เส้น integration/CMS สร้างผู้ติดต่อผ่านตัวอื่น (นอกขอบเขตข้อ 19)
9. เข้มขึ้น + ทางไปต่อ: (ก) ใบขายที่อ่านผู้ซื้อไม่ได้และไม่ได้เลือกผู้ติดต่อ ⇒ **บล็อก**ด้วยข้อความไทย (เดิมผูกบริษัทเราเงียบ) — ทางไปต่อ: กรอก “ชื่อผู้ซื้อ”
   หรือเลือกใน “จับคู่ผู้ติดต่อ” (`C03_*`) (ข) ชื่อผู้ซื้อสั้น/กำกวม ⇒ สร้างลูกค้าใหม่ + `[BUYER]` (เดิมเดาเงียบ) — ทางไปต่อ: เลือกลูกค้าในเอกสาร/รวมผู้ติดต่อซ้ำ
   (ทิศตรงข้าม `C01_TruncatedBuyerName_*`) (ค) สกุลเงินกำกวม ⇒ หยุดอนุมัติเอง (`C09_TwoCurrencyPaper_*`) — ผู้ใช้อนุมัติเองได้ (ง) ที่อยู่แถวสาขาของสแกนเก่า ⇒ ว่าง + ข้อความ
   (`Addr29_*`) (จ) เติมเลขภาษีชนแถวอื่น ⇒ ไม่เติม + `[Enrich]` (`R6_*`)
10. ค่าที่ persist ก่อนแก้: ข้อ 29 **ไม่แก้ข้อมูลเก่าโดยคำตัดสิน** (DEFAULT false = ไม่รู้) · ข้อ 28 ไม่ backfill (เอกสารที่อนุมัติไปแล้วเรียนไปแล้ว/ไม่เรียนแล้ว — `OcrIntelTrainedAt`
    กันเรียนซ้ำ; backfill `VendorIntelligenceService` อ่าน `UserCorrectedFields` ที่ sync เขียนหลังจากนี้) · `WhtIncomeTypeCode` ที่ถูกล้างเงียบไปแล้ว (C-08) แยกไม่ได้จากที่ผู้ใช้ล้างเอง ⇒ ไม่ migrate ·
    ลูกค้าที่ถูกผูกผิดด้วย substring (C-01) แยกไม่ได้ ⇒ ไม่ migrate (หน้า contact-hygiene/เอกสารแก้ได้) · สกุลเงินของสแกนเก่าไม่คำนวณใหม่ (อ่านเฉพาะตอนสแกน/สร้าง)
11. แตะเงิน/ภาษี (WHT learning · ผู้ติดต่อ AR) — **ยังไม่ได้ส่ง subagent ฝ่ายค้าน** (ทีมนี้เป็น subagent) ⇒ main agent ควรส่ง diff ให้ฝ่ายค้าน 1 รอบ
12. DOCUMENT_FLOW §1 OCR (บล็อก K2) + §2.2 วงจร WHT (คนแก้ในฟอร์มเอกสาร · ลำดับ sync → train) + Last verified · TEST_PLAN §0 (387 ไฟล์) + OCR-U-24..29 · CHANGELOG ·
    `docs/lessons/ocr-pipeline.md` (5 ข้อ) · ACCOUNT_STRUCTURE ไม่ต้องขยับ

## เครื่องมือที่รันแล้ว (ไม่มี .NET SDK)

- `required_call_site_check` (รวม negative test ในตัว) · `ocr_helper_test_check` (65 คลาส ไม่มีเทสต์ 0) · `record_arg_check` · `nullable_arg_check` · `using_check` ·
  `advisory_lock_key_check` · `html_attr_escape_check` · `string_quote_close_check` · `comment_line_break_check` · `identifier_space_check` · `tuple_name_merge_check` ·
  `namespace_shadow_check` · `dead_helper_check` · `regex_line_span_check` · `accessibility_check` · `arg_type_check` · `ocr_round_midpoint_check` (ใหม่) ·
  `test_inventory --check` · `node tools/wht_income_type_payload_sim.js` (ใหม่) · `node --check` ทุก `<script>` ใน `document-scan.html` · brace-balance ทุก .cs ที่แก้ = 0
- ⚠️ `undeclared_local_check` · `contact_taxid_only_match_check` (และ `attachment_gate_check` · `upload_route_check`) **ข้ามทุก path ที่มี `.claude`** ⇒ ใน worktree ของ
  agent ขึ้น "ตรวจ 0 ไฟล์" (เขียวปลอม) — รันบนสำเนานอก `.claude` แล้ว: undeclared_local 1553 ไฟล์ 0 จุด · contact_taxid_only 3 = baseline 3
- ผลรวม `bash tools/check_all.sh` — ดูหัวข้อ "ผล check_all" ท้ายไฟล์

## ความเสี่ยงคอมไพล์ที่เหลือ

- `var contacts = forPreview ? _db.Contacts.AsNoTracking() : _db.Contacts;` (IQueryable vs DbSet — แปลงทางเดียว ⇒ ชนิด IQueryable) · `SoftScope(contacts, …)` เลือก overload IQueryable
- tuple return `(Guid? ContactId, Contact? AdoptedRow, string? Note)` คืน `(predecessorContactId, null, null)` / `(row.Id, cond ? row : null, pick.Note)`
- `catch (Exception) when (contactCreateTx != null)` รอบบล็อกสร้างผู้ติดต่อ (~170 บรรทัด ไม่ re-indent — ตรวจแล้วไม่มีตัวแปรที่ประกาศในบล็อกถูกใช้หลังบล็อก · ไม่มี `return`)
- `adoptedEntry.CurrentValues.SetValues(adoptedEntry.OriginalValues)` แล้ว `State = Unchanged` (ถอนการเติมเลขที่ยังไม่บันทึก)
- `lines[i].Any(char.IsDigit)` (method group → `Func<char,bool>`)
- `OcrLinePreviewResponse(..., Counterparty: counterparty)` named optional ท้าย record
- EF: คอลัมน์ใหม่ `VendorAddressUserTyped` (bool NOT NULL DEFAULT false) — convention mapping

## คำถามค้าง (ทิศที่เลือก = มองเห็นและย้อนได้)

- **Q1 (C-01 ฝั่งขาย ถอยสุดท้าย)** ใบขายที่อ่านผู้ซื้อไม่ได้เลย: เดิมผูก `MatchedContactId` (ฝั่งผู้ขาย = มักเป็นเรา) เงียบ · ตอนนี้ผูกเฉพาะเมื่อผู้ใช้เลือกเองและไม่ใช่บริษัทเรา
  ไม่งั้นบล็อก + บอกทาง — ถ้าเจ้าของต้องการ "สร้างเป็นลูกค้าทั่วไป (walk-in)" แทนการบล็อก ต้องตัดสิน (ใบกำกับเต็มรูปต้องมีชื่อผู้ซื้อ §86/4)
- **Q2 (C-06)** `ImplicitMayRecord` ครอบ `TargetDocumentType` ด้วย (เดิมเส้น 1-click ทับ Explicit ของหน้ารีวิวเป็น Implicit) — เปลี่ยนพฤติกรรมเดิมเล็กน้อยในทิศที่ถูก
- **Q3 (ข้อ 28)** WHT ฝั่งขาย (ลูกค้าหักเรา) ไม่นับ — ประวัติ WHT เป็นของผู้ขาย · ถ้าต้องการ KPI "ช่องที่ถูกแก้" ของฝั่งขายด้วย ต้องแยกรหัสประเภทเงินได้ (ฝั่งขายไม่เติมลงบรรทัด)
- ค้างจากทีม K (Q1–Q4 ใน `team-K.md`) ไม่แตะ

## ผล check_all

`bash tools/check_all.sh` (ไม่มี SDK): checker ทุกตัวผ่าน **ยกเว้น 1 แถวของ `required_call_site_check` ที่ไม่ใช่ของทีมนี้** —
`Services/Settlement/SettlementPostingService.cs BuildGateAsync ไม่เรียก c.CountryCode` (แถวของทีม W · โค้ดที่ HEAD `9ecff37f` ใช้ `counterparty?.CountryCode` ·
ทีมนี้ไม่แตะไฟล์นั้น — ล้มตั้งแต่ก่อนรอบนี้ ⇒ main agent ต้องปรับแถวของทีม W) · simulation 10 ตัวผ่าน (รวม sim ใหม่) · brace/node/U+FFFD 31 ไฟล์ผ่าน ·
TEST_PLAN §0 ตรง · แถวใหม่ของทีมนี้ทั้ง 9 + แถวที่ปรับ 2 (CreateDocumentFromScanCoreAsync รอบ 197 → `ResolveSalesCounterpartyAsync` · `ApproveDocumentAsync#2`) ผ่าน
