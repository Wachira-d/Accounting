using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
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
    private readonly ILogger<RecurringTransactionService> _logger;
    private readonly IErrorLogService _errorLogService;
    private readonly IEmailScheduleService? _emailSchedule;
    private readonly INotificationEngine? _notify;

    public RecurringTransactionService(
        AccountingDbContext db,
        IDocumentService documentService,
        IAccountingService accountingService,
        ILogger<RecurringTransactionService> logger,
        IErrorLogService errorLogService,
        IEmailScheduleService? emailSchedule = null,
        INotificationEngine? notify = null)
    {
        _db = db;
        _documentService = documentService;
        _accountingService = accountingService;
        _logger = logger;
        _errorLogService = errorLogService;
        _emailSchedule = emailSchedule;
        _notify = notify;
    }

    /// <summary>ตัวตัดสินกลางว่า template เป็น "สมุดรายวัน" หรือ "เอกสาร" —
    /// ห้ามคำนวณเองซ้ำที่อื่น (ExecuteRecurringAsync/ValidateTemplateAsync/
    /// invariant ส่งอีเมล ใช้ตัวเดียวกันทั้งหมด). "journal" ไม่สนตัวพิมพ์ =
    /// สมุดรายวัน, ค่าว่าง/อื่น ๆ = เอกสาร (พฤติกรรมเดิมเป๊ะ).</summary>
    /// <summary>ทางไปต่อของช่องที่หน้ารายการประจำไม่มีให้แก้ (บัญชีรายบรรทัด · โครงการ · สาขา — ตั้งผ่าน API/ระบบเชื่อมต่อ) — ข้อ 150 ทุกบล็อกต้องมีทางไปต่อ</summary>
    private const string ApiOnlyHint = " (ถ้าช่องนี้ไม่มีในฟอร์ม — ตั้งผ่านระบบเชื่อมต่อ — แก้ผ่านระบบนั้น หรือลบรายการประจำนี้แล้วสร้างใหม่)";

    private static bool IsJournalTemplate(string? templateType)
        => string.Equals(templateType, "journal", StringComparison.OrdinalIgnoreCase);

    public async Task<RecurringTransactionResponse> CreateAsync(Guid companyId, CreateRecurringTransactionRequest request, string createdBy)
    {
        await ValidateTemplateAsync(companyId, request.TemplateType, request.TemplateData, request.ContactId, atSave: true);
        // วันสิ้นสุดก่อนวันเริ่ม = รายการที่ไม่มีวันรันเลย (เดิมบันทึกได้แล้วค้าง "ใช้งาน" เงียบ ๆ ตลอดไป)
        if (request.EndDate.HasValue && !RecurringSchedule.WithinEnd(request.StartDate, request.EndDate))
            throw new BusinessRuleException("วันสิ้นสุดอยู่ก่อนวันเริ่มต้น — แก้วันสิ้นสุด (หรือเว้นว่างถ้าไม่มีกำหนด) แล้วบันทึกอีกครั้ง",
                RecurringTemplate.RuleCode);

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
            // ส่งอีเมลอัตโนมัติ = ต้องอนุมัติอัตโนมัติด้วยเสมอ — เอกสาร Draft
            // ยังเป็นเลข DRAFT-{guid} (§86/4 ออกเลขจริงตอน Approve เท่านั้น)
            // ส่งออกไปหาลูกค้าไม่ได้. บังคับตรงนี้ + echo กลับใน response
            // (ไม่ใช่เงียบ ๆ ไม่ส่งอีเมลแล้วผู้ใช้ไม่รู้ว่าทำไม)
            // template สมุดรายวันไม่มีคู่ค้า/PDF ให้ส่ง → ปัดธงทิ้ง ไม่งั้นจะไป
            // บังคับ AutoApprove ซึ่งฝั่ง journal แปลว่า "post JE อัตโนมัติ"
            // (คนละเรื่องกับที่ผู้ใช้ติ๊ก)
            AutoApprove = request.AutoApprove
                || (request.AutoSendEmail && !IsJournalTemplate(request.TemplateType)),
            AutoSendEmail = request.AutoSendEmail && !IsJournalTemplate(request.TemplateType),
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
        // วันเริ่ม/ความถี่ คือตัวขับของ "กำหนดรันถัดไป" — แก้แล้วต้อง recompute
        // ไม่งั้นเปลี่ยนรายเดือน→รายไตรมาสแล้วระบบยังออกใบเดือนหน้าอีก 1 รอบ
        // (ลูกค้าถูกเรียกเก็บเกิน) และ "วันเริ่มต้น" เดิมแก้แล้วหายเงียบ
        var scheduleChanged = false;
        if (request.StartDate.HasValue && request.StartDate.Value != DateTime.MinValue
            && recurring.StartDate != request.StartDate.Value)
        {
            recurring.StartDate = request.StartDate.Value;
            recurring.PreferredDay = request.StartDate.Value.Day;
            scheduleChanged = true;
        }
        if (request.Frequency.HasValue && recurring.Frequency != request.Frequency.Value)
        {
            recurring.Frequency = request.Frequency.Value;
            scheduleChanged = true;
        }
        if (scheduleChanged)
        {
            // ยังไม่เคยรัน → นัดแรก = วันเริ่ม · เคยรันแล้ว → นัดถัดไปคำนวณจาก
            // รอบล่าสุดด้วยความถี่ใหม่ (ห้ามย้อนไปออกใบซ้ำรอบที่ผ่านมา)
            // GetNextRunDate เดินหน้าจาก NextRunDate ของ entity — ตั้งฐานก่อนเรียก
            if (recurring.LastRunDate.HasValue)
            {
                recurring.NextRunDate = recurring.LastRunDate.Value;
                recurring.NextRunDate = GetNextRunDate(recurring);
            }
            else
            {
                recurring.NextRunDate = recurring.StartDate;
            }
        }
        // MinValue = ล้าง ("รันไม่มีกำหนดสิ้นสุด") — เดิมล้างไม่ได้ รอบบิลหยุดตามวันเก่า
        if (request.EndDate.HasValue)
            recurring.EndDate = request.EndDate.Value == DateTime.MinValue ? null : request.EndDate.Value;
        if (request.MaxRuns.HasValue) recurring.MaxRuns = request.MaxRuns.Value;
        if (request.DocumentType.HasValue) recurring.DocumentType = request.DocumentType.Value;
        // ตรวจทั้งเมื่อแก้เทมเพลต และเมื่อเปลี่ยนผู้ติดต่ออย่างเดียว (เดิมเปลี่ยนผู้ติดต่อผ่าน API ไม่ถูกตรวจเลย ⇒ cron ล้มแทน)
        if (request.TemplateData != null || request.ContactId.HasValue)
            await ValidateTemplateAsync(companyId, recurring.TemplateType, request.TemplateData ?? recurring.TemplateData,
                request.ContactId ?? recurring.ContactId, atSave: true);
        if (request.ContactId.HasValue) recurring.ContactId = request.ContactId.Value;
        if (request.TemplateData != null) recurring.TemplateData = request.TemplateData;
        if (request.NotifyBeforeRun.HasValue) recurring.NotifyBeforeRun = request.NotifyBeforeRun.Value;
        if (request.NotifyDaysBefore.HasValue) recurring.NotifyDaysBefore = request.NotifyDaysBefore.Value;
        if (request.AutoApprove.HasValue) recurring.AutoApprove = request.AutoApprove.Value;
        if (request.AutoSendEmail.HasValue) recurring.AutoSendEmail = request.AutoSendEmail.Value;
        // template สมุดรายวันไม่มีคู่ค้า/PDF ให้ส่ง → ปัดธงทิ้ง **เสมอ** ไม่ใช่
        // เฉพาะตอน request ส่ง field มา — แถวเก่า/ที่แก้ตรง DB ที่ค้างธงไว้ จะได้
        // ไม่ไปบังคับ AutoApprove (ฝั่ง journal = post JE อัตโนมัติ คนละเรื่อง)
        if (IsJournalTemplate(recurring.TemplateType)) recurring.AutoSendEmail = false;
        // invariant เดียวกับตอนสร้าง: ส่งอีเมล ⇒ ต้องอนุมัติอัตโนมัติ
        // (ตั้งหลังทั้งสองฟิลด์ ไม่ว่าผู้ใช้ส่งมาลำดับไหน)
        if (recurring.AutoSendEmail) recurring.AutoApprove = true;
        if (request.Status.HasValue) recurring.Status = request.Status.Value;
        if (recurring.EndDate.HasValue && !RecurringSchedule.WithinEnd(recurring.StartDate, recurring.EndDate))
            throw new BusinessRuleException("วันสิ้นสุดอยู่ก่อนวันเริ่มต้น — แก้วันสิ้นสุด (หรือเว้นว่างถ้าไม่มีกำหนด) แล้วบันทึกอีกครั้ง",
                RecurringTemplate.RuleCode);
        // "เสร็จสิ้น" เพราะเลยวันสิ้นสุด/ครบจำนวนครั้ง แล้วผู้ใช้แก้ให้ไปต่อได้ (ขยาย/ล้างวันสิ้นสุด) ⇒ กลับมาใช้งาน — ไม่งั้นแก้แล้วไม่มีผลเงียบ ๆ
        // (กฎ #4 A) · กำหนดที่ผ่านไปแล้วเริ่มวันนี้ (แบบ Resume — ไม่ออกใบตามหลังรวดเดียว)
        if (!request.Status.HasValue && recurring.Status == RecurringStatus.Completed)
        {
            var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
            var resumeAt = recurring.NextRunDate < today ? today : recurring.NextRunDate;
            if (!RecurringSchedule.IsFinished(resumeAt, recurring.EndDate, recurring.TotalRuns, recurring.MaxRuns))
            {
                recurring.Status = RecurringStatus.Active;
                recurring.NextRunDate = resumeAt;
            }
        }

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
        EnsureRunnableNow(recurring);

        // ล็อกต่อรายการ (session lock ข้ามเครื่อง) กันชนกับ cron/ดับเบิลคลิก + เลื่อน NextRunDate เหมือน cron (audit D3) ·
        // ทีมตรวจรายการประจำ 2026-10-14: เดิมห่อด้วยธุรกรรม + FOR UPDATE แต่ CreateDocumentAsync/ApproveDocumentAsync เปิดธุรกรรมของตัวเอง
        // ⇒ EF โยน "connection is already in a transaction" ทุกครั้ง (ปุ่มนี้กับ cron ไม่เคยออกเอกสารได้) — ใช้แบบเดียวกับเส้นเช็คเอาต์ที่พัก
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.RecurringRun, recurring.Id.ToString(), async () =>
        {
            await _db.Entry(recurring).ReloadAsync();
            EnsureRunnableNow(recurring);
            // งวดค้างหลายงวด ⇒ ข้ามไปงวดล่าสุดที่ถึงกำหนด (ปุ่มนี้ออกงวดเดียว — ไม่ใช่งวดเก่าสุด) · ตัวตัดสินเดียวกับ cron
            var skipNote = await SkipOverduePeriodsAsync(recurring, DateTime.UtcNow);
            // ถึงกำหนดแล้ว = รอบนี้คืองวดนั้น (เลื่อนกำหนดถัดไป) · ก่อนกำหนด = รอบพิเศษ ไม่กินงวด
            var consumesPeriod = recurring.NextRunDate <= DateTime.UtcNow;
            try
            {
                await ExecuteRecurringAsync(recurring, performedBy, consumesPeriod);
                if (skipNote != null) await AppendRunNoticeAsync(recurring, skipNote);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ล้มดังทั้งในคำตอบ (โยนต่อ) และบนแถว (เหตุผลในหน้ารายการประจำ) — ไม่แจ้งเตือนซ้ำเพราะผู้ใช้อยู่ตรงนั้น
                await RecordRunFailureAsync(companyId, recurring.Id, recurring.Name, recurring.CreatedBy, ex, fromSchedule: false);
                throw;
            }
        }, _logger, companyId);
        if (!ran)
            throw new BusinessRuleException("รายการนี้กำลังสร้างเอกสารอยู่ (รอบอัตโนมัติหรือหน้าต่างอื่น) — รอสักครู่แล้วรีเฟรชหน้าเพื่อดูผล",
                RecurringRunFailure.RuleCode, 409);

        var fresh = await _db.RecurringTransactions.AsNoTracking()
            .Include(r => r.Contact)
            .FirstAsync(r => r.Id == id && r.CompanyId == companyId);
        return MapToResponse(fresh);
    }

    /// <summary>ด่าน "กดสร้างทันทีได้ไหม" — ตัวเดียวทั้งก่อนและหลังได้ล็อก (สถานะอาจเปลี่ยนระหว่างรอ)</summary>
    private static void EnsureRunnableNow(RecurringTransaction recurring)
    {
        if (recurring.Status != RecurringStatus.Active)
            throw new InvalidOperationException("รายการนี้ไม่อยู่ในสถานะ Active");
        if (recurring.MaxRuns.HasValue && recurring.TotalRuns >= recurring.MaxRuns.Value)
            throw new InvalidOperationException("รายการนี้ถึงจำนวนครั้งสูงสุดแล้ว");
        // วันสิ้นสุดนับรวมวันนั้น (ตามปฏิทินไทย) — เดิมเทียบกับ 00:00 UTC ⇒ กดในวันสิ้นสุดหลัง 07:00 น. ไม่ได้
        if (!RecurringSchedule.WithinEnd(DateTime.UtcNow, recurring.EndDate))
            throw new InvalidOperationException("รายการนี้เลยวันสิ้นสุดแล้ว — ขยายวันสิ้นสุดในหน้าแก้ไขก่อน ถ้าต้องการสร้างต่อ");
    }

    public async Task ProcessDueRecurringTransactionsAsync()
    {
        var now = DateTime.UtcNow;
        // อ่านแค่รหัส — แต่ละรายการโหลดใหม่ภายใต้ล็อก (ไม่ถือ entity ข้ามรอบ: ของค้างจากรายการที่ล้มห้ามไหลไปกับการบันทึกของรายการถัดไป) ·
        // ไม่กรองวันสิ้นสุดที่นี่ — รายการที่เลยวันสิ้นสุดถูกปิดเป็น "เสร็จสิ้น" ในลูป (เดิมค้าง "ใช้งาน" ตลอดไป และงวดที่ตรงวันสิ้นสุดถูกตัดทิ้ง)
        var due = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Status == RecurringStatus.Active && r.NextRunDate <= now)
            .OrderBy(r => r.NextRunDate)
            .Select(r => new { r.Id, r.CompanyId, r.Name, r.CreatedBy })
            .ToListAsync();

        foreach (var item in due)
        {
            _db.ChangeTracker.Clear();
            try
            {
                // ล็อกต่อรายการแบบ try: อีกเครื่อง/ปุ่ม "สร้างทันที" ถืออยู่ = ข้าม (รอบหน้าค่อยว่ากัน) — กันออกใบงวดเดียวกันซ้ำตอน scale หลายเครื่อง
                await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.RecurringRun, item.Id.ToString(), async () =>
                {
                    var recurring = await _db.RecurringTransactions
                        .FirstOrDefaultAsync(r => r.Id == item.Id && r.CompanyId == item.CompanyId);
                    // อ่านใหม่ภายใต้ล็อก: อีกเครื่องเพิ่งรันเสร็จ (NextRunDate เลื่อนแล้ว) / ถูกหยุด / ถูกลบ ⇒ ข้าม
                    if (recurring == null || recurring.Status != RecurringStatus.Active || recurring.NextRunDate > now) return;
                    var skipNote = await SkipOverduePeriodsAsync(recurring, now);
                    if (RecurringSchedule.IsFinished(recurring.NextRunDate, recurring.EndDate, recurring.TotalRuns, recurring.MaxRuns))
                    {
                        recurring.Status = RecurringStatus.Completed;
                        await _db.SaveChangesAsync();
                        return;
                    }
                    await ExecuteRecurringAsync(recurring, "System", consumesPeriod: true);
                    if (skipNote != null) await AppendRunNoticeAsync(recurring, skipNote);
                }, _logger, item.CompanyId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try
                {
                    await RecordRunFailureAsync(item.CompanyId, item.Id, item.Name, item.CreatedBy, ex, fromSchedule: true);
                }
                catch (Exception rex) when (rex is not OperationCanceledException)
                {
                    // บันทึกเหตุผลลงแถวไม่ได้ (ฐานข้อมูลมีปัญหา) — ลง log ทั้งสองตัวแล้วไปรายการถัดไป (รายการอื่นต้องได้รันต่อ)
                    _logger.LogError(ex, "Failed to process recurring transaction {Id} ({Name})", item.Id, item.Name);
                    _logger.LogError(rex, "Failed to record recurring failure {Id}", item.Id);
                }
            }
        }
    }

    /// <summary>ฝ่ายค้าน 2026-10-14 P0: รายการประจำชนิด<b>เอกสาร</b>ล้มทุกรอบตั้งแต่ 2026-09-03 (ธุรกรรมซ้อน) ⇒ NextRunDate ค้างงวดแรก ·
    /// พอแก้ให้รันได้ รอบอัตโนมัติจะไล่ออกใบกำกับภาษีย้อนหลังทีละงวดทุก 15 นาที (เลขจริง · ลงวันที่วันนี้ · ส่งอีเมลลูกค้า) ซ้ำกับที่ผู้ใช้ออกมือไปแล้ว ⇒
    /// เลื่อนไปงวดล่าสุดที่ถึงกำหนด ออกงวดเดียว + บอกบนแถวว่าข้ามกี่งวด (ผู้ใช้ตัดสินเองว่าจะกด "สร้างทันที" เพิ่มไหม — คำตัดสินข้อ 150) ·
    /// สมุดรายวันไม่แตะ (ไม่เคยค้าง — CreateJournalEntryAsync ใช้ธุรกรรมเดิม · พฤติกรรมเดิมเป๊ะ) · คืนข้อความแจ้ง หรือ null เมื่อไม่ค้าง</summary>
    private async Task<string?> SkipOverduePeriodsAsync(RecurringTransaction recurring, DateTime nowUtc)
    {
        if (IsJournalTemplate(recurring.TemplateType) || recurring.NextRunDate > nowUtc) return null;
        var (latest, skipped) = RecurringSchedule.CatchUp(recurring.Frequency, recurring.NextRunDate, recurring.PreferredDay,
            recurring.StartDate, nowUtc);
        if (skipped == 0) return null;
        var from = ThaiDate.CalendarDateUtc(recurring.NextRunDate);
        recurring.NextRunDate = latest;
        await _db.SaveChangesAsync();
        var note = $"ข้าม {skipped} งวดที่ค้าง (ตั้งแต่งวด {from:dd/MM}/{from.Year + 543}) ระหว่างที่ระบบสร้างเอกสารไม่ได้ — ออกเฉพาะงวดล่าสุด · "
                   + "ถ้างวดที่ข้ามยังไม่ได้ออกเอง กด \"สร้างทันที\" เพื่อออกเพิ่มทีละใบ";
        await AppendRunNoticeAsync(recurring, note);
        return note;
    }

    /// <summary>ต่อท้ายข้อความแจ้งบนแถว (ไม่ทับข้อความของรอบ) — เขียนตรงแบบ SetRunNoticeAsync</summary>
    private async Task AppendRunNoticeAsync(RecurringTransaction recurring, string note)
    {
        var current = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == recurring.Id && r.CompanyId == recurring.CompanyId)
            .Select(r => r.LastRunNotice).FirstOrDefaultAsync();
        if (current != null && current.Contains(note)) return;
        await SetRunNoticeAsync(recurring, new List<string> { string.IsNullOrEmpty(current) ? note : current + " · " + note });
    }

    /// <summary>หนึ่งรอบของรายการประจำ: ตรวจเทมเพลตด้วยตัวตรวจเดียวกับตอนบันทึก → สร้างเอกสาร/สมุดรายวัน → ประทับรอบ (ทันทีหลังสร้าง)
    /// → อนุมัติ/ส่งอีเมล · <paramref name="consumesPeriod"/> = รอบนี้คืองวดตามกำหนด (เลื่อนกำหนดถัดไป)</summary>
    private async Task ExecuteRecurringAsync(RecurringTransaction recurring, string performedBy, bool consumesPeriod)
    {
        // เทมเพลตว่าง = เดิม "รัน" สำเร็จ (นับครั้ง + เลื่อนงวด) โดยไม่มีเอกสารสักใบ (silent no-op) ⇒ ตอนนี้ล้มพร้อมทางไปต่อ ·
        // ของที่อ้างไว้ถูกลบ/ปิดหลังบันทึก ⇒ ข้อความบอกช่อง (เดิมไปล้มลึกใน CreateDocumentAsync เป็นข้อความเทคนิค —
        // หรือแย่กว่า: บัญชีธนาคารที่ถูกลบ ⇒ AutoPost ตกไปลงเงินสด 111 เงียบ ๆ)
        await ValidateTemplateAsync(recurring.CompanyId, recurring.TemplateType, recurring.TemplateData, recurring.ContactId, atSave: false);
        var baseDate = RecurringSchedule.PlaceholderBaseDate(recurring.NextRunDate, DateTime.UtcNow, consumesPeriod);
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        if (IsJournalTemplate(recurring.TemplateType))
            await CreateJournalFromTemplateAsync(recurring, performedBy, jsonOptions, baseDate, consumesPeriod);
        else
            await CreateDocumentFromTemplateAsync(recurring, performedBy, jsonOptions, baseDate, consumesPeriod);
    }

    /// <summary>ประทับ "รอบนี้สร้างแล้ว" ทันทีหลังเอกสาร/สมุดรายวันถูกบันทึก (ก่อนอนุมัติ/ส่งอีเมล) — ช่องว่างที่ระบบล่มแล้วออกใบงวดเดิมซ้ำ
    /// เหลือแค่ระหว่างสองคำสั่งนี้ · ล้างผลล้มของรอบก่อน · ครบจำนวนครั้ง/เลยวันสิ้นสุด ⇒ เสร็จสิ้น</summary>
    private async Task MarkRunAsync(RecurringTransaction recurring, bool consumesPeriod, Guid? documentId)
    {
        recurring.LastRunDate = DateTime.UtcNow;
        recurring.TotalRuns++;
        if (consumesPeriod) recurring.NextRunDate = GetNextRunDate(recurring);
        recurring.LastRunError = null;
        recurring.LastRunErrorAt = null;
        recurring.LastRunNotice = null;
        if (documentId.HasValue) recurring.LastDocumentId = documentId;
        if (RecurringSchedule.IsFinished(recurring.NextRunDate, recurring.EndDate, recurring.TotalRuns, recurring.MaxRuns))
            recurring.Status = RecurringStatus.Completed;
        await _db.SaveChangesAsync();
    }

    /// <summary>เรื่องที่ต้องทำต่อของรอบที่สร้างเอกสารสำเร็จ (อนุมัติอัตโนมัติไม่สำเร็จ · ส่งอีเมลไม่ได้ ฯลฯ) — เขียนตรงที่แถว
    /// (ไม่ผ่าน SaveChanges: การอนุมัติ/post ที่ล้มอาจทิ้งค่าค้างใน context ซึ่งห้ามถูกบันทึกตาม)</summary>
    private async Task SetRunNoticeAsync(RecurringTransaction recurring, List<string> notices)
    {
        if (notices.Count == 0) return;
        var text = string.Join(" · ", notices);
        if (text.Length > 2000) text = text[..2000] + "…";
        await _db.RecurringTransactions
            .Where(r => r.Id == recurring.Id && r.CompanyId == recurring.CompanyId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.LastRunNotice, text));
    }

    /// <summary>รอบล้ม ⇒ เหตุผล (ไทย · บอกช่อง + ทางแก้) ลงแถว + ErrorLog/แจ้งผู้ตั้งรายการ <b>เฉพาะครั้งแรก/เมื่อสาเหตุเปลี่ยน</b>
    /// (เดิม ErrorLog ทุก 15 นาทีตลอดไป และผู้ใช้ไม่เห็นอะไรเลย) · ระบบยังลองใหม่ทุกรอบ — แก้แล้วรอบถัดไปเดินเอง (คำตัดสินข้อ 150)</summary>
    private async Task RecordRunFailureAsync(Guid companyId, Guid recurringId, string name, string? createdBy, Exception ex, bool fromSchedule)
    {
        var reason = RecurringRunFailure.UserMessage(ex);
        _db.ChangeTracker.Clear();   // เอกสารครึ่งทางของรอบที่ล้มต้องไม่ถูกบันทึกตามไปกับคำสั่งถัดไป
        var previous = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == recurringId && r.CompanyId == companyId)
            .Select(r => r.LastRunError)
            .FirstOrDefaultAsync();
        var at = DateTime.UtcNow;
        await _db.RecurringTransactions
            .Where(r => r.Id == recurringId && r.CompanyId == companyId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.LastRunError, reason).SetProperty(r => r.LastRunErrorAt, (DateTime?)at));
        if (previous == reason)
        {
            _logger.LogInformation("Recurring {Id} still failing (same reason) — skip duplicate error log/notification", recurringId);
            return;
        }

        _logger.LogError(ex, "Failed to process recurring transaction {Id} ({Name})", recurringId, name);
        await _errorLogService.LogErrorAsync(ex, $"RecurringTransaction.Execute/{recurringId}");
        if (!fromSchedule || _notify == null) return;
        try
        {
            await _notify.DispatchAsync(companyId, global::Accounting.Models.Constants.NotificationEvents.RecurringRunFailed, new NotificationContext
            {
                Title = $"รายการประจำ \"{name}\" สร้างเอกสารไม่สำเร็จ",
                Message = reason + " — แก้ไขที่หน้ารายการประจำ แล้วกด \"สร้างทันที\" หรือรอรอบอัตโนมัติถัดไป (ระบบลองใหม่ให้เอง)",
                ActionUrl = "/pages/recurring.html",
                EntityType = "RecurringTransaction",
                EntityId = recurringId,
                RecipientUserId = Guid.TryParse(createdBy, out var ownerId) ? ownerId : null,
            });
        }
        catch (Exception nex) when (nex is not OperationCanceledException)
        {
            // เหตุผลอยู่บนแถวแล้ว (หน้ารายการประจำ) — การแจ้งเตือนล้มไม่ทำให้รอบอื่นหยุด
            _logger.LogWarning(nex, "Recurring {Id}: failure notification dispatch failed", recurringId);
        }
    }

    private static DateTime GetNextRunDate(RecurringTransaction r)
        => RecurringSchedule.Next(r.Frequency, r.NextRunDate, r.PreferredDay, r.StartDate);

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

    private async Task CreateDocumentFromTemplateAsync(RecurringTransaction recurring, string performedBy, JsonSerializerOptions jsonOptions,
        DateTime baseDate, bool consumesPeriod)
    {
        try
        {
            using var doc = JsonDocument.Parse(recurring.TemplateData!);
            var root = doc.RootElement;

            var docType = recurring.DocumentType ?? DocumentType.Invoice;
            var contactId = recurring.ContactId
                ?? throw new InvalidOperationException($"รายการที่เกิดซ้ำ '{recurring.Name}' ไม่มีผู้ติดต่อ (ContactId) กรุณาตั้งค่าผู้ติดต่อก่อนดำเนินการ");

            // ตัวอ่านเดียวกับตัวตรวจตอนบันทึก (RecurringTemplate) — ค่าที่อ่านไม่ได้ ⇒ ข้อความบอกช่อง (เดิม Guid.Parse/GetDecimal โยนข้อความเทคนิค)
            Guid? projectId = RecurringTemplate.ReadGuid(root, "projectId", 0);

            // ── Placeholder ในเทมเพลต — แทนค่าวันที่/เดือน/ปี ตอนสร้างเอกสารจริง
            // เช่น "ค่าบริการทำบัญชีเดือน <<month>>" → "...เดือน กรกฎาคม".
            // ฐาน = วันของงวด (รอบตามกำหนด) หรือวันนี้ (รอบพิเศษก่อนกำหนด) ตามปฏิทินไทย — RecurringSchedule.PlaceholderBaseDate
            // (เดิมวันนี้เสมอ ⇒ ใบตามหลังหลายงวดเขียนเดือนเดียวกันทุกใบ) · ใช้กับ description + notes + reference
            string? Sub(string? s) => ApplyRecurringPlaceholders(s, baseDate);

            // ฝ่ายค้าน P3-6 (รอบ 204): ผู้ถูกหัก (หัวเอกสาร + รายบรรทัด) ของเทมเพลตที่ผู้ติดต่อถูกลบไปแล้ว ⇒ สืบทอดเป็น null + หมายเหตุบนใบ +
            // ไม่อนุมัติอัตโนมัติใบนั้น (เดิมตัวตรวจตอนสร้างปฏิเสธ ⇒ รายการประจำล้มทุกงวดจนกว่าจะกู้ผู้ติดต่อ) — ตัวสืบทอดเดียว WhtPayeeSplit.InheritPayee
            static Guid? TplPayee(JsonElement el) => el.TryGetProperty("whtPayeeContactId", out var tpEl)
                && tpEl.ValueKind == JsonValueKind.String && Guid.TryParse(tpEl.GetString(), out var tpId) ? tpId : null;
            // ฝ่ายค้านรอบ 204 (หลัง 0f428969): นับเฉพาะผู้ถูกหักที่มีผลกับภาษีหัก (บรรทัดที่มีอัตราหัก · หัวเอกสารที่มีบรรทัดหักตามอยู่)
            // ฝ่ายค้านรอบ 204 (หลัง 7b88c053): "หักภาษีจริง" = อัตรา > 0 และยอดบรรทัด > 0 (ตรงกับตอนสร้างที่ดูยอดภาษีที่คำนวณได้) —
            // เดิมดูแค่อัตรา ⇒ บรรทัดราคา 0 ที่ตั้งอัตราไว้ทำให้ไม่ยกผู้ถูกหัก แล้วตอนสร้างโดนด่านหัวเอกสารขัดกับบรรทัด 400 ทุกรอบ
            static bool TplCarriesWht(JsonElement el)
            {
                // ตัวอ่านเดียวกับบรรทัดที่สร้างจริง (RecurringTemplate.TryNumber — สตริงตัวเลขอ่านเหมือนกัน · ค่าอ่านไม่ได้ ⇒ ตัวตรวจปฏิเสธก่อนถึงตรงนี้)
                if (!(RecurringTemplate.TryNumber(el, "withholdingTaxRate", 0m) is decimal tw && tw > 0m))
                    return false;
                var tq = RecurringTemplate.TryNumber(el, "quantity", 1m) ?? 1m;
                var tp = RecurringTemplate.TryNumber(el, "unitPrice", 0m) ?? 0m;
                // ส่วนลด % และส่วนลดบาทของบรรทัด (ตรงกับตอนสร้าง) — ลด 100% ⇒ ไม่มีภาษีหัก ⇒ ไม่นับ
                var tdp = RecurringTemplate.TryNumber(el, "discountPercent", 0m) ?? 0m;
                var tda = RecurringTemplate.TryNumber(el, "discountAmount", 0m) ?? 0m;
                return tq * tp * (1m - tdp / 100m) - tda > 0m;
            }
            var tplLineWht = new List<(Guid? Payee, bool CarriesWht)>();
            if (root.TryGetProperty("lines", out var tplLinesEl) && tplLinesEl.ValueKind == JsonValueKind.Array)
                foreach (var tplLine in tplLinesEl.EnumerateArray())
                    tplLineWht.Add((TplPayee(tplLine), TplCarriesWht(tplLine)));
            var tplPayeeIds = tplLineWht.Select(l => l.Payee).Append(TplPayee(root)).ToList();
            var tplActivePayees = await Accounting.Helpers.ContactHydration.ActiveContactIdsAsync(_db, recurring.CompanyId, tplPayeeIds);
            var tplPayerSide = Accounting.Helpers.WhtCertIssuePlan.AppliesTo(docType);
            var droppedPayees = Accounting.Helpers.WhtPayeeSplit.DroppedInheritedWhtPayees(TplPayee(root), tplPayerSide,
                tplLineWht, tplPayerSide, tplActivePayees);
            // หัวเอกสารของเทมเพลต "ยกตามบรรทัด" ก่อนส่ง (ตัวยกเดียว LiftSinglePayee) — เทมเพลตรูป "หัว X ทุกบรรทัดที่หัก Y" ต้องไม่ชนด่าน
            // "ระบุหัวเอกสารขัดกับรายบรรทัด" แล้วล้มทุกงวด (ผลเท่ากับการยกตอนบันทึก) · น้ำหนักบรรทัด = มีอัตราหักไหม (เทมเพลตไม่มียอดภาษี)
            var tplHeaderPayee = Accounting.Helpers.WhtPayeeSplit.InheritPayee(TplPayee(root), tplPayerSide, tplActivePayees);
            if (tplPayerSide)
                tplHeaderPayee = Accounting.Helpers.WhtPayeeSplit.LiftSinglePayee(contactId, tplHeaderPayee,
                    tplLineWht.Select(l => new Accounting.Helpers.WhtPayeeLine(
                        Accounting.Helpers.WhtPayeeSplit.InheritPayee(l.Payee, tplPayerSide, tplActivePayees), l.CarriesWht ? 1m : 0m)).ToList())
                    .DocPayeeOverride;

            // Extract lines from template
            var lines = new List<Models.DTOs.Document.DocumentLineRequest>();
            if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
            {
                var lineNo = 0;
                foreach (var line in linesEl.EnumerateArray())
                {
                    lineNo++;
                    Guid? lineProjectId = RecurringTemplate.ReadGuid(line, "projectId", lineNo);

                    // ตัวอ่านเดียวกับตัวตรวจ (RecurringTemplate): ตัวเลขที่เป็นสตริง ("1,200") อ่านได้เหมือนยอดบนหน้าจอ · รหัสบัญชีที่อ่านไม่ได้
                    // ⇒ ข้อความบอกบรรทัด/ช่อง (เดิม Guid.Parse ⇒ FormatException · GetDecimal ⇒ InvalidOperationException ตอนเที่ยงคืน)
                    lines.Add(new Models.DTOs.Document.DocumentLineRequest(
                        Description: Sub(RecurringTemplate.Text(line, "description") ?? "") ?? "",
                        Quantity: RecurringTemplate.Number(line, "quantity", 1m, lineNo),
                        UnitPrice: RecurringTemplate.Number(line, "unitPrice", 0m, lineNo),
                        Unit: RecurringTemplate.Text(line, "unit"),
                        DiscountPercent: RecurringTemplate.Number(line, "discountPercent", 0m, lineNo),
                        VatRate: RecurringTemplate.Number(line, "vatRate", 7m, lineNo),
                        WithholdingTaxRate: RecurringTemplate.Number(line, "withholdingTaxRate", 0m, lineNo),
                        AccountId: RecurringTemplate.ReadGuid(line, "accountId", lineNo),
                        ProjectId: lineProjectId,
                        // audit D1: template ที่ใช้ส่วนลดบาท/ธง VAT ต้องห้าม/สินค้า
                        // เดิมหาย → ใบที่ generate แพงกว่า template + VAT ต้องห้าม
                        // กลับเคลมได้
                        ProductCode: line.TryGetProperty("productCode", out var pc) && pc.ValueKind == JsonValueKind.String ? pc.GetString() : null,
                        IsVatClaimable: !line.TryGetProperty("isVatClaimable", out var ivc) || ivc.ValueKind != JsonValueKind.False,
                        DiscountAmount: line.TryGetProperty("discountAmount", out var da) && da.ValueKind is JsonValueKind.Number or JsonValueKind.String
                            ? RecurringTemplate.Number(line, "discountAmount", 0m, lineNo) : null,
                        // ฝ่ายค้าน M4 (2026-10-12): ประเภทเงินได้รายบรรทัดของเทมเพลต (ช่อง fIncomeType ของหน้ารายการประจำ) — เดิมไม่อ่าน ⇒
                        // ใบที่ generate ไม่มีประเภท ⇒ 50 ทวิ ได้ "8" เสมอ · ค่าที่ตารางกลางชี้ขาดไม่ได้ (เทมเพลตเก่า) = ไม่ระบุ (ไม่ทำให้ทั้งรอบล้ม)
                        IncomeTypeCode: Accounting.Helpers.DocumentLineIncomeType.Inherit(
                            line.TryGetProperty("incomeTypeCode", out var itc) && itc.ValueKind == JsonValueKind.String ? itc.GetString() : null,
                            Accounting.Helpers.WhtCertIssuePlan.AppliesTo(docType)),
                        // รอบ 204 (ข้อ 143): ผู้ถูกหักรายบรรทัดของเทมเพลต — ไม่มี key = ตามหัวเอกสาร (พฤติกรรมเดิม) · ชนิดฝั่งอื่น = null
                        WhtPayeeContactId: Accounting.Helpers.WhtPayeeSplit.InheritPayee(TplPayee(line), tplPayerSide, tplActivePayees)
                    ));
                }
            }

            // bankAccountId = ทะเบียนบัญชีธนาคาร (BankAccounts) · paymentAccountId/expenseCategoryId = ผังบัญชี — ตัวตรวจค้นแต่ละตัวในตารางของมันแล้ว
            Guid? bankAccountId = RecurringTemplate.ReadGuid(root, "bankAccountId", 0);
            Guid? paymentAccountId = RecurringTemplate.ReadGuid(root, "paymentAccountId", 0);
            Guid? expenseCategoryId = RecurringTemplate.ReadGuid(root, "expenseCategoryId", 0);

            var request = new Models.DTOs.Document.CreateDocumentRequest(
                DocumentType: docType,
                DocumentDate: DateTime.UtcNow,
                // dueDays ระบุใน template = ใช้ตามนั้น (พร้อม CreditDays ให้
                // สองช่องสอดคล้องกัน) · ไม่ระบุ = ปล่อยให้ DocumentService เติม
                // จากเครดิตของคู่ค้า — เดิม fallback แข็ง +30 วันเสมอ ขณะที่
                // CreditDays ถูกเติมจาก contact (เช่น 45) ⇒ ใบที่ generate
                // ทุกเดือนพิมพ์ "เครดิต 45 วัน" คู่กับครบกำหนด +30 วัน =
                // เอกสารขัดกันเอง + ระบบทวงหนี้เร็วไป 15 วันทุกรอบ
                DueDate: root.TryGetProperty("dueDays", out var dd) && dd.ValueKind == JsonValueKind.Number && dd.TryGetInt32(out var ddDays)
                    ? DateTime.UtcNow.AddDays(ddDays) : null,
                CreditDays: root.TryGetProperty("dueDays", out var dd2) && dd2.ValueKind == JsonValueKind.Number && dd2.TryGetInt32(out var ddCredit)
                    ? ddCredit : null,
                ContactId: contactId,
                Reference: Sub($"AUTO-{recurring.Name}"),
                Notes: Sub(RecurringTemplate.Text(root, "notes")),
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
                    && dl.ValueKind == JsonValueKind.String ? dl.GetString() : null,
                // สาขาผู้ออกใบ — template เก่าไม่มี key นี้ → null = ใช้ค่าบริษัท
                // ตามเดิมเป๊ะ. CreateDocumentAsync ตรวจว่าเป็นสาขาของบริษัทนี้อีกชั้น
                BranchId: RecurringTemplate.ReadGuid(root, "branchId", 0),
                // flag หัวเอกสาร (policy "ทำงานตามหัวกระดาษ") — บิลรายเดือนแบบ
                // "ใบแจ้งหนี้/ใบกำกับภาษี ใบเดียว" คือเคสหลักของ recurring ใน
                // ไทย. template เก่าไม่มี key = false/null (พฤติกรรมเดิม).
                // ไม่รองรับ issuedAsCashReceipt โดยเจตนา — โหมดขายสดลง Dr
                // เงินสดทันที การ auto-generate รายเดือนโดยไม่มีเงินเข้าจริง
                // = เงินสดปลอมในบัญชีทุกเดือน
                CombinedInvoiceTaxInvoice: root.TryGetProperty("combinedInvoiceTaxInvoice", out var citEl)
                    && citEl.ValueKind == JsonValueKind.True,
                BuyerDeclinedTaxInvoice: root.TryGetProperty("buyerDeclinedTaxInvoice", out var bdtEl)
                    && bdtEl.ValueKind == JsonValueKind.True ? true : (bool?)null,
                // คำขอเจ้าของ 2026-10-12 — 50 ทวิ: ค่าเบี้ยประกันรายเดือนผ่านโบรกเกอร์ (ผู้ถูกหัก = บริษัทประกัน) · template เก่าไม่มี key
                // = null = เปิดอัตโนมัติ/ผู้ถูกหัก = ผู้ติดต่อ (พฤติกรรมเดิมเป๊ะ) · CreateDocumentAsync ตรวจผู้ถูกหัก (บริษัทนี้ · ชนิดฝั่งผู้หัก) อีกชั้น
                // ฝ่ายค้าน M5: ติ๊ก "สร้างหนังสือรับรองหัก ณ ที่จ่ายอัตโนมัติ" ของหน้ารายการประจำ (autoGenerateWht) = ติ๊กเดียวกับบนเอกสาร —
                // ตัวตัดสินเดียว WhtCertIssuePlan.RecurringTemplateAutoIssue (เดิมติ๊กนี้ไปเรียก CreateAsync ตรงด้วยรหัส "40(8)" ข้ามตัวตัดสิน)
                WhtCertAutoIssue: Accounting.Helpers.WhtCertIssuePlan.RecurringTemplateAutoIssue(
                    root.TryGetProperty("whtCertAutoIssue", out var waiEl)
                        && waiEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? waiEl.GetBoolean() : (bool?)null,
                    root.TryGetProperty("autoGenerateWht", out var awEl)
                        && awEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? awEl.GetBoolean() : (bool?)null),
                WhtPayeeContactId: tplHeaderPayee,
                // รอบ 204 (เอกสารลูกสืบทอด) — ผู้จ่ายออกภาษีแทนของรายการประจำ ("PayAlways"/"PayOnce"/"Withhold" หรือ 1/2/3) · template เก่าไม่มี key
                // = หัก ณ ที่จ่าย (พฤติกรรมเดิม) · ใบปลายทางฝั่งรับเงิน ⇒ ไม่ส่ง (ตัวสืบทอดเดียว WhtGrossUp.Inherit — ไม่ให้รายการประจำล้มทุกงวด)
                WhtCertType: ParseTemplateWhtCertType(root) is { } tplWhtType
                    ? Accounting.Helpers.WhtGrossUp.Inherit(tplWhtType, docType, docType)
                    : null,
                // รอบ 204 (ทีม B · เอกสารลูกสืบทอด) — "whtBorneByContract" / "whtViaEWithholding" (true/false) · template เก่าไม่มี key ⇒ null
                // (สัญญา = ค่าตั้งต้นของผู้ถูกหัก · e-Withholding = false) · ใบฝั่งรับเงิน ⇒ ไม่ส่ง (ตัวสืบทอดเดียว WhtGrossUp.InheritPayerSideFlag)
                WhtBorneByContract: root.TryGetProperty("whtBorneByContract", out var wbcEl) && wbcEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? Accounting.Helpers.WhtGrossUp.InheritPayerSideFlag(wbcEl.GetBoolean(), docType, docType)
                    : null,
                WhtViaEWithholding: root.TryGetProperty("whtViaEWithholding", out var wewEl) && wewEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? Accounting.Helpers.WhtGrossUp.InheritPayerSideFlag(wewEl.GetBoolean(), docType, docType)
                    : null
            );

            var result = await _documentService.CreateDocumentAsync(recurring.CompanyId, request, performedBy);
            // ประทับรอบทันทีหลังเอกสารถูกบันทึก (ก่อนอนุมัติ/ส่งอีเมล) — ขั้นหลังล้มต้องไม่ทำให้รอบถัดไปออกใบงวดเดิมซ้ำ
            await MarkRunAsync(recurring, consumesPeriod, result.Id);
            // เรื่องที่ต้องทำต่อของรอบนี้ ⇒ LastRunNotice บนแถว (หน้ารายการประจำ) — นอกเหนือจากหมายเหตุบนตัวเอกสาร
            var notices = new List<string>();
            var approved = false;

            // ฝ่ายค้าน P3-6: ผู้ถูกหักถูกตัดทิ้ง ⇒ ป้าย "50 ทวิ ต้องตรวจ" บนใบ (ตัวเดียวกับแปลง/คัดลอก — Decide/คำเตือนอนุมัติอ่าน) + ไม่อนุมัติอัตโนมัติ
            // (50 ทวิ จะออกผิดคน) · ฝ่ายค้านรอบ 204 (หลัง 0f428969): การไม่อนุมัติอัตโนมัติเดินเส้นรายงานเดียวกับการอนุมัติอัตโนมัติล้ม
            // (RecordAutoApproveFailureAsync — หมายเหตุบนเอกสาร + log) ไม่ใช่ LogWarning ที่ไม่มีใครเห็น · ใบอื่นเดินทางเดิม
            if (result != null && droppedPayees > 0)
            {
                var droppedDoc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == result.Id && d.CompanyId == recurring.CompanyId);
                if (droppedDoc != null)
                {
                    droppedDoc.InternalNotes = Accounting.Helpers.WhtPayeeSplit.MarkDroppedInheritedPayees(droppedDoc.InternalNotes,
                        $"รายการประจำ \"{recurring.Name}\"", droppedPayees);
                    await _db.SaveChangesAsync();
                }
                if (recurring.AutoApprove)
                    await _documentService.RecordAutoApproveFailureAsync(recurring.CompanyId, result.Id, "รายการประจำ (อนุมัติอัตโนมัติ)",
                        new Accounting.Helpers.BusinessRuleException(Accounting.Helpers.WhtPayeeSplit.DroppedPayeeAutoApproveSkipReason(droppedPayees),
                            Accounting.Helpers.WhtPayeeSplit.RuleCode));
                notices.Add(Accounting.Helpers.WhtPayeeSplit.DroppedPayeeAutoApproveSkipReason(droppedPayees));
            }

            // Auto-approve if configured (ผู้ถูกหักถูกตัดทิ้ง = ไม่อนุมัติอัตโนมัติ — ข้างบน)
            if (recurring.AutoApprove && result != null && droppedPayees == 0)
            {
                try
                {
                    // คำตัดสินข้อ 114 Q1: ข้อสังเกต §65 ตรีที่การอนุมัติอัตโนมัติผ่านไป (ไม่บล็อก — ข้อ 110) ต้องถึงคน — เก็บรายการที่ผ่านแล้วแจ้งผู้ตั้งรายการประจำ
                    var passed = new List<string>();
                    var approvedDoc = await _documentService.ApproveDocumentAsync(recurring.CompanyId, result.Id, performedBy,
                        Accounting.Helpers.ApprovalAckSource.Unattended, withAiHints: false, passed);
                    approved = true;
                    if (Accounting.Helpers.Section65TerApprovalWarnings.PassedNotice(passed) is { } notice && _notify != null)
                    {
                        try
                        {
                            await _notify.DispatchAsync(recurring.CompanyId, global::Accounting.Models.Constants.NotificationEvents.RecurringApprovedWithNotes, new NotificationContext
                            {
                                Title = $"รายการประจำอนุมัติ {approvedDoc.DocumentNumber} พร้อมข้อสังเกต §65 ตรี",   // เลขจริงหลังอนุมัติ (ไม่ใช่ DRAFT-…)
                                Message = notice,
                                ActionUrl = "/pages/documents.html#doc=" + result.Id,
                                EntityType = "Document",
                                EntityId = result.Id,
                                RecipientUserId = Guid.TryParse(recurring.CreatedBy, out var ownerId) ? ownerId : null,
                            });
                        }
                        catch (Exception nex) when (nex is not OperationCanceledException)
                        {
                            // การแจ้งเตือนล้มไม่ย้อนการอนุมัติ — ข้อสังเกตอยู่บนเอกสาร (หมายเหตุ + audit) แล้ว · ลง log ให้ผู้ดูแลเห็น
                            _logger.LogWarning(nex, "Recurring {Id}: §65 notice dispatch failed for {Doc}", recurring.Id, result.Id);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // PP36_REVIEW P0-2: ApproveDocumentAsync ถอยค่าค้างแล้ว (ใบค้างเป็นร่างจริง) · ล้มดังบนตัวเอกสาร (หมายเหตุภายในพร้อมเหตุผล) —
                    // เดิม log อย่างเดียว ⇒ ผู้ใช้เห็นใบร่างโผล่โดยไม่รู้ว่าทำไมไม่อนุมัติตามที่ตั้งไว้
                    var outcome = await _documentService.RecordAutoApproveFailureAsync(recurring.CompanyId, result.Id, "รายการประจำ (อนุมัติอัตโนมัติ)", ex);
                    approved = outcome.Committed;
                    notices.Add(outcome.Message);
                    await _errorLogService.LogErrorAsync(ex, $"RecurringTransaction.AutoApprove/{result.Id}");
                }
            }

            // ── 50 ทวิ ของใบที่ generate (ฝ่ายค้าน M5 · 2026-10-12) ──
            // ไม่ออกใบเองที่นี่อีกแล้ว: เดิมเรียก _whtService.CreateAsync ตรง (รหัส "40(8)" ฝัง · ผู้ถูกหัก = ผู้ติดต่อเสมอ · ไม่ดูติ๊กบนใบ ·
            // ออกให้ทั้งใบร่าง/ใบเครดิตที่ยังไม่จ่าย) ⇒ ข้าม WhtCertIssuePlan ทั้งตัว · ตอนนี้ 50 ทวิ ออกทางเดียวกับใบคีย์มือ:
            // อนุมัติอัตโนมัติข้างบน (ApproveDocumentAsync → TryAutoIssueForDocumentAsync · Approve) หรืออนุมัติ/บันทึกจ่ายทีหลัง (Payment)
            // — เคารพติ๊ก WhtCertAutoIssue · ผู้ถูกหัก WhtPayeeContactId · ประเภทเงินได้รายบรรทัด · ล้ม = ป้ายบนเอกสาร + ลิสต์รอออกใบ

            _logger.LogInformation("Created document {DocNumber} from recurring {RecurringId}", result?.DocumentNumber, recurring.Id);

            // Auto-email hook: enqueue ตามกฎ RecurringInvoiceCreated ถ้ามี;
            // ถ้าไม่มีกฎเลย EmailScheduleService จะดูธง AutoSendEmail บนตัว
            // recurring แทน (ผู้ใช้ติ๊กในฟอร์มเดียวจบ ไม่ต้องไปสร้างกฎอีกหน้า)
            // ใช้ trigger แยกจาก DocumentApproved เพราะเงื่อนไข/เวลาส่งต่างกัน.
            // ส่งเฉพาะใบที่อนุมัติแล้ว — Draft ยังไม่มีเลขจริง §86/4. fail-safe.
            if (result != null && _emailSchedule != null)
            {
                try { await _emailSchedule.OnRecurringDocumentCreatedAsync(recurring.CompanyId, result.Id, recurring.Id); }
                catch (Exception ex2) when (ex2 is not OperationCanceledException)
                {
                    _logger.LogWarning(ex2, "Email schedule (recurring) enqueue failed Doc={Doc}", result.Id);
                    if (recurring.AutoSendEmail)
                        notices.Add($"เข้าคิวส่งอีเมลเอกสาร {result.DocumentNumber} ไม่สำเร็จ — ส่งเองจากหน้าเอกสาร");
                }
            }
            // ส่งอีเมลอัตโนมัติที่ "ไม่มีอะไรออก" ต้องบอกผู้ใช้ที่หน้ารายการประจำ (เดิม log warning อย่างเดียว · RCE-07)
            if (recurring.AutoSendEmail && result != null)
            {
                if (!approved)
                    notices.Add("ยังไม่ได้ส่งอีเมล — เอกสารไม่ได้ถูกอนุมัติ (ใบร่างยังไม่มีเลขที่จริงตาม §86/4) อนุมัติที่หน้าเอกสารแล้วส่งจากที่นั่น");
                else if (string.IsNullOrWhiteSpace(await _db.Contacts.AsNoTracking()
                             .Where(c => c.Id == contactId && c.CompanyId == recurring.CompanyId)
                             .Select(c => c.Email).FirstOrDefaultAsync()))
                    notices.Add("ไม่ได้ส่งอีเมล — ผู้ติดต่อไม่มีอีเมล: เพิ่มอีเมลที่หน้าผู้ติดต่อ แล้วส่งเอกสารนี้จากหน้าเอกสาร");
            }
            await SetRunNoticeAsync(recurring, notices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create document from recurring template {RecurringId}", recurring.Id);
            throw;
        }
    }

    private async Task CreateJournalFromTemplateAsync(RecurringTransaction recurring, string performedBy, JsonSerializerOptions jsonOptions,
        DateTime baseDate, bool consumesPeriod)
    {
        try
        {
            using var doc = JsonDocument.Parse(recurring.TemplateData!);
            var root = doc.RootElement;

            // แทนค่า placeholder (&lt;&lt;month&gt;&gt; ฯลฯ) เหมือนฝั่งเอกสาร — วันฐานตามงวด (RecurringSchedule.PlaceholderBaseDate)
            string? Sub(string? s) => ApplyRecurringPlaceholders(s, baseDate);

            var lines = new List<Models.DTOs.Accounting.JournalLineRequest>();
            if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
            {
                var lineNo = 0;
                foreach (var line in linesEl.EnumerateArray())
                {
                    lineNo++;
                    // ตัวอ่านเดียวกับตัวตรวจ — ตัวตรวจบังคับบัญชีทุกบรรทัดแล้ว (เดิมไม่มีบัญชี ⇒ Guid.Empty ⇒ ล้มลึกใน CreateJournalEntryAsync)
                    lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                        AccountId: RecurringTemplate.ReadGuid(line, "accountId", lineNo) ?? Guid.Empty,
                        DebitAmount: RecurringTemplate.Number(line, "debitAmount", 0m, lineNo),
                        CreditAmount: RecurringTemplate.Number(line, "creditAmount", 0m, lineNo),
                        Description: Sub(RecurringTemplate.Text(line, "description"))
                    ));
                }
            }

            var request = new Models.DTOs.Accounting.CreateJournalEntryRequest(
                EntryDate: DateTime.UtcNow,
                Description: Sub(RecurringTemplate.Text(root, "description") ?? recurring.Name) ?? recurring.Name,
                Reference: $"AUTO-{recurring.Name}",
                Lines: lines
            );

            var result = await _accountingService.CreateJournalEntryAsync(recurring.CompanyId, request, performedBy);
            await MarkRunAsync(recurring, consumesPeriod, documentId: null);

            // Auto-post if configured
            if (recurring.AutoApprove && result != null)
            {
                try
                {
                    await _accountingService.PostJournalEntryAsync(recurring.CompanyId, result.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // สมุดรายวันค้างเป็นร่าง — เดิม log อย่างเดียว ผู้ใช้ไม่รู้ว่าไม่ได้ลงบัญชี ⇒ บอกที่แถวรายการประจำ (ข้อความไทยหรือข้อความกลาง)
                    _logger.LogWarning(ex, "Auto-post failed for recurring journal {JournalId}", result.Id);
                    await _errorLogService.LogErrorAsync(ex, $"RecurringTransaction.AutoPost/{result.Id}");
                    await SetRunNoticeAsync(recurring, new List<string>
                    {
                        $"ลงบัญชีสมุดรายวัน {result.EntryNumber} อัตโนมัติไม่สำเร็จ (ค้างเป็นร่าง): {RecurringRunFailure.UserMessage(ex)} — เปิดสมุดรายวันแล้วกดลงบัญชีเอง",
                    });
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
            r.NotifyDaysBefore, r.AutoApprove, r.CreatedAt, r.AutoSendEmail,
            r.LastRunError, r.LastRunErrorAt, r.LastRunNotice, r.LastDocumentId);

    /// <summary>ตัวตรวจเทมเพลต <b>ตัวเดียว</b> — ตอนบันทึก (Create/Update · <paramref name="atSave"/>=true) และ <b>ตอนถึงรอบทุกครั้ง</b>
    /// (<c>ExecuteRecurringAsync</c> · false) · รหัสทุกตัวค้นใน "ตารางที่ถูก" ของมัน + บริษัทนี้ + ไม่ถูกลบ (<see cref="RecurringTemplate.Read"/>
    /// แยกชนิดให้): บรรทัด/ช่องทางรับจ่าย(ผัง)/หมวดค่าใช้จ่าย → ผังบัญชี · บัญชีธนาคาร → ทะเบียนบัญชีธนาคาร · โครงการ · สาขา · ผู้ติดต่อ ·
    /// ผู้ถูกหัก (เฉพาะตอนบันทึก — ตอนถึงรอบ ผู้ถูกหักที่ถูกลบสืบทอดเป็น null + ป้าย P3-6 ไม่ล้มทั้งรอบ)
    /// <para>ที่มา 2026-10-14: เดิมรวม <c>bankAccountId</c> เข้าชุดผังบัญชี ⇒ "Template มี accountId ที่ไม่พบในผังบัญชี" ทุกใบที่เลือกบัญชีธนาคาร ·
    /// โครงการ/สาขา/ผู้ถูกหักไม่ถูกตรวจเลย ⇒ cron ล้มแทน · ตอนถึงรอบไม่ตรวจซ้ำ ⇒ บัญชีธนาคารที่ถูกลบหลังบันทึกทำให้ JE ตกไปลงเงินสดเงียบ ๆ</para>
    /// <para>ทุกข้อความบอกช่อง + ทางไปต่อ (ห้าม GUID) · บล็อกเฉพาะ GL/tenant/ข้อมูลที่สร้างเอกสารไม่ได้จริง (คำตัดสินข้อ 150)</para></summary>
    private async Task ValidateTemplateAsync(Guid companyId, string? templateType, string? templateData, Guid? contactId, bool atSave)
    {
        var isJournal = IsJournalTemplate(templateType);
        if (string.IsNullOrWhiteSpace(templateData))
            throw new BusinessRuleException(isJournal
                ? "รายการประจำแบบสมุดรายวันยังไม่มีบรรทัดรายการ — เพิ่มบรรทัดเดบิต/เครดิตแล้วบันทึกอีกครั้ง"
                : "รายการประจำยังไม่มีรายละเอียดรายการ (ยอดเงิน/บรรทัด) — เปิดแก้ไข กรอกยอดเงิน แล้วบันทึกอีกครั้ง", RecurringTemplate.RuleCode);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(templateData); }
        catch (JsonException)
        {
            throw new BusinessRuleException("รายละเอียดรายการประจำอ่านไม่ได้ (รูปแบบข้อมูลเสีย) — เปิดแก้ไขรายการแล้วบันทึกใหม่จากฟอร์ม",
                RecurringTemplate.RuleCode);
        }

        using var _ = doc;
        var scan = RecurringTemplate.Read(doc.RootElement, isJournal);
        if (scan.Problems.Count > 0)
            throw new BusinessRuleException($"รายการประจำมีช่องที่อ่านไม่ได้: {string.Join(" · ", scan.Problems.Take(5))}" +
                $"{(scan.Problems.Count > 5 ? $" … +{scan.Problems.Count - 5}" : "")} — เปิดแก้ไขแล้วกรอกใหม่", RecurringTemplate.RuleCode);

        if (isJournal)
        {
            if (scan.LineCount < 2)
                throw new BusinessRuleException("รายการประจำแบบสมุดรายวันต้องมีอย่างน้อย 2 บรรทัด (เดบิต + เครดิต)", RecurringTemplate.RuleCode);
            decimal totalDebit = 0m, totalCredit = 0m;
            foreach (var line in doc.RootElement.GetProperty("lines").EnumerateArray())
            {
                totalDebit += RecurringTemplate.TryNumber(line, "debitAmount", 0m) ?? 0m;
                totalCredit += RecurringTemplate.TryNumber(line, "creditAmount", 0m) ?? 0m;
            }
            if (Math.Round(totalDebit, 2, MidpointRounding.AwayFromZero) != Math.Round(totalCredit, 2, MidpointRounding.AwayFromZero))
                throw new BusinessRuleException(
                    $"รายการประจำแบบสมุดรายวันไม่สมดุล: เดบิต {totalDebit:N2} ≠ เครดิต {totalCredit:N2} — แก้ยอดให้เท่ากันแล้วบันทึกอีกครั้ง",
                    RecurringTemplate.RuleCode);
        }
        else
        {
            if (contactId == null)
                throw new BusinessRuleException("รายการประจำชนิดเอกสารต้องเลือกผู้ติดต่อ (ลูกค้า/ผู้ขาย) — เลือกผู้ติดต่อแล้วบันทึกอีกครั้ง",
                    RecurringTemplate.RuleCode);
            if (scan.LineCount == 0)
                throw new BusinessRuleException("รายการประจำยังไม่มีบรรทัดรายการ (§86/4 เอกสารต้องมีรายการอย่างน้อย 1 บรรทัด) — กรอกยอดเงิน/รายละเอียดแล้วบันทึกอีกครั้ง",
                    RecurringTemplate.RuleCode);
        }

        if (contactId.HasValue)
        {
            var contactExists = await _db.Contacts.AsNoTracking()
                .AnyAsync(c => c.Id == contactId.Value && c.CompanyId == companyId && !c.IsDeleted);
            if (!contactExists)
                throw new BusinessRuleException("ผู้ติดต่อของรายการประจำไม่พบในบริษัทนี้ (อาจถูกลบไปแล้ว) — เปิดแก้ไขรายการแล้วเลือกผู้ติดต่อใหม่",
                    RecurringTemplate.RuleCode);
        }

        List<RecurringTemplate.Ref> Of(RecurringTemplate.RefKind kind) => scan.Refs.Where(r => r.Kind == kind).ToList();
        static string Fields(IEnumerable<RecurringTemplate.Ref> refs) => string.Join(", ", refs.Select(r => r.Field).Distinct().Take(5));

        // ── ผังบัญชี: บัญชีรายบรรทัด · ช่องทางรับ/จ่าย (paymentAccountId) · หมวดค่าใช้จ่าย ──
        var coaRefs = Of(RecurringTemplate.RefKind.ChartOfAccount);
        if (coaRefs.Count > 0)
        {
            var ids = coaRefs.Select(r => r.Id).Distinct().ToList();
            var found = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && ids.Contains(a.Id))
                .Select(a => new { a.Id, a.AccountCode, a.AccountName, a.IsActive })
                .ToListAsync();
            var foundIds = found.Select(a => a.Id).ToHashSet();
            var missing = coaRefs.Where(r => !foundIds.Contains(r.Id)).ToList();
            if (missing.Count > 0)
                throw new BusinessRuleException(
                    $"บัญชีที่เลือกไว้ไม่พบในผังบัญชีของบริษัทนี้ (อาจถูกลบไปแล้ว): {Fields(missing)} — เปิดแก้ไขรายการประจำ เลือกบัญชีใหม่ แล้วบันทึกอีกครั้ง{ApiOnlyHint}",
                    RecurringTemplate.RuleCode);
            var inactive = found.Where(a => !a.IsActive).ToDictionary(a => a.Id);
            var inactiveRefs = coaRefs.Where(r => inactive.ContainsKey(r.Id)).ToList();
            if (inactiveRefs.Count > 0)
                throw new BusinessRuleException(
                    $"บัญชีถูกปิดใช้งาน: {string.Join(", ", inactiveRefs.Take(3).Select(r => $"{r.Field} = {inactive[r.Id].AccountCode} {inactive[r.Id].AccountName}"))}" +
                    " — เปิดใช้งานที่ผังบัญชี หรือเลือกบัญชีอื่นในรายการประจำ", RecurringTemplate.RuleCode);
        }

        // ── ทะเบียนบัญชีธนาคาร (bankAccountId — ไม่ใช่ผังบัญชี · บั๊ก 2026-10-14) ──
        foreach (var bankRef in Of(RecurringTemplate.RefKind.BankAccount))
        {
            var bank = await _db.BankAccounts.AsNoTracking()
                .Where(b => b.CompanyId == companyId && b.Id == bankRef.Id)
                .Select(b => new { b.AccountName, b.IsActive })
                .FirstOrDefaultAsync();
            if (bank == null)
                throw new BusinessRuleException(
                    "บัญชีธนาคารที่เลือกในช่องทางรับ/จ่ายเงินไม่พบในบริษัทนี้ (อาจถูกลบไปแล้ว) — เปิดแก้ไขรายการประจำ เลือกช่องทางรับ/จ่ายใหม่ แล้วบันทึกอีกครั้ง",
                    RecurringTemplate.RuleCode);
            if (!bank.IsActive)
                throw new BusinessRuleException(
                    $"บัญชีธนาคาร \"{bank.AccountName}\" ถูกปิดใช้งาน — เปิดใช้งานที่หน้าบัญชีธนาคาร หรือเลือกช่องทางรับ/จ่ายอื่นในรายการประจำ",
                    RecurringTemplate.RuleCode);
        }

        // ── โครงการ (หัวเอกสาร + รายบรรทัด) ──
        var projectRefs = Of(RecurringTemplate.RefKind.Project);
        if (projectRefs.Count > 0)
        {
            var ids = projectRefs.Select(r => r.Id).Distinct().ToList();
            var okIds = (await _db.Set<Project>().AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted && ids.Contains(p.Id))
                .Select(p => p.Id).ToListAsync()).ToHashSet();
            var missing = projectRefs.Where(r => !okIds.Contains(r.Id)).ToList();
            if (missing.Count > 0)
                throw new BusinessRuleException(
                    $"โครงการที่เลือกไม่พบในบริษัทนี้ (อาจถูกลบไปแล้ว): {Fields(missing)} — เลือกโครงการใหม่หรือเอาออก แล้วบันทึกอีกครั้ง{ApiOnlyHint}",
                    RecurringTemplate.RuleCode);
        }

        // ── สาขาผู้ออกใบ ──
        var branchRefs = Of(RecurringTemplate.RefKind.Branch);
        if (branchRefs.Count > 0)
        {
            var ids = branchRefs.Select(r => r.Id).Distinct().ToList();
            var okCount = await _db.Set<Branch>().AsNoTracking()
                .CountAsync(b => b.CompanyId == companyId && !b.IsDeleted && ids.Contains(b.Id));
            if (okCount != ids.Count)
                throw new BusinessRuleException(
                    $"สาขาผู้ออกใบที่เลือกไว้ไม่พบในบริษัทนี้ (อาจถูกลบไปแล้ว) — เลือกสาขาใหม่ (หรือเว้นว่าง = ใช้ข้อมูลบริษัท) แล้วบันทึกอีกครั้ง{ApiOnlyHint}",
                    RecurringTemplate.RuleCode);
        }

        // ── ผู้ถูกหักภาษี — ตอนบันทึก: ต้องเป็นผู้ติดต่อของบริษัทนี้ (ข้ามบริษัท/ไม่มีอยู่จริง = บล็อก · ข้อ 150 (3)) ·
        // ผู้ติดต่อที่ "ถูกลบ" ไม่บล็อก — ผู้ถูกหักรายบรรทัดตั้งได้ทาง API เท่านั้น หน้าเว็บเอาออกไม่ได้ ⇒ บล็อกแล้วผู้ใช้แก้รายการไม่ได้เลย ·
        // ตอนถึงรอบสืบทอดเป็น null + ป้าย "50 ทวิ ต้องตรวจ" + ไม่อนุมัติอัตโนมัติ (P3-6) ──
        var payeeRefs = Of(RecurringTemplate.RefKind.WhtPayeeContact);
        if (atSave && payeeRefs.Count > 0)
        {
            var ids = payeeRefs.Select(r => r.Id).Distinct().ToList();
            var known = (await _db.Contacts.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.CompanyId == companyId && ids.Contains(c.Id))
                .Select(c => c.Id).ToListAsync()).ToHashSet();
            var missing = payeeRefs.Where(r => !known.Contains(r.Id)).ToList();
            if (missing.Count > 0)
                throw new BusinessRuleException(
                    $"ผู้ถูกหักภาษีที่เลือกไม่ใช่ผู้ติดต่อของบริษัทนี้: {Fields(missing)} — เลือกผู้ถูกหักใหม่ หรือเอาเครื่องหมาย \"ผู้ถูกหักคนละรายกับผู้ติดต่อ\" ออก",
                    RecurringTemplate.RuleCode);
        }
    }

    private static decimal ExtractAmount(string? templateData)
    {
        if (string.IsNullOrEmpty(templateData)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(templateData);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            {
                decimal total = 0;
                foreach (var line in lines.EnumerateArray())
                {
                    // ⚠️ เดิมเรียก GetDecimal() ตรง ๆ — ค่าที่เป็นสตริง ("1,200") หรือ null โยน ⇒ ยอด ฿0 บนหน้าจอ ·
                    // ตอนนี้ตัวอ่านเดียวกับตอนสร้างเอกสาร (RecurringTemplate.TryNumber) — ยอดบนจอ = ยอดที่จะออกใบจริง
                    var qty = RecurringTemplate.TryNumber(line, "quantity", 1m) ?? 1m;
                    var price = RecurringTemplate.TryNumber(line, "unitPrice", 0m) ?? 0m;
                    total += qty * price;
                }
                return total;
            }
        }
        catch (JsonException) { /* template JSON เสีย — คืน 0 ให้ list ยังโหลดได้ (ตัวตรวจตอนถึงรอบบอกผู้ใช้เองบนแถว) */ }
        return 0;
    }

    /// <summary>รอบ 204 — อ่าน "whtCertType" จาก template (ชื่อ enum หรือเลข 1/2/3) · ไม่มี/อ่านไม่ออก ⇒ null (= หัก ณ ที่จ่าย)</summary>
    private static Accounting.Models.DTOs.Tax.WithholdingTaxCertType? ParseTemplateWhtCertType(JsonElement root)
    {
        if (!root.TryGetProperty("whtCertType", out var el)) return null;
        if (el.ValueKind == JsonValueKind.String
            && Enum.TryParse<Accounting.Models.DTOs.Tax.WithholdingTaxCertType>(el.GetString(), ignoreCase: true, out var byName)
            && Enum.IsDefined(byName))
            return byName;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)
            && Enum.IsDefined(typeof(Accounting.Models.DTOs.Tax.WithholdingTaxCertType), n))
            return (Accounting.Models.DTOs.Tax.WithholdingTaxCertType)n;
        return null;
    }
}
