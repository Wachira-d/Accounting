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
/// **รอบ 282 — สลิปที่ผูกใบเดียวแล้ว ⇒ บันทึกรับเงินเอง หรือ เข้าหน้ายืนยันของผู้ขาย** (คำตัดสิน 282-1..282-6)
/// <list type="bullet">
/// <item>ผู้เรียกเดียว: <c>LineChatSlipService</c> (ทุกทางเข้าของสลิปค่าสินค้า/บริการ: แชต/กลุ่ม OA บริษัท · กลุ่ม OA กลาง · อีเมล) หลังแจ้งโอนผ่านประตูเดิม</item>
/// <item>ตัวตัดสิน: แหล่งเงิน <see cref="SlipMoneySource.Decide"/> (ผู้รับบนสลิป ↔ บัญชีของบริษัท) → <see cref="SlipAutoRecordRule.Decide"/> (ค่าตั้ง · ผู้ส่ง · ใบเดียว · QR · ref ไม่ซ้ำ · วันที่ · บัญชี)</item>
/// <item>ผ่าน ⇒ เส้นยืนยันตัวเดียว <see cref="SlipIntentConfirmService.ConfirmAsync"/> (ผู้ยืนยัน <c>slip-auto</c> · แหล่งเหตุการณ์ System) ⇒ handler ⇒ <c>CreatePaymentAsync</c>
/// (ใบเสร็จ/ใบกำกับ ณ วันรับเงิน 275-C · แจ้งคู่ค้า 281 ในขั้นหลัง commit ของมันเอง)</item>
/// <item>ไม่ผ่าน ⇒ รายการรอยืนยันเดิม (คิวเดียวของ 260 · หน้ารายการรับชำระ) + เหตุบนรายการ + แจ้งผู้ขาย + เตือนซ้ำ (<see cref="SlipMoneySource.ReminderDue"/>) — ไม่ประทับ "ชำระแล้ว"</item>
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

    public SlipAutoRecordService(AccountingDbContext db, IPaymentIntentService intents, ILogger<SlipAutoRecordService> logger,
        Accounting.Services.Storage.IFileStorage? storage = null, INotificationEngine? notify = null)
    { _db = db; _intents = intents; _logger = logger; _storage = storage; _notify = notify; }

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
        var settingOn = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => (bool?)s.SlipAutoRecordEnabled).FirstOrDefaultAsync(ct) ?? false;

        // เลขอ้างอิงจาก QR ตรวจสลิป (CRC ถูก) = เลขของธนาคาร ⇒ ใช้กันซ้ำได้ (ต่างจาก ref ของ OCR — 260 ฝ่ายค้าน M1)
        var qrRef = reading.Qr == null ? null : SlipEvidenceRule.NormalizeRef(reading.Qr.TransactionRef);
        var dupStatuses = SlipEvidenceRule.DuplicateStatuses;
        var refDuplicate = qrRef != null && await _db.PaymentIntents.AsNoTracking()
            .AnyAsync(i => i.CompanyId == companyId && i.Id != intentId && i.SlipBankRef == qrRef && dupStatuses.Contains(i.Status), ct);

        var receiving = await SlipReceivingAccount.ResolveAsync(_db, _storage?.Locator.AttachmentsRoot, intent, null, ct);
        var payee = SlipReceivingAccount.SlipPayeeMatch(intent.SlipReadJson, receiving.Accounts);
        var money = SlipMoneySource.Decide(null, payee, receiving.Usable.Select(a => a.Id).ToList());
        var sug = receiving.Decision.Suggested;
        var verdict = SlipAutoRecordRule.Decide(new SlipAutoRecordRule.Facts(
            SettingOn: settingOn, SenderVerified: senderVerified, SingleMatch: singleMatch && doc != null, QrVerified: qrRef != null, RefDuplicate: refDuplicate,
            SlipDateTh: reading.Date, DocumentDateTh: doc?.DocumentDate ?? DateTime.MaxValue, Money: money,
            ReceivingBank: receiving.Decision.BankAccountId, ReceivingDocChannelBank: sug.InheritsBank ? sug.DisplayBankAccountId : null,
            ReceivingProblem: receiving.Decision.Problem, NowUtc: DateTime.UtcNow));

        if (verdict.Auto)
        {
            // เลขอ้างอิงของธนาคารบนรายการเดียวกับที่ยืนยัน (กันซ้ำทุกช่องทาง + ตัวระบุตัวให้ BankReceiptAutoSettle)
            if (intent.SlipBankRef == null)
                await _db.PaymentIntents.Where(i => i.Id == intentId && i.CompanyId == companyId && i.SlipBankRef == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.SlipBankRef, qrRef), ct);
            var outcome = await new SlipIntentConfirmService(_db, _intents).ConfirmAsync(companyId, intent, reading.Date, money.AccountId, Guid.Empty,
                Actor + ":" + SlipAutoRecordRule.RuleCode, SlipAutoRecordRule.ConfirmReason(SlipAutoRecordRule.ChannelLabel(sourceKind)),
                _storage?.Locator.AttachmentsRoot, ct, PaymentEventSource.System);
            if (outcome.Ok)
            {
                await StampAsync(companyId, intentId, null, null, "SlipAutoRecorded", verdict, money, ct);
                await NotifyAsync(companyId, NotificationEvents.AutopilotSlipAutoRecorded,
                    $"⚙️ ระบบบันทึกรับเงิน {intent.Amount:N2} บาท ของ {doc!.DocumentNumber} จากสลิปเองแล้ว",
                    $"{money.Reason} · ช่องทาง {SlipAutoRecordRule.ChannelLabel(sourceKind)} — ตรวจกับรายการเดินบัญชี · ไม่ถูกต้องให้ยกเลิกการรับชำระที่เอกสาร",
                    "/pages/documents.html?openDoc=" + intent.SourceId, intent.SourceId, ct);
                return new(true, verdict.Code, verdict.Reason);
            }
            verdict = new SlipAutoRecordRule.Verdict(false, SlipAutoRecordRule.Codes.ConfirmFailed, "บันทึกรับเงินเองไม่สำเร็จ: " + outcome.Message);
        }
        await StampAsync(companyId, intentId, verdict.Code, verdict.Reason, "SlipAutoRecordBlocked", verdict, money, ct);
        // ค่าตั้งปิด = พฤติกรรม 260 เดิม (ประตูแจ้งโอนแจ้งทีมแล้ว) · อื่น ๆ = วิธีสำรองของคำสั่งเจ้าของ: แจ้งให้ยืนยันพร้อมเหตุ + ลิงก์หน้ายืนยัน
        if (verdict.Code != SlipAutoRecordRule.Codes.SettingOff)
        {
            await NoticeNeedsConfirmAsync(companyId, intentId, intent.Amount, doc?.DocumentNumber, verdict.Reason, 0, ct);
            await _db.PaymentIntents.Where(i => i.Id == intentId && i.CompanyId == companyId)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.ConfirmNoticeAt, (DateTime?)DateTime.UtcNow), ct);
        }
        return new(false, verdict.Code, verdict.Reason);
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
            "ระบบไม่บันทึกเอง — " + reason + " · ตรวจรูปสลิป/บัญชีที่เงินเข้า แล้วกดยืนยัน (หรือปฏิเสธ) ที่หน้ารายการรับชำระ",
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

    /// <summary>เตือนซ้ำสลิปที่ยังรอยืนยัน (ผู้เรียกเดียว <c>AutopilotJob</c> ขั้น SlipConfirmReminders · ล็อกของขั้น) — ไม่ถี่ · มีเพดาน · ทุก query ผูกบริษัทของแถว</summary>
    public async Task<int> RemindPendingAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var max = SlipMoneySource.MaxReminders;
        var off = SlipAutoRecordRule.Codes.SettingOff;
        var open = PaymentIntentPolicy.OpenStatuses;
        var rows = await _db.PaymentIntents.AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.SourceKind == PaymentSourceKind.Document && i.MethodKind == PaymentMethodKind.ManualSlip && open.Contains(i.Status)
                && i.AutoRecordCode != null && i.AutoRecordCode != off && i.ConfirmNoticeAt != null && i.ConfirmRemindCount < max)
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
