using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ECommerceService : IECommerceService
{
    private readonly AccountingDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ECommerceService> _logger;

    private static readonly Dictionary<string, string> PlatformApiEndpoints = new()
    {
        { "Lazada", "https://api.lazada.co.th/rest" },
        { "Shopee", "https://partner.shopeemobile.com/api/v2" },
        { "TikTokShop", "https://open-api.tiktokglobalshop.com" },
        { "WooCommerce", "" },
        { "LINE Shopping", "https://api-lineshopping.line.me" }
    };

    public ECommerceService(AccountingDbContext db, IHttpClientFactory httpClientFactory, ILogger<ECommerceService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ECommerceConnectionResponse> ConnectAsync(Guid companyId, ConnectECommerceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Platform))
            throw new InvalidOperationException("กรุณาระบุแพลตฟอร์ม");

        if (string.IsNullOrWhiteSpace(request.ShopName))
            throw new InvalidOperationException("กรุณาระบุชื่อร้านค้า");

        var existing = await _db.Set<ExternalIntegration>()
            .AnyAsync(e => e.CompanyId == companyId && e.SystemName == request.Platform
                && e.SettingsJson != null && e.SettingsJson.Contains(request.ShopName) && e.IsActive);
        if (existing)
            throw new InvalidOperationException($"ร้าน {request.ShopName} บน {request.Platform} เชื่อมต่ออยู่แล้ว");

        var settings = JsonSerializer.Serialize(new
        {
            request.ShopName,
            request.ShopId,
            request.AccessToken,
            request.RefreshToken,
            request.AutoSync,
            request.AutoCreateInvoice,
            Platform = request.Platform,
            ConnectedAt = DateTime.UtcNow
        });

        var integration = new ExternalIntegration
        {
            CompanyId = companyId,
            SystemName = request.Platform,
            SystemType = "ECommerce",
            BaseUrl = PlatformApiEndpoints.GetValueOrDefault(request.Platform, ""),
            ApiKey = request.ApiKey ?? "",
            ApiKeyHash = "",
            ApiKeyPrefix = (request.ApiKey ?? "").Length >= 8 ? request.ApiKey![..8] : request.ApiKey ?? "",
            SecretKey = request.ApiSecret,
            IsActive = true,
            SettingsJson = settings,
            WebhookEnabled = true
        };

        _db.Set<ExternalIntegration>().Add(integration);
        await _db.SaveChangesAsync();

        return MapToResponse(integration, request.ShopName);
    }

    public async Task<List<ECommerceConnectionResponse>> GetConnectionsAsync(Guid companyId)
    {
        var integrations = await _db.Set<ExternalIntegration>()
            .Where(e => e.CompanyId == companyId && e.SystemType == "ECommerce" && e.IsActive)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        return integrations.Select(e =>
        {
            var shopName = ExtractShopName(e.SettingsJson);
            return MapToResponse(e, shopName);
        }).ToList();
    }

    public async Task<ECommerceSyncResult> SyncOrdersAsync(Guid companyId, Guid connectionId, DateTime? since = null)
    {
        var integration = await _db.Set<ExternalIntegration>()
            .FirstOrDefaultAsync(e => e.CompanyId == companyId && e.Id == connectionId && e.IsActive)
            ?? throw new KeyNotFoundException("ไม่พบการเชื่อมต่อ E-Commerce");

        var shopName = ExtractShopName(integration.SettingsJson);
        var fromDate = since ?? integration.LastSyncAt ?? DateTime.UtcNow.AddDays(-7);

        try
        {
            var orders = await FetchOrdersAsync(integration, fromDate);

            int newOrders = 0, invoicesCreated = 0;
            decimal totalAmount = 0;

            foreach (var order in orders)
            {
                var alreadySynced = await _db.Set<IntegrationSyncLog>()
                    .AnyAsync(l => l.IntegrationId == connectionId && l.ExternalId == order.OrderId);

                if (alreadySynced) continue;

                newOrders++;
                totalAmount += order.TotalAmount;

                var contact = await GetOrCreateECommerceCustomer(companyId, order, integration.SystemName);

                var syncLog = new IntegrationSyncLog
                {
                    CompanyId = companyId,
                    IntegrationId = connectionId,
                    EventType = "order.completed",
                    ExternalId = order.OrderId,
                    ExternalRef = order.OrderNumber,
                    Status = "Success",
                    RequestPayloadJson = JsonSerializer.Serialize(order),
                    CreatedContactId = contact.Id
                };

                var settings = DeserializeSettings(integration.SettingsJson);
                if (settings?.AutoCreateInvoice == true)
                {
                    var doc = await CreateInvoiceFromOrder(companyId, order, contact);
                    syncLog.CreatedDocumentId = doc.Id;
                    invoicesCreated++;
                }

                _db.Set<IntegrationSyncLog>().Add(syncLog);
            }

            integration.LastSyncAt = DateTime.UtcNow;
            integration.TotalSyncCount += newOrders;
            integration.ConsecutiveErrors = 0;

            await _db.SaveChangesAsync();

            return new ECommerceSyncResult(connectionId, integration.SystemName, shopName,
                orders.Count, newOrders, invoicesCreated, totalAmount, "Success", null);
        }
        catch (Exception ex)
        {
            integration.ErrorCount++;
            integration.ConsecutiveErrors++;
            await _db.SaveChangesAsync();

            _logger.LogError(ex, "E-Commerce sync failed for {Platform} shop {ShopName}", integration.SystemName, shopName);
            return new ECommerceSyncResult(connectionId, integration.SystemName, shopName,
                0, 0, 0, 0, "Failed", ex.Message);
        }
    }

    public async Task<ECommerceSyncResult> SyncAllAsync(Guid companyId)
    {
        var connections = await _db.Set<ExternalIntegration>()
            .Where(e => e.CompanyId == companyId && e.SystemType == "ECommerce" && e.IsActive)
            .ToListAsync();

        int totalNew = 0, totalInvoices = 0;
        decimal totalAmount = 0;

        foreach (var conn in connections)
        {
            var result = await SyncOrdersAsync(companyId, conn.Id);
            totalNew += result.NewOrders;
            totalInvoices += result.InvoicesCreated;
            totalAmount += result.TotalAmount;
        }

        return new ECommerceSyncResult(Guid.Empty, "All Platforms", "",
            totalNew, totalNew, totalInvoices, totalAmount, "Success", null);
    }

    public async Task DisconnectAsync(Guid companyId, Guid connectionId)
    {
        var integration = await _db.Set<ExternalIntegration>()
            .FirstOrDefaultAsync(e => e.CompanyId == companyId && e.Id == connectionId)
            ?? throw new KeyNotFoundException("ไม่พบการเชื่อมต่อ");

        integration.IsActive = false;
        await _db.SaveChangesAsync();
    }

    public async Task<ECommerceConnectionResponse> TestConnectionAsync(Guid companyId, Guid connectionId)
    {
        var integration = await _db.Set<ExternalIntegration>()
            .FirstOrDefaultAsync(e => e.CompanyId == companyId && e.Id == connectionId)
            ?? throw new KeyNotFoundException("ไม่พบการเชื่อมต่อ");

        try
        {
            var client = _httpClientFactory.CreateClient();
            var settings = DeserializeSettings(integration.SettingsJson);
            if (!string.IsNullOrEmpty(settings?.AccessToken))
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.AccessToken);

            if (!string.IsNullOrEmpty(integration.BaseUrl))
            {
                var response = await client.GetAsync($"{integration.BaseUrl}/shop/info");
                integration.ConsecutiveErrors = response.IsSuccessStatusCode ? 0 : integration.ConsecutiveErrors + 1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "E-Commerce connection test failed for {Platform}", integration.SystemName);
        }

        await _db.SaveChangesAsync();
        return MapToResponse(integration, ExtractShopName(integration.SettingsJson));
    }

    private async Task<List<ECommerceOrder>> FetchOrdersAsync(ExternalIntegration integration, DateTime fromDate)
    {
        var client = _httpClientFactory.CreateClient();
        var settings = DeserializeSettings(integration.SettingsJson);

        if (!string.IsNullOrEmpty(settings?.AccessToken))
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.AccessToken);

        if (!string.IsNullOrEmpty(integration.ApiKey))
            client.DefaultRequestHeaders.Add("X-Api-Key", integration.ApiKey);

        var url = integration.SystemName switch
        {
            "Lazada" => $"{integration.BaseUrl}/orders/get?created_after={fromDate:yyyy-MM-dd'T'HH:mm:sszzz}&status=delivered",
            "Shopee" => $"{integration.BaseUrl}/order/get_order_list?time_from={new DateTimeOffset(fromDate).ToUnixTimeSeconds()}&order_status=COMPLETED",
            "TikTokShop" => $"{integration.BaseUrl}/order/202309/orders/search?create_time_ge={new DateTimeOffset(fromDate).ToUnixTimeSeconds()}",
            _ => $"{integration.BaseUrl}/orders?after={fromDate:yyyy-MM-dd}"
        };

        try
        {
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return new List<ECommerceOrder>();

            var json = await response.Content.ReadAsStringAsync();
            return ParseOrders(json, integration.SystemName);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"ไม่สามารถดึงข้อมูลคำสั่งซื้อจาก {integration.SystemName}: {ex.Message}");
        }
    }

    private static List<ECommerceOrder> ParseOrders(string json, string platform)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var dataElement = platform switch
            {
                "Lazada" => root.TryGetProperty("data", out var d) ? d.GetProperty("orders") : default,
                "Shopee" => root.TryGetProperty("response", out var r) ? r.GetProperty("order_list") : default,
                _ => root.TryGetProperty("data", out var dt) ? dt : root
            };

            if (dataElement.ValueKind != JsonValueKind.Array)
                return new List<ECommerceOrder>();

            return dataElement.EnumerateArray().Select(o => new ECommerceOrder
            {
                OrderId = o.TryGetProperty("order_id", out var id) ? id.ToString() : Guid.NewGuid().ToString(),
                OrderNumber = o.TryGetProperty("order_number", out var num) ? num.GetString() ?? "" : "",
                CustomerName = o.TryGetProperty("customer_name", out var cn) ? cn.GetString() ?? "" : "ลูกค้า E-Commerce",
                CustomerPhone = o.TryGetProperty("customer_phone", out var cp) ? cp.GetString() : null,
                TotalAmount = o.TryGetProperty("total_amount", out var ta) ? ta.GetDecimal() : 0,
                OrderDate = o.TryGetProperty("created_at", out var ca) ? DateTime.TryParse(ca.GetString(), out var dt) ? dt : DateTime.UtcNow : DateTime.UtcNow,
                Items = new List<ECommerceOrderItem>()
            }).ToList();
        }
        catch
        {
            return new List<ECommerceOrder>();
        }
    }

    private async Task<Contact> GetOrCreateECommerceCustomer(Guid companyId, ECommerceOrder order, string platform)
    {
        // Match by phone first (more unique than name), then by name+phone combo
        Contact? existing = null;
        if (!string.IsNullOrEmpty(order.CustomerPhone))
            existing = await _db.Contacts
                .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Phone == order.CustomerPhone && c.IsCustomer);

        existing ??= await _db.Contacts
            .FirstOrDefaultAsync(c => c.CompanyId == companyId
                && c.Name == order.CustomerName && c.IsCustomer
                && (c.ContactPerson != null && c.ContactPerson.Contains(platform)));

        if (existing != null) return existing;

        var contact = new Contact
        {
            CompanyId = companyId,
            Name = order.CustomerName,
            Phone = order.CustomerPhone,
            IsCustomer = true,
            IsSupplier = false,
            ContactPerson = $"ลูกค้าจาก {platform}",
            CreatedBy = "system-ecommerce"
        };
        _db.Contacts.Add(contact);
        return contact;
    }

    private async Task<Document> CreateInvoiceFromOrder(Guid companyId, ECommerceOrder order, Contact contact)
    {
        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);

        decimal subTotal, vatAmount;
        if (company is { IsVatRegistered: true })
        {
            var vatRate = company.VatRate;
            subTotal = order.TotalAmount / (1 + vatRate / 100);
            vatAmount = order.TotalAmount - subTotal;
        }
        else
        {
            subTotal = order.TotalAmount;
            vatAmount = 0;
        }

        var doc = new Document
        {
            CompanyId = companyId,
            DocumentType = DocumentType.TaxInvoice,
            DocumentNumber = $"EC-{order.OrderNumber}",
            Status = DocumentStatus.Approved,
            DocumentDate = order.OrderDate,
            DueDate = order.OrderDate,
            ContactId = contact.Id,
            SubTotal = Math.Round(subTotal, 2),
            VatAmount = Math.Round(vatAmount, 2),
            TotalAmount = order.TotalAmount,
            BalanceDue = 0,
            Reference = $"{order.OrderId}",
            Notes = $"Auto-created from E-Commerce order",
            CreatedBy = "system-ecommerce"
        };

        _db.Documents.Add(doc);

        var lineOrder = 1;
        var lineVatRate = company is { IsVatRegistered: true } ? company.VatRate : 0m;
        foreach (var item in order.Items)
        {
            decimal lineVatAmount = 0;
            decimal lineSubTotal = item.Total;
            if (lineVatRate > 0)
            {
                lineSubTotal = item.Total / (1 + lineVatRate / 100);
                lineVatAmount = item.Total - lineSubTotal;
            }

            _db.DocumentLines.Add(new DocumentLine
            {
                DocumentId = doc.Id,
                LineOrder = lineOrder++,
                Description = item.Name,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Amount = Math.Round(lineSubTotal, 2),
                VatRate = lineVatRate,
                VatAmount = Math.Round(lineVatAmount, 2),
                Unit = "ชิ้น"
            });
        }

        return doc;
    }

    private static string ExtractShopName(string? settingsJson)
    {
        if (string.IsNullOrEmpty(settingsJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            return doc.RootElement.TryGetProperty("ShopName", out var sn) ? sn.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static ECommerceSettings? DeserializeSettings(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<ECommerceSettings>(json); }
        catch { return null; }
    }

    private static ECommerceConnectionResponse MapToResponse(ExternalIntegration e, string shopName) => new(
        e.Id, e.SystemName, shopName,
        e.IsActive ? "Active" : "Disconnected",
        e.LastSyncAt, e.TotalSyncCount, 0,
        true, true);
}

internal class ECommerceOrder
{
    public string OrderId { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string? CustomerPhone { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime OrderDate { get; set; }
    public List<ECommerceOrderItem> Items { get; set; } = new();
}

internal class ECommerceOrderItem
{
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total { get; set; }
}

internal class ECommerceSettings
{
    public string? ShopName { get; set; }
    public string? ShopId { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public bool AutoSync { get; set; }
    public bool AutoCreateInvoice { get; set; }
}
