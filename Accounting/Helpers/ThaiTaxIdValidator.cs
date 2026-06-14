namespace Accounting.Helpers;

/// <summary>
/// Thai Tax ID checksum validator per สำนักทะเบียนกลาง spec:
///   13-digit ID, last digit = ((11 - Σ(d[i] × (13-i)) mod 11) mod 10)
///
/// ใช้กับ:
///   • Personal Citizen ID (เริ่ม 1-8)
///   • Juristic / Company Tax ID (เริ่ม 0)
///   • Foreigner Identification (เริ่ม 0,8)
///
/// คืน ValidationResult: IsValid + Reason สำหรับ UI แสดง.
/// </summary>
public static class ThaiTaxIdValidator
{
    public sealed record ValidationResult(bool IsValid, string? Reason);

    public static ValidationResult Check(string? taxId)
    {
        if (string.IsNullOrWhiteSpace(taxId))
            return new ValidationResult(true, null);   // optional field

        var clean = new string(taxId.Where(char.IsDigit).ToArray());
        if (clean.Length != 13)
            return new ValidationResult(false, $"ต้องเป็นเลข 13 หลัก (ปัจจุบัน {clean.Length})");

        // First digit 0 = company / 1-8 = personal Thai
        var first = clean[0];
        if (first < '0' || first > '8')
            return new ValidationResult(false, "หลักแรกต้องเป็น 0-8");

        // Mod-11 checksum
        int sum = 0;
        for (int i = 0; i < 12; i++)
            sum += (clean[i] - '0') * (13 - i);
        var check = (11 - (sum % 11)) % 10;
        if (check != (clean[12] - '0'))
            return new ValidationResult(false, "เลข check digit ไม่ถูกต้อง (ตรวจสอบเลขที่กรอกใหม่อีกครั้ง)");

        return new ValidationResult(true, null);
    }

    /// <summary>Normalize → 13 digit string. คืน null ถ้า invalid.</summary>
    public static string? Normalize(string? input)
        => input == null ? null
            : new string(input.Where(char.IsDigit).ToArray()) is { Length: 13 } s ? s : null;
}
