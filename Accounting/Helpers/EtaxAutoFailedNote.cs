namespace Accounting.Helpers;

/// <summary>
/// **ป้าย "ออก e-Tax อัตโนมัติไม่สำเร็จ" บนหมายเหตุภายในของเอกสาร — เจ้าของรูปแบบตัวเดียว** (รอบ 193 · ฝ่ายค้าน P-3/P-4)
///
/// <para>ที่มา: <c>IssuedDocumentHooks</c> ต่อท้าย <c>[ETAX-AUTO-FAILED] …</c> ลง <c>InternalNotes</c> แต่ไม่มีใครอ่านป้าย
/// (หน้าเอกสารไม่แสดง · ไม่มีรายงาน) และเมื่อผู้ใช้กด "สร้าง e-Tax" สำเร็จแล้วป้ายยังค้าง ⇒ "ล้มดัง" แค่ในนาม (F2 ข้อ 7).
/// ตอนนี้: เขียน/อ่าน/ล้างผ่านที่นี่ที่เดียว · เซิร์ฟเวอร์คำนวณ <see cref="Has"/> ส่งเป็นธงใน response ·
/// <c>EtaxInvoiceService.GenerateAsync</c> ล้มป้ายเมื่อออกสำเร็จ</para>
/// </summary>
public static class EtaxAutoFailedNote
{
    public const string Marker = "[ETAX-AUTO-FAILED]";

    /// <summary>ข้อความป้าย (ย่อหน้าเดียว)</summary>
    public static string Build(string reason)
        => $"{Marker} ออก e-Tax อัตโนมัติไม่สำเร็จ: {reason} — "
           + "กด “สร้าง e-Tax” ที่หน้ารายละเอียดเอกสารเพื่อลองใหม่ "
           + "(เอกสารนี้ยังไม่ถูกนำส่งกรมสรรพากร)";

    /// <summary>ต่อท้ายหมายเหตุภายในเดิม (ไม่ทับ — ช่องนี้เป็นที่สะสมของหลายด่าน) · ป้ายเดิมถูกแทนด้วยอันล่าสุด
    /// (ล้มซ้ำหลายรอบต้องไม่สะสมป้ายจนอ่านไม่ออก)</summary>
    public static string Append(string? existing, string reason)
    {
        var kept = Clear(existing);
        var note = Build(reason);
        return string.IsNullOrWhiteSpace(kept) ? note : kept!.TrimEnd() + "\n\n" + note;
    }

    /// <summary>มีป้ายค้างอยู่ไหม</summary>
    public static bool Has(string? notes)
        => !string.IsNullOrEmpty(notes) && notes.Contains(Marker, StringComparison.Ordinal);

    /// <summary>ถอดทุกย่อหน้าที่ขึ้นต้นด้วยป้าย (ย่อหน้าอื่นคงเดิมตามลำดับ) · ไม่มีป้าย = คืนค่าเดิมทุกตัวอักษร ·
    /// เหลือแต่ช่องว่าง = <c>null</c></summary>
    public static string? Clear(string? notes)
    {
        if (!Has(notes)) return notes;
        var paras = notes!.Replace("\r\n", "\n").Split("\n\n")
            .Where(p => !p.TrimStart().StartsWith(Marker, StringComparison.Ordinal));
        var joined = string.Join("\n\n", paras).Trim();
        return joined.Length == 0 ? null : joined;
    }
}
