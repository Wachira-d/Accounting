using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 207 — หน้าทำงานประจำวันของผู้ให้บริการ (คำขอเจ้าของข้อ 3 · 5 · 9):
/// เริ่มให้บริการพร้อมผู้ให้บริการหลายคน + แบ่ง % (<see cref="BookingProviderShare"/>) · ผู้ให้บริการที่แนะนำ (ตัวจัดอันดับเดียว
/// <see cref="BookingSlotEngine.RankResources"/>) · เสร็จสิ้นพร้อมรับชำระ (เลือกวิธี/บัญชีรับเงิน · ระบุยอด) ผ่านเส้นเอกสารเดิม
/// (<c>UpdateBookingStatusCoreAsync</c> → <c>BookingInvoiceTimingPolicy</c>) · รายละเอียดการจอง (เอกสารทุกใบ · ประวัติรับเงิน · ไทม์ไลน์)
/// </summary>
public partial class CmsBookingService
{
    private const string WorkflowRuleCode = "CMS-BOOKING-WORKFLOW";

    /// <summary>รับเงินตอนเสร็จ — <see cref="PaidInFull"/> ⇒ (ไม่จด VAT) ใบเสร็จรับเงินขายสด · ผังเงินเข้า = บัญชีธนาคาร/ผังที่เลือก (null = เงินสดตั้งต้น)</summary>
    private sealed record BookingCompletionMoney(bool PaidInFull, DateTime PaidAt, Guid? MoneyAccountId, Guid? BankAccountId);

    private sealed record CapableResource(Guid Id, string Name, Guid? StaffUserId);

    /// <summary>ผู้ให้บริการที่ใช้งานและรับบริการนี้ได้ (ติ๊กบริการไว้)</summary>
    private async Task<List<CapableResource>> CapableResourcesAsync(Guid companyId, Guid siteId, Guid serviceId)
        => await _db.SiteBookingResources.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.SiteId == siteId && r.IsActive && !r.IsDeleted
                && r.Services.Any(l => l.BookingServiceId == serviceId && !l.IsDeleted))
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name)
            .Select(r => new CapableResource(r.Id, r.Name, r.StaffUserId))
            .ToListAsync();

    /// <summary>จัดอันดับผู้ให้บริการสำหรับรอบของการจองนี้ (ไม่นับการจองนี้เองเป็นงานชน) — ตัวเดียวกับที่ระบบจัดให้ตอนจอง</summary>
    private async Task<(IReadOnlyList<BookingResourceRank> Ranks, BookingSlotRules Rules)> RankForBookingAsync(Guid companyId, Guid siteId, SiteBooking booking)
    {
        var svc = await _db.SiteBookingServices.AsNoTracking()
            .FirstAsync(s => s.Id == booking.BookingServiceId && s.CompanyId == companyId);
        var date = DateOnly.FromDateTime(booking.BookingDate);
        var ctx = await LoadDayAsync(companyId, siteId, svc, date, excludeBookingId: booking.Id, forProviders: true);
        var slot = BookingSlotEngine.SlotOfBooking(booking.StartTime, booking.EndTime, ctx.Rules.Capacity);
        return (BookingSlotEngine.RankResources(ctx.Rules, slot, date, ctx.Resources, ctx.Occupied, Math.Max(1, booking.GuestCount)), ctx.Rules);
    }

    private static string? UnavailableReason(BookingResourceRank r)
        => !r.OnDuty ? "ไม่อยู่ในเวลางาน / ลา / หยุด" : !r.Fits ? "มีงานอื่นชนเวลานี้" : null;

    // ═══════════════ ผู้ให้บริการตอนเริ่มให้บริการ ═══════════════

    public async Task<BookingProviderCandidatesResponse> GetProviderCandidatesAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        var booking = await _db.SiteBookings.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Booking not found.");
        var enabled = await ProvidersEnabledAsync(companyId, siteId);
        var capable = await CapableResourcesAsync(companyId, siteId, booking.BookingServiceId);
        var resp = new BookingProviderCandidatesResponse
        {
            ProviderAllocationEnabled = enabled,
            RequiresProvider = BookingProviderAllocation.RequiresProviderAtStart(enabled, capable.Count),
        };
        if (!enabled) return resp;
        if (capable.Count == 0) { resp.Note = BookingProviderAllocation.NoCapableProviderNote; return resp; }

        var (ranks, rules) = await RankForBookingAsync(companyId, siteId, booking);
        resp.StrategyLabel = BookingProviderAllocation.StrategyOptions().First(o => o.Value == rules.Strategy.ToString()).Label;
        var assigned = await _db.SiteBookingAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.BookingId == bookingId && !a.IsDeleted)
            .Select(a => a.ResourceId).ToListAsync();
        if (assigned.Count == 0 && booking.ResourceId is Guid rid) assigned.Add(rid);
        var firstFree = ranks.FirstOrDefault(x => x.OnDuty && x.Fits)?.Resource.Id;
        resp.Candidates = ranks.Select(x => new BookingProviderCandidateDto
        {
            ResourceId = x.Resource.Id, Name = x.Resource.Name,
            OnDuty = x.OnDuty, Available = x.OnDuty && x.Fits,
            UnavailableReason = UnavailableReason(x),
            Recommended = x.Resource.Id == firstFree,
            Reason = BookingProviderAllocation.ReasonLabel(rules.Strategy, x.Resource.IsPrimary, x.Resource.Priority,
                x.DayLoadMinutes, x.Resource.PeriodLoadMinutes, x.Resource.LastAssignedUtc),
            IsPrimaryForService = x.Resource.IsPrimary,
            CurrentlyAssigned = assigned.Contains(x.Resource.Id),
        }).ToList();
        return resp;
    }

    /// <summary>ด่านของเส้นเปลี่ยนสถานะเป็น "กำลังให้บริการ" ที่ไม่ได้ส่งผู้ให้บริการมา (ปุ่มสถานะเดิม/API) — มีการมอบหมายแล้ว = ผ่าน ·
    /// มีคนที่จัดไว้ตอนจอง ⇒ มอบหมายคนนั้น 100% · ไซต์เปิด + มีคนรับบริการนี้ แต่ยังไม่มีใคร ⇒ ปฏิเสธ (บอกทางไปต่อ)</summary>
    private async Task EnsureStartAssignmentsAsync(Guid companyId, Guid siteId, SiteBooking booking, string userId)
    {
        var enabled = await ProvidersEnabledAsync(companyId, siteId);
        var capable = await CapableResourcesAsync(companyId, siteId, booking.BookingServiceId);
        if (!BookingProviderAllocation.RequiresProviderAtStart(enabled, capable.Count)) return;
        // รวมแถวที่ลบแล้ว (unique BookingId+ResourceId) — มอบหมายคนเดิมซ้ำ = ฟื้นแถวเดิม ไม่ชน index
        var existing = await _db.SiteBookingAssignments.IgnoreQueryFilters()
            .Where(a => a.CompanyId == companyId && a.BookingId == booking.Id).ToListAsync();
        if (existing.Any(a => !a.IsDeleted)) return;
        if (booking.ResourceId is Guid rid && capable.Any(c => c.Id == rid))
        {
            WriteAssignments(companyId, siteId, booking, new[] { new BookingProviderSharePart(rid, 100m) }, capable, existing, userId);
            return;
        }
        throw new BusinessRuleException(BookingProviderAllocation.ProviderRequiredMessage, BookingProviderAllocation.RuleCode);
    }

    /// <summary>เขียนการมอบหมาย (แทนชุดเดิมทั้งชุด) + ผู้ให้บริการหลักบนการจอง + ช่างที่ POS เติมให้ (พนักงานที่ผูก)</summary>
    private void WriteAssignments(Guid companyId, Guid siteId, SiteBooking booking, IReadOnlyList<BookingProviderSharePart> parts,
        IReadOnlyList<CapableResource> capable, IReadOnlyList<SiteBookingAssignment> existing, string userId)
    {
        foreach (var old in existing) { old.IsDeleted = true; old.UpdatedBy = userId; }
        foreach (var p in parts)
        {
            var rev = existing.FirstOrDefault(e => e.ResourceId == p.ResourceId);
            var name = capable.FirstOrDefault(c => c.Id == p.ResourceId)?.Name ?? rev?.ResourceName ?? "";
            if (rev != null)
            {
                rev.IsDeleted = false; rev.SharePercent = p.SharePercent; rev.ResourceName = name; rev.UpdatedBy = userId;
            }
            else
                _db.SiteBookingAssignments.Add(new SiteBookingAssignment
                {
                    CompanyId = companyId, SiteId = siteId, BookingId = booking.Id, ResourceId = p.ResourceId,
                    ResourceName = name, SharePercent = p.SharePercent, CreatedBy = userId,
                });
        }
        var primary = BookingProviderShare.Primary(parts);
        booking.ResourceId = primary;
        // ช่างที่ POS เติมให้ = พนักงานที่ผูกกับคนหลัก · คนหลักไม่ผูกพนักงาน ⇒ ล้าง (ไม่ค้างช่างคนเก่า · ฝ่ายค้านรอบ 207 ข้อ 11)
        var primaryRes = capable.FirstOrDefault(c => c.Id == primary);
        booking.AssignedStaffId = primaryRes?.StaffUserId;
        booking.AssignedStaffName = primaryRes?.StaffUserId != null ? primaryRes.Name : null;
        var desc = BookingProviderShare.Describe(parts.Select(p => (capable.FirstOrDefault(c => c.Id == p.ResourceId)?.Name ?? "?", p.SharePercent)));
        booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes,
            $"[{BookingProviderShare.RuleCode}] ผู้ให้บริการ: {desc} ({BookingSlotEngine.BangkokNow(DateTime.UtcNow):dd/MM HH:mm})");
        booking.UpdatedBy = userId;
    }

    /// <summary>ตรวจรายชื่อ + สัดส่วน (ตัวตรวจเดียว BookingProviderShare) · ทุกคนต้องรับบริการนี้ได้ · <paramref name="checkAvailability"/> ⇒ คนที่เพิ่มต้องว่างช่วงนั้น</summary>
    private async Task<(IReadOnlyList<BookingProviderSharePart> Parts, List<CapableResource> Capable)> ValidateAssignmentsAsync(Guid companyId, Guid siteId,
        SiteBooking booking, IReadOnlyList<BookingAssignmentRequestItem> items, bool checkAvailability, IReadOnlyCollection<Guid> alreadyAssigned)
    {
        var (parts, err) = BookingProviderShare.Normalize(items.Select(i => (i.ResourceId, i.SharePercent)).ToList());
        if (err != null) throw new BusinessRuleException(err, BookingProviderShare.RuleCode);
        var capable = await CapableResourcesAsync(companyId, siteId, booking.BookingServiceId);
        var notCapable = parts.Where(p => capable.All(c => c.Id != p.ResourceId)).ToList();
        if (notCapable.Count > 0)
            throw new BusinessRuleException("มีผู้ให้บริการที่ไม่ได้รับบริการนี้/ถูกปิดใช้ — ติ๊กบริการที่ผู้ให้บริการรับได้ก่อน", BookingProviderShare.RuleCode);
        if (checkAvailability)
        {
            var (ranks, _) = await RankForBookingAsync(companyId, siteId, booking);
            foreach (var p in parts.Where(p => !alreadyAssigned.Contains(p.ResourceId)))
            {
                var r = ranks.FirstOrDefault(x => x.Resource.Id == p.ResourceId);
                if (r == null || !r.OnDuty || !r.Fits)
                    throw new BusinessRuleException($"{capable.First(c => c.Id == p.ResourceId).Name} ไม่ว่างในรอบนี้ ({(r == null ? "ไม่พบ" : UnavailableReason(r))}) — เลือกคนที่ว่าง",
                        BookingProviderShare.RuleCode, 409);
            }
        }
        return (parts, capable);
    }

    /// <summary>เริ่มให้บริการ + ผู้ให้บริการ (ได้หลายคน · แบ่ง %) — ตรวจว่าง/รับบริการได้ใต้ล็อกไซต์ แล้วเปลี่ยนสถานะผ่านเส้นเดิม</summary>
    public async Task<BookingResponse> StartServiceAsync(Guid companyId, Guid siteId, Guid bookingId, StartBookingServiceRequest request, string userId)
    {
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            await LockSiteBookingsAsync(companyId, siteId);
            var booking = await _db.SiteBookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
                ?? throw new KeyNotFoundException("Booking not found.");
            if (booking.Status != BookingStatus.Confirmed)
                throw new BusinessRuleException("เริ่มให้บริการได้เฉพาะการจองที่ยืนยันแล้ว — ยืนยันการจองก่อน", WorkflowRuleCode, 409);
            var enabled = await ProvidersEnabledAsync(companyId, siteId);
            var items = request.Assignments ?? new();
            if (items.Count > 0)
            {
                if (!enabled) throw new BusinessRuleException("ไซต์นี้ปิดจัดสรรผู้ให้บริการ — เปิดที่ เว็บไซต์ → การจอง → ผู้ให้บริการ ก่อน", BookingProviderAllocation.RuleCode);
                var existing = await _db.SiteBookingAssignments.IgnoreQueryFilters()
                    .Where(a => a.CompanyId == companyId && a.BookingId == bookingId).ToListAsync();
                var (parts, capable) = await ValidateAssignmentsAsync(companyId, siteId, booking, items, checkAvailability: true,
                    alreadyAssigned: Array.Empty<Guid>());
                WriteAssignments(companyId, siteId, booking, parts, capable, existing, userId);
            }
            if (!string.IsNullOrWhiteSpace(request.Note))
                booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes, request.Note.Trim());
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return await UpdateBookingStatusCoreAsync(companyId, siteId, bookingId,
            new UpdateBookingStatusRequest { Status = BookingStatus.InProgress }, userId, null);
    }

    /// <summary>แก้ผู้ให้บริการ/สัดส่วนหลังเริ่ม (กำลังให้บริการ/เสร็จแล้ว — รายงานถูกต้อง) · คนที่เพิ่มระหว่างให้บริการต้องว่าง</summary>
    public async Task<BookingResponse> SetAssignmentsAsync(Guid companyId, Guid siteId, Guid bookingId, StartBookingServiceRequest request, string userId)
    {
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            await LockSiteBookingsAsync(companyId, siteId);
            var booking = await _db.SiteBookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
                ?? throw new KeyNotFoundException("Booking not found.");
            if (booking.Status is not (BookingStatus.InProgress or BookingStatus.Completed))
                throw new BusinessRuleException("แก้ผู้ให้บริการ/สัดส่วนได้หลังเริ่มให้บริการ — ก่อนเริ่มใช้ปุ่ม \"เริ่มให้บริการ\"", WorkflowRuleCode, 409);
            if (!await ProvidersEnabledAsync(companyId, siteId))
                throw new BusinessRuleException("ไซต์นี้ปิดจัดสรรผู้ให้บริการ", BookingProviderAllocation.RuleCode);
            var existing = await _db.SiteBookingAssignments.IgnoreQueryFilters()
                .Where(a => a.CompanyId == companyId && a.BookingId == bookingId).ToListAsync();
            var current = existing.Where(e => !e.IsDeleted).Select(e => e.ResourceId).ToList();
            if (current.Count == 0 && booking.ResourceId is Guid rid) current.Add(rid);
            var (parts, capable) = await ValidateAssignmentsAsync(companyId, siteId, booking, request.Assignments ?? new(),
                checkAvailability: booking.Status == BookingStatus.InProgress, alreadyAssigned: current);
            WriteAssignments(companyId, siteId, booking, parts, capable, existing, userId);
            if (!string.IsNullOrWhiteSpace(request.Note))
                booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes, request.Note.Trim());
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return await GetBookingAsync(companyId, siteId, bookingId) ?? throw new InvalidOperationException("Failed.");
    }

    // ═══════════════ สรุปการทำงาน / รายงานผู้ให้บริการ (คำขอเจ้าของข้อ 4) ═══════════════

    public const int ProviderReportMaxDays = 366;

    public async Task<BookingProviderReportResponse> GetProviderReportAsync(Guid companyId, Guid siteId, DateTime from, DateTime to)
    {
        await EnsureSiteAsync(companyId, siteId);
        var f = from.Date; var t = to.Date;
        if (t < f) throw new BusinessRuleException("วันที่สิ้นสุดต้องไม่ก่อนวันที่เริ่ม", WorkflowRuleCode);
        if ((t - f).TotalDays >= ProviderReportMaxDays) throw new BusinessRuleException($"ช่วงรายงานได้ไม่เกิน {ProviderReportMaxDays} วัน", WorkflowRuleCode);
        var end = t.AddDays(1);
        var bookings = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.BookingDate >= f && b.BookingDate < end
                && b.Status != BookingStatus.Waitlisted)
            .OrderBy(b => b.BookingDate).ThenBy(b => b.StartTime)
            .Select(b => new
            {
                b.Id, b.BookingNumber, b.BookingDate, b.StartTime, b.EndTime, b.DurationMinutes, b.Status, b.BookingServiceId,
                ServiceName = b.BookingService.Name,
                CustomerName = b.Customer != null ? b.Customer.FullName : b.GuestName,
                b.TotalAmount, b.ResourceId,
            })
            .ToListAsync();
        var ids = bookings.Select(b => b.Id).ToList();
        var assigns = await _db.SiteBookingAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.SiteId == siteId && !a.IsDeleted && ids.Contains(a.BookingId))
            .Select(a => new { a.BookingId, a.ResourceId, a.ResourceName, a.SharePercent }).ToListAsync();
        var resIds = bookings.Where(b => b.ResourceId != null).Select(b => b.ResourceId!.Value).Distinct().ToList();
        var names = await _db.SiteBookingResources.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.CompanyId == companyId && resIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name);
        var rows = bookings.Select(b =>
        {
            var mine = assigns.Where(a => a.BookingId == b.Id).ToList();
            IReadOnlyList<(Guid, string, decimal)> providers = mine.Count > 0
                ? mine.Select(a => (a.ResourceId, a.ResourceName, a.SharePercent)).ToList()
                : b.ResourceId is Guid rid ? new List<(Guid, string, decimal)> { (rid, names.GetValueOrDefault(rid) ?? "(ลบแล้ว)", 100m) }
                : new List<(Guid, string, decimal)>();
            return new BookingProviderReportRow(b.Id, b.BookingNumber, b.BookingDate, b.StartTime.ToString("HH:mm"), b.EndTime.ToString("HH:mm"),
                b.DurationMinutes, b.Status, b.BookingServiceId, b.ServiceName, b.CustomerName, b.TotalAmount, providers);
        }).ToList();
        var (list, totals) = BookingProviderReport.Build(rows);
        return new BookingProviderReportResponse
        {
            From = f, To = t, ProviderAllocationEnabled = await ProvidersEnabledAsync(companyId, siteId),
            Totals = totals, Providers = list,
        };
    }

    // ═══════════════ เสร็จสิ้น + รับชำระ ═══════════════

    /// <summary>แผนตอนกด "เสร็จสิ้น" — ถามตัวตัดสินเดียว (BookingInvoiceTimingPolicy) ว่าจะออกเอกสารอะไร · ยอดค้าง = ราคา − รับแล้ว</summary>
    private async Task<BookingCompletionPlanDto?> CompletionPlanAsync(Guid companyId, Guid siteId, BookingResponse b)
    {
        if (b.Status is BookingStatus.Completed or BookingStatus.Cancelled or BookingStatus.NoShow or BookingStatus.Waitlisted) return null;
        var due = Math.Max(0m, Math.Round(b.TotalAmount - b.PaidAmount, 2, MidpointRounding.AwayFromZero));
        var plan = new BookingCompletionPlanDto
        {
            AmountDue = due,
            PaymentMethods = new[] { PaymentMethod.Cash, PaymentMethod.BankTransfer, PaymentMethod.PromptPay, PaymentMethod.CreditCard,
                    PaymentMethod.EWallet, PaymentMethod.Cheque, PaymentMethod.Other }
                .Select(m => new BookingFormFields.FieldOption(m.ToString(), PaymentMethodLabel(m))).ToList(),
        };
        if (b.CreditPassNumber != null)
        {
            plan.Explanation = "ชำระด้วยบัตรคอร์สแล้ว — ไม่มีเงินต้องเก็บ · ไม่ออกเอกสารใหม่";
            plan.AmountDue = 0m; plan.CollectBlockedNote = plan.Explanation;
            return plan;
        }
        // รับชำระตอนเสร็จ = ออกเอกสารรับเงินเสมอ (collectingPayment) · กดเสร็จไม่รับชำระ = ตามค่าตั้งของเว็บ (ป้ายแยก)
        var facts = await LoadInvoiceFactsAsync(companyId, siteId, b.Id, BookingInvoiceEvent.ServiceCompleted,
            paidInFullNow: due > 0m && b.PaidAmount <= 0.005m, collectingPayment: true);
        if (facts == null) return plan;
        var factsNoPay = await LoadInvoiceFactsAsync(companyId, siteId, b.Id, BookingInvoiceEvent.ServiceCompleted);
        if (factsNoPay != null)
        {
            var np = BookingInvoiceTimingPolicy.Decide(factsNoPay.Facts);
            plan.DocumentLabelNoPayment = BookingDocumentFlow.Label(np)
                ?? (np.Action == BookingInvoiceAction.AlreadyIssued ? "ใช้เอกสารที่ออกไว้แล้ว" : "ไม่ออกเอกสาร (ตามค่าตั้งของเว็บ)");
        }
        if (facts.Facts.PosBill != BookingPosBillState.None)
        {
            plan.Explanation = "การจองนี้มีบิล POS — เก็บเงินและออกเอกสารที่ POS";
            plan.CollectBlockedNote = plan.Explanation;
            return plan;
        }
        if (b.BookingType == BookingType.PrePayment)
        {
            plan.DocumentLabel = due > 0m
                ? DocumentTypeNames.Title(BookingInvoiceTimingPolicy.TaxDocumentFor(facts.Facts.VatRegistered), null) + "ส่วนที่เหลือ (หักมัดจำ)"
                : "รับรู้รายได้จากมัดจำ (ไม่ออกเอกสารใหม่)";
            plan.Explanation = due > 0m ? "ราคาเต็มเกินมัดจำ — ระบบออกใบส่วนที่เหลือแล้วลงรับชำระที่ใบนั้น" : "รับครบแล้วจากมัดจำ";
            plan.CanCollectPayment = due > 0m;
            return plan;
        }
        var decision = BookingInvoiceTimingPolicy.Decide(facts.Facts);
        plan.DocumentLabel = BookingDocumentFlow.Label(decision)
            ?? (decision.Action == BookingInvoiceAction.AlreadyIssued ? "ใช้เอกสารที่ออกไว้แล้ว" : null);
        var hasIssued = decision.Action is BookingInvoiceAction.AlreadyIssued or BookingInvoiceAction.ApprovePendingTaxInvoice;
        plan.CanCollectPayment = due > 0m && (plan.DocumentLabel != null || hasIssued);
        plan.Explanation = decision.Notice;
        if (due > 0m && !plan.CanCollectPayment)
            plan.CollectBlockedNote = decision.Notice ?? "ไม่มีเอกสารให้ลงรับชำระ — ออกเอกสารที่หน้าเอกสารก่อน";
        return plan;
    }

    /// <summary>เสร็จสิ้น (+ รับชำระ) — เอกสารเดินเส้นเดิม (ใบกำกับ/ใบแจ้งหนี้/ใบเสร็จขายสด ตามสถานะ VAT) แล้วลงรับชำระที่ใบที่ค้าง ·
    /// ลงเงินไม่สำเร็จ ⇒ ล้มดัง (ข้อความถึงผู้กด + หมายเหตุบนการจอง) และไม่เพิ่มยอดรับบนการจอง</summary>
    public Task<BookingResponse> CompleteBookingAsync(Guid companyId, Guid siteId, Guid bookingId, CompleteBookingRequest request, string userId)
        // ฝ่ายค้านรอบ 207 ข้อ 4: กดซ้ำ/สองแท็บ ⇒ คำขอที่สองรอล็อกแล้วเห็นสถานะเสร็จแล้ว (ปฏิเสธ) — ไม่ลงเงินซ้ำ
        => WithBookingSessionLockAsync(companyId, BookingStatusLockPart(bookingId),   // คีย์เดียวกับเส้นสถานะ (ล็อกซ้อนได้)
            () => CompleteBookingLockedAsync(companyId, siteId, bookingId, request, userId));

    private async Task<BookingResponse> CompleteBookingLockedAsync(Guid companyId, Guid siteId, Guid bookingId, CompleteBookingRequest request, string userId)
    {
        var now = DateTime.UtcNow;
        var current = await GetBookingAsync(companyId, siteId, bookingId) ?? throw new KeyNotFoundException("Booking not found.");
        if (current.Status is not (BookingStatus.InProgress or BookingStatus.Confirmed))
            throw new BusinessRuleException("กดเสร็จสิ้นได้เฉพาะการจองที่ยืนยันแล้ว/กำลังให้บริการ", WorkflowRuleCode, 409);
        BookingCompletionMoney? money = null;
        decimal amount = 0m;
        if (request.CollectPayment)
        {
            var plan = current.CompletionPlan ?? await CompletionPlanAsync(companyId, siteId, current);
            if (plan == null || !plan.CanCollectPayment)
                throw new BusinessRuleException(plan?.CollectBlockedNote ?? "การจองนี้บันทึกรับเงินตอนเสร็จไม่ได้", WorkflowRuleCode);
            amount = Math.Round(request.Amount ?? plan.AmountDue, 2, MidpointRounding.AwayFromZero);
            if (amount <= 0m) throw new BusinessRuleException("ยอดรับชำระต้องมากกว่า 0", WorkflowRuleCode);
            if (amount > plan.AmountDue + 0.005m)
                throw new BusinessRuleException($"ยอดรับชำระ {amount:N2} เกินยอดค้าง {plan.AmountDue:N2}", WorkflowRuleCode);
            if (CmsBookingOnlinePayment.ValidatePaidAt(request.PaidAt, now) is string dateErr) throw new BusinessRuleException(dateErr, WorkflowRuleCode);
            var paidAt = request.PaidAt ?? now;
            // ฝ่ายค้านรอบ 207 ข้อ 7: วันรับเงิน = วันที่ใบกำกับ/ใบเสร็จ ⇒ ห้ามก่อนวันที่มีการจอง (กันลงย้อนเข้างวดภาษีที่ยื่นแล้ว)
            if (ThaiDate.CalendarDateUtc(paidAt) < ThaiDate.CalendarDateUtc(current.CreatedAt))
                throw new BusinessRuleException($"วันที่รับเงินต้องไม่ก่อนวันที่จอง ({ThaiDate.CalendarDateUtc(current.CreatedAt):dd/MM/yyyy})", WorkflowRuleCode);
            if (LodgingPaymentBank.Problem(request.PaymentMethod, amount, request.BankAccountId) is string bankErr)
                throw new BusinessRuleException(bankErr, LodgingPaymentBank.RuleCode);
            if (request.BankAccountId is Guid bankId) await _db.ResolveBankGlAsync(companyId, bankId);   // บัญชีธนาคารของบริษัทนี้เท่านั้น
            // ฝ่ายค้านรอบ 207 ข้อ 5: ใบเสร็จขายสด (ไม่จด VAT) เฉพาะเมื่อรับครบครั้งนี้ และไม่มีเงินที่บันทึกบนการจองไว้ก่อน (ใบเสร็จต้องเท่ายอดที่รับจริง)
            var paidInFull = amount + 0.005m >= plan.AmountDue && current.PaidAmount <= 0.005m;
            money = new BookingCompletionMoney(paidInFull, paidAt, null, request.BankAccountId);
        }

        var result = await UpdateBookingStatusCoreAsync(companyId, siteId, bookingId,
            new UpdateBookingStatusRequest { Status = BookingStatus.Completed, InternalNotes = request.InternalNotes }, userId, money);
        if (money == null) return result;

        var notices = result.ErpNotices.ToList();
        var note = await ApplyCompletionPaymentAsync(companyId, siteId, bookingId, amount, money, request, userId);
        var after = await GetBookingAsync(companyId, siteId, bookingId) ?? result;
        if (note != null) notices.Add(note);
        after.ErpNotices = notices;
        return after;
    }

    /// <summary>ล็อกระดับ session ต่อการจอง (แบบ ApplyInvoiceTimingAsync) — งานที่เปิดธุรกรรมของตัวเองหลายช่วงใช้ล็อก xact ไม่ได้</summary>
    private Task<T> WithBookingSessionLockAsync<T>(Guid companyId, string part, Func<Task<T>> body)
        => AdvisorySessionLock.RunAsync(_db, AdvisoryLockKey.For(companyId, AdvisoryLockKey.CmsBookingInvoice, part), body,
            "มีการบันทึกของการจองนี้กำลังทำงานอยู่ — รอสักครู่แล้วเปิดดูใหม่", WorkflowRuleCode);

    /// <summary>เอกสารของ "การจองนี้" — เลขการจองนับต่อเว็บ (สองเว็บในบริษัทเดียวกันมี BK-… ซ้ำได้ · ฝ่ายค้านรอบ 207 ข้อ 3) ⇒
    /// เลขตรง + (ใบที่ผูกกับการจอง หรือผู้ติดต่อเดียวกับใบที่ผูก) · ยังไม่มีใบผูก = ไม่มีเอกสาร</summary>
    private IQueryable<Document> BookingDocumentsQuery(Guid companyId, string bookingNumber, Guid? erpDocumentId)
    {
        if (erpDocumentId is not Guid linked) return _db.Documents.AsNoTracking().Where(d => false);
        var contact = _db.Documents.Where(x => x.Id == linked && x.CompanyId == companyId).Select(x => x.ContactId);
        return _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.BookingNumber == bookingNumber
                && (d.Id == linked || contact.Contains(d.ContactId)));
    }

    /// <summary>ลงรับชำระของการเสร็จสิ้น: ใบเสร็จขายสดที่ผูกกับการจองหลังขั้นเอกสาร = รับครบในตัว · ใบกำกับ/ใบแจ้งหนี้ที่ค้าง ⇒ <c>CreatePaymentAsync</c> ·
    /// ไม่มีใบให้ลง ⇒ ล้มดัง (ไม่แต่งยอดรับบนการจอง)</summary>
    private async Task<string?> ApplyCompletionPaymentAsync(Guid companyId, Guid siteId, Guid bookingId, decimal amount, BookingCompletionMoney money,
        CompleteBookingRequest request, string userId)
    {
        var b0 = await _db.SiteBookings.AsNoTracking().Where(b => b.Id == bookingId && b.CompanyId == companyId)
            .Select(b => new { b.BookingNumber, b.ErpDocumentId }).FirstAsync();
        var number = b0.BookingNumber;
        var revenueTypes = new[] { DocumentType.TaxInvoice, DocumentType.Invoice, DocumentType.Receipt };
        var docs = await BookingDocumentsQuery(companyId, number, b0.ErpDocumentId)
            .Where(d => !d.IsDeposit && revenueTypes.Contains(d.DocumentType)
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.TotalAmount, d.BalanceDue })
            .ToListAsync();
        decimal collected = 0m;
        string? docNo = null;
        string? failure = null;
        // ใบเสร็จขายสด = ใบที่ขั้นเอกสารของการกดครั้งนี้ผูกกับการจอง (relink) — ไม่เดาจากช่วงเวลา
        var cashReceipt = docs.FirstOrDefault(d => d.Id == b0.ErpDocumentId && d.DocumentType == DocumentType.Receipt);
        var open = docs.FirstOrDefault(d => d.DocumentType != DocumentType.Receipt && d.BalanceDue > 0.005m);
        if (money.PaidInFull && cashReceipt != null && open == null)
        {
            collected = Math.Min(amount, cashReceipt.TotalAmount);
            docNo = cashReceipt.DocumentNumber;
        }
        else if (open != null && _docService != null)
        {
            var pay = Math.Min(amount, open.BalanceDue);
            try
            {
                await _docService.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                    DocumentId: open.Id, PaymentDate: money.PaidAt, Amount: pay, PaymentMethod: request.PaymentMethod,
                    Reference: request.Reference, BankAccount: null, Notes: $"รับชำระตอนให้บริการเสร็จ {number}",
                    OverrideBankAccountId: request.BankAccountId), userId);
                collected = pay; docNo = open.DocumentNumber;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                _logger.LogWarning(ex, "ลงรับชำระของการจอง {Booking} ที่ {Doc} ไม่สำเร็จ", number, open.DocumentNumber);
                failure = CmsBookingCancelPolicy.FailureNote("การลงรับชำระตอนให้บริการเสร็จ", open.DocumentNumber, ReasonOf(ex));
            }
        }
        else
            failure = CmsBookingCancelPolicy.FailureNote("การลงรับชำระตอนให้บริการเสร็จ", null,
                "ไม่พบเอกสารที่ค้างชำระของการจองนี้ (ดูข้อความขั้นเอกสารด้านบน) — บันทึกรับเงินที่หน้าเอกสารเอง");

        var booking = await _db.SiteBookings.FirstAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId);
        if (collected > 0m)
        {
            booking.PaidAmount = Math.Round(booking.PaidAmount + collected, 2, MidpointRounding.AwayFromZero);
            booking.PaidAt = money.PaidAt;
            booking.PaymentReference = CmsBookingOnlinePayment.AppendReference(booking.PaymentReference, request.Reference?.Trim());
            _db.SiteBookingPayments.Add(new SiteBookingPayment
            {
                CompanyId = companyId, BookingId = bookingId, Amount = collected, Currency = booking.Currency,
                PaymentMethod = request.PaymentMethod, Reference = request.Reference?.Trim(), Status = SitePaymentStatus.Completed,
                PaidAt = money.PaidAt, CreatedBy = userId,
            });
            booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes,
                $"[{WorkflowRuleCode}] รับชำระตอนให้บริการเสร็จ {collected:N2} ({request.PaymentMethod}) ลง {docNo}");
        }
        if (failure != null) booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes, failure);
        booking.UpdatedBy = userId;
        await _db.SaveChangesAsync();
        return failure ?? (collected > 0m ? $"รับชำระ {collected:N2} บาท ลงเอกสาร {docNo} แล้ว" : null);
    }

    // ═══════════════ รายละเอียดการจอง (หน้าทำงาน) ═══════════════

    /// <summary>ผู้ให้บริการที่มอบหมาย — การจองเก่าที่มีผู้ให้บริการคนเดียวแต่ไม่มีแถวมอบหมาย ⇒ แสดงคนนั้น 100%</summary>
    private async Task FillAssignmentsAsync(Guid companyId, BookingResponse resp)
    {
        var rows = await _db.SiteBookingAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.BookingId == resp.Id && !a.IsDeleted)
            .OrderByDescending(a => a.SharePercent).ThenBy(a => a.ResourceName)
            .Select(a => new BookingAssignmentDto { ResourceId = a.ResourceId, ResourceName = a.ResourceName, SharePercent = a.SharePercent })
            .ToListAsync();
        if (rows.Count == 0 && resp.ResourceId is Guid rid && resp.ResourceName != null)
            rows.Add(new BookingAssignmentDto { ResourceId = rid, ResourceName = resp.ResourceName, SharePercent = 100m });
        foreach (var r in rows) r.IsPrimary = r.ResourceId == resp.ResourceId;
        resp.Assignments = rows;
    }

    /// <summary>เอกสารทุกใบ (เลขการจองบนเอกสาร) · ประวัติรับเงิน · ไทม์ไลน์ · แผนตอนเสร็จ</summary>
    private async Task FillWorkspaceAsync(Guid companyId, Guid siteId, BookingResponse resp)
    {
        // ฝ่ายค้านรอบ 207 ข้อ 3: เลขการจองซ้ำข้ามเว็บได้ ⇒ ขอบเขตเดียวกับตอนลงรับชำระ
        var docs = await BookingDocumentsQuery(companyId, resp.BookingNumber, resp.ErpDocumentId)
            .OrderBy(d => d.CreatedAt)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.Status, d.DocumentDate, d.TotalAmount, d.BalanceDue, d.IsDeposit })
            .ToListAsync();
        resp.Documents = docs.Select(d => new BookingDocumentLinkDto
        {
            Id = d.Id, DocumentNumber = string.IsNullOrWhiteSpace(d.DocumentNumber) || d.DocumentNumber.StartsWith("DRAFT-") ? "(ร่าง)" : d.DocumentNumber,
            DocumentType = d.DocumentType.ToString(),
            TypeLabel = d.IsDeposit ? $"{DocumentTypeNames.Title(d.DocumentType, null)} (มัดจำ)" : DocumentTypeNames.Title(d.DocumentType, null),
            Status = d.Status.ToString(), StatusLabel = LodgingReservationDocuments.StatusLabel(d.Status),
            DocumentDate = d.DocumentDate, TotalAmount = d.TotalAmount, BalanceDue = d.BalanceDue, IsDeposit = d.IsDeposit,
            IsPrimaryLink = d.Id == resp.ErpDocumentId,
        }).ToList();

        var docIds = docs.Select(d => d.Id).ToList();
        var docPays = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && docIds.Contains(p.DocumentId) && !p.IsDeleted)
            .OrderBy(p => p.PaymentDate)
            .Select(p => new { p.PaymentDate, p.Amount, p.PaymentMethod, p.Reference, p.DocumentId })
            .ToListAsync();
        var lines = docPays.Select(p => new BookingPaymentLineDto
        {
            PaidAt = p.PaymentDate, Amount = p.Amount, Source = PaymentMethodLabel(p.PaymentMethod), Reference = p.Reference,
            DocumentNumber = docs.FirstOrDefault(d => d.Id == p.DocumentId)?.DocumentNumber,
        }).ToList();
        // เงินที่บันทึกบนการจอง (มัดจำ/gateway) ที่ไม่ได้ลงเป็น Payment ของเอกสาร — ใบเสร็จ (Receipt) รับเงินในตัว
        foreach (var d in docs.Where(d => d.DocumentType == DocumentType.Receipt && d.Status != DocumentStatus.Voided
                     && d.Status != DocumentStatus.Rejected && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                     && docPays.All(p => p.DocumentId != d.Id)))
            lines.Add(new BookingPaymentLineDto { PaidAt = d.DocumentDate, Amount = d.TotalAmount, Source = d.IsDeposit ? "มัดจำ (ใบเสร็จ)" : "ใบเสร็จรับเงิน", DocumentNumber = d.DocumentNumber });
        resp.PaymentLines = lines.OrderBy(l => l.PaidAt).ToList();

        var started = await _db.SiteBookings.AsNoTracking().Where(b => b.Id == resp.Id && b.CompanyId == companyId)
            .Select(b => b.StartedAt).FirstOrDefaultAsync();
        resp.StartedAt = started;
        var tl = new List<BookingTimelineDto> { new() { At = resp.CreatedAt, Label = "จอง" } };
        if (resp.ConfirmedAt is DateTime c) tl.Add(new() { At = c, Label = "ยืนยันการจอง" });
        if (started is DateTime s) tl.Add(new() { At = s, Label = "เริ่มให้บริการ" });
        if (resp.CompletedAt is DateTime done) tl.Add(new() { At = done, Label = "ให้บริการเสร็จ" });
        if (resp.CancelledAt is DateTime x) tl.Add(new() { At = x, Label = resp.Status == BookingStatus.NoShow ? "ไม่มา" : "ยกเลิก" });
        resp.Timeline = tl.OrderBy(t => t.At).ToList();

        resp.ServiceProductName = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.Id == resp.BookingServiceId && s.CompanyId == companyId && s.Product != null)
            .Select(s => s.Product!.Code + " " + s.Product.Name).FirstOrDefaultAsync();
        resp.CompletionPlan = await CompletionPlanAsync(companyId, siteId, resp);
        resp.CustomerStatusLabel = BookingStatusLabels.Customer(resp.Status);
    }

    private static string PaymentMethodLabel(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "เงินสด",
        PaymentMethod.BankTransfer => "โอนเงิน",
        PaymentMethod.CreditCard => "บัตรเครดิต",
        PaymentMethod.Cheque => "เช็ค",
        PaymentMethod.PromptPay => "พร้อมเพย์",
        PaymentMethod.DirectDebit => "หักบัญชี",
        PaymentMethod.EWallet => "e-Wallet",
        _ => "อื่น ๆ",
    };
}
