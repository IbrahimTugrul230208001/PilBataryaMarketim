using ECommerceBatteryShop.DataAccess.Abstract;
using ECommerceBatteryShop.DataAccess.Entities;
using ECommerceBatteryShop.Infrastructure;
using ECommerceBatteryShop.Mapping;
using ECommerceBatteryShop.Services;
using ECommerceBatteryShop.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text.Json;
using System.Security.Claims;

namespace ECommerceBatteryShop.Controllers
{
    public class SepetController : Controller
    {
        private readonly ICartRepository _repo;
        private readonly ICartService _cartService;
        private readonly ICurrencyService _currencyService;
        private readonly IAddressRepository _addressRepository;
        private readonly IPricingService _pricing;
        public readonly ILogger<SepetController> _logger;
        private const string CookieConsentCookieName = "COOKIE_CONSENT";
        private const string CookieConsentRejectedValue = "rejected";

        private const string CartConsentMessage =
            "Çerezleri reddettiniz. Sepet özelliğini kullanabilmek için çerezleri kabul etmelisiniz.";


        public SepetController(ICartRepository repo, ICartService cartService, ICurrencyService currencyService,
            IAddressRepository addressRepository, IPricingService pricing, ILogger<SepetController> logger)
        {
            _repo = repo;
            _cartService = cartService;
            _currencyService = currencyService;
            _addressRepository = addressRepository;
            _pricing = pricing;
            _logger = logger;
        }

        private bool IsCookieConsentRejected()
        {
            if (!Request.Cookies.TryGetValue(CookieConsentCookieName, out var consent))
            {
                return false;
            }

            return string.Equals(consent, CookieConsentRejectedValue, StringComparison.OrdinalIgnoreCase);
        }

        private IActionResult CookieConsentRequired(string message)
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["cookie-consent-required"] = message
            });

            Response.Headers["HX-Trigger"] = payload;
            return StatusCode(StatusCodes.Status409Conflict, new { message });
        }

        public async Task<IActionResult> Index(CancellationToken ct)
        {
            CartOwner owner;
            var userId = User.GetUserId();
            if (userId is not null)
            {
                owner = CartOwner.FromUser(userId.Value);
            }
            else
            {
                if (IsCookieConsentRejected())
                {
                    return View(new CartViewModel
                    {
                        CookiesDisabled = true,
                        CookieMessage = CartConsentMessage
                    });
                }

                var anonId = AnonymousId.Read(Request);
                if (anonId is null)
                {
                    return View(new CartViewModel());
                }

                owner = CartOwner.FromAnon(anonId);
            }

            var rate = await _currencyService.GetCachedUsdTryAsync();
            decimal fx = rate ?? _currencyService.FallbackRate;
            var cart = await _cartService.GetAsync(owner, createIfMissing: false, ct);
            var model = new CartViewModel();
            if (cart is not null)
            {
                model.Items = cart.Items.Select(i =>
                {
                    var priced = _pricing.PriceUnit(i.UnitPrice, i.Product?.ExtraAmount ?? 0, i.Product?.Discounts, fx);

                    return new CartItemViewModel
                    {
                        ProductId = i.ProductId,
                        Name = i.Product?.Name ?? string.Empty,
                        ImageUrl = i.Product?.ImageUrl,
                        UnitPrice = priced.Final,
                        OriginalUnitPrice = priced.Original,
                        DiscountPercentage = priced.DiscountPercentage,
                        Slug = i.Product?.Slug,
                        Quantity = i.Quantity
                    };
                }).ToList();
            }

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Siparis()
        {
            CartOwner owner;
            var isAuthenticated = User.Identity?.IsAuthenticated == true;
            if (isAuthenticated)
            {
                // adapt this to however you store user id in claims
                var userId = int.Parse(User.FindFirst("sub")!.Value);
                owner = CartOwner.FromUser(userId);
            }
            else
            {
                if (IsCookieConsentRejected())
                {
                    return CookieConsentRequired(CartConsentMessage);
                }

                owner = CartOwner.FromAnon(AnonymousId.Ensure(HttpContext));
            }

            var rate = await _currencyService.GetCachedUsdTryAsync();
            decimal fx = rate ?? _currencyService.FallbackRate;

            IReadOnlyList<AddressViewModel> addresses = Array.Empty<AddressViewModel>();
            int? defaultAddressId = null;
            if (isAuthenticated)
            {
                var userId = int.Parse(User.FindFirst("sub")!.Value);
                var addressEntities = await _addressRepository.GetByUserAsync(userId, HttpContext.RequestAborted);
                addresses = addressEntities.Select(AddressMapper.ToViewModel).ToList();
                defaultAddressId = addresses.FirstOrDefault(a => a.IsDefault)?.Id
                                   ?? addresses.FirstOrDefault()?.Id;
            }

            // build brief cart items for the checkout page
            var cart = await _cartService.GetAsync(owner, createIfMissing: false, HttpContext.RequestAborted);
            var cartItems = cart?.Items.Select(i => new CartItemViewModel
            {
                ProductId = i.ProductId,
                Name = i.Product?.Name ?? string.Empty,
                ImageUrl = i.Product?.ImageUrl,
                UnitPrice = i.UnitPrice,
                Quantity = i.Quantity
            }).ToList() ?? new List<CartItemViewModel>();

            // Calculate subtotal with ExtraAmount included (matching Index page formula)
            decimal subTotal = 0m;
            if (cart is not null)
            {
                foreach (var item in cart.Items)
                {
                    var priced = _pricing.PriceUnit(item.UnitPrice, item.Product?.ExtraAmount ?? 0, item.Product?.Discounts, fx);
                    subTotal += priced.Final * item.Quantity;
                }
            }

            var guest = isAuthenticated ? null : GuestInfoCookie.Read(Request);
            AddressViewModel? selectedAddressViewModel = addresses.FirstOrDefault(a => a.Id == defaultAddressId);

            // Generate the contract model with default values
            var contractModel = BuildContractViewModel(
                cart,
                selectedAddressViewModel,
                guest,
                fx,
                150m);

            var model = new CheckoutPageViewModel
            {
                SubTotal = subTotal,
                Addresses = addresses,
                IsGuest = !isAuthenticated,
                Guest = guest,
                CartItems = cartItems,
                Contract = contractModel
            };

            return View(model);
        }

        private ContractViewModel BuildContractViewModel(
            Cart? cart,
            AddressViewModel? selectedAddress,
            GuestCheckoutViewModel? guest,
            decimal rate,
            decimal? shipping)
        {
            const decimal DefaultShippingFee = 150m;

            var orderItems = new List<OrderItem>();

            if (cart is not null)
            {
                foreach (var item in cart.Items)
                {
                    var priced = _pricing.PriceUnit(item.UnitPrice, item.Product?.ExtraAmount ?? 0, item.Product?.Discounts, rate);

                    orderItems.Add(new OrderItem
                    {
                        Quantity = item.Quantity,
                        UnitPrice = priced.Final
                    });
                }
            }

            if (orderItems.Count == 0)
            {
                orderItems.Add(new OrderItem
                {
                    Quantity = 1,
                    UnitPrice = 0m
                });
            }

            var buyerEmail = User.Identity?.IsAuthenticated == true ? User.FindFirst(ClaimTypes.Email)?.Value : null;

            string buyerName;
            string buyerPhone;
            string buyerAddress;

            if (selectedAddress is not null)
            {
                buyerName = $"{selectedAddress.Name} {selectedAddress.Surname}".Trim();
                buyerPhone = selectedAddress.PhoneNumber ?? string.Empty;
                var addressParts = new[]
                {
                    selectedAddress.FullAddress,
                    selectedAddress.Neighbourhood,
                    string.Join('/',
                        new[] { selectedAddress.State, selectedAddress.City }.Where(s => !string.IsNullOrWhiteSpace(s)))
                };
                buyerAddress = string.Join(" ", addressParts.Where(part => !string.IsNullOrWhiteSpace(part)));
            }
            else if (guest is not null)
            {
                buyerName = $"{guest.Name} {guest.Surname}".Trim();
                buyerPhone = guest.Phone ?? string.Empty;
                var addressParts = new[]
                {
                    guest.FullAddress,
                    guest.Neighbourhood,
                    string.Join('/', new[] { guest.State, guest.City }.Where(s => !string.IsNullOrWhiteSpace(s)))
                };
                buyerAddress = string.Join(" ", addressParts.Where(part => !string.IsNullOrWhiteSpace(part)));
                if (string.IsNullOrWhiteSpace(buyerEmail))
                {
                    buyerEmail = guest.Email;
                }
            }
            else
            {
                buyerName = "Belirtilmedi";
                buyerPhone = "+90 000 000 00 00";
                buyerAddress = "Belirtilmedi";
            }

            if (string.IsNullOrWhiteSpace(buyerName)) buyerName = "Belirtilmedi";
            if (string.IsNullOrWhiteSpace(buyerPhone)) buyerPhone = "+90 000 000 00 00";
            if (string.IsNullOrWhiteSpace(buyerAddress)) buyerAddress = "Belirtilmedi";
            buyerEmail = string.IsNullOrWhiteSpace(buyerEmail) ? "info@pilbataryamarketim.com" : buyerEmail;

            var shippingFee = orderItems.Any(i => i.UnitPrice > 0m)
                ? (shipping ?? DefaultShippingFee)
                : 0m;

            var model = new ContractViewModel
            {
                BuyerName = buyerName,
                BuyerAddress = buyerAddress,
                BuyerPhone = buyerPhone,
                BuyerEmail = buyerEmail!,
                OrdererName = buyerName,
                OrdererAddress = buyerAddress,
                OrdererPhone = buyerPhone,
                OrdererEmail = buyerEmail!,
                Items = orderItems,
                ShippingFee = shippingFee,
                InvoiceTitle = buyerName,
                InvoiceTax = "00000000000",
                InvoiceAddress = buyerAddress,
                InvoicePhone = buyerPhone,
                InvoiceEmail = buyerEmail!,
                ReturnPath = Url.Action("Iade", "Ev") ?? "/Ev/Iade",
                OrderDate = DateTime.Now
            };
            return model;
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Add(int productId, int quantity, CancellationToken ct = default)
        {
            // resolve owner: account vs guest
            CartOwner owner;
            var userId = User.GetUserId();
            if (userId is not null)
            {
                owner = CartOwner.FromUser(userId.Value);
            }
            else
            {
                if (IsCookieConsentRejected())
                {
                    return CookieConsentRequired(CartConsentMessage);
                }

                owner = CartOwner.FromAnon(AnonymousId.Ensure(HttpContext));
            }

            var count = await _cartService.AddAsync(owner, productId, quantity, ct);

            // returns updated count as partial view (HTMX/JS can swap it in header)
            return PartialView("_CartCount", count);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetQuantity(int productId, int quantity, CancellationToken ct = default)
        {
            CartOwner owner;
            var userId = User.GetUserId();
            if (userId is not null)
            {
                owner = CartOwner.FromUser(userId.Value);
            }
            else
            {
                if (IsCookieConsentRejected())
                {
                    return CookieConsentRequired(CartConsentMessage);
                }

                owner = CartOwner.FromAnon(AnonymousId.Ensure(HttpContext));
            }

            var count = await _cartService.SetQuantityAsync(owner, productId, quantity, ct);

            return PartialView("_CartCount", count);
        }

        [ValidateAntiForgeryToken]
        [HttpPost]
        public async Task<IActionResult> Delete(int productId, CancellationToken ct = default)
        {
            CartOwner owner;
            var userId = User.GetUserId();
            if (userId is not null)
            {
                owner = CartOwner.FromUser(userId.Value);
            }
            else
            {
                if (IsCookieConsentRejected())
                {
                    return CookieConsentRequired(CartConsentMessage);
                }

                var anonId = AnonymousId.Read(Request);
                if (anonId is null)
                {
                    return PartialView("_CartCount", 0);
                }

                owner = CartOwner.FromAnon(anonId);
            }

            var count = await _cartService.RemoveAsync(owner, productId, ct);

            return PartialView("_CartCount", count);
        }

        [ValidateAntiForgeryToken]
        [HttpPost]
        public async Task<IActionResult> DeleteAll(CancellationToken ct = default)
        {
            CartOwner owner;
            var userId = User.GetUserId();
            if (userId is not null)
            {
                owner = CartOwner.FromUser(userId.Value);
            }
            else
            {
                if (IsCookieConsentRejected())
                {
                    return CookieConsentRequired(CartConsentMessage);
                }

                var anonId = AnonymousId.Read(Request);
                if (anonId is null)
                {
                    return PartialView("_CartCount", 0);
                }

                owner = CartOwner.FromAnon(anonId);
            }

            var count = await _cartService.RemoveAllAsync(owner, ct);

            return PartialView("_CartCount", count);
        }
    }
}
