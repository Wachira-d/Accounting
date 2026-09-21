namespace Accounting.Helpers;

/// <summary>ช่องหนึ่งช่องที่ไม่ผ่านการตรวจของ model binding</summary>
/// <param name="Field">ชื่อช่องแบบที่ฟอร์มใช้ (camelCase ตรงกับ <c>name="..."</c> บนหน้าเว็บ)</param>
/// <param name="Message">ข้อความไทยที่บอกว่าช่องนี้ผิดอย่างไร</param>
public readonly record struct ValidationFieldError(string Field, string Message);

/// <summary>ผลแปล ModelState ทั้งชุดเป็นภาษาที่ผู้ใช้อ่านรู้เรื่อง</summary>
/// <param name="Message">ข้อความสรุปที่ขึ้น toast — ต้อง<b>ระบุชื่อช่องเสมอ</b></param>
/// <param name="Fields">รายชื่อช่อง (camelCase) ให้หน้าเว็บไปหา <c>[name=...]</c> แล้วชี้ป้ายไทยจริง</param>
/// <param name="Errors">ข้อความรายช่อง เรียงตาม <paramref name="Fields"/></param>
public readonly record struct ValidationErrorSummary(string Message, List<string> Fields, List<string> Errors);

/// <summary>
/// **ตัวแปล ModelState → ข้อความไทยที่บอกว่า "ช่องไหน ขาดอะไร"** — pure · ไม่รับ HttpContext
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-09-21) ═══
/// กด "💾 บันทึกการตั้งค่า" ที่หน้าตั้งค่าที่พัก แล้วได้ toast แดง:
/// <code>One or more validation errors occurred. — Code: The Code field is required.</code>
/// คำร้องของผู้ใช้ตรงตัว: *"ไม่มีบอกว่า Require อันไหน หรือ ขาดอะไร อันไหน"* — และถูก:
/// <list type="bullet">
/// <item>ข้อความเป็น**อังกฤษ**ทั้งประโยค ทั้งที่ทั้งระบบเป็นไทย (CLAUDE.md "ภาษา")</item>
/// <item>ชื่อ <c>Code</c> เป็น**ชื่อ property ใน C#** ซึ่งไม่ตรงกับป้ายใด ๆ ที่ผู้ใช้เห็น
///   (ป้ายจริงบนหน้าคือ "รหัส (ใช้ในเลขจอง RES-XXXX-…)") ⇒ หาไม่เจอว่าต้องแก้ตรงไหน</item>
/// <item>ช่องนั้น**ไม่มีดอกจัน ไม่มี <c>required</c>** บนฟอร์ม ⇒ หน้าเว็บบอกว่า "ไม่บังคับ"
///   แต่เซิร์ฟเวอร์บอกว่า "บังคับ" — ผู้ใช้ไม่มีทางเดาได้</item>
/// </list>
///
/// ═══ ทำไมข้อความถึงเป็นอังกฤษตั้งแต่แรก ═══
/// โปรเจกต์เปิด <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c> ⇒ ASP.NET Core ใส่
/// <c>[Required]</c> <b>โดยปริยาย</b> ให้ property ชนิด reference ที่ไม่ได้ประกาศ
/// <c>?</c> ทุกตัว (implicit required for non-nullable reference types) แล้วตอบด้วย
/// ข้อความมาตรฐานของ framework <c>"The {0} field is required."</c> — ด่านนี้ทำงาน
/// **ก่อน** โค้ดใน service ทั้งหมด ⇒ <c>BusinessRuleException</c> ภาษาไทยที่เขียนไว้
/// อย่างดี (เช่น "กรุณาระบุหมายเลขห้อง") <b>ไม่เคยถูกเรียก</b>ในเคส null
/// (ตรงกับ F2 ข้อ 2 "มี ≠ ถูกเรียก")
///
/// ═══ ทำไมไม่ปิด implicit required ทั้งระบบ ═══
/// มี property แบบนี้ 71 ตัวใน <c>Models/DTOs/</c> · ปิดทีเดียวแปลว่า null ไหลเข้า
/// <c>d.Name.Trim()</c> ได้ ⇒ NullReferenceException 500 ซึ่ง<b>แย่กว่า</b>และเงียบกว่า
/// (DECISION_DOCTRINE §1 G5 "ทิศปลอดภัย = ทิศที่ความเสียหายมองเห็นและแก้ทัน")
/// ⇒ เก็บด่านไว้ แต่ทำให้มัน**พูดไทยและชี้ช่องได้** และแก้ DTO ที่ประกาศผิดทีละตัว
/// โดยมี <c>tools/dto_nullable_contract_check.py</c> กันไม่ให้กลับมา
///
/// ═══ ขอบเขตของ helper นี้ (สำคัญ — G3 "ไม่รู้ต้องบอกว่าไม่รู้") ═══
/// เซิร์ฟเวอร์รู้แค่**ชื่อ property** ไม่รู้ป้ายไทยบนหน้าจอ (ป้ายอยู่ใน HTML) ⇒
/// helper นี้**ห้ามแต่งป้ายไทยขึ้นเอง** มันคืนชื่อช่องแบบ camelCase ที่ตรงกับ
/// <c>name="..."</c> บนฟอร์มให้หน้าเว็บไปอ่านป้ายจริงจาก DOM ของตัวเอง
/// (F2 ข้อ 5 "Server computes · page displays" — ไม่มีสำเนาป้ายที่ฝั่งเซิร์ฟเวอร์)
/// ถ้าหน้าเว็บหาช่องนั้นไม่เจอ หน้าเว็บต้องบอกตรง ๆ ว่า "ไม่มีช่องนี้บนหน้านี้"
/// ไม่ใช่สั่งให้ผู้ใช้ไปกรอกของที่มองไม่เห็น
/// </summary>
public static class ValidationErrorText
{
    public const string RuleCode = "API-MODELSTATE";

    // CamelCase / NormalizeKey / TranslateMessage เป็น `private` โดยตั้งใจ — เป็นขั้นตอน
    // ภายในของ Describe ไม่ใช่ API ของ helper · เทสต์ยิงผ่าน Describe ทั้งหมด
    // (helper public ที่ไม่มีผู้เรียกนอกไฟล์ = "มี ≠ ถูกเรียก" ที่ tools/dead_helper_check.py
    //  ฟ้อง และเทสต์ที่ยิงตรงเข้า private step จะผ่านได้แม้เส้นจริงไม่เคยเดินผ่านมัน)

    // ข้อความมาตรฐานของ ASP.NET Core / System.Text.Json ที่เจอบ่อยที่สุด
    // (ตรวจแบบ "ขึ้นต้นด้วย/มีคำนี้" เพราะ framework แทรกชื่อ property ไว้กลางประโยค)
    private const string FrameworkRequired = "field is required";
    private const string FrameworkNotValid = "is not valid for";
    private const string FrameworkJsonConvert = "could not be converted";
    private const string FrameworkInvalidStart = "is an invalid start of a value";

    /// <summary>แปลงชื่อ property C# → ชื่อที่อยู่ใน JSON/ฟอร์ม (อัลกอริทึมเดียวกับ
    /// <c>JsonNamingPolicy.CamelCase</c> ที่ <c>Program.cs</c> ตั้งไว้ — <c>Code</c> → <c>code</c> ·
    /// <c>URL</c> → <c>url</c> · <c>URLValue</c> → <c>urlValue</c>)</summary>
    private static string CamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0])) return name;
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i > 0 && i + 1 < chars.Length && !char.IsUpper(chars[i + 1])) break;
            chars[i] = char.ToLowerInvariant(chars[i]);
        }
        return new string(chars);
    }

    /// <summary>คีย์ของ ModelState มาได้ 2 ทรง — ชื่อ property ตรง ๆ (<c>Code</c>) จากด่าน
    /// validation และ JSON path (<c>$.code</c> · <c>$.lines[0].unitPrice</c>) จากด่าน
    /// deserialize · ทำให้เหลือทรงเดียวที่หน้าเว็บเอาไปหา <c>[name=...]</c> ได้</summary>
    private static string NormalizeKey(string key)
    {
        var k = (key ?? "").Trim();
        if (k.StartsWith("$.", StringComparison.Ordinal)) k = k[2..];
        else if (k == "$") k = "";
        if (k.Length == 0) return "";
        // เอาเฉพาะส่วนสุดท้ายของ path มาทำ camelCase ไม่ได้ — ช่องซ้อน (lines[0].qty)
        // ต้องคงรูปไว้ทั้งเส้น เพื่อให้หน้าเว็บรู้ว่าเป็นบรรทัดที่เท่าไร
        return string.Join('.', k.Split('.').Select(CamelCase));
    }

    /// <summary>แปลข้อความมาตรฐานของ framework เป็นไทย · ข้อความที่แปลไม่ได้
    /// <b>ส่งต่อตามเดิม</b> พร้อมป้ายว่ามาจากระบบ (ห้ามกลืนแล้วแต่งใหม่ — F2 ข้อ 3)</summary>
    private static string TranslateMessage(string? raw)
    {
        var m = (raw ?? "").Trim();
        if (m.Length == 0) return "ค่าไม่ถูกต้อง";
        // ข้อความไทยอยู่แล้ว (มาจาก [Required(ErrorMessage="...")] ของเราเอง) → ใช้ตามนั้น
        if (m.Any(ch => ch is >= '฀' and <= '๿')) return m;
        if (m.Contains(FrameworkRequired, StringComparison.OrdinalIgnoreCase))
            return "ต้องมีค่า — เว้นว่างไม่ได้";
        if (m.Contains(FrameworkNotValid, StringComparison.OrdinalIgnoreCase)
            || m.Contains(FrameworkJsonConvert, StringComparison.OrdinalIgnoreCase)
            || m.Contains(FrameworkInvalidStart, StringComparison.OrdinalIgnoreCase))
            return "ค่าที่กรอกผิดชนิด (เช่น กรอกตัวอักษรในช่องตัวเลข หรือวันที่ผิดรูปแบบ)";
        return $"ค่าไม่ถูกต้อง (ระบบแจ้งว่า: {m})";
    }

    /// <summary>รวม ModelState ทั้งชุดเป็นข้อความเดียวที่<b>ระบุชื่อช่องเสมอ</b></summary>
    /// <param name="entries">คู่ (คีย์ ModelState, ข้อความ) — ผู้เรียกดึงจาก
    /// <c>ActionContext.ModelState</c> เพื่อให้ helper นี้เทสต์ได้โดยไม่ต้องลาก ASP.NET มา</param>
    public static ValidationErrorSummary Describe(IEnumerable<(string Key, IEnumerable<string> Messages)> entries)
    {
        var fields = new List<string>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bodyLevel = 0;   // ผิดที่ตัว request ทั้งก้อน ไม่ใช่ช่องใดช่องหนึ่ง

        foreach (var (key, messages) in entries)
        {
            var field = NormalizeKey(key);
            var text = TranslateMessage(messages?.FirstOrDefault());
            if (field.Length == 0) { bodyLevel++; errors.Add(text); continue; }
            if (!seen.Add(field)) continue;
            fields.Add(field);
            errors.Add(text);
        }

        if (fields.Count == 0)
        {
            var detail = errors.Count > 0 ? " — " + string.Join(" · ", errors) : "";
            return new ValidationErrorSummary(
                $"ข้อมูลที่ส่งมาไม่ถูกต้อง{detail} (ระบบไม่ได้ระบุว่าเป็นช่องไหน — "
                + "ถ้าเกิดซ้ำกรุณาแจ้งผู้ดูแลพร้อมชื่อหน้าและปุ่มที่กด)",
                fields, errors);
        }

        var pairs = string.Join(" · ",
            fields.Select((f, i) => $"{f}: {(i < errors.Count ? errors[i] : "ค่าไม่ถูกต้อง")}"));
        var head = fields.Count == 1
            ? $"บันทึกไม่ได้ — มี 1 ช่องที่ยังไม่ผ่าน"
            : $"บันทึกไม่ได้ — มี {fields.Count} ช่องที่ยังไม่ผ่าน";

        return new ValidationErrorSummary(
            $"{head}: {pairs} · หน้าเว็บจะไฮไลต์ช่องที่ต้องแก้ให้ "
            + "ถ้าไม่พบช่องนั้นบนหน้าจอ แปลว่าฟอร์มไม่ได้ส่งค่ามาเอง — กรุณาแจ้งผู้ดูแลระบบ",
            fields, errors);
    }
}
