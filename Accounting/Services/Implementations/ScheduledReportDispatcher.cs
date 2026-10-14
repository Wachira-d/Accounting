using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// ScheduledReportDispatcher — รัน hourly บน Bangkok TZ. ดึง
/// ScheduledReports ที่ถึงเวลาส่ง (matching Frequency + DayOfWeek/Month
/// + HourBangkok) → render report → email recipients → update LastSentAt.
///
/// ทำเป็น idempotent: คำนวณ "next scheduled" จาก LastSentAt + Frequency
/// → ถ้า now ≥ next → ส่ง. คือถ้า service down ไป 2 ชม. → recover ตรง
/// 2 ครั้ง pending ก็ส่งได้ (limit รัน 1 schedule ต่อรอบ ใน hour เดียวกัน)
///
/// Sleep 60 min ระหว่าง iterations. Cancellation ใน RequestStop.
/// </summary>
public class ScheduledReportDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ScheduledReportDispatcher> _logger;
    private static readonly TimeZoneInfo BkkTz = ResolveBkkTz();

    public ScheduledReportDispatcher(IServiceScopeFactory scopes, ILogger<ScheduledReportDispatcher> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    private static TimeZoneInfo ResolveBkkTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Bangkok"); }
        catch { return TimeZoneInfo.CreateCustomTimeZone("BKK", TimeSpan.FromHours(7), "Bangkok", "Bangkok"); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay ให้แอป boot เสร็จ + ไม่กระทบ startup
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "ScheduledReportDispatcher iteration failed"); }
            // 60 นาทีต่อรอบ — schedule granularity ระดับชั่วโมง
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>กันสอง instance ส่งรายงานรอบเดียวกันพร้อมกัน (ผลตรวจทีม G · G-01)
    ///
    /// <para>⚠️ <c>IsDue</c> อ่าน <c>LastSentAt</c> แล้ว <b>ส่งอีเมลก่อน</b> ค่อย
    /// เขียนกลับ ⇒ ทุก instance ตื่นพร้อมกัน (หน่วงเริ่มงาน 2 นาทีเท่ากันทุกเครื่อง
    /// หลัง deploy) จึงอ่านเห็นค่าเก่าเหมือนกันหมด ⇒ ลูกค้าได้รายงาน P&amp;L /
    /// AR aging <b>N ฉบับตามจำนวนเครื่อง</b> ทุกงวด</para>
    ///
    /// <para>ใช้ <b>try</b> ไม่ใช่ wait — งานตามตารางที่อีกเครื่องทำอยู่ การรอคือ
    /// ทำงานเดิมซ้ำเปล่า ๆ</para></summary>
    private async Task RunGuardedAsync(CancellationToken ct)
    {
        using var lockScope = _scopes.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await Accounting.Helpers.JobLock.RunExclusiveAsync(
            lockDb, Accounting.Helpers.AdvisoryLockKey.BackgroundJob,
            nameof(ScheduledReportDispatcher), () => RunOnceAsync(ct), _logger, ct: ct);
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        var nowBkk = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BkkTz);
        var schedules = await db.ScheduledReports
            .Where(s => !s.IsDeleted && s.IsActive)
            .ToListAsync(ct);

        foreach (var s in schedules)
        {
            if (!IsDue(s, nowBkk)) continue;
            try
            {
                await DispatchAndRecordAsync(scope.ServiceProvider, db, s, _logger, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to dispatch ScheduledReport {Id} ({Name})", s.Id, s.Name);
                s.LastResult = $"ERROR · ส่งไม่สำเร็จ: {ex.Message.Substring(0, Math.Min(180, ex.Message.Length))} — ตรวจการตั้งค่ารายงานนี้แล้วกด “ส่งทันที”";
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>ส่งรายงาน 1 รายการ<b>จริง</b> แล้วบันทึกผลลง <c>LastResult</c>/<c>LastSentAt</c> (ผู้เรียก SaveChanges) — ใช้ทั้งรอบตามเวลาและปุ่ม "ส่งทันที"
    /// · รอบ 2026-10-14 ทีม F4 (ID#25): ผลต่อผู้รับมาจากผลจริงของช่องส่งอีเมล (สำเร็จ/ล้มรายคน) ⇒ ล้มบางราย = PARTIAL · ทุกราย = ERROR ·
    /// ประทับ LastSentAt เฉพาะเมื่อมีผู้รับได้รับอย่างน้อย 1 ราย (ตัวตัดสินเดียว Helpers/ScheduledReportOutcome)</summary>
    public static async Task<ScheduledReportOutcome.Result> DispatchAndRecordAsync(IServiceProvider sp, AccountingDbContext db,
        ScheduledReport s, ILogger logger, CancellationToken ct)
    {
        var nowBkk = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BkkTz);
        var outcome = await DispatchOneAsync(sp, db, s, nowBkk, logger, ct);
        s.LastResult = outcome.LastResult;
        if (outcome.AnyDelivered) s.LastSentAt = DateTime.UtcNow;
        s.UpdatedAt = DateTime.UtcNow;
        return outcome;
    }

    /// <summary>Check schedule due NOW. Match by HourBangkok (current hour)
    /// + Frequency:
    ///   Daily — ทุกวัน, hour เดียวกัน
    ///   Weekly — DayOfWeek match
    ///   Monthly — DayOfMonth match
    ///   Quarterly — DayOfMonth match + month ใน {3, 6, 9, 12} (ส่งสิ้นไตรมาส)
    /// + dedup: LastSentAt ถ้าอยู่ใน 23 ชม.ที่ผ่านมา → skip (ห้ามส่งซ้ำ
    /// hour เดียวกันจาก crash-recover loop).</summary>
    private static bool IsDue(ScheduledReport s, DateTime nowBkk)
    {
        if (nowBkk.Hour != s.HourBangkok) return false;
        if (s.LastSentAt.HasValue && (DateTime.UtcNow - s.LastSentAt.Value).TotalHours < 23) return false;
        return s.Frequency switch
        {
            "Daily" => true,
            "Weekly" => s.DayOfWeek.HasValue && (int)nowBkk.DayOfWeek == s.DayOfWeek.Value,
            "Monthly" => s.DayOfMonth.HasValue && nowBkk.Day == s.DayOfMonth.Value,
            "Quarterly" => s.DayOfMonth.HasValue && nowBkk.Day == s.DayOfMonth.Value
                && (nowBkk.Month == 3 || nowBkk.Month == 6 || nowBkk.Month == 9 || nowBkk.Month == 12),
            _ => false,
        };
    }

    private static async Task<ScheduledReportOutcome.Result> DispatchOneAsync(IServiceProvider sp, AccountingDbContext db,
        ScheduledReport s, DateTime nowBkk, ILogger logger, CancellationToken ct)
    {
        List<string> recipients;
        try { recipients = System.Text.Json.JsonSerializer.Deserialize<List<string>>(s.RecipientsJson) ?? new(); }
        catch (System.Text.Json.JsonException)
        {
            return ScheduledReportOutcome.Summarize(0, Array.Empty<(string, string?)>(),
                "รายชื่อผู้รับอ่านไม่ได้ — เปิดแก้รายงานนี้แล้วบันทึกรายชื่ออีเมลผู้รับใหม่");
        }
        recipients = recipients.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct().ToList();
        if (recipients.Count == 0)
            return ScheduledReportOutcome.Summarize(0, Array.Empty<(string, string?)>(),
                "ยังไม่มีอีเมลผู้รับ — เปิดแก้รายงานนี้แล้วเพิ่มอีเมลผู้รับ");

        // ช่องส่งอีเมลของบริษัท (ตั้งค่าเองหรือ SMTP กลาง) — คืนผลสำเร็จ/ล้มรายคน (เดิมใช้ IEmailService.SendAsync ที่กลืน error และ
        // "ข้ามเงียบ" เมื่อไม่ได้ตั้ง SMTP ⇒ ตัวส่งไม่มีทางรู้ว่าส่งไม่ถึง)
        var senders = sp.GetService<IEmailSenderFactory>();
        if (senders == null)
        {
            logger.LogWarning("IEmailSenderFactory not registered — cannot send ScheduledReport {Name}", s.Name);
            return ScheduledReportOutcome.Summarize(recipients.Count, Array.Empty<(string, string?)>(),
                "ระบบยังไม่มีช่องส่งอีเมล — แจ้งผู้ดูแลระบบให้ตั้งค่าอีเมล");
        }
        var sender = await senders.GetSenderAsync(s.CompanyId, ct);

        // Generate report inline body (PDF/Excel attachment ต่อไปทำเพิ่ม)
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == s.CompanyId, ct);
        var subject = $"[{company?.Name ?? "Next Acc"}] {s.Name} — {nowBkk:dd/MM/yyyy}";
        string body;
        try { body = await BuildReportBodyAsync(sp, db, s, nowBkk, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Build ScheduledReport {Name} body failed", s.Name);
            return ScheduledReportOutcome.Summarize(recipients.Count, Array.Empty<(string, string?)>(),
                $"สร้างตัวเลขรายงานไม่สำเร็จ ({ex.Message}) — เปิดรายงานนี้บนหน้าจอเพื่อดูสาเหตุ แล้วกด “ส่งทันที” อีกครั้ง");
        }

        var failures = new List<(string To, string? Error)>();
        foreach (var to in recipients)
        {
            try
            {
                var result = await sender.SendAsync(new EmailMessage { To = { to }, Subject = subject, HtmlBody = body }, ct);
                if (!result.Success) failures.Add((to, result.ErrorMessage));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Send ScheduledReport {Name} to {To} failed", s.Name, to);
                failures.Add((to, ex.Message));
            }
        }
        return ScheduledReportOutcome.Summarize(recipients.Count, failures, null);
    }

    private static async Task<string> BuildReportBodyAsync(IServiceProvider sp, AccountingDbContext db,
        ScheduledReport s, DateTime nowBkk, CancellationToken ct)
    {
        // Render concise inline HTML based on ReportCode. แต่ละ report code
        // ดึงตัวเลขสรุปจาก service ที่มีอยู่ — ไม่ใช่ทำ PDF เต็ม.
        var acc = sp.GetService<IAccountingService>();
        var html = $"<h2>{System.Net.WebUtility.HtmlEncode(s.Name)}</h2><p>ส่งอัตโนมัติเวลา {nowBkk:dd/MM/yyyy HH:mm} ({System.Net.WebUtility.HtmlEncode(s.Frequency)})</p>";  // กฎ #4 C: ชื่อที่ผู้ใช้ตั้งต้องหนีก่อนต่อ HTML

        // ID#25: สร้างตัวเลขรายงานไม่สำเร็จ ⇒ โยนให้ผู้เรียกบันทึกเป็น "ไม่ได้ส่ง" (เดิมส่งอีเมลที่มีแต่ข้อความ error ถึงผู้รับ แล้วผลยังเป็น OK)
        {
            switch (s.ReportCode)
            {
                case "PNL":
                    if (acc != null)
                    {
                        var from = new DateTime(nowBkk.Year, nowBkk.Month, 1).AddMonths(-1);
                        var to = nowBkk.Date;
                        var totals = await acc.GetSnapshotTotalsAsync(s.CompanyId, from, to);
                        html += $"<table border='1' cellpadding='8' style='border-collapse:collapse'>" +
                                $"<tr><th>หมวด</th><th>ยอด (บาท)</th></tr>" +
                                $"<tr><td>รายได้</td><td>{totals.Revenue:N2}</td></tr>" +
                                $"<tr><td>ค่าใช้จ่าย</td><td>{totals.Expense:N2}</td></tr>" +
                                $"<tr><td><b>กำไรสุทธิ</b></td><td><b>{totals.Revenue - totals.Expense:N2}</b></td></tr>" +
                                $"</table><p>ระหว่าง {from:yyyy-MM-dd} → {to:yyyy-MM-dd}</p>";
                    }
                    break;
                case "AR_AGING":
                case "AP_AGING":
                    html += "<p>เปิดดู aging ใน Next Acc dashboard เพื่อรายละเอียดครบ.</p>";
                    break;
                default:
                    html += "<p>ดูรายงานเต็มใน Next Acc dashboard.</p>";
                    break;
            }
        }
        return html;
    }
}
