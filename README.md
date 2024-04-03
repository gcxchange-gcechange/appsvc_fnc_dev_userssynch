# B2B User Synchronization

## Summary

This user synch application is part of a bigger process. This app map user from a specific group in a specific tenant and create a json file of this group of user in a storage account.

To set this app up you need:
* A keyvault where the secret key of each app registration of each tenant is.
* A storage account where: You will store all json file  and create reference table where you store the reference data of each tenant
                      
AppSetting you need:
* containerName: name of the container where the json file is store
* tableName: name of the table where the reference info is store

![image](https://user-images.githubusercontent.com/15112568/167149317-5c76afa0-a5e4-4aaa-a010-9a760d36aea6.png)

## Prerequisites

The following user accounts (as reflected in the app settings) are required:

| Account           | Membership requirements                               |
| ----------------- | ----------------------------------------------------- |
| delegatedUserName | n/a                                                   |
| emailUserName     | n/a                                                   |

Note that user account design can be modified to suit your environment

## Version 

![dotnet 6](https://img.shields.io/badge/net6.0-blue.svg)

## API permission

MSGraph

| API / Permissions name    | Type        | Admin consent | Justification                       |
| ------------------------- | ----------- | ------------- | ----------------------------------- |
| GroupMember.Read.All      | Application | Yes           | Read all group memberships          |
| User.Read.All             | Application | Yes           | Read all users' full profiles       | 


## App setting

| Name                    | Description                                                                    |
| ----------------------- | ------------------------------------------------------------------------------ |
| AzureWebJobsStorage     | Connection string for the storage acoount                                      |
| clientId                | The application (client) ID of the app registration                            |
| delegatedUserName       | User principal name for the service account that reads application and secrets |
| delegatedUserSecret     | Secret name for delegatedUserName                                              |
| emailUserId			  | Object Id for the email user account                                           |
| emailUserName           | Email address used to send notifications                                       |
| emailUserSecret         | Secret name for emailUserSecret                                                |
| keyVaultUrl             | Address for the key vault                                                      |
| recipientAddress        | Email address(es) that receive notifications                                   |
| secretName              | Secret name used to authorize the function app                                 |
| tenantId                | Id of the Azure tenant that hosts the function app                             |

## Version history

Version|Date|Comments
-------|----|--------
1.0    |TBD |Initial release

## Disclaimer

**THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.**
