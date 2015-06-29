# CouchDB Assembler

Assembles CouchDB design documents from a directory structure similar to Couchapp's [filesystem
mapping](https://github.com/couchapp/couchapp/wiki/Complete-Filesystem-to-Design-Doc-Mapping-Example).


### Features

* Windows / Visual Studio friendly.
* Validates JavaScript and JSON before upload.
* Support for TypeScript (with Visual Studio).


### How to use

There are several ways to use this to develop CouchDB design documents.

The preferred way is using Visual Studio for development.


#### Visual Studio usage

Use this Visual Studio project as a basis for development. Configure database URL, username and password in `Properties\app.config` (make sure you're not commiting passwords to public repos).

Design documents are built from the `_design` directory. Other JSON documents can be placed besides the `_design` directory.

Only files copied to output are processed (don't forget to set "Copy if newer"). TypeScript files should not be copied: include the generated JavaScript file and copy that one instead.

Building the project assembles documents and uploads them to the database. Visual Studio does IntelliSense for TypeScript, JavaScript and JSON, and reports syntax errors and warnings.

#### Command-line usage

```
Usage: CouchDBAssembler [source-dir] [database-url]

  [source-dir]      The directory from which to assemble design documents.

  [database-url]    The url of the CouchDB database to update.

  -u, --username    Database username.

  -p, --password    Database password.

  -h, --help        Display this help screen.
```

Command line arguments take priority over `app.config`. If unspecified, `source-dir` is the current directory.
Either `source-dir` is named `_design`, or it should contain a `_design` directory and other JSON documens besides it.


### Notes

Take care with spurious newlines at the end of files. This is particularly important for [builtin reduce functions](http://docs.couchdb.org/en/latest/couchapp/ddocs.html#reducefun-builtin), and `_id` files.

Comments are stripped from JSON files before upload.

Other than that, if any syntax errors are found, no documents are uploaded.


#### Dependencies

* [Command Line Parser Library](https://www.nuget.org/packages/CommandLineParser)
* [Microsoft Ajax Minifier](https://www.nuget.org/packages/AjaxMin/)
* [Json.NET](https://www.nuget.org/packages/Newtonsoft.Json)
* [MyCouch](https://www.nuget.org/packages/MyCouch/)
