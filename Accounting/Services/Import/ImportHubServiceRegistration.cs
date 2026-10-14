using System.Reflection;
using Accounting.Services.Import.Targets;

namespace Accounting.Services.Import;

/// <summary>
/// **ลงทะเบียน DI ของศูนย์นำเข้าข้อมูลทั้งหมด** (รอบ 205 T0) — <c>Program.cs</c> เรียก <see cref="AddImportHub"/> บรรทัดเดียว
///
/// <para>ทีม A–E <b>ไม่แตะ Program.cs</b>: target ใหม่ = เพิ่ม class ที่ implement <see cref="IImportTarget"/> (ถูก scan อัตโนมัติด้วย
/// <see cref="ImportTargetTypes"/>) · service ใหม่ของทีม = เพิ่มบรรทัดในบล็อกของทีมตัวเองข้างล่าง (คนละบรรทัด ⇒ merge ไม่ชน)</para>
/// </summary>
public static class ImportHubServiceRegistration
{
    public static IServiceCollection AddImportHub(this IServiceCollection services)
    {
        // ── T0: สัญญา + โครง ──
        services.AddScoped<IImportPipelineService, ImportPipelineService>();
        services.AddScoped<IImportRowWriter, ImportRowWriter>();
        services.AddScoped<IImportEntityResolver, ImportEntityResolver>();
        services.AddScoped<IImportMappingTemplateStore, ImportMappingTemplateStore>();
        services.AddScoped<IOpeningBalanceReconciliation, OpeningBalanceReconciliation>();
        services.AddScoped<IDocumentImportBuilder, DocumentImportBuilder>();
        foreach (var t in ImportTargetTypes(typeof(IImportTarget).Assembly))
            services.AddScoped(typeof(IImportTarget), t);
        services.AddScoped<IImportTargetRegistry, ImportTargetRegistry>();
        services.AddHostedService<ImportBatchWorker>();

        // ── ทีม A (mapping + AI) ──
        // ทางเข้าเดียวของการจับคู่คอลัมน์ (แม่แบบ → คำพ้อง → เรียนรู้ → รูปข้อมูล → AI ผ่าน orchestrator) + ปิดลูป feedback ตอนยืนยัน
        services.AddScoped<IImportColumnMatchService, ImportColumnMatchService>();

        // ── ทีม B (engine + writer เดิม) ──

        // ── ทีม C (ธนาคาร) ──

        // ── ทีม D (บัญชี + migration) ──
        // ตัวหาบัญชีของรหัสจากไฟล์ (ImportAccountCodeResolver → AI ImportAccountMap) — ใช้ร่วม: ImportEntityResolver.ResolveAccountsAsync ·
        // MigrationWizardService (แนะนำบัญชี/ตรวจ) · OpeningTrialBalanceImportTarget · DocumentImportBuilder
        services.AddScoped<ImportAccountMatcher>();

        // ── ทีม E (UX + gate) ──

        return services;
    }

    /// <summary>class ที่ลงทะเบียนเป็น <see cref="IImportTarget"/> — ไม่ abstract · ไม่ใช่ generic เปิด · เรียงตามชื่อเต็ม (ลำดับคงที่ทุกเครื่อง)</summary>
    public static IReadOnlyList<Type> ImportTargetTypes(Assembly assembly)
        => assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && typeof(IImportTarget).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
}
