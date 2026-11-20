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
        // รอบ 274: ค่า enc1:… = converter ถอดไม่ออก (คีย์ถูกเปลี่ยน) — ห้ามลงไฟล์ สปส. ทั้งก้อน/ตัดตัวเลขจาก ciphertext (ตัวเลขสุ่ม 13 ตัวผ่าน checksum ได้ ~1/10)
        if (ssn.Length > 0 && !EncryptedColumnConverter.IsUnreadable(ssn)) return (ssn, false);
        return !EncryptedColumnConverter.IsUnreadable(citizenId) && ThaiTaxId.HasValidChecksum(citizenId)
            ? (ThaiTaxId.Normalize(citizenId), true) : ("", false);
    }

    /// <summary>รอบ 274 (ฝ่ายค้าน LOW-5 · สอดคล้องกับ ภ.ง.ด.1): ไฟล์ สปส. ที่มีผู้ประกันตนเลขบัตรถอดรหัสไม่ได้/ไม่ถูกต้อง ⇒ <b>ไม่ออกไฟล์</b>
    /// พร้อมรายชื่อ (เลขปิดบัง) + ทางไปต่อ · เลขบัตร<b>ว่าง</b> คงเดิม (ช่องว่าง + นับใน <see cref="MissingNotice"/>)</summary>
    public const string UnusableRuleCode = "SSO-CITIZENID-UNUSABLE";

    /// <remarks>รอบ 275 แพ็ก E (T4 F3 · คำตัดสิน 4.2 "ไฟล์ สปส. ตามสเปก สปส."): เลขบัตร<b>ไม่ถูกต้อง</b>กันเฉพาะคนที่<b>ไม่มีเลขประกันสังคมที่ใช้ได้</b>
    /// (13 หลัก + mod-11 — <see cref="HasUsableSsn"/> · ฝ่ายค้าน MED-1)
    /// (เดิมตรวจเลขบัตรทุกคน ⇒ ต่างชาติที่มีเลข ปกส. ถูกต้องแต่ช่องเลขบัตรมีพาสปอร์ตเก่า ทำให้ไฟล์ทั้งบริษัทถูกปฏิเสธ) — คนนั้นช่องเลขบัตรในไฟล์ว่าง
    /// (<see cref="CitizenIdForFile"/>) ช่องเลข ปกส. ใช้เลข ปกส. · ถอดรหัสไม่ได้ยังกันทั้งไฟล์เหมือนเดิม (ข้อมูลหาย ต้องให้คนเห็น)</remarks>
    public static void ThrowIfUnusable(string formName, IEnumerable<(string Code, string? Name, string? CitizenId, string? SocialSecurityNumber)> people)
    {
        var bad = people
            .Select(p => (p.Code, p.Name, Id: EmployeeTaxIdentity.InspectValue(p.CitizenId), HasSsn: HasUsableSsn(p.SocialSecurityNumber)))
            .Where(p => p.Id.Problem == TaxIdProblem.Undecryptable || (p.Id.Problem == TaxIdProblem.Invalid && !p.HasSsn))
            .DistinctBy(p => p.Code)
            .ToList();
        if (bad.Count == 0) return;
        throw new BusinessRuleException(
            $"{formName}: ไม่ออกไฟล์ — เลขบัตรประชาชนของผู้ประกันตน {bad.Count} รายใช้ไม่ได้:\n"
            + string.Join("\n", bad.Select(b => $"• {b.Code} {b.Name} [{b.Id.Masked ?? "-"}]: {b.Id.Reason}"))
            + "\nแก้ที่ทะเบียนพนักงาน (ถอดรหัสไม่ได้: แจ้งผู้ดูแลระบบ — ดู " + EncryptionHealthText.PageName + ") แล้วดาวน์โหลดใหม่", UnusableRuleCode, 422);
    }

    /// <summary>รอบ 274: เลขบัตรที่ลงไฟล์ สปส. — ค่า enc1:… (ถอดรหัสไม่ได้) ⇒ ว่าง (ไม่ลง ciphertext ทั้งก้อน · ไม่ตัดตัวเลขจากมัน) · อื่น ๆ คงเดิม ·
    /// เหตุ/แถวที่ต้องแก้: รายงาน <c>EmployeeIdentityHealth</c></summary>
    public static string CitizenIdForFile(string? citizenId)
        => EncryptedColumnConverter.IsUnreadable(citizenId) ? ""
            // รอบ 275 แพ็ก E: เลขที่ไม่ถูกต้อง (ผ่านด่านได้เพราะคนนั้นมีเลข ปกส.) ⇒ ว่าง — ไม่ลงเลขผิดในช่องเลขบัตร
            : EmployeeTaxIdentity.InspectValue(citizenId).Problem == TaxIdProblem.Invalid ? "" : citizenId ?? "";

    /// <summary>ฝ่ายค้าน MED-1 (คำตัดสิน 275-E-3 ฉบับแก้): เลขประกันสังคม "ใช้ได้" ที่จะปล่อยผ่านเลขบัตรที่ผิด = <b>13 หลักที่ผ่าน mod-11</b> (เลข ปกส. ของทั้งคนไทยและ
    /// ต่างชาติเป็น 13 หลัก) · ถอดรหัสไม่ได้/ผิดรูป/checksum ไม่ผ่าน ⇒ ใช้ไม่ได้ · เท่ากับเลขบัตรที่ checksum ผิด ⇒ ผิดตามไปด้วยโดยอัตโนมัติ (ไม่ผ่าน mod-11)</summary>
    internal static bool HasUsableSsn(string? ssn)
    {
        return !string.IsNullOrWhiteSpace(ssn) && !EncryptedColumnConverter.IsUnreadable(ssn) && ThaiTaxId.IsValid(ThaiTaxId.Normalize(ssn));
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
