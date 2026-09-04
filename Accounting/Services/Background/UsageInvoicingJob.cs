using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// **ปิดรอบบิลค่าใช้งาน** — รวม `UsageEvent` ที่ยังไม่ออกบิลของงวดที่ปิดแล้ว
/// เป็น **ใบแจ้งหนี้ใบเดียวหลายบรรทัด** ต่อบริษัท (LODGING_LICENSING_PLAN §6, เฟส 4)
///
/// <para>ทำไมต้องมี: ก่อนหน้านี้ `UsageEvent` ถูกเขียนครบ มี `BilledPeriod`/
/// `BilledDocumentId` เตรียมไว้ตั้งแต่ต้น — แต่**ไม่มีใครเขียนสองช่องนั้นเลย**
/// ⇒ เงินที่คิดได้ค้างอยู่ในตารางตลอดกาล ไม่เคยกลายเป็นใบแจ้งหนี้ (defect class
/// "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" ที่ CLAUDE.md บอกว่าใหญ่ที่สุดในเรพนี้)</para>
///
/// <para><b>Prepaid ไม่ออกใบแจ้งหนี้</b> — เครดิตถูกตัดไปแล้วตอนบันทึกการใช้งาน
/// (`UsageMeteringService.RecordAsync`) ออกบิลอีกใบ = เก็บเงินสองรอบ. แต่ยัง
/// **ต้องตีตรา `BilledPeriod`** ไว้ ไม่งั้นงานนี้จะวนอ่านแถวเดิมทุกรอบตลอดไป
/// (BilledDocumentId = null แปลว่า "ปิดรอบแล้วโดยไม่มีใบ" ซึ่งเป็นความจริง
/// ไม่ใช่การซ่อนข้อมูล)</para>
/// </summary>
public class UsageInvoicingJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UsageInvoicingJob> _logger;
    private static readonly TimeSpan Cycle = TimeSpan.FromHours(6);

    /// <summary>ยอดต่ำกว่านี้ยกยอดไปงวดหน้า — ค่าธรรมเนียมออกบิล/ตามเก็บแพงกว่า
    /// ตัวเงิน และลูกค้าไม่อยากได้ใบกำกับ ฿3.50 (ไม่ตีตรา = รวมกับงวดถัดไป)</summary>
    private const decimal MinInvoiceAmount = 50m;

    public UsageInvoicingJob(IServiceScopeFactory scopeFactory, ILogger<UsageInvoicingJob> logger)
    { _scopeFactory = scopeFactory; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnce(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "UsageInvoicingJob cycle failed"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var issuer = scope.ServiceProvider.GetService<IPlatformBillingDocumentIssuer>();
        var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
        var startedAt = DateTime.UtcNow;

        if (issuer == null || !await issuer.IsEnabledAsync())
        {
            // ยังไม่ได้ตั้ง tenant ผู้ให้บริการ = ยังออกเอกสารจริงไม่ได้ — **ห้ามตีตรา
            // BilledPeriod ทิ้ง** ไม่งั้นรายได้หายถาวรเมื่อตั้งค่าเสร็จทีหลัง
            if (recorder != null)
                await recorder.RecordAsync("UsageInvoicing", true, "ยังไม่ได้ตั้ง tenant ผู้ให้บริการ — ข้ามรอบนี้", 0, startedAt);
            return;
        }

        // ปิดบิลเฉพาะ "งวดที่จบแล้ว" — ต้นเดือนนี้ตามเวลาไทย แปลงกลับเป็น UTC
        var th = DateTime.UtcNow.AddHours(7);
        var thisMonthStartUtc = new DateTime(th.Year, th.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(-7);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // คีย์ระดับระบบ (ไม่ผูกบริษัท) — กันหลาย instance ปิดรอบเดียวกันพร้อมกัน
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(AdvisoryLockKey.UsageInvoicing, AddOnBilling.PeriodOf(DateTime.UtcNow)) }, ct);

        var pending = await db.UsageEvents
            .Where(u => u.BilledPeriod == null && !u.IsDeleted && !u.IsSandbox
                     && u.OccurredAt < thisMonthStartUtc)
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            await tx.RollbackAsync(ct);
            if (recorder != null)
                await recorder.RecordAsync("UsageInvoicing", true, "ไม่มีรายการค้างออกบิล", 0, startedAt);
            return;
        }

        // ชื่อฟีเจอร์สำหรับแสดงบนใบ — รหัสดิบ ("lodging.stay") ไม่ใช่ภาษาที่ลูกค้าอ่านออก
        var names = await db.ApiFeatures.AsNoTracking()
            .ToDictionaryAsync(f => f.FeatureCode, f => new { f.Name, f.UnitLabel }, ct);

        var prepaidAccounts = await db.BillingAccounts.AsNoTracking()
            .Where(a => a.PaymentModel == PaymentModel.Prepaid)
            .Select(a => a.Id).ToListAsync(ct);
        var prepaid = prepaidAccounts.ToHashSet();

        var issued = 0; var closedPrepaid = 0; var carried = 0;

        foreach (var group in pending.GroupBy(u => new { u.CompanyId, Period = AddOnBilling.PeriodOf(u.OccurredAt) }))
        {
            if (ct.IsCancellationRequested) break;
            var rows = group.ToList();
            var period = group.Key.Period;
            var accountId = rows.Select(r => r.BillingAccountId).FirstOrDefault(x => x.HasValue);

            // Prepaid: ตัดเครดิตไปแล้ว — ปิดรอบโดยไม่ออกใบ (แต่ต้องตีตรา)
            if (accountId.HasValue && prepaid.Contains(accountId.Value))
            {
                foreach (var r in rows) r.BilledPeriod = period;
                closedPrepaid += rows.Count;
                continue;
            }

            var total = rows.Sum(r => r.ChargedAmount);
            if (total < MinInvoiceAmount)
            {
                // ไม่ตีตรา — ยกยอดไปรวมกับงวดถัดไป (ไม่ใช่การยกเลิกหนี้)
                carried += rows.Count;
                continue;
            }

            var lines = rows
                .GroupBy(r => r.FeatureCode)
                .Select(g =>
                {
                    names.TryGetValue(g.Key, out var meta);
                    var qty = g.Sum(x => x.Quantity);
                    return new PlatformInvoiceLine(
                        Description: $"{meta?.Name ?? g.Key} · งวด {period}",
                        AmountNet: g.Sum(x => x.ChargedAmount),
                        Unit: meta?.UnitLabel ?? "รายการ",
                        Quantity: qty > 0 ? qty : 1m);
                })
                .OrderByDescending(l => l.AmountNet)
                .ToList();

            var issueDate = DateTime.UtcNow;
            // อ้างอิงต้องคงที่ต่อ (บริษัท, งวด) — ถ้ารอบก่อนออกใบไปแล้วแต่ตีตราไม่สำเร็จ
            // จะเห็นได้ทันทีว่าเป็นใบซ้ำของงวดเดียวกัน (8 ตัวแรกของ id พอแยกได้)
            var shortId = group.Key.CompanyId.ToString("N")[..8];
            var doc = await issuer.IssueUsageInvoiceAsync(
                buyerCompanyId: group.Key.CompanyId,
                lines: lines,
                issueDate: issueDate,
                dueDate: issueDate.AddDays(7),
                reference: $"USAGE-{period}-{shortId}",
                notes: $"ค่าใช้งานตามจริงงวด {period} — รายละเอียดแยกตามรายการด้านล่าง");

            // ออกใบไม่สำเร็จ = **ห้ามตีตรา** ปล่อยให้รอบหน้าลองใหม่
            // (ตีตราแล้วรายได้หายถาวรโดยไม่มีใครรู้ — เงียบที่สุดในบรรดาบั๊กเรื่องเงิน)
            if (doc == null)
            {
                _logger.LogWarning("ออกใบแจ้งหนี้ค่าใช้งานงวด {Period} ของบริษัท {Cid} ไม่สำเร็จ — จะลองใหม่รอบหน้า",
                    period, group.Key.CompanyId);
                continue;
            }

            foreach (var r in rows) { r.BilledPeriod = period; r.BilledDocumentId = doc.DocumentId; }
            issued++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var summary = $"ออกใบแจ้งหนี้ {issued} ใบ · ปิดรอบ prepaid {closedPrepaid} รายการ · ยกยอดต่ำกว่าเกณฑ์ {carried} รายการ";
        if (issued > 0 || closedPrepaid > 0) _logger.LogInformation("ปิดรอบบิลค่าใช้งาน: {Summary}", summary);
        if (recorder != null)
            await recorder.RecordAsync("UsageInvoicing", true, summary, issued, startedAt);
    }
}
