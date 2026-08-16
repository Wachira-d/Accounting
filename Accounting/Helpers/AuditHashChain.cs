using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// Canonical form + SHA-256 ของ audit hash chain — **ตัวเดียวของทั้งระบบ**
/// ใช้ร่วมทั้งฝั่งเขียน (<c>AccountingDbContext.ApplyAuditHashChain</c>) และฝั่ง
/// ตรวจ (<c>AuditTrailService.VerifyHashChainAsync</c>)
///
/// ทำไมต้องเป็นฟังก์ชันเดียว: เดิมสองฝั่งเขียน format string แยกกัน แล้ว drift —
/// ฝั่งเขียนใช้ <c>(int)Action</c> + มี <c>OldValues</c>, ฝั่งตรวจใช้ชื่อ enum +
/// ตก <c>OldValues</c> ⇒ hash ไม่มีวันตรง ⇒ <c>VerifyHashChainAsync</c> คืน
/// invalid ที่แถวแรกของทุกบริษัทเสมอแม้ไม่มีใครแก้ข้อมูล, งานตรวจรายสัปดาห์
/// แจ้งเตือนหลอกทุก tenant, และ**แยก "โดนแก้จริง" ออกจาก "mismatch ในตัว"
/// ไม่ได้เลย** = tamper-evidence ตาม พ.ร.บ.การบัญชี ม.11 ทวิ / SOC2 ล้มเหลวเงียบ ๆ
/// (ดู CLAUDE.md กฎเหล็ก #4 C — "hash/signature มี canonical function เดียว")
///
/// ⚠️ การแก้รูปแบบ canonical = ทำให้ hash ของแถวเก่าทั้งหมดตรวจไม่ผ่าน
/// (โดยเจตนา — chain ผูกกับรูปแบบ) ถ้าจำเป็นต้องเปลี่ยนจริง ต้องมีแผน
/// re-seal/versioning ไม่ใช่แก้เฉย ๆ
/// </summary>
public static class AuditHashChain
{
    /// <summary>ลำดับ field ตายตัว:
    /// Timestamp(ISO-O)|UserId|UserEmail|(int)Action|EntityType|EntityId|NewValues|OldValues|PrevHash</summary>
    public static string Canonical(
        DateTime timestamp, Guid? userId, string? userEmail, AuditAction action,
        string entityType, string? entityId, string? newValues, string? oldValues, string? prevHash)
        => $"{timestamp:O}|{userId}|{userEmail}|{(int)action}|{entityType}|{entityId}|{newValues}|{oldValues}|{prevHash}";

    /// <summary>SHA-256 hex (ตัวพิมพ์ใหญ่) ของ canonical form</summary>
    public static string ComputeRowHash(
        DateTime timestamp, Guid? userId, string? userEmail, AuditAction action,
        string entityType, string? entityId, string? newValues, string? oldValues, string? prevHash)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Canonical(timestamp, userId, userEmail, action, entityType, entityId,
                newValues, oldValues, prevHash))));

    /// <summary>overload สำหรับแถวที่มีอยู่แล้ว — ใช้ <c>row.PrevHash</c> เป็น
    /// ตัวเชื่อม (ฝั่งตรวจ). ฝั่งเขียนใช้ overload ข้างบนเพราะยังไม่ได้ set PrevHash</summary>
    public static string ComputeRowHash(AuditLog row)
        => ComputeRowHash(row.Timestamp, row.UserId, row.UserEmail, row.Action,
            row.EntityType, row.EntityId, row.NewValues, row.OldValues, row.PrevHash);
}
