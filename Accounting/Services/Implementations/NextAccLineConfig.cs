using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ดู <see cref="INextAccLineConfig"/> — ไฟล์เดียวที่อ่าน config <c>Line:*</c> (fallback ของ deployment เดิม)</summary>
public class NextAccLineConfig : INextAccLineConfig
{
    private readonly AccountingDbContext? _db;
    private readonly IConfiguration _config;
    private readonly ISecretProtector _secrets;
    private NextAccLineSettings? _loaded;

    /// <param name="db">null = ใช้ config อย่างเดียว (เทสต์ที่ไม่มีฐาน)</param>
    public NextAccLineConfig(AccountingDbContext? db, IConfiguration config, ISecretProtector secrets)
    {
        _db = db; _config = config; _secrets = secrets;
    }

    public async Task<NextAccLineSettings> GetAsync(CancellationToken ct = default)
    {
        if (_loaded != null) return _loaded;
        NextAccLineValues? admin = null;
        string? adminTokenUnusableReason = null;
        string? adminSecretUnusableReason = null;
        var opsAlertsToGroup = true;
        if (_db != null)
        {
            var row = await _db.SiteSettings.AsNoTracking()
                .Select(s => new { s.LineMessagingChannelAccessToken, s.LineMessagingChannelSecret, s.LineMessagingBasicId, s.LineMessagingBotUserId, s.LineMessagingGroupId,
                    s.LineMessagingOpsAlertsToGroup })
                .FirstOrDefaultAsync(ct);
            if (row != null) opsAlertsToGroup = row.LineMessagingOpsAlertsToGroup;   // รอบ 232: แอดมินปิดช่องกลุ่มกลางสำหรับเรื่องของระบบได้
            // ฝ่ายค้าน (B+D) ข้อ 3: มี token เก็บไว้แต่ถอดไม่ได้ ⇒ ไม่ถอยไป config (อาจเป็นคนละ OA) · รอบ 231: เหตุมาจากตัวตรวจจริง ไม่ใช่ "คีย์ถูกเปลี่ยน" ตายตัว
            // (N2: Inspect ครั้งเดียวต่อช่อง · S3: secret ใช้ไม่ได้ก็ดังเท่ากัน — ไม่ปล่อย webhook 401 เงียบ)
            if (row != null && !string.IsNullOrWhiteSpace(row.LineMessagingChannelAccessToken))
                adminTokenUnusableReason = SecretWarnings.ReasonFor(_secrets.Inspect(row.LineMessagingChannelAccessToken));
            if (row != null && !string.IsNullOrWhiteSpace(row.LineMessagingChannelSecret))
                adminSecretUnusableReason = SecretWarnings.ReasonFor(_secrets.Inspect(row.LineMessagingChannelSecret));
            if (row != null)
                admin = new NextAccLineValues(_secrets.Unprotect(row.LineMessagingChannelAccessToken), _secrets.Unprotect(row.LineMessagingChannelSecret),
                    row.LineMessagingBasicId, row.LineMessagingBotUserId, row.LineMessagingGroupId);
        }
        _loaded = NextAccLineConfigRule.Merge(admin, FromConfig(_config), adminTokenUnusableReason, adminSecretUnusableReason) with { OpsAlertsToGroup = opsAlertsToGroup };
        return _loaded;
    }

    public async Task<IReadOnlyList<string>> PlatformGroupIdsAsync(CancellationToken ct = default)
    {
        string? admin = null;
        if (_db != null)
            admin = await _db.SiteSettings.AsNoTracking().Select(s => s.LineMessagingGroupId).FirstOrDefaultAsync(ct);
        return NextAccLineConfigRule.PlatformGroupIds(admin, FromConfig(_config).GroupId);
    }

    /// <summary>ค่าใน config <c>Line:*</c> (appsettings / env <c>Line__ChannelAccessToken</c> ฯลฯ) — ที่เดียวของระบบ</summary>
    public static NextAccLineValues FromConfig(IConfiguration config) => new(
        config["Line:ChannelAccessToken"], config["Line:ChannelSecret"], config["Line:BasicId"], config["Line:BotUserId"], config["Line:GroupId"]);
}
