# E — คุณภาพวิศวกรรม · เทสต์ · เอกสาร (รอบ 241–249)

> ตรวจอ่านอย่างเดียว 2026-11-06 · ช่วง `git log --since="3 days ago"` (63 คอมมิต · base เทียบ = `c63789e9` · 483 ไฟล์ / +52,889 −3,381 ·
> เฉพาะ `.cs` 164 ไฟล์ +18,690) · ไม่มี .NET SDK ในเครื่อง ⇒ ใช้ผล CI เป็น compiler · **ระหว่างตรวจ branch ขยับจาก `43cfdba1` → `fe9036f7` → merge `5a13c6e9` (PR #29)** ผลตรวจอ้างถึงสถานะล่าสุดเว้นแต่ระบุ
> กรอบ: CLAUDE.md กฎเหล็ก #4 (F2 หลักการ 10 ข้อ · F3 checklist · F4 ข้อห้าม · G testing mandate · H กันถดถอย)

## 0. สถานะ CI ที่ใช้เป็นหลักฐาน

| head | job build | job Db (`Category=Db`) | job static | job `dotnet test` (pure) |
| --- | --- | --- | --- | --- |
| `7cb6bd08` | ✅ | ❌ 4 (asset — ด่านผลต่างมีเครื่องหมาย) | ❌ (write_permission_gate ฟ้องผิด DryRun ×3) | **skipped** |
| `ca10f623` | ✅ | ❌ | ✅ | skipped |
| `43cfdba1` | ❌ **CS1501** `DocumentService.cs(11191,37)` `CostSourceVoidBlockAsync` ไม่มี overload 5 อาร์กิวเมนต์ | ❌ (build ล้มก่อน) | ✅ | skipped |
| `fe9036f7` (run 42193745876) | ✅ | ✅ | ✅ | **skipped** |

⇒ ณ `fe9036f7` ทั้ง build + Db test + static เขียว · **แต่ job เทสต์ pure-function ไม่เคยรันบน branch นี้เลย** (ดู H-1)

---

## 1. ผลตรวจเรียงตามความรุนแรง

### H-1 · เทสต์ pure-function ของรอบ 241–249 (และทุกรอบก่อนหน้าบน branch นี้) **ไม่เคยถูกรันโดย CI**
- **ที่:** `.github/workflows/ci.yml:79-83` — job `dotnet test` มี `if: github.event_name != 'push' || startsWith(github.ref, 'refs/heads/main') || … master`
  · trigger `pull_request: branches: [main, master]` (บรรทัด 6-7) แต่ PR #19–#29 ทั้งหมด base = `claude/erp-system-review-team-660mev` (ตรวจ `gh api pulls/28,29`) ⇒ event `pull_request` ไม่เคยยิง
  · run แบบ `pull_request` ล่าสุด = **2026-07-21** (`d674dcf6`)
- **ผล:** เทสต์ที่คอมมิตสัปดาห์นี้ ~3,400 บรรทัด / 235 เทสต์ (`AssetCostSourceRulesTests` 42 · `PaymentQrRound246Tests` 30 · `ContactRecipientRuleTests` 27 · `AutopilotRound248Tests` 24 ·
  `EtaxByEmailRound244Tests` 23 · `DocumentAutoSendRound241Tests` 21 · `PaymentNotificationRound249Tests` 17 · `DocumentCascadePurgePolicyTests` 15 · `RecurringRunOutcomeTests` 14 ·
  `WhtCreditWriteOffTests` 12 · `DocumentConversionHeaderTotalRound242Tests` 10 · `OcrVatRateTableTests` ฯลฯ) **ถูกคอมไพล์** (job build รวม Accounting.Tests) **แต่ไม่เคยรัน** —
  "เทสต์ทิศตรงข้าม" ที่ทุกคอมมิตอ้างใน F3 ข้อ 9 จึงยังเป็นเพียงโค้ดที่คอมไพล์ผ่าน · ข้อความใน CLAUDE.md "CI บน `claude/**` คือ compiler ตัวแรก" จริงเฉพาะ compile ไม่ใช่ test
- **แก้:** ตัด `if:` ของ job `dotnet test` ออก (หรือเปลี่ยนเป็น `if: always()`) · หรือย้าย `--filter "Category!=Db"` ไปรันใน job `db-test` ต่อท้าย (มี PostgreSQL อยู่แล้ว แต่ pure test ไม่ต้องใช้) · อัปเดต CLAUDE.md "วิธีทำงาน" ให้ตรงความจริง · **effort: 5 นาที + 1 รอบ CI** (คาดว่ามีเทสต์เก่าที่ล้มสะสมตั้งแต่ ก.ค. — ต้องเผื่อเวลาแก้)
- **หลักการที่ผิด:** F2 ข้อ 2 "มี ≠ ถูกเรียก" (เทสต์ที่ไม่มี runner = ไม่มีเทสต์) · G "control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control"

### H-2 · `doc_commit_sha_check` ไม่ได้อยู่ใน `check_all.sh` ทั้งที่ CLAUDE.md F3 ข้อ 4 บอกว่าอยู่ — รันเดี่ยวแล้ว **exit 1**
- **ที่:** `tools/check_all.sh` (`grep -c doc_commit_sha` = 0) · CLAUDE.md §F3 ข้อ 4 "doc sha อยู่บน branch (doc_commit_sha_check)"
- **ฟ้อง 3 จุด:** `erp-review/2026-10-14/PAYMENT_CHANNEL_AUDIT.md:375,377` (`2560776c`, `c382580d` — pre-existing · blame `1b7f7618`) · **`erp-review/2026-11-06/RETENTION_LAW_CHECK.md:190`** `0a829110` — **ใหม่สัปดาห์นี้** (`e550918c`) คัดลอก sha ตายมาจาก `review193-r2-sec-tax.md:54/62` (ที่นั่นก็ตายอยู่แล้ว — ไม่มีใครเคยฟ้องเพราะ checker ไม่ได้ถูกเรียก)
- **แก้:** ต่อสาย `doc_commit_sha_check.py` เข้า `check_all.sh` (ratchet baseline 2 จุดเดิมถ้าหา sha จริงไม่ได้) · หา sha จริงของ R2-C1/C2/C4 รอบ 193 ด้วย `git log -S` แล้วแทนทั้ง 2 ไฟล์ · **effort: 15 นาที**
- **หลักการ:** F2 ข้อ 2 (checker ที่ไม่มีใครเรียก = ไม่มี) · F2 ข้อ 10 (`grep` ว่า "ไม่มี" ไม่ใช่หลักฐาน — ที่นี่กลับกัน: doc บอกว่า "มี" แต่ไม่มี)

### M-1 · `_Last verified … commit <เติมในคอมมิตตามหลัง>` ค้าง 24 บรรทัด — คอมมิต "ตามหลัง" 2 ตัวเติมแค่ 2 จุด
- `f990b16e` / `43cfdba1` อ้างว่า "เติม sha 369bc403/c314266c ใน DOCUMENT_FLOW/CHANGELOG" — เติมจริงเฉพาะบรรทัดระดับ § ของสินทรัพย์หลายใบ (DOCUMENT_FLOW §6 + CHANGELOG L6540) ·
  บรรทัดท้ายไฟล์ `_Last verified` ของรอบ 240–249 **ทั้ง 8 บรรทัด** ยังเป็น placeholder (DOCUMENT_FLOW.md:7764–7771) · CHANGELOG 12 บรรทัด · ACCOUNT_STRUCTURE 4 บรรทัด (เก่ากว่า) — รายการ + sha ที่ควรใส่อยู่ใน §6.2
- **แก้:** คอมมิต doc-only 1 ตัว (ไม่รัน CI ตาม `paths-ignore` — ต้องบอกผู้ใช้ว่าไม่มีรอบให้รอ) · **effort: 10 นาที** · กลไกกันซ้ำ: ให้ `doc_commit_sha_check` (H-2) นับ placeholder ที่อายุเกิน 1 คอมมิตเป็น fail

### M-2 · ข้อความคอมมิต `369bc403` ถูกทับด้วยข้อความของ `ecc6710f` (shared scratch) — ประวัติ git บอกเรื่องผิด
- `369bc403` = "รอบ 249: แจ้งเตือนรับ/จ่ายชำระ…" แต่เนื้อหา = **สินทรัพย์หลายใบ** (22 ไฟล์ · `AssetCostSourceRulesTests` +333 · `FixedAssetService.CostSources.cs` ฯลฯ) · `ecc6710f` (17 ไฟล์) คือแจ้งเตือนตัวจริง
  · CHANGELOG L6540 จดไว้แล้ว ("ข้อความคอมมิตนี้…") แต่ `git log --grep` / `git log -S` / blame จะพาคนอ่านไปผิดทางตลอดไป · ซ้ำด้วย `7cb6bd08` "สองทีม stage แยก hunk" ทำบล็อกใน `api.js`/CHANGELOG สลับที่
- **แก้:** amend ไม่ได้ (push แล้ว) ⇒ (1) ใส่ `git notes add 369bc403` (ไม่เปลี่ยน sha) · (2) จดเป็น defect class ใหม่ใน `docs/lessons/process-docs.md` (ดู §6.3) · (3) กติกา: สอง session ห้ามใช้ไฟล์ข้อความคอมมิตใน scratchpad ชื่อเดียวกัน — ใช้ `git commit -F <sha-เฉพาะ>.txt` หรือ `-m` ตรง · **effort: 10 นาที**

### M-3 · `PaymentQrMigration.MoveLegacyTemplatePromptPayAsync` — งานย้ายข้อมูลครั้งเดียวที่ **ไม่มีเทสต์เลย** + คีย์ล็อกคนละสูตรกับงานคู่แฝด
- **ที่:** `Accounting/Helpers/PaymentQrMigration.cs:63-121` · เรียกจาก `Program.cs:2141` ตอนบูต · `required_call_site_check` 0 กติกา · เทสต์ 0 (เทียบ `ContactNotifyEndpointMigration` มี Db test + กติกา R245g/R245j)
- **ปัญหา 2 ชั้น:** (ก) เขียน `BankAccount.PromptPayId` ของลูกค้าจริงตอนบูต (persist ⇒ F2 ข้อ 9) โดยไม่มีเทสต์ "ย้ายถูกบริษัท · ข้ามบริษัทที่มี 2 บัญชี · บูตซ้ำ no-op · บริษัทที่บัญชีมีเลขอยู่แล้วไม่ถูกทับ" ·
  (ข) ล็อกด้วย `pg_advisory_xact_lock(hashtext({markerKey}))` (`:68`) ขณะที่ `ContactNotifyEndpointMigration.cs` ใช้ `AdvisoryLockKey.For(AdvisoryLockKey.OneShotDataFix, MarkerKey)` — สูตรคีย์สองสำเนา (F2 ข้อ 4) · `advisory_lock_key_check` ผ่าน 0 จุดเพราะไม่รู้จัก `hashtext`
- **แก้:** เพิ่ม Db test 4 ทิศ (ลอก `ContactNotifyEndpointRound245DbTests` เคส R245g) · เปลี่ยนคีย์เป็น `AdvisoryLockKey.For(OneShotDataFix, MarkerKey)` · สอน `advisory_lock_key_check` ให้ฟ้อง `hashtext(` ใน `pg_advisory_xact_lock` · **effort: 1 ชม.**

### M-4 · `DocumentPurgeDbTests.ProductionFkShapeAsync` แก้ schema ของฐานเทสต์ที่ใช้ร่วมกัน ขณะ xunit รันคลาสขนานกัน
- **ที่:** `Accounting.Tests/Db/DocumentPurgeDbTests.cs:89-112` (`ALTER TABLE … DROP CONSTRAINT` / `ADD CONSTRAINT … ON DELETE RESTRICT` บน `DocumentSignatures`/`DocumentApprovals`) ·
  `DbTestDatabase.cs` = ฐานเดียว `EnsureCreated` ครั้งเดียว · ไม่มี `xunit.runner.json`/`[CollectionBehavior]` ⇒ ค่าตั้งต้น xunit รันแต่ละ test class ขนาน
- **เสี่ยง:** `ALTER TABLE` ถือ ACCESS EXCLUSIVE ⇒ คลาสอื่นที่สร้าง `DocumentApprovals`/`DocumentSignatures` ผ่าน service พร้อมกัน (การยอมรับใบเสนอราคารอบ 239 · ลายเซ็น/sign request รอบ 240 — เขียนผ่าน `ExecuteUpdate`/service ไม่ใช่ `Add` ตรง) รอหรือ deadlock เป็นครั้งคราว · ตอนนี้เขียว (run 42193745876) แต่เป็น flaky class ที่มีเหตุผลเชิงโครงสร้าง · และเมื่อ constraint ถูกเปลี่ยนเป็น RESTRICT แล้ว เทสต์อื่นที่พึ่ง CASCADE ของ EF (ลบ Document แล้วคาดว่าลูกหาย) จะพฤติกรรมเปลี่ยนตามลำดับการรัน
- **แก้:** ย้าย `ProductionFkShapeAsync` ไปทำครั้งเดียวใน `DbTestDatabase` static init (ฐานเทสต์ = รูป production เสมอ ซึ่งถูกต้องกว่าอยู่แล้ว) · หรือ `[Collection("purge-schema")]` + `DisableParallelization` · **effort: 30 นาที**

### M-5 · baseline `write_permission_gate` ค้าง 4 แถวที่ด่านมีแล้ว (ratchet เดินทางเดียว — เตือนอย่างเดียว ไม่ fail)
- `tools/write_permission_gate_baseline.txt:462` `ScheduledReportController.SendNow` · `:479` `StampDutyController.MarkPaid` · `:486` `SubscriptionController.SubmitPayment` · `:534` `WithholdingTaxCertController.Void`
  — pre-existing (ไฟล์ baseline แตะล่าสุด `26ec41b4` สำหรับแถวอื่น) · **แก้:** ลบ 4 แถว · effort 2 นาที · เหตุที่ต้องทำ: baseline ที่ใหญ่เกินจริง = ด่านที่หลวมโดยไม่ตั้งใจ (F2 ข้อ 6)

### M-6 · `AUTO_SEND_WHT_DESIGN.md` §1.3 G-8 "ไม่มีการเชื่อมคู่ค้าข้ามผู้เช่า (NextAcc↔NextAcc)" **ผิดข้อเท็จจริง**
- **ที่:** `erp-review/2026-11-06/AUTO_SEND_WHT_DESIGN.md:84` · ตาราง §1.1 (บรรทัด 27–49) ไม่มีแถว `CrossTenantWorkflow`
- **ของจริง:** `Models/Entities/CrossTenantWorkflow.cs:22` `class TradingPartnership : BaseEntity` + `TradingPartnershipStatus` + `CrossTenantWorkflowService` + `CrossTenantWorkflowController` + migration ใน `DatabaseMigrationHelper`
- **ผล:** แผนรอบ 246–247 "สร้างการเชื่อมคู่ค้า" จะสร้างสำเนาที่สองของสิ่งที่มีอยู่ (F2 ข้อ 4 · F4 ข้อ 7) · **แก้:** เพิ่มแถว §1.1 + แก้ G-8 เป็น "มี `TradingPartnership` แต่ยังไม่ต่อกับการส่งเอกสาร/50 ทวิ" + ระบุใน DECISIONS ว่าต้องต่อยอดจากของเดิม · effort 20 นาที (ต้องอ่าน `CrossTenantWorkflowService` ก่อนว่าครอบอะไร)

### L-1 · ด่านที่ยังไม่มีเทสต์ตรงตัว / ไม่มีทิศตรงข้าม (รายละเอียดใน matrix §2)
- `AutoApprovePolicy.Releases` — ไม่มีเทสต์เรียกตรง (ผ่าน `ApprovalAcknowledgement.cs:105` เท่านั้น · ครอบโดย `AutopilotRound248Tests` ทางอ้อม)
- `WhtWriteOffJournalLock.EnsureNotWriteOffEntriesAsync` — เทสต์เฉพาะ `BlockMessage` (ข้อความ) · ตัวด่านจริงไม่มีเทสต์ (กติกา R243a8 ล็อกจุดเรียกไว้ · ไม่มี Db test ที่ยิง `ReverseJournalEntryAsync` บน JE ตัดสูญ)
- `EtaxByEmailFormat.UatWarning` — 0 เทสต์ · `EtaxDeliveryRouter.Decide` 3 เทสต์ pos แต่ไม่มี neg (เส้น "ไม่ใช่ e-Tax by Email ⇒ ส่งปกติ")
- `PostPaymentAutomationPolicy.Effective` / `IdempotencyKey` — มี pos ไม่มี neg · `DocumentPayLinkPolicy.Allowed`/`OnlineOnly` — pos เท่านั้น
- `PaymentQrPolicy.Pick` / `PromptPayUsable` — ด้านเดียว
- รอบ 247 (`RecurringRunOutcome`) — 0 กติกา call-site (ยอมรับได้: เป็น UX ไม่ใช่ด่านเงิน) แต่ `Describe/Steps` ถูกเทสต์ผ่าน `Build` เท่านั้น
- **แก้:** เพิ่มเทสต์ทิศที่ขาด 8 ตัว (~1 ชม.) — แต่ไม่มีประโยชน์จนกว่า H-1 จะแก้

### L-2 · `WhtCreditService.SettleYearEndAsync` ล็อกหลายแถวตามลำดับที่ฐานคืน ไม่ได้เรียง id
- `WhtCreditService.cs:937-938` `foreach (var w in rows) … ExpireCoreAsync(… w.Id …)` → `LockCreditAsync` ต่อแถว · `rows` มาจาก `Where(ids.Contains)` ไม่มี `OrderBy` ⇒ ปิดปีสองคำขอพร้อมกัน (หรือปิดปี + `UndoWriteOffAsync` หลายแถว) deadlock ได้ในทางทฤษฎี (PostgreSQL จะ abort ฝั่งหนึ่งด้วย 40P01 — ไม่เสียข้อมูล แต่ 500)
- **แก้:** `.OrderBy(w => w.Id)` ก่อนวน · effort 1 นาที · เทียบ `DocumentService.cs:14143` ที่เรียงแล้ว (ฝ่ายค้าน S4 รอบ 245) — รูปแบบเดียวกันควรเป็นมาตรฐาน

### L-3 · `FixedAssetService.LockCostSourcesIfLinkedAsync` ตรวจ "ผูกอยู่ไหม" ก่อนถือคีย์ (TOCTOU แคบ)
- `FixedAssetService.CostSources.cs:198-203` อ่าน `FixedAssetCostSources` แบบไม่ล็อก แล้วถือคีย์เฉพาะถ้า `linked` · ผู้เรียก (`DocumentService.cs:9912` ยกเลิก · `:10964` ลบถาวร) ล็อกแถวเอกสารหลังจากนั้น ·
  ฝั่งผูก (`LinkLinesCoreAsync`) ถือคีย์ก่อนแล้ว `FOR UPDATE` เอกสาร ⇒ ถ้า attach commit ระหว่าง "ตรวจ linked" กับ "FOR UPDATE" ของ void: void ไม่ได้ถือคีย์แต่ถือแถวเอกสาร · attach จะรอแถวเอกสาร · void ตรวจ `CostSourceVoidBlockAsync` (เห็นเฉพาะที่ commit แล้ว) — ถ้า attach commit ก่อน void ถึงจุดตรวจ ด่านจับได้ · ถ้ายังไม่ commit void ผ่าน แล้ว attach ผูกใบที่ถูกยกเลิก (ต้องดูว่า `LinkLinesCoreAsync` ตรวจสถานะใบซ้ำหลังได้ล็อกไหม — ตรวจจาก diff: มี `LoadCostLineFactsAsync` หลังล็อก ⇒ น่าจะเห็น Voided) ⇒ **ความเสี่ยงต่ำ แต่ควรปิดให้สนิท**
- **แก้:** ถือคีย์ `AssetCostSource` เสมอเมื่อชนิดเอกสารอยู่ใน `AssetCostSourceRules.KindFor` ≠ null (ไม่ต้องตรวจก่อน) — ค่าใช้จ่ายคือ serialize void/purge ของใบซื้อต่อบริษัท ซึ่งถูกอยู่แล้ว · effort 10 นาที + เทสต์ Db 1 ตัว

### L-4 · เทสต์ใส่รหัสกฎเป็น string literal แทนค่าคงที่
- `RecurringAutopilotRound248DbTests.cs:365` `"BUYER-86/4"` · `AutopilotRound248Tests` มี `"AR-OVERDUE"` literal (`:133`) ทั้งที่ `ArOverdueApprovalWarning.Code` มี ⇒ เปลี่ยนรหัสแล้วเทสต์ยังเขียวแบบผิด ๆ (F2 ข้อ 5 "สำเนากติกา") · effort 5 นาที

### L-5 · คำเตือนคอมไพล์ใหม่ `CS8604` `DocumentController.cs(1530,96)` — `documentIds` อาจ null ส่งเข้า `Task<(List<Docu…>` (ลบถาวรหลายใบ) · ไม่ fail build แต่เป็น nullable contract ที่ยังไม่ปิด · effort 2 นาที

---

## 2. Matrix — ด่านใหม่ ↔ เทสต์สองทิศ ↔ กติกา call-site

สัญลักษณ์: **P** = เทสต์ที่ใส่บั๊กแล้วจับได้ (positive) · **N** = เทสต์ใบถูกไม่ถูกแตะ (negative/ทิศตรงข้าม) · ตัวเลข = จำนวนเทสต์ที่เรียกด่านนั้นตรง ๆ ใน `[Fact]` body · กติกา = id ใน `tools/required_call_site_check.py` (ผ่านทั้ง 3,320 กติกา + 207 negative) · **เทสต์ทุกแถวที่ไม่ใช่ Db ยังไม่เคยรันใน CI (H-1)**

| รอบ | ด่าน (OWNER helper · จุด throw) | ไฟล์:บรรทัด | P | N | กติกา call-site | verdict |
| --- | --- | --- | --- | --- | --- | --- |
| 241 | `DocumentPayLinkPolicy.IssueRefusal/EmailBlock/LinkUsable/TokenMatches/CompanyAvailable/UnavailableReason` → `DocumentPayLinkService.cs:106,108,329` · `EmailScheduleService.cs:959,962` (`PAYLINK-241`) | `Helpers/DocumentPayLinkPolicy.cs` | 1–2 ต่อฟังก์ชัน (`DocumentAutoSendRound241Tests`) | 1–2 | `_R241_` 14 กติกา (DocumentPaymentHandler · DocumentController.CreatePayment · EmailScheduleWorker · EmailScheduleService ×2 …) | ✅ ครบ · `Allowed`/`OnlineOnly` มี P ไม่มี N (L-1) |
| 241 | `DocumentPayLinkPolicy.WhtForPayment/ExpectedWht/BuyerWithholds` (ยอดสุทธิหลังหัก) | เดียวกัน | 1 | 1 | R241 (`HandleSucceededAsync` must `WhtForPayment(` · `BuyerWithholds(` · `ExpectedWht(`) | ✅ |
| 241 | `PostPaymentAutomationPolicy.Decide` → `PostPaymentAutomationService.cs:136` (`POSTPAY-241`) | `Helpers/PostPaymentAutomationPolicy.cs` | 6 | 3 | R241 (`DocumentController.CreatePayment` must `_postPay.EnqueueAsync(`) + Db `DocumentAutoSendRound241DbTests` | ✅ · `Effective`/`IdempotencyKey` ไม่มี N |
| 242 เอกสาร | `DocumentCascadePurgePolicy.Decide` → `DocumentService.cs:10943,10972,10975,11044` (`RuleDescendantEffective`/`RuleRetention`/`RuleReasonRequired`/`RuleEtaxReached`/`RuleFilingLocked`) | `Helpers/DocumentCascadePurgePolicy.cs` | 15 (`DocumentCascadePurgePolicyTests`) + Db 5 | 12 | R242a–g (`PurgeOneDocumentCoreAsync` ×2 · `PurgeDocumentAsync#3` ×3 · `BuildPurgePlanAsync` · `LoadConversionSummariesAsync`) + กติกา `Judge@1271` | ✅ ดีที่สุดของรอบ |
| 242 เอกสาร | `DocumentPurgeOrder.DeleteDocumentChildrenAsync/DeletePaymentChildrenAsync` (ลำดับ FK) | `Helpers/DocumentPurgeOrder.cs` | Db `ความครบ_ทุกFK…` (คิวรี pg_constraint เทียบ `Covers`) | Db เจ้าของ_ลบใบ… | R242a/R242e + กติกา 1297–1299 | ✅ (เทสต์ invariant แบบที่ build-errors.md แนะนำ) |
| 242 เอกสาร | `DocumentConversionProgress.HeaderTotalConvertWarning` → `DocumentService.cs:13463` | `Helpers/DocumentConversionProgress.cs` | 1 (`DocumentConversionHeaderTotalRound242Tests` 10 เทสต์รวม) | 1 (+4 ทิศตรงข้ามชื่อ "ทิศตรงข้าม_…") | กติกา 1274–1277 | ✅ |
| 242 OCR | `OcrLineVatMarks` (ตาราง ภ.พ. แบบอัตรานำหน้า) | `Helpers/OcrLineVatMarks.cs` | 10 ไฟล์เทสต์กระดาษจริง (`OcrVatRateTableTests` ฯลฯ) | ใช่ (ใบเดิมไม่ถูกแตะ — golden) | 5 กติกา | ✅ (ผ่าน `ocr_helper_test_check`) |
| 243a | `WhtCreditWriteOff.Decide` → `WhtCreditService.cs:580` · `:567,779,784` (`RD-65ter(6bis)-WHT-WRITEOFF`) | `Helpers/WhtCreditWriteOff.cs` | `WhtCreditWriteOffTests` 12 (ผ่าน helper `D()`) + Db `WhtCreditWriteOffDbTests` | ✅ ("กดซ้ำคืนผลเดิม" · "สถานะที่ตัดสูญไม่ได้") | R243a1–a7 (`ExpireCoreAsync` ×4 · `MarkReceivedAsync` · `SyncWhtCreditReceivedAsync` · `ReverseWriteOffJournalCoreAsync`) | ✅ |
| 243a | `WhtWriteOffJournalLock.EnsureNotWriteOffEntriesAsync` → `WhtWriteOffJournalLock.cs:36` (`GL-WHT-WRITEOFF-ENTRY` 409) | `Helpers/WhtWriteOffJournalLock.cs` | 1 (`BlockMessage` ข้อความเท่านั้น) | 0 | R243a8 (`ReverseJournalEntryAsync`) | ⚠️ ตัวด่านจริงไม่มีเทสต์ (L-1) |
| 243a | `UndoWriteOffAsync` / `DeleteAsync` ด่าน `:344,406,408,414,760,763` (`RD-50bis-WHT-WRITEOFF-REVERSE` · `RD-WHT-WRITEOFF-UNDO-SOURCE`) | `WhtCreditService.cs` | Db `WhtCreditWriteOffDbTests` | Db | R243a3 | ✅ |
| 244 | `EtaxByEmailSenderGate.Refusal/From` → `DocumentEmailService.cs:253,492,562` (`ETAX-EMAIL-SENDER-UNVERIFIED`) | `Helpers/EtaxByEmailSenderGate.cs` | 3 | 2 | R244b (+R240i) | ✅ |
| 244 | `EtaxByEmailFormat.AttachmentProblem/Subject/Recipients/RevenueWarning/ModeWarning` → `:253` (`ETAX-EMAIL-FORMAT`) · `RecordEtdaStampAsync :576-590` (`ETAX-EMAIL-RESEND`) | `Helpers/EtaxByEmailFormat.cs` | 6/4/1/1/1 | 3/2/1/1/1 | R244a,c,g,i (`SendEtaxByEmailAsync` · `RecordEtdaStampAsync`) + Db `EtaxByEmailRound244DbTests` | ✅ · `UatWarning` 0 เทสต์ · `Stage` ไม่มี N |
| 244 | `EtaxDeliveryRouter.Decide` → `SendDocumentRoutedAsync` | `Helpers/EtaxDeliveryRouter.cs` | 3 | 0 | R244d | ⚠️ ไม่มีทิศ "ไม่ใช่ e-Tax ⇒ เส้นเดิม" |
| 245 L1 | `ContactRecipientRule.Resolve/Status/ValidateNewEmail/MergeUnlinks` → `ContactNotifyEndpointService.cs:33-225` (`NOTIFY-CONTACT-*` ×8 รหัส) | `Helpers/ContactRecipientRule.cs` | 12 (`Status`) · 1 · 2 (+`Resolve` ผ่าน helper `R()`) | 8 · 1 · 2 | R245a–n (14 กติกา: `ResolveAsync` · `LoadFactsAsync` · `MergeContactsAsync` · `SendExpiryRemindersAsync` · `MigrateCoreAsync` …) + Db `ContactNotifyEndpointRound245DbTests` (514 บรรทัด) | ✅ |
| 245 L1 | `ContactNotifyEndpointMigration.MigrateLegacyLineUserIdsAsync` (one-shot) | `Helpers/ContactNotifyEndpointMigration.cs` | Db (6 การเรียก) | Db (บูตซ้ำ no-op) | R245g, R245j | ✅ |
| 246 | `PaymentQrPolicy.SourceProblem/PickAccount/BankTextMismatch/ShowBankQr/ShowPayLinkQr/EmbedAmount` → `PaymentQrService.cs:160,163,194,209,214` (`PAYQR-246`) | `Helpers/PaymentQrPolicy.cs` | 1/3/1/3/1/1 | 1/3/1/2/1/1 | R246a–h (`GenerateDocumentPdfAsync` · `ComposeFooter` · `UploadPaymentQrImage` ×2 · `ResolveCoreAsync` ×2 · `UpdateBankAccountAsync` · `UploadImageAsync`) + Db `PaymentQrRound246DbTests` | ✅ · `Pick`/`PromptPayUsable` ด้านเดียว |
| 246 | `PaymentQrImageValidator.Validate` → `:160` | `Helpers/PaymentQrImageValidator.cs` | 4 | 1 | R246c, R246f | ✅ |
| 246 | `PaymentQrMigration.MoveLegacyTemplatePromptPayAsync` (one-shot) | `Helpers/PaymentQrMigration.cs` | **0** | **0** | **0** | ❌ **M-3** |
| 247 | `RecurringRunOutcome.Build/SettingWarning` (UX เท่านั้น) | `Helpers/RecurringRunOutcome.cs` | 14 (`RecurringRunOutcomeTests`) | ✅ ("Opposite_*" 3 ตัว) | 0 (ไม่ใช่ด่านเงิน) | ✅ ยอมรับได้ |
| 248 | `AutoApprovePolicy.Validate/MatchesBinding/Effective/NeverPassable/Released/BindingHash` → `RecurringAutopilotService.cs:355` (`APPROVE-UNATTENDED-POLICY`) · `ApprovalAcknowledgement.cs:105` | `Helpers/AutoApprovePolicy.cs` | 2/1/1/1/1/1 | 1/1/1/1/1/1 | R248b,e,f,j,k (`ApproveDocumentAsync#2` · `SetApprovePolicy` · `CanStillApproveAsync` · `LoadAsync` · `PreviewApprovalWarningsForUnsavedAsync`) + Db 10 เทสต์ (`RecurringAutopilotRound248DbTests`) | ✅ · `Releases` ไม่มีเทสต์ตรง (L-1) |
| 248 | `AutopilotReadiness.Evaluate` → `DocumentService.cs:6575,6577` (`AUTOPILOT-READINESS`) · `DenyAutopilotReadAsync` (ca10f623) | `Helpers/AutopilotReadiness.cs` | 6 | 1 | กติกา 15116 (`Evaluate(`) + 15149–15151 (`DenyAutopilotReadAsync` ×3) + Db "ไม่เขียนอะไรเลย" (นับแถวก่อน-หลัง 5 ตาราง) | ✅ |
| 248 | `AutopilotSettingsResolver.ValidateMode/ValidateChannels/Resolve` → `RecurringAutopilotService.cs:76,395,397` · `RecurringTransactionService.cs:49,51` (`AUTOPILOT-PAYMODE`) | `Helpers/AutopilotSettingsResolver.cs` | 1/1/3 | 1/1/3 | R248c,d,h (`BuildAsync` ×3) · R248g (`EnsureLinkAsync`) · R248l (`RecheckDocumentAtSendAsync`) | ✅ |
| 248 ฝ่ายค้าน | `[RejectApiKey]` บน approve-policy ×2 + ช่องทางรับเงินผู้ติดต่อ | `RecurringController.cs` | — | — | `owner_action_wiring_check` R248-K1..K3 (self-test ผ่าน) · `required_call_site_check` 0 (ถูกต้อง — เป็น attribute ไม่ใช่ call) | ✅ |
| 249 แจ้งเตือน | `PaymentNotificationText.Build` (คีย์ `payment_made`) · `NotificationEventSplit.MigrationSql` | `Helpers/PaymentNotificationText.cs` · `Helpers/NotificationEventSplit.cs` | 3 (+Db 3) | 3 | R249a–f (`CreatePaymentAsync` · `LineText` · `DispatchAsync` ×2 · `LoadPaymentNotificationFactsAsync` · `DispatchEmailAsync`) + กติกา 10930/10951 | ✅ |
| 249 สินทรัพย์ | `AssetCostSourceRules.VoidBlock/LineChangeBlock/AllocationProblem/ReleaseProblem/PlaceInServiceProblem/SourceBalanceProblem/OverRegistrationIncrease/OrphanIncrease/AdjustmentProblem/LineProblem/ContractPriceWarning/CanAbsorbLegacyDraft/TransferLegs/ExcessOverGl` → `DocumentService.cs:8325,8891,9152,9889,11192` (`TFRS-NPAE-10-COST-SOURCES` 409) · `FixedAssetService.CostSources.cs` | `Helpers/AssetCostSourceRules.cs` | 42 เทสต์รวม (`AssetCostSourceRulesTests`) — ทุกฟังก์ชัน ≥1 P | ทุกฟังก์ชัน ≥1 N ยกเว้น `PlaceInServiceProblem` (0 N) | `_R249FA_` **35 กติกา** (`LoadCostLineFactsAsync` · `LinkLinesCoreAsync` · `PlaceInServiceAsync` · `ApplyPostCapitalizationCostAsync` · `LinkExcessOverGlAsync` · `PostAssetTransferJournalAsync` · `ReleaseCostSourceAsync` · `ReleaseCostSourcesForVoidAsync` · `CostSourceVoidBlockAsync` · `AssetGlBackingAsync` · `CreateFromSourcesAsync` + ผู้เรียกใน DocumentService) + Db 17 เทสต์ (`AssetMultiDocumentDbTests`) | ✅ (หลัง c314266c: ด่านผลต่างมีเครื่องหมาย → นับเฉพาะส่วนเกิน + เทสต์ทิศตรงข้าม 2 ตัว `OverRegistrationIncrease` N=2) |
| 249 สินทรัพย์ | `LockCostSourceWritersAsync`/`LockCostSourcesIfLinkedAsync` (ลำดับคีย์ → เอกสาร → สินทรัพย์) | `FixedAssetService.CostSources.cs:191-203` | — (ไม่มีเทสต์ concurrency) | — | กติกา 15312–15337 (7+1 จุด) | ⚠️ ล็อกจุดเรียกแล้ว · พฤติกรรมพร้อมกันไม่มีเทสต์ (L-3) |

**สรุป:** ด่านที่ไม่มีทั้งเทสต์และกติกา = **1** (`PaymentQrMigration`) · ด่านที่มีกติกาแต่ตัวด่านไม่มีเทสต์ = **1** (`WhtWriteOffJournalLock.EnsureNotWriteOffEntriesAsync`) · ขาดทิศตรงข้าม = 8 ฟังก์ชัน (L-1) · ที่เหลือครบสองทิศ + กติกา — **แต่ทั้งหมดยกเว้น Db ยังไม่เคยรัน (H-1)**

---

## 3. Db tests ที่ไม่เคยรันในเครื่อง — สแกนกับดัก seed ที่เคยทำ CI แดง

ข้อเท็จจริงก่อน: run 42193745876 (`fe9036f7`) job Db **ผ่านทั้งหมด** ⇒ ไม่มี "likely failure" ณ head · ตารางนี้คือ (ก) ยืนยันว่าแต่ละกับดักถูกเลี่ยงอย่างไร (ข) ความเปราะที่ยังเหลือ

| ไฟล์ | TaxId NOT NULL | FileStorage Locator | `ChangeTracker.Clear` หลัง `ExecuteUpdate` | นับ audit กรอง `Action` | JSON-escaped ไทย | สถานะคืนเงิน legacy | `DateTime.Min/Max` | งวดปิด | ผังบัญชีครบสำหรับอนุมัติ | ความเปราะที่เหลือ |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `RecurringAutopilotRound248DbTests` (10) | ✅ ใส่ `0105556000025`/`…017`/`…033` ทุกตัว | — | ไม่ใช้ `ExecuteUpdate` | `:155,177,180` เทียบ `NewValues.Contains(รหัส ASCII)` — ปลอดภัยเพราะรหัสไม่ใช่ไทย | `:175` เทียบ `doc.InternalNotes` (คอลัมน์ตรง ไม่ใช่ JSON) ✅ | — | — | ไม่ seed งวด ✅ | seed 4 ผัง (111 · 11310 · 41000 · 21911) + revenue line ⇒ อนุมัติ Invoice ผ่าน (CI ยืนยัน) | `:70` ใบค้างชำระ `AgingDays=45` เป็นค่าคงที่ไม่ได้คำนวณจาก DueDate — ถ้าด่าน AR-OVERDUE เปลี่ยนไปคำนวณเอง เทสต์ยังผ่านแบบผิด ๆ · `:365` literal `"BUYER-86/4"` (L-4) · `:306` เปลี่ยนเป็น `TaxInvoice` แล้วอาศัย "ที่อยู่ผู้ซื้อว่าง" เป็นตัวหยุด — ถ้า seed ที่อยู่ว่างถูก auto-fill ในอนาคต (`DbdIdentityGuard`) เทสต์ LOW7 จะหลุด |
| `PaymentNotificationRound249DbTests` (3) | ✅ `0105556000249` | — | — | — | เทียบข้อความกลุ่ม LINE (string ตรง ไม่ใช่ JSON) ✅ | — | — | — | ไม่เกี่ยว | `:34` token ถูก Protect ด้วย `SecretProtector("db-test-key-249")` ตัวเดียวกับ engine ✅ · `:88` regex ลิงก์พึ่ง `App:BaseUrl` ที่ส่งผ่าน `IConfiguration` ใหม่ (ctor optional) — ถ้าระบบจริงไม่ได้ตั้ง base URL — ตรวจแล้ว `Helpers/NotificationLink.cs:28` คืน `null` (ไม่ใส่ลิงก์เลย ไม่ใช่ path สัมพัทธ์ — F2 ข้อ 3) ผ่านตัวตัดสิน `SystemBaseUrl.IsConfigured` ตัวเดียว ✅ |
| `AssetMultiDocumentDbTests` (17) | ✅ `0105556000025` ซ้ำทุกบริษัท (ไม่มี unique index ⇒ ผ่าน · ถ้าเพิ่ม unique ภายหลังจะล้มทั้ง 17) | — | — | ไม่นับ audit | ไม่เทียบ JSON | — | — | ไม่ seed งวด · วันที่ทั้งหมด `Y()` = ปีก่อน ⇒ ไม่ชน "วันพร้อมใช้ > วันนี้" ✅ (M1 ของ c314266c) | seed 13 ผังครบ (12280/12260/18360/56160/11810/…) ✅ | `:130` `PostJe` หา `DocumentDate` จาก `db.Documents.Local` — ถ้าเอกสารไม่ได้อยู่ใน tracker (เช่น seed ผ่าน context อื่น) จะใช้ `UtcNow` เงียบ ๆ ⇒ ด่านยอดคงเหลือ "ณ วันลงรายการ" เปลี่ยนความหมายโดยไม่มีใครรู้ · `:191` คาดเดือน 4 ของปีก่อน — ขึ้นกับ `AvailableDate()` = 15 มี.ค. ⇒ ถูกเสมอ |
| `DocumentPurgeDbTests` (6) | ✅ `0105556000007` | — | ไม่ใช้ `ExecuteUpdate` | ✅ `:159,232` กรอง `Action == AuditAction.Delete` (แก้ CI 244 ข้อ 5) | ✅ `:235` `JsonDocument.Parse(...).GetProperty("reason")` | — | — | ใบ `Voided` วันนี้ ⇒ ชนด่าน retention ตามที่เทสต์ต้องการ ✅ | ไม่อนุมัติ (seed Approved ตรง) | **M-4** ALTER TABLE บนฐานร่วมขณะขนาน · `:115` `Svc()` ส่ง `null!` 7 ตัว — ถ้า `PurgeDocumentAsync` เริ่มแตะ `_tax`/`_email` จะ NRE (เทสต์ N7 ส่ง `taxService` จริงแล้ว ดี) |
| `QuotationAcceptanceRound239DbTests` | — | ✅ 3 จุด (`Locator`) | ✅ 1 (`ChangeTracker.Clear` หลัง `ExecuteUpdate` ×3) | ✅ 3 | 2 จุด `NewValues.Contains` — ต้องเป็น ASCII (ตรวจ: ใช้ `"Approve"`/รหัส) | — | — | — | — | ไม่ได้แก้สัปดาห์นี้ (แก้ 41b04e0a/a2fa3b60/2b5ac414 รอบ 239) — คงที่ |
| `WhtCreditWriteOffDbTests` | — | — | — | 3 จุด `AuditLogs` ไม่กรอง `Action` (แต่ใช้ `Any`/`Contains` รหัส ASCII ไม่ใช่ `Single`) ✅ | 3 จุด `Contains` รหัส `RD-…` ASCII ✅ | — | — | 2 จุด seed งวดปิดโดยตั้งใจ (ด่าน "งวดปิด") ✅ | 8 จุด seed ผัง 11910/58010/21810/11920 ✅ | — |
| `EtaxByEmailRound244DbTests` | — | — | ✅ 4 | 2 จุด ไม่กรอง — ใช้ `Any` ✅ | 2 จุด — คอมมิต 1abfda1c "ถอด JSON ก่อนเทียบ" ✅ | — | — | — | — | — |
| `ContactNotifyEndpointRound245DbTests` | — | — | ✅ 1 | ✅ 1 กรอง | 2 จุด (R245g แก้แล้ว 1abfda1c) | — | — | — | — | `:486` xUnit2029 warning (`Assert.Empty` บน `Count`) — cosmetic |
| `PaymentQrRound246DbTests` | — | ✅ 3 (`Locator`) | ✅ 5 | 2 จุด `Any` ✅ | 2 จุด — รหัส `PAYQR-246` ASCII ✅ | — | — | — | 2 ผัง | — |
| `DocumentAutoSendRound241DbTests` | — | — | ✅ 8 (`ExecuteUpdate` 8 จุด) | ไม่นับ audit | — | — | — | — | 1 ผัง | `FakeNotify : INotificationEngine` ครบสมาชิก (1 เมธอด) ✅ · `IEmailSenderFactory` ปลอมคืน `PlatformFromAddress()` string (แก้ CI 244 ข้อ 6–8) ✅ |

**สรุปข้อ 2:** ไม่พบกับดักที่ค้าง — ทุกตัวที่เคยทำ CI แดง (TaxId · Locator · Clear หลัง ExecuteUpdate · กรอง Action · JSON ไทย) ถูกเลี่ยงแล้วและ CI ยืนยัน · ความเปราะใหม่ที่ควรปิด: **M-4** (schema ร่วม) · `AssetMultiDocumentDbTests:130` fallback `UtcNow` เงียบ · TaxId ซ้ำข้ามบริษัท · literal รหัสกฎ (L-4)

---

## 4. Compile-risk scan ของ diff 3 วัน (ไม่มี compiler ในเครื่อง — ยืนยันด้วย CI `fe9036f7` build ✅)

| ประเภทความเสี่ยง | วิธีตรวจ | ผล |
| --- | --- | --- |
| วงเล็บ/ปีกกาไม่สมดุล | นับหลังถอด string/char/comment ทั้ง 5 ไฟล์ใหม่ที่นับดิบไม่ลงตัว (`AutopilotRound248Tests` · `ContactRecipientRuleTests` · `ContactNotifyEndpointRound245DbTests` · `DocumentAutoSendRound241Tests` · `RecurringRunOutcome.cs`) | ✅ สมดุลทุกไฟล์ (ตัวที่เคยพัง `RecurringRunOutcome` CS1026 แก้แล้ว `b2e96b45`) |
| tuple element ชื่อ `Rest` (CS8126) | regex บน diff | ✅ 0 (แก้เป็น `Remainder` ที่ `db2a391e`) |
| namespace shadowing (`Accounting.Helpers.X` ใต้ `Accounting.Models.*`) | `namespace_shadow_check` + grep diff | ✅ 0 · ที่เหลือใน `BankAccount.cs:33` / `RecurringTransaction.cs:87,92` / `DocumentDtos.cs:6` (`using PurgeNodeAction = …`) ไม่ถูกบัง (checker ยืนยัน · `b2e96b45` ใส่ `global::` ที่จุดเสี่ยง) |
| `using` ขาดสำหรับ type ใหม่ | `using_check` (6,874 ชนิด) | ✅ 0 |
| สมาชิกซ้ำข้าม partial (`DocumentService` · `FixedAssetService` + `.CostSources`) | CI build | ✅ (CS0111 จะฟ้อง) |
| `??` บน string ไม่ nullable ใน DTO | grep diff ไฟล์ DTO | ✅ 0 (แก้ `1abfda1c`) · `dto_nullable_contract_check` 830 DTO 0 ขัด |
| `Protect()` ลงช่องที่ไม่ลงทะเบียน | `secret_registry_check` | ✅ 28/29 ช่อง (`ContactNotifyEndpoint.LineUserId` ลงทะเบียนใน `SecretReprotectJob` ที่ `1abfda1c`/`a16fad65`) · `DocumentPayLinkService.cs:224` `PayLinkTokenProtected` อยู่ในทะเบียน |
| async ไม่ await | heuristic บน diff | ✅ 0 (CS4014 เป็น warning — build log ไม่มี) |
| interface เพิ่มสมาชิกแต่ implementer/test double ไม่ครบ | grep `class X : I…` ทั้ง src+tests | `IDocumentService` (+5: `PreviewApprovalWarningsForUnsavedAsync` · `PurgeDocumentAsync` · `PreviewPurgeAsync` · `ApproveDocumentAsync` +param) · `IFixedAssetService` (+6) · `IDocumentEmailService` (+4) · `IRecurringTransactionService` (+1) · `INextAccLineConfig` (+1) — **ทุกตัวมี implementer เดียว** (ไม่มี test fake; เทสต์ใช้ `F4Proxy<>`/`AllowAll<>` dynamic proxy ⇒ ไม่พังเมื่อเพิ่มสมาชิก) · `INotificationEngine` มี fake `FakeNotify` ใน `DocumentAutoSendRound241DbTests:40` — ครบ (interface มี `DispatchAsync` ตัวเดียว) ✅ |
| DI ctor เปลี่ยน (`NotificationEngine(…, IConfiguration? config = null)`) vs `new` ในเทสต์ | grep | `NotificationLineGroupRound232DbTests:93` ส่ง 5 อาร์กิวเมนต์ (ใช้ default) · `PaymentNotificationRound249DbTests:50` ส่ง 6 ✅ |
| **CS1501 จริงที่หลุด** | CI `43cfdba1` | `DocumentService.cs:11191` เรียก `CostSourceVoidBlockAsync(…, "ลบถาวร")` 5 อาร์กิวเมนต์ ก่อน `FixedAssetService.CostSources.cs:336` จะมี `string verb = "ยกเลิก"` — **แก้แล้ว `fe9036f7`** (defect class: แก้ผู้เรียกก่อนแก้นิยาม/ไม่ได้รัน `callers.py` หลังเปลี่ยน signature) |
| warning ใหม่ | build log | `CS8604 DocumentController.cs(1530,96)` `documentIds` nullable (L-5) |

---

## 5. ตาราง checker (รันเดี่ยวทุกตัวในเครื่องนี้ · 2026-11-06)

| checker | exit | ผล | จำแนก |
| --- | --- | --- | --- |
| `namespace_shadow_check` | 0 | 0 จุด (สแกน 64,707 ไฟล์ — ตัวเลขสูงผิดปกติ: น่าจะกวาด `bin/obj`/สำเนา — ไม่กระทบผล แต่ช้า) | ✅ |
| `onclick_js_string_check` | 0 | 0 จุด / 201 html | ✅ |
| `dead_helper_check` | 0 | ไม่มี dead helper ใหม่ · 4 แถวกลับมามีผู้เรียก (`PndTextFileFormat.DetailRow/SplitName` · `Pp30SalesClassifier.ShouldReport` · `ThaiPitCalculator.ExpenseDeduction`) — ตัดจาก baseline ได้ | ✅ (ratchet to-do) |
| `using_check` | 0 | 0 จุด | ✅ |
| `dto_nullable_contract_check` | 0 | 0 ขัด / 830 DTO | ✅ |
| `raw_fetch_api_check` | 0 | baseline 165 คงที่ | ✅ |
| `write_permission_gate_check` | 0 | ผ่าน · ⚠️ 4 แถว baseline ค้าง (M-5) · ไม่ฟ้อง DryRun ×3 แล้ว (ca10f623 เพิ่ม `READ_ONLY_POSTS_IN_FILE`) | pre-existing drift |
| `settings_reader_check` | 0 | baseline 42 แถวรอตัดสิน | ✅ |
| `secret_registry_check` | 0 | 28/29 ช่อง | ✅ |
| `contact_picker_check` | 0 | ผ่าน (ฟ้องผิด `fContactPaymentMode` แก้โดยเปลี่ยน id เป็น `fCustPayMode` ที่ 29d6730f — **checker ยังจับด้วยชื่อ id ไม่ใช่ความหมาย** ⇒ ฟ้องผิดตัวถัดไปจะเกิดอีก) | checker false positive (เลี่ยงแล้ว ไม่ได้แก้ที่ต้นเหตุ) |
| `owner_action_wiring_check --self-test` | 0 | ถอดด่าน 103 แถว = ฟ้องครบ · กลายพันธุ์ 11 เคส | ✅ |
| `deep_link_param_check` | 0 | 294 ลิงก์ · 0 param ที่ปลายทางไม่อ่าน | ✅ |
| `doc_commit_sha_check` | **1** | 3 sha ไม่มีใน repo (H-2) · **ไม่อยู่ใน `check_all.sh`** | real (doc) + checker ไม่ถูกเรียก |
| `test_inventory --check` | 0 | TEST_PLAN §0 ตรงกับ Accounting.Tests | ✅ |
| `required_call_site_check` (1,700 s budget · จบใน <10 นาที) | 0 | 3,320 กติกา + 207 negative ผ่าน | ✅ |
| `advisory_lock_key_check` (เพิ่มเอง) | 0 | 0 จุด — แต่ไม่รู้จัก `hashtext(` (M-3) | checker blind spot |

---

## 6. เอกสาร

### 6.1 §/บรรทัดที่ควรมี — มีครบ
DOCUMENT_FLOW: §2.7 Recurring (2208 · Autopilot) · §2.12g ส่งเอกสารอัตโนมัติ (2877) · §2.12h ช่องทางแจ้งเตือนผู้ติดต่อ (2912) · §2.12i QR (2949) · §6 สินทรัพย์ 1 ชิ้น ↔ หลายใบ (3354) · §6.3 retention/ลบถาวร (6337) · ACCOUNT_STRUCTURE: แถวส่งเอกสารอัตโนมัติ (100) · §3.1e-2 ผัง 11910/58010 (252) · TEST_PLAN §0 ตรงเทสต์จริง · CHANGELOG มีบล็อกทุกรอบ 240–249 (รวมฝ่ายค้าน)

### 6.2 รายการ placeholder ที่ต้องเติม (sha จาก `git blame` ของบรรทัดนั้น + คอมมิตแก้ CI ที่ตามมา)

| ไฟล์:บรรทัด | รอบ | sha ที่ควรใส่ |
| --- | --- | --- |
| DOCUMENT_FLOW.md:7764 | 240 | `61f6b0f6` |
| DOCUMENT_FLOW.md:7765 | 243a | `26ec41b4` (+ `db2a391e` CS8126) |
| DOCUMENT_FLOW.md:7766 | 242 OCR | `26ec41b4` |
| DOCUMENT_FLOW.md:7767 | 242 เอกสาร | `26ec41b4` |
| DOCUMENT_FLOW.md:7768 | 246 QR | `6a8e7ab3` (+ `1abfda1c` · `a16fad65`) |
| DOCUMENT_FLOW.md:7769 | 241 | `26ec41b4` |
| DOCUMENT_FLOW.md:7770 | 248 | `8304ce95` (+ `29d6730f` ฝ่ายค้าน · `ca10f623`) |
| DOCUMENT_FLOW.md:7771 | 249 แจ้งเตือน | `ecc6710f` (**ไม่ใช่** `369bc403` — ดู M-2) |
| CHANGELOG.md:6387 | 240 ฝ่ายค้าน | `61f6b0f6` |
| CHANGELOG.md:6411 | 242 เอกสาร | `26ec41b4` |
| CHANGELOG.md:6423 | 241 | `26ec41b4` |
| CHANGELOG.md:6432 | 243a | `26ec41b4` |
| CHANGELOG.md:6450 | 244 | `6a8e7ab3` |
| CHANGELOG.md:6458 | 244 ฝ่ายค้าน | `6a8e7ab3` |
| CHANGELOG.md:6471 | 245 L1 | `6a8e7ab3` (+ `a16fad65` · `1abfda1c`) |
| CHANGELOG.md:6481 | 246 QR | `6a8e7ab3` |
| CHANGELOG.md:6492 | 247 | `ca8acbe8` (+ `b2e96b45`) |
| CHANGELOG.md:6507 | 248 | `8304ce95` |
| CHANGELOG.md:6521 | 249 แจ้งเตือน | `ecc6710f` |
| CHANGELOG.md:6527 | 248 ฝ่ายค้าน | `29d6730f` (+ `ca10f623`) — blame ชี้ `7cb6bd08` เพราะถูกย้าย hunk |
| ACCOUNT_STRUCTURE.md:1420 | ทีม FS | `d6d83995` |
| ACCOUNT_STRUCTURE.md:1422 | 230 Omise | `ab876762` |
| ACCOUNT_STRUCTURE.md:1424 · 1478 | 232 Notify · Public checkout | `b4780a33` |
| ACCOUNT_STRUCTURE.md:1208 · 1211 · 1397 · 1398 | rev 46/49/50 (ต.ค.) | หา `git log -S` — เก่า นอกขอบเขตแต่ควรปิดพร้อมกัน |
| erp-review/2026-11-06/RETENTION_LAW_CHECK.md:190 | sha ตาย `0a829110` | หา sha จริงของ R2-C4 รอบ 193 (`git log -S"R2-C4" -- erp-review/2026-09-24/`) |

### 6.3 ข้อความล้าสมัย / ต้องแก้
- `AUTO_SEND_WHT_DESIGN.md:84` G-8 + ตาราง §1.1 — M-6
- CLAUDE.md §F3 ข้อ 4 "doc sha อยู่บน branch (doc_commit_sha_check)" + §วิธีทำงาน "CI บน `claude/**` คือ compiler ตัวแรก" — ให้ตรงความจริง (H-1 · H-2) หรือแก้ให้จริงแล้วคง doc
- `docs/lessons/checker-writing.md` §"รายการ checker ทั้งหมด (57)" — ตรวจว่ามี `doc_commit_sha_check` และ `advisory_lock_key_check` ในรายการและระบุว่าตัวไหนไม่อยู่ใน `check_all.sh`

### 6.4 `docs/lessons/` ที่ควร append (defect class ใหม่สัปดาห์นี้ — ยังไม่มีใน 8 ไฟล์ · grep `369bc403`/`hunk`/`scratch` = 0)
1. **process-docs.md** — "ข้อความคอมมิตถูกทับจาก scratch ร่วม" (`369bc403` ได้ข้อความของ `ecc6710f`) ⇒ กติกา: ไฟล์ข้อความคอมมิตตั้งชื่อตามเนื้องาน/sha ชั่วคราว · ตรวจ `git log -1 --stat` ทันทีหลัง commit ว่าหัวข้อตรงไฟล์ · แก้หลัง push ด้วย `git notes` ไม่ amend
2. **process-docs.md** — "สองทีม stage แยก hunk ในไฟล์เดียว" (`7cb6bd08`: คอมเมนต์ใน `api.js`/บล็อก CHANGELOG ไปอยู่ผิดที่ · blame ของ CHANGELOG L6527 ชี้คอมมิตที่ย้าย ไม่ใช่คอมมิตที่เขียน) ⇒ กติกา: ไฟล์ append-only (CHANGELOG/TEST_PLAN §0) ให้ทีมเดียวเขียนต่อท้าย · อีกทีม rebase ก่อน stage · หลัง `git add -p` ต้อง `git diff --cached` ดูบริบทรอบ hunk
3. **checker-writing.md** — "checker ฟ้องผิด 2 ตัวในสัปดาห์เดียว" (`contact_picker_check` R2 จับ `<select id=fContact…>` ด้วยชื่อ id · `write_permission_gate_check` จับ POST ที่ไม่เขียนเพราะ verb) ⇒ บทเรียน: ตัดสินด้วย *พฤติกรรม* (มี `.Add/.Remove/SaveChanges`/`ExecuteUpdate` ในเส้น) ไม่ใช่ชื่อ · ทางแก้ที่ถูกคือเพิ่ม negative case ใน self-test ของ checker ไม่ใช่เปลี่ยน id หน้าเว็บให้หลบ (29d6730f เลี่ยง · ca10f623 แก้ถูกทาง — เพิ่ม allowlist พร้อมเหตุผล + self-test 5 ทิศ)
4. **money-tax.md** — "ด่านเทียบผลต่างแบบมีเครื่องหมาย" (`c314266c`: ทะเบียน − GL = −400,000 ผูกจนเป็น 0 ถูกนับเป็น "เกิน 400,000" ⇒ 4 Db test ล้ม) ⇒ กฎ: ด่าน "ห้ามเกิน/ห้ามขาด" ต้องนับ `max(0, after) − max(0, before)` ฝั่งเดียว · เทสต์ต้องมีเคส "ข้ามศูนย์" (−400k → +20k) และ "จากศูนย์" (0 → +20k) · ตัวตั้งตัวเดียว (`OverRegistrationIncrease`/`OrphanIncrease`)
5. **build-errors.md** — "เปลี่ยน signature แล้วแก้ผู้เรียกก่อนนิยาม" (`43cfdba1` CS1501 `CostSourceVoidBlockAsync` 5 อาร์กิวเมนต์) ⇒ `python3 tools/callers.py CostSourceVoidBlockAsync` หลังแก้ ไม่ใช่ก่อนอย่างเดียว · ค่า default ของพารามิเตอร์ใหม่ต้องอยู่ในคอมมิตเดียวกับผู้เรียกตัวแรก
6. **process-docs.md** — "คอมมิต 'ตามหลัง' เติม sha ไม่ครบ" (`f990b16e`/`43cfdba1` เติม 2 จาก 10 จุด) ⇒ ใช้ `grep -n 'เติมในคอมมิตตามหลัง' DOCUMENT_FLOW.md CHANGELOG.md ACCOUNT_STRUCTURE.md TEST_PLAN.md` เป็น checklist ก่อนคอมมิตตามหลัง · และให้ `doc_commit_sha_check` นับ placeholder เป็น fail เมื่อบรรทัดนั้นอายุ > 1 คอมมิต

---

## 7. Multi-instance (#4 D)

| จุด | คีย์/กลไก | ถูกต้อง? | หมายเหตุ |
| --- | --- | --- | --- |
| `ContactNotifyEndpointMigration.MigrateCoreAsync` (one-shot ตอนบูต) | `pg_advisory_xact_lock(AdvisoryLockKey.For(OneShotDataFix, MarkerKey))` + marker `OneShotDataFixes` ตรวจ/เขียนในธุรกรรมเดียว · ล้ม = rollback ไม่มี marker ⇒ บูตถัดไปทำใหม่ | ✅ idempotent · หลาย instance บูตพร้อมกัน serialize ที่ลock | ตาราง `OneShotDataFixes` สร้างใน `Statements()` ของตัวเองผ่าน `ApplyMissingColumns` ก่อน ✅ |
| `PaymentQrMigration.MoveCoreAsync` | `pg_advisory_xact_lock(hashtext(markerKey))` + marker เดียวกัน | ✅ ทำงานถูก · ❌ สูตรคีย์ต่างจากข้างบน (M-3) · พึ่งตาราง `OneShotDataFixes` ที่ migration ของรอบ 245 สร้าง — ถ้าวันหนึ่งถอดรอบ 245 ออก จะล้มเงียบใน `catch` ที่ `Program.cs:2147` (log Error · ไม่หยุดบูต) | ย้าย `CREATE TABLE OneShotDataFixes` ไปเป็นของกลาง (`DatabaseMigrationHelper`) |
| `NotificationEventSplit.MigrationSql` (คัดลอก `payment_received` → `payment_made`) | SQL `INSERT … SELECT … WHERE NOT EXISTS … ON CONFLICT DO NOTHING` ใน `ApplyMissingColumns` (ลำดับหลัง `EnableLineGroup`) | ✅ idempotent ตามเทสต์ ค_ (รัน 2 ครั้ง) · ไม่มีล็อกเฉพาะแต่ `ON CONFLICT` กันได้ | ต้องมี unique index `(CompanyId, EventKey, RecipientRole)` — เทสต์ `Assert.Single` ยืนยันทางอ้อม |
| `ContactNotifyEndpointService.LockContactAsync` | `For(companyId, ContactNotifyEndpoint, contactId)` ต่อผู้ติดต่อ | ✅ | `DocumentService.MergeContactsAsync:14141-14143` ถือคีย์เดียวกันทุก id **เรียง id** ✅ (S4 รอบ 245) |
| `FixedAssetService.LockCostSourceWritersAsync` | `For(companyId, AssetCostSource, "")` คีย์เดียวต่อบริษัท → `FOR UPDATE` เอกสารเรียง id → สินทรัพย์ | ✅ ลำดับเดียวทุกเส้น (กติกา R249FA ล็อก 8 จุด) | L-3: ฝั่ง void/purge ตรวจ "linked" ก่อนถือคีย์ |
| `WhtCreditService.LockCreditAsync` | `For(companyId, "wht-credit-writeoff", id)` — **string literal** ไม่ใช่ `AdvisoryLockKey` const (แถวเดียวในโค้ดที่ใช้ scope นี้ ⇒ ยังไม่มีสำเนาที่สอง แต่ผิดแบบแผน) | ✅ ต่อแถว · L-2 ปิดปีหลายแถวไม่เรียง | เพิ่ม `AdvisoryLockKey.WhtCreditWriteOff` const |
| `PostPaymentAutomationService.ProcessDueAsync` (เรียกจาก `EmailScheduleWorker.cs:46`) | `FOR UPDATE SKIP LOCKED` จองแถว + unique ต่อการชำระ | ✅ หลายเครื่องไม่ทำซ้ำ | worker เดิมไม่มี `JobLock` แต่ SKIP LOCKED พอ — ไม่ใช่ BackgroundService ใหม่ ⇒ ไม่ผิด F4 ข้อ 6 |
| `RecurringAutopilotService.DryRun/Readiness` | ไม่เขียน (Db test นับแถว 5 ตาราง) · `DenyAutopilotReadAsync` | ✅ | — |
| BackgroundService ใหม่ | `git diff` `AddHostedService`/`: BackgroundService` = 0 | ✅ ไม่มี | — |

---

## 8. ตรวจแล้วไม่ใช่บั๊ก (ห้ามรายงานซ้ำ)

1. **`BankAccount.cs:33` / `RecurringTransaction.cs:87,92` อ้าง `Accounting.Helpers.*` ใต้ `Accounting.Models.Entities`** — `namespace_shadow_check` ยืนยันไม่ถูกบัง (ไม่มี namespace `Accounting.Models.Entities.Accounting`) · `DocumentDtos.cs:6` ใช้ `using … = global`-safe alias
2. **`EmailScheduleWorker` เรียก `postPay.ProcessDueAsync` โดยไม่มี `JobLock`** — `SKIP LOCKED` ใน `PostPaymentAutomationService.cs:110` กันซ้ำข้ามเครื่องแล้ว · worker ไม่ใช่ของใหม่
3. **QuestPDF ไม่มี QR?** — มี: `PdfGenerationService.DocumentRenderer.cs:1056-1057` `ComposePaymentQr(col, paymentQr, L)` คู่กับ HTML `PdfGenerationService.PaymentQr.cs` · ข้อความผ่าน `DocumentLabels` (`payqr_*` 4 คีย์) ⇒ ไม่ drift (#4 A)
4. **`NotificationEngine` ctor เพิ่ม `IConfiguration? config = null`** — DI resolve `IConfiguration` ได้เสมอ · เทสต์เก่า (232) ส่ง 5 อาร์กิวเมนต์ยังคอมไพล์ (CI build ✅)
5. **`FakeNotify : INotificationEngine` ใน `DocumentAutoSendRound241DbTests`** — interface มีสมาชิกเดียว `DispatchAsync` ⇒ ครบ
6. **4 Db test asset ที่ล้มใน `7cb6bd08`** — สาเหตุ = ด่านผลต่างมีเครื่องหมาย (ไม่ใช่ seed) · แก้ที่ด่าน `c314266c` + เทสต์ทิศตรงข้าม 2 ตัว · `fe9036f7` เขียว
7. **`write_permission_gate_check` ฟ้อง `DryRun/DryRunDraft/ReadinessDraft`** — POST ที่ไม่เขียน (Db test ล็อกว่าไม่เขียน) · allowlist พร้อมเหตุผล + self-test (`ca10f623`) = วิธีแก้ที่ถูก
8. **`PaymentQrPolicy.SourceProblem` / `AutopilotSettingsResolver.ValidateMode` throw ก่อน `KeyNotFound`** — ลำดับตรวจค่าคำขอก่อนโหลดแถวเป็นเจตนา (ปฏิเสธค่าที่ไม่รู้จัก ไม่ตกค่าตั้งต้นเงียบ — F2 ข้อ 3) · เทสต์ "ค่าจากคำขอที่ไม่รู้จัก_ถูกปฏิเสธ" ล็อกไว้
9. **`AssetMultiDocumentDbTests` ใช้ `TaxId` เดียวกันทุกบริษัท** — ไม่มี unique index บน `Companies.TaxId` (CI ผ่าน 17/17) · เป็นความเปราะในอนาคต ไม่ใช่บั๊กวันนี้
10. **`advisory_lock_key_check` 0 จุดทั้งที่ `hashtext` ไม่ deterministic ข้ามเครื่อง?** — `hashtext` ของ PostgreSQL deterministic ต่อ cluster (ค่าเดียวกันทุก connection) ⇒ ล็อกทำงานถูก · ปัญหาคือสำเนาสูตร (M-3) ไม่ใช่ความถูกต้อง
11. **`DocumentService.cs:11191` 5 อาร์กิวเมนต์** — แก้แล้ว `fe9036f7` (ไม่ต้องรายงานซ้ำ) · เหลือเฉพาะบทเรียน (§6.4 ข้อ 5)

---

## 9. สรุปสั้นสำหรับเจ้าของ

- โค้ดรอบ 241–249 **คอมไพล์ผ่านและ Db test ผ่าน** ที่ `fe9036f7` · ด่านเงิน/ภาษีใหม่ 19 กลุ่มมีเทสต์สองทิศ + กติกา call-site ครบ 17 กลุ่ม
- **แต่** เทสต์ pure-function ทั้งหมด (235 เทสต์ของสัปดาห์นี้ + ทุกรอบตั้งแต่ ก.ค.) **ไม่เคยถูกรัน** เพราะ job ถูก skip บน `claude/**` และ PR ไม่ได้เข้า main (H-1) — แก้ 1 บรรทัดใน `ci.yml` แล้วเผื่อเวลาแก้เทสต์ที่ล้มสะสม
- `doc_commit_sha_check` หลุดจาก `check_all.sh` (H-2) ⇒ placeholder sha ค้าง 24 บรรทัด + sha ตาย 3 จุดไม่มีใครเห็น
- งานย้ายข้อมูลตอนบูต `PaymentQrMigration` เขียนข้อมูลลูกค้าโดยไม่มีเทสต์ (M-3) · ฐานเทสต์ร่วมถูก ALTER ขณะขนาน (M-4)
- ลำดับทำ: H-1 → H-2 → M-1 (doc-only) → M-3 → M-4 → M-6 → ที่เหลือ
