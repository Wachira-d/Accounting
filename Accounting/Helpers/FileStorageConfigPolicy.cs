namespace Accounting.Helpers;

/// <summary>
/// **ด่านค่าตั้งของที่เก็บไฟล์กลาง — ตัวตัดสินเดียว** (ทีม FS · 2026-11-02 · CLAUDE.md กฎเหล็ก #4 C "secret ใหม่ทุกตัว fail-fast ใน production")
///
/// <para><c>FileStorage:Provider</c> = <c>Local</c> (ค่าเริ่มต้น · พฤติกรรมเดิม) หรือ <c>S3</c> (AWS S3 / Cloudflare R2 / MinIO / DO Spaces) ·
/// เลือก S3 แล้วค่าใดว่าง/เป็น placeholder ⇒ production <b>ไม่ boot</b> (Program.cs โยน) — ห้ามถอยไปเขียนดิสก์เงียบ ๆ
/// เพราะไฟล์จะกระจายอยู่บนดิสก์ของแต่ละเครื่องแล้ว "หาย" เมื่อคอนเทนเนอร์ถูกสร้างใหม่ · นอก production = เตือนดังแล้วใช้ Local</para>
/// </summary>
public static class FileStorageConfigPolicy
{
    public const string ProviderLocal = "Local";
    public const string ProviderS3 = "S3";

    /// <summary>ชื่อผู้ให้บริการที่รู้จัก (ไม่สนตัวพิมพ์) · ว่าง = Local · ไม่รู้จัก = null</summary>
    public static string? NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return ProviderLocal;
        var p = provider.Trim();
        if (p.Equals(ProviderLocal, StringComparison.OrdinalIgnoreCase) || p.Equals("LocalDisk", StringComparison.OrdinalIgnoreCase)) return ProviderLocal;
        if (p.Equals(ProviderS3, StringComparison.OrdinalIgnoreCase)) return ProviderS3;
        return null;
    }

    private static readonly string[] PlaceholderMarkers =
    {
        // คำยาวพอที่จะไม่โผล่ในคีย์สุ่มจริง (คีย์ S3 เป็น base64 40 ตัว — คำสั้นอย่าง "xxx"/"todo" มีโอกาสชนคีย์จริงจนด่านฟ้องผิด)
        "changeme", "change_me", "change-this", "change_this", "placeholder", "your-", "your_", "<", "example",
    };

    /// <summary>ค่าที่ "ไม่ว่างแต่ใช้ไม่ได้" — ตัวอย่างใน appsettings/เอกสาร (เช่น AKIAIOSFODNN7EXAMPLE) · ค่าที่ขึ้นต้น <c>${</c></summary>
    public static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var v = value.Trim();
        if (v.StartsWith("${", StringComparison.Ordinal)) return true;
        var lower = v.ToLowerInvariant();
        return PlaceholderMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>ปัญหาของค่าตั้ง (ข้อความไทย พร้อมชื่อคีย์/env) · ว่าง = ใช้ได้ · Local ไม่มีค่าบังคับ</summary>
    public static List<string> Problems(string? provider, string? bucket, string? endpoint, string? region, string? accessKeyId, string? secretAccessKey)
    {
        var problems = new List<string>();
        var p = NormalizeProvider(provider);
        if (p == null)
        {
            problems.Add($"FileStorage:Provider = \"{provider}\" ไม่รู้จัก — ใช้ \"Local\" หรือ \"S3\"");
            return problems;
        }
        if (p == ProviderLocal) return problems;

        if (IsPlaceholder(bucket)) problems.Add("FileStorage:S3:Bucket (env FileStorage__S3__Bucket) ว่างหรือเป็น placeholder");
        if (IsPlaceholder(accessKeyId)) problems.Add("FileStorage:S3:AccessKeyId (env FileStorage__S3__AccessKeyId) ว่างหรือเป็น placeholder");
        if (IsPlaceholder(secretAccessKey)) problems.Add("FileStorage:S3:SecretAccessKey (env FileStorage__S3__SecretAccessKey) ว่างหรือเป็น placeholder");
        else if (secretAccessKey!.Trim().Length < 16) problems.Add("FileStorage:S3:SecretAccessKey สั้นผิดปกติ (< 16 อักษร)");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            // ไม่ระบุ endpoint = AWS S3 ⇒ ต้องรู้ region จริง ("auto" เป็นของ R2 ใช้กับ AWS ไม่ได้)
            if (string.IsNullOrWhiteSpace(region) || region.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
                problems.Add("FileStorage:S3:Endpoint ว่าง — ระบุ endpoint (R2/MinIO/Spaces) หรือ FileStorage:S3:Region ของ AWS (เช่น ap-southeast-1)");
        }
        else if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
        {
            problems.Add($"FileStorage:S3:Endpoint \"{endpoint}\" ไม่ใช่ URL http(s)");
        }
        else if (IsPlaceholder(u.Host))
        {
            problems.Add("FileStorage:S3:Endpoint เป็น placeholder");
        }
        return problems;
    }

    /// <summary>endpoint ที่ใช้จริง — ไม่ระบุ ⇒ AWS S3 ของ region นั้น</summary>
    public static Uri ResolveEndpoint(string? endpoint, string region)
        => string.IsNullOrWhiteSpace(endpoint)
            ? new Uri($"https://s3.{region.Trim()}.amazonaws.com")
            : new Uri(endpoint.Trim().TrimEnd('/'));
}
