using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Lodging;

/// <summary>โมดูลที่พัก — ส่วนตั้งค่า (partial 1/3). ส่วนจอง/เอกสารอยู่ใน
/// LodgingService.Reservations.cs และแม่บ้าน/แดชบอร์ดใน LodgingService.Operations.cs</summary>
public partial class LodgingService : ILodgingService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<LodgingService> _logger;
    private readonly IDocumentService _docService;
    private readonly IEmailService? _email;
    /// <summary>แจ้งกลุ่ม LINE ของที่พักเมื่อมีจอง/สลิปใหม่ — ที่พักไทยเช็ค LINE
    /// จริงกว่าอีเมล · ไม่ได้ตั้งค่า = เงียบ ไม่ error (LDG-P2-06)</summary>
    private readonly ILineNotifyService? _line;
    private readonly IImageProcessingService? _images;
    /// <summary>มิเตอร์ — optional: บันทึกไม่ได้ต้องไม่ทำให้เช็คเอาต์พัง (เสียรายได้
    /// 1 รายการยอมรับได้ · ทำให้แขกออกจากที่พักไม่ได้ยอมรับไม่ได้)</summary>
    private readonly IUsageMeteringService? _metering;
    private readonly IEntitlementService? _entitlement;

    public LodgingService(AccountingDbContext db, ILogger<LodgingService> logger, IDocumentService docService,
        IEmailService? email = null, IImageProcessingService? images = null,
        IUsageMeteringService? metering = null, IEntitlementService? entitlement = null,
        ILineNotifyService? line = null)
    {
        _db = db;
        _logger = logger;
        _docService = docService;
        _email = email;
        _images = images;
        _metering = metering;
        _entitlement = entitlement;
        _line = line;
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static string J(object o) => JsonSerializer.Serialize(o, JsonOpts);
    private static List<string> ParseStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? new(); } catch { return new(); }
    }
    private static List<Guid> ParseGuids(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<Guid>>(json, JsonOpts) ?? new(); } catch { return new(); }
    }
    private static string Time(TimeOnly t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    private static TimeOnly ParseTime(string? s, TimeOnly fallback)
        => TimeOnly.TryParseExact(s ?? "", "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.None, out var t) ? t : fallback;
    private static string Slugify(string s)
    {
        var chars = s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private async Task<decimal> EffectiveVatRateAsync(Guid companyId, LodgingProperty prop)
    {
        if (prop.ChargeVat is bool cv) return cv ? 7m : 0m;
        var vat = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => (bool?)c.IsVatRegistered).FirstOrDefaultAsync();
        return vat == true ? 7m : 0m;
    }

    private async Task<LodgingProperty> RequirePropertyAsync(Guid companyId, Guid propertyId, bool tracking = false)
    {
        var q = tracking ? _db.LodgingProperties : _db.LodgingProperties.AsNoTracking();
        return await q.FirstOrDefaultAsync(p => p.Id == propertyId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบที่พัก");
    }

    // ═══════════════════════════ Property ═══════════════════════════

    public async Task<List<LodgingPropertyDto>> GetPropertiesAsync(Guid companyId)
    {
        var props = await _db.LodgingProperties.AsNoTracking()
            .Where(p => p.CompanyId == companyId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync();
        var list = new List<LodgingPropertyDto>();
        foreach (var p in props) list.Add(await ToDtoAsync(companyId, p));
        return list;
    }

    public async Task<LodgingPropertyDto?> GetPropertyAsync(Guid companyId, Guid propertyId)
    {
        var p = await _db.LodgingProperties.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == propertyId && x.CompanyId == companyId);
        return p == null ? null : await ToDtoAsync(companyId, p);
    }

    public async Task<LodgingPropertyDto> CreatePropertyAsync(Guid companyId, LodgingPropertyDto dto, string userId)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new BusinessRuleException("กรุณาระบุชื่อที่พัก");

        // ที่พักแห่งแรกใช้ฟรี (มากับเว็บ Hotel template) · แห่งที่ 2 ขึ้นไปเป็น add-on
        // — hard block ตรงนี้ได้เพราะเป็น "ของใหม่ที่ยังไม่เคยเปิด" ไม่กระทบงานที่ทำอยู่
        // (ต่างจากโควตาเอกสารที่ห้ามบล็อก — LODGING_LICENSING_PLAN §5)
        var existing = await _db.LodgingProperties.CountAsync(x => x.CompanyId == companyId && !x.IsDeleted);
        if (existing >= 1 && _entitlement != null)
        {
            var ent = await _entitlement.CheckAsync(companyId, Models.Constants.AddOnCodes.LodgingMultiProperty);
            if (!ent.Allowed)
                throw new BusinessRuleException(
                    $"เปิดที่พักได้ 1 แห่งในแพ็กเกจปัจจุบัน — {ent.UpgradeHint ?? "เปิดส่วนเสริม \"ที่พักหลายแห่ง\""}"
                    + " (ที่หน้า \"ส่วนเสริมของฉัน\")",
                    "ADDON-REQUIRED:" + Models.Constants.AddOnCodes.LodgingMultiProperty);
        }

        var p = new LodgingProperty { CompanyId = companyId, CreatedBy = userId };
        Apply(p, dto);
        await EnsureSiteNotBoundElsewhereAsync(companyId, p);
        await GuardAccountingModeAsync(companyId, p, LodgingAccountingMode.Full, dto, userId);
        if (string.IsNullOrWhiteSpace(p.Code)) p.Code = DeriveCode(p.Name);
        await EnsureUniqueCodeAsync(companyId, p);
        _db.LodgingProperties.Add(p);
        await _db.SaveChangesAsync();
        return await ToDtoAsync(companyId, p);
    }

    public async Task<LodgingPropertyDto> UpdatePropertyAsync(Guid companyId, Guid propertyId, LodgingPropertyDto dto, string userId)
    {
        var p = await RequirePropertyAsync(companyId, propertyId, tracking: true);
        var prevMode = p.AccountingMode;
        Apply(p, dto);
        await EnsureSiteNotBoundElsewhereAsync(companyId, p);
        await GuardAccountingModeAsync(companyId, p, prevMode, dto, userId);
        if (string.IsNullOrWhiteSpace(p.Code)) p.Code = DeriveCode(p.Name);
        await EnsureUniqueCodeAsync(companyId, p);
        p.UpdatedBy = userId;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await ToDtoAsync(companyId, p);
    }

    /// <summary>ด่านของโหมดออกเอกสาร (LODGING_LICENSING_PLAN §13.3 · ทีม CPA)
    ///
    /// • `Off` = ไม่ออกเอกสารเลย — บริษัทที่ **จด VAT** เลือกได้ต่อเมื่อติ๊กยืนยันว่า
    ///   ออกใบกำกับจากระบบอื่น มิฉะนั้นเราคือ "สาเหตุ" ที่ทำให้ลูกค้าผิด §86/4
    /// • `ReceiptOnly` = สำหรับกิจการที่ไม่จด VAT เท่านั้น (จด VAT แล้วรับเงินค่าห้อง
    ///   ต้องออกใบกำกับ ไม่ใช่ใบเสร็จเปล่า)
    /// เก็บวัน/ผู้ยืนยันเป็นหลักฐาน — ไม่ใช่แค่ผ่านด่านแล้วลืม</summary>
    private async Task GuardAccountingModeAsync(Guid companyId, LodgingProperty p,
        LodgingAccountingMode previous, LodgingPropertyDto dto, string userId)
    {
        if (p.AccountingMode == LodgingAccountingMode.Full) { p.AccountingModeAckAt = null; p.AccountingModeAckBy = null; return; }

        var vatRegistered = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => (bool?)c.IsVatRegistered).FirstOrDefaultAsync() == true;

        if (p.AccountingMode == LodgingAccountingMode.ReceiptOnly && vatRegistered)
            throw new BusinessRuleException(
                "บริษัทจดทะเบียน VAT ต้องออกใบกำกับภาษีเมื่อรับเงินค่าห้อง (§86/4) — "
                + "โหมด \"ใบเสร็จอย่างเดียว\" ใช้ได้เฉพาะกิจการที่ไม่ได้จด VAT",
                "RD-86/4");

        if (p.AccountingMode == LodgingAccountingMode.Off && vatRegistered && !dto.AccountingModeAcknowledged
            && p.AccountingModeAckAt == null)
            throw new BusinessRuleException(
                "ปิดการออกเอกสารได้ แต่บริษัทจด VAT ต้องยืนยันว่าจะออกใบกำกับภาษีจากระบบอื่น "
                + "(§86/4 บังคับให้ออกทุกครั้งที่รับเงิน) — ติ๊กยืนยันในหน้าตั้งค่าก่อน",
                "RD-86/4-ACK");

        if (dto.AccountingModeAcknowledged && p.AccountingModeAckAt == null)
        {
            p.AccountingModeAckAt = DateTime.UtcNow;
            p.AccountingModeAckBy = userId;
        }
        if (previous != p.AccountingMode)
            _logger.LogInformation("ที่พัก {Prop} เปลี่ยนโหมดออกเอกสาร {From} → {To} โดย {User}",
                p.Id, previous, p.AccountingMode, userId);
    }

    private static string DeriveCode(string name)
    {
        var letters = new string(name.Where(char.IsLetterOrDigit).Take(4).ToArray()).ToUpperInvariant();
        return string.IsNullOrEmpty(letters) || letters.Any(c => c > 127) ? "STAY" : letters;
    }

    /// <summary>เว็บหนึ่งผูกที่พักได้แห่งเดียว — storefront อ่านที่พักจาก SiteId (`ResolvePropertyIdForSiteAsync`
    /// หยิบ FirstOrDefault) ถ้าผูกซ้ำได้ แขกจะจองที่พัก A ขณะเจ้าของคิดว่าเปิด B โดยไม่มี error ที่ไหน
    /// (ทีมตรวจรอบ 158 L-02) · index ฐานข้อมูลเป็น partial unique คู่กัน — ด่านนี้ให้ข้อความไทยแทน 500</summary>
    private async Task EnsureSiteNotBoundElsewhereAsync(Guid companyId, LodgingProperty p)
    {
        if (p.SiteId == null) return;
        var other = await _db.LodgingProperties.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.Id != p.Id && x.SiteId == p.SiteId)
            .Select(x => x.Name).FirstOrDefaultAsync();
        if (other != null)
            throw new BusinessRuleException($"เว็บไซต์นี้ผูกกับที่พัก \"{other}\" อยู่แล้ว — เว็บหนึ่งเปิดจองได้ที่พักเดียว (หน้า /booking อ่านที่พักจากเว็บ) กรุณาปลดการผูกที่ที่พักเดิมก่อน หรือเลือกเว็บอื่น");
    }

    private async Task EnsureUniqueCodeAsync(Guid companyId, LodgingProperty p)
    {
        var code = p.Code.Trim().ToUpperInvariant();
        var clash = await _db.LodgingProperties.AnyAsync(x => x.CompanyId == companyId && x.Id != p.Id && x.Code == code);
        if (clash) throw new BusinessRuleException($"รหัสที่พัก {code} ซ้ำกับที่พักอื่น");
        p.Code = code;
    }

    private static void Apply(LodgingProperty p, LodgingPropertyDto d)
    {
        p.SiteId = d.SiteId; p.BranchId = d.BranchId;
        p.Name = d.Name.Trim(); p.NameEn = d.NameEn?.Trim(); p.Code = d.Code?.Trim() ?? "";
        p.PropertyType = d.PropertyType; p.Description = d.Description; p.Address = d.Address;
        p.Phone = d.Phone; p.Email = d.Email; p.LineId = d.LineId; p.MapUrl = d.MapUrl;
        p.StarRating = Math.Clamp(d.StarRating, 0, 5);
        p.ImagesJson = J(d.Images ?? new()); p.AmenitiesJson = J(d.Amenities ?? new());
        p.CheckInTime = ParseTime(d.CheckInTime, new TimeOnly(14, 0));
        p.CheckOutTime = ParseTime(d.CheckOutTime, new TimeOnly(12, 0));
        p.EarlyCheckInHours = Math.Max(0, d.EarlyCheckInHours); p.EarlyCheckInFee = Math.Max(0, d.EarlyCheckInFee);
        p.LateCheckOutHours = Math.Max(0, d.LateCheckOutHours); p.LateCheckOutFee = Math.Max(0, d.LateCheckOutFee);
        p.MinNights = Math.Max(1, d.MinNights); p.MaxNights = Math.Max(p.MinNights, d.MaxNights);
        p.MaxAdvanceDays = Math.Max(1, d.MaxAdvanceDays); p.MinAdvanceHours = Math.Max(0, d.MinAdvanceHours);
        p.AutoConfirmOnDeposit = d.AutoConfirmOnDeposit; p.ConfirmWithoutDeposit = d.ConfirmWithoutDeposit;
        p.PaymentHoldMinutes = Math.Max(15, d.PaymentHoldMinutes); p.OverbookingAllowance = Math.Max(0, d.OverbookingAllowance);
        p.ChildMaxAge = Math.Max(0, d.ChildMaxAge); p.InfantMaxAge = Math.Max(0, d.InfantMaxAge);
        p.OnlineBookingEnabled = d.OnlineBookingEnabled; p.RequireGuestIdNumber = d.RequireGuestIdNumber;
        p.DepositPercent = Math.Clamp(d.DepositPercent, 0, 100); p.DepositFixedAmount = d.DepositFixedAmount;
        p.DepositMinAmount = Math.Max(0, d.DepositMinAmount); p.DepositMaxAmount = d.DepositMaxAmount;
        p.DepositDeferredAccountCode = string.IsNullOrWhiteSpace(d.DepositDeferredAccountCode) ? null : d.DepositDeferredAccountCode.Trim();
        p.DepositOutputVatDeferred = d.DepositOutputVatDeferred;
        p.AccountingMode = d.AccountingMode;
        p.PricesIncludeVat = d.PricesIncludeVat; p.ChargeVat = d.ChargeVat;
        p.ServiceChargePercent = Math.Clamp(d.ServiceChargePercent, 0, 100);
        p.RoomRevenueAccountCode = string.IsNullOrWhiteSpace(d.RoomRevenueAccountCode) ? null : d.RoomRevenueAccountCode.Trim();
        p.ServiceChargeAccountCode = string.IsNullOrWhiteSpace(d.ServiceChargeAccountCode) ? null : d.ServiceChargeAccountCode.Trim();
        p.CancellationFeeAccountCode = string.IsNullOrWhiteSpace(d.CancellationFeeAccountCode) ? null : d.CancellationFeeAccountCode.Trim();
        p.WeekendMultiplier = d.WeekendMultiplier <= 0 ? 1m : d.WeekendMultiplier;
        p.WeekendDaysMask = d.WeekendDaysMask & 127; p.ExtraGuestPrice = Math.Max(0, d.ExtraGuestPrice);
        p.DefaultCancellationPolicyId = d.DefaultCancellationPolicyId;
        p.NoShowChargePercent = Math.Clamp(d.NoShowChargePercent, 0, 100);
        p.ConfirmationMessage = d.ConfirmationMessage; p.HouseRules = d.HouseRules;
        p.NotifyOwnerOnBooking = d.NotifyOwnerOnBooking; p.NotifyEmails = d.NotifyEmails;
        p.HousekeepingMinutesPerRoom = Math.Max(5, d.HousekeepingMinutesPerRoom);
        p.AutoCreateHousekeepingTaskOnCheckout = d.AutoCreateHousekeepingTaskOnCheckout;
        p.IsActive = d.IsActive; p.SortOrder = d.SortOrder;
    }

    private async Task<LodgingPropertyDto> ToDtoAsync(Guid companyId, LodgingProperty p)
    {
        var rtCount = await _db.LodgingRoomTypes.CountAsync(r => r.PropertyId == p.Id && r.CompanyId == companyId && r.IsActive);
        var unitCount = await _db.LodgingUnits.CountAsync(u => u.CompanyId == companyId && u.IsActive && u.RoomType.PropertyId == p.Id);
        string? siteName = p.SiteId == null ? null
            : await _db.Sites.AsNoTracking().Where(s => s.Id == p.SiteId && s.CompanyId == companyId).Select(s => s.Name).FirstOrDefaultAsync();
        return new LodgingPropertyDto
        {
            Id = p.Id, SiteId = p.SiteId, BranchId = p.BranchId, Name = p.Name, NameEn = p.NameEn, Code = p.Code,
            PropertyType = p.PropertyType, Description = p.Description, Address = p.Address, Phone = p.Phone,
            Email = p.Email, LineId = p.LineId, MapUrl = p.MapUrl, StarRating = p.StarRating,
            Images = ParseStrings(p.ImagesJson), Amenities = ParseStrings(p.AmenitiesJson),
            CheckInTime = Time(p.CheckInTime), CheckOutTime = Time(p.CheckOutTime),
            EarlyCheckInHours = p.EarlyCheckInHours, EarlyCheckInFee = p.EarlyCheckInFee,
            LateCheckOutHours = p.LateCheckOutHours, LateCheckOutFee = p.LateCheckOutFee,
            MinNights = p.MinNights, MaxNights = p.MaxNights, MaxAdvanceDays = p.MaxAdvanceDays, MinAdvanceHours = p.MinAdvanceHours,
            AutoConfirmOnDeposit = p.AutoConfirmOnDeposit, ConfirmWithoutDeposit = p.ConfirmWithoutDeposit,
            PaymentHoldMinutes = p.PaymentHoldMinutes, OverbookingAllowance = p.OverbookingAllowance,
            ChildMaxAge = p.ChildMaxAge, InfantMaxAge = p.InfantMaxAge, OnlineBookingEnabled = p.OnlineBookingEnabled,
            RequireGuestIdNumber = p.RequireGuestIdNumber,
            DepositPercent = p.DepositPercent, DepositFixedAmount = p.DepositFixedAmount, DepositMinAmount = p.DepositMinAmount,
            DepositMaxAmount = p.DepositMaxAmount, DepositDeferredAccountCode = p.DepositDeferredAccountCode,
            DepositOutputVatDeferred = p.DepositOutputVatDeferred,
            AccountingMode = p.AccountingMode,
            AccountingModeAcknowledged = p.AccountingModeAckAt != null,
            PricesIncludeVat = p.PricesIncludeVat, ChargeVat = p.ChargeVat, ServiceChargePercent = p.ServiceChargePercent,
            RoomRevenueAccountCode = p.RoomRevenueAccountCode, ServiceChargeAccountCode = p.ServiceChargeAccountCode,
            CancellationFeeAccountCode = p.CancellationFeeAccountCode,
            WeekendMultiplier = p.WeekendMultiplier, WeekendDaysMask = p.WeekendDaysMask, ExtraGuestPrice = p.ExtraGuestPrice,
            DefaultCancellationPolicyId = p.DefaultCancellationPolicyId, NoShowChargePercent = p.NoShowChargePercent,
            ConfirmationMessage = p.ConfirmationMessage, HouseRules = p.HouseRules,
            NotifyOwnerOnBooking = p.NotifyOwnerOnBooking, NotifyEmails = p.NotifyEmails,
            HousekeepingMinutesPerRoom = p.HousekeepingMinutesPerRoom,
            AutoCreateHousekeepingTaskOnCheckout = p.AutoCreateHousekeepingTaskOnCheckout,
            IsActive = p.IsActive, SortOrder = p.SortOrder,
            RoomTypeCount = rtCount, UnitCount = unitCount, SiteName = siteName,
            EffectiveVatRate = await EffectiveVatRateAsync(companyId, p),
        };
    }

    // ═══════════════════════════ Room types / Units ═══════════════════════════

    public async Task<List<LodgingRoomTypeDto>> GetRoomTypesAsync(Guid companyId, Guid propertyId, bool includeInactive = false)
    {
        var q = _db.LodgingRoomTypes.AsNoTracking().Where(r => r.PropertyId == propertyId && r.CompanyId == companyId);
        if (!includeInactive) q = q.Where(r => r.IsActive);
        var types = await q.Include(r => r.Units).OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync();
        return types.Select(ToDto).ToList();
    }

    private static LodgingRoomTypeDto ToDto(LodgingRoomType r) => new()
    {
        Id = r.Id, PropertyId = r.PropertyId, Name = r.Name, NameEn = r.NameEn, Code = r.Code, Slug = r.Slug,
        Description = r.Description, Images = ParseStrings(r.ImagesJson), Amenities = ParseStrings(r.AmenitiesJson),
        BedType = r.BedType, SizeSqm = r.SizeSqm, ViewType = r.ViewType,
        StandardOccupancy = r.StandardOccupancy, MaxAdults = r.MaxAdults, MaxChildren = r.MaxChildren, MaxOccupancy = r.MaxOccupancy,
        AllowExtraBed = r.AllowExtraBed, MaxExtraBeds = r.MaxExtraBeds, PricingMode = r.PricingMode, BaseRate = r.BaseRate,
        ExtraGuestPrice = r.ExtraGuestPrice, ExtraBedPrice = r.ExtraBedPrice, MinNights = r.MinNights,
        IncludesBreakfast = r.IncludesBreakfast, ProductId = r.ProductId, IsActive = r.IsActive, SortOrder = r.SortOrder,
        UnitCount = r.Units.Count(u => u.IsActive && !u.IsDeleted),
        Units = r.Units.Where(u => !u.IsDeleted).OrderBy(u => u.SortOrder).ThenBy(u => u.Number).Select(u => ToDto(u, r.Name)).ToList(),
    };

    private static LodgingUnitDto ToDto(LodgingUnit u, string? roomTypeName) => new()
    {
        Id = u.Id, RoomTypeId = u.RoomTypeId, Number = u.Number, Floor = u.Floor, Building = u.Building, Notes = u.Notes,
        HousekeepingStatus = u.HousekeepingStatus, IsOutOfService = u.IsOutOfService, OutOfServiceUntil = u.OutOfServiceUntil,
        IsActive = u.IsActive, SortOrder = u.SortOrder, RoomTypeName = roomTypeName,
    };

    public async Task<LodgingRoomTypeDto> SaveRoomTypeAsync(Guid companyId, LodgingRoomTypeDto dto, string userId)
    {
        await RequirePropertyAsync(companyId, dto.PropertyId);
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new BusinessRuleException("กรุณาระบุชื่อประเภทห้อง");
        if (dto.BaseRate < 0) throw new BusinessRuleException("ราคาฐานต้องไม่ติดลบ");
        LodgingRoomType r;
        if (dto.Id is Guid id)
        {
            r = await _db.LodgingRoomTypes.Include(x => x.Units)
                    .FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId && x.PropertyId == dto.PropertyId)
                ?? throw new KeyNotFoundException("ไม่พบประเภทห้อง");
            r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            r = new LodgingRoomType { CompanyId = companyId, PropertyId = dto.PropertyId, CreatedBy = userId };
            _db.LodgingRoomTypes.Add(r);
        }
        r.Name = dto.Name.Trim(); r.NameEn = dto.NameEn?.Trim();
        r.Code = string.IsNullOrWhiteSpace(dto.Code) ? DeriveCode(r.NameEn ?? r.Name) : dto.Code.Trim().ToUpperInvariant();
        r.Slug = string.IsNullOrWhiteSpace(dto.Slug) ? Slugify(r.NameEn ?? r.Code) : Slugify(dto.Slug);
        if (string.IsNullOrEmpty(r.Slug)) r.Slug = r.Code.ToLowerInvariant();
        r.Description = dto.Description; r.ImagesJson = J(dto.Images ?? new()); r.AmenitiesJson = J(dto.Amenities ?? new());
        r.BedType = dto.BedType; r.SizeSqm = dto.SizeSqm; r.ViewType = dto.ViewType;
        r.StandardOccupancy = Math.Max(1, dto.StandardOccupancy); r.MaxAdults = Math.Max(1, dto.MaxAdults);
        r.MaxChildren = Math.Max(0, dto.MaxChildren); r.MaxOccupancy = Math.Max(r.MaxAdults, dto.MaxOccupancy);
        r.AllowExtraBed = dto.AllowExtraBed; r.MaxExtraBeds = dto.AllowExtraBed ? Math.Max(0, dto.MaxExtraBeds) : 0;
        r.PricingMode = dto.PricingMode; r.BaseRate = dto.BaseRate;
        r.ExtraGuestPrice = dto.ExtraGuestPrice; r.ExtraBedPrice = dto.ExtraBedPrice; r.MinNights = dto.MinNights;
        r.IncludesBreakfast = dto.IncludesBreakfast; r.ProductId = dto.ProductId;
        r.IsActive = dto.IsActive; r.SortOrder = dto.SortOrder;

        var slugClash = await _db.LodgingRoomTypes.AnyAsync(x => x.CompanyId == companyId && x.PropertyId == r.PropertyId && x.Id != r.Id && x.Slug == r.Slug);
        if (slugClash) r.Slug = $"{r.Slug}-{Guid.NewGuid():N}"[..Math.Min(40, r.Slug.Length + 9)];
        await _db.SaveChangesAsync();
        return ToDto(r);
    }

    public async Task<bool> DeleteRoomTypeAsync(Guid companyId, Guid roomTypeId)
    {
        var r = await _db.LodgingRoomTypes.FirstOrDefaultAsync(x => x.Id == roomTypeId && x.CompanyId == companyId);
        if (r == null) return false;
        var hasFuture = await _db.LodgingReservationRooms.AnyAsync(x => x.CompanyId == companyId && x.RoomTypeId == roomTypeId
            && x.Reservation.Status != LodgingReservationStatus.Cancelled && x.Reservation.Status != LodgingReservationStatus.NoShow
            && x.Reservation.CheckOutDate >= DateTime.UtcNow.Date);
        if (hasFuture) throw new BusinessRuleException("ประเภทห้องนี้ยังมีการจองที่ยังไม่สิ้นสุด — ปิดใช้งาน (IsActive=false) แทนการลบ");
        r.IsDeleted = true; r.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<LodgingUnitDto>> GetUnitsAsync(Guid companyId, Guid propertyId)
    {
        return await _db.LodgingUnits.AsNoTracking()
            .Where(u => u.CompanyId == companyId && u.RoomType.PropertyId == propertyId)
            .OrderBy(u => u.RoomType.SortOrder).ThenBy(u => u.SortOrder).ThenBy(u => u.Number)
            .Select(u => new LodgingUnitDto
            {
                Id = u.Id, RoomTypeId = u.RoomTypeId, Number = u.Number, Floor = u.Floor, Building = u.Building, Notes = u.Notes,
                HousekeepingStatus = u.HousekeepingStatus, IsOutOfService = u.IsOutOfService, OutOfServiceUntil = u.OutOfServiceUntil,
                IsActive = u.IsActive, SortOrder = u.SortOrder, RoomTypeName = u.RoomType.Name,
            }).ToListAsync();
    }

    public async Task<LodgingUnitDto> SaveUnitAsync(Guid companyId, LodgingUnitDto dto, string userId)
    {
        var rt = await _db.LodgingRoomTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == dto.RoomTypeId && x.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบประเภทห้อง");
        if (string.IsNullOrWhiteSpace(dto.Number)) throw new BusinessRuleException("กรุณาระบุหมายเลขห้อง");
        LodgingUnit u;
        if (dto.Id is Guid id)
        {
            u = await _db.LodgingUnits.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId) ?? throw new KeyNotFoundException("ไม่พบห้อง");
            u.UpdatedBy = userId; u.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            u = new LodgingUnit { CompanyId = companyId, CreatedBy = userId };
            _db.LodgingUnits.Add(u);
        }
        var number = dto.Number.Trim();
        var clash = await _db.LodgingUnits.AnyAsync(x => x.CompanyId == companyId && x.Id != u.Id && x.Number == number && x.RoomType.PropertyId == rt.PropertyId);
        if (clash) throw new BusinessRuleException($"หมายเลขห้อง {number} มีอยู่แล้วในที่พักนี้");
        u.RoomTypeId = dto.RoomTypeId; u.Number = number; u.Floor = dto.Floor; u.Building = dto.Building; u.Notes = dto.Notes;
        u.HousekeepingStatus = dto.HousekeepingStatus; u.IsOutOfService = dto.IsOutOfService; u.OutOfServiceUntil = dto.OutOfServiceUntil;
        u.IsActive = dto.IsActive; u.SortOrder = dto.SortOrder;
        await _db.SaveChangesAsync();
        return ToDto(u, rt.Name);
    }

    public async Task<bool> DeleteUnitAsync(Guid companyId, Guid unitId)
    {
        var u = await _db.LodgingUnits.FirstOrDefaultAsync(x => x.Id == unitId && x.CompanyId == companyId);
        if (u == null) return false;
        var inUse = await _db.LodgingReservationRooms.AnyAsync(x => x.CompanyId == companyId && x.UnitId == unitId
            && (x.Reservation.Status == LodgingReservationStatus.Confirmed || x.Reservation.Status == LodgingReservationStatus.CheckedIn || x.Reservation.Status == LodgingReservationStatus.Pending)
            && x.Reservation.CheckOutDate >= DateTime.UtcNow.Date);
        if (inUse) throw new BusinessRuleException("ห้องนี้ถูก assign ให้การจองที่ยังไม่สิ้นสุด — ย้ายแขกก่อนลบ");
        u.IsDeleted = true; u.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<LodgingUnitDto> SetUnitStatusAsync(Guid companyId, Guid unitId, LodgingUnitStatusRequest request, string userId)
    {
        var u = await _db.LodgingUnits.Include(x => x.RoomType).FirstOrDefaultAsync(x => x.Id == unitId && x.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบห้อง");
        u.HousekeepingStatus = request.Status;
        if (request.IsOutOfService is bool oos)
        {
            u.IsOutOfService = oos;
            u.OutOfServiceUntil = oos ? request.OutOfServiceUntil : null;
            if (oos && u.HousekeepingStatus is LodgingHousekeepingStatus.VacantClean or LodgingHousekeepingStatus.VacantDirty)
                u.HousekeepingStatus = LodgingHousekeepingStatus.OutOfOrder;
        }
        if (!string.IsNullOrWhiteSpace(request.Note)) u.Notes = request.Note;
        u.UpdatedBy = userId; u.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return ToDto(u, u.RoomType.Name);
    }

    // ═══════════════════════════ Rate plans ═══════════════════════════

    public async Task<List<LodgingRatePlanDto>> GetRatePlansAsync(Guid companyId, Guid propertyId)
        => (await _db.LodgingRatePlans.AsNoTracking().Where(x => x.PropertyId == propertyId && x.CompanyId == companyId)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync()).Select(ToDto).ToList();

    private static LodgingRatePlanDto ToDto(LodgingRatePlan x) => new()
    {
        Id = x.Id, PropertyId = x.PropertyId, RoomTypeId = x.RoomTypeId, Name = x.Name, NameEn = x.NameEn, Code = x.Code,
        Description = x.Description, AdjustMode = x.AdjustMode, AdjustValue = x.AdjustValue, IncludesBreakfast = x.IncludesBreakfast,
        IsRefundable = x.IsRefundable, CancellationPolicyId = x.CancellationPolicyId, MinNights = x.MinNights, MaxNights = x.MaxNights,
        MinAdvanceDays = x.MinAdvanceDays, MaxAdvanceDays = x.MaxAdvanceDays, ValidFrom = x.ValidFrom, ValidTo = x.ValidTo,
        ApplicableDaysMask = x.ApplicableDaysMask, DepositPercent = x.DepositPercent, IsDefault = x.IsDefault, IsActive = x.IsActive, SortOrder = x.SortOrder,
    };

    public async Task<LodgingRatePlanDto> SaveRatePlanAsync(Guid companyId, LodgingRatePlanDto dto, string userId)
    {
        await RequirePropertyAsync(companyId, dto.PropertyId);
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new BusinessRuleException("กรุณาระบุชื่อแผนราคา");
        if (dto.AdjustMode == LodgingRateAdjustMode.Multiplier && dto.AdjustValue <= 0) throw new BusinessRuleException("ตัวคูณต้องมากกว่า 0");
        if (dto.AdjustMode == LodgingRateAdjustMode.Absolute && dto.AdjustValue < 0) throw new BusinessRuleException("ราคาต้องไม่ติดลบ");
        LodgingRatePlan x;
        if (dto.Id is Guid id)
        {
            x = await _db.LodgingRatePlans.FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId && r.PropertyId == dto.PropertyId)
                ?? throw new KeyNotFoundException("ไม่พบแผนราคา");
            x.UpdatedBy = userId; x.UpdatedAt = DateTime.UtcNow;
        }
        else { x = new LodgingRatePlan { CompanyId = companyId, PropertyId = dto.PropertyId, CreatedBy = userId }; _db.LodgingRatePlans.Add(x); }
        x.RoomTypeId = dto.RoomTypeId; x.Name = dto.Name.Trim(); x.NameEn = dto.NameEn?.Trim();
        x.Code = string.IsNullOrWhiteSpace(dto.Code) ? DeriveCode(x.NameEn ?? x.Name) : dto.Code.Trim().ToUpperInvariant();
        x.Description = dto.Description; x.AdjustMode = dto.AdjustMode; x.AdjustValue = dto.AdjustValue;
        x.IncludesBreakfast = dto.IncludesBreakfast; x.IsRefundable = dto.IsRefundable; x.CancellationPolicyId = dto.CancellationPolicyId;
        x.MinNights = dto.MinNights; x.MaxNights = dto.MaxNights; x.MinAdvanceDays = dto.MinAdvanceDays; x.MaxAdvanceDays = dto.MaxAdvanceDays;
        x.ValidFrom = dto.ValidFrom?.Date; x.ValidTo = dto.ValidTo?.Date; x.ApplicableDaysMask = dto.ApplicableDaysMask & 127;
        x.DepositPercent = dto.DepositPercent is decimal dp ? Math.Clamp(dp, 0, 100) : null;
        x.IsDefault = dto.IsDefault; x.IsActive = dto.IsActive; x.SortOrder = dto.SortOrder;
        if (x.IsDefault)
        {
            var others = await _db.LodgingRatePlans.Where(r => r.CompanyId == companyId && r.PropertyId == x.PropertyId && r.Id != x.Id && r.IsDefault).ToListAsync();
            foreach (var o in others) o.IsDefault = false;
        }
        await _db.SaveChangesAsync();
        return ToDto(x);
    }

    public async Task<bool> DeleteRatePlanAsync(Guid companyId, Guid ratePlanId)
    {
        var x = await _db.LodgingRatePlans.FirstOrDefaultAsync(r => r.Id == ratePlanId && r.CompanyId == companyId);
        if (x == null) return false;
        x.IsDeleted = true; x.IsActive = false; x.IsDefault = false;
        await _db.SaveChangesAsync();
        return true;
    }

    // ═══════════════════════════ Seasons ═══════════════════════════

    public async Task<List<LodgingSeasonDto>> GetSeasonsAsync(Guid companyId, Guid propertyId)
        => (await _db.LodgingSeasons.AsNoTracking().Where(x => x.PropertyId == propertyId && x.CompanyId == companyId)
                .OrderBy(x => x.StartDate).ToListAsync()).Select(ToDto).ToList();

    private static LodgingSeasonDto ToDto(LodgingSeason x) => new()
    {
        Id = x.Id, PropertyId = x.PropertyId, Name = x.Name, SeasonType = x.SeasonType, StartDate = x.StartDate, EndDate = x.EndDate,
        Multiplier = x.Multiplier, IsRecurringYearly = x.IsRecurringYearly, MinNights = x.MinNights,
        RoomTypeIds = ParseGuids(x.RoomTypeIdsJson), IsActive = x.IsActive,
    };

    public async Task<LodgingSeasonDto> SaveSeasonAsync(Guid companyId, LodgingSeasonDto dto, string userId)
    {
        await RequirePropertyAsync(companyId, dto.PropertyId);
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new BusinessRuleException("กรุณาระบุชื่อฤดูกาล");
        if (dto.Multiplier <= 0) throw new BusinessRuleException("ตัวคูณราคาต้องมากกว่า 0");
        if (!dto.IsRecurringYearly && dto.EndDate.Date < dto.StartDate.Date) throw new BusinessRuleException("วันสิ้นสุดต้องไม่ก่อนวันเริ่ม");
        LodgingSeason x;
        if (dto.Id is Guid id)
        {
            x = await _db.LodgingSeasons.FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId && r.PropertyId == dto.PropertyId)
                ?? throw new KeyNotFoundException("ไม่พบฤดูกาล");
            x.UpdatedBy = userId; x.UpdatedAt = DateTime.UtcNow;
        }
        else { x = new LodgingSeason { CompanyId = companyId, PropertyId = dto.PropertyId, CreatedBy = userId }; _db.LodgingSeasons.Add(x); }
        x.Name = dto.Name.Trim(); x.SeasonType = dto.SeasonType;
        x.StartDate = ThaiDate.CalendarDateUtc(dto.StartDate); x.EndDate = ThaiDate.CalendarDateUtc(dto.EndDate);
        x.Multiplier = dto.Multiplier; x.IsRecurringYearly = dto.IsRecurringYearly; x.MinNights = dto.MinNights;
        x.RoomTypeIdsJson = dto.RoomTypeIds is { Count: > 0 } ? J(dto.RoomTypeIds) : null; x.IsActive = dto.IsActive;
        await _db.SaveChangesAsync();
        return ToDto(x);
    }

    public async Task<bool> DeleteSeasonAsync(Guid companyId, Guid seasonId)
    {
        var x = await _db.LodgingSeasons.FirstOrDefaultAsync(r => r.Id == seasonId && r.CompanyId == companyId);
        if (x == null) return false;
        x.IsDeleted = true; x.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    // ═══════════════════════════ Rate overrides ═══════════════════════════

    public async Task<List<LodgingRateOverrideDto>> GetRateOverridesAsync(Guid companyId, Guid propertyId, DateTime from, DateTime to, Guid? roomTypeId = null)
    {
        var f = from.Date; var t = to.Date;
        var q = _db.LodgingRateOverrides.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.RoomType.PropertyId == propertyId && o.Date >= f && o.Date <= t);
        if (roomTypeId is Guid rt) q = q.Where(o => o.RoomTypeId == rt);
        return await q.OrderBy(o => o.Date).Select(o => new LodgingRateOverrideDto
        {
            Id = o.Id, RoomTypeId = o.RoomTypeId, Date = o.Date, Rate = o.Rate, StopSell = o.StopSell,
            Allotment = o.Allotment, MinNights = o.MinNights, Note = o.Note,
        }).ToListAsync();
    }

    public async Task<int> SaveRateOverridesAsync(Guid companyId, LodgingRateOverrideBulkRequest request, string userId)
    {
        var rt = await _db.LodgingRoomTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.RoomTypeId && x.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบประเภทห้อง");
        var from = ThaiDate.CalendarDateUtc(request.FromDate); var to = ThaiDate.CalendarDateUtc(request.ToDate);
        if (to < from) throw new BusinessRuleException("ช่วงวันที่ไม่ถูกต้อง");
        if ((to - from).TotalDays > 366) throw new BusinessRuleException("ตั้งราคาได้ครั้งละไม่เกิน 1 ปี");
        if (request.Rate is decimal r && r < 0) throw new BusinessRuleException("ราคาต้องไม่ติดลบ");

        var existing = await _db.LodgingRateOverrides
            .Where(o => o.CompanyId == companyId && o.RoomTypeId == rt.Id && o.Date >= from && o.Date <= to).ToListAsync();
        var byDate = existing.ToDictionary(o => o.Date.Date);
        var n = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (request.Clear)
            {
                if (byDate.TryGetValue(d, out var del)) { _db.LodgingRateOverrides.Remove(del); n++; }
                continue;
            }
            if (!byDate.TryGetValue(d, out var o))
            {
                o = new LodgingRateOverride { CompanyId = companyId, RoomTypeId = rt.Id, Date = d, CreatedBy = userId };
                _db.LodgingRateOverrides.Add(o);
            }
            else { o.UpdatedBy = userId; o.UpdatedAt = DateTime.UtcNow; }
            // ส่ง null = ไม่แตะช่องนั้น (ตั้งเฉพาะที่ผู้ใช้กรอก) — ยกเว้น Rate ที่ผู้ใช้ตั้งใจล้างต้องส่ง Clear
            if (request.Rate.HasValue) o.Rate = request.Rate;
            if (request.StopSell.HasValue) o.StopSell = request.StopSell.Value;
            if (request.Allotment.HasValue) o.Allotment = request.Allotment.Value <= 0 ? null : request.Allotment;
            if (request.MinNights.HasValue) o.MinNights = request.MinNights.Value <= 0 ? null : request.MinNights;
            if (request.Note != null) o.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note;
            n++;
        }
        await _db.SaveChangesAsync();
        return n;
    }

    // ═══════════════════════════ Cancellation policies ═══════════════════════════

    public async Task<List<LodgingCancellationPolicyDto>> GetPoliciesAsync(Guid companyId, Guid propertyId)
        => (await _db.LodgingCancellationPolicies.AsNoTracking().Where(x => x.PropertyId == propertyId && x.CompanyId == companyId)
                .OrderByDescending(x => x.IsDefault).ThenBy(x => x.Name).ToListAsync()).Select(ToDto).ToList();

    private static LodgingCancellationPolicyDto ToDto(LodgingCancellationPolicy x) => new()
    {
        Id = x.Id, PropertyId = x.PropertyId, Name = x.Name, Description = x.Description,
        Rules = LodgingPricingEngine.ParseRules(x.RulesJson).Select(r => new LodgingCancellationRuleDto(r.DaysBefore, r.PenaltyPercent)).ToList(),
        NonRefundable = x.NonRefundable, IsDefault = x.IsDefault, IsActive = x.IsActive,
    };

    public async Task<LodgingCancellationPolicyDto> SavePolicyAsync(Guid companyId, LodgingCancellationPolicyDto dto, string userId)
    {
        await RequirePropertyAsync(companyId, dto.PropertyId);
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new BusinessRuleException("กรุณาระบุชื่อนโยบาย");
        foreach (var r in dto.Rules ?? new())
            if (r.DaysBefore < 0 || r.PenaltyPercent < 0 || r.PenaltyPercent > 100)
                throw new BusinessRuleException("กฎยกเลิกไม่ถูกต้อง (วัน ≥ 0, ค่าปรับ 0–100%)");
        LodgingCancellationPolicy x;
        if (dto.Id is Guid id)
        {
            x = await _db.LodgingCancellationPolicies.FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId && r.PropertyId == dto.PropertyId)
                ?? throw new KeyNotFoundException("ไม่พบนโยบาย");
            x.UpdatedBy = userId; x.UpdatedAt = DateTime.UtcNow;
        }
        else { x = new LodgingCancellationPolicy { CompanyId = companyId, PropertyId = dto.PropertyId, CreatedBy = userId }; _db.LodgingCancellationPolicies.Add(x); }
        x.Name = dto.Name.Trim(); x.Description = dto.Description; x.NonRefundable = dto.NonRefundable;
        x.RulesJson = J((dto.Rules ?? new()).OrderByDescending(r => r.DaysBefore).Select(r => new { daysBefore = r.DaysBefore, penaltyPercent = r.PenaltyPercent }));
        x.IsDefault = dto.IsDefault; x.IsActive = dto.IsActive;
        if (x.IsDefault)
        {
            var others = await _db.LodgingCancellationPolicies.Where(r => r.CompanyId == companyId && r.PropertyId == x.PropertyId && r.Id != x.Id && r.IsDefault).ToListAsync();
            foreach (var o in others) o.IsDefault = false;
        }
        await _db.SaveChangesAsync();
        if (x.IsDefault)
        {
            var prop = await _db.LodgingProperties.FirstOrDefaultAsync(p => p.Id == x.PropertyId && p.CompanyId == companyId);
            if (prop != null && prop.DefaultCancellationPolicyId != x.Id) { prop.DefaultCancellationPolicyId = x.Id; await _db.SaveChangesAsync(); }
        }
        return ToDto(x);
    }

    public async Task<bool> DeletePolicyAsync(Guid companyId, Guid policyId)
    {
        var x = await _db.LodgingCancellationPolicies.FirstOrDefaultAsync(r => r.Id == policyId && r.CompanyId == companyId);
        if (x == null) return false;
        x.IsDeleted = true; x.IsActive = false; x.IsDefault = false;
        var props = await _db.LodgingProperties.Where(p => p.CompanyId == companyId && p.DefaultCancellationPolicyId == policyId).ToListAsync();
        foreach (var p in props) p.DefaultCancellationPolicyId = null;
        await _db.SaveChangesAsync();
        return true;
    }

    // ═══════════════════════════ Extras ═══════════════════════════

    public async Task<List<LodgingExtraDto>> GetExtrasAsync(Guid companyId, Guid propertyId, bool includeInactive = false)
    {
        var q = _db.LodgingExtras.AsNoTracking().Where(x => x.PropertyId == propertyId && x.CompanyId == companyId);
        if (!includeInactive) q = q.Where(x => x.IsActive);
        return (await q.OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync()).Select(ToDto).ToList();
    }

    private static LodgingExtraDto ToDto(LodgingExtra x) => new()
    {
        Id = x.Id, PropertyId = x.PropertyId, Name = x.Name, NameEn = x.NameEn, Description = x.Description, Category = x.Category,
        PriceMode = x.PriceMode, Price = x.Price, MaxQuantity = x.MaxQuantity, ProductId = x.ProductId,
        ShowOnWebsite = x.ShowOnWebsite, IsActive = x.IsActive, SortOrder = x.SortOrder,
    };

    public async Task<LodgingExtraDto> SaveExtraAsync(Guid companyId, LodgingExtraDto dto, string userId)
    {
        await RequirePropertyAsync(companyId, dto.PropertyId);
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new BusinessRuleException("กรุณาระบุชื่อบริการเสริม");
        if (dto.Price < 0) throw new BusinessRuleException("ราคาต้องไม่ติดลบ");
        LodgingExtra x;
        if (dto.Id is Guid id)
        {
            x = await _db.LodgingExtras.FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId && r.PropertyId == dto.PropertyId)
                ?? throw new KeyNotFoundException("ไม่พบบริการเสริม");
            x.UpdatedBy = userId; x.UpdatedAt = DateTime.UtcNow;
        }
        else { x = new LodgingExtra { CompanyId = companyId, PropertyId = dto.PropertyId, CreatedBy = userId }; _db.LodgingExtras.Add(x); }
        x.Name = dto.Name.Trim(); x.NameEn = dto.NameEn?.Trim(); x.Description = dto.Description; x.Category = dto.Category;
        x.PriceMode = dto.PriceMode; x.Price = dto.Price; x.MaxQuantity = dto.MaxQuantity is int mq && mq > 0 ? mq : null;
        x.ProductId = dto.ProductId; x.ShowOnWebsite = dto.ShowOnWebsite; x.IsActive = dto.IsActive; x.SortOrder = dto.SortOrder;
        await _db.SaveChangesAsync();
        return ToDto(x);
    }

    public async Task<bool> DeleteExtraAsync(Guid companyId, Guid extraId)
    {
        var x = await _db.LodgingExtras.FirstOrDefaultAsync(r => r.Id == extraId && r.CompanyId == companyId);
        if (x == null) return false;
        x.IsDeleted = true; x.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }
}
