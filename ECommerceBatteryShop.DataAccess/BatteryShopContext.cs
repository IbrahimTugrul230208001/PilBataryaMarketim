using ECommerceBatteryShop.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace ECommerceBatteryShop.DataAccess;

public class BatteryShopContext : DbContext
{
    public BatteryShopContext(DbContextOptions<BatteryShopContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        const string trCollation = "tr_icu_det";
        modelBuilder.Entity<Product>(e =>
        {
            e.Property(p => p.Name).UseCollation(trCollation);
            e.HasIndex(p => p.Slug).IsUnique(); // Index for Slug lookup in product detail pages
        });

        // IMPORTANT: Our categories do NOT use a parent FK. Prevent EF from generating a shadow FK (CategoryId)
        // and querying a non-existent column by ignoring the self-referencing collection.
        modelBuilder.Entity<Category>().Ignore(c => c.SubCategories);

        modelBuilder.Entity<Cart>(b =>
        {
            b.HasIndex(c => c.UserId).IsUnique().HasFilter("\"UserId\" IS NOT NULL");
            b.HasIndex(c => c.AnonId).IsUnique().HasFilter("\"AnonId\" IS NOT NULL");
        });

        // Index for faster cart items lookup and uniqueness guarantee
        modelBuilder.Entity<CartItem>(b => { b.HasIndex(ci => new { ci.CartId, ci.ProductId }).IsUnique(); });

        // Index for faster category browsing
        modelBuilder.Entity<ProductCategory>(b => { b.HasIndex(pc => pc.CategoryId); });

        // Index for faster user order history lookup
        modelBuilder.Entity<Order>(b => { b.HasIndex(o => o.UserId); });

        // NEW API (EF Core 9+): put check constraint on the table builder
        modelBuilder.Entity<Cart>()
            .ToTable(tb => tb.HasCheckConstraint(
                "CK_Cart_Owner",
                "(\"UserId\" IS NOT NULL AND \"AnonId\" IS NULL) OR (\"UserId\" IS NULL AND \"AnonId\" IS NOT NULL)"
            ));

        // Ensure SavedCard PK is value-generated and add a uniqueness guard to avoid duplicates
        modelBuilder.Entity<SavedCard>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();
            b.HasIndex(x => new { x.UserId, x.CardToken }).IsUnique();
        });

        modelBuilder.Entity<PasswordResetToken>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).IsRequired();
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasOne(x => x.User)
                .WithMany(u => u.PasswordResetTokens)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Supplier stock sync: each supplier SKU maps to one product, and a product links to a supplier at most once
        modelBuilder.Entity<Supplier>(b =>
        {
            b.HasIndex(s => s.Code).IsUnique();
            b.HasData(new Supplier
            {
                Id = 1,
                Code = "pilmagazasi",
                Name = "Pil Mağazası",
                FeedFormat = "akakce-xml",
                IsEnabled = true,
                MinExpectedItems = 300,
                StaleAfterHours = 48
            });
        });

        modelBuilder.Entity<ProductSupplierLink>(b =>
        {
            b.HasIndex(l => new { l.SupplierId, l.SupplierSku }).IsUnique();
            b.HasIndex(l => new { l.ProductId, l.SupplierId }).IsUnique();
            b.HasOne(l => l.Product).WithMany(p => p.SupplierLinks).HasForeignKey(l => l.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(l => l.Supplier).WithMany(s => s.ProductLinks).HasForeignKey(l => l.SupplierId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StockSyncRun>(b => { b.HasIndex(r => new { r.SupplierId, r.StartedAt }); });

        base.OnModelCreating(modelBuilder);
    }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<User> Users => Set<User>();
    public DbSet<FavoriteList> FavoriteLists => Set<FavoriteList>();
    public DbSet<FavoriteListItem> FavoriteListItems => Set<FavoriteListItem>();
    public DbSet<SavedCard> SavedCards => Set<SavedCard>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<ProductDiscount> ProductDiscounts => Set<ProductDiscount>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<ProductSupplierLink> ProductSupplierLinks => Set<ProductSupplierLink>();
    public DbSet<StockSyncRun> StockSyncRuns => Set<StockSyncRun>();
}

