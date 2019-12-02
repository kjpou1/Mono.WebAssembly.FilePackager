/* Stuff considered while porting this file from Python:
 * - Var names are left equal, except been C-Sharpified: this way will help us keep changes from the original source
 * - Comments are stripped: to avoid their maintenance
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mono.Options;

namespace Mono.WebAssembly.FilePackager
{
    class Program
    {
        const string ExportName = "Module";
        
        static readonly string[] AudioSuffixes = new string[] { ".ogg", ".wav", ".mp3" };

        static Configuration config = new Configuration();
        static OptionSet options;
        static List<(string srcPath, string dstPath, string mode, bool explicitDstPath, int dataStart, int dataEnd)> newDataFiles = 
            new List<(string srcPath, string dstPath, string mode, bool explicitDstPath, int dataStart, int dataEnd)>();
        static List<string> newDirNames = new List<string>();
        static bool hasPreloaded = false;
        static bool lz4 = false;
        static bool force = true;

        static void Main(string[] args)
        {
            var shouldShowHelp = false;
            const string preloadfile = @"preload-file <filename>.  Specify a file to preload before running the compiled code
asynchronously.  The path is relative to the current directory at compile time. If a directory is passed here, its entire contents
will be embedded.

Preloaded files are stored in `<filename>.data`, where `<filename>.html` is the main file you are compiling to.
To run your code, you will need both the `<filename>.html` and the `<filename>.data`.";

            const string noHeapCopy = @"If specified, the preloaded filesystem is not copied inside the Emscripten HEAP, but kept in a separate typed array outside it.  
The default, if this is not specified, is to embed the VFS inside the HEAP, so that mmap()ing files in it is a no-op.
Passing this flag optimizes for fread() usage, omitting it optimizes for mmap() usage.";

            const string separateMetadata = @"Stores package metadata separately. Only applicable when preloading and js-output file is specified.";

            options = new OptionSet {
                { "t|target=", "target data filename." , t => config.Target = t },
                { "p|preload-file=", preloadfile , plf =>
                {
                    config.PreloadFile = plf;
                    hasPreloaded = true;
                }},
                { "j|js-output=", "Writes output in FILE, if not specified, standard output is used." , jso => config.JSOutput = jso },
                { "n|no-heap-copy", noHeapCopy , _ => config.HeapCopy = false },
                { "s|separate-metadata", separateMetadata , _ => config.SeparateMetaData = true },
                { "h|help", "show this message and exit", h => shouldShowHelp = h != null },
            };

            List<string> extra;
            try
            {
                extra = options.Parse(args);
            }
            catch (OptionException e)
            {
                // output some error message
                Console.Write("Mono.WebAssembly.FilePackger.exe: ");
                Console.WriteLine(e.Message);
                Console.WriteLine("Try `Mono.WebAssembly.FilePackger.exe --help' for more information.");
                return;
            }

            if (string.IsNullOrEmpty(config.Target))
            {
                shouldShowHelp = true;

            }

            if (shouldShowHelp)
            {
                Usage();
            }

            Console.WriteLine($"Target: {config.Target}");

            Run();
        }

        static void Run()
        {
            var mode = "leading";
            var preloadFile = new string(config.PreloadFile);
            var atPosition = preloadFile.Replace("@@", "__").IndexOf('@');
            var usesAtNotation = atPosition != -1;
            string srcPath, dstPath;
            var dataFiles = new List<(string srcPath, string dstPath, string mode, bool explicitDstPath, 
                int dataStart, int dataEnd)>();

            if (usesAtNotation)
            {
                srcPath = preloadFile.Substring(0, atPosition).Replace("@@", "@");
                dstPath = preloadFile.Substring(atPosition + 1).Replace("@@", "@");
            }
            else
            {
                srcPath = dstPath = preloadFile.Replace("@@", "@");

                if (File.Exists(srcPath) || Directory.Exists(srcPath))
                {
                    dataFiles.Add((srcPath, dstPath, mode, usesAtNotation, -1, -1));
                }
                else
                {
                    Console.WriteLine($"Warning: {preloadFile} does not exist, ignoring.");
                }
            }

            var ret = @$"
var Module = typeof {ExportName} !== 'undefined' ? {ExportName} : {{}};
";
            ret += @"
if (!Module.expectedDataFileDownloads)
{
  Module.expectedDataFileDownloads = 0;
  Module.finishedDataFileDownloads = 0;
}
Module.expectedDataFileDownloads++;
(function() {
 var loadPackage = function(metadata) {
";
            var code = @"
function assert(check, msg) {
  if (!check) throw msg + new Error().stack;
}
";
            foreach (var file in dataFiles)
            {
                if (!ShouldIgnore(file.srcPath))
                {
                    if (Directory.Exists(file.srcPath))
                    {
                        Add(file.mode, file.srcPath, file.dstPath);
                    }
                    else
                    {
                        newDataFiles.Add(file);
                    }
                }
            }

            dataFiles = newDataFiles.Where(file => !Directory.Exists(file.srcPath)).ToList();

            if (dataFiles.Count == 0)
            {
                Console.WriteLine("Nothing to do!");
                Environment.Exit(1);
            }

            var currAbsPath = Directory.GetCurrentDirectory();

            for (var i = 0; i < dataFiles.Count; i++)
            {
                var file = dataFiles[i];

                if (!file.explicitDstPath)
                {
                    var path = file.dstPath;
                    var absPath = Path.GetFullPath(path);
                    Debug.WriteLine(path, absPath, currAbsPath);

                    if (!absPath.StartsWith(currAbsPath))
                    {
                        Console.WriteLine($"Error: Embedding \"{path}\" which is below the current directory " +
                            $"\"{currAbsPath}\". This is invalid since the current directory becomes the " +
                            "root that the generated code will see");
                        Environment.Exit(1);
                    }

                    file.dstPath = absPath.Substring(currAbsPath.Length + 1);

                    if (Path.IsPathRooted(path))
                    {
                        Console.WriteLine($"Warning: Embedding an absolute file/directory name \"{path}\" to the " +
                            "virtual filesystem. The file will be made available in the " +
                            $"relative path \"{file.dstPath}\". You can use the explicit syntax " +
                            "--preload-file srcpath@dstpath to explicitly specify the target " +
                            "location the absolute source path should be directed to.");
                    }
                }
            }

            for (var i = 0; i < dataFiles.Count; i++)
            {
                var file = dataFiles[i];
                file.dstPath = file.dstPath.Replace(Path.DirectorySeparatorChar, '/');

                if (file.dstPath.EndsWith('/'))
                {
                    file.dstPath = file.dstPath + Path.GetFileName(file.srcPath);
                }

                file.dstPath = Path.Join("/", file.dstPath);
                Debug.WriteLine($"Packaging file \"{file.srcPath}\" to VFS in path \"{file.dstPath}\".");
            }

            // Remove duplicates
            dataFiles = dataFiles
                .GroupBy(file => file.dstPath)
                .Select(group => group.First())
                .ToList();

            var metadata = new Dictionary<string, object>()
            {
                { "files", new List<object>() }
            };

            var partialDirs = new List<string>();

            foreach (var file in dataFiles)
            {
                var dirname = Path.GetDirectoryName(file.dstPath);

                if (dirname != string.Empty)
                {
                    var parts = dirname.Split('/');

                    for (var i = 0; i < parts.Length; i++)
                    {
                        var partial = string.Join('/', parts.Take(i + 1));

                        if (!partialDirs.Contains(partial))
                        {
                            code += $"Module['FS_createPath']" +
                                $"('{string.Join('/', parts.Take(i))}', '{parts[i]}', true, true);\n";
                            partialDirs.Add(partial);
                        }
                    }
                }
            }

            if (hasPreloaded)
            {
                var data = File.OpenWrite(config.Target);
                var start = 0;

                for (var i = 0; i < dataFiles.Count; i++)
                {
                    var file = dataFiles[i];
                    file.dataStart = start;
                    var curr = File.ReadAllBytes(file.srcPath);
                    file.dataEnd = start + curr.Length;
                    start += curr.Length;
                    data.Write(curr);
                }

                data.Close();

                if (start > 256 * 1024 * 1024)
                {
                    Console.WriteLine(
                        $"warning: file packager is creating an asset bundle of {start / (1024 * 1024)} MB. " +
                        "this is very large, and browsers might have trouble loading it. " +
                        "see https://hacks.mozilla.org/2015/02/synchronous-execution-and-filesystem-access-in-emscripten/");
                }

                var createData = @"
        Module['FS_createDataFile'](this.name, null, byteArray, true, true, true); // canOwn this data in the filesystem, it is a slide into the heap that will never change
        Module['removeRunDependency']('fp ' + that.name);
";

                code += @$"
    function DataRequest(start, end, audio) {{
      this.start = start;
      this.end = end;
      this.audio = audio;
    }}
    DataRequest.prototype = {{
      requests: {{}},
      open: function(mode, name) {{
        this.name = name;
        this.requests[name] = this;
        Module['addRunDependency']('fp ' + this.name);
      }},
      send: function() {{}},
      onload: function() {{
        var byteArray = this.byteArray.subarray(this.start, this.end);
        this.finish(byteArray);
      }},
      finish: function(byteArray) {{
        var that = this;
{createData}
        this.requests[this.name] = null;
      }}
    }};

        var files = metadata.files;
        for (var i = 0; i < files.length; ++i) {{
          new DataRequest(files[i].start, files[i].end, files[i].audio).open('GET', files[i].filename);
        }}";
            }

            var counter = 0;

            foreach (var file in dataFiles)
            {
                var fileName = file.dstPath;
                var dirName = Path.GetDirectoryName(fileName);
                var baseName = Path.GetFileName(fileName);

                if (file.mode == "preload")
                {
                    var varName = $"filePreload{counter}";
                    counter += 1;
                    ((List<object>)metadata["files"]).Add(new
                    {
                        FileName = file.dstPath,
                        Start = file.dataStart,
                        End = file.dataEnd,
                        Audio = AudioSuffixes.Contains(fileName.Substring(fileName.Length - 4)) ? 1 : 0,
                    });
                }
                else
                {
                    Debug.Assert(false);
                }
            }

            string use_data = null;

            if (hasPreloaded)
            {
                if (!lz4)
                {
                    if (config.HeapCopy)
                    {
                        use_data = @"
        // copy the entire loaded file into a spot in the heap. Files will refer to slices in that. They cannot be freed though
        // (we may be allocating before malloc is ready, during startup).
        var ptr = Module['getMemory'](byteArray.length);
        Module['HEAPU8'].set(byteArray, ptr);
        DataRequest.prototype.byteArray = Module['HEAPU8'].subarray(ptr, ptr+byteArray.length);
  ";
                    }
                    else
                    {
                        use_data = @"
        // Reuse the bytearray from the XHR as the source for file reads.
        DataRequest.prototype.byteArray = byteArray;
  ";
                        use_data += @"
          var files = metadata.files;
          for (var i = 0; i < files.length; ++i) {
            DataRequest.prototype.requests[files[i].filename].onload();
          }
    ";
                        use_data += "          Module['removeRunDependency']" +
                            $"('datafile_{EscapeForJSString(config.Target)}');\n";
                    }
                }

                var packageUuid = Guid.NewGuid();
                var packageName = config.Target;
                var remotePackageSize = new FileInfo(packageName).Length;
                var remotePackageName = Path.GetFileName(packageName);
                ret += @$"
    var PACKAGE_PATH;
    if (typeof window === 'object') {{
      PACKAGE_PATH = window['encodeURIComponent'](window.location.pathname.toString().substring(0, window.location.pathname.toString().lastIndexOf('/')) + '/');
    }} else if (typeof location !== 'undefined') {{
      // worker
      PACKAGE_PATH = encodeURIComponent(location.pathname.toString().substring(0, location.pathname.toString().lastIndexOf('/')) + '/');
    }} else {{
      throw 'using preloaded data can only be done on a web page or in a web worker';
    }}
    var PACKAGE_NAME = '{EscapeForJSString(config.Target)}';
    var REMOTE_PACKAGE_BASE = '{EscapeForJSString(remotePackageName)}';
    if (typeof Module['locateFilePackage'] === 'function' && !Module['locateFile']) {{
      Module['locateFile'] = Module['locateFilePackage'];
      err('warning: you defined Module.locateFilePackage, that has been renamed to Module.locateFile (using your locateFilePackage for now)');
    }}
    var REMOTE_PACKAGE_NAME = Module['locateFile'] ? Module['locateFile'](REMOTE_PACKAGE_BASE, '') : REMOTE_PACKAGE_BASE;
  ";
                metadata.Add("remote_package_size", remotePackageSize);
                metadata.Add("package_uuid", packageUuid);
                ret += @"
    var REMOTE_PACKAGE_SIZE = metadata.remote_package_size;
    var PACKAGE_UUID = metadata.package_uuid;
  ";

                ret += @"
    function fetchRemotePackage(packageName, packageSize, callback, errback) {
      var xhr = new XMLHttpRequest();
      xhr.open('GET', packageName, true);
      xhr.responseType = 'arraybuffer';
      xhr.onprogress = function(event) {
        var url = packageName;
        var size = packageSize;
        if (event.total) size = event.total;
        if (event.loaded) {
          if (!xhr.addedTotal) {
            xhr.addedTotal = true;
            if (!Module.dataFileDownloads) Module.dataFileDownloads = {};
            Module.dataFileDownloads[url] = {
              loaded: event.loaded,
              total: size
            };
          } else {
            Module.dataFileDownloads[url].loaded = event.loaded;
          }
          var total = 0;
          var loaded = 0;
          var num = 0;
          for (var download in Module.dataFileDownloads) {
          var data = Module.dataFileDownloads[download];
            total += data.total;
            loaded += data.loaded;
            num++;
          }
          total = Math.ceil(total * Module.expectedDataFileDownloads/num);
          if (Module['setStatus']) Module['setStatus']('Downloading data... (' + loaded + '/' + total + ')');
        } else if (!Module.dataFileDownloads) {
          if (Module['setStatus']) Module['setStatus']('Downloading data...');
        }
      };
      xhr.onerror = function(event) {
        throw new Error('NetworkError for: ' + packageName);
      }
      xhr.onload = function(event) {
        if (xhr.status == 200 || xhr.status == 304 || xhr.status == 206 || (xhr.status == 0 && xhr.response)) { // file URLs can return 0
          var packageData = xhr.response;
          callback(packageData);
        } else {
          throw new Error(xhr.statusText + ' : ' + xhr.responseURL);
        }
      };
      xhr.send(null);
    };
    function handleError(error) {
      console.error('package error:', error);
    };
  ";

                code += @$"'
    function processPackageData(arrayBuffer) {{
      Module.finishedDataFileDownloads++;
      assert(arrayBuffer, 'Loading data file failed.');
      assert(arrayBuffer instanceof ArrayBuffer, 'bad input to processPackageData');
      var byteArray = new Uint8Array(arrayBuffer);
      var curr;
      {use_data}
    }};
    Module['addRunDependency']('datafile_{EscapeForJSString(config.Target)}');
  ";
                code += @"
    if (!Module.preloadResults) Module.preloadResults = {};
  ";

                ret += @"
      var fetchedCallback = null;
      var fetched = Module['getPreloadedPackage'] ? Module['getPreloadedPackage'](REMOTE_PACKAGE_NAME, REMOTE_PACKAGE_SIZE) : null;
      if (!fetched) fetchRemotePackage(REMOTE_PACKAGE_NAME, REMOTE_PACKAGE_SIZE, function(data) {
        if (fetchedCallback) {
          fetchedCallback(data);
          fetchedCallback = null;
        } else {
          fetched = data;
        }
      }, handleError);
    ";

                code += @"
      Module.preloadResults[PACKAGE_NAME] = {fromCache: false};
      if (fetched) {
        processPackageData(fetched);
        fetched = null;
      } else {
        fetchedCallback = processPackageData;
      }
    ";
            }

            ret += @"
  function runWithFS() {
";
            ret += code;
            ret += @"
  }
  if (Module['calledRun']) {
    runWithFS();
  } else {
    if (!Module['preRun']) Module['preRun'] = [];
    Module['preRun'].push(runWithFS); // FS is not initialized yet, wait for it
  }
";

            var metadataTemplate = $@"
 }}
 loadPackage({JsonConverter.Convert(metadata)});
";

            ret += $@"{metadataTemplate}
}})();
";


            if (force || dataFiles.Any())
            {
                if (string.IsNullOrEmpty(config.JSOutput))
                {
                    Console.WriteLine(ret);
                }
                else
                {
                    if (File.Exists(config.JSOutput))
                    {
                        var old = File.ReadAllText(config.JSOutput);

                        if (old != ret)
                        {
                            File.WriteAllText(config.JSOutput, ret);
                        }
                    }
                    else
                    {
                        File.WriteAllText(config.JSOutput, ret);
                    }
                }
            }

            Environment.Exit(0);
        }

        static string EscapeForJSString(string s) => s.Replace('\\', '/').Replace("'", "\\'").Replace("\"", "\\\"");

        static void Add(string mode, string rootpathsrc, string rootpathdst)
        {
            foreach (var fullName in Directory.EnumerateDirectories(rootpathsrc))
            {
                if (!ShouldIgnore(fullName))
                {
                    var name = Path.GetRelativePath(rootpathsrc, fullName);
                    newDirNames.Add(name);
                }
                else
                {
                    Debug.WriteLine(
                        $"Skipping directory \"{fullName}\" from inclusion in the emscripten virtual file system.");
                }
            }

            foreach (var fullName in Directory.EnumerateFiles(rootpathsrc))
            {
                if (!ShouldIgnore(fullName))
                {
                    var dstPath = Path.Join(rootpathdst, Path.GetRelativePath(rootpathsrc, fullName));
                    newDataFiles.Add((fullName, dstPath, mode, true, -1, -1));
                }
                else
                {
                    Debug.WriteLine($"Skipping file \"{fullName}\" from inclusion in the emscripten " +
                        "virtual file system.");
                }
            }
        }

        static bool ShouldIgnore(string fullName)
        {
            var attributes = File.GetAttributes(fullName);

            if (attributes.HasFlag(FileAttributes.Hidden))
            {
                return true;
            }

            return false;
        }

        static void Usage()
        {
            // show some app description message
            Console.WriteLine("Usage: Mono.WebAssembly.FilePackger.exe --target TARGET [--preload A [B..]] [--js-output=OUTPUT.js] [--no-heap-copy] [--separate-metadata]");
            Console.WriteLine();

            // output the options
            Console.WriteLine("Options:");
            options.WriteOptionDescriptions(Console.Out);
        }
    }
}