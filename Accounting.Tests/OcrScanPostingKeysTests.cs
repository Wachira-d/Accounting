using Accounting.Helpers;
using Accounting.Models.Constants;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// action ของสแกนที่สร้างผลทางบัญชี/สต็อกต้องถือคีย์ของโมดูลปลายทาง (ฝ่ายค้านรอบสอง 193 · R2-C3 → ทีม S2)
/// ครึ่งที่ 1: ขึ้นทะเบียนสินทรัพย์ = Asset.Manage (ชุดเดียวกับ FixedAssetController) · นำเข้าสต็อก = Inventory.Receive/Product.Edit ·
/// บรรทัดสินทรัพย์ปนบรรทัดสินค้าต้องมีทั้งสองชุด
/// ครึ่งที่ 2: คำขอที่มีแต่บรรทัดสินค้าไม่ถูกบังคับคีย์สินทรัพย์ (และกลับกัน)
/// </summary>
public class OcrScanPostingKeysTests
{
    [Fact]
    public void ขึ้นทะเบียนสินทรัพย์จากสแกน_ต้องมีAssetManage()
        => Assert.Equal(new[] { PermissionKeys.AssetManage }, OcrScanPostingKeys.AnyOf(OcrScanPostingTarget.FixedAsset));

    [Fact]
    public void นำเข้าสต็อกจากสแกน_ต้องมีคีย์รับของหรือคีย์สินค้า()
    {
        var keys = OcrScanPostingKeys.AnyOf(OcrScanPostingTarget.StockReceipt);
        Assert.Contains(PermissionKeys.InventoryReceive, keys);
        Assert.Contains(PermissionKeys.ProductEdit, keys);
        Assert.DoesNotContain(PermissionKeys.AssetManage, keys);
    }

    [Fact]
    public void บรรทัดปนกัน_ต้องตรวจทั้งสองผล()
        => Assert.Equal(new[] { OcrScanPostingTarget.FixedAsset, OcrScanPostingTarget.StockReceipt },
            OcrScanPostingKeys.TargetsForImport(anyFixedAssetLine: true, anyStockOrSuppliesLine: true));

    [Fact]
    public void ทิศตรงข้าม_มีแต่บรรทัดสินค้า_ไม่ถูกบังคับคีย์สินทรัพย์()
        => Assert.Equal(new[] { OcrScanPostingTarget.StockReceipt },
            OcrScanPostingKeys.TargetsForImport(anyFixedAssetLine: false, anyStockOrSuppliesLine: true));

    [Fact]
    public void ข้อความบอกคีย์ที่ต้องขอ_เป็นภาษาไทย()
    {
        Assert.Contains("Asset.Manage", OcrScanPostingKeys.DeniedMessage(OcrScanPostingTarget.FixedAsset));
        Assert.Contains("ไม่มีสิทธิ์", OcrScanPostingKeys.DeniedMessage(OcrScanPostingTarget.StockReceipt));
    }
}
