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

    /// <summary>รอบ 227 (PLATFORM_OMISE_AUDIT B-4): ค่าใน appsettings ที่มากับซอร์ส — โดเมนที่<b>ไม่ใช่ของเรา</b> (ที่มาของบั๊กเด้งไป app.nextacc.com)</summary>
    public const string ShippedDefault = "https://app.nextacc.com";

    /// <summary>รอบ 227: "ตั้ง URL ของระบบแล้วจริง" = ผู้ดูแลตั้งในหน้า admin หรือ deployment ตั้ง <c>App:BaseUrl</c> เป็นค่าอื่นที่ไม่ใช่ค่าที่มากับซอร์ส ·
    /// ใช้กับด่านเปิดรับเงินค่าบริการจริง (PaymentLiveGate.PlatformRefusal) — ค่าที่มากับซอร์ส ≠ ตั้งแล้ว</summary>
    public static bool IsConfigured(string? adminBaseUrl, string? configBaseUrl)
        => !string.IsNullOrWhiteSpace(adminBaseUrl)
           || (!string.IsNullOrWhiteSpace(configBaseUrl)
               && !string.Equals(configBaseUrl.Trim().TrimEnd('/'), ShippedDefault, StringComparison.OrdinalIgnoreCase));

    public static async Task<string?> ResolveAsync(AccountingDbContext db, IConfiguration? config, CancellationToken ct = default)
    {
        var admin = await db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.AppBaseUrl).FirstOrDefaultAsync(ct);
        return Pick(admin, config?["App:BaseUrl"]);
    }
}
