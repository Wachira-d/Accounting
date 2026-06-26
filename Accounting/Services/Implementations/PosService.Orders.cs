using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Pos;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;   // EmailMessage / IEmailSenderFactory
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

    public async Task<OrderResponse> RefundOrderAsync(Guid companyId, Guid orderId, RefundOrderRequest request, string userId)
    {
        var order = await _db.PosOrders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบออเดอร์");
        if (order.Status != PosOrderStatus.Completed)
            throw new InvalidOperationException("คืนเงินได้เฉพาะออเดอร์ที่ปิดบิลแล้ว");
        if (request.Lines == null || request.Lines.Count == 0)
            throw new InvalidOperationException("กรุณาเลือกรายการที่จะคืนเงิน");

        // Validate + compute the refund (proportional to each line).
        decimal refundGross = 0, refundVat = 0, refundCogs = 0;
        var toRestore = new List<(PosOrderItem Item, decimal Qty)>();
        foreach (var line in request.Lines)
        {
            if (line.Quantity <= 0) continue;
            var item = order.Items.FirstOrDefault(i => i.Id == line.ItemId)
                ?? throw new InvalidOperationException("ไม่พบรายการในออเดอร์นี้");
            var remaining = item.Quantity - item.RefundedQuantity;
            if (line.Quantity > remaining + 0.0001m)
                throw new InvalidOperationException(
                    $"รายการ '{item.ItemName}' คืนได้ไม่เกิน {remaining:0.##} (ขอคืน {line.Quantity:0.##})");

            var ratio = item.Quantity > 0 ? line.Quantity / item.Quantity : 0m;
            refundGross += item.TotalAmount * ratio;
            refundVat += item.VatAmount * ratio;
            toRestore.Add((item, line.Quantity));
        }
        if (toRestore.Count == 0)
            throw new InvalidOperationException("ไม่มีรายการที่จะคืนเงิน");

        refundGross = Math.Round(refundGross, 2, MidpointRounding.AwayFromZero);
        refundVat = Math.Round(refundVat, 2, MidpointRounding.AwayFromZero);
        var refundNet = refundGross - refundVat;

        await using var txn = await _db.Database.BeginTransactionAsync();
        try
        {
            // Restore stock + accumulate COGS for stock-tracked products.
            foreach (var (item, qty) in toRestore)
            {
                item.RefundedQuantity += qty;
                if (item.ProductId.HasValue)
                {
                    var product = await _db.Products.FindAsync(item.ProductId.Value);
                    if (product?.TrackStock == true)
                    {
                        product.CurrentStock += qty;
                        refundCogs += product.CostPrice * qty;
                        _db.StockMovements.Add(new StockMovement
                        {
                            CompanyId = companyId,
                            ProductId = product.Id,
                            MovementDate = DateTime.UtcNow,
                            MovementType = "IN",
                            Quantity = qty,
                            UnitCost = product.CostPrice,
                            BalanceAfter = product.CurrentStock,
                            Reference = $"REFUND-{order.OrderNumber}",
                            Notes = "คืนสินค้าจากการคืนเงิน POS",
                            CreatedBy = userId,
                        });
                    }
                }
            }
            refundCogs = Math.Round(refundCogs, 2, MidpointRounding.AwayFromZero);

            // Reversal journal entry — back out the refunded portion:
            //   Dr รายได้ขาย / Dr ภาษีขาย   Cr เงินสด
            //   Dr สินค้าคงเหลือ            Cr ต้นทุนขาย
            await CreateRefundJournalEntryAsync(companyId, order, refundNet, refundVat, refundGross, refundCogs, userId);

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
            if (order.Items.All(i => i.RefundedQuantity >= i.Quantity - 0.0001m))
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

    private async Task CreateRefundJournalEntryAsync(Guid companyId, PosOrder order,
        decimal refundNet, decimal refundVat, decimal refundGross, decimal refundCogs, string userId)
    {
        var cashAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "11111")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("111") && a.Level >= 4);
        var salesAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "41000")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("41") && a.Level >= 4);
        var vatAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "21911")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("219") && a.Level >= 4);
        if (cashAccount == null || salesAccount == null) return;

        var lines = new List<Models.DTOs.Accounting.JournalLineRequest>
        {
            new(salesAccount.Id, refundNet, 0, $"คืนรายได้ขาย POS #{order.OrderNumber}"),
        };
        if (refundVat > 0 && vatAccount != null)
            lines.Add(new(vatAccount.Id, refundVat, 0, $"คืนภาษีขาย POS #{order.OrderNumber}"));
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
                DateTime.UtcNow, $"POS Refund #{order.OrderNumber}", $"REFUND-{order.OrderNumber}", lines);
            var journal = await _accountingService.CreateJournalEntryAsync(companyId, journalRequest, userId);
            await _accountingService.PostJournalEntryAsync(companyId, journal.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POS refund journal creation failed for order {OrderNumber} in company {CompanyId}",
                order.OrderNumber, companyId);
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
        var buyerName = (request.BuyerName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(buyerName))
            throw new InvalidOperationException("กรุณาระบุชื่อผู้ซื้อ");

        await using var txn = await _db.Database.BeginTransactionAsync();
        try
        {
            // Resolve / create the buyer contact.
            var taxId = request.BuyerTaxId?.Trim();
            Models.Entities.Contact? contact = null;
            if (!string.IsNullOrWhiteSpace(taxId))
                contact = await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.TaxId == taxId);
            contact ??= await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Name == buyerName);
            if (contact == null)
            {
                contact = new Models.Entities.Contact
                {
                    CompanyId = companyId,
                    Name = buyerName,
                    TaxId = string.IsNullOrWhiteSpace(taxId) ? null : taxId,
                    BranchCode = string.IsNullOrWhiteSpace(request.BuyerBranchCode) ? null : request.BuyerBranchCode!.Trim(),
                    Address = string.IsNullOrWhiteSpace(request.BuyerAddress) ? null : request.BuyerAddress!.Trim(),
                    IsCustomer = true,
                    IsActive = true,
                    CreatedBy = userId,
                };
                _db.Contacts.Add(contact);
            }
            else
            {
                contact.IsCustomer = true; // make sure the role is set
            }

            // Build the Document directly — POS already posted its own JE for
            // this sale, so we must NOT go through ApproveDocumentAsync (would
            // create a duplicate JE). We stamp the JE's SourceDocumentId at
            // the end to suppress the VAT-report JE-fallback (avoids VAT
            // double-count: the Document path counts it, the JE path skips it).
            var docNumber = await Accounting.Helpers.DocumentNumberGenerator.NextAsync(
                _db, companyId, Models.Enums.DocumentType.TaxInvoice);

            var doc = new Models.Entities.Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = Models.Enums.DocumentType.TaxInvoice,
                DocumentDate = order.CompletedAt ?? DateTime.UtcNow,
                ContactId = contact.Id,
                Contact = contact,
                Status = Models.Enums.DocumentStatus.Approved,
                IsOpeningBalance = false,
                Reference = order.OrderNumber,
                Notes = request.Notes ?? $"ใบกำกับภาษีเต็มรูปจาก POS — ออเดอร์ {order.OrderNumber}",
                CreatedBy = userId,
            };

            decimal totalNet = 0, totalVat = 0;
            var lineOrder = 1;
            foreach (var i in order.Items.Where(x => !x.IsDeleted).OrderBy(x => x.LineOrder))
            {
                var lineGross = i.TotalAmount;
                var lineVat = i.VatAmount;
                var lineNet = lineGross - lineVat;
                var unitPriceNet = i.Quantity > 0 ? Math.Round(lineNet / i.Quantity, 4, MidpointRounding.AwayFromZero) : 0m;
                doc.Lines.Add(new Models.Entities.DocumentLine
                {
                    LineOrder = lineOrder++,
                    ProductCode = i.ItemCode,
                    Description = i.ItemName,
                    Quantity = i.Quantity,
                    Unit = i.Unit ?? "ชิ้น",
                    UnitPrice = unitPriceNet,
                    DiscountPercent = 0,
                    DiscountAmount = 0,
                    Amount = lineNet,
                    VatRate = lineNet > 0 ? Math.Round(lineVat * 100 / lineNet, 2, MidpointRounding.AwayFromZero) : 0m,
                    VatAmount = lineVat,
                });
                totalNet += lineNet;
                totalVat += lineVat;
            }
            doc.SubTotal = totalNet;
            doc.VatAmount = totalVat;
            doc.TotalAmount = totalNet + totalVat;
            doc.BalanceDue = 0;                 // POS already collected payment
            doc.PaidAmount = doc.TotalAmount;
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
            var posYm = DateTime.UtcNow.ToString("yyyyMM");
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

            var order = new PosOrder
            {
                CompanyId = companyId,
                SessionId = request.SessionId,
                OrderNumber = $"{posPrefix}{posSeq:D4}",
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

            // GL + stock — same as the online CompleteOrderAsync.
            await CreateSalesJournalEntryAsync(companyId, order, createdBy);
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
                        MovementDate = request.CompletedAt,
                        MovementType = "OUT",
                        Quantity = -item.Quantity,
                        UnitCost = product.CostPrice,
                        BalanceAfter = product.CurrentStock,
                        Reference = order.OrderNumber,
                        Notes = "POS Sale (offline sync)",
                    });
                }
            }

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
        var posYm = DateTime.UtcNow.ToString("yyyyMM");
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
                OrderType = parent.OrderType,
                CustomerName = parent.CustomerName,
                TableNumber = parent.TableNumber == null ? null : $"{parent.TableNumber}/{splitIdx}",
                Notes = $"แยกจาก {parent.OrderNumber} (ส่วนที่ {splitIdx})",
                Reference = parent.OrderNumber,
                Status = PosOrderStatus.Open,
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
                    VatAmount = 0,
                    LineOrder = srcItem.LineOrder,
                    Status = srcItem.Status,
                    Notes = srcItem.Notes,
                };
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
        // Sales / VAT / COGS / Inventory accounts — single source per company.
        var salesAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "41000")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("41") && a.Level >= 4);
        var vatAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "21911")
            ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("219") && a.Level >= 4);

        if (salesAccount == null) return; // Skip if no sales account configured

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
            var cashAccount = await ResolvePaymentAccountAsync(companyId, PaymentMethod.Cash);
            if (cashAccount == null) return;
            lines.Add(new(cashAccount.Id, order.NetAmount, 0, $"รับเงิน POS #{order.OrderNumber}"));
        }
        else
        {
            foreach (var pay in paymentsToBook)
            {
                var acct = await ResolvePaymentAccountAsync(companyId, pay.PaymentMethod);
                if (acct == null) continue;
                // Use Amount (allocated to invoice), not ReceivedAmount, so cash-tendered-with-change
                // posts the invoice value, not the full bill the customer handed over.
                var debit = pay.Amount;
                if (debit <= 0) continue;
                var methodLabel = PaymentMethodThaiLabel(pay.PaymentMethod);
                lines.Add(new(acct.Id, debit, 0, $"รับเงิน {methodLabel} POS #{order.OrderNumber}"));
            }
        }

        // Credit: Sales Revenue (net of VAT)
        var revenueAmount = order.NetAmount - order.VatAmount;
        lines.Add(new(salesAccount.Id, 0, revenueAmount, $"รายได้ขาย POS #{order.OrderNumber}"));

        // Credit: VAT Payable (if any)
        if (order.VatAmount > 0 && vatAccount != null)
            lines.Add(new(vatAccount.Id, 0, order.VatAmount, $"ภาษีขาย POS #{order.OrderNumber}"));

        // Credit: เงินรับฝาก-ทิปพนักงาน (Liability) — tip is NOT revenue, it's
        // held in trust for the staff and paid out via payroll. Cr 2160 by
        // default; fall back to any 21xx liability with "ทิป" in name.
        if (order.TipAmount > 0)
        {
            var tipAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "2160")
                ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("216") && a.Level >= 4)
                ?? await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("21") && a.AccountName.Contains("ทิป") && a.Level >= 4);
            if (tipAccount != null)
                lines.Add(new(tipAccount.Id, 0, order.TipAmount, $"ทิปลูกค้า POS #{order.OrderNumber}"));
            // If no tip-account exists, fold into Sales so JE balances — better
            // than dropping the entry. The log warning lets owner correct later.
            else
            {
                _logger.LogWarning("No tip-liability account (2160) for company {Cid}; tip {Tip:N2} posted to sales for order {Order}.",
                    companyId, order.TipAmount, order.OrderNumber);
                lines.Add(new(salesAccount.Id, 0, order.TipAmount, $"ทิป (ไม่มีบัญชี 2160) POS #{order.OrderNumber}"));
            }
        }

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

    /// <summary>Z-Report (สิ้นกะ) — query สรุปยอด session ที่ closed.
    /// Read-only — ไม่ post JE เพิ่ม (JE เกิดต่อออเดอร์ใน CompleteOrderAsync
    /// อยู่แล้ว). ใช้เทียบเงินสดในลิ้นชัก + audit ก่อนปิดงาน.</summary>
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

        return summaries.Select(s => new CommissionSummaryResponse(
            s.Id, s.StaffId, s.StaffName, s.PeriodStart, s.PeriodEnd,
            s.TotalActivities, s.TotalCommission, s.PaidAmount, s.RemainingAmount, s.IsPaid
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
                a.Notes
            }
        ).ToListAsync();

        return rows.Select(r => new CommissionDetailResponse(
            r.Id, r.OrderId, r.OrderNumber, r.OrderDate, r.OrderItemId,
            r.ItemName, r.ComponentName, r.Status.ToString(), r.CompletedAt,
            r.CommissionAmount, r.Notes
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
        CouponDiscountAmount: o.CouponDiscountAmount);

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
    private async Task<Accounting.Models.Entities.ChartOfAccount?> ResolvePaymentAccountAsync(Guid companyId, PaymentMethod method)
    {
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
