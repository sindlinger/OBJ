using System;
using System.IO;
using Obj.DocDetector;

namespace Obj.DocDetector.ContentsTitleDetectorRunner
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            {
                Console.WriteLine("Usage: dotnet run --project ContentsTitleDetectorRunner.csproj -- <pdf_path>");
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
                var json = DocumentTitleDetector.DetectToJson(pdfPath);
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
