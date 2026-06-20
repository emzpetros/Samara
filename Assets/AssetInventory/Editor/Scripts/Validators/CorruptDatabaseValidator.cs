using System.Collections.Generic;
using System.Threading.Tasks;
using Database;

namespace AssetInventory
{
    public sealed class CorruptDatabaseValidator : Validator
    {
        public CorruptDatabaseValidator()
        {
            Type = ValidatorType.DB;
            Name = "Corrupted Database";
            Description = "Runs an integrity check on the database to scan for any errors.";
            FixCaption = "Optimize Database";
        }

        public override async Task Validate()
        {
            CurrentState = State.Scanning;

            await Task.Yield();

            DBIssues = new List<AssetInfo>();
            if (DBAdapter.DB is SQLiteDatabaseConnection)
            {
                string result = DBAdapter.DB.ExecuteScalar<string>("PRAGMA integrity_check");
                if (result != "ok")
                {
                    AssetInfo info = new AssetInfo();
                    info.Path = result;
                    DBIssues.Add(info);
                }
            }
            else if (!DBAdapter.DB.TestConnection())
            {
                AssetInfo info = new AssetInfo();
                info.Path = "Database connection test failed.";
                DBIssues.Add(info);
            }
            CurrentState = State.Completed;
        }

        public override async Task Fix()
        {
            CurrentState = State.Fixing;

            await Task.Yield();
            DBAdapter.Optimize();
            DBIssues = new List<AssetInfo>();

            CurrentState = State.Completed;
        }
    }
}
