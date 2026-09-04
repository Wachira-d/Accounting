using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Accounting.Filters;

/// <summary>
/// **บังคับสิทธิ์ read/write/delete ของ API key — ด่านที่เขียนไว้แล้วแต่ไม่เคยมีใครเรียก**
///
/// ═══ ที่มา (บั๊กจริง · ผลตรวจ F-05) ═══
/// <c>ApiKeyMiddleware</c> อ่านค่า <c>CanRead</c> / <c>CanWrite</c> / <c>CanDelete</c>
/// ของคีย์จากฐานข้อมูลแล้วเขียนลง <c>HttpContext.Items</c> ครบทุกตัว — แต่
/// <b>grep ทั้งเรพไม่มีใครอ่านเลยสักจุด</b> ⇒ คีย์ที่แอดมินตั้งเป็น "อ่านอย่างเดียว"
/// (<c>CanWrite</c> default = false) <b>เขียนและลบข้อมูลได้เต็ม</b> — หน้าจอบอก
/// อย่าง ระบบทำอีกอย่าง
///
/// <para>defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" — control ที่เป็น
/// "ค่าที่เก็บไว้" แล้วไม่มีใครอ่าน = ไม่มี control (CLAUDE.md กฎเหล็ก #4)</para>
///
/// ═══ ทำไมเป็น global filter ═══
/// คีย์ยิงเข้าได้ทุก endpoint ที่ <c>[Authorize]</c> ยอมรับ — ใส่ทีละคอนโทรลเลอร์
/// แปลว่าคอนโทรลเลอร์ตัวถัดไปที่ใครเขียนเพิ่มจะไม่มีด่านโดยอัตโนมัติ
/// (บทเรียน "ด่านที่ไล่ไม่ครบทุกทางเข้า") · ผู้ใช้ที่ล็อกอินด้วย JWT ปกติ
/// ไม่ได้ตั้ง <c>IsApiKeyAuth</c> จึงผ่านฟิลเตอร์นี้ไปเฉย ๆ
/// </summary>
public sealed class ApiKeyScopeFilter : IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var items = context.HttpContext.Items;
        if (items["IsApiKeyAuth"] is not true) return Task.CompletedTask;

        var method = context.HttpContext.Request.Method;
        var (needed, label) = method switch
        {
            "GET" or "HEAD" or "OPTIONS" => ("ApiKeyCanRead", "อ่านข้อมูล"),
            "DELETE" => ("ApiKeyCanDelete", "ลบข้อมูล"),
            _ => ("ApiKeyCanWrite", "เขียนข้อมูล"),   // POST · PUT · PATCH
        };

        // ค่าที่ middleware ไม่ได้ตั้ง (คีย์รุ่นเก่า / เส้นที่ยังไม่ผ่าน middleware)
        // ถือว่า **ไม่อนุญาต** — fail closed. คีย์ที่ควรทำได้ต้องมีค่า true จริง
        // ในฐานข้อมูล ไม่ใช่ผ่านเพราะ "ไม่มีข้อมูล"
        if (items[needed] is true) return Task.CompletedTask;

        context.Result = new ObjectResult(new
        {
            success = false,
            message = $"API key นี้ไม่มีสิทธิ์{label} — แก้สิทธิ์ของคีย์ในหน้าตั้งค่า API",
            requiredScope = needed.Replace("ApiKey", ""),
        })
        { StatusCode = StatusCodes.Status403Forbidden };
        return Task.CompletedTask;
    }
}
