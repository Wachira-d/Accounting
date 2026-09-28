# Round 198: adversarial review (F3 #11) of team C posting, `84d47dda` (merged in `771545fc`)

Read-only. I read `SettlementPostingService.cs`, `Helpers/SettlementPosting.cs`, `WalkInCustomerContact.cs`, the team A contract diff, team B's line-edit service, and the callee paths in `DocumentService`, `WithholdingTaxCertService`, `BankService`, `AccountingService`, `JournalEntryBuilder`, `JobLock` and `PdfGenerationService`. HEAD still has these files exactly as they were in `84d47dda`. Nothing here was compiled. There is no SDK in this environment.

## Summary: CONFIRMED P0/P1

| ID | P | One line |
|---|---|---|
| **C-1** | **P1** | Team C's posting lock (`settle-post`, session) and team B's edit lock (`settlement-import`, xact) are different keys, and B treats a half-posted batch (still `Matched`) as editable. If a user **voids the batch after a partial post** and re-imports the file, the approved fee PVs, the issued summary tax invoice and the receipts are orphaned. The next post creates them again: expense and input VAT twice (fee docs have no duplicate check at all), and revenue and output VAT twice (the duplicate-sale check skips voided or deleted batches). |
| **C-2** | **P1** | Before it touches anything, Unpost checks only closed accounting periods. It does not check a declared or filed ภ.พ.30 or ภ.ง.ด.53, an Accepted e-Tax, or the 50 ทวิ filing. It voids payments, then certificates, then documents in arbitrary order, so a void that is refused halfway leaves the batch `Posted`/`BankMatched` with receipts and fee docs already reversed and the payout JE still live. Retrying fails at the same document every time, and `Post` answers "already posted". The ledger is stuck until someone writes a manual JE. Also, `IWithholdingTaxCertService.VoidAsync` has no filing guard, so certificates already filed in ภ.ง.ด.53 are voided silently. |

The P2/P3 items (C-3 to C-16) follow.

## สถานะการแก้ — ทีม S3 รอบ 198 (ติ๊กไม่ลบแถว · sha เติมในคอมมิตตามหลัง)

| ID | สถานะ | แก้ที่ / เหตุผลที่ยังค้าง |
|---|---|---|
| C-1 | ✅ <pending> | (a) `Helpers/SettlementChannelLock` ตัวเดียว — ผู้นำเข้า `pg_advisory_xact_lock(Key)` · ผู้ลงบัญชี `JobLock(Scope, Part)` = คีย์ตัวเลขเดียวกัน (เทสต์ล็อก) · ทุกเส้นแก้ข้อมูลล็อกก่อนโหลด · (b) `IsEditable(status, artifacts)` — รอบที่มีเอกสาร/การรับชำระที่มีป้ายของรอบ ⇒ แก้/เติม/ยกเลิกไม่ได้ (`SETTLEMENT-BATCH-PARTIAL`) · (c) `CommitPostedAsync` คิดแผนใหม่ใต้ `FOR UPDATE` เทียบ `SettlementPlanFingerprint` · (d) ใบสรุปวันเดียวกันนับรอบที่ยกเลิก/ลบแล้ว + ของกำพร้าของรอบที่ยกเลิกในช่องทางเดียวกัน ⇒ `OrphanPostingArtifacts` (บล็อกพร้อมทางไปต่อ) |
| C-2 | ✅ <pending> | `SettlementUnpostGate.Evaluate` ก่อนแตะชิ้นแรก (e-Tax Accepted · รายงานที่ล็อก · ภ.พ.30 ประกาศ/ยื่นแล้ว · ภ.พ.36 · 50 ทวิ ที่ยื่นแล้ว) ⇒ ปฏิเสธทั้งรอบพร้อมทางไปต่อ (ใบลดหนี้/ปรับปรุงงวดปัจจุบัน) · ลำดับคงที่ `VoidOrder` (ใบขายสรุป → ใบค่าธรรมเนียม → การรับชำระ → ถอนจับคู่ธนาคาร → กลับ JE) · `WithholdingTaxCertService.VoidAsync` + `DocumentService.VoidDocumentAsync` ปฏิเสธ 50 ทวิ ที่ยื่นแล้ว (`WhtCertVoidGuard` ตัวเดียว · `WhtCertFilingScope.Filed` + `TaxFilingLockPolicy.DeclaredOrFiledStatuses`) |
| C-3 | ✅ <pending> | `SettlementWhtCertResume.Decide` — ร่างค้าง ⇒ ออก · ออกแล้วยอดตรง ⇒ ข้าม · ยอดไม่ตรง ⇒ ล้มดัง |
| C-4 | ✅ <pending> | `SettlementReceiptReconcile.Stale` — การรับชำระที่มีป้ายต้องตรงแผน (ใบเดียวกัน · ยอดรวมต่อใบเท่ากัน) ไม่ตรง ⇒ `StaleDocument` บล็อก |
| C-5 | ✅ <pending> | `SettlementPostingCompleteness.Missing` ก่อนประทับ Posted (ทุกชิ้นมีเอกสารใบเดียว ออกแล้ว ยอดตรงยอดที่ตรวจรอบนี้ · รับชำระครบ · ไม่มีชิ้นเกิน) · `SettlementArtifactGuard` ใน `VoidDocumentAsync`/`VoidPaymentAsync` (รวมใบขายที่รอบโอนรับชำระ) — รอบ Posted/BankMatched ⇒ 409 "ใช้ยกเลิกการลงบัญชี" · ผ่านเฉพาะ `SettlementUnpostScope` · รอบค้างครึ่งทาง/ยกเลิกแล้ว ยกเลิกทีละใบได้ (ทางไปต่อ) |
| C-6 | ✅ <pending> (บล็อกชั่วคราว — รอคำตัดสินเจ้าของ) | `AbbreviatedTaxInvoiceRule.Judge(…, AbbreviatedInvoiceChannel.Document)` ⇒ ใบขายสรุปมี VAT แต่บริษัทไม่มีสิทธิ์ §86/6 ⇒ บล็อก `SummaryTaxInvoiceNotAllowed` พร้อมทางไปต่อ (ติ๊กกิจการขายปลีก ถ้าจริง · ไม่งั้นออกใบกำกับเต็มรูปรายออเดอร์แล้วจับคู่) — เดิมหัวถูกลดเป็นใบเสร็จ REC เงียบ ๆ ทั้งที่ภาษีขายเข้า ภ.พ.30 · ดูคำถามเจ้าของ O-3 ข้างล่าง |
| C-7 | ⏳ เจ้าของตัดสิน | ดู O-1 ข้างล่าง |
| C-8 | ✅ <pending> | ใน service: จับคู่ธนาคาร = `Bank.Reconcile` · ปิด chargeback = `Journal.Manage` · ยกเลิกการลงบัญชี = ระดับ Void ของชนิดเอกสาร**และ**ใบที่รับชำระ (ตัวเดียวกับหน้าเอกสาร) + `Bank.Reconcile` เมื่อต้องถอนจับคู่ · กลับ JE รอบโอน = กระจกของการลงบัญชี (ใช้สิทธิ์ยกเลิกเอกสารชุดเดียวกัน ไม่เพิ่ม `Journal.Manage`) |
| C-9 | ⏳ เจ้าของตัดสิน | ดู O-2 ข้างล่าง |
| C-10 | ✅ <pending> | ข้อความ `SETTLEMENT-POST-AMOUNT` บอกว่าใบสำคัญจ่ายอนุมัติแล้ว ต้องยกเลิก (ใบร่างให้ลบ) |
| C-11 | backlog | ด่านลงบัญชียังไม่ดูเดือน ภ.พ.36/ภ.ง.ด.3 (ด่านยกเลิกการลงบัญชีดูแล้ว — รู้แบบของใบรับรองจริง) |
| C-12 | backlog | ส่ง `WithholdingTaxAmount: 0m` ตอนรับชำระใบที่ลูกค้าหัก — ต้องตรวจเส้น `CreatePaymentAsync` ก่อน |
| C-13 | backlog | ล็อกตอนสร้างผู้ติดต่อ "ลูกค้าเงินสด" |
| C-14 | ✅ <pending> | `ResolveChargebackAsync` ถือล็อกต่อช่องทางตัวเดียวกับยกเลิกการลงบัญชี |
| C-15–C-20 | backlog | สต็อก/COGS ของใบสรุป · cut-off ค่าธรรมเนียมข้ามเดือน · คืนเงินของออเดอร์ในใบสรุปรอบเดียวกัน · ป้ายการรับชำระปลอมได้ (ควรเป็น FK) · ปุ่มทิ้งการลงค้าง · `ChangeTracker.Clear` ใน execution strategy |
| ปุ่มหน้าจอ (ทีม D · หลัง merge) | ✅ <pending> | `SettlementBatchActions.For` ใช้ `SettlementSaleMatch.IsEditable(status, artifacts)` ตัวเดียวกับ `LoadEditableBatchAsync` + ผล `SettlementUnpostGate` จาก `ISettlementPostingService.UnpostBlockersAsync` (ตัวโหลดเดียวกับ `UnpostAsync`) ⇒ ปุ่มกับด่านไม่ drift · ปุ่มที่ซ่อนมีเหตุผลบนหน้า |
| E2-10 (จาก review198-E2) | ✅ <pending> | intent ที่ `RefundOutcomeUnknownSince != null`: จับคู่ CSV ⇒ `Unmatched` "ตรวจผลการคืนเงินก่อน" · ประกอบจาก intent ⇒ เตือน · ผู้ลงบัญชี ⇒ บล็อก `RefundOutcomeUnknown` (NextStep "ตรวจผลการคืนเงินก่อน") |

### คำถามที่ต้องให้เจ้าของตัดสิน (ห้ามเดาแทน)

- **O-1 (C-7) ตัวตนผู้อนุมัติของเอกสารที่ระบบสร้าง** — ใบสำคัญจ่ายค่าธรรมเนียม (PV เงินสด) ถูกอนุมัติใน `CreateDocumentAsync` ด้วย `approvedBy = createdBy = "system:settlement:…"` ⇒ SoD (ผู้ทำ ≠ ผู้ตรวจ) ไม่เคยทำงานกับเอกสารชุดนี้ ·
  ทางเลือก: (ก) ส่ง `createdBy = userId` แล้วย้ายป้ายของรอบโอนไปคอลัมน์เฉพาะ (`Document.SettlementBatchId` + migration — ป้ายใน CreatedBy คือกุญแจทำต่อจากที่ค้าง ต้องย้ายทั้งชุด) ·
  (ข) คง CreatedBy เป็นระบบ แต่ให้ SoD เทียบกับผู้นำเข้า/ผู้จัดประเภทรอบโอน · (ค) ยอมรับ (ระบบเป็นผู้ทำ · คนกดลงบัญชีเป็นผู้ตรวจ) และบันทึก `ApprovedBy = userId` ชัด ๆ
- **O-2 (C-9) ใบขายสรุป 1 ใบ/วัน/แพลตฟอร์ม vs payout หลายรอบที่มีออเดอร์วันเดียวกัน** — ด่าน `SummarySaleDuplicate` บล็อกรอบโอนที่สองที่มียอดขายวันเดียวกัน (DECISIONS ข้อ 2) ·
  ทางเลือก: (ก) ใบสรุปเพิ่มเติมของวันเดียวกัน (เลขใหม่ · อ้างใบแรก) · (ข) ใช้วันปล่อยเงิน (release) เป็นจุดความรับผิดแทนวันสั่งซื้อ · (ค) คงเดิม (ออกเอกสารขายของวันนั้นเองแล้วจับคู่)
- **O-3 (C-6) บริษัทจด VAT ที่ไม่ใช่กิจการขายปลีก** — ใบขายสรุปให้ "ลูกค้าเงินสด" เป็นใบกำกับเต็มรูป §86/4 ไม่ได้ (ไม่มีชื่อ/ที่อยู่ผู้ซื้อ) และอย่างย่อก็ไม่ได้ (§86/6) · รอบนี้**บล็อก**พร้อมทางไปต่อ (ทิศที่มองเห็น) ·
  ทางเลือกถาวร: (ก) marketplace = ขายปลีกเสมอ ⇒ เปิดสิทธิ์อย่างย่อเฉพาะช่องทาง settlement · (ข) ใบกำกับเต็มรูปรายออเดอร์จากข้อมูลผู้ซื้อในไฟล์ (PDPA) · (ค) ใบเสร็จรับเงิน + ยื่นภาษีขายตามรายงาน (ต้องให้นักบัญชีรับรอง)

---

## CONFIRMED

### C-1 · CONFIRMED · P1: void-after-partial-post orphans issued documents, and re-import doubles revenue, VAT and expense
- `SettlementPostingService.cs:122,597,744` lock with `JobLock.RunExclusiveAsync(_db, "settle-post", channelId)`.
- `SettlementImportService.Lines.cs:243,360,430,462` (Reclassify, AssignLineMatch, Rematch, VoidBatch) lock with `pg_advisory_xact_lock(AdvisoryLockKey.For(companyId, "settlement-import", channelId))`. The keys differ, so the two sides never exclude each other.
- `SettlementSaleMatch.IsEditable` (`SettlementSaleMatch.cs:125`) is `Imported|Classified|Matched`. A batch whose post failed halfway is still `Matched`, so every B edit, **including `VoidBatchAsync`**, is allowed. `VoidBatchAsync` never asks whether documents or payments carrying `CreatorPrefix(batchId)` or `PaymentMarker(batchId)` exist.
- Scenario (sequential, no race needed):
  1. `RequireApprovalForDocuments` is on with a threshold of 50,000, and the daily summary is 180,000.
  2. Post: the fee PV is auto-approved inside `CreateDocumentAsync` (`DocumentService.cs:1619-1633`). The summary TaxInvoice is created as a draft, and approval then throws the threshold error (`DocumentService.cs:5925-5941`). Result: 409 `SETTLEMENT-POST-PARTIAL`.
  3. The user voids the batch and re-imports the same file. R-A9 allows this after a void.
  4. Post on the new batch. `ExistingDocsAsync` is keyed by the **new** batchId, so it finds nothing. `DuplicateSalesAsync` builds `channelBatches` with `!b.IsDeleted && b.Status != Voided` (`:532-536`), so the old summary draft or issued document is invisible. There is no fee-document duplicate check anywhere.
  5. Result: a second fee PV, so expense and 11640 input VAT are doubled (the double input-VAT claim is a §82/5 risk). If the old summary had been approved, output VAT is also doubled. The old orphans point at a deleted batch, and Unpost cannot reach them because `HeadAsync` filters `!IsDeleted`.
- Race variant: `CommitPostedAsync` takes `FOR UPDATE` on the batch row, but B's `LoadEditableBatchAsync` reads without a row lock and writes `Status`/`IsDeleted` unconditionally. A concurrent `VoidBatchAsync` can therefore overwrite a just-`Posted` batch with `Voided`+`IsDeleted`, which orphans the payout JE too. A concurrent `ReclassifyLineAsync` or `AssignLineMatchAsync` between gate and commit makes `Posted` reflect a plan that no longer matches the lines, and `:320-323` then overwrites the user's new `MatchStatus` with `AutoSummary`.
- Fix:
  - (a) One lock for both teams: B takes the `settle-post` channel key (`TryXactLock` against the session lock), or C also takes the `settlement-import` key for the whole run.
  - (b) B's `IsEditable`/`VoidBatchAsync` refuses when any live document or payment carries the batch's marker ("unpost or void those first").
  - (c) `CommitPostedAsync` re-runs `Plan` under `FOR UPDATE` and compares it with the gate's plan.
  - (d) The duplicate check also looks at summary and fee documents whose batch is voided or deleted (the `CreatedBy` prefix is enough).

### C-2 · CONFIRMED · P1: Unpost has no pre-flight for filing, e-Tax or 50 ทวิ, and fails into a half-reversed ledger
- The pre-checks at `SettlementPostingService.cs:614-640` cover: CanVoid per document type, an open chargeback JE, and closed periods. Then the order of work is:
  1. bank unmatch (`:647-653`),
  2. **void every payment** (`:655-659`),
  3. for each doc in **undefined order** (`ExistingDocsAsync` has no `OrderBy`): void its certificates, then `VoidDocumentAsync` (`:661-670`).
- `VoidDocumentAsync` throws on a filing-locked report or an **Accepted e-Tax** (`DocumentService.cs:8035-8051`).
- **Scenario A (e-Tax):** a VAT-registered company has e-Tax on. The summary TaxInvoice (T03) is Accepted by RD a few minutes after it is issued. A week later the user unposts to fix a line.
  - Step 1 unmatches the bank. Step 2 voids all receipts (the invoices are open AR again and clearing is credited). Some fee PVs may be voided.
  - The summary void then throws, and the result is 409 `SETTLEMENT-UNPOST-PARTIAL`.
  - The batch is still `BankMatched` while the bank transaction is now Unmatched. The payout JE is still live. Clearing is off by the reversed receipts and fees.
  - Every retry throws at the same document, and `PostAsync` returns AlreadyPosted. **No path forward except a manual JE.**
- **Scenario B (asymmetric gates):**
  - For VAT the Post gate uses `TaxFilingLockPolicy.DeclaredOrFiledStatuses` (Filed+Submitted, `:396-402`). Unpost relies on `IsDocumentFilingLockedAsync`, which only looks at `FilingLockedAt` (`TaxService.RdCompliance.cs:221-229`).
  - So a ภ.พ.30 that was only *declared* (status `Submitted`, no number) does not stop Unpost. Output VAT that was already declared is reversed in the books, and the books and the return drift apart silently.
  - For WHT: `WithholdingTaxCertService.VoidAsync` (`:318-325`) has no guard at all. Unpost voids a 50 ทวิ that was already filed in ภ.ง.ด.53 and remitted (the payee already holds it), and the payout-JE reversal flips 21917 to a debit balance.
  - The next Post is then blocked by `TaxPeriodFiled` (`SettlementPosting.cs:283-286`), so the batch sits unposted for a month that was already filed.
- Fix: a pure `SettlementUnpostGate`, fed facts for every document, payment and certificate, run **before step 1**. It should check: e-Tax Accepted, `IsDocumentFilingLockedAsync`, VAT month Declared/Filed for sale and fee docs, the ภ.ง.ด.53 month and the certificate's filing, and the ภ.พ.36 month. Void in a fixed order: the documents most likely to be refused first, payments last. Add a negative test: an Accepted e-Tax summary means Unpost refuses and nothing is touched.

### C-3 · CONFIRMED · P2: a 50 ทวิ can stay Draft forever after a crash between Create and Issue
- `SettlementPostingService.cs:177-184` calls `_whtCerts.CreateAsync` (commits a Draft) and then `IssueAsync` (a separate SaveChanges).
- If the process crashes or throws between the two, the retry's guard is `AnyAsync(DocumentId == docId && Status != Voided)`. That is true for the Draft, so `IssueAsync` is never called again.
- `WhtCertFilingScope.Filed` counts only Issued/Printed. The 21917 credit sits in the payout JE, but the certificate is missing from ภ.ง.ด.53 and from the remittance.
- Fix: look up the existing certificate. If it is Draft, call `IssueAsync`. If it exists, check that `TotalTaxAmount` equals `fee.WhtAmount`.

### C-4 · CONFIRMED · P2: stale-work detection covers documents but not payments (receipts)
- `EnsureReceiptAsync` (`:213`) and `ReceiptTargets.AlreadyReceived` (`:434`, `SettlementPosting.cs:317`) key on `DocumentId` only. The `StaleDocument` check (`:450-462`) looks only at `ExistingDocs`.
- Scenario: run 1 records a 1,000 receipt on invoice X and then fails later. The user rematches that line to invoice Y (B allows this, see C-1) or adds a 500 line for X. Run 2:
  - The orphan payment on X either stays and is counted in `Items`, or X is skipped at 1,000 instead of 1,500.
  - `Posted` is stamped with the clearing account off by that amount.
- Fix: for each marker payment, compare `(DocumentId, Amount)` against `plan.Receipts`. Any mismatch is a blocking `StaleDocument` issue.

### C-5 · CONFIRMED · P2: `CommitPostedAsync` stamps `Posted` without checking that every planned piece exists and is issued
- `:306-315` just collects whatever live documents and payments exist. A fee PV or summary voided through the normal document UI between the try-block and commit (no lock prevents it), or at any time after `Posted`, leaves `Posted` with the component missing. This is the R1 "status stamped by the system itself" class.
- After `Posted`, voiding a settlement document through the normal UI is also not refused. `VoidDocumentAsync` does not know about the marker. That is recoverable only via Unpost.
- Fix:
  - Inside the `FOR UPDATE` transaction, assert that every `FeeComponent`/`SummaryComponent` of the plan has exactly one live, issued document with the expected total, and that every receipt has a payment.
  - Add a void guard in `DocumentService` for documents whose `CreatedBy` starts with `system:settlement:` ("unpost the batch").

### C-6 · CONFIRMED · P2: summary "tax invoice" is silently downgraded to a receipt (REC series) or an abbreviated invoice, and the gate never consults `AbbreviatedTaxInvoiceRule`
- `SettlementDocumentBuilder.SummaryDocument` (`SettlementPosting.cs:449-462`) issues `TaxInvoice` + walk-in buyer. `PdfGenerationService.IsAbbreviatedTaxInvoiceDoc` (`:1661-1673`) treats a walk-in plus VAT as **abbreviated**, and only when `AbbreviatedTaxInvoiceRule.CanIssue(... Document)` is true (the company is `IsRetailApproved`).
- Otherwise the heading falls back to "ใบเสร็จรับเงิน" and the number goes to the REC series (`TaxInvoiceSeriesPolicy`). This is exactly the round-191 defect "หัวลดเงียบ ๆ".
- A non-retail VAT registrant (the common marketplace seller) therefore gets a daily document that is not a §86/4 tax invoice, while its VAT still flows into ภ.พ.30. Nothing in the plan or the gate says so. The team C docstring says "ใบกำกับภาษี/ใบเสร็จรับเงินใบเดียว".
- Fix: add a non-blocking `SettlementPlanIssue` (or a blocking one pending an owner decision) from `AbbreviatedTaxInvoiceRule.Judge(..., AbbreviatedInvoiceChannel.Document)`, with the heading the document will actually get. Put it on the owner-decision list: daily summary vs §86/4 per-sale for non-§86/6 companies.

### C-7 · CONFIRMED · P2: the approver identity is the system string, and SoD (maker ≠ checker) is bypassed in substance
- A PV Cash is auto-approved **inside** `CreateDocumentAsync` with `approvedBy = createdBy = "system:settlement:<batch>:fee-…"` (`DocumentService.cs:1619-1633`). So fee PVs record the system as approver, not the user who clicked. That contradicts the comments at `SettlementPostingKeys.CreatorPrefix` (`SettlementPosting.cs:23`) and at `:247-249`.
- With `SodBlockSelfApproval=true`, the maker/checker comparison (`DocumentService.cs:5948-5953`) compares `"system:…"` with the user, so it never trips. The same user who imports, classifies and presses Post also "checks" every generated document.
- Fix: pass `createdBy = userId` and carry the marker in a dedicated column or in `InternalNotes` (the latter needs care: see the flag-field overwrite checker). Otherwise make SoD compare against the batch's importer or classifier. Either way, record `ApprovedBy = userId` explicitly.

### C-8 · CONFIRMED · P2: no permission check inside the service for bank match, chargeback JE, payment void or JE reversal
- `MatchBankTransactionAsync` (`:739-809`), `ResolveChargebackAsync` (`:813-899`, which posts a JE) and the payment voids and JE reversal in Unpost (`:655-698`) do no permission check. Only `CanVoid` on document types (`:614-616`) and `CanApprove` in the gate are checked.
- The team C commit says the controller (team D) will add `settlement.post`. Until then any caller of the service can post chargeback JEs. This is the R5 "other entry points skip the gate" class once a second caller exists (LINE, mobile, job).
- Fix: check the permissions in the service (bank reconcile, JE post, payment void), and lock them with `required_call_site_check`.

### C-9 · CONFIRMED · P2: the duplicate-summary gate blocks a legitimate second payout for the same sale date
- `DuplicateSalesAsync` part (1) (`:537-551`) blocks whenever **any** other live batch of the channel already has a `sum-yyyyMMdd` document.
- Marketplaces release orders of one day across several payouts. If `TxnDate` is mapped to the order date, which is common with `GenericColumnMapAdapter`, every later payout containing that day is blocked. The only way forward is to issue documents by hand.
- DECISIONS item 2 ("1 เอกสาร/วัน/แพลตฟอร์ม") conflicts with the payout data model. This needs an owner decision: a supplementary summary document for the same day, or the order date vs the release date as the tax point.

### C-10 · CONFIRMED · P3: the message at `:238-241` says "ยกเลิก/ลบเอกสารร่าง" for `SETTLEMENT-POST-AMOUNT`, but a fee PV is already approved
- For a PV, `CreateDocumentAsync` approves before returning, so the amount check runs after numbering and the JE. The document is issued, not a draft. The next retry then fails with `SETTLEMENT-POST-STALE` until the user voids the document.
- Fix: check the total before approval by computing it with `DocumentService.PreviewTotals`, or say "ยกเลิก" for issued documents.

### C-11 · CONFIRMED · P3: the filing-lock gate misses ภ.พ.36 and ภ.ง.ด.3
- `:396-402` loads only `TaxType.VAT` and `WithholdingTax53`.
- Fee documents with `SelfAssessedPp36`/`...NotClaimable` credit 21912 on the payout day. A declared `VatPp36` month is not checked.
- `ResolveWhtFormType` can move the certificate to ภ.ง.ด.3 when the platform contact is an individual. That month is not checked either.

### C-12 · CONFIRMED · P3: a receipt on a matched invoice can pick up a proportional WHT
- `ReceiptPayment` leaves `WithholdingTaxAmount` null (`SettlementPosting.cs:482-486`, and the test even asserts null). `CreatePaymentAsync` then computes `Amount × doc.WHT / doc.Total` when the invoice carries customer WHT (`DocumentService.cs` around 12153-12220).
- The platform never withholds, so pass `WithholdingTaxAmount: 0m` explicitly.

### C-13 · CONFIRMED · P3: `WalkInCustomerContact.GetOrCreateAsync` is check-then-insert with no lock or unique index
- Two channels posting at the same time (different channel locks), or the integration path, can create two walk-in rows. The helper's own comment says this breaks `FirstOrDefault(IsWalkInCustomer)`.
- Fix: take an xact advisory lock keyed on company + "walk-in", or add a partial unique index.

### C-14 · CONFIRMED · P3: ResolveChargeback does not take the channel lock, so it races Unpost
- Unpost checks for an open chargeback JE with `AsNoTracking` before it acts (`:618-629`). A `ResolveChargebackAsync` running at the same moment uses only a per-line xact lock (`:848-849`).
- The resolution JE can land after Unpost's check. The batch returns to `Matched` with the dispute already closed, a re-post parks the dispute again, and `ResolveChargeback` then answers "ปิดไว้แล้ว". The dispute account is left stuck.
- Fix: take the channel `settle-post` lock in ResolveChargeback.

## PLAUSIBLE

- **C-15 · P2: stock/COGS not moved.** The summary document has no product lines, so inventory sellers overstate inventory and understate COGS for every auto-summary sale. It is disclosed only in `InternalNotes` (`SettlementPosting.cs:469-470`), not as a plan issue. Add a non-blocking issue for `BusinessType ∈ {Trading, Manufacturing}` (§87(3) companies).
- **C-16 · P2: cut-off and WHT timing.** Fee PVs, input VAT, 50 ทวิ and ภ.ง.ด.53 month all use `payoutDay` (`SettlementPosting.cs:442, 488-499`). Fees are deducted continuously over `PeriodFrom..PeriodTo`, so a period that crosses a month end books September fees and WHT in October. Under ท.ป.4/2528, WHT falls at the payment time. Needs an owner or accountant decision: split by `TxnDate` month, or accept.
- **C-17 · P2: refund deadlock for orders in a same-batch summary.** A refund line for an order that is going into the summary created by **this** batch cannot be matched: the summary document does not exist yet, and B requires an issued `RefundTargetTypes` document. So `RefundUnmatched` blocks and there is no way forward in the tool. This sits across team A and team B; C's gate adds `RefundNeedsCreditNote` for every refund anyway.
- **C-18 · P3: forgeable payment marker.** Any user who records a payment with `Notes` containing `[SETTLEMENT:<batchN>]` is adopted as the batch's receipt (the receipt is skipped and the clearing account stays under-debited) and is voided by Unpost. The batch id is visible in URLs. Use a dedicated FK column (`Payment.SettlementBatchId`).
- **C-19 · P3: no "abandon partial post" operation.** After a partial failure the only way back is voiding documents one by one. Unpost requires `Posted`.
- **C-20 · P3 (latent): no change-tracker reset in the execution-strategy lambdas.** `CommitPostedAsync`, `UnpostCoreAsync` and `MatchCoreAsync` run inside `strategy.ExecuteAsync` without `ChangeTracker.Clear()`. That is harmless today because no retry strategy is configured, but it would double-insert the JE if `EnableRetryOnFailure` is ever turned on. B's lambdas do clear.

## NOT-A-BUG (checked)
- **Tenant isolation.** Every query carries `CompanyId`. All account ids go through `SettlementChartIndex`, built from this company's chart only: channel, `FeeAccountMap`, line override, clearing, and the bank GL via `BankAccounts.CompanyId`. A foreign id gives an error, never a fallback. The counterparty, receipt documents, intents, payments and bank transaction are all company-filtered.
- **Dr=Cr.** Every plan leg is a balanced pair. `JournalEntryBuilder.PostAsync` enforces balance and the closed period. The clearing credit on a fee PV equals `Deducted` in every VAT treatment: `ForeignServiceVat.SplitCredit` credits the base, and NotClaimable puts deducted + pp36 on expense with pp36 to 21912. That is consistent with `ClearingMovement`.
- **Posted without a JE.** This happens only when `PayoutJournal` is empty (NetPayout 0 and no direct or WHT legs). An unresolved leg is always an error and blocks.
- **Double post from two users or instances.** They share the same channel session lock, and there is a re-check under `FOR UPDATE`. Crash-and-retry of a document is resumed via `CreatedBy` (written in the same INSERT). Markers include the batch GUID, so there is no collision across batches or tenants.
- **WHT double-deduction.** Fee document lines have WHT rate 0, so `WithholdingTaxAmount = 0` and the approve-time `AutoGenerateFromDocumentAsync` does not fire. The certificate amount equals the positive-line set of the 21917 legs (the R-A3 fix).
- **Numbering.** Numbers are assigned per document at approval in its own transaction, so there is no gap. Drafts keep `DRAFT-guid`.
- **BankMatched (R1).** It requires a real bank transaction of this company, on the same bank account, in the same direction, with an exactly equal amount, not linked to another batch. The transaction side is stamped by `IBankService.ReconcileAsync`, the owner, inside the same transaction. JE reversal clears any bank match on the original (`AccountingService` FIX #4).
- **No `catch {}`.** Both catch blocks log and rethrow a 409 with the step name. Rounding uses `AwayFromZero`.
- **Compile scan (static only).** Checked: record positional and named args (`DocumentLineRequest`, `CreateDocumentRequest`, `CreatePaymentRequest`, the WHT DTOs), overloads (`ApproveDocumentAsync(..., ApprovalAckSource, bool)`, `ThaiDate.CalendarDateUtc(DateTime)`), `IBankService.ReconcileAsync`/`UnmatchTransactionAsync`, the `JobLock` signature, `BusinessRuleException(msg, inner, code, status)`, enum values (`EWallet`, `PayAlways`, `Interest`), namespaces (`Models.DTOs.Document` resolves to `Accounting.Models`; no `Accounting.Services.*.Models` shadow) and the `IntegrationService` `using Accounting.Helpers`. **No compile error found.** A compiler still has to confirm this.

## Missing tests (opposite direction and negative)
The pure helpers are well covered. None of the following is tested, and a DbContext-level test (or a pure `SettlementUnpostGate` plus a commit-completeness check) is needed:
- void-after-partial and re-import (C-1)
- Unpost with an Accepted e-Tax, a declared ภ.พ.30, or a filed ภ.ง.ด.53, where nothing may be touched (C-2)
- a certificate stuck in Draft (C-3)
- a stale receipt amount or document (C-4)
- a fee document voided before or after commit (C-5)
- a non-retail company getting the summary heading (C-6)
