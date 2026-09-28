# Review of team E2's work in round 198: payment gateway (R-E2..R-E6 + E-2)

- **Scope:** `cbd50b37` + `3b9c00c3` (base `8104de53`), merged into `claude/erp-system-review-team-660mev` as `2d4ae9f1`.
- **Method:** read-only review (F3 #11). Line numbers are from HEAD `327249e3`. No code or commits were changed.
- **Checkers run here:** `write_permission_gate_check`, `owner_action_wiring_check`, `required_call_site_check` (299 rules + 12 negative tests), `record_arg_check`, `nullable_arg_check`, `undeclared_local_check`, `service_interface_check` and `tuple_name_merge_check` all pass. `using_check` and `accessibility_check` hit the 120 s timeout, so they did not finish.
- **Compile:** nothing was compiled; this environment has no .NET SDK.

## Summary

| ID | Status | P | Topic |
|---|---|---|---|
| ✅ <pending> E2-1 | CONFIRMED | **P1** | An HTTP 5xx/504 from Omise on a refund is treated as a definite failure, so the refund is not locked and a retry can refund twice. This is the same defect class E-2 fixed, reaching the same result by another route. |
| ✅ <pending> E2-2 | PLAUSIBLE | **P1** | The verify step unlocks with "NoMoneyOut" whenever the provider's refunded total equals ours. It has no minimum wait and no marker for the attempt, so a refund still in flight looks like "no money out", the lock opens and a second refund goes out. |
| ✅ <pending> E2-3 | CONFIRMED (logic) / PLAUSIBLE (field) | **P1** | If the provider sends no refunded total (field `refunded_amount` missing, or `ProviderRef` empty), the refund lock never clears and **every future settlement round for that provider is blocked**. Settled intents are selected without a date filter. No admin exit exists. |
| ✅ <pending> E2-4 | CONFIRMED | P1 | Recording a fee tax invoice has no duplicate guard on supplier tax id + invoice number, so the same invoice can be claimed repeatedly while the 11630 pool has room. The same invoice number then appears twice in the purchase VAT report. |
| ✅ <pending> E2-5 | CONFIRMED | P2 | The purchase VAT report line for a fee-VAT claim shows the claim date, not the invoice date. The invoice number comes from a regex and may be truncated or replaced by the JV number. The branch column is empty. |
| ✅ <pending> E2-6 | CONFIRMED | P2 | A fee-VAT claim only checks closed fiscal periods, not VAT months whose ภ.พ.30 is already declared or filed (`TaxFilingLockPolicy`). |
| ✅ <pending> E2-7 | CONFIRMED | P2 | "Verify" books the whole provider-vs-recorded difference as the uncertain refund. It never compares the difference with the amount of the attempt. After an E-1 case (refund went out, JE failed, JE then booked by hand) this books the same money twice. |
| ✅ <pending> E2-8 | CONFIRMED | P2 | The `RefundTimingUnknown` block tells the user to "ask an admin to record per-refund amounts", but no such tool exists. The test locks that dead end in place. The backfill could have parsed the amounts from the notes. |
| 📋 backlog E2-9 | PLAUSIBLE | P2 | The Bangkok-midnight cutoff on the payout date is a heuristic. When a boundary case is wrong, the only fix offered is to change the payout date, which posts the bank JE on the wrong day. |
| ➡️ S3 E2-10 | PLAUSIBLE | P2 | The team-B settlement import path (`SettlementImportService`/`PaymentIntentAdapter`) ignores `RefundOutcomeUnknownSince`: another entry point skips the E-2 check. |
| 📋 backlog E2-11 | PLAUSIBLE | P2 | R-E5 on old data: settled rows whose refund was deducted in a later round under older code have `RefundDeductedAfterSettlement = 0`, so reconciliation stays off by the refund amount. The value could be derived, but there is no migration. |
| 🔨 บางส่วน <pending> E2-12 | CONFIRMED | P3 | A group of small issues, detailed below. |
| — | NOT-A-BUG | — | Items checked and found fine, listed at the end. |

---

## E2-1 · CONFIRMED · P1: HTTP 5xx/504 on refund bypasses the E-2 "unknown outcome" lock

**Where:**
- `Services/Payments/Providers/OmisePaymentProvider.cs:243`: `if (!resp.IsSuccessStatusCode) return new ProviderRefund("", amount, false, FriendlyError(...))`
- `Services/Payments/GatewayRefundService.cs:180`: `if (!result.Succeeded) return Fail(...)` returns without stamping `RefundOutcomeUnknownSince`.

**Scenario:**
1. The user refunds 300.00.
2. The Omise backend records the refund, but the edge or a proxy answers `502/504 Gateway Timeout`, or a `500` after the commit.
3. The provider returns `Succeeded=false` and the service answers "ผู้ให้บริการปฏิเสธการคืนเงิน". There is no lock, no JE and `RefundedAmount` stays 0.
4. The user presses refund again and gets a 2xx.
5. Omise has now refunded 600 while the books show 300.

E-2 covers only exceptions (the 20 s timeout in `Program.cs:327`). A 5xx is "outcome unknown" for exactly the same reason.

**Fix:** have the provider return a three-way result (Succeeded / Rejected / Unknown), or throw for `>= 500` and `408`, so that `RefundCoreAsync` goes through `MarkOutcomeUnknown`. Only a 4xx carrying a clear error code counts as a definite failure. Add tests in both directions:
- 504 must lock.
- 400 `failed_refund` must not lock.

---

## E2-2 · PLAUSIBLE · P1: "NoMoneyOut" can unlock while the refund is still being processed (R1 self-stamp by timing)

**Where:** `Helpers/GatewayRefundMath.cs:134` and `Services/Payments/GatewayRefundService.cs:339-342`.

**Scenario:**
1. The refund POST times out at 20 s, or the user closes the page. `ct` is passed to `provider.RefundAsync` (`:163`), so closing the tab cancels the HTTP call mid-flight. Omise has not finished processing.
2. About 10 s later the user presses "ตรวจผลการคืนเงิน".
3. `GET /charges/{id}` returns `refunded_amount` equal to the recorded amount, so `NoMoneyOut` fires and the lock is removed.
4. The user refunds again. Once the first request finishes, the money has gone out twice.

For some sources (bank / e-wallet) Omise refunds may stay pending before `refunded_amount` moves; this needs checking against Omise's documentation.

The decision is not a self-stamp by a person, but the system decides "no money went out" from a snapshot taken too early.

**Fix (pick one or combine):**
- (a) Put a unique marker (attempt GUID) in `metadata[attempt]` of the refund request. Verify by reading `GET /charges/{id}/refunds` (which includes pending refunds) and look for the marker. Found means `MoneyWentOut`; absent after N minutes means `NoMoneyOut`.
- (b) Refuse `NoMoneyOut` until at least X minutes after `RefundOutcomeUnknownSince`.
- (c) Use `CancellationToken.None` for the money-out call. The page closing should not cut a money transfer mid-way.

---

## E2-3 · CONFIRMED (logic) / PLAUSIBLE (field) · P1: a silent provider locks refunds and settlement rounds forever

**Where:**
- `GatewayRefundMath.Verify` → `ProviderSilent`, which never clears the lock.
- `OmisePaymentProvider.cs:165` reads only `refunded_amount`.
- `GatewaySettlementService.cs:386`: `refundedAfter` includes every settled intent that has `RefundOutcomeUnknownSince != null`, **with no date filter**.
- `GatewaySettlementMath.cs:266`: the whole plan is blocked.

**Scenarios:**
- (1) The provider does not pin `Omise-Version` (no such header anywhere in the file), so the account's default API version decides the field names. As I recall, older Omise versions name this field `refunded` rather than `refunded_amount`; this should be checked against Omise's changelog. In that case every verify returns ProviderSilent and the lock never clears.
- (2) `ProviderRef` is empty. Then `GetChargeAsync` returns `RefundedTotal=null`.

**Consequences:**
- If the intent is already settled, **every future settlement round for this provider** is blocked (message 7).
- If the intent is unsettled, the round containing it is blocked.

The only exit mentioned is "ติดต่อผู้ดูแลระบบ", but nothing anywhere clears `RefundOutcomeUnknownSince` other than `VerifyCoreAsync` (grep: writers only at `:273/:342/:375`). This breaks F2 principle 8 ("stricter needs a way forward").

**Fix:**
- Also read `refunded` for older API versions, or sum `GET /charges/{id}/refunds`.
- Add an owner-level resolution path, e.g. `[RequireOwner]` with evidence (screenshot or provider refund id) and a hash-chained audit entry, that stamps the result from provider evidence entered by a person.
- In the settlement plan, block only when the flagged intent is in the round's actual window. A settled intent with no pending refund should produce a warning, not a block for every round.

---

## E2-4 · CONFIRMED · P1: the same fee tax invoice can be claimed repeatedly

**Where:** `GatewaySettlementService.ClaimFeeVatAsync` (`:591-633`). The only guard is `vat ≤ outstanding` (`GatewayFeeVatClaim.Check`). Nothing checks for an earlier claim JE with the same `Reference = invoiceNo` and the same tax id in the description.

**Scenario:**
1. 11630 holds fee VAT for August (100.00) and September (100.00), so outstanding is 200.
2. The user records invoice INV-A for August, VAT 100. It passes and outstanding drops to 100.
3. A double-submit, or a mistake a week later, records INV-A again with VAT 100. It passes and outstanding drops to 0.
4. The ภ.พ.30 purchase VAT report now has **two lines with the same invoice number INV-A**.
5. September's real invoice cannot be claimed ("exceeds pool").

The total VAT claimed stays within the pool, but the second claim has no tax invoice behind it (§82/5(1)). In an audit that means the input VAT is disallowed plus a surcharge.

The advisory lock only serialises the two requests; it does not stop the duplicate.

**Fix:**
- Before posting, check for an earlier claim JE: `Posted`, `ReversedByEntryId == null`, `Tags == ClaimTag`, `Reference == invoiceNo`, and the same `taxId` (the description contains it, or add a dedicated field).
- If one exists, block with a message naming the earlier JE number.
- Add a test: same invoice twice → blocked. Opposite direction: same number from a different tax id → passes.

---

## E2-5 · CONFIRMED · P2: §87 report fields for a fee-VAT claim (no purchase document behind it)

**Where:** `TaxService.cs:1307/1340` (JE_INPUT path): `TransactionDate = je.EntryDate`, `Description = ExtractDocRefFromJe(je)` (regex `[A-Z]{2,6}[-/]?\d[\d/-]*\d`, `TaxService.cs:2496`). `TaxPayerBranchCode` is never set.

**Problems:**
- **Invoice date:** the report shows the claim date. For a late claim (allowed up to 6 months) the purchase VAT report shows a date that does not match the paper invoice. §87 requires the date on the tax invoice.
- **Invoice number:**
  - A purely numeric number (e.g. `2026090001`) or a lower-case one fails the regex on Reference, falls through to the description and lines (no match), and ends up as the JV number.
  - `OMTH-INV-2026-09-0001` is truncated to `INV-2026-09-0001`.
- **Branch:** the supplier branch (Notice 199) exists only as text in the description, and the name column ends up as "… สาขา 00000". This is a gap in the JE_INPUT path as a whole (sales/purchase report lines never set `TaxPayerBranchCode`). E2 relied on that path as if it were complete.

**Fix:**
- Store invoice no / invoice date / branch as data on the claim JE: dedicated fields, a structured `Note`, or a linked `TaxReportLine` source.
- Have JE_INPUT read them directly rather than by regex.
- Or create the claim as a document (a purchase-VAT-only line type) instead of a JV.

---

## E2-6 · CONFIRMED · P2: the claim date can land in a VAT month already declared or filed

**Where:** `ClaimFeeVatAsync:601` checks only `ClosedPeriodReasonAsync`.

**Scenario:** ภ.พ.30 for August is already declared, but the fiscal period is not closed. The user picks `ClaimDate = 31/08`.
- The Dr 11610 JE lands in August.
- The stored report never picks it up, so the input VAT is silently lost.
- If the report is regenerated, a filed return changes.

The system already has `TaxFilingLockPolicy.DeclaredOrFiledStatuses` (used in `TaxService.cs:1100/1411` and `SettlementPosting`).

**Fix:** check that the claim month is not in a filed VAT month. If it is, block and tell the user to claim in the current month (within §82/3).

---

## E2-7 · CONFIRMED · P2: verify books the whole difference without comparing it to the attempted amount

**Where:** `GatewayRefundMath.cs:141` (`diff = provider − recorded`) and `GatewayRefundService.cs:369`. The attempted amount is not stored in any column; it appears only in the note text of the ⚠️ event.

**Scenario:**
1. An earlier refund of 200 took the E-1 path: money went out and the JE failed. The message told the user to book the JE by hand, which they did. `RefundedAmount` stayed at 0, because only `BookRefundAsync` writes that field.
2. Later a refund of 300 times out and is marked unknown.
3. Verify reads the provider total of 500 against recorded 0 and returns `MoneyWentOut` 500.
4. The system books a 500 JE, but 200 of it was already booked by hand, so AR and clearing are 200 too high.

A refund made on the provider dashboard gives the same result.

**Fix:**
- Store `RefundOutcomeUnknownAmount` on the intent.
- `Verify`: diff == attempted → MoneyWentOut; diff == 0 → NoMoneyOut; anything else → Inconsistent (a person resolves it).
- Add a test with diff ≠ attempted.

---

## E2-8 · CONFIRMED · P2: the `RefundTimingUnknown` block points to an exit that does not exist

**Where:**
- `GatewaySettlementMath.cs:274-281`: "ให้ผู้ดูแลระบบบันทึกยอดคืนรายครั้ง … ตามแดชบอร์ด".
- Test `RE2_ยอดรายครั้งไม่ครบ…` asserts `Contains("ผู้ดูแลระบบ")` as "a way forward that actually works".
- grep: `RefundAmount` is written only by `BookRefundAsync` and the migration.

**Consequences:**
- An intent refunded ≥ 2 times before this column existed, whose last refund is on or after a payout date, blocks that round permanently.
- If the intent is already settled, **every round for that provider** is blocked, because `refundedAfter` has no date filter.

The migration backfills only single-refund intents (`DatabaseMigrationHelper.cs:6250`). The note format written since `ef3d97b5` is always `"คืนเงิน {amount:N2} (สะสม {total:N2}) · …"`, so multi-refund intents could have been parsed. Only `Σ = RefundedAmount` needs checking before writing.

**Fix:** either parse the notes to backfill multi-refund intents (writing only when the sum matches), or build a real admin screen for recording per-refund amounts, with audit. Otherwise do not tell users an exit exists.

---

## E2-9 · PLAUSIBLE · P2: the "Bangkok midnight on payout day" cutoff is an assumption; the offered fix changes the JE date

**Where:** `GatewaySettlementMath.RefundCutoffUtc` and the NetMismatch message.

**Numbers:** payout 20/09 (Bangkok):
- Cutoff = 19/09 17:00Z.
- A refund at 19/09 23:30 Bangkok (16:30Z) is deducted in this round.
- A refund at 20/09 00:30 Bangkok (17:30Z) goes to the next round.

The timezone handling is correct: `ThaiDate.CalendarDateUtc` treats Unspecified/Local as UTC and converts to the Bangkok calendar date, and events use `At = DateTime.UtcNow`. I found no timezone bug.

**The problem:** Omise computes the transfer amount when the transfer is created, which can be the day before the money reaches the bank. A refund on day D−1 after transfer creation belongs to the next round, but the system counts it in this one. NetMismatch then tells the user to "check the payout date". The only lever is to change `SettledAt`, which moves the bank JE date so it no longer matches the statement.

**Fix:** let the user choose which refund belongs to this round (per-intent override with audit), or use the provider's transfer/balance data. Do not use the payout date as the adjustment knob.

---

## E2-10 · PLAUSIBLE · P2: the other entry point (settlement import, team B) skips the E-2 check

**Where:** `Services/Settlement/Adapters/PaymentIntentAdapter.cs:58` (`refundDelta = RefundedAmount − RefundAlreadyInLines`) and `SettlementImportService.cs:165-172`. Neither reads `RefundOutcomeUnknownSince`.

**Scenario:** an intent flagged "unknown" is imported from the provider's settlement file. The file has a 300 refund line that the system has not recorded. The refund is not booked twice, but it can be settled without resolving the E-2 lock, and the refund line has no JE or credit note behind it.

**Fix:** block or warn in the import plan for intents carrying the flag (F3 #8).

---

## E2-11 · PLAUSIBLE · P2: R-E5 on old data has no migration

**Where:** `GatewayReconciliation.cs:103` (`settled += SettledAmount − RefundDeductedAfterSettlement`). The column was added with `DEFAULT 0` and no backfill.

**Scenario:** under `ef3d97b5` code:
- A settled intent for 1,000 (fee 39, `SettledAmount` 961) had 300 refunded after payout, deducted in a later round.
- Reconciliation today: settled 961 + pending 0 = 961, against expected 661.
- The report stays unbalanced by 300 for the life of the row.

**Fix:** backfill `RefundDeductedAfterSettlement = RefundSettledAmount − (Amount − FeeDeducted(mode) − SettledAmount)`, or sum the "ยอดคืนเงินหลังรอบโอนก่อน X ถูกหัก…" events.

The population is probably small (the feature is from round 198 itself), but principle 9 says persisted data needs a migration.

---

## E2-12 · CONFIRMED · P3: smaller issues

- **Verify JE date:** in `GatewayRefundService.cs` the MoneyWentOut JE is dated **today**, but the event uses `At = attemptAt`. If the attempt was at month-end and verify runs the next month, the JE lands in a different month from when the money left.
- **Verify refund reference:** `refundRef = "VERIFY-…"` is not the provider's real refund id. Omise can list refunds, so the real id could be used.
- **Misleading lock:** a missing secret key throws before any request is sent (`OmisePaymentProvider.cs:82`), but it is still marked "unknown, money may have left". The message misleads, and the lock stays until the key is fixed.
- **Blank branch defaults silently:** `GatewayFeeVatClaim.Check:125` turns an empty supplier branch into `00000` instead of blocking. §86/4 and Notice 199 require the branch.
- **ClaimDate is unbounded:** a future date or a date before the deferring settlement JE is accepted, which can make 11630 go negative within a month.
- **Settled-row fee mode:** reconciliation for settled rows uses **today's** fee VAT mode (`ModeOf`). Change the mode after settling and the old rounds become unbalanced. The mode at settlement time is not stored.
- **R-E5 and old refunds:** intents with status Refunded but `RefundedAmount=0` (old refunds or E-1) now sit in "not yet in a payout round" at `-fee` forever, because the settlement plan excludes them. Previously they were not counted.
- **Pooled tolerance:** `vat > outstanding + 0.005` uses a pooled limit. VAT the provider actually charged but that differs through per-invoice rounding leaves a residue in 11630 with no write-off tool.

---

## Checked and not a bug

- **Idempotency and concurrency of verify:** it uses the same advisory lock as refunds (`refund:{id}`). A second click, once the first commits, sees `RefundOutcomeUnknownSince == null` and returns "no need". In the closed-period/chart branch it returns before any write, so the tx rolls back and the lock stays. Status changes go through `ApplyChargeAsync` after commit.
- **Permissions:** verify uses `[RejectApiKey]` + `RequirePermission(Refund=BankPaymentInit)`; claim uses `[RejectApiKey]` + `RequirePermission(PostSettlement=JournalManage)`; fee-vat GET uses `PreviewSettlement`. `write_permission_gate_check` and `owner_action_wiring_check` pass.
- **Tenant isolation:** every new query filters `CompanyId` (intent, config, events, JE lines via `l.JournalEntry.CompanyId`).
- **Claim JE cannot be edited:** `JournalEntryBuilder` sets `IsAutoGenerated = true`, and `UpdateJournalEntryAsync` rejects such JEs, so nobody can edit its Tags to push outstanding back up. On reversal the original gets `ReversedByEntryId` and is excluded; the reversal JE carries no tag, so it is not counted twice.
- **Claim JE is picked up by the report:** JE_INPUT accepts JEs with `SourceDocumentId == null` and `11610` counts as input VAT. The 11630 legs of settlement JEs are excluded from the report, so there is no double count.
- **§82/3 window:** calendar months from the invoice to the claim, blocked when > 6 and requiring a reason for 1–6. This matches CLAUDE.md B (invoice Jan → claim up to Jul).
- **Company not VAT-registered:** the claim is blocked, and the card is hidden when there is no deferred balance.
- **Migrations:**
  - The 3 `ADD COLUMN IF NOT EXISTS` statements are correct: `numeric(18,2) NULL`; `NOT NULL DEFAULT 0`; `timestamptz NULL`.
  - The backfill is idempotent (skips intents that already have a per-refund amount).
  - The note prefix `'คืนเงิน %'` does not collide with other notes (⚠️ / "manual:" / "ตรวจผล…").
  - `RefundedAmount` is written only by `BookRefundAsync`, so single-refund backfills are correct.
  - The migration runs at first startup, before any refund with the new code, so the "old + new mixed" case cannot happen.
- **`IsSettled` switch:** changing it to `SettlementJournalEntryId != null` is safe; `SettledAt` and `SettlementJournalEntryId` have always been written together.
- **Compile (by eye):** `return (Fail(...), null)` to a nullable tuple · `$"VERIFY-{id:N}"[..15]` · `buckets.Max(b => b.Level)` on an enum · `PlanCore(...) with { Warning }` · the 10-argument `GatewayRefundOutcome` · `Map(c, vatRegistered)` updated at all 3 call sites. I found no errors.

---

## ผลการแก้ของทีม E3 (รอบ 198 · commit <pending>)

ยังไม่ได้คอมไพล์ในเครื่องนี้ (ไม่มี .NET SDK) — CI บน `claude/**` คือ compiler ตัวแรก

| ID | สถานะ | ทำอะไร (ไฟล์) |
|---|---|---|
| E2-1 | ✅ | `GatewayRefundMath.ClassifyRefundHttpStatus` (2xx สำเร็จ · 4xx ยกเว้น 408 = ปฏิเสธ · 5xx/408/อื่น = ไม่รู้) · `ProviderRefund.OutcomeUnknown` · Omise adapter คืน `OutcomeUnknown: true` · `RefundCoreAsync` ล็อกทางเดียวกับข้อยกเว้น (`MarkOutcomeUnknown`) · เทสต์ `E21_*` (504/502/500/503/408 ล็อก · 400/401/404/409/422/429 ไม่ล็อก) |
| E2-2 | ✅ | (c) `provider.RefundAsync(..., CancellationToken.None)` · (b) `MinVerifyWait` = 10 นาที — ส่วนต่าง 0 ก่อนนั้น = `TooEarly` (ล็อกต่อ) · (a) `metadata[attempt]` = เครื่องหมายเฉพาะครั้ง เก็บที่ `PaymentIntent.RefundOutcomeUnknownAttempt` · adapter อ่าน `refunds.data[].metadata.attempt` (เฉพาะเมื่อรายการครบทั้งชุด `total == data.length`) · พบ = หลักฐานตรง + เลขอ้างอิงจริง · ไม่พบ ≠ ไม่ออก · เทสต์ `E22_*` |
| E2-3 | ✅ | ยอดสะสมสำรองจากผลรวม `refunds.data` (ไม่ใช้ช่อง `refunded` เพราะยืนยันเอกสารไม่ได้ — ไม่เดา) · `POST pay/intents/{id}/refund/resolve-manually` (`[RejectApiKey]` + `[RequireOwner]` + `Bank.PaymentInit` · `GatewayRefundMath.CheckManualResolution`: หลักฐานบังคับ · NoMoneyOut ต้องพ้นช่วงรอ · MoneyWentOut ต้องมียอด ≤ ที่ยังคืนได้ + เลขอ้างอิงการคืน → `BookRefundAsync` · `AddChainedAuditLog`) · รอบโอน: รายการที่บันทึกรอบแล้ว + ธง ⇒ **เตือน** (`SettledOutcomeUnknownWarning` เฉพาะรอบที่จุดตัดอยู่หลังเวลาพยายามคืน) ไม่บล็อกทุกรอบ · รายการยังไม่บันทึกรอบในช่วงยังบล็อก · เทสต์ `E23_*` |
| E2-4 | ✅ | `GatewayFeeVatClaim.FindDuplicate/PriorClaim` (เลขที่ไม่สนตัวพิมพ์ + เลขผู้เสียภาษีผู้ออก · ใบสำคัญเคลมทุกผู้ให้บริการที่ยังไม่ถูกกลับรายการ · ใบเก่าอ่านเลขจากคำอธิบาย · หาไม่เจอ = บล็อกให้คนตรวจ) ก่อนลงใบสำคัญใต้ล็อกเดิม · เทสต์ `E24_*` สองทิศ |
| E2-5 | ✅ | ช่องใหม่ `JournalEntry.TaxInvoiceNo/TaxInvoiceDate/TaxInvoiceSupplierName/TaxInvoiceSupplierTaxId/TaxInvoiceSupplierBranch` (migration `ADD COLUMN IF NOT EXISTS`) · เส้นเคลมเขียนช่องเหล่านี้ · รายงานภาษีซื้อ JE_INPUT อ่านผ่าน `Helpers/JournalInputTaxInvoice.Resolve` (วันที่ใบกำกับ · เลขที่ตามจริง · สาขา) · JE อื่นใช้ค่าที่แกะมาตามเดิม · ใบสำคัญเคลมเก่าก่อนมีช่อง = พฤติกรรมเดิม (ไม่ย้อนเติม — ข้อมูลอยู่ใน audit log) · เทสต์ `E25_*` |
| E2-6 | ✅ | `ClaimFeeVatAsync` บล็อกเดือนภาษีที่ `TaxFilingLockPolicy.DeclaredOrFiledStatuses` หรือ `FilingLockedAt` (ตัวเดียวกับ `DocumentService.VatPeriodDeclaredOrFiledAsync`) · ข้อความ `DeclaredVatMonthMessage` บอกให้เคลมเดือนที่ยังไม่ยื่น · เทสต์ `E26_*` (ข้อความ — คิวรีล็อกด้วย `required_call_site_check`) |
| E2-7 | ✅ | `PaymentIntent.RefundOutcomeUnknownAmount` · `Verify`: ส่วนต่าง = ยอดที่พยายามคืน (หรือยอดรายการที่มีเครื่องหมาย) ⇒ ลงบัญชี · อื่น ๆ/ไม่รู้ยอด ⇒ `Inconsistent` → บันทึกผลด้วยมือ · migration เติมยอดของแถวที่ล็อกอยู่จากเหตุการณ์ ⚠️ · เทสต์ `E27_*` |
| E2-8 | ✅ | ข้อความ `RefundTimingUnknown` บอกตรง ๆ ว่ายังไม่มีหน้าจอเติมยอดรายครั้ง (ไม่ชี้ไปเครื่องมือที่ไม่มี) + เทสต์ `RE2_ยอดรายครั้งไม่ครบ…` เปลี่ยนเป็นล็อกข้อความใหม่ · migration แกะยอดรายครั้งของรายการที่คืนหลายครั้งจากข้อความ "คืนเงิน {ยอด} (สะสม …)" เมื่อ**ทุก**เหตุการณ์แกะได้และผลรวม = ยอดสะสม |
| E2-9 | 📋 backlog | ต้องออกแบบ "เลือกยอดคืนเข้ารอบนี้รายรายการ + audit" หรือใช้ข้อมูล transfer ของผู้ให้บริการ — เปลี่ยนสัญญาของแผนรอบโอนทั้งเส้น (หน้าเว็บ + แผน + มาร์ก) ไม่ใช่การแก้เล็ก · ระหว่างนี้ข้อความ NetMismatch ยังบอกให้ตรวจวันเงินเข้า (ทางแก้ที่ผิดวัน JE ตามที่ผลตรวจชี้) — ควรให้เจ้าของเลือกทาง (override รายการ vs ดึงข้อมูล transfer) |
| E2-10 | ➡️ ทีม S3 | ไฟล์ `SettlementImportService`/`PaymentIntentAdapter` อยู่ระหว่างแก้โดยทีม S3 — ส่งต่อให้ S3 ตามคำสั่ง ไม่แตะในรอบนี้ |
| E2-11 | 📋 backlog | สูตรเติมย้อนหลัง `RefundSettledAmount − (Amount − FeeDeducted(mode) − SettledAmount)` ขึ้นกับโหมด VAT ค่าธรรมเนียม **ณ วันบันทึกรอบ** ซึ่งไม่ได้เก็บ (E2-12 ข้อ "Settled-row fee mode") ⇒ เติมด้วยโหมดวันนี้อาจผิดเงียบ · ทางที่ปลอดภัยกว่าคือแกะจากเหตุการณ์ "ยอดคืนเงินหลังรอบโอนก่อน X ถูกหัก…" แต่ข้อความนั้นเกิดในรอบ 198 เดียวกัน (ประชากรเกือบศูนย์) — รอเก็บโหมด ณ วันบันทึกรอบก่อน |
| E2-12 | 🔨 บางส่วน | ✅ คีย์ลับยังไม่ตั้ง = ปฏิเสธ (ไม่ล็อกผิด) · ✅ ตรวจผลใช้เลขอ้างอิงจริงของผู้ให้บริการเมื่อพบเครื่องหมาย · 📋 วันที่ JE ของการตรวจผล (= วันนี้) · ClaimDate ในอนาคต/ก่อนรอบโอน · สาขาว่าง → 00000 เงียบ · โหมดค่าธรรมเนียม ณ วันบันทึกรอบ · รายการ Refunded ยอด 0 ค้าง −fee · ส่วนต่างปัดเศษ 11630 — ยังเป็น backlog |

**ต้องให้เจ้าของตัดสิน**: (1) ระยะรอ `MinVerifyWait` = 10 นาที (ผลตรวจเสนอ ≥ 2) · (2) ปักหัว `Omise-Version` ไหม (วันนี้ไม่ปัก ⇒ ชื่อช่องขึ้นกับเวอร์ชันของบัญชี — ปักแล้วต้องทดสอบทุกเส้นใน sandbox) · (3) E2-9 ทางไหน · (4) บันทึกผลด้วยมือ "เงินออก" ลงบัญชีวันนี้ (ไม่ใช่วันที่พยายามคืน — เหตุผลเดียวกับ E2-12 ข้อแรก)
