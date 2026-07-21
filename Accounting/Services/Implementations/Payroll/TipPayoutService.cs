using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Payroll;

/// <summary>POS tip distribution → payroll payout with §50 ทวิ WHT.
///
/// Workflow:
/// 1. POS sale collects tip → Cr "เงินรับฝาก-ทิปพนักงาน" (2160 liability)
/// 2. Sales accumulate per period (เช่นรอบ 1 ส.ค.-31 ส.ค.) — total in 2160
/// 3. Manager distribute tip ตามสัดส่วน (e.g. ตาม service activity hours
///    ต่อ session, equal share ทั้ง shift, role-based)
/// 4. **TipPayoutService.DistributeAndPayoutAsync** จ่ายให้พนักงาน
///    - คำนวณ tip per staff
///    - **§50 ทวิ check**: ถ้า cumulative payout ที่เกิน 1,000 บาท/รอบจ่าย
///      จากผู้จ่ายเดียวกัน → หัก WHT 3% (ม.40(2) ค่าบริการ)
///    - Dr 2160 ทิป (clear liability) / Cr เงินสด/ธนาคาร (พนักงานรับ)
///      / Cr 21915 WHT payable (ถ้ามี)
///    - ออก หนังสือรับรอง 50 ทวิ ทุกคน (ม.50(7))
/// 5. Reset 2160 balance ของรอบนั้น
///
/// อ้างอิง: ป.รัษฎากร §50, §50 ทวิ, ม.40(2)</summary>
public interface ITipPayoutService
{
    Task<TipPayoutResult> DistributeAndPayoutAsync(Guid companyId, DateTime periodStart,
        DateTime periodEnd, IReadOnlyDictionary<Guid, decimal> staffSharePercent,
        string actor, CancellationToken ct = default);
}

public sealed record TipPayoutResult(
    decimal TotalTipPool,
    int StaffCount,
    decimal TotalWhtWithheld,
    decimal TotalPaidOut,
    Guid? JournalEntryId,
    string? JournalNumber,
    IReadOnlyList<StaffTipPayoutLine> Lines);

public sealed record StaffTipPayoutLine(
    Guid StaffId,
    string StaffName,
    decimal GrossTip,
    decimal WhtRate,
    decimal WhtAmount,
    decimal NetPaid,
    Guid? Wht50TawiCertId);

public class TipPayoutService : ITipPayoutService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<TipPayoutService> _logger;
    private readonly Accounting.Services.Interfaces.IWithholdingTaxCertService _whtCert;

    public TipPayoutService(AccountingDbContext db, ILogger<TipPayoutService> logger,
        Accounting.Services.Interfaces.IWithholdingTaxCertService whtCert)
    {
        _db = db;
        _logger = logger;
        _whtCert = whtCert;
    }

    public async Task<TipPayoutResult> DistributeAndPayoutAsync(Guid companyId,
        DateTime periodStart, DateTime periodEnd,
        IReadOnlyDictionary<Guid, decimal> staffSharePercent, string actor,
        CancellationToken ct = default)
    {
        // 1. Sum tip pool จาก PosOrder ในงวด (ที่ Completed)
        var totalTip = await _db.PosOrders.AsNoTracking()
            .Where(o => o.CompanyId == companyId
                && o.Status == PosOrderStatus.Completed
                && o.CreatedAt >= periodStart && o.CreatedAt <= periodEnd
                && o.TipAmount > 0)
            .SumAsync(o => o.TipAmount, ct);

        if (totalTip <= 0)
            throw new InvalidOperationException("ไม่มีทิปในงวดนี้ที่จะแจกจ่าย");

        var totalShare = staffSharePercent.Values.Sum();
        if (Math.Abs(totalShare - 100m) > 0.01m)
            throw new InvalidOperationException(
                $"สัดส่วนแจกจ่ายต้องรวม 100% (ได้รับ {totalShare:N2}%)");

        // 2. Look up GL accounts
        var tipLiab = await _db.ChartOfAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CompanyId == companyId
                && a.AccountCode == "2160" && a.IsActive, ct);
        var cash = await _db.ChartOfAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CompanyId == companyId
                && a.AccountCode.StartsWith("1011") && a.IsActive, ct);
        var whtPayable = await _db.ChartOfAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CompanyId == companyId
                && a.AccountCode == "21915" && a.IsActive, ct);
        if (tipLiab == null || cash == null)
            throw new InvalidOperationException(
                "ผังบัญชี 2160 (ทิปค้างจ่าย) หรือ 1011 (เงินสด) ไม่พบ — สร้างก่อน");

        const decimal WhtThreshold = 1000m;
        const decimal WhtRate = 3m;       // §50 ทวิ + ม.40(2) ค่าบริการ → 3%

        var lines = new List<StaffTipPayoutLine>();
        var staffIds = staffSharePercent.Keys.ToList();
        var staffEntities = await _db.Set<Employee>().AsNoTracking()
            .Where(e => staffIds.Contains(e.Id) && e.CompanyId == companyId && !e.IsDeleted)
            .Select(e => new { e.Id, Name = $"{e.FirstNameTh} {e.LastNameTh}".Trim(), e.ContactId })
            .ToListAsync(ct);
        var staffMap = staffEntities.ToDictionary(e => e.Id, e => e.Name);
        var staffContactId = staffEntities.ToDictionary(e => e.Id, e => e.ContactId);
        // เก็บรายการที่ต้องออก 50 ทวิ (มี WHT + ผูก Contact ผู้รับ) → ออกหลัง commit JE
        var certTargets = new List<(Guid StaffId, Guid ContactId, decimal Gross, decimal Wht)>();

        decimal totalWhtWithheld = 0m;
        decimal totalNet = 0m;

        // 3. Build JE ผ่าน JournalEntryBuilder
        var builder = Journal.JournalEntryBuilder
            .For(_db, companyId, DateTime.UtcNow.Date)
            .Description($"จ่ายทิปพนักงานงวด {periodStart:yyyy-MM-dd} ถึง {periodEnd:yyyy-MM-dd}")
            .Reference($"TIP-PAYOUT-{periodEnd:yyyyMM}")
            .CreatedBy(actor);

        // Dr 2160 (เคลียร์ liability)
        builder.Debit(tipLiab.Id, totalTip, $"เคลียร์ทิปค้างจ่ายงวด {periodEnd:yyyy-MM}");

        foreach (var (staffId, pct) in staffSharePercent)
        {
            var grossTip = Math.Round(totalTip * pct / 100m, 2);
            var withholding = grossTip >= WhtThreshold ? Math.Round(grossTip * WhtRate / 100m, 2) : 0m;
            var net = grossTip - withholding;

            lines.Add(new StaffTipPayoutLine(
                StaffId: staffId,
                StaffName: staffMap.GetValueOrDefault(staffId, "(ไม่พบพนักงาน)"),
                GrossTip: grossTip,
                WhtRate: withholding > 0 ? WhtRate : 0m,
                WhtAmount: withholding,
                NetPaid: net,
                Wht50TawiCertId: null));   // เติมหลัง commit (ดู certTargets ด้านล่าง)

            // มี WHT + ผูก Contact ผู้รับ → คิว 50 ทวิ (ม.50(7)). พนักงานที่ยังไม่ผูก
            // Contact จะยังไม่ออก cert (log เตือน) — ไม่บล็อกการจ่าย
            if (withholding > 0 && staffContactId.GetValueOrDefault(staffId) is Guid cid)
                certTargets.Add((staffId, cid, grossTip, withholding));

            totalWhtWithheld += withholding;
            totalNet += net;

            // Cr cash for net (เงินที่พนักงานรับจริง)
            builder.Credit(cash.Id, net, $"จ่ายทิปสุทธิ {staffMap.GetValueOrDefault(staffId, "")}");
        }

        // Cr WHT payable ถ้ามี withholding
        if (totalWhtWithheld > 0 && whtPayable != null)
            builder.Credit(whtPayable.Id, totalWhtWithheld, $"หัก ณ ที่จ่ายทิป §50 ทวิ");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var je = await builder.PostAsync(actor, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Tip payout posted: pool {Pool:N2}, staff {Count}, WHT {Wht:N2}, net {Net:N2}, JE {JeNum}",
                totalTip, lines.Count, totalWhtWithheld, totalNet, je.EntryNumber);

            // ออกหนังสือรับรอง 50 ทวิ (ม.50(7)) — best-effort หลัง commit JE:
            // cert เป็นเอกสารประกอบ ไม่ควร roll back การจ่ายเงินที่ลง GL แล้ว.
            // ทิปจ่ายบุคคล = ม.40(2) ค่าบริการ → ภงด.3 (WithholdingTax3), IncomeTypeCode "2".
            foreach (var t in certTargets)
            {
                try
                {
                    var certReq = new Models.DTOs.Tax.CreateWithholdingTaxCertRequest(
                        PayeeContactId: t.ContactId,
                        TaxFormType: Models.Enums.TaxType.WithholdingTax3,
                        TaxYear: periodEnd.Year,
                        TaxMonth: periodEnd.Month,
                        CertificateType: Models.DTOs.Tax.WithholdingTaxCertType.Withhold,
                        Lines: new List<Models.DTOs.Tax.WithholdingTaxCertLineRequest>
                        {
                            new(
                                IncomeTypeCode: "2",
                                IncomeDescription: $"ค่าบริการ (ทิป) งวด {periodStart:yyyy-MM-dd} ถึง {periodEnd:yyyy-MM-dd}",
                                PaymentDate: DateTime.UtcNow.Date,
                                IncomeAmount: t.Gross,
                                TaxRate: WhtRate,
                                TaxAmount: t.Wht,
                                Condition: "หักภาษี ณ ที่จ่าย")
                        });
                    var cert = await _whtCert.CreateAsync(companyId, certReq, actor);
                    var idx = lines.FindIndex(l => l.StaffId == t.StaffId);
                    if (idx >= 0) lines[idx] = lines[idx] with { Wht50TawiCertId = cert.Id };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ออก 50 ทวิ ทิปไม่สำเร็จสำหรับพนักงาน {StaffId} — JE จ่ายแล้ว ออก cert ภายหลังได้", t.StaffId);
                }
            }
            var staffNoContact = staffSharePercent.Keys
                .Where(id => certTargets.All(t => t.StaffId != id))
                .Count(id => staffContactId.GetValueOrDefault(id) == null
                    && lines.Any(l => l.StaffId == id && l.WhtAmount > 0));
            if (staffNoContact > 0)
                _logger.LogWarning("{Count} พนักงานถูกหัก WHT ทิปแต่ยังไม่ผูก Contact → ยังไม่ออก 50 ทวิ", staffNoContact);

            return new TipPayoutResult(
                TotalTipPool: totalTip,
                StaffCount: lines.Count,
                TotalWhtWithheld: totalWhtWithheld,
                TotalPaidOut: totalNet,
                JournalEntryId: je.Id,
                JournalNumber: je.EntryNumber,
                Lines: lines);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
