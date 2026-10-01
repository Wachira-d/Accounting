# รอบ 200 · ทีม V2 — ของกำพร้าในรอบโอน settlement

ขอบเขต: DECISIONS ข้อ 10 · review198-S3 S3-6 (ค้าง) / S3-8 / S3-11 · review198-S4 S4-1 "ความเสี่ยงที่เหลือ" / S4-5 ·
**ยังไม่ได้คอมไพล์** (env ไม่มี .NET SDK) · คอมมิตโค้ด `d7fb2c43` (รายงานนี้ + sha อยู่ในคอมมิตตามหลัง) · `bash tools/check_all.sh` ผ่านทั้งหมด (checker 53 · sim 8 · brace/node/U+FFFD · TEST_PLAN §0)

> หมายเหตุ worktree: worktree ของทีมถูกสร้างจาก `origin/master` (ไม่มีโค้ด settlement/brief) — ย้ายสาขาของ worktree นี้ไปที่
> `5c1fe028` (ปลาย `claude/erp-system-review-team-660mev`) ก่อนเริ่ม (tree สะอาด ไม่มีงานหาย) ⇒ คอมมิตของทีมต่อจาก `5c1fe028`

## สรุปรายการ

| ID | สถานะ | ที่แก้ (file:line ณ คอมมิตนี้) | เทสต์ |
|---|---|---|---|
| ข้อ 10 (ก) ใบที่อ้างยกเลิกไม่ได้ ⇒ `Unvoidable` | ✅ | `Helpers/SettlementPostingGuards.cs` `SettlementOrphanChild` · `SettlementOrphanTriage.Split(..., children)` · `ChildUnvoidableReason` · `Services/Settlement/SettlementPostingService.cs` `OrphanChildrenAsync` (โหลดใบที่อ้างจาก `DocumentVoidPreconditions.ChildFactsAsync` ตัวเดียวกับ `VoidDocumentAsync` + e-Tax Accepted · รายงานล็อก · `WhtCertVoidGuard.CheckDocumentAsync` · สถานะ `Sent`) | `V2_ใบกำพร้ามีใบลดหนี้อ้างที่eTaxตอบรับแล้ว_…` · `V2_ใบที่อ้างอยู่ในรายงานล็อก_ส่งลูกค้าแล้ว_50ทวิยื่นแล้ว_…` (Theory ×3) · ทิศตรงข้าม `V2_ทิศตรงข้าม_ใบที่อ้างยังยกเลิกได้_…` · `V2_ใบกำพร้าไม่มีเหตุใดเลย_…ไม่ลาม…` |
| ข้อ 10 (ข) รับรู้ของกำพร้า | ✅ | endpoint `POST api/companies/{id}/settlement/orphans/acknowledge` (`Controllers/SettlementController.cs` `AcknowledgeOrphan` — `[RejectApiKey]` + `[RequirePermission(Settlement.Post)]`) → `SettlementPostingService.AcknowledgeOrphanAsync` (เหตุผลบังคับ `AckReasonProblem` · สิทธิ์ `Settlement.Post` ตรวจใน service ซ้ำ · ล็อกช่องทาง `JobLock`/`SettlementChannelLock`) → `AcknowledgeOrphanCoreAsync` (ตัวแยกตัวเดียวใต้ล็อก · `AckRefusal` รับรู้ได้เฉพาะกอง Unvoidable · ประทับ `SettlementOrphanAckAt/By/Reason` บน `Document`/`Payment` + `AddChainedAuditLog` · รับรู้ไว้แล้ว = ตอบซ้ำไม่บันทึกซ้ำ) · คอลัมน์ใหม่ 6 ตัวใน `DatabaseMigrationHelper.SettlementSchemaStatements` · ด่านลงบัญชี `SettlementPostingGate.Evaluate`: Unvoidable ยังไม่รับรู้ = **บล็อก** (ทางไปต่อ = รับรู้) · รับรู้แล้ว = แสดง ไม่บล็อก (`SettlementPostingFacts.AcknowledgedOrphans`) · echo: `SettlementPostingPreview.Orphans` (`SettlementOrphanItem` — กอง/ป้ายกอง/เหตุ/ทางไปต่อ/ผู้-เวลา-เหตุผล/`CanAcknowledge`) · หน้า `settlements.html` `renderOrphans` + ปุ่ม `ackOrphan` (ใช้ modal เหตุผลเดิม) · `api.js` `acknowledgeSettlementOrphan` | `V2_ยังไม่รับรู้_บล็อก…_รับรู้แล้ว_ลงบัญชีได้และแสดงผู้รับรู้` · `V2_การรับรู้ค้างบนชิ้นที่ตอนนี้ยกเลิกได้แล้ว_ไม่มีผล…` · `V2_การรับชำระกำพร้าที่ใบเสร็จeTaxตอบรับแล้ว_รับรู้ได้…` · `V2_ปุ่มรับรู้_…ปฏิเสธพร้อมทางไปต่อ…` · `V2_เหตุผลรับรู้ว่าง_ปฏิเสธ` (Theory ×3) · `V2_เหตุผลรับรู้ยาวเกิน…` · ผู้ไม่มีสิทธิ์: `V2_endpointรับรู้ของกำพร้า_ต้องมีสิทธิ์ลงบัญชีรอบโอนและห้ามคีย์API` + แถว `required_call_site_check` (สิทธิ์ใน service ก่อนล็อก) · schema `V2_Migration_คอลัมน์รับรู้ของกำพร้า_…ตรงกับmodelของEF` · ปรับความหมายเทสต์เดิม `S41_ทิศตรงข้าม_…บล็อกจนรับรู้…` · `S36_…รับรู้แล้วเตือนไม่บล็อก_ยังไม่รับรู้บล็อก…` |
| S3-8 race ตอน commit | ✅ (ยืนยัน — ทีม S4 แก้แล้ว) | `SettlementArtifactGuard.CheckLockedAsync` (`FOR SHARE` แถวรอบโอน) ใต้ธุรกรรมของ `VoidDocumentAsync`/`VoidPaymentAsync` + แถว required_call_site เดิม · รอเฉพาะช่วงธุรกรรม commit ของ `CommitPostedAsync` (สั้น — ไม่ใช่ session lock ที่ถือทั้งการลงบัญชี) ⇒ ไม่ทำให้คำขอค้าง · ไม่แตะโค้ด | (เดิม) |
| S3-11 (1) `Take(200)` | ✅ | `OrphanArtifactsAsync` ไม่ตัดรอบ · การรับชำระของทุกรอบที่ยกเลิกค้นด้วยคำค้นเดียว (`SettlementPostingKeys.PaymentMarkerHead` + `BatchIdFromPaymentNotes`) แทนวนทีละรอบ · checker `forbid .Take(` | (checker) |
| S3-11 (2) GET โหลดทั้งรอบ/ช่องทางถูกลบ | ✅ | `UnpostBlockersAsync` อ่านแค่หัวรอบ (Status/PayoutDate) · checker `forbid await LoadAsync(` | (checker) |
| S3-11 (3) SoD แค่ผู้สร้างรอบ | ✅ บางส่วน | `SettlementPostingGate.SodSelfApproval(bool, string?, IEnumerable<string?>, Guid)` นับผู้สร้างบรรทัด (ผู้เติมไฟล์เข้ารอบเดิม) · ผู้ตัดสินการจับคู่/จัดประเภท **ไม่ได้ถูกบันทึกบนบรรทัด** (Lines.cs ไม่ประทับ `UpdatedBy` — ไฟล์ทีม T/I) ⇒ 📋 | `S311_SoD_ผู้เติมไฟล์เข้ารอบเดิมกดลงบัญชีเอง_บล็อก…` | · ✅ 07baa11b รอบ 201 ทีม ST (A-ST7)
| S3-11 (4) JournalManage ใน Post/Unpost | 📋 (คำถามค้าง) | ไม่แก้: คำอธิบายสิทธิ์ `Settlement.Post` (`PermissionKeys.cs:275`) นิยามรวม "สร้าง…JE · ยกเลิกการลงบัญชี" ไว้แล้ว ⇒ เพิ่มเงื่อนไข JournalManage = เปลี่ยนนโยบายสิทธิ์ (ผู้ที่มี Post แต่ไม่มี Journal ถูกกันทันที) — ต้องให้เจ้าของตัดสิน | — |
| S3-11 (5) ป้าย `[SETTLEMENT:]` ใน `Payment.Notes` ที่ผู้ใช้พิมพ์ได้ | 📋 | ต้องแก้ที่ `DocumentService.CreatePaymentAsync` (ขอบเขตทีม V1) หรือเพิ่มคอลัมน์ `Payment.SettlementBatchId` แล้วย้ายตัวหาทุกตัว (ป้ายเดียวกันใช้ใน `SettlementArtifactGuard`/`Lines.cs`/ตัวหาของกำพร้า) — insider เท่านั้น (ต้องรู้ id รอบ) · ขนาดเกินรอบนี้ | — | · ✅ 07baa11b รอบ 201 ทีม ST (A-ST1)
| S4-5 ลายนิ้วมือ Piece ตอนออกเอกสาร | 📋 | ต้อง (1) คอลัมน์ใหม่บน `Document` เก็บลายนิ้วมือตอน `CreateOrAdoptAsync` **และ** (2) เปลี่ยนตัวเทียบใน `SettlementImportService.Lines.LoadRedecidableBatchAsync` (ไฟล์ทีม T) + (3) ตัดสินเอกสารเก่าที่ไม่มีลายนิ้วมือ · ทำแค่ (1) = เก็บค่าที่ไม่มีผู้อ่าน (F2 ข้อ 2) จึงไม่ทำครึ่งเดียว | — | · ✅ 07baa11b รอบ 201 ทีม ST (A-ST8)

## การเปลี่ยนพฤติกรรมที่ผู้ใช้เห็น (F2 ข้อ 8)

- **เข้มขึ้น**: ของกำพร้าที่ยกเลิกไม่ได้จริงเดิม "เตือนไม่บล็อก" (S4) ⇒ ตอนนี้ **บล็อกจนมีคนรับรู้** — ผู้ใช้ที่ถูกกันมีทางไปต่อ = ปุ่ม "รับรู้ของกำพร้า" บนพรีวิว (ต้องมีสิทธิ์ลงบัญชีรอบโอน + เหตุผล) · ผู้ที่มีแค่สิทธิ์ดูเห็นรายการแต่ไม่เห็นปุ่ม · เทสต์ทิศตรงข้าม `V2_ยังไม่รับรู้_บล็อก…_รับรู้แล้ว_ลงบัญชีได้…`
- **ผ่อนลง**: ใบกำพร้าที่มีใบลดหนี้อ้างซึ่งยกเลิกไม่ได้ เดิมบล็อกถาวร ⇒ รับรู้ได้ · กองที่ยังยกเลิกได้ (ทันที/เมื่อทำขั้นก่อน) **ยังบล็อกเหมือนเดิม** และปุ่มรับรู้ปฏิเสธพร้อมทางไปต่อ (`AckRefusal`)
- การรับรู้ที่ค้างอยู่บนชิ้นซึ่งภายหลังกลายเป็นยกเลิกได้ (เช่นรายงานถูกปลดล็อก) ⇒ ไม่มีผล ยังบล็อก + ข้อความบอก "การรับรู้เดิมไม่มีผล"

## ไฟล์นอกขอบเขตที่แตะ (เล็กที่สุด)

- `Helpers/DocumentVoidPreconditions.cs` (ทีม V1): แยก `ChildFactsAsync` (ข้อเท็จจริงดิบพร้อม `ChildId`) ออกจาก `ChildBlocksAsync` — กติกา/ข้อความเดิมทุกตัวอักษร · `DocumentVoidChildFact` เพิ่มพารามิเตอร์ท้าย `Guid? ChildId = null` · เหตุ: ตัวแยกของกำพร้าต้องรู้ id ของใบที่อ้างโดยไม่เขียนเงื่อนไขชุดที่สอง (F2 ข้อ 4)
- `Models/Entities/Document.cs` · `Payment.cs` · `Data/DatabaseMigrationHelper.cs` (บล็อก settlement) · `wwwroot/js/api.js` (1 เมธอด)

## ความเสี่ยงคอมไพล์ (ไม่มี SDK)

- `SettlementOrphanTriage.Split` ใช้ tuple ชื่ออนุมาน `(c.ParentId, Why: …)` แล้วอ้าง `x.ParentId` (C# ≥ 7.1 — โปรเจกต์ net8 ✔)
- `OrphanArtifactsAsync` มี local function `AckOf` ใน async method (อนุญาต) · `docRows.ToDictionary(..., d => AckOf(...))` ชนิดค่า `SettlementOrphanAck?`
- `SettlementOrphanItem` เป็น positional record ที่มี property คำนวณ `PileLabel` ในตัว (System.Text.Json ส่งออกให้ — ชื่อ `pileLabel`)
- EF: `d.SettlementOrphanAckAt/By/Reason` ใน projection ของ query ที่มีอยู่ · `p.Notes.Contains(SettlementPostingKeys.PaymentMarkerHead)` (const) · `_db.CompanyUsers.Any(...)` ใน `Where` (รูปแบบเดียวกับ `BankService.CompanyMemberNamesAsync`)
- เทสต์อ่านคีย์สิทธิ์ผ่าน `GetCustomAttributesData().ConstructorArguments[0]` (ตัวกรองเก็บคีย์ใน field ส่วนตัว)
- overload ใหม่ `SodSelfApproval(bool, string?, IEnumerable<string?>, Guid)` คู่กับรูปเดิม 3 อาร์กิวเมนต์ (ไม่กำกวม — จำนวนอาร์กิวเมนต์ต่าง)

## คำถามค้าง (ให้เจ้าของตัดสิน)

1. **"ส่งลูกค้าแล้ว" (`DocumentStatus.Sent`) ของใบที่อ้าง = ยกเลิกไม่ได้** — DECISIONS ข้อ 10 ระบุไว้ในคำถาม แต่ `VoidDocumentAsync` ยังยกเลิกใบ Sent ได้ · เลือกทิศ "ให้รับรู้ได้" (มองเห็น · มีผู้/เหตุผล · ย้อนดูได้) — ถ้าเจ้าของต้องการให้ยกเลิกใบลดหนี้ก่อนเสมอ ให้ถอด `SentToCustomer` ออกจาก `ChildUnvoidableReason`
2. ใบที่อ้างซ้อนอีกชั้น (หลานของใบกำพร้า) ไม่ตามต่อ — ใบลูกที่มีหลานซึ่งยกเลิกไม่ได้ยังตกกอง NeedsUserAction · ✅ 07baa11b รอบ 201 ทีม ST (A-ST6)
3. การรับรู้ไม่มีปุ่ม "ถอนการรับรู้" (ประทับครั้งเดียว + audit) — ถ้าต้องการ ต้องเพิ่ม endpoint + audit ฝั่งถอน
4. S3-11 (4) JournalManage ใน Post/Unpost — ดูตารางด้านบน
5. ✅ 49458e34 (รอบ 201 ทีม DV · A-DV5) ธงรับรู้ไม่ได้ echo ใน `DocumentResponse` (หน้าเอกสาร) — แสดงเฉพาะในพรีวิวรอบโอน (ตามโจทย์) · ถ้าต้องการบนหน้าเอกสาร ต้องแตะ `MapToResponse` ของ `DocumentService` (ขอบเขตทีม V1)
6. **F3 ข้อ 11 (ฝ่ายค้าน)** ยังไม่ได้ทำ — agent ทีมนี้ไม่มีเครื่องมือเรียก subagent · ขอให้ main agent ส่ง diff `d7fb2c43` ให้ฝ่ายค้าน 1 รอบ
   (3 คำถาม: ทางเข้าอื่นที่ประทับธง/ข้ามด่าน? ทิศตรงข้าม — ของกำพร้าที่ยกเลิกได้หลุดเป็น "รับรู้ได้" ไหม? สถานะปลายทางประทับเองไหม — ธงรับรู้ประทับเฉพาะเมื่อคนกด)
