using System;
using System.Collections.Generic;
using System.Linq;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Unified command for PDF object inspection.
    /// Usage:
    ///   tjpdf-cli inspect objects list --input file.pdf [--limit N]
    ///   tjpdf-cli inspect objects analyze --input file.pdf [--limit N]
    ///   tjpdf-cli inspect objects deep --input file.pdf [--limit N]
    /// </summary>
    public class ObjectsCommand : Command
    {
        public override string Name => "objects";
        public override string Description => "Lists/analyzes PDF objects (basic, summary, deep)";

        public override void Execute(string[] args)
        {
            var (mode, rest) = ParseMode(args, new[] { "list", "analyze", "deep", "inspect", "summary", "types", "table", "filter", "operators", "ops", "shell", "text", "texto", "objdiff", "diff", "extractfields", "fronthead", "backtail" });
            if (string.IsNullOrWhiteSpace(mode))
            {
                ShowHelp();
                return;
            }

            switch (Normalize(mode))
            {
                case "list":
                case "inspect":
                    ObjectsListCommand.Execute(rest);
                    break;
                case "analyze":
                case "summary":
                    ObjectsListCommand.Execute(AppendDetail(rest, "analyze"));
                    break;
                case "types":
                case "table":
                    ObjectsListCommand.Execute(AppendDetail(rest, "table"));
                    break;
                case "filter":
                    ObjectsListCommand.Execute(rest);
                    break;
                case "operators":
                case "ops":
                    ObjectsOperators.Execute(rest);
                    break;
                case "fronthead":
                    ObjectsFrontBack.Execute(rest, ObjectsFrontBack.BandMode.Front);
                    break;
                case "backtail":
                    ObjectsFrontBack.Execute(rest, ObjectsFrontBack.BandMode.Back);
                    break;
                case "objdiff":
                case "diff":
                    ObjectsObjDiff.Execute(rest);
                    break;
                case "extractfields":
                    ObjectsTextOpsExtractFields.Execute(ExpandExtractFieldsArgs(rest));
                    break;
                case "deep":
                    ObjectsListCommand.Execute(AppendDetail(rest, "deep"));
                    break;
                case "shell":
                    ObjectsShell.Execute(rest);
                    break;
                case "text":
                case "texto":
                    ObjectsText.Execute(rest);
                    break;
                default:
                    Console.WriteLine($"Unknown mode: {mode}");
                    ShowHelp();
                    break;
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli inspect objects list --input file.pdf [--limit N] [--text-only] [--length]");
            Console.WriteLine("                             [--type /XObject] [--subtype /Image] [--derived texto|sem_texto|stream|dict]");
            Console.WriteLine("                             [--detail analyze|deep|table]");
            Console.WriteLine("aliases: analyze|deep|table|filter (redirecionam para list)");
            Console.WriteLine("tjpdf-cli inspect objects operators <subcmd> [...]");
            Console.WriteLine("  subcmd: text|var|fixed|diff|anchors");
            Console.WriteLine("tjpdf-cli inspect objects objdiff --input target.pdf --inputs a.pdf,b.pdf [--input-dir <dir>] [--page N|--pages 1,3] [--top N] [--min-score 0.70]");
            Console.WriteLine("                                   [--band front|back] [--y-range 0.70-1.00]");
            Console.WriteLine("tjpdf-cli inspect objects fronthead --input file.pdf [--page N|--pages 1,3] [--y-range 0.70-1.00] [--include-xobject] [--limit N]");
            Console.WriteLine("tjpdf-cli inspect objects backtail --input file.pdf [--page N|--pages 1,3] [--y-range 0.00-0.35] [--include-xobject] [--limit N]");
            Console.WriteLine("tjpdf-cli inspect objects extractfields --input file.pdf [--map <map.yml>] [--fields a,b] [--validate] [--json] [--out <arquivo>]");
            Console.WriteLine("                                     [--apply-rules] [--config <config.yaml>] [--anchors|--require-anchors]");
            Console.WriteLine("                                     [--self-variable|--self-fixed] [--min-token-len N] [--max-token-len N]");
            Console.WriteLine("                                     [--min-text-len N] [--max-text-len N]");
            Console.WriteLine("                                     [--rules <yml> | --doc <nome>]");
            Console.WriteLine("tjpdf-cli inspect objects extractfields PJ (PROCESSO_JUDICIAL) --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields PA (PROCESSO_ADMINISTRATIVO, obrigatório) --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields VARA --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields COMARCA --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields PROMOVENTE (sinônimos: AUTOR) --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields PROMOVIDO (sinônimos: REU) --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields PERITO --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields CPF_PERITO (sinônimos: CPF) --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields ESPECIALIDADE (sinônimos: ESPEC, ESPEC.) --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields ESPECIE_DA_PERICIA (sinônimos: ESP_PERICIA) --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields VALOR_JZ (VALOR_ARBITRADO_JZ) --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields VALOR_DE (VALOR_ARBITRADO_DE) --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects extractfields DATA_DESPESA (DATA_ARBITRADO_FINAL) --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects shell --input file.pdf [--last]");
            Console.WriteLine("tjpdf-cli inspect objects text --input file.pdf");
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

        private static string[] AppendDetail(string[] args, string detail)
        {
            if (args.Any(a => string.Equals(a, "--detail", StringComparison.OrdinalIgnoreCase)))
                return args;
            var list = new List<string>(args) { "--detail", detail };
            return list.ToArray();
        }

        private static string[] ExpandExtractFieldsArgs(string[] args)
        {
            if (args.Length == 0)
                return args;

            var head = args[0];
            if (head.StartsWith("-", StringComparison.Ordinal))
                return args;

            if (!TryGetExtractField(head, out var fields))
                return args;

            var list = new List<string>(args.Length + 2);
            list.Add("--fields");
            list.Add(string.Join(",", fields));
            list.AddRange(args.Skip(1));
            return list.ToArray();
        }

        private static bool TryGetExtractField(string name, out string[] fields)
        {
            fields = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(name))
                return false;

            switch (name.Trim().ToUpperInvariant())
            {
                case "PA":
                case "PROC_ADM":
                case "PROCESSO_ADMINISTRATIVO":
                    fields = new[] { "PROCESSO_ADMINISTRATIVO" };
                    return true;
                case "PJ":
                case "PROC_JUD":
                case "PROCESSO_JUDICIAL":
                    fields = new[] { "PROCESSO_JUDICIAL" };
                    return true;
                case "VARA":
                    fields = new[] { "VARA" };
                    return true;
                case "COMARCA":
                    fields = new[] { "COMARCA" };
                    return true;
                case "AUTOR":
                case "PROMOVENTE":
                    fields = new[] { "PROMOVENTE" };
                    return true;
                case "REU":
                case "PROMOVIDO":
                    fields = new[] { "PROMOVIDO" };
                    return true;
                case "PERITO":
                    fields = new[] { "PERITO" };
                    return true;
                case "CPF":
                case "CPF_PERITO":
                    fields = new[] { "CPF_PERITO" };
                    return true;
                case "ESPEC":
                case "ESPEC.":
                case "ESPECIALIDADE":
                    fields = new[] { "ESPECIALIDADE" };
                    return true;
                case "ESP_PERICIA":
                case "ESPECIE_PERICIA":
                case "ESPECIE_DA_PERICIA":
                    fields = new[] { "ESPECIE_DA_PERICIA" };
                    return true;
                case "VALOR_JZ":
                case "VALOR_ARBITRADO_JZ":
                    fields = new[] { "VALOR_ARBITRADO_JZ" };
                    return true;
                case "VALOR_DE":
                case "VALOR_ARBITRADO_DE":
                    fields = new[] { "VALOR_ARBITRADO_DE" };
                    return true;
                case "DATA_DESPESA":
                case "DATA_ARBITRADO_FINAL":
                    fields = new[] { "DATA_DESPESA" };
                    return true;
                default:
                    return false;
            }
        }
    }
}
