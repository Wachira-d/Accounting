# Round 198: adversarial review (F3 #11) of team A contract `11ed3e32` and the unreviewed parts of team E `ef3d97b5`

> Read-only review by a subagent. No code was edited. Nothing was compiled because this machine has no .NET SDK.
> Part 1 was read from branch `settlement-base` (merge `8e32582a` = A on top of E). Part 2 was read at HEAD (`ef3d97b5` merged).
> I ran these checkers on an extracted `settlement-base` tree and all passed: gl_code_check, advisory_lock_key_check, record_arg_check, using_check,
> nullable_arg_check, accessibility_check, namespace_shadow_check, undeclared_local_check, tuple_name_merge_check,
> settlement_line_type_rules_check and write_permission_gate_check. `required_call_site_check` timed out (300 s), so it has no result.
> Line numbers for A are in `settlement-base` and line numbers for E are at HEAD.

## Summary: CONFIRMED P0/P1

| ID | Sev | What |
|---|---|---|
| **R-E1** | **P0** | The webhook verifies the event with **any** tenant's gateway config, then applies `metadata.intentId` to **any** tenant's intent. It checks neither the company, the config, the amount nor livemode. Result: a charge of 0 THB on another tenant's Omise **test** account marks the victim's invoice, order or booking as paid |
| **R-A1** | **P1** (latent until a gateway channel is wired) | `EnsureClearingAccountAsync` skips the 11340 fallback of `IGatewayAccountResolver`. A gateway channel whose config leaves `ClearingAccountId` empty (the default) gets a new 11341. Intents stay in 11340 forever and 11341 goes negative by the same amount |

The P2/P3 items follow. They block nothing today, but several must be fixed before teams B/C/D wire the contract (R-A2..R-A8).

---

## PART 1 — team A contract (`11ed3e32`)

### R-A1 · CONFIRMED · P1 (latent): a gateway channel creates a second clearing account instead of 11340
- `Helpers/SettlementChannelAccounts.cs:51-63` reads `PaymentProviderConfig.ClearingAccountId` **directly**. When that column is null, which is the default
  unless the user picks an account, the code falls through to `CreateUnderLockAsync` and creates `11341 ลูกหนี้แพลตฟอร์ม Omise`.
- The single resolver of the gateway path, `GatewayAccountResolver.ResolveClearingAccountAsync` (`Services/Payments/GatewayAccountResolver.cs:86-106`),
  falls back to **11340** in that case. That is where every `PaymentIntent` has been posted.
- Numbers: intent 1,070 posts Dr 11340 1,070. The batch has Sale 1,070 (with `PaymentIntentId`, so it is counted as `AlreadyInClearing` and not re-posted) and PaymentFee −41.79.
  The plan posts Cr 11341 1,028.21 (payout) and Cr 11341 41.79 (fee document paid from the clearing account). Result: **11340 = +1,070 forever · 11341 = −1,070**.
  The helper's own class comment (lines 18-19) names this exact failure: "ถ้าแยกผังใหม่ ยอดรับชำระกับยอดโอนจะอยู่คนละผังตลอดไป".
- A second gap in the same class: `SettlementBatchMath.Plan:258/262` counts any line that has a `PaymentIntentId`/`PaymentId` as "already in clearing".
  It never checks that the intent/payment was posted to **this channel's** clearing account.
- Fix: in the gateway branch call `IGatewayAccountResolver.ResolveClearingAccountAsync(companyId, cfg.ProviderCode, cfg.Id)` (or pass the resolved id in).
  Never create 1134x for `Kind == Gateway && PaymentProviderConfigId != null`; fail loudly if the resolver returns null.
  Add a test with config ClearingAccountId = null and 11340 present, which must return the 11340 id and create no new account.

### R-A2 · CONFIRMED · P2: new 1134x accounts are counted as trade AR and produce a false variance every month (the S-1 defect class again)
- The auto-created name is `"ลูกหนี้แพลตฟอร์ม {DisplayName}"` (`SettlementChannelAccounts.cs:164-168`), with code 1134x.
- `TradeReceivableAccount.IsTradeReceivableControl` (`Helpers/TradeReceivableAccount.cs:35-41`) excludes only code `11340` and the config clearing ids.
  Any `113*` account whose name contains "ลูกหนี้" is counted, so `SubLedgerReconciliationService.cs:60-68` compares 11341's GL balance with open invoices.
- Numbers: a Shopee wallet of 25,000 gives a variance of +25,000 on "ลูกหนี้การค้า (AR)" every month-end, and the same happens for every other 1134x.
- Fix: in `IsTradeReceivableControl` also exclude `SettlementChannelAccounts.IsClearingCode(code)` and every `SettlementChannel.ClearingAccountId`
  (have the service load them the way it loads gateway ids). Add a test that "11341 ลูกหนี้แพลตฟอร์ม X is not trade AR".

### R-A3 · CONFIRMED · P2: fee refunds of a different type inside the same VAT group make the 50 ทวิ disagree with 21917 and create negative purchase-document lines
- `SettlementBatchMath.cs:329-346` blocks with `FeeGroupNetRefund` only when the whole **VAT group** nets negative. `AddWhtLegs` (`:471-488`) posts a leg only for
  document lines with `WhtAmount > 0`, while `SettlementFeeDocumentPlan.WhtAmount` is the **signed** sum.
- Counter-example (ThaiVat7, registered, W3 `SelfWithholdPayerBorne`): Commission −1,070 plus AdsFee +535 (ads fee refunded).
  - Commission: pre-VAT 1,000 · WHT 3/97 = **30.93**. AdsFee (sign −1): pre-VAT 500 · WHT 2/98 = −10.20.
  - The group deduction is 535 > 0, so it is not blocked. The document's `WhtAmount` = **20.73** (the 50 ทวิ amount), but the JE posts Dr 53140 30.93 / Cr 21917 **30.93**.
  - The same happens with W2 (11320 30.93 vs certificate 20.73). The purchase document also carries a line of Expense −500 / VAT −35. That is an ads fee credit
    without the platform's credit note, and a negative line that the document validation (Qty > 0, UnitPrice ≥ 0) will likely reject when team C posts it.
- Fix: in `BuildFeeLines`, raise `FeeGroupNetRefund` per **(type, account) document line** when `Deducted < 0`, not per VAT group.
  Also make `AddWhtLegs` and `SettlementFeeDocumentPlan.WhtAmount` use the same set of lines. Add a test with exactly this pair.

### ✅ 84d47dda R-A4 · PLAUSIBLE (legal) · P2: non-VAT company with a foreign provider (`ForeignPp36`) creates no ภ.พ.36 and gives no warning
- `SettlementFeeTax.cs:86-96`: when `!companyVatRegistered` the treatment is `NoVat`, with pp36 = 0 and no issue raised. §83/6 puts the remittance duty on the **payer**
  whether or not the payer is VAT-registered; only the input-VAT credit is lost. The exception is a foreign provider registered for e-Service that charges VAT itself.
  report-S1 §5/§7 ("โรงแรมไม่จด VAT ไม่ต้อง ภ.พ.36") conflicts with the wording of §83/6. The comment at `SettlementFeeTax.cs:51-52` only covers the e-Service case.
- Numbers: a non-VAT hotel pays an Agoda commission of 450 that is not under e-Service. It should file ภ.พ.36 of 31.50 by the 7th of the next month (the cost is borne by the hotel). The system shows nothing.
- Fix: send it to an accountant (add to §10 of report-S1). Until then have `Plan` raise a non-blocking issue "ช่องทางต่างประเทศ + บริษัทไม่จด VAT — ตรวจว่าต้องยื่น ภ.พ.36 หรือผู้ให้บริการเก็บ VAT แล้ว".
  The DECISION_DOCTRINE rule is that an unknown must not pass silently.

### ✅ 84d47dda (บล็อก) · รอบ 200 ทีม W 79f3f8de (หัก ภ.ง.ด.54 ได้จริง) R-A5 · CONFIRMED · P2: foreign provider + WHT mode = ภ.ง.ด.53 at the Thai rate
- `SettlementFeeTax.cs:116-117` always uses `ThaiWhtRateTable.RateFor(code, payeeIsJuristic: true)`. `DefaultCode(WhtPayable)` = 21917 (ภ.ง.ด.53).
  Nothing guards the combination `FeeVatMode == ForeignPp36 && FeeWhtMode != None`.
- Numbers: a foreign ads platform charges 1,000 with W3. The system computes 20.41 (2/98) into 21917, as ภ.ง.ด.53 to a foreign payee. The correct treatment is ภ.ง.ด.54 at 15%,
  or 0% under a DTA with a CoR (report-S1 §5).
- Fix: have `Plan` block this combination with a next step ("ภ.ง.ด.54/DTA ยังไม่รองรับ — ปิดโหมดหัก หรือบันทึกเป็นเอกสารซื้อต่างประเทศ"), or have the settings page refuse to save it.

### R-A6 · PLAUSIBLE · P2: summary-sale date uses UTC `.Date`, not the Thai calendar date
- `SettlementBatchMath.cs:290-291` groups by `(l.TxnDate ?? batch.PayoutDate).Date`. The repo convention for turning an instant into a day is `ThaiDate.CalendarDateUtc`.
- If adapter B stores TxnDate as a real instant (for example a Shopee timestamp of 2026-10-01 03:00 +07 = 2026-09-30T20:00Z), the summary sale is dated **30 Sep**.
  That moves the tax invoice and the VAT month (ภ.พ.30) into September. The tests only use midnight UTC, so they cannot catch this.
- Fix: `GroupBy(l => ThaiDate.CalendarDateUtc(l.TxnDate ?? batch.PayoutDate))`, plus a test at 00:00–07:00 Bangkok time around month end.

### R-A7 · PLAUSIBLE · P2: summary sales are created late with no check against the §87 3-day window or a VAT month already filed
- The summary sale is created when the batch is posted, which is after the escrow release (often 7–15 days after delivery). It is dated from TxnDate, which is the date of the wallet
  entry, not the delivery date that D2 sets as the tax point. `Plan` has no issue for "summary date is older than 3 business days" or "that VAT month was already filed".
- Numbers: delivered 28 Sep, released 8 Oct, payout imported 20 Oct. A summary invoice dated 28 Sep (or 8 Oct) is created after the September ภ.พ.30 was filed on 15 Oct (e-Filing).
  Either output VAT is added to a filed month, requiring an amended ภ.พ.30 and a surcharge, or team C's poster fails with no explanation.
- The risk of double revenue is also passed through only as a non-blocking issue (`SummarySaleCreated`). If daily order import (DECISIONS 2) already created that day's summary,
  revenue and output VAT are doubled. The owner chose "create automatically + flag" (DECISIONS 3), so this is not a bug in the decision itself.
  The poster should still check for an existing summary document for (channel, date) and block if one exists.
- Fix: add an issue code `SummarySaleLate` (warn) and `SummarySaleInFiledVatPeriod` (block with a next step that points to the order-import path / amended return),
  using `TaxFilingLockPolicy`. Have the poster look up existing summaries per (channel, date).

### R-A8 · PLAUSIBLE · P2: a channel in foreign currency with a batch left at the default THB passes the FX block
- `SettlementBatchMath.cs:178` checks only `batch.Currency`. The entity default is `"THB"` (`Settlement.cs:63`), and `channel.Currency` (`:47`) is never compared.
- Numbers: a Stripe USD channel whose adapter does not set batch.Currency. A USD 1,000.00 payout is posted as Dr bank **1,000 THB**.
- Fix: block when `channel.Currency != "THB"` or `channel.Currency != batch.Currency`.

### R-A9 · PLAUSIBLE · P2: the unique `(CompanyId, ChannelId, ExternalTxnId)` index clashes with how the data is shaped
- `AccountingDbContext.cs:3331-3332` and the migration (`DatabaseMigrationHelper.cs:207`). The model splits one provider transaction into several lines
  (Sale 1,070 + PaymentFee −41.79, following the golden test). In Omise the fee sits inside the same transaction id `trxn_…`, and Shopee/Lazada reports have one order id
  carrying several fee columns. If adapter B puts the same txn id on every component line, the second line fails with **DbUpdateException 23505 (HTTP 500)**.
  A refund that carries the id of the original sale is rejected the same way.
- The unique filters ignore `Status = Voided`. After voiding a batch (status 9, not soft-deleted), re-importing the same PayoutRef or the same txn ids is blocked for good.
- Fix: before team B starts, define the key clearly as `(ChannelId, ExternalTxnId, LineType[, component])`, or dedupe with a row hash.
  Define void as soft delete, or add `AND "Status" <> 9` to both filters (in both EF and the migration).

### R-A10 · CONFIRMED · P3: shipping has a VAT asymmetry, leaving a credit in 53130 and VAT stuck in 11630
- `ShippingFeeCharged` = FeeDocument with VAT split (`SettlementLineTypeRules.cs:152-153`). `ShippingSubsidy` = DirectJournal at the gross amount with no VAT (`:154-155`).
- Numbers: charged −50 and subsidy +50 (free shipping). Result: 53130 Dr 46.73 / Cr 50 = **−3.27** and 11630 +3.27. The platform's tax invoice charges net shipping of 0,
  so the 3.27 is never matched and after 6 months it has to be written off (§82/3).
- Fix: have the subsidy net against ShippingFeeCharged in the fee document (a positive line in the same group), or split VAT in the same mode. Add a test.

### R-A11 · PLAUSIBLE · P3: the plan's VAT figures do not equal what `DocumentService` computes itself about 6.5% of the time
- Brute force over 0.01–2,000.00 THB: `gross − R(gross×100/107)` differs from `R(net×7%)` for **13,084 of 200,000 amounts** (for example fee 1.15: plan VAT 0.08, exclusive ×7% gives 0.07).
- If team C builds the fee document or summary sale from `Expense/Net` as exclusive prices and lets DocumentService compute VAT, the document total will not equal `Deducted`
  (a 0.01 AP residue or overpayment remains in the clearing account). Groups with explicit VAT in the file also cannot be reproduced by inclusive pricing.
- Fix: in the plan's contract, require the poster to use inclusive pricing, one line per `SettlementFeeDocumentLine`, **or** pass the plan's VAT figure explicitly. Add a round-trip test once C exists.

### R-A12 · PLAUSIBLE · P3: the balance equation turns into a tautology when the file has no wallet balance
- `Plan` never checks `OpeningWalletBalance` against the previous batch's `ClosingWalletBalance`, or against the GL balance of the clearing account. If the adapter derives `Closing − Opening = Σ lines − NetPayout` itself,
  the gate always passes, which is exactly what F2 #6 warns against.
- Fix: have the poster (C) check the continuity Opening(n) = Closing(n−1) of the same channel, or = clearing account balance at PayoutDate−1, and block with the difference.

### R-A13 · P3 (small, CONFIRMED)
- `EnsureClearingAccountAsync` calls `SaveChangesAsync` inside the caller's transaction (`:60,109,136`). That saves every pending change the caller has, not only the new account.
  The comment says it should only be called when binding a channel. Suggestion: guard it or document it as a hard precondition.
- A concurrent manual account creation with code 1134x (from the chart-of-accounts page) does not use the same lock, so it ends in a 23505 error (HTTP 500) instead of a `BusinessRuleException`.
- `SettlementChannelKind` has no value 0. A DTO that omits `kind` binds to 0 and is persisted as an invalid value. Validate before saving.
- `SettlementLine.ChannelId` has no FK and no check that it equals `Batch.ChannelId`. The unique index relies on this denormalised copy.
- The dispute account (11320) and W2 `WhtReimbursable` (11320) share one "other receivables" account across every channel and cannot be reconciled per channel.
- `Adjustment` accepts `OverrideAccountId` pointing at 41xxx, which gives revenue with no output VAT, or at the clearing/bank account. A reason is required, but no account class is blocked.
- `SettlementFeeTax` does not use the ฿1,000 threshold of `ThaiWhtRateTable` (ท.ป.4/2528 ข้อ 12). WHT is deducted on every batch even when the fee is 50 THB.
  This is probably correct for an ongoing contract, but it should be decided explicitly.

### NOT-A-BUG (checked)
- Rounding 7/107 vs 100/107: brute force over 0.01–2,000.00 found **0** cases where `R(g×7/107) ≠ g − R(g×100/107)`. g×100/107 can never land on an exact
  midpoint because 107 is coprime to 10.
- Dr = Cr for every combination inside `Plan`: every JE leg is added as a pair. The fee document gives Deducted = Expense + InputVat (ThaiVat7), = Expense (VatNotClaimable/NoVat),
  and Expense + 11640 = Deducted + 21912 (Pp36). W3 borne = wht on every part. The `ClearingMovement` invariant = LinesTotal − Already − NetPayout.
  I could not build a counter-example for the balance.
- Enum numbers and DB defaults match (Kind 2, FeeVatMode 1, FeeWhtMode 0, RevenueModel 1, Status 0, SourceKind 1). The migration index names and partial filters equal the EF model
  (a test locks this), and `IX_SettlementBatches_ChannelId` is created to match the EF convention.
- The lock key is deterministic (FNV, `AdvisoryLockKey.For`). The context has no retrying execution strategy, so the "retry adds a duplicate account" path cannot happen.
- Tenant: every query in `EnsureClearingAccountAsync` filters `CompanyId`. `IgnoreQueryFilters` also filters CompanyId.
- The seed is `INSERT … ON CONFLICT DO NOTHING` with no UPDATE and never touches 1134x. It sets every NOT NULL column.
- `ParseClassifierAnswer` rejects numbers and `Unclassified` (anti-hallucination guard). `GenericFeedbackDistillationModel` is registered for key 57.

---

## PART 2 — team E (`ef3d97b5`), the parts main agent did not review

### R-E1 · CONFIRMED · **P0**: cross-tenant payment spoofing through the webhook (G-8 "exempt but verified" does not hold)
- `Controllers/PaymentGatewayController.cs:446-466`: the controller verifies the event with **every** active config of that provider **from every company**. When any one of them verifies,
  it calls `_intents.ApplyChargeAsync(verified.IntentId, …)`.
- `Providers/OmisePaymentProvider.cs:295-303`: `IntentId` comes from `metadata.intentId` of the charge fetched with **the key of the config that passed**.
  `metadata.companyId` (line 181) is never checked.
- `PaymentIntentService.ApplyChargeAsync:262-322` loads the intent by `Id` **alone**. It checks neither `CompanyId == cfg.CompanyId`, nor
  `ProviderConfigId == cfg.Id`, nor that `charge.Amount` equals `intent.Amount`, nor livemode against the intent's config mode. It then calls `DispatchSucceededAsync`
  (issues the receipt, settles the debt, confirms the booking).
- Attack scenario: the attacker is a customer of shop X (the public page returns `intentId`: `PublicPaymentController` `…/intents/{intentId}`). The attacker registers their own tenant
  and configures Omise **test keys**. They create a test charge of 20 THB with `metadata[intentId]=<X's intentId>`, then POST `{"id":"evnt_test_…"}` to `/api/pay/webhooks/omise`.
  The server fetches the event with the attacker's key and it is found. X's intent of 10,000 THB becomes Succeeded, the order/booking is confirmed,
  and the JE posts Dr 11340 10,000, which will never arrive.
- Fix (all of these):
  1. In the webhook, after verifying, require `intent.CompanyId == cfg.CompanyId && intent.ProviderConfigId == cfg.Id`. Otherwise log it and do not apply.
  2. `ApplyChargeAsync` must reject `Succeeded` when `charge.Amount != intent.Amount`, or when livemode does not match `config.Mode`, and record an event.
  3. The Omise adapter should cross-check `metadata.companyId`.
  4. Add a two-tenant test.

### ✅ cbd50b37 R-E2 · CONFIRMED · P2: a refund after the payout date but before the user records the settlement gives a NetMismatch that cannot be fixed
> **Fixed (team E2)**: `GatewaySettlementMath.RefundedAsOf` + `RefundCutoffUtc` — refunds count **as of the payout date** (cutoff = 00:00 Bangkok
> on `SettledAt`; a refund on/after the payout day belongs to the next round). Per-refund amounts are now stored on the refund event
> (`PaymentIntentEvent.RefundAmount`, backfilled for single-refund intents). Settlement marks `RefundSettledAmount` = refunded-as-of, so the
> later refund is deducted in the next round. The refund-after branch uses the same cutoff (not `ToDate`). When per-refund data is missing
> (multi-refund before tracking) the plan blocks with `RefundTimingUnknown` instead of guessing. NetMismatch text now names the payout date
> as the cause when the round has refunds. Tests: `GatewaySettlementReview198Tests.RE2_*` (the 1,000/961/300 numbers both rounds · opposite
> direction refund-before-payout · straddling refunds · unknown). Residual: no admin UI to key in per-refund dates for the legacy
> multi-refund case (the message tells the user to ask an admin).
- `GatewaySettlementMath.Contribution:152` uses `Amount − RefundedAmount` (the running total now), not the refunds made before `req.SettledAt`.
  `SelectCandidatesAsync:319-338` filters unsettled intents by `ConfirmedAt` only. The refund-after branch uses `LastRefundedAt < ToDate+1`, not `SettledAt`.
- Numbers: charge 1,000 on 18 Sep. Omise payout on 20 Sep of 961.00 (fee 39.00). Refund 300 on 22 Sep. The user records the 20 Sep payout on 25 Sep.
  Expected = 661 against actual = 961, a difference of **300**, which is blocked. The message tells the user to fix the fee or the date range, and neither helps.
  Next round, the provider deducts 300 but that intent is not in refund-after (it has no SettlementJournalEntryId).
- Fix: `Contribution` should take `refundedAsOf(SettledAt)`. This needs refund timestamps per refund (they are in `PaymentIntentEvents`/refund history),
  or a "refund after this payout" flag. At minimum, detect the case and write a message that states the real cause (F2 #7).

### ✅ cbd50b37 R-E3 · CONFIRMED · P2: gateway fee VAT is parked in 11630 with no path to 11610 (the "มี ≠ ถูกเรียก" class)
> **Fixed (team E2, minimal flow)**: "รับใบกำกับค่าธรรมเนียม" on the settlements page → `POST pay/settlements/fee-vat/claim` →
> `GatewaySettlementService.ClaimFeeVatAsync`: JV **Dr 11610 / Cr 11630** for the VAT on the provider's tax invoice (no expense line), with
> invoice no (Reference) · supplier name/tax ID (checksum)/branch in the description so the purchase-VAT report (JE_INPUT path) counts it as
> claimable · VAT ≤ the provider's outstanding deferred VAT (settlement-JE 11630 lines − earlier claims tagged `gateway-fee-vat:{provider}`) ·
> §82/3: > 6 months blocks, 1–6 months needs a late reason · closed-period gate · hash-chain audit. Aging card (`GET pay/settlements/fee-vat`,
> `Helpers/GatewayFeeVatClaim.Aging`): FIFO outstanding per settlement month, warning at ≥ 5 months, "may be past the window" > 6.
> The page and the settings hint say not to record the provider's invoice as a purchase document.
> **Owner/accountant questions (not decided here)**: (1) outstanding 11630 past 6 months — CLAUDE.md #2-B says "expired → auto reclassify
> to expense", but the real invoice date is unknown to the system (settlement month is a proxy) ⇒ we only warn; should the system post the
> write-off JV automatically, and against which expense account? (2) should a purchase document from the provider's contact be **blocked**
> while that provider has deferred 11630 VAT? That needs a provider ↔ contact mapping on `PaymentProviderConfig` (not built).
- `GatewaySettlementService.cs:423-431` posts Dr 11630. `payment-settings.html` promises "ย้ายเข้า 11610 เมื่อได้ใบกำกับรายเดือน". A grep of `"11630"` finds no flow that
  reclassifies the gateway fee VAT (DocumentService uses 11630 only as a fallback for undue VAT) and no 6-month alert (§82/3).
- Result: the input VAT the user was told is "claimable" never enters ภ.พ.30 unless someone writes a manual JV. A user who records the provider's monthly tax invoice
  as a purchase document gets the expense twice and input VAT in both 11610 and 11630. That is the same problem report-S1 (ก) raised, still unsolved.
- Fix: add an "attach monthly fee invoice" flow (Dr 11610 / Cr 11630, difference shown), or warn and block once a PI is created from the same provider with a 11630 balance.
  Add an aging alert for 11630 older than 6 months.

### ✅ cbd50b37 R-E4 · CONFIRMED · P2: the "แก้ค่าธรรมเนียม" button on the settlements page shows fee+VAT but saves it as the pre-VAT fee (AddedOnTop mode)
> **Fixed (team E2)**: `PendingSettlementItem.FeeInput` (= stored `FeeActual ?? FeeEstimated`, the value `PUT /fee` saves) +
> `FeeInputLabel` (per mode, from `GatewaySettlementMath.FeeInputLabel`) — the page pre-fills and labels from these; `payment-intents.html`
> gets the same label. Test `RE4_กดตกลงโดยไม่แก้_ต้องไม่เปลี่ยนยอดที่ถูกหัก` (39.06 → still 41.79 deducted; the old path gave 44.72).
- `payment-settlements.html` editFee takes its default from `i.fee` = `c.FeeDeducted` (= fee + VAT in AddedOnTop mode, `GatewaySettlementMath.cs:161-164`).
  `PUT /fee` stores that into `FeeActual`, which means **pre-VAT** in that mode.
- Numbers: fee 39.06, VAT 2.73. The page shows 41.79. The user clicks OK without changing anything, FeeActual becomes 41.79, VAT becomes 2.93, and FeeDeducted becomes 44.72.
  The expected net drops by 2.93, so a round that was correct now shows NetMismatch.
- Fix: send `feeActual` (the raw field) in `PendingSettlementItem` and use it as the default, with a label saying "ก่อน VAT/รวม VAT" per mode.
  `payment-intents.html` uses `r.feeActual` and is correct.

### ✅ cbd50b37 R-E5 · CONFIRMED · P2: the gateway reconciliation report (`GatewayReconciliation.Compute`) stays unbalanced after G-2/G-3
> **Fixed (team E2)**: `Compute` now takes every per-intent figure from `GatewaySettlementMath.Contribution` with the provider's
> `FeeVatMode` (lifetime expected · outstanding = unsettled net, or pending refund-after for settled rows · settled = `SettledAmount` −
> new `PaymentIntent.RefundDeductedAfterSettlement`). Tests for the three cases (AddedOnTop 10 × 36.50 → 0, was +25.60 · refund-after
> before/after deduction → 0, was −300 · full refund unsettled → outstanding −fee, was −fee unexplained) + opposite direction (a real
> fee mismatch still shows). The old test that locked "full refund is not unsettled" was the bug and now asserts the corrected value.
> Legacy settled rows that already had a refund-after round before this column existed show the deducted refund as unexplained
> (visible, not silent).
- It is a second formula next to `GatewaySettlementMath.Contribution` (F2 #4):
  - AddedOnTop: `fee` excludes VAT, but the provider deducts fee + VAT. With 10 intents × fee 36.50, `UnexplainedDifference` = +25.60 every month.
  - Refund after settlement: SettledAmount stays at the original c.Net while expectedNet deducts the refund. Refund 300 gives a permanent −300.
  - Full refund not yet settled: excluded from `unsettledRows` although the fee is still deducted, giving a difference of −fee.
- Fix: have Reconciliation call `Contribution` (the same feeVatMode) and include the refund-after amount.

### ✅ cbd50b37 R-E6 · PLAUSIBLE · P2: default `GatewayFeeVatMode.None` passes an unknown VAT treatment silently
> **Partly fixed (team E2 — safe visible warning)**: `GatewaySettlementMath.FeeVatModeWarning(mode, companyVatRegistered)` — one message
> used by the settings page (`ConfigResponse.FeeVatWarning`), the pending list and the settlement preview (`SettlementPlan.Warning`, non-blocking).
> The stored default was **not** changed: flipping `None` → `IncludedInFee`/`Unknown` changes the posting of every existing config and
> needs a migration + owner decision (foreign/non-VAT providers exist). **Owner question**: add `Unknown` and block preview/record for
> VAT-registered companies until chosen, or keep the warning? Also align with team A's `SettlementFeeVatMode.ThaiVat7` default.
- `Payments.cs:63` defaults to `None`, meaning "no VAT, the whole amount is expense". For a VAT-registered company using Omise (a Thai provider that charges 7% VAT), input VAT is lost with no warning.
  The same fact in A's contract defaults to `SettlementFeeVatMode.ThaiVat7`, so the two defaults contradict each other.
- Fix: add a value `Unknown` (or a null setting), and have preview/record block for VAT-registered companies until the user chooses.
  Alternatively use `IncludedInFee` as the default for Thai providers, and give both enums the same meaning.

### R-E7 · CONFIRMED · P3
- `RecordAsync:203-208` catches every `InvalidOperationException` from the builder (unbalanced, fewer than 2 lines) and labels it `PeriodClosed`.
  A round of all-zero intents (full refunds + fee corrected to 0) gets "JE ต้องมีอย่างน้อย 2 บรรทัด" with a "งวดปิด" reason. A round with only refund-after (gross < 0)
  gets `NegativeNet` with the message "ค่าธรรมเนียมมากกว่ายอดรับ", which is the wrong cause.
- `CorrectFeeAsync` does not take the settlement advisory lock. When it runs at the same time as `RecordAsync`, the latter sets `FeeActual ??= estimate` from the value it read before,
  overwriting the new value. The audit log says 36.50, but the JE and FeeActual use the old value.
- `FeeActual ??= fee` at settlement (`:223`) marks the estimate as "actual" per intent, although only the round total matched within ±0.01 (R1-ish).
- The fee account for the same economic fee differs between the two paths: gateway falls back to **54710** (`:414-415`) while A's contract uses **53170** (D6).
  `ServiceWhtRate 0.03` and `FeeVatRate 0.07` are a second copy of `ThaiWhtRateTable`/`PartnerVatRate` (F2 #4).
  In `None` mode, WHT is based on the fee including VAT. Posting is blocked by `WhtCertificateRequired`, but the preview shows a figure about 7% too high.
- G-8: GET `settlements/pending`, `reconciliation`, `intents` and `events` have no permission check, while `settlements/preview`, which is also read-only, requires Bank.View.
  GET `status?live=true` writes (Refresh leads to Apply and Dispatch) with only `[Authorize]`. It reflects the provider's real state, so that is acceptable, but it should not accept API keys.
  `confirm-manually` writes a PaymentIntentEvent but no `AddChainedAuditLog`, while CorrectFee does.

### NOT-A-BUG (checked)
- `GatewaySettlementMath.Plan` is Dr = Cr in every path that reaches posting (Dr = gross + wht = Cr). Negative fees that would drop the FeeExpense line are blocked in `CorrectFeeAsync` (`fee < 0`),
  and the provider never sends a negative fee.
- G-5: preview and record use `ClosedPeriodReasonAsync`/`PostAsync`, the same gate, on the date `CalendarDateUtc(SettledAt)`.
- G-4: a plan with WHT always has `Ok=false` (no 21917 without a 50 ทวิ).
- G-8 write endpoints: refund = Bank.PaymentInit + `[RejectApiKey]` · confirm = Bank.Reconcile + RejectApiKey · fee/settlement = Journal.Manage + RejectApiKey ·
  create intent = the source module's permission (unknown source kind falls to the strictest permission). `RequirePermission` fails closed.
- The race between a refund and settlement on the same intent: EF writes only changed columns, so `RefundedAmount` is not overwritten, and the refund that slips through is deducted next round.
- Tenant: every query in `GatewaySettlementService`/`GatewayAccountResolver` filters `CompanyId`. The problem is the webhook path (R-E1).

---

## Missing opposite-direction and negative tests (both teams)
Two-tenant webhook (R-E1) · gateway channel with a null config clearing account (R-A1) · 11341 is not trade AR (R-A2) · mixed-sign fee pair with WHT (R-A3) ·
ForeignPp36 × non-VAT and ForeignPp36 × WHT (R-A4/5) · TxnDate 00:00–07:00 Bangkok time at month end (R-A6) · USD channel with default THB batch (R-A8) ·
component lines sharing an ExternalTxnId, and re-import after void (R-A9) · refund after the payout date recorded late (R-E2) · AddedOnTop editFee round-trip (R-E4) ·
reconciliation after refund-after (R-E5).
