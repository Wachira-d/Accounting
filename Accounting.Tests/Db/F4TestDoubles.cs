using System.Reflection;

namespace Accounting.Tests.Db;

/// <summary>ตัวแทน interface สำหรับเทสต์ DB ของทีม F4 (รอบ 2026-10-14) — สร้างผ่าน <see cref="DispatchProxy"/> แทนการเขียนคลาสที่ implement ทุกเมธอด
/// (env นี้ไม่มี SDK ให้คอมไพล์ตรวจลายเซ็น ⇒ ลดความเสี่ยงที่ลายเซ็นไม่ตรงทำให้ทั้งโปรเจกต์เทสต์คอมไพล์ไม่ผ่าน) ·
/// <see cref="Handler"/> ได้ชื่อเมธอด + อาร์กิวเมนต์ แล้วคืน "ค่าข้างใน" — ตัวห่อแปลงเป็น Task/Task&lt;T&gt; ให้เอง</summary>
public class F4Proxy<T> : DispatchProxy where T : class
{
    public Func<string, object?[], object?> Handler { get; set; } = (_, _) => null;

    public static T Create(Func<string, object?[], object?> handler)
    {
        var proxy = DispatchProxy.Create<T, F4Proxy<T>>();
        ((F4Proxy<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException("ไม่มีเมธอด");
        var value = Handler(method.Name, args ?? Array.Empty<object?>());
        var rt = method.ReturnType;
        if (rt == typeof(Task)) return value as Task ?? Task.CompletedTask;
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
        {
            if (value is Task t && rt.IsInstanceOfType(t)) return t;
            var inner = rt.GetGenericArguments()[0];
            var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner);
            return fromResult.Invoke(null, new[] { value ?? (inner.IsValueType ? Activator.CreateInstance(inner) : null) });
        }
        if (value == null && rt.IsValueType && rt != typeof(void)) return Activator.CreateInstance(rt);
        return value;
    }
}

/// <summary>IServiceProvider แบบตาราง — ให้ service ที่ resolve บริการอื่นตอนใช้ (ApprovalService · MobileApiService) หาเจอเฉพาะที่เทสต์ใส่</summary>
public sealed class F4ServiceMap : IServiceProvider
{
    private readonly Dictionary<Type, object> _map = new();
    public F4ServiceMap Add<TService>(TService service) where TService : class
    {
        _map[typeof(TService)] = service;
        return this;
    }
    public object? GetService(Type serviceType) => _map.TryGetValue(serviceType, out var s) ? s : null;
}
