namespace Accounting.Helpers;

/// <summary>
/// **แยก payload ของ QR "ตรวจสอบสลิป" ท้ายสลิปโอนเงินไทย (mini-QR) — pure · ไม่เรียกเครือข่าย** (รอบ 264 แพ็ก P2 · คำตัดสิน 264-4-1 = ร่าง 264-2-6)
///
/// <para>═══ โครงสร้างที่<b>ยืนยันกับสลิปจริง 6 ใบ</b> (กรุงไทย 2 · SCB 3 · K+ 1 — ถอดด้วย ZXing + OpenCV ในเครื่อง 2026-11-13 · CRC ตรงทุกใบ) ═══
/// TLV แบบ EMVCo (แท็ก 2 หลัก · ความยาว 2 หลัก · ค่า):
/// <code>
/// 00 LL  [ 00 06 "000001"        ← รหัส API ของมาตรฐาน (ทั้ง 6 ใบ = 000001)
///          01 03 "004"           ← รหัสธนาคาร<b>ผู้โอน</b> (ธนาคารที่ออกสลิป: 004 K+ · 006 กรุงไทย · 014 SCB)
///          02 LL "016790246801ERTY0209" ] ← เลขอ้างอิงรายการของธนาคาร = "รหัสอ้างอิง"/"เลขที่รายการ" ที่พิมพ์บนสลิป (ทุกใบตรงตัวอักษร)
/// 51 02  "TH"                    ← ประเทศ
/// 91 04  "1AAF"                  ← CRC-16/CCITT-FALSE (poly 0x1021 · init 0xFFFF) ของข้อความตั้งแต่ต้นถึง "9104" รวม · ฐานสิบหกตัวใหญ่
/// </code>
/// <b>ไม่มี</b>ยอด · เลขบัญชี · ชื่อ · ผู้รับ ใน QR (ตรวจรายการกับธนาคาร = บริการภายนอก — backlog คำตัดสิน 264-2-6) ·
/// สลิปที่<b>ไม่มี QR</b> บนกระดาษจริง: K+ จ่ายบิลด้วยบัตรเครดิต · ttb จ่ายบัตรเครดิต (อังกฤษ) · KBIZ โอน/จ่ายบิล (อังกฤษ)</para>
///
/// <para>═══ กติกา ═══ รูปแบบไม่ตรง / CRC ผิด / ไม่มีแท็ก 51=TH / รหัส API ไม่ใช่ 000001 / รหัสธนาคารไม่ใช่ตัวเลข 3 หลัก ⇒ <c>null</c> (ไม่เดา) ·
/// QR พร้อมเพย์สำหรับ<b>ชำระเงิน</b> (EMVCo "000201…") ไม่ใช่ QR สลิป ⇒ null (ทิศตรงข้าม)</para>
/// </summary>
public static class ThaiSlipQr
{
    /// <summary>รหัส API เดียวที่พบบนสลิปจริง — ค่าอื่นยังไม่มีกระดาษยืนยัน ⇒ ไม่รับ</summary>
    public const string ApiId = "000001";

    /// <param name="SendingBankCode">รหัส 3 หลักของธนาคารผู้โอน (ตาราง <see cref="ThaiBankCodes"/>)</param>
    /// <param name="TransactionRef">เลขอ้างอิงรายการของธนาคาร (ตัวพิมพ์เล็ก/ใหญ่ตามจริง — อย่าแปลง)</param>
    public sealed record Payload(string SendingBankCode, string TransactionRef)
    {
        public ThaiBankCodes.BankInfo? SendingBank => ThaiBankCodes.ByCode(SendingBankCode);
    }

    public static Payload? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Length < 12 || !s.All(c => c >= 0x20 && c < 0x7F)) return null;
        // CRC อยู่ท้ายสุดเสมอ: "...9104XXXX"
        if (s.Length < 8 || s[^8..^4] != "9104") return null;
        if (!string.Equals(Crc16(s[..^4]), s[^4..], StringComparison.OrdinalIgnoreCase)) return null;
        var top = Tlv(s[..^8]);
        if (top == null || !top.TryGetValue("00", out var inner) || !top.TryGetValue("51", out var country) || country != "TH") return null;
        var sub = Tlv(inner);
        if (sub == null || !sub.TryGetValue("00", out var api) || api != ApiId) return null;
        if (!sub.TryGetValue("01", out var bank) || bank.Length != 3 || !bank.All(char.IsAsciiDigit)) return null;
        if (!sub.TryGetValue("02", out var tref) || tref.Length < 6 || !tref.All(char.IsAsciiLetterOrDigit)) return null;
        return new Payload(bank, tref);
    }

    /// <summary>CRC-16/CCITT-FALSE (ตัวเดียวกับ QR พร้อมเพย์ EMVCo) — ฐานสิบหกตัวใหญ่ 4 หลัก</summary>
    internal static string Crc16(string data)
    {
        var crc = 0xFFFF;
        foreach (var ch in data)
        {
            crc ^= (ch & 0xFF) << 8;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return crc.ToString("X4");
    }

    /// <summary>TLV ชั้นเดียว · แท็กซ้ำ/ความยาวเกินข้อความ/ตัวอักษรความยาวไม่ใช่ตัวเลข ⇒ null</summary>
    private static Dictionary<string, string>? Tlv(string s)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < s.Length)
        {
            if (i + 4 > s.Length) return null;
            var tag = s.Substring(i, 2);
            if (!tag.All(char.IsAsciiDigit) || !int.TryParse(s.AsSpan(i + 2, 2), out var len) || !s.Substring(i + 2, 2).All(char.IsAsciiDigit)) return null;
            if (i + 4 + len > s.Length) return null;
            if (!map.TryAdd(tag, s.Substring(i + 4, len))) return null;
            i += 4 + len;
        }
        return map;
    }
}
