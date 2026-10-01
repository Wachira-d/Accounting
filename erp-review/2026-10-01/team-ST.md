# รอบ 201 ทีม ST — Settlement

> ขอบเขต: BACKLOG §1.2 (A-ST1..A-ST10) + คำตัดสินข้อ 82 (C-9) + คำถามค้างทีม DV Q1 (main agent ส่งเพิ่มหลัง DV merge `e97ba288`) ·
> โค้ด commit `07baa11b` · ตรวจที่ HEAD `5eed54bf` → merge `e97ba288` · ไม่มี .NET SDK ⇒ **ยังไม่ได้คอมไพล์/รันเทสต์ในเครื่องนี้** (CI เป็นตัวแรก)

## 1. สถานะรายข้อ

| ID | สถานะ | file:line (หลัก) | เทสต์ |
|---|---|---|---|
| A-ST1 ป้าย `[SETTLEMENT:]` ใน `Payment.Notes` เป็นกุญแจ | ✅ | `Models/Entities/Payment.cs` (`SettlementBatchId`) · `Helpers/SettlementPaymentOwner.cs` (ตัวประทับใน `SavingChanges`) · `SettlementPostingService.EnsureReceiptAsync` (ประทับ + ตรวจว่าประทับ) · ผู้อ่าน 8 จุดย้ายมาคอลัมน์ (`SettlementPaymentsAsync` · `OrphanArtifactsAsync` · `AcknowledgeOrphanAsync` · `PendingReceiptsElsewhereAsync` · `Lines.FrozenPartsAsync` · `Lines.PostingArtifactsAsync` · `SettlementArtifactGuard.CheckDocumentPaymentsAsync` · `DocumentService.VoidPaymentAsync` ×2 นิพจน์) · `BatchIdFromPaymentNotes` ถอด · `DatabaseMigrationHelper.PaymentSettlementOwnerMigrationSql` (คอลัมน์ + backfill ครั้งเดียว) · `SettlementPostingKeys.PaymentOwnerBackfillSql` | `ST1_*` 6 เทสต์ · checker NOTES_MARKER_FORBID (self-test ในตัว) |
| A-ST10 ส่ง LINE ไม่มีบันทึกการส่ง | **NOT-A-BUG** | `DocumentLineDeliveryService.SendDocumentLineAsync` **ไม่มีผู้เรียกทั้งเรพ** (`grep` = DI ใน `Program.cs:800` เท่านั้น) ⇒ ไม่มีการส่งให้นับ · บันทึกเงื่อนไขการต่อสายไว้ใน `Helpers/DocumentDeliveryEvidence.cs` (push ปัจจุบันกลืนผลล้ม ⇒ ต้องทำ push ที่คืนผล + บันทึก + ช่องทางในตัวตัดสินพร้อมกัน) | — |
| A-ST2 ล็อก gateway ไม่ครอบเส้นไฟล์/จับคู่มือ/rematch | ✅ | `SettlementImportService.Lines.cs` `SyncIntentStampsAsync` (ล็อก + ตรวจซ้ำใต้ล็อก ⇒ 409 `SETTLEMENT-INTENT-TAKEN`) · `LockGatewaysAsync`/`LockGatewayAsync` (ตัวเดียว · เรียงชื่อ) · `VoidBatchAsync` · `PersistAsync` ใช้ตัวล็อกตัวเดียว | required_call_site + negative (ไม่มี pure — ล็อกเป็นเรื่อง DB) |
| A-ST3 ข้อความยอดไม่ลงตัว (ไม่กรอกถึงวันที่) | ✅ | `Helpers/SettlementBatchMath.cs` `UnbalancedNextStep` (ใช้ใน `Plan`) | `ST3_*` 2 (Theory 3 แถว) |
| A-ST5 การรับรู้ไม่ผูกกับเหตุ | ✅ | `Document/Payment.SettlementOrphanAckReasonHash` · `SettlementOrphanTriage.ReasonHash/RefusalKey/ChildKey` + `AckCovers(…, reasonHash)` · `SettlementUnpostRefusal.Code` (รหัสโครงสร้าง 4 เหตุ Hard) · `AcknowledgeOrphanCoreAsync` ประทับ + audit | `ST5_*` 2 · ปรับเทสต์เดิม 3 ไฟล์ให้การรับรู้ประทับลายนิ้วมือแบบ service |
| A-ST6 ใบที่อ้างชั้นหลาน | ✅ | `SettlementPostingService.OrphanChildrenAsync` (ไล่ทุกชั้น ≤ `MaxChildDepth` = 6 · กันวน) · `SettlementOrphanTriage.DescendantHardReasons` + `ChildUnvoidableReason(c, via)` | `ST6_*` 3 |
| A-ST4 รายงานระดับช่องทาง | ✅ | `ISettlementPostingService.ChannelOrphanReportAsync` · `Helpers/SettlementOrphanReport.cs` · `SettlementController.ChannelOrphans` (`GET settlement/channels/{id}/orphans` · View · ยอดซ่อนตาม D-P5) · `settlement-channels.html` ปุ่ม "ของกำพร้า" · `api.js` +1 เมธอด | `ST4_*` 2 |
| A-ST7 ผู้ตัดสินการจับคู่/จัดประเภทไม่ถูกบันทึก | ✅ | `SettlementLine.DecidedBy/DecidedAt` · `Lines.MarkDecided` (จับคู่มือทุกแบบ · จัดประเภท + ใช้กับป้ายเดียวกัน) · `AssignLineMatchAsync` รับ `userId` (interface + controller) · `Helpers/SettlementLineMakers` → `BuildGateAsync` ส่งเข้า `SodSelfApproval` ผ่าน**พารามิเตอร์เดิม** (ไม่แตะเมธอดของทีม TX) | `ST7_*` 2 |
| A-ST8 ลายนิ้วมือ Piece ตอนออกเอกสาร | ✅ | `Document.SettlementPieceFingerprint` · `SettlementPlanFingerprint.PieceHash` (ประทับใน `CreateOrAdoptAsync`) · ผู้อ่าน: `Lines.FrozenPartsAsync` → `SettlementPartialEdit.Refusal` (แก้กลับให้ตรงที่ออก = ได้) · `IssuedDrift` → คำเตือน `IssuedPieceDrift = 70` (ไม่บล็อก) ใน `BuildGateAsync` · ไม่มีค่า = พฤติกรรมเดิม | `ST8_*` 3 |
| A-ST9 คีย์รุ่นก่อนชนแถวต่างรายการ | ✅ | `SettlementLine.KeyVersion` (`SettlementTxnKey.StoredKeyVersion`) · `SettlementTxnKey.LegacyKeySets/CountsAsExisting` · `PersistAsync`/`ExistingKeysAsync` | `ST9_*` 2 |
| C-9 (ข้อ 82) ใบกำพร้าที่รับรู้ = ใบแรกของวัน | ✅ | `BuildGateAsync` (ตัวแยกของกำพร้ามาก่อน) · `DuplicateSalesAsync` · `SettlementSummarySupplement.AckedOrphanCountsAsFirst/OrphanFirstDuplicates` · `SettlementContentOverlap.AgainstBatchesAsync` (บรรทัดของรอบเจ้าของรวมที่ถูกลบ) | `C9_*` 3 |
| DV Q1 (งานเพิ่มจาก main agent) | ✅ | `SettlementPostingService.UnpostCoreAsync` — ผลของ `VoidDocumentAsync` (ธง e-Tax · ภาษีขายที่ถอยไม่ได้) เข้าข้อความผล + audit `notices` | required_call_site + negative |

ลำดับทำตาม §5: A-ST1 → A-ST10 → A-ST2 → A-ST3 → A-ST5 → A-ST6 → A-ST4 → A-ST7 → A-ST8 → A-ST9 · C-9 · DV Q1

## 2. การตัดสินใจที่ต้องรู้ (ทิศที่มองเห็นและย้อนได้)

1. **A-ST1 ผู้เขียนในธุรกรรมเดียวกันโดยไม่แตะ `CreatePaymentAsync`** (ช่วงเมธอดทีม TX ระยะ 2) และไม่เพิ่มช่องใน `CreatePaymentRequest` (DTO ที่ controller bind จาก body ⇒ ผู้ใช้ตั้งเจ้าของเองได้) —
   ใช้ตัวฟัง `DbContext.SavingChanges` ที่เปิดเฉพาะรอบการเรียกของผู้ลงบัญชี (DbContext scoped ต่อคำขอ) + ตรวจหลังสร้างว่าประทับจริง (ไม่ประทับ = 409 ล้มดัง)
2. **A-ST1 backfill ครั้งเดียว** (ใน DO block ที่ตรวจ information_schema แบบ `DepositBaseSplitMigrationSql`) — ถ้า backfill ทุกบูต ป้ายที่ผู้ใช้พิมพ์หลัง deploy (รูปแบบตรง) จะกลายเป็นเจ้าของ = ช่องโหว่เดิม ·
   หลักฐาน 5 ข้อพร้อมกัน (รูปแบบข้อความที่ระบบเขียนไม่เปลี่ยนตั้งแต่ `84d47dda`) · ป้ายที่ไม่ผ่าน = ไม่ใช่ของรอบโอน
3. **A-ST5 การรับรู้ก่อนรอบ 201 (ไม่มีลายนิ้วมือ) = ไม่ครอบ** (DOCTRINE §1 · หลักเดียวกับข้อ 29) ⇒ ช่องทางที่มีของกำพร้ารับรู้ไว้แล้วจะกลับมาบล็อกจนกดรับรู้ใหม่หนึ่งครั้ง (มีปุ่ม · ข้อความบอกเหตุ) —
   ฟีเจอร์รับรู้เกิดรอบ 200 จึงกระทบน้อย · ลายนิ้วมือทำจาก**รหัสโครงสร้าง**ของเหตุ (ไม่ใช่ถ้อยคำ) ⇒ แก้ข้อความในโค้ดไม่ทำให้การรับรู้หลุด
4. **A-ST8 ไม่บล็อกเมื่อเนื้อหาคลาด** — เดิมคิดจะบล็อกตอนรับเอกสารเดิมมาลงต่อ แต่เอกสารที่ยกเลิกไม่ได้ (e-Tax) จะกลายเป็นทางตัน (S3-1) และยอดยังถูกด่านยอดตรวจอยู่ ⇒ เตือน `IssuedPieceDrift` ·
   ตัวเทียบรอบค้างครึ่งทาง: กติกาเดิมยังอยู่ทุกตัว + ผ่อนเฉพาะ "แก้กลับให้ตรงที่ออก"
5. **A-ST9 บรรทัดที่นำเข้าระหว่าง deploy รอบ 200 กับรอบ 201** ไม่มีรุ่น (null) ⇒ ยังถูกถือเป็นรุ่นก่อน (พฤติกรรมเดิม — การชนแคบยังเกิดได้กับบรรทัดช่วงนั้นเท่านั้น · บรรทัดใหม่ปิดแล้ว) — ไม่เดาจากวันที่สร้าง
6. **C-9 ด่านซ้ำของใบกำพร้า**: เทียบเนื้อหา (แถวไม่มี id) กับบรรทัดของรอบเจ้าของ**ที่ถูกลบพร้อมรอบ** (`IgnoreQueryFilters` · tenant+ช่องทาง+รอบที่ระบุ) + เลขรายการ/เลขออเดอร์ตรง = รายได้ซ้ำ —
   ตัวกันซ้ำระดับออเดอร์เดิมดูแค่รอบที่ Posted ⇒ ถ้าไม่เพิ่ม ไฟล์เดิมที่นำเข้าใหม่ด้วยเลขรอบต่างจะกลายเป็นใบสรุปเพิ่มเติม

## 3. ไฟล์นอกขอบเขตที่แตะ (เล็กที่สุด)

- `Services/Implementations/DocumentService.cs` `VoidPaymentAsync` — **2 นิพจน์** (ช่วงเมธอดทีม DV): `BatchIdFromPaymentNotes(payment.Notes)` → `payment.SettlementBatchId` และ `…(locked.Notes)` → `locked.SettlementBatchId` ·
  BRIEF ขอ "บรรทัดเดียว" แต่เมธอดเรียกด่านสองครั้ง (ก่อนธุรกรรม + ใต้ล็อก S3-8) ⇒ แทนตรงตัวทั้งสองจุด ไม่เปลี่ยนตรรกะอื่น
- `Models/Entities/Document.cs` +2 property (`SettlementOrphanAckReasonHash` · `SettlementPieceFingerprint`) + จัดกลุ่ม "ไม่ตามไป" ใน `SettlementPaidReissue.DocumentNotCarriedFields` (เทสต์ reflection ของ V1G)
- `Data/DatabaseMigrationHelper.cs` บล็อกท้ายไฟล์ "รอบ 201 ทีม ST" + 1 บรรทัด `AddRange` ในบล็อก settlement (ก่อนผัง — เทสต์ล็อกว่าผังเป็นคำสั่งสุดท้าย) · merge กับบล็อกทีม DV แล้ว
- `wwwroot/js/api.js` +1 เมธอด `getSettlementChannelOrphans`
- `tools/required_call_site_check.py` บล็อก "รอบ 201 ทีม ST" (+23 แถว · NOTES_MARKER_FORBID) + ปรับแถวเดิม 13 แถวให้ตรงกุญแจใหม่ (FrozenParts · OrphanArtifacts ×2 · AcknowledgeOrphan · CheckDocumentPayments ·
  VoidPaymentAsync · PendingReceiptsElsewhere · PersistAsync ×3 (LegacyKeySets · ล็อก gateway) · BuildGate (ชุดผู้ทำ) · AssignLineMatch)

## 4. ความเสี่ยงคอมไพล์ที่เหลือ

- ไม่มี SDK — checker ทั้งชุดผ่าน (รายการใน §6) · จุดที่ checker ตรวจไม่ได้: (ก) `EventHandler<SavingChangesEventArgs>` lambda ที่คืน `int` (ถูกต้องตามภาษา — expression statement) ·
  (ข) ส่ง `IEnumerable<(Guid Id, string?, string?)>` เข้าพารามิเตอร์ tuple ชื่อต่าง (identity conversion) · (ค) `out var` ภายในเงื่อนไข `&&` ใน `SettlementPartialEdit.Refusal` ·
  (ง) `AssignLineMatchAsync` เปลี่ยนลายเซ็น (เพิ่ม `userId` ลำดับที่ 2 — ผู้เรียกมีตัวเดียวคือ controller แก้แล้ว)
- เทสต์ `ST1_ตัวฟังSaveChanges…` สร้าง `AccountingDbContext` แบบ offline แล้วเรียก `SaveChanges` ที่ต้องล้ม (ไม่มีฐาน) — พึ่งว่าตัวฟังทำงานก่อนเปิดการเชื่อมต่อ (EF Core: `SavingChanges` ก่อน `StateManager.SaveChanges`)

## 5. คำถามค้าง

1. **A-ST10**: จะต่อสาย "ส่งเอกสารผ่าน LINE" หรือลบ `DocumentLineDeliveryService` (ของที่ไม่มีผู้เรียก · F2 ข้อ 2) — ถ้าต่อสาย ต้องทำ push ที่คืนผล + บันทึกการส่ง + ช่องทางในตัวตัดสินหลักฐานพร้อมกัน
2. **A-ST5**: ยอมรับให้การรับรู้ของรอบ 200 ต้องกดใหม่หนึ่งครั้งไหม (ทางที่เลือกตาม DOCTRINE) — ทางกลับคือถือ null = ครอบ (บรรทัดเดียวใน `AckCovers`)
3. **A-ST7**: doc-comment ของ `SettlementPostingGate.SodSelfApproval` (ไฟล์ช่วงทีม TX) ยังเขียนว่า "ผู้ตัดสินการจับคู่ไม่ได้ถูกบันทึก" — ให้ทีม TX ปรับเมื่อยุบเข้า `ApprovalControlPolicy` (A-TX3)
4. **ฝ่ายค้าน (F3 ข้อ 11)**: ยังไม่ได้ส่ง diff ให้ subagent ฝ่ายค้าน — ขอ main agent ส่งรอบรวม (จุดเสี่ยง: backfill A-ST1 · ลำดับล็อก A-ST2 · การรับรู้เดิมกลับมาบล็อก A-ST5)

## 6. checker ที่รัน (ผลทั้งหมดผ่าน)

record_arg · nullable_arg · using · undeclared_local · arg_type · service_interface · write_permission_gate · attachment_gate · dto_nullable_contract · string_quote_close ·
comment_line_break · tuple_name_merge · accessibility · identifier_space · html_attr_escape · onclick_js_string · enum_number_compare · dead_helper (หลังทำ 5 ตัวเป็น internal) ·
settings_reader · gl_code · advisory_lock_key · verbatim_string · escape_helper · deep_link_param · dead_link · js_dup_method · css_var · namespace_shadow · regex_line_span ·
ocr_helper_test · test_inventory --check (วางทับ §0 ด้วย `--row`) · required_call_site (เต็ม + negative มือ 21 กรณีจับได้ครบ) · `node --check` (`settlement-channels.html` · `api.js`) · awk brace/U+FFFD ไฟล์ .cs ที่แก้
