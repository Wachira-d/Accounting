using System;
using System.Collections.Generic;

namespace Accounting.Helpers;

/// <summary>สิทธิ์ที่ request หนึ่งต้องใช้ — ตัดสินจาก HTTP method ตัวเดียวทั้งระบบ
/// (ทั้งคีย์ <c>acc_</c> ผ่าน <c>ApiKeyScopeFilter</c> และคีย์ <c>int_</c> ทั้งสองทางเข้า)</summary>
public enum ApiKeyScope
{
    Read = 0,
    Write = 1,
    Delete = 2,
}

/// <summary>สิทธิ์ของคีย์หนึ่งดอก (อ่าน/เขียน/ลบ แยกกัน · เขียนได้ ≠ ลบได้)</summary>
public readonly record struct IntegrationKeyScopes(bool CanRead, bool CanWrite, bool CanDelete)
{
    /// <summary>สิทธิ์เต็ม — ใช้เฉพาะคีย์รุ่นเก่าที่ยังอยู่ในช่วงผ่อนผัน</summary>
    public static IntegrationKeyScopes Full => new(true, true, true);

    /// <summary>ค่าเริ่มต้นของคีย์ที่ออกใหม่: อ่านอย่างเดียว (เจ้าของเลือกเพิ่มเอง)</summary>
    public static IntegrationKeyScopes ReadOnly => new(true, false, false);
}

/// <summary>ทางที่ใช้ตีความ header <c>X-Acting-User</c> ของ request นี้</summary>
public enum ActingUserPath
{
    /// <summary>ไม่ได้ส่ง header มา</summary>
    None = 0,

    /// <summary>ตรงกับแถว <c>IntegrationUserMapping</c> ที่เจ้าของผูกไว้ + ยังเป็นสมาชิกบริษัท</summary>
    Mapping = 1,

    /// <summary>คีย์รุ่นเก่าในช่วงผ่อนผัน — จับคู่ด้วยอีเมลสมาชิก (เส้นเดิม) · ต้องทิ้งร่องรอยทุกครั้ง</summary>
    LegacyEmailMatch = 2,

    /// <summary>ส่ง header มาแต่ไม่มีการผูก ⇒ **ไม่สวมใคร** (identity = ตัว integration)</summary>
    Unmapped = 3,
}

/// <param name="UserId">ผู้ใช้ที่ request นี้ทำงานแทน (null = ไม่สวมใคร)</param>
/// <param name="Path">ทางที่ใช้ตัดสิน — middleware ใช้ log/audit และบอกคู่ค้าใน response header</param>
public sealed record ActingUserDecision(Guid? UserId, ActingUserPath Path);

/// <summary>
/// **นโยบายคีย์ integration (<c>int_</c>) ตัวเดียวของระบบ** — ผลตรวจ G2-01 · คำตัดสินเจ้าของข้อ 37 (รอบ 193)
///
/// <para>═══ ที่มา (บั๊กจริง) ═══ สมาชิกคนไหนก็ออกคีย์ได้ · middleware ตั้ง
/// <c>ApiKeyCanRead/Write/Delete = true</c> ตายตัว ⇒ <c>ApiKeyScopeFilter</c> เป็น no-op สำหรับคีย์ชนิดนี้ ·
/// แถม <c>X-Acting-User: &lt;อีเมลเจ้าของ&gt;</c> ทำให้ NameIdentifier กลายเป็นเจ้าของ ⇒ พนักงาน "ดูอย่างเดียว"
/// ยกระดับเป็นเจ้าของบริษัทได้ด้วยคีย์ที่ตัวเองออกเอง</para>
///
/// <para>═══ คำตัดสินเจ้าของ ═══
/// (1) คีย์ที่มีอยู่แล้ว = <b>รุ่นเก่า (legacy)</b> คงสิทธิ์เต็ม + สวมด้วยอีเมลได้ต่อ <b>จนถึงวันเลิกใช้</b>
/// (<see cref="LegacyGraceDays"/> วันนับจากวันที่ migration รัน) — ไม่ทำ TakeTime พังวันนี้ ·
/// (2) คีย์ใหม่ = เจ้าของออกเท่านั้น · ค่าเริ่มต้นอ่านอย่างเดียว · สิทธิ์แยก อ่าน/เขียน/ลบ ·
/// สวมได้เฉพาะผู้ใช้ที่เจ้าของผูกไว้ใน <c>IntegrationUserMapping</c></para>
///
/// <para>หลังวันเลิกใช้ คีย์รุ่นเก่าใช้ "สิทธิ์ที่เก็บไว้" (ค่าที่ migration ใส่ = อ่านอย่างเดียว) ⇒ การเขียนของคู่ค้า
/// ได้ 403 พร้อมข้อความชี้ทาง ไม่ใช่เงียบ — ทิศที่ "มองเห็นและแก้ทัน" (DECISION_DOCTRINE §1)</para>
/// </summary>
public static class IntegrationKeyPolicy
{
    /// <summary>ช่วงผ่อนผันของคีย์รุ่นเก่า (วัน) — <b>ตัวตั้งตัวเดียว</b>: migration ใช้ค่านี้คำนวณ
    /// <c>LegacyDeprecatesAt</c> และหน้าเว็บแสดงวันที่ที่เซิร์ฟเวอร์คำนวณแล้ว (ไม่มีสำเนาใน JS)</summary>
    public const int LegacyGraceDays = 90;

    /// <summary>คีย์รุ่นเก่ายังได้สิทธิ์แบบเดิมอยู่ไหม — <b>ไม่รู้วันเลิกใช้ = ไม่ได้</b>
    /// (เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล ห้ามตกเป็น "ผ่าน" · DECISION_DOCTRINE §1)</summary>
    public static bool IsLegacyPrivilegeActive(bool isLegacyKey, DateTime? legacyDeprecatesAtUtc, DateTime nowUtc)
        => isLegacyKey && legacyDeprecatesAtUtc.HasValue && nowUtc < legacyDeprecatesAtUtc.Value;

    /// <summary>สิทธิ์ที่บังคับใช้จริงกับ request — คีย์รุ่นเก่าในช่วงผ่อนผันได้สิทธิ์เต็ม · นอกนั้นใช้ค่าที่เก็บไว้</summary>
    public static IntegrationKeyScopes EffectiveScopes(
        bool isLegacyKey, DateTime? legacyDeprecatesAtUtc, IntegrationKeyScopes stored, DateTime nowUtc)
        => IsLegacyPrivilegeActive(isLegacyKey, legacyDeprecatesAtUtc, nowUtc) ? IntegrationKeyScopes.Full : stored;

    /// <summary>สิทธิ์ที่ HTTP method ต้องใช้ — GET/HEAD/OPTIONS = อ่าน · DELETE = ลบ · อื่น ๆ (POST/PUT/PATCH) = เขียน
    /// · method ที่ไม่รู้จักถือเป็น "เขียน" (fail closed)</summary>
    public static ApiKeyScope RequiredScope(string? httpMethod)
        => (httpMethod ?? "").Trim().ToUpperInvariant() switch
        {
            "GET" or "HEAD" or "OPTIONS" => ApiKeyScope.Read,
            "DELETE" => ApiKeyScope.Delete,
            _ => ApiKeyScope.Write,
        };

    /// <summary>คีย์ที่มีสิทธิ์ <paramref name="scopes"/> ทำ request ด้วย <paramref name="httpMethod"/> ได้ไหม</summary>
    public static bool Allows(IntegrationKeyScopes scopes, string? httpMethod)
        => RequiredScope(httpMethod) switch
        {
            ApiKeyScope.Read => scopes.CanRead,
            ApiKeyScope.Delete => scopes.CanDelete,
            _ => scopes.CanWrite,
        };

    /// <summary>ป้ายไทยของสิทธิ์ (ใช้ในข้อความ 403 ทุกทางเข้า)</summary>
    public static string ScopeLabel(ApiKeyScope scope) => scope switch
    {
        ApiKeyScope.Read => "อ่านข้อมูล",
        ApiKeyScope.Delete => "ลบข้อมูล",
        _ => "เขียนข้อมูล",
    };

    /// <summary>ชื่อสิทธิ์ที่ส่งให้คู่ค้าใน body ของ 403 (ตรงกับชื่อคอลัมน์)</summary>
    public static string ScopeName(ApiKeyScope scope) => scope switch
    {
        ApiKeyScope.Read => "CanRead",
        ApiKeyScope.Delete => "CanDelete",
        _ => "CanWrite",
    };

    /// <summary>สิทธิ์ของคีย์ที่ออกใหม่จากคำขอของเจ้าของ — ช่องที่ไม่ได้ส่งมา = ค่าเริ่มต้นอ่านอย่างเดียว
    /// (ห้ามตีความ "ไม่ได้ส่ง" เป็น "ให้ทั้งหมด")</summary>
    public static IntegrationKeyScopes ScopesForNewKey(bool? canRead, bool? canWrite, bool? canDelete)
    {
        var d = IntegrationKeyScopes.ReadOnly;
        return new IntegrationKeyScopes(canRead ?? d.CanRead, canWrite ?? d.CanWrite, canDelete ?? d.CanDelete);
    }

    /// <summary>การแก้สิทธิ์ของคีย์ที่มีอยู่: ช่องที่ไม่ได้ส่งมาคงค่าเดิม ·
    /// <c>Changed</c> = มีการส่งสิทธิ์มาอย่างน้อยหนึ่งช่อง ⇒ คีย์รุ่นเก่า<b>ย้ายเข้านโยบายใหม่ทันที</b>
    /// (เจ้าของเลือกสิทธิ์เองแล้ว ช่วงผ่อนผันหมดความหมาย)</summary>
    public static (IntegrationKeyScopes Scopes, bool Changed) ApplyScopeUpdate(
        IntegrationKeyScopes current, bool? canRead, bool? canWrite, bool? canDelete)
    {
        var changed = canRead.HasValue || canWrite.HasValue || canDelete.HasValue;
        return (new IntegrationKeyScopes(
            canRead ?? current.CanRead, canWrite ?? current.CanWrite, canDelete ?? current.CanDelete), changed);
    }

    /// <summary>
    /// ตัดสินว่า request นี้ "ทำงานแทนผู้ใช้คนไหน" จาก <c>X-Acting-User</c>
    ///
    /// <para>ลำดับ: (1) แถวที่เจ้าของผูกไว้ (ต้องยังเป็นสมาชิก — ผู้เรียกตรวจก่อนส่งเข้ามา) ชนะเสมอ ·
    /// (2) จับคู่ด้วยอีเมล <b>เฉพาะคีย์รุ่นเก่าในช่วงผ่อนผัน</b> · (3) นอกนั้น = ไม่สวมใคร</para>
    ///
    /// <para>⚠️ attribution ห้ามกลายเป็น authorization: คีย์ใหม่ส่งอีเมลเจ้าของมาก็<b>ไม่ได้</b>เป็นเจ้าของ
    /// ต่อให้ผู้เรียกบังเอิญหา email match มาให้ก็ตาม (ตัดสินที่นี่ ไม่ใช่ที่ query)</para>
    /// </summary>
    /// <param name="hasActingHeader">คู่ค้าส่ง header มาหรือไม่</param>
    /// <param name="mappedMemberUserId">ผลจาก IntegrationUserMapping ที่ผ่านการตรวจสมาชิกแล้ว</param>
    /// <param name="legacyPrivilegeActive">ผลของ <see cref="IsLegacyPrivilegeActive"/></param>
    /// <param name="emailMatchedMemberUserId">สมาชิกบริษัทที่อีเมลตรงกับ header (ผู้เรียกควรหาเฉพาะเมื่อ legacy)</param>
    public static ActingUserDecision ResolveActingUser(
        bool hasActingHeader, Guid? mappedMemberUserId, bool legacyPrivilegeActive, Guid? emailMatchedMemberUserId)
    {
        if (!hasActingHeader) return new ActingUserDecision(null, ActingUserPath.None);
        if (mappedMemberUserId.HasValue) return new ActingUserDecision(mappedMemberUserId, ActingUserPath.Mapping);
        if (legacyPrivilegeActive && emailMatchedMemberUserId.HasValue)
            return new ActingUserDecision(emailMatchedMemberUserId, ActingUserPath.LegacyEmailMatch);
        return new ActingUserDecision(null, ActingUserPath.Unmapped);
    }

    /// <summary>ควรเสีย query ไปหา email match ไหม — เฉพาะคีย์รุ่นเก่าในช่วงผ่อนผัน · ไม่มีแถวผูก · header เป็นอีเมล</summary>
    public static bool ShouldTryLegacyEmailMatch(bool legacyPrivilegeActive, Guid? mappedMemberUserId, string? actingKey)
        => legacyPrivilegeActive && !mappedMemberUserId.HasValue
           && !string.IsNullOrWhiteSpace(actingKey) && actingKey.Contains('@');

    /// <summary>ค่า response header <c>X-Acting-User-Resolved</c> — บอกคู่ค้าตรง ๆ ว่า header ของเขาถูกตีความยังไง
    /// (ส่งมาแต่ไม่ถูกผูก ต้องไม่เงียบ)</summary>
    public static string ResolvedHeaderValue(ActingUserPath path) => path switch
    {
        ActingUserPath.Mapping => "mapping",
        ActingUserPath.LegacyEmailMatch => "legacy-email-match",
        ActingUserPath.Unmapped => "unmapped",
        _ => "none",
    };

    /// <summary>
    /// SQL migration ของคอลัมน์สิทธิ์/ธงรุ่นเก่า — <b>idempotent</b> (รันทุกครั้งที่เปิดเครื่อง)
    ///
    /// <para>เทคนิค "default ตอน ADD ≠ default หลังจากนั้น": ADD COLUMN ใส่ค่าให้<b>แถวที่มีอยู่แล้ว</b>
    /// (= คีย์รุ่นเก่า) จากนั้นเปลี่ยน default เป็นค่าของคีย์ใหม่ · รันซ้ำ ADD เป็น no-op จึงไม่มีทางย้อนไปติดป้าย
    /// คีย์ใหม่ว่าเป็นรุ่นเก่า · UPDATE วันเลิกใช้แตะเฉพาะแถวรุ่นเก่าที่ยังไม่มีวัน ⇒ รันซ้ำไม่เลื่อนวัน</para>
    ///
    /// <para>สิทธิ์ที่เก็บของแถวเดิม = อ่านอย่างเดียว (ค่าหลังช่วงผ่อนผัน) — ช่วงผ่อนผันให้สิทธิ์เต็มผ่าน
    /// <see cref="EffectiveScopes"/> ไม่ใช่ผ่านค่าที่เก็บ ⇒ วันเลิกใช้มีผลจริงโดยไม่ต้องมีงานกลางคืน</para>
    /// </summary>
    public static IReadOnlyList<string> MigrationStatements() => new[]
    {
        """ALTER TABLE "ExternalIntegrations" ADD COLUMN IF NOT EXISTS "CanRead" boolean NOT NULL DEFAULT true;""",
        """ALTER TABLE "ExternalIntegrations" ADD COLUMN IF NOT EXISTS "CanWrite" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "ExternalIntegrations" ADD COLUMN IF NOT EXISTS "CanDelete" boolean NOT NULL DEFAULT false;""",
        // แถวที่มีอยู่ ณ วันที่เพิ่มคอลัมน์ = คีย์รุ่นเก่า (true) · หลังจากนั้น default = false
        """ALTER TABLE "ExternalIntegrations" ADD COLUMN IF NOT EXISTS "IsLegacyKey" boolean NOT NULL DEFAULT true;""",
        """ALTER TABLE "ExternalIntegrations" ALTER COLUMN "IsLegacyKey" SET DEFAULT false;""",
        """ALTER TABLE "ExternalIntegrations" ADD COLUMN IF NOT EXISTS "LegacyDeprecatesAt" timestamp NULL;""",
        $"""UPDATE "ExternalIntegrations" SET "LegacyDeprecatesAt" = timezone('UTC', now()) + interval '{LegacyGraceDays} days' WHERE "IsLegacyKey" = true AND "LegacyDeprecatesAt" IS NULL;""",
    };
}
