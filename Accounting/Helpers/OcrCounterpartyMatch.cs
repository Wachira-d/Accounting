namespace Accounting.Helpers;

/// <summary>ผู้ติดต่อที่เป็นผู้สมัครของการจับคู่ด้วยชื่อ (โหลดจากชุดที่ <c>ContactTaxBranchKey.SoftScope</c> อนุญาต)</summary>
public readonly record struct OcrCounterpartyCandidate(Guid Id, string? Name, bool IsCustomer);

/// <summary>ที่มาของผลจับคู่ผู้ซื้อด้วยชื่อ — <see cref="OcrCounterpartyMatch.PickBuyerByName"/></summary>
public enum OcrCounterpartyNameBasis
{
    /// <summary>ไม่มีผู้สมัครที่เข้าเกณฑ์ ⇒ ผู้เรียกสร้างลูกค้าใหม่จากชื่อบนกระดาษ</summary>
    None,
    /// <summary>ชื่อเท่ากันหลัง normalize และรูปนิติบุคคลเดียวกัน (<see cref="ContactMatchKind.ExactName"/>)</summary>
    ExactName,
    /// <summary>ชื่อบนกระดาษเป็น<b>ส่วนหนึ่ง</b>ของชื่อลูกค้า<b>รายเดียว</b> (OCR ตัดชื่อ — ทรง RG-03 "แอม แฮปปี้" ⊂ "หจก. แอม แฮปปี้เนส")</summary>
    UniqueSuperstring,
    /// <summary>ชื่อสั้นเกินกว่าจะเป็นหลักฐาน ("บจก" · "จำกัด" · "บริษัท" — แก่นชื่อว่าง/สั้นกว่าเกณฑ์) ⇒ ไม่จับ</summary>
    TooShort,
    /// <summary>เข้าเกณฑ์มากกว่าหนึ่งราย ⇒ ไม่เดา (เดิมเลือก "ชื่อสั้นสุด" = เดาเงียบ)</summary>
    Ambiguous,
}

/// <summary>ผลจับคู่ผู้ซื้อด้วยชื่อ · <see cref="Note"/> = ข้อความลง ProcessingNotes เมื่อไม่จับเพราะกำกวม/สั้นเกิน (null = ไม่มีอะไรต้องบอก)</summary>
public readonly record struct OcrCounterpartyNamePick(Guid? ContactId, OcrCounterpartyNameBasis Basis, string? Note);

/// <summary>
/// **คู่ค้าฝั่งขายของสแกนคือใคร — ด่านจับคู่ด้วยชื่อ + ข้อความเมื่อหาคู่ค้าไม่ได้** (pure · รอบ 200 ทีม K2 · ผลตรวจรอบ 189 C-01/C-03)
///
/// <para>═══ ที่มา (C-01) ═══ ฝั่งขายของ <c>CreateDocumentFromScanCoreAsync</c> ถอยไปจับลูกค้าด้วย <c>Name.Contains(ชื่อผู้ซื้อ)</c> ดิบ ๆ แล้วหยิบ
/// "ชื่อสั้นสุด" ⇒ OCR อ่านชื่อผู้ซื้อได้แค่ "บริษัท" หรือ "จำกัด" ก็จับลูกค้าคนแรกของบริษัทได้ · ชื่อที่ถูกตัดตรงลูกค้าสองรายก็เดาเงียบ ·
/// ไม่ดู <c>IsCustomer</c> (ผู้ขายล้วนถูกเลือกเป็นลูกค้าของใบขาย) ⇒ ใบกำกับ §86/4 ออกให้ผู้ซื้อผิดราย + ลูกหนี้ลงคู่ค้าผิด</para>
///
/// <para>กติกา (ฝั่งซื้อไม่เคย fuzzy — ใช้คีย์เลขภาษี+สาขา · ฝั่งขายยังต้องถอยด้วยชื่อได้เพราะใบขายจำนวนมากไม่มีเลขผู้ซื้อ):
/// ชื่อเท่ากันหลัง normalize (<see cref="ContactTaxBranchKey.NameMatchKind"/> = ExactName) รายเดียว → ใช้ · หลายราย → ลูกค้ารายเดียวในนั้น → ใช้ ·
/// ไม่งั้นชื่อบนกระดาษต้องมีแก่นยาว ≥ <see cref="MinCoreLength"/> และเป็นส่วนหนึ่งของชื่อ<b>ลูกค้า</b> (<c>IsCustomer</c>)<b>รายเดียว</b> (superstring ไม่ใช่
/// fuzzy — CLAUDE.md §H) · กำกวม/สั้นเกิน = ไม่จับ (ผู้เรียกสร้างลูกค้าใหม่ = มองเห็นและรวมทีหลังได้ · ผิดราย = เงียบ)</para>
/// </summary>
public static class OcrCounterpartyMatch
{
    /// <summary>ความยาวแก่นชื่อขั้นต่ำของการจับแบบ "ส่วนหนึ่งของชื่อ" (ไม่นับคำบอกรูปนิติบุคคล/ช่องว่าง)</summary>
    public const int MinCoreLength = 4;

    public static OcrCounterpartyNamePick PickBuyerByName(string? scannedName, IEnumerable<OcrCounterpartyCandidate> candidates)
    {
        var core = ContactTaxBranchKey.NameCore(scannedName);
        if (core.Length == 0) return new(null, OcrCounterpartyNameBasis.TooShort, TooShortNote(scannedName));
        var list = (candidates ?? Enumerable.Empty<OcrCounterpartyCandidate>())
            .Where(c => c.Id != Guid.Empty)
            .GroupBy(c => c.Id).Select(g => g.First())
            .ToList();

        var exact = list.Where(c => ContactTaxBranchKey.NameMatchKind(scannedName, c.Name) == ContactMatchKind.ExactName).ToList();
        if (exact.Count == 1) return new(exact[0].Id, OcrCounterpartyNameBasis.ExactName, null);
        if (exact.Count > 1)
        {
            var exactCustomers = exact.Where(c => c.IsCustomer).ToList();
            if (exactCustomers.Count == 1) return new(exactCustomers[0].Id, OcrCounterpartyNameBasis.ExactName, null);
            return new(null, OcrCounterpartyNameBasis.Ambiguous, AmbiguousNote(scannedName, exact.Count));
        }

        if (core.Length < MinCoreLength) return new(null, OcrCounterpartyNameBasis.TooShort, TooShortNote(scannedName));
        var super = list
            .Where(c => c.IsCustomer && ContactTaxBranchKey.NameCore(c.Name).Contains(core, StringComparison.Ordinal))
            .ToList();
        if (super.Count == 1) return new(super[0].Id, OcrCounterpartyNameBasis.UniqueSuperstring, null);
        if (super.Count > 1) return new(null, OcrCounterpartyNameBasis.Ambiguous, AmbiguousNote(scannedName, super.Count));
        return new(null, OcrCounterpartyNameBasis.None, null);
    }

    /// <summary>
    /// **ข้อความเมื่อสร้างเอกสารจากสแกนไม่ได้เพราะไม่รู้คู่ค้า** (C-03) — เดิมโยน <c>InvalidOperationException</c> ภาษาอังกฤษ ⇒ middleware ปิดบังเป็น
    /// "ไม่สามารถดำเนินการนี้ได้ในสถานะปัจจุบัน" ผู้ใช้ตัน · ข้อความชี้ป้ายไทยที่ผู้ใช้เห็นบนหน้ารีวิว ("ชื่อผู้ซื้อ"/"ชื่อผู้ขาย" · "จับคู่ผู้ติดต่อ")
    /// </summary>
    public static string NoCounterpartyMessage(bool salesSide) => salesSide
        ? "ยังไม่รู้ว่าผู้ซื้อ (ลูกค้า) ของใบนี้คือใคร — ระบบอ่านชื่อ/เลขผู้เสียภาษีผู้ซื้อจากกระดาษไม่ได้ · "
          + "เปิดการ์ดนี้ (ตรวจสอบ & สอนระบบ) แล้วกรอก “ชื่อผู้ซื้อ” หรือเลือกลูกค้าในหัวข้อ “จับคู่ผู้ติดต่อ” "
          + "แล้วกดสร้างเอกสารอีกครั้ง"
        : "ยังไม่รู้ว่าผู้ขายของใบนี้คือใคร — ระบบอ่านชื่อผู้ขายจากกระดาษไม่ได้ (ภาพไม่ชัด/ใบต่างประเทศ) · "
          + "เปิดการ์ดนี้ (ตรวจสอบ & สอนระบบ) แล้วกรอก “ชื่อผู้ขาย” หรือเลือกผู้ติดต่อในหัวข้อ “จับคู่ผู้ติดต่อ” แล้วกดสร้างเอกสารอีกครั้ง";

    /// <summary>รหัสกฎของ <see cref="NoCounterpartyMessage"/></summary>
    public const string NoCounterpartyRuleCode = "OCR-NO-COUNTERPARTY";

    private static string AmbiguousNote(string? name, int count)
        => $"[BUYER] ชื่อผู้ซื้อ '{name?.Trim()}' ตรงกับลูกค้า {count} ราย — ไม่เดาว่าเป็นรายไหน จึงสร้างลูกค้าใหม่ตามชื่อบนกระดาษ "
           + "(ถ้าเป็นรายเดิม เลือกลูกค้าในเอกสาร แล้วรวมผู้ติดต่อซ้ำที่หน้าผู้ติดต่อ)";

    private static string TooShortNote(string? name)
        => $"[BUYER] ชื่อผู้ซื้อที่อ่านได้ ('{name?.Trim()}') สั้นเกินกว่าจะใช้จับคู่ลูกค้าเดิม — ตรวจชื่อผู้ซื้อกับกระดาษ";
}
