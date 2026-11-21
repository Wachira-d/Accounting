using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **รอบ 282 — สลิปที่ผูกใบเดียวแล้ว ⇒ บันทึกรับเงินเมื่อรายการเดินบัญชียืนยัน หรือ เข้าหน้ายืนยันของผู้ขาย** (คำตัดสิน 282-1..282-8)
/// <list type="bullet">
/// <item>ผู้เรียกเดียว: <c>LineChatSlipService</c> (ทุกทางเข้าของสลิปค่าสินค้า/บริการ: แชต/กลุ่ม OA บริษัท · กลุ่ม OA กลาง · อีเมล) หลังแจ้งโอนผ่านประตูเดิม</item>
/// <item>ตัวตัดสิน: แหล่งเงิน <see cref="SlipMoneySource.Decide"/> (ผู้รับบนสลิป ↔ บัญชีของบริษัท) → <see cref="SlipAutoRecordRule"/> (ค่าตั้ง · ผู้ส่ง · ใบเดียว · QR · ref ไม่ซ้ำ ·
/// วันที่ · งวดปิด · บัญชี · <b>รายการเดินบัญชี</b>)</item>
/// <item>282-8 (ฝ่ายค้าน HIGH-1): <b>สลิปอย่างเดียวไม่บันทึกรับเงิน</b> — QR ไม่มียอด/บัญชี/ผู้รับ · CRC เป็นสูตรสาธารณะ ⇒ รูปแต่งยอดผ่านได้ ·
/// ด่านสลิปครบ ⇒ หารายการเดินบัญชีของบัญชีบนสลิป ยอดตรง วันตรง ยังไม่จับคู่ (หนึ่งรายการ) ⇒ ตัวตัดสินเงินเข้าเดียว <see cref="IBankReceiptSettleService.EvaluateLineAsync"/>
/// (แหล่งของบรรทัด · สิทธิ์ผู้นำเข้า · ค่าตั้งตัดชำระของบริษัท · <c>CreatePaymentAsync</c> · ผูกบรรทัด + audit) — การบันทึกเงินเกิดที่นั่นที่เดียว</item>
/// <item>ไม่ผ่าน ⇒ รายการรอยืนยันเดิม (หน้ารายการรับชำระ · ค่าที่เสนอครบ) + เหตุ + แจ้ง + เตือนซ้ำ · รายการเดินบัญชีมาทีหลัง ⇒ งาน BankAutoSettle เดิมจับคู่ย้อน
/// (เลขอ้างอิง QR บนรายการชำระ = สัญญาณ <c>SlipRefMatched</c>)</item>
/// <item>ไม่มี AI ⇒ ปิด provider ทุกตัวแล้วทำงานเท่าเดิม (kill-switch)</item>
/// </list>
/// </summary>
public sealed class SlipAutoRecordService
{
    private readonly AccountingDbContext _db;
    private readonly IPaymentIntentService _intents;
    private readonly Accounting.Services.Storage.IFileStorage? _storage;
    private readonly ILogger<SlipAutoRecordService> _logger;
    private readonly INotificationEngine? _notify;
    /// <summary>282-8: ตัวตัดสินเงินเข้าจากรายการเดินบัญชีตัวเดียว (ผู้บันทึกเงินของเส้นนี้) · null = ไม่มีทางบันทึกเอง (ทุกใบเข้าหน้ายืนยัน)</summary>
    private readonly IBankReceiptSettleService? _bankSettle;

    public SlipAutoRecordService(AccountingDbContext db, IPaymentIntentService intents, ILogger<SlipAutoRecordService> logger,
        Accounting.Services.Storage.IFileStorage? storage = null, INotificationEngine? notify = null, IBankReceiptSettleService? bankSettle = null)
    { _db = db; _intents = intents; _logger = logger; _storage = storage; _notify = notify; _bankSettle = bankSettle; }

    public const string Actor = "slip-auto";
    public const string ConfirmPageHref = "/pages/payment-intents.html";
    public const int RemindBatch = 200;

    public sealed record Result(bool Recorded, string Code, string Reason);

    /// <param name="singleMatch">ตัวจับคู่ของ 260 ได้ใบเดียวอย่างมั่นใจ (<c>LineChatSlipRule.Outcome.One</c>)</param>
    /// <param name="sourceKind">แหล่งของแถวสลิป (ป้ายช่องทาง)</param>
    public async Task<Result> TryAutoAsync(Guid companyId, Guid intentId, OcrSlipText.Reading reading, bool singleMatch, bool senderVerified, string? sourceKind,
        CancellationToken ct = default)
    {
        var intent = await _db.PaymentIntents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);
        if (intent == null) return new(false, SlipAutoRecordRule.Codes.ConfirmFailed, "ไม่พบรายการชำระ");
        var doc = await _db.Documents.AsNoTracking().Where(d => d.Id == intent.SourceId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new { d.DocumentNumber, d.DocumentDate }).FirstOrDefaultAsync(ct);
        var settings = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => new { s.SlipAutoRecordEnabled, s.BankAutoSettleEnabled }).FirstOrDefaultAsync(ct);

        // เลขอ้างอิงจาก QR ตรวจสลิป (CRC ถูก) = เลขของธนาคาร ⇒ กันซ้ำ + สัญญาณให้ตัวจับคู่รายการเดินบัญชี (ต่างจาก ref ของ OCR — 260 ฝ่ายค้าน M1)
        var qrRef = reading.Qr == null ? null : SlipEvidenceRule.NormalizeRef(reading.Qr.TransactionRef);
        var dupStatuses = SlipEvidenceRule.DuplicateStatuses;
        var refDuplicate = qrRef != null && await _db.PaymentIntents.AsNoTracking()
            .AnyAsync(i => i.CompanyId == companyId && i.Id != intentId && i.SlipBankRef == qrRef && dupStatuses.Contains(i.Status), ct);
        if (qrRef != null && !refDuplicate && intent.SlipBankRef == null)
            await _db.PaymentIntents.Where(i => i.Id == intentId && i.CompanyId == companyId && i.SlipBankRef == null)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.SlipBankRef, qrRef), ct);

        var receiving = await SlipReceivingAccount.ResolveAsync(_db, _storage?.Locator.AttachmentsRoot, intent, null, ct);
        var payee = SlipReceivingAccount.SlipPayeeMatch(intent.SlipReadJson, receiving.Accounts);
        var money = SlipMoneySource.Decide(null, payee, receiving.Usable.Select(a => a.Id).ToList());
        var sug = receiving.Decision.Suggested;
        var periodLocked = reading.Date is DateTime pd && await PeriodLockedAsync(companyId, pd.Date, ct);
        var facts = new SlipAutoRecordRule.Facts(
            SettingOn: settings?.SlipAutoRecordEnabled ?? false, SenderVerified: senderVerified, SingleMatch: singleMatch && doc != null, QrVerified: qrRef != null,
            RefDuplicate: refDuplicate, SlipDateTh: reading.Date, DocumentDateTh: doc?.DocumentDate ?? DateTime.MaxValue, Money: money,
            ReceivingBank: receiving.Decision.BankAccountId, ReceivingDocChannelBank: sug.InheritsBank ? sug.DisplayBankAccountId : null,
            ReceivingProblem: receiving.Decision.Problem, NowUtc: DateTime.UtcNow, PeriodLocked: periodLocked);

        string? lineNote = null;
        var recorded = false;
        if (SlipAutoRecordRule.SlipReady(facts) && _bankSettle != null && money.AccountId is Guid acc && reading.Date is DateTime slipDay)
        {
            var from = slipDay.Date;
            var to = from.AddDays(1);
            var blocked = new[] { BankReceiptAutoSettle.Stamps.Applying, BankReceiptAutoSettle.Stamps.Settled, BankReceiptAutoSettle.Stamps.Rejected,
                BankReceiptAutoSettle.Stamps.Held };
            var lines = await _db.BankTransactions.AsNoTracking()
                .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.BankAccountId == acc && t.TransactionType == BankTransactionType.Deposit
                    && t.Amount == intent.Amount && t.TransactionDate >= from && t.TransactionDate < to
                    && t.ReconciliationStatus == ReconciliationStatus.Unmatched && t.MatchedPaymentId == null && t.MatchedJournalEntryId == null
                    && (t.AutoSettleVerdict == null || !blocked.Contains(t.AutoSettleVerdict)))
                .Select(t => t.Id).Take(2).ToListAsync(ct);
            if (lines.Count == 1)
            {
                await _bankSettle.EvaluateLineAsync(companyId, lines[0], settings?.BankAutoSettleEnabled ?? false, DateTime.UtcNow, ct);
                recorded = await LineSettledDocumentAsync(companyId, lines[0], intent.SourceId, ct);
                if (!recorded)
                    lineNote = "รายการเดินบัญชีที่ตรงยังไม่ผ่านเกณฑ์ตัดชำระเอง: " + (await _db.BankTransactions.AsNoTracking()
                        .Where(t => t.Id == lines[0] && t.CompanyId == companyId).Select(t => t.AutoSettleReason).FirstOrDefaultAsync(ct) ?? "ไม่ทราบเหตุ");
            }
            else if (lines.Count > 1) lineNote = "รายการเดินบัญชียอด/วันเดียวกันมีหลายรายการ — ระบบไม่เดา";
        }
        var verdict = SlipAutoRecordRule.Decide(facts with { BankLineConfirmed = recorded });

        if (verdict.Auto)
        {
            // ฝ่ายค้าน MED-1: เงินลงแล้ว (ตัวตัดสินเงินเข้าผูกบรรทัด + audit ในธุรกรรมของมันเอง) ⇒ ขั้นตามหลังล้ม = LogError ไม่ประทับกลับทิศ
            try
            {
                await StampAsync(companyId, intentId, null, "เงินเข้าแล้วตามรายการเดินบัญชี (ตัดชำระแล้ว) — สลิปนี้ไม่ใช่เงินก้อนที่สอง ปฏิเสธรายการนี้ได้",
                    "SlipAutoRecorded", verdict, money, ct);
                await NotifyAsync(companyId, NotificationEvents.AutopilotSlipAutoRecorded,
                    $"⚙️ ระบบบันทึกรับเงิน {intent.Amount:N2} บาท ของ {doc!.DocumentNumber} แล้ว (สลิปตรงรายการเดินบัญชี)",
                    $"{money.Reason} · ช่องทาง {SlipAutoRecordRule.ChannelLabel(sourceKind)} — ไม่ถูกต้องให้ยกเลิกการรับชำระที่เอกสาร",
                    "/pages/documents.html?openDoc=" + intent.SourceId, intent.SourceId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "บันทึกรับเงินของรายการ {Intent} สำเร็จแล้ว แต่ประทับผล/แจ้งไม่สำเร็จ — สถานะจริง = บันทึกแล้ว", intentId);
            }
            return new(true, verdict.Code, verdict.Reason);
        }
        var reason = verdict.Reason + (lineNote == null ? "" : " · " + lineNote);
        await StampAsync(companyId, intentId, verdict.Code, reason, "SlipAutoRecordBlocked", verdict, money, ct);
        // ค่าตั้งปิด = พฤติกรรม 260 เดิม (ประตูแจ้งโอนแจ้งทีมแล้ว) · อื่น ๆ = วิธีสำรองของคำสั่งเจ้าของ: แจ้งให้ยืนยันพร้อมเหตุ + ลิงก์หน้ายืนยัน
        if (verdict.Code != SlipAutoRecordRule.Codes.SettingOff)
        {
            await NoticeNeedsConfirmAsync(companyId, intentId, intent.Amount, doc?.DocumentNumber, reason, 0, ct);
            await _db.PaymentIntents.Where(i => i.Id == intentId && i.CompanyId == companyId)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.ConfirmNoticeAt, (DateTime?)DateTime.UtcNow), ct);
        }
        return new(false, verdict.Code, reason);
    }

    /// <summary>บรรทัดถูกผูกกับการชำระของเอกสารนี้แล้วไหม (ผลจริงจากตัวตัดสินเงินเข้า — ไม่เชื่อค่าที่ประทับเอง)</summary>
    private async Task<bool> LineSettledDocumentAsync(Guid companyId, Guid lineId, Guid documentId, CancellationToken ct)
    {
        var pid = await _db.BankTransactions.AsNoTracking().Where(t => t.Id == lineId && t.CompanyId == companyId).Select(t => t.MatchedPaymentId).FirstOrDefaultAsync(ct);
        return pid is Guid p && await _db.Payments.AsNoTracking().AnyAsync(x => x.Id == p && x.CompanyId == companyId && x.DocumentId == documentId && !x.IsDeleted, ct);
    }

    /// <summary>ฝ่ายค้าน MED-1: ผลจริงของรายการ (ผู้เรียกที่ล้มกลางทางใช้ตัดสินว่าจะประทับอะไร) — รายการรับเงินแล้ว หรือมีการชำระของเอกสารที่ผูกรายการเดินบัญชีหลังสลิปเข้ามา</summary>
    public async Task<bool> RecordedAsync(Guid companyId, Guid intentId, CancellationToken ct = default)
    {
        var i = await _db.PaymentIntents.AsNoTracking().Where(x => x.Id == intentId && x.CompanyId == companyId)
            .Select(x => new { x.Status, x.SourceId, x.CreatedAt }).FirstOrDefaultAsync(ct);
        if (i == null) return false;
        if (i.Status == PaymentIntentStatus.Succeeded) return true;
        return await (from p in _db.Payments.AsNoTracking()
                      join t in _db.BankTransactions.AsNoTracking() on p.Id equals t.MatchedPaymentId
                      where p.CompanyId == companyId && t.CompanyId == companyId && p.DocumentId == i.SourceId && !p.IsDeleted && p.CreatedAt >= i.CreatedAt
                      select p.Id).AnyAsync(ct);
    }

    /// <summary>LOW (ฝ่ายค้าน 282): งวดบัญชีปิด หรือเดือน ภ.พ.30 ที่ประกาศว่ายื่น/ยื่น/ล็อกแล้ว (ชุดสถานะ <c>TaxFilingLockPolicy.DeclaredOrFiledStatuses</c> ตัวเดียว)</summary>
    private async Task<bool> PeriodLockedAsync(Guid companyId, DateTime day, CancellationToken ct)
    {
        if (await _db.FiscalPeriods.AsNoTracking().AnyAsync(p => p.CompanyId == companyId && p.StartDate <= day && p.EndDate >= day && p.Status != FiscalPeriodStatus.Open, ct))
            return true;
        var statuses = TaxFilingLockPolicy.DeclaredOrFiledStatuses;
        return await _db.TaxReports.AsNoTracking().AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted && t.TaxType == TaxType.VAT
            && t.Year == day.Year && t.Month == day.Month && (statuses.Contains(t.Status) || t.FilingLockedAt != null), ct);
    }

    private async Task StampAsync(Guid companyId, Guid intentId, string? code, string? reason, string eventName, SlipAutoRecordRule.Verdict v,
        SlipMoneySource.Verdict money, CancellationToken ct)
    {
        var r = reason == null ? null : LineMessageLimits.Truncate(reason, 500);
        await _db.PaymentIntents.Where(i => i.Id == intentId && i.CompanyId == companyId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.AutoRecordCode, code).SetProperty(i => i.AutoRecordReason, r), ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = null, UserEmail = Actor, Action = AuditAction.Update, EntityType = "PaymentIntent", EntityId = intentId.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = eventName, code = v.Code, reason = v.Reason, moneyEvidence = money.Evidence.ToString(), bankAccountId = money.AccountId,
                ruleCode = SlipAutoRecordRule.RuleCode, legalReference = SlipAutoRecordRule.LegalReference, moneyRuleCode = SlipMoneySource.RuleCode, usedAi = false,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task NoticeNeedsConfirmAsync(Guid companyId, Guid intentId, decimal amount, string? documentNumber, string reason, int reminder, CancellationToken ct)
        => await NotifyAsync(companyId, NotificationEvents.AutopilotSlipNeedsConfirm,
            (reminder > 0 ? $"🔔 เตือนครั้งที่ {reminder}: " : "") + $"สลิป {amount:N2} บาท ของ {documentNumber ?? "เอกสาร"} รอยืนยัน",
            "ระบบไม่บันทึกเอง — " + reason + " · ตรวจรายการเงินเข้า แล้วกดยืนยัน (หรือปฏิเสธ) ที่หน้ารายการรับชำระ",
            ConfirmPageHref + "?intent=" + intentId, intentId, ct);

    private async Task NotifyAsync(Guid companyId, string eventKey, string title, string message, string url, Guid entityId, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(companyId, eventKey, new NotificationContext
            {
                Title = title, Message = message, ActionUrl = url, EntityType = "PaymentIntent", EntityId = entityId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ct;
            _logger.LogError(ex, "แจ้งบริษัท {Company} เรื่องสลิป ({Event}) ไม่สำเร็จ — รายการและเหตุบันทึกบนรายการชำระแล้ว", companyId, eventKey);
        }
    }

    /// <summary>เตือนซ้ำสลิปที่ยังรอยืนยัน (ผู้เรียกเดียว <c>AutopilotJob</c> ขั้น SlipConfirmReminders · ล็อกของขั้น) — ไม่ถี่ · มีเพดาน · ทุก query ผูกบริษัทของแถว ·
    /// ใบที่ถูกตัดชำระด้วยเงินก้อนอื่นแล้ว (สลิปถูกติดธง superseded) ไม่เตือน</summary>
    public async Task<int> RemindPendingAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var max = SlipMoneySource.MaxReminders;
        var off = SlipAutoRecordRule.Codes.SettingOff;
        var open = PaymentIntentPolicy.OpenStatuses;
        var superseded = DocumentSlipIntents.SupersededCode;
        var rows = await _db.PaymentIntents.AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.SourceKind == PaymentSourceKind.Document && i.MethodKind == PaymentMethodKind.ManualSlip && open.Contains(i.Status)
                && i.AutoRecordCode != null && i.AutoRecordCode != off && i.ConfirmNoticeAt != null && i.ConfirmRemindCount < max
                && (i.FailureCode == null || i.FailureCode != superseded))
            .OrderBy(i => i.ConfirmNoticeAt).Take(RemindBatch)
            .Select(i => new { i.Id, i.CompanyId, i.SourceId, i.Amount, i.AutoRecordReason, i.ConfirmNoticeAt, i.ConfirmRemindCount, i.ConfirmRemindedAt })
            .ToListAsync(ct);
        var n = 0;
        foreach (var r in rows.Where(r => SlipMoneySource.ReminderDue(r.ConfirmNoticeAt!.Value, r.ConfirmRemindCount, r.ConfirmRemindedAt, nowUtc)))
        {
            // กันส่งซ้ำข้าม instance: ขยับตัวนับแบบ compare-and-set ก่อนส่ง
            var claimed = await _db.PaymentIntents.IgnoreQueryFilters()
                .Where(i => i.Id == r.Id && i.CompanyId == r.CompanyId && i.ConfirmRemindCount == r.ConfirmRemindCount)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.ConfirmRemindCount, r.ConfirmRemindCount + 1)
                    .SetProperty(i => i.ConfirmRemindedAt, (DateTime?)nowUtc), ct);
            if (claimed == 0) continue;
            var number = await _db.Documents.AsNoTracking().IgnoreQueryFilters().Where(d => d.Id == r.SourceId && d.CompanyId == r.CompanyId)
                .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct);
            await NoticeNeedsConfirmAsync(r.CompanyId, r.Id, r.Amount, number, r.AutoRecordReason ?? "รอตรวจ", r.ConfirmRemindCount + 1, ct);
            n++;
        }
        return n;
    }
}
