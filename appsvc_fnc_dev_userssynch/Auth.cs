using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Identity.Client;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Threading;

namespace appsvc_fnc_dev_userssynch
{
    class Auth
    {
        public GraphServiceClient graphAuth(string alias, string tenantid, ILogger log)
        {

            IConfiguration config = new ConfigurationBuilder()

           .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
           .AddEnvironmentVariables()
           .Build();

            log.LogInformation("C# HTTP trigger function processed a request.");
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
            KeyVaultSecret clientID = client.GetSecret(keynameClient);

            IConfidentialClientApplication confidentialClientApplication = ConfidentialClientApplicationBuilder
            .Create(clientID.Value)
            .WithTenantId(tenantid)
            .WithClientSecret(secret.Value)
            .Build();

            // Build the Microsoft Graph client. As the authentication provider, set an async lambda
            // which uses the MSAL client to obtain an app-only access token to Microsoft Graph,
            // and inserts this access token in the Authorization header of each API request. 
            GraphServiceClient graphServiceClient =
                new GraphServiceClient(new DelegateAuthenticationProvider(async (requestMessage) => {

                    // Retrieve an access token for Microsoft Graph (gets a fresh token if needed).
                    var authResult = await confidentialClientApplication
                        .AcquireTokenForClient(scopes)
                        .ExecuteAsync();

                    // Add the access token in the Authorization header of the API request.
                    requestMessage.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", authResult.AccessToken);
                })
                );
            return graphServiceClient;
        }

    }

    public class ROPCConfidentialTokenCredential : Azure.Core.TokenCredential
    {
        // Implementation of the Azure.Core.TokenCredential class
        string _username = "";
        string _password = "";
        string _tenantId = "";
        string _clientId = "";
        string _clientSecret = "";
        string _tokenEndpoint = "";
        ILogger _log;

        public ROPCConfidentialTokenCredential(ILogger log)
        {
            IConfiguration config = new ConfigurationBuilder()
           .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
           .AddEnvironmentVariables()
           .Build();

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
            KeyVaultSecret secret_client = client.GetSecret(config["secretName"]);
            var clientSecret = secret_client.Value;
            KeyVaultSecret secret_password = client.GetSecret(config["secretNamePassword"]);
            var password = secret_password.Value;

            _username = config["delegated_username"];
            _password = password;
            _tenantId = config["tenantId"];
            _clientId = config["clientId"];
            _clientSecret = clientSecret;
            _tokenEndpoint = "https://login.microsoftonline.com/" + _tenantId + "/oauth2/v2.0/token";
            _log = log;
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            HttpClient httpClient = new HttpClient();

            // Create the request body
            var Parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("client_secret", _clientSecret),
                new KeyValuePair<string, string>("scope", string.Join(" ", requestContext.Scopes)),
                new KeyValuePair<string, string>("username", _username),
                new KeyValuePair<string, string>("password", _password),
                new KeyValuePair<string, string>("grant_type", "password")
            };

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(Parameters)
            };
            var response = httpClient.SendAsync(request).Result.Content.ReadAsStringAsync().Result;
            dynamic responseJson = JsonConvert.DeserializeObject(response);
            var expirationDate = DateTimeOffset.UtcNow.AddMinutes(60.0);
            return new AccessToken(responseJson.access_token.ToString(), expirationDate);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            HttpClient httpClient = new HttpClient();

            // Create the request body
            var Parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("client_secret", _clientSecret),
                new KeyValuePair<string, string>("scope", string.Join(" ", requestContext.Scopes)),
                new KeyValuePair<string, string>("username", _username),
                new KeyValuePair<string, string>("password", _password),
                new KeyValuePair<string, string>("grant_type", "password")
            };

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint) { Content = new FormUrlEncodedContent(Parameters) };
            var response = httpClient.SendAsync(request).Result.Content.ReadAsStringAsync().Result;
            dynamic responseJson = JsonConvert.DeserializeObject(response);
            var expirationDate = DateTimeOffset.UtcNow.AddMinutes(60.0);
            return new ValueTask<AccessToken>(new AccessToken(responseJson.access_token.ToString(), expirationDate));
        }
    }
}