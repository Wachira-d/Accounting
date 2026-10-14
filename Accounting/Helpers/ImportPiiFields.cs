using System.Text;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **ช่องข้อมูลส่วนบุคคลอ่อนไหวของการนำเข้า + วิธีปิดบัง** — ตัวตั้งเดียวของศูนย์นำเข้า (ฝ่ายค้านรอบ ETL 2026-10-14 · PDPA ม.26 · CLAUDE.md §J)
///
/// <para>ที่มา: ทะเบียนพนักงานที่นำเข้ามีเลขบัตรประชาชน · เลขประกันสังคม · เลขบัญชีธนาคาร · เงินเดือน — แถวพัก (GET rows) · ไฟล์แถวผิด (errors.csv) ·
/// ไฟล์ต้นทาง เปิดได้ด้วยสิทธิ์นำเข้า (HrAdmin) อย่างเดียว ทั้งที่ส่งออกทะเบียนเดียวกันต้องมี <c>Pii.View</c>
/// (<see cref="ImportExportPermissionScope.ForExport"/>) ⇒ ค่าอ่อนไหวต้องถูกปิดบังเว้นแต่ผู้ใช้มี <c>Pii.View</c> (controller ตัดสิน) ·
/// และตัวอย่างค่าที่ส่งให้ AI จับคู่คอลัมน์ต้องไม่มีเลขจริง (<see cref="RedactForPrompt"/> — รูปตัวเลขคงไว้ให้โมเดลยังเดาชนิดคอลัมน์ได้)</para>
/// <para>รูปการปิดบังใช้ <see cref="PiiMask"/> ตัวเดียวของระบบ (เลขบัตร <c>1-XXXX-XXXXX-XX-3</c> · เลขบัญชีเปิด 5 ท้าย) · G6: pure · ไม่ throw</para>
/// </summary>
public static class ImportPiiFields
{
    private enum MaskKind { CitizenId, BankAccount, Hidden, Phone, Email }

    /// <summary>ชนิดข้อมูล (คีย์ normalize ด้วย <see cref="ImportExportPermissionScope.NormalizeEntityType"/>) → ช่องอ่อนไหว → วิธีปิดบัง</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, MaskKind>> ByEntity =
        new Dictionary<string, IReadOnlyDictionary<string, MaskKind>>(StringComparer.Ordinal)
        {
            ["employees"] = new Dictionary<string, MaskKind>(StringComparer.OrdinalIgnoreCase)
            {
                ["CitizenId"] = MaskKind.CitizenId,
                // เลขประกันสังคม = เลขบัตรประชาชน (สปส.) — ปิดแบบเดียวกัน
                ["SocialSecurityNumber"] = MaskKind.CitizenId,
                ["BankAccountNumber"] = MaskKind.BankAccount,
                ["BaseSalary"] = MaskKind.Hidden,
                ["DateOfBirth"] = MaskKind.Hidden,
                ["Phone"] = MaskKind.Phone,
                ["Email"] = MaskKind.Email,
            },
        };

    private const string HiddenText = "••••••";

    private static IReadOnlyDictionary<string, MaskKind>? Fields(string? entityType)
        => ByEntity.TryGetValue(ImportExportPermissionScope.NormalizeEntityType(entityType), out var f) ? f : null;

    /// <summary>ชนิดข้อมูลนี้มีช่องอ่อนไหวไหม (ไม่มี = ไม่ต้องถามสิทธิ์ Pii.View)</summary>
    public static bool HasPii(string? entityType) => Fields(entityType) != null;

    /// <summary>ช่องนี้ของชนิดนี้เป็นข้อมูลอ่อนไหวไหม</summary>
    public static bool IsPii(string? entityType, string? field)
        => field != null && Fields(entityType) is { } f && f.ContainsKey(field);

    /// <summary>
    /// ค่าที่แสดงให้ผู้ไม่มี <c>Pii.View</c> — ช่องอ่อนไหว = ปิดตามชนิด · ช่องอื่นของชนิดที่มี PII ที่ไม่ได้จับคู่ (<paramref name="field"/> null —
    /// คอลัมน์ที่ไม่ได้ใช้ของไฟล์อาจเป็นเลขบัตร) = ปิดตามรูปค่า · ชนิดที่ไม่มี PII = คืนค่าเดิม
    /// </summary>
    public static string? Mask(string? entityType, string? field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var fields = Fields(entityType);
        if (fields == null) return value;
        if (field != null && fields.TryGetValue(field, out var kind))
            return kind switch
            {
                MaskKind.CitizenId => PiiMask.CitizenId(value),
                MaskKind.BankAccount => PiiMask.BankAccountNo(value),
                MaskKind.Phone => PiiMask.Phone(value),
                MaskKind.Email => PiiMask.Email(value),
                _ => HiddenText,
            };
        return field == null ? MaskByShape(value) : value;
    }

    /// <summary>
    /// ตัวอย่างค่าที่จะส่งให้ AI (จับคู่คอลัมน์ · ตรวจข้อมูล) — ชนิดที่มี PII: ช่องอ่อนไหว/คอลัมน์ที่ยังไม่รู้ชนิด ⇒ เหลือแค่ "รูป" (ตัวเลข → 9 · อีเมลปิดชื่อ)
    /// · ชนิดอื่น: เฉพาะค่าที่รูปเหมือนเลขบัตรประชาชนบุคคล (13 หลักขึ้นต้น 1–8) และอีเมล ⇒ รูป · ที่เหลือคงเดิม (หัวตาราง/ข้อความธุรกิจช่วยโมเดลเดา)
    /// </summary>
    public static string? RedactForPrompt(string? entityType, string? field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var fields = Fields(entityType);
        if (fields != null && (field == null || fields.ContainsKey(field))) return Shape(value);
        var digits = Digits(value);
        if (digits.Length == 13 && digits[0] is >= '1' and <= '8' && value.Count(char.IsLetter) == 0) return Shape(value);
        return value.Contains('@') ? Shape(value) : value;
    }

    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());

    private static readonly Regex EmailLike = new(@"^[^@\s]+@[^@\s]+$", RegexOptions.Compiled);

    /// <summary>ปิดค่าของคอลัมน์ที่ไม่รู้ชนิดตามรูป — 13 หลัก = เลขบัตร · ≥ 8 หลัก = เลขบัญชี · อีเมล · อื่น ๆ คงเดิม</summary>
    private static string MaskByShape(string value)
    {
        var t = value.Trim();
        if (EmailLike.IsMatch(t)) return PiiMask.Email(t) ?? HiddenText;
        var digits = Digits(t);
        if (digits.Length == 13) return PiiMask.CitizenId(t) ?? HiddenText;
        if (digits.Length >= 8 && digits.Length * 2 >= t.Length) return PiiMask.BankAccountNo(t) ?? HiddenText;
        return value;
    }

    /// <summary>"รูป" ของค่า — ตัวเลขทุกตัวเป็น 9 · ส่วนชื่อของอีเมลเป็น x (คงตัวคั่น/ความยาว ⇒ โมเดลยังเห็นว่าเป็นวันที่/ยอด/เลข 13 หลัก)</summary>
    private static string Shape(string value)
    {
        var sb = new StringBuilder(value.Length);
        var at = value.IndexOf('@');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            sb.Append(char.IsDigit(c) ? '9' : at > 0 && i < at && char.IsLetter(c) ? 'x' : c);
        }
        return sb.ToString();
    }
}
