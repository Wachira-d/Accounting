namespace Accounting.Helpers;

/// <summary>
/// คำนำหน้าชื่อสำหรับแบบราชการไทย — ระบบ e-Service ของประกันสังคมรับเฉพาะ
/// คำนำหน้าไทย (ปฏิเสธทั้งแถวพร้อมข้อความ "รหัสคำนำหน้า Mrs. ไม่ถูกต้อง")
/// ข้อมูลคำนำหน้าอังกฤษหลุดเข้ามาได้ทางหน้า import / sync API (หน้าจอปกติ
/// เป็น dropdown ไทย) จึงต้อง normalize ทั้งตอนบันทึกและตอนสร้างไฟล์ยื่น
/// </summary>
public static class ThaiTitleHelper
{
    /// <summary>คำนำหน้าที่ สปส. ยอมรับในไฟล์เงินสมทบ</summary>
    public static readonly HashSet<string> SsoValidTitles = new()
    { "นาย", "นาง", "นางสาว", "เด็กชาย", "เด็กหญิง" };

    /// <summary>แปลงคำนำหน้าอังกฤษ/ตัวย่อที่พบบ่อย → ไทย. คำที่ไม่รู้จัก
    /// (เช่น "ดร." ที่ผู้ใช้ตั้งใจเลือก) คืนค่าเดิม — ปลอดภัยต่อการเรียกตอน
    /// บันทึกข้อมูลพนักงาน เพราะแตะเฉพาะคำที่แปลได้ชัดเจนเท่านั้น</summary>
    public static string Normalize(string? title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return t;
        // ตารางกลางมาก่อนเสมอ — เดิม switch ข้างล่างไม่รู้จัก "ดร"/"บจก."/"หจก."
        // ที่ตารางรู้จัก ⇒ สองตัวตอบไม่ตรงกันในไฟล์เดียวกัน
        foreach (var p in All)
        {
            if (string.Equals(p.Thai, t, StringComparison.OrdinalIgnoreCase)) return p.Thai;
            foreach (var a in p.Aliases)
                if (string.Equals(a, t, StringComparison.OrdinalIgnoreCase)) return p.Thai;
        }
        var key = t.TrimEnd('.').Trim().ToUpperInvariant();
        return key switch
        {
            "MR" or "MISTER" => "นาย",
            "MRS" or "MADAM" or "MADAME" => "นาง",
            "MISS" or "MS" => "นางสาว",
            "MASTER" => "เด็กชาย",
            "น.ส" or "นส" => "นางสาว",
            "ด.ช" or "ดช" => "เด็กชาย",
            "ด.ญ" or "ดญ" => "เด็กหญิง",
            _ => t,
        };
    }

    /// <summary>สำหรับไฟล์ยื่น สปส. โดยเฉพาะ: Normalize ก่อน ถ้ายังไม่อยู่ใน
    /// ชุดที่ สปส. รับ ให้เดาจากเพศ (ชาย=นาย, หญิง=นางสาว — สปส. รับ นางสาว
    /// สำหรับผู้หญิงทุกกรณี) ถ้าเดาไม่ได้คืนค่าเดิมให้ผู้เรียกแจ้งเตือน</summary>
    public static string NormalizeForSso(string? title, string? gender = null)
    {
        var t = Normalize(title);
        if (SsoValidTitles.Contains(t)) return t;
        var g = (gender ?? "").Trim().ToUpperInvariant();
        if (g is "M" or "MALE" or "ชาย") return "นาย";
        if (g is "F" or "FEMALE" or "หญิง") return "นางสาว";
        return t;
    }

    /// <summary>true เมื่อคำนำหน้าใช้ยื่น สปส. ได้โดยไม่โดนปฏิเสธ</summary>
    public static bool IsValidForSso(string? title) => SsoValidTitles.Contains((title ?? "").Trim());

    // =====================================================================
    // ตารางคำนำหน้าชื่อกลางของระบบ — **ที่เดียวเท่านั้น**
    //
    // เดิมความรู้ชุดนี้ถูกคัดลอกไว้ 3 ที่ที่ไม่ตรงกัน: ลิสต์ "ไว้ตัดทิ้ง" ใน
    // PndTextFileFormat (มี ดร. แต่ไม่มี เด็กชาย/เด็กหญิง/นิติบุคคล) ·
    // TaxFilingExportService.TitleCode (มีนิติบุคคล แต่ไม่มี ดร./เด็กชาย) ·
    // SsoValidTitles ข้างบน ⇒ ผู้ถูกหักชื่อ "เด็กชาย สมชาย ใจดี" ได้
    // Col4="เด็กชาย" Col5="สมชาย ใจดี" บนไฟล์ยื่น (ชื่อจริงกลายเป็นนามสกุล)
    // ตามกฎ CLAUDE.md: ตารางเชิงกฎหมายต้องมีที่เดียว แล้วทุกเส้นอ่านจากตัวนี้
    // =====================================================================

    /// <summary>คำนำหน้า 1 รายการ</summary>
    /// <param name="Thai">รูปเต็มภาษาไทย — ค่านี้คือสิ่งที่ลงไฟล์ยื่น (หน้า
    /// import ของ RD รับ **ข้อความไทย** ไม่ใช่รหัส — ยืนยันกับผู้ใช้ 2026-09-16)</param>
    /// <param name="RdCode">รหัสกรมสรรพากรสำหรับไฟล์ "สื่อบันทึก" H|D|T
    /// (ภ.ง.ด.1/1ก) ซึ่งเป็นคนละรูปแบบกับไฟล์นำเข้าเว็บของ ภ.ง.ด.3/53</param>
    /// <param name="IsJuristic">true = รูปแบบนิติบุคคล/คณะบุคคล (ไม่มีนามสกุล)</param>
    /// <param name="Aliases">รูปย่อ/อังกฤษที่พบบนกระดาษและไฟล์นำเข้า</param>
    public sealed record TitlePrefix(string Thai, string RdCode, bool IsJuristic, string[] Aliases);

    /// <summary>ตารางกลาง — เรียงยาวไปสั้นตอนจับคู่เสมอ (ไม่งั้น "นาง" กิน "นางสาว")</summary>
    public static readonly IReadOnlyList<TitlePrefix> All = new List<TitlePrefix>
    {
        new("นาย",       "1", false, new[] { "Mr.", "Mr" }),
        new("นาง",       "2", false, new[] { "Mrs.", "Mrs" }),
        new("นางสาว",    "3", false, new[] { "น.ส.", "น.ส", "นส.", "Miss", "Ms.", "Ms" }),
        new("เด็กชาย",   "9", false, new[] { "ด.ช.", "ด.ช", "ดช.", "Master" }),
        new("เด็กหญิง",  "9", false, new[] { "ด.ญ.", "ด.ญ", "ดญ." }),
        new("ดร.",       "9", false, new[] { "ดร", "Dr.", "Dr" }),
        new("บริษัท",    "4", true,  new[] { "บจก.", "บมจ.", "บริษัทมหาชนจำกัด" }),
        new("ห้างหุ้นส่วนจำกัด",  "5", true, new[] { "หจก.", "หจก" }),
        new("ห้างหุ้นส่วนสามัญ",  "5", true, new[] { "หสน.", "หสน" }),
        new("คณะบุคคล",  "6", true,  Array.Empty<string>()),
        new("มูลนิธิ",    "7", true,  Array.Empty<string>()),
        new("สมาคม",     "7", true,  Array.Empty<string>()),
    };

    /// <summary>รหัสคำนำหน้าของกรมสรรพากรสำหรับไฟล์ H|D|T (ภ.ง.ด.1/1ก).
    /// ไม่รู้จัก → "9" (อื่น ๆ) ตามตารางของ RD</summary>
    public static string RdCode(string? title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return "9";
        foreach (var p in All)
        {
            if (string.Equals(p.Thai, t, StringComparison.OrdinalIgnoreCase)) return p.RdCode;
            foreach (var a in p.Aliases)
                if (string.Equals(a, t, StringComparison.OrdinalIgnoreCase)) return p.RdCode;
        }
        return "9";
    }

    /// <summary>true เมื่อคำนำหน้านั้นเป็นรูปแบบนิติบุคคล/คณะบุคคล</summary>
    public static bool IsJuristicTitle(string? title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return false;
        foreach (var p in All)
        {
            if (!p.IsJuristic) continue;
            if (string.Equals(p.Thai, t, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var a in p.Aliases)
                if (string.Equals(a, t, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// แยก "คำนำหน้า" ออกจากชื่อเต็มที่ผู้ใช้พิมพ์รวมกันมา คืนรูปเต็มภาษาไทย
    /// เสมอ (เช่น "น.ส." → "นางสาว") · ไม่พบ → <c>("", ชื่อเดิม)</c>
    ///
    /// ⚠️ <b>ด่านกันตัดชื่อร้าน</b>: ตัดเฉพาะเมื่อคำนำหน้าตามด้วย**ช่องว่าง**
    /// หรือส่วนที่เหลือ**มีช่องว่างอย่างน้อยหนึ่งตัว** (= มีทั้งชื่อและสกุล)
    /// ⇒ "นายช่างการไฟฟ้า" · "นางเลิ้งพาณิชย์" · "นายหน้าประกันภัย" ไม่ถูกแตะ
    /// เพราะเป็นชื่อกิจการที่บังเอิญขึ้นต้นเหมือนคำนำหน้า — การเดาผิดตรงนี้
    /// ไหลไปถึงชื่อบนไฟล์ยื่น ภ.ง.ด. และใบกำกับภาษี
    /// </summary>
    public static (string Title, string Rest) Split(string? fullName)
    {
        var name = (fullName ?? "").Trim();
        if (name.Length == 0) return ("", "");

        // ยาวไปสั้น — "นางสาว" ต้องชนะ "นาง" · "ห้างหุ้นส่วนจำกัด" ต้องชนะ ""
        var candidates = All
            .SelectMany(p => p.Aliases.Append(p.Thai)
                .Select(form => (Form: form, Canonical: p.Thai, IsCanonical: form == p.Thai)))
            .OrderByDescending(x => x.Form.Length)
            .ToList();

        foreach (var (form, canonical, isCanonical) in candidates)
        {
            if (!name.StartsWith(form, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = name[form.Length..].TrimStart();
            if (rest.Length == 0) continue;                       // ทั้งชื่อเป็นคำนำหน้า = ไม่ใช่คำนำหน้า
            var followedBySpace = char.IsWhiteSpace(name[form.Length]);
            if (!followedBySpace)
            {
                if (!rest.Contains(' ')) continue;                // ชื่อร้าน — ห้ามตัด
                // ⚠️ ติดกันโดยไม่มีช่องว่าง: ยอมเฉพาะรูปที่ "จบในตัวเอง" —
                // รูปเต็มภาษาไทย ("นางสาวสมหญิง ใจดี") หรือตัวย่อที่มีจุดปิด
                // ("น.ส.สมหญิง ใจดี"). ตัวย่อที่**ไม่มีจุด** ("ดร" · "Dr" · "นส")
                // เป็นแค่ต้นคำของคำอื่นได้ ⇒ "ดรุณี ใจดี" เคยถูกตัดเป็น
                // ("ดร.", "ุณี ใจดี") และ "Drake Co Ltd" เป็น ("ดร.", "ake Co Ltd")
                if (!isCanonical && !form.EndsWith('.')) continue;
                // ชื่อไทยไม่มีทางขึ้นต้นด้วยสระบน/ล่าง/วรรณยุกต์ — ถ้าตัวถัดไป
                // เป็นเครื่องหมายผสม แปลว่าเรากำลังผ่ากลางคำเดียวกัน
                if (IsThaiCombiningMark(rest[0])) continue;
            }
            return (canonical, rest);
        }
        return ("", name);
    }

    /// <summary>สระบน/สระล่าง/วรรณยุกต์/เครื่องหมายไทยที่ "เกาะ" พยัญชนะตัวหน้า —
    /// ตัวอักษรกลุ่มนี้ขึ้นต้นคำไม่ได้</summary>
    private static bool IsThaiCombiningMark(char c)
        => c == '\u0E31' || (c >= '\u0E33' && c <= '\u0E3A') || (c >= '\u0E47' && c <= '\u0E4E');

    /// <summary>
    /// ด่านตรวจคำนำหน้าชื่อ — **ตัวตัดสินตัวเดียว**ของทุกทางเข้า (ฟอร์ม · API ·
    /// import · ไฟล์ยื่น). รับรูปย่อ/อังกฤษแล้วคืน "รูปเต็มภาษาไทย" ที่ลงไฟล์ได้
    ///
    /// คืน <c>reason</c> เป็น**ข้อความไทยที่เอาไปโชว์ได้ทันที** ไม่ใช่ <c>bool</c>
    /// เปล่า ๆ — ไม่งั้นแต่ละหน้าจอจะไปแต่งคำเอง = สำเนามือชุดถัดไป (กฎ CLAUDE.md
    /// เดียวกับ <c>PayrollRunEditPolicy.CanVoid</c>) และลิสต์ในข้อความสร้างจาก
    /// <see cref="All"/> ตอน runtime ห้ามพิมพ์ซ้ำในสตริง
    /// </summary>
    /// <returns>true = ว่าง (ไม่ระบุ ซึ่งถูกต้อง) หรือรู้จัก · false = ไม่อยู่ในตาราง</returns>
    public static bool TryCanonical(string? input, out string canonical, out string? reason)
    {
        canonical = "";
        reason = null;
        var t = (input ?? "").Trim();
        if (t.Length == 0) return true;            // ไม่ระบุ = ถูกต้อง (นิติบุคคล/ไม่ทราบ)

        foreach (var p in All)
        {
            if (string.Equals(p.Thai, t, StringComparison.OrdinalIgnoreCase))
            { canonical = p.Thai; return true; }
            foreach (var a in p.Aliases)
                if (string.Equals(a, t, StringComparison.OrdinalIgnoreCase))
                { canonical = p.Thai; return true; }
        }

        // รูปย่อที่ Normalize รู้จักแต่ไม่อยู่ในตาราง (Mister/Madam/…)
        var viaNormalize = Normalize(t);
        if (!string.Equals(viaNormalize, t, StringComparison.Ordinal))
            return TryCanonical(viaNormalize, out canonical, out reason);

        var person = string.Join(" · ", All.Where(p => !p.IsJuristic).Select(p => p.Thai));
        var juristic = string.Join(" · ", All.Where(p => p.IsJuristic).Select(p => p.Thai));
        reason = $"คำนำหน้า \"{t}\" ใช้กับไฟล์ยื่นของกรมสรรพากร/ประกันสังคมไม่ได้ — "
               + $"บุคคลธรรมดาใช้: {person} · นิติบุคคลใช้: {juristic}";
        return false;
    }
}
