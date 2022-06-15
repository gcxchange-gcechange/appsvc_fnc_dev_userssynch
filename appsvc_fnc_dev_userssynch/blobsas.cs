using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using System;
using System.Net.Http.Headers;

namespace appsvc_fnc_dev_userssynch
{
    class BlobSas
    {
        public CloudStorageAccount blobAuth( ILogger log)
        {

            IConfiguration config = new ConfigurationBuilder()

           .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
           .AddEnvironmentVariables()
           .Build();

            log.LogInformation("C# HTTP trigger function processed a request.");
            var scopes = new string[] { "https://graph.microsoft.com/.default" };
            var keyVaultUrl = config["keyVaultUrl"];
            var keyname = config["keyblob"];

            SecretClientOptions options = new SecretClientOptions()
            {
                Retry =
                {
                    Delay= TimeSpan.FromSeconds(2),
                    MaxDelay = TimeSpan.FromSeconds(16),
                    MaxRetries = 5,
                    Mode = RetryMode.Exponential
                 }
            };
            var client = new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential(), options);

            KeyVaultSecret secret = client.GetSecret(keyname);

            StorageCredentials storageCredentials = new StorageCredentials(secret.Value);

            string accountName = config["blobaccountname"];
            
            var storageAccountSas = new CloudStorageAccount(storageCredentials, accountName, null, true);
            log.LogInformation("Can you see me");
            return storageAccountSas;
        }

    }
}
