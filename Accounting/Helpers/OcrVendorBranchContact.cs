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
    /// ประโยคประกาศสาขาผู้ออกใบ 0.90 · บล็อกผู้ขาย (ก่อนป้ายผู้ซื้อ) 0.85 · อ่านทั้งหน้าเพราะไม่มีป้ายผู้ซื้อ 0.70 ·
    /// ถอยอ่านทั้งหน้าทั้งที่มีป้ายผู้ซื้อ 0.60 (ได้รหัสเดียวกับผู้ซื้อ 0.40) · ขัดกับประโยคบนกระดาษ 0.50 · อ่านไม่ได้ 0.30 ·
    /// e-Tax XML 1.0 (ตัวตั้งของคะแนนจากตัวอ่านป้าย = <c>BranchCodeExtractor.Result.SellerConfidence</c> · รอบ 197 ฝ่ายค้าน K-2)</summary>
    public const double ReliableBranchConfidence = 0.85;

    /// <summary>ผู้ติดต่อที่เลขผู้เสียภาษีตรงกับกระดาษ (กรองบริษัทตัวเองออกแล้ว)</summary>
    public sealed record Candidate(Guid Id, string? Name, string? BranchCode);

    /// <summary>รหัสสาขาผู้ขายมีหลักฐานพอจะ<b>สร้างผู้ติดต่อแถวถาวร</b>ไหม — ผู้ใช้แก้/ยืนยันรหัสสาขาเอง = มีเสมอ ·
    /// <paramref name="confidence"/> null = <b>ไม่รู้ว่ามาจากไหน</b> (สแกนรุ่นเก่าที่ไม่มีคะแนนแยกช่อง · ทางที่ลืมใส่คะแนน) ⇒
    /// <b>ไม่พอ</b> (ฝ่ายค้าน K-6 รอบ 197 · DECISION_DOCTRINE §1 "เงื่อนไขที่เท็จเพราะไม่มีข้อมูล ห้ามตกเป็นผ่าน") —
    /// ผลคือ <see cref="OcrVendorBranchOutcome.OtherBranchRow"/>: ผูกแถวเดิมของนิติบุคคลเดียวกัน + ข้อความเตือนให้ผู้ใช้ยืนยัน
    /// (ทิศที่ความเสียหายมองเห็นและแก้ทัน) แทนการสร้างผู้ติดต่อถาวรจากค่าที่ไม่รู้ที่มา · e-Tax XML ใส่ 1.0 ไว้แล้วจึงไม่กระทบ</summary>
    public static bool IsReliableBranch(double? confidence, bool userCorrected = false)
        => userCorrected || (confidence is double c && c >= ReliableBranchConfidence);

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
            + "ตรวจรหัสสาขาบนกระดาษ ถ้าถูกต้อง พิมพ์รหัสสาขาในช่อง “รหัสสาขาผู้ขาย” ของหน้านี้ใหม่ (หรือแก้ให้ถูก) แล้วกดสร้างเอกสาร "
            + "ระบบจะสร้างผู้ติดต่อของสาขานั้นให้",
            fallback);
    }

    /// <summary>
    /// **ผู้ใช้แก้ผลสแกน (<c>SubmitCorrectionAsync</c>) แล้วต้องตัดสินผู้ติดต่อผู้ขายใหม่ไหม** (รอบ 200 · ฝ่ายค้าน K-4 รอบ 197) —
    /// เส้น "แก้ในฟอร์มก่อน" ส่งคำแก้แล้วอ่าน <c>MatchedContactId</c> กลับไปเติมฟอร์ม ⇒ เดิมผู้ใช้แก้รหัสสาขา 00000 → 00005 ในหน้ารีวิว
    /// ฟอร์มยังได้ผู้ติดต่อสำนักงานใหญ่ (เส้นกดสร้างเอกสารตัดสินซ้ำแล้ว แต่เส้นฟอร์มไม่). ตัดสินใหม่ด้วย<b>ตัวเดียวกับเส้นสแกน/เส้นสร้างเอกสาร</b>
    /// (<see cref="Decide"/> → <see cref="ContactTaxBranchKey.PickBranch"/>) เมื่อ:
    /// <list type="bullet">
    /// <item>กุญแจผู้ขายเปลี่ยนจริง (รหัสสาขาเปลี่ยนจากที่เก็บไว้ · เลขผู้เสียภาษีเปลี่ยน) — ส่งค่าเดิมกลับมา = ไม่ใช่การเปลี่ยน (K-1)</item>
    /// <item>ผู้ใช้<b>ไม่ได้</b>เลือกผู้ติดต่อเอง (<c>MatchContactAsync</c>) — คำตอบของคนชนะเสมอ</item>
    /// <item>ยังไม่ได้สร้างเอกสาร — เอกสารที่สร้างแล้วถือผู้ติดต่อของตัวเอง (แก้ที่เอกสาร ไม่ใช่ที่ผลสแกน)</item>
    /// <item>เป็นฝั่งซื้อ — ฝั่งขายตัดสินคู่ค้าจากบล็อกผู้ซื้อทุกครั้งที่สร้างอยู่แล้ว (<c>MatchedContactId</c> ของฝั่งขายคือเรา)</item>
    /// </list>
    /// </summary>
    public static bool ShouldRedecideOnCorrection(bool vendorKeyChanged, bool userPickedContact,
        bool documentCreated, bool salesSide)
        => vendorKeyChanged && !userPickedContact && !documentCreated && !salesSide;

    /// <summary>
    /// **ผู้ใช้ลงมือกับกุญแจผู้ขายในคำแก้นี้ไหม** — ตัวป้อน <c>vendorKeyChanged</c> ของ <see cref="ShouldRedecideOnCorrection"/>
    /// (รอบ 200 ทีม K2 · ฝ่ายค้าน K R1): นับเมื่อ (ก) รหัสสาขาเปลี่ยนจากที่เก็บไว้ (<see cref="OcrCorrectedFieldList.BranchChanged"/>) ·
    /// (ข) ผู้ใช้<b>พิมพ์ยืนยัน</b>รหัสสาขา (<c>VendorBranchConfirmed == true</c>) แม้ค่าเท่าเดิม — ข้อความ <see cref="OcrVendorBranchOutcome.OtherBranchRow"/>
    /// สั่งให้ผู้ใช้ "พิมพ์รหัสสาขาใหม่" เองเพื่อยืนยัน · เดิมข้อ (ข) ไม่นับ ⇒ ปุ่ม "แก้ในฟอร์มก่อน" ได้แถวสำนักงานใหญ่ ขณะที่ปุ่ม "สร้างเอกสาร"
    /// (ซึ่งอ่าน "VendorBranchCode" ใน <c>UserCorrectedFields</c> — นับการยืนยันอยู่แล้ว) ได้แถวสาขา = สองเส้นให้ผู้ติดต่อคนละแถวบนใบเดียว ·
    /// (ค) เลขผู้เสียภาษีเปลี่ยน (เทียบตัวเลขล้วน) · ส่งค่าเดิมกลับมาโดยไม่ยืนยัน = ไม่นับ (K-1)
    /// </summary>
    public static bool VendorKeyTouched(string? submittedBranch, string? storedBranch, bool? branchConfirmed,
        string? submittedTaxId, string? storedTaxId)
        => branchConfirmed == true
           || (submittedBranch != null && OcrCorrectedFieldList.BranchChanged(submittedBranch, storedBranch))
           || (submittedTaxId != null && ThaiTaxId.Normalize(submittedTaxId) != ThaiTaxId.Normalize(storedTaxId));

    /// <summary>
    /// **สแกนนี้ลงบัญชีไปแล้วหรือยัง** (ตัวป้อน <c>documentCreated</c> ของ <see cref="ShouldRedecideOnCorrection"/>) — รอบ 200 ทีม K2 · ฝ่ายค้าน K R5:
    /// เดิมดูแค่ <c>CreatedDocumentId</c> ⇒ สแกนที่ลงเป็น JE อย่างเดียว (<c>CreatedJournalEntryId</c>) ยังถูกเปลี่ยนผู้ติดต่อได้เมื่อแก้สาขา ⇒ สแกนกับรายการ
    /// ที่ลงแล้วชี้ผู้ติดต่อคนละแถว · ลงแล้วทางใดทางหนึ่ง = แก้ที่ตัวรายการ ไม่ใช่ที่ผลสแกน
    /// </summary>
    public static bool ScanAlreadyPosted(Guid? createdDocumentId, Guid? createdJournalEntryId)
        => createdDocumentId.HasValue || createdJournalEntryId.HasValue;

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
