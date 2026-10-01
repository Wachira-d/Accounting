# รอบ 200 · ทีม V1 — ยกเลิก-ออกใบแทน (S3-5 · S4-8 ค้าง · S3-7)

> ขอบเขต (BRIEF): `DocumentService` (Void*) · `ChequeService` · `DocumentVoidPreconditions` · `SettlementUnpostGate` (เฉพาะ S3-7) ·
> คำตัดสิน DECISIONS ข้อ 9, 11, 17 · **ยังไม่ได้คอมไพล์ในเครื่องนี้ (ไม่มี .NET SDK) — รบกวน rebuild + `dotnet test` ฝั่ง CI/ผู้ใช้**
>
> หมายเหตุ worktree: worktree ของทีมถูกสร้างจาก `69fd88e8` (main เก่า ไม่มี `erp-review/2026-09-29/`) — ทีมย้ายสาขาของตัวเองไปที่
> `5c1fe028` (ปลาย `claude/erp-system-review-team-660mev` ที่มี BRIEF/DECISIONS) ก่อนเริ่มงาน · ไม่แตะสาขาอื่น

## ตารางสถานะ

| ข้อ | สถานะ | ที่แก้ (file:method) | เทสต์ |
|---|---|---|---|
| **S3-5 → ข้อ 9** ยกเลิกและออกใบแทน ใบขายที่รอบโอน Posted รับชำระ | ✅ | `Services/Implementations/DocumentService.Reissue.cs` (ใหม่ · partial — F4 ข้อ 7): `ReissueSettlementPaidDocumentAsync` · `EvaluateSettlementPaidReissueAsync` (ด่านเดียวของปุ่ม+การกดจริง) · `RepointDocumentLinksAsync` · `ResolveNumberSeriesTypeAsync` (ย้ายจาก `ApproveDocumentAsync` — กติกาชุดเลขตัวเดียว) · `Helpers/SettlementPaidReissue.cs` (ใหม่ · pure) · `Controllers/DocumentController.cs` `POST {id}/reissue-settlement-paid` (สิทธิ์ยกเลิก **และ** อนุมัติ) · `DocumentService.GetDocumentAsync` → `CanReissueSettlementPaid`/`ReissueSettlementPaidBlockedReason` · หน้าเว็บ `documents.html` ปุ่ม + โมดัล (ผู้ซื้อ · คำบรรยาย · หมายเหตุ · เหตุผล) + แถบใบแทนสองชนิด · `api.js reissueSettlementPaid` | `VoidReissueR200Tests`: `R200_V1_ใบขายที่รอบโอนPostedรับชำระ_ด่านเดิมผ่านทุกตัว…` · `…ใบที่ไม่เกี่ยว_ไม่แสดงปุ่มเลย` · `…ด่านเดิมของการยกเลิกเอกสารยังบล็อก…` (Theory 12 เคส) · `…เปลี่ยนเฉพาะผู้ซื้อ_หมายเหตุ_คำบรรยาย_ผ่านด่าน…` · `…ยอด_อัตราVAT_taxpoint…_ด่านปฏิเสธ` · `…โคลนทุกช่องค่า…ด่านเทียบใบใหม่กับใบเดิมผ่าน` · `…หมายเหตุบนใบใหม่…` |
| ใบแทนสองชนิด (กระดาษ vs ถือผลทางบัญชี) | ✅ | คอลัมน์ `Documents.ReplacementCarriesPostings` (`DatabaseMigrationHelper` ต่อจากบล็อกใบแทนเดิม · default false = ใบแทนเดิมคงพฤติกรรม) · `ApproveDocumentAsync` `isFullTaxInvoiceReplacement` · `VoidDocumentAsync` `isReplacementDoc` + ไม่ปลดตราประทับใบเดิมที่ Voided · `RestoreVoidedDocumentAsync` Gate 0 (409 `DOC-RESTORE-REPLACED`) · echo ใน `DocumentResponse` (ใบเดิมได้ค่าของใบแทน) | ล็อกจุดเรียก (must_re) |
| ข้อความ 409 ของ `CheckDocumentPaymentsAsync` ชี้ทางนี้ | ✅ | `Helpers/SettlementPostingGuards.cs` `SettlementArtifactGuard.PaidDocumentVoidReason` (3 ทางตามเหตุ: ออกใบแทน · ใบลดหนี้/ใบเพิ่มหนี้ + บรรทัดรอบโอนถัดไป · ยกเลิกการลงบัญชี) · `LockBatchRowAsync`/`BatchStateAsync` แยกออกมา (CheckLockedAsync ใช้ตัวเดียวกัน) | `R200_V1_ยกเลิกใบขายที่รอบโอนPostedรับชำระ_ข้อความชี้…` · `…รอบโอนยังไม่ลงบัญชี_ถูกลบ_หรือกำลังยกเลิก…_ยกเลิกได้ตามเดิม` |
| ทางเข้าอื่นที่ได้ 409 | ✅ | `IntegrationService.VoidDocumentByExternalRefAsync` — ข้อความใหม่ไปถึงคู่ค้าผ่าน `HandleSyncError` (ไม่ต้องแก้ไฟล์) · `CmsBookingService` — notice ใช้ `ex.Message` อยู่แล้ว · `CmsCommerceService.UpdateOrderStatusAsync` — **เดิม LogWarning อย่างเดียว (เงียบ)** ⇒ ปัก `[ERP-VOID-FAILED …]` บน `SiteOrder.InternalNotes` (echo ใน OrderResponse แล้ว) | — (ข้อความจาก helper ที่มีเทสต์) |
| **S4-8 ค้าง → ข้อ 11** `VoidPaymentAsync` / เช็คเด้ง ดู e-Tax ของใบเสร็จอัตโนมัติ | ✅ | `Helpers/DocumentVoidPreconditions.cs`: `AutoReceiptOnPaymentVoid` + `PaymentVoidCause` + `EtaxReachedRdStatuses` + `StrongestEtax` (pure · ตัวเดียว) · `DocumentService.VoidPaymentAsync(…, PaymentVoidCause)` → `Task<PaymentVoidResult>` · `DecideAutoReceiptOnPaymentVoidAsync` (ก่อนแตะ JE — ปฏิเสธ = 409 `RD-ETAX-RECEIPT-SENT`) + `ApplyAutoReceiptOnPaymentVoidAsync` เรียกจาก **ทั้งสองเส้น** (`ReversePaymentInternalAsync` · `ReverseMultiDocPaymentInternalAsync`) · `ChequeService.MarkBouncedAsync` → `PaymentVoidCause.ChequeBounce` (ห้ามบล็อก · ธง `Documents.EtaxCancelRequiredAt/Reason` · log + webhook `etaxCancellationRequired`) · รายการงานค้าง `GET document/etax-cancel-required` + แถบบนหน้ารายการเอกสาร + แถบบนหน้าใบ · ใบเสร็จที่ยกเลิกได้ ยกเลิก e-Tax ที่ค้างของใบนั้นด้วย (เดิมค้างสถานะเดิม) | `R200_V1_ใบเสร็จที่ยังไม่ถึงกรมสรรพากร_ยกเลิกการชำระได้เหมือนเดิมทุกทางเข้า` (Theory) · `…ผู้ใช้กดยกเลิกการชำระ_ใบเสร็จถึงกรมสรรพากรแล้ว_ปฏิเสธ…` · `…ยกเลิกการลงบัญชีรอบโอน…ตาข่ายชั้นสองปฏิเสธ` · `…เช็คเด้ง_ห้ามบล็อก…ติดธง…` · `…สถานะeTaxที่ไปไกลที่สุด…` |
| ด่านยกเลิกการลงบัญชีต้องเห็นใบเสร็จ Submitted (ผลตามของข้อ 11) | ✅ | `SettlementUnpostPayment.ReceiptEtaxSubmitted` + `SettlementUnpostGate.Evaluate` (NeedsUserAction · ทางไปต่อ = ยกเลิก e-Tax ของใบเสร็จ) · `SettlementPostingService.LoadUnpostFactsAsync` โหลดจาก `EtaxReachedRdStatuses` ชุดเดียว · `UnpostCoreAsync` ส่ง `PaymentVoidCause.SettlementUnpost` (ไฟล์ของ V2 — แก้เล็กที่สุด 2 จุด) | `R200_V1_ด่านยกเลิกการลงบัญชี_ใบเสร็จส่งeTaxแล้วยังไม่ตอบรับ…` (สองทิศ) |
| **S3-7 → ข้อ 17** เดือนภาษีของการรับชำระใบบริการ §78/1 ใน `SettlementUnpostGate.Evaluate` | ✅ (มีอยู่แล้ว — ยืนยัน) | แก้แล้วที่ `c3116a4d` (ทีม S4 รอบ 198): `SettlementUnpostPayment.OutputVatDueAt` + ยอดรับสะสม · ป้อนจาก `LoadUnpostFactsAsync` · เทสต์ `SettlementReview198S4Tests` (บรรทัด ~203) · รอบนี้เพิ่มแถวล็อกจุดเรียก `SettlementPostingGuards.cs:Evaluate` (`p.OutputVatDueAt` · `p.DocumentPaidAmount - p.BatchPaidOnDocument`) + ติ๊ก `✅ c3116a4d` ใน review198-S3 | เดิม: `SettlementReview198S4Tests` S3-7 |

## ออกแบบ — ทำไม "ย้าย" ไม่ใช่ "กลับรายการแล้วลงใหม่"
เงินจริงไม่เปลี่ยน + ยอด/บรรทัด/อัตรา/tax point เท่าเดิม ⇒ ทุกขาบัญชีเท่าเดิม. ถ้ากลับรายการใบเดิมแล้วอนุมัติใบใหม่ปกติ: ต้นทุน FIFO ขยับ · JE ย้ายภาษีขายถึงกำหนด §78/1
ถูกกลับ (ใบบริการกลับไปพัก 21913 ทั้งที่รับเงินแล้ว) · งวดปิดล้ม · และ `ApproveDocumentAsync`/`VoidDocumentAsync` เปิดธุรกรรมของตัวเอง ⇒ "ธุรกรรมเดียว" ทำไม่ได้.
จึงใช้กลไก `ReplacesDocumentId/ReplacedByDocumentId` เดิม + ธง `ReplacementCarriesPostings` แยกชนิด และ **ชี้ความเป็นเจ้าของ** (FK) ทุกอย่างไปใบใหม่ใน
ธุรกรรมเดียว — ใบใหม่กลายเป็นใบปกติที่ถือผลทางบัญชีเอง (ยกเลิกทีหลัง = กลับรายการ/คืนสต็อกตามปกติ ผ่านด่าน settlement เดิม)

## F3 ข้อ 7–12 (ตอบในคอมมิตด้วย)
7. รูปแบบเดิม: ตัวประทับใบเสร็จอัตโนมัติ Voided โดยไม่ดู e-Tax เหลือ **0 จุด** (2 จุดใน internal ถูกแทนด้วย `ApplyAutoReceiptOnPaymentVoidAsync` · forbid ล็อก) ·
   `ReplacesDocumentId.HasValue` ที่ใช้ตัดสิน "ข้าม JE/สต็อก" เหลือ 0 จุดที่ไม่ดู `ReplacementCarriesPostings` (approve · void · 7-replacement) — PDF
   `DocumentJournalExpectation.ExpectsLiveJournal(isReplacement)` ยังใช้ `HasValue` (ใบแทนแบบใหม่มี JE อยู่แล้ว ⇒ แค่ไม่เตือน ไม่ผิดตัวเลข)
8. ทางเข้าอื่นของการยกเลิกการชำระ: หน้าเอกสาร · ยกเลิกการลงบัญชี · เช็คเด้ง · cascade ใน `VoidDocumentAsync` (ChildBlocks กันไว้ก่อนถึงใบเสร็จ active) — ผ่านตัวตัดสินเดียว ·
   ทางเข้าของการยกเลิกใบขายที่รอบโอนรับชำระ: หน้าเอกสาร · API/integration · CMS ×2 — ข้อความเดียวกัน
9. เข้มขึ้น: ยกเลิกการชำระที่ใบเสร็จส่ง e-Tax แล้ว (Submitted) ⇒ ปฏิเสธ — ทางไปต่อ: ยกเลิก e-Tax ที่หน้า e-Tax (`EtaxController.Void` มีอยู่) แล้วกดใหม่ · เช็คเด้งไม่ถูกกัน ·
   ยกเลิกการลงบัญชีที่มีใบเสร็จ Submitted ⇒ ปฏิเสธก่อนแตะชิ้นแรก · เทสต์ทิศตรงข้าม: `…ยังไม่ถึงกรมสรรพากร_ยกเลิกการชำระได้เหมือนเดิมทุกทางเข้า` ·
   `…ใบเสร็จที่ยังไม่ถึงกรมสรรพากร ไม่ถูกด่านนี้แตะ` (ใน `…ด่านยกเลิกการลงบัญชี…`)
10. ค่าที่ persist ก่อนแก้: ใบเสร็จที่เคยถูกประทับ Voided ทั้งที่ e-Tax ตอบรับ (ก่อนรอบนี้) — **ไม่มี migration** (ระบบแยกไม่ได้ว่าใบไหนผู้ใช้ไปยกเลิกที่กรมสรรพากรแล้ว) ⇒ คำถามค้าง Q3 ·
    คอลัมน์ใหม่ default false/null = ข้อมูลเดิมคงพฤติกรรมเดิม
11. ฝ่ายค้าน: ยังไม่ได้ส่ง diff ให้ subagent ฝ่ายค้าน (ทีมทำงานเป็น subagent เดี่ยว) — **ขอ main agent ส่งรอบฝ่ายค้านก่อน merge** (3 คำถาม: ทางเข้าอื่น? ทิศตรงข้าม? สถานะปลายทางประทับเองไหม?)
12. DOCUMENT_FLOW §2.4c (ใหม่) · §2.10 (S3-5 ตัดสินแล้ว + ใบเสร็จ Submitted) · §3.5 (ยกเลิกการชำระ vs e-Tax · เช็คเด้ง) · บรรทัด Last verified · TEST_PLAN §0 · CHANGELOG

## ความเสี่ยงคอมไพล์ที่ตรวจไม่ได้ (ไม่มี SDK)
1. `SettlementPaidReissue.CopyScalars` ใช้ reflection — คอมไพล์ได้แน่ แต่ runtime: `Document` มี property ค่าที่เป็น `[NotMapped]` settable (เช่น `ServedAsReceipt`) ถูกคัดลอกด้วย (ไม่มีผลต่อฐาน) · ถ้ามีช่องค่าที่**ห้าม**ตามมา (โทเคน/สถานะ) และเพิ่มทีหลัง ต้องตั้งค่าเองใน `ReissueSettlementPaidDocumentAsync` (รายการที่ตั้งแล้ว: Id · เลขที่ · Created/Updated · ผู้ซื้อ · หมายเหตุ · ตราใบแทน · ธง e-Tax · โทเคนใบเสนอราคา/เซ็นรับ · revision · aging · dunning · สถานะ Sent→Approved)
2. `ExecuteUpdateAsync` บน `ReconciliationGroupItems` ที่กรองผ่าน subquery `_db.ReconciliationGroups.Any(...)` — EF Core 8 แปลได้ (UPDATE … WHERE EXISTS) แต่ยังไม่ได้รันจริง
3. `_db.Payments.FromSqlRaw("… FOR UPDATE").ToListAsync()` รูปเดียวกับ `VoidPaymentAsync`/`IssueReceiptForPaymentAsync` (EF ครอบ subquery + ตัวกรอง IsDeleted)
4. `BatchStateAsync` คืน `(…)?` ด้วย conditional แบบ target-typed (`batch == null ? null : (…)`) — C# 9+ (net8 = C# 12) · `is not { } batch` บน nullable tuple
5. `VoidPaymentAsync` เปลี่ยนชนิดคืนเป็น `Task<PaymentVoidResult>` (interface + impl) — ผู้เรียกเดิม 3 จุด `await` เฉย ๆ คอมไพล์ได้ · ไม่มี fake/mock ของ `IDocumentService` ในเทสต์ (grep แล้ว 0)
6. named argument ใน mapper `MapToResponse` (`ReplacementCarriesPostings:` ฯลฯ) แทรกกลางชุด named args — ถูกต้องเพราะทุกตัวหลังจุดนั้นเป็น named
7. ชื่อ `Accounting.Helpers.X` ใน `DocumentDtos.cs` ใช้ `global::` (namespace `Accounting.Models.DTOs.Accounting` บังราก — `namespace_shadow_check` จับได้และแก้แล้ว)

## คำถามค้าง (เลือกทิศที่มองเห็นและย้อนได้แล้ว — ให้เจ้าของทบทวน)
- **Q1** ใบเสร็จที่ติดธง "ต้องยกเลิกทาง e-Tax" หลังผู้ใช้ไปยกเลิกที่กรมสรรพากรแล้ว — ระบบยังไม่มีทางบันทึก "ยกเลิกที่กรมสรรพากรแล้ว" (ไม่มี e-Tax cancel/replace channel: `EtaxInvoiceService.VoidAsync` ปฏิเสธ Accepted) ⇒ ธงค้าง + ใบเสร็จยังเป็นลูก active ของใบขาย (ChildBlocks กันยกเลิกใบขาย) · ต้องมีปุ่ม "ยืนยันว่ายกเลิกที่กรมสรรพากรแล้ว (แนบเลขอ้างอิง)" หรือเส้นส่งใบแทน e-Tax (TIVC/RCTC purpose code) — ห้ามเดา
- **Q2** "ยกเลิกและออกใบแทน" ยังไม่รองรับใบที่เกี่ยวกับมัดจำ (ใบมัดจำ / มีมัดจำตัดชำระ) — บล็อกพร้อมข้อความ · ต้องย้าย JV ตัดมัดจำ (อ้างด้วยเลขที่ใน `Reference`) ด้วย
- **Q3** ข้อมูลเก่า: ใบเสร็จอัตโนมัติที่เคยถูกประทับ Voided ขณะ e-Tax Accepted (ก่อนรอบนี้) — ควรมีรายงานอ่านอย่างเดียวให้นักบัญชีตรวจ (แบบคำตัดสินข้อ 20) หรือไม่
- ✅ 49458e34 (รอบ 201 ทีม DV · C-1 · ข้อ 74) **Q4** ด่านเดือนภาษี: ใช้ด่านเดียวกับ "ยกเลิกเอกสาร" (รายงานที่ **ล็อก** แล้วเท่านั้น) ตามคำตัดสิน — ใบในเดือนที่ **ประกาศว่ายื่น** แต่ยังไม่ล็อก ออกใบแทนได้ (ยอด VAT ไม่เปลี่ยน · ใบใหม่ลงวันที่เดิม) — ถ้าเจ้าของต้องการเข้มกว่า เพิ่ม `TaxFilingLockPolicy.DeclaredOrFiledStatuses` ใน `EvaluateSettlementPaidReissueAsync`
- ✅ 23d7a6de (รอบ 201 PL · A-PL10) **Q5** `CmsCommerceService.UpdateOrderStatusAsync` ไม่ `ChangeTracker.Clear()` หลัง `VoidDocumentAsync` ล้มกลางธุรกรรม (ของเดิม) — ถ้าล้มหลังแก้ entity แล้ว การ SaveChanges ของออเดอร์อาจบันทึกของค้าง · ไม่แก้รอบนี้ (ต้องโหลดออเดอร์ใหม่หลัง Clear — นอกขอบเขต)

## คอมมิต
- โค้ด + เอกสาร: `d2335b38` · เติม sha: คอมมิตตามหลัง (ห้าม amend)
