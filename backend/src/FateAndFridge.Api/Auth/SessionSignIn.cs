using FateAndFridge.Api.Domain;
using Microsoft.AspNetCore.Antiforgery;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Establishes the browser session after login or setup completion: writes the
/// persistent session cookie, swaps the request principal to the new session
/// and mints the antiforgery pair bound to the new token hash and user. Shared
/// so the two entry points cannot drift.
/// </summary>
public static class SessionSignIn
{
    public static void Apply(
        HttpContext context,
        User user,
        UserSession session,
        string rawToken,
        IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        ArgumentNullException.ThrowIfNull(antiforgery);

        // Persistent cookie for the 30-day idle window (docs/API.md §5).
        SessionCookie.Append(context.Response, rawToken, session.IdleExpiresAt);

        // Swap the principal so the freshly minted antiforgery pair is bound to
        // the new session token hash and user id; the CSRF middleware ran while
        // the request was still unauthenticated.
        context.User = SessionClaims.Create(user, session.Id);
        context.Items[SessionAuthenticationDefaults.SessionTokenHashItemKey] =
            Convert.ToHexString(SessionTokenHasher.Hash(rawToken));

        var tokenSet = antiforgery.GetAndStoreTokens(context);
        if (!string.IsNullOrEmpty(tokenSet.HeaderName))
        {
            context.Response.Headers[tokenSet.HeaderName] = tokenSet.RequestToken;
        }
    }
}
