using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 285 — <b>ตัวตัดสินเดียว</b>ของ "บริษัทยืนยันเลขผู้เสียภาษีแล้ว" (คำตัดสิน 285-1…285-6 · ที่มา: 280-10 ฝ่ายค้าน MED-1 —
/// ใครก็ตั้ง <c>Company.TaxId</c> เป็นเลขของคนอื่นได้)
///
/// <para><b>หลักฐาน (285-1):</b> ชื่อในทะเบียนเป็นข้อมูลสาธารณะ ⇒ "ชื่อตรงทะเบียน" พิสูจน์ความเป็นเจ้าของ<b>ไม่ได้</b> ใช้เป็นด่านตัดของที่ผิดชัด
/// (ชื่อไม่ตรง = ยื่นไม่ได้) เท่านั้น · ตัวพิสูจน์ = เอกสาร ภ.พ.20 หรือหนังสือรับรองการจดทะเบียน ที่<b>คน</b> (ผู้ดูแลแพลตฟอร์ม · SystemAdmin)
/// เปิดดูแล้วอนุมัติ · การยืนยันทางอีเมลโดเมนไม่ใช้ (โดเมนไม่ผูกกับเลขภาษี) · เทียบชื่อ = ตรงตัวหลังตัดคำนำหน้า/ต่อท้ายด้วย
/// <see cref="DbdIdentityGuard.Normalize"/> — ห้าม fuzzy</para>
///
/// <para><b>ผล (285-3):</b> ยืนยันแล้ว ⇒ เปิดคำขอเชื่อมคู่ค้าที่ส่งถึงเลขนี้<b>ก่อน</b>ตั้งเลขได้ (<see cref="IncomingRequestCutoff"/>) ·
/// ยังไม่ยืนยัน ⇒ เฉพาะคำขอหลัง <c>TaxIdSetAt</c> (พฤติกรรมรอบ 280)</para>
/// <para><b>หลุดสถานะ (285-4):</b> เปลี่ยนเลข ⇒ <see cref="ClearOnTaxIdChange"/> (เรียกจาก SaveChanges ทุกทางเข้า) · และ
/// <see cref="IsVerified"/> เทียบเลขที่ยืนยันกับเลขปัจจุบันอีกชั้น</para>
/// </summary>
public static class CompanyTaxIdVerification
{
    public const string RuleCode = "R285-TAXID-VERIFY";
    public const string LegalReference = "ภ.พ.20 (ใบทะเบียนภาษีมูลค่าเพิ่ม) / หนังสือรับรองการจดทะเบียนนิติบุคคล — หลักฐานเจ้าของเลขประจำตัวผู้เสียภาษี";
    /// <summary>โฟลเดอร์ของไฟล์หลักฐานใต้ <c>AttachmentsRoot/{CompanyId}/</c> ในที่เก็บไฟล์กลาง (<c>IFileStorage</c>) — ไม่ใช่ <c>FileAttachments</c>
    /// (เส้นไฟล์แนบทั่วไปจึงเปิด/ลบไม่ได้ · ข้อเพิ่มรอบ 285)</summary>
    public const string EvidenceFolder = "CompanyTaxIdEvidence";
    /// <summary>ชนิดข้อมูลใน <c>PdpaPiiAccessLogs.SubjectType</c> เมื่อเปิดไฟล์หลักฐาน</summary>
    public const string PiiSubjectType = "CompanyTaxIdVerificationRequest";
    /// <summary>ระยะเก็บหลักฐาน (ปี) นับจากวันยื่น — นโยบายแพลตฟอร์ม (คำตัดสิน 285-9): ภ.พ.20/หนังสือรับรองมีชื่อกรรมการ/ผู้มีอำนาจ = ข้อมูลส่วนบุคคล
    /// เก็บเท่าที่ต้องใช้พิสูจน์การยืนยัน · ไม่มีทางลบก่อนครบ · งานลบเมื่อครบยังไม่มี (เลื่อน — ไฟล์ค้าง = ทิศปลอดภัย)</summary>
    public const int EvidenceRetentionYears = 5;

    public const string EvidencePp20 = "Pp20";
    public const string EvidenceCompanyCertificate = "CompanyCertificate";
    public static readonly IReadOnlyList<string> EvidenceKinds = new[] { EvidencePp20, EvidenceCompanyCertificate };

    public const string StatusPending = "Pending";
    public const string StatusApproved = "Approved";
    public const string StatusRejected = "Rejected";
    /// <summary>อนุมัติแล้วแต่ผู้ดูแลถอนภายหลัง</summary>
    public const string StatusRevoked = "Revoked";

    public const string RegistryMatch = "Match";
    public const string RegistryMismatch = "Mismatch";
    public const string RegistryUnknown = "Unknown";

    public const int MaxEvidenceBytes = 10 * 1024 * 1024;
    public const int MaxNoteLength = 1000;

    public static DateTime EvidenceRetainUntil(DateTime submittedAtUtc) => submittedAtUtc.AddYears(EvidenceRetentionYears);

    /// <summary>ใครเปิดไฟล์หลักฐานได้ — ผู้ดูแลแพลตฟอร์ม (SystemAdmin) หรือ<b>เจ้าของ</b>บริษัทนั้น (ไม่ใช่ทุกคนที่มีสิทธิ์ตั้งค่าบริษัท)</summary>
    public static bool CanViewEvidence(bool isPlatformAdmin, bool isCompanyOwner) => isPlatformAdmin || isCompanyOwner;

    public const string EvidenceDeniedMessage = "ไฟล์หลักฐานยืนยันเลขผู้เสียภาษีเปิดได้เฉพาะเจ้าของบริษัทและผู้ดูแลระบบ NextAcc";

    /// <summary>กระดิ่ง/อีเมลถึงผู้ดูแลแพลตฟอร์มเมื่อมีคำขอใหม่</summary>
    public static (string Title, string Message) AdminNewRequestNotice(string companyName, string taxId, string registryResult, string? registryName)
        => ($"คำขอยืนยันเลขผู้เสียภาษีใหม่: {companyName}",
            $"เลข {taxId} · {RegistryLabel(registryResult, registryName)} · เปิดไฟล์หลักฐานก่อนอนุมัติที่หน้า ยืนยันเลขผู้เสียภาษีบริษัท");

    /// <summary>กระดิ่ง/อีเมลถึงบริษัทเมื่อผู้ดูแลตัดสิน (พร้อมเหตุ)</summary>
    public static (string Title, string Message) CompanyReviewedNotice(string status, string taxId, string? note) => status switch
    {
        StatusApproved => ($"ยืนยันเลขผู้เสียภาษี {taxId} แล้ว",
            "ผู้ดูแลระบบ NextAcc อนุมัติหลักฐานแล้ว — คำขอเชื่อมคู่ค้าที่ส่งถึงเลขนี้ก่อนสมัครจะเปิดให้ตอบที่หน้าคู่ค้า" + (string.IsNullOrWhiteSpace(note) ? "" : $" · หมายเหตุ: {note}")),
        StatusRejected => ($"คำขอยืนยันเลขผู้เสียภาษี {taxId} ถูกปฏิเสธ",
            $"เหตุ: {note} · แก้ตามเหตุแล้วยื่นหลักฐานใหม่ได้ที่หน้าตั้งค่าบริษัท"),
        StatusRevoked => ($"การยืนยันเลขผู้เสียภาษี {taxId} ถูกถอน",
            $"เหตุ: {note} · ยื่นหลักฐานใหม่ได้ที่หน้าตั้งค่าบริษัท"),
        _ => ("สถานะยืนยันเลขผู้เสียภาษีเปลี่ยน", note ?? ""),
    };

    public const string AdminPageUrl = "/admin/taxid-verifications.html";
    public const string CompanyPageUrl = "/pages/settings.html";

    /// <summary>ป้ายชนิดหลักฐาน (หน้าแสดงตรงตัว)</summary>
    public static string EvidenceLabel(string? kind) => kind switch
    {
        EvidencePp20 => "ภ.พ.20 (ใบทะเบียนภาษีมูลค่าเพิ่ม)",
        EvidenceCompanyCertificate => "หนังสือรับรองการจดทะเบียนนิติบุคคล",
        _ => "ไม่ทราบชนิด",
    };

    public static string StatusLabel(string? status) => status switch
    {
        StatusPending => "รอผู้ดูแลระบบตรวจ",
        StatusApproved => "ยืนยันแล้ว",
        StatusRejected => "ถูกปฏิเสธ",
        StatusRevoked => "ถูกถอนการยืนยัน",
        _ => "ยังไม่ได้ยื่น",
    };

    public static string RegistryLabel(string? result, string? registryName) => result switch
    {
        RegistryMatch => $"ชื่อตรงทะเบียน ({registryName})",
        RegistryMismatch => $"⚠️ ชื่อไม่ตรงทะเบียน: {registryName} — อนุมัติได้เมื่อระบุเหตุผล",
        _ => "ตรวจกับทะเบียนไม่ได้ (ทะเบียนไม่ตอบ/ไม่ใช่นิติบุคคล) — ผู้ดูแลตัดสินจากเอกสาร",
    };

    /// <summary><b>ตัวตัดสินเดียว</b>ว่าบริษัทยืนยันเลขปัจจุบันแล้วหรือไม่ — ต้องมีเวลาอนุมัติ + เลขที่ยืนยันตรงเลขปัจจุบัน + เลขถูกต้อง (checksum)</summary>
    public static bool IsVerified(string? currentTaxId, DateTime? verifiedAt, string? verifiedFor)
    {
        if (verifiedAt == null || string.IsNullOrEmpty(verifiedFor)) return false;
        if (!ThaiTaxId.IsValid(currentTaxId)) return false;
        return ThaiTaxId.Normalize(currentTaxId) == ThaiTaxId.Normalize(verifiedFor);
    }

    public static bool IsVerified(Company c) => IsVerified(c.TaxId, c.TaxIdVerifiedAt, c.TaxIdVerifiedFor);

    /// <summary>ล้างสถานะยืนยันเมื่อเลขเปลี่ยน (ผู้เรียก: <c>AccountingDbContext.StampTaxIdSetAt</c> เท่านั้น)</summary>
    public static void ClearOnTaxIdChange(Company c)
    {
        c.TaxIdVerifiedAt = null;
        c.TaxIdVerifiedFor = null;
        c.TaxIdVerificationRequestId = null;
    }

    /// <summary>คำขอเชื่อมคู่ค้าที่ส่งถึงเลขของเรา "ตั้งแต่เมื่อไร" ที่เปิดให้ได้ · ยืนยันแล้ว = ทุกคำขอที่ยังไม่หมดอายุ (<see cref="DateTime.MinValue"/>) ·
    /// ยังไม่ยืนยัน = หลังตั้งเลข · ไม่รู้เวลาตั้งเลข = null (ไม่เปิดเลย)</summary>
    public static DateTime? IncomingRequestCutoff(bool verified, DateTime? taxIdSetAt) => verified ? DateTime.MinValue : taxIdSetAt;

    /// <summary>เทียบชื่อบริษัทกับชื่อทะเบียน — ตรงตัวหลังตัดคำนำหน้า/ต่อท้าย/ช่องว่าง/เครื่องหมาย (ไม่ fuzzy) · ทะเบียนว่าง = ไม่ทราบ</summary>
    public static string CompareRegistryName(string? companyName, string? registryName)
    {
        var reg = DbdIdentityGuard.Normalize(registryName);
        if (reg.Length == 0) return RegistryUnknown;
        var mine = DbdIdentityGuard.Normalize(companyName);
        if (mine.Length == 0) return RegistryMismatch;
        return mine == reg ? RegistryMatch : RegistryMismatch;
    }

    /// <summary>ยื่นได้ไหม — null = ได้ · อื่น ๆ = เหตุ + ทางไปต่อ (ข้อความไทยถึงผู้ใช้)</summary>
    /// <remarks>ฝ่ายค้าน MED-B2: ชื่อไม่ตรงทะเบียน (ชื่อการค้า/ชื่ออังกฤษ) <b>ไม่</b>บล็อกการยื่นอีกต่อไป — ติดธงให้ผู้ดูแลเห็น ·
    /// ผู้ดูแลอนุมัติได้เมื่อระบุเหตุผล (<see cref="ApproveProblem"/>)</remarks>
    public static string? SubmitProblem(string? taxId, string? evidenceKind, int fileBytes, bool alreadyVerified, bool hasPending)
    {
        if (!ThaiTaxId.IsValid(taxId))
            return "เลขผู้เสียภาษีของบริษัทยังไม่ถูกต้อง (ต้องเป็นเลข 13 หลักที่ผ่าน checksum) — แก้ที่ช่องเลขผู้เสียภาษีแล้วบันทึกก่อนยื่นหลักฐาน";
        if (evidenceKind == null || !EvidenceKinds.Contains(evidenceKind))
            return "เลือกชนิดหลักฐาน: ภ.พ.20 หรือหนังสือรับรองการจดทะเบียน";
        if (fileBytes <= 0) return "แนบไฟล์หลักฐาน (PDF หรือรูปภาพ)";
        if (fileBytes > MaxEvidenceBytes) return "ไฟล์หลักฐานใหญ่เกิน 10 MB";
        if (alreadyVerified) return "เลขผู้เสียภาษีนี้ยืนยันแล้ว";
        if (hasPending) return "มีคำขอยืนยันที่รอผู้ดูแลระบบตรวจอยู่แล้ว — รอผลก่อนยื่นใหม่";
        return null;
    }

    /// <summary>อนุมัติได้ไหม — คำขอต้องรอตรวจ · เลขในคำขอต้องตรงเลขปัจจุบันของบริษัท (บริษัทเปลี่ยนเลขหลังยื่น = อนุมัติไม่ได้) ·
    /// เลข+สาขานี้มีบริษัทอื่นยืนยันอยู่แล้ว = ไม่ได้จนกว่าจะถอนของเดิม (ฝ่ายค้าน MED-B1 — เลขหนึ่งยืนยันได้บริษัทเดียวต่อสาขา) ·
    /// ชื่อไม่ตรงทะเบียน = ต้องระบุเหตุผล (MED-B2)</summary>
    /// <param name="verifiedByOtherCompany">ชื่อบริษัทอื่นที่ยืนยันเลข+สาขาเดียวกันอยู่ (null = ไม่มี)</param>
    public static string? ApproveProblem(string? status, string? requestTaxId, string? currentTaxId, string? registryResult, bool hasEvidence,
        string? note, string? verifiedByOtherCompany)
    {
        if (status != StatusPending) return "คำขอนี้ไม่ได้อยู่ในสถานะรอตรวจ";
        if (!ThaiTaxId.IsValid(currentTaxId) || ThaiTaxId.Normalize(currentTaxId) != ThaiTaxId.Normalize(requestTaxId))
            return "บริษัทเปลี่ยนเลขผู้เสียภาษีหลังยื่นคำขอ — อนุมัติไม่ได้ (ให้บริษัทยื่นหลักฐานของเลขปัจจุบันใหม่)";
        if (!hasEvidence) return "ไม่พบไฟล์หลักฐาน — อนุมัติไม่ได้";
        if (verifiedByOtherCompany != null) return ConflictLabel(verifiedByOtherCompany) + " — ถอนการยืนยันของเดิม (ระบุเหตุ) ก่อนอนุมัติคำขอนี้";
        if (registryResult == RegistryMismatch && string.IsNullOrWhiteSpace(note))
            return "ชื่อบริษัทไม่ตรงทะเบียน — ระบุเหตุผลที่อนุมัติ (เช่น ตรวจแล้วเป็นชื่อการค้า/ชื่อภาษาอังกฤษของนิติบุคคลเดียวกัน)";
        return null;
    }

    /// <summary>ธงเด่นบนคิวของผู้ดูแล: เลข+สาขานี้ยืนยันแล้วโดยบริษัทอื่น</summary>
    public static string ConflictLabel(string otherCompanyName) => $"เลขนี้ยืนยันแล้วโดยบริษัท {otherCompanyName}";

    /// <summary>กุญแจ "เลขหนึ่งยืนยันได้บริษัทเดียวต่อสาขา" — สาขาว่าง = สำนักงานใหญ่ (<see cref="TaxBranchCode"/>)</summary>
    public static string SlotKey(string? taxId, string? branchCode) => ThaiTaxId.Normalize(taxId) + ":" + TaxBranchCode.Normalize(branchCode);

    /// <summary>ปฏิเสธ/ถอน ต้องมีเหตุ (บริษัทเห็นเหตุนี้)</summary>
    public static string? ReasonProblem(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "ระบุเหตุผล (บริษัทจะเห็นข้อความนี้)";
        if (reason.Trim().Length > MaxNoteLength) return "เหตุผลยาวเกิน 1,000 ตัวอักษร";
        return null;
    }

    /// <summary>ป้ายสถานะเลขภาษีของ<b>คู่ค้า</b>บนการ์ดคำขอ (เซิร์ฟเวอร์ตัดสิน · หน้าแสดงตรงตัว)</summary>
    public static string PartnerTaxIdLabel(bool verified) => verified
        ? "เลขผู้เสียภาษีนี้ผ่านการยืนยันเอกสารกับ NextAcc แล้ว"
        : "เลขผู้เสียภาษีนี้ผู้ขอตั้งเอง — ยังไม่ได้ยืนยันเอกสารกับ NextAcc";
}
