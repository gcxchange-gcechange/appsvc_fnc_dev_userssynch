using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Graph;
using System.Collections.Generic;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.Extensions.Configuration;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.Auth;
using Azure.Storage.Blobs;
using Azure.Identity;
using System.Text;
using Microsoft.Graph.ExternalConnectors;
using System.Reflection.Metadata;


// Hello! As you do some work on the sync, can you test something for me. Can you try running the function on a group that have multiple nested group.
// What I mean by that is having a group with multiple level of group. (A group, in a group, in a group, etc.)



namespace appsvc_fnc_dev_userssynch
{
    public static class synch
    {
        struct EmailNotificationList
        {
            public string[] EmailNotificationListForUsersThatCannotBeInvited { get; set; }
        }

        struct UserAccount
        {
            public string DisplaName;
            public string EmailAddress;
            public string ReasonForRejection;
        }

        struct SMTPInfo
        {
            public string SMTPPort { get; set; }
            public string SMTPServerFQDN { get; set; }

        }

        [FunctionName("synch")]
            public static async Task Run([TimerTrigger(" 0 */60 * * * *")] TimerInfo myTimer, ExecutionContext context, ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");


            try {

                var config = new ConfigurationBuilder()
                    .SetBasePath(context.FunctionAppDirectory)
                    .AddJsonFile("local.settings.json", true, true)
                    .AddEnvironmentVariables().Build();

                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(config["AzureWebJobsStorage"]);
                // CloudStorageAccount storageAccountTBS = CloudStorageAccount.Parse(config["AzureWebJobsStorageTBS"]);

                string containerName = config["containerName"];
                string accountName = config["accountName"];
                string containerNameRef = config["containerNameRef"];
                string tableName = config["tableName"];
                string fileNameDomain = config["fileNameDomain"];

                CloudTableClient tableClient = storageAccount.CreateCloudTableClient();
                CloudTable table = tableClient.GetTableReference(tableName);

                TableContinuationToken token = null;
                do
                {
                    //get domain config file
                    // Connect to the blob storage
                    CloudBlobClient serviceClient = storageAccount.CreateCloudBlobClient();
                    // Connect to the blob container
                    CloudBlobContainer containerRef = serviceClient.GetContainerReference($"{containerNameRef}");
                    // Connect to the blob file
                    CloudBlockBlob blobRef = containerRef.GetBlockBlobReference($"{fileNameDomain}");
                    // Get the blob file as text
                    string contents = blobRef.DownloadTextAsync().Result;
                    // var domainsList = JsonConvert.SerializeObject(contents);
                    var domainsList = JsonConvert.DeserializeObject<List<UserDomainList>>(contents);

                    var q = new TableQuery<Table_Ref>();
                    var queryResult = await table.ExecuteQuerySegmentedAsync(q, token);
                    foreach (var item in queryResult.Results)
                    {
                        // string cliendID = item.client_id;
                        string rg_code = item.rg_code;
                        string tenantid = item.tenant_id;
                        string group_alias = item.group_alias;
                        string allgroupid = item.group_id;
                        string allgroupname = item.group_name;

                        //// TEMP - delete before check-in !!
                        //if (group_alias == "FCAC")
                        //{
                        //    continue;
                        //}

                        //CreateFile Title
                        string FileTitle = $"{group_alias}-b2b-sync-group-memberships.json";
                        string FileTitleStatus = $"{group_alias}-group-sync-status.txt.";

                        //string blobContainerName = containerName;
                        //BlobSas blobsas = new BlobSas();
                        //var storageAccountSas = blobsas.blobAuth(log);

                        string departcontainerName = group_alias.ToLower() + "-aad-to-gcx-b2b-sync-data";

                        // Construct the blob container endpoint from the arguments.
                        string containerEndpoint = string.Format("https://{0}.blob.core.windows.net/{1}",
                                                                    accountName,
                                                                    departcontainerName);

                        // Get a credential and create a service client object for the blob container.
                        BlobContainerClient containerClient = new BlobContainerClient(new Uri(containerEndpoint),
                                                                                        new DefaultAzureCredential());


                        // CloudBlobClient blobClient = storageAccountTBS.CreateCloudBlobClient();
                        //  CloudBlobContainer blobContainer = blobClient.GetContainerReference(blobContainerName);
                        var blobClient = containerClient.GetBlobClient(FileTitle);

                        if (!blobClient.Exists())
                        {

                            Auth auth = new Auth();
                            var graphAPIAuth = auth.graphAuth(group_alias, tenantid, log);

                            string stringUserList = "";
                            //Get all group id
                            var array_groupid = allgroupid.Split(",");
                            var array_groupname = allgroupname.Split(",");
                            var positiongroupname = 0;
                            List<UserAccount> rejectedList = new List<UserAccount>();

                            foreach (var groupid in array_groupid)
                            {
                                List<User> users = new List<User>();
                                var groupMembers = await graphAPIAuth.Groups[groupid.ToString()].TransitiveMembers.Request().Select("userType,mail,accountEnabled,displayName").GetAsync();
                                users.AddRange(groupMembers.CurrentPage.OfType<User>());
                                // fetch next page
                                while (groupMembers.NextPageRequest != null)
                                {
                                    groupMembers = await groupMembers.NextPageRequest.GetAsync();
                                    users.AddRange(groupMembers.CurrentPage.OfType<User>());
                                }
                                log.LogInformation("STart on " + groupid);

                                //List of user
                                List<string> userList = new List<string>();
                                string reason = string.Empty;
                                UserAccount account;

                                foreach (var user in users)
                                {
                                    log.LogInformation("User: " + user.Mail);

                                    reason = string.Empty;

                                    //check if user is a guest
                                    if (user.UserType != "Guest" && user.Mail != null && user.AccountEnabled == true)
                                    {

                                        //get user domain
                                        string UserDomain = user.Mail.Split("@")[1];
                                        log.LogInformation(UserDomain);
                                        bool domainMatch = false;

                                        //check if domain part of the domain list
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
                                        log.LogError($"User is a guest, no email or disable {user.DisplayName}.");

                                        if (user.UserType == "Guest")
                                            reason = "User is a guest";
                                        else if (user.Mail == null)
                                            reason = "User has no email";
                                        else if (user.AccountEnabled != true)
                                            reason = "User account is disabled";
                                        else
                                            reason = "Other";
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
                            //Create content files
                            var stringInsideTheFile = $"{{\"B2BGroupSyncAlias\": \"{group_alias}\",\"groupAliasToUsersMapping\":{{ {resultUserList} }} }}";
                            var statustext = "Ready";

                            // containerClient.Properties.ContentType = "application/json";
                            // Upload text to a new block blob.
                            byte[] byteArray = Encoding.ASCII.GetBytes(stringInsideTheFile);

                            using (MemoryStream stream = new MemoryStream(byteArray))
                            {
                                await containerClient.UploadBlobAsync(FileTitle, stream);
                            }

                            //using (var ms = new MemoryStream())
                            //{
                            //    LoadStreamWithJson(ms, stringInsideTheFile);
                            //    await containerClient.UploadBlobAsync(ms);
                            //}
                            //Add status file
                            //CloudBlockBlob blobStatus = blobContainer.GetBlockBlobReference(FileTitleStatus);

                            //cloudBlob.Properties.ContentType = "application/json";

                            byte[] byteArrayStatus = Encoding.ASCII.GetBytes(statustext);

                            using (MemoryStream stream = new MemoryStream(byteArrayStatus))
                            {
                                await containerClient.UploadBlobAsync(FileTitleStatus, stream);
                            }

                            //using (var ms = new MemoryStream())
                            //{
                            //    LoadStreamWithJson(ms, statustext);
                            //    await blobStatus.UploadFromStreamAsync(ms);
                            //}
                            //await blobStatus.SetPropertiesAsync();

                            if (rejectedList.Count > 0)
                            {

                                string SMTPInfoFileName = "general-config.json";
                                BlobContainerClient blobContainerClientSMTP = new BlobContainerClient(new Uri(string.Format("https://{0}.blob.core.windows.net/{1}", accountName, "sync-bootstrap-config-v4")), new DefaultAzureCredential());
                                var bcSMTP = blobContainerClientSMTP.GetBlobClient(SMTPInfoFileName);

                                if (bcSMTP.Exists())
                                {
                                    using (var streamSMTP = await bcSMTP.OpenReadAsync())
                                    using (var srSMTP = new StreamReader(streamSMTP))
                                    using (var jrSMTP = new JsonTextReader(srSMTP))
                                    {
                                        var result = JsonSerializer.CreateDefault().Deserialize<SMTPInfo>(jrSMTP);
                                        var smtpPort = result.SMTPPort;
                                        var smtpServer = result.SMTPServerFQDN;

                                        // SendRejectedList(string.Join(",", result.EmailNotificationListForUsersThatCannotBeInvited), rejectedList, log);
                                    }
                                }
                                else
                                {
                                    log.LogError($"File {SMTPInfoFileName} not found");
                                }
                                // need to get email notification list here!
                                // The list of email has to come from the storage account based on the department.
                                // For example, TBS will be from: TBS-To-GCX-B2B-Sync.json, DFO from: DFO-To-GCX-B2B-Sync.json, etc.

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
                                        SendRejectedList(string.Join(",", result.EmailNotificationListForUsersThatCannotBeInvited), rejectedList, log);
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
                    token = queryResult.ContinuationToken;
                } while (token != null);


            }
            catch (Exception e) {

                log.LogError($"Message: {e.Message}");
                if (e.InnerException is not null) log.LogError($"InnerException: {e.InnerException.Message}");
                log.LogError($"StackTrace: {e.StackTrace}");

            }
        }

        private static void LoadStreamWithJson(Stream ms, object obj)
        {
            StreamWriter writer = new StreamWriter(ms);
            writer.Write(obj);
            writer.Flush();
            ms.Position = 0;
        }


        private static void SendRejectedList(string emailNotificationList, List<UserAccount> rejectedList, ILogger log)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("<p>The following user's were not included in the synch:</p>");

            sb.AppendLine($"<table border='1' width='100%'>");
            sb.AppendLine($"<tr><td style='font-weight: bold'>Name</td><td style='font-weight: bold'>Email Address</td><td style='font-weight: bold'>Reason for rejection</td></tr>");

            foreach (var account in rejectedList)
            {
                sb.AppendLine($"<tr><td>{account.DisplaName}</td><td>{account.EmailAddress}</td><td>{account.ReasonForRejection}</td></tr>");
            }

            sb.AppendLine($"</table>");

            Email.SendEmail(emailNotificationList, "GXChange User Synch Report", sb.ToString(), log);
        }
    }
}