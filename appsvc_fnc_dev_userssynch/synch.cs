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

namespace appsvc_fnc_dev_userssynch
{
    public static class synch
    {
        [FunctionName("synch")]
            public static async Task Run([TimerTrigger(" 0 */60 * * * *")] TimerInfo myTimer, ExecutionContext context, ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

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
                    string allgroupid =  item.group_id;
                    string allgroupname = item.group_name;

                    //CreateFile Title
                    string FileTitle = $"{group_alias}-b2b-sync-group-memberships.json";
                    string FileTitleStatus = $"{group_alias}-group-sync-status.txt.";

                    //string blobContainerName = containerName;
                    //BlobSas blobsas = new BlobSas();
                    //var storageAccountSas = blobsas.blobAuth(log);

                    string departcontainerName= group_alias.ToLower()+"-aad-to-gcx-b2b-sync-data";

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
                        var graphAPIAuth = auth.graphAuth(rg_code, tenantid, log);

                        string stringUserList = "";
                        //Get all group id
                        var array_groupid = allgroupid.Split(",");
                        var array_groupname = allgroupname.Split(",");
                        var positiongroupname = 0;
                        foreach (var groupid in array_groupid)
                        {
                            List<User> users = new List<User>();
                            var groupMembers = await graphAPIAuth.Groups[groupid.ToString()].Members.Request().Select("userType,mail").GetAsync();
                            users.AddRange(groupMembers.CurrentPage.OfType<User>());
                            // fetch next page
                            while (groupMembers.NextPageRequest != null)
                            {
                                groupMembers = await groupMembers.NextPageRequest.GetAsync();
                                users.AddRange(groupMembers.CurrentPage.OfType<User>());
                            }

                            //List of user
                            List<string> userList = new List<string>();
                            foreach (var user in users)
                            {
                                //check if user is a guest
                                if (user.UserType != "Guest" && user.Mail != null)
                                {
                                    //get user domain
                                    string UserDomain = user.Mail.Split("@")[1];
                                    bool domainMatch = false;

                                    //check if domain part of the domain list
                                    foreach (var domain in domainsList)
                                    {
                                        if (domain.UserDomains.Contains(UserDomain))
                                        {
                                            userList.Add(user.Mail);
                                            domainMatch = true;
                                        }
                                    }
                                    if (!domainMatch)
                                    {
                                        log.LogError($"User domain do not exist {user.Mail}.");
                                    }
                                }
                                else
                                {
                                    log.LogError($"User is a guest or no email {user.DisplayName}.");
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
                    }
                    else
                    {
                        log.LogInformation("File already exist" + FileTitle);
                    }
                }
                token = queryResult.ContinuationToken;
            } while (token != null);
        }

        private static void LoadStreamWithJson(Stream ms, object obj)
        {
            StreamWriter writer = new StreamWriter(ms);
            writer.Write(obj);
            writer.Flush();
            ms.Position = 0;
        }
    }
}
