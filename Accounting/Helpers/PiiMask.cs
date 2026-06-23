namespace Accounting.Helpers;

/// <summary>PDPA ม.26 + ม.37 — masking sensitive PII fields ตอน serialize
/// กลับไป API response. Full value แสดงเฉพาะตอน user มี permission
/// "pii:view" (resolved ก่อน serialize). Default = masked.
///
/// Mask format ตามกฎหมายไทย (ประกาศ PDPC + แนวปฏิบัติทั่วไป):
/// - เลขบัตรประชาชน 13 หลัก: `1-XXXX-XXXXX-XX-3` (เปิด digit 1+13)
/// - เลขผู้เสียภาษี 13 หลัก: `1234567XXXXXX` (เปิด 7 ตัวแรก)
/// - PassportNumber: `AAXXXX12` (เปิด 2 ตัวแรก + 2 ท้าย)
/// - เลขบัญชีธนาคาร: `XXX-X-X1234-5` (เปิด 5 ท้าย)
/// - เบอร์โทร: `08X-XXX-XX99` (เปิด 2 ตัวแรก + 2 ท้าย)
/// - Email: `n***@gmail.com` (เปิด 1 ตัวก่อน @ + domain เต็ม)
///
/// Static helper — caller ต้องเรียกเองตอน Map → DTO. ทำให้ field
/// ที่ leak ไม่ได้ปลอดภัยตาม default.</summary>
public static class PiiMask
{
    public static string? CitizenId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length != 13) return "XXXXXXXXXXXXX";
        return $"{digits[0]}-XXXX-XXXXX-XX-{digits[12]}";
    }

    public static string? TaxId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length != 13) return "XXXXXXXXXXXXX";
        // เปิด 7 หลักแรก ปกปิด 6 หลักหลัง (เพียงพอเทียบ vendor + ไม่ระบุตัว)
        return digits[..7] + "XXXXXX";
    }

    public static string? Passport(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var s = value.Trim();
        if (s.Length < 6) return new string('X', s.Length);
        return s[..2] + new string('X', s.Length - 4) + s[^2..];
    }

    public static string? BankAccountNo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return new string('X', digits.Length);
        return new string('X', digits.Length - 5) + digits[^5..];
    }

    public static string? Phone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length < 4) return new string('X', digits.Length);
        return digits[..2] + new string('X', digits.Length - 4) + digits[^2..];
    }

    public static string? Email(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var at = value.IndexOf('@');
        if (at <= 0) return value;
        var local = value[..at]; var domain = value[at..];
        if (local.Length <= 1) return local + "***" + domain;
        return local[0] + "***" + domain;
    }
}
