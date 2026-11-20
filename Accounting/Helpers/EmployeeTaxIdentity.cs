namespace Accounting.Helpers;

/// <summary>
/// "เลขประจำตัวผู้เสียภาษีของพนักงาน" ที่ใช้ออก 50 ทวิ ภ.ง.ด.1 · ไฟล์ ภ.ง.ด.1/1ก/91 ·
/// ใบสำคัญจ่ายเบิกค่าใช้จ่าย/เงินทดรอง — **ตัวตัดสินตัวเดียวของระบบ**
///
/// ═══ ที่มา (D-01 · P0 รอบ 189 · แก้รอบ 193) ═══
/// <c>Employee.TaxId</c> ไม่มีจุดเขียนเลยทั้งเรพ (ไม่มีใน Create/Update/Response DTO
/// แม้ฟอร์มจะมีช่อง <c>fTaxId</c>) แต่ <c>IssueMonthlyPnd1CertsAsync</c> ใช้มัน**เดี่ยว ๆ**
/// เป็นด่านออก 50 ทวิ ⇒ ทุกบริษัทที่เพิ่มพนักงานจากหน้าเว็บ: กดจ่ายเงินเดือน →
/// "⚠️ ออกหนังสือรับรอง 50 ทวิ ไม่ครบ … (ยังไม่ได้กรอกเลขประจำตัวผู้เสียภาษี)" ทุกงวด
/// และปุ่ม "นำส่ง ภ.ง.ด.1" ถูกบล็อก <c>WHT-CERT-UNISSUED</c> ตลอดกาล — ข้อความชี้ไปยัง
/// ช่องที่บันทึกไม่ได้ = ด่านที่ปิดตาย
///
/// ขณะเดียวกันจุดอื่นอีก 6 จุดเขียน fallback เอง**สองทิศ**: ไฟล์ยื่น ภ.ง.ด.1/1ก/91 ใช้
/// <c>CitizenId ?? TaxId</c> · ใบเบิก/เงินทดรองใช้ <c>TaxId ?? CitizenId</c> — สำเนามือ
/// ที่เรียงคนละทาง (กฎเหล็ก #4 A "Resolver กลาง ห้าม inline ??")
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item><c>TaxId</c> ที่ HR กรอกเอง — ชัดเจนที่สุด (ผู้มีเงินได้ต่างด้าวที่ได้เลข
///   ผู้เสียภาษีจากกรมสรรพากรจะไม่มีเลขบัตรประชาชนไทย)</item>
/// <item>ไม่มี → <c>CitizenId</c> — บุคคลธรรมดาสัญชาติไทย เลขประจำตัวผู้เสียภาษี =
///   เลขประจำตัวประชาชน 13 หลัก (ประกาศอธิบดีกรมสรรพากร เรื่องการใช้เลขประจำตัว
///   ประชาชนแทนเลขประจำตัวผู้เสียภาษีอากร)</item>
/// <item>ไม่มีทั้งคู่ → <c>null</c> ("ไม่รู้" ต้องเป็นค่าของมันเอง — ผู้เรียกข้ามแถว
///   **พร้อมเตือนดัง** ไม่ใช่แต่งเลข)</item>
/// </list>
/// คืนเฉพาะตัวเลข (ตัดขีด/ช่องว่าง) — ตรงกับรูป <c>Contact.TaxId</c> ที่ใช้จับคู่ใบ 50 ทวิ
///
/// ═══ รอบ 274 — ตรวจก่อนคืน (บั๊ก production) ═══
/// ผู้ใช้ตั้ง <c>ENCRYPTION_KEY</c> หลังมีพนักงานแล้ว ⇒ <c>EncryptedColumnConverter</c> ถอด <c>CitizenId</c>/<c>TaxId</c> ที่เข้ารหัสด้วย dev key ไม่ออก
/// ⇒ คืน ciphertext <c>enc1:…</c> ทั้งก้อน ⇒ ตัวนี้เดิม "เหลือเฉพาะตัวเลข" ⇒ ได้ตัวเลขที่ปนอยู่ในข้อความ base64 (5/7/8/10/14 หลัก · บางแถว 13 หลักแบบสุ่ม)
/// ⇒ ลงช่อง 5 ของไฟล์ ภ.ง.ด.1/1ก และกล่อง 13 หลักของ 50 ทวิ · ตอนนี้ <see cref="Inspect"/> คืนค่า<b>เฉพาะเลข 13 หลักที่ผ่าน mod-11</b> ·
/// ค่า enc1:… ⇒ <see cref="TaxIdProblem.Undecryptable"/> · ผิดรูป/หลักตรวจไม่ผ่าน ⇒ <see cref="TaxIdProblem.Invalid"/> · ผู้เรียกต้อง<b>กันแถวนั้นพร้อมเหตุ</b> ไม่ใช่เขียนเลขผิดลงแบบ
/// (ด่านล็อกจุดเรียกด้วย <c>tools/required_call_site_check.py</c>)
/// </summary>
public static class EmployeeTaxIdentity
{
    /// <summary>ข้อความกลางเมื่อเลขใช้ไม่ได้ — ตรงกับที่ผู้ใช้เห็นบนสรุปไฟล์/คำเตือน 50 ทวิ</summary>
    public const string UndecryptableText = "เลขประจำตัวถอดรหัสไม่ได้";
    public const string InvalidText = "เลขประจำตัวไม่ถูกต้อง";

    /// <summary>ผลตรวจ · <see cref="Value"/> มีค่าเฉพาะเมื่อ <see cref="Problem"/> = None · <see cref="Masked"/> ปิดบังเสมอ (ใช้ในข้อความ/log ได้)</summary>
    public sealed record Result(string? Value, TaxIdProblem Problem, string? Masked)
    {
        public bool Ok => Problem == TaxIdProblem.None;

        /// <summary>เหตุเป็นภาษาไทย (null เมื่อใช้ได้) — บอกทางไปต่อของผู้ใช้</summary>
        public string? Reason => Problem switch
        {
            // รอบ 275 แพ็ก A (T4 M-1): ผู้ใช้ของบริษัทตั้ง env ของแพลตฟอร์มไม่ได้ ⇒ ทางไปต่อของผู้ใช้ + ชี้หน้าจอ (ชื่อ env อยู่ใน log/หน้าผู้ดูแลเท่านั้น)
            TaxIdProblem.Undecryptable => UndecryptableText + " (ข้อมูลเข้ารหัสด้วยคีย์อื่น — กรอกเลขบัตรประชาชนใหม่ที่ทะเบียนพนักงาน "
                + "หรือแจ้งผู้ดูแลระบบ · ดู " + EncryptionHealthText.PageName + ")",
            TaxIdProblem.Invalid => InvalidText + $" ({Masked} — ต้องเป็นเลข 13 หลักที่หลักสุดท้ายถูกต้อง · แก้ที่ทะเบียนพนักงาน/ผู้ติดต่อ)",
            TaxIdProblem.Missing => "ยังไม่ได้กรอกเลขบัตรประชาชนหรือเลขประจำตัวผู้เสียภาษี",
            _ => null,
        };
    }

    /// <summary>TaxId ที่กรอกเองก่อน → เลขบัตร → ไม่รู้ · ช่องแรกที่มีค่าเป็นตัวตัดสิน (ไม่ข้ามไปช่องถัดไปเมื่อช่องแรกเสีย — เลขที่ HR กรอกเองผิด
    /// ต้องถูกแก้ ไม่ใช่ถูกแทนเงียบ ๆ)</summary>
    public static Result Inspect(string? taxId, string? citizenId)
        => HasContent(taxId) ? InspectValue(taxId) : InspectValue(citizenId);

    /// <summary>มีอะไรให้ตรวจ — ciphertext หรือมีตัวเลขอย่างน้อยหนึ่งตัว (ช่องที่มีแต่ "-" = ว่าง เหมือนเดิม)</summary>
    private static bool HasContent(string? v)
        => !string.IsNullOrWhiteSpace(v) && (EncryptedColumnConverter.IsUnreadable(v) || ThaiTaxId.Normalize(v).Length > 0);

    /// <summary>ตรวจค่าเดี่ยว (เช่น <c>Contact.TaxId</c> ของผู้รับใน 50 ทวิ) ด้วยกติกาเดียวกัน</summary>
    public static Result InspectValue(string? raw)
    {
        if (!HasContent(raw)) return new(null, TaxIdProblem.Missing, null);
        if (EncryptedColumnConverter.IsUnreadable(raw)) return new(null, TaxIdProblem.Undecryptable, "(ถอดรหัสไม่ได้)");   // enc1 หรือรุ่นก่อน (MED-1)
        var d = ThaiTaxId.Normalize(raw);
        return ThaiTaxId.IsValid(d) ? new(d, TaxIdProblem.None, PiiMask.CitizenId(d))
            : new(null, TaxIdProblem.Invalid, MaskInvalid(d));
    }

    /// <summary>ค่าที่แสดงบนจอ — ใช้ได้ ⇒ เลขเต็ม (ผู้มีสิทธิ์ <c>Pii.View</c>) หรือเลขปิดบัง · ใช้ไม่ได้ ⇒ ป้ายเหตุ (ไม่เผย ciphertext/ตัวเลขที่ผิด) · ว่าง ⇒ null</summary>
    public static string? Display(Result r, bool includePii) => r.Problem switch
    {
        TaxIdProblem.None => includePii ? r.Value : r.Masked,
        TaxIdProblem.Undecryptable => "⛔ " + UndecryptableText,
        TaxIdProblem.Invalid => $"⛔ {InvalidText} ({r.Masked})",
        _ => null,
    };

    // ═══ รอบ 275 แพ็ก E (T4 H-3 · คำตัดสิน 4.2) — ผู้ไม่มีเลข 13 หลักที่ผู้ทำบัญชีรับทราบรายคน ═══
    // เดิมไฟล์ ภ.ง.ด.1 "ยอมเลขว่าง" (เติม 0) แต่ 50 ทวิ ไม่ออก ⇒ ด่านนำส่ง WHT-CERT-UNISSUED บล็อกทั้งบริษัท และไม่มีทางรับทราบ — สามทางตัดสินคนละแบบ ·
    // ตอนนี้ 50 ทวิ รายเดือน/รายปี · ไฟล์ ภ.ง.ด.1/1ก · ด่านนำส่ง (ผ่านใบที่ออก) ใช้ตัวนี้ตัวเดียว

    public const string NeedsAcknowledgementText = "ยังไม่มีเลขบัตรประชาชน/เลขประจำตัวผู้เสียภาษี 13 หลัก และยังไม่ได้รับทราบ — "
        + "กรอกเลขที่ทะเบียนพนักงาน/ผู้ติดต่อ หรือ (ผู้ที่ไม่มีเลข 13 หลักจริง เช่น ต่างชาติ) กด “รับทราบไม่มีเลข 13 หลัก” ที่ " + EncryptionHealthText.PageName;

    /// <summary>ผลตัดสินสำหรับ "ยื่นแบบ/ออก 50 ทวิ" ของพนักงานหนึ่งคน</summary>
    public enum FilingKind
    {
        /// <summary>เลข 13 หลักที่ถูกต้อง</summary>
        Ok,
        /// <summary>ไม่มีเลขเลย + ผู้ทำบัญชีรับทราบแล้ว ⇒ 50 ทวิ ออกได้ (กล่องเลขว่าง + เลขพาสปอร์ตประกอบ) · ไฟล์ ภ.ง.ด.1 เติม 0 ตามสเปก</summary>
        WaivedNoId,
        /// <summary>ไม่มีเลขเลย + ยังไม่รับทราบ ⇒ บล็อก (เมื่อมีภาษีหัก) เหมือนเดิม</summary>
        NeedsAcknowledgement,
        /// <summary>ถอดรหัสไม่ได้ / ผิดรูป / checksum ไม่ผ่าน ⇒ บล็อกเสมอ · <b>การรับทราบไม่มีผล</b> (ต้องแก้เลข)</summary>
        Unusable,
    }

    public sealed record FilingIdentity(FilingKind Kind, Result Inspected)
    {
        /// <summary>เลข 13 หลักที่ลงแบบได้ (null เมื่อไม่ใช่ <see cref="FilingKind.Ok"/>)</summary>
        public string? Value => Kind == FilingKind.Ok ? Inspected.Value : null;
        /// <summary>ออก 50 ทวิ ได้ไหม (Ok หรือรับทราบแล้ว)</summary>
        public bool CanIssueCert => Kind is FilingKind.Ok or FilingKind.WaivedNoId;
        /// <summary>บล็อกแถวที่มีภาษีหักไหม — ผู้เรียกที่ไม่มีภาษีหัก (ภาษี 0) ใช้ <see cref="FilingKind.Unusable"/> อย่างเดียว</summary>
        public bool BlocksWhenTaxed => Kind is FilingKind.NeedsAcknowledgement or FilingKind.Unusable;
        /// <summary>เหตุ + ทางไปต่อ (null เมื่อไม่บล็อก)</summary>
        public string? Reason => Kind switch
        {
            FilingKind.NeedsAcknowledgement => NeedsAcknowledgementText,
            FilingKind.Unusable => Inspected.Reason,
            _ => null,
        };
    }

    /// <summary><b>ตัวตัดสินเดียว</b> ของ 50 ทวิ ภ.ง.ด.1 (รายเดือน/รายปี) · ไฟล์ ภ.ง.ด.1/1ก — การรับทราบมีผล<b>เฉพาะเมื่อไม่มีเลขเลย</b> (คำตัดสิน 4.2:
    /// เลข checksum ผิดไม่ได้รับสิทธิ์นี้) · มีเลขที่ถูกต้องแล้ว ⇒ Ok (การรับทราบเดิมไม่มีผล)</summary>
    public static FilingIdentity ForFiling(string? taxId, string? citizenId, DateTime? waiverAcknowledgedAt)
    {
        var r = Inspect(taxId, citizenId);
        var kind = r.Problem switch
        {
            TaxIdProblem.None => FilingKind.Ok,
            TaxIdProblem.Missing => waiverAcknowledgedAt != null ? FilingKind.WaivedNoId : FilingKind.NeedsAcknowledgement,
            _ => FilingKind.Unusable,
        };
        return new FilingIdentity(kind, r);
    }

    public static FilingIdentity ForFiling(Accounting.Models.Entities.Employee e)
        => ForFiling(e.TaxId, e.CitizenId, e.TaxIdWaiverAcknowledgedAt);

    /// <summary>ฝ่ายค้าน MED-2: ผู้รับใน ภ.ง.ด.1 ที่ไม่ใช่พนักงาน (ใบ 50 ทวิ ที่ออกด้วยมือ) — กติกาเดียวกัน · ไม่มีทางลัดผ่านประเทศของผู้ติดต่อ
    /// (ไม่มีเลขไทย ⇒ ต้องรับทราบรายคนเสมอ · เลขต่างประเทศในช่องเลข = เลขไทยที่ผิด ⇒ ล้างช่อง/แก้เลขก่อน)</summary>
    public static FilingIdentity ForFiling(Accounting.Models.Entities.Contact c)
        => ForFiling(c.TaxId, null, c.TaxIdWaiverAcknowledgedAt);

    /// <summary>รับทราบได้ไหม — null = ได้ · ข้อความ = เหตุที่ปฏิเสธ (มีเลขแล้ว/เลขผิดต้องแก้/ถอดรหัสไม่ได้)</summary>
    public static string? WaiverRefusal(string? taxId, string? citizenId)
    {
        var r = Inspect(taxId, citizenId);
        return r.Problem switch
        {
            TaxIdProblem.Missing => null,
            TaxIdProblem.None => "พนักงานคนนี้มีเลขประจำตัว 13 หลักที่ถูกต้องแล้ว — ไม่ต้องรับทราบ",
            TaxIdProblem.Invalid => "รับทราบไม่ได้: " + r.Reason + " · เลขที่ checksum ไม่ผ่านต้องแก้ให้ถูก (ถ้าเป็นเลขพาสปอร์ต ให้ย้ายไปช่องเลขหนังสือเดินทางแล้วล้างช่องเลขบัตร)",
            _ => "รับทราบไม่ได้: " + r.Reason,
        };
    }

    /// <summary>ข้อความประกอบบน 50 ทวิ ของผู้ไม่มีเลข 13 หลักที่รับทราบแล้ว (เลขพาสปอร์ตเป็นข้อมูลประกอบ — คำตัดสิน 4.2) · null = ไม่แสดง ·
    /// ตัวเดียวของ HTML + QuestPDF + หน้าพิมพ์ (ผ่าน DTO) · พาสปอร์ตที่ถอดรหัสไม่ได้ ⇒ ไม่พิมพ์ตัวเลข (ไม่เผย ciphertext)</summary>
    public static string? WaiverCertNote(bool waived, string? passportNumber)
    {
        if (!waived) return null;
        var p = (passportNumber ?? "").Trim();
        var usable = p.Length > 0 && !EncryptedColumnConverter.IsUnreadable(p);
        return "ผู้มีเงินได้ไม่มีเลขประจำตัวผู้เสียภาษี 13 หลัก" + (usable ? $" · เลขหนังสือเดินทาง {p}" : "");
    }

    /// <summary>เลขที่ใช้ได้ (13 หลัก + checksum) หรือ null — ผู้เรียกที่ต้องรู้เหตุใช้ <see cref="Inspect"/></summary>
    public static string? Resolve(string? taxId, string? citizenId) => Inspect(taxId, citizenId).Value;

    /// <summary>ปิดบังเลขผิดรูป — บอกจำนวนหลักให้ผู้ใช้รู้ว่าผิดตรงไหน โดยไม่เผยตัวเลข</summary>
    private static string MaskInvalid(string digits)
        => digits.Length == 0 ? "(ไม่มีตัวเลข)" : $"{digits.Length} หลัก";
}

/// <summary>เหตุที่ใช้เลขประจำตัวไม่ได้ (<see cref="EmployeeTaxIdentity.Inspect"/>)</summary>
public enum TaxIdProblem
{
    None,
    /// <summary>ไม่มีค่า — ผู้เรียกแต่ละรายตัดสินเอง (ไฟล์ ภ.ง.ด.1 เติม 0 ตามสเปก · 50 ทวิ ข้ามพร้อมเตือน)</summary>
    Missing,
    /// <summary>ค่าเป็น ciphertext <c>enc1:</c> ที่ถอดไม่ออก (คีย์ไม่ตรง)</summary>
    Undecryptable,
    /// <summary>ไม่ใช่ 13 หลัก หรือ checksum ไม่ผ่าน</summary>
    Invalid,
}
