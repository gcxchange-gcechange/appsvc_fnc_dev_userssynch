using Microsoft.WindowsAzure.Storage.Table;

namespace appsvc_fnc_dev_userssynch
{
    public class users_smtp
    {
        public string Email { get; set; }
    }

    public class Table_Ref : TableEntity
    {
        public Table_Ref(string skey, string srow)
        {
            this.PartitionKey = skey;
            this.RowKey = srow;
        }

        public Table_Ref() { }
        public string rg_code { get; set; }
        public string client_id { get; set; }
        public string tenant_id { get; set; }
        public string group_alias { get; set; }
        public string group_id { get; set; }
        public string group_name { get; set; }

    }

    public class UserDomainList
    {
        public string Alias { get; set; }
        public string EnglishName { get; set; }
        public string FrenchName { get; set; }
        public string AADOrgDomain { get; set; }
        public string AppOnlyServicePrincipalClientIdKeyVaultSecretName { get; set; }
        public string AppOnlyServicePrincipalClientSecretKeyVaultSecretName { get; set; }
        public string AppOnlyServicePrincipalClientIdAutomationAccountCredentialName { get; set; }
        public string AppOnlyServicePrincipalClientSecretAutomationAccountCredentialName { get; set; }

        private string[] _UserDomains;
        public string[] UserDomains
        {
            get { return _UserDomains; }
            set { _UserDomains = value.Select(s => s.ToLowerInvariant()).ToArray(); }
        }
    }

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
}