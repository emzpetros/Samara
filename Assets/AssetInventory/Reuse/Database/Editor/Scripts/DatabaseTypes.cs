using System;

namespace Database
{
    /// <summary>
    /// Result of a CreateTable operation
    /// </summary>
    public enum CreateTableResult
    {
        /// <summary>
        /// A new table was created
        /// </summary>
        Created,

        /// <summary>
        /// An existing table was migrated (columns added)
        /// </summary>
        Migrated
    }

    /// <summary>
    /// Flags for table creation options
    /// </summary>
    [Flags]
    public enum CreateFlags
    {
        /// <summary>
        /// No special flags
        /// </summary>
        None = 0x000,

        /// <summary>
        /// Create a primary key index for a property named "Id"
        /// </summary>
        ImplicitPK = 0x001,

        /// <summary>
        /// Create indexes for properties that end in "Id"
        /// </summary>
        ImplicitIndex = 0x002,

        /// <summary>
        /// Create both implicit PK and implicit indexes
        /// </summary>
        AllImplicit = 0x003,

        /// <summary>
        /// Make the primary key auto-incrementing
        /// </summary>
        AutoIncPK = 0x004,

        /// <summary>
        /// Create virtual table using FTS3
        /// </summary>
        FullTextSearch3 = 0x100,

        /// <summary>
        /// Create virtual table using FTS4
        /// </summary>
        FullTextSearch4 = 0x200
    }

    /// <summary>
    /// Information about a table column, abstracted from database-specific implementations
    /// </summary>
    public struct ColumnInfo
    {
        /// <summary>
        /// Name of the column
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Whether the column allows null values (0 = allows null, non-zero = NOT NULL)
        /// </summary>
        public int NotNull { get; set; }

        /// <summary>
        /// Create a ColumnInfo from name and not-null flag
        /// </summary>
        public ColumnInfo(string name, int notNull)
        {
            Name = name;
            NotNull = notNull;
        }

        /// <summary>
        /// Whether this column is nullable
        /// </summary>
        public bool IsNullable => NotNull == 0;

        public override string ToString()
        {
            return $"{Name} (NotNull: {NotNull})";
        }
    }
}
