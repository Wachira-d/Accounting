using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Lodging;

/// <summary>โมดูลที่พัก — lifecycle ของการจอง + เอกสารบัญชี (partial 3/4)
///
/// เส้นเงิน (ทุกใบผ่าน IDocumentService — เลข gap-free §86/4):
///   ยืนยัน+รับมัดจำ → Receipt(IsDeposit) ⇒ Cr 217xx (+VAT ตอนรับเงิน §78/1 เว้นแต่ DepositOutputVatDeferred)
///   เช็คเอาต์      → TaxInvoice/Invoice ทั้งการเข้าพัก + folio, DepositApplied* ขับ JE ตัดมัดจำ, รับส่วนที่เหลือ = Payment
///   ยกเลิก/no-show → ส่วนที่คืน = RefundDeposit · ส่วนที่ริบ = RealizeDeposit เข้า CancellationFeeAccountCode
/// </summary>
public partial class LodgingService
{
    private static readonly LodgingReservationStatus[] Terminal = { LodgingReservationStatus.Cancelled, LodgingReservationStatus.NoShow, LodgingReservationStatus.CheckedOut };

    /// <summary>ป้ายโมดูลบนเอกสารที่โมดูลนี้ออกให้ — ใช้กันนับโควตาเอกสารซ้ำ
    /// (มิเตอร์ของที่พักคือ lodging.stay ไม่ใช่จำนวนใบ — DocumentQuotaPolicy)</summary>
    internal const string LodgingOrigin = "Lodging";

    /// <summary>นับการเข้าพักนี้เป็น 1 หน่วยมิเตอร์ (`lodging.stay`)
    ///
    /// เรียกตอน **ปิดสถานะ** เท่านั้น: เช็คเอาต์ · no-show · ยกเลิกที่มีเงินมัดจำ —
    /// ไม่ใช่ตอนจอง (จองแล้วยกเลิกฟรีต้องไม่โดนคิด) และไม่ใช่ตอนออกเอกสาร (ที่พัก
    /// ที่ตั้ง AccountingMode=Off ไม่มีเอกสารเลยแต่ต้องนับเท่ากัน — §13.2/§13.3)
    ///
    /// กันนับซ้ำสองชั้น: `MeteredPeriod` บนการจอง + IdempotencyKey ต่อ ReservationId
    /// **ห้าม throw** — มิเตอร์พังต้องไม่ทำให้เช็คเอาต์/ยกเลิกทำไม่ได้</summary>
    private async Task MeterStayAsync(Guid companyId, LodgingReservation r, string reason)
    {
        if (r.MeteredPeriod != null) return;
        var period = AddOnBilling.PeriodOf(DateTime.UtcNow);
        r.MeteredPeriod = period;
        if (_metering == null) return;
        try
        {
            await _metering.RecordAsync(new Accounting.Services.Interfaces.UsageRecordRequest(
                CompanyId: companyId,
                FeatureCode: Models.Constants.AddOnCodes.LodgingStay,
                Quantity: 1,
                IdempotencyKey: $"stay:{r.Id:N}",
                RefEntityType: "LodgingReservation",
                RefEntityId: r.Id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "บันทึกมิเตอร์การเข้าพัก {No} ({Reason}) ไม่สำเร็จ — งานหลักสำเร็จแล้ว",
                r.ReservationNumber, reason);
        }
    }

    private IQueryable<LodgingReservation> ResQuery(Guid companyId) => _db.LodgingReservations
        .Include(r => r.Property)
        .Include(r => r.Rooms).ThenInclude(x => x.Unit)
        .Include(r => r.Extras)
        .Include(r => r.Charges)
        .Include(r => r.RatePlan)
        .Where(r => r.CompanyId == companyId);

    private async Task<LodgingReservation> RequireReservationAsync(Guid companyId, Guid reservationId)
        => await ResQuery(companyId).FirstOrDefaultAsync(r => r.Id == reservationId) ?? throw new KeyNotFoundException("ไม่พบการจอง");

    private async Task<LodgingReservation?> ByTokenAsync(Guid companyId, Guid siteId, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        var t = token.Trim().ToLowerInvariant();
        return await ResQuery(companyId).FirstOrDefaultAsync(r => r.PublicToken == t && r.SiteId == siteId);
    }

    // ═══════════════════════════ Public (token) ═══════════════════════════

    public async Task<LodgingReservationResponse?> GetReservationByTokenAsync(Guid companyId, Guid siteId, string token)
    {
        var r = await ByTokenAsync(companyId, siteId, token);
        return r == null ? null : await MapAsync(companyId, r, includeToken: true, includeInternal: false);
    }

    public async Task<LodgingReservationResponse?> UploadSlipByTokenAsync(Guid companyId, Guid siteId, string token, IFormFile file, string? reference)
    {
        var r = await ByTokenAsync(companyId, siteId, token);
        if (r == null) return null;
        if (r.Status is LodgingReservationStatus.Cancelled or LodgingReservationStatus.NoShow)
            throw new BusinessRuleException("การจองนี้ถูกยกเลิกแล้ว");
        var url = await SaveSlipFileAsync(companyId, r.Id, file);
        r.PaymentSlipUrl = url; r.PaymentReference = reference; r.SlipUploadedAt = DateTime.UtcNow;
        // อัปโหลดสลิปแล้ว = ต่อเวลาถือห้องให้พนักงานตรวจ (ไม่ปล่อยห้องระหว่างรอตรวจ)
        if (r.Status == LodgingReservationStatus.Pending && r.HoldExpiresAt != null && r.HoldExpiresAt < DateTime.UtcNow.AddHours(24))
            r.HoldExpiresAt = DateTime.UtcNow.AddHours(24);
        _db.AuditLogs.Add(Audit(companyId, AuditAction.Update, r, new { action = "GuestUploadedSlip", reference }));
        await _db.SaveChangesAsync();
        await TryNotifyAsync(companyId, r.Id, "slip");
        return await MapAsync(companyId, r, includeToken: true, includeInternal: false);
    }

    public async Task<LodgingReservationResponse?> CancelByTokenAsync(Guid companyId, Guid siteId, string token, LodgingCancelRequest request)
    {
        var r = await ByTokenAsync(companyId, siteId, token);
        if (r == null) return null;
        if (r.Status is LodgingReservationStatus.CheckedIn or LodgingReservationStatus.CheckedOut)
            throw new BusinessRuleException("เช็คอินแล้ว — กรุณาติดต่อที่พักโดยตรง");
        await CancelCoreAsync(companyId, r, request.Reason ?? "แขกยกเลิกเอง", "guest", noShow: false);
        return await MapAsync(companyId, r, includeToken: true, includeInternal: false);
    }

    public async Task<LodgingGuestRequestDto?> CreateGuestRequestByTokenAsync(Guid companyId, Guid siteId, string token, LodgingGuestRequestCreate request)
    {
        var r = await ByTokenAsync(companyId, siteId, token);
        if (r == null) return null;
        if (string.IsNullOrWhiteSpace(request.Details)) throw new BusinessRuleException("กรุณาระบุรายละเอียด");
        var gr = new LodgingGuestRequest
        {
            CompanyId = companyId, PropertyId = r.PropertyId, ReservationId = r.Id, RequestType = request.RequestType,
            Details = request.Details.Trim().Length > 2000 ? request.Details.Trim()[..2000] : request.Details.Trim(), CreatedBy = "guest",
        };
        _db.LodgingGuestRequests.Add(gr);
        await _db.SaveChangesAsync();
        return ToDto(gr, r);
    }

    // ═══════════════════════════ Admin: list / get / update ═══════════════════════════

    public async Task<PagedResponse<LodgingReservationListItem>> ListReservationsAsync(Guid companyId, Guid? propertyId, string? status,
        DateTime? from, DateTime? to, string? search, string? view, int page, int pageSize)
    {
        if (propertyId is Guid pidExp) await ExpireHoldsAsync(companyId, pidExp);
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var q = _db.LodgingReservations.AsNoTracking().Where(r => r.CompanyId == companyId);
        if (propertyId is Guid pid) q = q.Where(r => r.PropertyId == pid);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<LodgingReservationStatus>(status, true, out var st)) q = q.Where(r => r.Status == st);
        var today = DateTime.UtcNow.AddHours(7).Date;
        switch (view)
        {
            case "arrivals": q = q.Where(r => r.CheckInDate == (from ?? today).Date && (r.Status == LodgingReservationStatus.Confirmed || r.Status == LodgingReservationStatus.Pending)); break;
            case "departures": q = q.Where(r => r.CheckOutDate == (from ?? today).Date && r.Status == LodgingReservationStatus.CheckedIn); break;
            case "inhouse": q = q.Where(r => r.Status == LodgingReservationStatus.CheckedIn); break;
            case "pending": q = q.Where(r => r.Status == LodgingReservationStatus.Pending); break;
            case "slips": q = q.Where(r => r.SlipUploadedAt != null && r.DepositPaid == 0 && r.Status == LodgingReservationStatus.Pending); break;
            default:
                if (from is DateTime f) q = q.Where(r => r.CheckOutDate > f.Date);
                if (to is DateTime t) q = q.Where(r => r.CheckInDate <= t.Date);
                break;
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(r => r.ReservationNumber.Contains(s) || r.GuestName.Contains(s) || (r.GuestPhone != null && r.GuestPhone.Contains(s))
                || (r.GuestEmail != null && r.GuestEmail.Contains(s)) || (r.SourceReference != null && r.SourceReference.Contains(s)));
        }
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(r => r.CheckInDate).ThenByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new
            {
                r.Id, r.ReservationNumber, r.Status, r.Source, r.GuestName, r.GuestPhone, r.CheckInDate, r.CheckOutDate, r.Nights,
                r.TotalAmount, r.FolioTotal, r.PaidAmount, r.DepositRequired, r.HoldExpiresAt, r.SlipUploadedAt, r.CreatedAt,
                Rooms = r.Rooms.Select(x => new { x.RoomTypeName, UnitNumber = x.Unit != null ? x.Unit.Number : null }).ToList(),
            }).ToListAsync();
        var list = items.Select(r => new LodgingReservationListItem
        {
            Id = r.Id, ReservationNumber = r.ReservationNumber, Status = r.Status, Source = r.Source, GuestName = r.GuestName, GuestPhone = r.GuestPhone,
            CheckInDate = r.CheckInDate, CheckOutDate = r.CheckOutDate, Nights = r.Nights, RoomCount = r.Rooms.Count,
            RoomSummary = string.Join(" · ", r.Rooms.GroupBy(x => x.RoomTypeName).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)),
            UnitNumbers = string.Join(", ", r.Rooms.Where(x => x.UnitNumber != null).Select(x => x.UnitNumber)),
            TotalAmount = r.TotalAmount, FolioTotal = r.FolioTotal, PaidAmount = r.PaidAmount,
            BalanceDue = Math.Max(0, r.TotalAmount + r.FolioTotal - r.PaidAmount), DepositRequired = r.DepositRequired,
            HoldExpiresAt = r.HoldExpiresAt, HasSlip = r.SlipUploadedAt != null, CreatedAt = r.CreatedAt,
        }).ToList();
        return new PagedResponse<LodgingReservationListItem>(list, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<LodgingReservationResponse?> GetReservationAsync(Guid companyId, Guid reservationId)
    {
        var r = await ResQuery(companyId).AsNoTracking().FirstOrDefaultAsync(x => x.Id == reservationId);
        return r == null ? null : await MapAsync(companyId, r, includeToken: true, includeInternal: true);
    }

    public async Task<LodgingReservationResponse> UpdateReservationAsync(Guid companyId, Guid reservationId, LodgingUpdateReservationRequest u, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (u.GuestName != null) { if (string.IsNullOrWhiteSpace(u.GuestName)) throw new BusinessRuleException("ชื่อผู้เข้าพักห้ามว่าง"); r.GuestName = u.GuestName.Trim(); }
        if (u.GuestEmail != null) r.GuestEmail = u.GuestEmail.Trim();
        if (u.GuestPhone != null) r.GuestPhone = u.GuestPhone.Trim();
        if (u.GuestNationality != null) r.GuestNationality = u.GuestNationality;
        if (u.GuestIdNumber != null) r.GuestIdNumber = u.GuestIdNumber.Trim();
        if (u.GuestAddress != null) r.GuestAddress = u.GuestAddress;
        if (u.GuestTaxId != null)
        {
            if (!string.IsNullOrWhiteSpace(u.GuestTaxId) && !ThaiTaxId.IsValid(u.GuestTaxId)) throw new BusinessRuleException("เลขประจำตัวผู้เสียภาษีไม่ถูกต้อง", "RD-86/4");
            r.GuestTaxId = string.IsNullOrWhiteSpace(u.GuestTaxId) ? null : ThaiTaxId.Normalize(u.GuestTaxId);
        }
        if (u.GuestCompanyName != null) r.GuestCompanyName = u.GuestCompanyName;
        if (u.ArrivalTime != null) r.ArrivalTime = u.ArrivalTime;
        if (u.SpecialRequests != null) r.SpecialRequests = u.SpecialRequests;
        if (u.InternalNotes != null) r.InternalNotes = u.InternalNotes;
        if (u.Infants is int inf) r.Infants = Math.Max(0, inf);
        if (u.SourceReference != null) r.SourceReference = u.SourceReference;
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    // ═══════════════════════════ Confirm + deposit ═══════════════════════════

    public async Task<LodgingReservationResponse> ConfirmAsync(Guid companyId, Guid reservationId, LodgingConfirmRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (Terminal.Contains(r.Status)) throw new BusinessRuleException($"การจองอยู่ในสถานะ {StatusTh(r.Status)} — ยืนยันไม่ได้");
        var amount = request.DepositAmount ?? Math.Max(0, r.DepositRequired - r.DepositPaid);
        if (amount < 0) throw new BusinessRuleException("ยอดมัดจำต้องไม่ติดลบ");
        var outstanding = r.TotalAmount + r.FolioTotal - r.PaidAmount;
        if (amount > outstanding + 0.005m) throw new BusinessRuleException($"ยอดรับเกินยอดคงค้าง ({outstanding:N2})");

        // ตรวจห้องว่างอีกครั้งตอนยืนยัน — Pending ที่หมด hold แล้วอาจถูกคนอื่นจองทับไปแล้ว
        if (r.Status == LodgingReservationStatus.Pending)
        {
            var ctx = await LoadContextAsync(companyId, r.PropertyId, r.CheckInDate, r.CheckOutDate, excludeReservationId: r.Id);
            foreach (var g in r.Rooms.GroupBy(x => x.RoomTypeId))
            {
                var avail = LodgingAvailability.AvailableRooms(r.CheckInDate, r.CheckOutDate, ctx.UnitsByRoomType.GetValueOrDefault(g.Key),
                    ctx.Property.OverbookingAllowance, ctx.BookedFor(g.Key), ctx.OverridesFor(g.Key), DateTime.UtcNow);
                if (avail < g.Count())
                    throw new BusinessRuleException($"{g.First().RoomTypeName} ว่างไม่พอแล้ว ({avail}/{g.Count()}) — ห้องถูกจองไปหลังหมดเวลาถือ", "LODGING-OVERSOLD");
            }
        }

        if (amount > 0)
        {
            if (r.DepositDocumentId != null && r.DepositPaid == 0)
                throw new BusinessRuleException("มีใบเสร็จมัดจำอยู่แล้วแต่ยอดยังไม่ถูกบันทึก — ตรวจสอบเอกสารก่อน");
            // โหมด Off = ไม่ออกเอกสารบัญชี (ลูกค้าใช้โปรแกรมบัญชีอื่น) — ยังบันทึกยอด
            // รับเงินไว้บนการจองตามปกติ และ**ยังนับมิเตอร์เท่าเดิม** (§13.3)
            if (r.Property.AccountingMode != LodgingAccountingMode.Off)
            {
                var doc = await CreateDepositReceiptAsync(companyId, r, amount, request, userId);
                r.DepositDocumentId ??= doc.Id;
            }
            r.DepositPaid += amount; r.PaidAmount += amount;
            if (!string.IsNullOrWhiteSpace(request.PaymentReference)) r.PaymentReference = request.PaymentReference;
        }
        var wasPending = r.Status == LodgingReservationStatus.Pending;
        r.Status = LodgingReservationStatus.Confirmed;
        r.ConfirmedAt ??= DateTime.UtcNow; r.ConfirmedBy ??= userId; r.HoldExpiresAt = null;
        if (!string.IsNullOrWhiteSpace(request.Note)) AppendInternal(r, request.Note);
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        _db.AuditLogs.Add(Audit(companyId, AuditAction.Approve, r, new { action = "Confirm", depositReceived = amount, method = request.PaymentMethod.ToString(), by = userId }));
        await _db.SaveChangesAsync();
        if (wasPending) await TryNotifyAsync(companyId, r.Id, "confirmed");
        return await MapAsync(companyId, r, true, true);
    }

    /// <summary>ใบเสร็จมัดจำ — Receipt IsDeposit=true (Cr ขายรอรับรู้ 217xx) ยอด = gross ที่รับจริง</summary>
    private async Task<DocumentResponse> CreateDepositReceiptAsync(Guid companyId, LodgingReservation r, decimal amount, LodgingConfirmRequest req, string userId)
    {
        var prop = r.Property;
        var vatRate = await EffectiveVatRateAsync(companyId, prop);
        var product = await FirstRoomProductCodeAsync(companyId, r);
        var request = new CreateDocumentRequest(
            DocumentType: DocumentType.Receipt,
            DocumentDate: req.PaymentDate ?? DateTime.UtcNow,
            DueDate: null,
            ContactId: r.ContactId ?? throw new BusinessRuleException("การจองไม่มีผู้ติดต่อ (Contact) — แก้ไขข้อมูลแขกก่อน"),
            Reference: r.ReservationNumber,
            Notes: $"มัดจำการจองที่พัก {r.ReservationNumber} · เข้าพัก {r.CheckInDate:dd/MM/yyyy}–{r.CheckOutDate:dd/MM/yyyy} ({r.Nights} คืน)",
            Lines: new List<DocumentLineRequest>
            {
                new(Description: $"มัดจำค่าห้องพัก {prop.Name} — จอง {r.ReservationNumber} ({RoomSummary(r)})",
                    Quantity: 1, Unit: "รายการ", UnitPrice: amount, DiscountPercent: 0, VatRate: vatRate, WithholdingTaxRate: 0,
                    AccountId: null, ProductCode: product)
            },
            PricesIncludeVat: true,
            BankAccountId: req.BankAccountId,
            BranchId: prop.BranchId,
            IsDeposit: true,
            DepositDeferredAccountCode: prop.DepositDeferredAccountCode,
            DepositOutputVatDeferred: prop.DepositOutputVatDeferred,
            BookingNumber: r.ReservationNumber,
            PaymentType: null,
            OriginModule: LodgingOrigin);
        var created = await _docService.CreateDocumentAsync(companyId, request, userId);
        await _docService.ApproveDocumentAsync(companyId, created.Id, userId, acknowledgeWarnings: true);
        return created;
    }

    private async Task<string?> FirstRoomProductCodeAsync(Guid companyId, LodgingReservation r)
    {
        var rtId = r.Rooms.Select(x => x.RoomTypeId).FirstOrDefault();
        if (rtId == Guid.Empty) return null;
        var pid = await _db.LodgingRoomTypes.AsNoTracking().Where(x => x.Id == rtId && x.CompanyId == companyId).Select(x => x.ProductId).FirstOrDefaultAsync();
        return pid == null ? null : await _db.Products.AsNoTracking().Where(p => p.Id == pid && p.CompanyId == companyId).Select(p => p.Code).FirstOrDefaultAsync();
    }

    private static string RoomSummary(LodgingReservation r)
        => string.Join(" · ", r.Rooms.GroupBy(x => x.RoomTypeName).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key));

    private static void AppendInternal(LodgingReservation r, string note)
    {
        var stamp = DateTime.UtcNow.AddHours(7).ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var line = $"[{stamp}] {note.Trim()}";
        r.InternalNotes = string.IsNullOrWhiteSpace(r.InternalNotes) ? line : r.InternalNotes.TrimEnd() + "\n" + line;
    }

    private static string StatusTh(LodgingReservationStatus s) => s switch
    {
        LodgingReservationStatus.Pending => "รอชำระมัดจำ", LodgingReservationStatus.Confirmed => "ยืนยันแล้ว",
        LodgingReservationStatus.CheckedIn => "เช็คอินแล้ว", LodgingReservationStatus.CheckedOut => "เช็คเอาต์แล้ว",
        LodgingReservationStatus.Cancelled => "ยกเลิก", LodgingReservationStatus.NoShow => "ไม่มาเข้าพัก", _ => s.ToString(),
    };

    // ═══════════════════════════ Assign / check-in ═══════════════════════════

    public async Task<LodgingReservationResponse> AssignUnitAsync(Guid companyId, Guid reservationId, LodgingAssignUnitRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (Terminal.Contains(r.Status)) throw new BusinessRuleException("การจองสิ้นสุดแล้ว");
        await AssignCoreAsync(companyId, r, request.ReservationRoomId, request.UnitId);
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    private async Task AssignCoreAsync(Guid companyId, LodgingReservation r, Guid reservationRoomId, Guid? unitId)
    {
        var room = r.Rooms.FirstOrDefault(x => x.Id == reservationRoomId) ?? throw new KeyNotFoundException("ไม่พบห้องในการจอง");
        if (unitId == null) { room.UnitId = null; room.Unit = null; return; }
        var unit = await _db.LodgingUnits.Include(u => u.RoomType).FirstOrDefaultAsync(u => u.Id == unitId && u.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหมายเลขห้อง");
        if (unit.RoomType.PropertyId != r.PropertyId) throw new BusinessRuleException("ห้องนี้ไม่ได้อยู่ในที่พักเดียวกัน");
        if (unit.RoomTypeId != room.RoomTypeId) AppendInternal(r, $"จัดห้อง {unit.Number} ({unit.RoomType.Name}) ให้การจองประเภท {room.RoomTypeName} — upgrade/ย้ายประเภท");
        if (unit.IsOutOfService && (unit.OutOfServiceUntil == null || unit.OutOfServiceUntil >= r.CheckInDate))
            throw new BusinessRuleException($"ห้อง {unit.Number} ปิดซ่อม/ปิดใช้งานอยู่");
        var clash = await _db.LodgingReservationRooms.AnyAsync(x => x.CompanyId == companyId && x.UnitId == unit.Id && x.ReservationId != r.Id
            && (x.Reservation.Status == LodgingReservationStatus.Confirmed || x.Reservation.Status == LodgingReservationStatus.CheckedIn
                || (x.Reservation.Status == LodgingReservationStatus.Pending && (x.Reservation.HoldExpiresAt == null || x.Reservation.HoldExpiresAt > DateTime.UtcNow)))
            && x.Reservation.CheckInDate < r.CheckOutDate && x.Reservation.CheckOutDate > r.CheckInDate);
        if (clash) throw new BusinessRuleException($"ห้อง {unit.Number} ถูกจัดให้การจองอื่นในช่วงเดียวกันแล้ว");
        room.UnitId = unit.Id; room.Unit = unit;
    }

    public async Task<LodgingReservationResponse> CheckInAsync(Guid companyId, Guid reservationId, LodgingCheckInRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (r.Status == LodgingReservationStatus.CheckedIn) throw new BusinessRuleException("เช็คอินไปแล้ว");
        if (r.Status != LodgingReservationStatus.Confirmed && r.Status != LodgingReservationStatus.Pending)
            throw new BusinessRuleException($"การจองอยู่ในสถานะ {StatusTh(r.Status)} — เช็คอินไม่ได้");
        if (r.Status == LodgingReservationStatus.Pending && r.DepositRequired > 0 && r.DepositPaid <= 0)
            throw new BusinessRuleException("ยังไม่ได้ยืนยันการจอง/รับมัดจำ — กด \"ยืนยัน\" ก่อน (หรือรับชำระตอนเช็คอิน)");
        var today = DateTime.UtcNow.AddHours(7).Date;
        if (r.CheckInDate > today.AddDays(1)) throw new BusinessRuleException($"วันเช็คอินคือ {r.CheckInDate:dd/MM/yyyy} — เช็คอินก่อนกำหนดไม่ได้ (เลื่อนวันก่อน)");
        foreach (var a in request.Assignments ?? new()) await AssignCoreAsync(companyId, r, a.ReservationRoomId, a.UnitId);
        var unassigned = r.Rooms.Where(x => x.UnitId == null).ToList();
        if (unassigned.Count > 0)
            throw new BusinessRuleException($"ยังไม่ได้จัดหมายเลขห้องให้ {unassigned.Count} ห้อง ({string.Join(", ", unassigned.Select(x => x.RoomTypeName))})");
        if (r.Property.RequireGuestIdNumber && string.IsNullOrWhiteSpace(request.GuestIdNumber ?? r.GuestIdNumber))
            throw new BusinessRuleException("ต้องบันทึกเลขบัตรประชาชน/พาสปอร์ตของผู้เข้าพักก่อนเช็คอิน");
        if (!string.IsNullOrWhiteSpace(request.GuestIdNumber)) r.GuestIdNumber = request.GuestIdNumber.Trim();
        if (!string.IsNullOrWhiteSpace(request.GuestNationality)) r.GuestNationality = request.GuestNationality;

        r.Status = LodgingReservationStatus.CheckedIn; r.CheckedInAt = DateTime.UtcNow; r.HoldExpiresAt = null;
        r.ConfirmedAt ??= DateTime.UtcNow; r.ConfirmedBy ??= userId;
        foreach (var room in r.Rooms.Where(x => x.Unit != null)) room.Unit!.HousekeepingStatus = LodgingHousekeepingStatus.Occupied;
        if (!string.IsNullOrWhiteSpace(request.Note)) AppendInternal(r, request.Note);
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        _db.AuditLogs.Add(Audit(companyId, AuditAction.Update, r, new { action = "CheckIn", units = r.Rooms.Select(x => x.Unit?.Number), by = userId }));
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    // ═══════════════════════════ Folio ═══════════════════════════

    public async Task<LodgingReservationResponse> AddChargeAsync(Guid companyId, Guid reservationId, LodgingAddChargeRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (r.Status is LodgingReservationStatus.CheckedOut or LodgingReservationStatus.Cancelled or LodgingReservationStatus.NoShow)
            throw new BusinessRuleException("การจองสิ้นสุดแล้ว — เพิ่มรายการไม่ได้ (ออกเอกสารแยกแทน)");
        if (string.IsNullOrWhiteSpace(request.Description)) throw new BusinessRuleException("กรุณาระบุรายการ");
        if (request.Quantity <= 0) throw new BusinessRuleException("จำนวนต้องมากกว่า 0");
        if (request.UnitPrice < 0) throw new BusinessRuleException("ราคาต้องไม่ติดลบ");
        var vat = request.VatRate ?? await EffectiveVatRateAsync(companyId, r.Property);
        var c = new LodgingFolioCharge
        {
            CompanyId = companyId, ReservationId = r.Id, ProductId = request.ProductId, Description = request.Description.Trim(),
            Quantity = request.Quantity, UnitPrice = request.UnitPrice,
            Total = Math.Round(request.Quantity * request.UnitPrice, 2, MidpointRounding.AwayFromZero),
            VatRate = vat, Source = request.Source, Status = LodgingChargeStatus.Pending, ChargedAt = DateTime.UtcNow, Notes = request.Notes, CreatedBy = userId,
        };
        r.Charges.Add(c);
        RecalcFolio(r);
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    public async Task<LodgingReservationResponse> CancelChargeAsync(Guid companyId, Guid reservationId, Guid chargeId, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        var c = r.Charges.FirstOrDefault(x => x.Id == chargeId) ?? throw new KeyNotFoundException("ไม่พบรายการ");
        if (c.Status == LodgingChargeStatus.Paid) throw new BusinessRuleException("รายการนี้ออกเอกสารแล้ว — ใช้ใบลดหนี้แทน");
        c.Status = LodgingChargeStatus.Cancelled; c.UpdatedBy = userId; c.UpdatedAt = DateTime.UtcNow;
        RecalcFolio(r);
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    private static void RecalcFolio(LodgingReservation r)
        => r.FolioTotal = Math.Round(r.Charges.Where(c => c.Status != LodgingChargeStatus.Cancelled).Sum(c => c.Total), 2, MidpointRounding.AwayFromZero);

    // ═══════════════════════════ Check-out ═══════════════════════════

    public async Task<LodgingReservationResponse> CheckOutAsync(Guid companyId, Guid reservationId, LodgingCheckOutRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (r.Status != LodgingReservationStatus.CheckedIn) throw new BusinessRuleException($"ต้องเช็คอินก่อนจึงเช็คเอาต์ได้ (สถานะปัจจุบัน {StatusTh(r.Status)})");
        if (r.FinalDocumentId != null) throw new BusinessRuleException("ออกเอกสารเช็คเอาต์ไปแล้ว");
        var prop = r.Property;
        // โหมด "ไม่ออกเอกสาร" — ปิดการเข้าพักให้จบงานหน้าเคาน์เตอร์ แล้วนับมิเตอร์
        // เท่ากับโหมดปกติ (ลูกค้าเลือกทิ้งมูลค่าส่วนเอกสารเอง ไม่ใช่ได้ใช้ฟรี)
        if (prop.AccountingMode == LodgingAccountingMode.Off)
            return await CheckOutWithoutDocumentAsync(companyId, r, request, userId);
        var vatRate = await EffectiveVatRateAsync(companyId, prop);

        if (request.DamageCharge is decimal dmg && dmg > 0)
        {
            r.Charges.Add(new LodgingFolioCharge
            {
                CompanyId = companyId, ReservationId = r.Id, Description = "ค่าเสียหาย/ของหาย" + (string.IsNullOrWhiteSpace(request.DamageDescription) ? "" : $" — {request.DamageDescription.Trim()}"),
                Quantity = 1, UnitPrice = dmg, Total = Math.Round(dmg, 2, MidpointRounding.AwayFromZero), VatRate = vatRate,
                Source = LodgingChargeSource.System, Status = LodgingChargeStatus.Pending, ChargedAt = DateTime.UtcNow, CreatedBy = userId,
            });
            RecalcFolio(r);
        }

        // ── ใบกำกับ/ใบแจ้งหนี้สุดท้าย ──
        var contactId = r.ContactId ?? throw new BusinessRuleException("การจองไม่มีผู้ติดต่อ (Contact)");
        if (request.IssueTaxInvoiceToCompany)
        {
            if (string.IsNullOrWhiteSpace(r.GuestTaxId) || string.IsNullOrWhiteSpace(r.GuestCompanyName))
                throw new BusinessRuleException("ออกใบกำกับในนามบริษัทต้องมีชื่อบริษัท + เลขผู้เสียภาษีของแขก (§86/4)", "RD-86/4");
            contactId = await FindOrCreateContactAsync(companyId, new LodgingCreateReservationRequest(r.CheckInDate, r.CheckOutDate, new(), r.GuestName, r.GuestEmail, r.GuestPhone,
                GuestAddress: r.GuestAddress, GuestTaxId: r.GuestTaxId, GuestCompanyName: r.GuestCompanyName), userId);
            r.ContactId = contactId;
        }

        var roomTypeIds = r.Rooms.Select(x => x.RoomTypeId).Distinct().ToList();
        var productCodes = await (from rt in _db.LodgingRoomTypes.AsNoTracking()
                                  join p in _db.Products.AsNoTracking() on rt.ProductId equals p.Id
                                  where rt.CompanyId == companyId && roomTypeIds.Contains(rt.Id)
                                  select new { rt.Id, p.Code }).ToDictionaryAsync(x => x.Id, x => x.Code);
        var extraProductIds = r.Extras.Where(e => e.ProductId != null).Select(e => e.ProductId!.Value)
            .Concat(r.Charges.Where(c => c.ProductId != null).Select(c => c.ProductId!.Value)).Distinct().ToList();
        var extraCodes = await _db.Products.AsNoTracking().Where(p => p.CompanyId == companyId && extraProductIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Code);

        var lines = new List<DocumentLineRequest>();
        foreach (var room in r.Rooms)
            lines.Add(new(
                Description: $"ค่าห้องพัก {room.RoomTypeName}{(room.Unit != null ? $" ห้อง {room.Unit.Number}" : "")} {r.Nights} คืน ({r.CheckInDate:dd/MM/yyyy}–{r.CheckOutDate:dd/MM/yyyy})",
                Quantity: 1, Unit: "รายการ", UnitPrice: room.Subtotal, DiscountPercent: 0, VatRate: vatRate, WithholdingTaxRate: 0,
                AccountId: null, AccountCode: prop.RoomRevenueAccountCode, ProductCode: productCodes.GetValueOrDefault(room.RoomTypeId)));
        foreach (var e in r.Extras)
            // Total ของบริการเสริมคูณด้วยคืน/คน ตามโหมด (ไม่ใช่ UnitPrice×Quantity ตรง ๆ) —
            // จึงลงเป็น 1 รายการยอดรวม ห้ามหาร Total/Quantity แล้วปัด (ยอดเพี้ยน 0.01)
            lines.Add(new(Description: $"{e.Name} ×{e.Quantity}", Quantity: 1, Unit: "รายการ", UnitPrice: e.Total,
                DiscountPercent: 0, VatRate: vatRate, WithholdingTaxRate: 0, AccountId: null,
                ProductCode: e.ProductId != null ? extraCodes.GetValueOrDefault(e.ProductId.Value) : null));
        foreach (var c in r.Charges.Where(c => c.Status == LodgingChargeStatus.Pending))
            lines.Add(new(Description: c.Description, Quantity: c.Quantity, Unit: "รายการ", UnitPrice: c.UnitPrice, DiscountPercent: 0,
                VatRate: c.VatRate, WithholdingTaxRate: 0, AccountId: null,
                ProductCode: c.ProductId != null ? extraCodes.GetValueOrDefault(c.ProductId.Value) : null));
        if (r.ServiceChargeAmount > 0)
            lines.Add(new(Description: $"Service charge {prop.ServiceChargePercent:0.##}%", Quantity: 1, Unit: "รายการ", UnitPrice: r.ServiceChargeAmount,
                DiscountPercent: 0, VatRate: vatRate, WithholdingTaxRate: 0, AccountId: null, AccountCode: prop.ServiceChargeAccountCode));
        if (lines.Count == 0) throw new BusinessRuleException("ไม่มีรายการให้ออกเอกสาร");

        string? depositRef = null;
        if (r.DepositPaid > 0 && r.DepositDocumentId != null)
            depositRef = await _db.Documents.AsNoTracking().Where(d => d.Id == r.DepositDocumentId && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefaultAsync();
        var applyDeposit = r.DepositPaid > 0 && depositRef != null;

        var docType = vatRate > 0 ? DocumentType.TaxInvoice : DocumentType.Invoice;
        var create = new CreateDocumentRequest(
            DocumentType: docType, DocumentDate: DateTime.UtcNow, DueDate: request.CollectBalanceNow ? null : DateTime.UtcNow.AddDays(30),
            ContactId: contactId, Reference: r.ReservationNumber,
            Notes: $"เข้าพัก {prop.Name} {r.CheckInDate:dd/MM/yyyy}–{r.CheckOutDate:dd/MM/yyyy} ({r.Nights} คืน) · จอง {r.ReservationNumber}",
            Lines: lines,
            DepositAppliedAmount: applyDeposit ? r.DepositPaid : null,
            DepositAppliedRef: applyDeposit ? depositRef : null,
            DepositAppliedDrivesJournal: applyDeposit ? true : null,
            PricesIncludeVat: prop.PricesIncludeVat,
            BankAccountId: request.BankAccountId,
            BranchId: prop.BranchId,
            BookingNumber: r.ReservationNumber,
            ServiceUsedDate: r.CheckOutDate,
            OriginModule: LodgingOrigin);
        var created = await _docService.CreateDocumentAsync(companyId, create, userId);
        var approved = await _docService.ApproveDocumentAsync(companyId, created.Id, userId, acknowledgeWarnings: true);

        decimal collected = 0;
        if (request.CollectBalanceNow && approved.BalanceDue > 0.005m)
        {
            await _docService.CreatePaymentAsync(companyId, new CreatePaymentRequest(
                DocumentId: approved.Id, PaymentDate: DateTime.UtcNow, Amount: approved.BalanceDue, PaymentMethod: request.PaymentMethod,
                Reference: request.PaymentReference, BankAccount: null, Notes: $"ชำระตอนเช็คเอาต์ {r.ReservationNumber}",
                OverrideBankAccountId: request.BankAccountId), userId);
            collected = approved.BalanceDue;
        }

        foreach (var c in r.Charges.Where(c => c.Status == LodgingChargeStatus.Pending)) c.Status = LodgingChargeStatus.Paid;
        r.FinalDocumentId = approved.Id;
        r.PaidAmount += collected;
        r.Status = LodgingReservationStatus.CheckedOut; r.CheckedOutAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Note)) AppendInternal(r, request.Note);
        if (!request.CollectBalanceNow && approved.BalanceDue > 0.005m)
            AppendInternal(r, $"เช็คเอาต์แบบเครดิต — ค้างชำระ {approved.BalanceDue:N2} บน {approved.DocumentNumber}");

        foreach (var room in r.Rooms.Where(x => x.Unit != null))
        {
            room.Unit!.HousekeepingStatus = LodgingHousekeepingStatus.VacantDirty;
            if (prop.AutoCreateHousekeepingTaskOnCheckout)
                _db.LodgingHousekeepingTasks.Add(new LodgingHousekeepingTask
                {
                    CompanyId = companyId, PropertyId = prop.Id, UnitId = room.Unit.Id, ReservationId = r.Id,
                    TaskType = LodgingHousekeepingTaskType.CheckoutClean, Priority = LodgingTaskPriority.Normal, Status = LodgingTaskStatus.Pending,
                    DueAt = DateTime.UtcNow.AddHours(2), EstimatedMinutes = prop.HousekeepingMinutesPerRoom, CreatedBy = userId,
                });
        }
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        await MeterStayAsync(companyId, r, "checkout");
        _db.AuditLogs.Add(Audit(companyId, AuditAction.Update, r, new
        {
            action = "CheckOut", finalDocument = approved.DocumentNumber, docType = docType.ToString(), depositApplied = applyDeposit ? r.DepositPaid : 0m,
            collected, balanceLeft = approved.BalanceDue - collected, by = userId,
        }));
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    /// <summary>เช็คเอาต์ของที่พักที่ตั้ง `AccountingMode = Off` — ปิดการเข้าพัก
    /// เก็บยอดที่รับจริงไว้บนการจอง แต่ไม่ออกเอกสารบัญชีใด ๆ (ลูกค้าลงบัญชีที่อื่น)
    /// มิเตอร์ `lodging.stay` ยังนับเท่าเดิม (§13.3)</summary>
    private async Task<LodgingReservationResponse> CheckOutWithoutDocumentAsync(
        Guid companyId, LodgingReservation r, LodgingCheckOutRequest request, string userId)
    {
        var vatRate = await EffectiveVatRateAsync(companyId, r.Property);
        if (request.DamageCharge is decimal dmg && dmg > 0)
        {
            r.Charges.Add(new LodgingFolioCharge
            {
                CompanyId = companyId, ReservationId = r.Id,
                Description = "ค่าเสียหาย/ของหาย" + (string.IsNullOrWhiteSpace(request.DamageDescription) ? "" : $" — {request.DamageDescription!.Trim()}"),
                Quantity = 1, UnitPrice = dmg, Total = Math.Round(dmg, 2, MidpointRounding.AwayFromZero), VatRate = vatRate,
                Source = LodgingChargeSource.System, Status = LodgingChargeStatus.Pending, ChargedAt = DateTime.UtcNow, CreatedBy = userId,
            });
            RecalcFolio(r);
        }
        var balance = Math.Max(0, r.TotalAmount + r.FolioTotal - r.PaidAmount);
        if (request.CollectBalanceNow && balance > 0) r.PaidAmount += balance;
        foreach (var c in r.Charges.Where(c => c.Status == LodgingChargeStatus.Pending)) c.Status = LodgingChargeStatus.Paid;
        r.Status = LodgingReservationStatus.CheckedOut; r.CheckedOutAt = DateTime.UtcNow;
        AppendInternal(r, $"เช็คเอาต์แบบไม่ออกเอกสาร (โหมด {r.Property.AccountingMode}) — ยอดรวม {r.TotalAmount + r.FolioTotal:N2}"
            + (request.CollectBalanceNow ? $" รับเพิ่ม {balance:N2}" : " ยังไม่รับส่วนที่เหลือ"));
        if (!string.IsNullOrWhiteSpace(request.Note)) AppendInternal(r, request.Note!);
        foreach (var room in r.Rooms.Where(x => x.Unit != null))
        {
            room.Unit!.HousekeepingStatus = LodgingHousekeepingStatus.VacantDirty;
            if (r.Property.AutoCreateHousekeepingTaskOnCheckout)
                _db.LodgingHousekeepingTasks.Add(new LodgingHousekeepingTask
                {
                    CompanyId = companyId, PropertyId = r.PropertyId, UnitId = room.Unit.Id, ReservationId = r.Id,
                    TaskType = LodgingHousekeepingTaskType.CheckoutClean, Priority = LodgingTaskPriority.Normal,
                    Status = LodgingTaskStatus.Pending, DueAt = DateTime.UtcNow.AddHours(2),
                    EstimatedMinutes = r.Property.HousekeepingMinutesPerRoom, CreatedBy = userId,
                });
        }
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        await MeterStayAsync(companyId, r, "checkout-nodoc");
        _db.AuditLogs.Add(Audit(companyId, AuditAction.Update, r, new
        { action = "CheckOutNoDocument", mode = r.Property.AccountingMode.ToString(), collected = request.CollectBalanceNow ? balance : 0m, by = userId }));
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    // ═══════════════════════════ Cancel / no-show ═══════════════════════════

    public async Task<LodgingReservationResponse> CancelAsync(Guid companyId, Guid reservationId, LodgingCancelRequest request, string userId, bool noShow = false)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (Terminal.Contains(r.Status)) throw new BusinessRuleException($"การจองอยู่ในสถานะ {StatusTh(r.Status)} แล้ว");
        if (r.Status == LodgingReservationStatus.CheckedIn) throw new BusinessRuleException("แขกเช็คอินแล้ว — ต้องเช็คเอาต์ (ออกบิล) แทนการยกเลิก");
        if (noShow && r.CheckInDate > DateTime.UtcNow.AddHours(7).Date) throw new BusinessRuleException("ยังไม่ถึงวันเช็คอิน — บันทึก no-show ไม่ได้");
        await CancelCoreAsync(companyId, r, request.Reason ?? (noShow ? "ไม่มาเข้าพัก" : "ยกเลิกโดยพนักงาน"), userId, noShow);
        return await MapAsync(companyId, r, true, true);
    }

    /// <summary>ยกเลิก: คิดค่าปรับจากนโยบายที่ตรึงไว้ ณ วันจอง → ส่วนที่คืน RefundDeposit · ส่วนที่ริบ RealizeDeposit (รายได้ริบมัดจำ)</summary>
    private async Task CancelCoreAsync(Guid companyId, LodgingReservation r, string reason, string actor, bool noShow)
    {
        decimal fee;
        if (noShow) fee = Math.Round(r.TotalAmount * r.Property.NoShowChargePercent / 100m, 2, MidpointRounding.AwayFromZero);
        else
        {
            var (rules, nonRefundable) = SnapshotRules(r);
            fee = LodgingPricingEngine.CancellationFee(r.TotalAmount, r.CheckInDate, DateTime.UtcNow.AddHours(7), rules, nonRefundable).Fee;
        }
        var deposit = r.DepositPaid;
        var forfeit = Math.Min(deposit, fee);
        var refund = Math.Max(0, deposit - forfeit);

        if (deposit > 0 && r.DepositDocumentId is Guid depId)
        {
            if (refund > 0.005m)
                await _docService.RefundDepositAsync(companyId, depId, new RefundDepositRequest(refund, DateTime.UtcNow, $"คืนมัดจำ — {(noShow ? "no-show" : "ยกเลิก")} {r.ReservationNumber}: {reason}"), actor);
            if (forfeit > 0.005m)
                await _docService.RealizeDepositAsync(companyId, depId, new RealizeDepositRequest(forfeit, DateTime.UtcNow, r.Property.CancellationFeeAccountCode ?? r.Property.RoomRevenueAccountCode), actor);
        }

        r.Status = noShow ? LodgingReservationStatus.NoShow : LodgingReservationStatus.Cancelled;
        r.CancelledAt = DateTime.UtcNow; r.CancellationReason = reason; r.CancellationFee = fee; r.RefundAmount = refund; r.HoldExpiresAt = null;
        r.PaidAmount = Math.Max(0, r.PaidAmount - refund);
        if (fee > deposit + 0.005m) AppendInternal(r, $"ค่าปรับตามนโยบาย {fee:N2} มากกว่ามัดจำที่รับ {deposit:N2} — ส่วนต่าง {fee - deposit:N2} ยังไม่ได้เรียกเก็บ");
        foreach (var room in r.Rooms) room.UnitId = null;
        // นับมิเตอร์เฉพาะการจองที่ "มีเงินเกี่ยวข้องจริง" — จองแล้วยกเลิกฟรีก่อนจ่ายมัดจำ
        // ต้องไม่ถูกคิด (ไม่งั้นการเปิดให้จองฟรีจะกลายเป็นกับดัก) · no-show คิดเสมอ
        // เพราะห้องถูกกันไว้จริงและมีค่าปรับตามนโยบาย
        if (noShow || deposit > 0 || fee > 0) await MeterStayAsync(companyId, r, noShow ? "no-show" : "cancel");
        _db.AuditLogs.Add(Audit(companyId, noShow ? AuditAction.Update : AuditAction.Delete, r, new { action = noShow ? "NoShow" : "Cancel", reason, fee, refund, forfeit, by = actor }));
        await _db.SaveChangesAsync();
        _logger.LogInformation("Lodging reservation {No} {Action}: fee {Fee} refund {Refund} forfeit {Forfeit}", r.ReservationNumber, noShow ? "no-show" : "cancelled", fee, refund, forfeit);
    }

    private static (IReadOnlyList<LodgingCancellationRule> Rules, bool NonRefundable) SnapshotRules(LodgingReservation r)
    {
        if (string.IsNullOrWhiteSpace(r.CancellationPolicySnapshotJson)) return (Array.Empty<LodgingCancellationRule>(), false);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.CancellationPolicySnapshotJson);
            var root = doc.RootElement;
            var nr = root.TryGetProperty("nonRefundable", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.True;
            var rules = root.TryGetProperty("rules", out var arr) ? LodgingPricingEngine.ParseRules(arr.GetRawText()) : Array.Empty<LodgingCancellationRule>();
            return (rules, nr);
        }
        catch { return (Array.Empty<LodgingCancellationRule>(), false); }
    }

    // ═══════════════════════════ Reschedule ═══════════════════════════

    public async Task<LodgingReservationResponse> RescheduleAsync(Guid companyId, Guid reservationId, LodgingRescheduleRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (r.Status != LodgingReservationStatus.Pending && r.Status != LodgingReservationStatus.Confirmed)
            throw new BusinessRuleException($"เลื่อนวันได้เฉพาะการจองที่ยังไม่เช็คอิน (สถานะ {StatusTh(r.Status)})");
        var checkIn = ThaiDate.CalendarDateUtc(request.CheckIn); var checkOut = ThaiDate.CalendarDateUtc(request.CheckOut);
        var ctx = await LoadContextAsync(companyId, r.PropertyId, checkIn, checkOut, excludeReservationId: r.Id);
        var rooms = r.Rooms.Select(x => new LodgingQuoteRoomRequest(x.RoomTypeId, x.Adults, x.Children, x.ExtraBeds)).ToList();
        var extras = r.Extras.Where(e => e.ExtraId != null).Select(e => new LodgingQuoteExtraRequest(e.ExtraId!.Value, e.Quantity)).ToList();
        var quote = BuildQuote(ctx, checkIn, checkOut, rooms, extras, r.RatePlanId, isStaff: true, excludeReservationId: r.Id);
        if (quote.Errors.Count > 0) throw new BusinessRuleException(string.Join(" · ", quote.Errors));

        var old = new { r.CheckInDate, r.CheckOutDate, r.TotalAmount };
        r.CheckInDate = checkIn; r.CheckOutDate = checkOut; r.Nights = quote.Nights;
        r.RoomSubtotal = quote.RoomSubtotal; r.ExtrasTotal = quote.ExtrasTotal; r.ServiceChargeAmount = quote.ServiceChargeAmount;
        r.VatAmount = quote.VatAmount; r.TotalAmount = quote.TotalAmount; r.PriceBreakdownJson = J(quote);
        // มัดจำที่รับแล้วคงเดิม (เงินอยู่ในบัญชีแล้ว) — เปลี่ยนเฉพาะ "ที่ต้องเรียกเก็บ" ถ้ายังไม่จ่าย
        if (r.DepositPaid <= 0) r.DepositRequired = quote.DepositRequired;
        var roomsById = r.Rooms.ToList();
        for (var i = 0; i < roomsById.Count && i < quote.Rooms.Count; i++)
        {
            roomsById[i].Subtotal = quote.Rooms[i].Subtotal; roomsById[i].NightlyRatesJson = J(quote.Rooms[i].Nights);
            roomsById[i].UnitId = null; roomsById[i].Unit = null;   // ห้องเดิมอาจไม่ว่างในช่วงใหม่ — ให้จัดใหม่
        }
        for (var i = 0; i < r.Extras.Count && i < quote.Extras.Count; i++) r.Extras.ElementAt(i).Total = quote.Extras[i].Total;
        AppendInternal(r, $"เลื่อนวัน {old.CheckInDate:dd/MM/yyyy}–{old.CheckOutDate:dd/MM/yyyy} → {checkIn:dd/MM/yyyy}–{checkOut:dd/MM/yyyy} · ยอด {old.TotalAmount:N2} → {r.TotalAmount:N2}{(string.IsNullOrWhiteSpace(request.Reason) ? "" : $" ({request.Reason})")}");
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        _db.AuditLogs.Add(Audit(companyId, AuditAction.Update, r, new { action = "Reschedule", old, @new = new { checkIn, checkOut, r.TotalAmount }, reason = request.Reason, by = userId }));
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }
}
