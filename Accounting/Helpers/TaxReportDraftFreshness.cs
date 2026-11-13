namespace Accounting.Helpers;

/// <summary>
/// **ร่างรายงานภาษีล้าสมัยไหม (pure · รอบ 269 ชุดสอง H1 · คำตัดสิน 269-3-1)** — แทนการ "ลบ-สร้างใหม่อัตโนมัติทุกครั้งที่เปิดหน้า"
///
/// <para>เทียบ "แถวรายการที่ระบบสร้าง" ของร่างกับการคำนวณสด: แถวที่มีเอกสาร · ไม่ใช่บรรทัดดึงด้วยมือ (<c>[ดึงเข้างวด-</c>) · ไม่ใช่แถวสรุป ·
/// <b>ไม่เทียบติ๊ก "ใช้"</b> (งานมือ — "สร้างใหม่" คืนให้) · ต่อเอกสาร: ยอดฐาน + ยอดภาษี (ปัด 2 ตำแหน่ง) · แถวที่ไม่มีเอกสาร (เลื่อนเคลม/JE ล้วน/เครดิตยกมา)
/// ไม่ถูกเทียบ (ข้อความบอกตรง ๆ — หลักการ #7 สาเหตุที่ระบุต้องเป็นสิ่งที่ตรวจ)</para>
/// </summary>
public static class TaxReportDraftFreshness
{
    public const string RuleCode = "TAX-DRAFT-STALE-269";

    public readonly record struct Entry(Guid DocumentId, string? IncomeTypeCode, decimal IncomeAmount, decimal TaxAmount);

    public sealed record Verdict(bool Stale, int Added, int Removed, int Changed);

    public static Dictionary<Guid, Entry> Signature(
        IEnumerable<(Guid? DocumentId, string? Description, string? IncomeTypeCode, decimal IncomeAmount, decimal TaxAmount)> lines,
        string pulledLinePrefix)
    {
        var result = new Dictionary<Guid, Entry>();
        foreach (var l in lines)
        {
            if (!l.DocumentId.HasValue) continue;
            if ((l.Description ?? "").Contains(pulledLinePrefix, StringComparison.Ordinal)) continue;
            if (VatReportLineKind.SideOf(l.IncomeTypeCode) == VatReportSide.Summary) continue;
            var id = l.DocumentId.Value;
            var add = new Entry(id, l.IncomeTypeCode, Round(l.IncomeAmount), Round(l.TaxAmount));
            result[id] = result.TryGetValue(id, out var e)
                ? e with { IncomeAmount = e.IncomeAmount + add.IncomeAmount, TaxAmount = e.TaxAmount + add.TaxAmount }
                : add;
        }
        return result;
    }

    public static Verdict Compare(IReadOnlyDictionary<Guid, Entry> stored, IReadOnlyDictionary<Guid, Entry> fresh)
    {
        var added = fresh.Keys.Count(k => !stored.ContainsKey(k));
        var removed = stored.Keys.Count(k => !fresh.ContainsKey(k));
        var changed = stored.Count(kv => fresh.TryGetValue(kv.Key, out var f)
            && (f.IncomeAmount != kv.Value.IncomeAmount || f.TaxAmount != kv.Value.TaxAmount));
        return new Verdict(added + removed + changed > 0, added, removed, changed);
    }

    public static string? Message(Verdict v, string periodLabel)
        => !v.Stale ? null
            : $"ร่างงวด {periodLabel} ล้าสมัย — เอกสารเปลี่ยนหลังสร้างร่าง (เพิ่ม {v.Added} · หายไป {v.Removed} · ยอดเปลี่ยน {v.Changed} ใบ) · "
              + "กด \"สร้างใหม่\" เพื่อคำนวณใหม่ (คืนติ๊กและบรรทัดที่ดึงเข้าให้ · บันทึก audit) · ไม่ได้เทียบบรรทัดที่ไม่มีเอกสาร (เลื่อนเคลม/JE ล้วน/เครดิตยกมา)";

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

/// <summary>สร้างร่างอัตโนมัติไม่สำเร็จบางงวด — ล้มดังพร้อมงวดที่ล้ม (รอบ 269 ชุดสอง H1 · เดิม Trace เงียบ)</summary>
public sealed class TaxReportAutoDraftException : BusinessRuleException
{
    public int Created { get; }
    public IReadOnlyList<string> Failures { get; }

    public TaxReportAutoDraftException(int created, IReadOnlyList<string> failures)
        : base($"สร้างร่างรายงานภาษีอัตโนมัติไม่สำเร็จ {failures.Count} งวด (สร้างได้ {created}): " + string.Join(" · ", failures),
            TaxReportDraftFreshness.RuleCode + "-CREATE", 422)
    {
        Created = created;
        Failures = failures;
    }
}
