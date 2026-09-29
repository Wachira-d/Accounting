using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **สี/รูปร่างของโต๊ะในผังร้าน — ค่าที่ต่อเข้า CSS <c>style</c>/<c>class</c> ของหน้า POS ได้อย่างปลอดภัย** (รอบ 200 ฝ่ายค้านทีม G · R200G-1)
///
/// <para>═══ ที่มา ═══ <c>PosFloorPlanController</c> รับ <c>Color</c>/<c>Shape</c> เป็นข้อความอิสระแล้วเก็บตรง ๆ ·
/// หน้า POS ต่อ <c>t.color</c> เข้า <c>style="…background:${bg}…"</c> และหน้าผังร้านต่อ <c>t.shape</c> เข้า <c>class="shape-${t.shape}"</c>
/// ⇒ ค่าที่มี <c>"</c> ปิด attribute แล้วเติม handler ได้ (JWT อยู่ใน localStorage ⇒ XSS = ขโมย token · กฎเหล็ก #4 C) —
/// หน้าเว็บส่งแค่สีจากจานสี/รูปร่างจาก dropdown แต่ API รับอะไรก็ได้</para>
///
/// <para>═══ กติกา ═══ ฝั่งเขียน: สีต้องเป็น hex <c>#rgb</c>…<c>#rrggbbaa</c> หรือว่าง · รูปร่างอยู่ในชุดปิด (ว่าง = สี่เหลี่ยมผืนผ้า) ·
/// ไม่ผ่าน = ปฏิเสธพร้อมเหตุผล (ไม่แก้ค่าเงียบ) · ฝั่งอ่าน: ค่าที่เก็บไว้ก่อนมีด่าน (ผ่าน API) ถูกส่งออกเป็นค่าปลอดภัย
/// (สีไม่ถูกรูป = ไม่มีสี · รูปร่างไม่รู้จัก = สี่เหลี่ยมผืนผ้า) — เป็นค่าการแสดงผลเท่านั้น ไม่มีตัวเลขเงิน/ภาษี</para>
///
/// <para>G6: pure · ไม่มี I/O</para>
/// </summary>
public static class PosTableStyle
{
    /// <summary>รูปร่างที่หน้าผังร้านรู้จัก (ตรงกับ <c>.fp-table.shape-*</c> ใน pos-floorplan.html)</summary>
    public static readonly IReadOnlyList<string> Shapes = new[] { "rectangle", "square", "circle" };

    /// <summary>รูปร่างเริ่มต้นเมื่อไม่ระบุ</summary>
    public const string DefaultShape = "rectangle";

    private static readonly Regex HexColor = new(@"^#[0-9A-Fa-f]{3,8}$", RegexOptions.CultureInvariant);

    /// <summary>สีที่ต่อเข้า CSS ได้โดยไม่แตก attribute — <c>null</c>/ว่าง ถือว่าถูก (= ไม่มีสี)</summary>
    private static bool IsValidColor(string? color)
        => string.IsNullOrEmpty(color) || HexColor.IsMatch(color);

    /// <summary>รูปร่างอยู่ในชุดปิด — <c>null</c>/ว่าง ถือว่าถูก (= ค่าเริ่มต้น)</summary>
    private static bool IsValidShape(string? shape)
        => string.IsNullOrEmpty(shape) || Shapes.Contains(shape);

    /// <summary>เหตุผลที่ปฏิเสธการบันทึก (ข้อความถึงผู้ใช้) · <c>null</c> = บันทึกได้</summary>
    public static string? RejectReason(string? tableNumber, string? shape, string? color)
    {
        if (!IsValidShape(shape))
            return $"โต๊ะ {tableNumber}: รูปร่างต้องเป็นหนึ่งใน {string.Join(" / ", Shapes)}";
        if (!IsValidColor(color))
            return $"โต๊ะ {tableNumber}: สีต้องเป็นรหัส hex เช่น #38bdf8 (หรือเว้นว่าง)";
        return null;
    }

    /// <summary>สีที่ส่งออกให้หน้าเว็บ — ค่าที่ไม่ถูกรูป (เก็บไว้ก่อนมีด่าน) = <c>null</c></summary>
    public static string? SafeColor(string? color)
        => string.IsNullOrEmpty(color) || !HexColor.IsMatch(color) ? null : color;

    /// <summary>รูปร่างที่ส่งออกให้หน้าเว็บ — ค่าที่ไม่รู้จัก = <see cref="DefaultShape"/></summary>
    public static string SafeShape(string? shape)
        => !string.IsNullOrEmpty(shape) && Shapes.Contains(shape) ? shape : DefaultShape;
}
