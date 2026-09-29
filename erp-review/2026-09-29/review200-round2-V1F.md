# รอบ 200 · ฝ่ายค้านรอบสอง — ตรวจงานทีม V1F (ยกเลิก-ออกใบแทน · เช็คเด้ง/e-Tax · SoD)

> ขอบเขต: `git diff 4a9ebd5a worktree-agent-ad895841f7b1bca2e` · อ่านที่ merge `348ccb4a` (บรรทัดทุกจุดอ้าง `348ccb4a`) ·
> อ่านอย่างเดียว — ไม่ได้แก้โค้ด ไม่ได้ commit · **ไม่ได้คอมไพล์** (ไม่มี .NET SDK)
> บริบท: `team-V1F.md` · `review200-V1.md` · `DECISIONS.md` ข้อ 9, 11, 42–46 (ข้อ 42–46 ลงหลัง merge ที่ `bfd4dc48`)
> checker ที่รันเองบนสำเนา `git archive 348ccb4a`: `approved_status_writer_check` ✅ (5 ≤ baseline 5) · `terminal_status_writer_check` ✅ ·
> `record_arg_check` ✅ · `service_interface_check` ✅ · `required_call_site_check` รันไม่จบใน 10 นาที (เครื่องช้า — ไม่ได้ยืนยันซ้ำ)

## สรุป

| ID | ระดับ | สถานะ | เรื่อง |
|---|---|---|---|
| RV1F-1 | P2 | CONFIRMED | ปิดธงด้วย "เลขที่ใบลดหนี้" แล้วระบบ **ยกเลิกใบเสร็จ** ⇒ ภาษีขายหายจากเดือนเดิมย้อนหลัง แต่ใบลดหนี้ไม่มีในระบบ |
| RV1F-2 | P2 | CONFIRMED | ถอยภาษีขายตอนปิดธงลงวันที่ **วันที่ใบแจ้งหนี้** (ไม่ใช่เดือน tax point) · งวดนั้นปิด ⇒ ล้มเงียบ แต่ตอบสำเร็จ |
| RV1F-3 | P2 | CONFIRMED | เช็คเด้ง → รับชำระใหม่ (ใบรับเปล่า) → ปิดธงใบเสร็จแรก ⇒ การขายนี้ **ไม่มีใบกำกับภาษีที่มีผลเลย** และไม่มีทางออกใบให้ |
| RV1F-4 | P2 | CONFIRMED (gap ตามคำตัดสิน) | ข้อ 46 — ทางเลขอ้างอิงยังไม่บังคับแนบไฟล์หลักฐาน (สตริง ≥3 ตัวพอ) |
| RV1F-5 | P2 | CONFIRMED | SoD ของใบแทน: ผู้ยืนยันเห็นแค่ "เหตุผล" — **ไม่เห็นผู้ซื้อใหม่/หมายเหตุ/คำบรรยาย** ที่กำลังอนุมัติ (ข้อ 42 อ้างว่า "คนที่สองเห็นจริง") |
| RV1F-6 | P2 | CONFIRMED (gap ตามคำตัดสิน) | ข้อ 43 — `VoidDocumentAsync`/`RestoreVoidedDocumentAsync` + ด่าน unpost ฝั่ง "เอกสาร" (ไม่ใช่ใบเสร็จ) ยังดู `Accepted` อย่างเดียว · P1 ครอบแค่ใบเสร็จ |
| RV1F-7 | P3 | CONFIRMED | race: "ยกเลิกคำขอ" ไม่ล็อกแถว — ชนกับ "ยืนยัน" แล้วตอบ "ใบนี้ไม่ถูกแตะ" + audit ว่ายกเลิกคำขอ ทั้งที่ใบถูกแทนแล้ว |
| RV1F-8 | P3 | PLAUSIBLE | race: ปิดธงไม่ล็อกใบต้นทาง — ชนกับรับชำระใหม่ ⇒ ใบรับเปล่า + ภาษีขายถูกถอยกลับ 21913 = VAT หายจาก ภ.พ.30 |
| RV1F-9 | P3 | PLAUSIBLE | หลักฐานทาง (ก) "e-Tax ยกเลิกในระบบ" = ผู้ใช้กด void แถว **Submitted** ในระบบเราเอง (ไม่ได้ส่งยกเลิกถึงกรมสรรพากร) |
| RV1F-10 | P3 | PLAUSIBLE | ใบเสร็จที่ถึงกรมสรรพากรแล้วถูก soft-delete ตอนปิดธง ⇒ หายจากรายการ/รายงาน (เลขใบกำกับที่ออกจริงมองไม่เห็นว่าถูกยกเลิก) |
| RV1F-11 | P3 | PLAUSIBLE | เทสต์ allowlist (R7) ครอบเฉพาะช่องค่า (value/string) — ช่องชนิดอ้างอิงที่ map เป็นคอลัมน์ในอนาคต (`string[]`/`byte[]`/jsonb) ไม่ถูกบังคับจัดกลุ่ม |
| RV1F-12 | P3 | PLAUSIBLE | `PreparerName`/`PreparerSignatureBase64` ของใบเดิมตามไปใบแทน (ผู้จัดทำใบใหม่คือผู้ขอ) |
| RV1F-13 | P3 | PLAUSIBLE | unpost-race (P2): ใบต้นทางถูกยกเลิกในขั้น 1 ขณะใบเสร็จติดธงยังถือแถว ภ.พ.30 ⇒ GL กลับแล้วแต่รายงานยังนับ |
| — | — | NOT-A-BUG | R6 golden `ApproveDocumentAsync` · race ยืนยันพร้อมกัน · R2 ทิศตรงข้าม · R2 รับชำระใหม่ไม่ออก VAT ซ้ำ · ภ.พ.30 เดือนเดียวกัน · P4 · คอมไพล์ 6 ข้อ · R7 ช่องสืบทอดหลัก |

---

## 1. R2 ผลตาม — ไล่ด้วยตัวอย่างตัวเลข

ตัวอย่าง: INV-1 ใบแจ้งหนี้บริการ 1,000 + VAT 70 ลง 5 ม.ค. (VAT พัก 21913) · 10 ก.พ. รับเช็ค 1,070 ⇒ RC-1 ใบเสร็จถือ VAT 70
(`CarriesTaxInvoiceRole` true) + JE reclass 10 ก.พ. Dr 21913 / Cr 21911 70 · `OutputVatDueAt = 10 ก.พ.` (`DocumentService.cs:14203,14230`) · e-Tax RC-1 Accepted

**(ก) เช็คเด้ง 20 ก.พ. — ใบเสร็จถึงกรมสรรพากรแล้ว** — NOT-A-BUG
- `ReversePaymentInternalAsync` (`DocumentService.cs:9751`): `DecideAutoReceiptOnPaymentVoidAsync` ⇒ FlagEtaxCancellation (ChequeBounce ห้ามบล็อก) ·
  JE รับชำระกลับ · ลูกหนี้เปิด 1,070 · RC-1 ติดธง ไม่ถูกยกเลิก (`:9587-9595`)
- `:9843-9856`: `FlaggedReceiptKeepsTaxPoint(Flag, 70)` = true ⇒ `ShouldUndoOutputVatReclass` = false ⇒ ไม่ถอย · 21911 ก.พ. = 70
- ภ.พ.30 ก.พ. (`TaxService`): INV-1 ถูกข้ามเพราะ `invoiceIdsOwnedByVatReceipt` (RC-1 ยังมีผล) · RC-1 เป็นเจ้าของแถว 70 ⇒ **GL 70 = รายงาน 70** ✅
- 25 ก.พ. ลูกค้าจ่ายสด 1,070: `CreatePaymentAsync` `:12481-12483` — `singleShotFull` true (payment เก่า soft-delete · `priorPaidBefore` 0) แต่
  `LiveVatReceiptExistsAsync` = true ⇒ RC-2 ใบรับเปล่า VAT 0 · `TryReclassifyUndueOutputVatAsync` ข้ามเพราะ `OutputVatDueAt` ตั้งอยู่แล้ว ⇒ ภ.พ.30 ก.พ. ยัง 70
  (**ไม่นับซ้ำ**) ✅ · เส้นออกย้อนหลัง `IssueReceiptForPaymentAsync` `:12102-12104` ใช้กติกาเดียวกัน ✅
- ยกเลิกการชำระ RC-2 ภายหลัง: RC-2 VAT 0 ⇒ `LiveVatReceiptExistsAsync(except RC-2)` เห็น RC-1 ⇒ ไม่ถอย ✅

**(ข) ทิศตรงข้าม — เช็คเด้งของใบเสร็จที่ยังไม่ส่ง e-Tax** — NOT-A-BUG (ในแง่ "เหมือนเดิม")
- Decision = Void ⇒ `FlaggedReceiptKeepsTaxPoint` false · `LiveVatReceiptExistsAsync(exceptReceiptId: RC-1)` false (ต้อง except เพราะ RC-1 ถูก void ใน
  change tracker แต่คิวรี AsNoTracking ยังเห็นแถว DB เดิม — ทีมใส่ไว้ถูก) ⇒ `TryUndoUndueOutputVatReclassAsync` เรียกเหมือนก่อนแก้ ✅
- แต่ "เหมือนเดิม" รวมถึงข้อบกพร่องเดิมเรื่องเดือน — ดู RV1F-2

### RV1F-3 · P2 · CONFIRMED — เช็คเด้ง → รับชำระใหม่ → ปิดธง ⇒ การขายไม่มีใบกำกับที่มีผล
ต่อจาก (ก): เม.ย. ผู้ใช้ยกเลิก RC-1 ที่กรมสรรพากรแล้วกด "บันทึกว่ายกเลิกทาง e-Tax แล้ว" (`ResolveEtaxCancellationAsync`, `DocumentService.Reissue.cs:581`)
- RC-1 ถูก void + soft-delete (`:623` → `DocumentService.cs:9597-9598`) · `src.PaidAmount` = 1,070 (RC-2) ⇒ `ShouldUndo` false (`:634`) ⇒ `OutputVatDueAt` คง 10 ก.พ.
- ภ.พ.30: RC-1 หาย ⇒ INV-1 ไม่ถูก "เป็นเจ้าของโดยใบเสร็จ" แล้ว ⇒ **แถว INV-1 (ใบแจ้งหนี้ ไม่ใช่ใบกำกับ) โผล่ในเดือน ก.พ.** ด้วยวันที่ 10 ก.พ.
- ผลทางกฎหมาย: เงินจริงรับ 25 ก.พ. (RC-2 = ใบรับเปล่า) · ใบกำกับใบเดียวของการขายนี้ถูกยกเลิกที่กรมสรรพากรแล้ว ⇒ **ไม่มีใบกำกับภาษีที่มีผลเลย** (§86/4 ผู้ขายต้องออก ณ tax point ·
  ผู้ซื้อเคลมภาษีซื้อไม่ได้) และระบบไม่มีทางไปต่อ: `IssueReceiptForPaymentAsync` คืนใบเดิม (RC-2 มีอยู่แล้ว) · RC-2 ยกหัวเป็นใบกำกับไม่ได้
- ข้อความตอบกลับ `DocumentController.ResolveEtaxCancellation` บอกแค่ "ใบต้นทาง … ยกเลิก/แก้ไขต่อได้" — ไม่เตือนว่าต้องออกใบกำกับให้การรับชำระที่เหลือ
- ทางแก้ที่เสนอ: ตอนปิดธง ถ้ายังมีการรับชำระที่ใบเสร็จเป็นใบรับเปล่า ⇒ ปฏิเสธพร้อมทางไปต่อ **หรือ** ออกใบกำกับ ณ วันรับเงินให้การรับชำระนั้น (ผ่านตัวสร้างใบเสร็จตัวเดียว)
  + เทสต์ลำดับเหตุการณ์ 3 ขั้นนี้ (ไม่มีในชุด `VoidReissueR200FTests`)

---

## 2. R3 — `POST document/{id}/etax-cancellation`

ตรวจแล้วถูก (NOT-A-BUG): สิทธิ์ `CanVoidAsync` ก่อนเรียก service (`DocumentController.cs` `ResolveEtaxCancellation`) · tenant ทุกคิวรี (`CompanyId == companyId`) ·
ล็อกแถวใบเสร็จ `FOR UPDATE` (`Reissue.cs:592`) ⇒ ปิดซ้อนสองคนได้ครั้งเดียว · Submitted ⇒ ปฏิเสธ (`DocumentVoidPreconditions.cs` `EtaxCancellationResolution`) ·
รายงานล็อก/งวดปิดของใบเสร็จ ⇒ ปฏิเสธก่อนแตะ (`Reissue.cs:603-610`) · ไม่ประทับสถานะ e-Tax เอง — `ApplyAutoReceiptOnPaymentVoidAsync` void เฉพาะแถวที่
**ไม่อยู่ใน** `EtaxReachedRdStatuses` (`DocumentService.cs:9600-9611`) · audit ใน hash chain พร้อมหลักฐาน

### RV1F-1 · P2 · CONFIRMED — "เลขที่ใบลดหนี้" เป็นหลักฐานได้ แต่ระบบทำเหมือน "ยกเลิก"
- ข้อความขอหลักฐาน: `DocumentVoidPreconditions.cs:223` และ prompt `documents.html:11909` — "เลขอ้างอิงการยกเลิก/**เลขที่ใบลดหนี้**จากระบบ e-Tax"
- แต่ผลคือ RC-1 ถูก Voided + soft-delete เสมอ (`Reissue.cs:623`) ⇒ แถวของ RC-1 หายจาก ภ.พ.30 **เดือนเดิม** (ย้อนหลัง) และถ้าไม่มีการรับชำระเหลือ ภาษีขายถูกถอย
- ถ้าผู้ใช้จัดการที่กรมสรรพากรด้วย "ใบลดหนี้" (ใบกำกับเดิมยังมีผล · ลดภาษีในเดือนที่ออกใบลดหนี้ §86/10) — ระบบเราไม่มีเอกสารใบลดหนี้นั้นเลย ⇒
  ภ.พ.30 เดือนปัจจุบันไม่เห็นการลด **และ** เดือนเดิมถูกลดย้อนหลัง = เล่าคนละเรื่องกับกรมสรรพากร (เดือนเดิมอาจยื่นแล้วแต่ยังไม่ "ล็อก" — V1-P3 ค้างเจ้าของ)
- ทางแก้: แยกหลักฐานสองชนิด — "ยกเลิกใบกำกับ" (void ได้) กับ "ออกใบลดหนี้แล้ว" (ต้องบันทึก CN ในระบบอ้างใบเดิม ไม่ใช่ void) หรือตัดคำว่าใบลดหนี้ออกจากข้อความ

### RV1F-2 · P2 · CONFIRMED — ถอยภาษีขายลงผิดเดือน · งวดปิด = ล้มเงียบแต่ตอบสำเร็จ
- `TryUndoUndueOutputVatReclassAsync` กลับ JE reclass ด้วย `reversalDate: inv.DocumentDate.Date` (`DocumentService.cs:14100`) — JE reclass ลงวันที่
  **วันรับเงิน** (`:14203` `EntryDate = when`) ⇒ ตัวกลับอยู่เดือนใบแจ้งหนี้ (ม.ค.) ขณะที่ตัวตั้งอยู่เดือน tax point (ก.พ.)
- ตัวอย่าง (ข้อ 1 ทิศ (ข) หรือปิดธงเมื่อไม่มีเงินเหลือ): GL 21911 ม.ค. −70 · ก.พ. +70 ขณะที่ ภ.พ.30 ทั้งสองเดือน = 0 ⇒ กระทบยอด GL↔ภ.พ.30 รายเดือนคลาดสองเดือน ·
  ถ้าจ่ายใหม่ มี.ค. ⇒ reclass ใหม่ มี.ค. +70 · สรุปรายเดือน ม.ค. −70 / ก.พ. +70 / มี.ค. +70 vs รายงาน 0/0/70
- ถ้างวด ม.ค. ปิด: `ReverseJournalEntryAsync` โยน "งวดถูกปิดแล้ว" (`AccountingService.cs:1010-1012`) ⇒ `catch` ใน TryUndo แค่ `LogError` (`:14119`) ⇒
  `OutputVatDueAt` คงอยู่ · ใบเสร็จถูก void ⇒ INV-1 กลับมาเป็นเจ้าของแถว ก.พ. ⇒ ภาษีขายของเงินที่ไม่เคยได้รับยังถูกรายงาน **ขณะที่ endpoint ตอบสำเร็จ**
  (F2 ข้อ 7 "ล้มดัง")
- ด่านของ R3 ตรวจงวดของ "วันที่ใบเสร็จ" (`Reissue.cs:608`) แต่ JE ที่ถูกเขียนจริงอยู่ "วันที่ใบแจ้งหนี้" — ด่านกับการเขียนดูคนละวัน
- หมายเหตุ: ตัว TryUndo เป็นของเดิม (void payment ใช้มาก่อน) — V1F ไม่ได้ทำให้แย่ลงในเส้นเดิม แต่ R3 เปิดเส้นใหม่ที่เกิด "หลายเดือนหลัง" ⇒ โอกาสข้ามงวดปิดสูงขึ้นมาก ·
  ทางแก้: ลงตัวกลับวันเดียวกับ JE reclass (หรือเดือนปัจจุบันถ้าเดือนนั้นยื่น/ปิดแล้ว) + ไม่กลืน exception ในเส้น R3

### RV1F-4 · P2 · CONFIRMED (gap ตามคำตัดสินข้อ 46)
- `EtaxCancellationResolution` (`DocumentVoidPreconditions.cs:217-225`) รับ `reference.Length >= 3` เป็นหลักฐานพอ · `ResolveEtaxCancellationRequest` ไม่มีช่องไฟล์ ·
  ไม่เรียก `IAttachmentAccessGate` · ข้อ 46 (ลง `bfd4dc48` หลัง merge) บังคับ "ต้องแนบไฟล์หลักฐาน ผ่านด่านไฟล์แนบตัวเดียว เมื่อใช้ทางเลขอ้างอิง" ⇒ **ยังไม่ทำ**

### RV1F-8 · P3 · PLAUSIBLE — race ปิดธง × รับชำระใหม่
- `CreatePaymentAsync` ล็อกใบต้นทาง `FOR UPDATE` (`DocumentService.cs:12166`) แต่ `ResolveEtaxCancellationAsync` ล็อกแค่ใบเสร็จ แล้วอ่าน `src.PaidAmount` ไม่ล็อก (`Reissue.cs:630`)
- ฉาก READ COMMITTED: P อ่าน RC-1 ยังมีผล ⇒ ใบรับเปล่า · R อ่าน `PaidAmount = 0` (P ยังไม่ commit) ⇒ ถอย reclass + `OutputVatDueAt = null` · P ข้าม reclass
  เพราะเห็น `OutputVatDueAt` ตั้งอยู่ ⇒ จบที่ "รับเงินแล้ว · ใบรับเปล่า · VAT กลับ 21913" = ภาษีขายหายจาก ภ.พ.30 ทั้งก้อน · แก้: ล็อก src `FOR UPDATE` ก่อนอ่าน

### RV1F-9 · P3 · PLAUSIBLE — หลักฐาน (ก) คือสถานะที่ระบบเราประทับเอง
- ทาง `EtaxVoidedInSystem` (`DocumentVoidPreconditions.cs:226`) ผ่านเมื่อไม่มีแถว e-Tax ที่ถึงกรมสรรพากร — ได้มาจาก `EtaxInvoiceService.VoidAsync` ที่ยอม void แถว
  **Submitted** โดยตั้ง `Status = Voided` ใน DB อย่างเดียว (`EtaxInvoiceService.cs:759-767`) ไม่ได้ส่งคำยกเลิกไปกรมสรรพากร ⇒ ถ้ากรมตอบรับภายหลัง ใบกำกับมีผลที่กรม แต่ระบบถอย VAT ไปแล้ว ·
  ข้อ 46 ยอมรับทางนี้โดยไม่ต้องแนบ — แจ้งเจ้าของว่าหลักฐานนี้อ่อนกว่าชื่อ (R1 DECISION_AUDIT)
- ย่อย: เมื่อสถานะที่เหลือเป็น Rejected/Error/Draft (แถวยังไม่ถูก void) คำตัดสินยังบันทึกหลักฐานเป็น `EtaxVoidedInSystem` ลง audit ⇒ ป้ายหลักฐานไม่ตรงข้อเท็จจริง

### RV1F-10 · P3 · PLAUSIBLE — ใบกำกับที่กรมสรรพากรรับแล้วถูก soft-delete
- ปิดธง ⇒ `rcpt.IsDeleted = true` (`DocumentService.cs:9598`) — ใบเสร็จนี้ **ออกเลขใบกำกับถึงกรมสรรพากรแล้ว** แต่หายจากทุกรายการ (global filter) ·
  กฎ #2 A "เลขที่ออกแล้วต้องออกใบยกเลิก" + §87 "ห้ามเว้นบรรทัด/แก้โดยไม่ขีดฆ่า" ⇒ ควรเหลือแถว Voided ที่มองเห็นได้ (ทีมเลือกเองในข้อออกแบบ 5 — ให้เจ้าของทบทวน)

---

## 3. R6 — `ApprovalControlPolicy` + คำขอใบแทน

**NOT-A-BUG (golden เส้นอนุมัติเดิมไม่เปลี่ยน)**: `ApproveDocumentAsync` — `settings is { RequireApprovalForDocuments: true } && TotalAmount >= threshold ?? 0` ≡
`NeedsSignatureFlow` · SoD เดิม (`!IsNullOrEmpty(CreatedBy) && OrdinalIgnoreCase`) ≡ `SelfApprovalBlocked` (ผู้สร้างไม่รู้ = ไม่บล็อก — คงเดิม) · ผู้ซื้อ §86/4
(`rd864Types` + เงื่อนไข walk-in/deposit/declined/นิติบุคคล) ≡ `MustEnforceBuyerFields` + `BuyerBlockingFields` (`TaxInvoiceCompletenessChecker.cs:127-149`) ทุกกิ่ง ·
race ยืนยันพร้อมกัน: `FOR UPDATE` ใบเดิม (`Reissue.cs:165`) + DbContext ต่อคำขอ ⇒ คนที่สองอ่านหลัง commit เห็นคำขอว่าง ⇒ `REISSUE-NO-PENDING` ✅ ·
ผู้ขอยืนยันเอง ⇒ `REISSUE-SOD-SAME-PERSON` ✅ · ระหว่างรอ ใบเดิมถูกเปลี่ยน (รับชำระเพิ่ม/บรรทัดหาย/ผู้ติดต่อ) ⇒ ตอนยืนยันรัน `Evaluate` + `RequestLineIssues` +
`ForbiddenChanges` ใหม่ใต้ล็อก ✅

### RV1F-5 · P2 · CONFIRMED — ผู้ยืนยันไม่เห็นสิ่งที่อนุมัติ
- คำขอเก็บ `ContactId` (ผู้ซื้อใหม่) · `Notes` · คำบรรยายรายบรรทัด ใน `ReissueRequestJson` แต่ `DocumentResponse` echo แค่ `ReissueRequestedAt/By/Reason`
  (`DocumentService.cs:17595-17597`) · แถบ "รอยืนยัน" แสดงแค่เหตุผล (`documents.html:9819`) · ปุ่มยืนยันส่ง `{confirmPendingRequest:true}` แล้วเซิร์ฟเวอร์ทำตาม JSON ที่ผู้ขอบันทึก
- ฉาก: ผู้ขอ A เลือกผู้ซื้อเป็นนิติบุคคลอื่น (ออกเลขใบกำกับจริงให้คนละบริษัท) พิมพ์เหตุผล "สะกดชื่อผิด" · B เห็นแค่ข้อความนั้นแล้วกดยืนยัน ⇒ SoD ผ่านตามรูปแบบ แต่ **ความเสี่ยงที่ข้อ 42 ระบุว่า
  "คนเดียวเปลี่ยนผู้ซื้อ — ปิดแล้ว" ยังเปิดอยู่** · ทางแก้: echo ผู้ซื้อใหม่ (ชื่อ/เลขภาษี/สาขา) + รายการที่เปลี่ยน (`AllowedChanges` ที่คำนวณได้อยู่แล้ว) ให้ผู้ยืนยันเห็นก่อนกด

### RV1F-7 · P3 · CONFIRMED — race ยกเลิกคำขอ × ยืนยัน
- `CancelReissueRequestAsync` (`Reissue.cs:436-467`) ไม่เปิดธุรกรรม ไม่ `FOR UPDATE` · entity ไม่มี concurrency token (ไม่มี xmin/RowVersion ใน `Document`)
- ฉาก: C อ่านใบ (คำขอยังค้าง) ขณะ B อยู่ในธุรกรรมยืนยัน · B commit (ใบเดิม Voided + ใบแทน) · UPDATE ของ C (เฉพาะคอลัมน์คำขอ + UpdatedAt/By) รอล็อกแล้วผ่าน ⇒
  ตอบ "ยกเลิกคำขอยกเลิกและออกใบแทนแล้ว — ใบนี้ไม่ถูกแตะ" + audit `void-and-reissue-request-cancelled` ทั้งที่ใบถูกแทนไปแล้ว (F2 ข้อ 3/7 — ข้อความที่ไม่จริง) ·
  แก้: ล็อกแถว + ตรวจ `Status != Voided && ReplacedByDocumentId == null` ใต้ล็อก
- ย่อย: คำขอที่ค้างไม่ถูกล้างเมื่อใบเดิมพ้นสภาพ "รับชำระโดยรอบโอน" ทางอื่น (เช่นยกเลิกการลงบัญชีรอบโอน) ⇒ แถบรอยืนยันค้างบนใบที่ปุ่มยืนยันจะตอบ 409 เสมอ (มีปุ่มยกเลิกคำขอเป็นทางออก — แค่ UX)

---

## 4. R7 — allowlist

- NOT-A-BUG: เทสต์ `R200_V1F_R7_ทุกช่องค่า…ครบ_ไม่ซ้ำ` ใช้ reflection (`SettlementPaidReissue.cs:431-438`) + assert สองทิศ (ขาด/เกิน) + ไม่ซ้ำ ⇒ ช่องค่าใหม่ใน
  `Document`/`DocumentLine` ทำเทสต์ล้มพร้อมชื่อจริง · ไล่ entity ที่ `348ccb4a` เอง: ครบทุกช่องค่า (เหลือแค่ navigation `Brand`/`Contact`/`Lines`/…)
- ช่องสืบทอดที่กฎ #4 A ต้องการ — **ตามไปครบ**: `DocumentLanguage` · `BranchId` + `IssuerBranchCode` (สาขา) · `PaymentType` + `PaymentTerms` + `CreditDays` (ช่องทาง/เทอม) ·
  `Notes` + `CustomFooterNotes`/`CustomTermsAndConditions`/`CustomAppendix` (หมายเหตุลูกค้า) · `Reference` · `TaxPointDate`/`OutputVatDueAt` · `PricesIncludeVat` · `Currency`/`ExchangeRate`
- **RV1F-11 · P3 · PLAUSIBLE**: ตัวกรอง `t.IsValueType || t == typeof(string)` ตัดชนิดอ้างอิงทุกตัว ⇒ ถ้าเพิ่มคอลัมน์ `string[]`/`List<string>`/`byte[]`/`JsonDocument` (Npgsql map ได้)
  เทสต์ไม่ฟ้อง และตัวคัดลอก allowlist จะ "ไม่พาไป" เงียบ ๆ (ทิศปลอดภัยสำหรับหลักฐาน แต่ทิศเงียบสำหรับช่องที่ลูกค้าเห็น) · แก้: นับทุก property ที่ EF map (`IEntityType.GetProperties()`) แทน reflection ล้วน
- **RV1F-12 · P3 · PLAUSIBLE**: `PreparerName`/`PreparerSignatureBase64` อยู่ใน Carried — ลายเซ็นผู้จัดทำใบเดิมพิมพ์บนใบแทนที่ผู้ขอเป็นคนจัดทำ (อาจตั้งใจ — ให้เจ้าของตัดสิน)

---

## 5. P1 / P2 / P4

- **P1** `EffectiveEtaxAsync` ใช้ครบ 3 เส้นที่อ้าง ✅ (`DecideAutoReceiptOnPaymentVoidAsync` `DocumentService.cs:9573` · `EvaluateSettlementPaidReissueAsync` `Reissue.cs:71` ·
  `SettlementPostingService.LoadUnpostFactsAsync` ส่วนใบเสร็จ `:1292-1296`)
- **RV1F-6 · P2 · CONFIRMED (gap ตามคำตัดสินข้อ 43)**: ยังดู `e.Status == EtaxStatus.Accepted` อย่างเดียว (ไม่เห็น Submitted/อีเมลประทับเวลา): `VoidDocumentAsync`
  (`DocumentService.cs:8070`, และพลิก Submitted เป็น Voided ที่ `:8311`) · `RestoreVoidedDocumentAsync` (`:8690`) · `SettlementPostingService.LoadUnpostFactsAsync` ส่วน
  **เอกสาร**ของรอบโอน (`:1253`) · `OrphanChildrenAsync` (`:921`) ⇒ ด่าน unpost ของใบขายที่ส่ง e-Tax by Email ผ่าน แล้ว `VoidDocumentAsync` ยกเลิกเงียบ ·
  R5 integration สั่งยกเลิกใบแทนที่ e-Tax Submitted ⇒ ถูกพลิก Voided (ตรงกับที่ R4 แก้ในเส้นออกใบแทน) — ต้องทำพร้อมข้อ 43
- **P2** ติดธงแทน throw ✅ — `AutoReceiptOnPaymentVoid` คืน Flag สำหรับ `SettlementUnpost` · `UnpostCoreAsync` เก็บ `EtaxCancellationFlag` เข้าข้อความผลลัพธ์ (ไม่ทิ้งผล) ·
  **RV1F-13 · P3 · PLAUSIBLE**: ขั้น 1 ยกเลิกเอกสารของรอบโอนก่อนขั้น 2 ยกเลิกการชำระ — ถ้าใบต้นทางถูกยกเลิก (JE รวม reclass ถูกกลับ) ขณะใบเสร็จที่ติดธงยังมีผล
  `TaxService` ยังนับแถวใบเสร็จ (เงื่อนไข `rcptSrcInfo.OutputVatDueAt != null` ไม่ดูสถานะใบต้นทาง) ⇒ GL กลับแล้วแต่รายงานยังนับ — เกิดได้เฉพาะ race ที่ทีมระบุ
- **P4** NOT-A-BUG (แต่ premise ไม่มีผล): hooks + `GetDocumentAsync` อยู่นอก lambda หลัง `CommitAsync` ✅ (`Reissue.cs:413,428-431`) · `approved_status_writer_check` ✅ ·
  อย่างไรก็ตามทั้งเรพ **ไม่มี `EnableRetryOnFailure`** ⇒ execution strategy เป็นแบบไม่ retry — "retry แล้วรอบสองเห็นใบ Voided" เกิดไม่ได้อยู่แล้ว · ถ้าเปิด retry ในอนาคต
  lambda ต้อง `ChangeTracker` สะอาดต้นรอบ (entity ที่แก้ค้างจากรอบแรกจะถูก identity-resolution คืนมา)

## 6. คอมไพล์ (อ่านโค้ด — ไม่มี SDK)

ทั้งหมด NOT-A-BUG ตามการอ่าน:
1. `DocumentResponse` +3 พารามิเตอร์ท้ายมีค่าเริ่มต้น · ผู้สร้างทั้งเรพ 3 จุด (`MapToResponse` named args · stub ลับ `DocumentService.cs:2069,2109` named args) — ไม่มีจุดส่งแบบ positional ถึงท้าย ✅
2. `strategy.ExecuteAsync` สอง return: `(Neo: (Document?)null, Receipts: newReceipts, Message: control.Reason)` / `(Neo: neo, Receipts: newReceipts, Message: (string?)null)` —
   ชนิด `(Document, List<Document>, string)` ชื่อ element ตรงกัน ⇒ อนุมาน `ExecuteAsync<TResult>(Func<Task<TResult>>)` ได้ ✅
3. `for (…; i < 10 && hop is { Status: Voided, ReplacedByDocumentId: Guid nextId }; i++)` — expression variable ในเงื่อนไข for มี scope ถึง body ✅ (`IntegrationService.cs` ~4388)
4. STJ + positional record `ReissueSettlementPaidRequest(…, bool? ConfirmPendingRequest = null)` — ctor param ที่ขาดได้ default · ทุกช่อง nullable ⇒ ไม่มี implicit `[Required]` ✅
5. `foreach (var (oldLineId, newLineId) in lineMap)` บน `IReadOnlyDictionary<Guid,Guid>` — `KeyValuePair.Deconstruct` ✅
6. อื่น ๆ: `GetValueOrDefault` บน `Dictionary<Guid, EtaxStatus?>` (มีใช้ในเรพแล้ว) · `BusinessRuleException` อยู่ `Accounting.Helpers` ✅ · `SettlementPaidReissueRequestCodec` internal + `InternalsVisibleTo` ✅ ·
   `Models.Enums.*` ใน `Services.Implementations.Tax` ไม่มี namespace ชื่อ `Models` ชั้นใน ✅ · `CarriesTaxInvoiceRole` ผู้เรียกทุกจุด (2 service + เทสต์ 6) ส่ง 4 อาร์กิวเมนต์ ✅

## 7. สิ่งที่ยังไม่ครอบด้วยเทสต์ (F2 ข้อ 2 — เทสต์เรียกแค่ helper)
เทสต์ V1F ทั้งหมดเป็น pure helper · ไม่มีเทสต์ลำดับเหตุการณ์ของ service: เช็คเด้ง→รับใหม่→ปิดธง (RV1F-3) · ปิดธงเมื่อไม่มีเงินเหลือแล้วดูวันที่ JE ตัวกลับ (RV1F-2) ·
บันทึกคำขอ→ยืนยัน/ยกเลิก (สถานะคอลัมน์คำขอ) · `required_call_site_check` ล็อกว่ามีการเรียก แต่ไม่ล็อก "วันที่" หรือ "ลำดับเหตุการณ์ข้ามคำขอ"

---

## ตารางสถานะ (ทีม V1G · รอบ 200 · รายงาน `team-V1G.md`)

| ID | สถานะ | ที่แก้ | เทสต์ |
|---|---|---|---|
| RV1F-1 | ✅ <pending> | `DocumentVoidPreconditions.EtaxCancellationResolution(EtaxCancellationClaim)` แยกทาง `EtaxCancellationPath.CancelledAtRd`/`CreditNote` · ทาง (ข) ต้องเป็นใบลดหนี้ในระบบ (`EtaxCreditNoteFact` · `Documents.EtaxCancelledByCreditNoteId` + unique index) ใบเสร็จเดิมคงมีผล · `ResolveEtaxCancellationAsync` · ตัวเลือก `GET …/etax-cancellation/credit-notes` · modal ใน documents.html | `R200_V1G_RV1F1_*` (3) |
| RV1F-2 | ✅ <pending> | `UndoUndueOutputVatReclassAsync` ลงวันที่ JE ย้ายภาษีเอง (ไม่ catch) · `OutputVatPeriodLockReasonAsync` · `ReclassLockReasonAsync` · `OutputVatUndoOnPaymentVoid` ใน `ReversePaymentInternalAsync` (ผู้ใช้ 409 · เช็คเด้ง/ยกเลิกการลงบัญชี ธง + `PaymentVoidResult.OutputVatNotice`) · ด่าน R3 ตรวจวันที่ใบเสร็จ + วันที่ JE ย้ายภาษี + วันรับเงินที่เหลือ | `R200_V1G_RV1F2_*` (3) |
| RV1F-3 | ✅ <pending> | `EtaxCancellationFollowUp` — ถอย/ย้ายภาษีไปวันรับเงินจริง + ออกใบกำกับ ณ วันรับเงินในธุรกรรมเดียว (`CreateSettlementReceiptAsync(carryVatFromSource: true)` · ยกเลิกใบรับเปล่าเดิมแบบคงแสดง · hooks หลัง commit) | `R200_V1G_ลำดับ_*` (5) |
| RV1F-4 | ✅ <pending> | `DocumentController.ResolveEtaxCancellation` เดิน `IAttachmentAccessGate` (+ แถว `attachment_gate_check`) · service ตรวจไฟล์เป็นของใบเสร็จนี้ (`a.EntityId == rcpt.Id`) | `R200_V1G_RV1F4_*` |
| RV1F-5 | ✅ <pending> | `Helpers/SettlementPaidReissueRequestView` (Build · Hash · ConfirmMismatch) · `BuildReissueRequestViewAsync` (หน้าจอ + ยืนยันใต้ล็อก) · `ReissueSettlementPaidRequest.ConfirmRequestHash` · `DocumentResponse.ReissueRequestDetail` · แถบรอยืนยันแสดงเดิม→ใหม่ | `R200_V1G_RV1F5_*` (2) |
| RV1F-6 | ✅ <pending> | `DocumentVoidEtaxBlock` + `EffectiveEtaxAsync`: `VoidDocumentAsync` (cascade เฉพาะแถวที่ยังไม่ถึง) · `RestoreVoidedDocumentAsync` · `SettlementPostingService.LoadUnpostFactsAsync`/`OrphanChildrenAsync` (+ `EtaxSubmitted`) · integration void ผ่าน `VoidDocumentAsync` · `EtaxStatus.Accepted` ที่ตัดสินเองในเส้นยกเลิก = 0 จุด | `R200_V1G_RV1F6_*` (3) |
| RV1F-7 | ✅ <pending> | `CancelReissueRequestAsync` ธุรกรรม + `FOR UPDATE` + ตรวจ `Voided`/`ReplacedByDocumentId` ใต้ล็อก (`REISSUE-ALREADY-REPLACED`) | required_call_site |
| RV1F-8 | ✅ <pending> | `ResolveEtaxCancellationAsync` ล็อกใบต้นทางก่อนใบเสร็จ (ลำดับเดียวกับ `CreatePaymentAsync`) | required_call_site (`before`) |
| RV1F-9 | ✅ <pending> | `EtaxCancellationEvidence.EtaxNeverReachedRd` แยกจาก `EtaxVoidedInSystem` · `EvidenceLabel` ลง audit/หมายเหตุ ("ไม่ใช่คำยืนยันจากกรมสรรพากร") · ข้อจำกัด `EtaxInvoiceService.VoidAsync` ยังไม่ส่งคำยกเลิกถึงกรมสรรพากร = คำถามค้าง | `R200_V1G_RV1F9_*` |
| RV1F-10 | ✅ <pending> | `ApplyAutoReceiptOnPaymentVoidAsync(…, keepVisible: true)` ในเส้นปิดธง — Voided คงแสดง ไม่ soft-delete (ภ.พ.30 กรอง Voided อยู่แล้ว) | required_call_site (`call_args`) |
| RV1F-11 | ✅ <pending> | เทสต์นับทุก property ที่ EF map (`IEntityType.GetProperties()`) | `R200_V1G_RV1F11_*` |
| RV1F-12 | ✅ <pending> | `PreparerName`/`PreparerSignatureBase64` ย้ายไป `DocumentNotCarriedFields` (ผู้จัดทำ = ผู้ขอ ผ่าน `CreatedBy`) | `R200_V1G_RV1F12_*` |
| RV1F-13 | NOT-A-BUG | สภาพ "ใบต้นทางถูกยกเลิกขณะใบเสร็จติดธงยังมีผล" เกิดไม่ได้: `VoidDocumentAsync` ปฏิเสธใบต้นทางที่มีเอกสารลูกยังมีผล (`ChildBlocksAsync` — ใบเสร็จอัตโนมัติ `RelatedDocumentId` = ใบต้นทาง) · cascade ยกเลิกการชำระใน `VoidDocumentAsync` ใช้ `PaymentVoidCause.User` ⇒ ใบเสร็จที่ถึงกรมสรรพากร = ปฏิเสธ (ไม่ติดธง) · ธงเกิดได้เฉพาะเช็คเด้ง/`VoidPaymentAsync(SettlementUnpost)` ซึ่งไม่ยกเลิกใบต้นทาง | — |
| ข้อ 44 | ✅ <pending> | `GET document/etax-reissue-review` (`GetEtaxReissueReviewAsync` · `Helpers/EtaxReissueReview`) อ่านอย่างเดียว + แถบบนหน้ารายการ | `R200_V1G_ข้อ44_*` (2) |
