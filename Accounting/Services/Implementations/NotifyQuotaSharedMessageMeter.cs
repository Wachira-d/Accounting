using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

/// <summary>รอบ 212 (รวมแพ็ก A+C): มิเตอร์ข้อความช่องกลางของสรุปรายวัน → โควตาจริงของแพ็ก A (<see cref="INotifyQuotaService"/>) ·
/// การกันส่งซ้ำของสรุปรายวันอยู่ที่แถว <c>StaffDailySummaryDeliveries</c> (unique ชุด/วัน/ผู้รับ) แล้ว ⇒ ที่นี่แค่จอง/คืนหน่วย ·
/// จำผลจองตามคีย์ไว้ใน scope เดียว (จอง→ส่ง→คืนเกิดในคำขอ/รอบงานเดียวกัน) เพื่อคืนให้ถูกชนิด (plan/bonus) — ไม่ใช่ state ข้ามคำขอ (CLAUDE.md §D)</summary>
public sealed class NotifyQuotaSharedMessageMeter : ISharedMessageMeter
{
    private readonly INotifyQuotaService _quota;
    private readonly Dictionary<string, NotifyReservation> _held = new(StringComparer.Ordinal);

    public NotifyQuotaSharedMessageMeter(INotifyQuotaService quota) { _quota = quota; }

    public async Task<bool> TryReserveAsync(Guid companyId, string messageKey, CancellationToken ct = default)
    {
        if (_held.ContainsKey(messageKey)) return true;   // คีย์เดิมใน scope เดียว ⇒ ไม่นับซ้ำ
        var r = await _quota.TryReserveAsync(companyId, essential: false, ct);
        if (!r.Ok) return false;
        _held[messageKey] = r;
        await _quota.AlertIfCrossedAsync(companyId, r, ct);
        return true;
    }

    public async Task ReleaseAsync(Guid companyId, string messageKey, CancellationToken ct = default)
    {
        if (_held.Remove(messageKey, out var r)) await _quota.ReleaseAsync(companyId, r, ct);
    }
}
