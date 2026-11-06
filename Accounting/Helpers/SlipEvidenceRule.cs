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

    /// <summary>ความยาวขั้นต่ำของเลขอ้างอิงโอนที่ใช้ได้ (ฝ่ายค้าน M6 — สั้นกว่านี้ชนเลขทั่วไปได้ง่าย)</summary>
    public const int MinRefLength = 8;

    /// <summary>เลขอ้างอิงโอน: ตัดช่องว่าง/ขีด · ตัวพิมพ์ใหญ่ · ตัวอักษร/ตัวเลขเท่านั้น · สั้นกว่า 8 หรือไม่มีตัวเลขเลย = ไม่ใช้ (ฝ่ายค้าน M6) · ยาวเกินตัดที่ 64</summary>
    public static string? NormalizeRef(string? input)
    {
        var s = new string((input ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (s.Length < MinRefLength || !s.Any(char.IsDigit)) return null;
        return s.Length > MaxRefLength ? s[..MaxRefLength] : s;
    }

    /// <summary>เลขอ้างอิงของสลิปตรงกับช่อง <b>อ้างอิง</b>ของรายการเดินบัญชีแบบเท่ากันทั้งคำ (ฝ่ายค้าน M6 — ไม่ใช่ substring ของคำอธิบาย) —
    /// ทั้งช่องหลังปรับรูปเท่ากัน หรือคำใดคำหนึ่งในช่อง (แยกด้วยอักขระที่ไม่ใช่ตัวอักษร/ตัวเลข) เท่ากัน · ตัวระบุตัวข้อ 3 ของ <c>BankReceiptAutoSettle</c></summary>
    public static bool RefMatchesLine(string? slipRef, string? lineReference)
    {
        if (string.IsNullOrEmpty(slipRef) || string.IsNullOrWhiteSpace(lineReference)) return false;
        if (NormalizeAll(lineReference) == slipRef) return true;
        var token = new System.Text.StringBuilder();
        foreach (var ch in lineReference + " ")
        {
            if (char.IsLetterOrDigit(ch)) { token.Append(char.ToUpperInvariant(ch)); continue; }
            if (token.Length > 0 && token.ToString() == slipRef) return true;
            token.Clear();
        }
        return false;
    }

    private static string NormalizeAll(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    // ═══ รอบ 255 WP-12B ข้อ 1 — แจ้งโอนจากหน้าชำระของเอกสาร (ทางเข้าที่ไม่ล็อกอิน · โทเคนลิงก์ชำระพิสูจน์สิทธิ์) ═══

    /// <summary>เพดานการแจ้งโอนต่อเอกสารต่อชั่วโมง (นับในฐาน) — ทางเข้าที่ไม่ล็อกอินต้องมีเพดาน</summary>
    public const int PayLinkMaxPerHour = 5;
    /// <summary>ขนาดไฟล์สูงสุดของสลิปจากหน้าชำระ</summary>
    public const int PayLinkMaxBytes = 10 * 1024 * 1024;

    /// <summary>ชนิดไฟล์จากไบต์จริง (ไม่เชื่อ Content-Type ที่ client ส่ง) — JPEG/PNG/WEBP (<see cref="ImageMagicBytes"/>) หรือ PDF (<c>%PDF-</c>) · อื่น ๆ = null</summary>
    public static string? DetectPublicType(ReadOnlySpan<byte> head)
    {
        if (ImageMagicBytes.Detect(head) is string img) return img;
        if (head.Length >= 5 && head[0] == (byte)'%' && head[1] == (byte)'P' && head[2] == (byte)'D' && head[3] == (byte)'F' && head[4] == (byte)'-')
            return "application/pdf";
        return null;
    }

    /// <summary>นามสกุลไฟล์ที่เก็บ — จากชนิดที่ตรวจจากไบต์ (ไม่ใช้ชื่อไฟล์ของ client) · ไม่รู้จัก = null</summary>
    public static string? ExtensionFor(string? sniffedType) => sniffedType switch
    {
        "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", "application/pdf" => ".pdf", _ => null,
    };

    /// <summary>ยอดสลิปรอตรวจของใบนี้รวมสลิปใหม่เกินยอดค้างไหม (ตรวจภายใต้ล็อก — สองคำขอพร้อมกันไม่ผ่านทั้งคู่) · null = รับได้ ·
    /// งวดย่อยรวมกันไม่เกินยอดค้างยังรับได้ (สลิปงวด 2 · ฝ่ายค้าน 250 H2)</summary>
    public static string? PendingProblem(decimal pendingSum, decimal amount, decimal balanceDue)
        => pendingSum + amount > balanceDue + 0.01m
            ? $"มีสลิปของเอกสารนี้รอผู้ขายตรวจ {pendingSum:#,##0.00} บาทแล้ว — รวมสลิปนี้เกินยอดค้าง ({balanceDue:#,##0.00} บาท) · รอผู้ขายตรวจ หรือติดต่อผู้ขาย"
            : null;

    /// <summary>หน้าชำระแสดงฟอร์มแจ้งโอนไหม — มีกล่องโอน (ใบรับโอนได้ + มีบัญชีรับโอน) และยอดสุทธิ &gt; 0</summary>
    public static bool PayLinkSlipAllowed(bool hasTransferBox, decimal net) => hasTransferBox && net > 0.005m;

    /// <summary>ด่านของการแจ้งโอนจากหน้าชำระ · null = รับได้ · ข้อความไทยถึงลูกค้า (ไม่มีข้อมูลของใบอื่น)</summary>
    public static string? PayLinkSlipProblem(bool allowed, bool hasPendingSlip, int submittedLastHour)
    {
        if (!allowed) return "เอกสารนี้แจ้งโอนผ่านหน้านี้ไม่ได้ — กรุณาติดต่อผู้ขาย";
        if (hasPendingSlip) return "ได้รับสลิปของเอกสารนี้แล้ว — รอผู้ขายตรวจและยืนยัน (ไม่ต้องแนบซ้ำ) · ถ้าแนบผิดใบ ติดต่อผู้ขาย";
        if (submittedLastHour >= PayLinkMaxPerHour) return "แจ้งโอนถี่เกินไป — กรุณารอสักครู่แล้วลองใหม่ หรือติดต่อผู้ขาย";
        return null;
    }

    /// <summary>ข้อความถึงลูกค้าเมื่อสลิปซ้ำ (ไม่บอกเลขเอกสารอื่น — ไม่รั่วข้อมูลใบของลูกค้าอื่น)</summary>
    public static string? DuplicateProblem(bool sameFileSeen, bool sameRefSeen)
    {
        if (sameFileSeen) return "สลิปนี้เคยแนบมาแล้ว — ไม่ต้องแนบซ้ำ · ถ้าเป็นการโอนครั้งใหม่ ให้แนบสลิปของการโอนครั้งนั้น หรือติดต่อผู้ขาย";
        if (sameRefSeen) return "เลขอ้างอิงการโอนนี้เคยแจ้งไว้แล้ว — ไม่ต้องแจ้งซ้ำ · ถ้าเลขไม่ถูกต้อง ให้ติดต่อผู้ขาย";
        return null;
    }
}
