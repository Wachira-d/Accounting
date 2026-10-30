using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

/// <summary>ค่าเริ่มต้นของ <see cref="ISharedMessageMeter"/> จนกว่าโควตาของแพ็ก A (รอบ 212) เข้า branch — ไม่จำกัด ไม่นับ (พฤติกรรมเดิม: ช่องกลางไม่มีเพดาน) ·
/// ผู้ประสานงานแทนที่การ register ใน <c>Program.cs</c> ด้วยตัวที่ห่อ <c>INotifyQuotaService</c> ตอน merge</summary>
public sealed class NoOpSharedMessageMeter : ISharedMessageMeter
{
    public Task<bool> TryReserveAsync(Guid companyId, string messageKey, CancellationToken ct = default) => Task.FromResult(true);

    public Task ReleaseAsync(Guid companyId, string messageKey, CancellationToken ct = default) => Task.CompletedTask;
}
