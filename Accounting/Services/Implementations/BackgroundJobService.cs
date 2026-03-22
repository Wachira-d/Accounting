using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// Background hosted service that runs scheduled tasks (recurring transactions, dunning, etc.)
/// </summary>
public class BackgroundJobService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackgroundJobService> _logger;

    public BackgroundJobService(IServiceScopeFactory scopeFactory, ILogger<BackgroundJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackgroundJobService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunScheduledJobs(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in background job cycle");
            }

            // Run every 15 minutes
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    private async Task RunScheduledJobs(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        // 1. Process due recurring transactions
        await ProcessRecurringTransactions(scope, ct);

        // 2. Process dunning letters for overdue invoices
        await ProcessDunningLetters(scope, ct);

        // 3. Send payment reminders
        await ProcessPaymentReminders(scope, ct);
    }

    private async Task ProcessRecurringTransactions(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var recurringService = scope.ServiceProvider.GetRequiredService<IRecurringTransactionService>();
            await recurringService.ProcessDueRecurringTransactionsAsync();
            _logger.LogInformation("Recurring transactions processed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process recurring transactions");
        }
    }

    private async Task ProcessDunningLetters(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            // Log overdue invoice count for monitoring
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var overdueCount = db.Documents
                .Count(d => d.DocumentType == DocumentType.Invoice
                    && d.Status == DocumentStatus.Approved
                    && d.DueDate.HasValue
                    && d.DueDate.Value.Date < DateTime.Today
                    && d.BalanceDue > 0);

            if (overdueCount > 0)
                _logger.LogWarning("Found {Count} overdue invoices requiring attention", overdueCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check dunning status");
        }
    }

    private async Task ProcessPaymentReminders(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            // Send reminders for invoices due within 3 days
            var emailService = scope.ServiceProvider.GetService<IEmailService>();
            if (emailService == null) return;

            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var dueSoon = db.Documents
                .Where(d => d.DocumentType == DocumentType.Invoice
                    && d.Status == DocumentStatus.Approved
                    && d.DueDate.HasValue
                    && d.DueDate.Value.Date <= DateTime.Today.AddDays(3)
                    && d.DueDate.Value.Date >= DateTime.Today
                    && d.BalanceDue > 0)
                .Join(db.Contacts, d => d.ContactId, c => c.Id, (d, c) => new { d, c })
                .Take(50)
                .ToList();

            foreach (var item in dueSoon)
            {
                if (!string.IsNullOrEmpty(item.c.Email))
                {
                    await emailService.SendPaymentReminderAsync(
                        item.c.Email, item.c.Name, item.d.DocumentNumber,
                        item.d.BalanceDue, item.d.DueDate!.Value);
                }
            }

            if (dueSoon.Any())
                _logger.LogInformation("Payment reminders sent for {Count} invoices", dueSoon.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send payment reminders");
        }
    }
}
