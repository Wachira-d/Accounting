using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
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
                await LogErrorFromScopeAsync(ex, "BackgroundJobService.ExecuteAsync");
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

        // 4. Escalate overdue approval requests
        await ProcessApprovalEscalations(scope, ct);

        // 5. Send tax filing deadline reminders
        await ProcessTaxCalendarReminders(scope, ct);

        // 6. Auto-sync open banking transactions
        await ProcessOpenBankingAutoSync(scope, ct);
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
            var errorLogService = scope.ServiceProvider.GetService<IErrorLogService>();
            if (errorLogService != null)
                await errorLogService.LogErrorAsync(ex, "BackgroundJob.RecurringTransactions");
        }
    }

    private async Task ProcessDunningLetters(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var lineNotify = scope.ServiceProvider.GetRequiredService<ILineNotifyService>();
            var today = DateTime.UtcNow.Date;

            var overdueDocuments = await db.Documents
                .Include(d => d.Contact)
                .Where(d => (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice)
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                    && d.Status != DocumentStatus.Paid
                    && d.DueDate.HasValue
                    && d.DueDate < today
                    && d.BalanceDue > 0)
                .ToListAsync(ct);

            if (overdueDocuments.Count > 0)
                _logger.LogWarning("Found {Count} overdue invoices requiring attention", overdueDocuments.Count);

            foreach (var doc in overdueDocuments)
            {
                if (doc.Status != DocumentStatus.Overdue)
                    doc.Status = DocumentStatus.Overdue;

                var daysOverdue = (today - doc.DueDate!.Value).Days;
                try
                {
                    await lineNotify.NotifyOverdueInvoiceAsync(
                        doc.CompanyId, doc.DocumentNumber,
                        doc.Contact?.Name ?? "ไม่ระบุ", doc.BalanceDue, daysOverdue);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "LINE notify skipped for overdue doc {DocId}", doc.Id);
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check dunning status");
            var errorLogService = scope.ServiceProvider.GetService<IErrorLogService>();
            if (errorLogService != null)
                await errorLogService.LogErrorAsync(ex, "BackgroundJob.DunningLetters");
        }
    }

    private async Task LogErrorFromScopeAsync(Exception ex, string source)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var errorLogService = scope.ServiceProvider.GetService<IErrorLogService>();
            if (errorLogService != null)
                await errorLogService.LogErrorAsync(ex, source);
        }
        catch
        {
            // Prevent error logging from crashing the background service
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
            var today = DateTime.UtcNow.Date;
            var reminderCutoff = today.AddDays(3);
            var dueSoon = db.Documents
                .Where(d => d.DocumentType == DocumentType.Invoice
                    && d.Status == DocumentStatus.Approved
                    && d.DueDate.HasValue
                    && d.DueDate <= reminderCutoff
                    && d.DueDate >= today
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
            var errorLogService = scope.ServiceProvider.GetService<IErrorLogService>();
            if (errorLogService != null)
                await errorLogService.LogErrorAsync(ex, "BackgroundJob.PaymentReminders");
        }
    }

    private async Task ProcessApprovalEscalations(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var approvalService = scope.ServiceProvider.GetRequiredService<IApprovalService>();
            await approvalService.EscalateOverdueApprovalsAsync();
            _logger.LogDebug("Approval escalation cycle completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to escalate overdue approvals");
            var errorLogService = scope.ServiceProvider.GetService<IErrorLogService>();
            if (errorLogService != null)
                await errorLogService.LogErrorAsync(ex, "BackgroundJob.ApprovalEscalations");
        }
    }

    private async Task ProcessTaxCalendarReminders(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var taxCalendarService = scope.ServiceProvider.GetRequiredService<ITaxCalendarService>();
            await taxCalendarService.ProcessRemindersAsync();
            _logger.LogDebug("Tax calendar reminder cycle completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process tax calendar reminders");
            var errorLogService = scope.ServiceProvider.GetService<IErrorLogService>();
            if (errorLogService != null)
                await errorLogService.LogErrorAsync(ex, "BackgroundJob.TaxCalendarReminders");
        }
    }

    private async Task ProcessOpenBankingAutoSync(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var openBankingService = scope.ServiceProvider.GetRequiredService<IOpenBankingService>();
            await openBankingService.ProcessAutoSyncAsync();
            _logger.LogDebug("Open banking auto-sync cycle completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-sync open banking transactions");
            var errorLogService = scope.ServiceProvider.GetService<IErrorLogService>();
            if (errorLogService != null)
                await errorLogService.LogErrorAsync(ex, "BackgroundJob.OpenBankingAutoSync");
        }
    }
}
