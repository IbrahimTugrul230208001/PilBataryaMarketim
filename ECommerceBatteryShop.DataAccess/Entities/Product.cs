namespace ECommerceBatteryShop.DataAccess.Entities;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public float Rating { get; set; }
    public int ExtraAmount { get; set; }
    public string? ImageUrl { get; set; } = string.Empty;
    public string? DocumentUrl { get; set; } = string.Empty;
    public string? Slug { get; set; } = string.Empty;
    public Inventory? Inventory { get; set; }
    public ICollection<ProductCategory> ProductCategories { get; set; } = new List<ProductCategory>();
    public ICollection<ProductDiscount> Discounts { get; set; } = new List<ProductDiscount>();
    public ICollection<ProductSupplierLink> SupplierLinks { get; set; } = new List<ProductSupplierLink>();
}
