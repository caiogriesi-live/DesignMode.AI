using System.Net;

namespace DesignMode.AI.Server.Services;

public static class RecoveryEmailLayout
{
    public static FunnelRecoveryEmail Render(string subject, string headline, string body,
        string cta, string resumeUrl, string? selectedImageUrl, string? unsubscribeUrl = null)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        var origin = new Uri(resumeUrl).GetLeftPart(UriPartial.Authority);
        var image = FunnelRecoverySelectedImage.PublicUrl(selectedImageUrl);
        var hero = image is null ? "" : $"""
            <tr><td style="padding:8px 0 24px">
            <!--[if mso]><a href="{E(resumeUrl)}"><img src="{E(image)}" alt="View your preview — your selected DesignMode concept" width="584" style="display:block;width:100%;max-width:584px;height:auto;border:0;border-radius:12px"></a><![endif]-->
            <!--[if !mso]><!-->
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="width:100%;max-width:584px"><tr>
            <td class="email-concept-background" align="center" valign="middle" style="background:#111321 url('{E(image)}') no-repeat center center;background-position:center center!important;background-repeat:no-repeat!important;background-size:cover;border-radius:12px;text-align:center">
            <a href="{E(resumeUrl)}" aria-label="View your preview" style="display:block;padding:128px 0;text-align:center;text-decoration:none;color:#ffffff;line-height:0">
            <span class="email-play-overlay" aria-hidden="true" style="display:inline-block;vertical-align:middle;box-sizing:border-box;width:72px;height:72px;line-height:0;text-align:center;border:2px solid #ffffff;border-radius:50%;background-color:#222222;background-color:rgba(17,19,33,0.25);color:#ffffff;font-family:Arial,Helvetica,sans-serif;font-size:0"><span class="email-play-triangle" style="display:inline-block;width:0;height:0;margin:17px 0 0 6px;border-top:17px solid transparent;border-bottom:17px solid transparent;border-left:28px solid #ffffff;font-size:0;line-height:0"></span></span>
            </a></td></tr></table>
            <!--<![endif]-->
            </td></tr>
            """;
        var unsubscribe = string.IsNullOrWhiteSpace(unsubscribeUrl) ? "" :
            $" | <a href=\"{E(unsubscribeUrl)}\" style=\"color:#626b82\">Unsubscribe</a>";
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{E(subject)}}</title>
            <style>@media only screen and (max-width:480px){.email-padding{padding:24px 18px!important}.headline{font-size:32px!important}.benefit{display:block!important;width:100%!important;padding:8px 0!important} }</style>
            </head><body style="margin:0;padding:0;background-color:#f3f3f6;color:#111321;font-family:Arial,Helvetica,sans-serif">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:#f3f3f6"><tr><td align="center" style="padding:24px 8px">
            <!--[if mso]><table role="presentation" width="664" align="center"><tr><td><![endif]-->
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:664px;background-color:#ffffff;border-radius:16px"><tr><td class="email-padding" style="padding:32px 40px">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0">
            <tr><td style="padding-bottom:24px;border-bottom:1px solid #dedfe8;font-size:32px;font-weight:bold;letter-spacing:-1.5px;color:#111321">Design<span style="color:#6936d8">Mode</span></td></tr>
            <tr><td align="center" style="padding:32px 0 12px;font-size:11px;letter-spacing:2px;color:#626b82">YOUR CREATIVE VISION, BROUGHT TO LIFE</td></tr>
            <tr><td align="center"><h1 class="headline" style="margin:0 0 16px;font-size:40px;line-height:1.15;letter-spacing:-1px;color:#111321">{{E(headline)}}</h1></td></tr>
            <tr><td align="center" style="padding-bottom:24px;font-size:17px;line-height:1.6;color:#626b82">{{E(body)}}</td></tr>
            {{hero}}
            <tr><td align="center" style="padding:0 0 24px">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:400px"><tr><td align="center" bgcolor="#592ce0" style="border-radius:40px;background-color:#592ce0;mso-padding-alt:18px 24px">
            <a href="{{E(resumeUrl)}}" style="display:block;padding:18px 24px;border-radius:40px;color:#ffffff;text-decoration:none;font-size:18px;font-weight:bold;line-height:24px">{{E(cta)}} &rarr;</a>
            </td></tr></table></td></tr>
            <tr><td align="center" style="padding:0 0 24px;color:#626b82;font-size:15px;line-height:1.6">Continue where you left off and take the next step toward your brand system.</td></tr>
            <tr><td style="padding:18px 0;border-top:1px solid #dedfe8;border-bottom:1px solid #dedfe8">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr>
            <td class="benefit" align="center" width="50%" style="padding:8px;color:#592ce0;font-size:14px;font-weight:bold">See your preview</td>
            <td class="benefit" align="center" width="50%" style="padding:8px;color:#592ce0;font-size:14px;font-weight:bold">Continue building your brand</td>
            </tr></table></td></tr>
            <tr><td align="center" style="padding:24px 16px;background-color:#f7f6fb;color:#111321;font-size:20px;font-weight:bold">DesignMode</td></tr>
            <tr><td align="center" style="padding-top:24px;color:#626b82;font-size:12px;line-height:1.8">
            <a href="{{E(origin + "/privacy.html")}}" style="color:#626b82">Privacy Policy</a>{{unsubscribe}}<br>
            &copy; {{DateTime.UtcNow.Year}} DesignMode Inc. All rights reserved.
            </td></tr></table></td></tr></table>
            <!--[if mso]></td></tr></table><![endif]-->
            </td></tr></table></body></html>
            """;
        var unsubscribeText = string.IsNullOrWhiteSpace(unsubscribeUrl) ? "" : $"\n\nUnsubscribe from recovery reminders: {unsubscribeUrl}";
        return new(subject, html, $"{headline}\n\n{body}\n\n{cta}: {resumeUrl}\n\nPrivacy Policy: {origin}/privacy.html{unsubscribeText}\n\n© {DateTime.UtcNow.Year} DesignMode Inc. All rights reserved.", resumeUrl);
    }
}
