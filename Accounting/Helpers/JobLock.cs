using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ล็อก "งานเบื้องหลังตัวนี้ต้องเดินทีละเครื่อง" — ตัวเดียวของระบบ**
///
/// ═══ ที่มา (ผลตรวจ F-09) ═══
/// จาก 16 job มีแค่ <b>5 ตัว</b> ที่ล็อก ที่เหลือ 11 ตัวรันพร้อมกันได้ทุกเครื่อง
/// และผลไม่ใช่แค่ "ทำงานซ้ำเปลืองแรง" — หลายตัว<b>เขียนข้อมูลจริง</b>:
/// <list type="bullet">
///   <item><c>DepreciationBackgroundService</c> — ลง JE ค่าเสื่อม<b>ซ้ำสองเท่า</b>
///     ⇒ ค่าใช้จ่ายเกิน · ค่าเสื่อมสะสมเกิน · กำไรต่ำกว่าจริงในงบที่ยื่น</item>
///   <item><c>EclAllowanceJob</c> — ตั้งค่าเผื่อหนี้สงสัยจะสูญซ้ำ</item>
///   <item><c>RecurringLateFeeAccrualJob</c> — คิดค่าปรับล่าช้าซ้ำ = เก็บลูกค้าเกิน</item>
///   <item><c>OverdueDunningJob</c> / <c>BankUnmatchedDigestJob</c> /
///     <c>EmailScheduleWorker</c> / <c>AccountPlanExpiryReminderJob</c> —
///     ส่งอีเมลถึงลูกค้า<b>ซ้ำ N เท่าตามจำนวนเครื่อง</b></item>
///   <item><c>PdpaRetentionPurgeJob</c> / <c>ChatRetentionPurgeJob</c> —
///     ลบข้อมูลพร้อมกันสองเครื่อง</item>
/// </list>
///
/// <para>ใช้ <b>try</b> ไม่ใช่ wait: งานตามตารางที่อีกเครื่องกำลังทำอยู่ การรอคือ
/// การทำงานเดิมซ้ำเปล่า ๆ — ข้ามไปรอบหน้าถูกกว่าเสมอ</para>
///
/// <para>ต้องเป็น <b>session-level</b> lock (<c>pg_try_advisory_lock</c>) ไม่ใช่
/// xact lock เพราะ job ทั่วไป <c>SaveChanges</c> หลายครั้ง ไม่ได้อยู่ในธุรกรรม
/// เดียว ⇒ xact lock จะถูกปล่อยตั้งแต่คำสั่งแรกจบ</para>
/// </summary>
public static class JobLock
{
    /// <summary>
    /// รัน <paramref name="work"/> เมื่อได้ล็อกเท่านั้น · คืน <c>false</c> เมื่อ
    /// instance อื่นถืออยู่ (ผู้เรียกไม่ต้องทำอะไรต่อ — รอบหน้าค่อยว่ากัน)
    /// </summary>
    /// <param name="db">DbContext ของ scope นั้น — ล็อกผูกกับ connection ตัวนี้
    /// จึงต้องเป็นตัวเดียวกับที่งานใช้เขียนข้อมูล</param>
    /// <param name="scope">ชนิดงานจาก <see cref="AdvisoryLockKey"/></param>
    /// <param name="part">ตัวแบ่งภายในชนิดเดียวกัน (เช่น CompanyId / งวด) —
    /// งานที่เดินทีเดียวทุก tenant ใช้ <c>"global"</c></param>
    public static async Task<bool> RunExclusiveAsync(
        AccountingDbContext db,
        string scope,
        string part,
        Func<Task> work,
        ILogger? logger = null,
        Guid companyId = default,
        CancellationToken ct = default)
    {
        var lockKey = AdvisoryLockKey.For(companyId, scope, part);
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere) await conn.OpenAsync(ct);

        bool acquired;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT pg_try_advisory_lock({lockKey})";
            acquired = Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
        }

        if (!acquired)
        {
            logger?.LogInformation(
                "ข้ามรอบนี้ — instance อื่นกำลังทำงาน {Scope}/{Part} อยู่ (lock {Key})",
                scope, part, lockKey);
            if (openedHere) await conn.CloseAsync();
            return false;
        }

        Exception? workError = null;
        try
        {
            await work();
            return true;
        }
        catch (Exception ex)
        {
            workError = ex;
            throw;
        }
        finally
        {
            // ปลดล็อกเสมอแม้ตัวงานจะโยน — ไม่งั้นล็อกค้างจนกว่า connection จะถูกคืน
            // pool (ซึ่งอาจเป็นชั่วโมง) แล้วทั้งคลัสเตอร์ข้ามงานนี้ไปเรื่อย ๆ
            // รอบ 194 R2: connection พังระหว่างงาน ⇒ คำสั่งปลดล็อกก็ล้ม — เดิม exception ของการปลดล็อก "ทับ" error เดิมของงาน
            // (ผู้ใช้เห็น "connection closed" แทนเหตุจริง) ⇒ งานล้มอยู่แล้ว: log แล้วปล่อย error เดิมไหลต่อ (session ที่พัง = ล็อกหลุดเองฝั่ง DB) ·
            // งานสำเร็จแต่ปลดล็อกล้ม = โยนตามเดิม (ล็อกอาจค้างบน connection ใน pool — ต้องดัง)
            try
            {
                await using var unlock = conn.CreateCommand();
                unlock.CommandText = $"SELECT pg_advisory_unlock({lockKey})";
                await unlock.ExecuteScalarAsync(CancellationToken.None);
                if (openedHere) await conn.CloseAsync();
            }
            catch (Exception unlockError) when (workError != null)
            {
                logger?.LogError(unlockError,
                    "ปลดล็อก {Scope}/{Part} (lock {Key}) ไม่สำเร็จหลังงานล้ม — คง error เดิมของงานไว้: {WorkError}",
                    scope, part, lockKey, workError.Message);
            }
        }
    }

    /// <summary>
    /// ล็อกระดับ<b>ธุรกรรม</b>แบบไม่รอ (<c>pg_try_advisory_xact_lock</c>) — ปลดเองตอน commit/rollback · คืน <c>false</c> เมื่อ session อื่นถือคีย์นี้อยู่
    /// (ทั้ง session lock จาก <see cref="RunExclusiveAsync"/> และ xact lock) · session เดียวกันที่ถือ session lock คีย์นี้อยู่แล้วได้ทันที
    /// <para>ไม่รอ (try) เพราะผู้เรียกอยู่กลางธุรกรรมที่ถือล็อกอื่นแล้ว (เลขเอกสาร/เลข JE/แถว) — การรอคีย์ที่อีก session ถือขณะมันรอล็อกของเรา = deadlock ·
    /// ล้มเร็วพร้อมข้อความ "รอสักครู่แล้วทำใหม่" ปลอดภัยกว่า (รอบ 194 R2 · P-a)</para>
    /// </summary>
    public static async Task<bool> TryXactLockAsync(AccountingDbContext db, long lockKey, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("TryXactLockAsync ต้องเรียกภายในธุรกรรม — นอกธุรกรรมล็อกถูกปลดทันทีหลังคำสั่ง (ไม่กันอะไร)");
        return await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock({0}) AS \"Value\"", lockKey).SingleAsync(ct);
    }
}
