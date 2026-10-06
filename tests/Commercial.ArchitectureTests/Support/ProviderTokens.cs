using System.Text;

namespace Commercial.ArchitectureTests.Support;

/// <summary>
/// Exact provider-token matcher (ARCHITECTURE_TEST_CONTRACT §4).
///
/// Matching is done on WORD SEQUENCES, not raw substrings: identifiers are split into words on
/// PascalCase/camelCase/acronym boundaries, '.', '_' and '-', and a token matches only when its
/// own word sequence appears contiguously (case-insensitive). Consequences:
///   CompanyLogo  -> [Company, Logo]        no token
///   LogoUrl      -> [Logo, Url]            no token (plain "Logo" is deliberately NOT a token)
///   Sapling...   -> [Sapling, ...]         no token (plain "SAP" is deliberately NOT a token)
///   LogoTigerX   -> [Logo, Tiger, X]       matches LogoTiger
///   BusinessOSClient -> [Business, OS, Client] matches BusinessOS
///
/// Generic English words are never tokens. The "Winner" ERP is therefore represented by the
/// exact token <c>WinnerErp</c> (plain "Winner" would flag e.g. <c>WinnerBadge</c>), mirroring
/// the Logo/SAP policy. New connectors add their exact root token here.
/// </summary>
public static class ProviderTokens
{
    public static readonly IReadOnlyList<string> All =
    [
        "Kumpara",
        "BusinessOS",
        "LogoTiger",
        "LogoGo",
        "LogoIsbasi",
        "LogoErp",
        "MikroErp",
        "Netsis",
        "Parasut",
        "Nebim",
        "NebimV3",
        "WinnerErp",
        "SapBusinessOne",
    ];

    private static readonly IReadOnlyList<(string Token, string[] Words)> TokenWords =
        All.Select(t => (t, SplitWords(t))).ToList();

    /// <summary>Returns the first provider token found in <paramref name="value"/>, or <c>null</c>.</summary>
    public static string? Find(string value)
    {
        var words = SplitWords(value);
        foreach (var (token, tokenWords) in TokenWords)
        {
            if (ContainsSequence(words, tokenWords))
            {
                return token;
            }
        }

        return null;
    }

    public static string[] SplitWords(string value)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                var prev = value[i - 1];
                var next = i + 1 < value.Length ? value[i + 1] : '\0';
                var lowerToUpper = char.IsUpper(c) && (char.IsLower(prev) || char.IsDigit(prev));
                var acronymEnd = char.IsUpper(c) && char.IsUpper(prev) && char.IsLower(next);
                if (lowerToUpper || acronymEnd)
                {
                    Flush();
                }
            }

            current.Append(c);
        }

        Flush();
        return words.ToArray();
    }

    private static bool ContainsSequence(string[] words, string[] sequence)
    {
        for (var start = 0; start + sequence.Length <= words.Length; start++)
        {
            var match = true;
            for (var j = 0; j < sequence.Length; j++)
            {
                if (!string.Equals(words[start + j], sequence[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
