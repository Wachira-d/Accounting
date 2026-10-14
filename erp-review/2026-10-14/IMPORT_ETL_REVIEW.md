# ศูนย์นำเข้าข้อมูล (Import Hub / ETL) — ผลตรวจ + แบบ + แบ่งทีม (รอบ 205 · 2026-10-14)

> คำสั่งเจ้าของ (2026-10-14): "ตั้งทีมตรวจสอบ ฟีเจอร์ การนำเข้าข้อมูลทั้งหมด และ ปรับปรุง พัฒนา ให้สามารถรองรับ ข้อมูลได้หลากหลายรูปแบบมากที่สุด
> ทำเหมือนเป็นเครื่องมือ etl ที่ทำงานอัตโนมัติให้ได้มากที่สุด และให้ผู้ใช้งาน เป็นเพียงผู้ตรวจสอบ ปรับปรุง ยืนยันข้อมูล ส่วนไหนต้องใช้ AI เข้ามาช่วย ก็ดำเนินการได้เลย"
> ประกอบกับคำตัดสินข้อ 150 (ไม่ล็อกตายตัว — ระบบแนะนำ ผู้ใช้ตัดสิน) · คำตัดสินข้อ 151 ใน `erp-review/2026-09-29/DECISIONS.md`
>
> **สถานะ:** แบบ (design) ของบอร์ดวิเคราะห์ · ตาราง "สถานะการแก้" ท้ายไฟล์ติ๊ก `✅ <sha>` เมื่อลงโค้ด · ห้ามลบแถว

## 1. Inventory — ทางนำเข้าเดิม 14 เส้น

| # | ฟีเจอร์ | ทางเข้า | ข้อบกพร่องหลัก |
|---|---|---|---|
| 1 | Generic import 17 entity | `ImportExportController.cs:48/61/77` → `ImportExportService.cs:35` (switch `:49-100`) | browser แยกไฟล์เอง `import-export.html:288-290,346` `split(',')` · xlsx ชีตแรกเท่านั้น · `DateTime.TryParse`×23 `decimal.TryParse`×41 `bool.TryParse`×2 ขึ้นกับ culture ("ใช่"/"Y" = false, dd/MM↔MM/dd สลับเงียบ) · ไม่มี staging · `SaveChanges` ครั้งเดียว (`:114`) บน change tracker ร่วม ⇒ แถวที่ล้มบันทึกครึ่ง ๆ · ไม่มี transaction · ไม่มี audit |
| 2 | Smart Import | `ImportExportController.cs:114/139/177/203` → `ImportExportService.cs:1880/2183/2027/2247` | `RawDataJson` plaintext (มี PII พนักงาน) ล้างหลัง confirm (`:2367`) ⇒ retry แถวล้มไม่ได้ · `DateFormat`/`DecimalSeparator` รับแต่ไม่ใช้ (`:2272`) = silent no-op · Status=Failed แม้แถวอื่นลงแล้ว (`:2361`) · heuristic 0.6–0.9 ติดป้าย `AiMatched` (`:2545`) = ป้ายไม่ซื่อสัตย์ · `_ai == null` ⇒ คืนว่าง ข้าม heuristic (`:2041`) + UI ทิ้งผล heuristic เมื่อ `usedAi=false` (`import-export.html:605-608`) = kill-switch พัง |
| 3 | ตัวจับคู่คอลัมน์ตัวที่สอง | `AiSuggestionController.cs:1768` `SuggestImportMapping` | synonym schema ส่วนตัว = owner ที่สองของกติกาเดียว |
| 4 | ย้ายจากโปรแกรมอื่น (Express/PEAK/FlowAccount × contacts/products/CoA) | `CompetitorImportController.cs:44/72` → `Migration/CompetitorImportFramework.cs` | UTF-8 เท่านั้น (`:128-132`) — Express มักเป็น TIS-620 · `ContactType` ฮาร์ดโค้ด JuristicPerson + IsCustomer/IsSupplier=true (`:181-183`) ข้าม `ContactTypeResolver`/`ThaiTaxIdValidator` · `ExpressContactsAdapter.CanHandle:377`/`FlowAccountContactsAdapter:483` จับแค่ชื่อไฟล์ + ลงทะเบียนก่อน (`Program.cs:404-421`) ⇒ `express_products.csv` ไป adapter ผู้ติดต่อ |
| 5 | Migration wizard (งบทดลองยกมา) | `MigrationWizardController.cs:23-98` → `MigrationWizardService.cs` | textarea `split(/[\t|,]/)` + `parseFloat` ("1,234.50" แตก) · จับคู่ผังด้วยมือล้วน · คำเตือนนับเป็น issue **บล็อก commit** (`:163`) ขัดข้อ 150 · rollback คืนค่ายกมาที่ถูกทับไม่ได้ (`:269-275` vs `:208`) · **ไม่มี permission gate** |
| 6 | Statement ธนาคาร | `BankController.cs:88` → `BankService.ImportBankStatementAsync:909` · `BankCsvParser.cs` · `BankExcelParser.cs` | `TryDecode:162` ไม่มี fallback Windows-874 (คอมเมนต์ `:43` อ้างว่ามี) · บังคับคอลัมน์ยอดคงเหลือ (`:278`) · xlsx ชีตแรก (`:40`) · ไม่เก็บไฟล์ต้นทาง |
| 7 | Open-banking file | `OpenBankingController.cs:41` → `OpenBankingService.cs:215-330` | **OFX/QIF นับบรรทัด ไม่บันทึก แต่ตอบว่า "ใหม่"** (`:305-311`) = HTTP 200 โกหก · dedupe ของตัวเอง = writer #2 |
| 8 | Generic `banktransactions` | `ImportExportService.ImportBankTransactionAsync:730` | writer #3 บวก `CurrentBalance` (`:759-760`) ขณะ `BankService` ตั้งเป็นยอด statement (`:1065`) — สองความหมาย |
| 9 | API v1 statements | `V1/BankV1Controller.cs:52` | writer #4 · dedupe เฉพาะ `ExternalId` |
| 10 | ไฟล์ตัดยอด/เกตเวย์ (ดีสุด) | `SettlementController.cs:136/151` → `SettlementImportService.cs` | `SettlementFileReader` (UTF-8 strict → UTF-16 BOM → Windows-874 strict → ล้มดัง) · `SettlementValueParser` · column map ต่อช่องทาง · AI มี student · ช่องว่าง: xlsx ชีตแรก · `.xls` ปฏิเสธ |
| 11 | สินทรัพย์ | `FixedAssetController.cs:209` → `FixedAssetService.ImportAsync:1245` | paste `split(',')` · **owner ที่สอง** `ImportExportService.ImportFixedAssetAsync:1195-1209` ข้าม `FixedAssetImportMethod` ⇒ ที่ดินผ่านทาง generic ถูกคิดค่าเสื่อม (ถดถอย E-03 รอบ 200) |
| 12 | Payroll run | `PayrollController.cs:311` → `PayrollService.ImportPayrollRunAsync:1151` | ดีแล้ว · master พนักงานผ่าน #1: `CitizenId` ไม่ตรวจ (`:1479`) |
| 13 | Duplicate detector | `ImportConflictController.cs:143` → `Import/DuplicateDetector.cs` | ไม่มีหน้าเรียก · ผลตัดสินไม่ถูกอ่าน · ไม่มี permission gate (`tools/write_permission_gate_baseline.txt:296-297`) |
| 14 | OCR/e-Tax XML/LINE/VAT history/API v1/Integration | หลายจุด | นอกขอบเขต — ใช้ helper ร่วมเท่านั้น |

ข้อเท็จจริงเพิ่ม:
- `AiFeatureKey.ImportColumnMatch=26`, `ImportDataReview=27` เรียก AI แต่อยู่ `KnownGapsWithoutStudent` (`DistillationModelRegistry.cs:89-90`) · `ImportPrompts.BuildColumnMatch` `ForceProviderCall=true` (`:147`) · `ImportAiAugmenter.cs:102-105` ทิ้ง `FeedbackId`/`UsedAi` · `SubmitManualMappingAsync` ไม่เรียก `RecordUserChoiceAsync` ⇒ ไม่เคยเรียนรู้ (ขัดกฎเหล็ก #1)
- modal นำเข้าใน `contacts.html:1262,1308` · `products.html:980,1019` · `journals.html:1065,1104` ส่ง `FormData` ไป endpoint `[FromBody]` JSON ⇒ 415 + อ่าน `validCount/preview/importedCount` ที่ไม่มีใน `ImportResult`
- `ImportExportPermissionScope.cs:59` ใช้ `"stockadjustment"` แต่ `"stock-adjustments"` normalize เป็น `"stockadjustments"` ⇒ หลุดไป JournalManage
- `ImportProductAsync` throw ตอน Skip (`:627`) ⇒ นับเป็น error
- JE import: CoA lookup ไม่กรอง `!IsDeleted` (`:784`) · contact lookup ใช้ `Contains` (`:793`)
- **Payments ข้าม domain service** `ImportPaymentAsync:1325-1384` แก้ `PaidAmount` ตรง ไม่มี JE/ธนาคาร/WHT ⇒ AR ลดใน subledger แต่ GL ไม่ขยับ
- **Documents ข้าม domain service** `ImportDocumentAsync:1591-1708` เขียน `Document` ตรง ใส่เลขจากไฟล์บน Draft (`:1653`) คำนวณยอดเอง (`:1703-1707`) ⇒ ข้ามกฎ DRAFT-number §86/4 + period lock
- `DocumentService.CreateDocumentAsync:944` เปิด transaction เอง (`:1136`) เช่นเดียวกับ `ProductService`/`AccountingService` ⇒ batch ห่อ domain service ด้วย transaction นอกไม่ได้
- `IFileAttachmentService` ไม่มีเมธอดอ่าน
- helper ที่ใช้ซ้ำ: `ThaiDate.TryParseFlexible`/`NormalizeYear` · `ThaiTaxIdValidator` · `TaxBranchCode.TryNormalize` · `ContactTypeResolver` · `ContactTaxBranchKey` · `SettlementValueParser` · `CharNgramSimilarity.TopK` · `FuzzyMatcher` · `ProductMatcher.MatchAsync` · `SubLedgerReconciliationService.ReconcileAsync` · `EncryptedColumnConverter` · `ImportReviewHeuristics` · `JobLock.RunExclusiveAsync`/`TryXactLockAsync` · claim pattern `FOR UPDATE SKIP LOCKED` (`EmailScheduleService.cs:413-425`)
- `AiFeatureKey` ว่างถัดไป = 58 (สูงสุด `SettlementLineClassify = 57`)

## 2. แบบ — pipeline เดียว

**Flow:** Upload → **Extract** (ชนิดไฟล์/encoding/ชีต/แถวหัวตาราง/ตัดแถวชื่อรายงาน+ยอดรวม) → **Transform** (จับคู่คอลัมน์หลายชั้น · normalize ค่า · จับคู่ข้อมูลเดิม · issue · คะแนนต่อแถว) → **Stage** (`ImportStagedRows`) → **Review** (ผู้ใช้แก้เซลล์ · รับคำแนะนำ · เลือก action รายแถว) → **Commit** (ผ่าน writer/domain service เดิม · idempotent) → **Learn** (แม่แบบ + `RecordUserChoiceAsync`) → **Undo** (ทางเลือก)

### 2.2 Entity (owner เดียว: ขยาย `SmartImportSession` ไม่สร้าง batch คู่ขนาน)
- `SmartImportSessions` + คอลัมน์ (ADD COLUMN IF NOT EXISTS): `SourceFileAttachmentId uuid` · `FileSha256 varchar(64)` · `SheetName varchar(200)` · `SheetNamesJson text` · `HeaderRowIndex int default 0` · `HeaderRowSpan int default 1` · `Encoding varchar(32)` · `Delimiter varchar(4)` · `HeaderSignature varchar(64)` · `MappingTemplateId uuid` · `SourceSystem varchar(64)` · `Phase int default 0` · `CommitMode int default 0` · `ProcessedRows int default 0` · `ClaimedBy varchar(100)` · `ClaimedAt timestamptz` · `HeartbeatAt timestamptz` · `ColumnMatchAiFeedbackId uuid` · `ColumnMatchUsedAi bool default false` · `OptionsJson text` · `ReviewSummaryJson text` · `UndoneAt timestamptz` · `UndoneBy text`
- `ImportSessionStatus` += Queued=10, Staged=11, Committing=12, PartiallyCompleted=13, Undone=14, Cancelled=15 (ค่าเดิมคงไว้)
- ตารางใหม่ `ImportStagedRows` (`TenantEntity`): `SessionId` FK cascade · `RowNo` · `RawJson`/`MappedJson`/`OriginalMappedJson`/`BeforeJson` (**`EncryptedColumnConverter`** — PII) · `DisplayKey varchar(300)` · `IssuesJson` (`[{field,code,severity,message,suggested,ruleCode,legalRef,source}]`) · `Confidence numeric(5,4)` · `Status int` (Pending/Valid/Warning/Error/Excluded/Committed/Failed/Undone) · `Action int` (Create/Update/Merge/Skip) · `MatchedEntityId uuid` · `MatchKind varchar(32)` · `MatchScore numeric(5,4)` · `GroupKey varchar(200)` · `CommittedEntityType varchar(64)` · `CommittedEntityId uuid` · `CommitError text` · `UserEdited bool` · `AiFeedbackIdsJson text` · unique `(SessionId,RowNo)` · index `(CompanyId,CommittedEntityType,CommittedEntityId)`
- ตารางใหม่ `ImportMappingTemplates` (`TenantEntity`): `EntityType` · `HeaderSignature` · `SourceSystem` · `Name` · `HeadersJson` · `ColumnMapJson` (header normalize → field + transform ต่อคอลัมน์: negate / date order / value map) · `OptionsJson` · `UseCount` · `LastUsedAt` · `ConfirmedByUser` · `AutoCommitWhenClean bool default false` (opt-in — ผู้ใช้ตัดสิน) · unique partial `(CompanyId,EntityType,HeaderSignature) WHERE NOT "IsDeleted"` · seed cold-start อยู่ใน **โค้ด** `Helpers/ImportSourceSeed.cs` (ไม่ใช่แถวข้ามบริษัท)
- `AdvisoryLockKey.ImportBatch = "import-batch"` · attachment rule `"ImportBatch"` ใน `AttachmentPermissionScope` (gate = `ImportExportPermissionScope.ForImport(entity)`)

### 2.3 Pure helper (owner เดียวต่อเรื่อง · `Helpers/`)
- `ImportTabularReader` — ย้ายจาก `SettlementFileReader` (Decode/ReadCsv/ReadExcel/Capped/CellText) + `Sniff` → `ImportFileKind {Csv,Tsv,Xlsx,Xls,Ofx,Qif,Xml,Json,Pdf}` · `ListSheets` · `Open(fileName, bytes, sheetName?)` → `ImportTabularRows(Rows, Delimiter, Encoding, SheetName, SheetNames)` · `ImportFormatException(Code, ThaiMessage)` · `.xls`/`Xml`/`Pdf` ล้มดังพร้อมทางไปต่อ · `SettlementFileReader` = wrapper บาง
- `ImportHeaderDetector.Detect(rows≤30, targets, synonyms)` → `HeaderDetection(HeaderRowIndex, HeaderRowSpan, Headers, Confidence, Reason)` (หัวสองแถว merge · ข้ามแถวชื่อรายงาน) · `IsSummaryRow(cells)` = owner เดียวของ regex ยอดรวม (ย้ายจาก `BankCsvParser.cs:103-104`) — "รวมมิตร" ต้องไม่ถูกตัด
- `ImportValueNormalizer` → `ImportValue<T>(Value, Ok, IssueCode, Message, Suggested)`: `Date` (→`ThaiDate.TryParseFlexible` + Excel serial + order) · `Amount` (→`SettlementValueParser`) · `Flag` (ใช่/ไม่ใช่/Y/N/1/0/✓/มี) · `TaxId` (checksum · scientific ⇒ lost precision · 12 หลัก "0"+x ผ่าน ⇒ แนะนำ) · `BranchCode` (+ สำนักงานใหญ่/สนญ/HQ/สาขาที่ n) · `VatRate` · `Phone` · `Email` · `Enum<T>` · `Text` · `CanonicalDate`/`CanonicalAmount` (ISO/invariant เข้มงวด สำหรับ writer เดิมหลัง Transform)
- `ImportHeaderSignature.Compute(headers)` — SHA-256 ของชุด header normalize เรียงแล้ว (ไม่ขึ้นกับลำดับ)
- `ImportFieldSynonymSeed` (รวม `GetFieldAliases:2619` + schema `AiSuggestionController:1768` + header adapter โปรแกรมอื่น + `BankCsvParser`) · `ImportSchemas` (ย้ายจาก `GetTemplateFields:2675`)
- `ImportColumnMapper.Resolve(...)` → `ImportColumnPlan` ชั้น: template → exact → synonym → learned → data pattern → AI → user · greedy unique · AI guard: field ∈ targets, conf ≥ 0.70, ไม่ทับชั้นสูงกว่า
- `ImportRowConfidence` · `ImportAccountCodeResolver` (รหัสตรง → `AccountMappings` เดิม → n-gram ชื่อ → หมวดเลขหน้า 1–5) · `ThaiLegalNameKey.Key(name)` — ชื่ออย่างเดียว = แนะนำเท่านั้น ห้าม merge อัตโนมัติถ้าไม่มีเลขภาษี (หลักการ 3)

### 2.4 Service (`Services/Import/`)
- `IImportPipelineService`: `CreateAsync` (เก็บไฟล์ `UploadBytesAsync("ImportBatch")` · hash · เตือนไฟล์ซ้ำที่ commit แล้ว · ≤2,000 แถวทำทันที ไม่งั้น Queued) · `AnalyzeAsync` · `GetAsync`/`GetRowsAsync` · `RemapAsync` · `EditRowAsync` · `BulkActionAsync` · `CommitAsync(ImportCommitOptions{Mode GoodRowsOnly|AllOrNothing, AcknowledgedWarningCodes, SaveTemplate=true, ApproveAfterCreate=false})` · `PreviewUndoAsync`/`UndoAsync(reason)`/`CancelAsync` · `AddChainedAuditLog` RuleCode `IMPORT-COMMIT`/`IMPORT-UNDO`
- `IImportTarget` ต่อ entity: `EntityType` · `Schema` · `CommitStyle {SharedTransaction | OwnTransactionPerGroup}` · `ValidateAsync` · `CommitGroupAsync` · `CanUndoAsync` · `UndoAsync`
  - SharedTransaction: transaction เดียว savepoint ต่อ chunk 200 แถว · ล้ม ⇒ rollback savepoint + clear tracker + replay ทีละแถว · AllOrNothing = rollback ทั้งชุด
  - OwnTransactionPerGroup (`DocumentService` ฯลฯ): commit ทีละกลุ่ม · idempotent ด้วย `Status=Committed`+`CommittedEntityId`
- `IImportRowWriter.WriteRowAsync(cid, entityType, row, opts, by)` — สกัด switch ซ้ำ (`:49-100` และ `:2290-2341`)
- `IImportEntityResolver` (ห่อ `DuplicateDetector.MatchBatchAsync`): ผู้ติดต่อ tax+branch → `ThaiLegalNameKey` → `CharNgramSimilarity.TopK` (≥0.88 แนะนำ) → AI `ImportEntityMatch` เฉพาะ top-2 ห่าง < 0.05 และไม่มีเลขภาษี · สินค้า `ProductMatcher` · ผังบัญชี `ImportAccountCodeResolver` → AI `ImportAccountMap`
- `ImportMappingTemplateStore` · `ImportBatchWorker : BackgroundService` (claim `FOR UPDATE SKIP LOCKED` + heartbeat 2 นาที + `JobLock.RunExclusiveAsync(AdvisoryLockKey.ImportBatch, batchId)` · chunk 500 · ไม่มี static state · purge staged rows ของ batch ที่ commit แล้ว > 90 วัน คงสรุป + ไฟล์ต้นทาง)

### 2.5 บล็อกเฉพาะ (ข้อ 150) — ทุกข้อมีทางไปต่อ
ไม่มีตัวตนหลังทุก fallback · JE/งบทดลองไม่ลงตัว (ทางไปต่อ: ลงส่วนต่างเข้าบัญชีผลต่างยกมา — ผู้ใช้เลือก) · งวดปิด (ข้อความบอกให้เปิดงวด) · เลขเอกสารชนใบที่ไม่ใช่ร่าง (§86/4) · อ้างข้ามบริษัท · **นอกนั้น = คำเตือน + คำแนะนำ ยืนยันรับทราบตอน commit** · `MigrationWizardService.ValidateSessionAsync` แยก error/warning

### 2.6 AI (กฎเหล็ก #1)
| Key | Student | Local / cold start | Guard | Feedback |
|---|---|---|---|---|
| `ImportColumnMatch` 26 | bespoke `ImportColumnMatchDistillationModel` (StructuredJson `{"mappings":[…]}`) | synonym seed + แม่แบบบริษัท + header→field ที่ยืนยันแล้ว | field ∈ targets, ≥0.70 | `SmartImportSession.ColumnMatchAiFeedbackId/UsedAi` · commit/remap ⇒ `RecordUserChoiceAsync(id, finalMappingJson, acceptedAi)` · `ForceProviderCall=false` |
| `ImportDataReview` 27 | bespoke `ImportDataReviewDistillationModel` ห่อ `ImportReviewHeuristics` | กฎ | row/field ต้องมีจริง | รับ/ปฏิเสธคำแนะนำ ⇒ `RecordUserChoiceAsync` |
| `ImportAccountMap` **58** | `GenericFeedbackDistillationModel` | `ImportAccountCodeResolver` + `AccountMappings` | ∈ รหัสผังบริษัท | ต่อแถวงบทดลองตอนยืนยัน |
| `ImportEntityMatch` **59** | `GenericFeedbackDistillationModel` | tax key + n-gram | ∈ candidate ids ∪ {"NEW"} | ต่อแถวกำกวมตอนยืนยัน |

ถอด 26/27 จาก `KnownGapsWithoutStudent` · เพิ่ม 58/59 ใน `GenericFeatureKeys` + ป้าย `AiUsageReportService` + รายการ `AiFeedbackTrainingJob` · ป้ายซื่อสัตย์ต่อคอลัมน์/แถว ("🤖 AI แนะนำ" เฉพาะ `UsedAi`, ไม่งั้น "⚙️ ระบบแนะนำ") · kill-switch: ทุกชั้นใต้ AI เป็น local

### 2.7 Endpoint `Controllers/ImportHubController.cs` (`api/companies/{cid}/import`)
ทุก action gate `ImportExportPermissionScope.ForImport(batch.EntityType)` · `POST batches` (multipart, `RequestSizeLimit` 30 MB) · `GET batches` · `GET batches/{id}` · `GET batches/{id}/rows` · `POST batches/{id}/analyze` · `PUT batches/{id}/mapping` · `PATCH batches/{id}/rows/{rowId}` · `POST batches/{id}/bulk` · `POST batches/{id}/commit` · `GET batches/{id}/undo-preview` · `POST batches/{id}/undo` · `POST batches/{id}/cancel` · `GET batches/{id}/errors.csv` · `GET batches/{id}/source-file` · `GET entities` · `GET/PUT/DELETE templates` · endpoint เดิมคงเป็น façade

### 2.8 หน้า `wwwroot/pages/import-hub.html`
1) วางไฟล์ → เซิร์ฟเวอร์เดา entity/โปรแกรมต้นทาง/ชีต/แถวหัว (chip แก้ได้) 2) ตาราง review server-paged 100 แถว: สีเซลล์ เติมอัตโนมัติ(ฟ้า)/normalize(เขียว + ค่าเดิมใน tooltip)/<0.85(เหลือง ไม่ว่าง)/error(แดง) · chip issue กรอง + "ใช้คำแนะนำทั้งหมด" · คอลัมน์จับคู่ (สร้างใหม่/อัปเดต "X" 0.93/ข้าม) · dropdown หัวคอลัมน์มีป้ายแหล่ง (📌 แม่แบบ / 📚 คำพ้อง / 🧠 เรียนรู้ / 🤖 AI / ✋ ผู้ใช้) 3) สรุป commit: โหมด · คำเตือนให้รับทราบ · "จำรูปแบบไฟล์นี้" ติ๊กไว้ · progress 4) ผล: รายแถว · "ย้อนการนำเข้า" · ดาวน์โหลดแถวผิด · แม่แบบตรง + ทุกแถวถูก ⇒ เปิดที่สรุปเลย (1 คลิก) · ป้าย/สถานะทั้งหมดจาก DTO เซิร์ฟเวอร์ (F2-5) · modal ของ contacts/products/journals/fixed-assets/import-export/bank/migrate-competitor ลิงก์ `import-hub.html?entity=…`

### 2.9 Undo ต่อ target (preview ก่อน — ผู้ใช้ตัดสิน)
master ที่สร้าง = soft-delete ถ้าไม่มีการอ้าง · ที่อัปเดต = คืนจาก `BeforeJson` · เอกสารร่าง = ลบผ่าน `DocumentService` ถ้ายังร่าง (อนุมัติแล้ว = บล็อก + ทาง void/CN) · รับชำระ = ทางกลับรายการเดิม ไม่งั้นบล็อก · งบทดลองยกมา = `MigrationWizardService.RollbackAsync` (คืนค่าเดิม) · สต็อก = movement ชดเชยผ่าน `StockLedger` · ธนาคาร = ลบถ้ายังไม่จับคู่ · สินทรัพย์ = ลบถ้ายังไม่ลงค่าเสื่อม

### 2.10 ลำดับย้ายระบบ (checklist ในหน้า)
ผังบัญชี → ผู้ติดต่อ → สินค้า → งบทดลองยกมา → ลูกหนี้/เจ้าหนี้คงค้าง → สต็อกยกมา → สินทรัพย์ → **กระทบยอด** (`SubLedgerReconciliationService.ReconcileAsync` AR/AP · มูลค่าสต็อก vs บัญชีสินค้า · ราคาทุน/ค่าเสื่อมสะสม vs งบทดลอง) · เอกสารนำเข้าเป็นร่าง + เลข DRAFT เสมอ เลขเดิมไปที่ `Reference`

## 3. แบ่งทีม (T0 ก่อน แล้ว A–E ขนาน · ไฟล์ไม่ทับกัน)
- **T0** schema + สัญญา + helper ฐาน 2 ตัว (`ImportTabularReader`, `ImportValueNormalizer`) + `Program.cs` (ทีมเดียวที่แตะ)
- **A** mapping + AI (synonym/signature/mapper/seed/schemas · student 26/27 · template store · augmenter/prompts · `AiSuggestionController.SuggestImportMapping` → mapper)
- **B** engine + writer เดิม (header detector · row confidence · ThaiLegalNameKey · pipeline service · worker · entity resolver · master-data targets · `ImportExportService` ทั้งไฟล์ · `DuplicateDetector.MatchBatchAsync`)
- **C** ธนาคาร (writer เดียว `BankService.ImportParsedStatementRowsAsync` · open-banking CSV/OFX จริง/QIF ล้มดัง · BankV1 · `BankCsvParser`/`BankExcelParser` · `BankOfxParser` · `BankStatementImportTarget`)
- **D** บัญชี + migration (targets: งบทดลอง/เอกสาร/รับชำระ/สินทรัพย์ · `ImportAccountCodeResolver` · `OpeningBalanceReconciliation` · `MigrationWizardService` error/warning + rollback คืนค่า + gate · adapter โปรแกรมอื่น detection + decode)
- **E** UX + gate (`ImportHubController` · `import-hub.html` · api.js/layout.js/translations · modal → hub · `ImportConflictController` gate + baseline)

## 4. รอข้อมูลภายนอก / คำตัดสิน
1. ไฟล์ส่งออกจริง Express (TIS-620/.xls/DBF) · PEAK · FlowAccount (ผู้ติดต่อ/สินค้า/ผัง/งบทดลอง/อายุหนี้/ทะเบียนขาย-ซื้อ) เพื่อทำ source profile
2. statement ธนาคารไทยแต่ละแห่ง (CSV/XLSX) · statement PDF รอตัวอย่าง
3. ตัวอย่าง OFX/QIF (QIF = แจ้ง "ยังไม่รองรับ" ชัดเจนจนกว่าจะมี)
4. `.xls` (BIFF8) — MiniExcel อ่านไม่ได้ + ห้าม lib อื่น ⇒ ล้มดัง "บันทึกเป็น .xlsx" (คำตัดสินข้อ 151)
5. นำเข้าเอกสารย้อนหลังแบบคงเลขเดิม/ลง GL — ตอนนี้ **ร่างเท่านั้น** (คำตัดสินข้อ 151)
6. ยอดสะสมเงินเดือนกลางปี (ภ.ง.ด.1ก / 50 ทวิ) — รอตัวอย่าง
7. สต็อกยกมาหลายคลัง/lot/FIFO · สินทรัพย์ค่าเสื่อมสะสมกลางปี — รอตัวอย่าง
8. e-Tax XML เป็น zip ผ่าน OCR — รอตัวอย่าง
9. `AutoCommitWhenClean` — opt-in ต่อแม่แบบ ค่าเริ่ม false (คำตัดสินข้อ 151)

## 5. สถานะการแก้
| รหัส | เรื่อง | สถานะ |
|---|---|---|
| ETL-T0 | schema + สัญญา + reader/normalizer | ✅ 95c3031b — สัญญา: `Models/DTOs/Import/ImportHubDtos.cs` · `Services/Import/I*.cs` · `Helpers/ImportTabularReader.cs` · `Helpers/ImportValueNormalizer.cs` |
| ETL-A | mapping + AI distillation 26/27/58/59 | ⏳ |
| ETL-B | engine + writer เดิม (culture parse · payment/asset/document bypass) | ⏳ |
| ETL-C | ธนาคาร writer เดียว + OFX จริง + TIS-620 | ⏳ |
| ETL-D | งบทดลอง/เอกสาร/รับชำระ/สินทรัพย์ + migration wizard | ⏳ |
| ETL-E | ImportHub UI + modal 415 + gate | ⏳ |
