using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Encodings.Web;
using ECommerceBatteryShop.DataAccess.Abstract;
using ECommerceBatteryShop.DataAccess.Entities;
using ECommerceBatteryShop.Infrastructure;
using ECommerceBatteryShop.Models;
using ECommerceBatteryShop.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ECommerceBatteryShop.Controllers;

[Authorize]
[AutoValidateAntiforgeryToken]
public class SiparisController : Controller
{
    private const decimal DefaultShippingFee = 129.99m;
    private const decimal IbanDiscountRate = 0.03m;

    private readonly ICartService _cartService;
    private readonly ICurrencyService _currencyService;
    private readonly IAddressRepository _addressRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly IIyzicoPaymentService _paymentService;
    private readonly ISavedCardRepository _savedCardRepository;
    private readonly IThreeDSStore _threeDSStore;
    private readonly IPricingService _pricing;
    private readonly ILogger<SiparisController> _logger;

    private const string GuestInfoCookie = "GUEST_INFO";

    public SiparisController(
        ICartService cartService,
        ICurrencyService currencyService,
        IAddressRepository addressRepository,
        IOrderRepository orderRepository,
        IIyzicoPaymentService paymentService,
        ISavedCardRepository savedCardRepository,
        IThreeDSStore threeDSStore,
        IPricingService pricing,
        ILogger<SiparisController> logger)
    {
        _cartService = cartService;
        _currencyService = currencyService;
        _addressRepository = addressRepository;
        _orderRepository = orderRepository;
        _paymentService = paymentService;
        _savedCardRepository = savedCardRepository;
        _threeDSStore = threeDSStore;
        _pricing = pricing;
        _logger = logger;
    }

    private static string ResolveCarrier(string? shippingId)
    {
        return (shippingId ?? string.Empty).ToLowerInvariant() switch
        {
            "aras" => "Aras Kargo",
            "hepsijet" => "HepsiJET",
            "yurtici" => "Yurtiçi Kargo",
            "test" => "Test Kargo",
            _ => "Bilinmeyen Kargo"
        };
    }

    [HttpGet]
    public async Task<IActionResult> SavedCards(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized();

        var userIdClaim = User.FindFirst("sub") ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !int.TryParse(userIdClaim.Value, out var userId))
            return Unauthorized();

        var cards = await _savedCardRepository.GetByUserAsync(userId, ct);
        return Json(cards.Select(c => new { id = c.Id, brand = c.Brand, last4 = c.Last4, holder = c.Holder }));
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Installments([FromQuery] string? binNumber, CancellationToken ct)
    {
        if (!TryResolveOwner(out var owner, out _))
            return BadRequest(new { success = false, message = "Sepet bulunamadı." });

        var fxRate = await _currencyService.GetCachedUsdTryAsync(ct);
        var cart = await _cartService.GetAsync(owner, createIfMissing: false, ct);
        if (cart is null || cart.Items.Count == 0)
            return BadRequest(new { success = false, message = "Sepet boş." });

        var (_, basketTotal) = BuildBasketItems(cart, (decimal)fxRate);

        var result = await _paymentService.GetInstallmentInfoAsync(
            binNumber?.Replace(" ", ""), basketTotal, ct);

        if (!result.Success)
            return BadRequest(new { success = false, message = result.ErrorMessage });

        return Json(new { success = true, details = result.Details });
    }

    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("payment")]
    public async Task<IActionResult> PlaceOrder([FromForm] PlaceOrderInputModel input,
        CancellationToken cancellationToken)
    {
        var fxRate = await _currencyService.GetCachedUsdTryAsync(cancellationToken);
        var shippingPrice = SanitizeShipping(input.ShippingPrice);

        if (!ModelState.IsValid)
        {
            var firstError = ModelState.Values.SelectMany(v => v.Errors).FirstOrDefault()?.ErrorMessage
                             ?? "Geçersiz istek.";
            return BadRequest(new { success = false, message = firstError });
        }

        if (!TryResolveOwner(out var owner, out var errorResult))
        {
            return errorResult ?? BadRequest(new { success = false, message = "Sepet bulunamadı." });
        }

        var cart = await _cartService.GetAsync(owner, createIfMissing: false, cancellationToken);
        if (cart is null || cart.Items.Count == 0)
        {
            return BadRequest(new { success = false, message = "Sepetiniz boş olduğu için ödeme alınamadı." });
        }

        var isGuest = owner.UserId is null;

        if (string.Equals(input.PaymentMethod, "iban", StringComparison.OrdinalIgnoreCase))
        {
            if (!isGuest)
            {
                // existing authenticated flow: unchanged
                if (owner.UserId is not int userId1)
                {
                    return BadRequest(new
                        { success = false, message = "IBAN ile ödeme için giriş yapmanız gerekmektedir." });
                }

                var address = await ResolveAddressAsync(userId1, cancellationToken);
                if (address is null)
                {
                    return BadRequest(new
                        { success = false, message = "Sipariş oluşturmak için kayıtlı bir adres bulunamadı." });
                }

                var orderTotalBeforeDiscount = CalculateOrderTotal(cart, (decimal)fxRate) + shippingPrice;
                var discountedTotal = decimal.Round(orderTotalBeforeDiscount * (1 - IbanDiscountRate), 2,
                    MidpointRounding.AwayFromZero);

                var order1 = new Order
                {
                    OrderId = GenerateOrderNumber(),
                    UserId = userId1,
                    AddressId = address.Id,
                    Status = "Sipariş alındı",
                    OrderDate = DateTime.UtcNow,
                    TotalAmount = discountedTotal,
                    Items = cart.Items.Select(i => new OrderItem
                    {
                        ProductId = i.ProductId,
                        Quantity = i.Quantity,
                        UnitPrice = i.UnitPrice
                    }).ToList(),
                    Payments =
                    {
                        new PaymentTransaction
                        {
                            Amount = discountedTotal,
                            TransactionDate = DateTime.UtcNow,
                            PaymentMethod = "iban",
                            TransactionId = null
                        }
                    },
                    Shipment = new Shipment
                    {
                        Carrier = ResolveCarrier(input.ShippingId),
                        TrackingNumber = string.Empty,
                        ShippedDate = DateTime.UtcNow
                    }
                };

                await _orderRepository.InsertOrderAsync(order1, cancellationToken);
                await _cartService.RemoveAllAsync(owner, cancellationToken);

                _logger.LogInformation(
                    "IBAN order created successfully with discount. OrderNumber: {OrderNumber}, UserId: {UserId}",
                    order1.OrderId, userId1);

                return Ok(new
                {
                    success = true,
                    message =
                        $"Siparişiniz başarıyla alındı. IBAN ile ödeme indirimi (%3) uygulandı. Sipariş numaranız: {order1.OrderId.ToString("D6", CultureInfo.InvariantCulture)}. Sipariş numarasını açıklamaya giriniz."
                });
            }
            else
            {
                // New guest flow for IBAN
                var guest = ReadGuestInfoFromRequest() ?? FromInput(input);
                if (guest is null)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Misafir bilgileri eksik. Lütfen ad, soyad ve adres bilgilerini giriniz."
                    });
                }

                var orderTotalBeforeDiscount = CalculateOrderTotal(cart, (decimal)fxRate) + shippingPrice;
                var discountedTotal = decimal.Round(orderTotalBeforeDiscount * (1 - IbanDiscountRate), 2,
                    MidpointRounding.AwayFromZero);

                var order = new Order
                {
                    OrderId = GenerateOrderNumber(),
                    UserId = null,
                    AddressId = null,
                    AnonId = cart.AnonId,
                    BuyerName = $"{guest.Name} {guest.Surname}".Trim(),
                    BuyerEmail = guest.Email,
                    BuyerPhone = SanitizeDigits(guest.Phone),
                    ShippingAddressText = guest.FullAddress,
                    ShippingNeighbourhood = guest.Neighbourhood,
                    ShippingState = guest.State,
                    ShippingCity = guest.City,
                    Status = "Sipariş alındı",
                    OrderDate = DateTime.UtcNow,
                    TotalAmount = discountedTotal,
                    Items = cart.Items.Select(i => new OrderItem
                    {
                        ProductId = i.ProductId,
                        Quantity = i.Quantity,
                        UnitPrice = i.UnitPrice
                    }).ToList(),
                    Payments =
                    {
                        new PaymentTransaction
                        {
                            Amount = discountedTotal,
                            TransactionDate = DateTime.UtcNow,
                            PaymentMethod = "iban",
                            TransactionId = null
                        }
                    },
                    Shipment = new Shipment
                    {
                        Carrier = ResolveCarrier(input.ShippingId),
                        TrackingNumber = string.Empty,
                        ShippedDate = DateTime.UtcNow
                    }
                };

                await _orderRepository.InsertOrderAsync(order, cancellationToken);
                await _cartService.RemoveAllAsync(owner, cancellationToken);

                _logger.LogInformation(
                    "Guest IBAN order created successfully. OrderNumber: {OrderNumber}, AnonId: {AnonId}",
                    order.OrderId, cart.AnonId);

                return Ok(new
                {
                    success = true,
                    message =
                        $"Siparişiniz başarıyla alındı. IBAN ile ödeme indirimi (%3) uygulandı. Sipariş numaranız: {order.OrderId.ToString("D6", CultureInfo.InvariantCulture)}. Sipariş numarasını açıklamaya giriniz."
                });
            }
        }

        // Handle card payments
        bool useSaved = string.Equals(input.PaymentMethod, "card_saved", StringComparison.OrdinalIgnoreCase);
        if (!useSaved && !string.Equals(input.PaymentMethod, "card_new", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Seçilen ödeme yöntemi şu anda desteklenmiyor." });
        }

        if (!isGuest)
        {
            if (owner.UserId is not int userId)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Kart ile ödeme için giriş yapmanız gerekmektedir."
                });
            }

            var orderAddress = await ResolveAddressAsync(userId, cancellationToken);
            if (orderAddress is null)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Kartla ödeme için kayıtlı bir adres bulunamadı."
                });
            }

            var (lineItems, basketTotal) = BuildBasketItems(cart, (decimal)fxRate);
            if (lineItems.Count == 0)
            {
                return BadRequest(new { success = false, message = "Ödeme için sepet ürünü bulunamadı." });
            }

            var paidPrice = basketTotal + shippingPrice;

            var buyerContext = await BuildBuyerContextAsync(owner, cart, cancellationToken);

            var paymentModel = new IyzicoPaymentModel
            {
                ConversationId = Guid.NewGuid().ToString("N"),
                BasketId = cart.Id.ToString(CultureInfo.InvariantCulture),
                Price = basketTotal,
                PaidPrice = paidPrice,
                Installment = input.Installment > 0 ? input.Installment : 1,
                Buyer = buyerContext.Buyer,
                BillingAddress = buyerContext.Billing,
                ShippingAddress = buyerContext.Shipping,
                Items = lineItems
            };

            if (useSaved)
            {
                if (string.IsNullOrWhiteSpace(input.CardId) || !int.TryParse(input.CardId, out var savedId))
                {
                    return BadRequest(new { success = false, message = "Kayıtlı kart seçimi geçersiz." });
                }

                var saved =
                    (await _savedCardRepository.GetByUserAsync(userId, cancellationToken)).FirstOrDefault(c =>
                        c.Id == savedId);
                if (saved is null)
                {
                    return BadRequest(new { success = false, message = "Kayıtlı kart bulunamadı." });
                }

                paymentModel.Saved = new IyzicoSavedCard
                    { CardUserKey = saved.CardUserKey, CardToken = saved.CardToken };
            }
            else
            {
                if (!TryParseCard(input, out var cardInfo, out var cardError))
                {
                    return BadRequest(new
                        { success = false, message = cardError ?? "Kart bilgileri eksik veya hatalı." });
                }

                cardInfo.RegisterCard = input.Save;
                paymentModel.Card = cardInfo;
            }

            if (input.ThreeDSecure)
            {
                var initResult = await _paymentService.InitializeThreeDSPaymentAsync(paymentModel, cancellationToken);

                // 🔹 HtmlContent yoksa RawResponse'tan çek
                string html = initResult.HtmlContent;

                if (string.IsNullOrWhiteSpace(html) && !string.IsNullOrWhiteSpace(initResult.RawResponse))
                {
                    using var doc = JsonDocument.Parse(initResult.RawResponse);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("HtmlContent", out var hc) ||
                        root.TryGetProperty("htmlContent", out hc))
                    {
                        html = hc.GetString(); // Unicode escape’ler çözülür
                    }
                }

                // 🔹 HTML varsa, logla + kaydet + yönlendir
                if (!string.IsNullOrWhiteSpace(html))
                {
                    _logger.LogInformation("3DS init html extracted. Length={Len}", html.Length);

                    var redirectUrl = Url.Action(nameof(ThreeDSInit),
                                          new { conversationId = paymentModel.ConversationId })
                                      ??
                                      $"/Siparis/ThreeDSInit?conversationId={Uri.EscapeDataString(paymentModel.ConversationId)}";

                    var saveCard = !useSaved && input.Save;

                    _threeDSStore.SaveInitHtml(paymentModel.ConversationId, html);
                    _threeDSStore.SaveContext(paymentModel.ConversationId, new PendingThreeDSContext(
                        userId,
                        null,
                        input.ShippingId,
                        shippingPrice,
                        saveCard,
                        useSaved,
                        null,
                        saveCard ? input.Name : null));

                    _logger.LogInformation("3D Secure initialization completed. ConvId={ConvId}, UserId={UserId}",
                        paymentModel.ConversationId,
                        userId);

                    return Ok(new
                    {
                        success = true,
                        message = "3D Secure doğrulama sayfasına yönlendiriliyorsunuz.",
                        redirectUrl
                    });
                }

                // 🔹 HTML yine yoksa — kontrollü hata
                var initMessage = string.IsNullOrWhiteSpace(initResult.ErrorMessage)
                    ? "3D Secure başlatılırken bir hata oluştu. Lütfen tekrar deneyin."
                    : initResult.ErrorMessage;

                _logger.LogError("3DS init failed (no HTML). Success={Success}, RawLen={RawLen}, ConvId={ConvId}",
                    initResult.Success,
                    initResult.RawResponse?.Length ?? 0,
                    paymentModel.ConversationId);

                return StatusCode((int)HttpStatusCode.BadGateway, new { success = false, message = initMessage });
            }

            var result = await _paymentService.CreatePaymentAsync(paymentModel, cancellationToken);
            if (!result.Success)
            {
                if (!useSaved && input.Save && IsCardStorageNotEnabled(result.RawResponse))
                {
                    _logger.LogWarning(
                        "Card storage disabled on merchant. Retrying payment without saving card. ConversationId: {ConversationId}",
                        paymentModel.ConversationId);
                    if (paymentModel.Card is not null)
                    {
                        paymentModel.Card.RegisterCard = false;
                        var retry = await _paymentService.CreatePaymentAsync(paymentModel, cancellationToken);
                        if (!retry.Success)
                        {
                            var retryMessage = string.IsNullOrWhiteSpace(retry.ErrorMessage)
                                ? "Ödeme sırasında bir hata oluştu. Lütfen tekrar deneyin."
                                : retry.ErrorMessage;
                            return StatusCode((int)HttpStatusCode.BadGateway,
                                new { success = false, message = retryMessage });
                        }

                        result = retry;
                    }
                    else
                    {
                        var errorMessageA = string.IsNullOrWhiteSpace(result.ErrorMessage)
                            ? "Ödeme sırasında bir hata oluştu. Lütfen tekrar deneyin."
                            : result.ErrorMessage;
                        return StatusCode((int)HttpStatusCode.BadGateway,
                            new { success = false, message = errorMessageA });
                    }
                }
                else
                {
                    var errorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? "Ödeme sırasında bir hata oluştu. Lütfen tekrar deneyin."
                        : result.ErrorMessage;
                    return StatusCode((int)HttpStatusCode.BadGateway, new { success = false, message = errorMessage });
                }
            }

            if (!useSaved && input.Save)
            {
                await TrySaveCardFromResponseAsync(userId, input.Name ?? string.Empty, result.RawResponse,
                    cancellationToken);
            }

            var orderTotal = paidPrice;
            var transactionId = ExtractTransactionId(result.RawResponse, paymentModel.ConversationId);

            var order = new Order
            {
                OrderId = GenerateOrderNumber(),
                UserId = userId,
                AddressId = orderAddress.Id,
                Status = "Ödeme alındı",
                OrderDate = DateTime.UtcNow,
                TotalAmount = orderTotal,
                Items = cart.Items.Select(i => new OrderItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice
                }).ToList(),
                Payments =
                {
                    new PaymentTransaction
                    {
                        Amount = orderTotal,
                        TransactionDate = DateTime.UtcNow,
                        PaymentMethod = "iyzico",
                        TransactionId = transactionId
                    }
                },
                Shipment = new Shipment
                {
                    Carrier = ResolveCarrier(input.ShippingId),
                    TrackingNumber = string.Empty,
                    ShippedDate = DateTime.UtcNow
                }
            };

            await _orderRepository.InsertOrderAsync(order, cancellationToken);
            await _cartService.RemoveAllAsync(owner, cancellationToken);

            _logger.LogInformation(
                "Iyzico payment completed successfully. ConversationId: {ConversationId}, OrderNumber: {OrderNumber}",
                paymentModel.ConversationId,
                order.OrderId);

            return Ok(new
            {
                success = true,
                message =
                    $"Ödemeniz başarıyla tamamlandı. Sipariş numaranız: {order.OrderId.ToString("D6", CultureInfo.InvariantCulture)}",
                raw = result.RawResponse
            });
        }
        else
        {
            var guest = ReadGuestInfoFromRequest() ?? FromInput(input);
            if (guest is null)
            {
                return BadRequest(new
                {
                    success = false, message = "Misafir bilgileri eksik. Lütfen ad, soyad ve adres bilgilerini giriniz."
                });
            }

            if (useSaved)
            {
                return BadRequest(new { success = false, message = "Misafir kullanıcılar kayıtlı kart kullanamaz." });
            }

            if (!TryParseCard(input, out var cardInfo, out var cardErrorGuest))
            {
                return BadRequest(new
                    { success = false, message = cardErrorGuest ?? "Kart bilgileri eksik veya hatalı." });
            }

            cardInfo.RegisterCard = false;

            var (lineItems, basketTotal) = BuildBasketItems(cart, (decimal)fxRate);
            if (lineItems.Count == 0)
            {
                return BadRequest(new { success = false, message = "Ödeme için sepet ürünü bulunamadı." });
            }

            var paidPrice = basketTotal + shippingPrice;

            var buyerContext = BuildGuestBuyerContext(owner, cart, guest);

            var paymentModel = new IyzicoPaymentModel
            {
                ConversationId = Guid.NewGuid().ToString("N"),
                BasketId = cart.Id.ToString(CultureInfo.InvariantCulture),
                Price = basketTotal,
                PaidPrice = paidPrice,
                Installment = input.Installment > 0 ? input.Installment : 1,
                Buyer = buyerContext.Buyer,
                BillingAddress = buyerContext.Billing,
                ShippingAddress = buyerContext.Shipping,
                Items = lineItems,
                Card = cardInfo
            };

            if (input.ThreeDSecure)
            {
                var initResult = await _paymentService.InitializeThreeDSPaymentAsync(paymentModel, cancellationToken);
                if (!initResult.Success || string.IsNullOrWhiteSpace(initResult.HtmlContent))
                {
                    var initMessage = string.IsNullOrWhiteSpace(initResult.ErrorMessage)
                        ? "3D Secure başlatılırken bir hata oluştu. Lütfen tekrar deneyin."
                        : initResult.ErrorMessage;
                    return StatusCode((int)HttpStatusCode.BadGateway, new { success = false, message = initMessage });
                }

                var redirectUrl = Url.Action(nameof(ThreeDSInit), new { conversationId = paymentModel.ConversationId })
                                  ??
                                  $"/Siparis/ThreeDSInit?conversationId={Uri.EscapeDataString(paymentModel.ConversationId)}";

                var anonId = cart.AnonId ?? owner.AnonId;
                _threeDSStore.SaveInitHtml(paymentModel.ConversationId, initResult.HtmlContent);
                _threeDSStore.SaveContext(paymentModel.ConversationId, new PendingThreeDSContext(
                    null,
                    anonId,
                    input.ShippingId,
                    shippingPrice,
                    SaveCard: false,
                    UsedSavedCard: false,
                    guest,
                    input.Name));

                _logger.LogInformation(
                    "3D Secure initialization completed for guest. ConversationId: {ConversationId}, AnonId: {AnonId}",
                    paymentModel.ConversationId,
                    anonId);

                return Ok(new
                {
                    success = true,
                    message = "3D Secure doğrulama sayfasına yönlendiriliyorsunuz.",
                    redirectUrl
                });
            }

            var result = await _paymentService.CreatePaymentAsync(paymentModel, cancellationToken);
            if (!result.Success)
            {
                var errorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "Ödeme sırasında bir hata oluştu. Lütfen tekrar deneyin."
                    : result.ErrorMessage;
                return StatusCode((int)HttpStatusCode.BadGateway, new { success = false, message = errorMessage });
            }

            var transactionId = ExtractTransactionId(result.RawResponse, paymentModel.ConversationId);

            var order = new Order
            {
                OrderId = GenerateOrderNumber(),
                UserId = null,
                AddressId = null,
                AnonId = cart.AnonId,
                BuyerName = $"{guest.Name} {guest.Surname}".Trim(),
                BuyerEmail = guest.Email,
                BuyerPhone = SanitizeDigits(guest.Phone),
                ShippingAddressText = guest.FullAddress,
                ShippingNeighbourhood = guest.Neighbourhood,
                ShippingState = guest.State,
                ShippingCity = guest.City,
                Status = "Ödeme alındı",
                OrderDate = DateTime.UtcNow,
                TotalAmount = paidPrice,
                Items = cart.Items.Select(i => new OrderItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice
                }).ToList(),
                Payments =
                {
                    new PaymentTransaction
                    {
                        Amount = paidPrice,
                        TransactionDate = DateTime.UtcNow,
                        PaymentMethod = "iyzico",
                        TransactionId = transactionId
                    }
                },
                Shipment = new Shipment
                {
                    Carrier = ResolveCarrier(input.ShippingId),
                    TrackingNumber = string.Empty,
                    ShippedDate = DateTime.UtcNow
                }
            };

            await _orderRepository.InsertOrderAsync(order, cancellationToken);
            await _cartService.RemoveAllAsync(owner, cancellationToken);

            _logger.LogInformation(
                "Guest Iyzico payment completed successfully. ConversationId: {ConversationId}, OrderNumber: {OrderNumber}, AnonId: {AnonId}",
                paymentModel.ConversationId,
                order.OrderId,
                cart.AnonId);

            return Ok(new
            {
                success = true,
                message =
                    $"Ödemeniz başarıyla tamamlandı. Sipariş numaranız: {order.OrderId.ToString("D6", CultureInfo.InvariantCulture)}",
                raw = result.RawResponse
            });
        }
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ThreeDSInit([FromQuery] string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return Content("<html><body>Geçersiz 3D Secure oturumu.</body></html>", "text/html");
        }

        if (_threeDSStore.TryGetInitHtml(conversationId, out var html) && !string.IsNullOrWhiteSpace(html))
        {
            return Content(html, "text/html");
        }

        var redirectUrl = Url.Action(nameof(ThreeDSStatus), new { conversationId })
                          ?? $"/Siparis/ThreeDSStatus?conversationId={Uri.EscapeDataString(conversationId)}";
        var encodedUrl = WebUtility.HtmlEncode(redirectUrl);
        var fallback =
            $"<html><head><meta charset=\"utf-8\" /></head><body><p>3D Secure oturumu bulunamadı.</p><a href=\"{encodedUrl}\">Durumu görmek için tıklayın</a></body></html>";
        return Content(fallback, "text/html");
    }

    [HttpPost]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [Consumes("application/x-www-form-urlencoded")]
    [Route("Siparis/ThreeDSCallback")] // conventional path we configure in settings
    [Route("odeme/3ds-callback")]      // matches Iyzico__ThreeDSCallbackUrl in .env
    [Route("Payment/Iyzico3DSReturn")] // optional aliases if configured on Iyzi dashboard
    [Route("Odeme/Iyzico3DSReturn")] // optional Turkish alias
    public async Task<IActionResult> ThreeDSCallback([FromForm] IyzicoThreeDSCallbackModel model,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[3DS CALLBACK] status={Status} mdStatus={MdStatus} paymentId={PaymentId} convId={ConvId}",
            model?.Status, model?.MdStatus, model?.PaymentId, model?.ConversationId);

        if (model is null || string.IsNullOrWhiteSpace(model.ConversationId))
        {
            return RenderThreeDSRedirect("/Siparis/ThreeDSStatus");
        }

        var conversationId = model.ConversationId!;
        var redirectUrl = Url.Action(nameof(ThreeDSStatus), new { conversationId })
                          ?? $"/Siparis/ThreeDSStatus?conversationId={Uri.EscapeDataString(conversationId)}";

        if (_threeDSStore.TryGetResult(conversationId, out var existing) && existing is not null)
        {
            return RenderThreeDSRedirect(redirectUrl);
        }

        if (!_threeDSStore.TryGetContext(conversationId, out var context) || context is null)
        {
            _threeDSStore.SaveResult(conversationId, new ThreeDSResult(false, null, "Sipariş oturumu bulunamadı."));
            return RenderThreeDSRedirect(redirectUrl);
        }

        // Accept only mdStatus == "1" as successful per Iyzi docs
        if (!string.Equals(model.MdStatus, "1", StringComparison.Ordinal) ||
            !string.Equals(model.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            var failureMessage = string.IsNullOrWhiteSpace(model.ErrorMessage)
                ? "3D Secure doğrulaması başarısız oldu. Lütfen farklı bir kart deneyin."
                : model.ErrorMessage;
            _threeDSStore.SaveResult(conversationId, new ThreeDSResult(false, null, failureMessage));
            return RenderThreeDSRedirect(redirectUrl);
        }

        // Resilient extraction of paymentId and conversationData (case-insensitive, with fallbacks and URL-decoding)
        string? paymentId = model.PaymentId;
        string? conversationData = model.ConversationData;

        string? TryGetFromFormOrQuery(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (Request.HasFormContentType && Request.Form.TryGetValue(key, out var v1) &&
                    !string.IsNullOrWhiteSpace(v1))
                {
                    var s = v1.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return WebUtility.UrlDecode(s);
                }

                if (Request.Query.TryGetValue(key, out var v2) && !string.IsNullOrWhiteSpace(v2))
                {
                    var s = v2.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return WebUtility.UrlDecode(s);
                }
            }

            // Try case-insensitive search across all keys when exact names fail
            if (Request.HasFormContentType)
            {
                foreach (var kvp in Request.Form)
                {
                    var k = kvp.Key;
                    if (keys.Any(target => string.Equals(k, target, StringComparison.OrdinalIgnoreCase)))
                    {
                        var s = kvp.Value.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) return WebUtility.UrlDecode(s);
                    }
                }
            }

            foreach (var kvp in Request.Query)
            {
                var k = kvp.Key;
                if (keys.Any(target => string.Equals(k, target, StringComparison.OrdinalIgnoreCase)))
                {
                    var s = kvp.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return WebUtility.UrlDecode(s);
                }
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(paymentId))
        {
            paymentId = TryGetFromFormOrQuery("paymentId", "PaymentId", "paymentid", "payment_id", "paymentID");
        }

        if (string.IsNullOrWhiteSpace(conversationData))
        {
            conversationData = TryGetFromFormOrQuery("conversationData", "ConversationData", "conversationdata",
                "conversation_data");
        }

        if (string.IsNullOrWhiteSpace(paymentId))
        {
            _threeDSStore.SaveResult(conversationId, new ThreeDSResult(false, null, "paymentId eksik."));
            return RenderThreeDSRedirect(redirectUrl);
        }

        var completeModel = new IyzicoThreeDSCompleteModel(
            paymentId!,
            conversationId,
            string.IsNullOrWhiteSpace(conversationData) ? null : conversationData);
        var paymentResult = await _paymentService.CompleteThreeDSPaymentAsync(completeModel, cancellationToken);
        if (!paymentResult.Success)
        {
            var errorMessage = string.IsNullOrWhiteSpace(paymentResult.ErrorMessage)
                ? "3D Secure ödeme tamamlanamadı. Lütfen tekrar deneyin."
                : paymentResult.ErrorMessage;
            _threeDSStore.SaveResult(conversationId, new ThreeDSResult(false, paymentResult.RawResponse, errorMessage));
            return RenderThreeDSRedirect(redirectUrl);
        }

        var (success, message) =
            await FinalizeThreeDSOrderAsync(conversationId, paymentId!, context, paymentResult, cancellationToken);
        _threeDSStore.SaveResult(conversationId, new ThreeDSResult(success, paymentResult.RawResponse, message));

        return RenderThreeDSRedirect(redirectUrl);
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ThreeDSStatus([FromQuery] string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return View("ThreeDSStatus", new ThreeDSStatusViewModel(false, "Geçersiz 3D Secure oturumu."));
        }

        if (_threeDSStore.TryGetResult(conversationId, out var result) && result is not null)
        {
            var message = string.IsNullOrWhiteSpace(result.Message)
                ? (result.Success ? "Ödemeniz başarıyla tamamlandı." : "Ödeme sırasında bir hata oluştu.")
                : result.Message!;
            return View("ThreeDSStatus", new ThreeDSStatusViewModel(result.Success, message));
        }

        return View("ThreeDSStatus",
            new ThreeDSStatusViewModel(false, "Ödemeniz işleniyor. Lütfen birkaç saniye içinde tekrar deneyin."));
    }

    private (List<IyzicoBasketItem> Items, decimal BasketTotal) BuildBasketItems(Cart cart, decimal fxRate)
    {
        var items = new List<IyzicoBasketItem>();
        decimal total = 0m;

        foreach (var item in cart.Items)
        {
            var linePrice = _pricing.ChargeLineTotal(item.UnitPrice, item.Product?.ExtraAmount ?? 0,
                item.Product?.Discounts, item.Quantity, fxRate);
            total += linePrice;
            items.Add(new IyzicoBasketItem
            {
                Id = item.ProductId.ToString(CultureInfo.InvariantCulture),
                Name = item.Product?.Name ?? $"Ürün #{item.ProductId}",
                Price = linePrice
            });
        }

        return (items, total);
    }

    private decimal CalculateOrderTotal(Cart cart, decimal fxRate)
    {
        decimal total = 0m;
        foreach (var item in cart.Items)
        {
            total += _pricing.ChargeLineTotal(item.UnitPrice, item.Product?.ExtraAmount ?? 0,
                item.Product?.Discounts, item.Quantity, fxRate);
        }

        return total;
    }

    private static decimal SanitizeShipping(decimal? shipping)
    {
        if (!shipping.HasValue) return DefaultShippingFee;
        if (shipping.Value < 0) return 0m;
        if (shipping.Value > 1000) return DefaultShippingFee; // guard
        return decimal.Round(shipping.Value, 2, MidpointRounding.AwayFromZero);
    }

    private static int GenerateOrderNumber()
        => RandomNumberGenerator.GetInt32(100000, 1_000_000);

    private bool TryResolveOwner(out CartOwner owner, out IActionResult? errorResult)
    {
        owner = default;
        errorResult = null;

        if (User.Identity?.IsAuthenticated == true)
        {
            var userIdClaim = User.FindFirst("sub") ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
            if (userIdClaim is not null && int.TryParse(userIdClaim.Value, out var userId))
            {
                owner = CartOwner.FromUser(userId);
                return true;
            }

            errorResult = BadRequest(new { success = false, message = "Kullanıcı kimliği doğrulanamadı." });
            return false;
        }

        var anonId = AnonymousId.Read(Request);
        if (anonId is null)
        {
            errorResult = BadRequest(new { success = false, message = "Misafir sepeti bulunamadı." });
            return false;
        }

        owner = CartOwner.FromAnon(anonId);
        return true;
    }

    private async Task<(IyzicoBuyer Buyer, IyzicoAddress Billing, IyzicoAddress Shipping)> BuildBuyerContextAsync(
        CartOwner owner,
        Cart cart,
        CancellationToken cancellationToken)
    {
        Address? address = null;
        if (owner.UserId is int userId)
        {
            address = await ResolveAddressAsync(userId, cancellationToken);
        }

        var defaultName = address is null ? "Müşteri" : address.Name;
        var defaultSurname = address is null ? "" : address.Surname;
        var contactName = string
            .Join(' ', new[] { defaultName, defaultSurname }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (string.IsNullOrWhiteSpace(contactName))
        {
            contactName = owner.UserId?.ToString(CultureInfo.InvariantCulture) ?? "Misafir";
        }

        var fullAddress = address is null
            ? "Adres belirtilmedi"
            : string.Join(' ', new[]
            {
                address.FullAddress,
                address.Neighbourhood,
                address.State,
                address.City
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var phone = NormalizePhone(address?.PhoneNumber) ?? "+905555555555";
        var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        if (string.IsNullOrWhiteSpace(email))
        {
            email = "no-reply@example.com";
        }

        var buyer = new IyzicoBuyer
        {
            Id = owner.UserId?.ToString(CultureInfo.InvariantCulture) ?? cart.AnonId ?? Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(defaultName) ? "Müşteri" : defaultName,
            Surname = string.IsNullOrWhiteSpace(defaultSurname) ? "" : defaultSurname,
            GsmNumber = phone,
            Email = email,
            IdentityNumber = DeriveIdentityNumber(address),
            RegistrationAddress = fullAddress,
            City = address?.City ?? "Ankara",
            Country = "Turkey",
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1"
        };

        var billing = new IyzicoAddress
        {
            ContactName = contactName,
            City = address?.City ?? "Ankara",
            Address = fullAddress,
            Country = "Turkey"
        };

        var shipping = new IyzicoAddress
        {
            ContactName = contactName,
            City = address?.City ?? "Ankara",
            Address = fullAddress,
            Country = "Turkey"
        };

        return (buyer, billing, shipping);
    }

    private (IyzicoBuyer Buyer, IyzicoAddress Billing, IyzicoAddress Shipping) BuildGuestBuyerContext(
        CartOwner owner, Cart cart, GuestCheckoutViewModel guest)
    {
        var contactName = $"{guest.Name} {guest.Surname}".Trim();
        var fullAddress = string.Join(' ', new[]
        {
            guest.FullAddress,
            guest.Neighbourhood,
            guest.State,
            guest.City
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var phone = NormalizePhone(guest.Phone) ?? "+905555555555";
        var email = string.IsNullOrWhiteSpace(guest.Email) ? "no-reply@example.com" : guest.Email;

        var buyer = new IyzicoBuyer
        {
            Id = cart.AnonId ?? Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(guest.Name) ? "Müşteri" : guest.Name,
            Surname = guest.Surname ?? string.Empty,
            GsmNumber = phone,
            Email = email,
            IdentityNumber = DeriveIdentityNumber(new Address { PhoneNumber = guest.Phone }),
            RegistrationAddress = fullAddress,
            City = string.IsNullOrWhiteSpace(guest.City) ? "Ankara" : guest.City,
            Country = "Turkey",
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1"
        };

        var addr = new IyzicoAddress
        {
            ContactName = contactName,
            City = string.IsNullOrWhiteSpace(guest.City) ? "Ankara" : guest.City,
            Address = fullAddress,
            Country = "Turkey"
        };

        return (buyer, addr, addr);
    }

    private async Task<Address?> ResolveAddressAsync(int userId, CancellationToken cancellationToken)
    {
        var addresses = await _addressRepository.GetByUserAsync(userId, cancellationToken);
        return addresses.FirstOrDefault(a => a.IsDefault) ?? addresses.FirstOrDefault();
    }

    private async Task TrySaveCardFromResponseAsync(int userId, string? holderName, string? rawResponse,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            var root = doc.RootElement;

            string? cardUserKey = null;
            string? cardToken = null;
            string? cardAssociation = null;
            string? last4 = null;

            if (root.TryGetProperty("cardUserKey", out var cuk)) cardUserKey = cuk.GetString();
            if (cardUserKey is null && root.TryGetProperty("CardUserKey", out var cuk2)) cardUserKey = cuk2.GetString();

            if (root.TryGetProperty("cardToken", out var ctok)) cardToken = ctok.GetString();
            if (cardToken is null && root.TryGetProperty("CardToken", out var ctok2)) cardToken = ctok2.GetString();

            if (root.TryGetProperty("cardAssociation", out var assoc)) cardAssociation = assoc.GetString();
            if (cardAssociation is null && root.TryGetProperty("CardAssociation", out var assoc2))
                cardAssociation = assoc2.GetString();

            if (root.TryGetProperty("lastFourDigits", out var lastFour)) last4 = lastFour.GetString();
            if (last4 is null && root.TryGetProperty("LastFourDigits", out var lastFour2))
                last4 = lastFour2.GetString();

            if (!string.IsNullOrWhiteSpace(cardUserKey) && !string.IsNullOrWhiteSpace(cardToken))
            {
                await _savedCardRepository.AddOrReplaceAsync(new SavedCard
                {
                    UserId = userId,
                    CardUserKey = cardUserKey!,
                    CardToken = cardToken!,
                    Brand = cardAssociation ?? string.Empty,
                    Last4 = last4 ?? string.Empty,
                    Holder = holderName ?? string.Empty
                }, cancellationToken);
            }
        }
        catch
        {
            // Ignore parsing issues
        }
    }

    private static bool TryParseCard(PlaceOrderInputModel input, out IyzicoPaymentCard card, out string? error)
    {
        card = null!;
        error = null;

        var number = SanitizeDigits(input.Number);
        if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(number) ||
            string.IsNullOrWhiteSpace(input.Cvc))
        {
            error = "Kart bilgileri eksik.";
            return false;
        }

        if (number.Length < 13 || number.Length > 19)
        {
            error = "Kart numarası geçerli değil.";
            return false;
        }

        var cvc = SanitizeDigits(input.Cvc);
        if (cvc.Length < 3 || cvc.Length > 4)
        {
            error = "CVC bilgisi geçerli değil.";
            return false;
        }

        if (!TryParseExpiry(input.Exp, out var month, out var year))
        {
            error = "Son kullanma tarihi geçerli değil.";
            return false;
        }

        card = new IyzicoPaymentCard
        {
            HolderName = input.Name.Trim(),
            Number = number,
            ExpireMonth = month,
            ExpireYear = year,
            Cvc = cvc,
            RegisterCard = false
        };

        return true;
    }

    private static bool TryParseExpiry(string? exp, out string month, out string year)
    {
        month = string.Empty;
        year = string.Empty;
        if (string.IsNullOrWhiteSpace(exp))
        {
            return false;
        }

        var match = Regex.Match(exp, @"^(?<m>\d{1,2})\s*/\s*(?<y>\d{2,4})$");
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["m"].Value, out var m) || m < 1 || m > 12)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["y"].Value, out var y))
        {
            return false;
        }

        if (y < 100)
        {
            y += 2000;
        }

        month = m.ToString("00", CultureInfo.InvariantCulture);
        year = y.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static string DeriveIdentityNumber(Address? address)
    {
        var digits = SanitizeDigits(address?.PhoneNumber);
        if (!string.IsNullOrWhiteSpace(digits) && digits.Length >= 11)
        {
            return digits[^11..];
        }

        return "11111111111";
    }

    private static string? NormalizePhone(string? phone)
    {
        var digits = SanitizeDigits(phone);
        if (string.IsNullOrWhiteSpace(digits))
        {
            return null;
        }

        if (!digits.StartsWith("90", StringComparison.Ordinal))
        {
            digits = "90" + digits;
        }

        return "+" + digits;
    }

    private static string SanitizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(char.IsDigit).ToArray());
    }

    private static string ExtractTransactionId(string? rawResponse, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(rawResponse))
        {
            try
            {
                using var document = JsonDocument.Parse(rawResponse);
                var root = document.RootElement;
                if (root.TryGetProperty("paymentId", out var paymentId))
                {
                    return paymentId.ValueKind switch
                    {
                        JsonValueKind.String => paymentId.GetString() ?? fallback,
                        JsonValueKind.Number => paymentId.ToString() ?? fallback,
                        _ => fallback
                    };
                }

                if (root.TryGetProperty("PaymentId", out var paymentId2))
                {
                    return paymentId2.ValueKind switch
                    {
                        JsonValueKind.String => paymentId2.GetString() ?? fallback,
                        JsonValueKind.Number => paymentId2.ToString() ?? fallback,
                        _ => fallback
                    };
                }

                if (root.TryGetProperty("conversationId", out var conversationId)
                    && conversationId.ValueKind == JsonValueKind.String)
                {
                    return conversationId.GetString() ?? fallback;
                }

                if (root.TryGetProperty("ConversationId", out var conversationId2)
                    && conversationId2.ValueKind == JsonValueKind.String)
                {
                    return conversationId2.GetString() ?? fallback;
                }
            }
            catch (JsonException)
            {
                // ignored - fall back to supplied value
            }
        }

        return fallback;
    }

    private GuestCheckoutViewModel? ReadGuestInfoFromRequest()
    {
        try
        {
            if (Request.Cookies.TryGetValue(GuestInfoCookie, out var json) && !string.IsNullOrWhiteSpace(json))
            {
                var guest = System.Text.Json.JsonSerializer.Deserialize<GuestCheckoutViewModel>(json);
                return guest;
            }
        }
        catch
        {
            // ignore malformed cookie
        }

        return null;
    }

    private static GuestCheckoutViewModel? FromInput(PlaceOrderInputModel input)
    {
        if (string.IsNullOrWhiteSpace(input.GuestName) || string.IsNullOrWhiteSpace(input.GuestSurname) ||
            string.IsNullOrWhiteSpace(input.GuestFullAddress))
        {
            return null;
        }

        return new GuestCheckoutViewModel
        {
            Name = input.GuestName!,
            Surname = input.GuestSurname!,
            Email = input.GuestEmail ?? string.Empty,
            Phone = input.GuestPhone ?? string.Empty,
            City = input.GuestCity ?? string.Empty,
            State = input.GuestState ?? string.Empty,
            Neighbourhood = input.GuestNeighbourhood ?? string.Empty,
            FullAddress = input.GuestFullAddress!
        };
    }

    private ContentResult RenderThreeDSRedirect(string redirectUrl)
    {
        var safeUrl = string.IsNullOrWhiteSpace(redirectUrl) ? "/" : redirectUrl;
        var htmlEncoded = WebUtility.HtmlEncode(safeUrl);
        var jsEncoded = JavaScriptEncoder.Default.Encode(safeUrl);

        var html =
            $"<html><head><meta charset=\"utf-8\" /><script>window.top.location.href=\"{jsEncoded}\";</script></head>" +
            $"<body><noscript><meta http-equiv=\"refresh\" content=\"0;url={htmlEncoded}\" />" +
            $"<p>Devam etmek için <a href=\"{htmlEncoded}\">buraya tıklayın</a>.</p></noscript></body></html>";

        return Content(html, "text/html");
    }

    private async Task<(bool Success, string Message)> FinalizeThreeDSOrderAsync(
        string conversationId,
        string paymentId,
        PendingThreeDSContext context,
        IyzicoPaymentResult paymentResult,
        CancellationToken cancellationToken)
    {
        if (context.UserId is int userId)
        {
            var owner = CartOwner.FromUser(userId);
            var cart = await _cartService.GetAsync(owner, createIfMissing: false, cancellationToken);
            if (cart is null || cart.Items.Count == 0)
            {
                _logger.LogWarning(
                    "3D Secure finalize failed: cart not found. ConversationId: {ConversationId}, UserId: {UserId}",
                    conversationId, userId);
                return (false, "Sepetiniz bulunamadı veya boş olduğu için sipariş oluşturulamadı.");
            }

            var orderAddress = await ResolveAddressAsync(userId, cancellationToken);
            if (orderAddress is null)
            {
                _logger.LogWarning(
                    "3D Secure finalize failed: address not found. ConversationId: {ConversationId}, UserId: {UserId}",
                    conversationId, userId);
                return (false, "Sipariş oluşturmak için kayıtlı bir adres bulunamadı.");
            }

            var fxRate = await _currencyService.GetCachedUsdTryAsync(cancellationToken);
            var total = CalculateOrderTotal(cart, (decimal)fxRate) + context.ShippingPrice;
            var transactionId = ExtractTransactionId(paymentResult.RawResponse, paymentId);

            var order = new Order
            {
                OrderId = GenerateOrderNumber(),
                UserId = userId,
                AddressId = orderAddress.Id,
                Status = "Ödeme alındı",
                OrderDate = DateTime.UtcNow,
                TotalAmount = total,
                Items = cart.Items.Select(i => new OrderItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice
                }).ToList(),
                Payments =
                {
                    new PaymentTransaction
                    {
                        Amount = total,
                        TransactionDate = DateTime.UtcNow,
                        PaymentMethod = "iyzico-3ds",
                        TransactionId = transactionId
                    }
                },
                Shipment = new Shipment
                {
                    Carrier = ResolveCarrier(context.ShippingId),
                    TrackingNumber = string.Empty,
                    ShippedDate = DateTime.UtcNow
                }
            };

            await _orderRepository.InsertOrderAsync(order, cancellationToken);
            await _cartService.RemoveAllAsync(owner, cancellationToken);

            if (context.SaveCard && !context.UsedSavedCard && !string.IsNullOrWhiteSpace(context.CardHolderName))
            {
                await TrySaveCardFromResponseAsync(userId, context.CardHolderName, paymentResult.RawResponse,
                    cancellationToken);
            }

            _logger.LogInformation(
                "3D Secure payment finalized for user. ConversationId: {ConversationId}, OrderNumber: {OrderNumber}",
                conversationId, order.OrderId);

            var message =
                $"Ödemeniz başarıyla tamamlandı. Sipariş numaranız: {order.OrderId.ToString("D6", CultureInfo.InvariantCulture)}";
            return (true, message);
        }
        else
        {
            var anonId = context.AnonId;
            var guest = context.GuestInfo;
            if (string.IsNullOrWhiteSpace(anonId) || guest is null)
            {
                _logger.LogWarning("3D Secure finalize failed: guest context missing. ConversationId: {ConversationId}",
                    conversationId);
                return (false, "Misafir sipariş bilgileri bulunamadı.");
            }

            var owner = CartOwner.FromAnon(anonId);
            var cart = await _cartService.GetAsync(owner, createIfMissing: false, cancellationToken);
            if (cart is null || cart.Items.Count == 0)
            {
                _logger.LogWarning(
                    "3D Secure finalize failed: guest cart missing. ConversationId: {ConversationId}, AnonId: {AnonId}",
                    conversationId, anonId);
                return (false, "Sepetiniz bulunamadı veya boş olduğu için sipariş oluşturulamadı.");
            }

            var fxRate = await _currencyService.GetCachedUsdTryAsync(cancellationToken);
            var total = CalculateOrderTotal(cart, (decimal)fxRate) + context.ShippingPrice;
            var transactionId = ExtractTransactionId(paymentResult.RawResponse, paymentId);

            var order = new Order
            {
                OrderId = GenerateOrderNumber(),
                UserId = null,
                AddressId = null,
                AnonId = cart.AnonId,
                BuyerName = $"{guest.Name} {guest.Surname}".Trim(),
                BuyerEmail = guest.Email,
                BuyerPhone = SanitizeDigits(guest.Phone),
                ShippingAddressText = guest.FullAddress,
                ShippingNeighbourhood = guest.Neighbourhood,
                ShippingState = guest.State,
                ShippingCity = guest.City,
                Status = "Ödeme alındı",
                OrderDate = DateTime.UtcNow,
                TotalAmount = total,
                Items = cart.Items.Select(i => new OrderItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice
                }).ToList(),
                Payments =
                {
                    new PaymentTransaction
                    {
                        Amount = total,
                        TransactionDate = DateTime.UtcNow,
                        PaymentMethod = "iyzico-3ds",
                        TransactionId = transactionId
                    }
                },
                Shipment = new Shipment
                {
                    Carrier = ResolveCarrier(context.ShippingId),
                    TrackingNumber = string.Empty,
                    ShippedDate = DateTime.UtcNow
                }
            };

            await _orderRepository.InsertOrderAsync(order, cancellationToken);
            await _cartService.RemoveAllAsync(owner, cancellationToken);

            _logger.LogInformation(
                "3D Secure payment finalized for guest. ConversationId: {ConversationId}, OrderNumber: {OrderNumber}, AnonId: {AnonId}",
                conversationId,
                order.OrderId,
                cart.AnonId);

            var message =
                $"Ödemeniz başarıyla tamamlandı. Sipariş numaranız: {order.OrderId.ToString("D6", CultureInfo.InvariantCulture)}";
            return (true, message);
        }
    }

    private static bool IsCardStorageNotEnabled(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return false;
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            var root = doc.RootElement;
            // errorCode can be in different casing depending on serializer
            if (root.TryGetProperty("errorCode", out var ec))
            {
                if (ec.ValueKind == JsonValueKind.String &&
                    string.Equals(ec.GetString(), "3007", StringComparison.Ordinal)) return true;
                if (ec.ValueKind == JsonValueKind.Number && ec.TryGetInt32(out var eci) && eci == 3007) return true;
            }

            if (root.TryGetProperty("ErrorCode", out var ec2))
            {
                if (ec2.ValueKind == JsonValueKind.String &&
                    string.Equals(ec2.GetString(), "3007", StringComparison.Ordinal)) return true;
                if (ec2.ValueKind == JsonValueKind.Number && ec2.TryGetInt32(out var eci2) && eci2 == 3007) return true;
            }

            if (root.TryGetProperty("errorMessage", out var em) && em.ValueKind == JsonValueKind.String)
            {
                var msg = em.GetString();
                if (!string.IsNullOrWhiteSpace(msg) &&
                    msg.Contains("kart saklama", StringComparison.OrdinalIgnoreCase)) return true;
            }

            if (root.TryGetProperty("ErrorMessage", out var em2) && em2.ValueKind == JsonValueKind.String)
            {
                var msg = em2.GetString();
                if (!string.IsNullOrWhiteSpace(msg) &&
                    msg.Contains("kart saklama", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch
        {
            // ignore parse errors, fallback to substring check
        }

        // last resort substring check
        return rawResponse.Contains("3007", StringComparison.Ordinal);
    }
}
