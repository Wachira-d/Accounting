using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ที่มาของคำตอบ "คู่ค้ารายนี้เป็นชนิดอะไร" — เรียงตาม<b>ระยะห่างจากของจริง</b>
/// (DECISION_DOCTRINE §1 G1) ไม่ใช่ความมั่นใจของผู้เสนอ</summary>
public enum ContactTypeEvidence
{
    /// <summary>ยังไม่มีหลักฐานใดเลย — ต้องคืน <see cref="ContactType.Unknown"/></summary>
    None = 0,
    /// <summary>คำในชื่อ ("บริษัท…จำกัด", "Co., Ltd.") — <b>หลักฐานอ่อนที่สุด</b>
    /// เพราะชื่อเป็นข้อความอิสระที่พิมพ์ผิด/ตัดคำได้</summary>
    NamePrefix = 1,
    /// <summary>ทะเบียนราชการ (DBD/RD) ยืนยันว่าเลขนี้เป็นนิติบุคคลที่จดทะเบียนจริง</summary>
    Registry = 2,
    /// <summary>รูปของเลขประจำตัวผู้เสียภาษี 13 หลักที่ผ่าน checksum —
    /// หลักแรก <c>0</c> = ทะเบียนนิติบุคคล/ราชการ · <c>1–8</c> = บุคคลธรรมดา.
    /// เป็น<b>กฎหมาย</b> ไม่ใช่การอนุมาน</summary>
    TaxIdShape = 3,
    /// <summary>มนุษย์/ระบบต้นทางประกาศมาเอง — ชั้นบนสุด เพราะเป็นเจตนาที่รับผิดชอบได้
    /// และเป็นชั้นเดียวที่แยก <c>GovernmentAgency</c> ออกจาก <c>JuristicPerson</c> ได้
    /// (ทั้งคู่ถือเลข 13 หลักขึ้นต้น 0 เหมือนกัน)</summary>
    Declared = 4,
}

/// <summary>คำตอบของ <see cref="ContactTypeResolver"/> — ชนิด · ที่มา · เหตุผล (ไทย พร้อมโชว์/ลง audit)</summary>
public readonly record struct ContactTypeVerdict(
    ContactType Type, ContactTypeEvidence Evidence, string Reason)
{
    /// <summary>ตัดสินได้จริง (ไม่ใช่ "ยังไม่รู้")</summary>
    public bool IsResolved => Type != ContactType.Unknown;
}

/// <summary>คำตอบของ <see cref="ContactTypeResolver.ResolveWithBranch"/> — ชนิด + รหัสสาขา
/// ต้องออกมาจาก<b>ตัวเดียวกัน</b> เพราะ §86/4(2) ผูกสองค่านี้เข้าด้วยกัน:
/// รหัสสาขาเป็นเรื่องของนิติบุคคล/ราชการเท่านั้น (ประกาศอธิบดีฯ ฉบับที่ 199)</summary>
public readonly record struct ContactIdentityVerdict(
    ContactType Type, ContactTypeEvidence Evidence, string Reason, string? BranchCode)
{
    public bool IsResolved => Type != ContactType.Unknown;
}

/// <summary>
/// **"คู่ค้ารายนี้เป็นบุคคลธรรมดา / นิติบุคคล / ราชการ / ยังไม่รู้" — ตัวตัดสินตัวเดียวของระบบ**
///
/// ═══ ทำไมต้องมีที่เดียว (DECISION_AUDIT_2026-09-18 §9.3 D-1) ═══
/// <para><c>ContactType</c> ตัดสิน <b>ภ.ง.ด.3 vs ภ.ง.ด.53</b> (ท.ป.4/2528) และ
/// <b>scheme ของ e-Tax XML</b> ที่ส่งกรมสรรพากร (<c>NIDN</c> สำหรับบุคคล ·
/// <c>TXID</c> สำหรับนิติบุคคล — ETDA ขมธอ.3-2560) ⇒ เดาผิด = แบบยื่นผิดและ XML
/// ผิด schema. กติกา "เดาชนิด" เคยกระจายอยู่หลายไฟล์ด้วยสูตรคนละแบบ
/// (<c>Length == 13</c> เฉย ๆ · <c>StartsWith('0')</c> ไม่ตรวจ checksum ·
/// <c>BranchCode != "00000"</c> · "มีชื่อบริษัทในช่อง company") — ทุกตัวจบด้วยการ
/// <b>เดาเป็น <c>Individual</c></b> เมื่อไม่รู้ ⇒ ความไม่รู้ถูกกลบจนมองไม่เห็น.</para>
///
/// ═══ ลำดับชั้นหลักฐาน (G1 · ชั้นบนชนะแม้ชั้นล่างจะ "ดูมั่นใจกว่า") ═══
/// <list type="number">
/// <item><see cref="ContactTypeEvidence.Declared"/> — ผู้ใช้/ระบบต้นทางเลือกเอง</item>
/// <item><see cref="ContactTypeEvidence.TaxIdShape"/> — เลข 13 หลักที่ผ่าน checksum mod-11</item>
/// <item><see cref="ContactTypeEvidence.Registry"/> — ทะเบียน DBD/RD ยืนยัน</item>
/// <item><see cref="ContactTypeEvidence.NamePrefix"/> — คำนำหน้านิติบุคคลในชื่อ</item>
/// <item>ไม่มีเลย → <see cref="ContactType.Unknown"/> — <b>ห้ามเดา</b></item>
/// </list>
///
/// <para><b>ทำไม Declared ชนะ TaxIdShape</b>: หน่วยงานราชการก็ถือเลข 13 หลักขึ้นต้น 0
/// เหมือนบริษัท ⇒ รูปเลขแยก <c>GovernmentAgency</c> ออกจาก <c>JuristicPerson</c>
/// ไม่ได้เลย. ถ้าให้รูปเลขชนะ หน่วยงานราชการทุกแห่งจะถูกกดกลับเป็น "นิติบุคคล"
/// ทุกครั้งที่ sync — แต่เมื่อขัดกัน <see cref="ContactTypeVerdict.Reason"/>
/// จะ<b>บันทึกตัวที่แพ้ไว้</b> (G4) ให้ผู้ใช้เห็นและสลับได้</para>
///
/// <para><b>ตัวเลขล้วนไม่มี "การสะกดผิด"</b> (CLAUDE.md §F2 ข้อ 3) ⇒ เลขผู้เสียภาษี
/// ใช้ <see cref="ThaiTaxId"/> ตรง ๆ <b>ห้าม fuzzy</b> · ส่วนชื่อเป็นข้อความ
/// จึงเทียบแบบ "มีคำนี้อยู่ไหม" ได้ แต่อยู่ชั้นล่างสุดเสมอ</para>
///
/// <para>G6: <c>public static</c> · pure ไม่มี I/O · รับข้อเท็จจริงเข้า (ผู้เรียกไปถาม
/// ทะเบียนมาเอง) · ไม่ throw · เกณฑ์เป็น <c>public const</c></para>
/// </summary>
public static class ContactTypeResolver
{
    /// <summary>คำนำหน้า/คำลงท้ายที่บ่งชี้นิติบุคคลไทย — ใช้เฉพาะชั้นล่างสุด
    ///
    /// <para>ตั้งใจ<b>ไม่</b>รวมคำว่า "ร้าน"/"shop" — ร้านค้าส่วนใหญ่ในไทยเป็นบุคคล
    /// ธรรมดาที่จดพาณิชย์ ไม่ใช่นิติบุคคล ⇒ ใส่แล้วจะดันคนเป็นบริษัททั้งฐาน</para></summary>
    public static readonly string[] JuristicNameMarkersTh =
    {
        "บริษัท", "บมจ", "บจก", "บจ.", "หจก", "หสน", "ห้างหุ้นส่วน", "มหาชน",
    };

    /// <summary>คำลงท้ายนิติบุคคลภาษาอังกฤษ (เทียบแบบ lower-case)</summary>
    public static readonly string[] JuristicNameMarkersEn =
    {
        "co.,ltd", "co., ltd", "co.ltd", "co ltd", "company limited", "limited partnership",
        "ltd.", "ltd ", " plc", "public company", "partnership", "corporation", "incorporated",
        " inc.", " inc ",
    };

    /// <summary>รหัสสาขาสำนักงานใหญ่ตามประกาศอธิบดีฯ ฉบับที่ 199</summary>
    public const string HeadOfficeBranchCode = "00000";

    // ── ชั้นที่ 4: ประกาศของมนุษย์/ระบบต้นทาง ────────────────────────────────

    /// <summary>แปลงค่าที่ระบบต้นทางส่งมาเป็น enum — คืน <c>null</c> เมื่อ<b>แปลงไม่ได้</b>
    /// (ห้ามแปลงค่าที่อ่านไม่ออกเป็น <c>Individual</c> เงียบ ๆ อย่างสำเนาเดิม)
    ///
    /// <para>รับทั้งชื่อ enum ("JuristicPerson") · เลข ("2") · และคำพ้องที่พาร์ตเนอร์
    /// ใช้จริง ("company", "juristic", "person", "นิติบุคคล", "บุคคลธรรมดา")</para></summary>
    public static ContactType? ParseDeclared(string? sent)
    {
        if (string.IsNullOrWhiteSpace(sent)) return null;
        var s = sent.Trim();

        if (Enum.TryParse<ContactType>(s, ignoreCase: true, out var parsed)
            && Enum.IsDefined(typeof(ContactType), parsed))
            return parsed == ContactType.Unknown ? null : parsed;

        return s.ToLowerInvariant() switch
        {
            "1" or "individual" or "person" or "natural" or "naturalperson"
                or "บุคคล" or "บุคคลธรรมดา" => ContactType.Individual,
            "2" or "juristic" or "juristicperson" or "company" or "corporate"
                or "legalentity" or "นิติบุคคล" => ContactType.JuristicPerson,
            "3" or "government" or "governmentagency" or "govt" or "publicsector"
                or "ราชการ" or "หน่วยงานราชการ" => ContactType.GovernmentAgency,
            _ => null,
        };
    }

    // ── ชั้นที่ 3: รูปของเลขผู้เสียภาษี ──────────────────────────────────────

    /// <summary>ชนิดที่<b>พิสูจน์ได้จากเลขผู้เสียภาษี</b> — <c>null</c> = พิสูจน์ไม่ได้
    /// (เลขว่าง/ไม่ครบ 13/checksum ไม่ผ่าน). แยก <c>GovernmentAgency</c> ไม่ได้
    /// เพราะราชการใช้เลขขึ้นต้น 0 เหมือนบริษัท</summary>
    public static ContactType? FromTaxId(string? taxId)
    {
        if (!ThaiTaxId.IsValid(taxId)) return null;
        return ThaiTaxId.IsJuristic(taxId) ? ContactType.JuristicPerson : ContactType.Individual;
    }

    // ── ชั้นที่ 1: คำในชื่อ ──────────────────────────────────────────────────

    /// <summary>ชื่อนี้มีคำบ่งชี้นิติบุคคลไหม — <b>หลักฐานเชิงบวกด้านเดียว</b>:
    /// "ไม่มีคำว่าบริษัท" <b>ไม่ได้</b>แปลว่าเป็นบุคคลธรรมดา (ชื่อย่อ/ชื่อแบรนด์
    /// ละตินของบริษัทก็ไม่มีคำพวกนี้) ⇒ คืน <c>false</c> = "ไม่รู้" ไม่ใช่ "บุคคล"
    ///
    /// <para><c>private</c> โดยตั้งใจ — ชั้นล่างสุดนี้<b>ห้ามถูกเรียกเดี่ยว ๆ</b>
    /// จากที่อื่น มิฉะนั้นจะเกิดสำเนาที่สองที่ข้ามชั้นบน (ผู้เรียกต้องผ่าน
    /// <see cref="Resolve"/> เสมอ) · ทดสอบผ่าน <see cref="Resolve"/></para></summary>
    private static bool NameLooksJuristic(string? name)
    {
        var t = (name ?? "").Trim();
        if (t.Length == 0) return false;
        if (JuristicNameMarkersTh.Any(k => t.Contains(k, StringComparison.Ordinal))) return true;
        var lower = t.ToLowerInvariant();
        return JuristicNameMarkersEn.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    // ── ตัวตัดสินหลัก ────────────────────────────────────────────────────────

    /// <summary>
    /// ชนิดผู้ติดต่อ + ที่มา + เหตุผล จากหลักฐานที่ผู้เรียกมีอยู่ ณ ตอนนั้น
    /// </summary>
    /// <param name="declared">ค่าที่ผู้ใช้เลือก / ระบบต้นทางส่งมา (<c>null</c> = ไม่ได้ส่ง)</param>
    /// <param name="taxId">เลขผู้เสียภาษีที่กำลังจะบันทึก (ยังไม่ต้อง normalize)</param>
    /// <param name="name">ชื่อคู่ค้า</param>
    /// <param name="registryJuristic">ทะเบียน DBD/RD ยืนยันว่าเป็นนิติบุคคลจริง
    /// — ผู้เรียกไปถามมาเอง (G6: helper ไม่มี I/O)</param>
    public static ContactTypeVerdict Resolve(
        string? declared, string? taxId, string? name, bool registryJuristic = false)
    {
        var fromTax = FromTaxId(taxId);
        var parsed = ParseDeclared(declared);

        // ชั้น 4 — ประกาศชนะทุกชั้น แต่ต้องบันทึกตัวที่แพ้ (G4)
        if (parsed.HasValue)
        {
            var reason = $"ระบุมาเอง ({ThaiLabel(parsed.Value)})";
            if (fromTax.HasValue && fromTax.Value != parsed.Value
                && !(parsed.Value == ContactType.GovernmentAgency && fromTax.Value == ContactType.JuristicPerson))
                reason += $" — ⚠️ ขัดกับรูปเลขผู้เสียภาษีซึ่งบ่งชี้ \"{ThaiLabel(fromTax.Value)}\" (ใช้ค่าที่ระบุมา โปรดตรวจเลขอีกครั้ง)";
            return new ContactTypeVerdict(parsed.Value, ContactTypeEvidence.Declared, reason);
        }

        // ชั้น 3 — รูปเลข 13 หลักที่ผ่าน checksum (กฎหมายกำหนด ไม่ใช่การอนุมาน)
        if (fromTax.HasValue)
            return new ContactTypeVerdict(fromTax.Value, ContactTypeEvidence.TaxIdShape,
                fromTax.Value == ContactType.JuristicPerson
                    ? "เลขประจำตัวผู้เสียภาษีขึ้นต้นด้วย 0 = ทะเบียนนิติบุคคล"
                    : "เลขประจำตัวผู้เสียภาษีขึ้นต้นด้วย 1–8 = บุคคลธรรมดา/ต่างด้าว");

        // ชั้น 2 — ทะเบียนราชการยืนยัน
        if (registryJuristic)
            return new ContactTypeVerdict(ContactType.JuristicPerson, ContactTypeEvidence.Registry,
                "ทะเบียนกรมพัฒนาธุรกิจการค้า/กรมสรรพากร ยืนยันว่าเป็นนิติบุคคล");

        // ชั้น 1 — คำในชื่อ (เชิงบวกด้านเดียว)
        if (NameLooksJuristic(name))
            return new ContactTypeVerdict(ContactType.JuristicPerson, ContactTypeEvidence.NamePrefix,
                "ชื่อมีคำบ่งชี้นิติบุคคล (บริษัท/ห้างหุ้นส่วน/Co.,Ltd.) — โปรดยืนยัน");

        // ไม่มีหลักฐานเลย — "ไม่รู้" ต้องเห็นได้ ห้ามเดาเป็นบุคคลธรรมดา (G3)
        return new ContactTypeVerdict(ContactType.Unknown, ContactTypeEvidence.None,
            "ยังไม่มีหลักฐานพอจะระบุชนิดผู้ติดต่อ (ไม่มีการเลือก · ไม่มีเลขผู้เสียภาษีที่ใช้ได้ · ชื่อไม่บ่งชี้)");
    }

    /// <summary>
    /// ชนิดผู้ติดต่อ **+ รหัสสาขา** จากตัวเดียวกัน — §86/4(2) ผูกสองค่านี้ไว้ด้วยกัน
    ///
    /// <para><b>ทำไมรหัสสาขาเป็น "สัญญาณ" ไม่ใช่ "หลักฐาน"</b>: สำเนาเดิม
    /// (<c>DocumentService.InferContactType</c>) ตัดสินว่า <c>BranchCode != "00000"</c>
    /// ⇒ นิติบุคคล ซึ่งกลับหัวกลับหาง — <c>"00000"</c> เป็นค่าที่ตัวเติมฟอร์ม/OCR
    /// ใส่ให้เองเป็น default ⇒ บุคคลธรรมดาที่ถูกเติม <c>"00000"</c> ให้โดยอัตโนมัติ
    /// กลายเป็น "นิติบุคคล" ทันทีถ้ามีเลขภาษีครบ 13 หลัก (สาขาที่ 3 ของสำเนานั้น)
    /// ⇒ ที่นี่รหัสสาขา<b>ไม่ยกระดับ</b>ชนิด ใช้เฉพาะเมื่อเป็นสาขาย่อยจริง
    /// (ไม่ใช่ศูนย์ล้วน) ซึ่งมีแต่นิติบุคคล/ราชการเท่านั้นที่มีได้</para>
    ///
    /// <para><b>รหัสสาขาที่คืนกลับ</b>: <c>null</c> เมื่อชนิดเป็นบุคคลธรรมดา
    /// (เลขบัตรประชาชนไม่มีสาขา — การพิมพ์ "(สำนักงานใหญ่)" ต่อท้ายเลขบัตรคือการ
    /// พิมพ์ข้อความเท็จลงเอกสารภาษี) · <c>null</c> เมื่อยังไม่รู้ชนิด (ห้ามเติม
    /// <c>"00000"</c> ให้ของที่ยังตัดสินไม่ได้) · ค่าที่ normalize แล้วเมื่อเป็น
    /// นิติบุคคล/ราชการ</para>
    /// </summary>
    public static ContactIdentityVerdict ResolveWithBranch(
        string? declared, string? taxId, string? branchCode, string? name,
        bool registryJuristic = false)
    {
        var branch = NormalizeBranchCode(branchCode);
        var hasRealBranch = branch != null && branch != HeadOfficeBranchCode;

        var v = Resolve(declared, taxId, name, registryJuristic);

        // สาขาย่อยจริง = หลักฐานว่า "มีสาขา" ซึ่งบุคคลธรรมดาทั่วไปไม่มี —
        // ยกระดับได้เฉพาะตอนที่ยัง **ไม่รู้** เท่านั้น ห้ามใช้ล้มชั้นที่สูงกว่า
        if (!v.IsResolved && hasRealBranch)
            v = new ContactTypeVerdict(ContactType.JuristicPerson, ContactTypeEvidence.NamePrefix,
                $"มีรหัสสาขาย่อย {branch} (§86/4(2) สาขาเป็นเรื่องของนิติบุคคล/ราชการ) — โปรดยืนยัน");

        var outBranch = BranchCodeFor(v.Type, branch);

        var reason = v.Reason;
        if (branch != null && outBranch == null)
            reason += $" · ไม่เก็บรหัสสาขา \"{branch}\" — {(v.Type == ContactType.Individual ? "บุคคลธรรมดาไม่มีสำนักงานใหญ่/สาขาตาม §86/4(2)" : "ยังไม่รู้ชนิดผู้ติดต่อ")}";

        return new ContactIdentityVerdict(v.Type, v.Evidence, reason, outBranch);
    }

    /// <summary>
    /// รหัสสาขาที่ "ควรเก็บ" สำหรับชนิดนี้ — <b>ตัวตัดสินตัวเดียว</b> ของทุกทางเข้า
    ///
    /// <list type="bullet">
    /// <item><b>นิติบุคคล/ราชการ</b> → เก็บตามที่ได้มา (รวม <c>"00000"</c> = สำนักงานใหญ่)</item>
    /// <item><b>บุคคลธรรมดา</b> → เก็บเฉพาะ<b>สาขาย่อยจริง</b> (ไม่ใช่ศูนย์ล้วน).
    ///   บุคคลจด VAT ที่มีสาขาย่อยจริงมีอยู่ ⇒ ห้ามลบทิ้ง · แต่ <c>"00000"</c> ที่
    ///   ตัวเติมฟอร์ม/OCR ใส่ให้เองต้องไม่ถูกเก็บ มิฉะนั้นเลขบัตรประชาชนจะถูกพิมพ์
    ///   ต่อท้ายด้วย "(สำนักงานใหญ่)" บนใบกำกับ = ข้อความเท็จบนเอกสารภาษี</item>
    /// <item><b>ยังไม่รู้ชนิด</b> → ไม่เก็บ (ห้ามแต่งค่าให้ของที่ยังตัดสินไม่ได้)</item>
    /// </list>
    /// </summary>
    public static string? BranchCodeFor(ContactType type, string? branchCode)
    {
        var branch = NormalizeBranchCode(branchCode);
        if (branch == null) return null;
        if (HasBranchStructure(type)) return branch;
        if (type == ContactType.Individual && branch != HeadOfficeBranchCode) return branch;
        return null;
    }

    /// <summary>รหัสสาขา 5 หลักตามประกาศอธิบดีฯ 199 — คืน <c>null</c> เมื่อว่าง/ผิดรูป
    /// (ผิดรูป = ไม่รู้ ⇒ ห้ามแต่งเป็น "00000" ให้)</summary>
    public static string? NormalizeBranchCode(string? branchCode)
    {
        var t = (branchCode ?? "").Trim();
        if (t.Length == 0) return null;
        var digits = new string(t.Where(char.IsDigit).ToArray());
        if (digits.Length == 0 || digits.Length > 5) return null;
        return digits.PadLeft(5, '0');
    }

    // ── การเขียนลงแถวที่มีอยู่แล้ว ────────────────────────────────────────────

    /// <summary>
    /// ค่าที่ควรเขียนลงแถว**เดิม** — ลำดับเดียวกับ <see cref="Resolve"/> แต่มี
    /// "ค่าที่เก็บไว้แล้ว" เป็นผู้เล่นเพิ่มอีกหนึ่งชั้น
    ///
    /// <list type="number">
    /// <item><b>ต้นทางประกาศมา</b> ⇒ ใช้ค่านั้น (เจ้าของทะเบียนคือระบบลูกค้า/ผู้ใช้)</item>
    /// <item><b>เลขผู้เสียภาษีผ่าน checksum</b> ⇒ ใช้รูปเลข — นี่คือจุดที่<b>ซ่อม</b>
    ///   แถวที่เคยถูกประทับผิดโดยกติกาเก่า (<c>Length == 13 ⇒ นิติบุคคล</c>)
    ///   <b>ยกเว้น</b> แถวที่เป็น <see cref="ContactType.GovernmentAgency"/> อยู่แล้ว
    ///   และรูปเลขบอกว่า "นิติบุคคล" — ราชการกับบริษัทใช้เลขขึ้นต้น 0 เหมือนกัน
    ///   รูปเลขจึงแยกไม่ได้ ⇒ ห้ามลดระดับราชการเป็นบริษัท</item>
    /// <item><b>ไม่มีรูปเลข แต่แถวเดิมรู้อยู่แล้ว</b> ⇒ คงค่าเดิม — ห้ามให้หลักฐาน
    ///   อ่อน (ทะเบียน/ชื่อ) ไปทับสิ่งที่คนเคยตั้งใจตั้ง</item>
    /// <item><b>แถวเดิมเป็น <see cref="ContactType.Unknown"/></b> ⇒ เติมด้วยผลอนุมาน
    ///   (ซึ่งอาจยังเป็น <c>Unknown</c> ต่อไปได้ — และนั่นถูกแล้ว)</item>
    /// </list>
    /// </summary>
    public static ContactTypeVerdict ApplyToExisting(
        ContactType current, string? declared, string? taxId, string? name,
        bool registryJuristic = false)
    {
        var v = Resolve(declared, taxId, name, registryJuristic);
        if (v.Evidence == ContactTypeEvidence.Declared) return v;

        if (v.Evidence == ContactTypeEvidence.TaxIdShape)
        {
            if (current == ContactType.GovernmentAgency && v.Type == ContactType.JuristicPerson)
                return new ContactTypeVerdict(current, ContactTypeEvidence.Declared,
                    "คงค่าเดิม \"หน่วยงานราชการ\" — เลขขึ้นต้น 0 ใช้ทั้งราชการและบริษัท จึงแยกจากรูปเลขไม่ได้");
            return v;
        }

        if (current != ContactType.Unknown)
            return new ContactTypeVerdict(current, ContactTypeEvidence.Declared,
                $"คงค่าเดิมที่บันทึกไว้ ({ThaiLabel(current)}) — รอบนี้ไม่มีหลักฐานใหม่ที่หนักกว่า");

        return v;
    }

    /// <summary>ป้ายไทยของชนิดผู้ติดต่อ — <b>ตัวแปลงตัวเดียว</b> ห้ามพิมพ์ซ้ำใน JS/renderer</summary>
    public static string ThaiLabel(ContactType t) => t switch
    {
        ContactType.Individual => "บุคคลธรรมดา",
        ContactType.JuristicPerson => "นิติบุคคล",
        ContactType.GovernmentAgency => "หน่วยงานราชการ",
        _ => "ยังไม่ระบุ",
    };

    /// <summary>ผู้ติดต่อรายนี้ "มีสาขา" ตามความหมาย §86/4(2) ไหม —
    /// ใช้แทนการเขียน <c>!= ContactType.Individual</c> เอง ซึ่งทำให้
    /// <see cref="ContactType.Unknown"/> ตกไปอยู่ฝั่งนิติบุคคลโดยไม่ตั้งใจ
    ///
    /// <para>⚠️ ยัง <c>private</c> เพราะผู้เรียกที่ควรใช้จริง (renderer สองตัวใน
    /// <c>PdfGenerationService</c> / <c>.DocumentRenderer</c> ที่ยังเขียน
    /// <c>!= ContactType.Individual</c> เอง) อยู่นอกขอบเขตของทีมนี้ —
    /// <b>เปิดเป็น <c>public</c> พร้อมกับการต่อสาย renderer ทั้งสองตัวในคอมมิต
    /// เดียวกัน</b> (สเปกอยู่ในรายงานรอบนี้) · "มี ≠ ถูกเรียก" — CLAUDE.md F2 ข้อ 2</para></summary>
    /// <summary>ชนิดนี้มี "โครงสร้างสาขา" ตาม §86/4(2) ไหม — บุคคลธรรมดาไม่มี
    /// และ <see cref="ContactType.Unknown"/> **ก็ยังไม่รู้ว่ามี** ⇒ ห้ามพิมพ์
    /// "(สำนักงานใหญ่)" ต่อท้ายเลขผู้เสียภาษีของคู่ค้าที่ยังไม่ได้ระบุชนิด
    /// <para>public เพราะ renderer ทั้งสองตัว (HTML + QuestPDF) ต้องใช้กติกาเดียวกัน —
    /// กฎเหล็ก #4 A ข้อแรก "สองเรนเดอเรอร์ห้าม drift"</para></summary>
    public static bool HasBranchStructure(ContactType t)
        => t is ContactType.JuristicPerson or ContactType.GovernmentAgency;
}
