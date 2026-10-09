namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของ "สแกนก่อนหน้าที่ hash ไฟล์ตรง" ที่ตัวตัดสินต้องใช้ — ผู้เรียกฉายจากแถว <c>OcrScanResult</c></summary>
public sealed record OcrPriorScanFacts(
    Guid Id,
    bool IsDeleted,
    string? ScanStatus,
    bool IsDuplicate,
    string? OcrEngine,
    int? ExtractionVersion,
    /// <summary>ช่องที่ผู้ใช้เคยแก้บนสแกนเดิม (ฝ่ายค้าน D3): ตอนอ่านใหม่ไม่คัดลอกค่าที่แก้มาให้ — บอกผู้ใช้ว่าต้องตรวจช่องไหนซ้ำ</summary>
    string? UserCorrectedFields = null);

/// <summary>ไฟล์ซ้ำแล้วทำอะไรกับ "ผลอ่าน"</summary>
public enum OcrDuplicateReuseAction
{
    /// <summary>ไม่ถือเป็นไฟล์ซ้ำ (ไม่มีสแกนเดิม · สแกนเดิมถูกลบ · ผู้ใช้สั่งสแกนใหม่) ⇒ อ่านไฟล์เต็มเส้น ไม่ติดธงซ้ำ</summary>
    NotDuplicate = 0,
    /// <summary>ไฟล์ซ้ำ + ผลอ่านเดิมยังเป็นของรุ่นปัจจุบัน ⇒ คัดลอกผลเดิม (engine = Cached) · ติดธงซ้ำ</summary>
    ReuseExtraction = 1,
    /// <summary>ไฟล์ซ้ำ แต่ห้ามคัดลอกผลเดิม ⇒ อ่านไฟล์ใหม่เต็มเส้น · <b>ยังติดธงซ้ำ</b> (คำเตือน §86/4 ภาษีซื้อซ้ำต้องอยู่)</summary>
    ReExtract = 2,
}

/// <summary>เหตุที่ไม่ใช้ผลอ่านเดิม (ขึ้นข้อความบน ProcessingNotes)</summary>
public enum OcrDuplicateReExtractReason
{
    None = 0,
    /// <summary>ผู้ใช้กด "แกะใหม่/สแกนใหม่" — ข้ามด่านไฟล์ซ้ำทั้งด่าน (พฤติกรรมเดิม)</summary>
    ForceRescan = 1,
    /// <summary>สแกนเดิมถูกลบแล้ว ("ลบทั้งคู่"/ลบสแกน) — ผู้ใช้ทิ้งผลนั้นแล้ว ห้ามเอากลับมา</summary>
    PriorDeleted = 2,
    /// <summary>สแกนเดิมยังไม่เสร็จ/ล้ม — ไม่มีผลอ่านให้ใช้</summary>
    PriorNotCompleted = 3,
    /// <summary>ไฟล์มี e-Tax XML ฝัง — อ่านซ้ำถูกและแน่นอน (ไม่ใช้โควตา engine) ⇒ อ่านใหม่ดีกว่าคัดลอกผลที่อาจมาจากตัวแปลงรุ่นเก่า</summary>
    EtaxXmlDeterministic = 4,
    /// <summary>ผลอ่านเดิมผลิตโดยตัวแกะรุ่นอื่น (หรือก่อนมีการประทับรุ่น = NULL)</summary>
    OtherExtractionVersion = 5,
    /// <summary>แถวเดิมเองเป็นสำเนา (Cached) — กันสำเนาของสำเนา</summary>
    PriorIsCopy = 6,
}

/// <summary>ผลตัดสิน · <see cref="IsDuplicate"/> = ต้องติดธงไฟล์ซ้ำ (คำเตือนบนหน้าจอ + ไม่สร้างเอกสารอัตโนมัติ + คืนโควตา)</summary>
public sealed record OcrDuplicateReuseVerdict(
    OcrDuplicateReuseAction Action,
    OcrDuplicateReExtractReason Reason,
    OcrPriorScanFacts? Prior,
    int CurrentVersion)
{
    public bool IsDuplicate => Action != OcrDuplicateReuseAction.NotDuplicate;
    public bool ReuseExtraction => Action == OcrDuplicateReuseAction.ReuseExtraction;
    /// <summary>id ของสแกนต้นฉบับที่ต้องผูกเป็น <c>DuplicateOfScanId</c> (null เมื่อไม่ซ้ำ)</summary>
    public Guid? DuplicateOfScanId => IsDuplicate ? Prior?.Id : null;
}

/// <summary>
/// **ไฟล์ที่อัปโหลดซ้ำ (hash ตรง) — ใช้ผลอ่านเดิมซ้ำได้เมื่อไร** · ตัวตัดสินตัวเดียวของด่านไฟล์ซ้ำใน <c>OcrService.ScanAsync</c>
///
/// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-10-09) ═══ สแกน e-Tax ไทวัสดุแล้วบรรทัดผิด · กด "ลบทั้งคู่" แล้วอัปไฟล์เดิมใหม่ ⇒ ได้ตัวเลขผิดชุดเดิม ·
/// ด่านเดิมคัดลอกผลอ่านของ "ต้นฉบับล่าสุดที่ hash ตรง" เสมอเมื่อไม่ได้กดสแกนใหม่ — <b>ไม่ดูว่าผลนั้นผลิตจากตัวแกะรุ่นไหน</b> ⇒
/// การแก้ตัวแกะไม่มีผลกับไฟล์ที่เคยสแกนแล้ว (ผลผิดถูกเล่นซ้ำตราบที่ต้นฉบับเก่ายังเหลือสักแถว) และไฟล์ e-Tax ที่อ่านซ้ำได้แน่นอน
/// แทบไม่มีต้นทุนก็ยังถูกคัดลอกแทนที่จะอ่านใหม่</para>
///
/// <para>═══ กติกา (ลำดับสำคัญ) ═══</para>
/// <list type="number">
///   <item>ผู้ใช้สั่งสแกนใหม่ (<c>forceRescan</c>) ⇒ ไม่ใช่ไฟล์ซ้ำ — พฤติกรรมเดิม (ปุ่ม "แกะใหม่" ต้องเดิน engine จริง)</item>
///   <item>ไม่มีสแกนเดิม · สแกนเดิมถูกลบ ⇒ ไม่ใช่ไฟล์ซ้ำ (ผู้ใช้ทิ้งผลนั้นและเอกสารของมันไปแล้ว — ไม่มีความเสี่ยงเคลมซ้ำจากแถวนั้น)</item>
///   <item>สแกนเดิมยังไม่ Completed ⇒ ไม่ใช่ไฟล์ซ้ำ (ไม่มีผลอ่าน)</item>
///   <item>ที่เหลือ = <b>ไฟล์ซ้ำเสมอ</b> (ธงเตือน §86/4 ภาษีซื้อซ้ำต้องอยู่) แล้วตัดสินแค่ "ผลอ่าน":
///     e-Tax XML ⇒ อ่านใหม่ · แถวเดิมเป็นสำเนา ⇒ อ่านใหม่ · รุ่นตัวแกะไม่ตรง <see cref="OcrExtractionVersion.Current"/> (รวม NULL) ⇒ อ่านใหม่ ·
///     นอกนั้น ⇒ คัดลอกผลเดิม (พฤติกรรมเดิม — ประหยัดโควตา engine)</item>
/// </list>
/// </summary>
public static class OcrDuplicateReusePolicy
{
    /// <summary>ชื่อ engine ของแถวที่คัดลอกผลอ่านมา (ไม่มี engine ทำงาน)</summary>
    public const string CachedEngine = "Cached";
    /// <summary>ชื่อ engine ของแถวที่อ่านจาก e-Tax XML ที่ฝังใน PDF</summary>
    public const string EtaxXmlEngine = "EtaxXml";

    /// <summary>แท็กบน ProcessingNotes: ใช้ผลอ่านเดิมซ้ำ (ไม่ได้อ่านไฟล์ใหม่)</summary>
    public const string ReusedTag = "[DUP-REUSED]";
    /// <summary>แท็กบน ProcessingNotes: ไฟล์ซ้ำแต่อ่านไฟล์ใหม่ด้วยตัวแกะรุ่นปัจจุบัน</summary>
    public const string ReExtractedTag = "[DUP-REEXTRACTED]";

    /// <summary>แถวนี้ "เป็นผลอ่านจริง" ที่ใช้เป็นต้นฉบับได้ไหม — สำเนา (Cached) ไม่ได้ (กันสำเนาของสำเนา) ·
    /// แถวที่ติดธงซ้ำแต่อ่านไฟล์จริง (อ่านใหม่เพราะรุ่นเปลี่ยน · เนื้อหาซ้ำ) ใช้ได้ · แถวเก่าที่ติดธงซ้ำโดยไม่บันทึก engine ถือเป็นสำเนา
    /// <para>⚠️ query ผู้สมัครใน <c>OcrService.ScanAsync</c> เขียนเงื่อนไขเดียวกันเป็นนิพจน์ EF (เมธอดนี้แปลเป็น SQL ไม่ได้) — แก้ที่นี่ต้องแก้ที่นั่น
    /// (ล็อกด้วย <c>tools/required_call_site_check.py</c>)</para></summary>
    private static bool IsRealExtraction(bool isDuplicate, string? ocrEngine) =>
        !isDuplicate || (!string.IsNullOrWhiteSpace(ocrEngine) && !string.Equals(ocrEngine, CachedEngine, StringComparison.Ordinal));

    public static OcrDuplicateReuseVerdict Decide(bool forceRescan, OcrPriorScanFacts? prior, int currentVersion = OcrExtractionVersion.Current)
    {
        if (forceRescan)
            return new(OcrDuplicateReuseAction.NotDuplicate, OcrDuplicateReExtractReason.ForceRescan, null, currentVersion);
        if (prior is null)
            return new(OcrDuplicateReuseAction.NotDuplicate, OcrDuplicateReExtractReason.None, null, currentVersion);
        if (prior.IsDeleted)
            return new(OcrDuplicateReuseAction.NotDuplicate, OcrDuplicateReExtractReason.PriorDeleted, null, currentVersion);
        if (!string.Equals(prior.ScanStatus, "Completed", StringComparison.Ordinal))
            return new(OcrDuplicateReuseAction.NotDuplicate, OcrDuplicateReExtractReason.PriorNotCompleted, null, currentVersion);

        if (string.Equals(prior.OcrEngine, EtaxXmlEngine, StringComparison.Ordinal))
            return new(OcrDuplicateReuseAction.ReExtract, OcrDuplicateReExtractReason.EtaxXmlDeterministic, prior, currentVersion);
        if (!IsRealExtraction(prior.IsDuplicate, prior.OcrEngine))
            return new(OcrDuplicateReuseAction.ReExtract, OcrDuplicateReExtractReason.PriorIsCopy, prior, currentVersion);
        if (prior.ExtractionVersion != currentVersion)
            return new(OcrDuplicateReuseAction.ReExtract, OcrDuplicateReExtractReason.OtherExtractionVersion, prior, currentVersion);

        return new(OcrDuplicateReuseAction.ReuseExtraction, OcrDuplicateReExtractReason.None, prior, currentVersion);
    }

    /// <summary>บรรทัดบน ProcessingNotes ที่บอกผู้ใช้ตรง ๆ ว่า "ใช้ผลเดิม" หรือ "อ่านใหม่ เพราะอะไร" · null เมื่อไม่ใช่ไฟล์ซ้ำ</summary>
    public static string? Note(OcrDuplicateReuseVerdict verdict)
    {
        if (!verdict.IsDuplicate || verdict.Prior is not { } p) return null;
        var ver = p.ExtractionVersion?.ToString() ?? "ไม่ทราบ";
        if (verdict.ReuseExtraction)
            return $"{ReusedTag} ไฟล์นี้ตรงกับสแกน {p.Id} ทุกไบต์ — ใช้ผลอ่านเดิม (engine: {p.OcrEngine ?? "unknown"} · ตัวแกะรุ่น {ver}) "
                + "โดยไม่ได้อ่านไฟล์ใหม่ · ถ้าผลไม่ถูก กด \"แกะใหม่\" เพื่ออ่านไฟล์ใหม่ด้วย engine จริง";
        var why = verdict.Reason switch
        {
            OcrDuplicateReExtractReason.EtaxXmlDeterministic => "ไฟล์มี e-Tax XML ฝังอยู่ อ่านซ้ำได้แน่นอนโดยไม่ใช้โควตา",
            OcrDuplicateReExtractReason.PriorIsCopy => "สแกนเดิมเป็นสำเนาของสแกนอื่นอีกที",
            OcrDuplicateReExtractReason.OtherExtractionVersion =>
                $"ผลอ่านเดิมมาจากตัวแกะรุ่น {ver} (รุ่นปัจจุบัน {verdict.CurrentVersion})",
            _ => "ไม่ใช้ผลอ่านเดิม",
        };
        var corrected = string.IsNullOrWhiteSpace(p.UserCorrectedFields)
            ? ""
            : $" · ค่าที่เคยแก้บนสแกนเดิม ({p.UserCorrectedFields.Trim()}) ไม่ได้คัดลอกมา — ตรวจช่องเหล่านี้ซ้ำ";
        return $"{ReExtractedTag} ไฟล์นี้ตรงกับสแกน {p.Id} ทุกไบต์ แต่อ่านไฟล์ใหม่ด้วยตัวแกะรุ่น {verdict.CurrentVersion} — {why} "
            + "· ยังนับเป็นไฟล์ซ้ำ (ตรวจว่าไม่ได้บันทึกใบเดียวกันสองครั้ง)" + corrected;
    }
}
