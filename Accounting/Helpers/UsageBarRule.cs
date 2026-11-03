using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 230 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §E แพ็ก 6 · D-4 · F2 ข้อ 5 "server computes · page displays") — <b>หลอดการใช้งานของหน้า "ใช้ไปเท่าไร"</b> คิดที่เซิร์ฟเวอร์ตัวเดียว ·
/// เดิม <c>pages/usage.html</c> คิด % เอง + เกณฑ์ "ไม่จำกัด" พิมพ์ในหน้า (<c>≥ 99999</c> / <c>≥ 999</c>) + สีเตือนของตัวเอง · pure
/// <para>เกณฑ์ไม่จำกัด (ค่าแทนที่ seed/แอดมินใช้): ผู้ใช้ ≥ <see cref="UnlimitedUsersAt"/> · เอกสาร ≥ <see cref="UnlimitedCountAt"/> หรือ ≤ 0 (ไม่ตั้ง — ตัวนับเอกสารไม่บังคับ) ·
/// OCR &lt; 0 · พื้นที่ ≥ <see cref="QuotaResources.UnlimitedBytes"/> · ระดับเตือนเดียวกับข้อความเตือนบนหน้า (≥ <see cref="WarnPercent"/>% เหลือง · ≥ <see cref="DangerPercent"/>% แดง)</para>
/// </summary>
public static class UsageBarRule
{
    public const int WarnPercent = 70;
    public const int DangerPercent = 90;
    public const int UnlimitedUsersAt = 999;
    public const int UnlimitedCountAt = 99999;

    /// <summary>1 หลอด — ทุกช่องคิดแล้ว (หน้าเว็บแสดงอย่างเดียว) · <see cref="Level"/> 0 ปกติ · 1 ใกล้เต็ม · 2 เต็ม/เกือบเต็ม</summary>
    public sealed record Bar(string Key, string Label, long Used, long? Limit, bool Unlimited, decimal Percent, int Level,
        string UsedText, string LimitText, string? Extra);

    /// <summary>% ใช้ไป (ทศนิยม 1 ตำแหน่ง · AwayFromZero · ไม่เกิน 100) — ไม่จำกัด/เพดาน 0 ที่ยังไม่ใช้ = 0 · เพดาน 0 แต่ใช้แล้ว = 100</summary>
    public static decimal Percent(long used, long? limit, bool unlimited)
    {
        if (unlimited || limit is not long max) return 0m;
        if (max <= 0) return used > 0 ? 100m : 0m;
        var p = Math.Round((decimal)used * 100m / max, 1, MidpointRounding.AwayFromZero);
        return p > 100m ? 100m : p < 0m ? 0m : p;
    }

    public static int Level(decimal percent) => percent >= DangerPercent ? 2 : percent >= WarnPercent ? 1 : 0;

    private static string BytesText(long b)
    {
        if (b >= QuotaResources.BytesPerGb) return $"{b / (double)QuotaResources.BytesPerGb:N1} GB";
        if (b >= 1024 * 1024) return $"{b / (1024.0 * 1024):N1} MB";
        if (b >= 1024) return $"{b / 1024.0:N0} KB";
        return $"{b:N0} B";
    }

    public static Bar Users(long used, int max)
    {
        var unlimited = max <= 0 || max >= UnlimitedUsersAt;
        return Make("users", "ผู้ใช้งาน", used, max, unlimited, $"{used:N0}", unlimited ? "ไม่จำกัด" : $"{max:N0}", null);
    }

    public static Bar Documents(long used, int max)
    {
        var unlimited = max <= 0 || max >= UnlimitedCountAt;
        return Make("documents", "เอกสาร / เดือน", used, max, unlimited, $"{used:N0}", unlimited ? "ไม่จำกัด" : $"{max:N0}", null);
    }

    /// <summary>OCR — เพดาน = โควตาแพ็กเกจ + โบนัส + เครดิตที่ซื้อคงเหลือ (เท่าที่ตัวนับ OCR ใช้จริง) · &lt; 0 = ไม่จำกัด</summary>
    public static Bar Ocr(long used, int planMax, int bonus, int creditRemaining)
    {
        var unlimited = planMax < 0;
        var limit = unlimited ? 0 : (long)planMax + Math.Max(0, bonus) + Math.Max(0, creditRemaining);
        var extra = (bonus > 0 ? $" รวมโบนัส {bonus:N0}" : "") + (creditRemaining > 0 ? $" · เครดิตที่ซื้อคงเหลือ {creditRemaining:N0}" : "");
        return Make("ocr", "OCR สแกน / เดือน", used, limit, unlimited, $"{used:N0}", unlimited ? "ไม่จำกัด" : $"{limit:N0}",
            extra.Length == 0 ? null : extra.TrimStart(' ', '·'));
    }

    public static Bar Storage(long usedBytes, long maxBytes)
    {
        var unlimited = maxBytes >= QuotaResources.UnlimitedBytes;
        return Make("storage", "พื้นที่จัดเก็บ", usedBytes, maxBytes, unlimited, BytesText(usedBytes), unlimited ? "ไม่จำกัด" : BytesText(maxBytes), null);
    }

    private static Bar Make(string key, string label, long used, long limit, bool unlimited, string usedText, string limitText, string? extra)
    {
        var pct = Percent(used, limit, unlimited);
        return new Bar(key, label, used, unlimited ? null : limit, unlimited, pct, unlimited ? 0 : Level(pct), usedText, limitText, extra);
    }

    /// <summary>วันที่เหลือของแพ็กเกจ (ปฏิทินไทย · ไม่ติดลบ) — เดิมหน้าเว็บคิดจากนาฬิกาเครื่องผู้ใช้</summary>
    public static int DaysLeft(DateTime endUtc, DateTime nowUtc)
        => Math.Max(0, (endUtc.AddHours(7).Date - nowUtc.AddHours(7).Date).Days);
}
