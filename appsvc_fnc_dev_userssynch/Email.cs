﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Net.Mail;
using System.Net;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace appsvc_fnc_dev_userssynch
{
    internal class Email
    {
        public static void SendEmail(string subject, string body, ILogger log)
        {
            IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true, reloadOnChange: true).AddEnvironmentVariables().Build();

            string emailNotificationList = config["EmailNotificationListForUsersThatCannotBeInvited"];
            string hostName = config["hostName"];
            string port = config["port"];
            string senderEmail = config["senderEmail"];

            try {
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
                KeyVaultSecret secret_password = client.GetSecret(config["secretNamePassword"]);
                var senderPassword = secret_password.Value;

                var smtpClient = new SmtpClient(hostName)
                {
                    Port = int.Parse(port),
                    Credentials = new NetworkCredential(senderEmail, senderPassword),
                    EnableSsl = true,
                };

                MailMessage mailMessage = new MailMessage();

                mailMessage.From = new MailAddress(senderEmail);

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
            }
        }

        public static async void SendEmailGraph(string subject, string BodyContent, ILogger log)
        {
            IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true, reloadOnChange: true).AddEnvironmentVariables().Build();

            string EmailSender = config["userId"];

            log.LogInformation($"EmailSender: {EmailSender}");

            var scopes = new[] { "user.read mail.send" };
            ROPCConfidentialTokenCredential auth = new ROPCConfidentialTokenCredential(log);
            GraphServiceClient graphClient = new GraphServiceClient(auth, scopes);

            var msg = new Message
            {
                Subject = subject,
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = BodyContent
                },
                ToRecipients = new List<Recipient>() { new Recipient { EmailAddress = new EmailAddress { Address = "oliver.postlethwaite@tbs-sct.gc.ca" } } }
            };

            try
            {
                await graphClient.Users[EmailSender].SendMail(msg).Request().PostAsync();
            }
            catch (Exception e)
            {
                log.LogError($"Message: {e.Message}");
                if (e.InnerException is not null) log.LogError($"InnerException: {e.InnerException.Message}");
            }
        }
    }
}