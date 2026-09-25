namespace Accounting.Helpers;

/// <summary>ผลการตัดสินว่า "ใบที่สแกน (สาขาที่อ่านได้) ควรผูกกับผู้ติดต่อแถวไหน"</summary>
public enum OcrVendorBranchOutcome
{
    /// <summary>ไม่มีผู้ติดต่อที่เลขผู้เสียภาษีตรงเลย — ไปเส้นสร้างใหม่ตามเดิม</summary>
    NoTaxIdMatch,
    /// <summary>อ่านสาขาจากกระดาษไม่ได้ — ใช้กติกาเดิม (สำนักงานใหญ่/แถวที่ไม่เคยระบุสาขา → แถวเดียวที่มี → รหัสต่ำสุด)</summary>
    BranchNotRead,
    /// <summary>มีแถวที่สาขาตรงกับกระดาษ (แถวที่ไม่เคยระบุสาขา ≡ สำนักงานใหญ่ — อ้างได้เฉพาะใบสำนักงานใหญ่)</summary>
    ExactBranch,
    /// <summary>
    /// <b>สาขาบนกระดาษมีหลักฐาน แต่ไม่ตรงสาขาใดของผู้ติดต่อที่มี ⇒ สร้างผู้ติดต่อแถวใหม่ของสาขานั้น</b> (รอบ 197 ทีม K ·
    /// คำตัดสินเจ้าของ: "ระบบควรสร้างผู้ติดต่อคนละอันกับที่มีเพราะสาขาคนละสาขา ที่อยู่คนละที่กัน") —
    /// <see cref="OcrVendorBranchPick.ContactId"/> = null · <see cref="OcrVendorBranchPick.TemplateContactId"/> = แถวของ
    /// นิติบุคคลเดียวกัน (สำนักงานใหญ่ก่อน) ที่ใช้เป็นแม่แบบชื่อนิติบุคคล/ชนิดผู้ติดต่อ · <b>ห้ามถอยไปจับด้วยชื่อ/AI</b>
    /// (จะได้แถวสำนักงานใหญ่กลับมา — ช่องเดียวกับ <see cref="ContactTaxBranchKey.SoftMatchScope"/>)
    /// </summary>
    NewBranchRow,
    /// <summary>ไม่มีแถวสาขาตรง แต่สาขาที่อ่านได้<b>หลักฐานอ่อน</b> (คะแนน &lt; เกณฑ์ เช่น ขัดกับประโยคบนกระดาษ) —
    /// ไม่สร้างแถวจากค่าที่ไม่แน่ใจ ("ไม่เดาสาขาใหม่") · ผูกแถวของนิติบุคคลเดียวกันตามกติกา "ไม่รู้สาขา" (พฤติกรรมเดิม)
    /// แต่ <b>ห้ามเอาข้อมูลจากกระดาษไปเติม/ทับแถวนั้น</b> และบอกผู้ใช้ตรง ๆ ใน trace</summary>
    OtherBranchRow,
}

/// <summary>คำตัดสิน — <see cref="ContactId"/> คือแถวที่จะผูก (null เมื่อต้องสร้างใหม่/ไม่มีคู่)</summary>
public sealed record OcrVendorBranchPick(
    OcrVendorBranchOutcome Outcome,
    Guid? ContactId,
    string? ScannedBranch,
    string Trace,
    Guid? TemplateContactId = null)
{
    /// <summary>ข้อมูลจากกระดาษ (ที่อยู่ · สาขา · เบอร์) เติมลงแถวที่ผูกได้ไหม — ได้เฉพาะเมื่อแถวนั้น
    /// เป็นสาขาเดียวกับกระดาษ (หรือไม่รู้สาขาทั้งสองฝั่ง). แถว "คนละสาขา" ห้ามแตะ มิฉะนั้นที่อยู่สาขาที่ 8
    /// ไปทับที่อยู่สำนักงานใหญ่ถาวร · <see cref="OcrVendorBranchOutcome.NoTaxIdMatch"/> = ตัวนี้ไม่ได้ผูก
    /// (แถวถูกจับด้วยชื่อทีหลัง) ⇒ คงพฤติกรรมเดิม</summary>
    public bool MayEnrichMatchedRow => Outcome is OcrVendorBranchOutcome.ExactBranch
        or OcrVendorBranchOutcome.BranchNotRead
        or OcrVendorBranchOutcome.NoTaxIdMatch;

    /// <summary>ต้องสร้างผู้ติดต่อแถวใหม่ของสาขาบนกระดาษ (และห้ามจับคู่ด้วยชื่อ/อีเมล/AI แทน)</summary>
    public bool MustCreateBranchRow => Outcome == OcrVendorBranchOutcome.NewBranchRow;
}

/// <summary>
/// "เลขผู้เสียภาษีเดียว หลายสาขา" — เลือกแถวผู้ติดต่อให้ตรงสาขาที่อ่านได้จากใบกำกับ (รอบ 190 ทีม C ข้อ 5 · รอบ 197 ทีม K)
///
/// <para><b>โครงสร้างที่ระบบถืออยู่แล้ว</b>: ผู้ติดต่อ 1 แถว = 1 (เลขผู้เสียภาษี, สาขา) —
/// <c>DocumentService.FindDuplicateContactAsync</c> และ <see cref="ContactTaxBranchKey"/> ถือว่าเลขเดียวกันคนละสาขา =
/// คนละแถว (ประกาศอธิบดีฯ ฉบับที่ 199: ใบกำกับต้องระบุสาขาที่ออกใบ · §86/4)</para>
///
/// <para><b>รอบ 197 (ใบ Makro สาขาชลบุรี 00005)</b>: ผู้ใช้ "ได้เลขผู้ขายและสาขาถูก แต่สร้างเอกสารใช้ผู้ติดต่อผิด
/// (สำนักงานใหญ่ 00000) · ต้องสร้างผู้ติดต่อใหม่เพราะคนละสาขา คนละที่อยู่". เดิมตัวนี้มีกติกาของตัวเอง (สำเนาที่สองของ
/// ตัวจับคู่คีย์): ไม่มีแถวสาขาตรง ⇒ <b>ผูกสำนักงานใหญ่</b> (รอเจ้าของตัดสินเรื่องสร้างแถว) · แถวที่ไม่เคยระบุสาขา ⇒ อ้างได้โดย
/// ใบของสาขาใดก็ได้ แล้วตัวเติม <c>[Enrich]</c> ประทับรหัสสาขาทับ. ตอนนี้ <b>ใช้ <see cref="ContactTaxBranchKey.PickBranch"/>
/// ตัวเดียวกับทุกทางเข้า</b> แล้วเพิ่มแค่ "จะทำอะไรเมื่อไม่พบ": สาขามีหลักฐาน ⇒ <see cref="OcrVendorBranchOutcome.NewBranchRow"/> ·
/// หลักฐานอ่อน ⇒ <see cref="OcrVendorBranchOutcome.OtherBranchRow"/> (ผูกแบบเดิมแต่ห้ามเติมข้ามสาขา)</para>
///
/// <para>ทิศตรงข้าม (ล็อกด้วยเทสต์): สาขาบนกระดาษตรงแถวเดิม ⇒ ผูกแถวเดิม · อ่านสาขาไม่ได้ ⇒ กติกาเดิม ไม่สร้างแถว ·
/// ใบสำนักงานใหญ่ + แถวที่ไม่เคยระบุสาขา ⇒ แถวนั้น</para>
/// </summary>
public static class OcrVendorBranchContact
{
    /// <summary>คะแนนขั้นต่ำของรหัสสาขาผู้ขายที่ถือว่า "มีหลักฐานบนกระดาษ" พอจะสร้างผู้ติดต่อแถวใหม่ —
    /// ตัวอ่านป้าย <c>BranchCodeExtractor</c> ให้ 0.85 · ประโยคประกาศสาขาผู้ออกใบ 0.90 · ขัดกันเอง 0.50 · อ่านไม่ได้ 0.30</summary>
    public const double ReliableBranchConfidence = 0.85;

    /// <summary>ผู้ติดต่อที่เลขผู้เสียภาษีตรงกับกระดาษ (กรองบริษัทตัวเองออกแล้ว)</summary>
    public sealed record Candidate(Guid Id, string? Name, string? BranchCode);

    /// <summary>รหัสสาขาผู้ขายมีหลักฐานพอไหม — <paramref name="confidence"/> null = ไม่มีคะแนนแยกช่อง (ค่ามาจาก engine/e-Tax
    /// ที่อ่านจากกระดาษตรง ๆ) ถือว่ามี · ผู้ใช้แก้รหัสสาขาเอง = มีเสมอ</summary>
    public static bool IsReliableBranch(double? confidence, bool userCorrected = false)
        => userCorrected || confidence is null || confidence.Value >= ReliableBranchConfidence;

    /// <param name="candidates">แถวที่เลขผู้เสียภาษีตรง (normalize แล้ว) — ลำดับไม่มีผล</param>
    /// <param name="scannedBranch">สาขาผู้ขายที่อ่านได้ (ว่าง/ผิดรูป = อ่านไม่ได้)</param>
    /// <param name="branchReliable">รหัสสาขามีหลักฐานพอจะสร้างแถวใหม่ (<see cref="IsReliableBranch"/>)</param>
    public static OcrVendorBranchPick Decide(IReadOnlyList<Candidate> candidates, string? scannedBranch,
        bool branchReliable = true)
    {
        if (candidates == null || candidates.Count == 0)
            return new(OcrVendorBranchOutcome.NoTaxIdMatch, null, null, "");

        var keys = candidates.Select(c => new ContactKeyCandidate(c.Id, null, c.BranchCode)).ToList();
        string NameOf(Guid? id) => candidates.FirstOrDefault(c => c.Id == id)?.Name ?? "";
        string BranchOf(Guid? id) => TaxBranchCode.Label(candidates.FirstOrDefault(c => c.Id == id)?.BranchCode);

        // กติกา "ไม่รู้สาขา" ของตัวจับคู่กลาง = แม่แบบ/แถวที่ผูกเมื่ออ่านสาขาไม่ได้
        var fallback = ContactTaxBranchKey.PickBranch(keys, null).ContactId;

        var scanned = ScannedCode(scannedBranch);
        if (scanned == null)
            return new(OcrVendorBranchOutcome.BranchNotRead, fallback, null,
                candidates.Count > 1
                    ? $"[Branch] TaxID ตรง {candidates.Count} รายชื่อ แต่อ่านสาขาบนกระดาษไม่ได้ — ใช้ '{NameOf(fallback)}' "
                      + $"({BranchOf(fallback)}) โปรดตรวจสอบ"
                    : "");

        var exact = ContactTaxBranchKey.PickBranch(keys, scanned);
        if (exact.ContactId is Guid hit)
            return new(OcrVendorBranchOutcome.ExactBranch, hit, scanned,
                candidates.Count > 1
                    ? $"[Branch] TaxID ตรง {candidates.Count} รายชื่อ — เลือกตามสาขา {TaxBranchCode.Label(scanned)} ({scanned}): '{NameOf(hit)}'"
                    : "");

        var existing = string.Join(" · ", candidates
            .Select(c => TaxBranchCode.Label(c.BranchCode)).Distinct(StringComparer.Ordinal));
        if (branchReliable)
            return new(OcrVendorBranchOutcome.NewBranchRow, null, scanned,
                $"[Branch] กระดาษออกโดย {TaxBranchCode.Label(scanned)} ({scanned}) แต่ในระบบมีผู้ติดต่อเลขนี้เฉพาะ {existing} "
                + $"— คนละสถานประกอบการ (ประกาศอธิบดีฯ 199 · §86/4) ⇒ สร้างผู้ติดต่อของสาขานี้แยก "
                + $"(ชื่อนิติบุคคลตาม '{NameOf(fallback)}') ไม่ผูกกับ{BranchOf(fallback)}",
                fallback);

        return new(OcrVendorBranchOutcome.OtherBranchRow, fallback, scanned,
            $"[Branch] ⚠ กระดาษน่าจะออกโดย {TaxBranchCode.Label(scanned)} ({scanned}) แต่รหัสสาขานี้ยังไม่แน่ใจ และในระบบมีผู้ติดต่อเลขนี้เฉพาะ "
            + existing
            + $" — ผูกกับ '{NameOf(fallback)}' ({BranchOf(fallback)}) ชั่วคราว · ไม่นำที่อยู่/สาขาจากกระดาษไปแก้ผู้ติดต่อรายนั้น · "
            + "ตรวจรหัสสาขาบนกระดาษ ถ้าถูกต้อง แก้รหัสสาขาในหน้านี้แล้วกดสร้างเอกสาร ระบบจะสร้างผู้ติดต่อของสาขานั้นให้",
            fallback);
    }

    /// <summary>
    /// ชื่อของผู้ติดต่อแถวใหม่ของสาขา — <b>ชื่อนิติบุคคล</b> (ทุกสาขาของเลขเดียวกันคือนิติบุคคลเดียว): ทะเบียนก่อน →
    /// ชื่อของแถวแม่แบบ (ตัดป้ายสาขาท้ายชื่อออก — "(สำนักงานใหญ่)" ไม่ใช่ชื่อของสาขาอื่น) → ชื่อที่อ่านจากกระดาษ ·
    /// ชื่อสาขาเก็บที่ <c>Contact.BranchName</c> ไม่ต่อท้ายชื่อ
    /// </summary>
    public static string? NewRowName(string? registryName, string? templateName, string? paperName)
    {
        if (!string.IsNullOrWhiteSpace(registryName)) return registryName.Trim();
        var (tpl, _) = OcrPartyName.StripBranchSuffix(templateName);
        if (!string.IsNullOrWhiteSpace(tpl)) return tpl.Trim();
        return string.IsNullOrWhiteSpace(paperName) ? null : paperName.Trim();
    }

    /// <summary>สาขาที่อ่านได้ → รหัส 5 หลัก · อ่านไม่ได้/ผิดรูป → null ("ไม่รู้" ห้ามกลายเป็น 00000)</summary>
    private static string? ScannedCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return TaxBranchCode.TryNormalize(raw, out var code, out _) ? code : null;
    }
}
