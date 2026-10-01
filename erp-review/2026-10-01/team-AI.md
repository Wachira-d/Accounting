# รอบ 201 — ทีม AI (AI / ธนาคาร) · BACKLOG §1.6

> HEAD เริ่ม `5eed54bf` · worktree ของทีม · **ยังไม่ได้คอมไพล์ในเครื่องนี้ (ไม่มี .NET SDK) — CI คือ compiler ตัวแรก**
> ลำดับทำตาม §5: A-AI7 → A-AI1 → A-AI3 → A-AI6 → A-AI4 → A-AI2 → A-AI5 → A-AI8 · หมวด C/B: ไม่มีงานที่มอบให้ทีม AI (BRIEF รอบ 201)

## สถานะรายข้อ

| ID | สถานะ | ที่แก้ (file:เมธอด) | เทสต์ / ด่าน |
|---|---|---|---|
| A-AI7 (H-9) | ✅ ce1328ec | `Helpers/BankAiCandidateGuard.cs` (ใหม่) · `BulkBankAiMatchService.ProposeAsync` (`ScreenAiMatches` ก่อน AiValidated/pre-dedup/dedup + ชั้นปรับเทียบตรวจซ้ำ · ลบ `? ra2 : c.Amount` · ดัชนีแถว feedback เดินคู่ข้อเสนอที่รอด) · `ProposedMatch.FabricatedCandidates` · `BankService.ValidateMatchAmountAsync` (ฝั่งเขียน: id ที่ไม่พบ ⇒ 404 ข้อความไทย) | `BankAiCandidateGuardTests` (สองทิศ 9 เคส) · required_call_site 3 แถว |
| A-AI1 (H-1 · P1) | ✅ ce1328ec | `BankReconciliationPattern.ExplicitConfirmCount` + migration DO-block (ADD COLUMN + backfill **ครั้งเดียว**) · `Helpers/BankPatternEvidence.cs` (ใหม่) · `BankService.Learning` (`UpsertPatternAsync` · `CaptureConfirmedMatchAsync(…, source)` · `RecordReconciliationPatternsAsync(…, sourceByItemId)`) · `ReconcileAsync`/`BatchReconcileAsync`/`CreateReconciliationGroupAsync` ส่ง source · DTO `Source` 3 ตัว · `BankMatchDistillationModel` นับ Explicit · `bank.html` ส่ง source 7 จุด (+ batch ส่ง `wasAiValidated`/`confidenceAtApply` ที่ไม่เคยส่ง · ติ๊กจากคลัง = `_autoSrc` → Implicit · ผู้ใช้แตะ = ล้าง) · `BankController` สร้าง JE จากบรรทัด = Explicit | `BankPatternEvidenceTests` ("กดรับรัว ๆ 50 ครั้งไม่ดัน" · backfill = สูตรเดิม) · `ai_feedback_source_check` กติกา 3–4 (negative: ถอด source ใน bank.html จริง → ฟ้อง 2 จุด) · required_call_site 7 แถว |
| A-AI3 (H-5) | ✅ ce1328ec | `BankMatchScorer.DepositPreference` (ใหม่) · `GetMatchCandidatesAsync` เลิกบวก/ลบคะแนนเฉพาะจอ → ลำดับรอง · `GetLearnedSuggestionsAsync` คะแนน = `BankMatchScorer.Score` · ติ๊ก = `BankMatchArbiter.Decide` บนรายการค้างทั้งหมด (`AutoSelect`) · โหลด pool ครั้งเดียว · DTO `LearnedSuggestion` +Score/Verdict/AutoSelect/ExplicitConfirmations/PatternRelevance · `bank.html` อ่าน `autoSelect` | `BankMatchSingleStandardTests` · required_call_site 2 แถว |
| A-AI6 (H-8) | ✅ ce1328ec | `Services/Ai/Distillation/DistillationModelRegistry.cs` (ใหม่ · Program.cs เรียกบรรทัดเดียว) · `AiOrchestrator` seam `LoadSiteSettingsAsync`/`LoadActiveProviderAsync` + `ActiveProviderFilter` · ตาข่ายชั้นนอกคืนคำตอบนักเรียน (`AskProgress`) | `AiKillSwitchOrchestratorTests` (orchestrator จริง ทุก feature ที่มีนักเรียน · provider ทุกตัว IsActive=false · negative ถอดนักเรียน · DB ล่มกลางทาง · ratchet ทะเบียนสองทิศ) · required_call_site 2 แถว |
| A-AI4 (H-6) | ✅ ce1328ec | `PaymentVoucherAccountingDistillationModel.cs` (ใหม่ · bespoke) · `DocumentAiAugmenter.ParseBulkPvResponse` (internal static · อ่านโครงนักเรียน · ไม่ parse คำเดี่ยว · ข้อความตามผู้ตอบจริง) · `ParseBulkApprovalResponse` (คลาสเดียวกัน — ข้อความ/ไม่ parse คำเดี่ยว) · แถว feedback ลูกจากนักเรียน = LocalModel/Skipped · `Helpers/AiAnswerSource.cs` (ใหม่) | `BulkPvStudentTests` (ใช้ `BulkPvAccountingPrompt.Build` ตัวจริง) · required_call_site 3 แถว |
| A-AI2 (H-4) | ✅ ce1328ec | `DocumentAiSuggestion` +`FromStudent`/`FromRule`/`HasAnswer`/`HasModelAnswer` · `Convert` · `BankAiAugmenter` · `BankFeedService.TryAutoMatchAsync` (`HasModelAnswer` + ScreenAiProposals ≥0.70 · ป้าย `BankFeedSuggested`/`BANK-MATCH-LOCAL-DOC` เมื่อนักเรียนตอบ) · `AiSuggestionController.ToDto` +fromLocalModel/sourceLabel | `BulkPvStudentTests` · required_call_site 2 แถว |
| A-AI5 (H-7) | ✅ ce1328ec | `Helpers/GlSuggestionApplyPolicy.cs` (ใหม่) · `DocumentService.SuggestPaymentVoucherAccountingAsync` (นอกช่วงเมธอดของ DV/TX) · DTO `SuggestPvAccountingLineResult.MayAutoFill/FromLocalModel` · `SuggestPvAccountingResponse.FromLocalModel/SourceLabel/Warnings` · `documents.html` ช่วง bulk-PV เท่านั้น (อ่านธง/ป้าย · จับคู่บรรทัดด้วย tempId) | `BulkPvStudentTests` (Theory 5 เคส + tier-2 0.45) · required_call_site 1 แถว |
| A-AI8 (ข้อ 58) | ✅ ce1328ec | `AiFeedbackRecorder.DiscardUnsaved` (internal static) ใน catch ของ `RecordUserChoiceAsync` · `BumpTenantReviewAsync` · `UpsertTenantRollupAsync` · `UpsertDailyRollupAsync` | `AiFeedbackRecorderDiscardTests` (change tracker · ไม่ต้องมี DB) · required_call_site 4 แถว · เทสต์ write→fail บน PostgreSQL รอ job `db-test` (A-PL2) |

## A-AI3 — BankMatchGolden ก่อน/หลัง + ใบที่อันดับเปลี่ยน (CLAUDE §H)
- **รัน dotnet ไม่ได้ในเครื่องนี้** ⇒ ยืนยันเชิงโครงสร้าง: `BankMatchScorer.Score` **ไม่เปลี่ยน** (เพิ่มเมธอดใหม่ `DepositPreference` แยก) และ
  `BankMatchArbiter.Decide` ไม่ถูกแตะ ⇒ ทุกเคสใน `BankMatchGoldenTests` (อินพุตเข้า Score/Decide ตรง) ให้ผลเท่าเดิม**โดยโครงสร้าง** · AutoMatch/BankFeed/OpenBanking
  ไม่ได้ส่งหลักฐานเงินลงที่ไหน ⇒ **คำตัดสินของเส้นเครื่องไม่เปลี่ยนแม้แต่ใบเดียว**
- อันดับที่เปลี่ยน — **เฉพาะรายการบนหน้าจับคู่ด้วยมือ** (`GetMatchCandidatesAsync`): คู่ที่คะแนนจริงต่างกันน้อยกว่าผลต่างโบนัสเดิม (Payment ±5 · JE +8/+4/−8
  ⇒ ช่วงพลิกได้ไม่เกิน 16 คะแนน) — ตัวอย่างที่ล็อกในเทสต์: JV เงินสด 85 กับ RV ธนาคาร 80 · เดิมจอโชว์ 77/88 (RV ขึ้นก่อน) แต่ arbiter ประทับ/เสนอด้วย 85/80
  ⇒ ตอนนี้จอโชว์ 85/80 · คะแนนเท่ากันยังเรียง "ลงธนาคารก่อน" เหมือนเดิม · ไม่มีกรณีที่อธิบายไม่ได้
- ปุ่ม "✨ AI จับคู่จากประวัติ": เดิมติ๊กทุกรายการที่ความเกี่ยวข้อง ≥ 0.4 (อาจหลายรายการต่อบรรทัด) → ตอนนี้ติ๊กเฉพาะที่ 1 ของรายการค้างทั้งหมดเมื่อ arbiter `Apply`
  (เข้มขึ้น · ทิศตรงข้าม: รายการที่ไม่ถูกติ๊กยังแสดงในข้อความ/เลือกเองได้ · เทสต์ "ผู้ใช้ทำอะไรได้แทน" = AI-05 ใน TEST_PLAN)

## กฎเหล็ก #1 — สิ่งที่พบเพิ่ม (ยืนยันด้วยการเปิดไฟล์)
- **29 feature เรียก AI แต่ไม่มีนักเรียน** (กฎเหล็ก #1 ข้อ 2 ยังไม่ผ่าน) — รายชื่อใน `DistillationModelRegistry.KnownGapsWithoutStudent` (ratchet) ·
  DOCUMENT_FLOW §6.4 เคยเขียนว่า 7 ตัวในนั้น "generic" (VatType · PaymentChannel · ProjectAllocation · ContactFuzzy · ManualJeAccount · Dimension · ManualJournal) — แก้ doc แล้ว
- ไม่ได้ลงทะเบียน generic ให้ 29 ตัวรอบนี้ — ต้องตรวจทีละ call site ก่อนว่าด่านใช้คำตอบ (tier-2 majority ไม่ดูอินพุต 0.45) ไม่ apply แบบไม่มีเกณฑ์ตัวเลข

## ความเสี่ยงคอมไพล์ (ยังไม่ได้คอมไพล์)
- `DocumentAiSuggestion` เปลี่ยนจาก positional record ล้วนเป็นมี body + 2 พารามิเตอร์ท้าย (default) — ผู้สร้างทุกตัวใช้ตำแหน่ง/ชื่อเดิม (ตรวจแล้ว 9 จุด)
- `IBankService.RecordReconciliationPatternsAsync` เพิ่มพารามิเตอร์ท้าย optional + `using Accounting.Models.Enums` ใน interface (ตรวจชื่อชนแล้ว 0)
- `AiOrchestrator` เพิ่ม `protected virtual` 2 ตัว + `internal static Expression` (เทสต์อยู่คนละ assembly — อาศัย InternalsVisibleTo ที่มีอยู่)
- เทสต์ `AiKillSwitchOrchestratorTests` สร้าง `AccountingDbContext` แบบ offline (Npgsql พอร์ต 1 — รูปแบบเดียวกับ `SettlementRound200V2Tests`) และ `ServiceCollection().AddLogging()`
  (อาศัย shared framework ที่ไหลจากโปรเจกต์หลัก) · `GetLearnedSuggestionsAsync` ใช้ named argument ตามตำแหน่ง (C# 7.2+)
- checker ที่รัน: required_call_site (เฉพาะ 24 แถวของทีม + negative 12 เคสผ่าน importlib · ตัวเต็มดูผลใน log ของ main agent) · record_arg · nullable_arg · using ·
  undeclared_local · arg_type · service_interface · string_quote_close · comment_line_break · tuple_name_merge · accessibility · identifier_space · html_attr_escape ·
  onclick_js_string · enum_number_compare · gl_code · namespace_shadow · verbatim_string · di_cycle · dto_nullable_contract · ai_feedback_source (+self-test) ·
  escape_helper · write_permission_gate · attachment_gate = 0 ปัญหา (ทุกตัวรายงานจำนวนไฟล์ > 0) · node --check bank.html/documents.html · brace/U+FFFD ไฟล์ที่แก้

## ไฟล์นอกรายการที่ถือ (แตะเล็กที่สุด)
`Program.cs` (บล็อกลงทะเบียนนักเรียน → 1 บรรทัด) · `DocumentService.SuggestPaymentVoucherAccountingAsync` (ไม่อยู่ในช่วงของ DV/TX) · `Models/DTOs/Document/DocumentDtos.cs`
(เฉพาะ record SuggestPv*) · `Controllers/AiSuggestionController.cs` (ToDto + bulk PV endpoint) · `Controllers/BankController.cs` (1 อาร์กิวเมนต์) ·
`Data/DatabaseMigrationHelper.cs` (บล็อกท้ายอาร์เรย์ติดป้ายทีม) · `tools/ai_feedback_source_check.py`

## คำถามค้าง (ทิศที่เลือก = มองเห็นและย้อนได้)
1. `/api/v1/bank/matches/confirm` (ระบบภายนอก) และการจับคู่รอบโอน settlement (`SettlementPostingService` · ไฟล์ทีม ST) ไม่ส่ง source ⇒ นับ **Implicit**
   (บันทึกแพตเทิร์นแต่ไม่ดันความมั่นใจ) — ถ้าเจ้าของต้องการให้ยืนยันจากระบบพาร์ทเนอร์/รอบโอนนับเป็นหลักฐาน ต้องตัดสิน (ST ส่ง `Source` ได้บรรทัดเดียว)
2. 29 feature ที่ไม่มีนักเรียน — ลำดับการทำนักเรียน/ตรวจ call site รอบถัดไป
3. ข้อเสนอจากประวัติ (A-AI3) ยังไม่แปลงสกุลเงิน (`BankMatchCurrency`) ก่อนให้คะแนน — เอกสารต่างสกุลจะได้คะแนนยอด 0 (ไม่ติ๊กให้ = ทิศปลอดภัย) · backlog
4. bulk คำเตือนอนุมัติ (`SuggestApprovalWarningFixesBulkAsync`) แก้แค่ข้อความ/ไม่ parse คำเดี่ยว — ยังไม่มีนักเรียนแบบมีโครงสำหรับทั้งชุด (ปิด provider = ไม่มีคำแนะนำ · บอกตรง ๆ)
5. ยังไม่ได้ส่งฝ่ายค้าน (F3 ข้อ 11) — แตะสิทธิ์/เงินทางอ้อม (ยืนยันจับคู่ธนาคาร) ⇒ main agent ควรส่ง 1 รอบหลัง merge
