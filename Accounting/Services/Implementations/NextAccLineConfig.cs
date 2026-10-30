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
        var adminTokenUndecryptable = false;
        if (_db != null)
        {
            var row = await _db.SiteSettings.AsNoTracking()
                .Select(s => new { s.LineMessagingChannelAccessToken, s.LineMessagingChannelSecret, s.LineMessagingBasicId, s.LineMessagingBotUserId, s.LineMessagingGroupId })
                .FirstOrDefaultAsync(ct);
            // ฝ่ายค้าน (B+D) ข้อ 3: มี token เก็บไว้แต่ถอดไม่ได้ ⇒ ไม่ถอยไป config (อาจเป็นคนละ OA)
            adminTokenUndecryptable = row != null && !string.IsNullOrWhiteSpace(row.LineMessagingChannelAccessToken)
                && !_secrets.IsUsable(row.LineMessagingChannelAccessToken);
            if (row != null)
                admin = new NextAccLineValues(_secrets.Unprotect(row.LineMessagingChannelAccessToken), _secrets.Unprotect(row.LineMessagingChannelSecret),
                    row.LineMessagingBasicId, row.LineMessagingBotUserId, row.LineMessagingGroupId);
        }
        _loaded = NextAccLineConfigRule.Merge(admin, FromConfig(_config), adminTokenUndecryptable);
        return _loaded;
    }

    /// <summary>ค่าใน config <c>Line:*</c> (appsettings / env <c>Line__ChannelAccessToken</c> ฯลฯ) — ที่เดียวของระบบ</summary>
    public static NextAccLineValues FromConfig(IConfiguration config) => new(
        config["Line:ChannelAccessToken"], config["Line:ChannelSecret"], config["Line:BasicId"], config["Line:BotUserId"], config["Line:GroupId"]);
}
