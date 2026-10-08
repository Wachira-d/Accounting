using Accounting.Models.DTOs.Pos;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีมตรวจงานค้าง 2026-10-08 (D3): <c>AddItemToOrder</c> เดิม <c>_db.PosOrderItems.Add(item)</c> แล้ว <c>order.Items.Add(item)</c> —
/// item ตั้ง OrderId และบิลถูกติดตามอยู่ ⇒ relationship fixup ของ EF ใส่ลง <c>order.Items</c> ให้แล้ว + List.Add ซ้ำ ⇒
/// <c>RecalculateOrder</c> นับรายการสองครั้ง (ยอดบิล/VAT เบิ้ล) · คลาสเดียวกับบทเรียน EfNewChild · ยอดบิลต้อง = ผลรวมแถวจริงในฐาน
/// </summary>
[Trait("Category", "Db")]
public class PosOrderTotalsDbTests
{
    private readonly ITestOutputHelper _out;
    public PosOrderTotalsDbTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task สร้างบิลสองรายการแล้วเพิ่มอีกหนึ่ง_ยอดบิลเท่าผลรวมรายการ_ไม่เบิ้ล()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var company = new Company { Name = "ร้านทดสอบยอดบิล", TaxId = "0105556000003" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var terminal = new PosTerminal { CompanyId = company.Id, Name = "POS-T1" };
        db.Set<PosTerminal>().Add(terminal);
        var session = new PosSession { CompanyId = company.Id, TerminalId = terminal.Id, OpenedByUserId = Guid.NewGuid(), Status = PosSessionStatus.Open };
        db.Set<PosSession>().Add(session);
        await db.SaveChangesAsync();

        PosService Svc(Accounting.Data.AccountingDbContext c) =>
            new(c, new AccountingService(c), NullLogger<PosService>.Instance, null!, null!);

        OrderResponse created;
        using (var req = DbTestDatabase.TryCreateContext()!)
            created = await Svc(req).CreateOrderAsync(company.Id, new CreateOrderRequest(
                session.Id, PosOrderType.WalkIn, null, null, null, null, null, null, null,
                Items: new List<CreateOrderItemRequest>
                {
                    new(null, null, "กาแฟ", null, 2m, "แก้ว", 50m),
                    new(null, null, "ขนม", null, 1m, "ชิ้น", 30m),
                }), "db-test");

        async Task AssertTotalsMatchRowsAsync()
        {
            using var check = DbTestDatabase.TryCreateContext()!;
            var order = await check.PosOrders.AsNoTracking().SingleAsync(o => o.Id == created.Id && o.CompanyId == company.Id);
            var rows = await check.PosOrderItems.AsNoTracking().Where(i => i.OrderId == created.Id && !i.IsDeleted).ToListAsync();
            Assert.Equal(rows.Sum(i => i.SubTotal), order.SubTotal);
        }

        Assert.Equal(130m, created.SubTotal);   // 2×50 + 30 — ไม่ใช่ 260
        await AssertTotalsMatchRowsAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var after = await Svc(req).AddOrderItemAsync(company.Id, created.Id, new CreateOrderItemRequest(null, null, "น้ำ", null, 1m, "ขวด", 20m));
            Assert.Equal(150m, after.SubTotal);
        }
        await AssertTotalsMatchRowsAsync();
    }
}
