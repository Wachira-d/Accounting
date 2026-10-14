using System.Globalization;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// **ตัวเขียน statement ธนาคารตัวเดียวของระบบ** (รอบ 205 ทีม C · IMPORT_ETL_REVIEW §1 แถว 6–9)
///
/// <para>═══ ที่มา ═══ เดิมมีตัวเขียน <c>BankTransaction</c> จาก statement 4 ตัว: หน้า "ธนาคาร" (ยอดคงเหลือ = ยอดตาม statement) ·
/// open-banking (คำนวณยอดเดินเอง + OFX/QIF <b>นับบรรทัดแล้วตอบว่า "ใหม่" โดยไม่บันทึกอะไร</b>) · นำเข้าทั่วไป "banktransactions"
/// (<b>บวก</b> <c>CurrentBalance</c> ทีละแถว = อีกความหมาย) · API v1 (กันซ้ำด้วย ExternalId อย่างเดียว) ⇒ ไฟล์เดียวกันผ่านคนละทาง
/// ได้ยอดบัญชีคนละค่า และ dedupe คนละกติกา · ตอนนี้ทุกทางแยกไฟล์/คำขอเป็น <see cref="BankStatementLineInput"/> แล้วเรียกที่นี่</para>
///
/// <para>ความหมายยอด (ตัวเดียว): <c>CurrentBalance</c>/<c>StatementBalance</c> = ยอดคงเหลือ<b>ตามธนาคาร</b>ของบรรทัดล่าสุดที่มียอดจริง
/// (หรือยอดปิด OFX LEDGERBAL) · ไฟล์ไม่มียอด ⇒ ไม่แตะการ์ดบัญชี + คำเตือน (ไม่บวกยอดเอง — ค่าที่แต่งขึ้นอันตรายกว่าไม่ตอบ · F2 ข้อ 3) ·
/// statement ที่เก่ากว่ายอดล่าสุดที่มี ⇒ คงยอดเดิม + คำเตือน (ไม่ย้อนยอดตามธนาคารถอยหลัง)</para>
/// </summary>
public partial class BankService
{
    /// <summary>ชนิดไฟล์แนบของไฟล์ statement ต้นทาง (ผูกกับบัญชีธนาคาร · ด่าน = <c>AttachmentPermissionScope</c> "BankStatement")</summary>
    public const string StatementAttachmentEntityType = "BankStatement";

    public async Task<BankStatementWriteResult> ImportParsedStatementRowsAsync(Guid companyId, Guid bankAccountId,
        IReadOnlyList<BankStatementLineInput> lines, BankStatementWriteOptions? options = null, CancellationToken ct = default)
    {
        options ??= new BankStatementWriteOptions();
        var account = await _db.Set<BankAccount>()
            .FirstOrDefaultAsync(a => a.Id == bankAccountId && a.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        var warnings = new List<string>(options.ParserNotes ?? Array.Empty<string>());
        AddSourceIdentityWarnings(account, options, warnings);

        // ── เตรียมบรรทัด: ทิศ + ยอดจากเครื่องหมาย (ช่องถอนเป็นเงินออกเสมอ · มีทั้งสองช่อง = ยอดสุทธิ) ──
        var prepared = new List<PreparedLine>(lines.Count);
        var netted = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Deposit != 0 && l.Withdrawal != 0) netted++;
            var signed = Math.Round(l.Deposit - Math.Abs(l.Withdrawal), 2, MidpointRounding.AwayFromZero);
            var desc = l.Description;
            if (!string.IsNullOrWhiteSpace(l.Channel)) desc = string.IsNullOrWhiteSpace(desc) ? l.Channel : $"{desc} | {l.Channel}";
            if (desc != null && desc.Length > 500) desc = desc[..500];
            var storedRef = !string.IsNullOrWhiteSpace(l.ExternalId) ? l.ExternalId!.Trim()
                : string.IsNullOrWhiteSpace(l.Reference) ? null : l.Reference!.Trim();
            prepared.Add(new PreparedLine(i, l, signed > 0, Math.Abs(signed), desc, storedRef));
        }
        if (netted > 0)
            warnings.Add($"⚠ {netted} บรรทัดมีทั้งเงินเข้าและเงินออกในบรรทัดเดียว — ระบบลงเป็นยอดสุทธิ ตรวจกับ statement อีกครั้ง");

        // ── ยอดคงเหลือ "ล่าสุดจริง" — ไม่ใช่แถวสุดท้ายของไฟล์ (ธนาคารไทยหลายแห่งส่งออกใหม่→เก่า) ──
        var newestFirst = lines.Count > 1 && lines[0].Date > lines[^1].Date;
        BankStatementLineInput? latestRow = null;
        var balanceRows = lines.Where(r => r.Balance.HasValue).ToList();
        if (balanceRows.Count > 0)
        {
            var maxDate = balanceRows.Max(r => r.Date);
            latestRow = newestFirst ? balanceRows.First(r => r.Date == maxDate) : balanceRows.Last(r => r.Date == maxDate);
        }

        // ── ความต่อเนื่องของยอด: ยอดแถวถัดไป = แถวก่อน + เข้า − ออก · ไม่ต่อเนื่อง = ไฟล์ขาดรายการ ──
        var chronological = newestFirst ? Enumerable.Reverse(prepared).ToList() : prepared;
        var continuityBreaks = 0;
        for (var ci = 1; ci < chronological.Count; ci++)
        {
            var prevBal = chronological[ci - 1].Line.Balance;
            var curBal = chronological[ci].Line.Balance;
            if (!prevBal.HasValue || !curBal.HasValue) continue;
            var c = chronological[ci];
            var expected = prevBal.Value + (c.IsDeposit ? c.Amount : -c.Amount);
            if (Math.Abs(expected - curBal.Value) > 0.01m) continuityBreaks++;
        }
        if (continuityBreaks > 0)
            warnings.Add($"⚠ ยอดคงเหลือในไฟล์ไม่ต่อเนื่อง {continuityBreaks} จุด — ไฟล์อาจขาดรายการ " +
                "(ถูกตัดหน้า/กรองบางประเภทออกตอน export) ยอดบัญชีอาจไม่ตรงธนาคารจนกว่าจะนำเข้าครบ");

        // ── ยอดรายบรรทัดเมื่อไฟล์ให้แต่ยอดปิด (OFX LEDGERBAL): ถอยจากยอดปิดตามลำดับเวลา (ข้อมูลของธนาคาร ไม่ใช่ค่าที่เดา) ──
        var derivedBalance = new Dictionary<int, decimal>();
        if (balanceRows.Count == 0 && options.ClosingBalance is { } closing && prepared.Count > 0)
        {
            var asOf = options.ClosingBalanceDate ?? lines.Max(l => l.Date);
            if (lines.All(l => l.Date.Date <= asOf.Date))
            {
                var running = closing;
                foreach (var p in chronological.AsEnumerable().Reverse())
                {
                    derivedBalance[p.Index] = running;
                    running -= p.IsDeposit ? p.Amount : -p.Amount;
                }
            }
        }

        // ── ของเดิมสำหรับกันซ้ำ (เฉพาะช่วงวันของไฟล์ + รหัสต้นทางทุกช่วงวัน) ──
        var existing = new List<ExistingTxn>();
        var valid = prepared.Where(p => p.Amount > 0).ToList();
        if (valid.Count > 0)
        {
            var from = valid.Min(p => p.Line.Date).Date;
            var to = valid.Max(p => p.Line.Date).Date.AddDays(1);
            var extIds = valid.Where(p => !string.IsNullOrWhiteSpace(p.Line.ExternalId)).Select(p => p.StoredReference!).Distinct().ToList();
            if (options.Dedupe == BankStatementDedupeMode.Statement)
                existing = await _db.Set<BankTransaction>().AsNoTracking()
                    .Where(t => t.CompanyId == companyId && t.BankAccountId == bankAccountId
                        && ((t.TransactionDate >= from && t.TransactionDate < to) || (t.Reference != null && extIds.Contains(t.Reference))))
                    .Select(t => new ExistingTxn(t.Id, t.TransactionDate, t.Amount, t.TransactionType, t.Description, t.Reference))
                    .ToListAsync(ct);
            else if (extIds.Count > 0)
                existing = await _db.Set<BankTransaction>().AsNoTracking()
                    .Where(t => t.CompanyId == companyId && t.BankAccountId == bankAccountId
                        && t.Reference != null && extIds.Contains(t.Reference))
                    .Select(t => new ExistingTxn(t.Id, t.TransactionDate, t.Amount, t.TransactionType, t.Description, t.Reference))
                    .ToListAsync(ct);
        }

        var results = new BankStatementLineResult?[lines.Count];
        var conflicts = new List<ImportConflict>();
        var added = new List<BankTransaction>();
        var overwritten = new List<BankTransaction>();   // แถวเดิมที่โหมด Overwrite แก้ — ล้มแล้วต้องคืนค่าใน tracker (ฝ่ายค้านรอบ 1 P2)
        int imported = 0, skipped = 0, insertedDespiteConflict = 0;
        var usedExisting = new HashSet<Guid>();
        var seenExternal = new HashSet<string>(StringComparer.Ordinal);

        var ownTx = _db.Database.CurrentTransaction == null;
        IDbContextTransaction? dbTransaction = ownTx ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            foreach (var p in prepared)
            {
                var r = p.Line;
                var rowNum = r.RowNumber > 0 ? r.RowNumber : p.Index + 1;
                if (p.Amount <= 0)
                {
                    results[p.Index] = new(p.Index, BankStatementLineOutcome.Invalid, null, null, $"แถว {rowNum}: ไม่มีจำนวนเงิน — ไม่ได้นำเข้า");
                    continue;
                }
                var txnType = p.IsDeposit ? BankTransactionType.Deposit : BankTransactionType.Withdrawal;

                // (1) รหัสต้นทางตรง = รายการเดียวกันแน่นอน (OFX FITID · API v1 ExternalId) — รวมซ้ำภายในไฟล์เดียวกัน
                if (!string.IsNullOrWhiteSpace(r.ExternalId))
                {
                    var byExt = existing.FirstOrDefault(t => t.Reference == p.StoredReference);
                    if (byExt != null || !seenExternal.Add(p.StoredReference!))
                    {
                        skipped++;
                        results[p.Index] = new(p.Index, BankStatementLineOutcome.Duplicate, null, byExt?.Id,
                            $"แถว {rowNum}: รหัสรายการ {p.StoredReference} นำเข้าไปแล้ว");
                        continue;
                    }
                }

                if (options.Dedupe == BankStatementDedupeMode.Statement)
                {
                    // (2) เลขอ้างอิง + วัน + ยอด + ทิศ ตรง = รายการเดียวกัน (คำอธิบายอาจต่างเพราะช่องทางต่อท้ายต่างรอบ)
                    var duplicate = p.StoredReference == null ? null : existing.FirstOrDefault(t =>
                        !usedExisting.Contains(t.Id) && t.Reference == p.StoredReference
                        && t.TransactionDate.Date == r.Date.Date && t.Amount == p.Amount && t.TransactionType == txnType);
                    var sameRef = duplicate != null;
                    // (3) วัน + ยอด + ทิศ แบบใช้ครั้งเดียว — statement มี 2 รายการยอดเท่ากันวันเดียวกันได้จริง (PromptPay 55 บาท 2 ครั้ง)
                    duplicate ??= existing.FirstOrDefault(t =>
                        !usedExisting.Contains(t.Id) && t.TransactionDate.Date == r.Date.Date
                        && t.Amount == p.Amount && t.TransactionType == txnType);

                    if (duplicate != null)
                    {
                        usedExisting.Add(duplicate.Id);
                        var sameContent = sameRef || string.Equals((duplicate.Description ?? "").Trim(), (p.Description ?? "").Trim(),
                            StringComparison.OrdinalIgnoreCase);
                        if (sameContent)
                        {
                            skipped++;
                            results[p.Index] = new(p.Index, BankStatementLineOutcome.Duplicate, null, duplicate.Id,
                                $"แถว {rowNum}: มีรายการนี้อยู่แล้ว");
                            continue;
                        }

                        switch (options.OnConflict)
                        {
                            case BankStatementConflictPolicy.Report:
                            case BankStatementConflictPolicy.KeepExisting:
                                conflicts.Add(new ImportConflict(rowNum, r.Date, p.Amount, p.Description, duplicate.Description, duplicate.Id));
                                if (options.OnConflict == BankStatementConflictPolicy.KeepExisting) skipped++;
                                results[p.Index] = new(p.Index, BankStatementLineOutcome.Conflict, null, duplicate.Id,
                                    $"แถว {rowNum}: วันที่/ยอดตรงรายการเดิมแต่คำอธิบายต่าง (เดิม: {duplicate.Description})");
                                continue;
                            case BankStatementConflictPolicy.Overwrite:
                            {
                                var existingEntity = await _db.Set<BankTransaction>()
                                    .FirstAsync(t => t.Id == duplicate.Id && t.CompanyId == companyId, ct);
                                existingEntity.Description = p.Description;
                                existingEntity.Reference = p.StoredReference;
                                if (r.Balance is decimal newBal)
                                {
                                    existingEntity.BalanceAfter = newBal;
                                    existingEntity.BalanceAfterUnknown = false;
                                }
                                if (!string.IsNullOrWhiteSpace(r.Payee)) existingEntity.Payee = r.Payee;
                                existingEntity.UpdatedAt = DateTime.UtcNow;
                                overwritten.Add(existingEntity);
                                imported++;
                                results[p.Index] = new(p.Index, BankStatementLineOutcome.Updated, duplicate.Id, duplicate.Id, null);
                                continue;
                            }
                            case BankStatementConflictPolicy.InsertAsNew:
                                insertedDespiteConflict++;
                                break;   // ลงเป็นรายการใหม่ข้างล่าง
                        }
                    }
                }

                // ฝ่ายค้านรอบ 1 P1: ยอดคงเหลือที่ไฟล์ไม่ให้ (และถอยจากยอดปิดไม่ได้) = "ไม่รู้" — ไม่ใช่ 0 (เดิมลบบรรทัดแล้วการ์ดบัญชีกลายเป็น 0)
                decimal? knownBalance = r.Balance ?? (derivedBalance.TryGetValue(p.Index, out var derived) ? derived : (decimal?)null);
                var entity = new BankTransaction
                {
                    CompanyId = companyId,
                    BankAccountId = bankAccountId,
                    TransactionDate = r.Date,
                    TransactionType = txnType,
                    Amount = p.Amount,
                    BalanceAfter = knownBalance ?? 0m,
                    BalanceAfterUnknown = knownBalance is null,
                    Description = p.Description,
                    Reference = p.StoredReference,
                    Payee = string.IsNullOrWhiteSpace(r.Payee) ? null : r.Payee.Trim(),
                    ReconciliationStatus = ReconciliationStatus.Unmatched,
                };
                if (options.CreatedBy != null) entity.CreatedBy = options.CreatedBy;
                _db.Set<BankTransaction>().Add(entity);
                added.Add(entity);
                imported++;
                results[p.Index] = new(p.Index, BankStatementLineOutcome.Created, entity.Id, null, null);
            }

            if (insertedDespiteConflict > 0)
                warnings.Add($"⚠ {insertedDespiteConflict} รายการวันที่/ยอดตรงกับรายการเดิมแต่คำอธิบายต่าง — ลงเป็นรายการใหม่ ตรวจว่าไม่ซ้ำ");

            // โหมด Report: พบขัดแย้ง ⇒ ไม่บันทึกอะไรเลย (ยังไม่มี SaveChanges ก่อนจุดนี้) + ถอดแถวที่เพิ่มออกจาก tracker
            // (เดิม rollback แล้วแถวค้างใน tracker — SaveChanges ครั้งถัดไปใน scope เดียวกันจะบันทึกแถวที่ "ย้อนแล้ว")
            if (conflicts.Count > 0 && options.OnConflict == BankStatementConflictPolicy.Report)
            {
                foreach (var e in added) _db.Entry(e).State = EntityState.Detached;
                if (dbTransaction != null) await dbTransaction.RollbackAsync(ct);
                return new BankStatementWriteResult(imported, skipped, conflicts.Count, conflicts, warnings,
                    Finish(results), RolledBack: true, AutoMatched: 0, SourceFileAttachmentId: null);
            }

            if (options.UpdateStatementSnapshot)
                ApplyStatementSnapshot(account, latestRow, options, lines, warnings);

            await _db.SaveChangesAsync(ct);

            // ไฟล์ต้นทาง = หลักฐานประกอบรายการ (พ.ร.บ.การบัญชี ม.10 · 5 ปี) · ฝ่ายค้านรอบ 1 P2: เขียนไฟล์<b>หลัง commit</b> เมื่อธุรกรรมเป็นของเรา —
            // เดิมเขียนก่อน commit ⇒ ย้อนธุรกรรมแล้วไฟล์จริงค้างในที่เก็บโดยไม่มีแถวอ้าง (orphan) · ธุรกรรมของผู้เรียก ⇒ อยู่ในธุรกรรมนั้น (ผู้เรียกคุมความเป็นชุด)
            Guid? attachmentId = null;
            if (!ownTx && options.SourceFile is { } callerFile && imported > 0)
                attachmentId = await KeepStatementSourceFileAsync(companyId, bankAccountId, callerFile, options.UploadedByUserId, warnings);

            if (dbTransaction != null) await dbTransaction.CommitAsync(ct);

            if (ownTx && options.SourceFile is { } file && imported > 0)
            {
                try
                {
                    attachmentId = await KeepStatementSourceFileAsync(companyId, bankAccountId, file, options.UploadedByUserId, warnings);
                }
                catch (Exception fex) when (fex is not OperationCanceledException)
                {
                    // รายการธนาคารบันทึกแล้ว (commit แล้ว) — เก็บไฟล์ไม่ได้ = คำเตือนถึงผู้ใช้ + log (ล้มดังที่คำตอบ ไม่ย้อนรายการที่ถูกต้อง)
                    _logger?.LogError(fex, "Bank statement import: keeping source file failed for account {Account}", bankAccountId);
                    DetachAddedAttachments();
                    warnings.Add("⚠ นำเข้ารายการแล้ว แต่เก็บไฟล์ statement ต้นทางไม่สำเร็จ — แนบไฟล์เองที่หน้าบัญชีธนาคาร (หลักฐานตาม พ.ร.บ.การบัญชี ม.10)");
                }
            }

            _logger?.LogInformation("Bank statement import: {Imported} imported, {Skipped} duplicates skipped, {Conflicts} conflicts",
                imported, skipped, conflicts.Count);

            // จับคู่อัตโนมัติหลังบันทึก — ตัวจับคู่ตัวเดียว (AutoMatchAsync) · ล้ม = คำเตือนถึงผู้ใช้ (ไม่ใช่แค่ log) แต่รายการที่นำเข้าคงอยู่
            var autoMatched = 0;
            if (options.AutoMatch && ownTx && added.Count > 0)
            {
                try { autoMatched = (await AutoMatchAsync(companyId, bankAccountId)).Count; }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Auto-match after statement import failed");
                    warnings.Add("⚠ นำเข้าสำเร็จแต่จับคู่อัตโนมัติไม่สำเร็จ — กด \"จับคู่อัตโนมัติ\" ที่หน้าบัญชีธนาคารอีกครั้ง");
                }
            }

            // KeepExisting: บรรทัดขัดแย้งนับใน Skipped แล้ว (ผลต่อบรรทัด = Conflict) — ไม่ส่งเป็น "ขัดแย้งรอเลือก" ซ้ำ
            return new BankStatementWriteResult(imported, skipped, 0, new List<ImportConflict>(),
                warnings, Finish(results), RolledBack: false, autoMatched, attachmentId);
        }
        catch
        {
            if (dbTransaction != null) await dbTransaction.RollbackAsync(CancellationToken.None);
            // ฝ่ายค้านรอบ 1 P2: ถอดของที่เมธอดนี้ทิ้งไว้ใน tracker (แถวใหม่ · แถวเดิมที่ Overwrite แก้ · ยอดการ์ดบัญชี · ไฟล์แนบที่ยังไม่บันทึก) —
            // เดิมค้างอยู่แล้ว SaveChanges ครั้งถัดไปใน scope เดียวกัน (เช่นกลุ่มถัดไปของ BankStatementImportTarget) บันทึกแถวที่ "ล้มแล้ว" ให้
            UndoTracked(added, overwritten, account);
            throw;
        }
        finally
        {
            if (dbTransaction != null) await dbTransaction.DisposeAsync();
        }
    }

    /// <summary>คืน tracker หลังการเขียนล้ม — แถวที่เพิ่ม = ถอด · แถว/บัญชีที่แก้ = คืนค่าเดิม (ไม่ query ซ้ำ — ธุรกรรมของผู้เรียกอาจอยู่ในสถานะล้มแล้ว)</summary>
    private void UndoTracked(List<BankTransaction> added, List<BankTransaction> overwritten, BankAccount account)
    {
        foreach (var e in added) _db.Entry(e).State = EntityState.Detached;
        foreach (var e in overwritten.Cast<object>().Append(account))
        {
            var entry = _db.Entry(e);
            if (entry.State != EntityState.Modified) continue;
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;
        }
        DetachAddedAttachments();
    }

    /// <summary>ไฟล์แนบที่ถูก Add แต่บันทึกไม่ผ่าน — ถอดออกจาก tracker (ไม่งั้น SaveChanges ถัดไปบันทึกแถวที่อ้างไฟล์ซึ่งไม่ได้เก็บ)</summary>
    private void DetachAddedAttachments()
    {
        foreach (var a in _db.ChangeTracker.Entries<FileAttachment>().Where(x => x.State == EntityState.Added).ToList())
            a.State = EntityState.Detached;
    }

    /// <summary>ยอดตามธนาคารบนการ์ดบัญชี — จากบรรทัดล่าสุดที่มียอดจริง หรือยอดปิดของไฟล์ (OFX) · ไม่มียอด ⇒ คงเดิม + คำเตือน ·
    /// statement เก่ากว่ายอดที่มีอยู่ ⇒ คงเดิม + คำเตือน</summary>
    private static void ApplyStatementSnapshot(BankAccount account, BankStatementLineInput? latestRow, BankStatementWriteOptions options,
        IReadOnlyList<BankStatementLineInput> lines, List<string> warnings)
    {
        decimal? balance = latestRow?.Balance;
        DateTime? asOf = latestRow?.Date;
        if (balance == null && options.ClosingBalance is { } closing)
        {
            balance = closing;
            asOf = options.ClosingBalanceDate ?? (lines.Count > 0 ? lines.Max(l => l.Date) : (DateTime?)null);
        }
        if (balance == null || asOf == null)
        {
            warnings.Add("⚠ ไฟล์ไม่มีคอลัมน์ยอดคงเหลือที่อ่านได้ — นำเข้ารายการแล้ว แต่ยอดตามธนาคารบนการ์ดบัญชีไม่ถูกอัปเดต " +
                "และตรวจความต่อเนื่องของยอดไม่ได้ (ส่งออกแบบที่มียอดคงเหลือถ้าธนาคารมีให้)");
            return;
        }
        if (account.StatementBalanceDate is { } current && asOf.Value < current)
        {
            warnings.Add($"ℹ️ statement นี้สิ้นสุด {asOf.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} เก่ากว่ายอดตามธนาคารล่าสุดที่มีอยู่ " +
                $"({current.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}) — คงยอดตามธนาคารเดิมไว้");
            return;
        }
        account.StatementBalance = balance;
        account.StatementBalanceDate = asOf;
        account.StatementImportedAt = DateTime.UtcNow;
        account.CurrentBalance = balance.Value;
    }

    /// <summary>เลขบัญชี/สกุลเงินตามไฟล์ไม่ตรงบัญชีที่เลือก ⇒ คำเตือน (ผู้ใช้ตัดสิน · คำตัดสินข้อ 150) — เทียบเฉพาะตัวเลข 4 หลักท้ายขึ้นไป</summary>
    private static void AddSourceIdentityWarnings(BankAccount account, BankStatementWriteOptions options, List<string> warnings)
    {
        static string Digits(string? s) => new((s ?? "").Where(char.IsAsciiDigit).ToArray());
        var src = Digits(options.SourceAccountNumber);
        var mine = Digits(account.AccountNumber);
        if (src.Length >= 4 && mine.Length >= 4 && !src.EndsWith(mine, StringComparison.Ordinal) && !mine.EndsWith(src, StringComparison.Ordinal))
            warnings.Add($"⚠ เลขบัญชีในไฟล์ (…{src[^4..]}) ไม่ตรงกับบัญชีที่เลือก \"{account.AccountName}\" (…{mine[^4..]}) — ตรวจว่าเลือกบัญชีถูก");
        if (!string.IsNullOrWhiteSpace(options.SourceCurrency) && !string.IsNullOrWhiteSpace(account.Currency)
            && !string.Equals(options.SourceCurrency.Trim(), account.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
            warnings.Add($"⚠ สกุลเงินในไฟล์ ({options.SourceCurrency.Trim()}) ต่างจากสกุลของบัญชี ({account.Currency}) — ตรวจว่าเลือกบัญชีถูก");
    }

    /// <summary>เก็บไฟล์ต้นทางเป็นไฟล์แนบของบัญชีธนาคาร · ไม่ทราบผู้นำเข้า (ไม่มี JWT) ⇒ ไม่เก็บ + คำเตือน (FK ผู้อัปโหลดห้ามเป็นค่าว่าง)</summary>
    private async Task<Guid?> KeepStatementSourceFileAsync(Guid companyId, Guid bankAccountId, BankStatementSourceFile file,
        Guid? uploadedBy, List<string> warnings)
    {
        if (_files == null)
        {
            warnings.Add("ℹ️ ไม่ได้เก็บไฟล์ต้นทาง (ระบบไฟล์แนบไม่พร้อมในทางเข้านี้) — เก็บไฟล์ statement ไว้เองเป็นหลักฐาน");
            return null;
        }
        var userId = uploadedBy ?? await ResolveCurrentUserIdAsync(companyId);
        if (userId == Guid.Empty)
        {
            warnings.Add("ℹ️ ไม่ได้เก็บไฟล์ต้นทาง (ไม่ทราบผู้นำเข้า) — เก็บไฟล์ statement ไว้เองเป็นหลักฐาน");
            return null;
        }
        var att = await _files.UploadBytesAsync(companyId, StatementAttachmentEntityType, bankAccountId,
            file.FileName, file.ContentType, file.Content, userId);
        return att.Id;
    }

    private static IReadOnlyList<BankStatementLineResult> Finish(BankStatementLineResult?[] results)
        => results.Select((r, i) => r ?? new BankStatementLineResult(i, BankStatementLineOutcome.Invalid, null, null, null)).ToList();

    private sealed record PreparedLine(int Index, BankStatementLineInput Line, bool IsDeposit, decimal Amount, string? Description, string? StoredReference);

    private sealed record ExistingTxn(Guid Id, DateTime TransactionDate, decimal Amount, BankTransactionType TransactionType,
        string? Description, string? Reference);
}
