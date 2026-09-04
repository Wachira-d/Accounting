using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ผลการตรวจค่าที่ระบบภายนอกส่งมาลงช่องที่อยู่</summary>
public readonly record struct AddressFieldCheck(bool Accepted, string? Rejected, string? Reason)
{
    public static AddressFieldCheck Ok() => new(true, null, null);
    public static AddressFieldCheck Reject(string value, string reason) => new(false, value, reason);
}

/// <summary>
/// **ด่านตรวจค่าที่ระบบภายนอกยัดลงช่องที่อยู่แบบมีโครง**
///
/// ═══ ที่มา (บั๊กจริง — ผู้ใช้รายงาน 2026-09-03) ═══
/// TakeTime ส่งค่า <c>"ทะเบียนการค้า : 0105564045849 บริษั"</c> มาลงช่อง <b>หมู่ที่</b>
/// ⇒ ที่อยู่บนใบกำกับภาษี (§86/4 บังคับให้ที่อยู่ถูกต้อง) มีข้อความที่ไม่ใช่ที่อยู่
/// ปนอยู่ · อาการคือ<b>ฟิลด์เหลื่อม</b>ฝั่งต้นทาง (ข้อความยาวถูกตัดกลางคำด้วย ซึ่ง
/// เป็นลายเซ็นของการ truncate) — เราแก้ที่ต้นทางไม่ได้ แต่ต้องไม่ปล่อยให้ไหลลงเอกสาร
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item>ช่องที่อยู่แบบมีโครง (หมู่ที่ · ตำบล · อำเภอ · จังหวัด · รหัสไปรษณีย์) เป็น
///   ค่า<b>สั้น</b>เสมอ — ค่ายาวผิดปกติ = ฟิลด์เหลื่อม</item>
/// <item>ค่าที่มี <b>เลข 13 หลัก</b> หรือคำอย่าง "ทะเบียน"/"เลขประจำตัวผู้เสียภาษี"
///   คือเนื้อหาของ<b>ช่องอื่น</b> ไม่ใช่ที่อยู่</item>
/// <item><b>ห้ามทิ้งเงียบ</b> — ค่าที่ถูกปฏิเสธต้องถูกเก็บไว้ให้คนเห็น (หมายเหตุ + log)
///   เพราะมันคือหลักฐานว่าต้นทางส่งผิด และอาจมีข้อมูลจริงปนอยู่ในนั้น
///   (กฎ "ห้าม silent no-op" + "ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ")</item>
/// </list>
/// </summary>
public static class InboundAddressSanity
{
    /// <summary>ความยาวสูงสุดที่สมเหตุสมผลของช่องที่อยู่แบบมีโครง
    ///
    /// <para>ชื่อตำบล/อำเภอไทยที่ยาวที่สุดยังไม่ถึง 40 ตัวอักษร · เผื่อไว้ที่ 60
    /// เพื่อไม่ไปปฏิเสธชื่อหมู่บ้าน/อาคารที่ยาวจริง</para></summary>
    public const int MaxStructuredLength = 60;

    private static readonly Regex ThirteenDigits = new(@"\d[- \t]?\d[- \t]?\d[- \t]?\d[- \t]?\d[- \t]?\d[- \t]?\d[- \t]?\d[- \t]?\d[- \t]?\d[- \t]?\d[- \t]?\d[- \t]?\d",
        RegexOptions.Compiled);

    /// <summary>คำที่บอกว่าค่านี้เป็นเนื้อหาของช่องอื่น</summary>
    private static readonly string[] ForeignFieldMarkers =
    {
        "ทะเบียน", "เลขประจำตัวผู้เสียภาษี", "เลขผู้เสียภาษี", "ผู้เสียภาษี",
        "tax id", "taxid", "vat no", "reg no",
    };

    /// <summary>ตรวจค่าที่จะลงช่องที่อยู่แบบมีโครง 1 ช่อง</summary>
    public static AddressFieldCheck CheckStructured(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AddressFieldCheck.Ok();
        var v = value.Trim();

        if (v.Length > MaxStructuredLength)
            return AddressFieldCheck.Reject(v,
                $"ยาวเกินปกติ ({v.Length} ตัวอักษร) — น่าจะเป็นข้อมูลของช่องอื่นที่ถูกส่งมาผิดช่อง");

        foreach (var marker in ForeignFieldMarkers)
            if (v.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return AddressFieldCheck.Reject(v,
                    $"มีคำว่า \"{marker}\" — เป็นเนื้อหาของช่องอื่น ไม่ใช่ที่อยู่");

        if (ThirteenDigits.IsMatch(v))
            return AddressFieldCheck.Reject(v,
                "มีเลข 13 หลัก (น่าจะเป็นเลขผู้เสียภาษี/เลขบัตรประชาชน) — ไม่ใช่ที่อยู่");

        return AddressFieldCheck.Ok();
    }

    /// <summary>รหัสไปรษณีย์ไทยต้องเป็นตัวเลข 5 หลักเท่านั้น
    ///
    /// <para>แยกจาก <see cref="CheckStructured"/> เพราะมีรูปแบบตายตัว และค่าที่ผิดรูป
    /// ทำให้ e-Tax XML (ETDA ขมธอ.3) ไม่ผ่าน validation</para></summary>
    public static AddressFieldCheck CheckPostalCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AddressFieldCheck.Ok();
        var v = value.Trim();
        return v.Length == 5 && v.All(char.IsDigit)
            ? AddressFieldCheck.Ok()
            : AddressFieldCheck.Reject(v, "รหัสไปรษณีย์ต้องเป็นตัวเลข 5 หลัก");
    }
}
