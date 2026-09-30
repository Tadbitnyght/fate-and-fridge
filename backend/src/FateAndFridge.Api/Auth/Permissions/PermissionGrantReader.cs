using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Auth.Permissions;

/// <summary>
/// Reads a user's raw granted permission keys. Deliberately uncached: rows are
/// read on every request so permission changes take effect immediately
/// (docs/DATABASE.md §2: "sessions do not cache permissions").
/// </summary>
public interface IPermissionGrantReader
{
    Task<IReadOnlyList<string>> ReadGrantedKeysAsync(
        long userId,
        CancellationToken cancellationToken = default);
}

/// <summary>Database-backed <see cref="IPermissionGrantReader"/>.</summary>
public sealed class DbPermissionGrantReader(AppDbContext dbContext) : IPermissionGrantReader
{
    public async Task<IReadOnlyList<string>> ReadGrantedKeysAsync(
        long userId,
        CancellationToken cancellationToken = default) =>
        await dbContext.UserPermissions
            .AsNoTracking()
            .Where(permission => permission.UserId == userId)
            .Select(permission => permission.PermissionKey)
            .ToListAsync(cancellationToken);
}
