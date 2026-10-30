using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 214 (คำตัดสินข้อ 173) — กติกาเวอร์ชันของเอกสารทางกฎหมายของแพลตฟอร์ม (<c>LegalDocumentVersions</c>) — ตัวตัดสินเดียว (pure · เทสต์ได้):
/// <list type="bullet">
/// <item><b>ฉบับที่ใช้อยู่</b> = ฉบับที่เผยแพร่แล้วและวันมีผล ≤ วันนี้ (เวลาไทย) ที่วันมีผลล่าสุด (เท่ากัน ⇒ เผยแพร่ทีหลังชนะ) ·
/// ไม่มีเลย ⇒ ฉบับตั้งต้นในโค้ด (<see cref="LegalDocumentDefaults"/> 1.0) — ระบบที่ยังไม่มีแถวทำงานเหมือนก่อนรอบ 214 ทุกอย่าง</item>
/// <item><b>ฉบับที่ประกาศล่วงหน้า</b> = เผยแพร่แล้วแต่วันมีผลยังไม่ถึง (ข้อกำหนดข้อ 10: แจ้งล่วงหน้า 30 วัน) — แสดงคู่กับฉบับปัจจุบัน</item>
/// <item><b>เผยแพร่แล้วแก้ไม่ได้</b> (append-only) — แก้ = ออกฉบับใหม่ · เลขฉบับต้องมากกว่าทุกฉบับเดิม · วันมีผลห้ามย้อนหลัง</item>
/// <item><b>เวอร์ชันความยินยอม</b> = ฉบับของนโยบาย + ข้อกำหนดรวมกัน (ช่องติ๊กเดียวยอมรับทั้งสองฉบับ)</item>
/// </list>
/// </summary>
public static class LegalDocumentPolicy
{
    public const string StatusDraft = "Draft";
    public const string StatusPublished = "Published";

    /// <summary>รหัสกฎสำหรับ audit/ข้อความผิดพลาด</summary>
    public const string RuleCode = "PDPA-23-NOTICE-VERSION";

    private static readonly Regex VersionPattern = new(@"^\d{1,4}(\.\d{1,4}){0,2}$", RegexOptions.Compiled);

    /// <summary>มุมมองขั้นต่ำของแถวที่ตัวตัดสินต้องใช้ (แยกจาก entity ⇒ เทสต์ได้โดยไม่ต้องมี DbContext)</summary>
    public sealed record VersionRow(string Version, DateOnly EffectiveDate, string Status, DateTime? PublishedAt);

    public static bool IsValidVersionLabel(string? v) => !string.IsNullOrWhiteSpace(v) && VersionPattern.IsMatch(v.Trim());

    /// <summary>เปรียบเทียบเลขฉบับแบบตัวเลขทีละส่วน ("1.10" &gt; "1.9" · "2" = "2.0")</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = a.Trim().Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pb = b.Trim().Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>ฉบับที่มีผลอยู่ ณ วันนี้ — null = ยังไม่มีฉบับเผยแพร่ที่ถึงวันมีผล (ใช้ฉบับตั้งต้น)</summary>
    public static VersionRow? PickCurrent(IEnumerable<VersionRow> rows, DateOnly today)
        => rows.Where(r => r.Status == StatusPublished && r.EffectiveDate <= today)
               .OrderByDescending(r => r.EffectiveDate)
               .ThenByDescending(r => r.PublishedAt ?? DateTime.MinValue)
               .FirstOrDefault();

    /// <summary>ฉบับที่ประกาศแล้วแต่ยังไม่ถึงวันมีผล (ใกล้ที่สุด) — null = ไม่มี</summary>
    public static VersionRow? PickUpcoming(IEnumerable<VersionRow> rows, DateOnly today)
        => rows.Where(r => r.Status == StatusPublished && r.EffectiveDate > today)
               .OrderBy(r => r.EffectiveDate)
               .FirstOrDefault();

    /// <summary>ตรวจร่างก่อนเผยแพร่ — คืนข้อความผิด (ไทย) หรือ null = เผยแพร่ได้ ·
    /// <paramref name="published"/> = ทุกฉบับที่เผยแพร่แล้วของเอกสารชนิดเดียวกัน · ฉบับตั้งต้นในโค้ด (1.0) นับเป็นฉบับที่มีอยู่แล้วเสมอ</summary>
    public static string? ValidatePublish(string? version, DateOnly effectiveDate, string? bodyMarkdown,
        IReadOnlyCollection<VersionRow> published, DateOnly today)
    {
        if (!IsValidVersionLabel(version))
            return "เลขฉบับต้องเป็นตัวเลขคั่นด้วยจุด เช่น 1.1 หรือ 2.0";
        if (string.IsNullOrWhiteSpace(bodyMarkdown))
            return "เนื้อความว่าง — เผยแพร่ไม่ได้";
        if (bodyMarkdown.Length > LegalMarkdown.MaxLength)
            return $"เนื้อความยาวเกิน {LegalMarkdown.MaxLength:N0} ตัวอักษร";
        var v = version!.Trim();
        var existing = published.Select(p => p.Version).Append(LegalDocumentDefaults.BuiltInVersion).ToList();
        var newest = existing.Aggregate((x, y) => CompareVersions(x, y) >= 0 ? x : y);
        if (CompareVersions(v, newest) <= 0)
            return $"เลขฉบับต้องมากกว่าฉบับล่าสุด ({newest}) — ฉบับที่เผยแพร่แล้วแก้ไม่ได้ ต้องออกฉบับใหม่";
        if (effectiveDate < today)
            return "วันมีผลย้อนหลังไม่ได้ — ตั้งเป็นวันนี้หรือวันในอนาคต (ประกาศล่วงหน้า)";
        var latestEffective = published.Select(p => p.EffectiveDate).DefaultIfEmpty(LegalDocumentDefaults.BuiltInEffectiveDate).Max();
        if (effectiveDate < latestEffective)
            return $"วันมีผลต้องไม่ก่อนฉบับล่าสุด ({latestEffective:yyyy-MM-dd})";
        return null;
    }

    /// <summary>แก้แถวได้เฉพาะร่าง — ฉบับที่เผยแพร่แล้วคือหลักฐานว่าผู้ใช้เห็นอะไรตอนยินยอม (ม.19 ภาระพิสูจน์)</summary>
    public static bool CanEdit(string status) => status == StatusDraft;

    /// <summary>เวอร์ชันที่ผูกกับความยินยอม "ยอมรับข้อกำหนด + นโยบาย" — สองฉบับเท่ากัน ⇒ เลขเดียว (เข้ากันกับแถวเดิม "1.0") ·
    /// ต่างกัน ⇒ ระบุทั้งคู่ "privacy 1.1 · terms 1.0"</summary>
    public static string ConsentVersion(string privacyVersion, string termsVersion)
        => privacyVersion == termsVersion ? privacyVersion : $"privacy {privacyVersion} · terms {termsVersion}";

    /// <summary>รอบ 219 ส่วน B (คำตัดสินข้อ 179): เวอร์ชันเงื่อนไขการซื้อที่ผู้ซื้อยอมรับก่อนชำระค่าบริการ — ระบุทั้งสองฉบับเสมอ
    /// (ต่างจาก <see cref="ConsentVersion"/> — นี่ไม่ใช่ความยินยอม PDPA และไม่กระทบแถบ "ฉบับใหม่")</summary>
    public static string PurchasePolicyVersion(string termsVersion, string refundVersion)
        => $"terms {termsVersion} · refund {refundVersion}";

    /// <summary>ผู้ใช้ต้องเห็นแถบ "นโยบายมีฉบับใหม่" ไหม — ไม่มีหลักฐานการยอมรับเลย หรือฉบับที่ยอมรับล่าสุดไม่ใช่ฉบับปัจจุบัน
    /// (แจ้ง + กดรับทราบ · ไม่บล็อกการใช้งาน — คำตัดสินข้อ 173)</summary>
    public static bool NeedsAcknowledge(string? acceptedConsentVersion, string currentConsentVersion)
        => string.IsNullOrWhiteSpace(acceptedConsentVersion) || acceptedConsentVersion.Trim() != currentConsentVersion;

    /// <summary>วันนี้ตามเวลาไทย (+07:00) — วันมีผลของนโยบายนับตามวันปฏิทินไทย</summary>
    public static DateOnly TodayBangkok(DateTime utcNow) => DateOnly.FromDateTime(utcNow.AddHours(7));
}
