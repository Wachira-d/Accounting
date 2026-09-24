using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Entities;
using Accounting.Models.Enums;   // AuditAction (AuditLog.Action) — คนละ namespace กับ entity

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
    internal static string ComputeRowHash(
        DateTime timestamp, Guid? userId, string? userEmail, AuditAction action,
        string entityType, string? entityId, string? newValues, string? oldValues, string? prevHash)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Canonical(timestamp, userId, userEmail, action, entityType, entityId,
                newValues, oldValues, prevHash))));

    /// <summary>(สูตร v1 — internal ตั้งแต่รอบ 193: ฝั่งเขียนใช้ <see cref="Seal"/> · ฝั่งตรวจใช้ <see cref="VerifyRow"/>)
    /// overload สำหรับแถวที่มีอยู่แล้ว — ใช้ <c>row.PrevHash</c> เป็น
    /// ตัวเชื่อม (ฝั่งตรวจ). ฝั่งเขียนใช้ overload ข้างบนเพราะยังไม่ได้ set PrevHash</summary>
    internal static string ComputeRowHash(AuditLog row)
        => ComputeRowHash(row.Timestamp, row.UserId, row.UserEmail, row.Action,
            row.EntityType, row.EntityId, row.NewValues, row.OldValues, row.PrevHash);

    // ======================================================================
    // สูตร v2 — ค่าที่ round-trip ผ่าน PostgreSQL แล้วได้เท่าเดิม (ฝ่ายค้านรอบ 193 PLAUSIBLE-1)
    // ======================================================================
    //
    // ทำไมสูตร v1 (ข้างบน) ตรวจไม่ผ่านทั้งระบบ: ฝั่งเขียน hash ค่า DateTime.UtcNow ในหน่วยความจำ
    // (Kind=Utc · 7 หลักทศนิยม ⇒ "…:01.1234567Z") แต่คอลัมน์ "Timestamp" เป็น timestamp without time zone
    // (Program.cs แปลง timestamptz ทิ้ง + EnableLegacyTimestampBehavior) ⇒ อ่านกลับได้ Kind=Unspecified และ
    // PostgreSQL เก็บแค่ไมโครวินาที ⇒ "O" ของค่าที่อ่านกลับ = "…:01.1234560" (ไม่มี Z · หลักที่ 7 หาย) ⇒ hash
    // ไม่ตรงตั้งแต่แถวแรกของทุกบริษัท แม้ไม่มีใครแตะข้อมูล. เทสต์เดิมผ่านเพราะ hash ค่าในหน่วยความจำทั้งสองฝั่ง
    // (ไม่มีขั้น round-trip)
    //
    // ทางแก้: สูตร v2 normalize เวลาเป็น UTC + ตัดเหลือไมโครวินาที + format ตายตัว ก่อน hash ทั้งฝั่งเขียนและตรวจ ·
    // แถว v2 ติดป้าย "v2:" หน้า RowHash (ไม่เพิ่มคอลัมน์ · แถวเก่าไม่ถูกเขียนทับ — ตาราง AuditLogs append-only)
    // แถว v1 ที่มีอยู่แล้วตรวจด้วย <see cref="VerifyLegacyRow"/> ซึ่งลองคืนหลัก 100ns ที่ PostgreSQL ตัดทิ้ง (10 ค่า)

    /// <summary>ป้ายรุ่นสูตรหน้า RowHash — แถวที่ไม่มีป้าย = v1 (ก่อนรอบ 193)</summary>
    public const string V2Prefix = "v2:";

    /// <summary>เวลาที่ใช้ hash ในสูตร v2: UTC (Unspecified = ค่าที่เราเขียนเป็น UTC อยู่แล้ว · Local แปลงกลับ) +
    /// ตัดเหลือไมโครวินาที (ความละเอียดของ PostgreSQL) — ค่าเดียวกันทั้งก่อนเขียนและหลังอ่านกลับ</summary>
    private static DateTime NormalizeTimestamp(DateTime t)
    {
        var utc = t.Kind switch
        {
            DateTimeKind.Local => t.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(t, DateTimeKind.Utc),
            _ => t,
        };
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }

    /// <summary>canonical v2 — ช่องเหมือน v1 แต่เวลาเป็น UTC ไมโครวินาทีรูปแบบตายตัว และขึ้นต้นด้วย "v2|"
    /// (สูตรต่างรุ่นไม่มีทางได้สตริงเดียวกัน)</summary>
    private static string CanonicalV2(AuditLog row)
        => "v2|" + NormalizeTimestamp(row.Timestamp).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
               System.Globalization.CultureInfo.InvariantCulture)
           + $"|{row.UserId}|{row.UserEmail}|{(int)row.Action}|{row.EntityType}|{row.EntityId}"
           + $"|{row.NewValues}|{row.OldValues}|{row.PrevHash}";

    /// <summary>RowHash รุ่น v2 = "v2:" + SHA-256 hex ของ <see cref="CanonicalV2"/></summary>
    private static string ComputeRowHashV2(AuditLog row)
        => V2Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalV2(row))));

    /// <summary><b>ฝั่งเขียนตัวเดียว</b> — ผูกแถวเข้ากับ hash ก่อนหน้า แล้วประทับ RowHash รุ่น v2 · ตั้ง
    /// <c>row.Timestamp</c> เป็นค่าที่ normalize แล้วด้วย (ค่าที่เก็บ = ค่าที่ hash · ไม่พึ่งการปัดของฐานข้อมูล)</summary>
    public static void Seal(AuditLog row, string? prevHash)
    {
        row.PrevHash = prevHash;
        row.Timestamp = NormalizeTimestamp(row.Timestamp);
        row.RowHash = ComputeRowHashV2(row);
    }

    /// <summary><b>ฝั่งตรวจตัวเดียว</b> ต่อแถว — v2 ตรวจตรง ๆ · v1 (ไม่มีป้าย) ตรวจแบบ legacy</summary>
    public static bool VerifyRow(AuditLog row)
    {
        if (string.IsNullOrEmpty(row.RowHash)) return false;
        if (row.RowHash.StartsWith(V2Prefix, StringComparison.Ordinal))
            return string.Equals(ComputeRowHashV2(row), row.RowHash, StringComparison.OrdinalIgnoreCase);
        return VerifyLegacyRow(row);
    }

    /// <summary>ตรวจแถว v1 ที่อ่านกลับจากฐานข้อมูล: ลองค่าเดิมตามที่อ่านมา (แถวที่ยังอยู่ในหน่วยความจำ/เทสต์)
    /// แล้วลองคืนรูปเดิมตอนเขียน = UTC + หลัก 100ns ที่ PostgreSQL ตัดทิ้ง (0–9) · ไม่มีค่าไหนตรง = ถูกแก้จริง
    /// (ความแข็งของ SHA-256 ไม่เปลี่ยน — ผู้แก้ยังต้องสร้าง hash ที่ตรงกับ 1 ใน 10 ค่าให้ได้ ซึ่งทำไม่ได้โดยไม่แก้ทั้ง chain)</summary>
    private static bool VerifyLegacyRow(AuditLog row)
    {
        if (string.IsNullOrEmpty(row.RowHash)) return false;
        bool Match(DateTime ts) => string.Equals(
            ComputeRowHash(ts, row.UserId, row.UserEmail, row.Action, row.EntityType, row.EntityId,
                row.NewValues, row.OldValues, row.PrevHash),
            row.RowHash, StringComparison.OrdinalIgnoreCase);
        if (Match(row.Timestamp)) return true;
        var baseUtc = NormalizeTimestamp(row.Timestamp);
        for (var sub = 0; sub < 10; sub++)
            if (Match(new DateTime(baseUtc.Ticks + sub, DateTimeKind.Utc))) return true;
        return false;
    }

    /// <summary>ตรวจทั้ง chain ของบริษัทหนึ่ง — <paramref name="rowsOrderedById"/> ต้องเรียงตาม <c>Id</c>
    /// (ลำดับเดียวกับที่ฝั่งเขียนหา hash ก่อนหน้า · เดิมฝั่งตรวจเรียงตาม Timestamp ซึ่งไม่รับประกันว่าตรงลำดับเขียน
    /// เมื่อเวลาข้ามเครื่องไม่ตรงกัน) · คืน index ของแถวแรกที่ผิด (-1 = ถูกทั้ง chain)</summary>
    public static int FirstBrokenIndex(IReadOnlyList<AuditLog> rowsOrderedById)
    {
        string? prev = null;
        for (var i = 0; i < rowsOrderedById.Count; i++)
        {
            var r = rowsOrderedById[i];
            if (r.PrevHash != prev) return i;
            if (!VerifyRow(r)) return i;
            prev = r.RowHash;
        }
        return -1;
    }

    /// <summary>hash ปลาย chain ที่แถวใหม่ต้องผูกต่อ: แถวที่<b>ยังรอบันทึก</b>ใน context เดียวกัน (ชนะ · ตัวท้ายสุด)
    /// มิฉะนั้นแถวล่าสุดในฐานข้อมูล — ฝ่ายค้านรอบ 193 PLAUSIBLE-2: เดิมอ่านจากฐานอย่างเดียว ⇒ เรียก
    /// AddChainedAuditLog สองครั้งก่อน SaveChanges ได้สองแถวที่ PrevHash เดียวกัน = chain แตกกิ่ง</summary>
    public static string? ResolveTip(IEnumerable<AuditLog> pendingSameCompanyInAddOrder, string? lastPersistedHash)
    {
        string? tip = null;
        foreach (var p in pendingSameCompanyInAddOrder)
            if (!string.IsNullOrEmpty(p.RowHash)) tip = p.RowHash;
        return tip ?? lastPersistedHash;
    }
}
