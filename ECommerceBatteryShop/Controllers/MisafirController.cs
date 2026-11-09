using ECommerceBatteryShop.Infrastructure;
using ECommerceBatteryShop.Models;
using Microsoft.AspNetCore.Mvc;

namespace ECommerceBatteryShop.Controllers;

public class MisafirController : Controller
{
    [HttpGet]
    public IActionResult Index()
    {
        // "Bilgileri düzenle" on checkout links here: prefill the form from the guest cookie if there is one,
        // so the guest only changes what's wrong instead of retyping everything.
        return View(GuestInfoCookie.Read(Request) ?? new GuestCheckoutViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Index(GuestCheckoutViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        GuestInfoCookie.Write(Response, model);
        return RedirectToAction("Siparis", "Sepet");
    }
}
