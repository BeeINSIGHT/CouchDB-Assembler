using Microsoft.Ajax.Utilities;
using MyCouch;
using MyCouch.Requests;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace CouchDBAssembler
{
    class Program
    {
        static readonly Regex validText = new Regex("^[\u0009\u000a\u000d\u0020-\uFFFD]*$");

        static readonly string[] knowGlobals = new[]
        {
            // CommonJS
            "require", "module", "exports",
            // All functions
            "log", "sum", "isArray", "toJSON", "JSON",
            // Map functions
            "emit",
            // Show functions
            "provides", "registerType",
            // List functions
            "getRow", "send", "start",
            // Cloudant search
            "index", "st_index"
        };

        static DirectoryInfo directory;
        static Uri uri;

        static void Main(string[] args)
        {
            if (CommandLine.Parser.Default.ParseArguments(args, Settings.Default))
            {
                GetDirectory();
                GetDatabaseUri();

                var cts = new CancellationTokenSource();

                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                RunAsync(cts.Token).Wait();
                if (Debugger.IsAttached && HasError) Console.ReadKey();
            }
            else
            {
                Environment.ExitCode = 1;
            }
        }

        static async Task RunAsync(CancellationToken token)
        {
            var store = new MyCouchStore(uri);
            try
            {
                // Get existing design document revisions
                var rows = await store.QueryAsync(new Query("_all_docs") { StartKey = "_desgin/", EndKey = "_design0", InclusiveEnd = false });
                var revs = rows.ToDictionary(r => r.Id, r => JsonConvert.DeserializeAnonymousType(r.Value, new { rev = string.Empty }).rev);

                // Create update bulk request
                var bulk = CreateBulkRequest(revs);

                if (HasError)
                {
                    Warning("Aborting.");
                }
                else
                {
                    Warning("Uploading...");
                    var res = await store.Client.Documents.BulkAsync(bulk);
                    if (res.IsSuccess)
                    {
                        foreach (var row in res.Rows)
                        {
                            if (row.Succeeded) continue;
                            Error("{0}: {1}", row.Id, row.Reason);
                        }
                    }
                    else
                    {
                        Error(res.Reason);
                    }
                    Warning("Done!");
                }
            }
            catch (Exception e)
            {
                Error(e.Message);
            }
            finally
            {
                store.Dispose();
            }
        }

        /// <summary>
        /// Creates a bulk request that updates the design documents.
        /// </summary>
        /// <param name="revs">A dictionary mapping design document ids to revisions.</param>
        /// <returns>The bulk request.</returns>
        /// <remarks>
        /// Create a design document from each subdirectory.
        /// Missing design documents are removed.
        /// </remarks>
        static BulkRequest CreateBulkRequest(Dictionary<string, string> revs)
        {
            var bulk = new BulkRequest();

            // Create a design document from each subdirectory
            foreach (var dir in directory.EnumerateDirectories())
            {
                var doc = BuildDocument(dir);

                var id = (string)doc["_id"];
                if (id == null)
                {
                    id = "_design/" + dir.Name;
                    doc["_id"] = id;
                }
                else if (!id.StartsWith("_design/", StringComparison.Ordinal))
                {
                    id = "_design/" + id;
                    doc["_id"] = id;
                }

                var rev = string.Empty;
                if (revs.TryGetValue(id, out rev))
                {
                    doc["_rev"] = rev;
                    revs.Remove(id);
                }

                bulk.Include(doc.ToString(Formatting.None));
            }

            // Missing design documents are removed
            foreach (var kvp in revs)
            {
                bulk.Delete(kvp.Key, kvp.Value);
            }

            return bulk;
        }

        /// <summary>
        /// Build a design document from a given directory.
        /// </summary>
        static JObject BuildDocument(DirectoryInfo directory, bool attachments = true)
        {
            var result = new JObject();

            // Go through subdirectories
            foreach (var dir in directory.EnumerateDirectories())
            {
                // Attachments subdirectory found
                if (attachments && dir.Name == "_attachments")
                {
                    result[dir.Name] = BuildAttachments(dir);
                }
                // Otherwise, recurse into subdirectory
                else
                {
                    result[dir.Name] = BuildDocument(dir, false);
                }
            }

            // Go through files
            foreach (var file in directory.EnumerateFiles("*.*"))
            {
                var name = Path.GetFileNameWithoutExtension(file.Name);
                var ext = Path.GetExtension(file.Name);

                switch (ext)
                {
                    // JavaScript files: syntax checked and loaded as string
                    case ".js":
                        result[name] = ParseJavaScript(file);
                        break;

                    // JSON files: syntax checked and loaded as JSON
                    case ".json":
                        result[name] = ParseJson(file);
                        break;

                    // Text files: load as string for templating
                    default:
                        result[name] = ParseText(file);
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Build attachments structure from a given directory.
        /// </summary>
        static JObject BuildAttachments(DirectoryInfo dir)
        {
            var baseUri = new Uri(dir.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var result = new JObject();

            foreach (var file in dir.EnumerateFiles("*.*", SearchOption.AllDirectories))
            {
                var attachment = new JObject();
                attachment["data"] = File.ReadAllBytes(file.FullName);
                attachment["content_type"] = MimeMapping.GetMimeMapping(file.Name);

                var name = Uri.UnescapeDataString(baseUri.MakeRelativeUri(new Uri(file.FullName)).ToString());
                result[name] = attachment;
            }

            return result;
        }

        /// <summary>
        /// Parses JavaScript for syntax errors, returns source code.
        /// </summary>
        static string ParseJavaScript(FileInfo file)
        {
            var path = GetRelativePath(file);
            try
            {
                var code = File.ReadAllText(file.FullName);

                switch (code)
                {
                    case "_sum":
                    case "_count":
                    case "_stats":
                        return code;
                }

                var parser = new JSParser();
                parser.Settings.MinifyCode = false;
                parser.Settings.PreprocessOnly = true;
                parser.Settings.SetKnownGlobalIdentifiers(knowGlobals);

                var block = parser.Parse(code);
                if (block.Count == 1 && block[0] is FunctionObject && (block[0] as FunctionObject).Binding == null)
                {
                    parser.Settings.SourceMode = JavaScriptSourceMode.Expression;
                }

                parser.CompilerError += CompilerError;
                parser.Parse(new DocumentContext(code) { FileContext = path });

                if (!HasError) return code.Replace(Environment.NewLine, "\n");
            }
            catch (Exception e)
            {
                Error("{0}: {1}", path, e.Message);
            }
            return string.Empty;
        }

        /// <summary>
        /// Parses JSON for syntax errors, returns JSON.
        /// </summary>
        static JToken ParseJson(FileInfo file)
        {
            var path = GetRelativePath(file);
            try
            {
                var json = File.ReadAllText(file.FullName);

                var settings = new CodeSettings();
                settings.MinifyCode = false;
                settings.Format = JavaScriptFormat.JSON;
                settings.SourceMode = JavaScriptSourceMode.Expression;
                
                var minifier = new Minifier { FileName = path };
                json = minifier.MinifyJavaScript(json, settings);
                minifier.ErrorList.ForEach(e => CompilerError(minifier, new ContextErrorEventArgs { Error = e }));

                if (!HasError) return JToken.Parse(json);
            }
            catch (Exception e)
            {
                Error("{0}: {1}", path, e.Message);
            }
            return JValue.CreateNull();
        }

        /// <summary>
        /// Parse text, returns string.
        /// </summary>
        static string ParseText(FileInfo file)
        {
            var path = GetRelativePath(file);
            try
            {
                var text = File.ReadAllText(file.FullName);
                
                if (validText.IsMatch(text))
                {
                    return text.Replace(Environment.NewLine, "\n");
                }

                Error("{0}: Binary file found.", path);
            }
            catch (Exception e)
            {
                Error("{0}: {1}", path, e.Message);
            }
            return string.Empty;
        }

        static void CompilerError(object sender, ContextErrorEventArgs e)
        {
            if (e.Error.IsError)
            {
                Error("{0}", e.Error);
            }
            else
            {
                Warning("{0}", e.Error);
            }
        }

        static void GetDirectory()
        {
            directory = new DirectoryInfo(Settings.Default.SourceDir);
            if (directory.Exists) return;

            Error("The directory name is invalid.");
            Environment.Exit(1);
        }

        static void GetDatabaseUri()
        {
            var database = Settings.Default.DatabaseUrl;
            var username = Settings.Default.Username;
            var password = Settings.Default.Password;

            var builder = new MyCouchUriBuilder(database);

            if (username != "" || password != "")
            {
                builder.SetBasicCredentials(username, password);
            }

            uri = builder.Build();
        }

        static string GetRelativePath(FileInfo file)
        {
            var baseUri = new Uri(directory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(new Uri(file.FullName)).ToString());
        }

        static bool HasError { get { return Environment.ExitCode != 0; } }

        static void Error(string message)
        {
            Console.Error.WriteLine(message);
            Environment.ExitCode = 1;
        }

        static void Error(string format, params object[] args)
        {
            Console.Error.WriteLine(format, args);
            Environment.ExitCode = 1;
        }

        static void Warning(string message)
        {
            Console.WriteLine(message);
        }

        static void Warning(string format, params object[] args)
        {
            Console.WriteLine(format, args);
        }
    }
}
