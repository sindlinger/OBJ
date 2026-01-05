using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Interactive shell to navigate objects by type/subtype.
    /// Usage: tjpdf-cli inspect objects shell --input file.pdf
    /// </summary>
    internal static class ObjectsShell
    {
        public static void Execute(string[] args)
        {
            if (!ParseOptions(args, out var inputFile, out var startType, out var autoList))
                return;

            PdfDocument? doc = null;
            ObjectIndex? index = null;
            var state = new ShellState
            {
                Identifier = ""
            };

            try
            {
                if (!TryOpenPdfForShell(inputFile, ref doc, ref index, state))
                    return;

                if (!string.IsNullOrWhiteSpace(startType))
                {
                    ChangeDir(index!, state, startType, "");
                    if (autoList)
                        ListCurrent(doc!, index!, state);
                }

                PrintShellHelp();
                while (true)
                {
                    WritePrompt(state);
                    var line = Console.ReadLine();
                    if (line == null) break;
                    var parts = SplitArgs(line);
                    if (parts.Count == 0) continue;

                    var cmd = parts[0].ToLowerInvariant();
                    if (cmd is "exit" or "quit")
                        break;

                    if (HandlePdfCommands(parts, ref doc, ref index, state))
                        continue;

                    if (cmd is "help" or "?")
                    {
                        if (parts.Count >= 2 && IsSchemaCommand(parts))
                        {
                            PrintSchema();
                        }
                        else
                        {
                        PrintShellHelp();
                    }
                    continue;
                }
                if (cmd is "schema")
                    {
                        PrintSchema();
                        continue;
                    }
                    if (cmd is "show")
                    {
                        if (parts.Count >= 2 && (parts[1].Equals("schema", StringComparison.OrdinalIgnoreCase) ||
                                                 parts[1].Equals("estrutura", StringComparison.OrdinalIgnoreCase)))
                        {
                            PrintSchema();
                            continue;
                        }
                    }
                if (cmd is "pwd")
                {
                    Console.WriteLine(PathOf(state));
                    continue;
                }
                if (state.ObjectId > 0)
                {
                    if (cmd is "text" or "texto")
                    {
                        state.View = "text";
                        ShowObjectView(doc!, index!, state.ObjectId, state.View);
                        continue;
                    }
                    if (cmd is "dump" or "raw")
                    {
                        state.View = "dump";
                        ShowObjectView(doc!, index!, state.ObjectId, state.View);
                        continue;
                    }
                    if (cmd is "dumphex" or "hex")
                    {
                        state.View = "dumphex";
                        ShowObjectView(doc!, index!, state.ObjectId, state.View);
                        continue;
                    }
                    if (cmd is "operators" or "ops")
                    {
                        state.View = "operators";
                        ShowObjectView(doc!, index!, state.ObjectId, state.View);
                        continue;
                    }
                    if (cmd is "textoperators" or "textops")
                    {
                        state.View = "textoperators";
                        ShowObjectView(doc!, index!, state.ObjectId, state.View);
                        continue;
                    }
                    if (cmd is "info" or "keys" or "dict")
                    {
                        state.View = "info";
                        ShowObjectView(doc!, index!, state.ObjectId, state.View);
                        continue;
                    }
                }
                if (cmd is "ls")
                {
                    ListCurrent(doc!, index!, state);
                    continue;
                }
                if (cmd is "cd" or "up" or "back" or "..")
                {
                    var arg1 = parts.Count > 1 ? parts[1] : "";
                    var arg2 = parts.Count > 2 ? parts[2] : "";
                    if (TryResolveLocalId(state, arg1, out var localId))
                    {
                        EnterObject(doc!, index!, state, localId);
                        continue;
                    }
                    if (cmd == "cd" && string.Equals(arg1, "d", StringComparison.OrdinalIgnoreCase) && parts.Count > 2)
                    {
                        if (TryResolveLocalId(state, "d" + parts[2], out var localCmdId))
                        {
                            EnterObject(doc!, index!, state, localCmdId);
                            continue;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(arg1) && int.TryParse(arg1, out var cdId))
                    {
                        EnterObject(doc!, index!, state, cdId);
                        continue;
                    }
                    ChangeDir(index!, state, arg1, arg2);
                    continue;
                }
                if (cmd is "open" or "show")
                {
                    if (parts.Count < 2 || !int.TryParse(parts[1], out var id))
                    {
                        if (parts.Count >= 2 && TryResolveLocalId(state, parts[1], out var localOpenId))
                        {
                            EnterObject(doc!, index!, state, localOpenId);
                            continue;
                        }
                        if (parts.Count >= 3 && string.Equals(parts[1], "d", StringComparison.OrdinalIgnoreCase)
                            && TryResolveLocalId(state, "d" + parts[2], out var localOpenId2))
                        {
                            EnterObject(doc!, index!, state, localOpenId2);
                            continue;
                        }
                        Console.WriteLine("Uso: open <id> | open d<pos>");
                        continue;
                    }
                    EnterObject(doc!, index!, state, id);
                    continue;
                }

                if ((cmd == "objeto" || cmd == "obj") && parts.Count >= 2 && int.TryParse(parts[1], out var objId))
                {
                    EnterObject(doc!, index!, state, objId);
                    continue;
                }

                if (cmd == "d")
                {
                    if (parts.Count >= 2 && TryResolveLocalId(state, "d" + parts[1], out var localId))
                    {
                        EnterObject(doc!, index!, state, localId);
                        continue;
                    }
                    Console.WriteLine("Uso: d <pos>");
                    continue;
                }

                if (parts.Count == 1 && int.TryParse(parts[0], out var quickId))
                {
                    EnterObject(doc!, index!, state, quickId);
                    continue;
                }

                // Implicit "cd" for convenience (e.g., "texto", "sem tipo", "sem texto")
                var implicitArg1 = parts[0];
                var implicitArg2 = parts.Count > 1 ? parts[1] : "";
                if (TryImplicitCd(index!, state, implicitArg1, implicitArg2))
                    continue;

                Console.WriteLine("Comando desconhecido. Use 'help'.");
            }
                doc?.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Falha ao abrir PDF: {ex.Message}");
            }
        }

        private static bool ParseOptions(string[] args, out string inputFile, out string startType, out bool autoList)
        {
            inputFile = "";
            startType = "";
            autoList = false;
            var useLast = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                    continue;
                }
                if (string.Equals(args[i], "--start", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    startType = args[++i];
                    continue;
                }
                if (string.Equals(args[i], "--auto-list", StringComparison.OrdinalIgnoreCase))
                {
                    autoList = true;
                    continue;
                }
                if (string.Equals(args[i], "--last", StringComparison.OrdinalIgnoreCase))
                {
                    useLast = true;
                    continue;
                }
                if (!args[i].StartsWith("-") && string.IsNullOrWhiteSpace(inputFile))
                {
                    inputFile = args[i];
                }
            }

            if (string.IsNullOrWhiteSpace(inputFile) && useLast)
                inputFile = LoadLastInput();

            if (string.IsNullOrWhiteSpace(inputFile))
            {
                Console.Write("Arquivo PDF para shell de objetos: ");
                inputFile = Console.ReadLine()?.Trim() ?? "";
            }

            if (string.IsNullOrWhiteSpace(inputFile))
            {
                Console.WriteLine("Cancelado.");
                return false;
            }

            inputFile = FilterPDF.Utils.PathUtils.NormalizePathForCurrentOS(inputFile.Trim());
            if (string.IsNullOrWhiteSpace(inputFile))
            {
                Console.WriteLine("Cancelado.");
                return false;
            }

            if (!File.Exists(inputFile))
            {
                Console.WriteLine($"Arquivo não encontrado: {inputFile}");
                return false;
            }

            inputFile = Path.GetFullPath(inputFile);
            SaveLastInput(inputFile);
            return true;
        }

        private static void PrintShellHelp()
        {
            Console.WriteLine("Comandos:");
            Console.WriteLine("  ls                -> lista tipos/subtipos/objetos conforme o nivel");
            Console.WriteLine("  cd <type> [sub]   -> entra no tipo/subtipo");
            Console.WriteLine("  cd texto|sem_texto|stream|dict -> entra nos derivados de (sem tipo)");
            Console.WriteLine("  texto | sem tipo | sem texto -> atalhos para entrar no subtipo");
            Console.WriteLine("  cd <id>           -> abre pelo ID real (coluna ID)");
            Console.WriteLine("  cd d<pos>         -> abre objeto pela posicao da lista atual (alias local)");
            Console.WriteLine("  cd text|operators|textoperators|dump|dumphex|info -> entra em detalhes do objeto");
            Console.WriteLine("  cd .. | up | back -> volta um nivel");
            Console.WriteLine("  pwd               -> mostra o caminho atual");
            Console.WriteLine("  open <id>         -> abre pelo ID real (coluna ID)");
            Console.WriteLine("  open d<pos>       -> abre objeto pela posicao da lista atual");
            Console.WriteLine("  <id>              -> atalho para 'open <id>'");
            Console.WriteLine("  objeto <id>       -> atalho para 'open <id>'");
            Console.WriteLine("  d <pos>           -> abre objeto pela posicao da lista atual");
            Console.WriteLine("  text              -> (dentro do objeto) mostra texto extraido do stream");
            Console.WriteLine("  operators         -> (dentro do objeto) lista operadores");
            Console.WriteLine("  textoperators     -> (dentro do objeto) lista apenas operadores de texto");
            Console.WriteLine("  dump              -> (dentro do objeto) mostra stream bruto");
            Console.WriteLine("  dumphex           -> (dentro do objeto) mostra stream em hex");
            Console.WriteLine("  info              -> (dentro do objeto) mostra chaves e metadados");
            Console.WriteLine("  list              -> lista PDFs monitorados");
            Console.WriteLine("  current           -> mostra PDF atual");
            Console.WriteLine("  add <path>        -> adiciona PDF (arquivo/dir/glob)");
            Console.WriteLine("  watch <dir>       -> adiciona todos PDFs do diretório");
            Console.WriteLine("  dir <dir>         -> define diretório base para paths relativos");
            Console.WriteLine("  clear             -> limpa lista de PDFs");
            Console.WriteLine("  reset <dir>       -> limpa e recarrega a partir de um diretorio");
            Console.WriteLine("  checkout <n|path> -> troca PDF atual");
            Console.WriteLine("  (pdf list/current/add/watch/dir/clear/reset/checkout também funcionam)");
            Console.WriteLine("  schema | show schema | show estrutura | help schema -> mostra estrutura geral do PDF");
            Console.WriteLine("  help              -> ajuda");
            Console.WriteLine("  exit              -> sair");
            Console.WriteLine();
        }

        private static bool IsSchemaCommand(List<string> parts)
        {
            if (parts.Count >= 2 && parts[1].Equals("schema", StringComparison.OrdinalIgnoreCase))
                return true;
            if (parts.Count >= 3 &&
                parts[1].Equals("show", StringComparison.OrdinalIgnoreCase) &&
                (parts[2].Equals("schema", StringComparison.OrdinalIgnoreCase) ||
                 parts[2].Equals("estrutura", StringComparison.OrdinalIgnoreCase)))
                return true;
            return false;
        }

        private static void PrintSchema()
        {
            Console.WriteLine("Estrutura geral (esquema completo, aberto):");
            Console.WriteLine("/Catalog");
            Console.WriteLine("  /Pages");
            Console.WriteLine("    /Kids -> [/Page | /Pages]");
            Console.WriteLine("    /Count");
            Console.WriteLine("  /Outlines (bookmarks)");
            Console.WriteLine("    /First /Last /Count");
            Console.WriteLine("    /Title /Dest /A");
            Console.WriteLine("  /Names");
            Console.WriteLine("    /Dests (NameTree)");
            Console.WriteLine("  /AcroForm (formularios)");
            Console.WriteLine("  /Metadata");
            Console.WriteLine("  /StructTreeRoot");
            Console.WriteLine();
            Console.WriteLine("/Page");
            Console.WriteLine("  /Contents (stream | array de streams)");
            Console.WriteLine("  /Resources");
            Console.WriteLine("    /Font -> /Type0, /Type1, /TrueType, /Type3");
            Console.WriteLine("      /Type0 -> /DescendantFonts -> /CIDFontType0 | /CIDFontType2");
            Console.WriteLine("    /XObject -> /Image | /Form");
            Console.WriteLine("    /ExtGState");
            Console.WriteLine("    /ColorSpace /Pattern /Shading (opcionais)");
            Console.WriteLine("  /Annots -> /Link | /Widget | /Text | /Highlight | /Underline ...");
            Console.WriteLine("  /MediaBox /CropBox /Rotate");
            Console.WriteLine();
            Console.WriteLine("/Annot");
            Console.WriteLine("  /Subtype /Link -> /A (Action)");
            Console.WriteLine("  /Subtype /Widget -> campos de formulario");
            Console.WriteLine();
            Console.WriteLine("/Action");
            Console.WriteLine("  /S /URI | /GoTo | /GoToR | /Launch | /JavaScript");
            Console.WriteLine();
            Console.WriteLine("/XObject");
            Console.WriteLine("  /Subtype /Image -> Width/Height/ColorSpace/BitsPerComponent/Filter");
            Console.WriteLine("  /Subtype /Form  -> stream com /Resources e /BBox");
            Console.WriteLine();
            Console.WriteLine("Dica: use 'ls' para ver o mapa real do arquivo atual.");
            Console.WriteLine();
        }

        private static void WritePrompt(ShellState state)
        {
            var pdfPath = string.IsNullOrWhiteSpace(state.InputFile) ? "<pdf>" : state.InputFile;
            if (state.CurrentFileIndex >= 0 && state.Files.Count > 0)
                pdfPath = $"[{state.CurrentFileIndex + 1}/{state.Files.Count}] {pdfPath}";
            var prompt = $"TJPDF {pdfPath} {PathOf(state)}> ";
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(prompt);
            Console.ForegroundColor = prev;
        }

        private static bool HandlePdfCommands(List<string> parts, ref PdfDocument? doc, ref ObjectIndex? index, ShellState state)
        {
            if (parts.Count == 0)
                return false;

            var cmd = parts[0].ToLowerInvariant();
            if (cmd is "add" or "watch" or "dir" or "list" or "checkout" or "current" or "clear" or "reset")
            {
                var subCmd = cmd switch
                {
                    "add" => "add",
                    "watch" => "add",
                    "dir" => "dir",
                    "list" => "list",
                    "checkout" => "checkout",
                    "current" => "current",
                    "clear" => "clear",
                    "reset" => "reset",
                    _ => "list"
                };
                var synthetic = new List<string> { "pdf", subCmd };
                synthetic.AddRange(parts.Skip(1));
                return HandlePdfCommands(synthetic, ref doc, ref index, state);
            }

            if (cmd is not ("pdf" or "file"))
                return false;

            if (parts.Count == 1)
            {
                PrintPdfList(state);
                return true;
            }

            var sub = parts[1].ToLowerInvariant();
            if (sub is "list" or "ls")
            {
                PrintPdfList(state);
                return true;
            }
            if (sub is "current" or "cur")
            {
                PrintCurrentPdf(state);
                return true;
            }
            if (sub is "dir" or "cd")
            {
                if (parts.Count < 3)
                {
                    PrintBaseDir(state);
                    return true;
                }
                var target = string.Join(" ", parts.Skip(2));
                if (!TrySetBaseDir(state, target))
                {
                    Console.WriteLine("Diretorio invalido.");
                }
                else
                {
                    PrintBaseDir(state);
                }
                return true;
            }
            if (sub is "clear" or "reset")
            {
                var target = parts.Count > 2 ? string.Join(" ", parts.Skip(2)) : "";
                ClearFiles(state);
                if (string.IsNullOrWhiteSpace(target))
                {
                    Console.WriteLine("Lista de PDFs limpa.");
                    return true;
                }
                var added = AddFilesFromPath(state, target);
                if (added.Count == 0)
                {
                    Console.WriteLine("Nenhum PDF encontrado.");
                    return true;
                }
                Console.WriteLine($"Adicionados {added.Count} PDF(s).");
                if (TryOpenPdfForShell(added[0], ref doc, ref index, state))
                    Console.WriteLine($"PDF atual: {state.InputFile}");
                return true;
            }
            if (sub is "add" or "watch")
            {
                var path = parts.Count > 2 ? string.Join(" ", parts.Skip(2)) : "";
                if (string.IsNullOrWhiteSpace(path))
                {
                    Console.WriteLine("Uso: pdf add <arquivo|dir|glob>");
                    return true;
                }
                var added = AddFilesFromPath(state, path);
                if (added.Count == 0)
                {
                    Console.WriteLine("Nenhum PDF encontrado.");
                    return true;
                }
                Console.WriteLine($"Adicionados {added.Count} PDF(s).");
                if (state.CurrentFileIndex < 0)
                {
                    if (TryOpenPdfForShell(added[0], ref doc, ref index, state))
                        Console.WriteLine($"PDF atual: {state.InputFile}");
                }
                return true;
            }
            if (sub is "open" or "use" or "switch" or "checkout")
            {
                if (parts.Count < 3)
                {
                    Console.WriteLine("Uso: pdf open <n|arquivo>");
                    return true;
                }
                var target = string.Join(" ", parts.Skip(2));
                if (int.TryParse(target, out var idx))
                {
                    var file = GetFileByIndex(state, idx);
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        Console.WriteLine("Indice inválido.");
                        return true;
                    }
                    if (TryOpenPdfForShell(file, ref doc, ref index, state))
                        Console.WriteLine($"PDF atual: {state.InputFile}");
                    return true;
                }

                var normalized = ResolvePath(state, target);
                if (string.IsNullOrWhiteSpace(normalized) || !File.Exists(normalized))
                {
                    Console.WriteLine($"Arquivo não encontrado: {target}");
                    return true;
                }
                if (TryOpenPdfForShell(normalized, ref doc, ref index, state))
                    Console.WriteLine($"PDF atual: {state.InputFile}");
                return true;
            }

            Console.WriteLine("Comando pdf desconhecido. Use: pdf list|current|add|open.");
            return true;
        }

        private static List<string> SplitArgs(string line)
        {
            var args = new List<string>();
            if (string.IsNullOrWhiteSpace(line))
                return args;

            var sb = new StringBuilder();
            var inQuotes = false;
            var quoteChar = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inQuotes)
                {
                    if (c == quoteChar)
                    {
                        inQuotes = false;
                        continue;
                    }
                    if (c == '\\' && i + 1 < line.Length && line[i + 1] == quoteChar)
                    {
                        sb.Append(quoteChar);
                        i++;
                        continue;
                    }
                    sb.Append(c);
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inQuotes = true;
                    quoteChar = c;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0)
                    {
                        args.Add(sb.ToString());
                        sb.Clear();
                    }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0)
                args.Add(sb.ToString());
            return args;
        }

        private static bool TryOpenPdfForShell(string inputFile, ref PdfDocument? doc, ref ObjectIndex? index, ShellState state)
        {
            var normalized = ResolvePath(state, inputFile);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                Console.WriteLine("Informe um arquivo PDF válido.");
                return false;
            }
            if (!File.Exists(normalized))
            {
                Console.WriteLine($"Arquivo não encontrado: {normalized}");
                return false;
            }

            var full = Path.GetFullPath(normalized);
            try
            {
                doc?.Close();
                doc = new PdfDocument(new PdfReader(full));
                index = BuildIndex(doc);
                UpdateCurrentFile(state, full);
                ResetNavigation(state);
                SaveLastInput(full);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Falha ao abrir PDF: {ex.Message}");
                return false;
            }
        }

        private static void UpdateCurrentFile(ShellState state, string fullPath)
        {
            var comparer = StringComparer.OrdinalIgnoreCase;
            var existing = state.Files.FindIndex(f => comparer.Equals(f, fullPath));
            if (existing < 0)
            {
                state.Files.Add(fullPath);
                existing = state.Files.Count - 1;
            }
            state.CurrentFileIndex = existing;
            state.InputFile = fullPath;
        }

        private static void ResetNavigation(ShellState state)
        {
            state.Type = "";
            state.Subtype = "";
            state.ObjectId = 0;
            state.View = "";
            state.LastListIds.Clear();
        }

        private static List<string> AddFilesFromPath(ShellState state, string path)
        {
            var added = new List<string>();
            if (string.IsNullOrWhiteSpace(path))
                return added;

            var normalized = ResolvePath(state, path);
            if (string.IsNullOrWhiteSpace(normalized))
                return added;

            if (Directory.Exists(normalized))
            {
                var files = Directory.GetFiles(normalized, "*.pdf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                    AddUniqueFile(state, added, Path.GetFullPath(file));
                return added;
            }

            if (File.Exists(normalized))
            {
                AddUniqueFile(state, added, Path.GetFullPath(normalized));
                return added;
            }

            var dir = Path.GetDirectoryName(normalized);
            var pattern = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(dir))
                dir = ".";
            if (!string.IsNullOrWhiteSpace(pattern) && Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, pattern).Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
                foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    AddUniqueFile(state, added, Path.GetFullPath(file));
            }
            return added;
        }

        private static void AddUniqueFile(ShellState state, List<string> added, string fullPath)
        {
            var comparer = StringComparer.OrdinalIgnoreCase;
            if (state.Files.Any(f => comparer.Equals(f, fullPath)))
                return;
            state.Files.Add(fullPath);
            added.Add(fullPath);
        }

        private static void PrintPdfList(ShellState state)
        {
            if (state.Files.Count == 0)
            {
                Console.WriteLine("Nenhum PDF monitorado.");
                return;
            }
            for (int i = 0; i < state.Files.Count; i++)
            {
                var mark = i == state.CurrentFileIndex ? "*" : " ";
                Console.WriteLine($"{mark} [{i + 1}] {state.Files[i]}");
            }
        }

        private static void PrintCurrentPdf(ShellState state)
        {
            if (string.IsNullOrWhiteSpace(state.InputFile))
            {
                Console.WriteLine("Nenhum PDF atual.");
                return;
            }
            Console.WriteLine(state.InputFile);
        }

        private static void PrintBaseDir(ShellState state)
        {
            if (string.IsNullOrWhiteSpace(state.BaseDir))
            {
                Console.WriteLine("Base dir: (vazio)");
                return;
            }
            Console.WriteLine($"Base dir: {state.BaseDir}");
        }

        private static bool TrySetBaseDir(ShellState state, string path)
        {
            var normalized = FilterPDF.Utils.PathUtils.NormalizePathForCurrentOS(path);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;
            if (!Path.IsPathRooted(normalized) && !string.IsNullOrWhiteSpace(state.BaseDir))
                normalized = Path.Combine(state.BaseDir, normalized);
            if (!Directory.Exists(normalized))
                return false;
            state.BaseDir = Path.GetFullPath(normalized);
            return true;
        }

        private static void ClearFiles(ShellState state)
        {
            state.Files.Clear();
            state.CurrentFileIndex = -1;
            state.InputFile = "";
            ResetNavigation(state);
        }

        private static string ResolvePath(ShellState state, string path)
        {
            var normalized = FilterPDF.Utils.PathUtils.NormalizePathForCurrentOS(path);
            if (string.IsNullOrWhiteSpace(normalized))
                return "";
            if (Path.IsPathRooted(normalized))
                return normalized;
            if (!string.IsNullOrWhiteSpace(state.BaseDir))
                return Path.Combine(state.BaseDir, normalized);
            return normalized;
        }

        private static string GetFileByIndex(ShellState state, int index)
        {
            if (index <= 0 || index > state.Files.Count)
                return "";
            return state.Files[index - 1];
        }

        private static string LoadLastInput()
        {
            try
            {
                if (!File.Exists(LastPromptPathFile))
                    return "";
                return File.ReadAllText(LastPromptPathFile).Trim();
            }
            catch
            {
                return "";
            }
        }

        private static void SaveLastInput(string inputFile)
        {
            try
            {
                var dir = Path.GetDirectoryName(LastPromptPathFile);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(LastPromptPathFile, inputFile ?? "");
            }
            catch
            {
                // Best-effort cache for convenience; ignore errors.
            }
        }

        private static readonly string LastPromptPathFile = Path.Combine("tmp", "prompt_last.txt");

        private static string PathOf(ShellState state)
        {
            if (string.IsNullOrWhiteSpace(state.Type))
            {
                if (state.ObjectId > 0)
                {
                    var view = string.IsNullOrWhiteSpace(state.View) ? "" : "/" + state.View;
                    return $"/objeto {state.ObjectId}{view}";
                }
                return "/";
            }

            var typePart = state.Type.StartsWith("/", StringComparison.Ordinal) ? state.Type : "/" + state.Type;
            if (string.IsNullOrWhiteSpace(state.Subtype))
            {
                if (state.ObjectId > 0)
                {
                    var view = string.IsNullOrWhiteSpace(state.View) ? "" : "/" + state.View;
                    return $"{typePart}/objeto {state.ObjectId}{view}";
                }
                return typePart;
            }

            var subtypePart = state.Subtype.StartsWith("/", StringComparison.Ordinal) ? state.Subtype : "/" + state.Subtype;
            if (state.ObjectId > 0)
            {
                var view = string.IsNullOrWhiteSpace(state.View) ? "" : "/" + state.View;
                return $"{typePart}{subtypePart}/objeto {state.ObjectId}{view}";
            }
            return $"{typePart}{subtypePart}";
        }

        private static void ChangeDir(ObjectIndex index, ShellState state, string typeToken, string subtypeToken)
        {
            if (string.IsNullOrWhiteSpace(typeToken) || typeToken == ".." || typeToken == "up" || typeToken == "back")
            {
                if (!string.IsNullOrWhiteSpace(state.View))
                {
                    state.View = "";
                    return;
                }
                if (state.ObjectId > 0)
                {
                    state.ObjectId = 0;
                    return;
                }
                if (!string.IsNullOrWhiteSpace(state.Subtype))
                {
                    state.Subtype = "";
                    return;
                }
                state.Type = "";
                return;
            }

            if (typeToken == "/" || typeToken == "root")
            {
                state.View = "";
                state.ObjectId = 0;
                state.Type = "";
                state.Subtype = "";
                return;
            }

            if (state.ObjectId > 0 && TryNormalizeView(typeToken, subtypeToken, out var view))
            {
                state.View = view;
                return;
            }

            if (TryNormalizeSemTipo(typeToken, subtypeToken, out var semType, out var semSubtype))
            {
                state.View = "";
                state.ObjectId = 0;
                state.Type = semType;
                state.Subtype = semSubtype;
                return;
            }

            if (typeToken == "sem" && subtypeToken == "tipo")
            {
                state.View = "";
                state.Type = "sem tipo";
                state.Subtype = "";
                return;
            }
            if (typeToken == "sem" && subtypeToken == "texto")
            {
                state.View = "";
                state.Type = "sem tipo";
                state.Subtype = "sem_texto";
                return;
            }

            var derived = NormalizeDerived(typeToken);
            if (!string.IsNullOrWhiteSpace(derived))
            {
                state.View = "";
                state.ObjectId = 0;
                state.Type = "sem tipo";
                state.Subtype = derived;
                return;
            }

            // if already inside a type, allow "cd <subtype>" to go deeper
            if (!string.IsNullOrWhiteSpace(state.Type) && string.IsNullOrWhiteSpace(state.Subtype) && string.IsNullOrWhiteSpace(subtypeToken))
            {
                var candidateSub = NormalizeTypeName(typeToken);
                if (index.ByType.TryGetValue(state.Type, out var subs) && subs.ContainsKey(candidateSub))
                {
                    state.View = "";
                    state.ObjectId = 0;
                    state.Subtype = candidateSub;
                    return;
                }
            }

            var normType = NormalizeTypeName(typeToken);
            state.View = "";
            state.ObjectId = 0;
            state.Type = normType;
            state.Subtype = "";
            if (!string.IsNullOrWhiteSpace(subtypeToken))
                state.Subtype = NormalizeTypeName(subtypeToken);
        }

        private static bool TryImplicitCd(ObjectIndex index, ShellState state, string typeToken, string subtypeToken)
        {
            if (string.IsNullOrWhiteSpace(typeToken)) return false;

            if (TryNormalizeSemTipo(typeToken, subtypeToken, out var semType, out var semSubtype))
            {
                state.ObjectId = 0;
                state.Type = semType;
                state.Subtype = semSubtype;
                return true;
            }

            if (typeToken == "sem" && (subtypeToken == "tipo" || subtypeToken == "texto"))
            {
                ChangeDir(index, state, typeToken, subtypeToken);
                return true;
            }

            var derived = NormalizeDerived(typeToken);
            if (!string.IsNullOrWhiteSpace(derived))
            {
                ChangeDir(index, state, typeToken, "");
                return true;
            }

            // If already inside a type, allow implicit subtype jump only when it exists.
            if (!string.IsNullOrWhiteSpace(state.Type) && string.IsNullOrWhiteSpace(state.Subtype))
            {
                var candidateSub = NormalizeTypeName(typeToken);
                if (index.ByType.TryGetValue(state.Type, out var subs) && subs.ContainsKey(candidateSub))
                {
                    ChangeDir(index, state, typeToken, "");
                    return true;
                }
            }

            // If at root, allow implicit type jump only when it exists.
            if (string.IsNullOrWhiteSpace(state.Type))
            {
                var candidateType = NormalizeTypeName(typeToken);
                if (index.ByType.ContainsKey(candidateType))
                {
                    ChangeDir(index, state, typeToken, "");
                    return true;
                }
            }

            return false;
        }

        private static bool TryNormalizeSemTipo(string typeToken, string subtypeToken, out string normType, out string normSubtype)
        {
            normType = "";
            normSubtype = "";
            var combined = (typeToken ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(subtypeToken))
                combined = $"{combined}/{subtypeToken.Trim()}";
            if (string.IsNullOrWhiteSpace(combined)) return false;
            if (combined.StartsWith("/", StringComparison.Ordinal))
                combined = combined.Substring(1);

            var parts = combined.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;

            if (!parts[0].Equals("sem", StringComparison.OrdinalIgnoreCase))
                return false;

            if (parts[1].Equals("tipo", StringComparison.OrdinalIgnoreCase))
            {
                normType = "sem tipo";
                normSubtype = "";
                return true;
            }
            if (parts[1].Equals("texto", StringComparison.OrdinalIgnoreCase))
            {
                normType = "sem tipo";
                normSubtype = "sem_texto";
                return true;
            }

            return false;
        }

        private static bool TryNormalizeView(string typeToken, string subtypeToken, out string view)
        {
            view = "";
            var token = (typeToken ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (!string.IsNullOrWhiteSpace(subtypeToken))
                token = $"{token}/{subtypeToken.Trim().ToLowerInvariant()}";

            return token switch
            {
                "text" => SetView("text", out view),
                "texto" => SetView("text", out view),
                "operators" => SetView("operators", out view),
                "ops" => SetView("operators", out view),
                "textoperators" => SetView("textoperators", out view),
                "textops" => SetView("textoperators", out view),
                "dump" => SetView("dump", out view),
                "raw" => SetView("dump", out view),
                "dumphex" => SetView("dumphex", out view),
                "hex" => SetView("dumphex", out view),
                "info" => SetView("info", out view),
                "keys" => SetView("info", out view),
                "dict" => SetView("info", out view),
                _ => false
            };
        }

        private static bool SetView(string value, out string view)
        {
            view = value;
            return true;
        }

        private static bool TryResolveLocalId(ShellState state, string? token, out int id)
        {
            id = 0;
            if (state.LastListIds == null || state.LastListIds.Count == 0) return false;
            if (string.IsNullOrWhiteSpace(token)) return false;
            var t = token.Trim();
            if (!t.StartsWith("d", StringComparison.OrdinalIgnoreCase)) return false;
            var rest = t.Substring(1);
            if (rest.StartsWith(":", StringComparison.Ordinal)) rest = rest.Substring(1);
            if (!int.TryParse(rest, out var pos)) return false;
            if (pos < 1 || pos > state.LastListIds.Count) return false;
            id = state.LastListIds[pos - 1];
            return true;
        }


        private static void ListCurrent(PdfDocument doc, ObjectIndex index, ShellState state)
        {
            if (state.ObjectId > 0)
            {
                if (!string.IsNullOrWhiteSpace(state.View))
                {
                    ShowObjectView(doc, index, state.ObjectId, state.View);
                    return;
                }
                ListObjectContents(doc, index, state.ObjectId);
                return;
            }

            if (string.IsNullOrWhiteSpace(state.Type))
            {
                state.LastListIds.Clear();
                foreach (var entry in index.ByType.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"{entry.Key}  ({entry.Value.Count})");
                return;
            }

            if (string.IsNullOrWhiteSpace(state.Subtype))
            {
                if (!index.ByType.TryGetValue(state.Type, out var map))
                {
                    Console.WriteLine("Tipo nao encontrado.");
                    return;
                }
                state.LastListIds.Clear();
                foreach (var entry in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"{entry.Key}  ({entry.Value.Count})");
                }
                return;
            }

            if (!index.ByType.TryGetValue(state.Type, out var subMap) ||
                !subMap.TryGetValue(state.Subtype, out var list))
            {
                Console.WriteLine("Subtipo nao encontrado.");
                return;
            }
            if (list.Count == 0)
            {
                Console.WriteLine("Sem objetos.");
                return;
            }

            var typeWidth = Math.Max(10, list.Max(r => r.Type.Length));
            var subtypeWidth = Math.Max(6, list.Max(r => r.Subtype.Length));
            var lenWidth = Math.Max(3, list.Max(r => r.Length.ToString(CultureInfo.InvariantCulture).Length));
            var idLabel = "[ID]";
            var idWidth = Math.Max(idLabel.Length - 2, list.Max(r => r.Id.ToString(CultureInfo.InvariantCulture).Length));
            var showName = list.Any(r => !string.IsNullOrWhiteSpace(r.Name));
            var nameWidth = showName ? Math.Max(4, list.Max(r => r.Name?.Length ?? 0)) : 0;
            state.LastListIds = list.OrderBy(r => r.Id).Select(r => r.Id).ToList();
            var posWidth = Math.Max(2, state.LastListIds.Count.ToString(CultureInfo.InvariantCulture).Length);

            if (showName)
                Console.WriteLine($"d{"".PadLeft(posWidth - 1)}  {idLabel.PadLeft(idWidth + 2)}  {"Type".PadRight(typeWidth)}  {"Subtype".PadRight(subtypeWidth)}  {"Len".PadLeft(lenWidth)}  {"Nome".PadRight(nameWidth)}  Detalhe");
            else
                Console.WriteLine($"d{"".PadLeft(posWidth - 1)}  {idLabel.PadLeft(idWidth + 2)}  {"Type".PadRight(typeWidth)}  {"Subtype".PadRight(subtypeWidth)}  {"Len".PadLeft(lenWidth)}  Detalhe");

            int pos = 1;
            foreach (var r in list.OrderBy(r => r.Id))
            {
                var name = r.Name ?? "";
                if (showName)
                {
                    Console.WriteLine($"d{pos.ToString(CultureInfo.InvariantCulture).PadLeft(posWidth)}  [{r.Id.ToString(CultureInfo.InvariantCulture).PadLeft(idWidth)}]  {r.Type.PadRight(typeWidth)}  {r.Subtype.PadRight(subtypeWidth)}  {r.Length.ToString(CultureInfo.InvariantCulture).PadLeft(lenWidth)}  {name.PadRight(nameWidth)}  {r.Detail}");
                }
                else
                {
                    Console.WriteLine($"d{pos.ToString(CultureInfo.InvariantCulture).PadLeft(posWidth)}  [{r.Id.ToString(CultureInfo.InvariantCulture).PadLeft(idWidth)}]  {r.Type.PadRight(typeWidth)}  {r.Subtype.PadRight(subtypeWidth)}  {r.Length.ToString(CultureInfo.InvariantCulture).PadLeft(lenWidth)}  {r.Detail}");
                }
                pos++;
            }
        }

        private static void EnterObject(PdfDocument doc, ObjectIndex index, ShellState state, int id)
        {
            if (!index.ById.TryGetValue(id, out _))
            {
                Console.WriteLine("Objeto nao encontrado.");
                return;
            }
            state.ObjectId = id;
            state.View = "";
            ListObjectContents(doc, index, id);
        }

        private static void ListObjectContents(PdfDocument doc, ObjectIndex index, int id)
        {
            ShowObjectInfo(doc, index, id);

            var obj = ResolveObject(doc, index, id);
            if (obj is PdfStream stream && HasTextOperators(stream))
            {
                var resources = PdfTextExtraction.TryFindResourcesForObjId(doc, id, out var found)
                    ? found
                    : new PdfResources(new PdfDictionary());
                var text = ExtractTextFromOperators(stream, resources);
                Console.WriteLine("Texto:");
                if (string.IsNullOrWhiteSpace(text))
                    Console.WriteLine("(vazio)");
                else
                    Console.WriteLine(text);
            }
        }

        private static void ShowObjectView(PdfDocument doc, ObjectIndex index, int id, string view)
        {
            var v = (view ?? "").Trim().ToLowerInvariant();
            switch (v)
            {
                case "info":
                case "keys":
                case "dict":
                    ShowObjectInfo(doc, index, id);
                    return;
                case "text":
                case "texto":
                    ShowObjectText(doc, index, id);
                    return;
                case "operators":
                case "ops":
                    ShowObjectOperators(doc, index, id, textOnly: false);
                    return;
                case "textoperators":
                case "textops":
                    ShowObjectOperators(doc, index, id, textOnly: true);
                    return;
                case "dump":
                case "raw":
                    ShowObjectDump(doc, index, id, asHex: false);
                    return;
                case "dumphex":
                case "hex":
                    ShowObjectDump(doc, index, id, asHex: true);
                    return;
                default:
                    ListObjectContents(doc, index, id);
                    return;
            }
        }

        private static void ShowObjectInfo(PdfDocument doc, ObjectIndex index, int id)
        {
            if (!index.ById.TryGetValue(id, out var info))
            {
                Console.WriteLine("Objeto nao encontrado.");
                return;
            }
            Console.WriteLine($"Objeto {info.Id}  Type={info.Type} Subtype={info.Subtype} Len={info.Length}");
            if (!string.IsNullOrWhiteSpace(info.Detail))
                Console.WriteLine($"Detalhe: {info.Detail}");
            if (!string.IsNullOrWhiteSpace(info.Name))
                Console.WriteLine($"Nome: {info.Name}");

            var obj = ResolveObject(doc, index, id);
            if (obj is PdfStream stream)
            {
                var filters = GetStreamFilters(stream);
                if (!string.IsNullOrWhiteSpace(filters))
                    Console.WriteLine($"Stream: filtros={filters}");
            }

            ShowObjectKeys(obj);
        }

        private static void ShowObjectText(PdfDocument doc, ObjectIndex index, int id)
        {
            var obj = ResolveObject(doc, index, id);
            if (obj is not PdfStream stream)
            {
                Console.WriteLine("Objeto nao e stream.");
                return;
            }
            var resources = PdfTextExtraction.TryFindResourcesForObjId(doc, id, out var found)
                ? found
                : new PdfResources(new PdfDictionary());
            var text = ExtractTextFromOperators(stream, resources);
            if (string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine("(sem texto)");
                return;
            }
            Console.WriteLine(text);
        }

        private static void ShowObjectOperators(PdfDocument doc, ObjectIndex index, int id, bool textOnly)
        {
            var obj = ResolveObject(doc, index, id);
            if (obj is not PdfStream stream)
            {
                Console.WriteLine("Objeto nao e stream.");
                return;
            }
            var ops = ExtractOperators(stream, textOnly);
            if (string.IsNullOrWhiteSpace(ops))
            {
                Console.WriteLine("(sem operadores)");
                return;
            }
            Console.WriteLine(ops);
        }

        private static void ShowObjectDump(PdfDocument doc, ObjectIndex index, int id, bool asHex)
        {
            var obj = ResolveObject(doc, index, id);
            if (obj is not PdfStream stream)
            {
                Console.WriteLine("Objeto nao e stream.");
                return;
            }
            if (asHex)
            {
                var bytes = ExtractStreamBytes(stream);
                var hex = BitConverter.ToString(bytes).Replace("-", " ");
                Console.WriteLine(hex);
                return;
            }
            var raw = ExtractStreamRaw(stream);
            Console.WriteLine(raw);
        }

        private static PdfObject? ResolveObject(PdfDocument doc, ObjectIndex index, int id)
        {
            if (index.IdToIndex.TryGetValue(id, out var idx))
                return doc.GetPdfObject(idx);
            return doc.GetPdfObject(id);
        }

        private static void ShowObjectKeys(PdfObject? obj)
        {
            if (obj is null)
            {
                Console.WriteLine("Sem dados.");
                return;
            }
            PdfDictionary? dict = obj as PdfDictionary;
            if (obj is PdfStream stream)
                dict = stream;
            if (dict == null)
            {
                Console.WriteLine("Sem dicionario.");
                return;
            }
            var keys = dict.KeySet().OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase).ToList();
            if (keys.Count == 0)
            {
                Console.WriteLine("Sem chaves.");
                return;
            }
            Console.WriteLine("Chaves:");
            foreach (var key in keys)
            {
                var value = dict.Get(key);
                Console.WriteLine($"  {key} -> {DescribeValue(value)}");
            }
        }

        private static ObjectIndex BuildIndex(PdfDocument doc)
        {
            var contentStreamResources = BuildContentStreamResources(doc);
            var index = new ObjectIndex();

            int max = doc.GetNumberOfPdfObjects();
            int nextSyntheticId = max + 1;
            for (int i = 0; i < max; i++)
            {
                var obj = doc.GetPdfObject(i);
                if (obj == null || obj.IsNull()) continue;

                var objNumber = obj.GetIndirectReference()?.GetObjNumber() ?? 0;
                if (objNumber <= 0)
                    objNumber = i;
                if (index.ById.ContainsKey(objNumber))
                {
                    objNumber = i;
                    if (index.ById.ContainsKey(objNumber))
                        objNumber = nextSyntheticId++;
                }

                string type = "";
                string subtype = "";
                if (obj is PdfDictionary dict)
                {
                    type = dict.GetAsName(PdfName.Type)?.ToString() ?? "";
                    subtype = dict.GetAsName(PdfName.Subtype)?.ToString() ?? "";
                }

                string derived = "";
                string name = "";
                if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(subtype))
                {
                    if (obj is PdfStream stream)
                    {
                        var id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                        if (id > 0 && contentStreamResources.TryGetValue(id, out var resources))
                        {
                            var hasText = HasTextOperators(stream);
                            derived = hasText ? "texto" : "sem_texto";
                            if (hasText)
                                name = ExtractFirstTextChunk(stream, resources);
                        }
                        else
                            derived = "stream";
                    }
                    else if (obj is PdfDictionary)
                    {
                        derived = "dict";
                    }
                }

                var groupType = string.IsNullOrWhiteSpace(type) ? "sem tipo" : type;
                var groupSubtype = string.IsNullOrWhiteSpace(subtype)
                    ? (string.IsNullOrWhiteSpace(derived) ? "-" : derived)
                    : subtype;

                var detail = DescribeDetail(doc, obj as PdfDictionary, type, subtype);
                long len = obj is PdfStream s ? s.GetLength() : 0;

                var info = new ObjectInfo
                {
                    Id = objNumber,
                    Type = groupType,
                    Subtype = groupSubtype,
                    Length = len,
                    Detail = detail,
                    Name = name
                };

                if (!index.ByType.TryGetValue(groupType, out var subMap))
                {
                    subMap = new Dictionary<string, List<ObjectInfo>>(StringComparer.OrdinalIgnoreCase);
                    index.ByType[groupType] = subMap;
                }
                if (!subMap.TryGetValue(groupSubtype, out var list))
                {
                    list = new List<ObjectInfo>();
                    subMap[groupSubtype] = list;
                }
                list.Add(info);
                index.ById[objNumber] = info;
                index.IdToIndex[objNumber] = i;
            }

            return index;
        }

        private static Dictionary<int, PdfResources> BuildContentStreamResources(PdfDocument doc)
        {
            var map = new Dictionary<int, PdfResources>();
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                foreach (var stream in EnumerateStreams(contents))
                {
                    var id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (id > 0 && !map.ContainsKey(id))
                        map[id] = resources;
                }
            }
            return map;
        }

        private static IEnumerable<PdfStream> EnumerateStreams(PdfObject? obj)
        {
            if (obj == null) yield break;
            if (obj is PdfStream s)
            {
                yield return s;
                yield break;
            }
            if (obj is PdfArray arr)
            {
                foreach (var item in arr)
                    if (item is PdfStream ss) yield return ss;
            }
        }

        private static bool HasTextOperators(PdfStream stream)
        {
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return false;
            var tokens = TokenizeContent(bytes);
            foreach (var tok in tokens)
                if (TextOperators.Contains(tok)) return true;
            return false;
        }

        private static byte[] ExtractStreamBytes(PdfStream stream)
        {
            try { return stream.GetBytes(); } catch { return Array.Empty<byte>(); }
        }

        private static string ExtractStreamRaw(PdfStream stream)
        {
            try
            {
                var bytes = ExtractStreamBytes(stream);
                return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
            }
            catch (Exception ex)
            {
                return $"[erro ao ler stream] {ex.Message}";
            }
        }

        private static string GetStreamFilters(PdfStream stream)
        {
            var filterObj = stream.Get(PdfName.Filter);
            if (filterObj == null) return "none";
            if (filterObj is PdfName name) return name.GetValue();
            if (filterObj is PdfArray arr)
            {
                var parts = new List<string>();
                foreach (var item in arr)
                {
                    if (item is PdfName n) parts.Add(n.GetValue());
                    else if (item != null) parts.Add(item.ToString() ?? "");
                }
                return string.Join(",", parts);
            }
            return filterObj.ToString() ?? "unknown";
        }

        private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
        {
            "q","Q","cm","w","J","j","M","d","ri","i","gs",
            "m","l","c","v","y","h","re","S","s","f","F","f*","B","B*","b","b*","n",
            "W","W*",
            "BT","ET","Tc","Tw","Tz","TL","Tf","Tr","Ts",
            "Td","TD","Tm","T*","Tj","TJ","'","\"",
            "Do","BI","ID","EI"
        };

        private static readonly HashSet<string> TextOperators = new(StringComparer.Ordinal)
        {
            "BT","ET","Tc","Tw","Tz","TL","Tf","Tr","Ts",
            "Td","TD","Tm","T*","Tj","TJ","'","\""
        };

        private static List<string> TokenizeContent(byte[] bytes)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c))
                {
                    i++;
                    continue;
                }
                if (c == '%')
                {
                    i = SkipToEol(bytes, i);
                    continue;
                }
                if (c == '(')
                {
                    tokens.Add(ReadLiteralString(bytes, ref i));
                    continue;
                }
                if (c == '<')
                {
                    if (i + 1 < bytes.Length && bytes[i + 1] == '<')
                    {
                        tokens.Add(ReadBalanced(bytes, ref i, "<<", ">>"));
                        continue;
                    }
                    tokens.Add(ReadHexString(bytes, ref i));
                    continue;
                }
                if (c == '[')
                {
                    tokens.Add(ReadArray(bytes, ref i));
                    continue;
                }
                if (c == '/')
                {
                    tokens.Add(ReadName(bytes, ref i));
                    continue;
                }
                tokens.Add(ReadToken(bytes, ref i));
            }
            return tokens;
        }

        private static string ExtractTextFromOperators(PdfStream stream, PdfResources resources)
        {
            var pieces = PdfTextExtraction.CollectTextOperatorTexts(stream, resources);
            if (pieces.Count > 0)
            {
                var joined = string.Concat(pieces);
                if (!string.IsNullOrWhiteSpace(joined))
                    return joined.Trim();
            }

            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return "";

            var tokens = TokenizeContent(bytes);
            var operands = new List<string>();
            var sb = new System.Text.StringBuilder();

            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                if (tok == "Tj" || tok == "'" || tok == "\"")
                {
                    if (operands.Count >= 1)
                    {
                        var text = ExtractTextOperand(operands[^1]);
                        if (!string.IsNullOrWhiteSpace(text))
                            sb.Append(text);
                    }
                }
                else if (tok == "TJ")
                {
                    if (operands.Count >= 1)
                    {
                        var text = ExtractTextFromArray(operands[^1]);
                        if (!string.IsNullOrWhiteSpace(text))
                            sb.Append(text);
                    }
                }

                operands.Clear();
            }

            return sb.ToString().Trim();
        }

        private static string ExtractFirstTextChunk(PdfStream stream, PdfResources resources)
        {
            var pieces = PdfTextExtraction.CollectTextOperatorTexts(stream, resources);
            if (pieces.Count > 0)
            {
                foreach (var piece in pieces)
                {
                    if (IsReadableName(piece))
                        return NormalizeName(piece);
                }
            }

            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return "";

            var tokens = TokenizeContent(bytes);
            var operands = new List<string>();

            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                if (tok == "Tj" || tok == "'" || tok == "\"")
                {
                    if (operands.Count >= 1)
                    {
                        var text = ExtractTextOperand(operands[^1]);
                        if (IsReadableName(text))
                            return NormalizeName(text);
                    }
                }
                else if (tok == "TJ")
                {
                    if (operands.Count >= 1)
                    {
                        var text = ExtractTextFromArray(operands[^1]);
                        if (IsReadableName(text))
                            return NormalizeName(text);
                    }
                }

                operands.Clear();
            }

            return "";
        }

        private static bool IsReadableName(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var cleaned = NormalizeName(text);
            if (string.IsNullOrWhiteSpace(cleaned)) return false;
            if (cleaned.Length < 2) return false;
            return cleaned.Any(char.IsLetter);
        }

        private static string NormalizeName(string text)
        {
            var cleaned = new string(text.Where(c => !char.IsControl(c)).ToArray());
            return cleaned.Trim();
        }

        private static string ExtractOperators(PdfStream stream, bool textOnly)
        {
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return "";

            var tokens = TokenizeContent(bytes);
            var sb = new System.Text.StringBuilder();
            var operands = new List<string>();

            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                if (!textOnly || IsTextOperator(tok))
                {
                    var line = operands.Count > 0 ? $"{string.Join(" ", operands)} {tok}" : tok;
                    line = PdfOperatorLegend.AppendDescription(line, tok);
                    sb.AppendLine(line);
                }

                operands.Clear();
            }

            return sb.ToString().TrimEnd();
        }

        private static bool IsOperatorToken(string token)
        {
            return Operators.Contains(token);
        }

        private static bool IsTextOperator(string token)
        {
            return TextOperators.Contains(token);
        }

        private static string ReadToken(byte[] bytes, ref int i)
        {
            int start = i;
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c) || IsDelimiter(c)) break;
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadName(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // skip '/'
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c) || IsDelimiter(c)) break;
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadLiteralString(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '('
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                byte b = bytes[i++];
                if (b == '\\')
                {
                    if (i < bytes.Length) i++;
                    continue;
                }
                if (b == '(') depth++;
                else if (b == ')') depth--;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadHexString(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '<'
            while (i < bytes.Length && bytes[i] != '>')
                i++;
            if (i < bytes.Length) i++;
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadBalanced(byte[] bytes, ref int i, string startToken, string endToken)
        {
            int start = i;
            i += startToken.Length;
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                if (Match(bytes, i, startToken))
                {
                    depth++;
                    i += startToken.Length;
                    continue;
                }
                if (Match(bytes, i, endToken))
                {
                    depth--;
                    i += endToken.Length;
                    continue;
                }
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadArray(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '['
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                byte b = bytes[i];
                if (b == '[') depth++;
                else if (b == ']') depth--;
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ExtractTextOperand(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "";
            if (token.StartsWith("(", StringComparison.Ordinal))
                return UnwrapLiteral(token);
            if (token.StartsWith("<", StringComparison.Ordinal) && token.EndsWith(">", StringComparison.Ordinal))
                return DecodeHexString(token);
            return "";
        }

        private static string ExtractTextFromArray(string? arrayToken)
        {
            if (string.IsNullOrWhiteSpace(arrayToken)) return "";
            var sb = new System.Text.StringBuilder();
            int i = 0;
            while (i < arrayToken.Length)
            {
                char c = arrayToken[i];
                if (c == '(')
                {
                    var lit = ReadLiteralString(arrayToken, ref i);
                    sb.Append(UnwrapLiteral(lit));
                    continue;
                }
                if (c == '<')
                {
                    var hex = ReadHexString(arrayToken, ref i);
                    sb.Append(DecodeHexString(hex));
                    continue;
                }
                i++;
            }
            return sb.ToString();
        }

        private static string UnwrapLiteral(string token)
        {
            if (token.Length >= 2 && token[0] == '(' && token[^1] == ')')
                return token.Substring(1, token.Length - 2);
            return token;
        }

        private static string DecodeHexString(string token)
        {
            if (token.Length < 2) return "";
            var hex = token.Trim('<', '>');
            if (hex.Length % 2 != 0) hex += "0";
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                    b = 0x20;
                bytes[i] = b;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
        }

        private static string ReadLiteralString(string text, ref int i)
        {
            int start = i;
            i++; // '('
            int depth = 1;
            while (i < text.Length && depth > 0)
            {
                char c = text[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == '(') depth++;
                if (c == ')') depth--;
                i++;
            }
            return text.Substring(start, i - start);
        }

        private static string ReadHexString(string text, ref int i)
        {
            int start = i;
            i++; // '<'
            while (i < text.Length && text[i] != '>') i++;
            if (i < text.Length) i++;
            return text.Substring(start, i - start);
        }

        private static bool Match(byte[] bytes, int idx, string token)
        {
            if (idx + token.Length > bytes.Length) return false;
            for (int j = 0; j < token.Length; j++)
                if (bytes[idx + j] != token[j]) return false;
            return true;
        }

        private static int SkipToEol(byte[] bytes, int i)
        {
            while (i < bytes.Length)
            {
                byte b = bytes[i++];
                if (b == '\n' || b == '\r') break;
            }
            return i;
        }

        private static bool IsWhite(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }

        private static bool IsDelimiter(char c)
        {
            return c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';
        }

        private static string NormalizeTypeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = value.Trim();
            if (!v.StartsWith("/") && v != "sem tipo") v = "/" + v;
            return v;
        }

        private static string NormalizeDerived(string value)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            return v switch
            {
                "texto" => "texto",
                "sem_texto" => "sem_texto",
                "stream" => "stream",
                "dict" => "dict",
                _ => ""
            };
        }

        private static string DescribeDetail(PdfDocument doc, PdfDictionary? dict, string type, string subtype)
        {
            if (doc == null || dict == null) return "";
            if (string.Equals(type, "/XObject", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(subtype, "/Image", StringComparison.OrdinalIgnoreCase))
            {
                var w = dict.GetAsNumber(PdfName.Width)?.DoubleValue() ?? 0;
                var h = dict.GetAsNumber(PdfName.Height)?.DoubleValue() ?? 0;
                var cs = dict.GetAsName(PdfName.ColorSpace)?.ToString() ?? "";
                var bpc = dict.GetAsNumber(PdfName.BitsPerComponent)?.IntValue() ?? 0;
                var filter = dict.Get(PdfName.Filter)?.ToString() ?? "";
                return $"tam={FormatNum(w)}x{FormatNum(h)} cs={cs} bpc={bpc} filter={filter}";
            }

            if (string.Equals(type, "/Annot", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(subtype, "/Link", StringComparison.OrdinalIgnoreCase))
            {
                var rect = dict.GetAsArray(PdfName.Rect);
                var rectStr = rect != null ? $"rect={FormatArray(rect)}" : "";
                var action = dict.GetAsDictionary(PdfName.A);
                var s = action?.GetAsName(PdfName.S)?.ToString() ?? "";
                var uri = action?.GetAsString(PdfName.URI)?.ToString() ?? "";
                var detail = $"{rectStr}";
                if (!string.IsNullOrWhiteSpace(s)) detail += $" action={s}";
                if (!string.IsNullOrWhiteSpace(uri)) detail += $" uri={uri}";
                return detail.Trim();
            }

            if (string.Equals(type, "/Action", StringComparison.OrdinalIgnoreCase))
            {
                var s = dict.GetAsName(PdfName.S)?.ToString() ?? "";
                var uri = dict.GetAsString(PdfName.URI)?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(uri)) return $"S={s} URI={uri}";
                return !string.IsNullOrWhiteSpace(s) ? $"S={s}" : "";
            }

            if (string.Equals(type, "/Font", StringComparison.OrdinalIgnoreCase))
            {
                var baseFont = dict.GetAsName(PdfName.BaseFont)?.ToString() ?? "";
                var enc = dict.GetAsName(PdfName.Encoding)?.ToString() ?? "";
                return $"base={baseFont} enc={enc}".Trim();
            }

            if (string.Equals(type, "/ExtGState", StringComparison.OrdinalIgnoreCase))
            {
                var ca = dict.GetAsNumber(PdfName.ca)?.DoubleValue() ?? 0;
                var CA = dict.GetAsNumber(PdfName.CA)?.DoubleValue() ?? 0;
                return $"CA={FormatNum(CA)} ca={FormatNum(ca)}";
            }

            if (string.Equals(type, "/Page", StringComparison.OrdinalIgnoreCase))
            {
                var media = dict.GetAsArray(PdfName.MediaBox);
                var rotate = dict.GetAsNumber(PdfName.Rotate)?.IntValue() ?? 0;
                return media != null ? $"media={FormatArray(media)} rot={rotate}" : $"rot={rotate}";
            }

            if (string.Equals(type, "/Pages", StringComparison.OrdinalIgnoreCase))
            {
                var count = dict.GetAsNumber(PdfName.Count)?.IntValue() ?? 0;
                return count > 0 ? $"count={count}" : "";
            }

            if (string.Equals(type, "/Catalog", StringComparison.OrdinalIgnoreCase))
            {
                var hasOutlines = dict.GetAsDictionary(PdfName.Outlines) != null;
                return hasOutlines ? "outlines=sim" : "outlines=nao";
            }

            return "";
        }

        private static string DescribeValue(PdfObject? value)
        {
            if (value == null) return "null";
            if (value is PdfName name) return name.ToString();
            if (value is PdfNumber num) return num.ToString();
            if (value is PdfString str) return str.ToString();
            if (value is PdfArray arr) return $"array[{arr.Size()}]";
            if (value is PdfStream stream)
            {
                var filters = GetStreamFilters(stream);
                return $"stream len={stream.GetLength()} filtros={filters}";
            }
            if (value is PdfDictionary) return "dict";
            if (value is PdfIndirectReference ir) return $"ref {ir.GetObjNumber()}";
            return value.ToString() ?? "";
        }

        private static string FormatArray(PdfArray arr)
        {
            var items = arr.Select(v => v?.ToString() ?? "").ToList();
            return $"[{string.Join(",", items)}]";
        }

        private static string FormatNum(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private sealed class ObjectIndex
        {
            public Dictionary<string, Dictionary<string, List<ObjectInfo>>> ByType { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<int, ObjectInfo> ById { get; } = new();
            public Dictionary<int, int> IdToIndex { get; } = new();
        }

        private sealed class ObjectInfo
        {
            public int Id { get; set; }
            public string Type { get; set; } = "";
            public string Subtype { get; set; } = "";
            public long Length { get; set; }
            public string Detail { get; set; } = "";
            public string Name { get; set; } = "";
        }

        private sealed class ShellState
        {
            public string Type { get; set; } = "";
            public string Subtype { get; set; } = "";
            public string Identifier { get; set; } = "";
            public string InputFile { get; set; } = "";
            public List<string> Files { get; set; } = new();
            public int CurrentFileIndex { get; set; } = -1;
            public string BaseDir { get; set; } = "";
            public int ObjectId { get; set; }
            public List<int> LastListIds { get; set; } = new();
            public string View { get; set; } = "";
        }
    }
}
