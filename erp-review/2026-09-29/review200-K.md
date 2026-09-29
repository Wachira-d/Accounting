# รอบ 200 — ฝ่ายค้านทีม K (OCR ผู้ติดต่อสาขา / ใบ Makro)

diff: `5c1fe028..worktree-agent-a3dc3a4026cab58e1` (`53dc5921` + `ae28d49a`) · อ่านอย่างเดียว · ไม่มี .NET SDK (ไม่ได้คอมไพล์/รันเทสต์)
เลขบรรทัดอ้างไฟล์บน branch `worktree-agent-a3dc3a4026cab58e1` (OcrService.cs = รุ่นบน branch)

static checker ที่รันบนสำเนา branch (git archive ลง scratchpad): using · record_arg · nullable_arg · tuple_name_merge · undeclared_local ·
advisory_lock_key · namespace_shadow = 0 ทุกตัว · `required_call_site_check` ถูกตัดที่ timeout 580 วิ (exit 143 · มีหลาย agent รันพร้อมกัน) — ไม่มีผล ต้องรันซ้ำ

## สรุป

| # | ข้อ | ผล | P |
|---|---|---|---|
| R1 | K-4: ผู้ใช้ **พิมพ์ยืนยันรหัสสาขาเดิม** (ตามที่ข้อความ `OtherBranchRow` สั่ง) แล้วกด "แก้ในฟอร์มก่อน" ⇒ ไม่ตัดสินใหม่ ฟอร์มได้แถว สนญ. | **CONFIRMED** | P2 |
| R2 | K-10 ทิศตรงข้าม: WHT ที่ผู้ใช้ใส่/แก้ใน**ฟอร์มเอกสาร** (ไม่ใช่หน้ารีวิว) ไม่ถูกนับว่า "คนแตะ" ⇒ ไม่เคยถูกเรียน · team-K.md ข้อ 9 เขียนว่า "แก้ในหน้ารีวิว**หรือเอกสาร** (นับทันที)" ซึ่งไม่จริงสำหรับ "เอกสาร" | **CONFIRMED** | P2 |
| R3 | K-9: สแกนที่เคยบันทึกผ่านหน้ารีวิว**ก่อน**รอบนี้มี `"VendorAddress"` ใน `UserCorrectedFields` อยู่แล้ว (กติกาเดิม "ส่งมา = แก้") ⇒ ถือว่า "ผู้ใช้พิมพ์ที่อยู่เอง" ⇒ ที่อยู่หัวกระดาษ (สนญ.) ยังลงแถวสาขาใหม่ | **CONFIRMED** (ข้อมูล persist · ไม่มี migration) | P3 |
| R4 | K-4: แก้ผลสแกน = สร้างผู้ติดต่อแถวสาขา**ถาวรทันที** (ไม่ใช่ตอนสร้างเอกสาร) ⇒ พิมพ์รหัสผิดแล้วกดบันทึก → แถวขยะค้าง (แก้รอบถัดไปสร้างอีกแถว ไม่ลบแถวเดิม) | PLAUSIBLE | P3 |
| R5 | K-4: `documentCreated` ดูแค่ `CreatedDocumentId` — สแกนที่ลงเป็น JE อย่างเดียว (`CreatedJournalEntryId`) ยังถูกเปลี่ยนผู้ติดต่อได้ | PLAUSIBLE | P3 |
| R6 | K-5: ทางที่ "ผูกเลขภาษีเข้าแถวเดิม" (backfill TaxId ฝั่งสแกนหลัง commit · AdoptTaxId ฝั่งขาย · backfill ฝั่งซื้อในเส้นสร้างเอกสาร) ไม่อยู่ใต้ล็อก ⇒ ยังเกิดคีย์ซ้ำได้ในจังหวะแข่ง | PLAUSIBLE | P3 |
| R7 | K-5: Branch 2 เลขไม่ผ่าน checksum ⇒ ล็อกด้วยเลขนั้น แต่แถวที่สร้างเก็บ `TaxId = null` ⇒ `FindAsync` หลังล็อกมองไม่เห็น ⇒ ล็อกไม่กันซ้ำ (ไม่แย่กว่าเดิม) | PLAUSIBLE | P3 |
| R8 | K-5: สแกนที่ล้ม**หลัง** SaveChanges ในธุรกรรมสั้นแต่ก่อน Commit (เช่น commit ล้ม) ⇒ rollback แต่ change tracker ยอมรับแถวแล้ว ⇒ บันทึกท้าย `ScanAsync` อ้าง `MatchedContactId` ของแถวที่ไม่มีจริง | PLAUSIBLE (หน้าต่างแคบมาก) | P3 |
| — | K-5 ธุรกรรม/ล็อก/deadlock/คีย์/ธุรกรรมซ้อน | NOT-A-BUG | — |
| — | K-10 baseline = ค่าที่เก็บก่อนรับคำแก้ (รวมคำแก้รอบก่อน) | NOT-A-BUG | — |
| — | K-4 ห้ามแตะสแกนที่สร้างเอกสารแล้ว / ผู้ใช้เลือกผู้ติดต่อเอง / ฝั่งขาย | NOT-A-BUG | — |
| — | K-3b raw SQL `= ANY({1})` + `Guid[]` · ขอบเขตนิติบุคคล · tenant | NOT-A-BUG | — |
| — | K-11 / K-8 กระดาษที่ถูกอยู่แล้ว | NOT-A-BUG (มีข้อสังเกต P3) | — |
| — | คอมไพล์ (record projection · local function → `Func<…>?` · using · ชื่อซ้ำในเมธอด) | NOT-A-BUG | — |

---

## CONFIRMED

### R1 (P2) — K-4 ไม่ครอบเคส "พิมพ์ยืนยันรหัสเดิม" ⇒ เส้น "แก้ในฟอร์มก่อน" กับเส้น "สร้างเอกสาร" ให้ผู้ติดต่อคนละแถว

- `OcrService.cs:5069-5073` — `vendorKeyChanged` = สาขา**เปลี่ยนค่า** (`BranchChanged`) หรือเลขภาษีเปลี่ยน · **ไม่ดู `correction.VendorBranchConfirmed`**
- แต่ `OcrCorrectedFieldList.From` นับ `VendorBranchCode` เมื่อ `VendorBranchConfirmed == true` (ช่องที่ผู้ใช้พิมพ์เองแม้ค่าเดิม) ⇒ เส้นสร้างเอกสาร
  (`DecideScanVendorBranchContactAsync` → `IsReliableBranch(userCorrected: true)`) ตัดสิน `NewBranchRow` ส่วนเส้นแก้ผลสแกนไม่ตัดสินเลย
- ฉาก: สแกนใบ Makro 00005 ได้คะแนนสาขา 0.50 (ขัดประโยค) ⇒ `OtherBranchRow` ผูก สนญ. + trace บอกผู้ใช้ "พิมพ์รหัสสาขาในช่อง “รหัสสาขาผู้ขาย” ใหม่
  (หรือแก้ให้ถูก)" (`OcrVendorBranchContact.cs` ข้อความ OtherBranchRow) · ผู้ใช้พิมพ์ `00005` ซ้ำ (ค่าเดิม ⇒ `vendorBranchConfirmed: true`) แล้วกดปุ่มหลัก
  **"📝 แก้ในฟอร์มก่อน"** (`document-scan.html` `openInDocumentForm` → `_persistReviewEdits` → โหลดสแกนใหม่ → `contactId: scan.matchedContactId`)
  ⇒ ฟอร์มได้แถว **สนญ.** ⇒ เอกสารสร้างผ่านฟอร์ม (ไม่ผ่าน `CreateDocumentFromScanCoreAsync`) ผูกสาขาผิด (§86/4 · ประกาศฯ 199) — ถ้ากด "สร้างเอกสาร" ได้แถว 00005
- ญาติกัน: สแกนรุ่นก่อน 197 ที่ `MatchedContactId` ผูก สนญ. ไว้ — เส้นสร้างเอกสารตัดสินใหม่ทุกครั้ง แต่เส้นฟอร์มตัดสินเฉพาะเมื่อผู้ใช้**เปลี่ยน**ค่า
  ⇒ "สามเส้นที่ผลิตของชิ้นเดียวกันต้องเรียกตัวสร้างตัวเดียว" (CLAUDE.md §H) ยังไม่ครบ: ตัวสร้างตัวเดียวแล้ว แต่**เงื่อนไขเรียก**ต่างกัน
- เทสต์ `Makro_UserChangesBranchInReview_FormGetsTheBranchRow` ครอบเฉพาะ 00000 → 00005 (ค่าเปลี่ยน) ไม่มีเคสยืนยันค่าเดิม
- ทางแก้ที่เสนอ: `vendorKeyChanged ||= correction.VendorBranchConfirmed == true` (หรือให้ `ShouldRedecideOnCorrection` รับ "สาขาอยู่ในช่องที่ผู้ใช้ยืนยัน")
  + เทสต์ทิศตรงข้าม "ส่งค่าเดิมโดยไม่แตะ ⇒ ไม่ตัดสินใหม่" (มีแล้ว)

### R2 (P2) — K-10 ทิศตรงข้าม: WHT ที่คนใส่ในฟอร์มเอกสารหายจากวงจรเรียนรู้ · และข้อความ "ทางไปต่อ" ใน team-K.md ไม่จริง

- ตัวตัดสิน: `VendorIntelligenceService.cs:536-567` เรียน WHT ตอนอนุมัติเมื่อ `OcrWhtLearningScope.Decide(...)` = Paper หรือ `UserEdited`
  (`UserEdited` = มี `HasWht/WhtRate/WhtIncomeTypeCode` ใน `scan.UserCorrectedFields`)
- ผู้เขียน `UserCorrectedFields` มีสองที่: `SubmitCorrectionAsync` (หน้ารีวิว) และ `DocumentService.cs:13518-13534` (sync สแกนตอนอนุมัติจาก `OcrPostedTruth.Diff`)
  — ตัวหลัง**ไม่มีช่อง WHT เลย** (`OcrPostedTruth` ไม่มี wht · `touched` มีแค่ชื่อ/เลข/สาขา/เลขที่/วันที่/ยอด/ชนิด/บรรทัด)
- ก่อนรอบนี้: หน้ารีวิวส่ง `hasWht` ทุกครั้ง ⇒ ทุกสแกนที่เปิดรีวิวบนเว็บเป็น `UserEdited` ⇒ WHT ที่ผู้ใช้**พิมพ์ในฟอร์มเอกสาร**ถูกเรียน "โดยบังเอิญ"
  (พร้อมกับค่าที่ระบบเติมเอง = เสียงรบกวนที่คำตัดสินข้อ 19 สั่งปิด)
- หลังรอบนี้ (ถูกตามคำตัดสิน) ค่าที่ระบบเติมไม่ถูกเรียนแล้ว — แต่**ค่าที่คนพิมพ์ในฟอร์มเอกสารก็ไม่ถูกเรียนด้วย** (ไม่มีทางไหนบอกว่า "คนแตะ WHT ที่ฟอร์ม")
  ⇒ ฉาก: ใบค่าบริการที่กระดาษไม่พิมพ์ WHT · ผู้ใช้กด "แก้ในฟอร์มก่อน" (ปุ่มหลัก) → ติ๊กหัก 3% ในฟอร์ม → อนุมัติ ⇒ `SystemSuggestedOnly` ⇒ ใบถัดไปของผู้ขาย
  รายนี้ไม่มีประวัติ WHT ⇒ ผู้ใช้ต้องใส่เองทุกใบ (ไม่เคยดีขึ้น) · นี่คือทรง CLAUDE.md §H ข้อแรก "ด่านที่ถอดการนับทับ อาจกำลังถอดตัวซ่อมของอีกบั๊กที่ไม่มีใครรู้"
- `erp-review/2026-09-29/team-K.md:52` เขียน "ทางไปต่อ: แก้/ติ๊กช่อง WHT ในหน้ารีวิว**หรือเอกสาร** (นับทันที)" — ครึ่ง "หรือเอกสาร" ไม่จริง (F2 ข้อ 8: ทางไปต่อที่อ้างต้องมีจริง)
- ทางแก้ที่เสนอ (ต้องให้เจ้าของเห็นก่อน เพราะแตะ D3-2): ฝั่งเอกสารต้องมีสัญญาณ "คนแตะ WHT" ของตัวเอง (เช่น ธงต่อบรรทัด/ต่อเอกสารที่ฟอร์มส่งเมื่อ
  `userTouched` ช่อง WHT แบบ `dataset.userTouched` ที่หน้ารีวิวใช้กับสาขา) แล้วให้ `OcrPostedTruth`/sync ตอนอนุมัติ merge `"HasWht"` เข้า `UserCorrectedFields`
  เมื่อ WHT บนเอกสาร ≠ ค่าที่ฟอร์มได้รับจากสแกน/ข้อเสนอ · อย่างน้อยแก้ข้อความใน team-K.md ให้ตรงความจริง

### R3 (P3) — K-9 ไม่มีผลกับสแกนที่รีวิวไปแล้วก่อน deploy

- `OcrService.cs:3437-3440` — `userCorrectedAddress: correctedFields.Contains("VendorAddress")` อ่านจาก `result.UserCorrectedFields` ที่ **persist ไว้แล้ว**
- กติกาเดิม (ก่อนรอบนี้) `Add("VendorAddress", c.VendorAddress)` = ส่งมา = แก้ และหน้ารีวิวส่ง `vendorAddress: v('revVendorAddress')` ทุกครั้งที่ช่องมีค่า
  ⇒ สแกนแทบทุกใบที่เคยกดบันทึก/แก้ในฟอร์มก่อน มี `"VendorAddress"` ติดอยู่ ⇒ `StoredAddressIsIssuerBranch` คืน true ⇒ ที่อยู่หัวกระดาษ (มักเป็น สนญ.)
  ลงแถวสาขาใหม่ — ตรงข้ามกับเป้าหมาย K-9 เฉพาะกลุ่มสแกนค้างที่ยังไม่สร้างเอกสาร
- team-K.md ข้อ 10 พูดถึง "HasWht"/"VendorAddress" ในแถวเก่าในแง่ WHT learning แต่ไม่ได้ระบุผลต่อ K-9 · ช่วงเปลี่ยนผ่านอย่างเดียว (สแกนใหม่ถูก) ⇒ P3
- ทางเลือก: ผูกเงื่อนไข "ผู้ใช้พิมพ์ที่อยู่" กับ `UserCorrectedAt >= วัน deploy` หรือยอมรับแล้วจดใน DOCUMENT_FLOW ว่าสแกนค้างเก่ายังได้ที่อยู่กระดาษ

---

## PLAUSIBLE

### R4 (P3) — แก้ผลสแกน = สร้างผู้ติดต่อถาวร
`OcrService.cs:5093-5112` — `SubmitCorrectionAsync` ถูกเรียกทั้งปุ่ม "บันทึกการแก้ไข" และ "แก้ในฟอร์มก่อน" ⇒ รหัสสาขาที่พิมพ์ผิด (00050) + บันทึก ⇒ แถว
"สาขาที่ 50" เกิดถาวร (`CreatedBy = OCR-BranchAutoCreate`) · แก้เป็น 00005 รอบถัดไป ⇒ แถวใหม่อีกแถว แถวแรกค้าง (ไม่มีเอกสาร · ไม่ถูกรายงานเพราะคีย์ไม่ซ้ำ)
ผู้ใช้เห็นข้อความ `[Auto-Create] … ตอนแก้ผลสแกน` (มองเห็นได้ ⇒ P3) · สอดคล้องกับการออกแบบเส้นสแกน (Branch 0 ก็สร้างตอนสแกน) จึงไม่ใช่บั๊กชัด —
ถ้าจะเข้มขึ้น: สร้างแถวเฉพาะเส้น "แก้ในฟอร์มก่อน" หรือให้หน้าผู้ติดต่อรายงานแถว OCR ที่ไม่มีเอกสารผูก

### R5 (P3) — สแกนที่ลง JE อย่างเดียว
`OcrService.cs:5081` `documentCreated: result.CreatedDocumentId.HasValue` — สแกนที่มี `CreatedJournalEntryId` (JE-only path) ยังถูกเปลี่ยน `MatchedContactId`
เมื่อแก้สาขา ⇒ สแกนกับ JE ที่ลงไปแล้วชี้ผู้ติดต่อคนละแถว (ไม่ได้ตรวจว่า JE-only เก็บ ContactId จากสแกนไหม — ถ้าเก็บ = ขัดกัน)

### R6 (P3) — ทางเขียนเลขภาษีเข้าแถวเดิมอยู่นอกล็อก K-5
- สแกน: backfill TaxId/BranchCode หลัง `contactCreateTx.CommitAsync()` (`OcrService.cs:2950` → `:2981`, `:3007`)
- สร้างเอกสาร: `AdoptTaxId` ฝั่งขาย (`:6384`) · backfill ฝั่งซื้อ `existing.TaxId = …` (`:6490`) — ทั้งสองไม่ตั้ง `pendingNewContact` ⇒ ไม่ล็อก
- ฉาก: A จับผู้ขายด้วยชื่อ (แถวยังไม่มีเลข) แล้วเติมเลข T · B พร้อมกันไม่เจอเลข T และชื่อไม่ผ่านเกณฑ์ ⇒ สร้างแถวใหม่เลข T ⇒ คีย์ T|สาขาซ้ำ
  หน้าต่างแคบ และรายงาน contact-hygiene ใหม่จะจับได้ ⇒ P3 · คำตัดสินข้อ 19 พูดถึง "ตอนสร้าง" จึงอยู่นอกขอบเขตได้ — ควรจดใน team-K.md ว่าไม่ครอบ

### R7 (P3) — Branch 2 เลข checksum ไม่ผ่าน
`OcrService.cs:2898` `TaxId = hasValidTaxId ? … : null` แต่ `lockContactCreate` ใช้ `LockPart(extractedData.VendorTaxId)` (≥ 10 หลัก ไม่ดู checksum) ⇒
ล็อกจริง แต่ `FindAsync(tax)` หลังล็อกหาแถวที่เก็บ `TaxId = null` ไม่เจอ ⇒ สองคำขอพร้อมกันยังได้สองแถว (ชื่อเดียวกัน ไม่มีเลข) — เท่ากับพฤติกรรมเดิม ไม่ถอย

### R8 (P3) — rollback หลัง SaveChanges ในธุรกรรมสั้นของสแกน
`OcrService.cs:2774-2950` อยู่ใน `try` ใหญ่ของ `ScanAsync` (`:242`–`:3309`) · ถ้า `CommitAsync` ล้ม (connection หลุด) ⇒ `await using` dispose ⇒ rollback ·
แต่ EF `AcceptChanges` แถวผู้ติดต่อ/การแก้ `scanResult` ไปแล้ว ⇒ catch ตั้ง Failed แล้ว `SaveChangesAsync` ท้ายเมธอด (`:3366`) เขียน `MatchedContactId` ที่ชี้แถวที่
ไม่มีจริง (FK ล้ม ⇒ 500 · สแกนค้าง "Processing") · ก่อนรอบนี้ save เป็น autocommit ⇒ ไม่มีหน้าต่างนี้ · หน้าต่างแคบมาก (ระหว่าง SaveChanges กับ Commit
มีแค่ logging/สตริง) ⇒ P3

ข้อสังเกตประกอบ (ไม่ใช่บั๊กวันนี้): `contactCreateTx` commit ที่ `:2950` แต่ dispose ตอนจบ `try` — `RelationalTransaction.Dispose` เรียก
`Connection.UseTransaction(null)` ซ้ำ ⇒ ถ้าวันหน้ามีคนเปิดธุรกรรมใหม่ที่ยัง**ค้างอยู่**ตอนจบ `try` (เช่นย้าย `await using var txn` ของเส้น autoCreate ขึ้นมาระดับเดียวกัน)
ธุรกรรมใหม่จะถูกปลดจาก context เงียบ ๆ · วันนี้ปลอดภัยเพราะ `CreateDocumentFromScanCoreAsync` ปิด `txn` ในเมธอดของตัวเองก่อน · ถ้าจะให้ทน: ห่อบล็อกสร้างด้วย
`{ … }` ของตัวเอง หรือ `await contactCreateTx.DisposeAsync()` ต่อจาก commit

---

## NOT-A-BUG (ตรวจแล้ว)

### K-5 ล็อก/ธุรกรรม
- **ในธุรกรรมจริงทุกเส้น**: สแกน `:2774` เปิดเมื่อ `CurrentTransaction == null` · สร้างเอกสาร `:6682` เปิดเสมอแล้วล็อก `:6686` · แก้ผลสแกน `:5099` · ตัวล็อก
  `LockAndFindConcurrentOcrContactAsync` (`:3475-3496`) throw ถ้านอกธุรกรรม — ทุกผู้เรียกเปิดก่อน ⇒ throw ไม่ถึงผู้ใช้
- **ธุรกรรมซ้อน**: ทุกจุดเช็ค `CurrentTransaction == null` ก่อน `BeginTransactionAsync` (ถ้ามีธุรกรรมนอก ⇒ `null` ⇒ ล็อกผูกธุรกรรมนอก ถือจนนอก commit) ⇒ ไม่ throw ·
  `cond ? await BeginTransactionAsync() : null` ชนิด `IDbContextTransaction?` + `await using` บน null = ข้าม ⇒ คอมไพล์ได้ · ไม่มี `EnableRetryOnFailure` ใน `Program.cs:80-93`
  ⇒ ไม่ชน execution strategy
- **commit ก่อน autoCreate**: `:2950` commit ก่อนถึง `CreateDocumentFromScanAsync` ใน `try` เดียวกัน และ EF เคลียร์ `CurrentTransaction` ตอน commit ⇒ `BeginTransactionAsync`
  ของเส้นสร้างเอกสารไม่ชน · ไม่มี `return` ระหว่าง `:2774`–`:2950`
- **คีย์คงที่ข้ามเครื่อง**: `AdvisoryLockKey.For(companyId, "ocr-contact-create", digits)` FNV-1a · `LockPart` = ตัวเลขล้วน ⇒ "0 10 7 567 00041 4" ≡ "0107567000414" ·
  ศูนย์ล้วน/< 10 หลัก/ว่าง ⇒ ไม่ล็อก (เทสต์ `LockPart_*`)
- **ไม่มีเลขภาษี**: ไม่ล็อก (ไม่มีกุญแจ) — ตรงคำตัดสินข้อ 19 (ล็อกต่อ TaxId) · ผลเท่าเดิม (R7 คือกรณีขอบ)
- **deadlock**: ทั้งสามเส้น `pg_advisory_xact_lock` เป็นคำสั่ง**แรก**หลัง BEGIN (สแกน: หลัง Begin ทันที · สร้างเอกสาร: `:6686` ก่อนเขียนอะไร · แก้ผลสแกน: ก่อน
  `SaveChangesAsync` `:5111`) ⇒ ผู้รอไม่ถือ row lock ใด ⇒ ไม่เกิดวง · ล็อกอื่นที่อาจตามมาในธุรกรรมสร้างเอกสาร (doc-seq/JE ถ้า auto-approve) ถูกถือ**หลัง**ล็อกนี้
  เสมอ และไม่มีเส้นไหนถือล็อกเหล่านั้นแล้วมาขอล็อกนี้ ⇒ ลำดับเดียว
- **ถามซ้ำหลังล็อก**: `FindAsync` เป็นคำสั่งใหม่หลังได้ล็อก (READ COMMITTED เห็นแถวที่อีกคำขอ commit แล้ว) · `ReuseAfterLock` ใช้คีย์กลาง (สาขาระบุ ⇒ ตรงสาขาเท่านั้น ·
  ไม่ระบุ ⇒ กติกา "ไม่รู้สาขา" เดียวกับ `Decide`) ⇒ ไม่ย้อนผลรอบ 197 (ไม่หยิบ สนญ. ให้ใบสาขา)
- **SaveChanges อื่นถูกรวมเข้าธุรกรรม**: ในช่วง `:2774-2950` มีแค่ SaveChanges ของ Branch 0/1/2 (flush `scanResult` + ผู้ติดต่อ) — commit สำเร็จ = ผลเท่าเดิม ·
  ต่างเฉพาะทาง rollback (R8)
- **เส้นสร้างเอกสาร**: ไม่มี SaveChanges ระหว่าง `_db.Contacts.Add` กับ `BeginTransactionAsync` (`:6478-6682`) ⇒ แถวยังไม่ถูก INSERT ก่อนล็อก · ถอดแถวด้วย
  `EntityState.Detached` แล้วสลับ `contactId` ก่อนผู้ใช้ `contactId` ตัวถัดไป (`:6709` · `:6742` · `:6948`)

### K-10 / K-9 baseline
- baseline = ค่าบนแถวสแกน**ก่อน**รับคำแก้ของคำขอนี้ (`:4904-4907`) — ถ้ารอบก่อนแก้ไปแล้ว ช่องนั้นถูก `Merge` ไว้ใน `UserCorrectedFields` แล้ว (สะสม ไม่ลบ) ⇒ รอบนี้
  ส่งค่าเดิมกลับมาไม่ทำให้หาย · แก้กลับเป็นค่าสแกนเดิม = นับ (ถูก: คนลงมือ)
- "ผู้ใช้ตั้งใจยืนยันค่าเดิม" ของ WHT: หลัง D3-2 `scan.HasWht` = อ่านจากกระดาษ ⇒ ยืนยันค่ากระดาษ = `Paper` เรียนอยู่แล้ว · ยืนยัน "ไม่หัก" = ไม่มีอะไรให้เรียน
  (VendorIntel เรียนเฉพาะ `WithholdingTaxAmount > 0`) · ปุ่ม "ใช้อัตรานี้" (`applySuggestedWht` ฝั่ง JS) เปลี่ยนค่า ⇒ นับ ⇒ ไม่มีช่องที่ "ยืนยันแต่ไม่นับ"
  ในหน้ารีวิว — ช่องที่หายคือฟอร์มเอกสาร (R2)
- `WhtRate`: select ว่าง → `Layout.numOrNull` = null ⇒ ไม่นับ/ไม่ทับ · อัตราที่ไม่มีใน option (1.5) แสดง "-" ⇒ null ⇒ ไม่ทับค่าเดิม
- ข้อสังเกตเดิม (ไม่ใช่ของรอบนี้): `loadWhtIncomeTypes` ล้ม ⇒ select มีแต่ "" ⇒ ส่ง "" ⇒ ล้าง `WhtIncomeTypeCode` ที่เก็บไว้ (และตอนนี้นับเป็นคำแก้) — P3 เดิม

### K-4
- สแกนที่สร้างเอกสารแล้ว: `ShouldRedecideOnCorrection(documentCreated: CreatedDocumentId.HasValue)` ⇒ ไม่แตะ (ดู R5 สำหรับ JE-only)
- `contactPickedByUser` (`:5076-5078`) ⇒ ไม่ตัดสินใหม่ · และใน `DecideScanVendorBranchContactAsync` (`:3411`, `:3416`) ผู้ใช้เลือกเองชนะอีกชั้น
- ฝั่งขาย/ผู้ขายคือเรา/ไม่มีเลข ⇒ ไม่แตะ (`:5082`, `:5088-5090`)

### K-3b
- `SqlQueryRaw<VendorHistoryRow>(sql, companyId, vendorIds.ToArray())` + `= ANY({1})` — รูปเดียวกับที่ใช้จริงแล้วในเส้นเงิน `DocumentService.cs:12559`
  (`FromSqlRaw(… ANY({0}) …, docIds.ToArray(), companyId)`) และ `:16607` ⇒ Npgsql map `Guid[]`→`uuid[]` ใช้งานได้ ⇒ ไม่เงียบใน catch
- ขอบเขต: `ContactTaxBranchKey.SameEntityIdsAsync` (กรอง `CompanyId` · เลขต้องใช้ได้ `IsUsableTaxId` · walk-in = ตัวเอง) ⇒ ไม่ข้ามไปผู้ขายไม่มีเลข/เลขศูนย์ ·
  SQL มี `d."CompanyId" = {0}` · alias/negative alias กรอง `CompanyId` · แคช `_vendorIdsCache` อยู่ใน instance `AddScoped` (`Program.cs:518`) ⇒ ไม่ข้ามคำขอ

### K-11 / K-8 (กระดาษที่ถูกอยู่แล้ว — ไล่ด้วยตา)
- `MaskStatements` จับเฉพาะประโยคที่มี "ออก…" (`OcrIssuerBranch.cs` `ThaiStatementRx`/`EnglishStatementRx`) — บล็อกผู้ซื้อ "สาขาที่ 00003"/"สาขา 00000" ไม่มี "ออก" ⇒ ไม่ถูกกลบ ·
  ความยาว/บรรทัดคงเดิม · ฝั่งผู้ขายยังอ่านบน `rawText` เดิม (จุดแบ่งฝั่งไม่ขยับ)
- WinePro (`Customer Info.` ไม่อยู่ในรายการ noise · สาขา "Branch: สำนักงานใหญ่") · MakroPage3of3 / UptoyouShopee / ScommerceLazada (ไม่มีป้ายฉบับ/ประโยคประกาศในหน้าต่างผู้ซื้อ ·
  "และออกใบกำกับภาษีอิเล็กทรอนิกส์" ไม่มี "โดย/ที่/ณ สาขา" ⇒ ไม่ match) · Radisson PaperB ("Branch Tax Invoice is Issued no. 8" — `BranchRegex` เดิมอ่านไม่ได้อยู่แล้ว
  เพราะมีคำคั่นก่อนเลข) · บิล กฟภ. (ไม่มีป้ายผู้ซื้อที่ `BuyerAnchorRegex` จับ) ⇒ คำตอบสาขาผู้ซื้อไม่ขยับ — ตรงกับตาราง §H ของทีม (1/77 เปลี่ยน = ใบเป้าหมาย)
- Makro ภาพจริง: หลังกลบ "ต้นฉบับลูกค้า/For Customer/Customer No." + ประโยคสาขา ⇒ ป้ายแรก "ชื่อลูกค้า" → ตัวอ่านเจอ "สาขา 00000" ก่อนหัวตาราง ⇒ 00000 ✓ ·
  "สาขาชลบุรี" ไม่มีเลขตาม ⇒ ไม่จับ
- ข้อสังเกต P3: รายการ noise กว้างกว่า "ป้ายฉบับ" (รวม "customer no/id/code", "รหัสลูกค้า", "customer service") ⇒ ใบที่ป้ายผู้ซื้อ**ตัวเดียว**คือ "Customer No." จะหาจุดเริ่มบล็อก
  ผู้ซื้อไม่เจอแล้ว (สาขาผู้ซื้อ null แทนค่าที่เคยอ่านได้) — ไม่พบในชุดกระดาษเทสต์ · ทิศเสียหาย = ว่าง (มองเห็น) ⇒ ไม่ถึง P2
- K-8 `OcrSignatureSlot`: บรรทัด Makro "ชื่อผู้รับสินค้า/ Receiver วชิร…" ไม่มีคำลงนาม/เส้นจุด/สองบทบาท (Receiver กลุ่มเดียวกับผู้รับสินค้า) · บรรทัดก่อนหน้าไม่ใช่เส้นล้วน ⇒
  ยังเป็นบล็อกผู้รับ ✓ · false-positive ที่เป็นไปได้ (บล็อกผู้รับแบบฟอร์มที่มีจุดไข่ปลา "ผู้รับสินค้า ........ คุณ ก") = ย้อนเป็นพฤติกรรมก่อนรอบ 197 ⇒ ไม่ใช่ถดถอย

### คอมไพล์
- `new Accounting.Helpers.OcrOpenPo(d.Id, d.DocumentNumber)` / `new ContactKeyRow(c.Id, c.Name, c.TaxId, c.BranchCode, c.CreatedBy, c.CreatedAt)` — positional record
  ไม่มี optional ⇒ CS0854 ไม่เกี่ยว · อยู่ใน `Select` สุดท้ายก่อน `ToListAsync` (หลัง `OrderByDescending`) ⇒ EF แปลได้ · `CreatedAt` เป็น `DateTime` (`BaseEntity.cs:6`) ตรงชนิด
- `IsOurOwnContact` เป็น local function `(string?, string?) → bool` ระดับ `try` เดียวกัน (`:2588`) ⇒ แปลงเป็น `Func<string?, string?, bool>?` ได้
- ชื่อ local ใหม่ (`concurrentContactId`, `reusedNote`, `wantedVendorBranch`, `hit`, `poPlan`, `redecided`, `newBranchRow`, `concurrentRowId` …) ไม่ซ้ำกับชื่ออื่นในเมธอดเดียวกัน
  (ไล่ช่วง `ScanAsync` 137–3370 · `SubmitCorrectionAsync` 4893–5700 · `CreateDocumentFromScanCoreAsync` 6160–7100)
- `OcrTargetDocumentType.Resolve(null, …, hasLinkedPurchaseOrder:)` ลายเซ็นตรง · `ContactHygieneController` มี `using Accounting.Helpers` · `ContactKeyRow`/`OcrOpenPo` ไม่ชนชื่อในเรพ
- ความเสี่ยงที่เหลือ = ชนิดที่ checker มองไม่เห็น (ต้องรอ CI)

## คำแนะนำก่อน merge
1. R1 — เพิ่ม `VendorBranchConfirmed` เข้า `vendorKeyChanged` + เทสต์ "พิมพ์ยืนยันค่าเดิม ⇒ ตัดสินใหม่" (P2 · แก้เล็ก)
2. R2 — แก้ข้อความ team-K.md ข้อ 9 ให้ตรง และเปิดคำถามให้เจ้าของ: สัญญาณ "คนแตะ WHT ในฟอร์มเอกสาร" (P2 · ต้องตัดสิน เพราะผูกกับ D3-2)
3. R3 — จดใน DOCUMENT_FLOW/คำถามค้างว่าสแกนค้างเก่ายังได้ที่อยู่กระดาษ (หรือผูกเวลา)

---

## สถานะหลังทีม K2 (รอบ 200 · `erp-review/2026-09-29/team-K2.md`)

| # | สถานะ | ที่แก้ · เทสต์ |
|---|---|---|
| R1 | ✅ fb459244 | `OcrVendorBranchContact.VendorKeyTouched` (นับ `VendorBranchConfirmed`) ใน `SubmitCorrectionAsync` · `OcrReview200K2Tests.R1_*` (สองทิศ) · required_call_site `call_args` |
| R2 | ✅ fb459244 (คำตัดสินข้อ 28) | `OcrPostedTruth.WhtTouched` ใน `DocumentService.SyncScanToPostedDocumentAsync` + ย้าย sync ขึ้นก่อน `TryTrainAsync` · `Wht28_*` · แก้ถ้อยคำ `team-K.md` ข้อ 9 |
| R3 | ✅ fb459244 (คำตัดสินข้อ 29) | คอลัมน์ `OcrScanResults.VendorAddressUserTyped` (DEFAULT false · ไม่ backfill) เขียนผ่าน `OcrCorrectedFieldList.VendorAddressTyped` · `DecideScanVendorBranchContactAsync` อ่านธงนี้ · `Addr29_*` |
| R4 | 📋 | แถวสาขาที่สร้างตอนแก้ผลสแกนยังถาวร — มองเห็นได้ (`[Auto-Create] … ตอนแก้ผลสแกน`) และสอดคล้องกับเส้นสแกน · ทางเข้มขึ้น (สร้างเฉพาะเส้นฟอร์ม/รายงานแถวไม่มีเอกสาร) ต้องออกแบบกับ contact-hygiene |
| R5 | ✅ fb459244 | `OcrVendorBranchContact.ScanAlreadyPosted(CreatedDocumentId, CreatedJournalEntryId)` · `R5_*` |
| R6 | ✅ fb459244 | สแกน: `AdoptTaxIdUnderOcrContactLockAsync` · สร้างเอกสาร (AdoptTaxId ฝั่งขาย + backfill ฝั่งซื้อ): ถามคีย์ซ้ำหลัง `BeginTransactionAsync` แล้วถอนการเติม · ตัวตัดสิน `OcrContactCreateLock.MayAdoptAfterLock` · `R6_*` |
| R7 | 📋 | เท่าพฤติกรรมเดิม (ไม่ถอย) — แถวไม่มีเลขภาษีไม่มีกุญแจให้ `FindAsync` เห็น · ทางแก้ต้องล็อกด้วยชื่อ (นอกคำตัดสินข้อ 19) |
| R8 | ✅ fb459244 | `try/catch when (contactCreateTx != null)` รอบบล็อกสร้าง → `UndoOcrContactCreateAfterRollback` (ถอดแถวที่เกิดในบล็อก · คืน `MatchedContactId` · บังคับเขียนแถวสแกนทั้งแถว) · required_call_site (ไม่มีเทสต์ DB — ต้องรอ CI/ทดสอบระบบ) |
| ข้อสังเกต dispose | 📋 | ไม่แก้ — ปลอดภัยวันนี้ (ผู้เรียกทุกตัวปิดธุรกรรมของตัวเองก่อน) |

