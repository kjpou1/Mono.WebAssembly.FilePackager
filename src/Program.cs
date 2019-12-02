/* Stuff considered while porting this file from Python:
 * - Var names are left equal, except been C-Sharpified: this way will help us keep changes from the original source
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

        static Configuration config = new Configuration();
        static OptionSet options;
        static List<(string srcPath, string dstPath, string mode, bool explicitDstPath)> newDataFiles = 
            new List<(string srcPath, string dstPath, string mode, bool explicitDstPath)>();
        static List<string> newDirNames = new List<string>();

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
                { "p|preload-file=", preloadfile , plf => config.PreloadFile = plf },
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
            var dataFiles = new List<(string srcPath, string dstPath, string mode, bool explicitDstPath)>();

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
                    dataFiles.Add((srcPath, dstPath, mode, usesAtNotation));
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

            // Continue at: https://github.com/emscripten-core/emscripten/blob/incoming/tools/file_packager.py#L310
        }

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
                    newDataFiles.Add((fullName, dstPath, mode, true));
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
