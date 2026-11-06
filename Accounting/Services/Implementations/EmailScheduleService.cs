using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class EmailScheduleService : IEmailScheduleService
{
    private readonly AccountingDbContext _db;
    private readonly IEmailSenderFactory _senderFactory;
    private readonly IPdfGenerationService _pdf;
    private readonly ILogger<EmailScheduleService> _logger;
    /// <summary>รอบ 241: ลิงก์ชำระเงินในอีเมลตามตาราง (null = สร้างนอก DI — ไม่แนบลิงก์)</summary>
    private readonly IDocumentPayLinkService? _payLinks;
    /// <summary>รอบ 241 ฝ่ายค้าน B2: เพดานอีเมลของแพลตฟอร์ม + URL ของระบบ (null = ค่าตั้งต้น)</summary>
    private readonly IConfiguration? _config;
    /// <summary>รอบ 244: ทาง e-Tax by Email ของใบกำกับในคิว (<c>SendDocumentRoutedAsync</c>) · null (สร้างนอก DI) ⇒ ใบที่ต้องไปทาง e-Tax ล้มดังบนคิว (ไม่ส่งเป็นอีเมลธรรมดา)</summary>
    private readonly IDocumentEmailService? _docEmail;

    public EmailScheduleService(AccountingDbContext db, IEmailSenderFactory senderFactory,
        IPdfGenerationService pdf, ILogger<EmailScheduleService> logger, IDocumentPayLinkService? payLinks = null,
        IConfiguration? config = null, IDocumentEmailService? docEmail = null)
    {
        _db = db;
        _senderFactory = senderFactory;
        _pdf = pdf;
        _logger = logger;
        _payLinks = payLinks;
        _config = config;
        _docEmail = docEmail;
    }

    /// <summary>รอบ 241: ลิงก์ชำระเงินของใบนี้ตามตาราง (ค่าตาราง ?? ค่าบริษัท · ช่องทางตาราง ∩ ที่บริษัทเปิด — ตัวตัดสิน <c>DocumentPayLinkPolicy</c>) ·
    /// null = ไม่แนบ · แนบแล้ว: <c>{PayLink}</c> ในเทมเพลต = URL · เทมเพลตไม่มี placeholder = ต่อบล็อกท้ายเนื้อ</summary>
    internal async Task<PayLinkResult?> PayLinkFor(EmailScheduleRule rule, Document doc, CancellationToken ct)
        => _payLinks == null ? null : await _payLinks.ForScheduledEmailAsync(rule, doc, ct);

    internal static string WithPayLink(string body, string? template, PayLinkResult? link, bool isEn)
    {
        if (link?.Url == null || link.Amounts == null || DocumentPayLinkPolicy.HasPlaceholder(template)) return body;
        // คิวเก็บที่หมาย {PayLink} ไม่ใช่ URL (ไม่มี token ตรงใน EmailQueues · ฝ่ายค้าน S1) — URL ประกอบตอนส่งใน ComposeQueueMessageAsync
        return body + DocumentPayLinkPolicy.EmailBlock(DocumentPayLinkPolicy.QueuePlaceholder, link.Amounts, isEn);
    }

    // ── เวลาท้องถิ่นไทย — เก็บใน DB เป็น UTC, แต่ ScheduledFor คำนวณจาก
    // SendAtHour ในมุมมอง Bangkok (UTC+7). ใช้ TimeZoneInfo ถ้ามี.
    private static DateTime BangkokNow() => DateTime.UtcNow.AddHours(7);
    private static DateTime ScheduledUtc(DateTime localDate, int hour)
        => new DateTime(localDate.Year, localDate.Month, localDate.Day, hour, 0, 0, DateTimeKind.Unspecified).AddHours(-7);

    // ═══════════════════════════════════════════════════════════════
    // Triggers
    // ═══════════════════════════════════════════════════════════════

    public async Task OnDocumentApprovedAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        try
        {
            var doc = await _db.Documents.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct);
            if (doc == null) return;
            await _db.HydrateContactAsync(companyId, doc);  // กัน INNER JOIN ตัดใบที่ contact ถูกลบ

            var rules = await _db.EmailScheduleRules.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.IsActive && !r.IsDeleted
                    && r.Trigger == "DocumentApproved"
                    && (r.DocumentType == null || r.DocumentType == doc.DocumentType.ToString()))
                .ToListAsync(ct);

            foreach (var rule in rules)
                await EnqueueDocumentAsync(rule, doc, daysOffset: rule.OffsetDays, suffix: "approved", ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "OnDocumentApproved enqueue failed Doc={Doc}", documentId); }
    }

    public async Task OnPayrollPaidAsync(Guid companyId, Guid payrollRunId, CancellationToken ct = default)
    {
        try
        {
            var rules = await _db.EmailScheduleRules.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.IsActive && !r.IsDeleted
                    && r.Trigger == "PayrollPaid")
                .ToListAsync(ct);
            if (rules.Count == 0) return;

            var details = await _db.PayrollDetails.AsNoTracking()
                .Include(d => d.Employee)
                .Include(d => d.PayrollRun)
                .Where(d => d.PayrollRunId == payrollRunId && d.CompanyId == companyId)
                .ToListAsync(ct);

            foreach (var rule in rules)
            {
                var sendUtc = ScheduledUtc(BangkokNow().Date.AddDays(rule.OffsetDays), rule.SendAtHour);
                foreach (var d in details)
                {
                    var email = ResolveEmployeeEmail(d.Employee);
                    if (string.IsNullOrWhiteSpace(email)) continue;
                    var idem = $"payslip:{payrollRunId}:{d.EmployeeId}";
                    if (await _db.EmailQueues.AnyAsync(q => q.CompanyId == companyId
                        && q.IdempotencyKey == idem && !q.IsDeleted, ct)) continue;

                    var ctx = new Dictionary<string, string?>
                    {
                        ["EmployeeName"] = $"{d.Employee.FirstNameTh} {d.Employee.LastNameTh}",
                        ["Period"] = $"{d.PayrollRun.Month:D2}/{d.PayrollRun.Year}",
                        ["NetPay"] = d.NetPay.ToString("N2"),
                    };
                    _db.EmailQueues.Add(new EmailQueue
                    {
                        CompanyId = companyId,
                        RuleId = rule.Id,
                        EntityType = "PayrollDetail",
                        EntityId = d.Id,
                        ToEmail = email!,
                        BccEmail = rule.BccEmails,
                        Subject = Render(rule.SubjectTemplate ?? "สลิปเงินเดือน {Period}", ctx),
                        Body = Render(rule.BodyTemplate ?? DefaultPayslipBody(), ctx, htmlEncodeValues: true),
                        AttachPdf = true,
                        ScheduledFor = sendUtc,
                        IdempotencyKey = idem,
                    });
                }
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "OnPayrollPaid enqueue failed Run={Run}", payrollRunId); }
    }

    public async Task OnWhtCertIssuedAsync(Guid companyId, Guid certId, CancellationToken ct = default)
    {
        try
        {
            var rules = await _db.EmailScheduleRules.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.IsActive && !r.IsDeleted
                    && r.Trigger == "WhtCertIssued")
                .ToListAsync(ct);
            if (rules.Count == 0) return;

            var cert = await _db.WithholdingTaxCerts.AsNoTracking()
                // ไม่ Include PayeeContact — hydrate แยก (กัน INNER JOIN ทำ cert=null → 50 ทวิ ไม่ส่งเมล)
                .FirstOrDefaultAsync(c => c.Id == certId && c.CompanyId == companyId, ct);
            await _db.HydratePayeeContactAsync(companyId, cert);
            if (cert?.PayeeContact == null) return;

            var email = cert.PayeeContact.Email;
            if (string.IsNullOrWhiteSpace(email)) return;

            foreach (var rule in rules)
            {
                var idem = $"wht-cert:{certId}";
                if (await _db.EmailQueues.AnyAsync(q => q.CompanyId == companyId
                    && q.IdempotencyKey == idem && !q.IsDeleted, ct)) continue;

                var sendUtc = ScheduledUtc(BangkokNow().Date.AddDays(rule.OffsetDays), rule.SendAtHour);
                var ctx = new Dictionary<string, string?>
                {
                    ["DocNumber"] = cert.CertificateNumber,
                    ["ContactName"] = cert.PayeeContact.Name,
                    ["Amount"] = cert.TotalTaxAmount.ToString("N2"),
                };
                _db.EmailQueues.Add(new EmailQueue
                {
                    CompanyId = companyId,
                    RuleId = rule.Id,
                    EntityType = "WithholdingTaxCert",
                    EntityId = certId,
                    ToEmail = email,
                    BccEmail = rule.BccEmails,
                    Subject = Render(rule.SubjectTemplate ?? "หนังสือรับรองหัก ณ ที่จ่าย {DocNumber}", ctx),
                    Body = Render(rule.BodyTemplate ?? DefaultWhtBody(), ctx, htmlEncodeValues: true),
                    AttachPdf = true,
                    ScheduledFor = sendUtc,
                    IdempotencyKey = idem,
                });
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "OnWhtCertIssued enqueue failed Cert={Cert}", certId); }
    }

    public async Task OnRecurringDocumentCreatedAsync(Guid companyId, Guid documentId, Guid recurringId, CancellationToken ct = default)
    {
        try
        {
            var doc = await _db.Documents.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct);
            if (doc == null) return;
            await _db.HydrateContactAsync(companyId, doc);  // กัน INNER JOIN ตัดใบที่ contact ถูกลบ

            // ห้ามส่งอีเมลเอกสาร Draft/รออนุมัติ — เลขจริง §86/4 ออกตอน Approve
            // เท่านั้น (DocumentEmailService บล็อกอยู่แล้ว จะทำให้คิว fail). ถ้า
            // recurring ตั้ง AutoApprove=true เอกสารจะ Approved ตั้งแต่ตอนสร้าง →
            // ส่งได้; ถ้า false ให้รอ trigger "DocumentApproved" ตอน user อนุมัติ.
            if (doc.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval
                or DocumentStatus.Rejected)
            {
                _logger.LogInformation(
                    "Recurring doc {Doc} ยังไม่อนุมัติ ({Status}) — ข้ามการส่งอีเมลอัตโนมัติ (กัน DRAFT ถึงลูกค้า)",
                    documentId, doc.Status);
                return;
            }

            var rules = await _db.EmailScheduleRules.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.IsActive && !r.IsDeleted
                    && r.Trigger == "RecurringInvoiceCreated"
                    && (r.DocumentType == null || r.DocumentType == doc.DocumentType.ToString()))
                .ToListAsync(ct);
            if (rules.Count > 0)
            {
                // recurringId เข้า suffix → idempotency: ทุก recurring run ไม่ทับกัน
                foreach (var rule in rules)
                    await EnqueueDocumentAsync(rule, doc, daysOffset: rule.OffsetDays,
                        suffix: $"recurring-{recurringId}", ct);
                await _db.SaveChangesAsync(ct);
                return;
            }

            // ── ไม่มีกฎในหน้า "ตารางส่งอีเมล" → ใช้ธง AutoSendEmail บนตัว
            // recurring แทน. เจตนา: ผู้ใช้ติ๊กในฟอร์ม "สร้างรายการประจำ" ครั้ง
            // เดียวจบ ไม่ต้องไปประกอบกฎเองอีกหน้า (เดิมติ๊กแล้วไม่มีอะไรเกิดขึ้น
            // = silent no-op). ถ้ามีกฎอยู่แล้วให้กฎชนะ — กันส่งซ้ำ 2 ฉบับ
            var autoSend = await _db.RecurringTransactions.AsNoTracking()
                .Where(r => r.Id == recurringId && r.CompanyId == companyId)
                .Select(r => (bool?)r.AutoSendEmail).FirstOrDefaultAsync(ct);
            if (autoSend != true) return;

            if (string.IsNullOrWhiteSpace(doc.Contact?.Email))
            {
                _logger.LogWarning(
                    "Recurring {Rec} เปิดส่งอีเมลอัตโนมัติ แต่ผู้ติดต่อของเอกสาร {Doc} ไม่มีอีเมล — ข้าม",
                    recurringId, documentId);
                return;
            }

            // rule เสมือน (ไม่บันทึกลง DB) — EmailQueue.RuleId เป็น null ได้
            // Id = Guid.Empty เพื่อให้ EnqueueDocumentAsync ใส่ RuleId = null
            // (ไม่งั้น FK ชี้ไปกฎที่ไม่มีจริง)
            var inlineRule = new EmailScheduleRule
            {
                Id = Guid.Empty,
                CompanyId = companyId,
                Trigger = "RecurringInvoiceCreated",
                OffsetDays = 0,
                SendAtHour = 9,
            };
            // ส่งทันที ไม่รอ 09:00 ของวัน — เจตนาคือ "ออกเอกสารแล้วส่งเลย"
            await EnqueueDocumentAsync(inlineRule, doc, daysOffset: 0,
                suffix: $"autosend-{recurringId}", ct, overrideSendUtc: DateTime.UtcNow);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "OnRecurringDocumentCreated enqueue failed Doc={Doc}", documentId); }
    }

    /// <summary>สรุปรายการที่เกิดขึ้นกับลูกค้าในเดือนที่แล้ว — invoice ที่
    /// ออก/ที่ชำระ/ที่ยังค้าง. ส่งวันที่ DayOfMonth ของเดือนปัจจุบัน
    /// (default 5 = ส่งวันที่ 5 รวมยอดเดือนก่อน). Idempotency: ป้องกัน
    /// ส่งซ้ำต่อ contact ต่อ rule ต่อรอบ year+month ที่เป็น "ของเดือนที่แล้ว".</summary>
    public async Task<int> ScanMonthlyStatementsAsync(CancellationToken ct = default)
    {
        var todayBkk = BangkokNow().Date;
        var rules = await _db.EmailScheduleRules.AsNoTracking()
            .Where(r => r.IsActive && !r.IsDeleted && r.Trigger == "MonthlyStatement")
            .ToListAsync(ct);
        if (rules.Count == 0) return 0;

        int enqueued = 0;
        foreach (var rule in rules)
        {
            // ส่งเฉพาะวันที่ DayOfMonth ของเดือนปัจจุบัน
            var sendDay = Math.Clamp(rule.DayOfMonth <= 0 ? 5 : rule.DayOfMonth, 1, 28);
            if (todayBkk.Day != sendDay) continue;

            // ยอดเดือนที่แล้ว
            var prevMonthStart = new DateTime(todayBkk.Year, todayBkk.Month, 1).AddMonths(-1);
            var prevMonthEnd = prevMonthStart.AddMonths(1).AddDays(-1);
            var period = $"{prevMonthStart:yyyy-MM}";
            var sendUtc = ScheduledUtc(todayBkk, rule.SendAtHour);

            // หา contact ที่มีรายการในเดือนที่แล้ว (ฝั่งขายเท่านั้น — AR statement)
            var arTypes = new[] { DocumentType.Invoice, DocumentType.TaxInvoice,
                DocumentType.Receipt, DocumentType.BillingNote, DocumentType.CreditNote, DocumentType.DebitNote };
            var contactRows = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == rule.CompanyId && !d.IsDeleted
                    && arTypes.Contains(d.DocumentType)
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                    && d.DocumentDate >= prevMonthStart && d.DocumentDate <= prevMonthEnd
                    && d.ContactId != Guid.Empty)
                .GroupBy(d => d.ContactId)
                .Select(g => new { ContactId = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            if (contactRows.Count == 0) continue;

            var contactIds = contactRows.Select(x => x.ContactId).ToList();
            var contacts = await _db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == rule.CompanyId && contactIds.Contains(c.Id) && !c.IsDeleted)
                .ToListAsync(ct);

            // company name สำหรับ template
            var companyName = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == rule.CompanyId)
                .Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";

            foreach (var contact in contacts)
            {
                if (string.IsNullOrWhiteSpace(contact.Email)) continue;
                var idem = $"statement:{contact.Id}:{period}:{rule.Id}";
                if (await _db.EmailQueues.AnyAsync(q => q.CompanyId == rule.CompanyId
                    && q.IdempotencyKey == idem && !q.IsDeleted, ct)) continue;

                // รวมยอดของ contact นี้ในเดือน
                var monthDocs = await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == rule.CompanyId && !d.IsDeleted
                        && d.ContactId == contact.Id
                        && arTypes.Contains(d.DocumentType)
                        && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                        && d.DocumentDate >= prevMonthStart && d.DocumentDate <= prevMonthEnd)
                    .Select(d => new { d.DocumentNumber, d.DocumentDate, d.DocumentType,
                        d.TotalAmount, d.BalanceDue, d.DueDate })
                    .OrderBy(d => d.DocumentDate).ToListAsync(ct);
                if (monthDocs.Count == 0) continue;

                var totalAmount = monthDocs.Sum(d => d.TotalAmount);
                var totalOutstanding = monthDocs.Sum(d => d.BalanceDue);
                var ctx = new Dictionary<string, string?>
                {
                    ["ContactName"] = contact.Name,
                    ["Period"] = period,
                    ["Amount"] = totalAmount.ToString("N2"),
                    ["BalanceDue"] = totalOutstanding.ToString("N2"),
                    ["CompanyName"] = companyName,
                    ["DocCount"] = monthDocs.Count.ToString(),
                };

                // ตาราง HTML สำหรับ default body
                var rowsHtml = string.Concat(monthDocs.Select(d =>
                    $"<tr><td style='padding:6px 10px;border-bottom:1px solid #e5e7eb'>{d.DocumentDate:dd/MM/yyyy}</td>"
                  + $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;font-family:monospace'>{System.Net.WebUtility.HtmlEncode(d.DocumentNumber)}</td>"
                  + $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;text-align:right'>{d.TotalAmount:N2}</td>"
                  + $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;text-align:right;color:{(d.BalanceDue > 0 ? "#dc2626" : "#10b981")}'>{d.BalanceDue:N2}</td></tr>"));

                var subject = Render(rule.SubjectTemplate ?? "สรุปรายการเดือน {Period} — {CompanyName}", ctx);
                var body = !string.IsNullOrEmpty(rule.BodyTemplate)
                    ? Render(rule.BodyTemplate, ctx, htmlEncodeValues: true)
                    : $@"<div style='font-family:sans-serif;max-width:680px'>
                        <p>เรียน คุณ{System.Net.WebUtility.HtmlEncode(contact.Name)}</p>
                        <p>สรุปรายการของเดือน <strong>{period}</strong> จำนวน {monthDocs.Count} รายการ ยอดรวม <strong>{totalAmount:N2} บาท</strong>
                        ค้างชำระ <strong style='color:#dc2626'>{totalOutstanding:N2} บาท</strong></p>
                        <table style='width:100%;border-collapse:collapse;font-size:13px;margin-top:10px'>
                          <thead><tr style='background:#f8fafc'>
                            <th style='padding:8px;text-align:left'>วันที่</th>
                            <th style='padding:8px;text-align:left'>เลขที่</th>
                            <th style='padding:8px;text-align:right'>ยอดรวม</th>
                            <th style='padding:8px;text-align:right'>ค้างชำระ</th>
                          </tr></thead>
                          <tbody>{rowsHtml}</tbody>
                        </table>
                        <p style='color:#64748b;font-size:13px;margin-top:14px'>ส่งโดย {System.Net.WebUtility.HtmlEncode(companyName)} โดยอัตโนมัติ</p>
                      </div>";

                _db.EmailQueues.Add(new EmailQueue
                {
                    CompanyId = rule.CompanyId,
                    RuleId = rule.Id,
                    EntityType = "MonthlyStatement",
                    EntityId = contact.Id,
                    ToEmail = contact.Email,
                    BccEmail = rule.BccEmails,
                    Subject = subject,
                    Body = body,
                    AttachPdf = false,    // statement render inline ใน body ไม่มี PDF
                    ScheduledFor = sendUtc,
                    IdempotencyKey = idem,
                });
                enqueued++;
            }
        }
        if (enqueued > 0) await _db.SaveChangesAsync(ct);
        return enqueued;
    }

    public async Task<int> ScanDueSoonAndOverdueAsync(CancellationToken ct = default)
    {
        var today = BangkokNow().Date;
        var rules = await _db.EmailScheduleRules.AsNoTracking()
            .Where(r => r.IsActive && !r.IsDeleted
                && (r.Trigger == "DocumentDueSoon" || r.Trigger == "DocumentOverdue"))
            .ToListAsync(ct);
        if (rules.Count == 0) return 0;

        int enqueued = 0;
        foreach (var rule in rules)
        {
            // DueSoon: OffsetDays ติดลบ — เช่น -3 = ส่งล่วงหน้า 3 วัน
            // → DueDate ของเอกสารต้องเท่ากับ today + |OffsetDays|.
            // Overdue: OffsetDays บวก — DueDate เท่ากับ today − OffsetDays.
            var target = rule.Trigger == "DocumentDueSoon"
                ? today.AddDays(Math.Abs(rule.OffsetDays))
                : today.AddDays(-rule.OffsetDays);

            var docsQuery = _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == rule.CompanyId
                    && !d.IsDeleted
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                    && d.BalanceDue > 0
                    && d.DueDate != null
                    && d.DueDate.Value.Date == target);
            if (!string.IsNullOrEmpty(rule.DocumentType)
                && Enum.TryParse<DocumentType>(rule.DocumentType, out var dt))
                docsQuery = docsQuery.Where(d => d.DocumentType == dt);

            var docs = await docsQuery.ToListAsync(ct);
            await _db.HydrateContactsAsync(rule.CompanyId, docs);  // กัน INNER JOIN ตัดใบที่ contact ถูกลบ
            var sendUtc = ScheduledUtc(today, rule.SendAtHour);
            foreach (var doc in docs)
            {
                if (await EnqueueDocumentAsync(rule, doc, daysOffset: 0,
                    suffix: rule.Trigger == "DocumentDueSoon" ? $"due-{rule.OffsetDays}" : $"overdue-{rule.OffsetDays}",
                    ct, overrideSendUtc: sendUtc))
                    enqueued++;
            }
        }
        if (enqueued > 0) await _db.SaveChangesAsync(ct);
        return enqueued;
    }

    public async Task<int> ProcessPendingQueueAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Atomic claim with SKIP LOCKED — required for multi-instance correctness.
        // Without this, two pods read the same top-50 "Pending" rows, both flip to
        // "Sending", and the customer receives the invoice/statement twice. The
        // CTE locks the chosen rows, updates them to "Sending" in one statement,
        // and returns the locked ids. Subsequent workers SKIP those rows.
        var claimedIds = await _db.Database.SqlQuery<Guid>(
            $@"WITH claimed AS (
                SELECT ""Id""
                FROM ""EmailQueues""
                WHERE NOT ""IsDeleted"" AND ""Status"" = 'Pending' AND ""ScheduledFor"" <= {now}
                ORDER BY ""ScheduledFor""
                LIMIT 50
                FOR UPDATE SKIP LOCKED
              )
              UPDATE ""EmailQueues"" q
              SET ""Status"" = 'Sending', ""UpdatedAt"" = {now}
              FROM claimed
              WHERE q.""Id"" = claimed.""Id""
              RETURNING q.""Id""").ToListAsync(ct);
        if (claimedIds.Count == 0) return 0;

        // แถวที่ context นี้ติดตามอยู่ก่อนแล้ว (สถานะในหน่วยความจำยังเป็น "Pending") ต้องปล่อยก่อนโหลด — ไม่งั้น EF คืนตัวเก่า (identity resolution)
        // แล้ว DeferOrFail ตั้ง "Pending" = ค่าเดิมในหน่วยความจำ ⇒ ไม่เขียนลงฐาน ⇒ แถวค้าง "Sending" ตลอดไป (ไม่ถูกส่งอีก) · CI 2026-10-14
        foreach (var tracked in _db.ChangeTracker.Entries<EmailQueue>().Where(e => claimedIds.Contains(e.Entity.Id)).ToList())
            tracked.State = EntityState.Detached;

        var batch = await _db.EmailQueues
            .Where(q => claimedIds.Contains(q.Id))
            .ToListAsync(ct);

        int sent = 0;
        foreach (var item in batch)
        {
            try
            {
                // ID#12: เอกสารเปลี่ยนไปหลังเข้าคิว (ยกเลิก · กลับเป็นร่าง · ชำระครบ) ⇒ ข้ามพร้อมเหตุผลบนคิว — ไม่ส่งใบที่ไม่มีผลแล้วถึงลูกค้า
                // ฝ่ายค้าน F4: 50 ทวิ ที่ถูกยกเลิกหลังเข้าคิวก็ต้องไม่ถูกส่ง (ไฟล์แนบเป็นใบที่ไม่มีผลแล้ว) — คลาสเดียวกัน
                var skipReason = item.EntityType == "Document" ? await RecheckDocumentAtSendAsync(item, ct)
                    : item.EntityType == "WithholdingTaxCert" ? await RecheckWhtCertAtSendAsync(item, ct)
                    : null;
                if (skipReason != null)
                {
                    item.Status = ScheduledEmailSendCheck.SkippedStatus;
                    item.ErrorMessage = skipReason;
                    item.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                // รอบ 244: ใบกำกับของบริษัทโหมด e-Tax by Email ⇒ ทาง e-Tax เท่านั้น (EtaxDeliveryRouter ตัวเดียว) — ด่านปฏิเสธ = คิว Failed พร้อมเหตุ ·
                // ห้ามตกไปเส้นอีเมลธรรมดาข้างล่าง (ลูกค้าจะได้ PDF/A-3 ที่ไม่มีวันถูกประทับเวลา)
                if (item.EntityType == "Document" && await SendViaEtaxIfRequiredAsync(item, ct) is bool etaxSent)
                {
                    if (etaxSent) sent++;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                // รอบ 241 ฝ่ายค้าน B2 (คลาสเดียวกับ 240 B1): ผู้ส่งจากตัวตัดสินเดียว — บริษัทตั้ง+ทดสอบผ่าน ⇒ SMTP บริษัท (พฤติกรรมเดิม) ·
                // ไม่งั้น ⇒ SMTP ของ NextAcc ในนามบริษัท = เพดาน (บริษัท/วัน · ผู้รับ · To+Cc+Bcc ≤ 5) + เทมเพลตคงที่ (ข้อความผู้ใช้เป็นข้อความล้วน)
                var compose = await ComposeQueueMessageAsync(item, ct);
                if (compose.Refusal != null)
                {
                    // เกินเพดาน/ผู้รับไม่ถูกต้อง ⇒ ไม่เรียกผู้ให้บริการ · ล้มพร้อมเหตุผลบนคิว + บันทึกการส่ง (กด "ลองใหม่" ได้)
                    item.Status = "Failed";
                    item.ErrorMessage = compose.Refusal;
                    item.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }
                var (msg, sender, log) = (compose.Message!, compose.Sender!, compose.Log);

                if (item.AttachPdf)
                {
                    // ID#12: สร้างไฟล์แนบไม่สำเร็จ ⇒ ไม่ส่งอีเมลเปล่าแล้วประทับ "Sent" (เดิมกลืน error เป็น LogWarning) — ลองใหม่/ล้มพร้อมเหตุผล
                    EmailAttachment? att;
                    try { att = await BuildAttachmentAsync(item, ct); }
                    catch (Exception attEx) when (attEx is not OperationCanceledException)
                    {
                        DeferOrFail(item, ScheduledEmailSendCheck.AttachmentFailure(attEx.Message, willRetry: item.RetryCount < 3));
                        _logger.LogWarning(attEx, "EmailQueue attachment build failed Id={Id}", item.Id);
                        await _db.SaveChangesAsync(ct);
                        continue;
                    }
                    if (att != null) msg.Attachments.Add(att);
                }

                var result = await sender.SendAsync(msg, ct);
                if (log != null)
                {
                    // เส้นแพลตฟอร์ม: ทุกฉบับลงบันทึก (ฐานนับเพดานรายวัน PlatformEmailQuota) · เนื้อในบันทึกคง {PayLink} (ไม่มี token ตรง — ฝ่ายค้าน S1)
                    log.Status = result.Success ? EmailLogStatus.Sent : EmailLogStatus.Failed;
                    log.SentAt = result.Success ? DateTime.UtcNow : null;
                    log.ErrorMessage = result.ErrorMessage;
                    log.ProviderMessageId = result.MessageId;
                    _db.DocumentEmailLogs.Add(log);
                }
                if (result.Success)
                {
                    item.Status = "Sent";
                    item.SentAt = DateTime.UtcNow;
                    item.ErrorMessage = null;
                    sent++;
                }
                else
                {
                    DeferOrFail(item, result.ErrorMessage);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DeferOrFail(item, ex.Message);
                _logger.LogWarning(ex, "EmailQueue send failed Id={Id}", item.Id);
            }
            await _db.SaveChangesAsync(ct);
        }
        return sent;
    }

    /// <summary>
    /// รอบ 244 — ใบในคิวที่ต้องส่งแบบ e-Tax by Email (<see cref="DocumentEmailService.RouteForAsync"/> → <c>EtaxDeliveryRouter</c>) ส่งผ่าน
    /// <c>IDocumentEmailService.SendDocumentRoutedAsync</c> (ด่านผู้ส่ง/รูปแบบ/สถานะ + หัวเรื่องตามกรมสรรพากร + CC ETDA) ·
    /// คืน null = ใบนี้ไม่ใช่ทาง e-Tax (ผู้เรียกเดินเส้นเดิม) · true = ส่งแล้ว · false = ไม่ส่ง (คิว Failed พร้อมเหตุ / ผู้ให้บริการล้ม ⇒ ลองใหม่)
    /// <para>ด่านปฏิเสธ (ค่าตั้งผู้ส่ง · CC/BCC ของตาราง · ไม่แนบ PDF · ใบยกเลิก ฯลฯ) ลองใหม่ไม่ช่วย ⇒ Failed ทันที (กด "ลองใหม่" หลังแก้ได้) ·
    /// ข้อความในคิว: <c>{PayLink}</c> แทนด้วย URL ตอนส่ง (ไม่เก็บ token) · หัวเรื่องของตารางไม่ถูกใช้ (หัวเรื่องตามกรมสรรพากร)</para>
    /// </summary>
    internal async Task<bool?> SendViaEtaxIfRequiredAsync(EmailQueue item, CancellationToken ct)
    {
        var route = await DocumentEmailService.RouteForAsync(_db, item.CompanyId, item.EntityId, ct);
        if (route.Channel != EtaxEmailChannel.EtaxByEmail) return null;
        if (_docEmail == null)
        {
            item.Status = "Failed";
            item.ErrorMessage = "ใบนี้ต้องส่งแบบ e-Tax by Email แต่บริการส่ง e-Tax ไม่พร้อม — ยังไม่ส่ง (ไม่ส่งเป็นอีเมลธรรมดาแทน)";
            item.UpdatedAt = DateTime.UtcNow;
            return false;
        }
        string? payUrl = null;
        if (_payLinks != null && DocumentPayLinkPolicy.HasPlaceholder(item.Body))
            payUrl = await _payLinks.CurrentUrlAsync(item.CompanyId, item.EntityId, ct);
        var body = DocumentPayLinkPolicy.ResolvePlaceholder(item.Body, payUrl, html: true);
        try
        {
            var log = await _docEmail.SendDocumentRoutedAsync(item.CompanyId, item.EntityId,
                new Models.DTOs.Email.SendDocumentEmailRequest(item.ToEmail, item.CcEmail, item.BccEmail, null, body, AttachPdf: item.AttachPdf),
                "email-schedule");
            if (log.Status == EmailLogStatus.Sent)
            {
                item.Status = "Sent";
                item.SentAt = DateTime.UtcNow;
                item.ErrorMessage = null;
                item.UpdatedAt = DateTime.UtcNow;
                return true;
            }
            DeferOrFail(item, log.ErrorMessage);
            return false;
        }
        catch (BusinessRuleException ex) when (ex.RuleCode is { } rc && rc.StartsWith("ETAX-", StringComparison.Ordinal))
        {
            // แถว e-Tax ที่ถูก Add ค้างจากการออกที่ล้ม ห้ามค้างใน context ของรอบคิวนี้
            foreach (var e in _db.ChangeTracker.Entries<EtaxInvoice>().Where(e => e.State == EntityState.Added).ToList())
                e.State = EntityState.Detached;
            item.Status = "Failed";
            var reason = "ส่ง e-Tax by Email ไม่ได้ (ยังไม่ส่ง): " + ex.Message;
            item.ErrorMessage = reason.Length > 2000 ? reason[..2000] : reason;
            item.UpdatedAt = DateTime.UtcNow;
            return false;
        }
    }

    internal sealed record QueueCompose(EmailMessage? Message, IEmailSender? Sender, DocumentEmailLog? Log, string? Refusal);

    /// <summary>
    /// รอบ 241 ฝ่ายค้าน B2 — ประกอบอีเมลของคิวตามผู้ส่งจริง (ตัวตัดสินเดียว <see cref="DocumentEmailService.ResolveOnBehalfSenderAsync"/>):
    /// <list type="bullet">
    /// <item>SMTP ของบริษัท (ตั้ง+ทดสอบผ่าน) ⇒ เนื้อเดิมของคิว (พฤติกรรมเดิม) · ไม่ลงบันทึกเพิ่ม</item>
    /// <item>SMTP ของ NextAcc ในนามบริษัท ⇒ ผู้รับรวม To+Cc+Bcc ≤ 5 · เพดานบริษัท/วัน + ต่อผู้รับ (<see cref="PlatformEmailQuota"/>) ก่อนเรียกผู้ให้บริการ ·
    ///   เนื้อ = เทมเพลตคงที่ (<see cref="SignRequestEmailText.WrapPlatformDocumentEmail"/>) — เทมเพลตที่ผู้ใช้เขียนเอง = ข้อความล้วน (HTML ถูกหนี) ·
    ///   ค่าตั้งต้นของเซิร์ฟเวอร์ + บล็อกลิงก์ชำระคงเป็น HTML · From = แพลตฟอร์ม · Reply-To = บริษัท · คืนบันทึก (ผู้เรียกเติมผลแล้วเก็บ)</item>
    /// </list>
    /// ลิงก์ชำระ: คิวเก็บ <c>{PayLink}</c> · URL ประกอบตอนนี้จาก token ฉบับเข้ารหัส (ฝ่ายค้าน S1) ใส่เฉพาะข้อความที่ส่ง ไม่ถูกเก็บลงคิว/บันทึก
    /// </summary>
    internal async Task<QueueCompose> ComposeQueueMessageAsync(EmailQueue item, CancellationToken ct)
    {
        var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == item.CompanyId, ct);
        var (plan, sender) = await DocumentEmailService.ResolveOnBehalfSenderAsync(_db, _senderFactory, item.CompanyId, settings, ct);

        string? payUrl = null;
        if (item.EntityType == "Document" && _payLinks != null
            && (DocumentPayLinkPolicy.HasPlaceholder(item.Body) || DocumentPayLinkPolicy.HasPlaceholder(item.Subject)))
            payUrl = await _payLinks.CurrentUrlAsync(item.CompanyId, item.EntityId, ct);

        var subject = item.Subject;
        var body = item.Body;
        var msg = new EmailMessage { To = { item.ToEmail } };
        if (!string.IsNullOrWhiteSpace(item.CcEmail)) msg.Cc.Add(item.CcEmail);
        if (!string.IsNullOrWhiteSpace(item.BccEmail))
            foreach (var b in item.BccEmail.Split(',', StringSplitOptions.RemoveEmptyEntries))
                msg.Bcc.Add(b.Trim());

        DocumentEmailLog? log = null;
        if (plan.OnBehalf)
        {
            var (valid, invalid) = SignEmailThrottle.ParseRecipients(new[] { item.ToEmail, item.CcEmail, item.BccEmail });
            var refusal = invalid.Count > 0
                ? $"อีเมลผู้รับไม่ถูกต้อง: {string.Join(", ", invalid.Take(3))}"
                : SignEmailThrottle.RecipientCountRefusal(valid.Count);   // BCC นับรวม (ฝ่ายค้าน B2)
            if (refusal == null)
            {
                var quota = await PlatformEmailQuota.ForCompanyAsync(_db, _config, item.CompanyId, DateTime.UtcNow, ct);
                foreach (var r in valid)
                {
                    var (sameDoc, any) = await PlatformEmailQuota.RecipientCountsAsync(_db, item.CompanyId, item.EntityId, r, quota.DayStartUtc, ct);
                    refusal = SignEmailThrottle.Refusal(true, quota.SentToday, quota.Cap, sameDoc, any);
                    if (refusal != null) break;
                }
            }
            log = new DocumentEmailLog
            {
                CompanyId = item.CompanyId,
                DocumentId = item.EntityType == "Document" ? item.EntityId : null,
                ToEmail = item.ToEmail, CcEmail = item.CcEmail, BccEmail = item.BccEmail,
                AttachedPdf = item.AttachPdf, Provider = sender.Provider, SenderMode = plan.Mode,
                Purpose = "scheduled", CreatedBy = "email-schedule",
            };
            if (refusal != null)
            {
                log.Subject = SignRequestEmailText.SafeSubject(subject);
                log.Body = "";
                log.Status = EmailLogStatus.Failed;
                log.ErrorMessage = refusal;
                _db.DocumentEmailLogs.Add(log);
                return new QueueCompose(null, null, null, refusal);
            }

            // เทมเพลตของผู้ใช้ = ข้อความล้วน (ค่าที่แทนถูกหนีตอนเข้าคิวแล้ว ⇒ ถอดก่อน แล้วหนีทั้งก้อนครั้งเดียว) · บล็อกลิงก์ชำระของเซิร์ฟเวอร์แยกออกมาคงเป็น HTML
            var userAuthored = item.RuleId is Guid rid && await _db.EmailScheduleRules.AsNoTracking()
                .AnyAsync(r => r.Id == rid && r.CompanyId == item.CompanyId && r.BodyTemplate != null && r.BodyTemplate != "", ct);
            var (rest, block) = DocumentPayLinkPolicy.SplitBlock(body);
            var inner = (userAuthored ? SignRequestEmailText.PlainTextToHtml(System.Net.WebUtility.HtmlDecode(rest)) : rest) + (block ?? "");
            var isEn = false;
            var title = subject;
            string? number = null;
            if (item.EntityType == "Document")
            {
                var heading = await HeadingForAsync(item.CompanyId, item.EntityId);
                isEn = heading.IsEnglish;
                title = heading.Title;
                number = await _db.Documents.AsNoTracking().Where(d => d.Id == item.EntityId && d.CompanyId == item.CompanyId)
                    .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct);
            }
            var co = await _db.Companies.AsNoTracking().Where(c => c.Id == item.CompanyId).Select(c => new { c.Name, c.TaxId }).FirstOrDefaultAsync(ct);
            var origin = await SystemBaseUrl.ResolveAsync(_db, _config, ct);
            body = SignRequestEmailText.WrapPlatformDocumentEmail(isEn, co?.Name, co?.TaxId, plan.ReplyTo, title, number, inner,
                origin == null ? null : $"{origin}/contact.html?topic=email-abuse");
            subject = SignRequestEmailText.SafeSubject(subject);
            msg.FromAddress = plan.FromAddress;
            msg.FromName = plan.FromName;
            msg.ReplyTo = plan.ReplyTo;
            log.Subject = subject;
            log.Body = body;   // ยังเป็น {PayLink} — ไม่เก็บ token
        }
        msg.Subject = DocumentPayLinkPolicy.ResolvePlaceholder(subject, payUrl, html: false);
        msg.HtmlBody = DocumentPayLinkPolicy.ResolvePlaceholder(body, payUrl, html: true);
        return new QueueCompose(msg, sender, log, null);
    }

    /// <summary>ไฟล์แนบของคิว — <b>โยน</b>เมื่อสร้างไม่สำเร็จ (ผู้เรียกเลื่อนไปลองใหม่ ไม่ส่งอีเมลเปล่า · ID#12) ·
    /// คืน null เฉพาะชนิดที่ยังไม่มีตัวสร้างไฟล์ (สลิป/50 ทวิรายปี — ตั้งใจส่งเนื้ออีเมลอย่างเดียว)</summary>
    private async Task<EmailAttachment?> BuildAttachmentAsync(EmailQueue item, CancellationToken ct)
    {
        if (item.EntityType == "Document")
        {
            var resp = await _pdf.GenerateDocumentPdfAsync(item.CompanyId,
                new Models.DTOs.DocumentTemplate.GeneratePdfRequest(item.EntityId, null, null, null, null));
            if (resp?.PdfData is not { Length: > 0 })
                throw new InvalidOperationException("ไฟล์ PDF ที่สร้างได้ว่างเปล่า");
            return new EmailAttachment { FileName = resp.FileName, ContentType = "application/pdf", Content = resp.PdfData };
        }
        if (item.EntityType == "WithholdingTaxCert")
        {
            var resp = await _pdf.GenerateWithholdingTaxCertPdfAsync(item.CompanyId, item.EntityId);
            if (resp?.PdfData is not { Length: > 0 })
                throw new InvalidOperationException("ไฟล์ PDF ที่สร้างได้ว่างเปล่า");
            return new EmailAttachment { FileName = resp.FileName, ContentType = "application/pdf", Content = resp.PdfData };
        }
        // Payslip / annual WHT cert ผ่าน payroll service — ทำใน
        // path future expansion. ส่งไม่มีไฟล์แนบดีกว่า fail ทั้งฉบับ.
        return null;
    }

    private async Task<bool> EnqueueDocumentAsync(EmailScheduleRule rule, Document doc,
        int daysOffset, string suffix, CancellationToken ct, DateTime? overrideSendUtc = null)
    {
        var email = doc.Contact?.Email;
        if (string.IsNullOrWhiteSpace(email)) return false;

        var idem = $"doc:{doc.Id}:{rule.Trigger}:{suffix}";
        if (await _db.EmailQueues.AnyAsync(q => q.CompanyId == doc.CompanyId
            && q.IdempotencyKey == idem && !q.IsDeleted, ct)) return false;

        var sendUtc = overrideSendUtc ?? ScheduledUtc(BangkokNow().Date.AddDays(daysOffset), rule.SendAtHour);
        // ภาษา + หัวเอกสารของเนื้ออีเมล default = ตัวกลางตัวเดียวกับ PDF ที่แนบ (S-12 รอบ 193) — เดิมคำนวณ
        // `doc ?? company` เอง (ข้ามภาษาของเทมเพลต) และหัวเรื่องเขียนแค่ "เอกสาร {DocNumber}"
        // (rule ที่ผู้ใช้เขียน template เองไม่ถูกแตะ — ได้ placeholder {DocTitle} เพิ่มให้ใช้)
        // รอบ 201 ทีม PL (A-PL9 · P-3 N+1): ค่าระดับบริษัทโหลดครั้งเดียวต่อบริษัท · หัวของใบเดียวกันคำนวณครั้งเดียวต่อรอบ
        // (หลาย rule ของใบเดียว = เดิมคำนวณซ้ำทุก rule) — ตัวตัดสินหัวยังเป็น ResolveDocumentHeadingAsync ตัวเดียว
        var heading = await HeadingForAsync(doc.CompanyId, doc.Id);
        var isEn = heading.IsEnglish;
        var ctx = DocumentEmailContext(doc, heading, _headingCompanies[doc.CompanyId].Company);
        // รอบ 241: ลิงก์ชำระเงิน (ช่องทางตามตาราง ∩ ที่บริษัทเปิด) — หนึ่งลิงก์ต่อใบ · {PayLink} ในเทมเพลต หรือบล็อกท้ายเนื้อ
        var payLink = await PayLinkFor(rule, doc, ct);
        ctx["PayLink"] = payLink?.Url != null ? DocumentPayLinkPolicy.QueuePlaceholder : "";   // ฝ่ายค้าน S1: ที่หมาย ไม่ใช่ URL
        // ตารางแรกที่ส่งใบนี้ = ค่าทับของงานหลังรับชำระ (ผู้อ่าน: PostPaymentAutomationService.SettingsForAsync)
        if (rule.Id != Guid.Empty && doc.AutoSendRuleId == null)
            await _db.Documents.Where(d => d.Id == doc.Id && d.CompanyId == doc.CompanyId && d.AutoSendRuleId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.AutoSendRuleId, (Guid?)rule.Id), ct);

        _db.EmailQueues.Add(new EmailQueue
        {
            CompanyId = doc.CompanyId,
            // rule เสมือน (Id ว่าง = ไม่ได้บันทึกลง DB เช่น AutoSendEmail ของ
            // recurring) → RuleId ต้องเป็น null ไม่งั้น FK ชี้ไปแถวที่ไม่มีจริง
            RuleId = rule.Id == Guid.Empty ? null : rule.Id,
            EntityType = "Document",
            EntityId = doc.Id,
            ToEmail = email!,
            BccEmail = rule.BccEmails,
            Subject = Render(rule.SubjectTemplate ?? DefaultDocSubject(rule.Trigger, isEn), ctx),
            Body = WithPayLink(Render(rule.BodyTemplate ?? DefaultDocBody(rule.Trigger, isEn), ctx, htmlEncodeValues: true), rule.BodyTemplate, payLink, isEn),
            AttachPdf = true,
            ScheduledFor = sendUtc,
            IdempotencyKey = idem,
        });
        return true;
    }

    // แคชของรอบนี้ (service เป็น scoped — หนึ่งคำขอ/หนึ่งรอบของ worker) · คีย์บริษัท/เอกสาร ไม่ข้าม tenant
    private readonly Dictionary<Guid, PdfGenerationService.HeadingCompanyContext> _headingCompanies = new();
    private readonly Dictionary<Guid, Models.DTOs.DocumentTemplate.DocumentHeading> _headings = new();

    private async Task<Models.DTOs.DocumentTemplate.DocumentHeading> HeadingForAsync(Guid companyId, Guid documentId)
    {
        if (!_headingCompanies.TryGetValue(companyId, out var shared))
        {
            shared = await PdfGenerationService.LoadHeadingCompanyContextAsync(_db, companyId);
            _headingCompanies[companyId] = shared;
        }
        if (_headings.TryGetValue(documentId, out var cached)) return cached;
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, documentId, shared);
        _headings[documentId] = heading;
        return heading;
    }

    /// <summary>ค่าที่แทนลงหัวเรื่อง/เนื้ออีเมลของเอกสาร — ตัวเดียวของตอนเข้าคิวและตอนส่ง (ID#12: ยอดค้างชำระ ณ เวลาส่ง)</summary>
    private static Dictionary<string, string?> DocumentEmailContext(Document doc,
        Models.DTOs.DocumentTemplate.DocumentHeading heading, Company companyRow)
    {
        var company = (heading.IsEnglish && !string.IsNullOrWhiteSpace(companyRow.NameEn)
            ? companyRow.NameEn : companyRow.Name) ?? "";
        return new Dictionary<string, string?>
        {
            ["DocNumber"] = doc.DocumentNumber,
            ["DocTitle"] = heading.Title,
            ["ContactName"] = doc.Contact?.Name ?? "",
            ["DueDate"] = doc.DueDate?.ToString("dd/MM/yyyy") ?? "",
            ["Amount"] = doc.TotalAmount.ToString("N2"),
            ["BalanceDue"] = doc.BalanceDue.ToString("N2"),
            ["CompanyName"] = company,
        };
    }

    /// <summary>ID#12 (รอบ 2026-10-14 ทีม F4): ตรวจเอกสารซ้ำตอนส่ง — ยกเลิก/กลับเป็นร่าง/ลบ ⇒ ข้าม · ชำระครบ ⇒ ไม่ทวง ·
    /// ส่งได้ + มีกฎจริง ⇒ เขียนหัวเรื่อง/เนื้อใหม่ด้วยค่า ณ เวลาส่ง (ยอดค้างชำระปัจจุบัน ไม่ใช่ยอดตอนเข้าคิว) · คืนเหตุผลที่ข้าม หรือ null</summary>
    private async Task<string?> RecheckDocumentAtSendAsync(EmailQueue item, CancellationToken ct)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == item.EntityId && d.CompanyId == item.CompanyId && !d.IsDeleted, ct);
        var rule = item.RuleId is { } ruleId
            ? await _db.EmailScheduleRules.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == item.CompanyId && !r.IsDeleted, ct)
            : null;
        var trigger = rule?.Trigger ?? ScheduledEmailSendCheck.TriggerFromIdempotencyKey(item.IdempotencyKey);
        var skip = ScheduledEmailSendCheck.SkipReason(trigger, doc != null,
            doc?.Status ?? DocumentStatus.Draft, doc?.BalanceDue ?? 0m);
        if (skip != null || doc == null) return skip;
        if (rule != null)
        {
            await _db.HydrateContactAsync(item.CompanyId, doc);
            var heading = await HeadingForAsync(doc.CompanyId, doc.Id);
            var ctx = DocumentEmailContext(doc, heading, _headingCompanies[doc.CompanyId].Company);
            // รอบ 241: ลิงก์ ณ เวลาส่ง (ยอดสุทธิปัจจุบัน · ใบชำระครบ/ยกเลิกแล้ว = ไม่แนบ)
            var payLink = await PayLinkFor(rule, doc, ct);
            ctx["PayLink"] = payLink?.Url != null ? DocumentPayLinkPolicy.QueuePlaceholder : "";   // ฝ่ายค้าน S1: คิวเก็บที่หมาย
            item.Subject = Render(rule.SubjectTemplate ?? DefaultDocSubject(rule.Trigger, heading.IsEnglish), ctx);
            item.Body = WithPayLink(Render(rule.BodyTemplate ?? DefaultDocBody(rule.Trigger, heading.IsEnglish), ctx, htmlEncodeValues: true),
                rule.BodyTemplate, payLink, heading.IsEnglish);
        }
        return null;
    }

    /// <summary>50 ทวิ ตอนส่ง — ถูกลบ/ยกเลิกหลังเข้าคิว ⇒ ข้ามพร้อมเหตุผล (ตัวตัดสินเดียว ScheduledEmailSendCheck.WhtCertSkipReason)</summary>
    private async Task<string?> RecheckWhtCertAtSendAsync(EmailQueue item, CancellationToken ct)
    {
        var status = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(c => c.Id == item.EntityId && c.CompanyId == item.CompanyId && !c.IsDeleted)
            .Select(c => (Models.DTOs.Tax.WithholdingTaxCertStatus?)c.Status)
            .FirstOrDefaultAsync(ct);
        return ScheduledEmailSendCheck.WhtCertSkipReason(status != null,
            status == Models.DTOs.Tax.WithholdingTaxCertStatus.Voided);
    }

    /// <summary>ส่งไม่สำเร็จ ⇒ ลองใหม่ใน 15 นาที (ครบ 3 ครั้ง = Failed) พร้อมเหตุผลที่ผู้ใช้เห็นบนคิว</summary>
    private static void DeferOrFail(EmailQueue item, string? reason)
    {
        item.Status = item.RetryCount >= 3 ? "Failed" : "Pending";
        item.RetryCount++;
        item.ErrorMessage = reason;
        item.ScheduledFor = DateTime.UtcNow.AddMinutes(15);
    }

    private static string ResolveEmployeeEmail(Employee e) => e.Email ?? "";

    /// <summary>แทน placeholder · <paramref name="htmlEncodeValues"/> = true สำหรับ<b>เนื้อ</b>อีเมล (HTML) — ค่าที่แทนลงไป
    /// (ชื่อลูกค้า/พนักงาน/เลขเอกสาร/หัวเอกสาร) ผู้ใช้คุมได้ ต้องหนีก่อนต่อเข้า HTML (กฎ #4 C) · หัวเรื่องเป็นข้อความล้วน ไม่หนี</summary>
    internal static string Render(string template, Dictionary<string, string?> ctx, bool htmlEncodeValues = false)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var result = template;
        foreach (var (k, v) in ctx)
            result = result.Replace("{" + k + "}",
                htmlEncodeValues ? System.Net.WebUtility.HtmlEncode(v ?? "") : v ?? "");
        return result;
    }

    // default (ตอน rule ไม่มี template ของตัวเอง) เลือกภาษาตามใบ — เนื้ออีเมล
    // ไทยครอบไฟล์แนบอังกฤษ = ลูกค้าต่างชาติอ่านไม่ออกว่าโดนทวงอะไร
    // ชื่อชนิดเอกสารมาจาก {DocTitle} (= หัวที่ PDF แนบพิมพ์จริง) — ห้ามมีตารางชื่อชนิดเอกสารของตัวเอง (S-12)
    internal static string DefaultDocSubject(string trigger, bool isEn = false) => trigger switch
    {
        "DocumentDueSoon" => isEn ? "Payment due soon: {DocTitle} No. {DocNumber}" : "ครบกำหนดชำระเร็ว ๆ นี้: {DocTitle} เลขที่ {DocNumber}",
        "DocumentOverdue" => isEn ? "Payment overdue: {DocTitle} No. {DocNumber}" : "เกินกำหนดชำระ: {DocTitle} เลขที่ {DocNumber}",
        _ => isEn ? "{DocTitle} No. {DocNumber}" : "{DocTitle} เลขที่ {DocNumber}",
    };

    internal static string DefaultDocBody(string trigger, bool isEn = false)
    {
        var heading = trigger switch
        {
            "DocumentDueSoon" => isEn
                ? "Dear {ContactName},<br>{DocTitle} No. {DocNumber} is due on {DueDate}.<br>Outstanding balance: <strong>THB {BalanceDue}</strong>"
                : "เรียน คุณ{ContactName}<br>{DocTitle} เลขที่ {DocNumber} ใกล้ครบกำหนดชำระวันที่ {DueDate}<br>ยอดค้างชำระ <strong>{BalanceDue} บาท</strong>",
            "DocumentOverdue" => isEn
                ? "Dear {ContactName},<br>{DocTitle} No. {DocNumber} has been overdue since {DueDate}.<br>Outstanding balance: <strong>THB {BalanceDue}</strong>. Please arrange payment at your earliest convenience."
                : "เรียน คุณ{ContactName}<br>{DocTitle} เลขที่ {DocNumber} เกินกำหนดชำระตั้งแต่ {DueDate}<br>ยอดค้างชำระ <strong>{BalanceDue} บาท</strong> กรุณาดำเนินการชำระโดยเร็ว",
            _ => isEn
                ? "Dear {ContactName},<br>Please find attached {DocTitle} No. {DocNumber} for a total of THB {Amount}."
                : "เรียน คุณ{ContactName}<br>แนบ{DocTitle} เลขที่ {DocNumber} ยอดรวม {Amount} บาท",
        };
        var footer = isEn ? "Sent automatically by {CompanyName}" : "ส่งโดย {CompanyName} โดยอัตโนมัติ";
        return $@"<div style='font-family:sans-serif;max-width:600px'>
            <p>{heading}</p>
            <p style='color:#64748b;font-size:13px'>{footer}</p>
        </div>";
    }

    private static string DefaultPayslipBody() => @"<div style='font-family:sans-serif;max-width:600px'>
        <p>เรียน {EmployeeName}</p>
        <p>แนบสลิปเงินเดือนประจำงวด {Period} ยอดสุทธิ <strong>{NetPay} บาท</strong></p>
        <p style='color:#64748b;font-size:13px'>กรุณาเก็บไว้เป็นหลักฐาน</p>
    </div>";

    private static string DefaultWhtBody() => @"<div style='font-family:sans-serif;max-width:600px'>
        <p>เรียน {ContactName}</p>
        <p>แนบหนังสือรับรองหัก ณ ที่จ่ายเลขที่ {DocNumber} จำนวนภาษี <strong>{Amount} บาท</strong></p>
    </div>";

    // ── CRUD rules + queue management ───────────────────────────────

    public async Task<List<EmailScheduleRule>> GetRulesAsync(Guid companyId)
        => await _db.EmailScheduleRules.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted)
            .OrderBy(r => r.Trigger).ThenBy(r => r.OffsetDays).ToListAsync();

    public async Task<EmailScheduleRule> UpsertRuleAsync(Guid companyId, EmailScheduleRule rule, string actor)
    {
        // รอบ 241: เปิดแนบลิงก์/เลือกช่องทางได้เฉพาะที่บริษัทเปิดจริง (เซิร์ฟเวอร์ตัดสิน — ไม่เก็บค่าที่ใช้ไม่ได้เงียบ ๆ)
        if (_payLinks != null && (rule.AttachPayLink == true || rule.PayLinkChannelsJson != null))
        {
            var facts = await _payLinks.CompanyFactsAsync(companyId);
            if (rule.AttachPayLink == true && DocumentPayLinkPolicy.UnavailableReason(facts) is { } why)
                throw new BusinessRuleException(why, DocumentPayLinkPolicy.RuleCode);
            var bad = DocumentPayLinkPolicy.Unknown(DocumentPayLinkPolicy.CompanyAvailable(facts), DocumentPayLinkPolicy.ParseChannels(rule.PayLinkChannelsJson));
            if (bad.Count > 0)
                throw new BusinessRuleException("ช่องทางที่บริษัทยังไม่ได้เปิด: " + string.Join(", ", bad), DocumentPayLinkPolicy.RuleCode);
        }
        if (rule.Id == Guid.Empty)
        {
            rule.CompanyId = companyId;
            rule.CreatedByName = actor;
            _db.EmailScheduleRules.Add(rule);
        }
        else
        {
            var existing = await _db.EmailScheduleRules
                .FirstOrDefaultAsync(r => r.Id == rule.Id && r.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบกฎ");
            existing.Trigger = rule.Trigger;
            existing.IsActive = rule.IsActive;
            existing.DocumentType = rule.DocumentType;
            existing.OffsetDays = rule.OffsetDays;
            existing.SendAtHour = rule.SendAtHour;
            existing.RepeatEveryDays = rule.RepeatEveryDays;
            existing.SubjectTemplate = rule.SubjectTemplate;
            existing.BodyTemplate = rule.BodyTemplate;
            existing.BccEmails = rule.BccEmails;
            existing.DayOfMonth = rule.DayOfMonth;
            existing.AudienceFilter = rule.AudienceFilter;
            // รอบ 241: ค่าทับต่อตาราง (null = ตามบริษัท) — ไม่ลอกไม่ได้ (ห้าม silent no-op)
            existing.AttachPayLink = rule.AttachPayLink;
            existing.PayLinkChannelsJson = rule.PayLinkChannelsJson;
            existing.PostPayEmailReceipt = rule.PostPayEmailReceipt;
            existing.PostPayIncludePartial = rule.PostPayIncludePartial;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return rule;
    }

    public async Task DeleteRuleAsync(Guid companyId, Guid ruleId)
    {
        var rule = await _db.EmailScheduleRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกฎ");
        rule.IsDeleted = true;
        rule.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<(int Pending, int Sent, int Failed)> GetQueueStatsAsync(Guid companyId)
    {
        var stats = await _db.EmailQueues.AsNoTracking()
            .Where(q => q.CompanyId == companyId && !q.IsDeleted
                && q.CreatedAt > DateTime.UtcNow.AddDays(-30))
            .GroupBy(q => q.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();
        return (
            stats.FirstOrDefault(s => s.Status == "Pending")?.Count ?? 0,
            stats.FirstOrDefault(s => s.Status == "Sent")?.Count ?? 0,
            stats.FirstOrDefault(s => s.Status == "Failed")?.Count ?? 0);
    }

    public async Task<List<EmailQueue>> GetRecentQueueAsync(Guid companyId, int take = 50)
        => await _db.EmailQueues.AsNoTracking()
            .Where(q => q.CompanyId == companyId && !q.IsDeleted)
            .OrderByDescending(q => q.CreatedAt).Take(take).ToListAsync();

    public async Task RetryFailedAsync(Guid companyId, Guid queueId)
    {
        var q = await _db.EmailQueues
            .FirstOrDefaultAsync(x => x.Id == queueId && x.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบคิว");
        q.Status = "Pending";
        q.RetryCount = 0;
        q.ScheduledFor = DateTime.UtcNow;
        q.ErrorMessage = null;
        await _db.SaveChangesAsync();
    }

    public async Task CancelPendingAsync(Guid companyId, Guid queueId)
    {
        var q = await _db.EmailQueues
            .FirstOrDefaultAsync(x => x.Id == queueId && x.CompanyId == companyId && x.Status == "Pending")
            ?? throw new KeyNotFoundException("ไม่พบคิวที่ยกเลิกได้");
        q.Status = "Cancelled";
        await _db.SaveChangesAsync();
    }
}
