namespace Accounting.Services.Interfaces;

/// <summary>ตัวนับ rate limit ของ chatbot ที่ใช้ร่วมกันได้ **ข้าม instance**
/// (เดิมเป็น in-memory ต่อ process — deploy 2 instance = เพดานคูณสอง).
/// เก็บลง `ChatRateBuckets` ด้วย atomic upsert ของ PostgreSQL จึงนับตรงแม้
/// หลาย request เข้าพร้อมกัน. Fail-open: DB มีปัญหา → ปล่อยผ่าน (ไม่ล็อก
/// ผู้ใช้ทั้งระบบเพราะตารางนับพัง) แล้วอาศัยด่านอื่น (เพดานห้อง/วัน + budget).</summary>
public interface IChatRateLimiter
{
    /// <summary>นับ 1 ครั้งแล้วบอกว่ายังอยู่ในโควตาไหม (นาที + วัน).</summary>
    Task<bool> TryConsumeAsync(string key, int perMinute, int perDay, CancellationToken ct = default);

    /// <summary>นับ "strike" (จำนวนครั้งที่ชนเพดาน) ในหน้าต่าง 1 ชม.
    /// — ใช้ยกระดับเป็น challenge เมื่อยิงรัวซ้ำ ๆ</summary>
    Task<int> RecordStrikeAsync(string key, CancellationToken ct = default);

    /// <summary>ลบ bucket เก่ากว่า 2 วัน (เรียกจาก purge job).</summary>
    Task<int> PurgeOldAsync(CancellationToken ct = default);
}
