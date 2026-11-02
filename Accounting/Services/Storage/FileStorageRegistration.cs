using Accounting.Helpers;

namespace Accounting.Services.Storage;

/// <summary>
/// ลงทะเบียนที่เก็บไฟล์กลาง (Program.cs เรียกบรรทัดเดียว) + <b>ด่าน fail-fast ใน production</b> (CLAUDE.md กฎเหล็ก #4 C)
///
/// <para>ค่าตั้ง (appsettings หรือ env แบบ <c>FileStorage__S3__Bucket</c>):
/// <c>FileStorage:Provider</c> = Local | S3 · <c>FileStorage:BasePath</c> (เดิม) ·
/// <c>FileStorage:S3:{Endpoint, Bucket, Region, AccessKeyId, SecretAccessKey, ForcePathStyle, KeyPrefix, LocalReadFallback}</c></para>
/// </summary>
public static class FileStorageRegistration
{
    public static IServiceCollection AddFileStorage(this IServiceCollection services, IConfiguration config, IWebHostEnvironment env)
    {
        var locator = FileStorageLocator.FromEnvironment(config, env);
        services.AddSingleton(locator);
        services.AddSingleton(new LocalDiskFileStorage(locator));

        var s3 = config.GetSection("FileStorage:S3");
        var provider = config["FileStorage:Provider"];
        var problems = FileStorageConfigPolicy.Problems(provider, s3["Bucket"], s3["Endpoint"], s3["Region"], s3["AccessKeyId"], s3["SecretAccessKey"]);
        if (problems.Count > 0)
        {
            var msg = "ค่าตั้งที่เก็บไฟล์ (FileStorage) ใช้ไม่ได้: " + string.Join(" · ", problems);
            if (env.IsProduction()) throw new InvalidOperationException(msg + " — production ไม่ถอยไปเขียนดิสก์เงียบ ๆ (ไฟล์จะหายเมื่อสร้างคอนเทนเนอร์ใหม่)");
            Console.Error.WriteLine($"⚠ {msg} — นอก production: ใช้ดิสก์ของเครื่องนี้ (Local) แทน");
            provider = FileStorageConfigPolicy.ProviderLocal;
        }

        if (FileStorageConfigPolicy.NormalizeProvider(provider) == FileStorageConfigPolicy.ProviderS3)
        {
            var region = string.IsNullOrWhiteSpace(s3["Region"]) ? "us-east-1" : s3["Region"]!.Trim();
            var prefix = (s3["KeyPrefix"] ?? "").Trim().Trim('/');
            var settings = new S3StorageSettings(
                FileStorageConfigPolicy.ResolveEndpoint(s3["Endpoint"], region),
                s3["Bucket"]!.Trim(), region, s3["AccessKeyId"]!.Trim(), s3["SecretAccessKey"]!.Trim(),
                ForcePathStyle: s3.GetValue("ForcePathStyle", true),
                KeyPrefix: prefix.Length == 0 ? "" : prefix + "/",
                LocalReadFallback: s3.GetValue("LocalReadFallback", true));
            // เพดานเวลาคุมต่อคำขอใน S3FileStorage (PUT 10 นาที · อื่น 100 วินาที) — ไม่ใช้ timeout รวมของ HttpClient (ตัดอัปโหลดไฟล์ใหญ่)
            services.AddHttpClient(S3FileStorage.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
            services.AddSingleton(settings);
            services.AddSingleton<S3FileStorage>();
            services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<S3FileStorage>());
            services.AddSingleton<IRemoteObjectStore>(sp => sp.GetRequiredService<S3FileStorage>());
        }
        else
        {
            services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<LocalDiskFileStorage>());
        }
        services.AddScoped<FileStorageMigrationService>();
        return services;
    }
}
