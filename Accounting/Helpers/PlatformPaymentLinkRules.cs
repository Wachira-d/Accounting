using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>สถานะของลิงก์ชำระ (มุมผู้เปิดลิงก์)</summary>
public enum PaymentLinkState
{
    Valid = 0,
    Expired = 1,
    Revoked = 2,
    /// <summary>ใบถูกชำระครบแล้ว — ลิงก์ปิดเอง (single invoice)</summary>
    Paid = 3,
    /// <summary>ใบถูกยกเลิก/ไม่ใช่ใบค้างอีกต่อไป</summary>
    InvoiceClosed = 4,
}

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 4 (PLATFORM_BILLING_AUDIT §4 · คำตัดสินข้อ 180) — <b>ลิงก์ชำระใบแจ้งหนี้ค่าบริการ (manual payment link)</b> ตัวตั้งเดียว (pure):
/// โทเคนสุ่ม 128 บิต (base64url) · เก็บเฉพาะ SHA-256 (<see cref="Hash"/> — canonical เดียว ใช้ทั้งตอนสร้างและตอนค้น) · สถานะ/ข้อความไทย/ทางไปต่อ
/// </summary>
public static class PlatformPaymentLinkRules
{
    public const int TokenBytes = 16;   // 128 บิต
    public const int MinTokenLength = 20, MaxTokenLength = 64;

    /// <summary>โทเคนใหม่ — แสดงครั้งเดียวตอนสร้าง (ฐานข้อมูลไม่มีตัวจริง)</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>รูปโทเคนที่รับได้ก่อนแตะฐาน (กันค้นด้วยขยะ/ยาวผิด) — ไม่ผ่าน = "ไม่พบ" เหมือนโทเคนผิด</summary>
    public static bool LooksValid(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length is >= MinTokenLength and <= MaxTokenLength
        && token.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    /// <summary>SHA-256 hex ตัวพิมพ์เล็กของโทเคน — ฝั่งเขียนและฝั่งค้นใช้ฟังก์ชันนี้ตัวเดียว</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static PaymentLinkState StateOf(DateTime expiresAtUtc, DateTime? revokedAtUtc, bool invoiceOpen, bool invoicePaid, DateTime nowUtc)
    {
        if (revokedAtUtc != null) return PaymentLinkState.Revoked;
        if (invoicePaid) return PaymentLinkState.Paid;
        if (!invoiceOpen) return PaymentLinkState.InvoiceClosed;
        if (nowUtc >= expiresAtUtc) return PaymentLinkState.Expired;
        return PaymentLinkState.Valid;
    }

    /// <summary>ข้อความ + ทางไปต่อของลิงก์ที่ใช้ไม่ได้ (null = ใช้ได้)</summary>
    public static string? Message(PaymentLinkState s) => s switch
    {
        PaymentLinkState.Valid => null,
        PaymentLinkState.Paid => "ใบแจ้งหนี้นี้ชำระครบแล้ว ขอบคุณค่ะ",
        PaymentLinkState.Expired => "ลิงก์ชำระนี้หมดอายุแล้ว — กรุณาขอลิงก์ใหม่จากผู้ดูแลระบบ NextAcc หรือเข้าสู่ระบบแล้วชำระที่ศูนย์บิลค่าบริการ",
        PaymentLinkState.Revoked => "ลิงก์ชำระนี้ถูกยกเลิกแล้ว — กรุณาขอลิงก์ใหม่จากผู้ดูแลระบบ NextAcc หรือชำระที่ศูนย์บิลค่าบริการ",
        _ => "ใบแจ้งหนี้นี้ไม่มียอดค้างให้ชำระแล้ว — ติดต่อผู้ดูแลระบบ NextAcc หากมีข้อสงสัย",
    };

    /// <summary>อายุลิงก์ — ค่าที่แอดมินระบุในคำขอ (บีบเข้าช่วงค่าตั้ง) หรือค่าตั้งต้นจาก <see cref="PlatformBillingPolicy.PaymentLinkExpiryDays"/></summary>
    public static int ExpiryDays(int? requested, PlatformBillingPolicy p) =>
        Math.Clamp(requested ?? p.PaymentLinkExpiryDays, PlatformBillingSettingsRules.MinPaymentLinkExpiryDays, PlatformBillingSettingsRules.MaxPaymentLinkExpiryDays);

    /// <summary>ชื่อผู้ซื้อแบบย่อบนหน้าสาธารณะ (ไม่มีที่อยู่/เลขภาษี — ข้อมูลขั้นต่ำ)</summary>
    public static string MaskedBuyerName(string? name) => string.IsNullOrWhiteSpace(name) ? "-" : name.Trim().Length > 80 ? name.Trim()[..80] : name.Trim();
}
