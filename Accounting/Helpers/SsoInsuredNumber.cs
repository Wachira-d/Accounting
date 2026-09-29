namespace Accounting.Helpers;

/// <summary>"เลขประกันสังคม" ในไฟล์ สปส.1-10 / สปส.6-09 (.txt) — ตัวตัดสินเดียว (รอบ 200 ทีม R · D-06)
///
/// ═══ ที่มา ═══
/// หน้าเพิ่ม/แก้พนักงานไม่มีช่องเลขประกันสังคม (ส่ง null ตายตัว) ⇒ พนักงานที่เพิ่มผ่านหน้าเว็บมีช่องนี้ว่างทุกคน ⇒ ไฟล์ .txt
/// ประกาศ <c>D|1|&lt;เลขบัตร&gt;||…</c> (ช่องที่ 4 ว่าง) อัปโหลดไม่ผ่าน ขณะที่สรุปท้ายไฟล์บอกว่าปกติ
///
/// ═══ กติกา ═══
/// ผู้ประกันตนสัญชาติไทยใช้เลขประจำตัวประชาชน 13 หลักเป็นเลขประกันสังคม ⇒ ช่องว่าง + เลขบัตรที่ checksum ผ่าน = ใช้เลขบัตร ·
/// ไม่มีทั้งคู่ (หรือเลขบัตรไม่ผ่าน checksum) = ว่าง + นับเป็น "ต้องกรอกเอง" ให้สรุปท้ายไฟล์บอกผู้ใช้ (ไม่แต่งเลข)</summary>
public static class SsoInsuredNumber
{
    /// <summary>(เลขที่ใช้ในไฟล์, มาจากเลขบัตรหรือไม่) · <c>Number</c> ว่าง = ไม่รู้ ต้องกรอกเอง</summary>
    public static (string Number, bool FromCitizenId) Resolve(string? socialSecurityNumber, string? citizenId)
    {
        var ssn = (socialSecurityNumber ?? "").Trim();
        if (ssn.Length > 0) return (ssn, false);
        return ThaiTaxId.HasValidChecksum(citizenId) ? (ThaiTaxId.Normalize(citizenId), true) : ("", false);
    }

    /// <summary>เลขประกันสังคมที่<b>รายงานบนจอ</b>แสดง — รอบ 200 ทีม RF (R200-X6): ตัวตัดสินเดียวกับไฟล์ สปส.1-10 (<see cref="Resolve"/>)
    /// แล้วค่อยปิดบังด้วย <c>PiiMask.CitizenId</c> เมื่อไม่มีสิทธิ์ Pii.View · เดิมจออ่าน <c>Employee.SocialSecurityNumber</c> ตรง ๆ (ว่างเกือบทุกคน)
    /// ⇒ จอขึ้น "-" ขณะที่ไฟล์ส่งเลขบัตร = สองความจริงของ "เลข ปกส. ของคนนี้" · ไม่รู้ = <c>null</c> (ไม่แต่งเลข)</summary>
    public static string? ForDisplay(string? socialSecurityNumber, string? citizenId, bool includePii)
    {
        var (number, _) = Resolve(socialSecurityNumber, citizenId);
        if (number.Length == 0) return null;
        return includePii ? number : PiiMask.CitizenId(number);
    }

    /// <summary>ข้อความเตือนต่อท้ายสรุปไฟล์เมื่อมีแถวที่ไม่มีเลขประกันสังคม (null = ไม่มี)</summary>
    public static string? MissingNotice(int missingCount)
        => missingCount <= 0 ? null
            : $" · ⚠️ {missingCount} คนไม่มีเลขประกันสังคมและเลขบัตรประชาชนที่ถูกต้อง — ช่องนี้ว่างในไฟล์ (ระบบ สปส. จะไม่รับ) "
              + "กรอกเลขบัตร/เลขประกันสังคมที่ทะเบียนพนักงานก่อนดาวน์โหลดใหม่";
}
