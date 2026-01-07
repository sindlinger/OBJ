using System;
using System.IO;
using System.Text.Json;
using Obj.DocDetector;

namespace Obj.DocDetector.OutlineRawCheckRunner
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            {
                Console.WriteLine("Usage: dotnet run --project OutlineRawCheckRunner.csproj -- <pdf_path>");
                return 1;
            }

            var pdfPath = args[0];
            if (!File.Exists(pdfPath))
            {
                Console.WriteLine("PDF not found: " + pdfPath);
                return 1;
            }

            try
            {
                var result = OutlineRawChecker.Check(pdfPath);
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                return 2;
            }
        }
    }
}
