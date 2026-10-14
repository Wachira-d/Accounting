# ตรวจช่องทางรับ/จ่ายเงิน (bank vs GL) ทุกหน้า — 2026-10-14

> ทีมตรวจ read-only หลังบั๊กรายการประจำ (e00896d7) · ติ๊ก ✅ <sha> เมื่อแก้ · ห้ามลบแถว

## Payment/bank channel audit (read-only; nothing compiled or run)

**Summary:** I found 2 P0s that block normal use. Petty cash posts a BankAccount.Id where the server expects a ChartOfAccount id, which is the same class of bug as recurring. The cheque page cannot open a cheque book at all. Several P1s post money to the wrong GL account without any message, and several pages switch the saved channel without telling the user. Everything below I confirmed by opening the file at the line cited.

### P0 — blocks normal flow

**1. Petty cash: the bank id is used as a GL id, and funds can never be linked to a GL account**
- Scenario: user clicks "เติมเงิน" on a fund.
  - The page loads `getBankAccounts()` and sends `sourceAccountId = accounts[idx].id` (or `accounts[0].id` when there is one bank). See `petty-cash.html:213-230`.
  - The server uses that value directly as the ChartOfAccount credit account. See `PettyCashService.cs:162-172` → `PettyCashJePlan.ForReplenishment` (`Helpers/PettyCashJePlan.cs:90-106`) → `JournalEntryLine.AccountId` (`PettyCashService.cs:208-214`).
  - There is no lookup through `LinkedAccountId` and no tenant check. Result: an FK error/500, or a line posted to a non-existent account.
- Before that point it already fails: `createFund` always sends `linkedAccountId: null` (`petty-cash.html:172`). There is no endpoint or UI to link a fund later (`SmeOperationsController.cs` only has create/list/disburse/replenish/transactions). So every disburse with an expense account and every replenish throws "ไปที่ตั้งค่าเงินสดย่อย…", a screen that does not exist (`PettyCashJePlan.cs:72-74, 94-97`).
- The disburse option "— ไม่โพสต์ JE —" (`petty-cash.html:118`) always fails too (`PettyCashJePlan.cs:68-71`).
- Fix:
  - Send the bank's `linkedAccountId`, or better, send `bankAccountId` and resolve it server-side the way `StatutoryRemittanceService.ResolveBankGlAsync` does.
  - Validate the account's `CompanyId`.
  - Add a fund GL picker (default 11112) on create and edit.
  - Remove the "ไม่โพสต์ JE" option.

**2. `cheques.html`: the bank dropdown is never filled, so a cheque book can never be opened**
- `loadBanks()` calls `api.get('/bank/accounts')` (`cheques.html:137`). `Layout.api()` returns `API.c(cid)` (`layout.js:2223-2226`), which has no `get` method. `get` exists only on the root `API` object (`api.js:550`).
- The resulting TypeError is swallowed by `catch { /* ignore */ }`, so `#bookBank` stays empty.
- `openBook` then sends `bankAccountId: ""`, which fails model binding for `Guid` with a 400.
- Separately, `ChequeService.OpenChequeBookAsync` (`:83-100`) does not check that the bank id belongs to the company.
- Fix: use `api.getBankAccounts()`, show an error when it fails, and validate the bank's `CompanyId` on the server.

### P1 — wrong posting or wrong data

**3. `payments.html`: the bank the user picks is ignored**
- The payload sends `bankAccountId` (`payments.html:204`), but `CreatePaymentRequest` has no such field. It only has `OverrideBankAccountId` / `OverridePaymentAccountId` (`DocumentDtos.cs:1588-1605`).
- So the payment posts to `doc.BankAccountId`, or to cash 111 when the document has none (`DocumentService.cs:14277`). The page is in the nav (`layout.js:1282`).
- Fix: rename the field to `overrideBankAccountId`.

**4. A bank with no GL link (or a stale or wrong id) is posted silently to cash, or not posted at all**
- Document auto-post (`DocumentService.cs:17168-17180`): `PaymentAccountId` → `bank.LinkedAccount` → `FindAccountAsync("111")`.
  - If the bank's `LinkedAccountId` is null, or the id is from another company or simply wrong, the entry goes to 11111 Cash with no message.
- Payment JE (`:19527-19541`, duplicated in `ResolvePaymentCashAccountAsync` at `:19435-19450`) has the same fallback.
  - `if (cashAccount == null) return;` at `:19541` saves the payment with **no JE at all** when the chart has no 111x account (custom charts).
- At the same time, `BankAccount.CurrentBalance` is still updated for the bank (`:14449-14459`), so the bank register and the GL drift apart.
- Fix:
  - Make one shared resolver that throws a business error naming the bank and pointing to "บัญชีธนาคาร → ผูกผังบัญชี" when the link is missing, instead of falling back.
  - Throw instead of `return` when no cash account is found.

**5. The document's own "แหล่งเงิน" (`PaymentAccountId`) is not carried into a later payment**
- `Payment` copies only `BankAccountId = request.OverrideBankAccountId ?? doc.BankAccountId`. `OverridePaymentAccountId` comes only from the request (`DocumentService.cs:14277-14279`).
- Scenario: a document whose channel is `account:` (petty cash 11112, director advance 21230) is paid later through the record-payment modal, whose label says "— ใช้บัญชีตามที่ผูกในเอกสาร (default) —" (`documents.html:12366-12368`). The payment posts to cash 111.
- The multi-document path also ignores `OverridePaymentAccountId` (`:14736-14737`). That path is API-only.
- Fix: `OverridePaymentAccountId = request.OverridePaymentAccountId ?? (request.OverrideBankAccountId == null ? doc.PaymentAccountId : null)`.

**6. Settings: saving any setting wipes the default payment account**
- The dropdown is filled from `getPaymentChannels` (`settings.html:2524-2527`). The comment says it returns "all cash/bank", but the server excludes 1112x (`AccountingService.cs:96`).
- So a saved bank GL, or a deactivated account, is missing from the options and the select shows "auto".
- Save then sends `Guid.Empty` (`settings.html:2882`), which clears the setting (`SettingsService.cs:244-246`). This affects OCR, payroll SSO and tax remittance defaults.
- The server also does not validate the id's tenant or that it is active before payroll and remittance post to it raw (`StatutoryRemittanceService.cs:1880-1884`).
- Fix:
  - Include the bank GL accounts in the list.
  - When the saved id is not in the list, add it back as an option and show a notice.
  - Send `null` (meaning "don't touch") instead of `Guid.Empty`.
  - Validate the id on the server.

**7. `recurring.html`: a saved channel that is no longer in the list silently becomes cash**
- Hydration sets `fPaymentChannel.value = 'bank:x'` (`recurring.html:715-716, 723`).
- If that bank was deleted or that account deactivated, no option matches and the select shows "— เงินสด (ค่าเริ่มต้น) —".
- Save then sends null for both fields (`:532-536`), and every generated document posts to cash.
- Fix: same add-back-and-notice pattern as `documents.html:_hydratePaymentAndCategory` (`:9565-9581`).

**8. Lodging: deposits and checkout collection have no bank picker**
- `lodgingConfirm` (`lodging.html:344`) and `lodgingCheckOut` with `collectBalanceNow` (`:392`) send a payment method (often a transfer) but no `bankAccountId`.
- The server supports it (`LodgingService.Lifecycle.cs:723, 1538`), but receives null, so bank transfers land in 11111 Cash.
- Fix: add a bank select, required when the method is not cash.

**9. Creating a bank account does not validate `LinkedAccountId`**
- The update path checks company, active and 111x (`BankService.cs:225-238`). The create path uses `request.LinkedAccountId` with no check (`:71-90`).
- A cross-tenant or non-cash GL id is accepted.
- Fix: run the same validation on create.

**10. Document create/update stores `BankAccountId` / `PaymentAccountId` with no validation**
- See `DocumentService.cs:1242-1243, 2967-2970`.
- A swapped id (bank id in `PaymentAccountId`) gives an FK 500 (`AccountingDbContext.cs:868-870`).
- A cross-tenant id passes the FK check and then silently posts to cash (see #4).
- Fix: validate both fields against their own tables with `CompanyId`, at save time.

**11. Hard-coded code prefixes break tenants with a custom chart of accounts**
- `GetPaymentChannelAccountsAsync` only returns codes starting 111 / 1133 / 2123 and drops 1112 (`AccountingService.cs:91-96`). A tenant whose cash is coded, say, 1010 sees only the "cash default", which then resolves to nothing (#4).
- Payroll per-employee source:
  - The page offers every linked bank's `linkedAccountCode` (`payroll.html:1697-1701`).
  - The server rejects anything not starting 111 / 1133 / 2123 (`PayrollService.cs:1553-1557`), so this is a hard block for custom charts.
- Bank linking only allows 111x, in both the UI (`bank.html:2708-2709`) and the server (`BankService.cs:234`). Auto-creating the link needs exactly 11121 / 11122 / 11123 (`BankService.cs:104-124`). A custom-chart tenant therefore cannot link a bank at all.
- The client-side `startsWith('1112')` filters are redundant copies of the server rule:
  - `documents.html:1890, 2666`
  - `recurring.html:458`
  - `payroll.html:1426`
  - `tax-remittance.html:783`

  A bank linked to a non-1112 111x account (for example 11131) shows up twice, as `bank:` and as `account:`.
- Fix: decide "money account" by a flag or account type in one helper, not by code prefix. Have the server return channel rows already tagged bank or GL, and drop the client filters.

**12. Payroll's default source posts to a group account**
- When no source is chosen, net pay is credited to `11122` (`PayrollService.cs:3016-3023`). That is the parent of the auto-created bank accounts `11122-001` and so on, so bank reconciliation never sees it.
- This also ignores `CompanySettings.DefaultPaymentAccountId`, which SSO settlement and tax remittance do use.
- Fix: use the same default resolver as `ResolveBankGlAsync`.

### P2 — UX, messages, small mismatches

13. `documents.html:2264` matches an OCR channel code with `b.accountCode`, which does not exist on `BankAccountResponse` (`BankDtos.cs:26-38` has `linkedAccountCode`). A bank's GL code therefore falls through to `coaByCode` and becomes `account:<bank GL>`. The document has no `BankAccountId`, so later payments go to cash and the bank balance does not sync. Fix: match `b.linkedAccountCode`.

14. The document-template default never applies. `document-templates.html:540-543` stores a raw CoA id (and includes 1112x). `documents.html:6290-6293` compares that raw GUID with `bank:…`/`account:…` option values, so it never matches.

15. Hydration precedence is inconsistent. The edit form prefers bank over `PaymentAccountId` (`documents.html:9569-9570`), while posting and the credit-note prefill prefer `PaymentAccountId` (`DocumentService.cs:17166-17180`, `documents.html:7606`). A document with both set (API/clone) switches its posting account on re-save.

16. Silent empty dropdowns from `.catch(() => ({data: []}))` or an ignored catch:
   - `documents.html:1876-1877, 2648-2649`
   - `recurring.html:446-447`
   - `payroll.html:1375-1376, 1676-1677`
   - `tax-remittance.html:185-186` (also not awaited, so opening the modal early shows only the default option)
   - `payments.html:167`
   - `payment-settlements.html:197`: with no banks the select is empty, `""` is sent as a Guid, and the user gets a cryptic 400.

   The user sees only "cash (default)" and saves, with no warning.

17. Unlinked-bank errors give no way forward:
   - `StatutoryRemittanceService.cs:1877`
   - `PayrollService.cs:5072`
   - `DocumentService.cs:8852` ("— ตั้งค่าก่อน")

   Follow the wording in `GatewaySettlementService.cs:595-597` or `LodgingService.Lifecycle.cs:1878-1879`.

18. Inactive bank accounts are offered everywhere except POS. `GetBankAccountsAsync` returns `IsActive=false` rows (`BankService.cs:163-168`).

19. These flows have no channel picker and always use cash:
   - Salary-advance disburse sends `bankAccountId: null` (`salary-advance.html:409`).
   - Bulk vendor pay sends no `bankAccountId`/`paymentAccountId` (`cash-management.html:501-503` → `VendorConsolidatedPaymentController.cs:130-131`).
   - Cash-advance disburse accepts `BankAccountId` and ignores it (`CashAdvanceController.cs:134-158`).

20. `_deriveMethodFromChannel` derives the payment method from code prefixes on the client (`documents.html:4630-4637`). Seeded e-wallet 11113 becomes Cash, and 1133x becomes Other.

21. Paying from a fund account (`rpFundAccount`) still sets `BankAccountId` to the document's bank, so that bank's `CurrentBalance` moves although no money went through it (`DocumentService.cs:14277, 14449`). `rpFundAccount` also lists header-level accounts and 1112x bank GL accounts, which bypasses the bank link (`documents.html:12579-12586`).

22. Payroll rows whose saved source code is no longer listed show "ค่าเริ่มต้น" (`payroll.html:1697-1707`). Nothing is saved, because saving happens only on change, but the display is wrong. At posting time an inactive code silently falls back to the default (`PayrollService.cs:3052-3054`).

### Checked and fine
- `tax-remittance.html` ↔ `StatutoryRemittanceService.ResolveBankGlAsync` (`:1856-1889`): the `bank:` and `account:` fields are split correctly, each is validated against its own table with `CompanyId`, and posting uses `LinkedAccountId`.
- Payroll SSO settle (`payroll.html:1419-1468` ↔ `PayrollService.cs:5049-5089`): same correct pattern.
- Reclassify payment source on documents (`documents.html:2635-2700` ↔ `DocumentService.cs:8839-8866`): uses `LinkedAccountId` and throws when it is missing.
- POS terminal (`pos.html:1268-1290`): the value is the bank's `linkedAccountId`, it is validated as a CoA id (`PosService.cs:82-95`), and a missing pin is kept with a notice.
- `settlements.html` (platform payouts): `SettlementPostingService.cs:611-614` uses `LinkedAccountId`.
- `payment-settlements.html` ↔ `GatewaySettlementService.cs:590-597`: clear message with a way forward.
- Lodging refund and security deposit (`lodging.html:423-474` ↔ `ResolveRefundMoneyAccountAsync` `:1871-1891`).
- Bank transaction create-JE (`BankController.cs:313-340`): refuses unlinked banks and posts to `LinkedAccountId`.
- `documents.html` edit hydration (`:9565-9581`): adds a missing saved option back instead of switching silently.
- `recurring.html` save parsing (`:532-536`): correct field split; the only problem is the hydration in #7.

Not audited, as instructed: import/ETL code, and `RecurringTransactionService.ValidateTemplateAsync`.

