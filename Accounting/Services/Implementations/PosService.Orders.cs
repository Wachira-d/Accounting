using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Pos;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class PosService
{
    // ==================== Order CRUD ====================

    public async Task<OrderResponse> CreateOrderAsync(Guid companyId, CreateOrderRequest request, string createdBy)
    {
        var session = await _db.PosSessions.FirstOrDefaultAsync(s => s.Id == request.SessionId && s.CompanyId == companyId && s.Status == PosSessionStatus.Open)
            ?? throw new KeyNotFoundException("ไม่พบกะการขายที่เปิดอยู่");

        var posYm = DateTime.UtcNow.ToString("yyyyMM");
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

        var order = new PosOrder
        {
            CompanyId = companyId,
            SessionId = request.SessionId,
            OrderNumber = orderNumber,
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
        order.Status = request.Status;
        if (request.Status == PosOrderStatus.Completed) order.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetOrderAsync(companyId, orderId);
    }

    public async Task VoidOrderAsync(Guid companyId, Guid orderId, string userId)
    {
        var order = await _db.PosOrders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status == PosOrderStatus.Voided) throw new InvalidOperationException("ออเดอร์นี้ถูกยกเลิกไปแล้ว");

        var wasCompleted = order.Status == PosOrderStatus.Completed;
        if (wasCompleted)
        {
            foreach (var item in order.Items.Where(i => i.ProductId.HasValue && !i.IsDeleted))
            {
                var product = await _db.Products.FindAsync(item.ProductId);
                if (product?.TrackStock == true)
                {
                    product.CurrentStock += item.Quantity;
                    _db.StockMovements.Add(new StockMovement
                    {
                        CompanyId = companyId,
                        ProductId = product.Id,
                        MovementDate = DateTime.UtcNow,
                        MovementType = "IN",
                        Quantity = item.Quantity,
                        UnitCost = product.CostPrice,
                        BalanceAfter = product.CurrentStock,
                        Reference = $"VOID-{order.OrderNumber}",
                        Notes = "คืนสต็อกจากการยกเลิกออเดอร์",
                        CreatedBy = userId
                    });
                }
            }
        }

        order.Status = PosOrderStatus.Voided;
        await _db.SaveChangesAsync();

        // Reverse the sales journal entry — voiding a completed POS sale must
        // back out the GL impact (cash/revenue/VAT/COGS), else revenue and
        // cash stay overstated.
        if (wasCompleted && order.JournalEntryId.HasValue)
        {
            try
            {
                await _accountingService.ReverseJournalEntryAsync(companyId, order.JournalEntryId.Value,
                    DateTime.UtcNow, $"กลับรายการ POS ยกเลิกบิล #{order.OrderNumber}", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS void: JE reversal failed for order {OrderNumber} in company {CompanyId}",
                    order.OrderNumber, companyId);
            }
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

        // Create journal entry for accounting integration
        await CreateSalesJournalEntryAsync(companyId, order, userId);

        // Deduct stock for product items
        foreach (var item in order.Items.Where(i => i.ProductId.HasValue && !i.IsDeleted))
        {
            var product = await _db.Products.FindAsync(item.ProductId);
            if (product?.TrackStock == true)
            {
                product.CurrentStock -= item.Quantity;
                _db.StockMovements.Add(new StockMovement
                {
                    CompanyId = companyId,
                    ProductId = product.Id,
                    MovementDate = DateTime.UtcNow,
                    MovementType = "OUT",
                    Quantity = -item.Quantity,
                    UnitCost = product.CostPrice,
                    BalanceAfter = product.CurrentStock,
                    Reference = order.OrderNumber,
                    Notes = "POS Sale"
                });
            }
        }

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

    private async Task CreateSalesJournalEntryAsync(Guid companyId, PosOrder order, string userId)
    {
        // Find accounts: Cash/Bank (Dr), Sales Revenue (Cr), VAT Payable (Cr)
        var cashAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "11111")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.Level >= 4);
        var salesAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "41000")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("41") && a.Level >= 4);
        var vatAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "21911")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("219") && a.Level >= 4);

        if (cashAccount == null || salesAccount == null) return; // Skip if no accounts configured

        var lines = new List<Models.DTOs.Accounting.JournalLineRequest>();

        // Debit: Cash / Bank
        lines.Add(new(cashAccount.Id, order.NetAmount, 0, $"รับเงิน POS #{order.OrderNumber}"));

        // Credit: Sales Revenue (net of VAT)
        var revenueAmount = order.NetAmount - order.VatAmount;
        lines.Add(new(salesAccount.Id, 0, revenueAmount, $"รายได้ขาย POS #{order.OrderNumber}"));

        // Credit: VAT Payable (if any)
        if (order.VatAmount > 0 && vatAccount != null)
            lines.Add(new(vatAccount.Id, 0, order.VatAmount, $"ภาษีขาย POS #{order.OrderNumber}"));

        // COGS: Dr ต้นทุนขาย / Cr สินค้าคงเหลือ — record cost of goods sold so
        // the P&L gross profit is correct (was previously omitted).
        var prodIds = order.Items.Where(i => i.ProductId.HasValue && !i.IsDeleted)
            .Select(i => i.ProductId!.Value).Distinct().ToList();
        if (prodIds.Count > 0)
        {
            var costByProduct = await _db.Products
                .Where(p => prodIds.Contains(p.Id) && p.TrackStock)
                .ToDictionaryAsync(p => p.Id, p => p.CostPrice);
            decimal totalCogs = 0;
            foreach (var item in order.Items.Where(i => i.ProductId.HasValue && !i.IsDeleted))
                if (costByProduct.TryGetValue(item.ProductId!.Value, out var cost))
                    totalCogs += item.Quantity * cost;
            totalCogs = Math.Round(totalCogs, 2, MidpointRounding.AwayFromZero);
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

        var journalRequest = new Models.DTOs.Accounting.CreateJournalEntryRequest(
            DateTime.UtcNow, $"POS Sale #{order.OrderNumber}", order.OrderNumber, lines);

        try
        {
            var journal = await _accountingService.CreateJournalEntryAsync(companyId, journalRequest, userId);
            order.JournalEntryId = journal.Id;
            await _accountingService.PostJournalEntryAsync(companyId, journal.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POS journal creation failed for order {OrderNumber} in company {CompanyId}. GL entry missing.",
                order.OrderNumber, companyId);
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

    public async Task<List<CommissionSummaryResponse>> GetCommissionSummariesAsync(Guid companyId, DateTime periodStart, DateTime periodEnd)
    {
        var summaries = await _db.StaffCommissionSummaries
            .Where(s => s.CompanyId == companyId && s.PeriodStart >= periodStart && s.PeriodEnd <= periodEnd)
            .OrderByDescending(s => s.TotalCommission)
            .ToListAsync();

        return summaries.Select(s => new CommissionSummaryResponse(
            s.Id, s.StaffId, s.StaffName, s.PeriodStart, s.PeriodEnd,
            s.TotalActivities, s.TotalCommission, s.PaidAmount, s.RemainingAmount, s.IsPaid
        )).ToList();
    }

    // ==================== Helpers ====================

    private async Task<decimal> GetCompanyVatRateAsync(Guid companyId)
    {
        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
        return company?.VatRate ?? 7;
    }

    private async Task AddItemToOrder(PosOrder order, CreateOrderItemRequest req, int lineOrder, decimal vatRate)
    {
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

    private static void RecalculateOrder(PosOrder order, decimal vatRate)
    {
        var activeItems = order.Items.Where(i => !i.IsDeleted).ToList();
        order.SubTotal = activeItems.Sum(i => i.SubTotal);
        order.DiscountAmount = order.SubTotal * order.DiscountPercent / 100;
        var afterDiscount = order.SubTotal - order.DiscountAmount;
        order.ServiceChargeAmount = Math.Round(afterDiscount * order.ServiceChargePercent / 100, 2, MidpointRounding.AwayFromZero);
        order.TotalAmount = afterDiscount + order.ServiceChargeAmount;
        order.VatAmount = vatRate > 0
            ? Math.Round(order.TotalAmount * vatRate / (100 + vatRate), 2, MidpointRounding.AwayFromZero)
            : 0;
        order.RoundingAmount = Math.Round(order.TotalAmount) - order.TotalAmount;
        order.NetAmount = order.TotalAmount + order.RoundingAmount;
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
        o.Payments.Select(MapPayment).ToList());

    private static OrderItemResponse MapOrderItem(PosOrderItem i) => new(
        i.Id, i.ProductId, i.ServicePackageId, i.ItemName, i.ItemCode,
        i.Quantity, i.Unit, i.UnitPrice, i.DiscountAmount, i.DiscountPercent,
        i.SubTotal, i.VatAmount, i.TotalAmount, i.LineOrder, i.Status, i.Notes,
        i.Modifiers.Select(m => new ItemModifierResponse(m.Id, m.ModifierOptionId, m.ModifierGroupName, m.ModifierName, m.PriceAdjustment)).ToList(),
        i.ServiceActivities.Select(a => new ServiceActivityResponse(a.Id, a.ComponentId, a.Component?.Name ?? "", a.Component?.StepOrder ?? 0, a.StaffId, a.StaffName, a.Status, a.StartedAt, a.CompletedAt, a.CommissionAmount, a.Notes)).ToList());

    private static PaymentResponse MapPayment(PosPayment p) => new(
        p.Id, p.PaymentMethod, p.Amount, p.ReceivedAmount, p.ChangeAmount, p.ReferenceNo, p.CardLastFour, p.PaidAt);
}
