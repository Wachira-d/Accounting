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
    /// <summary>สูตร v1 (ก่อนรอบ 193) — <b>private</b>: ใช้ได้แค่ตรวจแถวเก่า (<see cref="VerifyLegacyRow"/>)
    /// ฝ่ายค้านรอบสอง W2-C2: เดิมเป็น public/internal ⇒ เส้นเขียนย้อนกลับไปใช้สูตรที่ round-trip ไม่ได้แล้วเทสต์ยังเขียว ·
    /// ตอนนี้ฝั่งเขียนมีทางเดียวคือ <see cref="Seal"/> (ล็อกซ้ำด้วย <c>tools/required_call_site_check.py</c>) ·
    /// ลำดับ field: Timestamp(ISO-O)|UserId|UserEmail|(int)Action|EntityType|EntityId|NewValues|OldValues|PrevHash
    /// (เทสต์ถือสำเนาสูตรนี้ไว้เป็น "สเปกที่แช่แข็ง" ของแถวเก่า — ห้ามแก้ที่นี่)</summary>
    private static string LegacyV1Hash(DateTime timestamp, AuditLog row)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{timestamp:O}|{row.UserId}|{row.UserEmail}|{(int)row.Action}|{row.EntityType}|{row.EntityId}"
            + $"|{row.NewValues}|{row.OldValues}|{row.PrevHash}")));

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
    internal static bool VerifyRow(AuditLog row)
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
        bool Match(DateTime ts) => string.Equals(LegacyV1Hash(ts, row), row.RowHash, StringComparison.OrdinalIgnoreCase);
        if (Match(row.Timestamp)) return true;
        var baseUtc = NormalizeTimestamp(row.Timestamp);
        for (var sub = 0; sub < 10; sub++)
            if (Match(new DateTime(baseUtc.Ticks + sub, DateTimeKind.Utc))) return true;
        return false;
    }

    /// <summary>ผลตรวจ chain ของบริษัทหนึ่ง — แยก<b>สาเหตุ</b>ออกจากกัน (ฝ่ายค้านรอบ 193 รอบสอง W2-C1 · F2 ข้อ 7
    /// "ข้อความที่ระบุสาเหตุต้องตรวจสาเหตุนั้นจริง")
    /// <list type="bullet">
    /// <item><b>Tampered</b> — เนื้อแถวไม่ตรงกับ RowHash ที่ประทับไว้ = ถูกแก้หลังบันทึก (<b>ทุกแถว</b> ไม่ใช่แค่แถวแรก)</item>
    /// <item><b>Dangling</b> — PrevHash ชี้ไปยัง hash ที่ไม่มีแถวไหนในบริษัทถืออยู่ = แถวก่อนหน้า<b>ถูกลบ หรือถูกแก้แล้วประทับ hash
    /// ใหม่</b> หรือแถวนั้นไม่ได้เกิดจากระบบ · <b>แยกสามกรณีนี้ไม่ได้</b> (hash ไม่มีกุญแจ — ผู้แก้ประทับแถวที่แก้ใหม่ให้ตรวจเนื้อผ่านได้
    /// แล้วร่องรอยเดียวที่เหลือคือแถวลูกที่ชี้ hash เก่า เหมือนกรณีลบทุกประการ) ⇒ ข้อความต้องครอบทุกกรณี ไม่ระบุว่า "ถูกลบ"
    /// (ฝ่ายค้านรอบ 193 รอบสี่ P4-6 · F2 ข้อ 7)</item>
    /// <item><b>Fork</b> — สองแถวขึ้นไปชี้ PrevHash เดียวกัน (หรือเป็นแถวแรกพร้อมกัน) = คำขอพร้อมกันอ่านปลาย chain เดียวกัน ·
    /// <b>ไม่ใช่หลักฐานการแก้ไข</b> (ทุกแถวยังตรวจเนื้อผ่าน) — รายงานแยก ไม่แจ้งลูกค้าว่า "ถูกแก้"</item>
    /// </list>
    /// ไม่พึ่งลำดับ Id (ลำดับ Id ≠ ลำดับ commit เมื่อคำขอพร้อมกัน · EF ไม่รับประกันลำดับ INSERT ในหนึ่ง SaveChanges — W2-P2)</summary>
    public sealed record ChainAnalysis(
        int TotalRows,
        IReadOnlyList<AuditLog> Tampered,
        IReadOnlyList<AuditLog> Dangling,
        int ForkCount)
    {
        /// <summary>มีหลักฐานว่าถูกแก้/ถูกลบ — เฉพาะกรณีนี้ที่แจ้งลูกค้า (fork ไม่นับ)</summary>
        public bool HasIntegrityFindings => Tampered.Count > 0 || Dangling.Count > 0;
    }

    /// <summary><b>ฝั่งตรวจทั้ง chain ตัวเดียว</b> (service · job · endpoint) — แถวที่ส่งมาต้องเป็นของบริษัทเดียวและมี RowHash
    /// (แถวนอก chain ให้ผู้เรียกนับแยก) · ลำดับที่ส่งมาใช้แค่เรียงรายงาน</summary>
    public static ChainAnalysis Analyze(IReadOnlyList<AuditLog> rows)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            if (!string.IsNullOrEmpty(r.RowHash)) known.Add(r.RowHash);
        var tampered = new List<AuditLog>();
        var dangling = new List<AuditLog>();
        var children = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const string genesis = "\u0000genesis";
        foreach (var r in rows)
        {
            if (!VerifyRow(r)) tampered.Add(r);
            if (!string.IsNullOrEmpty(r.PrevHash) && !known.Contains(r.PrevHash)) { dangling.Add(r); continue; }
            var parent = string.IsNullOrEmpty(r.PrevHash) ? genesis : r.PrevHash;
            children[parent] = children.TryGetValue(parent, out var c) ? c + 1 : 1;
        }
        var forks = children.Values.Where(c => c > 1).Sum(c => c - 1);
        return new ChainAnalysis(rows.Count, tampered, dangling, forks);
    }

    /// <summary>ข้อความแจ้งเตือนถึงบริษัท — <b>ตามสาเหตุที่ตรวจพบจริง</b> (null = ไม่มีอะไรต้องแจ้ง · fork อย่างเดียวไม่แจ้ง)
    /// ไม่อ้างวิธี ("raw SQL") ที่ตรวจไม่ได้ · hash chain ไม่มีกุญแจ ⇒ ผู้ที่เขียนฐานข้อมูลได้ประทับใหม่ทั้งช่วงได้ (W2-P3)
    /// จึงบอกแค่ "พบความไม่ตรงกัน" ไม่ใช่ "พิสูจน์ได้ว่าไม่มีใครแก้" เมื่อผ่าน</summary>
    public static string? AlertMessage(ChainAnalysis a, int maxIds = 20)
    {
        if (!a.HasIntegrityFindings) return null;
        string Ids(IReadOnlyList<AuditLog> xs) => string.Join(", ", xs.Take(maxIds).Select(x => "#" + x.Id))
            + (xs.Count > maxIds ? $" และอีก {xs.Count - maxIds} แถว" : "");
        var parts = new List<string>();
        if (a.Tampered.Count > 0)
            parts.Add($"เนื้อหาของ audit log {a.Tampered.Count} แถวไม่ตรงกับลายนิ้วมือ (hash) ที่ประทับไว้ตอนบันทึก — แถวเหล่านี้ถูกแก้หลังบันทึก: {Ids(a.Tampered)}");
        if (a.Dangling.Count > 0)
            parts.Add($"ลำดับ audit log ขาดตอน {a.Dangling.Count} จุด — แถวก่อนหน้าของแถวต่อไปนี้ไม่อยู่ในสภาพที่บันทึกไว้: "
                      + "ถูกลบ หรือถูกแก้แล้วประทับ hash ใหม่ (ระบบแยกสองกรณีนี้ไม่ได้) หรือแถวนี้ไม่ได้บันทึกโดยระบบ: "
                      + Ids(a.Dangling));
        return string.Join(" · ", parts) + $" (ตรวจ {a.TotalRows} แถว)";
    }

    /// <summary>hash ปลาย chain ที่แถวใหม่ต้องผูกต่อ: แถวที่<b>ยังรอบันทึก</b>ใน context เดียวกันที่ไม่มีแถวรอบันทึกอื่นผูกต่อ
    /// (ปลายจริงของกลุ่ม — ไม่พึ่งลำดับของ ChangeTracker · W2-P2) มิฉะนั้นแถวล่าสุดในฐานข้อมูล ·
    /// PLAUSIBLE-2 รอบแรก: เดิมอ่านจากฐานอย่างเดียว ⇒ AddChainedAuditLog สองครั้งก่อน SaveChanges แตกกิ่ง</summary>
    public static string? ResolveTip(IEnumerable<AuditLog> pendingSameCompany, string? lastPersistedHash)
    {
        var sealedRows = pendingSameCompany.Where(p => !string.IsNullOrEmpty(p.RowHash)).ToList();
        if (sealedRows.Count == 0) return lastPersistedHash;
        var referenced = new HashSet<string>(
            sealedRows.Where(p => !string.IsNullOrEmpty(p.PrevHash)).Select(p => p.PrevHash!),
            StringComparer.OrdinalIgnoreCase);
        return sealedRows.LastOrDefault(p => !referenced.Contains(p.RowHash!))?.RowHash ?? sealedRows[^1].RowHash;
    }
}
