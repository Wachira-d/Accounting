using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ผู้ให้บริการวิดีโอที่ระบบฝังได้ — **ต้องตรงกับ `frame-src` ใน
/// `SecurityMiddleware`** มิฉะนั้นเบราว์เซอร์บล็อกเงียบและผู้ใช้เห็นแค่กรอบว่าง
/// (บทเรียน CLAUDE.md: CSP เป็น allow-list · สคริปต์/เฟรมที่ไม่ได้ระบุถูกบล็อก
/// โดยไม่มี error ให้ไล่)</summary>
public enum HelpMediaProvider
{
    /// <summary>ไฟล์ที่อัปโหลดเข้าระบบเอง (เสิร์ฟจาก /uploads/help-media)</summary>
    SelfHosted = 0,
    YouTube = 1,
    Facebook = 2,
    TikTok = 3,
    /// <summary>ลิงก์ภายนอกอื่น — เปิดแท็บใหม่ ไม่ฝัง (ฝังไม่ได้เพราะ CSP)</summary>
    ExternalLink = 9,
}

/// <summary>
/// แปลงลิงก์วิดีโอที่ผู้ดูแลวางมา → URL สำหรับ <c>&lt;iframe&gt;</c>
///
/// <para><b>ทำไมต้องมี</b>: ผู้ใช้ก๊อปลิงก์จากแถบที่อยู่ (<c>watch?v=…</c>,
/// <c>youtu.be/…</c>, <c>/shorts/…</c>) ซึ่ง**ฝังไม่ได้** — ต้องเป็น
/// <c>/embed/{id}</c> เท่านั้น ถ้าไม่แปลงให้ ผู้ดูแลจะเห็นกรอบว่างแล้วเข้าใจว่า
/// ระบบพัง ทั้งที่ลิงก์ถูกต้องในมุมของเขา</para>
///
/// <para><b>pure ทั้งหมด</b> (ไม่แตะ DB/เครือข่าย) จึงเทสต์ได้ตามกฎเหล็ก #4 G</para>
/// </summary>
public static class HelpMediaEmbed
{
    private static readonly Regex YouTubeId = new(
        @"(?:youtube\.com/(?:watch\?(?:.*&)?v=|embed/|shorts/|live/)|youtu\.be/)([A-Za-z0-9_-]{6,20})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TikTokId = new(
        @"tiktok\.com/@[^/]+/video/(\d{6,25})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>เดาผู้ให้บริการจาก URL — ใช้ตอนผู้ดูแลวางลิงก์เพื่อไม่ต้องให้เลือกเอง
    /// (เลือกผิดแล้ว embed พังโดยไม่มีอะไรบอก)</summary>
    public static HelpMediaProvider DetectProvider(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return HelpMediaProvider.SelfHosted;
        var u = url.Trim().ToLowerInvariant();
        if (u.StartsWith("/uploads/")) return HelpMediaProvider.SelfHosted;
        if (u.Contains("youtube.com") || u.Contains("youtu.be")) return HelpMediaProvider.YouTube;
        if (u.Contains("facebook.com") || u.Contains("fb.watch")) return HelpMediaProvider.Facebook;
        if (u.Contains("tiktok.com")) return HelpMediaProvider.TikTok;
        return HelpMediaProvider.ExternalLink;
    }

    /// <summary>URL ที่ใส่ใน <c>iframe src</c> ได้จริง — คืน <c>null</c> เมื่อฝังไม่ได้
    ///
    /// <para><b>null ไม่ใช่ error</b> แต่แปลว่า "ให้เปิดเป็นลิงก์แทน" — ฝั่ง UI ต้อง
    /// วาดปุ่ม "เปิดในแท็บใหม่" ไม่ใช่กรอบว่าง (ห้ามเงียบ)</para></summary>
    public static string? ToEmbedUrl(string? url, HelpMediaProvider provider)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();

        switch (provider)
        {
            case HelpMediaProvider.SelfHosted:
                // ไฟล์ของเราเอง — เล่นด้วย <video> ไม่ใช่ iframe จึงคืน path ตรง ๆ
                return u.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase) ? u : null;

            case HelpMediaProvider.YouTube:
            {
                var m = YouTubeId.Match(u);
                // ไม่ตัด query อื่นทิ้งทั้งหมด แต่ก็ไม่ส่งต่อ — พารามิเตอร์อย่าง
                // `t=` ของหน้า watch ใช้กับ /embed ไม่ได้ตรง ๆ (ต้องเป็น start=)
                return m.Success ? $"https://www.youtube.com/embed/{m.Groups[1].Value}" : null;
            }

            case HelpMediaProvider.Facebook:
                // Facebook ต้องห่อผ่าน plugin endpoint และ **encode URL เดิมทั้งอัน**
                return $"https://www.facebook.com/plugins/video.php?href={Uri.EscapeDataString(u)}&show_text=false";

            case HelpMediaProvider.TikTok:
            {
                var m = TikTokId.Match(u);
                return m.Success ? $"https://www.tiktok.com/embed/v2/{m.Groups[1].Value}" : null;
            }

            default:
                return null;   // ลิงก์ภายนอกอื่น = เปิดแท็บใหม่ ไม่ฝัง
        }
    }

    /// <summary>ภาพปกอัตโนมัติ (เฉพาะ YouTube ที่มี URL ปกแบบเดาได้) —
    /// ที่เหลือคืน null ให้ UI ใช้ไอคอนแทน **ห้ามเดาเป็นภาพของเจ้าอื่น**</summary>
    public static string? ThumbnailUrl(string? url, HelpMediaProvider provider)
    {
        if (provider != HelpMediaProvider.YouTube || string.IsNullOrWhiteSpace(url)) return null;
        var m = YouTubeId.Match(url);
        return m.Success ? $"https://i.ytimg.com/vi/{m.Groups[1].Value}/hqdefault.jpg" : null;
    }
}
