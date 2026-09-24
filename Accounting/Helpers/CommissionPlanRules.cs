namespace Accounting.Helpers;

/// <summary>ขั้นของแผนคอมมิชชันแบบขั้นบันได — ยอดตั้งแต่ (รวม) ถึง (ไม่เกิน · null = ไม่จำกัด) คิดอัตรา %</summary>
public sealed record CommissionTierSpec(decimal FromAmount, decimal? ToAmount, decimal Rate);

/// <summary>แผนคอมมิชชันที่ผ่านการตรวจแล้ว — ค่าที่ service เขียนลง entity ได้ทันที</summary>
public sealed record CommissionPlanSpec(
    string Name, string CalculationBasis, string CalculationMethod,
    decimal? FlatRate, IReadOnlyList<CommissionTierSpec> Tiers);

/// <summary>
/// ตัวตั้งตัวเดียวของ "แผนคอมมิชชันที่ถูกต้องหน้าตาเป็นอย่างไร" — ชุดฐานคำนวณ · ชุดวิธีคำนวณ ·
/// ป้ายไทย · และกติกาอัตรา/ขั้น ที่ทั้งสร้างและแก้ไขต้องเดินผ่าน (F2 ข้อ 4)
///
/// <para><b>ที่มา (รอบ 193 · A04):</b> หน้า <c>commission.html</c> ส่ง
/// <c>{name, commissionType, rate, minimumSales, cap}</c> ขณะที่สัญญาคือ
/// <c>{Name, CalculationBasis, CalculationMethod, FlatRate, Tiers}</c> — ตรงกันแค่ชื่อ ⇒ สร้างแผนไม่ได้เลย
/// และต่อให้ผ่าน อัตราที่ผู้ใช้กรอกก็ไม่มีที่ลง (แผนอัตรา null = คำนวณได้ 0 ตลอดกาล). ชุดค่านี้ตรงกับ
/// ที่ <c>CommissionService.CalculateAsync</c>/<c>CalculateCommissionAmount</c> รู้จักจริง —
/// "Quantity" ในคอมเมนต์ entity <b>ไม่อยู่ในชุด</b>เพราะไม่มีเส้นคำนวณ (เลือกได้ = ได้ 0 เงียบ ๆ)</para>
/// </summary>
public static class CommissionPlanRules
{
    public const string DefaultBasis = "Revenue";
    public const string DefaultMethod = "Percentage";

    public static readonly IReadOnlyList<(string Value, string Label)> Bases = new[]
    {
        ("Revenue", "ยอดขาย (เอกสารขายที่ชำระแล้วในเดือน)"),
        ("CollectedAmount", "ยอดเงินที่เก็บได้ในเดือน"),
        ("Profit", "กำไร (ขาย − ซื้อ ที่ชำระแล้วในเดือน)"),
    };

    public static readonly IReadOnlyList<(string Value, string Label)> Methods = new[]
    {
        ("Percentage", "เปอร์เซ็นต์ของฐาน"),
        ("Fixed", "จำนวนเงินคงที่ (เดือนที่มียอด)"),
        ("Tiered", "ขั้นบันได (Tier)"),
    };

    /// <summary>
    /// ตรวจและทำให้เป็นค่ามาตรฐาน — ผิดกติกา = <see cref="BusinessRuleException"/> ภาษาไทยที่บอกช่อง
    /// · basis/method ว่าง = ค่าเริ่มต้น (ตรงกับค่าเริ่มต้นของ entity) · FlatRate ถูกล้างเมื่อเป็นขั้นบันได
    /// และขั้นถูกล้างเมื่อไม่ใช่ขั้นบันได (ห้ามเก็บของที่ไม่ถูกใช้ไว้ให้เข้าใจผิด)
    /// </summary>
    public static CommissionPlanSpec Validate(
        string? name, string? basis, string? method,
        decimal? flatRate, IEnumerable<CommissionTierSpec>? tiers)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new BusinessRuleException("กรุณาระบุชื่อแผน");

        var b = Pick(basis, Bases, DefaultBasis, "ฐานคำนวณ");
        var m = Pick(method, Methods, DefaultMethod, "วิธีคำนวณ");

        switch (m)
        {
            case "Percentage":
                if (flatRate is not { } pct || pct <= 0 || pct > 100)
                    throw new BusinessRuleException("อัตรา (%) ต้องมากกว่า 0 และไม่เกิน 100");
                return new CommissionPlanSpec(n, b, m, pct, Array.Empty<CommissionTierSpec>());

            case "Fixed":
                if (flatRate is not { } amt || amt <= 0)
                    throw new BusinessRuleException("จำนวนเงินคงที่ต้องมากกว่า 0");
                return new CommissionPlanSpec(n, b, m, amt, Array.Empty<CommissionTierSpec>());

            default: // Tiered
                var list = (tiers ?? Array.Empty<CommissionTierSpec>()).OrderBy(t => t.FromAmount).ToList();
                if (list.Count == 0)
                    throw new BusinessRuleException("แผนแบบขั้นบันไดต้องมีอย่างน้อย 1 ขั้น");
                for (var i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    var no = i + 1;
                    if (t.FromAmount < 0)
                        throw new BusinessRuleException($"ขั้นที่ {no}: ยอดตั้งแต่ต้องไม่ติดลบ");
                    if (t.ToAmount is { } to && to <= t.FromAmount)
                        throw new BusinessRuleException($"ขั้นที่ {no}: ยอดถึงต้องมากกว่ายอดตั้งแต่");
                    if (t.Rate <= 0 || t.Rate > 100)
                        throw new BusinessRuleException($"ขั้นที่ {no}: อัตรา (%) ต้องมากกว่า 0 และไม่เกิน 100");
                    if (i < list.Count - 1)
                    {
                        // ขั้นที่ไม่ใช่ขั้นสุดท้ายต้องมีเพดาน และห้ามทับขั้นถัดไป — ทับ = ยอดช่วงเดียวถูกคิดคอมซ้ำสองขั้น
                        if (t.ToAmount is not { } upper)
                            throw new BusinessRuleException($"ขั้นที่ {no}: ต้องระบุยอดถึง (เว้นว่างได้เฉพาะขั้นสุดท้าย)");
                        if (upper > list[i + 1].FromAmount)
                            throw new BusinessRuleException($"ขั้นที่ {no} กับขั้นที่ {no + 1} ช่วงยอดทับกัน");
                    }
                }
                return new CommissionPlanSpec(n, b, m, null, list);
        }
    }

    private static string Pick(string? value, IReadOnlyList<(string Value, string Label)> set,
        string fallback, string fieldLabel)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var v = value.Trim();
        foreach (var (val, _) in set)
            if (string.Equals(val, v, StringComparison.OrdinalIgnoreCase)) return val;
        throw new BusinessRuleException(
            $"{fieldLabel} \"{v}\" ไม่อยู่ในรายการที่ระบบรองรับ — เลือกได้: " +
            string.Join(" · ", set.Select(s => s.Label)));
    }
}
