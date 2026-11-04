namespace Accounting.Helpers;

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §5.2 / §7.1) — <b>ตัวตัดสินเดียว (pure)</b> ของบัตรที่บันทึกไว้ + คำสั่งตัดบัตรอัตโนมัติของค่าบริการ NextAcc:
/// ข้อความ/ฉบับความยินยอม · ด่านติ๊กยินยอม · วันหมดอายุบัตร · ป้ายแสดงผล · บัตรใบนี้ใช้กับรายการนี้ได้ไหม · ตัดอัตโนมัติได้ไหม (เพราะอะไร)
/// <para>หน้าเว็บแสดงข้อความ/ป้าย/เหตุผลจากเซิร์ฟเวอร์เท่านั้น (F2 ข้อ 5) · PCI-DSS SAQ-A: ไม่มีอินพุตเลขบัตรในไฟล์นี้ — มีแค่ 4 ตัวท้ายที่ผู้ให้บริการส่งกลับ</para>
/// </summary>
public static class PlatformSavedCardRules
{
    /// <summary>วัตถุประสงค์ใน PdpaConsentRecord (ม.19 — ระบุวัตถุประสงค์ชัด)</summary>
    public const string ConsentPurpose = "platform-billing:saved-card-autocharge";
    /// <summary>ช่องทางของหลักฐาน (ต่อด้วยบริษัทผู้จ่ายใน canonical)</summary>
    public const string ConsentChannel = "billing-center:saved-card";
    /// <summary>ฉบับของ<b>ข้อความ</b>ยินยอม — แก้ <see cref="Text"/> เมื่อไรต้องขยับค่านี้ (หลักฐานต้องชี้ข้อความที่ผู้ใช้เห็นจริง)</summary>
    public const string ConsentTextVersion = "autocharge-2";
    /// <summary>ฝ่ายค้าน 234 S2: ฉบับข้อความก่อนหน้า ("ตัดเต็มยอด ไม่หักภาษี ณ ที่จ่าย") — คำสั่งตัดที่ยินยอมฉบับนี้ <b>ยังตัดได้</b> (เต็มยอด) จนกว่าจะยินยอมฉบับใหม่ ·
    /// ฉบับใหม่ (<see cref="ConsentTextVersion"/>) บอกเรื่อง "ตั้งหัก ณ ที่จ่าย ⇒ ตัดยอดสุทธิ + ส่ง 50 ทวิ" ⇒ ตัดยอดสุทธิได้เฉพาะฉบับนี้ (<see cref="MandateAllowsNetCharge"/>)</summary>
    public const string LegacyFullChargeTextVersion = "autocharge-1";

    public const string RuleSave = "PLATFORM-SAVED-CARD-SAVE", RuleRemove = "PLATFORM-SAVED-CARD-REMOVE",
        RuleDefault = "PLATFORM-SAVED-CARD-DEFAULT", RuleMandate = "PLATFORM-AUTOCHARGE-MANDATE",
        RuleAdminBlock = "PLATFORM-AUTOCHARGE-ADMIN-BLOCK", RuleGlobal = "PLATFORM-AUTOCHARGE-GLOBAL";
    public const string LegalReference = "พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล 2562 ม.19 (ความยินยอมชัดแจ้ง · ถอนได้) · PCI-DSS SAQ-A (ไม่เก็บเลขบัตร)";

    public const string MissingMessage = "กรุณาติ๊กยินยอมให้บันทึกบัตรและตัดเงินอัตโนมัติก่อน (อ่านข้อความข้างช่องติ๊ก)";
    /// <summary>ฝ่ายค้าน S2: หลักฐานต้องผูกข้อความที่แสดงจริง ⇒ หน้าเว็บต้องส่งฉบับที่แสดงมาเสมอ</summary>
    public const string VersionMissingMessage = "หน้าเว็บไม่ได้ส่งฉบับข้อความยินยอมที่แสดง — กรุณาโหลดหน้าใหม่แล้วติ๊กอีกครั้ง";
    /// <summary>ฝ่ายค้าน S1: ข้อความรับทราบแยกของผู้จ่ายนิติบุคคล (ตัดเต็มยอด ไม่หักภาษี ณ ที่จ่าย)</summary>
    public const string WhtWarningText = "บริษัทของท่านเป็นนิติบุคคล — การตัดบัตรอัตโนมัติตัดเต็มยอดค้างของใบแจ้งหนี้ โดยไม่หักภาษี ณ ที่จ่าย "
        + "เว้นแต่บริษัทตั้งค่า \"หักภาษี ณ ที่จ่ายทุกครั้ง\" ที่การ์ดข้อมูลบริษัทในศูนย์บิล — ระบบจะตัดยอดสุทธิหลังหัก (ม.40(2) · 3%) และบริษัทต้องส่งหนังสือรับรอง 50 ทวิ ที่ศูนย์บิล · "
        + "ติ๊กช่องนี้เพื่อรับทราบเงื่อนไขภาษีหัก ณ ที่จ่ายของยอดที่ตัดอัตโนมัติ";
    public const string WhtAckMissingMessage = "บริษัทนิติบุคคลต้องติ๊กรับทราบเรื่องภาษีหัก ณ ที่จ่ายก่อนเปิดตัดบัตรอัตโนมัติ (หรือไม่ติ๊กบันทึกบัตร แล้วชำระเอง)";
    /// <summary>ต่อท้ายฉบับในหลักฐานเมื่อผู้จ่ายนิติบุคคลรับทราบเรื่องภาษีหัก ณ ที่จ่าย (hash ครอบข้อความที่แสดงเพิ่ม)</summary>
    public const string WhtAckVersionSuffix = "+wht-ack";

    public const string StaleMessage = "ข้อความยินยอม/นโยบายมีการปรับปรุงระหว่างที่ท่านเปิดหน้านี้ — กรุณาโหลดหน้าใหม่ อ่านข้อความล่าสุด แล้วติ๊กอีกครั้ง";

    /// <summary>ฉบับที่หลักฐานอ้าง = ฉบับข้อความ + ฉบับนโยบายยกเลิก/คืนเงิน (ข้อความอ้างนโยบายฉบับนั้นตรง ๆ) — นโยบายเปลี่ยน ⇒ ฉบับเปลี่ยน ⇒ ต้องยืนยันใหม่</summary>
    public static string Version(string purchasePolicyVersion) => $"{ConsentTextVersion}/{purchasePolicyVersion}";

    /// <summary>ข้อความยินยอม (ดีไซน์ §7.1 ที่ผู้ประสานงานตัดสินไว้) + เรื่องภาษีหัก ณ ที่จ่าย (ตัดเต็มยอด — ผู้ที่ต้องหักให้ปิดการตัดอัตโนมัติ)</summary>
    public static string Text(string purchasePolicyVersion)
        => "ข้าพเจ้ายินยอมให้ NextAcc บันทึกบัตรนี้ไว้กับผู้ให้บริการรับชำระเงิน (NextAcc ไม่เก็บเลขบัตร) และตัดเงินค่าแพ็กเกจ/ค่าส่วนเสริม/ค่าใช้งาน"
           + "ตามใบแจ้งหนี้ที่ถึงกำหนดโดยอัตโนมัติ ตามราคาที่แสดงในศูนย์บิล (ตัดเต็มยอดค้างของใบ — ถ้าบริษัทตั้ง \"หักภาษี ณ ที่จ่ายทุกครั้ง\" ไว้ "
           + "จะตัดยอดสุทธิหลังหักภาษี ณ ที่จ่าย และบริษัทต้องส่งหนังสือรับรอง 50 ทวิ ที่ศูนย์บิล) · "
           + "ยกเลิกการตัดอัตโนมัติได้ทุกเมื่อที่ศูนย์บิล (มีผลกับรอบถัดไป) · ไม่มีการคืนเงินตามนโยบายการยกเลิกและคืนเงิน ฉบับ "
           + purchasePolicyVersion;

    /// <summary>ด่านติ๊กยินยอม — null = ผ่าน (pure) · ฉบับที่หน้าเว็บแสดงไม่ตรงฉบับปัจจุบัน ⇒ ปฏิเสธ (ผู้ใช้ต้องเห็นข้อความที่ใช้จริง)</summary>
    public static string? ConsentProblem(bool accepted, string? versionShown, string currentVersion)
    {
        if (!accepted) return MissingMessage;
        if (string.IsNullOrWhiteSpace(versionShown)) return VersionMissingMessage;   // ฝ่ายค้าน S2: ไม่รับ "ไม่ส่งฉบับ"
        if (versionShown.Trim() != currentVersion) return StaleMessage;
        return null;
    }

    /// <summary>ฝ่ายค้าน S1: ผู้จ่ายนิติบุคคลยังไม่รับทราบเรื่องภาษีหัก ณ ที่จ่าย ⇒ ไม่บันทึก/ไม่เปิด (ค่าตั้งต้นปิด) · บุคคลธรรมดา = ไม่ต้อง</summary>
    public static string? WhtAckProblem(bool payerIsJuristic, bool acknowledged)
        => payerIsJuristic && !acknowledged ? WhtAckMissingMessage : null;

    /// <summary>ฉบับที่ประทับลงหลักฐาน = ฉบับที่แสดง (+ เครื่องหมายรับทราบภาษีหัก ณ ที่จ่ายของนิติบุคคล)</summary>
    public static string EvidenceVersion(string versionShown, bool whtAcknowledged)
        => whtAcknowledged ? versionShown + WhtAckVersionSuffix : versionShown;

    /// <summary>ฉบับข้อความ+นโยบายของคำสั่งตัด (ตัดเครื่องหมายรับทราบภาษีหักท้าย)</summary>
    public static string? MandateBase(string? mandateConsentVersion)
        => mandateConsentVersion?.EndsWith(WhtAckVersionSuffix, StringComparison.Ordinal) == true
            ? mandateConsentVersion[..^WhtAckVersionSuffix.Length] : mandateConsentVersion;

    /// <summary>ฉบับเดียวกันแต่ข้อความรุ่นก่อน (ตัดเต็มยอด) — ใช้ยอมรับคำสั่งตัดเดิม</summary>
    public static string LegacyVersionOf(string currentConsentVersion)
        => currentConsentVersion.StartsWith(ConsentTextVersion, StringComparison.Ordinal)
            ? LegacyFullChargeTextVersion + currentConsentVersion[ConsentTextVersion.Length..] : currentConsentVersion;

    /// <summary>ฝ่ายค้าน 234 S2: คำสั่งตัดนี้ยินยอมข้อความฉบับที่บอกเรื่อง "ตัดยอดสุทธิหลังหัก ณ ที่จ่าย" แล้วหรือยัง — ยัง ⇒ ตัดเต็มยอด (ไม่หัก) จนกว่าจะยินยอมใหม่</summary>
    public static bool MandateAllowsNetCharge(string? mandateConsentVersion, string currentConsentVersion)
        => MandateBase(mandateConsentVersion) == currentConsentVersion;

    /// <summary>วันสุดท้ายที่บัตรใช้ได้ (สิ้นเดือนหมดอายุ) — เดือน/ปีไม่สมเหตุผล ⇒ null (ไม่ทราบ — ไม่เดา)</summary>
    public static DateTime? ValidUntil(int? month, int? year)
        => month is >= 1 and <= 12 && year is >= 2000 and <= 2200
            ? new DateTime(year.Value, month.Value, DateTime.DaysInMonth(year.Value, month.Value), 0, 0, 0, DateTimeKind.Utc)
            : null;

    /// <summary>บัตรหมดอายุแล้ว (เทียบวันปฏิทินไทย) — ไม่ทราบวันหมดอายุ = ยังไม่ถือว่าหมด (ผู้ให้บริการปฏิเสธเองถ้าหมดจริง)</summary>
    public static bool Expired(DateTime? validUntil, DateTime nowUtc)
        => validUntil is DateTime v && PlatformInvoiceRules.ThaiDate(nowUtc).Date > v.Date;

    /// <summary>4 ตัวท้ายที่แสดงได้ — ตัวเลขล้วน 4 หลักเท่านั้น (อย่างอื่น = null · ห้ามเก็บข้อความที่อาจเป็นเลขบัตรยาว)</summary>
    public static string? Last4(string? raw)
        => raw is { Length: 4 } && raw.All(char.IsAsciiDigit) ? raw : null;

    public static string? Brand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        return t.Length > 40 ? t[..40] : t;
    }

    /// <summary>ป้ายบัตร เช่น "Visa •••• 4242 (หมดอายุ 12/2030)"</summary>
    public static string Label(string? brand, string? last4, DateTime? validUntil)
        => $"{(string.IsNullOrWhiteSpace(brand) ? "บัตร" : brand)} •••• {last4 ?? "????"}"
           + (validUntil is DateTime v ? $" (หมดอายุ {v.Month:00}/{v.Year})" : "");

    /// <summary>บัตรที่บันทึกใช้กับรายการนี้ได้ไหม — null = ได้ · ข้อความ = ปฏิเสธ (pure)</summary>
    public static string? UseRefusal(bool methodIsCard, bool samePayer, bool sameProvider, bool sameMode)
    {
        if (!methodIsCard) return "บัตรที่บันทึกไว้ใช้ได้กับการชำระด้วยบัตรเท่านั้น";
        if (!samePayer) return "ไม่พบบัตรที่บันทึกไว้ของบริษัทนี้";
        if (!sameProvider) return "บัตรนี้บันทึกไว้กับผู้ให้บริการรายเดิม — ช่องทางรับชำระของ NextAcc เปลี่ยนแล้ว กรุณาบันทึกบัตรใหม่";
        if (!sameMode) return "บัตรนี้บันทึกไว้คนละโหมด (ทดสอบ/ใช้งานจริง) กับช่องทางรับชำระตอนนี้ — กรุณาบันทึกบัตรใหม่";
        return null;
    }

    /// <summary>ฝ่ายค้าน B2: รายการชำระนี้เป็นหลักฐานว่าบัตรใบนี้ใช้ได้ไหม — ต้องถูกสร้าง<b>ในคำขอนี้</b> (ไม่ใช่รายการเปิดเดิมที่ถูกใช้ซ้ำ) ·
    /// ตัดด้วยบัตรใบนี้ (เลขอ้างอิงบัตรตรง) · ผู้จ่ายเดียวกัน (pure)</summary>
    public static bool IntentProvesCard(DateTime intentCreatedAtUtc, DateTime requestStartedAtUtc, string? intentSavedCardRef, string cardRef,
        Guid? intentPayerCompanyId, Guid payerCompanyId)
        => intentCreatedAtUtc >= requestStartedAtUtc && intentSavedCardRef == cardRef && intentPayerCompanyId == payerCompanyId;

    /// <summary>บัตรใบหนึ่งใช้ตัดอัตโนมัติได้ไหม — null = ได้ (pure)</summary>
    public static string? CardUnusableReason(bool removed, bool verified, DateTime? validUntil, bool sameMode, DateTime nowUtc)
    {
        if (removed) return "บัตรถูกลบแล้ว";
        if (!verified) return "บัตรยังไม่ผ่านการชำระครั้งแรก (ยืนยันกับธนาคาร) — ลบแล้วบันทึกใหม่ตอนชำระด้วยบัตรครั้งถัดไป";
        if (Expired(validUntil, nowUtc)) return "บัตรหมดอายุแล้ว — ลบแล้วบันทึกบัตรใหม่ตอนชำระครั้งถัดไป";
        if (!sameMode) return "บัตรบันทึกไว้คนละโหมด (ทดสอบ/ใช้งานจริง) กับช่องทางรับชำระตอนนี้";
        return null;
    }

    /// <summary>ตัดอัตโนมัติให้บริษัทนี้ได้ไหม (ไม่รวมเรื่องของใบ) — null = ได้ · ข้อความ = เหตุที่ผู้เช่า/แอดมินเห็น (pure)</summary>
    public static string? AutoChargeBlockReason(bool globalEnabled, bool mandateEnabled, bool adminBlocked,
        string? mandateConsentVersion, string currentConsentVersion, string? defaultCardProblem,
        bool payerIsJuristic = false, bool whtAcknowledged = false)
    {
        if (!globalEnabled) return "ผู้ดูแลระบบ NextAcc ยังไม่เปิดการตัดบัตรอัตโนมัติ";
        if (adminBlocked) return "ผู้ดูแลระบบ NextAcc ระงับการตัดบัตรอัตโนมัติของบริษัทนี้ไว้";
        if (!mandateEnabled) return "ยังไม่ได้เปิดตัดบัตรอัตโนมัติ (เปิดเองเมื่อชำระครั้งแรกด้วยบัตรที่บันทึกสำเร็จ)";
        if (WhtAckProblem(payerIsJuristic, whtAcknowledged) is string wht) return wht;
        // ฉบับในคำสั่งตัดอาจมีเครื่องหมายรับทราบภาษีหัก ณ ที่จ่ายต่อท้าย — เทียบเฉพาะฉบับข้อความ+นโยบาย ·
        // ฝ่ายค้าน 234 S2: ฉบับข้อความก่อนหน้า (นโยบายเดียวกัน) ยังตัดได้แบบเต็มยอด
        var mandateBase = MandateBase(mandateConsentVersion);
        if (mandateBase != currentConsentVersion && mandateBase != LegacyVersionOf(currentConsentVersion))
            return "นโยบาย/ข้อความยินยอมมีฉบับใหม่ — กดเปิดตัดบัตรอัตโนมัติอีกครั้งเพื่อยืนยัน (ระบบหยุดตัดจนกว่าจะยืนยัน)";
        if (defaultCardProblem != null) return defaultCardProblem;
        return null;
    }
}
