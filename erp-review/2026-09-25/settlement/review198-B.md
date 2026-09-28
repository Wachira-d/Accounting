# Round 198 · phase 1 team B · adversarial review (F3 #11)

- **Target:** commit `71c8fdeb` on local branch `worktree-agent-a988114fcb9a56cca` (base `8e32582a`). The review is read-only. Nothing in the repo was edited or committed.
- **Method:** read every file in the diff at the commit (`git show 71c8fdeb:<path>`), plus the callers and contracts they depend on: `AiOrchestrator`, `AiFeedbackRecorder`, `GenericFeedbackDistillationModel`, `GatewayAccountResolver`, `GatewaySettlementService`, `FileAttachmentService`, `SettlementBatchMath`, `SettlementLineTypeRules`, the entities and indexes.
- **Checkers run on a `git archive` copy of the tree:** all green except `dead_helper` (`PlanChargebackResolution`, which belongs to team C, as the commit already states). The green ones are `settlement_adapter_boundary`, `required_call_site`, `using`, `record_arg`, `nullable_arg`, `accessibility`, `tuple_name_merge`, `undeclared_local`, `regex_line_span`, `advisory_lock_key`, `terminal_status_writer`, `contact_taxid_only_match`, `attachment_gate`, `dto_nullable_contract`, `service_interface`, `di_cycle`, `write_permission_gate`, `approved_status_writer` and `gl_code`.
- **Not verified:** nothing was compiled, because there is no .NET SDK in this environment.

Status legend: **CONFIRMED** means the code path was read end to end. **PLAUSIBLE** means the logic is real but depends on data or configuration that was not seen. **NOT-A-BUG** means the item was checked and is fine.

---

## Summary: P0/P1

| ID | Status | Sev | One line |
|---|---|---|---|
| R-B1 | CONFIRMED | **P1** | `ReclassifyLineAsync` with "apply to same label", and a reclassify of the line itself, re-runs automatic matching and **silently overwrites the user's manual match** (`AssignLineMatch`). A line the user pinned to an invoice can fall back to `AutoSummary`, so the same revenue is counted twice. |
| R-B2 | CONFIRMED | **P1** | A late refund in a CSV for a gateway channel (the intent is already in an earlier batch) comes out `Unmatched` with a **false message**: "this payment was never refunded through the system". Following it leads to a second credit note for a refund that `GatewayRefundService` has already booked. |
| R-B3 | CONFIRMED (order) | **P1** | `VoidBatchAsync` and `RematchBatchAsync` read the batch and check `IsEditable` **before** taking the channel lock (TOCTOU). A void that waits behind a posting can soft-delete a batch that is already **Posted** and un-stamp its intents, so they can be settled again. |
| R-B4 | PLAUSIBLE | **P1** | "No trace, so AutoSummary" is decided **without looking** at intents when the channel is Gateway without a config, or its clearing account has drifted from the resolver. Sales already recorded through PaymentIntent then become a daily summary sale, and revenue is counted twice. This breaks the doctrine "a condition that is false for lack of data must not pass" (DOCTRINE §1). |
| R-B5 | CONFIRMED (mechanism) | **P1/P2** | The dedupe key can **silently drop genuinely different rows** across files: same id and a label that differs only in digits, or rows without an id that match on (order, label, amount, date). The only safety net is the batch equation, which becomes a tautology when the user types the wallet balances (R-A12). |

P2 and P3 are listed below: R-B6 through R-B22.

## สถานะการแก้ — ทีม S3 รอบ 198 (ติ๊กไม่ลบแถว · sha เติมในคอมมิตตามหลัง)

| ID | สถานะ | แก้ที่ / เหตุผลที่ยังค้าง |
|---|---|---|
| R-B1 | ✅ <pending> | `SettlementLines.MatchDecidedByUser` (migration `ADD COLUMN IF NOT EXISTS … DEFAULT false`) · `SetMatch` ติดธง · `MatchLinesAsync` ไม่เขียนทับบรรทัดที่คนตัดสิน · `RematchChangedAsync` คงคำตัดสินเมื่อยังอยู่กลุ่มการจับคู่เดิม (`SettlementSaleMatch.KeepUserMatch`) · "ใช้กับป้ายเดียวกัน" แตะเฉพาะบรรทัดที่ประเภทเปลี่ยนจริง · `RematchBatchAsync` ข้ามใบสรุปที่คนยืนยัน · echo ใน `SettlementLineView.MatchDecidedByUser` |
| R-B2 | ✅ <pending> | `SettlementSaleMatch.IntentCandidate` — ต้นทางคืนเงิน = `RefundedAmount` − Σ บรรทัดคืนเงินที่อ้าง intent ในทุกรอบ (ไม่ดูว่ารอบไหนเป็นเจ้าของ) · จัดสรรทีละบรรทัด `ApplyIntentRefundCapacity` · เหตุผลแยก 3 สาเหตุ · ผู้ลงบัญชี: บรรทัดคืนเงินของ intent ที่รอบก่อนเป็นเจ้าของ **ไม่ใช่** "นับซ้ำ" (`IntentSettledElsewhere` — เดิมบล็อกทุกบรรทัดคืนเงินภายหลังรวมเส้น PaymentIntent) · `CommitPostedAsync` ไม่ย้ายเจ้าของ intent (`??=`) |
| R-B3 | ✅ <pending> | ทุกเส้นแก้ข้อมูล (นำเข้า · จัดประเภท · จับคู่ · จับคู่ใหม่ · ยกเลิก) ล็อก `SettlementChannelLock` **ก่อน**โหลด/ตรวจ (`LockChannelAsync` → `LoadEditableBatchAsync`) · ล็อกตัวเดียวกับผู้ลงบัญชี (C-1) |
| R-B4 | ✅ <pending> | ค้น intent ด้วย `ProviderRef` ทั้งบริษัทเสมอ · พบแต่ใช้ไม่ได้ (ไม่ผูก gateway นั้น/ผังพักไม่ตรง/เส้นเดิมบันทึกแล้ว) ⇒ `Unmatched` + เหตุผล ไม่เคย `AutoSummary` · `Reference`/`SourceReference` เทียบตัดช่องว่าง-ไม่สนตัวพิมพ์ · **ค้าง**: ค้นใน `ReceiptVoucher`/`BookingNumber` (backlog — ชนิด/ช่องใหม่ต้องตัดสินว่าเป็นใบรับชำระได้ไหม) · ไม่บล็อกนำเข้า CSV ของช่องทาง Gateway ที่ไม่ผูก config (ไม่จำเป็นแล้ว — ค้น intent เสมอ) |
| R-B5 | ✅ <pending> | `SettlementTxnKey` รุ่น `v2:` — มี id: id + แฮช(ป้าย·ยอด·วันที่) · ไม่มี id: แฮช(ออเดอร์·ป้าย·ยอด·วันที่·**รอบโอน**) · `#n` เฉพาะแถวที่เหมือนกันทุกช่อง · แถวที่ถูกข้ามเตือนรายแถว + รอบโอนที่มีอยู่ · ไม่มีข้อมูลเดิมที่ต้องย้าย (ยังไม่มี controller/หน้าจอเรียก service นี้) |
| R-B6 | ✅ <pending> | ป้ายในคีย์ผ่าน `SettlementTxnKey.FrozenLabel` ของคีย์เอง (ไม่ใช้ `NormalizeLabel`/ตัวตัด PII) และถูกแฮช · คำนำหน้ารุ่น `SettlementTxnKey.Version` · **ค้าง**: golden test ของไฟล์ตัวอย่าง (รอไฟล์จริงจากเจ้าของ — adapter เฉพาะเจ้าเขียนเมื่อมีไฟล์จริงเท่านั้น) |
| R-B7–R-B11 | backlog | ตัวอ่านไฟล์ (xlsx เลขยาว · เขตเวลา · ลำดับวัน/เดือน · วงเล็บ+ลบ · แถวสรุปแบบกว้าง · เพดานแถว) — ไม่อยู่ในขอบเขตรอบนี้ (ทีม S3 = P1 ของ import/match + posting) |
| R-B12 | ✅ <pending> | ขายผ่าน intent: ยอดของออเดอร์ ≠ ยอดที่รับชำระ ±0.01 ⇒ `AmountMismatch` (ผูก intent ไว้ให้คนตรวจ) · **ค้าง**: intent ที่บรรทัดขายอื่นอ้างแล้ว (ในรอบเดียวกันกลุ่มออเดอร์เดียวกัน = ตั้งใจ) |
| R-B13 | backlog | รับชำระเกินข้ามรอบโอน — ต้องให้ผู้ลงบัญชีรวมยอดที่จับคู่ใบเดียวกันในรอบอื่นที่ยังไม่ลง (ด่าน `ReceiptDocumentNotPayable` กันได้แค่รอบที่ลงแล้ว) |
| R-B14 | ✅ <pending> | `ParseClassifierAnswer` ต้องเป็นชื่อเดียวตรงตัว (`Enum.GetNames` ก่อน `TryParse`) |
| R-B15 | backlog | แยก "เรียก AI แล้ว" กับ "แสดงคำแนะนำ AI" + warn-gate — งานของหน้าจอทีม D |
| R-B16 | ✅ <pending> บางส่วน | `CorrectFeeAsync` ของ intent ที่อยู่ในรอบโอนแล้ว ⇒ ปฏิเสธพร้อมทางไปต่อ (`SettlementSaleMatch.FeeEditBlockedByBatch`) · **ค้าง**: ตัวนับ "legacy refund" `:129` และ `isSettled` ของ `PaymentGatewayController` (ไฟล์ของทีม E — แจ้งทีม E3) |
| R-B17 | ✅ <pending> บางส่วน | คืนเงินภายหลังนับยอดในบรรทัดของ**ทุกช่องทาง** (intent เป็นของบริษัท) ⇒ ช่องทางที่สองไม่ได้บรรทัดคืนซ้ำ · **ค้าง**: 1 ช่องทาง Gateway ต่อ config (กติกาของ `SettlementChannelService` — ต้องตัดสินว่าร้านหลายร้านบน gateway เดียวเป็นไปได้ไหม) |
| R-B18 | backlog (มีตาข่าย) | ผู้ลงบัญชีบล็อกอยู่แล้ว (`ResolveMoneyInAccountAsync` คืน null ⇒ `ClearingSourceMismatch`) · ห้ามผูกช่องทางกับ provider ที่โอนตรงเข้าธนาคาร = งานของหน้าตั้งค่าช่องทาง |
| R-B19–R-B22 | backlog | ผังพักที่เป็นลูกหนี้จริง · ค่าธรรมเนียมประมาณการ · วันที่ของบรรทัดคืนเงิน · `Kind` ค่าเริ่มต้น — ไม่อยู่ในขอบเขตรอบนี้ |

---

## 1. Dedupe key (`SettlementTxnKey`)

### R-B5 · CONFIRMED (mechanism) · P1/P2: two different rows get the same key and one is dropped silently

`Helpers/SettlementTxnKey.cs:39-57` · `Services/Settlement/SettlementImportService.cs:234-246, 279`

- **Row with an id:** the key is `id|NormalizeLabel(label)`, and `NormalizeLabel` **strips every number** (`SettlementLineClassification.cs:40,49`). Rows that differ only in amount, date or a number inside the label get the same key.
  - Scenario A: file 1 contains `(T1, "Refund", −100)`. Next period, file 2 contains `(T1, "Refund", −50)`, a second partial refund. This happens when the user maps the order-number column as the txn id, or the platform reuses the txn id. Both rows get key `T1|refund`, so the second is dropped.
  - Scenario B: labels such as "Adjustment 1" and "Adjustment 2", or "ค่าธรรมเนียม 3%" and "ค่าธรรมเนียม 5%", collapse to one key.
- **Row without an id:** the key is `row:` + SHA(order, label, amount, date). The PayoutRef is deliberately left out. Two payouts on the same day that each have a `"Withdrawal fee", −10.00` row with no order id get the same key, so the second batch loses its fee.
- **The `#n` suffix depends on neighbouring rows in the same file**, which partly brings back R-A9. Suppose file 1 has K and K#2, and file 2 contains only a new row identical to K. That new row gets K, collides, and is dropped.
- **Why it is silent:** the only feedback is `SkippedDuplicates` (a number). A warning appears only when *every* row is a duplicate (`:305`). The real safety net is `SettlementBatchMath` `Unbalanced`, but the opening and closing wallet balances come from the header the user types (default 0). If the user "fixes" the gap by changing the balance, the missing row is never seen again (R-A12).
- **Fix:**
  - When there is an id: `id|label|amount|date` (digits kept), with `#n` only for rows that are identical in every field.
  - When there is no id and the row carries a PayoutRef column: include the row's own PayoutRef in the basis. The same row always has the same payout ref, so this is safe.
  - Return a per-row list of dropped rows ("row n = already in batch X") and warn whenever `skippedDup > 0`.

### R-B6 · CONFIRMED · P2: the key depends on two heuristics that are going to change (the PII scrubber and `NormalizeLabel`)

`SettlementImportService.cs:235` (`SettlementPiiScrubber.Scrub(r.RawTypeLabel)`) → `SettlementTxnKey.cs:42,49`

- Changing any regex in the scrubber or `NormalizeLabel` changes the keys of rows that were already imported. `NormalizeLabel` is also the key for the seed, the channel library and the AI fingerprint, so it will be tuned again.
- After such a change, importing the same file (or an overlapping period) **does not dedupe**, and revenue and fees are duplicated. The net is again the batch equation, which is weak as described in R-B5.
- **Fix:** freeze the key algorithm in its own function with a version prefix (`v1:`). Hash the raw label with a separate, frozen normaliser that does not share a regex with the scrubber or the classifier. Add a golden test that pins the keys of the sample CSV.

### R-B7 · PLAUSIBLE · P2: xlsx id cells stored as numbers lose precision

`Services/Settlement/Adapters/SettlementFileReader.cs:168` (`double d => d.ToString("R")`)

- Order ids of 16 or more digits (TikTok Shop order ids are 18 digits) come out as `1.2345678901234568E+17`. Different ids collapse to one double, which gives R-B5 collisions. They also never match `Document.Reference`, which gives `AutoSummary` and R-B4.
- **Fix:** for the txn, order and payout columns, reject cells that are numbers with more than 15 significant digits or exponent format. Fail the whole file with a message such as "the order-number column is an Excel number and has lost precision; export as CSV or text".

## 2. Parsing money and dates

- **R-B8 · PLAUSIBLE · P2: timestamps without a zone are always treated as Bangkok**
  - `SettlementValueParser.cs:124-141` (the NumericDate path ignores the time).
  - This includes Excel `DateTime` cells (`CellText` → `"yyyy-MM-dd HH:mm:ss"`). Reports that export UTC without a `Z` (for example "Created (UTC)" columns) put a sale at 2026-09-30 18:00 UTC (01:00 on 1 Oct in Bangkok) on 30 Sep, so the tax point and summary sale land in the wrong VAT month.
  - Fix: add an `InputTimeZone` option to `SettlementColumnMap` (Bangkok or UTC), and detect "(UTC)" / "GMT" in the header.
- **R-B9 · PLAUSIBLE · P2: the day/month order is decided silently when the file is ambiguous**
  - `SettlementValueParser.cs:100-113` + `GenericColumnMapAdapter.cs:76-78`.
  - A file covering days 1–12 of a month has no evidence of order, so it defaults to day/month. The detected order is never written back into `DateOrder` in the saved map.
  - A month/day file for a short period swaps day and month (5 Sep becomes 9 May) with no warning.
  - Fix: when there is no saved order and the file is ambiguous, fail with "choose the date format" (or show samples in Inspect), and save the order once a file proves it.
- **R-B10 · CONFIRMED · P3: `"(-100)"` parses as +100**
  - `SettlementValueParser.cs:40-45`: the parentheses and the minus sign cancel each other. This should be rejected.
- **R-B10 · CONFIRMED · P3: `"1,500"` in a decimal-comma locale is read as 1500**
  - Loud failures cover `1.234,56`, but `1,500` is accepted. This is rare in Thailand. A `;` delimiter strongly suggests an EU locale, so that is a cheap signal to warn on.
- **R-B11 · PLAUSIBLE · P2: summary rows in wide files are not caught**
  - `GenericColumnMapAdapter.cs:33-34, 86-90`: summary rows are recognised only when the txn and order cells are empty **and** the first cell starts with "รวม/total/sum".
  - In a wide file, a summary row with a different label ("Net", "ยอดสุทธิ", "Summary") or a blank first cell is imported as real lines. Revenue and fees are doubled, and the only net is the equation.
  - Fix: in wide layout, a row with no order id and no txn id should always be skipped with a notice, or should fail the file.
- **P3: no row cap (memory)**
  - A 25 MB xlsx that decompresses to millions of rows is held in `raw` (a list of dictionaries) and then as millions of `SettlementLine` in one transaction.
  - Fix: add a row cap (for example 200k) with a Thai error message.
- **NOT-A-BUG:**
  - Thai digits, Buddhist-era years (≥ 2400 → −543), two-digit years failing loudly, parentheses, trailing minus, ฿/THB, rounding `AwayFromZero`.
  - Formula cells: MiniExcel returns the cached value. With no cached value the cell is blank and the row goes to the skipped list, or a formula string fails loudly.
  - `CodePagesEncodingProvider` 874 with strict decoding.

## 3. Sale matching

### R-B1 · CONFIRMED · P1: reclassify re-runs matching and overwrites the user's manual choice

`SettlementImportService.Lines.cs:274-291` → `RematchChangedAsync :333-346` → `MatchLinesAsync(apply:true)`

- The filter for "same label" (`:281-284`) selects every line with `ClassifiedBy != User`, **even when its type does not change**. `Apply()` is called and the line is sent back to `MatchLinesAsync`. The line the user is reclassifying is also re-matched.
- `MatchStatus`, `MatchedDocumentId` and `PaymentIntentId` are rewritten from `Decide`. Nothing records that a human already decided.
- **Scenario:**
  1. Line A ("Order income", `Learned`) has an order id that matches nothing. Auto-matching gave `AutoSummary`.
  2. The user sees that the revenue is already on INV-9 (a different Reference) and runs `AssignLineMatch`, which sets it to `Matched` → INV-9.
  3. Later the user reclassifies line B with the same label and "apply to same label".
  4. Line A is re-matched → `AutoSummary`. The batch becomes `Matched` and is ready to post.
  5. Team C creates a daily summary sale **as well as** INV-9. Revenue and output VAT are doubled.
- `RematchBatchAsync` (`:435-436`) also reopens `AutoSummary` lines the user chose explicitly.
- **Fix:**
  - Add a `MatchDecidedBy` field (or `MatchStatus.UserAssigned`).
  - Never re-match lines a person decided unless their type changes into a group that no longer requires a match.
  - Exclude lines whose type does not change from the ApplyToSameLabel set.

### R-B2 · CONFIRMED · P1: a late refund through CSV is told "never refunded through the system", which invites a double credit note

`SettlementImportService.Lines.cs:87-92` (`free = … SettlementBatchId == null || == batchId`; `IsRefundTarget = free && RefundedAmount > 0`) + `Helpers/SettlementSaleMatch.cs:81-85`

- **Scenario:**
  1. On a gateway channel importing CSV, batch 1 matched the sale of charge X to intent X, so X is stamped with batch 1.
  2. The customer is refunded through our system (`GatewayRefundService` has already posted the refund JE).
  3. The next payout (batch 2) carries the refund line for X. Because X belongs to batch 1, `free = false`, so `IsRefundTarget = false`, so `Unmatched`, with the note: "รายการชำระนี้ยังไม่เคยคืนเงินผ่านระบบ — คืนเงินนอกระบบต้องเลือกใบขายเดิมเพื่อออกใบลดหนี้".
  4. The user follows the note, chooses the original invoice, and team C issues a credit note and posts the refund again. The refund is booked twice and the clearing account goes negative.
- The PaymentIntent path already handles this case (`LoadIntentRowsAsync` "late" query). The CSV path does not.
- **Fix:**
  - For refunds, an intent is a valid target when Σ refund lines that reference it is less than or equal to `RefundedAmount`, whichever batch the intent belongs to. `SyncIntentStamps` already keeps the first batch as owner.
  - Split the note by cause: "not refunded through the system" versus "belongs to another batch".

### R-B4 · PLAUSIBLE · P1: `AutoSummary` is decided without looking at the evidence the system has

`SettlementImportService.Lines.cs:68-72` (intents are searched only when Gateway + config + `GatewayClearingMatchesAsync`) + `SettlementSaleMatch.cs:68-72`

- `SettlementChannelService` allows a Gateway channel with no config (`:79-80` only checks the opposite direction). Clearing can also drift, when the payment config's `ClearingAccountId` changes after the channel was bound.
- In both cases the CSV path **skips intents** and lands on "no trace", which gives `AutoSummary`. But those sales already have revenue from the intent completion handlers, so revenue is counted twice. This contradicts the helper's own doc comment ("`AutoSummary` เฉพาะเมื่อไม่มีร่องรอยเลย").
- The trace search also covers only `Document.Reference` (exact, case-sensitive) in Invoice, TaxInvoice and Receipt. It does not cover `ReceiptVoucher` or `BookingNumber`.
- **Fix:**
  - Always search intents by `ProviderRef` within the company.
  - Found but not usable (clearing mismatch, no config): return `Unmatched` with a note, never `AutoSummary`.
  - Block CSV import for a Gateway channel with no config, or require the config.
  - Use a trimmed, case-insensitive comparison for `Reference`.

### R-B12 · CONFIRMED · P2: matching to an intent checks no amount (unlike matching to a document)

`SettlementSaleMatch.cs:93-94`

- A CSV sale line of 1,000 matched to an intent of 800, or several sale components of one order each matched to the same intent, are all `Matched` as "already in clearing".
- The difference stays in the clearing account (the R-A1 defect class). The batch equation does not catch it, because the lines come from the file and balance among themselves.
- The same intent can also be referenced by two sale lines in one batch (intent path plus CSV under the same PayoutRef).
- **Fix:** compare `orderGroupAmount` with `Amount − RefundedAmount` using ±0.01 and return `AmountMismatch`. Treat an intent that another sale line already references as not free.

**R-B13 · PLAUSIBLE · P2: over-receipt across batches.** Automatic and manual matching (`AssignLineMatch :397-402`) check only `BalanceDue > 0`, not the sale amounts already matched to the same document in other unposted batches. Two batches can each match 1,000 to one invoice of 1,000. Team C must guard at posting, or B must add up the lines already matched to that document.

**P3 · collisions between channels and ids.** Document candidates are not limited to the channel or counterparty. A short numeric order id can match an OTA `LodgingReservation.SourceReference`, or the `Reference` of another customer's invoice. When the amount happens to be equal, the result is `Matched` to the wrong customer. Probability is low.

**NOT-A-BUG: tenant isolation.** Every query in B has `CompanyId == companyId`: Documents, PaymentIntents, LodgingReservations, BankAccounts, ChartOfAccounts, Contacts, PaymentProviderConfigs and SettlementChannels (including the `IgnoreQueryFilters` ones). Raw SQL is only `pg_advisory_xact_lock`, and the key comes from `AdvisoryLockKey.For` (deterministic, tested). There is no way to attach a line to another tenant's document.

## 4. AI (iron rule #1)

- **R-B14 · CONFIRMED · P2: an answer naming two types is accepted as a third type**
  - `Helpers/SettlementLineTypeRules.cs:190-191` (`Enum.TryParse`), used by `AcceptModelAnswer` (B's gate).
  - `Enum.TryParse` accepts a comma list and ORs the values even on an enum without `[Flags]`. A model answer of `"Sale, Refund"` becomes 3 = **Chargeback**, and `"Commission, PaymentFee"` becomes 7 = **ShippingFeeCharged**. The result passes `ByType.ContainsKey`, and at ≥ 0.70 with a matching sign it is written to the line as `Ai`. This is an anti-hallucination bypass. A user-supplied `AmountColumn.Type` in the column map goes through the same path.
  - Fix: reject any answer containing `,`, or check `Enum.GetNames(...).Contains(a, OrdinalIgnoreCase)` before parsing.
- **R-B15 · CONFIRMED · P3: `ClassifyUsedAi = true` on lines the gate rejected**
  - `SettlementImportService.cs:508-509`: the line is still `Unclassified` but the UI will show "🤖 AI". `ListBatches.Ai` counts it too.
  - The rejected answer (for example confidence 0.6) is also thrown away instead of being shown as a suggestion (warn-gate, DOCTRINE §2).
  - Fix: separate "AI was called" from "an AI suggestion is being shown".
- **P3: payload fingerprint**
  - It contains no amounts, names or digits. It can still carry **letters from order ids** ("Order #ABC123" → "order #abc"). Labels that differ per row then create many fingerprints and use up the 25 AI calls per import.
  - The scrubber is a regex with no knowledge of names without a title, as its own doc says.
- **P3: honorific regex**
  - `SettlementPiiScrubber.cs:47` treats "DR"/"MS" (debit or product words) as a person's title. "DR Shipping fee" becomes "[ชื่อบุคคล] fee", the seed misses, and the line goes to AI.
- **P3: ReclassifyLine feedback**
  - `SettlementImportService.Lines.cs:298-313`: `RecordCallAsync` returns `Guid.Empty` when it fails (the recorder swallows the error), and `ClassifyAiFeedbackId = Guid.Empty` is stored. After that the loop never closes for that line.
  - When the insert fails in Postgres, the transaction is aborted, so the user's reclassification fails at commit.
  - `acceptedAi` (`:271-273`) is also true when the line became `Learned` through ApplyToSameLabel and happens to equal the user's choice, even though the model answered something else. This pushes the accept-AI metric up.
- **NOT-A-BUG:**
  - Student first: the orchestrator runs `TryPredictLocalAsync` itself.
  - Majority fallback: the `-majority` suffix plus the 0.45 cap mean it is never applied.
  - Kill-switch: `FallbackToLocal` → `AcceptModelAnswer` returns null → `Unclassified`, silently, plus a warning in the list.
  - `FeedbackId` is stored on every line of the group.
  - `RecordUserChoiceAsync(..., UserChoiceSource.Explicit)`.
  - The fingerprint on the write side and the learning side is the same payload (no `local_model` block).
  - The per-channel library counts only `ClassifiedBy == User`, so the system never teaches itself.
  - `GenericFeedbackDistillationModel` is registered for `SettlementLineClassify` (`Program.cs:633-640`).
  - AI calls happen outside the transaction and lock.

## 5. Transactions, locks, R1, permissions

### R-B3 · CONFIRMED (order) · P1: check-then-lock in void and rematch

`SettlementImportService.Lines.cs:461-463` (`VoidBatchAsync`: `LoadEditableBatchAsync` → then `pg_advisory_xact_lock`) and `:429-431` (`RematchBatchAsync`)

- The batch is loaded, tracked, and checked for `IsEditable` before the lock is taken.
- **Scenario:**
  1. Team C starts posting a batch, holds the lock, and takes a few seconds.
  2. The user presses "void". The request reads `Matched` (the posting has not committed) and waits for the lock.
  3. The posting commits with `Posted`.
  4. The void gets the lock and continues on the stale entity: `Status = Voided`, `IsDeleted = true`, soft-deletes the lines, and un-stamps the intents.
- The posted JE is left without a batch, and the intents can be put into a new batch or picked up by the old path. The bank or clearing account is then counted twice.
- If team C does not use the same lock, there is no serialisation at all.
- **Fix:** lock first, then load and check. Team C's posting must take the same `SettlementImport` lock on the channel. Consider an `xmin` concurrency token on `SettlementBatch`.

**R-B16 · CONFIRMED · P2: gateway fields in the old path are not all updated ("fixed in one place, the rest left").** The commit says the intent-selection condition of the old path "เหลือ 1 จุด", but there are three more places that do not know about `SettlementBatchId`:

- `GatewaySettlementService.cs:267` `CorrectFeeAsync` blocks only on `SettlementJournalEntryId`. The fee can be changed on an intent in a batch that is **posted or already imported**. The batch's "fee" line has already been snapshotted, and the key `pi:…:fee` means it is never re-imported, so the edit **has no effect, silently**.
- `:129`: the "legacy refund" count still includes stamped intents.
- `PaymentGatewayController.cs:123`: `isSettled` only checks the JE, so the UI shows an intent that is in a batch as "not yet settled".

**R-B17 · PLAUSIBLE · P2: two Gateway channels on the same config.** The clearing-account uniqueness check skips the Gateway kind (`SettlementChannelService.cs:127`). The "late" query in `LoadIntentRowsAsync` (`SettlementImportService.cs:170-174`) is not limited to intents in batches of *this channel*, and `refundInLines` counts only this channel's lines. So the same refund is imported in **both** channels. Fix: allow one Gateway channel per `PaymentProviderConfig`, and filter "late" by the channel of the owning batch.

**R-B18 · PLAUSIBLE · P2 (R-A1): providers that pay straight into the bank.** `GatewayClearingMatchesAsync` (`SettlementImportService.cs:569-575`) and `EnsureClearingAccountAsync` only compare the account from `ResolveClearingAccountAsync`. They ignore `IPaymentProvider.SettlesDirectlyToBank`, which makes `ResolveMoneyInAccountAsync` return null (the money went to the bank). A channel bound to such a provider sees its intents as "already in clearing", and team C posts clearing to bank a second time. Fix: gate on the same resolver the money-in leg uses (or a new method `IsClearedViaAccount`), and block binding a channel to such a provider.

**P3 items in this area:**

- **Orphan file and retries.** `UploadBytesAsync` writes the file to disk before commit (`:418-427`), which the commit already acknowledges. An `ExecutionStrategy` retry writes the file again. The `warnings` list is captured outside the lambda, so a retry adds duplicate warnings.
- **Attachment gate.** `AttachmentAccessGate` `"SettlementBatch"` uses the query filter, so the source file of a voided batch can no longer be opened. The physical file is kept (`AttachmentRetention`, unknown type). This is acceptable but should be documented.
- **Stale line.** `ReclassifyLine` and `AssignLineMatch` read the line before the lock. The batch is read after the lock, so the posted check is correct, but the line may be stale (a small lost update).

**NOT-A-BUG:**

- Lock ordering (channel → provider) can only happen one way, so there is no deadlock.
- `23505` becomes Thai text.
- Void = soft-delete, matching the filtered unique indexes.
- R1: `DeriveImportStatus` stamps only Imported, Classified or Matched, and never Posted or BankMatched. `terminal_status_writer_check` is green.
- Permissions: `Bank.Reconcile` for reading and deleting the source file (buyer PII) is suitable as an existing key, and `ClientUploadAllowed=false` is correct. What is not enough yet is that the service itself has no permission gate on reclassify, assign, void and rematch. That is the job of the team D controller, which must get a `write_permission_gate` row.

## 6. R-A1 / R-A2

- **R-A1:** the logic of `DecideGatewayClearing` is correct and tested (Gateway + config means the resolver's account only; no 1134x; loud failure on mismatch). The gaps are R-B18 (direct-to-bank) and R-B4 (clearing drift after binding, where the CSV path goes silent instead of failing loudly).
- **R-B19 · PLAUSIBLE · P2 (R-A2 hides real AR): a real AR control can be chosen as a channel's clearing account**
  - `SettlementChannelService.cs:118-126` rejects only code `11310`. The user can pick another AR control ("11311/11320 ลูกหนี้การค้า-…", or an account used as `Contact.DefaultArAccountId`), a bank account, or 11340 for a non-gateway channel.
  - Once chosen, `SubLedgerReconciliationService.cs:61-64` removes that account from AR reconciliation for good (including deleted channels, via `IgnoreQueryFilters`), so **the real AR variance disappears**.
  - Fix: reject accounts that `IsTradeReceivableControl` would treat as AR (unless they are 1134x created by the system), accounts used as a contact's `DefaultArAccountId`, and accounts linked to a bank.
- **P3:** `TradeReceivableAccount.cs:41` excludes 11341–11349 **by code**. That fits the system-created accounts, but a custom chart that uses 1134x for real AR (the templates do not use 1134x) would drop out of reconciliation. Excluding only accounts bound to a channel (the DB query already does this) is enough.

## 7. PaymentIntentAdapter

- **R-B20 · CONFIRMED · P2: an estimated fee is presented as a real fee line**
  - `PaymentIntentAdapter.cs:54`: `fee = FeeActual ?? FeeEstimated` creates a `PaymentFee` line classified by `AdapterRule`, looking exactly like a real fee. The class doc and the commit say "an unknown fee = no invented number".
  - The expense is posted at the estimate, and the equation is either unbalanced or "fixed" through the user-typed wallet balance.
  - Fix: use `FeeActual` only. When there is only an estimate, count it as unknown and let the payout difference show up as an explicit line, or mark the line `IsEstimated` and block posting.
- **P3:** the refund line uses the date of the **sale** (`ConfirmedAt`, `:58-62`), not `LastRefundedAt`. The date of the credit note and the period it lands in are wrong.
- **P3:** "late" refunds ignore `PeriodTo` and pull every refund up to now into this batch. The `to` boundary is `CalendarDateUtc(PeriodTo)+1` compared against a UTC instant, which extends the day to 07:00 the next morning in Bangkok. The old path has the same behaviour.

## 8. Compile risk (static reading only)

- **NOT-A-BUG:**
  - `MiniExcel` flows from `Accounting.csproj` to the test project transitively (no `PrivateAssets`).
  - `MiniExcel.SaveAs(ms, rows, excelType: ExcelType.XLSX)` and `MiniExcel.Query(stream, useHeaderRow:false)` exist in 1.34.
  - `CodePagesEncodingProvider` is part of the shared framework on net8, and `GetEncoding(int, EncoderFallback, DecoderFallback)` is a public virtual.
  - `AiRequest` is built with an object initializer that covers every `required` member.
  - The `internal` members (`DecideGatewayClearing`, `CellText`, `IsClearingCode`) are visible to the tests through `InternalsVisibleTo`.
  - `yield` never sits inside a try/catch.
  - `is A or B && x` has the right precedence.
  - `ThaiDate.CalendarDateUtc(DateTime?)` has an overload.
  - `BusinessRuleException(msg, inner, code)` exists.
  - `char.IsAsciiLetterUpper` is available on net8.
- **P3: R-A13 is closed in the comment but not in behaviour.** The comment claims R-A13 is closed ("body ที่ไม่ส่ง kind จะได้ 0 ⇒ ต้องตีกลับ", `SettlementChannelService.cs:71`). In fact `SettlementChannelUpsertRequest.Kind` defaults to `Marketplace`, so a body without `kind` becomes Marketplace, and on update it silently changes the kind of a channel that has no batches. Either remove the default or require the field.

## 9. Things team C/D must pick up (not B's bugs, but found here)

- Posting must take the same `SettlementImport` lock on the channel (R-B3).
- It must check Σ matched lines against the document balance across batches (R-B13).
- It must record implicit acceptance of AI-classified lines at approval time, to close the loop (DOCTRINE §3).
- The UI must `HtmlEncode` `RawTypeLabel`/`Description` (file data).
- The UI must show `SkippedDuplicates` and `SkippedRows` in full.
- Continuity check of the wallet balance, opening(n) = closing(n−1) (R-A12). Without it, R-B5, R-B6 and R-B11 have no real net.
