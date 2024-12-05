using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConvertNPCVisualReplacerToSkyPatcher.Models
{
    internal class ModsToConvertModel
    {
        public string ModToConvertPath { get; set; }
        public string ModFilename { get; set; }
        public bool Convert { get; set; }

        public ModsToConvertModel()
        {
            ModToConvertPath = string.Empty;
            ModFilename = string.Empty;
            Convert = false;
        }
    }
}
