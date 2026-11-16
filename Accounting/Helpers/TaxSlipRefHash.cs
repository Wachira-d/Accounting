using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **แฮช/ลายเซ็นของเส้นสลิปภาษี — ฟังก์ชัน canonical เดียว** (รอบ 272 · ดีไซน์ §3.2/§7 · กฎ #4 C "Hash/signature มี canonical function เดียว"):
/// Control Code · เลขที่รายการธนาคาร · ปุ่มเลือกในกลุ่ม (postback) — ใช้ทั้งฝั่งเขียนและฝั่งตรวจ · <b>ค่าเต็มไม่ลงฐาน</b> (เก็บแฮช + 4 หลักท้าย · PDPA ม.22)
/// <para>กุญแจ HMAC มาจาก <c>ENCRYPTION_KEY</c> (แหล่งเดียวกับ <c>EncryptionKeyResolver</c>) — ตั้งครั้งเดียวตอนบูตผ่าน <see cref="ConfigureKey"/> ·
/// production ไม่มีคีย์/เป็น dev key = boot ไม่ขึ้น (<c>EncryptionKeyResolver.RequireUsable</c> ใน Program.cs · กฎ #4 C fail-fast)</para>
/// </summary>
public static class TaxSlipRefHash
{
    private static byte[] _key = Derive(EncryptionKeyResolver.DevKey);

    private static byte[] Derive(string serverKey) => SHA256.HashData(Encoding.UTF8.GetBytes("nextacc/tax-slip-ref/v1|" + serverKey));

    /// <summary>ตั้งกุญแจ (Program.cs) · ว่าง = throw (ห้าม fallback เงียบ)</summary>
    public static void ConfigureKey(string serverKey)
    {
        if (string.IsNullOrWhiteSpace(serverKey)) throw new InvalidOperationException("ต้องมีกุญแจฝั่งเซิร์ฟเวอร์สำหรับแฮชเลขอ้างอิงสลิปภาษี (ENCRYPTION_KEY)");
        _key = Derive(serverKey);
    }

    private static string Mac(string purpose, string value)
        => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(purpose + "|" + value))).ToLowerInvariant();

    /// <summary>ตัดให้เหลือตัวเลข/ตัวอักษรพิมพ์ใหญ่ (สลิปกับใบแจ้งชำระพิมพ์ขีด/ช่องว่างต่างกัน)</summary>
    public static string Canon(string? value) => new((value ?? "").Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>แฮช Control Code — ว่าง ⇒ null</summary>
    public static string? ControlCode(string? value) => Canon(value) is { Length: > 0 } c ? Mac("cc", c) : null;

    /// <summary>แฮชเลขที่รายการธนาคาร (ผูกธนาคาร — เลขเดียวกันคนละธนาคารไม่ชนกัน) — ว่าง ⇒ null</summary>
    public static string? BankTxn(string? bankCode, string? value) => Canon(value) is { Length: > 0 } c ? Mac("txn", (bankCode ?? "-") + ":" + c) : null;

    /// <summary>4 ตัวท้าย (ป้าย "••1234") — ว่าง ⇒ null</summary>
    public static string? Last4(string? value) => Canon(value) is { Length: > 0 } c ? c[^Math.Min(4, c.Length)..] : null;

    public const string PostbackPrefix = "tsc";

    /// <summary>ลายเซ็นปุ่มเลือกในกลุ่ม (สลิป · ลำดับผู้สมัคร · ช่องทาง) — ปุ่มที่ไม่ได้ออกโดยระบบกดไม่ได้</summary>
    public static string Sign(Guid slipId, int index, string sourceKey)
        => Mac("pb", slipId.ToString("N") + "|" + index + "|" + sourceKey)[..24];

    public static string PostbackData(Guid slipId, int index, string sourceKey)
        => string.Join('|', PostbackPrefix, slipId.ToString("N"), index.ToString(System.Globalization.CultureInfo.InvariantCulture), Sign(slipId, index, sourceKey));

    public sealed record Postback(Guid SlipId, int Index, string Signature);

    public static bool IsOurs(string? data) => data != null && data.StartsWith(PostbackPrefix + "|", StringComparison.Ordinal);

    /// <summary>อ่าน postback · รูปผิด ⇒ null · <paramref name="index"/> = -1 คือ "ไม่ใช่ทั้งหมด"</summary>
    public static Postback? TryParse(string? data)
    {
        if (!IsOurs(data)) return null;
        var p = data!.Split('|');
        if (p.Length != 4 || !Guid.TryParseExact(p[1], "N", out var id) || !int.TryParse(p[2], out var idx) || idx < -1 || idx > 9) return null;
        return new Postback(id, idx, p[3]);
    }

    public static bool Verify(Postback pb, string sourceKey)
    {
        var expected = Encoding.ASCII.GetBytes(Sign(pb.SlipId, pb.Index, sourceKey));
        var given = Encoding.ASCII.GetBytes(pb.Signature ?? "");
        return expected.Length == given.Length && CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
