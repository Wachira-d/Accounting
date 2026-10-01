namespace Accounting.Helpers;

/// <summary>ผู้ติดต่อที่เป็นผู้สมัครของการจับคู่ด้วยชื่อ (โหลดจากชุดที่ <c>ContactTaxBranchKey.SoftScope</c> อนุญาต) ·
/// <see cref="TaxId"/>/<see cref="BranchCode"/> (รอบ 200 ทีม Z · K2-1) = ใช้ตัดสินว่า "ชื่อตรงหลายแถว" เป็นสาขาของนิติบุคคลเดียวกันไหม</summary>
public readonly record struct OcrCounterpartyCandidate(Guid Id, string? Name, bool IsCustomer, string? TaxId = null, string? BranchCode = null);

/// <summary>ที่มาของผลจับคู่ผู้ซื้อด้วยชื่อ — <see cref="OcrCounterpartyMatch.PickBuyerByName"/></summary>
public enum OcrCounterpartyNameBasis
{
    /// <summary>ไม่มีผู้สมัครที่เข้าเกณฑ์ ⇒ ผู้เรียกสร้างลูกค้าใหม่จากชื่อบนกระดาษ</summary>
    None,
    /// <summary>ชื่อเท่ากันหลัง normalize และรูปนิติบุคคลเดียวกัน (<see cref="ContactMatchKind.ExactName"/>)</summary>
    ExactName,
    /// <summary>ชื่อบนกระดาษเป็น<b>ส่วนหนึ่ง</b>ของชื่อลูกค้า<b>รายเดียว</b> (OCR ตัดชื่อ — ทรง RG-03 "แอม แฮปปี้" ⊂ "หจก. แอม แฮปปี้เนส")</summary>
    UniqueSuperstring,
    /// <summary>ชื่อตรงหลายแถวแต่ทุกแถวเป็น<b>นิติบุคคลเดียวกัน</b> (เลขผู้เสียภาษีเดียวกัน · สำนักงานใหญ่ + สาขา) ⇒ เลือกแถวด้วยกติกาสาขาของ
    /// <c>ContactTaxBranchKey.Pick</c> (ใบไม่ระบุสาขาผู้ซื้อ ⇒ สำนักงานใหญ่) — รอบ 200 ทีม Z (K2-1 ข) · เดิมกำกวม ⇒ สร้างแถวที่สามทุกครั้ง</summary>
    SameEntityBranch,
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
///
/// <para>═══ รอบ 200 ทีม Z (ฝ่ายค้านรอบสอง K2-1 — ทิศตรงข้ามของ C-01) ═══
/// (ก) ผู้ติดต่อเดิมที่<b>ชื่อตรง</b> (ExactName) ใช้ได้แม้ยังไม่ติ๊ก <c>IsCustomer</c> (ชื่อตรงทุกตัว = รายเดียวกัน · ไม่สร้างซ้ำ) ·
/// (ข) ชื่อตรง/ครอบชื่อหลายแถวที่เป็นนิติบุคคลเดียวกัน (เลขผู้เสียภาษีเดียวกันทุกแถว — สำนักงานใหญ่ + แถวสาขาที่รอบ 197 สร้างด้วยชื่อนิติบุคคล) ⇒
/// เลือกแถวด้วย <see cref="ContactTaxBranchKey.Pick"/> ตามสาขาผู้ซื้อบนกระดาษ (ไม่ระบุ ⇒ สำนักงานใหญ่) — <b>ห้ามสร้างแถวที่สาม</b> ·
/// (ค) ชื่อถูกตัด = superstring ของลูกค้าเท่านั้น (เดิม) — ผู้ติดต่อที่ไม่ใช่ลูกค้าและครอบชื่อ <b>ไม่ผูกอัตโนมัติ</b> (ผู้ขายล้วนอาจเป็นคนละรายที่ชื่อคล้าย)
/// แต่ถูกบอกชื่อในโน้ต · <b>ทุกกรณีที่ผู้เรียกจะสร้างลูกค้าใหม่ มีโน้ต <c>[BUYER]</c> เสมอ</b> (<see cref="OcrCounterpartyNamePick.Note"/> ไม่ว่างเมื่อ ContactId ว่าง) ·
/// เดิม Basis.None ไม่มีโน้ต ⇒ ลูกค้าซ้ำเกิดเงียบ</para>
/// </summary>
public static class OcrCounterpartyMatch
{
    /// <summary>ความยาวแก่นชื่อขั้นต่ำของการจับแบบ "ส่วนหนึ่งของชื่อ" (ไม่นับคำบอกรูปนิติบุคคล/ช่องว่าง)</summary>
    public const int MinCoreLength = 4;

    /// <param name="scannedName">ชื่อผู้ซื้อบนกระดาษ</param>
    /// <param name="candidates">ผู้สมัคร (ชุดที่ SoftScope อนุญาต)</param>
    /// <param name="paperBranchCode">รหัสสาขาผู้ซื้อบนกระดาษ (null/ว่าง = ไม่ระบุ ⇒ สำนักงานใหญ่เมื่อชื่อตรงหลายสาขาของนิติบุคคลเดียว)</param>
    public static OcrCounterpartyNamePick PickBuyerByName(string? scannedName, IEnumerable<OcrCounterpartyCandidate> candidates,
        string? paperBranchCode = null)
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
            if (SameEntityRow(exact, paperBranchCode) is Guid sameEntity) return new(sameEntity, OcrCounterpartyNameBasis.SameEntityBranch, null);
            var exactCustomers = exact.Where(c => c.IsCustomer).ToList();
            if (exactCustomers.Count == 1) return new(exactCustomers[0].Id, OcrCounterpartyNameBasis.ExactName, null);
            if (LiteralTieBreak(scannedName, exactCustomers.Count > 0 ? exactCustomers : exact) is Guid literal)
                return new(literal, OcrCounterpartyNameBasis.ExactName, null);
            return new(null, OcrCounterpartyNameBasis.Ambiguous, AmbiguousNote(scannedName, exact.Count));
        }

        if (core.Length < MinCoreLength) return new(null, OcrCounterpartyNameBasis.TooShort, TooShortNote(scannedName));
        var containing = list.Where(c => ContactTaxBranchKey.NameCore(c.Name).Contains(core, StringComparison.Ordinal)).ToList();
        var super = containing.Where(c => c.IsCustomer).ToList();
        if (super.Count == 1) return new(super[0].Id, OcrCounterpartyNameBasis.UniqueSuperstring, null);
        if (super.Count > 1)
        {
            if (SameEntityRow(super, paperBranchCode) is Guid sameEntity) return new(sameEntity, OcrCounterpartyNameBasis.SameEntityBranch, null);
            return new(null, OcrCounterpartyNameBasis.Ambiguous, AmbiguousNote(scannedName, super.Count));
        }
        var nonCustomers = containing.Where(c => !c.IsCustomer).Select(c => (c.Name ?? "").Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return new(null, OcrCounterpartyNameBasis.None, nonCustomers.Count > 0
            ? NonCustomerNote(scannedName, nonCustomers)
            : NewCustomerNote(scannedName));
    }

    /// <summary>
    /// **คำค้นเสริมของการกรองผู้สมัครในฐานข้อมูล** (รอบ 200 ทีม Z · K2-1 ก) — ผู้เรียกกรองด้วย <c>Name.Contains(ชื่อดิบ)</c> ซึ่งตัดแถวที่ชื่อ "ตรงกันหลัง normalize"
    /// แต่สะกดรูปนิติบุคคลต่างกันทิ้งก่อนถึงตัวตัดสิน ("หจก.แอม แฮปปี้เนส" ⊄ "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส") ⇒ สร้างลูกค้าซ้ำ ·
    /// คืนคำ (คั่นด้วยช่องว่าง) ที่ยาวที่สุดซึ่ง<b>ไม่มีคำบอกรูปนิติบุคคลปน</b> และแก่นยาว ≥ <see cref="MinCoreLength"/> — ใช้เป็นเงื่อนไข OR เพิ่ม ·
    /// ตัวตัดสินจริงยังเป็น <see cref="PickBuyerByName"/> (คำค้นกว้างขึ้นไม่ทำให้จับหลวมขึ้น) · ไม่มีคำที่เข้าเกณฑ์ = <c>null</c>
    /// </summary>
    public static string? PrefilterToken(string? scannedName)
    {
        string? best = null;
        foreach (var raw in (scannedName ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = raw.Trim('.', ',', '(', ')', '"', '\'', '“', '”');
            var core = ContactTaxBranchKey.NameCore(tok);
            if (core.Length < MinCoreLength) continue;
            // แก่นสั้นกว่าตัวอักษรในคำ = มีคำบอกรูปนิติบุคคลถูกตัดออก ⇒ คำดิบไม่อยู่ในชื่อที่สะกดรูปต่าง
            var letters = tok.Count(ch => char.IsLetterOrDigit(ch)
                || char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.NonSpacingMark
                    or System.Globalization.UnicodeCategory.SpacingCombiningMark);
            if (core.Length != letters) continue;
            if (best == null || tok.Length > best.Length) best = tok;
        }
        return best;
    }

    /// <summary>
    /// ชื่อตรง/ครอบชื่อหลายแถว — ทุกแถวถือเลขผู้เสียภาษี 13 หลัก<b>เดียวกัน</b> (นิติบุคคลเดียว) ⇒ แถวตามกติกาสาขาของ <see cref="ContactTaxBranchKey.Pick"/>
    /// (สาขาบนกระดาษ ⇒ แถวสาขานั้น · ไม่ระบุ ⇒ สำนักงานใหญ่/แถวไม่ระบุสาขา → แถวเดียว → รหัสต่ำสุด) · แถวใดไม่มีเลข/คนละเลข ⇒ null (ไม่รู้ว่าเป็นรายเดียวกัน)
    /// · สาขาบนกระดาษไม่มีแถว ⇒ null (ผู้เรียกถือว่ากำกวม — ไม่ผูกสาขาอื่นแทน)
    /// </summary>
    private static Guid? SameEntityRow(IReadOnlyList<OcrCounterpartyCandidate> rows, string? paperBranchCode)
    {
        if (rows.Count < 2) return null;
        var taxes = rows.Select(r => Digits(r.TaxId)).ToList();
        if (taxes[0].Length != 13 || taxes.Any(t => t != taxes[0])) return null;
        var m = ContactTaxBranchKey.Pick(rows.Select(r => new ContactKeyCandidate(r.Id, r.TaxId, r.BranchCode)), taxes[0], paperBranchCode);
        return m.ContactId;
    }

    /// <summary>
    /// ชื่อตรงหลัง normalize หลายแถว (สะกดรูปนิติบุคคลต่างกัน) ที่<b>ไม่มีหลักฐานว่าเป็นคนละราย</b> (เลข 13 หลักที่รู้ไม่เกิน 1 ค่า) ⇒
    /// แถวเดียวที่ชื่อตรง<b>ตัวอักษร</b>กับกระดาษ (trim · ordinal) — ฝ่ายค้านรอบสาม Z-1: คำค้นเสริม <see cref="PrefilterToken"/> ดึงแถวซ้ำเก่าที่สะกดต่างเข้ามา
    /// ทำให้บริษัทที่มีลูกค้าซ้ำจากบั๊กเดิมได้ "กำกวม" แล้วสร้างแถวใหม่ทุกครั้งที่สแกน · เดิม (กรอง <c>Contains(ชื่อดิบ)</c>) ผูกแถวที่สะกดตรงตัว ⇒ คงพฤติกรรมนั้น ·
    /// ตรงตัวอักษร 0 หรือ ≥ 2 แถว หรือเลขผู้เสียภาษีขัดกัน ⇒ null (ยังกำกวม)
    /// </summary>
    private static Guid? LiteralTieBreak(string? scannedName, IReadOnlyList<OcrCounterpartyCandidate> rows)
    {
        var raw = (scannedName ?? "").Trim();
        if (raw.Length == 0) return null;
        if (rows.Select(r => Digits(r.TaxId)).Where(t => t.Length == 13).Distinct(StringComparer.Ordinal).Count() > 1) return null;
        var literal = rows.Where(r => string.Equals((r.Name ?? "").Trim(), raw, StringComparison.Ordinal)).ToList();
        return literal.Count == 1 ? literal[0].Id : null;
    }

    private static string Digits(string? s) => new((s ?? "").Where(ch => ch >= '0' && ch <= '9').ToArray());

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

    /// <summary>ไม่พบลูกค้าเดิมเลย ⇒ ผู้เรียกสร้างลูกค้าใหม่ — บอกให้เห็น (รอบ 200 ทีม Z · K2-1: เดิมไม่มีโน้ต ⇒ ลูกค้าซ้ำเกิดเงียบ)</summary>
    private static string NewCustomerNote(string? name)
        => $"[BUYER] ไม่พบลูกค้าเดิมที่ชื่อ '{name?.Trim()}' — สร้างลูกค้าใหม่ตามชื่อบนกระดาษ "
           + "(ถ้าเป็นลูกค้าเดิมที่ชื่อต่างไป เลือกลูกค้าในเอกสาร แล้วรวมผู้ติดต่อซ้ำที่หน้าผู้ติดต่อ)";

    /// <summary>ชื่อบนกระดาษเป็นส่วนหนึ่งของผู้ติดต่อที่<b>ยังไม่ได้ติ๊กเป็นลูกค้า</b> — ไม่ผูกอัตโนมัติ (อาจเป็นผู้ขายคนละราย) แต่บอกชื่อให้ผู้ใช้เลือกได้</summary>
    private static string NonCustomerNote(string? name, IReadOnlyList<string> existing)
        => $"[BUYER] ชื่อผู้ซื้อ '{name?.Trim()}' ตรงกับผู้ติดต่อเดิมที่ยังไม่ได้ติ๊กเป็นลูกค้า: "
           + string.Join(" · ", existing.Take(3).Select(n => $"'{n}'")) + (existing.Count > 3 ? $" (และอีก {existing.Count - 3} ราย)" : "")
           + " — ระบบไม่ผูกให้อัตโนมัติ จึงสร้างลูกค้าใหม่ตามชื่อบนกระดาษ (ถ้าเป็นรายเดียวกัน เลือกผู้ติดต่อนั้นในเอกสาร แล้วรวมผู้ติดต่อซ้ำที่หน้าผู้ติดต่อ)";

    /// <summary>
    /// เลขผู้เสียภาษีผู้ซื้อบนกระดาษมีผู้ติดต่ออยู่แล้วแต่<b>คนละสาขา</b> (<c>ContactTaxBranchKey.SoftScope</c> = ห้ามถอยไปจับชื่อ) ⇒ ผู้เรียกสร้างผู้ติดต่อของสาขานั้น —
    /// บอกให้เห็น (รอบ 200 ทีม Z · K2-1 "สร้างใหม่ต้องมีโน้ตเสมอ")
    /// </summary>
    public static string NewBranchNote(string? name, string? taxId, string? branchCode)
        => $"[BUYER] เลขผู้เสียภาษีผู้ซื้อ {taxId?.Trim()} มีผู้ติดต่ออยู่แล้วแต่คนละสาขา — สร้างผู้ติดต่อของ{TaxBranchCode.Label(branchCode)} "
           + $"('{name?.Trim()}') แยกตามประกาศอธิบดีฯ 199 (ถ้าสาขาบนกระดาษอ่านผิด แก้รหัสสาขาผู้ซื้อแล้วสร้างใหม่)";

    private static string TooShortNote(string? name)
        => $"[BUYER] ชื่อผู้ซื้อที่อ่านได้ ('{name?.Trim()}') สั้นเกินกว่าจะใช้จับคู่ลูกค้าเดิม — ตรวจชื่อผู้ซื้อกับกระดาษ";
}
