using Microsoft.Ajax.Utilities;
using MyCouch;
using MyCouch.Requests;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
                    Error(directory, "Directory name is invalid.");
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
                    Console.WriteLine("Aborting.");
                }
                else
                {
                    Console.WriteLine("Uploading...");
                    var res = await store.Client.Documents.BulkAsync(bulk);
                    if (res.IsSuccess)
                    {
                        foreach (var row in res.Rows)
                        {
                            if (row.Succeeded) continue;
                            Error(row.Id + ": " + row.Reason);
                        }
                    }
                    else
                    {
                        Error(res.Reason);
                    }
                    Console.WriteLine("Done!");
                }
            }
            catch (AggregateException e)
            {
                foreach (var ie in e.InnerExceptions) Error(ie);
            }
            catch (Exception e)
            {
                Error(e);
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

            var docs = new DocumentCollection(BuildDocuments(directory));
            if (docs.Count > 0)
            {
                await store.QueryAsync<AllDocsValue>(new Query("_all_docs").Configure(c => c.Keys(docs.Keys.ToArray())), r =>
                {
                    if (r.Id == null) return;
                    docs[r.Id]["_rev"] = r.Value.Rev;
                });

                lock (bulk) bulk.Include(docs.Select(d => d.ToString(Formatting.None)).ToArray());
            }
        }

        /// <summary>
        /// Build documents from a given directory.
        /// </summary>
        static IEnumerable<JObject> BuildDocuments(DirectoryInfo directory)
        {
            // Go through subdirectories
            foreach (var dir in directory.EnumerateDirectories())
            {
                if (dir.Name == "_design") continue;
                if (dir.Name == "_attachments") continue;
                if (Path.GetExtension(dir.Name) == "._attachments") continue;

                foreach (var doc in BuildDocuments(dir)) yield return doc;
            }

            // Go through files
            foreach (var file in directory.EnumerateFiles("*.json"))
            {
                foreach (var doc in BuildDocuments(file)) yield return doc;
            }
        }

        /// <summary>
        /// Build documents from a given file and attachments.
        /// </summary>
        static IEnumerable<JObject> BuildDocuments(FileInfo file)
        {
            var doc = ParseJson(file);

            if (doc is JObject)
            {
                var id = (string)doc["_id"];
                if (id != null)
                {
                    var attach = new DirectoryInfo(Path.ChangeExtension(file.FullName, "._attachments"));
                    if (attach.Exists) doc["_attachments"] = BuildAttachments(attach);
                    yield return doc as JObject;
                    yield break;
                }
                Error(file, doc, "Document must have an _id.");
                yield break;
            }

            if (doc is JArray)
            {
                foreach (var d in doc)
                {
                    if (d is JObject)
                    {
                        var id = (string)d["_id"];
                        if (id != null)
                        {
                            yield return d as JObject;
                            continue;
                        }
                        Error(file, d, "Document must have an _id.");
                        continue;
                    }
                    Error(file, d, "Document must be an object literal.");
                }
                yield break;
            }

            Error(file, doc, "Document must be an object literal.");
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
                    if (dir.Name == "_attachments")
                    {
                        if (attachments) result[dir.Name] = BuildAttachments(dir);
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
                Error(directory, e);
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
                Error(directory, e);
            }
            return result;
        }

        /// <summary>
        /// Parses JavaScript for syntax errors, returns source code.
        /// </summary>
        static string ParseJavaScript(FileInfo file)
        {
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

                var settings = new CodeSettings();
                settings.MinifyCode = false;
                settings.IgnoreErrorList = "JS1010";
                settings.SetKnownGlobalIdentifiers(knowGlobals);

                var minifier = new Minifier { FileName = GetRelativePath(file), WarningLevel = 4 };
                code = minifier.MinifyJavaScript(code, settings);
                minifier.ErrorList.ForEach(e => Error(e));
                if (!HasError) return code;
            }
            catch (Exception e)
            {
                Error(file, e);
            }
            return string.Empty;
        }

        /// <summary>
        /// Parses JSON for syntax errors, returns JSON.
        /// </summary>
        static JToken ParseJson(FileInfo file)
        {
            try
            {
                var json = File.ReadAllText(file.FullName);
                return JToken.Parse(json);
            }
            catch (Exception e)
            {
                Error(file, e as dynamic);
            }
            return new JArray();
        }

        /// <summary>
        /// Parse text, returns string.
        /// </summary>
        static string ParseText(FileInfo file)
        {
            try
            {
                var text = File.ReadAllText(file.FullName);

                if (validText.IsMatch(text))
                {
                    return text.Replace(Environment.NewLine, "\n");
                }

                Error(file, "Binary file found.");
            }
            catch (Exception e)
            {
                Error(file, e);
            }
            return string.Empty;
        }

        #region Error handling

        static bool HasError { get { return Environment.ExitCode != 0; } }

        static void Error(FileSystemInfo info, IJsonLineInfo line, string message)
        {
            var origin = GetOrigin(info, line.LineNumber, line.LinePosition);
            Error(origin, message);
        }

        static void Error(FileSystemInfo info, JsonReaderException exception)
        {
            var origin = GetOrigin(info, exception.LineNumber, exception.LinePosition);
            var message = GetMessage(exception);
            Error(origin, message);
        }

        static void Error(FileSystemInfo info, Exception exception)
        {
            var origin = GetOrigin(info);
            var message = GetMessage(exception);
            Error(origin, message);
        }

        static void Error(FileSystemInfo info, string message)
        {
            var origin = GetOrigin(info);
            Error(origin, message);
        }

        static void Error(Exception exception)
        {
            var message = GetMessage(exception);
            Error(message);
        }

        static void Error(ContextError error)
        {
            if (error.IsError)
            {
                Console.Error.WriteLine(error);
                Environment.ExitCode = 1;
            }
            else
            {
                Console.WriteLine(error);
            }
        }

        static void Error(string origin, string message)
        {
            Console.Error.WriteLine(origin + ": error: " + message);
            Environment.ExitCode = 1;
        }

        static void Error(string message)
        {
            Console.Error.WriteLine("Fatal error: " + message);
            Environment.ExitCode = 1;
        }

        #endregion

        #region Helper classes

        class AllDocsValue
        {
            public string Rev { get; set; }
        }

        class DocumentCollection : KeyedCollection<string, JObject>
        {
            internal DocumentCollection(IEnumerable<JObject> collection)
            {
                foreach (var item in collection) Add(item);
            }

            internal IEnumerable<string> Keys
            {
                get { return Dictionary.Keys; }
            }

            protected override string GetKeyForItem(JObject item)
            {
                return (string)item["_id"];
            }
        }

        #endregion

        #region Helper methods

        static string GetRelativePath(FileSystemInfo info)
        {
            var baseUri = new Uri(directory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(new Uri(info.FullName)).ToString());
        }

        static string GetOrigin(FileSystemInfo info, int line = 0, int position = 0)
        {
            var origin = GetRelativePath(info);

            if (line > 0)
            {
                if (position > 0)
                {
                    origin = string.Format("{0}({1},{2})", origin, line, position);
                }
                else
                {
                    origin = string.Format("{0}({1})", origin, line);
                }
            }

            return origin;
        }

        static string GetMessage(Exception exception)
        {
            var message = exception.Message.Trim();
            var pos = message.IndexOfAny(Environment.NewLine.ToCharArray());
            if (pos > 0) return message.Remove(pos);
            return message;
        }

        #endregion
    }
}
