using System.Security.Cryptography;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Lodging;

/// <summary>โมดูลที่พัก — ค้นหา/เสนอราคา/จอง/มัดจำ/เช็คอิน-เอาต์/ยกเลิก (partial 2/3)</summary>
public partial class LodgingService
{
    private const string ResLockScope = "lodging-res";

    // ═══════════════════════════ Pricing context ═══════════════════════════

    /// <summary>ข้อมูลทั้งหมดที่ engine ต้องใช้ — โหลดครั้งเดียวต่อ request แล้วส่งเข้า
    /// LodgingPricingEngine (pure) ทั้งค้นหา/quote/สร้างจอง จึงได้เลขเดียวกันเสมอ</summary>
    private sealed class PricingContext
    {
        public LodgingProperty Property = null!;
        public decimal VatRate;
        public List<LodgingRoomType> RoomTypes = new();
        public List<LodgingSeason> Seasons = new();
        public List<LodgingRateOverride> Overrides = new();
        public List<LodgingRatePlan> RatePlans = new();
        public List<LodgingCancellationPolicy> Policies = new();
        public List<LodgingExtra> Extras = new();
        public Dictionary<Guid, int> UnitsByRoomType = new();
        public List<(Guid RoomTypeId, LodgingAvailability.BookedRange Range)> Booked = new();

        public IReadOnlyList<LodgingSeasonInput> SeasonsFor(Guid roomTypeId) => Seasons
            .Where(s => s.IsActive && (string.IsNullOrWhiteSpace(s.RoomTypeIdsJson) || ParseGuids(s.RoomTypeIdsJson).Contains(roomTypeId)))
            .Select(s => new LodgingSeasonInput(s.Name, s.SeasonType, s.StartDate, s.EndDate, s.Multiplier, s.IsRecurringYearly, s.MinNights))
            .ToList();

        public IReadOnlyList<LodgingOverrideInput> OverridesFor(Guid roomTypeId) => Overrides
            .Where(o => o.RoomTypeId == roomTypeId)
            .Select(o => new LodgingOverrideInput(o.Date, o.Rate, o.StopSell, o.Allotment, o.MinNights)).ToList();

        public IReadOnlyList<LodgingAvailability.BookedRange> BookedFor(Guid roomTypeId, Guid? excludeReservationId = null)
            => Booked.Where(b => b.RoomTypeId == roomTypeId).Select(b => b.Range).ToList();

        public LodgingRoomPricingInput InputFor(LodgingRoomType rt, LodgingRatePlan? plan) => new(
            rt.BaseRate, rt.PricingMode, rt.StandardOccupancy,
            rt.ExtraGuestPrice ?? Property.ExtraGuestPrice, rt.ExtraBedPrice ?? 0m,
            Property.WeekendMultiplier, Property.WeekendDaysMask,
            SeasonsFor(rt.Id), OverridesFor(rt.Id),
            plan == null ? null : new LodgingRatePlanInput(plan.Name, plan.AdjustMode, plan.AdjustValue, plan.IncludesBreakfast));
    }

    private async Task<PricingContext> LoadContextAsync(Guid companyId, Guid propertyId, DateTime checkIn, DateTime checkOut, Guid? excludeReservationId = null)
    {
        var prop = await RequirePropertyAsync(companyId, propertyId);
        var ctx = new PricingContext { Property = prop, VatRate = await EffectiveVatRateAsync(companyId, prop) };
        ctx.RoomTypes = await _db.LodgingRoomTypes.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.PropertyId == propertyId && r.IsActive)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync();
        ctx.Seasons = await _db.LodgingSeasons.AsNoTracking().Where(s => s.CompanyId == companyId && s.PropertyId == propertyId && s.IsActive).ToListAsync();
        var from = checkIn.Date; var to = checkOut.Date;
        ctx.Overrides = await _db.LodgingRateOverrides.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.RoomType.PropertyId == propertyId && o.Date >= from && o.Date < to).ToListAsync();
        ctx.RatePlans = await _db.LodgingRatePlans.AsNoTracking().Where(p => p.CompanyId == companyId && p.PropertyId == propertyId && p.IsActive)
            .OrderBy(p => p.SortOrder).ToListAsync();
        ctx.Policies = await _db.LodgingCancellationPolicies.AsNoTracking().Where(p => p.CompanyId == companyId && p.PropertyId == propertyId && p.IsActive).ToListAsync();
        ctx.Extras = await _db.LodgingExtras.AsNoTracking().Where(e => e.CompanyId == companyId && e.PropertyId == propertyId && e.IsActive).OrderBy(e => e.SortOrder).ToListAsync();

        // ห้องที่ขายได้ = active และไม่ปิดซ่อม (หรือปิดซ่อมแต่กลับมาก่อนวันเช็คอิน)
        var units = await _db.LodgingUnits.AsNoTracking()
            .Where(u => u.CompanyId == companyId && u.RoomType.PropertyId == propertyId && u.IsActive
                && (!u.IsOutOfService || (u.OutOfServiceUntil != null && u.OutOfServiceUntil < from)))
            .GroupBy(u => u.RoomTypeId).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        ctx.UnitsByRoomType = units.ToDictionary(x => x.Key, x => x.N);

        var booked = await _db.LodgingReservationRooms.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.Reservation.PropertyId == propertyId
                && r.Reservation.CheckInDate < to && r.Reservation.CheckOutDate > from
                && (r.Reservation.Status == LodgingReservationStatus.Pending
                    || r.Reservation.Status == LodgingReservationStatus.Confirmed
                    || r.Reservation.Status == LodgingReservationStatus.CheckedIn)
                && (excludeReservationId == null || r.ReservationId != excludeReservationId))
            .Select(r => new { r.RoomTypeId, r.Reservation.CheckInDate, r.Reservation.CheckOutDate, r.Reservation.Status, r.Reservation.HoldExpiresAt })
            .ToListAsync();
        ctx.Booked = booked.Select(b => (b.RoomTypeId, new LodgingAvailability.BookedRange(b.CheckInDate, b.CheckOutDate, 1, b.Status, b.HoldExpiresAt))).ToList();
        return ctx;
    }

    private static void ValidateDates(LodgingProperty prop, DateTime checkIn, DateTime checkOut, bool isStaff)
    {
        if (checkOut.Date <= checkIn.Date) throw new BusinessRuleException("วันเช็คเอาต์ต้องหลังวันเช็คอิน");
        var nights = (int)(checkOut.Date - checkIn.Date).TotalDays;
        if (nights > prop.MaxNights) throw new BusinessRuleException($"จองได้สูงสุด {prop.MaxNights} คืน");
        if (isStaff) return;
        var today = DateTime.UtcNow.AddHours(7).Date;
        if (checkIn.Date < today) throw new BusinessRuleException("วันเช็คอินต้องไม่ย้อนหลัง");
        if ((checkIn.Date - today).TotalDays > prop.MaxAdvanceDays) throw new BusinessRuleException($"จองล่วงหน้าได้ไม่เกิน {prop.MaxAdvanceDays} วัน");
        if (prop.MinAdvanceHours > 0)
        {
            var checkInAt = checkIn.Date.Add(prop.CheckInTime.ToTimeSpan()).AddHours(-7);
            if (checkInAt < DateTime.UtcNow.AddHours(prop.MinAdvanceHours))
                throw new BusinessRuleException($"ต้องจองล่วงหน้าอย่างน้อย {prop.MinAdvanceHours} ชั่วโมงก่อนเวลาเช็คอิน");
        }
    }

    private LodgingRatePlan? PickRatePlan(PricingContext ctx, Guid? ratePlanId, LodgingRoomType rt, DateTime checkIn, int nights)
    {
        var candidates = ctx.RatePlans.Where(p => p.RoomTypeId == null || p.RoomTypeId == rt.Id).ToList();
        if (ratePlanId is Guid id)
            return candidates.FirstOrDefault(p => p.Id == id) ?? throw new BusinessRuleException("แผนราคาที่เลือกใช้กับห้องนี้ไม่ได้");
        return candidates.FirstOrDefault(p => p.IsDefault && PlanApplies(p, checkIn, nights))
            ?? candidates.FirstOrDefault(p => p.AdjustMode == LodgingRateAdjustMode.Base && PlanApplies(p, checkIn, nights));
    }

    private static bool PlanApplies(LodgingRatePlan p, DateTime checkIn, int nights)
    {
        var d = checkIn.Date;
        if (p.ValidFrom is DateTime vf && d < vf.Date) return false;
        if (p.ValidTo is DateTime vt && d > vt.Date) return false;
        if (p.MinNights is int mn && nights < mn) return false;
        if (p.MaxNights is int mx && nights > mx) return false;
        var lead = (int)(d - DateTime.UtcNow.AddHours(7).Date).TotalDays;
        if (p.MinAdvanceDays is int mad && lead < mad) return false;
        if (p.MaxAdvanceDays is int xad && lead > xad) return false;
        if (p.ApplicableDaysMask != 0 && (p.ApplicableDaysMask & LodgingPricingEngine.DayMask(d.DayOfWeek)) == 0) return false;
        return true;
    }

    private static LodgingCancellationPolicy? PolicyFor(PricingContext ctx, LodgingRatePlan? plan)
    {
        if (plan?.CancellationPolicyId is Guid pid) return ctx.Policies.FirstOrDefault(p => p.Id == pid);
        if (ctx.Property.DefaultCancellationPolicyId is Guid did) return ctx.Policies.FirstOrDefault(p => p.Id == did);
        return ctx.Policies.FirstOrDefault(p => p.IsDefault);
    }

    private static List<LodgingNightlyRateDto> NightsDto(IEnumerable<LodgingNightlyRate> n)
        => n.Select(x => new LodgingNightlyRateDto(x.Date, x.Rate, x.SeasonName, x.IsWeekend, x.IsOverride)).ToList();

    // ═══════════════════════════ Public: info / search / quote ═══════════════════════════

    public async Task<Guid?> ResolvePropertyIdForSiteAsync(Guid companyId, Guid siteId)
        => await _db.LodgingProperties.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.SiteId == siteId && p.IsActive)
            .OrderBy(p => p.SortOrder).Select(p => (Guid?)p.Id).FirstOrDefaultAsync();

    public async Task<LodgingPublicInfo?> GetPublicInfoAsync(Guid companyId, Guid siteId)
    {
        var pid = await ResolvePropertyIdForSiteAsync(companyId, siteId);
        if (pid == null) return null;
        var p = await RequirePropertyAsync(companyId, pid.Value);
        var site = await _db.Sites.AsNoTracking().Where(s => s.Id == siteId && s.CompanyId == companyId).Select(s => new { s.DefaultCurrency }).FirstOrDefaultAsync();
        return new LodgingPublicInfo
        {
            PropertyId = p.Id, Name = p.Name, NameEn = p.NameEn, PropertyType = p.PropertyType, Description = p.Description,
            Address = p.Address, Phone = p.Phone, Email = p.Email, LineId = p.LineId, MapUrl = p.MapUrl, StarRating = p.StarRating,
            Images = ParseStrings(p.ImagesJson), Amenities = ParseStrings(p.AmenitiesJson),
            CheckInTime = Time(p.CheckInTime), CheckOutTime = Time(p.CheckOutTime),
            MinNights = p.MinNights, MaxNights = p.MaxNights, MaxAdvanceDays = p.MaxAdvanceDays, MinAdvanceHours = p.MinAdvanceHours,
            ChildMaxAge = p.ChildMaxAge, InfantMaxAge = p.InfantMaxAge, OnlineBookingEnabled = p.OnlineBookingEnabled,
            RequireGuestIdNumber = p.RequireGuestIdNumber, DepositPercent = p.DepositPercent, DepositFixedAmount = p.DepositFixedAmount,
            PricesIncludeVat = p.PricesIncludeVat, VatRate = await EffectiveVatRateAsync(companyId, p),
            ServiceChargePercent = p.ServiceChargePercent, HouseRules = p.HouseRules, ConfirmationMessage = p.ConfirmationMessage,
            PaymentHoldMinutes = p.PaymentHoldMinutes, Currency = site?.DefaultCurrency ?? "THB",
            RoomTypes = await GetRoomTypesAsync(companyId, p.Id),
            // บริการที่ตั้งค่าไม่ครบ (#36) ไม่โชว์ให้แขกเลือก — คิดราคาไม่ได้ · เจ้าของเห็นป้ายเตือนในหน้าตั้งค่า
            Extras = (await GetExtrasAsync(companyId, p.Id)).Where(e => e.ShowOnWebsite && e.ConfigProblem == null).ToList(),
            RatePlans = (await GetRatePlansAsync(companyId, p.Id)).Where(r => r.IsActive).ToList(),
            Policies = (await GetPoliciesAsync(companyId, p.Id)).Where(r => r.IsActive).ToList(),
        };
    }

    public async Task<List<LodgingSearchResult>> SearchAsync(Guid companyId, Guid propertyId, LodgingSearchRequest request)
    {
        await ExpireHoldsAsync(companyId, propertyId);
        var checkIn = ThaiDate.CalendarDateUtc(request.CheckIn); var checkOut = ThaiDate.CalendarDateUtc(request.CheckOut);
        var ctx = await LoadContextAsync(companyId, propertyId, checkIn, checkOut);
        ValidateDates(ctx.Property, checkIn, checkOut, isStaff: false);
        var nights = (int)(checkOut - checkIn).TotalDays;
        var now = DateTime.UtcNow;
        var results = new List<LodgingSearchResult>();
        var guests = Math.Max(1, request.Adults) + Math.Max(0, request.Children);
        foreach (var rt in ctx.RoomTypes)
        {
            var total = ctx.UnitsByRoomType.GetValueOrDefault(rt.Id);
            var available = LodgingAvailability.AvailableRooms(checkIn, checkOut, total, ctx.Property.OverbookingAllowance, ctx.BookedFor(rt.Id), ctx.OverridesFor(rt.Id), now);
            var minNights = LodgingPricingEngine.EffectiveMinNights(checkIn, checkOut, ctx.Property.MinNights, rt.MinNights, ctx.SeasonsFor(rt.Id), ctx.OverridesFor(rt.Id));
            var plan = PickRatePlan(ctx, request.RatePlanId, rt, checkIn, nights);
            var quote = LodgingPricingEngine.QuoteRoom(checkIn, checkOut, Math.Max(1, request.Adults), Math.Max(0, request.Children), 0, ctx.InputFor(rt, plan));
            var r = new LodgingSearchResult
            {
                RoomTypeId = rt.Id, Name = rt.Name, NameEn = rt.NameEn, Description = rt.Description,
                Images = ParseStrings(rt.ImagesJson), Amenities = ParseStrings(rt.AmenitiesJson),
                BedType = rt.BedType, SizeSqm = rt.SizeSqm, ViewType = rt.ViewType,
                StandardOccupancy = rt.StandardOccupancy, MaxAdults = rt.MaxAdults, MaxChildren = rt.MaxChildren, MaxOccupancy = rt.MaxOccupancy,
                AllowExtraBed = rt.AllowExtraBed, PricingMode = rt.PricingMode,
                IncludesBreakfast = rt.IncludesBreakfast || (plan?.IncludesBreakfast ?? false),
                AvailableRooms = available, MinNights = minNights,
                PricePerRoom = quote.Subtotal,
                AverageNightlyRate = nights > 0 ? Math.Round(quote.RoomRate / nights, 2, MidpointRounding.AwayFromZero) : 0,
                Nights = NightsDto(quote.Nights),
            };
            foreach (var p in ctx.RatePlans.Where(p => (p.RoomTypeId == null || p.RoomTypeId == rt.Id) && PlanApplies(p, checkIn, nights)))
            {
                var pq = LodgingPricingEngine.QuoteRoom(checkIn, checkOut, Math.Max(1, request.Adults), Math.Max(0, request.Children), 0, ctx.InputFor(rt, p));
                var pol = PolicyFor(ctx, p);
                r.RatePlans.Add(new LodgingRatePlanQuote(p.Id, p.Name, p.Code, pq.Subtotal, rt.IncludesBreakfast || p.IncludesBreakfast, p.IsRefundable, p.DepositPercent, pol?.Name));
            }
            if (available < request.Rooms) r.UnavailableReason = available == 0 ? "ห้องเต็ม/ปิดขายในช่วงนี้" : $"เหลือเพียง {available} ห้อง";
            else if (nights < minNights) r.UnavailableReason = $"ช่วงนี้ต้องพักอย่างน้อย {minNights} คืน";
            else if (guests > rt.MaxOccupancy) r.UnavailableReason = $"ห้องนี้พักได้สูงสุด {rt.MaxOccupancy} คน";
            results.Add(r);
        }
        return results;
    }

    public async Task<LodgingQuoteResponse> QuoteAsync(Guid companyId, Guid propertyId, LodgingQuoteRequest request)
    {
        var checkIn = ThaiDate.CalendarDateUtc(request.CheckIn); var checkOut = ThaiDate.CalendarDateUtc(request.CheckOut);
        var ctx = await LoadContextAsync(companyId, propertyId, checkIn, checkOut);
        return BuildQuote(ctx, checkIn, checkOut, request.Rooms, request.Extras, request.RatePlanId, isStaff: false, excludeReservationId: null);
    }

    /// <summary>คิดราคาทั้งการจอง — ใช้ร่วมกันทั้ง quote (หน้าเว็บ) · สร้างจอง · เลื่อนวัน</summary>
    private LodgingQuoteResponse BuildQuote(PricingContext ctx, DateTime checkIn, DateTime checkOut,
        List<LodgingQuoteRoomRequest> rooms, List<LodgingQuoteExtraRequest>? extras, Guid? ratePlanId, bool isStaff, Guid? excludeReservationId)
    {
        var res = new LodgingQuoteResponse
        {
            CheckIn = checkIn, CheckOut = checkOut, PricesIncludeVat = ctx.Property.PricesIncludeVat, VatRate = ctx.VatRate,
        };
        try { ValidateDates(ctx.Property, checkIn, checkOut, isStaff); }
        catch (BusinessRuleException ex) { res.Errors.Add(ex.Message); return res; }
        if (rooms == null || rooms.Count == 0) { res.Errors.Add("กรุณาเลือกห้องอย่างน้อย 1 ห้อง"); return res; }

        var nights = (int)(checkOut - checkIn).TotalDays;
        res.Nights = nights;
        var now = DateTime.UtcNow;
        LodgingRatePlan? planUsed = null;
        decimal roomSubtotal = 0;
        var totalGuests = 0;

        foreach (var grp in rooms.GroupBy(r => r.RoomTypeId))
        {
            var rt = ctx.RoomTypes.FirstOrDefault(x => x.Id == grp.Key);
            if (rt == null) { res.Errors.Add("ไม่พบประเภทห้องที่เลือก"); continue; }
            var total = ctx.UnitsByRoomType.GetValueOrDefault(rt.Id);
            var available = LodgingAvailability.AvailableRooms(checkIn, checkOut, total, ctx.Property.OverbookingAllowance, ctx.BookedFor(rt.Id, excludeReservationId), ctx.OverridesFor(rt.Id), now);
            var wanted = grp.Count();
            if (available < wanted) res.Errors.Add($"{rt.Name}: ว่าง {available} ห้อง (ต้องการ {wanted})");
            var minNights = LodgingPricingEngine.EffectiveMinNights(checkIn, checkOut, ctx.Property.MinNights, rt.MinNights, ctx.SeasonsFor(rt.Id), ctx.OverridesFor(rt.Id));
            if (nights < minNights) res.Errors.Add($"{rt.Name}: ช่วงนี้ต้องพักอย่างน้อย {minNights} คืน");

            LodgingRatePlan? plan;
            try { plan = PickRatePlan(ctx, ratePlanId, rt, checkIn, nights); }
            catch (BusinessRuleException ex) { res.Errors.Add(ex.Message); plan = null; }
            planUsed ??= plan;
            var input = ctx.InputFor(rt, plan);
            foreach (var room in grp)
            {
                var adults = Math.Max(1, room.Adults); var children = Math.Max(0, room.Children);
                var beds = rt.AllowExtraBed ? Math.Clamp(room.ExtraBeds, 0, rt.MaxExtraBeds) : 0;
                if (adults > rt.MaxAdults) res.Errors.Add($"{rt.Name}: ผู้ใหญ่สูงสุด {rt.MaxAdults} คน/ห้อง");
                if (children > rt.MaxChildren) res.Errors.Add($"{rt.Name}: เด็กสูงสุด {rt.MaxChildren} คน/ห้อง");
                if (adults + children > rt.MaxOccupancy + beds) res.Errors.Add($"{rt.Name}: พักได้สูงสุด {rt.MaxOccupancy} คน/ห้อง");
                var q = LodgingPricingEngine.QuoteRoom(checkIn, checkOut, adults, children, beds, input);
                res.Rooms.Add(new LodgingQuoteRoomLine
                {
                    RoomTypeId = rt.Id, RoomTypeName = rt.Name, Adults = adults, Children = children, ExtraBeds = beds,
                    RoomRate = q.RoomRate, ExtraGuestCharge = q.ExtraGuestCharge, ExtraBedCharge = q.ExtraBedCharge, Subtotal = q.Subtotal,
                    Nights = NightsDto(q.Nights),
                });
                roomSubtotal += q.Subtotal;
                totalGuests += adults + children;
            }
        }

        decimal extrasTotal = 0;
        foreach (var e in extras ?? new())
        {
            if (e.Quantity <= 0) continue;
            var ex = ctx.Extras.FirstOrDefault(x => x.Id == e.ExtraId);
            if (ex == null) { res.Errors.Add("ไม่พบบริการเสริมที่เลือก"); continue; }
            // #36 — วิธีคิดราคาที่ไม่มีในระบบ = ปฏิเสธการคิดราคา (เดิมตกเป็น "ครั้งเดียว" เงียบ ๆ = เก็บเงินขาด)
            if (LodgingPricingEngine.ExtraConfigProblem(ex.PriceMode, ex.Category) is string extraProblem)
            { res.Errors.Add($"{ex.Name}: {extraProblem}"); continue; }
            var qty = ex.MaxQuantity is int mq ? Math.Min(mq, e.Quantity) : e.Quantity;
            var t = LodgingPricingEngine.ExtraTotal(new LodgingExtraInput(ex.Name, ex.PriceMode, ex.Price, qty), nights, totalGuests);
            res.Extras.Add(new LodgingQuoteExtraLine { ExtraId = ex.Id, Name = ex.Name, PriceMode = ex.PriceMode, UnitPrice = ex.Price, Quantity = qty, Total = t });
            extrasTotal += t;
        }

        var depositPct = planUsed?.DepositPercent ?? ctx.Property.DepositPercent;
        var totals = LodgingPricingEngine.Totals(roomSubtotal, extrasTotal, 0m, ctx.Property.ServiceChargePercent, ctx.VatRate,
            ctx.Property.PricesIncludeVat, depositPct, planUsed?.DepositPercent != null ? null : ctx.Property.DepositFixedAmount,
            ctx.Property.DepositMinAmount, ctx.Property.DepositMaxAmount);
        if (ctx.Property.ConfirmWithoutDeposit) totals = totals with { DepositRequired = 0m };

        res.RatePlanId = planUsed?.Id; res.RatePlanName = planUsed?.Name;
        res.RoomSubtotal = totals.RoomSubtotal; res.ExtrasTotal = totals.ExtrasTotal; res.DiscountAmount = totals.DiscountAmount;
        res.ServiceChargeAmount = totals.ServiceChargeAmount; res.VatAmount = totals.VatAmount; res.TotalAmount = totals.TotalAmount;
        res.DepositRequired = totals.DepositRequired;
        var policy = PolicyFor(ctx, planUsed);
        if (policy != null)
        {
            res.CancellationPolicyId = policy.Id; res.CancellationPolicyName = policy.Name;
            res.NonRefundable = policy.NonRefundable || (planUsed != null && !planUsed.IsRefundable);
            res.CancellationRules = LodgingPricingEngine.ParseRules(policy.RulesJson).Select(r => new LodgingCancellationRuleDto(r.DaysBefore, r.PenaltyPercent)).ToList();
        }
        else if (planUsed != null && !planUsed.IsRefundable) res.NonRefundable = true;
        return res;
    }

    // ═══════════════════════════ Create reservation ═══════════════════════════

    public async Task<LodgingReservationResponse> CreateReservationAsync(Guid companyId, Guid propertyId,
        LodgingCreateReservationRequest request, LodgingReservationSource source, string actor, Guid? siteId = null)
    {
        var isStaff = source != LodgingReservationSource.Web;
        if (string.IsNullOrWhiteSpace(request.GuestName)) throw new BusinessRuleException("กรุณาระบุชื่อผู้เข้าพัก");
        if (!isStaff && string.IsNullOrWhiteSpace(request.GuestPhone) && string.IsNullOrWhiteSpace(request.GuestEmail))
            throw new BusinessRuleException("กรุณาระบุเบอร์โทรหรืออีเมลสำหรับติดต่อ");
        if (!string.IsNullOrWhiteSpace(request.GuestTaxId) && !ThaiTaxId.IsValid(request.GuestTaxId))
            throw new BusinessRuleException("เลขประจำตัวผู้เสียภาษีไม่ถูกต้อง (13 หลัก + checksum)", "RD-86/4");

        await ExpireHoldsAsync(companyId, propertyId);
        var checkIn = ThaiDate.CalendarDateUtc(request.CheckIn); var checkOut = ThaiDate.CalendarDateUtc(request.CheckOut);
        var ctx = await LoadContextAsync(companyId, propertyId, checkIn, checkOut);
        if (!isStaff && !ctx.Property.OnlineBookingEnabled) throw new BusinessRuleException("ที่พักนี้ยังไม่เปิดรับจองออนไลน์ — กรุณาติดต่อโดยตรง");
        if (!isStaff && ctx.Property.RequireGuestIdNumber && string.IsNullOrWhiteSpace(request.GuestIdNumber))
            throw new BusinessRuleException("ที่พักนี้ต้องการเลขบัตรประชาชน/พาสปอร์ตของผู้เข้าพัก");
        if (!isStaff && !string.IsNullOrWhiteSpace(ctx.Property.HouseRules) && !request.AcceptHouseRules)
            throw new BusinessRuleException("กรุณายอมรับกติกาที่พักก่อนจอง");

        var quote = BuildQuote(ctx, checkIn, checkOut, request.Rooms, request.Extras, request.RatePlanId, isStaff, null);
        if (quote.Errors.Count > 0) throw new BusinessRuleException(string.Join(" · ", quote.Errors));

        var contactId = request.ContactId ?? await FindOrCreateContactAsync(companyId, request, actor);
        var prop = ctx.Property;
        var immediateConfirm = prop.ConfirmWithoutDeposit || quote.DepositRequired <= 0 || (isStaff && request.ConfirmImmediately);
        var policy = quote.CancellationPolicyId is Guid polId ? ctx.Policies.FirstOrDefault(p => p.Id == polId) : null;

        var res = new LodgingReservation
        {
            CompanyId = companyId, PropertyId = propertyId, SiteId = siteId ?? prop.SiteId,
            PublicToken = NewToken(), Status = immediateConfirm ? LodgingReservationStatus.Confirmed : LodgingReservationStatus.Pending,
            Source = source, SourceReference = request.SourceReference,
            CheckInDate = checkIn, CheckOutDate = checkOut, Nights = quote.Nights,
            Adults = quote.Rooms.Sum(r => r.Adults), Children = quote.Rooms.Sum(r => r.Children), Infants = Math.Max(0, request.Infants),
            ArrivalTime = request.ArrivalTime, SpecialRequests = request.SpecialRequests,
            ContactId = contactId, GuestName = request.GuestName.Trim(), GuestEmail = request.GuestEmail?.Trim(), GuestPhone = request.GuestPhone?.Trim(),
            GuestNationality = request.GuestNationality, GuestIdNumber = request.GuestIdNumber?.Trim(), GuestAddress = request.GuestAddress,
            GuestTaxId = string.IsNullOrWhiteSpace(request.GuestTaxId) ? null : ThaiTaxId.Normalize(request.GuestTaxId),
            GuestCompanyName = request.GuestCompanyName,
            RatePlanId = quote.RatePlanId, CancellationPolicyId = quote.CancellationPolicyId,
            CancellationPolicySnapshotJson = policy == null ? (quote.NonRefundable ? J(new { nonRefundable = true, rules = Array.Empty<object>() }) : null)
                : J(new { nonRefundable = quote.NonRefundable, name = policy.Name, rules = quote.CancellationRules.Select(r => new { daysBefore = r.DaysBefore, penaltyPercent = r.PenaltyPercent }) }),
            PromoCode = request.PromoCode,
            RoomSubtotal = quote.RoomSubtotal, ExtrasTotal = quote.ExtrasTotal, DiscountAmount = quote.DiscountAmount,
            ServiceChargeAmount = quote.ServiceChargeAmount, VatAmount = quote.VatAmount, TotalAmount = quote.TotalAmount,
            Currency = "THB", PriceBreakdownJson = J(quote),
            DepositRequired = quote.DepositRequired,
            HoldExpiresAt = immediateConfirm ? null : DateTime.UtcNow.AddMinutes(prop.PaymentHoldMinutes),
            ConfirmedAt = immediateConfirm ? DateTime.UtcNow : null, ConfirmedBy = immediateConfirm ? actor : null,
            InternalNotes = request.InternalNotes, CreatedBy = actor,
        };
        foreach (var r in quote.Rooms)
            res.Rooms.Add(new LodgingReservationRoom
            {
                CompanyId = companyId, RoomTypeId = r.RoomTypeId, RoomTypeName = r.RoomTypeName, Adults = r.Adults, Children = r.Children,
                ExtraBeds = r.ExtraBeds, NightlyRatesJson = J(r.Nights), Subtotal = r.Subtotal, CreatedBy = actor,
            });
        foreach (var e in quote.Extras)
        {
            var ex = ctx.Extras.FirstOrDefault(x => x.Id == e.ExtraId);
            res.Extras.Add(new LodgingReservationExtra
            {
                CompanyId = companyId, ExtraId = e.ExtraId, Name = e.Name, PriceMode = e.PriceMode, UnitPrice = e.UnitPrice,
                Quantity = e.Quantity, Total = e.Total, ProductId = ex?.ProductId, CreatedBy = actor,
            });
        }

        // เลขจอง: RES-{code}-{yyMM}-{####} ต่อที่พัก — advisory lock ข้าม instance (FNV-1a key)
        // ห้ามใช้ HashCode.Combine (สุ่มต่อ process — บทเรียนใน CLAUDE.md)
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            var lockKey = AdvisoryLockKey.For(companyId, ResLockScope, propertyId.ToString("N"));
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);
            var prefix = $"RES-{prop.Code}-{DateTime.UtcNow.AddHours(7):yyMM}-";
            // integer-max ผ่านตัวกลาง — เดิมเรียงแบบ**ข้อความ** ⇒ โรงแรมที่มี
            // การจองเกิน 9,999 ครั้งในเดือนเดียว "RES-…-9999" ยังชนะ "…-10000"
            // ⇒ เลขวนกลับไปทับใบเดิม (ล็อกมีอยู่แล้วจึงคงไว้ ไม่ล็อกซ้อน)
            var seqSuffixes = await _db.LodgingReservations.IgnoreQueryFilters()
                .Where(x => x.CompanyId == companyId && x.PropertyId == propertyId
                            && x.ReservationNumber.StartsWith(prefix))
                .OrderByDescending(x => x.CreatedAt)
                .Take(Accounting.Helpers.SequenceNumber.ScanWindow)
                .Select(x => x.ReservationNumber.Substring(prefix.Length))
                .ToListAsync();
            res.ReservationNumber = Accounting.Helpers.SequenceNumber.Format(
                prefix, Accounting.Helpers.SequenceNumber.NextSequence(seqSuffixes));
            _db.LodgingReservations.Add(res);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        _db.AuditLogs.Add(Audit(companyId, AuditAction.Create, res, new { action = "CreateReservation", source = source.ToString(), by = actor, total = res.TotalAmount, deposit = res.DepositRequired }));
        await _db.SaveChangesAsync();
        _logger.LogInformation("Lodging reservation {No} created ({Status}, total {Total}, deposit {Dep})", res.ReservationNumber, res.Status, res.TotalAmount, res.DepositRequired);

        await TryNotifyAsync(companyId, res.Id, "created");
        return (await GetReservationAsync(companyId, res.Id))!;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private async Task<Guid> FindOrCreateContactAsync(Guid companyId, LodgingCreateReservationRequest r, string actor)
    {
        var taxId = string.IsNullOrWhiteSpace(r.GuestTaxId) ? null : ThaiTaxId.Normalize(r.GuestTaxId);
        var email = r.GuestEmail?.Trim(); var phone = r.GuestPhone?.Trim();
        Contact? c = null;
        // รอบ 193 ข้อ 20: คีย์เลขภาษี + สาขา (Helpers/ContactTaxBranchKey) — ฟอร์มจองไม่มีช่องสาขา และเส้นนี้สร้างผู้ติดต่อ
        // เป็น "00000" เสมอ ⇒ ความหมายเดิม = สำนักงานใหญ่ จึงส่ง "00000" (ไม่หยิบแถวสาขาอื่นของเลขเดียวกัน)
        var taxKey = default(Accounting.Helpers.ContactKeyMatch);
        if (taxId != null)
        {
            taxKey = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(_db.Contacts, companyId, taxId, Accounting.Helpers.TaxBranchCode.HeadOffice);
            if (taxKey.ContactId is Guid keyId)
                c = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == keyId && x.CompanyId == companyId);
        }
        // รอบ 193 (ฝ่ายค้าน C3): เดิมถอยไปจับด้วยอีเมล/เบอร์เสมอ ⇒ แขกนิติบุคคลที่เลขมีอยู่แล้วแต่คนละสาขา หรือเลขใหม่ที่อีเมลตรงกับ
        // ผู้ติดต่อเลขอื่น ได้แถวของคนอื่นไปออกใบกำกับ (§86/4 ผู้ซื้อผิดตัว) — ตัดสินขอบเขตด้วยตัวกลางตัวเดียว
        var soft = Accounting.Helpers.ContactTaxBranchKey.SoftMatchScope(taxId, taxKey);
        // แถว "ลูกค้าทั่วไป" (walk-in) ไม่ใช่ตัวแขก — ห้ามถอยไปจับ (ใบกำกับจะพิมพ์ชื่อกลาง · ทีม C3: แถว walk-in ไม่รับเลข)
        var softScope = soft == Accounting.Helpers.ContactSoftMatch.RowsWithoutTaxId
            ? _db.Contacts.Where(x => x.CompanyId == companyId && !x.IsWalkInCustomer && (x.TaxId == null || x.TaxId == ""))
            : _db.Contacts.Where(x => x.CompanyId == companyId && !x.IsWalkInCustomer);
        // รอบ 193 (ฝ่ายค้าน C-7): แขกที่ส่งเลขภาษี/ชื่อบริษัท ห้ามได้แถวบุคคลธรรมดา/แถวชื่ออื่นที่อีเมลหรือเบอร์บังเอิญตรง
        // (ใบกำกับจะออกในชื่อบุคคลโดยไม่มีเลขผู้ซื้อ §86/4) — ตัวตัดสินตัวเดียว Helpers/LodgingGuestContact
        if (c == null && soft != Accounting.Helpers.ContactSoftMatch.None)
        {
            // ชนิดการจับจริงของแต่ละผู้สมัคร (อีเมลก่อน แล้วเบอร์) — ส่งเข้า AdoptTaxId ให้ตัวกลางตัดสินว่าเติมเลขได้ไหม
            var candidates = new List<(Contact Row, Accounting.Helpers.ContactMatchKind Kind)>();
            if (!string.IsNullOrEmpty(email))
                candidates.AddRange((await softScope.Where(x => x.Email == email).OrderBy(x => x.CreatedAt).Take(20).ToListAsync())
                    .Select(x => (x, Accounting.Helpers.ContactMatchKind.Email)));
            if (!string.IsNullOrEmpty(phone))
                candidates.AddRange((await softScope.Where(x => x.Phone == phone).OrderBy(x => x.CreatedAt).Take(20).ToListAsync())
                    .Select(x => (x, Accounting.Helpers.ContactMatchKind.Phone)));
            var pick = candidates.FirstOrDefault(x => Accounting.Helpers.LodgingGuestContact.SoftCandidateAcceptable(
                taxId, r.GuestCompanyName, x.Row.Name, x.Row.ContactType));
            // แถวนิติบุคคลชื่อตรงที่ยังไม่มีเลข — "จับได้แล้วต้องเติมเลข" ผ่านตัวกลางตัวเดียว (ContactTaxBranchKey.AdoptTaxId รูปใหม่ของ
            // ทีม C3: ไม่ทับเลขของนิติบุคคลอื่น · ชนิด/สาขาผ่าน ContactTypeResolver · Reject = ห้ามใช้แถว ⇒ สร้างแถวใหม่) ·
            // ฟอร์มจองไม่มีช่องสาขา = สำนักงานใหญ่
            if (pick.Row != null)
            {
                switch (Accounting.Helpers.ContactTaxBranchKey.AdoptTaxId(pick.Row, taxId, Accounting.Helpers.TaxBranchCode.HeadOffice, pick.Kind))
                {
                    case Accounting.Helpers.ContactAdoptOutcome.Adopted:
                        if (string.IsNullOrWhiteSpace(pick.Row.Address) && !string.IsNullOrWhiteSpace(r.GuestAddress)) pick.Row.Address = r.GuestAddress;
                        pick.Row.UpdatedBy = actor;
                        await _db.SaveChangesAsync();
                        c = pick.Row;
                        break;
                    case Accounting.Helpers.ContactAdoptOutcome.Keep:
                        c = pick.Row;
                        break;
                    case Accounting.Helpers.ContactAdoptOutcome.Reject:   // ห้ามใช้แถวนี้ ⇒ สร้างแถวใหม่ด้านล่าง
                    default:
                        c = null;
                        break;
                }
            }
        }
        if (c != null) return c.Id;
        var isCompany = !string.IsNullOrWhiteSpace(r.GuestCompanyName);
        c = new Contact
        {
            CompanyId = companyId, Name = isCompany ? r.GuestCompanyName!.Trim() : r.GuestName.Trim(),
            ContactPerson = isCompany ? r.GuestName.Trim() : null,
            TaxId = taxId, BranchCode = taxId != null ? "00000" : null,
            ContactType = isCompany ? ContactType.JuristicPerson : ContactType.Individual,
            IsCustomer = true, Email = email, Phone = phone, Address = r.GuestAddress, CreatedBy = actor,
        };
        // ทีม C3 (ฝ่ายค้านรอบสี่ P4-5): เลขที่แขกกรอกไม่ผ่าน checksum/ศูนย์ล้วน ⇒ เก็บตามที่กรอก (หลักฐาน) แต่ติดป้าย [TAXID-CHECKSUM]
        // บนผู้ติดต่อ ให้ตรวจก่อนออกใบกำกับ (ตัวกลางตัวเดียวกับทุกทางเข้าที่สร้างผู้ติดต่อจากเลขภายนอก)
        Accounting.Helpers.ContactTaxBranchKey.StampTaxIdWarning(c);
        _db.Contacts.Add(c);
        await _db.SaveChangesAsync();
        return c.Id;
    }

    private static AuditLog Audit(Guid companyId, AuditAction action, LodgingReservation res, object payload) => new()
    {
        CompanyId = companyId, Action = action, EntityType = "LodgingReservation", EntityId = res.Id.ToString(),
        NewValues = J(new { reservationNumber = res.ReservationNumber, status = res.Status.ToString(), detail = payload }),
    };

    /// <summary>ปล่อยห้องของการจองที่หมดเวลาชำระมัดจำ — เรียกก่อนค้นหา/สร้างจอง/แสดงรายการ
    /// (ไม่มี background job: กติกา "ไม่มี state ข้าม request" — engine เองก็ไม่นับ hold ที่หมดอายุอยู่แล้ว
    /// ตรงนี้แค่ทำให้สถานะในตารางตรงกับความจริงที่ผู้ใช้เห็น)</summary>
    private async Task ExpireHoldsAsync(Guid companyId, Guid propertyId)
    {
        var now = DateTime.UtcNow;
        var expired = await _db.LodgingReservations
            .Where(r => r.CompanyId == companyId && r.PropertyId == propertyId && r.Status == LodgingReservationStatus.Pending
                && r.HoldExpiresAt != null && r.HoldExpiresAt < now && r.DepositPaid == 0 && r.SlipUploadedAt == null)
            .ToListAsync();
        if (expired.Count == 0) return;
        foreach (var r in expired)
        {
            r.Status = LodgingReservationStatus.Cancelled; r.CancelledAt = now;
            r.CancellationReason = "หมดเวลาชำระมัดจำ (ระบบยกเลิกอัตโนมัติ)";
            _db.AuditLogs.Add(Audit(companyId, AuditAction.Update, r, new { action = "AutoExpireHold" }));
        }
        await _db.SaveChangesAsync();
    }
}
