using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Recurring;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

public class RecurringTransactionService : IRecurringTransactionService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _documentService;
    private readonly IAccountingService _accountingService;
    private readonly IWithholdingTaxCertService _whtService;
    private readonly ILogger<RecurringTransactionService> _logger;
    private readonly IErrorLogService _errorLogService;
    private readonly IEmailScheduleService? _emailSchedule;

    public RecurringTransactionService(
        AccountingDbContext db,
        IDocumentService documentService,
        IAccountingService accountingService,
        IWithholdingTaxCertService whtService,
        ILogger<RecurringTransactionService> logger,
        IErrorLogService errorLogService,
        IEmailScheduleService? emailSchedule = null)
    {
        _db = db;
        _documentService = documentService;
        _accountingService = accountingService;
        _whtService = whtService;
        _logger = logger;
        _errorLogService = errorLogService;
        _emailSchedule = emailSchedule;
    }

    public async Task<RecurringTransactionResponse> CreateAsync(Guid companyId, CreateRecurringTransactionRequest request, string createdBy)
    {
        await ValidateTemplateAsync(companyId, request.TemplateType, request.TemplateData, request.ContactId);

        var recurring = new RecurringTransaction
        {
            CompanyId = companyId,
            Name = request.Name,
            Description = request.Description,
            Frequency = request.Frequency,
            Status = RecurringStatus.Active,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            NextRunDate = request.StartDate,
            PreferredDay = request.StartDate.Day,
            MaxRuns = request.MaxRuns,
            TemplateType = request.TemplateType,
            DocumentType = request.DocumentType,
            ContactId = request.ContactId,
            TemplateData = request.TemplateData,
            NotifyBeforeRun = request.NotifyBeforeRun,
            NotifyDaysBefore = request.NotifyDaysBefore,
            AutoApprove = request.AutoApprove,
            CreatedBy = createdBy
        };

        _db.RecurringTransactions.Add(recurring);
        await _db.SaveChangesAsync();
        return MapToResponse(recurring);
    }

    public async Task<RecurringTransactionResponse> GetByIdAsync(Guid companyId, Guid id)
    {
        var recurring = await _db.RecurringTransactions
            .Include(r => r.Contact)
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");
        return MapToResponse(recurring);
    }

    public async Task<PagedResponse<RecurringTransactionResponse>> GetAllAsync(Guid companyId, PagedRequest request, string? status = null)
    {
        var query = _db.RecurringTransactions.Include(r => r.Contact).Where(r => r.CompanyId == companyId);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<RecurringStatus>(status, true, out var statusEnum))
            query = query.Where(r => r.Status == statusEnum);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(r => r.Name.Contains(request.Search));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<RecurringTransactionResponse>(
            items.Select(MapToResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<RecurringTransactionResponse> UpdateAsync(Guid companyId, Guid id, UpdateRecurringTransactionRequest request)
    {
        var recurring = await _db.RecurringTransactions
            .Include(r => r.Contact)
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

        if (request.Name != null) recurring.Name = request.Name;
        if (request.Description != null) recurring.Description = request.Description;
        if (request.Frequency.HasValue) recurring.Frequency = request.Frequency.Value;
        if (request.EndDate.HasValue) recurring.EndDate = request.EndDate.Value;
        if (request.MaxRuns.HasValue) recurring.MaxRuns = request.MaxRuns.Value;
        if (request.DocumentType.HasValue) recurring.DocumentType = request.DocumentType.Value;
        if (request.ContactId.HasValue) recurring.ContactId = request.ContactId.Value;
        if (request.TemplateData != null)
        {
            await ValidateTemplateAsync(companyId, recurring.TemplateType, request.TemplateData,
                request.ContactId ?? recurring.ContactId);
            recurring.TemplateData = request.TemplateData;
        }
        if (request.NotifyBeforeRun.HasValue) recurring.NotifyBeforeRun = request.NotifyBeforeRun.Value;
        if (request.NotifyDaysBefore.HasValue) recurring.NotifyDaysBefore = request.NotifyDaysBefore.Value;
        if (request.AutoApprove.HasValue) recurring.AutoApprove = request.AutoApprove.Value;
        if (request.Status.HasValue) recurring.Status = request.Status.Value;

        await _db.SaveChangesAsync();
        return MapToResponse(recurring);
    }

    public async Task DeleteAsync(Guid companyId, Guid id)
    {
        var recurring = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

        recurring.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task<RecurringTransactionResponse> PauseAsync(Guid companyId, Guid id)
    {
        var recurring = await _db.RecurringTransactions
            .Include(r => r.Contact)
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

        if (recurring.Status != RecurringStatus.Active)
            throw new InvalidOperationException("สามารถหยุดชั่วคราวได้เฉพาะรายการที่ Active เท่านั้น");

        recurring.Status = RecurringStatus.Paused;
        await _db.SaveChangesAsync();
        return MapToResponse(recurring);
    }

    public async Task<RecurringTransactionResponse> ResumeAsync(Guid companyId, Guid id)
    {
        var recurring = await _db.RecurringTransactions
            .Include(r => r.Contact)
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

        if (recurring.Status != RecurringStatus.Paused)
            throw new InvalidOperationException("สามารถ resume ได้เฉพาะรายการที่ Paused เท่านั้น");

        recurring.Status = RecurringStatus.Active;
        if (recurring.NextRunDate < DateTime.UtcNow)
            recurring.NextRunDate = DateTime.UtcNow.Date;

        await _db.SaveChangesAsync();
        return MapToResponse(recurring);
    }

    public async Task<RecurringTransactionResponse> RunNowAsync(Guid companyId, Guid id, string performedBy)
    {
        var recurring = await _db.RecurringTransactions
            .Include(r => r.Contact)
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

        if (recurring.Status != RecurringStatus.Active)
            throw new InvalidOperationException("รายการนี้ไม่อยู่ในสถานะ Active");
        if (recurring.MaxRuns.HasValue && recurring.TotalRuns >= recurring.MaxRuns.Value)
            throw new InvalidOperationException("รายการนี้ถึงจำนวนครั้งสูงสุดแล้ว");
        if (recurring.EndDate.HasValue && DateTime.UtcNow > recurring.EndDate.Value)
            throw new InvalidOperationException("รายการนี้เลยวันสิ้นสุดแล้ว");

        // audit D3: lock แถวกันชนกับ cron/ดับเบิลคลิก + เลื่อน NextRunDate เหมือน
        // cron path — เดิม RunNow ไม่เลื่อน → cron รอบถัดไปเห็น NextRunDate เดิม
        // ยัง due → สร้างเอกสารซ้ำ (เลขจริง อนุมัติแล้ว) จากงวดเดียวกัน
        await using var tx = await _db.Database.BeginTransactionAsync();
        _ = await _db.RecurringTransactions
            .FromSqlRaw("SELECT * FROM \"RecurringTransactions\" WHERE \"Id\" = {0} FOR UPDATE", recurring.Id)
            .AsNoTracking()
            .FirstOrDefaultAsync();
        await _db.Entry(recurring).ReloadAsync();
        if (recurring.Status != RecurringStatus.Active)
            throw new InvalidOperationException("รายการนี้ไม่อยู่ในสถานะ Active");

        await ExecuteRecurringAsync(recurring, performedBy);
        if (recurring.NextRunDate <= DateTime.UtcNow)
            recurring.NextRunDate = GetNextRunDate(recurring);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return MapToResponse(recurring);
    }

    public async Task ProcessDueRecurringTransactionsAsync()
    {
        var now = DateTime.UtcNow;
        var dueItems = await _db.RecurringTransactions
            .Where(r => r.Status == RecurringStatus.Active && r.NextRunDate <= now)
            .Where(r => r.EndDate == null || r.EndDate >= now)
            .ToListAsync();

        foreach (var recurring in dueItems)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                // Lock the row to prevent concurrent scheduler instances from processing
                // the same item. ⚠️ ต้อง "เช็คผล" ของ SKIP LOCKED — ถ้าอีก instance ถือ
                // lock อยู่ query จะคืน 0 แถว (ถูก skip) → ต้องข้าม. เดิมใช้
                // ExecuteSqlRawAsync แล้วทิ้งผล → instance ที่ถูก skip ยังทำต่อ (ReloadAsync
                // อ่าน committed state ที่ NextRunDate ยังไม่ถูก advance) → สร้างเอกสารซ้ำ
                // ตอน scale หลาย instance / job รันซ้อน.
                var lockRow = await _db.RecurringTransactions
                    .FromSqlRaw("SELECT * FROM \"RecurringTransactions\" WHERE \"Id\" = {0} FOR UPDATE SKIP LOCKED", recurring.Id)
                    .AsNoTracking()
                    .FirstOrDefaultAsync();
                if (lockRow == null)
                {
                    // instance อื่นถือ lock/กำลังประมวลผลรายการนี้อยู่ → ข้าม กันสร้างซ้ำ
                    await transaction.CommitAsync();
                    continue;
                }

                // Re-read inside transaction to get the latest state (ตอนนี้ถือ lock แล้ว)
                await _db.Entry(recurring).ReloadAsync();

                if (recurring.Status != RecurringStatus.Active || recurring.NextRunDate > now)
                {
                    await transaction.CommitAsync();
                    continue;
                }

                if (recurring.MaxRuns.HasValue && recurring.TotalRuns >= recurring.MaxRuns.Value)
                {
                    recurring.Status = RecurringStatus.Completed;
                    await _db.SaveChangesAsync();
                    await transaction.CommitAsync();
                    continue;
                }

                await ExecuteRecurringAsync(recurring, "System");
                recurring.NextRunDate = GetNextRunDate(recurring);
                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process recurring transaction {Id} ({Name})", recurring.Id, recurring.Name);
                await _errorLogService.LogErrorAsync(ex, $"RecurringTransaction.Execute/{recurring.Id}");
            }
        }
    }

    private async Task ExecuteRecurringAsync(RecurringTransaction recurring, string performedBy)
    {
        // Create actual document or journal entry from template
        if (!string.IsNullOrEmpty(recurring.TemplateData))
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var templateType = recurring.TemplateType?.ToLowerInvariant() ?? "document";

            if (templateType == "journal")
            {
                await CreateJournalFromTemplateAsync(recurring, performedBy, jsonOptions);
            }
            else
            {
                await CreateDocumentFromTemplateAsync(recurring, performedBy, jsonOptions);
            }
        }

        recurring.LastRunDate = DateTime.UtcNow;
        recurring.TotalRuns++;

        if (recurring.MaxRuns.HasValue && recurring.TotalRuns >= recurring.MaxRuns.Value)
            recurring.Status = RecurringStatus.Completed;
    }

    private static DateTime GetNextRunDate(RecurringTransaction r) => r.Frequency switch
    {
        RecurringFrequency.Daily => r.NextRunDate.AddDays(1),
        RecurringFrequency.Weekly => r.NextRunDate.AddDays(7),
        RecurringFrequency.BiWeekly => r.NextRunDate.AddDays(14),
        RecurringFrequency.Monthly => AdvanceMonths(r, 1),
        RecurringFrequency.Quarterly => AdvanceMonths(r, 3),
        RecurringFrequency.SemiAnnual => AdvanceMonths(r, 6),
        RecurringFrequency.Annual => AdvanceMonths(r, 12),
        _ => AdvanceMonths(r, 1)
    };

    private static DateTime AdvanceMonths(RecurringTransaction r, int months)
    {
        var preferredDay = r.PreferredDay ?? r.StartDate.Day;
        var next = r.NextRunDate.AddMonths(months);
        var daysInMonth = DateTime.DaysInMonth(next.Year, next.Month);
        var day = Math.Min(preferredDay, daysInMonth);
        return new DateTime(next.Year, next.Month, day, 0, 0, 0, DateTimeKind.Utc);
    }

    private static readonly string[] _thMonthsFull =
        { "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน",
          "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม" };
    private static readonly string[] _enMonthsFull =
        { "", "January", "February", "March", "April", "May", "June",
          "July", "August", "September", "October", "November", "December" };

    /// <summary>แทนค่า placeholder ในข้อความเทมเพลต recurring ด้วยวันที่จริง
    /// ตอนสร้างเอกสาร (base = วันที่ออกเอกสาร ตามปฏิทินไทย). รองรับทั้ง
    /// &lt;&lt;token&gt;&gt; และ {{token}} (case-insensitive). token ที่ใช้ได้ ดู
    /// RecurringPlaceholderHelp. คืน null ถ้า input null.</summary>
    internal static string? ApplyRecurringPlaceholders(string? text, DateTime baseDate)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var prev = baseDate.AddMonths(-1);
        var next = baseDate.AddMonths(1);
        var q = (baseDate.Month - 1) / 3 + 1;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["month"] = _thMonthsFull[baseDate.Month],
            ["month_en"] = _enMonthsFull[baseDate.Month],
            ["month_no"] = baseDate.Month.ToString("D2"),
            ["month_prev"] = _thMonthsFull[prev.Month],
            ["month_prev_en"] = _enMonthsFull[prev.Month],
            ["month_next"] = _thMonthsFull[next.Month],
            ["month_next_en"] = _enMonthsFull[next.Month],
            ["year"] = (baseDate.Year + 543).ToString(),          // พ.ศ.
            ["year_ce"] = baseDate.Year.ToString(),                // ค.ศ.
            ["year_prev"] = (prev.Year + 543).ToString(),          // ปี พ.ศ. ของเดือนก่อน (คุม ม.ค.)
            ["year_next"] = (next.Year + 543).ToString(),
            ["quarter"] = $"Q{q}",
            ["quarter_th"] = $"ไตรมาส {q}",
            ["date"] = $"{baseDate.Day:D2}/{baseDate.Month:D2}/{baseDate.Year + 543}",
            ["day"] = baseDate.Day.ToString("D2"),
        };

        return System.Text.RegularExpressions.Regex.Replace(
            text, @"(?:<<|\{\{)\s*([a-zA-Z_]+)\s*(?:>>|\}\})",
            m => map.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private async Task CreateDocumentFromTemplateAsync(RecurringTransaction recurring, string performedBy, JsonSerializerOptions jsonOptions)
    {
        try
        {
            using var doc = JsonDocument.Parse(recurring.TemplateData!);
            var root = doc.RootElement;

            var docType = recurring.DocumentType ?? DocumentType.Invoice;
            var contactId = recurring.ContactId
                ?? throw new InvalidOperationException($"รายการที่เกิดซ้ำ '{recurring.Name}' ไม่มีผู้ติดต่อ (ContactId) กรุณาตั้งค่าผู้ติดต่อก่อนดำเนินการ");

            Guid? projectId = root.TryGetProperty("projectId", out var pjEl) && pjEl.ValueKind == JsonValueKind.String && Guid.TryParse(pjEl.GetString(), out var pjId) ? pjId : null;

            // ── Placeholder ในเทมเพลต — แทนค่าวันที่/เดือน/ปี ตอนสร้างเอกสารจริง
            // เช่น "ค่าบริการทำบัญชีเดือน <<month>>" → "...เดือน กรกฎาคม".
            // ฐานคือ "วันที่ออกเอกสาร" (วันนี้ ตามปฏิทินไทย). ดู PlaceholderHelp
            // สำหรับ token ทั้งหมด. ใช้กับ description + notes + reference.
            var baseDate = Accounting.Helpers.ThaiDate.CalendarDateUtc(DateTime.UtcNow);
            string? Sub(string? s) => ApplyRecurringPlaceholders(s, baseDate);

            // Extract lines from template
            var lines = new List<Models.DTOs.Document.DocumentLineRequest>();
            if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in linesEl.EnumerateArray())
                {
                    Guid? lineProjectId = line.TryGetProperty("projectId", out var lpEl) && lpEl.ValueKind == JsonValueKind.String && Guid.TryParse(lpEl.GetString(), out var lpId) ? lpId : null;

                    lines.Add(new Models.DTOs.Document.DocumentLineRequest(
                        Description: Sub(line.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "") ?? "",
                        Quantity: line.TryGetProperty("quantity", out var qty) ? qty.GetDecimal() : 1,
                        UnitPrice: line.TryGetProperty("unitPrice", out var up) ? up.GetDecimal() : 0,
                        Unit: line.TryGetProperty("unit", out var unit) ? unit.GetString() : null,
                        DiscountPercent: line.TryGetProperty("discountPercent", out var dp) ? dp.GetDecimal() : 0,
                        VatRate: line.TryGetProperty("vatRate", out var vr) ? vr.GetDecimal() : 7,
                        WithholdingTaxRate: line.TryGetProperty("withholdingTaxRate", out var wt) ? wt.GetDecimal() : 0,
                        AccountId: line.TryGetProperty("accountId", out var aid) && aid.ValueKind == JsonValueKind.String ? Guid.Parse(aid.GetString()!) : null,
                        ProjectId: lineProjectId,
                        // audit D1: template ที่ใช้ส่วนลดบาท/ธง VAT ต้องห้าม/สินค้า
                        // เดิมหาย → ใบที่ generate แพงกว่า template + VAT ต้องห้าม
                        // กลับเคลมได้
                        ProductCode: line.TryGetProperty("productCode", out var pc) && pc.ValueKind == JsonValueKind.String ? pc.GetString() : null,
                        IsVatClaimable: !line.TryGetProperty("isVatClaimable", out var ivc) || ivc.ValueKind != JsonValueKind.False,
                        DiscountAmount: line.TryGetProperty("discountAmount", out var da) && da.ValueKind == JsonValueKind.Number ? da.GetDecimal() : null
                    ));
                }
            }

            Guid? bankAccountId = root.TryGetProperty("bankAccountId", out var baEl) && baEl.ValueKind == JsonValueKind.String && Guid.TryParse(baEl.GetString(), out var baId) ? baId : null;
            Guid? paymentAccountId = root.TryGetProperty("paymentAccountId", out var paEl) && paEl.ValueKind == JsonValueKind.String && Guid.TryParse(paEl.GetString(), out var paId) ? paId : null;
            Guid? expenseCategoryId = root.TryGetProperty("expenseCategoryId", out var ecEl) && ecEl.ValueKind == JsonValueKind.String && Guid.TryParse(ecEl.GetString(), out var ecId) ? ecId : null;

            var request = new Models.DTOs.Document.CreateDocumentRequest(
                DocumentType: docType,
                DocumentDate: DateTime.UtcNow,
                DueDate: root.TryGetProperty("dueDays", out var dd) ? DateTime.UtcNow.AddDays(dd.GetInt32()) : DateTime.UtcNow.AddDays(30),
                ContactId: contactId,
                Reference: Sub($"AUTO-{recurring.Name}"),
                Notes: Sub(root.TryGetProperty("notes", out var notes) ? notes.GetString() : null),
                Lines: lines,
                ProjectId: projectId,
                BankAccountId: bankAccountId,
                PaymentAccountId: paymentAccountId,
                ExpenseCategoryId: expenseCategoryId,
                // audit D2: template ราคารวม VAT / มีส่วนลดท้ายบิล — เดิมไม่ส่ง →
                // ราคารวม VAT ถูกบวก VAT ซ้ำ (+7%) และส่วนลดหาย
                PricesIncludeVat: root.TryGetProperty("pricesIncludeVat", out var piv) && piv.ValueKind == JsonValueKind.True,
                BillDiscountPercent: root.TryGetProperty("billDiscountPercent", out var bdp) && bdp.ValueKind == JsonValueKind.Number ? bdp.GetDecimal() : null,
                BillDiscountAmount: root.TryGetProperty("billDiscountAmount", out var bda) && bda.ValueKind == JsonValueKind.Number ? bda.GetDecimal() : null,
                // ภาษาเอกสารรายใบ (th/en) — template เก่าไม่มี key นี้ → null =
                // ตามค่าบริษัท (พฤติกรรมเดิมเป๊ะ). CreateDocumentAsync กรองค่า
                // ขยะให้อีกชั้นอยู่แล้ว จึงส่งผ่านตรง ๆ ได้
                DocumentLanguage: root.TryGetProperty("documentLanguage", out var dl)
                    && dl.ValueKind == JsonValueKind.String ? dl.GetString() : null
            );

            var result = await _documentService.CreateDocumentAsync(recurring.CompanyId, request, performedBy);

            // Auto-approve if configured
            if (recurring.AutoApprove && result != null)
            {
                try
                {
                    await _documentService.ApproveDocumentAsync(recurring.CompanyId, result.Id, performedBy);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-approve failed for recurring document {DocId}", result.Id);
                    await _errorLogService.LogErrorAsync(ex, $"RecurringTransaction.AutoApprove/{result.Id}");
                }
            }

            // Auto-generate WHT certificate if requested and there's WHT > 0
            var autoWht = root.TryGetProperty("autoGenerateWht", out var awEl) && awEl.ValueKind == JsonValueKind.True;
            if (autoWht && result != null && result.WithholdingTaxAmount > 0)
            {
                try
                {
                    await GenerateWhtCertFromTemplateAsync(recurring.CompanyId, result.Id, contactId, root, performedBy);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-WHT cert generation failed for recurring document {DocId}", result.Id);
                    await _errorLogService.LogErrorAsync(ex, $"RecurringTransaction.AutoWht/{result.Id}");
                }
            }

            _logger.LogInformation("Created document {DocNumber} from recurring {RecurringId}", result?.DocumentNumber, recurring.Id);

            // Auto-email hook: enqueue ตามกฎ RecurringInvoiceCreated.
            // ใช้ trigger แยกจาก DocumentApproved เพราะ recurring อาจไม่
            // approve (AutoApprove=false) แต่ user ยังอยากส่ง draft ออกได้
            // หรือ approve แล้วก็ส่งได้ — กฎต่างกัน. fail-safe.
            if (result != null && _emailSchedule != null)
            {
                try { await _emailSchedule.OnRecurringDocumentCreatedAsync(recurring.CompanyId, result.Id, recurring.Id); }
                catch (Exception ex2) { _logger.LogWarning(ex2, "Email schedule (recurring) enqueue failed Doc={Doc}", result.Id); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create document from recurring template {RecurringId}", recurring.Id);
            throw;
        }
    }

    private async Task GenerateWhtCertFromTemplateAsync(Guid companyId, Guid documentId, Guid payeeContactId, JsonElement root, string performedBy)
    {
        // Re-load the document with lines so we get accurate per-line WHT amounts
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId);
        if (doc == null) return;

        var whtLines = doc.Lines.Where(l => l.WithholdingTaxAmount > 0).ToList();
        if (whtLines.Count == 0) return;

        // Income type code: prefer line-level template field, fallback to first non-empty
        string defaultCode = "40(8)";
        if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in linesEl.EnumerateArray())
            {
                if (l.TryGetProperty("incomeTypeCode", out var codeEl) &&
                    codeEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(codeEl.GetString()))
                {
                    defaultCode = codeEl.GetString()!;
                    break;
                }
            }
        }

        var now = doc.DocumentDate;
        var certLines = whtLines.Select((l, i) => new Models.DTOs.Tax.WithholdingTaxCertLineRequest(
            IncomeTypeCode: !string.IsNullOrWhiteSpace(l.IncomeTypeCode) ? l.IncomeTypeCode! : defaultCode,
            IncomeDescription: l.Description,
            PaymentDate: now,
            IncomeAmount: l.Amount,
            TaxRate: l.WithholdingTaxRate,
            TaxAmount: l.WithholdingTaxAmount,
            Condition: "หักภาษี ณ ที่จ่าย"
        )).ToList();

        var certRequest = new Models.DTOs.Tax.CreateWithholdingTaxCertRequest(
            PayeeContactId: payeeContactId,
            TaxFormType: null,                          // service auto-detects from contact
            TaxYear: now.Year + 543,                    // Buddhist year
            TaxMonth: now.Month,
            CertificateType: Models.DTOs.Tax.WithholdingTaxCertType.Withhold,
            Lines: certLines
        );

        var cert = await _whtService.CreateAsync(companyId, certRequest, performedBy);

        // Link cert <-> document for traceability
        var entity = await _db.WithholdingTaxCerts.FirstOrDefaultAsync(w => w.Id == cert.Id);
        if (entity != null)
        {
            entity.DocumentId = documentId;
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation("Auto-generated WHT certificate {CertNumber} for document {DocNumber}",
            cert.CertificateNumber, doc.DocumentNumber);
    }

    private async Task CreateJournalFromTemplateAsync(RecurringTransaction recurring, string performedBy, JsonSerializerOptions jsonOptions)
    {
        try
        {
            using var doc = JsonDocument.Parse(recurring.TemplateData!);
            var root = doc.RootElement;

            // แทนค่า placeholder (&lt;&lt;month&gt;&gt; ฯลฯ) เหมือนฝั่งเอกสาร
            var baseDate = Accounting.Helpers.ThaiDate.CalendarDateUtc(DateTime.UtcNow);
            string? Sub(string? s) => ApplyRecurringPlaceholders(s, baseDate);

            var lines = new List<Models.DTOs.Accounting.JournalLineRequest>();
            if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in linesEl.EnumerateArray())
                {
                    lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                        AccountId: line.TryGetProperty("accountId", out var aid) ? Guid.Parse(aid.GetString()!) : Guid.Empty,
                        DebitAmount: line.TryGetProperty("debitAmount", out var da) ? da.GetDecimal() : 0,
                        CreditAmount: line.TryGetProperty("creditAmount", out var ca) ? ca.GetDecimal() : 0,
                        Description: Sub(line.TryGetProperty("description", out var desc) ? desc.GetString() : null)
                    ));
                }
            }

            var request = new Models.DTOs.Accounting.CreateJournalEntryRequest(
                EntryDate: DateTime.UtcNow,
                Description: Sub(root.TryGetProperty("description", out var d) ? d.GetString() ?? recurring.Name : recurring.Name) ?? recurring.Name,
                Reference: $"AUTO-{recurring.Name}",
                Lines: lines
            );

            var result = await _accountingService.CreateJournalEntryAsync(recurring.CompanyId, request, performedBy);

            // Auto-post if configured
            if (recurring.AutoApprove && result != null)
            {
                try
                {
                    await _accountingService.PostJournalEntryAsync(recurring.CompanyId, result.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-post failed for recurring journal {JournalId}", result.Id);
                    await _errorLogService.LogErrorAsync(ex, $"RecurringTransaction.AutoPost/{result.Id}");
                }
            }

            _logger.LogInformation("Created journal entry {EntryNumber} from recurring {RecurringId}", result?.EntryNumber, recurring.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create journal entry from recurring template {RecurringId}", recurring.Id);
            throw;
        }
    }

    private static RecurringTransactionResponse MapToResponse(RecurringTransaction r) =>
        new(r.Id, r.Name, r.Description, r.Frequency, r.Status,
            r.StartDate, r.EndDate, r.NextRunDate, r.LastRunDate,
            r.TotalRuns, r.MaxRuns, r.TemplateType, r.DocumentType,
            r.ContactId, r.Contact?.Name,
            r.TemplateData, ExtractAmount(r.TemplateData), r.NotifyBeforeRun,
            r.NotifyDaysBefore, r.AutoApprove, r.CreatedAt);

    /// <summary>ตรวจ template ตอน Create/Update — fail fast ก่อนรอ
    /// midnight cron แล้วเจอ Guid.Empty / GL ผิด tenant. ตรวจ:
    /// (1) JSON parse ได้, (2) journal template → debit = credit + ทุก
    /// accountId อยู่ใน company + GL active, (3) document template →
    /// ContactId required + ทุก line.accountId (ถ้ามี) อยู่ใน company,
    /// (4) bankAccountId/paymentAccountId อยู่ใน company.</summary>
    private async Task ValidateTemplateAsync(Guid companyId, string? templateType, string? templateData, Guid? contactId)
    {
        if (string.IsNullOrWhiteSpace(templateData)) return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(templateData); }
        catch (JsonException ex)
        { throw new InvalidOperationException($"TemplateData ไม่ใช่ JSON ที่ถูกต้อง: {ex.Message}"); }

        using var _ = doc;
        var root = doc.RootElement;
        var type = templateType?.ToLowerInvariant() ?? "document";
        var accountIds = new HashSet<Guid>();

        if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
        {
            decimal totalDebit = 0, totalCredit = 0;
            int idx = 0;
            foreach (var line in linesEl.EnumerateArray())
            {
                idx++;
                if (line.TryGetProperty("accountId", out var aid)
                    && aid.ValueKind == JsonValueKind.String
                    && Guid.TryParse(aid.GetString(), out var gid)
                    && gid != Guid.Empty)
                {
                    accountIds.Add(gid);
                }
                if (type == "journal")
                {
                    totalDebit  += line.TryGetProperty("debitAmount", out var da) ? da.GetDecimal() : 0m;
                    totalCredit += line.TryGetProperty("creditAmount", out var ca) ? ca.GetDecimal() : 0m;
                }
            }

            if (type == "journal")
            {
                if (idx < 2)
                    throw new InvalidOperationException("Template ประเภท journal ต้องมีอย่างน้อย 2 บรรทัด (debit + credit)");
                if (Math.Round(totalDebit, 2) != Math.Round(totalCredit, 2))
                    throw new InvalidOperationException(
                        $"Template journal ไม่สมดุล: Dr {totalDebit:N2} ≠ Cr {totalCredit:N2} — debit ต้องเท่า credit");
            }
        }
        else if (type == "journal")
        {
            throw new InvalidOperationException("Template ประเภท journal ต้องมี field 'lines' เป็น array");
        }

        if (type == "document" && contactId == null)
            throw new InvalidOperationException("Template ประเภท document ต้องมี ContactId (ลูกค้า/ผู้ขาย)");

        // Collect bank/payment/expense category refs
        Guid? bankId = ParseGuidProperty(root, "bankAccountId");
        Guid? paymentId = ParseGuidProperty(root, "paymentAccountId");
        if (bankId.HasValue) accountIds.Add(bankId.Value);
        if (paymentId.HasValue) accountIds.Add(paymentId.Value);

        if (accountIds.Count > 0)
        {
            var validIds = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && a.IsActive && accountIds.Contains(a.Id))
                .Select(a => a.Id)
                .ToListAsync();
            var missing = accountIds.Except(validIds).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Template มี accountId ที่ไม่พบในผังบัญชี (อาจถูกลบหรือคนละบริษัท): {string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? $" ... +{missing.Count - 3}" : "")}");
        }

        if (contactId.HasValue)
        {
            var contactExists = await _db.Contacts.AsNoTracking()
                .AnyAsync(c => c.Id == contactId.Value && c.CompanyId == companyId && !c.IsDeleted);
            if (!contactExists)
                throw new InvalidOperationException("ContactId ที่ระบุไม่พบในบริษัทนี้ (หรือถูกลบไปแล้ว)");
        }
    }

    private static Guid? ParseGuidProperty(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String
            && Guid.TryParse(el.GetString(), out var g)
            && g != Guid.Empty)
            return g;
        return null;
    }

    private static decimal ExtractAmount(string? templateData)
    {
        if (string.IsNullOrEmpty(templateData)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(templateData);
            if (doc.RootElement.TryGetProperty("lines", out var lines) && lines.GetArrayLength() > 0)
            {
                decimal total = 0;
                foreach (var line in lines.EnumerateArray())
                {
                    var qty = line.TryGetProperty("quantity", out var q) ? q.GetDecimal() : 1m;
                    var price = line.TryGetProperty("unitPrice", out var p) ? p.GetDecimal() : 0m;
                    total += qty * price;
                }
                return total;
            }
        }
        catch { }
        return 0;
    }
}
