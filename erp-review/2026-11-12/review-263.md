# Adversarial review: round 263 (fb083106 + 73dea894 docs)

Scope: recurring "รอบล่าสุด…มีเรื่องต้องทำต่อ" is now built live (`RecurringRunOutcome.Build(…, live)` + `LoadRunLiveAsync`), legacy text is parsed (`FactsFromLegacyNotice`), skipped periods carry over to later runs (`MarkRunAsync`), there is a new `POST dismiss-skipped`, and approver names are filled in (`ActorLabelAsync`, `GetDocumentAsync`). Decisions 263-1-1…5.
Method: I read the code at the branch head. Nothing here was compiled. A finding stays in only if I could trace it to a line.

## Findings

### F1 MED: "แก้ครบแล้ว" can show while some notice text was never checked (F2.3 / F2.7)
- `Accounting/Helpers/RecurringRunOutcome.cs:245`: `FactsFromLegacyNotice` puts the parts it does not recognise into `LegacyOther`. `Build` never reads `LegacyOther`; it is referenced only at lines 43 and 245. The doc comment at line 40 says these parts are "not counted as todo **and not told as resolved**". `Build` does the opposite: `steps.Count == 0` then returns `AllResolved: true` at line 451.
- The same happens on rows that have facts. `AppendRunNoticeTextAsync` (RecurringTransactionService.cs, around line 664) adds text-only notices (`LockedPeriodSkipNote`, `BackfillNote`) to `LastRunNotice` and keeps the old facts. `Build` with facts present never reads that text.
- **Scenario A (legacy row):** the notice holds `AutoApproveFailure.Message(StillDraft)` + `RecurringRunFailure.AfterCreateNotice(...)` (for example, e-Tax or another follow-up step failed). The parser recognises the approval part, and the follow-up part goes into `LegacyOther`. The user approves the document. Result: the table badge disappears and the detail shows "✅ …แก้ครบแล้ว". The failed follow-up is now visible only inside a collapsed `<details>` ("ข้อความจากรอบนั้น").
- **Scenario B (journal):** a catch-up run has one period skipped because the accounting period is closed (lockedNote: "ให้ผู้มีสิทธิ์…") and another period whose auto-post failed (facts `JournalPostBlockedReason`). The user posts the journal. Result: "แก้ครบแล้ว", and the locked-period item is hidden.
- **Minimal fix:** in `Build`, if `facts.LegacyOther` is not empty, or if `legacyNotice` contains a segment that the facts do not explain, do not return `AllResolved`. Add a step such as "ข้อความจากรอบนั้นที่ระบบตรวจสถานะให้ไม่ได้: …". Put the lockedNote into facts (for example `LockedPeriodNotes`) rather than text only.
- **Test to add:** a mixed legacy notice (approval + unknown part) with the document Approved gives `AllResolved == false`. Today the only test is fully-unknown text (`ข้อความแถวเก่าที่ไม่รู้จัก_*`).

### F2 MED: journal catch-up loses the auto-post failure of every period except the last, and then says "แก้ครบแล้ว"
- `RecurringTransactionService.cs:1549`: each period in the catch-up loop (`CatchUpJournalPeriodsAsync`) calls `MarkRunAsync`. Journals have no skip, so `SkipCarry` returns null, and `LastRunOutcome` and `LastRunNotice` are reset to null. The facts `JournalPostBlockedReason` / `JournalEntryId` of the earlier period are gone. The next period overwrites them with its own `SetRunNoticeAsync`.
- **Scenario:** the creator lost `Journal.Manage`, so `autoPostGone` applies to every period. Three periods are overdue, so JE1, JE2 and JE3 are all Draft. The box names only JE3. The user posts JE3 and sees "สมุดรายวัน JE3 ลงบัญชีแล้ว", headline "แก้ครบแล้ว". JE1 and JE2 are still drafts outside the ledger, with nothing telling the user.
- Before round 263 the text stayed up forever, so at least nobody was told "everything is resolved". This commit turns the existing data loss into a false resolution.
- **Minimal fix:** store a list of journal entries per run, for example `JournalPostBlocked: [{Id, No, Reason}]`. `MarkRunAsync` keeps items from the same run, or `CatchUpJournalPeriodsAsync` builds them up and writes once at the end, as lockedNote does. `Build` resolves the whole set only when every entry is Posted, Voided or Reversed.

### F3 MED: "Paid in full ends the email item" is also applied to tax invoices
- `RecurringRunOutcome.cs:361`: `isPaid` ends the email item with the text "ไม่ต้องส่งเอกสารเรียกเก็บทางอีเมลอีก", whatever the document type.
- Recurring invoices can be `CombinedInvoiceTaxInvoice` (RecurringTransactionService.cs:1235). The reason given in decision 263-1-2 ("ลูกค้าจ่ายแล้วไม่ต้องการใบเรียกเก็บ") is true only for billing documents. A tax invoice still has to be delivered to the buyer: §86 says the seller "จัดทำและส่งมอบ" the tax invoice, and the buyer needs it to claim input VAT.
- **Scenario:** a recurring combined invoice/tax invoice with AutoSendEmail, and the contact has no email. The customer pays in full by transfer. The box says "แก้ครบแล้ว", but the buyer never received the tax invoice.
- **Minimal fix:** apply the Paid shortcut only when the document is a billing document and not a tax invoice (`Type ∈ {Invoice, BillingNote}` && `!CombinedInvoiceTaxInvoice`). The tax-invoice case falls through to the existing steps. Then update decision 263-1-2 to state the scope.

### F4 MED: "ออกเองแล้ว/ไม่ต้องออก" has no audit trail, and the next run erases the stamp
- `RecurringTransactionService.cs:883`: `DismissSkippedPeriodsAsync` writes with `ExecuteUpdateAsync`. That bypasses the ChangeTracker, so no automatic audit row is created, and there is no `AddChainedAuditLog`.
- The who and when are kept only inside the JSON. The next `MarkRunAsync` drops them: `SkipCarry` returns null when `SkippedDismissedAt != null`, and both columns become null.
- **Scenario:** an employee dismisses 3 skipped tax-invoice periods that were never issued (unbilled revenue). After the next run there is no trace of who closed the item. This conflicts with rule M (audit append-only).
- **Minimal fix:** write `AddChainedAuditLog` (EntityType `RecurringTransaction`, action `RECURRING-SKIP-DISMISS`, before = count/from, after = dismissed, reason) in the same request. Add a `required_call_site_check` must-rule for it.

### F5 LOW: dismiss does not take the run lock
- `DismissSkippedPeriodsAsync` does not use `JobLock.RunExclusiveAsync(AdvisoryLockKey.RecurringRun, id)`. `MarkRunAsync` reads `currentRun` and then writes it back with `SaveChanges`, forcing `IsModified`.
- If a dismiss lands between that read and write, the dismissal is lost and the item comes back with no error. The window is small.
- **Fix:** wrap the dismiss in the same lock, or compare-and-set on the old `LastRunOutcome` value.

### F6 LOW: the email evidence is broader than the message claims
- `LoadRunLiveAsync` counts as "sent" any `DocumentEmailLog` with status Sent for the document, and any `EmailQueue` with EntityType=Document, whatever the recipient. That includes an email the user sent to themselves, or a reminder queued by another rule.
- The message then says "ส่งอีเมล…**ถึงผู้ติดต่อ**แล้ว".
- **Fix:** either change the wording to "มีบันทึกส่งอีเมลเอกสารนี้แล้ว (ถึง {ToEmail})", or filter on the `ToEmail` that the recipient resolver returns for the contact.

### F7 LOW: a deleted draft journal keeps the warning, and user-name lookups are not scoped to the company
- (a) If the draft JE is deleted, `live.Journal` is null and the `default` case keeps showing "ยังเป็นร่าง" with no `openJournal` button. That stays until the next run. Fix: when `Journal == null && JournalEntryId != null`, resolve as "ไม่พบสมุดรายวันแล้ว (ถูกลบ)".
- (b) `GetDocumentAsync`, `LoadRunLiveAsync` and `ActorLabelAsync` look up `Users.FullName` by any GUID that appears in the note, without checking company membership. The risk is low because no user path writes `InternalNotes` on documents; `CreateDocumentRequest` and `UpdateDocumentRequest` have no such field. Still, as defense in depth, add a join to `CompanyUsers` for `companyId`.

### F8 LOW: three empty "| เทสต์ที่มี |" rows in TEST_PLAN §0
- TEST_PLAN.md:1112-1114 has three empty `| เทสต์ที่มี |` rows. They already existed before this commit; they appear as context lines in the diff. This commit did not add them, but they should be removed.
- `tools/required_call_site_check.py` has no merge-conflict markers. Its new round-263 rules read correctly, but I could not wait for the checker run to finish (it is slow), so CI decides.

## Checked and not a bug (do not report again)
- **`GetDocumentAsync` id→name substitution being saved back.** It is not saved back. `InternalNotes` exists only on `DocumentResponse` (DocumentDtos.cs:954). `documents.html` only displays it (line 11245), and neither Create nor Update accepts it. The name is persisted only through `MarkResolved` at approval time, and only when `HasOpenFailure` is true, which is the same condition `MarkResolved` checks itself.
- **`MarkRunAsync` forced `IsModified` reading from the database.** This is correct. `SkipOverduePeriodsAsync`/`SetRunNoticeAsync` write with `ExecuteUpdate`, which makes the tracked values stale. Without `IsModified`, setting null over a stale null would issue no UPDATE.
- **A failed run losing the skip item.** It is not lost. `SkipOverduePeriodsAsync` writes before the run, and `RecordRunFailureAsync` writes only `LastRunError`. The next attempt has `CatchUp` = 0, so nothing is counted twice.
- **Removing the second skip-note write.** This is correct. `SetRunNoticeAsync` brings the carry back when `facts.SkippedPeriods == 0`.
- **Tenant isolation of the live queries.** Documents, DocumentEmailLogs, EmailQueues, LineQueues and JournalEntries are all filtered by `CompanyId == companyId`, and the dismiss query filters by `r.CompanyId == companyId`.
- **N+1 on the list page.** There is none. The page is paged with `Take(PageSize)`, and the live evidence is one batched query per table. The per-contact `DocumentEmailToAsync` loop existed before this commit.
- **Dismiss permission.** `DenyRecurringWriteAsync` runs before the write and uses the same permission as editing.
- **Autopilot or the notification bell reading stale text.** Neither reads `LastRunNotice` or `LastRunOutcome`. The only reader is the recurring page, and its export now uses the live outcome.
- **Counting an extra run as one issued period, and "approval ends at any effective, non-voided status".** Both match decision 263-1-3 and 263-1-5 as written. The page tells the user that the extra document is dated today.
- **Brittle legacy regexes.** If a regex fails to match, the text falls back to the old display, which is safe. The real problem is a partial match, which is F1.
