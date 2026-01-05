using System;
using System.Collections.Generic;

namespace FilterPDF.Commands
{
    internal static class ObjectsText
    {
        public static void Execute(string[] args)
        {
            var rest = new List<string>(args ?? Array.Empty<string>());
            rest.Add("--start");
            rest.Add("texto");
            rest.Add("--auto-list");
            ObjectsShell.Execute(rest.ToArray());
        }
    }
}
