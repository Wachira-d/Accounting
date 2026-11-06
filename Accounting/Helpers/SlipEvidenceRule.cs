using System.Security.Cryptography;

namespace Accounting.Helpers;

/// <summary>
/// **สลิปโอนที่ลูกค้าแนบ = "ข้อบ่งชี้" — กันซ้ำด้วยแฮชไฟล์ + เลขอ้างอิงโอน** (รอบ 252 WP-12A · AUTOPILOT §5.6 G-T5) · pure
/// <para>สลิปไม่มีวันปิดหนี้เอง (F2 ข้อ 3): ปิดได้เมื่อ (ก) รายการเดินบัญชีจริงมาถึงแล้ว <c>BankReceiptAutoSettle</c> จับได้ (เลขอ้างอิงตรง = ตัวระบุตัวข้อ 3) หรือ
/// (ข) คนยืนยันสลิปที่หน้ารายการรับชำระ (ทางเดิม <c>ManualSlipPaymentProvider</c>)</para>
/// </summary>
public static class SlipEvidenceRule
{
    public const string RuleDuplicate = "SLIP-DUP";
    public const int MaxRefLength = 64;

    /// <summary>SHA-256 (hex ตัวเล็ก) ของไบต์ไฟล์ต้นฉบับ — ก่อนย่อ/แปลงรูป (ไฟล์เดิมส่งซ้ำ = แฮชเดิม)</summary>
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes ?? Array.Empty<byte>())).ToLowerInvariant();

    /// <summary>เลขอ้างอิงโอน: ตัดช่องว่าง/ขีด · ตัวพิมพ์ใหญ่ · ตัวอักษร/ตัวเลขเท่านั้น · สั้นกว่า 6 = ไม่ใช้ (กันชนเลขทั่วไป) · ยาวเกินตัดที่ 64</summary>
    public static string? NormalizeRef(string? input)
    {
        var s = new string((input ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (s.Length < 6) return null;
        return s.Length > MaxRefLength ? s[..MaxRefLength] : s;
    }

    /// <summary>เลขอ้างอิงของสลิปอยู่ในข้อความของรายการเดินบัญชีไหม (อ้างอิง/คำอธิบาย) — ตัวระบุตัวข้อ 3 ของ <c>BankReceiptAutoSettle</c></summary>
    public static bool RefMatchesLine(string? slipRef, string? lineReference, string? lineDescription)
    {
        if (string.IsNullOrEmpty(slipRef)) return false;
        var hay = NormalizeAll(lineReference) + "|" + NormalizeAll(lineDescription);
        return hay.Contains(slipRef, StringComparison.Ordinal);
    }

    private static string NormalizeAll(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>ข้อความถึงลูกค้าเมื่อสลิปซ้ำ (ไม่บอกเลขเอกสารอื่น — ไม่รั่วข้อมูลใบของลูกค้าอื่น)</summary>
    public static string? DuplicateProblem(bool sameFileSeen, bool sameRefSeen)
    {
        if (sameFileSeen) return "สลิปนี้เคยแนบมาแล้ว — ไม่ต้องแนบซ้ำ · ถ้าเป็นการโอนครั้งใหม่ ให้แนบสลิปของการโอนครั้งนั้น หรือติดต่อผู้ขาย";
        if (sameRefSeen) return "เลขอ้างอิงการโอนนี้เคยแจ้งไว้แล้ว — ไม่ต้องแจ้งซ้ำ · ถ้าเลขไม่ถูกต้อง ให้ติดต่อผู้ขาย";
        return null;
    }
}
