using System;

namespace Mono.WebAssembly.FilePackager
{
    public class Configuration
    {
        public string Target { get; set; }

        public string Preload { get; set; }

        public string JSOutput { get; set; }

        public bool HeapCopy { get; set; } = true;

        public bool SeparateMetaData { get; set; } = false;
    }
}
