using ECommerceBatteryShop.DataAccess.Entities;

namespace ECommerceBatteryShop.DataAccess.Abstract;

public interface ISavedCardRepository
{
    Task<IReadOnlyList<SavedCard>> GetByUserAsync(int userId, CancellationToken ct = default);
    Task<SavedCard?> GetByTokenAsync(int userId, string cardToken, CancellationToken ct = default);
    Task AddAsync(SavedCard card, CancellationToken ct = default);

    /// <summary>
    /// Saves a card without creating duplicates: if the user already has a card with the same
    /// brand + last 4 digits (or the same token), that row is updated with the new token instead.
    /// </summary>
    Task AddOrReplaceAsync(SavedCard card, CancellationToken ct = default);
    Task DeleteAsync(int userId, int id, CancellationToken ct = default);
}
