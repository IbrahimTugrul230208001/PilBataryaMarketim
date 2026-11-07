using ECommerceBatteryShop.DataAccess.Abstract;
using ECommerceBatteryShop.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace ECommerceBatteryShop.DataAccess.Concrete;

public class SavedCardRepository : ISavedCardRepository
{
    private readonly BatteryShopContext _ctx;
    public SavedCardRepository(BatteryShopContext ctx) { _ctx = ctx; }

    public async Task<IReadOnlyList<SavedCard>> GetByUserAsync(int userId, CancellationToken ct = default)
    {
        return await _ctx.SavedCards
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<SavedCard?> GetByTokenAsync(int userId, string cardToken, CancellationToken ct = default)
    {
        return await _ctx.SavedCards.FirstOrDefaultAsync(c => c.UserId == userId && c.CardToken == cardToken, ct);
    }

    public async Task AddAsync(SavedCard card, CancellationToken ct = default)
    {
        await _ctx.SavedCards.AddAsync(card, ct);
        await _ctx.SaveChangesAsync(ct);
    }

    public async Task AddOrReplaceAsync(SavedCard card, CancellationToken ct = default)
    {
        // Iyzico issues a new CardToken every time the same card is registered again, so the token
        // alone can't detect duplicates. Brand + Last4 identifies "the same card" from the user's view.
        var hasCardIdentity = !string.IsNullOrWhiteSpace(card.Last4);

        var existing = await _ctx.SavedCards.FirstOrDefaultAsync(c =>
            c.UserId == card.UserId &&
            (c.CardToken == card.CardToken ||
             (hasCardIdentity && c.Last4 == card.Last4 && c.Brand == card.Brand)), ct);

        if (existing is null)
        {
            await _ctx.SavedCards.AddAsync(card, ct);
        }
        else
        {
            // Keep one row per card, pointing at the newest (known-valid) token.
            existing.CardUserKey = card.CardUserKey;
            existing.CardToken = card.CardToken;
            existing.Holder = card.Holder;
            existing.CreatedAt = DateTime.UtcNow;
        }

        await _ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int userId, int id, CancellationToken ct = default)
    {
        var entity = await _ctx.SavedCards.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (entity is null) return;
        _ctx.SavedCards.Remove(entity);
        await _ctx.SaveChangesAsync(ct);
    }
}
