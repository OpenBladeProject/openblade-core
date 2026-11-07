using System.Globalization;

namespace OpenBlade.Tray;

public static class GlobalSearchRanking
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public static int? Score(
        string[] terms, string query, string title, string breadcrumb, string aliases, CompareInfo comparison)
    {
        if (terms.Length == 0)
        {
            return null;
        }

        if (comparison.Compare(title, query, Options) == 0)
        {
            return 0;
        }

        if (comparison.IsPrefix(title, query, Options))
        {
            return 1;
        }

        if (Contains(title, query, comparison))
        {
            return 2;
        }

        if (terms.All(term => Contains(title, term, comparison)))
        {
            return 3;
        }

        return terms.All(term => Contains(title, term, comparison)
            || Contains(breadcrumb, term, comparison)
            || Contains(aliases, term, comparison))
            ? 4
            : null;
    }

    private static bool Contains(string text, string term, CompareInfo comparison) =>
        comparison.IndexOf(text, term, Options) >= 0;
}
