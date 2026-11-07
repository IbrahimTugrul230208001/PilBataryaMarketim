namespace ECommerceBatteryShop.DataAccess.Entities;

/// <summary>Says "our product X is the supplier's SKU Y" and remembers that supplier's last quantity.</summary>
public class ProductSupplierLink
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public string SupplierSku { get; set; } = string.Empty;

    /// <summary>Quantity from the supplier's latest feed that contained this SKU; null until first seen.</summary>
    public int? LastQuantity { get; set; }

    /// <summary>When a feed last contained this SKU; null until first seen.</summary>
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
