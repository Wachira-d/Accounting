namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินตัวเดียวของป้าย "ต้นฉบับ / สำเนา" บนเอกสารที่พิมพ์ — ใช้ร่วมทั้ง HTML renderer (<c>PdfGenerationService.BuildDocumentHtml</c>)
/// และ QuestPDF (<c>PdfGenerationService.DocumentRenderer</c>) ⇒ สอง renderer ห้ามตัดสินเอง (กฎเหล็ก #4 A "สอง renderer ห้าม drift")
///
/// <para>ค่าใน <c>DocumentTemplate.CopyLabelPosition</c> (รอบ 202 · คำขอผู้ใช้ "อยากได้ ต้นฉบับ และ สำเนา ต่อท้ายหัวกระดาษ ·
/// ลายน้ำก็เป็นลายน้ำทั้งต้นฉบับและสำเนา"):</para>
/// <list type="bullet">
/// <item><c>TitleSuffix</c> — ต่อท้ายชื่อเอกสารทั้งคู่: "ใบเสนอราคา (ต้นฉบับ)" / "ใบเสนอราคา (สำเนา)" · ไม่มีลายน้ำ ต้นฉบับ/สำเนา</item>
/// <item><c>WatermarkBoth</c> — ลายน้ำกลางหน้าทั้งคู่: "ต้นฉบับ" / "สำเนา" · ไม่ต่อท้ายชื่อ</item>
/// <item><c>Watermark</c> — <b>ค่าเดิม (ผสม)</b>: ต้นฉบับต่อท้ายชื่อ · สำเนาเป็นลายน้ำ — คงชื่อค่าเดิมไว้ ⇒ เทมเพลตที่บันทึกแล้วพิมพ์เหมือนเดิมทุกใบ
/// (ไม่เปลี่ยนความหมายของค่าที่เก็บอยู่ = ไม่มีเอกสารของลูกค้าเปลี่ยนหน้าตาเงียบ ๆ)</item>
/// <item><c>TopRight</c> / <c>TopLeft</c> — กรอบเล็กมุมบนทั้งคู่ · ไม่ต่อท้ายชื่อ · ไม่มีลายน้ำ ต้นฉบับ/สำเนา</item>
/// </list>
/// <para>ลายน้ำที่ผู้ใช้ตั้งเองในเทมเพลต (เช่น "DRAFT") บนต้นฉบับยังพิมพ์ตามเดิมทุกโหมด · โหมด WatermarkBoth: ลายน้ำที่ตั้งเองชนะ "ต้นฉบับ"
/// (ไม่ซ้อนสองลายน้ำกลางหน้า) · ใบสำเนา: ป้ายสำเนาแทนลายน้ำที่ตั้งเองเสมอ (พฤติกรรมเดิม)</para>
/// </summary>
public static class CopyLabelPlacement
{
    public const string Mixed = "Watermark";
    public const string TitleSuffix = "TitleSuffix";
    public const string WatermarkBoth = "WatermarkBoth";
    public const string TopRight = "TopRight";
    public const string TopLeft = "TopLeft";

    /// <summary>ค่าที่รับได้ทั้งหมด (ลำดับ = ลำดับในหน้าตั้งค่า)</summary>
    public static readonly IReadOnlyList<string> All = new[] { TitleSuffix, WatermarkBoth, Mixed, TopRight, TopLeft };

    /// <summary>ค่าที่ผู้ใช้ส่งมา → ชื่อมาตรฐาน · ว่าง = null (ไม่เปลี่ยน) · ไม่รู้จัก = null (ผู้เรียกต้องปฏิเสธ ไม่ใช่เดา)</summary>
    public static string? Normalize(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return All.FirstOrDefault(a => string.Equals(a, v, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>ข้อความลายน้ำที่ส่งเข้ามาเป็นการพิมพ์สำเนาไหม (ตัวตรวจเดิมของทั้งสอง renderer — ย้ายมาไว้ที่เดียว)</summary>
    private static bool IsCopyText(string? watermark)
        => !string.IsNullOrWhiteSpace(watermark)
           && (watermark.Contains("สำเนา") || watermark.Contains("COPY", StringComparison.OrdinalIgnoreCase));

    /// <summary>ผลการตัดสิน — <paramref name="TitleSuffix"/> ต่อท้ายชื่อเอกสาร · <paramref name="Watermark"/> ลายน้ำกลางหน้า ·
    /// <paramref name="Corner"/> ป้ายกรอบมุมบน (<paramref name="CornerLeft"/> = มุมซ้าย) · <paramref name="IsCopy"/> = ใบนี้คือสำเนา</summary>
    public sealed record Decision(string? TitleSuffix, string? Watermark, string? Corner, bool CornerLeft, bool IsCopy);

    /// <param name="position">ค่า <c>CopyLabelPosition</c> ของเทมเพลต (ไม่รู้จัก/ว่าง = ค่าเดิม)</param>
    /// <param name="watermark">ลายน้ำที่มีผลของใบนี้ = ข้อความสำเนาที่สั่งพิมพ์ (ถ้ามี) ?? ลายน้ำที่ตั้งเองในเทมเพลต (ถ้าเปิด)</param>
    /// <param name="originalLabel">ป้าย "ต้นฉบับ" ตามภาษาเอกสาร (DocumentLabels)</param>
    /// <param name="copyLabel">ป้าย "สำเนา" ตามภาษาเอกสาร (DocumentLabels)</param>
    public static Decision Decide(string? position, string? watermark, string originalLabel, string copyLabel)
    {
        var mode = Normalize(position) ?? Mixed;
        var isCopy = IsCopyText(watermark);
        var label = isCopy ? copyLabel : originalLabel;
        // ลายน้ำที่ตั้งเอง (ไม่ใช่สำเนา) ของต้นฉบับ — ทุกโหมดคงไว้
        var custom = isCopy || string.IsNullOrWhiteSpace(watermark) ? null : watermark;
        return mode switch
        {
            TopRight or TopLeft => new Decision(null, custom, label, mode == TopLeft, isCopy),
            TitleSuffix => new Decision(label, custom, null, false, isCopy),
            // ใบสำเนาในโหมดลายน้ำ: พิมพ์ข้อความสำเนาที่สั่งมาตามตัว (พฤติกรรมเดิม — ผู้เรียกอาจส่ง "สำเนา" ที่มีเลขชุดต่อท้าย)
            WatermarkBoth => new Decision(null, isCopy ? watermark : custom ?? label, null, false, isCopy),
            _ => new Decision(isCopy ? null : label, isCopy ? watermark : custom, null, false, isCopy),
        };
    }
}
