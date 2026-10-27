using Microsoft.AspNetCore.Antiforgery;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Binds antiforgery request tokens to the resolved session by embedding the
/// session's stored token hash in the token (docs/SECURITY.md §2/§15). A token
/// minted for one session is therefore rejected for another session, and tokens
/// stop validating after <see cref="SessionService.RotateAsync"/> because the
/// stored hash changes.
/// </summary>
public sealed class SessionAntiforgeryDataProvider : IAntiforgeryAdditionalDataProvider
{
    public string GetAdditionalData(HttpContext context) =>
        context.Items[SessionAuthenticationDefaults.SessionTokenHashItemKey] as string
        ?? string.Empty;

    public bool ValidateAdditionalData(HttpContext context, string additionalData) =>
        string.Equals(additionalData, GetAdditionalData(context), StringComparison.Ordinal);
}
