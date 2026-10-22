// Persistence/Services/MailService.cs
using Core.Interfaces;
using Application.Common.Email;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Persistence.Services
{
    public class MailService : IMailService
    {
        private const string DefaultSmtpServer = "smtp.gmail.com";
        private const int DefaultSmtpPort = 587;

        private readonly string _smtpServer;
        private readonly int _smtpPort;
        private readonly string _senderEmail;
        private readonly string _smtpUsername;
        private readonly string _senderPassword;
        private readonly string _displayName;
        private readonly string? _replyTo;

        public MailService(IConfiguration configuration)
        {
            _smtpServer = ResolveSetting(configuration["SmtpSettings:Server"], DefaultSmtpServer);
            _smtpPort = int.TryParse(ResolveSetting(configuration["SmtpSettings:Port"], DefaultSmtpPort.ToString()), out var configuredPort)
                ? configuredPort
                : DefaultSmtpPort;
            _senderEmail = ResolveSetting(configuration["SmtpSettings:SenderEmail"], string.Empty);
            _smtpUsername = ResolveSetting(configuration["SmtpSettings:Username"], _senderEmail);
            _senderPassword = ResolveSetting(configuration["SmtpSettings:Password"], string.Empty);
            _displayName = ResolveSetting(configuration["SmtpSettings:DisplayName"], "Roomora");
            _replyTo = ResolveSetting(configuration["SmtpSettings:ReplyTo"], string.Empty);
        }

        public async Task SendEmailAsync(string to, string subject, string body)
        {
            var senderEmail = RequireSetting(_senderEmail, "SmtpSettings:SenderEmail");
            var senderPassword = RequireSetting(_senderPassword, "SmtpSettings:Password");
            using var client = new SmtpClient(_smtpServer, _smtpPort)
            {
                Credentials = new NetworkCredential(RequireSetting(_smtpUsername, "SmtpSettings:Username"), senderPassword),
                EnableSsl = true
            };

            using var message = new MailMessage
            {
                From = new MailAddress(senderEmail, _displayName, Encoding.UTF8),
                Subject = subject,
                SubjectEncoding = Encoding.UTF8,
                BodyEncoding = Encoding.UTF8,
                HeadersEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrWhiteSpace(_replyTo))
                message.ReplyToList.Add(new MailAddress(_replyTo, "Roomora Destek", Encoding.UTF8));
            message.To.Add(to);

            var plainText = CreatePlainText(body);
            message.AlternateViews.Add(
                AlternateView.CreateAlternateViewFromString(plainText, Encoding.UTF8, MediaTypeNames.Text.Plain));

            var htmlView = AlternateView.CreateAlternateViewFromString(body, Encoding.UTF8, MediaTypeNames.Text.Html);
            var logoPath = Path.Combine(
                AppContext.BaseDirectory,
                "wwwroot",
                "brand",
                "roomora-email-logo.png");

            if (File.Exists(logoPath))
            {
                var logo = new LinkedResource(logoPath, MediaTypeNames.Image.Png)
                {
                    ContentId = RoomoraEmailTemplate.LogoContentId,
                    TransferEncoding = TransferEncoding.Base64
                };
                htmlView.LinkedResources.Add(logo);
            }

            message.AlternateViews.Add(htmlView);
            await client.SendMailAsync(message);
        }

        private static string CreatePlainText(string html)
        {
            var withBreaks = Regex.Replace(
                html,
                @"<(br|/p|/div|/h[1-6]|/tr)\b[^>]*>",
                Environment.NewLine,
                RegexOptions.IgnoreCase);
            var withoutTags = Regex.Replace(withBreaks, "<[^>]+>", " ");
            var decoded = WebUtility.HtmlDecode(withoutTags);
            var compactLines = decoded
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                .Select(line => Regex.Replace(line, @"\s+", " ").Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line));

            return string.Join(Environment.NewLine, compactLines);
        }

        private static string ResolveSetting(string? configuredValue, string fallbackValue)
        {
            if (string.IsNullOrWhiteSpace(configuredValue))
                return fallbackValue;

            var value = configuredValue.Trim();
            return value.StartsWith("CHANGE_ME_", System.StringComparison.OrdinalIgnoreCase)
                ? fallbackValue
                : value;
        }

        private static string RequireSetting(string? configuredValue, string settingName)
        {
            if (string.IsNullOrWhiteSpace(configuredValue) ||
                configuredValue.Trim().StartsWith("CHANGE_ME_", System.StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{settingName} yapılandırılmalıdır. Gizli değerleri kaynak kodda tutmayın; environment variable veya secret store kullanın.");
            }

            return configuredValue.Trim();
        }
    }
}
