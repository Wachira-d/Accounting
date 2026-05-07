namespace Accounting.Models.Entities;

public class CompanyRole : BaseEntity
{
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Color { get; set; } = "#6B7280";
    public string Icon { get; set; } = "👤";
    public bool IsSystemRole { get; set; }
    public int SortOrder { get; set; }

    public ICollection<CompanyRolePermission> Permissions { get; set; } = new List<CompanyRolePermission>();
    public ICollection<CompanyUser> CompanyUsers { get; set; } = new List<CompanyUser>();
}

public class CompanyRolePermission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyRoleId { get; set; }
    public CompanyRole CompanyRole { get; set; } = null!;

    public string MenuItemId { get; set; } = "";
    public bool CanAccess { get; set; } = true;
}
