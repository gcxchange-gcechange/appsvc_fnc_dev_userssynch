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

                    //string[,] department = new string[,] { { "Cipher", "fc30239e-4e0f-4274-a0e0-2b10605f3c96", "111", "28d8f6f0-3824-448a-9247-b88592acc8b7", "Cipher_GCX_Access", "852e3cd8-d27f-4e1b-af69-88f1147dcbc8" }, { "TBS", "9e84d6ef-ec00-434f-9c7f-e5d2baf1f29b", "056", "28d8f6f0-3824-448a-9247-b88592acc8b7", "TBS_GCX_Access", "d59a06ea-ccac-49e7-bd5c-08e911b2473b" } };
            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", true, true)
                .AddEnvironmentVariables().Build();

            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(config["AzureWebJobsStorage"]);
            string containerName = config["containerName"];
            string tableName = config["tableName"];


            CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

            // Get user that never sign in
            CloudTable table = tableClient.GetTableReference(tableName);

            TableContinuationToken token = null;
            do
            {
                var q = new TableQuery<Table_Ref>();
                var queryResult = await table.ExecuteQuerySegmentedAsync(q, token);
                foreach (var item in queryResult.Results)
                {
                    string cliendID = item.client_id;
                    string rg_code = item.rg_code;
                    string tenantid = item.tenant_id;
                    string group_alias = item.group_alias;
                    string groupid = item.group_id;

                    log.LogInformation(cliendID);

                Auth auth = new Auth();
                var graphAPIAuth = auth.graphAuth(cliendID, rg_code, tenantid, log);

                List<User> users = new List<User>();
                var groupMembers = await graphAPIAuth.Groups[groupid].Members.Request().GetAsync();

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
                    userList.Add(user.Mail);
                }
                var list_of_users = string.Join(",", userList);

                //create mapping field value
                string grouplist = $"{{{groupid}:[{list_of_users}]}}";
               
                //create group object
                List<group_users> groupList = new List<group_users>();
                groupList.Add(new group_users()
                {
                    B2BGroupSyncAlias = group_alias,
                    groupAliasToUsersMapping = grouplist
                });

                //group object into json
                string json = JsonConvert.SerializeObject(groupList.ToArray());

                // CloudStorageAccount storageAccount = GetCloudStorageAccount;
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
