using Accounting.Data;
using Accounting.Models.DTOs.Ai;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class AiService : IAiService
{
    private readonly AccountingDbContext _db;

    public AiService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Auto-Categorization =====

    public async Task<CategorizationResultResponse> AutoCategorizeAsync(Guid companyId, string entityType, Guid entityId)
    {
        // Get entity details for matching
        var description = "";
        var reference = "";
        var payee = "";
        decimal amount = 0;

        if (entityType == "BankTransaction")
        {
            var txn = await _db.BankTransactions
                .FirstOrDefaultAsync(t => t.Id == entityId && t.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบรายการธนาคาร");
            description = txn.Description ?? "";
            reference = txn.Reference ?? "";
            payee = txn.Payee ?? "";
            amount = Math.Abs(txn.Amount);
        }
        else if (entityType == "Document")
        {
            var doc = await _db.Documents
                .FirstOrDefaultAsync(d => d.Id == entityId && d.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
            description = doc.Reference ?? "";
            reference = doc.DocumentNumber;
            amount = doc.TotalAmount;
        }
        else if (entityType == "JournalEntry")
        {
            var je = await _db.JournalEntries
                .FirstOrDefaultAsync(j => j.Id == entityId && j.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบใบสำคัญ");
            description = je.Description ?? "";
            reference = je.Reference ?? "";
            amount = je.TotalDebit;
        }

        // Find matching rules ordered by priority
        var rules = await _db.AutoCategorizationRules
            .Where(r => r.CompanyId == companyId && r.IsActive && !r.IsDeleted)
            .OrderByDescending(r => r.Priority)
            .ToListAsync();

        AutoCategorizationRule? bestMatch = null;
        decimal bestConfidence = 0;

        foreach (var rule in rules)
        {
            var fieldValue = rule.MatchField switch
            {
                "Description" => description,
                "Reference" => reference,
                "Payee" => payee,
                "Amount" => amount.ToString(),
                _ => description
            };

            bool matches = false;
            decimal confidence = 0;

            // Check amount range if specified
            if (rule.MinAmount.HasValue && amount < rule.MinAmount.Value) continue;
            if (rule.MaxAmount.HasValue && amount > rule.MaxAmount.Value) continue;

            switch (rule.MatchType)
            {
                case "Contains":
                    if (!string.IsNullOrEmpty(rule.MatchPattern) &&
                        fieldValue.Contains(rule.MatchPattern, StringComparison.OrdinalIgnoreCase))
                    {
                        matches = true;
                        confidence = 0.85m;
                    }
                    break;
                case "StartsWith":
                    if (!string.IsNullOrEmpty(rule.MatchPattern) &&
                        fieldValue.StartsWith(rule.MatchPattern, StringComparison.OrdinalIgnoreCase))
                    {
                        matches = true;
                        confidence = 0.90m;
                    }
                    break;
                case "Regex":
                    if (!string.IsNullOrEmpty(rule.MatchPattern))
                    {
                        try
                        {
                            if (System.Text.RegularExpressions.Regex.IsMatch(fieldValue, rule.MatchPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            {
                                matches = true;
                                confidence = 0.80m;
                            }
                        }
                        catch { /* invalid regex, skip */ }
                    }
                    break;
                case "AI":
                    // AI-based matching uses amount range as primary signal
                    if (rule.MinAmount.HasValue || rule.MaxAmount.HasValue)
                    {
                        matches = true;
                        confidence = 0.70m;
                    }
                    break;
            }

            if (matches && confidence > bestConfidence)
            {
                bestMatch = rule;
                bestConfidence = confidence;
            }
        }

        // Create categorization result
        string? suggestedAccountName = null;
        if (bestMatch?.TargetAccountId != null)
        {
            var account = await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.Id == bestMatch.TargetAccountId && a.CompanyId == companyId);
            suggestedAccountName = account?.AccountName;
        }

        var result = new CategorizationResult
        {
            CompanyId = companyId,
            EntityType = entityType,
            EntityId = entityId,
            SuggestedAccountId = bestMatch?.TargetAccountId,
            SuggestedDimensionId = bestMatch?.TargetDimensionId,
            SuggestedCategory = bestMatch?.TargetCategory,
            Confidence = bestConfidence,
            AppliedRuleId = bestMatch?.Id,
            ReasoningJson = bestMatch != null
                ? $"{{\"rule\":\"{bestMatch.RuleName}\",\"matchType\":\"{bestMatch.MatchType}\",\"field\":\"{bestMatch.MatchField}\"}}"
                : null
        };

        _db.CategorizationResults.Add(result);

        // Increment applied count on the rule
        if (bestMatch != null)
        {
            bestMatch.TimesApplied++;
        }

        await _db.SaveChangesAsync();

        return MapToCategorizationResponse(result, suggestedAccountName);
    }

    public async Task<List<CategorizationResultResponse>> BatchCategorizeAsync(Guid companyId, string entityType, List<Guid> entityIds)
    {
        var results = new List<CategorizationResultResponse>();
        foreach (var entityId in entityIds)
        {
            var result = await AutoCategorizeAsync(companyId, entityType, entityId);
            results.Add(result);
        }
        return results;
    }

    public async Task AcceptCategorizationAsync(Guid companyId, Guid resultId, Guid userId)
    {
        var result = await _db.CategorizationResults
            .FirstOrDefaultAsync(r => r.Id == resultId && r.CompanyId == companyId && !r.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบผลการจัดหมวดหมู่");

        result.IsAccepted = true;
        result.IsRejected = false;
        result.AcceptedByUserId = userId;
        result.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task RejectCategorizationAsync(Guid companyId, Guid resultId, Guid userId)
    {
        var result = await _db.CategorizationResults
            .FirstOrDefaultAsync(r => r.Id == resultId && r.CompanyId == companyId && !r.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบผลการจัดหมวดหมู่");

        result.IsRejected = true;
        result.IsAccepted = false;
        result.AcceptedByUserId = userId;
        result.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    // ===== Rules Management =====

    public async Task<AutoCatRuleResponse> CreateRuleAsync(Guid companyId, CreateAutoCatRuleRequest request)
    {
        var rule = new AutoCategorizationRule
        {
            CompanyId = companyId,
            RuleName = request.RuleName,
            MatchType = request.MatchType,
            MatchField = request.MatchField,
            MatchPattern = request.MatchPattern,
            MinAmount = request.MinAmount,
            MaxAmount = request.MaxAmount,
            TargetAccountId = request.TargetAccountId,
            TargetDimensionId = request.TargetDimensionId,
            TargetCategory = request.TargetCategory,
            Priority = request.Priority
        };

        _db.AutoCategorizationRules.Add(rule);
        await _db.SaveChangesAsync();

        return MapToRuleResponse(rule);
    }

    public async Task<List<AutoCatRuleResponse>> GetRulesAsync(Guid companyId)
    {
        var rules = await _db.AutoCategorizationRules
            .Where(r => r.CompanyId == companyId && !r.IsDeleted)
            .OrderByDescending(r => r.Priority)
            .ToListAsync();

        return rules.Select(MapToRuleResponse).ToList();
    }

    public async Task<AutoCatRuleResponse> UpdateRuleAsync(Guid companyId, Guid ruleId, UpdateAutoCatRuleRequest request)
    {
        var rule = await _db.AutoCategorizationRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == companyId && !r.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบกฎการจัดหมวดหมู่");

        if (request.RuleName != null) rule.RuleName = request.RuleName;
        if (request.MatchPattern != null) rule.MatchPattern = request.MatchPattern;
        if (request.MinAmount.HasValue) rule.MinAmount = request.MinAmount;
        if (request.MaxAmount.HasValue) rule.MaxAmount = request.MaxAmount;
        if (request.TargetAccountId.HasValue) rule.TargetAccountId = request.TargetAccountId;
        if (request.TargetCategory != null) rule.TargetCategory = request.TargetCategory;
        if (request.Priority.HasValue) rule.Priority = request.Priority.Value;
        if (request.IsActive.HasValue) rule.IsActive = request.IsActive.Value;
        rule.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return MapToRuleResponse(rule);
    }

    public async Task DeleteRuleAsync(Guid companyId, Guid ruleId)
    {
        var rule = await _db.AutoCategorizationRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == companyId && !r.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบกฎการจัดหมวดหมู่");

        rule.IsDeleted = true;
        rule.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task<List<AutoCatRuleResponse>> LearnRulesFromHistoryAsync(Guid companyId)
    {
        var newRules = new List<AutoCategorizationRule>();

        // Analyze accepted categorization results to learn patterns
        var acceptedResults = await _db.CategorizationResults
            .Where(r => r.CompanyId == companyId && r.IsAccepted && !r.IsDeleted)
            .ToListAsync();

        // Group by suggested category to find recurring patterns
        var categoryGroups = acceptedResults
            .Where(r => r.SuggestedCategory != null)
            .GroupBy(r => new { r.SuggestedCategory, r.SuggestedAccountId })
            .Where(g => g.Count() >= 3) // minimum 3 occurrences to learn a pattern
            .ToList();

        foreach (var group in categoryGroups)
        {
            // Check if a similar rule already exists
            var existingRule = await _db.AutoCategorizationRules
                .AnyAsync(r => r.CompanyId == companyId
                    && r.TargetCategory == group.Key.SuggestedCategory
                    && r.TargetAccountId == group.Key.SuggestedAccountId
                    && !r.IsDeleted);

            if (existingRule) continue;

            var rule = new AutoCategorizationRule
            {
                CompanyId = companyId,
                RuleName = $"Auto-learned: {group.Key.SuggestedCategory}",
                MatchType = "AI",
                MatchField = "Description",
                TargetAccountId = group.Key.SuggestedAccountId,
                TargetCategory = group.Key.SuggestedCategory,
                Priority = 0,
                IsAiGenerated = true,
                IsActive = true,
                ConfidenceThreshold = 0.7m
            };

            _db.AutoCategorizationRules.Add(rule);
            newRules.Add(rule);
        }

        // Analyze bank transactions to find amount-based patterns
        var transactions = await _db.BankTransactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted)
            .ToListAsync();

        var payeeGroups = transactions
            .Where(t => !string.IsNullOrEmpty(t.Payee))
            .GroupBy(t => t.Payee)
            .Where(g => g.Count() >= 5) // minimum 5 transactions from same payee
            .ToList();

        foreach (var group in payeeGroups)
        {
            var existingRule = await _db.AutoCategorizationRules
                .AnyAsync(r => r.CompanyId == companyId
                    && r.MatchField == "Payee"
                    && r.MatchPattern == group.Key
                    && !r.IsDeleted);

            if (existingRule) continue;

            var avgAmount = group.Average(t => Math.Abs(t.Amount));
            var rule = new AutoCategorizationRule
            {
                CompanyId = companyId,
                RuleName = $"Auto-learned: Payee {group.Key}",
                MatchType = "Contains",
                MatchField = "Payee",
                MatchPattern = group.Key,
                MinAmount = avgAmount * 0.5m,
                MaxAmount = avgAmount * 2.0m,
                Priority = 1,
                IsAiGenerated = true,
                IsActive = true,
                ConfidenceThreshold = 0.7m
            };

            _db.AutoCategorizationRules.Add(rule);
            newRules.Add(rule);
        }

        await _db.SaveChangesAsync();

        return newRules.Select(MapToRuleResponse).ToList();
    }

    // ===== Anomaly Detection =====

    public async Task<List<AnomalyResponse>> DetectAnomaliesAsync(Guid companyId, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var from = fromDate ?? DateTime.UtcNow.AddMonths(-3);
        var to = toDate ?? DateTime.UtcNow;

        var detectedAnomalies = new List<AnomalyDetection>();

        // 1. Detect unusual journal entry amounts (amount deviation)
        var journalEntries = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId && !j.IsDeleted
                && j.EntryDate >= from && j.EntryDate <= to)
            .ToListAsync();

        if (journalEntries.Count > 0)
        {
            var avgAmount = journalEntries.Average(j => j.TotalDebit);
            var stdDev = CalculateStdDev(journalEntries.Select(j => j.TotalDebit));
            var threshold = avgAmount + (stdDev * 3); // 3 sigma rule

            foreach (var je in journalEntries)
            {
                if (je.TotalDebit > threshold && threshold > 0)
                {
                    var deviation = stdDev > 0
                        ? ((je.TotalDebit - avgAmount) / avgAmount) * 100
                        : 0;

                    detectedAnomalies.Add(new AnomalyDetection
                    {
                        CompanyId = companyId,
                        AnomalyType = "UnusualAmount",
                        Severity = deviation > 500 ? "Critical" : deviation > 200 ? "High" : "Medium",
                        EntityType = "JournalEntry",
                        EntityId = je.Id,
                        Description = $"Journal entry {je.EntryNumber} has an unusually high amount of {je.TotalDebit:N2} (average: {avgAmount:N2})",
                        ExpectedValue = avgAmount,
                        ActualValue = je.TotalDebit,
                        DeviationPercent = deviation,
                        Status = "Open",
                        DetectedAt = DateTime.UtcNow
                    });
                }
            }

            // 2. Detect duplicate entries (same date, same amount, same description)
            var duplicateGroups = journalEntries
                .GroupBy(j => new { j.EntryDate.Date, j.TotalDebit, j.Description })
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in duplicateGroups)
            {
                foreach (var je in group.Skip(1)) // skip the first, flag the rest as duplicates
                {
                    detectedAnomalies.Add(new AnomalyDetection
                    {
                        CompanyId = companyId,
                        AnomalyType = "DuplicateEntry",
                        Severity = "High",
                        EntityType = "JournalEntry",
                        EntityId = je.Id,
                        Description = $"Possible duplicate: Journal entry {je.EntryNumber} matches another entry on {je.EntryDate:yyyy-MM-dd} with amount {je.TotalDebit:N2}",
                        ExpectedValue = null,
                        ActualValue = je.TotalDebit,
                        DeviationPercent = null,
                        Status = "Open",
                        DetectedAt = DateTime.UtcNow
                    });
                }
            }
        }

        // 3. Detect unbalanced journal entries
        foreach (var je in journalEntries)
        {
            if (je.TotalDebit != je.TotalCredit)
            {
                detectedAnomalies.Add(new AnomalyDetection
                {
                    CompanyId = companyId,
                    AnomalyType = "BalanceDiscrepancy",
                    Severity = "Critical",
                    EntityType = "JournalEntry",
                    EntityId = je.Id,
                    Description = $"Journal entry {je.EntryNumber} is unbalanced: Debit {je.TotalDebit:N2} != Credit {je.TotalCredit:N2}",
                    ExpectedValue = je.TotalDebit,
                    ActualValue = je.TotalCredit,
                    DeviationPercent = je.TotalDebit != 0
                        ? Math.Abs((je.TotalCredit - je.TotalDebit) / je.TotalDebit) * 100
                        : 100,
                    Status = "Open",
                    DetectedAt = DateTime.UtcNow
                });
            }
        }

        // 4. Detect unusual bank transactions
        var bankTxns = await _db.BankTransactions
            .Where(t => t.CompanyId == companyId && !t.IsDeleted
                && t.TransactionDate >= from && t.TransactionDate <= to)
            .ToListAsync();

        if (bankTxns.Count > 0)
        {
            var avgBankAmount = bankTxns.Average(t => Math.Abs(t.Amount));
            var bankStdDev = CalculateStdDev(bankTxns.Select(t => Math.Abs(t.Amount)));
            var bankThreshold = avgBankAmount + (bankStdDev * 3);

            foreach (var txn in bankTxns)
            {
                if (Math.Abs(txn.Amount) > bankThreshold && bankThreshold > 0)
                {
                    var deviation = avgBankAmount > 0
                        ? ((Math.Abs(txn.Amount) - avgBankAmount) / avgBankAmount) * 100
                        : 0;

                    detectedAnomalies.Add(new AnomalyDetection
                    {
                        CompanyId = companyId,
                        AnomalyType = "UnusualAmount",
                        Severity = deviation > 500 ? "Critical" : deviation > 200 ? "High" : "Medium",
                        EntityType = "BankTransaction",
                        EntityId = txn.Id,
                        Description = $"Bank transaction has an unusually high amount of {Math.Abs(txn.Amount):N2} (average: {avgBankAmount:N2})",
                        ExpectedValue = avgBankAmount,
                        ActualValue = Math.Abs(txn.Amount),
                        DeviationPercent = deviation,
                        Status = "Open",
                        DetectedAt = DateTime.UtcNow
                    });
                }
            }
        }

        // Avoid duplicate anomaly records for the same entity
        var existingAnomalyEntityIds = await _db.AnomalyDetections
            .Where(a => a.CompanyId == companyId && a.Status == "Open" && !a.IsDeleted)
            .Select(a => new { a.EntityId, a.AnomalyType })
            .ToListAsync();

        var existingSet = existingAnomalyEntityIds
            .Select(a => $"{a.EntityId}_{a.AnomalyType}")
            .ToHashSet();

        var newAnomalies = detectedAnomalies
            .Where(a => !existingSet.Contains($"{a.EntityId}_{a.AnomalyType}"))
            .ToList();

        if (newAnomalies.Count > 0)
        {
            _db.AnomalyDetections.AddRange(newAnomalies);
            await _db.SaveChangesAsync();
        }

        return newAnomalies.Select(MapToAnomalyResponse).ToList();
    }

    public async Task<List<AnomalyResponse>> GetAnomaliesAsync(Guid companyId, string? status = null)
    {
        var query = _db.AnomalyDetections
            .Where(a => a.CompanyId == companyId && !a.IsDeleted);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(a => a.Status == status);

        var anomalies = await query
            .OrderByDescending(a => a.DetectedAt)
            .ToListAsync();

        return anomalies.Select(MapToAnomalyResponse).ToList();
    }

    public async Task AcknowledgeAnomalyAsync(Guid companyId, Guid anomalyId)
    {
        var anomaly = await _db.AnomalyDetections
            .FirstOrDefaultAsync(a => a.Id == anomalyId && a.CompanyId == companyId && !a.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการ Anomaly");

        anomaly.Status = "Acknowledged";
        anomaly.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task ResolveAnomalyAsync(Guid companyId, Guid anomalyId, string resolutionNotes, string resolvedBy)
    {
        var anomaly = await _db.AnomalyDetections
            .FirstOrDefaultAsync(a => a.Id == anomalyId && a.CompanyId == companyId && !a.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการ Anomaly");

        anomaly.Status = "Resolved";
        anomaly.ResolutionNotes = resolutionNotes;
        anomaly.ResolvedBy = resolvedBy;
        anomaly.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task MarkFalsePositiveAsync(Guid companyId, Guid anomalyId)
    {
        var anomaly = await _db.AnomalyDetections
            .FirstOrDefaultAsync(a => a.Id == anomalyId && a.CompanyId == companyId && !a.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการ Anomaly");

        anomaly.Status = "FalsePositive";
        anomaly.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    // ===== Cash Flow Forecasting =====

    public async Task<CashFlowForecastResponse> GenerateForecastAsync(Guid companyId, CreateForecastRequest request)
    {
        var periodMonths = (int)((request.PeriodEnd - request.PeriodStart).TotalDays / 30) + 1;

        // Get historical data for projection
        var historicalPeriod = request.PeriodEnd - request.PeriodStart;
        var historicalStart = request.PeriodStart.AddDays(-historicalPeriod.TotalDays);
        var historicalEnd = request.PeriodStart;

        // Get recent bank balances for opening balance
        var latestBankBalances = await _db.Set<BankAccount>()
            .Where(b => b.CompanyId == companyId && b.IsActive && !b.IsDeleted)
            .SumAsync(b => b.CurrentBalance);

        var openingBalance = latestBankBalances;

        // Get historical documents for inflow/outflow analysis
        var historicalDocs = await _db.Documents
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.DocumentDate >= historicalStart && d.DocumentDate < historicalEnd)
            .ToListAsync();

        // Calculate historical monthly averages
        var historicalMonths = Math.Max(1, (int)(historicalPeriod.TotalDays / 30));

        var historicalInflows = historicalDocs
            .Where(d => d.DocumentType == DocumentType.Invoice
                     || d.DocumentType == DocumentType.Receipt
                     || d.DocumentType == DocumentType.TaxInvoice)
            .Sum(d => d.TotalAmount);

        var historicalOutflows = historicalDocs
            .Where(d => d.DocumentType == DocumentType.Expense
                     || d.DocumentType == DocumentType.PurchaseInvoice
                     || d.DocumentType == DocumentType.PurchaseOrder)
            .Sum(d => d.TotalAmount);

        var avgMonthlyInflow = historicalMonths > 0 ? historicalInflows / historicalMonths : 0;
        var avgMonthlyOutflow = historicalMonths > 0 ? historicalOutflows / historicalMonths : 0;

        // Get recurring transaction count for more accurate forecasting
        var recurringCount = await _db.Set<RecurringTransaction>()
            .Where(r => r.CompanyId == companyId && r.Status == RecurringStatus.Active && !r.IsDeleted
                && r.Frequency == RecurringFrequency.Monthly)
            .CountAsync();

        // Estimate recurring outflow from historical average per recurring transaction
        var recurringMonthlyOutflow = recurringCount > 0 && historicalMonths > 0
            ? (historicalOutflows / historicalMonths / Math.Max(recurringCount, 1)) * recurringCount * 0.1m
            : 0;

        var projectedInflows = avgMonthlyInflow * periodMonths;
        var projectedOutflows = (avgMonthlyOutflow + recurringMonthlyOutflow) * periodMonths;
        var projectedClosingBalance = openingBalance + projectedInflows - projectedOutflows;

        // Create forecast lines per month
        var lines = new List<CashFlowForecastLine>();
        var currentDate = request.PeriodStart;

        for (int i = 0; i < periodMonths && currentDate <= request.PeriodEnd; i++)
        {
            // Sales inflow line
            lines.Add(new CashFlowForecastLine
            {
                CompanyId = companyId,
                PeriodDate = currentDate,
                Category = "Sales",
                FlowType = "Inflow",
                ProjectedAmount = avgMonthlyInflow * 0.7m,
                Confidence = request.ForecastMethod == "Historical" ? 0.75m : 0.60m,
                Source = "Historical"
            });

            // Other inflow
            lines.Add(new CashFlowForecastLine
            {
                CompanyId = companyId,
                PeriodDate = currentDate,
                Category = "Other",
                FlowType = "Inflow",
                ProjectedAmount = avgMonthlyInflow * 0.3m,
                Confidence = request.ForecastMethod == "Historical" ? 0.60m : 0.50m,
                Source = "Historical"
            });

            // Purchases outflow
            lines.Add(new CashFlowForecastLine
            {
                CompanyId = companyId,
                PeriodDate = currentDate,
                Category = "Purchases",
                FlowType = "Outflow",
                ProjectedAmount = avgMonthlyOutflow,
                Confidence = request.ForecastMethod == "Historical" ? 0.70m : 0.55m,
                Source = "Historical"
            });

            // Recurring payment outflow (estimated from historical patterns)
            if (recurringMonthlyOutflow > 0)
            {
                lines.Add(new CashFlowForecastLine
                {
                    CompanyId = companyId,
                    PeriodDate = currentDate,
                    Category = "Recurring",
                    FlowType = "Outflow",
                    ProjectedAmount = recurringMonthlyOutflow,
                    Confidence = 0.90m,
                    Source = "RecurringPayment"
                });
            }

            currentDate = currentDate.AddMonths(1);
        }

        var forecast = new CashFlowForecast
        {
            CompanyId = companyId,
            Name = request.Name,
            ForecastDate = DateTime.UtcNow,
            PeriodStart = request.PeriodStart,
            PeriodEnd = request.PeriodEnd,
            ForecastMethod = request.ForecastMethod,
            OpeningBalance = openingBalance,
            ProjectedInflows = projectedInflows,
            ProjectedOutflows = projectedOutflows,
            ProjectedClosingBalance = projectedClosingBalance,
            AccuracyPercent = 0,
            Lines = lines
        };

        _db.CashFlowForecasts.Add(forecast);
        await _db.SaveChangesAsync();

        return MapToForecastResponse(forecast);
    }

    public async Task<CashFlowForecastResponse> GetForecastAsync(Guid companyId, Guid forecastId)
    {
        var forecast = await _db.CashFlowForecasts
            .Include(f => f.Lines)
            .FirstOrDefaultAsync(f => f.Id == forecastId && f.CompanyId == companyId && !f.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลพยากรณ์");

        return MapToForecastResponse(forecast);
    }

    public async Task<List<CashFlowForecastResponse>> GetForecastsAsync(Guid companyId)
    {
        var forecasts = await _db.CashFlowForecasts
            .Include(f => f.Lines)
            .Where(f => f.CompanyId == companyId && !f.IsDeleted)
            .OrderByDescending(f => f.ForecastDate)
            .ToListAsync();

        return forecasts.Select(MapToForecastResponse).ToList();
    }

    // ===== Private Helpers =====

    private static decimal CalculateStdDev(IEnumerable<decimal> values)
    {
        var list = values.ToList();
        if (list.Count <= 1) return 0;

        var avg = list.Average();
        var sumOfSquares = list.Sum(v => (v - avg) * (v - avg));
        return (decimal)Math.Sqrt((double)(sumOfSquares / (list.Count - 1)));
    }

    private static CategorizationResultResponse MapToCategorizationResponse(CategorizationResult r, string? suggestedAccountName)
    {
        return new CategorizationResultResponse(
            r.Id,
            r.EntityType,
            r.EntityId,
            r.SuggestedAccountId,
            suggestedAccountName,
            r.SuggestedCategory,
            r.Confidence,
            r.IsAccepted,
            r.IsRejected
        );
    }

    private static AutoCatRuleResponse MapToRuleResponse(AutoCategorizationRule r)
    {
        return new AutoCatRuleResponse(
            r.Id,
            r.RuleName,
            r.MatchType,
            r.MatchField,
            r.MatchPattern,
            r.TargetAccountId,
            r.TargetCategory,
            r.Priority,
            r.TimesApplied,
            r.IsAiGenerated,
            r.IsActive
        );
    }

    private static AnomalyResponse MapToAnomalyResponse(AnomalyDetection a)
    {
        return new AnomalyResponse(
            a.Id,
            a.AnomalyType,
            a.Severity,
            a.EntityType,
            a.EntityId,
            a.Description,
            a.ExpectedValue,
            a.ActualValue,
            a.DeviationPercent,
            a.Status,
            a.DetectedAt
        );
    }

    private static CashFlowForecastResponse MapToForecastResponse(CashFlowForecast f)
    {
        return new CashFlowForecastResponse(
            f.Id,
            f.Name,
            f.PeriodStart,
            f.PeriodEnd,
            f.ForecastMethod,
            f.OpeningBalance,
            f.ProjectedInflows,
            f.ProjectedOutflows,
            f.ProjectedClosingBalance,
            f.ActualClosingBalance,
            f.AccuracyPercent,
            f.Lines.Select(l => new ForecastLineResponse(
                l.PeriodDate,
                l.Category,
                l.FlowType,
                l.ProjectedAmount,
                l.ActualAmount,
                l.Confidence
            )).ToList()
        );
    }
}
