using System;
using Database;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class DatabaseConfigurationUI : BasicEditorUI
    {
        private Vector2 _scrollPos;
        private string _selectedDatabaseType = DatabaseFactory.SQLITE;
        private bool _isTesting;

        // MySQL settings
        private string _mysqlHost = "localhost";
        private int _mysqlPort = 3306;
        private string _mysqlDatabase = "";
        private string _mysqlUser = "";
        private string _mysqlPassword = "";
        private bool _mysqlUseSSL;
        private int _mysqlConnectionTimeout = 30;
        private bool _showPassword;
        private bool _hasChanges;

        // Foldout states for database panels
        private bool _sqlitePanelExpanded;
        private bool _mysqlPanelExpanded;

        public static DatabaseConfigurationUI ShowWindow()
        {
            DatabaseConfigurationUI window = GetWindow<DatabaseConfigurationUI>("Database Configuration");
            window.minSize = new Vector2(900, 350);
            window.Show();

            return window;
        }

        private void OnEnable()
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            _selectedDatabaseType = AI.Config.databaseType ?? DatabaseFactory.SQLITE;

            _mysqlHost = AI.Config.mysqlHost ?? "localhost";
            _mysqlPort = AI.Config.mysqlPort > 0 ? AI.Config.mysqlPort : 3306;
            _mysqlDatabase = AI.Config.mysqlDatabase ?? "";
            _mysqlUser = AI.Config.mysqlUser ?? "";
            _mysqlPassword = "";
            if (!string.IsNullOrEmpty(AI.Config.mysqlEncryptedPassword))
            {
                _mysqlPassword = EncryptionUtil.Decrypt(AI.Config.mysqlEncryptedPassword) ?? "";
            }
            _mysqlUseSSL = AI.Config.mysqlUseSSL;
            _mysqlConnectionTimeout = AI.Config.mysqlConnectionTimeout > 0 ? AI.Config.mysqlConnectionTimeout : 30;

            _hasChanges = false;
        }

        public override void OnGUI()
        {
            // Current status
            DrawCurrentStatus();
            EditorGUILayout.Space();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            // Database type selection with pros/cons
            EditorGUILayout.LabelField("Select Database Type", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            GUILayout.BeginHorizontal();

            // SQLite Panel
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            DrawDatabasePanel(DatabaseFactory.SQLITE,
                new[]
                {
                    "No server setup required",
                    "Zero configuration",
                    "File-based (easy backup and portability)",
                    "Fast for single-user scenarios",
                    "Embedded, no external dependencies",
                    "Portable across systems"
                },
                new[]
                {
                    "Limited concurrent access",
                    "Limited network access (file share)",
                    "Smaller data amounts (1-2 Gb)",
                    "Limited concurrency"
                },
                "Local development, single-user scenarios, simple deployments",
                _selectedDatabaseType == DatabaseFactory.SQLITE,
                ref _sqlitePanelExpanded);
            GUILayout.EndVertical();

            EditorGUILayout.Space();

            // MySQL Panel
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            DrawDatabasePanel(DatabaseFactory.MYSQL,
                new[]
                {
                    "Multi-user support and concurrent access",
                    "Network accessible",
                    "Highly scalable",
                    "Better for large datasets",
                    "Advanced features and optimizations",
                    "Industry-standard for production"
                },
                new[]
                {
                    "Requires server setup and configuration",
                    "Network dependency",
                    "More complex setup",
                    "Licensing considerations for commercial use"
                },
                "Team environments, remote access, large-scale deployments",
                _selectedDatabaseType == DatabaseFactory.MYSQL,
                ref _mysqlPanelExpanded);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            // Configuration section based on selection
            EditorGUILayout.Space(15);
            if (_selectedDatabaseType == DatabaseFactory.SQLITE)
            {
                DrawSQLiteConfiguration();
            }
            else
            {
                DrawMySQLConfiguration();
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space();

            // Action buttons
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUI.BeginDisabledGroup(!_hasChanges || _isTesting || (_selectedDatabaseType == DatabaseFactory.MYSQL && !IsMySQLConfigValid()));
            if (GUILayout.Button("Save & Connect", CommonUIStyles.mainButton, GUILayout.Width(140), GUILayout.Height(30)))
            {
                SaveAndConnect();
            }
            EditorGUI.EndDisabledGroup();

            if (_selectedDatabaseType == DatabaseFactory.MYSQL)
            {
                EditorGUILayout.Space();
                EditorGUI.BeginDisabledGroup(_isTesting || !IsMySQLConfigValid());
                if (GUILayout.Button(_isTesting ? "Testing..." : "Test Connection", GUILayout.Width(140), GUILayout.Height(30)))
                {
                    TestMySQLConnection();
                }
                EditorGUI.EndDisabledGroup();
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            EditorGUILayout.Space();
        }

        private void DrawCurrentStatus()
        {
            GUILayout.BeginVertical("box");
            string currentType = AI.Config?.databaseType ?? DatabaseFactory.SQLITE;
            string status = DBAdapter.IsDBOpen() ? "Connected" : "Disconnected";
            Color statusColor = DBAdapter.IsDBOpen() ? Color.green : Color.red;

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Current Database:", EditorStyles.boldLabel, GUILayout.Width(120));
            EditorGUILayout.LabelField(currentType);
            GUILayout.FlexibleSpace();

            Color oldColor = GUI.color;
            GUI.color = statusColor;
            EditorGUILayout.LabelField(status, EditorStyles.boldLabel, GUILayout.Width(90));
            GUI.color = oldColor;

            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(DBAdapter.DBError))
            {
                EditorGUILayout.HelpBox($"Connection Error: {DBAdapter.DBError}", MessageType.Error);
            }

            GUILayout.EndVertical();
        }

        private void DrawDatabasePanel(string name, string[] pros, string[] cons, string bestFor, bool isSelected, ref bool showDetails)
        {
            Color oldBg = GUI.backgroundColor;
            Color oldContent = GUI.contentColor;

            if (isSelected)
            {
                GUI.backgroundColor = new Color(0.3f, 0.5f, 0.8f, 0.3f);
            }

            GUILayout.BeginVertical("box");
            GUI.backgroundColor = oldBg;

            // Header
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(name, EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
            if (isSelected)
            {
                EditorGUILayout.LabelField("✓", EditorStyles.boldLabel, GUILayout.Width(20));
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Best for (always visible)
            EditorGUILayout.LabelField(bestFor, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(5);

            // Foldout for details
            showDetails = EditorGUILayout.Foldout(showDetails, "Show Details", true);

            if (showDetails)
            {
                EditorGUILayout.Space(5);

                // Pros
                EditorGUILayout.LabelField("Advantages", EditorStyles.miniLabel);
                GUILayout.BeginVertical("box");
                foreach (string pro in pros)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(15);
                    EditorGUILayout.LabelField("• " + pro, EditorStyles.wordWrappedMiniLabel);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();

                EditorGUILayout.Space(5);

                // Cons
                GUI.contentColor = new Color(0.7f, 0.7f, 0.7f);
                EditorGUILayout.LabelField("Limitations", EditorStyles.miniLabel);
                GUILayout.BeginVertical("box");
                foreach (string con in cons)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(15);
                    EditorGUILayout.LabelField("• " + con, EditorStyles.wordWrappedMiniLabel);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
                GUI.contentColor = oldContent;
            }

            EditorGUILayout.Space();

            // Show selection status or button
            if (isSelected)
            {
                EditorGUI.BeginDisabledGroup(true);
                GUILayout.Button("Current Selection", GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT));
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                // Show select button for unselected database
                if (GUILayout.Button("Select", GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                {
                    _selectedDatabaseType = name;
                    _hasChanges = true;
                }
            }

            GUILayout.EndVertical();
        }

        private void DrawSQLiteConfiguration()
        {
            EditorGUILayout.LabelField("SQLite Configuration", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.HelpBox("SQLite databases are stored as files. Use the database location settings to change the folder.", MessageType.Info);
        }

        private void DrawMySQLConfiguration()
        {
            EditorGUILayout.LabelField("MySQL Configuration", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            GUILayout.BeginVertical("box");

            int labelWidth = 150;

            // Host
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Host", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            string newHost = EditorGUILayout.TextField(_mysqlHost);
            if (newHost != _mysqlHost)
            {
                _mysqlHost = newHost;
                _hasChanges = true;
            }
            GUILayout.EndHorizontal();

            // Port
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Port", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            int newPort = EditorGUILayout.IntField(_mysqlPort);
            if (newPort != _mysqlPort)
            {
                _mysqlPort = newPort > 0 ? newPort : 3306;
                _hasChanges = true;
            }
            GUILayout.EndHorizontal();

            // Database
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Database Name", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            string newDb = EditorGUILayout.TextField(_mysqlDatabase);
            if (newDb != _mysqlDatabase)
            {
                _mysqlDatabase = newDb;
                _hasChanges = true;
            }
            GUILayout.EndHorizontal();

            // Username
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Username", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            string newUser = EditorGUILayout.TextField(_mysqlUser);
            if (newUser != _mysqlUser)
            {
                _mysqlUser = newUser;
                _hasChanges = true;
            }
            GUILayout.EndHorizontal();

            // Password
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Password", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            string newPassword;
            if (_showPassword)
            {
                newPassword = EditorGUILayout.TextField(_mysqlPassword);
            }
            else
            {
                newPassword = EditorGUILayout.PasswordField(_mysqlPassword);
            }
            if (newPassword != _mysqlPassword)
            {
                _mysqlPassword = newPassword;
                _hasChanges = true;
            }
            _showPassword = GUILayout.Toggle(_showPassword, _showPassword ? "Hide" : "Show", GUI.skin.button, GUILayout.Width(50));
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_mysqlPassword))
            {
                EditorGUILayout.HelpBox("Password will be encrypted when saved.", MessageType.Info);
            }

            if (ShowAdvanced())
            {
                // SSL
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Use SSL", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                bool newSSL = EditorGUILayout.Toggle(_mysqlUseSSL);
                if (newSSL != _mysqlUseSSL)
                {
                    _mysqlUseSSL = newSSL;
                    _hasChanges = true;
                }
                GUILayout.EndHorizontal();

                // Connection Timeout
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Connection Timeout (s)", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                int newTimeout = EditorGUILayout.IntField(_mysqlConnectionTimeout);
                if (newTimeout != _mysqlConnectionTimeout)
                {
                    _mysqlConnectionTimeout = newTimeout > 0 ? newTimeout : 30;
                    _hasChanges = true;
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.EndVertical();
        }

        private bool IsMySQLConfigValid()
        {
            return !string.IsNullOrWhiteSpace(_mysqlHost) &&
                !string.IsNullOrWhiteSpace(_mysqlDatabase) &&
                !string.IsNullOrWhiteSpace(_mysqlUser);
        }

        private void TestMySQLConnection()
        {
            _isTesting = true;
            Repaint();

            // Use EditorApplication.delayCall to allow UI to update
            EditorApplication.delayCall += () =>
            {
                try
                {
                    // Create test settings for MySQL connection
                    DatabaseSettings testSettings = new DatabaseSettings
                    {
                        DatabaseType = DatabaseFactory.MYSQL,
                        MySqlHost = _mysqlHost,
                        MySqlPort = _mysqlPort,
                        MySqlDatabase = _mysqlDatabase,
                        MySqlUser = _mysqlUser,
                        MySqlPassword = _mysqlPassword,
                        MySqlUseSSL = _mysqlUseSSL,
                        MySqlConnectionTimeout = _mysqlConnectionTimeout
                    };

                    MySQLDatabaseConnection testConn = new MySQLDatabaseConnection(testSettings);

                    testConn.TestConnection();
                    testConn.Close();
                    testConn.Dispose();

                    EditorUtility.DisplayDialog("Connection Test", "Connection successful!", "OK");
                }
                catch (NotImplementedException e)
                {
                    // Log full stack trace to console for debugging
                    Debug.LogError($"MySQL Connection Test Failed:\n{e}");

                    EditorUtility.DisplayDialog("Connection Test Failed",
                        "Could not connect to MySQL database.\n\n" +
                        "The password is most likely incorrect. Please verify your credentials and try again.",
                        "OK");
                }
                catch (Exception e)
                {
                    // Log full stack trace to console for debugging
                    Debug.LogError($"MySQL Connection Test Failed:\n{e}");

                    EditorUtility.DisplayDialog("Connection Test Failed",
                        $"Could not connect to MySQL database: {e.Message}",
                        "OK");
                }
                finally
                {
                    _isTesting = false;
                    Repaint();
                }
            };
        }

        private void SaveAndConnect()
        {
            try
            {
                // Save configuration
                AI.Config.databaseType = _selectedDatabaseType;

                if (_selectedDatabaseType == DatabaseFactory.MYSQL)
                {
                    AI.Config.mysqlHost = _mysqlHost;
                    AI.Config.mysqlPort = _mysqlPort;
                    AI.Config.mysqlDatabase = _mysqlDatabase;
                    AI.Config.mysqlUser = _mysqlUser;
                    AI.Config.mysqlUseSSL = _mysqlUseSSL;
                    AI.Config.mysqlConnectionTimeout = _mysqlConnectionTimeout;

                    // Encrypt password
                    if (!string.IsNullOrEmpty(_mysqlPassword))
                    {
                        AI.Config.mysqlEncryptedPassword = EncryptionUtil.Encrypt(_mysqlPassword);
                        if (string.IsNullOrEmpty(AI.Config.mysqlEncryptedPassword))
                        {
                            EditorUtility.DisplayDialog("Error", "Failed to encrypt password.", "OK");
                            return;
                        }
                    }
                    else
                    {
                        AI.Config.mysqlEncryptedPassword = "";
                    }
                }

                AI.SaveConfig();

                // Close current connection and switch with full reinitialization
                DBAdapter.Close();
                AI.ClearAllCaches();
                AI.Init(false, true);

                // Notify any open UI windows to reload (via AI.OnDatabaseSwitched event)
                AI.TriggerDatabaseSwitched();

                // Check if connection was successful
                if (!string.IsNullOrEmpty(DBAdapter.DBError))
                {
                    EditorUtility.DisplayDialog("Connection Error",
                        $"Failed to connect to {_selectedDatabaseType} database:\n\n{DBAdapter.DBError}\n\nPlease check your settings and try again.",
                        "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Success",
                        $"Successfully switched to {_selectedDatabaseType} database.",
                        "OK");
                    Close();
                }
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Error",
                    $"Error saving database configuration:\n\n{e.Message}",
                    "OK");
            }
        }
    }
}