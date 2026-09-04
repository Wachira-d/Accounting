using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **เครื่องออก "เลขรัน" ตัวเดียวของระบบ สำหรับทุกอย่างที่ไม่ใช่เลขเอกสาร**
/// (เลขเอกสาร §86/4 ใช้ <see cref="DocumentNumberGenerator"/> · เลข JE ใช้
/// <c>JournalEntryBuilder.NextJournalNumberAsync</c> ซึ่งเรียกตัวนี้ต่ออีกที)
///
/// ═══ ที่มา (ผลตรวจ F-08) ═══
/// มีจุดที่ออกเลขเองด้วย <c>OrderByDescending(...).First() + 1</c> อยู่ราว
/// <b>25 จุด</b> กระจายทั่ว service — ทุกจุดพลาดเรื่องเดียวกัน 3 ข้อ:
/// <list type="number">
///   <item><b>ไม่มีล็อก</b> — สอง request (หรือสอง instance) อ่าน max ได้เลข
///     เดียวกัน แล้วเขียนทับกัน. ที่มีคือ unique index ⇒ อีกฝั่ง<b>ล้มดัง</b>
///     ("บันทึกไม่สำเร็จ" แบบสุ่มที่กดใหม่แล้วหาย) หรือแย่กว่านั้นคือ
///     <b>ไม่มี unique index</b> ⇒ เลขซ้ำเงียบ ๆ</item>
///   <item><b>เรียงแบบข้อความ</b> — <c>"...-9999" &gt; "...-10000"</c> ตาม
///     lexicographic ⇒ พอทะลุหลักพัน เลขจะ<b>วนกลับ</b>ไปทับของเดิม</item>
///   <item><b>ไม่นับแถวที่ยังค้างใน change tracker</b> — งานที่สร้างสองแถว
///     ในธุรกรรมเดียว (เช่นกลับรายการ + ตั้งใหม่) ได้เลขซ้ำกันเองทันที</item>
/// </list>
///
/// <para>⚠️ <b>ล็อกจะกันได้จริงก็ต่อเมื่อผู้เรียกอยู่ใน transaction</b> —
/// <c>pg_advisory_xact_lock</c> ปล่อยล็อกตอน commit/rollback. ถ้าเรียกนอก
/// transaction, PostgreSQL ถือว่าแต่ละคำสั่งเป็นธุรกรรมของตัวเอง ⇒ ล็อกถูก
/// ปล่อย<b>ทันทีที่ SELECT จบ</b> ก่อนแถวจะถูก insert เสียอีก. เมธอดนี้จึง
/// <b>เตือนดัง ๆ</b> (ไม่ throw เพราะจะทำให้เส้นที่เคยทำงานอยู่พังทันที) และ
/// ผู้เรียกที่ยังไม่มีธุรกรรมควรห่อด้วย <c>BeginTransactionAsync</c></para>
/// </summary>
public static class SequenceNumber
{
    /// <summary>จำนวนแถวล่าสุดที่ดึงมาหา max — พอสำหรับทุกกรณีจริงและกัน
    /// การสแกนทั้งตารางของ tenant ที่มีข้อมูลหลายปี</summary>
    public const int ScanWindow = 2000;

    /// <summary>logger สำหรับคำเตือน "ออกเลขนอก transaction" — ตั้งครั้งเดียวตอน
    /// boot ใน <c>Program.cs</c> · เป็น static เพราะตัวออกเลขเป็น helper ที่
    /// ทุกที่เรียกได้โดยไม่ผ่าน DI (เขียนครั้งเดียวตอนเริ่ม ไม่ใช่ state ต่อ request)</summary>
    public static ILogger? Log { get; set; }

    /// <summary>
    /// **ส่วนที่เป็นคณิตศาสตร์ล้วน** — หาลำดับถัดไปจากรายการ suffix ที่มีอยู่
    ///
    /// <para>ข้ามค่าที่แปลงเป็นจำนวนเต็มไม่ได้ (เลขที่ผู้ใช้พิมพ์เองสมัยก่อน /
    /// เลขนำเข้าจากระบบเก่า) — ไม่ทิ้ง ไม่ระเบิด แค่ไม่นับเป็นฐาน</para>
    /// </summary>
    public static int NextSequence(IEnumerable<string?> existingSuffixes)
    {
        var next = 1;
        foreach (var s in existingSuffixes)
        {
            if (s is null) continue;
            var t = s.Trim();
            if (t.Length == 0) continue;
            if (int.TryParse(t, out var n) && n >= next) next = n + 1;
        }
        return next;
    }

    /// <summary>จัดรูปเลข — ไม่ตัดหลักเมื่อทะลุความกว้างที่ตั้งไว้
    /// (<c>0001..9999</c> แล้วต่อด้วย <c>10000</c> ไม่ใช่วนกลับเป็น <c>0000</c>)</summary>
    public static string Format(string prefix, int sequence, int digits = 4)
        => prefix + sequence.ToString(new string('0', Math.Max(1, digits)));

    /// <summary>
    /// ออกเลขถัดไปของ number space หนึ่ง ๆ — ล็อก + integer-max + นับแถวที่ค้าง
    /// ใน change tracker
    /// </summary>
    /// <param name="db">DbContext ที่จะใช้ทั้งอ่านและเขียนแถวใหม่ (ต้องตัวเดียวกัน
    /// ไม่งั้นล็อกอยู่คนละ connection)</param>
    /// <param name="companyId">คีย์ล็อกผูกกับบริษัท — คนละบริษัทไม่ต้องรอกัน</param>
    /// <param name="scope">ชนิดของ number space จาก <see cref="AdvisoryLockKey"/></param>
    /// <param name="prefix">คำนำหน้าที่รวมงวดแล้ว เช่น <c>"PAY-202609-"</c></param>
    /// <param name="existingNumbers">เลขที่มีอยู่แล้วใน number space นี้ —
    /// ผู้เรียกกรอง <c>CompanyId</c> + <c>StartsWith(prefix)</c> มาให้เรียบร้อย
    /// และควรใส่ <c>IgnoreQueryFilters()</c> เพื่อให้เห็นแถวที่ soft-delete ด้วย
    /// (ไม่งั้นออกเลขทับของที่ยังจองที่อยู่)</param>
    /// <param name="pendingLocal">เลขในชุดเดียวกันที่ถูก Add ไว้แล้วแต่ยังไม่ save</param>
    /// <param name="lockPart">คีย์ล็อกเมื่อ number space ถูกแบ่งด้วยอย่างอื่น
    /// นอกจาก prefix (ค่าปกติ = ใช้ prefix)</param>
    public static async Task<string> NextAsync(
        AccountingDbContext db,
        Guid companyId,
        string scope,
        string prefix,
        IQueryable<string> existingNumbers,
        IEnumerable<string?>? pendingLocal = null,
        int digits = 4,
        string? lockPart = null,
        CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
        {
            // ไม่ throw — เส้นที่เคยทำงานอยู่ต้องไม่พังเพราะการเพิ่มด่าน แต่ต้อง
            // "ดัง" พอให้เห็นตอนตรวจ log (กติกา "ห้ามเงียบ" ของกฎเหล็ก #4)
            //
            // ⚠️ เดิมบรรทัดนี้เป็น `Debug.WriteLine` ซึ่ง **คอมไพล์หายไปใน Release**
            // ⇒ คำเตือนที่ตั้งใจให้ดัง กลับเงียบสนิทบนเครื่องจริง — ตรงข้ามกับ
            // คอมเมนต์ที่เขียนไว้เอง (ญาติของบทเรียน "LogWarning แล้วเดินต่อ":
            // ดังในที่ที่ไม่มีคนดู = ไม่ดัง) จึงยิงผ่าน logger จริง และถ้ายังไม่มี
            // ใครตั้ง ก็ออก stderr ไว้ก่อน — ห้ามมีทางที่มันหายไปทั้งหมด
            var msg = $"[SequenceNumber] ออกเลข '{prefix}' นอก transaction — advisory lock "
                + "จะถูกปล่อยก่อน insert ⇒ กันเลขซ้ำได้แค่ unique index เท่านั้น";
            if (Log is { } log) log.LogWarning("{Message}", msg);
            else Console.Error.WriteLine(msg);
        }

        // lockPart แยกจาก prefix เพราะ number space บางชุดถูกแบ่งด้วยคีย์อื่น
        // ที่ไม่ได้อยู่ในตัวเลข (เช่นเว็บไซต์ของ CMS: เลขนับแยกต่อ SiteId
        // แต่ prefix บนกระดาษเหมือนกัน) — ใช้ prefix เป็นคีย์เฉย ๆ จะทำให้
        // สองเว็บของบริษัทเดียวกันรอกันโดยไม่จำเป็น
        var lockKey = AdvisoryLockKey.For(companyId, scope, lockPart ?? prefix);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);

        var suffixes = await existingNumbers
            .Select(n => n.Substring(prefix.Length))
            .Take(ScanWindow)
            .ToListAsync(ct);

        var next = NextSequence(suffixes);

        if (pendingLocal != null)
        {
            var localNext = NextSequence(pendingLocal
                .Where(v => v != null && v.StartsWith(prefix, StringComparison.Ordinal))
                .Select(v => v![prefix.Length..]));
            if (localNext > next) next = localNext;
        }

        return Format(prefix, next, digits);
    }
}
