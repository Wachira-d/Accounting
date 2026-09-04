using System.Net;
using Accounting.Helpers;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Lodging;

/// <summary>โมดูลที่พัก — แม่บ้าน · คำขอแขก · แดชบอร์ด · ปฏิทิน · mapping · สลิป · แจ้งเตือน (partial 4/4)</summary>
public partial class LodgingService
{
    // ═══════════════════════════ Mapping ═══════════════════════════

    /// <summary>mask เลขบัตร/พาสปอร์ต (PDPA ม.26) — 13 หลัก: 1-XXXX-XXXXX-XX-3 · อื่น ๆ: โชว์ 2 ตัวท้าย</summary>
    internal static string? MaskId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var s = id.Trim();
        var digits = new string(s.Where(char.IsLetterOrDigit).ToArray());
        if (digits.Length == 13 && digits.All(char.IsDigit)) return $"{digits[0]}-XXXX-XXXXX-XX-{digits[12]}";
        if (digits.Length <= 2) return new string('X', digits.Length);
        return new string('X', digits.Length - 2) + digits[^2..];
    }

    private async Task<LodgingReservationResponse> MapAsync(Guid companyId, LodgingReservation r, bool includeToken, bool includeInternal)
    {
        var prop = r.Property ?? await RequirePropertyAsync(companyId, r.PropertyId);
        var docIds = new[] { r.DepositDocumentId, r.FinalDocumentId }.Where(x => x != null).Select(x => x!.Value).ToList();
        var docNos = docIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && docIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.DocumentNumber);
        var (rules, nonRefundable) = SnapshotRules(r);
        string? policyName = null;
        if (r.CancellationPolicyId is Guid pid)
            policyName = await _db.LodgingCancellationPolicies.AsNoTracking().Where(p => p.Id == pid && p.CompanyId == companyId).Select(p => p.Name).FirstOrDefaultAsync();
        var requests = await _db.LodgingGuestRequests.AsNoTracking().Where(g => g.CompanyId == companyId && g.ReservationId == r.Id)
            .OrderByDescending(g => g.CreatedAt).ToListAsync();

        var res = new LodgingReservationResponse
        {
            Id = r.Id, PropertyId = r.PropertyId, PropertyName = prop.Name, ReservationNumber = r.ReservationNumber,
            PublicToken = includeToken ? r.PublicToken : null, Status = r.Status, Source = r.Source, SourceReference = r.SourceReference,
            CheckInDate = r.CheckInDate, CheckOutDate = r.CheckOutDate, Nights = r.Nights, Adults = r.Adults, Children = r.Children, Infants = r.Infants,
            ArrivalTime = r.ArrivalTime, SpecialRequests = r.SpecialRequests, ContactId = r.ContactId,
            GuestName = r.GuestName, GuestEmail = r.GuestEmail, GuestPhone = r.GuestPhone, GuestNationality = r.GuestNationality,
            GuestIdNumberMasked = MaskId(r.GuestIdNumber), GuestAddress = r.GuestAddress, GuestTaxId = r.GuestTaxId, GuestCompanyName = r.GuestCompanyName,
            RatePlanId = r.RatePlanId, RatePlanName = r.RatePlan?.Name, CancellationPolicyId = r.CancellationPolicyId, CancellationPolicyName = policyName,
            CancellationRules = rules.Select(x => new LodgingCancellationRuleDto(x.DaysBefore, x.PenaltyPercent)).ToList(), NonRefundable = nonRefundable,
            PromoCode = r.PromoCode, RoomSubtotal = r.RoomSubtotal, ExtrasTotal = r.ExtrasTotal, DiscountAmount = r.DiscountAmount,
            ServiceChargeAmount = r.ServiceChargeAmount, VatAmount = r.VatAmount, TotalAmount = r.TotalAmount, FolioTotal = r.FolioTotal,
            GrandTotal = r.TotalAmount + r.FolioTotal, DepositRequired = r.DepositRequired, DepositPaid = r.DepositPaid, PaidAmount = r.PaidAmount,
            BalanceDue = Accounting.Helpers.LodgingAmounts.BalanceDue(r.TotalAmount, r.FolioTotal, r.PaidAmount), Currency = r.Currency, HoldExpiresAt = r.HoldExpiresAt,
            DepositDocumentId = r.DepositDocumentId, DepositDocumentNumber = r.DepositDocumentId is Guid dd ? docNos.GetValueOrDefault(dd) : null,
            FinalDocumentId = r.FinalDocumentId, FinalDocumentNumber = r.FinalDocumentId is Guid fd ? docNos.GetValueOrDefault(fd) : null,
            // ⚠️ ไม่คืน **storage key** ดิบ ๆ — คืน endpoint ที่มีด่านแทน (LDG-P2-06)
            // ฝั่งพนักงานใช้เส้นที่ต้องมีสิทธิ์ LodgingManage · ฝั่งแขกใช้เส้นที่ต้องมี token ของตัวเอง
            PaymentSlipUrl = SlipViewUrl(companyId, r, includeInternal),
            PaymentReference = r.PaymentReference, SlipUploadedAt = r.SlipUploadedAt,
            SlipRejectedCount = r.SlipRejectedCount, SlipRejectedReason = r.SlipRejectedReason,
            SlipRejectedAt = r.SlipRejectedAt, SlipUploadBlocked = r.SlipUploadBlocked,
            ConfirmedAt = r.ConfirmedAt, CheckedInAt = r.CheckedInAt, CheckedOutAt = r.CheckedOutAt, CancelledAt = r.CancelledAt,
            CancellationReason = r.CancellationReason, CancellationFee = r.CancellationFee, RefundAmount = r.RefundAmount,
            InternalNotes = includeInternal ? r.InternalNotes : null, CreatedAt = r.CreatedAt,
            ConfirmationMessage = prop.ConfirmationMessage, HouseRules = prop.HouseRules,
            CheckInTime = Time(prop.CheckInTime), CheckOutTime = Time(prop.CheckOutTime), PropertyPhone = prop.Phone, PropertyLineId = prop.LineId,
            PropertyAddress = prop.Address, PropertyMapUrl = prop.MapUrl,
            Rooms = r.Rooms.Select(x => new LodgingReservationRoomDto
            {
                Id = x.Id, RoomTypeId = x.RoomTypeId, RoomTypeName = x.RoomTypeName, UnitId = x.UnitId, UnitNumber = x.Unit?.Number,
                Adults = x.Adults, Children = x.Children, ExtraBeds = x.ExtraBeds, Subtotal = x.Subtotal, GuestNames = x.GuestNames,
                Nights = ParseNights(x.NightlyRatesJson),
            }).ToList(),
            Extras = r.Extras.Select(e => new LodgingReservationExtraDto { Id = e.Id, ExtraId = e.ExtraId, Name = e.Name, PriceMode = e.PriceMode, UnitPrice = e.UnitPrice, Quantity = e.Quantity, Total = e.Total }).ToList(),
            Charges = r.Charges.OrderBy(c => c.ChargedAt).Select(c => new LodgingFolioChargeDto
            {
                Id = c.Id, ProductId = c.ProductId, Description = c.Description, Quantity = c.Quantity, UnitPrice = c.UnitPrice, Total = c.Total,
                VatRate = c.VatRate, Source = c.Source, Status = c.Status, ChargedAt = c.ChargedAt, PosOrderNumber = c.PosOrderNumber, Notes = c.Notes,
            }).ToList(),
            Requests = requests.Select(g => ToDto(g, r)).ToList(),
        };
        return res;
    }

    private static List<LodgingNightlyRateDto> ParseNights(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<LodgingNightlyRateDto>>(json, JsonOpts) ?? new(); } catch { return new(); }
    }

    private static LodgingGuestRequestDto ToDto(LodgingGuestRequest g, LodgingReservation? r) => new()
    {
        Id = g.Id, ReservationId = g.ReservationId, ReservationNumber = r?.ReservationNumber, GuestName = r?.GuestName,
        UnitNumbers = r == null ? null : string.Join(", ", r.Rooms.Where(x => x.Unit != null).Select(x => x.Unit!.Number)),
        RequestType = g.RequestType, Details = g.Details, Status = g.Status, ResolvedAt = g.ResolvedAt, ResolvedBy = g.ResolvedBy,
        ResponseNote = g.ResponseNote, CreatedAt = g.CreatedAt,
    };

    // ═══════════════════════════ Housekeeping ═══════════════════════════

    public async Task<List<LodgingHousekeepingTaskDto>> GetTasksAsync(Guid companyId, Guid propertyId, string? status, DateTime? date)
    {
        var q = _db.LodgingHousekeepingTasks.AsNoTracking().Where(t => t.CompanyId == companyId && t.PropertyId == propertyId);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<LodgingTaskStatus>(status, true, out var st)) q = q.Where(t => t.Status == st);
        else if (string.IsNullOrWhiteSpace(status)) q = q.Where(t => t.Status != LodgingTaskStatus.Verified && t.Status != LodgingTaskStatus.Cancelled);
        if (date is DateTime d) { var f = d.Date.AddHours(-7); var t2 = f.AddDays(1); q = q.Where(t => (t.DueAt ?? t.CreatedAt) >= f && (t.DueAt ?? t.CreatedAt) < t2); }
        return await q.OrderBy(t => t.Status).ThenByDescending(t => t.Priority).ThenBy(t => t.DueAt)
            .Select(t => new LodgingHousekeepingTaskDto
            {
                Id = t.Id, PropertyId = t.PropertyId, UnitId = t.UnitId, UnitNumber = t.Unit.Number, RoomTypeName = t.Unit.RoomType.Name,
                ReservationId = t.ReservationId, TaskType = t.TaskType, Priority = t.Priority, Status = t.Status,
                AssignedEmployeeId = t.AssignedEmployeeId, AssignedToName = t.AssignedToName, DueAt = t.DueAt, StartedAt = t.StartedAt,
                CompletedAt = t.CompletedAt, VerifiedAt = t.VerifiedAt, EstimatedMinutes = t.EstimatedMinutes, Notes = t.Notes, CreatedAt = t.CreatedAt,
            }).ToListAsync();
    }

    public async Task<LodgingHousekeepingTaskDto> CreateTaskAsync(Guid companyId, LodgingHousekeepingTaskDto dto, string userId)
    {
        var unit = await _db.LodgingUnits.AsNoTracking().Include(u => u.RoomType).FirstOrDefaultAsync(u => u.Id == dto.UnitId && u.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบห้อง");
        var t = new LodgingHousekeepingTask
        {
            CompanyId = companyId, PropertyId = unit.RoomType.PropertyId, UnitId = unit.Id, ReservationId = dto.ReservationId,
            TaskType = dto.TaskType, Priority = dto.Priority, Status = dto.AssignedEmployeeId != null || !string.IsNullOrWhiteSpace(dto.AssignedToName) ? LodgingTaskStatus.Assigned : LodgingTaskStatus.Pending,
            AssignedEmployeeId = dto.AssignedEmployeeId, AssignedToName = dto.AssignedToName, DueAt = dto.DueAt,
            EstimatedMinutes = dto.EstimatedMinutes > 0 ? dto.EstimatedMinutes : 30, Notes = dto.Notes, CreatedBy = userId,
        };
        _db.LodgingHousekeepingTasks.Add(t);
        if (t.TaskType == LodgingHousekeepingTaskType.Maintenance)
        {
            var tracked = await _db.LodgingUnits.FirstAsync(u => u.Id == unit.Id);
            if (tracked.HousekeepingStatus is LodgingHousekeepingStatus.VacantClean or LodgingHousekeepingStatus.VacantDirty) tracked.HousekeepingStatus = LodgingHousekeepingStatus.Maintenance;
        }
        await _db.SaveChangesAsync();
        return (await GetTasksAsync(companyId, t.PropertyId, t.Status.ToString(), null)).First(x => x.Id == t.Id);
    }

    public async Task<LodgingHousekeepingTaskDto> UpdateTaskStatusAsync(Guid companyId, Guid taskId, LodgingTaskStatusRequest request, string userId)
    {
        var t = await _db.LodgingHousekeepingTasks.Include(x => x.Unit).FirstOrDefaultAsync(x => x.Id == taskId && x.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบงาน");
        var now = DateTime.UtcNow;
        t.Status = request.Status;
        if (request.AssignedEmployeeId != null) t.AssignedEmployeeId = request.AssignedEmployeeId;
        if (request.AssignedToName != null) t.AssignedToName = request.AssignedToName;
        switch (request.Status)
        {
            case LodgingTaskStatus.InProgress:
                t.StartedAt ??= now;
                if (t.Unit.HousekeepingStatus != LodgingHousekeepingStatus.Occupied) t.Unit.HousekeepingStatus = t.TaskType == LodgingHousekeepingTaskType.Maintenance ? LodgingHousekeepingStatus.Maintenance : LodgingHousekeepingStatus.Cleaning;
                break;
            case LodgingTaskStatus.Completed:
                t.CompletedAt ??= now;
                // เสร็จ = พร้อมขาย (ถ้าที่พักต้องการขั้นตรวจ ให้หัวหน้ากด Verified ทีหลัง — สถานะห้องไม่ถอยหลัง)
                if (t.Unit.HousekeepingStatus != LodgingHousekeepingStatus.Occupied && !t.Unit.IsOutOfService) t.Unit.HousekeepingStatus = LodgingHousekeepingStatus.VacantClean;
                break;
            case LodgingTaskStatus.Verified:
                t.CompletedAt ??= now; t.VerifiedAt = now;
                if (t.Unit.HousekeepingStatus != LodgingHousekeepingStatus.Occupied && !t.Unit.IsOutOfService) t.Unit.HousekeepingStatus = LodgingHousekeepingStatus.VacantClean;
                break;
            case LodgingTaskStatus.Cancelled:
                if (t.Unit.HousekeepingStatus is LodgingHousekeepingStatus.Cleaning or LodgingHousekeepingStatus.Inspecting) t.Unit.HousekeepingStatus = LodgingHousekeepingStatus.VacantDirty;
                break;
        }
        if (!string.IsNullOrWhiteSpace(request.Note)) t.Notes = string.IsNullOrWhiteSpace(t.Notes) ? request.Note : t.Notes + "\n" + request.Note;
        t.UpdatedBy = userId; t.UpdatedAt = now;
        await _db.SaveChangesAsync();
        return (await GetTasksAsync(companyId, t.PropertyId, t.Status.ToString(), null)).First(x => x.Id == t.Id);
    }

    // ═══════════════════════════ Guest requests ═══════════════════════════

    public async Task<List<LodgingGuestRequestDto>> GetGuestRequestsAsync(Guid companyId, Guid propertyId, bool openOnly)
    {
        var q = _db.LodgingGuestRequests.AsNoTracking().Include(g => g.Reservation).ThenInclude(r => r.Rooms).ThenInclude(x => x.Unit)
            .Where(g => g.CompanyId == companyId && g.PropertyId == propertyId);
        if (openOnly) q = q.Where(g => g.Status != LodgingTaskStatus.Completed && g.Status != LodgingTaskStatus.Verified && g.Status != LodgingTaskStatus.Cancelled);
        var list = await q.OrderByDescending(g => g.CreatedAt).Take(500).ToListAsync();
        return list.Select(g => ToDto(g, g.Reservation)).ToList();
    }

    public async Task<LodgingGuestRequestDto> ResolveGuestRequestAsync(Guid companyId, Guid requestId, LodgingGuestRequestResolve request, string userId)
    {
        var g = await _db.LodgingGuestRequests.Include(x => x.Reservation).ThenInclude(r => r.Rooms).ThenInclude(x => x.Unit)
            .FirstOrDefaultAsync(x => x.Id == requestId && x.CompanyId == companyId) ?? throw new KeyNotFoundException("ไม่พบคำขอ");
        g.Status = request.Status;
        if (request.ResponseNote != null) g.ResponseNote = request.ResponseNote;
        if (request.Status is LodgingTaskStatus.Completed or LodgingTaskStatus.Verified or LodgingTaskStatus.Cancelled) { g.ResolvedAt = DateTime.UtcNow; g.ResolvedBy = userId; }
        g.UpdatedBy = userId; g.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return ToDto(g, g.Reservation);
    }

    // ═══════════════════════════ Dashboard / calendar ═══════════════════════════

    public async Task<LodgingDashboard> GetDashboardAsync(Guid companyId, Guid propertyId, DateTime? date)
    {
        await RequirePropertyAsync(companyId, propertyId);
        await ExpireHoldsAsync(companyId, propertyId);
        var day = (date ?? DateTime.UtcNow.AddHours(7)).Date;
        var units = await _db.LodgingUnits.AsNoTracking().Include(u => u.RoomType)
            .Where(u => u.CompanyId == companyId && u.RoomType.PropertyId == propertyId && u.IsActive)
            .OrderBy(u => u.RoomType.SortOrder).ThenBy(u => u.SortOrder).ThenBy(u => u.Number).ToListAsync();
        var active = await _db.LodgingReservations.AsNoTracking().Include(r => r.Rooms).ThenInclude(x => x.Unit)
            .Where(r => r.CompanyId == companyId && r.PropertyId == propertyId
                && (r.Status == LodgingReservationStatus.Pending || r.Status == LodgingReservationStatus.Confirmed || r.Status == LodgingReservationStatus.CheckedIn)
                && r.CheckInDate <= day && r.CheckOutDate >= day)
            .ToListAsync();
        var inHouse = active.Where(r => r.Status == LodgingReservationStatus.CheckedIn).ToList();
        var arrivals = active.Where(r => r.CheckInDate == day && r.Status != LodgingReservationStatus.CheckedIn).ToList();
        var departures = inHouse.Where(r => r.CheckOutDate == day).ToList();
        var occupiedUnitIds = inHouse.SelectMany(r => r.Rooms).Where(x => x.UnitId != null).Select(x => x.UnitId!.Value).ToHashSet();
        var monthStart = new DateTime(day.Year, day.Month, 1);
        var revenue = await _db.LodgingReservations.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.PropertyId == propertyId && r.Status == LodgingReservationStatus.CheckedOut
                && r.CheckedOutAt != null && r.CheckedOutAt >= monthStart.AddHours(-7) && r.CheckedOutAt < monthStart.AddMonths(1).AddHours(-7))
            .SumAsync(r => r.TotalAmount + r.FolioTotal);

        var sellable = units.Where(u => !u.IsOutOfService).ToList();
        var board = units.Select(u =>
        {
            var cur = inHouse.FirstOrDefault(r => r.Rooms.Any(x => x.UnitId == u.Id));
            var arr = arrivals.FirstOrDefault(r => r.Rooms.Any(x => x.UnitId == u.Id));
            return new LodgingUnitBoardItem
            {
                UnitId = u.Id, Number = u.Number, Floor = u.Floor, RoomTypeName = u.RoomType.Name, HousekeepingStatus = u.HousekeepingStatus, IsOutOfService = u.IsOutOfService,
                CurrentReservationId = cur?.Id, CurrentReservationNumber = cur?.ReservationNumber, CurrentGuestName = cur?.GuestName, CurrentCheckOut = cur?.CheckOutDate,
                ArrivingReservationId = arr?.Id, ArrivingGuestName = arr?.GuestName,
            };
        }).ToList();

        return new LodgingDashboard
        {
            Date = day, TotalUnits = sellable.Count, OccupiedUnits = occupiedUnitIds.Count,
            OccupancyPercent = sellable.Count == 0 ? 0 : Math.Round(100m * occupiedUnitIds.Count / sellable.Count, 1, MidpointRounding.AwayFromZero),
            ArrivalsToday = arrivals.Count, DeparturesToday = departures.Count, InHouse = inHouse.Count,
            PendingReservations = await _db.LodgingReservations.CountAsync(r => r.CompanyId == companyId && r.PropertyId == propertyId && r.Status == LodgingReservationStatus.Pending),
            PendingSlips = await _db.LodgingReservations.CountAsync(r => r.CompanyId == companyId && r.PropertyId == propertyId && r.Status == LodgingReservationStatus.Pending && r.SlipUploadedAt != null && r.DepositPaid == 0),
            DirtyUnits = units.Count(u => u.HousekeepingStatus == LodgingHousekeepingStatus.VacantDirty),
            OutOfServiceUnits = units.Count(u => u.IsOutOfService),
            OpenHousekeepingTasks = await _db.LodgingHousekeepingTasks.CountAsync(t => t.CompanyId == companyId && t.PropertyId == propertyId && (t.Status == LodgingTaskStatus.Pending || t.Status == LodgingTaskStatus.Assigned || t.Status == LodgingTaskStatus.InProgress)),
            OpenGuestRequests = await _db.LodgingGuestRequests.CountAsync(g => g.CompanyId == companyId && g.PropertyId == propertyId && (g.Status == LodgingTaskStatus.Pending || g.Status == LodgingTaskStatus.Assigned || g.Status == LodgingTaskStatus.InProgress)),
            RevenueMonthToDate = revenue,
            Arrivals = arrivals.OrderBy(r => r.ArrivalTime).Select(ToListItem).ToList(),
            Departures = departures.Select(ToListItem).ToList(),
            RoomBoard = board,
        };
    }

    private static LodgingReservationListItem ToListItem(LodgingReservation r) => new()
    {
        Id = r.Id, ReservationNumber = r.ReservationNumber, Status = r.Status, Source = r.Source, GuestName = r.GuestName, GuestPhone = r.GuestPhone,
        CheckInDate = r.CheckInDate, CheckOutDate = r.CheckOutDate, Nights = r.Nights, RoomCount = r.Rooms.Count, RoomSummary = RoomSummary(r),
        UnitNumbers = string.Join(", ", r.Rooms.Where(x => x.Unit != null).Select(x => x.Unit!.Number)),
        TotalAmount = r.TotalAmount, FolioTotal = r.FolioTotal, PaidAmount = r.PaidAmount, BalanceDue = Accounting.Helpers.LodgingAmounts.BalanceDue(r.TotalAmount, r.FolioTotal, r.PaidAmount),
        DepositRequired = r.DepositRequired, HoldExpiresAt = r.HoldExpiresAt, HasSlip = r.SlipUploadedAt != null, CreatedAt = r.CreatedAt,
    };

    public async Task<LodgingCalendar> GetCalendarAsync(Guid companyId, Guid propertyId, DateTime from, DateTime to)
    {
        var f = from.Date; var t = to.Date;
        if (t <= f) t = f.AddDays(14);
        if ((t - f).TotalDays > 92) t = f.AddDays(92);
        var ctx = await LoadContextAsync(companyId, propertyId, f, t);
        var units = await _db.LodgingUnits.AsNoTracking().Include(u => u.RoomType)
            .Where(u => u.CompanyId == companyId && u.RoomType.PropertyId == propertyId && u.IsActive)
            .OrderBy(u => u.RoomType.SortOrder).ThenBy(u => u.SortOrder).ThenBy(u => u.Number).ToListAsync();
        var rooms = await _db.LodgingReservationRooms.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.Reservation.PropertyId == propertyId
                && x.Reservation.CheckInDate < t && x.Reservation.CheckOutDate > f
                && (x.Reservation.Status == LodgingReservationStatus.Pending || x.Reservation.Status == LodgingReservationStatus.Confirmed || x.Reservation.Status == LodgingReservationStatus.CheckedIn))
            .Select(x => new LodgingCalendarBar
            {
                ReservationId = x.ReservationId, ReservationRoomId = x.Id, ReservationNumber = x.Reservation.ReservationNumber, GuestName = x.Reservation.GuestName,
                RoomTypeId = x.RoomTypeId, RoomTypeName = x.RoomTypeName, CheckIn = x.Reservation.CheckInDate, CheckOut = x.Reservation.CheckOutDate,
                Status = x.Reservation.Status, UnitId = x.UnitId,
            }).ToListAsync();
        var cal = new LodgingCalendar { From = f, To = t };
        foreach (var u in units)
            cal.Rows.Add(new LodgingCalendarRow
            {
                UnitId = u.Id, Number = u.Number, RoomTypeId = u.RoomTypeId, RoomTypeName = u.RoomType.Name, HousekeepingStatus = u.HousekeepingStatus,
                IsOutOfService = u.IsOutOfService, Bars = rooms.Where(b => b.UnitId == u.Id).ToList(),
            });
        cal.Unassigned = rooms.Where(b => b.UnitId == null).ToList();
        var now = DateTime.UtcNow;
        foreach (var rt in ctx.RoomTypes)
        {
            var av = new LodgingCalendarAvailability { RoomTypeId = rt.Id, RoomTypeName = rt.Name, TotalUnits = ctx.UnitsByRoomType.GetValueOrDefault(rt.Id) };
            var input = ctx.InputFor(rt, null);
            var overrides = ctx.OverridesFor(rt.Id);
            for (var d = f; d < t; d = d.AddDays(1))
            {
                var free = LodgingAvailability.AvailableRooms(d, d.AddDays(1), av.TotalUnits, 0, ctx.BookedFor(rt.Id), overrides, now);
                var ov = overrides.FirstOrDefault(o => o.Date.Date == d);
                av.Days.Add(new LodgingCalendarDay(d, free, LodgingPricingEngine.NightlyRate(d, input).Rate, ov?.StopSell ?? false, ov?.Allotment));
            }
            cal.Availability.Add(av);
        }
        return cal;
    }

    // ═══════════════════════════ Slip file ═══════════════════════════

    /// <summary>พาธไฟล์สลิปที่ปลอดภัย — ตรรกะด่านอยู่ที่ <see cref="LodgingSlipPath"/>
    /// (pure + มีเทสต์) · ที่นี่แค่บอกว่า wwwroot อยู่ไหน</summary>
    private static string? ResolveSlipPath(string? storedUrl)
        => LodgingSlipPath.Resolve(storedUrl,
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));

    private static string SlipContentType(string path) => LodgingSlipPath.ContentType(path);

    /// <summary>URL ที่ให้หน้าเว็บเปิดดูสลิป — <c>null</c> เมื่อยังไม่มีสลิป
    ///
    /// <para>เลือกเส้นตาม**ผู้ดู**: พนักงาน (<paramref name="forStaff"/>) เดินเส้นที่
    /// ตรวจสิทธิ์ <c>LodgingManage</c> · แขกเดินเส้นที่พิสูจน์ด้วย <c>PublicToken</c>
    /// ของตัวเอง — การจองที่ไม่ผูกเว็บไซต์ (พนักงานสร้างเอง) ไม่มีเส้นฝั่งแขก</para></summary>
    private static string? SlipViewUrl(Guid companyId, LodgingReservation r, bool forStaff)
    {
        if (string.IsNullOrWhiteSpace(r.PaymentSlipUrl)) return null;
        if (forStaff) return $"/api/companies/{companyId}/lodging/reservations/{r.Id}/slip";
        if (r.SiteId is not Guid sid || string.IsNullOrWhiteSpace(r.PublicToken)) return null;
        return $"/api/companies/{companyId}/cms/sites/{sid}/lodging/reservations/"
             + $"{Uri.EscapeDataString(r.PublicToken)}/slip";
    }


    private async Task<string> SaveSlipFileAsync(Guid companyId, Guid reservationId, IFormFile file)
    {
        if (file == null || file.Length == 0) throw new BusinessRuleException("กรุณาเลือกไฟล์สลิป");
        if (file.Length > 10 * 1024 * 1024) throw new BusinessRuleException("ไฟล์ใหญ่เกิน 10 MB");
        var ct = file.ContentType ?? "";
        if (!ct.StartsWith("image/") && ct != "application/pdf") throw new BusinessRuleException("รองรับเฉพาะรูปภาพหรือ PDF");
        // โฟลเดอร์นี้ต้องอยู่ใน publicUploadPrefixes ของ Program.cs (ไม่งั้น static handler ตอบ 404 — tools/upload_route_check.py)
        var relDir = $"uploads/lodging-slips/{DateTime.UtcNow:yyyy-MM}";
        var absDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", relDir);
        Directory.CreateDirectory(absDir);
        string absPath, relUrl, storedName;
        if (_images != null && _images.IsProcessableImage(ct))
        {
            await using var s = file.OpenReadStream();
            var processed = await _images.ProcessAndSaveAsync(s, ct, file.FileName ?? "slip", absDir, "/" + relDir, ImageProfile.Slip);
            absPath = processed.AbsolutePath; relUrl = processed.RelativeUrl; storedName = Path.GetFileName(absPath);
        }
        else
        {
            var ext = Path.GetExtension(file.FileName); if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
            storedName = $"{Guid.NewGuid():N}{ext}"; absPath = Path.Combine(absDir, storedName);
            await using var fs = File.Create(absPath);
            await file.CopyToAsync(fs);
            relUrl = "/" + relDir + "/" + storedName;
        }
        _db.Set<FileAttachment>().Add(new FileAttachment
        {
            CompanyId = companyId, FileName = storedName, OriginalFileName = file.FileName ?? storedName, ContentType = ct,
            FileSize = file.Length, StoragePath = absPath, EntityType = "LodgingReservation", EntityId = reservationId,
            UploadedByUserId = Guid.Empty, CreatedBy = "lodging-guest",
        });
        return relUrl;
    }

    // ═══════════════════════════ Notifications ═══════════════════════════

    /// <summary>อีเมลแจ้งแขก + เจ้าของ — best-effort: ล้มเหลวแค่ log ไม่กระทบการจอง (graceful degradation)</summary>
    private async Task TryNotifyAsync(Guid companyId, Guid reservationId, string evt)
    {
        // ⚠️ เดิม `if (_email == null) return;` อยู่บนสุด ⇒ ถ้าวันหนึ่งบริษัทไม่ตั้งอีเมล
        // ช่องทาง LINE จะเงียบตามไปด้วยทั้งที่ตั้งค่าไว้แล้ว — สองช่องทางต้องแยกกัน
        await TryNotifyLineAsync(companyId, reservationId, evt);
        if (_email == null) return;
        try
        {
            var r = await ResQuery(companyId).AsNoTracking().FirstOrDefaultAsync(x => x.Id == reservationId);
            if (r == null) return;
            var prop = r.Property;
            var link = await GuestLinkAsync(companyId, r);
            var enc = (string? s) => WebUtility.HtmlEncode(s ?? "");
            var summary = $@"<p><b>{enc(prop.Name)}</b><br>เลขที่จอง <b>{enc(r.ReservationNumber)}</b><br>
เข้าพัก {r.CheckInDate:dd/MM/yyyy} (หลัง {Time(prop.CheckInTime)}) — ออก {r.CheckOutDate:dd/MM/yyyy} (ก่อน {Time(prop.CheckOutTime)}) · {r.Nights} คืน<br>
ห้อง: {enc(RoomSummary(r))} · ผู้เข้าพัก {r.Adults} ผู้ใหญ่{(r.Children > 0 ? $" {r.Children} เด็ก" : "")}<br>
ยอดรวม {r.TotalAmount:N2} บาท · มัดจำ {r.DepositRequired:N2} บาท{(r.DepositPaid > 0 ? $" (รับแล้ว {r.DepositPaid:N2})" : "")}</p>"
                + (link != null ? $@"<p><a href=""{enc(link)}"">ดูรายละเอียด / อัปโหลดสลิป / ยกเลิก</a></p>" : "");

            if (!string.IsNullOrWhiteSpace(r.GuestEmail) && evt is "created" or "confirmed")
            {
                var subject = evt == "confirmed" ? $"ยืนยันการจอง {r.ReservationNumber} — {prop.Name}" : $"รับคำขอจอง {r.ReservationNumber} — {prop.Name}";
                var intro = evt == "confirmed"
                    ? $"<p>เรียน คุณ{enc(r.GuestName)}<br>การจองของท่านได้รับการยืนยันแล้ว</p>{(string.IsNullOrWhiteSpace(prop.ConfirmationMessage) ? "" : $"<p>{enc(prop.ConfirmationMessage)}</p>")}"
                    : $"<p>เรียน คุณ{enc(r.GuestName)}<br>เราได้รับคำขอจองของท่านแล้ว{(r.Status == LodgingReservationStatus.Pending && r.HoldExpiresAt != null ? $" กรุณาชำระมัดจำ {r.DepositRequired:N2} บาท และอัปโหลดสลิปภายใน {r.HoldExpiresAt.Value.AddHours(7):dd/MM/yyyy HH:mm} น. เพื่อยืนยันห้อง" : "")}</p>";
                var rules = string.IsNullOrWhiteSpace(prop.HouseRules) ? "" : $"<p><b>กติกาที่พัก</b><br>{enc(prop.HouseRules).Replace("\n", "<br>")}</p>";
                await _email.SendAsync(r.GuestEmail, subject, intro + summary + rules);
            }
            // สลิปไม่ผ่าน = แขกต้องลงมือทำอะไรต่อ ⇒ ห้ามเงียบ ต้องถึงตัวเขา ไม่ใช่แค่บนหน้าเว็บ
            // ที่เขาอาจไม่กลับมาเปิดอีกเลย (กติกา "ดัง 3 ที่": ตัวข้อมูล · สถานะ · คำตอบถึงผู้เกี่ยวข้อง)
            if (!string.IsNullOrWhiteSpace(r.GuestEmail) && evt == "slip-rejected")
            {
                var why = enc(r.SlipRejectedReason ?? "ข้อมูลในสลิปไม่ตรงกับยอดที่ต้องชำระ");
                var next = r.SlipUploadBlocked
                    ? "<p>ที่พัก<b>ปิดรับสลิปของการจองนี้แล้ว</b> — กรุณาชำระออนไลน์ผ่านลิงก์ด้านล่าง หรือติดต่อที่พักโดยตรง</p>"
                    : "<p>กรุณาตรวจสอบแล้ว<b>ส่งสลิปใหม่</b>ผ่านลิงก์ด้านล่าง — ที่พักต่อเวลาถือห้องให้แล้ว</p>";
                await _email.SendAsync(r.GuestEmail,
                    $"สลิปไม่ผ่านการตรวจสอบ — การจอง {r.ReservationNumber}",
                    $"<p>เรียน คุณ{enc(r.GuestName)}<br>สลิปที่ท่านส่งมายังไม่ผ่านการตรวจสอบ</p>"
                    + $"<p><b>เหตุผล:</b> {why}</p>" + next + summary);
            }
            if (prop.NotifyOwnerOnBooking && evt is "created" or "slip")
            {
                var to = (prop.NotifyEmails ?? prop.Email ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var subject = evt == "slip" ? $"[สลิปใหม่] {r.ReservationNumber} — {r.GuestName}" : $"[จองใหม่] {r.ReservationNumber} — {r.GuestName} ({RoomSummary(r)})";
                var body = $"<p>{(evt == "slip" ? "แขกอัปโหลดสลิปมัดจำแล้ว รอตรวจสอบ" : "มีการจองใหม่จากเว็บไซต์")}</p>{summary}<p>ติดต่อ: {enc(r.GuestPhone)} {enc(r.GuestEmail)}</p>";
                foreach (var addr in to) await _email.SendAsync(addr, subject, body);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ส่งอีเมลแจ้งการจองที่พัก {Id} ({Evt}) ไม่สำเร็จ", reservationId, evt); }
    }

    /// <summary>แจ้งกลุ่ม LINE ของที่พัก — เฉพาะเหตุการณ์ที่**เจ้าของต้องลงมือทำต่อ**
    /// (จองใหม่ · แขกส่งสลิปรอตรวจ) · ยืนยัน/ยกเลิกไม่ต้องเตือนซ้ำเพราะเป็นผลจาก
    /// การกดของเจ้าหน้าที่เอง</summary>
    private async Task TryNotifyLineAsync(Guid companyId, Guid reservationId, string evt)
    {
        if (_line == null || evt is not ("created" or "slip")) return;
        try
        {
            var r = await ResQuery(companyId).AsNoTracking().FirstOrDefaultAsync(x => x.Id == reservationId);
            if (r == null || !r.Property.NotifyOwnerOnBooking) return;
            await _line.NotifyLodgingBookingAsync(companyId, r.Property.Name, r.ReservationNumber,
                r.GuestName, RoomSummary(r), r.CheckInDate, r.CheckOutDate, r.Nights,
                r.TotalAmount, r.DepositRequired, isSlipUploaded: evt == "slip");
        }
        catch (Exception ex)
        {
            // ช่องทางแจ้งเตือนล้มต้องไม่ทำให้การจองล้ม — แต่ต้องดังใน log
            _logger.LogWarning(ex, "ส่ง LINE แจ้งการจองที่พัก {Id} ({Evt}) ไม่สำเร็จ", reservationId, evt);
        }
    }

    private async Task<string?> GuestLinkAsync(Guid companyId, LodgingReservation r)
    {
        if (r.SiteId == null) return null;
        var domain = await _db.SiteDomains.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.SiteId == r.SiteId && d.IsActive)
            .OrderByDescending(d => d.IsPrimary).Select(d => d.Domain).FirstOrDefaultAsync();
        return domain == null ? null : $"https://{domain}/reservation/{r.PublicToken}";
    }
}
