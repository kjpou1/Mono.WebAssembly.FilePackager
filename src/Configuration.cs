﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Mono.WebAssembly.FilePackager
{
    public class Configuration
    {
        public string Target { get; set; }
        public string PreloadFile { get; set; }
        public string JSOutput { get; set; }
        public bool HeapCopy { get; set; } = true;
        public bool SeparateMetaData { get; set; } = false;

    }
}
