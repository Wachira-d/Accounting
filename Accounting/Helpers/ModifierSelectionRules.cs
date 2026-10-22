namespace Accounting.Helpers;

/// <summary>ชนิดช่องของกลุ่มตัวเลือก (dynamic option builder) — ค่าที่เก็บใน <c>ProductModifierGroup.InputType</c></summary>
public static class ModifierInputType
{
    public const string Radio = "radio";
    public const string Dropdown = "dropdown";
    public const string Checkbox = "checkbox";
    public const string Text = "text";
    public const string Image = "image";

    public static readonly IReadOnlyList<(string Value, string Label)> All = new[]
    {
        (Radio, "ปุ่มเลือก 1 อย่าง (radio)"),
        (Dropdown, "รายการเลื่อนเลือก 1 อย่าง (dropdown)"),
        (Checkbox, "ติ๊กได้หลายอย่าง (checkbox)"),
        (Text, "ข้อความอิสระ (เช่น ข้อความบนเค้ก)"),
        (Image, "อัปโหลดรูปตัวอย่าง"),
    };

    /// <summary>ชนิดที่ต้องเลือกจากตัวเลือกของกลุ่ม (ไม่ใช่ช่องพิมพ์/อัปโหลด)</summary>
    internal static bool IsChoice(string t) => t == Radio || t == Dropdown || t == Checkbox;

    /// <summary>ค่าที่เก็บไว้ → ชนิดจริง · ว่าง/ไม่รู้จัก (ข้อมูลก่อนมีช่องนี้) ⇒ อนุมานจากธงเดิม
    /// <c>AllowMultiple</c> (true = checkbox · false = radio) — กลุ่มเดิมทำงานเหมือนเดิมทุกตัว</summary>
    public static string Resolve(string? stored, bool allowMultiple)
    {
        var s = (stored ?? "").Trim().ToLowerInvariant();
        foreach (var (v, _) in All) if (v == s) return v;
        return allowMultiple ? Checkbox : Radio;
    }

    /// <summary>ตรวจค่าที่ผู้ตั้งค่าส่งมา — ว่าง = null (ให้ <see cref="Resolve"/> อนุมาน) · ไม่รู้จัก = โยน</summary>
    public static string? Validate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToLowerInvariant();
        foreach (var (v, _) in All) if (v == s) return v;
        throw new BusinessRuleException(
            $"ชนิดช่อง \"{raw}\" ไม่รู้จัก — เลือกได้: {string.Join(" · ", All.Select(a => a.Value))}", "MOD-INPUT-TYPE");
    }
}

/// <summary>ตัวเลือกหนึ่งตัวของกลุ่ม (ข้อมูลจากฐานข้อมูล ไม่ใช่จาก client)</summary>
public sealed record ModifierOptionSpec(Guid Id, string Name, decimal PriceAdjustment, int ExtraLeadDays, bool IsActive);

/// <summary>กลุ่มตัวเลือกของสินค้า (ข้อมูลจากฐานข้อมูล)</summary>
/// <param name="InputPriceAdjustment">ราคาบวกเพิ่มของช่องพิมพ์/อัปโหลด เมื่อลูกค้ากรอก (เช่น เขียนข้อความบนเค้ก +50)</param>
/// <param name="InputExtraLeadDays">วันผลิตเพิ่มเมื่อลูกค้ากรอกช่องพิมพ์/อัปโหลด</param>
public sealed record ModifierGroupSpec(
    Guid Id, string Name, string InputType, bool IsRequired,
    int? MinSelect, int? MaxSelect, int? MaxTextLength,
    decimal InputPriceAdjustment, int InputExtraLeadDays,
    IReadOnlyList<ModifierOptionSpec> Options);

/// <summary>สิ่งที่ลูกค้าเลือก/กรอกต่อกลุ่ม (มาจาก client — <b>ไม่มีราคา</b> โดยตั้งใจ)</summary>
public sealed record ModifierChoice(Guid GroupId, IReadOnlyList<Guid>? OptionIds, string? Text, Guid? AttachmentId);

/// <summary>บรรทัดตัวเลือกที่ผ่านการตรวจ — ราคา/วันมาจากฐานข้อมูล</summary>
public sealed record ResolvedModifier(
    Guid GroupId, string GroupName, Guid? OptionId, string Label,
    decimal PriceAdjustment, int ExtraLeadDays, string? Text, Guid? AttachmentId);

/// <summary>ผลการประเมิน — <see cref="IsValid"/> false ⇒ <see cref="Errors"/> บอกช่องที่ผิดเป็นภาษาไทย</summary>
public sealed record ModifierEvaluation(
    IReadOnlyList<ResolvedModifier> Lines, decimal PriceAdjustmentTotal, int ExtraLeadDays, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// **ตัวตรวจ + คิดราคาตัวเลือกสินค้าตามสั่ง (made-to-order)** — ตัวเดียวที่ทุกทางเข้าใช้ (ตะกร้าร้านออนไลน์ · สร้างคำสั่งซื้อ)
///
/// <para>═══ ที่มา (ทีม K · 2026-10-22) ═══ ต่อยอดจาก modifier ของ POS (<c>ProductModifierGroup/Option</c>) ไม่สร้างระบบที่สอง ·
/// เพิ่มชนิดช่อง (radio/dropdown/checkbox/ข้อความ/รูป) · จำนวนเลือก min–max · ราคาบวกเพิ่มของช่องพิมพ์ · วันผลิตเพิ่ม</para>
///
/// <para><b>Server computes</b> (CLAUDE.md F2 ข้อ 5): client ส่งแค่ "เลือกอะไร" — ราคาบวกเพิ่ม/วันผลิตเพิ่มมาจากฐานข้อมูลเสมอ ·
/// ตัวเลือกที่ไม่อยู่ในกลุ่มของสินค้านั้น / ปิดใช้แล้ว ⇒ ผิด (ไม่ข้ามเงียบ ๆ — ลูกค้าจะได้ของที่ไม่ได้สั่ง)</para>
///
/// <para><b>จำนวนเลือก</b>: ขั้นต่ำ = <c>MinSelect</c> ?? (บังคับ ? 1 : 0) · สูงสุด = radio/dropdown = 1 เสมอ ·
/// checkbox = <c>MaxSelect</c> ?? จำนวนตัวเลือกที่เปิดใช้ · <b>วันผลิตเพิ่ม = ผลรวม</b>ของทุกตัวเลือกที่เลือก
/// (สัญญาวันรับช้ากว่าดีกว่าสัญญาเร็วแล้วทำไม่ทัน — ถ้าร้านทำขนานกันได้ ให้ตั้งวันเพิ่มเฉพาะตัวที่นานสุด)</para>
/// </summary>
public static class ModifierSelectionRules
{
    public const int DefaultMaxTextLength = 200;

    public static ModifierEvaluation Evaluate(IReadOnlyList<ModifierGroupSpec> groups, IReadOnlyList<ModifierChoice>? choices)
    {
        var errors = new List<string>();
        var lines = new List<ResolvedModifier>();
        var byGroup = new Dictionary<Guid, ModifierChoice>();
        foreach (var c in choices ?? Array.Empty<ModifierChoice>())
        {
            if (groups.All(g => g.Id != c.GroupId)) { errors.Add("มีตัวเลือกที่ไม่อยู่ในสินค้านี้ — กรุณาเลือกใหม่"); continue; }
            if (byGroup.ContainsKey(c.GroupId)) { errors.Add("ส่งตัวเลือกของกลุ่มเดียวกันซ้ำ — กรุณาเลือกใหม่"); continue; }
            byGroup[c.GroupId] = c;
        }

        foreach (var g in groups)
        {
            byGroup.TryGetValue(g.Id, out var choice);
            if (ModifierInputType.IsChoice(g.InputType))
                EvaluateChoiceGroup(g, choice, lines, errors);
            else
                EvaluateInputGroup(g, choice, lines, errors);
        }

        return new ModifierEvaluation(lines,
            lines.Sum(l => l.PriceAdjustment),
            lines.Sum(l => Math.Max(0, l.ExtraLeadDays)),
            errors);
    }

    /// <summary>ขั้นต่ำ/สูงสุดที่ใช้จริงของกลุ่ม (ให้หน้าเว็บแสดง "เลือก 1–3 อย่าง" จากค่าเดียวกับด่าน)</summary>
    public static (int Min, int Max) SelectBounds(ModifierGroupSpec g)
    {
        var active = g.Options.Count(o => o.IsActive);
        var min = Math.Max(0, g.MinSelect ?? (g.IsRequired ? 1 : 0));
        int max;
        if (g.InputType == ModifierInputType.Checkbox)
            max = g.MaxSelect is int m && m > 0 ? Math.Min(m, active) : active;
        else
            max = 1;
        if (g.IsRequired && min == 0) min = 1;
        return (min, Math.Max(max, 0));
    }

    private static void EvaluateChoiceGroup(ModifierGroupSpec g, ModifierChoice? choice,
        List<ResolvedModifier> lines, List<string> errors)
    {
        var (min, max) = SelectBounds(g);
        var picked = (choice?.OptionIds ?? Array.Empty<Guid>()).Distinct().ToList();
        var resolved = new List<ModifierOptionSpec>();
        foreach (var id in picked)
        {
            var opt = g.Options.FirstOrDefault(o => o.Id == id);
            if (opt == null) { errors.Add($"{g.Name}: ตัวเลือกที่ส่งมาไม่อยู่ในกลุ่มนี้"); return; }
            if (!opt.IsActive) { errors.Add($"{g.Name}: ตัวเลือก \"{opt.Name}\" ปิดรับแล้ว — กรุณาเลือกใหม่"); return; }
            resolved.Add(opt);
        }
        if (resolved.Count < min)
        {
            errors.Add(min == 1 ? $"กรุณาเลือก{g.Name}" : $"{g.Name}: กรุณาเลือกอย่างน้อย {min} อย่าง");
            return;
        }
        if (resolved.Count > max)
        {
            errors.Add(max == 1 ? $"{g.Name}: เลือกได้ 1 อย่าง" : $"{g.Name}: เลือกได้ไม่เกิน {max} อย่าง");
            return;
        }
        // เรียงตามลำดับของกลุ่ม (ไม่ใช่ลำดับที่ client ส่ง) ⇒ สลิป/ใบสั่งผลิตอ่านเหมือนกันทุกครั้ง
        foreach (var opt in g.Options.Where(o => resolved.Any(r => r.Id == o.Id)))
            lines.Add(new ResolvedModifier(g.Id, g.Name, opt.Id, opt.Name, opt.PriceAdjustment, opt.ExtraLeadDays, null, null));
    }

    private static void EvaluateInputGroup(ModifierGroupSpec g, ModifierChoice? choice,
        List<ResolvedModifier> lines, List<string> errors)
    {
        if (g.InputType == ModifierInputType.Text)
        {
            var text = (choice?.Text ?? "").Trim();
            var maxLen = g.MaxTextLength is int ml && ml > 0 ? ml : DefaultMaxTextLength;
            if (text.Length == 0)
            {
                if (g.IsRequired) errors.Add($"กรุณากรอก{g.Name}");
                return;
            }
            if (text.Length > maxLen) { errors.Add($"{g.Name}: ยาวได้ไม่เกิน {maxLen} ตัวอักษร"); return; }
            lines.Add(new ResolvedModifier(g.Id, g.Name, null, text, g.InputPriceAdjustment, g.InputExtraLeadDays, text, null));
            return;
        }

        // Image
        if (choice?.AttachmentId is not Guid att || att == Guid.Empty)
        {
            if (g.IsRequired) errors.Add($"กรุณาอัปโหลด{g.Name}");
            return;
        }
        lines.Add(new ResolvedModifier(g.Id, g.Name, null, "แนบรูปตัวอย่าง", g.InputPriceAdjustment, g.InputExtraLeadDays, null, att));
    }

    /// <summary>ตรวจค่าตั้งของกลุ่มตอนบันทึก (หน้าตัวเลือกเสริม) — ขั้นต่ำ ≤ สูงสุด · ไม่ติดลบ · ช่องพิมพ์ยาวได้ 1–1000 ·
    /// วันผลิตเพิ่ม 0–365 · ผิด = <see cref="BusinessRuleException"/> (ค่าตั้งที่ทำให้ลูกค้าสั่งไม่ได้ทุกครั้ง ต้องถูกกันตั้งแต่ตอนตั้ง)</summary>
    public static void ValidateGroupConfig(string inputType, int? minSelect, int? maxSelect, int? maxTextLength, int inputExtraLeadDays)
    {
        if (minSelect is < 0) throw new BusinessRuleException("จำนวนเลือกขั้นต่ำต้องไม่ติดลบ", "MOD-CONFIG");
        if (maxSelect is < 0) throw new BusinessRuleException("จำนวนเลือกสูงสุดต้องไม่ติดลบ", "MOD-CONFIG");
        if (minSelect is int mn && maxSelect is int mx && mx > 0 && mn > mx)
            throw new BusinessRuleException($"จำนวนเลือกขั้นต่ำ ({mn}) มากกว่าสูงสุด ({mx}) — ลูกค้าจะสั่งไม่ได้เลย", "MOD-CONFIG");
        if (inputType != ModifierInputType.Checkbox && (minSelect is > 1 || maxSelect is > 1))
            throw new BusinessRuleException("ช่องแบบเลือก 1 อย่าง (radio/dropdown) ตั้งจำนวนเลือกเกิน 1 ไม่ได้ — เปลี่ยนเป็น checkbox", "MOD-CONFIG");
        if (maxTextLength is int t && (t < 1 || t > 1000))
            throw new BusinessRuleException("ความยาวข้อความสูงสุดต้องอยู่ระหว่าง 1–1000 ตัวอักษร", "MOD-CONFIG");
        if (inputExtraLeadDays < 0 || inputExtraLeadDays > 365)
            throw new BusinessRuleException("วันผลิตเพิ่มต้องอยู่ระหว่าง 0–365 วัน", "MOD-CONFIG");
    }

    /// <summary>คำอธิบายตัวเลือกบนบรรทัดเอกสาร/สลิป — "ขนาด: 2 ปอนด์ · ข้อความ: HBD แม่"</summary>
    public static string Describe(IEnumerable<ResolvedModifier> lines)
        => string.Join(" · ", lines.Select(l => $"{l.GroupName}: {l.Label}"));
}
