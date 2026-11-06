using Accounting.Helpers;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 250 WP-08 — สินทรัพย์ P2 (AUDIT_249 §2 คำตัดสิน 26/27 · ASSET_MULTI_DOCUMENT_DESIGN §6 Q1/Q5):
/// <list type="bullet">
/// <item><b>ราคาตามสัญญา</b> แก้ได้หลังสร้าง (<c>PATCH …/contract-price</c> · สิทธิ์ <c>Asset.Manage</c> · audit chain) — คง<b>คำเตือน</b>
/// <see cref="AssetCostSourceRules.ContractPriceWarning"/> ไม่บล็อก (เดิมใส่ผิดตอนสร้างแล้วแก้ไม่ได้ = ฟ้องตลอด)</item>
/// <item><b>วันที่พร้อมใช้งาน</b> ของสินทรัพย์ใบเดียว (<c>PATCH …/available-date</c>) — แก้ได้จนกว่ามีค่าเสื่อมลงบัญชี · ว่าง = วันที่เอกสาร ·
/// จุดตั้งต้นค่าเสื่อมทุกเส้นผ่าน <see cref="DepreciationSchedule.StartDate"/> ตัวเดียว · แผนที่ยังไม่ลงบัญชีสร้างใหม่ทันที</item>
/// </list>
/// </summary>
public partial class FixedAssetService
{
    public async Task<FixedAssetResponse> SetContractPriceAsync(Guid companyId, Guid assetId, decimal? contractPrice, string actor)
    {
        var (value, problem) = AssetCostSourceRules.NormalizeContractPrice(contractPrice);
        if (problem != null) throw new BusinessRuleException(problem, AssetCostSourceRules.RuleCode);
        var asset = await _db.FixedAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");
        var before = asset.ContractPrice;
        if (before == value) return await WithEditLocksAsync(companyId, MapToResponse(asset));   // ไม่เปลี่ยน ⇒ ไม่เขียน audit ซ้ำ
        asset.ContractPrice = value;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = ActorId(actor),
            Action = AuditAction.Update,
            EntityType = nameof(FixedAsset),
            EntityId = asset.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { ContractPrice = before }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "contract-price-updated",
                ContractPrice = value,
                ruleCode = AssetCostSourceRules.RuleCode,
            }),
        });
        await _db.SaveChangesAsync();
        // ฝ่ายค้าน LOW: คำตอบเดียวกับ GET (ฟอร์มที่ hydrate จากคำตอบนี้ยังรู้ว่าช่องวันพร้อมใช้ถูกล็อกไหม)
        return await WithEditLocksAsync(companyId, MapToResponse(asset));
    }

    public async Task<FixedAssetResponse> SetAvailableForUseDateAsync(Guid companyId, Guid assetId, DateTime? availableForUseDate, string actor)
    {
        var ownsTx = _db.Database.CurrentTransaction is null;
        var tx = ownsTx ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            // ล็อกแถวสินทรัพย์ก่อนตรวจ "มีค่าเสื่อมลงบัญชีแล้วไหม" (สองคำขอ/งานค่าเสื่อมพร้อมกันต้องเห็นสถานะเดียวกัน)
            await LockAssetAsync(companyId, assetId);
            var asset = await _db.FixedAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");
            var hasSources = await _db.FixedAssetCostSources.AnyAsync(s => s.CompanyId == companyId && s.FixedAssetId == asset.Id);
            var hasPosted = await _db.AssetDepreciations.AnyAsync(d => d.CompanyId == companyId && d.FixedAssetId == asset.Id && d.IsPosted);
            var newDate = availableForUseDate?.Date;
            var problem = FixedAssetValuationEdit.AvailableDateProblem(asset.IsUnderAcquisition, hasSources, hasPosted,
                asset.PurchaseDate, newDate, TodayBangkok(), asset.Status, asset.AccumulatedDepreciation, asset.ReviewEffectiveFromMonthIndex != null);
            if (problem != null) throw new BusinessRuleException(problem, AssetCostSourceRules.RuleCode, 409);

            var before = asset.AvailableForUseDate;
            if (before?.Date != newDate)
            {
                asset.AvailableForUseDate = newDate;
                // แผนที่ยังไม่ลงบัญชีคิดจากจุดตั้งต้นใหม่ (สูตรเดียว BuildScheduleRows → DepreciationSchedule.StartDate)
                await RebuildPlannedScheduleAsync(companyId, asset, actor);
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = ActorId(actor),
                    Action = AuditAction.Update,
                    EntityType = nameof(FixedAsset),
                    EntityId = asset.Id.ToString(),
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new { AvailableForUseDate = before }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "available-for-use-date-updated",
                        AvailableForUseDate = newDate,
                        depreciationStart = DepreciationSchedule.StartDate(asset.PurchaseDate, newDate),
                        ruleCode = AssetCostSourceRules.RuleCode,
                    }),
                });
                await _db.SaveChangesAsync();
            }
            if (tx != null) await tx.CommitAsync();
            return await WithEditLocksAsync(companyId, MapToResponse(asset));
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync();
            throw;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }

    /// <summary>เหตุที่ช่อง "วันที่พร้อมใช้งาน" แก้ไม่ได้ (null = แก้ได้) — ตัวตัดสินเดียวกับ <see cref="SetAvailableForUseDateAsync"/> ⇒ หน้าเว็บล็อกช่องพร้อมเหตุ
    /// แทนการให้กดบันทึกแล้วถูกปฏิเสธ (ห้าม silent no-op · กฎเหล็ก #4 A)</summary>
    private async Task<FixedAssetResponse> WithEditLocksAsync(Guid companyId, FixedAssetResponse r)
    {
        var hasSources = await _db.FixedAssetCostSources.AsNoTracking().AnyAsync(s => s.CompanyId == companyId && s.FixedAssetId == r.Id);
        var hasPosted = await _db.AssetDepreciations.AsNoTracking().AnyAsync(d => d.CompanyId == companyId && d.FixedAssetId == r.Id && d.IsPosted);
        var hasReview = await _db.FixedAssets.AsNoTracking()
            .AnyAsync(a => a.Id == r.Id && a.CompanyId == companyId && a.ReviewEffectiveFromMonthIndex != null);
        var lockReason = FixedAssetValuationEdit.AvailableDateProblem(r.IsUnderAcquisition, hasSources, hasPosted, r.PurchaseDate, null, TodayBangkok(),
            r.Status, r.AccumulatedDepreciation, hasReview);
        return r with
        {
            AvailableDateLockReason = lockReason,
            DepreciationStartDate = DepreciationSchedule.StartDate(r.PurchaseDate, r.AvailableForUseDate),
        };
    }
}
