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
    /// ตัวเงิน และลูกค้าไม่อยากได้ใบกำกับ ฿3.50 (ไม่ตีตรา = รวมกับงวดถัดไป) ·
    /// รอบ 231: ย้ายเป็นค่าตั้ง <c>PlatformBillingSettings.OverageMinInvoiceAmount</c> (ตั้งต้น 50) — ค่านี้เป็นแค่ fallback เมื่ออ่านค่าตั้งไม่ได้</summary>
    private const decimal MinInvoiceAmount = Accounting.Helpers.PlatformBillingSettingsRules.DefaultOverageMinInvoiceAmount;

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
        var issuedDocs = new List<(Guid Buyer, Guid DocumentId)>();
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

        // รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §B.5 · คำตัดสินข้อ 231-4): ขั้นต่ำ + กำหนดชำระ (วันออก + OverageGraceDays) จากค่าตั้งของแอดมิน
        var billingPolicy = await PlatformBillingSettingsService.LoadPolicyAsync(db, ct);
        var minInvoice = Accounting.Helpers.UsageInvoiceLines.MinInvoice(billingPolicy.OverageMinInvoiceAmount, MinInvoiceAmount);

        var issued = 0; var closedPrepaid = 0; var carried = 0; var closedZero = 0;

        // ฝ่ายค้านรอบ 231 B3: จัดกลุ่มตาม "บริษัท" (ทุกงวดที่ปิดแล้ว) — งวดที่ยกยอดมาเพราะต่ำกว่าขั้นต่ำถูกรวมเข้าใบเดียวกับงวดถัดไป (คนละบรรทัด) ·
        // เดิมจัดกลุ่มต่อ (บริษัท, งวด) ⇒ งวดละ 30 บาทไม่เคยถึงขั้นต่ำ 50 สักใบ = ยกยอดตลอดไป
        foreach (var group in pending.GroupBy(u => u.CompanyId))
        {
            if (ct.IsCancellationRequested) break;
            var rows = group.ToList();
            var accountId = rows.Select(r => r.BillingAccountId).FirstOrDefault(x => x.HasValue);
            var periodOf = rows.ToDictionary(r => r.Id, r => AddOnBilling.PeriodOf(r.OccurredAt));

            // ตัวตัดสินเดียว (pure · Helpers/UsageInvoiceLines): prepaid ปิดรอบไม่มีใบ · รวม 0 บาท (ใบที่กฎหมายบังคับในโหมดล็อก · มิเตอร์ข้อมูลอย่างเดียว — รอบ 231) ปิดงวดไม่มีใบ ·
            // ต่ำกว่าขั้นต่ำ ยกยอด (ไม่ตีตรา — ไม่ใช่การยกเลิกหนี้) · อื่น ออกใบ · ยอดถูก snapshot แล้ว (ChargedAmount) การตั้งราคาทีหลังไม่คิดย้อนหลัง
            var total = rows.Sum(r => r.ChargedAmount);
            var disposition = Accounting.Helpers.UsageInvoiceLines.Dispose(accountId.HasValue && prepaid.Contains(accountId.Value), total, minInvoice);
            if (disposition == Accounting.Helpers.UsageInvoiceLines.Disposition.ClosePrepaid)
            {
                foreach (var r in rows) r.BilledPeriod = periodOf[r.Id];
                closedPrepaid += rows.Count;
                continue;
            }
            if (disposition == Accounting.Helpers.UsageInvoiceLines.Disposition.CloseZero)
            {
                foreach (var r in rows) r.BilledPeriod = periodOf[r.Id];
                closedZero += rows.Count;
                continue;
            }
            if (disposition == Accounting.Helpers.UsageInvoiceLines.Disposition.Carry)
            {
                carried += rows.Count;
                continue;
            }

            // บรรทัดใบ (§86/4: ชื่อที่อ่านออก · Qty × ราคา/หน่วย = ยอดบรรทัด · แยกบรรทัดตามงวด/หน่วย ณ ตอนซื้อ/ราคา) — ตัวประกอบเดียว Helpers/UsageInvoiceLines.Build
            var lineRows = rows.Select(r => new Accounting.Helpers.UsageInvoiceLines.Row(periodOf[r.Id], r.FeatureCode, r.UnitLabelSnapshot, r.UnitPriceSnapshot, r.Quantity, r.ChargedAmount)).ToList();
            var lines = Accounting.Helpers.UsageInvoiceLines.Build(lineRows,
                code => names.TryGetValue(code, out var meta) ? ((string? Name, string? UnitLabel)?)(meta.Name, meta.UnitLabel) : null);
            var latestPeriod = Accounting.Helpers.UsageInvoiceLines.LatestPeriod(lineRows);
            var periodsText = string.Join(", ", lineRows.Select(r => r.Period).Distinct().OrderBy(p => p).Select(Accounting.Helpers.UsageHistoryRule.Label));

            var issueDate = DateTime.UtcNow;
            var doc = await issuer.IssueUsageInvoiceAsync(
                buyerCompanyId: group.Key,
                lines: lines,
                issueDate: issueDate,
                dueDate: Accounting.Helpers.UsageInvoiceLines.DueDate(issueDate, billingPolicy.OverageGraceDays),   // รอบ 231: ครบกำหนด = วันออก + OverageGraceDays
                reference: Accounting.Helpers.UsageInvoiceLines.Reference(group.Key, latestPeriod),
                notes: $"ค่าใช้งานตามจริงงวด {periodsText} — รายละเอียดแยกตามรายการด้านล่าง");

            // ออกใบไม่สำเร็จ = **ห้ามตีตรา** ปล่อยให้รอบหน้าลองใหม่
            // (ตีตราแล้วรายได้หายถาวรโดยไม่มีใครรู้ — เงียบที่สุดในบรรดาบั๊กเรื่องเงิน)
            if (doc == null)
            {
                _logger.LogWarning("ออกใบแจ้งหนี้ค่าใช้งานงวด {Period} ของบริษัท {Cid} ไม่สำเร็จ — จะลองใหม่รอบหน้า",
                    latestPeriod, group.Key);
                continue;
            }

            foreach (var r in rows) { r.BilledPeriod = periodOf[r.Id]; r.BilledDocumentId = doc.DocumentId; }
            issued++;
            issuedDocs.Add((group.Key, doc.DocumentId));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // รอบ 219 เฟส 2 ขั้น 2 (G-7): ส่งใบแจ้งหนี้ถึงผู้เช่า <b>หลัง commit</b> (ใบมีจริงแล้ว) — คีย์กันซ้ำต่อใบ · ล้ม = งานทวงหนี้ส่งตามให้รอบหน้า
        var notifier = scope.ServiceProvider.GetService<Accounting.Services.Implementations.IPlatformBillingNotifier>();
        if (notifier != null)
            foreach (var (buyer, docId) in issuedDocs) await notifier.InvoiceIssuedAsync(buyer, docId, ct);

        var summary = $"ออกใบแจ้งหนี้ {issued} ใบ · ปิดรอบ prepaid {closedPrepaid} รายการ · ปิดงวดรายการ 0 บาท {closedZero} รายการ · ยกยอดต่ำกว่าเกณฑ์ {carried} รายการ";
        if (issued > 0 || closedPrepaid > 0) _logger.LogInformation("ปิดรอบบิลค่าใช้งาน: {Summary}", summary);
        if (recorder != null)
            await recorder.RecordAsync("UsageInvoicing", true, summary, issued, startedAt);
    }
}
