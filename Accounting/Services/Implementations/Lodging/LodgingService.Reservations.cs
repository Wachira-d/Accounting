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

    // ═══════════════════════════ ล็อกต่อที่พัก (O-P0-1 รอบ 202) ═══════════════════════════

    /// <summary>คีย์ล็อก "การใช้ห้องของที่พักนี้" — คีย์เดียวกับเลขจองเดิม (FNV-1a ผ่าน AdvisoryLockKey · คงที่ข้ามเครื่อง)</summary>
    private static long PropertyLockKey(Guid companyId, Guid propertyId) => AdvisoryLockKey.For(companyId, ResLockScope, propertyId.ToString("N"));

    /// <summary>
    /// รัน <paramref name="work"/> ใต้ <c>pg_advisory_xact_lock</c> ของที่พัก — <b>ทุกเส้นที่ตัดสิน "ห้องว่างพอไหม" แล้วเขียนผล</b> ต้องอยู่ในนี้
    /// (สร้างจอง · ยืนยัน (จองห้องคืนก่อนออกใบมัดจำ) · เลื่อนวัน · จัดห้อง · เช็คอิน · ต่อเวลาถือห้องตอนจ่ายออนไลน์ · ยกเลิกอัตโนมัติ)
    ///
    /// <para><b>ลำดับล็อก</b>: ล็อกที่พัก → (ในธุรกรรมนี้ไม่มีล็อกเอกสาร/JE) → audit ปิดผนึกตอน commit (ล็อกบริษัท · ท้ายสุด) ·
    /// เส้นที่ออกเอกสาร (ยืนยัน+มัดจำ · เช็คเอาต์) <b>ไม่</b>ออกเอกสารใต้ล็อกนี้ — เส้นเอกสารเปิดธุรกรรมของตัวเอง (ซ้อนไม่ได้) ⇒ ยืนยันจึง "จองห้องไว้"
    /// ใต้ล็อกก่อน แล้วออกใบมัดจำหลังปลดล็อก (<see cref="ClaimInventoryForConfirmAsync"/>)</para>
    ///
    /// <para>มีธุรกรรมของผู้เรียกอยู่แล้ว ⇒ ล็อกในธุรกรรมนั้น (ปลดตอนผู้เรียก commit) ไม่เปิดซ้อน · ไม่มี ⇒ เปิดเอง commit เอง ·
    /// โยน = rollback ทั้งก้อน (ไม่มีการจอง/ผู้ติดต่อครึ่ง ๆ)</para>
    /// </summary>
    private async Task<T> WithPropertyLockAsync<T>(Guid companyId, Guid propertyId, Func<Task<T>> work)
    {
        if (_db.Database.CurrentTransaction != null)
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", PropertyLockKey(companyId, propertyId));
            return await work();
        }
        await using var tx = await _db.Database.BeginTransactionAsync();
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", PropertyLockKey(companyId, propertyId));
        var result = await work();
        await tx.CommitAsync();
        return result;
    }

    /// <summary>ตรวจห้องว่างของการจองนี้ (ไม่นับตัวเอง) จาก context ที่โหลดใต้ล็อก — ไม่พอ = โยน <c>LODGING-OVERSOLD</c></summary>
    private static void EnsureRoomsAvailable(PricingContext ctx, LodgingReservation r, string whenText)
    {
        foreach (var g in r.Rooms.GroupBy(x => x.RoomTypeId))
        {
            var avail = LodgingAvailability.AvailableRooms(r.CheckInDate, r.CheckOutDate, ctx.UnitsByRoomType.GetValueOrDefault(g.Key),
                ctx.Property.OverbookingAllowance, ctx.BookedFor(g.Key), ctx.OverridesFor(g.Key), DateTime.UtcNow);
            if (avail < g.Count())
                throw new BusinessRuleException($"{g.First().RoomTypeName} ว่างไม่พอแล้ว ({avail}/{g.Count()}) — {whenText}", "LODGING-OVERSOLD");
        }
    }

    /// <summary>
    /// ยืนยันการจองที่ยังรอชำระ: <b>ตรวจห้องว่าง + จองห้องไว้</b> ใต้ล็อกที่พัก (ธุรกรรมสั้น) ก่อนออกใบมัดจำ — hold ถูกต่อเป็นอย่างน้อย
    /// <see cref="LodgingHoldRule.ConfirmClaimWindow"/> ⇒ ระหว่างออกใบมัดจำ (นอกล็อก) ไม่มีใครจองห้องนี้ซ้อนได้ · ยืนยันสำเร็จล้าง hold เอง ·
    /// ออกใบล้ม = hold หมดเองตามเวลา (ทิศปลอดภัย) · อ่านสถานะ/hold ใหม่จากฐานหลังได้ล็อก (ตัวยกเลิกอัตโนมัติอาจปิดใบไปก่อนแล้ว)
    /// </summary>
    private async Task ClaimInventoryForConfirmAsync(Guid companyId, LodgingReservation r)
    {
        await WithPropertyLockAsync(companyId, r.PropertyId, async () =>
        {
            await _db.Entry(r).ReloadAsync();
            if (r.Status != LodgingReservationStatus.Pending)
                throw new BusinessRuleException(
                    $"การจองเปลี่ยนสถานะเป็น {StatusTh(r.Status)} ระหว่างดำเนินการ — เปิดการจองดูใหม่", "LODGING-STATE-CHANGED");
            var ctx = await LoadContextAsync(companyId, r.PropertyId, r.CheckInDate, r.CheckOutDate, excludeReservationId: r.Id);
            EnsureRoomsAvailable(ctx, r, "ห้องถูกจองไปหลังหมดเวลาถือ");
            r.HoldExpiresAt = LodgingHoldRule.ExtendHold(r.HoldExpiresAt, DateTime.UtcNow + LodgingHoldRule.ConfirmClaimWindow);
            await _db.SaveChangesAsync();
            return true;
        });
    }

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

        // รอบ 202 (คำตัดสินข้อ 119): แขกที่เช็คอินค้างหลังวันออกกันห้องคืนนี้ต่อ ⇒ ดึงแถว CheckedIn ที่วันออกผ่านไปแล้วมาด้วย (superset)
        // แล้วให้ LodgingHoldRule.EffectiveCheckOut ตัดสินวันออกที่ใช้นับ · ธงสลิป/มัดจำ/เงินออนไลน์ค้าง ⇒ Pending ยังกันห้อง (O-P1-4/5)
        var todayThai = DateTime.UtcNow.AddHours(7).Date;
        var booked = await _db.LodgingReservationRooms.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.Reservation.PropertyId == propertyId
                && r.Reservation.CheckInDate < to
                && (r.Reservation.CheckOutDate > from
                    || (r.Reservation.Status == LodgingReservationStatus.CheckedIn && from <= todayThai))
                && (r.Reservation.Status == LodgingReservationStatus.Pending
                    || r.Reservation.Status == LodgingReservationStatus.Confirmed
                    || r.Reservation.Status == LodgingReservationStatus.CheckedIn)
                && (excludeReservationId == null || r.ReservationId != excludeReservationId))
            .Select(r => new
            {
                r.RoomTypeId, r.Reservation.CheckInDate, r.Reservation.CheckOutDate, r.Reservation.Status, r.Reservation.HoldExpiresAt,
                r.Reservation.DepositPaid, r.Reservation.SlipUploadedAt, r.Reservation.PaymentProblemAt, r.Reservation.CheckedOutAt,
            })
            .ToListAsync();
        ctx.Booked = booked.Select(b => (b.RoomTypeId, new LodgingAvailability.BookedRange(
            b.CheckInDate, LodgingHoldRule.EffectiveCheckOut(b.Status, b.CheckOutDate, todayThai, b.CheckedOutAt), 1, b.Status, b.HoldExpiresAt,
            b.DepositPaid, b.SlipUploadedAt != null, b.PaymentProblemAt != null))).ToList();
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

    /// <summary>แผนราคาของประเภทห้องนี้ — id ที่ผู้เรียกส่งมาเอง: แขกต้องผ่านเงื่อนไขของแผน (O-P1-3 · รอบ 202) · พนักงานเลือกนอกเงื่อนไขได้ (ตั้งใจ)
    /// · ไม่ระบุ = แผนตั้งต้นที่ใช้ได้ → แผนราคาฐานที่ใช้ได้</summary>
    private LodgingRatePlan? PickRatePlan(PricingContext ctx, Guid? ratePlanId, LodgingRoomType rt, DateTime checkIn, int nights, bool isStaff)
    {
        var candidates = ctx.RatePlans.Where(p => p.RoomTypeId == null || p.RoomTypeId == rt.Id).ToList();
        if (ratePlanId is Guid id)
        {
            var chosen = candidates.FirstOrDefault(p => p.Id == id) ?? throw new BusinessRuleException("แผนราคาที่เลือกใช้กับห้องนี้ไม่ได้");
            if (LodgingBookingGuards.ExplicitRatePlanProblem(isStaff, PlanApplies(chosen, checkIn, nights), chosen.Name) is string planProblem)
                throw new BusinessRuleException(planProblem, "LODGING-RATEPLAN-NOT-APPLICABLE");
            return chosen;
        }
        return candidates.FirstOrDefault(p => p.IsDefault && PlanApplies(p, checkIn, nights))
            ?? candidates.FirstOrDefault(p => p.AdjustMode == LodgingRateAdjustMode.Base && PlanApplies(p, checkIn, nights));
    }

    private static bool PlanApplies(LodgingRatePlan p, DateTime checkIn, int nights)
        => LodgingBookingGuards.RatePlanApplies(p.ValidFrom, p.ValidTo, p.MinNights, p.MaxNights, p.MinAdvanceDays, p.MaxAdvanceDays,
            p.ApplicableDaysMask, checkIn, nights, DateTime.UtcNow.AddHours(7).Date);

    private static LodgingCancellationPolicy? PolicyFor(PricingContext ctx, LodgingRatePlan? plan)
    {
        if (plan?.CancellationPolicyId is Guid pid) return ctx.Policies.FirstOrDefault(p => p.Id == pid);
        if (ctx.Property.DefaultCancellationPolicyId is Guid did) return ctx.Policies.FirstOrDefault(p => p.Id == did);
        return ctx.Policies.FirstOrDefault(p => p.IsDefault);
    }

    /// <summary>ราคาคนเสริมต่อคนต่อคืนที่การจองนี้จ่ายไว้ ต่อประเภทห้อง (ย้อนจาก snapshot ราคา <c>PriceBreakdownJson</c>) — ฝ่ายค้านรอบ 202 P2-4:
    /// เลื่อนวัน/ขยายคืนของพนักงานคงราคาเดิมเมื่อที่พักปิด/เปลี่ยนเตียงเสริมภายหลัง · อ่านไม่ได้ = ไม่มีคีย์ (ผู้เรียกบล็อกพร้อมทางไปต่อ)</summary>
    private static Dictionary<Guid, decimal> KeptExtraBedPrices(LodgingReservation r)
    {
        var kept = new Dictionary<Guid, decimal>();
        if (string.IsNullOrWhiteSpace(r.PriceBreakdownJson)) return kept;
        LodgingQuoteResponse? snap;
        try { snap = System.Text.Json.JsonSerializer.Deserialize<LodgingQuoteResponse>(r.PriceBreakdownJson, JsonOpts); }
        catch (System.Text.Json.JsonException) { return kept; }   // snapshot เสีย = ไม่มีราคาเดิม (ผู้เรียกบล็อกพร้อมข้อความ — ไม่เดาราคา)
        if (snap == null) return kept;
        foreach (var line in snap.Rooms.Where(x => x.ExtraBeds > 0))
            if (!kept.ContainsKey(line.RoomTypeId)
                && LodgingOccupancy.PerPersonNightPrice(line.ExtraBedCharge, line.ExtraBeds, snap.Nights) is decimal p)
                kept[line.RoomTypeId] = p;
        return kept;
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
            // รอบ 200 (F-05): endpoint สาธารณะ — ตัดรายการห้อง (เลขห้อง/หมายเหตุภายใน/สถานะแม่บ้าน) + ประเภทที่ปิดขาย
            RoomTypes = Accounting.Helpers.LodgingPublicProjection.RoomTypes(await GetRoomTypesAsync(companyId, p.Id)),
            // บริการที่ตั้งค่าไม่ครบ (#36) ไม่โชว์ให้แขกเลือก — คิดราคาไม่ได้ · เจ้าของเห็นป้ายเตือนในหน้าตั้งค่า
            Extras = (await GetExtrasAsync(companyId, p.Id)).Where(e => e.ShowOnWebsite && e.ConfigProblem == null).ToList(),
            RatePlans = (await GetRatePlansAsync(companyId, p.Id)).Where(r => r.IsActive).ToList(),
            Policies = (await GetPoliciesAsync(companyId, p.Id)).Where(r => r.IsActive).ToList(),
        };
    }

    public async Task<List<LodgingSearchResult>> SearchAsync(Guid companyId, Guid propertyId, LodgingSearchRequest request, bool isStaff = false)
    {
        // P2 รอบ 202: โค้ดส่วนลดไม่มีเครื่องคิด ⇒ ปฏิเสธพร้อมทางไปต่อ (เดิมรับแล้วคิด 0 เงียบ)
        if (LodgingBookingGuards.PromoCodeProblem(request.PromoCode) is string promoProblem) throw new BusinessRuleException(promoProblem, "LODGING-PROMO-UNSUPPORTED");
        await ExpireHoldsAsync(companyId, propertyId);
        var checkIn = ThaiDate.CalendarDateUtc(request.CheckIn); var checkOut = ThaiDate.CalendarDateUtc(request.CheckOut);
        var ctx = await LoadContextAsync(companyId, propertyId, checkIn, checkOut);
        ValidateDates(ctx.Property, checkIn, checkOut, isStaff);
        var nights = (int)(checkOut - checkIn).TotalDays;
        var now = DateTime.UtcNow;
        var results = new List<LodgingSearchResult>();
        // ฝ่ายค้าน P1-3 (รอบ 202 LW): ช่องค้นหาส่งยอดรวมทุกห้อง ⇒ ด่านความจุ/ราคาต่อห้องใช้ "ต่อห้อง" (ปัดขึ้น) — เดิมเทียบยอดรวมกับเพดานต่อห้อง
        var adultsPerRoom = LodgingSearchGuests.PerRoom(request.Adults, request.Rooms, 1);
        var childrenPerRoom = LodgingSearchGuests.PerRoom(request.Children, request.Rooms, 0);
        foreach (var rt in ctx.RoomTypes)
        {
            var total = ctx.UnitsByRoomType.GetValueOrDefault(rt.Id);
            var available = LodgingAvailability.AvailableRooms(checkIn, checkOut, total, ctx.Property.OverbookingAllowance, ctx.BookedFor(rt.Id), ctx.OverridesFor(rt.Id), now);
            var minNights = LodgingPricingEngine.EffectiveMinNights(checkIn, checkOut, ctx.Property.MinNights, rt.MinNights, ctx.SeasonsFor(rt.Id), ctx.OverridesFor(rt.Id));
            var plan = PickRatePlan(ctx, request.RatePlanId, rt, checkIn, nights, isStaff);
            var quote = LodgingPricingEngine.QuoteRoom(checkIn, checkOut, adultsPerRoom, childrenPerRoom, 0, ctx.InputFor(rt, plan));
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
                var pq = LodgingPricingEngine.QuoteRoom(checkIn, checkOut, adultsPerRoom, childrenPerRoom, 0, ctx.InputFor(rt, p));
                var pol = PolicyFor(ctx, p);
                r.RatePlans.Add(new LodgingRatePlanQuote(p.Id, p.Name, p.Code, pq.Subtotal, rt.IncludesBreakfast || p.IncludesBreakfast, p.IsRefundable, p.DepositPercent, pol?.Name));
            }
            if (available < request.Rooms) r.UnavailableReason = available == 0 ? "ห้องเต็ม/ปิดขายในช่วงนี้" : $"เหลือเพียง {available} ห้อง";
            else if (nights < minNights) r.UnavailableReason = $"ช่วงนี้ต้องพักอย่างน้อย {minNights} คืน";
            // คำตัดสินข้อ 124: ความจุนับเฉพาะผู้ใหญ่ (รวมคนเสริมที่ซื้อได้) · เด็ก/ทารกไม่นับ — ตัวตัดสิน LodgingOccupancy
            else if (adultsPerRoom > LodgingOccupancy.MaxAdultsWithExtras(rt.MaxAdults, rt.AllowExtraBed, rt.MaxExtraBeds, rt.ExtraBedPrice))
                r.UnavailableReason = $"ห้องนี้รับผู้ใหญ่ได้สูงสุด {LodgingOccupancy.MaxAdultsWithExtras(rt.MaxAdults, rt.AllowExtraBed, rt.MaxExtraBeds, rt.ExtraBedPrice)} คน/ห้อง (รวมคนเสริม)";
            results.Add(r);
        }
        return results;
    }

    /// <summary>ราคาก่อนจอง — <paramref name="isStaff"/> ต้องตรงกับเส้นสร้างจองของผู้เรียก (P2 รอบ 202: เดิม quote พนักงานใช้ด่านของแขกเสมอ ⇒
    /// หน้าพนักงานเห็น "วันเช็คอินย้อนหลัง/แผนใช้ไม่ได้" ทั้งที่กดจองจริงผ่าน)</summary>
    public async Task<LodgingQuoteResponse> QuoteAsync(Guid companyId, Guid propertyId, LodgingQuoteRequest request, bool isStaff = false)
    {
        var checkIn = ThaiDate.CalendarDateUtc(request.CheckIn); var checkOut = ThaiDate.CalendarDateUtc(request.CheckOut);
        var ctx = await LoadContextAsync(companyId, propertyId, checkIn, checkOut);
        return BuildQuote(ctx, checkIn, checkOut, request.Rooms, request.Extras, request.RatePlanId, isStaff, excludeReservationId: null,
            promoCode: request.PromoCode, infants: Math.Max(0, request.Infants));
    }

    /// <summary>คิดราคาทั้งการจอง — ใช้ร่วมกันทั้ง quote (หน้าเว็บ) · สร้างจอง · เลื่อนวัน</summary>
    private LodgingQuoteResponse BuildQuote(PricingContext ctx, DateTime checkIn, DateTime checkOut,
        List<LodgingQuoteRoomRequest> rooms, List<LodgingQuoteExtraRequest>? extras, Guid? ratePlanId, bool isStaff, Guid? excludeReservationId,
        string? promoCode = null, int infants = 0, IReadOnlyDictionary<Guid, decimal>? staffKeptExtraBedPrice = null,
        LodgingGuestConfirmMode? reservationMode = null)
    {
        var res = new LodgingQuoteResponse
        {
            CheckIn = checkIn, CheckOut = checkOut, PricesIncludeVat = ctx.Property.PricesIncludeVat, VatRate = ctx.VatRate,
        };
        if (LodgingBookingGuards.PromoCodeProblem(promoCode) is string promoProblem) { res.Errors.Add(promoProblem); return res; }
        try { ValidateDates(ctx.Property, checkIn, checkOut, isStaff); }
        catch (BusinessRuleException ex) { res.Errors.Add(ex.Message); return res; }
        if (rooms == null || rooms.Count == 0) { res.Errors.Add("กรุณาเลือกห้องอย่างน้อย 1 ห้อง"); return res; }

        var nights = (int)(checkOut - checkIn).TotalDays;
        res.Nights = nights;
        var now = DateTime.UtcNow;
        LodgingRatePlan? planUsed = null;
        decimal roomSubtotal = 0;
        var totalGuests = 0;
        int totalAdults = 0, totalChildren = 0, totalExtra = 0;

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
            try { plan = PickRatePlan(ctx, ratePlanId, rt, checkIn, nights, isStaff); }
            catch (BusinessRuleException ex) { res.Errors.Add(ex.Message); plan = null; }
            planUsed ??= plan;
            var input = ctx.InputFor(rt, plan);
            foreach (var room in grp)
            {
                var adults = Math.Max(1, room.Adults); var children = Math.Max(0, room.Children);
                // คำตัดสินข้อ 123/124 (รอบ 202): คนเสริม = extraBeds (คนละคนกับ adults) · เกิน/ห้องไม่รับ = ปฏิเสธพร้อมข้อความ (เดิมตัดทิ้งเงียบ) ·
                // ความจุนับเฉพาะผู้ใหญ่ — เด็ก/ทารกไม่นับ (เดิมนับ MaxChildren/MaxOccupancy)
                var beds = Math.Max(0, room.ExtraBeds);
                var roomInput = input;
                // ฝ่ายค้านรอบ 202 P2-5: ราคาคนเสริมว่าง = ไม่ขายคนเสริม (ไม่ใช่ฟรี) — ตัวตัดสิน LodgingOccupancy.SellsExtraBeds
                if (LodgingOccupancy.ExtraBedProblem(rt.Name, beds, rt.AllowExtraBed, rt.MaxExtraBeds, rt.ExtraBedPrice) is string extraProblem)
                {
                    // P2-4: เส้นเลื่อนวันของพนักงาน — ใบเดิมซื้อคนเสริมไว้ก่อนที่พักปิด/เปลี่ยนเตียงเสริม ⇒ คำเตือน + คงราคาคนเสริมเดิม (ไม่บล็อก) ·
                    // เส้นแขก/สร้างใหม่ยังบล็อก · หาราคาเดิมไม่ได้ = บล็อกพร้อมทางไปต่อ (ห้ามคิด 0 เงียบ)
                    if (isStaff && staffKeptExtraBedPrice != null && staffKeptExtraBedPrice.TryGetValue(rt.Id, out var keptPrice))
                    {
                        res.Warnings.Add($"{extraProblem} — การจองนี้ซื้อคนเสริม {beds} คนไว้ก่อนแล้ว คงไว้ในราคาเดิม {keptPrice:N2} บาท/คน/คืน");
                        roomInput = input with { ExtraBedPrice = keptPrice };
                    }
                    else if (isStaff && staffKeptExtraBedPrice != null)
                        res.Errors.Add($"{extraProblem} — หาราคาคนเสริมเดิมของการจองนี้ไม่ได้ · แก้จำนวนคนเสริมของห้องก่อนเลื่อนวัน");
                    else res.Errors.Add(extraProblem);
                }
                if (LodgingOccupancy.AdultProblem(rt.Name, adults, rt.MaxAdults, rt.AllowExtraBed, rt.MaxExtraBeds, rt.ExtraBedPrice) is string adultProblem)
                    res.Errors.Add(adultProblem);
                var q = LodgingPricingEngine.QuoteRoom(checkIn, checkOut, adults, children, beds, roomInput);
                res.Rooms.Add(new LodgingQuoteRoomLine
                {
                    RoomTypeId = rt.Id, RoomTypeName = rt.Name, Adults = adults, Children = children, ExtraBeds = beds,
                    RoomRate = q.RoomRate, ExtraGuestCharge = q.ExtraGuestCharge, ExtraBedCharge = q.ExtraBedCharge, Subtotal = q.Subtotal,
                    Nights = NightsDto(q.Nights),
                });
                roomSubtotal += q.Subtotal;
                // บริการเสริม "ต่อคน" นับทุกคนที่พักจริง รวมคนเสริม (ยกเว้นทารก) — ตัวเดียว LodgingOccupancy.ChargeableGuests
                totalGuests += LodgingOccupancy.ChargeableGuests(adults, children, beds);
                totalAdults += adults; totalChildren += children; totalExtra += beds;
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
        // คำตัดสินข้อ 128: มัดจำที่ต้องชำระตามโหมดยืนยัน (Instant ⇒ 0 · RequireSlip มัดจำ 0 ⇒ ยอดเต็ม) — ตัวตัดสินเดียว ·
        // เลื่อนวันส่งโหมดที่ตรึงบนใบ (reservationMode) · ใบเดิมที่ไม่มีโหมด ⇒ โหมดปัจจุบันของที่พักตามช่องทาง (พฤติกรรมเดิม)
        var channelMode = reservationMode ?? LodgingGuestConfirmPolicy.ForChannel(LodgingGuestConfirmPolicy.Resolve(ctx.Property), isStaff);
        totals = totals with { DepositRequired = LodgingGuestConfirmPolicy.QuotedDeposit(channelMode, totals.DepositRequired, totals.TotalAmount) };
        var terms = LodgingGuestConfirmPolicy.QuoteTerms(channelMode, totals.DepositRequired, ctx.Property.AutoConfirmOnSlip, ctx.Property.SlipDeadlineMinutes);
        res.SlipRequired = terms.SlipRequired; res.AmountDueLabel = terms.AmountLabel; res.ConfirmModeNote = terms.Note;

        res.RatePlanId = planUsed?.Id; res.RatePlanName = planUsed?.Name;
        // คำตัดสินข้อ 123: จำนวนผู้เข้าพักรวมจากเซิร์ฟเวอร์ (หน้าเว็บห้ามบวกเอง)
        var guestTotals = LodgingOccupancy.Totals(totalAdults, totalChildren, infants, totalExtra);
        res.Adults = guestTotals.Adults; res.Children = guestTotals.Children; res.Infants = guestTotals.Infants;
        res.ExtraGuests = guestTotals.ExtraGuests; res.TotalGuests = guestTotals.Total; res.GuestSummary = LodgingOccupancy.Summary(guestTotals);
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

        if (LodgingBookingGuards.PromoCodeProblem(request.PromoCode) is string promoProblem)
            throw new BusinessRuleException(promoProblem, "LODGING-PROMO-UNSUPPORTED");

        await ExpireHoldsAsync(companyId, propertyId);
        var checkIn = ThaiDate.CalendarDateUtc(request.CheckIn); var checkOut = ThaiDate.CalendarDateUtc(request.CheckOut);

        // ── O-P0-1 รอบ 202: ตรวจห้องว่าง + คิดราคา + ออกเลขจอง ใต้ล็อกต่อที่พักตัวเดียวกัน ──
        // เดิมโหลดห้องว่าง/คิดราคานอกล็อก แล้วล็อกเฉพาะตอนออกเลข ⇒ สองคำขอพร้อมกันเห็น "ว่าง 1" ทั้งคู่ แล้วได้เลขจองคนละเลข
        // = ห้องสุดท้ายถูกจองซ้อน · ตอนนี้ทุกเส้นที่เปลี่ยนการใช้ห้อง (สร้าง · ยืนยัน · เลื่อนวัน · จัดห้อง · เช็คอิน · ยกเลิกอัตโนมัติ)
        // อ่านห้องว่างหลังได้ล็อกเดียวกัน (PropertyLockKey) · ผู้ติดต่อสร้างในธุรกรรมเดียวกัน (ล้ม = ไม่เหลือแถวกำพร้า)
        var res = await WithPropertyLockAsync(companyId, propertyId, async () =>
        {
            var ctx = await LoadContextAsync(companyId, propertyId, checkIn, checkOut);
            if (!isStaff && !ctx.Property.OnlineBookingEnabled) throw new BusinessRuleException("ที่พักนี้ยังไม่เปิดรับจองออนไลน์ — กรุณาติดต่อโดยตรง");
            if (!isStaff && ctx.Property.RequireGuestIdNumber && string.IsNullOrWhiteSpace(request.GuestIdNumber))
                throw new BusinessRuleException("ที่พักนี้ต้องการเลขบัตรประชาชน/พาสปอร์ตของผู้เข้าพัก");
            if (!isStaff && !string.IsNullOrWhiteSpace(ctx.Property.HouseRules) && !request.AcceptHouseRules)
                throw new BusinessRuleException("กรุณายอมรับกติกาที่พักก่อนจอง");

            var quote = BuildQuote(ctx, checkIn, checkOut, request.Rooms, request.Extras, request.RatePlanId, isStaff, null,
                infants: Math.Max(0, request.Infants));
            if (quote.Errors.Count > 0) throw new BusinessRuleException(string.Join(" · ", quote.Errors));

            // O-P1-6 · คำตัดสินข้อ 122: การจองสาธารณะที่ยังรอชำระ ≤ 3 ต่อเบอร์/อีเมลต่อที่พัก (นับใต้ล็อก — ยิงพร้อมกันหลบเพดานไม่ได้)
            if (!isStaff)
            {
                var pendingGuests = await _db.LodgingReservations.AsNoTracking()
                    .Where(x => x.CompanyId == companyId && x.PropertyId == propertyId && x.Status == LodgingReservationStatus.Pending)
                    .Select(x => new { x.GuestPhone, x.GuestEmail }).ToListAsync();
                var existing = LodgingBookingGuards.CountPendingForGuest(
                    pendingGuests.Select(x => (x.GuestPhone, x.GuestEmail)), request.GuestPhone, request.GuestEmail);
                if (LodgingBookingGuards.PendingCapProblem(existing) is string capProblem)
                    throw new BusinessRuleException(capProblem, "LODGING-PENDING-CAP", 429);
            }

            var contactId = request.ContactId ?? await FindOrCreateContactAsync(companyId, request, actor);
            var prop = ctx.Property;
            // คำตัดสินข้อ 128: สถานะเริ่มต้น + เวลาถือห้องจากตัวตัดสินเดียว (เดิม `prop.ConfirmWithoutDeposit || มัดจำ 0 || staff ConfirmImmediately`) ·
            // โหมดที่ใช้กับใบนี้ตรึงบนใบ (เส้นพนักงานของที่พัก RequireSlip ⇒ RequireDeposit — ไม่บังคับสลิป)
            var channelMode = LodgingGuestConfirmPolicy.ForChannel(LodgingGuestConfirmPolicy.Resolve(prop), isStaff);
            var initial = LodgingGuestConfirmPolicy.Initial(channelMode, quote.DepositRequired, isStaff && request.ConfirmImmediately,
                prop.PaymentHoldMinutes, prop.SlipDeadlineMinutes);
            var immediateConfirm = initial.Status == LodgingReservationStatus.Confirmed;
            var policy = quote.CancellationPolicyId is Guid polId ? ctx.Policies.FirstOrDefault(p => p.Id == polId) : null;

            var created = new LodgingReservation
            {
                CompanyId = companyId, PropertyId = propertyId, SiteId = siteId ?? prop.SiteId,
                PublicToken = NewToken(), Status = initial.Status, GuestConfirmMode = channelMode,
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
                PromoCode = null,   // ไม่มีเครื่องคิดโค้ด — ไม่ว่างถูกปฏิเสธด้านบน (ไม่เก็บค่าที่ไม่มีผล)
                RoomSubtotal = quote.RoomSubtotal, ExtrasTotal = quote.ExtrasTotal, DiscountAmount = quote.DiscountAmount,
                ServiceChargeAmount = quote.ServiceChargeAmount, VatAmount = quote.VatAmount, TotalAmount = quote.TotalAmount,
                Currency = "THB", PriceBreakdownJson = J(quote),
                DepositRequired = quote.DepositRequired,
                HoldExpiresAt = initial.HoldMinutes is int holdMinutes ? DateTime.UtcNow.AddMinutes(holdMinutes) : null,
                ConfirmedAt = immediateConfirm ? DateTime.UtcNow : null, ConfirmedBy = immediateConfirm ? actor : null,
                InternalNotes = request.InternalNotes, CreatedBy = actor,
            };
            foreach (var r in quote.Rooms)
                created.Rooms.Add(new LodgingReservationRoom
                {
                    CompanyId = companyId, RoomTypeId = r.RoomTypeId, RoomTypeName = r.RoomTypeName, Adults = r.Adults, Children = r.Children,
                    ExtraBeds = r.ExtraBeds, NightlyRatesJson = J(r.Nights), Subtotal = r.Subtotal, CreatedBy = actor,
                });
            foreach (var e in quote.Extras)
            {
                var ex = ctx.Extras.FirstOrDefault(x => x.Id == e.ExtraId);
                created.Extras.Add(new LodgingReservationExtra
                {
                    CompanyId = companyId, ExtraId = e.ExtraId, Name = e.Name, PriceMode = e.PriceMode, UnitPrice = e.UnitPrice,
                    Quantity = e.Quantity, Total = e.Total, ProductId = ex?.ProductId, CreatedBy = actor,
                });
            }

            // เลขจอง: RES-{code}-{yyMM}-{####} ต่อที่พัก — อยู่ใต้ล็อกเดียวกับการตรวจห้องว่าง (FNV-1a key · ห้าม HashCode.Combine)
            var prefix = $"RES-{prop.Code}-{DateTime.UtcNow.AddHours(7):yyMM}-";
            // integer-max ผ่านตัวกลาง — เดิมเรียงแบบ**ข้อความ** ⇒ โรงแรมที่มี
            // การจองเกิน 9,999 ครั้งในเดือนเดียว "RES-…-9999" ยังชนะ "…-10000"
            // ⇒ เลขวนกลับไปทับใบเดิม
            var seqSuffixes = await _db.LodgingReservations.IgnoreQueryFilters()
                .Where(x => x.CompanyId == companyId && x.PropertyId == propertyId
                            && x.ReservationNumber.StartsWith(prefix))
                .OrderByDescending(x => x.CreatedAt)
                .Take(Accounting.Helpers.SequenceNumber.ScanWindow)
                .Select(x => x.ReservationNumber.Substring(prefix.Length))
                .ToListAsync();
            created.ReservationNumber = Accounting.Helpers.SequenceNumber.Format(
                prefix, Accounting.Helpers.SequenceNumber.NextSequence(seqSuffixes));
            _db.LodgingReservations.Add(created);
            await _db.SaveChangesAsync();
            return created;
        });

        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Create, res, new { action = "CreateReservation", source = source.ToString(), by = actor, total = res.TotalAmount, deposit = res.DepositRequired }));
        await _db.SaveChangesAsync();
        _logger.LogInformation("Lodging reservation {No} created ({Status}, total {Total}, deposit {Dep})", res.ReservationNumber, res.Status, res.TotalAmount, res.DepositRequired);

        // คำตัดสินข้อ 128: ใบที่ยังรอสลิปไม่ใช่ "จองใหม่สำเร็จ" ⇒ ไม่แจ้งเจ้าของตอนนี้ (แจ้งตอนแขกส่งสลิป) · แขกได้อีเมลพร้อมลิงก์ส่งสลิป
        await TryNotifyAsync(companyId, res.Id, LodgingGuestConfirmPolicy.CreatedEvent(res.GuestConfirmMode, res.Status));
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
    /// ตรงนี้แค่ทำให้สถานะในตารางตรงกับความจริงที่ผู้ใช้เห็น)
    ///
    /// <para>รอบ 202 (O-P1-4/5): เงื่อนไข "หมด hold" มาจาก <see cref="LodgingHoldRule.HoldLapsed"/> ตัวเดียวกับตัวนับห้องว่าง ⇒ ใบที่ส่งสลิปแล้ว ·
    /// รับมัดจำแล้ว · เงินออนไลน์เข้าแต่ยืนยันไม่ได้ <b>ไม่ถูกยกเลิกและยังกันห้อง</b> จนกว่าพนักงานตัดสิน · ยกเลิกใต้ล็อกที่พัก + อ่านค่าใหม่
    /// หลังได้ล็อก (การต่อเวลาถือห้องตอนแขกเริ่มจ่ายออนไลน์ชนะ — ไม่ยกเลิกใบที่เพิ่งถูกต่อ)</para></summary>
    private async Task ExpireHoldsAsync(Guid companyId, Guid propertyId)
    {
        var now = DateTime.UtcNow;
        // superset ราคาถูก (ไม่ล็อก) — ไม่มีแถวเข้าข่าย = ไม่แตะล็อกเลย (หน้ารายการ/ค้นหาเรียกบ่อย)
        var anyCandidate = await _db.LodgingReservations.AsNoTracking()
            .AnyAsync(r => r.CompanyId == companyId && r.PropertyId == propertyId && r.Status == LodgingReservationStatus.Pending
                && r.HoldExpiresAt != null && r.HoldExpiresAt <= now);
        if (!anyCandidate) return;
        await WithPropertyLockAsync(companyId, propertyId, async () =>
        {
            var facts = await _db.LodgingReservations.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.PropertyId == propertyId && r.Status == LodgingReservationStatus.Pending
                    && r.HoldExpiresAt != null && r.HoldExpiresAt <= now)
                .Select(r => new { r.Id, r.Status, r.HoldExpiresAt, r.DepositPaid, r.SlipUploadedAt, r.PaymentProblemAt })
                .ToListAsync();
            var lapsedIds = facts
                .Where(f => LodgingHoldRule.HoldLapsed(new LodgingHoldFacts(f.Status, f.HoldExpiresAt, f.DepositPaid, f.SlipUploadedAt != null, f.PaymentProblemAt != null), now))
                .Select(f => f.Id).ToList();
            if (lapsedIds.Count == 0) return 0;
            var expired = await _db.LodgingReservations.Where(r => r.CompanyId == companyId && lapsedIds.Contains(r.Id)).ToListAsync();
            foreach (var r in expired)
            {
                r.Status = LodgingReservationStatus.Cancelled; r.CancelledAt = now;
                r.CancellationReason = LodgingHoldRule.AutoExpireReason;   // ตัวอ่าน: LodgingHoldRule.IsAutoExpiredHold (สลิปหลังหมด hold · P1-3ก)
                _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "AutoExpireHold" }));
            }
            await _db.SaveChangesAsync();
            return expired.Count;
        });
    }

    /// <summary>
    /// <b>ถือห้องไว้ระหว่างแขกจ่ายออนไลน์</b> (O-P1-4 รอบ 202) — เรียกสองครั้งจากหน้าจ่ายเงินสาธารณะ: ก่อนสร้างรายการชำระ
    /// (<paramref name="intentExpiresAt"/> = null ⇒ ถือ <see cref="LodgingHoldRule.DefaultPaymentWindow"/>) และหลังสร้างแล้ว (ถึงวันหมดอายุของ QR/ลิงก์)
    ///
    /// <para>ใต้ล็อกที่พัก: hold หมดแล้ว (ห้องอาจถูกขายไป) ⇒ ตรวจห้องว่างก่อน — ไม่ว่าง = ปฏิเสธ<b>ก่อนแขกจ่าย</b> (ดีกว่าเงินเข้าแล้วยืนยันไม่ได้) ·
    /// ไม่ใช่ Pending / ไม่มี hold = ไม่ต้องทำอะไร</para>
    /// </summary>
    public async Task HoldForOnlinePaymentAsync(Guid companyId, Guid reservationId, DateTime? intentExpiresAt)
    {
        var propertyId = await _db.LodgingReservations.AsNoTracking()
            .Where(x => x.Id == reservationId && x.CompanyId == companyId).Select(x => (Guid?)x.PropertyId).FirstOrDefaultAsync();
        if (propertyId == null) return;
        await WithPropertyLockAsync(companyId, propertyId.Value, async () =>
        {
            var r = await RequireReservationAsync(companyId, reservationId);
            await _db.Entry(r).ReloadAsync();
            if (r.Status != LodgingReservationStatus.Pending || r.HoldExpiresAt == null) return false;
            var now = DateTime.UtcNow;
            var facts = new LodgingHoldFacts(r.Status, r.HoldExpiresAt, r.DepositPaid, r.SlipUploadedAt != null, r.PaymentProblemAt != null);
            if (LodgingHoldRule.HoldLapsed(facts, now))
            {
                var ctx = await LoadContextAsync(companyId, r.PropertyId, r.CheckInDate, r.CheckOutDate, excludeReservationId: r.Id);
                EnsureRoomsAvailable(ctx, r, "หมดเวลาถือห้องแล้วและห้องถูกจองไป · กรุณาติดต่อที่พักหรือจองใหม่");
            }
            var until = LodgingHoldRule.PaymentHoldUntil(now, intentExpiresAt);
            var extended = LodgingHoldRule.ExtendHold(r.HoldExpiresAt, until);
            if (extended == r.HoldExpiresAt) return false;
            r.HoldExpiresAt = extended;
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "HoldForOnlinePayment", until = extended, intentExpiresAt }));
            await _db.SaveChangesAsync();
            return true;
        });
    }
}
