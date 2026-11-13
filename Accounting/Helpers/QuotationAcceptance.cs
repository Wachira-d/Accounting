using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **กติกาการยอมรับใบเสนอราคาออนไลน์ — ตัวตั้งเดียว** (รอบ 239 · คำตัดสิน 239-1..239-6 ใน erp-review/2026-09-29/DECISIONS.md)
///
/// <para>ลูกค้าเปิดลิงก์ <c>quotation-accept.html?token=…</c> → ดู/ดาวน์โหลด PDF → พิมพ์ชื่อ (บังคับ) + วาดลายเซ็น (ไม่บังคับ เว้นบริษัทเปิด
/// <c>CompanySettings.RequireSignatureForQuotationAccept</c>) → ยอมรับ · ผู้เขียนเดียว <c>PublicQuotationController.Accept</c></para>
///
/// <para>═══ กติกา ═══
/// (1) ยอมรับซ้ำ = ตอบสถานะเดิม (idempotent) ไม่เขียนทับ — ต้องการให้ลูกค้ายอมรับใหม่ ⇒ ผู้ขายออก Rev ใหม่ (<see cref="ClearOn"/> ล้าง
/// หลักฐานชุดนี้ทั้งชุด · ค่าเดิมอยู่ใน snapshot ของ revision) แล้วส่งลิงก์ใหม่ ·
/// (2) ชื่อบังคับเสมอ · ลายเซ็นบังคับตามค่าตั้งบริษัท · ลายเซ็นที่ส่งมาต้องผ่าน <see cref="SignatureImageValidator"/> (ส่งมาแต่ไม่ผ่าน = ปฏิเสธ
/// ไม่ใช่ทิ้งเงียบแล้วยอมรับแบบไม่เซ็น) ·
/// (3) หลักฐาน ม.9 พ.ร.บ.ว่าด้วยธุรกรรมทางอิเล็กทรอนิกส์: เวลา (UTC) · IP · SHA-256 ของ User-Agent · SHA-256 ของ token · hash เนื้อหา
/// (<see cref="DocumentSignedContent.Hash"/> — สูตรเดียวกับลายเซ็นลูกค้าทุกเส้น) + แถว audit chain ·
/// (4) ลายเซ็นเป็นข้อมูลส่วนบุคคล — เก็บเป็นไฟล์ใต้โฟลเดอร์ <see cref="StorageFolder"/> ของที่เก็บกลาง (<b>ไม่อยู่</b>ใน allow-list ของ
/// <c>/uploads</c> ⇒ ไม่มี URL สาธารณะ) อ่านได้เฉพาะผ่าน token ที่ยังใช้ได้ หรือสิทธิ์อ่านเอกสารของผู้ขาย · อายุเก็บตามเอกสาร ·
/// (5) แปลงใบเสนอราคา → ใบสั่งขาย/ใบแจ้งหนี้/ใบกำกับ: ลายเซ็นยอมรับ<b>ไม่</b>ถูกคัดลอกไปเอกสารลูก (เป็นหลักฐานของข้อเสนอฉบับนั้น ไม่ใช่ของ
/// ใบกำกับภาษี) — เอกสารลูกอ้างใบเสนอราคาผ่าน RelatedDocumentId ตามเดิม</para>
/// </summary>
public static class QuotationAcceptance
{
    public const int NameMax = 200;
    public const int NoteMax = 500;
    /// <summary>โฟลเดอร์ย่อยใต้ <c>FileStorageLocator.AttachmentsRoot</c> — ห้ามเพิ่มเข้า publicUploadPrefixes ของ Program.cs</summary>
    public const string StorageFolder = "quotation-acceptances";

    /// <summary>ข้อความยินยอมก่อนเซ็น (หน้าลูกค้าแสดงข้อความนี้จาก API — ไม่มีสำเนาใน JS)</summary>
    public const string ConsentText =
        "การกด \"ยอมรับใบเสนอราคา\" พร้อมพิมพ์ชื่อ (และลายเซ็น ถ้ามี) ถือเป็นการลงลายมือชื่ออิเล็กทรอนิกส์เพื่อตกลงตามข้อเสนอฉบับนี้ "
        + "(พ.ร.บ.ว่าด้วยธุรกรรมทางอิเล็กทรอนิกส์ พ.ศ. 2544 มาตรา 9) — ระบบบันทึกเวลา ที่อยู่ IP ข้อมูลอุปกรณ์ (แบบแฮช) และลายนิ้วมือของเนื้อหาเอกสาร "
        + "ณ ขณะยอมรับไว้เป็นหลักฐาน ลายเซ็นของท่านจะแสดงบนใบเสนอราคาฉบับนี้และเก็บรักษาตามระยะเวลาเก็บเอกสารของผู้ขาย";

    public enum Outcome { Accept, AlreadyAccepted, NameRequired, SignatureRequired, SignatureInvalid }

    /// <summary>ผลตัดสิน — <see cref="Signature"/> = ลายเซ็นที่ผ่านตัวตรวจแล้ว (null = ยอมรับโดยไม่เซ็น)</summary>
    public sealed record Decision(Outcome Outcome, string Message, SignatureImageValidator.Result? Signature)
    {
        public bool Accept => Outcome == Outcome.Accept;
    }

    /// <summary>ตัวตัดสินเดียวของคำขอยอมรับ (เรียกหลังหาใบจาก token แล้ว)</summary>
    public static Decision DecideAccept(DateTime? acceptedAt, string? name, string? signatureInput, bool requireSignature)
    {
        if (acceptedAt != null) return new(Outcome.AlreadyAccepted, "ใบเสนอราคานี้ถูกยอมรับไปแล้ว", null);
        if (CleanText(name, NameMax) == null) return new(Outcome.NameRequired, "กรุณาระบุชื่อผู้ยอมรับ", null);
        if (string.IsNullOrWhiteSpace(signatureInput))
            return requireSignature
                ? new(Outcome.SignatureRequired, "ผู้ขายกำหนดให้ต้องเซ็นลายมือชื่อก่อนยอมรับใบเสนอราคา — กรุณาวาดลายเซ็นในช่อง", null)
                : new(Outcome.Accept, "ยอมรับใบเสนอราคาเรียบร้อย — ผู้ขายจะติดต่อกลับเพื่อดำเนินการต่อ", null);
        var sig = SignatureImageValidator.Validate(signatureInput);
        if (!sig.Ok) return new(Outcome.SignatureInvalid, sig.Message ?? "ลายเซ็นไม่ถูกต้อง", null);
        return new(Outcome.Accept, "ยอมรับใบเสนอราคาเรียบร้อย — ลายเซ็นถูกประทับลงใบเสนอราคาแล้ว", sig);
    }

    /// <summary>ข้อความที่ผู้ใช้พิมพ์ → ตัดช่องว่างหัวท้าย · ตัดอักขระควบคุม (ขึ้นบรรทัด/แท็บ → ช่องว่าง) · ตัดความยาว · ว่าง = null</summary>
    public static string? CleanText(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var sb = new StringBuilder(s.Length);
        var t0 = s.Trim();
        for (var i = 0; i < t0.Length; i++)
        {
            var ch = t0[i];
            // ฝ่ายค้านรอบ 239 N: surrogate ที่ไม่มีคู่ (ตัดครึ่งอีโมจิ/ข้อมูลเสีย) ⇒ ทิ้ง — PostgreSQL/JSON/PDF รับ UTF-16 ไม่สมบูรณ์ไม่ได้
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < t0.Length && char.IsLowSurrogate(t0[i + 1])) { sb.Append(ch).Append(t0[i + 1]); i++; }
                continue;
            }
            if (char.IsLowSurrogate(ch)) continue;
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        var t = sb.ToString().Trim();
        if (t.Length == 0) return null;
        if (t.Length <= max) return t;
        var cut = char.IsHighSurrogate(t[max - 1]) ? max - 1 : max;   // ไม่ตัดกลางคู่ surrogate
        return t[..cut].TrimEnd();
    }

    /// <summary>ตำแหน่งไฟล์ลายเซ็นบนที่เก็บกลาง — แยกโฟลเดอร์ต่อบริษัท · ชื่อไฟล์มี GUID สุ่ม (ไม่เดาได้จากเลขเอกสาร)</summary>
    public static string SignatureLocation(string attachmentsRoot, Guid companyId, Guid documentId, Guid fileId)
        => Path.Combine(attachmentsRoot, StorageFolder, companyId.ToString("N"), $"{documentId:N}-{fileId:N}.png");

    /// <summary>ตำแหน่งที่เก็บไว้เป็นของบริษัท/เอกสารนี้จริงไหม — กันแถวที่ถูกแก้ให้ชี้ไฟล์ของบริษัทอื่น (อ่านผ่าน token ของอีกบริษัท) ·
    /// ฝ่ายค้านรอบ 239 N: พาธเต็มต้องอยู่ใต้ <c>{attachmentsRoot}/quotation-acceptances/{companyId}/</c> ของที่เก็บนี้จริง (ไม่ใช่แค่มีชื่อโฟลเดอร์อยู่กลางพาธ)</summary>
    public static bool IsOwnSignatureLocation(string? location, string attachmentsRoot, Guid companyId, Guid documentId)
    {
        if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(attachmentsRoot)) return false;
        string full, dir;
        try
        {
            full = Path.GetFullPath(location);
            dir = Path.GetFullPath(Path.Combine(attachmentsRoot, StorageFolder, companyId.ToString("N")));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        full = full.Replace('\\', '/');
        dir = dir.Replace('\\', '/').TrimEnd('/') + "/";
        if (!full.StartsWith(dir, StringComparison.Ordinal)) return false;
        var file = full[dir.Length..];
        return !file.Contains('/') && file.StartsWith(documentId.ToString("N") + "-", StringComparison.Ordinal)
            && file.EndsWith(".png", StringComparison.Ordinal);
    }

    /// <summary>ฝ่ายค้านรอบ 239 S3 — เนื้อหาเอกสารตอนนี้ยังตรงกับฉบับที่ลูกค้ายอมรับไหม · ตัวตัดสินเดียวของกระดาษ (สอง renderer ผ่าน
    /// <c>ResolveSignersAsync</c>) และหน้าเอกสาร (<c>DocumentResponse.QuotationAcceptContentMatches</c>) ·
    /// null = ไม่มี hash (ยอมรับก่อนรอบ 239 — ไม่รู้ จึงพิมพ์ตามเดิม §H) หรือไม่ได้โหลดบรรทัด (ไม่ได้ตรวจ — ไม่ใช่ "ไม่ตรง")</summary>
    public static bool? ContentMatches(Document doc, IEnumerable<DocumentLine>? lines)
    {
        if (string.IsNullOrWhiteSpace(doc.QuotationAcceptContentHash)) return null;
        var list = lines?.ToList();
        if (list == null || list.Count == 0) return null;   // ใบเสนอราคามีอย่างน้อย 1 บรรทัดเสมอ ⇒ ว่าง = ไม่ได้โหลด
        return string.Equals(doc.QuotationAcceptContentHash, DocumentSignedContent.Hash(doc, list), StringComparison.Ordinal);
    }

    /// <summary>ฝ่ายค้านรอบ 239 N (PDPA) — เอกสารยังอยู่ในอายุเก็บไหม: MAX(<c>RetentionUntil</c>, วันที่เอกสาร + 5 ปี) (พ.ร.บ.การบัญชี ม.14 ·
    /// ป.รัษฎากร §87/3 · กฎ M "MAX(retention)") — อยู่ ⇒ legal hold: คงหลักฐานลายเซ็น/การยอมรับ (หลักฐานสัญญา) แม้มีคำขอลบ</summary>
    public static bool UnderLegalHold(Document doc, DateTime nowUtc) => UnderLegalHold(doc.DocumentDate, doc.RetentionUntil, nowUtc);

    /// <summary>สูตรเดียวของ "เอกสารยังอยู่ในอายุเก็บไหม" (ผู้เรียกที่มีแค่วันที่/RetentionUntil ของใบ — รอบ 267 P2: แถวหลักฐานการตอบรับของลูกค้า)</summary>
    public static bool UnderLegalHold(DateTime documentDate, DateTime? retentionUntil, DateTime nowUtc)
    {
        var byDate = documentDate.AddYears(5);
        var until = retentionUntil is { } r && r > byDate ? r : byDate;
        return until > nowUtc;
    }

    /// <summary>รอบ 267 P2 (PDPA ม.33) — ล้างข้อมูลส่วนบุคคลของผู้ลงนามในแถวหลักฐาน (<see cref="CustomerDocumentAction"/>) ของเอกสารที่พ้นอายุเก็บแล้ว:
    /// ชื่อ → <see cref="Anonymized"/> · นามสกุล/ตำแหน่ง/ชื่อผู้ซื้อ/อีเมล/ข้อความ/IP/hash เบราว์เซอร์ → ว่าง · ประทับ <c>ErasedAt</c> ·
    /// คงเวลา · ชนิด · Rev · hash เนื้อหา/token/ไฟล์ (ไม่ระบุตัวบุคคล) · คืนตำแหน่งไฟล์ (ลายเซ็น · ฉบับที่ลงนาม) ที่ผู้เรียกต้องลบหลังบันทึกแถวสำเร็จ</summary>
    public static IReadOnlyList<string> EraseEvidencePii(CustomerDocumentAction row, DateTime nowUtc)
    {
        var files = new List<string>();
        if (row.ErasedAt != null) return files;
        if (!string.IsNullOrWhiteSpace(row.SignatureFilePath)) files.Add(row.SignatureFilePath!);
        if (!string.IsNullOrWhiteSpace(row.SignedPdfPath)) files.Add(row.SignedPdfPath!);
        row.SignerFirstName = Anonymized;
        row.SignerLastName = null;
        row.SignerTitle = null;
        row.SignerOrgName = null;
        row.SignerEmail = null;
        row.Message = null;
        row.Ip = null;
        row.UserAgentHash = null;
        row.SignatureFilePath = null;
        row.SignedPdfPath = null;
        row.ErasedAt = nowUtc;
        return files;
    }

    public const string Anonymized = "[ANONYMIZED]";

    /// <summary>ฝ่ายค้านรอบ 239 N (PDPA ม.33) — ล้างข้อมูลส่วนบุคคลของผู้ยอมรับ/ผู้เซ็นรับสินค้าบนเอกสารที่พ้นอายุเก็บแล้ว
    /// (ชื่อ · หมายเหตุ · IP · hash UA · รูปลายเซ็น) — คงเวลา/hash เนื้อหา/hash token (ไม่ระบุตัวบุคคล) · คืนตำแหน่งไฟล์ลายเซ็นที่ต้องลบ</summary>
    public static (bool Changed, string? SignatureFile) ErasePii(Document doc)
    {
        var changed = doc.QuotationAcceptedBy is not (null or Anonymized) || doc.QuotationAcceptNote != null || doc.QuotationAcceptIp != null
            || doc.QuotationAcceptUserAgentHash != null || doc.QuotationAcceptSignaturePath != null
            || doc.DeliverySignedBy is not (null or Anonymized) || doc.DeliverySignatureBase64 != null;
        var file = doc.QuotationAcceptSignaturePath;
        if (doc.QuotationAcceptedAt != null) doc.QuotationAcceptedBy = Anonymized; else doc.QuotationAcceptedBy = null;
        doc.QuotationAcceptNote = null;
        doc.QuotationAcceptIp = null;
        doc.QuotationAcceptUserAgentHash = null;
        doc.QuotationAcceptSignaturePath = null;
        if (doc.DeliverySignedAt != null) doc.DeliverySignedBy = Anonymized; else doc.DeliverySignedBy = null;
        doc.DeliverySignatureBase64 = null;
        return (changed, file);
    }

    /// <summary>SHA-256 hex (ตัวพิมพ์เล็ก) · ว่าง = null — ใช้กับ User-Agent/token (เก็บลายนิ้วมือ ไม่เก็บค่าดิบ)</summary>
    public static string? Sha256Hex(string? value)
        => string.IsNullOrEmpty(value) ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>เวลาไทย (UTC+7) ของเวลาที่เก็บเป็น UTC — ใช้แสดงผลเท่านั้น</summary>
    public static DateTime BangkokLocal(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(7);

    /// <summary>เทมเพลตมีช่องลายเซ็นลำดับที่ 3 (ช่องลูกค้า) ที่ renderer จะวาดไหม — เงื่อนไขเดียวกับที่ HTML/QuestPDF วาดช่องที่ 3 ·
    /// ไม่มีช่อง ⇒ ทั้งสอง renderer พิมพ์บล็อก "ลูกค้ายอมรับใบเสนอราคาออนไลน์" แยกแทน (การยอมรับต้องไม่หายจากกระดาษ)</summary>
    public static bool CustomerBoxShown(bool showSignature, int signatureCount, string? label3)
        => showSignature && signatureCount >= 3 && !string.IsNullOrWhiteSpace(label3);

    /// <summary>ล้างหลักฐานการยอมรับทั้งชุด (ออก Rev ใหม่) — ไม่ลบไฟล์ลายเซ็น (หลักฐานของ Rev เดิม · อายุเก็บตามเอกสาร)</summary>
    public static void ClearOn(Document doc)
    {
        doc.QuotationAcceptedAt = null;
        doc.QuotationAcceptedBy = null;
        doc.QuotationAcceptSignaturePath = null;
        doc.QuotationAcceptNote = null;
        doc.QuotationAcceptContentHash = null;
        doc.QuotationAcceptIp = null;
        doc.QuotationAcceptUserAgentHash = null;
        doc.QuotationAcceptTokenHash = null;
        // รอบ 240 (ฝ่ายค้าน N4): Rev ใหม่ = ลูกค้ายังไม่เคยเห็นฉบับนี้ ⇒ ล้างสถานะ "ส่งแล้ว/เปิดแล้ว/เตือนแล้ว" ด้วย (ป้าย + งานเตือนเริ่มใหม่)
        SignRequestKinds.ResetTracking(doc);
    }
}
