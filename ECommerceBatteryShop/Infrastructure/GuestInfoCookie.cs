using System.Text.Json;
using ECommerceBatteryShop.Models;

namespace ECommerceBatteryShop.Infrastructure;

/// <summary>
/// Single source of truth for the guest-checkout cookie (GUEST_INFO) and its policy.
/// Previously the read logic was copy-pasted in Misafir/Sepet/Siparis/Hesap controllers,
/// each with its own constant, and the write used HttpOnly=false / Secure=false.
/// </summary>
public static class GuestInfoCookie
{
    public const string CookieName = "GUEST_INFO";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    /// <summary>Returns the guest details from the cookie, or null if absent, empty or malformed.</summary>
    public static GuestCheckoutViewModel? Read(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GuestCheckoutViewModel>(json);
        }
        catch (JsonException)
        {
            return null; // malformed or tampered cookie: treat as "no guest info"
        }
    }

    /// <summary>Stores the guest details for checkout, with one cookie policy for the whole app.</summary>
    public static void Write(HttpResponse response, GuestCheckoutViewModel model)
    {
        response.Cookies.Append(CookieName, JsonSerializer.Serialize(model), new CookieOptions
        {
            HttpOnly = true,                               // no script reads it; keeps the address/phone away from JS
            IsEssential = true,                            // needed for guest checkout, not blocked by consent
            SameSite = SameSiteMode.Lax,
            Secure = response.HttpContext.Request.IsHttps, // true on prod (HTTPS), false on local http://localhost
            Expires = DateTimeOffset.UtcNow.Add(Lifetime)
        });
    }
}
