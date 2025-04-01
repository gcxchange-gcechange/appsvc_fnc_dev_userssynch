using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Graph;
using System.Collections.Generic;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.Extensions.Configuration;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Table;
using Azure.Storage.Blobs;
using Azure.Identity;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace appsvc_fnc_dev_userssynch
{
    public static class synch
    {
        // Run every hour
        [FunctionName("synch")]
        public static async Task Run([TimerTrigger(" 0 */60 * * * *")] TimerInfo myTimer, ExecutionContext context, ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            try {
                var config = new ConfigurationBuilder().SetBasePath(context.FunctionAppDirectory).AddJsonFile("local.settings.json", true, true).AddEnvironmentVariables().Build();

                string accountName = config["accountName"];           // stsyncostdps
                string containerNameRef = config["containerNameRef"]; // reference
                string tableName = config["tableName"];               // UserSyncRef
                string fileNameDomain = config["fileNameDomain"];     // domain-config.json

                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(config["AzureWebJobsStorage"]);  // AccountName=stsynchdevstd

                CloudTableClient tableClient = storageAccount.CreateCloudTableClient();
                CloudTable table = tableClient.GetTableReference(tableName);
                TableContinuationToken token = null;

                do
                {
                    // Get domain config file
                    
                    // Connect to the blob storage
                    CloudBlobClient serviceClient = storageAccount.CreateCloudBlobClient();
                    
                    // Connect to the blob container
                    CloudBlobContainer containerRef = serviceClient.GetContainerReference($"{containerNameRef}");   

                    // Connect to the blob file
                    CloudBlockBlob blobRef = containerRef.GetBlockBlobReference($"{fileNameDomain}");

                    // Get the blob file as text
                    string contents = blobRef.DownloadTextAsync().Result;

                    var domainsList = JsonConvert.DeserializeObject<List<UserDomainList>>(contents);

                    var q = new TableQuery<Table_Ref>();
                    var queryResult = await table.ExecuteQuerySegmentedAsync(q, token);

                    List<string> authFailureList = new();

                    foreach (var item in queryResult.Results)
                    {
                        // Note: client_id is not taken from the table data, it is taken from the keyvault
                        string rg_code = item.rg_code;
                        string tenantid = item.tenant_id;
                        string group_alias = item.group_alias;
                        string allgroupid = item.group_id;
                        string allgroupname = item.group_name;

                        log.LogInformation($"Start with {group_alias}");

                        // Create File Title
                        string FileTitle = $"{group_alias}-b2b-sync-group-memberships.json";
                        string FileTitleStatus = $"{group_alias}-group-sync-status.txt.";
                        string departcontainerName = $"{group_alias.ToLower()}-aad-to-gcx-b2b-sync-data";

                        // Construct the blob container endpoint from the arguments.
                        string containerEndpoint = string.Format("https://{0}.blob.core.windows.net/{1}", accountName, departcontainerName);

                        // Get a credential and create a service client object for the blob container.
                        BlobContainerClient containerClient = new BlobContainerClient(new Uri(containerEndpoint), new DefaultAzureCredential());

                        var blobClient = containerClient.GetBlobClient(FileTitle);

                        if (!blobClient.Exists())
                        {
                            GraphServiceClient graphAPIAuth;

                            try {
                                Auth auth = new Auth();
                                graphAPIAuth = auth.graphAuth(group_alias, tenantid, log);
                            }
                            catch {
                                log.LogError($"Authentication failure for group_alias: {group_alias}");
                                authFailureList.Add(group_alias);
                                continue;
                            }

                            string stringUserList = "";
                            //Get all group id
                            var array_groupid = allgroupid.Split(",");
                            var array_groupname = allgroupname.Split(",");
                            var positiongroupname = 0;
                            List<UserAccount> rejectedList = new List<UserAccount>();

                            foreach (var groupid in array_groupid)
                            {
                                List<User> users = new List<User>();

                                var group = await graphAPIAuth.Groups[groupid.ToString()].Request().GetAsync();
                                log.LogInformation($"groups: {group.Id}");

                                var groupMembers = await graphAPIAuth.Groups[groupid.ToString()].TransitiveMembers.Request().Select("userType,mail,accountEnabled,displayName,id").GetAsync();
                                users.AddRange(groupMembers.CurrentPage.OfType<User>());

                                // fetch next page
                                while (groupMembers.NextPageRequest != null)
                                {
                                    groupMembers = await groupMembers.NextPageRequest.GetAsync();
                                    users.AddRange(groupMembers.CurrentPage.OfType<User>());
                                }
                                log.LogInformation("Start on group " + groupid);

                                // List of user
                                List<string> userList = new List<string>();
                                string reason = string.Empty;
                                UserAccount account;

                                foreach (var user in users)
                                {
                                    reason = string.Empty;

                                    // check if user is a guest
                                    if (user.UserType != "Guest" && user.Mail != null && user.AccountEnabled == true)
                                    {

                                        // get user domain
                                        string UserDomain = user.Mail.Split("@")[1];
                                        bool domainMatch = false;

                                        // check if domain part of the domain list
                                        foreach (var domain in domainsList)
                                        {
                                            if (domain.UserDomains.Contains(UserDomain.ToLower()))
                                            {
                                                userList.Add(user.Mail);
                                                domainMatch = true;
                                            }
                                        }
                                        if (!domainMatch)
                                        {
                                            log.LogError($"User domain does not exist {user.Mail}.");
                                            reason = $"User domain does not exist {user.Mail}.";
                                        }
                                    }
                                    else
                                    {
                                        if (user.UserType == "Guest")
                                            reason = "User is a guest";
                                        else if (user.Mail == null)
                                            reason = "User has no email";
                                        else if (user.AccountEnabled != true)
                                            reason = "User account is disabled";
                                        else
                                            reason = "Other";

                                        log.LogWarning($"User is a guest, no email or disable {user.DisplayName} - {user.Id}. Reason: {reason}");
                                    }

                                    if (reason != string.Empty)
                                    {
                                        account = new UserAccount();
                                        account.DisplaName = user.DisplayName;
                                        account.EmailAddress = user.Mail;
                                        account.ReasonForRejection = reason;
                                        rejectedList.Add(account);
                                    }
                                }
                                var res = string.Join("\",\"", userList);
                                stringUserList += $"\"{array_groupname[positiongroupname]}\":[\"{res}\"],";
                                positiongroupname++;
                            }

                            string resultUserList = stringUserList.Remove(stringUserList.Length - 1);
                            // Create content files
                            var stringInsideTheFile = $"{{\"B2BGroupSyncAlias\": \"{group_alias}\",\"groupAliasToUsersMapping\":{{ {resultUserList} }} }}";
                            var statustext = "Ready";

                            // Upload text to a new block blob.
                            byte[] byteArray = Encoding.ASCII.GetBytes(stringInsideTheFile);

                            using (MemoryStream stream = new MemoryStream(byteArray))
                            {
                                await containerClient.UploadBlobAsync(FileTitle, stream);
                            }

                            byte[] byteArrayStatus = Encoding.ASCII.GetBytes(statustext);

                            using (MemoryStream stream = new MemoryStream(byteArrayStatus))
                            {
                                await containerClient.UploadBlobAsync(FileTitleStatus, stream);
                            }

                            if (rejectedList.Count > 0)
                            {
                                string notificationFileName = $"{group_alias.ToUpper()}-To-GCX-B2B-Sync.json";
                                BlobContainerClient blobContainerClient = new BlobContainerClient(new Uri(string.Format("https://{0}.blob.core.windows.net/{1}", accountName, "b2b-sync-config")), new DefaultAzureCredential());
                                var bc = blobContainerClient.GetBlobClient(notificationFileName);

                                if (bc.Exists())
                                {
                                    using (var stream = await bc.OpenReadAsync())
                                    using (var sr = new StreamReader(stream))
                                    using (var jr = new JsonTextReader(sr))
                                    {
                                        var result = JsonSerializer.CreateDefault().Deserialize<EmailNotificationList>(jr);
                                        log.LogInformation($"EmailNotificationList: {string.Join(",", result.EmailNotificationListForUsersThatCannotBeInvited)}");
                                        SendRejectedList(string.Join(",", result.EmailNotificationListForUsersThatCannotBeInvited), rejectedList, group_alias, log);
                                    }
                                }
                                else
                                {
                                    log.LogError($"File {notificationFileName} not found");
                                }
                            }
                        }
                        else
                        {
                            log.LogInformation("File already exist " + FileTitle);
                        }
                    }
                    
                    if (authFailureList.Count > 0)
                    {
                        SendAuthFailureList(config["authFailureNotificationList"], authFailureList, log);
                    }

                    token = queryResult.ContinuationToken;
                } while (token != null);
            }
            catch (Exception e) {

                log.LogError($"Message: {e.Message}");
                if (e.InnerException is not null) log.LogError($"InnerException: {e.InnerException.Message}");
                log.LogError($"StackTrace: {e.StackTrace}");

            }
        }

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

            Email.SendEmail(emailNotificationList, "["+ group_alias.ToUpper() +"] GXChange User Synch Report", sb.ToString(), log);
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