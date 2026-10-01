# รอบ 200 · ทีม V1H — ลงมือตามคำตัดสินข้อ 50–54 (คำถามค้าง Q1–Q5 ของทีม V1G)

> แหล่ง: `DECISIONS.md` ข้อ 50–54 · `team-V1G.md` (Q1–Q5) · CLAUDE.md กฎเหล็ก #2 A/C/F · #4 F2 ข้อ 2, 3, 4, 7, 8 ·
> **ยังไม่ได้คอมไพล์ในเครื่องนี้ (ไม่มี .NET SDK) — รบกวน rebuild + `dotnet test` ฝั่ง CI/ผู้ใช้**
>
> worktree ถูกสร้างจาก master เก่า ⇒ `reset --hard origin/claude/erp-system-review-team-660mev` (`f9a38e10`) ก่อนเริ่ม

## ตารางรายการ

| ข้อ | สถานะ | ที่แก้ (file:method) | เทสต์ |
|---|---|---|---|
| **50** ยกเลิกการชำระที่จัดสรรหลายใบไม่ถอยภาษีขาย (Q1) | ✅ | `DocumentService.cs` `UndoOutputVatOnPaymentVoidAsync` (ใหม่ — ตัวถอยภาษีตัวเดียว: `ReceiptHoldsTaxPointFor` → `LiveVatReceiptExistsAsync` → `ShouldUndoOutputVatReclass` → `ReclassLockReasonAsync` → `OutputVatUndoOnPaymentVoid` → `UndoUndueOutputVatReclassAsync`) · `ReversePaymentInternalAsync` เรียกตัวนี้แทนสำเนา inline · `ReverseMultiDocPaymentInternalAsync` ขั้น 4b ถอยรายใบหลังคืนยอด + คืน `(EtaxFlag, VatNotice)` · `VoidPaymentAsync` รับ `outputVatNotice` จากเส้นหลายใบ · `Helpers/DocumentVoidPreconditions.ReceiptHoldsTaxPointFor` (ใหม่ — ใบเสร็จติดธงถือจุดความรับผิดของใบที่มันอ้างเท่านั้น) | `R200_V1H_ข้อ50_ใบเสร็จติดธงถือจุดความรับผิดเฉพาะใบที่มันอ้าง…` · `…ทิศตรงข้าม_ใบเสร็จที่ยกเลิกแล้วหรือไม่ถือภาษี…` (Theory 2) · `…เส้นหลายใบเดือนที่ตั้งรายการปิด_ให้ผลเดียวกับเส้นใบเดียว` (Theory 3) |
| **51** `EtaxInvoiceService.VoidAsync` ยกเลิกแถว Submitted ในฐานเราเงียบ (Q2) | ✅ | `Helpers/EtaxVoidPolicy.cs` (ใหม่: `Decide` · `VoidedLabel` · `VoidedStatusNote`) · `EtaxInvoiceService.VoidAsync(companyId, etaxId, EtaxVoidRequest?, actor)` — ไฟล์หลักฐานต้องเป็นไฟล์แนบของเอกสารของแถว (`a.EntityId == etax.DocumentId` · tenant) · `BusinessRuleException` 409 `RD-ETAX-VOID-EVIDENCE` (เดิม `InvalidOperationException`) · `AddChainedAuditLog` `etax-voided-in-system` · `EtaxController.Void` เดิน `IAttachmentAccessGate.DenyAttachmentAsync("Document", docId, Read, …, evidenceId)` ก่อน service (แถวใหม่ `attachment_gate_check`) · `EtaxInvoiceResponse` + `VoidedAt/VoidReason/VoidedNote` · `etax.html` ป้าย Voided = “ยกเลิกในระบบนี้” + หมายเหตุจากเซิร์ฟเวอร์ + ฟอร์มแนบหลักฐาน (`voidEvidenceModal`) · ข้อความทางไปต่อ Submitted ใน `DocumentVoidPreconditions`/`SettlementPaidReissue`/`SettlementPostingGuards` บอกให้แนบไฟล์ | `R200_V1H_ข้อ51_Submitted_ไม่มีไฟล์หลักฐานหรือเหตุผล…` · `…ทิศตรงข้าม_ยังไม่ถึงกรมสรรพากร…` (Theory 4) · `…ตอบรับแล้วหรือยกเลิกแล้ว…` (Theory 2) · `…ป้ายสถานะบอกยกเลิกในระบบนี้…` |
| **52** ปิดธงเมื่อรับหลายงวด (Q3) | ✅ บางส่วน · 📋 ส่วนออกใบรายงวด | `DocumentVoidPreconditions.EtaxCancellationFollowUp` — ใบแจ้งหนี้ถือ VAT ที่การรับชำระที่ยังมีผลไม่ใช่ “รับครบงวดเดียว” (หลายงวด · บางส่วน · รับรวมเอกสารอื่น) ⇒ **ปฏิเสธพร้อมทางไปต่อ** (`InstallmentTaxInvoiceRequired`) แทนการยกเลิกใบเสร็จแล้วย้ายภาษีไปวันรับเงินแรกโดยไม่มีใบกำกับ · ทางไปต่อ: ทาง (ค) หรือให้ผู้ทำบัญชีตัดสิน (ธงค้างให้เห็น) | `R200_V1H_ข้อ52_รับหลายงวด_บางส่วน_หรือรับรวมเอกสารอื่น…` · `…ทิศตรงข้าม_รับครบงวดเดียว…` · ปรับ `R200_V1G_ลำดับ_ทิศตรงข้าม_…รับหลายงวด…` (เดิมล็อก Allowed) |
| **53** ตัวกลับภาษีเก่าลงวันที่ใบแจ้งหนี้ (Q4) | ✅ | `Helpers/EtaxReissueReview.ReclassReversalMisdated` + `EtaxReviewReversalRow` + `EtaxReissueReviewReport.MisdatedOutputVatReversals` · `DocumentService.Reissue.cs` `GetEtaxReissueReviewAsync` (คู่ JE ย้ายภาษี Dr 21913 ของใบแจ้งหนี้ ↔ ตัวกลับที่ยังมีผล ลงคนละเดือน · tenant ทุกตาราง · อ่านอย่างเดียว) · `DocumentController.GetEtaxReissueReview` นับรวม · `documents.html` แถบรายงานแสดงกลุ่มใหม่ | `R200_V1H_ข้อ53_ตัวกลับลงวันที่ใบแจ้งหนี้คนละเดือน…` |
| **54** ลูกค้าชำระใหม่แทนเช็คที่เด้ง (Q5) | ✅ | `EtaxCancellationPath.OriginalStillValid = 2` · `EtaxCancellationEvidence.OriginalInvoiceStillValid = 5` · `EtaxCancellationClaim` + `ReceiptTotalAmount/LivePaymentCoverage/OtherLiveVatReceipt/SourceVatUndone` (ค่าเริ่มต้น = พฤติกรรมเดิม) · `EtaxCancellationResolution` ทาง (ค): ตอบรับแล้ว · ไม่มีใบกำกับอื่น · ภาษีไม่ถูกถอยไปแล้ว · ยอดรับชำระที่มีผลครอบยอดใบเสร็จ · `ResolveEtaxCancellationAsync` (สิทธิ์อนุมัติเอกสารชนิดนั้นตรวจใน service — ไม่รู้ผู้กด = 403 `ETAX-CANCEL-KEEP-APPROVE` · ล้างธงอย่างเดียว · audit `RD-ETAX-ORIGINAL-STILL-VALID`) · `LivePaymentCoverageAsync` (ใหม่ — นับจากการรับชำระจริง) · ข้อความธงเช็คเด้ง + ปฏิเสธทาง (ข) ชี้ทาง (ค) · `documents.html` ตัวเลือก (ค) | `R200_V1H_ข้อ54_ลำดับ_เช็คเด้ง_รับเงินสดครบ_ปิดธงทางค…` · `…ทางค_เงื่อนไขไม่ครบ…` (Theory 7) · `…ทิศตรงข้าม_ทางกและขไม่ถูกแตะ…` |

## 📋 ข้อ 52 — เหตุผลที่ไม่ทำใบกำกับรายงวดในรอบนี้
ระบบทั้งระบบใช้กติกา “ย้ายภาษีขายเต็มก้อนตอนรับเงินงวดแรก” (`TryReclassifyUndueOutputVatAsync` — idempotent ด้วย `OutputVatDueAt`) และใบรับของการรับบางส่วนไม่ถือ VAT
(`SettlementReceiptPolicy.CarriesTaxInvoiceRole`) — **เส้นรับชำระปกติก็ไม่ออกใบกำกับรายงวดเช่นกัน**. ทำใบรายงวดเฉพาะในเส้นปิดธงจะสร้างกติกาภาษีชุดที่สอง (F2 ข้อ 4) และต้องมี:
(1) ย้าย 21913→21911 บางส่วนต่องวด (2) ใบเสร็จถือ VAT ตามสัดส่วนเงินแต่ละงวด (3) ตัวเลือกเจ้าของแถว ภ.พ.30 ที่รู้จักหลายใบกำกับต่อการขาย (4) ตัวถอยภาษีรายงวด —
กระทบเส้นรับชำระปกติทุกทางเข้า ⇒ ต้องเป็นงานแยกพร้อมเทสต์ golden ของ ภ.พ.30. รอบนี้ทำส่วนที่ปลอดภัย: **ด่านที่มองเห็น** (ไม่ยกเลิกใบเสร็จจนการขายไม่มีใบกำกับ) + ทางไปต่อ.

## ออกแบบที่ตัดสินเอง (ทิศมองเห็นและย้อนได้ — ให้เจ้าของทบทวน)
1. **ข้อ 50 · ใบเสร็จของการชำระหลายใบ** ถือจุดความรับผิดของใบที่มัน `RelatedDocumentId` อ้างเท่านั้น — ใบอื่นในการจัดสรรตัดสินด้วยใบเสร็จถือ VAT ของตัวเอง
2. **ข้อ 50 · cascade จาก `VoidDocumentAsync`** ของใบที่ชำระด้วยเงินจัดสรร: เดิมตัวกลับ JE ย้ายภาษีเกิดในขั้นกลับ JE ทั่วไป (ลงวันที่ `ResolveReversalDateAsync` ไม่ตรวจงวดของ JE ย้ายภาษี) —
   ตอนนี้เดินด่านเดียวกับเส้นใบเดียว ⇒ เดือนรับเงินปิด = 409 (เข้มขึ้นเท่าเส้นใบเดียวที่ V1G ทำไว้ · ทางไปต่อในข้อความ: เปิดงวด/Reject & Reverse หรือใบลดหนี้)
3. **ข้อ 51 · ด่านไฟล์แนบเรียกทุกครั้ง** (checker บังคับให้อยู่ระดับบนสุด) — ผู้มีสิทธิ์ยกเลิก e-Tax แต่อ่านเอกสารไม่ได้ จะยกเลิกไม่ได้ (เข้มขึ้น) · เลขอ้างอิงไม่บังคับ (ไฟล์คือหลักฐาน)
4. **ข้อ 54 · “ครอบยอด”** = Σ การรับชำระที่ยังมีผล (ตรง: ยอด+ค่าธรรมเนียม+บรรทัดปรับ · จัดสรร: ยอดจัดสรร) ≥ ยอดรวมใบเสร็จที่ติดธง · ไม่ใช้ `PaidAmount` (รวมใบลดหนี้ที่หักล้าง)
5. **ข้อ 54 · ปฏิเสธเมื่อภาษีของใบต้นทางถูกถอยไปแล้ว** (กลุ่มแรกของรายงานข้อ 44) — ปิดธงตอนนั้น ภ.พ.30 จะไม่มีภาษีของใบกำกับที่ยังมีผล ⇒ ให้ผู้ทำบัญชีตัดสินก่อน

## F3 ข้อ 7–12
7. รูปแบบเดิม: ตรรกะถอยภาษีแบบ inline ใน `ReversePaymentInternalAsync` เหลือ **0** (forbid ใน required_call_site) · ผู้เรียก `ReverseMultiDocPaymentInternalAsync` 2 จุด (VoidPaymentAsync รับ tuple · VoidDocumentAsync ทิ้งผล) ·
   ผู้เรียก `EtaxInvoiceService.VoidAsync` **1** จุด (EtaxController) · `FlaggedReceiptKeepsTaxPoint` นอกไฟล์ตัวเอง **0** (เปลี่ยนเป็น internal · เทสต์ยังเรียก) · ข้อความ “(ทำได้ก่อนกรมสรรพากรตอบรับ)” ที่ไม่บอกเรื่องไฟล์ **0**
8. ทางเข้าอื่น: ยกเลิกการชำระ (หน้าเอกสาร · cascade ยกเลิกเอกสาร · เช็คเด้ง · ยกเลิกการลงบัญชีรอบโอน) ทั้งใบเดียว/หลายใบ → ตัวถอยภาษีตัวเดียว · ยกเลิก e-Tax มีทางเข้าเดียว (หน้า e-Tax/API) ·
   ปิดธงมีทางเข้าเดียว (`POST document/{id}/etax-cancellation`) · มือถือ/LINE ไม่มีเส้นเหล่านี้
9. เข้มขึ้น + ทางไปต่อ: ยกเลิก e-Tax Submitted ไม่มีไฟล์ ⇒ แนบไฟล์ (ทิศตรงข้าม `…ทิศตรงข้าม_ยังไม่ถึงกรมสรรพากร…`) · ปิดธงทาง (ก) รับหลายงวด ⇒ ทาง (ค)/ผู้ทำบัญชี (ทิศตรงข้าม `…ข้อ52_ทิศตรงข้าม_รับครบงวดเดียว…`) ·
   ยกเลิกการชำระหลายใบเดือนปิด ⇒ เปิดงวด/ใบลดหนี้ (ทิศตรงข้ามใน Theory ข้อ 50) · ทาง (ค) ไม่ครบเงื่อนไข ⇒ ทาง ก/ข (`…ทิศตรงข้าม_ทางกและขไม่ถูกแตะ…`)
10. ค่าที่ persist ก่อนแก้: การชำระหลายใบที่ถูกยกเลิกไปแล้วโดยไม่ถอยภาษี — **ไม่แก้อัตโนมัติ** และยังไม่อยู่ในรายงานข้อ 44 (→ Q1) · แถว e-Tax Submitted ที่เคยถูกยกเลิกเงียบ — ไม่แก้ (→ Q2) ·
    ตัวกลับเก่าที่ลงคนละเดือน — อยู่ในรายงานข้อ 53 แล้ว · ไม่มีคอลัมน์ใหม่ (ไม่มี migration)
11. ฝ่ายค้าน: ขอ main agent ส่ง diff ให้ฝ่ายค้าน (เงิน/ภาษี) — โดยเฉพาะ `UndoOutputVatOnPaymentVoidAsync` ในเส้นหลายใบ (ลำดับหลังคืนยอด · ใบเสร็จที่ยังไม่ save) และทาง (ค)
12. DOCUMENT_FLOW §2.4c (ย่อหน้า V1H + แก้ประโยค “เส้นหลายใบยังไม่ถอย” / “รับหลายงวด”) · §3.5 ขั้น 3 (Submitted ต้องแนบไฟล์) · Last verified · TEST_PLAN §0 (`--row`) · CHANGELOG

## checker ที่รัน
`required_call_site_check` ✅ (589 กติกาผ่าน + negative test ในตัว 14 เคส · แถวใหม่ของ V1H ยังไม่ได้ทดลองถอดการเรียกจริงทีละแถว — เครื่องช้า ~10 นาที/รอบ) · `attachment_gate_check` ✅ (14 target + negative test) · `record_arg_check` ✅ · `nullable_arg_check` ✅ · `using_check` ✅ ·
`undeclared_local_check` ✅ · `arg_type_check` ✅ · `service_interface_check` ✅ · `write_permission_gate_check` ✅ · `string_quote_close_check` ✅ · `comment_line_break_check` ✅ ·
`tuple_name_merge_check` ✅ · `dead_helper_check` ✅ (จับ `FlaggedReceiptKeepsTaxPoint`/`InstallmentTaxInvoiceRequired` เรียกในไฟล์ตัวเอง → internal) · `namespace_shadow_check` ✅ ·
`accessibility_check` ✅ · `identifier_space_check` ✅ · `verbatim_string_check` ✅ · `html_attr_escape_check` ✅ · `onclick_js_string_check` ✅ · `js_dup_method_check` ✅ ·
`deep_link_param_check` ✅ · `css_var_check` ✅ · `terminal_status_writer_check` ✅ (แถวที่ลดคือ `BankV1Controller` ของทีมอื่น — ไม่แตะ) · `approved_status_writer_check` ✅ ·
`test_inventory --check` ✅ · `node --check` สคริปต์ใน `etax.html` + `documents.html` ✅ · awk brace = 0 ทุก .cs ที่แก้ · U+FFFD ไม่พบ ·
checker ทุกตัวรายงาน 1,574 ไฟล์ .cs (ไม่ได้ข้าม path `.claude` ของ worktree) · `check_all.sh` เต็มไม่ได้รัน (เครื่องโหลดหนัก)

## ความเสี่ยงคอมไพล์ที่ตรวจไม่ได้ (ไม่มี SDK)
1. `ReverseMultiDocPaymentInternalAsync` เปลี่ยนชนิดคืนเป็น `Task<(string? EtaxFlag, string? VatNotice)>` — `VoidPaymentAsync` deconstruct เข้าตัวแปรที่ประกาศนอก lambda · `VoidDocumentAsync` `await` ทิ้งผล
2. `EtaxInvoiceResponse` เพิ่ม 3 ช่องท้าย (ไม่มีค่าเริ่มต้น) — ผู้สร้างตัวเดียว `MapToResponse` · `items.Select(MapToResponse)` (method group ไม่มี optional)
3. `EtaxCancellationClaim` เพิ่ม 4 ช่องท้ายแบบมีค่าเริ่มต้น — ผู้เรียกเดิม (service + เทสต์ V1F/V1G) ใช้ positional 10 ตัว · service ส่งชื่อ `ReceiptTotalAmount:` ฯลฯ
4. `[FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] EtaxVoidRequest? request` — ชื่อเต็มของ enum (ไม่ได้เพิ่ม using)
5. LINQ join `rev.OriginalEntryId equals (Guid?)orig.Id` / `orig.SourceDocumentId equals (Guid?)src.Id` + subquery `_db.JournalEntryLines.Any(… _db.ChartOfAccounts.Any(…))` ใน `GetEtaxReissueReviewAsync`
6. `ruleCode = path switch { … }` + `liveCoverage = … ? liveCoverage : (decimal?)null` ในตัวสร้าง anonymous object ของ audit
7. เทสต์ใช้ `Facts(bool, DocumentType = …, decimal = …, params EtaxCancelLivePayment[] payments)` เรียกด้วย `payments: new[] {…}` · Theory ส่ง `int vat` เข้าพารามิเตอร์ `decimal`

## คำถามค้าง (ให้เจ้าของทบทวน)
- **Q1** การชำระหลายใบที่ถูกยกเลิกก่อนรอบนี้ (ภาษีขายของใบที่ไม่เหลือเงินรับยังค้างใน ภ.พ.30) — เพิ่มเป็นกลุ่มที่ 5 ของรายงานข้อ 44 ไหม (ระบุได้จาก `OutputVatDueAt != null` + `PaidAmount = 0` + ไม่มีใบเสร็จถือ VAT ที่มีผล)
- **Q2** แถว e-Tax Submitted ที่เคยถูกยกเลิกในฐานเราโดยไม่มีหลักฐาน (ก่อนข้อ 51) — ให้รายงานอ่านอย่างเดียวด้วยไหม (audit เดิมไม่มี — ระบุได้แค่จาก `SubmittedAt != null` + `Status = Voided`)
- **Q3** ข้อ 52 ส่วนออกใบกำกับรายงวด — ทำเป็นงานแยกที่เปลี่ยนกติกาย้ายภาษีของเส้นรับชำระปกติด้วยไหม (ต้องมี golden ภ.พ.30 ก่อน) หรือคงด่านปฏิเสธไว้
- **Q4** ทาง (ค) ใช้สิทธิ์ “อนุมัติเอกสารชนิดนั้น” (เดียวกับการออกใบกำกับ) — ต้องการสิทธิ์เฉพาะไหม

## คอมมิต
- โค้ด + เอกสาร: `abf0892f` · เติม sha: คอมมิตตามหลัง (ห้าม amend)
