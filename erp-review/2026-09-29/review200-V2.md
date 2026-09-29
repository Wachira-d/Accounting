# ฝ่ายค้าน รอบ 200 · ทีม V2 (รับรู้ของกำพร้า · DECISIONS ข้อ 10 · S3-11 บางส่วน)

ขอบเขต: `git diff 5c1fe028 fa54a422` (โค้ด `d7fb2c43` + รายงาน `fa54a422`) · อ่านอย่างเดียว · **file:line ทุกตัวอ้าง ณ `fa54a422`**
(HEAD ปัจจุบันขยับไปแล้วจาก merge ทีมอื่น — ไฟล์เดียวกันถูกต่อพารามิเตอร์ท้าย record แต่ไม่แตะตรรกะของกำพร้า)
· ยังไม่ได้คอมไพล์ (ไม่มี SDK) · `tools/required_call_site_check.py` / `accessibility_check.py` รันไม่จบใน 9 นาทีในเครื่องนี้ (timeout — **ไม่ได้ยืนยันซ้ำ** ผลที่ทีมรายงาน) ·
checker อื่นที่รันผ่าน: record_arg · nullable_arg · using · tuple_name_merge · undeclared_local · arg_type

## สรุป

| ID | ระดับ | จัด | เรื่อง |
|---|---|---|---|
| V2-C1 | P2 | CONFIRMED | "ส่งลูกค้าแล้ว" ผูกกับ `DocumentStatus.Sent` ที่ไม่มีโค้ดไหนประทับ ⇒ ทริกเกอร์ที่เจ้าของระบุไว้ในข้อ 10 ไม่เคยทำงานจริง (เทสต์ Theory เขียวด้วยอินพุตสังเคราะห์) |
| V2-C2 | P3 | CONFIRMED | หน้าจอแสดง "✅ รับรู้แล้ว" บนรายการที่การรับรู้ **ไม่มีผลแล้ว** (กองยกเลิกได้/ต้องทำขั้นก่อน) |
| V2-C3 | P3 | CONFIRMED (latent) | ธุรกรรมของปุ่มรับรู้ไม่ `ChangeTracker.Clear()` ต้น lambda ของ execution strategy — หลัง merge ไม่ตามแบบแผน C-20 ที่ `CommitPostedAsync` ใช้ ⇒ ถ้าวันหนึ่งเปิด retry = audit ซ้ำ |
| V2-C4 | P3 | CONFIRMED | S3-11: ตัวหาการรับชำระกำพร้าดึง `Id+Notes` ของการรับชำระที่มีป้ายรอบโอน **ทุกแถวของบริษัท (ทุกช่องทาง ทุกเวลา)** ทุกครั้งที่เปิดพรีวิว/ลงบัญชี |
| V2-P1 | P3 | PLAUSIBLE | การรับรู้ผูกกับชิ้น ไม่ผูกกับรอบที่ตรวจ ⇒ รอบที่นำเข้าใหม่ด้วย PayoutRef เดียวกับรอบเจ้าของ (กรณีซ้ำแท้) ผ่านเงียบถ้ามีคนรับรู้ไว้ก่อนเพื่อรอบอื่น · audit ไม่บันทึกว่าตรวจเทียบรอบไหน |
| V2-P2 | P3 | PLAUSIBLE (by design) | รับรู้แล้วลงบัญชีได้ทั้งที่ผังพักค้างยอดของใบกำพร้า — ไม่มีด่านเชิงตัวเลข · "แสดงในรายงานรอบโอน/ช่องทาง" ของข้อ 10 มีแค่ในพรีวิว |
| V2-P3 | P3 | PLAUSIBLE | การรับรู้ที่ "ไม่มีผล" (ยกเลิกได้ชั่วคราว) กลับมามีผลเองเมื่อชิ้นกลับเป็นยกเลิกไม่ได้อีก (เช่นปลดล็อก→ล็อกรายงานใหม่) โดยไม่มีคนตรวจซ้ำ |
| V2-P4 | P3 | PLAUSIBLE (ทีมบันทึกแล้ว) | ใบที่อ้างซึ่งมี "หลาน" ที่ยกเลิกไม่ได้ ยังตกกอง NeedsUserAction ⇒ ช่องทางบล็อกถาวรแบบเดิมในกรณีสองชั้น |

**ไม่พบ P0/P1** · ไม่พบทางเข้าที่ข้ามด่าน · ไม่พบ query ขาด `CompanyId` · ไม่พบความเสี่ยงคอมไพล์ที่ยืนยันได้

---

## 1. ทางเข้าอื่น (ด่าน `Settlement.Post` · เหตุผล · ล็อกช่องทาง · tenant)

**NOT-A-BUG (ตรวจแล้ว)**
- ผู้เขียนธง `SettlementOrphanAck*` มีจุดเดียวในทั้งเรพ: `SettlementPostingService.AcknowledgeOrphanCoreAsync` (`SettlementPostingService.cs:858–897`) — grep ทั้งเรพ
  (นอก worktree) ไม่พบผู้เขียนอื่น · ไม่มี `SetValues`/`MemberwiseClone`/deserialize entity ตรง ⇒ ไม่มีทาง mass-assign ผ่าน update เอกสาร/การรับชำระ ·
  ไม่มีผู้ล้างธง (ธงค้างบนใบที่ถูกยกเลิกภายหลัง = ไม่มีผล เพราะตัวหาของกำพร้ากรอง `Status != Voided`)
- ผู้เรียก `ISettlementPostingService` มีแค่ `SettlementController` (ไม่มี job/มือถือ/API v1/LINE) · endpoint `AcknowledgeOrphan` มี `[RejectApiKey]` +
  `[RequirePermission(Settlement.Post)]` (`SettlementController.cs:397–398`) และ service ตรวจสิทธิ์ซ้ำก่อนแตะอะไร (`:832`) · เหตุผลตรวจก่อนสิทธิ์ (`:831`)
- ล็อก: `JobLock.RunExclusiveAsync(_db, SettlementChannelLock.Scope, Part(owner.ChannelId), …, companyId)` (`:849`) = คีย์เดียวกับ `PostAsync` ⇒ รับรู้แทรกกลางการลงบัญชีไม่ได้ ·
  ตัดสินกองใต้ล็อกด้วยตัวแยกตัวเดียวกับด่าน (`:862` → `AckRefusal`) — และที่สำคัญกว่า: ธงถูกอ่านเฉพาะในกิ่ง hard ของ `Split` (`SettlementPostingGuards.cs:471`)
  ที่คำนวณสดทุกครั้งตอนพรีวิว/ลงบัญชี ⇒ race ใด ๆ ระหว่างรับรู้กับการเปลี่ยนสถานะ (ยกเลิก/ปลดล็อกรายงาน) ไม่ทำให้ของที่ยกเลิกได้หลุดผ่าน
- tenant: ทุก query ใหม่มี `CompanyId == companyId` — `OrphanArtifactsAsync` (dead batches · docs · `markedPays` · `payRows`) · `OrphanChildrenAsync`
  (`ChildFactsAsync` ทั้งสอง query · `EtaxInvoices` · `TaxReports` · `Documents` · `WhtCertVoidGuard.CheckDocumentAsync`) · `MemberNamesAsync` (ผ่าน `CompanyUsers.CompanyId`) ·
  `AcknowledgeOrphanAsync` (Payment/Document/SettlementBatches — แม้ `IgnoreQueryFilters` ก็มี `CompanyId`) · เขียน (`:873`, `:881`) · `UnpostBlockersAsync` หัวรอบ
- ความสอดคล้องสองเส้น: เส้นรับรู้ (`excludeBatchId=null`, `fallbackDate=owner.PayoutDate`) กับเส้นลงบัญชี (`batch.Id`, `batch.PayoutDate`) ให้กองเดียวกัน — รอบปัจจุบันไม่ใช่รอบตาย
  จึงไม่ต่าง และ `fallbackDate` ใช้เฉพาะเมื่อหา `DocumentDate` ไม่เจอ (query ไม่กรอง IsDeleted ⇒ เจอเสมอ)
- ป้ายการรับชำระ: เปลี่ยนจาก `Notes.Contains(marker)` เป็น `BatchIdFromPaymentNotes` (ป้าย "ตัวแรก") — ตรงกับ `SettlementArtifactGuard` ที่เส้นยกเลิกใช้ และระบบเขียนป้ายไว้ต้น Notes
  (`SettlementPosting.cs:606` ณ HEAD) ⇒ ไม่ใช่การถดถอย

## 2. ทิศตรงข้าม

**NOT-A-BUG (ตรวจแล้ว)**
- กอง Voidable / NeedsUserAction ยังบล็อก: `Split` ใส่ลง `voidable`/`needs` เสมอ ไม่ว่ามีธงหรือไม่ (ธงแค่เติม `StaleAckNote`) · `SettlementPostingGate.Evaluate` บล็อกทั้งสองกอง
  (`SettlementPosting.cs:431–437` ณ `fa54a422`) · `AckRefusal` ปฏิเสธทั้งสองกองพร้อมทางไปต่อ · Unvoidable ที่ยังไม่รับรู้ = บล็อก (`:439–440`) · รับรู้แล้ว = ไม่บล็อก (`:441–442`)
- ใบที่ภายหลังกลายเป็นยกเลิกได้: การรับรู้เดิมไม่มีผล ยังบล็อก + ข้อความบอก — มีเทสต์ `V2_การรับรู้ค้างบนชิ้นที่ตอนนี้ยกเลิกได้แล้ว_…` ทั้งกอง NeedsUserAction และ Voidable
- เทสต์เดิม 2 ตัวที่กลับความหมาย (`S36_…` ใน S4Tests · `S41_ทิศตรงข้าม_…` ใน S5Tests) — สมเหตุผล: ข้อ 10 สั่งให้ Unvoidable บล็อกจนรับรู้ ·
  ทั้งสองตัวยังล็อกทั้งสองทิศ (ยังไม่รับรู้ = บล็อกพร้อมทางไปต่อ "รับรู้ของกำพร้า" · รับรู้แล้ว = ลงได้) และ S36 ยังล็อกทิศ "ยกเลิกได้ = บล็อก" เดิม
- **"Sent = ยกเลิกไม่ได้" เป็นช่องข้ามด่านไหม** — เชิงหลักการ `VoidDocumentAsync` ยกเลิกใบ Sent ได้จริง (ไม่มีด่านสถานะ Sent · `DocumentService.cs` ส่วนต้นของ `VoidDocumentAsync`)
  ⇒ การจัด Sent เป็น Unvoidable = ให้ "รับรู้แทนการยกเลิกใบลดหนี้" ได้ ซึ่ง**เป็นคำตัดสินเจ้าของ** (ข้อ 10 ระบุ "ใบลดหนี้ยกเลิกไม่ได้ (e-Tax Accepted / ส่งลูกค้าแล้ว)")
  และยังต้องมีสิทธิ์ + เหตุผล + audit ⇒ ไม่ใช่การข้ามด่านโดยพลการ · **แต่ในทางปฏิบัติไม่มีผลเลย ดู V2-C1**

**V2-C1 · P2 · CONFIRMED — ทริกเกอร์ "ส่งลูกค้าแล้ว" ไม่มีวันเป็นจริง**
- `OrphanChildrenAsync` (`SettlementPostingService.cs:801–803`) นับ "ส่งลูกค้าแล้ว" = `d.Status == DocumentStatus.Sent` → `ChildUnvoidableReason` (`SettlementPostingGuards.cs:513`)
- นับการประทับสถานะเอกสารทั้งเรพ: `Status = DocumentStatus.{Approved 10, Draft 13, Overdue 1, Paid 4, Voided 5, WaitingApproval 2}` — **Sent = 0** ·
  ทางอ้อม (`invoice.Status = depSettle.Status` ฯลฯ) คืนค่าจากตัวคำนวณการชำระ/ค่าที่เคยเป็น ไม่สร้าง Sent · `DocumentEmailService.SendDocumentEmailAsync` เขียนแค่
  `DocumentEmailLog.Status` ไม่แตะ `Document.Status` · DTO ไม่มีช่องรับสถานะเอกสาร ⇒ `SentToCustomer` = false เสมอบนข้อมูลที่ระบบนี้สร้าง
- ฉาก: ใบกำพร้ามีใบลดหนี้อ้าง · ใบลดหนี้ถูกอีเมลให้ลูกค้าแล้ว (มี `DocumentEmailLog`) แต่ยังไม่ส่ง e-Tax ⇒ ใบลดหนี้สถานะ Approved ⇒ ตกกอง NeedsUserAction
  ทางไปต่อ "ยกเลิกเอกสารที่อ้างใบนี้ก่อน" ⇒ ผู้ใช้ถูกพาไปยกเลิกใบลดหนี้ที่ลูกค้าถือไว้แล้ว (สิ่งที่ข้อ 10 ตั้งใจเลี่ยง) · ทิศความเสียหายยังเป็นทิศปลอดภัย (บล็อก ไม่ซ้ำเงียบ) จึง P2 ไม่ใช่ P1
- เทสต์ `V2_ใบที่อ้างอยู่ในรายงานล็อก_ส่งลูกค้าแล้ว_…` แถว `sent: true` เขียวเพราะป้อน `SettlementOrphanChild` ตรง — "เทสต์เรียกแค่ helper ≠ ด่านถูกต่อสาย" (F2 ข้อ 2)
- ทางแก้ที่ต้องเลือก (เจ้าของ): (ก) นิยาม "ส่งแล้ว" จากหลักฐานการส่งจริง (`DocumentEmailLog` สำเร็จ · LINE delivery · e-Tax by email) ผ่าน helper ตัวเดียว หรือ
  (ข) ถอด `SentToCustomer` ออกและแก้ข้อความข้อ 10/รายงานทีมให้ตรงว่าตรวจเฉพาะ e-Tax/รายงานล็อก/50 ทวิ · ทั้งสองทางต้องแก้ doc ให้ตรงโค้ด

**V2-P3 · P3 · PLAUSIBLE — การรับรู้ที่ไม่มีผลกลับมามีผลเอง**
- ธงไม่ถูกล้างเมื่อชิ้นย้ายไปกองที่ยกเลิกได้ (`Split` แค่เติมหมายเหตุ) ⇒ ถ้าชิ้นกลับเป็น Unvoidable อีก (รายงานภาษีถูกปลดล็อกแล้วล็อกใหม่ · 50 ทวิ ยกเลิกแล้วออกใหม่ในแบบที่ยื่น)
  การรับรู้เก่าที่ตรวจในบริบทเดิมกลับมาปลดบล็อกเงียบ · ความเสี่ยงต่ำ (ต้องมีคนรับรู้แล้วครั้งหนึ่ง) · ทางเลือก: ผูกการรับรู้กับ "เหตุ" (hash ของ `hardReasons`) แล้วต้องรับรู้ใหม่เมื่อเหตุเปลี่ยน

**V2-P4 · P3 · PLAUSIBLE (ทีมบันทึกเป็นคำถามค้าง 2)** — `OrphanChildrenAsync` ไม่ตามชั้นหลาน: ใบลดหนี้ (ยกเลิกได้ในตัวเอง) ที่มีใบอ้างซึ่งยกเลิกไม่ได้ ⇒ ใบกำพร้ายังอยู่
NeedsUserAction ทางไปต่อทำไม่ได้ = บล็อกถาวรแบบที่ข้อ 10 ต้องการปิด (กรณีหายาก)

## 3. สถานะปลายทางประทับเอง · ผังพัก · audit

**NOT-A-BUG** — ธงประทับเฉพาะเมื่อคนกด (มีสิทธิ์ + เหตุผลบังคับ ≤1000 ตัว) ไม่มี job/ตัวเติมอัตโนมัติ · รับรู้ซ้ำ = ตอบค่าเดิม ไม่เขียนซ้ำ ·
audit ผ่าน `_db.AddChainedAuditLog` (`SettlementPostingService.cs:890`) ในธุรกรรมเดียวกับการประทับ (`:871–897`) · การแก้แถว Document/Payment ยังถูก
`AuditTrailService.CaptureAuditEntries` จับเข้า chain อีกแถวตามปกติของ `SaveChangesAsync` (ไม่ใช่ปัญหา)

**V2-P2 · P3 · PLAUSIBLE (by design) — รับรู้แล้วลงบัญชีได้ในสภาพที่ผังพักคลาด**
- ใบค่าธรรมเนียมกำพร้า = PV จ่ายจากผังพัก (`SettlementDocumentBuilder.FeeDocument` → `PaymentAccountId: clearingAccountId`) ของรอบที่ไม่เคยมี JE รอบโอน (ของกำพร้าเกิดจาก
  "ลงค้างครึ่งทาง → ยกเลิกรอบ" ก่อน `CommitPostedAsync`) ⇒ ผังพักมียอดค้างเท่ายอดใบกำพร้าอยู่แล้ว · รับรู้แล้วลงรอบใหม่ ⇒ ยอดค้างนั้นอยู่ต่อ และถ้ารอบใหม่มีค่าธรรมเนียมเดียวกัน
  = ค่าใช้จ่าย/ภาษีซื้อ/50 ทวิ ซ้ำ · ระบบไม่มีด่านเชิงตัวเลข (เช่นต้องอ้างเลข JE ปรับปรุง/ใบลดหนี้) — พึ่งข้อความเหตุผลเท่านั้น
- นี่คือความเสี่ยงที่ข้อ 10 ยอมรับ ("ธง+เหตุผล+ผู้อนุมัติ = มองเห็นและตรวจย้อนได้") และสถานะก่อนรอบนี้ (S4/S5 เตือนไม่บล็อก) แย่กว่า ⇒ ไม่ใช่การถดถอย
- แต่ส่วน "แสดงในรายงานรอบโอน/ช่องทาง" ของข้อ 10 มีแค่ตารางในพรีวิวของรอบที่เปิดอยู่ (`settlements.html` `renderOrphans`) — ไม่มีรายงานช่องทาง/หน้าเอกสาร/รายงานกระทบยอดผังพักที่เห็นยอดค้างนี้
  (ทีมบันทึกหน้าเอกสารเป็นคำถามค้าง 5) · และเมื่อ `post()` ล้มแล้ว render จากผล 409 ตาราง orphans หายไป (ไม่ส่ง `orphans`)

**V2-P1 · P3 · PLAUSIBLE — การรับรู้ไม่ผูกกับรอบที่ตรวจ**
- คำขอรับรู้ไม่มี `batchId` ของรอบที่ผู้ใช้กำลังดู · audit บันทึกแค่ `ownerBatchId` (รอบตาย) ⇒ ผู้ตรวจย้อนไม่รู้ว่า "ตรวจแล้วไม่ซ้ำ" หมายถึงรอบไหน
- ฉาก: รอบตาย B1 (PayoutRef P1) ทิ้ง PV กำพร้าที่ e-Tax ตอบรับ · รอบถัดไป B2 ถูกบล็อก → รับรู้ "B2 ไม่ซ้ำ" (ถูกต้องสำหรับ B2) · ภายหลังนำเข้าไฟล์ P1 ใหม่
  (`VoidBatchAsync` soft-delete รอบ (`SettlementImportService.Lines.cs:568`) ⇒ unique PayoutRef ว่าง ⇒ นำเข้าได้) — นี่คือกรณี**ซ้ำแท้** แต่ไม่ถูกบล็อกอีกเลย
  (ใบค่าธรรมเนียมไม่มีตัวกันซ้ำอื่น — ข้อความของทีมเอง)
- ระดับ P3 เพราะรอบที่ลงค้างครึ่งทางยกเลิกไม่ได้แล้ว (`LoadEditableBatchAsync`) ⇒ ของกำพร้าใหม่ไม่เกิด เหลือแต่ข้อมูลเก่า · ทางเลือกราคาถูก: ถ้ารอบที่กำลังลงมี PayoutRef
  เท่ารอบเจ้าของของชิ้นที่รับรู้แล้ว ⇒ ยังบล็อก (หรือต้องรับรู้ใหม่) + ส่ง `batchId` ไปเก็บใน audit

## 4. ความเสี่ยงคอมไพล์ (เปิดนิยามจริงทุกตัว)

**NOT-A-BUG (ตรวจแล้ว)**
- tuple ชื่ออนุมาน `(c.ParentId, Why: …)` → `x.ParentId` และ `(p.Id, Owner: …)` → `x.Id`/`x.Owner` — C# ≥ 7.1 · net8 ✔
- `Split`: lambda `a =>` ใน `open.Any/Select` กับ `foreach (var a in open)` เป็น scope พี่น้อง ไม่ซ้อน ⇒ ไม่มี CS0136 · `foreach (var a in g.Where(a => Judged(…)))` รูปเดียวกับโค้ดเดิมที่คอมไพล์อยู่แล้ว ·
  `a.IsPayment ? new List<string>() : x ?? new List<string>()` — `??` ผูกแน่นกว่า `?:` ถูกความหมาย · `triage.Items ?? Array.Empty<…>()` ได้ `IReadOnlyList<T>`
- local function `AckOf` ใน async (non-static จับ `names`) และ `static … Fail(string)` ใน async — อนุญาต · ประกาศหลังตัวแปรที่จับ ✔ · `Fail` ชื่อซ้ำเมธอดคอนโทรลเลอร์ไม่เกี่ยว (คนละคลาส)
- positional record: `SettlementOrphanItem` + property คำนวณ `PileLabel` ✔ · `SettlementOrphanArtifact … with { Ack = ack }` (init) ✔ · `DocumentVoidChildFact` เพิ่มพารามิเตอร์ท้ายมีค่าเริ่มต้น —
  ผู้สร้างเดิมทุกจุด (S4Tests named arg `ByTextReference:` · S5Tests 4 อาร์กิวเมนต์) ยังคอมไพล์ · ไม่มีที่ deconstruct `SettlementOrphanTriageResult` (5 พารามิเตอร์แล้ว)
- overload `SodSelfApproval` 3 กับ 4 อาร์กิวเมนต์ไม่กำกวม · `lines.Select(l => l.CreatedBy)` = `IEnumerable<string?>` (`BaseEntity.CreatedBy` เป็น `string?`)
- EF: ไม่มี method ที่มี optional arg ใน expression (CS0854) — `string.Contains(string)` · `List<Guid>.Contains` · `_db.CompanyUsers.Any(...)` (รูปเดียวกับ `BankService.CompanyMemberNamesAsync`) ·
  `BatchIdFromPaymentNotes`/`BatchIdFromCreator` ถูกเรียกฝั่ง client หลัง `ToListAsync`/`FirstOrDefaultAsync` ✔ · `Substring(18,32)` ใน `parts.Contains` เป็นของเดิม
- สัญลักษณ์ที่อ้าง: `IPermissionService.HasPermissionAsync(Guid, Guid, string)` · `PermissionKeys.LabelOf(string)` · `PermissionKeys.SettlementPost` · `SettlementPermissionScope.Post` (const) ·
  `RejectApiKeyAttribute(string verb)` · `JobLock.RunExclusiveAsync(db, scope, part, Func<Task>, logger, companyId, ct)` · `WhtCertVoidGuard.CheckDocumentAsync(db, companyId, documentId, ct)` ·
  `ThaiDate.ToThaiDisplayString(DateTime)` · `AccountingDbContext.AddChainedAuditLog(AuditLog)` · `AuditAction.Update` · `User.FullName` · `DbSet<CompanyUser> CompanyUsers` —
  มีครบ · `ChildUnvoidableReason`/`AckLabel`/`DocumentVoidPreconditions.Reason` เป็น `internal` และ `Accounting.csproj:82` มี `InternalsVisibleTo Accounting.Tests` ✔
- เทสต์: `GetCustomAttributesData()` + `ConstructorArguments[0]` ใช้ได้กับ `RequirePermissionAttribute(string permissionKey)` (ไม่ใช่ TypeFilter) ✔ ·
  `Assert.Equal(Fee, block.ArtifactId)` อนุมาน `T = Guid?` ✔ · `new AccountingDbContext(options)` มี ctor รูปนี้ และมีเทสต์อื่นใช้ `UseNpgsql("Host=127.0.0.1;Port=1…")` แบบเดียวกัน ✔
- migration ↔ entity: 6 คอลัมน์ `timestamptz NULL` / `uuid NULL` / `text NULL` ตรงชนิด `DateTime?`/`Guid?`/`string?` · ตาราง `Documents`/`Payments` · ไม่มี NOT NULL ✔ ·
  (`timestamptz` ถูกแปลงเป็น `timestamp` ตอนบูตโดย `Program.cs` fixTimestampSql เหมือนคอลัมน์ settlement ทุกตัว — สอดคล้อง `EnableLegacyTimestampBehavior`)

**V2-C3 · P3 · CONFIRMED (latent)** — `AcknowledgeOrphanCoreAsync` เปิด `strategy.ExecuteAsync` (`SettlementPostingService.cs:871` ณ `fa54a422`) โดยไม่ `_db.ChangeTracker.Clear()` ต้น lambda ·
ณ `fa54a422` ยังไม่มีธุรกรรมไหนในไฟล์ทำ แต่ **HEAD ปัจจุบัน (หลัง merge ทีมอื่น) เติม `ChangeTracker.Clear()` ให้ `CommitPostedAsync` และอีก 2 ธุรกรรมแล้ว (review198-C C-20) — ธุรกรรมรับรู้ยังไม่มี**
(ธุรกรรมท้ายของ `UnpostCoreAsync` ก็ยังไม่มีเช่นกัน — นอกขอบเขต diff นี้) ⇒ หลัง merge ธุรกรรมรับรู้หลุดแบบแผน · ถ้าเปิด retry ของ execution strategy วันหนึ่ง รอบที่สองจะมีแถว audit + Document/Payment ที่ค้าง Added/Modified จากรอบแรก
= audit ซ้ำใน chain · วันนี้ไม่เกิด (ไม่ได้เปิด `EnableRetryOnFailure`)

**V2-C4 · P3 · CONFIRMED (ประสิทธิภาพ)** — `markedPays` (`SettlementPostingService.cs:750–752`) กรองแค่ `Notes LIKE '%[SETTLEMENT:%'` ⇒ ดึง Id+Notes ของการรับชำระจากรอบโอน
**ทุกแถวของบริษัท ทุกช่องทาง ทุกเวลา** แล้วค่อยกรองรอบตายฝั่ง client — รันทุก GET พรีวิว + ทุกการลงบัญชี + ทุกการรับรู้ · ร้านที่รับชำระรายออเดอร์ผ่านรอบโอนจะโตเป็นหมื่น–แสนแถว ·
ทางเลือก: ป้ายอยู่ต้น Notes เสมอ ⇒ `p.Notes.Substring(12, 32)` เข้า `parts` ใน SQL แบบเดียวกับฝั่งเอกสาร (หรือทำคอลัมน์ `Payment.SettlementBatchId` ตาม S3-11(5))

**V2-C2 · P3 · CONFIRMED (UI)** — `Split` ส่ง `a.Ack` เข้า `SettlementOrphanItem` ของกอง Voidable/NeedsUserAction ด้วย (`SettlementPostingGuards.cs:459`, `:492`) และหน้าเว็บแสดง
`✅ รับรู้แล้ว` ทุกครั้งที่ `o.ack` มีค่า (`settlements.html:782`) ⇒ คอลัมน์สถานะบอก "รับรู้แล้ว" บนรายการที่การรับรู้ไม่มีผลและกำลังบล็อกอยู่ (ข้อความในคอลัมน์รายละเอียดบอกถูก —
แต่สองคอลัมน์ขัดกัน) · แก้: แสดงป้ายเฉพาะ `pile === 'Unvoidable'` หรือให้เซิร์ฟเวอร์ส่ง `AckEffective`

## 5. S3-11 ส่วนอื่น (ตรวจแล้ว)

- SoD ผู้สร้างบรรทัด: `SettlementLine.CreatedBy = userId.ToString()` ตอนนำเข้า (`SettlementImportService.cs` ตัวสร้างบรรทัด) รูปเดียวกับ `postingUserId.ToString()` ⇒ เทียบได้จริง ·
  ค่าว่าง/บรรทัดระบบไม่นับ · NOT-A-BUG
- `UnpostBlockersAsync` อ่านหัวรอบ: เงื่อนไข `Id/CompanyId/!IsDeleted` + สถานะ เท่าเดิม · ไม่ throw เมื่อช่องทางถูกลบ · NOT-A-BUG
- ถอด `Take(200)`: รอบตายทั้งหมดของช่องทางเข้า `parts` (IN list) — รับได้ · NOT-A-BUG (ดู V2-C4 สำหรับฝั่งการรับชำระ)

## 6. ข้อที่ไม่ได้ตรวจ / ข้อจำกัด

- ไม่ได้รัน `required_call_site_check.py` / `accessibility_check.py` จนจบ (timeout 9–10 นาทีในเครื่องนี้) — ผล "ผ่าน" เป็นคำรายงานของทีม
- ไม่ได้ build/test (ไม่มี SDK) — ความเห็นคอมไพล์ข้างบนมาจากการเปิดนิยามจริงทุกตัว

---

## สถานะการแก้ (รอบ 200 ทีม SF · รายงาน `team-SF.md` · คำตัดสิน DECISIONS ข้อ 25)

| ID | สถานะ | ที่แก้ / เหตุผล |
|---|---|---|
| V2-C1 | ✅ SF-PENDING | `Helpers/DocumentDeliveryEvidence.DeliveredAsync` (บันทึก `DocumentEmailLog` ส่งสำเร็จ — รวม e-Tax by email) แทน `DocumentStatus.Sent` ใน `OrphanChildrenAsync` · checker ห้าม `DocumentStatus.Sent` ในเมธอดนั้น · ส่งทาง LINE ยังไม่มีบันทึก (ข้อจำกัด) · เทสต์ `V2C1_…` |
| V2-C2 | ✅ SF-PENDING | `SettlementOrphanItem.AckEffective` / `AckStatusLabel` (server computes) · settlements.html แสดงป้ายจากเซิร์ฟเวอร์ · เทสต์ `V2C2_…` |
| V2-C3 | ✅ SF-PENDING | `ChangeTracker.Clear()` ต้น lambda ของ `AcknowledgeOrphanCoreAsync` และธุรกรรมท้ายของ `UnpostCoreAsync` (ตรวจแล้ว: ของที่โหลดก่อน lambda เป็น AsNoTracking · ขั้นยกเลิกเอกสาร/การรับชำระ commit ธุรกรรมของตัวเอง) · checker `before` |
| V2-C4 | ✅ SF-PENDING | `OrphanArtifactsAsync` กรองการรับชำระของรอบตายใน SQL (`parts.Contains(p.Notes.Substring(…))` เมื่อป้ายอยู่ต้น Notes · ป้ายที่ไม่อยู่ต้น Notes ยังให้ตัวอ่านป้ายตัดสิน ⇒ ผลเท่าเดิม) |
| V2-P1 | ✅ SF-PENDING | คำขอรับรู้ส่ง `batchId` ของรอบที่ตรวจเทียบ (ช่องทางเดียวกัน · audit `checkedBatchId/checkedPayoutRef`) · `SettlementOrphanTriage.AckCovers`: รอบที่ใช้เลขรอบโอนเดียวกับรอบเจ้าของและนำเข้าหลังการรับรู้ ⇒ การรับรู้เดิมไม่ครอบ ต้องรับรู้ใหม่ · เทสต์ `V2P1_…` สองทิศ |
| V2-P2 | ✅ บางส่วน SF-PENDING | ตารางของกำพร้าไม่หายเมื่อกดลงบัญชีแล้วถูกบล็อก (409) · 📋 รายงานระดับช่องทาง/หน้าเอกสารที่เห็นยอดค้างผังพักของใบกำพร้าที่รับรู้แล้ว — ต้องมี endpoint/หน้าใหม่ (คำถามค้าง 5 ของทีม V2) |
| V2-P3 | 📋 | การรับรู้ผูกกับ "เหตุ" (hash ของเหตุยกเลิกไม่ได้) — ต้องเก็บคอลัมน์เพิ่ม · ความเสี่ยงต่ำ (ต้องมีคนรับรู้แล้วครั้งหนึ่ง) |
| V2-P4 | 📋 | ตามชั้นหลาน — คำถามค้าง 2 ของทีม V2 (กรณีหายาก) |
