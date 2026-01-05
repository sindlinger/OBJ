using System;
using System.Collections.Generic;
using System.Linq;

namespace FilterPDF.Commands
{
    internal static class ObjectsOperators
    {
        public static void Execute(string[] args)
        {
            var (mode, rest) = ParseMode(args, new[] { "text", "operators", "textoperators", "var", "variavel", "variaveis", "fixed", "fixo", "fixos", "diff", "anchors", "ancoras" });
            if (string.IsNullOrWhiteSpace(mode))
            {
                ShowHelp();
                return;
            }

            switch (Normalize(mode))
            {
                case "text":
                case "operators":
                case "textoperators":
                    ObjectsTextOperators.Execute(rest);
                    break;
                case "var":
                case "variavel":
                case "variaveis":
                    ObjectsTextOpsDiff.Execute(rest, ObjectsTextOpsDiff.DiffMode.Variations);
                    break;
                case "fixed":
                case "fixo":
                case "fixos":
                    ObjectsTextOpsDiff.Execute(rest, ObjectsTextOpsDiff.DiffMode.Fixed);
                    break;
                case "diff":
                    ObjectsTextOpsDiff.Execute(rest, ObjectsTextOpsDiff.DiffMode.Both);
                    break;
                case "anchors":
                case "ancoras":
                    ObjectsTextOpsDiff.Execute(EnsureAnchorsFlag(rest), ObjectsTextOpsDiff.DiffMode.Variations);
                    break;
                default:
                    Console.WriteLine($"Unknown operators mode: {mode}");
                    ShowHelp();
                    break;
            }
        }

        private static void ShowHelp()
        {
            Console.WriteLine("tjpdf.exe objects operators text --input file.pdf [--id N] [--limit N] [--op Tj,TJ]");
            Console.WriteLine("tjpdf.exe objects operators diff --inputs a.pdf,b.pdf --obj N [--op Tj,TJ] [--doc tjpb_despacho]");
            Console.WriteLine("  Defaults: full-text diff + range text + line breaks (Td/Tm) as space + cleanup lossless");
            Console.WriteLine("  Range: front_head em configs/textops_anchors/<doc>_obj<obj>_roi.yml");
            Console.WriteLine("tjpdf.exe objects operators var --input file.pdf --obj N --self [--anchors] [--anchors-out <dir|file>]");
            Console.WriteLine("tjpdf.exe objects operators fixed --input file.pdf --obj N --self [--rules <yml> | --doc <nome>]");
            Console.WriteLine("tjpdf.exe objects operators anchors --input file.pdf --obj N --self [--anchors-out <dir|file>] [--anchors-merge]");
        }

        private static string[] EnsureAnchorsFlag(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--anchors", StringComparison.OrdinalIgnoreCase)))
                return args;
            var list = new List<string>(args) { "--anchors" };
            return list.ToArray();
        }

        private static (string mode, string[] rest) ParseMode(string[] args, string[] modes)
        {
            string mode = "";
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if ((arg == "--mode" || arg == "--action") && i + 1 < args.Length)
                {
                    mode = args[++i];
                    continue;
                }
                if (!arg.StartsWith("-") && string.IsNullOrWhiteSpace(mode))
                {
                    mode = arg;
                    continue;
                }
                if (arg.StartsWith("--") && string.IsNullOrWhiteSpace(mode))
                {
                    var flag = arg.TrimStart('-');
                    if (modes.Contains(flag, StringComparer.OrdinalIgnoreCase))
                    {
                        mode = flag;
                        continue;
                    }
                }
                rest.Add(arg);
            }
            return (mode, rest.ToArray());
        }

        private static string Normalize(string mode)
        {
            return (mode ?? "").Trim().ToLowerInvariant();
        }
    }
}
