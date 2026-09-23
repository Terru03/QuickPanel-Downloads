using System;

namespace QuickPanel.Services;

public static class DiagnosticPrivacy
{
    public static string FormatPageOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return "None";
        }

        try
        {
            UriBuilder sanitized = new(uri.Scheme, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };
            return sanitized.Uri.GetLeftPart(UriPartial.Authority);
        }
        catch (UriFormatException)
        {
            return "None";
        }
    }
}
