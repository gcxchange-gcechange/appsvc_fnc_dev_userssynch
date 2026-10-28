using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Newtonsoft.Json;
using System.Text;

namespace appsvc_fnc_dev_userssynch
{
    public class Synch
    {
        private readonly ILogger<Synch> log;

        public Synch(ILogger<Synch> logger)
        {
            log = logger;
        }

        // Every hour, at minute 0.
        [Function("Synch")]
        public async Task Run([TimerTrigger("0 0 * * * *")] TimerInfo myTimer)
        {
            log.LogInformation("User synchronization timer trigger started.");

            try
            {
                var config = new ConfigurationBuilder().SetBasePath(Environment.CurrentDirectory).AddJsonFile("local.settings.json", optional: true, reloadOnChange: true).AddEnvironmentVariables().Build();

                string accountName = config["accountName"];             // stsyncostdps
                string containerNameRef = config["containerNameRef"];   // reference
                string tableName = config["tableName"];                 // UserSyncRef
                string fileNameDomain = config["fileNameDomain"];       // domain-config.json
                string logUserSyncIssues = config["logUserSyncIssues"]; // 1 = true; 0 = false
                string storageConnectionString = config["AzureWebJobsStorage"];

                // Replaces CloudStorageAccount / CloudBlobClient.
                var blobServiceClient = new BlobServiceClient(storageConnectionString);

                // Replaces CloudTableClient / CloudTable.
                var tableClient = new TableClient(storageConnectionString, tableName);

                // Existing managed identity credential used for destination containers.
                var credential = new DefaultAzureCredential();

                // Get the reference domain JSON once, rather than once per table page.
                BlobClient domainConfigBlob = blobServiceClient.GetBlobContainerClient(containerNameRef).GetBlobClient(fileNameDomain);

                string domainConfigJson = (await domainConfigBlob.DownloadContentAsync()).Value.Content.ToString();

                var domainsList = JsonConvert.DeserializeObject<List<UserDomainList>>(domainConfigJson) ?? new List<UserDomainList>();

                var authFailureList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // QueryAsync handles continuation tokens/pages internally.
                await foreach (TableEntity item in tableClient.QueryAsync<TableEntity>())
                {
                    string tenantId = item.GetString("tenant_id");
                    string groupAlias = item.GetString("group_alias");
                    string allGroupId = item.GetString("group_id");
                    string allGroupName = item.GetString("group_name");

                    if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(groupAlias) || string.IsNullOrWhiteSpace(allGroupId) || string.IsNullOrWhiteSpace(allGroupName))
                    {
                        log.LogWarning("Skipping table entity {PartitionKey}/{RowKey}: required values are missing.", item.PartitionKey, item.RowKey);
                        continue;
                    }

                    log.LogInformation("Starting synchronization for {GroupAlias}", groupAlias);

                    string fileTitle = $"{groupAlias}-b2b-sync-group-memberships.json";

                    // Preserves the original filename, including the final period.
                    // Remove the final "." only if it was accidental and downstream
                    // consumers expect a ".txt" filename.
                    string fileTitleStatus = $"{groupAlias}-group-sync-status.txt.";
                    string departmentContainerName = $"{groupAlias.ToLowerInvariant()}-aad-to-gcx-b2b-sync-data";

                    var destinationContainerClient = new BlobContainerClient(new Uri($"https://{accountName}.blob.core.windows.net/{departmentContainerName}"), credential);

                    BlobClient outputBlob = destinationContainerClient.GetBlobClient(fileTitle);

                    if ((await outputBlob.ExistsAsync()).Value)
                    {
                        log.LogInformation("Output file already exists: {FileTitle}", fileTitle);
                        continue;
                    }

                    GraphServiceClient graphApiAuth;

                    try
                    {
                        Auth auth = new Auth();
                        graphApiAuth = auth.graphAuth(groupAlias, tenantId, log);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Authentication failure for group alias {GroupAlias}", groupAlias);
                        authFailureList.Add(groupAlias);
                        continue;
                    }

                    string[] groupIds = allGroupId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    string[] groupNames = allGroupName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    if (groupIds.Length != groupNames.Length)
                    {
                        log.LogError("Group ID/name count mismatch for {GroupAlias}. IDs: {IdCount}, names: {NameCount}.", groupAlias, groupIds.Length, groupNames.Length);
                        continue;
                    }

                    var rejectedList = new List<UserAccount>();

                    var groupAliasToUsersMapping = new Dictionary<string, List<string>>(StringComparer.Ordinal);

                    for (int groupIndex = 0; groupIndex < groupIds.Length; groupIndex++)
                    {
                        string groupId = groupIds[groupIndex];
                        string groupName = groupNames[groupIndex];

                        var users = new List<User>();

                        try
                        {
                            var group = await graphApiAuth.Groups[groupId].GetAsync();

                            log.LogInformation("Retrieved group {GroupId} for alias {GroupAlias}", group?.Id, groupAlias);

                            var groupMembers =  await graphApiAuth.Groups[groupId].TransitiveMembers.GetAsync(requestConfiguration => {requestConfiguration.QueryParameters.Select = ["userType", "mail", "accountEnabled", "displayName", "id"];});

                            if (groupMembers?.Value is not null)
                            {
                                users.AddRange(groupMembers.Value.OfType<User>());
                            }

                            while (!string.IsNullOrEmpty(groupMembers?.OdataNextLink))
                            {
                                var nextPageRequestInformation = new RequestInformation
                                {
                                    HttpMethod = Method.GET,
                                    UrlTemplate = groupMembers.OdataNextLink
                                };

                                groupMembers = await graphApiAuth.RequestAdapter.SendAsync(nextPageRequestInformation, parseNode => new DirectoryObjectCollectionResponse());

                                if (groupMembers?.Value is not null)
                                {
                                    users.AddRange(groupMembers.Value.OfType<User>());
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "Failed to retrieve group {GroupId} for group alias {GroupAlias}.", groupId, groupAlias);
                            authFailureList.Add(groupAlias);
                            continue;
                        }

                        var validUsers = new List<string>();

                        foreach (User user in users)
                        {
                            string reason = string.Empty;

                            if (!string.Equals(user.UserType, "Guest", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(user.Mail) && user.AccountEnabled == true)
                            {
                                string userDomain = user.Mail.Split('@').Last();
                                bool domainMatch = false;

                                foreach (var domain in domainsList)
                                {
                                    if (domain.UserDomains.Contains(userDomain, StringComparer.OrdinalIgnoreCase))
                                    {
                                        validUsers.Add(user.Mail);
                                        domainMatch = true;
                                        break;
                                    }
                                }

                                if (!domainMatch)
                                {
                                    reason = $"User domain does not exist: {user.Mail}.";

                                    if (logUserSyncIssues == "1")
                                    {
                                        log.LogError("{Reason}", reason);
                                    }
                                }
                            }
                            else
                            {
                                if (string.Equals(user.UserType, "Guest", StringComparison.OrdinalIgnoreCase))
                                {
                                    reason = "User is a guest.";
                                }
                                else if (string.IsNullOrWhiteSpace(user.Mail))
                                {
                                    reason = "User has no email.";
                                }
                                else if (user.AccountEnabled != true)
                                {
                                    reason = "User account is disabled.";
                                }
                                else
                                {
                                    reason = "Other.";
                                }

                                if (logUserSyncIssues == "1")
                                {
                                    log.LogWarning("User rejected: {DisplayName} ({UserId}). Reason: {Reason}", user.DisplayName, user.Id, reason);
                                }
                            }

                            if (!string.IsNullOrEmpty(reason))
                            {
                                rejectedList.Add(new UserAccount
                                {
                                    DisplaName = user.DisplayName,
                                    EmailAddress = user.Mail,
                                    ReasonForRejection = reason
                                });
                            }
                        }

                        groupAliasToUsersMapping[groupName] = validUsers;
                    }

                    // Safer than manually composing JSON: correctly escapes names and emails.
                    string outputJson = JsonConvert.SerializeObject(new
                    {
                        B2BGroupSyncAlias = groupAlias,
                        groupAliasToUsersMapping
                    });

                    // Creates a new blob and fails if it was created between ExistsAsync and UploadAsync.
                    await outputBlob.UploadAsync(BinaryData.FromString(outputJson), overwrite: false);

                    BlobClient statusBlob = destinationContainerClient.GetBlobClient(fileTitleStatus);

                    await statusBlob.UploadAsync(BinaryData.FromString("Ready"), overwrite: false);

                    if (rejectedList.Count > 0)
                    {
                        string notificationFileName = $"{groupAlias.ToUpperInvariant()}-To-GCX-B2B-Sync.json";

                        var notificationContainerClient = new BlobContainerClient(new Uri($"https://{accountName}.blob.core.windows.net/b2b-sync-config"), credential);

                        BlobClient notificationBlob = notificationContainerClient.GetBlobClient(notificationFileName);

                        if ((await notificationBlob.ExistsAsync()).Value)
                        {
                            string notificationJson = (await notificationBlob.DownloadContentAsync()).Value.Content.ToString();

                            var notification = JsonConvert.DeserializeObject<EmailNotificationList>(notificationJson);

                            var recipients = notification.EmailNotificationListForUsersThatCannotBeInvited;

                            if (recipients is not null)
                            {
                                SendRejectedList(string.Join(",", recipients), rejectedList, groupAlias, log);
                            }
                        }
                        else
                        {
                            log.LogError("Notification configuration file not found: {FileName}", notificationFileName);
                        }
                    }
                }

                if (authFailureList.Count > 0)
                {
                    SendAuthFailureList(config["authFailureNotificationList"] ?? string.Empty, authFailureList.ToList(), log);
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "User synchronization failed.");
            }
        }

        // Keep SendRejectedList and SendAuthFailureList unchanged.


        private static void SendRejectedList(string emailNotificationList, List<UserAccount> rejectedList, string group_alias, ILogger log)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("<p>The following user's were not included in the synch:</p>");

            sb.AppendLine($"<table border=\"1\" width='100%'>");
            sb.AppendLine($"<tr><td style='font-weight: bold'>Name</td><td style='font-weight: bold'>Email Address</td><td style='font-weight: bold'>Reason for rejection</td></tr>");

            foreach (var account in rejectedList)
            {
                sb.AppendLine($"<tr><td>{account.DisplaName}</td><td>{account.EmailAddress}</td><td>{account.ReasonForRejection}</td></tr>");
            }

            sb.AppendLine($"</table>");

            Email.SendEmail(emailNotificationList, "[" + group_alias.ToUpper() + "] GXChange User Synch Report", sb.ToString(), log);
        }

        private static void SendAuthFailureList(string emailNotificationList, List<string> failureList, ILogger log)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("<p>The following groups reported an authentication failure during the synch:</p>");

            sb.AppendLine($"<ul>");

            foreach (var group_alias in failureList)
            {
                sb.AppendLine($"<li>{group_alias}</li>");
            }

            sb.AppendLine($"</ul>");

            Email.SendEmail(emailNotificationList, "GXChange User Synch Authentication Failure Report", sb.ToString(), log);
        }


    }
}