using Accounting.Helpers;

namespace Accounting.Services.Import;

/// <summary>
/// ทะเบียน <see cref="IImportTarget"/> ทุกตัวที่ DI scan เจอ (รอบ 205 T0) — คีย์ = <see cref="ImportExportPermissionScope.NormalizeEntityType"/>
/// ตัวเดียวกับด่านสิทธิ์ ⇒ "chart-of-accounts" / "chartofaccounts" / "chart_of_accounts" หา target เดียวกัน ·
/// สอง target อ้างชนิดเดียวกัน = ล้มดังตอนประกอบ (ไม่ใช่ "ตัวแรกชนะ" เงียบ ๆ)
/// </summary>
public class ImportTargetRegistry : IImportTargetRegistry
{
    private readonly Dictionary<string, IImportTarget> _byKey = new(StringComparer.Ordinal);

    public ImportTargetRegistry(IEnumerable<IImportTarget> targets)
    {
        All = targets.ToList();
        foreach (var t in All)
        {
            var key = ImportExportPermissionScope.NormalizeEntityType(t.EntityType);
            if (key.Length == 0)
                throw new InvalidOperationException($"IImportTarget {t.GetType().Name} ไม่ระบุ EntityType");
            if (!_byKey.TryAdd(key, t))
                throw new InvalidOperationException(
                    $"IImportTarget ซ้ำชนิด \"{t.EntityType}\": {t.GetType().Name} กับ {_byKey[key].GetType().Name} — ชนิดหนึ่งมีปลายทางได้ตัวเดียว");
        }
    }

    public IReadOnlyList<IImportTarget> All { get; }

    public IImportTarget? Find(string? entityType)
        => _byKey.TryGetValue(ImportExportPermissionScope.NormalizeEntityType(entityType), out var t) ? t : null;
}
