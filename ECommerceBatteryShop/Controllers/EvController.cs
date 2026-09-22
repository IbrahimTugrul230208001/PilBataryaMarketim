using ECommerceBatteryShop.DataAccess.Abstract;
using ECommerceBatteryShop.DataAccess.Entities;
using ECommerceBatteryShop.Infrastructure;
using ECommerceBatteryShop.Models;
using ECommerceBatteryShop.Services;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace ECommerceBatteryShop.Controllers
{
    public class EvController : Controller
    {
        private readonly ILogger<EvController> _logger;
        private readonly IProductRepository _repo;
        private readonly ICurrencyService _currency;
        private readonly IFavoritesService _favorites;
        private readonly IPricingService _pricing;

        public EvController(IProductRepository repo,
            ICurrencyService currency,
            IFavoritesService favorites,
            ILogger<EvController> log,
            IPricingService pricing)
        {
            _repo = repo;
            _currency = currency;
            _favorites = favorites;
            _logger = log;
            _pricing = pricing;
        }

        // Target of UseExceptionHandler in production; must exist or the handler itself 404s.
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            ViewData["Title"] = "Bir hata oluştu";
            return View();
        }

        public async Task<IActionResult> Index(CancellationToken ct)
        {
            const int perSection = 16;

            ViewData["Title"] = "PilBataryaMarketim | Lityum Pil ve Enerji Depolama Mağazası";
            ViewData["Description"] =
                "PilBataryaMarketim'de Li-ion ve LiFePO4 pil paketleri, BMS koruma devreleri ve enerji depolama sistemleriyle ihtiyaçlarınıza uygun çözümleri keşfedin.";
            ViewData["Keywords"] = "PilBataryaMarketim, Aspilsan, lityum pil, lifepo4 batarya, bms, enerji depolama";
            ViewData["OgImage"] = Url.Content("~/img/pilbataryamarketim.webp");
            ViewData["Canonical"] = Request.GetDisplayUrl();

            // category ids from your DB
            const int LiIonId = 20;
            const int LiPolymerId = 21;
            const int BmsId = 50;
            const int LfpId = 22;
            const int socketsId = 51;
            const int puntaCihazıId = 53;
            const int siliconCablesId = 54;
            const int bandsId = 55;
            const int batteryPackages12vId = 59;
            const int batteryPackages24vId = 60;
            var rate = await _currency.GetCachedUsdTryAsync(ct);
            var fx = rate ?? _currency.FallbackRate;

            // Ensure this includes ProductCategories (CategoryId is enough; Category.Include not required)
            var plan = new[]
            {
                new { Title = "Lityum 12V Batarya Paketleri", CatId = batteryPackages12vId, CatSlug = "lifepo4-batarya-paketleri-12v", Thumb = true },
                new { Title = "Lityum 24V Batarya Paketleri", CatId = batteryPackages24vId, CatSlug = "lifepo4-batarya-paketleri-24v", Thumb = true },
                new { Title = "Lityum Polymer Pil", CatId = LiPolymerId, CatSlug = "lithium-polymer-pil", Thumb = true },
                new { Title = "Punta Cihazları", CatId = puntaCihazıId, CatSlug = "punta-cihazi", Thumb = true },
                new { Title = "Lithium-ion Pil", CatId = LiIonId, CatSlug = "lithium-ion-pil", Thumb = true },
                new { Title = "BMS - Pil Koruma Devresi", CatId = BmsId, CatSlug = "bms-pil-koruma-devresi", Thumb = true },
                new { Title = "LiFePO4 Pil", CatId = LfpId, CatSlug = "lifepo4-pil", Thumb = true },
                new { Title = "Soketler", CatId = socketsId, CatSlug = "soketler", Thumb = false },
                new { Title = "Silikon Kablolar", CatId = siliconCablesId, CatSlug = "silikon-kablolar", Thumb = true },
                new { Title = "Bantlar", CatId = bandsId, CatSlug = "bantlar", Thumb = false }
            };
            var favoriteIds = await LoadFavoriteIdsAsync(ct);

            ProductViewModel Map(Product p)
            {
                var priced = _pricing.PriceUnit(p, fx);

                return new ProductViewModel
                {
                    Id = p.Id,
                    Name = p.Name,
                    Price = priced.Final,
                    OriginalPrice = priced.Original,
                    DiscountPercentage = priced.DiscountPercentage,
                    Rating = p.Rating,
                    ImageUrl = p.ImageUrl ?? string.Empty,
                    ExtraAmount = p.ExtraAmount,
                    Description = p.Description ?? string.Empty,
                    IsFavorite = favoriteIds.Contains(p.Id),
                    Slug = p.Slug,
                    StockQuantity = p.Inventory?.Quantity ?? 0
                };
            }


            var sections = new List<ProductSectionViewModel>();
            var used = new HashSet<int>();

            foreach (var def in plan)
            {
                var raw = await _repo.BringProductsByCategoryIdAsync(def.CatId, 1, perSection * 2);
                var ps = raw.Items.Where(p => !used.Contains(p.Id)).Take(perSection).ToList();
                foreach (var p in ps) used.Add(p.Id);

                if (ps.Count > 0)
                    sections.Add(new ProductSectionViewModel
                    {
                        Title = def.Title,
                        AllLink = $"/Urun/{def.CatSlug}",
                        Products = ps.Select(Map).ToList(),
                        UseThumbnails = def.Thumb
                    });
            }

            return View(sections);

            async Task<HashSet<int>> LoadFavoriteIdsAsync(CancellationToken token)
            {
                FavoriteOwner? owner = null;

                var userId = User.GetUserId();
                if (userId is not null)
                {
                    owner = FavoriteOwner.FromUser(userId.Value);
                }
                else
                {
                    var anonId = AnonymousId.Read(Request);
                    if (anonId is not null)
                    {
                        owner = FavoriteOwner.FromAnon(anonId);
                    }
                }

                if (owner is null)
                {
                    return new HashSet<int>();
                }

                var list = await _favorites.GetAsync(owner, createIfMissing: false, token);

                return list is null
                    ? new HashSet<int>()
                    : new HashSet<int>(list.Items.Select(i => i.ProductId));
            }
        }

        public IActionResult Gizlilik()
        {
            return View();
        }

        public IActionResult Iade()
        {
            return View();
        }

        public IActionResult Cerezler()
        {
            return View();
        }

        public IActionResult Hakkimizda()
        {
            return View();
        }
    }
}
