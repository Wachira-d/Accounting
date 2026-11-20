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
        string pulledLinePrefix, string? carryForwardPrefix = null)
    {
        var result = new Dictionary<Guid, Entry>();
        foreach (var l in lines)
        {
            if (!l.DocumentId.HasValue) continue;
            if ((l.Description ?? "").Contains(pulledLinePrefix, StringComparison.Ordinal)) continue;
            // บรรทัด "ยกมา §82/3" = งานตัดสินของผู้ใช้ (ติ๊กแล้วใบถูกนับว่า "ใช้" ⇒ การคำนวณสดไม่ยกมาให้อีก) — ไม่ใช่ความต่างของเอกสาร
            if (carryForwardPrefix != null && (l.Description ?? "").StartsWith(carryForwardPrefix, StringComparison.Ordinal)) continue;
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

    // ═════ รอบ 275 แพ็ก B (T2 H2 · M3 · M4) — แบบ ภ.ง.ด. เทียบด้วย "ใบ 50 ทวิ" ไม่ใช่ "เอกสาร" ═════
    // เดิม (ลายเซ็นต่อเอกสาร) มองไม่เห็น: ใบมือที่ไม่มีเอกสาร · ร่าง→ออก / ออก→ยกเลิก (ยอดต่อเอกสารเท่าเดิม) · เปลี่ยนการผูก A→B ยอดเท่ากัน ·
    // แก้เลขผู้เสียภาษี/สาขา/ชื่อผู้ถูกหักหลังสร้างร่าง (ไฟล์ยื่นใช้สำเนาเก่า) ⇒ งวดเดียวมีตัวเลขสามชุด (ทะเบียน · ร่าง · ที่ยื่น)

    public const string FileRuleCode = "TAX-DRAFT-STALE-275";

    /// <summary>ชนิดรายงานที่แถบร่างล้าสมัยตรวจ — แยกจากชุด "สร้างร่างอัตโนมัติ" (M3: ภ.ง.ด.2/54/ภ.พ.36 ไม่สร้างเองโดยตั้งใจ แต่ร่างที่มีอยู่ต้องถูกตรวจ ·
    /// ภ.ง.ด.1 = รายงานรุ่นเก่าที่ระบบไม่คำนวณแล้ว ⇒ ขึ้นแถบเสมอพร้อมทางไปต่อ)</summary>
    public static readonly Models.Enums.TaxType[] CheckedTaxTypes =
    {
        Models.Enums.TaxType.VAT, Models.Enums.TaxType.WithholdingTax1, Models.Enums.TaxType.WithholdingTax2,
        Models.Enums.TaxType.WithholdingTax3, Models.Enums.TaxType.WithholdingTax53, Models.Enums.TaxType.WithholdingTax54,
        Models.Enums.TaxType.VatPp36,
    };

    /// <summary>แบบที่ลายเซ็นคีย์ด้วยใบ 50 ทวิ (<see cref="WhtSignature"/>) — ภ.พ.30/ภ.พ.36 ใช้ <see cref="Signature"/> (ต่อเอกสาร)</summary>
    public static bool UsesWhtSignature(Models.Enums.TaxType t) => WhtReportLineRule.Applies(t);

    public readonly record struct WhtLine(Guid? WhtCertId, Guid? DocumentId, string? Description, string? IncomeTypeCode,
        decimal IncomeAmount, decimal TaxAmount, string? TaxPayerId, string? BranchCode, string? TaxPayerName);

    public readonly record struct WhtEntry(WhtReportLineKind Kind, decimal IncomeAmount, decimal TaxAmount,
        string TaxPayerId, string BranchCode, string TaxPayerName);

    public sealed record WhtVerdict(bool Stale, int Added, int Removed, int Changed, int LegacyLines);

    /// <summary>ลายเซ็นของแบบ ภ.ง.ด.: บรรทัดจากใบ ⇒ คีย์ <c>ใบ|ประเภทเงินได้</c> (ชนิดบรรทัด ร่าง/ออกแล้ว/e-Withholding อยู่ในค่า) ·
    /// แถวเตือนของเอกสาร ⇒ คีย์ <c>เอกสาร|ชนิด|ประเภทเงินได้</c> · ค่า = ยอดเงินได้ + ภาษี + เลขผู้เสียภาษี + สาขา + ชื่อผู้ถูกหัก ·
    /// <b>ไม่เทียบติ๊ก "ใช้"</b> (งานมือ — สร้างใหม่คืนให้) · ไม่เทียบแถว [สรุป] (คำนวณจากบรรทัด) และบรรทัดที่ไม่มีทั้งใบและเอกสาร
    /// (JE ล้วน — ยอดฐานเป็นค่าประมาณที่ผู้ทำบัญชีแก้บนรายงานได้)</summary>
    public static Dictionary<string, WhtEntry> WhtSignature(IEnumerable<WhtLine> lines)
    {
        var result = new Dictionary<string, WhtEntry>(StringComparer.Ordinal);
        foreach (var l in lines)
        {
            if (VatReportLineKind.SideOf(l.IncomeTypeCode) == VatReportSide.Summary) continue;
            if (!l.WhtCertId.HasValue && !l.DocumentId.HasValue) continue;
            var kind = WhtReportLineRule.KindOf(l.Description);
            var type = (l.IncomeTypeCode ?? "").Trim();
            var key = l.WhtCertId is Guid c ? $"C:{c}|{type}" : $"D:{l.DocumentId}|{kind}|{type}";
            var add = new WhtEntry(kind, Round(l.IncomeAmount), Round(l.TaxAmount),
                (l.TaxPayerId ?? "").Trim(), NormalizeBranch(l.BranchCode), (l.TaxPayerName ?? "").Trim());
            result[key] = result.TryGetValue(key, out var e)
                ? e with { IncomeAmount = e.IncomeAmount + add.IncomeAmount, TaxAmount = e.TaxAmount + add.TaxAmount }
                : add;
        }
        return result;
    }

    /// <summary>เทียบลายเซ็น ภ.ง.ด. · <c>LegacyLines</c> = บรรทัดของร่างที่สร้างก่อนรอบ 275 (มีเอกสาร · นับเข้ายอด · ไม่มีตัวอ้างใบ) — ขึ้นแถบหนึ่งครั้ง</summary>
    public static WhtVerdict CompareWht(IReadOnlyDictionary<string, WhtEntry> stored, IReadOnlyDictionary<string, WhtEntry> fresh)
    {
        var added = fresh.Keys.Count(k => !stored.ContainsKey(k));
        var removed = stored.Keys.Count(k => !fresh.ContainsKey(k));
        var changed = stored.Count(kv => fresh.TryGetValue(kv.Key, out var f) && f != kv.Value);
        var legacy = stored.Count(kv => kv.Key.StartsWith("D:", StringComparison.Ordinal) && kv.Value.Kind == WhtReportLineKind.Counted);
        return new WhtVerdict(added + removed + changed > 0, added, removed, changed, legacy);
    }

    public static string? WhtMessage(WhtVerdict v, string formLabel, string periodLabel)
        => !v.Stale ? null
            : $"ร่าง {formLabel} งวด {periodLabel} ไม่ตรงทะเบียน 50 ทวิ/เอกสารปัจจุบัน (เพิ่ม {v.Added} · หายไป {v.Removed} · เปลี่ยน {v.Changed} รายการ — "
              + "ออก/ยกเลิก/ผูกใบ · ยอด · ประเภทเงินได้ · เลขผู้เสียภาษี/สาขา/ชื่อผู้ถูกหัก)"
              + (v.LegacyLines > 0 ? $" · ร่างนี้สร้างก่อนรอบ 275 ({v.LegacyLines} บรรทัดยังไม่อ้างเลข 50 ทวิ) — สร้างใหม่หนึ่งครั้ง" : "")
              + " · กด \"สร้างใหม่\" (คืนติ๊กที่ผู้ใช้ติ๊กเข้าเองให้ · บันทึก audit) · ไม่ได้เทียบบรรทัด JE ล้วน";

    /// <summary>ข้อความบล็อกตอนยื่น/สร้างไฟล์ยื่น — ด่านทะเบียน (H2 (3)) ใช้ผลเทียบเดียวกับแถบ</summary>
    public static string FileBlockMessage(string staleMessage)
        => "ยื่น/สร้างไฟล์ยื่นไม่ได้ — " + staleMessage;

    /// <summary>ภ.ง.ด.1 ที่ค้างเป็นรายงานรุ่นเก่า (สร้างใหม่ไม่ได้แล้ว · ไม่ถูกเทียบกับรอบเงินเดือน/50 ทวิ เงินเดือน)</summary>
    public static string LegacyPnd1Message(string periodLabel)
        => $"ร่าง ภ.ง.ด.1 งวด {periodLabel} เป็นรายงานรุ่นเก่า — ระบบเลิกคำนวณ ภ.ง.ด.1 จากหน้านี้แล้ว (ยอดไม่ถูกเทียบกับรอบเงินเดือน/50 ทวิ เงินเดือน) · "
           + "ยื่นจากเมนู \"Export ยื่นภาษี / SSO\" การ์ด ภ.ง.ด.1 แล้วลบร่างนี้";

    public const string LegacyPnd1RuleCode = "TAX-PND1-LEGACY-275";

    /// <summary>สาขาว่าง/0/00000 = สำนักงานใหญ่ ("00000") — ตัวเดียวกับคีย์แถว [สรุป] (<see cref="WhtReportTotals"/>)</summary>
    public static string NormalizeBranch(string? branch)
    {
        var digits = new string((branch ?? "").Where(char.IsDigit).ToArray());
        return digits.Length == 0 || digits.All(ch => ch == '0') ? "00000" : digits.PadLeft(5, '0');
    }
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
