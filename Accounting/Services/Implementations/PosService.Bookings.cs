using Accounting.Helpers;
using Accounting.Models.DTOs.Pos;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// ทีม K (2026-10-22) — คิวการจองของวัน → เปิดบิล · ช่างรายบรรทัด · ยอดขายรายช่าง
///
/// <para>ตัวตัดสินทั้งหมดอยู่ใน <c>Helpers/</c> (<see cref="PosBookingIntake"/> · <see cref="PosStaffAttribution"/>) —
/// ไฟล์นี้แค่ดึงข้อมูล (กรอง <c>CompanyId</c> ทุก query) แล้วส่งเข้าตัวตัดสิน</para>
/// </summary>
public partial class PosService
{
    private static PosBookingState MapState(ReservationStatus s) => s switch
    {
        ReservationStatus.Pending => PosBookingState.Pending,
        ReservationStatus.Confirmed => PosBookingState.Confirmed,
        ReservationStatus.Seated => PosBookingState.InProgress,
        ReservationStatus.Completed => PosBookingState.Completed,
        ReservationStatus.Cancelled => PosBookingState.Cancelled,
        _ => PosBookingState.NoShow,
    };

    private static PosBookingState MapState(BookingStatus s) => s switch
    {
        BookingStatus.Pending => PosBookingState.Pending,
        BookingStatus.Confirmed => PosBookingState.Confirmed,
        BookingStatus.InProgress => PosBookingState.InProgress,
        BookingStatus.Completed => PosBookingState.Completed,
        BookingStatus.Cancelled => PosBookingState.Cancelled,
        BookingStatus.Waitlisted => PosBookingState.Waitlisted,
        _ => PosBookingState.NoShow,
    };

    /// <summary>ข้อมูลเตรียมเปิดบิลของการจองหนึ่งรายการ (ดึงครั้งเดียว ใช้ทั้งคิวและเปิดบิล)</summary>
    private sealed record BookingSnapshot(
        string Source, Guid Id, string? Number, string CustomerName, string? Phone, Guid? ContactId,
        DateTime StartAtUtc, int DurationMinutes, string? ServiceName, decimal? ServicePrice, Guid? ServiceProductId,
        string? TableNumber, int PartySize, Guid? StaffId, string? StaffName, PosBookingState State,
        string? RevenueDocNo, decimal DepositPaid);

    private async Task<List<BookingSnapshot>> LoadBookingSnapshotsAsync(Guid companyId, DateTime dayBangkok,
        string? onlySource = null, Guid? onlyId = null)
    {
        var day = dayBangkok.Date;
        var dayStartUtc = DateTime.SpecifyKind(day.AddHours(-7), DateTimeKind.Utc);
        var dayEndUtc = dayStartUtc.AddDays(1);
        var list = new List<BookingSnapshot>();

        // ── การจองโต๊ะ/คิวของ POS ──
        if (onlySource is null or PosBookingSource.PosReservation)
        {
            var q = _db.PosReservations.AsNoTracking().Where(r => r.CompanyId == companyId && !r.IsDeleted);
            q = onlyId is Guid rid ? q.Where(r => r.Id == rid) : q.Where(r => r.ReservedAt >= dayStartUtc && r.ReservedAt < dayEndUtc);
            var rows = await q.OrderBy(r => r.ReservedAt).ToListAsync();
            list.AddRange(rows.Select(r => new BookingSnapshot(
                PosBookingSource.PosReservation, r.Id, null, r.CustomerName, r.Phone, r.ContactId,
                DateTime.SpecifyKind(r.ReservedAt, DateTimeKind.Utc), r.DurationMinutes, null, null, null,
                r.TableNumber, r.PartySize, r.AssignedStaffId, r.AssignedStaffName, MapState(r.Status),
                null, r.DepositPaid ? r.DepositAmount : 0m)));
        }

        // ── การจองบริการจากเว็บไซต์ ──
        if (onlySource is null or PosBookingSource.CmsBooking)
        {
            var q = _db.SiteBookings.AsNoTracking().Where(b => b.CompanyId == companyId && !b.IsDeleted);
            q = onlyId is Guid bid ? q.Where(b => b.Id == bid)
                // BookingDate เป็น "วันที่" (เก็บได้ทั้งเที่ยงคืน UTC และเที่ยงคืนไทย) — ดึงกว้าง ±1 วันแล้วตัดด้วยปฏิทินไทยในหน่วยความจำ
                : q.Where(b => b.BookingDate >= day.AddDays(-1) && b.BookingDate < day.AddDays(2));
            var rows = await q
                .Select(b => new
                {
                    b.Id, b.BookingNumber, b.Status, b.BookingDate, b.StartTime, b.DurationMinutes,
                    b.GuestName, b.GuestPhone, b.GuestCount, b.TotalAmount, b.PaidAmount, b.ErpDocumentId,
                    b.AssignedStaffId, b.AssignedStaffName,
                    CustomerName = b.Customer != null ? b.Customer.FullName : null,
                    CustomerPhone = b.Customer != null ? b.Customer.Phone : null,
                    ContactId = b.Customer != null ? b.Customer.ContactId : null,
                    ServiceName = b.BookingService.Name,
                    ServiceProductId = b.BookingService.ProductId,
                })
                .ToListAsync();
            if (onlyId is null)
                rows = rows.Where(b => ThaiDate.CalendarDateUtc(b.BookingDate).Date == day).ToList();

            var docIds = rows.Where(b => b.ErpDocumentId.HasValue).Select(b => b.ErpDocumentId!.Value).Distinct().ToList();
            var docs = new Dictionary<Guid, (string? No, bool IsDeposit, DocumentType Type, DocumentStatus Status, decimal Total)>();
            if (docIds.Count > 0)
            {
                var docRows = await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && docIds.Contains(d.Id))
                    .Select(d => new { d.Id, d.DocumentNumber, d.IsDeposit, d.DocumentType, d.Status, d.TotalAmount })
                    .ToListAsync();
                foreach (var d in docRows)
                    docs[d.Id] = (d.DocumentNumber, d.IsDeposit, d.DocumentType, d.Status, d.TotalAmount);
            }

            foreach (var b in rows)
            {
                string? revenueDocNo = null;
                var depositPaid = b.PaidAmount > 0m ? b.PaidAmount : 0m;
                if (b.ErpDocumentId is Guid did && docs.TryGetValue(did, out var d))
                {
                    var dead = d.Status is DocumentStatus.Voided or DocumentStatus.Rejected;
                    if (!dead && d.IsDeposit)
                    {
                        if (depositPaid == 0m && d.Status is DocumentStatus.Approved or DocumentStatus.Paid or DocumentStatus.PartiallyPaid)
                            depositPaid = d.Total;
                    }
                    else if (!dead && d.Type != DocumentType.Quotation)
                    {
                        // เอกสารรายได้ของการจอง (ใบกำกับจากการจองแบบรับประกัน) — ร่างก็นับ: อนุมานทีหลัง = รายได้ซ้ำกับบิล POS
                        revenueDocNo = string.IsNullOrWhiteSpace(d.No) ? "(ร่าง)" : d.No;
                    }
                }
                var dateUtc = ThaiDate.CalendarDateUtc(b.BookingDate);
                var startUtc = DateTime.SpecifyKind(dateUtc.Date.Add(b.StartTime.ToTimeSpan()).AddHours(-7), DateTimeKind.Utc);
                list.Add(new BookingSnapshot(
                    PosBookingSource.CmsBooking, b.Id, b.BookingNumber,
                    (b.GuestName ?? b.CustomerName ?? "ลูกค้า").Trim(), b.GuestPhone ?? b.CustomerPhone, b.ContactId,
                    startUtc, b.DurationMinutes, b.ServiceName, b.TotalAmount, b.ServiceProductId,
                    null, Math.Max(1, b.GuestCount), b.AssignedStaffId, b.AssignedStaffName, MapState(b.Status),
                    revenueDocNo, depositPaid));
            }
        }
        return list.OrderBy(x => x.StartAtUtc).ToList();
    }

    /// <summary>บิลที่ยังไม่ยกเลิกของการจองแต่ละรายการ — ตัวจริงของ "เปิดแล้ว" (ตรงกับ unique index)</summary>
    private async Task<Dictionary<(string Type, Guid Id), (Guid OrderId, string Number)>> LoadLinkedOpenOrdersAsync(
        Guid companyId, IReadOnlyCollection<Guid> bookingIds)
    {
        if (bookingIds.Count == 0) return new();
        var rows = await _db.PosOrders.AsNoTracking()
            .Where(o => o.CompanyId == companyId && !o.IsDeleted && o.Status != PosOrderStatus.Voided
                && o.SourceBookingId != null && bookingIds.Contains(o.SourceBookingId.Value))
            .Select(o => new { o.Id, o.OrderNumber, o.SourceBookingType, o.SourceBookingId })
            .ToListAsync();
        var map = new Dictionary<(string, Guid), (Guid, string)>();
        foreach (var r in rows)
            map[(r.SourceBookingType ?? "", r.SourceBookingId!.Value)] = (r.Id, r.OrderNumber);
        return map;
    }

    public async Task<List<PosBookingQueueItem>> GetBookingQueueAsync(Guid companyId, DateTime dateBangkok)
    {
        var snaps = await LoadBookingSnapshotsAsync(companyId, dateBangkok);
        var linked = await LoadLinkedOpenOrdersAsync(companyId, snaps.Select(s => s.Id).ToList());
        return snaps.Select(s =>
        {
            linked.TryGetValue((s.Source, s.Id), out var lo);
            var hasLink = lo.OrderId != Guid.Empty;
            var d = PosBookingIntake.Decide(new PosBookingFacts(s.Source, s.State,
                hasLink ? lo.Number : null, s.RevenueDocNo, s.DepositPaid));
            return new PosBookingQueueItem(
                s.Source, s.Id, s.Number, s.CustomerName, s.Phone, s.StartAtUtc, s.DurationMinutes,
                s.ServiceName, s.ServicePrice, s.TableNumber, s.PartySize, s.StaffId, s.StaffName,
                s.State.ToString(), d.StatusLabel, d.CanOpen, d.BlockReason, d.Warning,
                hasLink ? lo.OrderId : null, hasLink ? lo.Number : null, s.DepositPaid);
        }).ToList();
    }

    public async Task<OrderResponse> OpenOrderFromBookingAsync(Guid companyId, OpenOrderFromBookingRequest request, string createdBy)
    {
        if (!PosBookingSource.IsKnown(request.Source))
            throw new BusinessRuleException("ไม่รู้จักแหล่งการจองนี้", "POS-BOOKING-OPEN");
        var snap = (await LoadBookingSnapshotsAsync(companyId, DateTime.UtcNow, request.Source, request.BookingId)).FirstOrDefault()
            ?? throw new KeyNotFoundException("ไม่พบการจองในบริษัทนี้");
        var linked = await LoadLinkedOpenOrdersAsync(companyId, new[] { snap.Id });
        linked.TryGetValue((snap.Source, snap.Id), out var lo);
        var decision = PosBookingIntake.Decide(new PosBookingFacts(snap.Source, snap.State,
            lo.OrderId != Guid.Empty ? lo.Number : null, snap.RevenueDocNo, snap.DepositPaid));
        if (!decision.CanOpen)
            throw new BusinessRuleException(decision.BlockReason ?? "เปิดบิลจากการจองนี้ไม่ได้", "POS-BOOKING-OPEN");

        // ช่าง: ที่แคชเชียร์เลือก > ที่จองไว้ · ต้องเป็นสมาชิกบริษัท (ResolveStaffAsync ตัวเดียวกับบรรทัด)
        var (staffId, staffName) = await ResolveStaffAsync(companyId, request.StaffId ?? snap.StaffId, lenient: false);

        var items = new List<CreateOrderItemRequest>();
        if (snap.Source == PosBookingSource.CmsBooking && !string.IsNullOrWhiteSpace(snap.ServiceName))
        {
            var price = snap.ServicePrice ?? 0m;
            var productOk = snap.ServiceProductId is Guid pid && await _db.Products.AsNoTracking()
                .AnyAsync(p => p.Id == pid && p.CompanyId == companyId && p.IsActive);
            items.Add(productOk
                ? new CreateOrderItemRequest(snap.ServiceProductId, null, snap.ServiceName!, null, 1m, null, price, StaffId: staffId)
                // บริการบนเว็บที่ไม่ได้ผูกสินค้า ⇒ รายการพิเศษชนิดบริการ (ไม่ตัดสต็อก · รายได้ขายมาตรฐาน) — ไม่แต่งสินค้าขึ้นมาเอง
                : new CreateOrderItemRequest(null, null, snap.ServiceName!, null, 1m, null, price,
                    StaffId: staffId, IsCustomItem: true, CustomItemKind: PosCustomItemRule.KindService));
        }

        var label = snap.Source == PosBookingSource.CmsBooking ? $"การจอง {snap.Number}" : "การจองโต๊ะ";
        var create = new CreateOrderRequest(
            request.SessionId,
            snap.Source == PosBookingSource.PosReservation ? PosOrderType.DineIn : PosOrderType.Appointment,
            snap.ContactId, snap.CustomerName, snap.TableNumber,
            snap.PartySize, null, snap.StartAtUtc, staffId,
            Notes: $"เปิดจาก{label}" + (decision.Warning != null ? $" · {decision.Warning}" : ""),
            Reference: snap.Number,
            Items: items);

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            OrderResponse created;
            try
            {
                created = await CreateOrderCoreAsync(companyId, create, createdBy, snap.Source, snap.Id);
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UX_PosOrders_SourceBooking_Open") == true)
            {
                // สองเครื่องกดพร้อมกัน — ฐานข้อมูลกันไว้ได้ใบเดียว
                throw new BusinessRuleException("การจองนี้เพิ่งถูกเปิดบิลจากอีกเครื่อง — รีเฟรชคิวแล้วเรียกบิลเดิม", "POS-BOOKING-OPEN");
            }

            if (snap.Source == PosBookingSource.PosReservation)
            {
                var r = await _db.PosReservations.FirstAsync(x => x.Id == snap.Id && x.CompanyId == companyId);
                r.Status = ReservationStatus.Seated;
                r.PosOrderId = created.Id;
                if (staffId != null) { r.AssignedStaffId = staffId; r.AssignedStaffName = staffName; }
                r.UpdatedAt = DateTime.UtcNow; r.UpdatedBy = createdBy;
            }
            else
            {
                var b = await _db.SiteBookings.FirstAsync(x => x.Id == snap.Id && x.CompanyId == companyId);
                if (b.Status is BookingStatus.Pending or BookingStatus.Confirmed) b.Status = BookingStatus.InProgress;
                b.PosOrderId = created.Id;
                if (staffId != null) { b.AssignedStaffId = staffId; b.AssignedStaffName = staffName; }
                b.UpdatedAt = DateTime.UtcNow; b.UpdatedBy = createdBy;
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return created;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>บิลจากการจองถูกยกเลิก ⇒ ปล่อยการจองให้เปิดบิลใหม่ได้ + สถานะถอยจาก "กำลังให้บริการ" กลับเป็น "ยืนยันแล้ว" ·
    /// ไม่แตะเงิน/มัดจำ (เส้นมัดจำเป็นของระบบจอง)</summary>
    private async Task ReleaseSourceBookingAsync(Guid companyId, PosOrder order)
    {
        if (order.SourceBookingId is not Guid bid) return;
        if (order.SourceBookingType == PosBookingSource.PosReservation)
        {
            var r = await _db.PosReservations.FirstOrDefaultAsync(x => x.Id == bid && x.CompanyId == companyId);
            if (r != null && r.PosOrderId == order.Id)
            {
                r.PosOrderId = null;
                // ยกเลิกบิลที่ปิดแล้ว ⇒ "เสร็จสิ้น" ที่ POS ประทับไว้ต้องถอยด้วย (ฝ่ายค้านทีม K ข้อ 9)
                if (r.Status is ReservationStatus.Seated or ReservationStatus.Completed) r.Status = ReservationStatus.Confirmed;
            }
        }
        else if (order.SourceBookingType == PosBookingSource.CmsBooking)
        {
            var b = await _db.SiteBookings.FirstOrDefaultAsync(x => x.Id == bid && x.CompanyId == companyId);
            if (b != null && b.PosOrderId == order.Id)
            {
                b.PosOrderId = null;
                if (b.Status == BookingStatus.InProgress) b.Status = BookingStatus.Confirmed;
                // POS ประทับ Completed เฉพาะการจองที่ไม่มีเงิน/เอกสารผูก (MarkSourceBookingCompletedAsync) ⇒ ถอยกลับได้ปลอดภัย
                else if (b.Status == BookingStatus.Completed && b.ErpDocumentId == null && b.PaidAmount <= 0m)
                {
                    b.Status = BookingStatus.Confirmed;
                    b.CompletedAt = null;
                }
            }
        }
    }

    /// <summary>ย้ายตัวชี้บิลบนการจองไปบิลใหม่ (รวมบิล)</summary>
    private async Task RelinkSourceBookingAsync(Guid companyId, string? type, Guid bookingId, Guid newOrderId)
    {
        if (type == PosBookingSource.PosReservation)
        {
            var r = await _db.PosReservations.FirstOrDefaultAsync(x => x.Id == bookingId && x.CompanyId == companyId);
            if (r != null) r.PosOrderId = newOrderId;
        }
        else if (type == PosBookingSource.CmsBooking)
        {
            var b = await _db.SiteBookings.FirstOrDefaultAsync(x => x.Id == bookingId && x.CompanyId == companyId);
            if (b != null) b.PosOrderId = newOrderId;
        }
    }

    /// <summary>บิลจากการจองปิดแล้ว ⇒ การจองโต๊ะ = เสร็จสิ้น · การจองจากเว็บ = เสร็จสิ้น<b>เฉพาะ</b>เมื่อไม่มีเงิน/เอกสารผูกกับการจอง
    /// (มีมัดจำ/เอกสาร ⇒ คงไว้ "กำลังให้บริการ" ให้คนปิดที่หน้าการจอง ซึ่งเป็นเส้นที่ตัดมัดจำ — POS ห้ามประทับ "เสร็จ" แทนเส้นเงิน)</summary>
    private async Task MarkSourceBookingCompletedAsync(Guid companyId, PosOrder order)
    {
        if (order.SourceBookingId is not Guid bid) return;
        // ฝ่ายค้านทีม K ข้อ 1: มัดจำถูกรับ<b>หลัง</b>เปิดบิล (ด่านตอนเปิดไม่เห็น) ⇒ ปิดบิลเต็มราคา = ลูกค้าจ่ายซ้ำ/รายได้ซ้ำ — บล็อกพร้อมทางไปต่อ
        if (order.SourceBookingType is string st)
        {
            var snap = (await LoadBookingSnapshotsAsync(companyId, DateTime.UtcNow, st, bid)).FirstOrDefault();
            if (snap != null && snap.DepositPaid > 0m)
                throw new BusinessRuleException(
                    $"การจองของบิลนี้มีมัดจำที่รับแล้ว {snap.DepositPaid:N2} บาท — POS ยังหักมัดจำในบิลไม่ได้ ปิดบิลเต็มราคาจะเก็บเงินซ้ำ · "
                    + "ทางไปต่อ: ยกเลิกบิลนี้แล้วปิดงานที่หน้าการจอง (ระบบตัดมัดจำให้) หรือคืนมัดจำที่หน้า \"มัดจำ\" ก่อน", "POS-BOOKING-DEPOSIT");
        }
        if (order.SourceBookingType == PosBookingSource.PosReservation)
        {
            var r = await _db.PosReservations.FirstOrDefaultAsync(x => x.Id == bid && x.CompanyId == companyId);
            if (r != null && r.Status is ReservationStatus.Seated or ReservationStatus.Confirmed or ReservationStatus.Pending)
            {
                r.Status = ReservationStatus.Completed;
                r.PosOrderId = order.Id;
            }
        }
        else if (order.SourceBookingType == PosBookingSource.CmsBooking)
        {
            var b = await _db.SiteBookings.FirstOrDefaultAsync(x => x.Id == bid && x.CompanyId == companyId);
            if (b != null && b.ErpDocumentId == null && b.PaidAmount <= 0m
                && b.Status is BookingStatus.InProgress or BookingStatus.Confirmed or BookingStatus.Pending)
            {
                b.Status = BookingStatus.Completed;
                b.CompletedAt = DateTime.UtcNow;
            }
        }
    }

    public async Task<OrderResponse> SetItemStaffAsync(Guid companyId, Guid orderId, Guid itemId, Guid? staffId)
    {
        var order = await _db.PosOrders.Include(o => o.Items).ThenInclude(i => i.ServiceActivities)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        // บิลที่ปิดแล้ว = ฐานคอมมิชชันถูกตรึง — แก้ทีหลังจะเปลี่ยนยอดที่อาจจ่ายไปแล้ว (บอกตรง ๆ ไม่ใช่ no-op)
        if (order.Status is PosOrderStatus.Completed or PosOrderStatus.Voided or PosOrderStatus.Refunded)
            throw new BusinessRuleException("บิลนี้ปิดแล้ว — เปลี่ยนช่างของรายการไม่ได้ (ฐานคอมมิชชันถูกตรึงตอนปิดบิล)", "POS-STAFF");
        var item = order.Items.FirstOrDefault(i => i.Id == itemId && !i.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการ");
        var (sid, name) = await ResolveStaffAsync(companyId, staffId, lenient: false);
        var previous = item.StaffId;
        item.StaffId = sid;
        item.StaffName = name;
        // ขั้นตอนบริการที่ยังไม่มีคนทำ หรือยังเป็นช่างเดิมของบรรทัด ⇒ ตามช่างใหม่ · ขั้นที่ตั้งคนอื่นไว้แล้วไม่แตะ
        foreach (var a in item.ServiceActivities.Where(a => !a.IsDeleted && (a.StaffId == null || a.StaffId == previous)))
        {
            a.StaffId = sid;
            a.StaffName = name;
        }
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, orderId);
    }

    public async Task<List<PosStaffOption>> GetStaffOptionsAsync(Guid companyId)
    {
        var rows = await _db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.User.Status == UserStatus.Active)
            .Select(cu => new { cu.UserId, cu.User.FullName })
            .ToListAsync();
        return rows.OrderBy(r => r.FullName).Select(r => new PosStaffOption(r.UserId, r.FullName)).ToList();
    }

    /// <summary>ข้อมูลดิบของบิลที่ปิดแล้วในช่วง (เวลาไทย) สำหรับ <see cref="PosStaffAttribution"/> — ตัวเดียวที่ทั้งรายงานและแผนคอมมิชชันใช้</summary>
    internal static async Task<(List<PosStaffOrder> Orders, Dictionary<Guid, string> Names)> LoadStaffSalesInputAsync(
        Accounting.Data.AccountingDbContext db, Guid companyId, DateTime fromUtc, DateTime toUtcExclusive, Guid? onlyStaffId = null)
    {
        var orders = await db.PosOrders.AsNoTracking()
            .Where(o => o.CompanyId == companyId && !o.IsDeleted
                && (o.Status == PosOrderStatus.Completed || o.Status == PosOrderStatus.Refunded)
                && o.CompletedAt >= fromUtc && o.CompletedAt < toUtcExclusive
                && o.Items.Any(i => !i.IsDeleted && i.StaffId != null && (onlyStaffId == null || i.StaffId == onlyStaffId)))
            .Select(o => new
            {
                o.DiscountAmount, o.TotalAmount, o.VatAmount,
                Items = o.Items.Where(i => !i.IsDeleted)
                    .Select(i => new { i.StaffId, i.StaffName, i.TotalAmount, i.Quantity, i.RefundedQuantity }).ToList(),
            })
            .ToListAsync();
        var names = new Dictionary<Guid, string>();
        var list = new List<PosStaffOrder>(orders.Count);
        foreach (var o in orders)
        {
            foreach (var i in o.Items)
                if (i.StaffId is Guid sid && !names.ContainsKey(sid) && !string.IsNullOrWhiteSpace(i.StaffName))
                    names[sid] = i.StaffName!;
            list.Add(new PosStaffOrder(o.Items.Sum(i => i.TotalAmount), o.DiscountAmount, o.TotalAmount, o.VatAmount,
                o.Items.Select(i => new PosStaffLine(i.StaffId, i.TotalAmount, i.Quantity, i.RefundedQuantity)).ToList()));
        }
        return (list, names);
    }

    public async Task<List<PosStaffSalesRow>> GetStaffSalesAsync(Guid companyId, DateTime from, DateTime to)
    {
        var fromUtc = ThaiDate.InstantFromUtc(from);
        var (bound, exclusive) = ThaiDate.InstantToUtc(to);
        var toExcl = exclusive ? bound : bound.AddTicks(1);
        var (orders, names) = await LoadStaffSalesInputAsync(_db, companyId, fromUtc, toExcl);
        return PosStaffAttribution.ByStaff(orders)
            .Select(s => new PosStaffSalesRow(s.StaffId, names.TryGetValue(s.StaffId, out var n) ? n : "พนักงาน", s.NetSales, s.LineCount))
            .ToList();
    }

    public async Task<PosCustomItemOptions> GetCustomItemOptionsAsync(Guid companyId)
    {
        var accounts = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive && a.AccountType == AccountType.Revenue)
            .WhereNotControlAccount(_db)
            .OrderBy(a => a.AccountCode)
            .Select(a => new PosRevenueAccountOption(a.Id, a.AccountCode, a.AccountName))
            .ToListAsync();
        var vatRate = await GetCompanyVatRateAsync(companyId);
        return new PosCustomItemOptions(
            PosCustomItemRule.Kinds.Select(k => new PosOptionValue(k.Value, k.Label)).ToList(),
            accounts,
            "รายได้จากการขาย (41000)",
            vatRate > 0
                ? $"ราคารวม VAT {vatRate:0.##}% ตามอัตราของบิล (รายการยกเว้น VAT รายบรรทัดยังไม่รองรับบน POS)"
                : "บริษัทไม่ได้จด VAT — ไม่คิด VAT");
    }
}
