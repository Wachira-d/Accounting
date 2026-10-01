# รอบ 200 · ทีม V1I — แก้ผลตรวจฝ่ายค้านของงานทีม V1H (merge `82eddbe6`)

> แหล่ง: ผลฝ่ายค้าน V1H-O1..O7 (main agent) · `DECISIONS.md` ข้อ 50–54 · 62–64 · `team-V1H.md` · CLAUDE.md กฎเหล็ก #2 A/F · #4 F2 ข้อ 1, 2, 3, 4, 6, 7, 8 ·
> **ยังไม่ได้คอมไพล์ในเครื่องนี้ (ไม่มี .NET SDK) — รบกวน rebuild + `dotnet test` ฝั่ง CI/ผู้ใช้**
>
> worktree ถูกสร้างจาก master เก่า ⇒ `reset --hard origin/claude/erp-system-review-team-660mev` (`09c8556a`) ก่อนเริ่ม · ทุกข้อเปิดไฟล์จริงยืนยันก่อนแก้ (ยืนยันแล้วทุกข้อ — ไม่มี NOT-A-BUG)

## ตารางรายการ

| ID | P | สถานะ | ยืนยันก่อนแก้ (ที่ HEAD `09c8556a`) | ที่แก้ (file:method) | เทสต์ / ด่าน |
|---|---|---|---|---|---|
| **V1H-O1** ทาง (ค) แล้วการรับชำระใหม่ถูกยกเลิก ⇒ ใบกำกับไม่มีธง | P2 | ✅ | จริง — `UndoOutputVatOnPaymentVoidAsync` เห็น RC1 เป็น "ใบเสร็จถือ VAT ที่ยังมีผล" (`LiveVatReceiptExistsAsync`) ⇒ `!ShouldUndo` ⇒ `LogInformation` แล้ว `return null` · และ `DocumentController.VoidPayment` ทิ้งผล `PaymentVoidResult` ทั้งก้อน (ผู้กดเห็นแค่ “สำเร็จ”) | `DocumentService.cs` `ReflagKeptOriginalReceiptsAsync` (~9982 · ใหม่ — ตัวเดียวของ `ReversePaymentInternalAsync` ~9868 และ `ReverseMultiDocPaymentInternalAsync` ~10136 รายใบ) · `CombineNotices` · `DocumentService.Reissue.cs` `LivePaymentCoverageAsync(..., excludePaymentId)` (~1039 — การรับชำระที่กำลังยกเลิกยังไม่ save) · ฝั่งเขียนป้ายใช้ `EtaxReissueReview.KeptOriginalMarker` (~753) · `Helpers/DocumentVoidPreconditions.KeptOriginalCoverageLost` (~234 · pure) · `Helpers/EtaxReissueReview.KeptOriginalMarker` + `LastResolutionKeptOriginal` (~31/35 · pure) · `DocumentController.VoidPayment` (~1428) ตอบข้อความธง · `documents.html` `voidPayment` แสดงเป็นคำเตือน | `R200_V1I_O1_ลำดับ_ทางค_แล้วยกเลิกการรับชำระใหม่_ยอดไม่ครอบ_ติดธงกลับพร้อมทางไปต่อ_ภาษีไม่ถูกถอยเงียบ` · `…ยกเลิกบางส่วน_ยอดที่ยังมีผลไม่ครอบ_ก็ติดธงกลับ` · `…ทิศตรงข้าม_ยอดยังครอบหรือไม่ใช่ทางค_ไม่แตะ` (Theory 5) · `…ป้ายทางค_ฝั่งเขียนฝั่งอ่านตัวเดียว_ปิดซ้ำด้วยทางอื่นไม่นับ` · required_call_site 4 แถว (Reflag · ใบเดียว · หลายใบ · controller) + 1 แถวฝั่งเขียนป้าย |
| **V1H-O2** e-Tax by Email ประทับเวลาแล้ว ยกเลิกแถว Signed ได้โดยไม่มีหลักฐาน + audit เท็จ | P2 | ✅ | จริง — `VoidAsync` เรียก `Decide(etax.Status, …)` (สถานะแถวอย่างเดียว) ⇒ Signed → default branch “ยกเลิกในระบบนี้ก่อนส่งถึงกรมสรรพากร” | `EtaxInvoiceService.VoidAsync` (~761–771) — `DocumentVoidPreconditions.EtaxEmailedWithRdTimestampAsync` (ใหม่ · ~134 — เกณฑ์อีเมลตัวเดียว `EffectiveEtaxAsync` เรียกตัวนี้ด้วย) → `EtaxVoidPolicy.StatusForVoid` (~61 · pure) → `Decide` · ข้อความ Accepted บอก “e-Tax by Email ที่ประทับเวลาแล้ว” · ruleCode audit ตามสถานะที่ใช้ตัดสิน | `R200_V1I_O2_แถวลงนามแล้วแต่ส่งอีเมลประทับเวลาแล้ว_ยกเลิกในระบบไม่ได้_ไม่เขียนว่าก่อนส่งถึงกรมสรรพากร` · `…ทิศตรงข้าม_ไม่มีอีเมลประทับเวลา_ใช้สถานะแถวเดิม` (Theory 6) · required_call_site (`VoidAsync` + `EffectiveEtaxAsync` ห้ามสำเนาคิวรี `DocumentEmailLogs`) |
| **V1H-O3** `VoidPaymentAsync` อ่าน `PaidAmount` ไม่ล็อก | P2 | ✅ | จริง — ล็อกแค่แถว Payment · `doc` อ่านนอกธุรกรรม (tracked) · เส้นหลายใบอ่านเอกสารผ่าน query ที่ identity resolution คืนค่าเก่า · **พบเพิ่ม**: แถว Payment ที่ `FromSqlRaw … FOR UPDATE` คืนคือ instance ที่ context ถือไว้ตั้งแต่อ่านนอกธุรกรรม (ไม่ refresh) ⇒ ด่าน idempotent `locked.IsDeleted` มองไม่เห็นการยกเลิกซ้อนที่ commit ไปแล้ว | `DocumentService.VoidPaymentAsync` (~9474–9497) — ต้นธุรกรรม: อ่าน doc id ของการจัดสรร + `payment.DocumentId` → `LockDocumentsForPaymentVoidAsync` (~9571 · ใหม่ — `ORDER BY "Id" FOR UPDATE` คำสั่งเดียว ลำดับเดียวกับ `CreateMultiDocPaymentAsync` + `ReloadAsync` แถว Unchanged ที่ context ถือ) → ล็อกแถว Payment → `_db.Entry(locked).ReloadAsync()` | ไม่มีตรรกะ pure ใหม่ (โครงสร้างล็อก) — ล็อกด้วย required_call_site 2 แถว (มี/ลำดับก่อน `.FromSqlRaw(` และก่อนเส้นกลับรายการทั้งสอง · `ORDER BY ""Id"" FOR UPDATE` + `""CompanyId"" = {1}` ในสตริง SQL · ส่ง `companyId`) |
| **V1H-O5** หลักฐาน = ไฟล์ใดก็ได้ของเอกสาร | P3 | ✅ | จริง — เงื่อนไขไฟล์ไม่ดูเวลา | `EtaxInvoiceService.VoidAsync` `a.CreatedAt > evidenceNotBefore` · `EtaxVoidPolicy.EvidenceNotBefore(SubmittedAt, CreatedAt)` (~69 · pure — แถวเก่าไม่มีเวลาส่ง = เวลาสร้างแถว) · ข้อความ Submitted บอก “ไฟล์ต้องแนบหลังวันที่ส่ง e-Tax” | `R200_V1I_O5_หลักฐานต้องแนบหลังวันส่ง_ไฟล์ก่อนส่งไม่นับ_ข้อความบอกเหตุ` · `…ทิศตรงข้าม_แถวเก่าไม่มีเวลาส่ง_ใช้เวลาสร้างแถว` · required_call_site (`a.CreatedAt > evidenceNotBefore` · ส่ง `etax.SubmittedAt`) |
| **V1H-O6** ด่านไฟล์แนบเรียกแม้ไม่ส่งไฟล์ | P3 | ✅ | จริง — `EtaxController.Void` เรียก `DenyAttachmentAsync` ทุกครั้ง (checker บังคับระดับบนสุด) · คู่สมมาตร `DocumentController.ResolveEtaxCancellation` เป็นแบบเดียวกัน (F2 ข้อ 1) | `EtaxController.Void` (~199) + `DocumentController.ResolveEtaxCancellation` (~1133) — ด่านใน `if (<id ไฟล์> != null) { … }` (service ไม่แตะไฟล์เมื่อไม่มี id — เปิดยืนยันแล้วทั้งสอง: `evidenceId is Guid fileId && …`) · `tools/attachment_gate_check.py`: TARGETS ช่องที่ 7 = regex เงื่อนไข "มีไฟล์" ที่อนุญาต**ต่อ target** · `_guarded` ยอมเฉพาะ `if (<เงื่อนไขนั้นตรงตัว>)` ที่ระดับบนสุด | negative test ในตัว: G1 เงื่อนไขอื่น · G2 กลับทิศ (`== null`) · G3 เติม `&&` · G5 ซ้อนในบล็อกอื่น · G6 target ที่ไม่มีช่องที่ 7 ห่อด้วยเงื่อนไขเดียวกัน = ฟ้อง · G7 ของจริงไม่ฟ้อง · ถอดการเรียกด่านของทั้งสอง target ยังฟ้อง (ข้อ 1 เดิม) |
| **V1H-O7** ธงของใบ Submitted แนะนำทาง (ค) ที่ถูกปฏิเสธ | P3 | ✅ | จริง — ประโยคทาง (ค) ต่อท้ายทั้งสองสาขา | `DocumentVoidPreconditions.AutoReceiptOnPaymentVoid` (~178–183) — ต่อท้ายเฉพาะ `accepted` | `R200_V1I_O7_ใบที่ส่งแล้วยังไม่รู้ผล_ไม่แนะนำทางค_ทิศตรงข้ามใบที่ตอบรับแล้วยังแนะนำ` (Theory 2 — เช็คเด้ง/ยกเลิกการลงบัญชี) |
| V1H-O4 สิทธิ์ทาง ค | — | ไม่แก้ | main agent ตัดสินแล้ว (ข้อ 64 · คง CanVoid + CanApprove) | — | — |

## ออกแบบที่ตัดสินเอง (ทิศมองเห็นและย้อนได้ — ให้เจ้าของทบทวน)
1. **O1 · ระบุ "ใบที่ปิดธงด้วยทาง (ค)" จากป้ายในหมายเหตุภายใน** (`KeptOriginalMarker` ตัวสุดท้ายของ `ResolvedMarker`) ตามข้อเสนอฝ่ายค้าน — ไม่มีคอลัมน์ใหม่ (ไม่มี migration) ·
   ป้ายที่ V1H เขียนลงฐานไปแล้วรูปเดียวกันทุกตัวอักษร (เทสต์ล็อก) · กันเพิ่ม `EtaxCancelledByCreditNoteId == null` · ทางเลือกที่ไม่ใช้ป้าย (เชิงโครงสร้าง: ใบเสร็จ VAT ที่ยังมีผลซึ่ง
   การรับชำระของตัวเองถูกยกเลิก) จดไว้เป็นคำถาม Q1
2. **O1 · ติดธงกลับทุกทางเข้า (ผู้ใช้ · เช็คเด้ง · ยกเลิกการลงบัญชี)** ไม่ปฏิเสธการยกเลิกของผู้ใช้ — ก่อน V1H ใบนี้ก็ติดธงค้างโดยไม่บล็อกการยกเลิก · ผู้ใช้อาจกำลังแก้การรับชำระที่บันทึกผิด ·
   ไม่ถอยภาษี (ใบกำกับยังมีผล — ตัวถอยเดิมถูกต้องแล้ว) · ยอดครอบเทียบแบบเดียวกับตอนปิดธง (Σ รายการรับชำระจริง ≥ ยอดใบกำกับ − 0.005) ⇒ ยกเลิกบางส่วนจนต่ำกว่ายอดก็ติดธง
3. **O1 · ข้อความธงไปช่อง `PaymentVoidResult.EtaxCancellationFlag`** (เป็นธง e-Tax บนใบเสร็จ) รวมกับธงของใบเสร็จคู่การชำระด้วย ` · ` · `POST document/payments/{id}/void`
   ตอบ `data` = ข้อความธง/ภาษี (null = ไม่มี) และ `message` ต่อท้าย · หน้าเอกสารแสดง toast คำเตือน 15 วินาที
4. **O2 · ใช้สถานะของ "แถว" + ธงอีเมลของเอกสาร** แทน `EffectiveEtaxAsync` ทั้งก้อน — `EffectiveEtaxAsync` คืนสถานะ**แรงสุดของทั้งเอกสาร** ⇒ แถว Error ที่ค้างคู่แถวที่ตอบรับแล้ว
   จะยกเลิกไม่ได้ (เข้มเกินโดยไม่มีเหตุ) · เกณฑ์อีเมลแยกเป็น `EtaxEmailedWithRdTimestampAsync` ตัวเดียวที่ `EffectiveEtaxAsync` เรียกด้วย (ไม่มีสำเนาคิวรี) ·
   ส่งอีเมลประทับเวลาแล้ว = **Accepted** (ตามความหมายเดิมของ `EffectiveEtax`) ⇒ ปฏิเสธพร้อมทางไปต่อ (ยกเลิก/ลดหนี้ทางกรมสรรพากร) — ไม่ใช่ "Submitted + แนบหลักฐาน"
   เพราะการยกเลิกแถวในฐานเราไม่เปลี่ยนสถานะที่ใช้ตัดสินของเอกสาร (บันทึกอีเมลยังอยู่) จึงไม่มีประโยชน์ให้ผู้ใช้และสร้าง audit ที่ชวนเข้าใจผิด
5. **O3 · ล็อกเอกสารก่อนแถว Payment** (ลำดับ Document → Payment เดียวกับ `CreatePaymentAsync`/`CreateMultiDocPaymentAsync`/`EvaluateSettlementPaidReissueAsync`) — เดิม Payment → Document
   ซึ่งกลับลำดับกับเส้นอื่น · อ่านใหม่ทั้งเอกสาร (Unchanged) และแถว Payment ใต้ล็อก
6. **O6 · ทำทั้งสอง controller** (คู่สมมาตร F2 ข้อ 1) — กติกา checker ผูกกับ target ที่ระบุ (ไม่ใช่รูปของเงื่อนไข) ⇒ target อื่นที่ห่อด่านด้วยเงื่อนไขหน้าตาเดียวกันยังฟ้อง

## F3 ข้อ 7–12
7. รูปแบบเดิม: `return null` เงียบของใบกำกับทาง (ค) ใน `!ShouldUndo` — ทุกเส้นยกเลิกการชำระผ่าน `ReflagKeptOriginalReceiptsAsync` แล้ว (ผู้เรียก 2 จุด = ใบเดียว + หลายใบ · cascade
   `VoidDocumentAsync` เรียก internal ทั้งสองจึงได้ด้วย) · `Decide(etax.Status` เหลือ **0** (forbid) · คิวรี `DocumentEmailLogs` สำหรับเกณฑ์อีเมลเหลือ **1** ที่
   (`EtaxEmailedWithRdTimestampAsync`) · `VoidPaymentAsync` ผู้เรียก 2 (controller · เช็คเด้ง) — controller ใช้ผลแล้ว · ChequeService ใช้ผลอยู่แล้ว · ด่านไฟล์แนบที่เรียกโดยไม่มีไฟล์ใน 2 target นี้ **0**
8. ทางเข้าอื่น: ยกเลิกการชำระ (หน้าเอกสาร · เช็คเด้ง · ยกเลิกการลงบัญชีรอบโอน · cascade ยกเลิกเอกสาร) ทั้งใบเดียว/หลายใบ → ตัวติดธงกลับตัวเดียว · ยกเลิก e-Tax ทางเข้าเดียว ·
   ปิดธงทางเข้าเดียว · มือถือ/LINE ไม่มีเส้นเหล่านี้ · ข้อจำกัด: cascade `VoidDocumentAsync` ทิ้งข้อความผล — ใบที่ถูกยกเลิกเองมีใบกำกับทาง ค ที่ยังมีผล = ด่านลูก `ChildBlocksAsync` บล็อกก่อนถึง · ใบอื่นในการจัดสรรเดียวกันได้ธงบนใบกำกับ (มองเห็นที่ใบ) แต่ข้อความไม่ถึงผู้กด
9. เข้มขึ้น + ทางไปต่อ: แถว e-Tax ที่ส่งอีเมลประทับเวลาแล้วยกเลิกในระบบไม่ได้ ⇒ ยกเลิก/ลดหนี้ทางกรมสรรพากรแล้วบันทึกที่เอกสาร (ทิศตรงข้าม `…O2_ทิศตรงข้าม_ไม่มีอีเมลประทับเวลา…`) ·
   ไฟล์หลักฐานที่แนบก่อนส่งไม่นับ ⇒ แนบไฟล์ตอบกลับการยกเลิกใหม่ (ทิศตรงข้าม `…O5_ทิศตรงข้าม…`) · ใบกำกับทาง (ค) ติดธงกลับ ⇒ รับชำระให้ครบแล้วยืนยันใหม่ / ทาง ก / ข
   (ทิศตรงข้าม `…O1_ทิศตรงข้าม…`) · **ผ่อนลง**: ผู้มีสิทธิ์ยกเลิก e-Tax/ยกเลิกเอกสารที่อ่านไฟล์แนบไม่ได้ ทำทางที่ไม่ใช้ไฟล์ได้อีกครั้ง (checker G1–G7)
10. ค่าที่ persist ก่อนแก้: ใบกำกับทาง (ค) ที่การรับชำระครอบยอดถูกยกเลิกไปแล้ว**ก่อน**คอมมิตนี้ — ไม่ถูกติดธงย้อนหลังอัตโนมัติ (→ Q2) · แถว e-Tax ที่ส่งอีเมลแล้วถูกยกเลิกในระบบ
    ก่อนคอมมิตนี้ — audit เดิมเขียน "ก่อนส่งถึงกรมสรรพากร" (→ Q2 กลุ่มเดียวกับข้อ 62) · ไม่มีคอลัมน์ใหม่ (ไม่มี migration) · ป้ายทาง (ค) ที่เขียนไว้แล้วอ่านได้ทันที
11. ฝ่ายค้าน: ขอ main agent ส่ง diff ให้ฝ่ายค้าน (เงิน/ภาษี/สิทธิ์) — โดยเฉพาะลำดับล็อกใหม่ใน `VoidPaymentAsync` (deadlock กับเส้นที่ล็อก Payment ก่อน Document ถ้ามี) และ
    การผ่อนด่านไฟล์แนบเมื่อไม่ส่งไฟล์
12. DOCUMENT_FLOW §2.4c (ย่อหน้า V1I) · §3.5 (`VoidPaymentAsync` ล็อกเอกสารก่อน) · Last verified · TEST_PLAN §0 (`--row`) · CHANGELOG (append)

## checker ที่รัน
`required_call_site_check` ✅ (632 กติกา ผ่าน · +9 แถวของ V1I · self-test ถอด/สลับ/ใส่สูตรต้องห้ามทุกแถวในตัว) · `attachment_gate_check` ✅ (14 target + negative test เดิม + G1–G7) · `record_arg_check` ✅ · `nullable_arg_check` ✅ · `using_check` ✅ ·
`undeclared_local_check` ✅ · `arg_type_check` ✅ · `service_interface_check` ✅ · `string_quote_close_check` ✅ · `comment_line_break_check` ✅ · `tuple_name_merge_check` ✅ ·
`dead_helper_check` ✅ · `identifier_space_check` ✅ · `namespace_shadow_check` ✅ · `accessibility_check` ✅ · `verbatim_string_check` ✅ · `flag_field_overwrite_check` ✅ ·
`write_permission_gate_check` ✅ · `html_attr_escape_check` ✅ · `onclick_js_string_check` ✅ · `js_dup_method_check` ✅ · `test_inventory --check` ✅ · `doc_commit_sha_check` ✅ ·
`node --check` สคริปต์ใน `documents.html` ✅ · awk brace = 0 ทุก .cs ที่แก้ · U+FFFD ไม่พบ · `check_all.sh` เต็มไม่ได้รัน (เครื่องช้า)

## ความเสี่ยงคอมไพล์ที่ตรวจไม่ได้ (ไม่มี SDK)
1. `EtaxVoidPolicy.StatusForVoid` (public) เรียก `DocumentVoidPreconditions.EffectiveEtax` (internal · assembly เดียวกัน) ส่ง `EtaxStatus` เข้า `EtaxStatus?` แล้ว `?? rowStatus`
2. `LivePaymentCoverageAsync(..., Guid? excludePaymentId = null)` ใช้ `excludePaymentId == null || a.PaymentId != excludePaymentId.Value` ใน LINQ-to-SQL (ตัวแปรที่ capture ไม่ใช่ optional ของ method ใน expression — ไม่ใช่ CS0854)
3. `LockDocumentsForPaymentVoidAsync` ส่ง `Guid[]` เป็นพารามิเตอร์ `ANY({0})` ผ่าน `ExecuteSqlRawAsync` (รูปเดียวกับ `LockDepositBalancesAsync`)
4. `CombineNotices(params string?[] parts)` · ตัวแปร `etaxCancellationFlag` (`var` = `string?`) ถูกกำหนดค่าใหม่
5. ประโยคต่อสตริงใน `AutoReceiptOnPaymentVoid` มีคอมเมนต์ `//` คั่นระหว่างตัวดำเนินการ `+`
6. `DocumentController.VoidPayment`: `string.Join(" · ", new[] { string?, string? }.Where(...))`
7. ตัวแปร `deny` ย้ายเข้าในบล็อก `if` ทั้งสอง controller (ไม่มีการอ้างนอกบล็อก)
8. เทสต์: named argument ที่อยู่ตำแหน่งตรงตามด้วย positional (`KeptOriginalCoverageLost(lastResolutionKeptOriginal: true, …, livePaymentCoverage: 0m, "RC-1", "PAY-2")`) — C# 7.2+ อนุญาต

## คำถามค้าง (ให้เจ้าของทบทวน)
- ✅ 49458e34 (รอบ 201 ทีม DV) **Q1** O1 ระบุใบทาง (ค) จากป้ายในหมายเหตุภายใน — ถ้าวันหน้าเปิดให้แก้หมายเหตุภายในของใบเสร็จที่อนุมัติแล้ว ป้ายจะหายได้ · ต้องการคอลัมน์ `EtaxKeptOriginalAt` (เก็บถาวร) แทนไหม
  (ต้องมี migration + เติมค่าจาก audit `RD-ETAX-ORIGINAL-STILL-VALID`)
- ✅ 49458e34 (รอบ 201 ทีม DV) **Q2** ใบกำกับทาง (ค) ที่การรับชำระครอบยอดถูกยกเลิกไปแล้วก่อนคอมมิตนี้ (ไม่มีธง) และแถว e-Tax ที่ส่งอีเมลประทับเวลาแล้วถูกยกเลิกในระบบก่อนคอมมิตนี้ — เพิ่มเป็นกลุ่มในรายงานข้อ 44
  (อ่านอย่างเดียว · แนวเดียวกับข้อ 62) ไหม · ระบุได้จาก: ป้ายทาง (ค) + ไม่มีธง + `LivePaymentCoverage` < ยอดใบ / แถว Voided + บันทึกอีเมลประทับเวลาของเอกสาร
- ✅ 49458e34 (รอบ 201 ทีม DV) **Q3** หลักฐานในเส้นปิดธงทาง (ก) (`ResolveEtaxCancellationAsync` — ข้อ 46) ยังรับไฟล์ใดก็ได้ของใบเสร็จเหมือน O5 เดิม — ใช้กติกา "แนบหลังเวลาส่ง e-Tax/อีเมล" ด้วยไหม
  (ไม่ทำในรอบนี้เพราะใบที่ส่งทางอีเมลไม่มี `SubmittedAt` — ต้องเลือกเวลาอ้างอิงจากบันทึกอีเมล)
- ✅ 49458e34 (รอบ 201 ทีม DV) **Q4** cascade `VoidDocumentAsync` ที่ยกเลิกการชำระหลายรายการในลูปเดียว — ยอดครอบของ O1 ไม่นับเฉพาะรายการที่กำลังยกเลิก (รายการก่อนหน้าในลูปที่ยังไม่ save ถูกนับว่ายังมีผล) ⇒ อาจไม่ติดธง
  ในเส้นนั้น (ปัจจุบันเส้นนั้นถูกด่านลูกบล็อกเมื่อมีใบเสร็จถือ VAT ที่ยังมีผล จึงไม่น่าถึง) · และ cascade ยังไม่ล็อกเอกสารอื่นในการจัดสรร (O3 ทำเฉพาะ `VoidPaymentAsync`)

## คอมมิต
- โค้ด + เอกสาร: `3f644286` · เติม sha: คอมมิตตามหลัง (ห้าม amend)


## ฝ่ายค้านรอบสาม (main agent ส่ง · 2026-10-01)

ความเสี่ยงคอมไพล์ 7 ข้อ: เปิดไฟล์ยืนยันแล้ว ไม่พบปัญหา · ผ่อนด่านไฟล์แนบเมื่อไม่ส่งไฟล์: ไม่พบทางที่ไฟล์ถูกแตะโดยไม่ผ่านด่าน

| ID | P | สถานะ | ที่แก้ |
|---|---|---|---|
| V1I-X1 | P2 | ✅ (main agent) | ลำดับล็อกใหม่ของ `VoidPaymentAsync` (เอกสาร → เลข JE) สวนกับอนุมัติ/ยกเลิกใบเสร็จ·ใบลดหนี้ที่อ้างใบเดียวกัน (เลข RV → เอกสาร) ⇒ deadlock 40P01 · แก้: `LockRelatedSourceDocumentAsync` ใน `ApproveDocumentAsync`/`VoidDocumentAsync` ทันทีหลังล็อกใบตัวเอง (ก่อน `AutoPostToJournalAsync`/`ReverseJournalEntryAsync`) · required_call_site 3 แถว (มี before) |
