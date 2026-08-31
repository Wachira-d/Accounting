using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accounting.Services.Background;

/// <summary>ค่าเผื่อหนี้สงสัยจะสูญอัตโนมัติ (Expected Credit Loss) ตาม
/// TFRS for NPAEs บทที่ 9 — คำนวณจาก AR aging bucket × loss rate ของบริษัท
/// แล้ว post ปรับปรุงค่าเผื่อให้ยอดคงเหลือ 18100 ตรงกับที่ควรตั้ง:
///
/// 1. เฉพาะบริษัทที่เปิด <c>CompanySettings.EclEnabled</c> (opt-in —
///    ไม่ตั้งสำรองให้ tenant เดิมโดยพลการ)
/// 2. AR คงค้าง (Invoice/TaxInvoice/DebitNote, BalanceDue&gt;0, ไม่ voided)
///    จัด bucket ตามวันเกินกำหนด: 0-30 / 31-60 / 61-90 / 91-180 / 180+
/// 3. required = Σ(balance × rate ต่อ bucket) — rate จาก
///    <c>EclLossRatesJson</c> (%) หรือ default 1/5/10/25/50
/// 4. adjustment = required − ยอดค่าเผื่อปัจจุบันใน GL (18100, Cr-normal)
///    → Dr 57130 หนี้สงสัยจะสูญ / Cr 18100 (หรือกลับทางเมื่อค่าเผื่อเกิน)
/// 5. Idempotent ต่อเดือน: Reference = "ECL-{yyyyMM}" — มีแล้วข้าม
///
/// รันทุก 24 ชม. แต่ post เฉพาะ 3 วันสุดท้ายของเดือน (month-end provision).</summary>
public class EclAllowanceJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EclAllowanceJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    // default loss rate ต่อ bucket (%) เมื่อบริษัทไม่ได้ตั้งเอง — ค่ากลาง ๆ
    // สำหรับ SME ไทย ปรับได้ผ่าน EclLossRatesJson: [1, 5, 10, 25, 50]
    private static readonly decimal[] DefaultRates = { 1m, 5m, 10m, 25m, 50m };

    public EclAllowanceJob(IServiceProvider services, ILogger<EclAllowanceJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(7), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "EclAllowanceJob failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        // month-end provision — post เฉพาะ 3 วันสุดท้ายของเดือน
        if (today.Day < daysInMonth - 2) return;
        var monthTag = $"ECL-{today:yyyyMM}";

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        var companies = await db.CompanySettings.AsNoTracking()
            .Where(s => s.EclEnabled)
            .Select(s => new { s.CompanyId, s.EclLossRatesJson })
            .ToListAsync(ct);

        foreach (var c in companies)
        {
            try { await ProcessCompanyAsync(db, c.CompanyId, c.EclLossRatesJson, monthTag, today, ct); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ECL provision failed for company {Cid}", c.CompanyId);
            }
        }
    }

    private async Task ProcessCompanyAsync(AccountingDbContext db, Guid companyId,
        string? ratesJson, string monthTag, DateTime today, CancellationToken ct)
    {
        // idempotent ต่อเดือน
        var already = await db.JournalEntries.AsNoTracking().AnyAsync(j =>
            j.CompanyId == companyId && j.Reference == monthTag
            && j.Status == JournalEntryStatus.Posted, ct);
        if (already) return;

        var rates = ParseRates(ratesJson);

        // AR คงค้าง — ประเภทที่ตั้งลูกหนี้จริงเท่านั้น
        var arTypes = new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote };
        var openDocs = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && arTypes.Contains(d.DocumentType)
                && d.BalanceDue > 0
                && d.Status != DocumentStatus.Draft
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected)
            .Select(d => new { d.BalanceDue, d.DueDate, d.DocumentDate, d.ExchangeRate })
            .ToListAsync(ct);
        if (openDocs.Count == 0) return;

        decimal required = 0m;
        foreach (var d in openDocs)
        {
            var basis = (d.DueDate ?? d.DocumentDate).Date;
            var overdue = (today - basis).Days;
            var rate = overdue switch
            {
                <= 30 => rates[0],
                <= 60 => rates[1],
                <= 90 => rates[2],
                <= 180 => rates[3],
                _ => rates[4],
            };
            // BalanceDue เป็นสกุลเอกสาร → แปลง THB ที่ rate เอกสาร
            var thb = d.ExchangeRate == 1m ? d.BalanceDue
                : Math.Round(d.BalanceDue * d.ExchangeRate, 2, MidpointRounding.AwayFromZero);
            required += thb * rate / 100m;
        }
        required = Math.Round(required, 2, MidpointRounding.AwayFromZero);

        // ผังบัญชี: ค่าเผื่อ (contra-asset, Cr-normal) + หนี้สงสัยจะสูญ (expense)
        var allowanceAcc = await FindAccountAsync(db, companyId, "18100")
            ?? await FindAccountAsync(db, companyId, "181");
        var expenseAcc = await FindAccountAsync(db, companyId, "57130")
            ?? await db.ChartOfAccounts.AsNoTracking().FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                && a.AccountType == AccountType.Expense
                && a.AccountName.Contains("หนี้สงสัยจะสูญ"), ct);
        if (allowanceAcc == null || expenseAcc == null)
        {
            _logger.LogWarning(
                "ECL company {Cid}: ไม่พบผังค่าเผื่อ (18100) หรือหนี้สงสัยจะสูญ (57130) — ข้าม",
                companyId);
            return;
        }

        // ยอดค่าเผื่อปัจจุบัน (Cr-normal): Cr − Dr ของ 18100
        var currentAllowance = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.AccountId == allowanceAcc.Id)
            .SumAsync(l => (decimal?)(l.CreditAmount - l.DebitAmount), ct) ?? 0m;

        var adjustment = Math.Round(required - currentAllowance, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(adjustment) < 1m) return;   // ต่ำกว่า 1 บาท ไม่คุ้ม JE

        // ⚠️ เดิม job นี้ออกเลข JV เอง โดย (ก) **ไม่มี advisory lock เลย** และ
        // (ข) ใช้ `OrderByDescending(EntryNumber)` = เรียงแบบ string ⇒ พังเมื่อ
        // เลขเกิน 9999 ("9999" มาหลัง "10000") ซึ่งตัวออกเลขกลางออกเป็น D5
        // ⇒ ตั้งสำรองฯ ชนเลขกับ JE ที่ผู้ใช้กำลัง approve อยู่
        // ใช้ตัวออกเลขกลางตัวเดียวกับทุกคน (ล็อก + integer max + นับ change tracker)
        var entryNumber = await Services.Implementations.Journal.JournalEntryBuilder
            .NextJournalNumberAsync(db, companyId, "JV", today, ct);

        var fiscalPeriod = await db.FiscalPeriods.AsNoTracking().FirstOrDefaultAsync(f =>
            f.CompanyId == companyId && f.StartDate <= today && f.EndDate >= today
            && f.Status == FiscalPeriodStatus.Open, ct);

        var abs = Math.Abs(adjustment);
        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = today,
            JournalType = JournalType.General,
            Description = $"ตั้ง/ปรับค่าเผื่อหนี้สงสัยจะสูญ (ECL) ประจำเดือน {today:MM/yyyy} " +
                          $"— required {required:N2}, เดิม {currentAllowance:N2}",
            Reference = monthTag,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            FiscalPeriodId = fiscalPeriod?.Id,
            TotalDebit = abs,
            TotalCredit = abs,
            CreatedBy = "EclAllowanceJob",
        };
        db.JournalEntries.Add(je);
        if (adjustment > 0)
        {
            // ตั้งเพิ่ม: Dr หนี้สงสัยจะสูญ / Cr ค่าเผื่อ
            db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = je.Id, AccountId = expenseAcc.Id,
                DebitAmount = abs, CreditAmount = 0,
                Description = "หนี้สงสัยจะสูญ (ECL bucket)", LineOrder = 1,
            });
            db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = je.Id, AccountId = allowanceAcc.Id,
                DebitAmount = 0, CreditAmount = abs,
                Description = "ค่าเผื่อหนี้สงสัยจะสูญ", LineOrder = 2,
            });
        }
        else
        {
            // ค่าเผื่อเกินความจำเป็น (ลูกหนี้เก็บได้/ชำระแล้ว): กลับรายการ
            db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = je.Id, AccountId = allowanceAcc.Id,
                DebitAmount = abs, CreditAmount = 0,
                Description = "ลดค่าเผื่อหนี้สงสัยจะสูญ", LineOrder = 1,
            });
            db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = je.Id, AccountId = expenseAcc.Id,
                DebitAmount = 0, CreditAmount = abs,
                Description = "กลับรายการหนี้สงสัยจะสูญ", LineOrder = 2,
            });
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "ECL company {Cid}: posted {Tag} adjustment {Adj:N2} (required {Req:N2}, prior {Cur:N2})",
            companyId, monthTag, adjustment, required, currentAllowance);
    }

    private static decimal[] ParseRates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return DefaultRates;
        try
        {
            var arr = JsonSerializer.Deserialize<decimal[]>(json);
            if (arr is { Length: 5 } && arr.All(r => r is >= 0 and <= 100)) return arr;
        }
        catch { /* malformed → default */ }
        return DefaultRates;
    }

    private static Task<ChartOfAccount?> FindAccountAsync(AccountingDbContext db, Guid companyId, string code) =>
        db.ChartOfAccounts.AsNoTracking().FirstOrDefaultAsync(a =>
            a.CompanyId == companyId && a.IsActive && !a.IsDeleted
            && (a.AccountCode == code || a.AccountCode.StartsWith(code)));
}
