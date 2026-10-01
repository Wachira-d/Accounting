# รอบ 200 — ทีม K (OCR ผู้ติดต่อสาขา / ใบ Makro ค้าง)

คอมมิตงาน: `53dc5921` (sha เติมในคอมมิตตามหลัง)

แหล่ง: `erp-review/2026-09-25/makro-branch/review197.md` (ตาราง "สถานะหลังทีม K2": K-3b · K-4 · K-5 · K-8 · K-9 · K-10 · K-11) ·
`erp-review/2026-09-25/review-r199-ocr.md` (แถวที่ยังไม่ ✅) · คำตัดสินเจ้าของ **ข้อ 19** (`DECISIONS.md`) · ใบจริง `makro-branch/paper-page3.jpg`

**ยังไม่ได้คอมไพล์** (เครื่องนี้ไม่มี .NET SDK) — ต้องรอ CI/rebuild ฝั่งผู้ใช้ · เทสต์ใหม่ยังไม่เคยถูกรัน

> หมายเหตุ worktree: branch ของ worktree นี้ถูกตัดจาก `origin/master` (ประวัติคนละสาย ไม่มี `erp-review/2026-09-29/`) — ตั้ง branch ของ
> worktree ตัวเองไปที่หัว `claude/erp-system-review-team-660mev` (`5c1fe028`) ก่อนเริ่ม (worktree สะอาด ไม่มีคอมมิตของทีม) · ไม่ได้แตะ branch อื่น

## ตารางรายการ

| ID | สถานะ | ที่แก้ (file) | เทสต์ |
|---|---|---|---|
| **K-10** (ข้อ 19) | ✅ | `Helpers/OcrCorrectedFieldList.cs` (`OcrWhtBaseline` ใน `OcrCorrectionBaseline` · `WhtChanged`) · `OcrService.SubmitCorrectionAsync` ส่งค่า WHT ของสแกนก่อนรับคำแก้ | `OcrReview200Tests.Wht_*` (ส่งค่าเดิม ⇒ ไม่นับ ⇒ `SystemSuggestedOnly` · ติ๊ก/ปลดติ๊ก/เปลี่ยนอัตรา/เปลี่ยนหรือล้างประเภทเงินได้ ⇒ นับ ⇒ `UserEdited` · ไม่มี baseline = กติกาเดิม) |
| **K-5** (ข้อ 19) | ✅ | `AdvisoryLockKey.OcrContactCreate` · `Helpers/OcrContactCreateLock.cs` (ใหม่) · `OcrService.LockAndFindConcurrentOcrContactAsync` (ล็อก `pg_advisory_xact_lock` ในธุรกรรม → `ContactTaxBranchKey.FindAsync` ซ้ำ) ใน 3 เส้น: `ScanAsync` (Branch 0/1/2 — ธุรกรรมสั้น commit ทันทีหลังบล็อกสร้าง) · `CreateDocumentFromScanCoreAsync` (หลัง `BeginTransactionAsync` · ถอดแถวที่เพิ่ง Add ถ้ามีคำขออื่นสร้างไปแล้ว — รวมลูกค้าฝั่งขาย) · `SubmitCorrectionAsync` (K-4) · รายงาน: `ContactDataHygiene.DuplicateKey/DuplicateKeyGroups` + `ContactHygieneController` + `contact-hygiene.html` (การ์ด "ผู้ติดต่อซ้ำ" · ไม่มีปุ่มรวม — รวมที่หน้าผู้ติดต่อ) · `DocumentService.GetDuplicateContactGroupsAsync` ใช้คีย์เดียวกัน (พฤติกรรมเดิม) · ไม่เพิ่ม unique index | `OcrReview200Tests.LockPart_*` · `AfterLock_*` · `DuplicateKey_*` · `DuplicateGroups_*` · จุดเรียกล็อกด้วย `required_call_site_check` |
| **K-4** | ✅ | `OcrService.DecideScanVendorBranchContactAsync` (ย้ายโค้ดชุดเดิมของเส้นสร้างเอกสารมาเป็นตัวเดียว) · `SubmitCorrectionAsync` เรียกเมื่อ `OcrVendorBranchContact.ShouldRedecideOnCorrection` · `document-scan.html` ไม่ต้องแก้ (`openInDocumentForm` บันทึกคำแก้แล้วโหลดผลสแกนใหม่อยู่แล้ว) | `Redecide_*` · `NoRedecide_OtherwiseUntouched` (4 ทาง) · `Makro_UserChangesBranchInReview_FormGetsTheBranchRow` |
| **K-3b** | ✅ | `Helpers/OcrVendorAliasScope.cs` (ใหม่) · `ProductMatcher.VendorEntityIdsAsync` (แคชต่อคำขอ) ใน `MatchAsync` (alias · คำปฏิเสธ · ประวัติซื้อ raw SQL `= ANY({1})`) + `GetVendorAdaptiveThresholdAsync` · `ScanAsync` คำแนะนำ Stock/Expense · ฝั่งเขียน (`RecordAliasAsync`/`RecordRejectionAsync`) ยังผูกแถวของใบ | `VendorAlias_*` |
| **K-8** | ✅ | `Helpers/OcrSignatureSlot.cs` (ใหม่) · `OcrPartyLabels.FindRecipientAll` กรองช่องลายเซ็น | `FooterSignatureSlot_*` · `SignatureSlotLines_*` · `RoleUnderABareSignatureRule_*` · ทิศตรงข้าม `Makro_RecipientBlock_IsStillTheBuyers` · `FooterRecipientInfoBlock_*` |
| **K-9** | ✅ | `OcrIssuerBranch.StoredAddressIsIssuerBranch` (ใหม่ — พิสูจน์จากข้อความที่เก็บด้วย `Detect` ตัวเดียวกับเส้นสแกน ⇒ ไม่ต้องเพิ่มคอลัมน์) · ใช้ใน `DecideScanVendorBranchContactAsync` · "ผู้ใช้พิมพ์ที่อยู่เอง" ต้องเป็นการเปลี่ยนจริง (`OcrTextBaseline` — ช่องนี้มีบั๊ก "ส่งมา = แก้" ทรงเดียวกับ K-1/K-10) | `BranchRowAddress_*` · `VendorAddress_EchoedBack_*` |
| **K-11** | ✅ | `BranchCodeExtractor.Extract` — สาขาผู้ซื้ออ่านบนข้อความที่กลบป้ายฉบับ (`OcrPartyLabels.MaskCopyNoise` ใหม่) + ประโยคประกาศสาขาผู้ออกใบ (`OcrIssuerBranch.MaskStatements` ใหม่) · จุดแบ่งฝั่งผู้ขายไม่ขยับ | `Makro_BuyerBranch_IsHeadOffice_*` · `BuyerBranch_PapersThatWereRight_AreUntouched` · `MaskStatements_*` · `MaskCopyNoise_*` |
| **r199 A-5** | ✅ | `Helpers/OcrOpenPurchaseOrders.cs` (ใหม่) · `ScanAsync` บล็อก PO ค้าง (เลิก `Take(5)` ก่อนเทียบ) | `OpenPo_*` |
| r199 B-1 | ✅ `33a127f2` (ทีม H) | ติ๊กในไฟล์ต้นทาง (แถวสถานะเดิมยังเขียน backlog) | — |
| r199 A-1/A-2/A-3/A-7/B-2/B-3/B-4/C-2 | ✅ `439a9bf1` (ทีม K3) | เติม sha แทน `<pending>` (ตรวจ `merge-base --is-ancestor` แล้ว) | — |
| r199 A-4 · A-6 · C-3 | 📋 รอเจ้าของ | ไม่มีในคำตัดสินรอบ 200 — ดูคำถามค้าง | — |

## กันถดถอย (CLAUDE.md §H) — รันกระดาษจริงก่อน/หลัง

ไม่มี SDK ⇒ เขียนตัวจำลอง Python ของ `BranchCodeExtractor` (สาขาผู้ซื้อ) และ `OcrSellerContactChannel` (อีเมล/เบอร์ผู้ขาย) ตาม regex/รายการคำจริง แล้วรัน
**ข้อความกระดาษทุกค่าคงที่ใน `Accounting.Tests/*.cs`** (77 ใบ: `OcrPaperSamples` · `OcrPartyZoneRealPaperTests` · `OcrMakroBranchVendorTests` · corpus ของ
`OcrReplayHarness` ฯลฯ) ก่อน/หลัง:

| ตัวตัดสิน | ใบที่คำตอบเปลี่ยน | อธิบาย |
|---|---|---|
| สาขาผู้ซื้อ (K-11) | 1/77 — `MakroPhoto` 00005 → **00000** | ใบเป้าหมาย (กระดาษพิมพ์ "Tax ID 0203562005871 สาขา 00000") |
| อีเมล/เบอร์ผู้ขาย (K-8) | 0/77 (+ ใบทดสอบช่องลายเซ็นท้ายบิล 1 ใบ: null → sales@a.co.th) | ไม่มีใบในชุดที่มีช่องลายเซ็น "ผู้รับสินค้า" ⇒ ไม่มีใบเดิมขยับ |

ตัดสินใจไม่ขยับ**จุดแบ่งฝั่งผู้ขาย**ของ `BranchCodeExtractor` แม้ป้ายฉบับจะทำให้มันผิดที่ด้วย — การขยับเปลี่ยนคะแนนสาขาผู้ขายของสลิป "CUSTOMER COPY"
(0.85 → 0.70 ⇒ หยุดสร้างแถวสาขา = ถอยทิศ A-4 ที่ยังรอเจ้าของ) · ขอบเขตของ K-11 คือสาขาผู้ซื้อ

## F3 ข้อ 7–12 (ตอบในข้อความคอมมิตด้วย)

7. รูปแบบเดิมทั้งเรพ: "ส่งมา = แก้" ของช่องที่มีผู้อ่านเชิงตัดสิน — สาขา (K-1 เดิม) · WHT · ที่อยู่ผู้ขาย ได้ baseline แล้ว (3/3 ช่องที่มีผู้อ่านตัดสิน · ช่องอื่นอ่านแค่ KPI) ·
   จุดสร้างผู้ติดต่อในเส้น OCR 7 จุด (สแกน Branch 0/1/2 · สร้างเอกสาร: ลูกค้าฝั่งขาย/แถวสาขา/fallback · แก้ผลสแกน: แถวสาขา) — ทุกจุดอยู่ใต้ล็อก K-5 เมื่อมีเลขภาษี ·
   ไม่มีเลข (สร้างจากชื่อ+ที่อยู่) ไม่ล็อก — ไม่มีกุญแจให้ชน ·
   `ProductAlias.ContactId ==` ฝั่งอ่านเหลือ 0 จุด (ฝั่งเขียน 2 จุดตั้งใจคงไว้) · `Take(5)` ก่อนเทียบเลข PO เหลือ 0
8. ทางเข้าอื่น: LINE และ API v1 ผ่าน `ScanAsync` → `CreateDocumentFromScanAsync` ตัวเดียวกัน ⇒ ได้ล็อก K-5/K-3b/K-11/K-8/A-5 ครบ · `SubmitCorrectionAsync` มีผู้เรียกเดียว
   (OcrController) · เส้นนำเข้า/integration/CMS สร้างผู้ติดต่อผ่านตัวอื่น (คำตัดสินข้อ 19 ครอบเฉพาะ OCR — ไม่ได้ขยาย)
9. ไม่มี throw ใหม่ที่ผู้ใช้เจอ (throw ใหม่ตัวเดียวคือ `LockAndFindConcurrentOcrContactAsync` นอกธุรกรรม = บั๊กของโปรแกรมเมอร์ ทุกผู้เรียกเปิดธุรกรรมก่อน) ·
   เข้มขึ้น 2 จุด: (ก) WHT ที่ไม่ได้แตะไม่สอนประวัติ — ทางไปต่อ: แก้/ติ๊กช่อง WHT ในหน้ารีวิว (นับทันที) · ในฟอร์มเอกสาร นับ**ตอนอนุมัติ**เมื่อค่าต่างจากสแกน (แก้ถ้อยคำโดยทีม K2 — เดิมเขียน "หรือเอกสาร (นับทันที)" ซึ่งไม่จริงจนคำตัดสินข้อ 28 · `OcrPostedTruth.WhtTouched`) · กระดาษพิมพ์ WHT = เรียนเหมือนเดิม
   (เทสต์ `Wht_UserTicksOrAppliesSuggestion_IsAnEdit_AndTeaches`) (ข) ที่อยู่แถวสาขาใหม่ในเส้นสร้างเอกสาร ว่างเมื่อพิสูจน์ไม่ได้ — ทางไปต่อ: ข้อความใน
   ProcessingNotes ให้เติมที่หน้าผู้ติดต่อ หรือแก้ที่อยู่ในหน้ารีวิวก่อนกดสร้าง (`BranchRowAddress_UserTypedAddress_IsProven_…`)
10. ค่าที่ persist ก่อนแก้: `UserCorrectedFields` ที่มี "HasWht"/"VendorAddress" จากการส่งซ้ำของเว็บ **แยกไม่ได้**ว่าแถวไหนแก้จริง ⇒ ไม่ migrate (ลบ = ทิ้งคำแก้จริง ·
    คง = backfill `VendorIntelligenceService` ยังเรียน WHT จากแถวเก่าเหล่านั้น) — คำถามค้าง Q1 · แถวผู้ติดต่อซ้ำเดิม ⇒ รายงาน (คำตัดสินข้อ 19) ไม่ migrate ·
    `ProductAlias` ไม่ต้อง migrate (ขยายที่ฝั่งอ่าน) · สาขาผู้ซื้อ 00005 ที่ persist บนสแกน Makro เก่า — ไม่ migrate (ผู้ใช้แก้ในหน้ารีวิวได้ · ช่องนี้ไม่ได้เป็นกุญแจผู้ติดต่อฝั่งซื้อ)
11. แตะเงิน/ภาษี (WHT learning) + สิทธิ์ข้อมูลผู้ติดต่อ — **ยังไม่ได้ส่ง subagent ฝ่ายค้าน** (ทีมนี้เป็น subagent ที่สั่งห้ามแตกงานซ้ำ) ⇒ main agent ควรส่ง diff นี้ให้ฝ่ายค้าน 1 รอบ
12. DOCUMENT_FLOW §1 OCR (ผู้ติดต่อตามสาขา + อีเมล/เบอร์) + Last verified · TEST_PLAN OCR-U-15..23 + §0 · CHANGELOG · `docs/lessons/ocr-pipeline.md` (3 ข้อ) · ACCOUNT_STRUCTURE ไม่ต้องขยับ

## ความเสี่ยงคอมไพล์ที่เหลือ (ไม่มี SDK)

- `SqlQueryRaw<VendorHistoryRow>(sql, companyId, vendorIds.ToArray())` — ส่ง `Guid[]` เป็นพารามิเตอร์ `= ANY({1})` (Npgsql map `uuid[]`) · อยู่ใน try เดิมที่คืนว่างเมื่อล้ม
  (ถ้า provider ไม่รับ จะเงียบเป็น "ไม่มีประวัติ" — ควรดูผลเทสต์ระบบ)
- `await using var x = cond ? await _db.Database.BeginTransactionAsync() : null;` (2 จุด) — ชนิด `IDbContextTransaction?` · ใช้รูปเดียวกับที่อื่นในเรพ
- `EntityState.Detached` เขียนเต็ม `Microsoft.EntityFrameworkCore.EntityState` กันชื่อชน
- EF projection `new OcrOpenPo(d.Id, d.DocumentNumber)` / `new ContactKeyRow(...)` ใน `Select` (record ไม่มี optional arg — CS0854 ไม่เกี่ยว)
- ส่ง local function `IsOurOwnContact` เป็น `Func<string?, string?, bool>?`
- static checker ที่รันผ่าน: using/record_arg/nullable_arg/accessibility/arg_type/tuple_name/identifier/verbatim/string_quote/comment_line_break/advisory_lock_key/
  ocr_helper_test/contact_taxid_only/regex_line_span/namespace_shadow/test_inventory + `bash tools/check_all.sh` (ดูผลในข้อความคอมมิต)

## คำถามค้าง (ทิศที่เลือก = มองเห็นและย้อนได้)

- **Q1** `UserCorrectedFields` เดิมที่มี "HasWht" จากการส่งซ้ำของเว็บ (ก่อน K-10) — จะให้ backfill ประวัติ WHT (`VendorIntelligenceService` เส้น rebuild) ข้ามแถวที่
  `UserCorrectedAt` ก่อนวันนี้ไหม? (ตอนนี้: คงไว้ — ไม่ลบหลักฐานที่อาจเป็นคำแก้จริง)
- **Q2** known-good ชื่อ/ที่อยู่ผู้ขาย (`RememberUserCorrectionAsync` ใน `SubmitCorrectionAsync`) ยังบันทึก `Source = "UserCorrection"` ทุกครั้งที่หน้าเว็บส่งค่า (แม้ไม่ได้แก้)
  ⇒ ค่าที่ OCR อ่านผิดแล้วผู้ใช้ไม่สังเกต กลายเป็น "คำแก้ของคน" ที่ชนะ Azure ถาวร — ทรงเดียวกับ K-10 แต่ไม่อยู่ในคำตัดสินข้อ 19 ⇒ ไม่แก้รอบนี้ · เสนอ: ใช้ baseline เดียวกัน
- **Q3 (r199 A-4)** ใบร้านค้า/สลิปที่ไม่มีบล็อกผู้ซื้อ + รหัสสาขาเดียวทั้งหน้า ⇒ ให้ 0.85 (สร้างแถวสาขาได้) ไหม · **(A-6)** settlement ข้ามแถวสาขาสืบทอด ContactId ของต้นทางไหม ·
  ✅ 2d7020af (รอบ 201 TX C-21) **(C-3)** สวิตช์ RequirePhoR06 ปิดด่านขายปลีกด้วย — ยังรอเจ้าของ
- **Q4 (ที่อยู่แถวสาขาในเส้นสแกน — Q-P3 เดิม)** เส้นสแกน Branch 0 ยังถอยไปใช้ที่อยู่ทะเบียน (= สำนักงานใหญ่) เมื่อกระดาษไม่มีที่อยู่ต่อท้ายประโยคประกาศสาขา
  (ใบ Makro) — K-9 แก้เฉพาะเส้นสร้างเอกสารตามขอบเขต · ถ้าเจ้าของเลือก "ไม่รู้ = ว่าง" ให้ใช้ `StoredAddressIsIssuerBranch` แบบเดียวกันที่เส้นสแกน
- **ข้อสังเกต (ไม่แก้)** ผู้ใช้แก้**เลขผู้เสียภาษี**ผู้ขายเป็นเลขที่ยังไม่มีผู้ติดต่อ — ทั้งเส้นสร้างเอกสาร (เดิม) และ K-4 คง `MatchedContactId` ของเลขเดิม (ไม่มีแถวของเลขใหม่ให้เลือก)
  ⇒ เอกสารผูกผู้ติดต่อของเลขเดิม · ควรตัดสินว่า "เลขเปลี่ยน + ไม่มีแถว" ⇒ ปล่อยว่างให้สร้างใหม่ไหม
