using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Webhook;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class WebhookService : IWebhookService
{
    private readonly AccountingDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IErrorLogService _errorLogService;
    private readonly ISecretProtector _secrets;

    public WebhookService(AccountingDbContext db, IHttpClientFactory httpClientFactory,
        IErrorLogService errorLogService, ISecretProtector secrets)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _errorLogService = errorLogService;
        _secrets = secrets;
    }

    /// <summary>header ที่ผู้เช่าตั้งเองไม่ได้ — ระบบใช้ยืนยันตัวตน/ระบุ event
    /// (ปล่อยให้ตั้งทับ = ปลอมลายเซ็นของเราส่งไปหาปลายทางได้)</summary>
    private static readonly HashSet<string> ReservedWebhookHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Webhook-Signature", "X-Webhook-Event", "X-Webhook-Delivery-Id",
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade",
    };

    // ==================== Registration ====================

    public async Task<WebhookRegistrationResponse> RegisterAsync(Guid companyId, CreateWebhookRequest request)
    {
        // ★ F-04: URL มาจากผู้เช่า — ถ้าไม่ตรวจ เซิร์ฟเวอร์กลายเป็นตัวยิงคำขอ
        // เข้าเครือข่ายภายในให้ผู้โจมตี (SSRF) และคืน status+เวลาเป็น oracle
        var fmt = Accounting.Helpers.OutboundUrlGuard.CheckFormat(request.Url);
        if (!fmt.Ok) throw new Accounting.Helpers.BusinessRuleException(fmt.Reason!, "WEBHOOK-URL");

        var registration = new WebhookRegistration
        {
            CompanyId = companyId,
            Name = request.Name,
            Url = request.Url,
            Secret = _secrets.Protect(request.Secret),
            EventTypes = request.EventTypes,
            MaxRetries = request.MaxRetries > 0 ? request.MaxRetries : 3,
            TimeoutSeconds = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 30,
            HeadersJson = request.HeadersJson,
            IsActive = true,
            FailureCount = 0
        };

        _db.Set<WebhookRegistration>().Add(registration);
        await _db.SaveChangesAsync();

        return MapToResponse(registration);
    }

    public async Task<List<WebhookRegistrationResponse>> GetRegistrationsAsync(Guid companyId)
    {
        var registrations = await _db.Set<WebhookRegistration>()
            .Where(w => w.CompanyId == companyId && !w.IsDeleted)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();

        return registrations.Select(MapToResponse).ToList();
    }

    public async Task<WebhookRegistrationResponse> UpdateAsync(Guid companyId, Guid webhookId, UpdateWebhookRequest request)
    {
        var registration = await _db.Set<WebhookRegistration>()
            .FirstOrDefaultAsync(w => w.Id == webhookId && w.CompanyId == companyId && !w.IsDeleted)
            ?? throw new InvalidOperationException($"Webhook registration {webhookId} not found.");

        if (request.Name is not null) registration.Name = request.Name;
        if (request.Url is not null)
        {
            // ตรวจตอนแก้ไขด้วย — ไม่งั้นสมัครด้วย URL ที่ผ่านแล้วค่อยแก้เป็นของภายใน
            var fmt = Accounting.Helpers.OutboundUrlGuard.CheckFormat(request.Url);
            if (!fmt.Ok) throw new Accounting.Helpers.BusinessRuleException(fmt.Reason!, "WEBHOOK-URL");
            registration.Url = request.Url;
        }
        if (request.Secret is not null) registration.Secret = _secrets.Protect(request.Secret);
        if (request.EventTypes is not null) registration.EventTypes = request.EventTypes;
        if (request.IsActive.HasValue) registration.IsActive = request.IsActive.Value;
        if (request.MaxRetries.HasValue) registration.MaxRetries = request.MaxRetries.Value;
        if (request.TimeoutSeconds.HasValue) registration.TimeoutSeconds = request.TimeoutSeconds.Value;

        registration.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapToResponse(registration);
    }

    public async Task DeleteAsync(Guid companyId, Guid webhookId)
    {
        var registration = await _db.Set<WebhookRegistration>()
            .FirstOrDefaultAsync(w => w.Id == webhookId && w.CompanyId == companyId && !w.IsDeleted)
            ?? throw new InvalidOperationException($"Webhook registration {webhookId} not found.");

        registration.IsDeleted = true;
        registration.IsActive = false;
        registration.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<WebhookRegistrationResponse> TestAsync(Guid companyId, Guid webhookId)
    {
        var registration = await _db.Set<WebhookRegistration>()
            .FirstOrDefaultAsync(w => w.Id == webhookId && w.CompanyId == companyId && !w.IsDeleted)
            ?? throw new InvalidOperationException($"Webhook registration {webhookId} not found.");

        var testPayload = new
        {
            @event = "webhook.test",
            timestamp = DateTime.UtcNow,
            data = new { message = "This is a test webhook delivery.", webhookId }
        };

        await DeliverWebhookAsync(registration, "webhook.test", testPayload);

        // Reload to get updated fields
        await _db.Entry(registration).ReloadAsync();
        return MapToResponse(registration);
    }

    // ==================== Delivery ====================

    public async Task TriggerAsync(Guid companyId, string eventType, object payload)
    {
        var registrations = await _db.Set<WebhookRegistration>()
            .Where(w => w.CompanyId == companyId && w.IsActive && !w.IsDeleted)
            .ToListAsync();

        var matchingRegistrations = registrations
            .Where(w => MatchesEventType(w.EventTypes, eventType))
            .ToList();

        foreach (var registration in matchingRegistrations)
        {
            await DeliverWebhookWithRetryAsync(registration, eventType, payload);
        }
    }

    public async Task<PagedResponse<WebhookDeliveryResponse>> GetDeliveriesAsync(Guid companyId, Guid webhookId, PagedRequest request)
    {
        // Verify the webhook belongs to this company
        var exists = await _db.Set<WebhookRegistration>()
            .AnyAsync(w => w.Id == webhookId && w.CompanyId == companyId && !w.IsDeleted);

        if (!exists)
            throw new InvalidOperationException($"Webhook registration {webhookId} not found.");

        var query = _db.Set<WebhookDelivery>()
            .Where(d => d.WebhookRegistrationId == webhookId && !d.IsDeleted)
            .OrderByDescending(d => d.DeliveredAt);

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);

        var deliveries = await query
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var items = deliveries.Select(d => new WebhookDeliveryResponse(
            d.Id,
            d.EventType,
            d.HttpStatusCode,
            d.IsSuccess,
            d.AttemptNumber,
            (int)d.DurationMs,
            d.DeliveredAt,
            d.ErrorMessage
        )).ToList();

        return new PagedResponse<WebhookDeliveryResponse>(items, totalCount, request.Page, request.PageSize, totalPages);
    }

    public async Task RetryAsync(Guid companyId, Guid deliveryId)
    {
        var delivery = await _db.Set<WebhookDelivery>()
            .Include(d => d.Registration)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.Registration.CompanyId == companyId && !d.IsDeleted)
            ?? throw new InvalidOperationException($"Webhook delivery {deliveryId} not found.");

        var payload = JsonSerializer.Deserialize<object>(delivery.PayloadJson);
        await DeliverWebhookAsync(delivery.Registration, delivery.EventType, payload ?? new { });
    }

    // ==================== Available Events ====================

    public Task<List<WebhookEventTypeResponse>> GetEventTypesAsync()
    {
        var eventTypes = new List<WebhookEventTypeResponse>
        {
            new("document.created", "Fired when a new document (invoice, receipt, etc.) is created.",
                JsonSerializer.Serialize(new { @event = "document.created", data = new { documentId = Guid.Empty, documentType = "Invoice", documentNumber = "INV-0001", totalAmount = 1000.00m } })),
            new("document.updated", "Fired when a document is updated.",
                JsonSerializer.Serialize(new { @event = "document.updated", data = new { documentId = Guid.Empty, status = "Approved" } })),
            new("document.voided", "Fired when a document is voided.",
                JsonSerializer.Serialize(new { @event = "document.voided", data = new { documentId = Guid.Empty, documentNumber = "INV-0001" } })),
            new("payment.received", "Fired when a payment is recorded against an invoice.",
                JsonSerializer.Serialize(new { @event = "payment.received", data = new { paymentId = Guid.Empty, amount = 500.00m, method = "BankTransfer" } })),
            new("payment.created", "Fired when a new outgoing payment is created.",
                JsonSerializer.Serialize(new { @event = "payment.created", data = new { paymentId = Guid.Empty, amount = 250.00m } })),
            new("expense.submitted", "Fired when an expense claim is submitted for approval.",
                JsonSerializer.Serialize(new { @event = "expense.submitted", data = new { expenseClaimId = Guid.Empty, totalAmount = 3500.00m } })),
            new("expense.approved", "Fired when an expense claim is approved.",
                JsonSerializer.Serialize(new { @event = "expense.approved", data = new { expenseClaimId = Guid.Empty } })),
            new("approval.required", "Fired when a new item requires approval.",
                JsonSerializer.Serialize(new { @event = "approval.required", data = new { entityType = "Document", entityId = Guid.Empty } })),
            new("approval.completed", "Fired when an approval workflow is completed.",
                JsonSerializer.Serialize(new { @event = "approval.completed", data = new { entityType = "Document", entityId = Guid.Empty, status = "Approved" } })),
            new("contact.created", "Fired when a new customer or supplier contact is created.",
                JsonSerializer.Serialize(new { @event = "contact.created", data = new { contactId = Guid.Empty, name = "Sample Co." } })),
            new("journal.posted", "Fired when a journal entry is posted.",
                JsonSerializer.Serialize(new { @event = "journal.posted", data = new { journalEntryId = Guid.Empty, totalDebit = 10000.00m } })),
            new("document.paid", "Fired when a document's outstanding balance reaches zero.",
                JsonSerializer.Serialize(new { @event = "document.paid", data = new { documentId = Guid.Empty, documentNumber = "INV-0001", paidAt = DateTime.UtcNow } })),
            new("document.status_changed", "Fired on any document status transition (Approve/Reject/Cancel/Pay).",
                JsonSerializer.Serialize(new { @event = "document.status_changed", data = new { documentId = Guid.Empty, from = "Draft", to = "Approved" } })),
            new("project.created", "Fired when a new project is created.",
                JsonSerializer.Serialize(new { @event = "project.created", data = new { id = Guid.Empty, code = "PRJ-001", name = "New Project", externalId = "JIRA-123", externalSystem = "Jira" } })),
            new("project.updated", "Fired when a project's fields change.",
                JsonSerializer.Serialize(new { @event = "project.updated", data = new { id = Guid.Empty, code = "PRJ-001" } })),
            new("project.status_changed", "Fired when a project transitions between Active/OnHold/Completed/Cancelled.",
                JsonSerializer.Serialize(new { @event = "project.status_changed", data = new { project = new { id = Guid.Empty, code = "PRJ-001" }, from = "Active", to = "OnHold", reason = "Client requested pause" } })),
            new("project.deleted", "Fired when a project is soft-deleted.",
                JsonSerializer.Serialize(new { @event = "project.deleted", data = new { id = Guid.Empty, code = "PRJ-001" } })),
            new("employee.created", "Fired when a new employee is added (HR master).",
                JsonSerializer.Serialize(new { @event = "employee.created", data = new { id = Guid.Empty, employeeCode = "E001", firstNameTh = "สมชาย", lastNameTh = "ใจดี", salaryType = "Monthly", baseSalary = 35000, externalId = "WD-12345", externalSystem = "Workday" } })),
            new("employee.updated", "Fired when employee fields are updated (including onboarding/offboarding).",
                JsonSerializer.Serialize(new { @event = "employee.updated", data = new { id = Guid.Empty, employeeCode = "E001", isActive = true, externalId = "WD-12345" } })),
            new("employee.terminated", "Fired when an employee is terminated (EndDate set).",
                JsonSerializer.Serialize(new { @event = "employee.terminated", data = new { id = Guid.Empty, employeeCode = "E001", endDate = DateTime.UtcNow, externalId = "WD-12345" } })),
            new("employee.deleted", "Fired when an employee is soft-deleted.",
                JsonSerializer.Serialize(new { @event = "employee.deleted", data = new { id = Guid.Empty, employeeCode = "E001", externalId = "WD-12345" } })),
            new("employee.restored", "Fired when a soft-deleted employee is restored.",
                JsonSerializer.Serialize(new { @event = "employee.restored", data = new { id = Guid.Empty, employeeCode = "E001" } })),
            new("project_time.created", "Fired when a project time entry is recorded (manual or sync).",
                JsonSerializer.Serialize(new { @event = "project_time.created", data = new { id = Guid.Empty, employeeId = Guid.Empty, projectId = Guid.Empty, workDate = DateTime.UtcNow, hours = 4.0m, category = "Billable" } })),
            new("project_time.updated", "Fired when a project time entry is edited.",
                JsonSerializer.Serialize(new { @event = "project_time.updated", data = new { id = Guid.Empty, hours = 5.0m } })),
            new("project_time.deleted", "Fired when a project time entry is deleted.",
                JsonSerializer.Serialize(new { @event = "project_time.deleted", data = new { id = Guid.Empty } })),
            new("payroll.labour_allocated", "Fired when a payroll run's labour cost is allocated to projects.",
                JsonSerializer.Serialize(new { @event = "payroll.labour_allocated", data = new { payrollRunId = Guid.Empty, year = 2026, month = 5, costEntriesCreated = 12, totalAllocated = 145000.00m, unallocatedAdmin = 35000.00m } })),
            new("cheque.cleared", "Fired when a cheque (inbound or outbound) clears at the bank — bank balance updated.",
                JsonSerializer.Serialize(new { @event = "cheque.cleared", data = new { id = Guid.Empty, chequeNumber = 1234567L, amount = 50000.00m, isInbound = false, clearedAt = DateTime.UtcNow, bankBalanceAfter = 250000.00m } })),
            new("cheque.bounced", "Fired when a cheque bounces (NSF, signature mismatch, etc.).",
                JsonSerializer.Serialize(new { @event = "cheque.bounced", data = new { id = Guid.Empty, chequeNumber = 1234567L, amount = 50000.00m, isInbound = false, reason = "Insufficient funds" } })),
            new("webhook.test", "Test event fired when you use the test endpoint.",
                JsonSerializer.Serialize(new { @event = "webhook.test", data = new { message = "Hello from Accounting!" } }))
        };

        return Task.FromResult(eventTypes);
    }

    // ==================== Private Helpers ====================

    private async Task DeliverWebhookWithRetryAsync(WebhookRegistration registration, string eventType, object payload)
    {
        var maxAttempts = Math.Max(1, registration.MaxRetries);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var delivery = await DeliverWebhookAsync(registration, eventType, payload, attempt);

            if (delivery.IsSuccess)
            {
                registration.FailureCount = 0;
                registration.LastSuccessAt = DateTime.UtcNow;
                registration.LastError = null;
                break;
            }

            if (attempt < maxAttempts)
            {
                // Exponential backoff: 1s, 2s, 4s, 8s...
                var delayMs = (int)Math.Pow(2, attempt - 1) * 1000;
                await Task.Delay(delayMs);
            }
            else
            {
                // All retries exhausted
                registration.FailureCount++;
                registration.LastError = delivery.ErrorMessage;

                // Auto-disable after 10 consecutive failures
                if (registration.FailureCount >= 10)
                {
                    registration.IsActive = false;
                }
            }
        }

        registration.LastTriggeredAt = DateTime.UtcNow;
        registration.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task<WebhookDelivery> DeliverWebhookAsync(WebhookRegistration registration, string eventType, object? payload, int attemptNumber = 1)
    {
        var delivery = new WebhookDelivery
        {
            CompanyId = registration.CompanyId,
            WebhookRegistrationId = registration.Id,
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(payload),
            AttemptNumber = attemptNumber,
            DeliveredAt = DateTime.UtcNow
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var httpClient = _httpClientFactory.CreateClient("WebhookClient");
            httpClient.Timeout = TimeSpan.FromSeconds(registration.TimeoutSeconds > 0 ? registration.TimeoutSeconds : 30);

            var jsonPayload = delivery.PayloadJson;
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            // ★ F-04: ตรวจ **ซ้ำตอนจะส่งจริง** — โดเมนที่ตอนสมัครชี้ IP สาธารณะ
            // ตอนนี้อาจชี้ 127.0.0.1 แล้ว (DNS rebinding) ⇒ ด่านตอนสมัครอย่างเดียว
            // คือด่านที่หลอกได้
            var urlCheck = await Accounting.Helpers.OutboundUrlGuard.CheckResolvedAsync(registration.Url);
            if (!urlCheck.Ok)
            {
                stopwatch.Stop();
                delivery.IsSuccess = false;
                delivery.ErrorMessage = urlCheck.Reason;
                // DurationMs เป็น decimal เหมือนทุกจุดในเมธอดนี้ — อย่าแปลงเป็น int
                delivery.DurationMs = (decimal)stopwatch.Elapsed.TotalMilliseconds;
                // บันทึกแถวเองเพราะ return ตรงนี้ ไม่ได้ไปถึงจุด Add ปลายเมธอด
                _db.Set<WebhookDelivery>().Add(delivery);
                await _db.SaveChangesAsync();
                return delivery;
            }

            var request = new HttpRequestMessage(HttpMethod.Post, registration.Url)
            {
                Content = content
            };

            // Add custom headers if configured
            if (!string.IsNullOrWhiteSpace(registration.HeadersJson))
            {
                var customHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(registration.HeadersJson);
                if (customHeaders is not null)
                {
                    foreach (var header in customHeaders)
                    {
                        // ห้ามให้ผู้เช่าตั้ง header ที่ระบบใช้เอง (ลายเซ็น/ชนิด event)
                        // หรือ header ที่ hop-by-hop — ไม่งั้นปลอมลายเซ็นของเราได้
                        if (ReservedWebhookHeaders.Contains(header.Key)) continue;
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            // Add HMAC signature if secret is configured. Secret is stored
            // encrypted at rest — sign with the cleartext.
            var clearSecret = _secrets.Unprotect(registration.Secret);
            if (!string.IsNullOrWhiteSpace(clearSecret))
            {
                var signature = ComputeHmacSha256(jsonPayload, clearSecret);
                request.Headers.TryAddWithoutValidation("X-Webhook-Signature", $"sha256={signature}");
            }

            request.Headers.TryAddWithoutValidation("X-Webhook-Event", eventType);
            request.Headers.TryAddWithoutValidation("X-Webhook-Delivery-Id", delivery.Id.ToString());

            var response = await httpClient.SendAsync(request);

            stopwatch.Stop();
            delivery.HttpStatusCode = (int)response.StatusCode;
            delivery.IsSuccess = response.IsSuccessStatusCode;
            delivery.DurationMs = (decimal)stopwatch.Elapsed.TotalMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                delivery.ResponseBody = responseBody.Length > 2000 ? responseBody[..2000] : responseBody;
                delivery.ErrorMessage = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            }
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            delivery.HttpStatusCode = 0;
            delivery.IsSuccess = false;
            delivery.DurationMs = (decimal)stopwatch.Elapsed.TotalMilliseconds;
            delivery.ErrorMessage = $"Request timed out after {registration.TimeoutSeconds} seconds.";
            await _errorLogService.LogErrorAsync(ex, $"Webhook.Timeout/{registration.Id}");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            delivery.HttpStatusCode = 0;
            delivery.IsSuccess = false;
            delivery.DurationMs = (decimal)stopwatch.Elapsed.TotalMilliseconds;
            delivery.ErrorMessage = $"Connection error: {ex.Message}";
            await _errorLogService.LogErrorAsync(ex, $"Webhook.ConnectionError/{registration.Id}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            delivery.HttpStatusCode = 0;
            delivery.IsSuccess = false;
            delivery.DurationMs = (decimal)stopwatch.Elapsed.TotalMilliseconds;
            delivery.ErrorMessage = $"Unexpected error: {ex.Message}";
            await _errorLogService.LogErrorAsync(ex, $"Webhook.UnexpectedError/{registration.Id}");
        }

        _db.Set<WebhookDelivery>().Add(delivery);
        await _db.SaveChangesAsync();

        return delivery;
    }

    private static bool MatchesEventType(string registeredEventTypes, string eventType)
    {
        if (string.IsNullOrWhiteSpace(registeredEventTypes))
            return false;

        var types = registeredEventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var type in types)
        {
            if (type == "*" || type.Equals(eventType, StringComparison.OrdinalIgnoreCase))
                return true;

            // Support wildcard prefixes like "document.*"
            if (type.EndsWith(".*"))
            {
                var prefix = type[..^2];
                if (eventType.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static string ComputeHmacSha256(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static WebhookRegistrationResponse MapToResponse(WebhookRegistration w)
    {
        return new WebhookRegistrationResponse(
            w.Id,
            w.Name,
            w.Url,
            w.EventTypes,
            w.IsActive,
            w.MaxRetries,
            w.FailureCount,
            w.LastTriggeredAt,
            w.LastSuccessAt,
            w.LastError
        );
    }
}
