using Accounting.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Accounting.Helpers;

/// <summary>รอบ 221 — URL ของระบบ (ตัวตั้งตัวเดียว): ค่าที่ผู้ดูแลตั้งในหน้า admin (<c>SiteSettings.AppBaseUrl</c>) ก่อน
/// แล้วจึง appsettings <c>App:BaseUrl</c> · คืนแบบไม่มี "/" ท้าย · ไม่รู้ = null (ไม่แต่งโดเมน)
/// <para>ที่มา: ทดลองจ่ายแล้ว Omise เด้งไป app.nextacc.com (ค่าเริ่มต้นใน appsettings — โดเมนที่ไม่ใช่ของเรา) และลิงก์ใน LINE
/// (สลิปเงินเดือน · เอกสาร · สรุปรายวัน · webhook OA ลูกค้า) อ่านแต่ appsettings เช่นกัน</para></summary>
public static class SystemBaseUrl
{
    public static string? Pick(string? adminBaseUrl, string? configBaseUrl)
    {
        var v = !string.IsNullOrWhiteSpace(adminBaseUrl) ? adminBaseUrl : configBaseUrl;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim().TrimEnd('/');
    }

    public static async Task<string?> ResolveAsync(AccountingDbContext db, IConfiguration? config, CancellationToken ct = default)
    {
        var admin = await db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.AppBaseUrl).FirstOrDefaultAsync(ct);
        return Pick(admin, config?["App:BaseUrl"]);
    }
}
