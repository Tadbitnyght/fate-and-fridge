using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Read-only view over an <see cref="IRequestCookieCollection"/> that hides a
/// single cookie (the antiforgery cookie during a mid-request session re-mint)
/// while returning every other parsed cookie exactly as the framework parsed
/// it. Nothing re-serializes the raw <c>Cookie</c> header, so percent-encoded
/// and quoted values in unrelated cookies are preserved untouched.
/// </summary>
public sealed class FilteredRequestCookieCollection : IRequestCookieCollection
{
    private readonly IRequestCookieCollection _inner;
    private readonly string _excludedName;

    public FilteredRequestCookieCollection(IRequestCookieCollection inner, string excludedName)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(excludedName);

        _inner = inner;
        _excludedName = excludedName;
    }

    public int Count => _inner.Count(pair => !IsExcluded(pair.Key));

    public ICollection<string> Keys => _inner.Keys.Where(key => !IsExcluded(key)).ToArray();

    public string? this[string key] => IsExcluded(key) ? null : _inner[key];

    public bool ContainsKey(string key) => !IsExcluded(key) && _inner.ContainsKey(key);

    public bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
    {
        if (IsExcluded(key))
        {
            value = null;
            return false;
        }

        return _inner.TryGetValue(key, out value);
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
        _inner.Where(pair => !IsExcluded(pair.Key)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Cookie names are matched case-insensitively (the framework's cookie
    /// collection is case-insensitive), and every member derives from the same
    /// predicate so a mixed-case excluded cookie is fully hidden.
    /// </summary>
    private bool IsExcluded(string key) =>
        string.Equals(key, _excludedName, StringComparison.OrdinalIgnoreCase);
}
