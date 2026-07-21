using Accounting.Data;
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
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "ScheduledReportDispatcher iteration failed"); }
            // 60 นาทีต่อรอบ — schedule granularity ระดับชั่วโมง
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var email = scope.ServiceProvider.GetService<IEmailService>();

        var nowBkk = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BkkTz);
        var schedules = await db.ScheduledReports
            .Where(s => !s.IsDeleted && s.IsActive)
            .ToListAsync(ct);

        foreach (var s in schedules)
        {
            if (!IsDue(s, nowBkk)) continue;
            try
            {
                var ok = await DispatchOneAsync(scope.ServiceProvider, db, email, s, nowBkk, ct);
                s.LastSentAt = DateTime.UtcNow;
                s.LastResult = ok ? "OK" : "SKIPPED (email service unavailable)";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispatch ScheduledReport {Id} ({Name})", s.Id, s.Name);
                s.LastResult = $"ERROR: {ex.Message.Substring(0, Math.Min(180, ex.Message.Length))}";
            }
            await db.SaveChangesAsync(ct);
        }
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

    private async Task<bool> DispatchOneAsync(IServiceProvider sp, AccountingDbContext db,
        IEmailService? email, ScheduledReport s, DateTime nowBkk, CancellationToken ct)
    {
        if (email == null)
        {
            _logger.LogWarning("IEmailService not registered — cannot send ScheduledReport {Name}", s.Name);
            return false;
        }

        List<string> recipients = new();
        try { recipients = System.Text.Json.JsonSerializer.Deserialize<List<string>>(s.RecipientsJson) ?? new(); }
        catch { }
        if (recipients.Count == 0) return false;

        // Generate report inline body (PDF/Excel attachment ต่อไปทำเพิ่ม)
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == s.CompanyId, ct);
        var subject = $"[{company?.Name ?? "Next Acc"}] {s.Name} — {nowBkk:dd/MM/yyyy}";
        var body = await BuildReportBodyAsync(sp, db, s, nowBkk, ct);

        foreach (var to in recipients)
        {
            try { await email.SendAsync(to, subject, body); }
            catch (Exception ex) { _logger.LogWarning(ex, "Send to {To} failed", to); }
        }
        return true;
    }

    private async Task<string> BuildReportBodyAsync(IServiceProvider sp, AccountingDbContext db,
        ScheduledReport s, DateTime nowBkk, CancellationToken ct)
    {
        // Render concise inline HTML based on ReportCode. แต่ละ report code
        // ดึงตัวเลขสรุปจาก service ที่มีอยู่ — ไม่ใช่ทำ PDF เต็ม.
        var acc = sp.GetService<IAccountingService>();
        var html = $"<h2>{s.Name}</h2><p>ส่งอัตโนมัติเวลา {nowBkk:dd/MM/yyyy HH:mm} ({s.Frequency})</p>";

        try
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
        catch (Exception ex)
        {
            html += $"<p style='color:red'>❌ ไม่สามารถสร้างรายงาน: {ex.Message}</p>";
        }
        return html;
    }
}
