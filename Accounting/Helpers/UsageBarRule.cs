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
    /// <summary>รอบ 231: เกณฑ์เตือนย้ายไป <see cref="QuotaWarnPolicy"/> (ค่าตั้ง <c>PlatformBillingSettings.QuotaWarnPercents</c> · ตั้งต้น 80/90/100) — ค่าคงที่เดิม 70/90 เลิกใช้</summary>
    public const int UnlimitedUsersAt = 999;
    public const int UnlimitedCompaniesAt = 99;
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

    /// <summary>ระดับของหลอด (0/1/2) — ตัวตัดสินเดียว <see cref="QuotaWarnPolicy.Severity"/> · <paramref name="percents"/> null = ค่าตั้งต้น</summary>
    public static int Level(long used, long? limit, bool unlimited, IReadOnlyList<int>? percents = null)
        => unlimited ? 0 : QuotaWarnPolicy.Severity(used, limit, percents);

    private static string BytesText(long b)
    {
        if (b >= QuotaResources.BytesPerGb) return $"{b / (double)QuotaResources.BytesPerGb:N1} GB";
        if (b >= 1024 * 1024) return $"{b / (1024.0 * 1024):N1} MB";
        if (b >= 1024) return $"{b / 1024.0:N0} KB";
        return $"{b:N0} B";
    }

    // เกณฑ์ค่าตัวแทน "ไม่จำกัด" — ที่เดียว (ผู้อ่านภายนอกใช้ผ่าน Users/Documents/Ocr/Storage/LimitTexts/UnlimitedFlags)
    private static bool UsersUnlimited(int max) => max <= 0 || max >= UnlimitedUsersAt;
    private static bool CountUnlimited(int max) => max <= 0 || max >= UnlimitedCountAt;
    private static bool CompaniesUnlimited(int max) => max <= 0 || max >= UnlimitedCompaniesAt;
    private static bool OcrUnlimited(int max) => max < 0;
    private static bool StorageUnlimited(long maxBytes) => maxBytes >= QuotaResources.UnlimitedBytes;

    public static Bar Users(long used, int max, IReadOnlyList<int>? percents = null)
    {
        var unlimited = UsersUnlimited(max);
        return Make("users", "ผู้ใช้งาน", used, max, unlimited, $"{used:N0}", unlimited ? "ไม่จำกัด" : $"{max:N0}", null, percents);
    }

    /// <param name="extra">รอบ 231: หมายเหตุ (เช่น "รวมโบนัส N") — null = ไม่มี</param>
    public static Bar Documents(long used, int max, IReadOnlyList<int>? percents = null, string? extra = null)
    {
        var unlimited = CountUnlimited(max);
        return Make("documents", "เอกสาร / เดือน", used, max, unlimited, $"{used:N0}", unlimited ? "ไม่จำกัด" : $"{max:N0}", extra, percents);
    }

    /// <summary>รอบ 231 (§A.2 "ไม่จำกัด ตัดสินที่เดียว"): ข้อความเพดานของแพ็กเกจ/subscription สำหรับหน้าเว็บทุกหน้า — เดิม <c>js/plan-display.js</c> และ
    /// <c>pages/subscription.html</c> พิมพ์เกณฑ์ 999/99/99999 เอง · คีย์: users · companies · docs · journals · ocr · storage · messages</summary>
    public static Dictionary<string, string> LimitTexts(int maxUsers, int maxCompanies, int maxDocs, int maxJournals, int maxOcr, long maxStorageBytes, int? maxMessages)
        => new()
        {
            ["users"] = UsersUnlimited(maxUsers) ? "ไม่จำกัด" : maxUsers.ToString("N0"),
            ["companies"] = CompaniesUnlimited(maxCompanies) ? "ไม่จำกัด" : maxCompanies.ToString("N0"),
            ["docs"] = CountUnlimited(maxDocs) ? "ไม่จำกัด" : maxDocs.ToString("N0"),
            ["journals"] = CountUnlimited(maxJournals) ? "ไม่จำกัด" : maxJournals.ToString("N0"),
            ["ocr"] = OcrUnlimited(maxOcr) ? "ไม่จำกัด" : maxOcr.ToString("N0"),
            ["storage"] = StorageUnlimited(maxStorageBytes) ? "ไม่จำกัด" : BytesText(maxStorageBytes),
            ["messages"] = maxMessages is int m && m >= 0 ? m.ToString("N0") : "ไม่จำกัด",
        };

    public static Dictionary<string, bool> UnlimitedFlags(int maxUsers, int maxCompanies, int maxDocs, int maxJournals, int maxOcr, long maxStorageBytes, int? maxMessages)
        => new()
        {
            ["users"] = UsersUnlimited(maxUsers), ["companies"] = CompaniesUnlimited(maxCompanies), ["docs"] = CountUnlimited(maxDocs),
            ["journals"] = CountUnlimited(maxJournals), ["ocr"] = OcrUnlimited(maxOcr), ["storage"] = StorageUnlimited(maxStorageBytes),
            ["messages"] = !(maxMessages is int m && m >= 0),
        };

    /// <summary>OCR — เพดาน = โควตาแพ็กเกจ + โบนัส + เครดิตที่ซื้อคงเหลือ (เท่าที่ตัวนับ OCR ใช้จริง) · &lt; 0 = ไม่จำกัด</summary>
    public static Bar Ocr(long used, int planMax, int bonus, int creditRemaining, IReadOnlyList<int>? percents = null)
    {
        var unlimited = OcrUnlimited(planMax);
        var limit = unlimited ? 0 : (long)planMax + Math.Max(0, bonus) + Math.Max(0, creditRemaining);
        var extra = (bonus > 0 ? $" รวมโบนัส {bonus:N0}" : "") + (creditRemaining > 0 ? $" · เครดิตที่ซื้อคงเหลือ {creditRemaining:N0}" : "");
        return Make("ocr", "OCR สแกน / เดือน", used, limit, unlimited, $"{used:N0}", unlimited ? "ไม่จำกัด" : $"{limit:N0}",
            extra.Length == 0 ? null : extra.TrimStart(' ', '·'), percents);
    }

    public static Bar Storage(long usedBytes, long maxBytes, IReadOnlyList<int>? percents = null)
    {
        var unlimited = StorageUnlimited(maxBytes);
        return Make("storage", "พื้นที่จัดเก็บ", usedBytes, maxBytes, unlimited, BytesText(usedBytes), unlimited ? "ไม่จำกัด" : BytesText(maxBytes), null, percents);
    }

    private static Bar Make(string key, string label, long used, long limit, bool unlimited, string usedText, string limitText, string? extra, IReadOnlyList<int>? percents)
    {
        var pct = Percent(used, limit, unlimited);
        return new Bar(key, label, used, unlimited ? null : limit, unlimited, pct, Level(used, unlimited ? null : limit, unlimited, percents), usedText, limitText, extra);
    }

    /// <summary>วันที่เหลือของแพ็กเกจ (ปฏิทินไทย · ไม่ติดลบ) — เดิมหน้าเว็บคิดจากนาฬิกาเครื่องผู้ใช้</summary>
    public static int DaysLeft(DateTime endUtc, DateTime nowUtc)
        => Math.Max(0, (endUtc.AddHours(7).Date - nowUtc.AddHours(7).Date).Days);
}
