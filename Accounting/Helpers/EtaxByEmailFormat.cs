using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ขั้นของใบหนึ่งใบในระบบ e-Tax by Email — ระบบรู้แค่ "ส่งพร้อมสำเนาถึง ETDA แล้ว" จากผลของผู้ให้บริการอีเมล ·
/// "ประทับเวลาแล้ว" ตั้งได้เฉพาะเมื่อผู้ใช้บันทึกใบที่ ETDA ส่งกลับ (CLAUDE.md F2 ข้อ 3 — ห้ามประทับสถานะปลายทางเอง)</summary>
public enum EtaxByEmailStage
{
    /// <summary>ยังไม่เคยส่งพร้อมสำเนาถึงที่อยู่ประทับเวลาของ ETDA (production)</summary>
    NotSent = 0,
    /// <summary>ส่งพร้อมสำเนาถึง ETDA แล้ว — ยังไม่มีหลักฐานว่า ETDA ประทับเวลา</summary>
    SentAwaitingStamp = 1,
    /// <summary>ผู้ใช้บันทึกใบ/อีเมลที่ ETDA ประทับเวลาแล้วส่งกลับ (พร้อมไฟล์หลักฐาน)</summary>
    Stamped = 2,
}

/// <summary>
/// **รูปแบบ e-Tax Invoice &amp; e-Receipt by Email — ตัวตั้งตัวเดียว** (รอบ 244 · erp-review/2026-11-06/ETAX_BY_EMAIL_AUDIT.md E1/E2/E10/E11/E13)
///
/// <para>═══ ที่มาของกติกา (ค้นผ่านผลค้นหา — เปิดเว็บราชการตรงจาก session ไม่ได้ · ต้องให้คนยืนยันกับคู่มือฉบับปัจจุบัน) ═══</para>
/// <para>R4 ส่งจากอีเมลที่ลงทะเบียน · To = ผู้ซื้อ <b>1 อีเมล</b> · CC = <c>csemail@etax.teda.th</c> (คู่มือ RD manual-etax-invoiceby-email-110161.pdf) — ความมั่นใจสูง</para>
/// <para>R5 1 อีเมล = ใบกำกับ 1 ฉบับ = ไฟล์แนบ 1 ไฟล์ (PDF/A-3 ที่ฝัง XML) · ไม่เกิน 3 MB · ห้ามเข้ารหัส — ความมั่นใจสูง</para>
/// <para>R6 หัวเรื่อง = วงเล็บเหลี่ยมติดกันไม่เว้นวรรค · วันที่ <c>ddMMyyyy</c> เป็น <b>พ.ศ.</b> · ใบกำกับ <c>[18012560][INV][เลขที่]</c> ·
/// ใบเพิ่มหนี้ <c>[วันที่][DBN][เลขใบเพิ่มหนี้][เลขใบเดิม]</c> · ใบลดหนี้ <c>[วันที่][CDN][เลขใบลดหนี้][เลขใบเดิม]</c> ·
/// ใบแทน <c>[วันที่][INV][เลขใหม่][เลขใบเดิมที่ยกเลิก]</c> (คู่มือ RD · RD seminar 180820/190819/190820 · getinvoice · peakaccount)</para>
/// <para>R8 สภาพแวดล้อมทดสอบ = <c>csemail@uat.etax.teda.th</c> (etax.teda.th eTax_Email_instruction_to_register_email_atTest_env_v1.0.pdf) — ความมั่นใจกลาง</para>
///
/// <para>═══ ตารางรหัสชนิดเอกสาร (ความมั่นใจรายแถว) ═══ — ห้ามมีสำเนาที่สองในโค้ด/หน้าเว็บ (หน้าเว็บแสดงหัวเรื่องที่เซิร์ฟเวอร์คำนวณเท่านั้น)</para>
/// <list type="bullet">
/// <item>ETDA <c>388/T02/T04 → INV</c> — สูง (ตัวอย่างในคู่มือ RD)</item>
/// <item><c>80 → DBN</c> — กลาง-สูง (RD seminar + getinvoice)</item>
/// <item><c>81 → CDN</c> — <b>กลาง</b>: แหล่งไม่ตรงกัน (CDN / CRN) · เลือก CDN ตามสไลด์ RD seminar · รอคนยืนยัน (คำตัดสิน 244-2)</item>
/// <item><c>T03 → RCT</c> (ใบเสร็จรับเงิน/ใบกำกับภาษี — ใบเสร็จ และใบกำกับที่ออกเป็นใบเสร็จในตัว) — <b>กลาง-ต่ำ</b>: ยังไม่พบตัวอย่างหัวเรื่อง [RCT] · รอคนยืนยัน</item>
/// <item>ชนิดตัดสินจาก <see cref="EtaxDocumentTypeMap.TypeCode"/> ตัวเดียวกับ XML (ฝ่ายค้าน S3) — ห้ามแยกตาม <c>DocumentType</c> เอง</item>
/// </list>
/// <para>ใบกำกับภาษีอย่างย่อ (§86/6) ไม่อยู่ในระบบนี้ (ระบบยังไม่ออก e-Tax T05/T06) · หนังสือยกเลิก [CIV] ยังไม่รองรับ (รอบนี้ไม่ส่งอีเมลยกเลิก)</para>
/// <para>ข้อความที่ผู้ใช้พิมพ์ <b>ไม่ถูกต่อเข้าหัวเรื่องเลย</b> — ใส่ในเนื้ออีเมลแทน (เนื้ออีเมลไม่ถูกบังคับรูปแบบ) · G6: pure</para>
/// </summary>
public static class EtaxByEmailFormat
{
    /// <summary>ที่อยู่ประทับเวลาของ ETDA (production)</summary>
    public const string ProductionTimestampAddress = "csemail@etax.teda.th";
    /// <summary>ที่อยู่ประทับเวลาของสภาพแวดล้อมทดสอบ ETDA (UAT) — ใช้เมื่อบริษัทติ๊ก "ทดสอบ e-Tax by Email" (<c>EtaxByEmailUseUat</c>)</summary>
    public const string UatTimestampAddress = "csemail@uat.etax.teda.th";
    /// <summary>ขนาดไฟล์แนบสูงสุดต่ออีเมล (R5) — 3 MB</summary>
    public const long MaxAttachmentBytes = 3L * 1024 * 1024;
    /// <summary>เพดานรายได้ของผู้มีสิทธิใช้ e-Tax by Email (R1 — ไม่เกิน 30 ล้านบาท/ปี) · คนละความหมายกับ <c>CitRateTable.SmeAnnualRevenueCeiling</c> (CIT)</summary>
    public const decimal RevenueCeiling = 30_000_000m;
    /// <summary>รหัสกฎของการปฏิเสธเชิงรูปแบบ (ลง audit/ตอบหน้าเว็บ)</summary>
    public const string RuleCode = "ETAX-EMAIL-FORMAT";
    /// <summary>รหัสกฎของ "ส่งซ้ำต้องยืนยัน" (409 — หน้าเว็บถามแล้วส่งใหม่พร้อมยืนยัน)</summary>
    public const string ResendRuleCode = "ETAX-EMAIL-RESEND";

    /// <summary>
    /// รหัสหัวเรื่องจาก<b>รหัสชนิดเอกสาร ETDA ของใบ</b> (<see cref="EtaxDocumentTypeMap.TypeCode"/> — ตัวตัดสินชนิดตัวเดียวกับ XML · ฝ่ายค้าน S3):
    /// <c>388</c> ใบกำกับภาษี · <c>T02</c> ใบแจ้งหนี้/ใบกำกับภาษี · <c>T04</c> ใบส่งของ/ใบกำกับภาษี ⇒ INV · <c>T03</c> ใบเสร็จรับเงิน/ใบกำกับภาษี (รวมใบกำกับที่ออกเป็นใบเสร็จ
    /// ในตัว <c>IssuedAsCashReceipt</c>) ⇒ RCT · <c>80</c> ใบเพิ่มหนี้ ⇒ DBN · <c>81</c> ใบลดหนี้ ⇒ CDN · <c>T05</c>/<c>T06</c> อย่างย่อ ⇒ null (ไม่รองรับ) ·
    /// รหัส CDN/RCT รอคนยืนยัน (คำตัดสิน 244-2)
    /// </summary>
    private static string? SubjectCode(string? etdaTypeCode) => etdaTypeCode switch
    {
        "388" or "T02" or "T04" => "INV",
        "T03" => "RCT",
        "80" => "DBN",
        "81" => "CDN",
        _ => null,
    };

    /// <summary>หัวเรื่องของใบนี้ — ชนิดต้องเป็น e-Tax (<see cref="EtaxAutoIssueScope.IsEtaxType"/>) แล้วรหัสจาก <see cref="EtaxDocumentTypeMap.TypeCode"/> ตัวเดียว</summary>
    public static SubjectResult Subject(Accounting.Models.Entities.Document doc, string? originalNumber = null, string? replacesNumber = null)
        => !EtaxAutoIssueScope.IsEtaxType(doc.DocumentType)
            ? new SubjectResult(null, $"เอกสารชนิด {doc.DocumentType} ไม่ใช่ใบกำกับภาษี/ใบเสร็จ/ใบเพิ่มหนี้/ใบลดหนี้ — ส่งแบบ e-Tax by Email ไม่ได้")
            : Subject(EtaxDocumentTypeMap.TypeCode(doc), doc.DocumentDate, doc.DocumentNumber, originalNumber, replacesNumber);

    /// <summary>วันที่ในหัวเรื่อง <c>ddMMyyyy</c> เป็น พ.ศ. ของวันที่ตามปฏิทินไทย (ตัวแปลงกลาง <see cref="ThaiDate.CalendarDateUtc(DateTime)"/>) ·
    /// ใช้ InvariantCulture เสมอ (กัน th-TH บวก 543 ซ้ำ)</summary>
    public static string BeDate(DateTime issueDate)
    {
        var d = ThaiDate.CalendarDateUtc(issueDate);
        return d.ToString("ddMM", CultureInfo.InvariantCulture) + (d.Year + 543).ToString("0000", CultureInfo.InvariantCulture);
    }

    /// <summary>ผลการประกอบหัวเรื่อง — <see cref="Problem"/> ไม่ว่าง = ส่งไม่ได้ (ข้อความไทยพร้อมทางไปต่อ)</summary>
    public sealed record SubjectResult(string? Subject, string? Problem);

    /// <summary>
    /// **หัวเรื่องตาม R6** — <c>[ddMMyyyy พ.ศ.][รหัส][เลขที่](เลขอ้างอิง)</c>
    /// <para>ใบเพิ่ม/ลดหนี้ ต้องมีเลขใบกำกับเดิม (§86/9-10) · ใบแทน (ออกแทนใบที่ยกเลิก) ต่อเลขใบเดิมที่ยกเลิก · เลขที่ห้ามมีวงเล็บเหลี่ยม/ขึ้นบรรทัด
    /// (ตัวแยกของ ETDA จะอ่านผิด) · เลข DRAFT ห้ามส่ง (§86/4)</para>
    /// </summary>
    /// <param name="originalNumber">เลขใบกำกับเดิม (ใบเพิ่มหนี้/ใบลดหนี้)</param>
    /// <param name="replacesNumber">เลขใบที่ถูกยกเลิกแล้วออกใบนี้แทน (ใบกำกับ/ใบเสร็จ) · null = ไม่ใช่ใบแทน</param>
    /// <param name="etdaTypeCode">รหัสชนิดเอกสาร ETDA (<see cref="EtaxDocumentTypeMap.TypeCode"/>)</param>
    public static SubjectResult Subject(string etdaTypeCode, DateTime issueDate, string? number, string? originalNumber = null,
        string? replacesNumber = null)
    {
        var code = SubjectCode(etdaTypeCode);
        if (code == null)
            return new SubjectResult(null, $"รหัสชนิดเอกสาร ETDA {etdaTypeCode} ยังไม่รองรับหัวเรื่อง e-Tax by Email (เช่น ใบกำกับภาษีอย่างย่อ) — ส่งแบบ e-Tax by Email ไม่ได้");
        var no = Clean(number);
        if (no == null)
            return new SubjectResult(null, "ไม่พบเลขที่เอกสาร — ประกอบหัวเรื่องตามรูปแบบกรมสรรพากรไม่ได้");
        if (no.StartsWith("DRAFT", StringComparison.OrdinalIgnoreCase))
            return new SubjectResult(null, "เอกสารยังเป็นฉบับร่าง (เลข DRAFT) — อนุมัติก่อนส่ง (§86/4)");
        if (HasBadChars(number))
            return new SubjectResult(null, $"เลขที่เอกสาร “{number}” มีวงเล็บเหลี่ยมหรือขึ้นบรรทัด — ETDA อ่านหัวเรื่องผิด · เปลี่ยนรูปแบบเลขที่เอกสารก่อนส่ง");
        var head = $"[{BeDate(issueDate)}][{code}][{no}]";
        if (code is "CDN" or "DBN")
        {
            var orig = Clean(originalNumber);
            if (orig == null)
                return new SubjectResult(null,
                    $"{(code == "CDN" ? "ใบลดหนี้" : "ใบเพิ่มหนี้")} {no} ไม่ได้อ้างเลขใบกำกับภาษีเดิม — หัวเรื่อง e-Tax by Email ต้องมีเลขใบเดิม (§86/9-10) · "
                    + "ผูกใบเดิมที่เอกสารนี้ก่อนส่ง");
            if (HasBadChars(originalNumber))
                return new SubjectResult(null, $"เลขใบเดิม “{originalNumber}” มีวงเล็บเหลี่ยมหรือขึ้นบรรทัด — ETDA อ่านหัวเรื่องผิด");
            return new SubjectResult(head + $"[{orig}]", null);
        }
        var rep = Clean(replacesNumber);
        if (rep != null)
        {
            if (HasBadChars(replacesNumber))
                return new SubjectResult(null, $"เลขใบเดิมที่ยกเลิก “{replacesNumber}” มีวงเล็บเหลี่ยมหรือขึ้นบรรทัด — ETDA อ่านหัวเรื่องผิด");
            return new SubjectResult(head + $"[{rep}]", null);
        }
        return new SubjectResult(head, null);
    }

    /// <summary>ที่อยู่ประทับเวลา — ธงทดสอบ e-Tax by Email (<c>CompanySettings.EtaxByEmailUseUat</c> <b>เท่านั้น</b> · ไม่ใช่ <c>EtaxTestMode</c> ของระบบเต็ม —
    /// ฝ่ายค้าน B1) ⇒ UAT · ไม่งั้น ⇒ production (R8)</summary>
    public static string TimestampAddress(bool useUat) => useUat ? UatTimestampAddress : ProductionTimestampAddress;

    /// <summary>ที่อยู่นี้คือที่อยู่ประทับเวลาของ ETDA (production หรือ UAT) ไหม · ไม่สนตัวพิมพ์</summary>
    private static bool IsTimestampAddress(string? address)
        => !string.IsNullOrWhiteSpace(address)
           && (string.Equals(address.Trim(), ProductionTimestampAddress, StringComparison.OrdinalIgnoreCase)
               || string.Equals(address.Trim(), UatTimestampAddress, StringComparison.OrdinalIgnoreCase));

    /// <summary>ผู้รับที่ส่งได้จริง — To 1 อีเมล · CC = ที่อยู่ประทับเวลา 1 อีเมลเท่านั้น (ไม่ซ้ำ)</summary>
    public sealed record Envelope(string? To, IReadOnlyList<string> Cc, string? Problem);

    /// <summary>
    /// **ผู้รับของอีเมล e-Tax by Email (R4)** — To = ผู้ซื้อ 1 อีเมลที่ถูกรูป (ไม่ใช่ที่อยู่ของ ETDA) · CC = ที่อยู่ประทับเวลาตามสภาพแวดล้อม 1 อีเมล ·
    /// CC ที่ผู้ใช้ใส่มาเป็นที่อยู่ประทับเวลาเดิม ⇒ ตัดซ้ำ · CC/BCC อื่น ⇒ ปฏิเสธพร้อมทางไปต่อ (ไม่ตัดทิ้งเงียบ) ·
    /// ไม่ CC ETDA / แนบ XML แยก / ไม่แนบ PDF ⇒ ปฏิเสธ (ทางนั้นคืออีเมลเอกสารธรรมดา)
    /// </summary>
    public static Envelope Recipients(string? to, string? cc, string? bcc, bool includeRdTimestamp, bool includePdf, bool includeXml,
        bool useUat)
    {
        if (!includeRdTimestamp)
            return new Envelope(null, Array.Empty<string>(),
                "e-Tax by Email ต้องส่งสำเนาถึงที่อยู่ประทับเวลาของ ETDA เสมอ — ถ้าไม่ต้องการ ให้ส่งเป็นอีเมลเอกสารธรรมดา (ใบนั้นจะไม่ถูกประทับเวลา)");
        if (!includePdf)
            return new Envelope(null, Array.Empty<string>(), "e-Tax by Email ต้องแนบไฟล์ PDF/A-3 (ฝัง XML) 1 ไฟล์เสมอ");
        if (includeXml)
            return new Envelope(null, Array.Empty<string>(),
                "e-Tax by Email แนบได้ 1 ไฟล์ต่ออีเมล (PDF/A-3 ที่ฝัง XML อยู่แล้ว) — ไม่แนบไฟล์ XML แยก");
        var (validTo, invalidTo) = SignEmailThrottle.ParseRecipients(new[] { to });
        if (invalidTo.Count > 0)
            return new Envelope(null, Array.Empty<string>(), $"อีเมลผู้รับไม่ถูกต้อง: {string.Join(", ", invalidTo.Take(3))}");
        if (validTo.Count != 1)
            return new Envelope(null, Array.Empty<string>(), validTo.Count == 0
                ? "กรุณาระบุอีเมลผู้ซื้อ 1 อีเมล"
                : "e-Tax by Email ส่งถึงผู้ซื้อได้ 1 อีเมลต่อฉบับ (คู่มือกรมสรรพากร) — เลือกอีเมลเดียว ถ้าต้องการให้คนอื่นได้สำเนา ให้ส่งต่ออีเมลที่ ETDA ประทับเวลาแล้วส่งกลับ");
        if (IsTimestampAddress(validTo[0]))
            return new Envelope(null, Array.Empty<string>(), "ผู้รับ (To) ต้องเป็นอีเมลของผู้ซื้อ — ที่อยู่ของ ETDA เป็นสำเนา (CC) ที่ระบบใส่ให้เอง");
        var (validCc, invalidCc) = SignEmailThrottle.ParseRecipients(new[] { cc, bcc });
        if (invalidCc.Count > 0)
            return new Envelope(null, Array.Empty<string>(), $"อีเมลสำเนาไม่ถูกต้อง: {string.Join(", ", invalidCc.Take(3))}");
        var extra = validCc.Where(a => !IsTimestampAddress(a)).ToList();
        if (extra.Count > 0)
            return new Envelope(null, Array.Empty<string>(),
                $"e-Tax by Email ส่งสำเนาได้เฉพาะที่อยู่ประทับเวลาของ ETDA (ระบบใส่ให้เอง) — เอาสำเนา/BCC ออก ({string.Join(", ", extra.Take(3))}) · "
                + "ถ้าต้องการให้คนอื่นได้สำเนา ให้ส่งต่ออีเมลที่ ETDA ประทับเวลาแล้วส่งกลับ");
        if (!string.IsNullOrWhiteSpace(bcc))
            return new Envelope(null, Array.Empty<string>(), "e-Tax by Email ไม่รับ BCC — เอา BCC ออกแล้วส่งใหม่");
        return new Envelope(validTo[0], new[] { TimestampAddress(useUat) }, null);
    }

    /// <summary>ไฟล์แนบตาม R5 — ต้อง 1 ไฟล์ · ไม่เกิน 3 MB · null = ผ่าน</summary>
    public static string? AttachmentProblem(int attachmentCount, long bytes)
    {
        if (attachmentCount != 1)
            return $"e-Tax by Email ต้องแนบ 1 ไฟล์ต่ออีเมล (พบ {attachmentCount} ไฟล์)";
        if (bytes <= 0)
            return "ไฟล์ PDF/A-3 ว่างเปล่า — ไม่ส่งอีเมลที่ขาดเอกสารตามกฎหมาย";
        if (bytes > MaxAttachmentBytes)
            return $"ไฟล์ PDF/A-3 ขนาด {bytes / 1024.0 / 1024.0:N2} MB เกินเพดาน 3 MB ของ e-Tax by Email — ลดขนาดไฟล์ (เช่น ลดรูปภาพ/โลโก้ในแม่แบบเอกสาร) แล้วส่งใหม่ · ยังไม่ส่งอีเมล";
        return null;
    }

    /// <summary>ขั้นของใบ — ส่งพร้อมสำเนาถึง ETDA (production) เมื่อ <paramref name="sentAt"/> · ประทับแล้วเมื่อมีบันทึกใบที่ ETDA ส่งกลับ</summary>
    public static EtaxByEmailStage Stage(DateTime? sentAt, DateTime? stampedAt)
        => stampedAt != null ? EtaxByEmailStage.Stamped
            : sentAt != null ? EtaxByEmailStage.SentAwaitingStamp
            : EtaxByEmailStage.NotSent;

    /// <summary>ป้ายของขั้น (หน้าเว็บแสดงอย่างเดียว)</summary>
    public static string StageLabel(EtaxByEmailStage stage) => stage switch
    {
        EtaxByEmailStage.Stamped => "ประทับเวลาแล้ว (ผู้ใช้บันทึกใบที่ ETDA ส่งกลับ)",
        EtaxByEmailStage.SentAwaitingStamp => "ส่งแล้ว (รอประทับเวลา)",
        _ => "ยังไม่ได้ส่งแบบ e-Tax by Email",
    };

    /// <summary>คำเตือนรายได้ (R1 ≤ 30 ล้านบาท/ปี) — รายได้ปีล่าสุดเกินเพดาน ⇒ ข้อความเตือน · ไม่มีข้อมูล/ไม่เกิน ⇒ null (ไม่บล็อก — ระบบไม่รู้รายได้ตามแบบ ภ.พ.30 ครบ)</summary>
    public static string? RevenueWarning(decimal? lastYearRevenue, int? year = null)
        => lastYearRevenue is decimal r && r > RevenueCeiling
            ? $"รายได้ปี{(year is int y ? " " + (y + 543) : "ล่าสุด")}ในบัญชีของระบบ {r:N2} บาท เกิน 30 ล้านบาท — e-Tax by Email ใช้ได้เฉพาะผู้ประกอบการรายได้ไม่เกิน 30 ล้านบาท/ปี "
              + "(ตรวจสิทธิกับกรมสรรพากร · ถ้าเกินต้องใช้ระบบ e-Tax Invoice & e-Receipt แบบเต็ม)"
            : null;

    /// <summary>คำเตือนโหมด "ทั้งสองระบบ" (R2 ใช้ได้ระบบเดียว · คำตัดสิน 244-1) — ค่าที่เก็บไว้ก่อนรอบ 244 ยังส่งทางอีเมลได้แต่ต้องเห็นคำเตือน</summary>
    public static string? ModeWarning(EtaxMode mode)
        => mode == EtaxMode.Both
            ? "บริษัทตั้งค่า e-Tax เป็น “ทั้งสองระบบ” — กรมสรรพากรให้ใช้ได้ระบบเดียว (by Email หรือ e-Tax Invoice & e-Receipt แบบเต็ม) · เลือกให้ตรงกับที่ได้รับอนุมัติที่ ตั้งค่า > e-Tax"
            : null;

    /// <summary>คำเตือนทดสอบ e-Tax by Email (ธง <c>EtaxByEmailUseUat</c>) — สำเนาไปที่ UAT ของ ETDA ⇒ ใบไม่ถูกประทับเวลาจริง · หน้าส่งถามยืนยันเมื่อเปิด</summary>
    public static string? UatWarning(bool useUat)
        => useUat
            ? $"เปิด “ทดสอบ e-Tax by Email (ส่งไป UAT)” — สำเนาถึง {UatTimestampAddress} (สภาพแวดล้อมทดสอบของ ETDA) ใบที่ส่งจะไม่ถูกประทับเวลาจริง · ใช้งานจริงให้ปิดช่องนี้ที่ ตั้งค่า > e-Tax"
            : null;

    private static string? Clean(string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static bool HasBadChars(string? s) => s != null && s.Trim().IndexOfAny(new[] { '[', ']', '\n', '\r' }) >= 0;
}
