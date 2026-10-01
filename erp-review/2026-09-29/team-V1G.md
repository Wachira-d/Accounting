# รอบ 200 · ทีม V1G — แก้ผลฝ่ายค้านรอบสองของทีม V1F (ยกเลิก-ออกใบแทน · เช็คเด้ง · ปิดธง e-Tax)

> แหล่ง: `review200-round2-V1F.md` (ตารางสถานะอยู่ท้ายไฟล์นั้น) · DECISIONS ข้อ 9, 11, 42–49 · CLAUDE.md กฎเหล็ก #2 A/C/F · #4 F2 ข้อ 3, 7 ·
> **ยังไม่ได้คอมไพล์ในเครื่องนี้ (ไม่มี .NET SDK) — รบกวน rebuild + `dotnet test` ฝั่ง CI/ผู้ใช้**
>
> worktree ถูกสร้างจาก `69fd88e8` (master เก่า) ⇒ `reset --hard origin/claude/erp-system-review-team-660mev` (`36a0aad5`) ก่อนเริ่ม

## ตารางรายการ

| ข้อ | ระดับ | สถานะ | ที่แก้ (file:method) | เทสต์ |
|---|---|---|---|---|
| **RV1F-1** ปิดธงด้วยเลขที่ใบลดหนี้แล้วระบบยกเลิกใบเสร็จ (ข้อ 47) | P2 | ✅ | `Helpers/DocumentVoidPreconditions.cs` `EtaxCancellationResolution(EtaxCancellationClaim)` — `EtaxCancellationPath.CancelledAtRd` (ก) / `CreditNote` (ข) · `EtaxCreditNoteFact` (ออกแล้ว · อ้างใบเสร็จ/ใบต้นทาง · ผู้ซื้อเดียวกัน · VAT ≥ ของใบเสร็จ · ยังไม่เคยใช้ · ใบต้นทางไม่มีเงินรับที่มีผล) · `Models/Entities/Document.EtaxCancelledByCreditNoteId` + `DatabaseMigrationHelper` ADD COLUMN + unique index · `DocumentService.Reissue.cs` `ResolveEtaxCancellationAsync` (ทาง ข: ใบเสร็จคงมีผล ไม่แตะภาษีเดือนเดิม) · `ListEtaxCancellationCreditNotesAsync` + `GET {id}/etax-cancellation/credit-notes` · modal `etaxCancelModal` ใน `documents.html` | `R200_V1G_RV1F1_ทางใบลดหนี้_ต้องมีใบลดหนี้ในระบบ…` · `…ไม่มีใบจริงหรือใบไม่ตรง_ปฏิเสธ…` (Theory 8) · `…ใบไม่ถึงกรมสรรพากร_หรือยังมีเงินรับที่มีผล…` |
| **RV1F-2** ถอยภาษีลงวันที่ใบแจ้งหนี้ + งวดปิดกลืน error (ข้อ 48) | P2 | ✅ | `DocumentService.cs` `UndoUndueOutputVatReclassAsync` (แทน `TryUndoUndueOutputVatReclassAsync` — ลงวันที่ JE ย้ายภาษีเอง · ไม่ catch) · `OutputVatReclassJournalsAsync` · `OutputVatPeriodLockReasonAsync` (งวดบัญชีไม่เปิด / ภ.พ.30 ยื่น·ประกาศ·ล็อก) · `ReclassLockReasonAsync` · `ReversePaymentInternalAsync` → `DocumentVoidPreconditions.OutputVatUndoOnPaymentVoid` (ผู้ใช้/cascade ยกเลิกเอกสาร = 409 `RD-78/1-VAT-UNDO-LOCKED` · เช็คเด้ง/ยกเลิกการลงบัญชี = ไม่บล็อก + ธง `[VAT-UNDO-BLOCKED]` บนใบต้นทาง + `PaymentVoidResult.OutputVatNotice` → `ChequeService` log/webhook `outputVatUndoBlocked` · `SettlementPostingService.UnpostCoreAsync` ข้อความผล) · ด่าน R3 ตรวจวันที่ใบเสร็จ (`OutputVatPeriodLockReasonAsync(rcpt.DocumentDate)` แทน `ClosedPeriodNameAsync`) + วันที่ JE ย้ายภาษี + วันรับเงินที่เหลือ | `R200_V1G_RV1F2_เดือนที่ตั้งรายการปิด_ผู้ใช้…ปฏิเสธดัง…` · `…เช็คเด้งห้ามบล็อก…ติดธง…` (Theory 2) · `…ทิศตรงข้าม_เดือนเปิด_ถอยได้ทุกทางเข้า…` (Theory 3) |
| **RV1F-3** เช็คเด้ง→รับใหม่→ปิดธง ⇒ ไม่มีใบกำกับที่มีผล (ข้อ 48) | P2 | ✅ | `DocumentVoidPreconditions.EtaxCancellationFollowUp(EtaxCancelFollowUpFacts)` — ไม่มีเงินเหลือ ⇒ ถอยในเดือนที่ตั้งรายการ · มีเงินที่มีผล ⇒ ถอยในเดือนเดิม + ย้าย ณ วันรับเงินจริงครั้งแรก + (รับครบงวดเดียว — `CarriesTaxInvoiceRole`) ออกใบกำกับ ณ วันรับเงิน · ใบรับเปล่าส่ง e-Tax แล้ว/งวดปิด = 409 · service: `CreateSettlementReceiptAsync(carryVatFromSource: true)` · ยกเลิกใบรับเปล่าเดิมแบบคงแสดง · `TryReclassifyUndueOutputVatAsync(…, throwOnFailure: true)` · ผู้กดต้องมีสิทธิ์อนุมัติใบเสร็จ · e-Tax อัตโนมัติหลัง commit | `R200_V1G_ลำดับ_เช็คเด้ง_รับชำระใหม่_ปิดธง…` (ไล่ 3 ขั้นด้วยตัวตัดสินจริง) · `…รับใหม่วันเดียวกับใบเดิม…` · `…งวดปิดหรือใบรับส่งeTaxแล้ว…` (Theory 3) · `…ไม่มีการรับชำระเหลือ…` · `…ทิศตรงข้าม_ใบเสร็จถือVATอื่น…_รับหลายงวด…` |
| **RV1F-4** ทางเลขอ้างอิงไม่บังคับไฟล์ (ข้อ 46) | P2 | ✅ | `DocumentController.ResolveEtaxCancellation` เรียก `IAttachmentAccessGate.DenyAttachmentAsync("Document", documentId, Read, …, request.EvidenceAttachmentId)` ก่อน service (แถวใหม่ `tools/attachment_gate_check.py`) · service ตรวจว่าไฟล์เป็นของใบเสร็จนี้ (`a.EntityId == rcpt.Id`) · ตัวตัดสินบังคับไฟล์เมื่อใบถึงกรมสรรพากร · หน้าเว็บอัปโหลดผ่านเส้นไฟล์แนบปกติแล้วส่ง id | `R200_V1G_RV1F4_ถึงกรมสรรพากรแล้ว_เลขอ้างอิงอย่างเดียวไม่พอ…_ทิศตรงข้าม…` |
| **RV1F-5** ผู้ยืนยันไม่เห็นคำขอ (ข้อ 49) | P2 | ✅ | `Helpers/SettlementPaidReissueRequestView.cs` (ใหม่: `Build` · `Hash` SHA-256 canonical · `ConfirmMismatch`) · `BuildReissueRequestViewAsync` + `PartySnapshot` (ที่อยู่ผ่าน `ThaiAddressFormatter.ResolvePartyAddress`) ตัวเดียวของหน้าจอ (`GetDocumentAsync`) และยืนยันใต้ล็อก · `ReissueSettlementPaidRequest.ConfirmRequestHash` · `DocumentResponse.ReissueRequestDetail` · 409 `REISSUE-REQUEST-CHANGED` · แถบรอยืนยันแสดงตารางเดิม→ใหม่ (`Layout.esc` ทุกช่อง · hash ผ่าน `data-hash`) · `api.confirmReissueSettlementPaid(id, hash)` | `R200_V1G_RV1F5_ผู้ยืนยันเห็นผู้ซื้อเดิมและใหม่…` · `…คำขอหรือผู้ซื้อเปลี่ยนหลังเปิดดู_hashเปลี่ยน…` |
| **RV1F-6** ยกเลิกเอกสารยังดูแค่ Accepted (ข้อ 43) | P2 | ✅ | `DocumentVoidPreconditions.DocumentVoidEtaxBlock` · `VoidDocumentAsync` (ด่าน + cascade เฉพาะแถวที่ยังไม่ถึง — ไม่พลิก Submitted) · `RestoreVoidedDocumentAsync` · `SettlementPostingService.LoadUnpostFactsAsync`/`OrphanChildrenAsync` (`EffectiveEtaxAsync` · `SettlementUnpostDocument/SettlementOrphanChild.EtaxSubmitted` = NeedsUserAction) · integration void เดินผ่าน `VoidDocumentAsync` | `R200_V1G_RV1F6_ยกเลิกเอกสารที่eTaxส่งแล้วหรือตอบรับ…` (Theory 2) · `…ทิศตรงข้าม_eTaxยังไม่ถึง…` (Theory 5) · `…ยกเลิกการลงบัญชีรอบโอน_เอกสารeTaxส่งแล้ว…` |
| **RV1F-7** race ยกเลิกคำขอ × ยืนยัน | P3 | ✅ | `CancelReissueRequestAsync` — execution strategy + ธุรกรรม + `FOR UPDATE` + อ่านใหม่ใต้ล็อก · ใบ Voided/ถูกแทนแล้ว ⇒ 409 `REISSUE-ALREADY-REPLACED` พร้อมเลขใบแทน | required_call_site (`before`/`must_re`) |
| **RV1F-8** race ปิดธง × รับชำระใหม่ | P3 | ✅ | `ResolveEtaxCancellationAsync` ล็อกใบต้นทาง `FOR UPDATE` ก่อนใบเสร็จ (ลำดับเดียวกับ `CreatePaymentAsync`) แล้วอ่าน `PaidAmount`/การรับชำระใต้ล็อก | required_call_site (`before`) |
| **RV1F-9** ป้ายหลักฐานเกินจริง | P3 | ✅ (บางส่วน) | `EtaxCancellationEvidence.EtaxNeverReachedRd` แยกจาก `EtaxVoidedInSystem` (ต้องมีแถวที่ถูกยกเลิกจริง) · `EvidenceLabel` ลง audit (`evidenceLabel`) + หมายเหตุภายใน "ไม่ใช่คำยืนยันจากกรมสรรพากร" · ตัว `EtaxInvoiceService.VoidAsync` ที่ยอมยกเลิกแถว Submitted ในฐานเราไม่ได้แก้ ⇒ คำถามค้าง Q2 | `R200_V1G_RV1F9_ป้ายหลักฐาน…` |
| **RV1F-10** ใบเสร็จที่ถึงกรมสรรพากรถูก soft-delete | P3 | ✅ | `ApplyAutoReceiptOnPaymentVoidAsync(…, keepVisible)` — เส้นปิดธงคง Voided ที่มองเห็น (ภ.พ.30 กรอง Voided อยู่แล้ว) · เส้นยกเลิกการชำระเดิมไม่เปลี่ยน | required_call_site (`call_args keepVisible: true` · `forbid IsDeleted = true`) |
| **RV1F-11** เทสต์ allowlist ไม่ครอบชนิดอ้างอิง | P3 | ✅ | เทสต์ใหม่นับทุก property ที่ EF map (`db.Model.FindEntityType(...).GetProperties()` แบบ offline เหมือน `SettlementRulesAndSchemaTests`) | `R200_V1G_RV1F11_ทุกpropertyที่EFmap…` |
| **RV1F-12** ผู้จัดทำใบเดิมตามไปใบแทน | P3 | ✅ | `PreparerName`/`PreparerSignatureBase64` ย้ายไป `DocumentNotCarriedFields` (ผู้จัดทำ = ผู้ขอ ผ่าน `CreatedBy` → `ResolveSignersAsync`) | `R200_V1G_RV1F12_…` |
| **RV1F-13** unpost race GL กลับแต่รายงานนับ | P3 | NOT-A-BUG | สภาพ "ใบต้นทางถูกยกเลิกขณะใบเสร็จติดธงยังมีผล" เกิดไม่ได้: `VoidDocumentAsync` ปฏิเสธใบที่มีเอกสารลูกยังมีผล (`DocumentVoidPreconditions.ChildBlocksAsync` — ใบเสร็จอัตโนมัติ `RelatedDocumentId` = ใบต้นทาง · ด่านยกเลิกการลงบัญชีใช้ตัวเดียวกันก่อนแตะชิ้นแรก) · cascade ยกเลิกการชำระใน `VoidDocumentAsync` ส่ง `PaymentVoidCause.User` ⇒ ใบที่ถึงกรมสรรพากร = ปฏิเสธ ไม่ติดธง · ธงเกิดเฉพาะเช็คเด้ง/`VoidPaymentAsync(SettlementUnpost)` ซึ่งไม่ยกเลิกใบต้นทาง | — |
| **ข้อ 44** รายงานอ่านอย่างเดียวให้นักบัญชี | — | ✅ | `Helpers/EtaxReissueReview.cs` (ใหม่: `FlaggedReceiptVatUndone` · `CarriedExcess` · `ExcessCheckFields` จาก `DocumentNotCarriedFields`) · `GetEtaxReissueReviewAsync` (3 กลุ่ม: ใบเสร็จติดธงที่ภาษีถูกถอยแล้ว · ใบเสร็จที่ปิดธงด้วยเส้น V1F `[ETAX-CANCELLED]` (อาจเป็นใบลดหนี้) · ใบแทนที่พาช่องเกิน) · `GET document/etax-reissue-review` · แถบ `etaxReviewNotice` บนหน้ารายการ · ไม่มีการเขียนใด ๆ (forbid `SaveChangesAsync`) | `R200_V1G_ข้อ44_…` (2) |

## ออกแบบที่ตัดสินเอง (ทิศมองเห็นและย้อนได้ — ให้เจ้าของทบทวน)
1. **ทาง (ข) ผูกใบลดหนี้ที่มีอยู่แล้ว** (สร้างผ่านหน้าเอกสาร/เส้นสร้าง CN เดิม) แทนการให้ endpoint สร้าง CN เอง — ไม่มีสูตร CN ชุดที่สอง · ใบลดหนี้ต้องอ้างใบเสร็จหรือใบต้นทาง ·
   ใบต้นทางต้องไม่มีเงินรับที่มีผล (ไม่งั้นยอดลดหนี้+รับชำระเกินหนี้ §86/10 — ปฏิเสธพร้อมทางไปต่อ)
2. **ยกเลิกการชำระของผู้ใช้เมื่อเดือนย้ายภาษีปิด = 409** (เดิมสำเร็จแต่ภาษีค้าง) — เข้มขึ้นกับเส้นเดิมทุกทางเข้าที่ผู้ใช้กด (รวม cascade จากยกเลิกเอกสาร) ·
   ทางไปต่อ: เปิดงวด/Reject & Reverse หรือใบลดหนี้ · เช็คเด้ง/ยกเลิกการลงบัญชี = ไม่บล็อก + ธง (ข้อ 11)
3. **รับหลายงวดแล้วปิดธง** ⇒ ย้ายจุดความรับผิดไปวันรับเงินครั้งแรกที่เหลือ และให้ใบต้นทางรายงานตามกติกาการรับหลายงวดเดิม (ไม่ออกใบกำกับรายงวด) → Q3
4. **ตัวกลับของ VoidDocument cascade** ก็ลงวันที่ JE ย้ายภาษีเช่นกัน (เดิมลงวันที่ใบแจ้งหนี้) — ขั้นกลับ JE อื่นของ `VoidDocumentAsync` ยังใช้ `ResolveReversalDateAsync` ตามเดิม

## F3 ข้อ 7–12
7. รูปแบบเดิม: `TryUndoUndueOutputVatReclassAsync` เหลือ **0** · `reversalDate: inv.DocumentDate` ในถอยภาษี **0** · `EtaxStatus.Accepted` ที่ตัดสินการยกเลิกเอง (ไม่ผ่าน `EffectiveEtaxAsync`)
   ในเส้นยกเลิก (`VoidDocumentAsync` · `RestoreVoidedDocumentAsync` · `SettlementPostingService` 921/1253) **0** — ที่เหลือคือ `EtaxInvoiceService` (เส้น e-Tax เอง) และการแยก Accepted/Submitted
   บนผลของตัวโหลดเดียว · `ClosedPeriodNameAsync` ในเส้นปิดธง **0** · เส้นปิดธงที่ soft-delete ใบเสร็จ **0**
8. ทางเข้าอื่น: ยกเลิกเอกสาร (หน้าเอกสาร · integration `document.voided` · ยกเลิกการลงบัญชีรอบโอน) เดินตัวตัดสินเดียว · ยกเลิกการชำระ (หน้าเอกสาร · cascade · เช็คเด้ง ·
   ยกเลิกการลงบัญชี) เดิน `OutputVatUndoOnPaymentVoid` ตัวเดียว · เส้นหลายใบ `ReverseMultiDocPaymentInternalAsync` **ไม่ถอยภาษีขายเลย** (ของเดิม) → Q1 · มือถือ/LINE ไม่มีเส้นปิดธง/ออกใบแทน
9. เข้มขึ้น + ทางไปต่อ: ยกเลิกเอกสาร Submitted ⇒ ยกเลิก e-Tax ก่อน (ทิศตรงข้าม `…ทิศตรงข้าม_eTaxยังไม่ถึง…`) · ยกเลิกการชำระเดือนปิด ⇒ เปิดงวด/ใบลดหนี้ (`…ทิศตรงข้าม_เดือนเปิด…`) ·
   ปิดธงเลขอ้างอิงไม่มีไฟล์ ⇒ แนบไฟล์ (`…ทิศตรงข้ามยกเลิกในระบบไม่ต้องแนบ`) · ยืนยันใบแทน hash ไม่ตรง ⇒ โหลดใหม่ (`…hashเดิมถูกปฏิเสธ_ทิศตรงข้ามhashตรงผ่าน`) ·
   ปิดธงงวดปิด ⇒ ทาง (ข) ใบลดหนี้
10. ค่าที่ persist ก่อนแก้: ใบเสร็จที่ติดธงแล้วภาษีถูกถอย · ใบที่ปิดธงด้วยเส้น V1F (ยกเลิก+ซ่อน) · ใบแทนที่คัดลอกช่องเกิน — **ไม่แก้อัตโนมัติ** (ข้อ 44) ⇒ รายงานอ่านอย่างเดียว ·
    ตัวกลับภาษีที่เคยลงวันที่ใบแจ้งหนี้ก่อนรอบนี้ไม่ซ่อม (GL ย้อนหลัง — ต้องให้นักบัญชีตัดสิน) → Q4 · คอลัมน์ใหม่ `EtaxCancelledByCreditNoteId` default null = พฤติกรรมเดิม
11. ฝ่ายค้าน: ขอ main agent ส่ง diff ให้ฝ่ายค้านอีกรอบ (โดยเฉพาะ `ResolveEtaxCancellationAsync` ทาง ก ที่ออกใบกำกับ/ย้ายภาษีในธุรกรรมเดียว · 409 ใหม่ของยกเลิกการชำระ)
12. DOCUMENT_FLOW §2.4c · §3.5 (ขั้น 3 ของยกเลิก + ย่อหน้า V1G) + Last verified · TEST_PLAN §0 (`--row`) · CHANGELOG · ตารางสถานะท้าย `review200-round2-V1F.md`

## checker ที่รัน
`required_call_site_check` (ผลท้ายรายงาน) · `attachment_gate_check` ✅ (13 target + negative test) · `terminal_status_writer_check` ✅ (แถวที่ลดคือ `BankV1Controller` ของทีมอื่น) ·
`approved_status_writer_check` ✅ (5 = baseline) · `record_arg_check` ✅ · `nullable_arg_check` ✅ · `using_check` ✅ · `undeclared_local_check` ✅ · `html_attr_escape_check` ✅ ·
`onclick_js_string_check` ✅ · `js_dup_method_check` ✅ · `write_permission_gate_check` ✅ · `service_interface_check` ✅ · `namespace_shadow_check` ✅ (จับ `Accounting.Helpers.` ใต้
`Accounting.Models.DTOs.Document` ได้ 2 จุด → ใช้ alias ระดับ global) · `tuple_name_merge_check` ✅ · `string_quote_close_check` ✅ · `comment_line_break_check` ✅ ·
`identifier_space_check` ✅ · `verbatim_string_check` ✅ · `accessibility_check` ✅ · `arg_type_check` ✅ · `dead_helper_check` ✅ · `test_inventory --check` ✅ ·
`node --check` documents.html (script block) + api.js ✅ · awk brace = 0 ทุก .cs ที่แก้

## ความเสี่ยงคอมไพล์ที่ตรวจไม่ได้ (ไม่มี SDK)
1. `ReversePaymentInternalAsync` เปลี่ยนชนิดคืนเป็น tuple `(string? EtaxFlag, string? VatNotice)` — ผู้เรียก 2 จุด (VoidPaymentAsync deconstruct เข้าตัวแปรที่ capture · VoidDocumentAsync ทิ้งผล)
2. `IDocumentService.ResolveEtaxCancellationAsync` คืน `EtaxCancellationResult` (implementation เดียว · controller ใช้ `.Source/.Message`)
3. `catch (Exception ex) when (!throwOnFailure)` ใน `TryReclassifyUndueOutputVatAsync` · BusinessRuleException ที่ throw ใน try ถูกปล่อยผ่านเมื่อ `throwOnFailure`
4. alias `using ReissueRequestView = Accounting.Helpers.ReissueRequestView;` ระดับ global ใน `DocumentDtos.cs` (ก่อน `namespace`)
5. `var info = hasReceipt ? payReceipts[...] : (Number: "", Vat: 0m);` — tuple ชื่อตรงกันสองสาขา
6. `none with { UndoReclass = true }` บน positional record · `JsonSerializer.Serialize(v with { RequestHash = "" })` ของ record ที่มี `IReadOnlyList<ReissueLineChange>`
7. เทสต์ `R200_V1G_RV1F11` สร้าง `AccountingDbContext` แบบ offline (`UseNpgsql` ไม่ต่อจริง) — แบบเดียวกับ `SettlementRulesAndSchemaTests` · `p.PropertyInfo != null` ตัด shadow FK

## คำถามค้าง (ให้เจ้าของทบทวน)
> รอบ 200: Q1–Q5 ได้คำตัดสินข้อ 50–54 แล้ว — ลงมือที่ `team-V1H.md` (Q1 ✅ · Q2 ✅ · Q3 ✅ ด่าน + 📋 ใบรายงวด · Q4 ✅ · Q5 ✅)
- **Q1** `ReverseMultiDocPaymentInternalAsync` (การชำระที่จัดสรรหลายใบ) ไม่ถอยภาษีขายถึงกำหนดของใบบริการเลย (ของเดิม — ไม่ใช่รอบนี้) — ต่อสายเข้าตัวตัดสินเดียวกันไหม
- **Q2** `EtaxInvoiceService.VoidAsync` ยอมยกเลิกแถว Submitted ในฐานเราโดยไม่ส่งคำยกเลิกถึงกรมสรรพากร (RV1F-9) — ข้อ 46 ยอมรับทาง "ยกเลิกในระบบ" โดยไม่แนบ · ป้ายตอนนี้บอกความจริงแล้ว
  แต่ควรบังคับแนบไฟล์เมื่อแถวที่ถูกยกเลิกเคยเป็น Submitted ไหม
- **Q3** ปิดธงเมื่อมีการรับชำระหลายงวด: ตอนนี้ใบต้นทางรายงานภาษี ณ วันรับเงินครั้งแรก (กติกาเดิม) — ต้องการใบกำกับรายงวดไหม
- **Q4** ตัวกลับภาษีขายที่เคยลงวันที่ใบแจ้งหนี้ก่อนรอบนี้ (GL คลาดสองเดือน) — ใส่ในรายงานข้อ 44 ด้วยไหม
- **Q5** ปิดธงทาง (ข) เมื่อลูกค้าชำระใหม่แล้ว — ปัจจุบันปฏิเสธ (การขายยังเกิด) · ควรมีทางที่สาม "ใบกำกับเดิมยังใช้ได้ (ไม่ยกเลิกที่กรมสรรพากร)" ที่ล้างธงโดยไม่แตะอะไรไหม

## คอมมิต
- โค้ด + เอกสาร: `3f4e1ea2` · เติม sha: คอมมิตตามหลัง (ห้าม amend)
