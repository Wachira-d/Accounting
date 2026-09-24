# ฝ่ายค้านรอบสอง (security / ภาษี) รอบ 193 — ตรวจการแก้ของ S2 (f4aa7d4) · C3 (6b2e7fb) · V (7601891)

> อ่านอย่างเดียวจากซอร์สที่ HEAD `ae0cc3d` (branch `claude/erp-system-review-team-660mev` ซึ่ง merge ทั้งสามคอมมิตแล้ว)
> · เครื่องนี้ไม่มี .NET SDK จึง**ยังไม่ได้คอมไพล์** · ทุกข้อ CONFIRMED เปิดไฟล์ตรงบรรทัดแล้ว
> · checker ใหม่ 3 ตัวรันจริงที่ HEAD (เขียวทั้งหมด) แล้วลองกลายพันธุ์ **ในสำเนาที่ scratchpad เท่านั้น** (ไม่แตะเรพ) — ผลอยู่ท้ายไฟล์
> · `di_cycle_check` = ผ่าน (193 service / 219 เส้น ไม่มีวง)
> · checker คอมไพล์อื่นที่ HEAD: `service_interface` · `undeclared_local` · `record_arg` · `accessibility` · `namespace_shadow` · `tuple_name_merge` · `write_permission_gate` · `required_call_site` (67 กติกา) = 0 จุด/ผ่าน · `check_all.sh` ทั้งชุดรันไม่จบใน 10 นาที (checker บางตัวกวาดไฟล์ใน `.claude/worktrees` ด้วย — 26k ไฟล์)

---

## 1. ข้อ CONFIRMED ของรอบแรก ปิดจริงไหม

### 1a. `review193-S2.md` (ทีม S2 → f4aa7d4)

| ID | ผล | หลักฐาน (file:line ที่ HEAD) | หมายเหตุ |
|---|---|---|---|
| S2-C1 (P0) สแกนไฟล์แนบชนิดใดก็ได้ | **ปิด** (มีผลข้างเคียงใหม่ → R2-C1) | `OcrController.cs:242-266` `DenyScanSourceAsync` ก่อน `ScanAsync` · `AttachmentAccessGate.cs:318-338` ไฟล์ของรายการอื่นต้องอ่านได้ก่อน · `AttachmentPermissionScope.cs:160-173` สแกนใช้ด่านของ**เจ้าของไฟล์** · `OcrService.cs:8722-8749` ลบสแกนตัดสินด้วย `OcrScanFileDisposal` | ครบทั้ง 3 ผล (RawText · รูป · ลบหลักฐาน) · แต่แกนของการแก้ (ใน gate service) ไม่มีทั้งเทสต์และ checker เฝ้า — ดู §5 A6/A7 |
| S2-C2 (P0) link-document ย้ายไฟล์ข้ามเอกสาร | **ปิด** | `OcrController.cs:517-533` ด่านเขียนสแกน + ด่านเขียนเอกสารปลายทาง · `OcrService.cs:8355` / `:8386-8396` `ScanFileRelinkable` + ห้ามย้ายสแกนที่ผูกใบอื่นที่ยังอยู่ · `FileAttachmentService.cs:155-160` relink-on-read กรอง `OcrScan` · `DocumentController.cs:294-302` `linked-scan` มีด่านอ่านเอกสาร | เส้นปกติ "สแกน → สร้างเอกสาร" ยังย้ายไฟล์ `OcrScan` ไปผูกเอกสารใหม่ได้ (`ScanFileRelinkable("OcrScan")=true`) ✔ · **แต่**กรณีเอกสารร่างที่สร้างจากสแกนถูกลบแล้วสร้างใหม่ พัง (R2-C1) |
| S2-C3 (P1) endpoint สแกน ~20 ตัวไม่มีด่าน | **ปิด (ตามตัวอักษร)** | นับเอง: 24 action ที่รับ `Guid scanId` เรียก `ScanGateAsync` ครบ (`OcrController.cs:303…1545`) + 3 action รับ `documentId` เรียก `DocGateAsync` + `Scan` เรียก `DenyScanSourceAsync` = 28 ตรงกับที่ทีมอ้าง | ด่านเขียนของสแกน "ยังไม่ผูก" เป็นระดับสมาชิกกับ**ทุก** 24 action รวม register-asset/import-stock ที่ลง JE/สต็อก (R2-C3) |
| S2-C4 (P2) จุดบอด checker | **บางส่วน** | `tools/attachment_gate_check.py` ปิดแถวที่รอบแรกลองได้ครบ (ใช้ผลแต่ไม่ return · ห่อใน if · ชนิด/ทิศผิดบน UploadReceipt · discovery 3 รูป) | กลายพันธุ์ใหม่หลุด 7/8 (§5) — ทิศ `write:` ของ `ScanGateAsync` ไม่ถูกตรวจ · พารามิเตอร์ชื่ออื่น · เงื่อนไขอ่อนลง · แกนใน gate service · และฟ้องผิดกับ `try { … }` |
| O1-C7 amount-audit / settlement-proposal | **ปิด** | `OcrController.cs:472-503` | — |
| S2-P1 ใบเบิก Submitted ถอดหลักฐาน | **ปิด** | `ExpenseClaimEvidencePolicy.cs` `OwnerMayChange(status, isRemoval)` · `AttachmentAccessGate.cs:104-111` · อนุมัติตรวจซ้ำ: เว็บ `ExpenseClaimService.cs:284-288` · มือถือ `MobileApiService.cs:518-524` | ทิศตรงข้าม: ผู้ยื่นยังเพิ่มหลักฐานช่วง Submitted ได้ ✔ · ร่างแนบ/ถอดได้ ✔ |
| S2-P2 ผู้ยื่นถือ Expense.Approve แก้ใบตัวเอง | **บางส่วน** | SoD เฉพาะ**ไฟล์แนบ** (`ReviewerKeyApplies`) | ตัวการอนุมัติ/จ่ายใบเบิกเองไม่มีด่านสิทธิ์เลย ทั้งเว็บและมือถือ ⇒ อนุมัติใบตัวเองได้ (R2-C2) — SoD ไฟล์แนบจึงกันแค่ขั้นที่เบากว่า |
| S2-P3 สแกนที่ลง JE | **ปิด** | `AttachmentPermissionScope.cs:169-170` · `AttachmentAccessGate.cs:209-219` | ไฟล์ของ JE-จากสแกนยังถูกงาน purge ลบจริงได้ใน 30 วัน (R2-C4) |
| S2-P4 predecessor-candidates / open-pos | **ปิด** | `OcrController.cs:590-605`, `:634-645` ตัดด้วย `HiddenDocumentIdsAsync` · link-po/link-predecessor ห้ามผูกใบที่มองไม่เห็น `:613`, `:653` | — |
| S2-P6 ถังก่อนบันทึก | **ปิด** | `AttachmentAccessGate.cs:51-70` · `AttachmentPermissionScope.UnsavedFileVisible/Removable` | — |
| S2-P7 ผูกไฟล์ในถังคนอื่น | **ปิด** | `WhtCreditService.cs:314-330` + `WhtCreditController.ActorAsync` | ช่องข้าง ๆ ยังอยู่: `AttachmentId` ที่ชี้ไฟล์ของรายการอื่น (ไม่ใช่ถัง) ถูกเก็บเป็นตัวชี้ได้ (PLAUSIBLE Q4) |
| S2-P8 ทิศเขียน OcrScan ที่ผูกแล้ว | รับทราบ (ตั้งใจ) | — | — |

### 1b. `review193-V-C3.md` (ทีม V → 7601891 · ทีม C3 → 6b2e7fb)

| ID | ผล | หลักฐาน | หมายเหตุ |
|---|---|---|---|
| C-1 e-Tax ประทับล้มให้ใบ walk-in | **ปิด** (ขอบเขตกว้างเกิน → R2-C6) | `EtaxAutoIssueScope.cs:81-101` · `IssuedDocumentHooks.cs:66-86` | ข้ามเงียบรวมถึง "ผู้ซื้อ**นิติบุคคล**ข้อมูล §86/4 ไม่ครบ" ซึ่งเส้นเว็บบล็อก (ดัง) แต่เส้น Integration เงียบ |
| C-2 ใบ `IsTaxInvoiceByLaw=false` ส่ง e-Tax ได้ | **ปิด** (ธงต้นทางผิดสำหรับ 0% → R2-C7) | `EtaxAutoIssueScope.cs:99` · `EtaxInvoiceService.cs:160-168` | — |
| C-3 ใบเสร็จ settlement §78/1 ไม่ได้ e-Tax | **ปิด** | `DocumentService.cs:11114-11120` (หลัง `tx.CommitAsync`) · `:11526-11532` (หลัง `transaction.CommitAsync`) | hook ไม่ throw · ร่าง/ใบเสร็จเปล่าถูก `Judge` ข้ามเอง ✔ · CN คืนมัดจำยังค้างใน baseline (ทีม L2) |
| C-4 ลิสต์คำรถใน JS | **ปิด (คำรถ)** · กติกา §82/5 อื่นใน JS ยังอยู่ | `InputVatScreenController.cs` · `documents.html:7815-7850`, `:10475-10500` | ลิสต์รถใน JS = 0 ✔ · ค่าที่ผู้ใช้เลือกชนะ (`userOverride`) ✔ · ผลตอบช้าถูกทิ้งด้วย seq ✔ · ยังเหลือ regex ค่ารับรอง/บิลเงินสดใน JS (ทีมจดไว้) + ปิดเคลมอัตโนมัติแบบ "ค้าง" (PLAUSIBLE Q1) |
| C-5 คำเตือนรถกำกวมหาย | **ปิด** | `InputVatVehicleRule.cs` ชั้น `UnclearCheck` | ไม่ได้รันเทสต์ C# (ไม่มี SDK) — อ่านตรรกะแล้วสอดคล้อง 5 แถวของรอบแรก |
| C-6 soft match นอก SoftScope | **ปิด (ฝั่งกัน)** · **ไม่ปิด (ฝั่งเติม)** | SoftScope ครบทุกทางเข้าที่อ้าง (grep 11 จุด) · `MayWriteTaxId` เฉพาะ `IntegrationService.cs:646` | แถวที่ถูกจับผ่าน `RowsWithoutTaxId` **ไม่ถูกเติมเลขภาษีของ payload** ใน 4 ทางเข้า (Integration ใบขาย · CMS · นำเข้า · API v1) = คลาสเดียวกับ C-7 ที่ที่พักแก้แล้ว (R2-C5) |
| C-7 ที่พักคืนแถวบุคคล | **ปิด** (ทีม L2) | `LodgingService.Reservations.cs:438-452` `LodgingGuestContact.SoftCandidateAcceptable` + เติมเลข | ยังเขียนตัวกรองเอง (`TaxId == null \|\| ""`) ไม่ผ่าน `SoftScope` ⇒ แถว "-" ถูกตัดสินต่างจากทางเข้าอื่น (drift เล็ก ทิศปลอดภัย) |
| C-8 "-" = เลขภาษี | **ปิด** | `ContactTaxBranchKey.cs:84`, `:226` `HasTaxId` · `PlatformBillingDocumentIssuer.cs:291-318` · `CrossTenantWorkflowService.cs:460-477`, `:492` | ลูกค้าที่ไม่มี ExternalId: ถอยไปชื่อบนแถวที่ไม่ได้ผูก tenant อื่น ✔ ไม่สร้างซ้ำ · **แต่** tenant ที่เคยบิลตอน "-" แล้วมากรอกเลขทีหลัง ได้ผู้ติดต่อซ้ำ (R2-C9) |
| C-9 บริษัทสมัครใหม่ = ไม่จด โดยไม่มีใครถาม | **บางส่วน** | `VatStatusConfirmedAt` + แถบ `layout.js:661-690` | migration idempotent ✔ · แถวเดิมไม่ขึ้นแถบผิด ✔ · แต่ "ยืนยันแล้ว" ถูกประทับจากการกดบันทึก**อะไรก็ได้**บนหน้าตั้งค่า/ข้อมูลบริษัท (R2-C8) |
| P-2 ออก e-Tax ซ้ำพร้อมกัน | **ปิด** | `EtaxInvoiceService.cs:260-271` `FOR UPDATE` + ตรวจซ้ำในธุรกรรม · hook ถือ "อีกตัวออกแล้ว" ไม่ใช่ล้ม `IssuedDocumentHooks.cs:106` | ไม่พบ deadlock: ทุกผู้เรียก hook อยู่หลัง commit ของธุรกรรมที่ล็อกเอกสาร (Approve `:5563→5592` · settlement ×2 · POS · CMS · Integration ไม่มี tx ห่อ) |
| P-3 StampFailure SaveChanges ทั้ง context | **ปิด** | `IssuedDocumentHooks.cs:124-156` ExecuteUpdate แถวเดียว | — |
| P-4 ป้ายไม่มีผู้อ่าน/ไม่ถูกล้าง | **ปิด** | `EtaxAutoFailedNote` · `DocumentService.cs:16312` · `EtaxInvoiceService.cs:343` | รูปแบบป้ายเดิม (`"\n\n"` + marker ต้นย่อหน้า — `git show 7bb5549`) ถูก `Clear` ถอดได้ ✔ |
| P-6 LoadByTaxIds ไม่ดึงเลขมีขีด | **ปิด** | `ContactTaxBranchKey.cs:160-171` | — |
| P-7 CMS อ่านธง VAT คนละตัว | **ปิด** | CmsLead/Booking/Commerce → `CompanyVatStatus` | — |
| P-8 settings.html ติ๊กจด VAT เป็นค่าเริ่มต้น | **ปิด (แท็บตั้งค่า)** · **ไม่ปิด (แท็บข้อมูลบริษัท)** | `settings.html:2263-2265`, `:2484-2487` | แท็บข้อมูลบริษัทยังส่ง `isVatRegistered` ทุกครั้ง (`:2119`) และ hydrate `c.isVatRegistered \|\| false` (`:1882`) — ทีมรู้แล้ว ("เสี่ยงต่ำ") แต่ตอนนี้ยังประทับ "ยืนยันแล้ว" ด้วย (R2-C8) |

---

## 2. CONFIRMED ใหม่ (file:line)

### R2-C1 (P1 · ถดถอยจาก f4aa7d4) ลบเอกสารร่างที่สร้างจากสแกน แล้วสแกนนั้น "ตาย" — ทุก action เขียนตอบ 404 และสร้างเอกสารใหม่ไม่ได้
- เส้นปกติ: สร้างเอกสารจากสแกน → ไฟล์ถูกย้ายเป็น `EntityType="Document", EntityId=<ใบร่าง>` (`OcrService.cs:8361-8363`)
- ผู้ใช้ลบใบร่าง: `DocumentService.DeleteDocumentAsync` ตัดตัวชี้ `scan.CreatedDocumentId = null` (`:7896-7910`) โดยตั้งใจให้ "สแกนกลับไปสถานะยังไม่ได้สร้างเอกสาร" (บั๊กผู้ใช้รายงาน 2026-09-18) แล้ว **hard-delete** แถวเอกสาร (`:7911-7912`) — **ไม่แตะแถวไฟล์** ⇒ ไฟล์ยังชี้เอกสารที่ไม่มีแล้ว
- `AttachmentPermissionScope.ScanOwner` กติกาข้อ 1 (`:165-166`) คืนเจ้าของ = ไฟล์ **โดยไม่ตรวจว่าเจ้าของยังอยู่** (ข้อ 2/3 ตรวจ `…Exists` แต่ข้อ 1 ไม่ตรวจ) ⇒ `DenyDocAsync` ทิศเขียน `doc == null` → **404 "ไม่พบเอกสารนี้ในบริษัท"** (`AttachmentAccessGate.cs:406-410`)
- ผล: สร้างเอกสารใหม่ · retry · แก้บรรทัด · จับคู่ผู้ติดต่อ · **ลบสแกน** ของใบนั้นทำไม่ได้ทั้งหมด (อ่านยังได้) · และต่อให้ผ่านด่าน `ScanFileRelinkable("Document", <ใบที่ลบ>, <ใบใหม่>) = false` (`OcrService.cs:8355`) ⇒ ใบใหม่ไม่มีไฟล์ต้นฉบับแนบ
- ก่อน f4aa7d4 เส้นนี้ทำงาน (action เขียนไม่มีด่าน · relink ย้ายได้ทุกกรณี) ⇒ เป็นทิศตรงข้ามที่เทสต์ `ไฟล์ของสแกนเอง_ย้ายเข้าเอกสารได้_และเรียกซ้ำกับใบเดิมได้` ไม่ครอบ
- แนวแก้: ข้อ 1 ของ `ScanOwner` ต้องรับ `fileOwnerExists` (เจ้าของหาย = ไม่ใช่เจ้าของ) และ `ScanFileRelinkable` ยอมย้ายเมื่อเอกสารเจ้าของถูกลบแล้ว — หรือ `DeleteDocumentAsync` คืนไฟล์ของสแกนที่ตัดตัวชี้กลับเป็น `OcrScan` (ร่าง = ไม่ใช่หลักฐานบัญชี) · เทสต์สองครึ่ง

### R2-C2 (P0 · ไม่ใช่การถดถอย · พบจากคำอ้าง SoD ของ S2) อนุมัติ/จ่าย/ยกเลิกใบเบิกค่าใช้จ่าย **ไม่มีด่านสิทธิ์เลย** — ผู้ยื่นอนุมัติและจ่ายใบของตัวเองได้
- `ExpenseClaimController.cs:80-110` (`approve` · `reject` · `pay` · `void` · `PUT`) มีแค่ `[Authorize]` ระดับคลาส (`:14`) · `ExpenseClaimService.ApproveAsync/MarkAsPaidAsync/VoidAsync` ไม่มี `HasPermission` สักจุด (grep = 0) · ไม่มีเงื่อนไข `approver != SubmittedByUserId`
- มือถือ: `MobileApiService.QuickApproveAsync` → `HandleExpenseClaimApprovalAsync` (`:343-345`, `:503-530`) ไม่ตรวจสิทธิ์เช่นกัน (เอกสารชนิดอื่นในเมธอดเดียวกันตรวจ `CanApproveAsync` ที่ `:433`)
- `MarkAsPaidAsync` สร้าง **ใบสำคัญจ่าย (PV) แล้วอนุมัติ** ในนาม `"system:expense-claim"` (`ExpenseClaimService.cs:467-468`) ⇒ ลง JE เงินสดออกโดยข้าม `DocumentPermissionHelper.CanApproveAsync` ที่ CLAUDE.md (ERP_REVIEW) บังคับให้ "ทุกทางเข้าอนุมัติเอกสาร" ต้องผ่าน
- สายโซ่: พนักงาน (สิทธิ์ต่ำสุด) สร้างใบเบิก → ส่ง → อนุมัติเอง → กดจ่ายเอง = เงินออก + PV อนุมัติ + JE · SoD ของ S2 กันได้แค่ "แก้ไฟล์แนบหลังล็อก"
- `write_permission_gate_check.WATCHED` ไม่มี `ExpenseClaimController.cs` ⇒ checker รายงานเขียว (บทเรียน "allow-list ครบไหม ≠ ผ่านไหม" รอบที่ 8)
- แนวแก้: `Expense.Approve`/`HR.Admin` ที่ approve/reject/void · คีย์จ่าย (หรือ `CanApproveAsync(PaymentVoucher)`) ที่ pay · ห้ามผู้ยื่นอนุมัติใบตัวเอง (ทางไปต่อสำหรับบริษัทคนเดียว = คำถามเจ้าของข้อ 1 ของ S2) · เพิ่มเข้า WATCHED

### R2-C3 (P1 · R5 ทางเข้าอื่น) สแกน "ยังไม่ผูก" ลงทะเบียนสินทรัพย์ + JE และนำเข้าสต็อกได้ระดับสมาชิก
- `ScanGateAsync` ส่ง `unlinkedEditIsMemberLevel: true` ให้**ทุก** action เขียน (`OcrController.cs:41-48`) ⇒ สแกนที่ยังไม่ผูก คีย์ = `ReadAnyOf` ของ OCR (ว่าง) = สมาชิกทุกคน
- docstring ของ gate (`AttachmentAccessGate.cs:181-183`) บอกขอบเขตไว้แค่ "แก้บรรทัด · จับคู่ผู้ติดต่อ · ลบสแกนทิ้ง" แต่ค่าเดียวกันไปถึง:
  - `register-asset` (`OcrController.cs:775-786`) → `RegisterAssetFromScanAsync` สร้าง FixedAsset **และโพสต์ JE ตั้งสินทรัพย์** (`OcrService.cs:~8408-8420` doc) ขณะที่ `FixedAssetController` บังคับ `Asset.Manage`
  - `import-stock` (`:991-1000`) สร้างสินค้า + ขยับสต็อก + สร้างสินทรัพย์ (`:1105`, `:1193`) โดยไม่มีคีย์ Product/Inventory
- `create-document` (`:387`) และ `create-journal-entry` (`:543`) มีด่านของตัวเองแล้ว ✔ — สองเส้นข้างบนไม่มี
- ไม่ใช่การถดถอย (เดิม `[Authorize]` ล้วน) แต่ข้อความคอมมิต "ทุก action สแกนเดินด่านเดียว" ทำให้ดูเหมือนปิดแล้ว · แนวแก้: ส่ง `unlinkedEditIsMemberLevel` เฉพาะ action แก้ผลอ่าน และเพิ่มคีย์โมดูลปลายทาง (Asset.Manage · Inventory.Receive/Product.Edit)

### R2-C4 (P2 · retention ม.10/§87/3 · PDPA) ระยะเก็บไฟล์สแกนกลับหัว: ที่ผู้ใช้ลบ = เก็บ**ตลอดไป** · ที่ลง JE แล้ว = **ลบจริงใน 30 วัน**
- ลบสแกนที่ยังไม่ผูก → `OcrScanFileDisposal` = `SoftDeleteKeepBytes` (`OcrScanFileDisposal.cs:44-47` · `MustKeepPhysicalFile("OcrScan") = true`) → `file.IsDeleted = true` + แถวสแกนถูกลบ (`OcrService.cs:8746`, `:8778`)
- งาน purge ตัวเดียวที่ลบไฟล์สแกน (`OcrSelfCorrectionService.cs:185-213`) **ไล่จากแถวสแกน** และค้นไฟล์ผ่าน query filter `!IsDeleted` (`AccountingDbContext.cs:2252`) ⇒ ไฟล์ที่ soft-delete แล้ว (และแถวสแกนหายแล้ว) ไม่มีวันถูกเก็บกวาด · grep ไม่พบงานอื่นที่ purge `FileAttachments.IsDeleted` — ตอบคำถามโจทย์: **ค้างตลอดไป** (ไม่มี `RetainUntil` ให้ตัดสินด้วย) · ขัด §J "retention by purpose → purge/anonymize"
- ทิศกลับกัน: งานเดียวกันลบ**ไฟล์จริง**ของสแกน `Completed && CreatedDocumentId == null` อายุ > 30 วัน (`:189`) ซึ่งรวมสแกนที่**ลงเป็น JE ตรง** (`CreatedJournalEntryId` ตั้งแล้ว แต่ไฟล์ยังเป็น `OcrScan` — `OcrService.cs:7720-7724` "stays attached to the scan") ⇒ JE ที่โพสต์แล้วเสียเอกสารประกอบภายใน 30 วัน ทั้งที่ S2-P3 เพิ่งประกาศว่าไฟล์นี้ใช้ด่านของ JE · ไม่มี `JobLock`/CompanyId (ไม่ใช่ประเด็นรอบนี้)
- แนวแก้: purge ต้องข้าม `CreatedJournalEntryId != null` (หรือ relink ไฟล์เป็น `JournalEntry`) · ไฟล์ OcrScan ที่ผู้ใช้ลบตั้ง `RetainUntil`/วันลบ แล้วให้งานเดียวกันเก็บกวาดเมื่อครบ (สแกนที่ไม่เคยเป็นรายการบัญชี ≠ หลักฐานที่ต้องเก็บ 5 ปี)

### R2-C5 (P1 · C3 × V) แถวที่ถูกจับผ่าน `RowsWithoutTaxId` ไม่ได้รับเลขภาษีของ payload ⇒ ใบกำกับออกให้ผู้ซื้อที่ไม่มีเลข และ e-Tax ถูกข้าม**เงียบ**
- Integration ใบขาย `ResolveContactAsync` (`IntegrationService.cs:1634-1637`): payload มีเลข 13 หลัก (เลขใหม่) + ชื่อตรงแถวที่ยังไม่มีเลข → คืนแถวนั้นเลย ไม่เขียน `TaxId` (`:1683` return) ⇒ TIV Approved ผู้ซื้อไม่มีเลข → `NotFullTaxInvoice` → hook ข้ามเป็น `EtaxAutoSkip.NotFullTaxInvoice` (ไม่มีป้าย) · PDF เป็นใบอย่างย่อ/ใบเสร็จ ผู้ซื้อเคลมภาษีซื้อไม่ได้
- คลาสเดียวกัน (จับได้แต่ไม่เติม): `CmsCustomerService.cs:351-355` (อีเมล) · `ImportExportService.cs:521-560` (อีเมล — ทั้ง Merge และ Overwrite ไม่เขียน `TaxId` เลย แต่ `ContactType` ถูกคำนวณจากเลขในไฟล์ `:549-551` ⇒ แถวถูกตั้งเป็นนิติบุคคลแต่ไม่มีเลข · นำเข้าซ้ำก็ไม่ติด) · `DocumentsV1Controller.cs:347-356` (ชื่อข้ามภาษา)
- เส้นที่ทำถูกแล้ว: `IntegrationService.ProcessCustomerAsync:646` (`MayWriteTaxId`) · ที่พัก `LodgingService.Reservations.cs:445-452` ⇒ สองแบบในระบบเดียว (R5)
- แนวแก้: ตัวช่วยกลาง "จับผ่าน SoftScope แล้ว → เติมเลข+สาขาของ payload ด้วย `MayWriteTaxId`" ใน `ContactTaxBranchKey` ให้ทุกทางเข้าเรียก + ล็อกใน `required_call_site_check`

### R2-C6 (P2 · V) e-Tax ข้ามเงียบกับ "ผู้ซื้อนิติบุคคลที่ข้อมูล §86/4 ไม่ครบ"
- `EtaxAutoSkip.NotFullTaxInvoice` รวม 3 เหตุ: walk-in · ผู้ซื้อไม่ประสงค์รับ · **ข้อมูลผู้ซื้อไม่ครบ** (`EtaxAutoIssueScope.cs:100` + `TaxService.cs:3104-3111`)
- เส้นเว็บ: ผู้ซื้อนิติบุคคลไม่ครบ = **บล็อกอนุมัติ** พร้อมทางไปต่อ (`DocumentService.cs:5029-5070`) · เส้นที่ข้าม ApproveDocumentAsync (Integration TIV/CN/DN · `IntegrationService.cs:990-992`) ไม่มีด่านนี้ ⇒ ใบของลูกค้านิติบุคคลที่ต้นทางส่งสาขา/ที่อยู่ไม่ครบ เดิมได้ `[ETAX-AUTO-FAILED]` (ดัง) ตอนนี้ข้ามเงียบ = "ควรมีแต่ออกไม่ได้" ถูกจัดเป็น "ข้ามโดยเจตนา" (ขัดคำนิยามของ `EtaxAutoIssueScope` เอง `:39-41`)
- แนวแก้: ข้ามเงียบเฉพาะ walk-in / declined / ผู้ซื้อไม่ใช่นิติบุคคล (`TaxInvoiceCompletenessChecker.IsJuristicBuyer`) · นิติบุคคลไม่ครบ = ปล่อยให้ `GenerateAsync` ล้มแล้วประทับป้าย

### R2-C7 (P2 · V + ธงต้นทาง) ใบขายอัตรา 0% (§80/1 ส่งออก) ผ่าน Integration ได้ `IsTaxInvoiceByLaw=false` ⇒ e-Tax ถูกข้ามเงียบ และสร้างด้วยมือก็ถูกปฏิเสธ
- `IntegrationService.cs:966-975` ตัดสินบทบาทด้วย `CarriesTaxInvoiceRole(probe{VatAmount=totalVat}, resolvedTitle: null)` = `TaxInvoice && VatAmount > 0` เท่านั้น (`TaxInvoiceSeriesPolicy.cs:60-62`) ⇒ 0% กับยกเว้น §81 ได้ธงเดียวกัน
- ก่อน 7601891 ไม่มีใครอ่านธงนี้ · ตอนนี้ `Judge` ข้ามเป็น `NotTaxInvoiceByLaw` (ไม่มีป้าย) และ `GenerateAsync` โยน "ไม่ใช่ใบกำกับภาษี (เช่น ไม่มี VAT หรือยกเว้น §81)" (`EtaxInvoiceService.cs:160-168`) ⇒ ผู้ส่งออกที่ส่งยอดขายผ่าน API ออก e-Tax ไม่ได้ทั้งอัตโนมัติและด้วยมือ ทั้งที่กฎเหล็ก #2 D: ZeroRated = **ออกใบกำกับ rate 0**
- เส้นเว็บไม่เป็น (หัวกระดาษของ TaxInvoice 0% ที่ผู้ซื้อครบยังมีคำว่าใบกำกับภาษี → `CarriesTaxInvoiceRole` จากหัว = true)
- สันนิษฐาน: พาร์ตเนอร์ส่งบรรทัด `VatRate = 0` สำหรับส่งออก (ไม่ได้ยืนยันกับ payload จริง) · แนวแก้: Integration แยก 0% (`VatRate == 0`) กับยกเว้น (`-1`) ตอนตรึงธง หรือเรียก resolver หัวกระดาษตัวเดียวกับเว็บ

### R2-C8 (P2 · V C-9) "ยืนยันสถานะ VAT แล้ว" ถูกประทับจากการกดบันทึกเรื่องอื่น ⇒ แถบเตือนหายโดยไม่มีใครตอบคำถาม
- แท็บตั้งค่า: ส่ง `vatRegistered` ทุกครั้งที่โหลดค่าได้ (`settings.html:2484-2487`) → `SettingsService.cs:141-146` ประทับ `VatStatusConfirmedAt = now` แม้ค่าไม่เปลี่ยน ⇒ เปลี่ยนภาษาเอกสาร/หัวอีเมล/ขนาดตรายาง = "ยืนยันว่าไม่จด VAT"
- แท็บข้อมูลบริษัท: `saveCompany` ส่ง `isVatRegistered` ทุกครั้ง (`:2119`) → `CompanyService.cs:318-324` ประทับเช่นกัน · บริษัทจากหน้าสมัครที่ทำเช็กลิสต์ตั้งค่า (กรอกชื่อ/เลขภาษีบริษัท — `SetupStatusController.cs:55`) จะ "ยืนยันไม่จด" โดยไม่เคยเห็นคำถาม ⇒ แก้ C-9 ได้แค่ช่วงก่อนบันทึกครั้งแรก
- แท็บข้อมูลบริษัทยัง hydrate `c.isVatRegistered || false` (`:1882`) โดยไม่มี guard โหลดล้ม ⇒ โหลดล้มแล้วกดบันทึก = พลิกบริษัทที่จดเป็น "ไม่จด" + ประทับยืนยัน (P-8 ฝั่งที่ทีมจดว่า "เสี่ยงต่ำ" — ตอนนี้ปิดแถบเตือนด้วย)
- แนวแก้: ประทับเมื่อ**ผู้ใช้แตะช่อง VAT** (ส่งธง dirty) หรือเมื่อค่าเปลี่ยน · ส่งช่อง VAT เฉพาะเมื่อแตะ (ตรงกับกฎ #4 A `userTouched`)

### R2-C9 (P2 · C3 · ไม่ใช่ถดถอย แต่ประกาศปิด) ใบค่าบริการแพลตฟอร์ม/ข้ามบริษัท: tenant ที่กรอกเลขภาษีทีหลังได้ผู้ติดต่อซ้ำ
- `PlatformBillingDocumentIssuer.cs:295-304`: มีเลข ⇒ ใช้ `FindAsync` อย่างเดียว ไม่ดูกุญแจ `ExternalSystem="NextAccTenant" + ExternalId` (ใช้แค่กิ่งไม่มีเลข `:309-310`) และไม่ถอยไป `SoftScope` (`RowsWithoutTaxId`)
- วงจรจริง: สมัคร (`TaxId="-"` — ทั้ง `AuthService` และ `layout.js:2099` · `settings.html:2111` เขียน "-" เอง) → บิลแรกสร้างผู้ติดต่อไม่มีเลข + ExternalId → ลูกค้ากรอกเลข → บิลถัดไปสร้าง**แถวที่สอง**ที่ ExternalId เดียวกัน ⇒ ลูกหนี้/ประวัติใบกำกับแยกสองแถว แถวเก่าไม่เคยได้เลข
- `CrossTenantWorkflowService.cs:460-468` แบบเดียวกัน (ไม่มี ExternalId เลย)
- แนวแก้: ExternalId เป็นกุญแจแรก**เสมอ** (ถ้าแถวนั้นยังไม่มีเลข ⇒ เติมด้วย `MayWriteTaxId`) แล้วค่อยเลข+สาขา

### R2-C10 (P2 · checker C3) `contact_taxid_only_match_check` ยกเว้นทั้งประโยคเมื่อมีคำ `ContactTaxBranchKey` — รวม header `if (ContactTaxBranchKey.HasTaxId(x)) {` ที่ติดมากับประโยคแรกในบล็อก
- ตัดประโยคด้วย `;` (`contact_taxid_only_match_check.py:97-101`) ⇒ ประโยคแรกหลัง `if (…HasTaxId…) {` มีคำ `ContactTaxBranchKey` → `EXEMPT` (`:64-67`) ⇒ `_db.Contacts.FirstOrDefaultAsync(c => … c.TaxId == partner.TaxId)` ใน `FindPartnerContactAsync` **ไม่ถูกฟ้อง** (probe C4 · ยืนยันด้วยการพิมพ์ประโยคที่ถูกยกเว้น)
- รูป `if (ContactTaxBranchKey.HasTaxId(...))` คือรูปที่ C3 เพิ่งใส่ 3 จุด (PlatformBilling · CrossTenant ×2) ⇒ ตำแหน่งที่โค้ดใหม่จะเกิดพอดี · สแกนทั้งเรพด้วยกติกายกเว้นที่แคบลง (ยกเว้นเฉพาะ `ContactTaxBranchKey.<ไม่ใช่ HasTaxId>`) = ไม่พบจุดจริงที่ซ่อนอยู่วันนี้ (ผลเท่า baseline 5)
- แนวแก้: ยกเว้นเฉพาะ `ContactTaxBranchKey.(FindAsync|Pick|PickContact|LoadByTaxIdsAsync|AllBranchIdsAsync|SoftScope)` + negative test รูปนี้

### R2-C11 (P2 · checker S2) `attachment_gate_check` หลุด 7/8 กลายพันธุ์ใหม่ และฟ้องผิดกับ `try { }` (รายละเอียด §5)
- ที่มีผลจริงที่สุด: **A1** เปลี่ยน `Delete` เป็น `ScanGateAsync(..., write: false)` ⇒ ผู้ที่แค่**อ่าน**เอกสารร่างได้ ลบสแกนที่ผูกร่างนั้น → `DeleteScanAsync` cascade soft-delete เอกสารร่าง (`OcrService.cs:~8690-8712`) — checker ตรวจแค่ว่ามีอาร์กิวเมนต์ `scanId` (`PARAM_RULES` `:112-117`) ไม่ตรวจทิศ
- **A6/A7** ถอยแกนของ S2-C1 ใน `AttachmentAccessGate` (`DenyScanSourceAsync` ตรวจเฉพาะไฟล์ Document · ไม่ส่งเจ้าของไฟล์เข้า `ScanOwner`) ⇒ checker เขียว และไม่มีเทสต์ (เทสต์ทดสอบแค่ helper pure — `AttachmentGateOtherEntriesTests`) = "control ที่ไม่มี round-trip test = ไม่มี control" (กฎ #4 G)
- ฟ้องผิด: ห่อ `GetResult` ด้วย `try { var deny = …; if (deny != null) return deny; … } catch (KeyNotFoundException) { return NotFound(); }` → "อยู่ในบล็อกเงื่อนไข/วนซ้ำ" (F2 ข้อ 6: checker ที่ฟ้องผิด = checker ที่พัง — ผู้แก้จะเลี่ยงด้วยการไม่ใช้ try)

### R2-C12 (P3 · checker V) `approved_status_writer_check` บอกว่า "เรียก hook หลัง commit" แต่ยอมรับการเรียก**ก่อน** commit
- probe B3: ย้าย `_issuedHooks.RunAsync(companyId, receipt)` ไปก่อน `tx.CommitAsync()` ใน `IssueReceiptForPaymentAsync` → เขียว · ผลจริงถ้าเกิด: `GenerateAsync` เปิดธุรกรรมซ้อน → ล้ม → ประทับป้ายผิด (ดังจึงไม่เงียบ — ความรุนแรงต่ำ) · ข้อความแนะนำของ checker (`:353`) จึงอ้างเกินสิ่งที่ตรวจ

---

## 3. PLAUSIBLE

- **Q1 ปิดเคลมอัตโนมัติแบบค้าง (กฎ #4 A)** — `suggestVatClaim`/`_applyVatScreen` (`documents.html:7815-7870`) ตั้ง `claim=false` เมื่อเซิร์ฟเวอร์ตอบ `claimable=false` แต่ไม่มี `autoSrc` ⇒ พิมพ์คำอธิบายต่อ/แก้จนไม่เข้าข่ายแล้ว ธงไม่กลับ (ต้องให้ผู้ใช้คลิกเอง) · ผลขึ้นกับ "เคยพิมพ์ผ่านคำนั้น" ระหว่าง debounce 400ms · regex ค่ารับรองที่ยังอยู่ใน JS (`/รับรอง/`) ปิดเคลมทันทีกับ "ค่าตรวจรับรองมาตรฐาน ISO"/"หนังสือรับรอง" (มีมาก่อน — ทีมจดค้างไว้)
- **Q2 URL ยาว** — `toggleVatClaim` ส่งคำอธิบายทุกบรรทัดใน query (`documents.html:10483-10486`) ภาษาไทย percent-encode ~9 ไบต์/อักษร ⇒ ใบ 20-30 บรรทัดเกิน `MaxRequestLineSize` 8KB ของ Kestrel → 414 → ตกไปถามแบบทั่วไป (ทิศปลอดภัย แต่คำเตือนเฉพาะ dealer/รถหาย) · ควรเป็น POST อ่านอย่างเดียว
- **Q3 สแกนพี่น้องชี้เอกสารคนละใบ** — `DenyScanCoreAsync` เลือก `aliveDoc` ด้วย `FirstOrDefault` ไม่มี `OrderBy` (`AttachmentAccessGate.cs:211-213`) ขณะที่ `HiddenScanIdsAsync` เลือกของแถวตัวเองก่อน (`:286-288`) ⇒ สองประตูตัดสินต่างกันได้เมื่อ retry แล้วผูกคนละใบ (หายาก)
- **Q4 50 ทวิ ชี้ไฟล์ของรายการอื่น** — `UpdateAsync`/`MarkReceivedAsync` ตั้ง `e.AttachmentId = r.AttachmentId` ทุกกรณี (`WhtCreditService.cs:267-271`, `:290-294`) แต่ `AdoptUnsavedAttachmentAsync` ตัดสินเฉพาะไฟล์ในถัง (`:320-330`) ⇒ ชี้ไฟล์ของสลิปเงินเดือน/เอกสารอื่นได้ (ดาวน์โหลดยังถูกด่านของเจ้าของไฟล์กัน — ความเสียหายคือหลักฐาน 50 ทวิ ผิดชิ้น)
- **Q5 `isVehicleDealer` คลาสเดียวกับ P-8** — ส่งทุกครั้ง (`settings.html:2489`) แม้โหลดค่าล้ม ⇒ ผู้ขายรถที่บันทึกหน้าตั้งค่าตอนโหลดล้มถูกปิดธง dealer (กระทบ §82/5(6) ทุกใบถัดไป)
- **Q6 V1 confirm ไม่ผูก tenant กับ feedbackId** — `OcrV1Controller.Confirm` ส่ง `FeedbackId` ของผู้เรียกเข้า `AiFeedbackRecorder.RecordUserChoiceAsync` ซึ่งค้นด้วย `f.Id == feedbackId` อย่างเดียว (`AiFeedbackRecorder.cs:243`) ⇒ คีย์ของบริษัท A เขียนคำตอบลงแถว feedback ของบริษัท B ได้ถ้ารู้ GUID (พิษต่อ distillation · กฎ M) — นอกขอบเขตสามทีม แต่เป็นทางเข้าข้อมูล OCR ชุดเดียวกัน
- **Q7 มือถืออนุมัติใบเบิกไม่สร้าง CertificateInLieu** (ทีม S2 พบเองแล้ว) — ยืนยันจากโค้ด `MobileApiService.cs:518-530` · ใบ no-receipt ที่อนุมัติทางมือถือไม่มีใบรับรองแทนใบเสร็จ (§65 ทวิ)

---

## 4. ตรวจแล้วไม่มีปัญหา

- **24 action ที่รับ `scanId`** เรียก `ScanGateAsync` ครบ + ใช้ผลแบบ `if (deny != null) return deny;` ก่อนแตะสแกน · `Scan` ใช้ `DenyScanSourceAsync` · 3 action `documentId` ใช้ `DocGateAsync` (ทิศถูก: repopulate/recheck = เขียน · settlement-proposal = อ่าน)
- **ทิศตรงข้ามของ Staff**: สแกนที่ยังไม่ผูก (ไฟล์ `OcrScan`) อ่าน/แก้/retry/ลบ ได้ระดับสมาชิกเหมือนเดิม (`ReadAnyOf` ของ OCR ว่าง · `unlinkedEditIsMemberLevel`) · สแกนที่ผูกเอกสารแล้วต้องมีสิทธิ์สร้างหรืออนุมัติชนิดนั้น (Staff ที่สร้างเอกสารนั้นได้อยู่แล้วผ่าน) · ยกเว้นกรณีเอกสารถูกลบ (R2-C1)
- **ทางเข้าอื่นที่สแกนไฟล์**: `LineBotService.cs:572` และ `OcrV1Controller.Scan` สแกนเฉพาะไฟล์ที่เพิ่งบันทึกเอง · ไม่พบ service อื่นเรียก `DeleteScanAsync`/`LinkScanToExistingDocumentAsync`
- **`IsUnsavedBucket` / ใบเบิก SoD ที่ไฟล์แนบ** ตรรกะตรงคำอธิบาย · สถานะที่ไม่รู้จัก = ล็อก
- **C3 `HasTaxId`/`SoftScope`/`MayWriteTaxId`** สองทิศ: payload ไม่มีเลข ⇒ ทุกแถว (เดิม) · เลขใหม่ ⇒ แถวไม่มีเลข/"-" · เลขมีแล้วคนละสาขา ⇒ ห้าม · `MayWriteTaxId` ไม่ทับเลขอื่น ไม่ล้างเลขจริงด้วย "-" · ทุก query ใน helper กรอง `CompanyId` · `SoftScope` ฝั่ง SQL (`NoTaxIdPlaceholders.Contains(c.TaxId.Trim())`) แปลได้ใน Npgsql
- **PlatformBilling ลูกค้าเดิมที่ไม่มี ExternalId**: กิ่งไม่มีเลข ถอยไปชื่อบนแถวที่ไม่ได้ผูก tenant อื่น ⇒ ไม่สร้างซ้ำ · แถว "-" ของ tenant แรกที่ถูกรายที่สองยืมใช้ (บั๊ก C-8 เดิม) ถูกแยกออกเพราะผูก ExternalId ของ tenant แรก ✔
- **`FOR UPDATE`**: อยู่ในธุรกรรมของ `GenerateAsync` เอง · ผู้เรียก hook ทุกตัวเรียกหลัง commit ของธุรกรรมที่ล็อกเอกสาร (ไม่มีการถือล็อกแถวเดียวกันข้ามสองธุรกรรมในคำขอเดียว) ⇒ ไม่พบ deadlock · การเปิดธุรกรรมใหม่หลัง `CommitAsync` ใน lambda ของ execution strategy ทำได้ (EF ล้าง `CurrentTransaction` เมื่อ commit)
- **`GET input-vat/screen`**: route มี `companyId` ⇒ `TenantAccessMiddleware` + `TenantGuardFilter` · query กรอง `CompanyId` · อ่านอย่างเดียว · สิ่งที่เปิดเผยคือธง dealer ของบริษัทตัวเอง — ไม่ต้องมีคีย์ · `ProhibitedInputVatScreener` เป็น `internal` แต่ใช้ภายในเมธอดเท่านั้น (ชนิดคืนเป็น public record) ⇒ ไม่ติด CS0050/0051
- **`suggestVatClaim`**: ค่าผู้ใช้ชนะ (`userOverride` ตรวจทั้งก่อนยิงและหลังตอบ) · ผลเก่าถูกทิ้งด้วย `vatScreenSeq` · ปิดเคลมเฉพาะ `claimable === false` · ล้ม = ไม่แตะธง
- **`VatStatusConfirmedAt` migration** (`DatabaseMigrationHelper.cs:4863-4864`): `ADD COLUMN IF NOT EXISTS … DEFAULT now()` เติมแถวเดิมครั้งเดียว (`now()` เป็น STABLE ⇒ PG ใช้ค่าเดียวกับทุกแถวเดิม) · รันซ้ำ = ADD ไม่ทำอะไร + `DROP DEFAULT` ซ้ำได้ ⇒ idempotent · บริษัทเดิมมีค่า ⇒ ไม่ขึ้นแถบ · แถบอ่าน `=== false` (เซิร์ฟเวอร์เก่า = ไม่แสดง) ✔ · ข้อสังเกตเล็ก: ถ้ามีแถวใหม่ถูก insert ระหว่างสองคำสั่งตอน rolling deploy จะได้ `now()` แทน null
- **settings.html (แท็บตั้งค่า)** ไม่ส่งช่อง VAT เมื่อโหลดค่าไม่สำเร็จ (`_vatLoaded`) ✔ · markup ไม่ติ๊กไว้แล้ว ✔
- **ป้าย `[ETAX-AUTO-FAILED]`**: Append แทนป้ายเดิม ไม่สะสม · ผู้อ่านธงอื่นใน `InternalNotes` ไม่กระทบ (ถอดเฉพาะย่อหน้าที่ขึ้นต้นด้วย marker)
- **คอมไพล์ (อ่านด้วยตา)**: `IAttachmentAccessGate` มีเมธอดใหม่ครบทั้ง interface/impl · ไม่มี fake ของ `IAttachmentAccessGate`/`IIssuedDocumentHooks` ในเทสต์ และไม่มีเทสต์ `new` `OcrController`/`WhtCreditController`/`DocumentController` · `CompanySettingsFactory.NewFor(company, vatStatusConfirmed)` ผู้เรียกทั้ง 3 จุด + เทสต์ส่งอาร์กิวเมนต์ครบ · `TaxService.NotFullTaxInvoice` มี overload `(decimal, bool, Contact?)` เป็น `internal static` เรียกจาก `IssuedDocumentHooks` ใน assembly เดียวกัน ✔ · `ScanFileOwner` เหลือผู้เรียก 0 (มีแค่คอมเมนต์) · `di_cycle_check` ผ่าน

---

## 5. ผลกลายพันธุ์ checker (สำเนาใน scratchpad · เรพไม่ถูกแตะ)

| # | checker | การกลายพันธุ์ | ผล |
|---|---|---|---|
| A1 | attachment_gate | `Delete`: `ScanGateAsync(…, write: false)` | ❌ หลุด |
| A2 | attachment_gate | `GetResult`: `if (deny != null && User.Identity == null) return deny;` | ❌ หลุด |
| A3 | attachment_gate | action ใหม่ `RawText(Guid companyId, Guid id)` คืน `GetResultAsync(companyId, id)` ไม่มีด่าน | ❌ หลุด (กติกาผูกกับ**ชื่อ**พารามิเตอร์) |
| A4 | attachment_gate | action ใหม่ `RawText(Guid companyId, Guid scanId)` expression-bodied ไม่มีด่าน | ✅ จับได้ |
| A5 | attachment_gate | `DenyScanCoreAsync`: `keys = ocrRule.ReadAnyOf` เสมอ (ทิศเขียนระดับสมาชิกทุกเส้น รวมหน้าไฟล์แนบ) | ❌ หลุด |
| A6 | attachment_gate | `DenyScanSourceAsync` ตรวจเฉพาะไฟล์ชนิด `Document` (สลิปเงินเดือนสแกนได้อีก) | ❌ หลุด |
| A7 | attachment_gate | `DenyScanCoreAsync` ส่ง `ScanOwner(null, null, …)` (ถอยแกน S2-C1) | ❌ หลุด |
| A8 | attachment_gate | `GetByEntityAsync`: `(f.EntityType == "OcrScan" \|\| f.EntityType != null)` | ❌ หลุด (ตรวจแค่มีข้อความ) |
| A-FP | attachment_gate | ห่อ `GetResult` ด้วย `try { … } catch (KeyNotFoundException)` (ด่านยังอยู่บนสุดของ try) | ⚠️ **ฟ้องผิด** |
| C1 | contact_taxid | Integration ลูกค้า ถอดชื่อออกจาก SoftScope | ✅ จับได้ (#soft) |
| C2 | contact_taxid | ถอยไปจับ `NameEn ==` บนทุกแถว | ❌ หลุด (SOFT_CMP รู้จักแค่ Name/Email/Phone) |
| C3 | contact_taxid | API v1 ผู้สมัครเทียบชื่อ (`CounterpartyNameMatcher`) จากทุกแถว | ❌ หลุด (ไม่ใช่รูป `==`) |
| C4 | contact_taxid | `c.TaxId == partner.TaxId` อย่างเดียว ในบล็อก `if (ContactTaxBranchKey.HasTaxId(..))` | ❌ หลุด (R2-C10) |
| C5 | contact_taxid | `c.TaxId == t && c.BranchCode != null` | ❌ หลุด (`!= null` นับเป็น "เทียบสาขา") |
| C6 | required_call_site | Integration เขียน `contact.TaxId = request.TaxId` ไม่ผ่าน `MayWriteTaxId` | ✅ จับได้ |
| C7 | required_call_site | PlatformBilling ถอดกุญแจ ExternalId | ❌ หลุด (ไม่มีกติกาล็อก) |
| B1 | approved_status_writer | ถอด hook ใน `IssueReceiptForPaymentAsync` | ✅ จับได้ |
| B2 | approved_status_writer | ถอด hook ใน `CreatePaymentAsync` | ✅ จับได้ |
| B3 | approved_status_writer | ย้าย hook ไปก่อน `tx.CommitAsync()` | ❌ หลุด (R2-C12) |
| B4 | approved_status_writer | เมธอดใหม่ `new Document { Status = DocumentStatus.Sent }` ไม่เรียก hook | ✅ จับได้ |

สรุป: checker ของ C3 (กติกา 1+2) และ V จับรูปหลักที่รอบแรกยกมาได้จริง · checker ของ S2 จับ "ไม่มีด่าน" ได้ แต่ **ไม่รู้ทิศ/เงื่อนไข/แกนใน service** — ส่วนนั้นต้องเป็นเทสต์ (gate service ไม่มี DB test ในเรพ ⇒ ควรแยกตรรกะ "เลือกเจ้าของจากแถวที่โหลดมา" ออกเป็น pure แล้วเทสต์ ไม่ใช่เขียน checker ที่ต้องรู้ชนิด — F4 ข้อ 1)

---

## 6. ลำดับที่เสนอให้แก้

1. **R2-C2** (P0) ด่านสิทธิ์ใบเบิก approve/pay/void ทั้งเว็บ+มือถือ + ห้ามอนุมัติใบตัวเอง + ใส่ `ExpenseClaimController` ใน WATCHED
2. **R2-C1** (P1 ถดถอย) เจ้าของไฟล์ที่ถูกลบ ≠ เจ้าของ · ลบร่างคืนไฟล์เป็น OcrScan
3. **R2-C5** (P1) ตัวช่วยกลาง "จับแล้วเติมเลข" ให้ 4 ทางเข้า
4. **R2-C3** register-asset / import-stock ต้องมีคีย์โมดูลปลายทาง
5. R2-C4 · R2-C6 · R2-C7 · R2-C8 · R2-C9 · checker R2-C10/11/12
