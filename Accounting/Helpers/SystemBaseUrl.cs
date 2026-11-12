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

    /// <summary>รอบ 232: origin สาธารณะสำหรับ URL ที่ระบบภายนอกต้องเรียกกลับ (webhook ของผู้ให้บริการรับชำระ) — URL ของระบบที่ตั้งไว้ก่อน ·
    /// ไม่ได้ตั้ง ⇒ scheme/host ของคำขอ · ที่มา: หน้าตั้งค่าแสดง <c>Request.Host</c> ซึ่งหลัง reverse proxy เป็นชื่อภายใน (ForwardedHeaders ของเรา
    /// ส่งต่อแค่ For/Proto ไม่ส่ง Host) ⇒ ผู้ดูแลคัดลอก URL ที่ผู้ให้บริการเรียกไม่ถึงไปตั้งในแดชบอร์ด</summary>
    public static string PublicOrigin(string? resolvedBaseUrl, string requestScheme, string requestHost)
    {
        // ฝ่ายค้าน 232 B2: ค่าที่มากับซอร์ส (app.nextacc.com) ≠ ตั้งแล้ว — ตัดสินด้วย IsConfigured ตัวเดียว ผู้เรียกไม่ต้องเช็คเอง
        if (IsConfigured(null, resolvedBaseUrl)
            && Uri.TryCreate(resolvedBaseUrl!.Trim(), UriKind.Absolute, out var b)
            && (b.Scheme == Uri.UriSchemeHttps || b.Scheme == Uri.UriSchemeHttp))
            return b.GetLeftPart(UriPartial.Authority);
        return $"{requestScheme}://{requestHost}";
    }

    /// <summary>รอบ 240: origin สาธารณะของลิงก์ที่ส่งถึงลูกค้า (ลิงก์เซ็น/ยอมรับ) จากคำขอของผู้ใช้ — URL ของระบบที่ตั้งไว้ก่อน · ไม่ได้ตั้ง ⇒ ของคำขอ
    /// (เดิม DocumentController ประกอบจาก <c>Request.Host</c> ตรง ๆ — หลัง reverse proxy ได้ชื่อภายใน)</summary>
    public static async Task<string> OriginForRequestAsync(AccountingDbContext db, IConfiguration? config, string requestScheme, string requestHost, CancellationToken ct = default)
        => PublicOrigin(await ResolveAsync(db, config, ct), requestScheme, requestHost);

    public static async Task<string?> ResolveAsync(AccountingDbContext db, IConfiguration? config, CancellationToken ct = default)
    {
        var admin = await db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.AppBaseUrl).FirstOrDefaultAsync(ct);
        return Pick(admin, config?["App:BaseUrl"]);
    }

    /// <summary>รอบ 260-3: "ตั้ง URL ของระบบแล้วจริง" จากฐาน + appsettings (ตัวตัดสิน <see cref="IsConfigured"/>) — ผู้อ่าน: การ์ดความพร้อมหน้ารับชำระออนไลน์ ·
    /// สูตรเดียวกับ <c>RecurringAutopilotService.LoadSharedFactsAsync</c> (ผ่าน <see cref="IsConfigured"/> ตัวเดียว)</summary>
    public static async Task<bool> IsConfiguredAsync(AccountingDbContext db, IConfiguration? config, CancellationToken ct = default)
    {
        var admin = await db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.AppBaseUrl).FirstOrDefaultAsync(ct);
        return IsConfigured(admin, config?["App:BaseUrl"]);
    }
}
