using System.Security.Claims;
using ECommerceBatteryShop.DataAccess;
using ECommerceBatteryShop.Models;
using ECommerceBatteryShop.Services;
using Microsoft.AspNetCore.Mvc;

namespace ECommerceBatteryShop.Controllers;

public class MisafirController : Controller
{
    private const string GuestInfoCookie = "GUEST_INFO";

    [HttpGet]
    public IActionResult Index()
    {
        // "Bilgileri düzenle" on checkout links here: prefill the form from the guest cookie if there is one,
        // so the guest only changes what's wrong instead of retyping everything.
        return View(ReadGuestInfo() ?? new GuestCheckoutViewModel());
    }

    private GuestCheckoutViewModel? ReadGuestInfo()
    {
        if (!Request.Cookies.TryGetValue(GuestInfoCookie, out var json) || string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<GuestCheckoutViewModel>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // malformed or tampered cookie: show an empty form
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Index(GuestCheckoutViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // Persist minimal guest data in a cookie for checkout
        Response.Cookies.Append(GuestInfoCookie, System.Text.Json.JsonSerializer.Serialize(new
        {
            model.Name,
            model.Surname,
            model.Email,
            model.Phone,
            model.City,
            model.State,
            model.Neighbourhood,
            model.FullAddress
        }), new CookieOptions
        {
            IsEssential = true,
            HttpOnly = false,
            Secure = false,
            Expires = DateTimeOffset.UtcNow.AddHours(4)
        });

        return RedirectToAction("Siparis", "Sepet");
    }
}
