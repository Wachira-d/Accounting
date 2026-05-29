namespace Accounting.Models.Entities;

/// <summary>
/// A floor / room in the restaurant where tables live. Multi-floor restaurants
/// or coffee shops with both indoor and outdoor seating create one PosFloorPlan
/// per area so tables don't crowd into a single canvas.
/// </summary>
public class PosFloorPlan : TenantEntity
{
    public string Name { get; set; } = null!;              // "ชั้น 1", "ลานนอก", "VIP"
    public int SortOrder { get; set; } = 0;
    public bool IsActive { get; set; } = true;

    /// <summary>Canvas dimensions in pixels — sets the editor work-area. The
    /// table coordinates are absolute pixels inside this box so a 1200×800
    /// floor can fit ~30 tables comfortably.</summary>
    public int CanvasWidth { get; set; } = 1200;
    public int CanvasHeight { get; set; } = 800;

    /// <summary>Optional background image (sketch / blueprint upload). Stored
    /// as /uploads/floor-plans/{guid}.jpg — drawn under the table layer.</summary>
    public string? BackgroundImageUrl { get; set; }

    public ICollection<PosTable> Tables { get; set; } = new List<PosTable>();
}

/// <summary>
/// A physical seating spot on a floor plan. The (X, Y, Width, Height, Rotation)
/// fields drive the visual canvas; the (TableNumber, Seats) fields drive the
/// POS workflow (matched to PosOrder.TableNumber).
/// </summary>
public class PosTable : TenantEntity
{
    public Guid FloorPlanId { get; set; }
    public PosFloorPlan FloorPlan { get; set; } = null!;

    public string TableNumber { get; set; } = null!;       // "A1", "VIP-1", "1"
    public int Seats { get; set; } = 4;                    // ที่นั่งสูงสุด

    /// <summary>"rectangle" / "circle" / "square" — drives the SVG/CSS shape.
    /// "rectangle" with Width >> Height = long table, etc.</summary>
    public string Shape { get; set; } = "rectangle";

    // Position on the floor canvas (top-left corner in pixels).
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 100;
    public int Height { get; set; } = 80;
    /// <summary>Rotation in degrees — 0 / 90 / 180 / 270 typical.</summary>
    public int Rotation { get; set; } = 0;

    /// <summary>Hex color for visual grouping (sections / smoking / VIP zone).
    /// Null = use status-based color (green = free, blue = active, etc).</summary>
    public string? Color { get; set; }

    public bool IsActive { get; set; } = true;
}
