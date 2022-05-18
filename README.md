# appsvc_fnc_dev_userssynch

This user synch application is part of a bigger process. This app map user from a specific group in a specific tenant and create a json file of this group of user in a storage account.

To set this app up you need:
* A keyvault where the secret key of each app registration of each tenant is.
* A storage account where: You will store all json file  and create reference table where you store the reference data of each tenant
                      
AppSetting you need:
* containerName: name of the container where the json file is store
* tableName: name of the table where the reference info is store

![image](https://user-images.githubusercontent.com/15112568/167149317-5c76afa0-a5e4-4aaa-a010-9a760d36aea6.png)
