using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Mail;
using System.Net;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace appsvc_fnc_dev_userssynch
{
    internal class Email
    {
        public static void SendEmail(string emailNotificationList, string subject, string body, ILogger log)
        {
            IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true, reloadOnChange: true).AddEnvironmentVariables().Build();

            string hostName = config["hostName"];
            string port = config["port"];

            try
            {
                SecretClientOptions options = new SecretClientOptions()
                {
                    Retry =
                    {
                        Delay = TimeSpan.FromSeconds(2),
                        MaxDelay = TimeSpan.FromSeconds(16),
                        MaxRetries = 5,
                        Mode = Azure.Core.RetryMode.Exponential
                    }
                };

                var client = new SecretClient(new System.Uri(config["keyVaultUrl"]), new DefaultAzureCredential(), options);
                KeyVaultSecret secret_password = client.GetSecret(config["secretNamePassword"]);    // dgcx-dev-key-email-password
                KeyVaultSecret sender_Email = client.GetSecret(config["senderEmail"]);              // dgcx-dev-key-email-sender

                var senderPassword = secret_password.Value;
                var senderEmail = sender_Email.Value;

                var smtpClient = new SmtpClient(hostName)
                {
                    Port = int.Parse(port),
                    Credentials = new NetworkCredential(senderEmail, senderPassword),
                    EnableSsl = true,
                };

                MailMessage mailMessage = new MailMessage() { IsBodyHtml = true };

                mailMessage.From = new MailAddress("tbs.donotreply-nepasrepondre.sct@tbs-sct.gc.ca");

                foreach (var address in emailNotificationList.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries))
                {
                    mailMessage.To.Add(address);
                }

                mailMessage.Subject = subject;
                mailMessage.Body = body;
                
                smtpClient.Send(mailMessage);
            }
            catch (Exception e) {
                log.LogError($"Message: {e.Message}");
                if (e.InnerException is not null) log.LogError($"InnerException: {e.InnerException.Message}");
                log.LogError($"StackTrace: {e.StackTrace}");
            }
        }
    }
}