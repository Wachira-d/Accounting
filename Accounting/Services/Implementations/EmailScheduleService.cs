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

    public EmailScheduleService(AccountingDbContext db, IEmailSenderFactory senderFactory,
        IPdfGenerationService pdf, ILogger<EmailScheduleService> logger)
    {
        _db = db;
        _senderFactory = senderFactory;
        _pdf = pdf;
        _logger = logger;
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
                        Body = Render(rule.BodyTemplate ?? DefaultPayslipBody(), ctx),
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
                    Body = Render(rule.BodyTemplate ?? DefaultWhtBody(), ctx),
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
            // recurringId เข้า suffix → idempotency: ทุก recurring run ไม่ทับกัน
            foreach (var rule in rules)
                await EnqueueDocumentAsync(rule, doc, daysOffset: rule.OffsetDays,
                    suffix: $"recurring-{recurringId}", ct);
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
                    ? Render(rule.BodyTemplate, ctx)
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

        var batch = await _db.EmailQueues
            .Where(q => claimedIds.Contains(q.Id))
            .ToListAsync(ct);

        int sent = 0;
        foreach (var item in batch)
        {
            try
            {
                var sender = await _senderFactory.GetSenderAsync(item.CompanyId, ct);
                var msg = new EmailMessage
                {
                    To = { item.ToEmail },
                    Subject = item.Subject,
                    HtmlBody = item.Body,
                };
                if (!string.IsNullOrWhiteSpace(item.CcEmail)) msg.Cc.Add(item.CcEmail);
                if (!string.IsNullOrWhiteSpace(item.BccEmail))
                    foreach (var b in item.BccEmail.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        msg.Bcc.Add(b.Trim());

                if (item.AttachPdf)
                {
                    var att = await BuildAttachmentAsync(item, ct);
                    if (att != null) msg.Attachments.Add(att);
                }

                var result = await sender.SendAsync(msg, ct);
                if (result.Success)
                {
                    item.Status = "Sent";
                    item.SentAt = DateTime.UtcNow;
                    item.ErrorMessage = null;
                    sent++;
                }
                else
                {
                    item.Status = item.RetryCount >= 3 ? "Failed" : "Pending";
                    item.RetryCount++;
                    item.ErrorMessage = result.ErrorMessage;
                    item.ScheduledFor = DateTime.UtcNow.AddMinutes(15);
                }
            }
            catch (Exception ex)
            {
                item.Status = item.RetryCount >= 3 ? "Failed" : "Pending";
                item.RetryCount++;
                item.ErrorMessage = ex.Message;
                item.ScheduledFor = DateTime.UtcNow.AddMinutes(15);
                _logger.LogWarning(ex, "EmailQueue send failed Id={Id}", item.Id);
            }
            await _db.SaveChangesAsync(ct);
        }
        return sent;
    }

    private async Task<EmailAttachment?> BuildAttachmentAsync(EmailQueue item, CancellationToken ct)
    {
        try
        {
            if (item.EntityType == "Document")
            {
                var resp = await _pdf.GenerateDocumentPdfAsync(item.CompanyId,
                    new Models.DTOs.DocumentTemplate.GeneratePdfRequest(item.EntityId, null, null, null, null));
                return new EmailAttachment { FileName = resp.FileName, ContentType = "application/pdf", Content = resp.PdfData };
            }
            if (item.EntityType == "WithholdingTaxCert")
            {
                var resp = await _pdf.GenerateWithholdingTaxCertPdfAsync(item.CompanyId, item.EntityId);
                return new EmailAttachment { FileName = resp.FileName, ContentType = "application/pdf", Content = resp.PdfData };
            }
            // Payslip / annual WHT cert ผ่าน payroll service — ทำใน
            // path future expansion. ส่งไม่มีไฟล์แนบดีกว่า fail ทั้งฉบับ.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Build attachment failed Item={Id}", item.Id);
            return null;
        }
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
        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == doc.CompanyId)
            .Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var ctx = new Dictionary<string, string?>
        {
            ["DocNumber"] = doc.DocumentNumber,
            ["ContactName"] = doc.Contact?.Name ?? "",
            ["DueDate"] = doc.DueDate?.ToString("dd/MM/yyyy") ?? "",
            ["Amount"] = doc.TotalAmount.ToString("N2"),
            ["BalanceDue"] = doc.BalanceDue.ToString("N2"),
            ["CompanyName"] = company,
        };

        _db.EmailQueues.Add(new EmailQueue
        {
            CompanyId = doc.CompanyId,
            RuleId = rule.Id,
            EntityType = "Document",
            EntityId = doc.Id,
            ToEmail = email!,
            BccEmail = rule.BccEmails,
            Subject = Render(rule.SubjectTemplate ?? DefaultDocSubject(rule.Trigger), ctx),
            Body = Render(rule.BodyTemplate ?? DefaultDocBody(rule.Trigger), ctx),
            AttachPdf = true,
            ScheduledFor = sendUtc,
            IdempotencyKey = idem,
        });
        return true;
    }

    private static string ResolveEmployeeEmail(Employee e) => e.Email ?? "";

    private static string Render(string template, Dictionary<string, string?> ctx)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var result = template;
        foreach (var (k, v) in ctx) result = result.Replace("{" + k + "}", v ?? "");
        return result;
    }

    private static string DefaultDocSubject(string trigger) => trigger switch
    {
        "DocumentDueSoon" => "ครบกำหนดชำระเร็ว ๆ นี้: {DocNumber}",
        "DocumentOverdue" => "เกินกำหนดชำระ: {DocNumber}",
        _ => "เอกสาร {DocNumber}",
    };

    private static string DefaultDocBody(string trigger)
    {
        var heading = trigger switch
        {
            "DocumentDueSoon" => "เรียน คุณ{ContactName}<br>เอกสารเลขที่ {DocNumber} ใกล้ครบกำหนดชำระวันที่ {DueDate}<br>ยอดค้างชำระ <strong>{BalanceDue} บาท</strong>",
            "DocumentOverdue" => "เรียน คุณ{ContactName}<br>เอกสารเลขที่ {DocNumber} เกินกำหนดชำระตั้งแต่ {DueDate}<br>ยอดค้างชำระ <strong>{BalanceDue} บาท</strong> กรุณาดำเนินการชำระโดยเร็ว",
            _ => "เรียน คุณ{ContactName}<br>แนบเอกสารเลขที่ {DocNumber} ยอดรวม {Amount} บาท",
        };
        return $@"<div style='font-family:sans-serif;max-width:600px'>
            <p>{heading}</p>
            <p style='color:#64748b;font-size:13px'>ส่งโดย {{CompanyName}} โดยอัตโนมัติ</p>
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
