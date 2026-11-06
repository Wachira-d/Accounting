using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **ใบเสนอราคาที่ลูกค้ายอมรับออนไลน์ → แปลงเป็นร่างใบแจ้งหนี้/ใบกำกับอัตโนมัติ** (รอบ 252 WP-12A · AUTOPILOT §7 · A-7 · คำตัดสิน §2 #36 Q5 ค่าตั้งต้น "ไม่แปลง" · ตั้งต่อใบ)
/// <list type="bullet">
/// <item>งานเบื้องหลัง (<c>AutopilotJob</c> ใต้ <see cref="JobLock"/> — ทีละเครื่อง) อ่านสถานะ ไม่เกาะเส้นยอมรับ (แบบ 240-7): ใบที่ยอมรับแล้ว + ยังไม่ตัดสิน
/// (<c>QuotationAutopilotAt</c> null) + โหมดที่มีผลไม่ใช่ปิด · ค่าตั้งต้นบริษัทมี watermark (<c>QuotationAcceptAutopilotSince</c>) — ไม่แปลงย้อนหลังเงียบ ๆ</item>
/// <item>ตัวตัดสินเดียว <see cref="QuotationAcceptAutopilot.Decide"/> · สูตรแปลงตัวเดียว <c>IDocumentService.ConvertDocumentAsync</c> (ห้ามสร้าง Document เอง) ·
/// กันซ้ำ: ใบที่มีลูกแล้ว = AlreadyConverted · แกนวางบิลของตัวแปลงกันแปลงซ้ำอีกชั้น</item>
/// <item>ล้มดัง: หยุด/ล้ม ⇒ ประทับเหตุบนใบ (หน้า Autopilot "เรื่องที่ระบบทำต่อไม่ได้" + ปุ่มลองอีกครั้ง) + แจ้งทันที E-01c · สำเร็จ ⇒ นับในสรุปประจำวัน (E-01b)</item>
/// </list>
/// </summary>
public class QuotationAcceptAutopilotService
{
    public const int BatchSize = 50;
    public const string Actor = "autopilot";

    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly ILogger<QuotationAcceptAutopilotService> _logger;
    private readonly INotificationEngine? _notify;

    public QuotationAcceptAutopilotService(AccountingDbContext db, IDocumentService docs, ILogger<QuotationAcceptAutopilotService> logger,
        INotificationEngine? notify = null)
    {
        _db = db;
        _docs = docs;
        _logger = logger;
        _notify = notify;
    }

    private static readonly string[] ConvertModes = { nameof(QuotationAcceptMode.ToInvoice), nameof(QuotationAcceptMode.ToTaxInvoice) };

    /// <summary>หนึ่งรอบ (ทุกบริษัท) — คืนจำนวนใบที่ตัดสิน</summary>
    public async Task<int> RunOnceAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        // ผู้สมัคร: ตั้งโหมดแปลงที่ใบเอง หรือ ใบไม่ตั้ง + ค่าบริษัทแปลง + ยอมรับหลังวันเปิดค่าบริษัท (watermark)
        var pending = await _db.Documents.AsNoTracking()
            .Where(d => !d.IsDeleted && d.DocumentType == DocumentType.Quotation && d.QuotationAcceptedAt != null && d.QuotationAutopilotAt == null
                && (ConvertModes.Contains(d.QuotationAcceptAutopilot!)
                    || ((d.QuotationAcceptAutopilot == null || d.QuotationAcceptAutopilot == "")
                        && _db.CompanySettings.Any(s => s.CompanyId == d.CompanyId
                            && ConvertModes.Contains(s.QuotationAcceptAutopilotDefault!)
                            && s.QuotationAcceptAutopilotSince != null && d.QuotationAcceptedAt >= s.QuotationAcceptAutopilotSince))))
            .OrderBy(d => d.QuotationAcceptedAt).Take(BatchSize)
            .Select(d => new { d.Id, d.CompanyId }).ToListAsync(ct);
        var done = 0;
        var failed = 0;
        foreach (var p in pending)
        {
            try
            {
                await ProcessOneAsync(p.CompanyId, p.Id, nowUtc, ct);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ฝ่ายค้าน M7: ล้มนอกด่าน ⇒ ประทับ Failed + เหตุบนใบ (หน้า Autopilot มีปุ่มลองใหม่ · ไม่วนหัวคิวทุก 5 นาที) · รอบนี้จบแล้วโยนสรุป ⇒ ผลรอบของงาน = ล้ม
                failed++;
                _logger.LogError(ex, "แปลงใบเสนอราคาอัตโนมัติ {Doc} ของบริษัท {Company} ล้มนอกด่าน", p.Id, p.CompanyId);
                _db.ChangeTracker.Clear();
                // ฝ่ายค้านรอบสอง LOW: แปลงสำเร็จแล้วแต่ประทับล้ม ⇒ กู้ด้วยการหาใบลูกที่ระบบสร้าง (ไม่ประทับ Failed ทั้งที่มีใบแล้ว)
                if (await AutopilotChildAsync(p.CompanyId, p.Id, ct) is Guid childId)
                    await StampAsync(p.CompanyId, p.Id, QuotationAcceptAutopilot.Outcome.Converted,
                        "ระบบแปลงแล้ว (กู้หลังบันทึกผลไม่สำเร็จ) — รออนุมัติตามขั้นตอนเดิม", childId, nowUtc, ct);
                else
                    await StampAsync(p.CompanyId, p.Id, QuotationAcceptAutopilot.Outcome.Failed,
                        "ระบบตัดสินใบนี้ไม่สำเร็จ: " + (ex is BusinessRuleException or InvalidOperationException ? ex.Message : "ข้อผิดพลาดของระบบ"), null, nowUtc, ct);
            }
        }
        if (failed > 0)
            throw new InvalidOperationException($"ตัดสินใบเสนอราคา {done + failed} ใบ ล้ม {failed} ใบ — เหตุอยู่บนแต่ละใบ (หน้า Autopilot)");
        return done;
    }

    /// <summary>ตัดสิน + ลงมือหนึ่งใบ (ผู้เรียก: <see cref="RunOnceAsync"/> · เทสต์)</summary>
    public async Task ProcessOneAsync(Guid companyId, Guid quotationId, DateTime nowUtc, CancellationToken ct = default)
    {
        var qt = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == quotationId && d.CompanyId == companyId && !d.IsDeleted, ct);
        if (qt == null || qt.QuotationAutopilotAt != null) return;
        var companyDefault = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => s.QuotationAcceptAutopilotDefault).FirstOrDefaultAsync(ct);
        var mode = QuotationAcceptAutopilot.Effective(qt.QuotationAcceptAutopilot, companyDefault);
        var hasChild = await _db.Documents.AsNoTracking().AnyAsync(c => c.CompanyId == companyId && c.RelatedDocumentId == qt.Id && !c.IsDeleted
            && c.Status != DocumentStatus.Voided && c.Status != DocumentStatus.Rejected, ct);
        var vat = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.IsVatRegistered).FirstOrDefaultAsync(ct);
        var decision = QuotationAcceptAutopilot.Decide(mode, new QuotationAcceptAutopilot.Facts(
            qt.DocumentType, qt.Status, qt.QuotationAcceptedAt, QuotationAcceptance.ContentMatches(qt, qt.Lines), hasChild, vat));

        switch (decision.Kind)
        {
            case QuotationAcceptAutopilot.Outcome.NotApplicable:
                return;
            case QuotationAcceptAutopilot.Outcome.AlreadyConverted:
                await StampAsync(companyId, qt.Id, decision.Kind, decision.Reason, null, nowUtc, ct);
                return;
            case QuotationAcceptAutopilot.Outcome.Held:
                if (await StampAsync(companyId, qt.Id, decision.Kind, decision.Reason, null, nowUtc, ct))
                    await NotifyBlockedAsync(companyId, qt.Id, qt.DocumentNumber, qt.ContactId, decision.Reason!, ct);
                return;
        }

        Guid childId;
        try
        {
            var child = await _docs.ConvertDocumentAsync(companyId, qt.Id, decision.Target!.Value, Actor);
            childId = child.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            // ฝ่ายค้านรอบสอง LOW: ตัวแปลง commit ใบลูกแล้วแต่ขั้นหลังโยน ⇒ ถือว่าแปลงแล้ว (ไม่ประทับล้ม · ไม่แปลงซ้ำรอบหน้า)
            if (await AutopilotChildAsync(companyId, qt.Id, ct) is Guid committedChild)
            {
                _logger.LogError(ex, "แปลงใบเสนอราคา {Doc} สำเร็จแต่ขั้นหลังแปลงล้ม — ใช้ใบลูกที่สร้างแล้ว", qt.Id);
                await StampAsync(companyId, qt.Id, QuotationAcceptAutopilot.Outcome.Converted,
                    "ระบบแปลงแล้ว (ขั้นหลังแปลงล้ม — ตรวจใบที่แปลงได้) — รออนุมัติตามขั้นตอนเดิม", committedChild, nowUtc, ct);
                return;
            }
            var msg = ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException ? ex.Message : "ข้อผิดพลาดของระบบระหว่างแปลงเอกสาร";
            if (ex is not (BusinessRuleException or InvalidOperationException or KeyNotFoundException))
                _logger.LogError(ex, "แปลงใบเสนอราคา {Doc} อัตโนมัติล้ม", qt.Id);
            var reason = "แปลงอัตโนมัติไม่สำเร็จ: " + msg;
            if (await StampAsync(companyId, qt.Id, QuotationAcceptAutopilot.Outcome.Failed, reason, null, nowUtc, ct))
                await NotifyBlockedAsync(companyId, qt.Id, qt.DocumentNumber, qt.ContactId, reason, ct);
            return;
        }
        await StampAsync(companyId, qt.Id, QuotationAcceptAutopilot.Outcome.Converted,
            "ระบบแปลงเป็นร่าง " + (decision.Target == DocumentType.TaxInvoice ? "ใบกำกับภาษี" : "ใบแจ้งหนี้") + " แล้ว — รออนุมัติตามขั้นตอนเดิม", childId, nowUtc, ct);
    }

    /// <summary>ใบลูกที่ระบบ (ผู้สร้าง <see cref="Actor"/>) แปลงจากใบเสนอราคานี้และยังมีผล — ใช้กู้เมื่อแปลงสำเร็จแต่บันทึกผลล้ม</summary>
    private async Task<Guid?> AutopilotChildAsync(Guid companyId, Guid quotationId, CancellationToken ct)
        => await _db.Documents.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.RelatedDocumentId == quotationId && !c.IsDeleted && c.CreatedBy == Actor
                && c.Status != DocumentStatus.Voided && c.Status != DocumentStatus.Rejected)
            .OrderBy(c => c.CreatedAt).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);

    /// <summary>ประทับผลแบบ compare-and-set (เฉพาะใบที่ยังไม่ตัดสิน) — คืน true เมื่อประทับได้ (คนที่ประทับได้คือคนแจ้ง — แจ้งครั้งเดียว)</summary>
    private async Task<bool> StampAsync(Guid companyId, Guid quotationId, QuotationAcceptAutopilot.Outcome outcome, string? message, Guid? childId,
        DateTime nowUtc, CancellationToken ct)
    {
        var outcomeName = outcome.ToString();
        var n = await _db.Documents.Where(d => d.Id == quotationId && d.CompanyId == companyId && d.QuotationAutopilotAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.QuotationAutopilotAt, (DateTime?)nowUtc)
                .SetProperty(d => d.QuotationAutopilotOutcome, outcomeName)
                .SetProperty(d => d.QuotationAutopilotMessage, message)
                .SetProperty(d => d.QuotationAutopilotChildId, childId), ct);
        return n > 0;
    }

    private async Task NotifyBlockedAsync(Guid companyId, Guid quotationId, string number, Guid contactId, string reason, CancellationToken ct)
    {
        if (_notify == null) return;
        var customer = await _db.Contacts.AsNoTracking().IgnoreQueryFilters().Where(c => c.Id == contactId && c.CompanyId == companyId)
            .Select(c => c.Name).FirstOrDefaultAsync(ct);
        var (title, message) = AutopilotNotice.QuotationBlocked(number, customer, reason);
        await _notify.DispatchAsync(companyId, NotificationEvents.AutopilotQuotationBlocked, new NotificationContext
        {
            Title = title, Message = message, ActionUrl = "/pages/autopilot.html", EntityType = "Document", EntityId = quotationId,
        });
    }
}
