using System;
using System.Collections.Generic;
using Mono.Options;

namespace Mono.WebAssembly.FilePackager
{
    class Program
    {
        static Configuration config = new Configuration();
        static OptionSet options;

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
