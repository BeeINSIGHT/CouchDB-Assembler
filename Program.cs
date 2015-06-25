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
                directory = Settings.Default.GetSourceDirectory();
                uri = Settings.Default.GetDatabaseUri();

                if (!directory.Exists)
                {
                    Error("The directory name is invalid.");
                    Environment.Exit(1);
                }

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
                var bulk = new BulkRequest();

                Task.WaitAll(
                    UpdateDesignDocuments(store, bulk),
                    UpdateOtherDocuments(store, bulk));

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
            catch (AggregateException e)
            {
                foreach (var ie in e.InnerExceptions) Error(ie.Message);
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
        /// Find and update design documents.
        /// </summary>
        static async Task UpdateDesignDocuments(IMyCouchStore store, BulkRequest bulk)
        {
            // Get the design document root
            var root = directory;
            if (root.Name != "_design") root = root.CreateSubdirectory("_design");

            // Get existing design document revisions
            var rows = await store.QueryAsync<AllDocsValue>(new Query("_all_docs") { StartKey = "_desgin/", EndKey = "_design0", InclusiveEnd = false });
            var revs = rows.ToDictionary(r => r.Id, r => r.Value.Rev);

            lock (bulk)
            {
                // Create a design document from each subdirectory
                foreach (var dir in root.EnumerateDirectories())
                {
                    var doc = BuildDesignDocument(dir);

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
            }
        }

        /// <summary>
        /// Find and update other documents.
        /// </summary>
        static async Task UpdateOtherDocuments(IMyCouchStore store, BulkRequest bulk)
        {
            var root = directory;
            if (root.Name == "_design") return;
            
            var docs = new Dictionary<string, JObject>();

            foreach (var file in root.EnumerateFiles("*.json"))
            {
                var doc = BuildDocument(file);

                var id = (string)doc["_id"];
                if (id == null)
                {
                    id = Path.GetFileNameWithoutExtension(file.Name);
                    doc["_id"] = id;
                }

                docs.Add(id, doc);
            }

            await store.QueryAsync<AllDocsValue>(new Query("_all_docs").Configure(c => c.Keys(docs.Keys.ToArray())), r =>
            {
                if (r.Id != null) docs[r.Id]["_rev"] = r.Value.Rev;
            });

            lock (bulk)
            {
                bulk.Include(docs.Values.Select(d => d.ToString(Formatting.None)).ToArray());
            }
        }

        /// <summary>
        /// Build a document from a given file and attachments.
        /// </summary>
        static JObject BuildDocument(FileInfo file)
        {
            try
            {
                var doc = ParseJson(file) as JObject;
                if (doc != null)
                {
                    var attach = new DirectoryInfo(Path.ChangeExtension(file.FullName, "_attachments"));
                    if (attach.Exists)
                    {
                        doc["_attachments"] = BuildAttachments(attach);
                    }

                    return doc;
                }
                Error("{0}: Document must be an object.", GetRelativePath(file));
            }
            catch (Exception e)
            {
                Error("{0}: {1}", GetRelativePath(file), e.Message);
            }
            return new JObject();
        }

        /// <summary>
        /// Build a design document from a given directory.
        /// </summary>
        static JObject BuildDesignDocument(DirectoryInfo directory, bool attachments = true)
        {
            var result = new JObject();
            try
            {
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
                        result[dir.Name] = BuildDesignDocument(dir, false);
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
            }
            catch (Exception e)
            {
                Error("{0}: {1}", GetRelativePath(directory), e.Message);
            }
            return result;
        }

        /// <summary>
        /// Build attachments structure from a given directory.
        /// </summary>
        static JObject BuildAttachments(DirectoryInfo directory)
        {
            var result = new JObject();
            try
            {
                var baseUri = new Uri(directory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

                foreach (var file in directory.EnumerateFiles("*.*", SearchOption.AllDirectories))
                {
                    var attachment = new JObject();
                    attachment["data"] = File.ReadAllBytes(file.FullName);
                    attachment["content_type"] = MimeMapping.GetMimeMapping(file.Name);

                    var name = Uri.UnescapeDataString(baseUri.MakeRelativeUri(new Uri(file.FullName)).ToString());
                    result[name] = attachment;
                }
            }
            catch (Exception e)
            {
                Error("{0}: {1}", GetRelativePath(directory), e.Message);
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
                minifier.ErrorList.ForEach(CompilerError);

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

        static string GetRelativePath(FileSystemInfo info)
        {
            var baseUri = new Uri(directory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(new Uri(info.FullName)).ToString());
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

        static void CompilerError(object sender, ContextErrorEventArgs e)
        {
            CompilerError(e.Error);
        }

        static void CompilerError(ContextError error)
        {
            if (error.IsError)
            {
                Error("{0}", error);
            }
            else
            {
                Warning("{0}", error);
            }
        }

        class AllDocsValue
        {
            public string Rev { get; set; }
        }
    }
}
