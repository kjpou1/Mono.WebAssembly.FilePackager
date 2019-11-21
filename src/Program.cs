using System;
using System.Collections.Generic;
using Mono.Options;

namespace Mono.WebAssembly.FilePackager
{
    class Program
    {
        static Configuration config = new Configuration();
        static void Main(string[] args)
        {
            var shouldShowHelp = false;
            var options = new OptionSet {
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

        }
    }
}
