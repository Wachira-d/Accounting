namespace Accounting.Helpers;

/// <summary>
/// **ที่อยู่ของแบรนด์มาจากไหน** — resolver กลางตัวเดียว ใช้ทั้ง renderer เอกสาร
/// และพรีวิวในหน้าตั้งค่า (พรีวิวคือ renderer ตัวที่สาม — ห้าม drift)
///
/// ═══ ทำไมต้องผูกกับทะเบียนได้ ═══
/// เดิมที่อยู่ของแบรนด์เป็นช่องพิมพ์เองล้วน ⇒ ย้ายออฟฟิศทีต้องไล่แก้ทุกแบรนด์
/// และแบรนด์ที่ลืมแก้จะพิมพ์ที่อยู่เก่าออกไปหาลูกค้าเงียบ ๆ
///
/// ═══ ขอบเขตทางกฎหมาย ═══
/// ไม่ว่าตั้งเป็นอะไร **เอกสารที่กฎหมายบังคับ** (ใบกำกับภาษี §86/4 · อย่างย่อ §86/6 ·
/// ใบเพิ่ม/ลดหนี้ §86/9-10 · ใบเสร็จ) ใช้ที่อยู่ของ **สถานประกอบการที่ออกใบ** เสมอ
/// (สาขาบนเอกสาร ถ้าไม่มีก็ที่อยู่บริษัท) ตาม ป.86/2542 — ที่อยู่หน้าร้านแบบ Custom
/// ทับไม่ได้. ด่านนั้นอยู่ที่ <see cref="DocumentIssuerIdentity"/> ตัวนี้แค่ตอบว่า
/// "ที่อยู่ของแบรนด์" คืออะไรสำหรับเอกสารที่แบรนด์ขึ้นหัวได้
/// </summary>
public static class BrandAddressSource
{
    public const string Company = "Company";
    public const string Branch = "Branch";
    public const string Custom = "Custom";

    /// <summary>ค่านอกลิสต์ตกไป Custom เสมอ (= พฤติกรรมเดิมของแถวที่สร้างก่อนมีฟีเจอร์นี้)</summary>
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim();
        return s is Company or Branch ? s : Custom;
    }

    /// <summary>
    /// ที่อยู่ที่แบรนด์นี้จะใช้ — **คืน null = ให้ผู้เรียกใช้ที่อยู่บริษัทตามเดิม**
    /// </summary>
    /// <param name="source">โหมดที่ตั้งไว้ (Company / Branch / Custom)</param>
    /// <param name="customAddress">ที่อยู่ที่พิมพ์เอง (ใช้เมื่อโหมด Custom)</param>
    /// <param name="branchAddress">ที่อยู่ของสาขาที่ผูกไว้ ประกอบเป็นบรรทัดเดียวแล้ว
    /// — ว่าง (สาขายังไม่กรอกที่อยู่ / สาขาถูกลบ) จะตกกลับไปใช้ที่อยู่บริษัท</param>
    public static string? Resolve(string? source, string? customAddress, string? branchAddress)
        => Normalize(source) switch
        {
            Company => null,                 // null = ใช้ที่อยู่บริษัท (แหล่งเดียว ไม่ drift)
            Branch => Blank(branchAddress),  // สาขาไม่มีที่อยู่ → null → ตกไปที่บริษัท
            _ => Blank(customAddress),
        };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
}
