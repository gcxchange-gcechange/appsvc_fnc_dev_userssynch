using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace appsvc_fnc_dev_userssynch
{
    public class users_smtp
    {
        public string Email { get; set; }
    }

    public class group_users
    {
        public string B2BGroupSyncAlias { get; set; }
        public string groupAliasToUsersMapping { get; set; }

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
    }
}