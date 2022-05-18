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

namespace appsvc_fnc_dev_userssynch
{
    public static class synch
    {
        [FunctionName("synch")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req, ExecutionContext context,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", true, true)
                .AddEnvironmentVariables().Build();

            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(config["AzureWebJobsStorage"]);
            string containerName = config["containerName"];
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
                    string cliendID = item.client_id;
                    string rg_code = item.rg_code;
                    string tenantid = item.tenant_id;
                    string group_alias = item.group_alias;
                    string allgroupid = item.group_id;

                    Auth auth = new Auth();
                    var graphAPIAuth = auth.graphAuth(cliendID, rg_code, tenantid, log);

                    Dictionary<string, dynamic> members = new Dictionary<string, dynamic>();

                    //Get all group id
                    var array_groupid = allgroupid.Split(",");
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
                            if(user.UserType != "Guest")
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
                                log.LogError($"User is a guest {user.Mail}.");
                            }
                        }
                        //Getting member list map to user group id
                        members.Add(groupid.ToString(), userList);
                    }
                    // Getting the mapping object
                    Dictionary<string, dynamic> mapping =
                        new Dictionary<string, dynamic>();

                        mapping.Add("B2BGroupSyncAlias", group_alias);
                        mapping.Add("groupAliasToUsersMapping", members);

                    //group object into json
                    string json = JsonConvert.SerializeObject(mapping.ToArray());

                    CreateContainerIfNotExists(log, containerName, storageAccount);
                    CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
                    CloudBlobContainer container = blobClient.GetContainerReference(containerName);

                    //CreateFil e Title
                    string FileTitle = $"{group_alias}User_Group.json";

                    CloudBlockBlob blob = container.GetBlockBlobReference(FileTitle);

                    blob.Properties.ContentType = "application/json";

                    using (var ms = new MemoryStream())
                    {
                        LoadStreamWithJson(ms, json);
                        await blob.UploadFromStreamAsync(ms);
                    }

                    await blob.SetPropertiesAsync();
                }
                token = queryResult.ContinuationToken;
            } while (token != null);

            return new OkObjectResult(new { message = "Finished" });
        }

        private static async void CreateContainerIfNotExists(ILogger logger, string ContainerName, CloudStorageAccount storageAccount)
        {
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
            string[] containers = new string[] { ContainerName };
            foreach (var item in containers)
            {
                CloudBlobContainer blobContainer = blobClient.GetContainerReference(item);
                await blobContainer.CreateIfNotExistsAsync();
            }
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
