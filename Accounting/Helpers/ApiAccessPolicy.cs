namespace Accounting.Helpers;

/// <summary>ผลตัดสินว่าคีย์ <c>acc_</c> ของบริษัทหนึ่งใช้งานได้ไหมตามสวิตช์ของเจ้าของ</summary>
/// <param name="Allowed">true = ผ่าน</param>
/// <param name="Message">ข้อความไทยที่ส่งถึงผู้เรียกเมื่อไม่ผ่าน (null เมื่อผ่าน)</param>
public readonly record struct ApiAccessDecision(bool Allowed, string? Message)
{
    public string? RuleCode => Allowed ? null : ApiAccessPolicy.RuleCode;

    /// <summary>HTTP status ที่ middleware ต้องตอบเมื่อไม่ผ่าน — 403 (ยืนยันตัวได้ แต่เจ้าของปิดทางเข้านี้)
    /// ไม่ใช่ 401 (คีย์ถูกต้อง การขอคีย์ใหม่ไม่ช่วย) · 200 = ปล่อยผ่านไปขั้นถัดไป</summary>
    public int StatusCode => Allowed ? 200 : 403;
}

/// <summary>
/// **สวิตช์ "เปิดใช้งาน API Access" (<c>CompanySettings.EnableApiAccess</c>) ตัวตัดสินตัวเดียว** — ผลตรวจ S-04 (รอบ 193)
///
/// <para>═══ บั๊กจริง ═══ สวิตช์ถูกตรวจแค่ตอน<b>ออกคีย์</b> (<c>SettingsService.CreateApiKeyAsync</c>) ·
/// <c>ApiKeyMiddleware</c> รับคีย์ทุกดอกที่ <c>Status = Active</c> ⇒ เจ้าของสงสัยคีย์รั่วแล้วกดปิดสวิตช์ → คู่ค้ายังอ่าน/เขียน
/// ข้อมูลได้ต่อ = ค่าตั้งที่ผู้ใช้กดแล้วไม่มีผล (กฎเหล็ก #4 A "ห้าม silent no-op")</para>
///
/// <para>ขอบเขต: <b>คีย์ <c>acc_</c> (ตาราง <c>ApiKeys</c>) เท่านั้น</b> — คีย์ integration <c>int_</c>
/// (ตาราง <c>ExternalIntegrations</c>) มีสวิตช์ของตัวเอง (<c>IsActive</c> + <c>IntegrationKeyPolicy</c>)
/// ที่หน้า "เชื่อมต่อระบบ" และหน้าตั้งค่าบอกผู้ใช้ไว้แล้วว่าสองชนิดนี้แยกกัน</para>
///
/// <para>"ไม่รู้" (ไม่มีแถว <c>CompanySettings</c>) = <b>ไม่ผ่าน</b> — ตรงกับค่าเริ่มต้นของ entity (<c>false</c>)
/// และคีย์ <c>acc_</c> ออกได้เฉพาะเมื่อมีแถวที่เปิดสวิตช์อยู่แล้ว ⇒ ไม่มีแถว = ข้อมูลผิดปกติ ห้ามตกเป็น "ผ่าน"
/// (DECISION_DOCTRINE §1)</para>
/// </summary>
public static class ApiAccessPolicy
{
    /// <summary>รหัสกฎสำหรับ log/audit/response</summary>
    public const string RuleCode = "SET-API-ACCESS-OFF";

    /// <summary>ข้อความถึงคู่ค้า/สคริปต์ที่ยังถือคีย์เดิม — บอกทั้งสาเหตุและทางไปต่อ</summary>
    public const string KeyDisabledMessage =
        "บริษัทนี้ปิดการเข้าถึงผ่าน API ไว้ — คีย์ที่ออกไว้แล้วจึงใช้งานไม่ได้ · "
        + "ให้เจ้าของบริษัทเปิด \"เปิดใช้งาน API Access\" ที่หน้าตั้งค่า → แท็บ \"อนุมัติ\" → หัวข้อ API Access ก่อน";

    /// <summary>ข้อความตอนพยายามออกคีย์ใหม่ขณะสวิตช์ปิด</summary>
    public const string IssueDisabledMessage =
        "API access ยังไม่เปิดใช้งาน กรุณาเปิด \"เปิดใช้งาน API Access\" ในหน้าตั้งค่าก่อนสร้าง API key";

    /// <summary>ตัดสินคำขอที่มากับคีย์ <c>acc_</c> · <paramref name="enableApiAccess"/> = ค่าของบริษัทเจ้าของคีย์
    /// (null = ไม่มีแถวค่าตั้ง)</summary>
    public static ApiAccessDecision EvaluateAccountKey(bool? enableApiAccess)
        => enableApiAccess == true
            ? new ApiAccessDecision(true, null)
            : new ApiAccessDecision(false, KeyDisabledMessage);

    /// <summary>ออกคีย์ <c>acc_</c> ใหม่ได้ไหม — ใช้สวิตช์ตัวเดียวกับตอนใช้งาน</summary>
    public static bool CanIssueKey(bool? enableApiAccess) => EvaluateAccountKey(enableApiAccess).Allowed;
}
