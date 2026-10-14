# ตรวจช่องทางรับ/จ่ายเงิน + รหัสผิดตาราง/อ้างอิงค้าง ทั้งระบบ — 2026-10-14

## ส่วนที่ 1 — ช่องทางรับ/จ่ายเงินทุกหน้า (ทีม PC)

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

**3. `payments.html`: the bank the user picks is ignored** — ✅ (1b39ec9f · ทีม F2)
- The payload sends `bankAccountId` (`payments.html:204`), but `CreatePaymentRequest` has no such field. It only has `OverrideBankAccountId` / `OverridePaymentAccountId` (`DocumentDtos.cs:1588-1605`).
- So the payment posts to `doc.BankAccountId`, or to cash 111 when the document has none (`DocumentService.cs:14277`). The page is in the nav (`layout.js:1282`).
- Fix: rename the field to `overrideBankAccountId`.

**4. A bank with no GL link (or a stale or wrong id) is posted silently to cash, or not posted at all** — ✅ (1b39ec9f · ทีม F2)
- Document auto-post (`DocumentService.cs:17168-17180`): `PaymentAccountId` → `bank.LinkedAccount` → `FindAccountAsync("111")`.
  - If the bank's `LinkedAccountId` is null, or the id is from another company or simply wrong, the entry goes to 11111 Cash with no message.
- Payment JE (`:19527-19541`, duplicated in `ResolvePaymentCashAccountAsync` at `:19435-19450`) has the same fallback.
  - `if (cashAccount == null) return;` at `:19541` saves the payment with **no JE at all** when the chart has no 111x account (custom charts).
- At the same time, `BankAccount.CurrentBalance` is still updated for the bank (`:14449-14459`), so the bank register and the GL drift apart.
- Fix:
  - Make one shared resolver that throws a business error naming the bank and pointing to "บัญชีธนาคาร → ผูกผังบัญชี" when the link is missing, instead of falling back.
  - Throw instead of `return` when no cash account is found.

**5. The document's own "แหล่งเงิน" (`PaymentAccountId`) is not carried into a later payment** — ✅ (1b39ec9f · ทีม F2)
- `Payment` copies only `BankAccountId = request.OverrideBankAccountId ?? doc.BankAccountId`. `OverridePaymentAccountId` comes only from the request (`DocumentService.cs:14277-14279`).
- Scenario: a document whose channel is `account:` (petty cash 11112, director advance 21230) is paid later through the record-payment modal, whose label says "— ใช้บัญชีตามที่ผูกในเอกสาร (default) —" (`documents.html:12366-12368`). The payment posts to cash 111.
- The multi-document path also ignores `OverridePaymentAccountId` (`:14736-14737`). That path is API-only.
- Fix: `OverridePaymentAccountId = request.OverridePaymentAccountId ?? (request.OverrideBankAccountId == null ? doc.PaymentAccountId : null)`.

**6. Settings: saving any setting wipes the default payment account** ✅ (6b406e09 — ทีม F3)
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

**8. Lodging: deposits and checkout collection have no bank picker** ✅ (6b406e09 — ทีม F3: บังคับเมื่อโอน/พร้อมเพย์ · บัตร/e-Wallet ไม่บังคับ เพราะลงบัญชีพักของช่องทางนั้น)
- `lodgingConfirm` (`lodging.html:344`) and `lodgingCheckOut` with `collectBalanceNow` (`:392`) send a payment method (often a transfer) but no `bankAccountId`.
- The server supports it (`LodgingService.Lifecycle.cs:723, 1538`), but receives null, so bank transfers land in 11111 Cash.
- Fix: add a bank select, required when the method is not cash.

**9. Creating a bank account does not validate `LinkedAccountId`** — ✅ (1b39ec9f · ทีม F2)
- The update path checks company, active and 111x (`BankService.cs:225-238`). The create path uses `request.LinkedAccountId` with no check (`:71-90`).
- A cross-tenant or non-cash GL id is accepted.
- Fix: run the same validation on create.

**10. Document create/update stores `BankAccountId` / `PaymentAccountId` with no validation** — ✅ (1b39ec9f · ทีม F2) (ด่านเดียวกับ ID#7)
- See `DocumentService.cs:1242-1243, 2967-2970`.
- A swapped id (bank id in `PaymentAccountId`) gives an FK 500 (`AccountingDbContext.cs:868-870`).
- A cross-tenant id passes the FK check and then silently posts to cash (see #4).
- Fix: validate both fields against their own tables with `CompanyId`, at save time.

**11. Hard-coded code prefixes break tenants with a custom chart of accounts** ✅ (6b406e09 — ทีม F3: server + payroll/tax-remittance · ค้าง: BankService:234/104-124 + bank.html ของทีม F2 · ตัวกรอง documents.html/recurring.html ของทีมอื่น — endpoint ค่าเดิมคืนเฉพาะกลุ่ม other แล้ว ลบตัวกรองได้)
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

**12. Payroll's default source posts to a group account** ✅ (6b406e09 — ทีม F3)
- When no source is chosen, net pay is credited to `11122` (`PayrollService.cs:3016-3023`). That is the parent of the auto-created bank accounts `11122-001` and so on, so bank reconciliation never sees it.
- This also ignores `CompanySettings.DefaultPaymentAccountId`, which SSO settlement and tax remittance do use.
- Fix: use the same default resolver as `ResolveBankGlAsync`.

### P2 — UX, messages, small mismatches

13. `documents.html:2264` matches an OCR channel code with `b.accountCode`, which does not exist on `BankAccountResponse` (`BankDtos.cs:26-38` has `linkedAccountCode`). A bank's GL code therefore falls through to `coaByCode` and becomes `account:<bank GL>`. The document has no `BankAccountId`, so later payments go to cash and the bank balance does not sync. Fix: match `b.linkedAccountCode`. — ✅ (1b39ec9f · ทีม F2)

14. The document-template default never applies. `document-templates.html:540-543` stores a raw CoA id (and includes 1112x). `documents.html:6290-6293` compares that raw GUID with `bank:…`/`account:…` option values, so it never matches. — ✅ (1b39ec9f · ทีม F2) (เซิร์ฟเวอร์คืน `defaultPaymentChannel`)

15. Hydration precedence is inconsistent. The edit form prefers bank over `PaymentAccountId` (`documents.html:9569-9570`), while posting and the credit-note prefill prefer `PaymentAccountId` (`DocumentService.cs:17166-17180`, `documents.html:7606`). A document with both set (API/clone) switches its posting account on re-save. — ✅ (1b39ec9f · ทีม F2) (`DocumentResponse.PaymentChannel`)

16. Silent empty dropdowns from `.catch(() => ({data: []}))` or an ignored catch: — ◐ ทีม F2 แก้ส่วน documents.html/payments.html/BankService ✅ (1b39ec9f) · หน้าอื่นอยู่ในขอบเขตทีมอื่น
   - `documents.html:1876-1877, 2648-2649`
   - `recurring.html:446-447`
   - `payroll.html:1375-1376, 1676-1677`
   - `tax-remittance.html:185-186` (also not awaited, so opening the modal early shows only the default option)
   - `payments.html:167`
   - `payment-settlements.html:197`: with no banks the select is empty, `""` is sent as a Guid, and the user gets a cryptic 400.

   The user sees only "cash (default)" and saves, with no warning.

17. Unlinked-bank errors give no way forward: ✅ StatutoryRemittance/Payroll (6b406e09 — ทีม F3 · DocumentService:8852 = ทีม F2)
   - `StatutoryRemittanceService.cs:1877`
   - `PayrollService.cs:5072`
   - `DocumentService.cs:8852` ("— ตั้งค่าก่อน")

   Follow the wording in `GatewaySettlementService.cs:595-597` or `LodgingService.Lifecycle.cs:1878-1879`.

18. Inactive bank accounts are offered everywhere except POS. `GetBankAccountsAsync` returns `IsActive=false` rows (`BankService.cs:163-168`). — ◐ ทีม F2 แก้ส่วน documents.html/payments.html/BankService ✅ (1b39ec9f) · หน้าอื่นอยู่ในขอบเขตทีมอื่น (`GET bank/accounts?activeOnly=true`)

19. These flows have no channel picker and always use cash:
   - Salary-advance disburse sends `bankAccountId: null` (`salary-advance.html:409`).
   - Bulk vendor pay sends no `bankAccountId`/`paymentAccountId` (`cash-management.html:501-503` → `VendorConsolidatedPaymentController.cs:130-131`).
   - Cash-advance disburse accepts `BankAccountId` and ignores it (`CashAdvanceController.cs:134-158`).

20. `_deriveMethodFromChannel` derives the payment method from code prefixes on the client (`documents.html:4630-4637`). Seeded e-wallet 11113 becomes Cash, and 1133x becomes Other. — ⏳ ทีม F2 ยังไม่แก้: ต้องให้เซิร์ฟเวอร์คืนวิธีชำระต่อแถว payment-channels (`AccountingService.GetPaymentChannelAccountsAsync`) ซึ่งผูกกับกติกา "ผังเงิน" ของทีม F3 (PC#11)

21. Paying from a fund account (`rpFundAccount`) still sets `BankAccountId` to the document's bank, so that bank's `CurrentBalance` moves although no money went through it (`DocumentService.cs:14277, 14449`). `rpFundAccount` also lists header-level accounts and 1112x bank GL accounts, which bypasses the bank link (`documents.html:12579-12586`). — ✅ (1b39ec9f · ทีม F2)

22. ✅ (6b406e09 — ทีม F3) Payroll rows whose saved source code is no longer listed show "ค่าเริ่มต้น" (`payroll.html:1697-1707`). Nothing is saved, because saving happens only on change, but the display is wrong. At posting time an inactive code silently falls back to the default (`PayrollService.cs:3052-3054`).

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



## ส่วนที่ 2 — รหัสผิดตาราง / อ้างอิงค้าง / ข้อความ GUID ทั้งระบบ (ทีม ID)

## ID-kind and stale-reference audit: findings, ranked by severity

This was a read-only audit. Every item below was confirmed by opening the code. Import/ETL and the recurring module were excluded as asked. Line numbers are as of HEAD e00896d7.

### P0 — a normal user flow is blocked

**1. Petty cash: same defect class as the recurring bug, plus a dead end**
- **Wrong table.** `wwwroot/pages/petty-cash.html:211-223` (replenish) fills `sourceAccountId` with `accounts[idx].id` from `getBankAccounts()`, which is a **BankAccount.Id**.
  - `Services/Implementations/PettyCash/PettyCashService.cs:162-175` treats that value as a **ChartOfAccount id**. It writes it straight into `JournalEntryLine.AccountId` (lines ~205-215), bypassing AccountingService validation.
  - The server's own fallback at line 165 correctly uses `b.LinkedAccountId`, so page and server disagree.
  - Result: an FK violation (`JournalEntryLines.AccountId` is Restrict). The user gets 409 "รายการนี้ยังถูกอ้างอิงโดยข้อมูลอื่น… ลบหรือเปลี่ยนไม่ได้", which is the wrong diagnosis.
- **Dead end.** `createFund` (petty-cash.html:172) always sends `linkedAccountId: null`. No endpoint or UI exists to set it later (the service has no update method).
  - So every disburse and replenish fails in `PettyCashJePlan` (`Helpers/PettyCashJePlan.cs:74,95`).
  - The message says "ไปที่ตั้งค่าเงินสดย่อย…", but that screen does not exist.
  - Also, the `dExpense` empty option reads "— ไม่โพสต์ JE —" while the server requires an expense account.
- **No tenant checks.** `expenseAccountId`, `linkedAccountId` and `sourceAccountId` are never checked for `CompanyId`.
- **Fix:**
  - On the page, send `b.linkedAccountId` (filter to banks with `linkedAccountId && isActive`).
  - Or add a `sourceBankAccountId` the server maps through `LinkedAccountId`.
  - Add a fund-account picker on create.
  - Validate all three ids against the company's CoA (tenant + active + Level ≥ 4).

**2. Loans: the page payload does not match the DTO**
- `wwwroot/pages/loans.html:173-184` sends `loanNumber, lenderName, startDate, maturityDate`.
- `Models/DTOs/Loan/LoanDtos.cs:3-10` (`CreateLoanRequest`) expects `Name, Lender, DisbursementDate, FirstPaymentDate, LoanAccountId, InterestExpenseAccountId, BankAccountId`.
- `[ApiController]` with nullable enabled makes `Name` required, so creating a loan from the page fails with 400. If it got past that, the dates would default to 0001-01-01 and no accounts would be set.
- Further problems:
  - `Loan.BankAccountId` is actually used as a **CoA id** (`LoanService.cs:95`, `AccountId = loan.BankAccountId.Value`). Any client that sends a BankAccounts.Id hits an FK error.
  - No tenant validation on any of the three account ids.
  - `UpdateLoanRequest` has no account fields, yet repayment errors (`LoanService.cs:365-370`) say "ตั้งค่าในรายละเอียดสินเชื่อ" — another dead end.
  - Repayment ignores `loan.BankAccountId` and always pays from 11122 / first 111x (lines 372-376).

### P1 — wrong data, silent no-op, or security

**3. The Payments page drops the chosen bank** — ✅ (1b39ec9f · ทีม F2)
- `wwwroot/pages/payments.html:204` sends `bankAccountId`.
- `CreatePaymentRequest` (`DocumentDtos.cs:1588`) has no such property; it has `OverrideBankAccountId`, and `BankAccount` as a string.
- The value is silently ignored, so the payment posts to the document's bank or to Cash 111.
- This page is in the nav ("ชำระเงิน / รวมจ่าย").
- **Fix:** rename the field to `overrideBankAccountId`.

**4. The template default payment account is never applied** — ✅ (1b39ec9f · ทีม F2)
- `documents.html:6290-6293` compares the raw `t.defaultPaymentAccountId` (a CoA id) against option values `bank:<BankAccount.id>` / `account:<CoA id>`. It never matches, so the setting is a silent no-op.
- Two compounding problems:
  - `document-templates.html:539-543` lists every 111x account, including 1112 bank GLs.
  - The form only exposes banks as `bank:` options (1112 GLs are excluded from `account:` options).
- **Fix:** map a 1112 GL to `bank:${bankAccounts.find(b=>b.linkedAccountId===id).id}`, otherwise use `account:${id}`. Validate in `DocumentTemplateService.cs:362,457`.

**5. The OCR → document-form handoff resolves banks by a field that does not exist** — ✅ (1b39ec9f · ทีม F2)
- `documents.html:2264` uses `this.bankAccounts.find(b => b.accountCode === code)`, but `BankAccountResponse` has `linkedAccountCode`, not `accountCode`. It never matches.
- The fallback at 2268-2281 then adds `account:<bank GL id>`. The document is saved with:
  - `PaymentAccountId` = the bank GL and `BankAccountId` = null;
  - payment method derived as `Cash` (lines 4626-4636);
  - JE text "จ่ายเงินสด".
- The server-side OCR path (`OcrService.cs:7110-7125`) does this correctly.
- **Fix:** use `b.linkedAccountCode`.

**6. Reclassify payment source recomputes the old GL from current settings** — ✅ (1b39ec9f · ทีม F2)
- `DocumentService.cs:8839-8865` takes the "old" credit GL from the bank account's *current* `LinkedAccountId`, not from the posted JE.
- If the original post fell back to 111 (see #7), or `LinkedAccountId` was changed since (`BankService.cs:227-238` allows that), the correcting JE reverses the wrong GL.
- If the old bank is still unlinked, it throws "บัญชีธนาคาร (เดิม) ยังไม่ได้ผูกผังบัญชี" and the user has no way forward.
- **Fix:** read the money line from the document's posted JE.

**7. Document header money ids are never validated, and posting silently falls back to cash** — ✅ (1b39ec9f · ทีม F2)
- Create/update store `BankAccountId`, `PaymentAccountId` and `ExpenseCategoryId` unchecked (`DocumentService.cs:1242-1244, 2967-2972`). Line `AccountId` *is* validated at line 530.
- At post time, an unresolved id (other tenant, wrong kind, or a bank with no `LinkedAccountId`) silently becomes Cash 111 or 51110:
  - `AutoPost` at 17153-17180;
  - payment JE at 19527-19542 and `ResolvePaymentCashAccountAsync` at 19434-19450;
  - at 19543, `if (cashAccount == null) return;` saves the payment with **no JE at all**.
- A cross-tenant id that satisfies the FK also leaks the other tenant's bank or account name through `d.BankAccount` / `d.PaymentAccount` in `MapToResponse` (19920-19925).
- POS already fails loudly in the same situation (`PosService.Orders.cs:2272-2290`).
- **Fix:** validate on save (tenant, kind, active, bank linked) with a Thai message that names the field. Throw at post instead of falling back.

**8. No money-channel picker, so bank transfers are booked as Cash 111**
- Lodging confirm/checkout: `lodging.html:344,392` never send `bankAccountId`, although the DTO supports it.
- Expense claim pay: `PayExpenseClaimRequest` has no account field, and `ExpenseClaimService.cs:512-527` builds the PV without one.
- Salary advance: `salary-advance.html:409` hard-codes `bankAccountId: null`.
- Cash advance disburse: `CashAdvanceController.cs:134-158` accepts `BankAccountId` and ignores it, and posts no JE.

**9. Mobile approve does not check who the assigned approver is** — ✅ (2f3e7f4f) ทีม F4: มือถือเรียก `ApprovalService.SubmitActionAsync` ตัวเดียว (ผู้อนุมัติของขั้น · สิทธิ์ขั้นสุดท้าย · แก้แถวที่รอ · 403/404 ไทย)
- `MobileApiService.cs:345-450` never checks that the caller is the current step's assigned approver; the web flow does this at `ApprovalService.cs:351`.
- Non-final steps advance and Reject works with no permission check at all. The permission check only runs on the final approval.
- It inserts a new `ApprovalAction` instead of updating the pending one.
- Failures come back as HTTP 200 with English + GUID text ("No pending approval request found for {entityType} {entityId}.").

**10. Cross-tenant workflow signatures** — ✅ (2f3e7f4f) ทีม F4: `Helpers/CrossTenantSignerResolver` + `CompanyMembership` · ผู้อนุมัติเริ่มต้นออกแล้วไม่ลงนามอัตโนมัติ + หมายเหตุ/คำเตือนบนค่าตั้ง
- `CrossTenantWorkflowController.cs:146-147,235-249` and `CrossTenantWorkflowService.cs:214-220` accept `SignatureUserId`, `DefaultApproverUserId` and `DefaultSignatureId` without checking membership of the approving company.
- Any user's signature, including another tenant's, can be stamped on the approval.
- A stale default approver who has left the company keeps auto-signing (`TryAutoApproveAsync` 316-347).

**11. Stale approvers in approval rules** — ✅ (2f3e7f4f) ทีม F4: ส่งไม่ได้ 409 + ทางไปต่อ · คำขอค้าง ⇒ แท็บ "ค้างเพราะผู้อนุมัติออก" + โอนผู้อนุมัติ
- `ApprovalService.cs:226-286` routes to rule steps without re-checking that approvers are still company members.
- The document sticks in WaitingApproval forever, because only that user can act (line 351).

**12. Scheduled emails do not re-check the document at send time** — ✅ (2f3e7f4f) ทีม F4: `Helpers/ScheduledEmailSendCheck` (Skipped + เหตุผล · ยอด ณ เวลาส่ง) · PDF แนบล้มไม่ประทับ Sent
- `EmailScheduleService.cs:438-485` sends queued `Document` emails (with `OffsetDays`) without re-checking the document. Voided or paid invoices are still emailed, with the `BalanceDue` captured at enqueue.
- `BuildAttachmentAsync` (490-512) swallows failures with `LogWarning`, and the item is marked `Sent` without the PDF.

**13. Account ids written straight into JE lines with no tenant/active check** ✅ ยกเว้น LoanService (ทีม F1) (6b406e09 — ทีม F3)
These all accept request ids and write `JournalEntryLine.AccountId` directly, bypassing `AccountingService` validation:
- `FinancialManagementService.cs:68, 191-212, 604-623`; `.Part2.cs:373-397`
- `LoanService.cs:64-103`
- `FixedAssetService.cs:79-80, 318-319`; the nightly `DepreciationBackgroundService` posts with these ids, and failures only `LogError` with no JobRunRecorder.
- Integration mappings: `IntegrationService.cs:296-318` (the `integrationId` is not tenant-checked either); used at 1640-1660 and 2278-2295.
- `PaymentSettingsController.cs:205-208` (`ClearingAccountId` / `FeeExpenseAccountId`), used unchecked by `GatewayAccountResolver.cs:104`.
- Cross-tenant GL lines are possible through the API.

**14. Partner integration problems** ✅ (6b406e09 — ทีม F3)
- `IntegrationService.cs:1091`: `PaymentAccountId` is unvalidated. A wrong kind gives a misleading 23503; another tenant's id makes the JE silently use 111.
- `ProcessDailySummaryAsync` (1633-1637) skips unmapped categories with only `LogWarning`, posts a partial JE and reports success.

**15. Payroll net pay uses a stale account code** ✅ (6b406e09 — ทีม F3: หน้ารอบเตือน + จ่ายไม่ได้พร้อมทางไปต่อ)
- `PayrollService.cs:3016-3055`: an employee's `NetPaymentAccountCode` is validated when set (1450). If that account is deactivated later, posting silently pays from the run default, 11122 or the first 111x.

**16. Create and update of bank accounts don't match** — ✅ (1b39ec9f · ทีม F2)
- `BankService.cs:71-90` (create) does not validate `LinkedAccountId`, while update (227-240) checks tenant, active and 111x.

### P2 — bad messages and smaller gaps

**17. The FK error message assumes a delete.** ✅ (6b406e09 — ทีม F3) `Middleware/ExceptionMiddleware.cs:160-163` maps every 23503 to "ยังถูกอ้างอิง… ลบหรือเปลี่ยนไม่ได้ · ปิดการใช้งานแทน". For inserts with a dangling or wrong-kind FK, this sends the user the wrong way.

**18. Raw GUIDs or English shown to users (worst first)** ✅ บางส่วน (6b406e09 — ทีม F3: AccountingService · WarehouseService · BankService.AiReconciliation · IntegrationService · InventoryCostingService · ค้าง: Mobile/PettyCash/Cheque/Webhook/documents.html ของทีมอื่น)
- `AccountingService.cs:314,739` "ไม่พบบัญชี {guid} ในผังบัญชีของบริษัท" — reached from manual JE, the expense-claim legacy path and other auto-post callers.
- `WarehouseService.cs:297` "สินค้า {ProductId} มีจำนวนไม่เพียงพอ…"
- `BankService.AiReconciliation.cs:791` "ไม่พบรายการธนาคาร {guid}"
- `IntegrationService.cs:4544`
- `MobileApiService.cs:358,513` (English + GUID) — ✅ (2f3e7f4f) ทีม F4
- `documents.html:9576`: the restored option is labelled `bank:<guid>` when no name comes back.
- PettyCash English messages passed through by `SmeOperationsController.cs:55,74` ("Fund not found.", "Insufficient petty cash…").
- English that gets masked to a generic message with no next step: ChequeService `ArgumentException` (`ChequeService.cs:83-84,143`); `InventoryCostingService.cs:71,115,159` "Product {id} not found."; WebhookService 83-195 (✅ (2f3e7f4f) ทีม F4 — 404 ไทยพร้อมทางไปต่อ).

**19. "Not found" reported as "not linked".** ✅ StatutoryRemittance/Payroll/GatewaySettlement/Lodging (6b406e09 — ทีม F3 · DocumentService = ทีม F2) `.Select(b=>b.LinkedAccountId).FirstOrDefault()` returns null for both, and none of these check `IsActive`:
- `StatutoryRemittanceService.cs:1871-1877`
- `PayrollService.cs:5064-5072`
- `DocumentService.cs:8843-8850`
- `GatewaySettlementService.cs:590-597`
- `LodgingService.Lifecycle.cs:1875-1879`

**20. Inactive banks are offered everywhere.** — ◐ ทีม F2 แก้ส่วน documents.html/payments.html/BankService ✅ (1b39ec9f) · หน้าอื่นอยู่ในขอบเขตทีมอื่น
- `GetBankAccountsAsync` (`BankService.cs:163-168`) returns inactive banks.
- documents.html (1886 / 2663 / 12540), payments.html, petty-cash, lodging and payment-settlements don't filter them out.
- Recurring now rejects inactive banks, and settlement import (`SettlementImportService.cs:192`) lacks the `IsActive` check that its update path has.

**21. Field priority disagrees.** — ✅ (1b39ec9f · ทีม F2) `documents.html:9568` hydrates bank first, while the server (17166) and `documents.html:7606` treat `PaymentAccountId` first.

**22. Settings default payment account.** ✅ (6b406e09 — ทีม F3) `settings.html:2517-2528` loads `payment-channels`, which excludes 1112, so a bank can't be chosen as the company default (the comment says otherwise). `SettingsService.cs:244` doesn't validate.

**23. Contact default AR/AP accounts.** ✅ (6b406e09 — ทีม F3: ตรวจตอนบันทึก · ผังที่ปักใช้ไม่ได้ตอนลงบัญชี = ล้มดังพร้อมทางไปต่อ) `Contact.DefaultAr/Ap/IrGrAccountId` are unvalidated (`DocumentService.cs:13485, 13699-13704`). `FindAccountAsync` (15463-15470) silently falls back when the pinned account is inactive.

**24. Manual JE checks are thin.** ✅ ใบสำคัญ (6b406e09 — ทีม F3 · Document `DimensionId` = ทีม F2) JE create/update (`AccountingService.cs:305-314, 413-455, 717-719`) doesn't check account `IsActive` / Level ≥ 4, and doesn't validate `BranchId`, `DimensionId` or line `ProjectId`. Document `DimensionId` (`DocumentService.cs:2963`) is also unvalidated.

**25. Scheduled reports report false success.** — ✅ (2f3e7f4f) ทีม F4: `Helpers/ScheduledReportOutcome` OK/PARTIAL/ERROR/SKIPPED + "ส่งทันที" ส่งจริง (เดิมประทับ OK โดยไม่ส่ง) `ScheduledReportDispatcher.cs:146-150` swallows per-recipient send failures, and `LastResult` stays "OK".

**26. Fields named `BankAccountId` that hold CoA ids.** `PosTerminal.BankAccountId` (the page sends `linkedAccountId`, the server validates against CoA — consistent today but easy to break) and `Loan.BankAccountId`.

### Checked and fine (no need to recheck)
- tax-remittance.html / payroll.html SSO `bank:`/`account:` parsing, and the server resolvers (StatutoryRemittance, Payroll SSO): kinds are correct; only the #19 nits apply.
- documents.html save (9855), reclassify modal (2676), record-payment `rpBankOverride` / `rpFundAccount`: kinds are correct.
- Lodging refund and security-deposit flows: bank → `LinkedAccountId` with `CompanyId`, and they fail loudly.
- Settlement import/post/update bank and `SettlementChartIndex`: tenant-safe.
- Gateway settlement account resolution: correct.
- POS terminal money-pin validation (`UsableTerminalMoneyPin`): correct.
- Server-side OCR credit-code mapping: correct.
- AiSuggestion payment-channel values match the page, and the page checks the option exists.
- Document line `AccountId`, `ProjectId` and `BranchId` validation: correct.
- ApprovalService rule validation (members + project tenant): correct.
- BankController quick-match counterpart account check: correct.
- BankV1Controller bank tenant check: correct.
- Cheque page sends BankAccounts.Id consistently (though the service has no tenant validation).
- NotificationEngine recipient lookups are tenant-scoped.
- No Product-vs-Variant or Warehouse-vs-Branch mix-ups found in non-import code.
- Bank accounts cannot be deleted, only deactivated, so "deleted bank" stale cases are theoretical; inactive is the real case.

