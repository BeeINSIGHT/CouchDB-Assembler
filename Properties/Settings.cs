using CommandLine;
using CommandLine.Text;
using MyCouch;
using MyCouch.Net;
using System;
using System.Configuration;
using System.IO;

namespace CouchDBAssembler
{
    sealed class Settings : ApplicationSettingsBase
    {
        static Settings defaultInstance = ApplicationSettingsBase.Synchronized(new Settings()) as Settings;

        public static Settings Default
        {
            get { return defaultInstance; }
        }

        [ValueOption(0)]
        [UserScopedSetting]
        [DefaultSettingValue(".")]
        public string SourceDir
        {
            get { return (string)this["SourceDir"]; }
            set { this["SourceDir"] = value; }
        }

        [ValueOption(1)]
        [UserScopedSetting]
        [DefaultSettingValue("")]
        [SpecialSetting(SpecialSetting.WebServiceUrl)]
        public string ServerUrl
        {
            get { return (string)this["ServerUrl"]; }
            set { this["ServerUrl"] = value; }
        }

        [ValueOption(2)]
        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string Database
        {
            get { return (string)this["Database"]; }
            set { this["Database"] = value; }
        }

        [Option('u', "username", HelpText = "Database username.")]
        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string Username
        {
            get { return (string)this["Username"]; }
            set { this["Username"] = value; }
        }

        [Option('p', "password", HelpText = "Database password.")]
        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string Password
        {
            get { return (string)this["Password"]; }
            set { this["Password"] = value; }
        }

        [Option('m', "minify", HelpText = "Minify JavaScript")]
        [UserScopedSetting]
        [DefaultSettingValue("false")]
        public bool Minify
        {
            get { return (bool)this["Minify"]; }
            set { this["Minify"] = value; }
        }

        [HelpOption('h', "help")]
        public string GetUsage()
        {
            var help = new HelpText { AdditionalNewLineAfterOption = true, AddDashesToOption = true };
            help.AddPreOptionsLine("Usage: CouchDBAssembler [source-dir] [server-url] [database-name]");
            help.AddPreOptionsLine("");
            help.AddPreOptionsLine("  [source-dir]      The directory from which to assemble design documents.");
            help.AddPreOptionsLine("");
            help.AddPreOptionsLine("  [server-url]      The url of the CouchDB instance to update.");
            help.AddPreOptionsLine("");
            help.AddPreOptionsLine("  [database-name]   The database name to update.");
            help.AddOptions(this);
            return help;
        }

        public DirectoryInfo GetSourceDirectory()
        {
            return new DirectoryInfo(SourceDir);
        }

        public DbConnectionInfo GetDbConnectionInfo()
        {
            var server = Settings.Default.ServerUrl;
            var database = Settings.Default.Database;
            var username = Settings.Default.Username;
            var password = Settings.Default.Password;

            var info = new DbConnectionInfo(server, database);

            if (username != "" || password != "")
            {
                info.BasicAuth = new BasicAuthString(username, password);
            }

            return info;
        }
    }
}
