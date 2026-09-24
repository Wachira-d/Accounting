using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Pos;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;   // EmailMessage / IEmailSenderFactory
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class PosService
{
    /// <summary>สาขา/คลังของเครื่องที่เปิดกะนี้ — ตรึงลงบิล **ตอนสร้าง** เท่านั้น
    ///
    /// <para>ห้าม resolve สดจาก terminal ตอนทำรายงาน: เครื่องย้ายสาขาได้ (ร้านย้าย
    /// แคชเชียร์ไปสาขาใหม่) แล้วยอดขายย้อนหลังจะย้ายตามไปทั้งก้อน — defect class
    /// เดียวกับ `IssuerBranchCode` บนเอกสารที่ §86/4 บังคับให้ตรึง</para></summary>
    private async Task<(Guid? BranchId, Guid? WarehouseId)> ResolveTerminalScopeAsync(
        Guid companyId, Guid sessionId)
    {
        var scope = await _db.PosSessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.CompanyId == companyId)
            .Select(s => new { s.Terminal.BranchId, s.Terminal.WarehouseId })
            .FirstOrDefaultAsync();
        return scope == null ? (null, null) : (scope.BranchId, scope.WarehouseId);
    }

    // ==================== Order CRUD ====================

    public async Task<OrderResponse> CreateOrderAsync(Guid companyId, CreateOrderRequest request, string createdBy)
    {
        var session = await _db.PosSessions.FirstOrDefaultAsync(s => s.Id == request.SessionId && s.CompanyId == companyId && s.Status == PosSessionStatus.Open)
            ?? throw new KeyNotFoundException("ไม่พบกะการขายที่เปิดอยู่");

        var posYm = Accounting.Helpers.ThaiDate.CalendarDateUtc(DateTime.UtcNow).ToString("yyyyMM");
        var posPrefix = $"POS-{posYm}-";
        var maxPos = await _db.PosOrders
            .IgnoreQueryFilters()
            .Where(o => o.CompanyId == companyId && o.OrderNumber.StartsWith(posPrefix))
            .Select(o => o.OrderNumber)
            .MaxAsync() as string;
        var posSeq = 1;
        if (maxPos != null)
        {
            var lastPart = maxPos.Substring(posPrefix.Length);
            if (int.TryParse(lastPart, out var parsed)) posSeq = parsed + 1;
        }
        var orderNumber = $"{posPrefix}{posSeq:D4}";
        var scope = await ResolveTerminalScopeAsync(companyId, request.SessionId);

        var order = new PosOrder
        {
            CompanyId = companyId,
            SessionId = request.SessionId,
            OrderNumber = orderNumber,
            BranchId = scope.BranchId,
            WarehouseId = scope.WarehouseId,
            OrderType = request.OrderType,
            CustomerId = request.CustomerId,
            CustomerName = request.CustomerName,
            TableNumber = request.TableNumber,
            GuestCount = request.GuestCount,
            QueueNumber = request.QueueNumber,
            AppointmentTime = request.AppointmentTime,
            PrimaryStaffId = request.PrimaryStaffId,
            DiscountPercent = request.DiscountPercent,
            ServiceChargePercent = request.ServiceChargePercent,
            Notes = request.Notes,
            Reference = request.Reference,
            CreatedBy = createdBy
        };
        _db.PosOrders.Add(order);
        await _db.SaveChangesAsync();

        // Add items if provided
        if (request.Items?.Count > 0)
        {
            var vatRate = await GetCompanyVatRateAsync(companyId);
            for (int i = 0; i < request.Items.Count; i++)
            {
                await AddItemToOrder(order, request.Items[i], i + 1, vatRate);
            }
            RecalculateOrder(order, vatRate);
            await _db.SaveChangesAsync();
        }

        return await GetOrderAsync(companyId, order.Id);
    }

    public async Task<OrderResponse> GetOrderAsync(Guid companyId, Guid orderId)
    {
        var order = await _db.PosOrders
            .Include(o => o.Items).ThenInclude(i => i.Modifiers)
            .Include(o => o.Items).ThenInclude(i => i.ServiceActivities)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        return MapOrder(order);
    }

    public async Task<PagedResponse<OrderResponse>> GetOrdersAsync(Guid companyId, PagedRequest request, Guid? sessionId = null, string? status = null)
    {
        var query = _db.PosOrders
            .Include(o => o.Items).Include(o => o.Payments)
            .Where(o => o.CompanyId == companyId);

        if (sessionId.HasValue) query = query.Where(o => o.SessionId == sessionId.Value);
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<PosOrderStatus>(status, true, out var s))
            query = query.Where(o => o.Status == s);
        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(o => o.OrderNumber.Contains(request.Search) || (o.CustomerName != null && o.CustomerName.Contains(request.Search)));

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(o => o.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToListAsync();

        return new PagedResponse<OrderResponse>(
            items.Select(MapOrder).ToList(), total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<OrderResponse> UpdateOrderAsync(Guid companyId, Guid orderId, UpdateOrderRequest request)
    {
        var order = await _db.PosOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status == PosOrderStatus.Completed || order.Status == PosOrderStatus.Voided)
            throw new InvalidOperationException("ไม่สามารถแก้ไขออเดอร์ที่เสร็จสิ้นหรือยกเลิกแล้ว");

        if (request.OrderType.HasValue) order.OrderType = request.OrderType.Value;
        if (request.CustomerId.HasValue) order.CustomerId = request.CustomerId;
        if (request.CustomerName != null) order.CustomerName = request.CustomerName;
        if (request.TableNumber != null) order.TableNumber = request.TableNumber;
        if (request.GuestCount.HasValue) order.GuestCount = request.GuestCount;
        if (request.QueueNumber != null) order.QueueNumber = request.QueueNumber;
        if (request.AppointmentTime.HasValue) order.AppointmentTime = request.AppointmentTime;
        if (request.PrimaryStaffId.HasValue) order.PrimaryStaffId = request.PrimaryStaffId;
        if (request.DiscountPercent.HasValue) order.DiscountPercent = request.DiscountPercent.Value;
        if (request.ServiceChargePercent.HasValue) order.ServiceChargePercent = request.ServiceChargePercent.Value;
        if (request.Notes != null) order.Notes = request.Notes;
        if (request.Reference != null) order.Reference = request.Reference;

        // Reload items to recalculate
        await _db.Entry(order).Collection(o => o.Items).LoadAsync();
        var vatRate = await GetCompanyVatRateAsync(companyId);
        RecalculateOrder(order, vatRate);
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, orderId);
    }

    public async Task<OrderResponse> UpdateOrderStatusAsync(Guid companyId, Guid orderId, UpdateOrderStatusRequest request, string userId)
    {
        var order = await _db.PosOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        // ★ E193-1 (รอบ 193) — เดิมเขียนสถานะตรง ๆ ⇒ ตั้ง Completed ได้โดยไม่ตัดสต็อก/ไม่ลง JE/
        // ไม่ออกเลข §86/6 และดึงบิลที่ปิดแล้วกลับมาปิดซ้ำได้ · ตอนนี้สลับได้เฉพาะสถานะที่ยังเปิด
        // สถานะปลายทางต้องไปเส้นของตัวเอง (ปิดบิล/ยกเลิก/คืนเงิน) — ตัดสินที่ helper ตัวเดียว
        var blocked = Accounting.Helpers.PosOrderStatusTransition.Check(order.Status, request.Status);
        if (blocked != null)
            throw new Accounting.Helpers.BusinessRuleException(blocked, "POS-STATUS-TRANSITION");
        order.Status = request.Status;
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, orderId);
    }

    public async Task VoidOrderAsync(Guid companyId, Guid orderId, string userId)
    {
        var order = await _db.PosOrders
            // ต้องมี Modifiers ด้วย — การคืนวัตถุดิบตามสูตรอ่านท็อปปิ้งที่ลูกค้าเลือก
            .Include(o => o.Items).ThenInclude(i => i.Modifiers)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status == PosOrderStatus.Voided) throw new InvalidOperationException("ออเดอร์นี้ถูกยกเลิกไปแล้ว");

        var wasCompleted = order.Status == PosOrderStatus.Completed;
        var liveItems = order.Items.Where(i => !i.IsDeleted).ToList();

        // ★ E193-2 (รอบ 193) — บิลที่คืนเงินไปบางส่วนแล้ว: กลับเฉพาะส่วนที่ยังค้าง
        // (สต็อก = จำนวนที่ยังไม่คืน · GL = กลับ JE ขาย + JE คืนเงินทุกใบ ⇒ สุทธิ = ส่วนที่เหลือ)
        // เดิมคืนสต็อกเต็มจำนวนและกลับ JE ขายทั้งใบ ⇒ ส่วนที่คืนไปแล้วถูกกลับซ้ำ
        var refundRef = $"REFUND-{order.OrderNumber}";
        var refundJournalIds = wasCompleted
            ? await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && j.Reference == refundRef
                         && j.Status == JournalEntryStatus.Posted && j.OriginalEntryId == null)
                .OrderBy(j => j.CreatedAt)
                .Select(j => j.Id)
                .ToListAsync()
            : new List<Guid>();
        var plan = Accounting.Helpers.PosVoidPlan.Decide(
            wasCompleted, order.JournalEntryId.HasValue,
            anyRefunded: liveItems.Any(i => i.RefundedQuantity > 0m),
            postedRefundJournalCount: refundJournalIds.Count);
        if (plan.Blocked)
            throw new Accounting.Helpers.BusinessRuleException(plan.BlockedMessage!, "POS-VOID-PARTIAL-REFUND");

        // ธุรกรรมชัดเจน — `IStockLedger` ล็อกด้วย `pg_advisory_xact_lock` ซึ่ง**ปล่อยทันที
        // ถ้าไม่มีธุรกรรมครอบ** (บทเรียน AdvisoryLockKey) ⇒ ไม่มีธุรกรรม = ไม่กันอะไรเลย
        await using var voidTxn = await _db.Database.BeginTransactionAsync();
        try
        {
            if (plan.RestoreRemainingStock)
            {
                // วัตถุดิบตามสูตรกลับเข้าคลังด้วยต้นทุน ณ วันขาย (JE ขายถูกกลับด้วยยอดวันขาย) — รอบ 193 M2
                var saleUnitCosts = await LoadSaleUnitCostsAsync(companyId, order);
                foreach (var item in liveItems.Where(i => i.ProductId.HasValue))
                {
                    var remaining = Accounting.Helpers.PosVoidPlan.RemainingQuantity(item.Quantity, item.RefundedQuantity);
                    if (remaining <= 0m) continue;          // คืนเงินครบบรรทัดแล้ว — สต็อกกลับไปตอนคืนเงิน
                    var product = await _db.Products.FindAsync(item.ProductId!.Value);
                    if (product == null) continue;
                    // บิลที่กินสูตรตอนขาย ต้อง **คืนวัตถุดิบ** ไม่ใช่คืนตัวสินค้าแม่ · บรรทัดจำลองที่มีแต่
                    // จำนวนที่เหลือ ให้ตัวคิดสูตรตัวเดียวกันคิดสัดส่วน (แบบเดียวกับเส้นคืนเงิน)
                    var rest = new PosOrderItem { Quantity = remaining };
                    foreach (var mod in item.Modifiers.Where(x => !x.IsDeleted)) rest.Modifiers.Add(mod);
                    var gaveBack = await ApplyRecipeConsumptionAsync(companyId, order, rest, product,
                        +1, DateTime.UtcNow, $"VOID-{order.OrderNumber}", "คืนวัตถุดิบจากการยกเลิกออเดอร์", userId,
                        saleUnitCosts);
                    if (!gaveBack.Handled && product.TrackStock)
                    {
                        await _stock.MoveAsync(new StockMoveRequest(
                            CompanyId: companyId,
                            ProductId: product.Id,
                            Quantity: remaining,              // + = คืนเข้าคลัง (เฉพาะที่ยังไม่คืน)
                            MovementType: "IN",
                            Reference: $"VOID-{order.OrderNumber}",
                            WarehouseId: order.WarehouseId,   // null = คลังหลัก (บริษัทที่ไม่ใช้ระบบคลัง)
                            PosOrderId: order.Id,
                            // ของกลับเข้าคลังด้วยต้นทุนเดียวกับที่ขายออกไป (★ E-01 · ตัวเดียวกับเส้นคืนเงิน)
                            UnitCostOverride: Accounting.Helpers.PosCogsBooking.RestockUnitCost(
                                item.CostOfGoodsSold, item.Quantity, EffectiveUnitCost(product)),
                            Notes: "คืนสต็อกจากการยกเลิกออเดอร์",
                            CreatedBy: userId));
                    }
                }
            }

            // ★ E193-3 (รอบ 193) — กลับ JE **ในธุรกรรมเดียวกับการคืนสต็อก/เปลี่ยนสถานะ** และไม่กลืน error
            // เดิมกลับ JE หลัง commit แล้ว catch → LogError ⇒ สต็อกกลับ บิลเป็น Voided แต่รายได้/
            // ภาษีขาย/เงินยังอยู่ใน GL โดยไม่มีใครเห็น (กฎเหล็ก #4 E) · ตอนนี้กลับไม่ได้ = ยกเลิกไม่สำเร็จ
            // ทั้งก้อน (บิลยังเป็นสถานะเดิม) · ReverseJournalEntryAsync ใช้ธุรกรรมของผู้เรียกเมื่อมีอยู่
            var journalsToReverse = new List<(Guid Id, string Label)>();
            if (plan.ReverseSaleJournal)
            {
                // ★ รอบ 193 (ฝ่ายค้าน M2) — JE ขายอาจถูกผู้ทำบัญชีกลับรายการด้วยมือไปก่อนแล้ว (JE ขาย POS
                // ไม่ผูกเอกสาร จึงกลับจากหน้า JE ได้) · เดิมกลับซ้ำ ⇒ ReverseJournalEntryAsync โยน ⇒ ยกเลิกบิล
                // ล้มทุกครั้งโดยไม่มีทางไปต่อ · ตอนนี้อ่านทั้งสาย แล้วให้ตัวตัดสินเดียวบอกว่า กลับ/ข้าม/บล็อก
                var chain = await LoadJournalChainAsync(companyId, order.JournalEntryId!.Value);
                var saleJe = Accounting.Helpers.PosVoidSaleJournal.Decide(chain, order.OrderNumber);
                switch (saleJe.Action)
                {
                    case Accounting.Helpers.PosVoidSaleJournalAction.Reverse:
                        journalsToReverse.Add((saleJe.ReverseEntryId!.Value, $"กลับรายการ POS ยกเลิกบิล #{order.OrderNumber}"));
                        break;
                    case Accounting.Helpers.PosVoidSaleJournalAction.SkipAlreadyReversed:
                        // ข้ามอย่างมีร่องรอย — ผู้สอบบัญชีต้องตอบได้ว่าทำไมยกเลิกบิลแล้วไม่มี JE กลับรายการ
                        // (ต้องอยู่ใน hash chain — AddChainedAuditLog · ฝ่ายค้าน C4: Add ตรง = RowHash null มองไม่เห็นตอน verify)
                        _db.AddChainedAuditLog(new AuditLog
                        {
                            CompanyId = companyId,
                            UserId = Guid.TryParse(userId, out var actorId) ? actorId : (Guid?)null,
                            Action = AuditAction.Update,
                            EntityType = "PosOrder.VoidSaleJournalSkipped",
                            EntityId = order.Id.ToString(),
                            NewValues = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                order.OrderNumber,
                                SaleJournalEntryId = order.JournalEntryId,
                                Chain = chain.Select(c => new { c.Id, c.EntryNumber, Status = c.Status.ToString() }),
                                Reason = saleJe.Message,
                            }),
                            Timestamp = DateTime.UtcNow,
                        });
                        _logger.LogInformation("ยกเลิกบิล POS {Order}: {Reason}", order.OrderNumber, saleJe.Message);
                        break;
                    default:
                        throw new Accounting.Helpers.BusinessRuleException(saleJe.Message, "POS-VOID-SALE-JE-PARTIAL");
                }
            }
            if (plan.ReverseRefundJournals)
                journalsToReverse.AddRange(refundJournalIds.Select(id =>
                    (id, $"กลับรายการคืนเงิน POS ยกเลิกบิล #{order.OrderNumber}")));
            foreach (var (jeId, label) in journalsToReverse)
            {
                try
                {
                    await _accountingService.ReverseJournalEntryAsync(companyId, jeId, DateTime.UtcNow, label, true);
                }
                catch (Exception ex) when (ex is not Accounting.Helpers.BusinessRuleException)
                {
                    // ไม่ใช่การกลืน — ห่อเป็นข้อความถึงผู้ใช้ที่บอกว่าบิลยังไม่ถูกยกเลิก แล้วโยนต่อ ⇒ rollback
                    throw new Accounting.Helpers.BusinessRuleException(
                        $"ยกเลิกบิล #{order.OrderNumber} ไม่สำเร็จ: กลับรายการบัญชีไม่ได้ ({ex.Message}) — "
                        + "บิลยังไม่ถูกยกเลิก สต็อกและบัญชีไม่เปลี่ยน · แก้สาเหตุ (เช่น เปิดงวดบัญชี) แล้วยกเลิกใหม่",
                        ex, "POS-VOID-JE-REVERSAL");
                }
            }

            order.Status = PosOrderStatus.Voided;
            await _db.SaveChangesAsync();
            await voidTxn.CommitAsync();
        }
        catch
        {
            await voidTxn.RollbackAsync();
            throw;
        }
    }

    public async Task<OrderResponse> RefundOrderAsync(Guid companyId, Guid orderId, RefundOrderRequest request, string userId)
    {
        var order = await _db.PosOrders
            .Include(o => o.Items).ThenInclude(i => i.Modifiers)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status != PosOrderStatus.Completed)
            throw new InvalidOperationException("คืนเงินได้เฉพาะออเดอร์ที่ปิดบิลแล้ว");
        if (request.Lines == null || request.Lines.Count == 0)
            throw new InvalidOperationException("กรุณาเลือกรายการที่จะคืนเงิน");

        // ลูกค้าจ่ายจริงตามยอดหลังหักส่วนลด/คูปองระดับบิล ไม่ใช่ยอดรวมรายบรรทัด
        // ดังนั้นต้องปรับสัดส่วนคืนเงินด้วย PosRefundMath.DiscountFactor มิฉะนั้นจะคืนเกิน
        // (เช่น สินค้า 1000 ลดทั้งบิล 10% ลูกค้าจ่าย 900 แต่ถ้าคืนเต็ม 1000 = คืนเกิน 100)
        // ★ รอบ 184 (คำตัดสินเจ้าของ "คืนทั้งหมด") — **ค่าบริการคืนไปกับของ**:
        // RecalculateOrder คิดค่าบริการจากยอด**หลังส่วนลด** แล้วบวกเข้า TotalAmount ⇒
        // ของทุกบาทถูกคิดเงิน (1 + ServiceChargePercent/100) บาท การคืนจึงคูณกลับด้วย
        // ตัวเดียวกัน · เดิมไม่คูณ ⇒ คืนทั้งใบได้แค่ยอดของ (บิล 990 คืน 900) = ร้านเก็บ
        // ค่าบริการของของที่ลูกค้าส่งคืนไว้เอง **และ** JE ขายเหลือรายได้/ภาษีขายค้าง
        // ที่ไม่มีวันถูกกลับรายการ
        // ทิป (Tip) ยังไม่คืน — เป็นหนี้สินที่ถือแทนพนักงาน (อาจจ่ายออกไปแล้ว) ต้องมีเส้นของตัวเอง
        // ค่าปัดเศษ (RoundingAmount) ไม่คืน — เศษระดับบิล ไม่ผูกบรรทัด (ดู doc-comment ของ helper)
        //
        // ★ D8-2 — ฐานของสัดส่วนต้องตรงกับฐานที่ RecalculateOrder ใช้คิดยอดที่เก็บจริง:
        //   • ส่วนลดระดับบิลคือ `DiscountAmount` **ตัวเดียว** (รวมคูปองไว้แล้วที่ :1745)
        //     เดิมบวก `CouponDiscountAmount` ซ้ำ ⇒ หักคูปองสองครั้ง ⇒ **คืนเงินลูกค้าต่ำกว่าจริง**
        //   • ยอดป้ายต้องนับเฉพาะบรรทัดที่ยังอยู่ (`!IsDeleted`) เหมือน RecalculateOrder —
        //     PosOrderItem ไม่มี global query filter ⇒ บรรทัดที่ถูกลบหลังคีย์ก็ไหลมาด้วย
        //     ⇒ ฐานใหญ่เกินจริง ⇒ สัดส่วนสูงเกิน ⇒ คืนเกิน
        // สูตรอยู่ที่ Helpers/PosRefundMath ตัวเดียว (เทสต์: PosRefundMathTests)
        var lineGrossTotal = order.Items.Where(i => !i.IsDeleted).Sum(i => i.TotalAmount);

        // Validate + collect the refund lines (proportional to each line).
        var refundLines = new List<Accounting.Helpers.PosRefundLine>();
        var toRestore = new List<(PosOrderItem Item, decimal Qty)>();
        // จำนวนที่ขอคืน**ในคำขอนี้**ต่อบรรทัด — คำขอที่ส่งบรรทัดเดียวกันซ้ำสองแถวต้องถูกนับรวม
        // (เดิมแต่ละแถวเทียบกับยอดคงเหลือเดิม ⇒ บรรทัดขาย 1 แก้วคืนได้ 2 ครั้งในคำขอเดียว =
        // คืนเงินเกิน + สต็อกกลับเกิน) · ทางไปต่อ: รวมเป็นแถวเดียวหรือส่งไม่เกินยอดคงเหลือ
        var askedInThisRequest = new Dictionary<Guid, decimal>();
        foreach (var line in request.Lines)
        {
            if (line.Quantity <= 0) continue;
            var item = order.Items.FirstOrDefault(i => i.Id == line.ItemId && !i.IsDeleted)
                ?? throw new InvalidOperationException("ไม่พบรายการในออเดอร์นี้");
            var askedBefore = askedInThisRequest.TryGetValue(item.Id, out var ab) ? ab : 0m;
            askedInThisRequest[item.Id] = askedBefore + line.Quantity;
            var remaining = item.Quantity - item.RefundedQuantity - askedBefore;
            if (line.Quantity > remaining + 0.0001m)
                throw new InvalidOperationException(
                    $"รายการ '{item.ItemName}' คืนได้ไม่เกิน {remaining:0.##} (ขอคืน {line.Quantity:0.##})");

            refundLines.Add(new Accounting.Helpers.PosRefundLine(
                LineGross: item.TotalAmount,
                LineVat: item.VatAmount,
                LineQuantity: item.Quantity,
                RefundQuantity: line.Quantity));
            toRestore.Add((item, line.Quantity));
        }
        if (toRestore.Count == 0)
            throw new InvalidOperationException("ไม่มีรายการที่จะคืนเงิน");

        var refund = Accounting.Helpers.PosRefundMath.Compute(
            lineGrossTotal, order.DiscountAmount, order.ServiceChargePercent, refundLines);
        var refundGross = refund.Gross;
        var refundVat = refund.Vat;
        var refundNet = refund.Net;

        // ★ E-01 (รอบ 193) — ต้นทุนที่กลับรายการ = **ส่วนของที่บิลขายลงไว้จริง**
        // (`PosOrderItem.CostOfGoodsSold`) ตามสัดส่วนจำนวน ไม่คิดใหม่จากต้นทุน/สูตรวันนี้ ·
        // เดิมบวก `gaveBack.TotalCost` (ต้นทุนวัตถุดิบเต็ม) ทั้งที่ JE ขายของเมนูชงสดลงไว้ 0
        // ⇒ ต้นทุนขายติดลบ · ต้องคิด **ก่อน** ขยับ RefundedQuantity (ใช้ยอดคืนสะสมก่อนคืน)
        // บิลเก่า (ไม่มีค่าตรึง) กลับตามสูตรขายเดิม — ตัดสินที่ Helpers/PosCogsBooking ตัวเดียว
        var refundNowByItem = toRestore.GroupBy(t => t.Item.Id)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Qty));
        var legacyProductIds = order.Items
            .Where(i => !i.IsDeleted && i.CostOfGoodsSold == null && i.ProductId.HasValue
                     && refundNowByItem.ContainsKey(i.Id))
            .Select(i => i.ProductId!.Value).Distinct().ToList();
        var legacyUnitCostByProduct = legacyProductIds.Count == 0
            ? new Dictionary<Guid, decimal>()
            : await _db.Products.AsNoTracking()
                .Where(p => p.CompanyId == companyId && legacyProductIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id,
                    p => Accounting.Helpers.PosCogsBooking.LegacyUnitCost(p.TrackStock, EffectiveUnitCost(p)));
        var refundCogs = Accounting.Helpers.PosCogsBooking.RefundCogs(order.Items
            .Where(i => !i.IsDeleted)
            .Select(i => new Accounting.Helpers.PosCogsRefundLine(
                BookedCost: i.CostOfGoodsSold,
                LineQuantity: i.Quantity,
                RefundedBefore: i.RefundedQuantity,
                RefundNow: refundNowByItem.TryGetValue(i.Id, out var now) ? now : 0m,
                LegacyUnitCost: i.ProductId is Guid pid && legacyUnitCostByProduct.TryGetValue(pid, out var lc) ? lc : 0m))
            .ToList());

        // ★ รอบ 193 หลังฝ่ายค้าน (P3) — ทางเข้าที่สองที่แตะ JE ขาย: ถ้าผู้ทำบัญชีกลับ JE ขายไปแล้ว การคืนเงินจะลง
        //   Dr รายได้/ภาษีขายซ้ำ ⇒ ติดลบ · ตัดสินด้วยตัวเดียวกับยกเลิกบิล (PosVoidSaleJournal) ไม่ตีความสายเอง
        if (order.JournalEntryId is Guid saleJeId)
        {
            var saleChain = await LoadJournalChainAsync(companyId, saleJeId);
            var refundBlock = Accounting.Helpers.PosVoidSaleJournal.RefundBlockMessage(
                Accounting.Helpers.PosVoidSaleJournal.Decide(saleChain, order.OrderNumber), order.OrderNumber);
            if (refundBlock != null)
                throw new Accounting.Helpers.BusinessRuleException(refundBlock, "POS-REFUND-SALE-JE-REVERSED");
        }

        // วัตถุดิบตามสูตรกลับเข้าคลังด้วยต้นทุน ณ วันขาย ⇒ มูลค่าคลังที่กลับ = COGS ที่ JE คืนเงินกลับ (รอบ 193 M2)
        var saleUnitCosts = await LoadSaleUnitCostsAsync(companyId, order);

        await using var txn = await _db.Database.BeginTransactionAsync();
        try
        {
            // Restore stock (ยอดต้นทุนที่กลับใน JE คิดไว้แล้วข้างบน — ไม่บวกจากผลคืนสต็อก)
            foreach (var (item, qty) in toRestore)
            {
                item.RefundedQuantity += qty;
                if (item.ProductId.HasValue)
                {
                    var product = await _db.Products.FindAsync(item.ProductId.Value);
                    if (product != null)
                    {
                        // คืนบางส่วน: สร้างบรรทัดจำลองที่มีเฉพาะจำนวนที่คืน เพื่อให้
                        // ตัวคิดสูตรตัวเดียวกันคำนวณสัดส่วนวัตถุดิบให้ (ห้ามเขียนสูตรซ้ำที่นี่)
                        var partial = new PosOrderItem { Quantity = qty };
                        foreach (var mod in item.Modifiers.Where(x => !x.IsDeleted)) partial.Modifiers.Add(mod);
                        var gaveBack = await ApplyRecipeConsumptionAsync(companyId, order, partial, product,
                            +1, DateTime.UtcNow, $"REFUND-{order.OrderNumber}",
                            "คืนวัตถุดิบจากการคืนเงิน POS", userId, saleUnitCosts);
                        if (!gaveBack.Handled && product.TrackStock)
                        {
                            await _stock.MoveAsync(new StockMoveRequest(
                                CompanyId: companyId,
                                ProductId: product.Id,
                                Quantity: qty,                    // + = คืนเข้าคลัง
                                MovementType: "IN",
                                Reference: $"REFUND-{order.OrderNumber}",
                                WarehouseId: order.WarehouseId,
                                PosOrderId: order.Id,
                                // ของกลับเข้าคลังด้วยต้นทุนที่ขายออกไป ⇒ มูลค่าคลัง = ยอดที่ JE กลับ
                                UnitCostOverride: Accounting.Helpers.PosCogsBooking.RestockUnitCost(
                                    item.CostOfGoodsSold, item.Quantity, EffectiveUnitCost(product)),
                                Notes: "คืนสินค้าจากการคืนเงิน POS",
                                CreatedBy: userId));
                        }
                    }
                }
            }

            // Reversal journal entry — back out the refunded portion:
            //   Dr รายได้ขาย / Dr ภาษีขาย   Cr เงินสด
            //   Dr สินค้าคงเหลือ            Cr ต้นทุนขาย
            await CreateRefundJournalEntryAsync(companyId, order, refundNet, refundVat, refundGross,
                refundCogs, request.RefundMethod, userId);

            // Record the cash-out as a negative payment so the shift/day
            // cash reconciliation reflects it.
            _db.Set<PosPayment>().Add(new PosPayment
            {
                OrderId = order.Id,
                PaymentMethod = request.RefundMethod,
                Amount = -refundGross,
                ReceivedAmount = 0,
                ChangeAmount = 0,
                ReferenceNo = "คืนเงิน" + (string.IsNullOrWhiteSpace(request.Reason) ? "" : ": " + request.Reason.Trim()),
                PaidAt = DateTime.UtcNow,
            });

            // Fully refunded → mark the order Refunded.
            // เฉพาะบรรทัดที่ยังอยู่ — บรรทัดที่ถูกลบไม่ได้ขาย (ไม่ถูกตัดสต็อก/ไม่อยู่ในยอด) จึงคืนไม่ได้
            // และต้องไม่กันบิลจากสถานะ "คืนครบ" (ฐานเดียวกับ lineGrossTotal ข้างบน)
            if (order.Items.Where(i => !i.IsDeleted).All(i => i.RefundedQuantity >= i.Quantity - 0.0001m))
                order.Status = PosOrderStatus.Refunded;

            await _db.SaveChangesAsync();
            await txn.CommitAsync();
        }
        catch
        {
            await txn.RollbackAsync();
            throw;
        }
        return await GetOrderAsync(companyId, orderId);
    }

    /// <summary>JE ของการคืนเงิน — ต้องเป็น**ภาพสะท้อน**ของ <see cref="CreateSalesJournalEntryAsync"/>
    /// ทุกด้าน: บัญชีเงินตามวิธีที่จ่ายคืน (ไม่ใช่ลิ้นชักเงินสดเสมอ) · รายได้ที่กลับรายการ
    /// รวมค่าบริการเหมือนที่ตอนขายเครดิตรวมไว้ · ภาษีขายกลับเท่าที่ลงไว้
    ///
    /// <para>★ รอบ 184 — เดิมเมธอดนี้ <c>return;</c> เงียบเมื่อหาผังไม่เจอ และ
    /// <c>catch</c> กลืน exception ทิ้ง ⇒ <b>เงินออกจากลิ้นชักจริงแต่ไม่มีรายการบัญชีเลย</b>
    /// ขณะที่ฝั่งขายของเมธอดคู่กัน <c>throw</c> ตั้งแต่รอบ H-A18 — "คู่สมมาตรที่แก้ข้างเดียว"
    /// (กฎเหล็ก #4 F2 ข้อ 1/7) · ตอนนี้ทั้งสองทางออกล้มดังเหมือนกัน และอยู่ใน transaction
    /// เดียวกับการคืนสต็อก ⇒ คืนไม่สำเร็จทั้งก้อน ดีกว่าคืนแล้ว GL ไม่รู้</para></summary>
    private async Task CreateRefundJournalEntryAsync(Guid companyId, PosOrder order,
        decimal refundNet, decimal refundVat, decimal refundGross, decimal refundCogs,
        PaymentMethod refundMethod, string userId)
    {
        // เครื่องที่เปิดกะของบิลใบนี้ — บัญชีเงินสด/ธนาคารที่ปักหมุดไว้บนเครื่องชนะผังมาตรฐาน
        // (เส้นขายใช้ resolver ตัวเดียวกัน · เดิมฝั่งคืนฮาร์ดโค้ด "11111" ⇒ คืนบัตรเครดิต
        // ก็ไปลดเงินสดของสาขา ⇒ กระทบยอดลิ้นชัก/ธนาคารรายสาขาไม่ตรงตลอดไป)
        var jeTerminal = await _db.PosSessions.AsNoTracking()
            .Where(x => x.Id == order.SessionId && x.CompanyId == companyId)
            .Select(x => x.Terminal)
            .FirstOrDefaultAsync();
        var cashAccount = await ResolvePaymentAccountAsync(companyId, refundMethod, jeTerminal);
        var salesAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "41000")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("41") && a.Level >= 4);
        var vatAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "21911")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("219") && a.Level >= 4);
        if (cashAccount == null)
            throw new Accounting.Helpers.BusinessRuleException(
                $"ไม่พบบัญชีเงินสด/ธนาคารสำหรับจ่ายคืนด้วยวิธี \"{PaymentMethodThaiLabel(refundMethod)}\" — "
                + "ตั้งค่าผังบัญชีของสาขา/เครื่องก่อนคืนเงิน (บิลยังอยู่ คืนใหม่ได้ทันทีที่ตั้งค่าเสร็จ)",
                "POS-NO-CASH-ACCOUNT");
        if (salesAccount == null)
            throw new Accounting.Helpers.BusinessRuleException(
                "ไม่พบบัญชีรายได้จากการขาย (41000) ในผังบัญชี — เพิ่มผังบัญชีก่อนคืนเงิน "
                + "มิฉะนั้นการคืนเงินจะไม่กลับรายการยอดขายในบัญชีแยกประเภท",
                "POS-NO-SALES-ACCOUNT");
        if (refundVat > 0 && vatAccount == null)
            throw new Accounting.Helpers.BusinessRuleException(
                "ไม่พบบัญชีภาษีขาย (21911) ในผังบัญชี — คืนเงินบิลที่มี VAT ไม่ได้ "
                + "เพราะภาษีขายที่ลงไว้ตอนขายจะค้างอยู่ทั้งก้อน (ภ.พ.30 นำส่งเกิน)",
                "POS-NO-VAT-ACCOUNT");

        var lines = new List<Models.DTOs.Accounting.JournalLineRequest>
        {
            new(salesAccount.Id, refundNet, 0, $"คืนรายได้ขาย POS #{order.OrderNumber}"),
        };
        if (refundVat > 0)
            lines.Add(new(vatAccount!.Id, refundVat, 0, $"คืนภาษีขาย POS #{order.OrderNumber}"));
        lines.Add(new(cashAccount.Id, 0, refundGross, $"จ่ายคืนเงิน POS #{order.OrderNumber}"));

        if (refundCogs > 0)
        {
            var cogsAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "51110")
                ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("511") && a.Level >= 4);
            var inventoryAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "11500")
                ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("115") && a.Level >= 4);
            if (cogsAccount != null && inventoryAccount != null)
            {
                lines.Add(new(inventoryAccount.Id, refundCogs, 0, $"รับคืนสินค้าคงเหลือ POS #{order.OrderNumber}"));
                lines.Add(new(cogsAccount.Id, 0, refundCogs, $"กลับต้นทุนขาย POS #{order.OrderNumber}"));
            }
        }

        try
        {
            var journalRequest = new Models.DTOs.Accounting.CreateJournalEntryRequest(
                DateTime.UtcNow, $"POS Refund #{order.OrderNumber}", $"REFUND-{order.OrderNumber}", lines,
                // มิติสาขาจาก snapshot บนบิล — ทำให้ P&L รายสาขาจาก
                // DimensionalAccountingService ตรงกับยอดขาย POS ของสาขานั้น
                BranchId: order.BranchId);
            var journal = await _accountingService.CreateJournalEntryAsync(companyId, journalRequest, userId);
            await _accountingService.PostJournalEntryAsync(companyId, journal.Id);
        }
        catch (Exception ex)
        {
            // ★ รอบ 184 — เดิมกลืนทิ้ง: เงินออกจากลิ้นชัก สต็อกกลับเข้าคลัง แต่ GL ไม่รู้เรื่อง
            // และไม่มีใครเห็นเพราะ LogError ไม่ใช่การล้มดัง (กฎเหล็ก #4 E/F2 ข้อ 7)
            // โยนต่อ ⇒ transaction ของ RefundOrderAsync rollback ทั้งก้อน
            _logger.LogError(ex, "POS refund journal creation failed for order {OrderNumber} in company {CompanyId}",
                order.OrderNumber, companyId);
            throw new Accounting.Helpers.BusinessRuleException(
                $"คืนเงินไม่สำเร็จ — ลงรายการบัญชีการคืนเงินของบิล #{order.OrderNumber} ไม่ได้ "
                + $"({ex.Message}) · ไม่มีเงินออกจากลิ้นชักและสต็อกไม่ถูกคืน แก้ผังบัญชี/งวดบัญชีแล้วลองใหม่",
                "POS-REFUND-JE-FAILED");
        }
    }

    public async Task<OrderResponse> IssueTaxInvoiceAsync(Guid companyId, Guid orderId, IssueTaxInvoiceRequest request, string userId)
    {
        var order = await _db.PosOrders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status != PosOrderStatus.Completed)
            throw new InvalidOperationException("ออกใบกำกับเต็มรูปได้เฉพาะออเดอร์ที่ปิดบิลแล้ว");
        if (order.DocumentId.HasValue)
            throw new InvalidOperationException("ออกใบกำกับภาษีไปแล้ว — ไม่สามารถออกซ้ำได้");
        // ★ รอบ 184 — บิลที่**คืนเงินไปบางส่วนแล้ว** ออกใบกำกับเต็มรูปไม่ได้:
        // ยอดที่ใบจะพิมพ์มาจาก order.TotalAmount/Items ซึ่ง **ไม่ขยับตามการคืนเงิน**
        // (RefundOrderAsync บวก RefundedQuantity + ลง JE กลับรายการ แต่ไม่แตะยอดบนบิล)
        // ⇒ ใบกำกับจะประกาศยอดเต็มทั้งที่เงินคืนไปแล้วบางส่วน = ผู้ซื้อเคลมภาษีซื้อเกิน
        // และภาษีขายบนกระดาษไม่ตรง GL · คืนทั้งใบไม่ต้องกันตรงนี้เพราะสถานะเป็น
        // Refunded แล้ว (ด่าน Completed ด้านบนดักไว้)
        // ทางไปต่อของผู้ใช้: ใบกำกับอย่างย่อ/ใบเสร็จของบิลยังใช้ได้ตามเดิม ถ้าผู้ซื้อ
        // ต้องการใบเต็มรูปให้ออกที่หน้าเอกสารพร้อมใบลดหนี้ของส่วนที่คืน (§86/10)
        if (order.Items.Any(i => !i.IsDeleted && i.RefundedQuantity > 0.0001m))
            throw new Accounting.Helpers.BusinessRuleException(
                "บิลนี้มีการคืนเงินบางส่วนแล้ว — ออกใบกำกับภาษีเต็มรูปจาก POS ไม่ได้ "
                + "เพราะยอดบนใบจะเป็นยอดก่อนคืน (ผู้ซื้อเคลมภาษีซื้อเกิน) · "
                + "ออกใบกำกับ + ใบลดหนี้ที่หน้าเอกสารแทน",
                "RD-86/4-POS-REFUNDED");
        var buyerName = (request.BuyerName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(buyerName))
            throw new InvalidOperationException("กรุณาระบุชื่อผู้ซื้อ");

        // ★ D8-1 — บริษัทที่ยังไม่จด VAT ออกใบกำกับไม่ได้เลย (§90/2 เป็นความผิดอาญา)
        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.IsVatRegistered })
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        // เลขใบที่ออกจริง — ต้องส่งกลับให้หน้าเว็บ (pos.html อ่าน `documentNumber`
        // มาตลอดแต่ MapOrder ส่ง null เสมอ = "หน้าเว็บอ่านฟิลด์ที่เซิร์ฟเวอร์ไม่เคยส่ง")
        string? issuedNumber = null;
        // ใบที่ออกจริง — ส่งต่อให้ผลข้างเคียงหลังออกเอกสาร (e-Tax อัตโนมัติ) หลัง commit (S-02)
        Models.Entities.Document? issuedDoc = null;

        await using var txn = await _db.Database.BeginTransactionAsync();
        try
        {
            // Resolve / create the buyer contact.
            var taxId = string.IsNullOrWhiteSpace(request.BuyerTaxId) ? null : request.BuyerTaxId!.Trim();
            var buyerBranch = string.IsNullOrWhiteSpace(request.BuyerBranchCode) ? null : request.BuyerBranchCode!.Trim();
            var buyerAddress = string.IsNullOrWhiteSpace(request.BuyerAddress) ? null : request.BuyerAddress!.Trim();

            Models.Entities.Contact? contact = null;
            // รอบ 193 ข้อ 20: คีย์เลขภาษี + สาขา (Helpers/ContactTaxBranchKey ตัวเดียวกับทุกทางเข้า) — ผู้ซื้อสาขา 8
            // ต้องไม่ได้ใบกำกับในนามแถวสำนักงานใหญ่ · สาขาว่าง = "ไม่ระบุ" (แถว สนญ. ก่อน) ตามความหมายเดิม
            var taxKey = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(_db.Contacts, companyId, taxId, buyerBranch);
            if (taxKey.ContactId is Guid keyId)
                contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == keyId && c.CompanyId == companyId);
            // ★ D8-1 — จับคู่ด้วย **ชื่อ** ได้เฉพาะตอนที่ยังไม่มีเลขภาษีมาชน: ชื่อซ้ำกันได้
            // (ชื่อเล่น/สาขา/บุคคลธรรมดาชื่อเหมือนกัน) การผูกใบกำกับเข้ากับผู้ติดต่อที่ถือ
            // **เลขภาษีคนละเลข** = ออกใบกำกับให้ผิดนิติบุคคล แก้ย้อนหลังไม่ได้ (§86/4)
            if (contact == null)
            {
                var byName = await _db.Contacts
                    .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Name == buyerName);
                if (byName != null && (taxId == null || string.IsNullOrWhiteSpace(byName.TaxId)))
                    contact = byName;
            }
            var isNewContact = contact == null;
            if (contact == null)
            {
                contact = new Models.Entities.Contact
                {
                    CompanyId = companyId,
                    Name = buyerName,
                    TaxId = taxId,
                    BranchCode = buyerBranch,
                    Address = buyerAddress,
                    IsCustomer = true,
                    IsActive = true,
                    CreatedBy = userId,
                };
            }
            else
            {
                contact.IsCustomer = true; // make sure the role is set
                // เติม**เฉพาะช่องที่ยังว่าง** — ห้ามทับข้อมูลที่ผู้ใช้เคยยืนยันไว้ด้วยสิ่งที่
                // แคชเชียร์พิมพ์หน้าเคาน์เตอร์ (ทิศเดียวกับบล็อก [Enrich] ของเส้น OCR)
                if (string.IsNullOrWhiteSpace(contact.TaxId) && taxId != null) contact.TaxId = taxId;
                if (string.IsNullOrWhiteSpace(contact.Address) && buyerAddress != null) contact.Address = buyerAddress;
                if (string.IsNullOrWhiteSpace(contact.BranchCode) && buyerBranch != null) contact.BranchCode = buyerBranch;
            }

            // ★ D8-1(ง) — ด่านออกใบกำกับเต็มรูป **ตัวเดียวกับเส้นเอกสาร**
            // (Helpers/FullTaxInvoiceReplacement เป็นเจ้าของกติกา: ไม่จด VAT · บิลไม่มี VAT ·
            // ผู้ซื้อไม่ครบ §86/4 · ออกไปแล้ว) — ห้ามเขียนสำเนาที่สองที่นี่ (F2 ข้อ 4)
            var missingBuyer = Tax.TaxInvoiceCompletenessChecker.MissingBuyerFields(contact);
            var eligibility = Accounting.Helpers.FullTaxInvoiceReplacement.Check(
                sourceIsIssued: order.Status == PosOrderStatus.Completed,
                sourceIsFullTaxInvoice: false,       // บิล POS ออกได้แค่ใบเสร็จ/อย่างย่อ
                sourceVatAmount: order.VatAmount,
                alreadyReplaced: order.DocumentId.HasValue,
                companyIsVatRegistered: company.IsVatRegistered,
                missingBuyerFields: missingBuyer);
            if (!eligibility.Allowed)
                throw new Accounting.Helpers.BusinessRuleException(
                    eligibility.Message ?? "ออกใบกำกับภาษีเต็มรูปจากบิลนี้ไม่ได้",
                    $"RD-86/4-POS-{eligibility.Reason}");

            if (isNewContact) _db.Contacts.Add(contact);

            // Build the Document directly — POS already posted its own JE for
            // this sale, so we must NOT go through ApproveDocumentAsync (would
            // create a duplicate JE). We stamp the JE's SourceDocumentId at
            // the end to suppress the VAT-report JE-fallback (avoids VAT
            // double-count: the Document path counts it, the JE path skips it).
            // เลขเอกสารใช้ yyyyMM ของ DocumentDate ให้สอดคล้องกัน
            // ★ H-A3: ต้องเป็น "วันตามปฏิทินไทย" ไม่ใช่วัน UTC — ร้านอาหาร/บาร์
            // ปิดบิลช่วง 00:00–07:00 ICT ยังเป็น **วันก่อนหน้า** ในเวลา UTC ⇒
            // ใบกำกับ + เลขชุด yyyyMM + JE ตกวัน/เดือนก่อน ⇒ ภ.พ.30 ผิดงวด
            // (DocumentService ใช้ ThaiDate.CalendarDateUtc มาตลอด — POS ตกหล่น)
            var posDocDate = Accounting.Helpers.ThaiDate.CalendarDateUtc(
                order.CompletedAt ?? DateTime.UtcNow);
            var docNumber = await Accounting.Helpers.DocumentNumberGenerator.NextAsync(
                _db, companyId, Models.Enums.DocumentType.TaxInvoice, posDocDate);

            // ★ D8-1(ค) — ใบย่อที่ลูกค้าถือไปแล้วต้องถูก**อ้างและประกาศว่าเรียกคืน**
            // บนใบเต็มรูป มิฉะนั้นการขายครั้งเดียวมีกระดาษสองใบที่ประกาศตัวเป็นใบกำกับ
            // ⇒ ผู้ซื้อเคลมภาษีซื้อได้สองรอบ (ข้อความอยู่ที่ FullTaxInvoiceReplacement)
            var abbreviated = string.IsNullOrWhiteSpace(order.AbbreviatedInvoiceNumber)
                ? null : order.AbbreviatedInvoiceNumber!.Trim();
            var replacedNote = abbreviated == null
                ? null
                : Accounting.Helpers.FullTaxInvoiceReplacement.ReplacementNote(abbreviated, posDocDate);

            var doc = new Models.Entities.Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = Models.Enums.DocumentType.TaxInvoice,
                DocumentDate = posDocDate,
                ContactId = contact.Id,
                Contact = contact,
                Status = Models.Enums.DocumentStatus.Approved,
                IsOpeningBalance = false,
                Reference = order.OrderNumber,
                // ★ D8-1(ข) — สาขาที่ออกใบ (§86/4(2)) มาจากบิลที่ผูก Branch ไว้ตั้งแต่ปิดบิล
                // · รหัสสาขาใช้ snapshot ที่ตรึงบนบิล **ห้ามเดา "00000"** (PosSlipHeader.BranchLabel)
                BranchId = order.BranchId,
                IssuerBranchCode = order.IssuerBranchCode,
                ReplacementReason = abbreviated == null ? null : $"แทนใบกำกับภาษีอย่างย่อ {abbreviated}",
                Notes = string.Join(" · ", new[]
                {
                    request.Notes?.Trim(),
                    $"ใบกำกับภาษีเต็มรูปจาก POS — ออเดอร์ {order.OrderNumber}",
                    replacedNote,
                }.Where(s => !string.IsNullOrWhiteSpace(s))),
                CreatedBy = userId,
            };

            // ★ D8-1(ก) — Σ บรรทัด ต้องเท่า **เงินที่ลูกค้าจ่ายสำหรับสินค้า/บริการ**
            // (ส่วนลดท้ายบิล · คูปอง · ค่าบริการ · ปัดเศษ — ทิปไม่อยู่บนใบกำกับ)
            // สูตรอยู่ที่ Helpers/PosTaxInvoiceLines ตัวเดียว (เทสต์: PosTaxInvoiceLinesTests)
            var lines = Accounting.Helpers.PosTaxInvoiceLines.Build(
                order.Items.Where(x => !x.IsDeleted).OrderBy(x => x.LineOrder)
                    .Select(x => new Accounting.Helpers.PosInvoiceSourceLine(
                        x.ItemName, x.ItemCode, x.Unit, x.Quantity, x.TotalAmount)),
                billDiscountAmount: order.DiscountAmount,
                couponAmount: order.CouponDiscountAmount,
                couponCode: order.CouponCode,
                serviceChargeAmount: order.ServiceChargeAmount,
                roundingAmount: order.RoundingAmount,
                headGrossPayable: Accounting.Helpers.PosTaxInvoiceLines.GrossPayableForGoods(
                    order.TotalAmount, order.RoundingAmount),
                headVat: order.VatAmount);

            var lineOrder = 1;
            foreach (var l in lines)
                doc.Lines.Add(new Models.Entities.DocumentLine
                {
                    LineOrder = lineOrder++,
                    ProductCode = l.ProductCode,
                    Description = l.Description,
                    Quantity = l.Quantity,
                    Unit = l.Unit ?? "ชิ้น",
                    UnitPrice = l.UnitPriceNet,
                    DiscountPercent = 0,
                    DiscountAmount = 0,
                    Amount = l.AmountNet,
                    VatRate = l.VatRate,
                    VatAmount = l.VatAmount,
                });

            doc.SubTotal = lines.Sum(l => l.AmountNet);
            doc.VatAmount = lines.Sum(l => l.VatAmount);
            // ★ รอบ 184 — ส่วนลดท้ายบิล/คูปอง/ปัดเศษลง ถูก**เฉลี่ยลงบรรทัด**แล้ว
            // (ห้ามบรรทัดติดลบ — `Helpers/DocumentLineKind`) ⇒ ยอดถูกต้องแต่ผู้ซื้อ
            // จะไม่เห็นว่ามีส่วนลด (บรรทัดถูกลดราคาลงเงียบ ๆ) · ตั้งช่องนี้เพื่อให้
            // renderer พิมพ์ "รวมก่อนหักท้ายบิล" + "ส่วนลดท้ายบิล" เหมือนใบที่คีย์มือ
            // ⚠️ ห้ามใส่ `DocumentLine.DiscountAmount` พร้อมกัน — ผู้ซื้อจะเห็นสองครั้ง
            doc.BillDiscountAmount = lines.Sum(l => l.DiscountNet);
            doc.TotalAmount = doc.SubTotal + doc.VatAmount;
            doc.BalanceDue = 0;                 // POS already collected payment
            doc.PaidAmount = doc.TotalAmount;
            // ★ D8-1(ข) — "ใบนี้เป็นใบกำกับตามกฎหมายไหม" ตัดสินด้วย resolver ตัวเดียว
            // ของระบบ (ห้ามเขียน true ดื้อ ๆ) แล้วตรึงไปพร้อมเลขที่ตาม §86/4
            doc.IsTaxInvoiceByLaw = Accounting.Helpers.TaxInvoiceSeriesPolicy
                .CarriesTaxInvoiceRole(doc, null);
            _db.Documents.Add(doc);
            await _db.SaveChangesAsync();

            // Link the order → document, and the JE → document so the VAT
            // report counts via the Document path (not the JE fallback).
            order.DocumentId = doc.Id;
            if (order.JournalEntryId.HasValue)
            {
                var je = await _db.JournalEntries.FirstOrDefaultAsync(j => j.Id == order.JournalEntryId.Value);
                if (je != null && je.SourceDocumentId == null) je.SourceDocumentId = doc.Id;
            }

            issuedNumber = doc.DocumentNumber;
            issuedDoc = doc;
            await _db.SaveChangesAsync();
            await txn.CommitAsync();
        }
        catch
        {
            await txn.RollbackAsync();
            throw;
        }

        // ★ รอบ 193 S-02 — ใบกำกับเต็มรูปจาก POS ประทับ Approved เอง (ไม่ผ่าน ApproveDocumentAsync)
        // ⇒ เดิมไม่เคยได้ e-Tax อัตโนมัติ แม้บริษัทเปิดไว้ · เรียกจุดเดียวกับเส้นเว็บ **หลัง commit**
        // (e-Tax ล้ม = ใบขายไม่ล้ม แต่ถูกประทับ [ETAX-AUTO-FAILED] บนตัวเอกสาร)
        // ไม่รวมวงเงินอนุมัติ/SoD/Budget — รอคำตัดสินเจ้าของ Q2
        if (issuedDoc != null) await _issuedHooks.RunAsync(companyId, issuedDoc);

        var response = await GetOrderAsync(companyId, orderId);
        return response with { DocumentNumber = issuedNumber };
    }

    public async Task<OrderResponse> SyncOfflineOrderAsync(Guid companyId, OfflineOrderRequest request, string createdBy)
    {
        // Idempotency: replays from the offline queue land here with the
        // same ClientOrderId. If we've already synced this sale, return it
        // — never create a duplicate.
        var existing = await _db.PosOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.CompanyId == companyId && o.ClientOrderId == request.ClientOrderId);
        if (existing != null) return await GetOrderAsync(companyId, existing.Id);

        var session = await _db.PosSessions.FirstOrDefaultAsync(s => s.Id == request.SessionId
                && s.CompanyId == companyId && s.Status == PosSessionStatus.Open)
            ?? throw new InvalidOperationException("กะที่บันทึกออเดอร์ออฟไลน์นี้ปิดไปแล้ว — ไม่สามารถ sync ได้");
        if (request.Items == null || request.Items.Count == 0)
            throw new InvalidOperationException("ออเดอร์ออฟไลน์ไม่มีรายการสินค้า");

        await using var txn = await _db.Database.BeginTransactionAsync();
        try
        {
            // Generate the official order number.
            var posYm = Accounting.Helpers.ThaiDate.CalendarDateUtc(DateTime.UtcNow).ToString("yyyyMM");
            var posPrefix = $"POS-{posYm}-";
            var maxPos = await _db.PosOrders.IgnoreQueryFilters()
                .Where(o => o.CompanyId == companyId && o.OrderNumber.StartsWith(posPrefix))
                .Select(o => o.OrderNumber).MaxAsync() as string;
            var posSeq = 1;
            if (maxPos != null)
            {
                var lastPart = maxPos.Substring(posPrefix.Length);
                if (int.TryParse(lastPart, out var parsed)) posSeq = parsed + 1;
            }

            var offlineScope = await ResolveTerminalScopeAsync(companyId, request.SessionId);
            var order = new PosOrder
            {
                CompanyId = companyId,
                SessionId = request.SessionId,
                OrderNumber = $"{posPrefix}{posSeq:D4}",
                BranchId = offlineScope.BranchId,
                WarehouseId = offlineScope.WarehouseId,
                OrderType = request.OrderType,
                CustomerId = request.CustomerId,
                CustomerName = request.CustomerName,
                TableNumber = request.TableNumber,
                QueueNumber = request.QueueNumber,
                DiscountPercent = request.DiscountPercent,
                Notes = request.Notes,
                ClientOrderId = request.ClientOrderId,
                Status = PosOrderStatus.Open,
                CreatedBy = createdBy,
            };
            _db.PosOrders.Add(order);
            await _db.SaveChangesAsync();

            // Items + totals — re-use the same helpers the online flow uses.
            var vatRate = await GetCompanyVatRateAsync(companyId);
            for (var i = 0; i < request.Items.Count; i++)
                await AddItemToOrder(order, request.Items[i], i + 1, vatRate);
            RecalculateOrder(order, vatRate);

            // Payments — fully provided by the client (they were collected at
            // sale time offline). Total must cover the order.
            decimal totalPaid = 0;
            foreach (var p in request.Payments ?? new List<OfflinePaymentRequest>())
            {
                var change = Math.Max(0, p.ReceivedAmount - p.Amount);
                order.Payments.Add(new PosPayment
                {
                    OrderId = order.Id,
                    PaymentMethod = p.PaymentMethod,
                    Amount = p.Amount,
                    ReceivedAmount = p.ReceivedAmount,
                    ChangeAmount = change,
                    ReferenceNo = p.ReferenceNo,
                    PaidAt = request.CompletedAt,
                });
                totalPaid += p.Amount;
            }
            if (totalPaid < order.NetAmount - 0.01m)
                throw new InvalidOperationException(
                    $"ยอดชำระออฟไลน์ ({totalPaid:N2}) ไม่ครบ ({order.NetAmount:N2})");

            order.Status = PosOrderStatus.Completed;
            order.CompletedAt = request.CompletedAt;
            await IssueAbbreviatedInvoiceNumberAsync(companyId, order);

            // Stock + GL — ตัวเดียวกับ CompleteOrderAsync และลำดับเดียวกัน (★ E-01):
            // ตัดสต็อกก่อนเพื่อให้ JE ได้ต้นทุนที่ออกจากคลังจริง
            var saleCogs = await DeductSaleStockAsync(companyId, order, request.CompletedAt,
                "วัตถุดิบตามสูตร POS (offline sync)", "POS Sale (offline sync)", createdBy);
            await CreateSalesJournalEntryAsync(companyId, order, createdBy, saleCogs);

            await _db.SaveChangesAsync();
            await txn.CommitAsync();
            return await GetOrderAsync(companyId, order.Id);
        }
        catch
        {
            await txn.RollbackAsync();
            throw;
        }
    }

    // ==================== Order Items ====================

    public async Task<OrderResponse> AddOrderItemAsync(Guid companyId, Guid orderId, CreateOrderItemRequest request)
    {
        var order = await _db.PosOrders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status == PosOrderStatus.Completed || order.Status == PosOrderStatus.Voided)
            throw new InvalidOperationException("ไม่สามารถเพิ่มรายการในออเดอร์ที่เสร็จสิ้น");

        var nextLine = order.Items.Count + 1;
        var vatRate = await GetCompanyVatRateAsync(companyId);
        await AddItemToOrder(order, request, nextLine, vatRate);
        RecalculateOrder(order, vatRate);
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, orderId);
    }

    public async Task RemoveOrderItemAsync(Guid companyId, Guid orderId, Guid itemId)
    {
        var order = await _db.PosOrders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        var item = order.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new KeyNotFoundException("ไม่พบรายการ");
        item.IsDeleted = true;
        var vatRate = await GetCompanyVatRateAsync(companyId);
        RecalculateOrder(order, vatRate);
        await _db.SaveChangesAsync();
    }

    /// <summary>Set a new quantity on an existing line. Used by the POS UI's
    /// +/- buttons after the order has been created on the server — keeps the
    /// local cart and the server in sync so subtotal/VAT don't drift.</summary>
    public async Task<OrderResponse> UpdateOrderItemQuantityAsync(Guid companyId, Guid orderId, Guid itemId, decimal newQuantity)
    {
        if (newQuantity < 0) throw new ArgumentException("จำนวนต้องไม่ติดลบ");
        var order = await _db.PosOrders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId && !o.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status == PosOrderStatus.Completed || order.Status == PosOrderStatus.Voided)
            throw new InvalidOperationException("ออเดอร์ปิดบิลหรือยกเลิกแล้ว — แก้ไขจำนวนไม่ได้");
        var item = order.Items.FirstOrDefault(i => i.Id == itemId && !i.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการ");

        if (newQuantity == 0)
        {
            item.IsDeleted = true;
        }
        else
        {
            item.Quantity = newQuantity;
            // Recompute per-line totals; VAT is computed at order level by RecalculateOrder.
            item.SubTotal = Math.Round(item.UnitPrice * newQuantity - item.DiscountAmount, 2, MidpointRounding.AwayFromZero);
            item.TotalAmount = item.SubTotal;
        }

        var vatRate = await GetCompanyVatRateAsync(companyId);
        RecalculateOrder(order, vatRate);
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, orderId);
    }

    /// <summary>Apply a per-line discount. Either DiscountAmount (฿) or DiscountPercent
    /// (%) can be set; passing percent re-computes amount from quantity × unit price.
    /// Triggers RecalculateOrder so VAT / Service Charge / Net adjust correctly.</summary>
    public async Task<OrderResponse> SetItemDiscountAsync(Guid companyId, Guid orderId, Guid itemId, decimal? discountAmount, decimal? discountPercent)
    {
        var order = await _db.PosOrders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId && !o.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status == PosOrderStatus.Completed || order.Status == PosOrderStatus.Voided)
            throw new InvalidOperationException("ออเดอร์ปิดบิลหรือยกเลิกแล้ว — แก้ไขส่วนลดไม่ได้");
        var item = order.Items.FirstOrDefault(i => i.Id == itemId && !i.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการ");

        var gross = Math.Round(item.UnitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero);
        if (discountPercent.HasValue)
        {
            if (discountPercent.Value < 0 || discountPercent.Value > 100)
                throw new ArgumentException("ส่วนลดเปอร์เซ็นต์ต้องอยู่ระหว่าง 0-100");
            item.DiscountPercent = discountPercent.Value;
            item.DiscountAmount = Math.Round(gross * discountPercent.Value / 100m, 2, MidpointRounding.AwayFromZero);
        }
        else if (discountAmount.HasValue)
        {
            if (discountAmount.Value < 0) throw new ArgumentException("ส่วนลดต้องไม่ติดลบ");
            if (discountAmount.Value > gross) throw new ArgumentException("ส่วนลดเกินยอดรายการ");
            item.DiscountAmount = discountAmount.Value;
            item.DiscountPercent = gross > 0 ? Math.Round(discountAmount.Value * 100 / gross, 2) : 0;
        }
        else
        {
            // No args = clear discount.
            item.DiscountAmount = 0;
            item.DiscountPercent = 0;
        }

        item.SubTotal = Math.Round(gross - item.DiscountAmount, 2, MidpointRounding.AwayFromZero);
        item.TotalAmount = item.SubTotal;
        var vatRate = await GetCompanyVatRateAsync(companyId);
        RecalculateOrder(order, vatRate);
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, orderId);
    }

    /// <summary>Set the tip amount on an open order. Re-runs RecalculateOrder so
    /// NetAmount (what the customer hands over) reflects tip on top of bill.</summary>
    public async Task<OrderResponse> SetTipAsync(Guid companyId, Guid orderId, decimal tipAmount)
    {
        if (tipAmount < 0) throw new ArgumentException("ทิปต้องไม่ติดลบ");
        var order = await _db.PosOrders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId && !o.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status == PosOrderStatus.Completed || order.Status == PosOrderStatus.Voided)
            throw new InvalidOperationException("ออเดอร์ปิดบิลหรือยกเลิกแล้ว — เพิ่ม/แก้ทิปไม่ได้");
        order.TipAmount = Math.Round(tipAmount, 2, MidpointRounding.AwayFromZero);
        var vatRate = await GetCompanyVatRateAsync(companyId);
        RecalculateOrder(order, vatRate);
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, orderId);
    }

    /// <summary>Apply a coupon code. Resolves against active CmsCoupons first
    /// (cross-channel — same codes the storefront accepts work in POS too), and
    /// records the absolute discount amount as CouponDiscountAmount. Empty code
    /// clears the coupon.</summary>
    public async Task<OrderResponse> ApplyCouponAsync(Guid companyId, Guid orderId, string? code)
    {
        var order = await _db.PosOrders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId && !o.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status == PosOrderStatus.Completed || order.Status == PosOrderStatus.Voided)
            throw new InvalidOperationException("ออเดอร์ปิดบิลหรือยกเลิกแล้ว — เปลี่ยนคูปองไม่ได้");

        var vatRate = await GetCompanyVatRateAsync(companyId);
        var subtotal = order.Items.Where(i => !i.IsDeleted).Sum(i => i.SubTotal);

        if (string.IsNullOrWhiteSpace(code))
        {
            order.CouponCode = null;
            order.CouponDiscountAmount = 0;
            RecalculateOrder(order, vatRate);
            await _db.SaveChangesAsync();
            return await GetOrderAsync(companyId, orderId);
        }

        var trimmedCode = code.Trim().ToUpperInvariant();
        var now = DateTime.UtcNow;
        // SiteCoupon is the canonical coupon table — cross-channel by design,
        // shared between storefront and POS. Filter by company so cashier can't
        // use another company's code accidentally.
        var coupon = await _db.Set<SiteCoupon>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId
                && c.Code.ToUpper() == trimmedCode
                && c.IsActive
                && !c.IsDeleted
                && (c.StartsAt == null || c.StartsAt <= now)
                && (c.ExpiresAt == null || c.ExpiresAt >= now)
                && (c.MaxUses == null || c.CurrentUses < c.MaxUses));
        if (coupon == null)
            throw new InvalidOperationException($"คูปอง \"{trimmedCode}\" ไม่ถูกต้อง / หมดอายุ / หมดสิทธิ์ใช้");

        if (coupon.MinOrderAmount.HasValue && subtotal < coupon.MinOrderAmount.Value)
            throw new InvalidOperationException($"ยอดขั้นต่ำสำหรับคูปองนี้ {coupon.MinOrderAmount:N2} บาท");

        decimal discount = coupon.DiscountType switch
        {
            CouponDiscountType.Percentage  => Math.Round(subtotal * coupon.DiscountValue / 100m, 2, MidpointRounding.AwayFromZero),
            CouponDiscountType.FixedAmount => coupon.DiscountValue,
            CouponDiscountType.FreeShipping => 0, // POS = walk-in; free shipping doesn't apply
            _ => 0
        };
        if (coupon.MaxDiscountAmount.HasValue && discount > coupon.MaxDiscountAmount.Value)
            discount = coupon.MaxDiscountAmount.Value;
        if (discount > subtotal) discount = subtotal;

        order.CouponCode = trimmedCode;
        order.CouponDiscountAmount = discount;
        RecalculateOrder(order, vatRate);
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, orderId);
    }

    /// <summary>Split an open order into N child orders. Each entry in
    /// <paramref name="itemGroups"/> is the list of item-ids that belongs to
    /// that check. Items not included are kept on the parent. We create child
    /// orders with copies of the original items (qty / price / discount intact)
    /// then void the items left behind on the parent.
    /// Returns parent + children in one list so the cashier can decide which to
    /// keep open. Payments cannot have been recorded on the source order.</summary>
    public async Task<List<OrderResponse>> SplitOrderAsync(Guid companyId, Guid orderId, List<List<Guid>> itemGroups, string userId)
    {
        if (itemGroups == null || itemGroups.Count == 0)
            throw new ArgumentException("ต้องระบุการแบ่งอย่างน้อย 1 บิล");
        var parent = await _db.PosOrders
            .Include(o => o.Items).ThenInclude(i => i.Modifiers)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId && !o.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (parent.Status == PosOrderStatus.Completed || parent.Status == PosOrderStatus.Voided)
            throw new InvalidOperationException("ออเดอร์ปิดบิลหรือยกเลิกแล้ว — แยกบิลไม่ได้");
        if (parent.Payments.Any(p => !p.IsDeleted))
            throw new InvalidOperationException("มีการชำระเงินแล้ว — ยกเลิกการชำระก่อนแยกบิล");
        // ส่วนลด/ค่าบริการที่เป็น **เปอร์เซ็นต์** แยกบิลได้ตรง ๆ (สัดส่วนคงที่
        // ต่อยอดของแต่ละใบ ⇒ ผลรวมเท่าเดิมเป๊ะ) แต่ค่าที่เป็น **จำนวนเงินก้อน**
        // (คูปอง/ทิป) แบ่งไม่ได้โดยไม่เดา: จะยกไปใบไหน? เฉลี่ยตามสัดส่วน?
        // ทั้งสองทางเป็นการตัดสินใจแทนร้าน และคูปองที่ไปโผล่หลายใบยังเสี่ยง
        // ถูกใช้ซ้ำ → บอกให้ถอดก่อน แล้วค่อยใส่กับใบที่ถูกต้องหลังแยก
        // (หลัก "ไม่รู้ = ต้องบอกว่าไม่รู้ ห้ามแต่งค่าเอง")
        if (parent.CouponDiscountAmount > 0)
            throw new InvalidOperationException(
                $"บิลนี้ใช้คูปองอยู่ (ส่วนลด {parent.CouponDiscountAmount:N2} บาท) — ถอดคูปองก่อนแยกบิล แล้วค่อยใส่คูปองกับใบที่ต้องการ");
        if (parent.TipAmount > 0)
            throw new InvalidOperationException(
                $"บิลนี้มีทิป {parent.TipAmount:N2} บาท — ล้างทิปก่อนแยกบิล แล้วค่อยใส่ทิปกับใบที่ลูกค้าจ่าย");

        // Validate: every requested item id belongs to the parent + no item assigned twice.
        var validIds = parent.Items.Where(i => !i.IsDeleted).Select(i => i.Id).ToHashSet();
        var seen = new HashSet<Guid>();
        foreach (var group in itemGroups)
        {
            foreach (var id in group)
            {
                if (!validIds.Contains(id)) throw new ArgumentException($"รายการ {id} ไม่อยู่ในออเดอร์ต้นทาง");
                if (!seen.Add(id)) throw new ArgumentException($"รายการ {id} ถูกระบุซ้ำในการแยกบิล");
            }
        }

        var vatRate = await GetCompanyVatRateAsync(companyId);
        var posYm = Accounting.Helpers.ThaiDate.CalendarDateUtc(DateTime.UtcNow).ToString("yyyyMM");
        var posPrefix = $"POS-{posYm}-";

        // Compute next number once, then increment per child to avoid round trips.
        var maxPos = await _db.PosOrders
            .IgnoreQueryFilters()
            .Where(o => o.CompanyId == companyId && o.OrderNumber.StartsWith(posPrefix))
            .Select(o => o.OrderNumber)
            .MaxAsync() as string;
        var seq = 1;
        if (maxPos != null && int.TryParse(maxPos.Substring(posPrefix.Length), out var parsed)) seq = parsed + 1;

        var children = new List<PosOrder>();
        var splitIdx = 1;
        foreach (var group in itemGroups)
        {
            if (group.Count == 0) { splitIdx++; continue; }
            var child = new PosOrder
            {
                CompanyId = companyId,
                SessionId = parent.SessionId,
                OrderNumber = $"{posPrefix}{seq:D4}",
                // สืบทอดจากใบแม่ ไม่ resolve ใหม่จากเครื่อง — ใบลูกต้องอยู่สาขา/คลัง
                // เดียวกับใบที่มันแยกออกมาเสมอ แม้เครื่องจะย้ายสาขาไปแล้ว
                BranchId = parent.BranchId,
                WarehouseId = parent.WarehouseId,
                OrderType = parent.OrderType,
                CustomerName = parent.CustomerName,
                TableNumber = parent.TableNumber == null ? null : $"{parent.TableNumber}/{splitIdx}",
                Notes = $"แยกจาก {parent.OrderNumber} (ส่วนที่ {splitIdx})",
                Reference = parent.OrderNumber,
                Status = PosOrderStatus.Open,
                // ⚠️ ต้องสืบทอด: เดิมใบลูกเกิดมาด้วยค่า 0 ทั้งคู่ แล้ว
                // RecalculateOrder(child) คิดจาก 0 ⇒ **ส่วนลดท้ายบิลและค่าบริการ
                // หายทั้งหมดตอนแยกบิล** (ร้านอาหารที่คิด service charge 10%
                // เสียรายได้ส่วนนั้นทุกครั้งที่แยกบิล ซึ่งเป็นงานประจำวัน;
                // ฝั่งส่วนลดกลับกัน = เก็บลูกค้าเกินกว่าที่ตกลงไว้)
                // เป็นเปอร์เซ็นต์จึงยกมาตรง ๆ ได้ — ผลรวมของใบลูกเท่าใบแม่พอดี
                DiscountPercent = parent.DiscountPercent,
                ServiceChargePercent = parent.ServiceChargePercent,
                CreatedBy = userId
            };
            seq++; splitIdx++;
            foreach (var srcItemId in group)
            {
                var srcItem = parent.Items.First(i => i.Id == srcItemId);
                var copy = new PosOrderItem
                {
                    // PosOrderItem is BaseEntity (scoped via OrderId.Order.CompanyId)
                    ProductId = srcItem.ProductId,
                    ServicePackageId = srcItem.ServicePackageId,
                    ItemName = srcItem.ItemName,
                    ItemCode = srcItem.ItemCode,
                    Unit = srcItem.Unit,
                    Quantity = srcItem.Quantity,
                    UnitPrice = srcItem.UnitPrice,
                    DiscountAmount = srcItem.DiscountAmount,
                    DiscountPercent = srcItem.DiscountPercent,
                    SubTotal = srcItem.SubTotal,
                    TotalAmount = srcItem.TotalAmount,
                    // ตัวรายการไม่ได้เปลี่ยน VAT ของบรรทัดจึงต้องเท่าเดิม
                    // (เดิมตั้ง 0 ⇒ ใบที่แยกออกมาโชว์ VAT รายบรรทัดเป็นศูนย์)
                    VatAmount = srcItem.VatAmount,
                    LineOrder = srcItem.LineOrder,
                    Status = srcItem.Status,
                    Notes = srcItem.Notes,
                };
                // ตัวเลือกเพิ่มเติม (เพิ่มชีส/พิเศษ) — ราคาถูกบวกเข้า SubTotal
                // ตั้งแต่ตอนเพิ่มรายการแล้ว ยอดจึงไม่หาย แต่ถ้าไม่คัดลอกแถวมา
                // ใบที่พิมพ์จะ**เก็บเงินค่าตัวเลือกโดยไม่มีบรรทัดอธิบาย**
                foreach (var m in srcItem.Modifiers.Where(m => !m.IsDeleted))
                    copy.Modifiers.Add(new PosOrderItemModifier
                    {
                        ModifierOptionId = m.ModifierOptionId,
                        ModifierGroupName = m.ModifierGroupName,
                        ModifierName = m.ModifierName,
                        PriceAdjustment = m.PriceAdjustment,
                    });
                child.Items.Add(copy);
                // Mark the source item as moved (delete-on-parent).
                srcItem.IsDeleted = true;
            }
            RecalculateOrder(child, vatRate);
            _db.PosOrders.Add(child);
            children.Add(child);
        }

        // Recompute parent — items left after split (if any) stay there.
        RecalculateOrder(parent, vatRate);
        // If everything was moved out, void the parent so reports don't see a ghost row.
        if (!parent.Items.Any(i => !i.IsDeleted))
            parent.Status = PosOrderStatus.Voided;

        await _db.SaveChangesAsync();

        var result = new List<OrderResponse>();
        if (parent.Status != PosOrderStatus.Voided)
            result.Add(await GetOrderAsync(companyId, parent.Id));
        foreach (var c in children)
            result.Add(await GetOrderAsync(companyId, c.Id));
        return result;
    }

    public async Task<OrderResponse> UpdateItemStatusAsync(Guid companyId, Guid orderId, Guid itemId, UpdateItemStatusRequest request)
    {
        var item = await _db.PosOrderItems.FirstOrDefaultAsync(i => i.Id == itemId && i.OrderId == orderId)
            ?? throw new KeyNotFoundException("ไม่พบรายการ");
        item.Status = request.Status;
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, orderId);
    }

    // ==================== Payment & Complete ====================

    public async Task<OrderResponse> AddPaymentAsync(Guid companyId, CreatePaymentRequest request, string userId)
    {
        var order = await _db.PosOrders.Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == request.OrderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status == PosOrderStatus.Voided)
            throw new InvalidOperationException("ไม่สามารถชำระเงินออเดอร์ที่ถูกยกเลิก");

        var payment = new PosPayment
        {
            OrderId = request.OrderId,
            PaymentMethod = request.PaymentMethod,
            Amount = request.Amount,
            ReceivedAmount = request.ReceivedAmount,
            ChangeAmount = request.PaymentMethod == PaymentMethod.Cash
                ? Math.Max(0, request.ReceivedAmount - request.Amount) : 0,
            ReferenceNo = request.ReferenceNo,
            CardLastFour = request.CardLastFour
        };
        _db.PosPayments.Add(payment);
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, order.Id);
    }

    public async Task<OrderResponse> CompleteOrderAsync(Guid companyId, Guid orderId, string userId)
    {
        var order = await _db.PosOrders
            .Include(o => o.Items).ThenInclude(i => i.Modifiers)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");

        if (order.Status == PosOrderStatus.Completed) throw new InvalidOperationException("ออเดอร์นี้เสร็จสิ้นแล้ว");

        var totalPaid = order.Payments.Sum(p => p.Amount);
        if (totalPaid < order.NetAmount)
            throw new InvalidOperationException($"ยอดชำระ ({totalPaid:N2}) ไม่ครบ ยอดที่ต้องจ่าย ({order.NetAmount:N2})");

        await using var txn = await _db.Database.BeginTransactionAsync();
        try
        {
        order.Status = PosOrderStatus.Completed;
        order.CompletedAt = DateTime.UtcNow;
        await IssueAbbreviatedInvoiceNumberAsync(companyId, order);

        // Loyalty: award 1 point per ฿100 spent (NetAmount excluding tip) and
        // bump visit counter / lastVisit on the linked Contact. Floor — fractions
        // don't round up so a ฿149 sale earns 1 not 2.
        if (order.CustomerId.HasValue)
        {
            var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == order.CustomerId.Value && c.CompanyId == companyId);
            if (contact != null)
            {
                var spend = order.NetAmount - order.TipAmount;
                var earn = (int)Math.Floor(spend / 100m);
                if (earn > 0) contact.LoyaltyPoints += earn;
                contact.LastVisitAt = DateTime.UtcNow;
                contact.TotalVisitCount += 1;
            }
        }

        // ★ E-01 (รอบ 193) — ลำดับ "ตัดสต็อก → รู้ต้นทุน → JE" · เดิม JE มาก่อน ⇒ ต้นทุน
        // วัตถุดิบตามสูตรไม่มีทางถึง JE (เมนูชงสด COGS = 0 แต่คืนเงินกลับเต็ม)
        var saleCogs = await DeductSaleStockAsync(companyId, order, DateTime.UtcNow,
            "วัตถุดิบตามสูตร POS", "POS Sale", userId);

        // Create journal entry for accounting integration
        await CreateSalesJournalEntryAsync(companyId, order, userId, saleCogs);

        await _db.SaveChangesAsync();
        await txn.CommitAsync();
        }
        catch
        {
            await txn.RollbackAsync();
            throw;
        }
        return await GetOrderAsync(companyId, orderId);
    }

    // ==================== Accounting Integration ====================

    /// <summary>ต้นทุนต่อหน่วยตาม CostingMethod: WeightedAverage → ค่าเฉลี่ย
    /// ถ่วงน้ำหนักปัจจุบัน, อื่น ๆ (Standard/FIFO ที่ยังไม่มี layer) → CostPrice.
    /// ใช้ทุกจุดที่ stamp UnitCost / คิด COGS ใน POS ให้สอดคล้องกัน</summary>
    private static decimal EffectiveUnitCost(Product p) =>
        p.CostingMethod == CostingMethod.WeightedAverage && p.AverageUnitCost > 0
            ? p.AverageUnitCost : p.CostPrice;

    /// <summary>ตัดสต็อกของบิลขายทั้งใบ (สูตร → วัตถุดิบ · ไม่ใช่สูตร+TrackStock → ตัวสินค้า) แล้ว
    /// **ตรึงต้นทุนที่ออกจากคลังจริง** ลง <c>PosOrderItem.CostOfGoodsSold</c> ทุกบรรทัด ·
    /// คืนยอด COGS รวม (ปัดแล้ว) ให้ JE ขายใช้ — ตัวเดียวของเส้นขายออนไลน์และ sync ออฟไลน์
    ///
    /// <para>★ E-01 (รอบ 193): ต้องเรียก **ก่อน** <see cref="CreateSalesJournalEntryAsync"/> เสมอ ·
    /// การตัดสินต้นทุนต่อบรรทัดอยู่ที่ <c>Helpers/PosCogsBooking.SaleLineCost</c> ตัวเดียว ·
    /// ไม่มี catch — ตัดสต็อกไม่ผ่าน (ด่านติดลบ/ไม่พบสินค้า) ต้องล้มทั้งบิล (กฎเหล็ก #4 E)</para></summary>
    private async Task<decimal> DeductSaleStockAsync(Guid companyId, PosOrder order,
        DateTime movementDate, string recipeNote, string ownNote, string userId)
    {
        var lineCosts = new List<decimal>();
        foreach (var item in order.Items.Where(i => !i.IsDeleted))
        {
            var lineCost = 0m;
            if (item.ProductId is Guid productId)
            {
                var product = await _db.Products.FindAsync(productId);
                if (product != null)
                {
                    // สินค้าที่ชงสด (`ConsumesBomOnSale`) กินวัตถุดิบตามสูตรแทนการตัดตัวเอง
                    var ate = await ApplyRecipeConsumptionAsync(companyId, order, item, product,
                        -1, movementDate, order.OrderNumber, recipeNote, userId);
                    decimal? ownMoveCost = null;
                    if (!ate.Handled && product.TrackStock)
                    {
                        var move = await _stock.MoveAsync(new StockMoveRequest(
                            CompanyId: companyId,
                            ProductId: product.Id,
                            Quantity: -item.Quantity,         // − = ตัดออกจากคลัง
                            MovementType: "OUT",
                            Reference: order.OrderNumber,
                            WarehouseId: order.WarehouseId,   // คลังของสาขาที่ขาย (null = คลังหลัก)
                            PosOrderId: order.Id,
                            MovementDate: movementDate,
                            Notes: ownNote,
                            CreatedBy: userId));
                        ownMoveCost = move.TotalCost;
                    }
                    lineCost = Accounting.Helpers.PosCogsBooking.SaleLineCost(ate.Handled, ate.TotalCost, ownMoveCost);
                }
            }
            item.CostOfGoodsSold = lineCost;
            lineCosts.Add(lineCost);
        }
        return Accounting.Helpers.PosCogsBooking.SaleTotal(lineCosts);
    }



    /// <summary>ตัด/คืน **วัตถุดิบตามสูตร** ของบรรทัดขาย 1 บรรทัด — ตัวเดียวที่ทุกเส้นเรียก
    /// (ขาย · sync ออฟไลน์ · ยกเลิกบิล · คืนเงิน) เพื่อไม่ให้เกิดสำเนาที่ drift
    ///
    /// <para><paramref name="direction"/>: −1 = ขาย (กินวัตถุดิบ) · +1 = คืน/ยกเลิก</para>
    ///
    /// <para><c>Handled = true</c> ⇒ ผู้เรียก **ห้ามตัดสต็อกตัวสินค้าแม่ซ้ำ**
    /// (ชานมไข่มุกไม่ได้อยู่ในสต็อกล่วงหน้า — ตัดตัวมันเองจะทำให้ยอดติดลบตลอดกาล) ·
    /// <c>TotalCost</c> = ต้นทุนวัตถุดิบรวมของบรรทัดนี้ ใช้ลง COGS</para>
    ///
    /// <para><c>Handled = false</c> เมื่อสินค้าไม่ได้ตั้ง <c>ConsumesBomOnSale</c> →
    /// ผู้เรียกตัดสต็อกตัวเองตามพฤติกรรมเดิมทุกประการ</para>
    ///
    /// <para>★ รอบ 193 (ฝ่ายค้าน M2) — สองเรื่องที่เส้นนี้ต้องตรงกับ GL:
    /// (1) <c>TotalCost</c> นับเฉพาะวัตถุดิบที่ <b>ติดตามสต็อก</b> (<c>TrackStock</c>) — ของที่ไม่ติดตาม
    /// ถูกลงค่าใช้จ่ายไปแล้วตอนซื้อ (ฝั่งซื้อ Dr 11500 เฉพาะ TrackStock) ⇒ นับซ้ำ = Dr 51110/Cr 11500
    /// สองครั้ง และ 11500 ติดลบเรื่อย ๆ · ตัดสินที่ <c>PosCogsBooking.RecipeCost</c> ตัวเดียว
    /// (2) ขาคืน (<paramref name="direction"/> &gt; 0) ใช้ต้นทุนต่อหน่วย <b>ณ วันขาย</b> จาก
    /// <paramref name="restockUnitCostByComponent"/> (ผู้เรียกหาจาก movement ขาออกของบิลนั้น) —
    /// JE กลับด้วยยอดที่ขายลงไว้ สต็อกต้องกลับด้วยต้นทุนเดียวกัน · ไม่มีค่า = ต้นทุนวันนี้ (พฤติกรรมเดิม)</para></summary>
    private async Task<(bool Handled, decimal TotalCost)> ApplyRecipeConsumptionAsync(
        Guid companyId, PosOrder order, PosOrderItem item, Product product,
        int direction, DateTime movementDate, string reference, string note, string userId,
        IReadOnlyDictionary<Guid, decimal>? restockUnitCostByComponent = null)
    {
        if (!product.ConsumesBomOnSale) return (false, 0m);

        var now = DateTime.UtcNow;
        var bomId = await _db.BillsOfMaterials.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.ParentProductId == product.Id
                     && b.IsActive && !b.IsDeleted
                     && b.EffectiveFrom <= now && (b.EffectiveTo == null || b.EffectiveTo >= now))
            .OrderByDescending(b => b.EffectiveFrom)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync();
        if (bomId is not Guid activeBomId)
        {
            // ตั้งธงว่ากินสูตรแต่ยังไม่มีสูตร = ตั้งค่าไม่ครบ · ห้ามเงียบ และห้ามตัด
            // สต็อกตัวเองแทน (จะได้ยอดติดลบโดยที่วัตถุดิบไม่ถูกตัด = ผิดสองทาง)
            _logger.LogWarning(
                "สินค้า {Code} ตั้งว่าขายแล้วกินสูตร แต่ยังไม่มีสูตรที่ใช้งานอยู่ — ไม่ได้ตัดวัตถุดิบให้บิล {Order}",
                product.Code, order.OrderNumber);
            return (true, 0m);
        }

        var recipe = await _db.BomLines.AsNoTracking()
            .Where(l => l.BomId == activeBomId && !l.IsDeleted)
            .Select(l => new Accounting.Helpers.BomRecipeLine(l.ComponentProductId, l.QuantityPerParent))
            .ToListAsync();

        // ท็อปปิ้งที่ลูกค้าเลือก — option ที่ผูกวัตถุดิบไว้เท่านั้น
        var optionIds = item.Modifiers.Where(m => !m.IsDeleted && m.ModifierOptionId.HasValue)
            .Select(m => m.ModifierOptionId!.Value).ToList();
        var modifiers = optionIds.Count == 0
            ? new List<Accounting.Helpers.BomModifierDraw>()
            : await _db.Set<ProductModifierOption>().AsNoTracking()
                .Where(o => optionIds.Contains(o.Id) && o.ComponentProductId != null && o.ComponentQuantity > 0)
                .Select(o => new Accounting.Helpers.BomModifierDraw(o.ComponentProductId!.Value, o.ComponentQuantity))
                .ToListAsync();

        var draws = Accounting.Helpers.BomConsumption.Resolve(recipe, modifiers, item.Quantity);
        // วัตถุดิบตัวไหนเป็น "สินค้าคงเหลือ" (TrackStock) — ชุดเดียวกับที่ฝั่งซื้อ Dr 11500
        var componentIds = draws.Select(d => d.ComponentProductId).Distinct().ToList();
        var trackedComponents = componentIds.Count == 0
            ? new HashSet<Guid>()
            : (await _db.Products.AsNoTracking()
                .Where(p => p.CompanyId == companyId && componentIds.Contains(p.Id) && p.TrackStock)
                .Select(p => p.Id)
                .ToListAsync()).ToHashSet();
        var moved = new List<(bool TrackStock, decimal MoveCost)>();
        foreach (var d in draws)
        {
            var qty = direction < 0 ? -d.Quantity : d.Quantity;
            decimal? restockCost = direction > 0
                && restockUnitCostByComponent != null
                && restockUnitCostByComponent.TryGetValue(d.ComponentProductId, out var saleUnitCost)
                && saleUnitCost > 0m
                    ? saleUnitCost : null;
            var move = await _stock.MoveAsync(new StockMoveRequest(
                CompanyId: companyId,
                ProductId: d.ComponentProductId,
                Quantity: qty,
                MovementType: qty < 0 ? "OUT" : "IN",
                Reference: reference,
                WarehouseId: order.WarehouseId,   // วัตถุดิบของ **สาขานั้น**
                PosOrderId: order.Id,
                MovementDate: movementDate,
                Notes: $"{note} ({product.Code}"
                     + (d.Source == Accounting.Helpers.BomDraw.FromModifier ? " · ท็อปปิ้ง" : " · สูตร") + ")",
                // ขาคืน: ต้นทุน ณ วันขาย (null = ledger ใช้ต้นทุนวันนี้ — บิลที่หา movement ขายไม่เจอ)
                UnitCostOverride: restockCost,
                CreatedBy: userId));
            moved.Add((trackedComponents.Contains(d.ComponentProductId), move.TotalCost));
        }
        return (true, Accounting.Helpers.PosCogsBooking.RecipeCost(moved));
    }

    /// <summary>อ่านสาย JE "ใบเดิม → ตัวกลับ → …" ตาม <c>ReversedByEntryId</c> พร้อมบรรทัด (สำหรับ
    /// <c>Helpers/PosVoidSaleJournal</c>) · กรอง CompanyId ทุกขั้น · กันวนด้วยชุด id ที่เห็นแล้ว</summary>
    private async Task<List<Accounting.Helpers.PosJournalChainEntry>> LoadJournalChainAsync(Guid companyId, Guid firstId)
    {
        var chain = new List<Accounting.Helpers.PosJournalChainEntry>();
        var seen = new HashSet<Guid>();
        Guid? nextId = firstId;
        while (nextId is Guid id && seen.Add(id) && chain.Count < 20)
        {
            var je = await _db.JournalEntries.AsNoTracking()
                // !IsDeleted: JE ที่ถูก soft-delete ต้องตกเป็น "หา JE ไม่พบ" (บล็อก) ไม่ใช่ถูกตัดสินว่ากลับได้ (ฝ่ายค้าน P4)
                .Where(j => j.Id == id && j.CompanyId == companyId && !j.IsDeleted)
                .Select(j => new
                {
                    j.Id, j.EntryNumber, j.Status, j.ReversedByEntryId,
                    Lines = j.Lines.Where(l => !l.IsDeleted)
                        .Select(l => new { l.AccountId, l.DebitAmount, l.CreditAmount }).ToList(),
                })
                .FirstOrDefaultAsync();
            if (je == null) break;
            chain.Add(new Accounting.Helpers.PosJournalChainEntry(je.Id, je.EntryNumber, je.Status,
                je.Lines.Select(l => new Accounting.Helpers.PosJournalLineAmount(l.AccountId, l.DebitAmount, l.CreditAmount)).ToList()));
            nextId = je.ReversedByEntryId;
        }
        return chain;
    }

    /// <summary>ต้นทุนต่อหน่วย <b>ณ วันขาย</b> ของทุกสินค้าที่บิลนี้ตัดออกจากคลัง — อ่านจาก movement ขาออก
    /// ที่ <see cref="DeductSaleStockAsync"/> เขียนไว้ (อ้างอิง = เลขบิล · ขาคืน/ยกเลิกใช้อ้างอิง REFUND-/VOID-
    /// จึงไม่ปน) · ใช้คืนวัตถุดิบตามสูตรด้วยต้นทุนเดียวกับที่ JE ขายลงไว้ (รอบ 193 · ฝ่ายค้าน M2)</summary>
    private async Task<IReadOnlyDictionary<Guid, decimal>> LoadSaleUnitCostsAsync(Guid companyId, PosOrder order)
    {
        var outs = await _db.StockMovements.AsNoTracking()
            .Where(m => m.CompanyId == companyId && !m.IsDeleted
                && m.Reference == order.OrderNumber && m.Quantity < 0m)
            .Select(m => new { m.ProductId, m.Quantity, m.UnitCost })
            .ToListAsync();
        return Accounting.Helpers.PosCogsBooking.SaleUnitCostByProduct(
            outs.Select(m => (m.ProductId, m.Quantity, m.UnitCost)));
    }

    /// <summary>ออก **เลขใบกำกับภาษีอย่างย่อ** (§86/6) ให้บิลที่ปิดแล้ว — ตรึงลงบิลพร้อม
    /// รหัสสาขา (§86/4 ห้ามแก้ย้อนหลัง)
    ///
    /// <para>ทำไมไม่ใช้ <c>OrderNumber</c>: เลขนั้นนับต่อ**บริษัท** และนับใบที่ถูกยกเลิกด้วย
    /// ⇒ เลขใบกำกับจะกระโดดและซ้ำข้ามสาขา · §86/6 ต้อง gap-free **ต่อสาขา**</para>
    ///
    /// <para>ทำไมต้องล็อก: สองแคชเชียร์ของสาขาเดียวกันปิดบิลพร้อมกันจะได้เลขซ้ำ ·
    /// คีย์ต้อง deterministic ข้าม process (`AdvisoryLockKey` ไม่ใช่ `HashCode.Combine`) ·
    /// ล็อกอยู่ในธุรกรรมของผู้เรียก (ปิดบิลมีธุรกรรมครอบอยู่แล้ว)</para>
    ///
    /// <para>ไม่มีสิทธิ์ออก (ยังไม่จด VAT / ไม่มี ภ.พ.06 / บิลไม่มี VAT) → **ไม่ออกเลข**
    /// และหัวสลิปเป็น "ใบเสร็จรับเงิน" — ห้ามพิมพ์คำว่าใบกำกับโดยไม่มีสิทธิ์</para></summary>
    private async Task IssueAbbreviatedInvoiceNumberAsync(Guid companyId, PosOrder order)
    {
        if (order.AbbreviatedInvoiceNumber != null) return;   // ออกไปแล้ว — idempotent

        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.IsVatRegistered, c.IsRetailApproved, c.PhoR06ApprovedDate })
            .FirstOrDefaultAsync();
        if (company == null) return;

        // ★ H-A3 — เลขใบกำกับอย่างย่อ (§86/6) ผูกกับวัน/เดือนตามปฏิทินไทย
        var issuedAt = Accounting.Helpers.ThaiDate.CalendarDateUtc(
            order.CompletedAt ?? DateTime.UtcNow);
        // นโยบายระดับแพลตฟอร์ม: บังคับ ภ.พ.06 ไหม (แอดมินปิดได้เมื่อกฎหมายเปลี่ยน)
        // — แถว SiteSettings ยังไม่ถูกสร้าง = ยึดกฎหมายวันนี้ (บังคับ) ไม่ใช่ปล่อยผ่าน
        var requirePhoR06 = await _db.SiteSettings.AsNoTracking()
            .Select(s => (bool?)s.RequirePhoR06ForAbbreviatedTaxInvoice)
            .FirstOrDefaultAsync() ?? true;
        // รหัสสาขาตรึงลงบิลเสมอ แม้ออกอย่างย่อไม่ได้ — รายงานภาษีขายต้องรู้ว่าใบนี้
        // ของสาขาไหน และค่านี้ต้องไม่เปลี่ยนเมื่อเครื่องย้ายสาขาภายหลัง
        // ⚠️ ต้องทำ **ก่อน** ตัดสินหัวสลิป เพราะ §86/4(2) เป็นหนึ่งในเงื่อนไขของการออกเลข
        order.IssuerBranchCode ??= order.BranchId is Guid bid
            ? await _db.Branches.AsNoTracking()
                .Where(b => b.Id == bid && b.CompanyId == companyId)
                .Select(b => b.TaxBranchCode)
                .FirstOrDefaultAsync()
            : null;

        var header = Accounting.Helpers.PosSlipHeader.Resolve(
            company.IsVatRegistered, company.IsRetailApproved, company.PhoR06ApprovedDate,
            order.VatAmount, issuedAt, requirePhoR06,
            billBelongsToBranch: order.BranchId.HasValue,
            issuerTaxBranchCode: order.IssuerBranchCode);

        if (!header.CanIssueAbbreviated) return;

        // เลขรันต่อ (สาขา, เดือนภาษี) — ตัวย่อจากเครื่อง ถ้าไม่ตั้งใช้ "ABB"
        var prefixRoot = await _db.PosSessions.AsNoTracking()
            .Where(x => x.Id == order.SessionId && x.CompanyId == companyId)
            .Select(x => x.Terminal.AbbreviatedInvoicePrefix)
            .FirstOrDefaultAsync();
        // ★ D8-P0 — ส่วนสาขาของเลขรันมาจาก resolver ตัวเดียวกับที่ตัดสินหัวสลิป
        // (ถึงตรงนี้ได้แปลว่าไม่เป็น null แล้ว เพราะ CanIssueAbbreviated ผ่าน)
        var branchPart = Accounting.Helpers.PosSlipHeader.BranchSeriesCode(
            order.BranchId.HasValue, order.IssuerBranchCode)
            ?? throw new InvalidOperationException("รหัสสาขาหายระหว่างออกเลขใบกำกับอย่างย่อ");
        var prefix = $"{(string.IsNullOrWhiteSpace(prefixRoot) ? "ABB" : prefixRoot)}-{branchPart}-{issuedAt:yyyyMM}-";

        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            Accounting.Helpers.AdvisoryLockKey.For(companyId,
                Accounting.Helpers.AdvisoryLockKey.DocumentSequence, prefix));

        var last = await _db.PosOrders.IgnoreQueryFilters()
            .Where(o => o.CompanyId == companyId && o.AbbreviatedInvoiceNumber != null
                     && o.AbbreviatedInvoiceNumber.StartsWith(prefix))
            .Select(o => o.AbbreviatedInvoiceNumber)
            .MaxAsync();
        var seq = 1;
        if (last != null && int.TryParse(last.Substring(prefix.Length), out var parsed)) seq = parsed + 1;
        order.AbbreviatedInvoiceNumber = $"{prefix}{seq:D5}";
    }

    /// <summary>JE ขาย POS · <paramref name="saleCogs"/> = ต้นทุนที่ออกจากคลังจริงของบิลนี้ จาก
    /// <see cref="DeductSaleStockAsync"/> (★ E-01 — เดิมคิดเองจาก TrackStock ของตัวแม่ ⇒ สูตร = 0)</summary>
    private async Task CreateSalesJournalEntryAsync(Guid companyId, PosOrder order, string userId, decimal saleCogs)
    {
        // เครื่องที่เปิดกะนี้ — ใช้เลือกบัญชีเงินสด/ธนาคารของสาขา (null ได้ = ใช้ผังมาตรฐาน)
        var jeTerminal = await _db.PosSessions.AsNoTracking()
            .Where(s => s.Id == order.SessionId && s.CompanyId == companyId)
            .Select(s => s.Terminal)
            .FirstOrDefaultAsync();

        // Sales / VAT / COGS / Inventory accounts — single source per company.
        var salesAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "41000")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("41") && a.Level >= 4);
        var vatAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "21911")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("219") && a.Level >= 4);

        // ★ H-A18: เดิม `return;` เงียบ ๆ ⇒ ออเดอร์ปิดสำเร็จ เงินเข้าลิ้นชัก
        // แต่ **ไม่มีรายการบัญชีเลย** = GL รั่วโดยไม่มีใครเห็น (ต่างจาก catch
        // ด้านล่างที่ throw แล้ว — ทางออกสองทางของเมธอดเดียวกันตัดสินคนละแบบ)
        // กฎเหล็ก #4 E: ห้ามกลืน error ใน payment/stock/JE path — fail loud
        if (salesAccount == null)
            throw new Accounting.Helpers.BusinessRuleException(
                "ไม่พบบัญชีรายได้จากการขาย (41000) ในผังบัญชี — เพิ่มผังบัญชีก่อนปิดบิล " +
                "มิฉะนั้นยอดขายจะไม่เข้าบัญชีแยกประเภท", "POS-NO-SALES-ACCOUNT");

        var lines = new List<Models.DTOs.Accounting.JournalLineRequest>();

        // Debit: one line per payment method (Cash → 1011, BankTransfer/PromptPay → 1012,
        // CreditCard → 1131 บัตรเครดิตค้างรับ). When the order has multiple payments
        // — e.g. half cash half transfer — we record each leg into its own account so
        // the GL reconciles to the bank/cash position correctly.
        var paymentsToBook = order.Payments?.Where(p => !p.IsDeleted).ToList() ?? new List<PosPayment>();
        if (paymentsToBook.Count == 0)
        {
            // No payment records — fall back to a single debit using the default cash
            // account so the JE still balances. Older orders without explicit payment
            // method end up here.
            var cashAccount = await ResolvePaymentAccountAsync(companyId, PaymentMethod.Cash, jeTerminal);
            // ★ H-A18 (ทางออกที่สอง) — เหตุผลเดียวกับข้างบน
            if (cashAccount == null)
                throw new Accounting.Helpers.BusinessRuleException(
                    "ไม่พบบัญชีเงินสด/ธนาคารสำหรับรับเงิน POS — ตั้งค่าผังบัญชีของสาขาก่อนปิดบิล",
                    "POS-NO-CASH-ACCOUNT");
            lines.Add(new(cashAccount.Id, order.NetAmount, 0, $"รับเงิน POS #{order.OrderNumber}"));
        }
        else
        {
            foreach (var pay in paymentsToBook)
            {
                // บิลที่ลูกค้าสแกนจ่ายผ่านระบบรับชำระออนไลน์: เงิน**ยังไม่เข้าบัญชีร้าน**
                // ผู้ให้บริการโอนเข้า T+n หลังหักค่าธรรมเนียม ⇒ ต้องลงบัญชีพัก 11340
                // ไม่ใช่บัญชีธนาคาร/ลิ้นชักของสาขา (ลงธนาคารเลย = ยอดธนาคารสูงเกินจริง
                // และกระทบยอดรายสาขาไม่ได้) · ตัวตัดสินคือ resolver ตัวเดียวของระบบ
                var acct = await ResolveGatewayClearingAsync(companyId, pay)
                    ?? await ResolvePaymentAccountAsync(companyId, pay.PaymentMethod, jeTerminal);
                if (acct == null) continue;
                // Use Amount (allocated to invoice), not ReceivedAmount, so cash-tendered-with-change
                // posts the invoice value, not the full bill the customer handed over.
                var debit = pay.Amount;
                if (debit <= 0) continue;
                var methodLabel = PaymentMethodThaiLabel(pay.PaymentMethod);
                lines.Add(new(acct.Id, debit, 0, $"รับเงิน {methodLabel} POS #{order.OrderNumber}"));
            }
        }

        // Credit: Sales Revenue (net of VAT AND tip). NetAmount รวมทิปไว้ →
        // ต้องหักทิปออกจากรายได้ ไม่งั้นทิปถูกเครดิตซ้ำ (ในรายได้ + บัญชีทิป 2160
        // ด้านล่าง) → เครดิตเกินเดบิต = JE ไม่สมดุล → CreateJournalEntry throw →
        // ถูก swallow (ด้านล่าง) → ออเดอร์ที่มีทิป "ไม่ลง GL เลย" (รายได้/VAT/COGS
        // หาย). กรณี fallback ไม่มีบัญชี 2160 ทิปจะถูกบวกกลับเข้า sales (ยังสมดุล).
        var revenueAmount = order.NetAmount - order.VatAmount - order.TipAmount;
        lines.Add(new(salesAccount.Id, 0, revenueAmount, $"รายได้ขาย POS #{order.OrderNumber}"));

        // Credit: VAT Payable (if any)
        if (order.VatAmount > 0 && vatAccount != null)
            lines.Add(new(vatAccount.Id, 0, order.VatAmount, $"ภาษีขาย POS #{order.OrderNumber}"));

        // Credit: เงินรับฝาก-ทิปพนักงาน (Liability) — tip is NOT revenue, it's
        // held in trust for the staff and paid out via payroll. Cr 2160 by
        // default; fall back to any 21xx liability with "ทิป" in name.
        if (order.TipAmount > 0)
        {
            // บัญชีจาก CompanySettings.PosTipPayableAccountCode → default 21814/21819 → 21xxx ที่ชื่อมี "ทิป"
            // (เดิม fallback prefix "216" ⇒ ผังมาตรฐานได้ 21610 "เงินมัดจำรับ" ทุกบริษัท · ERP_REVIEW H-07)
            var tipCfg = await _db.CompanySettings.AsNoTracking()
                .Where(cs => cs.CompanyId == companyId).Select(cs => cs.PosTipPayableAccountCode).FirstOrDefaultAsync();
            var tipAccount = await TipAccountResolver.ResolveAsync(_db, companyId, tipCfg);
            // ★ D8-8 — เดิมไม่พบบัญชีทิป → **ยัดเข้ารายได้ขาย** + LogWarning ⇒ เงินที่
            // บริษัทถือแทนพนักงานกลายเป็นรายได้ของบริษัท: กำไรบวม · เสียภาษีเงินได้จาก
            // เงินที่ไม่ใช่ของตัวเอง · หนี้สินที่ต้องจ่ายพนักงานหายไปจากงบ · และไม่มี
            // ใครเห็นเพราะ LogWarning ไม่ใช่การล้มดัง (กฎเหล็ก #4 F2 ข้อ 7)
            // ทางออกของผู้ใช้อยู่ในข้อความ — ทางเดียวกับ H-A18 ที่เมธอดนี้ทำอยู่แล้ว
            if (tipAccount == null)
                throw new Accounting.Helpers.BusinessRuleException(
                    $"ไม่พบบัญชี \"ทิปพนักงานค้างจ่าย\" ในผังบัญชี — ปิดบิลที่มีทิป {order.TipAmount:N2} บาทไม่ได้ "
                    + "(ทิปเป็นเงินที่ถือแทนพนักงาน ลงเป็นรายได้ไม่ได้) · "
                    + $"เพิ่มบัญชี {string.Join(" หรือ ", TipAccountResolver.DefaultCodes)} ในผังบัญชี "
                    + "หรือตั้งค่ารหัสบัญชีทิปในตั้งค่าบริษัท แล้วปิดบิลใหม่ (หรือเอาทิปออกจากบิลนี้)",
                    "POS-NO-TIP-ACCOUNT");
            lines.Add(new(tipAccount.Id, 0, order.TipAmount, $"ทิปลูกค้า POS #{order.OrderNumber}"));
        }

        // COGS: Dr ต้นทุนขาย / Cr สินค้าคงเหลือ — ยอด = ต้นทุนที่ออกจากคลังจริง ซึ่งผู้เรียกตัด
        // สต็อกแล้วส่งมา (★ E-01 รอบ 193) · เดิมคิดตรงนี้จาก `Products.Where(p => p.TrackStock)`
        // ของ**ตัวแม่** ⇒ เมนูชงสดที่ตัดวัตถุดิบตามสูตร (ตัวแม่ TrackStock=false) ได้ 0 เสมอ
        // ขณะที่คืนเงินกลับต้นทุนวัตถุดิบเต็ม = ต้นทุนขายติดลบ · สินค้าปกติได้ตัวเลขเท่าเดิม
        // (ต้นทุนขาออกของ ledger = ถัวเฉลี่ย/CostPrice ตัวเดียวกับสูตรเดิม, ปัดครั้งเดียวเหมือนเดิม)
        {
            var totalCogs = saleCogs;
            if (totalCogs > 0)
            {
                var cogsAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "51110")
                    ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("511") && a.Level >= 4);
                var inventoryAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "11500")
                    ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("115") && a.Level >= 4);
                if (cogsAccount != null && inventoryAccount != null)
                {
                    lines.Add(new(cogsAccount.Id, totalCogs, 0, $"ต้นทุนขาย POS #{order.OrderNumber}"));
                    lines.Add(new(inventoryAccount.Id, 0, totalCogs, $"ตัดสินค้าคงเหลือ POS #{order.OrderNumber}"));
                }
            }
        }

        // ★ H-A3: วันที่ลงบัญชี = วันตามปฏิทินไทยของเวลาที่ปิดบิล
        var jeDate = Accounting.Helpers.ThaiDate.CalendarDateUtc(
            order.CompletedAt ?? DateTime.UtcNow);
        var journalRequest = new Models.DTOs.Accounting.CreateJournalEntryRequest(
            jeDate, $"POS Sale #{order.OrderNumber}", order.OrderNumber, lines,
            BranchId: order.BranchId);

        try
        {
            var journal = await _accountingService.CreateJournalEntryAsync(companyId, journalRequest, userId);
            order.JournalEntryId = journal.Id;
            await _accountingService.PostJournalEntryAsync(companyId, journal.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POS journal creation failed for order {OrderNumber} in company {CompanyId}.",
                order.OrderNumber, companyId);
            // เดิมกลืน error → order เสร็จสิ้นโดยไม่มี JE = รายได้/ภาษีขาย/COGS หายจาก
            // บัญชี (silent GL hole). แก้: โยนต่อ → caller (CompleteOrderAsync /
            // SyncOfflineOrderAsync ซึ่งเป็น transactional ทั้งคู่) rollback ทั้ง order
            // → แคชเชียร์เห็น error + ลองใหม่ ไม่มีทางขายเสร็จแบบบัญชีหาย.
            throw new InvalidOperationException(
                $"ลงบัญชีการขาย POS #{order.OrderNumber} ไม่สำเร็จ ({ex.Message}) — ออเดอร์ยังไม่เสร็จสิ้น " +
                "กรุณาตรวจผังบัญชี (รายได้ 41xx / ภาษีขาย 21911 / เงินสด 111) แล้วลองใหม่", ex);
        }
    }

    // ==================== Reports ====================

    public async Task<PosDailySummaryResponse> GetDailySummaryAsync(Guid companyId, DateTime date)
    {
        var startOfDay = date.Date;
        var endOfDay = startOfDay.AddDays(1);

        var orders = await _db.PosOrders
            .Include(o => o.Payments)
            .Where(o => o.CompanyId == companyId && o.CreatedAt >= startOfDay && o.CreatedAt < endOfDay)
            .ToListAsync();

        var completed = orders.Where(o => o.Status == PosOrderStatus.Completed).ToList();
        var voided = orders.Count(o => o.Status == PosOrderStatus.Voided);

        var paymentBreakdown = completed
            .SelectMany(o => o.Payments)
            .GroupBy(p => p.PaymentMethod)
            .Select(g => new PaymentMethodSummary(g.Key, g.Key.ToString(), g.Count(), g.Sum(p => p.Amount)))
            .ToList();

        return new PosDailySummaryResponse(
            date.Date, orders.Count, completed.Count, voided,
            completed.Sum(o => o.TotalAmount), completed.Sum(o => o.DiscountAmount),
            completed.Sum(o => o.VatAmount), completed.Sum(o => o.ServiceChargeAmount),
            completed.Sum(o => o.NetAmount), paymentBreakdown);
    }

    /// <summary>Z-Report (สิ้นกะ) — query สรุปยอด session ที่ closed.
    /// Read-only — ไม่ post JE เพิ่ม (JE เกิดต่อออเดอร์ใน CompleteOrderAsync
    /// อยู่แล้ว). ใช้เทียบเงินสดในลิ้นชัก + audit ก่อนปิดงาน.</summary>

    public async Task<PosBranchSummaryResponse> GetBranchSummaryAsync(Guid companyId, DateTime from, DateTime to)
    {
        var start = from.Date;
        var end = to.Date.AddDays(1);

        var orders = await _db.PosOrders.AsNoTracking()
            .Include(o => o.Payments)
            .Where(o => o.CompanyId == companyId && o.CreatedAt >= start && o.CreatedAt < end)
            .ToListAsync();

        var branchIds = orders.Where(o => o.BranchId.HasValue).Select(o => o.BranchId!.Value).Distinct().ToList();
        var branches = await _db.Branches.AsNoTracking()
            .Where(b => b.CompanyId == companyId && branchIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Name, b.TaxBranchCode })
            .ToListAsync();
        var byId = branches.ToDictionary(b => b.Id);

        var rows = orders
            .GroupBy(o => o.BranchId)
            .Select(g =>
            {
                var completed = g.Where(o => o.Status == PosOrderStatus.Completed).ToList();
                var net = completed.Sum(o => o.NetAmount);
                var name = g.Key is Guid bid && byId.TryGetValue(bid, out var b)
                    ? b.Name
                    // บิลที่ไม่ผูกสาขา ต้องโชว์เป็นแถวของตัวเอง ไม่ใช่ยัดรวมกับสำนักงานใหญ่
                    // (ไม่งั้นผลรวมรายสาขาจะไม่เท่ายอดบริษัทโดยไม่มีใครรู้ว่าทำไม)
                    : "(ยังไม่ผูกสาขา)";
                var code = g.Key is Guid bid2 && byId.TryGetValue(bid2, out var b2) ? b2.TaxBranchCode : null;
                return new PosBranchSummaryRow(
                    g.Key, name, code,
                    completed.Count,
                    g.Count(o => o.Status == PosOrderStatus.Voided),
                    net,
                    completed.Sum(o => o.VatAmount),
                    completed.Sum(o => o.DiscountAmount),
                    completed.Count > 0
                        ? Math.Round(net / completed.Count, 2, MidpointRounding.AwayFromZero) : 0m,
                    completed.SelectMany(o => o.Payments)
                        .GroupBy(pmt => pmt.PaymentMethod)
                        .Select(pg => new PaymentMethodSummary(
                            pg.Key, PaymentMethodThaiLabel(pg.Key), pg.Count(), pg.Sum(x => x.Amount)))
                        .ToList());
            })
            .OrderByDescending(r => r.NetSales)
            .ToList();

        return new PosBranchSummaryResponse(
            start, end.AddDays(-1), rows,
            rows.Sum(r => r.NetSales),
            rows.Any(r => r.BranchId == null));
    }

    public async Task<PosZReportResponse> GetZReportAsync(Guid companyId, Guid sessionId)
    {
        var session = await _db.PosSessions.AsNoTracking()
            .Include(s => s.Terminal)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ session");

        var openedBy = await _db.Users.AsNoTracking()
            .Where(u => u.Id == session.OpenedByUserId)
            .Select(u => u.FullName).FirstOrDefaultAsync();
        string? closedBy = null;
        if (session.ClosedByUserId.HasValue)
        {
            closedBy = await _db.Users.AsNoTracking()
                .Where(u => u.Id == session.ClosedByUserId.Value)
                .Select(u => u.FullName).FirstOrDefaultAsync();
        }

        var to = session.ClosedAt ?? DateTime.UtcNow;
        return await BuildZReportAsync(companyId, sessionId, session.TerminalId,
            session.Terminal?.Name, openedBy, closedBy,
            session.OpenedAt, to,
            session.OpeningBalance, session.ClosingBalance);
    }

    /// <summary>X-Report (ระหว่างกะ) — ดูยอดวิ่งทันที. ถ้าระบุ sessionId
    /// = session.OpenedAt..now; ระบุ terminalId + from/to = filter ตรง ๆ.</summary>
    public async Task<PosZReportResponse> GetXReportAsync(Guid companyId,
        Guid? terminalId = null, DateTime? from = null, DateTime? to = null)
    {
        var f = from ?? DateTime.UtcNow.Date;
        var t = to ?? DateTime.UtcNow;
        string? termName = null;
        if (terminalId.HasValue)
            termName = await _db.PosTerminals.AsNoTracking()
                .Where(x => x.Id == terminalId.Value && x.CompanyId == companyId)
                .Select(x => x.Name).FirstOrDefaultAsync();
        return await BuildZReportAsync(companyId, null, terminalId, termName,
            null, null, f, t, 0m, 0m);
    }

    private async Task<PosZReportResponse> BuildZReportAsync(Guid companyId,
        Guid? sessionId, Guid? terminalId, string? terminalName,
        string? openedByName, string? closedByName,
        DateTime from, DateTime to, decimal openingCash, decimal closingCash)
    {
        var q = _db.PosOrders.AsNoTracking()
            .Include(o => o.Payments)
            .Include(o => o.Items)
            .Where(o => o.CompanyId == companyId
                && o.CreatedAt >= from && o.CreatedAt <= to);
        if (sessionId.HasValue) q = q.Where(o => o.SessionId == sessionId.Value);
        if (terminalId.HasValue) q = q.Where(o => o.Session.TerminalId == terminalId.Value);
        var orders = await q.ToListAsync();

        var completed = orders.Where(o => o.Status == PosOrderStatus.Completed).ToList();
        var voided = orders.Where(o => o.Status == PosOrderStatus.Voided).ToList();
        var refunded = orders.Where(o => o.Status == PosOrderStatus.Refunded).ToList();

        var paymentBreakdown = completed.Concat(refunded)
            .SelectMany(o => o.Payments)
            .GroupBy(p => p.PaymentMethod)
            .Select(g => new PaymentMethodSummary(g.Key, g.Key.ToString(),
                g.Count(), g.Sum(p => p.Amount)))
            .OrderByDescending(p => p.Amount)
            .ToList();

        var cashSales = completed.SelectMany(o => o.Payments)
            .Where(p => p.PaymentMethod == PaymentMethod.Cash)
            .Sum(p => p.Amount);
        var cashRefunds = refunded.SelectMany(o => o.Payments)
            .Where(p => p.PaymentMethod == PaymentMethod.Cash)
            .Sum(p => p.Amount);
        var expectedCash = openingCash + cashSales - cashRefunds;
        var variance = closingCash > 0 ? closingCash - expectedCash : 0m;

        // Top 10 products
        var top = completed.SelectMany(o => o.Items)
            .Where(i => i.ProductId.HasValue && i.Status != PosItemStatus.Cancelled)
            .GroupBy(i => new { i.ProductId, ItemName = i.ItemName })
            .Select(g => new PosTopProductSummary(
                g.Key.ProductId!.Value, g.Key.ItemName ?? "(ไม่ระบุ)",
                g.Sum(x => x.Quantity), g.Sum(x => x.TotalAmount)))
            .OrderByDescending(p => p.Revenue).Take(10).ToList();

        return new PosZReportResponse(
            from, to, sessionId, terminalId, terminalName, openedByName, closedByName,
            TotalOrders: orders.Count,
            CompletedOrders: completed.Count,
            VoidedOrders: voided.Count,
            RefundedOrders: refunded.Count,
            GuestCount: completed.Sum(o => o.GuestCount ?? 0),
            GrossSales: completed.Sum(o => o.SubTotal),
            TotalDiscount: completed.Sum(o => o.DiscountAmount),
            NetSales: completed.Sum(o => o.NetAmount),
            TotalVat: completed.Sum(o => o.VatAmount),
            TotalServiceCharge: completed.Sum(o => o.ServiceChargeAmount),
            TotalTip: completed.Sum(o => o.TipAmount),
            RefundedAmount: refunded.Sum(o => o.TotalAmount),
            VoidedAmount: voided.Sum(o => o.TotalAmount),
            PaymentBreakdown: paymentBreakdown,
            OpeningCash: openingCash,
            ClosingCash: closingCash,
            CashSales: cashSales,
            CashRefunds: cashRefunds,
            ExpectedCash: expectedCash,
            CashVariance: variance,
            TopProducts: top);
    }

    public async Task<List<CommissionSummaryResponse>> GetCommissionSummariesAsync(Guid companyId, DateTime periodStart, DateTime periodEnd)
    {
        var summaries = await _db.StaffCommissionSummaries
            .Where(s => s.CompanyId == companyId && s.PeriodStart >= periodStart && s.PeriodEnd <= periodEnd)
            .OrderByDescending(s => s.TotalCommission)
            .ToListAsync();

        // ★ รอบ 193 หลังฝ่ายค้าน (C5): ธงที่จุดเงินไหล — กิจกรรมจากขั้นตอนที่ประเภทคอมมิชชันต้องตรวจ
        //   (ข้อมูลเก่าคิดกลับด้าน รอเจ้าของตัดสิน ห้ามแปลง) ⇒ นับต่อพนักงานในช่วงของแถวสรุป
        var staffIds = summaries.Select(s => s.StaffId).Distinct().ToList();
        var reviewRows = staffIds.Count == 0 ? new List<(Guid StaffId, DateTime At)>() : (await (
            from a in _db.Set<PosServiceActivity>().AsNoTracking()
            join oi in _db.Set<PosOrderItem>().AsNoTracking() on a.OrderItemId equals oi.Id
            join o in _db.Set<PosOrder>().AsNoTracking() on oi.OrderId equals o.Id
            join c in _db.Set<ServiceComponent>().AsNoTracking() on a.ComponentId equals c.Id
            where o.CompanyId == companyId && a.StaffId != null && staffIds.Contains(a.StaffId.Value)
                && o.CreatedAt >= periodStart && o.CreatedAt <= periodEnd
            select new { StaffId = a.StaffId!.Value, o.CreatedAt, c.CommissionType, c.CommissionTypeConfirmedAt }
        ).ToListAsync())
            .Where(r => Accounting.Helpers.ServiceCommissionTypeReview.NeedsReview(
                Accounting.Helpers.ServiceCommissionTypeReview.Judge(r.CommissionType, r.CommissionTypeConfirmedAt)))
            .Select(r => (StaffId: r.StaffId, At: r.CreatedAt))
            .ToList();

        return summaries.Select(s => new CommissionSummaryResponse(
            s.Id, s.StaffId, s.StaffName, s.PeriodStart, s.PeriodEnd,
            s.TotalActivities, s.TotalCommission, s.PaidAmount, s.RemainingAmount, s.IsPaid,
            ActivitiesNeedingTypeReview: reviewRows.Count(r => r.StaffId == s.StaffId
                && r.At >= s.PeriodStart && r.At <= s.PeriodEnd)
        )).ToList();
    }

    public async Task<List<CommissionDetailResponse>> GetCommissionDetailsAsync(
        Guid companyId, Guid staffId, DateTime periodStart, DateTime periodEnd)
    {
        // Join activity → orderItem → order, scoped to company + staff + period.
        // Period filter uses Order.CreatedAt (inherited from BaseEntity — the
        // moment the order was rung up) so a single payout window matches
        // the StaffCommissionSummaries row.
        var rows = await (
            from a in _db.Set<PosServiceActivity>().AsNoTracking()
            join oi in _db.Set<PosOrderItem>().AsNoTracking() on a.OrderItemId equals oi.Id
            join o in _db.Set<PosOrder>().AsNoTracking() on oi.OrderId equals o.Id
            join c in _db.Set<ServiceComponent>().AsNoTracking() on a.ComponentId equals c.Id
            where o.CompanyId == companyId
                && a.StaffId == staffId
                && o.CreatedAt >= periodStart && o.CreatedAt <= periodEnd
            orderby o.CreatedAt descending, o.OrderNumber, oi.LineOrder
            select new
            {
                a.Id,
                OrderId = o.Id,
                o.OrderNumber,
                OrderDate = o.CreatedAt,
                OrderItemId = oi.Id,
                ItemName = oi.ItemName,
                ComponentName = c.Name,
                Status = a.Status,
                a.CompletedAt,
                a.CommissionAmount,
                a.Notes,
                c.CommissionType,
                c.CommissionTypeConfirmedAt,
            }
        ).ToListAsync();

        return rows.Select(r =>
        {
            // ★ รอบ 193 หลังฝ่ายค้าน (C5): ยอดคอมของขั้นตอนที่ประเภทยังไม่ยืนยันอาจคิดกลับด้าน — ธงจากเซิร์ฟเวอร์
            var clarity = Accounting.Helpers.ServiceCommissionTypeReview.Judge(r.CommissionType, r.CommissionTypeConfirmedAt);
            return new CommissionDetailResponse(
                r.Id, r.OrderId, r.OrderNumber, r.OrderDate, r.OrderItemId,
                r.ItemName, r.ComponentName, r.Status.ToString(), r.CompletedAt,
                r.CommissionAmount, r.Notes,
                CommissionTypeNeedsReview: Accounting.Helpers.ServiceCommissionTypeReview.NeedsReview(clarity),
                CommissionTypeReviewNote: Accounting.Helpers.ServiceCommissionTypeReview.Note(clarity));
        }).ToList();
    }

    // ==================== Helpers ====================

    private async Task<decimal> GetCompanyVatRateAsync(Guid companyId)
    {
        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
        // บริษัทไม่จด VAT → คิด 0% (ห้ามเก็บ/ลง output VAT — §90/2). POS ลง JE
        // เอง (21911) ไม่ผ่าน ApproveDocumentAsync จึงต้องกันที่ต้นทางตรงนี้
        // ตัวตัดสินเดียวกับเส้นอื่นที่สร้างเอกสารขายเองโดยไม่ผ่าน Approve
        // (Time Billing / integration) — Helpers/OutputVatRate
        return Accounting.Helpers.OutputVatRate.ForCompany(
            company?.IsVatRegistered ?? false, company?.VatRate ?? 0m);
    }

    private async Task AddItemToOrder(PosOrder order, CreateOrderItemRequest req, int lineOrder, decimal vatRate)
    {
        // ProductId/ServicePackageId มาจาก client — ต้องเป็นของบริษัทเดียวกับ
        // order เท่านั้น: ปลายทาง (Complete/Void/Refund) ใช้ FindAsync ตัดสต๊อก/
        // อ่านต้นทุนโดยไม่กรอง tenant ถ้าปล่อยผ่านตรงนี้ user บริษัท A จะตัด
        // สต๊อกและอ่านต้นทุนสินค้าของบริษัท B ได้
        if (req.ProductId.HasValue)
        {
            var okProduct = await _db.Products.AsNoTracking()
                .AnyAsync(p => p.Id == req.ProductId.Value && p.CompanyId == order.CompanyId);
            if (!okProduct) throw new KeyNotFoundException("ไม่พบสินค้าในบริษัทนี้");
        }
        if (req.ServicePackageId.HasValue)
        {
            var okPackage = await _db.ServicePackages.AsNoTracking()
                .AnyAsync(p => p.Id == req.ServicePackageId.Value && p.CompanyId == order.CompanyId);
            if (!okPackage) throw new KeyNotFoundException("ไม่พบแพ็กเกจบริการในบริษัทนี้");
        }

        var item = new PosOrderItem
        {
            OrderId = order.Id,
            ProductId = req.ProductId,
            ServicePackageId = req.ServicePackageId,
            ItemName = req.ItemName,
            ItemCode = req.ItemCode,
            Quantity = req.Quantity,
            Unit = req.Unit,
            UnitPrice = req.UnitPrice,
            DiscountPercent = req.DiscountPercent,
            LineOrder = lineOrder,
            Notes = req.Notes
        };

        // Calculate line totals
        var modifierTotal = req.Modifiers?.Sum(m => m.PriceAdjustment) ?? 0;
        var linePrice = (item.UnitPrice + modifierTotal) * item.Quantity;
        item.DiscountAmount = linePrice * item.DiscountPercent / 100;
        item.SubTotal = linePrice - item.DiscountAmount;
        item.VatAmount = vatRate > 0
            ? Math.Round(item.SubTotal * vatRate / (100 + vatRate), 2, MidpointRounding.AwayFromZero)
            : 0;
        item.TotalAmount = item.SubTotal;

        _db.PosOrderItems.Add(item);

        // Add modifiers
        if (req.Modifiers != null)
        {
            foreach (var mod in req.Modifiers)
            {
                _db.PosOrderItemModifiers.Add(new PosOrderItemModifier
                {
                    OrderItemId = item.Id,
                    ModifierOptionId = mod.ModifierOptionId,
                    ModifierGroupName = mod.ModifierGroupName,
                    ModifierName = mod.ModifierName,
                    PriceAdjustment = mod.PriceAdjustment
                });
            }
        }

        // Create service activities if it's a service package
        if (req.ServicePackageId.HasValue)
        {
            var components = await _db.ServiceComponents
                .Where(c => c.PackageId == req.ServicePackageId.Value)
                .OrderBy(c => c.StepOrder)
                .ToListAsync();

            foreach (var comp in components)
            {
                _db.PosServiceActivities.Add(new PosServiceActivity
                {
                    OrderItemId = item.Id,
                    ComponentId = comp.Id,
                    CommissionAmount = comp.CommissionType == CommissionType.Fixed
                        ? comp.CommissionValue
                        : Math.Round(item.TotalAmount * comp.CommissionValue / 100, 2, MidpointRounding.AwayFromZero)
                });
            }
        }

        order.Items.Add(item);
    }

    /// <summary>คิดยอดทั้งบิลใหม่จากรายการที่ยังอยู่ + ธงระดับบิล
    /// (internal เพื่อให้เทสต์ยืนยัน invariant "แยกบิลแล้วยอดรวมต้องเท่าเดิม" ได้
    /// — เดิมส่วนลด/ค่าบริการหายตอนแยกบิลเพราะใบลูกไม่ได้สืบทอดเปอร์เซ็นต์มา)</summary>
    internal static void RecalculateOrder(PosOrder order, decimal vatRate)
    {
        var activeItems = order.Items.Where(i => !i.IsDeleted).ToList();
        order.SubTotal = activeItems.Sum(i => i.SubTotal);
        // DiscountAmount = % discount + coupon discount. CouponDiscountAmount is
        // tracked separately for reporting but it stacks on the same line.
        var pctDiscount = order.SubTotal * order.DiscountPercent / 100;
        order.DiscountAmount = pctDiscount + order.CouponDiscountAmount;
        var afterDiscount = order.SubTotal - order.DiscountAmount;
        if (afterDiscount < 0) afterDiscount = 0; // coupon can't drive total negative
        order.ServiceChargeAmount = Math.Round(afterDiscount * order.ServiceChargePercent / 100, 2, MidpointRounding.AwayFromZero);
        order.TotalAmount = afterDiscount + order.ServiceChargeAmount;
        order.VatAmount = vatRate > 0
            ? Math.Round(order.TotalAmount * vatRate / (100 + vatRate), 2, MidpointRounding.AwayFromZero)
            : 0;
        order.RoundingAmount = Math.Round(order.TotalAmount) - order.TotalAmount;
        // Tip is added AFTER rounding so the lookup amount stays clean — what the
        // customer hands over is NetAmount + TipAmount.
        order.NetAmount = order.TotalAmount + order.RoundingAmount + order.TipAmount;
    }

    private static OrderResponse MapOrder(PosOrder o) => new(
        o.Id, o.SessionId, o.OrderNumber, o.OrderType, o.Status,
        o.CustomerId, o.CustomerName, o.TableNumber, o.GuestCount,
        o.QueueNumber, o.AppointmentTime, o.PrimaryStaffId,
        o.SubTotal, o.DiscountAmount, o.DiscountPercent,
        o.ServiceChargePercent, o.ServiceChargeAmount, o.VatAmount,
        o.TotalAmount, o.RoundingAmount, o.NetAmount,
        o.Notes, o.Reference, o.JournalEntryId, o.CompletedAt, o.CreatedAt,
        o.Items.Where(i => !i.IsDeleted).Select(MapOrderItem).ToList(),
        o.Payments.Select(MapPayment).ToList(),
        DocumentId: o.DocumentId,
        DocumentNumber: null,
        TipAmount: o.TipAmount,
        CouponCode: o.CouponCode,
        CouponDiscountAmount: o.CouponDiscountAmount,
        BranchId: o.BranchId,
        WarehouseId: o.WarehouseId,
        AbbreviatedInvoiceNumber: o.AbbreviatedInvoiceNumber,
        IssuerBranchCode: o.IssuerBranchCode,
        IssuerBranchLabel: Accounting.Helpers.PosSlipHeader.BranchLabel(o.IssuerBranchCode),
        // บิลที่ออกเลขอย่างย่อไปแล้ว = หัวถูกตรึงตั้งแต่ตอนปิดบิล (§86/4 ห้ามแก้ย้อนหลัง)
        // บิลที่ยังไม่ปิด ยังไม่รู้ผล — ปล่อย null ให้หน้าเว็บถามตอนจะพิมพ์
        SlipTitle: o.AbbreviatedInvoiceNumber != null
            ? Accounting.Helpers.PosSlipHeader.AbbreviatedTaxInvoice
            : o.Status == PosOrderStatus.Completed ? Accounting.Helpers.PosSlipHeader.Receipt : null);

    private static OrderItemResponse MapOrderItem(PosOrderItem i) => new(
        i.Id, i.ProductId, i.ServicePackageId, i.ItemName, i.ItemCode,
        i.Quantity, i.Unit, i.UnitPrice, i.DiscountAmount, i.DiscountPercent,
        i.SubTotal, i.VatAmount, i.TotalAmount, i.LineOrder, i.Status, i.Notes,
        i.Modifiers.Select(m => new ItemModifierResponse(m.Id, m.ModifierOptionId, m.ModifierGroupName, m.ModifierName, m.PriceAdjustment)).ToList(),
        i.ServiceActivities.Select(a => new ServiceActivityResponse(a.Id, a.ComponentId, a.Component?.Name ?? "", a.Component?.StepOrder ?? 0, a.StaffId, a.StaffName, a.Status, a.StartedAt, a.CompletedAt, a.CommissionAmount, a.Notes)).ToList(),
        i.RefundedQuantity);

    private static PaymentResponse MapPayment(PosPayment p) => new(
        p.Id, p.PaymentMethod, p.Amount, p.ReceivedAmount, p.ChangeAmount, p.ReferenceNo, p.CardLastFour, p.PaidAt);

    // Cash/bank/card account picker keyed by PaymentMethod. Defaults match
    // the Thai SME chart-of-accounts seeded by SeedCoaService:
    //   1011 เงินสด / 1012 ธนาคาร / 1131 บัตรเครดิตค้างรับ
    // Fallback by prefix lets companies with a customized COA still resolve.
    /// <summary>บัญชีพักของ gateway สำหรับรายการที่จ่ายผ่านระบบรับชำระออนไลน์ —
    /// <c>null</c> = ไม่ได้จ่ายผ่าน gateway (หรือ gateway นั้นเงินเข้าธนาคารทันที)
    /// ⇒ ผู้เรียกตกไปใช้ผังตามวิธีจ่ายเหมือนเดิม
    ///
    /// <para>กติกาอยู่ที่ <see cref="IGatewayAccountResolver"/> ที่เดียวของระบบ —
    /// ห้าม POS ตัดสินเองว่าเจ้าไหนเข้าธนาคารทันที (จะกลายเป็นกติกาชุดที่สอง)</para></summary>
    private async Task<Accounting.Models.Entities.ChartOfAccount?> ResolveGatewayClearingAsync(
        Guid companyId, PosPayment pay)
    {
        if (_gatewayAccounts == null || pay.PaymentIntentId is not Guid intentId) return null;
        var intent = await _db.PaymentIntents.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId);
        if (intent == null) return null;
        var accId = await _gatewayAccounts.ResolveMoneyInAccountAsync(intent);
        if (accId is not Guid id) return null;
        return await _db.ChartOfAccounts.FirstOrDefaultAsync(
            a => a.Id == id && a.CompanyId == companyId && !a.IsDeleted);
    }

    private async Task<Accounting.Models.Entities.ChartOfAccount?> ResolvePaymentAccountAsync(
        Guid companyId, PaymentMethod method, PosTerminal? terminal = null)
    {
        // บัญชีที่ตั้งไว้ **บนเครื่อง** ชนะเสมอ — สาขาที่มีบัญชีธนาคาร/ลิ้นชักเงินสด
        // ของตัวเองต้องลงคนละบัญชี ไม่งั้นเงินของทุกสาขากองรวมกันแล้วกระทบยอด
        // ธนาคารรายสาขาไม่ได้เลย (ค่า null = ใช้ผังบัญชีตามวิธีจ่ายเหมือนเดิม)
        var pinned = method == PaymentMethod.Cash ? terminal?.CashAccountId : terminal?.BankAccountId;
        if (pinned is Guid acctId)
        {
            var pinnedAccount = await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.Id == acctId && a.CompanyId == companyId);
            if (pinnedAccount != null) return pinnedAccount;
            // ตั้งไว้แต่หาไม่เจอ (ถูกลบ/ย้ายบริษัท) → ตกไปใช้ผังบัญชีมาตรฐาน
            // แต่ต้องดัง ไม่ใช่เงียบ — เงินจะลงบัญชีที่เจ้าของไม่ได้ตั้งใจ
            _logger.LogWarning(
                "เครื่อง POS {Terminal} ตั้งบัญชีรับเงิน {Account} ไว้ แต่ไม่พบในผังบัญชีของบริษัท {Company} — ใช้บัญชีมาตรฐานแทน",
                terminal?.Name, acctId, companyId);
        }

        string preferred; string prefix;
        switch (method)
        {
            case PaymentMethod.Cash:
                preferred = "1011"; prefix = "111"; break;
            case PaymentMethod.BankTransfer:
            case PaymentMethod.PromptPay:
            case PaymentMethod.DirectDebit:
            case PaymentMethod.EWallet:
                preferred = "1012"; prefix = "112"; break;
            case PaymentMethod.CreditCard:
                preferred = "1131"; prefix = "113"; break;
            case PaymentMethod.Cheque:
                preferred = "1012"; prefix = "112"; break;
            default:
                preferred = "1011"; prefix = "111"; break;
        }

        return await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == preferred)
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith(prefix) && a.Level >= 4);
    }

    /// <summary>Merge one or more open orders into a destination order — items
    /// move to the destination, source orders get voided. Used when two parties
    /// at separate tables decide to share one bill. Both sides must be Open,
    /// unpaid, and belong to the same session/company. Returns the destination.</summary>
    public async Task<OrderResponse> MergeOrdersAsync(Guid companyId, Guid destinationOrderId, List<Guid> sourceOrderIds, string userId)
    {
        if (sourceOrderIds == null || sourceOrderIds.Count == 0)
            throw new ArgumentException("ระบุ source order อย่างน้อย 1 บิล");
        if (sourceOrderIds.Contains(destinationOrderId))
            throw new ArgumentException("source ห้ามมีปลายทางอยู่ในนั้น");

        var dest = await _db.PosOrders.Include(o => o.Items).Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == destinationOrderId && o.CompanyId == companyId && !o.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบบิลปลายทาง");
        if (dest.Status == PosOrderStatus.Completed || dest.Status == PosOrderStatus.Voided)
            throw new InvalidOperationException("บิลปลายทางปิดแล้ว — รวมไม่ได้");
        if (dest.Payments.Any(p => !p.IsDeleted))
            throw new InvalidOperationException("บิลปลายทางมีการชำระเงินแล้ว — ยกเลิกการชำระก่อน");

        var sources = await _db.PosOrders.Include(o => o.Items).Include(o => o.Payments)
            .Where(o => sourceOrderIds.Contains(o.Id) && o.CompanyId == companyId && !o.IsDeleted)
            .ToListAsync();
        if (sources.Count != sourceOrderIds.Count)
            throw new InvalidOperationException("มีบิล source บางใบหาไม่เจอ / ถูกลบไปแล้ว");
        foreach (var src in sources)
        {
            if (src.Status == PosOrderStatus.Completed || src.Status == PosOrderStatus.Voided)
                throw new InvalidOperationException($"บิล {src.OrderNumber} ปิดแล้ว — รวมไม่ได้");
            if (src.Payments.Any(p => !p.IsDeleted))
                throw new InvalidOperationException($"บิล {src.OrderNumber} มีการชำระเงินแล้ว — ยกเลิกก่อน");
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var nextLine = dest.Items.Where(i => !i.IsDeleted).Select(i => i.LineOrder).DefaultIfEmpty(0).Max() + 1;
            foreach (var src in sources)
            {
                foreach (var srcItem in src.Items.Where(i => !i.IsDeleted))
                {
                    dest.Items.Add(new PosOrderItem
                    {
                        // PosOrderItem is BaseEntity — CompanyId inherited via dest's OrderId.
                        ProductId = srcItem.ProductId,
                        ServicePackageId = srcItem.ServicePackageId,
                        ItemName = srcItem.ItemName,
                        ItemCode = srcItem.ItemCode,
                        Unit = srcItem.Unit,
                        Quantity = srcItem.Quantity,
                        UnitPrice = srcItem.UnitPrice,
                        DiscountAmount = srcItem.DiscountAmount,
                        DiscountPercent = srcItem.DiscountPercent,
                        SubTotal = srcItem.SubTotal,
                        TotalAmount = srcItem.TotalAmount,
                        VatAmount = 0,
                        LineOrder = nextLine++,
                        Status = srcItem.Status,
                        Notes = srcItem.Notes != null ? $"{srcItem.Notes} [จาก {src.OrderNumber}]" : $"[จาก {src.OrderNumber}]",
                    });
                    srcItem.IsDeleted = true;
                }
                src.Status = PosOrderStatus.Voided;
                src.Notes = string.IsNullOrEmpty(src.Notes)
                    ? $"รวมเข้าบิล {dest.OrderNumber}"
                    : $"{src.Notes} / รวมเข้าบิล {dest.OrderNumber}";
                src.UpdatedBy = userId;
            }

            // Append a note on destination so the audit trail tells the story.
            var mergedFrom = string.Join(", ", sources.Select(s => s.OrderNumber));
            dest.Notes = string.IsNullOrEmpty(dest.Notes)
                ? $"รวมจากบิล: {mergedFrom}"
                : $"{dest.Notes} / รวมจากบิล: {mergedFrom}";
            dest.UpdatedBy = userId;

            var vatRate = await GetCompanyVatRateAsync(companyId);
            RecalculateOrder(dest, vatRate);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        return await GetOrderAsync(companyId, dest.Id);
    }

    /// <summary>Move an order to a different table number. The item list,
    /// payments, and totals stay intact — only the TableNumber changes,
    /// with an audit note appended.</summary>
    public async Task<OrderResponse> TransferTableAsync(Guid companyId, Guid orderId, string? newTableNumber, string userId)
    {
        var order = await _db.PosOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId && !o.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status == PosOrderStatus.Completed || order.Status == PosOrderStatus.Voided)
            throw new InvalidOperationException("ออเดอร์ปิด/ยกเลิกแล้ว — ย้ายโต๊ะไม่ได้");
        var old = order.TableNumber ?? "(ไม่ระบุ)";
        var dest = string.IsNullOrWhiteSpace(newTableNumber) ? null : newTableNumber.Trim();
        order.TableNumber = dest;
        order.Notes = string.IsNullOrEmpty(order.Notes)
            ? $"ย้ายโต๊ะ {old} → {dest ?? "(ไม่ระบุ)"}"
            : $"{order.Notes} / ย้ายโต๊ะ {old} → {dest ?? "(ไม่ระบุ)"}";
        order.UpdatedBy = userId;
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, orderId);
    }

    /// <summary>Email a plain-HTML copy of the receipt to a customer address.
    /// Uses the company's email sender (Microsoft Graph / Gmail / SMTP) — falls
    /// back to the global SMTP if no per-company config exists. The order must
    /// be Completed; we don't send drafts.</summary>
    public async Task EmailReceiptAsync(Guid companyId, Guid orderId, string email)
    {
        if (_emailFactory == null)
            throw new InvalidOperationException("ระบบส่งอีเมลยังไม่ตั้งค่า — ติดต่อแอดมิน");
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            throw new ArgumentException("อีเมลไม่ถูกต้อง");
        var order = await _db.PosOrders
            .Include(o => o.Items).Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId && !o.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status != PosOrderStatus.Completed)
            throw new InvalidOperationException("ออเดอร์ยังไม่ปิดบิล — ส่งใบเสร็จไม่ได้");
        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
        var companyName = company?.Name ?? company?.NameEn ?? "ร้านค้า";

        var itemsHtml = string.Concat(order.Items.Where(i => !i.IsDeleted).Select(i =>
            $"<tr><td>{System.Net.WebUtility.HtmlEncode(i.ItemName)}</td><td style='text-align:center'>{i.Quantity}</td><td style='text-align:right'>{i.UnitPrice:N2}</td><td style='text-align:right'>{i.TotalAmount:N2}</td></tr>"));
        var paymentsHtml = string.Concat(order.Payments.Where(p => !p.IsDeleted).Select(p =>
            $"<tr><td>{PaymentMethodThaiLabel(p.PaymentMethod)}</td><td style='text-align:right'>{p.Amount:N2}</td></tr>"));

        var html = $@"<!DOCTYPE html><html><body style='font-family:Tahoma,sans-serif;max-width:520px;margin:auto;padding:20px;color:#0f172a'>
<h2 style='text-align:center;margin:0 0 4px'>{System.Net.WebUtility.HtmlEncode(companyName)}</h2>
<div style='text-align:center;color:#64748b;font-size:13px;margin-bottom:14px'>ใบเสร็จรับเงิน #{order.OrderNumber}</div>
<div style='border-top:1px dashed #cbd5e1;border-bottom:1px dashed #cbd5e1;padding:10px 0'>
  <div>วันที่: {order.CompletedAt:yyyy-MM-dd HH:mm}</div>
  {(string.IsNullOrEmpty(order.TableNumber) ? "" : $"<div>โต๊ะ: {order.TableNumber}</div>")}
  {(string.IsNullOrEmpty(order.CustomerName) ? "" : $"<div>ลูกค้า: {System.Net.WebUtility.HtmlEncode(order.CustomerName)}</div>")}
</div>
<table style='width:100%;border-collapse:collapse;margin:14px 0;font-size:13px'>
  <thead><tr style='background:#f1f5f9'><th style='text-align:left;padding:6px'>รายการ</th><th style='padding:6px'>จน.</th><th style='text-align:right;padding:6px'>ราคา</th><th style='text-align:right;padding:6px'>รวม</th></tr></thead>
  <tbody>{itemsHtml}</tbody>
</table>
<div style='border-top:1px dashed #cbd5e1;padding-top:10px'>
  <div style='display:flex;justify-content:space-between'><span>รวม</span><span>{order.SubTotal:N2}</span></div>
  {(order.DiscountAmount > 0 ? $"<div style='display:flex;justify-content:space-between;color:#dc2626'><span>ส่วนลด</span><span>-{order.DiscountAmount:N2}</span></div>" : "")}
  {(order.ServiceChargeAmount > 0 ? $"<div style='display:flex;justify-content:space-between'><span>Service Charge</span><span>{order.ServiceChargeAmount:N2}</span></div>" : "")}
  {(order.VatAmount > 0 ? $"<div style='display:flex;justify-content:space-between'><span>VAT</span><span>{order.VatAmount:N2}</span></div>" : "")}
  {(order.TipAmount > 0 ? $"<div style='display:flex;justify-content:space-between;color:#16a34a'><span>ทิป</span><span>{order.TipAmount:N2}</span></div>" : "")}
  <div style='display:flex;justify-content:space-between;font-weight:700;font-size:16px;border-top:1px solid #0f172a;padding-top:6px;margin-top:6px'><span>ยอดสุทธิ</span><span>{order.NetAmount:N2} ฿</span></div>
</div>
<table style='width:100%;margin-top:14px;font-size:13px'>
  <thead><tr style='background:#f1f5f9'><th style='text-align:left;padding:6px'>การชำระเงิน</th><th style='text-align:right;padding:6px'>จำนวน</th></tr></thead>
  <tbody>{paymentsHtml}</tbody>
</table>
<div style='text-align:center;color:#64748b;font-size:11px;margin-top:20px'>ขอบคุณที่ใช้บริการ — ส่งจากระบบ POS อัตโนมัติ</div>
</body></html>";

        var sender = await _emailFactory.GetSenderAsync(companyId);
        var msg = new EmailMessage
        {
            FromAddress = company?.Email ?? "noreply@accounting.local",
            FromName = companyName,
            To = new List<string> { email.Trim() },
            Subject = $"ใบเสร็จรับเงิน #{order.OrderNumber} - {companyName}",
            HtmlBody = html,
        };
        var result = await sender.SendAsync(msg);
        if (!result.Success)
            throw new InvalidOperationException("ส่งอีเมลไม่สำเร็จ: " + result.ErrorMessage);
        _logger.LogInformation("Receipt for order {Order} emailed to {Email}", order.OrderNumber, email);
    }

    private static string PaymentMethodThaiLabel(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash         => "เงินสด",
        PaymentMethod.BankTransfer => "โอนธนาคาร",
        PaymentMethod.PromptPay    => "พร้อมเพย์",
        PaymentMethod.CreditCard   => "บัตรเครดิต",
        PaymentMethod.Cheque       => "เช็ค",
        PaymentMethod.DirectDebit  => "หักบัญชี",
        PaymentMethod.EWallet      => "e-Wallet",
        _                          => m.ToString()
    };
}
