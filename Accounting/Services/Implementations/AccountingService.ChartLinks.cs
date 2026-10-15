namespace Accounting.Services.Implementations;

public partial class AccountingService
{
    // ═══ ผูกบัญชีแม่ของผังที่นำเข้าไว้ก่อน 2026-10-15 (ฝ่ายค้านรอบส่งออก P1-2 · Helpers/ChartParentLink · F2 ข้อ 9) ═══
    // รายงานก่อน (อ่านอย่างเดียว) → ผูกเฉพาะผังที่เลือก + audit chain · ไม่มีงานไหนผูกเองตอนเปิดระบบ

    public Task<List<ChartParentLinkSuspect>> FindUnlinkedAccountsAsync(Guid companyId)
        => ChartParentLinker.FindUnlinkedAsync(_db, companyId);

    public Task<ChartParentLinkRepairResult> RepairAccountParentLinksAsync(Guid companyId, IReadOnlyCollection<Guid> accountIds, string actor)
        => ChartParentLinker.RepairAsync(_db, companyId, accountIds, actor);
}
