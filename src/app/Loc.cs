using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace QFTPlus;

internal static class Loc
{
    static readonly ResourceManager Resources = new("QFTPlus.Localization.Strings", typeof(Loc).Assembly);

    internal static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) is { Length: > 0 } translated
        ? translated : Resources.GetString(key, CultureInfo.InvariantCulture) ?? key;

    internal static string Format(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, Get(key), args); }
        catch (FormatException)
        {
            // A malformed community translation must not interrupt tracking or setup.
            return string.Format(CultureInfo.CurrentCulture, Resources.GetString(key, CultureInfo.InvariantCulture) ?? key, args);
        }
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(key);
}
