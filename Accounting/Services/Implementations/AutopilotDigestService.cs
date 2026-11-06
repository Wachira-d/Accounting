using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **สรุป Autopilot ประจำวันถึงทีม (E-15)** (รอบ 252 WP-12A · AUTOPILOT §10.2 · คำตัดสิน Q7 "สำเร็จ = digest 08:40")
/// <para>ประกอบจากแถวสถานะจริงตอนส่ง (<see cref="IAutopilotService.DigestFactsAsync"/> — ไม่มีตาราง digest ซ้ำ) · ข้อความจาก <see cref="AutopilotNotice.ComposeDigest"/> ·
/// ส่งผ่านเครื่องแจ้งเตือนกลาง (<see cref="NotificationEvents.AutopilotDailyDigest"/> · ค่าตั้งต้น กระดิ่ง + อีเมลถึงเจ้าของ · ปิดได้ที่ตารางแจ้งเตือน) ·
/// watermark ต่อบริษัท <c>CompanySettings.AutopilotDigestLastSentOn</c> ประทับแบบ compare-and-set <b>ก่อน</b>ส่ง (หลายเครื่อง = ส่งครั้งเดียว · ล้มหลังประทับ = ไม่ส่งซ้ำ
/// ในวันนั้น — ยอมพลาดหนึ่งฉบับดีกว่าส่งซ้ำ) · ไม่มีอะไรรายงาน ⇒ ประทับแต่ไม่ส่ง (ไม่ส่งอีเมลว่าง)</para>
/// <para>บริษัทที่ "มีกิจกรรม Autopilot" เท่านั้น: มีค่าตั้ง Autopilot ที่เปิด หรือมีรายการประจำใช้งาน หรือมีงานหลังรับชำระ/ข้อเสนอเงินโอน — บริษัทที่ไม่ใช้เลยไม่ได้อีเมล</para>
/// </summary>
public class AutopilotDigestService
{
    public const int BatchSize = 200;

    private readonly AccountingDbContext _db;
    private readonly IAutopilotService _autopilot;
    private readonly INotificationEngine _notify;
    private readonly ILogger<AutopilotDigestService> _logger;

    public AutopilotDigestService(AccountingDbContext db, IAutopilotService autopilot, INotificationEngine notify, ILogger<AutopilotDigestService> logger)
    {
        _db = db;
        _autopilot = autopilot;
        _notify = notify;
        _logger = logger;
    }

    /// <summary>หนึ่งรอบ — คืนจำนวนบริษัทที่ส่งสรุป</summary>
    public async Task<int> RunOnceAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var today = AutopilotNotice.BangkokDate(nowUtc);
        var since30 = nowUtc.AddDays(-30);
        // บริษัทที่ใช้ Autopilot (มีสิ่งที่ระบบทำแทน) · ยังไม่ส่งวันนี้
        // ฝ่ายค้าน LOW: กรองเวลาที่ถึงแล้วใน SQL ก่อน Take (เดิมบริษัทที่ยังไม่ถึงเวลา 200 แถวแรกบังบริษัทที่ถึงแล้ว) — ค่าเก็บรูป "HH:mm" เสมอ (NormalizeDigestTime)
        // ⇒ เทียบสตริงได้ · ตัวตัดสินจริงยังเป็น AutopilotNotice.DigestDue ข้างล่าง · โหมดแปลงใบเสนอราคา "Off" ไม่นับว่าใช้ Autopilot
        var nowHm = nowUtc.AddHours(7).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var defaultHm = AutopilotNotice.DefaultDigestTime.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
        var candidates = await _db.CompanySettings.AsNoTracking()
            .Where(s => (s.AutopilotDigestLastSentOn == null || s.AutopilotDigestLastSentOn < today)
                && string.Compare(s.AutopilotDigestLocalTime ?? defaultHm, nowHm) <= 0
                && (s.BankAutoSettleEnabled || (s.QuotationAcceptAutopilotDefault != null && s.QuotationAcceptAutopilotDefault != "Off") || s.PostPayEmailReceipt
                    || _db.RecurringTransactions.Any(r => r.CompanyId == s.CompanyId && !r.IsDeleted && r.Status == Models.Enums.RecurringStatus.Active)
                    || _db.BankTransactions.Any(t => t.CompanyId == s.CompanyId && t.AutoSettleVerdict != null && t.CreatedAt >= since30)))
            .OrderBy(s => s.CompanyId).Take(BatchSize)
            .Select(s => new { s.CompanyId, s.AutopilotDigestLocalTime, s.AutopilotDigestLastSentOn }).ToListAsync(ct);
        var sent = 0;
        foreach (var c in candidates)
        {
            if (!AutopilotNotice.DigestDue(nowUtc, c.AutopilotDigestLocalTime, c.AutopilotDigestLastSentOn)) continue;
            try
            {
                if (await SendForCompanyAsync(c.CompanyId, nowUtc, ct)) sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "สรุป Autopilot ประจำวันของบริษัท {Company} ล้ม", c.CompanyId);
            }
        }
        return sent;
    }

    /// <summary>ส่งสรุปของบริษัทเดียว (ผู้เรียก: <see cref="RunOnceAsync"/> · เทสต์) — คืน true เมื่อส่งจริง</summary>
    public async Task<bool> SendForCompanyAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var today = AutopilotNotice.BangkokDate(nowUtc);
        // compare-and-set: เครื่องที่ประทับได้คือคนส่ง (อีกเครื่องได้ 0 แถว)
        var claimed = await _db.CompanySettings
            .Where(s => s.CompanyId == companyId && (s.AutopilotDigestLastSentOn == null || s.AutopilotDigestLastSentOn < today))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.AutopilotDigestLastSentOn, (DateTime?)today), ct);
        if (claimed == 0) return false;

        // ช่วงที่สรุป: ตั้งแต่เวลาส่งของวันก่อน (เวลาไทยเดียวกัน) — ครั้งแรก/ขาดหลายวัน = 24 ชั่วโมงล่าสุด
        var from = nowUtc.AddDays(-1);
        var facts = await _autopilot.DigestFactsAsync(companyId, from, nowUtc, ct);
        var composed = AutopilotNotice.ComposeDigest(today, facts);
        if (composed is not { } m) return false;
        await _notify.DispatchAsync(companyId, NotificationEvents.AutopilotDailyDigest, new NotificationContext
        {
            Title = m.Title, Message = m.Message, ActionUrl = "/pages/autopilot.html", EntityType = "Company", EntityId = companyId,
        });
        return true;
    }
}
