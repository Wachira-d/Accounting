using Accounting.Helpers;
using Accounting.Models.DTOs.CourseCredit;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 · P5 (R-209-C5 · คำตัดสินข้อ 168 ฎ) — สมาชิก "ไม่จำกัดครั้งตามช่วงเวลา" · พักบัตร · ขยายอายุ · โอนบัตร · แจ้งเตือนใกล้หมดอายุ
/// <para>ตัวเลขทั้งหมดจาก <see cref="CourseCreditMath"/> (pure + เทสต์ <c>CourseCreditMembershipTests</c>) · JE ผ่าน <c>PostRecognitionAsync</c> ตัวเดิม
/// (<c>JournalEntryBuilder</c> · ด่านงวดปิดเดียวกัน) · ทุกการขยับล็อกแถวบัตรในธุรกรรมเดียวกับ JE + สมุด + audit (<c>AddChainedAuditLog</c>)</para>
/// </summary>
public partial class CourseCreditService
{
    /// <summary>วันหมดอายุของบัตรใหม่จากแพ็กเกจ — สมาชิกไม่จำกัด = วันเริ่ม + วัน − 1 (30 วันใช้ได้ 30 วัน) · บัตรครั้ง = สูตรเดิม (<see cref="CourseCreditMath.ExpiresOn"/>)</summary>
    private static DateTime? PassExpiresOn(CreditPackage pkg, DateTime purchasedAt)
        => CourseCreditMath.IsUnlimited(pkg.Kind)
            ? CourseCreditMath.UnlimitedExpiresOn(ThaiDate.CalendarDateUtc(purchasedAt), pkg.ValidityDays, 0)
            : CourseCreditMath.ExpiresOn(purchasedAt, pkg.ValidityDays);

    private static CourseCreditMath.PeriodFacts PeriodFactsOf(CreditPass p, IReadOnlyCollection<CourseCreditMath.FreezeSpan> freezes)
        => new(CourseCreditMath.EffectiveContract(p.ContractAmount, p.RefundedAmount), p.PeriodDays,
            p.PeriodStartsOn ?? ThaiDate.CalendarDateUtc(p.PurchasedAt), p.PeriodBaseRecognized, p.PeriodBaseActiveDays,
            p.RecognizedAmount, p.RecognizedThroughDate, freezes);

    private async Task<List<CourseCreditMath.FreezeSpan>> LoadFreezeSpansAsync(CreditPass pass, CancellationToken ct)
        => (await _db.CreditPassFreezes.AsNoTracking()
                .Where(f => f.CompanyId == pass.CompanyId && f.PassId == pass.Id && f.CancelledAt == null)
                .Select(f => new { f.FromDate, f.ToDate }).ToListAsync(ct))
            .Select(f => new CourseCreditMath.FreezeSpan(f.FromDate, f.ToDate)).ToList();

    /// <summary>ช่วงเวลา UTC ของ "วันไทย" ที่ <paramref name="startUtc"/> ตกอยู่ [00:00, 24:00) เวลาไทย</summary>
    private static (DateTime From, DateTime To) ThaiDayRangeUtc(DateTime startUtc)
    {
        var day = ThaiDate.CalendarDateUtc(startUtc);
        var dayStart = DateTime.SpecifyKind(day.AddHours(-7), DateTimeKind.Utc);
        return (dayStart, dayStart.AddDays(1));
    }

    /// <summary>ตัวตัดสินการจองตัวเดียวของทุกทางเข้า (<c>BookAsync</c> · <c>HoldAsync</c>) — เรียก<b>ใต้ล็อกบัตร</b> ·
    /// บัตรครั้ง = <see cref="CourseCreditMath.CannotBookReason"/> (เดิม) + ช่วงพัก (เฉพาะบัตรที่เคยพัก — บัตรที่ไม่เคยพักไม่มีคิวรีเพิ่ม) ·
    /// สมาชิกไม่จำกัด = <see cref="CourseCreditMath.CannotBookUnlimitedReason"/> (ไม่ตัดเครดิต · เพดานต่อวัน/จองค้าง/ช่วงพัก)</summary>
    private async Task<string?> BookReasonAsync(CreditPass pass, decimal credits, DateTime startUtc, int seats, Guid? serviceId,
        CreditClassFormat format, CancellationToken ct)
    {
        if (!CourseCreditMath.IsUnlimited(pass.Kind))
        {
            var reason = CourseCreditMath.CannotBookReason(Facts(pass), credits, startUtc, serviceId, format, DateTime.UtcNow);
            if (reason != null || pass.FrozenDays <= 0) return reason;
            return CourseCreditMath.FrozenOnReason(await LoadFreezeSpansAsync(pass, ct), startUtc);
        }
        var freezes = await LoadFreezeSpansAsync(pass, ct);
        var (dayFrom, dayTo) = ThaiDayRangeUtc(startUtc);
        var sameDay = await _db.CreditClassBookings.AsNoTracking().CountAsync(b => b.CompanyId == pass.CompanyId && b.PassId == pass.Id
            && b.Status != CreditBookingStatus.Cancelled && b.StartAt >= dayFrom && b.StartAt < dayTo, ct);
        var active = await _db.CreditClassBookings.AsNoTracking().CountAsync(b => b.CompanyId == pass.CompanyId && b.PassId == pass.Id
            && b.Status == CreditBookingStatus.Booked, ct);
        var facts = new CourseCreditMath.UnlimitedFacts(pass.Status, pass.ExpiresOn, pass.PeriodStartsOn, pass.MaxBookingsPerDay,
            pass.MaxActiveBookings, freezes, pass.ClassFormat, ParseIds(pass.AllowedServiceIds));
        return CourseCreditMath.CannotBookUnlimitedReason(facts, startUtc, seats, sameDay, active, serviceId, format, DateTime.UtcNow);
    }

    // ===================================================================== รับรู้รายได้ตามวัน (สมาชิกไม่จำกัด)

    /// <summary>รับรู้รายได้ของบัตรสมาชิกไม่จำกัดถึง <paramref name="through"/> (ไม่เกินวันหมดอายุ) ตามแผน <see cref="CourseCreditMath.PeriodRecognitionPlan"/>
    /// — หนึ่ง JE ต่อเดือนปฏิทิน (Dr 21713 · Cr รายได้) ลงวันสุดท้ายของช่วง · ประทับ <c>RecognizedThroughDate</c> · เรียกใต้ล็อกบัตรเท่านั้น · คืนยอดที่รับรู้</summary>
    private async Task<decimal> RecognizePeriodCoreAsync(CreditPass pass, DateTime through, string actor, CancellationToken ct)
    {
        if (!CourseCreditMath.IsUnlimited(pass.Kind) || pass.Status != CreditPassStatus.Active) return 0m;
        var throughDate = through.Date;
        if (pass.ExpiresOn is DateTime e && throughDate > e.Date) throughDate = e.Date;
        var freezes = await LoadFreezeSpansAsync(pass, ct);
        var plan = CourseCreditMath.PeriodRecognitionPlan(PeriodFactsOf(pass, freezes), DateTime.SpecifyKind(throughDate, DateTimeKind.Utc));
        if (plan.Count == 0) return 0m;
        if (plan.Any(x => x.Amount > 0m)) await EnsureSaleDocumentLiveAsync(pass, ct);
        decimal total = 0m;
        var jeIds = new List<Guid>();
        foreach (var seg in plan)
        {
            if (seg.Amount > 0m)
            {
                var je = await PostRecognitionAsync(pass, seg.Amount, seg.PostingDate,
                    $"สมาชิกไม่จำกัด — รับรู้ถึง {ThaiDate.ToThaiDisplayString(seg.PostingDate)} (วันใช้งาน {seg.ActiveDays}/{pass.PeriodDays})", actor, ct);
                pass.RecognizedAmount += seg.Amount;
                total += seg.Amount;
                jeIds.Add(je.Id);
                _db.CreditLedgerEntries.Add(new CreditLedgerEntry
                {
                    CompanyId = pass.CompanyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.PeriodRecognize,
                    Credits = 0m, RecognizedAmount = seg.Amount, JournalEntryId = je.Id,
                    Note = $"รับรู้ตามวันถึง {ThaiDate.ToThaiDisplayString(seg.PostingDate)} · วันใช้งาน {seg.ActiveDays}/{pass.PeriodDays}"
                        + (je.DateNote != null ? $" · {je.DateNote}" : ""),
                });
            }
            pass.RecognizedThroughDate = seg.PostingDate;
        }
        pass.UpdatedAt = DateTime.UtcNow; pass.UpdatedBy = actor;
        if (total > 0m)
            _db.AddChainedAuditLog(Audit(pass.CompanyId, actor, AuditAction.Update, "CreditPass", pass.Id, new
            {
                action = "PeriodRecognize", pass = pass.PassNumber, recognized = total, through = pass.RecognizedThroughDate,
                journalEntryIds = jeIds, ruleCode = CourseCreditMath.MembershipRuleCode, legalReference = CourseCreditMath.MembershipLegalReference,
            }));
        return total;
    }

    /// <summary>งานรายคืน/ปุ่ม: รับรู้รายได้สมาชิกไม่จำกัดทุกบัตรของบริษัทถึง "เมื่อวาน" (วันที่ผ่านไปครบแล้ว) — บัตรเดียวล้มไม่หยุดทั้งรอบ (ล้มดังในผล + log Error)</summary>
    public async Task<CreditPeriodRunResult> RecognizePeriodDueAsync(Guid companyId, string actor, CancellationToken ct = default)
    {
        var yesterday = ThaiDate.CalendarDateUtc(DateTime.UtcNow).AddDays(-1);
        var due = await _db.CreditPasses.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.Kind == CreditPackageKind.UnlimitedPeriod && p.Status == CreditPassStatus.Active
                && p.PeriodStartsOn != null && p.PeriodStartsOn <= yesterday
                && (p.RecognizedThroughDate == null || (p.RecognizedThroughDate < yesterday && (p.ExpiresOn == null || p.RecognizedThroughDate < p.ExpiresOn))))
            .OrderBy(p => p.PeriodStartsOn).Select(p => new { p.Id, p.PassNumber }).Take(1000).ToListAsync(ct);
        int n = 0, failed = 0; decimal total = 0m; var messages = new List<string>();
        foreach (var d in due)
        {
            try
            {
                total += await InTxAsync(async () =>
                {
                    var pass = await LockPassAsync(companyId, d.Id, ct);
                    var amount = await RecognizePeriodCoreAsync(pass, yesterday, actor, ct);
                    await _db.SaveChangesAsync(ct);
                    return amount;
                }, ct);
                n++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++; messages.Add($"{d.PassNumber}: {ex.Message}");
                _logger.LogError(ex, "รับรู้รายได้สมาชิกไม่จำกัด {Pass} (บริษัท {Cid}) ไม่สำเร็จ", d.PassNumber, companyId);
            }
        }
        return new CreditPeriodRunResult(n, failed, total, messages);
    }

    // ===================================================================== พักบัตร / ขยายอายุ

    public async Task<CreditPassResponse> FreezePassAsync(Guid companyId, Guid passId, FreezeCreditPassRequest request, string actor, CancellationToken ct = default)
    {
        var fromDay = ThaiDate.CalendarDateUtc(request.FromDate);
        var toDay = ThaiDate.CalendarDateUtc(request.ToDate);
        await InTxAsync(async () =>
        {
            var pass = await LockPassAsync(companyId, passId, ct);
            var spans = await LoadFreezeSpansAsync(pass, ct);
            var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
            var rangeFrom = DateTime.SpecifyKind(fromDay.AddHours(-7), DateTimeKind.Utc);
            var rangeTo = DateTime.SpecifyKind(toDay.AddDays(1).AddHours(-7), DateTimeKind.Utc);
            var openInRange = await _db.CreditClassBookings.AsNoTracking().CountAsync(b => b.CompanyId == companyId && b.PassId == passId
                && b.Status == CreditBookingStatus.Booked && b.StartAt >= rangeFrom && b.StartAt < rangeTo, ct);
            if (CourseCreditMath.FreezeProblem(pass.Status, fromDay, toDay, today, pass.RecognizedThroughDate, pass.ExpiresOn, pass.FrozenDays,
                    pass.MaxFreezeDays, spans, openInRange, request.Reason) is string problem)
                throw new BusinessRuleException(problem, CourseCreditMath.MembershipRuleCode);
            var days = (toDay - fromDay).Days + 1;
            var before = pass.ExpiresOn;
            var row = new CreditPassFreeze
            {
                CompanyId = companyId, CreatedBy = actor, PassId = pass.Id, FromDate = fromDay, ToDate = toDay, Days = days, Reason = request.Reason!.Trim(),
            };
            _db.CreditPassFreezes.Add(row);
            pass.FrozenDays += days;
            pass.ExpiresOn = pass.ExpiresOn!.Value.AddDays(days);
            // วันหมดอายุใหม่ ⇒ แจ้งเตือนรอบใหม่ (ประทับเดิมเป็นของวันหมดอายุเก่า)
            pass.ExpiryNotifiedAt = null; pass.ExpiryNotifyError = null; pass.ExpiryNotifyAttemptedAt = null;
            pass.UpdatedAt = DateTime.UtcNow; pass.UpdatedBy = actor;
            var note = $"พักบัตร {ThaiDate.ToThaiDisplayString(fromDay)} – {ThaiDate.ToThaiDisplayString(toDay)} ({days} วัน) · หมดอายุ {ThaiDate.ToThaiDisplayString(before!.Value)} → {ThaiDate.ToThaiDisplayString(pass.ExpiresOn.Value)} · {row.Reason}";
            _db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                CompanyId = companyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.Freeze, Credits = 0m, Note = note,
            });
            _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Update, "CreditPass", pass.Id, new
            {
                action = "FreezePass", pass = pass.PassNumber, freezeId = row.Id, fromDate = fromDay, toDate = toDay, days, reason = row.Reason,
                expiresBefore = before, expiresAfter = pass.ExpiresOn, frozenDaysTotal = pass.FrozenDays,
                ruleCode = CourseCreditMath.MembershipRuleCode, legalReference = CourseCreditMath.MembershipLegalReference,
            }));
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return (await GetPassAsync(companyId, passId, ct)).Pass;
    }

    /// <summary>ยกเลิกการพักที่ยังไม่เริ่ม — วันหมดอายุเลื่อนกลับ · การจองที่อยู่หลังวันหมดอายุใหม่ ⇒ บล็อก (ทางไปต่อ: ยกเลิกการจองนั้นก่อน)</summary>
    public async Task<CreditPassResponse> CancelFreezeAsync(Guid companyId, Guid passId, Guid freezeId, string actor, CancellationToken ct = default)
    {
        await InTxAsync(async () =>
        {
            var pass = await LockPassAsync(companyId, passId, ct);
            var row = await _db.CreditPassFreezes.FirstOrDefaultAsync(f => f.Id == freezeId && f.PassId == passId && f.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบการพักบัตร");
            var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
            if (row.CancelledAt != null) throw new BusinessRuleException("การพักนี้ถูกยกเลิกไปแล้ว", CourseCreditMath.MembershipRuleCode);
            if (row.FromDate.Date <= today.Date)
                throw new BusinessRuleException("การพักเริ่มแล้ว — ยกเลิกย้อนหลังไม่ได้ (วันที่พักไปแล้วไม่ถูกนับเป็นวันใช้งาน)", CourseCreditMath.MembershipRuleCode);
            if (pass.Status != CreditPassStatus.Active)
                throw new BusinessRuleException($"บัตรอยู่ในสถานะ {CourseCreditMath.StatusTh(pass.Status)} — แก้การพักไม่ได้", CourseCreditMath.MembershipRuleCode);
            var newExpiry = pass.ExpiresOn!.Value.AddDays(-row.Days);
            var afterFrom = DateTime.SpecifyKind(newExpiry.Date.AddDays(1).AddHours(-7), DateTimeKind.Utc);
            var lateBookings = await _db.CreditClassBookings.AsNoTracking().CountAsync(b => b.CompanyId == companyId && b.PassId == passId
                && b.Status == CreditBookingStatus.Booked && b.StartAt >= afterFrom, ct);
            if (lateBookings > 0)
                throw new BusinessRuleException($"มีการจอง {lateBookings} รายการหลังวันหมดอายุใหม่ ({ThaiDate.ToThaiDisplayString(newExpiry)}) — ยกเลิกการจองเหล่านั้นก่อน",
                    CourseCreditMath.MembershipRuleCode);
            var before = pass.ExpiresOn;
            row.CancelledAt = DateTime.UtcNow; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor;
            pass.FrozenDays = Math.Max(0, pass.FrozenDays - row.Days);
            pass.ExpiresOn = newExpiry;
            pass.ExpiryNotifiedAt = null; pass.ExpiryNotifyError = null; pass.ExpiryNotifyAttemptedAt = null;
            pass.UpdatedAt = DateTime.UtcNow; pass.UpdatedBy = actor;
            _db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                CompanyId = companyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.Freeze, Credits = 0m,
                Note = $"ยกเลิกการพัก {ThaiDate.ToThaiDisplayString(row.FromDate)} – {ThaiDate.ToThaiDisplayString(row.ToDate)} ({row.Days} วัน) · หมดอายุ {ThaiDate.ToThaiDisplayString(before!.Value)} → {ThaiDate.ToThaiDisplayString(newExpiry)}",
            });
            _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Update, "CreditPass", pass.Id, new
            {
                action = "CancelFreeze", pass = pass.PassNumber, freezeId = row.Id, days = row.Days, expiresBefore = before, expiresAfter = newExpiry,
                ruleCode = CourseCreditMath.MembershipRuleCode,
            }));
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return (await GetPassAsync(companyId, passId, ct)).Pass;
    }

    /// <summary>ขยายอายุบัตร (ไม่มีเงิน · audit) — สมาชิกไม่จำกัด: รับรู้ถึงเมื่อวานก่อน แล้ว<b>ตั้งฐานใหม่</b> (ส่วนที่ยังไม่รับรู้กระจายบนวันที่เหลือ + วันที่เพิ่ม)</summary>
    public async Task<CreditPassResponse> ExtendPassAsync(Guid companyId, Guid passId, ExtendCreditPassRequest request, string actor, CancellationToken ct = default)
    {
        await InTxAsync(async () =>
        {
            var pass = await LockPassAsync(companyId, passId, ct);
            if (CourseCreditMath.ExtendProblem(pass.Status, pass.ExpiresOn, request.Days, request.Reason) is string problem)
                throw new BusinessRuleException(problem, CourseCreditMath.MembershipRuleCode);
            var before = pass.ExpiresOn;
            object? rebase = null;
            if (CourseCreditMath.IsUnlimited(pass.Kind))
            {
                var yesterday = ThaiDate.CalendarDateUtc(DateTime.UtcNow).AddDays(-1);
                await RecognizePeriodCoreAsync(pass, yesterday, actor, ct);
                var spans = await LoadFreezeSpansAsync(pass, ct);
                var start = pass.PeriodStartsOn ?? ThaiDate.CalendarDateUtc(pass.PurchasedAt);
                var k = pass.RecognizedThroughDate is DateTime rt ? CourseCreditMath.ActiveDaysThrough(start, rt, spans) : 0;
                pass.PeriodBaseRecognized = pass.RecognizedAmount;
                pass.PeriodBaseActiveDays = Math.Min(k, pass.PeriodDays);
                pass.PeriodDays += request.Days;
                rebase = new { baseRecognized = pass.PeriodBaseRecognized, baseActiveDays = pass.PeriodBaseActiveDays, periodDays = pass.PeriodDays };
            }
            pass.ExpiresOn = pass.ExpiresOn!.Value.AddDays(request.Days);
            pass.ExpiryNotifiedAt = null; pass.ExpiryNotifyError = null; pass.ExpiryNotifyAttemptedAt = null;
            pass.UpdatedAt = DateTime.UtcNow; pass.UpdatedBy = actor;
            var reason = request.Reason!.Trim();
            _db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                CompanyId = companyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.Extend, Credits = 0m,
                Note = $"ขยายอายุ {request.Days} วัน · หมดอายุ {ThaiDate.ToThaiDisplayString(before!.Value)} → {ThaiDate.ToThaiDisplayString(pass.ExpiresOn.Value)} · {reason} (ไม่มีเงิน)",
            });
            _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Update, "CreditPass", pass.Id, new
            {
                action = "ExtendPass", pass = pass.PassNumber, days = request.Days, reason, expiresBefore = before, expiresAfter = pass.ExpiresOn, rebase,
                ruleCode = CourseCreditMath.MembershipRuleCode, legalReference = CourseCreditMath.MembershipLegalReference,
            }));
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return (await GetPassAsync(companyId, passId, ct)).Pass;
    }

    // ===================================================================== โอนบัตร

    /// <summary>โอนบัตรให้ลูกค้าคนอื่น (คำตัดสินข้อ 168 ฎ) — ปิดบัตรเดิม (สถานะ "โอนแล้ว") + เปิดบัตรใหม่ยอดที่เหลือเท่าเดิม ·
    /// <b>ไม่มี JE</b> (ค่าบริการรับล่วงหน้าคงอยู่ผังเดิม — ย้ายเจ้าของ ไม่ย้ายผัง) · <b>ไม่ออกใบกำกับภาษีใหม่</b> (โอนภายใน · ภาษีขายเกิดครบตอนขาย §78/1) ·
    /// ใบขายเดิมยังชี้บัตรต้นทาง — ยกเลิก/ลดหนี้ใบนั้นถูกบล็อกที่ด่าน <see cref="CreditPassDocumentRule"/> (สถานะ Transferred)</summary>
    public async Task<CreditPassResponse> TransferPassAsync(Guid companyId, Guid passId, TransferCreditPassRequest request, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new BusinessRuleException("ระบุเหตุผลการโอนบัตร (เช่น ของขวัญ · ลูกค้าขอโอนสิทธิ์)", CourseCreditMath.MembershipRuleCode);
        if (!await _db.Contacts.AsNoTracking().AnyAsync(c => c.Id == request.ToContactId && c.CompanyId == companyId, ct))
            throw new BusinessRuleException("ไม่พบผู้รับโอนในบริษัทนี้ — เลือกลูกค้าใหม่", CourseCreditMath.MembershipRuleCode);
        var newId = await InTxAsync(async () =>
        {
            var pass = await LockPassAsync(companyId, passId, ct);
            await EnsureSaleDocumentLiveAsync(pass, ct);
            var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
            if (CourseCreditMath.IsUnlimited(pass.Kind)) await RecognizePeriodCoreAsync(pass, today.AddDays(-1), actor, ct);
            var spans = await LoadFreezeSpansAsync(pass, ct);
            var openBookings = await _db.CreditClassBookings.AsNoTracking()
                .CountAsync(b => b.CompanyId == companyId && b.PassId == passId && b.Status == CreditBookingStatus.Booked, ct);
            var start = pass.PeriodStartsOn ?? ThaiDate.CalendarDateUtc(pass.PurchasedAt);
            var k = pass.RecognizedThroughDate is DateTime rt ? CourseCreditMath.ActiveDaysThrough(start, rt, spans) : 0;
            var plan = CourseCreditMath.PlanTransfer(pass.Kind, pass.Status, pass.ContactId == request.ToContactId,
                CourseCreditMath.EffectiveContract(pass.ContractAmount, pass.RefundedAmount), pass.RecognizedAmount,
                pass.CreditsTotal, pass.CreditsUsed, openBookings, spans.Any(f => f.To.Date >= today.Date), pass.ExpiresOn,
                pass.PeriodDays, k, today);
            if (plan.Problem != null) throw new BusinessRuleException(plan.Problem, CourseCreditMath.MembershipRuleCode);

            var prefix = $"CP-{today:yyyyMM}-";
            var number = await SequenceNumber.NextAsync(_db, companyId, AdvisoryLockKey.CreditPassSequence, prefix,
                _db.CreditPasses.IgnoreQueryFilters().Where(p => p.CompanyId == companyId && p.PassNumber.StartsWith(prefix)).Select(p => p.PassNumber),
                _db.CreditPasses.Local.Select(p => p.PassNumber), ct: ct);
            var reason = request.Reason!.Trim();
            var unlimited = CourseCreditMath.IsUnlimited(pass.Kind);
            var np = new CreditPass
            {
                CompanyId = companyId, CreatedBy = actor, PassNumber = number, PackageId = pass.PackageId, ContactId = request.ToContactId,
                PackageName = pass.PackageName, Unit = pass.Unit, ClassFormat = pass.ClassFormat, AllowedServiceIds = pass.AllowedServiceIds,
                FreeCancelHours = pass.FreeCancelHours, PurchasedAt = today, ExpiresOn = plan.ExpiresOn,
                CreditsTotal = plan.CreditsTotal, ContractAmount = plan.Contract,
                UnearnedAccountId = pass.UnearnedAccountId, RevenueAccountId = pass.RevenueAccountId,
                SaleChannel = pass.SaleChannel, SaleDocumentId = null, SaleDocumentNumber = pass.SaleDocumentNumber,
                Kind = pass.Kind, MaxBookingsPerDay = pass.MaxBookingsPerDay, MaxActiveBookings = pass.MaxActiveBookings,
                // สิทธิ์พักที่เหลือติดไปกับบัตร (ไม่ได้สิทธิ์พักใหม่จากการโอน)
                MaxFreezeDays = Math.Max(0, pass.MaxFreezeDays - pass.FrozenDays),
                PeriodDays = plan.PeriodDays, PeriodStartsOn = unlimited ? today : null,
                TransferredFromPassId = pass.Id,
                Notes = $"รับโอนจากบัตร {pass.PassNumber} — {reason}",
            };
            _db.CreditPasses.Add(np);
            pass.Status = CreditPassStatus.Transferred;
            pass.TransferredToPassId = np.Id;
            pass.TransferredOutAmount = plan.Contract;
            pass.ClosedAt = DateTime.UtcNow; pass.UpdatedAt = DateTime.UtcNow; pass.UpdatedBy = actor;
            var what = unlimited ? $"{plan.PeriodDays} วันใช้งาน" : $"{plan.CreditsTotal:0.##} {UnitTh(pass.Unit)}";
            _db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                CompanyId = companyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.TransferOut, Credits = plan.CreditsTotal,
                Note = $"โอนให้บัตร {number} — {what} · ค่าบริการรับล่วงหน้า {plan.Contract:N2} (ไม่มี JE · ไม่ออกใบกำกับใหม่) · {reason}",
            });
            _db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                CompanyId = companyId, CreatedBy = actor, PassId = np.Id, Kind = CreditLedgerKind.TransferIn, Credits = plan.CreditsTotal,
                Note = $"รับโอนจากบัตร {pass.PassNumber} — {what} · ค่าบริการรับล่วงหน้า {plan.Contract:N2} · {reason}",
            });
            var payload = new
            {
                action = "TransferPass", fromPass = pass.PassNumber, toPass = number, fromContact = pass.ContactId, toContact = request.ToContactId,
                contract = plan.Contract, credits = plan.CreditsTotal, periodDays = plan.PeriodDays, expiresOn = plan.ExpiresOn, reason,
                ruleCode = CourseCreditMath.MembershipRuleCode,
                legalReference = "โอนสิทธิ์ภายใน — ไม่ออกใบกำกับภาษีใหม่ (ภาษีขายเกิดครบตอนขาย ป.รัษฎากร §78/1) · หนี้สินรับล่วงหน้าคงผังเดิม (TFRS for NPAEs บทที่ 6)",
            };
            _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Update, "CreditPass", pass.Id, payload));
            _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Create, "CreditPass", np.Id, payload));
            await _db.SaveChangesAsync(ct);
            return np.Id;
        }, ct);
        return (await GetPassAsync(companyId, newId, ct)).Pass;
    }

    // ===================================================================== แจ้งเตือนใกล้หมดอายุ (R-209-C4 ส่วนเครดิต)

    /// <summary>ส่งแจ้งเตือน "บัตรใกล้หมดอายุ" ตาม <c>CreditPackage.ExpiryReminderDays</c> ผ่าน <see cref="IBookingNotifier"/> (อีเมล → LINE) —
    /// ประทับ <c>ExpiryNotifiedAt</c> <b>เฉพาะเมื่อช่องทางตอบสำเร็จจริง</b> (F2 #3) · ล้ม ⇒ ประทับเหตุบนบัตร (พนักงานเห็นบนหน้าบัตร) + เว้นระยะแล้วลองใหม่ ·
    /// ไม่ล้มทั้งรอบเพราะใบเดียว</summary>
    public async Task<CreditReminderRunResult> SendExpiryRemindersAsync(Guid companyId, string actor, CancellationToken ct = default)
    {
        var pkgDays = await _db.CreditPackages.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.ExpiryReminderDays > 0)
            .ToDictionaryAsync(p => p.Id, p => p.ExpiryReminderDays, ct);
        var messages = new List<string>();
        if (pkgDays.Count == 0) return new CreditReminderRunResult(0, 0, 0, messages);
        var now = DateTime.UtcNow;
        var today = ThaiDate.CalendarDateUtc(now);
        var horizon = today.AddDays(pkgDays.Values.Max());
        var pkgIds = pkgDays.Keys.ToList();
        var candidates = await _db.CreditPasses.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.Status == CreditPassStatus.Active && p.ExpiresOn != null
                && p.ExpiresOn >= today && p.ExpiresOn <= horizon && p.ExpiryNotifiedAt == null && pkgIds.Contains(p.PackageId))
            .OrderBy(p => p.ExpiresOn).Take(500).ToListAsync(ct);
        var shop = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        int sent = 0, failed = 0, due = 0;
        foreach (var c in candidates)
        {
            if (!CourseCreditMath.ExpiryReminderDue(c.ExpiresOn!.Value, today, pkgDays[c.PackageId], c.ExpiryNotifiedAt, c.ExpiryNotifyAttemptedAt, now)) continue;
            due++;
            var contact = await _db.Contacts.AsNoTracking().Where(x => x.Id == c.ContactId && x.CompanyId == companyId)
                .Select(x => new { x.Name, x.Email, x.LineUserId }).FirstOrDefaultAsync(ct);
            var remaining = CourseCreditMath.IsUnlimited(c.Kind)
                ? "ไม่จำกัดครั้งถึงวันหมดอายุ"
                : $"{CourseCreditMath.Available(c.CreditsTotal, c.CreditsUsed, c.CreditsReserved):0.##} {UnitTh(c.Unit)}";
            var text = CourseCreditMath.ExpiryReminderText(contact?.Name, c.PassNumber, c.PackageName, c.ExpiresOn.Value, remaining, shop);
            BookingSendResult result;
            if (_notifier == null) result = BookingSendResult.Fail("ระบบแจ้งเตือนไม่พร้อม (ไม่มีช่องส่งอีเมล/LINE)");
            else
            {
                try { result = await _notifier.SendAsync(companyId, new BookingContact(contact?.Email, contact?.LineUserId) { LineTarget = CustomerLineTarget.ForContact(c.ContactId) }, $"บัตร {c.PassNumber} ใกล้หมดอายุ", text, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { result = BookingSendResult.Fail(ex.Message); }
            }
            var row = await _db.CreditPasses.FirstAsync(p => p.Id == c.Id && p.CompanyId == companyId, ct);
            row.ExpiryNotifyAttemptedAt = now;
            if (result.Sent) { row.ExpiryNotifiedAt = now; row.ExpiryNotifyError = null; sent++; }
            else { row.ExpiryNotifyError = result.Error; failed++; messages.Add($"{c.PassNumber}: {result.Error}"); }
            await _db.SaveChangesAsync(ct);
        }
        return new CreditReminderRunResult(sent, failed, due, messages);
    }
}
