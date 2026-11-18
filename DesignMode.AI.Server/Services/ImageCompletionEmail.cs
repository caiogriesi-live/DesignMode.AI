
using System.Net;
using DesignMode.AI.Server.Services;

namespace DesignMode.AI.Server.Services;

public static class ImageCompletionEmail
{
    public static FunnelRecoveryEmail Create(
        string[] imageUrls,
        string resumeUrl)
    {
        if (imageUrls.Length != 3 ||
            imageUrls.Any(x =>
                !Uri.TryCreate(x, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Three HTTPS images required.");

        static string E(string value) => WebUtility.HtmlEncode(value);


        var cards = string.Join("", imageUrls.Select((url, i) => $"""
    <td width="33.33%" valign="top"
        style="padding:0 5px">
        <a href="{E(resumeUrl)}"
           style="display:block;text-decoration:none">
            <img src="{E(url)}"
                 width="185"
                 alt="Concept {i + 1}"
                 style="display:block;width:100%;height:auto;border:0;border-radius:10px">
        </a>
        <p style="text-align:center;font-size:13px;font-weight:bold;color:#111321;margin:10px 0">
            Concept {i + 1}
        </p>
    </td>
    """));


        const string subject = "Your three DesignMode concepts are ready!";
        var html = $"""
            <!doctype html>
            <html>
            <body style="margin:0;padding:24px;background:#f3f3f6;font-family:Arial,sans-serif">
            <table role="presentation" align="center" width="100%"
                   style="max-width:664px;background:white;border-radius:16px">
            <tr><td style="padding:32px">
                <h2 style="color:#111321">
                    Design<span style="color:#6936d8">Mode</span>
                </h2>
                <hr style="border:0;border-top:1px solid #dedfe8">
                <p style="text-align:center;color:#16a34a">
                    ✓ YOUR DESIGNS ARE READY
                </p>
                <h1 style="text-align:center;font-size:34px;color:#111321">
                    Your three design concepts are ready!
                </h1>
                <p style="text-align:center;color:#626b82;line-height:1.6">
                    Explore three unique creative directions made just for your brand.
                </p>
                <table role="presentation" width="100%">
                    <tr>{cards}</tr>
                </table>
                <p style="text-align:center;margin:32px 0">
                    <a href="{E(resumeUrl)}"
                       style="background:#592ce0;color:white;padding:18px 32px;
                              border-radius:40px;text-decoration:none;font-weight:bold">
                        View my designs &rarr;
                    </a>
                </p>
                <p style="text-align:center;color:#626b82;font-size:13px">
                    Your designs are saved in your workspace.
                </p>
                <p style="text-align:center;color:#626b82;font-size:12px">
                    © {DateTime.UtcNow.Year} DesignMode Inc.
                </p>
            </td></tr>
            </table>
            </body>
            </html>
            """;

        var text = $"Your three DesignMode concepts are ready!\n\nView your designs: {resumeUrl}";

        return new FunnelRecoveryEmail(subject, html, text, resumeUrl);
    }
}
