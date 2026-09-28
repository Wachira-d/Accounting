using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Accounting.Services.Settlement.Adapters;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 198 เฟส 1 ทีม D — สัญญาระหว่าง <c>SettlementController</c>/หน้าเว็บ กับ service ของทีม B/C:
/// ด่านสิทธิ์ (ตารางเดียว) · ข้อมูลอ้างอิงที่หน้าเว็บใช้แทนตารางป้ายของตัวเอง · ปุ่มตามสถานะ · ผู้สมัครจับคู่เงินเข้า —
/// ทุกกลุ่มมีทั้ง "สิ่งที่ต้องเปิด" และ "สิ่งที่ต้องปิด"
/// </summary>
public class SettlementControllerContractTests
{
    // ═══ สิทธิ์ ═══

    [Fact]
    public void คีย์settlementทั้งสี่อยู่ในแคตตาล็อก_หน้าบทบาทติ๊กได้()
    {
        var keys = new[]
        {
            SettlementPermissionScope.View, SettlementPermissionScope.Import,
            SettlementPermissionScope.Post, SettlementPermissionScope.Channels,
        };
        Assert.Equal(4, keys.Distinct().Count());
        foreach (var k in keys)
        {
            Assert.True(PermissionKeys.IsPermissionKey(k));
            var meta = Assert.Single(PermissionKeys.Catalog, m => m.Key == k);
            Assert.False(string.IsNullOrWhiteSpace(meta.LabelTh));
        }
    }

    [Fact]
    public void ไฟล์ต้นฉบับ_อ่านได้เฉพาะคนนำเข้าหรือลงบัญชี_ลบได้เฉพาะคนนำเข้า_ดูอย่างเดียวไม่พอ()
    {
        Assert.Contains(SettlementPermissionScope.Import, SettlementPermissionScope.SourceFileReaders);
        Assert.Contains(SettlementPermissionScope.Post, SettlementPermissionScope.SourceFileReaders);
        Assert.DoesNotContain(SettlementPermissionScope.View, SettlementPermissionScope.SourceFileReaders);
        Assert.Equal(new[] { SettlementPermissionScope.Import }, SettlementPermissionScope.SourceFileWriters);
        var rule = AttachmentPermissionScope.Resolve("SettlementBatch");
        Assert.Equal(SettlementPermissionScope.SourceFileReaders, rule.ReadAnyOf);
        Assert.Equal(SettlementPermissionScope.SourceFileWriters, rule.WriteAnyOf);
    }

    // ═══ ข้อมูลอ้างอิง (หน้าเว็บไม่มีตารางป้ายเอง) ═══

    [Fact]
    public void ข้อมูลอ้างอิง_ครบทุกค่าของทุกenum_ป้ายไม่ว่าง()
    {
        var r = SettlementReferenceCatalog.Build();
        AssertCovers<SettlementChannelKind>(r.ChannelKinds);
        AssertCovers<SettlementBatchStatus>(r.BatchStatuses);
        AssertCovers<SettlementMatchStatus>(r.MatchStatuses);
        AssertCovers<SettlementClassifiedBy>(r.ClassifiedBy);
        AssertCovers<SettlementSourceKind>(r.SourceKinds);
        AssertCovers<SettlementFeeVatMode>(r.FeeVatModes);
        AssertCovers<SettlementFeeWhtMode>(r.FeeWhtModes);
        AssertCovers<SettlementRevenueModel>(r.RevenueModels);
        AssertCovers<SettlementDateOrder>(r.DateOrders);
        // ป้ายสถานะ/โหมดภาษีต้องเป็นภาษาไทย ไม่ใช่ชื่อ enum ที่หลุดมา (defect class "ป้ายเป็น -/ชื่ออังกฤษ" รอบ 189)
        foreach (var o in r.BatchStatuses.Concat(r.MatchStatuses).Concat(r.FeeVatModes).Concat(r.FeeWhtModes))
            Assert.Contains(o.Label, ch => ch is >= '฀' and <= '๿');
    }

    private static void AssertCovers<T>(IReadOnlyList<SettlementEnumOption> options) where T : struct, Enum
    {
        Assert.Equal(Enum.GetNames<T>().OrderBy(x => x), options.Select(o => o.Value).OrderBy(x => x));
        Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
    }

    [Fact]
    public void ประเภทบรรทัด_ป้ายและคุณสมบัติมาจากตารางกติกาตัวเดียว_รอจัดประเภทไม่ใช่คำตอบ()
    {
        var r = SettlementReferenceCatalog.Build();
        Assert.Equal(Enum.GetValues<SettlementLineType>().Length, r.LineTypes.Count);
        foreach (var o in r.LineTypes)
        {
            var rule = SettlementLineTypeRules.For(Enum.Parse<SettlementLineType>(o.Value));
            Assert.Equal(rule.LabelTh, o.Label);
            Assert.Equal(rule.Postable, o.Postable);
            Assert.Equal(rule.RequiresReason, o.RequiresReason);
            Assert.Equal(rule.Posting.ToString(), o.Posting);
        }
        Assert.Equal(nameof(SettlementPostingKind.Refund), r.LineTypes.Single(o => o.Value == nameof(SettlementLineType.Refund)).Posting);
        Assert.False(r.LineTypes.Single(o => o.Value == nameof(SettlementLineType.Unclassified)).Postable);
        Assert.True(r.LineTypes.Single(o => o.Value == nameof(SettlementLineType.Adjustment)).RequiresReason);
        Assert.False(r.LineTypes.Single(o => o.Value == nameof(SettlementLineType.Sale)).RequiresReason);
    }

    [Fact]
    public void บทบาทผังค่าธรรมเนียม_ตรงกับชุดที่ตัวอ่านFeeAccountMapรับ_และผังมาตรฐานเดียวกัน()
    {
        var r = SettlementReferenceCatalog.Build();
        Assert.Equal(SettlementAccountRoles.Mappable, r.FeeRoles.Select(f => f.Role));
        foreach (var f in r.FeeRoles)
            Assert.Equal(SettlementAccountRoles.DefaultCode(f.Role), f.DefaultAccountCode);
        // ทิศตรงข้าม: แผนผังที่หน้าเว็บประกอบจากบทบาทเหล่านี้ต้องผ่านตัวอ่านกลางโดยไม่มีคีย์ถูกปฏิเสธ
        var json = JsonSerializer.Serialize(r.FeeRoles.ToDictionary(f => f.Role, _ => Guid.NewGuid().ToString()));
        var (map, rejected) = SettlementLineTypeRules.ParseFeeAccountMap(json);
        Assert.Empty(rejected);
        Assert.Equal(r.FeeRoles.Count, map.Count);
    }

    [Fact]
    public void ช่องจับคู่คอลัมน์_คีย์ที่หน้าเว็บส่ง_serviceอ่านได้จริง()
    {
        var r = SettlementReferenceCatalog.Build();
        var payload = r.ColumnFields.ToDictionary(f => f.Key, f => (object)("หัว " + f.Key));
        payload["layout"] = "Long";
        var map = SettlementColumnMap.Parse(JsonSerializer.Serialize(payload))!;
        var referenced = map.ReferencedHeaders().ToList();
        foreach (var f in r.ColumnFields)
            Assert.Contains("หัว " + f.Key, referenced);
        Assert.True(r.ColumnFields.Single(f => f.Key == "type").Required);
    }

    // ═══ ปุ่มตามสถานะ ═══

    [Theory]
    [InlineData(SettlementBatchStatus.Imported)]
    [InlineData(SettlementBatchStatus.Classified)]
    [InlineData(SettlementBatchStatus.Matched)]
    public void รอบที่ยังไม่ลงบัญชี_แก้บรรทัด_ยกเลิก_ลงบัญชีได้_แต่ยกเลิกการลงบัญชีและจับคู่ธนาคารไม่ได้(SettlementBatchStatus s)
    {
        var cb = Guid.NewGuid();
        var a = SettlementBatchActions.For(s, new[] { (cb, SettlementLineType.Chargeback) });
        Assert.True(a.CanEditLines);
        Assert.True(a.CanVoid);
        Assert.True(a.CanPost);
        Assert.False(a.CanUnpost);
        Assert.False(a.CanBankMatch);
        Assert.Empty(a.ResolvableChargebackLineIds);   // chargeback ปิดผลได้หลังลงบัญชีเท่านั้น
        Assert.Null(a.LockedReason);
    }

    [Fact]
    public void ลงบัญชีแล้ว_ยกเลิกการลงบัญชีและจับคู่ธนาคารได้_แก้บรรทัดไม่ได้พร้อมเหตุผล()
    {
        var cb = Guid.NewGuid();
        var fee = Guid.NewGuid();
        var a = SettlementBatchActions.For(SettlementBatchStatus.Posted,
            new[] { (cb, SettlementLineType.Chargeback), (fee, SettlementLineType.Commission) });
        Assert.False(a.CanEditLines);
        Assert.False(a.CanVoid);
        Assert.False(a.CanPost);
        Assert.True(a.CanUnpost);
        Assert.True(a.CanBankMatch);
        Assert.Equal(new[] { cb }, a.ResolvableChargebackLineIds);
        Assert.False(string.IsNullOrWhiteSpace(a.LockedReason));
    }

    [Fact]
    public void จับคู่ธนาคารแล้ว_ยกเลิกการลงบัญชียังทำได้_จับคู่ซ้ำไม่ได้_ยกเลิกแล้วไม่มีปุ่มใดเลย()
    {
        var matched = SettlementBatchActions.For(SettlementBatchStatus.BankMatched, Array.Empty<(Guid, SettlementLineType)>());
        Assert.True(matched.CanUnpost);
        Assert.False(matched.CanBankMatch);
        var voided = SettlementBatchActions.For(SettlementBatchStatus.Voided, Array.Empty<(Guid, SettlementLineType)>());
        Assert.False(voided.CanEditLines || voided.CanVoid || voided.CanPost || voided.CanUnpost || voided.CanBankMatch);
        Assert.False(string.IsNullOrWhiteSpace(voided.LockedReason));
    }

    // ═══ ทีม S3: ปุ่มใช้ตัวตัดสินเดียวกับด่านของ service (C-1 · C-2) ═══

    [Fact]
    public void S3_ลงค้างครึ่งทาง_แก้บรรทัดและยกเลิกรอบไม่ได้พร้อมเหตุผล_ลงบัญชีต่อได้_ตัวตัดสินเดียวกับด่านservice()
    {
        var half = SettlementBatchActions.For(SettlementBatchStatus.Matched, Array.Empty<(Guid, SettlementLineType)>(), postingArtifacts: 2);
        Assert.False(half.CanEditLines);
        Assert.False(half.CanVoid);
        Assert.True(half.CanPost);                                           // ลงต่อจากที่ค้าง
        Assert.Contains("ค้างครึ่งทาง", half.LockedReason);
        Assert.Equal(SettlementSaleMatch.IsEditable(SettlementBatchStatus.Matched, 2), half.CanEditLines);   // ไม่ drift จาก LoadEditableBatchAsync
        // ทิศตรงข้าม: ยังไม่มีของ ⇒ เหมือนเดิม · ไม่ได้ตรวจ (null) ⇒ ตัดสินจากสถานะ (service ตรวจตอนกด)
        var clean = SettlementBatchActions.For(SettlementBatchStatus.Matched, Array.Empty<(Guid, SettlementLineType)>(), postingArtifacts: 0);
        Assert.True(clean.CanEditLines && clean.CanVoid && clean.CanPost);
        Assert.Null(clean.LockedReason);
        Assert.True(SettlementBatchActions.For(SettlementBatchStatus.Matched, Array.Empty<(Guid, SettlementLineType)>()).CanVoid);
    }

    [Fact]
    public void S3_ลงบัญชีแล้วแต่ด่านยกเลิกการลงบัญชีปฏิเสธ_ซ่อนปุ่มพร้อมเหตุผลและทางไปต่อ_ไม่มีเหตุ_ปุ่มยังอยู่()
    {
        var refusals = new[] { new SettlementUnpostRefusal("TIV-0001", "e-Tax ตอบรับแล้ว", "ออกใบลดหนี้แทน") };
        var blocked = SettlementBatchActions.For(SettlementBatchStatus.Posted, Array.Empty<(Guid, SettlementLineType)>(), 0, refusals);
        Assert.False(blocked.CanUnpost);
        Assert.Contains("TIV-0001", blocked.UnpostBlockedReason);
        Assert.Contains("ออกใบลดหนี้แทน", blocked.UnpostBlockedReason);
        Assert.True(blocked.CanBankMatch);                                   // ปุ่มอื่นไม่ถูกแตะ
        var open = SettlementBatchActions.For(SettlementBatchStatus.Posted, Array.Empty<(Guid, SettlementLineType)>(), 0,
            Array.Empty<SettlementUnpostRefusal>());
        Assert.True(open.CanUnpost);
        Assert.Null(open.UnpostBlockedReason);
    }

    // ═══ ผู้สมัครเงินเข้าธนาคาร ═══

    private static readonly Guid Bank = Guid.NewGuid();
    private static readonly DateTime PayDay = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    private static SettlementBankBatchFacts PostedBatch(SettlementBatchStatus status = SettlementBatchStatus.Posted)
        => new(Guid.NewGuid(), status, 1000m, PayDay, Bank, Guid.NewGuid(), null);

    private static SettlementBankTxnRow Txn(decimal amount, int dayOffset = 0, BankTransactionType type = BankTransactionType.Deposit,
        Guid? bank = null, Guid? linkedBatch = null)
        => new(Guid.NewGuid(), bank ?? Bank, PayDay.AddDays(dayOffset), type, amount, ReconciliationStatus.Unmatched, null, linkedBatch,
            "โอนเข้า", null);

    [Fact]
    public void ผู้สมัคร_ยอดตรงทิศตรงบัญชีเดียวกัน_จับคู่ได้และขึ้นก่อน_ที่เหลือแสดงเหตุผล()
    {
        var exact = Txn(1000m, dayOffset: 2);
        var near = Txn(999.99m, dayOffset: 0);
        var outflow = Txn(1000m, dayOffset: 1, type: BankTransactionType.Withdrawal);
        var taken = Txn(1000m, dayOffset: 1, linkedBatch: Guid.NewGuid());
        var list = SettlementBankCandidates.Evaluate(PostedBatch(), new[] { near, outflow, taken, exact });

        Assert.Equal(exact.Id, list[0].BankTransactionId);
        Assert.True(list[0].CanMatch);
        Assert.Equal(2, list[0].DaysFromPayout);
        Assert.All(list.Skip(1), c => Assert.False(c.CanMatch));
        Assert.All(list.Skip(1), c => Assert.False(string.IsNullOrWhiteSpace(c.Message)));
        Assert.Contains("ไม่เท่า", list.Single(c => c.BankTransactionId == near.Id).Message);
    }

    [Fact]
    public void ผู้สมัคร_นอกช่วงวันถูกตัด_รอบที่ยังไม่ลงบัญชีจับคู่ไม่ได้ทุกแถว()
    {
        var far = Txn(1000m, dayOffset: SettlementBankCandidates.WindowDays + 1);
        var inWindow = Txn(1000m, dayOffset: -SettlementBankCandidates.WindowDays);
        var posted = SettlementBankCandidates.Evaluate(PostedBatch(), new[] { far, inWindow });
        Assert.Equal(new[] { inWindow.Id }, posted.Select(c => c.BankTransactionId));

        var notPosted = SettlementBankCandidates.Evaluate(PostedBatch(SettlementBatchStatus.Matched), new[] { inWindow });
        Assert.False(Assert.Single(notPosted).CanMatch);
        Assert.Contains("ลงบัญชีรอบโอนก่อน", notPosted[0].Message);
    }
}
