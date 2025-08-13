using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;

namespace appsvc_fnc_dev_userssynch
{
    internal class Auth
    {
        public GraphServiceClient graphAuth(string alias, string tenantId, ILogger log)
        {
            IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true, reloadOnChange: true).AddEnvironmentVariables().Build();

            var scopes = new string[] { "https://graph.microsoft.com/.default" };
            var keyVaultUrl = config["keyVaultUrl"];

            var keynameClient = alias.ToUpper()+"AppOnlyCredsClientId";
            var keynameSecret = alias.ToUpper()+"AppOnlyCredsClientSecret";

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

            KeyVaultSecret secret = client.GetSecret(keynameSecret);
            KeyVaultSecret clientId = client.GetSecret(keynameClient);

            var optionsToken = new TokenCredentialOptions {AuthorityHost = AzureAuthorityHosts.AzurePublicCloud};

            // https://docs.microsoft.com/dotnet/api/azure.identity.clientsecretcredential
            var clientSecretCredential = new ClientSecretCredential(tenantId, clientId.Value, secret.Value, optionsToken);

            var graphClient = new GraphServiceClient(clientSecretCredential, scopes);
            return graphClient;
        }
    }
}