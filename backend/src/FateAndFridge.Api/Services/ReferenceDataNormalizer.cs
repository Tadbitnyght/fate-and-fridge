using System.Globalization;
using System.Text;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Why a reference-data name or alias cannot be normalized. Endpoints
/// map these reasons to <c>400 VALIDATION_FAILED</c> field errors.
/// </summary>
public enum ReferenceDataNormalizationFailureReason
{
    /// <summary>Null, blank after trimming, or empty once diacritics are folded.</summary>
    Empty,

    /// <summary>The value contains Unicode control or format characters.</summary>
    ControlCharacters,
}

/// <summary>Outcome of <see cref="ReferenceDataNormalizer.Normalize"/>.</summary>
public abstract record ReferenceDataNormalizationResult
{
    private ReferenceDataNormalizationResult()
    {
    }

    /// <summary>
    /// The valid pair stored by reference-data writes and used for matching
    /// <see cref="Display"/> keeps the caller's case and diacritics (only
    /// trimming and whitespace collapse applied), while <see cref="Normalized"/>
    /// is the canonical lookup form that drives uniqueness and search.
    /// </summary>
    public sealed record Success(string Display, string Normalized) : ReferenceDataNormalizationResult;

    /// <summary>The value is not a usable name or alias.</summary>
    public sealed record Invalid(ReferenceDataNormalizationFailureReason Reason) : ReferenceDataNormalizationResult;
}

/// <summary>
/// Canonical normalization for reference-data names and aliases: trim, collapse internal whitespace, fold Unicode
/// diacritics, and lowercase with the invariant culture. Plurals are explicit
/// aliases, so there is no plural folding, punctuation removal, or any other
/// transformation.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is fixed and idempotent: trim, then collapse every run of
/// Unicode whitespace to one space, then fold diacritics, then trim and
/// collapse again (folding can remove marks that sat between whitespace), then
/// invariant lowercase. <c>Normalized</c> is therefore always trimmed and
/// whitespace-collapsed, and normalizing it again returns it unchanged, so
/// writes and searches always compare the same text.
/// </para>
/// <para>
/// Diacritic folding is table-driven and ICU-independent, so it also works when
/// NET runs in globalization-invariant mode (<c>String.Normalize</c> is a
/// no-op there). Folding strips non-spacing marks in every script: that handles
/// decomposed input such as <c>Jalapeño</c>, but it also folds marked
/// forms of mark-based scripts onto their unmarked form (for example Devanagari
/// <c>कु</c> → <c>क</c> and Arabic <c>بَيْت</c> → <c>بيت</c>), which is a
/// known limitation for those scripts.
/// </para>
/// <para>
/// The composed-letter table maps Latin-1 Supplement letters (all letters
/// U+00C0–U+00FF except × and ÷), all of Latin Extended-A (U+0100–U+017F
/// including Ø, Æ, Œ, ß, Đ, Ł, ı, İ, Ŋ, and ſ), and Greek tonos/dialytika
/// vowels in U+0370–U+03FF (including final sigma and the upsilon hook symbols
/// U+03D3/U+03D4) to their base letters: <c>Jalapeño</c> → <c>jalapeno</c>
/// <c>Łódź</c> → <c>lodz</c>, <c>Σόφια</c> → <c>σοφια</c>. Composed Latin
/// Extended-B and Latin Extended Additional letters are not folded and pass
/// through; their decomposed forms still fold through non-spacing-mark
/// stripping. Extending the table is a V2/M2.x enhancement. There is no
/// transliteration and no plural folding.
/// </para>
/// <para>
/// The type is pure and dependency-free; it reads no database. Control and
/// format characters are rejected before trimming and collapsing, so
/// unintended whitespace can never be silently erased. Tab, line feed, and
/// carriage return immediately before a line feed are the allowed whitespace
/// exceptions (lone carriage returns are rejected).
/// </para>
/// </remarks>
public static class ReferenceDataNormalizer
{
    /// <summary>
    /// Composed letters whose base form is not reached by stripping a combining
    /// mark, keyed to that base form. Case is preserved because invariant
    /// lowercasing runs after folding.
    /// </summary>
    private static readonly Dictionary<char, string> ComposedLetterBaseLetters = BuildComposedLetterTable();

    /// <summary>
    /// Normalizes one name or alias. Expected invalid input returns
    /// <see cref="ReferenceDataNormalizationResult.Invalid"/> rather than
    /// throwing.
    /// </summary>
    public static ReferenceDataNormalizationResult Normalize(string? value)
    {
        if (value is null)
        {
            return Invalid(ReferenceDataNormalizationFailureReason.Empty);
        }

        if (ContainsDisallowedControlCharacter(value))
        {
            return Invalid(ReferenceDataNormalizationFailureReason.ControlCharacters);
        }

        var display = CollapseWhitespace(value.Trim());
        if (display.Length == 0)
        {
            return Invalid(ReferenceDataNormalizationFailureReason.Empty);
        }

        // Stripping marks can expose whitespace runs that were not adjacent
        // before folding, so the folded form is trimmed and collapsed again to
        // keep Normalized a fixed point of this method.
        var folded = CollapseWhitespace(FoldDiacritics(display).Trim());
        if (folded.Length == 0)
        {
            return Invalid(ReferenceDataNormalizationFailureReason.Empty);
        }

        return new ReferenceDataNormalizationResult.Success(display, folded.ToLowerInvariant());
    }

    private static ReferenceDataNormalizationResult Invalid(ReferenceDataNormalizationFailureReason reason) =>
        new ReferenceDataNormalizationResult.Invalid(reason);

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }
            }
            else
            {
                builder.Append(character);
                previousWasWhitespace = false;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Folds diacritics without relying on ICU normalization (which is a no-op
    /// in globalization-invariant mode). Pass one drops non-spacing marks in
    /// every script, so already-decomposed input folds too; pass two replaces
    /// composed letters listed in <see cref="ComposedLetterBaseLetters"/> with
    /// their base letters. Every other character is kept as is.
    /// </summary>
    private static string FoldDiacritics(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (ComposedLetterBaseLetters.TryGetValue(character, out var baseLetters))
            {
                builder.Append(baseLetters);
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool ContainsDisallowedControlCharacter(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '\t' or '\n')
            {
                continue;
            }

            if (character == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
            {
                continue;
            }

            if (char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<char, string> BuildComposedLetterTable()
    {
        var table = new Dictionary<char, string>();

        // Latin-1 Supplement and Latin Extended-A: accented letters plus the
        // non-decomposing stroke, horn, ligature, and long-s letters.
        Map(table, "A", "\u00C0\u00C1\u00C2\u00C3\u00C4\u00C5\u0100\u0102\u0104"); // À Á Â Ã Ä Å Ā Ă Ą
        Map(table, "a", "\u00E0\u00E1\u00E2\u00E3\u00E4\u00E5\u0101\u0103\u0105"); // à á â ã ä å ā ă ą
        Map(table, "AE", "\u00C6"); // Æ
        Map(table, "ae", "\u00E6"); // æ
        Map(table, "C", "\u00C7\u0106\u0108\u010A\u010C"); // Ç Ć Ĉ Ċ Č
        Map(table, "c", "\u00E7\u0107\u0109\u010B\u010D"); // ç ć ĉ ċ č
        Map(table, "D", "\u00D0\u010E\u0110"); // Ð Ď Đ
        Map(table, "d", "\u00F0\u010F\u0111"); // ð ď đ
        Map(table, "E", "\u00C8\u00C9\u00CA\u00CB\u0112\u0114\u0116\u0118\u011A"); // È É Ê Ë Ē Ĕ Ė Ę Ě
        Map(table, "e", "\u00E8\u00E9\u00EA\u00EB\u0113\u0115\u0117\u0119\u011B"); // è é ê ë ē ĕ ė ę ě
        Map(table, "G", "\u011C\u011E\u0120\u0122"); // Ĝ Ğ Ġ Ģ
        Map(table, "g", "\u011D\u011F\u0121\u0123"); // ĝ ğ ġ ģ
        Map(table, "H", "\u0124\u0126"); // Ĥ Ħ
        Map(table, "h", "\u0125\u0127"); // ĥ ħ
        Map(table, "I", "\u00CC\u00CD\u00CE\u00CF\u0128\u012A\u012C\u012E\u0130"); // Ì Í Î Ï Ĩ Ī Ĭ Į İ
        Map(table, "i", "\u00EC\u00ED\u00EE\u00EF\u0129\u012B\u012D\u012F\u0131"); // ì í î ï ĩ ī ĭ į ı
        Map(table, "IJ", "\u0132"); // Ĳ
        Map(table, "ij", "\u0133"); // ĳ
        Map(table, "J", "\u0134"); // Ĵ
        Map(table, "j", "\u0135"); // ĵ
        Map(table, "K", "\u0136"); // Ķ
        Map(table, "k", "\u0137\u0138"); // ķ ĸ
        Map(table, "L", "\u0139\u013B\u013D\u013F\u0141"); // Ĺ Ļ Ľ Ŀ Ł
        Map(table, "l", "\u013A\u013C\u013E\u0140\u0142"); // ĺ ļ ľ ŀ ł
        Map(table, "N", "\u00D1\u0143\u0145\u0147\u014A"); // Ñ Ń Ņ Ň Ŋ
        Map(table, "n", "\u00F1\u0144\u0146\u0148\u014B"); // ñ ń ņ ň ŋ
        Map(table, "O", "\u00D2\u00D3\u00D4\u00D5\u00D6\u00D8\u014C\u014E\u0150"); // Ò Ó Ô Õ Ö Ø Ō Ŏ Ő
        Map(table, "o", "\u00F2\u00F3\u00F4\u00F5\u00F6\u00F8\u014D\u014F\u0151"); // ò ó ô õ ö ø ō ŏ ő
        Map(table, "OE", "\u0152"); // Œ
        Map(table, "oe", "\u0153"); // œ
        Map(table, "R", "\u0154\u0156\u0158"); // Ŕ Ŗ Ř
        Map(table, "r", "\u0155\u0157\u0159"); // ŕ ŗ ř
        Map(table, "S", "\u015A\u015C\u015E\u0160\u017F"); // Ś Ŝ Ş Š ſ
        Map(table, "s", "\u015B\u015D\u015F\u0161"); // ś ŝ ş š
        Map(table, "T", "\u0162\u0164\u0166"); // Ţ Ť Ŧ
        Map(table, "t", "\u0163\u0165\u0167"); // ţ ť ŧ
        Map(table, "U", "\u00D9\u00DA\u00DB\u00DC\u0168\u016A\u016C\u016E\u0170\u0172"); // Ù Ú Û Ü Ũ Ū Ŭ Ů Ű Ų
        Map(table, "u", "\u00F9\u00FA\u00FB\u00FC\u0169\u016B\u016D\u016F\u0171\u0173"); // ù ú û ü ũ ū ŭ ů ű ų
        Map(table, "W", "\u0174"); // Ŵ
        Map(table, "w", "\u0175"); // ŵ
        Map(table, "Y", "\u00DD\u0176\u0178"); // Ý Ŷ Ÿ
        Map(table, "y", "\u00FD\u00FF\u0177"); // ý ÿ ŷ
        Map(table, "Z", "\u0179\u017B\u017D"); // Ź Ż Ž
        Map(table, "z", "\u017A\u017C\u017E"); // ź ż ž
        Map(table, "Th", "\u00DE"); // Þ
        Map(table, "th", "\u00FE"); // þ
        Map(table, "ss", "\u00DF"); // ß
        Map(table, "SS", "\u1E9E"); // ẞ
        Map(table, "\u02BCn", "\u0149"); // ŉ (apostrophe + n, matching its compatibility decomposition)

        // Greek tonos/dialytika vowels and final sigma fold to the plain
        // letters.
        Map(table, "\u0391", "\u0386"); // Ά
        Map(table, "\u0395", "\u0388"); // Έ
        Map(table, "\u0397", "\u0389"); // Ή
        Map(table, "\u0399", "\u038A\u03AA"); // Ί Ϊ
        Map(table, "\u039F", "\u038C"); // Ό
        Map(table, "\u03A5", "\u038E\u03AB\u03D3\u03D4"); // Ύ Ϋ ϓ ϔ
        Map(table, "\u03A9", "\u038F"); // Ώ
        Map(table, "\u03B1", "\u03AC"); // ά
        Map(table, "\u03B5", "\u03AD"); // έ
        Map(table, "\u03B7", "\u03AE"); // ή
        Map(table, "\u03B9", "\u03AF\u03CA\u0390"); // ί ϊ ΐ
        Map(table, "\u03BF", "\u03CC"); // ό
        Map(table, "\u03C3", "\u03C2"); // ς
        Map(table, "\u03C5", "\u03CD\u03CB\u03B0"); // ύ ϋ ΰ
        Map(table, "\u03C9", "\u03CE"); // ώ

        return table;
    }

    private static void Map(Dictionary<char, string> table, string baseLetters, string composedLetters)
    {
        foreach (var composed in composedLetters)
        {
            table.Add(composed, baseLetters);
        }
    }
}
