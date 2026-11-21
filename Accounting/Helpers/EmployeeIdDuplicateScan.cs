namespace Accounting.Helpers;

/// <summary>
/// รอบ 284 (แพ็ก F · audit T4 L-1): ตัวตัดสินเดียวของ "เลขบัตรในไฟล์นำเข้าซ้ำกับพนักงานเดิมไหม" เมื่อคอลัมน์ของพนักงานเดิมเข้ารหัส
///
/// <para><b>ที่มา:</b> ตัวจับซ้ำเดิมถอดรหัสแล้วเทียบในหน่วยความจำ (รอบ 200 G2-06) แต่แถวที่<b>ถอดรหัสไม่ได้</b> (คีย์เปลี่ยน · ciphertext ค้าง —
/// <see cref="EncryptedColumnConverter.IsUnreadable"/>) ได้ค่า ciphertext มาแทน ⇒ เทียบไม่ตรง ⇒ "ไม่พบของซ้ำ" แบบเงียบ ⇒ สร้างพนักงานคนเดิมซ้ำ
/// (เงินเดือนสองแถว) · ตัวนี้ข้ามค่าที่ถอดไม่ได้ (ไม่ normalize ciphertext เป็นตัวเลข — กันจับคู่ปลอม) และนับจำนวนให้ผู้เรียกขึ้นคำเตือน
/// "ตรวจซ้ำด้วยเลขบัตรไม่ได้ N คน" — ไม่เดาว่าซ้ำ (ไม่รู้ = บอกว่าไม่รู้ · F2 ข้อ 3)</para>
/// </summary>
public static class EmployeeIdDuplicateScan
{
    /// <summary>รหัสคำเตือนของศูนย์นำเข้า (ผู้ใช้ต้องรับทราบก่อน commit)</summary>
    public const string IssueCode = "employee-id-unreadable";

    /// <param name="MatchId">พนักงานเดิมที่เลขบัตร/เลขผู้เสียภาษีตรงกัน (null = ไม่พบในแถวที่อ่านได้)</param>
    /// <param name="UnreadableCount">จำนวนพนักงานเดิมที่เลขบัตรหรือเลขผู้เสียภาษีถอดรหัสไม่ได้ (ตรวจซ้ำกับคนเหล่านี้ไม่ได้)</param>
    public readonly record struct Result(Guid? MatchId, int UnreadableCount);

    public static Result Scan(IEnumerable<(Guid Id, string? CitizenId, string? TaxId)> existing, string? idCard)
    {
        Guid? match = null;
        var unreadable = 0;
        foreach (var e in existing)
        {
            var cu = EncryptedColumnConverter.IsUnreadable(e.CitizenId);
            var tu = EncryptedColumnConverter.IsUnreadable(e.TaxId);
            if (cu || tu) unreadable++;
            if (match == null
                && ((!cu && EncryptedIdMatch.Same(e.CitizenId, idCard)) || (!tu && EncryptedIdMatch.Same(e.TaxId, idCard))))
                match = e.Id;
        }
        return new Result(match, unreadable);
    }

    /// <summary>ข้อความคำเตือน (ไทย · บอกทางไปต่อ) · 0 = ไม่มีคำเตือน (null)</summary>
    public static string? Warning(int unreadableCount)
        => unreadableCount <= 0 ? null
            : $"ตรวจซ้ำด้วยเลขบัตรไม่ได้ {unreadableCount} คน — พนักงานเดิมมีเลขบัตร/เลขผู้เสียภาษีที่ถอดรหัสไม่ได้ ระบบจึงบอกไม่ได้ว่าแถวนี้เป็นคนเดิมหรือไม่ · " +
              "ตรวจรายชื่อในหน้า \"สุขภาพข้อมูลเข้ารหัส\" (/pages/encryption-health.html) ก่อน หรือใส่รหัสพนักงานเดิมในไฟล์ (ระบบจับคู่ด้วยรหัสได้) · ยืนยันว่าเป็นคนใหม่จึงนำเข้า";
}
