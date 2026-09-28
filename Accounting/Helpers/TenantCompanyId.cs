using Microsoft.AspNetCore.Http;

namespace Accounting.Helpers;

/// <summary>บริษัทของคำขอมาจากไหน</summary>
public enum TenantCompanySource
{
    None = 0,
    /// <summary>พารามิเตอร์ route <c>{companyId}</c> (<c>/api/companies/{companyId}/…</c>) — หน้าเว็บทุกหน้า</summary>
    Route = 1,
    /// <summary>header <c>X-Company-Id</c> — partner/integration ที่ส่งมาเอง (api.js ไม่ส่ง)</summary>
    Header = 2,
}

/// <summary>ผลการหาบริษัทของคำขอ</summary>
/// <param name="CompanyId">บริษัทที่คำขอนี้แตะข้อมูลจริง — route ชนะ header เสมอ (ตัวเดียวกับที่
/// <c>TenantAccessMiddleware</c> ตรวจว่าผู้ใช้เป็นสมาชิก)</param>
/// <param name="Source">ได้มาจาก route หรือ header</param>
/// <param name="HeaderCarried">คำขอส่ง <c>X-Company-Id</c> ที่เป็น GUID มาด้วยไหม (ไม่ว่าจะตรงกับ route หรือไม่)</param>
/// <param name="HeaderDisagreesWithRoute">header กับ route ชี้คนละบริษัท — ใช้ route (header ถูกทิ้ง)</param>
public readonly record struct TenantCompanyTarget(
    Guid? CompanyId, TenantCompanySource Source, bool HeaderCarried, bool HeaderDisagreesWithRoute);

/// <summary>
/// <b>ตัวหาบริษัทของคำขอตัวเดียว</b> ของ middleware ทั้งสองตัว (<c>TenantAccessMiddleware</c> ·
/// <c>SubscriptionCheckMiddleware</c>) — รอบ 198 ข้อ 5
///
/// <para>ที่มา: <c>SubscriptionCheckMiddleware</c> เคยรู้บริษัทจาก <c>X-Company-Id</c> อย่างเดียว ขณะที่
/// <c>TenantAccessMiddleware</c> อ่าน route ก่อนแล้วค่อย header ⇒ (ก) หน้าเว็บไม่เคยถูก gate แพ็กเกจ/ระงับบริษัทเลย
/// เพราะ api.js ไม่ส่ง header (ข) คำขอที่ส่ง header ของบริษัท B มากับ route ของบริษัท A ผ่านด่านสมาชิกด้วย A
/// แต่ถูกตัดสินแพ็กเกจด้วย B — ปลอม header เพื่อยืมแพ็กเกจของบริษัทอื่นได้. สองตัวต้องตอบ "บริษัทไหน" ตรงกันเสมอ
/// ⇒ อยู่ที่นี่ที่เดียว</para>
///
/// <para>ลำดับ: route ก่อน header (พฤติกรรมเดิมของ <c>TenantAccessMiddleware</c>) · ค่าที่ไม่ใช่ GUID = ไม่มี</para>
/// </summary>
public static class TenantCompanyId
{
    public const string HeaderName = "X-Company-Id";
    public const string RouteKey = "companyId";

    /// <summary>ตัวตัดสินบริสุทธิ์ — รับค่าดิบจาก route/header (ทดสอบได้โดยไม่มี HttpContext)</summary>
    public static TenantCompanyTarget Resolve(object? routeValue, string? headerValue)
    {
        var route = Parse(routeValue?.ToString());
        var header = Parse(headerValue);
        if (route is Guid r)
            return new TenantCompanyTarget(r, TenantCompanySource.Route, header is not null, header is Guid h0 && h0 != r);
        if (header is Guid h)
            return new TenantCompanyTarget(h, TenantCompanySource.Header, true, false);
        return new TenantCompanyTarget(null, TenantCompanySource.None, false, false);
    }

    /// <summary>อ่านจากคำขอจริง — <c>RouteValues["companyId"]</c> (มีเมื่อ endpoint ถูกจับคู่แล้ว · WebApplication
    /// ใส่ UseRouting ไว้ต้น pipeline ให้เอง) และ header <c>X-Company-Id</c></summary>
    public static TenantCompanyTarget FromHttp(HttpContext context)
    {
        context.Request.RouteValues.TryGetValue(RouteKey, out var routeValue);
        string? header = context.Request.Headers.TryGetValue(HeaderName, out var hv) ? hv.ToString() : null;
        return Resolve(routeValue, header);
    }

    private static Guid? Parse(string? s) => Guid.TryParse(s, out var g) ? g : null;
}
