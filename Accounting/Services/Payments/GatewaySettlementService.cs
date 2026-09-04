using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>คำขอบันทึก "เงินที่ผู้ให้บริการโอนเข้าธนาคาร" 1 รอบ</summary>
public sealed record RecordSettlementRequest(
    string ProviderCode,
    DateTime FromDate,
    DateTime ToDate,
    // ยอดสุทธิที่เข้าบัญชีจริงตามสเตทเมนต์ — **ตัวตั้ง** ของการตรวจ
    decimal ActualNetReceived,
    DateTime SettledAt,
    string SettlementRef,
    Guid BankAccountId);

/// <summary>ผลของการดูตัวอย่าง/บันทึก — โครงเดียวกันทั้งสองเส้นเพื่อให้หน้าเว็บวาดที่เดียว</summary>
public sealed record SettlementOutcome(
    bool Ok,
    string? Message,
    SettlementPlan Plan,
    Guid? JournalEntryId,
    string? JournalEntryNumber);

public interface IGatewaySettlementService
{
    /// <summary>ดูตัวอย่างก่อนบันทึก — ไม่เขียนอะไรเลย</summary>
    Task<SettlementOutcome> PreviewAsync(Guid companyId, RecordSettlementRequest req,
        CancellationToken ct = default);

    /// <summary>บันทึกจริง: ลง JE + มาร์กรายการว่าโอนเข้าแล้ว (atomic)</summary>
    Task<SettlementOutcome> RecordAsync(Guid companyId, RecordSettlementRequest req, string actor,
        CancellationToken ct = default);
}

/// <summary>
/// **ขั้น "เงินเข้าธนาคารจริง" ของ payment gateway** (PAYMENT_GATEWAY_DESIGN.md §4.5, §5)
///
/// ═══ ทำไมต้องแยกเป็นอีกขั้น ═══
/// ตอนลูกค้าจ่ายสำเร็จ เงิน<b>ยังไม่เข้าบัญชีเรา</b> ⇒ ระบบลง
/// <c>Dr 11340 ลูกหนี้ผู้ให้บริการรับชำระเงิน / Cr ลูกหนี้การค้า</c> ไว้ก่อน ·
/// ถ้าลง Dr ธนาคารตั้งแต่ตอนนั้น <b>ยอดธนาคารในระบบจะไม่ตรงกับสเตทเมนต์ตลอดเวลา</b>
/// และผู้ทำบัญชีจะกระทบยอดไม่ได้เลย · ขั้นนี้คือขั้นที่ล้าง 11340 ออกด้วยเงินจริง
///
/// ═══ ทำไม "บันทึกด้วยมือจากสเตทเมนต์" ถึงเป็นเส้นหลัก (ไม่ใช่ดึงอัตโนมัติ) ═══
/// ตัวตั้งของขั้นนี้คือ<b>ยอดที่เข้าบัญชีธนาคารจริง</b> ซึ่งผู้ใช้เห็นจากสเตทเมนต์/แดชบอร์ด
/// ของผู้ให้บริการ · ระบบเอายอดนั้นมา<b>ตรวจ</b>กับผลรวมที่คำนวณได้ แล้ว<b>บล็อกเมื่อไม่ตรง</b>
/// — ปลอดภัยกว่าการเชื่อ API รายงานยอดโอนซึ่งแต่ละเจ้าคืนโครงต่างกันและเราตรวจสอบไม่ได้
/// <para>การดึงอัตโนมัติเป็นงานของ <c>IPaymentProvider.ListSettlementsAsync</c> ในอนาคต
/// (ยังไม่มีเจ้าไหน implement — ดู PAYMENT_GATEWAY_DESIGN.md §7.1 backlog)</para>
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item>คณิตทั้งหมดอยู่ใน <see cref="GatewaySettlementMath"/> (บริสุทธิ์ + มีเทสต์) —
///   ที่นี่ทำแค่ "หาข้อมูลให้กติกา แล้วลงมือตามที่กติกาบอก"</item>
/// <item>ล็อกต่อ (บริษัท, provider) — การเลือกรายการที่ยังไม่โอนแล้วมาร์กทีหลังเป็น
///   read-modify-write · สองคนกดพร้อมกันจะลง JE ซ้ำ ⇒ ธนาคารเกินสองเท่า</item>
/// <item>ไม่มี "ปัดให้ลงตัว" — ไม่ตรงคือบล็อกพร้อมบอกว่าต่างเท่าไร</item>
/// </list>
/// </summary>
public class GatewaySettlementService : IGatewaySettlementService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<GatewaySettlementService> _logger;

    public GatewaySettlementService(AccountingDbContext db, ILogger<GatewaySettlementService> logger)
    { _db = db; _logger = logger; }

    public async Task<SettlementOutcome> PreviewAsync(Guid companyId, RecordSettlementRequest req,
        CancellationToken ct = default)
    {
        var (plan, _) = await BuildPlanAsync(companyId, req, ct);
        return new SettlementOutcome(plan.Ok, plan.Message, plan, null, null);
    }

    public async Task<SettlementOutcome> RecordAsync(Guid companyId, RecordSettlementRequest req,
        string actor, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // ล็อกต้องอยู่ **ก่อน** การเลือกรายการ — ไม่งั้นสองคนเลือกชุดเดียวกันได้
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.GatewaySettlement,
                    req.ProviderCode) }, ct);

            var (plan, intents) = await BuildPlanAsync(companyId, req, ct);
            if (!plan.Ok)
            {
                await tx.RollbackAsync(ct);
                return new SettlementOutcome(false, plan.Message, plan, null, null);
            }

            var config = await _db.PaymentProviderConfigs
                .FirstOrDefaultAsync(c => c.CompanyId == companyId
                    && c.ProviderCode == req.ProviderCode && !c.IsDeleted, ct);

            var accounts = await ResolveAccountsAsync(companyId, config, req.BankAccountId, plan, ct);
            if (accounts.Error != null)
            {
                await tx.RollbackAsync(ct);
                return new SettlementOutcome(false, accounts.Error, plan, null, null);
            }

            var entryDate = ThaiDate.CalendarDateUtc(req.SettledAt);
            var period = await _db.FiscalPeriods.FirstOrDefaultAsync(
                p => p.CompanyId == companyId && p.StartDate <= entryDate && p.EndDate >= entryDate, ct);

            var totalDebit = plan.Lines.Sum(l => l.Debit);
            var totalCredit = plan.Lines.Sum(l => l.Credit);
            var je = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = await Accounting.Services.Implementations.Journal.JournalEntryBuilder
                    .NextJournalNumberAsync(_db, companyId, "RV", entryDate),
                EntryDate = entryDate,
                JournalType = JournalType.CashReceipts,
                Description = $"รับโอนจากผู้ให้บริการรับชำระเงิน ({req.ProviderCode}) {req.SettlementRef}",
                Reference = req.SettlementRef,
                Status = JournalEntryStatus.Posted,
                TotalDebit = totalDebit,
                TotalCredit = totalCredit,
                CreatedBy = actor,
                IsAutoGenerated = true,
                FiscalPeriodId = period?.Id,
            };
            _db.JournalEntries.Add(je);

            var order = 1;
            foreach (var line in plan.Lines)
            {
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    JournalEntryId = je.Id,
                    AccountId = accounts.Map[line.Role],
                    DebitAmount = line.Debit,
                    CreditAmount = line.Credit,
                    Description = line.Description,
                    LineOrder = order++,
                });
            }

            foreach (var intent in intents)
            {
                // ยอดที่ "โอนเข้าจริง" ของแต่ละรายการ = ยอดเต็มหักค่าธรรมเนียมของตัวเอง
                // (เก็บรายรายการเพื่อให้รายงานกระทบยอดตรวจย้อนได้ว่ารายการไหนโอนแล้ว)
                var fee = intent.FeeActual ?? intent.FeeEstimated;
                intent.SettledAmount = Math.Round(intent.Amount - fee, 2, MidpointRounding.AwayFromZero);
                intent.SettledAt = req.SettledAt;
                intent.SettlementRef = req.SettlementRef;
                intent.SettlementJournalEntryId = je.Id;
                // ค่าธรรมเนียมที่ยังเป็นตัวประมาณ ถูกยืนยันด้วยยอดโอนจริงแล้ว
                intent.FeeActual ??= fee;
                intent.UpdatedAt = DateTime.UtcNow;

                _db.PaymentIntentEvents.Add(new PaymentIntentEvent
                {
                    CompanyId = companyId,
                    IntentId = intent.Id,
                    At = DateTime.UtcNow,
                    Source = PaymentEventSource.Manual,
                    FromStatus = intent.Status,
                    ToStatus = intent.Status,
                    Note = $"บันทึกเงินโอนเข้าธนาคาร {req.SettlementRef} "
                         + $"สุทธิ {intent.SettledAmount:N2} (ค่าธรรมเนียม {fee:N2}) โดย {actor}",
                });
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "บันทึกเงินโอนเข้าจาก gateway {Provider} บริษัท {Company}: {Count} รายการ "
                + "ยอดเต็ม {Gross:N2} ค่าธรรมเนียม {Fee:N2} สุทธิ {Net:N2} → JE {Je}",
                req.ProviderCode, companyId, plan.IntentIds.Count, plan.Gross, plan.FeeGrossedUp,
                plan.ExpectedNet, je.EntryNumber);

            return new SettlementOutcome(true,
                $"บันทึกเงินโอนเข้า {plan.IntentIds.Count} รายการแล้ว — ใบสำคัญ {je.EntryNumber}",
                plan, je.Id, je.EntryNumber);
        });
    }

    /// <summary>เลือกรายการที่เข้าเงื่อนไข แล้วให้ <see cref="GatewaySettlementMath"/> ตัดสิน</summary>
    private async Task<(SettlementPlan Plan, List<PaymentIntent> Intents)> BuildPlanAsync(
        Guid companyId, RecordSettlementRequest req, CancellationToken ct)
    {
        var from = ThaiDate.CalendarDateUtc(req.FromDate);
        var to = ThaiDate.CalendarDateUtc(req.ToDate).AddDays(1);   // ปลายช่วงแบบ exclusive

        // เลือกเฉพาะ "สำเร็จแล้ว · ยังไม่เคยบันทึกการโอน" — รายการที่คืนเงินไปแล้ว
        // ไม่มีวันโอนเข้า จึงต้องไม่ถูกนับ (บทเรียนข้อ 18 ของเอกสารออกแบบ)
        var intents = await _db.PaymentIntents
            .Where(i => i.CompanyId == companyId
                && i.ProviderCode == req.ProviderCode
                && i.Status == PaymentIntentStatus.Succeeded
                && i.SettlementJournalEntryId == null
                && i.ConfirmedAt != null
                && i.ConfirmedAt >= from && i.ConfirmedAt < to)
            .OrderBy(i => i.ConfirmedAt)
            .ToListAsync(ct);

        var config = await _db.PaymentProviderConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId
                && c.ProviderCode == req.ProviderCode && !c.IsDeleted, ct);

        var plan = GatewaySettlementMath.Plan(
            intents.Select(i => new SettlementIntentInput(
                i.Id, i.Amount, i.FeeActual, i.FeeEstimated)).ToList(),
            req.ActualNetReceived,
            config?.WhtOnFee ?? GatewayFeeWhtMode.None,
            req.SettlementRef);

        return (plan, intents);
    }

    private sealed record AccountResolution(
        Dictionary<SettlementLineRole, Guid> Map, string? Error);

    /// <summary>หาผังบัญชีของแต่ละบทบาท — ขาดตัวไหนต้องบอก<b>ชื่อผังที่ขาด + ที่ตั้งค่า</b>
    /// ไม่ใช่ "เกิดข้อผิดพลาด" (ผู้ใช้ต้องรู้ว่าไปตั้งตรงไหน)</summary>
    private async Task<AccountResolution> ResolveAccountsAsync(Guid companyId,
        PaymentProviderConfig? config, Guid bankAccountId, SettlementPlan plan, CancellationToken ct)
    {
        var map = new Dictionary<SettlementLineRole, Guid>();

        var bankGl = await _db.Set<BankAccount>().AsNoTracking()
            .Where(b => b.Id == bankAccountId && b.CompanyId == companyId && !b.IsDeleted)
            .Select(b => b.LinkedAccountId)
            .FirstOrDefaultAsync(ct);
        if (bankGl is not Guid bankGlId)
            return new AccountResolution(map,
                "บัญชีธนาคารที่เลือกยังไม่ได้ผูกกับผังบัญชี — ไปที่ ตั้งค่า → บัญชีธนาคาร "
                + "แล้วเลือกผังบัญชีของธนาคารนี้ก่อน (ไม่งั้นเงินเข้าจะลงผังผิด)");
        map[SettlementLineRole.Bank] = bankGlId;

        var clearingId = config?.ClearingAccountId
            ?? (await FindByCodeAsync(companyId, "11340", ct))?.Id;
        if (clearingId is not Guid cid)
            return new AccountResolution(map,
                "ยังไม่ได้ตั้ง \"บัญชีพักเงินผู้ให้บริการรับชำระเงิน\" (แนะนำ 11340) — "
                + "ตั้งได้ที่หน้า ตั้งค่าการรับชำระเงินออนไลน์");
        map[SettlementLineRole.Clearing] = cid;

        if (plan.Lines.Any(l => l.Role == SettlementLineRole.FeeExpense))
        {
            var feeId = config?.FeeExpenseAccountId
                ?? (await FindByCodeAsync(companyId, "54710", ct))?.Id;
            if (feeId is not Guid fid)
                return new AccountResolution(map,
                    "ยังไม่ได้ตั้งผังบัญชี \"ค่าธรรมเนียมรับชำระเงิน\" (แนะนำ 54710 ค่าธรรมเนียมธนาคาร) — "
                    + "ตั้งได้ที่หน้า ตั้งค่าการรับชำระเงินออนไลน์");
            map[SettlementLineRole.FeeExpense] = fid;
        }

        if (plan.Lines.Any(l => l.Role == SettlementLineRole.WhtPayable))
        {
            // ผู้ให้บริการรับชำระเงินเป็นนิติบุคคล ⇒ ภ.ง.ด.53 (21917)
            var whtAcc = await FindByCodeAsync(companyId, "21917", ct);
            if (whtAcc == null)
                return new AccountResolution(map,
                    "ไม่พบผังบัญชี 21917 ภาษีหัก ณ ที่จ่าย - ภ.ง.ด.53 — "
                    + "เพิ่มในผังบัญชีก่อน หรือปิดตัวเลือก \"หัก ณ ที่จ่ายค่าธรรมเนียม\"");
            map[SettlementLineRole.WhtPayable] = whtAcc.Id;
        }

        return new AccountResolution(map, null);
    }

    private Task<ChartOfAccount?> FindByCodeAsync(Guid companyId, string code, CancellationToken ct)
        => _db.ChartOfAccounts.FirstOrDefaultAsync(
            a => a.CompanyId == companyId && a.AccountCode == code && !a.IsDeleted, ct);
}
