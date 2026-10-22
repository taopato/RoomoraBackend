using System.Net;

namespace Application.Common.Email;

public static class RoomoraEmailTemplate
{
    public const string LogoContentId = "roomora-logo";

    public static string VerificationCode(string code, string purpose)
    {
        var isPasswordReset = purpose == "reset";
        var title = isPasswordReset ? "Şifreni yenile" : "E-posta adresini doğrula";
        var eyebrow = isPasswordReset ? "Güvenli hesap erişimi" : "Roomora'ya hoş geldin";
        var message = isPasswordReset
            ? "Roomora şifreni yenilemek için aşağıdaki tek kullanımlık kodu kullan."
            : "Hesabını tamamlamak için aşağıdaki doğrulama kodunu Roomora uygulamasına gir.";

        return Build(
            title,
            eyebrow,
            $"""
            <p class="lead" style="margin:0 0 26px;color:#53677a;font-size:16px;line-height:1.7">{message}</p>
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin:0 0 22px;background:#eaf3fb;border:1px solid #c8dff1;border-radius:8px">
              <tr>
                <td align="center" style="padding:24px 18px">
                  <div style="color:#2f6fa8;font-size:11px;line-height:1.2;font-weight:700;text-transform:uppercase">Doğrulama kodun</div>
                  <div style="margin-top:10px;color:#142f47;font-size:38px;line-height:1.1;font-weight:800;letter-spacing:8px">{WebUtility.HtmlEncode(code)}</div>
                </td>
              </tr>
            </table>
            {InfoBox("10 dakika geçerli", "Güvenliğin için bu kodu kimseyle paylaşma. Bu işlemi sen başlatmadıysan e-postayı yok sayabilirsin.")}
            """);
    }

    public static string Invitation(string inviteLink, DateTime expiresAt)
    {
        var safeLink = WebUtility.HtmlEncode(inviteLink);
        return Build(
            "Eve davet edildin",
            "Paylaşılan giderlerin kolay yolu",
            $"""
            <p style="margin:0 0 10px;color:#203b53;font-size:17px;line-height:1.5;font-weight:700">Ev arkadaşların Roomora'da seni bekliyor.</p>
            <p class="lead" style="margin:0 0 28px;color:#53677a;font-size:16px;line-height:1.7">Daveti kabul ederek ortak giderleri, faturaları ve ev notlarını tek bir yerde birlikte yönetebilirsin.</p>
            {PrimaryButton(safeLink, "Daveti kabul et")}
            {InfoBox($"{expiresAt:dd.MM.yyyy} tarihine kadar geçerli", "Butona dokunduğunda Roomora uygulaması açılır ve davet bilgilerin güvenli şekilde aktarılır.")}
            <p style="margin:20px 0 0;color:#7b8998;font-size:12px;line-height:1.6;word-break:break-all">Buton açılmazsa bu bağlantıyı kullan:<br><a href="{safeLink}" style="color:#2f6fa8;text-decoration:underline">{safeLink}</a></p>
            """);
    }

    public static string AccountDeletion(string deletionUrl)
    {
        var safeLink = WebUtility.HtmlEncode(deletionUrl);
        return Build(
            "Hesap silme isteği",
            "Önemli güvenlik bildirimi",
            $"""
            <p class="lead" style="margin:0 0 26px;color:#53677a;font-size:16px;line-height:1.7">Roomora hesabını kalıcı olarak silmek için bir istek aldık. Bu işlem ev ve hesap verilerini geri alınamayacak şekilde kaldırır.</p>
            {DangerButton(safeLink, "Hesabımı kalıcı olarak sil")}
            {InfoBox("15 dakika geçerli", "Bu isteği sen yapmadıysan hiçbir işlem yapma. Hesabın güvende kalmaya devam eder.")}
            """);
    }

    private static string Build(string title, string eyebrow, string content)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeEyebrow = WebUtility.HtmlEncode(eyebrow);

        return $$"""
        <!doctype html>
        <html lang="tr">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <meta name="color-scheme" content="light">
          <meta name="supported-color-schemes" content="light">
          <title>{{safeTitle}}</title>
        </head>
        <body style="margin:0;padding:0;background:#eef4f8;font-family:Arial,'Helvetica Neue',Helvetica,sans-serif;-webkit-text-size-adjust:100%">
          <div style="display:none!important;max-height:0;max-width:0;overflow:hidden;opacity:0;color:transparent;mso-hide:all">{{safeTitle}} · Roomora</div>
          <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="width:100%;background:#eef4f8">
            <tr>
              <td class="email-shell" align="center" style="padding:38px 14px">
                <table class="email-card" role="presentation" width="100%" cellspacing="0" cellpadding="0" style="width:100%;max-width:600px;background:#ffffff;border:1px solid #d9e4ec;border-radius:8px;overflow:hidden;box-shadow:0 8px 24px rgba(20,47,71,0.07)">
                  <tr>
                    <td class="brand-header" align="center" style="padding:24px 30px;background:#eaf3fb;border-bottom:1px solid #d6e6f3">
                      <table role="presentation" cellspacing="0" cellpadding="0" style="margin:0 auto">
                        <tr>
                          <td width="84" valign="middle">
                            <img class="brand-logo" src="cid:{{LogoContentId}}" width="84" height="84" alt="Roomora logosu" style="display:block;width:84px;height:84px;border:0;outline:none;text-decoration:none;border-radius:16px">
                          </td>
                          <td valign="middle" style="padding-left:17px;text-align:left">
                            <div class="brand-name" style="color:#142f47;font-size:30px;line-height:1.1;font-weight:800">Roomora</div>
                            <div style="margin-top:7px;color:#56738c;font-size:13px;line-height:1.4">Paylaşılan giderlerin kolay yolu</div>
                          </td>
                        </tr>
                      </table>
                    </td>
                  </tr>
                  <tr>
                    <td class="email-body" style="padding:34px 38px 38px">
                      <div style="margin-bottom:10px;color:#2f6fa8;font-size:11px;line-height:1.3;font-weight:700;text-transform:uppercase">{{safeEyebrow}}</div>
                      <h1 class="email-title" style="margin:0 0 18px;color:#142f47;font-size:30px;line-height:1.24;font-weight:800">{{safeTitle}}</h1>
                      {{content}}
                    </td>
                  </tr>
                  <tr>
                    <td class="email-footer" style="padding:22px 38px;background:#f7f9fc;border-top:1px solid #e2e9ef">
                      <div style="color:#687b8d;font-size:12px;line-height:1.65">Bu e-posta Roomora işleminle ilgili olarak otomatik gönderildi.</div>
                      <div style="margin-top:7px;color:#8492a0;font-size:11px;line-height:1.6">Yardıma mı ihtiyacın var? <a href="mailto:destek@takosware.com" style="color:#2f6fa8;text-decoration:none;font-weight:700">destek@takosware.com</a></div>
                    </td>
                  </tr>
                </table>
                <div style="padding:18px 12px 0;color:#8795a4;font-size:11px;line-height:1.5">© {{DateTime.UtcNow.Year}} Roomora · Takosware</div>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    private static string PrimaryButton(string safeUrl, string label) =>
        $"""
        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin:0 0 24px">
          <tr>
            <td align="center">
              <a href="{safeUrl}" style="display:inline-block;min-width:220px;padding:15px 24px;background:#2f6fa8;color:#ffffff;text-decoration:none;border-radius:8px;font-size:15px;line-height:1.2;font-weight:700;text-align:center">{label}</a>
            </td>
          </tr>
        </table>
        """;

    private static string DangerButton(string safeUrl, string label) =>
        $"""
        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="margin:0 0 24px">
          <tr>
            <td align="center">
              <a href="{safeUrl}" style="display:inline-block;min-width:240px;padding:15px 24px;background:#b42318;color:#ffffff;text-decoration:none;border-radius:8px;font-size:15px;line-height:1.2;font-weight:700;text-align:center">{label}</a>
            </td>
          </tr>
        </table>
        """;

    private static string InfoBox(string title, string message) =>
        $"""
        <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f7f9fc;border:1px solid #e0e8ef;border-radius:8px">
          <tr>
            <td width="4" style="width:4px;background:#7ca6c7;border-radius:8px 0 0 8px;font-size:0;line-height:0">&nbsp;</td>
            <td style="padding:16px 18px">
              <div style="color:#344b61;font-size:13px;line-height:1.4;font-weight:700">{WebUtility.HtmlEncode(title)}</div>
              <div style="margin-top:5px;color:#687b8d;font-size:13px;line-height:1.6">{WebUtility.HtmlEncode(message)}</div>
            </td>
          </tr>
        </table>
        """;
}
